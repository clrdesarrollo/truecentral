#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/LiveView.xaml, src/TrueCentralVms.Client/Views/LiveView.xaml.cs, src/TrueCentralVms.Client/Views/AuxLiveWindow.xaml, src/TrueCentralVms.Client/Views/AuxLiveWindow.xaml.cs, src/TrueCentralVms.Client/Views/LoadingWindow.xaml, src/TrueCentralVms.Client/Views/ZoomDragController.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.LiveViews.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Session.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Locations.cs, src/TrueCentralVms.Client/ViewModels/VideoCellViewModel.cs, src/TrueCentralVms.Client/ViewModels/VideoLayouts.cs, src/TrueCentralVms.Client/ViewModels/DigitalZoom.cs, src/TrueCentralVms.Client/ViewModels/AuxScreenViewModel.cs, src/TrueCentralVms.Client/ViewModels/DeviceTreeNodes.cs, src/TrueCentralVms.Client/ViewModels/LocationTreeNodes.cs, src/TrueCentralVms.Server/wwwroot/live.js, src/TrueCentralVms.Server/Api/LiveViewsApi.cs, src/TrueCentralVms.Server/Api/StreamsApi.cs, src/TrueCentralVms.Client/Views/CommandCenterTemplates.xaml, src/TrueCentralVms.Client/Views/WebCellView.xaml, src/TrueCentralVms.Client/Views/WebUrlWindow.xaml, src/TrueCentralVms.Client/ViewModels/MainViewModel.Resources.cs, src/TrueCentralVms.Client/ViewModels/ResourceNodes.cs, src/TrueCentralVms.Client/ViewModels/CellPanels.cs, src/TrueCentralVms.Client/ViewModels/CellPanels.Resources.cs, src/TrueCentralVms.Client/ViewModels/CellPanels.Cards.cs, src/TrueCentralVms.Client/ViewModels/CellPanels.Web.cs, src/TrueCentralVms.Server/wwwroot/boards.js, src/TrueCentralVms.Client/Views/MainWindow.Tabs.cs, src/TrueCentralVms.Client/Views/DetachedTabWindow.xaml, src/TrueCentralVms.Client/Views/DetachedTabWindow.xaml.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Tabs.cs, src/TrueCentralVms.Client/Views/CardSourcesWindow.xaml, src/TrueCentralVms.Client/Views/CardSourcesWindow.xaml.cs, src/TrueCentralVms.Server/Api/LiveViewsApi.cs

= Centro de mando <cap-vista-en-vivo>

El Centro de mando reúne en una misma grilla de cuadros las cámaras en tiempo
real y los demás recursos del sistema: puertas, alarmas, cercos, citófonos,
parlantes y módulos de E/S, además de tarjetas de reporte y páginas web. A la
izquierda está la lista de recursos y, debajo de ella, el panel PTZ; arriba,
la barra de herramientas con la división de la grilla, los tableros y la
pantalla completa. El Centro de mando completo se puede llevar a su propia
ventana, por ejemplo en otro monitor. Este capítulo describe el Centro de mando
del cliente de monitoreo y, al final, la Vista en vivo del panel web, que
muestra solo cámaras.

#captura-pendiente("Cliente de monitoreo: Centro de mando con un tablero cargado que mezcla cámaras, una puerta, un panel de alarma, la tarjeta Estado de los equipos y una página web; la lista RECURSOS abierta en una ubicación con cámaras y recursos")

== Abrir el Centro de mando

#requiere("Ver video en vivo")

Haga clic en el botón *Centro de mando* del riel o en la tarjeta *Centro de
mando* de la página Inicio. Se agrega la viñeta *Centro de mando* en la barra
superior y la grilla parte con la última división que usó en este equipo.

Si pasa a otro módulo, los videos y los cuadros siguen andando por detrás. La
#boton("✕") de la viñeta (*Cerrar el Centro de mando (detiene los streams)*)
cierra el módulo y detiene todos los videos. Para verlo en otro monitor, saque
su viñeta de la barra: ver _Llevar el Centro de mando a otro monitor_, más
abajo.

== La lista de recursos

Bajo el título *RECURSOS* está el árbol de ubicaciones del sistema (sitio,
edificio, piso, sector, punto). Cada ubicación muestra sus cámaras y los demás
recursos que usted puede ver: puertas, áreas de alarma, cercos, citófonos,
parlantes y módulos de E/S. Junto a cada ubicación se indica cuántos recursos
tiene, contando los de sus sububicaciones; las ubicaciones vacías no aparecen.

Lo que todavía nadie ubicó queda en *Sin ubicación*, agrupado por el equipo
del que sale (por ejemplo, las cámaras de un grabador o las puertas de una
controladora). Para ordenarlo, ubique los equipos en #menu("Recursos") del
panel web (ver #capitulo(<cap-recursos-y-ubicaciones>)).

Al final de la lista van tres grupos que no son de una ubicación:

- *Tarjetas*: reportes en vivo para poner en un cuadro (ver _Tarjetas de
  reporte_ más abajo).
- *Grupos de parlantes*: los que armó el administrador#modulo("speakers")[ (ver
  #capitulo(<cap-parlantes-ip>))]. Aparece si hay alguno y sus roles incluyen
  *Usar parlantes*.
- *Páginas web*: las de la lista del servidor, las suyas de _Mis páginas_ (con
  una estrella) y, con el permiso *Abrir cualquier página web*, *Otra
  dirección…*.

La lista muestra solo lo habilitado, lo que su alcance le permite ver y los
tipos de recurso que sus roles le dejan ver u operar: por ejemplo, las puertas
aparecen si sus roles incluyen *Abrir y cerrar puertas* o *Ver control de
acceso*.

El campo *Buscar…* filtra la lista mientras escribe, sin distinguir mayúsculas
ni tildes. Encuentra una cámara o un recurso por su nombre, por el de su equipo
o por el de su ubicación; en los grupos del final, también por su descripción o
por el nombre del grupo. Pase el puntero sobre un recurso para ver su detalle y
su estado.

#table(
  columns: (1fr, 2fr),
  table.header[En la lista][Significa],
  [◉ verde junto a una cámara], [La cámara tiene señal.],
  [◉ gris junto a una cámara], [La cámara no tiene señal, o su equipo no
    responde.],
  [▶ y nombre en negrita], [La cámara está en algún cuadro. Pase el puntero
    para ver en cuál, por ejemplo «En vivo en cuadro 2, cuadro 5».],
  [Ícono verde junto a un recurso], [El recurso está en línea y en estado
    normal.],
  [Ícono naranja o rojo junto a un recurso], [El recurso pide atención (por
    ejemplo, una puerta abierta) o está en alarma.],
  [Ícono gris junto a un recurso], [Sin conexión o estado desconocido.],
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

=== Una ubicación o un equipo completos

Doble clic en una ubicación abre de una vez sus cámaras y las de sus
sububicaciones (los demás recursos no se abren solos); lo mismo hace *Abrir
sus cámaras*, en el menú del clic derecho sobre la ubicación. Dentro de *Sin
ubicación*, doble clic en un equipo abre todas sus cámaras.

La grilla se adapta a la cantidad de cámaras según la opción *Al abrir un
equipo completo* de la configuración: una cuadrícula a medida, sin cuadros de
sobra (por ejemplo, 5×3 para 15 cámaras), o la división estándar más chica
donde quepan. Se abren hasta 64 cámaras; si hay más, la barra de estado
avisa, por ejemplo, «"NVR Bodega" tiene 80 canales: se abren los primeros 64.»

Mientras se abren, aparece un aviso al centro con el avance («Abriendo canales
de "NVR Bodega"… 8/16») y la ventana no acepta clics.

#consejo[Si la apertura tarda, presione #tecla("Esc"): el aviso se cierra y
  recupera el control, mientras las cámaras terminan de abrirse.]

== Abrir recursos, tarjetas y páginas web

Un recurso que no es cámara se abre igual que una cámara: con doble clic en la
lista (en el cuadro seleccionado o en el primero libre) o arrastrándolo hasta
un cuadro. En vez de video, el cuadro muestra el estado del recurso y los
botones para operarlo, y se actualiza solo. La barra de estado lo confirma, por
ejemplo «"Puerta principal" en el cuadro 3.».

Estos cuadros se comportan como los de video: se mueven o se intercambian
arrastrándolos desde su barra, se maximizan con doble clic en su barra, se cierran con la #boton("✕") de
su barra y se guardan en los tableros y en la última sesión. Lo que se abre en
un cuadro ocupado reemplaza lo que tenía. Los botones de audio, P/S, captura y
grabación aparecen solo en los cuadros con video.

=== Qué muestra cada cuadro

#table(
  columns: (1fr, 2fr),
  table.header[Recurso][En el cuadro],
  [Puerta], [Su estado (*Abierta*, *Cerrada*, *Siempre abierta*, *Siempre
    cerrada*, *Equipo sin conexión*…), el equipo y la ubicación, los botones
    #boton("Abrir"), #boton("Cerrar"), #boton("Siempre abierta") y
    #boton("Siempre cerrada"), y *ÚLTIMOS PASOS*: la hora, el resultado
    (*Autorizado*, *Rechazado*…) y quién pasó.],
  [Área de alarma], [El panel completo, con cada una de sus áreas y los
    botones #boton("Armar total"), #boton("Armar parcial"),
    #boton("Desarmar") y #boton("Silenciar").],
  [Cerco eléctrico], [Su estado y #boton("Armar"), #boton("Desarmar") y
    #boton("Silenciar sirena").],
  [Citófono], [Su estado y la llamada en curso, con
    #boton("Contestar / ver el frente") (abre la ventana de la llamada) y
    #boton("Abrir la puerta").],
  [Parlante], [#boton("Hablar"), que cambia a #boton("Dejar de hablar") mientras
    habla. Usa el micrófono elegido en el panel de parlantes.],
  [Grupo de parlantes], [#boton("Hablar al grupo"), #boton("Detener"), un
    sonido del servidor con #boton("Reproducir") y la lista *PARLANTES* con el
    estado de cada uno. Ver _Hablarle a un grupo de parlantes_ más abajo.],
  [Módulo de E/S], [*SALIDAS*, cada una con #boton("Encender"),
    #boton("Apagar") y #boton("Pulso"), y *ENTRADAS*, cada una *Activa* o
    *Inactiva*.],
)

Si sus roles no le permiten operar el recurso, o el recurso está fuera de su
alcance, los botones quedan deshabilitados y el cuadro dice por qué; por
ejemplo, «Sus roles no incluyen operar puertas.». Si el recurso se borró o
salió de su alcance, el cuadro lo avisa, por ejemplo «La puerta ya no existe o
está fuera de su alcance.».

#nota[Las órdenes desde un cuadro valen lo mismo que desde el módulo de cada
  equipo: piden los mismos permisos y quedan en la bitácora de auditoría.]

=== Hablarle a un grupo de parlantes

#requiere("Usar parlantes")

+ Arrastre el grupo desde *Grupos de parlantes*, al final de la lista, hasta un
  cuadro. El cuadro muestra cuántos parlantes tiene y cuántos están en línea,
  por ejemplo «4 parlante(s) · 3 en línea».
+ Haga clic en #boton("Hablar al grupo") y hable por el micrófono elegido en el
  panel de parlantes. Se habla a los parlantes del grupo que están en línea y a
  su alcance; si alguno estaba sin conexión, el cuadro lo dice, por ejemplo
  «Hablando. Sin conexión, se omitió: Bodega.».
+ Haga clic en #boton("Dejar de hablar") para terminar.

Para hacer sonar un aviso grabado, elija el sonido y haga clic en
#boton("Reproducir"): suena en todo el grupo a la vez. #boton("Detener") corta
lo que esté sonando en los parlantes del grupo.

=== Tarjetas de reporte

Las tarjetas están en el grupo *Tarjetas*, al final de la lista. Muestran un
resumen en vivo, y la barra del cuadro lleva el nombre de la tarjeta desde que
se abre:

#table(
  columns: (1fr, 2fr),
  table.header[Tarjeta][Qué muestra],
  [*Estado de los equipos*], [Primero, la lista de tipos de equipo con cuántos
    están en línea de cuántos hay, por ejemplo «12 / 14» junto a *Cámaras*
    (cámaras, grabadores y cámaras IP, control de acceso, paneles de alarma,
    cercos, citófonos, parlantes y módulos de E/S; solo los tipos que hay).
    Debajo, el resumen: «Todos los equipos en línea» o «2 equipo(s) sin
    conexión», y la hora de la última lectura, por ejemplo «Actualizado
    14:32:05». Bajo *SIN CONEXIÓN*, cada equipo que no responde con su tipo,
    su detalle y desde cuándo («desde 29-10 08:15»). Se actualiza cada 15
    segundos.],
  [*Servidor* (en el cuadro, «Estado del servidor»)], [La versión y hace
    cuánto está encendido; el tráfico de red (*THROUGHPUT*: entrada, salida,
    el pico de los últimos 5 minutos y cuánto del enlace se usa); el uso de
    *CPU*, *MEMORIA* y *DISCO*; *CLIENTES CONECTADOS*: cuántos hay ahora, los
    de escritorio (con los puestos que permite la licencia) y los del panel
    web, por ejemplo «3 de escritorio (de 5 puestos) · 1 en el panel web»; y
    cuántos servicios están funcionando, con los detenidos o con problemas. Se
    actualiza cada 5 segundos.],
  [*Últimas alertas*], [Las alertas de las automatizaciones, con la elegida
    arriba (su foto y el botón para confirmarla) y la lista abajo. Ver _La
    tarjeta Últimas alertas_. Pide el permiso *Atender alertas*.],
  [*Últimas patentes*], [Los reconocimientos de patente en vivo; al elegir uno
    se ven la escena y el recorte de la patente. Con el engranaje de la barra
    del cuadro se eligen las cámaras (ver _Elegir las cámaras de Últimas
    patentes_). Pide el permiso *Ver patentes*.],
  [*Últimos accesos*], [Los pasos por las puertas, en vivo, con la foto del
    último que pasó; por omisión, sin los eventos del sistema. Con el engranaje
    de la barra del cuadro se eligen los equipos de acceso (ver _Elegir los
    equipos de Últimos accesos_). Ver _La tarjeta Últimos accesos_. Pide *Ver
    control de acceso*.],
  [*Cuadro vigía*], [No es un resumen: es un cuadro que cambia solo a la
    cámara del último evento (alarmas, accesos denegados, analíticas, avisos de
    las automatizaciones…). Ver _El Cuadro vigía_.],
)

=== La tarjeta Últimas alertas

#requiere("Atender alertas")

La tarjeta muestra las últimas 12 alertas de las
automatizaciones#modulo("alarm_screens")[, con el mismo diseño de las
pantallas de alarma de las casetas (ver #capitulo(<cap-pantallas-de-alarma>))].

#captura-pendiente("Cliente de monitoreo: cuadro con la tarjeta Últimas alertas, una alerta CRÍTICA pendiente elegida con foto, QUÉ HACER, CONFIRMAR y Ver alarma, y la lista con alertas pendientes y confirmadas")

Arriba está la alerta elegida:

- su prioridad (*CRÍTICA*, *ADVERTENCIA* o *INFORMATIVA*) y, en rojo, cuántas
  quedan por confirmar, por ejemplo «2 SIN CONFIRMAR»;
- el título; de dónde viene (la ubicación del recurso o, si no tiene, la
  automatización), con la fecha y la hora; y el mensaje;
- *QUÉ HACER*: las consignas de la ficha del recurso, si las tiene;
- la foto que tomó la automatización, o «Esta alarma no trae imagen»;
- #boton("CONFIRMAR"), mientras está pendiente. Si ya se atendió, en su lugar
  dice quién y cuándo, por ejemplo «Confirmada por operador1 a las 14:32:10»;
- #boton("Ver alarma"), que abre la ventana de alarma con las cámaras
  vinculadas en vivo#modulo("automation")[ (ver
  #capitulo(<cap-centro-de-eventos>))].

Abajo está la lista. Cada alerta lleva una franja de color según su prioridad
(roja la crítica, naranja la advertencia y amarilla la informativa), el
título, de dónde viene, la hora y la marca *SIN CONFIRMAR* o «✓ confirmada».
Las ya atendidas se ven más tenues. Haga clic en una para verla arriba.

Al abrirse, la tarjeta elige la pendiente más reciente (o la más reciente, si
no hay pendientes) y, cuando llega una alerta nueva, pasa a ella, salvo que
usted esté mirando una más antigua. Sin alertas, muestra «Sin alarmas
pendientes» y «Esperando nuevos eventos».

El interruptor *Solo sin confirmar*, sobre la lista, deja en ella solo las
alertas pendientes: al confirmar una, sale de la lista y queda elegida la
siguiente. El cuadro lo recuerda: un tablero o la última sesión vuelven a abrir
la tarjeta con el interruptor como quedó.

#boton("CONFIRMAR") vale lo mismo que darse por enterado en la ventana de
alarma: la alerta queda confirmada a su nombre en el Centro de eventos y deja
de estar pendiente en todos los puestos.

=== La tarjeta Últimos accesos

#requiere("Ver control de acceso")

La tarjeta tiene el mismo diseño que *Últimas alertas*: arriba, el último paso
de una persona del padrón; abajo, los últimos 12 pasos por las puertas.

#captura-pendiente("Cliente de monitoreo: cuadro con la tarjeta Últimos accesos, una persona AUTORIZADO con la foto del momento (Captura del terminal) al lado de su foto del padrón, la puerta, la hora, la credencial y Ver en vivo, y la lista con pasos, el ícono de cámara en los que tienen foto, un botón de salida atenuado y el interruptor Eventos del sistema apagado")

Arriba se ve:

- el resultado (*AUTORIZADO* en verde, *RECHAZADO* en rojo) y el nombre de la
  persona;
- la puerta y el equipo, con la fecha y la hora;
- su área, su cargo y con qué se identificó, por ejemplo «Tarjeta 3558822549»,
  «Rostro» o «Huella»;
- la foto del momento del paso y, al lado, su foto del padrón (o «Sin foto en
  el padrón» si no tiene), para comparar de un vistazo quién pasó con quién
  dice ser (ver _La foto del momento_);
- #boton("Ver en vivo"), que abre las cámaras de esa puerta en la ventana de
  verificación, sin tocar la grilla. Si la puerta no tiene cámaras, la tarjeta
  lo dice.

En la lista, cada evento lleva una franja de color según el resultado (verde el
autorizado, roja el rechazado o la alarma y gris lo demás). Los eventos que no
son el paso de una persona del padrón, como una salida con el botón, se ven más
tenues. Haga clic en uno para verlo arriba.

Por omisión, la lista muestra solo lo que sirve para vigilar: los accesos
autorizados y rechazados, las alarmas y las aperturas con el botón de salida, a
distancia o con la clave de la puerta. El interruptor *Eventos del sistema*,
sobre la lista, suma lo demás: la puesta en hora (si otro sistema, como
HikCentral, sigue conectado a los terminales, la hace cada minuto en cada uno),
los avisos del equipo, como el armado o la batería, y lo que informan la
cerradura y el sensor de la puerta en cada paso. El cuadro lo recuerda: un
tablero o la última sesión vuelven a abrir la tarjeta con el interruptor como
quedó.

Cuando pasa alguien, la tarjeta lo muestra arriba, salvo que usted esté mirando
otro evento. Si los eventos más nuevos son todos del equipo, el último que pasó
queda igual arriba y al pie de la lista.

=== La foto del momento

El servidor guarda una foto de cada paso (no de los eventos del sistema), de
una de estas dos fuentes:

- *La captura del terminal*: la que toma el terminal facial al leer la
  credencial. Es la del instante exacto. El terminal tiene que estar
  configurado para guardar las fotos de autenticación; al que no las guarda se
  le deja de pedir por una hora.
- *La cámara de la puerta*: la del propio terminal o, si no tiene, la primera
  cámara de la ficha de la puerta (ver
  #capitulo(<cap-recursos-y-ubicaciones>)). Se toma solo si el paso llegó hace
  pocos segundos, porque más tarde la foto ya no muestra a quien pasó. El pie
  lo dice, por ejemplo «Cámara Garita · 4 s después del paso».

La foto llega un poco después que el paso: aparece sola cuando está lista. En
la lista, los pasos con foto llevan el ícono de una cámara. Las fotos se
guardan cifradas en el servidor y se borran a los 30 días.

=== Elegir las cámaras de Últimas patentes

Por omisión, la tarjeta *Últimas patentes* muestra las lecturas de todas las
cámaras con reconocimiento de patentes que usted puede ver. Para que muestre
solo algunas, por ejemplo las del acceso que vigila ese puesto:

+ Haga clic en el engranaje de la barra del cuadro (*Elegir de qué cámaras
  ANPR se reciben los reconocimientos*). Se abre *Cámaras de «Últimas
  patentes»*.
+ Elija *Solo de las cámaras marcadas* y marque las cámaras. Bajo cada una se
  indica su última lectura, «Sin lecturas todavía» o «Apagada como fuente de
  patentes».
+ Haga clic en #boton("Aplicar").

La barra del cuadro lo indica, por ejemplo «Últimas patentes · 2 cámaras». Si
entre las lecturas más recientes no hay ninguna de esas cámaras, la tarjeta
dice «Sin lecturas recientes de las cámaras elegidas.». Para volver a verlas todas,
elija *De todas las cámaras ANPR*. Lo elegido queda con el cuadro: se guarda en
la última sesión y en los tableros.

=== Elegir los equipos de Últimos accesos

Por omisión, la tarjeta *Últimos accesos* muestra los pasos por las puertas de
todos los equipos de acceso que usted puede ver. Para que muestre solo algunos,
por ejemplo los de la entrada que vigila ese puesto:

+ Haga clic en el engranaje de la barra del cuadro (*Elegir de qué equipos de
  acceso se muestran los pasos*). Se abre *Equipos de «Últimos accesos»*.
+ Elija *Solo de los equipos marcados* y marque los equipos. El punto verde
  indica que el equipo está en línea; bajo cada uno se indica su puerta (o
  cuántas tiene) y su ubicación.
+ Haga clic en #boton("Aplicar").

La barra del cuadro lo indica, por ejemplo «Últimos accesos · 2 equipos». El
último que pasó, arriba, también es de esos equipos. Para volver a verlos todos,
elija *De todos los equipos de acceso*. Lo elegido queda con el cuadro: se
guarda en la última sesión y en los tableros.

Si la lista dice «No hay cámaras ANPR a su alcance: se configuran en el panel
web (Fuentes de video → ANPR).», no hay cámaras con esa función o ninguna está
dentro de su alcance#modulo("anpr")[ (ver
#capitulo(<cap-reconocimiento-de-patentes>))].

=== Páginas web en un cuadro

Un cuadro también puede mostrar una página web: un mapa, un sistema externo, un
sitio de tránsito…

- Las del grupo *Páginas web* las agrega el administrador en el panel web y
  las puede abrir cualquiera con *Ver video en vivo*.
- Para abrir otra, elija *Otra dirección…* (pide *Abrir cualquier página
  web*). En la ventana *Otra página web*, escriba la *Dirección de la página*
  y, si quiere, el *Nombre en la barra del cuadro (opcional)*; si lo deja
  vacío, se usa el nombre del sitio. Haga clic en #boton("Abrir").

Al pie del cuadro hay una franja con:

- #boton("Recargar") (*Volver a cargar la página*);
- el zoom de la página: los botones de alejar y acercar y, al medio, el
  porcentaje; un clic en el porcentaje vuelve a 100 %. También se acerca y se
  aleja con #tecla("Ctrl") y la rueda del mouse sobre la página. El zoom se
  guarda con el cuadro, en los tableros y en la última sesión;
- la dirección que está mostrando.

Sobre una página se puede soltar una cámara (la reemplaza) y usar
#boton("Unir") como en cualquier cuadro. La página sigue tal como estaba
—donde navegó, lo que se escribió en ella, su desplazamiento— al moverla o
intercambiarla con otro cuadro, al ampliar, unir o separar la grilla y al
sacar la viñeta del Centro de mando a otra ventana: no se vuelve a cargar.
Abrir y cerrar una página queda en la bitácora de auditoría con su dirección
y el tiempo que estuvo abierta.

#nota[Algunos sitios no se dejan mostrar dentro de otra aplicación.]

=== Mis páginas

Una página abierta con *Otra dirección…* queda guardada para abrirla otra vez
sin escribirla: en la ventana *Otra página web* viene marcada *Guardar en «Mis
páginas» para abrirla otra vez desde la lista*. La página aparece en el grupo
*Páginas web*, después de las de la lista del servidor, con una estrella, y la
barra de estado lo confirma, por ejemplo «"Mapa de tránsito" quedó en la lista
de páginas web (Mis páginas).». Si no quiere guardarla, desmarque la casilla
antes de hacer clic en #boton("Abrir").

- _Mis páginas_ son de su usuario: no las ve nadie más, pero lo siguen a
  cualquier puesto donde ingrese.
- Para quitar una, haga clic derecho sobre ella en la lista y elija *Quitar de
  Mis páginas*. Los cuadros que la estén mostrando siguen abiertos.
- Guardar y quitar páginas queda en la bitácora de auditoría.

Para que una página la vean todos los puestos, pídale al administrador que la
agregue a la lista del servidor (ver _Tableros compartidos y páginas web_, al
final de este capítulo).

=== El Cuadro vigía

Un Cuadro vigía cambia solo a la cámara del último evento que le interesa, para
que no tenga que buscarla en la lista. Arriba del video muestra una franja con
qué pasó, hace cuánto y dónde. No reemplaza a los avisos: solo muestra, no hay
nada que confirmar.

Para ponerlo:

- Desde la lista: grupo *Tarjetas* → *Cuadro vigía*, con doble clic (va al
  cuadro seleccionado) o arrastrándolo a un cuadro.
- Desde una ubicación: clic derecho en la ubicación → *Vigilar esta ubicación en
  el cuadro seleccionado*. El vigía queda filtrado a esa ubicación y a lo que
  contiene, y su barra dice, por ejemplo, «Vigía · Bodegas».

Por omisión vigila todo lo que usted ve: avisos de automatizaciones, alarmas de
paneles y de puertas, accesos denegados, analíticas de video, pérdida de video,
patentes no autorizadas, cerco eléctrico, entradas de módulos de E/S y llamadas
de citofonía, de gravedad advertencia o crítica.

==== La franja

- En espera dice «Esperando eventos» y el cuadro explica que cambiará solo.
- Con un evento, la franja toma el color de su gravedad (rojo crítica, ámbar
  advertencia) y muestra el título, «×3» si se repitió, hace cuánto («ahora»,
  «hace 12 s», «hace 2 min») y el recurso y su ubicación.
- Si el evento trae varias cámaras, las flechas ◀ ▶ pasan de una a otra
  («Cámara 1/2»).
- #boton("Ver aviso") (solo en los avisos de automatizaciones) abre la ventana de
  alarma de ese aviso.
- #boton("Listo") da el evento por visto: pasa al siguiente de la cola o vuelve a
  esperar.
- «+2 en cola» indica eventos esperando su turno; #boton("Siguiente") muestra ya
  el próximo.
- El alfiler fija el vigía: deja de cambiar y los eventos nuevos quedan en cola.
  Se suelta con otro clic o solo a los 5 minutos (la barra muestra cuánto
  falta), para que nadie deje un vigía detenido sin darse cuenta.

Los botones de video de la barra (audio, captura, stream, grabación) y el PTZ
funcionan sobre la cámara que muestra el vigía, como en cualquier cuadro.

Fijar y soltar un vigía, y dar por visto con #boton("Listo") un evento crítico,
quedan en la bitácora de auditoría.

==== Cuándo cambia

- Un evento queda al menos 20 segundos. Uno que llega antes, igual de grave,
  espera en la cola; uno más grave lo reemplaza de inmediato.
- No cambia mientras usted usa el cuadro: con zoom digital, o durante unos
  segundos después de tocar sus botones.
- Varios vigías con la misma configuración se reparten los eventos: el evento
  va al vigía libre de número más bajo; si todos muestran algo, al que muestra
  algo menos grave o ya cumplió su tiempo; si no, a la cola del que tenga menos.
  Con cuatro vigías se ven los cuatro últimos eventos.
- Los eventos que se repiten (la misma puerta, la misma zona) no cambian la
  cámara: suben el contador.

Soltar una cámara de la lista sobre un vigía lo reemplaza por esa cámara, igual
que con cualquier otro contenido. Arrastrar el vigía a otro cuadro lo mueve con
lo que está mostrando.

==== Configurar el vigía

El engranaje de la barra del cuadro abre *Cuadro vigía*:

#table(
  columns: (auto, 1fr),
  table.header[Opción][Qué hace],
  [Qué vigila], [Los tipos de evento. *Detección de movimiento*, *Accesos
    concedidos* y *Patentes autorizadas* vienen desmarcados porque llegan muy
    seguido.],
  [Dónde], [*Todo lo que veo* o *Solo estas ubicaciones (y lo que contienen)*.
    Un evento de un recurso sin ubicación solo llega a los vigías de «Todo lo
    que veo».],
  [Gravedad mínima], [Informativa (todo), Advertencia y crítica, o Solo
    crítica.],
  [Tiempo mínimo en pantalla], [De 5 a 120 segundos.],
  [En espera muestra], [Nada (queda esperando) o una cámara de reposo.],
  [«Fijar» se suelta solo], [De 1 a 30 minutos, o nunca.],
  [Stream principal], [Más calidad; el secundario, que es el de omisión,
    cambia de cámara más rápido.],
)

Lo elegido se guarda con el cuadro en los tableros y en la última sesión. Un
administrador también puede poner un Cuadro vigía en un tablero compartido
desde el panel web; sus opciones se eligen en el cliente.

#nota[Cada usuario ve solo lo suyo: un evento llega a un vigía si su rol
  permite verlo (por ejemplo, los accesos piden *Ver control de acceso* y las
  alarmas *Ver alarmas*) y si el recurso está dentro de su
  alcance; las cámaras que no puede ver no se abren. Los avisos llegan a los
  mismos usuarios que su ventana de alarma.]

=== La cinta de eventos

La cinta es una franja plegable bajo la grilla con los últimos eventos en vivo
(los mismos que reciben los Cuadros vigía), el más reciente a la izquierda. Se
abre y se cierra con el botón del reloj de la barra inferior, junto a la
división; con la cinta cerrada, un número rojo en ese botón cuenta los eventos
que llegaron. La cinta queda abierta o cerrada como la dejó.

Cada ficha muestra el título del evento, la hora, «×3» si se repitió, el
recurso y su ubicación, el ícono de cámara si tiene una que usted puede ver y
«Aviso» si viene de una automatización. El borde va en el color de la
gravedad: rojo crítica, ámbar advertencia, verde informativa.

- Clic en una ficha: abre su cámara en el cuadro seleccionado (o en el primero
  libre).
- Arrastrar una ficha a un cuadro: abre ahí su cámara, igual que una cámara de
  la lista.
- Doble clic en un aviso: abre su ventana de alarma.
- La rueda del mouse recorre la cinta de lado.
- La ✕ de la derecha la vacía: quedan solo los eventos que lleguen desde ahora.

A la izquierda de la cinta se elige la gravedad mínima (*Todo*, *Advertencia y
crítica* o *Solo crítica*) y *Solo de esta grilla*: muestra solo los eventos de
las cámaras y recursos que hay en los cuadros, o los que tomaría alguno de sus
vigías. Cada Centro de mando abierto con «+» tiene su propia cinta.

== Los cuadros de video

Cada cuadro tiene arriba una barra y debajo el video. Un cuadro sin cámara
dice «Cuadro libre».

#captura("cliente-vista-en-vivo-cuadro.png", pie: [Barra de un cuadro mientras graba.])

#table(
  columns: (1fr, 2fr),
  table.header[En la barra][Para qué sirve],
  [Rótulo], [La posición del cuadro en la grilla: «CARD 01», «CARD 02»… Con la
    apariencia _Clásica_ del cliente se ve solo el número.],
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
  [Engranaje], [Solo en las tarjetas con opciones, como *Últimas patentes* o
    *Últimos accesos*: las configura.],
  [Ícono *Subdividir este card para poner más de una cámara*], [Parte el
    cuadro en varios (ver _Subdividir un cuadro_). Está también en los cuadros
    libres.],
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

=== Divisiones para un monitor vertical

Arriba a la derecha de *División de pantalla* está el interruptor
*Horizontal* | *Vertical*. Encendido en *Vertical*, el selector ofrece
divisiones más altas que anchas, para un monitor parado:

#table(
  columns: (auto, 1fr),
  table.header[Familia][Divisiones],
  [Uniformes], [De 1 a 48 cuadros iguales, con tantas filas como columnas o
    más.],
  [Con cuadro principal arriba], [De 3 a 17 cuadros, con uno grande arriba.],
  [En filas], [Cuadros ordenados uno sobre otro.],
  [Combinadas], [Las combinadas de siempre, giradas.],
)

La primera vez que abre el selector, el interruptor parte según la forma de la
ventana: en *Vertical* si es más alta que ancha. Después queda como usted lo
dejó. Una división vertical se guarda en los tableros y en la última sesión
como cualquier otra.

=== Ampliar la grilla

Para sumar una fila o una columna sin cambiar de división, lleve una cámara
de la lista hasta un borde de la grilla (arriba, abajo, a la izquierda o a la
derecha) y déjela ahí un momento, sin soltar el botón:

+ Arrastre la cámara desde la lista hasta el borde de la grilla.
+ Mantenga el puntero en el borde alrededor de un segundo. La grilla se aleja
  un poco y aparece la *franja nueva*, punteada, con sus cuadros; el cuadro
  donde caerá la cámara se resalta con un «+» y el cambio, por ejemplo
  «Columna nueva · 2×2 → 3×2».
+ Mueva el puntero a lo largo de la franja para elegir el cuadro y suelte.

#captura("cliente-vista-en-vivo-ampliar.png", ancho: 85%, pie: [Franja nueva a la derecha de una grilla de 2×2.])

La fila o columna se agrega con la cámara en el cuadro elegido; las demás
cámaras siguen con su video, en el mismo lugar relativo. La barra de estado lo
confirma, por ejemplo «Columna nueva a la derecha: la grilla pasa a 3×2 (6
cuadros).»

La franja aparece solo si el puntero se queda en el borde: si pasa por él
camino a un cuadro, el arrastre sigue siendo hacia ese cuadro, que se ilumina
en amarillo como siempre. Para no agregar nada, saque el puntero del borde
antes de soltar.

La grilla llega hasta 8 columnas, 8 filas y 64 cuadros, y no pasa de la
cantidad de cámaras disponibles en la lista. Al llegar a un tope, la barra de
estado lo explica, por ejemplo «La grilla ya tiene 8 columnas, el máximo.» Con
un cuadro maximizado no se puede ampliar: restáurelo primero.

=== Unir y separar cuadros

El botón #boton("Unir"), en la barra de herramientas, junta varios cuadros en
uno solo, para darle más espacio a una cámara.

+ Haga clic en #boton("Unir").
+ Apriete el botón del mouse sobre un cuadro y arrástrelo hasta otro. Se
  dibuja un recuadro con lo que quedaría unido: todo el rectángulo entre los
  dos cuadros, con el aviso «Unir N cuadros».
+ Suelte el botón: los cuadros del recuadro pasan a ser uno y
  #boton("Unir") se apaga solo. Para unir otros cuadros, vuelva a encenderlo.

Para salir sin unir nada, haga clic otra vez en #boton("Unir") o presione
#tecla("Esc").

#captura("cliente-vista-en-vivo-unir.png", ancho: 85%, pie: [Selección de cuatro cuadros para unirlos.])

El cuadro unido conserva la cámara del cuadro donde empezó la selección o, si
estaba libre, la primera que haya en el recuadro. Las demás cámaras del
recuadro se cierran y la barra de estado dice cuáles, por ejemplo «4 cuadros
unidos en uno. Se cerró: "Bodega".»

Solo se unen rectángulos completos. Si el recuadro corta un cuadro por la
mitad (un cuadro grande que asoma afuera, o una selección en forma de L), se
marca en rojo con «Corta un cuadro: no se puede unir» y al soltar no cambia
nada.

#captura("cliente-vista-en-vivo-unir-corta.png", ancho: 85%, pie: [La selección corta el cuadro grande: no se puede unir.])

Para *separar* un cuadro unido, con #boton("Unir") encendido haga un clic en
él, sin arrastrar: vuelve a dividirse en cuadros simples, la cámara queda en
el de arriba a la izquierda y #boton("Unir") se apaga.

Una grilla ampliada o con cuadros unidos se guarda en los tableros y en la
última sesión como cualquier división; si coincide con una del selector
(por ejemplo, cuatro cuadros unidos en una grilla de 9 dan «6 con cuadro
principal»), el selector la marca. Si no, el botón de la división la muestra
como «a medida», con sus columnas y filas.

=== Subdividir un cuadro

Para poner varias cámaras donde había una, sin cambiar el resto de la grilla,
subdivida ese cuadro (un _card_, como lo llama la barra):

+ Haga clic en el ícono *Subdividir este card para poner más de una cámara*,
  en la barra del cuadro.
+ Elija en cuántos: *En 2, lado a lado*, *En 2, uno sobre otro*, *En 3, lado
  a lado*, *En 4 (2 × 2)*, *En 6 (3 × 2)* o *En 9 (3 × 3)*.

#captura-pendiente("Cliente de monitoreo: grilla de 4 con el menú del ícono Subdividir abierto sobre el cuadro 2")

Lo que mostraba el cuadro queda en el primer pedazo, arriba a la izquierda, y
la selección pasa al segundo, así que los doble clic siguientes en la lista
llenan los pedazos en orden. La barra de estado lo confirma, por ejemplo «Card
subdividido en 4: la grilla queda en 7 cuadros. Arrastre o haga doble clic en
las cámaras para llenarlos.»

Los demás cuadros conservan su forma, su lugar y su video. Los pedazos son
cuadros normales: se pueden volver a subdividir o juntar con #boton("Unir"), y
la grilla subdividida se guarda en los tableros y en la última sesión como
cualquier división a medida.

La grilla tiene los mismos topes que al ampliarla: si no cabe, la barra de
estado lo explica, por ejemplo «La grilla quedaría de 12×12 celdas y el máximo
es 8×8: subdivida un cuadro más grande o use una división con menos
cuadros.». Con un cuadro maximizado no se puede subdividir: restáurelo
primero.

=== Seleccionar un cuadro

Haga clic en cualquier parte de un cuadro: su barra, su video, una tarjeta,
una página web o uno de sus botones. Queda con un borde azul más grueso. Un
clic en un cuadro ya seleccionado no lo suelta; para dejar la grilla sin cuadro
seleccionado, presione #tecla("Esc"). El cuadro seleccionado es donde se abre
la próxima cámara y el que maneja el panel PTZ; sin cuadro seleccionado, la
próxima cámara va al primer cuadro libre.

=== Mover o intercambiar cámaras

Arrastre un cuadro, desde su barra o desde el video, y suéltelo sobre otro;
el destino se ilumina en amarillo.

- Si el destino tiene video, las dos cámaras se intercambian.
- Si está libre, la cámara se muda y el cuadro de origen queda libre.

El video no se corta: la grabación en curso, el zoom digital y el audio viajan
con la cámara. Los números de los cuadros no cambian, porque indican la
posición. La barra de estado confirma el cambio, por ejemplo «Cuadros 1 y 3
intercambiados: …».

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
de estado. Presione #tecla("Esc") para volver. Si el Centro de mando está en
su propia ventana, la pantalla completa cubre el monitor de esa ventana y la
ventana principal queda como estaba.

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
la vez en toda la aplicación: al activar uno se silencia el que estaba
sonando. Los cuadros parten silenciados, con el
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

=== Con un joystick

Un joystick USB que Windows reconozca como mando de juego mueve la cámara PTZ
del cuadro seleccionado, sin instalar nada:

- incline la palanca hacia donde quiera mover la cámara, también en diagonal:
  cuanto más la incline, más rápido se mueve;
- gire la palanca en sentido horario para acercar y al revés para alejar; se
  puede mover y hacer zoom a la vez;
- al soltarla, la cámara se detiene.

Con el joystick conectado y una cámara PTZ seleccionada, el panel muestra la
marca *JOYSTICK*. Actúa mientras alguna ventana del cliente está activa,
también la del Centro de mando si lo llevó a su propia ventana; si cambia a
otro programa, la cámara se
detiene, salvo que marque *Mover la cámara aunque el cliente no esté en primer
plano*: así el joystick sigue moviendo la cámara seleccionada mientras trabaja
en otro programa, con la Vista en vivo abierta. Si se corta la red mientras se
mueve, el servidor la detiene a los 3 segundos.

En #menu("Configuración", "Joystick") se ve el joystick detectado, con sus
ejes y botones en vivo, y se puede desactivar, *Invertir arriba y abajo*,
*Invertir el zoom del giro* o dejarlo operar en segundo plano (la opción de
arriba). #boton("Asignar botones…") abre la lista de
acciones: elija #boton("Asignar") y apriete el botón del joystick que la hará.
Hay acciones para zoom, foco e iris (mientras se sostiene el botón), el modo
precisión, los dígitos del 0 al 9 para teclear el número de preset, *Ir al
preset tecleado*, *Guardar la posición en el preset tecleado*, *Borrar el
número tecleado*, el cuadro siguiente y el anterior, y maximizar. Los ajustes
se guardan en este equipo, por modelo de joystick; los botones, apenas se
asignan.

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

== Tableros

#requiere("Ver video en vivo", "Crear tableros propios")

Un tablero guarda la división de la grilla y lo que hay en cada cuadro: la
cámara con su stream, el recurso, la tarjeta (con sus opciones, como las
cámaras de *Últimas patentes* o los equipos de *Últimos accesos*), el grupo de parlantes o la página web (con su
zoom; una de _Mis páginas_ se guarda con su dirección). Los tableros quedan en el servidor: puede cargarlos desde
cualquier puesto. Para cargar un tablero basta *Ver video en vivo*; para
guardarlos, cambiarlos o borrarlos hace falta además *Crear tableros propios*.

Hay dos clases de tableros:

- *Propios*: los que guarda cada usuario para sí.
- *Compartidos*: los ven los usuarios que elige el administrador, todos o solo
  algunos roles. Llevan un candado (*Tablero compartido: solo lo cambia quien
  administra los tableros*): solo los cambia o los borra quien tiene
  *Administrar tableros compartidos*. Ver _Tableros compartidos y páginas web_,
  en la configuración, al final de este capítulo.

El botón #boton("Tableros") está al centro de la barra de herramientas; a su
lado aparece el nombre del último tablero que cargó.

#captura-pendiente("Cliente de monitoreo: desplegable TABLEROS con un tablero propio y uno compartido con candado, el campo Nombre del tablero… con Guardar y la casilla Compartir con todos")

=== Cargar un tablero

+ Haga clic en #boton("Tableros").
+ Haga clic en el tablero. Bajo su nombre se indica qué tiene, su división y
  con quién se comparte, por ejemplo «6 cámara(s) y 2 cuadro(s) más · división
  9 · para Guardias».

La grilla se arma con el mismo aviso de avance que al abrir un equipo completo
(«Cargando el tablero "Portería"… 4/8»). Si algo del tablero ya no existe o no
está a su alcance, su cuadro queda libre y la barra de estado lo dice, por
ejemplo «Tablero "Portería": 5 cámara(s) y 2 cuadro(s) más en pantalla; 1
cuadro(s) quedaron libres (ya no existen o no están a su alcance).»

=== Guardar la grilla como tablero

+ Arme la grilla con lo que quiere guardar: cámaras, recursos, tarjetas o
  páginas web.
+ Haga clic en #boton("Tableros").
+ Escriba un nombre en *Nombre del tablero…*.
+ Si tiene *Administrar tableros compartidos* y quiere que lo vean otros,
  marque *Compartir con todos (los roles se eligen en el panel web)*. Queda
  para todos los usuarios con video en vivo; para limitarlo a algunos roles,
  edítelo en el panel web.
+ Haga clic en #boton("Guardar").

Si ya tiene un tablero con ese nombre, el cliente pregunta «Ya existe el
tablero "…". ¿Reemplazarlo con la grilla actual?».

=== Actualizar o eliminar un tablero

En los tableros que usted puede cambiar aparecen dos íconos a la derecha:

- el lápiz (*Actualizar este tablero con la grilla actual*) reemplaza su
  contenido por lo que hay ahora en la grilla, después de confirmar;
- el basurero (*Eliminar este tablero*) lo borra, después de confirmar. No se
  puede deshacer.

Si el tablero es compartido, la confirmación lo advierte: el cambio lo ven
todos los que lo usan y, si lo elimina, desaparece para todos.

#nota[Un tablero compartido muestra a cada usuario solo lo de su alcance y lo
  que sus permisos le dejan ver: los demás cuadros quedan libres.]

Mensajes del desplegable:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [«Escriba un nombre para el tablero.»], [Escriba el nombre antes de
    guardar.],
  [«La grilla está vacía: abra lo que quiere guardar (cámaras, recursos,
    tarjetas o páginas).»], [Abra al menos una cámara o un recurso.],
  [«Su rol no le permite hacer esto (requiere "Crear tableros propios").»],
  [Sus roles no incluyen *Crear tableros propios*: pídalo al
    administrador.],
)

== Volver a abrir la última sesión

Con la opción *Volver a abrir las cámaras de la última sesión* encendida
(configuración del cliente, apartado *Video*), al ingresar el cliente abre
solo el Centro de mando tal como quedó: la división, lo que había en cada
cuadro (la cámara con su stream, el recurso, la tarjeta con sus opciones o la
página web) y, si estaba en su propia ventana, esa ventana donde quedó.

- Lo que hay en pantalla se recuerda a medida que cambia, así que también
  sirve si el cliente se cerró de golpe.
- Es por usuario y por servidor: otra persona que ingrese en este equipo no
  hereda sus cámaras.
- Si el monitor donde estaba la ventana ya no está conectado, la ventana
  aparece en su lugar de siempre.

Mientras se abre, la barra de estado dice «Volviendo a abrir la última
sesión…» y al terminar, por ejemplo, «Última sesión restaurada: 8 cuadro(s) en
pantalla.». Si algo ya no está disponible, se agrega cuántos cuadros y esos
cuadros quedan libres.

== Llevar el Centro de mando a otro monitor

El Centro de mando completo puede salir de la ventana principal a una ventana
propia, como una pestaña que se arrastra fuera de un navegador. Se lleva todo:
la grilla con su video, la lista de recursos, el panel PTZ, los tableros y la
barra de herramientas. El video no se corta al mudarse.

#captura-pendiente("Cliente de monitoreo: el Centro de mando en su propia ventana, con su viñeta arriba, el botón Devolver a la ventana principal y la grilla con cámaras; detrás, la ventana principal sin esa viñeta")

+ Apriete el botón del mouse sobre la viñeta *Centro de mando*, en la barra
  superior.
+ Arrástrela hacia abajo, fuera de la barra. La viñeta sale en una ventana
  nueva que sigue al puntero.
+ Suelte el botón donde quiera la ventana, por ejemplo en otro monitor, y
  maximícela si quiere.

También puede hacer clic derecho en la viñeta y elegir *Llevar a una ventana
nueva*: la ventana aparece donde estuvo la última vez.

Mientras está afuera, la viñeta deja de verse en la ventana principal, que
sigue con los demás módulos. El botón *Centro de mando* del riel y su tarjeta
de la página Inicio traen esa ventana al frente.

La ventana tiene arriba su propia viñeta *Centro de mando*: arrastrándola se
mueve la ventana, y doble clic sobre ella la maximiza o la restaura. A la
derecha están el botón para devolverla (*Devolver a la ventana principal*),
*Minimizar*, *Maximizar* y *Cerrar*. #boton("Pantalla completa") cubre el
monitor de esta ventana; #tecla("Esc") vuelve.

=== Devolverlo a la ventana principal

- Arrastre la viñeta de la ventana y suéltela sobre la barra de viñetas de la
  ventana principal, o
- haga clic en el botón *Devolver a la ventana principal*, a la derecha de la
  barra de la ventana.

El Centro de mando vuelve a la ventana principal con todo lo que tenía, sin
cortar el video, y su viñeta queda activa.

=== Cerrar la ventana

Cerrar la ventana, con su #boton("✕") o con la de su viñeta, cierra el Centro
de mando y detiene todos los videos, igual que la #boton("✕") de la viñeta en
la ventana principal. Para guardarlo sin cerrarlo, devuélvalo a la ventana
principal.

Al salir del cliente, el Centro de mando que estaba en su propia ventana se
recuerda: con *Volver a abrir las cámaras de la última sesión* encendida,
vuelve a abrirse en esa ventana, en el monitor y la posición donde quedó.

#nota[Las demás viñetas también se pueden llevar a su propia ventana (ver
  #capitulo(<cap-primeros-pasos>)). Esta ventana reemplaza a las _pantallas
  auxiliares_ de versiones anteriores: si al actualizar había alguna abierta,
  vuelve con la última sesión hasta que la cierre, pero ya no se abren nuevas.]

== Órdenes sobre una ubicación

#requiere("Órdenes por ubicación")

En la lista de recursos, el clic derecho sobre una ubicación ofrece, además
de *Abrir sus cámaras* y *Vigilar esta ubicación en el cuadro seleccionado*
(ver _El Cuadro vigía_), órdenes para todas las áreas de alarma de esa
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

Si hay parlantes IP configurados, entre la lista de recursos y el panel PTZ
aparece el panel *PARLANTES*, para hablar, reproducir sonidos y leer textos en
voz alta por los parlantes. Su uso se explica en #capitulo(<cap-parlantes-ip>).
]

== Atajos de teclado y mouse

#table(
  columns: (auto, 1fr),
  table.header[Tecla o gesto][Acción],
  [Doble clic en una cámara de la lista], [La abre en el cuadro seleccionado o
    en el primero libre.],
  [Doble clic en una ubicación (o en un equipo de *Sin ubicación*)], [Abre
    todas sus cámaras.],
  [Doble clic en un recurso, una tarjeta o una página web], [Lo abre en el
    cuadro seleccionado o en el primero libre.],
  [Arrastrar una cámara o un recurso de la lista a un cuadro], [Lo abre en ese
    cuadro.],
  [Arrastrar una cámara a un borde de la grilla y esperar], [Muestra la franja
    nueva; al soltar agrega esa fila o columna con la cámara.],
  [Arrastrar un cuadro sobre otro], [Intercambia o muda lo que muestra.],
  [Con #boton("Unir"): arrastrar de un cuadro a otro], [Une el rectángulo en un
    solo cuadro.],
  [Con #boton("Unir"): clic en un cuadro unido], [Lo separa en cuadros
    simples.],
  [Clic en un cuadro (barra, video, tarjeta o página web)], [Lo
    selecciona.],
  [Doble clic en la barra de un cuadro o en su video], [Lo maximiza o lo
    devuelve a la grilla.],
  [#tecla("Ctrl") + rueda del mouse sobre una página web], [Acerca o aleja la
    página.],
  [Rueda del mouse sobre el video], [Zoom digital alrededor del puntero.],
  [Clic derecho sobre el video (modo zoom)], [Vuelve el zoom digital a 1×.],
  [Clic derecho en una ubicación], [Abre el menú de la ubicación.],
  [#tecla("↑") #tecla("↓") #tecla("←") #tecla("→")], [Mueven la cámara PTZ
    del cuadro seleccionado mientras mantiene la tecla.],
  [#tecla("+") #tecla("−")], [Acercan o alejan la cámara PTZ.],
  [#tecla("Shift") sostenido], [Modo precisión: el PTZ por teclado se mueve a
    la velocidad mínima.],
  [Arrastrar la viñeta *Centro de mando* fuera de la barra], [Lo lleva a su
    propia ventana.],
  [Soltar la viñeta de esa ventana sobre la barra principal], [Lo devuelve a
    la ventana principal.],
  [#tecla("Esc")], [Termina el modo #boton("Unir"); sale de la pantalla
    completa; si no, deja la grilla sin cuadro seleccionado. Durante la
    apertura de un equipo completo, cierra el aviso y devuelve el control.],
)

== Vista en vivo desde el navegador

#requiere("Ver video en vivo")

El panel web tiene su propia Vista en vivo, en
#menu("Aplicaciones", "Vista en vivo"), para ver cámaras desde cualquier equipo sin instalar nada. Funciona
como el Centro de mando del cliente de monitoreo, pero solo con cámaras: los
recursos, las tarjetas y las páginas web se ven en el cliente. Para monitorear
muchas cámaras a la vez, use el cliente.

#captura("web-vista-en-vivo.png", pie: [Vista en vivo del panel web con un tablero y los ajustes abiertos.])

=== Lo mismo que en el cliente

- La lista de cámaras, que en el navegador se agrupa por #boton("Equipo") o
  #boton("Ubicación"), con el buscador *Buscar canal…*. Cada equipo y cada ubicación se pliega con la
  flecha de su izquierda, y el navegador lo recuerda.
- Doble clic en una cámara, un equipo o una ubicación; el clic derecho en una
  ubicación, con *Abrir sus cámaras* y las órdenes a sus áreas de alarma.
- Arrastrar una cámara a un cuadro, y un cuadro sobre otro (tómelo de su
  barra).
- Ampliar la grilla dejando una cámara un momento en un borde, y
  #boton("Unir") para unir y separar cuadros, con el mismo recuadro. En el
  navegador la grilla ampliada llega hasta 16 cuadros.
- La barra de cada cuadro: audio, P/S, captura, grabación y
  #boton("✕") (*Cerrar el video de este cuadro*).
- Doble clic en el video para maximizar, #boton("Limpiar todo"),
  #boton("Tableros") y #boton("Pantalla completa").
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
    (para verlos todos, use el cliente de escritorio).» Un tablero más grande
    se acomoda en una división de hasta 16 cuadros y el resto se omite, con
    aviso.],
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
  [Otro monitor], [#boton("Pantalla auxiliar") abre otra ventana del navegador
    con su propia grilla (ver _Pantallas auxiliares en el navegador_). En el
    cliente, en cambio, se lleva el Centro de mando entero a otra ventana.],
  [Subdividir un cuadro y divisiones verticales], [No están: se usan en el
    cliente.],
  [Ajustes], [Se cambian con el engranaje de la barra y quedan guardados en
    ese navegador.],
  [Joystick PTZ], [Solo con la ventana principal activa (en el cliente vale
    cualquier ventana del cliente). Sus opciones están en *Ajustes de este
    navegador*: ver _Joystick PTZ_ más abajo.],
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
  [*Joystick PTZ*], [El joystick detectado y sus opciones (ver _Joystick PTZ_).
    Solo en la ventana principal.],
)

=== Joystick PTZ

#requiere("Mover cámaras PTZ")

En la ventana principal, un joystick USB conectado a este equipo mueve la
cámara PTZ del cuadro seleccionado. Sirve cualquier joystick que Windows
reconozca como mando de juego, sin instalar nada.

+ Conecte el joystick y, con la Vista en vivo abierta, muévalo o apriete un
  botón: el navegador lo detecta recién ahí, y la barra de estado dice
  «Joystick conectado: …».
+ Seleccione el cuadro de la cámara PTZ. El panel PTZ muestra la marca
  *JOYSTICK*.
+ Incline la palanca hacia donde quiera mover la cámara, también en diagonal:
  cuanto más la incline, más rápido se mueve. Gire la palanca en sentido
  horario para acercar y al revés para alejar. Al soltarla, la cámara se
  detiene.

Se puede mover y hacer zoom a la vez, con la velocidad tan fina como la
inclinación. El servidor usa la vía de cada marca: la SDK en los equipos
Hikvision y Dahua, y ONVIF en el resto. Si un equipo no admite el movimiento
combinado (por ejemplo, el PTZ por coaxial de algunos grabadores), mueve igual,
con las velocidades 1 a 7 del panel.

Solo actúa con la ventana del panel activa; si cambia a otra ventana, la cámara
se detiene. Si el navegador deja de responder o se corta la red mientras la
cámara se mueve, el servidor la detiene a los 3 segundos. El modo precisión
(#tecla("Shift")) también vale para el joystick: la cámara se mueve a un
quinto de la velocidad.

En *Ajustes de este navegador*, la sección *Joystick PTZ* muestra el joystick
detectado y permite:

- Desactivarlo: *Mover la cámara PTZ seleccionada con el joystick*.
- *Invertir arriba y abajo* e *Invertir el zoom del giro*.
- *Mover la cámara aunque esta ventana no esté en primer plano*: sigue
  actuando con el panel visible detrás de otra ventana, siempre que el
  navegador entregue los datos del joystick en ese caso (no todos lo hacen).
- #boton("Asignar botones…"): elija #boton("Asignar") en una acción y apriete
  el botón del joystick que la hará. Hay acciones para zoom, foco e iris
  (mientras se sostiene el botón), el modo precisión, los dígitos del 0 al 9,
  *Ir al preset tecleado*, *Guardar la posición en el preset tecleado*,
  *Borrar el número tecleado*, el cuadro siguiente y el anterior, y
  maximizar.

Con los dígitos se arma el número de preset, como en un teclado PTZ. Por
ejemplo, 1, 2 y el botón de *Ir al preset tecleado* llevan la cámara al preset
12 («PTZ: moviéndose al preset 12.»). La configuración se guarda en ese
navegador, por joystick.

=== Tableros en el navegador

Son los mismos tableros del cliente de monitoreo, pero el panel web abre solo
sus cámaras: si un tablero tiene otros cuadros, la barra de estado avisa
cuántos se ven solo en el cliente de escritorio. Sin el permiso *Crear
tableros propios*, el desplegable solo permite cargarlos. Para guardar,
escriba el nombre y haga clic en #boton("Guardar") o presione #tecla("Enter").

A diferencia del cliente, si ya tiene un tablero con ese nombre el panel no
ofrece reemplazarlo: muestra «Ya tiene un tablero con ese nombre.». Para
cambiar un tablero existente, use su lápiz (*Actualizar este tablero con la
grilla actual*). Un tablero con recursos, tarjetas o páginas web no se
actualiza desde el navegador, porque se perderían esos cuadros: el panel pide
hacerlo desde el cliente de escritorio o, si es compartido, en
#menu("Configuración", "Tableros"). Al eliminar un tablero compartido, el panel
advierte que desaparece para todos los puestos.

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

Algunos navegadores no reciben por WebRTC las cámaras configuradas en H.265
(Edge, por ejemplo). El panel lo resuelve solo, en este orden:

+ Abre la cámara por la *entrega alternativa*, si el navegador decodifica
  H.265 en ese equipo. El cuadro avisa «H.265 por entrega alternativa: sin
  audio en el panel web.»: la imagen llega con algo más de retardo y sin
  audio. El navegador lo recuerda para las próximas veces.
+ Si no, abre el stream secundario, que suele venir en H.264. El cuadro avisa
  «Se muestra el secundario: este navegador no reproduce el formato del
  principal.» y no sube a principal al maximizarlo.

Si ninguna de las dos sirve, el cuadro lo dice y no reintenta: «Este navegador
no reproduce el formato de video de la cámara: típicamente H.265, que en este
equipo no decodifica ni por WebRTC ni por la entrega alternativa (le falta
decodificación por hardware). Use Chrome actualizado, configure el stream de la
cámara en H.264 o use el cliente de escritorio.» Las grabaciones siguen el
mismo camino, salvo el secundario. *Ajustes de este navegador* (el engranaje)
dice qué admite el navegador en uso: «H.265 en este navegador: por WebRTC no; por la
entrega alternativa (sin audio) sí.»

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

Lo que ve y puede hacer cada operador en el Centro de mando depende de lo
siguiente:

#table(
  columns: (1fr, 2fr),
  table.header[Qué][Dónde se configura],
  [Quién ve video, mueve cámaras PTZ, arma tableros y da órdenes por
    ubicación],
  [Los permisos *Ver video en vivo*, *Mover cámaras PTZ*, *Crear tableros
    propios*, *Administrar tableros compartidos*, *Abrir cualquier página web*
    y *Órdenes por ubicación* de sus roles. Ver
    #capitulo(<cap-usuarios-y-roles>).],
  [Qué recursos aparecen y se pueden operar],
  [Los permisos de cada módulo (puertas, alarmas, cercos, citofonía,
    parlantes, módulos de E/S) y el alcance de sus roles.],
  [Qué cámaras aparecen en la lista],
  [Solo los canales con la casilla *Habilitado*, en la ventana
    #boton("Canales") de cada equipo de
    #menu("Dispositivos", "Fuentes de video"). Ver
    #capitulo(<cap-fuentes-de-video>).],
  [Qué cámaras tienen control PTZ],
  [La casilla *PTZ / lente* de la misma ventana. Se marca sola cuando el
    equipo informa que la cámara es PTZ; márquela a mano, por ejemplo, en
    domos conectados por coaxial a un grabador.],
  [Las ubicaciones de la lista y el alcance de cada usuario],
  [El árbol de #menu("Recursos") y el alcance de los roles. Ver
    #capitulo(<cap-recursos-y-ubicaciones>).],
)

=== Tableros compartidos y páginas web

#requiere("Administrar tableros compartidos")

En el panel web, #menu("Configuración", "Tableros") administra los tableros
compartidos y la lista de páginas web que se pueden poner en un cuadro. Los
tableros compartidos aparecen en el Centro de mando de los usuarios a los que
se asignan, con candado. Para que un rol no arme tableros propios, quítele
*Crear tableros propios* en #menu("Seguridad", "Roles").

#captura-pendiente("Panel web: editor Nuevo tablero compartido con la división de 4, Solo estos roles: con un rol marcado, dos cuadros llenos y el cuadro 3 elegido con la lista de búsqueda abierta")

Para armar un tablero compartido:

+ Haga clic en #boton("Nuevo tablero").
+ Escriba el *Nombre* y elija la *División*.
+ En *Lo ven*, elija *Todos los usuarios con video en vivo* o *Solo estos
  roles:* y marque los roles.
+ Haga clic en un cuadro de la grilla. A la derecha, busque qué mostrará
  (*Buscar cámara, puerta, tarjeta, página…*): una cámara, un recurso, un grupo
  de parlantes, una tarjeta de reporte o una página web, y haga clic en él. La
  selección pasa sola al cuadro libre siguiente.
+ Con *Abrir cualquier página web*, también puede escribir otra dirección
  (*https://…*), con un *Rótulo (opcional)*, y hacer clic en #boton("Poner").
+ Haga clic en #boton("Guardar").

La × de un cuadro (*Dejar libre*) lo vacía. Si cambia a una división más chica,
el editor avisa cuántos cuadros quedan fuera y no se guardarán. Un tablero
guardado desde el cliente con una división que el panel web no dibuja (más de
16 cuadros o a medida) muestra «(del cliente)»: en el panel web se le cambian
el nombre y con quién se comparte, y sus cuadros se conservan. Una tarjeta
*Últimas patentes* con cámaras elegidas en el cliente aparece como, por
ejemplo, «Tarjeta · 2 cámaras», y una *Últimos accesos* con equipos elegidos
y los eventos del sistema, como «Tarjeta · 3 equipos · con eventos del
sistema».

La tabla *Tableros compartidos* muestra, para cada uno, quiénes lo ven
(*Todos*, los roles, o *Sin roles* si sus roles se borraron), la división, el
contenido y cuándo se actualizó, con #boton("Editar") y #boton("Eliminar").
Un tablero compartido también se puede guardar desde el cliente (ver
_Tableros_).

En *Páginas web* está la lista de páginas que cualquiera con video en vivo puede
poner en un cuadro. #boton("Agregar página web") pide *Nombre*, *Dirección* y
*Descripción (opcional)*. La columna *En tableros* dice en cuántos tableros
compartidos está cada página; al quitar una que se usa, el panel advierte que
esos cuadros quedarán libres. Si cambia la dirección de una página, los
tableros que la tienen abren la nueva. Las páginas que cada operador guarda en
_Mis páginas_ no aparecen en esta lista: son solo suyas.

#consejo[Algunos sitios no se dejan mostrar dentro de otra aplicación: pruebe la
  página en un cuadro del cliente antes de ponerla en un tablero.]

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
