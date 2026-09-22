# CLRobotics TrueCentral VMS — Plan de implementación

VMS cliente-servidor multimarca (competencia HikCentral / SmartPSS).
Solución **nueva** e independiente; reutiliza módulos probados copiados desde `..\vwcontroller` (CLRTrueCentral videowall v1.1.0).

## Decisiones confirmadas

1. **Solución nueva** en este directorio. Prefijo de proyectos `TrueCentralVms.*` para no colisionar con el producto videowall.
2. **Plano de media = MediaMTX embebido** (proceso hijo administrado, v1.20.0 pineada):
   - 1 pull RTSP por cámara/canal bajo demanda → N clientes (fan-out nativo de MediaMTX vía `sourceOnDemand`).
   - Seguridad: auth externa (`authMethod: http`) delegada al servidor; tokens de sesión de corta vida en la URL RTSP; API de control en 127.0.0.1 con credenciales aleatorias por arranque; RTMP/HLS/WebRTC/SRT deshabilitados en v1.
   - Los SDK (Hik/Dahua) se usan para gestión (validar credenciales, info del equipo, canales, snapshots), no para transporte de video.
3. **Cliente WPF** (net10.0-windows, x64) con **FlyleafLib** (FFmpeg) para grillas multicámara.
4. **Alcance v1 = núcleo**: panel web admin + mantenedores de dispositivos con validación de credenciales + live view multicámara en cliente. Playback, videowall, live web y eventos quedan para después (la arquitectura no los bloquea).

## Matriz de puertos (coexiste con vwcontroller en la misma máquina)

| Servicio | Puerto | Bind |
|---|---|---|
| HTTP / API / SignalR / panel web | **5090** | 0.0.0.0 |
| PostgreSQL embebido | **25490** | 127.0.0.1 |
| MediaMTX RTSP (clientes) | **8654** | 0.0.0.0, solo TCP |
| MediaMTX API control | **9911** | 127.0.0.1 |

(vwcontroller usa 5080 / 25480 / 8554.)

## Estructura

```
CLRTrueCentralVMS.slnx
VERSION (0.1.0) · Directory.Build.props (lee VERSION) · .gitignore
Resources\            # SDKs de fábrica (git-ignored)
native\hikvision\     # DLLs HCNetSDK 6.1.9.48 (desde Resources\...\EN-HCNetSDKV6.1.9.48...\lib)
native\dahua\         # DLLs NetSDK V3.061 (desde Resources\...\Bin)
tools\postgres\pgsql\ # copiado de vwcontroller
tools\mediamtx\       # copiado de vwcontroller (v1.20.0)
tools\ffmpeg\bin\     # para FlyleafLib (cliente)
src\
  TrueCentralVms.Core\              # contratos: DTOs, IDeviceDriver, DriverRegistry, VmsHubContract
  TrueCentralVms.Server\            # ASP.NET Core minimal API, x64
  TrueCentralVms.Client\            # WPF
  TrueCentralVms.Drivers.Hikvision\ # interop copiado de vwcontroller + DLLs 6.1.9.48
  TrueCentralVms.Drivers.Dahua\     # interop P/Invoke propio (~600 líneas, subset de gestión)
  TrueCentralVms.Drivers.Onvif\     # SOAP a mano sobre HttpClient (WS-UsernameToken)
```

## Contratos Core

`IDeviceDriver`: `ConnectAsync` (login SDK/ONVIF) · `GetDeviceInfoAsync` (modelo, serie, firmware, canales) · `EnumerateChannelsAsync` · `BuildRtspUrl(channel, profile, rtspPort)` · `CaptureSnapshotAsync`. Factories con clave estable en BD: `hikvision-netsdk`, `dahua-netsdk`, `onvif` (patrón `DriverRegistry` de vwcontroller).

Hub SignalR `/hubs/vms`, eventos servidor→cliente: `DeviceStatusChanged(DeviceDto)`, `ConfigChanged(entity)`, `SessionsChanged(ActiveSessionDto[])`.

## Seguridad de cuentas (requisitos confirmados)

- **Arranque desactivado**: sin usuarios en BD el servidor está "no inicializado"; el único flujo habilitado es `GET /api/setup/status` + `POST /api/setup/admin`, y este último **solo acepta conexiones desde loopback (127.0.0.1/::1)**. Crea el primer admin y entrega sesión.
- **Política de contraseñas** (compartida en Core para reuso en cliente): mínimo 8 caracteres, ≥1 mayúscula, ≥1 minúscula, ≥1 número, ≥1 carácter especial. Aplica a setup, creación de usuarios y cambios de clave.
- **Historial**: tabla `PasswordHistory` (userId, hash, salt, fecha). Toda clave nueva se verifica contra el historial completo del usuario — no se puede reutilizar ninguna clave anterior.
- **Caducidad configurable**: `Security:PasswordMaxAgeDays` en appsettings (0 = deshabilitada). Vencida, el login responde 403 `mustChangePassword` y `POST /api/auth/change-password` (usuario + clave actual + nueva) permite renovarla aplicando política + historial.

## Base de datos (EF Core + Npgsql, **migraciones reales** desde el día 1)

- **User**: username único, PBKDF2-SHA256 100k, rol Admin/Operator, PasswordChangedAt.
- **PasswordHistory**: (userId FK, hash, salt, createdAt) — impide reutilización de claves.
- **Device**: tipo (Camera/Dvr/Nvr/Xvr), driverKey, host, sdkPort, rtspPort, username, **PasswordCiphertext (AES-256-GCM, llave local `tcvms-data.key` junto a pgdata)**, modelo/serie/firmware, status. Índice único (host, sdkPort).
- **Channel**: (deviceId, channelNumber) único, nombre, enabled. Se autopobla al agregar el equipo.
- **StreamSession**: auditoría de quién vio qué (usuario, device, canal, perfil, IP, inicio/fin).

## API REST (resumen)

`POST /api/auth/login|logout` · `GET /api/health` · `GET /api/drivers` · CRUD `/api/devices` + `POST /api/devices/probe` (valida credenciales y devuelve info SIN persistir — botón "Probar" del wizard) + `/{id}/channels` + `/{id}/snapshot/{ch}` · `POST /api/streams/request` → `{rtspUrl, token, expira}` · `POST /api/streaming/auth` (callback MediaMTX, solo loopback) · `GET /api/streams/active` · `GET /api/discovery/scan` (SADP + DHDiscover Dahua + WS-Discovery ONVIF; `?host=IP` sondea unicast un Dahua remoto) · CRUD `/api/users`.

Auth: tokens opacos 32 bytes, 12 h, `Bearer` o `?access_token=` (SignalR) — copiado de vwcontroller (`ApiSecurity`, `TokenService`, `PasswordHasher`).

## Flujo de streaming (extremo a extremo)

1. Cliente: `POST /api/streams/request {deviceId, channel, profile}`.
2. Servidor valida y emite token (TTL 60 s) → `rtsp://servidor:8654/ch/{dev}/{ch}/{main|sub}?token=…`.
3. Cliente abre la URL con FlyleafLib (RTSP/TCP).
4. MediaMTX consulta `/api/streaming/auth` → servidor valida token → 200 → registra StreamSession.
5. Primer lector dispara el pull al equipo (`sourceOnDemand`); N lectores comparten ese único pull.
6. Sin lectores por 10 s → MediaMTX suelta el upstream. `SessionAccounting` sondea la API de MediaMTX cada 5 s para cerrar sesiones y refrescar el dashboard.

Las credenciales de los equipos solo existen dentro del `mediamtx.runtime.yml` generado en runtime (ACL restrictiva, nunca servido, git-ignored).

## Drivers

- **Hikvision**: copiar `CHCNetSDK.cs` (15.662 líneas) + `HikvisionSdk.cs` de vwcontroller; DLLs 6.1.9.48. Login `NET_DVR_Login_V40` → `NET_DVR_DEVICEINFO_V40` (serie, canales) → `NET_DVR_GetDVRConfig(DEVICECFG_V40)` (modelo/firmware) → snapshot `NET_DVR_CaptureJPEGPicture`. RTSP: `/Streaming/Channels/{ch}0{1|2}`.
- **Dahua**: interop mínimo a mano contra `Resources\...\Include\Common\dhnetsdk.h`: `CLIENT_Init/Cleanup`, `CLIENT_LoginWithHighLevelSecurity` (→ `NET_DEVICEINFO_Ex`: serie, canales), `CLIENT_QueryDevState`/`GetDevConfig` (versión), `CLIENT_SnapPictureEx`, `CLIENT_GetLastError`. Reglas x64: `LDWORD`=IntPtr, ANSI, delegates en campos estáticos. RTSP: `/cam/realmonitor?channel={ch}&subtype={0|1}`.
- **ONVIF**: SOAP manual (`GetSystemDateAndTime` → corrección de reloj, `GetDeviceInformation`, `GetProfiles`, `GetStreamUri`, `GetSnapshotUri`). 1 dispositivo = 1 canal en v1.

## Cliente WPF

Login (URL servidor + credenciales) → MainWindow: árbol dispositivos/canales (íconos por estado), grilla 1/4/9/16, drag & drop de canal a celda, `VideoCellViewModel` dueño de un `Player` Flyleaf (dispose al limpiar/cerrar), re-pide grant en cada reconexión. >9 celdas usa perfil sub por defecto. SignalR con reconexión automática. Paquetes: FlyleafLib, SignalR.Client, CommunityToolkit.Mvvm.

## Panel web (vanilla JS, sin build, patrón vwcontroller, español de Chile, tema oscuro #0F141A/#171F28/#E6EDF3)

`#/login` · `#/` dashboard (salud, sesiones activas) · `#/devices` mantenedor con wizard (dropdown de driver → formulario → **Probar conexión** muestra modelo/serie/firmware/canales antes de Guardar) + thumbnails snapshot · `#/users` · `#/sessions` (M5).

## Milestones (cada uno termina ejecutable y verificable)

- **M1 — Esqueleto + PG embebido + auth + login web**: ✅ **COMPLETO** (2026-08-27). PG en 25490 solo loopback, migraciones EF reales, setup inicial solo-localhost, política+historial+caducidad de claves, panel login/dashboard/usuarios verificado.
- **M2 — CRUD dispositivos + driver Hikvision + mantenedor web**: ✅ **COMPLETO** (2026-08-27). Contratos IDeviceDriver/DriverRegistry, interop CHCNetSDK 6.1.9.48 cargando OK, Device/Channel + migración, CredentialProtector AES-GCM, probe→info→canales→persistir, snapshots con cache 25 s y throttle 4, DeviceStatusMonitor TCP 30 s, wizard web con "Probar conexión". Verificado por API y DOM; **pendiente prueba con equipo Hikvision real** (probe contra IP inalcanzable devuelve error SDK 7 en español en 5 s).
- **M3 — MediaMTX + grants + cliente WPF MVP**: ✅ **COMPLETO** (2026-08-27). MediaMTX v1.20 embebido como proceso hijo (132 rutas generadas, reinicio automático ante crash), tokens de streaming 60 s con auth delegada (`authMethod: http`), auditoría de sesiones con cierre por sondeo de la API MediaMTX y dedupe DESCRIBE/PLAY por token. **Verificado E2E contra el DVR real**: 2 lectores ffmpeg simultáneos → `readers=2` con UN solo `rtspSource` hacia el equipo; URL sin token rechazada. Cliente WPF: FlyleafLib 3.11.3 + FFmpeg 8 compartido (release oficial Flyleaf en `tools\ffmpeg-flyleaf`), login, árbol dispositivos/canales con SignalR, grilla 1/4/9/16, perfil main≤4/sub>4, celda pide grant nuevo en cada apertura. Pendiente de M5: audio por celda, drag&drop, reconexión automática de celdas.
- **M4 — Dahua + ONVIF + snapshots + discovery SADP**: ✅ **COMPLETO** (validado con hardware real 2026-08-29). Driver Dahua con interop propio verificado contra dhnetsdk.h (login alta seguridad, GetDeviceType→modelo, GetSoftwareVersion→firmware, QueryChannelName, snapshot por CGI HTTP digest); driver ONVIF con SOAP manual (WS-UsernameToken + digest HTTP + corrección de reloj, GetStreamUri → URLs guardadas por canal e inyección de credenciales en MediaMTX); SADP copiado + `/api/discovery/sadp` + botón "Buscar en la red" en el panel. Canales reportados como deshabilitados/sin cámara por el equipo entran ocultos automáticamente (pedido del usuario). Snapshots ya estaban desde M2. **Validación real**: cámara Dahua DH-IPC-HDW2449T-S-PRO (firmware 3.120) vía VPN — ambos drivers E2E (probe, alta, snapshot y video HEVC 1080p por MediaMTX); los firmwares Dahua nuevos exigen digest HTTP en ONVIF y usan una cuenta ONVIF separada de la web/SDK.
- **M5 — Pulido**: ✅ **COMPLETO** (2026-08-28). Página web `#/sessions` (sondeo 5 s) + tarjeta en dashboard + `POST /api/streams/{id}/kick` (corta la sesión RTSP vía API de MediaMTX; verificado E2E: el kick cierra la sesión y la celda del cliente se reconecta sola con una concesión nueva en <15 s). Cliente: reconexión automática de celdas (PlaybackStopped/OpenCompleted → reintento cada 5 s mientras la celda siga asignada), audio por cuadro exclusivo (Config.Audio.Enabled en caliente), drag & drop árbol→cuadro (drop aceptado también en las ventanas Surface/Overlay de Flyleaf), divisiones de pantalla estilo iVMS-4200 (1/4/6/8/9/13/16/25/36/64 con VideoLayoutPanel propio), buscador del árbol (sin tildes), selección grilla→árbol sincronizada, panel PTZ minimizable, indicadores CPU/RAM/disco del servidor en el navbar (GET /api/system/metrics, umbrales 70/90) y logo del producto (compartido con vwcontroller) + barra de título propia. README.md del repo. (El reinicio de MediaMTX ante crash quedó hecho en M3.)
- **M6 — Reproducción de grabaciones (remota)**: ✅ **COMPLETO** (2026-08-30). Las grabaciones viven en el disco del propio DVR/NVR (o en la tarjeta de la cámara): el VMS no almacena video, lo consulta y lo reproduce por el mismo camino del vivo (MediaMTX + tokens), sin que las credenciales del equipo lleguen jamás al cliente. `IDeviceDriver` suma `QueryRecordingsAsync`, `BuildPlaybackUrl` y `SupportsExactPlaybackSeek`. **Hikvision** por SDK (`NET_DVR_FindFile_V40` + `/Streaming/tracks/{canal·100+1}?starttime=…`, verificado E2E contra el DVR real); **Dahua** por NetSDK (`CLIENT_FindFile`/`CLIENT_FindNextFile` con sesión cacheada + `/cam/playback?channel=N&starttime=YYYY_MM_DD_HH_MM_SS`); **ONVIF Perfil G** (`GetServices` → servicios Search/Replay: `GetRecordings`/`FindRecordings`, `GetRecordingInformation` y `GetReplayUri`, con corrección UTC→hora local del equipo). Límite del estándar ONVIF: solo informa el rango total de cada grabación (sus tramos se pintan como "Otra") y posicionarse exige la cabecera RTSP `Range: clock=…` que el media server no envía, así que el equipo reproduce desde el inicio de la grabación y el cliente lo avisa. Servidor: `GET /api/playback/{dev}/{canal}/segments?date=`, `POST /api/playback/request` (ruta dinámica en MediaMTX por su API de control + token corto) y `GET /api/playback/{dev}/{canal}/download?start=&end=` (exporta el tramo a MP4 con FFmpeg —video copiado sin recodificar, audio a AAC— enviándolo mientras el equipo lo entrega). Cliente: módulo Reproducción con hasta **4 canales sincronizados** (una pista por canal en la línea de tiempo de 24 h; todos abren el mismo instante), saltos de ±30 s y ±5 min, velocidad 0,25×–8×, y arrastre sobre la línea para marcar un tramo, reproducirlo o exportarlo a MP4 con progreso, cancelación y notificación flotante al terminar. **Pendiente de validación con hardware**: Dahua y ONVIF (Hikvision ya verificado E2E).

- **M7 — Muro de video**: ✅ **COMPLETO** (2026-08-30). Módulo portado desde `vwcontroller` (que sigue existiendo como producto aparte) e integrado en el VMS: `IDecoderDriver`/`IDecoderDiagnostics`/`DecoderDriverRegistry` en Core (el registro se renombró para no chocar con el de drivers de dispositivo), `HikvisionDecoderDriver` + `WallInterop` en el driver Hikvision (el `CHCNetSDK.cs` de ambos productos resultó idéntico, así que entró sin conflictos), entidades `Decoder`/`VideoWall`/`WallScreen`/`ScreenWindow`/`WallFloatingWindow`/`WallLayoutPreset` + migración `VideoWall`, `DecoderSessionManager` (una sesión viva por equipo, contraseña descifrada al vuelo) y `WallService` (asignar, limpiar, intercambiar, dividir, agrupar, flotantes, pantalla completa por monitor y de muro completo, layouts y sincronización). **Cambio de fondo respecto del original**: las ventanas se asignan a **canales del inventario del VMS** (`AssignedChannelId`), no a un registro de fuentes propio — Hikvision viaja por protocolo privado y el resto por la URL RTSP del driver del dispositivo; el decodificador se conecta directo al equipo (las concesiones de MediaMTX son de 60 s, un muro decodifica días). La proyección de pantalla del operador, que en el original creaba una "fuente" ficticia, se modeló como **fuente externa por URL** (`ExternalUrl`/`ExternalLabel` en la ventana). Panel web: `#/decoders` y `#/walls` (`wwwroot\walls.js`). Cliente: módulo `WallView` con sus estilos propios (`WallStyles.xaml`, fusionados solo en el módulo para no alterar el resto de la aplicación), viñeta en el navbar y tarjeta de inicio. Al eliminar un dispositivo, el servidor apaga antes sus ventanas en el muro. **Pendiente**: validación contra el decodificador real.

- **M8 — Aplicaciones · Reconocimiento de patentes**: ✅ **COMPLETO** (2026-08-30). Apartado nuevo **Aplicaciones** en el menú principal del cliente (tarjeta en Inicio, viñeta en el navbar y botón en el riel, separados de los módulos de video) con el módulo **Reconocimiento de patentes**. El reconocimiento lo hace la cámara: el VMS solo mantiene abierto su canal de eventos. Core suma `PlateRecognition`/`IPlateSubscription` y los miembros opcionales `SupportsAnpr`/`SubscribePlatesAsync` de `IDeviceDriver` (mismo patrón que la reproducción remota de M6). Driver Hikvision: `HikvisionAnpr` (callback global `NET_DVR_SetDVRMessageCallBack_V30` + `NET_DVR_SetupAlarmChan_V41` con `byAlarmInfoType=1`, enrutado por `lUserID`; agrupa por `dwMatchNo` los mensajes de una misma pasada para entregar un solo evento con todas sus fotos) y `ItsText` para traducir los códigos del SDK. Servidor: entidad `PlateEvent` + `Device.AnprEnabled` + migraciones `Anpr`/`AnprVehicleAttributes`, `AnprStore` (fotos en disco junto a pgdata; la base solo lleva la ruta), `AnprService` (reconcilia suscripciones cada 20 s, cola acotada entre el hilo del SDK y la persistencia, purga por antigüedad y volumen) y `AnprApi` (`/api/anpr/events`, fotos `scene`/`plate`, `/api/anpr/sources`). Hub: `PlateRecognized`. Panel web: columna *Patentes* en `#/devices`.

  **Hallazgo importante**: el `CHCNetSDK.cs` heredado trae las estructuras ITS de un SDK anterior — `NET_DVR_PLATE_INFO` sin diez campos y dos punteros previos a `sLicense`, y `NET_ITS_PICTURE_INFO` sin cuatro—, así que desempaquetar con ellas corre todos los campos (patentes con caracteres fantasma, velocidades de cuatro dígitos, cero imágenes). Se agregó `Interop\ItsInterop.cs` calcado de `incEn\HCNetSDK.h` 6.1.9.48, verificado por tamaño (944/1024/96/48/104/20 bytes) contra la cabecera. La hora de captura se toma del `byAbsTime` de la propia foto (coincide con el rótulo que quema la cámara); `struSnapFirstPicTime` llega en cero en este firmware.

  **Validación real** contra la cámara **DS-2CD7A26G0/P-IZS** (firmware V5.6.10) de 192.168.10.52: canal de eventos abierto, eventos recibidos, escenas JPEG completas y hora exacta. **Pendiente**: ver una patente efectivamente leída (en las pruebas, de noche y con lluvia, la cámara informó `noPlate` en todas las pasadas) y el recorte de la placa, que solo viaja cuando hay lectura.

## Velocidad de reproducción remota (medido contra el DVR real, 2026-08-31)

El grabador pacea la entrega a tiempo real, así que por el camino actual
(cliente ← MediaMTX ← equipo) ninguna velocidad mayor a 1× puede funcionar:
no hay más video llegando. Medido con `RtspRateProbe` sobre el DVR CLRobotics
(DS-9632NI-I16), ventanas de 10 s, midiendo el tiempo de video recibido según
las marcas RTP:

| PLAY | factor real |
|---|---|
| sin pedir nada | **0,98×** |
| `Scale: 4.000` | **3,93×** |
| `Rate-Control: no` | **8,44×** |

O sea el equipo **sí honra** `Scale` y `Rate-Control` (aunque no los devuelve
en la respuesta). Lo que bloquea la función es que MediaMTX pulsa el equipo
con su propio cliente RTSP y no envía esas cabeceras.

Para que la velocidad funcione el servidor tiene que ser dueño de la sesión
RTSP contra el equipo: cliente RTSP propio (ya escrito para la medición) que
hace PLAY con el `Scale` pedido y reenvía el RTP a MediaMTX, y el cliente WPF
acompaña con `Player.Speed` igual — el equipo manda los cuadros más rápido y
el reproductor los presenta más rápido. Cambiar de velocidad es un PLAY nuevo
sobre la misma sesión.

Nota de implementación del cliente RTSP: estos DVR ofrecen **Basic** (no
digest) sobre RTSP, y `System.Uri` NO puebla `UserInfo` para el esquema
`rtsp`, así que las credenciales y la URL limpia se sacan de la cadena.

## Paneles de alarma (centrales de intrusión)

Módulo aparte de los dispositivos de video: un panel tiene **áreas** (particiones) y **zonas** (detectores), no canales. Entidades `AlarmPanel` / `AlarmArea` / `AlarmZone` / `AlarmEvent` (migración `AlarmPanels`; el historial no tiene FK al panel para sobrevivir a su eliminación).

- **Driver** `IAlarmPanelDriver` en Core (probe, estado, armar/desarmar/borrar alarma, bypass, suscripción a eventos, traducción del push) + `AlarmDriverRegistry`. Implementación **Hikvision AX PRO / AX Hybrid por ISAPI** (`hikvision-isapi`, sin HCNetSDK): `HikvisionIsapiClient` elige la autenticación una sola vez — si el equipo ofrece login de sesión, entra por ahí de entrada (los AX vinculados a Hik-Connect rechazan el Digest y **cada rechazo descuenta un intento** hasta bloquear la IP), y en modo sesión las peticiones salen solo con la cookie. El reto SHA-256 iterado usa **doble salt** cuando el panel entrega `salt2` (SHA256(user+salt+clave) → SHA256(user+salt2+x) → SHA256(x+challenge) → SHA256^n, leído del `encodePwd` del firmware; sin el paso salt2 el panel responde 401 aunque la clave sea correcta). Conexiones cacheadas por panel+usuario. Endpoints: `/ISAPI/SecurityCP/status/subSystems|zones?format=json`, `Configuration/subSys|zones` (nombres), `control/arm/{id}?ways=away|stay&format=json`, `control/disarm/{id}`, `control/clearAlarm/{id}` (`0xffffffff` = todas; si el firmware rechaza el comodín se manda el lote `{"SubSysList":[{"SubSys":{"id":n,"armType":...}}]}` con las áreas habilitadas, como hace la web del panel), `control/bypass|bypassRecover/{zona}` (forma verificada con hardware; si no existe se prueban los lotes `{"List":[{"id":n}]}` y `{"BypassCtrl":{"zoneIds":[n]}}`). El mismo driver cubre la **línea clásica de paneles híbridos cableados** (DS-PHA64-W4M, firmware V1.3.x, web antigua `/doc/page/config.asp`): acepta Digest y sesión con un salt, marca las zonas `shielded` (deshabilitadas en el panel) como no configuradas, y su alertStream late con un `cidEvent` vacío cada 2 s (se ignora) pegando el delimitador multipart al final del cuerpo (el lector lo tolera); fecha con desfase de un dígito (`-4:00`, se normaliza). Eventos por dos vías: el multipart `/ISAPI/Event/notification/alertStream` y la **notificación HTTP** que el panel empuja al VMS (el AX Hybrid PRO real deja mudo el alertStream, así que la notificación HTTP es la vía de tiempo real). Los códigos Contact-ID se traducen con `ContactIdCatalog` (Core, compartible entre marcas).
- **Driver vía Hik IP Receiver Pro** (`hikvision-iprp`, `HikvisionIpReceiverDriver.cs`, 2026-09-02): el VMS no habla con el panel sino con la pasarela de Hikvision (IPRP) por su API REST ("Hik IP Receiver Pro API Developer Guide V2.5.0"), **Digest** con la cuenta de la pasarela (`HikvisionIsapiClient` con `digestOnly`: la sesión web entra pero no habilita la suscripción). `AlarmConnectionInfo.DeviceId` / `AlarmPanel.GatewayDeviceId` (migración `AlarmPanelGatewayDeviceId`) identifican al equipo dentro de la pasarela: uuid, serie, cuenta (accountID), ID ISUP/OTAP o nombre; se resuelve buscando en `POST /ISAPI/ContentMgmt/DeviceMgmt/deviceList` (SearchDescription/SearchResult) y se cachea 5 min. `IAlarmPanelDriverFactory.NeedsDeviceId` → el mantenedor web muestra el campo; con el campo vacío la prueba de conexión responde listando los equipos de la pasarela. Estado: `GET status/subSystems?format=json&devIndex=` y `POST status/zones` paginado (`ZoneCond` → `ZoneSearch`), reutilizando los traductores del driver directo (ahora `internal`); la pasarela no expone nombres de área. Órdenes: mismas rutas SecurityCP + `devIndex` (`0xffffffff` = todas; bypass por lote `{"List":[{"id":n}]}`). Eventos: `POST /ISAPI/Event/notification/subscribeDeviceMgmt` (multipart con TODOS los equipos; se filtra por devIndex; primera parte = `SubscribeDeviceMgmtRsp`; latido cada `PrivateParams.heartInterval` s): `CIDAlarm` (CIDCode → `ContactIdCatalog`, `subSys`, `zoneNo`, `CIDParam` = userType,userNo,zoneNo,…,userName → operador) y `devStatusChanged` (panel en línea/fuera de línea respecto de la pasarela, códigos R350/E350). Requisito en la pasarela: `PUT /ISAPI/System/ProtocolMangement/ProtocolParams` con el documento **entero y envuelto**, tal como lo devuelve el GET (`{"ProtocolParams":{"deviceHeartbeatInterval":30,"enabled":true,"protocolType":"Private"}}`, `application/json`), que es Automation Output → Protocol; sin eso el API contesta 403 "Invalid operation" — desde el 2026-09-11 **lo deja puesto el sistema solo**, ver la sección de la receptora. Equipo fuera de línea en la pasarela → 503 "Device Busy/networkError", traducido a un mensaje claro. Uniqueness de paneles = dirección+puerto+equipo. Validado contra el IPRP V2.5.0.6 local (127.0.0.1): listado, resolución por ID ISUP, suscripción abierta, traducción de mensajes de la guía; pendiente un panel en línea en la pasarela (el DS-PHA64-W4M se registra por ISUP en 192.168.1.38:7660 con ID `PA02`, así que en la pasarela local figura fuera de línea).
- **Servidor** `AlarmPanelService`: por panel habilitado mantiene el `alertStream` abierto y sondea el estado (`Alarms:PollSeconds`, 30 s) y además apenas llega un evento; la diferencia contra la base genera eventos por lo que el panel no empujó (deduplicados contra lo ya informado por el panel o por una orden del VMS en una ventana de 15 s). Credenciales rechazadas → reintento recién a los 10 min (insistir bloquea el panel). Estado y eventos se empujan por el hub (`AlarmPanelStateChanged`, `AlarmEventReceived`). API `/api/alarms/*` (CRUD admin; armar/desarmar/bypass cualquier usuario con sesión); todo auditado en la categoría `alarms`, incluidas las alarmas recibidas del panel (`alarm-received`, actor `panel`) y los rechazos del equipo (`success=false`). **Notificación HTTP del panel**: `POST /api/alarms/push` (anónimo, aceptado solo desde la IP de un panel habilitado) → `NotifyPushed`: registra el evento si el driver lo entiende y siempre relee el estado (el cuerpo del POST no se toma como verdad). Se configura en el panel apuntando su httpHost a `http://<servidor>:5090/api/alarms/push`. **Centro receptor de alarmas (ARC) propio**: `AlarmReceiverService` escucha por TCP (`Alarms:Receiver`, puerto 5091) tramas **SIA DC-09** (`Core\Drivers\SiaDc09.cs`, independiente de la marca: parser con CRC-16 y largo, ACK/NAK/DUH, ADM-CID vía `ContactIdCatalog` y SIA-DCS con catálogo propio). Es la vía por la que el panel reporta TODO con confirmación y reintentos (alarmas, armados con usuario, bypass, fallas, test periódico), sin depender del alertStream ni de un httpHost libre. Se acepta solo desde la IP de un panel habilitado (NAK + auditoría acelerada `arc-rejected` al resto), se contesta ACK antes de procesar, el evento entra por `NotifyPushed` (historial + relectura del estado) y los latidos `NULL` solo marcan actividad (`NotifyAlive`). Cifrado (`*ADM-CID`) no soportado → DUH. `ZoneNumbersStartAt` (1 en Hikvision) convierte la zona Contact-ID al id de la API; en los eventos 4xx el tercer campo es el usuario (operador). En el panel Hikvision: Alarm Receiving Center → Tcp/IP, protocolo ADM-CID, IP del servidor, puerto 5091, cuenta de 3-16 dígitos, TCP.
- **Cliente WPF**: módulo "Paneles de alarma" (riel, viñeta, tarjeta de inicio): lista de paneles, áreas con Armar total / Parcial / Desarmar / Silenciar, zonas con estado y Anular/Restituir, flujo de eventos en vivo con filtros; una alarma crítica avisa con toast y enciende (pulsando) el icono del riel aunque la viñeta esté cerrada.
- **Panel web** `#/alarm-panels` (Dispositivos → Paneles de alarma): mantenedor con "Probar conexión" y detalle desplegable con las mismas órdenes.
- **Validación**: driver y flujo completo probados contra un panel AX PRO **simulado** (`scratchpad\mockpanel`, digest y sesión) y **verificado contra el panel real** (2026-09-01): **DS-PHA64-LP (AX Hybrid PRO), firmware V1.1.2, HTTPS 443, cuenta local `installer`** (la `admin` web queda interceptada por Hik-Connect). Probe (modelo/tipo/firmware), estado (1 área activa + 8 zonas reales con su tipo/detector), reutilización de sesión y traducción del push CID 1130→alarma crítica: todo OK. **Segundo panel real (2026-09-02): DS-PHA64-W4M (línea clásica), firmware V1.3.3, HTTP 80, cuenta `admin`**: probe, estado (2 áreas «CE Oriente»/«Sensores» + 4 zonas cableadas, 60 deshabilitadas), reutilización de sesión, **bypass y restitución de zona verificados en vivo**, alertStream abierto y leído (latidos), latido `cidEvent` ignorado y CID 1130 traducido con hora correcta. Formatos de las órdenes de armado tomados del JS de la web del panel (`deviceStatus/subSystem/subSystemStatus.js`), no ejecutados para no armar una instalación en uso. Pendiente aún con hardware: armar/desarmar en vivo, una alarma real de intrusión E2E (en el W4M el bypass no salió por el alertStream, así que conviene configurar también el httpHost → `/api/alarms/push`; el equipo admite 2 hosts y el 1 ya está ocupado por EHome).

## Automatizaciones (workflows)

Módulo transversal: los demás módulos **publican** lo que ocurre y el motor decide qué hacer. Entidades `Workflow` / `WorkflowAction` / `WorkflowRun` / `SmtpSettings` (migración `Workflows`; el historial no tiene FK al workflow para sobrevivir a su eliminación).

- **Motor** `WorkflowEngine` (`Services\Workflows`): `Publish(WorkflowTrigger)` solo encola en un canal acotado — quien publica (el hilo que atiende la alarma) nunca espera a un FTP lento. Un consumidor único evalúa las condiciones contra las automatizaciones habilitadas que mantiene en memoria (`Invalidate()` desde la API al guardar) y lanza cada ejecución con un tope de simultaneidad (`Workflows:MaxConcurrentRuns`). Cada acción corre con su propio tope de tiempo (`ActionTimeoutSeconds`) y puede tener espera previa y política "seguir si falla". Tiempo mínimo entre ejecuciones por workflow (anti-avalancha). Purga del historial y de las fotos por `RetentionDays`/`MaxRuns`.
- **Editor visual y diagrama de flujo** (2026-09-11): la automatización dejó de ser "disparador + lista de acciones" y pasó a ser un **grafo** (`Workflow.GraphJson` = nodos con posición + conexiones con puerto; migración `WorkflowGraph`), armado en un editor de lienzo del panel web (`wwwroot/workflow-editor.js`, ruta `#/workflows/edit?id=N`: paleta a la izquierda, lienzo SVG+HTML con arrastre/zoom/desplazamiento al centro, panel de propiedades a la derecha; menú al hacer clic en un puerto de salida para agregar-y-conectar el paso siguiente). Tipos de nodo (`WorkflowNodeKinds`): `trigger` (uno, lleva el filtro = `ConditionsJson`), `condition` (sí/no, mismas condiciones que el filtro), `delay`, `action`, `end`; puertos (`WorkflowPorts`): `next`, `yes`, `no`, `error`. La configuración de cada acción sigue en su fila `WorkflowAction` (contraseña cifrada) enlazada por `NodeId`; `WorkflowGraph` (server) valida el diagrama (un solo disparador, nada suelto, puertos válidos por tipo, ≤ 60 pasos, ciclos tolerados con tope de 200 pasos por ejecución) y lo vuelca/reconstruye; sin `GraphJson` (automatizaciones viejas) se ejecuta como línea recta y "seguir si falla" se traduce en una conexión desde `error`. El motor recorre el grafo recursivamente (`WalkAsync`): las ramas que salen de un mismo puerto corren en secuencia, en el orden de las conexiones, compartiendo las fotos capturadas. Cada paso queda en el historial con su `NodeId`, y el botón **Probar** del editor pinta cada tarjeta en verde/rojo con el resultado. El router del panel (`app.js`) ahora admite parámetros en el hash y marca el enlace del menú en las subrutas.
- **Disparadores** (`WorkflowTriggerTypes`): `alarm-event` (todo evento de panel, con sus nombres de área y zona ya resueltos), `panel-status` (conexión perdida/recuperada), y desde 2026-09-11: `device-status` (cámara/grabador, terminal de acceso o parlante que pierde o recupera conexión; lo publican `DeviceStatusMonitor`, `AccessControlService` y `SpeakerService`, omitiendo la primera lectura Desconocido→En línea), `video-event` (analíticas y alarmas del equipo: `VideoEventService` + `IDeviceDriver.SubscribeEventsAsync`; hoy solo Hikvision por SDK —`HikvisionEvents` parsea `COMM_ALARM_V30/V40` (movimiento, pérdida de video, tapado, entradas de alarma, fallas), `COMM_ALARM_RULE` (cruce de línea, intrusión, entrada/salida de región, merodeo…, con foto), rostro, conteo, audio y desenfoque—; el servicio se suscribe SOLO a los equipos que piden las automatizaciones habilitadas (`SetWanted`, recalculado en cada `Invalidate`), descarta repeticiones por equipo/canal/tipo en `Workflows:VideoEventDebounceSeconds` (5 s) y NO persiste los eventos: quedan en el historial de ejecuciones), `plate-recognized` (lo publica `AnprService` tras guardar; filtro de lista blanca/negra con comodines y confianza mínima), `access-event` (lo publica `AccessEventService` tras guardar), `schedule` (reloj del motor: un tic por minuto si existe alguna automatización de horario, que compara `ScheduleTimes` + días) y `webhook` (`POST /api/workflows/hook/{clave}` anónimo; la clave de 40 hex la genera el servidor y vive en `Conditions.HookKey`; los campos del cuerpo JSON quedan como marcas; se audita cada llamada y, acelerado por IP, cada rechazo). Gotcha del SDK Hikvision: el callback de mensajes es ÚNICO por proceso, así que `HikvisionAlarmChannel` lo instala una vez y enruta por `lUserID` a patentes o a eventos (antes lo instalaba `HikvisionAnpr` solo). Los servicios que ahora publican resuelven el motor por `IServiceProvider` al vuelo: por constructor sería un ciclo, porque el motor contiene las acciones que usan esos mismos servicios.
- **Franjas horarias múltiples** (2026-09-15): además de la franja principal (`DaysOfWeek`/`FromTime`/`ToTime`), `WorkflowConditionsDto.TimeBands` lleva franjas adicionales (`WorkflowTimeBandDto`: días + desde/hasta); la automatización rige si se cumple CUALQUIERA (`WorkflowEngine.InSchedule` → `InBand`). Así "lunes a viernes 18:00–06:00 y fin de semana 24 h" es una sola automatización y no dos que se pisen. Reglas por franja: sin días ni horas no vale (validación en `WorkflowGraph.ValidateConditions`); solo "desde" rige hasta fin del día y solo "hasta" desde el comienzo; "24:00" vale como fin del día (`WorkflowGraph.TryParseWindowTime`), nunca como inicio; el editor web usa campos de texto hh:mm en 24 h (`wfNormalizeTime`) porque `<input type=time>` muestra AM/PM según el idioma del navegador, y lista los días de lunes a domingo (`WF_DAY_ORDER`) sin cambiar el valor guardado; una ventana que cruza la medianoche pertenece al día en que empieza, así que la noche del domingo hasta el lunes 06:00 exige marcar el domingo en la franja nocturna. El editor web (`wfeConditionsForm`) pinta cada franja adicional con su calendario y botón de quitar; el disparador `schedule` no las usa.
- **Eventos de cámaras vía DVR/NVR y destinatarios del aviso** (2026-09-22, v0.3.6): (a) el grabador solo empuja una analítica por el canal de alarma del SDK si el evento tiene enlazado «Notificar al centro de vigilancia» (`/ISAPI/Event/triggers/linedetection-<canal>`, método `center`); (b) en un grabador `NET_VCA_DEV_INFO` identifica la alarma por la IP y el canal propio de la cámara, no por el canal del grabador: `HikvisionEvents` lee `NET_DVR_GET_IPPARACFG_V40` al suscribirse y traduce (IP, canal) → canal del grabador (cámara desconocida = canal 0 y refresco en segundo plano); (c) la cola de `NET_VCA_RULE_ALARM` copiada del demo C# corría `pImage` sobre `pAppendInfo` y la foto llegaba nula (corregida según HCNetSDK.h 6.1.9, 352 bytes); (d) la acción `notify` acepta `userIds`: el aviso va solo a los grupos SignalR `user:{id}` de esos usuarios (`VmsHub.UserGroup`), la alerta guarda `RecipientUserIds`/`Recipients` y un operador solo ve las alertas dirigidas a todos o a él (el administrador, todas). Migración `WorkflowAlertRecipients`.
- **Botón de pánico por zona y parlante en bucle** (2026-09-22, v0.3.6): la acción `speaker` (inventario) acepta `command: play|stop`, `repeat: 0` = sonido del servidor en bucle hasta que una acción «Detener», el operador o el apagado lo corten (`SpeakerService.PlayServerSoundAsync` abre los canales y sigue transmitiendo en segundo plano con `StreamAsync`, sin bloquear la ejecución), y `setVolume`/`volume` para fijar el volumen del parlante antes de sonar (`SpeakerPlayRequestDto.Volume`). Para que un botón N/C en una zona funcione como pulsador: `WorkflowEngine.ApplyFastPoll` activa el sondeo rápido (Alarms:FastPollSeconds, 2 s) en los paneles de cualquier automatización con «Sensor interrumpido»/«Sensor restablecido», y esos dos eventos ya no pasan por la ventana antirrepetición de 15 s (el sondeo es su única fuente). Receta: automatización A = sensor interrumpido en la zona → parlante en bucle; B = sensor restablecido → detener; ambas con mínimo entre ejecuciones 0.
- **Biblioteca de sonidos y ganancia** (2026-09-22, v0.3.6): página `#/sounds` (menú principal, bajo Automatizaciones) con los sonidos del servidor (duración, tamaño, pico y nivel medio en dBFS medidos con `ffmpeg -af volumedetect`, `WorkflowStore.MeasureAudioAsync`) y, solo lectura, la biblioteca de cada parlante. `POST /api/workflows/audio/{name}/gain` (`WorkflowStore.ApplyGainAsync`) aplica una ganancia en dB (o sin valor: al máximo sin saturar, pico a 0,3 dB bajo 0 dBFS; nunca por encima de ese techo aunque se pida más), deja el original como WAV PCM 16 bits y reconvierte a G.711; auditado como `audio-gain`. El botón «Sonidos» de Automatizaciones lleva a la página; el buscador del listado filtra por todas las columnas.
- **Acciones nuevas** (2026-09-11): `door` (orden a puertas vía `AccessControlService.CommandDoorAsync`, auditada como `door-opened`/… con actor "sistema"), `panel` (armar total/parcial, desarmar o borrar alarma vía `AlarmPanelService.ExecuteAsync`, con operador "automatización '…'" en el historial del panel) y `ptz-preset` (ir a un preset con el driver del equipo). Las tres resuelven sus servicios dentro de `ExecuteAsync` (misma razón del ciclo). Naturaleza nueva `AlarmEventKind.ZoneTriggered` («Sensor interrumpido»): la genera `ApplyState` por diferencia cuando una zona pasa a `Triggered` sin alarma — las centrales no mandan Contact-ID con el área desarmada, así que sin esto no habría nada que automatizar en una puerta abierta fuera de horario. Los publica `AlarmPanelService` en los cinco puntos donde ya persistía o detectaba algo; agregar un disparador = construir un `WorkflowTrigger` con sus campos y publicarlo. Desde v0.3.2 el regreso a reposo es `AlarmEventKind.ZoneRestored` («Sensor restablecido»), tipo propio y no `Restore`, para que una automatización de armado/desarmado que marque "Restauración" no dispare con cada movimiento en un área desarmada; ambos ignoran el filtro de severidad.
- **Condición sostenida** (`SustainedSeconds`): durante la ventana el motor mira la zona **cada segundo** (la lee de la base, que el sondeo rápido mantiene al día) y descarta la ejecución en cuanto el sensor queda libre. Dos aprendizajes del terreno, ambos costaron un "no dispara" en vivo: (a) los detectores de movimiento NO informan la interrupción de corrido sino en **pulsos** con pausas de uno o dos segundos, así que se tolera una pausa de `Workflows:SustainedGapSeconds` (3 s) — exigir interrupción continua descartaba justo el caso de alguien dando vueltas frente al sensor; (b) el **tiempo mínimo entre ejecuciones se consume al ejecutar, no al evaluar**: marcándolo antes de la espera, cada pulso corto que después se descartaba dejaba bloqueada la interrupción larga siguiente, que era la que importaba (visto con hardware: la zona estuvo interrumpida 81 s y el motor la ignoró por un pulso ocurrido 20 s antes). Solo se admite una verificación en curso por automatización (`_verifying`), porque un detector que pulsa manda varios eventos del MISMO hecho. Como una interrupción de 5 s cabe entera entre dos sondeos de 30 s, el motor le pide al módulo de paneles **sondeo rápido** (`Alarms:FastPollSeconds`, 2 s) solo para los paneles de esas automatizaciones (`SetFastPoll`, recalculado en cada `Invalidate`); para que ese ritmo no se note, el sondeo compara una huella del estado y solo escribe filas y publica por el hub cuando algo cambió de verdad. El motor resuelve `AlarmPanelService` por `IServiceProvider` al vuelo: por constructor sería un ciclo (el servicio de paneles publica en el motor).
- **Acciones** (`IWorkflowActionExecutor`): `snapshot`, `email` (MailKit), `ftp` (FluentFTP), `http`, `speaker` y `notify` (aviso en pantalla con la foto capturada y **alarma sonora en el equipo del operador**: el cliente descarga el sonido elegido —los mismos de los parlantes, `GET /api/workflows/audio/{name}/file`— lo cachea en el temporal y lo reproduce con `MediaPlayer`; si falla, pitido del sistema). Cada una valida su propia configuración al guardar y devuelve un resultado en español para el historial. La configuración va como JSON por tipo y la contraseña, cifrada aparte (`SecretCiphertext`, AES-GCM); al editar, la clave que el panel nunca recibió se arrastra por Id. Registrar una clase nueva en `Program.cs` la hace aparecer sola en el editor web (el catálogo `/api/workflows/catalog` sale del registro de ejecutores).
- **Parlantes IP**: el modo Hikvision abre `/ISAPI/System/TwoWayAudio/channels/{id}/open`, envía el payload G.711 en trozos de 80 ms al ritmo real (los equipos descartan lo que llega de golpe) y cierra siempre. La autenticación Digest se firma a mano (`DigestAuthenticator`) porque el manejador de .NET resuelve el desafío **reenviando** la petición, imposible con un cuerpo que se transmite en vivo. Los sonidos se suben por el panel y FFmpeg los convierte a µ-law y A-law; se manda el que declare el equipo (`audioCompressionType`).
- **Alertas con acuse de recibo** (`WorkflowAlert`, migración `WorkflowAlerts`): la acción `notify` no solo empuja el aviso, lo REGISTRA. La alerta nace pendiente con su título, mensaje, severidad, foto, sonido y el resumen del disparo; la confirmación escribe usuario, hora, origen (`client`/`web`) e IP, y se publica por el hub (`WorkflowAlertAcknowledged`) para bajarla del resto de los puestos. La PRIMERA confirmación es la que vale (no se reescribe). El cliente encola las pendientes, muestra la más reciente sin cierre automático y las recupera al abrir o al reconectar (`GET /api/workflows/alerts?pending=true`); si el POST de confirmación falla, el aviso se queda en pantalla para reintentar — un acuse que no quedó registrado no sirve. La purga borra alertas confirmadas por retención y **nunca** una pendiente. Sin FK a la ejecución ni al workflow: el registro sobrevive a la purga del historial y al borrado de la automatización (el id de la ejecución se enlaza después, porque mientras las acciones corren todavía no existe).
- **Ventana de alarma del cliente** (`Views\AlertWindow`): detalle a la izquierda, pestañas a la derecha (video en vivo de las cámaras vinculadas en **grilla** con `VideoCellViewModel` por cuadro y perfil secundario, fotos con tira de miniaturas, y los pasos de la ejecución), y abajo Enterado / Silenciar / paginador. Las cámaras salen de la alerta (`ChannelIdsJson`, migración `AlertLinkedChannels`): las que capturaron foto o las que indique la acción de aviso. El video se abre solo con la pestaña a la vista y se suelta al cambiarla o cerrar (una ventana de aviso no puede quedarse con sesiones RTSP). La alarma sonora con `soundRepeat = 0` suena **en bucle** hasta que alguien confirme, silencie o cierre; `AlertSoundPlayer.Stop()` la corta desde los tres caminos, incluida la confirmación hecha en OTRO puesto (llega por el hub).
- **Centro de eventos del cliente** (`EventCenterViewModel` + `EventCenterView` + `EventCenterWindow`): el botón del riel que estaba como "próximamente" ahora abre el historial de alertas como una SECCIÓN del shell con su viñeta (200 últimas, filtro de pendientes, *Enterado* en la lista y *Ver* que abre la ventana de alarma). El botón **Ventana aparte** lo saca a una ventana independiente y al cerrarla vuelve a la viñeta: la vista es un `UserControl` reutilizado por ambos y el ViewModel es el MISMO, así que la lista no se duplica ni se desincroniza. Se refresca con los eventos del hub (`WorkflowNotification` y `WorkflowAlertAcknowledged`), sin sondeo. Las ventanas propias del módulo (alarma y centro de eventos) usan el marco del producto (`WindowStyle=None` + `WindowChrome` + barra con logo y botones `TitleBarButton`), y las pestañas/casillas usan estilos oscuros nuevos en `App.xaml` (`DarkTabControl`, `DarkTabItem`, `DarkCheckBox`) para no mostrar cromo de Windows.
- **Auditoría**: categoría `workflows` (creación/modificación/eliminación, prueba manual, ejecución correcta o con errores, **alerta confirmada por un operador**, correo configurado y probado, sonidos, consulta del historial). La ejecución automática se registra con `LogSystemAsync` (actor "sistema", origen "server") y el detalle de cada acción va en el JSON del evento.
- **Pendiente de validación con hardware**: envío real por un SMTP de producción, subida a un FTP real y reproducción en un parlante Hikvision/Axis (la lógica está verificada por prueba manual desde el panel).

## Parlantes IP (altavoces de red)

Módulo **Parlantes IP** (2026-09-02): entidad `Speaker` (migración `Speakers`; contraseña AES-GCM como los paneles), `ISpeakerDriver`/`SpeakerDriverRegistry` en Core, driver `hikvision-isapi` (`HikvisionSpeakerDriver`), `SpeakerService` (sondeo, reproducción, voz), `SpeakersApi`, página web `#/speakers`, panel **PARLANTES** en la Vista en Vivo del cliente y modo *inventario* de la acción `speaker` de las automatizaciones. Validado E2E contra un **DS-QAZ1325G1T** real (firmware V1.6.0): alta con probe, biblioteca (28 audios), reproducción por nombre, texto a voz en español, sonido del servidor por el canal en vivo, voz por WebSocket, detener, volumen, borrado y auditoría.

- **Lo que expone el equipo** (sondeado con la web del propio parlante, cuyos bundles traen las 738 rutas ISAPI): audio bidireccional `TwoWayAudio/channels/1/open → audioData (PUT largo G.711 a-law/µ-law/PCM/MP3) → close`; biblioteca JSON bajo `/ISAPI/AccessControl/EventCardLinkageCfg/CustomAudio/*` (carpetas, `SearchFolderCustomAudio`, subida multipart `CustomAudioInfo`+`audioData`, `{id}/play|stop`, `CreateTTSAudioFile` con idioma `spanish`); estado de salida en `System/Audio/AudioOut/SearchAduioOutStatus` (broadcastType 4 = archivo local); volumen en `AudioOut/channels/1`. NO tiene sincronía nativa entre parlantes (`AudioSynAssociationParams` → notSupport), ni reproducción desde URL, ni multicast fuera de ISUP; SIP estándar existe pero está apagado y no se usa. **Gotchas**: `deleteCustomAudio`/`batchDelete` responden invalidOperation, lo que borra es `DeleteFolderCustomAudio` por carpeta (raíz "1" incluida); el equipo no contesta el PUT de `audioData` hasta que llega el `close`, por eso el driver cierra primero y recién después espera la respuesta del flujo; el parlante ya estaba registrado por ISUP a un HikCentral (192.168.1.38), y un parlante admite un solo centro ISUP, así que esa vía quedó descartada.
- **Sincronía entre parlantes**: la hace el VMS. Un sonido del servidor (los mismos de Automatizaciones → Sonidos, ya convertidos a G.711) abre el canal en vivo de cada parlante elegido y manda el MISMO trozo de 80 ms a todos antes de esperar (lock-step con reloj absoluto): el desfase es el jitter de la LAN. Los audios de biblioteca y el TTS se resuelven por nombre en cada equipo y se ordenan en paralelo (arranque casi simultáneo; el TTS se genera primero en todos y recién entonces suena). Un mismo texto reutiliza el archivo (`tcvms-tts-<hash>`), porque generar voz tarda ~4 s.
- **Voz en vivo**: WebSocket propio `/api/speakers/talk?ids=1,2&access_token=` (fuera de SignalR, que no sirve para audio binario continuo). El cliente WPF captura el micrófono con NAudio (`WaveInEvent` 8 kHz/16 bits/mono, tramas de 40 ms) y lo manda tal cual; el servidor lo convierte a G.711 (`Core\Drivers\G711.cs`, sin dependencias) y lo escribe en todos los canales a la vez. Corte por inactividad (20 s) y tope de 10 min; el servidor avisa `ready`/`closed` en texto. Un parlante atiende UNA cosa del VMS a la vez (`SpeakerService.BusyOf`): voz y sonidos del servidor comparten el canal del equipo.
- **API**: `/api/speakers` (CRUD admin, probe), `/{id}/library` (GET; POST multipart y DELETE admin; `/tts` admin), `/{id}/status`, `/{id}/volume`, `POST /play` (`source` = server | library | tts; un sonido del servidor responde a los 2,5 s con "transmisión en curso" y el resultado final se audita al terminar), `POST /stop`, `/sounds`, `/tts-languages`, `/drivers`. Hub: `SpeakerStatusChanged` + `ConfigChanged("speakers")`.
- **Auditoría** (categoría `speakers`): alta/edición/borrado/prueba, play/play-failed, stop, talk-started/stopped/rejected (con segundos de audio y motivo), volumen, biblioteca (subida/borrado/TTS) y caídas/recuperaciones del sondeo.
- **Pendiente**: validar el panel del cliente WPF con el usuario (micrófono real, varios parlantes marcados) y probar la sincronía con un segundo parlante físico.

## Control de acceso — 2026-09-08 / 2026-09-09

Módulo nuevo, en dos etapas. **Etapa 1: el administrador de dispositivos.**
**Etapa 2 (2026-09-09): la operación** — puertas, horarios, niveles de acceso,
padrón de personas e historial. Queda pendiente el enganche con
automatizaciones y video.

- **Modelo**: `AccessDevice` + `AccessDoor` (migración `AccessControlDevices`),
  entidad aparte de `Device` — no tiene canales de video, tiene puertas. La
  contraseña va cifrada con AES-GCM como en paneles y parlantes. La **puerta**
  es la unidad que cuenta la licencia (`access_doors`; la prueba de 30 días pasó
  a traer el módulo activo con 4 puertas, antes venía apagado).
- **Contratos**: `IAccessControlDriver`/`AccessDriverRegistry` en Core
  (`ProbeAsync` = identificación + capacidades + puertas; `PingAsync` = sondeo
  liviano de estado), DTOs en `Contracts\AccessDtos.cs`.
- **Multimarca (2026-09-08)**: tres drivers en el registro, uno por familia de
  transporte. El mantenedor se adapta a la marca elegida porque el catálogo de
  drivers ahora declara con qué se autentica (`AccessAuthMode`) y una ayuda
  para el formulario:
  - `hikvision-isapi` — ISAPI sobre HTTP, usuario y contraseña.
  - `dahua-http` (`DahuaAccessDriver`) — CGI del equipo con Digest:
    `magicBox.cgi` para identificación, `configManager.cgi?name=AccessControl`
    para confirmar el subsistema y contar puertas (una entrada
    `table.AccessControl[i]` por puerta) y `recordFinder.cgi?getQuerySize` para
    saber si lleva tarjetas y eventos. **Nunca** se sondea
    `accessControl.cgi?action=openDoor`: esa llamada abre la puerta.
  - `zkteco-tcp` (`ZkTecoAccessDriver` + `ZkProtocol`/`ZkConnection`) —
    protocolo propio del fabricante en TCP 4370, implementado de cero
    (framing `50 50 82 7d` + largo, checksum que se envuelve en 65535, y el
    cifrado de la clave de comunicación con la sesión). El equipo **no tiene
    usuario**: la credencial es la *Comm Key* numérica (0 de fábrica), y acepta
    **una sola conexión a la vez**, así que cada operación abre y cierra su
    sesión y el descubrimiento no identifica a los equipos ya administrados
    (los estaría peleando con el sondeo de estado).
- **Driver `hikvision-isapi`** (`HikvisionAccessDriver`): reutiliza el
  transporte ISAPI de los paneles (`HikvisionIsapiClient`, que ahora recibe
  cómo nombrar al equipo en sus mensajes de error). Lee `/ISAPI/System/deviceInfo`
  y confirma que es control de acceso con `/ISAPI/AccessControl/Door/param/capabilities`
  (o `/ISAPI/AccessControl/capabilities`, o `…/RemoteControl/door/capabilities`);
  de ahí saca cuántas puertas administra y les pide el nombre a
  `/ISAPI/AccessControl/Door/param/{n}`. Las capacidades (apertura remota,
  eventos, tarjeta, huella, rostro, cupos de personas y tarjetas) se consultan
  de forma **tolerante**: un 404 significa "no lo soporta", no un error, porque
  los firmware de la familia varían mucho en qué rutas exponen.
- **Servicio y API**: `AccessControlService` (sondeo de estado cada 60 s, hasta
  4 equipos en paralelo, cambios auditados y empujados por el hub) y
  `AccessApi` (`/api/access/devices` CRUD, `/{id}/revalidate`, `/probe`,
  `/drivers`). Hub: `AccessDeviceStatusChanged` + `ConfigChanged("access-devices")`.
- **Descubrimiento**: `GET /api/discovery/scan?kind=access` corre SADP
  (Hikvision), DHDiscover (Dahua) y el sondeo propio de ZKTeco —WS-Discovery no
  distingue equipos de control de acceso, así que ahí se omite— y devuelve
  **solo los equipos compatibles**, clasificados por el `ClassifyModel` de cada
  driver: DS-K1T/DS-K5 y ASI/ASA terminal, DS-K2 y ASC controladora, DS-K3 y
  ASG torniquete. La intercomunicación (DS-KH/KV/KD/KB, VT*) y las cerraduras
  autónomas quedan fuera. ZKTeco no tiene anuncio propio: se difunde por UDP
  4370 el mismo saludo del SDK y a quien contesta se le pregunta el modelo con
  una sesión corta con la clave de fábrica (con clave propia aparece igual,
  sin identificar).
- **Panel web**: `#/access` (`wwwroot\access.js`), bajo *Dispositivos → Control
  de acceso*: tabla de equipos administrados (tipo, dirección, modelo, firmware,
  puertas, funciones, conexión) con Revalidar/Editar/Eliminar, y debajo la tabla
  **SADP** de equipos en línea con **Agregar** en un clic.
- **Auditoría** (categoría `access`): alta, edición, borrado, prueba de
  conexión, revalidación, sondeo de la red y caídas/recuperaciones del sondeo.

### Etapa 2 — la operación (2026-09-09)

- **La cadena de decisión**, que es lo que ordena todo el resto:
  `HORARIO (cuándo) + PUERTAS (por dónde) = NIVEL DE ACCESO`, y el nivel se le
  asigna a las PERSONAS. El VMS es la fuente de verdad y los equipos son una
  caché suya.
- **Modelo** (migración `AccessControlCatalog`): `AccessSchedule` +
  `AccessScheduleSegment`, `AccessLevel` con sus dos tablas de unión
  (`AccessLevelDoor`, `AccessLevelPerson`), `AccessPerson` + `AccessCard`
  (clave de teclado cifrada con AES-GCM como el resto de las credenciales),
  `AccessPersonDevice` (qué pasó al escribir a cada persona en CADA equipo) y
  `AccessEvent` (historial). `AccessDoor` gana modo/sensor/lectura y
  `AccessDevice` una marca de agua de eventos (`LastEventAt`), que sobrevive al
  reinicio del servidor.
- **`AccessPlanSlot`: por qué existe.** Los equipos guardan los horarios en una
  tabla NUMERADA y cada persona referencia, por puerta, UN número de esa tabla.
  Cuando alguien llega a la misma puerta por dos niveles con horarios distintos,
  lo correcto es escribir la UNIÓN (uno la deja pasar el martes, el otro el
  jueves ⇒ pasa los dos días), y esa unión también necesita su número. La tabla
  reparte ranuras **por huella del contenido**: mismos tramos ⇒ misma ranura, así
  que cien personas del mismo nivel no gastan cien ranuras. Tope 128 (el de los
  Hikvision de acceso, el más chico de la familia); agotarlo es un error con
  mensaje, no un fallo silencioso.
- **Sincronizador** (`AccessSyncService`): calcula, por persona, qué le
  corresponde en cada equipo, saca una **huella** de eso y sólo escribe si
  cambió. Los horarios se suben ANTES que la persona (una ranura vacía la
  rechaza el equipo) y una vez por equipo y pasada. Lo que falla queda pendiente
  con su motivo y se reintenta cada 5 min; un equipo caído deja la persona
  `Pending`, no `Failed`, que es lo que evita convertir un corte de red en una
  alarma de configuración. Borrar a alguien del VMS lo borra de los equipos: si
  no, seguiría entrando.
- **Reenvío forzado** (`MarkForResendAsync`): la huella de lo aplicado es lo que
  evita molestar a los equipos al pepe, pero también es lo que hace que un
  terminal reseteado se quede vacío para siempre —el VMS lo cree al día—. Los
  botones *Reenviar todo* (`/api/access/sync/full`) y *Reenviar padrón* por
  equipo (`/api/access/devices/{id}/resync`) borran esa huella a propósito y
  vuelven a escribir. Las pasadas se serializan con un semáforo: la API y el
  lazo de fondo pueden pedirla a la vez y dos en paralelo se pisarían las filas
  de estado. **Verificado contra el DS-K1T321MFWX**: misma huella (el contenido
  no cambió) pero `SyncedAt` nuevo, o sea que el equipo se reescribió de verdad.
- **Eventos EN VIVO** (`alertStream`): el sondeo no sirve para un guardia
  mirando el monitoreo, así que los equipos que saben empujar sus eventos se
  escuchan de verdad. `GET /ISAPI/Event/notification/alertStream` deja la
  respuesta abierta y manda cada evento como una parte MIME con un JSON
  (`multipart/mixed; boundary=MIME_boundary`, verificado contra el
  DS-K1T321MFWX). **Ojo**: en el flujo los códigos se llaman
  `majorEventType`/`subEventType`, no `major`/`minor` como en la búsqueda del
  historial; el parser entiende los dos.
  Se eligió `alertStream` sobre `httpHosts` —la otra vía que soporta el
  equipo— porque NO hay que configurar nada EN el terminal (httpHosts pide
  darle una URL nuestra y solo admite dos, que pueden estar ocupadas por un
  HikCentral) y porque la conexión la abre el servidor, así que anda igual
  detrás de NAT. Una escucha por equipo, con reintento creciente hasta 60 s.
- **Lector de eventos** (`AccessEventService`): el sondeo quedó como RESPALDO
  de la escucha en vivo —cubre a las marcas que no empujan y tapa los huecos de
  cuando la escucha estuvo caída o el servidor apagado—. Sondea cada 15 s desde
  la marca de agua de cada equipo, ata los eventos a la persona del padrón por su
  identificador (o por la tarjeta, si el equipo sólo informa eso) y purga según
  `Access:EventRetentionDays` (365). Los accesos **no** van a la bitácora de
  auditoría: esa registra lo que hacen los USUARIOS del VMS.
- **Drivers**: Hikvision completo (`RemoteControl/door` para las órdenes,
  `AcsWorkStatus` para el modo de cada puerta, `AcsEvent` paginado para el
  historial, `UserInfo`/`CardInfo` para las personas y
  `UserRightWeekPlanCfg` + `UserRightPlanTemplate` para los horarios; se manda
  `doorRight` Y `RightPlan` porque los firmware viejos sólo entienden el
  primero). Dahua: apertura/cierre por `accessControl.cgi` y historial por el
  buscador de registros de cuatro pasos; ZKTeco: `CMD_UNLOCK` y el historial
  binario de 40 bytes por renglón. En esas dos marcas el padrón se carga en el
  equipo, y `SupportsPersonSync` hace que la interfaz lo diga en vez de ofrecer
  algo que va a fallar.
- **Códigos de evento**: las cuatro tablas de `HikvisionAccessDriver` son las
  constantes `MAJOR_`/`MINOR_` del **CHCNetSDK de control de acceso**, 274
  códigos: `MAJOR_EVENT` (0x5, lo que pasa en la puerta), `MAJOR_ALARM` (0x1,
  sabotaje/coacción/incendio), `MAJOR_EXCEPTION` (0x2, el equipo fallando) y
  `MAJOR_OPERATION` (0x3, lo que alguien le ordenó). Aunque las constantes
  vengan del SDK, los códigos son los mismos que manda ISAPI, en el historial
  y en el flujo en vivo. Se escriben **en hexadecimal** igual que en el SDK
  para poder cotejarlos uno a uno: la tabla anterior estaba deducida en
  decimal y por eso se había corrido seis códigos (el botón de salida es
  `0x17`, no 21). Lo que igual no esté **no se adivina**: se guarda como
  "Otro" con su código y su JSON crudo a la vista, porque inventar una
  traducción mostraría un rechazo como si fuera un acceso concedido. La
  credencial (tarjeta/huella/rostro) se saca además de `currentVerifyMode`,
  que pisa a la de la tabla cuando el equipo lo informa.
- **API** (`AccessCatalogApi`): `/api/access/doors` (+ `/{id}/command`),
  `/schedules`, `/levels` (+ `/{id}/persons`), `/persons` (+ `/{id}/sync`),
  `/events`, `/overview`, `/sync`, `/departments`. Operar una puerta y mirar el
  historial es de cualquier usuario (es el trabajo del guardia); configurar
  horarios, niveles y personas es de administrador (es decidir quién entra).
- **Panel web** (`wwwrootccess-catalog.js`), menú *Control de acceso*:
  `#/access-monitor` (puertas en vivo + lo que va pasando), `#/access-persons`,
  `#/access-levels`, `#/access-schedules` (editor semanal con "+ tramo" y
  "copiar a todos") y `#/access-events`. El alta de personas es un **asistente
  de tres pasos** (datos / credenciales / accesos): lo escrito vive en un
  borrador en memoria, no en el DOM —el cuerpo del modal se redibuja al cambiar
  de paso—, cada paso valida lo suyo antes de dejar avanzar y se guarda UNA vez
  al final, así cancelar no deja personas a medio crear. Editando, los tres
  pasos están abiertos desde el principio y "Guardar cambios" está en todos.
- **Supervisor**: los tres servicios del módulo (estado, padrón, historial)
  entran al catálogo de *Sistema → Servicios*.

### El login ISAPI de la familia DS-K (2026-09-09)

Un DS-K1T804AMF devolvía **401 con la contraseña correcta** —la misma que entra
sin problemas por iVMS-4200— mientras que otro terminal andaba bien. Preguntando
al equipo salieron tres manías del firmware, cada una suficiente por sí sola
para el rechazo:

1. **`Content-Type: application/xml; charset=utf-8`** → 401. Sin el `charset`,
   el MISMO hash entra. (`StringContent(xml, Encoding.UTF8, "application/xml")`
   agrega el charset solo: hay que armar el cuerpo con `ByteArrayContent`.)
2. **`?timeStamp=` en la URL del `sessionLogin`** → 401. Es un rompecachés que
   cualquier servidor ignoraría; este no.
3. **Reusar el reto** de una llamada anterior a `sessionLogin/capabilities` →
   401. El equipo lo rota en cada consulta, así que cada intento necesita el
   suyo.

Y hay una cuarta, la peor: **cualquier cabecera `Accept` o `User-Agent` en la
petición** también devuelve 401. Sin cabeceras propias, el mismo hash entra.

**La solución no fue pelear con eso, sino evitarlo**: en los terminales DS-K el
**digest anda de una** (200 a la primera, verificado), así que el driver de
control de acceso construye su cliente con `digestOnly: true`. Los paneles de
alarma siguen prefiriendo el login de sesión porque ellos sí rechazan el digest;
son familias con manías opuestas. Si algún firmware DS-K rechazara el digest, el
transporte cae solo al login de sesión ante el primer 401, así que no se pierde
nada.

Igual quedó arreglado el camino del login de sesión (cliente sin cabeceras
propias, sin charset, sin timeStamp y reutilizando el reto ya pedido), con
`SessionLoginStyle` para reintentar con la forma anterior si hiciera falta:
**cuál funcionó queda anotado por conexión**, porque cada rechazo le consume un
intento de login a la cuenta.

**Ojo con el bloqueo**: una vez que el equipo bloquea, el digest queda bloqueado
también, así que no se lo puede ni reiniciar por ISAPI — hay que esperar los 30
minutos o cortarle la corriente.

Lección para la próxima: **diagnosticar contra un equipo Hikvision cuesta
intentos de login**. Investigando esto se bloqueó el terminal 30 minutos. Si hay
que probar formas de autenticación, conviene hacerlo contra un equipo de banco,
no contra uno en servicio.

### Huellas — el complemento de enrolamiento (2026-09-09)

- **Por qué un proceso aparte**: el navegador no llega al lector USB. El
  complemento (`src\TrueCentralVms.WebControl`, WPF en bandeja) expone su API
  con Kestrel **solo en 127.0.0.1** —sin urlacl y sin elevación, así arranca con
  la sesión sin UAC— y el panel lo descubre probando 5081, 25471 y 25472.
  Portado del `TrueCentral.WebControl` de vwcontroller, que ya estaba validado
  contra el lector real.
- **Lector**: DS-K1F820-F por `FPModule_SDK.dll` (P/Invoke). Dos rarezas del
  hardware que están documentadas en el código porque cuestan de adivinar:
  Windows lo presenta como **unidad de CD-ROM USB** (el SDK abre `\.\D:` y le
  habla por SCSI), y el SDK **no admite indicarle cuál abrir** —para forzar uno
  se toman los otros con un `FileStream` exclusivo—. Además bloquea durante todo
  el enrolamiento y avisa por callback, así que la captura vive en un hilo
  propio y el panel sondea el estado.
- **Modelo**: `AccessFingerprint` (persona + número de dedo 1..10 + plantilla
  **cifrada** + calidad + origen), migración `AccessFingerprints`. La plantilla
  NUNCA sale del servidor: el DTO informa qué dedo está tomado, con qué calidad
  y de dónde salió. En el panel, una huella que ya estaba viaja con
  `template: null`, que es como se dice "dejá la que tenés".
- **Bajada a los equipos** (las tres cosas verificadas preguntándole al
  DS-K1T321MFWX, porque la documentación no coincide con el firmware):
  `POST /ISAPI/AccessControl/FingerPrintDownload` —con PUT contesta
  `methodNotAllowed`— y el objeto raíz del cuerpo se llama **`FingerPrintCfg`**,
  no como la ruta: si no, responde `MessageParametersLack: FingerPrintCfg`, que
  es justamente el nombre que espera. Para borrar,
  `PUT /ISAPI/AccessControl/FingerPrint/Delete` **con barra**; la
  `FingerPrintDelete` que documentan otros modelos responde `notSupport`. Las
  dos formas quedaron con respaldo, porque los firmware de la familia difieren.
  `enableCardReader` sale de `FingerPrintCfg/capabilities`, que declara su
  `@max` (1 en un terminal), con respaldo de un lector por puerta.
  `SupportsFingerprintSync` va aparte de `SupportsPersonSync` porque escribir
  personas es una cosa y la biometría otra.
- **El borrado previo NO se pasa por alto**: se borran las huellas de la persona
  antes de escribir las nuevas, y si el borrado falla se corta con un error en
  vez de seguir. Si el equipo se quedara con una huella que en el VMS ya no
  está, esa huella seguiría abriendo la puerta — es el único lugar del módulo
  donde tolerar un fallo sería un agujero de seguridad. (Al borrar la persona
  entera sí se tolera: lo que le quita el acceso es el borrado de la persona,
  que viene enseguida.)
  Única excepción, y **con evidencia**: si el borrado falla se le pregunta al
  equipo qué huellas tiene la persona
  (`POST /ISAPI/AccessControl/FingerPrintUpload`), y solo si CONTESTA que
  ninguna (`status: NoFP`) se sigue adelante. El V1.4.0 responde
  `badParameters` al borrado de alguien que no tiene ninguna huella, así que
  sin esto no se le puede escribir biometría a ese equipo nunca. Si el equipo
  no sabe contestar la consulta, no se tolera nada: se corta igual.
  El error que se informa es el de la ruta que **contestó algo con sentido**, no
  el de la última probada: la segunda ruta de borrado responde `notSupport` en
  los equipos que tienen la primera, y quedarse con eso tapaba el motivo real.
- **Las diez huellas entran** (hasta diez dedos por persona, como corresponde),
  pero LEERLAS es lo difícil. La consulta `FingerPrintUpload` devuelve **UN
  dedo por llamada** aunque la persona tenga varios, y paginar con
  `searchResultPosition` repite el mismo: hay que preguntar dedo por dedo
  (`fingerPrintID` en la condición). Creerle la lista corta a esa consulta fue
  lo que hizo que la comprobación acusara huellas perdidas que sí estaban.
  El filtro por dedo tampoco es universal: el DS-K1T804AMF V1.4.0 lo ignora y
  contesta que tiene cualquier dedo que se le nombre. Eso deja la comprobación
  corta en ese equipo, pero nunca acusando en falso, que es el lado seguro.
- **El equipo aplica lo que se le manda EN DIFERIDO**, y eso obliga a esperar
  en dos lugares, no en uno. Durante unos 3 s sigue contestando el estado
  anterior y a la operación pegada le dice `deviceBusy · leaderFP`. Lo que
  cuesta descubrir es que **el BORRADO también es diferido**: si se escribe la
  primera huella enseguida, el borrado la alcanza por dentro y se la lleva
  puesta —quedaba siempre sin el primer dedo, y el equipo contestaba OK a las
  dos escrituras—. Por eso se espera a que el equipo CONFIRME que no le queda
  ninguna antes de escribir, hay un respiro entre huella y huella, y la
  comprobación reintenta en vez de leer una sola vez.

- **Una credencial que falla NO corta a las demás.** Tarjetas, huellas y rostro
  se escriben cada una por su cuenta: si el equipo no sabe modelar la foto, la
  persona igual queda escrita con su puerta, su clave, sus tarjetas y sus
  huellas, y el error dice QUÉ faltó en vez de dejar todo en rojo sin explicar.
  Antes una foto chica dejaba a la persona entera como fallida en ese equipo
  aunque el resto ya estuviera adentro, y el operador no sabía que igual podía
  entrar. El equipo sigue quedando en rojo —falta algo, y eso hay que verlo—
  pero el mensaje empieza por lo que SÍ quedó.
  (Lo que no se tolera es el núcleo: la ficha de la persona y los horarios. Sin
  eso no hay nada que escribirle encima.)

### Leer la tarjeta en el lector

- `GET /ISAPI/AccessControl/CaptureCardInfo?format=json` deja al equipo
  esperando que pasen una tarjeta y contesta
  `{"CardInfo":{"cardNo":"3558822549"}}`. Verificado con una tarjeta real.
  **No responde enseguida**: se queda unos 10 s y, si no pasó nadie, contesta
  `deviceError`. Eso NO es un fallo, es "todavía nada": el panel vuelve a pedirlo
  mientras el diálogo esté abierto. Con POST o PUT contesta `methodNotAllowed`:
  es un GET.
- **El 404 de estos equipos NO significa "esa ruta no existe"**: con eso dicen
  "no pasó nadie" y "ya hay otra captura". Con el `allowNotFound` normal, el
  cliente devolvía null y el panel mostraba "este equipo no sabe leer una
  tarjeta" mientras el lector la esperaba en la cara del operador. Va con
  `allowNotFound: false` para poder leer el cuerpo.
- Atiende **una captura a la vez**; a la segunda contesta `deviceBusy`, que se
  trata igual que "todavía nada".
- **Cuánto espera el equipo cambia por firmware**, y eso marca el ritmo del
  bucle: el DS-K1T321MFWX deja la petición abierta unos 10 s, y el
  DS-K1T804AMF V1.4.0 contesta al instante (medido: 843 respuestas en 140 s).
  Por eso el panel espera 1,2 s entre pedidos: sin eso le haría seis por
  segundo al mismo terminal que está atendiendo eventos y sincronización.
- **El LECTOR no se puede elegir: el equipo ignora `cardReaderNo`.** Confirmado
  con una prueba alternada contra el DS-K1T321MFWX: pidiendo por turnos el
  lector 1 y el 2 mientras se pasaba la MISMA tarjeta por el MISMO lector
  físico, la leyó en los dos casos. O sea que el terminal escucha en todos sus
  lectores y el parámetro es decorativo (también contesta igual para un lector
  que no existe). Por eso el panel ofrece elegir el EQUIPO y no el lector: no
  se ofrece un control que no hace nada. El parámetro se manda igual si el
  llamador lo pide, por si algún firmware sí lo respeta.

### Rostro (terminales con cámara)

- Se guarda la **FOTO**, no una plantilla: el modelo lo arma el propio terminal,
  así que hay que conservar la imagen para poder mandársela a un equipo nuevo.
  Va cifrada como las huellas (`AccessFace`, migración `AccessFaces`), una por
  persona, y no viaja en el JSON del padrón: se pide aparte por
  `GET /api/access/persons/{id}/face`, porque pesa cientos de veces más que la
  ficha y el listado la traería para todos sin necesitarla.
- **Matrícula**: `POST /ISAPI/Intelligent/FDLib/FaceDataRecord` en
  `multipart/form-data`, con una parte JSON llamada `FaceDataRecord` y otra
  binaria llamada `img`. Biblioteca `FDID=1` / `blackFD`, hasta 500 rostros, JPG
  o PNG (todo verificado contra el DS-K1T321MFWX).
- **El separador del multipart va SIN comillas.** .NET escribe
  `boundary="----xxx"`, que es válido según el RFC, y el terminal contesta
  `badJsonFormat`: su parser se queda con la comilla pegada y no encuentra
  ninguna parte. La cabecera se arma a mano por eso.
- **Borrado**: `PUT /ISAPI/Intelligent/FDLib/FDSearch/Delete` con
  `{"FPID":[{"value":"<legajo>"}]}` —con la lista de strings a secas contesta
  `MessageParametersLack`—. Se tolera el `notSupport`: un equipo sin cámara no
  puede estar guardando un rostro, y sin esa tolerancia toda persona quedaba
  fallida en los terminales sin biblioteca de caras.
- **La foto CHICA es el rechazo más común.** Medido con una foto real: a
  135×189 px el equipo contesta `SubpicAnalysisModelingError`, y **esa misma
  imagen ampliada al doble (270×378) la acepta**. No hay un mínimo publicado, así
  que el panel no bloquea: avisa cuando el lado menor baja de 300 px y muestra la
  medida, y el mensaje de rechazo nombra el tamaño primero. Ampliar una foto
  chica pasa el filtro pero no agrega detalle: conviene una original grande.
- **El borrado del rostro también es diferido**, igual que el de las huellas: si
  se escribe encima antes de que termine, el equipo contesta
  `deviceUserAlreadyExistFace`. Se espera a que confirme que ya no lo tiene.
- **El rechazo típico no es de red**: `SubpicAnalysisModelingError · saveFacePic`
  significa que el equipo leyó todo bien y no encontró una cara utilizable en la
  foto. Se traduce a un mensaje que dice qué hacer (foto de frente, despejada,
  bien iluminada) en vez de mostrar el código.
- **Se COMPRUEBA lo escrito**: después de bajar las huellas se vuelven a leer
  del equipo y se comparan con las del plan (`VerifyFingerprintsAsync`). Sin
  esto el VMS marcaba `Synced` a una persona de la que solo había entrado la
  mitad de la biometría —y peor: en un equipo había quedado una huella ajena al
  padrón, matriculada en el propio terminal, que el VMS daba por buena—. Como
  el equipo contesta en diferido, la lectura se reintenta (4 × 1,5 s) antes de
  acusar una pérdida; leer una sola vez daba falsos positivos.
- **El borrado de huellas necesita el cuerpo DETALLADO en los firmware viejos**:
  el DS-K1T804AMF V1.4.0 solo borra si además del legajo se le mandan
  `enableCardReader` y `fingerPrintID` (los diez dedos) dentro de
  `EmployeeNoDetail` —su esquema los declara, y sin ellos contesta un
  `badParameters` que no dice cuál falta—. Se prueba primero esa forma y después
  la escueta, que es la que aceptan los firmware nuevos.
- **`searchID`: el campo que más cuesta** (vale para huellas, personas e
  historial). Es una **sesión de búsqueda**: si se repite un valor ya usado, el
  equipo contesta como si la búsqueda estuviera agotada —`NoFP`, "no tiene
  huellas"— aunque la persona sí las tenga. Con un valor fijo, la consulta que
  decide si un borrado fallido es benigno habría dado siempre "no hay nada que
  revocar": el agujero exacto que esa consulta viene a tapar. Va uno nuevo por
  búsqueda (el mismo entre páginas de UNA búsqueda, que es como se pagina).
  Y el **largo** tiene tope por ruta y por firmware, con rechazo mudo
  (`badParameters`, sin decir qué campo): el DS-K1T804AMF V1.4.0 acepta 32 en
  las huellas pero solo **20** en el historial; el DS-K1T321MFWX V3.9.20 acepta
  64. `Guid.ToString()` da 36 y no entra en ninguno de los dos topes del viejo,
  así que su historial fallaba entero. Se usan **16** caracteres, que entran en
  todos. Mismo criterio con `maxResults`: el V1.4.0 declara `@max=10` en
  `AcsEvent`, así que se pide de a 10 en todos los equipos.
- **Probado de punta a punta**: con una plantilla sintética en el padrón, el
  sincronizador la bajó al DS-K1T321MFWX y la persona quedó `Synced` sin error;
  después se quitó del VMS y el reenvío la borró también del equipo.
- **Empaquetado**: `installer\complemento.iss` →
  `CLRTrueCentralVMS-Complemento-Setup-<v>.exe`, que la suite publica en
  `webcontrol\` del servidor; `/api/webcontrol/info|installer` lo ofrecen desde
  el propio panel cuando falta.
- **Verificado contra hardware**: el DS-K1F820-F conectado responde
  (`DS-K1F820-F_GML_GM_V1.2.0_build190306`, SDK
  `FPModuleSDK_Win_x64_V2.2.0_Build190225`); descubrimiento del complemento,
  listado del lector, prueba de conexión, inicio de captura ("apoye el dedo",
  paso 1 de 3) y cancelación, todo desde el panel. Falta apoyar un dedo de
  verdad y ver la plantilla bajar a un terminal.

- **Validado con hardware (2026-09-09)**: contra un **DS-K1T321MFWX** real se
  escribió una persona completa —horario semanal (`UserRightWeekPlanCfg`),
  plantilla de horario (`UserRightPlanTemplate`), persona con clave y permisos
  por puerta (`UserInfo`)— y quedó `Synced`. **El equipo cuenta los topes en
  BYTES, no en caracteres**: `templateName` recortado a 32 *caracteres* eran 34
  *bytes* por los acentos, y lo rechazaba con
  `Invalid Content · beyondARGSRangeLimit · templateName`. De ahí `Truncate` por
  bytes y `PlanLabel`, que además transcribe el nombre del horario a ASCII
  porque es una etiqueta interna del equipo que no lee nadie (el nombre de la
  PERSONA sí conserva los acentos: ese se muestra en el terminal). Falta todavía
  probar la bajada de una huella (`FingerPrintDownload`) y el resto de las rutas
  (puertas, eventos) contra el equipo.
- **Pendiente**: validar el resto de las rutas ISAPI/CGI contra hardware (las de
  puertas, eventos y biometría siguen tomadas de la documentación);
  después, padrón para Dahua y ZKTeco,
  huella y rostro (hoy se enrolan en el propio terminal), disparador de
  automatizaciones por evento de acceso y enganche con video.

## Supervisor de servicios (watchdog) — 2026-09-03

- **Qué**: watchdog interno del servidor (`Services\Supervisor\`) que vigila PostgreSQL embebido, MediaMTX y los ocho subsistemas `BackgroundService` (monitor de dispositivos, contabilidad de sesiones, paneles de alarma, receptor SIA DC-09, parlantes IP, ANPR, automatizaciones, retención de bitácora), y permite a un administrador iniciarlos / detenerlos / reiniciarlos y apagar el auto-reinicio por servicio desde **Sistema → Servicios** del panel web.
- **Diseño**: `IManagedService` (Id, salud, Start/Stop) con tres adaptadores: `PostgresManagedService` (SELECT 1; esencial → solo reiniciar), `MediaMtxManagedService` (proceso vivo + API de control; respeta el relanzamiento propio de `MediaMtxManager`, que ahora expone `RestartPending`/`ProcessId`/`StartedAtUtc` y vuelve a ser arrancable tras `StopAsync`) y `HostedServiceAdapter` (tarea de ejecución viva ⇒ sano; `BackgroundService` admite Stop+Start). `ServiceSupervisor` es el último hosted service (todos ya arrancaron cuando juzga); pasada cada `Supervisor:CheckSeconds` (10), reintentos con espera 5/10/20/40/60 s hasta `MaxRestartAttempts` (5) dentro de `RestartWindowMinutes` (10). Un gate por servicio serializa órdenes y pasada. Estado por hub `ServiceStateChanged`; caídas y reinicios automáticos auditados como `sistema`, órdenes manuales con el actor real (`service-started/stopped/restarted/failed/autorestart-changed`, `server-restart-requested`).
- **Reinicio completo**: solo como servicio de Windows (`Restart-Service CLRTrueCentralVMS` desde un `powershell.exe` auxiliar fuera del job). El proceso del servidor no puede vigilarse a sí mismo: eso lo cubre `sc.exe failure ...` del SCM (documentado en README).
- **Pendiente**: página equivalente en el cliente WPF (hoy solo indicadores CPU/RAM/disco) y persistir el interruptor de auto-reinicio (hoy en memoria, vuelve a activo al reiniciar el servidor).

## Watchdog de escritorio — 2026-09-07

- **Qué**: `src\TrueCentralVms.Watchdog` (WPF, portado de `vwcontroller\src\TrueCentral.Watchdog`): ventana de monitoreo del servidor con el estado del servicio de Windows, de la API (`/api/health`) y de PostgreSQL embebido, botones iniciar/detener/abrir panel y registro de actividad que vuelca el Visor de eventos ante una caída. Es la capa EXTERNA de vigilancia: el supervisor interno reinicia subsistemas, pero no puede recuperar el proceso si muere entero.
- **Diseño**: `ServerEnvironment.Discover()` busca la instalación en la carpeta del ejecutable y en su padre (`{app}\watchdog` → `{app}`) y lee su configuración con la misma precedencia que el servidor; el `postgresql.conf` del clúster manda sobre `PgPort`. Sondeo cada 2 s (servicio por `ServiceController`, API por `127.0.0.1` para no pagar el intento por `::1`, base por conexión TCP). Manifiesto `requireAdministrator`: un solo UAC al abrir. Sin servicio instalado pero con API y base vivas informa "Servidor en ejecución (sin servicio de Windows)", que es el caso de desarrollo.
- **Auditoría**: el servidor registra ahora `system/server-stopped` en `ApplicationStopping` (PostgreSQL recién se detiene en `ApplicationStopped`, así que la base sigue viva para recibirlo). Un apagado pedido desde el Watchdog, `services.msc` o el SCM deja evidencia junto al `server-started` que ya existía.
- **Distribución**: `suite.iss` lo instala en `{app}\watchdog` (icono en el menú Inicio, tarea `desktopicon` para el escritorio, casilla postinstall con `shellexec` por la elevación) y lo cierra con `taskkill` antes de copiar y al desinstalar; `build-installers.ps1` lo publica self-contained y la etapa `Publicar` de Jenkins lo deja en `dist\server\watchdog` para que el .zip calce con una instalación.

## Instaladores (Inno Setup 6) — 2026-09-03

- **Dos instaladores** en `installer\` (patrón tomado de `vwcontroller\installer`): `suite.iss` = **suite completa** (servidor como servicio de Windows `CLRTrueCentralVMS` + panel web + drivers + PostgreSQL embebido + MediaMTX + FFmpeg, con tarea opcional que instala el cliente en el mismo equipo lanzando en silencio el instalador del cliente, que además queda en `{app}\client-setup` para los demás puestos) y `client.iss` = **solo cliente** (WPF + FFmpeg de Flyleaf en `FFmpeg\` + ffmpeg/ffprobe + MediaMTX para proyección, regla de firewall 8554). `build-installers.ps1` publica self-contained win-x64, verifica native\ y tools\, compila primero el cliente y luego la suite; salida en `dist\CLRTrueCentralVMS-{Suite,Client}-Setup-<versión>.exe`.
- **Suite en el equipo**: datos en `%ProgramData%\CLRTrueCentralVMS` (ACL SYSTEM+Administradores), `appsettings.Production.json` (Urls 5090, PgDataDir en ProgramData, log HCNetSDK) reemplazado en cada versión, `appsettings.Local.json` nunca tocado; puerto PG 25490 con búsqueda de libre hasta 25539 anotada en Local.json; servicio con `sc failure` (5/15/60 s); firewall TCP 5090+5091, UDP por programa (SADP/WS-Discovery/Dahua) y TCP 8654 para mediamtx.exe; al terminar arranca el servicio, espera `/api/health` y abre el panel para crear el primer admin. Desinstalar ofrece quitar el cliente y los datos.
- **Gotchas**: el SDK Web publica todo `*.json` del proyecto como contenido → el script borra de `build\publish\server` los restos de desarrollo (appsettings.Local.json, mediamtx.runtime.yml, pgdata/anpr/workflows) y el .iss los excluye igual; los .iss y el .ps1 llevan BOM UTF-8 (acentos en PowerShell 5.1 e ISCC). Jenkins tiene etapa `Instaladores` (parámetro INSTALLERS, solo si el agente tiene ISCC.exe y los binarios de terceros) que archiva `dist\*.exe`.

## Receptora de paneles en el instalador — 2026-09-07

- **Runtime de Visual C++ junto a PostgreSQL**: los binarios de EDB son builds MSVC y dependen de `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` y `MSVCP140.dll`, que **no** vienen con Windows. En la VM de pruebas (Server 2022 limpio) `initdb.exe` moría con `-1073741515` (`0xC0000135`, DLL no encontrada) y el servicio no arrancaba. Se distribuyen *app-local* en `tools\postgres\pgsql\bin` (`build\setup-binaries.ps1` los copia del redist de VS o de System32; `installer\build-installers.ps1` falla si faltan). `EmbeddedPostgres.DescribeStartupFailure` traduce 0xC0000135/0xC0000142/0xC000007B a un mensaje entendible en el log.
- **Hik IP Receiver Pro embebido en la suite** (`tools\iprp\HikIpReceiverPro-Setup.exe`, V2.5.0.6): tarea opcional marcada por defecto. Instalación silenciosa NSIS (`/S /D=`), y después se le cambia el puerto de la web/API: `nginx\conf\nginx.conf` pasa de `listen 80;` a `listen 127.0.0.1:8091;` y `Config.xml` (nodo `<HTTP>`) a 8091. Solo se toca si siguen en el valor de fábrica. Doble candado: regla de firewall que **bloquea** TCP 8091 (las reglas de bloqueo ganan sobre las de permiso y el loopback no pasa por el firewall) y regla que **abre** 7091, 7660-7667 y 8661, por donde se registran y reportan los paneles.
- **Gotcha del nginx de la receptora**: su `server` compara `$host` con `$server_addr` y devuelve 403 si no coinciden (protección de host header). Con la escucha en loopback hay que llamarla por `127.0.0.1:8091`, **no** por `localhost:8091`.
- **La clave del panel viaja cifrada** (2026-09-07): en la tabla de endpoints de su panel web, `addDevice` declara `security1: ["password","userName","EhomeKey","streamKey","OTAPKey"]`; esos campos van cifrados o la pasarela responde `addDeviceFailed` (que nuestro traductor mostraba como «el usuario no tiene permiso», despistando). Esquema, reversado de `webSdkUtils.strToAESKey`: `GET /ISAPI/Security/capabilities?username=` da `salt`, `keyIterateNum` e `isIrreversible`; base = isIrreversible ? SHA256(usuario+salt+contrasena) : contrasena; llave = SHA256(base + "AaBbCcDd1234!@#$") iterado `keyIterateNum` veces, primeros 32 caracteres hex = AES-128; el valor va como AES-128-CBC hex y el IV en la URL (`&security=1&iv=`). Si la pasarela no expone capacidades, o si aun cifrada rechaza, se reintenta en claro.
- **Alta y baja de equipos en la receptora desde el VMS** (`addDevice`/`delDevice`, API de la pasarela desde su V2.5.0): `HikvisionIpReceiverDriver.AddGatewayDeviceAsync` / `DeleteGatewayDeviceAsync` / `ListGatewayDevicesAsync`; endpoints `POST /api/alarms/receiver/devices/list`, `POST /api/alarms/receiver/devices` y `POST /api/alarms/receiver/devices/delete` (credenciales en el cuerpo, nunca en la URL; se pueden tomar de un panel ya guardado con `panelId`). El alta devuelve el `devIndex` (uuid) y el mantenedor web lo carga solo en el campo del panel. No se permite quitar de la pasarela un equipo que un panel del VMS esté usando. Auditoría: `receiver-devices-listed`, `receiver-device-added`, `receiver-device-removed`.
- **Activación automática de la receptora** (`LocalIpReceiverService`, 2026-09-07): el servidor la activa solo en el primer arranque con una contraseña aleatoria de 24 caracteres que guarda cifrada (CredentialProtector) en `pgdata\tcvms-iprp.secret`. El operador nunca la ve ni la escribe. El esquema, reversado del propio panel web de la receptora (webpack módulos 432/131/55) y **verificado contra el equipo real**: (1) `POST /ISAPI/Security/challenge` con `{"PublicKey":{"key":base64(módulo RSA 1024 en hex)}}`; (2) la respuesta `Challenge.key` es base64 de un hex (a veces con `?` al final) que se descifra con la privada (**RSA PKCS#1 v1.5**) y entrega 32 caracteres; (3) esos 32 caracteres, leídos como **hex**, son la llave **AES-128 ECB con relleno de ceros**; el mensaje es `base64( AES(primeros 16 caracteres del desafío) + AES(contraseña) )`, todo en hexadecimal; (4) `PUT /ISAPI/System/activate` con `{"ActivateInfo":{"password":…}}`, sin autenticación. Vectores de AES comparados uno a uno con las funciones del panel web. Si la receptora ya venía activada por fuera responde 403 `hasActivated` y queda anotado en el registro (hay que escribir esa contraseña a mano una vez).
- **Arranque del servicio de la receptora tras reconfigurarla** (2026-09-07): esperar a que «deje de estar RUNNING» no basta, porque pasa por STOP_PENDING y en ese estado el SCM rechaza `sc start`; como ademas no se comprobaba el resultado, el instalador se quedaba esperando a un servicio que nadie habia arrancado (6 min de barra verde, receptora instalada y detenida). Ahora se espera a STOPPED (hasta 2 min) y el arranque se reintenta hasta 5 veces verificando RUNNING. **Con esto la activacion automatica quedo verificada de punta a punta en la VM**: la receptora paso a responder 401 y el servidor guardo su credencial en `pgdata	cvms-iprp.secret`.
- **Formato de la activacion** (2026-09-07, depurado contra la VM): el `PUT /ISAPI/System/activate` exige el cuerpo JSON declarado como `application/x-www-form-urlencoded; charset=UTF-8` (asi lo manda su panel web; se capturo interceptando XHR). Con `application/json` el equipo descarta el cuerpo y contesta `{"subStatusCode":"notActivated"}`, que parece «no llego la peticion» y despista. `POST /ISAPI/Security/challenge` acepta ambos. Se prueban los dos formatos y se registra cual funciono; la contrasena generada es de 16 caracteres (limite habitual de Hikvision).
- **Paneles con la receptora local sin credenciales**: `GET /api/alarms/receiver/local` dice si está lista (nunca devuelve la contraseña) y el mantenedor web muestra «Usar la receptora instalada en este servidor»; el servidor completa usuario y contraseña al crear, probar o administrar equipos si la dirección es la del loopback y el puerto configurado (`Alarms:LocalReceiver`).
- **Receptora sin interfaz web** (2026-09-07): su nginx atiende en un solo puerto la SPA (`location /`) y la API (`location /ISAPI`, `/SDK`, `/daf`, `/Streaming`, …). El instalador inserta `return 403;` al abrir el bloque de la raíz —comparando la línea sin espacios (`location/{`), así no toca las de la API— y guarda `nginx.conf.clr-original` antes de escribir. Verificado simulando la transformación sobre el nginx.conf real: 10 rutas de API intactas y una sola escucha (`127.0.0.1:8091`). Se pierde el complemento de video de la receptora, que el VMS no usa. Alternativa descartada: hablarle directo al servicio interno de `127.0.0.1:8081` (probado: responde ISAPI con el mismo Digest), pero es un puerto interno no documentado que puede cambiar entre versiones.
- **Receptora ya activada por otro** (2026-09-07): el fabricante no permite reiniciar la contrasena en local (solo un Super Admin de Hik-Partner Pro), asi que el instalador detecta el caso —hay servicio `DeviceGatewayService` pero no existe `pgdata	cvms-iprp.secret`— y ofrece reinstalarla desde cero, avisando que se borran su historial y sus equipos; por defecto NO, y nunca en instalacion silenciosa. La alternativa sin perdida de datos es escribir esa contrasena una vez al agregar el primer panel.
- **No cortar el primer arranque de la receptora** (2026-09-07, visto en la VM 192.168.10.232): el instalador detenia `DeviceGatewayService` apenas terminaba la instalacion silenciosa, que es cuando la receptora crea su propio cluster PostgreSQL; cortado a la mitad, el servicio ya no levanta (queda instalada, bien configurada y muerta). Ahora se espera hasta 8 minutos a que su API conteste (probando 80 y 8091) antes de detenerla para cambiarle el puerto.
- **Orden del instalador**: la receptora se instala ANTES de arrancar el servicio del VMS, porque el servidor la activa en su arranque; ademas el arranque reintenta la activacion cada 30 s hasta 10 veces, por si la receptora tarda en levantar sus propios servicios.
- **La receptora es obligatoria en la suite**: se instala siempre (no es una tarea marcable) y `build-installers.ps1` y el propio `.iss` fallan si falta `tools\iprp\HikIpReceiverPro-Setup.exe`.
- **La salida de eventos «Private» se habilita sola** (2026-09-11): la receptora viene con *Automation Output → Protocol* apagado y su API rechaza la suscripción (403 `invalidOperation`), así que los paneles quedaban **En línea pero con el canal de eventos cerrado**, funcionando solo por sondeo (hasta 30 s de atraso, sin el usuario que armó o desarmó, y perdiendo lo que empieza y termina dentro de la misma ventana). El instalador no puede hacerlo: la contraseña de la receptora recién existe cuando el servidor la activa. Ahora `HikvisionIpReceiverDriver.EnsureEventsEnabledAsync` lee `ProtocolParams` y solo reescribe si hace falta, devolviendo el documento con la misma forma en que vino (envuelto o plano) y el resto de los campos intactos; `LocalIpReceiverService` lo llama después de activar la receptora y en cada arranque hasta lograrlo (mientras falle, el estado no es final y el reintento del arranque insiste), y `SubscribeEventsAsync` lo intenta solo cuando una suscripción es rechazada y reintenta una vez —así también se recupera una pasarela ajena, o una que alguien apagó desde su web—. Si aun así no se puede (cuenta sin permiso), queda el mensaje de siempre en el estado del panel. **Verificado contra la V2.5.0.6** (2026-09-11, receptora del servidor de Jemo): de fábrica responde `{"ProtocolParams":{"deviceHeartbeatInterval":30,"enabled":false,"protocolType":"Sur-Gard"}}` —apagada y apuntando a Sur-Gard— y acepta ese mismo documento de vuelta con los dos campos cambiados (`application/json`, `statusCode 1`); a los pocos segundos la suscripción dejó de ser rechazada. **Gotcha al probarlo a mano**: en PowerShell 5.1 `curl.exe -d "{\"a\":1}"` le entrega las barras invertidas al proceso y el equipo contesta `badJsonFormat`, que despista hacia el esquema; hay que mandar el cuerpo desde un archivo (`--data-binary "@C:\pp.json"`). Una salida ya **encendida con otro protocolo** (Sur-Gard reportando a la central del cliente) solo se pisa en la receptora propia: en una pasarela ajena se deja como está y el panel muestra qué protocolo la está ocupando.
- **Una sola suscripción por receptora, compartida entre sus paneles** (2026-09-11): con el protocolo ya habilitado, un panel quedó verde y el otro siguió rojo. La pasarela admite **una sola suscripción a la vez** y a la segunda le contesta `{"errorCode":1073746314,"errorMsg":"No more task can be added.","statusCode":4,"statusString":"Invalid Operation","subStatusCode":"noMoreTasksCanBeAdded"}` —verificado abriendo una tercera con curl mientras el servidor tenía la suya—; el servicio abría una **por panel**, así que el segundo perdía siempre. Como el flujo ya trae los eventos de todos los equipos, `HikvisionIpReceiverDriver` ahora multiplexa: `GatewayStream` (estático, indexado por esquema+usuario+clave+host+puerto) abre una sola y cada panel recibe un `Subscriber` que vive mientras viva la compartida y se traduce con su propio devIndex; el último en soltarla la cierra, y si el flujo se cae el primer panel que reintente abre la siguiente. **Ojo con el diagnóstico**: este error comparte el `statusString` "Invalid Operation" con el protocolo apagado, así que hay que mirar el `subStatusCode` —confundirlos hacía que el panel pidiera habilitar Automation Output cuando ya estaba habilitado, y con la autocuración nueva habría reescrito `ProtocolParams` cada minuto al pedo—.

## Licenciamiento — 2026-09-07

Integrado con `license_service_server` (producto `truecentral`, ver README §Licenciamiento).
Decisiones:
- Archivo `.lic` v2 con `signed_payload` (bytes firmados) para verificar en C# con
  BouncyCastle sin canonicalizar JSON; clave pública compilada (`LicensingConstants`),
  override solo en Debug.
- Modos `ONLINE` (heartbeat + gracia, ambos firmados dentro del .lic) y `OFFLINE`
  (solo archivo, conviene expiración). Activación sin internet por `.req` → backoffice → `.lic`.
- Licencias base + **expansiones** (`ADDON` con `parent`) que se suman en el servidor
  de licencias; el VMS solo conoce el archivo de la base.
- Enforcement al **habilitar** (altas y PUT enable): video (canales sobrantes entran
  deshabilitados), ANPR, paneles, decodificadores, muros, parlantes, automatizaciones,
  usuarios y clientes de escritorio simultáneos (login 402). Modo restringido = 402 a
  escrituras salvo auth/servicios/licencia.
- Prueba incorporada de 30 días desde el primer arranque (state.json), anti-retroceso de reloj.
- Pendiente para producción: clave pública y API key del servidor productivo en
  `LicensingConstants` / `appsettings.Production.json`; el servidor de pruebas
  192.168.10.232 arranca en prueba de 30 días y hay que emitirle licencia.

## Riesgos vigilados

Patentes: el callback de mensajes de HCNetSDK es único por proceso y el delegado debe vivir en un campo estático (si el GC se lo lleva, el SDK llama a memoria liberada y el proceso cae) · las estructuras ITS del SDK cambian entre versiones: al actualizar HCNetSDK hay que revisar `ItsInterop` contra la cabecera nueva · Muro: el decodificador guarda su propio mapa de ventanas y el servidor re-sincroniza al arrancar (`WarmUpDecodersAsync`) — vigilar que un reinicio del servidor no deje ventanas huérfanas en el equipo · forma exacta del body de auth de MediaMTX (verificar contra docs v1.20 en M3) · marshaling x64 Dahua (harness de consola contra hardware real antes de integrar) · acople versión FlyleafLib↔FFmpeg (pinear juntos, LGPL shared build) · encoding URL de contraseñas con `@`/`:` en las source URLs · grant vencido en retry del cliente (re-pedir siempre) · interop Hik viejo + DLLs 6.1.9.48 (validar tamaños de structs en M2) · Flyleaf en vivo (2026-09-15): `avformat_find_stream_info` cuenta `analyzeduration` en tiempo del STREAM, no de reloj, así que un secundario de 7 fps tardaba 3,6 s en abrir (1,3 s el principal) y el cambio main↔sub del doble clic se sentía lento; `VideoCellViewModel.CreatePlayer` lo omite (`AllowFindStreamInfo = false`: 0,3-0,5 s de apertura, imagen en ~1 s; el SDP de MediaMTX trae sprop-*) — los cuadros de reproducción lo siguen necesitando para la duración. Además, al maximizar (sub→main) el secundario saliente queda **estacionado** (`VideoCellViewModel._parked`, `SwitchToProfileAsync(..., keepCurrentForRestore: true)`): sigue reproduciendo sin superficie y en silencio, y restaurar lo vuelve a poner con un intercambio instantáneo en vez de abrir de nuevo (una sesión de secundario extra mientras dura el maximizado; se libera al restaurar, cambiar de canal/división, limpiar o si muere).
