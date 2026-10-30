#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/AlarmsView.xaml, src/TrueCentralVms.Client/ViewModels/AlarmsViewModel.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Locations.cs, src/TrueCentralVms.Client/Views/LiveView.xaml, src/TrueCentralVms.Server/wwwroot/alarm-monitor.js, src/TrueCentralVms.Server/wwwroot/alarms.js, src/TrueCentralVms.Server/Api/AlarmsApi.cs, src/TrueCentralVms.Server/Services/AlarmReceiverService.cs

= Paneles de alarma <cap-paneles-de-alarma>

El módulo Paneles de alarma muestra el estado de las centrales de intrusión
Hikvision conectadas al sistema y permite operarlas a distancia: ver cada
panel con sus áreas (particiones) y zonas, armar, desarmar, silenciar alarmas,
anular zonas y seguir sus eventos al instante. El servidor mantiene la
conexión con los paneles todo el tiempo, y cada orden queda registrada en la
bitácora de auditoría.

#captura-pendiente("Cliente de monitoreo: Paneles de alarma con un panel seleccionado, sus áreas y zonas, y la columna Eventos", alto: 6cm)

== Abrir el módulo

#requiere("Ver alarmas")

En el cliente de monitoreo, haga clic en el botón *Paneles de alarma* del riel
o en la tarjeta del mismo nombre en Inicio. La pantalla tiene tres columnas:

- *Paneles*: la lista de paneles con su estado de armado y de conexión.
- El detalle del panel seleccionado, con sus *ÁREAS* y *ZONAS*.
- *Eventos*: lo que van informando los paneles, del más reciente al más
  antiguo.

Arriba, el resumen indica cuántos paneles hay y cuántos están en alarma; el
punto se pone rojo si alguno lo está.

#nota[Cerrar la viñeta del módulo no desconecta nada: el servidor sigue
  conectado a los paneles y las alarmas siguen avisando.]

== Leer el estado de un panel

#requiere("Ver alarmas")

Cada panel de la lista muestra su nombre, una etiqueta con el estado de armado,
el estado de la conexión y cuántas áreas y zonas tiene.

#table(
  columns: (auto, 1fr),
  table.header[Etiqueta de armado][Significado],
  [Armado], [Todas las áreas están armadas.],
  [Desarmado], [Ninguna área está armada.],
  [Parcial (n/m)], [n de sus m áreas están armadas.],
  [Armando…], [Un área está en el conteo de salida.],
  [Sin áreas], [El panel no ha informado áreas.],
)

La etiqueta es verde con todo armado, azul con parte armada o armando, gris
con todo desarmado y roja si hay una alarma activa.

#table(
  columns: (auto, 1fr),
  table.header[Conexión][Significado],
  [En línea · recibiendo eventos], [El servidor lee el estado del panel y
    recibe sus eventos al instante.],
  [En línea], [El servidor lee el estado, pero no tiene abierto el canal de
    eventos: los cambios se ven en la siguiente lectura periódica.],
  [Sin conexión], [El servidor no logra comunicarse con el panel. En el
    detalle aparece el motivo, en rojo.],
  [Credenciales rechazadas], [El panel no aceptó el usuario o la contraseña.
    Un administrador debe corregirlos.],
  [Conectando…], [El servidor está estableciendo la conexión.],
  [Monitoreo desactivado], [Un administrador pausó el panel.],
)

Al seleccionar un panel, la cabecera del detalle muestra el modelo, el número
de serie, el firmware y la dirección, y la hora de la última lectura («Estado
leído a las…»). #boton("Actualizar") vuelve a leer el estado del panel en ese
momento.

Si el panel se conecta por ISUP, en lugar de la dirección aparece «ISUP»
seguido del ID del panel: es el panel el que llama al servidor, así que el
sistema no usa su dirección de red.

Dos avisos en rojo advierten problemas del propio equipo:

- «Tapa del panel abierta (sabotaje): el equipo no permitirá armar hasta
  cerrarla.»
- «Falla de corriente de red: el panel está funcionando en batería.»

=== Áreas

Cada área es una tarjeta con su nombre, su número, cuántas zonas tiene y su
estado. Si está en alarma, la tarjeta se pone roja y dice *¡ALARMA ACTIVA!*.

#table(
  columns: (auto, 1fr),
  table.header[Estado del área][Significado],
  [Desarmada], [No vigila.],
  [Armada · total], [Vigila todas sus zonas.],
  [Armada · parcial], [Armado parcial o «en casa».],
  [Armada · vacaciones], [Armada en modo vacaciones.],
  [Armando… (N s)], [Conteo de salida en curso: quedan N segundos para que
    quede armada.],
  [Estado desconocido], [El panel no informó el estado del área.],
)

=== Zonas

Cada zona muestra su nombre, su área y una etiqueta de estado. Más abajo, en
amarillo, pueden aparecer las marcas *anulada*, *tamper*, *batería baja* y
*armada*, y en gris el tipo de detector cuando el panel lo informa.

#table(
  columns: (auto, 1fr),
  table.header[Estado de la zona][Significado],
  [Normal], [Detector en reposo.],
  [Activada], [El detector está interrumpido (por ejemplo, una puerta
    abierta).],
  [Falla], [El panel informa una falla en la zona.],
  [Sin comunicación], [El detector no se comunica con el panel.],
  [Sin área], [La zona no pertenece a ningún área.],
  [¡ALARMA!], [La zona está en alarma.],
)

== Armar y desarmar

#requiere("Operar alarmas")

Cada tarjeta de área tiene sus botones:

- #boton("Armar total") arma el área completa.
- #boton("Parcial") la arma en modo parcial («en casa»).
- #boton("Desarmar") la desarma. Durante el conteo de salida el botón dice
  #boton("Cancelar") y detiene el armado.

Para todo el panel a la vez, la cabecera ofrece #boton("Armar todo"),
#boton("Armar parcial (todo)") y #boton("Desarmar todo"), que durante un
conteo de salida pasa a #boton("Cancelar armado").

+ Seleccione el panel en la lista.
+ Haga clic en el botón del área, o en el de todo el panel.
+ Mientras el área está en el conteo de salida, su estado dice «Armando…» con
  los segundos que faltan. Al terminar, pasa a «Armada · total» o «Armada ·
  parcial».

#importante[Las órdenes se envían en el acto, sin pedir confirmación.]

Los botones se desactivan cuando la orden no corresponde: el área ya está en
ese modo, el panel no está en línea, la tapa del panel está abierta o el
recurso queda fuera de su alcance por ubicación. En ese último caso el detalle
lo explica: «Fuera de su alcance: puede verlo, pero no operarlo.»

La franja azul bajo la barra del módulo confirma el envío («Armado enviado.»,
«Desarmado enviado.») o explica por qué no se pudo. Por ejemplo, con la tapa
abierta dice «No se puede armar: la tapa del panel está abierta (sabotaje).
Ciérrela antes de armar.»

== Silenciar una alarma

#requiere("Operar alarmas")

Mientras un área está en alarma, su tarjeta muestra #boton("Silenciar"), y la
cabecera del panel, #boton("Silenciar alarmas") para todas sus áreas. Silenciar
borra la alarma activa sin cambiar el armado: el área sigue armada.

== Anular y restituir zonas

#requiere("Operar alarmas")

Anular una zona (bypass) la deja fuera del armado: aunque el detector se
active, no dispara la alarma. Sirve, por ejemplo, para armar con una ventana
que quedó abierta.

+ Busque la zona en *ZONAS*.
+ Haga clic en #boton("Anular"). La zona queda marcada *anulada* y el botón
  cambia a #boton("Restituir").
+ Cuando el problema esté resuelto, haga clic en #boton("Restituir") para que
  la zona vuelva a vigilar.

#importante[Una zona anulada no protege nada. Restitúyala en cuanto se pueda.]

== Seguir los eventos

#requiere("Ver alarmas")

La columna *Eventos* muestra, al instante, las alarmas, armados, desarmados,
anulaciones, fallas y restauraciones de los paneles (hasta 500 en pantalla).
La barra de color de cada evento indica su severidad: roja para lo crítico,
amarilla para las advertencias. Cada evento dice:

- qué pasó;
- dónde: panel · área · zona;
- cuándo, y quién o qué lo originó, con el código del evento y su significado
  si el panel lo informó.

#table(
  columns: (auto, 1fr),
  table.header[Origen][Significado],
  [Operador: nombre], [Orden dada desde el sistema por ese usuario.],
  [Usuario del panel: nombre], [Orden dada en el panel por uno de sus
    usuarios.],
  [Orden desde keypad (teclado/llavero)], [Orden dada en un teclado o llavero
    del panel.],
  [Orden desde la app (Hik-Connect)], [Orden dada desde la aplicación del
    fabricante.],
  [Informado por el panel], [El panel avisó el hecho por su cuenta.],
  [Detectado por sondeo], [El servidor notó el cambio al releer el estado del
    panel.],
)

Para filtrar la columna:

- *Solo el panel seleccionado* deja solo los eventos del panel elegido en la
  lista.
- La lista desplegable de la barra elige el tipo: Todos los eventos, Alarmas,
  Armados, Desarmados, Anulaciones, Sensores, Fallas, Restauraciones o Sistema.

== Avisos de alarma en el cliente

Cuando un panel informa un evento crítico, el cliente avisa aunque el módulo
esté cerrado:

- aparece un aviso flotante «Alarma · nombre del panel», con lo ocurrido, el
  lugar y la hora, en la esquina inferior derecha de la ventana principal;
- la barra de estado muestra «ALARMA:» seguido del evento;
- el botón *Paneles de alarma* del riel se pone rojo y parpadea mientras algún
  panel siga en alarma.

Si la zona, o su área, tiene una ficha con consignas o cámaras asociadas, se
abre además la ventana de verificación (#capitulo(<cap-recursos-y-ubicaciones>)).
#modulo("automation")[Una automatización también puede convertir los eventos de
  los paneles en alertas con acuse de recibo, que se atienden en el Centro de
  eventos (#capitulo(<cap-centro-de-eventos>)).]

== Armar o desarmar una ubicación completa

#requiere("Órdenes por ubicación")

Cuando los paneles están ubicados en el árbol de ubicaciones, una sola orden
arma o desarma todas las áreas de una ubicación y de sus sububicaciones.

+ En el Centro de mando del cliente, busque la ubicación en la lista de
  recursos y haga clic derecho sobre ella.
+ Elija *Armar total sus áreas de alarma…*, *Armar parcial sus áreas de
  alarma…* o *Desarmar sus áreas de alarma…*.
+ El sistema muestra las áreas que va a tocar y pregunta «¿Continuar?».
  Haga clic en #boton("Sí").

Si alguna área no obedece, un mensaje final lista cuáles y por qué.

== Alarmas desde el panel web

#requiere("Ver alarmas")

En el panel web, abra #menu("Aplicaciones", "Alarmas"). La página tiene las
mismas tres columnas que el cliente (paneles, detalle y eventos), con los
mismos botones de operación, y se actualiza cada 5 segundos.

#captura-pendiente("Panel web: página Alarmas con la barra de filtros, un panel seleccionado y la lista de eventos", alto: 6cm)

La barra de filtros busca en el historial de eventos:

+ Elija el *Tipo de evento*, escriba en *Buscar texto* (descripción, zona,
  área u operador) y, si quiere, un rango *Desde* / *Hasta*.
+ Marque *Solo el panel seleccionado* para ver solo los eventos del panel
  elegido.
+ Haga clic en #boton("Buscar"). #boton("Limpiar") quita todos los filtros.

El tipo de evento y *Solo el panel seleccionado* filtran apenas se cambian.
Con una fecha *Hasta* fijada, la lista no agrega los eventos nuevos.

Al armar, desarmar o anular aparece un aviso como «Orden de armado enviada.».
Si llega un evento crítico mientras la página está abierta, aparece un aviso
rojo «ALARMA:» con el evento y el lugar.

#nota[En el panel web, los botones para todo el panel (Armar todo, Armar
  parcial (todo) y Desarmar todo) aparecen solo en los paneles con más de un
  área. El panel web avisa de las alarmas solo mientras esta página está
  abierta.]

== Configuración

#requiere("Paneles de alarma")

Los paneles se agregan y se editan en el panel web, en #menu("Dispositivos",
"Paneles de alarma"). La tabla muestra de cada panel su *Nombre* (con su
ubicación), *Dirección*, *Modelo*, *N° serie*, *Firmware*, cuántas *Áreas* y
*Zonas* tiene, el estado de *Armado* y de *Conexión* (con el último error, si
lo hay) y, en *Eventos*, un punto verde si el canal de eventos está abierto o
rojo si está cerrado. La tabla se actualiza sola cada 10 segundos.

#boton("Estado") despliega bajo la fila las áreas y zonas del panel, con los
mismos botones de operación que el módulo de monitoreo. Sirve para probar un
panel recién agregado.

=== Tipos de panel

En *Marca / protocolo* se elige cómo se conecta el sistema con el panel:

#table(
  columns: (auto, 1fr),
  table.header[Marca / protocolo][Cuándo usarlo],
  [Hikvision AX PRO / AX Hybrid / DS-PHA (ISAPI)], [Conexión directa: el
    servidor se conecta a la dirección IP del panel. El panel tiene que ser
    alcanzable desde el servidor.],
  [Hikvision AX PRO / AX Hybrid (ISUP, el panel se conecta al servidor)], [El
    panel se conecta al servidor por ISUP con su ID y su clave. Sirve cuando el
    servidor no llega a la dirección del panel: detrás de un router, por 4G o
    en otra red.],
)

=== Agregar un panel con conexión directa

#captura-pendiente("Panel web: formulario Agregar panel de alarma con conexión directa y el resultado de Probar conexión", alto: 6cm)

+ Haga clic en #boton("Agregar panel"). Se abre la página *Agregar panel de
  alarma*.
+ Escriba el *Nombre* y elija su *Ubicación*. Las áreas y zonas del panel
  heredan esa ubicación; las que ubique aparte en Recursos se quedan donde
  están.
+ En *Marca / protocolo*, elija *Hikvision AX PRO / AX Hybrid / DS-PHA
  (ISAPI)*.
+ Escriba la *Dirección del panel (IP o hostname)* y el *Puerto HTTP* (80 por
  omisión).
+ Escriba el *Usuario del panel* y la *Contraseña del panel*.
+ Marque *Usar HTTPS (certificado autofirmado aceptado)* si el panel atiende
  por HTTPS.
+ Deje marcado *Monitoreo activo (sondeo de estado y recepción de eventos)*.
+ Haga clic en #boton("Probar conexión"). Si todo está bien aparece *Conexión
  validada*, con el modelo, el número de serie, el firmware y las áreas y
  zonas que encontró.
+ Haga clic en #boton("Guardar"). El sistema vuelve a validar las
  credenciales, lee las áreas y zonas, y avisa «Panel agregado y validado.».

#importante[Use un usuario *local* del panel (el creado al activarlo,
  normalmente admin), no la cuenta de la nube Hik-Connect. Tras varios intentos
  fallidos el panel bloquea el acceso por 30 minutos.]

=== Recepción de eventos de un panel con conexión directa

El servidor mantiene abierto un canal de eventos con cada panel habilitado y,
además, relee su estado periódicamente y después de cada evento. Para que los
eventos lleguen también cuando ese canal no está disponible, el panel puede
configurarse para reportar al servidor por una de estas vías:

- *Notificación HTTP*: en el panel, agregue como destino
  `http://IP-del-servidor/api/alarms/push` (puerto 80, sin cifrado: es lo
  único que el servidor atiende sin HTTPS, junto con las fotos que bajan los
  terminales ISUP).
- *Alarm Receiving Center*: en el panel, configure un centro receptor por
  Tcp/IP con el protocolo ADM-CID (o SIA-DCS), sin cifrado, con la dirección
  del servidor, el puerto 5091 y un número de cuenta de 3 a 16 dígitos.

El servidor acepta estos reportes solo si llegan desde la dirección con que el
panel está registrado y habilitado en el sistema. El instalador del servidor
abre los puertos 80 y 5091 en su firewall. El estado del servicio *Receptor
de alarmas (SIA DC-09)* se ve en #menu("Sistema", "Servicios").

=== Agregar un panel que se conecta por ISUP

+ Haga clic en #boton("Agregar panel"). Se abre la página *Agregar panel de
  alarma*.
+ Escriba el *Nombre* y elija su *Ubicación*.
+ En *Marca / protocolo*, elija *Hikvision AX PRO / AX Hybrid (ISUP, el panel
  se conecta al servidor)*.
+ Escriba el *ID ISUP del panel* y la *Clave ISUP*. El recuadro de ayuda
  muestra lo que hay que configurar en el panel (en el AX PRO: Comunicación →
  ISUP): el protocolo ISUP 5.0, la dirección del servidor, el puerto y ese
  mismo ID y esa misma clave.
+ Haga clic en #boton("Guardar").

Si el panel ya está conectado, el sistema lo valida y lee sus áreas y zonas en
ese momento. Si todavía no se conectó, se guarda igual («Panel guardado: queda
esperando que se conecte por ISUP.») y se completa solo en su primer contacto.
Mientras tanto, la columna *Conexión* explica qué falta: los datos que hay que
escribir en el panel o, si el panel intentó conectarse y fue rechazado, el
motivo (por ejemplo, que su clave no coincide).

#importante[El ID admite hasta 32 letras, números, guiones y guiones bajos; la
  clave, hasta 32 caracteres sin espacios. Deben ser exactamente los que tiene
  configurados el panel.]

El instalador del servidor abre en su firewall los puertos del servicio ISUP
(7660 a 7667). Si el servidor está detrás de un router, esos puertos deben
llegar al servidor, y la dirección que se anuncia a los equipos se ajusta en
el servidor (vea #capitulo(<ap-servidor>)). Los equipos conectados y los
intentos rechazados se ven en #menu("Sistema", "Conexión ISUP") (vea
#capitulo(<cap-sistema>)).

#nota[Los paneles que en versiones anteriores reportaban a la receptora Hik IP
  Receiver Pro pasan solos a este tipo al actualizar, con su mismo ID y su
  misma clave, y siguen conectándose sin tocarlos. Si de alguno no se pudo
  deducir el ID, queda con el monitoreo desactivado y un aviso: edítelo y
  escriba su ID ISUP.]

=== Editar, pausar y eliminar un panel

- #boton("Editar") abre el mismo formulario. Las contraseñas pueden quedar
  vacías para no cambiarlas.
- Desmarcar *Monitoreo activo* pausa el panel: el sistema deja de leerlo y de
  recibir sus eventos, y la tabla lo marca *pausado*.
- #boton("Eliminar") pide confirmación. El historial de eventos se conserva.

#nota[Los paneles de alarma son un módulo de la licencia, que además fija
  cuántos paneles con monitoreo activo admite. Si la licencia no lo incluye o
  el cupo está completo, al guardar aparece «El módulo «Paneles de alarma» no
  está incluido en la licencia. Amplíe la licencia para habilitarlo.» o, por
  ejemplo, «La licencia permite 4 paneles y ya hay 4 en uso. Amplíe la licencia
  para agregar más.». Los paneles pausados no ocupan cupo.]
