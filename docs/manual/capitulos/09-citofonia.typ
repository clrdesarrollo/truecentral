#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/IntercomView.xaml, src/TrueCentralVms.Client/Views/IntercomCallWindow.xaml, src/TrueCentralVms.Client/Views/IntercomCallWindow.xaml.cs, src/TrueCentralVms.Client/ViewModels/IntercomsViewModel.cs, src/TrueCentralVms.Client/Services/IntercomVoiceClient.cs, src/TrueCentralVms.Server/wwwroot/intercom-console.js, src/TrueCentralVms.Server/wwwroot/intercoms.js, src/TrueCentralVms.Server/Services/IntercomService.cs, src/TrueCentralVms.Server/Api/IntercomsApi.cs

= Citofonía <cap-citofonia>

La citofonía conecta los frentes de videoportero (el citófono con cámara de
cada acceso) con el puesto de monitoreo. Cuando un visitante toca el timbre,
la llamada suena en el cliente de monitoreo y se abre sola una ventana con el
video del frente: el guardia contesta, conversa con el visitante y le abre la
puerta sin moverse del puesto. Cada llamada queda registrada en un historial.

#captura-pendiente("Cliente de monitoreo: módulo Citofonía con la lista de frentes a la izquierda y las últimas llamadas a la derecha", alto: 6cm)

== Cuando suena una llamada

#requiere("Atender citofonía")

Las llamadas se reciben apenas inicia sesión en el cliente de monitoreo,
aunque no haya abierto el módulo Citofonía. Cuando un visitante toca el
timbre:

- suena un timbre en el equipo;
- el botón *Citofonía* del riel lateral se pone ámbar y parpadea;
- se abre la ventana de la llamada, por encima de las demás, con el video del
  frente a la izquierda y los controles a la derecha.

#captura-pendiente("Cliente de monitoreo: ventana de llamada en estado LLAMADA ENTRANTE, con el video del frente y los botones Contestar, Rechazar y Silenciar timbre en este puesto", alto: 6cm)

Para atender:

+ Mire el video para ver quién está en la puerta.
+ Haga clic en #boton("Contestar"). La conversación se abre de inmediato:
  hable con el visitante por el micrófono del equipo.
+ Si no quiere atenderla, haga clic en #boton("Rechazar").

Si necesita silencio para atender otra cosa, haga clic en
#boton("Silenciar timbre en este puesto"): el timbre se calla en su equipo,
pero la llamada sigue sonando en los demás puestos. Cerrar la ventana
mientras suena tiene el mismo efecto.

La parte superior de la ventana muestra el estado de la llamada, el nombre del
frente y un contador: el tiempo que lleva sonando o, ya contestada, el tiempo
de conversación.

#table(
  columns: (auto, 1fr),
  table.header[Estado][Significa],
  [LLAMADA ENTRANTE], [El visitante está llamando. El borde de la ventana se pone ámbar.],
  [EN CONVERSACIÓN], [Usted contestó y está hablando con el visitante.],
  [ATENDIDA EN OTRO PUESTO], [Otro operador contestó primero. La ventana se cierra sola a los pocos segundos.],
  [LLAMADA TERMINADA], [La llamada terminó. La ventana se cierra sola.],
  [NO CONTESTADA], [Nadie contestó o el visitante cortó antes. La ventana se cierra sola.],
  [RECHAZADA], [Alguien rechazó la llamada. La ventana se cierra sola.],
  [EN VIVO], [Ventana abierta a mano, sin llamada: se ve el frente.],
  [HABLANDO], [Ventana abierta a mano y usted le está hablando al frente.],
  [SIN CONEXIÓN], [El frente no responde.],
)

#nota[Si hay varios puestos con el cliente abierto, la llamada suena en todos.
  El primero que contesta se queda con ella; en los demás, la ventana pasa a
  *ATENDIDA EN OTRO PUESTO* y se cierra sola.]

#nota[Los frentes que están fuera de su alcance aparecen en la lista, pero sus
  llamadas no suenan en su puesto. Si abre uno de ellos, ve el video y el
  aviso _Fuera de su alcance: puede verlo, pero no operarlo._]

== Conversar con el visitante

#requiere("Atender citofonía")

Con la llamada en *EN CONVERSACIÓN*, la ventana muestra:

- #boton("Colgar"): termina la llamada.
- #boton("Silenciar mi micrófono"): el visitante deja de oírlo, pero usted lo
  sigue escuchando. El botón cambia a #boton("Micrófono silenciado"); otro
  clic vuelve a activarlo.
- *Mi micrófono* y *Visitante*: barras que se mueven con la voz de cada uno.
  Sirven para comprobar que el audio llega en ambos sentidos.
- El deslizador de volumen, que sube o baja la voz del visitante solo en este
  equipo.

Para terminar, haga clic en #boton("Colgar"). Cerrar la ventana durante la
conversación también cuelga la llamada.

El cliente usa el micrófono elegido en el panel de parlantes de la vista en
vivo#modulo("speakers")[ (vea #capitulo(<cap-parlantes-ip>))]; si no eligió
ninguno, usa el micrófono predeterminado de Windows. Si el equipo no tiene
micrófono, la ventana avisa _Este equipo no tiene micrófono: solo podrá
escuchar._

#importante[El propio frente corta la llamada unos segundos después de abrir
  la puerta o al cumplir su tiempo máximo de conversación. Si eso pasa
  mientras habla, la ventana avisa _El citófono terminó la llamada (…). La
  conversación sigue abierta: cuelgue cuando termine._ Puede seguir hablando
  con el visitante; recuerde hacer clic en #boton("Colgar") al terminar.]

Por omisión, el sistema corta una conversación a los 10 minutos.

== Abrir la puerta

#requiere("Atender citofonía")

Desde la ventana de la llamada, haga clic en #boton("Abrir puerta"). Si el
frente controla más de una puerta, hay un botón por cada una
(#boton("Abrir puerta 1"), #boton("Abrir puerta 2")…). Los botones funcionan
mientras la llamada suena, durante la conversación y también sin llamada. El
resultado aparece al pie de la ventana, por ejemplo _Puerta 1 de 'Acceso
principal' abierta._

También puede abrir la puerta sin abrir la ventana:

+ En el módulo Citofonía, busque el frente en la lista.
+ Haga clic en #boton("Abrir puerta").
+ Confirme con #boton("Sí") en la pregunta _¿Abrir la puerta de «…»?_

#nota[Si la puerta se abre durante una llamada, el historial lo registra en la
  columna *Puerta* con el nombre de quien la abrió.]

== Ver un frente y hablar sin llamada

#requiere("Atender citofonía")

El módulo Citofonía se abre con el botón *Citofonía* del riel lateral o con
la tarjeta *Citofonía* del inicio. Cerrar su viñeta no apaga nada: las
llamadas siguen sonando en el puesto.

La lista *FRENTES DE CITOFONÍA* muestra cada frente con un punto verde (en
línea) o rojo (sin conexión), su modelo, dirección y grupo, el estado de la
conexión y el de la llamada:

#table(
  columns: (auto, 1fr),
  table.header[Llamada][Significa],
  [Libre], [Sin llamada en curso.],
  [Sonando], [Hay una llamada esperando respuesta.],
  [En conversación · usuario], [El usuario indicado está atendiendo la llamada.],
  [Pausado], [El frente está desactivado en la configuración: sus llamadas no llegan al sistema.],
  [El botón no llama a la central], [El botón del frente no está configurado para llamar al sistema. Avise al administrador.],
)

Para ver la cámara de un frente o llamar a alguien que está frente a él:

+ Haga clic en #boton("Ver y hablar") en el frente. Se abre su ventana en
  estado *EN VIVO*.
+ Para hablarle, haga clic en #boton("Hablar con el frente"). El estado pasa a
  *HABLANDO*.
+ Para terminar, haga clic en #boton("Dejar de hablar").

#nota[Solo un operador a la vez puede hablar con un frente. Si otro ya lo está
  haciendo, o la llamada la tomó otro puesto, aparece _El servidor no abrió la
  conversación: otro operador está hablando con este frente, la llamada ya la
  tomó alguien o el frente no responde._]

Si la ventana dice _Este frente no tiene cámara asociada_, el administrador
debe elegir su cámara en la configuración del frente (vea la sección
Configuración de este capítulo). Si dice _La cámara del frente no está disponible en este
cliente (canal deshabilitado o sin permiso)_, la cámara existe, pero su canal
está deshabilitado o usted no tiene permiso para verlo.

== Historial de llamadas

#requiere("Atender citofonía")

La parte derecha del módulo, *ÚLTIMAS LLAMADAS*, muestra las 100 llamadas más
recientes y se actualiza sola; el botón #boton("Actualizar") la vuelve a leer.

#table(
  columns: (auto, 1fr),
  table.header[Columna][Contenido],
  [Inicio], [Fecha y hora en que empezó a sonar.],
  [Frente], [El frente que llamó.],
  [Resultado], [_Sonando_, _En conversación_, _Contestada_, _No contestada_ o _Rechazada_.],
  [Atendió], [El usuario que contestó. _otro receptor (monitor interior)_ si la contestó un monitor del edificio.],
  [Duración], [Tiempo de conversación.],
  [Puerta], [_Abierta_ y quién la abrió, si se abrió durante la llamada.],
  [Detalle], [Cómo terminó: _nadie contestó_, _el visitante cortó_, _colgó …_, _rechazada por …_, _contestada en otro equipo_, entre otros.],
)

== Atender desde el panel web

#requiere("Atender citofonía")

El panel web tiene su propio puesto de atención en #menu("Citofonía") (sección
Aplicaciones). A la izquierda están los *Frentes de citofonía*, con la
cantidad en línea, y a la derecha las *Últimas llamadas*, que se pueden
filtrar por frente y por resultado (_Contestadas_, _No contestadas_,
_Rechazadas_) y recorrer de a 50 con #boton("« Anteriores") y
#boton("Siguientes »").

Cuando una llamada suena, el navegador toca el timbre y abre la ventana de la
llamada sobre la página; en la lista, el botón del frente cambia a
#boton("Atender"). La ventana tiene los mismos controles que en el cliente:
#boton("Contestar"), #boton("Rechazar"), la casilla *Silenciar timbre en este
puesto*, #boton("Colgar"), el silencio del micrófono, el volumen y un botón
por puerta.

Diferencias con el cliente de monitoreo:

- En lugar de video en vivo, la ventana muestra una foto de la cámara del
  frente que se renueva cada pocos segundos.
- Se atiende un frente a la vez. Con una conversación abierta, otro frente
  muestra _Termine la conversación en curso antes de atender otro frente._
- El navegador solo entrega el micrófono si el panel se abrió por HTTPS (o en
  el propio servidor). Si no, la página lo avisa arriba y la conversación se
  abre _SOLO PARA ESCUCHAR_: puede oír al visitante y abrirle la puerta, pero
  no hablarle. Lo mismo ocurre si el navegador no tiene permiso para usar el
  micrófono.

#importante[El panel web solo recibe llamadas mientras la página Citofonía
  está abierta. Si se va a otra página, el timbre se calla y la ventana se
  cierra; si estaba conversando, la llamada se corta. Para un puesto de
  atención permanente use el cliente de monitoreo.]

== Configuración

#requiere("Citofonía")

Los frentes se administran en el panel web, en #menu("Dispositivos",
"Citofonía"). La tabla muestra cada frente con su *Nombre* (y ubicación),
*Grupo*, *Dirección*, *Modelo*, *Cámara*, cantidad de *Puertas*, estado de
*Conexión* y *Llamada* en curso; el estado se actualiza solo.

#captura-pendiente("Panel web: Dispositivos › Citofonía con la tabla de frentes y el formulario Agregar frente de citofonía abierto")

=== Antes de empezar

- El sistema es compatible con frentes de videoportero Hikvision de las
  familias DS-KB, DS-KD y DS-KV.
- Para ver video al sonar, agregue también el frente en *Fuentes de video*
  como cámara Hikvision (vea #capitulo(<cap-fuentes-de-video>)). Luego lo
  elegirá como cámara del frente.
- Tenga a mano la dirección IP, el usuario y la contraseña del frente.

=== Agregar un frente

+ Haga clic en #boton("Agregar frente").
+ Escriba el *Nombre* (por ejemplo, _Portería principal_) y, si quiere, un
  *Grupo* (_Portería_, _Torre A_…) que ayuda a ordenar la lista.
+ Elija la *Ubicación* del frente en el árbol de recursos (vea
  #capitulo(<cap-recursos-y-ubicaciones>)). Un frente _Por ubicar_ no lo ven
  los operadores con alcance restringido.
+ En *Marca / protocolo*, deje _Hikvision (frente de videoportero DS-KB /
  DS-KD / DS-KV)_.
+ Escriba la *Dirección (IP o hostname)* y revise los puertos en *Puerto SDK /
  Puerto HTTP* (8000 y 80 por omisión).
+ Escriba el *Usuario del frente* y la *Contraseña*.
+ En *Cámara del frente (video al sonar)*, elija el canal del frente. Si no
  tiene cámara, deje _— Sin video —_.
+ Deje marcadas las tres casillas (vea la tabla de abajo).
+ Si quiere revisar los datos antes de guardar, haga clic en
  #boton("Probar conexión").
+ Haga clic en #boton("Guardar"). El sistema se conecta con el frente para
  validar las credenciales; si todo está bien, aparece _Frente agregado y
  validado._

#table(
  columns: (1fr, 1fr),
  table.header[Casilla][Para qué sirve],
  [Configurar el botón del frente para que llame a la central (necesario para recibir las llamadas)],
  [Deja el botón del frente llamando al sistema. Sin esto, las llamadas no llegan.],
  [Ajustar el video del frente para que la imagen aparezca al instante (un cuadro completo por segundo)],
  [Si el frente entrega un cuadro completo cada varios segundos, el video
    tarda eso en aparecer al sonar. Con la casilla marcada, el sistema lo
    deja en uno por segundo.],
  [Activo (el servidor recibe sus llamadas y lo ofrece a los operadores)],
  [Desmarcada, el frente queda _pausado_: sus llamadas no llegan al sistema.],
)

#boton("Probar conexión") muestra _✔ Conexión validada_ con lo que el
sistema leyó del equipo:

#table(
  columns: (auto, 1fr),
  table.header[Dato][Significa],
  [Modelo, Nombre en el equipo, N° de serie, Firmware], [Identificación del frente.],
  [Puertas], [Cuántas puertas controla: es la cantidad de botones de apertura de la ventana de llamada.],
  [Voz], [El formato de voz del frente. _no soportada_ significa que no se podrá conversar con él.],
  [Botón → central], [_sí_, o _no (se configurará al guardar)_ si falta configurarlo.],
  [Cuadro completo], [Cada cuánto entrega un cuadro completo. Con la marca _lento_, se ajustará a 1 segundo al guardar.],
  [Cámara], [_encontrada en Fuentes de video (seleccionada)_ si el frente ya está como cámara; queda elegido en el formulario.],
)

Si guarda y el sistema no logra configurar el botón, aparece _No se pudo
configurar el botón del frente para llamar a la central; revise la
bitácora._ y la columna *Llamada* muestra *No llama a la central*. Edite el
frente, revise que la casilla esté marcada y vuelva a guardar.

#importante[El frente solo llama al sistema mientras el servidor está
  conectado con él. Si el servidor está detenido o no llega al frente, el
  timbre llama solo al monitor interior del edificio (si lo hay).]

=== Buscar frentes en la red

Debajo de la tabla, *Equipos en línea* lista los frentes compatibles que el
servidor encuentra en su segmento de red; se actualiza cada 30 segundos y
#boton("Buscar") repite la búsqueda. El botón #boton("Agregar") de cada fila
abre el formulario con la dirección y el modelo ya completos. Los equipos de
fábrica se activan y su IP se cambia desde la misma tabla, igual que en
Fuentes de video (vea #capitulo(<cap-fuentes-de-video>)).

#nota[La búsqueda no cruza routers ni VPN. Un frente en otra red se agrega a
  mano con su dirección.]

=== Editar, pausar o eliminar un frente

- #boton("Editar") abre el mismo formulario. Deje la *Contraseña* vacía para
  conservar la actual. Para pausar un frente sin borrarlo, desmarque
  *Activo*.
- #boton("Eliminar") pide confirmación. El historial de llamadas del frente se
  conserva. No se puede eliminar un frente con una conversación en curso.
- #boton("Historial") filtra el *Historial de llamadas* que está al final de
  la página para mostrar solo ese frente. Ese historial también se puede
  filtrar por resultado y se recorre de a 50 llamadas.

=== Mensajes al guardar

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [Ya existe un frente con esa dirección y puerto.], [El frente ya está agregado: búsquelo en la tabla.],
  [La contraseña del frente es obligatoria.], [Escriba la contraseña del frente.],
  [El módulo «Citofonía» no está incluido en la licencia. Amplíe la licencia para habilitarlo.], [La licencia no trae citofonía: contacte a su proveedor.],
  [La licencia permite N frentes y ya hay N en uso. Amplíe la licencia para agregar más.], [Cada frente activo ocupa un cupo. Pause o elimine uno que no use, o amplíe la licencia.],
  [El frente está en una conversación (…); termínela antes de eliminarlo.], [Espere a que termine la llamada.],
)

Si el frente rechaza la conexión, el formulario muestra el motivo (por
ejemplo, credenciales incorrectas o equipo que no responde). Revise la
dirección, los puertos, el usuario y la contraseña.
