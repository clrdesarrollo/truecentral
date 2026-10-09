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

Bajo el título del módulo, un punto verde y el texto _N de M fuente(s)
activa(s)_ indican cuántas cámaras están entregando lecturas en este momento.
El punto queda gris si ninguna está conectada.

Cerrar la pestaña solo saca el módulo de la vista: el servidor sigue recibiendo
y guardando las lecturas.

== Seguir las lecturas en vivo

La lista *Últimos reconocimientos*, a la izquierda, muestra las lecturas más
nuevas arriba. Cada fila tiene:

- una miniatura con el primer plano de la placa (o la escena, si la cámara no
  envió el recorte);
- la patente y la confianza de la lectura;
- la hora del equipo, con milésimas de segundo, y la cámara que la leyó;
- un resumen del vehículo (tipo, color y velocidad), cuando la cámara lo
  informa.

La lista guarda las 50 lecturas más recientes; arriba a la derecha se ve
cuántas hay en pantalla. Con las flechas #tecla("↑") y #tecla("↓") se recorre
la lista.

Con *Seguir en vivo* marcado, la ficha de la derecha salta sola a cada lectura
nueva: es el modo para una caseta de control. Desmárquelo para estudiar una
lectura con calma sin que la ficha cambie cuando llega otra.

== Revisar la ficha de una lectura

Haga clic en una lectura de la lista para ver su ficha:

- arriba, la patente en grande, la fecha y hora, la cámara y la confianza;
- a la izquierda, la escena completa con un recuadro verde sobre la placa;
- a la derecha, el primer plano de la placa y los datos que envió la cámara.

La ficha muestra solo los datos que la cámara envió, en este orden:

#table(
  columns: (2fr, 3fr),
  table.header[Dato][Qué indica],
  [Fecha y hora del equipo], [Momento de la lectura, según el reloj de la
    cámara.],
  [Equipo, Canal], [Cámara que hizo la lectura.],
  [Confianza de lectura], [Seguridad de la cámara sobre la patente leída, en
    porcentaje. Un guion indica que no la informó.],
  [Confianza por carácter], [La misma seguridad, letra por letra.],
  [Tipo de vehículo, Color del vehículo, Marca reconocida, Observado en el
    vehículo], [Lo que la cámara reconoció del vehículo.],
  [Velocidad, Largo del vehículo, Carril, Sentido], [Datos de tránsito, si la
    cámara los mide.],
  [Disparo de la captura, Infracción], [Qué provocó la captura y, si
    corresponde, la infracción detectada.],
  [Color de la placa, Tipo de placa], [Características de la placa.],
  [Recibido por el servidor], [Hora en que la lectura llegó al servidor.],
)

#nota[Las horas de la lista y de la ficha son las del reloj de la cámara. Si
  _Fecha y hora del equipo_ difiere mucho de _Recibido por el servidor_, el
  reloj de la cámara está desajustado.]

Si la cámara no envió la escena, la ficha muestra _El equipo no envió la
imagen de la escena._

=== Copiar la patente y guardar las fotos

- #boton("Copiar patente") copia la patente al portapapeles, para pegarla en
  otro programa.
- #boton("Guardar imágenes") guarda la escena y el primer plano de la placa
  como JPG en la *Carpeta de capturas* de la configuración del cliente, con
  nombres como `ABCD12_20261009_143005_escena.jpg` y
  `ABCD12_20261009_143005_placa.jpg`. Aparece la notificación _Reconocimiento
  guardado_ con la ruta; haga clic en ella para abrir la carpeta.

Si la lectura no tiene fotos, aparece _Este reconocimiento no tiene imágenes
que guardar._

== Buscar una patente en el cliente

#requiere("Ver patentes")

+ Si quiere limitar la búsqueda a una cámara, elíjala en la lista de equipos de
  la barra superior (vacía, busca en todas).
+ Escriba la patente completa o una parte en el cuadro de búsqueda. No importan
  las mayúsculas, los guiones ni los espacios: `cd-12` encuentra `ABCD12`.
+ Haga clic en #boton("Buscar") o presione #tecla("Enter").

La lista muestra las 50 lecturas más recientes que coinciden. Mientras el
filtro esté puesto, las lecturas nuevas que no coinciden no se agregan a la
lista. #boton("Limpiar") quita los filtros y vuelve a las últimas lecturas.

Si nada coincide, aparece _Ningún reconocimiento coincide con la búsqueda._

#consejo[Para buscar entre fechas o revisar más de 50 lecturas, use la página
  ANPR del panel web.]

== Usar la página ANPR del panel web

#requiere("Ver patentes")

La página se abre en #menu("Aplicaciones", "ANPR"). Muestra las mismas lecturas
que el cliente, con más filtros, y se actualiza sola cada pocos segundos.

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

En la ficha:

- #boton("Copiar patente") copia la patente al portapapeles;
- #boton("Guardar escena") y #boton("Guardar placa") descargan cada foto a la
  carpeta de descargas del navegador.

=== Eliminar una lectura

#requiere("Borrar lecturas de patentes")

+ Seleccione la lectura en la lista.
+ Haga clic en #boton("Eliminar").
+ Confirme el mensaje _¿Eliminar la lectura de … y sus fotos? No se puede
  deshacer._

La lectura desaparece de la lista y aparece el aviso _Lectura eliminada._ Las
lecturas solo se pueden eliminar desde el panel web.

#importante[La lectura y sus fotos se borran del servidor de forma
  definitiva.]

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
