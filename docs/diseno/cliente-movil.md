# Plan: cliente móvil de TrueCentral (Android y iOS)

Borrador de planificación (30/10/2026). Solo planificación: no hay código todavía, salvo el proyecto Android vacío
creado en `..\CLRobotics TrueCentral Android Client`. Toma como referencia HikCentral Mobile (lo que se quiere
igualar), Milestone XProtect Mobile, Genetec Security Center Mobile y Dahua DSS, con nombres y UX propios de
TrueCentral.

**Decisión tomada:** apps **nativas**. Primero **Android (Kotlin + Jetpack Compose)**; **iOS (Swift + SwiftUI)**
después, con el mismo diseño.

## 1. Objetivo

1. Una app de **operador** para celular y tablet: ver, enterarse y actuar sobre lo que el usuario tiene en su
   alcance, desde la red local o desde Internet.
2. **Avisos en el teléfono** (push) aunque la app esté cerrada: alertas, llamadas del citófono, equipos caídos.
3. **Una sola app**: las funciones aparecen según la licencia del servidor y los permisos del usuario.

Fuera del alcance: la **configuración** del sistema (equipos, usuarios, automatizaciones) sigue en el panel web.

## 2. Referencias y propuesta propia

| Producto | Cómo lo resuelve |
|---|---|
| Hikvision | Seis apps separadas (cliente final, instalador, HikCentral, control de acceso…), de 110 a 342 MB; navegación por tipo de recurso |
| Milestone XProtect Mobile | Componente aparte en el servidor (*Mobile Server*); verificación en dos pasos; *Video Push* (la cámara del teléfono entra al sistema) |
| Genetec Security Center Mobile | Herramienta de respuesta: alarmas, tareas y mapa primero, video después |
| Dahua DSS | App propia del VMS con vivo, reproducción, alarmas, puertas y citófono |

Propuesta de TrueCentral:
- **Una app, «TrueCentral»** (nombre corto: el launcher corta los largos).
- La pantalla de inicio es **Atención**: lo que me toca resolver ahora (alertas por acusar, llamadas en curso,
  equipos caídos de mi alcance), apoyada en el panel de inicio que ya existe.
- Se navega **por Ubicaciones** (la capa lógica del web), no con pestañas por tipo de recurso.
- No hace falta un «servidor móvil» aparte: la app consume la misma API, el mismo hub y el mismo MediaMTX que el web.

### Pantallas

| Pestaña | Contenido |
|---|---|
| **Atención** | Alertas pendientes con acuse, llamadas del citófono, equipos fuera de línea, resumen de intrusión, cerco y puertas |
| **Ubicaciones** | Árbol según alcance → ficha de la ubicación con sus cámaras (miniaturas), puertas (abrir), parlantes (hablar), citófonos y E/S |
| **Vivo** | Grilla de 1 o 4 cuadros en teléfono (hasta 9 en tablet); vistas guardadas; tocar = pantalla completa con principal; PTZ por gestos |
| **Más** | Reproducción, Últimos accesos, Flota, cuenta y dispositivo, servidores |

## 3. Estado actual: lo que el servidor ya ofrece

| Necesidad de la app | Pieza existente |
|---|---|
| Iniciar sesión | `POST /api/auth/login` → token opaco de 12 h (`TokenService`, en memoria, sin renovación) |
| Perfil, permisos, módulos licenciados | `GET /api/auth/me` (alcance, permisos, roles, módulos, `IsOperational`), `GET /api/auth/account` |
| Eventos en vivo | Hub `/hubs/vms` (solo servidor → cliente), `ScopedHub` filtra por permiso y alcance; token por `?access_token=` |
| Árbol y recursos | `GET /api/locations` (plano, ya filtrado), `GET /api/resources?locationId=` (Camera, Door, Partition, Zone, Fence, Speaker, Intercom, IoModule) |
| Panel de inicio | `GET /api/dashboard` (equipos, intrusión, cerco, puertas, alertas pendientes, actividad, fuera de línea) |
| Video en vivo | `POST /api/streams/request` (RTSP con token de 60 s), `POST /api/streams/webrtc` (WHEP intermediado), WebSocket `/api/streams/mse` (fMP4) |
| Miniaturas | `GET /api/devices/{id}/snapshot/{channelNumber}` (caché 25 s, máx. 4 capturas simultáneas, ticket de URL) |
| PTZ | `POST …/ptz/move` (pan/tilt/zoom −1..1, renovar ≥1/s, hombre muerto a los 3 s), `…/ptz-preset` (Goto/Set/Clear) |
| Reproducción | `/api/playback/{dev}/{ch}/days`, `/segments`, `POST /api/playback/request` y `/webrtc`, descarga con ticket |
| Puertas | `GET /api/access/doors`, `POST /api/access/doors/{id}/command` (Open/Close/RemainOpen/RemainLocked), hub `AccessDoorStateChanged` |
| Últimos accesos | `GET /api/access/events` + hub `AccessEventReceived`; foto del padrón y foto del momento (PR #67, 0.5.36) |
| Alertas | `GET /api/workflows/alerts?pending=true`, `POST /api/workflows/alerts/{id}/ack`, hub `WorkflowNotification` / `WorkflowAlertAcknowledged` |
| Citofonía | Hub `IntercomCallChanged`, `POST /api/intercoms/calls/{id}/answer` (también `reject` y `hangup`), abrir puerta, voz por WebSocket, video `ic/{id}`, snapshot |
| Parlantes | WebSocket `/api/speakers/talk` (hablar en vivo), `POST /api/speakers/play` y `/stop` (pregrabados, TTS) |
| Flota | `GET /api/fleet/positions`, hub `FleetPositions` (lotes cada 2 s), recorridos, teselas del mapa intermediadas |
| Imágenes y descargas | Tickets de URL (`POST /api/auth/ticket`, `?ticket=` de 60 s): el token nunca va en una URL |
| HTTPS | 443 con CA propia de nombres restringidos; pin SPKI estable (el certificado se reemite con la misma llave) |
| Acceso desde Internet | Permiso `remote.access`, dirección pública, puertos publicados (`WanAccessService`) |
| Enrolar con código | Agente de parlantes (WebSocket) y base de rondas (`POST /api/patrol-agent/enroll`): código con hash y vencimiento, token de 32 B guardado como hash, pin del certificado al enrolar |

### Huecos que afectan al móvil

- **Sesión**: 12 h en memoria, sin renovación; un reinicio del servidor la borra. El WPF «renueva» repitiendo el
  login con la clave guardada. En un teléfono eso significa pedir la clave o guardarla.
- **Puestos**: un cliente que manda `X-TCVMS-Client` cuenta como **puesto de escritorio** (`max_client_sessions`).
- **Origen**: la bitácora y el acuse solo distinguen `web` / `client` / `screen`; no hay «móvil».
- **No hay 2FA** ni registro de dispositivos.
- **RTSP sin cifrar**: MediaMTX corre con `rtspEncryption: "no"` (puerto 8654, TCP). Por Internet el video RTSP viaja
  en claro (también para el WPF remoto).
- **No hay push** ni lista de presets PTZ.

## 4. Arquitectura

```
   Teléfono (Kotlin / Swift)
     ├─ REST   https://servidor/api/...        Bearer de sesión (canjeado con la credencial del dispositivo)
     ├─ Hub    wss://servidor/hubs/vms          eventos mientras la app está abierta
     ├─ Video  WebRTC (8660) · fMP4 por WSS (443) · RTSP (8654)      → MediaMTX
     └─ Voz    wss://servidor/api/intercoms/{id}/voice · /api/speakers/talk

   Servidor TrueCentral (instalación del cliente)
     └─ aviso ─► Relé de avisos CLRobotics (nube) ─► FCM (Google) / APNs (Apple) ─► teléfono
```

- Todo pasa por TLS con el **pin SPKI** del servidor; el teléfono no habla nunca con los equipos.
- El relé es la única pieza nueva fuera del servidor del cliente (§6).

## 5. Dispositivos móviles: enrolamiento y sesión (servidor, nuevo)

### Entidad

`MobileDevice`: usuario, nombre («iPhone de J. Pérez»), plataforma y modelo, versión de la app, hash de la
credencial, token de push (cifrado), alta, último contacto e IP, revocado (fecha y por quién).

### Enrolar

1. **Por QR (lo normal)**: en el web, *Mi cuenta → Celulares → Agregar*, el servidor muestra un QR con dirección local,
   dirección pública, puerto, **pin SPKI** y un **código de un solo uso** (10 min, guardado como hash, ligado al
   usuario). La app lo escanea, confía en el certificado por el pin y llama a `POST /api/mobile/enroll`.
2. **Manual**: dirección + usuario + clave; la primera vez muestra la huella del certificado para aceptarla, como el
   diálogo de confianza del WPF.
3. El servidor responde con una **credencial de dispositivo** (32 B aleatorios; guarda solo su SHA-256), igual que los
   agentes de parlantes y rondas.

### Sesión

- `POST /api/mobile/session` con la credencial → **token de sesión normal** de `TokenService`. La credencial vive en la
  BD, así que **un reinicio del servidor no obliga a volver a escribir la clave**: la app canjea de nuevo y sigue.
- En el teléfono la credencial queda en el **Android Keystore / Keychain de iOS**, protegida por **huella, rostro o PIN
  del equipo**. Dispositivo + biometría es el segundo factor que hoy no existe.
- Bloqueo de la app tras N minutos en segundo plano.
- `TokenService` distingue móvil de escritorio: **el móvil no consume puestos de escritorio**.

### Revocar

- *Mi cuenta → Celulares* (los propios) y una página de administración (todos, con permiso).
- Usuario deshabilitado o revocado: corta sesiones (`RevokeUser`, después de `SaveChanges`) y el canje de la
  credencial falla. La app, al recibir «revocado», **borra sus datos locales** (teléfono perdido).
- `remote.access` rige igual: con datos móviles el teléfono casi siempre es «remoto».

### Elegir dirección

La app guarda la dirección local y la pública; prueba la local con un tiempo corto y si no responde usa la pública.
El certificado ya incluye ambos nombres y el pin es el mismo. iOS exige permiso de **red local**
(`NSLocalNetworkUsageDescription`, lección de la app del muro).

## 6. Avisos (push) y relé de CLRobotics

### Por qué un relé

Las llaves de FCM y APNs son del **dueño de la app** (CLRobotics). No se pueden repartir en cada servidor instalado:
quien la extraiga podría mandar avisos a los usuarios de todos los clientes.

### Diseño

- **Relé de avisos** en la nube de CLRobotics, junto al servidor de licencias (mismo despliegue Jenkins + compose).
  Cada servidor TrueCentral se autentica con la **identidad de su instalación** (la que ya usa el heartbeat de
  licencias; verificar cómo se firma hoy).
- La app registra su token de FCM/APNs **en su propio servidor**, nunca en el relé directamente.
- **Contenido mínimo**: por Google/Apple no viajan nombres, patentes ni fotos. El aviso dice «Nueva alerta» con un
  identificador opaco; la app **trae el detalle de su servidor** antes de mostrarlo (Android: mensaje de datos de alta
  prioridad; iOS: *Notification Service Extension*). Opción por servidor para mostrar el título en el aviso.
- **Qué se avisa** a cada dispositivo: lo mismo que filtra `ScopedHub` (permiso + alcance), más las preferencias del
  usuario (tipos de alerta, gravedad mínima, silencio por horario).
- **Llamadas del citófono**: Android con mensaje de alta prioridad + notificación de llamada a pantalla completa
  (`CallStyle`, `USE_FULL_SCREEN_INTENT`); iOS con **PushKit + CallKit**. El primero que contesta toma la llamada;
  los demás ven «atendida por …» (`IntercomCallChanged`).
- **Sitio sin Internet**: no hay push. En Android se puede ofrecer una **conexión permanente** (servicio en primer
  plano con el hub abierto, a costa de batería); en iOS no es posible.
- Bitácora resumida de avisos enviados y fallidos.

## 7. Video

### Vías posibles

| Vía | Puertos | Cifrado | H.265 | Android | iOS |
|---|---|---|---|---|---|
| **WebRTC** (como el web) | 443 + ICE 8660 UDP/TCP | Sí (DTLS-SRTP) | Depende del SDK | libwebrtc | libwebrtc |
| **fMP4 por WebSocket** (`/api/streams/mse`) | Solo 443 | Sí (TLS con pin) | Sí | Media3 con fuente propia | Demux propio + VideoToolbox |
| **RTSP con token** (como el WPF) | 8654 TCP | **No** (sin RTSPS) | Sí | Media3 RTSP o LibVLC | LibVLC (VLCKit) |

- El MSE cuesta un FFmpeg por espectador en el servidor (remux, sin recodificar).
- Inclinación: **la misma estrategia que el panel web**: WebRTC primero y fMP4 por WSS cuando el teléfono no puede con
  el códec. RTSP solo en la red local o si se habilita RTSPS en MediaMTX (que también le serviría al WPF remoto).
- **Se decide con la prueba de concepto de la etapa 0**, con las cámaras reales (H.264 y H.265, principal y
  secundario), en Wi-Fi y en 4G: tiempo a la primera imagen, latencia, batería y datos.

### Reglas

- Grilla siempre con **secundario**; un cuadro a pantalla completa pasa a principal (como el WPF en automático).
- Al ir a segundo plano se corta el video (más adelante, imagen en imagen en Android).
- Concesión nueva en cada reconexión (el token vive 60 s y no se renueva), reintento cada 5 s como el WPF.
- Miniaturas con el snapshot y ticket de URL; respetar el máximo de 4 capturas simultáneas.
- Opción «solo secundario con datos móviles».
- PTZ: arrastrar = mover (renovar cada ≤1 s; soltar = 0,0,0), pellizcar = zoom, presets por número. Falta un endpoint
  que **liste los presets**.

### Reproducción

Calendario con `days`, línea de tiempo con `segments`, reproducción por la misma vía que el vivo
(`/api/playback/webrtc` o RTSP). Cada salto en la línea de tiempo es una concesión nueva. Descarga con ticket.

## 8. Voz: citofonía y parlantes

- Formato del servidor: **PCM 16 bits, mono, 8 kHz**, binario por WebSocket, en ambos sentidos (citófono) o solo de
  subida (parlantes). El WPF manda **640 bytes cada 40 ms**.
- El servidor corta tras **30 s sin audio** (citófono) o **20 s** (parlantes): la app manda tramas de silencio mientras
  la conversación siga abierta.
- Android: `AudioRecord` con fuente `VOICE_COMMUNICATION` + cancelador de eco; iOS: `AVAudioEngine` con
  *voice processing*.
- Parlantes: botón «mantener para hablar» por parlante o grupo; pregrabados y TTS con `/api/speakers/play`.

## 9. Seguridad

- **Pin SPKI** en todas las conexiones (REST, hub, WebSocket, señalización WebRTC), con la misma regla que
  `ServerTrust.cs` del WPF: se acepta si la cadena es válida **o** si el SPKI coincide con el pin guardado.
- Credencial del dispositivo en Keystore/Keychain con biometría; nada sensible en preferencias planas.
- Tokens nunca en URLs, salvo hub y WebSocket (`?access_token=`), como hoy; imágenes con ticket de URL.
- Contenido de los avisos sin datos personales (§6).
- Opcional por servidor: **bloquear capturas de pantalla** en el video (`FLAG_SECURE`; en iOS solo se puede detectar).
- Más adelante: Play Integrity / App Attest para rechazar equipos alterados.
- **Bitácora**: categoría nueva `mobile` (dispositivo enrolado, renombrado, revocado; canje de sesión
  aceptado/rechazado; push registrado). Origen `mobile` en la bitácora y en `AcknowledgedFrom` del acuse.

## 10. Licencias (propuesta)

| Clave | Tipo | Qué habilita |
|---|---|---|
| `module_mobile` | bool | App móvil (enrolar dispositivos) |
| `mobile_devices` | entero | Cupo de **dispositivos enrolados** (no simultáneos), aparte de `max_client_sessions` |

- Avisos push: incluidos en el módulo o como suscripción (el relé tiene costo de operación). Decisión abierta.
- Pendiente desde antes: la licencia de la vista en vivo del web (¿módulo propio o puestos?). Conviene decidir las dos
  juntas.
- Regla del proyecto: `LicenseFeatures` + `LicenseCatalog`, subir `Version`, `Deny` en las rutas, importar el catálogo
  al servidor de licencias.

## 11. Permisos

- Se reusan los actuales: `live.view`, `live.ptz`, `live.views`, `playback.view`, `playback.export`, `events.attend`,
  `alarms.monitor/operate`, `access.monitor`, `access.doors`, `intercom.answer`, `speakers.play`, `fleet.view/live`,
  `patrol.view`, `remote.access`.
- Nuevos (tentativos): `mobile.use` («Usar la app móvil»: quién puede enrolar celulares) y `mobile.manage` (ver y
  revocar los de otros).
- Regla: validar el **recurso concreto**, no solo su ubicación.

## 12. Tecnología

### Android (ahora)

| Pieza | Elección |
|---|---|
| Lenguaje e interfaz | Kotlin, Jetpack Compose, Material 3; minSdk 29 (Android 10) |
| Inyección | Hilt (como `citofono4g`) |
| HTTP y WebSocket | OkHttp (con `TrustManager` de pin SPKI); Ktor sobre OkHttp si se quiere seguir a `citofono4g` |
| Hub | Cliente **propio** del protocolo JSON de SignalR (el hub solo empuja eventos: negociación, saludo, invocaciones y ping). Sirve igual en Swift, donde no hay cliente oficial |
| Video | Según la etapa 0: libwebrtc, Media3 (con fuente fMP4 propia o RTSP) o LibVLC |
| Mapas (Flota) | MapLibre Native, con las teselas intermediadas por el servidor |
| Almacenamiento | DataStore + Keystore |
| Push | Firebase Cloud Messaging |
| Compilación | Gradle Kotlin DSL; Jenkins con el patrón del `Jenkinsfile` de `citofono4g` (firma con credenciales) |

### iOS (después)

Swift + SwiftUI, URLSession con evaluación de confianza propia (pin), el mismo cliente de hub, PushKit/CallKit,
Keychain. Requiere **Mac** (Mac mini como agente de Jenkins) y **cuenta de Apple Developer**.

### Contratos

Con dos apps nativas se pierde la ventaja de compartir los DTO de C# (la razón por la que la app del muro se hizo en
MAUI). Mitigación: publicar un **OpenAPI** de la superficie que usa el móvil y probar los contratos contra él.

### Identificador de la app

- Hoy el proyecto usa `com.clrobotics.truecentralmobileclient`.
- `cl.clrobotics.truecentral` ya lo usa la app MAUI del muro (`vwcontroller_ios`, APK firmado con la llave de depuración).
- `citofono4g` usa `com.clrobotics.*`.
- Recomendación: **`com.clrobotics.truecentral`**, el mismo para Android e iOS. Se fija antes de la primera subida a
  Play: después no se puede cambiar.

### Distribución

APK interno primero (como la app del muro) → Play en prueba cerrada → *Managed Google Play* para clientes con MDM.
iOS: TestFlight → App Store o *apps personalizadas* (Apple Business Manager). Apple exige un **servidor y una cuenta de
demostración** para revisar (base: el entorno demo del manual).

## 13. Etapas

| # | Etapa | Dónde | Entrega |
|---|---|---|---|
| 0 | Prueba de concepto de video | Android | WebRTC vs fMP4/WSS vs RTSP con cámaras reales, en Wi-Fi y 4G; decide la vía de video |
| 1 | Dispositivos y sesión | servidor + web + Android | `MobileDevice`, enrolar por QR y manual, canje de sesión, biometría, revocación, licencia, bitácora, puestos aparte |
| 2 | Ubicaciones y vivo | Android | Árbol por alcance, fichas, vivo 1/4 (tablet 9), vistas guardadas, PTZ, abrir puertas, hub en primer plano |
| 3 | Atención y avisos | servidor + relé + Android | Relé CLRobotics, registro de push, preferencias, Atención con acuse, detalle traído del servidor |
| 4 | Reproducción | Android | Calendario, línea de tiempo, reproducción y descarga |
| 5 | Voz | Android | Citófono con llamada entrante (pantalla completa) y parlantes «mantener para hablar» |
| 6 | iOS | iOS | Paridad con las etapas 1–5 (PushKit/CallKit) |
| 7 | Extras | ambos | Flota en mapa, Últimos accesos, botón de pánico, **teléfono como cámara** (WHIP a MediaMTX), rondas con NFC si los puntos lo admiten |

## 14. Riesgos

- H.265 por WebRTC en teléfonos: incierto según SDK y equipo (de ahí la etapa 0).
- El push depende de que el sitio tenga Internet y de un servicio nuevo que CLRobotics debe operar.
- Fabricantes Android que matan apps en segundo plano: llamadas del citófono poco confiables sin prioridad alta.
- Políticas de las tiendas: declaración de pantalla completa en Play; revisión de Apple con servidor de demo.
- Batería y datos con video por 4G.
- Contratos duplicados en Kotlin y Swift.

## 15. Decisiones abiertas

1. Identificador definitivo de la app (`com.clrobotics.truecentral` recomendado).
2. Vía de video (resultado de la etapa 0).
3. Contenido de los avisos: genérico siempre o título opcional por servidor.
4. Relé de avisos: incluido en la licencia o suscripción.
5. Enrolar por QR: ¿pide además la clave del usuario?
6. Cupo `mobile_devices` por dispositivo o por usuario.
7. Relación con la app del muro (`vwcontroller_ios`): ¿se retira, convive o la nueva app incluye operar el muro?
8. Conexión permanente en Android para sitios sin Internet.
9. Habilitar RTSPS en MediaMTX (beneficia también al WPF remoto).
10. Canal de distribución inicial: APK interno, Play cerrado o Managed Google Play.
