// Manual de usuario de CLR TrueCentral VMS.
// Compilar con build-manual.ps1 (pone la versión del archivo VERSION).
// Guía de estilo y componentes: README.md de esta carpeta.
#import "plantilla.typ": *

#show: manual.with(
  titulo: "Manual de usuario",
  subtitulo: "Operación del cliente de monitoreo y del panel web",
)

#outline(depth: 2)

#parte("Primeros pasos", descripcion: [Qué es el sistema, cómo ingresar y cómo
  moverse por el cliente de monitoreo y el panel web.])
#include "capitulos/01-introduccion.typ"
#include "capitulos/02-primeros-pasos.typ"

#parte("Módulos", descripcion: [El uso diario de cada módulo y, al final de
  cada capítulo, su configuración para administradores.])
#modulo("video", include "capitulos/03-vista-en-vivo.typ")
#modulo("playback", include "capitulos/04-reproduccion.typ")
// Las alertas del Centro de eventos solo las crean las automatizaciones.
#modulo("automation", include "capitulos/05-centro-de-eventos.typ")
#modulo("alarms", include "capitulos/06-paneles-de-alarma.typ")
#include "capitulos/07-cerco-electrico.typ"
#modulo("access", include "capitulos/08-control-de-acceso.typ")
#modulo("intercom", include "capitulos/09-citofonia.typ")
#modulo("speakers", include "capitulos/10-parlantes-ip.typ")
#modulo("videowall", include "capitulos/11-muro-de-video.typ")
#modulo("anpr", include "capitulos/12-reconocimiento-de-patentes.typ")
#modulo("automation", include "capitulos/13-automatizaciones.typ")

#parte("Administración", descripcion: [Equipos de video, ubicaciones, usuarios
  y permisos, y el estado del sistema.])
#include "capitulos/14-fuentes-de-video.typ"
#include "capitulos/15-recursos-y-ubicaciones.typ"
#include "capitulos/16-usuarios-y-roles.typ"
#include "capitulos/17-sistema.typ"

#show: apendices
#include "capitulos/a1-atajos.typ"
#include "capitulos/a2-servidor.typ"
