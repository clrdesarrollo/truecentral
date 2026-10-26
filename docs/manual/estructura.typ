// Partes, orden de los capítulos y módulo de licencia de cada uno. Lo leen el
// PDF (manual.typ) y el sitio web (manual-web.typ): un capítulo agregado aquí
// sale en los dos.
//
// modulo: clave de LicenseFeatures sin "module_"; sin modulo = va en todas
// las ediciones.
// icono: ícono del menú del sitio web, el mismo del menú del panel cuando el
// módulo tiene uno (Material Design Icons de wwwroot/vendor/mdi, sin "mdi-";
// es una versión antigua: confirmar que el nombre exista en su css).
#import "plantilla.typ": tiene-modulo

#let titulo = "Manual de usuario"
#let subtitulo = "Operación del cliente de monitoreo y del panel web"

#let partes = (
  (
    titulo: "Primeros pasos",
    descripcion: [Qué es el sistema, cómo ingresar y cómo moverse por el cliente
      de monitoreo y el panel web.],
    capitulos: (
      (archivo: "01-introduccion", icono: "information-outline"),
      (archivo: "02-primeros-pasos", icono: "rocket"),
    ),
  ),
  (
    titulo: "Módulos",
    descripcion: [El uso diario de cada módulo y, al final de cada capítulo, su
      configuración para administradores.],
    capitulos: (
      (archivo: "03-vista-en-vivo", modulo: "video", icono: "video"),
      (archivo: "04-reproduccion", modulo: "playback", icono: "history"),
      // Las alertas del Centro de eventos solo las crean las automatizaciones.
      (archivo: "05-centro-de-eventos", modulo: "automation", icono: "bell-ring"),
      (archivo: "06-paneles-de-alarma", modulo: "alarms", icono: "shield"),
      (archivo: "07-cerco-electrico", icono: "flash"),
      (archivo: "08-control-de-acceso", modulo: "access", icono: "key-variant"),
      (archivo: "09-citofonia", modulo: "intercom", icono: "deskphone"),
      (archivo: "10-parlantes-ip", modulo: "speakers", icono: "volume-high"),
      (archivo: "10b-modulos-de-es", modulo: "io", icono: "toggle-switch"),
      (archivo: "10c-pantallas-de-alarma", modulo: "alarm_screens", icono: "monitor"),
      (archivo: "11-muro-de-video", modulo: "videowall", icono: "monitor-multiple"),
      (archivo: "12-reconocimiento-de-patentes", modulo: "anpr", icono: "car"),
      (archivo: "13-automatizaciones", modulo: "automation", icono: "sitemap"),
    ),
  ),
  (
    titulo: "Administración",
    descripcion: [Equipos de video, ubicaciones, usuarios y permisos, y el estado
      del sistema.],
    capitulos: (
      (archivo: "14-fuentes-de-video", icono: "server-network"),
      (archivo: "15-recursos-y-ubicaciones", icono: "file-tree"),
      (archivo: "16-usuarios-y-roles", icono: "account-key"),
      (archivo: "17-sistema", icono: "settings"),
    ),
  ),
)

#let apendices-del-manual = (
  (archivo: "a1-atajos", icono: "keyboard"),
  (archivo: "a2-servidor", icono: "server"),
)

#let en-la-edicion(capitulo) = {
  let clave = capitulo.at("modulo", default: none)
  clave == none or tiene-modulo(clave)
}

// include resuelve la ruta desde este archivo, por eso vive aquí.
#let contenido-de(capitulo) = include "capitulos/" + capitulo.archivo + ".typ"
