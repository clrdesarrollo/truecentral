#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Client/Views/CercoView.xaml, src/TrueCentralVms.Client/ViewModels/CercoViewModel.cs, src/TrueCentralVms.Client/ViewModels/MainViewModel.cs, src/TrueCentralVms.Client/Services/CercoSiren.cs, src/TrueCentralVms.Client/Views/ToastWindow.xaml, src/TrueCentralVms.Server/wwwroot/cerco.js, src/TrueCentralVms.Server/wwwroot/cerco-alarm.js, src/TrueCentralVms.Server/Api/CercoApi.cs, src/TrueCentralVms.Server/Services/CercoMapper.cs

= Cerco eléctrico <cap-cerco-electrico>

El módulo Cerco eléctrico muestra en tiempo real el estado de los paneles de
cerco (energizadores con firmware de CLRobotics) y permite armarlos,
desarmarlos y silenciar su sirena. Cada panel se conecta por sí mismo al
servidor y avisa al instante cualquier cambio. Las alarmas de cerco suenan en
el puesto aunque el módulo esté cerrado.

#captura-pendiente("Cliente de monitoreo: Cerco eléctrico con tres tarjetas de panel, una en alarma, y la lista Eventos recientes", alto: 6cm)

== Abrir el módulo

#requiere("Ver cercos eléctricos")

En el cliente de monitoreo, haga clic en el botón *Cerco eléctrico* del riel o
en la tarjeta del mismo nombre en Inicio. Cada panel aparece como una tarjeta;
a la derecha está la lista *Eventos recientes*, con los últimos 50 eventos de
todos los paneles.

#nota[Cerrar la viñeta del módulo no apaga nada: las alarmas de cerco siguen
  sonando en este puesto.]

== Leer la tarjeta de un panel

#requiere("Ver cercos eléctricos")

La tarjeta muestra el nombre del panel, su conexión (*En línea*, *Sin conexión*
o *Pausado*) y su ubicación o, si no tiene, su ID de equipo. Debajo, unas
etiquetas resumen su estado:

#table(
  columns: (auto, 1fr),
  table.header[Etiqueta][Significado],
  [Armado / Desarmado], [Si el cerco está energizado. *Armando…* indica el
    retardo de salida antes de energizar.],
  [Sirena sonando], [La sirena del panel está activa.],
  [Cerco caído], [El panel detectó una caída o un corte del cerco.],
  [Zona N en alarma], [Una zona del panel está en alarma.],
  [Sin retorno], [El cerco está armado pero el panel no detecta el retorno
    del pulso.],
  [Nivel N/21], [La potencia configurada del energizador (de 7 a 21). El
    panel no mide kilovoltios.],
  [Señal buena / regular / débil · N dBm], [La señal WiFi del panel. Con menos
    de −75 dBm puede perder la conexión de vez en cuando: revise la antena o
    acerque el punto de acceso.],
)

Mientras hay algo que atender (sirena sonando, cerco caído o una zona en
alarma), la tarjeta tiene el borde rojo.

== Armar, desarmar y silenciar

#requiere("Operar cercos eléctricos")

Cada tarjeta tiene tres botones:

- #boton("Armar") energiza el cerco. Se habilita con el panel en línea y
  desarmado.
- #boton("Desarmar") lo desenergiza. También detiene un armado en curso.
- #boton("Silenciar") apaga la sirena del panel sin desarmarlo. Se habilita
  solo mientras la sirena suena.

La orden viaja al panel y la tarjeta cambia cuando el panel la cumple; el
resultado también queda en *Eventos recientes* (por ejemplo, «Cerco armado»).
Si la orden no se puede entregar, el motivo aparece bajo el título del módulo,
precedido del nombre del panel; por ejemplo, «El panel no está conectado.».
Armar, desarmar y silenciar quedan registrados en la bitácora de auditoría.

Si un panel queda fuera de su alcance por ubicación, su tarjeta lo dice:
«Fuera de su alcance: puede verlo, pero no operarlo.»

== Cuando suena una alarma

Los eventos *Caída/corte del cerco*, *Alarma en zona N*, *Pánico desde control
remoto*, *Sabotaje del gabinete* y *Falla de alto voltaje* disparan la alarma en
el cliente, esté donde esté:

- suena en el puesto una sirena de dos tonos, distinta de los demás sonidos del
  sistema, que se repite hasta que alguien la calle;
- aparece un aviso flotante «Alarma de cerco · nombre del panel» en la esquina
  inferior derecha de la ventana principal, que no se cierra solo;
- la barra de estado muestra «ALARMA DE CERCO:» seguido del panel y del evento;
- el botón *Cerco eléctrico* del riel se pone rojo y parpadea.

Para atenderla:

+ Haga clic en #boton("Enterado") en el aviso flotante. Esto calla la sirena
  de este puesto.
+ Abra el módulo Cerco eléctrico y revise la tarjeta del panel.
+ Si corresponde, haga clic en #boton("Silenciar") para apagar la sirena del
  panel, o en #boton("Desarmar").

La sirena del puesto también se calla cuando alguien silencia la sirena del
panel, desde este puesto o desde otro. Al desarmar el panel, el aviso flotante
se cierra.

Si el panel tiene una ficha con consignas o cámaras asociadas, se abre además
la ventana de verificación (#capitulo(<cap-recursos-y-ubicaciones>)).

== Eventos del cerco

#requiere("Ver cercos eléctricos")

Los eventos se muestran con un color según su gravedad: rojo para los
críticos, ámbar para las advertencias y gris para los informativos.

#table(
  columns: (auto, 1fr),
  table.header[Evento][Significado],
  [Caída/corte del cerco], [Crítico. El panel detectó una caída o un corte del
    cerco.],
  [Alarma en zona N], [Crítico. Una zona del panel entró en alarma.],
  [Pánico desde control remoto], [Crítico. Se pulsó un botón de control remoto
    programado como pánico.],
  [Sabotaje del gabinete], [Crítico. El panel informa sabotaje de su
    gabinete.],
  [Falla de alto voltaje], [Crítico. El panel informa una falla del
    energizador.],
  [Detección de arcos], [Advertencia.],
  [Sirena activada], [Advertencia.],
  [Armado rechazado: zona 0 abierta], [Advertencia. El panel no armó porque la
    zona 0 está abierta.],
  [Armado rechazado: cerco sin retorno], [Advertencia. El panel no armó porque
    no detecta el retorno del pulso.],
  [Corte de energía: operando con batería], [Advertencia.],
  [Cerco armado / Cerco desarmado], [Informativo. Entre paréntesis puede
    indicar el origen: (llave), (control RF) o (restaurado tras reinicio).],
  [Sirena silenciada], [Informativo. Incluye «Sirena silenciada desde control
    remoto».],
  [Zona N normal], [Informativo. La zona volvió a la normalidad.],
  [Energía restablecida], [Informativo.],
  [Reconexión con el servidor], [Informativo. Indica cuántos segundos estuvo
    sin conexión.],
  [Arranque del panel], [Informativo. El panel se encendió o se reinició.],
)

== Cerco eléctrico en el panel web

#requiere("Ver cercos eléctricos")

En el panel web, abra #menu("Aplicaciones", "Cerco eléctrico"). La página
*Monitor de cercos* muestra una tarjeta por panel, con las mismas etiquetas que
el cliente más *Llave* (la entrada de llave del panel está cerrada) y *Pulso
OK* (el cerco armado tiene retorno). Debajo, *Eventos recientes* lista los 10
últimos eventos con su *Hora*, *Panel*, *Evento* y *Zona*. Todo se actualiza en
tiempo real.

Cada tarjeta tiene #boton("Armar") o #boton("Desarmar"), según el estado, y
#boton("Silenciar")\; para usarlos se necesita el permiso *Operar cercos
eléctricos*. Con el panel sin conexión, los botones se desactivan y la tarjeta
dice «Sin conexión — comandos deshabilitados.». Al enviar una orden aparece
«Comando enviado.».

=== Aviso de alarma en cualquier página

#captura-pendiente("Panel web: aviso Alarma de cerco eléctrico fijo arriba, con los botones Silenciar sirena, Ver monitor y Reconocer", alto: 5cm)

Ante un evento crítico de cerco, el panel web avisa en cualquier página: arriba
aparece el recuadro *Alarma de cerco eléctrico* con los paneles y eventos, suena
una sirena en el navegador y el título de la pestaña parpadea con «ALARMA DE
CERCO».

- #boton("Silenciar sirena") apaga la sirena del panel.
- #boton("Ver monitor") abre la página Cerco eléctrico.
- #boton("Reconocer") calla el sonido en este navegador y cierra el aviso. No
  cambia nada en el panel.

Si alguien silencia la sirena del panel, el sonido se apaga pero el aviso
sigue; si alguien desarma el panel, sus alarmas desaparecen del aviso.

#nota[Los navegadores no reproducen sonido hasta que el usuario interactúa con
  la página. Si el aviso dice «Haga clic en cualquier parte de la página para
  activar el sonido de la alarma», haga un clic. Si la pestaña está en segundo
  plano, el navegador puede mostrar además una notificación del sistema
  operativo, pero solo si el panel web se abrió por `https` o desde el mismo
  servidor y usted la autorizó.]

=== Historial de eventos

+ En la página Cerco eléctrico, haga clic en #boton("Historial").
+ Filtre por *Panel*, *Evento*, *Desde* y *Hasta*, y haga clic en
  #boton("Buscar"). Los resultados se muestran de a 50, con
  #boton("Anterior") y #boton("Siguiente").
+ Para llevarse los resultados, haga clic en #boton("Exportar CSV")\; descarga
  hasta 100.000 eventos que coincidan con los filtros.

Un símbolo ⚠ junto a un evento (descripción emergente «HMAC no verificado»)
indica que el sistema no pudo verificar la firma con que el panel envió ese
mensaje.

== Configuración

#requiere("Paneles de cerco")

Los paneles de cerco se agregan y se configuran en el panel web, en
#menu("Dispositivos", "Paneles de cerco"). La tabla muestra de cada panel su
*Nombre*, *ID de equipo*, *Ubicación*, *Firmware*, si está *Armado*, si la
*Sirena* suena, el *Nivel AV*, la *Señal* y la *Conexión*, y se actualiza en
tiempo real.

A diferencia de los paneles de alarma, el servidor no se conecta al panel: es el
panel el que se conecta al servidor, con un ID de equipo y una clave propia que
el sistema genera.

=== Agregar un panel

+ Haga clic en #boton("Agregar panel").
+ Escriba el *Nombre*, elija la *Ubicación* y deje marcado *Habilitado*.
+ Haga clic en #boton("Crear y generar clave").
+ El sistema muestra las *Credenciales* del panel: el *ID de equipo* (6
  dígitos), el *Código de enrolamiento* (8 dígitos) y la *URL WebSocket* a la
  que debe conectarse. Anótelas o haga clic en #boton("Copiar todo"), y luego
  en #boton("Listo").
+ El instalador carga el ID y el código en el panel, con CLR Cerco Provisioner
  o con el portal del equipo.
+ Cuando el panel se conecta por primera vez, desaparece la marca *sin
  enrolar* de la columna ID de equipo y la conexión pasa a verde.

#captura("web-paneles-de-cerco-credenciales.png", ancho: 70%, pie: [Credenciales de un panel de cerco recién creado.])

#importante[Las credenciales se muestran una sola vez. El código de
  enrolamiento sirve una sola vez y vence a las 48 horas. Si se pierde o vence,
  use *Rotar clave* (ver más abajo) para generar otro.]

Los paneles se conectan al servidor por el puerto TCP 5092; la URL que se
sugiere es `ws://IP-del-servidor:5092/panel`. El firewall del servidor debe
permitir ese puerto desde la red de los paneles; el instalador lo abre.

#nota[Si los paneles llegan al servidor por otra dirección (NAT, un nombre DNS
  o un proxy con TLS), la URL que se muestra en las credenciales se fija en la
  configuración del servidor (`Cerco` › `Receiver` › `PublicUrl`; vea
  #capitulo(<ap-servidor>)).]

=== Ajustar el panel

#boton("Configurar") abre la configuración del panel. Arriba indica si la
configuración ya está *aplicada en el panel*, si se está *aplicando…* o si el
panel está *desconectado* (los cambios se aplican cuando se conecte).

#table(
  columns: (auto, 1fr),
  table.header[Ajuste][Para qué sirve],
  [Potencia (Nivel Voltaje)], [De 7 a 21. Más nivel es más tiempo de carga y
    un pulso más fuerte. El panel no mide kilovoltios.],
  [Duración de la sirena de alarma], [Cuánto suena la sirena, de 10 a 900
    segundos.],
  [Retardo de salida antes de energizar], [Espera entre la orden de armar y la
    energización, de 0 a 120 segundos.],
  [Chirp de sirena al armar/desarmar], [Un toque corto de sirena al armar y al
    desarmar.],
  [Llave], [*Deshabilitada*; *Nivel* (cerrada = armado, abierta = desarmado); o
    *Pulso* (cada activación arma o desarma). Debajo se ve el estado actual de
    la llave.],
  [Zona 0 (entrada cableada)], [*Deshabilitada*; *Instantánea* (alarma solo
    con el cerco armado); o *24 horas* (alarma siempre, para pánico o
    sabotaje).],
  [Si está abierta, no permitir armar], [Impide armar con la zona 0 abierta.],
)

La *Lectura actual* de la zona 0 ayuda a revisar el cableado: entre 384 y 895
es normal; fuera de ese rango hay un corto o la línea está abierta.

Haga clic en #boton("Guardar configuración"). El aviso dice «Configuración
enviada al panel.» o, si el panel está desconectado, «Guardada. Se aplicará
cuando el panel se conecte.».

=== Programar controles remotos

Los controles remotos de 433 MHz se programan botón por botón; cada botón
tiene su propia acción. Un control de dos botones ocupa dos entradas.

+ En la configuración del panel, escriba un nombre para el botón, por ejemplo
  «Control Juan - botón A».
+ Elija su acción: *Armar*, *Desarmar*, *Armar / desarmar*, *Pánico (sirena)* o
  *Silenciar sirena*.
+ Haga clic en #boton("Programar botón").
+ Antes de 30 segundos, presione el botón del control (la cuenta regresiva se
  ve en pantalla). Al reconocerlo, el panel informa «Control programado», con
  la acción, y el botón aparece en la tabla.

Si no se presiona a tiempo, aparece «Programación de control sin respuesta»;
«Programación de control: memoria llena» indica que el panel no admite más
botones.

En la tabla, cada botón muestra su número, *Nombre*, *Acción* y *Código*. Puede
cambiar el nombre y hacer clic en #boton("Guardar"), o quitarlo con
#boton("Eliminar"). #boton("Eliminar todos los controles") los borra todos: dejan
de funcionar de inmediato.

=== Actualizar el firmware

#captura-pendiente("Panel web: configuración de un panel de cerco, sección Firmware con el progreso de una actualización", alto: 5cm)

+ Desarme el panel y verifique que esté conectado.
+ En la configuración del panel, sección *Firmware*, elija el archivo `.bin`
  de la nueva versión.
+ Haga clic en #boton("Actualizar firmware") y confirme.
+ Espere: el recuadro muestra la descarga y la escritura con su porcentaje. Al
  terminar, el panel se reinicia (alrededor de un minuto) y el recuadro
  confirma «Actualizado: el panel volvió con la versión …».

#importante[La actualización es de un solo panel y solo con el panel desarmado.
  No hay vuelta atrás automática: pruebe cada versión en banco antes de
  instalarla en un cerco en servicio.]

Si el panel no instala la versión, sigue con la anterior y el recuadro dice
«No se actualizó:» seguido del motivo:

#table(
  columns: (auto, 1fr),
  table.header[Mensaje][Qué hacer],
  [el panel está armado; desármelo y reintente], [Desarme el panel y vuelva a
    intentarlo.],
  [ya hay una actualización en curso], [Espere a que termine.],
  [el archivo no tiene una firma válida de CLRobotics; no se instaló], [Use
    solo archivos de firmware entregados por CLRobotics.],
  [la imagen no cabe en el panel o su tamaño no coincide], [Revise que el
    archivo sea el correcto para ese panel.],
  [la descarga se cortó; no se instaló], [Revise la señal WiFi del panel y
    reintente.],
  [el panel no pudo descargar el archivo (HTTP …)], [Revise que el firewall
    del servidor permita el puerto 5093.],
)

El servidor también puede rechazar el archivo antes de enviarlo: «El panel no
está conectado.», «Desarme el panel antes de actualizar el firmware.» o «El
archivo es demasiado grande para el panel (máx. 1 MB).». Si a los tres minutos
el panel no volvió con la versión nueva, el recuadro pide revisar el historial
del panel y, si sigue con la anterior, reintentar.

=== Zona de riesgo

Al final de la configuración, la *Zona de riesgo* reúne tres acciones que
cortan la comunicación con el panel. Para cada una hay que escribir el ID de
equipo del panel antes de que se habilite el botón final, que dice «Entiendo el
riesgo…».

#table(
  columns: (auto, 1fr),
  table.header[Acción][Qué hace y qué arriesga],
  [Rotar clave], [Genera un código de enrolamiento nuevo (válido 48 horas) e
    invalida la clave actual. El panel se desconecta y no vuelve hasta que el
    instalador cargue el código nuevo; mientras tanto no llegan alarmas ni se
    puede operar desde la central.],
  [Reiniciar el equipo], [Reinicia el panel a distancia, sin borrar su
    configuración, su clave ni sus controles. Queda fuera de línea mientras
    arranca, normalmente menos de un minuto. Si no reconecta, hay que ir a
    terreno.],
  [Restaurar de fábrica], [Borra el WiFi, el servidor, la clave y la
    contraseña del panel; conserva los ajustes del cerco y los controles. La
    comunicación se pierde hasta que se vuelva a provisionar en terreno.],
)

Reiniciar y restaurar requieren que el panel esté conectado.

=== Editar y eliminar un panel

- #boton("Editar") cambia el *Nombre*, la *Ubicación* y si está *Habilitado*.
- #boton("Eliminar") pide confirmación. El historial de eventos se conserva.
