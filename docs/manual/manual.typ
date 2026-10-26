// Manual de usuario de CLR TrueCentral VMS en PDF.
// Compilar con build-manual.ps1 (pone la versión del archivo VERSION).
// Orden de los capítulos: estructura.typ. Guía de estilo: README.md.
#import "plantilla.typ": *
#import "estructura.typ": *

#show: manual.with(titulo: titulo, subtitulo: subtitulo)

#outline(depth: 2)

#for p in partes {
  parte(p.titulo, descripcion: p.descripcion)
  for c in p.capitulos.filter(en-la-edicion) { contenido-de(c) }
}

#show: apendices
#for c in apendices-del-manual { contenido-de(c) }
