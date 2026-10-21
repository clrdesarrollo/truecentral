#!/usr/bin/env bash
# Prepara una Raspberry Pi como pantalla de alarma de una caseta de TrueCentral.
# Raspberry Pi OS de 64 bits con escritorio, Pi 4 o Pi 5. La Pi 3 queda fuera por el render por
# software, los 100 Mbps compartidos con el USB y no poder arrancar de SSD.
#
#   ./install-screen.sh     (da igual desde qué directorio se llame)
#
# Busca el paquete truecentral-screen-<versión>-linux-arm64.tar.gz (lo genera
# installer\build-installers.ps1 en dist\) junto al script, en la raíz del repositorio o en el
# directorio actual; si hay varios, usa el de versión más alta. Sin paquete compila desde el
# código (si está el repositorio), instalando el SDK de .NET en ~/.dotnet si falta — avisa antes,
# porque lo preferible es generar el paquete en el equipo de desarrollo y no dejar un compilador
# en la caseta.
#
# Al terminar, la Pi arranca sola en la pantalla de alarma, busca el servidor de TrueCentral en
# la red y muestra su código de emparejamiento. No hay nada más que configurar en el equipo.

set -euo pipefail

APP_DIR="$HOME/truecentral-pantalla"
EXE="TrueCentralVms.AlarmScreen"
SERVICE="truecentral-pantalla.service"
AUTOSTART_DIR="$HOME/.config/autostart"
SERVICE_DIR="$HOME/.config/systemd/user"
UPDATER="$HOME/.local/bin/truecentral-screen-update.sh"
UPDATE_LOG="$HOME/.local/state/truecentral-pantalla/update.log"

# Todo se resuelve desde donde vive el script, no desde donde se lo llamó.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PACKAGE_GLOB="truecentral-screen-*-linux-arm64.tar.gz"
PROJECT="$REPO_ROOT/src/TrueCentralVms.AlarmScreen/TrueCentralVms.AlarmScreen.csproj"

DOTNET_CHANNEL="10.0"
DOTNET_ROOT="$HOME/.dotnet"

# ----------------------------------------------------------------------------------------
# SDK de .NET, solo para el caso de compilar en la Pi
# ----------------------------------------------------------------------------------------

find_dotnet() {
    command -v dotnet >/dev/null 2>&1 && return 0
    for candidate in "$DOTNET_ROOT" /usr/share/dotnet /usr/lib/dotnet; do
        if [[ -x "$candidate/dotnet" ]]; then
            export DOTNET_ROOT="$candidate"
            export PATH="$candidate:$PATH"
            return 0
        fi
    done
    return 1
}

# libicu es la única dependencia nativa que .NET no trae y que Debian no garantiza. El nombre
# lleva la versión pegada (libicu72, libicu76…): se busca la que ofrezca esta distribución.
install_icu() {
    local pkg
    pkg="$(apt-cache --names-only search '^libicu[0-9]+$' 2>/dev/null | awk '{print $1}' | sort -V | tail -1 || true)"
    [[ -z "$pkg" ]] && return 0
    sudo apt-get install -y --no-install-recommends "$pkg" >/dev/null
}

# Instala el SDK con el script oficial de Microsoft, en el home y sin tocar el sistema. El
# instalador se baja a un archivo y se ejecuta aparte (no curl | bash): si la descarga se corta,
# media línea de script no llega a ejecutarse.
install_dotnet_sdk() {
    echo "==> Instalando el SDK de .NET $DOTNET_CHANNEL en $DOTNET_ROOT (algunos minutos)"
    install_icu
    local installer
    installer="$(mktemp)"
    if ! curl -fsSL --proto '=https' --tlsv1.2 https://dot.net/v1/dotnet-install.sh -o "$installer"; then
        rm -f "$installer"
        echo "No se pudo descargar el instalador de .NET. ¿Tiene salida a internet esta Pi?" >&2
        return 1
    fi
    if ! bash "$installer" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_ROOT" --no-path; then
        rm -f "$installer"
        return 1
    fi
    rm -f "$installer"
    export DOTNET_ROOT
    export PATH="$DOTNET_ROOT:$PATH"
}

echo "==> Dependencias de audio y utilidades"
sudo apt-get update -qq
sudo apt-get install -y --no-install-recommends alsa-utils mpg123 unclutter curl

# Sin pipewire-alsa, aplay abre el primer HDMI en crudo y no el del monitor (comprobado en una Pi 4
# con trixie: "audio open error 524" y la alarma muda). Con él, el "default" de ALSA va a PipeWire,
# que elige la salida conectada. Solo en imágenes con PipeWire.
if apt-cache show pipewire-alsa >/dev/null 2>&1; then
    sudo apt-get install -y --no-install-recommends pipewire-alsa
fi

echo "==> Instalando la aplicación en $APP_DIR"

# Reinstalar sobre una pantalla que ya está andando: parar el servicio antes de tocar los
# archivos, o se reemplaza el binario por debajo de un proceso vivo.
systemctl --user stop "$SERVICE" 2>/dev/null || true

# Se vacía en vez de escribir encima: .NET cargaría igual los ensamblados que la versión nueva
# ya no trae. El emparejamiento y los sonidos no viven aquí sino en ~/.config/TrueCentralPantalla,
# así que reinstalar no obliga a volver a aprobar la pantalla en el panel.
if [[ -d "$APP_DIR" ]]; then
    echo "    limpiando la instalación anterior"
    rm -rf "${APP_DIR:?}"/*
fi
mkdir -p "$APP_DIR"

# El paquete se busca donde es razonable dejarlo; si hay varios, el de versión más alta.
PACKAGE="$(ls -1 "$SCRIPT_DIR"/$PACKAGE_GLOB "$REPO_ROOT"/$PACKAGE_GLOB "$REPO_ROOT"/dist/$PACKAGE_GLOB ./$PACKAGE_GLOB 2>/dev/null \
    | sort -V | tail -1 || true)"

if [[ -n "$PACKAGE" ]]; then
    echo "    desde el paquete $PACKAGE"
    tar -xzf "$PACKAGE" -C "$APP_DIR"
elif [[ -f "$PROJECT" ]]; then
    if ! find_dotnet; then
        cat <<MSG
    Está el código fuente pero no el SDK de .NET.

    Se va a instalar en $DOTNET_ROOT. Si prefieres no dejar el SDK en esta Pi, corta
    ahora con Ctrl+C, genera el paquete en el equipo de desarrollo con
    installer\\build-installers.ps1 (queda en dist\\) y cópialo junto a este script.

MSG
        sleep 5
        install_dotnet_sdk || { echo "No se pudo instalar el SDK de .NET." >&2; exit 1; }
    fi
    echo "    compilando desde el código fuente (tarda varios minutos en la Pi)"
    dotnet publish "$PROJECT" -c Release -r linux-arm64 --self-contained -o "$APP_DIR"
else
    echo "No encuentro el paquete $PACKAGE_GLOB ni el código fuente en $REPO_ROOT/src." >&2
    exit 1
fi

if [[ ! -f "$APP_DIR/$EXE" ]]; then
    echo "El paquete no trae el ejecutable $EXE." >&2
    exit 1
fi
chmod +x "$APP_DIR/$EXE"
echo "    versión $("$APP_DIR/$EXE" --version 2>/dev/null || echo '?')"

echo "==> Servicio de usuario (reinicia solo si se cae)"
mkdir -p "$SERVICE_DIR"
cat > "$SERVICE_DIR/$SERVICE" <<EOF
[Unit]
Description=TrueCentral — pantalla de alarma
After=graphical-session.target
PartOf=graphical-session.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/$EXE
Restart=always
RestartSec=5
Environment=DISPLAY=:0

[Install]
WantedBy=graphical-session.target
EOF

systemctl --user daemon-reload
systemctl --user enable "$SERVICE"

# que el servicio de usuario sobreviva sin sesión iniciada por teclado
sudo loginctl enable-linger "$USER"

# Disparo del servicio al entrar a la sesión gráfica. Raspberry Pi OS no activa nunca
# graphical-session.target (comprobado en trixie con labwc), así que además se arranca desde el
# autostart XDG, que es lo que esta imagen sí ejecuta. import-environment traslada al gestor de
# usuario las variables de la sesión: sin eso el servicio no tiene pantalla.
mkdir -p "$AUTOSTART_DIR"
cat > "$AUTOSTART_DIR/truecentral-pantalla.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=TrueCentral pantalla de alarma
Comment=Arranca la pantalla de alarma dentro de la sesión gráfica
Exec=/bin/bash -c "systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XDG_RUNTIME_DIR XDG_SESSION_TYPE; systemctl --user start $SERVICE"
X-GNOME-Autostart-enabled=true
NoDisplay=true
EOF

# Arrancarla ya al reinstalar sobre una pantalla que estaba funcionando: entre el fin del
# instalador y el reinicio, el guardia se quedaría sin pantalla.
if systemctl --user restart "$SERVICE" 2>/dev/null; then
    sleep 3
    if [[ "$(systemctl --user is-active "$SERVICE")" == "active" ]]; then
        echo "    pantalla en marcha"
    else
        echo "    AVISO: el servicio no quedó activo; míralo con systemctl --user status $SERVICE" >&2
    fi
fi

echo "==> Actualización remota (todos los días a las 04:17)"
mkdir -p "$(dirname "$UPDATER")" "$(dirname "$UPDATE_LOG")"
cp "$SCRIPT_DIR/truecentral-screen-update.sh" "$UPDATER"
chmod +x "$UPDATER"
( crontab -l 2>/dev/null | grep -v 'truecentral-screen-update.sh' || true
  echo "17 4 * * * $UPDATER >> $UPDATE_LOG 2>&1" ) | crontab -
echo "    instalada en $UPDATER (registro en $UPDATE_LOG)"

# Reinicio ordenado desde el panel: permiso solo para ese comando (no se confía en el NOPASSWD
# general del primer usuario, que en una instalación endurecida es lo primero que se quita).
echo "==> Permiso para reiniciar desde el panel"
printf '%s ALL=(root) NOPASSWD: /usr/bin/systemctl reboot, /sbin/reboot\n' "$USER" \
    | sudo tee /etc/sudoers.d/truecentral-pantalla-reboot >/dev/null
sudo chmod 440 /etc/sudoers.d/truecentral-pantalla-reboot
if sudo visudo -c -f /etc/sudoers.d/truecentral-pantalla-reboot >/dev/null 2>&1; then
    echo "    concedido"
else
    # una regla inválida en sudoers.d puede dejar sudo inutilizable: mejor retirarla
    sudo rm -f /etc/sudoers.d/truecentral-pantalla-reboot
    echo "    AVISO: la regla no validó y se retiró; el reinicio remoto no estará disponible" >&2
fi

echo "==> Kiosco"
if sudo raspi-config nonint do_blanking 1 2>/dev/null; then
    echo "    apagado de pantalla desactivado"
else
    echo "    AVISO: no se pudo desactivar el apagado de pantalla; revísalo con 'sudo raspi-config'" >&2
fi

# Se pregunta por la sesión del asiento gráfico y no por la actual: este instalador se corre a
# menudo por SSH, donde XDG_SESSION_TYPE vale "tty".
SEAT_SESSION="$(loginctl list-sessions --no-legend 2>/dev/null | awk '/seat0/{print $1; exit}')"
SESSION_TYPE=""
[[ -n "$SEAT_SESSION" ]] && SESSION_TYPE="$(loginctl show-session "$SEAT_SESSION" -p Type --value 2>/dev/null)"
[[ -z "$SESSION_TYPE" ]] && SESSION_TYPE="${XDG_SESSION_TYPE:-desconocida}"

if [[ "$SESSION_TYPE" == "wayland" ]]; then
    echo "    sesión Wayland: el cursor queda visible si se conecta un ratón (labwc no lo oculta solo)"
    rm -f "$AUTOSTART_DIR/truecentral-pantalla-kiosk.desktop"
else
    echo "    sesión X11: sin salvapantallas y sin cursor"
    cat > "$AUTOSTART_DIR/truecentral-pantalla-kiosk.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=TrueCentral pantalla kiosco
Exec=/bin/bash -c "xset s off; xset -dpms; xset s noblank; unclutter -idle 1 &"
X-GNOME-Autostart-enabled=true
EOF
fi

echo "==> Autologin en el escritorio"
sudo raspi-config nonint do_boot_behaviour B4 || \
    echo "  (no se pudo fijar el autologin automáticamente; hazlo con 'sudo raspi-config')"

echo
echo "======================================================================"
echo " Pantalla lista. Reinicia la Pi:  sudo reboot"
echo
echo " Al arrancar buscará el servidor de TrueCentral en la red y mostrará un"
echo " código de 6 dígitos. Apruébalo en el panel web, en Dispositivos →"
echo " Pantallas de alarma, y desde ahí arranca sola — también después de un"
echo " corte de luz."
echo
echo " Si el servidor está en otra subred el broadcast no llega: en la misma"
echo " pantalla se puede escribir su dirección (http://IP:5090)."
echo
echo " Recomendado para producción: arrancar desde SSD por USB, o dejar el"
echo " sistema de archivos en overlay. La SD se corrompe con los cortes de luz."
echo "======================================================================"
