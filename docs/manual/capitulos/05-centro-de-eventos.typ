#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/EventCenterView.xaml, src/TrueCentralVms.Client/Views/EventCenterWindow.xaml, src/TrueCentralVms.Client/ViewModels/EventCenterViewModel.cs, src/TrueCentralVms.Client/Views/AlertWindow.xaml, src/TrueCentralVms.Client/Views/AlertWindow.xaml.cs, src/TrueCentralVms.Client/Views/ToastWindow.xaml, src/TrueCentralVms.Client/Views/ImageViewerWindow.xaml, src/TrueCentralVms.Client/Services/AlertSoundPlayer.cs, src/TrueCentralVms.Client/Views/SettingsWindow.xaml, src/TrueCentralVms.Client/Views/MainWindow.xaml, src/TrueCentralVms.Server/wwwroot/event-center.js, src/TrueCentralVms.Server/wwwroot/workflows.js

= Centro de eventos <cap-centro-de-eventos>

El Centro de eventos reúne las alertas que el sistema emite para los
operadores y lleva el registro de su acuse de recibo: quién se dio por
enterado de cada una, a qué hora y desde dónde. Después de un incidente
permite responder qué avisó el sistema y quién lo atendió, o si nadie lo hizo.

#captura("cliente-centro-de-eventos.png", pie: [Centro de eventos del cliente de monitoreo.])

Las alertas nacen de las automatizaciones: cada vez que una automatización
ejecuta la acción «Avisar a los operadores», se crea una alerta
#modulo("automation")[(vea #capitulo(<cap-automatizaciones>))].

#nota[Las alarmas de los paneles, del cerco eléctrico y de las puertas se ven
  en sus propios módulos. Llegan al Centro de eventos solo si una automatización
  las convierte en alerta.]

== Cuando llega una alerta

#requiere("Atender alertas")

Cada alerta trae un título, un mensaje, una severidad (Crítica, Advertencia o
Informativa) y, si la automatización las capturó, fotos de las cámaras. Lo que
hace el cliente de monitoreo depende de si la alerta exige confirmación:

- *Alerta que exige confirmación.* Suena la alarma sonora, si la automatización
  tiene una, y se abre la ventana *Información de la alarma* delante de todo,
  esté donde esté trabajando. El botón *Centro de eventos* del riel se pone
  rojo, parpadea y muestra cuántas alertas siguen sin confirmar; la barra de
  estado también lo indica.
- *Alerta informativa.* Aparece un aviso flotante en la esquina inferior
  derecha de la ventana principal, con la foto si la tiene, y suena la alarma
  sonora si la automatización tiene una. Como nadie la confirma, el sonido no
  queda en bucle: se repite las veces configuradas (con 0, suena una vez). El
  aviso se cierra solo a los 15 segundos (30 si trae foto); la ✕ lo descarta
  antes.

Si el cliente estaba cerrado o sin conexión cuando se emitió una alerta, al
abrirlo (o al recuperar la conexión) muestra las que siguen sin confirmar.

La automatización puede dirigir la alerta a usuarios concretos. Si no lo hace,
la reciben todos los que tienen el permiso *Atender alertas* y pueden ver el
recurso que la originó, según su alcance por ubicación. El administrador las ve
todas.

== Atender una alerta en la ventana de alarma

#requiere("Atender alertas")

#captura("cliente-ventana-de-alarma.png", pie: [Ventana Información de la alarma, con una alerta pendiente.])

A la izquierda de la ventana está el detalle del hecho:

- *CONSIGNAS PARA EL OPERADOR*: lo que debe hacer, tomado de la ficha del
  recurso que originó la alerta (#capitulo(<cap-recursos-y-ubicaciones>)). Va
  primero y solo aparece si la ficha tiene consignas.
- *ORIGEN*: la *Automatización*, *Qué la disparó*, la *Ubicación*, la
  *Severidad*, la *Hora del evento*, la *Ejecución* y la *Alarma sonora*
  configurada.
- *DESCRIPCIÓN*: el mensaje de la alerta.
- *ACUSE DE RECIBO*: PENDIENTE, Confirmada (por quién, cuándo, cuántos segundos
  después de emitida y desde dónde) o Informativa.

A la derecha están las pestañas *Fotos*, *Video en vivo* y *Qué hizo el
sistema*.#modulo("playback")[ Si la alerta tiene cámaras y usted puede ver
grabaciones, aparece primero *Repetición del evento* (ver «Ver la repetición del
evento», más abajo).]

=== Darse por enterado

+ Lea las consignas, si las hay, y revise las fotos o el video.
+ Haga clic en #boton("Enterado").

El sistema registra su usuario, la hora y si confirmó desde el cliente o desde
el panel web. La ventana pasa a la siguiente alerta pendiente y se cierra cuando
no queda ninguna.#modulo("video")[ También puede confirmar una alerta con
#boton("CONFIRMAR") en la tarjeta *Últimas alertas* del Centro de mando, sin
abrir esta ventana (ver #capitulo(<cap-vista-en-vivo>)).]

#modulo("alarm_screens")[Si la alerta también se mostró en las pantallas de
  alarma de las casetas (#capitulo(<cap-pantallas-de-alarma>)) y un guardia la
  marca como vista allá, sale sola de la ventana, que se cierra si no queda
  otra pendiente. El acuse queda a nombre de la pantalla, desde _una pantalla
  de alarma de caseta_. Y al revés: al confirmarla aquí, la pantalla de la
  caseta deja de sonar. Las alertas que van solo a las pantallas no abren esta
  ventana, pero aparecen en el Centro de eventos.]

#importante[Cerrar la ventana con #boton("Cerrar") o con la ✕ no confirma nada:
  la alerta sigue pendiente hasta que alguien se dé por enterado. Lo que sí hace
  es callar la alarma sonora.]

#nota[Vale solo la primera confirmación. Si otro operador confirma la alerta
  antes que usted, en su ventana aparece como Confirmada, con el nombre de quien
  la confirmó, y la alarma sonora se calla. El acuse de recibo no se puede
  modificar después.]

Si el servidor no responde, el botón cambia a #boton("Reintentar") y aparece
«No se pudo registrar la confirmación», seguido del motivo. La alerta sigue
pendiente: vuelva a intentarlo.

=== Silenciar la alarma sonora

La automatización define la alarma sonora: un sonido de la biblioteca o el
pitido del sistema, repetido de una a cinco veces o sin parar hasta que alguien
confirme la alerta. Si el sonido no se puede descargar o reproducir, suena el
pitido del sistema. Una alerta nueva reemplaza el sonido de la anterior.

Para callarla sin confirmar la alerta, haga clic en #boton("Silenciar")\; el
botón cambia a *Silenciada*. La alarma también se calla al confirmar la alerta,
en este puesto o en otro, y al cerrar la ventana.

=== Ver las fotos, el video y lo que hizo el sistema

- *Fotos*: la foto principal con su número (por ejemplo, 1/3) y las miniaturas
  debajo; un clic en una miniatura la muestra en grande. Doble clic en la foto,
  o el botón de la esquina *Pantalla completa con zoom digital*, abre el visor.
- *Video en vivo*: el video de las cámaras vinculadas a la alerta, todas a la
  vez en una grilla. Se abre al pasar a la pestaña. Si la alerta no trae fotos
  pero sí cámaras, la ventana se abre directamente en esta pestaña.
- *Qué hizo el sistema*: los pasos que ejecutó la automatización, cada uno con
  ✓ si resultó o ✕ si falló, y lo que tardó.

Si la pestaña Video en vivo dice «Las cámaras vinculadas ya no están
disponibles en este cliente», esas cámaras no están en la lista de dispositivos
de su puesto (se quitaron o quedan fuera de su alcance).

#modulo("playback")[
=== Ver la repetición del evento

La pestaña *Repetición del evento* responde lo primero que hay que saber: qué
pasó. Reproduce en bucle lo que grabó el equipo (el DVR/NVR o la tarjeta de la
cámara) alrededor del evento: por omisión, desde 5 segundos antes hasta 15
segundos después. Cuando la alerta la tiene, la ventana se abre en esta pestaña.

Al abrirse una alerta recién emitida, el equipo todavía está grabando el
después del evento. Mientras tanto, la pestaña muestra el video en vivo de la
misma cámara y arriba dice «El equipo está grabando el después del evento: la
repetición estará en N s». Cuando la grabación está disponible, la pestaña pasa
sola a la repetición. En una alerta antigua (abierta desde el Centro de
eventos) la repetición empieza de inmediato. La cuenta regresiva usa lo que suele tardar ese
grabador en dejar disponible lo recién grabado: el servidor lo aprende con cada
repetición.

Debajo del video:

- La línea del clip, con una *marca roja en el instante del evento*. Arrastre el
  control o haga clic en la línea para ir a otro momento. Al centro se lee
  cuánto falta o cuánto pasó desde el evento («−2,9 s del evento») y la hora de
  la grabación.
- El botón de pausa y el de volver al comienzo del clip.
- #boton("½×"), #boton("1×") y #boton("2×"): la velocidad.
- #boton("Abrir en Reproducción"): abre el módulo Reproducción en esa cámara,
  posicionado en el clip, para ver más (#capitulo(<cap-reproduccion>)).
- #boton("Guardar clip"): abre la exportación con la cámara y el rango del clip
  ya puestos. Aparece solo si sus roles permiten descargar grabaciones.

Con varias cámaras en la alerta, elija la cámara en la lista de arriba: se
prepara una a la vez, porque los grabadores admiten pocas reproducciones
simultáneas.

La franja de arriba dice la hora del evento. Si dice «hora aproximada», el
evento lo informó otro equipo (por ejemplo, una zona de un panel de alarma) y la
hora se tomó del reloj del servidor: la repetición incluye un segundo de margen
por cada lado.

#table(
  columns: (auto, 1fr),
  table.header[Mensaje][Qué hacer],
  [«El equipo no tiene grabación de ese momento»],
  [La cámara no estaba grabando: revise en el grabador que grabe en forma
    continua o por evento con pregrabación, que el disco no esté lleno y que el
    horario de grabación cubra esa hora. Queda registrado en la bitácora como
    «Repetición del evento sin grabación en el equipo».],
  [«El equipo no grabó el clip completo»],
  [La grabación cubre solo una parte; se reproduce lo que hay y el mensaje dice
    cuántos segundos faltan.],
  [«Sus roles no incluyen ver grabaciones»],
  [La repetición requiere el permiso de ver grabaciones. El video en vivo sigue
    disponible en su pestaña.],
  [No aparece la pestaña Repetición del evento],
  [La alerta no tiene cámaras, la automatización tiene apagada la repetición, o
    su puesto no tiene el módulo Reproducción.],
)
]

El visor de fotos se maneja así:

#table(
  columns: (auto, 1fr),
  table.header[Tecla o gesto][Acción],
  [#tecla("←") #tecla("→")], [Foto anterior o siguiente.],
  [#tecla("+") #tecla("−")], [Acerca o aleja.],
  [#tecla("0")], [Ajusta la foto a la pantalla.],
  [#tecla("1")], [Muestra la foto en tamaño real (1:1).],
  [Rueda del mouse], [Zoom.],
  [Arrastrar], [Mueve la foto ampliada.],
  [Doble clic], [Alterna entre tamaño real y ajustada a la pantalla.],
  [#tecla("Esc")], [Cierra el visor.],
)

=== Varias alertas pendientes

La ventana de alarma es una sola para todas las alertas pendientes. Abajo a la
derecha indica en cuál está y cuántas faltan, por ejemplo «1/3 · 2 sin
confirmar». Las flechas *Alerta anterior* y *Alerta siguiente* recorren la
lista. Cuando llega una alerta nueva, la ventana la deja a la vista.

== Revisar el historial de alertas

#requiere("Atender alertas")

Abra el Centro de eventos con el botón *Centro de eventos* del riel o con la
tarjeta del mismo nombre en Inicio. La lista muestra las últimas 200 alertas,
de la más reciente a la más antigua, y se actualiza sola. Arriba aparece el
resumen, por ejemplo «12 alerta(s) · 2 sin confirmar».

Cada alerta muestra su título y su etiqueta de estado, el mensaje, la fecha y
hora · la automatización · qué la disparó, y la línea *Acuse de recibo*.

#table(
  columns: (auto, 1fr),
  table.header[Etiqueta][Significado],
  [PENDIENTE (roja)], [Exige confirmación y nadie se ha dado por enterado.],
  [Confirmada (verde)], [Alguien se dio por enterado. La línea Acuse de recibo
    dice quién, cuándo, a los cuántos segundos y desde dónde (cliente o panel
    web).],
  [Informativa (gris)], [No requiere confirmación.],
)

- #boton("Ver") abre la ventana de alarma de esa alerta, aunque ya esté
  confirmada.
- #boton("Enterado") confirma la alerta desde la lista, sin abrir la ventana.
  Solo aparece en las pendientes.
- *Solo sin confirmar* deja en la lista solo las pendientes.
- #boton("Actualizar") vuelve a pedir la lista al servidor.

=== Llevar el Centro de eventos a otro monitor

+ Haga clic en #boton("Ventana aparte"). También puede arrastrar la viñeta
  *Centro de eventos* fuera de la barra superior, o hacer clic derecho en ella
  y elegir *Llevar a una ventana nueva*.
+ Arrastre la ventana *Centro de eventos* al monitor que prefiera.

Es la misma lista, no una copia. Mientras está afuera, el botón del riel la trae
al frente. Al cerrarla, el Centro de eventos vuelve al panel principal.

== Atender alertas desde el panel web

#requiere("Atender alertas")

En el panel web, abra #menu("Aplicaciones", "Centro de eventos"). A la
izquierda está la lista de alertas y, a la derecha, la ficha de la alerta
seleccionada, con el mismo contenido que la ventana de alarma del cliente.

#captura("web-centro-de-eventos.png", pie: [Centro de eventos del panel web con una alerta pendiente.])

- La página consulta las alertas cada 5 segundos. Cuando llega una nueva, queda
  seleccionada y aparece el aviso «Nueva alerta», con su título.
- La lista muestra 50 alertas por página; #boton("« Anterior") y
  #boton("Siguiente »") cambian de página.
- La ficha agrega *Dirigida a* (los destinatarios, o «todos los operadores») y,
  si el recurso que la originó tiene ficha, el enlace *ficha del recurso*.
- Las pestañas son *Repetición* (si la alerta la tiene y sus roles permiten ver
  grabaciones), *Fotos*, *Cámaras* y *Qué hizo el sistema*. Un clic en la foto
  la abre en tamaño completo en otra pestaña del navegador.
- *Repetición* hace lo mismo que en el cliente: cuenta regresiva mientras el
  equipo graba el después, el clip en bucle con la marca roja del instante y
  #boton("↺ Desde el comienzo"). Si el navegador no reproduce el formato de la
  cámara (H.265 sin decodificación por hardware), lo dice; la repetición se ve
  en el cliente de escritorio.
- #boton("Enterado") está en la tarjeta de la lista y en la ficha. Al confirmar
  aparece «Alerta confirmada; quedó registrado a su nombre.»; si otro operador
  se le adelantó, el aviso dice quién la había confirmado.

Para que las alertas nuevas suenen en este navegador, marque *Sonar alertas
nuevas*; el navegador recuerda la elección. #boton("Silenciar") calla el sonido
sin confirmar la alerta.

#nota[El panel web no reproduce video: la pestaña Cámaras solo indica cuántas
  cámaras tiene vinculadas la alerta. El video en vivo se ve en la ventana de
  alarma del cliente de monitoreo.]

#importante[El panel web avisa de las alertas nuevas solo mientras la página
  Centro de eventos está abierta. Para el monitoreo permanente use el cliente de
  monitoreo.]

== Configuración

#modulo("automation")[
=== Qué genera las alertas

Un administrador decide qué genera alertas, con qué texto y para quién, en la
acción «Avisar a los operadores» de cada automatización
(#capitulo(<cap-automatizaciones>)). Estas opciones de la acción determinan lo
que ve el operador:

#table(
  columns: (auto, 1fr),
  table.header[Opción de la acción][Efecto en el Centro de eventos],
  [Exigir que un operador se dé por enterado], [Marcada, la alerta nace
    PENDIENTE y abre la ventana de alarma. Desmarcada, es informativa y se
    muestra como aviso flotante.],
  [Destinatarios], [Sin ninguno marcado, la alerta llega a todos los
    operadores.],
  [Importancia], [La severidad: Crítica, Advertencia o Informativa.],
  [Alarma sonora en el equipo del operador], [El sonido que suena en el
    puesto.],
  [Repeticiones del sonido], [De 1 a 5; con 0 suena sin parar hasta que un
    operador confirme la alerta, la silencie o cierre la ventana. El bucle
    solo existe si la alerta exige confirmación: en una informativa, 0 suena
    una vez.],
  [Cámaras del video en vivo de la ventana de alarma], [Las cámaras de la
    pestaña Video en vivo. Si no marca ninguna, se usan las que capturaron foto
    o, si no hubo fotos, las asociadas al recurso en su ficha.],
  [Repetición del evento], [Encendida, la ventana muestra la grabación de esas
    cámaras alrededor del evento, con los segundos antes y después que indique
    la acción.],
)
]

=== Dónde se abre la ventana de alarma

La ventana de alarma se abre en el monitor, la posición y el tamaño en que se
cerró la anterior: en puestos con dos pantallas basta con dejarla una vez en
la de trabajo. Si ese monitor ya no está conectado, aparece centrada.

Es la opción *Abrir cada ventana donde quedó la última vez* (Configuración →
*Sistema*), que viene encendida y vale para todas las ventanas del cliente;
apagada, la ventana de alarma aparece centrada.
