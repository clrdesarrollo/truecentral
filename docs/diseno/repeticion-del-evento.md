# Diseño: Repetición del evento (video antes y después)

Borrador de diseño (31/10/2026). Solo diseño: no hay código todavía. Va de la mano con el
[Cuadro vigía](cuadro-vigia.md). **Incluido en la base** (decisión del usuario, 31/10/2026): no agrega módulo ni
cupo de licencia; usa la reproducción que ya existe.

## 1. Objetivo

Cuando una automatización avisa una alarma, la ventana de alarma (el popup) muestra hoy **fotos** y **video en
vivo**. Lo que falta es lo más útil para verificar: **qué pasó en el momento**. La Repetición del evento es un clip
corto sacado de la grabación del propio equipo (DVR/NVR o tarjeta de la cámara), por omisión **5 s antes y 15 s
después** del instante del evento, que se reproduce en bucle dentro del popup, con una marca en el instante exacto.

Los segundos antes y después se pueden fijar en la acción «Avisar» o tomarse **del propio equipo** (su pregrabación
y posgrabación configuradas).

## 2. Estado actual

- **Ventana de alarma** (`Views/AlertWindow`): pestañas «Fotos», «Video en vivo» y «Qué hizo el sistema». El vivo
  abre las `ChannelIds` del aviso en una grilla de `VideoCellViewModel` con stream secundario.
- **Reproducción**: `POST /api/playback/request` (`PlaybackRequestDto(DeviceId, RtspChannel, StartLocal, EndLocal,
  Speed)`) arma una ruta temporal en MediaMTX hacia la URL de reproducción del fabricante
  (`IDeviceDriver.ResolvePlaybackUrlAsync`) con permiso `playback.view`, alcance por canal y bitácora.
  `GET /api/playback/{dev}/{ch}/segments` lista los tramos grabados de un día (`QueryRecordingsAsync`). Las horas
  van en **hora local del equipo**.
- **Descarga**: `/api/playback/{dev}/{ch}/download` (permiso `playback.export`), Centro de descargas del cliente.
- **Aviso** (`WorkflowAlert`): guarda `RaisedAt` (UTC, cuando se creó el aviso) y `ChannelIdsJson`, pero **no la
  hora del evento** según el equipo. El disparo (`WorkflowTrigger.At`) usa la hora del servidor al recibirlo; la
  hora del equipo va solo como texto en la marca `{horaequipo}`.
- **Pregrabación en el equipo**: el SDK de Hikvision la expone en `NET_DVR_RECORD_V30` (`dwPreRecordTime`:
  0 = sin pregrabación, 1 = 5 s … 6 = 30 s, 7 = máxima; `dwRecordTime` = posgrabación: 0 = 5 s, 1 = 20 s, 2 = 30 s,
  3 = 1 min…). Ya está declarada en `CHCNetSDK.cs`, sin usar.

## 3. Lo difícil y cómo se resuelve

### 3.1 La hora: usar el reloj del equipo, no el del servidor

El clip se pide en la hora del grabador. Si se calcula desde la hora del servidor, cualquier diferencia de reloj
(o el retraso con que llega el evento: ISUP, alertStream, sondeo) corre el clip y el instante queda fuera. Por eso:

- El disparo lleva una **hora del evento según el equipo** (`EventAtDevice`, hora local del equipo) cuando el
  evento la trae: analíticas (`DeviceEvent.At`), patentes (`CapturedAt`, que ya es hora local del equipo), accesos
  (`Timestamp`), paneles. Si el evento no la trae, se usa la hora del servidor al recibirlo y el aviso lo marca
  como «hora aproximada».
- Si el evento viene de otro equipo que la cámara (una zona de alarma con la cámara de su ficha, una puerta con su
  cámara), se usa la hora del servidor convertida, y la supervisión de hora de equipos (Sistema → Hora) es la que
  mantiene los relojes alineados. Se agrega 1 s de margen por cada lado en ese caso.

### 3.2 El después todavía no está grabado

El popup se abre ~1-2 s después del evento: los 15 s posteriores **aún no existen** y los grabadores, además,
tardan en dejar disponible lo recién grabado (índice y escritura en bloques; en Hikvision suele ser de 5 a 30 s).
Pedir el clip apenas se abre la ventana da un video cortado o un error.

Se resuelve en dos tiempos:

1. **Mientras se graba**: la pestaña muestra el vivo de esa cámara con una barra de progreso «El grabador está
   guardando el después del evento… 0:12» (cuenta regresiva hasta evento + después + margen del equipo).
2. **Clip listo**: el servidor consulta los tramos (`QueryRecordingsAsync` del día) hasta que la grabación cubra
   `[evento − antes, evento + después]` o se agote un tope (90 s). Recién ahí da la concesión de reproducción y el
   cliente cambia solo a la repetición en bucle.

Si al cumplirse el tope la grabación cubre solo una parte, se reproduce lo que haya y se dice «El equipo no grabó
los 15 s posteriores completos (hay 9 s)». Si no hay nada: «El equipo no tiene grabación de ese momento» con la
causa probable (sin grabación continua ni por evento, disco lleno, cámara grabando solo en otro horario).

El margen por modelo se aprende: el servidor guarda, por equipo, cuánto tardó la grabación en estar disponible
las últimas veces (`Device.RecordingLagSeconds`, promedio móvil) y lo usa para la siguiente cuenta regresiva.

### 3.3 La cámara la graba otro equipo

La reproducción se pide al equipo donde vive el canal (el NVR para sus canales; la cámara misma si está agregada
directo con tarjeta). Si una cámara está agregada directo **y** la graba un NVR, hoy TrueCentral no sabe que existe
esa grabación. Fase 2: «Grabación en…» por canal (elegir el canal del NVR que la graba, con sugerencia por IP como
ya hace el mapa IP→canal de los eventos de video de DVR).

## 4. Cuántos segundos: configuración

Prioridad, de lo más específico a lo más general:

1. **La acción «Avisar»** de la automatización: nueva sección «Repetición del evento» con
   - Activada (casilla, encendida por omisión cuando el aviso tiene cámaras).
   - Antes: `Según el equipo` | 0-60 s. Después: `Según el equipo` | 5-120 s.
2. **Según el equipo**: pregrabación y posgrabación configuradas en el grabador para ese canal, leídas por el driver
   (`IDeviceDriver.ReadRecordWindowAsync(info, channel)` → `(pre, post)`; Hikvision por `NET_DVR_GET_RECORDCFG_V30`,
   Dahua por su configuración de grabación —a verificar con equipo real—, ONVIF sin dato). Se guardan por canal
   (`Channel.PreRecordSeconds`/`PostRecordSeconds`) con la revalidación del equipo, no en cada aviso.
3. **Valor del sistema** (Sistema → Video): 5 s antes y 15 s después.

«Según el equipo» con un equipo que no lo informa cae al valor del sistema, y el aviso lo dice en la pestaña
(«5 s antes y 15 s después, valor del sistema: el equipo no informa su pregrabación»). Topes: antes ≤ 60 s,
después ≤ 120 s, clip ≤ 3 min.

## 5. UX en la ventana de alarma

```
 ┌ Fotos │ Repetición del evento │ Video en vivo │ Qué hizo el sistema ┐
 │                                                                     │
 │            clip en bucle (cámara principal del aviso)               │
 │                                                                     │
 │ ▶ ⏸  ↺   ├───────────●─────────────────────────────────────┤ 0:07   │
 │            −5 s      ▲ 14:32:08 (instante del evento)      +15 s    │
 │ [Cámara 1 ▾]  [½× 1× 2×]  [Abrir en Reproducción]  [Guardar clip]   │
 └─────────────────────────────────────────────────────────────────────┘
```

- **Pestaña nueva «Repetición del evento»**, primera cuando el aviso tiene cámaras: lo primero que el guardia ve es
  qué pasó; las fotos y el vivo siguen a un clic. Si el aviso no tiene cámaras, la pestaña no aparece.
- **Bucle** automático, sin audio por omisión; la línea de tiempo marca el instante del evento (rojo) y se puede
  arrastrar; velocidades ½×, 1× y 2×; ↺ vuelve al inicio.
- **Varias cámaras**: selector «Cámara 1 ▾» (la principal primero); cada una se prepara al elegirla, para no pedir
  N reproducciones juntas al grabador.
- **Abrir en Reproducción**: abre el módulo de Reproducción en esa cámara y ese minuto, para ver más.
- **Guardar clip**: descarga ese rango por el Centro de descargas (requiere `playback.export`; sin él, el botón no
  aparece).
- **Sin permiso de reproducción** (`playback.view`): la pestaña dice «Sus roles no incluyen ver grabaciones» y el
  vivo sigue disponible. Fuera de alcance: no se ofrece la cámara (ya pasa con el vivo).
- **Avisos viejos** (Centro de eventos → abrir uno de ayer): el clip se pide directo, sin cuenta regresiva.
- **Cola de avisos**: si llegan varios, cada uno prepara su clip solo cuando se muestra (no se pide al grabador por
  avisos que nadie está mirando).

### Pantallas de alarma y panel web

- **Panel web** (ventana de aviso de la web): misma pestaña, por WHEP (`/api/playback/webrtc`). Fase 2.
- **Pantallas de alarma de las casetas** (Pi): no; muestran foto y texto, y reproducir grabaciones exige permiso
  de un usuario. Se puede reconsiderar con el kiosco.
- **Cuadro vigía**: botón «Repetir» en la franja del evento que abre el mismo clip dentro del cuadro (antes y
  después del valor del sistema). Comparte el servicio de preparación del clip.

## 6. Servidor

- **Disparo y aviso**: `WorkflowTrigger.EventAtDevice` (DateTime? local del equipo) + `EventTimeExact` (bool);
  `WorkflowAlert.EventAtDevice`, `ClipPreSeconds`, `ClipPostSeconds` (null = sin repetición) y su DTO. Migración
  nueva; avisos viejos sin estos datos usan `RaisedAt` como hora aproximada.
- **`ClipService`**: `GET /api/workflows/alerts/{id}/clip?channelId=` devuelve el estado
  (`recording` con segundos restantes | `ready` con rango exacto | `partial` | `missing` + motivo). Consulta los
  tramos por el driver con caché de 5 s por canal, para que varios operadores mirando el mismo aviso no multipliquen
  las consultas al grabador. Permiso `playback.view` + alcance del canal + ver el aviso (`ReceivesAlert`).
- **Reproducción**: el cliente pide la concesión normal (`/api/playback/request`) con el rango que devolvió el
  servicio: no hay ruta nueva de video. La bitácora de reproducción agrega «desde el aviso #N» en el detalle (el
  aviso viaja como dato opcional en la petición).
- **Lectura de la ventana del equipo**: `ReadRecordWindowAsync` en `IDeviceDriver` (por omisión null). Se llama en
  el alta/revalidación del equipo y una vez por ejecución en el `DeviceStatusMonitor`, como las partes del equipo.

## 7. Cliente WPF

- `AlertWindow`: pestaña `ReplayTab` con un `PlaybackCellViewModel` (el de Reproducción) en modo clip: rango fijo,
  bucle, marca del instante, velocidades. Se reutiliza `TimelineBar` acotada al clip.
- Estado `recording`: vivo de la cámara + cuenta regresiva; consulta `.../clip` cada 3 s; al pasar a `ready` cambia
  sola a la repetición (sin robar la pestaña si el guardia está en otra).
- Al cerrar o cambiar de aviso se suelta la reproducción (libera la sesión en el grabador).

## 8. Bitácora y licencia

- Bitácora: la reproducción ya queda registrada («Reprodujo grabaciones de…»), ahora con el número de aviso.
  Guardar el clip queda como cualquier descarga. Cambiar los segundos en la acción «Avisar» entra en la bitácora de
  automatizaciones que ya existe. Sin acciones nuevas en `AuditCatalog` salvo `workflows/alert-clip-missing`
  (el equipo no tenía grabación del momento), útil para detectar cámaras que no graban.
- Licencia: incluido en la base. Usa la reproducción (`module_playback`); si un cliente no tiene reproducción
  licenciada, la pestaña no aparece.

## 9. Riesgos

| Riesgo | Mitigación |
|---|---|
| Reloj del equipo corrido | Hora del evento del propio equipo cuando la trae; supervisión de hora; margen de 1 s |
| Grabador lento en dejar disponible lo reciente | Cuenta regresiva aprendida por equipo, consulta de tramos antes de reproducir, tope de 90 s |
| La cámara no graba (sin disco, horario, solo por evento sin pregrabación) | Mensaje con la causa probable + `alert-clip-missing` en la bitácora |
| Sesiones de reproducción limitadas en el grabador | Una cámara a la vez en el popup; solo el aviso a la vista; se suelta al cerrar |
| Varios operadores con el mismo aviso | Caché de tramos por canal en el servidor |
| Firmware que corre el rango de búsqueda (ya visto en terminales con horario de verano) | Pedir con margen y recortar por la hora del tramo devuelto |

## 10. Pruebas

- Cámara simulada con grabación: MediaMTX de prueba con `record` y un driver falso de reproducción que sirva los
  tramos con un retraso configurable (simula el retraso del grabador).
- Hardware real: DVR Jemo (.233) y NVR del 5090 (sus canales graban continuo) disparando un aviso por entrada de
  alarma o analítica; medir el retraso real de disponibilidad y verificar que el instante cae en la marca.
- Arnés de API: estados `recording → ready`, `partial`, `missing`; permisos (sin `playback.view`, fuera de alcance).

## 11. Fases

| Fase | Contenido | Tamaño |
|---|---|---|
| 1 | Hora del evento en disparos y avisos, `ClipService`, pestaña en `AlertWindow`, configuración en «Avisar» y en Sistema | Mediano |
| 2 | «Según el equipo» (lectura Hikvision/Dahua), retraso aprendido, panel web, botón Repetir del vigía | Mediano |
| 3 | «Grabación en…» para cámaras directas grabadas por un NVR | Chico |
| — | Con el módulo de Grabación ([plan](grabacion-y-evidencias.md)), el clip sale del servidor al instante y la cuenta regresiva desaparece; una «evidencia» puede crearse desde el clip | — |

## 12. Estado (31/10/2026)

Fase 1 hecha en la rama `feat/repeticion-evento`:

- Hora del evento en el disparo (`WorkflowTrigger.DeviceAt`, del reloj del equipo en analíticas y patentes) y en el
  aviso (`EventAt`, `EventDeviceId`, `EventDeviceAt`, `ClipPreSeconds`, `ClipPostSeconds`; migración
  `AlertEventClip`).
- `Services/AlertClips` + `GET /api/workflows/alerts/{id}/clip?channelId=`: espera a que el reloj del equipo pase el
  final del clip + 5 s, luego consulta los tramos (caché 5 s) hasta cubrirlo o 90 s; `partial`/`missing` con motivo y
  `workflows/alert-clip-missing` en la bitácora. La reproducción lleva `AlertId` y la bitácora dice «repetición del
  aviso N».
- Acción «Avisar»: «Repetición del evento» (encendida por omisión) y segundos antes/después (`Valor del servidor` o
  fijos). **Diferencia con el diseño:** el valor del servidor va en `appsettings.json` (`Alerts:ClipPreSeconds` /
  `ClipPostSeconds`, 5/15) en vez de una página de Sistema; la página queda para la fase 2 junto con «Según el
  equipo».
- Cliente: pestaña «Repetición del evento» (primera), vivo + cuenta regresiva mientras graba, bucle, marca del
  instante, ½×/1×/2×, «Abrir en Reproducción» y «Guardar clip».
- Probado con el DVR CLRobotics real (iDS-7232HQHI-M2/S) en un servidor aislado con copia de la base: API 14/14,
  clip listo a los ~17 s de un evento con 5 s + 10 s, 15,8 s de video entregados.

Fase 2 hecha en la rama `feat/repeticion-fase2` (sin migraciones):

- «Según el equipo»: `IDeviceDriver.ReadRecordWindowAsync` (Hikvision por `NET_DVR_GET_RECORDCFG_V30`, campos por
  `Marshal.OffsetOf`; Dahua y ONVIF sin dato → valen los del servidor). La acción «Avisar» acepta `device` en
  `clipPre`/`clipPost`; el aviso guarda -1 y el servidor lo resuelve por cámara al pedir el clip (caché de 1 h).
  Probado con el DVR CLRobotics: 5 s de pregrabación y 30 s de posgrabación.
- Espera aprendida: lo que tarda cada grabador desde el final del clip hasta tenerlo (promedio móvil 0,7/0,3, solo
  con clips por los que se esperó), en SystemBlobs `alerts.clip.lag`; se consulta al grabador desde el final + 3 s y
  la cuenta regresiva usa lo aprendido.
- Valores del servidor editables: SystemBlobs `alerts.clip` + `GET/PUT /api/workflows/clip-settings` (bitácora
  `workflows/alert-clip-settings-updated`), botón «Repetición del evento» en Automatizaciones; `appsettings` `Alerts:`
  queda como valor inicial.
- Panel web: pestaña «Repetición» en la ficha del aviso del Centro de eventos (PbSession/PbMseSession, bucle, marca).
- Vigía del cliente: botón «Repetir» (eventos que no son avisos) que abre Reproducción unos segundos antes.

Queda: «según el equipo» para Dahua, «Repetir» en el vigía web, repetición en las pantallas de alarma (no previsto).

## 13. Preguntas abiertas

1. ¿La pestaña «Repetición del evento» va **primera** (propuesta) o después de «Fotos»?
2. ¿Audio en el clip apagado por omisión (propuesta) o encendido?
3. ¿Además del popup, el **Centro de eventos** debe mostrar la repetición al abrir avisos viejos? (Propuesta: sí,
   viene gratis.)
