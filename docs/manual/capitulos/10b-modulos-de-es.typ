#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/io-modules.js, src/TrueCentralVms.Server/Api/IoModulesApi.cs, src/TrueCentralVms.Server/Services/IoModuleService.cs, src/TrueCentralVms.Server/Services/Workflows/Actions/RelayAction.cs, src/TrueCentralVms.Drivers.Adam/AdamModel.cs, src/TrueCentralVms.Server/Services/EquipmentParts.cs (entradas y salidas de otros equipos), src/TrueCentralVms.Drivers.Hikvision/HikvisionIoDriver.cs, src/TrueCentralVms.Drivers.Hikvision/HikvisionSdkIoDriver.cs, src/TrueCentralVms.Drivers.Hikvision/HikvisionAcsIoDriver.cs

= Módulos de entradas y salidas <cap-modulos-de-es>

Los módulos de entradas y salidas (E/S) conectan el sistema con equipos
eléctricos simples. Sus *salidas* (relés) levantan una barrera, abren un
portón o encienden una baliza o una sirena. Sus *entradas* leen un pulsador,
el lazo detector de una barrera o el sensor de una puerta.

El sistema trabaja con los módulos Advantech ADAM-6050, 6051, 6052, 6055, 6060
y 6066, conectados a la red. También usa como módulos las entradas y salidas
de alarma de los terminales de acceso, las cámaras y los grabadores Hikvision
(vea «Entradas y salidas de alarma de otros equipos»).

#captura-pendiente("Panel web: Dispositivos › Módulos de E/S con un módulo en línea, sus entradas (una activa) y sus relés con los botones Pulso, Encender y Apagar", alto: 6cm)

== Ver las entradas y los relés

#requiere("Operar relés", "Módulos de E/S")

Abra #menu("Dispositivos", "Módulos de E/S"). Cada módulo es una tarjeta con:

- su nombre y su ubicación (o _Por ubicar_);
- su dirección, su modelo y el estado de la conexión: _En línea_, _Sin
  conexión_, _Pausado_ o _Conectando…_. Sin conexión, debajo aparece el motivo;
- a la izquierda, sus *Entradas*: cada una con su nombre, su rótulo en el
  equipo (DI0, DI1…) y su estado, _Activa_ (punto verde) o _En reposo_;
- a la derecha, sus *Salidas (relés)*: cada una con su nombre, su rótulo
  (DO0, DO1…) y su estado, _Encendida_ (punto verde) o _Apagada_.

El estado se actualiza solo, en cuanto el módulo informa un cambio. Si el
módulo no está en línea, las entradas y salidas muestran _Sin lectura_.

#nota[Al dejar el puntero sobre el nombre de una entrada se ve si se
  considera activa al cerrar o al abrir el contacto (vea «Agregar un módulo»).
  En las de un terminal, una cámara o un grabador no aparece: eso lo decide el
  propio equipo.]

== Operar un relé a mano

#requiere("Operar relés")

Cada salida tiene tres botones:

- #boton("Pulso"): enciende el relé y lo apaga solo después de los segundos
  escritos en la casilla de al lado (1 por omisión; de 0,1 a 3600). Es la
  orden para levantar una barrera o abrir un portón.
- #boton("Encender"): enciende el relé y lo deja así.
- #boton("Apagar"): apaga el relé. También corta un pulso o un parpadeo en
  curso.

Al dar la orden aparece un aviso con lo que se hizo, por ejemplo _Dio un pulso
de 1 s a 'Barrera estacionamiento' (DO0) de 'Gabinete acceso vehicular'._ Cada
orden queda en la bitácora de auditoría con el usuario que la dio.

Si el módulo no responde, el aviso dice _El módulo '…' no recibió la orden
sobre …_ con el motivo. Los botones de un módulo fuera de su alcance aparecen
deshabilitados.

#modulo("automation")[
  == Actuar solo: barreras, portones y balizas

  Las automatizaciones usan los módulos de dos formas:

  - la acción _Operar relé_ da un pulso, enciende, apaga, alterna o hace
    parpadear relés;
  - el disparador _Entrada de módulo de E/S_ se ejecuta cuando una entrada se
    activa o vuelve a reposo.

  Ejemplo: levantar la barrera a las patentes autorizadas.

  + Disparador _Lectura de patente_, con la cámara de la barrera y *Patentes*
    en _Solo las de la lista (lista blanca)_.
  + Acción _Operar relé_ con el relé de la barrera y *Orden* en _Pulso_, de 1
    segundo.
  + Desde su salida *Si falla*, la acción _Avisar a los operadores_, para que
    el guardia abra a mano.

  Vea #capitulo(<cap-automatizaciones>).
]

== Entradas y salidas de alarma de otros equipos

Los terminales de acceso, las cámaras y los grabadores Hikvision que tienen
entradas y salidas de alarma las aportan como un módulo de E/S más, incluido
con el equipo: no ocupa módulo de la licencia. El módulo se crea solo al
agregar o revalidar el equipo, si en su formulario está marcada la casilla
*Sus entradas y salidas de alarma, como un módulo de E/S* (vea
#capitulo(<cap-fuentes-de-video>)#modulo("access")[ y
#capitulo(<cap-control-de-acceso>)]), y lleva el nombre del equipo.

Su tarjeta se distingue por la línea bajo el nombre: _Entradas y salidas de
alarma de un terminal de acceso · incluidas, no ocupan licencia_ o _Entradas y
salidas de alarma de una cámara o grabador · incluidas, no ocupan licencia_. En
vez del modelo muestra cuántas tiene, por ejemplo _2 entrada(s) · 1
salida(s)_. De fábrica, cada una se llama como la numera el equipo: _Entrada
de alarma 1_, _Salida de alarma 1_…, con su rótulo DI0, DO0…

Se operan igual que las de un módulo ADAM: #boton("Pulso"), #boton("Encender")
y #boton("Apagar") en la tarjeta#modulo("automation")[, y en las
automatizaciones la acción _Operar relé_ (con estos equipos, el parpadeo no
puede alternar más rápido que cada 1 segundo) y el disparador _Entrada de
módulo de E/S_]. Las diferencias:

- Si una entrada está _Activa_ o _En reposo_ lo informa el propio equipo:
  aquí no se elige si se activa al cerrar o al abrir el contacto.
- El estado se lee cada 2 a 5 segundos, según el equipo. Un pulsador
  apretado por menos tiempo puede pasar inadvertido.

En vez de #boton("Editar") y #boton("Eliminar"), la tarjeta tiene
#boton("Nombres") y #boton("Ver equipo"), que abre el terminal en Control de
acceso o #menu("Dispositivos", "Fuentes de video"). La dirección y las
credenciales son las del equipo y se actualizan solas cuando cambian allá.
Para quitar estas entradas y salidas, desmarque la casilla en el formulario
del equipo. Si elimina el equipo, el módulo se va con él; si desactiva un
terminal, su módulo queda pausado.

#nota[Estas entradas y salidas no se agregan con #boton("Agregar módulo"): la
  lista *Marca / protocolo* no las ofrece.]

=== Nombrar sus entradas y salidas

#requiere("Módulos de E/S")

+ Haga clic en #boton("Nombres") en la tarjeta.
+ En la ventana _Entradas y salidas de «…»_, escriba en cada fila de
  *Entradas de alarma* y *Salidas de alarma* el nombre de lo que tiene
  cableado (hasta 64 caracteres), por ejemplo _Sensor puerta bodega_.
+ Haga clic en #boton("Guardar"). Aparece _Nombres guardados._

Con esos nombres aparecen en el Centro de mando, en las automatizaciones y en
la bitácora. Una fila que deje vacía vuelve al nombre de fábrica (_Entrada de
alarma 1_, _Salida de alarma 2_…).

== Configuración

=== Agregar un módulo

#requiere("Módulos de E/S")

+ Abra #menu("Dispositivos", "Módulos de E/S") y haga clic en
  #boton("Agregar módulo").
+ Escriba un *Nombre*, por ejemplo _Gabinete acceso vehicular_, y elija su
  *Ubicación*.
+ Deje *Marca / protocolo* en _Advantech ADAM-6000 (Modbus TCP, puerto 502)_.
+ En *Modelo* deje _Automático (lo informa el equipo)_: el sistema lo lee del
  módulo. Elíjalo a mano solo si el módulo no informa su modelo o si todavía
  no está conectado.
+ Escriba la *Dirección (IP o hostname)* del módulo. El *Puerto Modbus TCP* es
  502, salvo que se haya cambiado en el equipo.
+ *Leer las entradas cada (ms)*: 250 detecta un pulsador o el paso de un
  vehículo sin cargar la red. Admite de 100 a 5000.
+ Haga clic en #boton("Probar conexión"). Si responde, aparece _✔ Conexión
  validada_ con el modelo que informa el equipo, el modelo con que se
  trabajará y cuántas entradas y salidas tiene. Debajo aparece la lista de
  entradas y salidas.
+ Escriba un nombre para cada entrada y cada relé que use (_Barrera
  estacionamiento_, _Baliza_, _Lazo de salida_). Los que deje vacíos se
  muestran con su rótulo (DI0, DO0…).
+ Para cada entrada elija cuándo se considera activa: _Activa al cerrar el
  contacto_ o _Activa al abrir el contacto_.
+ Haga clic en #boton("Guardar").

#consejo[En contacto seco, el módulo lee 1 con el contacto abierto. Un
  pulsador, un lazo o un sensor normal abierto queda «activo» al cerrarse: deje
  _Activa al cerrar el contacto_. Para un sensor normal cerrado (por ejemplo,
  el de una puerta que se abre) elija _Activa al abrir el contacto_.]

Si el módulo no responde y el modelo está en _Automático_, el alta se rechaza:
elija el modelo a mano para darlo de alta igual. Así queda guardado y el
servidor lo sigue intentando; el aviso dice _El módulo no respondió (…);
quedó dado de alta y el servidor lo seguirá intentando._

#importante[Antes de cablear una barrera o una cerradura, configure el
  *watchdog de comunicación* del propio módulo (valores de seguridad FSV,
  desde su página web o el Adam.NET Utility). El servidor controla la duración
  de los pulsos: si se detiene de golpe a mitad de un pulso, el relé queda
  encendido hasta que el watchdog del módulo lo suelte. Al detenerse de forma
  normal, el servidor suelta los relés que estaban en un pulso o un parpadeo.]

=== Editar, pausar y eliminar

#requiere("Módulos de E/S")

- #boton("Editar") abre el mismo formulario. Cambiar la dirección, el puerto
  o el modelo vuelve a probar la conexión antes de guardar.
- Desmarcar *Activo* pausa el módulo: el servidor cierra su conexión, suelta
  los relés que estaban en un pulso o un parpadeo y no lo ofrece a los
  operadores ni a las automatizaciones.
- #boton("Eliminar") pide confirmar: _¿Eliminar el módulo "…"? Las
  automatizaciones que usan sus relés o entradas dejarán de funcionar._

El servidor debe ser el único que se conecta a cada módulo: estos equipos
admiten pocas conexiones a la vez. Cierre los programas de prueba del
fabricante mientras el servidor lo esté usando.

=== Cupo de la licencia

La licencia fija cuántos módulos de E/S pueden estar activos a la vez. Las
entradas y salidas de terminales, cámaras y grabadores no cuentan. Si se
supera, el alta o la activación se rechaza con uno de estos mensajes:

- _La licencia permite N módulos y ya hay N en uso. Amplíe la licencia para
  agregar más._ Pause otro módulo o amplíe la licencia.
- _El módulo «Módulos de E/S» no está incluido en la licencia. Amplíe la
  licencia para habilitarlo._

=== Mensajes de la configuración

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [No se pudo conectar con el módulo en …], [Revise la dirección, el puerto y que
    el módulo esté encendido y en la misma red que el servidor.],
  [El módulo en … no respondió a tiempo.], [Revise la red; si el módulo está
    lejos, pruebe de nuevo.],
  [El módulo no informa su modelo. Elíjalo a mano.], [Elija el *Modelo* en la
    lista.],
  [El equipo se identifica como …, que no es un módulo de E/S digital
    conocido.], [Elija a mano el modelo con la misma cantidad de entradas y
    salidas.],
  [El equipo se identifica como …, pero se trabajará como … (elegido a
    mano).], [Compruebe que el modelo elegido sea el correcto.],
  [Ya existe un módulo con esa dirección y puerto.], [Ese módulo ya está
    agregado: edítelo en vez de agregarlo de nuevo.],
)
