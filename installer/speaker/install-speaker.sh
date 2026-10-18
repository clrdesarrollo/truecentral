#!/usr/bin/env bash
# Instala TrueCentral Speaker en una Raspberry Pi (Raspberry Pi OS de 64 bits, Pi 3B+/4/5/Zero 2 W) como
# servicio de systemd. No necesita escritorio.
#
#   sudo ./install-speaker.sh [paquete.tar.gz]
#
# Sin argumento busca truecentral-speaker-<versión>-linux-arm64.tar.gz junto al script o en el directorio
# actual (usa el de versión más alta). El paquete se arma en el equipo de desarrollo con:
#
#   dotnet publish src/TrueCentralVms.SpeakerAgent -c Release -r linux-arm64 --self-contained -o out
#   tar -czf truecentral-speaker-<versión>-linux-arm64.tar.gz -C out .
#
# Al terminar el equipo queda corriendo y muestra su página en http://<ip>:8088. Para enrolarlo:
#   - desde esa página (servidor + código), o
#   - por consola: sudo truecentral-speaker enroll --server http://<servidor>:5090 --code ABCD-EFGH

set -euo pipefail

APP_DIR="/opt/truecentral-speaker"
DATA_DIR="/var/lib/truecentral-speaker"
SERVICE="truecentral-speaker.service"
SERVICE_USER="truecentral-speaker"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ $EUID -ne 0 ]]; then
    echo "Ejecute con sudo." >&2
    exit 1
fi

package="${1:-}"
if [[ -z "$package" ]]; then
    package="$(ls -1 "$SCRIPT_DIR"/truecentral-speaker-*-linux-arm64.tar.gz ./truecentral-speaker-*-linux-arm64.tar.gz 2>/dev/null | sort -V | tail -1 || true)"
fi
if [[ -z "$package" || ! -f "$package" ]]; then
    echo "No se encontró el paquete truecentral-speaker-<versión>-linux-arm64.tar.gz." >&2
    exit 1
fi
echo "Paquete: $package"

# ALSA (libasound2) viene en Raspberry Pi OS; alsa-utils trae aplay/alsamixer para diagnosticar.
apt-get install -y --no-install-recommends libasound2 alsa-utils >/dev/null || true

# Usuario de servicio sin login, en el grupo audio (y gpio para las etapas con entradas/salidas).
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi
usermod -a -G audio "$SERVICE_USER"
getent group gpio >/dev/null && usermod -a -G gpio "$SERVICE_USER" || true

systemctl stop "$SERVICE" 2>/dev/null || true
rm -rf "$APP_DIR.new"
mkdir -p "$APP_DIR.new"
tar -xzf "$package" -C "$APP_DIR.new"
chmod +x "$APP_DIR.new/truecentral-speaker"
rm -rf "$APP_DIR"
mv "$APP_DIR.new" "$APP_DIR"
ln -sf "$APP_DIR/truecentral-speaker" /usr/local/bin/truecentral-speaker

mkdir -p "$DATA_DIR"
chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
chmod 700 "$DATA_DIR"

install -m 644 "$SCRIPT_DIR/$SERVICE" "/etc/systemd/system/$SERVICE"
systemctl daemon-reload
systemctl enable --now "$SERVICE"

ip="$(hostname -I | awk '{print $1}')"
echo
echo "TrueCentral Speaker $("$APP_DIR/truecentral-speaker" version) instalado y corriendo."
echo "Página del equipo: http://$ip:8088"
echo "Enrolar por consola: sudo truecentral-speaker enroll --server http://<servidor>:5090 --code <código>"
echo "Registro: journalctl -u $SERVICE -f"
