# CLR TrueCentral VMS

Sistema de gestión de video (VMS) cliente-servidor multimarca de CLRobotics:
administración web, monitoreo en vivo multicámara con control PTZ y un plano
de media centralizado que hace fan-out de los streams (competencia directa de
HikCentral / SmartPSS / iVMS-4200).

## Arquitectura

```
┌────────────────────────────── Servidor (Windows) ──────────────────────────────┐
│  TrueCentralVms.Server (ASP.NET Core, x64)                                     │
│  ├─ PostgreSQL embebido      (tools\postgres, 127.0.0.1:25490)                 │
│  ├─ MediaMTX embebido        (tools\mediamtx, RTSP :8654 solo TCP,             │
│  │                            API 127.0.0.1:9911, auth delegada por HTTP)      │
│  ├─ Panel web de administración (wwwroot, SPA sin framework, :5090)            │
│  ├─ API REST + SignalR (/hubs/vms)                                             │
│  └─ Drivers de gestión: Hikvision (HCNetSDK) · Dahua (NetSDK) · ONVIF (SOAP)   │
└─────────────────────────────────────────────────────────────────────────────────┘
        ▲ HTTPS/HTTP + SignalR                     ▲ RTSP/TCP con token
        │                                          │
   Panel web (admin)                    TrueCentralVms.Client (WPF + FlyleafLib)
```

- **Los SDK de fábrica se usan solo para gestión** (validar credenciales,
  modelo/serie/firmware, canales, snapshots, PTZ). El video SIEMPRE viaja por
  MediaMTX: un solo pull RTSP por cámara (`sourceOnDemand`) compartido entre N
  espectadores.
- **Toda lectura RTSP se autoriza contra el servidor** (`authMethod: http`):
  el cliente pide una concesión (`POST /api/streams/request`), recibe una URL
  con token de 60 s y MediaMTX valida ese token en `/api/streaming/auth`.
  Cada visualización queda auditada en `StreamSessions` (quién, qué canal,
  desde qué IP, cuándo empezó y terminó).

## Matriz de puertos

| Servicio | Puerto | Bind |
|---|---|---|
| HTTP / API / SignalR / panel web | **5090** | 0.0.0.0 |
| PostgreSQL embebido | **25490** | 127.0.0.1 |
| MediaMTX RTSP (espectadores) | **8654** | 0.0.0.0, solo TCP |
| MediaMTX API de control | **9911** | 127.0.0.1 |
| MediaMTX del **cliente** (solo al proyectar la pantalla al muro) | **8554** | 0.0.0.0, TCP |

El 8554 lo abre el propio puesto de operación mientras transmite su pantalla
al muro (lo consume el decodificador); el resto del tiempo no escucha nada.
El producto videowall `vwcontroller`, del que se portó el muro, usa
5080/25480/8554: coexisten en la misma máquina.

## Estructura de la solución

```
CLRTrueCentralVMS.slnx
src\TrueCentralVms.Core\               contratos: DTOs, IDeviceDriver, hub SignalR
src\TrueCentralVms.Server\             servidor + panel web (wwwroot)
src\TrueCentralVms.Client\             cliente de escritorio WPF (FlyleafLib)
src\TrueCentralVms.Watchdog\           monitor de escritorio del servidor
src\TrueCentralVms.Drivers.Hikvision\  interop HCNetSDK 6.1.9.48
src\TrueCentralVms.Drivers.Dahua\      interop propio contra dhnetsdk.h (x64)
src\TrueCentralVms.Drivers.Onvif\      SOAP manual (WS-UsernameToken)
native\hikvision\ · native\dahua\      DLLs de los SDK (git-ignored)
tools\postgres\ · tools\mediamtx\      binarios embebidos (git-ignored)
tools\ffmpeg-flyleaf\                  FFmpeg compartido para FlyleafLib (git-ignored)
build\setup-binaries.ps1               regenera todos los binarios git-ignored
```

## Ejecutar en desarrollo

Requisitos: .NET 10 SDK, Windows x64, y los binarios de `build\setup-binaries.ps1`.

```powershell
# 1. Binarios nativos (SDKs, PostgreSQL, MediaMTX, FFmpeg) — una sola vez
.\build\setup-binaries.ps1

# 2. Servidor (el directorio de trabajo define dónde vive pgdata)
cd src\TrueCentralVms.Server
dotnet run

# 3. Cliente de escritorio
cd src\TrueCentralVms.Client
dotnet run

# 4. Watchdog del servidor (opcional; pide elevación al abrirse)
cd src\TrueCentralVms.Watchdog
dotnet run
```

Panel web: `http://localhost:5090`. El sistema viene **desactivado de
fábrica**: el primer administrador se crea desde el asistente del panel, que
solo acepta conexiones desde la propia máquina del servidor.

## Instaladores

Dos instaladores de Windows (Inno Setup 6, x64, en español) salen de
`installer\build-installers.ps1`:

| Instalador | Qué instala | Dónde |
|---|---|---|
| `CLRTrueCentralVMS-Suite-Setup-<versión>.exe` | **Suite completa**: servidor como servicio de Windows `CLRTrueCentralVMS` (API, panel web, SignalR, drivers), PostgreSQL embebido, MediaMTX y FFmpeg; opcionalmente el cliente de escritorio en el mismo equipo (tarea marcada por defecto). El instalador del cliente queda además en `client-setup\` para llevarlo a los demás puestos. | `%ProgramFiles%\CLR TrueCentral VMS\Server` · datos en `%ProgramData%\CLRTrueCentralVMS` (pgdata, anpr, workflows, logs) |
| `CLRTrueCentralVMS-Client-Setup-<versión>.exe` | **Solo el cliente** de operación (WPF) con el FFmpeg de FlyleafLib, ffmpeg/ffprobe y MediaMTX para la proyección de pantalla al muro. | `%ProgramFiles%\CLR TrueCentral VMS\Client` · preferencias en `%AppData%\CLRTrueCentralVMS\client.json` |

Ambos son self-contained (.NET 10 incluido: el equipo destino no necesita
instalar nada más) y sirven tanto para instalar como para **actualizar**
sobre una instalación existente: la suite detiene el servicio, reemplaza los
binarios, conserva los datos y `appsettings.Local.json`, y vuelve a arrancar
el servicio verificando `/api/health`; las migraciones de esquema las aplica
el propio servidor al iniciar.

Qué hace la suite en el equipo, además de copiar archivos:

- Crea (o reconfigura) el servicio `CLRTrueCentralVMS` (LocalSystem, inicio
  automático) con acciones de recuperación del SCM (reinicio a los 5, 15 y
  60 s si el proceso muere; contador reseteado tras un día estable). Las
  caídas de los servicios internos las cubre el supervisor del servidor.
- Reglas de firewall: TCP 5090 (panel/API) y 5091 (receptor SIA DC-09), UDP
  por programa para el descubrimiento de equipos (SADP 37020, WS-Discovery
  3702, Dahua 37810) y TCP 8654 para el MediaMTX del servidor.
- ACL de `%ProgramData%\CLRTrueCentralVMS` solo para SYSTEM y
  Administradores (ahí viven la clave del clúster y la llave AES de las
  credenciales de los equipos).
- Si el puerto 25490 de PostgreSQL está ocupado, elige el primer libre hasta
  25539 y lo anota en `appsettings.Local.json` (una vez creado el clúster,
  el puerto vive dentro de él y no se cambia).
- Al terminar ofrece abrir `http://localhost:5090`: el sistema viene
  desactivado de fábrica y el primer administrador se crea desde el asistente
  del panel, que solo acepta conexiones desde la propia máquina.
- Copia el **runtime de Visual C++** (`VCRUNTIME140`, `VCRUNTIME140_1`,
  `MSVCP140`) junto a los binarios de PostgreSQL. No viene con Windows: sin
  él `initdb.exe` muere con `0xC0000135` en un servidor recién instalado y la
  base nunca se crea.
- Instala en silencio el **Hik IP Receiver Pro**, la receptora de los paneles
  que reportan por ISUP/OTAP (AX PRO, AX HYBRID PRO). No es opcional: sin ella
  esos paneles no se pueden conectar. Queda escuchando en `127.0.0.1:8091` en
  vez del puerto 80 abierto a la red, así que su web y su API las usa solo el
  servidor del VMS desde este mismo equipo. Los paneles no usan ese puerto: se
  registran por TCP 7091, 7660-7667 y 8661, que sí quedan abiertos en el
  firewall. Aparece como un programa aparte en «Programas y características»,
  pero no es opcional ni independiente: desinstalar el VMS la quita también
  (ver «Desinstalación»).
  Durante la instalación, el instalador del fabricante abre el navegador en la
  página del receptor: es un paso suyo que hace incluso en modo silencioso y no
  se puede desactivar. El instalador de la suite pide cerrar el navegador antes
  de empezar y, si se partió sin ninguno abierto, cierra esa ventana al
  terminar (solo la que apunta al receptor: mira la línea de comandos, así que
  no toca ninguna otra). Si se prefiere seguir con el navegador abierto, la
  ventana queda ahí y basta con cerrarla a mano; no se cierra un navegador que
  ya estaba en uso, porque ahí Windows abre una pestaña en vez de un proceso
  nuevo y se perdería lo que hubiera abierto.
- **La receptora queda sin interfaz web.** Sirve en el mismo puerto su panel y
  su API, repartiendo por ruta, así que el instalador corta solo la raíz: quien
  abra `http://127.0.0.1:8091` recibe 403 y la API sigue funcionando. Así la
  receptora es un servicio interno que nadie ve ni administra, ni siquiera
  desde el propio servidor. Es reversible: el archivo original queda guardado
  como `nginx.conf.clr-original` en la carpeta de la receptora.
- **La receptora se activa sola.** En su primer arranque el servidor le crea la
  contraseña de administrador, que genera al azar y guarda cifrada junto a los
  datos (`tcvms-iprp.secret`, misma protección que la credencial de
  PostgreSQL). Nadie la escribe ni la conoce: para el operador la receptora es
  un servicio interno que no se ve, como PostgreSQL o MediaMTX. Al agregar un
  panel basta marcar «Usar la receptora instalada en este servidor» y el
  formulario deja de pedir dirección y credenciales.

### Desinstalación

CLR TrueCentral VMS es una solución cerrada: nada de lo que instala sirve por
separado. Por eso **desinstalar es un borrado total**, sin preguntas por
partes. Se avisa una sola vez, al principio, con un cuadro que hay que
confirmar (no aparece en desinstalación silenciosa), y a partir de ahí se
elimina:

- el servicio `CLRTrueCentralVMS` y todo `%ProgramFiles%\CLR TrueCentral VMS`
  (incluidos los certificados que MediaMTX se genera solo, que no están en el
  registro de instalación);
- **la base de datos completa** y el resto de `%ProgramData%\CLRTrueCentralVMS`:
  personas, huellas, tarjetas, permisos, eventos, matrículas, workflows,
  registros y la licencia activada;
- el **Hik IP Receiver Pro**, con su servicio `DeviceGatewayService`, su propio
  PostgreSQL, su historial de eventos y los paneles que tenga registrados;
- el **cliente de escritorio** y el **complemento de enrolamiento** instalados
  en ese equipo (se lanzan sus desinstaladores en silencio);
- las reglas de firewall de los cuatro programas;
- las preferencias y registros por usuario, en todos los perfiles de la
  máquina: `%AppData%\CLRTrueCentralVMS` (cliente y complemento),
  `%LocalAppData%\CLRTrueCentral` (proyección de pantalla) y
  `%LocalAppData%\CLRobotics\TrueCentral` (migrador desde HikCentral, que puede
  guardar credenciales de HCP).

No queda copia de la base de datos: **si hace falta conservarla, hay que
respaldarla antes**.

> **La licencia hay que desactivarla antes.** El desinstalador borra el
> `license.lic` local, pero no avisa al servidor de licencias: para el servidor
> esa activación sigue ocupada y el equipo nuevo no podrá tomarla. Si la
> licencia se va a reutilizar, hay que desactivarla **con el servidor todavía
> funcionando**, desde Sistema → Licencia («Desactivar en este equipo»).
>
> El desinstalador lo detecta y avisa: si encuentra una licencia activa
> (`activationCode` en `%ProgramData%\CLRTrueCentralVMS\license\state.json`),
> antes de cualquier otra cosa muestra el código de activación, explica que la
> licencia va a quedar contada como usada y ofrece cancelar para liberarla —
> con los pasos, y con «No» por defecto. En ese momento el servicio todavía
> está corriendo, así que el panel web responde y la desactivación es posible;
> una vez empezada la desinstalación, ya no.

> **Actualizar no pasa por acá.** Para pasar a una versión nueva se ejecuta su
> instalador **encima** de la anterior (mismo `AppId`): eso conserva los datos,
> `appsettings.Local.json` y la receptora. Desinstalar para «reinstalar limpio»
> borra todo.

Los desinstaladores del cliente y del complemento, ejecutados por su cuenta en
un puesto de operación, también se llevan sus propios ajustes de ese usuario.

Compilar los instaladores (requiere Inno Setup 6, `winget install -e --id
JRSoftware.InnoSetup`, y los binarios de `build\setup-binaries.ps1`):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1              # ambos
powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1 -Solo Client # solo el cliente
```

El script publica servidor y cliente (`build\publish\`), verifica que estén
las DLL nativas, PostgreSQL, MediaMTX y FFmpeg, compila `client.iss` y luego
`suite.iss` (que empaqueta el .exe del cliente) y deja los .exe en `dist\`.
La versión sale del archivo `VERSION`. Instalación desatendida:
`Setup.exe /VERYSILENT /NORESTART` (la suite acepta además
`/TASKS=installclient` o `/TASKS=` para incluir u omitir el cliente).

## Cliente de escritorio

- Página de inicio con módulos (estilo DSS); **Vista en Vivo** se abre como
  viñeta en el navbar y la ventana usa barra de título propia (sin marco de
  Windows), con indicadores de CPU/RAM/disco del servidor (gris/amarillo/rojo
  por umbral, detalle en tooltip).
- Vista en Vivo: árbol de dispositivos con buscador, divisiones de pantalla
  estilo iVMS-4200 (1/4/6/8/9/13/16/25/36/64, incluidas las asimétricas; la
  última usada se recuerda como preferencia local), apertura por doble clic o
  arrastrando el canal a un cuadro, selección sincronizada grilla↔árbol,
  audio por cuadro (exclusivo), y **reconexión automática** de cada cuadro
  ante cortes de red, reinicios del equipo o expulsiones (pide una concesión
  nueva cada vez).
- Botón **Vistas** al centro de la barra, sobre la grilla: guarda y recupera
  *vistas* — la división de pantalla más qué cámara estaba en cada cuadro —
  como las *Custom View* de iVMS-4200. El desplegable lista las vistas
  (nombre, cuántas cámaras y qué división), el clic en una la **carga** en la
  grilla (por tandas, con el mismo aviso bloqueante de la apertura masiva) y
  cada fila propia trae **actualizar** (la reescribe con lo que hay ahora) y
  **eliminar**, ambos con confirmación. Abajo, el recuadro para guardar la
  grilla actual con un nombre; la casilla **Compartir con todos los puestos**
  la deja visible para todo el mundo (si no, es privada de su dueño). El
  rótulo del botón muestra la última vista cargada.
- Las vistas viven **en el servidor**, no en el puesto: el operador recupera su
  pantalla de siempre desde cualquier estación, y los nombres no chocan entre
  usuarios (cada uno puede tener su "Turno noche"). Modificarlas o borrarlas
  solo puede su dueño o un administrador. Las cámaras que ya no estén en el
  inventario se descartan al guardar y su cuadro queda libre al cargar, con
  aviso en la barra de estado. Guardar, actualizar, eliminar y cargar una
  vista quedan en la **bitácora de auditoría** (categoría *Video en vivo*).
- Doble clic sobre un video: el cuadro se **maximiza** dentro de la grilla
  (los demás siguen corriendo ocultos); doble clic de nuevo restaura la
  división anterior. Botón **Pantalla completa**: el área de la grilla ocupa
  el monitor completo sin ningún elemento de la interfaz (Esc para salir).
- **Indicador de carga** en la barra de cada cuadro mientras se abre el video
  (conectar con la cámara o pedirle el tramo al grabador toma un momento). Va
  en la barra y no encima de la imagen porque el video se pinta en una ventana
  propia superpuesta, que taparía cualquier elemento puesto sobre ella.
- Botón **Zoom** (Vista en Vivo y Reproducción): enciende el zoom digital —
  el puntero pasa a lupa y arrastrando sobre el video se dibuja el recuadro
  del área a acercar; al soltar, esa área ocupa todo el cuadro (hasta 20×) y
  el clic derecho vuelve a 1×. Es zoom sobre la imagen ya recibida (recorte
  del viewport en GPU): no mueve la cámara ni pide otro stream, así que
  funciona igual en vivo, en grabaciones y con el video pausado. En la Vista
  en Vivo la rueda del mouse sigue acercando/alejando centrada en el cursor.
- PTZ: panel siempre presente (minimizado por defecto) con controles
  habilitados solo si el cuadro seleccionado tiene cámara PTZ
  (pan/tilt/zoom/foco/iris continuos + presets 1..300). Manejo por teclado:
  **flechas** = pan/tilt, **+/−** = zoom, y **Shift sostenido** = modo
  precisión (velocidad mínima, con píldora indicadora en el panel).
- Reproducción: las grabaciones viven en el disco del propio equipo (el VMS
  no almacena video). El doble clic en el árbol suma canales hasta **4
  sincronizados** (con la grilla llena reemplaza el cuadro con foco; Ctrl +
  doble clic deja solo ese canal) y todos reproducen el mismo instante.
- Cambiar de día **continúa en la misma hora**: si venía en las 14:32 del
  martes y elige el viernes, arranca en las 14:32 del viernes; sin nada en
  reproducción parte a las 00:00 del día elegido, y si a esa hora no hay
  grabación salta al primer tramo disponible.
- Canales al cuadro por **arrastrar y soltar** desde el árbol (además del
  doble clic): soltar sobre un cuadro reemplaza ESE cuadro. En ambos casos la
  reproducción **arranca sola** en el día y la hora que muestra la barra: si
  ya hay otro canal andando, el nuevo se engancha a ese mismo instante; si no,
  parte desde donde quedó la aguja o desde las 00:00 del día elegido.
- **Calendario** en la barra de fecha: la fecha abre un calendario mensual y
  las casillas traen una marca en los días que el equipo tiene grabados, así
  se ve de un vistazo hasta dónde llega su disco (llegar al viernes de la
  semana pasada ya no exige recorrer día por día con las flechas). Los días
  del mes se resuelven con UNA búsqueda contra el equipo y quedan en caché por
  canal y mes: los meses ya cerrados no se vuelven a preguntar en la sesión, y
  el mes en curso se refresca cada 5 minutos porque sigue grabando.
- Línea de tiempo con una pista por canal, tramos coloreados por tipo y
  **zoom de 24 h a 1 minuto** (botones − / + o rueda del mouse): el clic
  salta a esa hora y el **arrastre** mueve la aguja mostrando la hora de
  destino. Mientras se reproduce, la vista acompaña a la aguja; si el
  operador acerca con la rueda o empuja la tira, la vista queda donde él la
  dejó (puede revisar otra hora sin que el video se la mueva) y vuelve a
  seguir la aguja al hacer clic para reproducir, con los botones − / + o al
  cambiar de día. Reloj con fecha y hora completas, saltos de ±30 s y ±5 min y
  velocidad 0,25×–8×. El grabador entrega a tiempo real salvo que se le pida
  otra cosa, así que **a velocidades distintas de 1× el servidor toma la
  sesión RTSP** contra el equipo y le pide ese ritmo con la cabecera `Scale`
  del PLAY, republicando hacia el media server; a 1× no interviene y el camino
  es el de siempre. Medido de punta a punta contra el DVR real: a 1×, 12 s de
  video en 13 s de reloj; a 4×, 11 s de video en **3 s**. Cambiar de velocidad
  reabre el tramo desde donde va la aguja.
- Doble clic sobre el video como en un reproductor: tercio izquierdo −30 s,
  tercio derecho +30 s y centro para entrar o salir de pantalla completa
  (Esc también sale).
- **Captura** y **cápsula** también en Reproducción, con los mismos botones,
  carpetas y avisos que la Vista en Vivo. El archivo se nombra con la hora de
  la GRABACIÓN que se está viendo, no con la del reloj del puesto. Es distinto
  de *Exportar MP4*: eso le pide al equipo un tramo por hora exacta; la cápsula
  guarda lo que el operador está mirando.
- Botón **Recorte**: arrastrando sobre la línea se marca un tramo para
  reproducirlo o **exportarlo a MP4** a la carpeta de grabaciones (el
  servidor lo arma con FFmpeg mientras el equipo lo entrega; se puede
  cancelar). Hikvision y Dahua se posicionan a la hora exacta; en ONVIF el
  estándar no lo permite y el cliente avisa.
- Aplicaciones → **Reconocimiento de patentes**: lecturas ANPR en vivo con
  ficha completa del vehículo e historial buscable (ver más abajo).
- **Paneles de alarma**: estado de áreas y zonas de las centrales de
  intrusión, armado/desarmado/anulación y eventos en vivo (ver más abajo);
  una alarma avisa con un aviso flotante y enciende el icono del riel aunque
  la viñeta esté cerrada.
- Credenciales recordadas cifradas con DPAPI, inicio de sesión automático
  opcional (se apaga en **Configuración → Sistema**), lista de usuarios
  recientes.
- El nombre del usuario en el navbar abre el **menú de la sesión**: a qué
  servidor está conectado (informativo) y **Cerrar sesión**, que revoca el
  token en el servidor (queda en la bitácora, categoría `auth`) y devuelve al
  login sin reiniciar la aplicación.

## Muro de video

Decodificación por hardware hacia muros de monitores, portada del producto
`vwcontroller`. La diferencia con aquel producto: **las cámaras salen del
inventario del VMS** (Devices/Channels), no de un registro aparte.

El decodificador se conecta **directo al equipo**, no a MediaMTX: las
concesiones de streaming del VMS son tokens de 60 s pensados para
espectadores y un muro decodifica durante días. Con Hikvision usa el
protocolo privado del fabricante (IP/puerto/usuario/canal); con Dahua y ONVIF,
la URL RTSP que arma el driver del dispositivo.

**Configuración (panel web, rol Admin)**

- `#/decoders` — registre el decodificador (IP, puerto 8000, credenciales,
  driver *Hikvision (HCNetSDK)*). Al guardar se valida contra el equipo.
  **Probar** lee sus salidas físicas (HDMI/VGA/BNC/DVI) y sus canales de
  decodificación; **Diagnóstico** vuelca el estado real del muro.
- `#/walls` — cree el muro: filas × columnas y, por cada posición, qué salida
  física alimenta esa pantalla y en cuántas ventanas se divide. Los canales
  de decodificación se asignan solos (uno por ventana). Desde la misma página
  se opera el muro y se guardan/aplican layouts.

**Operación (cliente WPF → Muro de video)**

- Arrastre un canal del panel derecho a una ventana; arrastre una ventana
  sobre otra para intercambiar sus cámaras; clic derecho para dividirla en
  4/9/16; **✕** para liberarla.
- **Doble clic**: la cámara ocupa su monitor completo (instantáneo: el equipo
  solo agranda esa ventana, el resto sigue decodificando). **Ctrl+doble
  clic**: ocupa TODO el muro. Cada monitor tiene un selector de división
  (1/2/4/6/8/9/12/16/25/36) que re-asigna canales y re-decodifica solo.
- **⊞ Agrupar** fusiona ventanas contiguas en una grande; **▣ Flotante**
  dibuja una ventana de rect libre encima del mosaico (puede cruzar
  monitores); **👁 Vista previa** muestra un fotograma de cada cámara dentro
  de su ventana; **Layouts** guarda el estado con nombre y lo aplica de una
  vez; **Sincronizar** re-empuja la división al equipo si estaba apagado.
- **🖥 Proyectar** transmite una pantalla de este PC (o un archivo de video)
  al muro: el puesto levanta MediaMTX + FFmpeg locales y el decodificador lee
  ese RTSP. Es temporal: al detenerla, cada ventana vuelve a su cámara
  anterior. Requiere `tools\mediamtx` y `tools\ffmpeg` junto al cliente y que
  el firewall permita `mediamtx.exe` (8554/TCP).
- Los cambios se propagan en vivo a todos los clientes y al panel por SignalR.

Notas del equipo (validadas contra un DS-6908UDI real en `vwcontroller`): en
la familia video wall cada sub-ventana es una **ventana del muro** y el
equipo tiene un presupuesto total de ventanas simultáneas; si se excede, el
sistema lo avisa y hay que reducir la división de otros monitores.

## Paneles de alarma

Centrales de intrusión (hoy **Hikvision por ISAPI**, sin SDK: AX PRO / AX
Hybrid PRO y los paneles híbridos cableados de la línea clásica como el
DS-PHA64-W4M, ambos validados con hardware real): el servidor mantiene con
cada panel habilitado el canal de eventos (`alertStream`) y relee su estado
cada 30 s y apenas llega un evento, así que lo que ve el operador es siempre
el estado real del equipo. Hay dos drivers Hikvision:

- **Directo** (`hikvision-isapi`): el VMS habla con el panel por su IP.
> Con la receptora de este servidor el formulario pide solo lo que hay que
> copiar del panel: su **ID y clave ISUP** (en el AX PRO, Comunicación → ISUP) y
> el protocolo. Dirección, puerto y credenciales de la receptora son internos y
> no se muestran, y al guardar el sistema da de alta el panel en ella. Para una
> receptora ajena (en otro equipo) el formulario sigue pidiendo su dirección y
> credenciales, y ofrece «Equipos de la receptora…» para ver y administrar su
> inventario. Todo queda en la bitácora ISO 27001.
>
> **La clave ISUP/OTAP del panel debe ser de 8 a 32 letras y números.** La
> receptora (Hik IP Receiver Pro V2.5.0.6) rechaza por API cualquier clave con
> símbolos o espacios —responde `badParameters` / `addDeviceFailed`, tanto en
> claro como cifrada—, así que una clave como `Clr$2023!` hace fallar el alta.
> Si el panel tiene una clave con símbolos, hay que cambiarla en el panel
> (Comunicación → ISUP) y usar esa misma en el VMS; el formulario lo valida
> antes de enviar.

> Los paneles se dan de alta **en la receptora desde el propio VMS**: en el
> formulario del panel, «Equipos de la receptora…» lista los equipos que tiene
> la pasarela, permite agregar uno nuevo con su ID y clave ISUP/OTAP y quitar
> los que ya no se usan, sin entrar a la interfaz web del fabricante. Todo
> queda en la bitácora ISO 27001.

- **A través del Hik IP Receiver Pro** (`hikvision-iprp`): los paneles se
  registran en la pasarela de Hikvision (ISUP 5.0, OTAP o Hik-Partner Pro) y
  el VMS consume su API con la cuenta de la pasarela. En el mantenedor se
  indica la dirección del IP Receiver Pro y el **equipo dentro de ella**
  (uuid, serie, cuenta o ID ISUP; con el campo vacío, "Probar conexión"
  lista los equipos disponibles). Para entregar eventos la pasarela necesita
  *Automation Output → Protocol* habilitado con tipo **Private** —sin eso su
  API contesta "Invalid operation" y el panel queda con el canal de eventos
  cerrado, funcionando solo por sondeo—, pero **eso lo deja puesto el propio
  sistema**: al activar la receptora de este servidor, y en cualquier
  pasarela apenas una suscripción es rechazada. Solo hay que habilitarlo a
  mano si la cuenta configurada no tiene permiso para cambiarlo. Estado,
  armado, desarmado y bypass van por
  la pasarela, y los eventos llegan por su suscripción (CIDAlarm con el
  usuario que armó/desarmó, y la conexión/desconexión del panel respecto de
  la pasarela). La pasarela admite **una sola suscripción a la vez** —a la
  segunda le contesta «No more task can be added»—, así que el servidor abre
  una por receptora y le cuelga todos sus paneles, repartiendo cada evento a
  su dueño: con dos o más paneles en la misma receptora, si no, uno quedaba
  siempre con el canal de eventos cerrado. Además el VMS actúa como **centro receptor de
alarmas (SIA DC-09, protocolos ADM-CID y SIA-DCS por TCP, puerto 5091,
`Alarms:Receiver`)**: en el panel se configura como *Alarm Receiving Center*
con la IP del servidor y ese puerto, y el equipo reporta con confirmación
todo lo que ocurre (alarmas, armados con el usuario que los hizo,
anulaciones, fallas y pruebas periódicas). Solo se aceptan reportes desde la
dirección de un panel habilitado; el resto se rechaza y queda en la bitácora.

- **La dirección que se muestra dice de quién es.** Un panel que llega por
  receptora aparece como `Receptora 127.0.0.1:8091 · panel 2001`, en el
  cliente y en el panel web: esa IP es la de la **receptora**, no la del
  panel. El panel es el que llama hacia ella (ISUP/OTAP), así que el sistema
  nunca ve su dirección de red. En el formulario, el campo se rotula
  «Dirección del IP Receiver Pro» cuando el driver es el de la pasarela.
- **Nombres del panel: la codificación no se da por buena.** Varios firmwares
  declaran UTF-8 y mandan los nombres en otra codificación, y así un área
  «Portón Proveedores» llegaba ilegible y se guardaba rota en el inventario y
  dentro de **cada evento del historial**, donde ya no hay cómo corregirla.
  Ahora el cuerpo de cada respuesta se decodifica probando, en orden: BOM si
  lo trae; el `charset` declarado solo cuando es una página china
  (gb2312/gbk/gb18030, que es información real); UTF-8 estricto; Latin-1 si no
  deja caracteres de control; y GB18030 como último recurso cuando Latin-1 sí
  los deja, que es la firma de un texto de dos bytes leído de a uno. Un nombre
  que aun así quede con caracteres de reemplazo o de control **se descarta**:
  el área o la zona muestra su nombre genérico («Área 7», «Zona 3») en vez de
  basura. Un texto chino que no declare su charset se lee como Latin-1: a esa
  altura es indistinguible de un nombre occidental acentuado y se prefiere
  acertarle al español.

- **Mantenedor** en `#/alarm-panels` (Dispositivos → Paneles de alarma, rol
  Admin): dirección, puerto, HTTPS opcional (certificado autofirmado
  aceptado) y usuario **local** del panel (el de la activación, normalmente
  `admin`; la cuenta Hik-Connect no sirve). **Probar conexión** muestra
  modelo, serie, firmware, áreas y zonas antes de guardar. Tras varios
  intentos fallidos el panel bloquea el acceso 30 min: el servidor no insiste
  (reintenta recién a los 10 min) y lo dice en el estado del panel.
- **Cliente**: módulo *Paneles de alarma* con la lista de paneles (armado
  resumido, conexión, canal de eventos), las áreas con **Armar total /
  Parcial / Desarmar / Silenciar alarma**, las zonas con su estado (normal,
  activada, falla, sin comunicación, tamper, batería baja) y **Anular /
  Restituir**, y el flujo de eventos en vivo filtrable por panel y tipo. El
  panel web ofrece las mismas órdenes en el detalle de cada panel.
- **Eventos**: los que empuja el panel (Contact-ID traducido al español:
  intrusión, pánico, incendio, tamper, armado/desarmado por usuario,
  anulaciones, fallas de batería/AC/comunicación, restauraciones) más los
  que el sondeo detecta por diferencia y los que salen del propio VMS (con el
  operador). Historial en `AlarmEvents` (`Alarms:RetentionDays`, 365 por
  omisión) y consulta por `/api/alarms/events`.
- **Auditoría** (categoría *Paneles de alarma*): altas/ediciones, pruebas de
  conexión, cada armado/desarmado/anulación con su operador, los rechazos del
  equipo (`success=false`), las alarmas recibidas del panel y la caída o
  recuperación de la conexión.
- Otra marca = implementar `IAlarmPanelDriverFactory` (Core) y registrarla en
  `Program.cs`.

## Automatizaciones (workflows)

"Cuando pase ESTO, hacer ESTO OTRO", sin programar nada: el servidor escucha
lo que ocurre y ejecuta acciones solo. Cada automatización es un **diagrama
de flujo** que se arma en un editor visual del panel web (`#/workflows/edit`,
al estilo Bizagi/Lucid): un **punto de partida** (el disparador y su filtro),
y desde ahí se agregan pasos —**condiciones sí/no**, **esperas**, **acciones**
y **fines**— conectados con flechas. Cada acción tiene dos salidas
(*Siguiente* y *Si falla*); cada condición, *Sí* y *No*. Se agrega un paso
haciendo clic en el círculo de salida de otro (menú), arrastrando desde la
paleta o con clic en la paleta (se conecta después del paso seleccionado);
las flechas se dibujan arrastrando de un puerto de salida a otro paso. El
motor recorre el diagrama tal cual está dibujado; las ramas que salen de un
mismo punto corren una tras otra, de izquierda a derecha. El botón **Probar**
guarda, ejecuta las acciones de verdad con un evento de ejemplo y pinta en el
diagrama qué paso salió bien y cuál no. Las automatizaciones anteriores al
editor se muestran como una línea recta. **Duplicar** (en el listado) crea
una copia completa —diagrama, filtro, acciones y contraseñas— que nace
pausada con "(copia)" en el nombre y se abre en el editor: es la forma rápida
de hacer una automatización parecida cambiando solo la zona o la cámara.

**Disparadores** (`WorkflowTriggerTypes`):

| Disparador | Cuándo | Filtro |
|---|---|---|
| **Evento de panel de alarma** | alarma, sensor interrumpido, armado, anulación, falla… | paneles, tipo, severidad, zonas y áreas por nombre, códigos Contact-ID, origen, texto, condición sostenida |
| **Conexión con un panel** | el servidor pierde o recupera un panel | paneles, estado |
| **Evento de cámara (analítica)** | movimiento, cruce de línea, intrusión, entrada/salida de región, merodeo, objeto abandonado, aglomeración, rostro, conteo, pérdida de video, cámara tapada, anomalía de audio/video, entrada de alarma, falla del equipo | tipo de evento, equipos, cámaras (canales) |
| **Lectura de patente** | una cámara ANPR leyó una placa | cámaras, **lista blanca / lista negra** con comodines (`*`, `?`), confianza mínima |
| **Evento de control de acceso** | acceso concedido/denegado, puerta abierta/cerrada, forzada, mantenida abierta, sabotaje | resultado, credencial, equipos, puertas, personas, texto |
| **Conexión de un equipo** | una cámara/grabador, terminal de acceso o parlante IP pierde o recupera la conexión | clase de equipo, equipos, estado |
| **Horario programado** | a las horas indicadas, los días marcados | horas (hh:mm), días |
| **Llamada externa (HTTP)** | otro sistema hace `POST /api/workflows/hook/{clave}` (clave de 40 hex generada por el servidor; el cuerpo JSON queda como marcas `{campo}`) | — |

Todos admiten además **ventana horaria** (días de la semana y rango, que
puede cruzar la medianoche: 22:00 a 06:00), y las mismas condiciones se usan
en los pasos de **condición** del diagrama ("¿es de noche?", "¿la patente es
de un residente?"). Los eventos de cámara los reciben hoy los equipos
**Hikvision** por el canal de alarma del SDK (`HikvisionEvents`; las reglas se
configuran en la web del propio equipo). El servidor se suscribe solo a los
equipos que pida alguna automatización habilitada (`VideoEventService`): sin
automatizaciones de ese tipo no se abre ningún canal.

Acciones disponibles (`IWorkflowActionExecutor`, una clase cada una):

| Acción | Qué hace |
|---|---|
| **Capturar foto** | Snapshot JPEG de las cámaras elegidas (1–5 por cámara). Queda en disco y disponible para las acciones siguientes. La foto que trae el propio evento (analíticas con captura) también queda disponible. |
| **Enviar correo** | SMTP con MailKit (STARTTLS, TLS implícito o sin cifrar), adjuntando las fotos capturadas. |
| **Subir a FTP** | FTP/FTPS (explícito o implícito) con las fotos y un informe de texto opcional; la carpeta remota admite marcas y se crea sola. |
| **Llamar a un servicio (HTTP)** | GET/POST/PUT/… a otro sistema, con cabeceras, cuerpo y autenticación básica o digest. |
| **Sonar parlante IP** | Hikvision (audio bidireccional ISAPI: el servidor le envía el sonido ya convertido a G.711 al ritmo real), Axis (`playclip.cgi`) o cualquier marca por URL. |
| **Avisar a los operadores** | Notificación en vivo por el hub a todos los conectados, **con la foto capturada** y **alarma sonora en el equipo del operador** (el sonido lo elige la automatización entre los cargados en el servidor, o el pitido del sistema). |
| **Orden a una puerta** | Abrir (pulso), mantener abierta, bloquear o cerrar puertas del control de acceso ("patente de la lista → abrir el portón"). Queda en la bitácora como orden del sistema. |
| **Armar / desarmar panel** | Armar (total o parcial), desarmar o borrar la alarma de un área (o todas) de un panel ("a las 22:00 armar la bodega"). |
| **Mover cámara PTZ a preset** | Apunta un domo a un preset guardado en el equipo antes de sacar la foto. |

Los textos (asunto, cuerpo, URL, carpeta remota…) admiten **marcas** que el
servidor reemplaza con los datos del evento: comunes (`{evento}`, `{tipo}`,
`{severidad}`, `{equipo}`, `{fechahora}`, `{workflow}`, `{servidor}`…) y
propias de cada disparador (`{panel}`, `{zona}`, `{patente}`, `{camara}`,
`{regla}`, `{persona}`, `{puerta}`, `{tarjeta}`, `{estado}`…); el editor las
lista al elegir el disparador. En las URL los valores se escapan.

Cada automatización tiene además un **tiempo mínimo entre ejecuciones** (60 s
por omisión): un detector que rebota veinte veces no manda veinte correos, y
una cámara con movimiento continuo (que informa cada segundo) tampoco.

**Condición sostenida**: la automatización se ejecuta solo si la interrupción
se mantiene N segundos — el servidor vigila la zona cada segundo durante ese
tiempo y descarta la ejecución en cuanto el sensor queda libre. Es lo que
distingue "alguien pasó frente al detector" de "la puerta quedó abierta".
Tolera las pausas cortas del detector (`Workflows:SustainedGapSeconds`, 3 s):
los sensores de movimiento informan por pulsos, no de corrido. El tiempo
mínimo entre ejecuciones se cuenta desde la última ejecución **real**, así una
verificación descartada no bloquea la interrupción larga que venga después.
Para no perderse una interrupción breve, los paneles vigilados así se sondean
cada 2 s (`Alarms:FastPollSeconds`) mientras exista una automatización que lo
pida; ese sondeo no escribe en la base ni empuja por el hub si nada cambió.

Como las centrales no reportan por Contact-ID lo que ocurre con el área
desarmada, el servidor genera por diferencia de estado el evento **«Sensor
interrumpido»** (y su restauración) cuando un detector se activa sin alarma:
es lo que permite automatizar puertas y portones fuera de horario.

**Ventana de alarma** (cliente de escritorio): las alertas que exigen
confirmación abren una ventana propia — a la izquierda el hecho (automatización,
qué la disparó, severidad, hora, descripción y estado del acuse) y a la derecha
tres pestañas: **video en vivo** de las cámaras vinculadas **en grilla** (1, 2,
2×2, 3×3 según cuántas sean, cada cuadro rotulado; se sueltan las concesiones al
cambiar de pestaña o cerrar), **fotos** capturadas con tira de miniaturas y
contador, y **qué hizo el sistema** (los pasos de la ejecución). Abajo, el botón
*Enterado*, *Silenciar* y un paginador para recorrer las alertas pendientes. La
alarma sonora admite **repeticiones = 0 = sonar hasta que alguien confirme**,
silencie o cierre la ventana.

**Centro de eventos** (sección del cliente, con su viñeta en la barra y su
botón en el riel): el historial de todas las alertas —
qué avisó el sistema, cuándo, qué lo disparó y **quién se dio por enterado, a
qué hora y en cuántos segundos** (o si quedó sin confirmar). Filtro *Solo sin
confirmar*, botón *Enterado* en la propia lista y *Ver* para abrir la ventana
de alarma completa (video, fotos y pasos). Se mantiene al día solo: cada
alerta nueva y cada confirmación llegan por el hub.

**Alertas con acuse de recibo**: cada aviso a los operadores queda registrado
como una alerta que nace **pendiente**. La ventana **no se cierra sola** y se
mantiene hasta que alguien la confirme (si el puesto estaba cerrado cuando se emitió,
al abrirlo aparecen las pendientes). La primera confirmación es la que queda —
usuario, hora, origen (cliente o panel) e IP— y baja el aviso en todos los
demás puestos. En `#/workflows` la sección **Alertas** muestra las pendientes
destacadas y el registro de quién confirmó cada una y **en cuánto tiempo**;
también se puede confirmar desde ahí. El botón **Ventana aparte** lo saca a
una ventana independiente (para dejarlo en otro monitor) con la misma vista y
el mismo ViewModel; al cerrarla, el módulo vuelve solo a su viñeta. Cada
confirmación va además a la
bitácora (`alert-acknowledged`), y una alerta pendiente **nunca** se purga
sola: es la evidencia de que nadie se dio por enterado.

Cada ejecución queda en el **historial** con qué la disparó, qué hizo cada
acción, cuánto demoró y las fotos que capturó, y en la **bitácora de
auditoría** (categoría `workflows`). El botón **Probar** ejecuta las acciones
de verdad con un evento de ejemplo, sin esperar a que el panel se alarme.

Panel web `#/workflows`: listado (con el número de pasos de cada diagrama),
historial, configuración del **servidor de correo** y carga de **sonidos**
para los parlantes; `#/workflows/edit?id=N` abre el **editor de diagrama**
(rol Admin). Los sonidos se convierten a G.711
µ-law y A-law con el FFmpeg que ya viene con el sistema). Las contraseñas de
cada acción (FTP, servicio HTTP, parlante) y la del correo se guardan cifradas
con AES-256-GCM y nunca salen por la API. Configuración en `appsettings.json`
→ `Workflows` (habilitación, ejecuciones simultáneas, tope por acción,
carpeta de las fotos y retención del historial).

## Parlantes IP

Altavoces de red (validado con Hikvision DS-QAZ1325G1T por ISAPI). Se
administran en **Dispositivos → Parlantes IP** (rol Admin): dirección, puerto,
usuario local del equipo (normalmente `admin`), grupo opcional y **Probar
conexión**, que muestra modelo, firmware y qué sabe hacer el equipo (voz en
vivo, biblioteca de audios, texto a voz). Cada parlante tiene su **Biblioteca**:
los audios guardados en el propio equipo (los de fábrica y los que suba desde el
panel en mp3/wav/aac), más **Crear desde texto**, que hace que el parlante
genere la voz (español incluido).

Desde el cliente de escritorio, en la Vista en Vivo, el panel **PARLANTES** (al
pie del árbol, junto al PTZ) permite marcar uno o varios parlantes y:

- **Mantener para hablar**: la voz del operador sale por los parlantes marcados
  mientras el botón esté presionado (micrófono del equipo del operador; el
  servidor la convierte a G.711 y la escribe en todos a la vez).
- **Sonido del servidor**: los mismos sonidos de Automatizaciones → Sonidos,
  transmitidos **sincronizados** a todos los marcados.
- **Biblioteca** y **Texto a voz**: el audio lo reproduce el propio parlante; con
  varios marcados se busca por nombre en cada uno.
- **Detener**: corta lo que esté sonando.

En Automatizaciones, la acción **Sonar parlante IP** ahora elige parlantes del
inventario (o todo un grupo) y puede reproducir un sonido del servidor, un
audio de la biblioteca del equipo o un texto leído en voz alta con las marcas
`{zona}`, `{panel}`, etc. Todo queda en la bitácora (categoría *Parlantes IP*):
quién habló, cuánto tiempo, qué se reprodujo y en qué equipos.

## Control de acceso

Terminales y controladoras de puertas de tres marcas:

| Marca | Equipos | Cómo se conecta | Credencial |
|---|---|---|---|
| Hikvision | DS-K1T, DS-K2, DS-K3, DS-K5 | ISAPI (HTTP) | usuario y contraseña |
| Dahua | ASI, ASA, ASC, ASG | CGI (HTTP) | usuario y contraseña |
| ZKTeco | terminales y controladoras | protocolo del SDK (TCP 4370) | clave de comunicación (Comm Key, 0 de fábrica) |

Los **equipos** se administran en **Dispositivos → Control de acceso** (rol
Admin): dirección, puerto, credencial
según la marca (el formulario cambia solo al elegirla), ubicación opcional y
**Probar conexión**, que muestra modelo, tipo, firmware, cuántas **puertas**
administra (con sus nombres) y qué sabe hacer (apertura remota, eventos,
tarjeta, huella, rostro y los cupos de personas y tarjetas del equipo). Al
guardar se valida contra el equipo: si no contesta, si la credencial no sirve o
si no es un equipo de control de acceso, no se guarda nada.

Debajo de la tabla, **Equipos en línea** lista lo que responde en el segmento
de red del servidor —SADP para Hikvision, DHDiscover para Dahua y el sondeo
propio de ZKTeco—, filtrado a lo **compatible**, con **Agregar** en un clic.
Los videoporteros (DS-KH/KV/KD, VT), las cámaras y las alarmas no aparecen
aquí: se administran en sus propias páginas. Los sondeos son de difusión y no
cruzan routers ni VPN; un equipo fuera del segmento se agrega escribiendo su
dirección.

Particularidades de ZKTeco, que se notan en el mantenedor: el equipo no tiene
usuario (la credencial es la **clave de comunicación**, un número), no habla
HTTPS y **acepta una sola conexión a la vez**, así que el VMS abre y cierra
sesión en cada operación y el descubrimiento no vuelve a interrogar a los
equipos que ya administra.

> **Si un equipo rechaza una contraseña que sí funciona en iVMS**: el VMS
> reintenta solo con la otra forma de login que acepta la familia (hay firmware
> de la línea DS-K que rechaza el `charset` del `Content-Type` o el
> `timeStamp` de la URL). Lo que NO conviene es insistir a mano: los equipos
> Hikvision bloquean el inicio de sesión a los pocos intentos fallidos, y el VMS
> avisa cuántos quedan y cuántos minutos falta para que se libere.

El servidor sondea cada equipo habilitado una vez por minuto para mantener el
estado de conexión y, a los que saben informarlo, les pregunta en qué modo está
cada puerta; **Revalidar** relee a pedido modelo, firmware, capacidades y
puertas. La licencia cuenta **puertas** (`access_doors`).

### Quién entra, por dónde y cuándo

La operación vive en el menú **Control de acceso**, y la cadena de decisión es
siempre la misma:

    HORARIO (cuándo)  +  PUERTAS (por dónde)  =  NIVEL DE ACCESO
    NIVEL DE ACCESO   →  PERSONAS                (quién puede)

- **Horarios**: los tramos en que se puede pasar, hasta 8 por día (es el tope de
  los equipos). Viene uno de fábrica, 24/7, que no se edita ni se borra. El
  editor tiene «+ tramo» y «Copiar a todos» para no repetir siete veces lo mismo.
- **Niveles de acceso**: un puñado de puertas más un horario. Es lo que se le
  asigna a la gente. Quien tiene dos niveles pasa por las puertas de los dos, y
  si una misma puerta le llega por ambos, vale la **suma** de sus horarios (uno
  la deja pasar el martes y el otro el jueves: pasa los dos días).
- **Personas**: el padrón. Se cargan con un **asistente de tres pasos** —quién
  es, con qué se identifica, por dónde pasa— en vez de un formulario de veinte
  campos: nombre, departamento, cargo y **vigencia** (los equipos la respetan
  solos); después **tarjetas**, **huellas** y **clave de teclado** —las dos
  últimas guardadas cifradas y que nunca se devuelven—; y al final los niveles
  de acceso, con una frase que resume por dónde va a entrar antes de guardar.
  Se guarda una sola vez, al terminar: cancelar a mitad de camino no deja
  personas a medio crear. El identificador con el que los equipos la reconocen
  se asigna al crearla y no cambia nunca.

### Avance de la escritura en los equipos

La página **Personas** muestra, mientras haya algo pendiente o fallido, una
franja con el avance de la pasada del sincronizador: cuántas personas van de
cuántas, a quién está escribiendo y en qué equipo, el tiempo estimado que falta
y un enlace a las que tienen error. La tabla se repinta sola cuando la pasada
avanza (no bajo un modal) y los botones «Escribir pendientes» y «Reenviar todo»
se deshabilitan mientras corre una pasada. Sale de `GET /api/access/sync/status`
(avance + conteo por estado) y se publica por el hub como `AccessSyncProgress`.

Dos reglas del sincronizador que nacieron de una migración de 114 personas:

- **Cada persona se toma una vez por pasada.** Antes, si cincuenta personas
  fallaban (fotos rechazadas), la misma pasada volvía a tomarlas y giraba sobre
  sí misma sin llegar nunca a las demás.
- **Las que fallan se reintentan con espera creciente**: 5, 10, 20… minutos
  hasta una hora, por persona y en memoria (tras reiniciar se reintenta
  enseguida). Los botones del panel y «Reintentar» las reintentan al instante.

### Huellas: el complemento de enrolamiento

El panel web no puede hablar con el lector USB del puesto —ningún navegador
puede—, así que esa parte la hace un **complemento** que se instala en el PC
donde está el lector (el de RR.HH. o portería, no el servidor). Corre en la
bandeja del sistema, arranca con Windows y expone su API **solo en 127.0.0.1**;
el panel lo descubre en los puertos 5081, 25471 y 25472, y si no lo encuentra
ofrece descargarlo del propio servidor.

- **Lector**: Hikvision **DS-K1F820-F / DS-K1F800-F** (FPModule_SDK V2.2.0).
  Windows lo ve como una unidad de CD-ROM USB, no como un puerto serie: el
  complemento los busca ahí y ofrece además la detección automática del SDK.
- **Cómo se enrola**: en *Personas → paso Credenciales* hay una grilla con los
  diez dedos y un botón **Capturar** en cada uno. El diálogo guía las tres
  apoyadas del dedo, muestra la imagen del sensor y la **calidad** de la
  plantilla (bajo 60/100 conviene repetir).
- **Dónde queda**: la plantilla viaja del complemento al panel y del panel al
  servidor, que la guarda **cifrada con AES-256-GCM** y **nunca la devuelve** —
  la API informa qué dedo está tomado y con qué calidad, no el dato biométrico.
  De ahí el sincronizador la baja a los equipos que la aceptan.
- **El rostro** se sigue tomando en el propio terminal, con la persona delante.

Se compila con el resto (`installeruild-installers.ps1`) y sale como
`CLRTrueCentralVMS-Complemento-Setup-<versión>.exe`; la suite lo deja publicado
en la carpeta `webcontrol\` del servidor, que es de donde el panel lo ofrece.
El SDK del lector lo copia `build\setup-binaries.ps1` desde `Resources\`.
- **Monitoreo**: las puertas en vivo, con **Abrir** (pulso), **Mantener
  abierta**, **Normal** y **Bloquear**, y debajo lo que va pasando. Cada orden
  queda en la bitácora con su autor.
- **Historial**: quién pasó por dónde y cuándo, con filtros por fecha, puerta,
  resultado y persona. Se purga solo según `Access:EventRetentionDays` (365 días
  por omisión).

Los accesos llegan **en vivo**: a los equipos que saben empujar sus eventos el
servidor les mantiene una escucha abierta, así que una tarjeta o el botón de
salida aparecen en el monitoreo en el momento, no en el próximo sondeo (medido
contra un terminal real: llegan en menos de un segundo). El sondeo sigue
existiendo como respaldo —cubre a las marcas que no empujan y recupera lo que
haya pasado mientras el servidor estuvo apagado—, y que las dos vías traigan el
mismo evento no duplica nada.

El VMS es la **fuente de verdad** y los equipos son una copia suya: cualquier
cambio en personas, niveles u horarios deja a los afectados *pendientes* y un
servicio en segundo plano los escribe en los equipos que correspondan. Lo que
no se puede escribir no se pierde —queda pendiente con su motivo a la vista
(equipo caído, credencial rechazada) y se reintenta solo cada 5 minutos—; la
columna **En los equipos** de la lista de personas muestra ese estado.

Para empujarlo a mano hay tres botones, de menos a más:

| Botón | Dónde | Qué hace |
|---|---|---|
| **Reintentar** | fila de una persona | Reescribe **esa** persona en sus equipos. |
| **Escribir pendientes** | *Personas* | Escribe ahora lo que esté pendiente, sin esperar el reintento solo. |
| **Reenviar todo** | *Personas* | Reescribe el padrón **completo**, aunque el VMS dé los equipos por al día. |
| **Reenviar padrón** | fila de un equipo | Lo mismo, pero contra **ese** equipo. |

La diferencia entre los dos primeros y los dos últimos importa: el
sincronizador guarda una huella de lo último que cada equipo aceptó y, si no
cambió nada, no lo vuelve a molestar. Los botones de **reenviar** borran esa
huella a propósito. Es lo que hace falta cuando el equipo perdió el padrón sin
que el VMS se entere: se reemplazó el terminal, se lo volvió a fábrica, o
alguien le borró personas desde su pantalla. En las marcas que no aceptan el
padrón (Dahua, ZKTeco) el botón por equipo no aparece.

Todo queda en la bitácora (categoría *Control de acceso*): altas y bajas de
equipos, órdenes sobre puertas, cambios de horarios, niveles y personas, y las
consultas al historial. Los accesos en sí **no** van a la bitácora —esa
registra lo que hacen los usuarios del VMS, y esto es lo que hace la gente
frente a una puerta—: su registro es el historial del módulo.

**Qué acepta cada marca.** El padrón (personas, credenciales, huellas y
horarios) hoy solo lo escriben los equipos **Hikvision**; en Dahua y ZKTeco el VMS opera las
puertas y lee su historial, pero las personas se cargan en el propio equipo, y
la interfaz lo dice en vez de ofrecer algo que va a fallar. Dahua tampoco acepta
«mantener abierta» ni «bloquear» desde el VMS, y ZKTeco solo acepta la apertura
remota: esos modos se configuran en el equipo.

> Validado a medias contra hardware: la escritura del padrón en Hikvision
> (horarios, plantillas de horario y personas) está probada contra un
> **DS-K1T321MFWX** real; las rutas de puertas, eventos y biometría todavía
> salen de la documentación del fabricante. Ojo con un detalle que costó:
> **los equipos cuentan los topes de texto en bytes, no en caracteres**, así que
> un nombre con acentos entra menos de lo que parece. En los terminales con
> cámara se puede cargar además una **foto de la persona** para que entre por
> reconocimiento facial: se elige desde el mismo asistente, y el modelo lo arma
> el propio terminal, así que si la foto no le sirve lo dice al momento y el
> VMS lo explica en castellano. Los códigos de evento
> están tomados de las tablas oficiales del fabricante (274 en total: puerta,
> alarma, fallas del equipo y órdenes remotas), así que el historial se lee en
> castellano y no en números. Lo que aun así no figure se guarda como «Otro»
> con su código y su respuesta cruda a la vista, en vez de adivinarse: mostrar
> un rechazo como si fuera un acceso concedido sería peor que no traducirlo.

### Migración desde HikCentral

Para reemplazar un HikCentral Professional sin citar a nadie a reenrolarse
hay un **migrador de escritorio** (`src\TrueCentralVms.Migrator`, ejecutable
`TrueCentralMigrador.exe`, se entrega como `dist\CLRTrueCentralVMS-Migrador-<versión>.zip`;
no necesita instalación ni .NET en el equipo). Es una ventana con cuatro pasos,
barra de avance y registro del proceso, y trae al padrón de TrueCentral las
personas, fotos, tarjetas y huellas.

1. **HikCentral**: dirección del servidor y clave/secreto del socio de
   integración (HikCentral → Sistema → Integración de terceros → OpenAPI).
   Al conectar lista los departamentos —se puede migrar solo algunos— y
   completa sola la lista de terminales registrados en HCP.
2. **Terminales (huellas)**: HikCentral **no entrega las plantillas de huella
   por su OpenAPI** (en 3.1 el campo llega vacío y no existe ruta de lectura),
   pero los terminales sí las devuelven por ISAPI (`FingerPrintUpload`). El
   migrador lee de cada terminal sus personas, tarjetas y plantillas, y las
   cruza con HikCentral por legajo, tarjeta o nombre. Hace falta estar en la
   red de los terminales (en sitio o por VPN) y la clave admin de cada uno.
3. **TrueCentral**: servidor destino y usuario administrador. Se puede elegir
   un **nivel de acceso** para las personas importadas: con él, el
   sincronizador las baja a los equipos de ese nivel apenas se crean (es la
   forma de «cargar la base de HikCentral en los controles» de una vez). Se
   elige también qué identificador de empleado conservar: el **legajo que ya usan los
   terminales** (recomendado si los equipos Hikvision siguen en uso: así el
   sincronizador no duplica personas), el código de persona de HCP, o uno
   nuevo. Las personas que ya existan se omiten; una tarjeta que ya sea de
   otra persona se deja fuera y se avisa.
4. **Migrar**: guarda un **paquete** (`paquete.json` + carpeta `fotos`) como
   respaldo —sirve para importar más tarde con «Importar un paquete guardado»—
   y da de alta a las personas por la API normal, así que cada alta queda en
   la bitácora. Al terminar escribe `registro.txt` en la misma carpeta.

Lo que NO se recupera: las **claves de teclado** (ni HCP ni los terminales las
devuelven) y los **niveles de acceso** (el modelo es distinto: se asignan en
TrueCentral después de importar; hasta entonces las personas no bajan a los
equipos). Las plantillas son del formato Hikvision: sirven para terminales
Hikvision, no para ZKTeco ni Dahua. El paquete contiene datos personales y
biométricos: hay que borrarlo al terminar.

Verificado contra un HCP 3.1.0 real: 114 personas, 12 departamentos, 36
tarjetas y 107 fotos (la foto llega como `data:image/jpeg;base64,…` crudo, sin
el sobre JSON habitual). Dos cosas que costaron:

- **Las fotos de la OpenAPI son miniaturas de 135x189 px** y los DS-K1T
  rechazan el rostro por debajo de ~300 px («no pudo reconocer una cara»: 70 %
  de fallas en la prueba). El migrador amplía las fotos chicas a 400 px de lado
  corto con interpolación bicúbica antes de mandarlas: con eso el mismo
  terminal aceptó **107 de 107** rostros (padrón completo bajado a un
  DS-K1T321MFWX y un DS-K1T804AMF en unos 7 minutos).
- **HikCentral inventa tarjetas virtuales** (números cercanos a
  18446744073709551615) para enlazar las huellas de quien no tiene tarjeta, y
  las lista tanto en su OpenAPI como en los terminales. No existen físicamente:
  el migrador las descarta. En la instalación de prueba, de 36 «tarjetas» solo
  11 eran reales.
- **Los firmware viejos no informan cuántas huellas tiene la persona**: un
  DS-K1T804AMF (V1.4.0) no trae `numOfFP`, `numOfCard` ni `numOfFace` en la
  ficha. Sin ese dato el migrador consulta igual los diez dedos (los que no
  existen contestan `NoFP`), en vez de dar por hecho que no hay huellas.
- **Las huellas se piden dedo por dedo**: un DS-K1T321MFWX (V3.9.20) contesta
  solo el primer dedo si `FingerPrintUpload` va sin `fingerPrintID`, aunque la
  ficha declare cuatro. Se consulta 1..10 hasta juntar los que declara
  `numOfFP` (validado con hardware: dedos 1, 2, 6 y 7 de 512 bytes cada uno).


## Aplicaciones → Reconocimiento de patentes

Lecturas de patentes (ANPR/LPR) de las cámaras ITS, en vivo y con historial.
El reconocimiento lo hace la **cámara**: el VMS no analiza video, solo
mantiene abierto su canal de eventos, guarda lo que llega y lo reparte.

- **Fuentes.** Cualquier equipo cuyo driver soporte ANPR (hoy, Hikvision por
  HCNetSDK) se enciende como fuente desde el propio módulo (botón
  **Fuentes**, rol Admin) o desde la columna *Patentes* de `#/devices`. El
  servidor le abre el canal de alarma mientras esté en línea, lo suelta si el
  equipo se cae y lo recupera solo cuando vuelve; reintenta cada minuto tras
  un fallo y rehace la suscripción si cambian las credenciales.
- **Pantalla.** Lista de los últimos reconocimientos (miniatura de la placa,
  patente, hora del equipo y resumen del vehículo) y ficha del seleccionado
  con la escena completa, el **recuadro que marcó la cámara sobre la placa**,
  el primer plano recortado y todo lo que informó el equipo: confianza global
  y por carácter, tipo/color/marca del vehículo, velocidad, largo, carril,
  sentido, qué disparó la captura, infracción, color y tipo de placa, y lo
  observado dentro del vehículo (cinturón, teléfono, carga peligrosa).
  Con **Seguir en vivo** la ficha salta sola a cada lectura nueva.
- **Búsqueda** por patente (parcial y sin formato: `bb12` encuentra `BBBB12`)
  y por equipo, sobre el historial completo.
- **Almacenamiento.** Las lecturas van a PostgreSQL y las fotos a disco
  (`Anpr:ImageDirectory`, por omisión junto a `pgdata`, para que viajen con
  los datos). Purga automática por antigüedad y volumen
  (`Anpr:RetentionDays` 90, `Anpr:MaxEvents` 200000; 0 desactiva ese límite).
- Cada lectura se empuja a los clientes por SignalR
  (`PlateRecognized`); no hay sondeo.

> Las estructuras ITS del SDK se desempaquetan con un interop propio
> (`Interop\ItsInterop.cs`) calcado de la cabecera 6.1.9.48: las que trae el
> `CHCNetSDK.cs` heredado son de una versión anterior y dejan todos los campos
> corridos.

## Supervisor de servicios (watchdog)

El servidor lleva un watchdog interno (`Services\Supervisor\ServiceSupervisor.cs`)
que vigila la salud de cada servicio y permite controlarlos desde el panel web
(**Sistema → Servicios**; cualquier usuario ve el estado, solo un
administrador opera):

| Servicio | Tipo | Cómo se comprueba | Control |
|---|---|---|---|
| Base de datos (PostgreSQL embebido) | proceso | `SELECT 1` por Npgsql | **esencial**: solo reiniciar |
| Media server (MediaMTX) | proceso | proceso vivo **y** su API de control responde | iniciar / detener / reiniciar |
| Monitor de dispositivos, Contabilidad de sesiones, Paneles de alarma, Receptor SIA DC-09, Parlantes IP, Reconocimiento de patentes, Automatizaciones, Retención de la bitácora | interno | la tarea del `BackgroundService` sigue viva (no terminó ni falló) | iniciar / detener / reiniciar |

Reglas:

- Cada `Supervisor:CheckSeconds` (10 s) se comprueba todo. Un servicio que
  cae pasa a **Caído** y, con su auto-reinicio activo, se reinicia solo con
  esperas crecientes (5, 10, 20, 40, 60 s) hasta agotar
  `Supervisor:MaxRestartAttempts` (5) dentro de
  `Supervisor:RestartWindowMinutes` (10); tras ese tiempo estable el contador
  vuelve a cero.
- Un servicio **detenido a mano queda detenido**: el watchdog no lo toca hasta
  que un administrador lo inicie. El auto-reinicio se puede apagar por
  servicio (en memoria: vuelve a "activo" al reiniciar el servidor).
- MediaMTX conserva su relanzamiento propio ante una caída del proceso; el
  supervisor lo muestra "Iniciando" mientras esa reposición está pendiente en
  vez de lanzar un segundo arranque encima.
- Todo cambio de estado se empuja por el hub (`ServiceStateChanged`) y queda
  en la bitácora (categoría **Sistema**): `service-failed` y los
  `service-restarted` automáticos los firma `sistema`; las órdenes manuales
  (`service-started`, `service-stopped`, `service-restarted`,
  `service-autorestart-changed`, `server-restart-requested`) llevan el
  administrador, la IP y el origen.
- **Reiniciar servidor completo** (solo instalado como servicio de Windows):
  el servidor audita la orden y lanza un auxiliar `powershell.exe` fuera de su
  job que ejecuta `Restart-Service CLRTrueCentralVMS`. En modo consola el
  botón queda deshabilitado.

Para agregar un servicio al supervisor basta con implementar
`IManagedService` (o envolver un `BackgroundService` con
`HostedServiceAdapter`) y agregarlo al catálogo de `ServiceSupervisor`;
los `BackgroundService` supervisados se registran como singleton +
`AddHostedService(sp => sp.GetRequiredService<T>())` para que el supervisor
tome la MISMA instancia que arranca el host.

API (`/api/system/...`):

```
GET  /api/system/services                        estado del servidor y de cada servicio (usuario)
POST /api/system/services/{id}/start|stop|restart  orden sobre un servicio (administrador)
PUT  /api/system/services/{id}/auto-restart      { "enabled": bool } (administrador)
POST /api/system/restart                         reinicio completo del servicio de Windows (administrador)
```

El watchdog vive dentro del proceso del servidor, así que no puede
recuperarlo si el proceso entero muere. Para eso se configuran las acciones
de recuperación del SCM al instalar el servicio de Windows (una sola vez,
como administrador):

```powershell
sc.exe failure CLRTrueCentralVMS reset= 86400 actions= restart/5000/restart/15000/restart/60000
sc.exe failureflag CLRTrueCentralVMS 1
```

### Watchdog de escritorio (`src\TrueCentralVms.Watchdog`)

La capa de AFUERA: una ventana WPF que se instala junto al servidor
(`{app}\watchdog`, con acceso directo en el menú Inicio y —si se marca la
tarea— en el escritorio) y que no vive dentro del proceso vigilado. Muestra
tres indicadores —servicio de Windows, API/panel web (`/api/health`) y
PostgreSQL embebido (conexión TCP a su puerto)—, un resumen del estado
general, el registro de actividad de la sesión y los botones **Iniciar
servicio** / **Detener servicio** / **Abrir panel web**.

- Sondea cada 2 s. Si el servicio se detiene **sin que nadie se lo haya
  pedido desde ahí**, lo marca en rojo y vuelca los errores recientes del
  Visor de eventos de Windows relacionados con el servidor: es lo primero que
  se pregunta en un llamado de soporte.
- Lee los puertos de la instalación que tiene al lado con la misma precedencia
  que el servidor (`appsettings.json` < `.Production.json` < `.Local.json`), y
  el `postgresql.conf` del clúster manda sobre el puerto configurado. Así
  sigue sirviendo en un equipo que quedó con puertos distintos a los de
  fábrica.
- Sondea la API por `127.0.0.1` y no por `localhost`: con el servidor atado
  solo a IPv4, resolver `::1` primero se comería el timeout y daría por caída
  una API perfectamente sana.
- Pide elevación en su manifiesto (`requireAdministrator`): controlar el
  servicio la exige, y así el aviso de UAC aparece UNA vez, al abrirlo.
- En un equipo de desarrollo —servidor corriendo como consola, sin servicio
  instalado— informa "Servidor en ejecución (sin servicio de Windows)" con la
  API y la base en verde, en vez de dar el sistema por caído.

Cada apagado ordenado del servidor (lo pida el Watchdog, `services.msc` o el
SCM al apagar el equipo) queda en la bitácora como `server-stopped`, al lado
del `server-started` de cada arranque.

## Panel web

`#/` dashboard (salud, dispositivos, sesiones activas) · `#/devices`
mantenedor con wizard "Probar conexión", canales, revalidación, snapshots y
descubrimiento SADP · `#/decoders` decodificadores de muro · `#/walls` muros
de video (estructura y operación) · `#/workflows` automatizaciones (disparadores, acciones,
historial de ejecuciones, correo saliente y sonidos) ·
`#/access` control de acceso (equipos y descubrimiento SADP filtrado) ·
`#/access-monitor` puertas en vivo · `#/access-persons` padrón ·
`#/access-levels` niveles de acceso · `#/access-schedules` horarios ·
`#/access-events` historial de accesos ·
`#/sessions` sesiones de video en vivo con **Expulsar**
(corta la sesión RTSP en MediaMTX; el espectador puede reconectarse — no
bloquea la cuenta) · `#/users` mantenedor de usuarios.

Cada canal tiene además la casilla **Proxy** (mantenedor de canales): para
cámaras que anuncian mal su audio (SDP inválido que MediaMTX rechaza con
"media N config is missing", p. ej. `a=fmtp` apuntando a otro payload). Con
la casilla marcada, MediaMTX lanza **FFmpeg bajo demanda**: FFmpeg tolera ese
SDP, pull-ea la cámara y republica solo el video (sin audio) en la misma
ruta; la publicación se autoriza por loopback + secreto por arranque. Caso
real: los canales 3–10 del NVR CIAPCO.

## Seguridad de cuentas

- Política de contraseñas compartida (8+ caracteres, mayúscula, minúscula,
  número y especial) + historial que impide reutilizar claves + caducidad
  configurable (`Security:PasswordMaxAgeDays`, 0 = off).
- Tokens de API opacos (12 h). Credenciales de equipos cifradas con
  AES-256-GCM (llave local junto a pgdata). Las credenciales solo existen en
  claro dentro de `mediamtx.runtime.yml`, generado en runtime y git-ignored.

## Licenciamiento

El VMS se licencia contra el **servidor central de licencias de CLRobotics**
(`license_service_server`, producto `truecentral`). Modelo comercial:

| Nivel | Cómo se vende | Cómo lo aplica el VMS |
|---|---|---|
| **Base** | Una licencia base por instalación (código `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`), vinculada al equipo (`hardware_id`) | Sin licencia base vigente el sistema entra en **modo restringido** |
| **Módulo** | Característica booleana: `module_video`, `module_playback`, `module_anpr`, `module_alarms`, `module_access`, `module_videowall`, `module_speakers`, `module_automation` | Las altas del módulo se rechazan con HTTP 402 si no está incluido |
| **Canal / cupo** | Característica entera: `video_channels`, `anpr_channels`, `alarm_panels`, `access_doors`, `videowalls`, `videowall_decoders`, `speaker_channels`, `automation_rules`, `max_users`, `max_client_sessions` | Cuenta los elementos **habilitados**; al llegar al cupo no se puede habilitar más (los canales de un equipo nuevo que no caben entran deshabilitados) |
| **Expansión** | Licencia `ADDON` colgada de la base (packs "8 canales", "1 decodificador", ...) | Se suma sola en la siguiente revalidación en línea o al importar el `.lic` regenerado |

Las claves viven en `Core\Contracts\LicenseDtos.cs` (`LicenseFeatures`) y son el
contrato con el catálogo del servidor de licencias (`manage.py seed_truecentral`).
El control de acceso cuenta **puertas** (`access_doors`), no equipos: es la
unidad que el cliente entiende y la que paga.

**Dos mecanismos de confianza** (`Server\Services\Licensing\`):

- **Archivo firmado (`.lic`, Ed25519)**: fuente de verdad local. Se verifica con
  la clave pública **compilada** en `LicensingConstants.ProductPublicKey` (la
  firma cubre los bytes exactos de `signed_payload`, así el VMS no reproduce la
  canonicalización JSON de Python). Solo en Debug se admite `Licensing:PublicKey`
  para apuntar a un servidor de desarrollo. **Antes del instalador de producción
  hay que pegar aquí la clave pública del servidor productivo.**
- **Heartbeat en línea** (licencias `ONLINE`): cada `heartbeat_interval_days`
  (firmado en el `.lic`, default 7) el servidor revalida contra `/api/v1/licenses/validate/`,
  que devuelve un `.lic` fresco (trae expansiones y revocaciones). Si no se logra,
  sigue operativo durante `grace_period_days` (default 30) avisando, y luego se
  restringe. Las licencias `OFFLINE` (redes cerradas) no requieren heartbeat: valen
  hasta `expires_at`.

**Activación**: en línea (código en Sistema → Licencia; usa `Licensing:ServerUrl`
+ `Licensing:ApiKey`) o **sin internet**: el panel genera la solicitud `.req`
(código + `hardware_id`), soporte la carga en el backoffice del servidor de
licencias (detalle de la licencia → "Activación sin conexión") y devuelve el
`.lic` que se importa. Desactivar libera el cupo del equipo (migración).

**Período de prueba**: sin licencia rigen `Licensing:TrialDays` (30) con todos
los módulos y cupos chicos (`LicensingConstants.TrialFeatures`). **Modo
restringido** (prueba vencida, licencia vencida/revocada, gracia agotada, reloj
retrocedido): la API responde 402 a toda escritura salvo login, servicios y la
propia licencia; las lecturas siguen; nada se borra. Estado y archivos en
`%ProgramData%\CLRTrueCentralVMS\license\` (junto a `pgdata`).

Endpoints: `GET /api/system/license` (estado + uso de cupos), `POST .../activate`,
`.../request`, `.../import`, `.../refresh`, `.../deactivate` (admin, auditados en la
categoría `license`). El cliente WPF muestra el aviso en la barra de estado y el
detalle en Configuración → Licencia; el cupo `max_client_sessions` cuenta máquinas
distintas con sesión de escritorio (cabecera `X-TCVMS-Client`).

## Estado (v0.1.0)

- **M1** esqueleto + PG embebido + auth + panel: ✅
- **M2** CRUD dispositivos + driver Hikvision (validado con hardware real): ✅
- **M3** MediaMTX + concesiones + cliente WPF (verificado E2E): ✅
- **M4** Dahua + ONVIF + SADP: ✅ validado con hardware real (cámara Dahua
  DH-IPC-HDW2449T-S-PRO por SDK nativo y por ONVIF).
- **M5** pulido (sesiones + kick, reconexión de celdas, audio por cuadro,
  drag & drop, divisiones iVMS, buscador, indicadores de salud, README): ✅

- **M6** reproducción remota de grabaciones (Hikvision/Dahua/ONVIF, línea de
  tiempo multicanal, velocidad, saltos y exportación a MP4): ✅ — **pendiente
  validar Dahua y ONVIF contra hardware real** (Hikvision verificado E2E).

- **M7** muro de video (módulo completo portado desde `vwcontroller`:
  decodificadores, muros, layouts, ventanas agrupadas/flotantes y proyección
  de pantalla, con las cámaras del inventario del VMS): ✅ — **pendiente
  validar contra el decodificador real** (el módulo original está verificado
  E2E contra un DS-6908UDI; aquí cambió el origen de las cámaras y el
  transporte de credenciales, no el driver).

- **M8** aplicaciones → reconocimiento de patentes (canal de eventos ANPR por
  HCNetSDK, historial con fotos, ficha completa y mantenedor de fuentes): ✅ —
  verificado contra una **DS-2CD7A26G0/P-IZS real**: canal de eventos abierto,
  estructuras ITS desempaquetadas con los tamaños exactos de la cabecera,
  imágenes de escena recuperadas y hora coincidente con el rótulo de la
  cámara. **Pendiente ver una patente leída de punta a punta**: durante las
  pruebas (de noche y con lluvia) la cámara informó `noPlate` en todas las
  pasadas.

- **M9** paneles de alarma (driver Hikvision ISAPI con digest y login de
  sesión, servicio de sondeo + canal de eventos, API auditada, módulo del
  cliente y mantenedor web): ✅ — verificado de punta a punta contra un
  **panel AX PRO simulado** (alta, armado/desarmado, alarma empujada por el
  panel, bypass, rechazos y cambios externos) y contra dos paneles reales:
  **DS-PHA64-LP (AX Hybrid PRO)** —probe, estado y sesión— y **DS-PHA64-W4M
  (línea clásica, firmware V1.3.3)** —probe, estado, bypass/restitución y
  alertStream—. Pendiente con hardware: armar/desarmar en vivo y una alarma
  real de intrusión de punta a punta.

Pendientes conocidos: transporte por SDK para equipos sin RTSP; mapas y
eventos (la arquitectura no los bloquea); foco/iris por ONVIF (servicio de
imagen).
