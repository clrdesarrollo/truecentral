"""Carga los datos de demostración en el servidor del entorno de demo.

Usa solo la API REST, como el panel web, así que los datos quedan igual que si
los hubiera cargado una persona. Se puede volver a correr: lo que ya existe
(por nombre) no se duplica.

Uso: python sembrar.py <demo.json> <carpeta de datos>
"""

import base64
import datetime as dt
import hashlib
import json
import os
import subprocess
import sys
import time
import uuid
import urllib.error
import urllib.request

UBICACIONES = [
    ("Casa matriz", "Site", None, "Av. Demostración 1234, Santiago"),
    ("Recepción", "Sector", "Casa matriz", None),
    ("Estacionamiento", "Sector", "Casa matriz", None),
    ("Oficinas", "Floor", "Casa matriz", None),
    ("Centro de distribución", "Site", None, "Camino de Prueba 500, Quilicura"),
    ("Andenes", "Sector", "Centro de distribución", None),
    ("Bodega", "Building", "Centro de distribución", None),
    ("Perímetro", "Sector", "Centro de distribución", None),
    ("Patio", "Sector", "Centro de distribución", None),
]

CONSIGNAS = {
    "Acceso principal": ("Portón de entrada de vehículos y peatones.",
                         "Verifique la identidad de toda visita antes de abrir. Fuera de horario, "
                         "avise al supervisor de turno al 600 000 0000."),
    "Andén de carga": ("Andenes 1 a 4 del centro de distribución.",
                       "Entre las 22:00 y las 06:00 no debe haber movimiento: ante cualquier persona, "
                       "llame al guardia de ronda y registre el evento."),
    "Domo patio": ("Domo PTZ sobre el patio de maniobras.",
                   "Use el preset 1 para la vista general y el 2 para el portón de camiones."),
}

ROLES = {
    "Supervisor de turno": dict(
        description="Opera todo el sistema durante su turno.",
        permissions=["live.view", "live.ptz", "live.views", "playback.view", "playback.export", "events.attend",
                     "locations.command", "alarms.monitor", "alarms.operate", "cerco.monitor", "cerco.operate",
                     "access.monitor", "access.doors", "anpr.view", "speakers.play", "intercom.answer"],
        alcance=None),
    "Guardia centro de distribución": dict(
        description="Vigila el centro de distribución; ve el resto sin operarlo.",
        permissions=["live.view", "live.ptz", "playback.view", "events.attend", "cerco.monitor"],
        alcance=dict(ubicaciones=["Centro de distribución"], veResto=True)),
    "Auditor": dict(
        description="Revisa registros y grabaciones, sin operar.",
        permissions=["audit.view", "playback.view", "access.monitor", "anpr.view", "workflows.view"],
        alcance=None),
    "Técnico": dict(
        description="Mantiene equipos y servicios.",
        permissions=["devices.manage", "maintenance.view", "maintenance.manage", "sessions.manage",
                     "system.services"],
        alcance=None),
}

PERSONAS = [  # nombre, apellido, departamento, tarjeta, cargo
    ("Ana", "Pérez", "Administración", "10203040", "Jefa de administración"),
    ("Carlos", "Muñoz", "Operaciones", "10203041", "Supervisor de turno"),
    ("Valentina", "Soto", "Bodega", "10203042", "Encargada de bodega"),
    ("Diego", "Fuentes", "Bodega", "10203043", "Operario de bodega"),
    ("Francisca", "Rojas", "Recursos humanos", "10203044", "Analista de personas"),
    ("Matías", "Contreras", "Transporte", "10203045", "Conductor"),
    ("Camila", "Díaz", "Operaciones", "10203046", "Guardia"),
    ("Benjamín", "Silva", "Mantención", "10203047", "Técnico eléctrico"),
]

# Para la ficha de credenciales: clave de teclado y dos huellas (una de calidad
# regular, para que se vean los dos estados). Las plantillas son bytes de
# relleno, no huellas de nadie: no hay terminales donde escribirlas.
CREDENCIALES_EXTRA = {
    ("Ana", "Pérez"): dict(clave="4821", huellas=[(2, 88), (7, 54)]),   # índice derecho e izquierdo
}
PERSONAS_INACTIVAS = {("Benjamín", "Silva")}
FUENTE_HUELLA = "Lector USB RECEPCION-01"

# Paneles de cerco sin enrolar (no hay equipo): sirven para la lista y para las
# credenciales que se muestran al crearlos, que se guardan en la carpeta de datos.
CERCOS = [("Cerco perímetro sur", "Perímetro")]
# La URL WebSocket que sugiere el servidor sale de la dirección con que se abrió
# el panel web; se pide con la de ejemplo del manual para no mostrar la IP real
# de este PC.
HOST_EJEMPLO = "192.168.1.10"


class Api:
    def __init__(self, base):
        self.base = base
        self.token = None

    def llamar(self, metodo, ruta, cuerpo=None, datos=None, tipo=None, host=None):
        cabeceras = {}
        if self.token:
            cabeceras["Authorization"] = f"Bearer {self.token}"
        if host:
            cabeceras["Host"] = host
        if cuerpo is not None:
            datos = json.dumps(cuerpo).encode("utf-8")
            tipo = "application/json"
        if tipo:
            cabeceras["Content-Type"] = tipo
        pedido = urllib.request.Request(self.base + ruta, data=datos, headers=cabeceras, method=metodo)
        try:
            with urllib.request.urlopen(pedido, timeout=60) as r:
                texto = r.read().decode("utf-8")
                return r.status, (json.loads(texto) if texto else None)
        except urllib.error.HTTPError as e:
            texto = e.read().decode("utf-8", "replace")
            try:
                return e.code, json.loads(texto) if texto else None
            except json.JSONDecodeError:
                return e.code, {"error": texto}

    def exigir(self, metodo, ruta, cuerpo=None, **kw):
        estado, respuesta = self.llamar(metodo, ruta, cuerpo, **kw)
        if estado >= 300:
            raise SystemExit(f"{metodo} {ruta} → {estado}: {respuesta}")
        return respuesta


def paso(texto):
    print(f"» {texto}", flush=True)


def sonido(ffmpeg, ruta, filtro):
    subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", filtro,
                    "-ac", "1", "-ar", "16000", ruta], check=True)


def subir_archivo(api, ruta_api, archivo):
    limite = uuid.uuid4().hex
    with open(archivo, "rb") as f:
        contenido = f.read()
    nombre = os.path.basename(archivo)
    cuerpo = (f"--{limite}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{nombre}\"\r\n"
              "Content-Type: audio/wav\r\n\r\n").encode("utf-8") + contenido + f"\r\n--{limite}--\r\n".encode()
    return api.exigir("POST", ruta_api, datos=cuerpo, tipo=f"multipart/form-data; boundary={limite}")


def plantilla_relleno(nombre, dedo):
    """512 bytes fijos por persona y dedo, en base64 (el servidor pide entre 64 y 4096)."""
    semilla = hashlib.sha256(f"demo:{nombre}:{dedo}".encode("utf-8")).digest()
    return base64.b64encode(semilla * 16).decode("ascii")


def nodo_accion(id_, tipo, config, x, y):
    return {"id": id_, "kind": "action", "x": x, "y": y, "label": None, "type": tipo, "config": config,
            "enabled": True, "delaySeconds": 0, "conditions": None, "secret": None, "actionId": 0}


def automatizacion(nombre, descripcion, activa, horas, acciones, enlaces):
    nodos = [{"id": "trigger", "kind": "trigger", "x": 300, "y": 30, "label": None, "type": None, "config": None,
              "enabled": True, "delaySeconds": 0, "conditions": None, "secret": None, "actionId": 0}] + acciones
    return {"name": nombre, "description": descripcion, "enabled": activa, "triggerType": "schedule",
            "cooldownSeconds": 0,
            "conditions": {"scheduleTimes": horas, "daysOfWeek": [], "fromTime": None, "toTime": None, "timeBands": []},
            "graph": {"nodes": nodos, "edges": enlaces}}


def main():
    config_ruta, datos_ruta = sys.argv[1:3]
    with open(config_ruta, encoding="utf-8") as f:
        config = json.load(f)
    api = Api(f"http://127.0.0.1:{config['puertos']['web']}")
    admin = config["administrador"]
    ffmpeg = os.path.join(datos_ruta, "tools", "ffmpeg", "bin", "ffmpeg.exe")

    # --- Sesión -------------------------------------------------------------
    estado = api.exigir("GET", "/api/setup/status")
    if estado.get("setupRequired"):
        paso("Creando el administrador")
        api.token = api.exigir("POST", "/api/setup/admin", {"username": admin["usuario"], "password": admin["clave"]})["token"]
    else:
        api.token = api.exigir("POST", "/api/auth/login", {"username": admin["usuario"], "password": admin["clave"]})["token"]
    licencia = api.exigir("GET", "/api/system/license")
    paso(f"Licencia: {licencia.get('mode')} ({licencia.get('daysRemaining')} días)")

    # --- Ubicaciones --------------------------------------------------------
    paso("Ubicaciones")
    existentes = api.exigir("GET", "/api/locations")
    ids_ubicacion = {}
    for nombre, tipo, padre, direccion in UBICACIONES:
        padre_id = ids_ubicacion.get(padre) if padre else None
        ya = next((u for u in existentes if u["name"] == nombre and u.get("parentId") == padre_id), None)
        if ya is None:
            ya = api.exigir("POST", "/api/locations", {"name": nombre, "kind": tipo, "parentId": padre_id,
                                                       "description": None, "address": direccion,
                                                       "latitude": None, "longitude": None})
        ids_ubicacion[nombre] = ya["id"]

    # --- Cámaras ------------------------------------------------------------
    paso("Cámaras (Fuentes de video)")
    cred = config["credencialesCamaras"]
    equipos = {d["sdkPort"]: d for d in api.exigir("GET", "/api/devices")}
    canales = {}
    for camara in config["camaras"]:
        ubicacion = ids_ubicacion[camara["ubicacion"][-1]]
        # El equipo lleva un nombre de inventario y su cámara el del lugar, como en una
        # instalación real (si no, la lista muestra "Recepción › Recepción").
        nombre_equipo = f"Domo PTZ {camara['id']:02d}" if camara["ptz"] else f"Cámara {camara['id']:02d}"
        cuerpo = {"name": nombre_equipo, "driverKey": "onvif", "host": "127.0.0.1", "sdkPort": camara["puerto"],
                  "rtspPort": config["puertos"]["camarasRtsp"], "username": cred["usuario"], "password": cred["clave"],
                  "locationId": ubicacion}
        equipo = equipos.get(camara["puerto"])
        if equipo is None:
            equipo = api.exigir("POST", "/api/devices", cuerpo)
        elif equipo["name"] != nombre_equipo:
            equipo = api.exigir("PUT", f"/api/devices/{equipo['id']}", dict(cuerpo, password=None))
        canal = api.exigir("GET", f"/api/devices/{equipo['id']}/channels")[0]
        if canal["name"] != camara["nombre"] or canal["supportsPtz"] != camara["ptz"]:
            canal = api.exigir("PUT", f"/api/devices/{equipo['id']}/channels/{canal['id']}", {
                "name": camara["nombre"], "enabled": True, "supportsPtz": camara["ptz"], "useFfmpegProxy": False})
        canales[camara["nombre"]] = canal["id"]
        print(f"   {camara['nombre']}: equipo {equipo['id']}, canal {canal['id']}")
    # MediaMTX aplica los cambios vigilando su archivo, y con varias altas en el
    # mismo segundo se pierde recargas: una revalidación al final lo deja al día.
    time.sleep(2)
    api.exigir("POST", f"/api/devices/{equipo['id']}/revalidate")

    paso("Consignas en las fichas")
    for nombre, (descripcion, consignas) in CONSIGNAS.items():
        ficha = api.exigir("GET", f"/api/resources/camera/{canales[nombre]}")
        api.exigir("PUT", f"/api/resources/camera/{canales[nombre]}", {
            "name": None, "locationId": ficha["resource"]["locationId"],
            "description": descripcion, "instructions": consignas})

    # --- Roles y usuarios ---------------------------------------------------
    paso("Roles y usuarios")
    roles = {r["name"]: r for r in api.exigir("GET", "/api/roles")}
    for nombre, rol in ROLES.items():
        if nombre in roles:
            continue
        alcance = rol["alcance"]
        roles[nombre] = api.exigir("POST", "/api/roles", {
            "name": nombre, "description": rol["description"], "permissions": rol["permissions"],
            "restrictScope": alcance is not None,
            "viewOutsideScope": bool(alcance and alcance["veResto"]),
            "locationIds": [ids_ubicacion[u] for u in alcance["ubicaciones"]] if alcance else [],
            "items": []})
    usuarios = {u["username"] for u in api.exigir("GET", "/api/users")}
    for usuario in config["usuarios"]:
        if usuario["usuario"] in usuarios:
            continue
        api.exigir("POST", "/api/users", {
            "username": usuario["usuario"], "password": usuario["clave"], "roleIds": [roles[usuario["rol"]]["id"]],
            "enabled": usuario["habilitado"], "restrictToLocations": False, "viewOutsideScope": False,
            "locationIds": []})

    # --- Biblioteca de sonidos ----------------------------------------------
    paso("Biblioteca de sonidos")
    carpeta_sonidos = os.path.join(datos_ruta, "sonidos")
    os.makedirs(carpeta_sonidos, exist_ok=True)
    cargados = {s["displayName"] for s in api.exigir("GET", "/api/workflows/audio")}
    for nombre, filtro in (
            ("Timbre", "sine=f=880:d=0.35,apad=pad_dur=0.15[a];sine=f=660:d=0.5[b];[a][b]concat=n=2:v=0:a=1"),
            ("Sirena", "aevalsrc=sin(2*PI*(600+300*sin(2*PI*0.5*t))*t):d=4"),
            ("Aviso de cierre", "sine=f=523:d=0.4,apad=pad_dur=0.1[a];sine=f=659:d=0.4,apad=pad_dur=0.1[b];"
                                "sine=f=784:d=0.6[c];[a][b][c]concat=n=3:v=0:a=1")):
        if nombre in cargados:
            continue
        archivo = os.path.join(carpeta_sonidos, f"{nombre}.wav")
        sonido(ffmpeg, archivo, filtro)
        subir_archivo(api, "/api/workflows/audio", archivo)

    # --- Automatizaciones ---------------------------------------------------
    paso("Automatizaciones")
    existentes = {w["name"]: w for w in api.exigir("GET", "/api/workflows")}
    aviso = {"severity": "Warning", "attachSnapshot": True, "requireAck": True, "sound": "Sirena", "soundRepeat": 1,
             "userIds": [], "channelIds": []}
    definiciones = [
        automatizacion(
            "Ronda nocturna del andén", "Foto del andén y aviso al guardia a las 22:00.", True, ["22:00"],
            [nodo_accion("a1", "snapshot", {"channelIds": [canales["Andén de carga"]], "count": 2, "intervalSeconds": 2}, 300, 180),
             nodo_accion("a2", "notify", dict(aviso, title="Ronda nocturna: andén de carga",
                                              message="Revise el andén y confirme la ronda · {fechahora}"), 300, 330)],
            [{"from": "trigger", "to": "a1", "port": "next"}, {"from": "a1", "to": "a2", "port": "next"},
             {"from": "a1", "to": "a2", "port": "error"}]),
        automatizacion(
            "Cierre de casa matriz", "Recuerda el cierre del edificio a las 20:00.", True, ["20:00"],
            [nodo_accion("a1", "notify", {"title": "Cierre de casa matriz", "message": "Verifique puertas y luces · {fechahora}",
                                          "severity": "Info", "attachSnapshot": False, "requireAck": False,
                                          "sound": "Aviso de cierre", "soundRepeat": 1, "userIds": [], "channelIds": []}, 300, 180)],
            [{"from": "trigger", "to": "a1", "port": "next"}]),
        automatizacion(
            "Revisión de perímetro (fin de semana)", "Pausada hasta la temporada alta.", False, ["03:00"],
            [nodo_accion("a1", "snapshot", {"channelIds": [canales["Perímetro sur"]], "count": 1, "intervalSeconds": 2}, 300, 180),
             nodo_accion("a2", "notify", dict(aviso, title="Revisión de perímetro", message="Perímetro sur · {fechahora}"), 300, 330)],
            [{"from": "trigger", "to": "a1", "port": "next"}, {"from": "a1", "to": "a2", "port": "next"}]),
    ]
    ids_auto = {}
    for definicion in definiciones:
        flujo = existentes.get(definicion["name"]) or api.exigir("POST", "/api/workflows", definicion)
        ids_auto[definicion["name"]] = flujo["id"]

    if not api.exigir("GET", "/api/workflows/runs?take=1")["items"]:
        paso("Ejecuciones de prueba y alertas")
        for _ in range(3):
            api.exigir("POST", f"/api/workflows/{ids_auto['Ronda nocturna del andén']}/test")
        api.exigir("POST", f"/api/workflows/{ids_auto['Cierre de casa matriz']}/test")
        alertas = api.exigir("GET", "/api/workflows/alerts?take=25&pending=true")["items"]
        # Deja una pendiente y confirma el resto, para mostrar ambos estados.
        for alerta in alertas[1:]:
            api.exigir("POST", f"/api/workflows/alerts/{alerta['id']}/ack")

    # --- Vistas guardadas ---------------------------------------------------
    paso("Vistas guardadas")
    vistas = {v["name"] for v in api.exigir("GET", "/api/live-views")}
    for nombre, camaras in (
            ("Casa matriz", ["Acceso principal", "Recepción", "Estacionamiento", "Pasillo oficinas"]),
            ("Centro de distribución", ["Andén de carga", "Bodega pasillo 1", "Perímetro sur", "Domo patio"])):
        if nombre not in vistas:
            api.exigir("POST", "/api/live-views", {
                "name": nombre, "layoutName": "4", "columns": 2, "rows": 2, "shared": True,
                "items": [{"cellIndex": i, "channelId": canales[c], "streamType": 1} for i, c in enumerate(camaras)]})

    # --- Control de acceso (sin equipos: horarios y personas) ---------------
    paso("Control de acceso: horarios y personas")
    horarios = {h["name"] for h in api.exigir("GET", "/api/access/schedules")}
    for nombre, tramos in (
            ("Oficina lunes a viernes", [(d, 480, 1080) for d in range(1, 6)]),
            ("Turno noche", [(d, 1320, 1440) for d in range(0, 7)] + [(d, 0, 360) for d in range(0, 7)])):
        if nombre not in horarios:
            api.exigir("POST", "/api/access/schedules", {
                "name": nombre, "description": None,
                "segments": [{"day": d, "startMinutes": a, "endMinutes": b} for d, a, b in tramos]})
    personas = api.exigir("GET", "/api/access/persons?pageSize=200")
    if isinstance(personas, dict):
        personas = personas.get("items", [])
    por_nombre = {(p.get("firstName"), p.get("lastName")): p for p in personas}
    hoy = dt.datetime.now(dt.timezone.utc).replace(hour=0, minute=0, second=0, microsecond=0)
    for nombre, apellido, area, tarjeta, cargo in PERSONAS:
        activa = (nombre, apellido) not in PERSONAS_INACTIVAS
        extra = CREDENCIALES_EXTRA.get((nombre, apellido), {})
        huellas = [{"number": dedo, "template": plantilla_relleno(f"{nombre} {apellido}", dedo), "quality": calidad,
                    "source": FUENTE_HUELLA} for dedo, calidad in extra.get("huellas", [])]
        ya = por_nombre.get((nombre, apellido))
        if ya is None:
            api.exigir("POST", "/api/access/persons", {
                "firstName": nombre, "lastName": apellido, "employeeNo": None, "department": area, "position": cargo,
                "email": None, "phone": None, "notes": None,
                "validFrom": hoy.strftime("%Y-%m-%dT%H:%M:%S.000Z"),
                "validTo": hoy.replace(year=hoy.year + 5).strftime("%Y-%m-%dT%H:%M:%S.000Z"),
                "enabled": activa, "pinCode": extra.get("clave"), "clearPin": False, "cards": [tarjeta],
                "fingerprints": huellas, "levelIds": [], "face": None, "clearFace": False})
            continue
        # Ya estaba (sembrada por una versión anterior de este script): se le
        # completa lo que falte, sin tocar lo demás.
        faltan_huellas = bool(huellas) and not ya["fingerprints"]
        falta_clave = bool(extra.get("clave")) and not ya["hasPin"]
        if ya.get("position") == cargo and ya["enabled"] == activa and not faltan_huellas and not falta_clave:
            continue
        api.exigir("PUT", f"/api/access/persons/{ya['id']}", {
            "firstName": ya["firstName"], "lastName": ya["lastName"], "department": ya["department"],
            "position": cargo, "email": ya["email"], "phone": ya["phone"], "notes": ya["notes"],
            "validFrom": ya["validFrom"], "validTo": ya["validTo"], "enabled": activa,
            "pinCode": extra.get("clave") if falta_clave else None, "clearPin": False,
            "cards": [c["number"] for c in ya["cards"]], "levelIds": ya["levelIds"],
            "fingerprints": huellas if faltan_huellas else None, "face": None, "clearFace": False})

    # --- Cerco eléctrico (sin equipo) ---------------------------------------
    paso("Cerco eléctrico: paneles sin enrolar")
    archivo_credenciales = os.path.join(datos_ruta, "cerco-credenciales.json")
    credenciales = {}
    if os.path.exists(archivo_credenciales):
        with open(archivo_credenciales, encoding="utf-8") as f:
            credenciales = json.load(f)
    paneles = {p["name"]: p for p in api.exigir("GET", "/api/cerco/panels")}
    for nombre, ubicacion in CERCOS:
        panel = paneles.get(nombre)
        if panel is None:
            creado = api.exigir("POST", "/api/cerco/panels", {"name": nombre, "locationId": ids_ubicacion[ubicacion],
                                                             "enabled": True}, host=HOST_EJEMPLO)
            credenciales[nombre] = creado["credentials"]
        elif nombre not in credenciales and not panel["enrolled"]:
            # Las credenciales se muestran una sola vez: si se perdió el archivo,
            # se rota la clave para tener otras (el panel nunca se enroló).
            credenciales[nombre] = api.exigir("POST", f"/api/cerco/panels/{panel['id']}/rotate-key", host=HOST_EJEMPLO)
    with open(archivo_credenciales, "w", encoding="utf-8") as f:
        json.dump(credenciales, f, ensure_ascii=False, indent=2)

    paso("Listo")


if __name__ == "__main__":
    main()
