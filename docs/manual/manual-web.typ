// Manual de usuario de CLR TrueCentral VMS como sitio web: una página por
// capítulo, con los mismos capítulos y la misma edición que el PDF
// (estructura.typ). Compilar con build-manual.ps1 -Web; usa la exportación
// "bundle" de Typst, todavía experimental (--features html,bundle).
//
// El tema es el del panel web: el sitio lleva su misma hoja de estilos, sus
// íconos, su logo y su favicon, leídos de wwwroot al compilar, y arma la barra
// superior, el menú lateral y el título de página con las clases del panel
// (index.html del servidor). Por eso se compila con la raíz del repositorio
// como --root (build-manual.ps1 lo pone). web/manual.css agrega solo lo propio
// del manual.
//
// Salida: index.html, un .html por capítulo con el nombre de su archivo,
// capturas/, los archivos del panel y, si se pasa --input pdf=…, el botón
// para descargar el PDF (build-manual.ps1 lo copia al lado).
#import "plantilla.typ": *
#import "estructura.typ": *

#let pdf = sys.inputs.at("pdf", default: none)
#let wwwroot = "/src/TrueCentralVms.Server/wwwroot/"

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
// «Capítulo 4» o «Apéndice B».
#let rotulo-de(h) = [#h.supplement #numero(h)]
#let parte-de(p) = if p.parte == none { "Apéndices" } else {
  "Parte " + numbering("I", p.parte + 1) + " · " + partes.at(p.parte).titulo
}

// Secciones (nivel 2) del capítulo que abre con el título h.
#let secciones(h) = {
  let siguiente = query(selector(heading.where(level: 1)).after(h.location(), inclusive: false))
  let sel = selector(heading.where(level: 2)).after(h.location())
  if siguiente.len() > 0 { sel = sel.before(siguiente.first().location(), inclusive: false) }
  query(sel)
}

#let mdi(nombre) = html.elem("i", attrs: (class: "mdi mdi-" + nombre, "aria-hidden": "true"))

// ---------------------------------------------------------------------------
// Armazón: el del panel web (barra superior, menú lateral, título de página)
// ---------------------------------------------------------------------------

#let barra = html.elem("header", attrs: (class: "topbar"), {
  html.elem("a", attrs: (href: "index.html", class: "topbar-brand", title: titulo), {
    elem("span", clase: "brand-mark logo", html.elem("img", attrs: (src: "logo.png", alt: "")))
    elem("span", clase: "brand-text")[TrueCentral #elem("span", clase: "accent", "VMS")]
  })
  elem("div", clase: "topbar-left", {
    html.elem("button", attrs: (type: "button", class: "topbar-icon", id: "btn-menu",
      title: "Mostrar u ocultar el menú", "aria-label": "Menú", "aria-expanded": "false"), mdi("menu"))
    elem("div", clase: "buscador", {
      mdi("magnify")
      html.elem("input", attrs: (type: "search", id: "buscar", placeholder: "Buscar en el manual",
        autocomplete: "off", "aria-label": "Buscar en el manual"))
      html.elem("div", attrs: (id: "resultados", class: "resultados", hidden: ""))
    })
  })
})

// Acción de todas las páginas, a la altura del título como en el panel.
#let boton-pdf = if pdf != none {
  html.elem("a", attrs: (class: "btn ghost", href: pdf, download: ""),
    mdi("file-pdf") + elem("span", clase: "etiqueta", "Descargar PDF"))
}

#let lateral(actual) = html.elem("aside", attrs: (class: "sidebar"), {
  html.elem("nav", attrs: (id: "nav", "aria-label": "Índice del manual"), context {
    let entrada(p) = {
      let h = query(p.etiqueta).first()
      let es-actual = p.archivo == actual
      html.elem("a", attrs: (href: href(p), class: if es-actual { "capitulo active" } else { "capitulo" }), {
        mdi(p.icono)
        elem("span", clase: "nav-text", h.body)
        elem("span", clase: "nav-num", numero(h))
      })
      // Del capítulo abierto, sus secciones: el script marca la que se lee.
      if es-actual {
        elem("div", clase: "nav-children", secciones(h).map(s =>
          html.elem("a", attrs: (href: "#" + ancla(s.body)), s.body)).join())
      }
    }
    html.elem("a", attrs: (href: "index.html", ..if actual == none { (class: "active") } else { (:) }),
      mdi("book-open-variant") + elem("span", clase: "nav-text", titulo))
    for (i, p) in partes.enumerate() {
      let suyas = paginas.filter(x => x.parte == i)
      if suyas.len() > 0 {
        elem("div", clase: "nav-divider", elem("span", clase: "nav-text", p.titulo))
        suyas.map(entrada).join()
      }
    }
    elem("div", clase: "nav-divider", elem("span", clase: "nav-text", "Apéndices"))
    paginas.filter(x => x.parte == none).map(entrada).join()
  })
  elem("div", clase: "sidebar-footer", mdi("book-open-page-variant") + elem("span", clase: "nav-text", "Versión " + version))
})

// Migas arriba y título debajo, como las páginas del panel.
#let titulos(migas, nombre, acciones: none) = elem("div", clase: "page-titles", {
  elem("div", clase: "page-titles-text", {
    html.elem("h2", nombre)
    html.elem("ol", attrs: (class: "breadcrumb", "aria-label": "Ubicación en el manual"),
      migas.map(m => html.elem("li", m)).join())
  })
  if acciones != none { elem("div", clase: "page-actions", acciones) }
})

#let paginacion(i) = elem("nav", clase: "paginacion", {
  let enlace(p, clase, rotulo, icono) = html.elem("a", attrs: (class: clase, href: href(p)), {
    elem("span", clase: "dir", if clase == "anterior" { mdi(icono) + rotulo } else { rotulo + mdi(icono) })
    elem("span", clase: "tit", p.titulo)
  })
  if i > 0 { enlace(paginas.at(i - 1), "anterior", "Anterior", "chevron-left") }
  if i + 1 < paginas.len() { enlace(paginas.at(i + 1), "siguiente", "Siguiente", "chevron-right") }
})

#let pie = elem("footer", clase: "pie")[
  CLR TrueCentral VMS · #titulo · Versión #version · CLRobotics
]

#let pagina(nombre, actual: none, cabeza, cuerpo) = html.elem("html", attrs: (lang: "es-CL"), {
  html.elem("head", {
    html.elem("meta", attrs: (charset: "utf-8"))
    html.elem("meta", attrs: (name: "viewport", content: "width=device-width, initial-scale=1"))
    html.elem("title", nombre)
    html.elem("link", attrs: (rel: "icon", href: "favicon.ico"))
    html.elem("link", attrs: (rel: "stylesheet", href: "vendor/mdi/css/materialdesignicons.min.css"))
    html.elem("link", attrs: (rel: "stylesheet", href: "panel.css"))
    html.elem("link", attrs: (rel: "stylesheet", href: "manual.css"))
    html.elem("script", attrs: (src: "manual.js", defer: ""))
  })
  html.elem("body", html.elem("div", attrs: (id: "app-shell", class: "shell"), {
    // Menú contraído a íconos: la misma preferencia que el panel, aplicada
    // antes de pintar.
    html.elem("script", "try { if (localStorage.getItem(\"tcvms.sidebar.mini\") === \"1\") document.currentScript.parentElement.classList.add(\"sidebar-mini\") } catch (e) {}")
    barra
    lateral(actual)
    elem("div", clase: "main", {
      cabeza
      html.elem("main", attrs: (class: "contenido"), cuerpo + pie)
    })
  }))
})

// ---------------------------------------------------------------------------
// Reglas de la salida web
// ---------------------------------------------------------------------------

#set text(lang: "es", region: "cl")
#set heading(numbering: "1.1")
#show heading.where(level: 1): set heading(supplement: [Capítulo])
// El título del capítulo va arriba, en el encabezado de la página; aquí queda
// solo su ancla, a la que llegan los enlaces de los otros capítulos.
#show heading.where(level: 1): it => elem("div", clase: "ancla-capitulo")
#show heading.where(level: 2): it => html.elem("h2", attrs: (id: ancla(it.body)), {
  if it.numbering != none { elem("span", clase: "num", counter(heading).display()) }
  it.body
})
#show heading.where(level: 3): it => html.elem("h3", it.body)
// En pantallas angostas la tabla se desplaza dentro de su marco.
#show table: it => elem("div", clase: "tabla", it)

// ---------------------------------------------------------------------------
// Sitio
// ---------------------------------------------------------------------------

#document("index.html", title: titulo + " · CLR TrueCentral VMS", pagina(
  titulo + " · CLR TrueCentral VMS",
  titulos(("Documentación del producto",), titulo, acciones: {
    boton-pdf
    html.elem("a", attrs: (class: "btn", href: href(paginas.first())), "Empezar a leer" + mdi("arrow-right"))
  }),
  {
    elem("div", clase: "card resumen", {
      elem("p", clase: "resumen-sub", subtitulo)
      elem("p", clase: "muted")[
        Versión #version · #fecha-larga(datetime.today())
        #if modulos != none [ · Edición con los módulos: #modulos.join(", ")]
      ]
    })
    context {
      let tarjeta(p) = {
        let h = query(p.etiqueta).first()
        let n = secciones(h).len()
        html.elem("a", attrs: (class: "card tarjeta", href: href(p)), {
          elem("span", clase: "tarjeta-icono", mdi(p.icono))
          elem("span", clase: "tarjeta-texto", {
            elem("span", clase: "card-label", rotulo-de(h))
            elem("span", clase: "tarjeta-titulo", h.body)
            elem("span", clase: "tarjeta-cuenta", str(n) + if n == 1 { " sección" } else { " secciones" })
          })
        })
      }
      let grupo(rotulo, nombre, descripcion, suyas) = elem("section", clase: "parte", {
        elem("div", clase: "parte-titulo", {
          html.elem("h3", elem("span", clase: "card-label", rotulo) + nombre)
          if descripcion != none { elem("p", clase: "muted", descripcion) }
        })
        elem("div", clase: "cards", suyas.map(tarjeta).join())
      })
      for (i, p) in partes.enumerate() {
        let suyas = paginas.filter(x => x.parte == i)
        if suyas.len() > 0 { grupo("Parte " + numbering("I", i + 1), p.titulo, p.descripcion, suyas) }
      }
      grupo("Referencia", "Apéndices", none, paginas.filter(x => x.parte == none))
    }
  },
))

#for (i, p) in paginas.enumerate() {
  let cuerpo = elem("article", clase: "articulo", contenido-de(p)) + paginacion(i)
  let cabeza = context {
    let h = query(p.etiqueta).first()
    titulos((html.elem("a", attrs: (href: "index.html"), titulo), parte-de(p), rotulo-de(h)), h.body, acciones: boton-pdf)
  }
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
    pagina(p.titulo + " · " + titulo + " · CLR TrueCentral VMS", actual: p.archivo, cabeza, cuerpo))
}

// Capturas que usan los capítulos de esta edición, cada una una sola vez.
#let capturas = (
  paginas
    .map(p => p.fuente.matches(regex("#captura\\(\\s*\"([^\"]+)\"")).map(m => m.captures.first()))
    .flatten()
    .dedup()
)
#for archivo in capturas { asset("capturas/" + archivo, read("capturas/" + archivo, encoding: none)) }

// Archivos del panel web, con las mismas rutas que en wwwroot.
#for archivo in (
  "favicon.ico",
  "logo.png",
  "vendor/mdi/LICENSE.txt",
  "vendor/mdi/css/materialdesignicons.min.css",
  "vendor/mdi/fonts/materialdesignicons-webfont.woff2",
  "vendor/mdi/fonts/materialdesignicons-webfont.woff",
) { asset(archivo, read(wwwroot + archivo, encoding: none)) }
#asset("panel.css", read(wwwroot + "styles.css", encoding: none))

#asset("manual.css", read("web/manual.css", encoding: none))
#asset("manual.js", read("web/manual.js", encoding: none))
