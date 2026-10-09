// Capítulos 3, 5, 10 y 13 (panel web): Vista en vivo, Centro de eventos,
// Biblioteca de sonidos y Automatizaciones.

const pausa = ms => new Promise(r => setTimeout(r, ms));

/** Cambia el tamaño de la ventana (en px CSS, escala 2 como el capturador).
 *  Quien lo agrande debe volver a 1280×800 en despues(). */
async function ventana(p, ancho, alto) {
  await p.cdp.enviar("Emulation.setDeviceMetricsOverride", { width: ancho, height: alto, deviceScaleFactor: 2, mobile: false });
  await pausa(500);
}

/** Espera a que haya `cuantos` <video> de la grilla reproduciendo de verdad. */
async function videosReproduciendo(p, cuantos, ms = 30000) {
  const fin = Date.now() + ms;
  while (Date.now() < fin) {
    const listos = await p.evaluar(() => [...document.querySelectorAll("#lv-grid video")]
      .filter(v => v.readyState >= 3 && !v.paused && v.currentTime > 0.5 && v.videoWidth > 0).length);
    if (listos >= cuantos) return;
    await pausa(300);
  }
  throw new Error(`No quedaron ${cuantos} videos reproduciendo`);
}

export default [
  // --- Capítulo 3 · Vista en vivo ------------------------------------------
  {
    archivo: "web-vista-en-vivo.png",
    antes: async p => {
      await p.ir("#/live", "#lv-tree .lv-node");
      await p.clic(".lv-seg [data-tree=location]");
      await p.esperar("#lv-tree .lv-loc");
      // La vista guardada «Casa matriz» deja la división 4 con sus cuatro cámaras.
      await p.clic("#lv-views-btn");
      await p.esperarTexto("#lv-views-list .lv-view-row", "Casa matriz");
      await p.clic("#lv-views-list .lv-view-row", { texto: "Casa matriz" });
      await videosReproduciendo(p, 4);
      await p.clic("#lv-settings-btn");
      await p.esperar("#lv-settings-pop:not([hidden])");
    },
    recorte: ".main",
    marcas: [
      { sel: ".lv-seg", en: "izq-fuera", etiqueta: "Lista por Equipo o por Ubicación" },
      { sel: "#lv-search", en: "der", etiqueta: "Buscar canal…" },
      { sel: ".lv-tb-left > .muted", texto: "División", en: "izq-fuera", etiqueta: "División" },
      { sel: "#lv-zoom-btn", en: "abajo", etiqueta: "Zoom digital" },
      { sel: "#lv-views-btn", en: "der-fuera", etiqueta: "Vistas, con el nombre de la vista cargada" },
      { sel: "#lv-aux-btn", en: "izq-fuera", etiqueta: "Pantalla auxiliar" },
      { sel: "#lv-full-btn", en: "abajo", etiqueta: "Pantalla completa" },
      { sel: "#lv-settings-pop", en: "izq-fuera", etiqueta: "Engranaje: Ajustes de este navegador" },
    ],
  },

  // --- Capítulo 5 · Centro de eventos --------------------------------------
  {
    archivo: "web-centro-de-eventos.png",
    antes: async p => {
      await p.ir("#/event-center", "#evc-list .evc-card");
      await p.clic("#evc-list .evc-card.pending .evc-card-main");
      await p.esperar("#evc-detail #evc-ack-detail");
      await p.clic(".evc-tab[data-tab=photos]");
      // Así queda mientras suena una alerta: «Sonar alertas nuevas» marcado y
      // «Silenciar» disponible (solo en este navegador, sin tocar el servidor).
      if (!(await p.evaluar(() => document.querySelector("#evc-sound").checked))) await p.clic("#evc-sound");
      await p.evaluar(() => { evcSound = { alertId: evcSelectedId, stop() { } }; evcUpdateMuteButton(); });
      await p.esperar(".evc-photo img");
    },
    recorte: ".main",
    marcas: [
      { sel: "#evc-only", etiqueta: "Solo sin confirmar" },
      { sel: "#evc-sound", etiqueta: "Sonar alertas nuevas" },
      { sel: "#evc-refresh", en: "abajo", etiqueta: "Actualizar" },
      { sel: "#evc-list .evc-card.pending .evc-ack", en: "der-fuera", etiqueta: "Enterado en la lista" },
      { sel: "#evc-detail .evc-facts dd:last-of-type", contenido: true, en: "der-fuera", etiqueta: "Dirigida a" },
      { sel: "#evc-detail .evc-tabs", en: "arriba", etiqueta: "Fotos, Cámaras y Qué hizo el sistema" },
      { sel: "#evc-mute", en: "der-fuera", etiqueta: "Silenciar" },
    ],
  },

  // --- Capítulo 10 · Biblioteca de sonidos ---------------------------------
  {
    archivo: "web-biblioteca-sonidos.png",
    antes: async p => {
      await p.ir("#/sounds", "#wf-sounds-table tr[data-name]");
      const fin = Date.now() + 15000;
      while (Date.now() < fin && await p.evaluar(() => document.querySelector("#wf-sounds-table").textContent.includes("midiendo")))
        await pausa(200);
    },
    recorte: ".main",
    marcas: [
      { sel: "#wf-audio-file", en: "izq-fuera", etiqueta: "Archivo de audio y Subir" },
      { sel: "#wf-sounds-table .wf-snd-level", etiqueta: "Nivel (pico)" },
      { sel: "#wf-sounds-table .btn-audio-gain", etiqueta: "Amplificar" },
      { sel: "#wf-sounds-table .btn-audio-gain-custom", etiqueta: "dB…" },
      { sel: "#view h3", texto: "Audios en los parlantes", etiqueta: "Audios en los parlantes" },
    ],
  },

  // --- Capítulo 13 · Automatizaciones --------------------------------------
  {
    archivo: "web-automatizaciones-listado.png",
    async antes(p) {
      await ventana(p, 1600, 1400);
      await p.ir("#/workflows", "#wf-table tbody tr");
      await p.esperar("#wf-alerts table");
      await p.esperar("#wf-runs table");
      const m = await p.rect(".main");
      const fin = await p.rect("#wf-runs");
      this.recorte = { x: m.x, y: 0, width: m.width, height: Math.ceil(fin.y + fin.height + 24) };
    },
    despues: async p => { await ventana(p, 1280, 800); },
    marcas: [
      { sel: "#wf-search", etiqueta: "Buscador" },
      { sel: "#btn-wf-smtp", etiqueta: "Servidor de correo, Sonidos y Nueva automatización" },
      { sel: "#wf-table .tag.off", etiqueta: "Estado: Activa o Pausada" },
      { sel: "#wf-table td.row-actions", etiqueta: "Probar, Abrir, Duplicar y Eliminar" },
      { sel: "#wf-alerts .btn-alert-ack", etiqueta: "Enterado" },
      { sel: "#wf-runs tr[data-run]", etiqueta: "Una ejecución: clic para ver el detalle" },
    ],
  },
  {
    archivo: "web-automatizaciones-ejecucion.png",
    antes: async p => {
      await p.ir("#/workflows", "#wf-runs tr[data-run]");
      await p.clic("#wf-runs tr[data-run]", { texto: "Ronda nocturna del andén" });
      await p.esperar(".modal .wf-thumbs img");
      // Que terminen de cargar las fotos y la animación de entrada de la ventana.
      await p.evaluar(async () => {
        for (let i = 0; i < 60 && ![...document.querySelectorAll(".modal .wf-thumbs img")].every(i => i.complete && i.naturalWidth > 0); i++)
          await new Promise(r => setTimeout(r, 250));
      });
    },
    reposo: 1200,
    recorte: ".modal",
    margen: 0,
    marcas: [
      { sel: ".modal .audit-detail-grid .tag", etiqueta: "Resultado" },
      { sel: ".modal table tbody tr", etiqueta: "Paso con su resultado y detalle" },
      { sel: ".modal .wf-thumbs", etiqueta: "Fotos capturadas" },
    ],
    despues: async p => { await p.tecla("Escape"); },
  },
  {
    archivo: "web-automatizaciones-editor.png",
    // Más ancha: con 1280 px la barra del editor parte en dos líneas.
    ancho: 1500,
    alto: 900,
    antes: async p => {
      await p.ir("#/workflows", "#wf-table tbody tr");
      // La de tres pasos (foto + aviso), que muestra mejor el diagrama.
      await p.evaluar(() => [...document.querySelectorAll("#wf-table tbody tr")]
        .find(tr => tr.textContent.includes("Ronda nocturna del andén")).querySelector(".btn-wf-edit").click());
      await p.esperar("#wfe-nodes .wfe-node");
      await p.reposo(800);
      await p.clic("#wfe-nodes .wfe-node", { texto: "Avisar a los operadores" });
      await p.esperarTexto("#wfe-props", "Avisar");
    },
    recorte: ".main",
    marcas: [
      { sel: "#wfe-name", etiqueta: "Nombre de la automatización" },
      { sel: "#wfe-cooldown", etiqueta: "Mínimo entre ejecuciones y Activa" },
      { sel: "#wfe-test", etiqueta: "Probar y Guardar" },
      { sel: ".wfe-palette", en: "arriba-izq", etiqueta: "Paleta Pasos" },
      { sel: "#wfe-nodes .wfe-port.out", en: "der-fuera", etiqueta: "Salida de un paso: arrástrela a otro para conectarlos" },
      { sel: "#wfe-props", en: "arriba-izq", etiqueta: "Propiedades del paso seleccionado" },
      { sel: ".wfe-zoom", en: "izq-fuera", etiqueta: "Acercar y alejar" },
    ],
  },
];
