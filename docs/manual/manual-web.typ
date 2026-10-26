// Manual de usuario de CLR TrueCentral VMS como sitio web: una página por
// capítulo, con los mismos capítulos y la misma edición que el PDF
// (estructura.typ). Compilar con build-manual.ps1 -Web; usa la exportación
// "bundle" de Typst, todavía experimental (--features html,bundle).
//
// Salida: index.html, un .html por capítulo con el nombre de su archivo,
// capturas/, manual.css, manual.js, favicon.svg y, si se pasa --input pdf=…, el botón para
// descargar el PDF (build-manual.ps1 lo copia al lado).
#import "plantilla.typ": *
#import "estructura.typ": *

#let pdf = sys.inputs.at("pdf", default: none)

// Título y etiqueta de cada capítulo, de su línea «= Título <etiqueta>».
#let cabecera(capitulo) = {
  let fuente = read("capitulos/" + capitulo.archivo + ".typ")
  let linea = fuente.match(regex("(?m)^=\\s+(.+?)\\s*<([\\w-]+)>"))
  assert(linea != none, message: capitulo.archivo + ".typ no empieza con «= Título <etiqueta>» (ver README).")
  (titulo: linea.captures.at(0), etiqueta: label(linea.captures.at(1)), fuente: fuente)
}

// Páginas en orden de lectura; parte = índice en partes (none en apéndices).
#let paginas = {
  let lista = ()
  for (i, p) in partes.enumerate() {
    for c in p.capitulos.filter(en-la-edicion) { lista.push((..c, ..cabecera(c), parte: i)) }
  }
  for c in apendices-del-manual { lista.push((..c, ..cabecera(c), parte: none)) }
  lista
}

#let href(pagina) = pagina.archivo + ".html"

// Ancla estable de una sección (#exportar-un-tramo): sirve para enlazar desde
// la aplicación y no cambia al agregar secciones antes.
#let texto-de(c) = {
  if type(c) == str { c }
  else if c.has("text") { texto-de(c.text) }
  else if c.has("children") { c.children.map(texto-de).fold("", (a, b) => a + b) }
  else if c.has("body") { texto-de(c.body) }
  else if c.func() == [ ].func() { " " }
  else { "" }
}
#let ancla(c) = {
  let t = lower(texto-de(c))
  for (con, sin) in ("á": "a", "é": "e", "í": "i", "ó": "o", "ú": "u", "ü": "u", "ñ": "n") {
    t = t.replace(con, sin)
  }
  t.replace(regex("[^a-z0-9]+"), "-").trim("-")
}

#let numero(h) = numbering(h.numbering, ..counter(heading).at(h.location()))

// Secciones (nivel 2) del capítulo que abre con el título h.
#let secciones(h) = {
  let siguiente = query(selector(heading.where(level: 1)).after(h.location(), inclusive: false))
  let sel = selector(heading.where(level: 2)).after(h.location())
  if siguiente.len() > 0 { sel = sel.before(siguiente.first().location(), inclusive: false) }
  query(sel)
}

// ---------------------------------------------------------------------------
// Partes de cada página
// ---------------------------------------------------------------------------

#let barra = elem("header", clase: "barra", {
  html.elem("button", attrs: (class: "abrir-menu", type: "button", "aria-label": "Índice", "aria-controls": "lateral"),
    elem("span", clase: "hamburguesa"))
  html.elem("a", attrs: (class: "marca-sitio", href: "index.html"), {
    html.frame(logo(tam: 24pt))
    elem("span", clase: "producto")[CLR TrueCentral #elem("b", "VMS")]
    elem("span", clase: "obra", titulo)
  })
  elem("div", clase: "buscador", {
    html.elem("input", attrs: (type: "search", id: "buscar", placeholder: "Buscar en el manual", autocomplete: "off",
      "aria-label": "Buscar en el manual"))
    html.elem("div", attrs: (id: "resultados", class: "resultados", hidden: ""))
  })
  elem("span", clase: "version", "Versión " + version)
  if pdf != none { html.elem("a", attrs: (class: "btn", href: pdf, download: ""), "Descargar PDF") }
})

#let lateral(actual) = html.elem("nav", attrs: (id: "lateral", class: "lateral", "aria-label": "Índice"), context {
  let entrada(p) = {
    let h = query(p.etiqueta).first()
    let es-actual = p.archivo == actual
    html.elem("li", attrs: if es-actual { (class: "actual") } else { (:) }, {
      html.elem("a", attrs: (href: href(p), class: "capitulo"),
        elem("span", clase: "num", numero(h)) + elem("span", h.body))
      if es-actual {
        elem("ol", clase: "nav-secciones", secciones(h).map(s => html.elem("li",
          html.elem("a", attrs: (href: "#" + ancla(s.body)), s.body))).join())
      }
    })
  }
  for (i, p) in partes.enumerate() {
    let suyas = paginas.filter(x => x.parte == i)
    if suyas.len() > 0 {
      elem("p", clase: "nav-parte", "Parte " + numbering("I", i + 1) + " · " + p.titulo)
      html.elem("ol", suyas.map(entrada).join())
    }
  }
  elem("p", clase: "nav-parte", "Apéndices")
  html.elem("ol", paginas.filter(x => x.parte == none).map(entrada).join())
})

#let paginacion(i) = elem("nav", clase: "paginacion", {
  let enlace(p, clase, rotulo) = html.elem("a", attrs: (class: clase, href: href(p)),
    elem("span", clase: "dir", rotulo) + elem("span", clase: "tit", p.titulo))
  if i > 0 { enlace(paginas.at(i - 1), "anterior", "Anterior") } else { elem("span") }
  if i + 1 < paginas.len() { enlace(paginas.at(i + 1), "siguiente", "Siguiente") }
})

#let pie = elem("footer", clase: "pie")[
  CLR TrueCentral VMS · #titulo · Versión #version · CLRobotics
]

#let pagina(nombre, actual: none, cuerpo) = html.elem("html", attrs: (lang: "es-CL"), {
  html.elem("head", {
    html.elem("meta", attrs: (charset: "utf-8"))
    html.elem("meta", attrs: (name: "viewport", content: "width=device-width, initial-scale=1"))
    html.elem("meta", attrs: (name: "color-scheme", content: "light dark"))
    html.elem("title", nombre)
    html.elem("link", attrs: (rel: "icon", href: "favicon.svg", type: "image/svg+xml"))
    html.elem("link", attrs: (rel: "stylesheet", href: "manual.css"))
    html.elem("script", attrs: (src: "manual.js", defer: ""))
  })
  html.elem("body", {
    barra
    elem("div", clase: "marco", {
      lateral(actual)
      html.elem("main", attrs: (id: "contenido", class: "contenido"), cuerpo + pie)
    })
  })
})

// ---------------------------------------------------------------------------
// Reglas de la salida web
// ---------------------------------------------------------------------------

#set text(lang: "es", region: "cl")
#set heading(numbering: "1.1")
#show heading.where(level: 1): set heading(supplement: [Capítulo])
#show heading.where(level: 1): it => elem("header", clase: "cabecera-capitulo", {
  if it.numbering != none {
    elem("p", clase: "rotulo")[#it.supplement #counter(heading).display(it.numbering)]
  }
  html.elem("h1", it.body)
})
#show heading.where(level: 2): it => html.elem("h2", attrs: (id: ancla(it.body)), {
  if it.numbering != none { elem("span", clase: "num", counter(heading).display()) }
  it.body
})
#show heading.where(level: 3): it => html.elem("h3", it.body)

// ---------------------------------------------------------------------------
// Sitio
// ---------------------------------------------------------------------------

#document("index.html", title: titulo + " · CLR TrueCentral VMS", pagina(titulo + " · CLR TrueCentral VMS", {
  elem("section", clase: "portada", {
    elem("p", clase: "sobretitulo", "Documentación del producto")
    html.elem("h1", titulo)
    elem("p", clase: "subtitulo", subtitulo)
    elem("p", clase: "datos")[
      Versión #version · #fecha-larga(datetime.today())
      #if modulos != none [ · Edición con los módulos: #modulos.join(", ")]
    ]
    elem("div", clase: "acciones", {
      html.elem("a", attrs: (class: "btn primario", href: href(paginas.first())), "Empezar a leer")
      if pdf != none { html.elem("a", attrs: (class: "btn", href: pdf, download: ""), "Descargar PDF") }
    })
  })
  context {
    let tarjeta(p) = {
      let h = query(p.etiqueta).first()
      let n = secciones(h).len()
      html.elem("a", attrs: (class: "tarjeta", href: href(p)), {
        elem("span", clase: "num", numero(h))
        elem("span", clase: "tit", h.body)
        elem("span", clase: "cuenta", str(n) + if n == 1 { " sección" } else { " secciones" })
      })
    }
    for (i, p) in partes.enumerate() {
      let suyas = paginas.filter(x => x.parte == i)
      if suyas.len() > 0 {
        elem("section", clase: "parte", {
          elem("p", clase: "rotulo", "Parte " + numbering("I", i + 1))
          html.elem("h2", p.titulo)
          elem("p", clase: "descripcion", p.descripcion)
          elem("div", clase: "tarjetas", suyas.map(tarjeta).join())
        })
      }
    }
    elem("section", clase: "parte", {
      elem("p", clase: "rotulo", "Referencia")
      html.elem("h2", "Apéndices")
      elem("div", clase: "tarjetas", paginas.filter(x => x.parte == none).map(tarjeta).join())
    })
  }
}))

#for (i, p) in paginas.enumerate() {
  let cuerpo = contenido-de(p) + paginacion(i)
  // Los apéndices se numeran A, B…; #apendices vuelve el contador a cero, así
  // que va solo en el primero y los demás llevan su numeración a mano.
  if p.parte == none {
    cuerpo = if paginas.at(i - 1).parte != none { apendices(cuerpo) } else {
      set heading(numbering: "A.1")
      show heading.where(level: 1): set heading(supplement: [Apéndice])
      cuerpo
    }
  }
  document(href(p), title: p.titulo + " · " + titulo,
    pagina(p.titulo + " · " + titulo + " · CLR TrueCentral VMS", actual: p.archivo, cuerpo))
}

// Capturas que usan los capítulos de esta edición, cada una una sola vez.
#let capturas = (
  paginas
    .map(p => p.fuente.matches(regex("#captura\\(\\s*\"([^\"]+)\"")).map(m => m.captures.first()))
    .flatten()
    .dedup()
)
#for archivo in capturas { asset("capturas/" + archivo, read("capturas/" + archivo, encoding: none)) }

#asset("manual.css", read("web/manual.css", encoding: none))
#asset("manual.js", read("web/manual.js", encoding: none))
#asset("favicon.svg", read("web/favicon.svg", encoding: none))
