#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/resources.js, src/TrueCentralVms.Server/wwwroot/app.js, src/TrueCentralVms.Server/wwwroot/live.js, src/TrueCentralVms.Client/Views/VerificationWindow.xaml, src/TrueCentralVms.Client/Views/VerificationWindow.xaml.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Locations.cs, src/TrueCentralVms.Client/ViewModels/LocationTreeNodes.cs, src/TrueCentralVms.Client/Views/LiveView.xaml, src/TrueCentralVms.Server/Api/LocationsApi.cs, src/TrueCentralVms.Server/Api/ResourcesApi.cs

= Recursos y ubicaciones <cap-recursos-y-ubicaciones>

Los equipos físicos (grabadores, paneles, controladoras) se administran en
_Dispositivos_. Lo que el operador usa día a día —cámaras, puertas, áreas y
zonas de alarma, cercos, parlantes y citófonos— son los *recursos*, y la página
#menu("Recursos") los ordena según dónde están, en un árbol de *ubicaciones*
(sitio, edificio, piso, sector, punto). Con ese orden:

- las cámaras se encuentran por lugar en la vista en vivo y en la reproducción;
- cada recurso tiene una ficha con consignas para el operador y cámaras
  asociadas, que el puesto de monitoreo muestra cuando el recurso avisa algo;
- se puede armar o desarmar todo lo de un lugar con una sola orden;
- se puede limitar lo que ve y opera cada usuario.

#captura("web-recursos.png", pie: [Recursos: árbol de ubicaciones y recursos de la ubicación elegida.])

== Qué es un recurso y qué es una ubicación

=== Recursos

Los recursos aparecen solos al agregar equipos en _Dispositivos_; no se crean
ni se borran desde esta página.

#table(
  columns: (auto, 1fr),
  table.header[Recurso][De dónde sale],
  [Cámara], [Cada canal de una fuente de video: una cámara IP o un canal de un grabador.],
  [Puerta], [Cada puerta de un equipo de control de acceso.],
  [Área de alarma], [Cada área de un panel de alarma.],
  [Zona], [Cada zona de un panel de alarma.],
  [Cerco], [Cada panel de cerco eléctrico.],
  [Parlante], [Cada parlante IP.],
  [Citófono], [Cada frente de citofonía.],
)

Cada recurso está en *una sola* ubicación, o en *Por ubicar* si nadie lo ha
ubicado todavía. Ahí llega, por ejemplo, lo que descubre un equipo nuevo que se
agregó sin ubicación.

=== Ubicaciones

Una ubicación es un lugar: una sucursal, un edificio, un piso, una bodega, un
acceso. Cada una tiene un *Tipo*, que solo cambia el ícono con que se muestra:

#table(
  columns: (auto, 1fr),
  table.header[Tipo][Uso habitual],
  [Sitio], [Un recinto o sucursal completa.],
  [Edificio], [Un edificio dentro del sitio.],
  [Piso], [Un piso o nivel.],
  [Sector], [Una zona del piso: bodega, estacionamiento, recepción.],
  [Punto], [Un lugar puntual: un acceso, una caja, un portón.],
)

Cualquier tipo puede ir dentro de cualquier otro; el árbol admite hasta 8
niveles. Lo que está en una ubicación incluye lo de todas sus sububicaciones:
por ejemplo, armar las áreas de un sitio arma también las de sus edificios.

== Recorrer la página Recursos

Abra #menu("Recursos") en la sección _Configuración_ del menú lateral.

- *Árbol de ubicaciones* (a la izquierda). Arriba está *Todos los recursos*;
  abajo, *Por ubicar*. El número al lado de cada ubicación cuenta sus recursos,
  incluidos los de sus sububicaciones. La flecha junto al nombre pliega o
  despliega la rama; el navegador recuerda lo que dejó plegado.
- *Encabezado*. Muestra la ruta de la ubicación elegida, su tipo y, si las
  tiene, la descripción, la dirección y las coordenadas.
- *Filtros*. Las etiquetas por tipo (_Todos_, _Cámaras_, _Puertas_…) muestran
  cuántos recursos de cada tipo hay en la selección; haga clic en una para ver
  solo ese tipo. El buscador encuentra por nombre, equipo o estado, aunque no
  escriba las tildes, y la lista *Todos los equipos* deja ver solo los recursos
  de un equipo.
- *Lista de recursos*, con las columnas *Recurso* (nombre, tipo y detalle, como
  el número de canal o de puerta), *Equipo físico*, *Ubicación* y *Estado*.

En la columna *Ubicación*, la ruta se muestra a partir de la ubicación
elegida: un guion significa que el recurso está justo ahí y no en una
sububicación. El nombre del equipo, en *Equipo físico*, lleva a la página de ese
equipo.

La página se actualiza sola cuando alguien cambia el árbol o los equipos, y el
estado de cada recurso se refresca cada 15 segundos.

#nota[Sin el permiso _Recursos y ubicaciones_ la página es de solo lectura: no
  aparecen los botones para crear, editar ni mover. Además, cada usuario ve
  solo las ubicaciones y los recursos de su alcance (ver la última sección).]

== Armar el árbol de ubicaciones

#requiere("Recursos y ubicaciones")

=== Crear una ubicación

+ Para una ubicación de primer nivel, haga clic en #boton("+ Nueva") en la
  cabecera del árbol. Para una dentro de otra, seleccione la de arriba en el
  árbol y haga clic en #boton("+ Sububicación").
+ Escriba el *Nombre*.
+ Revise el *Tipo*. El sistema sugiere el siguiente nivel según la ubicación
  de arriba (dentro de un sitio, un edificio; dentro de un edificio, un piso), y
  usted puede cambiarlo.
+ En *Dentro de* queda la ubicación de arriba; elija _— En la raíz del árbol —_
  para que sea de primer nivel.
+ Si quiere, complete *Descripción*, *Dirección*, *Latitud* y *Longitud*.
+ Haga clic en #boton("Crear"). La ubicación nueva queda seleccionada.

#nota[Dos ubicaciones con el mismo padre no pueden llamarse igual, aunque
  cambien mayúsculas y minúsculas. Latitud y longitud se escriben como número
  decimal (por ejemplo, `-33.4489`) y van las dos juntas o ninguna.]

=== Editar o mover una ubicación

- Para cambiar sus datos, selecciónela y haga clic en #boton("Editar"). Cambiar
  *Dentro de* la mueve con todo lo que contiene.
- También puede arrastrar una ubicación sobre otra en el árbol. Para dejarla en
  el primer nivel, suéltela sobre *Todos los recursos*.

Una ubicación no se puede mover dentro de sí misma ni de sus sububicaciones, ni
a un lugar donde el árbol pasaría de 8 niveles.

=== Eliminar una ubicación

+ Selecciónela y haga clic en #boton("Eliminar").
+ Confirme. Sus recursos y los equipos ubicados ahí quedan en *Por ubicar*:
  no se borra ningún equipo.

No se puede eliminar una ubicación que:

- tenga sububicaciones: muévalas o elimínelas antes;
- esté en el alcance de algún usuario o rol: el mensaje dice cuáles. Quítela
  antes en #menu("Seguridad", "Usuarios") o #menu("Seguridad", "Roles").

== Ubicar los recursos

#requiere("Recursos y ubicaciones")

=== Mover varios a la vez

+ En el árbol, elija dónde están hoy (por ejemplo, *Por ubicar*).
+ Marque las casillas de los recursos. La casilla del encabezado de la lista
  marca todos los que se ven con los filtros actuales.
+ En la barra que aparece sobre la lista, haga clic en #boton("Mover a…").
+ Elija la ubicación de destino y haga clic en #boton("Mover").

En la misma barra, #boton("Quitar ubicación") devuelve los marcados a *Por
ubicar* y #boton("Desmarcar") limpia la selección. La barra también avisa
cuántos marcados quedaron fuera de la vista actual por los filtros.

=== Arrastrar

Arrastre una fila de la lista hasta una ubicación del árbol. Si la fila está
marcada, se llevan todas las marcadas; si no, solo esa. Soltar sobre *Por
ubicar* les quita la ubicación.

#consejo[Use *Por ubicar* como bandeja de entrada: después de agregar un equipo,
  revise ahí sus recursos y ubíquelos de una vez.]

#importante[Mover un recurso cambia en el acto quién lo ve: los usuarios con el
  alcance limitado a ciertas ubicaciones ganan o pierden ese recurso de
  inmediato en sus listas y en sus puestos.]

== Ubicación de cada equipo y herencia

#requiere("Fuentes de video", "Paneles de alarma", "Paneles de cerco", "Equipos de acceso", "Parlantes IP", "Citofonía")

Los formularios para agregar o editar un equipo en _Dispositivos_ (fuentes de
video, paneles de alarma, paneles de cerco, control de acceso, parlantes IP y
citofonía) tienen el campo *Ubicación*, con el árbol y la opción *Por ubicar*.
Cada formulario exige el permiso de configuración de su propio módulo.

En los equipos que tienen recursos adentro (un grabador y sus canales, un panel
de alarma y sus áreas y zonas, un equipo de acceso y sus puertas), la ubicación
del equipo se *hereda*:

- los recursos nuevos del equipo (al agregarlo, al revalidarlo o cuando el panel
  informa áreas y zonas nuevas) entran en la ubicación del equipo; una zona
  nueva entra con su área, si el área ya está ubicada;
- al cambiar la ubicación del equipo, se mueven con él los recursos que estaban
  en su ubicación anterior o por ubicar;
- los recursos que usted ubicó aparte desde Recursos se quedan donde están.

En las listas de equipos, la ruta de la ubicación aparece bajo el nombre de cada
equipo. Los que dicen _Por ubicar_ no los ven los usuarios con el alcance
limitado a ciertas ubicaciones.

#nota[Si el panel no logra leer el árbol de ubicaciones, el campo *Ubicación*
  queda bloqueado con el texto _No se pudo leer el árbol: se conserva la
  actual_, y al guardar el equipo conserva la ubicación que tenía.]

== La ficha del recurso

Haga clic en el nombre de un recurso en la lista para abrir su ficha. Arriba
muestra el tipo, el detalle, la ruta de su ubicación y su estado; debajo, las
pestañas. #boton("← Volver a la lista") regresa a la lista, y elegir una
ubicación en el árbol también.

La ficha tiene su propia dirección en el navegador, incluida la pestaña
abierta: puede guardarla o enviarla como enlace a otro usuario.

#captura("web-recursos-ficha.png", pie: [Ficha de un recurso, con sus consignas para el operador.])

=== General

#requiere("Recursos y ubicaciones")

+ Si hace falta, corrija el *Nombre* y la *Ubicación*.
+ En *Descripción*, escriba qué es y dónde está exactamente.
+ En *Consignas para el operador*, escriba qué debe hacer el operador cuando
  este recurso avisa algo. Es el texto que verá el guardia en el puesto.
+ Haga clic en #boton("Guardar").

El nombre de las cámaras, puertas, parlantes y citófonos se cambia aquí. El de
las áreas y zonas lo define el panel de alarma (se cambia en el propio panel) y
el de un cerco se cambia en #menu("Dispositivos", "Paneles de cerco"); en esos
casos el campo está bloqueado y dice por qué.

Sin el permiso, la pestaña muestra la ubicación, la descripción y las consignas
como texto.

A la derecha está la *Imagen actual*: la de la propia cámara o, en una puerta,
zona u otro recurso, la de su cámara principal. Con *Actualizar cada* elija
cada cuánto se refresca (5 s, 10 s, 30 s, 1 min o _manual_; el navegador lo
recuerda) y con #boton("Actualizar ahora") pídala en el momento. Si el equipo no
la entrega, dice _El equipo no entregó una imagen (sin conexión o sin señal)._

=== Equipo

Muestra el *Equipo físico* (nombre, modelo, número de serie, firmware,
dirección, estado de conexión y cuándo se vio por última vez) y las
*Características* del recurso según su tipo. #boton("Ir al equipo") abre la
página del equipo.

=== Cámaras asociadas

#requiere("Recursos y ubicaciones")

Las cámaras que muestran lo que pasa en el recurso; la primera es la
*Principal*. Son las que ve el operador cuando el recurso avisa algo. Esta
pestaña no existe en las cámaras.

+ Escriba en el buscador parte del nombre de la cámara o de su equipo.
+ Haga clic en la cámara para asociarla. El buscador queda listo para agregar
  otra.
+ Ordénelas con las flechas #boton("↑") y #boton("↓"): la que quede primera es
  la principal. #boton("Quitar") la desasocia.

Se pueden asociar hasta 16 cámaras por recurso. En un citófono, la cámara de su
frente aparece fija, marcada _Cámara del frente_; esa se cambia en la
configuración de citofonía.

=== Automatizaciones

Lista las automatizaciones que nombran al recurso y cómo lo usan: _La dispara_,
_La consulta_ o _Actúa sobre él_, y si están _Activa_ o _Pausada_. Debajo
indica cuántas automatizaciones generales lo alcanzan sin nombrarlo (por
ejemplo, las que se disparan con cualquier panel de alarma).
#modulo("automation")[Las automatizaciones se explican en
  #capitulo(<cap-automatizaciones>).]

=== Historial

Las 80 entradas más recientes del recurso, de la más nueva a la más antigua:

- *Evento*: lo que informó el equipo (accesos de una puerta, eventos de un área
  o zona, del cerco, llamadas del citófono, patentes de una cámara);
- *Aviso*: alertas de automatizaciones originadas por el recurso, con quién las
  atendió;
- *Bitácora*: cambios hechos por los usuarios (ficha, ubicación, órdenes).

== Verificación en el puesto

#requiere("Ver alarmas", "Ver cercos eléctricos", "Ver control de acceso")

Cuando un recurso avisa algo grave y su ficha tiene consignas o cámaras
asociadas, el cliente de monitoreo abre sola la ventana *Verificación del
aviso*, sin que nadie tenga que configurar una automatización. Cada aviso llega
solo a quien tiene el permiso para ver ese tipo de equipo y el recurso en su
alcance. Ocurre con:

- una alarma crítica de un panel de alarma (se usa la ficha de la zona y, si la
  zona no tiene consignas ni cámaras, la de su área);
- una alarma de cerco eléctrico;
- una alarma de puerta (forzada, mantenida abierta, sabotaje, coacción) de los
  últimos 5 minutos.

#captura-pendiente("Cliente de monitoreo: ventana Verificación del aviso — consignas, recurso, ubicación y aviso a la izquierda; dos cámaras asociadas en vivo a la derecha", alto: 6cm)

La ventana muestra:

- arriba, el aviso, el recurso, su ubicación y la hora;
- a la izquierda, *Consignas para el operador*, *Recurso*, *Ubicación*,
  *Descripción* y *Aviso*;
- a la derecha, el video en vivo de las cámaras asociadas, con la principal
  como cuadro 1.

Para atender el aviso:

+ Lea las consignas y siga las instrucciones.
+ Revise el video de las cámaras.
+ Si necesita más detalle, haga clic en #boton("Abrir en la Vista en vivo"):
  las cámaras pasan a la grilla principal.
+ Haga clic en #boton("Cerrar") cuando termine.

Una sola ventana reúne todos los avisos: si llegan varios, use las flechas
_Aviso anterior_ y _Aviso siguiente_. Si el mismo recurso vuelve a avisar dentro
del minuto, se actualiza su entrada en vez de sumar otra.

#nota[Cerrar la ventana no cambia nada en el equipo: no desarma, no silencia ni
  da por atendido nada. Si una automatización avisa por el mismo recurso, se
  muestra solo la ventana de la automatización, que ya trae las consignas y las
  cámaras.]

Si el recurso no tiene consignas, la ventana lo dice y recuerda que se escriben
en la ficha.#modulo("automation")[ Las consignas y la ubicación también
aparecen en las alertas del Centro de eventos
(#capitulo(<cap-centro-de-eventos>)).]

== Ver las cámaras por ubicación

#requiere("Ver video en vivo", "Ver grabaciones")

En la vista en vivo y en la reproducción del cliente de monitoreo, y en la
vista en vivo del panel web, la lista de cámaras tiene el selector
#boton("Equipo") | #boton("Ubicación"). Con *Ubicación*:

- las cámaras se agrupan según el árbol de Recursos, y las ubicaciones sin
  cámaras no se muestran;
- las cámaras que nadie ubicó van al final, en *Sin ubicación*;
- el buscador también encuentra por el nombre de la ubicación;
- doble clic en una ubicación abre todas sus cámaras, incluidas las de sus
  sububicaciones.

El cliente recuerda el modo elegido, y las pantallas auxiliares siguen al de la
ventana principal.
#modulo("video")[La vista en vivo se explica en #capitulo(<cap-vista-en-vivo>).]

== Órdenes por ubicación

#requiere("Órdenes por ubicación")

Arma o desarma de una vez todas las áreas de alarma de una ubicación, incluidas
las de sus sububicaciones. Solo cuentan las áreas habilitadas de paneles
habilitados.

=== Desde el panel web

+ En #menu("Recursos"), seleccione la ubicación. Si contiene áreas de alarma,
  bajo el encabezado aparece la barra con #boton("Armar total"),
  #boton("Armar parcial") y #boton("Desarmar").
+ Haga clic en la orden. El sistema muestra cuántas áreas va a tocar y sus
  nombres.
+ Confirme.

Si todas aceptan, un aviso lo confirma. Si alguna falla, la ventana *Resultado
de la orden* lista cada área que no aceptó, con su panel y el motivo.

En la vista en vivo del panel web, con la lista de cámaras por *Ubicación*, el
clic derecho sobre una ubicación ofrece las mismas órdenes que en el cliente
(ver más abajo).

=== Desde el cliente de monitoreo

+ En la vista en vivo, con la lista de cámaras por *Ubicación*, haga clic
  derecho en la ubicación (o selecciónela y presione #tecla("Mayús") +
  #tecla("F10")).
+ Elija _Armar total sus áreas de alarma…_, _Armar parcial sus áreas de
  alarma…_ o _Desarmar sus áreas de alarma…_. El mismo menú tiene _Abrir sus
  cámaras_.
+ Revise la lista de áreas y confirme.

Si la ubicación está fuera de lo que usted puede operar, las órdenes aparecen
deshabilitadas.

#nota[Cada área recibe la orden por separado y queda en la bitácora igual que
  si se hubiera dado desde el módulo de alarmas. Un panel con la tapa abierta
  (sabotaje) rechaza el armado de sus áreas; ciérrela antes de armar.]

#modulo("alarms")[Los paneles de alarma se explican en
  #capitulo(<cap-paneles-de-alarma>).]

== Cómo las ubicaciones limitan lo que ve cada usuario

Un rol puede valer en todo el sistema o solo en ciertas ubicaciones y recursos
sueltos, y a un usuario se le puede poner además un límite propio por
ubicación. Un usuario con el alcance limitado:

- ve en Recursos solo su parte del árbol: sus ubicaciones aparecen como
  primer nivel y no ve las de más arriba;
- ve listas, eventos, alarmas y video solo de los recursos de su alcance, en el
  panel web y en el cliente;
- no ve lo que está *Por ubicar*, salvo que uno de sus roles se lo dé como
  recurso suelto.

Si sus roles le permiten *ver el resto sin operarlo* (supervisión), ve todo,
pero los controles de lo que queda fuera de su alcance aparecen deshabilitados,
con el aviso _Fuera de su alcance: puede verlo, pero no operarlo._ El menú del
usuario muestra su alcance.

Cómo se arma el alcance de cada rol y de cada usuario se explica en
#capitulo(<cap-usuarios-y-roles>).

== Mensajes frecuentes

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [_Ya hay una ubicación "…" en …_],
  [Hay otra ubicación con ese nombre en el mismo lugar del árbol. Use otro nombre.],
  [_"…" tiene N sububicación(es): muévalas o elimínelas antes._],
  [Vacíe la ubicación de sububicaciones antes de eliminarla.],
  [_"…" está en el alcance de N usuario(s) …_ o _de N rol(es) …_],
  [Quite la ubicación del límite de esos usuarios o del alcance de esos roles en
    _Seguridad_ y vuelva a intentarlo.],
  [_El árbol admite hasta 8 niveles._],
  [Elija un destino más arriba en el árbol o reduzca la profundidad de esa rama.],
  [_Una ubicación no se puede mover dentro de sí misma ni de sus sububicaciones._],
  [Elija un destino fuera de esa rama.],
  [_Primero cree una ubicación (botón «+ Nueva» del árbol)._],
  [No hay ubicaciones adonde mover los recursos. Cree una primero.],
  [_Ese recurso ya no existe: se eliminó su equipo o una revalidación ya no lo trae._],
  [La ficha es de un recurso que el equipo ya no informa. Vuelva a la lista.],
  [_No hay áreas de alarma en …_],
  [La ubicación no tiene áreas habilitadas: ubique ahí las áreas del panel.],
  [_Ese recurso está fuera de su alcance (las ubicaciones y recursos de sus roles)._],
  [Pida a quien administra los roles que amplíe su alcance.],
  [_Su rol no le permite hacer esto (requiere "Órdenes por ubicación")._],
  [Sus roles no incluyen ese permiso. Pídalo a quien administra los roles.],
)
