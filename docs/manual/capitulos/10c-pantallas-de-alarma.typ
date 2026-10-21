#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/alarm-screens.js, src/TrueCentralVms.Server/Api/AlarmScreensApi.cs, src/TrueCentralVms.Server/Services/AlarmScreenService.cs, src/TrueCentralVms.Server/Services/Workflows/Actions/NotifyAction.cs, src/TrueCentralVms.AlarmScreen/, installer/screen/

= Pantallas de alarma <cap-pantallas-de-alarma>

Una pantalla de alarma es un monitor en una caseta de guardia, conectado a una
Raspberry Pi, que muestra los avisos de las automatizaciones con sonido, foto
y las consignas del recurso, y deja marcarlos como vistos con un toque.

Es el mismo aviso que ve el operador en la ventana de alarma: si el guardia lo
marca como visto en la caseta, la ventana del operador se cierra sola; si el
operador lo confirma, la pantalla de la caseta deja de sonar. Cada
automatización elige dónde se muestra el aviso: en la ventana de alarma, en
las pantallas de alarma o en ambas.

En la pantalla no se inicia sesión: el guardia solo marca los avisos. La marca
queda a nombre de la pantalla desde la que se hizo, de modo que el registro
responde dónde se atendió el aviso y cuándo.

#captura-pendiente("Pantalla de la caseta: lista de avisos a la izquierda, detalle con la foto y el botón MARCAR COMO VISTA a la derecha", alto: 6cm)

== Lo que ve el guardia

Arriba: el nombre de la pantalla y de la instalación, el estado de la conexión,
cuántos avisos quedan sin ver, la hora y el botón #boton("TODAS VISTAS").

- *Lista* (a la izquierda, o abajo con el monitor en pie): los avisos recientes.
  Los pendientes resaltan con la etiqueta _SIN VER_; los ya marcados se ven
  atenuados con _✓ vista_.
- *Detalle*: la importancia (CRÍTICA, ADVERTENCIA o INFORMATIVA), el título, la
  ubicación del recurso o el mensaje, las consignas bajo *QUÉ HACER* (si el
  recurso tiene ficha con consignas), la hora, la automatización que lo emitió
  y la foto. #boton("AMPLIAR") muestra la foto a pantalla completa.
- *Aviso nuevo*: al llegar un aviso, ocupa toda la pantalla en rojo con su foto
  y el botón #boton("MARCAR COMO VISTA"). Si llega otro antes de marcarlo, se
  reemplaza el contenido en vez de apilar avisos.

Mientras quede algún aviso sin marcar, la pantalla recuerda con un sonido cada
cierto tiempo (vea «Sonidos y ajustes»). Marcar un aviso corta el sonido en el
acto, se haga en esta pantalla, en otra o en TrueCentral.

#nota[Un aviso marcado por otra pantalla aparece como _Vista en Caseta norte a
  las 18:10:29_; uno confirmado por un operador, como _Confirmada en
  TrueCentral por …_.]

== Instalar una pantalla

Se necesita una Raspberry Pi 4 o 5 con Raspberry Pi OS de 64 bits (con
escritorio), un monitor y, si se quiere sonido, parlantes por HDMI, por el jack
o por USB.

+ Copie a la Pi la carpeta `installer/screen` del sistema y el paquete
  `truecentral-screen-<versión>-linux-arm64.tar.gz` (lo trae la carpeta `dist`
  de la versión; también queda en el servidor, en `screen-packages`).
+ En la Pi, ejecute `./install-screen.sh`. Instala la aplicación, la deja como
  servicio que arranca solo, activa el inicio de sesión automático y la
  actualización diaria.
+ Reinicie la Pi. Al arrancar busca el servidor en la red y muestra un código de
  6 dígitos con el texto _Apruebe este código en TrueCentral → Pantallas de
  alarma_, y debajo la *huella del servidor* que ve.

#importante[La búsqueda automática del servidor no cruza VLANs ni subredes. Si
  la pantalla no lo encuentra, muestra _Buscando el servidor_ con una casilla
  para escribir su dirección (por ejemplo, `https://192.168.1.50`) y el
  botón #boton("USAR").]

== Aprobar una pantalla

#requiere("Pantallas de alarma")

Abra #menu("Dispositivos", "Pantallas de alarma"). Las pantallas que piden
emparejarse aparecen arriba, en *Piden emparejarse*, con su código en grande,
el nombre del equipo, su dirección IP y cuándo lo pidieron.

+ Compruebe que el código coincide con el que muestra la pantalla en la caseta.
+ Haga clic en #boton("Aprobar"). La ventana muestra la huella del servidor:
  debe ser la misma que la de la pantalla. Si es otra, *no la apruebe*: hay
  alguien interceptando la red entre la caseta y el servidor.
+ Escriba el *Nombre de la pantalla*, por ejemplo _Caseta acceso principal_. Es
  el nombre que verá en las automatizaciones y en el registro de quién marcó
  cada aviso.
+ Haga clic en #boton("Aprobar").

En unos segundos la pantalla recibe su credencial, muestra su nombre y queda
_En línea_. Desde ahí arranca sola, también después de un corte de luz, y solo
acepta a ese servidor: si otro equipo se hace pasar por él (o si se cambia el
certificado del servidor), la pantalla se detiene y lo avisa. Para volver a
emparejarla después de un cambio de certificado, toque #boton("OLVIDAR
SERVIDOR Y VOLVER A EMPAREJAR") en la pantalla y reinicie su emparejamiento
en el panel.

#boton("Rechazar") descarta el pedido: el equipo no puede volver a pedirlo
hasta que se lo quite de la lista.

#nota[La licencia fija cuántas pantallas pueden estar aprobadas a la vez; la
  página lo muestra arriba (_Licencia: 1 de 4 pantalla(s) aprobada(s)_). Por
  sobre el cupo, la aprobación se rechaza.]

== Administrar las pantallas

#requiere("Pantallas de alarma")

La tabla *Pantallas* muestra de cada una: el nombre, la *Conexión* (_En línea_,
_Sin conexión_ con la última vez que se vio, _Deshabilitada_ o _Emparejando…_),
el equipo y su IP, la versión de la aplicación, el volumen, el monitor y cuántos
avisos dirigidos a ella siguen *Sin ver*. El estado se actualiza solo.

- #boton("Editar"): el *Nombre*, el *Volumen*, el *Monitor* (_Apaisado_: lista a
  la izquierda y detalle a la derecha; _En pie_: detalle arriba y lista abajo,
  para monitores rotados) y la *Salida de audio* (HDMI, jack o parlante USB:
  la lista la informa la propia pantalla al conectarse). La pantalla aplica los
  cambios en el acto, sin ir a la caseta.
- #boton("Reiniciar equipo"): reinicia la Raspberry Pi. Solo con la pantalla
  conectada; queda sin mostrar avisos alrededor de un minuto.
- #boton("Deshabilitar"): deja de recibir avisos hasta que se habilite con
  #boton("Habilitar"). Conserva su credencial: al habilitarla vuelve sola.
- #boton("Reiniciar emparejamiento"): descarta su credencial. La pantalla
  muestra un código nuevo y hay que aprobarla otra vez. Sirve si se reinstaló
  la Pi o se cambió su tarjeta SD.
- #boton("Quitar"): la borra de la lista. Si el equipo vuelve a anunciarse,
  aparece de nuevo esperando aprobación.

=== Sonidos y ajustes

El botón #boton("Sonidos y ajustes") define lo que es común a todas las
pantallas:

- *Nombre de la instalación*: el que muestran en su encabezado (vacío = el
  nombre del servidor).
- *Aviso nuevo*, *Aviso crítico* y *Recordatorio*: sonidos de la biblioteca
  (#menu("Automatizaciones", "Sonidos")) o el tono propio de la pantalla. Son
  los mismos en todas las casetas: un guardia que cambia de caseta tiene que
  oír lo mismo. Un aviso con alarma sonora propia suena con la suya.
- *Recordar cada (segundos)*: cada cuánto suena el recordatorio mientras quede
  algún aviso sin marcar (10 por omisión; 0 = solo al llegar).
- *Insistir hasta (veces)*: 0 = sin límite, suena hasta que se marque todo.
- *Al reconectarse, traer los avisos de las últimas (horas)*: 24 por omisión.

#importante[Con un tope en *Insistir hasta*, un aviso sin atender termina en
  silencio. Déjelo en 0 salvo que el sitio lo pida expresamente.]

== Mostrar avisos en las pantallas

#modulo("automation")[
  En la acción _Avisar a los operadores_ de una automatización, elija en *Dónde
  se muestra*:

  - _Ventana de alarma de los operadores_: lo de siempre.
  - _Pantallas de alarma de las casetas_: solo en las pantallas. Los operadores
    no reciben ventana de alarma, pero el aviso queda en el Centro de eventos.
  - _Ventana de alarma y pantallas de alarma_: en los dos lados.

  Con las pantallas, marque en *Pantallas de alarma* a cuáles va; sin ninguna
  marcada, va a todas las aprobadas. Vea #capitulo(<cap-automatizaciones>).
]

#nota[Si las pantallas no están en la licencia, o no hay ninguna aprobada, el
  aviso sale en la ventana de alarma de los operadores: un aviso que nadie
  puede ver es peor que uno en el lugar equivocado. El historial de la
  ejecución lo dice.]

== Actualización y registro

Cada pantalla revisa todas las noches si el servidor tiene una versión más
nueva de su aplicación y la instala sola. El instalador del servidor trae la
aplicación de su misma versión: al actualizar el servidor, las pantallas se
actualizan la noche siguiente. Si la versión nueva no arranca, la pantalla
vuelve sola a la anterior.

Todo queda en la bitácora de auditoría, categoría _Pantallas de alarma_:
los pedidos de emparejamiento, las aprobaciones, los cambios, los reinicios,
el «Todas vistas» y cuándo una pantalla se desconecta por más de un minuto o
vuelve. Cada aviso marcado en una pantalla queda como _Alerta confirmada_, con
el origen _Pantalla de alarma_.
