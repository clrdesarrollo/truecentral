# Diseño: Cuadro vigía y cinta de eventos

Borrador de diseño (31/10/2026). Va de la mano con [Repetición del evento](repeticion-del-evento.md). Solo diseño: no hay código todavía. Sale de la comparación del Centro de mando con
Genetec Security Desk (cuadros que muestran solos lo que pasa y lista de eventos de la tarea de monitoreo) y
Milestone XProtect Smart Client (gestor de alarmas), con nombres y UX propios de TrueCentral.

## 1. Objetivo

Que el guardia **no tenga que buscar la cámara** cuando algo pasa. Hoy un evento llega como ventana de alarma
(si una automatización lo avisa) o como fila en una tarjeta; para verlo hay que encontrar la cámara en la lista y
soltarla en un cuadro. Con el **Cuadro vigía**, un cuadro de la grilla cambia solo a la cámara del último evento que
le interesa, con una franja que dice qué pasó, dónde y hace cuánto.

1. **Cuadro vigía** (fase 1): un tipo de cuadro nuevo del Centro de mando, configurable y guardable en tableros.
2. **Cinta de eventos** (fase 2): franja plegable al pie del Centro de mando con los eventos recientes; clic o
   arrastre lleva el evento a un cuadro.
3. **Panel web** (fase 3): lo mismo en `#/live` y tableros web.

No es una alarma ni la reemplaza: el vigía **muestra**, no exige acuse. Las alertas de automatizaciones siguen con
su ventana, su sonido y su acuse; el vigía solo las aprovecha como una fuente más.

## 2. Estado actual (lo que se reutiliza)

| Necesidad | Pieza existente |
|---|---|
| Un único punto por donde pasan los eventos | `WorkflowEngine.Publish(WorkflowTrigger)`: alarmas (`AlarmPanelService`), accesos (`AccessControlService`), video y analíticas (`VideoEventService`), patentes (`AnprService`), entradas de E/S (`IoModuleService`), parlantes, flota, rondas |
| Del evento al recurso y sus cámaras | `ResourceBriefings.ForEventAsync(db, EventRef, ct, scope)`: recurso de origen (`OriginKey`), ubicación, consignas y cámaras asociadas (puerta → cámara fija del terminal, zona → cámaras de su ficha), filtrando por el alcance del usuario |
| Mandar a cada operador solo lo suyo | `ScopedHub.SendTrimmedAsync` + `UserScope.CanViewResourceKey` |
| Un cuadro que no es solo video | `VideoCellViewModel.Panel` (`CellPanel`), `DashboardItemKinds`, opciones en `Ref` como `card-plates?devices=…`, engranaje `CellPanel.CanConfigure` |
| Guardar en tableros y última sesión | `LiveViewItem` Kind/RefId/Ref/Title, `DashboardItemOf`, `PanelFor` |
| Cambio rápido de cámara | Apertura RTSP con `AllowFindStreamInfo=false` (~0,4 s), stream secundario |

**Lo que falta:** los eventos crudos no llegan al cliente con su recurso ni sus cámaras (los `AlarmEventDto`,
`AccessEventDto`, `PlateEventDto`… traen equipo y número, no la cámara), y los **eventos de video y analíticas no
llegan al cliente en absoluto**: hoy solo alimentan las automatizaciones.

**Ojo con dos detalles del código actual:**

- `WorkflowEngine.Publish` retorna de inmediato si `Workflows:Enabled` es false: la derivación al vigía va **antes**
  de esa revisión.
- `VideoCellViewModel.OpenAsync` desecha el `Panel` (para que una página web no quede sobre la cámara): el vigía
  necesita un panel que **conviva** con el video (ver 5.2).
- Cerco y citofonía no pasan por `WorkflowEngine.Publish`; se conectan desde `CercoReceiverService` / `IntercomService`.

## 3. Arquitectura

```
 AlarmPanelService ─┐
 AccessControlSvc  ─┤                       ┌──────────────────────────────┐
 VideoEventService ─┼─► WorkflowEngine ─────┤ LiveEventFeed (nuevo)        │
 AnprService       ─┤    .Publish(trigger)  │ · filtra lo que vale mostrar │
 IoModuleService   ─┘        │              │ · antirrebote por recurso    │
 CercoReceiver ──────────────┼─────────────►│ · resuelve recurso + cámaras │
 IntercomService ────────────┘              │   (ResourceBriefings, caché) │
                                            │ · anillo de los últimos 300  │
                                            └──────────────┬───────────────┘
                                                           │ hub "LiveEvent"
                                                           │ (recortado por alcance)
                                     ┌─────────────────────┼─────────────────────┐
                                     ▼                     ▼                     ▼
                               Cuadro vigía          Cinta de eventos       Panel web (fase 3)
```

### 3.1 Servidor: `Services/LiveEventFeed`

- `Offer(WorkflowTrigger)` llamado desde `WorkflowEngine.Publish` (antes del corte por `Workflows:Enabled`) y desde
  cerco/citofonía con un `Offer(LiveEventSource)` equivalente. **No bloquea**: escribe en un `Channel` acotado y un
  lector en segundo plano hace el resto (si se llena, descarta lo más viejo de baja gravedad y lo registra).
- **Qué entra** (catálogo `LiveEventCategories`, en Core para que cliente y web filtren igual):

  | Categoría | Origen | Por omisión |
  |---|---|---|
  | `alarm` | Alarma / sensor interrumpido de panel | Sí |
  | `alarm-system` | Armado, desarmado, falla, anulación | No |
  | `access-denied` | Acceso denegado | Sí |
  | `access-granted` | Acceso concedido | No |
  | `door-alarm` | Puerta forzada / abierta demasiado tiempo / alarma de puerta | Sí |
  | `video-analytics` | Cruce de línea, intrusión, merodeo, objeto abandonado… | Sí |
  | `video-motion` | Detección de movimiento | No (inunda) |
  | `video-health` | Pérdida de video, sabotaje | Sí |
  | `plate` | Patente leída; `plate-flagged` = no autorizada o fuera del padrón (`PlateRegistryStatuses` Unauthorized/Unregistered) | Solo `plate-flagged` |
  | `fence` | Alarma de cerco | Sí |
  | `io-input` | Entrada de E/S que se activa | Sí |
  | `intercom` | Llamada de citófono | Sí |
  | `alert` | Aviso de una automatización (ya trae cámaras y recurso) | Sí |

  Fuera: estado de conexión de equipos (ya está en la tarjeta de equipos), flota y rondas (tienen su módulo).
- **Antirrebote**: un evento por (categoría, recurso) cada 10 s; los repetidos suben un contador en el evento ya
  emitido (`Repeats`), no generan otro. Movimiento: uno cada 30 s por cámara.
- **Resolución**: `ResourceBriefings.ForEventAsync` **sin** alcance (se recorta después por usuario), con caché por
  `EventRef` de 60 s que se vacía con `ConfigChanged` de recursos/ubicaciones. Así una ráfaga de eventos de la
  misma puerta no consulta la base cada vez.
- **Envío**: `ScopedHub.SendTrimmedAsync("LiveEvent", dto, trim)`: sin `CanViewResourceKey` no llega; las cámaras
  se recortan a las que el usuario ve (un evento visible sin cámaras visibles llega igual, con foto si la hay).
  Requiere además el permiso de vista en vivo.
- **Memoria, no base**: anillo de los últimos 300 eventos para `GET /api/live-events/recent?take=` (al conectar o
  reconectar, la cinta se rellena). El historial ya vive en cada módulo; el feed no persiste nada.

### 3.2 Contrato

```csharp
public sealed record LiveEventDto(
    string Id,                 // "ae:1234", "pl:88", "ve:dev3:ch2:…" — estable para deduplicar
    DateTime At,               // hora del evento (UTC)
    string Category,           // LiveEventCategories
    AlarmSeverity Severity,    // Info / Warning / Critical (mapeo por categoría; la alerta trae la suya)
    string Title,              // "Acceso denegado", "Cruce de línea", "Patente no autorizada"
    string? Detail,            // "Juan Pérez · tarjeta 1234", "ABCD12"
    string? ResourceKey,       // origen ("Door:5"); null si no se resolvió
    string? ResourceName,
    string? LocationPath,
    IReadOnlyList<int> ChannelIds,   // la principal primero, ya recortadas al usuario
    string? PhotoUrl = null,   // foto del momento (acceso, patente, analítica), por Api.mediaUrl
    long? AlertId = null,      // si es un aviso: abre su ventana de alarma
    int Repeats = 0);
```

## 4. UX del Cuadro vigía

### 4.1 Estados

```
 ┌─ EN ESPERA ───────────────────────────┐   ┌─ EVENTO ──────────────────────────────┐
 │ ◉ Vigía · Bodega y patio       ⚙  📌  │   │ ▌ACCESO DENEGADO · hace 4 s     ⚙  📌 │ ← franja del color
 │                                       │   │ ▌Puerta bodega · Bodega › Patio  +2   │   de la gravedad
 │      (cámara de reposo en vivo,       │   │                                       │
 │       o la última, atenuada)          │   │        video en vivo de la cámara     │
 │                                       │   │        principal del recurso          │
 │   Esperando eventos · 0 en cola       │   │                                       │
 │                                       │   │ [◀ cámara 1/2 ▶] [Siguiente] [Listo]  │ ← al pasar el mouse
 └───────────────────────────────────────┘   └───────────────────────────────────────┘
```

- **En espera**: muestra la *cámara de reposo* (opcional) o el último evento atenuado. El borde es neutro.
- **Evento**: franja superior con color de gravedad (los mismos tokens de las alertas), título, recurso, ubicación
  y un reloj relativo que corre («hace 4 s», «hace 1 min»). Borde que destella 2 veces y queda marcado mientras dure.
  Si el evento trae foto (acceso, patente), una miniatura en la esquina; un clic la amplía.
- **Sin cámara visible**: la franja más la foto del evento a cuadro completo, o el texto si no hay foto.
- **Cámara sin conexión**: la franja + «La cámara no responde» + foto si hay; el vigía no se queda pegado: sigue con
  la cola.

### 4.2 Cuándo cambia

- **Tiempo mínimo en pantalla** (por omisión 20 s): un evento nuevo de igual o menor gravedad espera en cola; uno de
  **mayor gravedad interrumpe**. La cola se ve como «+3» en la franja; un clic la despliega (lista corta con hora,
  título y recurso; elegir uno lo muestra).
- **Repetidos** del mismo recurso no cambian la cámara: suben el contador («×3») y reinician el reloj de permanencia.
- **No interrumpir al operador**: si el guardia está moviendo el PTZ, con zoom digital, hablando por el parlante o
  con el mouse encima del cuadro, el vigía no cambia; el evento queda en cola y la franja lo avisa («1 esperando»).
- **Fijar** (📌): congela el vigía en lo que muestra; los eventos se acumulan en la cola. Se suelta con otro clic o a
  los 5 min (para que nadie deje un vigía muerto sin darse cuenta; el tiempo es ajustable).
- **Listo**: da por visto el evento, pasa al siguiente de la cola o vuelve a espera.
- **Arrastrar** el vigía a otro cuadro deja ahí la cámara **fija** (para seguir mirándola mientras el vigía continúa).
- **Doble clic**: amplía el vigía como cualquier cuadro (sigue funcionando ampliado).
- Si el evento es un **aviso** (`AlertId`), un botón «Ver aviso» abre la ventana de alarma de siempre.

### 4.3 Varios vigías en la misma grilla

Los vigías de una misma ventana con la misma configuración forman un **grupo** que reparte la cola: el evento va al
vigía en espera; si todos están ocupados, al que muestra el evento más viejo de menor gravedad que ya cumplió su
tiempo mínimo; si ninguno, a la cola común. Así 4 vigías de 2×2 muestran los 4 últimos eventos sin pisarse
(equivalente a los cuadros en cascada de Genetec, pero sin configurar un orden a mano).

### 4.4 Configuración (engranaje del cuadro)

| Opción | Valores | Por omisión |
|---|---|---|
| Qué vigila | Casillas por categoría (tabla 3.1) | Las marcadas «Sí» |
| Dónde | Ubicaciones y/o recursos sueltos (selector con «Agregar», como en Niveles) | Todo lo que ve el usuario |
| Gravedad mínima | Informativa / Advertencia / Crítica | Advertencia |
| Tiempo mínimo en pantalla | 5–120 s | 20 s |
| Cámara de reposo | Ninguna / una cámara | Ninguna |
| Stream | Secundario / principal | Secundario (cambio más rápido) |
| Soltar «Fijar» solo | 1–30 min / nunca | 5 min |

Se guarda en `Ref` como las tarjetas: `watch?cat=alarm,access-denied&loc=12,15&res=Door:5&sev=1&hold=20&idle=33`.
El título por omisión sale de «Dónde» («Vigía · Bodega y patio»); se puede renombrar como cualquier cuadro.

### 4.5 Cómo se agrega

- En la lista de Recursos, grupo «Tarjetas» → **Vigía** (se arrastra a un cuadro como las demás tarjetas).
- Clic derecho en una **ubicación** → «Vigilar esta ubicación en el cuadro seleccionado» (el vigía queda filtrado
  a esa ubicación). Es el atajo que más se va a usar.

## 5. Cliente WPF

### 5.1 Datos

- `VmsHubClient.LiveEventReceived` (mensaje `LiveEvent`) + `ApiClient.GetRecentLiveEventsAsync`.
- `LiveEventHub` (servicio del shell, uno por cliente): mantiene los últimos 300, deduplica por `Id`, entrega a los
  vigías y a la cinta. Al reconectar el hub pide `recent` y completa el hueco.

### 5.2 El cuadro

- `DashboardItemKinds.Watch = "watch"` (Ref con opciones; `IsKnownCard` no, tipo propio).
- `WatchCellPanel : CellPanel` con **`KeepsVideo = true`**: propiedad nueva de `CellPanel` que hace que
  `VideoCellViewModel` muestre el host de Flyleaf y los botones de video aunque haya panel, y que `OpenAsync` **no**
  deseche ese panel cuando lo llama el propio vigía (un `OpenAsync` del operador, al soltar otra cámara, sí lo
  desecha: el cuadro deja de ser vigía, como hoy con una página).
- La franja, la cola y los botones van en una plantilla de `CommandCenterTemplates.xaml` superpuesta al video
  (mismo lugar que hoy la barra del cuadro).
- `WatchGroup` por `ICommandCenter` (ventana principal y cada Centro de mando extra tienen el suyo) reparte según 4.3.
- Detección de «el operador está usando el cuadro»: PTZ activo (`JoystickPtzController`/botones), `DigitalZoom`
  distinto de 1×, parlante de ese cuadro hablando, mouse encima en los últimos 3 s.
- Funciona en viñetas extraídas e instancias extra sin cambios (mismo `DataContext`).

### 5.3 Cinta de eventos (fase 2)

- Franja plegable bajo la grilla (alto ~72 px, una fila de fichas horizontales con desplazamiento), con botón en la
  barra del Centro de mando y recordada por ventana.
- Filtro rápido: «Todo lo mío» / «Lo de este tablero» (recursos y ubicaciones de los cuadros del tablero, incluidos
  los filtros de sus vigías) + gravedad mínima.
- Clic en una ficha → abre la cámara en el cuadro seleccionado; arrastre → al cuadro donde se suelta; doble clic en
  un aviso → ventana de alarma.
- Es lo que en Genetec es la lista de eventos de la tarea de monitoreo, pero horizontal y compacta para no quitarle
  alto a la grilla.

## 6. Panel web (fase 3)

`live.js` recibe `LiveEvent` por el hub que ya usa; vigía como tipo de cuadro en `boards.js` (hoy `live.js` no
actualiza tableros con cuadros no-video: se amplía para `watch`). Video por WHEP como el resto del vivo web.

## 7. Permisos, alcance, bitácora y licencia

- **Permisos**: ver eventos en vivo = permiso de vista en vivo existente; poner un vigía en un tablero compartido =
  `dashboards.manage`, en uno propio = `live.views` (los mismos de hoy). Sin permiso nuevo.
- **Alcance**: todo recortado en el servidor (evento por `CanViewResourceKey`, cámaras por `CanViewChannel`). Regla
  del proyecto: validar el recurso concreto, no su ubicación. Cambios de alcance (`ConfigChanged "scope"`) vacían la
  cinta y piden `recent` de nuevo.
- **Bitácora** (regla ISO 27001): guardar un tablero con vigías ya queda registrado (`live-views`). Se agregan
  eventos de cliente `live-watch-pinned` (fijó un vigía, con duración al soltar) y `live-watch-dismissed` (marcó
  «Listo» en un evento de gravedad Crítica), para poder responder «¿alguien vio esto?». Ver un evento no se
  registra (volumen).
- **Licencia**: **incluido en la base** (decisión del usuario, 31/10/2026), como el resto del Centro de mando. Sin
  módulo ni cupo nuevo en `LicenseFeatures`.

## 8. Rendimiento y riesgos

| Riesgo | Mitigación |
|---|---|
| Ráfagas de movimiento o de accesos | Movimiento apagado por omisión, antirrebote por recurso, contador de repetidos, canal acotado en el feed |
| Consulta a la base por cada evento | Caché de resolución por `EventRef` (60 s) |
| Un cuadro que cambia mientras el guardia lo usa | Regla de no interrumpir (4.2) + Fijar |
| Cámaras que tardan en abrir | Stream secundario por omisión; mientras abre, la franja ya muestra el evento y la foto si la hay |
| Terminal con pocas suscripciones | El feed no abre suscripciones nuevas: escucha lo que ya llega |
| Muchos clientes conectados | Un solo cálculo por evento; el recorte por usuario es el de `SendTrimmedAsync` |
| Vigía «muerto» (fijado y olvidado) | Soltar Fijar solo a los 5 min |

## 9. Pruebas

- Servidor: prueba de API/hub con usuarios demo de distinto alcance (un evento de una puerta ajena no llega; una
  cámara ajena se recorta) y de antirrebote, con los simuladores ya usados (`terminal_sim.py` con `/sim/input/N/on`,
  receptor SIA, ANPR simulado, cámara simulada con analítica).
- Cliente: servidor aislado (gfx1) + cliente de prueba (ctA) con el gancho de pruebas para leer el estado del vigía
  (los botones del cuadro no salen en UI Automation); nunca un cliente extra contra el 5090.
- Medir: evento → video visible en el vigía (objetivo < 1,5 s con stream secundario).

## 10. Fases

| Fase | Contenido | Tamaño |
|---|---|---|
| 1a | `LiveEventFeed` + `LiveEventDto` + mensaje de hub + `recent` + pruebas de alcance | Mediano |
| 1b | Cuadro vigía en el cliente (estados, cola, fijar, grupo, configuración, tableros/sesión) | Grande |
| 1c | Manual (cap. 03 «Centro de mando»), bitácora, versión | Chico |
| 2 | Cinta de eventos | Mediano |
| 3 | Vigía y cinta en el panel web | Mediano |

## 11. Estado (31/10/2026)

Fase 3 (vigía en el panel web) hecha en la rama `feat/vigia-web`: `wwwroot/live-watch.js` sobre `live.js` (el vigía
es `LvCell.watch`; abre la cámara con `lvOpenInCell(…, keepWatch)`), mensaje `LiveEvent` del hub, mismas opciones en
`card-watch?…` (tableros compartidos con el cliente y última sesión del navegador), botón «Vigía», «Vigilar esta
ubicación», engranaje y «Ver aviso» con acuse. De paso, en web y cliente: el evento desplazado por uno más grave antes
de cumplir su tiempo vuelve a la cola (antes se perdía).

Fases 1a y 1b hechas en la rama `feat/cuadro-vigia`:

- Servidor: `Services/LiveEventFeed` (derivación en `WorkflowEngine.Publish` antes del corte por `Workflows:Enabled`,
  más cerco, citofonía y la acción «Avisar»), antirrebote 10 s / 30 s, caché de resolución 60 s, anillo de 300,
  `ScopedHub.SendEachAsync` (permiso del tipo de evento + alcance + cámaras recortadas; los avisos con los mismos
  destinatarios que su ventana), `GET /api/live-events/recent`. `ResourceBriefings.EventRef` ganó `IoModuleId`.
- Cliente: `LiveEventHub`, `WatchCellPanel` + `WatchCoordinator` (reparto por grupo, cola, fijar, no interrumpir con
  zoom o recién tocado), `CellPanel.KeepsVideo` / `AttachTo` / `DetachFrom`, `VideoCellViewModel.OpenAsync(keepPanel)`
  y `StopVideo`, `WatchSettingsWindow`, tarjeta «Cuadro vigía» (`card-watch?…` en tableros y sesión), clic derecho en
  una ubicación → «Vigilar esta ubicación». Web: la tarjeta se puede poner en un tablero compartido (boards.js).
- **Diferencias con el diseño:** el filtro «Dónde» es solo por ubicaciones (sin recursos sueltos); arrastrar el vigía
  lo mueve entero (no deja una copia fija de la cámara); la foto del evento queda para después (la bitácora de
  «Fijar» y de «Listo» en eventos críticos sí está); la cinta de eventos (fase 2) y el vigía en el panel web (fase 3) siguen pendientes.

## 12. Preguntas abiertas

1. ~~Licencia~~: decidido, incluido en la base.
2. **Nombre**: «Cuadro vigía» y «Cinta de eventos» (propuestos, propios; no usar «monitoreo de cuadros» de Genetec).
3. ¿La patente leída **conocida** (en el padrón) debe poder vigilarse, o solo las no autorizadas o fuera del padrón?
4. ¿Un aviso de automatización que va dirigido a ciertos usuarios (`Recipients`) debe aparecer también en los
   vigías de los demás? Propuesta: no, respeta a quién se dirigió.
5. ¿Sonido propio del vigía? Propuesta: ninguno (los avisos ya suenan); como mucho un clic suave opcional.
