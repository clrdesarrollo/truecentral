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

`POST /api/auth/login|logout` · `GET /api/health` · `GET /api/drivers` · CRUD `/api/devices` + `POST /api/devices/probe` (valida credenciales y devuelve info SIN persistir — botón "Probar" del wizard) + `/{id}/channels` + `/{id}/snapshot/{ch}` · `POST /api/streams/request` → `{rtspUrl, token, expira}` · `POST /api/streaming/auth` (callback MediaMTX, solo loopback) · `GET /api/streams/active` · `GET /api/discovery/sadp` · CRUD `/api/users`.

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
- **M4 — Dahua + ONVIF + snapshots + discovery SADP**: ✅ **CÓDIGO COMPLETO** (2026-08-27). Driver Dahua con interop propio verificado contra dhnetsdk.h (login alta seguridad, GetDeviceType→modelo, GetSoftwareVersion→firmware, QueryChannelName, snapshot por CGI HTTP digest); driver ONVIF con SOAP manual (WS-UsernameToken + corrección de reloj, GetStreamUri → URLs guardadas por canal e inyección de credenciales en MediaMTX); SADP copiado + `/api/discovery/sadp` + botón "Buscar en la red" en el panel. Canales reportados como deshabilitados/sin cámara por el equipo entran ocultos automáticamente (pedido del usuario). **Pendiente: validar Dahua y ONVIF contra hardware real** (structs x64 de Dahua sin probar en vivo). Snapshots ya estaban desde M2.
- **M5 — Pulido**: dashboard sesiones + kick, reinicio MediaMTX ante crash, estados offline, README.

## Riesgos vigilados

Forma exacta del body de auth de MediaMTX (verificar contra docs v1.20 en M3) · marshaling x64 Dahua (harness de consola contra hardware real antes de integrar) · acople versión FlyleafLib↔FFmpeg (pinear juntos, LGPL shared build) · encoding URL de contraseñas con `@`/`:` en las source URLs · grant vencido en retry del cliente (re-pedir siempre) · interop Hik viejo + DLLs 6.1.9.48 (validar tamaños de structs en M2).
