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

## Riesgos vigilados

Patentes: el callback de mensajes de HCNetSDK es único por proceso y el delegado debe vivir en un campo estático (si el GC se lo lleva, el SDK llama a memoria liberada y el proceso cae) · las estructuras ITS del SDK cambian entre versiones: al actualizar HCNetSDK hay que revisar `ItsInterop` contra la cabecera nueva · Muro: el decodificador guarda su propio mapa de ventanas y el servidor re-sincroniza al arrancar (`WarmUpDecodersAsync`) — vigilar que un reinicio del servidor no deje ventanas huérfanas en el equipo · forma exacta del body de auth de MediaMTX (verificar contra docs v1.20 en M3) · marshaling x64 Dahua (harness de consola contra hardware real antes de integrar) · acople versión FlyleafLib↔FFmpeg (pinear juntos, LGPL shared build) · encoding URL de contraseñas con `@`/`:` en las source URLs · grant vencido en retry del cliente (re-pedir siempre) · interop Hik viejo + DLLs 6.1.9.48 (validar tamaños de structs en M2).
