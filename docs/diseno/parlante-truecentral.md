# TrueCentral Speaker — parlante IP en software (Raspberry Pi y Windows)

Estado (2026-10-18): **etapa S1 implementada y probada de punta a punta** en Windows contra un servidor de prueba
(enrolamiento por página local y por consola, voz en vivo, sonido del servidor, volumen en ambos sentidos, tono,
caída/reconexión y revocación). Falta probarla en una Raspberry Pi real (publica para linux-arm64; la salida ALSA
no se ha ejercitado en hardware). Instalación: [installer/speaker/README.md](../../installer/speaker/README.md).

Lo implementado en S1 difiere del borrador en: puerto por omisión del servidor 5090 sobre `ws://` (wss con huella
fijada al enrolar), códec del flujo a-law 8 kHz (el que ya entregan `TalkSession` y `WorkflowStore`), código de
8 caracteres sin 0/O/1/I/L, y el agente sin `UseSystemd` (unidad `Type=simple`).

## 1. Objetivo

Un programa que convierte una Raspberry Pi (o un PC con Windows) con parlante, amplificador y, opcionalmente,
micrófono y entradas/salidas, en un **parlante IP de TrueCentral** con todo lo que hoy ofrece un IPSpeaker
Hikvision (DS-QAZ) y algo más:

| Función del IPSpeaker | Hikvision DS-QAZ | TrueCentral Speaker |
|---|---|---|
| Voz en vivo del operador (mantener para hablar) | ISAPI TwoWayAudio, G.711 8 kHz | Sí, PCM 16 kHz / Opus (mejor calidad) |
| Sonidos del servidor (alertas, workflows) | flujo G.711 | Sí, mismo canal |
| Biblioteca local (subir, listar, renombrar, borrar, descargar, tocar, detener) | Sí | Sí |
| Texto a voz en el equipo | Sí (≤100 caracteres) | Sí, Piper offline en español (sin límite práctico) |
| Volumen de salida | Sí | Sí + ganancia y limitador |
| Estado de reproducción | Sí | Sí, empujado al servidor (sin sondeo) |
| Prueba de altavoz (tono) | Sí | Sí |
| Programación de anuncios (planes semanales) | Sí | Sí, se ejecuta aunque no haya servidor |
| Prioridades (en vivo > programado > local) | Sí | Sí, con mezcla o interrupción configurable |
| Entrada física (IO-1) con vinculación local | Sí | Sí, varias entradas GPIO + disparador de automatizaciones |
| Salidas (relé, baliza) | No | Sí, GPIO (encender amplificador, baliza, sirena) |
| Escucha del micrófono ambiente | RTSP (alaw) | Sí, a demanda, con permiso propio y auditoría |
| Sincronía entre varios parlantes | **No** (la hace el VMS a 80 ms) | Sí, reproducción con marca de tiempo (< 10 ms entre equipos) |
| Detrás de NAT / 4G | Solo por ISUP | Sí: el equipo llama al servidor |
| SIP | Sí | Fase posterior (opcional) |

## 2. Decisión principal: protocolo propio, no emular ISAPI

Se evaluaron dos caminos:

1. **Emular un DS-QAZ (ISAPI + Digest)** para que TrueCentral lo use con el driver `hikvision-isapi` sin cambios.
   Descartado: hereda las rarezas de ese firmware (chunked que no suena, `speakerVolume` de solo lectura, G.711
   8 kHz, sondeo cada 60 s), obliga a que el servidor alcance al equipo por IP y no deja crecer (GPIO, micrófono,
   sincronía). Además sería imitar un producto ajeno (ver la regla de identidad propia).
2. **Driver nuevo `truecentral-agent` con conexión saliente del equipo al servidor.** Elegido.

El equipo abre **un WebSocket TLS hacia el servidor** (`wss://<servidor>:5090/api/speaker-agents/connect`), igual
que ISUP en espíritu: funciona detrás de NAT, 4G o MPLS, no abre puertos entrantes y el servidor se entera al
instante de la caída. Encaja en lo que ya existe: `ISpeakerDriverFactory` ya tiene `UsesIsup` y
`SpeakerConnectionInfo` ya lleva un ID en lugar de host; se suma el equivalente `UsesAgent` / `AgentId`.

## 3. Arquitectura

```
 Raspberry Pi / Windows                                      Servidor TrueCentral
┌───────────────────────────────────────┐            ┌─────────────────────────────────────┐
│ TrueCentralVms.SpeakerAgent           │   wss      │ SpeakerAgentHub (endpoint WS)       │
│  ├ Conexión (WS, reconexión, cola)    │◄──────────►│  ├ enrolamiento / token del equipo  │
│  ├ Mezclador y prioridades            │  control   │  └ sesiones vivas por AgentId       │
│  ├ Salida de audio (ALSA / WASAPI)    │  JSON +    │ AgentSpeakerDriver : ISpeakerDriver │
│  ├ Biblioteca (archivos + índice)     │  audio     │ SpeakerService (sin cambios de API) │
│  ├ TTS (Piper)                        │  binario   │ Workflows: disparador speaker-input │
│  ├ Planificador de anuncios           │            │ Web #/speakers, cliente WPF         │
│  ├ GPIO (entradas / salidas)          │            └─────────────────────────────────────┘
│  ├ Micrófono (escucha)                │
│  └ Web local de puesta en marcha :8088│
└───────────────────────────────────────┘
```

### 3.1 Tecnología del agente

- **.NET 10**, igual que el resto de la solución; un solo código para `linux-arm64` y `win-x64`, publicación
  autocontenida. Reutiliza `TrueCentralVms.Core` (DTOs, `G711`) y el estilo del repo.
- **Servicio del sistema**: `Microsoft.Extensions.Hosting` con `UseSystemd()` en Linux y `UseWindowsService()` en
  Windows.
- **Salida de audio** detrás de `IAudioOutput`:
  - Linux: ALSA por P/Invoke a `libasound` (`snd_pcm_writei`), dispositivo elegible (`hw:`/`plughw:`), latencia
    ~40 ms.
  - Windows: NAudio 2.2.1 (ya usado en el cliente) con WASAPI compartido.
- **Entrada de audio** (`IAudioInput`): ALSA capture / NAudio `WaveInEvent`.
- **Decodificación de archivos**: `ffmpeg` (en la Pi por `apt`, en Windows incluido en el instalador como en el
  servidor). Al subir un archivo se normaliza una vez a WAV PCM 48 kHz mono y se guarda el original para
  "Descargar".
- **TTS**: [Piper](https://github.com/rhasspy/piper) offline, voces `es_ES`/`es_MX` (corre en tiempo real en
  Pi 4/5). En Windows, Piper también; SAPI como respaldo.
- **GPIO**: `System.Device.Gpio` (libgpiod) en la Pi. En Windows no hay GPIO: las entradas/salidas se pueden
  delegar a un módulo ADAM ya integrado o quedan deshabilitadas.

### 3.2 Hardware de referencia (Pi)

- Raspberry Pi 4 (2 GB) o Pi 5; Pi Zero 2 W para equipos sin TTS pesado.
- Audio: HAT DAC + amplificador clase D (HiFiBerry MiniAmp/Amp4, IQaudio) o tarjeta USB + amplificador externo
  para bocina de exterior.
- Micrófono USB o HAT con micrófono (opcional).
- Entradas GPIO con optoacoplador (pulsador de pánico, contacto seco); salida a relé (habilitar amplificador,
  baliza).
- Alimentación PoE (HAT PoE+) para instalar como un parlante IP más.
- Sistema de archivos raíz en solo lectura (overlayfs) y datos en `/var/lib/truecentral-speaker` para proteger la
  SD ante cortes de luz.

## 4. Protocolo agente ↔ servidor

Un solo WebSocket por equipo. Mensajes de texto = JSON de control; mensajes binarios = audio.

### 4.1 Enrolamiento y seguridad

1. En la web (#/speakers → *Agregar* → *TrueCentral Speaker*) el administrador crea el parlante y obtiene un
   **código de enrolamiento** de un solo uso (8 caracteres, vence en 15 min) y la huella SHA-256 del certificado
   del servidor.
2. En la web local del equipo (`http://<equipo>:8088`) se ingresa la dirección del servidor y el código; el equipo
   verifica la huella mostrada (fijación de certificado, el servidor suele tener certificado propio).
3. El servidor responde un **token del equipo** (256 bits; se guarda solo su hash) ligado al `AgentId`. El equipo
   lo guarda cifrado (DPAPI en Windows, archivo 0600 en Linux).
4. Revocar desde la web corta la conexión (mismo patrón que `RevokeUser` / `HubConnections`).
5. La web local exige una clave propia que se define en el primer arranque; después del enrolamiento puede
   quedar solo en lectura (la configuración la manda el servidor).

Todo el enrolamiento, la revocación y los cambios quedan en la bitácora (categoría `speakers`).

### 4.2 Mensajes de control (resumen)

Servidor → agente (peticiones con `id`, el agente responde `{id, ok, error?, data?}`):

| `op` | Datos | Mapea a `ISpeakerDriver` |
|---|---|---|
| `hello.ack` | config completa (volumen, GPIO, planes, prioridades) | — |
| `probe` | — | `ProbeAsync` (modelo, versión, capacidades) |
| `library.list` / `upload` / `delete` / `rename` / `download` | id, nombre, bytes en binario | biblioteca |
| `tts.create` | texto, idioma, voz | `CreateTtsAsync` |
| `play` / `stop` | audioId o texto TTS, repeticiones, prioridad, `startAt` | `PlayLibraryAsync` / `StopAsync` |
| `volume.get` / `volume.set` | 0..100 | volumen |
| `live.open` / `live.close` | códec, prioridad, `startAt` | `OpenLiveAudioAsync` |
| `listen.open` / `listen.close` | — | escucha del micrófono (nuevo) |
| `output.set` | salida, estado, pulso ms | relé (nuevo) |
| `test.tone` | segundos | prueba de altavoz |
| `config.set` | sección cambiada | — |

Agente → servidor (eventos, sin respuesta):
`state` (reproduciendo / qué / prioridad), `input` (entrada GPIO cambió), `health` (temperatura, CPU, disco,
dispositivo de audio presente, versión), `log` (errores relevantes).

### 4.3 Audio

- Trama binaria: cabecera de 16 bytes (`streamId` u16, `seq` u32, `timestamp` u64 en µs de reloj del servidor,
  `codec` u8, reservado) + carga.
- Códecs: `pcm16` 16 kHz (LAN), `opus` 16/48 kHz (enlaces lentos, 4G), `alaw`/`ulaw` 8 kHz (compatibilidad con
  lo que hoy genera `WorkflowStore` y `TalkSession`). Fase 1 acepta `pcm16` 8 kHz, que es lo que ya entrega
  `ISpeakerAudioSession`; el driver no necesita convertir nada.
- **Sincronía**: el agente mide el desfase de reloj con el servidor (ping de 4 marcas tipo NTP cada 10 s sobre
  el mismo WS) y reproduce cada trama en `timestamp + retardo de grupo` (por omisión 150 ms) con un búfer de
  fluctuación. Varios agentes con el mismo `startAt` suenan alineados aunque la red no sea pareja; ya no depende
  del lock-step de 80 ms de `SpeakerService` (que sigue sirviendo para mezclar con equipos Hikvision).
- Escucha del micrófono: el agente envía Opus 16 kHz al servidor, que lo reenvía al cliente que pidió escuchar
  (mismo WebSocket `/api/speakers/talk` en sentido inverso o uno nuevo `/api/speakers/listen`).

## 5. Comportamiento del agente

### 5.1 Mezclador y prioridades

Cuatro niveles, configurables desde el servidor: **en vivo (10) > servidor/automatización (8) > programado (5) >
local/GPIO (3)**. Política por nivel: *interrumpir* (por omisión) o *mezclar* con atenuación del fondo
(-12 dB, "ducking"). Al terminar un nivel alto, el bajo se reanuda o se descarta según el caso (un anuncio
programado no se reanuda a la mitad; la música de fondo sí). Limitador a -1 dBFS en la salida para que la
ganancia (`Speakers:TalkGain`) no sature la bocina.

### 5.2 Sin servidor

- La biblioteca, los planes de anuncios y las vinculaciones locales (entrada GPIO → tocar audio / activar salida)
  se guardan en el equipo y **funcionan sin conexión**.
- Los eventos (`input`, `state`, errores) se encolan (hasta 1.000) y se envían al reconectar con su hora real.
- Reconexión con espera exponencial (1 s → 30 s) y aviso en el panel de Supervisor de servicios del servidor.

### 5.3 Puesta en marcha local (`:8088`)

Página mínima (no es otro VMS): dirección del servidor y enrolamiento, dispositivo de salida/entrada, prueba de
tono y vúmetro del micrófono, mapeo de pines GPIO, red (IP fija en la Pi), versión y reinicio. Una vez enrolado,
todo lo demás se administra desde TrueCentral.

### 5.4 Actualizaciones

El servidor publica los paquetes del agente (`.deb` arm64 y `.exe` Windows) en una ruta descargable; el agente
compara versión en el `hello` y, si el administrador lo aprueba desde la web, descarga, verifica la firma
SHA-256 y se reinstala (systemd / servicio de Windows reinician solos).

## 6. Cambios en TrueCentral

1. **Core**
   - `SpeakerConnectionInfo`: `AgentId`.
   - `ISpeakerDriverFactory.UsesAgent`.
   - `SpeakerCapabilities`: `SupportsListen`, `SupportsIo` (+ lista de entradas/salidas), `SupportsSchedules`,
     `SupportsSync`. Valores falsos para los drivers Hikvision actuales.
2. **Server**
   - Entidad `SpeakerAgent` (AgentId, hash del token, código de enrolamiento y vencimiento, última conexión,
     versión, salud) ligada 1:1 a `Speaker`; `Speaker.AgentId`. Migración.
   - `SpeakerAgentHub`: endpoint WS, autenticación por token, mapa `AgentId → conexión`, peticiones con
     correlación y tiempo de espera, ping de reloj.
   - `AgentSpeakerDriver : ISpeakerDriver` (clave `truecentral-agent`) que traduce cada método a un `op`. Como el
     estado llega empujado, `SpeakerService` deja de sondear estos equipos (`MarkOnlineAsync` al conectar, como
     ISUP).
   - API: `POST /api/speakers/{id}/enroll` (genera código), `DELETE /api/speakers/{id}/enroll` (revoca),
     `POST /api/speakers/{id}/outputs/{n}`, `GET /api/speakers/{id}/listen` (WS), CRUD de planes de anuncios y de
     vinculaciones locales, `GET /api/speakers/agent/package/{plataforma}`.
   - **Automatizaciones**: disparador `speaker-input` (equipo, entrada, flanco) y acción `speaker-output`; la
     acción `speaker` existente sirve tal cual.
3. **Web y cliente WPF**: tipo "TrueCentral Speaker" en el alta con el código de enrolamiento; pestañas de
   Entradas/Salidas, Planes y Salud; botón "Escuchar" en el panel PARLANTES del cliente (solo con permiso).
4. **Permisos** (regla de 6 pasos): nuevo `speakers.listen` («Escuchar micrófono de parlantes»), separado de
   `speakers.play` por privacidad; `speakers.configure` cubre enrolamiento, planes y GPIO. Validar el parlante
   concreto contra el alcance del usuario, no solo su ubicación.
5. **Auditoría** (checklist de 6 pasos): enrolar, revocar, conectar/desconectar, escuchar (inicio y fin, quién),
   activar salida, crear/editar planes, actualizar el agente.
6. **Licencia** (propuesta): cada agente consume un cupo de `SpeakerChannels` como cualquier parlante (sin
   módulo nuevo); opcionalmente una característica `SpeakerAgent` para venderlo aparte del módulo de parlantes.
   Subir versión del catálogo e importarlo en el servidor de licencias.
7. **Instaladores**: `installer/speaker-agent.iss` (Windows, Inno Setup como los demás) y un `.deb` con unidad
   systemd para Raspberry Pi OS (Bookworm, 64 bits) + script de una línea; etapa en Jenkins.

## 7. Estructura propuesta en el repo

```
src/TrueCentralVms.SpeakerAgent/          # servicio (net10.0, linux-arm64 + win-x64)
  Connection/   AgentConnection, ClockSync, OutboundQueue
  Audio/        IAudioOutput, AlsaOutput, WasapiOutput, Mixer, JitterBuffer, Limiter, IAudioInput
  Library/      LibraryStore (archivos + index.json), FfmpegTranscoder
  Tts/          ITtsEngine, PiperTts, SapiTts
  Io/           IGpio, LinuxGpio, NullGpio, LocalLinkage
  Scheduling/   AnnouncementScheduler
  Setup/        página local :8088
src/TrueCentralVms.Core/Contracts/SpeakerAgentProtocol.cs   # mensajes compartidos agente/servidor
src/TrueCentralVms.Server/Services/SpeakerAgents/           # hub + driver
installer/speaker-agent.iss, installer/debian/
```

## 8. Etapas

| Etapa | Contenido | Resultado verificable |
|---|---|---|
| S1 | Agente base, enrolamiento, WS, `probe`, volumen, audio en vivo `pcm16` 8 kHz, salud | "Mantener para hablar" y sonidos de workflows suenan en una Pi desde el cliente WPF |
| S2 | Biblioteca completa + estado empujado + prueba de tono | Web #/speakers opera la biblioteca igual que con un DS-QAZ |
| S3 | TTS Piper en español | Acción de automatización con texto suena en la Pi |
| S4 | GPIO: entradas → disparador `speaker-input`, salidas → acción; vinculación local offline | Pulsador de pánico en la Pi dispara una automatización |
| S5 | Sincronía por marca de tiempo + Opus | Dos Pi en red con 4G/LAN suenan alineadas (medido con grabación) |
| S6 | Planes de anuncios y prioridades/mezcla | Anuncio programado suena sin servidor |
| S7 | Escucha del micrófono con permiso y auditoría | Operador con permiso escucha; sin permiso, 403 auditado |
| S8 | Instaladores (.deb, Inno), actualización desde el servidor, imagen de SD lista | Pi nueva en marcha en < 10 min |
| S9 (opcional) | Cuenta SIP para recibir llamadas/paging de centrales | Llamada desde una central SIP suena |

## 9. Riesgos y preguntas abiertas

- **Latencia de audio en la Pi**: ALSA directo debería dar < 50 ms; validar con el HAT elegido.
- **Calidad del TTS** de Piper en español chileno: probar voces `es_MX`/`es_ES` antes de comprometerlo.
- **Privacidad** de la escucha ambiente: ¿se exige indicador luminoso en el equipo mientras se escucha?
- **Licencia**: ¿cuenta como un parlante más o se vende como producto propio?
- **Windows sin GPIO**: ¿basta con delegar en un ADAM, o se necesita soporte de algún módulo USB de relés?
- **Hardware oficial**: definir un kit (Pi + HAT + caja IP66 + PoE) para que soporte e instalación sean uniformes.
