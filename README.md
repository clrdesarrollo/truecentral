# CLR TrueCentral VMS

Software de gestión de video (VMS) cliente-servidor y multimarca: administra
cámaras, DVR, NVR y XVR de **Hikvision** y **Dahua** por SDK nativo, y el resto
de las marcas por **ONVIF**.

El servidor distribuye el video estilo *streaming media server*: abre **un solo
stream** contra cada equipo y lo reparte a **N clientes**, de modo que agregar
operadores no agrega carga al DVR/NVR.

```
Cliente WPF (xN) ─┐
                  ├── REST + SignalR ──► TrueCentral VMS Server ──┬─► SDK nativo / ONVIF (gestión, PTZ)
Panel web         ─┘                              │               └─► RTSP (1 pull por canal)
                                                  ▼
                                          MediaMTX embebido
                                       (fan-out RTSP a N clientes)
```

## Características

- **Multimarca**: driver Hikvision (HCNetSDK), Dahua (NetSDK) y ONVIF genérico.
  Agregar una marca es implementar `IDeviceDriverFactory` y registrarla.
- **Alta de equipos validada**: al agregar un dispositivo se inicia sesión
  contra el equipo real y se obtienen modelo, número de serie, firmware,
  canales y puerto RTSP; si las credenciales fallan no se guarda nada.
- **Streaming 1→N** con MediaMTX embebido, autorización delegada por tokens de
  corta vida y auditoría de sesiones (quién vio qué canal y cuándo).
- **Control PTZ** por SDK y por ONVIF, visible solo en canales que lo soportan.
- **PostgreSQL embebido**, privado del sistema: escucha únicamente en
  `127.0.0.1`, en un puerto propio y con credenciales generadas.
- **Seguridad de cuentas**: servidor desactivado de fábrica (el primer
  administrador se crea solo desde la máquina del servidor), política de
  contraseñas, historial que impide reutilizarlas y caducidad configurable.
- **Cliente WPF** con árbol de dispositivos, grilla 1/4/9/16 y video acelerado
  por GPU; **panel web** de administración sin dependencias ni build.

## Estructura

| Proyecto | Descripción |
|---|---|
| `TrueCentralVms.Core` | Contratos compartidos: DTOs, abstracción de drivers, política de contraseñas |
| `TrueCentralVms.Server` | ASP.NET Core (API REST + SignalR + panel web + PostgreSQL y MediaMTX embebidos) |
| `TrueCentralVms.Client` | Cliente de escritorio WPF |
| `TrueCentralVms.Drivers.Hikvision` | Driver HCNetSDK (P/Invoke) |
| `TrueCentralVms.Drivers.Dahua` | Driver NetSDK (P/Invoke) |
| `TrueCentralVms.Drivers.Onvif` | Driver ONVIF (SOAP sobre HttpClient) |

## Puertos

| Servicio | Puerto | Escucha en |
|---|---|---|
| HTTP / API / SignalR / panel web | 5090 | todas las interfaces |
| PostgreSQL embebido | 25490 | 127.0.0.1 |
| MediaMTX RTSP (clientes) | 8654 | todas las interfaces (solo TCP) |
| MediaMTX API de control | 9911 | 127.0.0.1 |

Elegidos fuera de los valores estándar para convivir con otros productos en la
misma máquina.

## Puesta en marcha

Requisitos: **.NET 10 SDK** y Windows x64 (los SDK de los fabricantes son
nativos de 64 bits).

```powershell
# 1. Binarios de terceros (no viven en el repositorio)
.\build\setup-binaries.ps1

# 2. Compilar
dotnet build CLRTrueCentralVMS.slnx

# 3. Servidor
dotnet run --project src\TrueCentralVms.Server

# 4. Cliente de escritorio (en otra consola)
dotnet run --project src\TrueCentralVms.Client
```

En el primer arranque el servidor inicializa su clúster PostgreSQL y queda
**desactivado**: abra `http://localhost:5090` **en la máquina del servidor**
para crear el primer administrador (por seguridad el asistente solo acepta
conexiones locales).

### Binarios de terceros

No se versionan por tamaño (~700 MB). `build\setup-binaries.ps1` arma `native\`
a partir de los SDK que se descompriman en `Resources\`, y puede copiar
`tools\` desde otro checkout con `-FromProduct <ruta>`. El script indica qué
falta y de dónde obtenerlo.

| Carpeta | Contenido |
|---|---|
| `Resources\` | SDK de Hikvision y Dahua tal como los entrega el fabricante |
| `native\hikvision`, `native\dahua` | DLLs que el servidor carga en runtime |
| `tools\postgres` | PostgreSQL portable |
| `tools\mediamtx` | MediaMTX v1.20 |
| `tools\ffmpeg-flyleaf` | FFmpeg compartido que exige FlyleafLib (cliente) |

> La versión de FFmpeg debe coincidir con la que espera el paquete NuGet de
> FlyleafLib: use el asset `FFmpeg` del release con el mismo número de versión.

## Seguridad

Estos archivos se generan en runtime y **nunca** deben versionarse (ya están en
`.gitignore`):

| Archivo | Contiene |
|---|---|
| `pgdata\tcvms-pg.secret` | Contraseña del superusuario de PostgreSQL |
| `pgdata\tcvms-data.key` | Llave AES-256 que descifra las contraseñas de los equipos |
| `mediamtx.runtime.yml` | URLs RTSP de origen **con las credenciales de los equipos** |

Las contraseñas de los dispositivos se guardan cifradas con AES-256-GCM y las
de los usuarios con PBKDF2-SHA256 (100.000 iteraciones).

## Estado

Ver [PLAN.md](PLAN.md) para la arquitectura detallada y el avance por hitos.
