"""Cámaras simuladas del entorno de demostración del manual.

Levanta un MediaMTX propio con un flujo de video por cámara, generado con
ffmpeg (degradado con ruido más la fecha, la hora y el nombre sobreimpresos,
como el OSD de una cámara real), y un servicio ONVIF mínimo por cámara para
que el VMS las agregue por el flujo normal de Fuentes de video: información
del equipo, perfiles, URL RTSP, foto, PTZ y grabaciones (Perfil G).

Uso: python camaras.py <demo.json> <carpeta de datos> <ffmpeg.exe> <mediamtx.exe>
Lo lanza y lo detiene demo.ps1; al detenerlo se cierra todo el árbol de procesos.
"""

import base64
import datetime as dt
import hashlib
import json
import os
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

FUENTES = r"C:\Windows\Fonts"  # ffmpeg corre aquí para nombrar las fuentes sin la unidad


def log(texto):
    print(f"{dt.datetime.now():%H:%M:%S} {texto}", flush=True)


# ---------------------------------------------------------------------------
# Video: MediaMTX propio + un ffmpeg por cámara
# ---------------------------------------------------------------------------

def escribir_mediamtx_yml(ruta, puerto_rtsp):
    # Solo RTSP por TCP en loopback. Las cámaras publican sin clave y el VMS
    # lee con las credenciales que le da el servicio ONVIF: cualquier usuario
    # desde la propia máquina, como un equipo de laboratorio.
    with open(ruta, "w", encoding="utf-8") as f:
        f.write(f"""logLevel: warn
rtspAddress: 127.0.0.1:{puerto_rtsp}
rtspTransports: [tcp]
rtmp: no
hls: no
webrtc: no
srt: no
moq: no
api: no
metrics: no
pprof: no
playback: no
authInternalUsers:
- user: any
  pass:
  ips: ['127.0.0.1', '::1']
  permissions:
  - action: publish
  - action: read
  - action: playback
paths:
  all_others:
""")


def filtro_video(camara):
    c0, c1 = camara["colores"]
    nombre = camara["nombre"]
    osd = "fontcolor=white:borderw=2:bordercolor=black@0.55"
    # Degradado lento + un "piso" más oscuro + viñeteado: se lee como una escena
    # sin pretender serlo, y comprime bien (el ruido por cuadro saturaba el flujo).
    return (
        f"gradients=s=1280x720:r=15:c0=0x{c0}:c1=0x{c1}:seed={camara['id']}:speed=0.003,"
        "drawbox=x=0:y=ih*0.64:w=iw:h=ih*0.36:color=black@0.28:t=fill,"
        "vignette=angle=PI/5,"
        f"drawtext=fontfile=segoeui.ttf:text='%{{localtime}}':x=28:y=24:fontsize=30:{osd},"
        f"drawtext=fontfile=segoeuib.ttf:text='{nombre}':x=28:y=h-th-30:fontsize=34:{osd}"
    )


PROCESOS = []  # ffmpeg en curso, para cerrarlos al salir (si no, quedan publicando huérfanos)


def publicar(ffmpeg, camara, puerto_rtsp, detener):
    """Mantiene vivo el flujo de una cámara (lo relanza si ffmpeg se cae)."""
    destino = f"rtsp://127.0.0.1:{puerto_rtsp}/cam{camara['id']}"
    comando = [
        ffmpeg, "-hide_banner", "-loglevel", "error", "-re",
        "-f", "lavfi", "-i", filtro_video(camara),
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-g", "30", "-pix_fmt", "yuv420p", "-b:v", "1500k", "-maxrate", "2000k", "-bufsize", "2000k",
        "-f", "rtsp", "-rtsp_transport", "tcp", destino,
    ]
    while not detener.is_set():
        proceso = subprocess.Popen(comando, cwd=FUENTES, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        PROCESOS.append(proceso)
        _, error = proceso.communicate()
        PROCESOS.remove(proceso)
        if detener.is_set():
            return
        log(f"cam{camara['id']}: ffmpeg terminó ({proceso.returncode}) {error.decode(errors='replace').strip()[:300]}")
        time.sleep(3)


# ---------------------------------------------------------------------------
# ONVIF
# ---------------------------------------------------------------------------

NS = {
    "s": "http://www.w3.org/2003/05/soap-envelope",
    "tds": "http://www.onvif.org/ver10/device/wsdl",
    "trt": "http://www.onvif.org/ver10/media/wsdl",
    "tptz": "http://www.onvif.org/ver20/ptz/wsdl",
    "trc": "http://www.onvif.org/ver10/recording/wsdl",
    "tse": "http://www.onvif.org/ver10/search/wsdl",
    "trp": "http://www.onvif.org/ver10/replay/wsdl",
    "tt": "http://www.onvif.org/ver10/schema",
}
XMLNS = " ".join(f'xmlns:{p}="{u}"' for p, u in NS.items())


def sobre(cuerpo):
    return f'<?xml version="1.0" encoding="UTF-8"?><s:Envelope {XMLNS}><s:Body>{cuerpo}</s:Body></s:Envelope>'


def falla(codigo, texto):
    return sobre(
        "<s:Fault><s:Code><s:Value>s:Sender</s:Value>"
        f"<s:Subcode><s:Value>{codigo}</s:Value></s:Subcode></s:Code>"
        f"<s:Reason><s:Text xml:lang=\"en\">{texto}</s:Text></s:Reason></s:Fault>")


def hora_onvif(etiqueta, momento):
    return (f"<tt:{etiqueta}><tt:Time><tt:Hour>{momento.hour}</tt:Hour><tt:Minute>{momento.minute}</tt:Minute>"
            f"<tt:Second>{momento.second}</tt:Second></tt:Time><tt:Date><tt:Year>{momento.year}</tt:Year>"
            f"<tt:Month>{momento.month}</tt:Month><tt:Day>{momento.day}</tt:Day></tt:Date></tt:{etiqueta}>")


def local(elemento, nombre):
    return next((e for e in elemento.iter() if e.tag.rsplit("}", 1)[-1] == nombre), None)


class Camara:
    def __init__(self, datos, credenciales, puerto_rtsp, ffmpeg):
        self.datos = datos
        self.usuario = credenciales["usuario"]
        self.clave = credenciales["clave"]
        self.puerto_rtsp = puerto_rtsp
        self.ffmpeg = ffmpeg
        self.base = f"http://127.0.0.1:{datos['puerto']}/onvif"
        self.flujo = f"rtsp://127.0.0.1:{puerto_rtsp}/cam{datos['id']}"
        self.presets = {}
        self.foto = (0.0, b"")
        self.candado = threading.Lock()

    # WS-UsernameToken con PasswordDigest = Base64(SHA1(nonce + created + clave)).
    def autorizado(self, raiz):
        token = local(raiz, "UsernameToken")
        if token is None:
            return False
        usuario = local(token, "Username")
        clave = local(token, "Password")
        nonce = local(token, "Nonce")
        creado = local(token, "Created")
        if None in (usuario, clave, nonce, creado) or (usuario.text or "") != self.usuario:
            return False
        esperado = base64.b64encode(hashlib.sha1(
            base64.b64decode(nonce.text or "") + (creado.text or "").encode() + self.clave.encode()).digest()).decode()
        return esperado == (clave.text or "").strip()

    def responder(self, operacion, raiz):
        d = self.datos
        if operacion == "GetSystemDateAndTime":
            ahora_utc = dt.datetime.now(dt.timezone.utc)
            return ("<tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime>"
                    "<tt:DateTimeType>NTP</tt:DateTimeType><tt:DaylightSavings>false</tt:DaylightSavings>"
                    + hora_onvif("UTCDateTime", ahora_utc) + hora_onvif("LocalDateTime", dt.datetime.now())
                    + "</tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>")
        if operacion == "GetDeviceInformation":
            modelo = "DEMO-PTZ-25X" if d["ptz"] else "DEMO-IPC-4MP"
            return ("<tds:GetDeviceInformationResponse><tds:Manufacturer>CLR Demo</tds:Manufacturer>"
                    f"<tds:Model>{modelo}</tds:Model><tds:FirmwareVersion>V1.0.0 build 261009</tds:FirmwareVersion>"
                    f"<tds:SerialNumber>DEMO{d['id']:04d}2026</tds:SerialNumber><tds:HardwareId>demo</tds:HardwareId>"
                    "</tds:GetDeviceInformationResponse>")
        if operacion == "GetCapabilities":
            ptz = f"<tt:PTZ><tt:XAddr>{self.base}/ptz_service</tt:XAddr></tt:PTZ>" if d["ptz"] else ""
            return ("<tds:GetCapabilitiesResponse><tds:Capabilities>"
                    f"<tt:Device><tt:XAddr>{self.base}/device_service</tt:XAddr></tt:Device>"
                    f"<tt:Media><tt:XAddr>{self.base}/media_service</tt:XAddr></tt:Media>{ptz}"
                    "</tds:Capabilities></tds:GetCapabilitiesResponse>")
        if operacion == "GetServices":
            servicios = [("tds", "device_service"), ("trt", "media_service"), ("trc", "recording_service"),
                         ("tse", "search_service"), ("trp", "replay_service")]
            if d["ptz"]:
                servicios.append(("tptz", "ptz_service"))
            return ("<tds:GetServicesResponse>" + "".join(
                f"<tds:Service><tds:Namespace>{NS[p]}</tds:Namespace><tds:XAddr>{self.base}/{s}</tds:XAddr>"
                "<tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version></tds:Service>"
                for p, s in servicios) + "</tds:GetServicesResponse>")
        if operacion == "GetProfiles":
            perfiles = ""
            for token, nombre in (("main", "Principal"), ("sub", "Secundario")):
                ptz = ("<tt:PTZConfiguration token=\"ptz0\"><tt:Name>PTZ</tt:Name><tt:UseCount>1</tt:UseCount>"
                       "<tt:NodeToken>ptznode</tt:NodeToken></tt:PTZConfiguration>") if d["ptz"] and token == "main" else ""
                perfiles += (f"<trt:Profiles token=\"{token}\" fixed=\"true\"><tt:Name>{nombre}</tt:Name>"
                             "<tt:VideoEncoderConfiguration token=\"enc\"><tt:Name>H264</tt:Name><tt:UseCount>1</tt:UseCount>"
                             "<tt:Encoding>H264</tt:Encoding><tt:Resolution><tt:Width>1280</tt:Width><tt:Height>720</tt:Height>"
                             f"</tt:Resolution></tt:VideoEncoderConfiguration>{ptz}</trt:Profiles>")
            return f"<trt:GetProfilesResponse>{perfiles}</trt:GetProfilesResponse>"
        if operacion == "GetStreamUri":
            # Principal y secundario comparten el mismo flujo (alcanza para las capturas).
            return (f"<trt:GetStreamUriResponse><trt:MediaUri><tt:Uri>{self.flujo}</tt:Uri>"
                    "<tt:InvalidAfterConnect>false</tt:InvalidAfterConnect><tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>"
                    "<tt:Timeout>PT0S</tt:Timeout></trt:MediaUri></trt:GetStreamUriResponse>")
        if operacion == "GetSnapshotUri":
            return (f"<trt:GetSnapshotUriResponse><trt:MediaUri><tt:Uri>{self.base}/snapshot.jpg</tt:Uri>"
                    "<tt:InvalidAfterConnect>false</tt:InvalidAfterConnect><tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>"
                    "<tt:Timeout>PT0S</tt:Timeout></trt:MediaUri></trt:GetSnapshotUriResponse>")
        if operacion in ("ContinuousMove", "Stop", "GotoPreset", "RemovePreset"):
            if operacion == "RemovePreset":
                token = local(raiz, "PresetToken")
                self.presets.pop((token.text or "") if token is not None else "", None)
            return f"<tptz:{operacion}Response/>"
        if operacion == "SetPreset":
            token = local(raiz, "PresetToken")
            valor = (token.text or "1") if token is not None else "1"
            self.presets[valor] = True
            return f"<tptz:SetPresetResponse><tptz:PresetToken>{valor}</tptz:PresetToken></tptz:SetPresetResponse>"
        if operacion == "GetRecordings":
            return (f"<trc:GetRecordingsResponse><trc:RecordingItem><tt:RecordingToken>rec-cam{d['id']}</tt:RecordingToken>"
                    "</trc:RecordingItem></trc:GetRecordingsResponse>")
        if operacion == "GetRecordingInformation":
            ahora = dt.datetime.now(dt.timezone.utc)
            inicio = (dt.datetime.now() - dt.timedelta(days=7)).replace(hour=0, minute=0, second=0, microsecond=0)
            inicio_utc = inicio.astimezone(dt.timezone.utc)
            return ("<tse:GetRecordingInformationResponse><tse:RecordingInformation>"
                    f"<tt:RecordingToken>rec-cam{d['id']}</tt:RecordingToken>"
                    f"<tt:EarliestRecording>{inicio_utc:%Y-%m-%dT%H:%M:%SZ}</tt:EarliestRecording>"
                    f"<tt:LatestRecording>{ahora:%Y-%m-%dT%H:%M:%SZ}</tt:LatestRecording>"
                    "<tt:RecordingStatus>Recording</tt:RecordingStatus>"
                    "</tse:RecordingInformation></tse:GetRecordingInformationResponse>")
        if operacion == "GetReplayUri":
            # Sin grabación real: la "grabación" es el mismo flujo en vivo.
            return f"<trp:GetReplayUriResponse><trp:Uri>{self.flujo}</trp:Uri></trp:GetReplayUriResponse>"
        return None

    def foto_jpeg(self):
        with self.candado:
            instante, datos = self.foto
            if time.monotonic() - instante < 5 and datos:
                return datos
            resultado = subprocess.run(
                [self.ffmpeg, "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp",
                 "-i", self.flujo, "-frames:v", "1", "-f", "image2", "-c:v", "mjpeg", "-q:v", "4", "pipe:1"],
                capture_output=True, timeout=15, stdin=subprocess.DEVNULL)
            if resultado.returncode == 0 and resultado.stdout:
                self.foto = (time.monotonic(), resultado.stdout)
            return self.foto[1]


def manejador_para(camara):
    class Manejador(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def enviar(self, estado, cuerpo, tipo="application/soap+xml; charset=utf-8"):
            datos = cuerpo.encode("utf-8") if isinstance(cuerpo, str) else cuerpo
            self.send_response(estado)
            self.send_header("Content-Type", tipo)
            self.send_header("Content-Length", str(len(datos)))
            self.end_headers()
            self.wfile.write(datos)

        def do_GET(self):
            if self.path.endswith("/snapshot.jpg"):
                try:
                    foto = camara.foto_jpeg()
                except Exception as error:  # noqa: BLE001 - la foto es opcional
                    log(f"cam{camara.datos['id']}: foto fallida: {error}")
                    foto = b""
                if foto:
                    self.enviar(200, foto, "image/jpeg")
                    return
            self.enviar(404, "", "text/plain")

        def do_POST(self):
            largo = int(self.headers.get("Content-Length") or 0)
            texto = self.rfile.read(largo).decode("utf-8", "replace")
            try:
                raiz = ET.fromstring(texto)
            except ET.ParseError:
                self.enviar(400, falla("ter:InvalidArgVal", "XML mal formado"))
                return
            cuerpo = local(raiz, "Body")
            pedido = next(iter(cuerpo), None) if cuerpo is not None else None
            operacion = pedido.tag.rsplit("}", 1)[-1] if pedido is not None else ""
            if operacion != "GetSystemDateAndTime" and not camara.autorizado(raiz):
                self.enviar(400, falla("ter:NotAuthorized", "Sender not Authorized"))
                return
            respuesta = camara.responder(operacion, raiz)
            if respuesta is None:
                self.enviar(400, falla("ter:ActionNotSupported", f"Optional Action Not Implemented: {operacion}"))
            else:
                self.enviar(200, sobre(respuesta))

    return Manejador


# ---------------------------------------------------------------------------

def main():
    if len(sys.argv) != 5:
        print(__doc__)
        sys.exit(2)
    config_ruta, datos_ruta, ffmpeg, mediamtx = sys.argv[1:]
    with open(config_ruta, encoding="utf-8") as f:
        config = json.load(f)
    puerto_rtsp = config["puertos"]["camarasRtsp"]

    carpeta = os.path.join(datos_ruta, "camaras")
    os.makedirs(carpeta, exist_ok=True)
    yml = os.path.join(carpeta, "mediamtx.yml")
    escribir_mediamtx_yml(yml, puerto_rtsp)
    servidor_video = subprocess.Popen([mediamtx, yml], cwd=carpeta, stdin=subprocess.DEVNULL)
    log(f"MediaMTX de las cámaras en 127.0.0.1:{puerto_rtsp} (PID {servidor_video.pid})")
    time.sleep(1.5)

    detener = threading.Event()
    for datos in config["camaras"]:
        threading.Thread(target=publicar, args=(ffmpeg, datos, puerto_rtsp, detener), daemon=True).start()
        camara = Camara(datos, config["credencialesCamaras"], puerto_rtsp, ffmpeg)
        http = ThreadingHTTPServer(("127.0.0.1", datos["puerto"]), manejador_para(camara))
        threading.Thread(target=http.serve_forever, daemon=True).start()
        log(f"cam{datos['id']} «{datos['nombre']}»: ONVIF en 127.0.0.1:{datos['puerto']}")

    try:
        while servidor_video.poll() is None:
            time.sleep(2)
        log(f"MediaMTX terminó ({servidor_video.returncode}): cierro las cámaras")
    except KeyboardInterrupt:
        pass
    finally:
        detener.set()
        for proceso in list(PROCESOS):
            proceso.terminate()
        if servidor_video.poll() is None:
            servidor_video.terminate()


if __name__ == "__main__":
    main()
