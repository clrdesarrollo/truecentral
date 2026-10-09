#import "../plantilla.typ": *
// Fuentes: src/TrueCentralVms.Core/Contracts/LicenseDtos.cs (módulos), installer/suite.iss, installer/client.iss, installer/complemento.iss

= Introducción <cap-introduccion>

CLR TrueCentral VMS es una plataforma de gestión de video multimarca: reúne en
un solo sistema las cámaras, grabadores y equipos de seguridad de distintos
fabricantes (Hikvision, Dahua y equipos ONVIF) para vigilarlos y controlarlos
desde un mismo lugar.

== Componentes del sistema

#table(
  columns: (auto, 1fr, auto),
  table.header[Componente][Para qué sirve][Quién lo usa],
  [Servidor],
  [Se instala en un equipo central como servicio de Windows. Se conecta con los
    equipos, guarda la configuración y entrega el video a los usuarios.],
  [Nadie lo opera directamente],

  [Panel web],
  [Administración del sistema (equipos, usuarios, roles, licencia) y vista en
    vivo desde un navegador, sin instalar nada.],
  [Administradores],

  [Cliente de monitoreo],
  [Aplicación de escritorio para la operación diaria: vista en vivo,
    reproducción, eventos y control de equipos.],
  [Operadores y guardias],

  ..if tiene-modulo("access") {(
    [Complemento de enrolamiento],
    [Programa que se instala en el PC donde está el lector USB de huellas, para
      enrolar huellas desde el panel web.],
    [Quien registra a las personas],
  )},
)

== Módulos de su edición

Lo que puede hacer depende de los módulos que incluye su licencia. Este manual
describe solo los de su edición:

#let catalogo = (
  ("video", "Vista en vivo", "Cámaras en vivo, control PTZ y pantallas auxiliares.", <cap-vista-en-vivo>),
  ("playback", "Reproducción", "Búsqueda y reproducción de las grabaciones de los equipos, y exportación de tramos.", <cap-reproduccion>),
  ("alarms", "Paneles de alarma", "Monitoreo y control de paneles de alarma.", <cap-paneles-de-alarma>),
  ("access", "Control de acceso", "Puertas, personas y credenciales en terminales de acceso.", <cap-control-de-acceso>),
  ("intercom", "Citofonía", "Atención de llamadas desde frentes de citofonía.", <cap-citofonia>),
  ("speakers", "Parlantes IP", "Voz en vivo, sonidos y texto leído en parlantes IP.", <cap-parlantes-ip>),
  ("videowall", "Muro de video", "Control de decodificadores y pantallas del muro.", <cap-muro-de-video>),
  ("anpr", "Reconocimiento de patentes", "Lecturas de patentes de cámaras con esa analítica.", <cap-reconocimiento-de-patentes>),
  ("automation", "Automatizaciones", "Reglas que reaccionan a eventos y actúan solas, y el Centro de eventos donde los operadores confirman las alertas.", <cap-automatizaciones>),
)

#let numero-de-capitulo(etiqueta) = context {
  let destino = query(etiqueta)
  if destino.len() > 0 {
    link(destino.first().location(), str(counter(heading).at(destino.first().location()).first()))
  }
}

#table(
  columns: (auto, 1fr, auto),
  table.header[Módulo][Qué permite][Capítulo],
  ..catalogo
    .filter(m => tiene-modulo(m.at(0)))
    .map(m => (m.at(1), m.at(2), numero-de-capitulo(m.at(3))))
    .flatten(),
)

La administración de los equipos de video, las ubicaciones, los usuarios y
roles, y el estado del sistema están en todas las ediciones.

== Cómo está organizado este manual

- La *Parte I* explica cómo ingresar y moverse por el cliente de monitoreo y
  el panel web.
- La *Parte II* tiene un capítulo por módulo. Cada uno empieza por el uso
  diario y termina con la configuración, que normalmente hace un
  administrador.
- La *Parte III* cubre la administración general: equipos de video,
  ubicaciones, usuarios y permisos, y el estado del sistema.
- Los *apéndices* reúnen los atajos de teclado y los ajustes avanzados del
  servidor.

Si una función no aparece en su pantalla, lo más probable es que su usuario no
tenga el permiso necesario o que el equipo esté fuera de su alcance; vea
#capitulo(<cap-usuarios-y-roles>).

== Convenciones

#table(
  columns: (auto, 1fr),
  table.header[Así se ve][Significa],
  [#boton("Ingresar")], [Un botón o control de la pantalla.],
  [*Servidor*], [El nombre de un campo, casilla o columna, tal como aparece en
    pantalla.],
  [#menu("Sistema", "Servicios")], [Una ruta del menú lateral del panel web:
    abra _Sistema_ y luego _Servicios_.],
  [#tecla("Shift") + #tecla("↑")], [Teclas: mantenga presionada la primera y
    presione la segunda.],
  [#marca(1)], [Un elemento señalado con ese número en la captura más
    cercana.],
  [#requiere("Ver grabaciones")], [Los permisos que necesita su usuario para
    esa función.],
)

Los procedimientos se escriben como pasos numerados, y a lo largo del texto
aparecen estos avisos:

#nota[Información que complementa el texto.]
#consejo[Una forma más rápida o cómoda de hacer algo.]
#importante[Algo que puede causar un problema si se pasa por alto.]
