# Plan: grabación en el servidor y gestión de evidencias

Borrador de planificación (30/10/2026). Solo planificación: no hay código todavía. Se basa en la conversación de
diseño del 30/10/2026, que tomó como referencia Genetec Streamvault/Security Center (grabación) y Axon Evidence
(evidencias), con nombres y UX propios de TrueCentral.

## 1. Objetivo

1. **Grabar en el servidor TrueCentral**, sin depender del DVR/NVR ni de la marca del grabador.
2. **Gestionar evidencias**: conservar copias protegidas, con huella, plazo y cadena de custodia, agruparlas en
   expedientes y **compartirlas por enlace sin que se puedan descargar**.
3. A futuro, un **equipo llave en mano** (servidor con TrueCentral preinstalado, endurecido y con discos
   dimensionados), al estilo de un appliance.

Son **dos módulos distintos que van de la mano**: un cliente puede querer uno, el otro o los dos.

## 2. Estado actual

- TrueCentral **no graba**. La reproducción lee lo que graba el propio equipo (DVR/NVR/tarjeta): `PlaybackApi`
  consulta los tramos por el driver (`QueryRecordingsAsync`) y monta una ruta temporal en MediaMTX hacia la URL de
  reproducción RTSP del fabricante (`ResolvePlaybackUrlAsync`).
- Las descargas (`/api/playback/.../download`, Centro de descargas del cliente) pasan por ffmpeg al ritmo que entrega
  el equipo, normalmente tiempo real.
- MediaMTX ya trae grabación (`record`, tramos fMP4) y servidor de reproducción, hoy apagado (`playback: no` en
  `MediaMtxManager`). Las rutas son `sourceOnDemand: yes`: solo se conectan si alguien mira.
- Licencias existentes relacionadas: `module_video` + `video_channels`, `module_playback`.
- Permisos existentes: `playback.view`, `playback.export`.

## 3. Arquitectura

```
                 ┌──────────────────────────┐   ┌───────────────────────────────┐
                 │  Grabación (módulo)       │   │  Evidencias (módulo)          │
                 │  continua / por evento    │   │  expedientes · custodia       │
                 │  reproducción y descarga  │   │  compartir por enlace         │
                 └────────────┬─────────────┘   └───────────────┬───────────────┘
                              │                                 │
                 ┌────────────┴─────────────────────────────────┴───────────────┐
                 │  Almacenamiento de video (base común, no se licencia sola)   │
                 │  volúmenes · índice de archivos · huellas · motor de retención│
                 └───────────────────────────────────────────────────────────────┘
```

- Servicios separados en el código (grabación, evidencias) sobre un almacenamiento común: la licencia apaga uno sin
  romper el otro.
- **Evidencias funciona sin Grabación**: copia el rango desde el NVR/tarjeta por la reproducción que ya existe
  (lento, en tiempo real, pero funciona). Con Grabación la copia es instantánea (archivos del mismo servidor).

### Encaje con lo que ya existe

| Necesidad | Pieza existente |
|---|---|
| Recibir y grabar el video | MediaMTX embebido (`MediaMtxManager`), una sola conexión por cámara para vivo y grabación |
| Reproducir desde el servidor | Intermediación por loopback como el WHEP (9914) y tokens del vivo |
| Exportar / convertir | ffmpeg distribuido (`LocateFfmpeg`), Centro de descargas |
| Índice, evidencias, comparticiones | PostgreSQL embebido (EF) |
| Eventos que disparan grabación o evidencia | Motor de automatizaciones (`WorkflowEngine`), eventos de video, alarmas, ANPR, acceso |
| Avisos de disco / grabación caída | Supervisor de servicios + alertas |
| Cadena de custodia | Bitácora ISO 27001 (`AuditCatalog` + `AuditService`) |
| Informe de custodia | `Services/Reports` (`PdfReportWriter`, `ReportTable`) |
| Hora de las marcas | Hora oficial (NTP) |
| Permisos y alcance | `Permissions.cs`, `ApiSecurity.Require`, `UserScope` (validar el recurso concreto) |
| Cortar sesiones al revocar | `HubConnections` + `TokenService` (revocación de sesiones) |
| Exponer la compartición a Internet | Acceso desde Internet (WAN), `OutboundAddressPolicy`, CSP estricta y tickets de URL |
| Licencia | `LicenseFeatures` / `LicenseCatalog` (catálogo como código) |

## 4. Almacenamiento

### Volúmenes

- Disco local, RAID, **iSCSI** (SAN o LUN de un NAS) y, con reservas, carpeta compartida SMB.
- iSCSI se ve en Windows como un disco local: no cambia el diseño, pero obliga a:
  - **identificar el volumen por su GUID, no por la letra** (la letra puede cambiar al reconectar);
  - vigilar que el volumen siga conectado: si desaparece, avisar al instante y, opcional, grabar en un disco local de
    respaldo hasta que vuelva;
  - que el servicio arranque después del iniciador iSCSI (destino favorito/persistente, dependencia de `MSiSCSI`);
  - guía de instalación: red o VLAN dedicada, MPIO, LUN con reserva completa (thick, no thin), GPT, NTFS/ReFS con
    unidad de asignación de 64 KB.
- SMB: obliga a una cuenta del servicio con acceso a la red y rinde peor con muchos archivos.
- **Cuota propia por volumen** («TrueCentral puede usar hasta X TB»), no solo el espacio libre: no se rompe si alguien
  copia otros archivos al disco. Se recomiendan volúmenes dedicados.
- Medir la latencia de escritura y avisar si el almacenamiento se pone lento.
- **Excluir las carpetas de grabación del antivirus** (el instalador puede hacerlo).

### Archivos (tramos)

- Estructura: `{volumen}\TrueCentral\Grabaciones\{cámara}\{aaaa-mm-dd}\{HH-mm-ss}.mp4` (`recordPath` de MediaMTX).
  Al vencer un día, su carpeta queda vacía y se elimina.
- **Duración configurable por cámara** (`recordSegmentDuration` es por ruta en MediaMTX):
  - grabación continua: **15 o 60 min** por omisión (decisión abierta, §14). 60 min es el valor del mercado y el de
    MediaMTX; 15 min reduce lo que se pierde si se daña un archivo y la ventana sin huella;
  - cámaras por evento: **1 min**, para el búfer de pre-alarma.
- fMP4 con fragmentos de ~1 s (`recordPartDuration`): ante una caída se pierde el último segundo, no el archivo.
- MediaMTX corta en un cuadro clave: la duración real varía (H.265+/Smart Codec espacian mucho los keyframes). El
  índice guarda **inicio y fin reales**, nunca supone la duración exacta.

| Duración | Archivos por cámara al día | 200 cámaras × 30 días | Tamaño a 4 Mbps |
|---|---|---|---|
| 1 min | 1.440 | ~8,6 millones | ~30 MB |
| 15 min | 96 | ~580 mil | ~450 MB |
| 60 min | 24 | ~144 mil | ~1,8 GB |

### Índice

- Tabla de tramos: cámara, volumen (GUID), archivo, inicio, fin, tamaño, huella, clase (normal / temporal de búfer),
  estado (en curso, cerrado, faltante).
- **Entrada al índice**: el servidor vigila las carpetas (o las revisa cada ~30 s) y anota por lotes. **No** usar
  `runOnRecordSegmentComplete` (lanza un proceso por archivo).
- **Conciliación** al arrancar y periódica: archivos sin fila (caída) y filas sin archivo (borrado externo).
- La línea de tiempo agrupa en tramos continuos («08:00–14:32 grabado, hueco, 14:35–…»).

## 5. Grabación

- Por cámara: grabar sí/no, qué flujo (principal o secundario), volumen, retención en días, modo (continua / por
  evento / por horario).
- Las rutas grabadas pasan a estar **siempre conectadas** (`sourceOnDemand: no`). La misma conexión sirve al vivo.
- MediaMTX sin recodificar: el procesador casi no trabaja; el límite es el disco.
- **Reproducción desde el servidor**: servidor de reproducción de MediaMTX solo en loopback, intermediado por
  TrueCentral con los mismos tokens que el vivo.
- **Descarga casi instantánea**: unir archivos sin esperar al equipo (hoy va a tiempo real).
- **Grabación por evento con pre-alarma**: se graba siempre en un búfer corto (p. ej. 10 min, tramos de 1 min marcados
  «temporales»). Un evento marca los tramos de antes y después «a conservar»; los demás se borran solos.
  Acción nueva de automatización: «Grabar / conservar tramo».
- **Relleno desde el equipo**: si se cortó la red, al reconectar se trae del NVR/tarjeta el tramo faltante con los
  drivers actuales. La línea de tiempo combina ambas fuentes y prefiere la del servidor.
- **Huecos**: cada reinicio o recarga de MediaMTX corta la grabación. Revisar cuándo se recarga la configuración
  (ver la memoria de recarga de MediaMTX) y avisar de cada hueco.
- Más adelante: **grabadores separados** (servicio aparte que se registra en el central) y respaldo entre ellos. Cada
  grabador con su propio volumen o LUN: un LUN no se monta en dos servidores a la vez; el de respaldo graba en el
  suyo.

## 6. Retención y borrado

Lo hace un **servicio de retención propio**; el borrado de MediaMTX queda apagado (`recordDeleteAfter: 0`), porque
solo borra por antigüedad, no mira el espacio, no conoce las evidencias y no avisa.

Reglas, evaluadas cada minuto:

1. **Por días, por cámara o grupo**: lo que pasa de su edad se borra **aunque sobre espacio** (puede ser un requisito
   de privacidad).
2. **Por espacio, por volumen**: sobre un umbral alto (p. ej. 92 %) se borra hasta uno bajo (p. ej. 88 %). Se borra
   **el tramo más antiguo de todo el volumen**, de cualquier cámara, para que todas queden con la misma profundidad.
3. Si chocan, **manda el espacio** (dejar de grabar es peor) y se avisa: «la cámara X guarda 18 días; se
   configuraron 30».

Nunca se borra automáticamente:
- el tramo en curso;
- tramos en uso: la reproducción y la exportación **reservan su rango** mientras duran;
- tramos de un volumen desconectado: se pausa su retención y no se marca nada como borrado;
- evidencias: viven aparte (§7) y tienen su propia retención.

Otros:
- La hora del tramo sale del nombre o del índice, no de la fecha de modificación del archivo.
- Cámara eliminada del sistema: sus grabaciones se quedan hasta vencer; purgarlas antes requiere permiso y bitácora.
- Bitácora **resumida**: «Retención: 120 tramos (36 GB) de 12 cámaras; lo más antiguo ahora es del 28-09».
- Integridad: si las huellas van encadenadas, al borrar se guarda la huella del tramo eliminado, para distinguir un
  borrado legítimo de un archivo que falta.
- Cada cámara muestra «grabación disponible desde …» (su tramo más antiguo): la retención real a la vista.

## 7. Evidencias

### Descargar no es lo mismo que gestionar una evidencia

| | Descargar un video | Gestionar una evidencia |
|---|---|---|
| Qué es | Sacar una copia del sistema | Conservar una copia **dentro** del sistema y responder por ella |
| Después | El sistema pierde el control del archivo | Queda protegido, con huella, tipo, plazo y responsable |
| Registro | «Fulano descargó X» (ya existe) | Toda su historia (creación, accesos, cambios, exportaciones, borrado) |
| Ante un juicio | Un MP4 suelto | Informe de custodia + exportación verificable |

- La **descarga básica sigue en el producto base**, sin la licencia de Evidencias (quitarla sería un retroceso).
- Política estricta opcional del módulo: «no se descarga libremente, todo sale como evidencia», o al menos **exigir
  un motivo** al descargar.

### Diseño

- **Evidencia = copia del rango** a un área de evidencias aparte (otro volumen si se quiere), con su huella al
  crearse. No se bloquean los tramos de la grabación (con archivos de 60 min sería retener 1,8 GB por 2 minutos).
- **Original inmutable**: recortes, censuras y copias de visualización son **derivadas** vinculadas al original.
- Fuentes: grabación del servidor (instantánea), NVR/tarjeta (por la reproducción existente), fotos de patentes,
  eventos de acceso y alarmas, documentos cargados a mano.
- **Tipos de evidencia con plazo** (p. ej. «Incidente menor» 90 días, «Denuncia» 2 años, «Judicial» indefinido). Si
  tiene varios, gana el más largo. Al vencer queda **pendiente de borrado durante un período de gracia**, recuperable.
- **Expedientes**: agrupan las evidencias de un hecho. Diferencial de TrueCentral: además del video, juntan los
  **eventos de acceso, patentes, alarmas y partes de citofonía** del mismo hecho.
- **Cadena de custodia**: la bitácora filtrada por evidencia → **informe de custodia en PDF**.
- **Exportación verificable**: paquete con los videos, manifiesto de huellas firmado y verificador o reproductor.
- **Restricción por evidencia**, encima de permisos y alcance: «solo este grupo la ve».
- **Evidencias automáticas** (con Grabación): acción de automatización «Crear evidencia» ante una alarma, una patente
  en lista negra o una puerta forzada, con pre-alarma.
- Integridad: huella SHA-256 por archivo, encadenada y firmada con una llave del servidor. Cifrado en disco: BitLocker
  a nivel de Windows antes que cifrar en la aplicación.
- Al final: censura de rostros, patentes y pantallas (ffmpeg + modelo de detección) y transcripción del audio.

## 8. Compartir evidencias por enlace

Requisito: el destinatario (fiscalía, abogado, aseguradora) **ve, pero no descarga**, y se controla cuántas veces se
abre, cuántas se reproduce, quién lo hace, con autenticación y autorización temporal.

### Límite honesto

Lo que se ve en pantalla siempre se puede grabar (grabador de pantalla, cámara de un celular). Lo que sí se hace:
1. **No existe un archivo que bajar**: pedazos cifrados con permisos de corta vida, sin MP4, sin botón, sin URL
   reutilizable.
2. **La filtración tiene nombre y apellido**: **marca de agua visible con la identidad de quien mira**, quemada en la
   imagen y en movimiento («Compartido con j.perez@fiscalia.cl · 30-10-2026 14:32 · ID 7F3K»). Es la disuasión real.

### Diseño

- **La compartición es un objeto con reglas**: qué (evidencia o expediente), **para quién** (correo o teléfono,
  nominativo), **ventana de validez**, **máximo de aperturas**, **máximo de reproducciones**, opcional tiempo total de
  visualización, redes o países permitidos, aceptación obligatoria de un aviso de confidencialidad; **revocable en
  cualquier momento**.
- **Autenticación**:
  - enlace con código largo y aleatorio; el servidor guarda solo su huella;
  - al abrirlo, **código de un solo uso al correo o SMS del destinatario** (enlace + correo = dos factores), con
    límite de intentos;
  - opcional: atar el enlace al primer dispositivo que lo abre;
  - **una sola sesión a la vez** por compartición, corta y renovable mientras se usa;
  - más adelante, cuentas externas permanentes para destinatarios frecuentes.
- **El servidor cuenta, no el navegador**: cada reproducción pide un permiso que descuenta del límite; sin permiso no
  se entregan los pedazos. Se registra quién, IP, navegador, inicio, cuánto vio, adelantos y retrocesos. Al llegar al
  límite, al vencer o al revocar, se cortan las sesiones abiertas.
- **Copia de visualización por destinatario**: derivada con su marca de agua, recodificada a **H.264** (compatible con
  cualquier navegador), entregada como HLS con AES a un reproductor web propio. La página indica que es una copia de
  visualización y la huella del original custodiado. Se borra al vencer la compartición.
- **Custodia**: creación, envío del código, códigos fallidos, aperturas, reproducciones, límites alcanzados,
  revocación y vencimiento entran al informe de custodia. Opcional: avisar a quien compartió («Fiscalía abrió el
  expediente»).
- **Quién comparte**: permiso propio, respetando el alcance. Opcional: **aprobación de un supervisor** antes de que
  salga un enlace externo (regla de dos personas).

### Exposición a Internet (decisión abierta, §14)

- **A) Directo desde el servidor del cliente**, con el acceso desde Internet existente y una **superficie mínima
  separada** (solo las rutas de compartición, límites de intentos, sin acceso a la API principal). Más barato; obliga
  a exponer el servidor.
- **B) Portal de CLRobotics en la nube** (como el servidor de licencias): el servidor del cliente sube la copia con
  marca de agua y el portal la sirve. El cliente no expone nada; hay que operar el servicio; abre un modelo de
  suscripción.
- Inclinación: **B para el producto final**, A como primera versión para clientes con acceso remoto ya configurado.

### Niveles de protección

- **Nivel 1**: enlace + código, límites, marca de agua por destinatario, pedazos cifrados, custodia. Cubre a casi
  todos.
- **Nivel 2**: DRM comercial (Widevine / PlayReady / FairPlay por un proveedor). En equipos compatibles la captura de
  pantalla sale en negro; tiene costo por licencia y no detiene la cámara de un celular. Solo si un cliente lo exige.

Requiere su propia **revisión de seguridad**: es la primera parte de TrueCentral pensada para personas que no son
usuarios del sistema.

## 9. Licencias (propuesta)

| Clave | Tipo | Qué habilita |
|---|---|---|
| `module_recording` | bool | Grabación en el servidor |
| `recording_channels` | entero | Cupo de cámaras grabadas en el servidor (cómo cobra el mercado) |
| `module_evidence` | bool | Evidencias, expedientes, custodia y exportación verificable |
| `module_evidence_sharing` | bool | Compartir por enlace (o incluido en `module_evidence`: decisión abierta) |

- Paquete Grabación + Evidencias a mejor precio: es donde está el valor.
- Pendiente decidir la relación con `module_playback`: ¿reproducir desde el servidor exige también `module_playback`,
  o queda dentro de `module_recording`?
- Regla del proyecto: definir en `LicenseFeatures` + `LicenseCatalog`, subir la versión del catálogo, Deny en las
  rutas e importar el catálogo al servidor de licencias.

## 10. Permisos y bitácora

Permisos nuevos (tentativos), cada uno validando el recurso concreto, no solo su ubicación:
- `storage.manage`: volúmenes, cuotas, umbrales;
- `recording.manage`: qué cámara graba, cómo y cuánto tiempo;
- `evidence.view`, `evidence.manage` (crear, clasificar, expedientes), `evidence.protect` (quitar protección y
  purgar);
- `evidence.share`, `evidence.share.approve`.

Bitácora (regla de 6 pasos de auditoría): categorías nuevas `recording`, `storage`, `evidence`, `share`. Lo automático
(retención, relleno) resumido; lo manual sobre evidencias, uno por uno.

## 11. Etapas

| # | Etapa | Módulo | Entrega |
|---|---|---|---|
| 1 | Base de almacenamiento | común | Volúmenes (local, RAID, iSCSI) por GUID, cuotas, índice, conciliación, motor de retención, avisos |
| 2 | Grabación continua | Grabación | Grabar por cámara, reproducción y descarga rápida desde el servidor |
| 3 | Evidencias v1 | Evidencias | Copia desde el servidor **o el equipo**, huella, tipos con plazo, período de gracia, custodia |
| 4 | Expedientes | Evidencias | Video + eventos del sistema, informe de custodia PDF, exportación verificable |
| 5 | Grabación por evento y evidencias automáticas | ambos | Búfer de pre-alarma, acciones de automatización |
| 6 | Compartir por enlace (nivel 1) | Evidencias | Comparticiones con código, límites, marca de agua, custodia; exposición A o B |
| 7 | Integridad encadenada y restricción por evidencia | Evidencias | Cadena de huellas firmada, acceso por evidencia |
| 8 | Relleno desde el equipo y grabadores separados | Grabación | Backfill tras cortes; grabadores aparte con respaldo |
| 9 | Censura, transcripción, DRM (nivel 2) | Evidencias | Según demanda |
| 10 | Equipo llave en mano | producto | Servidor preinstalado y endurecido, calculadora de capacidad |

Las etapas se alternan para que cada entrega le sirva a quien tenga uno u otro módulo.

## 12. Dimensionamiento

- 1080p H.264 a ~4 Mbps ≈ **43 GB por cámara al día**; 30 días ≈ **1,3 TB por cámara**. H.265 ≈ la mitad.
- 64 cámaras a 4 Mbps ≈ 256 Mbps ≈ 32 MB/s de escritura: cabe en un RAID de discos mecánicos y en iSCSI por 1 GbE con
  margen; sobre ~150–200 cámaras conviene 10 GbE.
- El límite es la capacidad, no la velocidad ni el procesador (no se recodifica). La excepción son las copias de
  visualización de la compartición, que sí recodifican (clips cortos).

## 13. Riesgos

- Reinicios y recargas de MediaMTX cortan la grabación: huecos visibles y avisados.
- Rutas siempre conectadas: probar el límite de sesiones de cada marca de cámara.
- Volumen iSCSI que desaparece: detección y respaldo local.
- Hora del servidor: manda en las marcas (NTP ya existe).
- H.265 en el panel web: mismas limitaciones que el vivo (la compartición lo evita con H.264).
- La compartición es superficie expuesta a Internet: revisión de seguridad propia.

## 14. Decisiones abiertas

1. Duración por omisión de los tramos de grabación continua: **15 o 60 min**.
2. Compartir por enlace: **dentro de `module_evidence` o licencia aparte**.
3. Exposición de la compartición: **A (servidor del cliente), B (portal CLRobotics) o A primero y B después**.
4. Relación entre `module_recording` y `module_playback`.
5. Cupo de Evidencias: sin cupo, por espacio o por usuarios.
6. Si la aprobación de un supervisor para compartir es opcional por cliente u obligatoria.
