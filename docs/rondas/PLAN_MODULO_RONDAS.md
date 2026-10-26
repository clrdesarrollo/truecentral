# Plan: módulo «Control de rondas» en TrueCentral

Borrador de planificación (26/10/2026). Se basa en:
- lo aprendido de la app Ronda: [FUNCIONES_RONDA.md](FUNCIONES_RONDA.md);
- el protocolo del bastón: [PROTOCOLO.md](PROTOCOLO.md);
- los datos recuperados: [migracion/MIGRACION.md](migracion/MIGRACION.md);
- la arquitectura actual de TrueCentral VMS (.NET 10, servidor + PostgreSQL embebido, cliente WPF, panel web).

## 1. Qué tiene que resolver el módulo

1. **Catálogo:** puntos de control (tags fijos), llaveros de guardia y bastones.
2. **Descarga del bastón** desde un puesto, poniéndolo en hora y vaciando su memoria.
3. **Bitácora de pasos:** qué guardia pasó por qué punto y cuándo.
4. **Rutas y calendarización:** qué se debía recorrer, en qué orden y con qué tiempos.
5. **Cumplimiento:** comparar lo esperado con lo real y avisar de lo incumplido.
6. **Informes:** los cinco de Ronda, más los que faltaban (lecturas huérfanas, estado de los bastones).

## 2. Encaje con lo que ya existe en TrueCentral

| Necesidad | Pieza existente que se reutiliza |
|---|---|
| Guardias | **`AccessPerson`** del control de acceso: el guardia es una persona más; el llavero de ronda es una credencial suya |
| Dónde está cada punto | **Ubicaciones** (`ILocatable`, `LocationId`) + alcance por usuario (`UserScope`) |
| Consignas por punto | **Ficha del recurso** (`ResourceProfile`): instrucciones y cámaras asociadas |
| Días, horas y feriados | **Calendario único de feriados** (`AccessHoliday`) y el modelo de horarios por día |
| Avisos de incumplimiento | **Motor de automatizaciones**: disparador nuevo `patrol` → acciones ya existentes (alerta, correo, pantallas de alarma, relé, parlante) |
| Ver la cámara del punto al revisar un paso | Cámaras asociadas a la ficha del recurso + reproducción remota existente |
| Informes | `Services/Reports` (`PdfReportWriter`, `XlsxWriter`, `ReportTable`) |
| Auditoría ISO 27001 | `AuditCatalog` + `AuditService`, categoría nueva `patrol` |
| Permisos | `Permissions.cs` + `ApiSecurity.Require` + `PermissionScope` en el cliente |
| Licencia | `LicenseFeatures` / `LicenseCatalog`: módulo `module_patrol` + cupo de bastones |
| Hablar con el bastón | **Nuevo** proyecto `TrueCentralVms.Drivers.Patrol`, que porta `Baston.Protocol` (ya probado con el bastón real) |

## 3. Modos de funcionamiento

Requisito: **entre que el guardia termina la ronda y la información está en el servidor debe pasar el menor tiempo
posible**, no una descarga diaria.

### Modo A: base de descarga («dock»), automático

El bastón **vive en una base** (diseño propio, impresa en 3D) que lo mantiene conectado y cargando. El guardia solo lo
saca para la ronda y lo devuelve al terminar.
- **Al devolverlo** se descarga solo: se pone en hora, se suben las lecturas y se vacía.
- La información llega **segundos después de que el guardia vuelve**. Como el bastón se vacía en cada vuelta, cada
  descarga es corta: tras el borrado, la lectura del bastón real tomó segundos, frente a 4 min con la memoria llena.
- Mientras está en la base no hay lecturas nuevas, así que no hay nada que descargar en bucle.
- Que el bastón salga de la base también es un dato: **«ronda iniciada»** (hora de salida) e **«ronda sin devolver»**
  (lleva X min fuera) sirven como avisos en el tablero y en las automatizaciones.

Dónde corre el software de la base (decisión, ver §9):
- **A1. PC de la caseta + agente** (servicio de Windows). La base es solo mecánica y cable USB.
- **A2. Base autónoma con Raspberry Pi** (Zero 2 W o similar) dentro: la base es un equipo de red más, por Ethernet o
  WiFi, **sin PC en la caseta**. El agente es el mismo programa .NET compilado para Linux ARM (como el agente de
  parlantes en Pi); el CP210x funciona en Linux con el driver del kernel (`/dev/ttyUSB0`). Se puede sumar un LED o un
  zumbador para el «listo» y un sensor de presencia.

### Modo B: lectura a pedido, desde el panel web

Un usuario de la aplicación, **desde la web, no desde el cliente WPF**, pide leer un bastón. Hay dos variantes, que no se excluyen:
- **B1. Bastón en una base o agente remoto:** en `#/patrol/readers`, botones «Leer ahora» y «Leer y vaciar». El servidor
  envía la orden al agente por su WebSocket y la web muestra el progreso en vivo por el hub. Sirve para revisar
  un bastón sin esperar a que el guardia lo devuelva, o para diagnosticar.
- **B2. Bastón conectado al PC del propio usuario:** el panel web habla directo con el puerto USB mediante la
  **Web Serial API** de Chrome/Edge (exige HTTPS, que TrueCentral ya tiene). No hay que instalar nada: se conecta el
  bastón, se pulsa «Leer bastón», el navegador pide permiso para el puerto, y la página despierta, lee, sube y
  (opcionalmente) vacía. Hay que portar el protocolo a JavaScript, que es corto (§ de tramas en PROTOCOLO.md).
  Útil para supervisores en terreno o sitios sin base.

En los dos casos la lectura pasa por la misma ingesta del servidor (idempotente, con ACK antes de vaciar) y queda
auditada con el usuario que la pidió.

**Detalles de B1 (orden a la base):**
- Si el bastón **está en la base**, la orden se ejecuta al momento. Si está **fuera** (en ronda), la web lo indica y
  ofrece dejar la orden pendiente; igual se descargará solo al volver.
- Mientras corre una descarga, la base no acepta otra: la orden espera o se rechaza con aviso.
- Permiso propio: `patrol.read-remote`. «Leer y vaciar» pide además `patrol.erase`.

**Detalles de B2 (Web Serial en el navegador):**
- **Solo Chrome y Edge de escritorio.** Firefox y Safari no tienen Web Serial; la página debe detectarlo y explicarlo.
- **Contexto seguro:** la página debe cargarse por HTTPS con un certificado en el que confíe el navegador. Con el
  certificado autofirmado de TrueCentral aceptado «a mano» (aviso de seguridad), Chrome puede bloquear Web Serial.
  Hay que verificarlo; si pasa, la solución es cargar un certificado válido o instalar el autofirmado como confiable
  en ese PC.
- Si el servidor envía `Permissions-Policy`, debe permitir `serial=(self)`.
- El navegador pide elegir el puerto la primera vez (filtro por `usbVendorId 0x10C4`, `usbProductId 0xEA60`) y luego lo
  recuerda para ese sitio.
- El PC del usuario necesita el driver CP210x (Windows Update lo suele instalar solo).
- **El protocolo vive dos veces:** en C# (agente) y en JavaScript (web). Para que no diverjan, las dos versiones se
  prueban contra los mismos bloques reales capturados del bastón (`copia_baston_007406/bloques_crudos_*.txt`).
- La web **no decodifica**: sube los bloques crudos y el servidor los interpreta con el mismo código C# del agente.
  Así la lógica de registros (10 u 11 bytes, BCD, tags) existe en un solo lugar; en JS solo queda transporte y tramas.
- Se audita como una descarga más, con el usuario y el puesto (navegador).

## 3.0 Arquitectura del agente (modo A y B1)

**Objetivo (pedido explícito):** el guardia conecta el bastón y el sistema hace todo solo. Lo detecta,
lo pone en hora, sube las lecturas al servidor y vacía el bastón, sin abrir programas ni pulsar botones.

Eso descarta el cliente WPF como responsable de la descarga, porque exigiría que alguien lo tenga abierto y con sesión.
La descarga la hace un **agente en segundo plano**: un servicio de Windows que arranca con el equipo, con el mismo
patrón que `TrueCentral Speaker`:
- conexión **saliente** por WebSocket al servidor (funciona detrás de NAT/4G);
- **enrolamiento por código** desde el panel web;
- token propio guardado en el equipo.

```
 [Bastón] ─USB/CP210x─► [PC de la caseta]
                          TrueCentral Patrol Agent (servicio de Windows, sin interfaz obligatoria)
                            ├─ vigila los puertos COM (llegada del VID/PID 10C4:EA60 + sondeo)
                            ├─ Drivers.Patrol: identificar, poner hora, leer, borrar
                            ├─ cola local cifrada (si el servidor no responde)
                            └─ aviso en pantalla y sonido: «Descargando… no desconecte» / «Listo»
                                   │ WebSocket saliente (token del agente)
                                   ▼
                          [Servidor TrueCentral]
                            PatrolAgentHub        → recibe lotes y confirma (ACK)
                            PatrolIngestService   → lecturas crudas (idempotente)
                            PatrolPairing         → pasos (punto + guardia)
                            PatrolEvaluator       → cumplimiento
                            WorkflowEngine        → disparador "patrol"
                                   │ hub (SignalR)
                                   ▼
                          [Web / Cliente]  bitácora, cumplimiento, estado de bastones y agentes
```

### 3.1 Flujo automático en cada conexión

1. **Detectar:** el agente ve aparecer un puerto COM del CP210x. También sondea cada pocos segundos (ver §10:
   el puerto puede estar en el cable y no en el bastón). Despierta el bastón (`0xFE`) e identifica (`0A`).
2. **Reconocer:** lee el número del bastón (`02`). Si el servidor no lo conoce, lo registra como **pendiente de aprobación**.
3. **Poner en hora** (`05`) con la **hora del servidor**, no la del PC: un PC mal configurado no debe desordenar las rondas.
   Rechaza la operación si la hora parece imposible (anterior a la última descarga).
4. **Leer** toda la memoria (`39 01` + `03` + bloques).
5. **Subir** el lote al servidor en tramos, cada uno con su número.
6. **Esperar el ACK:** el servidor confirma solo cuando las lecturas ya están guardadas (commit en la base).
7. **Borrar** (`04`) solo tras el ACK y solo si el bastón está aprobado. Después vuelve a leer la cabecera y comprueba que quedó en 0.
8. **Avisar:** muestra un aviso breve en el PC y un sonido de «listo». Si falla, «Vuelva a conectar el bastón», y el
   servidor recibe el error para el tablero y las automatizaciones.
9. **Esperar la salida de la base:** mientras siga conectado no repite la descarga. Detecta cuándo deja de responder
   (salió a ronda: evento «ronda iniciada») y cuándo vuelve (descarga inmediata).

### 3.2 Reglas de seguridad de los datos

- **Nunca se borra sin ACK del servidor.** Si el servidor no responde:
  - el lote queda en una cola local cifrada (DPAPI de máquina) y se reintenta;
  - el bastón **no se borra** (política por omisión);
  - opción configurable: borrar tras guardar en la cola local si la memoria supera el 80 %, para no repetir lo de 2023.
- **Desconexión a mitad de la lectura:** no se borra nada. La próxima conexión vuelve a leer todo y la clave única
  `(bastón, tag, fecha-hora)` evita duplicados.
- **Bastón desconocido o pendiente:** se descarga y se guarda, pero no se borra hasta que un administrador lo apruebe
  (y la licencia tenga cupo). No se pierde nada y no se vacía un equipo ajeno.
- **Auditoría sin usuario humano:** el actor es el agente (`patrol-agent:<equipo>`) con el número del bastón,
  vía `LogSystemAsync`. Se registran descarga, borrado, puesta en hora y errores.

### 3.3 Cliente WPF y panel web

**El cliente WPF no interviene en la lectura del bastón**; solo puede mostrar estado y avisos. Toda operación a pedido
(«Leer ahora», «Leer y vaciar», Web Serial) vive en el **panel web**, que además muestra el estado de cada base,
agente y bastón: en la base o fuera, desde cuándo, última descarga, memoria y errores.
Para enrolar tags («Obtener ID») se usa la última lectura recibida del bastón: se pasa por el tag, se conecta, y en el
panel aparece como «tag nuevo sin asignar».

## 4. Modelo de datos (propuesto)

```
PatrolReader            bastón
  Id, Serial (TermNo, único), Name, DriverKey ("jwm-captor4"), LocationId,
  Enabled, LastDownloadAt, LastDownloadBy, LastClockSetAt, LastMemoryUsed, CreatedAt…

PatrolCheckpoint        punto de control  (ILocatable → ubicación, ficha, alcance)
  Id, Name, LocationId, Enabled, Notes,
  Kind ("tag" | "access"),
  TagId (hex, único)                 ← si Kind = tag: se marca con el bastón
  AccessDoorId? / AccessDeviceId?    ← si Kind = access: se marca en un terminal de acceso, en tiempo real

PatrolGuard             guardia  (lista propia en pantalla; por debajo, la misma persona del padrón)
  Id, AccessPersonId (único), Active, Notes, CreatedAt, DeactivatedAt?, DeactivatedBy?, DeactivationReason?

PatrolTag               llavero de guardia
  Id, TagId (hex, único), PatrolGuardId?, Label, Enabled, AssignedAt?, ReleasedAt?

PatrolDownload          cada descarga (lote)
  Id, ReaderId, UserId, Workstation, StartedAt, FinishedAt,
  RawCount, NewCount, DuplicateCount, ClockSet (bool), Erased (bool), Status, Error

PatrolRawRead           lectura cruda, tal cual sale del bastón (nunca se borra ni edita)
  Id, ReaderId, TagId, ReadAt (hora local del bastón), DownloadId
  UNIQUE (ReaderId, TagId, ReadAt)   ← elimina duplicados entre descargas

PatrolPass              paso = punto + guardia  (lo que muestra la bitácora)
  Id, CheckpointId, PatrolTagId?, PatrolGuardId? (ON DELETE SET NULL),
  GuardName, GuardEmployeeNo,        ← copia del nombre al momento del paso: el historial sobrevive aunque se borre la persona
  ReaderId, ReadAt,
  CheckpointReadId, GuardReadId?, Source ("reader" | "access" | "app" | "manual"),
  EditedBy?, EditedAt?, EditReason?           ← correcciones auditadas, nunca destructivas

PatrolRoute             ruta
  Id, Name, LocationId, Enabled,
  OrderMode ("strict" | "free"),     ← estricto: en secuencia; libre: cualquier orden, basta con marcar los requeridos
  StartWindowMinutes,                ← margen para empezar la ronda respecto de la hora calendarizada
  MaxDurationMinutes                 ← en modo libre: plazo total para marcar todos los requeridos
PatrolRouteStop
  RouteId, Sequence, CheckpointId,
  Required (bool),                   ← los opcionales suman información pero no hacen incumplir la ronda
  OffsetMinutes?, ToleranceMinutes?  ← solo en modo estricto (tiempo desde el inicio ± margen, como Ronda)

PatrolSchedule          calendarización  (sin guardia asignado: la puede cumplir cualquiera, con cualquier bastón)
  Id, RouteId, DaysMask, StartTime, SkipHolidays, ValidFrom/To

PatrolRun               ronda esperada (se materializa desde la calendarización)
  Id, RouteId, ScheduleId, ExpectedStart, ActualStart?, ActualEnd?, Status
    ("pending" | "in-progress" | "awaiting-data" | "complete" | "partial" | "missed"), EvaluatedAt
PatrolRunGuard          quién la hizo (puede ser más de uno si se relevaron)
  RunId, AccessPersonId?, PatrolTagId?, PassCount
PatrolRunStop
  RunId, Sequence, CheckpointId, Required, ExpectedAt?, ToleranceMinutes?, PassId?, ReaderId?,
  Status ("on-time" | "early" | "late" | "out-of-order" | "done" | "missed" | "skipped-optional")
```

### 4.1 Guardias: lista propia, identidad única

Hay que cumplir dos cosas: que los conceptos no se mezclen y que, al desvincular a un guardia, no haya que buscarlo
en cada rincón del sistema.

**En pantalla, «Guardias» es un mantenedor propio del módulo de rondas.** Muestra solo guardias, con sus llaveros y su
historial de rondas. Quien administra rondas nunca ve niveles de acceso, puertas ni horarios de acceso.

**Por debajo, cada guardia es una persona del padrón (`AccessPerson`) con el rol «guardia» (`PatrolGuard`).**
- Al crear un guardia desde Rondas se crea la persona automáticamente, **sin niveles de acceso**. Como TrueCentral
  solo envía a los terminales a las personas con niveles, un guardia que es solo guardia **nunca se carga en un terminal**.
- **Los guardias también circulan por las dependencias** con sus huellas, rostro, tarjetas y PIN, como cualquier
  persona del control de acceso. Es la misma persona: no hay dos fichas que mantener sincronizadas.
  - **Ficha del guardia con dos pestañas:** «Rondas» (llaveros e historial) y «Acceso», que reutiliza el mismo
    editor de personas del control de acceso (huellas, rostro, tarjetas, niveles). Esta última solo es visible con
    el permiso de gestionar personas, para que quien administra rondas no toque accesos sin autorización.
  - **Alta sin duplicados:** al crear un guardia se busca primero en el padrón por RUT / número de empleado. Si ya
    existe (por ejemplo, ya tenía huellas cargadas), se le agrega el rol de guardia en vez de crear otra persona.
    Al revés, desde Personas: «Marcar como guardia».
  - Con niveles de acceso asignados, el guardia sí se envía a los terminales que le correspondan, como cualquier persona.
  - **Fase 4 sale casi gratis:** el guardia podrá marcar puntos de ronda en un terminal con la misma huella o tarjeta
    con la que abre puertas.
  - **Posible mejora a verificar:** si los terminales leen la misma tecnología que los llaveros (los tags del bastón
    parecen EM4100 de 125 kHz, por su formato de 5 bytes), el llavero de ronda podría registrarse también como tarjeta
    de acceso, para que un solo llavero sirva para todo. Hay que comprobar con un terminal real qué número informa al
    leer ese llavero.
- El llavero de ronda (`PatrolTag`) es una credencial propia del módulo, **no** una tarjeta de acceso (`AccessCard`):
  no se envía a los terminales y su formato (hex de 5 bytes) es distinto.

**Desvincular (un solo botón, desde Guardias o desde Personas):**
1. Desactiva la persona → el control de acceso la quita de todos los terminales (lo que ya hace hoy al deshabilitarla).
2. Libera sus llaveros de ronda. Un llavero que se siga usando aparece en la bitácora como «llavero sin guardia»,
   y queda disponible para asignarlo a otro guardia.
3. Marca el guardia como inactivo, con fecha, motivo y quién lo hizo. Queda en la bitácora de auditoría.
4. **El historial se conserva:** sus rondas y pasos siguen en los informes con su nombre.

**Eliminar definitivamente** queda como acción aparte y excepcional, por ejemplo ante una solicitud de borrado de
datos personales. Aun así, los pasos conservan la copia del nombre (`GuardName`) o se anonimizan («Guardia
eliminado») si la solicitud lo exige. **Cambio necesario en lo existente:** hoy borrar una persona del control de
acceso es un borrado definitivo. Hay que revisar que ese borrado no rompa las referencias de rondas
(`ON DELETE SET NULL`) y ofrecer «Desactivar» como acción normal.

Diferencias importantes con Ronda:
- **Las lecturas crudas se guardan siempre.** Ronda descartaba en silencio las lecturas que no podía
  emparejar; aquí quedan visibles como «huérfanas» y se pueden reprocesar si se da de alta un tag tarde.
- **No hay duplicados**, gracias a la clave única. Sirve también si se descarga sin borrar.
- **Correcciones sin pérdida:** cambiar el guardia de un paso deja quién, cuándo y por qué, en la tabla y en la auditoría.
- **El estado `awaiting-data`:** el bastón no está en línea, y como **cualquier guardia puede hacer cualquier ronda
  con cualquier bastón**, una ronda sin pasos solo es «incumplida» cuando **todos los bastones activos** están ya
  en su base o se descargaron después del cierre de la ventana. Antes de eso, está «esperando datos», con un tope
  configurable (por ejemplo, 2 h) tras el cual se declara incumplida igual. Es el matiz que más importa para no
  llenar de falsas alarmas.
- **Puntos en terminales de acceso:** sus pasos llegan en tiempo real (el guardia se identifica con su propia
  credencial de acceso: tarjeta, huella o rostro) y no esperan ninguna descarga.

## 5. Lógica central

### 5.1 Emparejamiento (lectura → paso)
- Una lectura de punto + una lectura de llavero del mismo bastón, como mucho N segundos después,
  forman un paso. N es configurable; con los datos de 2020–2023, 30 s cubre todo (mediana 2 s).
- Un llavero leído varias veces seguidas se ignora tras el primero (en los datos hay ~3.000 casos).
- Si no hay llavero, el paso queda **sin guardia**, en lugar de perderse. Opcional: atribuirlo al último guardia identificado en ese bastón en los X minutos previos.
- Si llega un tag desconocido, queda como lectura huérfana y se ofrece «dar de alta como punto o llavero».

### 5.2 Evaluación de cumplimiento
- Un servicio de fondo crea las `PatrolRun` del día según la calendarización, saltando feriados si así se configura.
- Se evalúa tras cada descarga (solo el rango de horas que cubre), con cada paso en tiempo real de un terminal de
  acceso y periódicamente para las rondas cuya ventana ya cerró.
- **Los pasos de todos los bastones y terminales cuentan para cualquier ronda.** La ronda no tiene dueño: se
  registra quién la hizo (`PatrolRunGuard`), que puede ser más de un guardia si hubo relevo.
- **Inicio de la ronda:** el primer paso por un punto de la ruta dentro de `hora calendarizada ± StartWindowMinutes`.
  En modo estricto debe ser el de secuencia 0, como en Ronda.
- **Modo estricto:** cada parada debe marcarse en orden y dentro de `inicio + offset ± tolerancia`.
  Estados por parada: `on-time`, `early`, `late`, `out-of-order` o `missed`.
- **Modo libre:** cualquier orden. Basta con que **todos los puntos requeridos** se marquen dentro de
  `inicio + MaxDurationMinutes`. Estados por parada: `done` o `missed`; los opcionales no marcados quedan como
  `skipped-optional`.
- **Resultado de la ronda:** `complete` (todos los requeridos bien), `partial` (alguno falló) o `missed` (no empezó).
- **Dos rondas de la misma ruta solapadas** (por ejemplo, cada 30 min con duración de 40): cada paso se asigna a una
  sola ronda, la más antigua que aún lo necesite. Así un mismo paso no cumple dos rondas.

### 5.3 Avisos (disparador `patrol` en automatizaciones)
- Ronda incumplida o parcial (con detalle de las paradas).
- Paso fuera de tolerancia o fuera de orden (opcional, puede ser ruidoso).
- **Bastón sin descargar** en más de X horas.
- **Memoria del bastón sobre el 80 %:** es lo que dejó al tuyo lleno desde 2023.
- Tag desconocido detectado.

## 6. Interfaz

### Agente (PC de la caseta)
- Sin ventana obligatoria. Solo un aviso emergente y un sonido al empezar («Descargando… no desconecte») y al
  terminar («Listo, N lecturas»), o un error con una instrucción simple.
- Página local de estado opcional, como la del agente de parlantes, para el técnico que lo instala.

### Cliente WPF (operador)
- Estado de los bastones y agentes, y la vista de cumplimiento del día, en el estilo de los demás módulos.
- Avisos en vivo por el hub: «Bastón 007406 descargado: 120 lecturas, 2 rondas incumplidas».

### Panel web
- `#/patrol/checkpoints`, `#/patrol/tags`, `#/patrol/readers`: mantenedores, con ubicación y alcance.
  En bastones: aprobar pendientes, ver última descarga y memoria, forzar descarga o borrado.
- `#/patrol/agents`: alta de agentes con código de enrolamiento, estado en línea, versión y errores.
- Tags nuevos sin asignar (leídos por un bastón pero sin dar de alta): asignarlos como punto o llavero con un clic.
- `#/patrol/routes`: editor de ruta con paradas ordenables, minutos y tolerancia, y una vista previa del recorrido esperado.
- `#/patrol/schedules`: días y horas, con feriados del calendario único.
- `#/patrol/log`: bitácora con filtros. Permite corregir el guardia o anular un paso, con motivo obligatorio.
- `#/patrol/compliance`: tablero de rondas del periodo con su estado y el detalle por parada.
- `#/patrol/reports`: informes en PDF y XLSX.

### Informes
De Ronda: lista de puntos, lista de guardias, general de rondas, por guardia, por punto, frecuencias por punto
(nº de pasos y tiempo mínimo y máximo entre pasos) y cumplimiento de rutas.
Nuevos: lecturas huérfanas, estado de los bastones (última descarga, memoria, hora puesta) y resumen de cumplimiento por guardia y por ruta.

## 7. Reglas de TrueCentral (checklist)

**Auditoría** (categoría `patrol` en `AuditCatalog`):
- `reader-registered/updated/deleted`, `download`, `download-failed`, `reader-erased`, `reader-clock-set`;
- `checkpoint-*`, `tag-*`, `route-*`, `schedule-*`;
- `pass-edited`, `pass-voided`, `orphan-reprocessed`, `report-exported`.

La descarga se hace en el cliente pero se audita en el servidor, al recibir el lote.

**Permisos** (`Permissions.cs`):

| Permiso | Para qué |
|---|---|
| `patrol.view` | ver bitácora y cumplimiento |
| `patrol.download` | leer un bastón desde el navegador (Web Serial); va al rol Operador por migración |
| `patrol.read-remote` | ordenar una lectura a una base desde la web |
| `patrol.erase` | vaciar un bastón a pedido (la base automática no lo necesita: actúa como agente) |
| `patrol.edit` | corregir o anular pasos |
| `patrol.configure` | puntos, llaveros, bastones, rutas y calendarización |
| `patrol.reports` | generar informes |

Todo filtrado por **alcance de ubicación**: un operador ve las rondas de sus ubicaciones.

**Licencia:**
- `module_patrol` (booleano) habilita el módulo.
- `patrol_readers` (cupo de bastones) y `patrol_checkpoints` (cupo de puntos de control). Los puntos en terminales
  de acceso **también cuentan** como puntos.
- Paquetes de expansión sueltos (`addon_patrol_readers_N`, `addon_patrol_checkpoints_N`) y valores incluidos en las
  ediciones que corresponda. Un bastón pendiente de aprobación no ocupa cupo hasta aprobarse; al aprobarlo se aplica `Deny`.
- Subir `LicenseCatalog.Version`, cupo de prueba e importar el catálogo en el servidor de licencias.
- Ocultar el módulo (web y cliente) si la licencia no lo incluye.

## 8. Fases propuestas

| Fase | Contenido | Resultado |
|---|---|---|
| **0. Base técnica** | `Drivers.Patrol` (portar `Baston.Protocol`, interfaz `IPatrolReaderDriver`, pruebas con los bloques capturados del bastón real), decisiones de §9 | el cliente sabe hablar con el bastón |
| **1. Bitácora (MVP)** | entidades + migración; mantenedores de puntos, llaveros, bastones y agentes; **agente de la base** (detección de presencia, enrolamiento, cola local, ACK antes de borrar, eventos salida/vuelta) + instalador; **lectura a pedido desde la web** (B1 por el agente; B2 Web Serial); ingesta idempotente; emparejamiento; bitácora con correcciones; informes general, por guardia y por punto; auditoría, permisos y licencia | datos en el servidor segundos después de que vuelve el guardia |
| **1b. Base física** | prototipo de la base 3D (en paralelo, a cargo del usuario): calce del conector, carga, sensor de presencia y, si se elige A2, Pi + LED/zumbador | el guardia solo saca y devuelve el bastón |
| **2. Rutas y cumplimiento** | rutas, calendarización, `PatrolRun`/evaluador, tablero de cumplimiento, informe de cumplimiento, disparador `patrol` en automatizaciones | control real de rondas, con avisos |
| **3. Operación y migración** | importador del historial (CSV de `migracion/`) y de una base Ronda si aparece; avisos de memoria y de bastón sin descargar; informes de frecuencias, huérfanas y bastones; manual (Typst) | historial recuperado y operación sin sorpresas |
| **4. Rondas en tiempo real** | puntos de control = **terminales de acceso** ya integrados (Hikvision/ZKTeco/Dahua): el evento de acceso del guardia en ese terminal genera un paso al instante. Modelo preparado desde la fase 1 (`Kind = access`, `Source = access`). Más adelante, app móvil con NFC/QR | rondas en vivo, sin esperar la descarga; se pueden mezclar puntos de bastón y de terminal en la misma ruta |

Recomendación: hacer las fases 0 y 1 juntas para tener algo usable pronto. La 2 es la que da el valor diferencial frente a Ronda.

## 9. Decisiones a tomar

1. ~~¿Guardias = personas del control de acceso o lista aparte?~~ **Resuelto (26/10/2026): lista aparte en pantalla,
   identidad única por debajo.** Ver §4.1.
2. ~~¿Descarga desde el cliente o con un agente?~~ **Resuelto:** modo A (base con descarga automática al devolver el bastón) + modo B (lectura a pedido solo desde la web). Falta decidir:
   - ~~¿A1 o A2?~~ **Resuelto (26/10/2026): A1.** La base se conecta por USB al PC de la guardia y el agente corre ahí
     como servicio de Windows. La variante con Raspberry Pi queda como evolución futura; el agente es el mismo programa.
   - ~~Modo B: ¿B1, B2 o ambas?~~ **Resuelto (26/10/2026): ambas.** Orden de lectura a la base de la guardia desde
     la web (B1) y lectura de un bastón conectado al PC del usuario desde el navegador (B2, Web Serial).
3. ~~¿Borrado automático?~~ **Resuelto:** sí, tras el ACK del servidor. Falta decidir:
   - qué hacer si el servidor no responde y la memoria pasa del 80 % (propuesto: borrar tras guardar en la cola local, configurable);
   - si un bastón dejado conectado se vuelve a descargar cada X horas.
4. ~~Asignación~~ **Resuelto:** las rondas no se asignan; cualquier guardia, con cualquier bastón.
5. ~~Orden~~ **Resuelto:** cada ruta elige modo **estricto** (secuencia inalterable) o **libre** (cualquier orden, basta
   con marcar todos los requeridos).
6. ~~Licencia~~ **Resuelto:** licencia que habilita el módulo + cupo por bastón + cupo por punto de control.
   Falta decidir en qué ediciones va y con qué cupos incluidos.
7. ~~Terminales de acceso~~ **Resuelto:** sí, se implementan (fase 4), con el modelo preparado desde la fase 1.
   Detalle a decidir entonces: si el terminal de un punto de ronda **también abre una puerta** o solo registra
   (se puede configurar como «solo lectura» para que marcar ronda no abra nada).

## 10. Riesgos y detalles técnicos

- **Una lectura completa del bastón lleno tarda ~4 min** (siempre devuelve la memoria entera). Con descargas
  frecuentes y borrado, son segundos. La ventana de descarga debe mostrar progreso y poder cancelarse sin borrar.
- **Detectar la presencia del bastón (medido, ver «Despertar y reposo» en PROTOCOLO.md):** basta con 8192 bytes de
  despertar (1,4 s), no los 20.480 de la DLL (3,6 s). Despierto responde en 60 ms y se duerme a los 5–15 s.
  Estrategia del agente: **en la base**, identificar cada 3 s (lo mantiene despierto y detecta el retiro en ≤ 3 s);
  **fuera**, despertar + identificar cada ~5 s (detecta la vuelta en ≤ ~7 s). **No hace falta sensor de presencia**,
  aunque uno simple (microinterruptor o reed) daría detección instantánea y es opcional en el diseño de la base.
- **Conector y carga del bastón:** hay que saber qué conector tiene (USB estándar o propio), si carga por el mismo
  cable de datos y si aguanta estar siempre cargando. Esto define el diseño mecánico de la base (guía para que el
  conector calce solo al dejarlo, o pines pogo).
- **¿Dónde está el chip USB, en el bastón o en el cable?** Si está en el cable, el puerto COM existe aunque el
  bastón no esté conectado, y «aparece el puerto» no significa «llegó un bastón». Por eso el agente, además del
  evento de llegada del puerto, sondea (despertar + `0A`) cada pocos segundos los puertos CP210x sin bastón.
  **Pendiente de comprobar con el equipo real.**
- **Puerto ocupado:** si otro programa (por ejemplo, la Ronda antigua) tiene abierto el COM, el agente no puede leer.
  Debe reintentar y avisar al servidor.
- **Driver CP210x:** Windows 10/11 lo instala solo por Windows Update; si no, hay que incluirlo en el instalador del agente.
- **Varios bastones en el mismo PC:** cada puerto se atiende por separado y en paralelo.
- **Bastón cargando conectado:** no repetir la descarga en bucle (§3.1, paso 9).
- **Hora del bastón:** no hay comando para leerla, solo para ponerla. Se pone en cada descarga y se registra cuándo
  (`ClockSet`). Si el bastón pierde la hora por batería, las lecturas pueden quedar con fechas imposibles:
  validar contra la última descarga y marcarlas.
- **Zona horaria:** el bastón guarda hora local sin zona; se interpreta con la zona del servidor o del puesto (decidir una).
- **Un solo modelo probado** (Captor 4 / JWM). La DLL del fabricante soporta más modelos con otros comandos;
  el diseño con `DriverKey` permite agregarlos después.
- **Ingesta grande:** 60.000 lecturas por lote. Enviar en tramos idempotentes y con inserción masiva.
