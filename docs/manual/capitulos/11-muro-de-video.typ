#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/WallView.xaml, src/TrueCentralVms.Client/Views/WallView.xaml.cs, src/TrueCentralVms.Client/Views/WallAssignDialog.xaml, src/TrueCentralVms.Client/Views/WallLayoutsWindow.xaml, src/TrueCentralVms.Client/Views/ProjectionDialog.cs, src/TrueCentralVms.Server/wwwroot/videowall.js, src/TrueCentralVms.Server/wwwroot/walls.js, src/TrueCentralVms.Server/Api/WallsApi.cs, src/TrueCentralVms.Server/Api/DecodersApi.cs

= Muro de video <cap-muro-de-video>

El muro de video es un conjunto de monitores conectados a un decodificador.
Desde el cliente de monitoreo o el panel web, el operador decide qué cámara se
ve en cada ventana de cada monitor, divide los monitores, abre ventanas
flotantes y guarda distribuciones para usarlas después. El video lo muestra el
propio decodificador: lo que está en el muro sigue ahí aunque se cierre el
cliente.

#captura-pendiente("Cliente de monitoreo: módulo Muro de video con un muro de 2×2 monitores, algunas ventanas con cámara y el panel Cámaras a la derecha", alto: 6cm)

== La pantalla del muro

#requiere("Operar el muro de video")

El módulo se abre con el botón *Muro de video* del riel lateral o con la
tarjeta *Muro de video* del inicio. Cerrar su viñeta no apaga nada: el muro
sigue mostrando lo que tenga.

- Arriba está la barra de herramientas. En *Muro:* se elige el muro, si hay
  más de uno.
- Al centro está el muro dibujado a escala: su nombre, filas × columnas y
  decodificador, y un recuadro por monitor. Cada monitor muestra su nombre (o
  _Salida N_) y sus ventanas numeradas; una ventana con cámara muestra el
  nombre de la cámara y, debajo, el equipo y el canal. Las posiciones sin
  monitor dicen _sin salida_.
- A la derecha está el panel *Cámaras*, con los equipos y sus canales con
  señal. *Buscar canal…* filtra por nombre, número o equipo. La pestaña « » del
  borde oculta o muestra el panel.
- Abajo, la barra de estado informa la hora y el resultado de la última
  acción.

Al dejar el puntero sobre una ventana o sobre una cámara del panel aparece una
foto de esa cámara.

#table(
  columns: (auto, 1fr),
  table.header[Botón][Para qué sirve],
  [#boton("Actualizar")], [Vuelve a leer los muros y la lista de cámaras.],
  [#boton("Layouts")], [Guarda la distribución actual o aplica una guardada.],
  [#boton("Sincronizar")], [Reenvía al decodificador la división de ventanas configurada.],
  [#boton("👁 Vista previa")], [Muestra una foto de cada cámara dentro de su ventana.],
  [#boton("⊞ Agrupar")], [Modo para fusionar ventanas dibujando un rectángulo.],
  [#boton("▣ Flotante")], [Modo para dibujar una ventana flotante sobre el muro.],
  [#boton("🖥 Proyectar")], [Transmite una pantalla de este equipo o un video al muro.],
  [#boton("Limpiar todo")], [Detiene todas las ventanas del muro.],
)

#nota[Mientras un cambio se aplica, el monitor afectado se oscurece con el
  texto _Aplicando cambio…_ y no acepta otras órdenes. El decodificador tarda
  unos segundos en hacerlo efectivo.]

Los cambios que hagan otros operadores, desde otro puesto o el panel web,
aparecen solos en su pantalla.

== Poner una cámara en una ventana

#requiere("Operar el muro de video")

+ Busque la cámara en el panel *Cámaras*. Despliegue su equipo o escriba en
  *Buscar canal…*.
+ Arrástrela sobre una ventana del muro. Si la ventana ya tenía cámara, se
  reemplaza.

La barra de estado confirma _… en pantalla._ La ventana usa el stream
principal de la cámara.

- *Intercambiar dos ventanas:* arrastre una ventana con cámara sobre otra;
  sus cámaras se intercambian.
- *Quitar una cámara:* haga clic en la ✕ de la esquina de la ventana
  (*Cerrar este video*), o clic derecho y #boton("✕ Cerrar este video").
- *Ubicar la cámara de una ventana:* haga clic en la ventana; su cámara se
  resalta en el panel *Cámaras*.

== Pantalla completa

#requiere("Operar el muro de video")

- *En un monitor:* doble clic en una ventana con cámara. La cámara ocupa todo
  su monitor y el monitor muestra la marca _PANTALLA COMPLETA_. Otro doble
  clic devuelve el monitor a su división.
- *En todo el muro:* #tecla("Ctrl") + doble clic en una ventana con cámara, o
  clic derecho y #boton("🖵 Ocupar todo el muro"). La cámara cubre todos los
  monitores como si fueran uno. Doble clic sobre ella vuelve al mosaico.

Las demás cámaras siguen funcionando debajo: al volver, ningún video se
corta.

== Dividir los monitores

#requiere("Operar el muro de video")

=== Cambiar la cantidad de ventanas

Cada monitor tiene, en su esquina, un selector con la cantidad de ventanas
(1, 2, 4, 6, 8, 9, 12, 16, 25 o 36). Elija la nueva cantidad.

Si reduce las ventanas y hay cámaras que quedarían fuera, el sistema pregunta
antes de continuar, porque esas cámaras dejarán de verse.

#nota[La cantidad de monitores, sus filas y columnas, y qué salida del
  decodificador alimenta a cada uno los define el administrador en el panel
  web.]

=== Agrupar ventanas

Agrupar une varias ventanas vecinas de un monitor en una sola ventana grande.

+ Haga #tecla("Ctrl") + clic en cada ventana que quiere unir. Deben estar en
  el mismo monitor y formar un rectángulo.
+ En el encabezado del monitor, haga clic en #boton("⊞ Agrupar N").

También puede activar #boton("⊞ Agrupar") en la barra de herramientas y
dibujar con el mouse un rectángulo sobre las ventanas de un monitor; al
soltar, se agrupan.

#importante[La ventana agrupada muestra una sola cámara: la de la esquina
  superior izquierda del grupo. Si había otras cámaras en las ventanas
  elegidas, el sistema pide confirmación y las libera.]

Para deshacerlo, seleccione la ventana agrupada y haga clic en
#boton("⊟ Desagrupar") en el encabezado del monitor, o clic derecho y
#boton("⊟ Desagrupar esta ventana").

=== Dividir una ventana

Haga clic derecho en una ventana y elija
#boton("⊞ Dividir esta ventana en 4"), #boton("⊞ Dividir esta ventana en 9")
o #boton("⊞ Dividir esta ventana en 16"). La ventana se parte en ese número de
ventanas más chicas: la cámara que tenía sigue en la primera, sin cortarse, y
las demás quedan vacías. El resto del monitor no cambia.

#nota[El menú del clic derecho no está disponible mientras el monitor está en
  pantalla completa.]

== Ventanas flotantes

#requiere("Operar el muro de video")

Una ventana flotante se dibuja libremente sobre el muro, encima de las demás
cámaras, y puede abarcar varios monitores.

+ Active #boton("▣ Flotante").
+ Dibuje un rectángulo sobre el muro arrastrando el mouse.
+ En la ventana *Elegir cámara*, elija el *Dispositivo*, el *Canal* (solo se
  ofrecen los que tienen señal) y el *Stream* (_Principal_ o _Secundario
  (sub)_).
+ Haga clic en #boton("Mostrar").

La ventana flotante muestra la marca _FLOTANTE_. Con ella:

- arrástrela para moverla, o arrastre cualquiera de sus esquinas para cambiar
  su tamaño; el video no se corta;
- haga doble clic para que cubra todos los monitores que toca (_FLOTANTE ·
  PANTALLA COMPLETA_); otro doble clic la devuelve a su tamaño;
- arrastre una cámara del panel sobre ella para cambiarla;
- haga clic en su ✕ para cerrarla.

== Vista previa

#boton("👁 Vista previa") muestra una foto de cada cámara dentro de su
ventana, con el nombre en una franja inferior. Las fotos se renuevan cada 30
segundos. Sirve para revisar de un vistazo qué hay en el muro; no es video en
vivo.

== Proyectar una pantalla o un video

#requiere("Operar el muro de video")

Desde el cliente de monitoreo puede mostrar en el muro lo que se ve en una
pantalla de su equipo, o un archivo de video, en lugar de una cámara. La
transmisión es temporal: al detenerla, cada ventana vuelve a la cámara que
tenía.

+ Haga clic en la ventana del muro donde quiere proyectar; queda resaltada.
  Con #tecla("Ctrl") + clic puede elegir varias.
+ Haga clic en #boton("🖥 Proyectar").
+ En *Proyectar al muro de video*, elija qué transmitir:
  - *Una pantalla de este PC*, y cuál en la lista; o
  - *Un archivo de video*: haga clic en #boton("Examinar…") y elija el
    archivo (mp4, mkv, avi, mov, wmv, ts o m4v). Marque *Reproducir en
    bucle* si quiere que se repita sin fin.
+ En *IP de este PC (la que el decoder puede alcanzar)*, elija o escriba la
  dirección de su equipo que el decodificador puede alcanzar. La última que
  funcionó aparece primero.
+ Haga clic en #boton("Iniciar").

#captura-pendiente("Cliente de monitoreo: ventana Proyectar al muro de video con Una pantalla de este PC elegida y la IP de este PC")

Las ventanas destino muestran _Iniciando transmisión…_, _Asignando la
fuente…_ y _Esperando que el decoder se conecte…_ hasta que aparece la
imagen; luego dicen _transmisión de este puesto_. El botón cambia a
#boton("🖥 Proyectando").

Si proyecta un video, la ventana muestra una barra de reproducción:

#table(
  columns: (auto, 1fr),
  table.header[Control][Acción],
  [⏸ / ▶], [Pausa el video (la imagen queda congelada en el muro) o lo reanuda.],
  [⟲], [Vuelve a reproducir desde el inicio.],
  [🔁], [Activa o desactiva el bucle. Sin bucle, la transmisión se detiene sola al terminar el video.],
  [Barra de posición], [Salta a otro momento del video. Al lado se ven el tiempo y la duración.],
)

Para terminar:

- Haga clic en #boton("🖥 Proyectando"). Todas las ventanas vuelven a su
  cámara anterior.
- La ✕ de una ventana proyectada detiene la transmisión solo en esa ventana
  (*Detener la transmisión en esta ventana (vuelve al canal anterior)*).
- #boton("Limpiar todo") también la detiene, pero deja las ventanas vacías.
- Al cerrar el cliente, la transmisión se detiene.

También puede proyectar en una ventana flotante nueva: dibújela con
#boton("▣ Flotante") y, en *Elegir cámara*, haga clic en
#boton("🖥 Proyectar aquí…"). Esa flotante se cierra sola al detener la
transmisión.

#importante[El decodificador se conecta a su equipo por el puerto 8554/TCP. Su
  equipo debe estar en una red que el decodificador alcance y el firewall de
  Windows debe permitir el programa `mediamtx.exe`. Si la imagen no aparece,
  el cliente avisa _La transmisión está activa pero el decoder todavía no se
  conectó a este PC._ La transmisión sigue en marcha: cuando el decodificador
  se conecte, la imagen aparecerá sola.]

#nota[Cada puesto proyecta una transmisión a la vez: si inicia otra, la
  anterior se detiene. Si otro operador pone una cámara en la ventana
  proyectada, la transmisión termina con _La ventana de proyección fue
  reasignada; transmisión detenida._ La proyección solo existe en el cliente
  de monitoreo, no en el panel web.]

== Layouts

#requiere("Operar el muro de video", "Guardar diseños del muro")

Un layout es una foto del muro que se aplica de una vez, por ejemplo para
cada turno. Guarda la cantidad de ventanas de cada monitor, las ventanas
agrupadas y la cámara (con su stream) de cada ventana. No guarda las ventanas
flotantes ni las proyecciones.

Haga clic en #boton("Layouts"). Se abre *Layouts guardados* con la lista del
muro elegido; cada layout muestra cuántas cámaras tiene y cuándo se guardó.

- *Guardar:* arme el muro como lo quiere, escriba un nombre y haga clic en
  #boton("Guardar estado actual").
- *Aplicar:* haga clic en #boton("Aplicar") en el layout. El módulo se bloquea
  con _Aplicando layout «…»…_ hasta terminar, para que nadie toque el muro a
  medio aplicar. Las ventanas que conservan su cámara siguen sin cortarse.
- *Eliminar:* haga clic en #boton("Eliminar") y confirme. No se puede deshacer.

#nota[Aplicar un layout solo requiere *Operar el muro de video*. Guardar y
  eliminar layouts requiere además *Guardar diseños del muro*; sin ese
  permiso aparece _Su rol no le permite hacer esto (requiere "Guardar diseños
  del muro")._]

== Limpiar y sincronizar

#requiere("Operar el muro de video")

- #boton("Limpiar todo") pregunta _¿Detener todas las ventanas de "…"?_ y, al
  confirmar, deja el muro vacío.
- #boton("Sincronizar") vuelve a enviar al decodificador la división de
  ventanas guardada en el sistema. Úselo si el muro no coincide con lo que
  muestra la pantalla, por ejemplo después de que el decodificador estuvo
  apagado o se reinició.

== Operar el muro desde el panel web

#requiere("Operar el muro de video")

El panel web tiene la misma pantalla en #menu("Videowall") (sección
Aplicaciones), con el selector *Muro:*, #boton("Actualizar"),
#boton("Layouts"), #boton("Sincronizar"), #boton("👁 Vista previa"),
#boton("▣ Flotante") y #boton("Limpiar todo"), el muro dibujado y el panel
*Cámaras*. La página se actualiza sola cada pocos segundos.

Los gestos son los mismos del cliente: arrastrar una cámara a una ventana,
arrastrar una ventana sobre otra para intercambiarlas, la ✕ para cerrar un
video, doble clic para la pantalla completa del monitor, #tecla("Ctrl") +
doble clic para todo el muro, y el clic derecho con sus opciones (además de
las del cliente, #boton("⛶ Pantalla completa del monitor") o
#boton("⊡ Volver al mosaico")). Además:

- Con una sola ventana seleccionada, un clic en una cámara del panel la pone
  en esa ventana.
- Para agrupar, seleccione las ventanas con #tecla("Ctrl") + clic y use
  #boton("⊞ Agrupar N") del encabezado del monitor.
- Al dibujar una flotante, la ventana *Nueva ventana flotante* pide la
  *Cámara* y el *Stream*; se abre con #boton("Abrir en el muro"). La flotante
  se mueve arrastrándola y cambia de tamaño desde su esquina inferior
  derecha.

== Configuración

#requiere("Fuentes de video")

El muro se configura en el panel web en dos pasos: primero se agrega el
decodificador y después se crea el muro sobre él. Ambas páginas están en
#menu("Dispositivos", "Videowalls").

=== Agregar un decodificador

En #menu("Dispositivos", "Videowalls", "Decodificadores"):

+ Haga clic en #boton("Agregar decodificador").
+ Escriba el *Nombre* (por ejemplo, _Decoder sala de control_).
+ En *Driver*, elija _Hikvision (HCNetSDK)_.
+ Escriba la *Dirección (IP o host)*, el *Puerto SDK* (8000 por omisión), el
  *Usuario* y la *Contraseña*. *Notas* es opcional.
+ Deje marcado *Activo*.
+ Haga clic en #boton("Guardar"). El sistema se conecta con el equipo para
  validar las credenciales y leer sus capacidades; aparece _Decodificador
  guardado._

Debajo de la tabla, *Decodificadores en línea* lista los decodificadores y
controladores de muro que el servidor encuentra en su red; #boton("Agregar")
abre el formulario con los datos ya completos.

En la tabla de decodificadores:

- #boton("Probar") se conecta con el equipo y muestra cuántos canales de
  decodificación tiene, su número de serie y sus salidas físicas, cada una
  con su canal display.
- #boton("Diagnóstico") muestra el estado real del equipo (muro, ventanas y
  decodificación). Sirve para el soporte técnico.
- #boton("Editar") y #boton("Eliminar"). Al editar, deje la *Contraseña* en
  blanco para conservarla.

=== Crear un muro

En #menu("Dispositivos", "Videowalls", "Muro de video"):

+ Haga clic en #boton("Crear muro").
+ Escriba el *Nombre* y elija el *Decodificador*.
+ Indique las *Filas* y *Columnas* de monitores del muro (de 1 a 8).
+ Haga clic en #boton("Leer salidas del decodificador"). El sistema informa
  cuántas salidas y canales de decodificación tiene, y propone una salida
  para cada posición.
+ En cada posición (F1 · C1, F1 · C2…), elija la *Salida* física del
  decodificador que alimenta ese monitor, o _— sin usar —_ si no hay
  monitor, y en *Ventanas* en cuántas se divide (_1 (completa)_ o _N
  ventanas_; las opciones dependen de la salida).
+ Haga clic en #boton("Guardar muro"). Aparece _Muro de video guardado._

#captura-pendiente("Panel web: formulario Nuevo muro de video con una grilla de 2×2, la salida y las ventanas elegidas en cada posición")

#importante[Cada ventana ocupa un canal de decodificación del equipo; el
  sistema los reparte solo. Si las ventanas pedidas superan lo que el equipo
  tiene, aparece _La configuración necesita N canales de decodificación pero
  el decodificador solo tiene N. Reduzca la cantidad de ventanas._]

Cada muro aparece en la página como una tarjeta con su grilla y los botones
#boton("Layouts"), #boton("Sincronizar"), #boton("Limpiar todo"),
#boton("Editar") y #boton("Eliminar"). En esa grilla, un clic en una ventana
abre un formulario para elegir *Dispositivo*, *Canal* y *Stream*
(#boton("Mostrar en el muro") o #boton("Liberar")), doble clic la pone en
pantalla completa y clic derecho la libera. Para la operación diaria es más
cómoda la pantalla #menu("Videowall").

=== Mensajes de la configuración

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [Primero registre un decodificador.], [Agregue el decodificador antes de crear el muro.],
  [Configure al menos una pantalla.], [Elija una salida en al menos una posición.],
  [La división de ventanas no llegó al decodificador (N pantalla(s)): use "Sincronizar" cuando esté en línea.], [El muro quedó guardado, pero el equipo no respondió. Cuando vuelva a estar en línea, haga clic en #boton("Sincronizar").],
  [No se pudo leer el decodificador para validar canales (se asignan desde el canal 1): …], [Revise que el decodificador esté encendido y en la red, y vuelva a guardar.],
  [Ya existe un decodificador con esa dirección y puerto.], [El decodificador ya está agregado.],
  [El módulo «Muro de video» no está incluido en la licencia. Amplíe la licencia para habilitarlo.], [La licencia no trae el muro de video: contacte a su proveedor.],
  [La licencia permite N muros y ya hay N en uso. Amplíe la licencia para agregar más.], [Cada muro ocupa un cupo. Elimine uno que no use o amplíe la licencia.],
  [La licencia permite N decodificadores y ya hay N en uso. Amplíe la licencia para agregar más.], [Cada decodificador activo ocupa un cupo. Desactive o elimine uno que no use, o amplíe la licencia.],
)
