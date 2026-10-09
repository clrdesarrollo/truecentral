// Capítulos 7 (Cerco eléctrico), 8 (Control de acceso) y 14 (Fuentes de video), panel web.
// La demo no tiene equipos de acceso ni paneles conectados: las personas quedan
// «Sin niveles» y el panel de cerco, sin enrolar (ver sembrar.py).

import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const demo = join(dirname(fileURLToPath(import.meta.url)), "..");
const config = JSON.parse(readFileSync(join(demo, "demo.json"), "utf8"));
const datos = config.carpetaDatos.replace(/%([^%]+)%/g, (_, v) => process.env[v] ?? "");

/** Elige en un <select> la opción con ese texto (sin la sangría del árbol de ubicaciones). */
async function elegir(p, selector, texto) {
  await p.evaluar((s, t) => {
    const el = document.querySelector(s);
    const opcion = [...el.options].find(o => o.textContent.replace(/ /g, "").trim() === t);
    if (!opcion) throw new Error(`No hay «${t}» en ${s}`);
    el.value = opcion.value;
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }, selector, texto);
}

/** Clic (del DOM) en el botón de la fila de una tabla que contiene el texto. */
async function botonDeFila(p, filas, texto, boton) {
  await p.evaluar((f, t, b) => {
    const fila = [...document.querySelectorAll(f)].find(tr => tr.textContent.includes(t));
    if (!fila) throw new Error(`No hay una fila con «${t}»`);
    fila.querySelector(b).click();
  }, filas, texto, boton);
}

/** Rectángulo que abarca a varios elementos, con un margen (para recortar sin un contenedor común). */
async function abarcar(p, selectores, margen = 0) {
  const rs = [];
  for (const s of selectores) rs.push(await p.rect(s));
  const x = Math.max(0, Math.min(...rs.map(r => r.x)) - margen);
  const y = Math.max(0, Math.min(...rs.map(r => r.y)) - margen);
  // El alto de la ventana depende de la captura (alto:): se lee de la página.
  const [anchoVentana, altoVentana] = await p.evaluar(() => [innerWidth, innerHeight]);
  const der = Math.min(anchoVentana, Math.max(...rs.map(r => r.x + r.width)) + margen);
  const abajo = Math.min(altoVentana, Math.max(...rs.map(r => r.y + r.height)) + margen);
  return { x, y, width: der - x, height: abajo - y };
}

// La búsqueda de equipos en la red (Equipos en línea) mostraría los equipos
// REALES de la red de este PC: en estas capturas no se dispara.
const sinBusquedaEnLaRed = p => p.cdp.enviar("Network.setBlockedURLs", { urls: ["*/api/discovery/*"] });
const conBusquedaEnLaRed = p => p.cdp.enviar("Network.setBlockedURLs", { urls: [] });
const cerrarModal = p => p.evaluar(() => closeModal());

export default [
  // -------------------------------------------------------------------------
  // Capítulo 8 · Control de acceso
  // -------------------------------------------------------------------------
  {
    archivo: "web-acceso-personas.png",
    alto: 1000,
    async antes(p) {
      await p.ir("#/access-persons", "#access-person-results table");
      this.recorte = await abarcar(p, ["#view > .toolbar", "#view .filter-bar", "#access-person-results"], 12);
    },
    marcas: [
      { sel: "#view .filter-bar", en: "arriba-izq", etiqueta: "Filtros: Buscar, Departamento, Nivel de acceso y En los equipos" },
      { sel: "#btn-person-sync", etiqueta: "Escribir pendientes, Reenviar todo y Agregar persona" },
      { sel: "#access-person-results td", texto: "huellas", etiqueta: "Credenciales cargadas" },
      { sel: "#access-person-results tbody tr:first-child td:nth-child(5) .tag", etiqueta: "«ninguno»: sin niveles de acceso, no entra por ninguna puerta" },
      { sel: "#access-person-results tbody tr:first-child td:nth-child(7) .tag", etiqueta: "Estado en los equipos" },
      { sel: "#access-person-results .tag", texto: "desactivada", etiqueta: "Persona desactivada" },
    ],
  },
  {
    archivo: "web-acceso-persona-credenciales.png",
    async antes(p) {
      await p.ir("#/access-persons", "#access-person-results table");
      await botonDeFila(p, "#access-person-results tbody tr", "Ana Pérez", ".btn-person-edit");
      await p.esperar("#pw-steps .wizard-step");
      await p.clic('#pw-steps .wizard-step[data-step="1"]');
      await p.esperar("#pw-fingers .hand-finger");
      // El índice derecho: enrolado y con buena calidad, para que se vea su ficha.
      await p.evaluar(() => document.querySelector('#pw-fingers .hand-finger[data-finger="2"]')
        .dispatchEvent(new MouseEvent("click", { bubbles: true })));
      await p.esperarTexto("#pw-finger-detail", "Índice derecho");
    },
    recorte: "#modal",
    marcas: [
      { sel: '#pw-steps .wizard-step[data-step="1"]', etiqueta: "Pasos del asistente" },
      { sel: "#pw-card-add", etiqueta: "+ tarjeta y Leer tarjeta en un lector" },
      { sel: "#pw-pin", etiqueta: "Clave de teclado" },
      { sel: "#pw-fingers svg.hands", en: "arriba-izq", etiqueta: "Las dos manos: elija el dedo en el dibujo" },
      { sel: "#pw-finger-detail", en: "arriba-izq", etiqueta: "Ficha del dedo elegido, con su calidad" },
      { sel: "#pw-face-pick", etiqueta: "Rostro: Elegir foto" },
    ],
    despues: cerrarModal,
  },
  {
    archivo: "web-acceso-horario.png",
    async antes(p) {
      await p.ir("#/access-schedules", "#btn-schedule-new");
      await p.clic("#btn-schedule-new");
      await p.esperar("#as-days .access-day");
      await p.escribir("#as-name", "Jornada comercial");
      await p.escribir("#as-description", "Atención de público");
      // Sábado con dos tramos; el horario nuevo ya trae lunes a viernes de 08:00 a 18:00.
      const sabado = '#as-days .access-day[data-day="6"]';
      await p.clic(`${sabado} .seg-add`);
      await p.clic(`${sabado} .seg-add`);
      await p.escribir(`${sabado} .access-segment[data-i="0"] .seg-start`, "09:00");
      await p.escribir(`${sabado} .access-segment[data-i="0"] .seg-end`, "13:00");
      await p.escribir(`${sabado} .access-segment[data-i="1"] .seg-start`, "14:00");
      await p.escribir(`${sabado} .access-segment[data-i="1"] .seg-end`, "17:00");
      await p.evaluar(() => document.activeElement?.blur());
    },
    recorte: "#modal",
    marcas: [
      { sel: "#as-name", etiqueta: "Nombre" },
      { sel: '#as-days .access-day[data-day="1"] .access-day-name', etiqueta: "Una fila por día, con sus tramos" },
      { sel: '#as-days .access-day[data-day="6"] .seg-add', etiqueta: "+ tramo: agrega otro tramo a ese día" },
      { sel: '#as-days .access-day[data-day="6"] .seg-copy', etiqueta: "Copiar a todos: copia ese día a los otros seis" },
      { sel: '#as-days .access-day[data-day="6"] .seg-del', etiqueta: "Quitar este tramo" },
      { sel: '#as-days .access-day[data-day="0"] .access-day-segments .muted', etiqueta: "Día sin tramos: no deja pasar a nadie" },
    ],
    despues: cerrarModal,
  },

  // -------------------------------------------------------------------------
  // Capítulo 14 · Fuentes de video
  // -------------------------------------------------------------------------
  {
    archivo: "web-fuentes-de-video.png",
    ancho: 1500,
    async antes(p) {
      await sinBusquedaEnLaRed(p);
      await p.ir("#/devices", "#view table.grid");
      this.recorte = await abarcar(p, ["#view > .toolbar", "#view .table-scroll"], 12);
    },
    marcas: [
      { sel: "#btn-device-new", etiqueta: "Agregar dispositivo" },
      { sel: "#view tbody tr:first-child td:first-child", etiqueta: "Nombre y, debajo, su ubicación" },
      { sel: "#view thead th", texto: "Canales", etiqueta: "Canales habilitados del total" },
      { sel: "#view tbody tr:first-child td:nth-child(10) .tag", etiqueta: "Estado de la conexión" },
      { sel: "#view tbody tr:first-child .btn-channels", etiqueta: "Canales, Revalidar, Editar y Eliminar" },
    ],
    despues: conBusquedaEnLaRed,
  },
  {
    archivo: "web-fuentes-de-video-agregar.png",
    alto: 1150,
    async antes(p) {
      await sinBusquedaEnLaRed(p);
      await p.ir("#/devices", "#btn-device-new");
      await p.clic("#btn-device-new");
      await p.esperar("#df-driver");
      await p.escribir("#df-name", "Cámara bodega norte");
      await elegir(p, "#df-location", "Bodega");
      await p.escribir("#df-driver", "onvif");
      // Una cámara simulada de la demo; se prueba la conexión, no se guarda.
      await p.escribir("#df-host", "127.0.0.1");
      await p.escribir("#df-sdkport", "8903");
      await p.escribir("#df-rtspport", String(config.puertos.camarasRtsp));
      await p.escribir("#df-username", config.credencialesCamaras.usuario);
      await p.escribir("#df-password", config.credencialesCamaras.clave);
      await p.clic("#df-probe");
      await p.esperarTexto("#probe-result", "Conexión validada", { ms: 40000 });
    },
    recorte: "#modal",
    marcas: [
      { sel: "#df-driver", etiqueta: "Marca / protocolo" },
      { sel: "#df-sdkport", etiqueta: "Puerto SDK y Puerto RTSP" },
      { sel: "#df-probe", etiqueta: "Probar conexión" },
      { sel: "#probe-result .probe-grid span", texto: "Cupo de licencia", etiqueta: "Cupo de licencia" },
    ],
    async despues(p) { await cerrarModal(p); await conBusquedaEnLaRed(p); },
  },
  {
    archivo: "web-fuentes-de-video-canales.png",
    async antes(p) {
      await sinBusquedaEnLaRed(p);
      await p.ir("#/devices", "#view table.grid");
      await botonDeFila(p, "#view tbody tr", "Domo PTZ 08", ".btn-channels");
      await p.esperar(".channel-row");
      // La miniatura sale de la cámara y es de carga diferida (loading=lazy), que
      // el navegador sin ventana no dispara: se fuerza la carga y se espera.
      await p.evaluar(() => {
        const img = document.querySelector(".channel-row .channel-thumb");
        img.loading = "eager";
        img.src = img.src;
      });
      const fin = Date.now() + 20000;
      while (!await p.evaluar(() => {
        const img = document.querySelector(".channel-row .channel-thumb");
        return img.complete && img.naturalWidth > 0;
      })) {
        if (Date.now() > fin) throw new Error("La miniatura del canal no cargó.");
        await p.reposo(300);
      }
    },
    recorte: "#modal",
    marcas: [
      { sel: ".channel-row .channel-thumb", etiqueta: "Miniatura de la imagen" },
      { sel: ".channel-row .ch-name", etiqueta: "Nombre del canal" },
      { sel: ".channel-row .tag", etiqueta: "Canal, número RTSP y Con señal / Sin señal" },
      { sel: ".channel-row label", texto: "Habilitado", etiqueta: "Habilitado" },
      { sel: ".channel-row label", texto: "PTZ", etiqueta: "PTZ / lente" },
      { sel: ".channel-row label", texto: "Proxy", etiqueta: "Proxy" },
      { sel: ".channel-row .ch-save", etiqueta: "Guardar (cada fila por separado)" },
    ],
    async despues(p) { await cerrarModal(p); await conBusquedaEnLaRed(p); },
  },

  // -------------------------------------------------------------------------
  // Capítulo 7 · Cerco eléctrico
  // -------------------------------------------------------------------------
  {
    // Las credenciales se muestran una sola vez, al crear el panel: sembrar.py
    // guarda las del panel de la demo y acá se abre la misma ventana con ellas.
    archivo: "web-paneles-de-cerco-credenciales.png",
    async antes(p) {
      const archivo = join(datos, "cerco-credenciales.json");
      if (!existsSync(archivo)) throw new Error("Falta cerco-credenciales.json: corra demo.ps1 Sembrar");
      const [nombre, credenciales] = Object.entries(JSON.parse(readFileSync(archivo, "utf8")))[0];
      await p.ir("#/cerco-panels", "#cerco-rows tr");
      await p.evaluar((c, n) => cercoCredentialsModal(c, n), credenciales, nombre);
      await p.esperar("#cr-id");
    },
    recorte: "#modal",
    marcas: [
      { sel: "#cr-id", etiqueta: "ID de equipo" },
      { sel: "#cr-code", etiqueta: "Código de enrolamiento" },
      { sel: "#cr-ws", etiqueta: "URL WebSocket" },
      { sel: "#cr-copy", etiqueta: "Copiar todo" },
      { sel: "#cr-close", etiqueta: "Listo" },
    ],
    despues: cerrarModal,
  },
];
