#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/access-monitor.js, src/TrueCentralVms.Server/wwwroot/access-catalog.js, src/TrueCentralVms.Server/wwwroot/enroll.js, src/TrueCentralVms.Server/wwwroot/access.js, src/TrueCentralVms.Server/wwwroot/index.html, src/TrueCentralVms.Client/ViewModels/MainViewModel.Locations.cs, src/TrueCentralVms.WebControl/MainWindow.xaml, src/TrueCentralVms.WebControl/App.xaml.cs, installer/complemento.iss

= Control de acceso <cap-control-de-acceso>

El módulo de control de acceso reúne en el panel web las puertas de los
terminales y controladoras de acceso. Muestra su estado en vivo, permite
abrirlas, dejarlas abiertas o bloquearlas a distancia y guarda cada paso por
ellas. También decide quién puede entrar, por dónde y a qué hora: usted carga
las personas con sus credenciales (tarjeta, huella, rostro y clave de
teclado), los horarios y los niveles de acceso, y el sistema los escribe solo
en los equipos. Los registros se buscan con filtros y se exportan en PDF o
Excel.

#captura-pendiente("Panel web: Control de acceso › Monitoreo — tarjetas de puertas en distintos estados (normal, mantenida abierta, bloqueada, sin conexión), la barra de órdenes por lote y, abajo, la tabla Lo que va pasando con el panel Último acceso")

== Cómo se organiza el módulo

Quién entra por cada puerta se decide con tres piezas:

- Un *horario* dice _cuándo_ se puede pasar (por ejemplo, de lunes a viernes de
  08:00 a 18:00). Por sí solo no da permiso.
- Un *nivel de acceso* junta _puertas_ con un horario: "por acá y a estas
  horas".
- A cada *persona* se le asignan uno o más niveles. Una persona sin niveles no
  entra por ninguna puerta.

Las páginas del módulo están en el menú lateral:

#table(
  columns: (auto, 1fr),
  table.header[Menú][Para qué sirve],
  [#menu("Control de acceso", "Monitoreo")], [Estado de las puertas, órdenes a distancia y los accesos en vivo.],
  [#menu("Control de acceso", "Personas")], [El padrón: quién es cada persona, con qué se identifica y por dónde pasa.],
  [#menu("Control de acceso", "Niveles de acceso")], [Qué puertas y en qué horario.],
  [#menu("Control de acceso", "Horarios")], [Los días y horas en que se puede pasar.],
  [#menu("Control de acceso", "Registros de acceso")], [Búsqueda en el historial y reportes en PDF y Excel.],
  [#menu("Dispositivos", "Control de acceso")], [Los terminales y controladoras (para administradores).],
)

#nota[Las personas, sus credenciales y sus horarios se escriben en los equipos
  Hikvision. En los equipos Dahua y ZKTeco las personas se cargan en el propio
  equipo.]

== Ver el estado de las puertas

#requiere("Ver control de acceso")

Abra #menu("Control de acceso", "Monitoreo"). Cada puerta es una tarjeta con:

- arriba, el nombre del equipo y su ubicación, y debajo el nombre de la puerta;
- un ícono grande cuyo color resume el estado (ver la tabla);
- su modo: *Normal*, *Mantenida abierta*, *Bloqueada* o *Modo sin informar*
  (el equipo no lo informa). Si el equipo no responde, en su lugar dice
  *Equipo sin conexión*, y si la puerta está pausada, *Puerta pausada*;
- el estado de la hoja (*Hoja abierta* o *Hoja cerrada*) y de la cerradura
  (*Cerradura trabada* o *Cerradura liberada*). Si el equipo no informa uno de
  ellos, se muestra con una raya (*Hoja: —*, *Cerradura: —*).

#table(
  columns: (auto, 1fr),
  table.header[Color del ícono][Significa],
  [Verde], [Cerrada y en modo normal.],
  [Ámbar], [La hoja quedó abierta, la cerradura está liberada o la puerta está mantenida abierta.],
  [Rojo], [Bloqueada: no entra nadie.],
  [Gris], [El equipo está sin conexión.],
)

Debajo de la barra de herramientas, una línea resume todas las puertas: cuántas
hay, cuántas sin conexión, con la hoja abierta, mantenidas abiertas y
bloqueadas. Los estados se actualizan solos.

Para encontrar una puerta:

- En la lista del filtro elija *Todas las puertas*, *Equipo en línea*, *Equipo
  sin conexión*, *Hoja abierta*, *Hoja cerrada*, *Cerradura liberada*,
  *Mantenidas abiertas* o *Bloqueadas*.
- En *Buscar puerta o equipo…* escriba parte del nombre de la puerta, del
  equipo o de la ubicación.

Las puertas aparecen solas cuando un administrador agrega su equipo (ver
_Agregar un equipo de control de acceso_, más adelante). Mientras no haya
ninguno, la página lo indica.

#nota[Si su usuario está limitado a algunas ubicaciones, puede ver puertas que
  no puede operar: sus botones aparecen deshabilitados con el aviso _Fuera de su
  alcance: puede verlo, pero no operarlo._ El alcance lo define el
  administrador; ver #capitulo(<cap-usuarios-y-roles>).]

== Abrir, cerrar o bloquear una puerta

#requiere("Abrir y cerrar puertas")

Cada tarjeta trae los botones de las órdenes:

#table(
  columns: (auto, 1fr),
  table.header[Botón][Qué hace],
  [#boton("Abrir")], [Pulso de apertura: la puerta abre y se cierra sola.],
  [#boton("Mantener abierta")], [Deja la puerta abierta hasta nueva orden: pasa cualquiera sin identificarse.],
  [#boton("Normal")], [Vuelve al modo normal: abre solo con una credencial válida.],
  [#boton("Bloquear")], [Bloquea la puerta: no entra nadie, ni con una credencial válida.],
)

+ Ubique la tarjeta de la puerta.
+ Haga clic en la orden.
+ Si eligió #boton("Mantener abierta") o #boton("Bloquear"), confirme el
  mensaje que explica lo que va a pasar.

Mientras la orden viaja, el botón dice *Enviando…*. Al terminar, un aviso lo
confirma (por ejemplo, _Puerta "Acceso principal" abierta._) y la tarjeta
muestra el estado nuevo.

Los botones aparecen deshabilitados cuando el equipo está sin conexión, cuando
la puerta está pausada o cuando el equipo no acepta órdenes a distancia (en la
lista de equipos no tiene la función *Apertura remota*).

=== Dar una orden a varias puertas a la vez

+ Marque la casilla de cada puerta, arriba a la izquierda de su tarjeta. Con
  *Seleccionar todas* se marcan todas las puertas visibles que se pueden
  operar.
+ La barra superior indica cuántas eligió (por ejemplo, _3 seleccionadas:_).
  Haga clic en la orden: #boton("Abrir"), #boton("Mantener abiertas"),
  #boton("Normal") o #boton("Bloquear").
+ Confirme la orden. Con más de una puerta, el sistema siempre pide
  confirmación.

La orden se envía puerta por puerta: si un equipo la rechaza, las demás se
cumplen igual. Al final, un aviso indica cuántas quedaron como se pidió y, si
alguna falló, cuál y por qué.

#consejo[Para devolver a la normalidad todas las puertas que quedaron abiertas,
  elija el filtro *Mantenidas abiertas*, marque *Seleccionar todas* y haga clic
  en #boton("Normal").]

== Seguir los accesos en vivo

#requiere("Ver control de acceso")

Debajo de las tarjetas, la tabla *Lo que va pasando* muestra los últimos
accesos a medida que ocurren, con el más reciente arriba y resaltado al
llegar.

#table(
  columns: (auto, 1fr),
  table.header[Columna][Contenido],
  [*Persona*], [Foto del rostro cargado en su ficha (o sus iniciales), nombre e identificador o tarjeta.],
  [*Área*], [Departamento de la persona.],
  [*Evento*], [Lo que informó el equipo, por ejemplo _Puerta forzada_.],
  [*Credencial*], [Con qué se identificó: Tarjeta, Huella, Rostro, Clave, Remoto, Botón de salida, Código QR o Patente.],
  [*Punto de acceso*], [Puerta y equipo.],
  [*Hora*], [Fecha y hora del evento.],
  [*Resultado*], [Acceso concedido, Acceso denegado, Puerta abierta, Puerta cerrada, Alarma u Otro.],
)

- *Eventos del sistema* muestra también los eventos de tipo _Otro_:
  operaciones y avisos del equipo que no son pasos por la puerta. Por omisión
  están ocultos.
- *Pausar* congela la lista para leerla con calma. Al desmarcarlo se pone al
  día.
- #boton("Buscar registros") lleva a los registros de acceso.

A la derecha, el panel *Último acceso* muestra en grande la foto de la última
persona que pasó o fue rechazada, para compararla con quien está en la puerta,
junto con su ID, área, cargo, tarjeta, credencial, puerta, hora y evento.

- Haga clic en una fila de la tabla para fijarla en el panel (*Registro
  seleccionado*). Para volver a seguir los accesos nuevos, haga clic de nuevo
  en la fila o en #boton("Seguir al último").
- #boton("Ver sus registros") abre los registros de acceso de esa persona de
  los últimos 7 días.

== Alarmas de puerta en el cliente de monitoreo

#requiere("Ver control de acceso")

Cuando una puerta informa una alarma (por ejemplo, _Puerta forzada_ o _Puerta
mantenida abierta demasiado tiempo_), el cliente de monitoreo de escritorio:

- muestra en la barra de estado el mensaje *ALARMA DE PUERTA*, con la puerta,
  el equipo y la descripción;
- abre un aviso flotante *Alarma de puerta · nombre de la puerta* con la
  descripción, el equipo y la hora. Si la misma puerta vuelve a avisar dentro
  del mismo minuto, no se abre otro aviso;
- si la ficha de la puerta tiene consignas o cámaras asociadas, abre además la
  ventana *Verificación del aviso*, con las consignas para el operador y las
  cámaras de la puerta. #boton("Abrir en la Vista en vivo") lleva esas cámaras
  a la grilla principal.

Solo se avisan las alarmas recientes: las que el equipo entrega con atraso al
reconectarse no se repiten. Las consignas y cámaras de cada puerta se
configuran en su ficha de Recursos; ver #capitulo(<cap-recursos-y-ubicaciones>).

#nota[Las órdenes a las puertas (abrir, mantener abierta, bloquear) se dan
  desde el panel web.]

== Buscar en los registros de acceso

#requiere("Ver control de acceso")

Abra #menu("Control de acceso", "Registros de acceso"). A la izquierda están los
filtros y a la derecha los resultados.

#captura-pendiente("Panel web: Control de acceso › Registros de acceso — filtros a la izquierda con el período Últimos 7 días y dos puertas marcadas, la tabla de resultados con fotos, y arriba a la derecha el selector de reporte con los botones Excel y PDF")

#table(
  columns: (auto, 1fr),
  table.header[Filtro][Uso],
  [*Período*], [Hoy, Ayer, Últimos 7 días, Últimos 30 días, Este mes, Mes anterior o Personalizado. Con Personalizado aparecen *Desde* y *Hasta*, con fecha y hora.],
  [*Puntos de acceso*], [Las puertas agrupadas por equipo: marque un equipo entero o puertas sueltas. *Todas* quita el filtro. El cuadro *Buscar puerta…* acorta la lista.],
  [*Resultado*], [Acceso concedido, Acceso denegado, Puerta abierta, Puerta cerrada, Alarma u Otro.],
  [*Credencial*], [Tarjeta, Huella, Rostro, Clave, Remoto, Botón de salida, Código QR o Patente.],
  [*Área*], [El departamento de las personas. Solo aparece si hay personas con departamento.],
  [*Buscar por*], [Elija *Todo*, *Persona*, *ID* o *Tarjeta* y escriba el texto. Con *Todo* se busca también en el detalle del evento.],
)

+ Elija los filtros.
+ Haga clic en #boton("Buscar"), o presione #tecla("Enter") en el cuadro de
  texto.
+ Para volver a los filtros iniciales (registros de hoy), haga clic en
  #boton("Limpiar").

Sobre la tabla se indica cuántos registros encontró y el rango de fechas. La
tabla muestra *Persona*, *ID*, *Tarjeta*, *Área*, *Fecha y hora*, *Punto de
acceso*, *Credencial*, *Resultado* y *Detalle*.

- Haga clic en el título de *Persona*, *ID*, *Tarjeta*, *Fecha y hora*, *Punto
  de acceso* o *Resultado* para ordenar por esa columna; otro clic invierte el
  orden.
- Haga clic en el nombre de una persona para ver solo sus registros. Su nombre
  queda como etiqueta bajo *Buscar por*; la cruz de la etiqueta quita ese
  filtro.
- Abajo elija cuántos registros ver por página (50, 100, 200 o 500) y avance
  con #boton("‹ Anterior") y #boton("Siguiente ›").

Si el período llega hasta el momento actual (por ejemplo, *Hoy*) y está en la
primera página, la tabla se actualiza sola cada 15 segundos.

#nota[Cada usuario ve solo los registros de las puertas de su alcance, tanto en
  la tabla como en los reportes.]

== Exportar un reporte de accesos

#requiere("Exportar registros de acceso")

Los reportes se generan con los mismos filtros de la búsqueda, así que contienen
lo que usted ve en pantalla.

#table(
  columns: (auto, 1fr),
  table.header[Reporte][Contenido],
  [*Detalle de registros*], [Cada registro, tal como se ve en la tabla.],
  [*Asistencia por persona y día*], [Primer y último acceso concedido de cada persona por día, permanencia, cantidad de accesos y rechazos, y puertas usadas.],
  [*Resumen por puerta*], [Por cada puerta: concedidos, denegados, aperturas y cierres, alarmas, personas distintas y último registro.],
)

+ Ajuste los filtros.
+ En la lista de la esquina superior derecha, elija el reporte. Debajo aparece
  una breve explicación de su contenido.
+ Haga clic en #boton("Excel") o en #boton("PDF"). El archivo se descarga con
  los filtros que tenga el formulario en ese momento.

El reporte indica en su encabezado quién lo generó, cuándo y con qué filtros.
El detalle en PDF incluye hasta 5.000 registros y en Excel hasta 50.000; si hay
más, el reporte lo advierte (_Se incluyen los primeros … de … registros: acote
los filtros para ver el resto._).

== Las personas

#requiere("Ver personas")

Abra #menu("Control de acceso", "Personas") para ver el padrón.

#captura("web-acceso-personas.png", pie: [Personas: filtros, credenciales y estado en los equipos.])

Para encontrar a alguien, use los filtros *Buscar* (nombre, identificador o
tarjeta), *Departamento*, *Nivel de acceso* y *En los equipos*, y haga clic en
#boton("Buscar"). #boton("Limpiar") los quita.

La tabla muestra 50 personas por página:

#table(
  columns: (auto, 1fr),
  table.header[Columna][Contenido],
  [*Persona*], [Nombre y cargo. La etiqueta _desactivada_ indica que no está activa.],
  [*Identificador*], [Número con que la reconocen los equipos.],
  [*Departamento*], [Su área.],
  [*Credenciales*], [Lo que tiene cargado, por ejemplo _1 tarjeta · 2 huellas · rostro · clave_.],
  [*Niveles de acceso*], [Sus niveles. La etiqueta roja _ninguno_ advierte que no entra por ninguna puerta.],
  [*Vigencia*], [Fecha en que termina su vigencia (al pasar el puntero se ve desde cuándo rige).],
  [*En los equipos*], [Si ya quedó escrita en sus equipos (ver la tabla siguiente).],
)

#table(
  columns: (auto, 1fr),
  table.header[Estado][Significa],
  [*Al día*], [Quedó escrita en todos los equipos donde le corresponde estar.],
  [*Pendiente*], [Falta escribirla en algún equipo: hubo un cambio reciente o el equipo está sin conexión. Se escribe sola.],
  [*Con problemas*], [Algún equipo no aceptó lo que se le mandó.],
  [*Sin niveles*], [No hay equipos donde escribirla: no tiene niveles de acceso, está desactivada o su vigencia terminó.],
)

Cuando la persona tiene equipos, el estado es un enlace: haga clic en él para
ver qué pasó en cada equipo (ver _Escritura en los equipos_, más adelante).

== Agregar o editar una persona

#requiere("Administrar personas")

La ficha de una persona se completa en tres pasos: *Datos de la persona* (quién
es), *Credenciales* (con qué se identifica) y *Accesos* (por dónde y cuándo
pasa).

+ Haga clic en #boton("Agregar persona"), o en #boton("Editar") en la fila de
  la persona.
+ Complete el paso *Datos de la persona* y haga clic en #boton("Siguiente »").
+ Cargue sus credenciales y haga clic en #boton("Siguiente »").
+ Marque sus niveles de acceso.
+ Haga clic en #boton("Agregar persona") (o #boton("Guardar cambios") si la
  está editando).

Nada se guarda hasta el final: #boton("Cancelar") descarta todo, también las
huellas recién capturadas. Al editar, puede saltar entre pasos haciendo clic en
sus títulos y guardar desde cualquiera de ellos. Al guardar se revisan los tres
pasos; si falta algo, el formulario vuelve al paso con el problema y lo explica.

Al guardar, la persona queda *Pendiente* y el sistema la escribe en los equipos
en segundo plano (_Persona agregada. Se está escribiendo en los equipos._).

=== Paso 1: Datos de la persona

#table(
  columns: (auto, 1fr),
  table.header[Campo][Uso],
  [*Nombre*, *Apellido*], [Obligatorios.],
  [*Identificador en los equipos*], [Número con que los equipos reconocen a la persona. Al crearla es opcional: si lo deja vacío, el sistema le asigna el siguiente número libre. Después no se puede cambiar.],
  [*Departamento*], [Su área (Operaciones, Casino…). Se usa en los filtros y aparece como *Área* en los registros.],
  [*Cargo*, *Teléfono*, *Correo*, *Notas*], [Opcionales.],
  [*Vigencia*], [Fecha y hora de inicio y de término. Los equipos la respetan solos: fuera de ese período no la dejan pasar. Una persona nueva parte vigente desde hoy a las 00:00:00 hasta el 31-12-2099 a las 23:59:59.],
  [*Activa*], [Al desmarcarla, la persona se borra de los equipos, pero se conserva su historial.],
)

#importante[Los terminales Hikvision aceptan vigencias solo hasta el
  31-12-2037. Si la vigencia termina después, en el equipo queda hasta ese día;
  en la ficha se conserva la fecha que usted escribió.]

=== Paso 2: Credenciales

#captura("web-acceso-persona-credenciales.png", ancho: 85%, pie: [Paso Credenciales: tarjetas, clave de teclado, huellas y rostro.])

- *Tarjetas*: #boton("+ tarjeta") agrega una fila para escribir el número tal
  como lo lee el equipo. Para no escribirlo a mano, use
  #boton("Leer tarjeta en un lector") (ver _Leer una tarjeta en el lector de un
  equipo_). La cruz de cada fila (_Quitar esta tarjeta_) la elimina. Una
  tarjeta no puede estar en dos personas a la vez.
- *Clave de teclado*: de 4 a 8 dígitos, solo para terminales con teclado. Al
  editar, déjela vacía para no cambiarla, o marque *Quitarle la clave*.
- *Huellas*: se capturan con un lector USB conectado a su PC; ver _Capturar
  las huellas_.
- *Rostro*: una foto para los terminales con reconocimiento facial; ver
  _Cargar la foto del rostro_.

=== Paso 3: Accesos

Marque los niveles de acceso de la persona; junto a cada uno se ve su horario.
Debajo, un resumen dice por qué puertas va a entrar y con qué credenciales, o
advierte que queda *sin ningún nivel de acceso*. Si todavía no hay niveles,
créelos primero en #menu("Control de acceso", "Niveles de acceso").

Al editar, este paso muestra también la tabla *En los equipos*, con el estado y
la última escritura en cada equipo.

== Leer una tarjeta en el lector de un equipo

#requiere("Administrar personas")

Para no copiar a mano el número de una tarjeta, el sistema puede leerlo en el
lector de un terminal:

+ En el paso *Credenciales*, haga clic en #boton("Leer tarjeta en un lector").
+ En *¿En qué equipo?*, elija el terminal donde va a pasar la tarjeta. La lista
  muestra solo los equipos en línea que pueden leer una tarjeta a pedido.
+ Haga clic en #boton("Esperar tarjeta").
+ Pase la tarjeta por el lector de ese equipo.

El número se agrega a la lista, la ventana se cierra y un aviso lo confirma
(_Tarjeta … leída._). La ventana espera hasta que pase una tarjeta o hasta que
haga clic en #boton("Cancelar"). Si la tarjeta ya estaba en la lista, lo avisa
y sigue esperando otra.

#nota[Solo los equipos Hikvision leen una tarjeta a pedido; los Dahua y ZKTeco
  no aparecen en la lista. Si no hay ninguno en línea, el sistema avisa _No hay
  equipos en línea que puedan leer una tarjeta (solo los Hikvision lo hacen).
  Escriba el número con «+ tarjeta»._]

== Capturar las huellas

#requiere("Administrar personas")

Para enrolar huellas necesita:

- un lector USB de huellas Hikvision, como el DS-K1F820-F, conectado al PC
  donde está usando el panel web;
- el complemento de enrolamiento instalado en ese mismo PC (ver _Instalar el
  complemento de enrolamiento_).

En el paso *Credenciales*, el campo *Huellas* muestra cuántas tiene la persona
(por ejemplo, _2 de 10_) y un dibujo de las dos manos, con las palmas hacia
abajo. Cada dedo muestra su estado según la leyenda: *enrolada*, *recién
capturada*, *sin huella* o *calidad para mejorar*. Al pasar el puntero sobre
un dedo se ven su nombre y su calidad. A la derecha, la ficha del dedo elegido
muestra su estado, una barra de calidad y los botones #boton("Capturar") (o
#boton("Recapturar")) y #boton("Quitar").

+ Haga clic en un dedo del dibujo. El formulario sugiere uno: primero los
  índices, después los medios y los pulgares, alternando las manos.
+ Haga clic en #boton("Capturar"), o haga doble clic en el dedo.
+ En la ventana *Capturar huella*, verifique que esté elegido *Lector USB de
  huellas* y que el recuadro diga *Complemento activo en* y el nombre de su PC.
  Si hay más de un lector conectado, elíjalo en *Lector*. Con
  #boton("Probar lector") puede comprobar que responde.
+ Haga clic en #boton("Iniciar captura").
+ Siga las instrucciones de la ventana: la persona apoya el dedo, lo levanta y
  lo vuelve a apoyar hasta completar las capturas (_Captura 1 de 3_, …).
  Siempre el mismo dedo, centrado y sin moverlo.
+ Cuando aparezca *Huella capturada* con su calidad, haga clic en
  #boton("Usar esta huella"), o en #boton("Repetir captura") si la calidad es
  baja.
+ Repita con los demás dedos. Si la calidad fue buena, el formulario ya deja
  elegido el siguiente dedo sugerido.
+ Guarde la persona: las huellas nuevas quedan marcadas _nueva_ y se guardan al
  terminar.

#table(
  columns: (auto, 1fr),
  table.header[Calidad][Qué hacer],
  [60 a 100 (buena)], [Nada: alcanza para un reconocimiento confiable.],
  [40 a 59 (regular)], [Conviene recapturarla con el dedo limpio y bien centrado.],
  [Menos de 40 (baja)], [Recapturarla.],
)

Para cancelar una captura en curso, haga clic en #boton("Cancelar captura") o
presione #tecla("Esc"). Para borrarle una huella a la persona, elija el dedo,
haga clic en #boton("Quitar") y guarde.

#consejo[Enrole al menos dos dedos, uno de cada mano: si se lastima uno, la
  persona igual puede pasar.]

#nota[No todos los terminales tienen lector de huella. A un equipo sin lector no
  se le mandan huellas: la persona queda al día en él con sus demás
  credenciales, y el detalle de la escritura lo indica (_No lleva sus huellas
  porque el equipo no tiene lector de huellas._). Si un terminal declara la
  función pero no tiene sensor, el sistema lo detecta al escribirle y deja de
  enviarle huellas.]

Si la captura falla, la ventana muestra *No se pudo capturar la huella* con el
motivo y los botones #boton("Cerrar") y #boton("Reintentar"):

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [_No se encontró el complemento en este equipo._], [Instale el complemento en este PC (la misma ventana ofrece descargarlo) y haga clic en #boton("Buscar de nuevo").],
  [_No se pudo comunicar con el lector: revise que esté conectado por USB y que ningún otro programa (SADP, iVMS, otra pestaña) lo tenga tomado._], [Revise el cable y cierre el otro programa o la otra pestaña que esté usando el lector.],
  [_Se agotó el tiempo de espera: no se apoyó el dedo a tiempo._], [Vuelva a iniciar la captura y apoye el dedo apenas lo pida.],
  [_No se pudo generar la plantilla: limpie el sensor y repita apoyando bien el mismo dedo._], [Limpie el sensor y el dedo, y repita.],
  [_Se perdió la comunicación con el complemento: …_], [El complemento se cerró. Ábralo de nuevo desde el menú Inicio de Windows (*CLR TrueCentral VMS Complemento*) y reintente.],
)

== Instalar el complemento de enrolamiento

El panel web no puede comunicarse con un lector conectado por USB al PC del
operador. De eso se encarga el *complemento de enrolamiento*: un programa
pequeño para Windows que se instala en el PC donde está el lector (normalmente
el de recursos humanos o el de portería, no el servidor). Queda en la bandeja
del sistema, arranca con Windows y solo atiende pedidos del propio PC (en
`127.0.0.1`, puertos 5081, 25471 o 25472).

Para obtener el instalador:

- en el panel web, abra el menú de su usuario (arriba a la derecha) y haga clic
  en *Descargar el complemento*; o
- en la ventana *Capturar huella*, si no encuentra el complemento, haga clic en
  #boton("Descargar el complemento").

Si el servidor no lo tiene, aparece _El instalador del complemento no está
publicado en este servidor (carpeta webcontrol)._: pídaselo al administrador.

Para instalarlo:

+ Ejecute `CLRTrueCentralVMS-Complemento-Setup-<versión>.exe`. No necesita
  permisos de administrador: se instala para el usuario de Windows que lo
  ejecuta.
+ Deje marcada la opción *Iniciar el complemento junto con Windows*.
+ Al final, deje marcada *Iniciar el complemento ahora* y termine.
+ Conecte el lector. En la bandeja del sistema aparece el ícono *CLR
  TrueCentral VMS — Complemento* con el aviso _Listo para enrolar huellas desde
  el panel web._

Desde el ícono de la bandeja:

- Doble clic o *Estado del web control…* abre la ventana *CLR TrueCentral VMS —
  Complemento de enrolamiento*, con su estado, la versión del SDK del lector de
  huellas, los *Lectores detectados*, la *Actividad* y los botones
  #boton("Actualizar lectores") y #boton("Probar lector"). Cerrar esta ventana
  no detiene el complemento.
- *Salir* cierra el complemento. Si hay una captura en curso, pide
  confirmación.

Para actualizarlo, ejecute el instalador nuevo encima: cierra solo el
complemento en uso. Se desinstala desde las aplicaciones de Windows, como *CLR
TrueCentral VMS — Complemento de enrolamiento*.

#importante[El complemento tiene que estar en el mismo PC donde se abre el panel
  web para enrolar. Si abre el panel en otro equipo, la ventana de captura no lo
  encuentra.]

== Cargar la foto del rostro

#requiere("Administrar personas")

Los terminales con reconocimiento facial arman el modelo del rostro a partir de
una foto.

+ En el paso *Credenciales*, junto a *Rostro*, haga clic en
  #boton("Elegir foto") y elija el archivo.
+ Revise la vista previa: lleva la etiqueta _nueva_ y la medida en píxeles.
+ Guarde la persona.

Para reemplazarla use #boton("Cambiar foto"); para quitarla, la cruz _Quitarle
el rostro_ (se quita al guardar).

La foto debe ser:

- JPG o PNG, de hasta 2 MB;
- de frente, con la cara despejada y bien iluminada, sin lentes oscuros ni
  gorro, y ocupando buena parte de la imagen;
- de al menos 300 píxeles de lado. Si es más chica, el panel avisa que el
  terminal podría rechazarla.

Al mandar la foto a los terminales, el sistema la ajusta solo, sin modificar la
que queda guardada en la ficha:

- endereza las fotos que el teléfono guardó giradas;
- la convierte a JPEG estándar (algunos terminales rechazan el JPEG
  "progresivo", como el que entrega WhatsApp);
- reduce las fotos grandes y agranda las muy chicas;
- la deja bajo el peso que aceptan los terminales (unos 200 KB).

#nota[Si aun así el terminal no encuentra una cara, la persona queda *Con
  problemas* y el detalle dice _El equipo no pudo reconocer una cara en la foto
  de …_. Cargue otra foto que cumpla los requisitos. La foto solo se manda a los
  terminales con reconocimiento facial; en los demás, el detalle indica _No
  lleva su rostro porque el equipo no reconoce rostros._]

== Escritura en los equipos

Cada cambio en personas, niveles u horarios se escribe en los equipos en
segundo plano: el panel no espera a que respondan. En la parte superior de
#menu("Control de acceso", "Personas"), una franja muestra el avance:

- *Escribiendo en los equipos*: cuántas personas lleva, cuánto falta y a quién
  está escribiendo en qué equipo;
- *Pendiente de escribir en los equipos*: cuántas faltan. _Se escriben solas en
  la próxima pasada (o pulse «Escribir pendientes»)._;
- *Escritura terminada*.

Si hay personas con error, el enlace _… con error_ filtra la tabla para verlas.
La franja desaparece cuando no queda nada pendiente ni con error.

El sistema vuelve a intentar solo: lo pendiente, cada pocos minutos; a una
persona que un equipo rechazó, cada vez más espaciado (hasta una vez por hora).
Un equipo sin conexión deja a la persona *Pendiente*, no *Con problemas*.

=== Ver qué pasó en cada equipo

#requiere("Ver personas")

Haga clic en el estado de la persona en la columna *En los equipos* (por
ejemplo, _Con problemas ›_). La ventana *Escritura en los equipos* muestra
arriba qué credenciales lleva, su vigencia y sus niveles, y debajo un bloque
por equipo, primero los que fallaron:

- el estado en ese equipo y la *Última escritura correcta*;
- qué pasó, en palabras;
- *Respuesta del equipo*: el texto técnico tal como lo devolvió el equipo, útil
  para soporte.

Corrija lo que indique el equipo (por ejemplo, cambie la foto) y haga clic en
#boton("Volver a escribir"). #boton("Editar persona") abre su ficha.

=== Volver a escribir en los equipos

#requiere("Administrar personas")

- #boton("Reintentar"), en la fila de una persona pendiente o con problemas, la
  escribe de nuevo ahora en todos sus equipos.
- #boton("Escribir pendientes") escribe ahora todo lo pendiente, sin esperar el
  reintento automático.
- #boton("Reenviar todo") reescribe el padrón completo en todos los equipos,
  aunque el sistema los dé por al día. Úselo cuando un equipo perdió lo suyo
  sin que el sistema se enterara: se reemplazó el terminal, se restableció de
  fábrica o alguien borró personas desde su pantalla. Con muchas personas puede
  demorar.

Mientras hay una escritura en curso, estos botones esperan a que termine. Para
reenviar el padrón a un solo equipo, ver _Reenviar el padrón a un equipo_, más
adelante.

== Desactivar o eliminar una persona

#requiere("Administrar personas")

- *Desactivar*: en el paso *Datos de la persona*, desmarque *Activa* y guarde.
  La persona se borra de los equipos, pero conserva su ficha, sus credenciales
  y su historial, y puede reactivarse.
- *Eliminar*: haga clic en #boton("Eliminar") en su fila y confirme. Se borra
  del padrón y de los equipos donde esté. Sus registros de acceso se conservan.

#importante[Si al eliminar aparece _La persona se eliminó del VMS, pero no se
  pudo borrar de estos equipos y podría seguir entrando por ellos: …_, revise
  esos equipos (por ejemplo, desde su propia página web): mientras la persona
  siga cargada en ellos, puede seguir entrando.]

== Horarios

#requiere("Ver personas")

Abra #menu("Control de acceso", "Horarios"). La tabla muestra cada horario con
*Cuándo deja pasar* (por ejemplo, _Lun a Vie 08:00-18:00_), los *Niveles que lo
usan* y su *Ranura*, el número que ocupa en los equipos.

El horario *Todo el día, todos los días (24/7)* viene con el sistema (etiqueta
_de fábrica_) y no se puede editar ni eliminar.

=== Crear o editar un horario

#requiere("Administrar personas")

#captura("web-acceso-horario.png", ancho: 85%, pie: [Crear horario: un día por fila, con sus tramos.])

+ Haga clic en #boton("Crear horario"), o en #boton("Editar") en la fila del
  horario.
+ Escriba el *Nombre* (por ejemplo, _Turno de noche_) y, si quiere, una
  *Descripción*.
+ En *Cuándo deja pasar* hay una fila por día, de lunes a domingo. Un horario
  nuevo parte con lunes a viernes de 08:00 a 18:00. En cada día:
  - ajuste las horas de cada tramo;
  - #boton("+ tramo") agrega otro tramo a ese día;
  - la cruz _Quitar este tramo_ lo elimina;
  - #boton("Copiar a todos") copia los tramos de ese día a los otros seis.
+ Haga clic en #boton("Crear") (o #boton("Guardar cambios")).

Reglas:

- hasta 8 tramos por día, que no se pueden pisar (si se pisan, júntelos en uno
  solo);
- un día sin tramos (_No deja pasar_) no deja pasar a nadie;
- el fin de cada tramo debe ser posterior a su comienzo.

Al modificar un horario en uso, el sistema reescribe en los equipos a todas las
personas que dependen de él. Un horario que usan niveles de acceso no se puede
eliminar: cámbieles antes el horario a esos niveles.

#nota[Los equipos guardan hasta 128 horarios distintos (la columna *Ranura*
  muestra el número de cada uno). Dos horarios con los mismos tramos comparten
  ranura, y una persona que llega a una puerta por dos niveles con horarios
  distintos ocupa una ranura con la suma de ambos. Si se agotan, junte niveles
  que usen el mismo horario o borre horarios que ya no se usan.]

== Niveles de acceso

#requiere("Ver personas")

Abra #menu("Control de acceso", "Niveles de acceso"). Cada nivel muestra su
*Horario*, sus *Puertas*, cuántas *Personas* lo tienen y su *Estado* (_Activo_ o
_Desactivado_). Alguien con dos niveles pasa por las puertas de los dos; si una
puerta le llega por ambos, vale la suma de sus horarios.

=== Crear o editar un nivel

#requiere("Administrar personas")

+ Haga clic en #boton("Crear nivel"), o en #boton("Editar") en la fila del
  nivel. El botón se habilita cuando hay al menos una puerta.
+ Escriba el *Nombre* (por ejemplo, _Bodega_), elija el *Horario* y, si quiere,
  una *Descripción*.
+ En *Puertas*, marque las del nivel. Están agrupadas por equipo. Una puerta
  pausada aparece como _(pausada)_ y no da permiso aunque se la incluya.
+ Deje marcado *Activo*. Desmarcarlo le quita el permiso a todos los que tienen
  el nivel, sin borrar su configuración.
+ Haga clic en #boton("Crear") (o #boton("Guardar cambios")).

Al eliminar un nivel, las personas que lo tenían pierden ese permiso y se les
quita de los equipos; el mensaje de confirmación indica cuántas son.

=== Asignar personas a un nivel

#requiere("Administrar personas")

+ En la fila del nivel, haga clic en #boton("Asignar personas").
+ En *Buscar*, escriba parte del nombre, identificador o departamento para
  acortar la lista.
+ Marque a quienes deben tener el nivel y desmarque a quienes no. Lo marcado se
  conserva aunque cambie la búsqueda.
+ Haga clic en #boton("Guardar").

También puede asignar niveles a una persona desde su ficha, en el paso
*Accesos*.

#nota[Esta lista muestra hasta 500 personas, ordenadas por apellido. Con un
  padrón más grande, asigne el nivel desde la ficha de cada persona.]

#modulo("automation")[
  #consejo[Las automatizaciones también pueden reaccionar a los accesos y dar
    órdenes a las puertas; ver #capitulo(<cap-automatizaciones>).]
]

== Agregar un equipo de control de acceso

#requiere("Equipos de acceso")

Los equipos se administran en #menu("Dispositivos", "Control de acceso"). El
sistema trabaja con estas marcas:

#table(
  columns: (auto, auto, 1fr),
  table.header[Marca / protocolo][Puerto][Credencial],
  [Hikvision DS-K], [80], [Usuario y contraseña del equipo.],
  [Dahua ASI/ASC], [80], [Usuario y contraseña del equipo.],
  [ZKTeco], [4370], [Clave de comunicación del equipo (Comm Key).],
)

Solo los equipos Hikvision reciben el padrón (personas, credenciales y
horarios), leen tarjetas a pedido y se configuran desde las pestañas *Puertas*,
*Lectores* y *Capacidades* de su página.

=== Desde la tabla de equipos en línea

Debajo de la lista, la tabla *Equipos en línea* muestra los equipos de control
de acceso compatibles que encuentra en la red del servidor (Hikvision DS-K,
Dahua ASI/ASC/ASG y ZKTeco). Se actualiza cada 30 segundos; #boton("Buscar")
la actualiza en el momento.

La columna *Estado* dice si el equipo es *Nuevo*, ya está *Agregado* o viene de
fábrica (*Sin activar* o *Sin inicializar*). Según el caso, la fila ofrece:

- #boton("Agregar"): abre el formulario de alta con la dirección, el puerto y la
  marca ya completos;
- #boton("Activar") (Hikvision) o #boton("Inicializar") (Dahua): le fija la
  contraseña de administrador a un equipo de fábrica;
- #boton("Cambiar IP"): le cambia la dirección de red sin entrar a él.

Activar e inicializar equipos y cambiarles la IP funciona igual que con las
cámaras; ver #capitulo(<cap-fuentes-de-video>).

#nota[La búsqueda solo ve el segmento de red del servidor: no cruza routers ni
  VPN. Un equipo fuera de ese segmento se agrega a mano con su dirección.]

=== Agregar un equipo a mano

+ Haga clic en #boton("Agregar equipo").
+ Escriba un *Nombre* que lo identifique (por ejemplo, _Portería principal_).
+ Elija su *Ubicación*. Sus puertas la heredan.
+ Elija la *Marca / protocolo*.
+ Escriba la *Dirección (IP o hostname)* y revise el puerto (*Puerto HTTP*, o
  *Puerto del equipo* en ZKTeco).
+ Complete la credencial:
  - Hikvision y Dahua: *Usuario del equipo* y *Contraseña* de un usuario local
    del equipo (normalmente _admin_, el mismo de su página web). Marque *Usar
    HTTPS (certificado autofirmado aceptado)* si el equipo lo exige.
  - ZKTeco: *Clave de comunicación*, la que tiene el equipo en Comunicación ›
    Seguridad (de fábrica, 0).
+ Deje marcado *Activo*: el sistema revisa su estado y sus puertas ocupan cupo
  de la licencia.
+ Si quiere comprobar los datos antes, haga clic en #boton("Probar conexión"):
  el recuadro *Conexión validada* muestra el modelo, tipo, número de serie,
  firmware, puertas, si acepta apertura remota y eventos, qué credenciales
  maneja y su cupo de personas y tarjetas.
+ Haga clic en #boton("Guardar").

Al guardar, el sistema se conecta con el equipo y lee su modelo, firmware,
capacidades y las puertas que administra. Si el equipo no responde o rechaza
las credenciales, no se guarda nada y el formulario muestra el motivo. Las
puertas aparecen solas en el monitoreo y en los niveles de acceso.

#importante[Los equipos Hikvision bloquean el inicio de sesión tras varios
  intentos fallidos, y cada intento extra alarga el bloqueo. Si el mensaje dice
  que quedan pocos intentos o que el equipo bloqueó el inicio de sesión,
  verifique el usuario y la contraseña antes de reintentar.]

#nota[Cada puerta activa de un equipo activo ocupa un cupo de la licencia. Si
  no alcanza, aparece _La licencia permite … puertas y ya hay … en uso. Amplíe
  la licencia para agregar más._ Pausar las puertas que no se usan libera
  cupo (ver _La página del equipo_).]

== La lista de equipos

#requiere("Equipos de acceso")

#table(
  columns: (auto, 1fr),
  table.header[Columna][Contenido],
  [*Nombre*], [Enlace a la página del equipo. La etiqueta _pausado_ indica que está desactivado.],
  [*Ubicación*], [Dónde está, o _Por ubicar_.],
  [*Marca*, *Tipo*], [Hikvision, Dahua o ZKTeco; Terminal, Controladora o Torniquete.],
  [*Dirección*, *Modelo*, *Firmware*], [Cómo se llega al equipo, su modelo con el número de serie y su firmware.],
  [*Puertas*], [Cuántas administra y sus nombres.],
  [*Funciones*], [Apertura remota, Eventos, Tarjeta, Huella y Rostro, según lo que el equipo informa.],
  [*Conexión*], [_En línea_, _Sin conexión_ o _Credenciales_ (rechazó el usuario o la contraseña), con el último error.],
)

El estado de conexión se actualiza solo. En cada fila:

- #boton("Revalidar") vuelve a leer del equipo su modelo, firmware,
  capacidades y puertas; úselo después de actualizar su firmware o cambiar sus
  puertas;
- #boton("Reenviar padrón") (solo Hikvision) le reescribe sus personas (ver la
  sección siguiente);
- #boton("Configurar") abre la página del equipo;
- #boton("Eliminar") lo borra junto con sus puertas, previa confirmación.

#importante[Al eliminar un equipo se borran también sus registros de acceso. Si
  solo quiere dejar de usarlo por un tiempo, desmarque *Activo* en su página.]

=== Reenviar el padrón a un equipo

#requiere("Equipos de acceso")

Cuando un equipo perdió sus personas sin que el sistema se enterara (se
reemplazó, se restableció de fábrica o alguien las borró desde su pantalla), el
sistema lo sigue dando por al día. Para reescribirlo:

+ En la fila del equipo, haga clic en #boton("Reenviar padrón").
+ Confirme el mensaje.

Se le reescriben todas las personas con permiso en sus puertas, con sus
credenciales y horarios. Al terminar, un aviso indica cuántas se procesaron.

== La página del equipo

#requiere("Equipos de acceso")

Haga clic en el nombre del equipo o en #boton("Configurar"). El encabezado
muestra la marca, el tipo, el modelo, el firmware, la dirección y la ubicación,
las funciones, la cantidad de puertas y el estado de conexión, con el botón
#boton("Revalidar"). Para regresar a la lista de equipos, use
#boton("← Volver a la lista").

#captura-pendiente("Panel web: página de un equipo de control de acceso, pestaña Conexión — encabezado con funciones y estado En línea, las pestañas Conexión, Puertas, Lectores, Capacidades y Hora y mantenimiento, y abajo la sección Puertas del equipo con una puerta pausada")

=== Conexión

Tiene los mismos datos del alta, con #boton("Probar conexión") y
#boton("Guardar cambios"). Deje la contraseña vacía para no cambiarla. Si cambia
la dirección, el puerto, el usuario o la contraseña, al guardar el sistema
vuelve a validar el equipo y relee sus datos y puertas.

Debajo, *Puertas del equipo* lista cada puerta con:

- #boton("Ficha en Recursos"): su ficha, donde se le cambia el nombre, la
  ubicación, las consignas y las cámaras (ver
  #capitulo(<cap-recursos-y-ubicaciones>)). Una vez renombrada ahí, revalidar el
  equipo no le devuelve el nombre de fábrica;
- *Activa*: al desmarcarla y confirmar, la puerta queda pausada. Deja de ocupar
  cupo de la licencia, se la quita de los niveles de acceso en los equipos y en
  el monitoreo aparece como _Puerta pausada_, sin órdenes.

=== Puertas y Lectores

Estas pestañas muestran los parámetros propios del equipo, tal como están en
él: se leen al abrir la pestaña y se escriben en el equipo al guardar. El
sistema no los guarda.

- *Puertas*: por ejemplo *Tiempo de apertura*, *Contacto de puerta (sensor
  magnético)*, *Botón de salida*, *Alarma de puerta abierta demasiado tiempo* o
  *Trabar la cerradura apenas se cierra la hoja*.
- *Lectores*: por ejemplo *Lector habilitado*, *Intervalo mínimo entre
  autenticaciones*, *Alarma por intentos fallidos*, *Detección de sabotaje
  (tamper)*, los umbrales del reconocimiento facial o *Antisuplantación facial
  (detección de vida)*.

Los campos dependen del modelo. Para cambiarlos:

+ Modifique los campos de una puerta o un lector. Aparece _Hay cambios sin
  guardar._
+ Haga clic en #boton("Guardar") de ese bloque.

El bloque se vuelve a mostrar con lo que el equipo dejó, que puede acotar o
redondear algún valor. Los campos de contraseña (*Código de coacción*,
*Contraseña maestra*, *Código de desbloqueo*) se dejan vacíos para no
cambiarlos. *Otros parámetros que informa el equipo* despliega, solo para
consulta, lo demás que entrega el equipo.

#nota[Solo los equipos Hikvision se configuran desde estas pestañas. Con otra
  marca aparece _Los equipos de esta marca todavía no se configuran desde el
  VMS: use la página web del equipo._]

=== Capacidades

Muestra lo que el equipo declara saber hacer, agrupado en *Padrón de personas*,
*Credenciales*, *Puertas y horarios*, *Eventos* y *Otras funciones que declara
el equipo*. Se lee al validar o revalidar el equipo: abrir la pestaña no le
pregunta nada. Después de actualizar el firmware, use #boton("Revalidar") para
releerla.

#table(
  columns: (auto, 1fr),
  table.header[Marca][Significa],
  [Visto], [El equipo declara que lo soporta.],
  [Cruz], [El equipo declara que no lo soporta.],
  [Signo de interrogación], [El equipo no lo declara: el sistema lo prueba al usarlo.],
)

#consejo[En *Eventos*, el punto *Suscripciones a sus eventos abiertas ahora*
  muestra qué otras plataformas están escuchando al equipo. Los equipos admiten
  pocas a la vez: si otra plataforma ocupa los cupos, los accesos pueden dejar
  de llegar en vivo al monitoreo.]

=== Hora y mantenimiento

#requiere("Ver hora y mantenimiento", "Mantenimiento de equipos")

Muestra la hora del equipo, su zona horaria y su desfase. Con el permiso
*Mantenimiento de equipos* aparecen #boton("Ajustar hora…") y
#boton("Reiniciar / restablecer…"). *Ver todos los equipos* lleva a
#menu("Dispositivos", "Hora y mantenimiento").

== Mensajes frecuentes

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [_Su rol no le permite hacer esto (requiere …)._], [Su usuario no tiene el permiso indicado. Pídaselo al administrador.],
  [_El equipo '…' no está en línea: …_], [El equipo no responde. Revise su red y su alimentación; la orden se puede dar cuando vuelva a estar en línea.],
  [_El equipo '…' está pausado._ / _La puerta está desactivada en el VMS._], [Un administrador debe activar el equipo o la puerta en la página del equipo.],
  [_Este equipo no acepta la orden «…» según sus propias capacidades (acepta: …)._], [Use una de las órdenes que el mensaje indica.],
  [_La tarjeta … ya es de …_], [Esa tarjeta pertenece a otra persona. Quítesela antes, o revise el número.],
  [_La clave de teclado son entre 4 y 8 dígitos._], [Escriba solo números, de 4 a 8.],
  [_El fin de la vigencia tiene que ser posterior a su comienzo._], [Corrija las fechas de *Vigencia*.],
  [_Ya hay una persona con el identificador '…'._], [Use otro identificador o déjelo vacío para que el sistema asigne uno.],
  [_El nivel de acceso necesita al menos una puerta._], [Marque al menos una puerta.],
  [_Hay tramos que se pisan el día …; júntelos en uno solo._], [Corrija las horas de ese día.],
  [_El horario lo usan … nivel(es) de acceso. Cámbieles el horario antes de borrarlo._], [Asigne otro horario a esos niveles y vuelva a eliminarlo.],
  [_Se agotaron las 128 ranuras de horario de los equipos. …_], [Junte niveles que usen el mismo horario o borre horarios que ya no se usan.],
  [_La foto tiene que ser JPG o PNG._ / _La foto pesa … KB; el máximo es 2048 KB._], [Use otra foto, en JPG o PNG y de hasta 2 MB.],
  [_No hay equipos de control de acceso en línea para leer la tarjeta._], [Espere a que un terminal esté en línea, o escriba el número a mano.],
  [_Ya existe un equipo de control de acceso con esa dirección y puerto._], [Ese equipo ya está agregado: búsquelo en la lista.],
  [_El módulo «Control de acceso» no está incluido en la licencia. …_], [La licencia no trae el módulo. Consulte con su proveedor.],
)
