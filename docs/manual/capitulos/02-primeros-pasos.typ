#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/LoginWindow.xaml, src/TrueCentralVms.Client/Views/MainWindow.xaml, src/TrueCentralVms.Client/Views/MainWindow.xaml.cs, src/TrueCentralVms.Client/Views/HomeView.xaml, src/TrueCentralVms.Client/Views/SettingsWindow.xaml, src/TrueCentralVms.Client/Views/SettingsWindow.xaml.cs, src/TrueCentralVms.Client/Views/ToastWindow.xaml, src/TrueCentralVms.Client/ViewModels/MainViewModel.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.Permissions.cs, src/TrueCentralVms.Client/Services/ApiClient.cs, src/TrueCentralVms.Server/wwwroot/index.html, src/TrueCentralVms.Server/wwwroot/app.js, src/TrueCentralVms.Server/wwwroot/license.js

= Primeros pasos <cap-primeros-pasos>

Este capítulo explica cómo ingresar al sistema, cómo moverse por el cliente de
monitoreo y por el panel web, y cómo cerrar la sesión. Lo que se hace dentro de
cada módulo está en su propio capítulo.

#captura-pendiente("Cliente de monitoreo: ventana principal en la página Inicio, con las tarjetas de módulos y la viñeta Vista en Vivo abierta")

== Ingresar al panel web

El panel web se abre desde un navegador actualizado (Chrome, Edge o Firefox)
en la dirección del servidor, que por omisión usa el puerto 5090; por ejemplo,
`http://192.168.1.10:5090`. Si no conoce la dirección, pídasela al
administrador del sistema.

#captura(
  "web-inicio-sesion.png",
  ancho: 62%,
  leyenda: none,
  pie: [Pantalla de ingreso del panel web.],
)

+ Escriba su nombre de usuario en #marca(1).
+ Escriba su contraseña en #marca(2). Para revisar lo que escribió, haga clic
  en el ojo #marca(3) y la contraseña se mostrará.
+ Haga clic en #boton("Ingresar") #marca(4).

#nota[El número de versión #marca(5) identifica la versión instalada en el
  servidor. Indíquelo siempre que pida soporte.]

== Si su contraseña venció

Por seguridad, el administrador puede exigir que las contraseñas se cambien
cada cierto tiempo. Cuando la suya vence, el panel web le pide una nueva al
ingresar, antes de dejarlo continuar:

+ Escriba su contraseña actual en *Contraseña actual*.
+ Escriba la nueva en *Contraseña nueva* y repítala en *Confirmar contraseña
  nueva*. La lista bajo el campo marca cada regla a medida que la cumple.
+ Haga clic en #boton("Cambiar contraseña").

La nueva contraseña debe tener:

- al menos 8 caracteres;
- una letra mayúscula y una minúscula;
- un número;
- un carácter especial, por ejemplo `. , ! $ % # @`.

#importante[No se puede volver a usar ninguna contraseña anterior, aunque haya
  pasado mucho tiempo.]

#nota[El cliente de monitoreo no tiene esta pantalla. Si al ingresar muestra
  «La contraseña está vencida: debe definir una nueva para continuar.»,
  cámbiela en el panel web y vuelva a ingresar en el cliente con la nueva.]

== Ingresar desde el cliente de monitoreo

#captura-pendiente("Cliente de monitoreo: ventana de inicio de sesión")

+ En *Servidor*, escriba la dirección del servidor, la misma del panel web
  (puede omitir `http://`).
+ En *Usuario*, escriba su nombre de usuario. La flecha #boton("▾") muestra los
  usuarios que ingresaron antes en este equipo; el basurero de cada uno lo
  quita de la lista.
+ Escriba su contraseña. Para verla, use el ojo del campo.
+ Si quiere, marque *Recordar contraseña* o *Inicio de sesión automático*.
+ Haga clic en #boton("Ingresar"). Mientras se conecta, el botón dice
  «Conectando…».

#consejo[Con *Inicio de sesión automático*, el cliente ingresa solo al abrirse.
  Úselo únicamente en puestos de monitoreo dedicados, nunca en equipos
  compartidos. Al marcarlo también se marca *Recordar contraseña*.]

#nota[Cada equipo donde se abre el cliente ocupa uno de los puestos que permite
  la licencia. El puesto se libera al cerrar el cliente. Si el cliente se
  cerró de golpe (por ejemplo, por un corte de luz), el puesto sigue ocupado
  hasta que la sesión venza, 12 horas después, o hasta que un administrador lo
  libere en #menu("Streaming", "Sesiones").]

Si no puede ingresar, el motivo aparece en rojo sobre el formulario:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [«Usuario o contraseña incorrectos.»],
  [Revise ambos campos. La contraseña distingue mayúsculas de minúsculas.],
  [«No se pudo conectar con el servidor. Verifique la URL y la red.»],
  [Revise la dirección en *Servidor* y la conexión de red del equipo. Si
    está bien escrita, el servidor puede estar apagado: avise al
    administrador.],
  [«El servidor no respondió a tiempo.»],
  [La red o el servidor están lentos. Vuelva a intentar en unos segundos.],
  [«La contraseña está vencida: debe definir una nueva para continuar.»],
  [Cámbiela en el panel web (ver la sección anterior).],
  [«La licencia permite 3 clientes y ya hay 3 en uso. Amplíe la licencia para
    agregar más.» (los números dependen de su licencia)],
  [Todos los puestos de cliente están ocupados. Pida a un administrador que
    libere uno que ya no se use.],
  [«Un administrador cerró la sesión de este equipo para liberar su puesto.
    Podrá volver a ingresar en un minuto.»],
  [Espere un minuto y vuelva a ingresar.],
)

== La ventana del cliente de monitoreo

Tras ingresar se abre la ventana principal. Arriba está la barra con las
viñetas de los módulos abiertos y el estado de la conexión; a la izquierda, el
riel con un botón por módulo; abajo, la barra de estado.

#captura-pendiente("Cliente de monitoreo: ventana principal con las viñetas Inicio y Vista en Vivo; marcar las viñetas, los indicadores CPU/RAM/disco, el estado de conexión, el botón del usuario, el riel lateral y la barra de estado")

=== Viñetas de la barra superior

La viñeta #boton("Inicio") está siempre. Cada módulo que abra agrega su propia
viñeta junto a ella; un clic en una viñeta cambia de módulo sin cerrar el
anterior, que sigue trabajando por detrás (por ejemplo, el video de la Vista en
Vivo no se corta al pasar a Inicio).

Para cerrar un módulo, haga clic en la #boton("✕") de su viñeta. Qué pasa al
cerrarlo depende del módulo:

#table(
  columns: (1fr, 2fr),
  table.header[Viñeta][Al cerrarla],
  [Vista en Vivo], [Se detienen todos los videos de la grilla.],
  [Reproducción], [Se detiene el video que se estaba reproduciendo.],
  [Muro de video], [Solo sale de la vista: el muro sigue mostrando lo que tenía.],
  [Paneles de alarma, Cerco eléctrico, Citofonía],
  [Solo sale de la vista: las alarmas y las llamadas siguen avisando en este
    puesto.],
  [Centro de eventos], [Solo sale de la vista: el historial sigue en el servidor.],
  [Reconocimiento de patentes],
  [Solo sale de la vista: el servidor sigue recibiendo y guardando las lecturas.],
)

A la derecha de la barra están los botones *Minimizar*, *Maximizar* y
*Cerrar* de la ventana. La barra también sirve para arrastrar la ventana.

=== Indicadores del servidor y conexión

Junto al nombre de usuario hay tres íconos con el uso de procesador, memoria y
disco del servidor. Se actualizan cada 5 segundos:

#table(
  columns: (auto, 1fr),
  table.header[Color][Uso],
  [Gris], [Menos de 70 %.],
  [Amarillo], [Entre 70 % y 89 %.],
  [Rojo], [90 % o más.],
)

Pase el puntero sobre un ícono para ver el detalle, por ejemplo «CPU del
servidor: 35%» o «RAM del servidor: 9,5 de 16 GB (59%)». Si el servidor no
responde, los íconos se ocultan.

El punto de color indica la conexión en tiempo real con el servidor: verde
con «Conectado», o rojo con «Reconectando…» mientras se recupera. El cliente
reintenta solo; al volver la conexión, recupera las alertas que quedaron sin
confirmar.

=== Menú del usuario

Haga clic en su nombre, a la derecha de la barra, para ver:

- su nombre y sus roles;
- su alcance, si está limitado a algunas ubicaciones (por ejemplo, «Alcance:
  Bodega central (ve el resto, sin operarlo)»);
- *Conectado al servidor*, con la dirección del servidor de esta sesión;
- #boton("Cerrar sesión"), que se explica más adelante en este capítulo.

=== Riel lateral

Cada botón del riel abre un módulo; pase el puntero sobre él para ver su
nombre. Solo aparecen los módulos que sus roles permiten:

#table(
  columns: (1fr, 1fr),
  table.header[Botón del riel][Aparece con el permiso],
  [Inicio], [Siempre],
  [Vista en Vivo], [Ver video en vivo],
  [Reproducción], [Ver grabaciones],
  [Muro de video], [Operar el muro de video],
  [Paneles de alarma], [Ver alarmas],
  [Cerco eléctrico], [Ver cercos eléctricos],
  [Citofonía], [Atender citofonía],
  [Centro de eventos], [Atender alertas],
  [Aplicaciones · Reconocimiento de patentes], [Ver patentes],
  [Centro de descargas (abajo)], [Exportar grabaciones],
  [Configuración (engranaje, abajo)], [Siempre],
)

Algunos botones avisan aunque su viñeta esté cerrada:

- *Paneles de alarma* y *Cerco eléctrico* se ponen rojos y parpadean mientras
  haya una alarma activa.
- *Citofonía* se pone ámbar y parpadea mientras hay una llamada sonando.
- *Centro de eventos* se pone rojo, parpadea y muestra una insignia con la
  cantidad de alertas sin confirmar.
- *Centro de descargas* se enciende y pulsa suave mientras hay descargas de
  grabaciones en curso o en cola.

=== Página Inicio

La página Inicio saluda con su nombre de usuario y muestra una tarjeta por
módulo, agrupadas en *MÓDULOS* (Vista en Vivo, Reproducción, Centro de
eventos, Muro de video, Paneles de alarma, Cerco eléctrico, Citofonía y Centro
de descargas) y *APLICACIONES* (Reconocimiento de patentes). Haga clic en una
tarjeta para abrir el módulo; se agrega su viñeta en la barra superior. El
Centro de descargas es la excepción: se abre en su propia ventana, igual que
con su botón del riel.

Una tarjeta aparece solo si sus roles incluyen el permiso del módulo, el
mismo de la tabla del riel. Si falta un módulo que necesita, pídale el
permiso al administrador. Si el administrador cambia sus roles con la sesión
abierta, el riel y la página Inicio se actualizan solos y se cierran los
módulos que ya no le corresponden.

=== Barra de estado

La barra inferior muestra a la izquierda lo que está pasando: «Listo.», una
indicación del módulo abierto o el resultado de la última acción.

A la derecha aparece, cuando corresponde, el aviso de licencia del servidor:
en ámbar si la licencia está por vencer o en período de gracia, y en rojo si
el sistema quedó restringido por licencia. Con el sistema restringido no se
pueden abrir cámaras ni hacer cambios: avise al administrador.

== Avisos emergentes

Algunos avisos aparecen en una ventana pequeña en la esquina inferior derecha,
sobre el video. Se muestra uno a la vez: uno nuevo reemplaza al anterior.

#table(
  columns: (1fr, 2fr),
  table.header[Aviso][Cómo se comporta],
  [Archivo guardado (captura, grabación o tramo exportado)],
  [Muestra el título, por ejemplo «Captura guardada», y la ruta del archivo.
    Un clic en la ruta abre el Explorador de Windows con el archivo
    seleccionado. Se cierra solo a los 8 segundos.],
  [Alarma (de un panel, de una puerta o de una automatización)],
  [Ícono rojo, título y detalle; puede traer la foto del hecho. Se cierra
    solo a los 15 segundos, o a los 30 si trae foto.],
  [Alarma de cerco eléctrico],
  [Trae el botón #boton("Enterado") y no se cierra sola hasta que lo use.
    #boton("Enterado") calla la sirena de este puesto; la sirena del panel se
    apaga con #boton("Silenciar") en el módulo Cerco eléctrico.],
)

La #boton("✕") del aviso (*Descartar*) lo cierra. Lo que debe hacer ante cada
alarma se explica en el capítulo de su módulo (por ejemplo,
#capitulo(<cap-cerco-electrico>)).

== Configuración del cliente

Las preferencias del cliente se guardan en este equipo. Para cambiarlas:

+ Haga clic en el engranaje *Configuración*, abajo en el riel.
+ Elija un apartado a la izquierda: *Video*, *Imagen*, *Sonido*, *Red*,
  *Sistema* o *Licencia*.
+ Cambie lo que necesite y haga clic en #boton("Guardar"). Con
  #boton("Cancelar") o la #boton("✕") (*Cerrar sin guardar*) se descartan los
  cambios.

Al guardar, la barra de estado dice «Configuración guardada.» y los cambios
rigen de inmediato, salvo los que indican lo contrario.

#captura-pendiente("Cliente de monitoreo: ventana Configuración en el apartado Video")

=== Video

#table(
  columns: (1fr, 2fr),
  table.header[Opción][Qué hace],
  [*Stream al abrir un canal*],
  [*Automático* usa el stream principal en divisiones de hasta 4 cuadros y el
    secundario en las más grandes. *Principal* y *Secundario* fijan uno. El
    botón P/S de cada cuadro sigue disponible para cambiarlo a mano.],
  [*Ajuste de imagen*],
  [*Mantener proporción* deja barras negras si la forma de la imagen no
    calza con el cuadro. *Estirar al cuadro* llena el cuadro deformando
    levemente la imagen. Se aplica a todos los cuadros al guardar.],
  [*Al abrir un equipo completo (doble clic en el árbol)*],
  [*Grilla ajustada al equipo* (la opción inicial) arma una cuadrícula a
    medida, sin cuadros de sobra; por ejemplo, 40 cámaras en 8×5. *División
    estándar más cercana* usa la división del selector donde quepan.],
  [*Al iniciar sesión*],
  [*Volver a abrir las cámaras de la última sesión* reabre la Vista en Vivo
    como quedó. Viene apagada. Ver
    #modulo("video")[#capitulo(<cap-vista-en-vivo>).]],
  [*Carpeta de grabaciones locales*],
  [Dónde se guardan las grabaciones que hace desde la Vista en Vivo. Escriba
    la ruta o use #boton("Examinar…"). Vacía, se usa la subcarpeta
    `TrueCentral VMS` de su carpeta Videos; la ruta exacta aparece bajo el
    campo.],
)

=== Imagen

#table(
  columns: (1fr, 2fr),
  table.header[Opción][Qué hace],
  [*Carpeta de capturas*],
  [Dónde se guardan las capturas de imagen. Vacía, se usa la subcarpeta
    `TrueCentral VMS` de su carpeta Imágenes; la ruta exacta aparece bajo el
    campo.],
  [*Formato de captura*],
  [*JPG (liviano)* o *PNG (sin pérdida)*.],
)

=== Sonido

*Volumen inicial de los cuadros* (de 0 a 100) es el volumen con que suena un
cuadro al activar su audio. Rige para los cuadros que se creen desde ahora (al
cambiar la división o volver a abrir la Vista en Vivo). El audio de cada
cuadro sigue partiendo silenciado.

=== Red

*Tiempo de espera de la API (segundos)* es cuánto espera el cliente una
respuesta del servidor antes de darla por fallida. Acepta de 5 a 120 segundos
y rige al reiniciar el cliente. Si escribe otro valor, al guardar aparece «El
tiempo de espera de la API debe ser un número entre 5 y 120.»

=== Sistema

#table(
  columns: (1fr, 2fr),
  table.header[Opción][Qué hace],
  [*Iniciar sesión automáticamente al abrir el cliente*],
  [La misma opción de la ventana de ingreso. Debajo se indica con qué cuenta y
    en qué servidor ingresa. Apagarla rige desde la próxima vez que se abra el
    cliente.],
  [*Abrir la ventana de alarma donde quedó la última vez*],
  [Apagada, la ventana de alarma aparece centrada sobre la ventana principal.
    Encendida, se abre en el monitor, la posición y el tamaño en que se cerró
    la anterior. Si ese monitor ya no está conectado, vuelve a aparecer
    centrada.],
)

#consejo[En puestos con dos pantallas, encienda *Abrir la ventana de alarma
  donde quedó la última vez* y arrastre la ventana de alarma una vez a la
  pantalla de trabajo.]

=== Licencia

Este apartado solo muestra información: el estado de la licencia del servidor
(«Licencia activa», «Período de prueba», «Licencia en período de gracia»,
«Sistema restringido por licencia» o «Sin licencia»), la clave, el aviso
vigente si lo hay, el cliente, el vencimiento y el servidor. En *Módulos y
cupos* lista cada módulo como incluido o no incluido y, si tiene cupo, cuánto
se usa (por ejemplo, «3 de 16 canales de video»).

La licencia se activa y se renueva en el panel web; ver
#capitulo(<cap-sistema>).

== El panel web

El panel web tiene un menú lateral a la izquierda y, arriba, el título de la
página y su nombre de usuario con sus roles.

#captura("web-panel.png", pie: [Página Panel con el menú del usuario abierto.])

=== Menú lateral

Los grupos con una flecha (*Dispositivos*, *Control de acceso*, *Seguridad*,
*Streaming* y *Sistema*) se abren y se cierran con un clic. El menú muestra
solo las páginas que sus roles permiten; los grupos y los títulos que quedan
vacíos tampoco aparecen.

#table(
  columns: (3fr, 2fr),
  table.header[Página del menú][Aparece con el permiso],
  [Panel, Recursos, #menu("Sistema", "Servicios"), #menu("Sistema", "Licencia")],
  [Siempre],
  [Automatizaciones], [Ver automatizaciones],
  [Biblioteca de sonidos], [Biblioteca de sonidos],
  [#menu("Aplicaciones", "Vista en vivo")], [Ver video en vivo],
  [#menu("Aplicaciones", "Alarmas")], [Ver alarmas],
  [#menu("Aplicaciones", "Cerco eléctrico")], [Ver cercos eléctricos],
  [#menu("Aplicaciones", "Centro de eventos")], [Atender alertas],
  [#menu("Aplicaciones", "Videowall")], [Operar el muro de video],
  [#menu("Aplicaciones", "Citofonía")], [Atender citofonía],
  [#menu("Aplicaciones", "ANPR")], [Ver patentes],
  [#menu("Dispositivos", "Fuentes de video"), y en
    #menu("Dispositivos", "Videowalls"): *Decodificadores* y *Muro de video*],
  [Fuentes de video],
  [#menu("Dispositivos", "Paneles de alarma")], [Paneles de alarma],
  [#menu("Dispositivos", "Paneles de cerco")], [Paneles de cerco],
  [#menu("Dispositivos", "Control de acceso")], [Equipos de acceso],
  [#menu("Dispositivos", "Parlantes IP")], [Parlantes IP, o Usar parlantes],
  [#menu("Dispositivos", "Citofonía")], [Citofonía],
  [#menu("Dispositivos", "Hora y mantenimiento")], [Ver hora y mantenimiento],
  [#menu("Control de acceso", "Monitoreo") y
    #menu("Control de acceso", "Registros de acceso")],
  [Ver control de acceso],
  [#menu("Control de acceso", "Personas"), *Niveles de acceso* y *Horarios*],
  [Ver personas],
  [#menu("Seguridad", "Usuarios")], [Usuarios],
  [#menu("Seguridad", "Roles")], [Roles],
  [#menu("Seguridad", "Auditoría")], [Bitácora de auditoría],
  [#menu("Streaming", "Sesiones")], [Sesiones],
)

Si abre una página que sus roles no permiten (por ejemplo, desde un enlace
guardado), en lugar de la página aparece «Sus roles no incluyen el acceso a
esta página. Si lo necesita, pídaselo a quien administra los usuarios.» Si un
administrador cambia sus roles mientras trabaja, el menú se rehace solo y
aparece el aviso «Sus permisos cambiaron: la pantalla se actualizó.»

Al pie del menú, el punto verde con «Conectado» indica que el servidor
responde; si deja de responder, cambia a rojo con «Sin conexión».

Cuando la licencia está por vencer, en período de gracia o restringida, bajo
la barra superior aparece una franja con el aviso y el enlace *Ver licencia*.

=== Página Panel

Es la página que se abre al ingresar. Resume el estado del sistema en
tarjetas:

#table(
  columns: (1fr, 2fr),
  table.header[Tarjeta][Muestra],
  [Estado del servidor], [«En línea» o «Sin conexión».],
  [Versión], [La versión instalada en el servidor.],
  [Dispositivos], [Cuántos equipos de video hay y, entre paréntesis, cuántos
    están en línea.],
  [Sesiones de video activas], [Cuántos videos se están viendo en este
    momento. Solo aparece con el permiso Sesiones.],
  [Licencia], [El estado (Activa, Período de prueba, Período de gracia,
    Restringida o Sin licencia), la clave y el aviso vigente. Un clic abre la
    página Licencia.],
  [Servicios del servidor], [Cuántos servicios están en ejecución; en rojo,
    «N caído(s): revisar» si alguno falló. Un clic abre la página Servicios.],
  [Usuarios], [Cuántos usuarios hay. Necesita el permiso Usuarios; sin él,
    muestra «—».],
  [Hora del servidor (UTC)], [La hora del servidor.],
)

=== Menú del usuario

Haga clic en su nombre, arriba a la derecha:

- *Su alcance*: aparece si su usuario está limitado a algunas ubicaciones, con
  la lista de ellas. «Ve el resto, sin operarlo» indica que puede ver lo demás
  pero no darle órdenes.
- #boton("Descargar el complemento"): descarga el instalador del complemento
  de enrolamiento, que se instala en el equipo donde está el lector USB de
  huellas#modulo("access")[ (ver #capitulo(<cap-control-de-acceso>))]. Si
  aparece «El instalador del complemento no está publicado en este servidor
  (carpeta webcontrol).», avise al administrador.
- #boton("Acerca de"): versión y build del servidor, estado de la licencia,
  ID de hardware, nombre del servidor, sistema operativo y desde cuándo está
  en ejecución. #boton("Copiar datos") copia un resumen para enviarlo a
  soporte; #boton("Ver licencia") (con el permiso Licencia) abre la página
  Licencia.
- #boton("Cerrar sesión").

== Cerrar sesión

=== En el cliente de monitoreo

+ Haga clic en su nombre, a la derecha de la barra superior.
+ Haga clic en #boton("Cerrar sesión").

El cliente detiene el video, cierra las pantallas auxiliares y vuelve a la
ventana de ingreso. Aunque tenga *Inicio de sesión automático*, no vuelve a
entrar solo hasta la próxima vez que se abra el cliente.

Para salir del todo, haga clic en *Cerrar* (la #boton("✕") de la barra
superior) y responda #boton("Sí") a «¿Cerrar CLR TrueCentral VMS?». Salir
también cierra la sesión en el servidor y libera el puesto de este equipo.

#importante[Si hay descargas de grabaciones en curso, el cliente avisa antes
  de cerrar la sesión o de salir: si continúa, las descargas se cancelan y los
  archivos a medias quedan incompletos.]

=== En el panel web

Haga clic en su nombre, arriba a la derecha, y luego en
#boton("Cerrar sesión"). El panel vuelve a la pantalla de ingreso. Las demás pestañas del
mismo navegador también se cierran, con el aviso «Se cerró la sesión desde
otra pestaña de este navegador. Ingrese de nuevo para seguir.»

== Cuando la sesión termina sola

=== En el cliente de monitoreo

Si la sesión vence o el servidor se reinicia, el cliente la renueva solo, con
los datos con que ingresó, y usted no nota nada. Si un administrador cambia
sus roles, el riel y la página Inicio se actualizan sin cerrar la sesión.

Solo vuelve a la ventana de ingreso cuando el servidor rechaza la sesión. En
ese caso cancela las descargas en curso sin preguntar y muestra el motivo en
rojo:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué pasó],
  [«El servidor cerró su sesión: su usuario fue deshabilitado o eliminado, o su
    contraseña cambió.»],
  [Un administrador deshabilitó o eliminó su usuario, o le cambió la
    contraseña. Ingrese con la contraseña nueva o consulte al administrador.],
  [«Un administrador cerró la sesión de este equipo para liberar su puesto.
    Podrá volver a ingresar en un minuto.»],
  [Un administrador liberó el puesto de este equipo. Espere un minuto para
    volver a ingresar.],
  [«La contraseña está vencida: debe definir una nueva para continuar.»],
  [Cámbiela en el panel web y vuelva a ingresar.],
  [«El servidor cerró su sesión. Vuelva a iniciar sesión.»],
  [La sesión terminó por otro motivo. Ingrese de nuevo.],
)

=== En el panel web

Una sesión del panel web dura 12 horas desde que ingresó. El panel revisa si
sigue vigente cuando vuelve a la pestaña o a la ventana, cada minuto mientras
está a la vista y a la hora exacta del vencimiento. Si terminó, detiene lo que
estaba haciendo y lleva a la pantalla de ingreso con el motivo:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué pasó],
  [«Su sesión venció a las 20:15. Ingrese de nuevo para seguir.»],
  [Pasaron las 12 horas de la sesión.],
  [«Su sesión se cerró: un administrador cambió su cuenta o el servidor se
    reinició. Ingrese de nuevo para seguir.»],
  [Un administrador deshabilitó su usuario o le cambió la contraseña, o el
    servidor se reinició.],
  [«Se cerró la sesión desde otra pestaña de este navegador. Ingrese de nuevo
    para seguir.»],
  [Cerró la sesión en otra pestaña del mismo navegador.],
  [«Su sesión anterior terminó. Ingrese de nuevo para seguir.»],
  [Al abrir el panel, la sesión guardada en el navegador ya no valía.],
  [«No se pudo hablar con el servidor: revise la conexión e ingrese de nuevo.»],
  [El servidor no respondió al abrir el panel. Revise la red.],
)

#importante[Lo que estaba escribiendo en un formulario sin guardar se pierde
  al terminar la sesión.]
