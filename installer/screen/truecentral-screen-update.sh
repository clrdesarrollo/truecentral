#!/usr/bin/env bash
# Actualización remota de una pantalla de alarma. El servidor de TrueCentral
# publica la versión vigente de la app (la que trae su instalador) y el paquete;
# esto lo descarga, verifica el hash y reemplaza la aplicación.
#
# install-screen.sh lo deja como tarea diaria (crontab del usuario, 04:17).
#
# El intercambio de archivos vive aquí, fuera de la aplicación, a propósito: una
# actualización a medias deja una caseta sin pantalla, y así el instalador puede
# revisar, ajustar o desactivar el mecanismo sin tocar el producto.

set -euo pipefail

APP_DIR="$HOME/truecentral-pantalla"
EXE="TrueCentralVms.AlarmScreen"
SERVICE="truecentral-pantalla.service"
CONFIG="$HOME/.config/TrueCentralPantalla/config.json"
STAGING="$APP_DIR.staging"

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"; }

SERVER=$(grep -oP '"ServerUrl"\s*:\s*"\K[^"]+' "$CONFIG" 2>/dev/null || true)
TOKEN=$(grep -oP '"DeviceToken"\s*:\s*"\K[^"]+' "$CONFIG" 2>/dev/null || true)

if [[ -z "$SERVER" || -z "$TOKEN" ]]; then
    log "La pantalla aún no está emparejada; nada que actualizar."
    exit 0
fi

INFO=$(curl -skf --max-time 20 -H "X-Screen-Token: $TOKEN" "$SERVER/api/screen/client/version") || {
    log "El servidor no respondió; se reintenta mañana."
    exit 0
}

LATEST=$(echo "$INFO" | grep -oP '"version"\s*:\s*"\K[^"]+' || true)
URL=$(echo "$INFO" | grep -oP '"downloadUrl"\s*:\s*"\K[^"]+' || true)
SHA=$(echo "$INFO"  | grep -oP '"sha256"\s*:\s*"\K[^"]+' || true)
CURRENT=$("$APP_DIR/$EXE" --version 2>/dev/null || true)
CURRENT=${CURRENT:-0.0.0}   # una versión ilegible se trata como antigua: mejor actualizar de más

if [[ -z "$LATEST" || -z "$URL" ]]; then
    log "El servidor no publica ninguna versión de la app."
    exit 0
fi
if [[ "$LATEST" == "$CURRENT" ]]; then
    log "Ya está en la versión $CURRENT."
    exit 0
fi

log "Actualizando de $CURRENT a $LATEST"
rm -rf "$STAGING" && mkdir -p "$STAGING"
curl -skf --max-time 600 -H "X-Screen-Token: $TOKEN" -o "$STAGING/paquete.tar.gz" "$SERVER$URL"

if [[ -n "$SHA" ]]; then
    echo "$SHA  $STAGING/paquete.tar.gz" | sha256sum -c - >/dev/null || {
        log "El hash no coincide: se aborta y se deja la versión actual."
        rm -rf "$STAGING"
        exit 1
    }
fi

tar -xzf "$STAGING/paquete.tar.gz" -C "$STAGING"
rm -f "$STAGING/paquete.tar.gz"
if [[ ! -f "$STAGING/$EXE" ]]; then
    log "El paquete no trae $EXE: se aborta y se deja la versión actual."
    rm -rf "$STAGING"
    exit 1
fi

# Respaldo de la versión anterior: si la nueva no levanta, se vuelve a ella sola.
# Se reemplaza la carpeta entera (no se copia encima): .NET cargaría igual los
# ensamblados que la versión nueva ya no trae.
systemctl --user stop "$SERVICE" || true
rm -rf "$APP_DIR.anterior"
mv "$APP_DIR" "$APP_DIR.anterior"
mv "$STAGING" "$APP_DIR"
chmod +x "$APP_DIR/$EXE"
systemctl --user start "$SERVICE"

sleep 10
if systemctl --user is-active --quiet "$SERVICE"; then
    log "Actualizada a $LATEST."
else
    log "La versión nueva no levantó: se restaura la anterior."
    rm -rf "$APP_DIR" && mv "$APP_DIR.anterior" "$APP_DIR"
    systemctl --user start "$SERVICE"
fi
