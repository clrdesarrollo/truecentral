// Partes, orden de los capítulos y módulo de licencia de cada uno. Lo leen el
// PDF (manual.typ) y el sitio web (manual-web.typ): un capítulo agregado aquí
// sale en los dos.
//
// modulo: clave de LicenseFeatures sin "module_"; sin modulo = va en todas
// las ediciones.
#import "plantilla.typ": tiene-modulo

#let titulo = "Manual de usuario"
#let subtitulo = "Operación del cliente de monitoreo y del panel web"

#let partes = (
  (
    titulo: "Primeros pasos",
    descripcion: [Qué es el sistema, cómo ingresar y cómo moverse por el cliente
      de monitoreo y el panel web.],
    capitulos: (
      (archivo: "01-introduccion"),
      (archivo: "02-primeros-pasos"),
    ),
  ),
  (
    titulo: "Módulos",
    descripcion: [El uso diario de cada módulo y, al final de cada capítulo, su
      configuración para administradores.],
    capitulos: (
      (archivo: "03-vista-en-vivo", modulo: "video"),
      (archivo: "04-reproduccion", modulo: "playback"),
      // Las alertas del Centro de eventos solo las crean las automatizaciones.
      (archivo: "05-centro-de-eventos", modulo: "automation"),
      (archivo: "06-paneles-de-alarma", modulo: "alarms"),
      (archivo: "07-cerco-electrico"),
      (archivo: "08-control-de-acceso", modulo: "access"),
      (archivo: "09-citofonia", modulo: "intercom"),
      (archivo: "10-parlantes-ip", modulo: "speakers"),
      (archivo: "10b-modulos-de-es", modulo: "io"),
      (archivo: "10c-pantallas-de-alarma", modulo: "alarm_screens"),
      (archivo: "11-muro-de-video", modulo: "videowall"),
      (archivo: "12-reconocimiento-de-patentes", modulo: "anpr"),
      (archivo: "13-automatizaciones", modulo: "automation"),
    ),
  ),
  (
    titulo: "Administración",
    descripcion: [Equipos de video, ubicaciones, usuarios y permisos, y el estado
      del sistema.],
    capitulos: (
      (archivo: "14-fuentes-de-video"),
      (archivo: "15-recursos-y-ubicaciones"),
      (archivo: "16-usuarios-y-roles"),
      (archivo: "17-sistema"),
    ),
  ),
)

#let apendices-del-manual = (
  (archivo: "a1-atajos"),
  (archivo: "a2-servidor"),
)

#let en-la-edicion(capitulo) = {
  let clave = capitulo.at("modulo", default: none)
  clave == none or tiene-modulo(clave)
}

// include resuelve la ruta desde este archivo, por eso vive aquí.
#let contenido-de(capitulo) = include "capitulos/" + capitulo.archivo + ".typ"
