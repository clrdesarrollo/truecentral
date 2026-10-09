#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/workflows.js, src/TrueCentralVms.Server/wwwroot/workflow-editor.js, src/TrueCentralVms.Server/Api/WorkflowsApi.cs, src/TrueCentralVms.Server/Services/Workflows/WorkflowEngine.cs, src/TrueCentralVms.Server/Services/Workflows/WorkflowGraph.cs, src/TrueCentralVms.Server/Services/Workflows/Actions/

= Automatizaciones <cap-automatizaciones>

Una automatización es una regla que el servidor cumple por su cuenta: cuando
pasa algo (una alarma de panel, una cámara que se cae, una patente leída, un
acceso, una analítica de video, una hora del día o la llamada de otro sistema),
revisa las condiciones que usted definió y ejecuta acciones: tomar fotos,
enviar un correo, avisar a los operadores, hacer sonar un parlante, abrir una
puerta, armar un panel o mover una cámara PTZ. Cada automatización se dibuja
como un diagrama de flujo en un editor visual y funciona aunque nadie tenga
abierto el panel web ni el cliente de monitoreo.

#captura("web-automatizaciones-listado.png", pie: [Automatizaciones: listado, alertas y últimas ejecuciones.])

== La página Automatizaciones

#requiere("Ver automatizaciones")

Abra #menu("Automatizaciones") en el menú lateral. La página tiene tres
partes: el listado de automatizaciones, las *Alertas* emitidas y las *Últimas
ejecuciones*. Las dos últimas se actualizan solas cada 10 segundos.

#table(
  columns: (auto, 1fr),
  table.header[Columna][Qué muestra],
  [*Nombre*], [El nombre y, debajo, la descripción.],
  [*Cuándo*], [El disparador: qué la pone en marcha.],
  [*Filtro*], [Un resumen del filtro del disparador, o _Cualquier evento_ si no tiene.],
  [*Qué hace*], [Las acciones del diagrama. Las desactivadas se ven atenuadas.],
  [*Pasos*], [Cuántos pasos tiene el diagrama, sin contar el punto de partida.],
  [*Espera*], [El mínimo entre ejecuciones, o «—» si no tiene.],
  [*Ejecuciones*], [Cuántas veces se ha ejecutado.],
  [*Última*], [Fecha y hora de la última ejecución.],
  [*Estado*], [_Activa_ o _Pausada_.],
)

Para encontrar una automatización, escriba en el buscador una o más palabras:
quedan a la vista las filas que contienen todas, en cualquier columna (por
ejemplo, «pausada» o el nombre de una acción). El texto buscado se conserva al
volver desde el editor.

Con el permiso _Editar automatizaciones_, cada fila tiene los botones
#boton("Probar"), #boton("Abrir"), #boton("Duplicar") y #boton("Eliminar"), y
arriba aparecen #boton("Servidor de correo"), #boton("Sonidos") y
#boton("Nueva automatización").

#nota[Mientras no haya un servidor de correo configurado, la página lo avisa
  arriba del listado: las acciones de correo fallarán hasta que se configure
  (vea «Configurar el servidor de correo», al final de este capítulo).]

#nota[Quien tiene solo el permiso _Ver automatizaciones_ ve el listado, las
  alertas y el historial, pero no puede abrir el editor: el botón #boton("Ver")
  lleva a un aviso de que le falta el permiso _Editar automatizaciones_.]

== Alertas y acuse de recibo

#requiere("Atender alertas")

Cada aviso de la acción _Avisar a los operadores_ queda como alerta. La
sección *Alertas* de la página muestra las 25 más recientes y responde quién
se dio por enterado y cuándo. Si hay alertas sin confirmar, un recuadro arriba
indica cuántas.

#table(
  columns: (auto, 1fr),
  table.header[Columna][Qué muestra],
  [*Emitida*], [Fecha y hora de la alerta.],
  [*Alerta*], [Título, mensaje y a quién iba dirigida (o «todos los operadores»).],
  [*Qué la disparó*], [El resumen del evento.],
  [*Estado*], [_Informativa_ (no exige confirmación), _Confirmada_ o _PENDIENTE_.],
  [*Confirmó*], [Quién, cuándo, a los cuántos segundos y desde dónde (cliente de escritorio o panel web).],
)

#boton("Foto") muestra la imagen de la alerta y #boton("Enterado") la confirma a
su nombre. Vale la primera confirmación: si otro usuario ya la había
confirmado, el sistema lo indica y el registro no cambia.

Quien tiene el permiso _Atender alertas_ ve las alertas dirigidas a él o a
todos; el administrador las ve todas. Los operadores atienden estas alertas
en el Centro de eventos; vea #capitulo(<cap-centro-de-eventos>).

== Historial de ejecuciones

#requiere("Ver automatizaciones")

La sección *Últimas ejecuciones* lista las 25 más recientes.

#table(
  columns: (auto, 1fr),
  table.header[Columna][Qué muestra],
  [*Fecha*], [Cuándo empezó.],
  [*Automatización*], [El nombre.],
  [*Qué la disparó*], [El resumen del evento.],
  [*Pasos*], [Cuántos pasos se ejecutaron; la etiqueta _con fotos_ indica que hubo capturas.],
  [*Resultado*], [_OK_ o _Con errores_.],
  [*Origen*], [«automático» si la disparó un evento, o el usuario que la probó.],
)

Haga clic en una fila para abrir el detalle: la fecha, el disparo, el origen,
el resultado, la tabla de pasos (cada uno con su resultado, detalle y
duración) y las fotos capturadas. Si ninguna rama llegó a una acción porque
las condiciones no se cumplieron, el detalle lo dice: «Ninguna acción
correspondía ejecutar con este evento.».

#captura("web-automatizaciones-ejecucion.png", ancho: 85%, pie: [Detalle de una ejecución, con sus pasos y las fotos capturadas.])

== Crear una automatización

#requiere("Editar automatizaciones")

+ En #menu("Automatizaciones"), haga clic en #boton("Nueva automatización").
  Se abre el editor con un solo paso: el punto de partida.
+ Escriba el nombre en *Nombre de la automatización* y, si quiere, una
  *Descripción (opcional)*.
+ Haga clic en el punto de partida y, en el panel de la derecha, elija en
  *¿Cuándo se ejecuta?* qué la dispara (vea «Disparadores»).
+ Marque el filtro del disparador. Sin nada marcado, cualquier evento de ese
  tipo la ejecuta.
+ Agregue los pasos desde la paleta *Pasos* de la izquierda: condiciones,
  esperas y acciones.
+ Haga clic en cada acción y complete su configuración en el panel de la
  derecha.
+ Revise *Mínimo entre ejecuciones* y deje marcada la casilla *Activa*.
+ Haga clic en #boton("Guardar"). El sistema revisa el diagrama, lo guarda y
  vuelve al listado con el aviso «Automatización creada.».

#nota[El editor ocupa toda la ventana y oculta el menú lateral. Para verlo, use
  el botón de menú *Mostrar u ocultar el menú* de la barra superior.]

#importante[#boton("← Volver") regresa al listado sin guardar: lo que no haya
  guardado se pierde.]

#consejo[Para armar una automatización parecida a otra (la misma lógica para
  otra zona u otra cámara), duplíquela y cambie solo lo necesario.]

== El editor de diagramas

El editor tiene tres zonas: la paleta *Pasos* a la izquierda, el lienzo con el
diagrama al centro y, a la derecha, el panel de propiedades del paso o la
flecha seleccionada. Sin nada seleccionado, ese panel muestra una ayuda breve
(_Cómo armar el diagrama_).

#captura("web-automatizaciones-editor.png", pie: [Editor de automatizaciones con un paso seleccionado.])

=== La barra superior

#table(
  columns: (auto, 1fr),
  table.header[Control][Para qué sirve],
  [#boton("← Volver")], [Vuelve al listado sin guardar.],
  [*Nombre de la automatización*], [Obligatorio, hasta 128 caracteres y distinto de los demás.],
  [*Descripción (opcional)*], [Texto libre de hasta 512 caracteres; aparece bajo el nombre en el listado.],
  [*Mínimo entre ejecuciones*],
  [Segundos que deben pasar entre dos ejecuciones (0 a 86 400; por omisión,
    60). Así un sensor que rebota no la ejecuta veinte veces seguidas. Cuenta
    desde el inicio de la última ejecución real.],
  [*Activa*], [Desmarcada, la automatización queda pausada y no se ejecuta sola.],
  [#boton("Ordenar")], [Acomoda los pasos en filas a partir del punto de partida.],
  [#boton("Ajustar")], [Encuadra el diagrama completo en la ventana.],
  [#boton("Probar")], [Guarda y ejecuta la automatización de verdad con un evento de ejemplo.],
  [#boton("Guardar")], [Revisa el diagrama, lo guarda y vuelve al listado.],
)

=== Agregar pasos

Hay tres formas de agregar un paso:

- Haga clic en un elemento de la paleta: se agrega debajo del paso
  seleccionado (o del último) y queda conectado desde su primera salida libre.
- Arrastre un elemento de la paleta hasta el lienzo: queda donde lo suelte,
  sin conectar.
- Haga clic en el círculo de salida de un paso: se abre el menú _Agregar
  después_, y el paso que elija queda conectado a esa salida.

#table(
  columns: (auto, 1fr),
  table.header[Grupo de la paleta][Pasos],
  [Flujo], [Condición (sí / no), Espera, Fin],
  [Capturar], [Capturar foto],
  [Avisar], [Enviar correo, Sonar parlante IP, Avisar a los operadores],
  [Integración], [Subir a FTP, Llamar a un servicio (HTTP)],
  [Equipos], [Orden a una puerta, Armar / desarmar panel, Mover cámara PTZ a preset],
)

=== Conectar y eliminar

Para conectar dos pasos, arrastre desde el círculo de salida de uno hasta el
otro. Cada clase de paso tiene sus salidas:

#table(
  columns: (auto, 1fr),
  table.header[Paso][Salidas],
  [Punto de partida], [_Siguiente_. Nada puede conectarse hacia él.],
  [Condición (sí / no)], [_Sí_ y _No_.],
  [Espera], [_Siguiente_.],
  [Acción], [_Siguiente_ si terminó bien y _Si falla_ si falló.],
  [Fin], [Ninguna: termina la rama.],
)

De una misma salida pueden partir varias flechas: esas ramas se ejecutan una
tras otra, no al mismo tiempo.

Para eliminar un paso o una flecha, selecciónelo con un clic y presione
#tecla("Supr"). Con una flecha seleccionada también puede usar
#boton("Eliminar conexión") en el panel de la derecha. El punto de partida no
se puede eliminar.

#importante[Todos los pasos deben quedar conectados al punto de partida. Si
  queda uno suelto, al guardar aparece «El paso «…» no está conectado al flujo:
  conéctelo o elimínelo.» y el paso queda seleccionado.]

=== Moverse por el diagrama

#table(
  columns: (auto, 1fr),
  table.header[Gesto][Acción],
  [Rueda del mouse], [Acerca o aleja, centrado en el puntero.],
  [Arrastrar el fondo], [Desplaza el diagrama.],
  [Arrastrar un paso], [Lo cambia de lugar.],
  [Doble clic en el fondo], [Acerca.],
  [#boton("+") #boton("−")], [Acercan y alejan (esquina del lienzo).],
  [#tecla("Esc")], [Quita la selección y cierra el menú _Agregar después_.],
)

== Disparadores

El punto de partida define qué ejecuta la automatización. Al hacer clic en él,
el panel de la derecha muestra la lista *¿Cuándo se ejecuta?*, el *Filtro del
disparador* (nada marcado = cualquiera) y las *Marcas disponibles en los
textos*.

#table(
  columns: (auto, auto, 1fr),
  table.header[Grupo][Disparador][Se ejecuta cuando…],
  [Paneles de alarma], [Evento de panel de alarma],
  [un panel informa una alarma, un sensor interrumpido, un armado, una anulación o una falla.],
  [Paneles de alarma], [Conexión con un panel],
  [el servidor pierde o recupera la conexión con un panel de alarma.],
  [Video], [Evento de cámara (analítica)],
  [una cámara o grabador informa movimiento, cruce de línea, intrusión, pérdida de video, entrada de alarma, etc.],
  [Video], [Lectura de patente], [una cámara de reconocimiento de patentes lee una patente.],
  [Control de acceso], [Evento de control de acceso],
  [alguien pasa (o lo rechazan) por una puerta, o la puerta informa forzada, mantenida abierta o sabotaje.],
  [Sistema], [Conexión de un equipo],
  [una cámara o grabador, un terminal de acceso o un parlante IP pierde o recupera la conexión.],
  [Sistema], [Horario programado], [llegan las horas indicadas, los días de la semana marcados.],
  [Sistema], [Llamada externa (HTTP)], [otro sistema llama a la dirección de esta automatización.],
)

#importante[Al cambiar el disparador se borran el filtro y las condiciones de
  todos los pasos _Condición_, porque sus campos dependen del disparador.]

=== Elegir los equipos que disparan

Casi todos los disparadores tienen un campo para elegir equipos: muestra lo
elegido como fichas y un botón que abre un árbol (por ejemplo,
#boton("Elegir cámaras…") o #boton("Elegir paneles y zonas…")).

+ Haga clic en el botón del campo.
+ Busque por nombre en *Buscar por nombre…* o use #boton("Desplegar todo").
+ Marque un equipo completo o solo algunos de sus elementos (zonas, áreas,
  cámaras o puertas). Abajo se resume lo marcado.
+ Haga clic en #boton("Aceptar").

La × de cada ficha la quita del filtro, y #boton("Desmarcar todo") vacía la
selección. Sin nada marcado, dispara cualquier equipo.

=== Evento de panel de alarma

- *Paneles, zonas y áreas*: un panel completo o solo algunas de sus zonas o
  áreas. Marcar una zona ya acota el panel.
- *Tipo de evento*: Alarma, Sensor interrumpido, Sensor restablecido,
  Restauración, Armado, Desarmado, Anulación, Falla, Sistema o Información.
- *Severidad*: Crítica, Advertencia o Informativa. No afecta a «Sensor
  interrumpido» ni a «Sensor restablecido», que no tienen severidad propia.
- *Códigos del evento (coma)*: por ejemplo, `1130, 1120`.
- *La descripción contiene*: un texto que debe aparecer en la descripción del
  evento.
- *Cómo se supo*: Informado por el panel, Detectado por sondeo u Orden desde el
  VMS.
- *Solo si la condición se sostiene (segundos)*: con 0 se ejecuta de
  inmediato. Con un valor mayor (hasta 600), el servidor vigila la zona ese
  tiempo y ejecuta solo si el sensor sigue interrumpido; así tolera los pulsos
  cortos del detector.
- *Estado del área*: con *Solo si el área del sensor está armada*, se lee el
  estado actual del panel. El retardo de salida todavía no cuenta como armada,
  y si el panel no informa su estado, no se bloquea.

=== Conexión con un panel

- *Paneles*: los que disparan.
- *Estado de conexión que dispara*: Sin conexión, Credenciales rechazadas o En
  línea.

=== Evento de cámara (analítica)

- *Tipo de evento*: Detección de movimiento, Cruce de línea, Intrusión,
  Entrada a región, Salida de región, Merodeo, Objeto abandonado / retirado,
  Estacionamiento indebido, Movimiento rápido, Aglomeración, Detección de
  rostro, Conteo de personas, Pérdida de video, Cámara tapada / sabotaje,
  Anomalía de video, Anomalía de audio, Entrada de alarma (contacto), Falla del
  equipo u Otro.
- *Cámaras*: un grabador completo o solo algunas de sus cámaras. Los equipos
  que no pueden enviar eventos aparecen en el árbol con la nota «el driver no
  recibe eventos».
- *Líneas / reglas de la analítica*: para los tipos que usan líneas o regiones
  (cruce de línea, intrusión, entrada y salida de región, merodeo, objeto
  abandonado, estacionamiento, movimiento rápido y aglomeración). Se ofrecen
  las que admite la cámara, numeradas como en su configuración; las atenuadas,
  marcadas «(sin dibujar)», existen pero no están dibujadas. Si el equipo no
  informa cuántas admite, se ofrece una numeración genérica (Línea 1, Línea
  2…). Nada marcado = cualquiera.
- *Cuándo se ejecuta*: _Al cruzar cualquiera de las líneas marcadas_ o _Solo
  cuando se crucen TODAS las líneas marcadas_. Con la segunda, en la misma
  cámara deben cruzarse todas las líneas marcadas *Dentro de (segundos)* (30
  por omisión) y, si marca *En orden (1 → 2 → …)*, en ese orden. Sirve, por
  ejemplo, para un vehículo que pasa la línea 1 y luego la 2. Exige marcar al
  menos dos líneas.

#importante[Hoy solo los equipos Hikvision (_Hikvision (SDK nativo)_) envían
  eventos de cámara. Las reglas (movimiento, cruce de línea…) se configuran en
  la web del propio equipo; el sistema solo las escucha, y solo de los equipos
  que pide alguna automatización. En un grabador, la regla debe tener marcada
  la acción de enlace «Notificar al centro de vigilancia»; sin ella, el
  grabador no envía el evento.]

#nota[Si el equipo envía el evento sin foto y la automatización no tiene una
  acción _Capturar foto_, el sistema toma una foto del canal al llegar el
  evento; en el historial aparece como el paso «Foto del evento».]

=== Lectura de patente

- *Cámaras ANPR*: solo los equipos que pueden leer patentes. Los que no están
  activados como fuente de patentes aparecen con la nota «no está marcada como
  fuente de patentes».
- *Patentes*: _Cualquier patente_, _Solo las de la lista (lista blanca)_ o
  _Todas menos las de la lista (lista negra)_. Escriba las patentes una por
  línea o separadas por coma. `*` reemplaza cualquier cosa y `?` un carácter;
  no se distinguen mayúsculas ni guiones.
- *Confianza mínima de la lectura (0–100)*: vacío = sin mínimo.

#modulo("anpr")[Las fuentes de patentes se activan como se explica en
  #capitulo(<cap-reconocimiento-de-patentes>).]

=== Evento de control de acceso

- *Resultado*: Acceso concedido, Acceso denegado, Alarma de puerta (forzada,
  mantenida abierta, sabotaje), Puerta abierta, Puerta cerrada u Otro.
- *Credencial usada*: Tarjeta, Huella, Rostro, Clave, Código QR, Patente, Orden
  remota, Botón de salida o Desconocida.
- *Terminales y puertas*: un terminal completo o solo algunas de sus puertas.
- *Identificadores de persona (coma)*: por ejemplo, `1001, 1002`.
- *La descripción contiene*: un texto de la descripción del evento.

=== Conexión de un equipo

- *Clase de equipo*: Cámaras y grabadores, Terminales de acceso o Parlantes IP.
- *Estado que dispara*: Sin conexión, Credenciales rechazadas o En línea
  (recuperado).
- *Equipos*: con equipos marcados, solo esos disparan; sin ninguno, cualquiera
  de la clase elegida.

=== Horario programado

- *Horas en que se ejecuta*: en formato hh:mm, separadas por coma; por
  ejemplo, `07:30, 22:00`. Es la hora local del servidor.
- *Días de la semana*: los días en que rige.

Debe indicar al menos una hora; si no, al guardar aparece «Indique al menos una
hora en que debe ejecutarse.».

=== Llamada externa (HTTP)

Otro sistema (una central de monitoreo, un botón de pánico, un script) ejecuta
la automatización llamando a una dirección propia de ella con el método POST.

- *URL que debe llamar el otro sistema (POST)*: la dirección completa. En una
  automatización nueva, la clave se genera al guardar. #boton("Copiar") la
  copia al portapapeles.
- #boton("Nueva clave") genera una clave distinta; la anterior deja de servir
  al guardar.

Si la llamada trae un cuerpo JSON (hasta 64 kB), cada campo queda disponible
como marca: un campo `mensaje` se usa como `{mensaje}`. También están
`{cuerpo}` (el JSON completo) e `{ip}` (la dirección que llamó). Si la clave no
existe o la automatización está pausada, el servidor responde con el código
404; si el cuerpo no es JSON válido, lo rechaza con «El cuerpo de la llamada no
es JSON válido.».

#importante[La clave de la dirección es la credencial: quien la conozca puede
  ejecutar la automatización. No la comparta más de lo necesario y, si se
  filtró, genere una nueva. Al duplicar una automatización de este tipo, la
  copia recibe su propia clave.]

=== Franjas horarias y ejecuciones simultáneas

Todos los disparadores, salvo _Horario programado_, admiten franjas horarias
en el filtro. Cada franja tiene *Solo estos días de la semana*, *Desde (hora
local)* y *Hasta (si es menor, cruza la medianoche)*. Con
#boton("+ Agregar franja horaria") se suman más, y #boton("Quitar") borra una
(la primera no se quita).

- La automatización rige si se cumple cualquiera de las franjas.
- Las horas van en formato de 24 horas. Puede escribirlas abreviadas: «6» queda
  como 06:00 y «1830» como 18:30.
- Una franja sin horas, o de 00:00 a 24:00, vale todo el día.
- Una franja que cruza la medianoche pertenece al día en que empieza. Ejemplo:
  de lunes a viernes de 18:00 a 06:00 en la primera franja, y sábado y domingo
  sin horas en la segunda. Para que la noche del domingo siga hasta el lunes a
  las 06:00, marque también el domingo en la primera.

La casilla *No volver a ejecutar mientras la anterior siga en curso* descarta
los eventos que llegan mientras la automatización todavía corre (por ejemplo,
durante una espera con la sirena sonando). Complementa al mínimo entre
ejecuciones.

== Condiciones, esperas y fin

=== Condición (sí / no)

Una condición se evalúa sobre el mismo evento que disparó la automatización y
muestra casi los mismos campos que el filtro del disparador (equipos, tipos de
evento, franjas horarias…). Si se cumple todo lo
marcado, el flujo sigue por *Sí*; si no, por *No*. Una condición sin nada
marcado siempre sigue por *Sí*. Use el *Rótulo (opcional)* para que el
diagrama se lea solo, por ejemplo «¿Es de noche?».

Con el disparador _Evento de panel de alarma_, la condición tiene además
*¿Sigue interrumpido durante… (segundos)?*: el paso espera ese tiempo mirando
el sensor que disparó y sigue por *Sí* si continuó interrumpido o por *No* si
se restableció. Sirve para escalar una respuesta.

=== Espera

Detiene la rama los *Segundos* indicados (1 a 3600; 10 por omisión) antes de
seguir. Con el disparador _Evento de panel de alarma_ aparece *Terminar antes
si se desarma el área*: la espera mira el panel cada segundo y, al desarmarse
el área del sensor, sigue de inmediato con el paso siguiente.

=== Fin

Termina la rama. Es opcional: una rama sin salida también termina.

=== Ejemplo: advertencia y luego sirena

+ Disparador _Evento de panel de alarma_, tipo «Sensor interrumpido», la zona
  del perímetro y una franja de 20:00 a 06:00.
+ Acción _Sonar parlante IP_ con un texto leído en voz alta de advertencia.
+ Condición con *¿Sigue interrumpido durante… (segundos)?* en 30.
+ Por *Sí*: _Sonar parlante IP_ con una sirena en bucle (*Repeticiones* en 0)
  y _Avisar a los operadores_. Por *No*: _Fin_.

Una segunda automatización con el tipo «Sensor restablecido» y la acción
_Sonar parlante IP_ en *Orden* «Detener lo que esté sonando» apaga la sirena
cuando el sensor vuelve a la normalidad.

== Acciones

Al seleccionar una acción, el panel de la derecha muestra qué hace, un
*Rótulo (opcional)*, sus campos y una sección *Ejecución* con:

- *Esperar antes de ejecutar (s)*: de 0 a 600 segundos.
- *Acción activa (desactivada = se salta y sigue)*: una acción desactivada no
  se ejecuta, y el flujo continúa como si hubiera terminado bien.

Si la acción falla, el flujo sigue por la salida *Si falla* (si está
conectada); si termina bien, por *Siguiente*. Una acción fallida deja la
ejecución «Con errores» aunque la rama _Si falla_ continúe. Cada acción tiene
un tiempo máximo (120 segundos por omisión); si lo supera, se da por fallida.

Los campos de contraseña muestran «(guardada; vacío = no cambiar)» cuando ya
hay una guardada: déjelos vacíos para conservarla.

=== Capturar foto

Toma fotos de las cámaras elegidas. Las fotos quedan disponibles para las
acciones siguientes (correo, FTP, aviso a los operadores) y en el historial.

- *Cámaras a capturar*: marque una o varias. El filtro *Filtrar cámaras por
  nombre…* esconde las que no coinciden (las marcadas siempre se ven). Al dejar
  el mouse un momento sobre un nombre aparece una vista previa del canal. Las
  cámaras cuyo equipo no captura imágenes aparecen deshabilitadas.
- Con dos o más cámaras aparece el *Orden de captura*, que es también el orden
  de las fotos en el aviso; ajústelo con las flechas ▲ y ▼.
- *Fotos por cámara*: de 1 a 5.
- *Segundos entre fotos*: de 0 a 30.

=== Enviar correo

Necesita el servidor de correo configurado (vea «Configurar el servidor de
correo»).

- *Para*: una o varias direcciones separadas por coma.
- *Copia (opcional)*.
- *Asunto*: por omisión, `{tipo}: {evento}`.
- *Mensaje*: trae un texto de ejemplo con el tipo, el evento, el equipo, la
  fecha y hora y el nombre de la automatización.
- *Adjuntar las fotos capturadas antes en esta ejecución*.

=== Avisar a los operadores

Muestra un aviso en pantalla a los operadores conectados, con foto, video en
vivo y alarma sonora. Cada aviso queda registrado como alerta (vea «Alertas y
acuse de recibo»).

- *Título* y *Mensaje*: por omisión, `{tipo}: {equipo}` y
  `{evento} · {fechahora}`. Además de las marcas del disparador, admiten
  `{ubicacion}` (dónde está el recurso, según Recursos) y `{consignas}` (lo que
  debe hacer el operador con ese recurso).
- *Importancia*: Crítica, Advertencia o Informativa.
- *Destinatarios*: los usuarios que reciben el aviso. Sin ninguno marcado,
  llega a todos los operadores conectados que atienden alertas.
- *Mostrar la foto capturada en el aviso*.
- *Exigir que un operador se dé por enterado*: el aviso queda en pantalla y en
  la lista de alertas hasta que alguien lo confirme; se registra quién y cuándo.
- *Cámaras del video en vivo de la ventana de alarma*: si no marca ninguna, se
  usan las que capturaron foto en la misma ejecución y, si tampoco hubo fotos,
  las cámaras asociadas al recurso que la disparó (su ficha en Recursos).
- *Alarma sonora en el equipo del operador*: _(sin sonido)_, _Pitido del
  sistema del equipo_ o uno de los sonidos de la biblioteca. El botón ▶ lo hace
  sonar en su navegador.
- *Repeticiones del sonido*: de 0 a 5. Con 0, suena sin parar hasta que un
  operador confirme la alerta, la silencie o cierre la ventana.

=== Sonar parlante IP

Reproduce un sonido, un audio guardado en el parlante o un texto leído en voz
alta. En *Tipo de parlante*, lo normal es _Parlantes del inventario (módulo
Parlantes IP)_:

- *Parlantes*: marque uno o varios. Si elige varios, los sonidos del servidor
  suenan sincronizados.
- *O bien todos los parlantes del grupo*: el nombre de un grupo tal como está
  en el mantenedor de parlantes; se suma a los marcados.
- *Orden*: _Reproducir_ o _Detener lo que esté sonando_. «Detener» es la pareja
  de un sonido en bucle.
- *Qué reproducir*: _Sonido del servidor (Sonidos)_, _Audio de la biblioteca del
  parlante_ (por su *Nombre del audio en el parlante*) o _Texto leído en voz
  alta (TTS del parlante)_.
- Para el texto: *Texto a leer (máximo 100 caracteres)*, que admite marcas como
  `{zona}` o `{panel}`; *Idioma de la voz* (Español, Inglés, Portugués (Brasil)
  o Francés) y *Voz* (Femenina o Masculina).
- *Repeticiones*: de 0 a 5. Con 0, suena en bucle hasta que una acción
  «Detener» o el operador lo corte; el bucle solo existe con un sonido del
  servidor.
- *Fijar el volumen del parlante antes de reproducir* y *Volumen (%)*: el
  volumen queda fijado en el equipo, no vuelve solo al valor anterior.

Los otros tipos sirven para equipos que no están en el inventario y se
configuran por su dirección: _Hikvision suelto (audio bidireccional ISAPI)_
(sonido del servidor y *Canal de audio*), _Axis (clip por VAPIX)_ (*Número de
clip en el parlante* y *Volumen (%)*) y _Otra marca (URL que gatilla el
mensaje)_ (*URL* y *Método*).

#modulo("speakers")[Los sonidos del servidor se cargan en la Biblioteca de
  sonidos (botón #boton("Sonidos") del listado), que se explica en
  #capitulo(<cap-parlantes-ip>).]

=== Orden a una puerta

- *Orden*: _Abrir (pulso: abre y se vuelve a cerrar)_, _Mantener abierta hasta
  nueva orden_, _Bloquear (no entra nadie, ni con credencial)_ o _Cerrar / volver
  a normal_.
- *Puertas*: una o varias puertas del control de acceso.

=== Armar / desarmar panel

- *Panel de alarma* y *Área* (_Todas las áreas_ o una en particular).
- *Orden*: _Armar_, _Desarmar_ o _Borrar / silenciar la alarma_.
- *Modo de armado* (al armar): _Total (fuera de casa)_ o _Parcial (en casa /
  perimetral)_.

=== Mover cámara PTZ a preset

- *Cámara PTZ*: solo se ofrecen las cámaras que tienen PTZ.
- *Preset (1–300)*: el preset debe estar guardado en la propia cámara, desde
  la vista en vivo del cliente o desde su web.

=== Subir a FTP

Sube las fotos de la ejecución y, si quiere, un informe de texto.

- *Servidor FTP* y *Puerto* (21 por omisión).
- *Seguridad*: _Sin cifrar (FTP)_, _FTPS explícito (AUTH TLS)_ o _FTPS
  implícito_.
- *Usuario* y *Contraseña*.
- *Carpeta remota*: por omisión `/alarmas/{fecha}`. Admite marcas y se crea sola
  si no existe.
- *Subir también un informe de texto con el evento*.
- *Aceptar certificado no verificable (FTPS con certificado propio)*.

=== Llamar a un servicio (HTTP)

Hace una petición a otro sistema con los datos del evento (una central de
monitoreo, un bot de mensajería, un módulo de relés).

- *Método*: POST, GET, PUT, PATCH o DELETE.
- *URL*: dirección completa, con `http://` o `https://`.
- *Tipo de contenido* (por omisión `application/json`) y *Cuerpo*: no
  aparecen con GET.
- *Cabeceras (una por línea: Nombre: valor)*.
- *Autenticación*: Ninguna, Básica o Digest, con *Usuario* y *Contraseña*.
- *Tiempo máximo de espera (s)*: de 1 a 120; 15 por omisión.
- *Aceptar certificado no verificable (HTTPS)*.

La URL, las cabeceras y el cuerpo admiten marcas; en la URL, los valores se
adaptan para no romperla.

== Marcas en los textos

Los textos de las acciones (asuntos, mensajes, cuerpos, carpetas, URL) admiten
marcas entre llaves que se reemplazan por los datos del evento. El panel del
punto de partida lista las disponibles para el disparador elegido; al dejar el
mouse sobre una se ve qué significa.

#table(
  columns: (auto, 1fr),
  table.header[Marca][Se reemplaza por],
  [`{workflow}`], [El nombre de la automatización.],
  [`{evento}`], [La descripción del evento.],
  [`{tipo}`], [La naturaleza del evento.],
  [`{severidad}`], [Crítica, Advertencia o Informativa.],
  [`{equipo}`], [El nombre del equipo (panel, cámara, terminal, parlante).],
  [`{origen}`], [Cómo se supo.],
  [`{fecha}`], [La fecha (dd-mm-aaaa).],
  [`{hora}`], [La hora (hh:mm:ss).],
  [`{fechahora}`], [La fecha y la hora.],
  [`{servidor}`], [El nombre del servidor.],
)

Cada disparador agrega las suyas:

#table(
  columns: (auto, 1fr),
  table.header[Disparador][Marcas propias],
  [Paneles de alarma (ambos)],
  [`{panel}`, `{codigo}`, `{area}`, `{areanumero}`, `{zona}`, `{zonanumero}`, `{operador}`, `{estado}`],
  [Evento de cámara], [`{camara}`, `{canal}`, `{regla}`, `{linea}`, `{entrada}`, `{horaequipo}`],
  [Lectura de patente],
  [`{patente}`, `{confianza}`, `{camara}`, `{tipovehiculo}`, `{colorvehiculo}`, `{marca}`,
    `{colorpatente}`, `{velocidad}`, `{carril}`, `{direccion}`, `{infraccion}`, `{horaequipo}`],
  [Evento de control de acceso],
  [`{puerta}`, `{puertanumero}`, `{persona}`, `{personaid}`, `{tarjeta}`, `{credencial}`, `{resultado}`],
  [Conexión de un equipo], [`{equipotipo}`, `{direccion}`, `{modelo}`, `{estado}`],
  [Llamada externa], [`{cuerpo}`, `{ip}` y un campo por cada campo del JSON recibido],
)

== Probar una automatización

#requiere("Editar automatizaciones")

La prueba ejecuta la automatización con un evento de ejemplo. Si el filtro
nombra equipos, el ejemplo usa uno real de ellos, para que los nombres del
correo o del aviso sean los de verdad.

#importante[La prueba ejecuta las acciones DE VERDAD: envía el correo, sube el
  archivo, abre la puerta y hace sonar el parlante. Avise antes a quien
  corresponda.]

Desde el listado:

+ Haga clic en #boton("Probar") en la fila de la automatización.
+ Confirme el aviso.
+ Al terminar aparece «Prueba ejecutada correctamente.» o «La prueba terminó
  con errores.» y se abre el detalle de la ejecución.

Desde el editor, #boton("Probar") primero guarda la automatización y luego
pide la misma confirmación. Al terminar, cada paso del diagrama muestra su
resultado (correcto o con error) con el detalle, para ver de un vistazo dónde
falló.

== Activar, pausar, duplicar y eliminar

#requiere("Editar automatizaciones")

- *Pausar o activar*: abra la automatización, marque o desmarque *Activa* y
  guarde. Una automatización pausada no se ejecuta sola, pero se puede probar.
- *Duplicar*: #boton("Duplicar") crea una copia completa (diagrama, filtro,
  acciones y contraseñas) con «(copia)» en el nombre. La copia nace _pausada_ y
  se abre en el editor: cambie lo necesario, márquela *Activa* y guarde.
- *Eliminar*: #boton("Eliminar") pide confirmación. El historial de
  ejecuciones se conserva.

#nota[La licencia fija cuántas automatizaciones pueden estar activas a la vez.
  Al superar el cupo aparece «La licencia permite N reglas y ya hay M en uso.
  Amplíe la licencia para agregar más.». Las pausadas no cuentan.]

== Configurar el servidor de correo

#requiere("Editar automatizaciones")

Las acciones _Enviar correo_ salen por un servidor de correo saliente (SMTP)
que se configura una sola vez.

+ En #menu("Automatizaciones"), haga clic en #boton("Servidor de correo").
+ Marque *Habilitado (las automatizaciones pueden enviar correos)*.
+ Complete *Servidor SMTP* y *Puerto*.
+ En *Seguridad*, elija _STARTTLS (normalmente puerto 587)_, _TLS implícito
  (normalmente puerto 465)_ o _Sin cifrado (servidor interno, puerto 25)_.
+ Escriba *Usuario* y *Contraseña*. Deje el usuario vacío si el servidor no
  pide autenticación.
+ Complete *Dirección del remitente* y, si quiere, *Nombre del remitente*.
+ Haga clic en #boton("Guardar").
+ Para comprobarlo, escriba una dirección en *Enviar un correo de prueba a* y
  haga clic en #boton("Probar"). Si llega, las automatizaciones ya pueden
  enviar correos.

#nota[La prueba usa la configuración ya guardada: guarde antes de probar. Si
  el servidor de correo es interno y usa un certificado propio, marque *Aceptar
  certificado no verificable (servidor de correo interno)*.]

== Mensajes frecuentes

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué hacer],
  [Ponga un nombre a la automatización.], [Escriba el nombre en la barra superior del editor.],
  [Agregue al menos una acción.], [El diagrama necesita al menos una acción conectada.],
  [El paso «…» no está conectado al flujo: conéctelo o elimínelo.],
  [Conecte el paso señalado o elimínelo.],
  [Ya existe una automatización con ese nombre.], [Use otro nombre.],
  [Indique al menos una hora en que debe ejecutarse.],
  [En _Horario programado_, complete *Horas en que se ejecuta*.],
  [«Enviar correo»: Indique al menos un destinatario del correo.],
  [Complete *Para* en la acción de correo.],
  [«Capturar foto»: Elija al menos una cámara para capturar.],
  [Marque una cámara en la acción.],
  [«Sonar parlante IP»: Elija al menos un parlante o un grupo.],
  [Marque un parlante o escriba el nombre de un grupo.],
  [El bucle hasta detener (0 repeticiones) solo está disponible con un sonido del servidor.],
  [Use un sonido del servidor o ponga 1 o más repeticiones.],
  [«Llamar a un servicio (HTTP)»: La URL debe ser absoluta y empezar con `http://` o `https://`.],
  [Corrija la URL de la acción.],
  [El módulo «Automatizaciones» no está incluido en la licencia.],
  [Amplíe la licencia (#menu("Sistema", "Licencia")).],
  [La licencia permite N reglas y ya hay M en uso.],
  [Pause otra automatización o amplíe la licencia.],
  [Primero guarde la configuración del servidor de correo.],
  [Guarde el servidor de correo antes de enviar la prueba.],
  [La acción superó el tiempo máximo (… s).],
  [El equipo o servicio no respondió a tiempo: revise que esté en línea y accesible desde el servidor.],
)
