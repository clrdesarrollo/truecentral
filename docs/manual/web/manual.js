// Manual de usuario de CLR TrueCentral VMS: salida web (manual-web.typ).
// Índice lateral en pantallas angostas, sección visible en el índice y
// búsqueda en todo el manual. La búsqueda lee las páginas del capítulo la
// primera vez que se usa: necesita el sitio servido por HTTP (servidor, typst
// watch); abierto como archivo busca solo en los títulos de los capítulos.
(() => {
  const cuerpo = document.body;

  // ---------- Índice lateral ----------
  document.querySelector('.abrir-menu')?.addEventListener('click', () => cuerpo.classList.toggle('menu-abierto'));
  document.querySelector('.lateral')?.addEventListener('click', e => {
    if (e.target.closest('a')) cuerpo.classList.remove('menu-abierto');
  });
  document.querySelector('.lateral li.actual')?.scrollIntoView({ block: 'center' });

  // Marca en el índice la sección que se está leyendo.
  const enlaces = new Map([...document.querySelectorAll('.nav-secciones a')].map(a => [decodeURIComponent(a.hash.slice(1)), a]));
  const titulos = [...document.querySelectorAll('.contenido h2[id]')];
  if (enlaces.size && titulos.length) {
    let previo = null;
    const marcar = () => {
      let visible = null;
      for (const h of titulos) { if (h.getBoundingClientRect().top < 150) visible = h; else break; }
      const enlace = visible && enlaces.get(visible.id);
      if (enlace === previo) return;
      previo?.classList.remove('visible');
      enlace?.classList.add('visible');
      previo = enlace;
    };
    addEventListener('scroll', marcar, { passive: true });
    marcar();
  }

  // ---------- Búsqueda ----------
  const entrada = document.getElementById('buscar');
  const caja = document.getElementById('resultados');
  if (!entrada || !caja) return;

  // Sin tildes ni mayúsculas, con el mismo largo que el texto original (las
  // posiciones sirven para resaltar sobre el original).
  const normal = s => s.normalize('NFC').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
  const escapar = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

  // Texto de un elemento con un espacio entre bloques (textContent pega el
  // final de un párrafo con el comienzo del siguiente). Sin las marcas de las
  // capturas ni las capturas pendientes.
  const bloque = /^(P|LI|TD|TH|TR|DIV|ASIDE|FIGCAPTION|FIGURE|H3|H4|PRE|OL|UL|TABLE)$/;
  function texto(n) {
    if (n.nodeType === Node.TEXT_NODE) return n.data;
    if (n.nodeType !== Node.ELEMENT_NODE || n.matches('.captura, .captura-pendiente')) return '';
    let s = '';
    for (const hijo of n.childNodes) s += texto(hijo);
    return bloque.test(n.tagName) || n.matches('.rotulo, .permiso') ? ` ${s} ` : s;
  }

  function secciones(href, doc) {
    const main = doc.querySelector('.contenido');
    if (!main) return [];
    const capitulo = main.querySelector('.cabecera-capitulo h1')?.textContent ?? '';
    const rotulo = main.querySelector('.cabecera-capitulo .rotulo')?.textContent ?? '';
    const lista = [];
    let actual = { href, titulo: capitulo, donde: rotulo, texto: '' };
    for (const el of main.children) {
      if (el.matches('h2[id]')) {
        lista.push(actual);
        const num = el.querySelector('.num')?.textContent ?? '';
        actual = { href: `${href}#${el.id}`, titulo: `${num} ${el.textContent.slice(num.length)}`.trim(), donde: `${rotulo} · ${capitulo}`, texto: '' };
      } else if (!el.matches('.cabecera-capitulo, .paginacion, .pie')) {
        actual.texto += ' ' + texto(el);
      }
    }
    lista.push(actual);
    return lista;
  }

  let indice = null;
  async function cargar() {
    if (indice) return indice;
    const capitulos = [...document.querySelectorAll('.lateral a.capitulo')];
    const lector = new DOMParser();
    const paginas = await Promise.all([...new Set(capitulos.map(a => a.getAttribute('href')))].map(async href => {
      try {
        const html = await (await fetch(href)).text();
        return secciones(href, lector.parseFromString(html, 'text/html'));
      } catch { return []; }
    }));
    let lista = paginas.flat();
    // Abierto como archivo el navegador no deja leer las otras páginas.
    if (!lista.length) lista = capitulos.map(a => ({ href: a.getAttribute('href'), titulo: a.textContent, donde: 'Capítulo', texto: '' }));
    indice = lista.map(s => {
      const texto = s.texto.replace(/\s+/g, ' ').trim();
      return { ...s, texto, nTitulo: normal(s.titulo), nTexto: normal(texto) };
    });
    return indice;
  }

  function extracto(s, terminos) {
    let desde = s.nTexto.indexOf(terminos[0]);
    if (desde < 0) return escapar(s.texto.slice(0, 140)) + (s.texto.length > 140 ? '…' : '');
    const ini = Math.max(0, desde - 50);
    const fin = Math.min(s.texto.length, desde + 110);
    const original = s.texto.slice(ini, fin);
    const normalizado = s.nTexto.slice(ini, fin);
    const marcas = new Array(original.length).fill(false);
    for (const t of terminos) {
      for (let i = normalizado.indexOf(t); i >= 0; i = normalizado.indexOf(t, i + t.length)) marcas.fill(true, i, i + t.length);
    }
    let html = '', abierta = false;
    for (let i = 0; i < original.length; i++) {
      if (marcas[i] !== abierta) { html += marcas[i] ? '<mark>' : '</mark>'; abierta = marcas[i]; }
      html += escapar(original[i]);
    }
    if (abierta) html += '</mark>';
    return (ini > 0 ? '…' : '') + html + (fin < s.texto.length ? '…' : '');
  }

  let activo = -1;
  async function buscar() {
    const consulta = normal(entrada.value.trim());
    if (consulta.length < 2) { caja.hidden = true; return; }
    const terminos = consulta.split(/\s+/);
    const lista = (await cargar())
      .map(s => {
        if (!terminos.every(t => s.nTitulo.includes(t) || s.nTexto.includes(t))) return null;
        const puntos = terminos.reduce((p, t) => p + (s.nTitulo.includes(t) ? 10 : 0), 0) + (s.nTexto.includes(consulta) ? 3 : 0);
        return { s, puntos };
      })
      .filter(Boolean)
      .sort((a, b) => b.puntos - a.puntos)
      .slice(0, 30);
    if (normal(entrada.value.trim()) !== consulta) return;
    activo = -1;
    caja.innerHTML = lista.length
      ? lista.map(({ s }) => `<a href="${escapar(s.href)}"><span class="donde">${escapar(s.donde)}</span>`
        + `<span class="que">${escapar(s.titulo)}</span><span class="extracto">${extracto(s, terminos)}</span></a>`).join('')
      : '<div class="vacio">Sin resultados.</div>';
    caja.hidden = false;
  }

  function mover(paso) {
    const opciones = [...caja.querySelectorAll('a')];
    if (!opciones.length) return;
    opciones[activo]?.classList.remove('activo');
    activo = (activo + paso + opciones.length) % opciones.length;
    opciones[activo].classList.add('activo');
    opciones[activo].scrollIntoView({ block: 'nearest' });
  }

  entrada.addEventListener('input', buscar);
  entrada.addEventListener('focus', () => { cargar(); if (entrada.value.trim().length >= 2) buscar(); });
  entrada.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); mover(1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); mover(-1); }
    else if (e.key === 'Enter') { (caja.querySelectorAll('a')[Math.max(activo, 0)])?.click(); }
    else if (e.key === 'Escape') { caja.hidden = true; entrada.blur(); }
  });
  caja.addEventListener('click', e => { if (e.target.closest('a')) caja.hidden = true; });
  document.addEventListener('click', e => { if (!e.target.closest('.buscador')) caja.hidden = true; });
  // «/» o Ctrl+K llevan a la búsqueda desde cualquier parte de la página.
  document.addEventListener('keydown', e => {
    const escribiendo = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName ?? '');
    if ((e.key === '/' && !escribiendo) || (e.key.toLowerCase() === 'k' && (e.ctrlKey || e.metaKey))) {
      e.preventDefault();
      entrada.focus();
      entrada.select();
    } else if (e.key === 'Escape') {
      cuerpo.classList.remove('menu-abierto');
    }
  });
})();
