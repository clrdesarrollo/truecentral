# Migración de control de acceso: HikCentral → TrueCentral

**Corte previsto:** viernes 30-10-2026 · **Piloto:** Sala Técnica (192.168.1.196) · **Borrador:** 28-10-2026

Alcance: 12 terminales Hikvision (6 DS-K1T321MFWX y 6 DS-K1T805MBFWX), ~114 personas con
rostro, huella, clave (PIN) y algunas con tarjeta. Además, la solución de casino.

---

## 1. Resumen

- **El viernes se migra el control de acceso. El casino se migra en un segundo paso, la semana
  siguiente.** El kiosco del casino NO lee las marcaciones desde HikCentral: las recibe directo de
  los terminales. De HikCentral solo toma el **padrón**: nombre, empresa, foto y el campo
  personalizado «Colacion». Si HikCentral sigue encendido y congelado, el casino sigue funcionando
  igual después del corte.
- **La llave de todo es el `employeeNo` del terminal, que es el `personCode` de HikCentral.**
  - El kiosco usa `employee_no` como llave primaria.
  - El backend Django tiene `unique(station, employee_no)`.
  - TrueCentral lo guarda como `EmployeeNo`.
  - El migrador lo conserva (opción «legajo de los terminales»). Esa opción NO se cambia.
- **El migrador actual tiene cuatro brechas que hay que cerrar antes del viernes** (detalle en la
  sección 2.3):
  - no migra las claves;
  - le asigna el mismo nivel a todos;
  - pierde «Colacion»;
  - conviene revisar la calidad de los rostros.
- **HikCentral no se apaga el viernes.** Queda encendido, congelado y conectado a los terminales.
  Es el camino de vuelta y sigue siendo la fuente del padrón del casino.

---

## 2. Hallazgos que definen el plan

### 2.1 La identidad de la persona

| Dónde | Campo | Valor |
|---|---|---|
| Terminal Hikvision | `employeeNo` | `personCode` de HCP (verificado: 108 de 108 personas cruzadas por legajo el 10-09) |
| HikCentral | `personCode` / `personId` | `personCode` es el legajo y `personId` es un id interno de HCP |
| Kiosco casino (SQLite) | `persons.employee_no` (PK) | `personCode` (o `personId` si el código viene vacío) |
| Backend casino (Django) | `Person.employee_no` + `unique(station, employee_no)` | ídem |
| TrueCentral | `AccessPerson.EmployeeNo` | lo que traen los terminales. **No se reasigna nunca.** |

**Reglas:**
- Al importar, `EmployeeNoSource = Terminal`. Nunca «lo asigna TrueCentral».
- Una persona nueva en TrueCentral recibe un `EmployeeNo` nuevo. Hasta que el casino lea desde
  TrueCentral, esa persona **no existe para el casino** (ver 7.4).
- El `personId` de HCP no es llave en el casino: solo se guarda como dato. Conviene conservarlo en
  TrueCentral como referencia externa (está en el paquete como `HcpPersonId`).

### 2.2 Cómo usa el casino a HikCentral

Lo que sigue es la lectura de los repos `integracion_casino` (kiosco CasinoAccess) e
`integracion_casino_backend`.

**Marcaciones: no dependen de HCP.**
- El kiosco arma los terminales por HCNetSDK (puerto 8000) y por `alertStream` ISAPI (solo en los
  equipos con «Facial con botón de llamada»).
- No recupera marcaciones perdidas: lo que se pierde se ingresa a mano.

**Padrón: sí depende de HCP.**
- Usa `person/personList`, `org/orgList` y `person/picture_data`, con firma HMAC antigua.
- Lo lee al arrancar, cada 30 minutos sin turno abierto y antes de abrir cada turno.
- Si HCP no responde, sigue con su padrón local.

**El kiosco ESCRIBE en los terminales:**
- `UserInfo/Modify` para habilitar o deshabilitar personas: `Valid.enable` y la vigencia
  2020→2037.
- «Solo tarjeta» para visitas (`userVerifyMode=card`).
- Planes de verificación (`CardReaderPlan`, `VerifyPlanTemplate/2`) y `AcsCfg.voicePrompt`.

**Del padrón usa:**
- `personCode`, `personId` y `personName`;
- `orgName` (el departamento HOJA, como «empresa»), que decide la autorización por turno;
- `customFieldList` → **«Colacion»** (0 = sin colación, 1 = solo almuerzo, 2 = todos los turnos);
- la foto.

**El backend Django no habla nunca con HCP.** Recibe todo del kiosco por `/api/sync/`.

### 2.3 Brechas del migrador actual (`src/TrueCentralVms.Migrator`)

| # | Dato | Hoy | Riesgo si no se corrige | Arreglo | ¿Antes del viernes? |
|---|---|---|---|---|---|
| M1 | **Clave (PIN)** | No se migra («hay que volver a definirlas») | Se pierden todas las claves | La OpenAPI de HCP 3.1 no la entrega, pero `UserInfo/Search` del terminal la devuelve en texto plano (verificado en laboratorio). Leer `password` en `TerminalReader` → `PackagePerson.Pin` → `PinCode` al importar. Avisar si dos terminales tienen claves distintas. | **Sí** |
| M2 | **Niveles de acceso** | Un solo nivel para todos (o ninguno) | Con «Full», todos entrarían a Sala Servidores, Contabilidad, etc. Sin nivel, nadie baja a los equipos. | Reconstruir desde los terminales: en qué terminal está cada persona (`FoundOnTerminals` ya se guarda) y con qué horario (`RightPlan.planTemplateNo` + `UserRightPlanTemplate`/`UserRightWeekPlanCfg`). Crear **un nivel por puerta** («Acceso · Sala Técnica»…) con su horario y asignar a cada persona los de sus puertas. Réplica exacta; consolidar en grupos con sentido queda para después. | **Sí** |
| M3 | **«Colacion»** (campo personalizado de HCP) | No se lee | Se pierde la política de colación de cada persona | Leer `customFieldList` en `HikCentralClient` y guardarlo en el paquete (todos los campos, no solo este). Importarlo a TrueCentral cuando existan los campos personalizados (sección 3.2). | Capturarlo: **sí**. Importarlo: semana siguiente. |
| M4 | **Rostro** | Foto de HCP (miniatura de 135×189) ampliada a 400 px | Al reescribir la persona, el terminal vuelve a modelar el rostro desde esa miniatura y puede reconocer peor que hoy | Leer también la foto que guarda el terminal (`FDLib/FDSearch` → `faceURL`) y usar la más grande. Medir en el piloto. | Recomendado |
| M5 | Departamento | Se guarda la ruta «Padre / Hijo» | El casino usa el nombre HOJA como empresa: una persona nueva quedaría con otra empresa | Guardar la hoja (o que la API la entregue aparte) | Antes de migrar el casino |
| M6 | Paquete | `paquete.json` con huellas y fotos (y desde M1, claves) en claro | Fuga de datos biométricos (ISO 27001) | Generarlo en una carpeta cifrada (EFS/BitLocker), no copiarlo a USB y borrarlo al cerrar la ventana de rollback. Opcional: cifrar el paquete en el migrador. | Procedimiento: **sí** |

**Estado al 28-10.** M1, M2, M3 (captura) y M4 quedaron implementados en la rama `feat/corte-hikcentral`,
sin commitear. Se probaron con un HikCentral y dos terminales simulados contra un servidor aislado.

- **Exportación:**
  - lee las claves;
  - lee las puertas y el horario de cada persona;
  - recrea un horario restringido (lun–vie 12:00–15:00);
  - guarda «Colacion» y el departamento hoja;
  - toma el rostro del terminal cuando es más grande.
- **Importación:**
  - crea un nivel «Puerta · terminal» por puerta y horario;
  - se detiene si falta un terminal en TrueCentral o un horario sin leer;
  - una segunda pasada no duplica nada;
  - «completar» agrega puertas y claves sin quitar nada.
- **En el terminal:** se comprobó la clave y el horario que escribe TrueCentral.
- **Falta** validarlo con los equipos reales en el piloto.

**Ojo con las claves fuera de 4 a 8 dígitos.** TrueCentral no las acepta. Además, al escribir a esa
persona le **borra** la clave que tenía en el equipo. El migrador las lista: hay que definirles una
nueva antes del corte.

### 2.4 Conflictos con el kiosco del casino (aunque el casino no se migre el viernes)

**K1 · Suscriptores de eventos.**
- TrueCentral abre una escucha `alertStream` en **todos** sus terminales y no tiene opción por
  equipo para no hacerlo (`AccessEventService.EnsureListeners`).
- Según el README del kiosco, los terminales con «Facial con botón de llamada» admiten **un solo
  suscriptor** (`deployExceedMax`).
- Si TrueCentral toma el lugar del kiosco, **el casino deja de contar colaciones**.
- Arreglo (**hecho el 28-10**, sin commitear): casilla «Escuchar sus eventos en vivo» en la ficha
  del equipo.
  - Apagada, TrueCentral suelta la escucha en el momento y trae todo por el historial (`serialNo`,
    cada 15 s).
  - El cambio queda en la bitácora.
  - Se probó con el simulador: suelta y abre la escucha en caliente.
- Se usa en los terminales del casino hasta que el kiosco lea desde TrueCentral.
- Hay que confirmar en el `devices.json` del kiosco de producción qué terminales usa y con qué
  modo.

**K2 · Habilitados por el kiosco.**
- TrueCentral escribe cada persona con `UserInfo/SetUp`: `Valid.enable=true`, `userType=normal`
  y `localUIRight=false`.
- La primera sincronización completa **reescribe a todos**. Con eso:
  - vuelve a habilitar a quienes el operador del casino deshabilitó en los terminales;
  - posiblemente quita el «solo tarjeta» de las visitas (hay que verificarlo en el piloto).
- Lo mismo pasa después cada vez que se edita a esa persona en TrueCentral. Hoy ocurre igual con
  HCP, pero de a una persona.
- Arreglo para el viernes: el migrador deja la lista en `pendientes-despues-del-corte.csv`, dentro
  de la carpeta del paquete. La lista sale de lo que tiene cada terminal:
  - personas deshabilitadas;
  - «solo tarjeta»;
  - administradores del menú.

  Después de la sincronización, eso se vuelve a aplicar desde el kiosco.
- Arreglo definitivo (fase casino): «autorizado en el casino» pasa a ser un nivel de TrueCentral
  («Casino») y el kiosco lo cambia por la API en vez de escribir en el terminal.

**K4 · Visitas.**
- El kiosco reconoce a una visita por el tipo de usuario que tiene en el terminal (`userType`
  «visitor»). Con eso la exime de la regla de empresa y de colación, y le aplica la de visitas.
- TrueCentral escribe a todas las personas como «normal». Así, una visita migrada pasaría a
  tratarse como empleado en el próximo ciclo del kiosco (cada 30 minutos).
- El migrador las detecta y las deja en `pendientes-despues-del-corte.csv`, igual que K2.
- Las tarjetas de visita que el kiosco administra él mismo, sin personas en el terminal, no se ven
  afectadas.
- Arreglo definitivo: que TrueCentral guarde y escriba el tipo de usuario, o que el casino lea desde
  TrueCentral (fase 5).

**K3 · Administradores locales.** `localUIRight=false` le quita a todos el acceso al menú del
terminal. Si algún técnico entra al menú con su rostro o huella, hay que saberlo antes.

### 2.5 Otros riesgos

**R-reloj.**
- TrueCentral corrige la hora de los equipos sola: está activado por omisión en cada equipo.
- HCP también puede sincronizarla.
- Debe haber un solo dueño:
  - mientras conviven en modo sombra, la corrección automática de TrueCentral va **apagada** en
    los 12 equipos;
  - desde el corte, TrueCentral es el dueño y se apaga la sincronización de hora de HCP.
- Una hora mal puesta corre las marcaciones del casino (y existe el gotcha del signo POSIX de
  Hikvision).

**R-licencia.**
- La licencia del servidor de producción debe traer `module_access` y `access_doors` de **12 o
  más**.
- El paquete Professional estándar trae **4 puertas**; Enterprise trae 64.
- Si faltan, hay que emitir una expansión en bouncer **antes del jueves**: las licencias emitidas
  están congeladas.

**R-tiempo.**
- La primera bajada completa es pesada: en el laboratorio, 116 personas en 2 terminales tardaron
  ~7 minutos.
- Para 12 terminales hay que **medirla en el piloto** y extrapolar. Estimación grosera: 30–60
  minutos.

**R-modelos.**
- El piloto (Sala Técnica) es un K1T321MFWX V3.9.20.
- Los K1T805MBFWX V1.9.0 (los dos del casino entre ellos) y el K1T321MFWX V4.61 («New FT Casino»)
  **no se han escrito nunca desde TrueCentral**. Hay que probarlos también (6.2).

**R-ranuras de horario.**
- TrueCentral escribe sus horarios en ranuras numeradas del terminal: la 1 es su 24/7 y la 2 en
  adelante, los suyos. Pisa la plantilla de HikCentral que esté en esa ranura.
- En el corte no importa, porque se reescribe a todas las personas.
- En el piloto (solo el .196) sí importa: una persona del equipo que no se migra y usa la plantilla
  2+ de HCP podría quedar con otro horario.
- En la exportación del .196, revisar en el registro qué plantillas usa y quiénes; la 1 debe ser
  «todos los días 00:00-23:59».
- En un rollback, HCP vuelve a escribir sus plantillas.

**R-servidor.**
- Si el servidor TrueCentral se cae, **las puertas siguen funcionando**: el terminal valida rostro,
  huella, tarjeta y clave localmente.
- Las marcaciones quedan en el terminal y TrueCentral las recupera por `serialNo` al volver.
- Una caída del servidor no es motivo de rollback. Solo lo es un problema de datos o de
  configuración en los equipos.

---

### 2.6 Revisión de HikCentral (28-10, en el navegador, solo lectura)

**Padrón.**
- 111 personas, todas habilitadas.
- Credenciales: 101 rostros, 320 huellas y 40 tarjetas reales (las 113 «tarjetas» por equipo incluyen
  las virtuales).
- 5 personas no tienen ninguna credencial.
- Nada pendiente de aplicar y nada fallido.
- El legajo (`PersonCode`) tiene de 7 a 10 dígitos y es distinto del ID interno.
- Vigencias hasta 2032–2036, dentro del tope de 2037.

**Departamentos.** PPE 51, PTJ 30, VISITAS 5, ASEO 5, IPSA 5, CASINO 4, CLROBOTICS 3,
ALTA SEGURIDAD 3, CIRPAN 2, JARDINERIA 1, FUMIGACION 1, GRUPO JEMO 1.

**Campos personalizados.** Uno solo, **«Colacion»** (numérico): 47 con «1», 46 con «2», 7 con «0» y
11 sin valor.

**Claves.** «Generar clave automática para cada persona» está **activado**, así que casi todas
tienen clave en los terminales: M1 es imprescindible.

**Niveles: 11.** A cada nivel se asignan personas directamente; ninguno se asigna por departamento.
Todas las personas tienen al menos uno y solo 3 tienen dos (confirmado por la OpenAPI el 28-10).

| Nivel | Horario | Puertas | Personas |
|---|---|---|---|
| Oficinas | lun–sáb 06:45–20:00 | 11 (todas menos Sala Servidores) | 41 |
| Planta | lun–vie 06:00–21:00, sáb 06:00–14:00 | 11 | 34 |
| Acceso Total | 24/7 | 12 | 12 |
| Servicios | lun–vie 06:00–20:00, sáb 06:00–14:00 | 11 | 10 |
| Aseo | lun–vie 06:30–21:00, sáb 06:30–15:00 | 11 | 6 |
| Guardias | dom, vie y sáb 24 h; lun–jue 00:00–07:30 y 18:30–24:00 | 11 | 3 |
| Casino | lun–vie 05:00–17:00 | 4 (Pileta N/S, Acceso Casino N/S) | 3 |
| Sala Servidores | 24/7 | 1 | 2 |
| Rejas | lun–vie 05:00–20:00 | 4 | 1 |
| Jardinería | lun–vie 08:00–16:00 | 4 | 1 |
| Acceso General | 24/7 | 12 | 1 |

Combinaciones de dos niveles: «Oficinas + Sala Servidores» (2 personas) y «Planta + Rejas» (1).

- **Nombres de las puertas en HikCentral.** No coinciden con los de los terminales: «Oficina Tecnica»
  es Sala Técnica, «Mampara» es Mampara Recepción y «New FT Casino_Door_1» es New FT Casino.
- **Sala Técnica (el piloto)** está en 7 de los 11 niveles, así que ahí se cargan casi todas las
  personas.
- **Feriados:** ningún horario los tiene.

**Consecuencia para M2.** El modo «uno por puerta» funciona, pero crearía unos **79 niveles**:
- 11 puertas × 5 horarios;
- 4 puertas × 3 horarios;
- 12 puertas con 24/7.

Replicar los 11 niveles de HikCentral con su nombre, horario, puertas y personas es mucho más
limpio. Requiere desarrollo (ver 3.1).

**Aplicación a los equipos.**
- «Aplicar a los equipos (programado)» y «Sincronizar registros (programado)» están **apagados**:
  HikCentral solo escribe cuando alguien guarda un cambio.
- Tarjetas: autenticación Wiegand 34 (4 bytes), que es una configuración del equipo y TrueCentral no
  la toca.

**HORA — crítico.** En Sistema → Red → Sincronización de hora está elegida «Hora del servidor local»
con **intervalo de 1 minuto**. HikCentral le pone su hora a los 12 equipos cada minuto, así que si
TrueCentral la corrige, se pelean.
- Mientras HCP siga conectado (todo el período de rollback), la corrección automática de hora de
  TrueCentral va **apagada** en los 12 equipos.
- Al retirar HCP (fase 6), se enciende.
- Los equipos muestran la zona UTC-04:00: en el piloto hay que revisar que el horario de verano
  quede bien.

**Escuchas.** HikCentral tiene armados («Arming») los 12 equipos. Con TrueCentral y el kiosco, cada
facial con llamada tendría tres suscriptores: la escucha de TrueCentral va apagada en esos equipos
(K1).

**Rollback, menú exacto.** Control de acceso → Troubleshooting → pestaña **Device**:
- marcar el o los equipos y luego «**Apply**»;
- la misma tabla compara, por equipo, las personas y credenciales «en el equipo» con las «en el
  sistema», y sirve para verificar.

## 3. Qué hay que construir

### 3.1 Antes del viernes (bloqueante)

| Ítem | Dónde | Esfuerzo aprox. |
|---|---|---|
| M1 Clave desde los terminales | Migrador (`TerminalReader`, `MigrationPackage`, `MigrationEngine`) | 1–2 h |
| M2 Niveles por puerta + horarios desde los terminales | Migrador (lectura de `RightPlan` y plantillas, alta de horarios/niveles por API, asignación) | 3–5 h |
| M3 Capturar campos personalizados de HCP | Migrador (`HikCentralClient`, paquete) | 1 h |
| M4 Rostro desde el terminal si es mejor | Migrador | 2 h |
| K1 «Solo sondeo» por equipo | Servidor: entidad + migración EF + DTO + web + auditoría | 2–3 h |
| Versión e instalador | Servidor de producción + zip del migrador | 1 h |

| M2b Replicar los 11 niveles de HikCentral (nombre, horario, puertas y personas), **por la OpenAPI** (verificado el 28-10 contra el HCP 3.1 del cliente, ver abajo). Los tramos del horario no salen por API: se toman de la plantilla que esa misma persona tiene en el terminal. Cada persona se compara con lo que tienen los terminales y se avisa si no coincide. | Migrador | **Hecho el 28-10** (rama `feat/migrador-niveles-hcp`): modo «Copiar los de HikCentral», el predeterminado. Reconoce las plantillas de fábrica 1/2/3, reintenta con la firma antigua y descarta los elementos que no son puertas. |

Rutas de la OpenAPI de HCP 3.1 para M2b, probadas solo con lectura:

**Existen:**
- `acs/v1/privilege/group` con `type: 1`: entrega los 11 niveles, cada uno con su horario
  (`timeSchedule.indexCode` y `name`) y sus puertas (`ElementList[].Element.ID` = `doorIndexCode`).
- `acs/v1/privilege/group/single/personList` (`privilegeGroupId`, paginado): las personas del nivel,
  por su `personId`.
- `resource/v1/acsDoor/acsDoorList`: relaciona `doorIndexCode` con su `acsDevIndexCode`, y de ahí se
  llega a la IP con `acsDeviceList`.

**No existen** (responden código 8):
- `acs/v2/privilege/group`;
- `privilege/group/single/persons`, `privilege/group/persons`, `single/doors` y `single/doorList`;
- todas las de plantillas de horario: `timeTemplate/list`, `schedule/template/list` y
  `privilege/group/timeTemplate`.

**Estado al 28-10:** M1–M4 y K1 están hechos y probados con el simulador (sección 2.3). Falta:
- commitear;
- generar la versión;
- instalarla en el servidor de producción;
- armar el zip del migrador (`build-installers.ps1 -Solo Migrador`).

### 3.2 Semana del 2-11 (para migrar el casino)

- **Campos personalizados de personas** en TrueCentral (genéricos: el administrador define nombre y
  tipo). Incluye ficha, API, auditoría e importación desde el paquete. «Colacion» es uno de ellos.
- **API de integración** con licencia (sección 4).
- **Adaptador en el kiosco.** `HikCentralService` es el único punto de contacto con HCP: se agrega
  un `TrueCentralService` con la misma forma (`FetchPersonsAsync`, `TestConnectionAsync`) y un
  selector de origen en Configuración. El backend Django no cambia.

---

## 4. API para el casino (propuesta)

### 4.1 Recomendación: «API de integración» propia y licenciada

Es un módulo aparte del API de usuarios, como la OpenAPI de HCP:
- tiene sus propias credenciales, no usuarios del VMS;
- tiene sus propios permisos y su propia bitácora;
- se habilita por licencia.

El diseño es propio (sin calcar Artemis), porque es lo que se va a vender para otras integraciones
(casino, remuneraciones/asistencia, ERP).

**Credenciales («Aplicaciones integradas»).** Se administran en *Sistema → Integraciones*:
- nombre, AppKey y AppSecret (el secreto se muestra una sola vez y se puede rotar);
- direcciones IP permitidas y fecha de vencimiento;
- permisos de la aplicación (`persons.read`, `persons.photo`, `access.events.read`, más adelante
  `access.levels.write`);
- **alcance** por ubicación, con la misma capa de recursos y ubicaciones que los roles: el casino
  solo ve lo suyo.

**Firma.**
- Cabeceras `X-TC-AppKey`, `X-TC-Timestamp`, `X-TC-Nonce` y `X-TC-Signature`.
- La firma es HMAC-SHA256 sobre `método\nruta?query\ntimestamp\nnonce\nsha256(cuerpo)`.
- Tolerancia de ±5 minutos y nonce de un solo uso.
- Siempre por HTTPS.
- Es más fuerte que la firma antigua de HCP, que no firma ni el cuerpo ni la hora y se puede
  repetir.

**Licencia.**
- Módulo `module_integration_api` («API de integración») y cupo `integration_apps` (aplicaciones
  activas).
- Packages sugeridos: Enterprise con 5, Professional con 0 y la expansión `addon_integration_app_1`.
- Se sigue la regla de licencias: constantes + `LicenseCatalog` + subir `Version` (6 → 7) +
  `Deny` al crear o habilitar + importar el catálogo en bouncer + **expansión para este cliente**.

**Permisos y auditoría.**
- Permiso nuevo para administrar integraciones.
- Van a la bitácora: altas, rotaciones y revocaciones de llaves, firmas rechazadas, el primer uso
  del día y las denegaciones por licencia.

**Endpoints v1, mínimo para el casino:**

| Método | Ruta | Para qué |
|---|---|---|
| GET | `/api/integration/v1/persons?updatedSince=&page=&pageSize=` | Padrón: `employeeNo`, `externalId` (personId de HCP), nombre, departamento (hoja y ruta), habilitado, vigencia, `attributes` (`colacion`…), `photo {hash, updatedAt}`. Con `total` y `complete` para deducir bajas. |
| GET | `/api/integration/v1/persons/{employeeNo}/photo` | Foto JPEG (permiso aparte: es biométrico) |
| GET | `/api/integration/v1/departments` | Empresas/departamentos |
| GET | `/api/integration/v1/health` | Versión, licencia y hora del servidor |

**Fase 2** (opcional, cuando el casino quiera dejar de armar los terminales):
- `GET /api/integration/v1/access/events?after=<cursor>`, con cursor por id de evento. Recupera
  lo que llegó tarde, lo que el kiosco hoy pierde.
- Empuje en vivo por WebSocket.
- `PUT …/persons/{employeeNo}/levels/{nivel}` para autorizar o desautorizar en el casino.

Con eso el kiosco ya no tiene que suscribirse ni escribir en los terminales, y desaparecen K1 y K2.

### 4.2 Alternativa rápida: capa compatible con la OpenAPI de HikCentral

Consiste en exponer en TrueCentral `person/personList`, `org/orgList` y `person/picture_data`, con
la firma antigua de HCP. El kiosco no cambia nada: solo su `hikcentral.json` (host, AK, SK).

- **A favor:** cero cambios en el kiosco, y el rollback es volver el archivo atrás.
- **En contra:** copia la API de HikCentral (choca con la identidad del producto), hereda una
  firma débil y no sirve para otras integraciones.

**Sugerencia:** la API propia (4.1). El adaptador del kiosco es chico porque todo pasa por una sola
clase.

---

## 5. Calendario

| Cuándo | Fase | Qué |
|---|---|---|
| Mié 28 (tarde) | 0 · Preparación | Revisar este plan. Confirmar las preguntas de la sección 10. Desarrollar M1–M4 y K1. Licencia. |
| Jue 29 (mañana) | 0 · Preparación | Terminar el desarrollo, instalar la versión en el servidor de producción y armar el zip del migrador. |
| Jue 29 (tarde) | 1 · Piloto | Sala Técnica completa + prueba de rollback + una persona de prueba en un K1T805. Exportación de prueba de los 12 terminales (solo lectura) para medir tiempos y conteos. |
| Jue 29 (noche) | 2 · Sombra | Los 12 equipos dados de alta en TrueCentral, **sin importar personas**: no se les escribe nada. Hora sin corregir y los del casino sin escucha en vivo. Se comparan las marcaciones con HCP; los eventos traen el legajo y el nombre que tiene el equipo. |
| **Vie 30** | **3 · Corte** | Runbook de la sección 7, en la ventana de menor uso. |
| 2–6 nov | 4 · Estabilización | HCP congelado y conectado. Seguimiento diario. Desarrollo de la sección 3.2. |
| 2–13 nov | 5 · Casino | El kiosco pasa a leer el padrón desde TrueCentral, con su propio rollback (7.4). |
| ≥ 2–4 semanas después del corte | 6 · Retiro de HCP | Sección 9. |

---

## 6. Piloto en Sala Técnica (adelantado al miércoles 28 por la tarde)

Servidor de producción: **192.168.1.78**. Corría una versión anterior y hay que actualizarlo a la
0.5.30. Su licencia trae control de acceso con 9999 puertas.

**Orden para hoy:**
1. Exportar **solo**, sin importar, desde HCP + el .196, y revisar el registro.
2. Después, importar con «Importar un paquete guardado».

La Sala Técnica (192.168.1.196, DS-K1T321MFWX V3.9.20) está sin uso. Desde este PC hay
conexión a los terminales y al HCP.

Las claves de los terminales las ingresa José en el migrador. **No probar claves al azar:** cinco
intentos fallidos bloquean la IP 30 minutos.

### 6.1 Sala Técnica

1. **Solo lectura.** Exportar con el migrador desde HCP + el terminal .196 y revisar:
   - personas;
   - tarjetas (descartadas las virtuales ≥ 1,8e19);
   - huellas;
   - **claves** (M1);
   - rostros (tamaño de la foto de HCP frente a la del terminal, M4);
   - horarios que usa cada persona (M2).
2. **Respaldo del terminal.** Guardar ese paquete (en una carpeta cifrada) como foto del estado
   actual.
3. **Alta en TrueCentral (producción).**
   - Dar de alta el .196.
   - Apagar la corrección automática de hora.
   - Ver que lleguen las marcaciones en vivo y por historial, con la hora correcta.
4. **Toma de control.** En el migrador:
   - leer solo el .196;
   - niveles «uno por puerta»;
   - marcar «solo las personas encontradas en los terminales leídos».

   El migrador crea el nivel «Puerta · Sala Tecnica» con su horario y le asigna solo a esas personas.
   Medir cuánto tarda la bajada.

   En el piloto **no** se usa «copiar los de HikCentral»: sus niveles tienen puertas de los 12
   equipos, y el migrador no importa nada si falta alguno en TrueCentral. Sí conviene revisar en el
   registro de la exportación los niveles, los horarios deducidos y la verificación contra el .196.
5. **Verificación en el equipo.**
   - En TrueCentral → Personas, cero personas con error en el .196.
   - Releer el terminal con el migrador y comparar los conteos contra el paso 1: personas,
     tarjetas, huellas, rostros y claves.
6. **Pruebas en la puerta**, con 3 a 5 personas reales:
   - rostro, huella, tarjeta y clave;
   - una persona sin el nivel es rechazada;
   - la vigencia;
   - el evento en TrueCentral con nombre y hora;
   - la apertura remota.
7. **Pruebas de administración.**
   - Una persona de prueba creada en TrueCentral aparece en el equipo.
   - Al quitarle la tarjeta, la tarjeta deja de abrir.
   - Al deshabilitarla, desaparece del equipo.
   - Una visita «solo tarjeta» y una persona deshabilitada «como el kiosco»: ver qué les hace la
     reescritura de TrueCentral (K2).
8. **Prueba de rollback** (la más importante).
   - Pausar el .196 en TrueCentral.
   - En HCP, volver a aplicar a ese equipo (Apply del nivel o de las personas del .196).
   - Verificar que las personas vuelven a entrar y que los conteos coinciden con el respaldo del
     paso 2.
   - Anotar cuánto tarda y qué menú exacto de HCP se usó: eso va en la sección 8.
   - Después, volver a darle el control a TrueCentral.

### 6.2 Modelos sin probar

Se hace con una persona de prueba, sin tocar a nadie más.

- Crear el nivel «Prueba K1T805» con solo la puerta de un K1T805MBFWX de poco uso (Pileta Norte
  .190 o Ventas Sur .102) y solo la persona de prueba (legajo 990001).
- TrueCentral escribe únicamente a esa persona, porque las demás no tienen niveles en
  TrueCentral. Probar rostro, huella, tarjeta y clave, y borrarla al final.
- Repetir en «New FT Casino» (.65, V4.61) **fuera del horario del casino**, con el kiosco en modo
  de pruebas, y confirmar que el kiosco sigue recibiendo sus marcaciones (K1).

### 6.3 Exportación completa de prueba (solo lectura)

- Leer HCP + los 12 terminales y guardar los conteos por terminal.
- Sirve para medir la duración real de la exportación del viernes y para detectar personas que no
  cruzan o claves distintas entre terminales.

**Criterio para seguir al viernes:** los pasos 5 a 8 salen bien, incluido el rollback.

---

## 7. Corte del viernes 30 (runbook)

**Ventana confirmada: viernes desde las 10:00.** El viernes el casino solo atiende el desayuno.

El kiosco (uno solo) **no usa el botón de llamada, sino un botón secundario conectado como botón de
salida**, para no apagar el reconocimiento facial. El kiosco detecta ese botón por el `alertStream`
(evento de botón de salida), así que en los terminales del kiosco **la escucha en vivo de
TrueCentral va apagada** (K1). Falta confirmar qué terminales tiene configurados el kiosco.

### 7.1 Antes (el día anterior y la primera hora)

- [ ] Avisar el **congelamiento de HCP**: desde la hora T0 no se edita nada en HikCentral. Si hace
      falta, cambiar temporalmente la clave del admin de HCP.
- [ ] Confirmar que HCP no tiene tareas programadas que reapliquen permisos a los equipos.
- [ ] **Respaldo de HikCentral** con su respaldo nativo (Backup and Restore) + exportación del
      padrón a Excel desde Person.
- [ ] **Respaldo de TrueCentral** (Sistema → Respaldos).
- [ ] Licencia del servidor de producción: control de acceso con ≥ 12 puertas.
- [ ] Del kiosco del casino: lista de personas **deshabilitadas** y de visitas «solo tarjeta» (K2).
- [ ] Los 12 terminales en línea en HCP y en TrueCentral. Los del casino en «solo sondeo» (K1).
- [ ] Carpeta cifrada para el paquete (M6).

### 7.2 Durante

| Paso | Acción | Verificación |
|---|---|---|
| 1 | T0: congelar HCP | Hora anotada |
| 2 | Revisar a las personas del piloto: ya están en TrueCentral con «Puerta · Sala Tecnica». La importación las completa, pero no les quita nada. Si alguna cambió en HCP desde el jueves (tarjeta dada de baja, vigencia), corregirla a mano. | Lista del piloto revisada |
| 3 | Exportación final: HCP + 12 terminales | Conteos contra la exportación de prueba del jueves: personas, tarjetas, huellas, claves, rostros y personas por terminal. Revisar `pendientes-despues-del-corte.csv` y las claves avisadas. |
| 4 | Importación con el migrador: legajo del terminal, niveles **«copiar los de HikCentral»** (M2b), claves (M1), campos personalizados en el paquete (M3) y «completar» marcado. Después, **borrar el nivel «Puerta · Sala Tecnica»** del piloto: las personas conservan la puerta por sus niveles copiados. | 0 errores en el registro; 11 niveles; las personas en TrueCentral = las de HCP |
| 5 | Bajada a los equipos. Seguir la franja de avance en Personas. | Cada equipo con 0 fallidas. Releer 2 o 3 terminales con el migrador y comparar conteos. |
| 6 | Hora: **no tocar**. HCP sigue poniendo la hora cada minuto; la corrección de TrueCentral queda apagada en los 12 equipos hasta retirar HCP (2.6). | Hora de los equipos sin desvíos en TrueCentral |
| 7 | Volver a aplicar desde el kiosco los deshabilitados y los «solo tarjeta» (K2) | Muestra de 3 personas en el terminal del casino |
| 8 | Prueba de humo en las 12 puertas: una persona con rostro en cada una; tarjeta, clave y huella donde corresponda; una persona sin permiso es rechazada en Sala Servidores | Planilla puerta × credencial |
| 9 | Marcaciones | Las 12 puertas aparecen en TrueCentral con la hora correcta. El casino cuenta una colación de prueba. |
| 10 | **Go / No-Go** | Según 7.3 |

### 7.3 Criterios de Go

Se sigue solo si se cumple todo:

- 100 % de las personas sincronizadas en los 12 equipos (0 fallidas).
- Conteos por terminal iguales a la exportación: personas, tarjetas, huellas, rostros y claves.
- La prueba de humo sale bien en las 12 puertas.
- Llegan las marcaciones de los 12 equipos.
- El kiosco recibe sus marcaciones y cuenta colaciones.

Si algo falla y no se arregla en **30 minutos**, se aplica el rollback de la sección 8.

### 7.4 Después del corte

**Altas y cambios.**
- Se hacen solo en TrueCentral.
- Hasta que el casino lea desde TrueCentral, **una persona nueva que deba comer también hay que
  crearla en HCP con el mismo `personCode`**, o dejar las altas de casino para después de la
  fase 5.
- Cada alta queda en la bitácora de TrueCentral: esa lista también es la que se usa para el
  rollback.

**«Colacion».**
- Hasta la fase 5 se sigue editando en HCP (solo ese campo).
- Ojo: al guardar una persona en HCP, HCP puede reaplicarla a los equipos. Hay que verificar en el
  piloto si editar un campo personalizado dispara esa reaplicación.

**Seguimiento.** Revisar cada mañana durante la primera semana:
- personas con error de sincronización;
- equipos desconectados;
- desvíos de hora;
- reclamos en las puertas.

---

## 8. Rollback

**Principio.** HikCentral sigue intacto: congelado a la hora T0, respaldado y conectado a los
terminales. Volver atrás es devolverle la escritura de los equipos.

Los terminales usan el mismo `employeeNo` en los dos sistemas. Por eso la reaplicación de HCP
sobrescribe a las mismas personas, sin duplicar.

**Disparadores:**
- Personas que no pueden entrar en una o más puertas y no se resuelve en 30 minutos.
- Un modelo de terminal que no acepta lo que escribe TrueCentral.
- El casino no recibe marcaciones por culpa de TrueCentral (K1).

**No es disparador:** una caída del servidor TrueCentral, porque las puertas siguen funcionando
(2.5).

**Niveles:**

**R1 · Una persona.**
- Se corrige en TrueCentral y se resincroniza.
- Si es urgente: alta de emergencia en el propio terminal o desde HCP para esa persona. Después se
  concilia en TrueCentral.

**R2 · Un terminal.**
1. En TrueCentral, **pausar** el equipo: deja de escribirle.
2. En HCP: Control de acceso → Troubleshooting → Device → marcar el equipo → **Apply** (se ensaya en
   el piloto, 6.1, paso 8).
3. Si es un terminal del casino, volver a aplicar los deshabilitados desde el kiosco.
4. Verificar los conteos contra el paquete de T0.

**R3 · Total.**
1. En TrueCentral, **deshabilitar** a las personas creadas después de T0 (según la bitácora):
   TrueCentral las borra de los equipos.
2. En TrueCentral, pausar los 12 equipos.
3. En HCP: Troubleshooting → Device → marcar los 12 equipos → **Apply**.
4. Hora: nada que hacer (HCP nunca dejó de sincronizarla).
5. Kiosco: volver a aplicar los deshabilitados y los «solo tarjeta». El kiosco nunca dejó de leer
   desde HCP.
6. Verificar conteos y hacer una prueba de humo en las 12 puertas.
7. Recrear en HCP las altas y cambios hechos en TrueCentral después de T0 (lista de la bitácora).

El tiempo estimado es similar al de la bajada inicial. El dato real se mide en el piloto.

**Punto de no retorno:** cuando se desconecte HCP de los terminales o se apague (fase 6). Hasta
ahí el rollback siempre es posible.

**Ensayo del 30-10 en la Sala Técnica (.196), después de que TrueCentral la escribió:**
- **Apply normal** (Troubleshooting → Device → Apply, sin «Apply Full»): **no hace nada**. HCP cree
  que no hay nada pendiente («To be Applied: 0»).
- **Apply Full con TrueCentral activo:** HCP vació el equipo, alcanzó a subir 39 de 107 personas
  sin credenciales y falló con `SYS[3225] Persistent connection exception`. Mientras tanto, **nadie
  podía entrar por esa puerta**.
- **Apply Full con TrueCentral pausado** (reintento con «Apply Now»): sí funcionó. Quedaron 107
  personas, 111 tarjetas (incluye las virtuales de HCP), 99 rostros y 0 fallidas. **Pero las huellas
  quedaron en 0** (316 en HCP): HCP no las repone.
- **Conclusiones:**
  - un Apply Full de HCP sobre una puerta en uso la deja **sin huellas**, y sin acceso mientras dura;
  - si TrueCentral escribió bien el equipo, el rollback más seguro es solo **pausarlo en TrueCentral
    y dejar lo escrito**: el terminal sigue validando con las mismas personas, credenciales y
    horarios;
  - el Apply Full se reserva para cuando lo que escribió TrueCentral está mal, y después hay que
    reponer las huellas;
  - siempre con TrueCentral pausado antes.

---

## 9. Retiro de HikCentral (fase 6)

**Condiciones:**
- el casino lleva al menos 1 semana leyendo desde TrueCentral;
- llevamos 2 a 4 semanas sin rollbacks;
- «Colacion» y los demás campos personalizados ya están en TrueCentral.

**Pasos:**
1. Respaldo final de HCP (base + exportaciones) en un medio guardado.
2. Exportar de HCP el historial de accesos que se quiera conservar. TrueCentral solo tiene desde el
   alta de cada equipo; retiene 365 días por omisión (`Access:EventRetentionDays`).
3. Quitar los equipos de HCP. Así libera su armado: un suscriptor menos en cada terminal.
4. Apagar HCP y borrar el paquete de migración (M6), dejando constancia en la bitácora.

---

## 10. Preguntas abiertas

1. ¿Qué servidor TrueCentral queda como producción del control de acceso (¿el 192.168.1.78?)?
   ¿Qué licencia tiene (Professional = 4 puertas)?
2. ¿Cuál es la ventana del viernes? ¿En qué horario hay menos uso de las puertas y del casino?
3. `devices.json` del kiosco de producción: ¿qué terminales usa y en cuáles está activo «Facial
   con botón de llamada» (`alertStream`)?
4. ¿Algún técnico o persona usa el menú local de los terminales (administrador local)? (K3)
5. ¿Hay horarios distintos de 24/7 en los niveles de HCP, o feriados? (Se ve en la exportación.)
6. API del casino: ¿la propia (4.1, sugerida) o la capa compatible con HCP (4.2)?
7. ¿Cuántas colaciones se pueden ingresar a mano sin problema si el kiosco pierde marcaciones
   durante la prueba de K1?
