#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/app.js (renderDevices, deviceModal, channelsModal, initModal, changeIpModal), src/TrueCentralVms.Server/wwwroot/maintenance.js, src/TrueCentralVms.Server/Api/DevicesApi.cs, src/TrueCentralVms.Server/Api/DiscoveryApi.cs, src/TrueCentralVms.Server/Api/DeviceMaintenanceApi.cs

= Fuentes de video <cap-fuentes-de-video>

Las fuentes de video son las cámaras y los grabadores (DVR, NVR y XVR) que
entregan video al sistema. Desde #menu("Dispositivos", "Fuentes de video") se
agregan, se buscan en la red, se dejan listos los equipos de fábrica y se
decide qué canales ven los operadores, con qué nombre y con qué controles. Al
final del capítulo se explica la página
#menu("Dispositivos", "Hora y mantenimiento").

#captura("web-fuentes-de-video.png", pie: [Dispositivos › Fuentes de video.])

== La lista de dispositivos

#requiere("Fuentes de video")

Abra #menu("Dispositivos", "Fuentes de video"). Arriba está la tabla
*Dispositivos administrados* y abajo, *Equipos en línea*, que muestra lo que el
sistema encuentra en la red (vea «Buscar equipos en la red»).

#table(
  columns: (auto, 1fr),
  table.header[Columna][Qué muestra],
  [*Nombre*], [El nombre y, debajo, su ubicación en Recursos, o _Por ubicar_.],
  [*Tipo*], [Cámara, DVR, NVR o XVR. Lo detecta el sistema con lo que informa el equipo.],
  [*Marca*], [La marca o el protocolo con que se conecta: _Hikvision (SDK nativo)_, _Dahua (SDK nativo)_ u _ONVIF (otras marcas)_.],
  [*Dirección*], [La IP o el nombre del equipo y su puerto SDK.],
  [*Modelo*, *N° serie*, *Firmware*], [Los datos que informó el equipo la última vez que se validó.],
  [*Canales*], [Canales habilitados del total (vea abajo).],
  [*Patentes*], [Si el equipo entrega lecturas de patentes. «—» si no puede.],
  [*Estado*], [_En línea_, _Sin conexión_ o _Credenciales_ (el equipo rechazó el usuario o la contraseña).],
)

La columna *Canales* muestra un número solo si todos los canales están
habilitados. Si hay deshabilitados muestra «habilitados / total»:

- en gris, cuando los deshabilitados no tienen señal (entradas del grabador
  sin cámara): es lo normal;
- destacado, cuando hay canales *con señal* deshabilitados: esas cámaras no las
  ven los operadores. Habilítelas desde #boton("Canales").

#nota[Un equipo _Por ubicar_ no lo ven los operadores que tienen el alcance
  limitado a ciertas ubicaciones. La ubicación se elige al agregar o editar el
  equipo; el árbol de ubicaciones se explica en
  #capitulo(<cap-recursos-y-ubicaciones>).]

== Marcas y protocolos

#table(
  columns: (auto, 1fr, auto, auto),
  table.header[Marca / protocolo][Para][Puerto SDK][Puerto RTSP],
  [Hikvision (SDK nativo)], [Cámaras y grabadores Hikvision.], [8000], [554],
  [Dahua (SDK nativo)], [Cámaras y grabadores Dahua.], [37777], [554],
  [ONVIF (otras marcas)], [Equipos de otras marcas compatibles con ONVIF. El
    puerto SDK es el puerto web del equipo, donde atiende ONVIF.], [80], [554],
)

Los puertos de la tabla son los que el formulario propone al elegir la marca;
cámbielos si el equipo usa otros.

#nota[Hoy solo los equipos _Hikvision (SDK nativo)_ envían eventos de cámara
  (movimiento, cruce de línea…) a las automatizaciones, y las lecturas de
  patentes llegan de equipos Hikvision y Dahua.]

== Agregar un dispositivo

#requiere("Fuentes de video")

+ Haga clic en #boton("Agregar dispositivo").
+ Escriba un *Nombre*, por ejemplo «NVR Bodega».
+ En *Ubicación*, elija dónde está el equipo o deje _Por ubicar_. Sus canales
  heredan esa ubicación.
+ Elija la *Marca / protocolo*. Los puertos toman el valor habitual de esa
  marca.
+ Escriba la *Dirección (IP o hostname)* y revise *Puerto SDK* y *Puerto RTSP*.
+ Escriba el *Usuario del equipo* y la *Contraseña del equipo*.
+ Haga clic en #boton("Probar conexión"). El sistema se conecta al equipo sin
  guardar nada y muestra lo que encontró.
+ Haga clic en #boton("Guardar"). Mientras dice _Validando…_, el sistema vuelve
  a conectarse con el equipo; si lo rechaza, no se guarda nada y aparece el
  motivo. Si todo está bien, aparece «Dispositivo agregado y validado.».

#captura("web-fuentes-de-video-agregar.png", ancho: 55%, pie: [Agregar dispositivo, con la conexión ya validada.])

El recuadro *Conexión validada* que aparece al probar muestra:

#table(
  columns: (auto, 1fr),
  table.header[Dato][Qué significa],
  [*Tipo detectado*], [Cámara, DVR, NVR o XVR. No se elige: lo decide el equipo.],
  [*Modelo*, *N° de serie*, *Firmware*], [Lo que informa el equipo.],
  [*Canales*], [Cuántos canales analógicos e IP tiene.],
  [*Puerto RTSP*], [El que informa el equipo («detectado por SDK») o _no reportado_.],
  [*Cupo de licencia*], [Cuántos canales de video quedan libres en la licencia y cuántos están en uso.],
)

Debajo aparecen los canales: en verde los que tienen señal y en gris los que no.

#nota[Si el equipo informa su puerto RTSP, ese valor reemplaza al que escribió
  en el formulario, al agregarlo y cada vez que se revalida.]

Al guardar, entran habilitados los canales que tienen señal; los que no tienen
señal quedan deshabilitados y puede habilitarlos después desde #boton("Canales").

=== Agregar desde la búsqueda en la red

En *Equipos en línea*, el botón #boton("Agregar") de un equipo abre el mismo
formulario con el nombre (el modelo), la dirección, el puerto y la marca ya
completos. Escriba el usuario y la contraseña, y siga desde el paso de probar
la conexión.

=== Si la licencia no alcanza para todos los canales

Cada canal habilitado ocupa un canal de video de la licencia.

- Si la licencia no tiene canales libres, al probar aparece «No hay canales de
  video disponibles en la licencia (N de M en uso). Amplíe la licencia o
  deshabilite canales en otros equipos antes de agregar este.» y no se puede
  guardar.
- Si el equipo tiene más canales con señal que los libres, aparece *Seleccione
  los canales a habilitar*, con los primeros canales con señal ya marcados
  hasta completar el cupo. Marque los que quiera (el contador indica cuántos
  lleva de los disponibles; al llenarse, el resto se bloquea) y guarde. Con
  #boton("Quitar todos") empieza de cero.

Los canales con señal que quedaron fuera aparecen en #boton("Canales") como
_Esperando cupo de licencia_ y se habilitan solos cuando la licencia tenga
cupo (por ejemplo, al ampliarla).

== Buscar equipos en la red

#requiere("Fuentes de video")

La sección *Equipos en línea* busca equipos de video en el segmento de red del
servidor al abrir la página y se actualiza sola cada 30 segundos;
#boton("Buscar") repite la búsqueda en el momento. Encuentra equipos Hikvision,
Dahua y de cualquier marca ONVIF, y lista solo equipos de video (cámaras, DVR,
NVR y decodificadores): los controles de acceso y las alarmas se omiten.

#table(
  columns: (auto, 1fr),
  table.header[Estado][Qué significa y qué hacer],
  [_Agregado_], [Ya está en la lista de dispositivos.],
  [_Nuevo_], [Listo para agregar: use #boton("Agregar").],
  [_Sin activar_], [Equipo Hikvision de fábrica: actívelo con #boton("Activar").],
  [_Sin inicializar_], [Equipo Dahua de fábrica: inicialícelo con #boton("Inicializar").],
)

Las demás columnas son *IP*, *Marca*, *Tipo*, *Modelo*, *N° serie*, *Puerto
SDK* y *MAC*. En los equipos Hikvision activados y en los Dahua inicializados
aparece además #boton("Cambiar IP").

#importante[La búsqueda solo alcanza el segmento de red del servidor. Un equipo
  que aparece en la lista pero está en otra subred (por ejemplo, con su IP de
  fábrica) no se puede agregar hasta cambiarle la IP: el botón #boton("Agregar")
  lo advierte con el mensaje «El equipo está en otra subred: cámbiele la IP
  antes de agregarlo».]

=== Activar un equipo Hikvision de fábrica

Un equipo Hikvision nuevo no tiene contraseña y no se puede agregar hasta
activarlo.

+ En *Equipos en línea*, haga clic en #boton("Activar") en la fila del equipo.
+ Escriba la contraseña del usuario *admin* en *Contraseña* y repítala en
  *Confirmar contraseña*.
+ Haga clic en #boton("Activar").
+ Al terminar, haga clic en #boton("Agregar ahora"); o, si el equipo quedó en
  una IP fuera de la red del servidor, en #boton("Cambiar IP ahora"), que
  reutiliza la contraseña recién elegida.

La contraseña debe tener entre 8 y 16 caracteres, combinar al menos dos tipos
(mayúsculas, minúsculas, números o símbolos), no llevar espacios y no contener
la palabra «admin».

=== Inicializar un equipo Dahua de fábrica

Un equipo Dahua nuevo no tiene usuarios. Al inicializarlo se le crea el usuario
*admin* con la contraseña que elija.

+ En *Equipos en línea*, haga clic en #boton("Inicializar") en la fila del
  equipo.
+ Escriba la *Contraseña* y repítala en *Confirmar contraseña*.
+ Si quiere, escriba un *Correo para recuperar la contraseña*. Algunos equipos
  lo exigen: en ese caso el campo no dice _(opcional)_.
+ Haga clic en #boton("Inicializar") y luego en #boton("Agregar ahora") o
  #boton("Cambiar IP ahora").

La contraseña debe tener entre 8 y 32 caracteres, combinar al menos dos tipos
(mayúsculas, minúsculas, números o símbolos) y no llevar espacios ni los
caracteres `' " ; : &`.

=== Cambiar la IP de un equipo

Cambia la red de un equipo Hikvision (activado) o Dahua (inicializado) sin
entrar a su web, aunque esté en otra subred. Si el equipo no está en la red
del servidor, el formulario propone una IP de esa red.

#captura-pendiente("Panel web: ventana Cambiar IP de un equipo Dahua — modo IP fija, IP nueva con el aviso «está libre», máscara, puerta de enlace y DNS")

+ Haga clic en #boton("Cambiar IP") en la fila del equipo.
+ Elija el *Modo*: _IP fija_ o _DHCP_.
+ Con IP fija, complete *IP nueva*, *Máscara de subred* y, si corresponde,
  *Puerta de enlace (opcional)*, *DNS preferido (opcional)* y *DNS alternativo
  (opcional)*. Al salir del campo de la IP, el sistema avisa si ya está en uso
  o si está libre.
+ Escriba el *Usuario del equipo* y la *Contraseña del equipo*.
+ Si quiere, marque *Sincronizar fecha, hora y zona horaria (con horario de
  verano) con este servidor*.
+ Haga clic en #boton("Aplicar"). Puede tardar hasta un minuto.

Al terminar, la ventana _Red del equipo cambiada_ indica en qué IP quedó y si se
configuraron los DNS y la hora. Si el equipo quedó en la red del servidor,
ofrece #boton("Agregar ahora").

#nota[Antes de aplicar se comprueba que la IP nueva no esté ocupada. Los DNS se
  configuran entrando al equipo cuando ya está en su IP nueva. Con DHCP, el
  sistema busca el equipo por su MAC para mostrar la IP que le tocó. Si el
  equipo aceptó el cambio pero todavía no responde, espere: algunos tardan
  hasta un minuto en reiniciar la red.]

== Configurar los canales

#requiere("Fuentes de video")

#boton("Canales") abre la lista de canales del equipo. Cada fila tiene una
miniatura de la imagen, el nombre editable, el número de canal y su número
RTSP, y la etiqueta _Con señal_ o _Sin señal_.

#captura("web-fuentes-de-video-canales.png", pie: [Ventana Canales de un equipo.])

#table(
  columns: (auto, 1fr),
  table.header[Casilla][Para qué sirve],
  [*Habilitado*],
  [El canal es visible para los operadores. Un canal deshabilitado no aparece
    en la vista en vivo ni en el resto del sistema, y no ocupa cupo de la
    licencia.],
  [*PTZ / lente*],
  [Muestra el control PTZ (mover, zoom, foco). Se marca solo cuando el equipo
    informa que el canal tiene PTZ.],
  [*Proxy*],
  [Para cámaras que anuncian mal su audio: el video pasa por un relé y se ve
    sin audio.],
)

Para cambiar un canal:

+ Escriba el nombre nuevo o cambie las casillas de su fila.
+ Haga clic en #boton("Guardar") en esa misma fila. Aparece «Canal
  actualizado.».
+ Repita con los demás canales y cierre con #boton("Cerrar").

Cada fila se guarda por separado: los cambios de una fila sin #boton("Guardar")
se pierden al cerrar.

=== Habilitar las cámaras con señal

Si hay canales con señal deshabilitados, arriba de la lista aparece cuántos son
y el botón #boton("Habilitar canales con señal"), que los habilita todos hasta
donde alcance la licencia. Los que no caben quedan _Esperando cupo de licencia_
y se habilitan solos cuando haya cupo.

#nota[Si usted habilita o deshabilita a mano un canal que esperaba cupo, deja
  de esperarlo: su decisión manda.]

=== Marcar el PTZ a mano

Marque *PTZ / lente* en los domos y en las cámaras con lente varifocal
motorizado que se controlan por el cable coaxial de un DVR, o por ONVIF a
través del grabador: se pueden mover, pero el grabador no informa que tienen
PTZ. La marca manual se conserva cuando se revalida el equipo.

=== Proxy: cámaras con error de reproducción

Algunas cámaras anuncian su audio de una forma que el sistema no acepta, y el
canal da error de reproducción aunque el equipo esté en línea. Marque *Proxy*
en ese canal y guarde: el video se ve, pero sin audio.

== Editar, revalidar y eliminar

#requiere("Fuentes de video")

- *Editar*: #boton("Editar") abre el mismo formulario. Deje la contraseña
  vacía para conservar la guardada. Si cambia la dirección, el puerto SDK, el
  usuario, la marca o la contraseña, al guardar el sistema vuelve a validar el
  equipo. Al cambiar la ubicación, los canales que estaban con el equipo lo
  siguen; los que se ubicaron aparte en Recursos se quedan donde están.
- *Revalidar*: #boton("Revalidar") vuelve a consultar el equipo con las
  credenciales guardadas y actualiza modelo, firmware, canales y puerto RTSP.
  Úselo después de cambiar el firmware o de conectar cámaras nuevas a un
  grabador. Aparece «Equipo revalidado: información y canales actualizados.».
- *Eliminar*: #boton("Eliminar") pide confirmación y borra el equipo con todos
  sus canales.

Al revalidar, los canales que ya existían conservan su nombre y si estaban
habilitados; los canales nuevos entran con el nombre que informa el equipo
(habilitados si tienen señal); y los que el equipo ya no informa se eliminan.

#importante[Eliminar un equipo no se puede deshacer. Si alguna de sus cámaras
  estaba en el muro de video, esas ventanas se apagan.]

#modulo("anpr")[La columna *Patentes* activa o apaga el equipo como fuente de
  lecturas de patentes; se explica en
  #capitulo(<cap-reconocimiento-de-patentes>).]

== Hora y mantenimiento

#requiere("Ver hora y mantenimiento")

La página #menu("Dispositivos", "Hora y mantenimiento") muestra el reloj de
cada equipo (hora, zona horaria, horario de verano, origen de la hora y
desfase), lo pone en hora según una política, y permite reiniciarlo o
restablecerlo. La hora importa: los terminales de acceso evalúan los horarios
y la vigencia de las personas con _su_ hora local.

#importante[Por ahora esta página cubre solo los equipos de control de acceso.
  Las fuentes de video, los paneles y la citofonía todavía no aparecen en ella.]

#captura-pendiente("Panel web: Dispositivos › Hora y mantenimiento — tarjetas de resumen y tabla de equipos con uno Desfasado")

Arriba hay cuatro tarjetas: *Hora del servidor*, *Cómo deben estar* (la zona y
el origen de la hora de la política), *Supervisión* (si _Corrige sola_ o _Solo
avisa_, cada cuánto revisa y cuánto desfase tolera) y *Equipos* (cuántos están
en hora). La tabla muestra *Equipo*, *Hora del equipo*, *Desfase*, *Origen de la
hora* y *Estado*. Las horas avanzan solas y los datos se actualizan cada 15
segundos.

#table(
  columns: (auto, 1fr),
  table.header[Estado][Qué significa],
  [_En hora_], [El desfase está dentro de lo tolerado.],
  [_Desfasado_], [El equipo está adelantado o atrasado más de lo tolerado.],
  [_Otra zona horaria_], [La zona del equipo no coincide con la política, aunque la hora esté bien.],
  [_No se pudo leer_], [El equipo no entregó su hora; debajo se indica el motivo.],
  [_Sin conexión_], [El equipo no está en línea.],
  [_Sin leer todavía_], [La supervisión aún no lo revisa.],
  [_No disponible_], [El equipo no permite que el sistema lea o cambie su hora.],
)

Debajo del estado se indica la última puesta en hora y si funcionó.
#boton("Leer ahora") pregunta la hora a los equipos marcados (o a todos) sin
cambiar nada. La ficha de cada terminal de acceso muestra lo mismo en su
apartado *Hora y mantenimiento*, con los botones #boton("Ajustar hora…") y
#boton("Reiniciar / restablecer…").

=== Definir cómo deben estar los equipos

#requiere("Mantenimiento de equipos")

+ Haga clic en #boton("Política…").
+ En *Zona horaria*, deje _La del servidor_ (lo normal) o elija otra. Incluye
  el horario de verano, que se escribe en los equipos que lo admiten.
+ En *Origen de la hora*, elija _La del servidor: el VMS los pone en hora_ o _Un
  servidor NTP_, con *Servidor NTP* y *Sincronizar cada (minutos)*. Los equipos
  que no admiten NTP reciben la hora del servidor.
+ En *Supervisión*, marque *Ponerlos en hora solos cuando se desfasan o tienen
  otra zona* si quiere que el sistema los corrija sin intervención (queda en la
  bitácora), y ajuste *Desfase tolerado (segundos)* (5 a 86 400) y *Revisar
  cada (minutos)* (1 a 1440).
+ Haga clic en #boton("Guardar"). Los equipos se revisan de inmediato.

=== Poner en hora

#requiere("Mantenimiento de equipos")

+ Marque los equipos en la primera columna, o no marque ninguno para incluir a
  todos los que están en línea.
+ Haga clic en #boton("Poner en hora") y confirme.

Quedan con la zona y el origen de la hora de la política. Si alguno falla, el
aviso indica cuál y por qué.

=== Ajustar la hora de un equipo

#requiere("Mantenimiento de equipos")

+ Haga clic en #boton("Ajustar…") en la fila del equipo.
+ Elija la *Zona horaria*.
+ En *¿De dónde saca la hora?*, elija _La del servidor_ (se le fija ahora),
  _Fijarla a mano_ (con fecha y hora) o _Un servidor NTP_ (con *Servidor* y
  *Cada (minutos)*), si el equipo lo admite.
+ Haga clic en #boton("Aplicar").

#importante[Si la supervisión corrige sola, después de un ajuste a mano espera
  una hora antes de volver a tocar el equipo, y luego lo deja como dice la
  política. Si este equipo debe quedar distinto, desmarque su *Corrección
  automática*.]

La columna *Corrección automática* (casilla _sí_) decide equipo por equipo si la
supervisión puede ponerlo en hora sola. Solo actúa si además la política tiene
marcada la corrección automática.

=== Reiniciar o restablecer un equipo

#requiere("Mantenimiento de equipos")

#boton("Reiniciar / restablecer…") abre la _Zona de riesgo_ del equipo, con las
operaciones que admite. Todo queda en la bitácora.

#table(
  columns: (auto, 1fr, 1fr),
  table.header[Operación][Qué hace][Riesgo],
  [Reiniciar],
  [Reinicia el equipo. No borra personas, horarios ni configuración.],
  [Queda fuera de servicio uno o dos minutos: en ese lapso no abre puertas ni
    registra pasadas.],
  [Restablecer la configuración],
  [Vuelve a la configuración de fábrica, pero conserva la red y las cuentas.
    El sistema le reescribe solo las personas y los horarios.],
  [Se pierde todo ajuste hecho en el propio equipo. Mientras se reescribe el
    padrón, puede no reconocer a algunas personas.],
  [De fábrica completo],
  [Borra todo, incluidas la red y las cuentas: el equipo queda desactivado,
    como recién sacado de la caja.],
  [El sistema lo pierde hasta que alguien lo vuelva a activar en la red con la
    misma contraseña. Hasta entonces no abre puertas para nadie.],
)

+ Haga clic en el botón de la operación (#boton("Reiniciar"),
  #boton("Restablecer") o #boton("Volver a fábrica")).
+ Para reiniciar, confirme con #boton("Sí, reiniciar ahora"). Para restablecer,
  escriba el nombre del equipo exactamente como está y haga clic en el botón
  _Entiendo el riesgo_ que se habilita.

Los botones están deshabilitados si el equipo no está en línea.

== Solución de problemas

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje o síntoma][Qué hacer],
  [No se pudo iniciar sesión en … : usuario o contraseña incorrectos (error 1).],
  [Revise el usuario y la contraseña del equipo.],
  [… usuario bloqueado por intentos fallidos (error 153). \ … cuenta bloqueada
    (error 5).],
  [El equipo bloqueó la cuenta por intentos fallidos. Espere a que se
    desbloquee (o desbloquéela desde su web) antes de reintentar con la
    contraseña correcta.],
  [… no se pudo conectar con el dispositivo (IP/puerto inaccesible).],
  [Revise la dirección, el *Puerto SDK* y que el servidor llegue al equipo por
    la red.],
  [El dispositivo ONVIF rechazó las credenciales…],
  [En algunas marcas (por ejemplo, Dahua) la cuenta ONVIF es distinta de la
    cuenta web: créela o revísela en el equipo (Cuentas → Usuario ONVIF).],
  [El dispositivo en … no respondió como servicio ONVIF (¿puerto correcto?).],
  [Revise el *Puerto SDK*: en ONVIF es el puerto web del equipo.],
  [El dispositivo ONVIF no expone perfiles de video. \ El dispositivo ONVIF no
    entregó la URL RTSP del stream principal.],
  [El equipo no ofrece video por ONVIF: revise en su web que ONVIF y RTSP
    estén habilitados.],
  [Ya existe un dispositivo con esa dirección y puerto.],
  [El equipo ya está agregado; edítelo en vez de agregarlo de nuevo.],
  [No hay canales de video disponibles en la licencia…],
  [Amplíe la licencia o deshabilite canales de otros equipos.],
  [La licencia permite N canales de video y ya hay M en uso.],
  [Al habilitar un canal: deshabilite otro o amplíe la licencia.],
  [Un canal en línea da error de reproducción.],
  [Marque *Proxy* en ese canal (vea «Proxy: cámaras con error de
    reproducción»).],
  [Una cámara PTZ conectada a un DVR no muestra el control PTZ.],
  [Marque *PTZ / lente* en el canal.],
  [El equipo ya no responde en la red. Vuelva a buscar e intente de nuevo.],
  [Al activar, inicializar o cambiar la IP: el equipo dejó de aparecer en la
    búsqueda. Pulse #boton("Buscar") y repita.],
  [La IP … ya está en uso por otro equipo de la red. Elija otra.],
  [Elija otra IP libre.],
  [El equipo no está activado: actívelo antes de cambiarle la IP. \ El equipo
    está sin inicializar: inicialícelo primero…],
  [Active o inicialice el equipo y luego cambie su IP con la misma contraseña.],
)
