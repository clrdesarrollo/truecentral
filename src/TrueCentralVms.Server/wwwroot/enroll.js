// CLR TrueCentral VMS — captura de huellas con el lector USB del puesto.
//
// El panel web no puede hablar con el hardware del PC del operador: eso lo hace
// el COMPLEMENTO DE ENROLAMIENTO, que se instala una vez en ese equipo, corre
// en la bandeja del sistema y expone su API en 127.0.0.1. Hoy maneja el lector
// de huellas USB (Hikvision DS-K1F820-F / DS-K1F800-F); las capacidades que se
// agreguen las anuncia en /api/control/status.
//
// Este módulo lo descubre, ofrece el modo de captura, guía el enrolamiento y
// devuelve la plantilla para que access-catalog.js la guarde como credencial.
// La plantilla viaja del complemento al panel y del panel al servidor, que la
// guarda cifrada; nunca vuelve a bajar al navegador.
//
// Se carga ANTES que access-catalog.js y app.js, y usa sus utilidades globales
// (esc, toast, Api).
"use strict";

// Puertos donde el complemento puede estar escuchando, en el mismo orden en que
// él los prueba al arrancar (ver WebControlSettings.Ports).
const ENROLL_AGENT_PORTS = [5081, 25471, 25472];

// Modos de captura, como el "Collection Mode" de HikCentral. Los que todavía no
// tienen driver se muestran deshabilitados para que se vea hacia dónde va.
const ENROLL_CAPTURE_MODES = [
  {
    key: "usb", label: "Lector USB de huellas", available: true,
    detail: "Hikvision DS-K1F820-F conectado a este PC, por medio del complemento de enrolamiento.",
  },
  {
    key: "device", label: "Terminal de control de acceso", available: false,
    detail: "Capturar desde un equipo de puerta ya registrado (requiere su driver).",
  },
];

// Preferencias del puesto (modo y lector elegidos), recordadas en este navegador.
const ENROLL_PREFS_KEY = "tcvms.enroll.prefs";

function enrollPrefs() {
  try { return JSON.parse(localStorage.getItem(ENROLL_PREFS_KEY)) || {}; }
  catch { return {}; }
}

function saveEnrollPrefs(prefs) {
  try { localStorage.setItem(ENROLL_PREFS_KEY, JSON.stringify({ ...enrollPrefs(), ...prefs })); }
  catch { /* modo privado */ }
}

// Último complemento encontrado (caché de la sesión del panel: evita tres
// sondeos por cada captura).
let enrollAgent = null;

/** Llamada al complemento local, con tope de espera propio (no pasa por Api). */
async function fetchAgent(baseUrl, path, options = {}, timeoutMs = 1500) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(`${baseUrl}${path}`, {
      ...options,
      signal: controller.signal,
      headers: options.body ? { "Content-Type": "application/json" } : undefined,
    });
    const text = await res.text();
    const data = text ? JSON.parse(text) : null;
    if (!res.ok) throw new Error(data?.error || `Error ${res.status} del complemento`);
    return data;
  } finally {
    clearTimeout(timer);
  }
}

/** Busca el complemento en los puertos conocidos; null si no está instalado o no corre. */
async function findEnrollAgent(force = false) {
  if (enrollAgent && !force) {
    try {
      enrollAgent.status = await fetchAgent(enrollAgent.baseUrl, "/api/control/status", {}, 1200);
      return enrollAgent;
    } catch {
      enrollAgent = null;   // se cerró: volver a buscarlo
    }
  }
  for (const port of ENROLL_AGENT_PORTS) {
    const baseUrl = `http://127.0.0.1:${port}`;
    try {
      const status = await fetchAgent(baseUrl, "/api/control/status", {}, 1200);
      if (status?.ok) {
        enrollAgent = { baseUrl, status };
        return enrollAgent;
      }
    } catch {
      // Puerto sin complemento: seguir con el siguiente.
    }
  }
  return null;
}

/** Bloque de descarga del instalador (lo publica el propio servidor). */
async function enrollDownloadHtml() {
  let info = null;
  try { info = await Api.get("/api/webcontrol/info"); } catch { /* servidor viejo */ }
  if (!info?.available) {
    return `<div class="muted" style="font-size:12px;margin-top:6px">
      El instalador del complemento no está publicado en este servidor: cópielo en la carpeta
      <code>webcontrol</code> del servidor o pídaselo al administrador.</div>`;
  }
  return `<button class="btn ghost" id="en-download" style="margin-top:8px">
      ⤓ Descargar el complemento (${info.sizeMb} MB)</button>`;
}

function bindEnrollDownload(root) {
  $("#en-download", root)?.addEventListener("click", () => {
    // La descarga la inicia el navegador: el token va por query porque no
    // puede mandar la cabecera Authorization.
    window.location.href = `/api/webcontrol/installer?access_token=${encodeURIComponent(Api.token)}`;
    toast("Descargando el instalador del complemento…");
  });
}

/**
 * Diálogo de captura. Se abre POR ENCIMA del asistente de personas y con su
 * propia capa: el modal del panel es uno solo, y reutilizarlo cerraría el
 * formulario a medio llenar.
 *
 * @param {string} personName  A quién se le enrola (solo para el título).
 * @param {number} fingerNumber  Dedo elegido (1 a 10).
 * @param {string} fingerName  Su nombre ("Índice derecho").
 * @param {(result: {template: string, quality: number, source: string}) => void} onCaptured
 */
function fingerprintCaptureDialog(personName, fingerNumber, fingerName, onCaptured) {
  const root = document.createElement("div");
  root.className = "modal-backdrop enroll-backdrop";
  root.innerHTML = `<div class="modal enroll-modal"><div id="en-body"></div></div>`;
  document.body.appendChild(root);

  let polling = null;
  let readers = [];
  let agent = null;
  let lastSeq = -1;

  const body = () => $("#en-body", root);

  const stopPolling = () => {
    if (polling) { clearInterval(polling); polling = null; }
  };

  const close = () => {
    stopPolling();
    document.removeEventListener("keydown", onKey);
    root.remove();
  };

  // Escape cancela: acá sí corresponde, porque lo que se pierde es una captura
  // que se puede repetir, no un formulario lleno.
  function onKey(e) { if (e.key === "Escape") cancelAndClose(); }
  document.addEventListener("keydown", onKey);

  async function cancelAndClose() {
    stopPolling();
    if (agent) { try { await fetchAgent(agent.baseUrl, "/api/enroll/cancel", { method: "POST" }); } catch { /* ya cerrado */ } }
    close();
  }

  // --- Paso 1: de dónde se captura ---------------------------------------
  function renderSource() {
    const prefs = enrollPrefs();
    const mode = ENROLL_CAPTURE_MODES.find((m) => m.key === prefs.mode && m.available) ?? ENROLL_CAPTURE_MODES[0];
    body().innerHTML = `
      <h3>Capturar huella — ${esc(personName)}</h3>
      <div class="muted" style="margin-top:-8px">Dedo a enrolar: <b>${esc(fingerName)}</b></div>

      <div class="enroll-sources">
        ${ENROLL_CAPTURE_MODES.map((m) => `
          <label class="enroll-source ${m.available ? "" : "disabled"}">
            <input type="radio" name="en-mode" value="${esc(m.key)}"
              ${m.key === mode.key ? "checked" : ""} ${m.available ? "" : "disabled"}>
            <div>
              <b>${esc(m.label)}</b>
              <div class="muted" style="font-size:12px">${esc(m.detail)}${m.available ? "" : " — próximamente."}</div>
            </div>
          </label>`).join("")}
      </div>

      <div id="en-agent" class="enroll-agent muted">Buscando el complemento en este equipo…</div>

      <div class="modal-actions">
        <button class="btn ghost" id="en-cancel">Cancelar</button>
        <button class="btn" id="en-start" disabled>Iniciar captura</button>
      </div>`;

    $("#en-cancel", root).addEventListener("click", close);
    $("#en-start", root).addEventListener("click", startCapture);
    $$('input[name="en-mode"]', root).forEach((r) =>
      r.addEventListener("change", () => saveEnrollPrefs({ mode: r.value })));
    detectAgent();
  }

  async function detectAgent() {
    const box = $("#en-agent", root);
    agent = await findEnrollAgent(true);
    if (!box || !box.isConnected) return;

    if (!agent) {
      box.className = "enroll-agent warn";
      box.innerHTML = `
        <b>No se encontró el complemento en este equipo.</b>
        <div class="muted" style="font-size:12px;margin-top:4px">
          Instálelo en el PC donde está conectado el lector: queda corriendo en la bandeja del
          sistema, arranca con Windows y escucha solo en 127.0.0.1
          (puertos ${ENROLL_AGENT_PORTS.join(", ")}).
        </div>
        ${await enrollDownloadHtml()}
        <button class="btn ghost" id="en-retry" style="margin-top:8px">Buscar de nuevo</button>`;
      bindEnrollDownload(root);
      $("#en-retry", root)?.addEventListener("click", detectAgent);
      return;
    }

    try { readers = await fetchAgent(agent.baseUrl, "/api/enroll/readers"); }
    catch { readers = []; }

    const prefs = enrollPrefs();
    const chosen = readers.some((r) => r.id === prefs.readerId) ? prefs.readerId : readers[0]?.id;

    box.className = "enroll-agent ok";
    box.innerHTML = `
      <b>Complemento activo en ${esc(agent.status.machine)}</b>
      <span class="muted" style="font-size:12px">· v${esc(agent.status.version)}
        · SDK ${esc(agent.status.fingerprint?.sdkVersion ?? "—")}</span>
      <div class="field" style="margin-top:8px">
        <label>Lector</label>
        <select id="en-reader">
          ${readers.map((r) => `<option value="${esc(r.id)}" ${r.id === chosen ? "selected" : ""}>${esc(r.name)}</option>`).join("")}
        </select>
      </div>
      <div class="row-actions" style="margin-top:8px;align-items:center">
        <button class="btn ghost" id="en-test">Probar lector</button>
        <span class="muted" style="font-size:12px" id="en-test-result"></span>
      </div>
      <div class="muted" style="font-size:12px;margin-top:6px">
        El SDK abre el primer lector Hikvision que encuentra; elegir uno en particular solo
        importa si hay más de uno conectado.
      </div>`;

    $("#en-reader", root)?.addEventListener("change", (e) => saveEnrollPrefs({ readerId: e.target.value }));
    $("#en-test", root).addEventListener("click", testReader);
    const start = $("#en-start", root);
    if (start) start.disabled = false;
  }

  async function testReader() {
    const button = $("#en-test", root);
    const result = $("#en-test-result", root);
    const readerId = $("#en-reader", root)?.value ?? "auto";
    button.disabled = true;
    result.textContent = "Conectando con el lector…";
    try {
      const res = await fetchAgent(agent.baseUrl, "/api/enroll/test",
        { method: "POST", body: JSON.stringify({ readerId }) }, 15000);
      result.textContent = res.message;
    } catch (err) {
      result.textContent = err.message;
    } finally {
      button.disabled = false;
    }
  }

  // --- Paso 2: captura en curso ------------------------------------------
  async function startCapture() {
    const readerId = $("#en-reader", root)?.value ?? "auto";
    lastSeq = -1;
    renderCapture();
    try {
      await fetchAgent(agent.baseUrl, "/api/enroll/start",
        { method: "POST", body: JSON.stringify({ readerId }) }, 4000);
    } catch (err) {
      renderError(err.message);
      return;
    }
    polling = setInterval(pollSession, 400);
  }

  function renderCapture() {
    body().innerHTML = `
      <h3>Capturando huella</h3>
      <div class="enroll-capture">
        <div class="enroll-preview" id="en-preview">
          <span class="muted" style="font-size:12px">Esperando la imagen del sensor…</span>
        </div>
        <div class="enroll-progress">
          <p class="enroll-message" id="en-message">Conectando con el lector…</p>
          <p class="muted" style="font-size:12px" id="en-step"></p>
          <p class="muted" style="font-size:12px">
            Apoye siempre el <b>mismo dedo</b> (${esc(fingerName)}), centrado y sin moverlo.
          </p>
        </div>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" id="en-abort">Cancelar captura</button>
      </div>`;
    $("#en-abort", root).addEventListener("click", cancelAndClose);
  }

  async function pollSession() {
    let session;
    try {
      session = await fetchAgent(agent.baseUrl, "/api/enroll/session", {}, 2000);
    } catch (err) {
      stopPolling();
      renderError(`Se perdió la comunicación con el complemento: ${err.message}`);
      return;
    }

    const message = $("#en-message", root);
    if (message) message.textContent = session.message;
    const step = $("#en-step", root);
    if (step && session.totalSteps)
      step.textContent = session.step ? `Captura ${session.step} de ${session.totalSteps}` : "";

    // La imagen se pide por número de secuencia: así no se baja en cada sondeo.
    if (session.imageSeq && session.imageSeq !== lastSeq) {
      lastSeq = session.imageSeq;
      const preview = $("#en-preview", root);
      if (preview)
        preview.innerHTML = `<img alt="Huella capturada" src="${agent.baseUrl}/api/enroll/image?seq=${session.imageSeq}">`;
    }

    if (session.state === "done") { stopPolling(); renderDone(session); }
    else if (session.state === "error") { stopPolling(); renderError(session.error || session.message); }
    else if (session.state === "cancelled") { stopPolling(); close(); }
  }

  // --- Paso 3: resultado --------------------------------------------------
  function renderDone(session) {
    const quality = session.quality ?? 0;
    const tag = quality >= 60 ? "on" : quality >= 40 ? "operator" : "off";
    body().innerHTML = `
      <h3>Huella capturada</h3>
      <div class="enroll-capture">
        <div class="enroll-preview">${lastSeq > 0
          ? `<img alt="Huella capturada" src="${agent.baseUrl}/api/enroll/image?seq=${lastSeq}">`
          : `<span class="muted" style="font-size:12px">Sin vista previa</span>`}</div>
        <div class="enroll-progress">
          <p><b>${esc(fingerName)}</b></p>
          <p>Calidad de la plantilla: <span class="tag ${tag}">${quality}/100</span></p>
          <p class="muted" style="font-size:12px">${quality >= 60
            ? "Calidad suficiente para un reconocimiento confiable."
            : "Calidad baja: conviene repetir la captura con el dedo limpio y bien centrado."}</p>
        </div>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" id="en-again">Repetir captura</button>
        <button class="btn" id="en-save">Usar esta huella</button>
      </div>`;

    $("#en-again", root).addEventListener("click", startCapture);
    $("#en-save", root).addEventListener("click", () => {
      onCaptured({
        template: session.template,
        quality,
        source: `Lector USB ${agent.status.machine}`,
      });
      close();
    });
  }

  function renderError(error) {
    body().innerHTML = `
      <h3>No se pudo capturar la huella</h3>
      <div class="error-box">${esc(error)}</div>
      <div class="modal-actions">
        <button class="btn ghost" id="en-close">Cerrar</button>
        <button class="btn" id="en-retry2">Reintentar</button>
      </div>`;
    $("#en-close", root).addEventListener("click", close);
    $("#en-retry2", root).addEventListener("click", renderSource);
  }

  renderSource();
}
