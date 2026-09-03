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
```

Panel web: `http://localhost:5090`. El sistema viene **desactivado de
fábrica**: el primer administrador se crea desde el asistente del panel, que
solo acepta conexiones desde la propia máquina del servidor.

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
  opcional, lista de usuarios recientes.

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
- **A través del Hik IP Receiver Pro** (`hikvision-iprp`): los paneles se
  registran en la pasarela de Hikvision (ISUP 5.0, OTAP o Hik-Partner Pro) y
  el VMS consume su API con la cuenta de la pasarela. En el mantenedor se
  indica la dirección del IP Receiver Pro y el **equipo dentro de ella**
  (uuid, serie, cuenta o ID ISUP; con el campo vacío, "Probar conexión"
  lista los equipos disponibles). Requisito en la pasarela: *Automation
  Output → Protocol* habilitado con tipo **Private**; sin eso su API
  contesta "Invalid operation". Estado, armado, desarmado y bypass van por
  la pasarela, y los eventos llegan por su suscripción (CIDAlarm con el
  usuario que armó/desarmó, y la conexión/desconexión del panel respecto de
  la pasarela). Además el VMS actúa como **centro receptor de
alarmas (SIA DC-09, protocolos ADM-CID y SIA-DCS por TCP, puerto 5091,
`Alarms:Receiver`)**: en el panel se configura como *Alarm Receiving Center*
con la IP del servidor y ese puerto, y el equipo reporta con confirmación
todo lo que ocurre (alarmas, armados con el usuario que los hizo,
anulaciones, fallas y pruebas periódicas). Solo se aceptan reportes desde la
dirección de un panel habilitado; el resto se rechaza y queda en la bitácora.

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
lo que ocurre y ejecuta acciones solo. Hoy dispara con los **paneles de
alarma** (cualquier evento: alarma, armado, anulación, falla…) y con la
**conexión** del servidor con un panel; el motor es genérico, así que sumar un
disparador nuevo (patentes, cámara fuera de línea) es publicar un
`WorkflowTrigger` más.

Acciones disponibles (`IWorkflowActionExecutor`, una clase cada una):

| Acción | Qué hace |
|---|---|
| **Capturar foto** | Snapshot JPEG de las cámaras elegidas (1–5 por cámara). Queda en disco y disponible para las acciones siguientes. |
| **Enviar correo** | SMTP con MailKit (STARTTLS, TLS implícito o sin cifrar), adjuntando las fotos capturadas. |
| **Subir a FTP** | FTP/FTPS (explícito o implícito) con las fotos y un informe de texto opcional; la carpeta remota admite marcas y se crea sola. |
| **Llamar a un servicio (HTTP)** | GET/POST/PUT/… a otro sistema, con cabeceras, cuerpo y autenticación básica o digest. |
| **Sonar parlante IP** | Hikvision (audio bidireccional ISAPI: el servidor le envía el sonido ya convertido a G.711 al ritmo real), Axis (`playclip.cgi`) o cualquier marca por URL. |
| **Avisar a los operadores** | Notificación en vivo por el hub a todos los conectados, **con la foto capturada** y **alarma sonora en el equipo del operador** (el sonido lo elige la automatización entre los cargados en el servidor, o el pitido del sistema). |

Los textos (asunto, cuerpo, URL, carpeta remota…) admiten **marcas** que el
servidor reemplaza con los datos del evento: `{panel}`, `{evento}`, `{tipo}`,
`{severidad}`, `{codigo}`, `{area}`, `{zona}`, `{operador}`, `{fechahora}`,
`{workflow}`, `{servidor}`… En las URL los valores se escapan.

Condiciones del disparador: paneles, tipo de evento, severidad, **áreas y
zonas elegidas por nombre** (para que cada sensor pueda disparar acciones
distintas: una automatización por zona), códigos Contact-ID, origen (panel /
sondeo / VMS), texto contenido y **ventana horaria** (días de la semana y
rango, que puede cruzar la medianoche: 22:00 a 06:00). Cada automatización
tiene además un **tiempo mínimo entre ejecuciones** (60 s por omisión): un
detector que rebota veinte veces no manda veinte correos.

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

Panel web `#/workflows`: mantenedor, historial, configuración del **servidor
de correo** y carga de **sonidos** para los parlantes (se convierten a G.711
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

## Panel web

`#/` dashboard (salud, dispositivos, sesiones activas) · `#/devices`
mantenedor con wizard "Probar conexión", canales, revalidación, snapshots y
descubrimiento SADP · `#/decoders` decodificadores de muro · `#/walls` muros
de video (estructura y operación) · `#/workflows` automatizaciones (disparadores, acciones,
historial de ejecuciones, correo saliente y sonidos) ·
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
