#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/LiveView.xaml (panel PARLANTES), src/TrueCentralVms.Client/Views/LiveView.xaml.cs, src/TrueCentralVms.Client/ViewModels/SpeakersViewModel.cs, src/TrueCentralVms.Client/Services/SpeakerTalkClient.cs, src/TrueCentralVms.Server/wwwroot/speakers.js, src/TrueCentralVms.Server/wwwroot/live.js (panel de parlantes), src/TrueCentralVms.Server/wwwroot/workflows.js (renderSounds), src/TrueCentralVms.Server/Api/SpeakersApi.cs, src/TrueCentralVms.Server/Services/SpeakerService.cs, src/TrueCentralVms.Server/Api/WorkflowsApi.cs (sonidos)

= Parlantes IP <cap-parlantes-ip>

Los parlantes IP permiten hablarle a la gente en terreno desde el puesto de
monitoreo: dar un aviso con el micrófono, hacer sonar una sirena o un mensaje
grabado, o hacer que el parlante lea un texto en voz alta. Se puede usar un
parlante o varios a la vez, y los sonidos del servidor suenan sincronizados en
todos los elegidos.

#captura-pendiente("Cliente de monitoreo: vista en vivo con el panel PARLANTES desplegado en la columna izquierda, dos parlantes marcados", alto: 6cm)

== El panel de parlantes

#requiere("Usar parlantes")

En el cliente de monitoreo, los parlantes se operan desde el panel
*PARLANTES* de la vista en vivo, en la columna izquierda, debajo de la lista
de cámaras. El panel aparece solo si hay parlantes configurados y parte
minimizado; la flecha de su esquina lo despliega o lo vuelve a minimizar.

El título del panel resume qué parlantes están marcados: _PARLANTES —
ninguno_, el nombre del parlante o la cantidad (_PARLANTES — 3 parlantes_).
Todo lo que haga en el panel se aplica a los parlantes marcados.

+ Despliegue el panel.
+ Marque la casilla de cada parlante que quiere usar. Para marcarlos todos de
  una vez use el botón *Marcar todos los parlantes*, junto a la flecha; el
  mismo botón los desmarca.

Cada parlante muestra un punto verde (en línea) o rojo (sin conexión). Si
está ocupado, junto al nombre aparece en qué: _(Voz: usuario)_ si alguien
está hablando por él o _(Sonido: nombre)_ si está sonando un sonido del
servidor. Al dejar el puntero sobre un parlante se ven su modelo, dirección,
grupo, estado y volumen.

#nota[Si solo hay un parlante, queda marcado solo. Los parlantes fuera de su
  alcance se ven en la lista, pero no se pueden marcar.]

Bajo los controles, una línea de estado informa el resultado de cada orden;
por ejemplo, _Reproducido en 2 parlantes._ o el motivo por el que un
parlante no respondió.

== Hablar por los parlantes

#requiere("Usar parlantes")

=== Elegir y probar el micrófono

Si el equipo tiene micrófonos, el panel muestra una lista para elegir cuál
usar. El cliente lo recuerda para la próxima vez y también lo usa para la
citofonía.

+ Elija el micrófono en la lista.
+ Haga clic en #boton("Probar") y hable: la barra de nivel debe moverse. La
  prueba no transmite nada a los parlantes.
+ Haga clic otra vez en #boton("Probar") para terminar la prueba.

#consejo[Si la barra se pone roja al hablar, está muy cerca del micrófono o
  habla muy fuerte: el sonido saldrá distorsionado. Si casi no se mueve, en
  los parlantes apenas se escuchará; revise que eligió el micrófono correcto.]

=== Dar un aviso

+ Marque los parlantes.
+ Mantenga presionado el botón #boton("Mantener para hablar"). Mientras lo
  mantiene, el botón dice #boton("● Hablando… (suelte para terminar)").
+ Hable.
+ Suelte el botón para terminar. Si mueve el puntero fuera del botón sin
  soltarlo, la voz también se corta.

Con la casilla *Tono al abrir el canal (dos pitidos antes de la voz)*, los
parlantes emiten dos pitidos cuando están listos para recibir su voz, como el
aviso de canal abierto de una radio. Espere los pitidos antes de hablar.

Al abrirse el canal, la línea de estado muestra _Hablando por …_ con los
parlantes que quedaron escuchando, y _No aceptaron →_ con los que no y su
motivo. Al soltar, muestra _Voz terminada (… s): …_ con la duración y el
motivo del cierre.

#nota[La voz se corta sola tras 20 segundos sin audio o a los 10 minutos.]

== Reproducir sonidos y mensajes

#requiere("Usar parlantes")

=== Un sonido del servidor

Los sonidos del servidor son los que el administrador cargó en la Biblioteca
de sonidos (vea la sección _Biblioteca de sonidos_ de este capítulo). Se
transmiten desde el servidor y suenan sincronizados en todos los parlantes
marcados.

+ Elija el sonido en la lista.
+ Para oírlo antes solo en su equipo, haga clic en el botón con forma de oreja
  (*Escuchar en ESTE equipo*). No suena en los parlantes; otro clic lo
  detiene.
+ Haga clic en el botón de reproducir (*Reproducir el sonido del servidor en
  los PARLANTES marcados*).

Con un sonido largo, la línea de estado muestra _Transmitiendo … a N
parlantes…_ mientras suena. Un sonido de más de 2 minutos se corta en ese
punto.

=== Un audio guardado en el parlante

Algunos parlantes tienen su propia biblioteca de audios (sirenas, avisos de
fábrica y los que cargue el administrador). La lista muestra la biblioteca del
primer parlante marcado que tenga una; en los demás parlantes marcados, el
audio se busca por el mismo nombre.

+ Elija el audio en la lista.
+ Si quiere oírlo antes en su equipo, use el botón con forma de oreja: el
  audio se descarga del parlante y suena solo en su equipo.
+ Haga clic en el botón de reproducir (*Reproducir el audio guardado en el
  PARLANTE*).

El botón de descarga (*Descargar el archivo desde el parlante a este equipo*)
guarda una copia del audio donde usted elija.

#nota[Si un parlante marcado no tiene un audio con ese nombre, la línea de
  estado lo indica con _El parlante no tiene un audio llamado '…' en su
  biblioteca._ y los demás lo reproducen igual.]

=== Leer un texto en voz alta

Los parlantes con texto a voz pueden leer un mensaje escrito. La fila aparece
solo si alguno de los parlantes marcados tiene esa función.

+ Escriba el mensaje en *Texto a leer en voz alta…* (hasta 100 caracteres).
+ Haga clic en el botón de reproducir (*El parlante lee el texto en voz
  alta*).

El texto se lee en español, con voz femenina. El parlante tarda unos segundos
en generar la voz la primera vez; si vuelve a leer el mismo texto, sale de
inmediato.

#nota[Cada texto leído queda guardado en la biblioteca del parlante, con un
  nombre que empieza con `tcvms-tts-`.]

=== Volumen y detener

- El deslizador *Volumen* (0 a 100) fija el volumen de salida de los
  parlantes marcados; parte en el volumen del primero. El cambio se aplica al
  dejar de moverlo y la línea de estado confirma _Volumen … aplicado a …
  parlantes._
- #boton("Detener") corta lo que esté sonando en los parlantes marcados,
  incluida su propia voz.

#importante[Un parlante atiende una sola cosa a la vez. Si está ocupado con la
  voz de otro operador o con un sonido del servidor, responde _El parlante
  está ocupado (…)_. Espere a que termine o deténgalo.]

=== Mensajes del panel

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [Marque al menos un parlante.], [Marque la casilla de uno o más parlantes.],
  [Este equipo no tiene micrófono disponible.], [Conecte un micrófono al equipo.],
  [No se pudo abrir el micrófono: …], [Elija otro micrófono o revise que no lo esté usando otra aplicación.],
  [El servidor no abrió la voz en vivo: el parlante está ocupado, sin conexión o no acepta audio en vivo.], [Revise el punto de estado del parlante y si está ocupado.],
  [El parlante no acepta audio en vivo.], [Ese modelo solo reproduce su biblioteca o textos.],
  [El parlante no genera texto a voz.], [Use otro parlante o un audio de su biblioteca.],
  [El parlante está desactivado.], [El administrador lo pausó en la configuración.],
  [Ningún parlante reprodujo el audio.], [Lea el motivo de cada parlante en la misma línea.],
)

== Usar los parlantes desde el panel web

#requiere("Usar parlantes")

La vista en vivo del panel web tiene el mismo panel *PARLANTES*, con los
mismos controles#modulo("video")[ (vea #capitulo(<cap-vista-en-vivo>))]. Para
hablar desde el navegador, el panel debe abrirse por HTTPS o desde el propio
servidor; si no, el botón #boton("Mantener para hablar") queda deshabilitado,
pero se pueden reproducir sonidos, audios y textos.

También se puede operar desde #menu("Dispositivos", "Parlantes IP"). La
tabla muestra cada parlante con sus *Funciones* (_Voz_, _Biblioteca_, _TTS_),
su *Volumen*, el estado de *Conexión* y si está *En uso*; se actualiza sola.

- El botón de *Volumen* de cada fila abre un deslizador; el cambio se aplica al
  soltarlo y #tecla("Esc") lo cierra.
- #boton("Reproducir") abre una ventana para hacer sonar ese parlante.
- #boton("Detener") corta lo que esté sonando.
- #boton("Biblioteca") despliega los audios guardados en el parlante, con
  #boton("🔈 Escuchar") (en el navegador), #boton("⬇ Descargar") y
  #boton("▶ Reproducir") (en el parlante).
- #boton("Reproducir en varios…"), arriba de la tabla, hace sonar varios
  parlantes a la vez.

Para reproducir desde la ventana:

+ Si eligió #boton("Reproducir en varios…"), marque los parlantes en
  *Parlantes*.
+ En *Qué reproducir*, elija el origen:
  - _Sonido del servidor_: elija el *Sonido* y cuántas *Repeticiones* (1 a
    5). El botón 🔈 lo escucha en el navegador.
  - _Audio de la biblioteca del parlante_: elija el audio o, con varios
    parlantes, escriba su nombre (se busca en cada uno).
  - _Texto leído en voz alta_: escriba el *Texto* (hasta 100 caracteres) y
    elija el *Idioma* y la *Voz* (_Femenina_ o _Masculina_).
+ Haga clic en #boton("Reproducir"). La ventana muestra el resultado de cada
  parlante con ✔ o ✖. #boton("Detener") corta el sonido sin cerrar la
  ventana.

== Configuración

#requiere("Parlantes IP")

Los parlantes se agregan en el panel web, en #menu("Dispositivos",
"Parlantes IP"). Son compatibles dos familias Hikvision:

#table(
  columns: (auto, 1fr),
  table.header[Marca / protocolo][Qué permite],
  [Hikvision (parlante IP por ISAPI)], [Voz en vivo, sonidos del servidor, biblioteca propia de audios y texto a voz, según el modelo.],
  [Hikvision DS-PA (adaptador de audio, RTP)], [Voz en vivo y sonidos del servidor. No tiene biblioteca ni texto a voz.],
)

#captura-pendiente("Panel web: Dispositivos › Parlantes IP con la tabla de parlantes y el formulario Agregar parlante IP con el resultado de Probar conexión")

=== Agregar un parlante

+ Haga clic en #boton("Agregar parlante").
+ Escriba el *Nombre* (por ejemplo, _Acceso proveedores_) y, si quiere, un
  *Grupo*. El grupo permite elegir varios parlantes de una vez en las
  automatizaciones.
+ Elija la *Ubicación* en el árbol de recursos (vea
  #capitulo(<cap-recursos-y-ubicaciones>)).
+ Elija la *Marca / protocolo*.
+ Escriba la *Dirección (IP o hostname)* y el *Puerto HTTP* (80 por omisión).
+ Escriba el *Usuario del parlante* y la *Contraseña*. Use un usuario local
  del parlante, normalmente _admin_.
+ Marque *Usar HTTPS (certificado autofirmado aceptado)* solo si el parlante
  se administra por HTTPS.
+ Deje marcada la casilla *Activo (sondeo de estado, disponible para
  operadores y automatizaciones)*.
+ Haga clic en #boton("Probar conexión"). El resultado muestra el *Modelo*, el
  *N° de serie*, el *Firmware* y si el parlante tiene *Voz en vivo*,
  *Biblioteca* (con la cantidad de audios) y *Texto a voz*, además de su
  *Volumen*.
+ Haga clic en #boton("Guardar"). Aparece _Parlante agregado y validado._

#nota[Al agregar un adaptador DS-PA que no tiene configurada la recepción de
  audio en su "Multicast Monitor", el sistema la configura sola.]

#consejo[Si un parlante responde pero no se escucha, revise su volumen en la
  columna *Volumen*: algunos equipos vienen de fábrica con el volumen muy
  bajo.]

Para modificar un parlante use #boton("Editar") (deje la *Contraseña* vacía
para conservar la actual). Para pausarlo sin borrarlo, desmarque *Activo*.
#boton("Eliminar") pide confirmación; un parlante en uso no se puede eliminar
hasta detenerlo.

=== La biblioteca de cada parlante

Con el permiso *Parlantes IP*, la #boton("Biblioteca") de cada fila suma estas
opciones:

- #boton("Subir archivo…"): carga un audio mp3, wav, aac o mp2 de hasta 20 MB.
  En *Nombre en el parlante* puede darle un nombre; vacío, usa el del archivo.
  El audio queda guardado en el propio parlante y se reproduce por nombre
  desde el cliente y las automatizaciones.
- #boton("Crear desde texto…") (solo parlantes con texto a voz): escriba el
  *Nombre del audio*, el *Texto* (hasta 100 caracteres), el *Idioma* y la
  *Voz*, y haga clic en #boton("Generar"). El parlante tarda unos segundos en
  crearlo.
- #boton("Renombrar"): cambia el nombre con que el parlante muestra el audio.
- #boton("Borrar"): quita el audio del parlante. Los audios marcados _de
  fábrica_ no se pueden borrar.

#importante[Las automatizaciones y la reproducción en varios parlantes buscan
  los audios por nombre. Si renombra o borra un audio, revise las
  automatizaciones que lo usan.]

=== Mensajes de la configuración

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [Ya existe un parlante con esa dirección y puerto.], [El parlante ya está agregado.],
  [La contraseña del parlante es obligatoria.], [Escriba la contraseña.],
  [El parlante rechazó las credenciales (usuario o contraseña incorrectos).], [Revise el usuario y la contraseña.],
  [El equipo respondió algo que no es la API de un DS-PA: verifique la dirección y el puerto.], [Revise la dirección, el puerto y la *Marca / protocolo* elegida.],
  [El módulo «Parlantes IP» no está incluido en la licencia. Amplíe la licencia para habilitarlo.], [La licencia no trae parlantes: contacte a su proveedor.],
  [La licencia permite N parlantes y ya hay N en uso. Amplíe la licencia para agregar más.], [Cada parlante activo ocupa un cupo. Pause o elimine uno, o amplíe la licencia.],
  [El parlante está en uso (…); deténgalo antes de eliminarlo.], [Haga clic en #boton("Detener") y vuelva a intentar.],
  [El audio no puede pesar más de 20 MB.], [Use un archivo más liviano.],
  [Formato no admitido por el parlante: use mp3, wav, aac o mp2.], [Convierta el archivo a uno de esos formatos.],
)

== Biblioteca de sonidos

#requiere("Biblioteca de sonidos")

La Biblioteca de sonidos guarda en el servidor los sonidos que se transmiten
a los parlantes: los que el operador elige en el panel *PARLANTES* y los que
usan las automatizaciones#modulo("automation")[ (vea
#capitulo(<cap-automatizaciones>))]. Las automatizaciones también pueden
tocarlos como alarma sonora en el equipo del operador. Se abre en el panel web
desde #menu("Biblioteca de sonidos") o con el botón #boton("Sonidos") de la
página Automatizaciones.

#nota[Algunas pantallas llaman a estos sonidos _Automatizaciones → Sonidos_ o
  _Sonido del servidor_: son los mismos de la Biblioteca de sonidos.]

#captura("web-biblioteca-sonidos.png", pie: [Biblioteca de sonidos.])

=== Subir un sonido

+ Haga clic en el selector de archivo y elija el audio (WAV, MP3 u otro
  formato de audio, de hasta 8 MB).
+ Haga clic en #boton("Subir"). Aparece _Sonido subido y convertido._

El sistema convierte el sonido al formato que usan los parlantes. El nombre
del sonido es el del archivo sin la extensión; si sube otro archivo con el
mismo nombre, reemplaza al anterior.

#importante[El servidor necesita FFmpeg para convertir los sonidos. Si no lo
  tiene, la página muestra _El servidor no tiene FFmpeg disponible: los
  sonidos no se pueden convertir ni amplificar._ y los sonidos quedan _Sin
  convertir_, sin poder sonar en los parlantes. Avise al soporte.]

=== La tabla de sonidos

#table(
  columns: (auto, 1fr),
  table.header[Columna][Contenido],
  [Sonido], [Nombre del sonido y, debajo, el archivo guardado.],
  [Duración, Tamaño], [Largo y peso del sonido.],
  [Nivel (pico)], [La parte más fuerte del sonido, en decibeles respecto del máximo posible (0 dB). La barra se pone roja cerca de 0 dB.],
  [Medio], [El volumen promedio del sonido.],
  [Estado], [_Listo_ para sonar, o _Sin convertir_.],
)

Botones de cada sonido:

- #boton("▶"): lo escucha en el navegador.
- #boton("Amplificar"): sube el sonido justo hasta antes de saturar. El botón
  indica cuánto lo subirá (por ejemplo, _Amplificar +6.0 dB_); si el sonido ya
  está al máximo dice _Al máximo_ y queda deshabilitado.
- #boton("dB…"): aplica una ganancia a elección. Escriba los decibeles
  (positivo amplifica, negativo atenúa); la pregunta indica el máximo sin
  saturar.
- #boton("Eliminar"): borra el sonido, previa confirmación. Las automatizaciones
  que lo usen quedan sin sonido.

#consejo[Si un sonido se oye bajo en los parlantes, use #boton("Amplificar")
  antes de subir el volumen de los equipos. La ganancia no afecta a los textos
  leídos por el parlante, que suenan al volumen del equipo.]

=== Audios en los parlantes

Al final de la página, *Audios en los parlantes* muestra, solo para consultar,
la biblioteca de cada parlante: *Nombre*, *Formato*, *Duración*, *Tamaño* y
*Origen* (_de fábrica_ o _cargado_). Esos audios se administran desde
#menu("Dispositivos", "Parlantes IP").
