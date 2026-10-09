#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/EventCenterView.xaml, src/TrueCentralVms.Client/Views/EventCenterWindow.xaml, src/TrueCentralVms.Client/ViewModels/EventCenterViewModel.cs, src/TrueCentralVms.Client/Views/AlertWindow.xaml, src/TrueCentralVms.Client/Views/AlertWindow.xaml.cs, src/TrueCentralVms.Client/Views/ToastWindow.xaml, src/TrueCentralVms.Client/Views/ImageViewerWindow.xaml, src/TrueCentralVms.Client/Services/AlertSoundPlayer.cs, src/TrueCentralVms.Client/Views/SettingsWindow.xaml, src/TrueCentralVms.Client/Views/MainWindow.xaml, src/TrueCentralVms.Server/wwwroot/event-center.js, src/TrueCentralVms.Server/wwwroot/workflows.js

= Centro de eventos <cap-centro-de-eventos>

El Centro de eventos reúne las alertas que el sistema emite para los
operadores y lleva el registro de su acuse de recibo: quién se dio por
enterado de cada una, a qué hora y desde dónde. Después de un incidente
permite responder qué avisó el sistema y quién lo atendió, o si nadie lo hizo.

#captura-pendiente("Cliente de monitoreo: Centro de eventos con varias alertas, una PENDIENTE y dos Confirmadas", alto: 6cm)

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
  sonora si la automatización tiene una. El aviso se cierra solo a los 15
  segundos (30 si trae foto); la ✕ lo descarta antes.

Si el cliente estaba cerrado o sin conexión cuando se emitió una alerta, al
abrirlo (o al recuperar la conexión) muestra las que siguen sin confirmar.

La automatización puede dirigir la alerta a usuarios concretos. Si no lo hace,
la reciben todos los que tienen el permiso *Atender alertas* y pueden ver el
recurso que la originó, según su alcance por ubicación. El administrador las ve
todas.

== Atender una alerta en la ventana de alarma

#requiere("Atender alertas")

#captura-pendiente("Cliente de monitoreo: ventana Información de la alarma con consignas, la pestaña Fotos y los botones Enterado y Silenciar", alto: 6cm)

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

A la derecha hay tres pestañas: *Fotos*, *Video en vivo* y *Qué hizo el
sistema*.

=== Darse por enterado

+ Lea las consignas, si las hay, y revise las fotos o el video.
+ Haga clic en #boton("Enterado").

El sistema registra su usuario, la hora y si confirmó desde el cliente o desde
el panel web. La ventana pasa a la siguiente alerta pendiente y se cierra cuando
no queda ninguna.

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

+ Haga clic en #boton("Ventana aparte").
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
- Las pestañas son *Fotos*, *Cámaras* y *Qué hizo el sistema*. Un clic en la
  foto la abre en tamaño completo en otra pestaña del navegador.
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
    operador confirme la alerta, la silencie o cierre la ventana.],
  [Cámaras del video en vivo de la ventana de alarma], [Las cámaras de la
    pestaña Video en vivo. Si no marca ninguna, se usan las que capturaron foto
    o, si no hubo fotos, las asociadas al recurso en su ficha.],
)
]

=== Dónde se abre la ventana de alarma

En puestos con dos pantallas conviene que la ventana de alarma se abra siempre
en la misma. Este ajuste es de cada puesto:

+ En el cliente de monitoreo, haga clic en el engranaje *Configuración* del
  riel.
+ Abra el apartado *Sistema*.
+ Marque *Abrir la ventana de alarma donde quedó la última vez*.
+ Haga clic en #boton("Guardar").

Desde entonces la ventana de alarma se abre en el monitor, la posición y el
tamaño en que se cerró la anterior. Si ese monitor ya no está conectado,
aparece centrada sobre la ventana principal.
