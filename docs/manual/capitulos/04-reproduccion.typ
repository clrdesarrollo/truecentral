#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/PlaybackView.xaml, src/TrueCentralVms.Client/Views/PlaybackView.xaml.cs, src/TrueCentralVms.Client/ViewModels/PlaybackViewModel.cs, src/TrueCentralVms.Client/ViewModels/PlaybackCellViewModel.cs, src/TrueCentralVms.Client/ViewModels/RecordingCalendar.cs, src/TrueCentralVms.Client/Views/TimelineBar.cs, src/TrueCentralVms.Client/Views/ExportDialog.xaml, src/TrueCentralVms.Client/Views/ExportDialog.xaml.cs, src/TrueCentralVms.Client/Views/DownloadCenterWindow.xaml, src/TrueCentralVms.Client/ViewModels/DownloadCenterViewModel.cs, src/TrueCentralVms.Client/Views/MainWindow.xaml, src/TrueCentralVms.Client/Views/SettingsWindow.xaml, src/TrueCentralVms.Server/Api/PlaybackApi.cs

= Reproducción <cap-reproduccion>

Reproducción sirve para revisar lo que grabaron los equipos de video: elegir
una o varias cámaras y un día, moverse por la línea de tiempo hasta el momento
que interesa, ver hasta cuatro cámaras sincronizadas a la misma hora y
exportar un tramo a un archivo de video. Se usa desde el cliente de monitoreo.

#captura-pendiente("Cliente de monitoreo: Reproducción en 4 posiciones, dos cámaras reproduciendo a la misma hora y la línea de tiempo con tramos de varios colores", alto: 6cm)

La pantalla tiene cuatro zonas:

- a la izquierda, el árbol *GRABACIONES* con las cámaras;
- arriba, la barra del día, los botones de división y la leyenda de colores;
- al centro, la grilla con los cuadros de video;
- abajo, los controles de reproducción, el tramo marcado y la línea de tiempo.

#nota[El sistema no guarda video propio: reproduce lo que tiene grabado cada
  equipo, en el disco del grabador o en la tarjeta de la cámara. Hasta qué
  fecha se puede retroceder depende de ese disco y de la configuración de
  grabación del equipo.]

== Abrir Reproducción

#requiere("Ver grabaciones")

+ Haga clic en el botón *Reproducción* del menú lateral o en la tarjeta
  *Reproducción* de la pantalla de inicio.
+ El módulo se abre en la pestaña *Reproducción* de la barra superior.

Para cerrarlo, haga clic en la ✕ de la pestaña. Cerrar la pestaña detiene la
reproducción.

== Elegir las cámaras

Las cámaras se eligen en el árbol *GRABACIONES*. Los botones #boton("Equipo") y
#boton("Ubicación") cambian cómo se agrupan, y el buscador *Buscar canal…*
filtra la lista aunque no escriba las tildes.

- *Doble clic* en una cámara la agrega en la primera posición libre de la
  grilla.
- *Arrastrar* una cámara hasta una posición la pone exactamente ahí. Si en esa
  posición había otra cámara, la reemplaza.
- #tecla("Ctrl") + doble clic deja solo esa cámara: cierra las demás y vuelve a
  la división de un canal.

Al agregar una cámara, la reproducción arranca sola. Si ya hay otras cámaras
reproduciendo, la nueva se suma a la misma hora. Si no, parte desde la aguja
de la línea de tiempo o, si todavía no se ha movido a ninguna hora, desde la
primera grabación del día elegido.

Se pueden ver hasta cuatro cámaras a la vez. Con las cuatro posiciones
ocupadas, el doble clic reemplaza la cámara del cuadro seleccionado. Una cámara
que ya está en la grilla no se abre dos veces: su cuadro queda seleccionado y
la barra de controles avisa que ya está en la grilla.

Para seleccionar un cuadro, haga clic en su barra de título o sobre el video:
queda con el borde azul. El cuadro seleccionado decide qué días se marcan en
el calendario y qué cámara propone la exportación.

Para quitar una cámara, haga clic en la ✕ de la barra de su cuadro (*Quitar el
canal de la reproducción*). La posición queda libre.

=== Una cámara o cuatro posiciones

- #boton("1 canal") muestra una sola cámara en toda la grilla. Si había varias,
  se queda la del cuadro seleccionado y las demás se cierran.
- #boton("4 posiciones") divide la grilla en un mosaico de 2×2 para comparar
  cámaras a la misma hora. Si está viendo un canal y agrega otro, la grilla
  pasa sola a 4 posiciones.

Junto a estos botones, un contador (por ejemplo, _2 de 4 canales_) indica
cuántas posiciones están ocupadas.

#nota[Cada cuadro abierto ocupa una reproducción en el grabador, y los
  grabadores limitan cuántas entregan a la vez, sumando las de otros puestos.
  Cierre los cuadros que no esté mirando.]

== Elegir el día

En la barra superior, junto a *Día:*:

- las flechas *Día anterior* y *Día siguiente* cambian de a un día (no se puede
  pasar de hoy);
- el botón con la fecha abre el calendario;
- #boton("Hoy") vuelve al día actual.

El calendario muestra un mes, con la semana partiendo el lunes, y sus flechas
cambian de mes. Los días con grabación llevan una pequeña marca azul (_con
grabación_), según lo que informe el equipo de la cámara seleccionada; mientras
el equipo responde se ve una barra de progreso. Hoy aparece con borde y los
días futuros no se pueden elegir. Haga clic en un día para ir a él.

#captura-pendiente("Cliente de monitoreo: Reproducción con el calendario abierto y varios días con la marca de grabación")

Si cambia de día mientras reproduce, la reproducción sigue a la misma hora: si
estaba viendo las 14:32 del martes y elige el viernes, continúa en las 14:32
del viernes. Así se compara un mismo momento en días distintos. Si no estaba
reproduciendo, parte desde la primera grabación del día. Si el día elegido no
tiene grabaciones, la reproducción se detiene.

#nota[No todos los equipos informan qué días tienen grabación. Si el
  calendario no muestra marcas, igual sirve para elegir la fecha.]

== Moverse en la línea de tiempo

La línea de tiempo tiene una franja por cámara abierta (con su nombre, si cabe)
y, abajo, la regla con las horas. Los tramos grabados se pintan según el tipo
de grabación que informa el equipo, con la misma leyenda de la barra superior:

#table(
  columns: (auto, auto, 1fr),
  table.header[Color][Leyenda][Tramo grabado],
  [Azul], [Continua], [Grabación permanente.],
  [Ámbar], [Movimiento], [Grabación disparada por detección de movimiento.],
  [Rojo], [Alarma], [Grabación disparada por una alarma o evento del equipo.],
  [Violeta], [Manual], [Grabación iniciada a mano en el equipo.],
  [Gris], [Otra], [Otro tipo de grabación, o un equipo que no informa el tipo.],
)

La aguja roja queda siempre al centro y lo que se mueve es la franja, como en
un grabador: mientras se reproduce, los tramos corren por debajo de la aguja.
Al centro de la barra de controles, el reloj muestra la fecha y la hora que se
está viendo.

- *Clic*: todas las cámaras saltan a esa hora.
- *Arrastrar*: empuja la franja; hacia la derecha trae horas anteriores.
  Mientras arrastra, el reloj y una etiqueta sobre la aguja muestran la hora a
  la que va a saltar. Al soltar, la reproducción sigue desde esa hora.
- *Rueda del mouse*, o los botones − y + a la derecha de la línea de tiempo:
  acercan o alejan la vista alrededor de la aguja, desde el día completo
  (24 h) hasta 1 minuto. Entre ambos botones se ve cuánto tiempo abarca la
  vista.

Si en la hora elegida no hay grabación, la reproducción salta al comienzo del
siguiente tramo grabado. Cerca de la medianoche, los costados de la franja se
oscurecen: ese tiempo pertenece al día anterior o al siguiente, no es falta de
grabación.

== Controlar la reproducción

Los controles están abajo a la izquierda y actúan sobre todas las cámaras a la
vez: las cámaras de la grilla reproducen siempre la misma hora.

#table(
  columns: (auto, 1fr),
  table.header[Control][Acción],
  [*Pausar / reanudar*], [Detiene la imagen o la reanuda. Puede pausar el tiempo
    que necesite: al reanudar, la reproducción sigue desde el mismo instante.],
  [*Detener la reproducción*], [Corta la reproducción de todas las cámaras.],
  [#boton("−5 m") #boton("−30 s")], [Retroceden 5 minutos o 30 segundos.],
  [#boton("+30 s") #boton("+5 m")], [Avanzan 30 segundos o 5 minutos.],
  [*Más lento* / *Más rápido*], [Cambian la velocidad entre 0,25×, 0,5×, 1×, 2×,
    4× y 8×. La velocidad actual se ve entre ambos botones.],
)

#nota[Al cambiar la velocidad, la grabación se vuelve a pedir al equipo desde
  donde va la aguja. A velocidades altas la imagen puede detenerse a ratos
  esperando datos, según el equipo y la red. Si el equipo no acepta la
  velocidad pedida, aparece _El equipo no aceptó reproducir a 4×._ (con la
  velocidad elegida): vuelva a 1×.]

=== Gestos sobre el video

- Doble clic en el tercio izquierdo del video: retrocede 30 segundos.
- Doble clic en el tercio derecho: avanza 30 segundos.
- Doble clic al centro: entra o sale de la pantalla completa.

=== Pantalla completa

El botón de pantalla completa, a la derecha de la barra de controles, o el
doble clic al centro del video, ocultan el árbol, la barra superior y el menú
lateral, y la ventana pasa a ocupar todo el monitor. La grilla, los controles y
la línea de tiempo siguen a la vista. Presione #tecla("Esc") para salir.

=== Zoom digital

Con #boton("Zoom") activo:

- arrastre sobre el video para marcar el área que quiere acercar; al soltar,
  esa área ocupa todo el cuadro;
- la rueda del mouse acerca o aleja sobre el puntero;
- el clic derecho vuelve a 1×.

El zoom funciona también en pausa, para revisar un detalle. Mientras el modo
esté activo, el doble clic sobre el video no salta ni cambia a pantalla
completa. Haga clic otra vez en #boton("Zoom") para desactivarlo.

=== Audio, capturas y cápsulas

La barra de título de cada cuadro tiene estos botones, de izquierda a derecha:

#table(
  columns: (1fr, 1fr),
  table.header[Botón (descripción emergente)][Acción],
  [*Audio de este cuadro*], [Enciende o apaga el sonido. Suena un solo cuadro a
    la vez: encender uno silencia los demás.],
  [*Grabar una cápsula de lo que se está reproduciendo*], [El primer clic
    empieza a grabar lo que se está viendo; mientras graba, el botón se pone
    rojo y muestra el tiempo. El segundo clic detiene la grabación.],
  [*Guardar una captura del cuadro que se está viendo*], [Guarda una imagen del
    cuadro.],
  [*Quitar el canal de la reproducción*], [Cierra el cuadro.],
)

Las capturas van a la *Carpeta de capturas* y las cápsulas a la *Carpeta de
grabaciones locales* de la configuración del cliente. El nombre de cada
archivo lleva la cámara y la hora de la grabación que se estaba viendo, no la
hora del reloj del puesto. Al guardar aparece una notificación (_Captura
guardada_ o _Grabación guardada_) con la ruta del archivo; haga clic en la ruta
para abrir la carpeta.

#consejo[La cápsula guarda lo que usted está mirando mientras la deja
  grabando. Para obtener un tramo exacto entre dos horas, use
  #boton("Exportar…") (siguiente sección).]

== Exportar un tramo

#requiere("Exportar grabaciones")

Exportar copia un tramo de la grabación del equipo a un archivo de video en
este computador. Puede marcar el tramo en la línea de tiempo o escribir las
horas directamente en la ventana de exportación.

=== Marcar el tramo en la línea de tiempo

+ Active #boton("Recorte") en la barra de controles.
+ Arrastre sobre la línea de tiempo desde el inicio hasta el fin del tramo. El
  tramo queda sombreado en azul y, junto a *Tramo:*, aparecen su inicio, su
  fin y su duración.
+ Para revisarlo, haga clic en #boton("Reproducir tramo"): la reproducción
  parte desde el inicio del tramo.

Para borrar la marca, haga clic en la ✕ junto al tramo (*Borrar la marca*) o
haga clic derecho sobre la línea de tiempo. Desactive #boton("Recorte") para
volver a moverse arrastrando la franja.

#captura-pendiente("Cliente de monitoreo: línea de tiempo en modo Recorte con un tramo marcado y el texto «Tramo:» con su duración")

=== Exportar

+ Haga clic en #boton("Exportar…"). Se abre la ventana *Exportar grabaciones*.
+ En *Cámara*, elija la cámara. Viene elegida la del cuadro seleccionado, pero
  puede elegir cualquier cámara, aunque no esté abierta en la grilla.
+ Revise *Inicio (fecha y hora)* y *Fin (fecha y hora)*. Cada uno tiene la
  fecha (dd-mm-aaaa) y la hora (hh:mm:ss, en 24 horas). Vienen con el tramo
  marcado; si no marcó ninguno, con una hora a partir de la aguja (o del
  mediodía del día visible).
+ En *Carpeta de destino*, escriba la carpeta o elíjala con #boton("…").
+ En *Formato de exportación*, elija una opción:
  - _MP4 — compatible con cualquier reproductor (audio convertido a AAC)_;
  - _MKV — copia exacta del equipo (video y audio originales)_.
+ En *Archivos*, elija _Una sola cápsula de video (tramo de hasta 2 horas)_ o
  _Dividir en archivos de_ 15 minutos, 30 minutos, 1 hora o 2 horas cada uno.
+ Revise el resumen al pie de la ventana (por ejemplo, _Se exportarán 2
  archivos MP4 · 60 min de video en total_) y haga clic en #boton("Exportar").

Cada archivo entra como una descarga al Centro de descargas, que se abre solo
(vea la sección siguiente). La ventana recuerda la carpeta, el formato y la
división para la próxima vez.

#captura-pendiente("Cliente de monitoreo: ventana Exportar grabaciones con un tramo de una hora, formato MP4 y división en archivos de 30 minutos")

- En ambos formatos el video se copia tal como lo grabó el equipo, sin pérdida
  de calidad. Si el video es HEVC (H.265), algunos reproductores no lo abren:
  use VLC o instale la extensión HEVC de Windows.
- Cada archivo se llama con el equipo, la cámara y la fecha y hora de su propio
  inicio, por ejemplo `Grabador_Acceso_Principal_20261009_143000.mp4`. Si ya
  existe un archivo con ese nombre, se agrega un número: `(2)`, `(3)`…
- Las propiedades del archivo (título y comentario) indican el equipo, la
  cámara y el tramo exportado, lo que ayuda a usarlo como evidencia.

Si algo no está bien, la ventana muestra el motivo en rojo y no exporta:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [_Elija la cámara a exportar._], [Elija una cámara en *Cámara*.],
  [_El inicio no es válido: use fecha dd-mm-aaaa y hora hh:mm:ss._ (o lo mismo
    para el fin)], [Corrija la fecha o la hora con ese formato.],
  [_El fin debe ser posterior al inicio._], [Revise las horas.],
  [_Una sola cápsula admite hasta 2 horas: acorte el tramo o elija dividir en
    varios archivos._], [Acorte el tramo o elija _Dividir en archivos de_.],
  [_El rango no puede superar 24 horas._], [Exporte por partes.],
  [_La división genera más de 48 archivos: use archivos más largos o acorte el
    rango._], [Elija archivos más largos.],
  [_Indique la carpeta de destino._], [Escriba o elija una carpeta.],
  [_No se pudo usar la carpeta de destino:_ …], [Elija una carpeta donde pueda
    escribir.],
)

=== Seguir las exportaciones en el Centro de descargas

#requiere("Exportar grabaciones")

El Centro de descargas se abre solo al exportar. También se abre con su botón
en la parte de abajo del menú lateral (*Centro de descargas*), que se enciende
y pulsa mientras haya descargas en curso o en cola. Puede seguir trabajando
mientras las descargas avanzan: cerrar la ventana no las detiene.

Las descargas se hacen de a una, en el orden en que se pidieron. Cada fila
muestra la cámara, el tramo y su duración, la hora en que se pidió, el nombre
del archivo (al pasar el mouse se ve la ruta completa), el avance en MB y el
estado:

#table(
  columns: (auto, 1fr),
  table.header[Estado][Significado],
  [Por comenzar], [En cola, esperando su turno.],
  [Descargando], [El servidor está trayendo el tramo desde el equipo
    (_Conectando con el equipo…_ y luego los MB recibidos).],
  [Terminada], [El archivo está completo. Aparece la notificación _Grabación
    exportada_ con la ruta.],
  [Fallida], [No se pudo exportar; el motivo aparece junto al avance.],
  [Cancelada], [Se canceló; el archivo a medias se borra.],
)

Los botones de cada fila (sin texto, con descripción emergente) son *Cancelar
esta descarga*, *Reintentar la exportación* (en las fallidas y canceladas),
*Abrir la carpeta del archivo* (en las terminadas) y *Quitar de la lista (no
borra el archivo)*. #boton("Limpiar terminadas") quita de la lista las
terminadas, fallidas y canceladas, sin borrar ningún archivo. La lista dura lo
que dura la sesión del cliente.

#captura-pendiente("Cliente de monitoreo: Centro de descargas con una exportación Descargando, una Por comenzar, una Terminada y una Fallida")

#importante[Si sale del cliente o cierra sesión con descargas en curso o en
  cola, el sistema avisa y pide confirmar. Si confirma, las descargas se
  cancelan y los archivos a medias quedan incompletos.]

Motivos frecuentes de una exportación *Fallida*:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [_El equipo no entregó video para ese tramo._ (o _tiempo de espera
    agotado_), _El servidor no entregó video para ese tramo._],
    [Revise en la línea de tiempo que haya grabación en ese tramo y que el
    equipo esté en línea, y use *Reintentar la exportación*.],
  [_Su rol no le permite hacer esto (requiere "Exportar grabaciones")._],
    [Su usuario no tiene el permiso para exportar. Pídaselo al administrador.],
  [_Este equipo aún no soporta reproducción remota desde el VMS._],
    [Vea la sección siguiente si el equipo es ONVIF.],
)

== Diferencias según el equipo

Las grabaciones de los equipos Hikvision y Dahua se reproducen exactamente
desde la hora elegida, y sus tramos se pintan según el tipo de grabación.

Los equipos agregados por ONVIF tienen estas limitaciones:

- Reproducen desde el inicio de la grabación que contiene la hora elegida, no
  desde esa hora. La barra de controles lo avisa con _El equipo ONVIF reproduce
  desde el inicio de su grabación: no permite posicionarse por hora._ En ese
  caso, el reloj de la barra no indica la hora real de la imagen: guíese por la
  hora sobreimpresa en el video, si la cámara la muestra.
- Solo informan el comienzo y el fin de cada grabación, así que sus tramos se
  pintan en gris (_Otra_) aunque tengan huecos.
- Al exportar, el archivo también parte desde el inicio de esa grabación. Si la
  exportación falla con _Este equipo aún no soporta reproducción remota desde el
  VMS._, abra primero esa cámara en Reproducción y vuelva a exportar.
- Si el equipo no ofrece búsqueda de grabaciones, el cuadro muestra _El equipo
  ONVIF no ofrece el servicio de búsqueda de grabaciones (Perfil G): no se
  pueden consultar sus grabaciones desde el VMS._

== Mensajes y qué hacer

Los avisos aparecen en la barra de controles, junto a *Tramo:*, o dentro del
cuadro de cada cámara.

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué significa y qué hacer],
  [_Sin grabaciones este día._ (en el cuadro) o _Sin grabaciones el …. El
    equipo conserva según su propio disco y política._],
    [El equipo no tiene grabación de esa cámara ese día. Elija otro día; las
    marcas del calendario ayudan.],
  [_No hay grabación desde esa hora en adelante._],
    [Después de la hora elegida no hay nada grabado. Elija una hora anterior.],
  [_Fin del tramo._],
    [El equipo terminó de entregar la grabación. Haga clic en la línea de
    tiempo para seguir desde otra hora.],
  [_El grabador no puede entregar otra reproducción simultánea (sin ancho de
    banda disponible). Cierre algún canal e intente de nuevo._],
    [El grabador llegó a su límite de reproducciones a la vez, que incluye las
    de otros puestos. Cierre cuadros que no use y vuelva a hacer clic en la
    línea de tiempo.],
  [_El equipo rechazó la reproducción:_ seguido de un código y un motivo],
    [Anote el mensaje completo e informe al administrador.],
  [_No se pudo abrir la grabación:_ …],
    [Vuelva a hacer clic en la línea de tiempo. Si se repite, revise que el
    equipo esté en línea.],
  [_El canal está deshabilitado._],
    [Un administrador debe habilitar esa cámara (botón #boton("Canales") del
    equipo en Fuentes de video).],
  [_Ese recurso está fuera de su alcance (las ubicaciones y recursos de sus
    roles)._],
    [Su usuario no tiene acceso a esa cámara. Pídaselo al administrador.],
  [_El servicio de streaming no está disponible en el servidor._],
    [Avise al administrador: el servicio de video del servidor está detenido.],
)

== Configuración

Reproducción no tiene una página propia en el panel web. Lo que el
administrador debe tener en cuenta:

- *Permisos.* _Ver grabaciones_ permite buscar y reproducir; _Exportar
  grabaciones_ permite exportar y usar el Centro de descargas. Se asignan en
  los roles; vea #capitulo(<cap-usuarios-y-roles>). Cada usuario puede
  reproducir solo las cámaras de su alcance.
- *Cámaras.* Las cámaras de Reproducción son las de los equipos agregados en
  Fuentes de video; vea #capitulo(<cap-fuentes-de-video>). Una cámara
  deshabilitada no se puede reproducir.
- *Carpetas del puesto.* En cada cliente, el botón de engranaje
  *Configuración* del menú lateral define dónde se guardan los archivos: en la
  sección *Video*, la *Carpeta de grabaciones locales* (cápsulas, y carpeta
  inicial de las exportaciones), y en la sección *Imagen*, la *Carpeta de
  capturas* y el *Formato de captura* (_JPG (liviano)_ o _PNG (sin pérdida)_).
  Si no se cambian, se usan las carpetas `TrueCentral VMS` dentro de Videos e
  Imágenes del usuario de Windows.

#nota[Las búsquedas, reproducciones, capturas, cápsulas y exportaciones quedan
  registradas en la bitácora de auditoría, con el usuario, la cámara y la hora
  de la grabación.]
