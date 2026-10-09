#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/app.js, src/TrueCentralVms.Server/wwwroot/services.js, src/TrueCentralVms.Server/wwwroot/license.js, src/TrueCentralVms.Server/Api/SystemApi.cs, src/TrueCentralVms.Server/Api/StreamsApi.cs, src/TrueCentralVms.Server/Api/LicenseApi.cs, src/TrueCentralVms.Server/Services/Supervisor/ServiceSupervisor.cs, src/TrueCentralVms.Server/Services/Licensing/LicenseService.cs

= Sistema <cap-sistema>

Tres páginas del panel web sirven para vigilar y mantener el servidor:
*Sesiones* (quién está conectado y qué video está viendo), *Servicios* (el
estado de las piezas internas del servidor) y *Licencia* (qué permite la
licencia y cuánto se está usando). En el *Panel*, la página de inicio, las
tarjetas _Licencia_ y _Servicios del servidor_ resumen su estado y llevan a
estas páginas.

#captura("web-servicios.png", ancho: 90%, pie: [Sistema › Servicios, con un servicio detenido a mano.])

== Sesiones

#requiere("Sesiones")

Abra #menu("Streaming", "Sesiones"). La página tiene dos partes y se actualiza
cada 5 segundos.

#captura("web-sesiones.png", pie: [Streaming › Sesiones.])

=== Clientes de escritorio

Cada equipo donde se abre el cliente de monitoreo ocupa un *puesto* de los que
permite la licencia; el panel web no ocupa puestos. El puesto es del equipo, no
de la versión del cliente: si en un mismo equipo se ingresa de nuevo (también
con otra versión), se reemplaza la sesión anterior en vez de sumar otra.

El título indica cuántos puestos están en uso y cuántos permite la licencia.
La tabla muestra, por equipo: *Equipo*, *Usuario* (y si es administrador u
operador), *Versión del cliente*, *Ingresó*, *Vence* y *Estado*:

#table(
  columns: (auto, 1fr),
  table.header[Estado][Significado],
  [Conectado], [El cliente está abierto y conectado al servidor.],
  [Sin conexión (sesión colgada)],
  [El cliente se cerró sin cerrar sesión (por ejemplo, se apagó el equipo). El
    puesto sigue ocupado hasta que la sesión venza.],
)

Las sesiones duran 12 horas desde el ingreso. Al cerrar el cliente
normalmente, su puesto se libera.

=== Liberar un puesto

Cuando todos los puestos están ocupados, la página lo advierte: _Están todos
los puestos de cliente ocupados: nadie más puede ingresar con el cliente de
escritorio._ En el cliente, quien intente ingresar ve un mensaje como _La
licencia permite 3 clientes y ya hay 3 en uso. Amplíe la licencia para agregar
más._ Para liberar un puesto:

+ En la fila del equipo, haga clic en #boton("Liberar puesto") (sesión colgada)
  o en #boton("Desconectar") (cliente conectado).
+ Lea el aviso y confirme.

Liberar una sesión colgada no afecta a nadie. Desconectar un cliente abierto lo
devuelve a la pantalla de ingreso, corta su video y le impide volver a entrar
durante un minuto; el cliente muestra _Un administrador cerró la sesión de este
equipo para liberar su puesto. Podrá volver a ingresar en un minuto._

=== Sesiones de video activas

Una fila por cada cámara que alguien está viendo, con las columnas *Usuario*,
*Dispositivo*, *Canal*, *Perfil* (_Principal_ o _Secundario_), *IP del
espectador*, *Inicio* y *Duración*. Si nadie está viendo video, dice _Nadie está
viendo video en este momento._

Para cortar una transmisión:

+ Haga clic en #boton("Expulsar") en su fila.
+ Confirme.

#nota[Expulsar corta esa transmisión, pero no bloquea al usuario: puede volver
  a abrir la cámara. Para impedir que siga entrando, deshabilítelo en
  #menu("Seguridad", "Usuarios") (#capitulo(<cap-usuarios-y-roles>)).]

== Servicios

Abra #menu("Sistema", "Servicios"). Todos los usuarios pueden ver esta página;
los botones solo aparecen a quien tiene el permiso _Servicios del servidor_
(los demás ven _Sin permiso_).

Arriba, las tarjetas muestran:

- *Servidor*: si corre como _Servicio de Windows_ (con su nombre) o como
  _Consola (desarrollo)_, la versión y el número de proceso;
- *En ejecución desde*: cuánto lleva funcionando sin reiniciarse;
- *Servicios*: cuántos están en ejecución, y cuántos caídos si los hay;
- *Memoria del servidor*;
- *Supervisión*: cada cuántos segundos se comprueba la salud de los servicios.

Debajo, la tabla *Supervisor de servicios* tiene una fila por servicio, con
las columnas *Servicio* (nombre y descripción), *Tipo* (_Proceso_ o
_Interno_), *Estado*, *Desde*, *Reinicios*, *Último error*, *Auto-reinicio* y
los botones. La tabla se actualiza cada 3 segundos.

=== Estados

#table(
  columns: (auto, 1fr),
  table.header[Estado][Significado],
  [En ejecución], [Funciona normalmente.],
  [Iniciando], [Está arrancando o se está relanzando solo. Espere unos segundos.],
  [Deteniendo], [Se está deteniendo.],
  [Detenido], [Lo detuvo un administrador. El supervisor no lo vuelve a iniciar
    hasta que alguien lo inicie a mano.],
  [Caído], [Dejó de responder o terminó con un error. Si tiene el
    auto-reinicio activado, bajo el error se ve la cuenta regresiva del
    próximo reintento.],
  [Deshabilitado], [Está apagado en la configuración del servidor; no se puede
    iniciar desde esta página.],
)

=== Servicios supervisados

#table(
  columns: (auto, 1fr),
  table.header[Servicio][Qué hace],
  [Base de datos (PostgreSQL embebido)], [Guarda usuarios, dispositivos, eventos y la bitácora. Es esencial: solo se puede reiniciar.],
  [Media server (MediaMTX)], [Entrega el video: toma una sola conexión por cámara y la comparte entre todos los que la miran.],
  [Monitor de dispositivos], [Revisa cada equipo de video y publica si está en línea o sin conexión.],
  [Contabilidad de sesiones], [Mantiene al día la lista de sesiones de video y cierra en la bitácora las que terminaron.],
  [Paneles de alarma], [Estado y eventos de cada panel de alarma habilitado.],
  [Receptor de alarmas (SIA DC-09)], [Recibe los reportes que envían los paneles de alarma por la red.],
  [Control de acceso], [Estado de terminales y controladoras, y modo de cada puerta.],
  [Padrón de control de acceso], [Escribe personas, credenciales, horarios y permisos en los equipos de acceso.],
  [Historial de accesos], [Trae de cada equipo quién pasó por cada puerta.],
  [Hora de los equipos], [Revisa la hora de los equipos y los pone en hora cuando se desfasan.],
  [Parlantes IP], [Estado de los parlantes, reproducción y voz en vivo.],
  [Citofonía], [Llamadas de los frentes de citofonía y conversaciones de los operadores.],
  [Reconocimiento de patentes], [Recibe las lecturas de patentes de los equipos marcados como fuente.],
  [Automatizaciones], [Ejecuta las acciones configuradas cuando ocurre un evento.],
  [Retención de la bitácora], [Borra los eventos antiguos de la bitácora, si se configuró un plazo de retención.],
)

=== Reinicio automático

Cuando un servicio cae, el supervisor lo reinicia solo, con esperas crecientes
(5, 10, 20, 40 y 60 segundos) hasta agotar los reintentos. Si no lo logra, el
servicio queda _Caído_ con el mensaje _Se agotaron los … reintentos
automáticos: revise el registro del servidor y reinícielo a mano._

=== Activar o desactivar el reinicio automático

#requiere("Servicios del servidor")

La casilla *Auto* de cada fila activa o desactiva el reinicio automático de ese
servicio. Con la casilla desmarcada, un servicio caído espera a que alguien lo
reinicie.

#nota[La elección de la casilla *Auto* dura hasta que el servidor se reinicia:
  después, todos los servicios vuelven a tener el reinicio automático
  activado.]

=== Iniciar, detener o reiniciar un servicio

#requiere("Servicios del servidor")

+ En la fila del servicio, haga clic en #boton("Iniciar"), #boton("Detener") o
  #boton("Reiniciar"). Solo se habilitan los que tienen sentido según su estado.
+ Para detener o reiniciar, confirme.

Un servicio detenido a mano no se vuelve a iniciar solo: el supervisor lo deja
así hasta que alguien haga clic en #boton("Iniciar"). Al reiniciar, las
operaciones en curso de ese servicio pueden fallar durante unos segundos. La
base de datos no se puede detener, solo reiniciar.

Cada caída, reinicio y orden queda en la bitácora de auditoría, en la categoría
_Sistema_.

=== Reiniciar el servidor completo

#requiere("Servicios del servidor")

+ Haga clic en #boton("Reiniciar servidor completo").
+ Confirme. El servidor responde _Reinicio solicitado: el servicio de Windows se
  detiene y vuelve a arrancar en unos segundos._

#importante[Reiniciar el servidor corta todas las sesiones de video y desconecta
  a todos los usuarios: los del panel web tienen que volver a ingresar. Úselo
  solo cuando reiniciar un servicio no basta.]

El botón solo funciona con el servidor instalado como servicio de Windows; si
corre como consola, está deshabilitado.

== Licencia

Abra #menu("Sistema", "Licencia"). Todos los usuarios ven el estado de la
licencia; activarla, revalidarla o desactivarla exige el permiso _Licencia_.

#captura("web-licencia.png", pie: [Sistema › Licencia durante el período de prueba.])

=== Qué muestra la página

Arriba aparece el aviso vigente, si lo hay. Las tarjetas muestran:

- *Estado*: el estado de la licencia (ver la tabla) y la modalidad: _En línea
  (heartbeat con gracia)_, _Sin conexión (archivo firmado)_ o _Prueba
  incorporada_;
- *Licencia*: el código de la licencia, el cliente y el paquete;
- *Vence* (o *Prueba hasta*): la fecha y los días que quedan, o _Perpetua_;
- *Validación en línea*: cuándo se revalidó por última vez, cuándo toca la
  próxima y hasta cuándo dura la gracia (_No aplica_ en una licencia sin
  conexión);
- *Este equipo*: el identificador de hardware del servidor, su nombre y la
  versión.

#table(
  columns: (auto, 1fr),
  table.header[Estado][Significado],
  [Activa], [La licencia es válida y está al día.],
  [Período de prueba], [El sistema funciona sin licencia durante los primeros 30 días, con todos los módulos y cupos reducidos.],
  [Período de gracia], [La licencia es válida, pero no se ha podido revalidar en línea. El sistema funciona normalmente hasta que termine la gracia.],
  [Restringida], [La licencia venció, fue invalidada, terminó la gracia o el archivo no corresponde a este equipo: el sistema está en modo restringido.],
  [Sin licencia], [No hay licencia y el período de prueba terminó: el sistema está en modo restringido.],
)

Debajo está la tabla *Módulos y cupos*: cada módulo y cupo, si está incluido
(_Base_, _Incluido_ o _No incluido_) y una barra con cuánto se usa del cupo,
que cambia de color al acercarse al tope. Los cupos cuentan solo los elementos
*habilitados*: al llegar al tope, el sistema no deja habilitar más, y los
canales de un equipo nuevo que no caben entran deshabilitados. Esos canales se
habilitan solos cuando vuelve a haber cupo.

#table(
  columns: (auto, auto),
  table.header[Módulo o cupo][Cuenta],
  [Video en vivo], [canales de video],
  [Reproducción remota], [—],
  [Reconocimiento de patentes], [fuentes ANPR],
  [Paneles de alarma], [paneles],
  [Control de acceso], [puertas],
  [Muro de video], [muros],
  [Decodificadores de muro], [decodificadores],
  [Parlantes IP], [parlantes],
  [Citofonía], [frentes],
  [Automatizaciones], [reglas],
  [Usuarios del sistema], [usuarios],
  [Clientes de escritorio simultáneos], [clientes],
)

Si se compraron expansiones (más canales, paneles, puertas, etc.), aparecen en
*Expansiones incluidas*, con su código, el pack, lo que aportan y su
vencimiento. Se suman solas en la siguiente revalidación en línea o al
importar el archivo `.lic` actualizado.

=== Activar en línea

#requiere("Licencia")

Requiere que el servidor tenga acceso a internet y el servidor de licencias
configurado (la tarjeta lo indica).

+ En *Activación en línea*, escriba el código del certificado de licencia
  (`XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`) en *Código de activación*.
+ Haga clic en #boton("Activar en línea").

Si el servidor no tiene configurado el servidor de licencias, el botón está
deshabilitado y la tarjeta lo dice: use la activación sin conexión.

=== Activar sin conexión

#requiere("Licencia")

Para servidores sin acceso a internet. La activación se hace en dos pasos con
dos archivos: una *solicitud* `.req` que sale del servidor y una *licencia*
`.lic` que entrega soporte.

+ En *Activación en línea*, escriba el código de activación (si lo tiene) y haga
  clic en #boton("Generar solicitud (.req)"). Se descarga un archivo `.req` con
  el identificador de este equipo.
+ Envíe ese archivo a soporte de CLRobotics (`soporte@clrobotics.cl`).
+ Cuando reciba el archivo `.lic`, en *Activación sin conexión* elíjalo con el
  selector de archivos.
+ Haga clic en #boton("Importar archivo .lic").

El mismo botón sirve para cargar un `.lic` actualizado, por ejemplo con
expansiones nuevas.

#importante[El archivo `.lic` queda ligado al equipo que generó la solicitud.
  Importado en otro servidor, se rechaza con _El archivo de licencia está
  vinculado a otro equipo (…); este servidor es …_. Genere la solicitud desde el
  servidor donde va a quedar la licencia.]

=== Revalidar

#requiere("Licencia")

Una licencia en línea se revalida sola contra el servidor de licencias cada
cierto tiempo. #boton("Revalidar ahora"), en *Mantenimiento*, lo hace en el
momento: trae las expansiones nuevas y renueva la gracia. Solo se habilita con
una licencia en línea.

=== Qué pasa al vencer o al perder contacto

- *Licencia en línea sin contacto*: si el servidor no logra revalidarla a
  tiempo, pasa a _Período de gracia_ y avisa la fecha en que quedará
  restringido. Al recuperar la conexión, o al importar un `.lic` actualizado,
  vuelve a _Activa_. Si la gracia termina sin revalidar, pasa a _Restringida_.
- *Vencimiento*: desde 30 días antes, el sistema avisa la fecha de vencimiento.
  Al vencer, pasa a _Restringida_.
- *Fin del período de prueba*: desde el inicio de la prueba se avisa cuándo
  termina; al terminar, el sistema queda _Sin licencia_.
- *Reloj del servidor atrasado*: si la hora del servidor retrocede más de un
  día, el sistema queda restringido hasta corregirla.

Los avisos aparecen como una franja bajo la barra superior en todas las
páginas, con el enlace _Ver licencia_.

En *modo restringido* se puede ingresar, consultar la información, administrar
la licencia, controlar los servicios y liberar puestos de cliente, pero no
guardar cambios, dar órdenes a los equipos ni abrir video: esas acciones
responden con el motivo de la restricción.

#nota[El modo restringido no borra nada: al activar o renovar la licencia,
  todo vuelve a funcionar como estaba.]

=== Mover la licencia a otro servidor

#requiere("Licencia")

+ En el servidor actual, en *Mantenimiento*, haga clic en
  #boton("Desactivar en este equipo") y confirme. El sistema vuelve al período
  de prueba, si queda, o queda sin licencia.
+ Revise el mensaje: si dice _Cupo liberado en el servidor de licencias._, la
  licencia quedó libre. Si dice que no se pudo liberar, pida a soporte que
  libere el cupo de ese equipo.
+ En el servidor nuevo, active la licencia en línea o sin conexión, como se
  explicó antes.

=== Datos para soporte

En el menú del usuario (arriba a la derecha), _Acerca de_ muestra la versión
del servidor, el estado de la licencia, su código, el identificador de hardware
y los datos del equipo. #boton("Copiar datos") copia un resumen (versión,
estado de la licencia, identificador de hardware y servidor) para pegarlo en un
correo a soporte, y #boton("Ver licencia") lleva a esta página.

=== Mensajes frecuentes

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [_Este servidor no tiene configurado el servidor de licencias: use la activación sin conexión (solicitud .req + archivo .lic)._],
  [Active sin conexión, o pida a soporte que configure la activación en línea.],
  [_Sin conexión con el servidor de licencias: …_ o _El servidor de licencias no respondió a tiempo._],
  [Revise el acceso a internet del servidor y vuelva a intentarlo, o active sin conexión.],
  [_El archivo de licencia está vinculado a otro equipo …_],
  [Genere la solicitud `.req` desde el servidor correcto y pida un `.lic` nuevo.],
  [_La firma del archivo de licencia no es válida (archivo alterado o de otro producto)._],
  [El archivo se modificó o no es de este producto. Pida a soporte que lo envíe de nuevo.],
  [_La licencia … venció el …_],
  [Renueve la licencia con soporte e importe el `.lic` nuevo.],
  [_El servidor de licencias invalidó la licencia (…)_],
  [La licencia fue revocada o suspendida. Contacte a soporte.],
  [_El reloj del servidor retrocedió (…). Corrija la hora del sistema._],
  [Ponga en hora el servidor; el sistema se normaliza solo.],
  [_La licencia permite N … y ya hay N en uso. Amplíe la licencia para agregar más._],
  [Se llegó al tope de un cupo. Deshabilite algo que no se use o amplíe la licencia.],
  [_El módulo «…» no está incluido en la licencia. Amplíe la licencia para habilitarlo._],
  [Ese módulo no está en su licencia. Contacte a soporte para agregarlo.],
)
