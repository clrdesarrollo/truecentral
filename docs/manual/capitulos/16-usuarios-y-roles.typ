#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Server/wwwroot/app.js, src/TrueCentralVms.Server/wwwroot/roles.js, src/TrueCentralVms.Core/Domain/Permissions.cs, src/TrueCentralVms.Server/Api/UsersApi.cs, src/TrueCentralVms.Server/Api/RolesApi.cs, src/TrueCentralVms.Server/Api/AuditApi.cs, src/TrueCentralVms.Server/Services/AuditCatalog.cs, src/TrueCentralVms.Server/Auth/PasswordGovernance.cs, src/TrueCentralVms.Core/Auth/PasswordPolicy.cs

= Usuarios y roles <cap-usuarios-y-roles>

En el grupo _Seguridad_ del menú lateral se decide quién entra al sistema, qué
puede hacer cada uno y dónde, y se consulta la bitácora de todo lo que pasó.
Cada usuario tiene uno o más *roles*; cada rol reúne *permisos* (qué puede
hacer) y un *alcance* (en qué ubicaciones y recursos). Los permisos se aplican
igual en el panel web y en el cliente de monitoreo: lo que un usuario no puede
hacer no aparece en su menú.

#captura("web-usuarios.png", pie: [Seguridad › Usuarios.])

== Cómo se combinan usuarios, roles y alcance

- Un *rol* dice qué se puede hacer (sus permisos) y dónde (su alcance: todo el
  sistema, o solo ciertas ubicaciones y recursos).
- Un usuario con varios roles *suma* los permisos de todos y también sus
  alcances. No existen permisos que quiten: basta un rol con el permiso para
  tenerlo, y basta un rol con _Todo el sistema_ para ver todo.
- A un usuario se le puede poner, además, un *límite propio por ubicación*:
  de lo que le dan sus roles, solo lo que esté en esas ubicaciones.
- Los permisos y el alcance se suman por separado: un permiso que da un rol
  vale en todo el alcance del usuario, aunque venga de otro rol.
- Hay dos roles de sistema, *Administrador* y *Operador*, y un usuario
  especial, el *superadministrador*.

Los cambios de roles y de alcance valen en el acto, sin que el usuario tenga
que volver a ingresar: su menú y sus pantallas se actualizan solos y, en el
panel web, ve el aviso _Sus permisos cambiaron: la pantalla se actualizó._

== Usuarios

#requiere("Usuarios")

Abra #menu("Seguridad", "Usuarios"). La tabla muestra, por usuario: *Usuario*,
*Roles*, *Alcance* (lo que efectivamente ve y opera, sumando sus roles y su
límite propio), *Estado* (_Habilitado_ o _Deshabilitado_), *Creado* y *Última
clave* (cuándo se cambió la contraseña por última vez).

=== Crear un usuario

+ Haga clic en #boton("Agregar usuario").
+ Escriba el *Nombre de usuario* (entre 3 y 64 caracteres).
+ En *Roles*, marque uno o más. De partida viene marcado _Operador_.
+ Si quiere limitarlo a ciertas ubicaciones además de lo que dan sus roles,
  complete el *Límite propio por ubicación* (ver más abajo).
+ Deje marcada la casilla *Habilitado*.
+ Escriba la *Contraseña*. La lista de requisitos bajo el campo se marca a
  medida que se cumplen.
+ Haga clic en #boton("Crear").

#nota[La licencia fija cuántos usuarios habilitados puede haber. Al llegar al
  tope, el sistema no deja crear ni habilitar más y muestra, por ejemplo, _La
  licencia permite 5 usuarios y ya hay 5 en uso. Amplíe la licencia para
  agregar más._ Los usuarios deshabilitados no cuentan.]

=== Editar un usuario

+ Haga clic en #boton("Editar") en su fila.
+ Cambie lo que necesite. Bajo los roles se muestra su *Alcance actual*.
+ Para cambiarle la contraseña, escríbala en *Contraseña nueva (vacío = no
  cambiar)*; si deja el campo vacío, la contraseña no cambia.
+ Haga clic en #boton("Guardar").

Una fila que usted no puede modificar dice _Sin acceso_ en lugar de los botones
(ver _Nadie da más de lo que tiene_, más abajo).

=== Deshabilitar o eliminar un usuario

- *Deshabilitar*: edite el usuario, desmarque *Habilitado* y guarde. No puede
  volver a ingresar hasta que lo habilite de nuevo, conserva sus datos y deja de
  ocupar cupo de la licencia. Es lo recomendado cuando alguien deja de trabajar
  en el lugar.
- *Eliminar*: haga clic en #boton("Eliminar") y confirme. No se puede deshacer.

Siempre debe quedar al menos un administrador habilitado, nadie puede eliminar
su propio usuario y el superadministrador no se puede deshabilitar ni eliminar.

=== Cerrar las sesiones de un usuario

No hay un botón aparte: las sesiones abiertas de un usuario se cierran en el
acto, en el panel web y en el cliente de monitoreo, cuando usted:

- lo deshabilita;
- le cambia la contraseña;
- le da o le quita el rol Administrador;
- lo elimina.

El usuario vuelve a la pantalla de ingreso. En el panel web ve _Su sesión se
cerró: un administrador cambió su cuenta o el servidor se reinició. Ingrese de
nuevo para seguir._; en el cliente, _El servidor cerró su sesión: su usuario fue
deshabilitado o eliminado, o su contraseña cambió._

#importante[El video que el usuario ya tenía abierto puede seguir corriendo. Para
  cortarlo de inmediato, use #boton("Expulsar") en #menu("Streaming", "Sesiones")
  (#capitulo(<cap-sistema>)).]

== Contraseñas

=== Política

Toda contraseña nueva, la crea quien la crea, debe tener:

- al menos 8 caracteres;
- una letra mayúscula y una minúscula;
- un número;
- un carácter especial, por ejemplo `. , ! $ % # @`.

Los formularios muestran estos requisitos y los van marcando mientras se
escribe. Si alguno falta, el panel dice _La contraseña no cumple la política de
seguridad._

=== Historial

El sistema guarda el historial completo de contraseñas de cada usuario y no
deja repetir ninguna, aunque haya pasado mucho tiempo. Si se intenta, responde
_Esa contraseña ya fue utilizada anteriormente: elija una distinta._ La regla
vale también cuando un administrador cambia la contraseña de otro usuario.

=== Caducidad y cambio obligado

Se puede exigir que las contraseñas se cambien cada cierto número de días.
Cuando la de un usuario vence, el panel web le muestra *Renovación de
contraseña* al ingresar: escribe la *Contraseña actual*, la *Contraseña nueva*
y su confirmación, y hace clic en #boton("Cambiar contraseña"). Hasta hacerlo no
puede entrar. El cliente de monitoreo solo muestra el aviso _La contraseña está
vencida: debe definir una nueva para continuar._, así que la renovación se hace
desde el panel web.

Es el único caso en que el sistema obliga a cambiar la contraseña al ingresar.
La columna *Última clave* de la tabla de usuarios ayuda a ver a quién le queda
poco.

#nota[La caducidad viene desactivada. Para activarla, en el servidor edite el
  archivo `appsettings.Local.json` de la carpeta de instalación, agregue
  `"Security": { "PasswordMaxAgeDays": 90 }` (con los días que quiera) y
  reinicie el servicio. Este archivo no se sobrescribe al actualizar; vea
  #capitulo(<ap-servidor>).]

== Roles

#requiere("Roles")

Abra #menu("Seguridad", "Roles"). A la izquierda está la lista de roles; cada
uno muestra una barra con la parte del catálogo de permisos que cubre, cuántos
permisos y usuarios tiene, y su alcance (_Todo el sistema_ o _Limitado:_ y un
resumen). A la derecha está el editor del rol elegido.

#captura("web-roles.png", pie: [Seguridad › Roles, con un rol de alcance limitado.])

=== Roles de sistema

#table(
  columns: (auto, 1fr),
  table.header[Rol][Características],
  [Administrador],
  [Tiene todos los permisos, también los que traigan versiones futuras, y ve y
    opera todo el sistema. No se modifica ni se elimina, y siempre debe quedar
    al menos un usuario habilitado con él.],
  [Operador],
  [Rol para la operación diaria. Se puede ajustar, pero no eliminar ni
    renombrar. En una instalación nueva incluye todos los permisos de los
    grupos _Video_ y _Monitoreo y operación_ salvo _Borrar lecturas de
    patentes_, más _Ver personas_, _Ver automatizaciones_ y _Ver hora y
    mantenimiento_.],
)

Los roles de sistema llevan la etiqueta _Sistema_ en la lista.

=== Crear un rol

+ Haga clic en #boton("+ Nuevo rol").
+ Elija el punto de partida: _En blanco_ o una de las plantillas (ver la
  tabla). Los permisos de la plantilla se copian al rol y después se pueden
  ajustar; el rol no queda vinculado a la plantilla. Haga clic en
  #boton("Continuar").
+ Escriba el *Nombre* (al menos 2 caracteres) y la *Descripción*, que se ve al
  asignar el rol.
+ Defina el alcance (ver _Alcance del rol_).
+ Marque los permisos con sus interruptores. #boton("Todos") y
  #boton("Ninguno") marcan o desmarcan un grupo completo, y *Buscar permiso…*
  filtra la lista.
+ Haga clic en #boton("Crear rol").

#table(
  columns: (auto, 1fr),
  table.header[Plantilla][Para quién],
  [Guardia], [Mira el video en vivo, atiende alertas y citofonía, y abre puertas.],
  [Operador de central], [Opera todo lo que monitorea: alarmas, cercos, puertas, muro y órdenes por ubicación.],
  [Investigador], [Revisa grabaciones, registros y patentes, y los exporta; no opera equipos.],
  [Administrador de acceso], [Mantiene personas, credenciales, niveles y horarios, y monitorea las puertas.],
  [Técnico], [Instala y configura equipos y su mantenimiento; no administra personas ni usuarios.],
  [Auditor], [Solo consulta: bitácora, registros y grabaciones, sin operar nada.],
)

Algunos permisos incluyen otros: por ejemplo, _Abrir y cerrar puertas_ incluye
_Ver control de acceso_. Al marcar uno se marca solo lo que incluye, y al
desmarcar uno se desmarca lo que depende de él. La descripción de cada permiso
dice qué incluye. Los permisos que dan control sobre la seguridad del sistema
llevan la etiqueta _Seguridad_.

Mientras edita, el contador junto al buscador avisa _sin guardar_. Si cambia de
rol sin guardar, el sistema pregunta si descarta los cambios.

=== Modificar, duplicar o eliminar un rol

- *Modificar*: elija el rol, haga los cambios y haga clic en
  #boton("Guardar cambios"). Los usuarios que lo tienen reciben los cambios en
  el acto.
- *Duplicar*: #boton("Duplicar") crea un rol nuevo con los mismos permisos y el
  mismo alcance, con _(copia)_ al final del nombre.
- *Eliminar*: #boton("Eliminar") borra un rol que no es de sistema. Un rol
  asignado no se puede eliminar: el sistema dice a quiénes está asignado, para
  que se lo quite antes.

Al pie del editor se listan los *Usuarios con este rol*.

=== Alcance del rol

La sección *Alcance: dónde valen estos permisos* ofrece dos opciones:

- *Todo el sistema*: el rol vale en todas partes.
- *Solo en estas ubicaciones y recursos*: el rol vale solo en lo que marque.

Con la segunda opción:

+ En *Ubicaciones*, marque las ubicaciones. Cada una incluye sus
  sububicaciones y todo lo que hay adentro, por eso estas quedan marcadas y
  bloqueadas.
+ En *Recursos sueltos*, agregue recursos de cualquier ubicación: elija el tipo
  (o _Todos los tipos_), busque por cámara, puerta, equipo o ubicación y marque
  los que correspondan. Lo elegido aparece arriba como etiquetas; la × de cada
  una lo quita.
+ Si quien tenga el rol debe supervisar todo lo demás sin operarlo, marque
  *Puede ver el resto, sin operarlo (supervisión)*.

Los recursos sueltos pueden ser una cámara, una puerta, un área de alarma, una
zona, un cerco, un parlante o un citófono, o un equipo completo (equipo de
video, equipo de acceso o panel de alarma). Un equipo completo incluye también
los canales, puertas o áreas que se le agreguen después, y un área de alarma
incluye sus zonas. Un recurso _Por ubicar_ solo puede entrar en un alcance como
recurso suelto.

#consejo[Use los recursos sueltos para casos como «solo la cámara del acceso
  principal» o «el control de acceso número 1 de cada sucursal», que no
  coinciden con una ubicación del árbol.]

#nota[Una ubicación que está en el alcance de un rol no se puede eliminar en
  Recursos hasta quitarla del rol.]

== Catálogo de permisos

Los permisos se agrupan como en la página Roles. _(Seguridad)_ marca los que dan
control sobre la seguridad del sistema.

#let grupo(nombre) = table.cell(colspan: 2, fill: fondo-suave)[*#nombre*]
#let seg = text(size: 0.85em, fill: gris)[(Seguridad)]
#let incluye(nombre) = text(fill: gris)[ _Incluye: #nombre._]

#table(
  columns: (auto, 1fr),
  table.header[Permiso][Qué permite],
  grupo[Video],
  [Ver video en vivo], [Abrir cámaras en vivo y sus imágenes instantáneas.],
  [Mover cámaras PTZ], [Mover, acercar y llamar posiciones guardadas de las cámaras PTZ. #incluye[Ver video en vivo]],
  [Guardar vistas], [Crear, modificar y borrar vistas (grupos de cámaras con su distribución). #incluye[Ver video en vivo]],
  [Ver grabaciones], [Buscar y reproducir video grabado.],
  [Exportar grabaciones], [Descargar tramos de video grabado al equipo del operador. #incluye[Ver grabaciones]],
  [Operar el muro de video], [Poner cámaras en el muro, limpiarlo y aplicar diseños guardados. #incluye[Ver video en vivo]],
  [Guardar diseños del muro], [Guardar y borrar diseños del muro de video. #incluye[Operar el muro de video]],
  grupo[Monitoreo y operación],
  [Atender alertas], [Ver el centro de eventos y dar por atendidas las alertas.],
  [Órdenes por ubicación], [Armar o desarmar con una sola orden todas las áreas de alarma de una ubicación.],
  [Ver alarmas], [Ver el estado de los paneles de alarma y sus eventos.],
  [Operar alarmas], [Armar, desarmar, reponer alarmas y anular zonas. #incluye[Ver alarmas]],
  [Ver cercos eléctricos], [Ver el estado de los cercos eléctricos y sus eventos.],
  [Operar cercos eléctricos], [Armar, desarmar, silenciar y activar la sirena de los cercos. #incluye[Ver cercos eléctricos]],
  [Ver control de acceso], [Ver puertas, su estado y los registros de acceso.],
  [Abrir y cerrar puertas], [Abrir, cerrar o dejar fijas las puertas a distancia. #incluye[Ver control de acceso]],
  [Exportar registros de acceso], [Descargar los registros de acceso en PDF o Excel. #incluye[Ver control de acceso]],
  [Ver patentes], [Ver las lecturas de patentes con sus fotos.],
  [Borrar lecturas de patentes], [Eliminar lecturas de patentes guardadas. #incluye[Ver patentes]],
  [Usar parlantes], [Reproducir audios, hablar y ajustar el volumen de los parlantes IP.],
  [Atender citofonía], [Contestar, rechazar y cortar llamadas, y abrir la puerta del frente.],
  grupo[Personas],
  [Ver personas], [Ver las personas registradas, sus fotos, tarjetas y niveles de acceso.],
  [Administrar personas], [Crear, modificar y borrar personas, sus credenciales, niveles de acceso y horarios. #incluye[Ver personas]],
  grupo[Configuración],
  [Fuentes de video], [Agregar y configurar cámaras, grabadores, decodificadores y muros; buscar equipos en la red.],
  [Equipos de acceso], [Agregar y configurar terminales y puertas, y sincronizarlos.],
  [Paneles de alarma], [Agregar y configurar paneles de alarma y la receptora.],
  [Paneles de cerco], [Agregar y configurar paneles de cerco, sus controles remotos y su firmware.],
  [Parlantes IP], [Agregar y configurar parlantes IP y su biblioteca de audios.],
  [Biblioteca de sonidos], [Subir, ajustar y borrar los sonidos que usan las automatizaciones.],
  [Citofonía], [Agregar y configurar frentes y monitores de citofonía.],
  [Recursos y ubicaciones], [Crear el árbol de ubicaciones, ubicar los recursos y escribir sus fichas.],
  [Ver automatizaciones], [Ver las automatizaciones y su historial de ejecuciones.],
  [Editar automatizaciones], [Crear, modificar, probar y borrar automatizaciones; correo de salida. #incluye[Ver automatizaciones]],
  [Ver hora y mantenimiento], [Ver la hora de los equipos y revisarla.],
  [Mantenimiento de equipos], [Corregir la hora, reiniciar y restablecer equipos. #incluye[Ver hora y mantenimiento]],
  grupo[Sistema y seguridad],
  [Usuarios #seg], [Crear, modificar y deshabilitar usuarios, y asignarles roles y alcance (nunca más permisos ni más ubicaciones de los que tiene quien los asigna).],
  [Roles #seg], [Crear y modificar roles (solo con permisos que tiene quien los edita).],
  [Bitácora de auditoría #seg], [Consultar y exportar la bitácora de auditoría.],
  [Sesiones], [Ver quién está conectado, cortar transmisiones y liberar puestos.],
  [Servicios del servidor #seg], [Detener, iniciar y reiniciar los servicios y el servidor.],
  [Licencia #seg], [Activar, renovar y desactivar la licencia del sistema.],
)

#nota[Algunas páginas se ven sin permiso especial, pero solo con lectura: por
  ejemplo, _Recursos_, _Servicios_ y _Licencia_ muestran su información a todo
  usuario, y sus botones de cambio solo a quien tiene el permiso.]

== Límite propio de un usuario

#requiere("Usuarios")

Además de lo que le dan sus roles, un usuario puede quedar limitado a ciertas
ubicaciones. Se define en su formulario, en *Límite propio por ubicación
(opcional)*:

+ Elija _De eso, solo lo que esté en estas ubicaciones (cada una con sus
  sububicaciones)_. La otra opción, _Sin límite propio: lo que le den sus
  roles_, es la que viene marcada.
+ Marque las ubicaciones. Marcar una incluye sus sububicaciones, que quedan
  marcadas y bloqueadas.
+ Si debe supervisar el resto sin operarlo, marque *Puede ver el resto, sin
  operarlo (supervisión)*.
+ Guarde.

Fuera de su alcance no ve listas, eventos ni video, ni puede dar órdenes. Lo
que está por ubicar queda fuera de un límite propio; el formulario avisa
cuántos recursos hay en esa situación. Si el usuario tiene el rol
Administrador, esta sección no se ofrece: _Los administradores ven y operan
todo._

En la columna *Alcance*, la etiqueta _límite propio_ indica qué usuarios lo
tienen. Al cambiar el alcance, las sesiones de video del usuario que quedaron
fuera se cortan y su puesto recarga sus listas.
Cómo se ordenan las ubicaciones se explica en
#capitulo(<cap-recursos-y-ubicaciones>).

== Superadministrador

El administrador que se creó al activar la plataforma es el
*superadministrador* (en una instalación que ya existía antes de esta función,
el sistema marcó como tal al administrador más antiguo). Se reconoce por la
etiqueta _Superadministrador_ en la tabla de usuarios y su alcance dice _Todo
(superadministrador)_.

- Tiene acceso total, sin importar sus roles ni ningún alcance.
- No se puede deshabilitar ni eliminar.
- Solo él puede cambiar su nombre y su contraseña: su formulario no tiene roles
  ni alcance. Para los demás administradores su fila dice _Sin acceso_.

#importante[Guarde las credenciales del superadministrador en un lugar seguro:
  nadie más puede cambiar su contraseña.]

== Nadie da más de lo que tiene

Quien administra usuarios o roles sin tener el rol Administrador solo puede
repartir lo que él mismo tiene. En pantalla lo verá así:

- En *Usuarios*, los roles que usted no puede asignar aparecen bloqueados
  (_Tiene permisos que usted no tiene_), y las filas de usuarios con más
  permisos o más alcance que usted dicen _Sin acceso_.
- En *Roles*, los permisos que usted no tiene aparecen bloqueados (_Usted no
  tiene este permiso: no puede otorgarlo_); un rol con más permisos o alcance
  que el suyo se puede ver pero no modificar; al crear un rol desde una
  plantilla, el sistema avisa cuántos permisos de ella quedarán fuera; y el
  buscador de recursos sueltos solo ofrece lo que usted opera.
- Sin el rol Administrador, nadie puede modificar su propio usuario.

Si aun así se intenta, el sistema lo rechaza con mensajes como estos:

#table(
  columns: (1fr, 1fr),
  table.header[Mensaje][Qué significa],
  [_No puede asignar el rol '…': tiene permisos que usted no tiene._],
  [Ese rol da permisos que usted no tiene.],
  [_Solo un administrador puede asignar el rol Administrador._],
  [El rol Administrador solo lo asigna quien ya lo tiene.],
  [_Usted tiene un alcance limitado: el usuario también debe quedar limitado a ubicaciones o recursos dentro del suyo (con roles limitados o con un límite propio)._],
  [Usted no ve todo el sistema, así que no puede dejar a otro sin límite.],
  [_El usuario quedaría con ubicaciones o recursos fuera de su propio alcance._],
  [Quite del rol o del límite propio lo que está fuera de su alcance.],
  [_No puede dar "ver el resto": usted no lo tiene._],
  [Desmarque _Puede ver el resto, sin operarlo_.],
  [_No puede otorgar permisos que usted no tiene: …_],
  [Desmarque los permisos que el mensaje nombra.],
  [_El alcance del rol debe quedar dentro del suyo: solo ubicaciones y recursos que usted opera._],
  [Ajuste el alcance del rol a lo que usted opera.],
  [_No puede modificar su propio usuario: pídaselo a otro administrador de usuarios._],
  [Pida el cambio a otra persona con el permiso _Usuarios_.],
  [_Debe existir al menos un administrador habilitado._],
  [Habilite o cree otro administrador antes de quitarle el rol, deshabilitar o eliminar a este.],
  [_El rol está asignado a …: quíteselo antes de eliminarlo._],
  [Quite el rol a esos usuarios y vuelva a intentarlo.],
)

== Auditoría

#requiere("Bitácora de auditoría")

La bitácora de auditoría registra quién hizo qué, cuándo, desde dónde y con qué
resultado, tanto lo que se hace en el panel web y en el cliente como lo que
hace el propio servidor (por ejemplo, la caída de un servicio). Abra
#menu("Seguridad", "Auditoría").

#captura("web-auditoria.png", pie: [Seguridad › Auditoría, filtrada por la categoría Usuarios.])

=== Qué se registra

#table(
  columns: (32%, 1fr),
  table.header[Categoría][Ejemplos],
  [Autenticación y permisos], [Ingresos, ingresos rechazados, cierres de sesión, cambios de contraseña, sesiones revocadas, puestos liberados, acciones rechazadas por falta de permiso o por estar fuera del alcance.],
  [Usuarios], [Usuarios creados, modificados y eliminados; cambios de roles y de límite propio.],
  [Roles y permisos], [Roles creados, modificados (con lo agregado y lo quitado) y eliminados.],
  [Dispositivos], [Equipos agregados, modificados, eliminados y revalidados; búsquedas en la red; cambios de IP.],
  [Recursos y ubicaciones], [Ubicaciones, recursos ubicados, fichas modificadas y órdenes por ubicación.],
  [Video en vivo], [Quién comenzó y dejó de ver cada canal, sesiones expulsadas, capturas y vistas.],
  [Control PTZ], [Movimientos y presets.],
  [Reproducción], [Búsquedas, reproducciones y exportaciones.],
  [Muro de video], [Configuración y operación de decodificadores y muros.],
  [Patentes (ANPR), Paneles de cerco eléctrico, Paneles de alarma, Control de acceso, Hora y mantenimiento de equipos, Parlantes IP, Citofonía, Automatizaciones],
  [La configuración y la operación de cada módulo: órdenes, aperturas de puertas, llamadas, cambios en el padrón, ejecuciones, etc.],
  [Licenciamiento], [Activaciones, importaciones, revalidaciones, cambios de estado y operaciones rechazadas por la licencia.],
  [Sistema], [Inicio y detención del servidor, órdenes y caídas de servicios, exportaciones y purgas de la bitácora.],
)

=== Buscar en la bitácora

+ Complete los filtros que necesite: *Desde*, *Hasta*, *Categoría*,
  *Subcategoría* (depende de la categoría elegida), *Usuario*, *Resultado*
  (_Éxito_ o _Fallo_) y *Buscar texto* (en el detalle, el objeto o la IP).
+ Haga clic en #boton("Buscar") o presione #tecla("Enter") en el campo de texto.

#boton("Limpiar") quita todos los filtros. La tabla muestra 50 eventos por
página, con las columnas *Fecha*, *Usuario*, *Origen* (_Panel web_,
_Cliente_ o _Servidor_), *IP*, *Categoría*, *Acción*, *Objeto*, *Detalle* y
*Resultado*; muévase con #boton("« Anterior") y #boton("Siguiente »"). Haga
clic en una fila para ver el evento completo, con sus *Datos adicionales*.

El historial de cada recurso, en su ficha de Recursos, también muestra las
entradas de la bitácora que le corresponden.

=== Exportar

#boton("Exportar CSV") descarga los eventos que coinciden con los filtros, hasta
100.000, en un archivo `.csv` separado por punto y coma que se abre con Excel.
La exportación queda registrada en la propia bitácora.

#nota[De fábrica, la bitácora conserva todos los eventos para siempre. Si su
  política de retención exige borrar los antiguos, pida a soporte que configure
  los días de retención en el servidor; cada purga queda a su vez registrada.]
