#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/LiveView.xaml, src/TrueCentralVms.Client/Views/LiveView.xaml.cs, src/TrueCentralVms.Client/Views/AuxLiveWindow.xaml, src/TrueCentralVms.Client/Views/AuxLiveWindow.xaml.cs, src/TrueCentralVms.Client/Views/LoadingWindow.xaml, src/TrueCentralVms.Client/Views/ZoomDragController.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.LiveViews.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Session.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Locations.cs, src/TrueCentralVms.Client/ViewModels/VideoCellViewModel.cs, src/TrueCentralVms.Client/ViewModels/VideoLayouts.cs, src/TrueCentralVms.Client/ViewModels/DigitalZoom.cs, src/TrueCentralVms.Client/ViewModels/AuxScreenViewModel.cs, src/TrueCentralVms.Client/ViewModels/DeviceTreeNodes.cs, src/TrueCentralVms.Client/ViewModels/LocationTreeNodes.cs, src/TrueCentralVms.Server/wwwroot/live.js, src/TrueCentralVms.Server/Api/LiveViewsApi.cs, src/TrueCentralVms.Server/Api/StreamsApi.cs

= Vista en vivo <cap-vista-en-vivo>

La Vista en Vivo muestra las cámaras en tiempo real en una grilla de cuadros.
A la izquierda está la lista de cámaras y, debajo de ella, el panel PTZ; arriba,
la barra de herramientas con la división de la grilla, las vistas guardadas,
las pantallas auxiliares y la pantalla completa. Este capítulo describe la
Vista en Vivo del cliente de monitoreo y, al final, la del panel web.

#captura-pendiente("Cliente de monitoreo: Vista en Vivo con división de 4 cuadros, tres con video y uno seleccionado (borde azul), la lista de cámaras por equipo y el panel PTZ abierto", alto: 6cm)

== Abrir la Vista en Vivo

#requiere("Ver video en vivo")

Haga clic en el botón *Vista en Vivo* del riel o en la tarjeta *Vista en Vivo*
de la página Inicio. Se agrega la viñeta *Vista en Vivo* en la barra superior
y la grilla parte con la última división que usó en este equipo.

Si pasa a otro módulo, los videos siguen andando por detrás. La #boton("✕")
de la viñeta (*Cerrar Vista en Vivo (detiene los streams)*) cierra el módulo
y detiene todos los videos.

== La lista de cámaras

Bajo el título *CÁMARAS* están todas las cámaras que usted puede ver. Los
botones #boton("Equipo") y #boton("Ubicación") cambian cómo se agrupan:

- *Equipo*: cada grabador o cámara con sus canales.
- *Ubicación*: el árbol de ubicaciones del sistema (sitio, edificio, piso,
  sector, punto). Junto a cada ubicación se indica cuántas cámaras tiene,
  contando las de sus sububicaciones. Las cámaras que todavía no se ubicaron
  quedan en *Sin ubicación*, al final, y las ubicaciones sin cámaras no
  aparecen.

La agrupación elegida se recuerda en este equipo. La lista muestra solo los
canales habilitados y que su alcance le permite ver.

El campo *Buscar canal…* filtra la lista mientras escribe, sin distinguir
mayúsculas ni tildes. Encuentra una cámara por su nombre o por el de su
equipo; en la lista por ubicación, también por el nombre de la ubicación.

#table(
  columns: (1fr, 2fr),
  table.header[En la lista][Significa],
  [Punto verde junto a un equipo], [El equipo está en línea.],
  [Punto rojo junto a un equipo], [El equipo no responde.],
  [◉ verde junto a una cámara], [La cámara tiene señal.],
  [◉ gris junto a una cámara], [La cámara no tiene señal, o su equipo no
    responde.],
  [▶ y nombre en negrita], [La cámara está en algún cuadro. Pase el puntero
    para ver en cuál, por ejemplo «En vivo en cuadro 2, cuadro 5 (pantalla
    auxiliar 1)».],
)

Al seleccionar un cuadro de la grilla, su cámara queda marcada en la lista.

Para dar más espacio a la grilla, haga clic en la franja angosta con una
flecha que hay en el borde derecho de la lista (*Mostrar u ocultar la lista de
dispositivos*). Otro clic la vuelve a mostrar.

== Abrir cámaras

=== Una cámara

- *Doble clic en una cámara* de la lista: se abre en el cuadro seleccionado;
  si no hay ninguno seleccionado, en el primero libre; y si todos están
  ocupados, en el primero. Después la selección pasa al cuadro siguiente, así
  que varios doble clic seguidos llenan la grilla en orden.
- *Arrastrar una cámara* de la lista y soltarla sobre un cuadro: se abre en
  ese cuadro. Mientras arrastra, el cuadro de destino se ilumina en amarillo.
  Si el cuadro tenía otra cámara, la reemplaza.

La cámara se abre con el stream que indica *Stream al abrir un canal* en la
configuración del cliente: con *Automático*, el principal en divisiones de
hasta 4 cuadros y el secundario en las más grandes. Ver
#capitulo(<cap-primeros-pasos>).

=== Un equipo o una ubicación completos

Doble clic en un equipo abre todas sus cámaras de una vez. En la lista por
ubicación, doble clic en una ubicación abre sus cámaras y las de sus
sububicaciones; lo mismo hace *Abrir sus cámaras*, en el menú del clic
derecho sobre la ubicación.

La grilla se adapta a la cantidad de cámaras según la opción *Al abrir un
equipo completo* de la configuración: una cuadrícula a medida, sin cuadros de
sobra (por ejemplo, 5×3 para 15 cámaras), o la división estándar más chica
donde quepan. Se abren hasta 64 cámaras; si hay más, la barra de estado
avisa, por ejemplo, «"NVR Bodega" tiene 80 canales: se abren los primeros 64.»

Mientras se abren, aparece un aviso al centro con el avance («Abriendo canales
de "NVR Bodega"… 8/16») y la ventana no acepta clics.

#consejo[Si la apertura tarda, presione #tecla("Esc"): el aviso se cierra y
  recupera el control, mientras las cámaras terminan de abrirse.]

== Los cuadros de video

Cada cuadro tiene arriba una barra y debajo el video. Un cuadro sin cámara
dice «Cuadro libre».

#captura-pendiente("Cliente de monitoreo: barra de un cuadro con video y grabación en curso; marcar número, nombre, estado, audio, P/S, captura, grabar con su contador y cerrar")

#table(
  columns: (1fr, 2fr),
  table.header[En la barra][Para qué sirve],
  [Número], [La posición del cuadro en la grilla.],
  [Nombre], [El equipo y la cámara, por ejemplo «NVR Bodega · Acceso».],
  [Círculo que gira], [Se está conectando con la cámara.],
  [Texto amarillo], [El estado o el resultado de la última acción (ver la
    tabla siguiente).],
  [Parlante], [Activa o silencia el audio de este cuadro.],
  [P o S], [Stream principal (P, azul) o secundario (S, gris). Un clic cambia
    al otro.],
  [Cámara fotográfica], [Guarda una captura de imagen.],
  [●], [Inicia o detiene una grabación local. Mientras graba se pone rojo y
    muestra el tiempo transcurrido.],
  [#boton("✕")], [Cierra el video de este cuadro y lo deja libre.],
)

Los mensajes más comunes de la barra:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué pasa],
  [«Conectando…»], [El cuadro está abriendo el video.],
  [«No se pudo abrir el video: …»], [La apertura falló. El cuadro reintenta
    solo cada 5 segundos.],
  [«Sin señal — reintentando…»], [El video se cortó (red, equipo apagado o
    reiniciado). El cuadro reintenta solo.],
  [«Reconectando…»], [El cuadro está volviendo a pedir el video.],
  [«No se pudo conectar con el servidor. Verifique la URL y la red.»], [No hay
    conexión con el servidor. El cuadro sigue reintentando.],
  [«El canal está deshabilitado.»], [Un administrador deshabilitó la cámara.],
  [«Ese recurso está fuera de su alcance (las ubicaciones y recursos de sus
    roles).»], [La cámara no está en su alcance.],
)

== Organizar la grilla

=== Elegir la división

Haga clic en el botón que está junto a *División:*, en la barra de
herramientas; muestra el dibujo y la cantidad de cuadros de la división
actual. Se despliega *División de pantalla* con las divisiones agrupadas por
familia. Haga clic en la que quiera.

#table(
  columns: (auto, 1fr),
  table.header[Familia][Divisiones],
  [Uniformes], [1, 4, 9, 16, 25, 36 y 64 cuadros iguales.],
  [Con cuadro principal], [6, 8, 9, 10, 12, 16 y 17 cuadros, con uno grande
    arriba a la izquierda.],
  [En columnas], [2, 3, 5, 6 y 8 cuadros ordenados en columnas.],
  [En filas], [2, 3, 5, 6 y 8 cuadros ordenados en filas.],
  [Combinadas], [4 (A y B), 5, 6, 7 (A y B), 12, 13, 24, 32, 36 y 48 cuadros,
    de distintos tamaños.],
)

Pase el puntero sobre una miniatura para ver su nombre completo, por ejemplo
«6 con principal» o «4 combinada A».

Al cambiar de división, las cámaras de los primeros cuadros se mantienen y las
de los cuadros que sobran se cierran. La división elegida se recuerda para la
próxima vez.

=== Seleccionar un cuadro

Haga clic en la barra de un cuadro o sobre su video: queda con borde azul.
Otro clic en el mismo cuadro lo deja sin seleccionar. El cuadro seleccionado
es donde se abre la próxima cámara y el que maneja el panel PTZ.

=== Mover o intercambiar cámaras

Arrastre un cuadro, desde su barra o desde el video, y suéltelo sobre otro;
el destino se ilumina en amarillo.

- Si el destino tiene video, las dos cámaras se intercambian.
- Si está libre, la cámara se muda y el cuadro de origen queda libre.

El video no se corta: la grabación en curso, el zoom digital y el audio viajan
con la cámara. Los números de los cuadros no cambian, porque indican la
posición. La barra de estado confirma el cambio, por ejemplo «Cuadros 1 y 3
intercambiados: …». También puede arrastrar cuadros entre la ventana principal
y las pantallas auxiliares.

=== Maximizar un cuadro

Doble clic en un cuadro con video (en su barra o en el video) lo muestra solo,
ocupando toda la grilla; los demás siguen andando ocultos. Otro doble clic
vuelve a la grilla.

Si el cuadro estaba en el stream secundario, mientras está maximizado pasa al
principal para verlo con más detalle, y vuelve al secundario al restaurarlo.
Cambiar de división también devuelve la grilla a la normalidad.

=== Pantalla completa

El botón #boton("Pantalla completa"), a la derecha de la barra de
herramientas, deja la grilla ocupando todo el monitor: se ocultan la barra
superior, el riel, la lista de cámaras, la barra de herramientas y la barra
de estado. Presione #tecla("Esc") para volver.

=== Limpiar la grilla

El botón #boton("Limpiar todo") cierra el video de todos los cuadros. Para
cerrar uno solo, use la #boton("✕") de su barra.

== Stream principal y secundario

Cada cámara entrega dos videos: el *principal*, de máxima calidad y más
pesado, y el *secundario*, liviano. El botón P/S de la barra del cuadro cambia
de uno a otro.

El cambio es suave: mientras dice «Cambiando a principal…» o «Cambiando a
secundario…», el cuadro sigue mostrando el video que tenía hasta que el otro
tiene imagen. Si después de dos intentos el otro stream no entrega imagen, el
cuadro se queda con el actual y avisa «El stream destino no entregó imagen
(¿perfil no disponible o enlace del equipo saturado?). Se mantiene el actual.»

#consejo[En grillas grandes use el secundario: cuida el ancho de banda de los
  grabadores y de la red. Para ver un detalle, maximice el cuadro: pasa solo al
  principal.]

== Audio

El botón del parlante de cada cuadro activa su audio. Suena un solo cuadro a
la vez en toda la aplicación, incluidas las pantallas auxiliares: al activar
uno se silencia el que estaba sonando. Los cuadros parten silenciados, con el
volumen que indica la configuración del cliente (apartado *Sonido*).

== Zoom digital

El zoom digital acerca la imagen que ya llegó al cliente: no mueve la cámara ni
cambia el stream.

- *Con la rueda del mouse* sobre el video: acerca o aleja un paso alrededor del
  puntero, hasta 6×. La barra del cuadro indica el nivel, por ejemplo «Zoom
  digital 2,4×». Para volver, gire la rueda hacia atrás hasta «Zoom 1×».
- *Con el modo zoom*: haga clic en #boton("Zoom") en la barra de herramientas;
  el puntero pasa a ser una lupa. Arrastre sobre el video para marcar un
  rectángulo: esa zona pasa a llenar el cuadro, hasta 20×. La rueda acerca y
  aleja sobre el puntero, y el clic derecho vuelve a 1×. Otro clic en
  #boton("Zoom") apaga el modo.

#nota[Con el modo zoom encendido, los clics sobre el video no seleccionan ni
  maximizan cuadros. Al abrir otra cámara en el cuadro, el zoom vuelve a 1×.]

== Control PTZ

#requiere("Mover cámaras PTZ")

El panel PTZ está bajo la lista de cámaras. Su encabezado dice «PTZ —» y el
nombre de la cámara del cuadro seleccionado, o «sin cámara PTZ» si ese cuadro
no tiene una. Si la cámara está fuera de su alcance, el nombre lleva «(fuera
de su alcance)»: puede verla, pero no moverla, y los controles quedan
deshabilitados. El panel parte minimizado; la flecha de su encabezado lo abre
y lo cierra.

#captura-pendiente("Cliente de monitoreo: panel PTZ abierto con una cámara PTZ seleccionada; marcar las flechas, Zoom/Foco/Iris, el número de preset con Ir, Guardar y Borrar, y la velocidad")

=== Mover la cámara

+ Seleccione el cuadro de la cámara PTZ.
+ Si el panel está minimizado, ábralo con la flecha de su encabezado.
+ Mantenga presionada una de las ocho flechas: la cámara se mueve mientras la
  mantiene y se detiene al soltar.

Los botones *Zoom* (#boton("−") y #boton("+")), *Foco* (#boton("Cerca") y
#boton("Lejos")) e *Iris* (#boton("Abrir") y #boton("Cerrar")) funcionan
igual: actúan mientras los mantiene presionados.

*Velocidad* va de 1 (lenta) a 7 (rápida); parte en 4.

=== Presets

Un preset es una posición guardada en la cámara, identificada por un número
de 1 a 300.

+ Escriba el número en *Preset*.
+ Haga clic en:
  - #boton("Ir") para llevar la cámara a esa posición («PTZ: moviéndose al
    preset 3.»);
  - #boton("Guardar") para guardar la posición actual con ese número («PTZ:
    posición actual guardada como preset 3.»);
  - #boton("Borrar") para eliminarlo de la cámara («PTZ: preset 3
    eliminado.»).

#importante[#boton("Guardar") y #boton("Borrar") no piden confirmación:
  #boton("Guardar") reemplaza el preset que tuviera ese número.]

Si una orden falla, la barra de estado muestra el motivo después de «PTZ:»,
por ejemplo «PTZ: Su rol no le permite hacer esto (requiere "Mover
cámaras PTZ").»

=== Con el teclado

Con una cámara PTZ seleccionada:

- las flechas #tecla("↑") #tecla("↓") #tecla("←") #tecla("→") mueven la
  cámara mientras mantiene la tecla;
- #tecla("+") y #tecla("−"), también los del teclado numérico, acercan y alejan;
- con #tecla("Shift") sostenido la cámara se mueve a la velocidad mínima
  (*modo precisión*); el panel muestra la marca *PRECISIÓN*.

El teclado no mueve la cámara mientras escribe en un campo de texto, como el
buscador o el número de preset.

#nota[Si una cámara PTZ no muestra el control PTZ, pida al administrador que
  la marque como PTZ (ver _Configuración_ al final de este capítulo).]

== Capturas de imagen y grabaciones locales

Las capturas y las grabaciones se guardan en este equipo, no en el servidor.
Cada una queda registrada en la bitácora de auditoría con la cámara y el
nombre del archivo.

=== Guardar una captura

Haga clic en la cámara fotográfica de la barra del cuadro. La imagen de ese
instante se guarda en la carpeta de capturas, en JPG o PNG según la
configuración del cliente (apartado *Imagen*). Aparece el aviso «Captura
guardada» con la ruta del archivo; un clic en la ruta abre el Explorador de
Windows con el archivo seleccionado.

=== Grabar un video

+ Haga clic en #boton("●") en la barra del cuadro. Se pone rojo y al lado
  corre el tiempo de grabación.
+ Para terminar, haga clic otra vez en #boton("●"). Aparece el aviso
  «Grabación guardada» con la ruta del archivo.

La grabación se guarda tal como llega de la cámara, sin recomprimir, en la
carpeta de grabaciones locales (configuración del cliente, apartado *Video*).
El nombre del archivo lleva el equipo, la cámara, la fecha y la hora.

La grabación también se detiene y se guarda sola si el video se corta, si
cambia el stream con P/S (también al maximizar un cuadro que estaba en el
secundario), si abre otra cámara en el cuadro o si lo cierra. Si
cierra el cliente o el cuadro desaparece al achicar la división, el archivo
queda guardado pero sin aviso.

Si algo falla, la barra del cuadro muestra «No se pudo guardar la captura: …»
o «No se pudo iniciar la grabación: …».

== Vistas guardadas

#requiere("Ver video en vivo", "Guardar vistas")

Una vista guarda la división de la grilla y qué cámara, con qué stream, había
en cada cuadro. Las vistas quedan en el servidor: puede cargarlas desde
cualquier puesto y también desde el panel web. Para cargar una vista basta
*Ver video en vivo*; para guardarlas, cambiarlas o borrarlas hace falta además
*Guardar vistas*.

El botón #boton("Vistas") está al centro de la barra de herramientas; a su
lado aparece el nombre de la última vista que cargó.

#captura-pendiente("Cliente de monitoreo: desplegable Vistas con dos vistas guardadas, una de ellas compartida por otro usuario, y el campo para guardar la grilla actual")

=== Cargar una vista

+ Haga clic en #boton("Vistas").
+ Haga clic en la vista. Bajo su nombre se indica cuántas cámaras tiene, su
  división y si es compartida, por ejemplo «6 cámara(s) · división 9 ·
  compartida por jperez».

La grilla se arma con el mismo aviso de avance que al abrir un equipo
completo. Si alguna cámara de la vista ya no existe o no está disponible para
usted, su cuadro queda libre y la barra de estado lo dice, por ejemplo «Vista
"Accesos": 5 cámara(s) en pantalla; 1 ya no está(n) en el inventario y su
cuadro quedó libre.»

=== Guardar la grilla como vista

+ Arme la grilla con las cámaras que quiere guardar.
+ Haga clic en #boton("Vistas").
+ Escriba un nombre en *Nombre de la vista…*.
+ Si quiere que la vean todos los usuarios, marque *Compartir con todos los
  puestos*.
+ Haga clic en #boton("Guardar").

Si ya tiene una vista con ese nombre, el cliente pregunta «Ya existe la vista
"…". ¿Reemplazarla con la grilla actual?».

=== Actualizar o eliminar una vista

En las vistas propias aparecen dos íconos a la derecha:

- el lápiz (*Actualizar esta vista con la grilla actual*) reemplaza su
  contenido por lo que hay ahora en la grilla, después de confirmar;
- el basurero (*Eliminar esta vista*) la borra, después de confirmar. No se
  puede deshacer.

Las vistas que compartió otro usuario se pueden cargar, pero solo su dueño o
un administrador puede cambiarlas o borrarlas.

#nota[Una vista compartida muestra a cada usuario solo las cámaras de su
  alcance: los cuadros de las demás quedan libres.]

Mensajes del desplegable:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [«Escriba un nombre para la vista.»], [Escriba el nombre antes de guardar.],
  [«La grilla está vacía: abra las cámaras que quiere guardar.»], [Abra al
    menos una cámara.],
  [«Su rol no le permite hacer esto (requiere "Guardar vistas").»], [Sus roles
    no incluyen *Guardar vistas*: pídalo al administrador.],
)

== Volver a abrir la última sesión

Con la opción *Volver a abrir las cámaras de la última sesión* encendida
(configuración del cliente, apartado *Video*), al ingresar el cliente abre
solo la Vista en Vivo tal como quedó: la división, la cámara y el stream de
cada cuadro, y las pantallas auxiliares en su monitor.

- Lo que hay en pantalla se recuerda a medida que cambia, así que también
  sirve si el cliente se cerró de golpe.
- Es por usuario y por servidor: otra persona que ingrese en este equipo no
  hereda sus cámaras.
- Si el monitor de una pantalla auxiliar ya no está conectado, esa pantalla
  se abre en el primer monitor libre.

Mientras se abre, la barra de estado dice «Volviendo a abrir la última
sesión…» y al terminar «Última sesión restaurada: 8 cámara(s) en pantalla.».
Si alguna cámara ya no está disponible, se agrega cuántas y su cuadro queda
libre.

== Pantallas auxiliares

Una pantalla auxiliar es otra ventana de Vista en Vivo, con su propia grilla,
para llevar a otro monitor. Puede abrir hasta tres.

+ Haga clic en #boton("Pantalla auxiliar") en la barra de herramientas. Se
  abre la ventana *Pantalla auxiliar 1*, maximizada en el primer monitor que
  no tenga ventanas del cliente.
+ Si no partió en el monitor que quería, arrástrela hasta él.
+ Abra cámaras con doble clic en la lista de esa ventana o arrastrándolas a
  sus cuadros.

Cada pantalla auxiliar tiene su lista de cámaras (con la misma agrupación y el
mismo filtro del buscador de la ventana principal), su división,
#boton("Zoom"), #boton("Limpiar todo") y #boton("Pantalla completa"), que
cubre su monitor (#tecla("Esc") para salir). Sus cuadros tienen la misma barra
que los de la ventana principal, y doble clic en un equipo abre todas sus
cámaras en esa pantalla. Puede arrastrar un cuadro de una ventana a otra sin
que se corte el video.

#nota[Las pantallas auxiliares no tienen panel PTZ, vistas guardadas ni
  buscador. Para mover una cámara PTZ, ábrala en la ventana principal.]

Para cerrar una pantalla auxiliar, use la #boton("✕") de su ventana (*Cerrar
esta pantalla (detiene sus streams)*). Al cerrar el cliente o la sesión se
cierran todas. Con tres abiertas, el botón avisa «Ya hay 3 pantallas
auxiliares abiertas (el máximo).»

== Órdenes sobre una ubicación

#requiere("Órdenes por ubicación")

En la lista por ubicación, el clic derecho sobre una ubicación ofrece, además
de *Abrir sus cámaras*, órdenes para todas las áreas de alarma de esa
ubicación y de sus sububicaciones:

+ Haga clic derecho en la ubicación y elija *Armar total sus áreas de
  alarma…*, *Armar parcial sus áreas de alarma…* o *Desarmar sus áreas de
  alarma…*.
+ El cliente muestra cuántas áreas se tocarían y cuáles, y pregunta
  «¿Continuar?».
+ Haga clic en #boton("Sí"). El resultado aparece en la barra de estado; si
  algún panel rechazó la orden, una ventana lista las áreas que fallaron y el
  motivo.

Si la ubicación no tiene áreas de alarma, el cliente lo informa y no envía
nada. Si la ubicación está fuera de su alcance, las órdenes aparecen
deshabilitadas.#modulo("alarms")[ El armado y desarmado se explican en
#capitulo(<cap-paneles-de-alarma>).]

#modulo("speakers")[
== Parlantes IP

Si hay parlantes IP configurados, entre la lista de cámaras y el panel PTZ
aparece el panel *PARLANTES*, para hablar, reproducir sonidos y leer textos en
voz alta por los parlantes. Su uso se explica en #capitulo(<cap-parlantes-ip>).
]

== Atajos de teclado y mouse

#table(
  columns: (auto, 1fr),
  table.header[Tecla o gesto][Acción],
  [Doble clic en una cámara de la lista], [La abre en el cuadro seleccionado o
    en el primero libre.],
  [Doble clic en un equipo o una ubicación], [Abre todas sus cámaras.],
  [Arrastrar una cámara de la lista a un cuadro], [La abre en ese cuadro.],
  [Arrastrar un cuadro sobre otro], [Intercambia o muda la cámara.],
  [Clic en la barra o el video de un cuadro], [Lo selecciona o lo deja sin
    seleccionar.],
  [Doble clic en un cuadro], [Lo maximiza o lo devuelve a la grilla.],
  [Rueda del mouse sobre el video], [Zoom digital alrededor del puntero.],
  [Clic derecho sobre el video (modo zoom)], [Vuelve el zoom digital a 1×.],
  [Clic derecho en una ubicación], [Abre el menú de la ubicación.],
  [#tecla("↑") #tecla("↓") #tecla("←") #tecla("→")], [Mueven la cámara PTZ
    del cuadro seleccionado mientras mantiene la tecla.],
  [#tecla("+") #tecla("−")], [Acercan o alejan la cámara PTZ.],
  [#tecla("Shift") sostenido], [Modo precisión: el PTZ por teclado se mueve a
    la velocidad mínima.],
  [#tecla("Esc")], [Sale de la pantalla completa. Durante la apertura de un
    equipo completo, cierra el aviso y devuelve el control.],
)

== Vista en vivo desde el navegador

#requiere("Ver video en vivo")

El panel web tiene su propia Vista en vivo, en
#menu("Aplicaciones", "Vista en vivo"), para ver cámaras desde cualquier equipo sin instalar nada. Funciona
como la del cliente de monitoreo; para monitorear muchas cámaras a la vez,
use el cliente.

#captura("web-vista-en-vivo.png", pie: [Vista en vivo del panel web con una vista guardada y los ajustes abiertos.])

=== Lo mismo que en el cliente

- La lista de cámaras por #boton("Equipo") o #boton("Ubicación"), con el
  buscador *Buscar canal…*. Cada equipo y cada ubicación se pliega con la
  flecha de su izquierda, y el navegador lo recuerda.
- Doble clic en una cámara, un equipo o una ubicación; el clic derecho en una
  ubicación, con *Abrir sus cámaras* y las órdenes a sus áreas de alarma.
- Arrastrar una cámara a un cuadro, y un cuadro sobre otro (tómelo de su
  barra).
- La barra de cada cuadro: audio, P/S, captura, grabación y
  #boton("✕") (*Cerrar el video de este cuadro*).
- Doble clic en el video para maximizar, #boton("Limpiar todo"),
  #boton("Vistas"), #boton("Pantalla auxiliar") y #boton("Pantalla completa").
- El panel PTZ, con sus atajos de teclado y el modo precisión con
  #tecla("Shift").#modulo("speakers")[ También el panel de parlantes, con el
  permiso *Usar parlantes*.]

=== Diferencias con el cliente

#table(
  columns: (1fr, 2fr),
  table.header[Tema][En el navegador],
  [Cantidad de cuadros], [Hasta 16: el selector ofrece solo las divisiones de
    hasta 16 cuadros. Un equipo con más cámaras abre las primeras 16 y avisa,
    por ejemplo, «"NVR Bodega" tiene 32 canales: el panel web abre hasta 16
    (para verlos todos, use el cliente de escritorio).» Una vista guardada más
    grande se acomoda en una división de hasta 16 cuadros y el resto se omite,
    con aviso.],
  [Zoom digital], [La rueda del mouse acerca siempre, hasta 8×. Con la imagen
    acercada, arrastre el video para recorrerla. El clic derecho vuelve a 1×
    en cualquier momento. La barra del cuadro muestra el nivel, por ejemplo
    «2,0×».],
  [Capturas y grabaciones], [Se descargan a la carpeta de descargas del
    navegador. El nombre lleva el equipo, la cámara, la fecha y la hora. Las
    grabaciones quedan en MP4 o WebM, según el navegador.],
  [Audio], [Si la cámara no envía audio, el botón lo indica: «Esta cámara no
    envía audio».],
  [Pantalla completa], [Es la del navegador: #tecla("Esc") sale.],
  [Ajustes], [Se cambian con el engranaje de la barra y quedan guardados en
    ese navegador.],
)

Si sus roles no incluyen *Mover cámaras PTZ*, el panel PTZ lo dice: «Sus roles
no incluyen mover cámaras PTZ.».

=== Ajustes de este navegador

El engranaje, a la derecha de la barra de herramientas, abre *Ajustes de este
navegador*. Los cambios se guardan apenas los hace:

#table(
  columns: (1fr, 2fr),
  table.header[Opción][Qué hace],
  [*Stream al abrir una cámara*], [*Automático (principal hasta 4 cuadros)*,
    *Siempre principal* o *Siempre secundario*.],
  [*Formato de las capturas*], [JPG o PNG.],
  [*Al abrir un equipo completo, grilla a la medida de sus canales*],
  [Igual que en el cliente: cuadrícula a medida o división estándar.],
  [*Estirar el video al tamaño del cuadro (sin mantener la proporción)*],
  [Llena cada cuadro, deformando levemente la imagen.],
  [*Volver a abrir las cámaras de la última sesión al entrar*], [Al abrir la
    página, reabre lo que había en la grilla la última vez en este navegador.
    Solo en la ventana principal.],
)

=== Vistas guardadas en el navegador

Son las mismas vistas del cliente de monitoreo. Sin el permiso *Guardar
vistas*, el desplegable solo permite cargarlas. Para guardar, escriba el
nombre y haga clic en #boton("Guardar") o presione #tecla("Enter").

A diferencia del cliente, si ya tiene una vista con ese nombre el panel no
ofrece reemplazarla: muestra «Ya tiene una vista guardada con ese nombre.».
Para cambiar una vista existente, use su lápiz (*Actualizar esta vista con la
grilla actual*). Al eliminar una vista compartida, el panel advierte que
desaparece para todos los puestos.

=== Pantallas auxiliares en el navegador

+ Haga clic en #boton("Pantalla auxiliar"). Se abre una ventana del navegador
  con solo la grilla, sin el menú del panel.
+ Llévela al monitor que quiera y use #boton("Pantalla completa").

Si el navegador tiene permiso para administrar ventanas, la pantalla
auxiliar se lleva sola a un monitor libre. Si aparece «El navegador bloqueó la
ventana emergente: permita las ventanas emergentes de este sitio.», permita
las ventanas emergentes del panel en el navegador y vuelva a intentarlo. Puede
arrastrar cuadros entre la ventana principal y las auxiliares. Las pantallas
auxiliares no tienen panel PTZ ni panel de parlantes.

=== Navegadores compatibles

Use Chrome, Edge o Firefox actualizados. Si el navegador no admite el video en
vivo, la página muestra «Este navegador no admite WebRTC: use Chrome, Edge o
Firefox actualizados para ver video en vivo.»

Algunos navegadores no pueden mostrar cámaras configuradas en H.265. El cuadro
lo dice y no reintenta: «Este navegador no puede reproducir el formato de
video de la cámara por WebRTC (típicamente H.265 sin decodificación por
hardware). Use Chrome actualizado, o configure el stream de la cámara en
H.264.»

#importante[Para hablar por los parlantes desde el navegador, el panel debe
  abrirse por HTTPS o desde el propio servidor (`http://localhost`). Si no, el
  botón *Mantener para hablar* queda deshabilitado.]

=== Si una cámara no se ve en el navegador

Mientras no hay imagen, el motivo aparece sobre el cuadro. El cuadro reintenta
solo, cada vez más espaciado, hasta cada 30 segundos.

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [«El video no llegó al navegador: revise que el puerto WebRTC del servidor
    (8660 UDP/TCP) esté abierto en el firewall y alcanzable desde este
    equipo.»],
  [Un firewall entre su equipo y el servidor bloquea el video. Avise al
    administrador.],
  [«La cámara no está entregando video (sin señal, equipo desconectado o no
    respondió a tiempo).»],
  [Revise la cámara en la lista. El cuadro reintenta solo.],
  [«La cámara no entregó imagen a tiempo.»], [El cuadro reintenta solo.],
  [«Señal perdida. Reconectando…»], [El video se cortó; el cuadro reconecta
    solo.],
  [«Sin video» en la barra], [El problema no se arregla reintentando (permiso,
    alcance, cámara deshabilitada o formato). El motivo está sobre el cuadro.],
  [«La vista en vivo del panel web está deshabilitada en este servidor
    (Streaming:WebRtcIcePort = 0).»],
  [El administrador desactivó la vista en vivo del panel web. Use el cliente
    de monitoreo.],
)

== Configuración

La Vista en Vivo no tiene una página de configuración propia. Lo que ve y
puede hacer cada operador depende de lo siguiente:

#table(
  columns: (1fr, 2fr),
  table.header[Qué][Dónde se configura],
  [Quién ve video, mueve cámaras PTZ, guarda vistas y da órdenes por
    ubicación],
  [Los permisos *Ver video en vivo*, *Mover cámaras PTZ*, *Guardar vistas* y
    *Órdenes por ubicación* de sus roles. Ver
    #capitulo(<cap-usuarios-y-roles>).],
  [Qué cámaras aparecen en la lista],
  [Solo los canales con la casilla *Habilitado*, en la ventana
    #boton("Canales") de cada equipo de
    #menu("Dispositivos", "Fuentes de video"). Ver
    #capitulo(<cap-fuentes-de-video>).],
  [Qué cámaras tienen control PTZ],
  [La casilla *PTZ / lente* de la misma ventana. Se marca sola cuando el
    equipo informa que la cámara es PTZ; márquela a mano, por ejemplo, en
    domos conectados por coaxial a un grabador.],
  [La lista por ubicación y el alcance de cada usuario],
  [El árbol de #menu("Recursos") y el alcance de los roles. Ver
    #capitulo(<cap-recursos-y-ubicaciones>).],
)

=== Sesiones de video

#requiere("Sesiones")

En #menu("Streaming", "Sesiones"), la tabla *Sesiones de video activas* muestra
quién está viendo qué cámara, con qué perfil, desde qué IP y hace cuánto. El
botón #boton("Expulsar") corta una sesión; el usuario puede volver a abrir la
cámara.

=== Puerto de la vista en vivo web

El video del panel web llega desde el servidor por el puerto 8660, en UDP y
TCP. El instalador lo abre en el firewall de Windows del servidor; si entre
los equipos de los operadores y el servidor hay otro firewall, también debe
permitirlo. Si no, los cuadros del navegador muestran «El video no llegó al
navegador…».
