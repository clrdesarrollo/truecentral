#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/LprView.xaml, src/TrueCentralVms.Client/Views/LprView.xaml.cs, src/TrueCentralVms.Client/ViewModels/LprViewModel.cs, src/TrueCentralVms.Client/ViewModels/PlateEventViewModel.cs, src/TrueCentralVms.Client/Views/MainWindow.xaml, src/TrueCentralVms.Server/wwwroot/anpr.js, src/TrueCentralVms.Server/wwwroot/app.js, src/TrueCentralVms.Server/Api/AnprApi.cs, src/TrueCentralVms.Server/Services/AnprService.cs

= Reconocimiento de patentes <cap-reconocimiento-de-patentes>

Las cámaras con reconocimiento de patentes leen la patente de cada vehículo que
pasa frente a ellas y la envían al servidor junto con sus fotos. El sistema
guarda cada lectura y la muestra al instante en el cliente de monitoreo y en la
página ANPR del panel web, donde además se puede buscar hacia atrás por
patente, equipo y fecha.

#captura-pendiente("Cliente de monitoreo: Reconocimiento de patentes con la lista de últimos reconocimientos y la ficha de una lectura con su escena y el recuadro verde sobre la placa", alto: 6cm)

#nota[La lectura de la patente la hace la propia cámara; el sistema no analiza
  el video. Solo llegan lecturas de las cámaras activadas como fuente de
  patentes (vea la sección Configuración).]

== Abrir el módulo en el cliente

#requiere("Ver patentes")

+ Haga clic en el botón *Aplicaciones · Reconocimiento de patentes* del menú
  lateral (debajo de la línea que separa las aplicaciones), o en la tarjeta
  *Reconocimiento de patentes* de la sección *APLICACIONES* de la pantalla de
  inicio.
+ El módulo se abre en la pestaña *Reconocimiento de patentes* de la barra
  superior.

La pantalla es la misma de la página ANPR del panel web. Junto al título
aparece cada cámara activada como fuente, con un punto verde si está
entregando lecturas y rojo si no tiene conexión; al pasar el mouse sobre ella
se ve el motivo y la hora de la última lectura. Si no hay ninguna, se lee _Sin
cámaras activas como fuente_.

Cerrar la pestaña solo saca el módulo de la vista: el servidor sigue recibiendo
y guardando las lecturas.

== Seguir las lecturas en vivo

La lista de la izquierda muestra las lecturas más nuevas arriba. Cada fila
tiene:

- una miniatura con el primer plano de la placa (o la escena, si la cámara no
  envió el recorte; _Sin foto_ si no envió ninguna);
- la patente y la confianza de la lectura;
- la hora del equipo, con milésimas de segundo, y la cámara que la leyó;
- un resumen del vehículo (tipo, color y velocidad), cuando la cámara lo
  informa.

La lista guarda tantas lecturas como indique el filtro *Mostrar* (100 por
omisión). Arriba de la lista se ve cuántas hay; si llegó a ese máximo, el
contador lo indica (_últimas 100_, por ejemplo). Con las flechas #tecla("↑") y
#tecla("↓") se recorre la lista.

Con *Seguir en vivo* marcado, la ficha de la derecha salta sola a cada lectura
nueva: es el modo para una caseta de control. Al elegir a mano una lectura que
no es la más nueva, se desmarca solo, para que la ficha no cambie mientras la
revisa; márquelo de nuevo para volver a seguir las lecturas.

== Revisar la ficha de una lectura

Haga clic en una lectura de la lista para ver su ficha:

- a la izquierda, la escena completa con un recuadro verde sobre la placa;
- a la derecha, el primer plano de la placa y, bajo él, la patente leída en
  letras verdes sobre negro, para compararla de un vistazo con la placa real.
  La patente sale en rojo si no es una patente chilena válida;
- debajo, los datos que envió la cámara.

La ficha muestra solo los datos que la cámara envió, en este orden. Los
nombres van abreviados para dejarle más ancho a la escena; al pasar el mouse
sobre uno se ve su nombre completo, el mismo de la página ANPR del panel web:

#table(
  columns: (2fr, 3fr),
  table.header[Dato][Qué indica],
  [Hora del equipo], [Fecha y hora de la lectura, según el reloj de la
    cámara.],
  [Equipo, Canal], [Cámara que hizo la lectura.],
  [Confianza], [Seguridad de la cámara sobre la patente leída, en porcentaje.
    Un guion indica que no la informó.],
  [Patente chilena], [Si la patente leída es válida en Chile: el dígito
    verificador (DV) y su formato, o, en rojo, por qué no lo es.],
  [Por carácter], [La misma seguridad, letra por letra: cada carácter de la
    patente con su porcentaje debajo.],
  [Vehículo, Color, Marca, Observado], [Lo que la cámara reconoció del
    vehículo.],
  [Velocidad, Largo, Carril, Sentido], [Datos de tránsito, si la cámara los
    mide.],
  [Disparo, Infracción], [Qué provocó la captura y, si corresponde, la
    infracción detectada.],
  [Color de placa, Tipo de placa], [Características de la placa.],
  [Recibido], [Hora en que la lectura llegó al servidor.],
)

#nota[Las horas de la lista y de la ficha son las del reloj de la cámara. Si
  _Hora del equipo_ difiere mucho de _Recibido_, el reloj de la cámara está
  desajustado.]

Si la cámara no envió la escena, la ficha muestra _El equipo no envió la foto
de la escena._; si no envió el recorte de la placa, _Sin primer plano de la
placa_.

=== Copiar la patente y descargar la lectura

- Haga clic en la patente verde para copiarla al portapapeles y pegarla en
  otro programa. La barra de avisos confirma _Patente … copiada al
  portapapeles._
- #boton("Descargar ZIP") guarda en la *Carpeta de capturas* de la
  configuración del cliente un archivo con las fotos de la lectura (escena y
  placa) y un JSON con todos sus datos, con un nombre como
  `ABCD12_09102026_143005.zip`. Aparece la notificación _Reconocimiento
  descargado_ con la ruta; haga clic en ella para abrir la carpeta. Cada
  descarga queda en la bitácora de auditoría.

== Buscar una patente en el cliente

#requiere("Ver patentes")

Los filtros están sobre la lista, cada uno con su nombre arriba:

#table(
  columns: (auto, 1fr),
  table.header[Filtro][Uso],
  [*Equipo*], [_Todos_ o una cámara. Las que no están activas como fuente
    aparecen con _(inactiva)_.],
  [*Patente*], [La patente completa o una parte. No importan las mayúsculas,
    los guiones ni los espacios: `cd-12` encuentra `ABCD12`.],
  [*Desde (hora del equipo)*, *Hasta (hora del equipo)*], [Fecha y hora según
    el reloj de la cámara, escritas como `dd-mm-aaaa hh:mm` (por ejemplo
    `09-10-2026 14:30`). Con solo la fecha, *Desde* toma el día desde las 00:00
    y *Hasta* hasta las 23:59:59. Vacíos, no limitan la búsqueda.],
  [*Mostrar*], [Cuántas lecturas traer: 50, 100, 200 o 500.],
)

Haga clic en #boton("Buscar") o presione #tecla("Enter") en *Patente*,
*Desde* o *Hasta*. Mientras haya un filtro puesto, las lecturas nuevas que no
coinciden no se agregan a la lista. #boton("Limpiar") quita todos los filtros y
vuelve a las últimas lecturas.

Si nada coincide, aparece _No hay lecturas que coincidan con los filtros._ Si
una fecha está mal escrita, la barra de avisos lo indica (_“Desde” no es una
fecha válida…_) y la búsqueda no se hace.

== Usar la página ANPR del panel web

#requiere("Ver patentes")

La página se abre en #menu("Aplicaciones", "ANPR"). Muestra las mismas lecturas
y los mismos filtros que el cliente, y se actualiza sola cada pocos segundos.

#captura-pendiente("Panel web: página ANPR con la barra de filtros, la lista de lecturas y la ficha de una lectura", alto: 6cm)

Junto al título *Lecturas de patentes* aparece cada cámara activada como
fuente, con un punto verde si está entregando lecturas y rojo si no tiene
conexión. Al pasar el mouse sobre ella se ve el motivo y la hora de la última
lectura. Si no hay ninguna, se lee _Sin cámaras activas como fuente_.

=== Buscar y filtrar

#table(
  columns: (auto, 1fr),
  table.header[Filtro][Uso],
  [*Equipo*], [_Todos_ o una cámara. Las que no están activas como fuente
    aparecen con _(inactiva)_.],
  [*Patente*], [Toda la patente o una parte, igual que en el cliente.],
  [*Desde (hora del equipo)*, *Hasta (hora del equipo)*], [Fecha y hora,
    según el reloj de la cámara.],
  [*Mostrar*], [Cuántas lecturas traer: 50, 100, 200 o 500.],
)

Haga clic en #boton("Buscar") (o presione #tecla("Enter") en *Patente*).
#boton("Limpiar") quita todos los filtros. Arriba de la lista se ve cuántas
lecturas hay; si llegó al máximo elegido en *Mostrar*, el contador lo indica
(_últimas 100_, por ejemplo). Si nada coincide, la lista muestra _No hay
lecturas que coincidan con los filtros._

#nota[Con *Hasta* puesto, la búsqueda mira al pasado y la lista deja de
  actualizarse sola.]

=== Revisar la ficha

Haga clic en una lectura para ver su ficha, con los mismos datos que en el
cliente. Con #tecla("↑") y #tecla("↓") se recorre la lista. Haga clic en la
escena para abrirla en tamaño completo en otra pestaña del navegador.

*Seguir en vivo* funciona como en el cliente. Al elegir a mano una lectura que
no es la más nueva, se desmarca solo, para que la ficha no cambie mientras la
revisa.

En la ficha, la patente leída aparece bajo el primer plano de la placa, para
compararla de un vistazo con la placa real:

- haga clic en la patente para copiarla al portapapeles;
- #boton("Descargar ZIP") descarga un archivo con las fotos de la lectura
  (escena y placa) y un JSON con todos sus datos. Cada descarga queda en la
  bitácora de auditoría.

#nota[Las lecturas no se pueden eliminar a mano: solo el sistema las borra al
  cumplir su antigüedad.]

#modulo("automation")[
  == Actuar cuando pasa una patente

  Para que el sistema haga algo al leer una patente (por ejemplo, abrir un
  portón a las patentes de una lista o avisar al operador ante una
  desconocida), cree una automatización con el disparador de patente leída. El
  disparador admite una lista blanca o una lista negra de patentes, con
  comodines. Vea #capitulo(<cap-automatizaciones>).
]

== Privacidad y conservación del historial

Las patentes son datos personales:

- cada búsqueda con filtros, en el cliente o en el panel web, queda registrada
  en la bitácora de auditoría con el usuario y lo que buscó;
- cada usuario ve solo las lecturas de las cámaras de su alcance;
- el servidor borra solas las lecturas antiguas, con sus fotos. Por omisión las
  conserva 90 días, con un máximo de 200~000 lecturas.

== Configuración

=== Activar una cámara como fuente de patentes

#requiere("Fuentes de video")

Una cámara entrega lecturas solo si está activada como fuente. Antes, la
cámara debe tener el reconocimiento de patentes configurado en su propio
equipo, y debe estar agregada en Fuentes de video con el driver _Hikvision
(SDK nativo)_ o _Dahua (SDK nativo)_; vea #capitulo(<cap-fuentes-de-video>).

Desde el panel web:

+ Abra #menu("Dispositivos", "Fuentes de video").
+ En la columna *Patentes* del equipo, haga clic en #boton("Activar"). Aparece
  el aviso _Equipo encendido como fuente de patentes._ y el botón pasa a
  decir #boton("Activo").
+ Para dejar de recibir lecturas de ese equipo, haga clic en #boton("Activo").

#captura-pendiente("Panel web: Dispositivos › Fuentes de video con la columna Patentes, un equipo Activo y otro con el botón Activar")

La columna *Patentes* muestra un guion en los equipos que no pueden entregar
lecturas.

Desde el cliente:

+ En Reconocimiento de patentes, haga clic en #boton("Fuentes"). Se despliega
  la lista de equipos capaces de entregar patentes, con su dirección, la
  cantidad de lecturas guardadas y un punto verde si el servidor está
  recibiendo sus eventos.
+ Marque la casilla del equipo para activarlo, o desmárquela para
  desactivarlo. La barra de avisos confirma el cambio (_… encendida: conectando
  con el equipo…_).

#captura-pendiente("Cliente de monitoreo: Reconocimiento de patentes con el panel Fuentes desplegado")

Sin el permiso _Fuentes de video_, las casillas aparecen desactivadas. Al
activar una cámara, el servidor se conecta con ella en pocos segundos. Si el
equipo se desconecta, el servidor vuelve a conectarse solo cuando el equipo
vuelve a estar en línea.

=== Cupo de la licencia

La licencia fija cuántas cámaras pueden ser fuente de patentes a la vez. Si se
supera, la activación se rechaza con uno de estos mensajes:

- _La licencia permite N fuentes ANPR y ya hay N en uso. Amplíe la licencia
  para agregar más._ Desactive otra fuente o amplíe la licencia.
- _El módulo «Reconocimiento de patentes» no está incluido en la licencia.
  Amplíe la licencia para habilitarlo._

La licencia se administra en el panel web; vea #capitulo(<cap-sistema>).

=== Si una fuente no entrega lecturas

Los avisos aparecen en la barra de avisos del módulo del cliente, en rojo bajo
cada equipo del panel *Fuentes* o, en el panel web, al pasar el mouse sobre la
cámara junto al título de la página ANPR.

#table(
  columns: (1fr, 1fr),
  table.header[Aviso][Qué hacer],
  [_Conectando con las cámaras…_], [Espere unos segundos: el servidor está
    abriendo la conexión.],
  [_El equipo está fuera de línea._], [Revise la red y la alimentación de la
    cámara. Se reconecta sola al volver.],
  [_No se pudo abrir el canal de eventos:_ …], [Revise la conexión con el
    equipo y sus datos de acceso en Fuentes de video. El servidor reintenta
    cada minuto.],
  [_Ninguna cámara está encendida como fuente…_], [Active al menos una cámara
    como fuente.],
  [_No hay equipos compatibles con reconocimiento de patentes en el
    inventario._], [Agregue la cámara en Fuentes de video con un driver
    compatible.],
)

=== Plazo de conservación

Por omisión, el servidor conserva las lecturas 90 días, con un máximo de
200~000. Para cambiarlo, edite el archivo `appsettings.Local.json` de la
carpeta de instalación del servidor, sección `Anpr`: `RetentionDays` (días) y
`MaxEvents` (máximo de lecturas). Un 0 quita ese límite. El cambio rige al
reiniciar el servidor; vea cómo editar ese archivo en
#capitulo(<ap-servidor>).
