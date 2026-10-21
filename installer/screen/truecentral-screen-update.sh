#!/usr/bin/env bash
# Actualización remota de una pantalla de alarma. El servidor de TrueCentral
# publica la versión vigente de la app (la que trae su instalador) y el paquete;
# esto lo descarga, verifica su FIRMA y reemplaza la aplicación.
#
# La firma es de CLRobotics (Ed25519, se hace al compilar, con una llave que el
# servidor no tiene): ni un servidor comprometido puede hacer que la pantalla
# instale algo que no salió de CLRobotics. Tampoco se acepta una versión
# anterior a la instalada (no se puede "volver" a una versión con fallas).
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

# Llave pública de firma de las actualizaciones (installer/new-update-signing-key.ps1).
UPDATE_PUBKEY='-----BEGIN PUBLIC KEY-----
MCowBQYDK2VwAyEAl4GE7kg370nxGnrJ05gOAnHJhoy817rU/Uq585IJUfo=
-----END PUBLIC KEY-----'

SERVER=$(grep -oP '"ServerUrl"\s*:\s*"\K[^"]+' "$CONFIG" 2>/dev/null || true)
TOKEN=$(grep -oP '"DeviceToken"\s*:\s*"\K[^"]+' "$CONFIG" 2>/dev/null || true)
# Llave del servidor fijada al emparejar: el paquete se ejecuta en la Pi, así que solo se baja del servidor de verdad.
PIN=$(grep -oP '"PinnedServerKeyBase64"\s*:\s*"\K[^"]+' "$CONFIG" 2>/dev/null || true)
CURL_TLS=(-k)
if [[ -n "$PIN" ]]; then
    CURL_TLS=(-k --pinnedpubkey "sha256//$PIN")
elif [[ "$SERVER" == https://* ]]; then
    log "La pantalla aún no fijó la llave del servidor (se fija al conectarse); se actualiza mañana."
    exit 0
fi

if [[ -z "$SERVER" || -z "$TOKEN" ]]; then
    log "La pantalla aún no está emparejada; nada que actualizar."
    exit 0
fi

INFO=$(curl -sf "${CURL_TLS[@]}" --max-time 20 -H "X-Screen-Token: $TOKEN" "$SERVER/api/screen/client/version") || {
    log "El servidor no respondió; se reintenta mañana."
    exit 0
}

LATEST=$(echo "$INFO" | grep -oP '"version"\s*:\s*"\K[^"]+' || true)
URL=$(echo "$INFO" | grep -oP '"downloadUrl"\s*:\s*"\K[^"]+' || true)
CURRENT=$("$APP_DIR/$EXE" --version 2>/dev/null || true)
CURRENT=${CURRENT:-0.0.0}   # una versión ilegible se trata como antigua: mejor actualizar de más

if [[ -z "$LATEST" || -z "$URL" ]]; then
    log "El servidor no publica ninguna versión de la app."
    exit 0
fi
# Solo hacia adelante: una versión igual o anterior no se instala.
if [[ "$(printf '%s\n%s\n' "$CURRENT" "$LATEST" | sort -V | tail -n1)" != "$LATEST" || "$LATEST" == "$CURRENT" ]]; then
    log "Ya está en la versión $CURRENT (el servidor ofrece $LATEST)."
    exit 0
fi

log "Actualizando de $CURRENT a $LATEST"
rm -rf "$STAGING" && mkdir -p "$STAGING"
VERIFY="$(mktemp -d)"
trap 'rm -rf "$VERIFY"' EXIT
fail() { log "$1 Se aborta y se deja la versión actual."; rm -rf "$STAGING"; exit 1; }

curl -sf "${CURL_TLS[@]}" --max-time 600 -H "X-Screen-Token: $TOKEN" -o "$STAGING/paquete.tar.gz" "$SERVER$URL"
curl -sf "${CURL_TLS[@]}" --max-time 20 -H "X-Screen-Token: $TOKEN" -o "$VERIFY/manifest" "$SERVER$URL.manifest" \
    || fail "El servidor no entrega el manifiesto firmado del paquete."
curl -sf "${CURL_TLS[@]}" --max-time 20 -H "X-Screen-Token: $TOKEN" -o "$VERIFY/manifest.sig.b64" "$SERVER$URL.manifest.sig" \
    || fail "El servidor no entrega la firma del paquete."

# 1) Firma del manifiesto con la llave pública de CLRobotics.
command -v openssl >/dev/null || fail "Falta openssl para verificar la firma."
printf '%s\n' "$UPDATE_PUBKEY" > "$VERIFY/pub.pem"
base64 -d "$VERIFY/manifest.sig.b64" > "$VERIFY/manifest.sig" 2>/dev/null || fail "La firma no es Base64 válido."
openssl pkeyutl -verify -pubin -inkey "$VERIFY/pub.pem" -rawin -in "$VERIFY/manifest" -sigfile "$VERIFY/manifest.sig" >/dev/null 2>&1 \
    || fail "La FIRMA del paquete no es válida (no salió de CLRobotics o fue alterado)."

# 2) El manifiesto firmado describe exactamente este paquete y esta versión.
field() { grep -m1 "^$1=" "$VERIFY/manifest" | cut -d= -f2-; }
[[ "$(field producto)" == "truecentral-screen" ]] || fail "El manifiesto es de otro producto."
[[ "$(field version)" == "$LATEST" ]] || fail "El manifiesto es de otra versión ($(field version))."
[[ "$(field archivo)" == "$(basename "$URL")" ]] || fail "El manifiesto es de otro archivo."
echo "$(field sha256)  $STAGING/paquete.tar.gz" | sha256sum -c - >/dev/null 2>&1 || fail "El paquete no coincide con su manifiesto firmado."
log "Firma verificada."

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
