// Capturas del panel web para el manual, contra el entorno de demostración.
//
// Abre Edge sin ventana con su propio perfil, entra con el administrador de la
// demo y, por cada captura de capturas-web/*.mjs: navega, prepara la pantalla,
// recorta y guarda el PNG en docs/manual/capturas. La posición de cada marca se
// calcula desde el elemento que señala y se guarda en capturas/marcas.json con
// su leyenda, así que al recapturar las marcas siguen a la interfaz.
//
// Uso:  node capturar-web.mjs [filtro…] [--puerto=9333] [--listar]
//       (filtro = parte del nombre de archivo; sin filtro, todas)

import { spawn } from "node:child_process";
import { mkdirSync, readFileSync, readdirSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const aqui = dirname(fileURLToPath(import.meta.url));
const config = JSON.parse(readFileSync(join(aqui, "demo.json"), "utf8"));
const base = `http://127.0.0.1:${config.puertos.web}`;
const salida = join(aqui, "..", "capturas");
const datos = config.carpetaDatos.replace(/%([^%]+)%/g, (_, v) => process.env[v] ?? "");

const argumentos = process.argv.slice(2);
const opcion = (nombre, porDefecto) =>
  argumentos.find(a => a.startsWith(`--${nombre}=`))?.split("=")[1] ?? porDefecto;
const filtros = argumentos.filter(a => !a.startsWith("--"));
const puertoDepuracion = Number(opcion("puerto", "9333"));

const ANCHO = 1280, ALTO = 800, ESCALA = 2;

// ---------------------------------------------------------------------------
// Protocolo de depuración (CDP) sobre WebSocket, sin dependencias
// ---------------------------------------------------------------------------

class Conexion {
  constructor(url) {
    this.ws = new WebSocket(url);
    this.siguiente = 1;
    this.pendientes = new Map();
    this.oyentes = [];
    this.ws.onmessage = ev => {
      const m = JSON.parse(ev.data);
      if (m.id && this.pendientes.has(m.id)) {
        const { ok, mal } = this.pendientes.get(m.id);
        this.pendientes.delete(m.id);
        m.error ? mal(new Error(`${m.error.message} ${m.error.data ?? ""}`)) : ok(m.result);
      } else if (m.method) {
        this.oyentes.forEach(f => f(m));
      }
    };
  }
  abierta() {
    return new Promise((ok, mal) => { this.ws.onopen = ok; this.ws.onerror = mal; });
  }
  enviar(method, params = {}) {
    const id = this.siguiente++;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((ok, mal) => this.pendientes.set(id, { ok, mal }));
  }
  cerrar() { this.ws.close(); }
}

const pausa = ms => new Promise(r => setTimeout(r, ms));

/** Lo que usan las especificaciones para preparar cada pantalla. */
class Pagina {
  constructor(cdp) { this.cdp = cdp; }

  async evaluar(expresion, ...args) {
    const codigo = typeof expresion === "function"
      ? `(${expresion})(...${JSON.stringify(args)})` : expresion;
    const r = await this.cdp.enviar("Runtime.evaluate", { expression: codigo, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
    return r.result.value;
  }

  /** Imprime en la consola lo que devuelve una expresión de la página (para armar capturas). */
  async log(expresion, ...args) { console.log(JSON.stringify(await this.evaluar(expresion, ...args), null, 1)); }

  /** Llama a la API REST con la sesión del panel (por ejemplo, para revisar o preparar datos). */
  async api(metodo, ruta, cuerpo) {
    return this.evaluar(async (m, r, c) => {
      const res = await fetch(r, {
        method: m,
        headers: { Authorization: `Bearer ${localStorage.getItem("tcvms_token")}`, ...(c ? { "Content-Type": "application/json" } : {}) },
        body: c ? JSON.stringify(c) : undefined,
      });
      const texto = await res.text();
      return { estado: res.status, cuerpo: texto ? JSON.parse(texto) : null };
    }, metodo, ruta, cuerpo ?? null);
  }

  /** Navega a una ruta del panel ("#/devices") y espera a que el contenido cargue. */
  async ir(ruta, esperarSelector = "main, #app, body") {
    await this.evaluar(r => { location.hash = r; }, ruta.replace(/^#/, ""));
    await pausa(400);
    await this.esperar(esperarSelector);
    await this.reposo();
  }

  async esperar(selector, { ms = 15000, visible = true } = {}) {
    const fin = Date.now() + ms;
    while (Date.now() < fin) {
      const ok = await this.evaluar((s, v) => {
        // getBoundingClientRect y no offsetWidth: los elementos SVG no tienen offsetWidth.
        const visible = e => { const b = e.getBoundingClientRect(); return b.width > 0 && b.height > 0; };
        const el = [...document.querySelectorAll(s)].find(e => !v || visible(e));
        return !!el;
      }, selector, visible);
      if (ok) return;
      await pausa(150);
    }
    throw new Error(`No apareció «${selector}»`);
  }

  /** Espera a que el texto aparezca en algún elemento que coincida con el selector. */
  async esperarTexto(selector, texto, { ms = 15000 } = {}) {
    const fin = Date.now() + ms;
    while (Date.now() < fin) {
      if (await this.evaluar((s, t) => [...document.querySelectorAll(s)].some(e => e.textContent.includes(t)), selector, texto)) return;
      await pausa(150);
    }
    throw new Error(`No apareció «${texto}» en «${selector}»`);
  }

  /** Espera a que no haya pedidos de red en curso por un momento. */
  async reposo(ms = 600) { await pausa(ms); }

  /** Clic real del mouse en el centro del elemento (abre menús que escuchan mousedown). */
  async clic(selector, { texto } = {}) {
    const r = await this.rect(selector, { texto, desplazar: true });
    const x = r.x + r.width / 2, y = r.y + r.height / 2;
    for (const type of ["mouseMoved", "mousePressed", "mouseReleased"])
      await this.cdp.enviar("Input.dispatchMouseEvent", { type, x, y, button: "left", clickCount: 1 });
    await pausa(250);
  }

  async doble(selector, opciones) {
    const r = await this.rect(selector, { ...opciones, desplazar: true });
    const x = r.x + r.width / 2, y = r.y + r.height / 2;
    for (const clickCount of [1, 2])
      for (const type of ["mousePressed", "mouseReleased"])
        await this.cdp.enviar("Input.dispatchMouseEvent", { type, x, y, button: "left", clickCount });
    await pausa(300);
  }

  async pasar(selector, opciones) {
    const r = await this.rect(selector, { ...opciones, desplazar: true });
    await this.cdp.enviar("Input.dispatchMouseEvent", { type: "mouseMoved", x: r.x + r.width / 2, y: r.y + r.height / 2 });
    await pausa(250);
  }

  /** Escribe en un campo como lo haría una persona (dispara input/change). */
  async escribir(selector, valor) {
    await this.evaluar((s, v) => {
      const el = document.querySelector(s);
      if (!el) throw new Error(`No existe ${s}`);
      el.focus();
      const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
        : el instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, "value").set.call(el, v);
      el.dispatchEvent(new Event("input", { bubbles: true }));
      el.dispatchEvent(new Event("change", { bubbles: true }));
    }, selector, valor);
    await pausa(200);
  }

  async tecla(key) {
    for (const type of ["keyDown", "keyUp"])
      await this.cdp.enviar("Input.dispatchKeyEvent", { type, key, code: key, windowsVirtualKeyCode: key === "Escape" ? 27 : key === "Enter" ? 13 : 0 });
    await pausa(200);
  }

  /** Rectángulo (en px CSS de la ventana) del primer elemento visible que coincide.
   *  contenido: true = el del texto del elemento, no el de su caja (para marcar
   *  junto a un texto centrado en un bloque ancho). desplazar: true lo trae a la
   *  vista antes de medir; solo para hacerle clic: al medir marcas, desplazar
   *  movería lo ya medido y las marcas quedarían corridas. */
  async rect(selector, { texto, contenido, desplazar } = {}) {
    const r = await this.evaluar((s, t, c, d) => {
      const visible = e => { const b = e.getBoundingClientRect(); return b.width > 0 && b.height > 0; };
      const el = [...document.querySelectorAll(s)].find(e => visible(e) && (!t || e.textContent.trim().includes(t)));
      if (!el) return null;
      if (d) el.scrollIntoView({ block: "nearest", inline: "nearest" });
      let b = el.getBoundingClientRect();
      if (c) { const rango = document.createRange(); rango.selectNodeContents(el); b = rango.getBoundingClientRect(); }
      return { x: b.x, y: b.y, width: b.width, height: b.height };
    }, selector, texto ?? null, !!contenido, !!desplazar);
    if (!r) throw new Error(`No hay un elemento visible «${selector}»${texto ? ` con «${texto}»` : ""}`);
    return r;
  }
}

// ---------------------------------------------------------------------------
// Navegador
// ---------------------------------------------------------------------------

function buscarEdge() {
  for (const ruta of [
    `${process.env["ProgramFiles(x86)"]}\\Microsoft\\Edge\\Application\\msedge.exe`,
    `${process.env.ProgramFiles}\\Microsoft\\Edge\\Application\\msedge.exe`,
    `${process.env.ProgramFiles}\\Google\\Chrome\\Application\\chrome.exe`]) {
    if (existsSync(ruta)) return ruta;
  }
  throw new Error("No se encontró Edge ni Chrome.");
}

async function abrirNavegador() {
  const perfil = join(datos, `edge-capturas-${puertoDepuracion}`);
  const proceso = spawn(buscarEdge(), [
    "--headless=new", `--remote-debugging-port=${puertoDepuracion}`, `--user-data-dir=${perfil}`,
    "--no-first-run", "--no-default-browser-check", "--hide-scrollbars", "--mute-audio",
    "--autoplay-policy=no-user-gesture-required", `--window-size=${ANCHO},${ALTO}`, "about:blank",
  ], { stdio: "ignore" });
  let version;
  for (let i = 0; i < 60 && !version; i++) {
    await pausa(250);
    try { version = await (await fetch(`http://127.0.0.1:${puertoDepuracion}/json/version`)).json(); } catch { }
  }
  if (!version) throw new Error("Edge no abrió el puerto de depuración.");
  const navegador = new Conexion(version.webSocketDebuggerUrl);
  await navegador.abierta();
  const { targetId } = await navegador.enviar("Target.createTarget", { url: "about:blank" });
  const objetivos = await (await fetch(`http://127.0.0.1:${puertoDepuracion}/json/list`)).json();
  const cdp = new Conexion(objetivos.find(o => o.id === targetId).webSocketDebuggerUrl);
  await cdp.abierta();
  for (const dominio of ["Page", "Runtime", "Network"]) await cdp.enviar(`${dominio}.enable`);
  await cdp.enviar("Emulation.setDeviceMetricsOverride", { width: ANCHO, height: ALTO, deviceScaleFactor: ESCALA, mobile: false });
  // El aviso del período de prueba solo existe en la demo: no va en el manual.
  await cdp.enviar("Page.addScriptToEvaluateOnNewDocument", { source: `
    document.addEventListener("DOMContentLoaded", () => {
      const s = document.createElement("style");
      s.textContent = "#license-banner{display:none!important}";
      document.head.appendChild(s);
    });` });
  return { proceso, navegador, cdp };
}

async function entrar(pagina) {
  await pagina.cdp.enviar("Page.navigate", { url: `${base}/` });
  await pausa(1200);
  await pagina.evaluar(() => localStorage.clear());
  await pagina.cdp.enviar("Page.navigate", { url: `${base}/` });
  await pagina.esperar("#auth-screen input[type=password]");
  const textos = await pagina.evaluar(() =>
    [...document.querySelectorAll("#auth-screen input")].filter(i => i.offsetWidth > 0).map(i => i.type));
  const usuario = textos.findIndex(t => t !== "password");
  await pagina.evaluar((u, c, iu) => {
    const campos = [...document.querySelectorAll("#auth-screen input")].filter(i => i.offsetWidth > 0);
    const poner = (el, v) => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set.call(el, v);
      el.dispatchEvent(new Event("input", { bubbles: true }));
    };
    poner(campos[iu], u);
    poner(campos.find(i => i.type === "password"), c);
  }, config.administrador.usuario, config.administrador.clave, usuario);
  await pagina.clic("#auth-screen button[type=submit]");
  await pagina.esperar(".sidebar #nav a");
  await pausa(800);
}

// ---------------------------------------------------------------------------
// Capturas
// ---------------------------------------------------------------------------

async function cargarEspecificaciones() {
  const carpeta = join(aqui, "capturas-web");
  const lista = [];
  for (const archivo of readdirSync(carpeta).filter(a => a.endsWith(".mjs")).sort()) {
    const modulo = await import(pathToFileURL(join(carpeta, archivo)).href);
    lista.push(...modulo.default);
  }
  return lista;
}

/** Punto de la marca sobre el elemento: borde izquierdo al medio, salvo que se pida otro. */
function puntoDe(r, donde = "izq") {
  const cx = r.x + r.width / 2, cy = r.y + r.height / 2;
  return {
    izq: [r.x, cy], der: [r.x + r.width, cy], centro: [cx, cy], arriba: [cx, r.y], abajo: [cx, r.y + r.height],
    "arriba-izq": [r.x, r.y], "arriba-der": [r.x + r.width, r.y],
    // Al costado, sin tapar el elemento (la marca mide unos 25 px de la ventana).
    "izq-fuera": [r.x - 16, cy], "der-fuera": [r.x + r.width + 16, cy],
  }[donde] ?? [r.x, cy];
}

/** Guarda las marcas de una captura releyendo el archivo justo antes: así dos
 *  lotes que corren a la vez (con --puerto distinto) no se pisan. */
function guardarMarcas(archivo, marcas) {
  const ruta = join(salida, "marcas.json");
  const todas = existsSync(ruta) ? JSON.parse(readFileSync(ruta, "utf8")) : {};
  todas[archivo] = marcas;
  const ordenado = Object.fromEntries(Object.entries(todas).sort(([a], [b]) => a.localeCompare(b)));
  writeFileSync(ruta, JSON.stringify(ordenado, null, 2) + "\n");
}

async function capturar(pagina, spec) {
  // ancho / alto: otra ventana para páginas largas o tablas anchas.
  const ancho = spec.ancho ?? ANCHO, alto = spec.alto ?? ALTO;
  const otra = ancho !== ANCHO || alto !== ALTO;
  if (otra) {
    await pagina.cdp.enviar("Emulation.setDeviceMetricsOverride", { width: ancho, height: alto, deviceScaleFactor: ESCALA, mobile: false });
    await pausa(400);
  }
  try {
    await capturarEn(pagina, spec, ancho, alto);
  } finally {
    if (otra)
      await pagina.cdp.enviar("Emulation.setDeviceMetricsOverride", { width: ANCHO, height: ALTO, deviceScaleFactor: ESCALA, mobile: false });
  }
}

async function capturarEn(pagina, spec, ANCHO, ALTO) {
  if (spec.antes) await spec.antes(pagina);
  await pagina.reposo(spec.reposo ?? 500);

  // Recorte: un selector (con margen), un rectángulo fijo o toda la ventana.
  let clip = { x: 0, y: 0, width: ANCHO, height: ALTO };
  if (typeof spec.recorte === "string") {
    const r = await pagina.rect(spec.recorte);
    const m = spec.margen ?? 0;
    clip = { x: Math.max(0, r.x - m), y: Math.max(0, r.y - m), width: r.width + 2 * m, height: r.height + 2 * m };
    clip.width = Math.min(clip.width, ANCHO - clip.x);
    clip.height = Math.min(clip.height, ALTO - clip.y);
  } else if (spec.recorte) {
    clip = spec.recorte;
  }

  const marcas = [];
  for (const marca of spec.marcas ?? []) {
    const r = await pagina.rect(marca.sel, { texto: marca.texto, contenido: marca.contenido });
    const [x, y] = puntoDe(r, marca.en);
    const fx = (x - clip.x) / clip.width, fy = (y - clip.y) / clip.height;
    // Una marca fuera de la imagen es un error de la especificación (o de la
    // pantalla): mejor fallar que guardar una marca en el vacío.
    if (fx < -0.02 || fx > 1.02 || fy < -0.02 || fy > 1.02)
      throw new Error(`La marca «${marca.etiqueta}» queda fuera del recorte (${fx.toFixed(2)}, ${fy.toFixed(2)}).`);
    marcas.push({ x: +fx.toFixed(4), y: +fy.toFixed(4), etiqueta: marca.etiqueta });
  }

  // El recorte de CDP va en coordenadas del documento y todo lo medido está en
  // las de la ventana: si la página quedó desplazada (un clic en una fila de
  // abajo la desplaza), hay que sumar el desplazamiento o sale otra zona.
  const [sx, sy] = await pagina.evaluar(() => [scrollX, scrollY]);
  const { data } = await pagina.cdp.enviar("Page.captureScreenshot", {
    format: "png", clip: { ...clip, x: clip.x + sx, y: clip.y + sy, scale: 1 }, captureBeyondViewport: false,
  });
  writeFileSync(join(salida, spec.archivo), Buffer.from(data, "base64"));
  guardarMarcas(spec.archivo, marcas);
  if (spec.despues) await spec.despues(pagina);
}

async function principal() {
  const specs = (await cargarEspecificaciones())
    .filter(s => filtros.length === 0 || filtros.some(f => s.archivo.includes(f)));
  if (argumentos.includes("--listar")) { specs.forEach(s => console.log(s.archivo)); return; }
  if (specs.length === 0) { console.log("Ninguna captura coincide."); return; }

  mkdirSync(salida, { recursive: true });
  const { proceso, navegador, cdp } = await abrirNavegador();
  const pagina = new Pagina(cdp);
  let fallas = 0;
  try {
    let sesion = false;
    for (const spec of specs) {
      try {
        if (spec.sinSesion) {
          await pagina.evaluar(() => localStorage.clear()).catch(() => { });
          await cdp.enviar("Page.navigate", { url: `${base}/` });
          await pausa(1500);
          sesion = false;
        } else if (!sesion) {
          await entrar(pagina);
          sesion = true;
        } else {
          // Página recargada: sin ventanas, menús ni desplazamientos de la captura anterior.
          // (con la consulta distinta: cambiar solo el # no recarga).
          await cdp.enviar("Page.navigate", { url: `${base}/?r=${Date.now()}#/` });
          await pagina.esperar(".sidebar #nav a");
          await pausa(600);
        }
        await capturar(pagina, spec);
        console.log(`✔ ${spec.archivo}`);
      } catch (error) {
        fallas++;
        console.log(`✘ ${spec.archivo}: ${error.message}`);
        await pagina.tecla("Escape").catch(() => { });
      }
    }
  } finally {
    cdp.cerrar();
    navegador.cerrar();
    proceso.kill();
  }
  if (fallas) process.exitCode = 1;
}

await principal();
