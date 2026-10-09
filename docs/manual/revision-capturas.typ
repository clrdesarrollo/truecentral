// Hoja de revisión: cada captura de capturas/marcas.json con sus marcas y su
// leyenda, tal como sale en el manual. Para revisar después de recapturar:
//   typst compile docs\manual\revision-capturas.typ revision.pdf
// (o con salida "revision-{0p}.png" para mirar página por página).
#import "plantilla.typ": *

#set page(paper: "a4", margin: 1.6cm)
#set text(font: fuente, size: 9pt, lang: "es")

#let filtro = sys.inputs.at("filtro", default: "")

#for (archivo, marcas) in marcas-capturadas.pairs().filter(p => filtro == "" or filtro in p.at(0)) {
  block(breakable: false, below: 1.6em, {
    text(weight: "bold", size: 10pt, archivo)
    text(fill: gris)[ · #marcas.len() marcas]
    // Fija (no flotante): que el nombre quede junto a su imagen.
    captura(archivo, flotante: false,
      ancho: if archivo.starts-with("web-inicio") or archivo.starts-with("cliente-inicio-sesion") { 55% } else { 100% })
  })
}
