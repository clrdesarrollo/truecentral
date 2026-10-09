// Plantilla del manual de usuario de CLR TrueCentral VMS.
//
// Uso (ver manual.typ):
//   #import "plantilla.typ": *
//   #show: manual.with(titulo: "Manual de usuario")
//
// Cada capítulo importa la plantilla para usar los componentes:
//   #boton("Ingresar")              botón o control de la pantalla
//   #tecla("Esc")                   tecla del teclado
//   #menu("Sistema", "Servicios")   ruta de menú
//   #nota[…] #consejo[…] #importante[…]
//   + primer paso                   los pasos son la enumeración normal de
//   + segundo paso                  Typst, con números en círculo
//   #captura("archivo.png", marcas: ((x, y), …), pie: […])
//                                   imagen de capturas/ con marcas numeradas;
//                                   x e y son fracciones (0 a 1) del ancho y
//                                   alto de la imagen, así que volver a
//                                   capturar la pantalla no borra las marcas
//   #marca(2)                       referencia en el texto a la marca 2
//   #captura-pendiente("…")         hueco para una captura que todavía falta
//   #pendiente[…]                   texto por escribir
//   #modulo("speakers")[…]          solo aparece si la edición trae el módulo
//
// Parámetros de compilación (typst compile --input clave=valor), que
// build-manual.ps1 completa solo:
//   version  versión del producto (archivo VERSION de la raíz)
//   modulos  módulos de la edición separados por coma, con las claves de
//            LicenseFeatures sin el prefijo "module_" (video,playback,…);
//            vacío = todos
//   final    "true" en la compilación de entrega: falla si queda algún
//            #pendiente o #captura-pendiente

// ---------------------------------------------------------------------------
// Parámetros
// ---------------------------------------------------------------------------

#let version = sys.inputs.at("version", default: "desarrollo")
#let final = sys.inputs.at("final", default: "false") == "true"
// "true" = falla ante una referencia a un capítulo que la edición no trae,
// aunque queden capturas pendientes (build-manual.ps1 -RevisarReferencias).
#let revisar-referencias = sys.inputs.at("referencias", default: "false") == "true"
#let modulos = {
  let lista = sys.inputs.at("modulos", default: "").trim()
  if lista == "" { none } else { lista.split(",").map(m => m.trim()) }
}

#let tiene-modulo(clave) = modulos == none or clave in modulos
#let modulo(clave, cuerpo) = if tiene-modulo(clave) { cuerpo }

// ---------------------------------------------------------------------------
// Identidad (colores del panel web, styles.css)
// ---------------------------------------------------------------------------

#let acento = rgb("#3B82F6")
#let tinta = rgb("#1F2937")
#let gris = rgb("#6B7280")
#let linea = rgb("#E5E7EB")
#let fondo-suave = rgb("#F3F4F6")
#let portada-fondo = rgb("#0F141A")
#let portada-texto = rgb("#E6EDF3")
#let portada-tenue = rgb("#8B98A5")
#let ambar = rgb("#F59E0B")
#let magenta = rgb("#C026D3")

#let fuente = ("Segoe UI", "Libertinus Serif")
#let fuente-mono = ("Cascadia Mono", "Consolas", "DejaVu Sans Mono")

#let meses = ("enero", "febrero", "marzo", "abril", "mayo", "junio", "julio",
  "agosto", "septiembre", "octubre", "noviembre", "diciembre")
#let fecha-larga(d) = str(d.day()) + " de " + meses.at(d.month() - 1) + " de " + str(d.year())

// Logo dibujado (el PNG del panel es de 128 px y en la portada se vería
// borroso): cuadro oscuro con cuatro cuadrados azules.
#let logo(tam: 2cm) = {
  let borde = tam * 0.17
  let separacion = tam * 0.09
  let cuadro = (tam - 2 * borde - separacion) / 2
  let pieza(color) = rect(width: cuadro, height: cuadro, radius: cuadro * 0.2, fill: color)
  box(
    width: tam, height: tam, inset: borde, radius: tam * 0.22,
    fill: rgb("#11161D"), stroke: (tam * 0.02) + rgb("#2A3542"),
    grid(
      columns: (cuadro, cuadro), rows: (cuadro, cuadro), gutter: separacion,
      pieza(rgb("#6BB0F9")), pieza(rgb("#2D7CF3")),
      pieza(rgb("#2D7CF3")), pieza(rgb("#2D7CF3")),
    ),
  )
}

// ---------------------------------------------------------------------------
// Componentes para los capítulos
// ---------------------------------------------------------------------------

#let circulo(n, relleno: acento, color: white, radio: 0.62em, borde: none) = circle(
  radius: radio, fill: relleno, stroke: borde, inset: 0pt,
  align(center + horizon, text(size: radio * 1.15, weight: "bold", fill: color, str(n))),
)

// En una línea de texto el círculo es más alto que las letras: ocupa solo la
// altura de una mayúscula y el resto sobresale, así no separa los renglones.
#let circulo-en-linea(n, radio: 0.6em, ..estilo) = box(
  width: 2 * radio, height: 0.7em,
  place(center + horizon, circulo(n, radio: radio, ..estilo)),
)

// Marca sobre una captura y su referencia en el texto: mismo dibujo, para que
// el lector las asocie de un vistazo. Ámbar porque destaca tanto sobre la
// interfaz oscura como sobre fondos claros.
#let marca-imagen(n) = circulo(n, relleno: ambar, color: tinta, radio: 9pt, borde: 1.5pt + white)
#let marca(n) = circulo-en-linea(n, relleno: ambar, color: tinta, radio: 0.56em)

#let boton(etiqueta) = box(
  fill: fondo-suave, stroke: 0.5pt + rgb("#D1D5DB"), radius: 3pt,
  inset: (x: 0.4em), outset: (y: 0.28em),
  text(weight: "semibold", size: 0.92em, etiqueta),
)

#let tecla(etiqueta) = {
  let c = rgb("#9CA3AF")
  box(
    fill: white, radius: 2.5pt, inset: (x: 0.38em), outset: (y: 0.26em),
    stroke: (left: 0.5pt + c, right: 0.5pt + c, top: 0.5pt + c, bottom: 1.3pt + c),
    text(font: fuente-mono, size: 0.82em, etiqueta),
  )
}

#let menu(..partes) = partes.pos().map(p => text(weight: "semibold", p)).join(text(fill: gris)[ › ])

#let aviso(etiqueta, color, fondo, cuerpo) = block(
  width: 100%, fill: fondo, radius: (right: 3pt),
  stroke: (left: 2.5pt + color),
  inset: (left: 12pt, right: 12pt, top: 9pt, bottom: 10pt),
  above: 1.2em, below: 1.2em,
  // Un aviso partido deja su título solo al pie de una página.
  breakable: false,
  {
    block(below: 0.5em, text(size: 7.5pt, weight: "bold", fill: color, tracking: 0.08em, upper(etiqueta)))
    cuerpo
  },
)

#let nota(cuerpo) = aviso("Nota", acento, rgb("#EFF6FF"), cuerpo)
#let consejo(cuerpo) = aviso("Consejo", rgb("#16A34A"), rgb("#F0FDF4"), cuerpo)
#let importante(cuerpo) = aviso("Importante", rgb("#D97706"), rgb("#FFFBEB"), cuerpo)

// Marcas que calcula el capturador de la demo (docs/manual/demo): por archivo,
// la posición de cada marca y su leyenda, medidas sobre los elementos reales.
#let marcas-capturadas = json("capturas/marcas.json")

// marcas: auto = las de marcas.json, con su leyenda debajo de la imagen;
// o una lista de (x, y) escrita a mano (sin leyenda). leyenda: none la oculta
// cuando el texto del capítulo ya explica cada marca.
// flotante: true (por omisión) deja que la captura vaya arriba o abajo de la
// página donde quepa y que el texto siga llenando: una captura grande que no
// entra en lo que queda de página ya no deja media página en blanco.
#let captura(archivo, marcas: auto, leyenda: auto, ancho: 100%, pie: none, flotante: true) = {
  let medidas = if marcas == auto { marcas-capturadas.at(archivo, default: ()) } else { () }
  let puntos = if marcas == auto { medidas.map(m => (m.x, m.y)) } else { marcas }
  let textos = if leyenda == auto { medidas.map(m => m.etiqueta) } else if leyenda == none { () } else { leyenda }
  figure(
    kind: image,
    caption: pie,
    placement: if flotante { auto } else { none },
    {
      layout(region => {
        let imagen = block(
          clip: true, radius: 5pt, stroke: 0.6pt + rgb("#D1D5DB"),
          image("capturas/" + archivo, width: region.width * (ancho / 100%)),
        )
        let medida = measure(imagen)
        box(width: medida.width, height: medida.height, {
          imagen
          for (i, punto) in puntos.enumerate() {
            let (x, y) = punto
            place(top + left, dx: medida.width * x - 9pt, dy: medida.height * y - 9pt, marca-imagen(i + 1))
          }
        })
      })
      if textos.len() > 0 {
        set align(left)
        set text(size: 8.8pt)
        block(above: 0.8em, width: 100%, grid(
          columns: (1fr, 1fr), column-gutter: 1.2em, row-gutter: 0.55em,
          ..textos.enumerate().map(((i, t)) => grid(
            columns: (auto, 1fr), column-gutter: 0.45em, marca(i + 1), t)),
        ))
      }
    },
  )
}

// Permisos que necesita una función, con los nombres que muestra la página
// de Roles (Permissions.cs). Va justo debajo del título de la sección.
#let requiere(..permisos) = block(above: 0.2em, below: 1em, text(size: 8.5pt, fill: gris)[
  #text(weight: "bold", tracking: 0.06em)[PERMISOS] #h(0.4em)
  #permisos.pos().map(p => text(fill: tinta, p)).join([ · ])
])

#let sin-terminar(que) = panic(
  "El manual tiene " + que + " sin terminar: compílelo sin final=true para ubicarlas.")

// Referencia a otro capítulo o apéndice por su etiqueta:
// #capitulo(<cap-reproduccion>) → "el capítulo 4, Reproducción";
// #capitulo(<ap-servidor>) → "el apéndice B, Puertos y ajustes del servidor".
// Si el destino no está (al compilar un capítulo suelto, o porque la edición
// no trae ese módulo) queda un aviso en vez de romper la compilación.
#let capitulo(etiqueta) = context {
  let destino = query(etiqueta)
  if destino.len() == 0 {
    if final or revisar-referencias {
      panic("Referencia a <" + str(etiqueta) + ">, que no está en esta edición: envuélvala en #modulo(…).")
    }
    text(fill: magenta)[[capítulo #str(etiqueta)]]
  } else {
    let h = destino.first()
    let n = counter(heading).at(h.location()).first()
    let tipo = if h.numbering == "A.1" { "el apéndice" } else { "el capítulo" }
    link(h.location())[#tipo #numbering(h.numbering, n), _#h.body;_]
  }
}

#let captura-pendiente(descripcion, alto: 4.5cm) = {
  if final { sin-terminar("capturas") }
  block(
    width: 100%, height: alto, radius: 5pt, fill: rgb("#FDF4FF"),
    stroke: (paint: magenta, thickness: 0.8pt, dash: "dashed"),
    above: 1.2em, below: 1.2em,
    align(center + horizon, text(size: 9pt, fill: rgb("#A21CAF"))[
      #text(weight: "bold", tracking: 0.06em)[CAPTURA PENDIENTE] \
      #descripcion
    ]),
  )
}

#let pendiente(cuerpo) = {
  if final { sin-terminar("secciones") }
  aviso("Por escribir", magenta, rgb("#FDF4FF"), cuerpo)
}

// ---------------------------------------------------------------------------
// Partes y apéndices (se usan en manual.typ)
// ---------------------------------------------------------------------------

#let parte(titulo, descripcion: none) = {
  pagebreak(weak: true)
  counter("parte").step()
  v(1fr)
  heading(level: 1, numbering: none, supplement: [Parte], titulo)
  if descripcion != none { block(width: 80%, text(size: 11.5pt, fill: gris, descripcion)) }
  v(1.6fr)
  pagebreak()
}

// Todo lo que va dentro se numera A, B, C… como apéndice.
#let apendices(cuerpo) = {
  counter(heading).update(0)
  set heading(numbering: "A.1")
  show heading.where(level: 1): set heading(supplement: [Apéndice])
  cuerpo
}

// ---------------------------------------------------------------------------
// Portada
// ---------------------------------------------------------------------------

#let portada(titulo, subtitulo) = {
  set text(fill: portada-texto)
  // Motivo del logo, grande y tenue, saliendo por la esquina inferior derecha.
  place(bottom + right, dx: 3.5cm, dy: 4.5cm, {
    let c = 7.5cm
    let pieza(a) = rect(width: c, height: c, radius: 1.4cm, fill: acento.transparentize(a))
    grid(columns: (c, c), gutter: 0.75cm, pieza(82%), pieza(90%), pieza(90%), pieza(94%))
  })

  pad(x: 2.3cm, top: 2.4cm, bottom: 2.2cm, {
    grid(
      columns: (auto, auto), column-gutter: 0.45cm, align: horizon,
      logo(tam: 1.25cm),
      text(size: 15pt, weight: "bold")[CLR TrueCentral #text(fill: acento)[VMS]],
    )
    v(1fr)
    text(size: 9pt, weight: "bold", fill: acento, tracking: 0.16em, upper[Documentación del producto])
    v(0.35cm)
    text(size: 38pt, weight: "bold", titulo)
    if subtitulo != none {
      v(0.25cm)
      text(size: 13pt, fill: portada-tenue, subtitulo)
    }
    v(0.7cm)
    line(length: 2.6cm, stroke: 3pt + acento)
    v(1fr)
    set text(size: 9.5pt, fill: portada-tenue)
    [Versión #text(fill: portada-texto, weight: "semibold", version) #h(0.6em) · #h(0.6em) #fecha-larga(datetime.today())]
    if modulos != none {
      linebreak()
      [Edición con los módulos: #modulos.join(", ")]
    }
    v(0.3cm)
    [CLRobotics]
  })
}

// ---------------------------------------------------------------------------
// Documento
// ---------------------------------------------------------------------------

#let manual(titulo: "Manual de usuario", subtitulo: none, cuerpo) = {
  set document(title: "CLR TrueCentral VMS — " + titulo, author: "CLRobotics")
  set text(font: fuente, size: 10.5pt, lang: "es", region: "cl", fill: tinta)
  set par(leading: 0.7em, spacing: 1.1em)
  show raw: set text(font: fuente-mono, size: 0.9em)
  show raw.where(block: true): block.with(width: 100%, fill: fondo-suave, inset: 9pt, radius: 4pt)
  show link: set text(fill: acento)
  set list(marker: text(fill: acento)[•], indent: 0.2em, body-indent: 0.6em)
  set enum(
    numbering: (..n) => circulo-en-linea(n.pos().last()),
    indent: 0pt, body-indent: 0.7em, spacing: 0.95em,
  )

  set table(
    inset: (x: 8pt, y: 6.5pt),
    stroke: (x, y) => (bottom: 0.5pt + linea),
    fill: (x, y) => if y == 0 { fondo-suave },
    align: (x, y) => left + horizon,
  )
  show table.cell.where(y: 0): set text(size: 9pt, weight: "semibold")
  show figure: set block(above: 1.4em, below: 1.4em)
  show figure.caption: set text(size: 8.5pt, fill: gris)

  // Capítulos (nivel 1): página nueva, número grande y regla de acento. Las
  // partes (ver #parte) también son nivel 1, sin número y con su propio dibujo.
  set heading(numbering: "1.1")
  show heading.where(level: 1): set heading(supplement: [Capítulo])
  show heading.where(level: 1): it => {
    if it.supplement == [Parte] {
      block(below: 0.35cm, text(size: 10pt, weight: "bold", fill: acento, tracking: 0.16em,
        upper[Parte #context numbering("I", counter("parte").get().first())]))
      block(below: 0.5cm, text(size: 32pt, weight: "bold", it.body))
      line(length: 2.6cm, stroke: 3pt + acento)
      v(0.5cm)
    } else {
      pagebreak(weak: true)
      v(1.6cm)
      if it.numbering != none {
        block(below: 0.35cm, text(size: 9pt, weight: "bold", fill: acento, tracking: 0.14em,
          upper[#it.supplement #counter(heading).display(it.numbering)]))
      }
      block(below: 0.45cm, text(size: 26pt, weight: "bold", it.body))
      line(length: 2.2cm, stroke: 2.5pt + acento)
      v(0.7cm)
    }
  }
  show heading.where(level: 2): it => block(above: 1.8em, below: 0.85em, sticky: true,
    text(size: 14pt, weight: "semibold")[
      #if it.numbering != none { text(fill: acento, counter(heading).display()) + h(0.5em) }#it.body
    ])
  show heading.where(level: 3): it => block(above: 1.4em, below: 0.7em, sticky: true,
    text(size: 11pt, weight: "semibold", it.body))

  show outline.entry.where(level: 1): it => {
    if it.element.supplement == [Parte] {
      let n = counter("parte").at(it.element.location()).first()
      block(above: 2em, below: 0.2em, link(it.element.location(),
        text(size: 8.5pt, weight: "bold", fill: acento, tracking: 0.12em,
          upper[Parte #numbering("I", n) · #it.element.body])))
    } else {
      set block(above: 1.1em)
      set text(weight: "semibold")
      it
    }
  }

  set page(
    paper: "a4",
    margin: (top: 2.6cm, bottom: 2.4cm, x: 2.3cm),
    header: context {
      // Sin encabezado en la página que abre un capítulo.
      let pagina = here().page()
      let abre = query(heading.where(level: 1)).any(h => h.location().page() == pagina)
      let previos = query(heading.where(level: 1).before(here()))
      if not abre and previos.len() > 0 {
        set text(size: 8pt, fill: gris)
        grid(columns: (1fr, auto), [CLR TrueCentral VMS · #titulo], previos.last().body)
        v(-0.35em)
        line(length: 100%, stroke: 0.4pt + linea)
      }
    },
    footer: context {
      set text(size: 8pt, fill: gris)
      grid(columns: (1fr, auto), [Versión #version], counter(page).display())
    },
  )

  page(margin: 0pt, fill: portada-fondo, header: none, footer: none, portada(titulo, subtitulo))
  counter(page).update(1)

  cuerpo
}
