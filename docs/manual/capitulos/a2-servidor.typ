#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/appsettings.json (claves y valores por omisión),
// installer/config/appsettings.Local.json, installer/suite.iss (reglas de firewall)

= Puertos y ajustes del servidor <ap-servidor>

Este apéndice es para quien instala y mantiene el servidor: qué puertos usa,
cuáles debe permitir la red y los ajustes que no tienen página en el panel web.

== Puertos

El instalador del servidor crea las reglas del firewall de Windows para estos
puertos. Si entre los equipos, los puestos y el servidor hay otro firewall
(de la red o de un enlace remoto), también debe permitirlos.

#table(
  columns: (auto, auto, 1fr),
  table.header[Puerto][Protocolo][Uso],
  [5090], [TCP], [Panel web y conexión de los clientes de monitoreo.],
  [8654], [TCP], [Video en vivo y reproducción hacia los clientes de
    monitoreo.],
  [8660], [UDP y TCP], [Vista en vivo del panel web (video hacia el
    navegador).],
  ..if tiene-modulo("alarms") {(
    [5091], [TCP], [Centro receptor de alarmas: reportes de paneles
      configurados con SIA DC-09.],
    [7091, 7660–7667, 8661], [TCP], [Registro y eventos de los paneles de alarma
      Hikvision que reportan a la receptora instalada junto al servidor.],
  )},
  [5092], [TCP], [Conexión de los paneles de cerco eléctrico.],
  [5093], [TCP], [Descarga de firmware de los paneles de cerco eléctrico.],
)

El servidor también usa puertos internos que no deben abrirse hacia la red:
la base de datos (25490), el control del servidor de video (9911 y 9914)
#modulo("alarms")[y la página de administración de la receptora de alarmas
(8091, que el instalador bloquea)].

#modulo("videowall")[
  #nota[Para proyectar una pantalla o un video al muro, el puesto que proyecta
    debe recibir conexiones en el puerto 8554/TCP; el instalador del cliente
    crea esa regla (vea #capitulo(<cap-muro-de-video>)).]
]

== El archivo de ajustes

Los ajustes propios de cada instalación van en el archivo
`appsettings.Local.json`, en la carpeta del servidor (por omisión,
`C:\Program Files\CLR TrueCentral VMS\Server`). Las actualizaciones del sistema
nunca lo sobrescriben.

Para cambiar un ajuste:

+ Abra el archivo con el Bloc de notas ejecutado como administrador.
+ Agregue la sección y la clave dentro de las llaves principales, por ejemplo:
  ```json
  {
    "Security": { "PasswordMaxAgeDays": 90 },
    "Anpr": { "RetentionDays": 30 }
  }
  ```
+ Guarde el archivo.
+ Reinicie el servidor desde #menu("Sistema", "Servicios") con
  #boton("Reiniciar servidor completo") (vea #capitulo(<cap-sistema>)). Los
  ajustes solo se leen al arrancar.

#importante[Si el archivo queda con un error de formato (una coma o una llave
  de más o de menos), el servidor no arranca. Guarde una copia antes de
  editarlo.]

== Ajustes disponibles

Cada ajuste se escribe como `Sección` › `Clave`. En la columna _Por omisión_
está el valor que usa el servidor si el archivo no lo indica.

=== Seguridad

#table(
  columns: (auto, auto, 1fr),
  table.header[Ajuste][Por omisión][Qué controla],
  [`Security` › `PasswordMaxAgeDays`], [0], [Días que dura una contraseña antes
    de que el sistema exija cambiarla. 0 = no vencen.],
)

=== Conservación de los registros

El servidor borra solo los registros más antiguos que estos límites. En los
que tienen dos límites, se aplica el primero que se alcance; un 0 quita ese
límite.

#table(
  columns: (auto, auto, 1fr),
  table.header[Ajuste][Por omisión][Qué controla],
  [`Audit` › `RetentionDays`], [0], [Días que se conserva la bitácora de
    auditoría. 0 = para siempre.],
  ..if tiene-modulo("access") {(
    [`Access` › `EventRetentionDays`], [365], [Días que se conservan los
      registros de acceso (entre 7 y 3650).],
  )},
  ..if tiene-modulo("alarms") {(
    [`Alarms` › `RetentionDays` \ `Alarms` › `MaxEvents`], [365 \ 500 000],
    [Historial de eventos de los paneles de alarma.],
  )},
  ..if tiene-modulo("anpr") {(
    [`Anpr` › `RetentionDays` \ `Anpr` › `MaxEvents`], [90 \ 200 000],
    [Lecturas de patentes, con sus fotos.],
  )},
  ..if tiene-modulo("automation") {(
    [`Workflows` › `RetentionDays` \ `Workflows` › `MaxRuns`], [90 \ 50 000],
    [Historial de ejecuciones de las automatizaciones, con sus fotos.],
  )},
)

=== Red y video

#table(
  columns: (auto, auto, 1fr),
  table.header[Ajuste][Por omisión][Qué controla],
  [`Urls` y `Streaming` › `ServerPort`], [`http://*:5090` \ 5090], [Puerto del
    panel web. Si lo cambia, cambie los dos al mismo número.],
  [`Streaming` › `PublicHost`], [vacío], [Nombre o IP con que los clientes y
    navegadores llegan al servidor cuando hay NAT o un nombre DNS (por
    ejemplo, `vms.miempresa.cl`). Vacío = la dirección que usó cada cliente.],
  [`Streaming` › `WebRtcIcePort`], [8660], [Puerto del video de la vista en
    vivo web. 0 desactiva la vista en vivo del panel web.],
  [`Streaming` › `WebRtcAdditionalHosts`], [vacío], [Otras IP o nombres del
    servidor que se ofrecen a los navegadores (varias redes o NAT), separados
    por comas.],
)

#modulo("alarms")[
  === Paneles de alarma

  #table(
    columns: (auto, auto, 1fr),
    table.header[Ajuste][Por omisión][Qué controla],
    [`Alarms` › `Receiver` › `Enabled` \ `Alarms` › `Receiver` › `Port`],
    [true \ 5091], [Centro receptor SIA DC-09: encendido y puerto.],
  )
]

=== Cerco eléctrico

#table(
  columns: (auto, auto, 1fr),
  table.header[Ajuste][Por omisión][Qué controla],
  [`Cerco` › `Receiver` › `Port`], [5092], [Puerto al que se conectan los
    paneles.],
  [`Cerco` › `Receiver` › `PublicUrl`], [vacío], [Dirección que se entrega a
    los paneles al crearlos, cuando llegan al servidor por NAT, un nombre DNS o
    un proxy con TLS (por ejemplo, `wss://vms.miempresa.cl/cerco`).],
  [`Cerco` › `Firmware` › `Port`], [5093], [Puerto de descarga del
    firmware.],
  [`Cerco` › `Firmware` › `PublicHost`], [vacío], [IP o nombre con que el panel
    llega al servidor para descargar el firmware. Fíjelo solo con NAT.],
)

#modulo("intercom")[
  === Citofonía

  #table(
    columns: (auto, auto, 1fr),
    table.header[Ajuste][Por omisión][Qué controla],
    [`Intercom` › `MaxRingSeconds`], [90], [Segundos que una llamada sigue
      sonando sin que nadie conteste (entre 15 y 600).],
    [`Intercom` › `MaxTalkMinutes`], [10], [Duración máxima de una
      conversación, en minutos (entre 1 y 60).],
  )
]

=== Supervisión de servicios

#table(
  columns: (auto, auto, 1fr),
  table.header[Ajuste][Por omisión][Qué controla],
  [`Supervisor` › `CheckSeconds`], [10], [Cada cuántos segundos se revisa el
    estado de los servicios internos.],
  [`Supervisor` › `MaxRestartAttempts` \ `Supervisor` › `RestartWindowMinutes`],
  [5 \ 10], [Reintentos automáticos de un servicio caído dentro de esa ventana
    de minutos, antes de dejarlo detenido y avisar.],
)
