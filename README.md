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

(El producto videowall `vwcontroller` usa 5080/25480/8554: coexisten en la
misma máquina.)

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
- PTZ: panel siempre presente (minimizado por defecto) con controles
  habilitados solo si el cuadro seleccionado tiene cámara PTZ
  (pan/tilt/zoom/foco/iris continuos + presets 1..300). Manejo por teclado:
  **flechas** = pan/tilt, **+/−** = zoom, y **Shift sostenido** = modo
  precisión (velocidad mínima, con píldora indicadora en el panel).
- Reproducción: las grabaciones viven en el disco del propio equipo (el VMS
  no almacena video). El doble clic en el árbol suma canales hasta **4
  sincronizados** (con la grilla llena reemplaza el cuadro con foco; Ctrl +
  doble clic deja solo ese canal) y todos reproducen el mismo instante.
- Línea de tiempo con una pista por canal, tramos coloreados por tipo y
  **zoom de 24 h a 1 minuto** (botones − / + o rueda del mouse): el clic
  salta a esa hora y el **arrastre** mueve la aguja mostrando la hora de
  destino. Reloj con fecha y hora completas, saltos de ±30 s y ±5 min y
  velocidad 0,25×–8×.
- Doble clic sobre el video como en un reproductor: tercio izquierdo −30 s,
  tercio derecho +30 s y centro para entrar o salir de pantalla completa
  (Esc también sale).
- Botón **Recorte**: arrastrando sobre la línea se marca un tramo para
  reproducirlo o **exportarlo a MP4** a la carpeta de grabaciones (el
  servidor lo arma con FFmpeg mientras el equipo lo entrega; se puede
  cancelar). Hikvision y Dahua se posicionan a la hora exacta; en ONVIF el
  estándar no lo permite y el cliente avisa.
- Credenciales recordadas cifradas con DPAPI, inicio de sesión automático
  opcional, lista de usuarios recientes.

## Panel web

`#/` dashboard (salud, dispositivos, sesiones activas) · `#/devices`
mantenedor con wizard "Probar conexión", canales, revalidación, snapshots y
descubrimiento SADP · `#/sessions` sesiones de video en vivo con **Expulsar**
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

Pendientes conocidos: transporte por SDK para equipos sin RTSP; mapas, muro
de video y eventos (la arquitectura no los bloquea); foco/iris por ONVIF
(servicio de imagen).
