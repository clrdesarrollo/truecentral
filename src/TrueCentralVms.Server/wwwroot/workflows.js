// CLR TrueCentral VMS — panel: automatizaciones (workflows).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
//
// Aquí viven el listado, las alertas, el historial, el correo saliente y los
// sonidos. El EDITOR es un diagrama de flujo aparte (workflow-editor.js,
// ruta #/workflows/edit); lo único compartido con él es CÓMO se pinta la
// configuración de cada acción (WF_FIELDS + wfFieldHtml + wfReadField).
"use strict";

let workflowsTimer = null;
let wfCatalogCache = null;
let wfCamerasCache = null;

async function wfCatalog() {
  wfCatalogCache ??= await Api.get("/api/workflows/catalog");
  return wfCatalogCache;
}
let wfSpeakersCache = null;
async function wfSpeakers() {
  wfSpeakersCache ??= await Api.get("/api/workflows/speakers");
  return wfSpeakersCache;
}

async function wfCameras() {
  wfCamerasCache ??= await Api.get("/api/workflows/cameras");
  return wfCamerasCache;
}

const WF_KINDS = [["Alarm", "Alarma"], ["ZoneTriggered", "Sensor interrumpido"], ["Restore", "Restauración"],
  ["Arm", "Armado"], ["Disarm", "Desarmado"], ["Bypass", "Anulación"], ["Trouble", "Falla"],
  ["System", "Sistema"], ["Info", "Información"]];
const WF_SEVERITIES = [["Critical", "Crítica"], ["Warning", "Advertencia"], ["Info", "Informativa"]];
const WF_STATUSES = [["Offline", "Sin conexión"], ["AuthFailed", "Credenciales rechazadas"], ["Online", "En línea"]];
const WF_SOURCES = [["panel", "Informado por el panel"], ["poll", "Detectado por sondeo"], ["vms", "Orden desde el VMS"]];
const WF_DAYS = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];

const WF_EMAIL_BODY =
  "{tipo}: {evento}\n\n" +
  "Equipo: {equipo}\n" +
  "Fecha y hora: {fechahora}\n\n" +
  "Aviso automático de CLR TrueCentral VMS ({servidor}) — automatización «{workflow}».";

// Cómo se pinta la configuración de cada acción. `when` oculta campos según
// otro campo (el modo del parlante, el tipo de autenticación...).
const WF_FIELDS = {
  snapshot: [
    { k: "channelIds", t: "cameras", label: "Cámaras a capturar" },
    { k: "count", t: "number", label: "Fotos por cámara", def: 1, min: 1, max: 5 },
    { k: "intervalSeconds", t: "number", label: "Segundos entre fotos", def: 2, min: 0, max: 30 },
  ],
  email: [
    { k: "to", t: "text", label: "Para", placeholder: "guardia@empresa.cl, jefe@empresa.cl" },
    { k: "cc", t: "text", label: "Copia (opcional)" },
    { k: "subject", t: "text", label: "Asunto", def: "{tipo}: {evento}" },
    { k: "body", t: "textarea", label: "Mensaje", def: WF_EMAIL_BODY, rows: 8 },
    { k: "attachSnapshots", t: "check", label: "Adjuntar las fotos capturadas antes en esta ejecución", def: true },
  ],
  ftp: [
    { k: "host", t: "text", label: "Servidor FTP", placeholder: "ftp.empresa.cl" },
    { k: "port", t: "number", label: "Puerto", def: 21, min: 1, max: 65535 },
    { k: "security", t: "select", label: "Seguridad", def: "None",
      options: [["None", "Sin cifrar (FTP)"], ["Explicit", "FTPS explícito (AUTH TLS)"], ["Implicit", "FTPS implícito"]] },
    { k: "username", t: "text", label: "Usuario" },
    { k: "secret", t: "password", label: "Contraseña" },
    { k: "remoteDirectory", t: "text", label: "Carpeta remota", def: "/alarmas/{fecha}",
      help: "Admite marcas: se crea sola si no existe." },
    { k: "includeReport", t: "check", label: "Subir también un informe de texto con el evento" },
    { k: "allowInvalidCertificate", t: "check", label: "Aceptar certificado no verificable (FTPS con certificado propio)" },
  ],
  http: [
    { k: "method", t: "select", label: "Método", def: "POST",
      options: [["POST", "POST"], ["GET", "GET"], ["PUT", "PUT"], ["PATCH", "PATCH"], ["DELETE", "DELETE"]], rerender: true },
    { k: "url", t: "text", label: "URL", placeholder: "https://sistema.empresa.cl/api/alarmas" },
    { k: "contentType", t: "text", label: "Tipo de contenido", def: "application/json",
      when: (c) => (c.method || "POST") !== "GET" },
    { k: "body", t: "textarea", label: "Cuerpo", rows: 5, when: (c) => (c.method || "POST") !== "GET",
      placeholder: '{"equipo":"{equipo}","evento":"{evento}","hora":"{fechahora}"}' },
    { k: "headers", t: "textarea", label: "Cabeceras (una por línea: Nombre: valor)", rows: 2 },
    { k: "auth", t: "select", label: "Autenticación", def: "None",
      options: [["None", "Ninguna"], ["Basic", "Básica"], ["Digest", "Digest"]], rerender: true },
    { k: "username", t: "text", label: "Usuario", when: (c) => (c.auth || "None") !== "None" },
    { k: "secret", t: "password", label: "Contraseña", when: (c) => (c.auth || "None") !== "None" },
    { k: "timeoutSeconds", t: "number", label: "Tiempo máximo de espera (s)", def: 15, min: 1, max: 120 },
    { k: "allowInvalidCertificate", t: "check", label: "Aceptar certificado no verificable (HTTPS)" },
  ],
  speaker: [
    { k: "mode", t: "select", label: "Tipo de parlante", def: "inventory", rerender: true,
      options: [["inventory", "Parlantes del inventario (módulo Parlantes IP)"], ["hikvision", "Hikvision suelto (audio bidireccional ISAPI)"],
        ["axis", "Axis (clip por VAPIX)"], ["http", "Otra marca (URL que gatilla el mensaje)"]] },
    // --- Parlantes del inventario ---
    { k: "speakerIds", t: "speakers", label: "Parlantes", when: (c) => (c.mode || "inventory") === "inventory",
      help: "Se marcan uno o varios; si elige varios, los sonidos del servidor suenan sincronizados." },
    { k: "group", t: "text", label: "O bien todos los parlantes del grupo", placeholder: "Bodega, Perímetro…",
      when: (c) => (c.mode || "inventory") === "inventory",
      help: "Nombre de grupo tal como está en el mantenedor de parlantes; se suma a los marcados arriba." },
    { k: "source", t: "select", label: "Qué reproducir", def: "server", rerender: true,
      when: (c) => (c.mode || "inventory") === "inventory",
      options: [["server", "Sonido del servidor (Sonidos)"], ["library", "Audio de la biblioteca del parlante"], ["tts", "Texto leído en voz alta (TTS del parlante)"]] },
    { k: "audio", t: "audio", label: "Sonido a reproducir",
      when: (c) => (c.mode || "inventory") === "inventory" && (c.source || "server") === "server" },
    { k: "libraryName", t: "text", label: "Nombre del audio en el parlante", placeholder: "Siren, RestrictAreaKeepAway…",
      when: (c) => (c.mode || "inventory") === "inventory" && c.source === "library",
      help: "Se busca por nombre en la biblioteca de cada parlante (con o sin extensión)." },
    { k: "text", t: "textarea", label: "Texto a leer (máximo 100 caracteres)", rows: 2,
      placeholder: "Atención: intruso detectado en {zona}. Retírese del lugar.",
      when: (c) => (c.mode || "inventory") === "inventory" && c.source === "tts",
      help: "Admite las marcas {zona}, {panel}, {evento}… El parlante genera la voz y la guarda; un mismo texto se reutiliza." },
    { k: "language", t: "select", label: "Idioma de la voz", def: "spanish",
      when: (c) => (c.mode || "inventory") === "inventory" && c.source === "tts",
      options: [["spanish", "Español"], ["english", "Inglés"], ["brazilianPortuguese", "Portugués (Brasil)"], ["french", "Francés"]] },
    { k: "voice", t: "select", label: "Voz", def: "female",
      when: (c) => (c.mode || "inventory") === "inventory" && c.source === "tts",
      options: [["female", "Femenina"], ["male", "Masculina"]] },
    { k: "repeat", t: "number", label: "Repeticiones", def: 1, min: 1, max: 5,
      when: (c) => (c.mode || "inventory") === "inventory" && (c.source || "server") === "server" },
    // --- Equipo suelto por dirección (modos heredados) ---
    { k: "host", t: "text", label: "Dirección del parlante", placeholder: "192.168.1.90", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "port", t: "number", label: "Puerto", def: 80, min: 1, max: 65535, when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "useHttps", t: "check", label: "Usar HTTPS", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "username", t: "text", label: "Usuario", def: "admin", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "secret", t: "password", label: "Contraseña", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "audio", t: "audio", label: "Sonido a reproducir", when: (c) => c.mode === "hikvision" },
    { k: "channel", t: "number", label: "Canal de audio", def: 1, min: 1, max: 8, when: (c) => c.mode === "hikvision" },
    { k: "clip", t: "number", label: "Número de clip en el parlante", def: 1, min: 0, max: 99, when: (c) => c.mode === "axis" },
    { k: "volume", t: "number", label: "Volumen (%)", def: 50, min: 0, max: 100, when: (c) => c.mode === "axis" },
    { k: "repeat", t: "number", label: "Repeticiones", def: 1, min: 1, max: 5, when: (c) => ["hikvision", "axis"].includes(c.mode) },
    { k: "url", t: "text", label: "URL", placeholder: "http://192.168.1.90/api/play?msg=1", when: (c) => c.mode === "http" },
    { k: "method", t: "select", label: "Método", def: "GET", options: [["GET", "GET"], ["POST", "POST"]], when: (c) => c.mode === "http" },
  ],
  notify: [
    { k: "title", t: "text", label: "Título", def: "{tipo}: {equipo}" },
    { k: "message", t: "text", label: "Mensaje", def: "{evento} · {fechahora}" },
    { k: "severity", t: "select", label: "Importancia", def: "Warning",
      options: [["Critical", "Crítica"], ["Warning", "Advertencia"], ["Info", "Informativa"]] },
    { k: "attachSnapshot", t: "check", label: "Mostrar la foto capturada en el aviso", def: true },
    { k: "requireAck", t: "check", label: "Exigir que un operador se dé por enterado", def: true,
      help: "El aviso queda en pantalla y en la lista de alertas hasta que alguien lo confirme; se registra quién y cuándo." },
    { k: "channelIds", t: "cameras", label: "Cámaras del video en vivo de la ventana de alarma",
      help: "Si no marca ninguna, se usan las que capturaron foto en la misma ejecución." },
    { k: "sound", t: "audio", label: "Alarma sonora en el equipo del operador", optional: true, system: true,
      def: "", rerender: true, help: "Los sonidos son los mismos que se cargan con el botón «Sonidos»." },
    { k: "soundRepeat", t: "number", label: "Repeticiones del sonido", def: 1, min: 0, max: 5,
      when: (c) => !!c.sound,
      help: "0 = suena sin parar hasta que un operador confirme la alerta, la silencie o cierre la ventana." },
  ],
  door: [
    { k: "command", t: "select", label: "Orden", def: "Open",
      options: [["Open", "Abrir (pulso: abre y se vuelve a cerrar)"], ["RemainOpen", "Mantener abierta hasta nueva orden"],
        ["RemainLocked", "Bloquear (no entra nadie, ni con credencial)"], ["Close", "Cerrar / volver a normal"]] },
    { k: "doorIds", t: "doors", label: "Puertas" },
  ],
  panel: [
    { k: "panelId", t: "panel", label: "Panel de alarma", rerender: true },
    { k: "areaNumber", t: "area", label: "Área", def: 0 },
    { k: "command", t: "select", label: "Orden", def: "Arm", rerender: true,
      options: [["Arm", "Armar"], ["Disarm", "Desarmar"], ["ClearAlarm", "Borrar / silenciar la alarma"]] },
    { k: "mode", t: "select", label: "Modo de armado", def: "Away", when: (c) => (c.command || "Arm") === "Arm",
      options: [["Away", "Total (fuera de casa)"], ["Stay", "Parcial (en casa / perimetral)"]] },
  ],
  "ptz-preset": [
    { k: "channelId", t: "ptzcamera", label: "Cámara PTZ" },
    { k: "preset", t: "number", label: "Preset (1–300)", def: 1, min: 1, max: 300,
      help: "El preset se guarda en la propia cámara (desde la vista en vivo del cliente o su web)." },
  ],
};

// --- Vista previa de sonidos ------------------------------------------------
// Suena en el navegador del que configura (no en el equipo del operador): el
// archivo se pide a la API con el token en la URL, que es lo que admite un
// elemento <audio>. Solo una reproducción a la vez.
let wfPreview = null;

function wfStopPreview() {
  if (!wfPreview) return;
  const { audio, button } = wfPreview;
  wfPreview = null;
  try { audio.pause(); } catch { /* ya terminó */ }
  if (button) button.textContent = button.dataset.playLabel || "▶";
}

function wfTogglePreview(name, button) {
  const playing = wfPreview && wfPreview.name === name;
  wfStopPreview();
  if (playing || !name) return;   // segundo clic = detener

  const audio = new Audio(
    `/api/workflows/audio/${encodeURIComponent(name)}/file?access_token=${encodeURIComponent(Api.token || "")}`);
  wfPreview = { audio, button, name };
  button.dataset.playLabel ??= button.textContent;
  button.textContent = "■";
  audio.addEventListener("ended", wfStopPreview);
  audio.addEventListener("error", () => {
    wfStopPreview();
    toast("El navegador no pudo reproducir ese sonido.", true);
  });
  audio.play().catch(() => {
    wfStopPreview();
    toast("El navegador no pudo reproducir ese sonido.", true);
  });
}

// ---------------------------------------------------------------------------
// Listado
// ---------------------------------------------------------------------------

async function renderWorkflows() {
  $("#page-title").textContent = "Automatizaciones";
  let workflows, catalog;
  try {
    [workflows, catalog] = await Promise.all([Api.get("/api/workflows"), wfCatalog()]);
  } catch (err) {
    $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }

  const isAdmin = Api.role === "Admin";
  const actionLabel = (key) => catalog.actions.find((a) => a.key === key)?.label ?? key;
  const triggerLabel = (key) => catalog.triggers.find((t) => t.key === key)?.label ?? key;
  const stepsOf = (w) => (w.graph?.nodes || []).filter((n) => n.kind !== "trigger").length;

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Automatizaciones <span class="muted" style="font-weight:normal;font-size:12px">(cuando pasa algo, el servidor actúa solo)</span></h3>
      <div class="row-actions">
        ${isAdmin ? `<button class="btn ghost" id="btn-wf-smtp">Servidor de correo</button>
        <button class="btn ghost" id="btn-wf-audio">Sonidos</button>
        <button class="btn" id="btn-wf-new">Nueva automatización</button>` : ""}
      </div>
    </div>
    ${!catalog.smtpConfigured ? `<div class="info-box" style="margin-bottom:12px">
      Aún no hay <b>servidor de correo</b> configurado: las acciones de correo fallarán hasta que se configure${isAdmin ? " (botón <b>Servidor de correo</b>)" : ""}.
    </div>` : ""}
    ${workflows.length === 0 ? `
      <div class="info-box">
        Todavía no hay automatizaciones. ${isAdmin
          ? "Con <b>Nueva automatización</b> se abre el editor de diagrama: parta de un disparador (una alarma de panel, una cámara caída, una patente leída, un acceso, una analítica de video, una hora del día o una llamada externa), agregue condiciones sí/no y conecte acciones: foto, correo, FTP, HTTP, parlante, aviso a los operadores, abrir puerta, armar/desarmar, mover un PTZ."
          : "Un administrador debe crearlas."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Cuándo</th><th>Filtro</th><th>Qué hace</th>
          <th>Pasos</th><th>Espera</th><th>Ejecuciones</th><th>Última</th><th>Estado</th><th></th>
        </tr></thead>
        <tbody>
          ${workflows.map((w) => `
            <tr data-id="${w.id}">
              <td>${esc(w.name)}${w.description ? `<div class="muted" style="font-size:11px">${esc(w.description)}</div>` : ""}</td>
              <td>${esc(triggerLabel(w.triggerType))}</td>
              <td class="muted" style="font-size:12px;max-width:260px">${esc(wfConditionsText(w))}</td>
              <td><div class="chip-row" style="margin:0">${w.actions.map((a) =>
                `<span class="chip" ${a.enabled ? "" : 'style="opacity:.5"'}>${esc(actionLabel(a.type))}</span>`).join("")}</div></td>
              <td class="muted">${stepsOf(w)}</td>
              <td class="muted">${w.cooldownSeconds > 0 ? `${w.cooldownSeconds} s` : "—"}</td>
              <td>${w.runCount}</td>
              <td class="muted">${w.lastRunAt ? wfDate(w.lastRunAt) : "—"}</td>
              <td>${w.enabled ? `<span class="tag on">Activa</span>` : `<span class="tag off">Pausada</span>`}</td>
              <td class="row-actions">
                ${isAdmin ? `<button class="btn ghost btn-wf-test" title="Ejecuta las acciones de verdad con un evento de ejemplo">Probar</button>
                <button class="btn ghost btn-wf-edit">Abrir</button>
                <button class="btn danger btn-wf-delete">Eliminar</button>` : `<button class="btn ghost btn-wf-edit">Ver</button>`}
              </td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}
    <h3 style="margin-top:22px">Alertas <span class="muted" style="font-weight:normal;font-size:12px">(quién se dio por enterado y cuándo)</span></h3>
    <div id="wf-alerts"><div class="info-box">Cargando…</div></div>
    <h3 style="margin-top:22px">Últimas ejecuciones</h3>
    <div id="wf-runs"><div class="info-box">Cargando…</div></div>`;

  $("#btn-wf-new")?.addEventListener("click", () => { location.hash = "#/workflows/edit"; });
  $("#btn-wf-smtp")?.addEventListener("click", wfSmtpModal);
  $("#btn-wf-audio")?.addEventListener("click", wfAudioModal);
  $$("#view .btn-wf-edit").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    location.hash = `#/workflows/edit?id=${id}`;
  }));
  $$("#view .btn-wf-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const workflow = workflows.find((w) => w.id === id);
    if (!confirm(`¿Eliminar la automatización "${workflow.name}"? El historial de ejecuciones se conserva.`)) return;
    try {
      await Api.delete(`/api/workflows/${id}`);
      toast("Automatización eliminada.");
      renderWorkflows();
    } catch (err) { toast(err.error, true); }
  }));
  $$("#view .btn-wf-test").forEach((b) => b.addEventListener("click", async (e) => {
    const button = e.target;
    const id = Number(button.closest("tr").dataset.id);
    if (!confirm("La prueba ejecuta las acciones DE VERDAD (envía el correo, sube el archivo, abre la puerta, hace sonar el parlante) con un evento de ejemplo. ¿Continuar?")) return;
    button.disabled = true;
    button.textContent = "Probando…";
    try {
      const run = await Api.post(`/api/workflows/${id}/test`);
      toast(run.success ? "Prueba ejecutada correctamente." : "La prueba terminó con errores.", !run.success);
      wfRunModal(run);
      wfLoadRuns();
    } catch (err) {
      toast(err.error, true);
    } finally {
      button.disabled = false;
      button.textContent = "Probar";
    }
  }));

  wfLoadAlerts();
  wfLoadRuns();
  // Las ejecuciones y las alertas ocurren solas: refrescar cada 10 s.
  clearInterval(workflowsTimer);
  workflowsTimer = setInterval(() => {
    if (location.hash !== "#/workflows") return;
    wfLoadAlerts();
    wfLoadRuns();
  }, 10000);
}

/// Resumen legible del filtro del disparador (columna del listado).
function wfConditionsText(workflow) {
  const text = wfeConditionsSummary(workflow.conditions || {}, workflow.triggerType);
  return text || "Cualquier evento";
}

function wfDate(value) {
  const date = new Date(value);
  return date.toLocaleString("es-CL", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

// ---------------------------------------------------------------------------
// Alertas y acuse de recibo
// ---------------------------------------------------------------------------

function wfAgo(from, to) {
  const seconds = Math.max(0, Math.round((new Date(to) - new Date(from)) / 1000));
  if (seconds < 60) return `${seconds} s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ${seconds % 60} s`;
  return `${Math.floor(seconds / 3600)} h ${Math.floor((seconds % 3600) / 60)} min`;
}

async function wfLoadAlerts() {
  const container = $("#wf-alerts");
  if (!container) return;
  let data;
  try { data = await Api.get("/api/workflows/alerts?take=25"); }
  catch (err) { container.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  if (data.total === 0) {
    container.innerHTML = `<div class="info-box">Todavía no se ha emitido ninguna alerta.</div>`;
    return;
  }
  container.innerHTML = `
    ${data.pending > 0 ? `<div class="error-box" style="margin-bottom:10px">
      <b>${data.pending} alerta(s) sin confirmar.</b> Mientras nadie se dé por enterado quedan pendientes y a la vista.
    </div>` : ""}
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>Emitida</th><th>Alerta</th><th>Qué la disparó</th><th>Estado</th><th>Confirmó</th><th></th>
      </tr></thead>
      <tbody>
        ${data.items.map((a) => `
          <tr data-alert="${a.id}">
            <td class="muted">${wfDate(a.raisedAt)}</td>
            <td>${esc(a.title)}<div class="muted" style="font-size:11px">${esc(a.message)}</div></td>
            <td class="muted" style="max-width:280px">${esc(a.triggerSummary)}</td>
            <td>${!a.requiresAck ? `<span class="tag operator">Informativa</span>`
              : a.acknowledgedAt ? `<span class="tag on">Confirmada</span>`
              : `<span class="tag off">PENDIENTE</span>`}</td>
            <td class="muted">${a.acknowledgedAt
              ? `${esc(a.acknowledgedBy)} · ${wfDate(a.acknowledgedAt)}<div style="font-size:11px">a los ${wfAgo(a.raisedAt, a.acknowledgedAt)} · ${a.acknowledgedFrom === "client" ? "cliente de escritorio" : "panel web"}</div>`
              : "—"}</td>
            <td class="row-actions">
              ${a.imagePath ? `<button class="btn ghost btn-alert-photo" title="Ver la foto">Foto</button>` : ""}
              ${a.requiresAck && !a.acknowledgedAt ? `<button class="btn btn-alert-ack">Enterado</button>` : ""}
            </td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    <div class="audit-pager"><span class="muted">${data.total} alerta(s) registradas.</span></div>`;

  $$("#wf-alerts .btn-alert-ack").forEach((b) => b.addEventListener("click", async (e) => {
    const id = e.target.closest("tr").dataset.alert;
    b.disabled = true;
    try {
      const alert = await Api.post(`/api/workflows/alerts/${id}/ack`);
      toast(alert.acknowledgedBy === Api.username
        ? "Alerta confirmada; quedó registrado a su nombre."
        : `La alerta ya la había confirmado ${alert.acknowledgedBy}.`);
      wfLoadAlerts();
    } catch (err) {
      toast(err.error, true);
      b.disabled = false;
    }
  }));
  $$("#wf-alerts .btn-alert-photo").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.alert);
    const alert = data.items.find((a) => a.id === id);
    const token = encodeURIComponent(Api.token || "");
    const url = `/api/workflows/files/${alert.imagePath.split("/").map(encodeURIComponent).join("/")}?access_token=${token}`;
    openModal(`
      <h3>${esc(alert.title)}</h3>
      <div class="muted" style="margin-bottom:10px">${esc(alert.message)}</div>
      <div class="wf-thumbs"><a href="${url}" target="_blank"><img src="${url}" alt="Captura"></a></div>
      <div class="modal-actions"><button class="btn ghost" type="button" id="wf-photo-close">Cerrar</button></div>`, "wider");
    $("#wf-photo-close").addEventListener("click", closeModal);
  }));
}

// ---------------------------------------------------------------------------
// Historial de ejecuciones
// ---------------------------------------------------------------------------

async function wfLoadRuns() {
  const container = $("#wf-runs");
  if (!container) return;
  let data;
  try { data = await Api.get("/api/workflows/runs?take=25"); }
  catch (err) { container.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  if (data.total === 0) {
    container.innerHTML = `<div class="info-box">Aún no se ha ejecutado ninguna automatización.</div>`;
    return;
  }
  container.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Fecha</th><th>Automatización</th><th>Qué la disparó</th><th>Pasos</th><th>Resultado</th><th>Origen</th></tr></thead>
      <tbody>
        ${data.items.map((r) => `
          <tr class="audit-row" data-run="${r.id}">
            <td class="muted">${wfDate(r.startedAt)}</td>
            <td>${esc(r.workflowName)}</td>
            <td class="muted" style="max-width:320px">${esc(r.triggerSummary)}</td>
            <td>${r.steps.length}${r.steps.some((s) => s.files?.length) ? ` <span class="chip">con fotos</span>` : ""}</td>
            <td>${r.success ? `<span class="tag on">OK</span>` : `<span class="tag off">Con errores</span>`}</td>
            <td class="muted">${esc(r.startedBy)}</td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    <div class="audit-pager"><span class="muted">${data.total} ejecución(es) en el historial.</span></div>`;

  $$("#wf-runs tr[data-run]").forEach((row) => row.addEventListener("click", async () => {
    try { wfRunModal(await Api.get(`/api/workflows/runs/${row.dataset.run}`)); }
    catch (err) { toast(err.error, true); }
  }));
}

function wfRunModal(run) {
  const token = encodeURIComponent(Api.token || "");
  openModal(`
    <h3>Ejecución de «${esc(run.workflowName)}»</h3>
    <div class="audit-detail-grid">
      <div class="muted">Fecha</div><div>${wfDate(run.startedAt)}</div>
      <div class="muted">Disparo</div><div>${esc(run.triggerSummary)}</div>
      <div class="muted">Origen</div><div>${esc(run.startedBy)}</div>
      <div class="muted">Resultado</div><div>${run.success ? `<span class="tag on">Todos los pasos se ejecutaron</span>` : `<span class="tag off">${esc(run.error || "Con errores")}</span>`}</div>
    </div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>#</th><th>Paso</th><th>Resultado</th><th>Detalle</th><th>Duración</th></tr></thead>
      <tbody>
        ${run.steps.map((s) => `
          <tr>
            <td>${s.order}</td>
            <td>${esc(s.label)}</td>
            <td>${s.success ? `<span class="tag on">OK</span>` : `<span class="tag off">Error</span>`}</td>
            <td style="max-width:420px">${esc(s.detail || "")}</td>
            <td class="muted">${(s.elapsedMs / 1000).toFixed(1)} s</td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    ${run.steps.some((s) => s.files?.length) ? `
      <div class="field" style="margin-top:12px"><label>Fotos capturadas</label>
        <div class="wf-thumbs">
          ${run.steps.flatMap((s) => s.files || []).map((f) =>
            `<a href="/api/workflows/files/${f.split("/").map(encodeURIComponent).join("/")}?access_token=${token}" target="_blank">
               <img src="/api/workflows/files/${f.split("/").map(encodeURIComponent).join("/")}?access_token=${token}" alt="Captura">
             </a>`).join("")}
        </div>
      </div>` : ""}
    <div class="modal-actions"><button class="btn ghost" type="button" id="wf-run-close">Cerrar</button></div>`, "wider");
  $("#wf-run-close").addEventListener("click", closeModal);
}

// ---------------------------------------------------------------------------
// Campos de configuración de las acciones (compartidos con el editor)
// ---------------------------------------------------------------------------

/// HTML de un campo de configuración de acción. `lists` trae las listas del
/// servidor: { cameras, audio, speakers, doors, panels }.
function wfFieldHtml(action, field, lists) {
  const { cameras = [], audio = [], speakers = [], doors = [], panels = [] } = lists;
  const value = action.config[field.k];
  const current = value === undefined ? field.def : value;
  const attributes = `data-key="${esc(field.k)}" data-type="${esc(field.t)}"`;
  const help = field.help ? `<div class="muted" style="font-size:11.5px;margin-top:3px">${esc(field.help)}</div>` : "";

  switch (field.t) {
    case "check":
      return `<label class="checkbox-row"><input type="checkbox" ${attributes} ${current ? "checked" : ""}> ${esc(field.label)}</label>`;
    case "textarea":
      return `<div class="field"><label>${esc(field.label)}</label>
        <textarea ${attributes} rows="${field.rows || 4}" placeholder="${esc(field.placeholder || "")}">${esc(current ?? "")}</textarea>${help}</div>`;
    case "number":
      return `<div class="field"><label>${esc(field.label)}</label>
        <input type="number" ${attributes} min="${field.min ?? 0}" max="${field.max ?? 999999}" value="${current ?? field.def ?? 0}">${help}</div>`;
    case "password":
      return `<div class="field"><label>${esc(field.label)}${action.hasSecret ? " (guardada; vacío = no cambiar)" : ""}</label>
        <input type="password" autocomplete="new-password" ${attributes} value="${esc(action.secret || "")}">${help}</div>`;
    case "select":
      return `<div class="field"><label>${esc(field.label)}</label>
        <select ${attributes}>${field.options.map(([v, l]) =>
          `<option value="${esc(v)}" ${String(current ?? field.def) === String(v) ? "selected" : ""}>${esc(l)}</option>`).join("")}</select>${help}</div>`;
    case "cameras": {
      const selected = (current || []).map(String);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${cameras.length === 0 ? `<div class="muted">No hay cámaras configuradas.</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${cameras.map((c) => `<label class="checkbox-row" ${c.supportsSnapshot ? "" : 'title="El driver de este equipo no captura imágenes"'}>
              <input type="checkbox" data-camera="${c.channelId}" ${selected.includes(String(c.channelId)) ? "checked" : ""} ${c.supportsSnapshot ? "" : "disabled"}>
              ${esc(c.deviceName)} · ${esc(c.channelName)}</label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "ptzcamera": {
      const ptz = cameras.filter((c) => c.supportsPtz);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${ptz.length === 0 ? `<div class="muted">Ninguna cámara informa PTZ.</div>` : `
          <select ${attributes}>
            <option value="">(elija la cámara)</option>
            ${ptz.map((c) => `<option value="${c.channelId}" ${String(current ?? "") === String(c.channelId) ? "selected" : ""}>${esc(c.deviceName)} · ${esc(c.channelName)}</option>`).join("")}
          </select>`}${help}</div>`;
    }
    case "speakers": {
      const selected = (current || []).map(String);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${speakers.length === 0 ? `<div class="muted">No hay parlantes IP configurados (Dispositivos → Parlantes IP).</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${speakers.map((s) => `<label class="checkbox-row" ${s.enabled ? "" : 'title="Parlante desactivado"'}>
              <input type="checkbox" data-speaker="${s.id}" ${selected.includes(String(s.id)) ? "checked" : ""}>
              ${esc(s.name)}${s.groupName ? ` <span class="muted">· ${esc(s.groupName)}</span>` : ""}</label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "doors": {
      const selected = (current || []).map(String);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${doors.length === 0 ? `<div class="muted">No hay puertas (Dispositivos → Control de acceso).</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${doors.map((d) => `<label class="checkbox-row" ${d.enabled ? "" : 'title="Puerta desactivada"'}>
              <input type="checkbox" data-door="${d.doorId}" ${selected.includes(String(d.doorId)) ? "checked" : ""}>
              ${esc(d.deviceName)} · ${esc(d.name)}</label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "panel":
      return `<div class="field"><label>${esc(field.label)}</label>
        ${panels.length === 0 ? `<div class="muted">No hay paneles de alarma configurados.</div>` : `
          <select ${attributes}>
            <option value="">(elija el panel)</option>
            ${panels.map((p) => `<option value="${p.id}" ${String(current ?? "") === String(p.id) ? "selected" : ""}>${esc(p.name)}</option>`).join("")}
          </select>`}${help}</div>`;
    case "area": {
      const panel = panels.find((p) => p.id === Number(action.config.panelId));
      const areas = panel?.areas || [];
      return `<div class="field"><label>${esc(field.label)}</label>
        <select ${attributes}>
          <option value="0" ${!current ? "selected" : ""}>Todas las áreas</option>
          ${areas.map((a) => `<option value="${a.number}" ${Number(current) === a.number ? "selected" : ""}>${a.number} · ${esc(a.name)}</option>`).join("")}
        </select>${help}</div>`;
    }
    case "audio": {
      // Con `optional` la lista permite "sin sonido"; con `system`, el pitido
      // del sistema operativo, que no necesita ningún archivo cargado.
      const extra = [];
      if (field.optional) extra.push(["", "(sin sonido)"]);
      if (field.system) extra.push(["sistema", "Pitido del sistema del equipo"]);
      if (audio.length === 0 && extra.length === 0)
        return `<div class="field"><label>${esc(field.label)}</label>
          <div class="muted">Aún no hay sonidos: súbalos con el botón <b>Sonidos</b> del listado.</div>${help}</div>`;
      const options = extra.concat(audio.map((a) => [a.displayName, a.displayName + (a.ready ? "" : " (sin convertir)")]));
      return `<div class="field"><label>${esc(field.label)}</label>
        <div style="display:flex;gap:8px;align-items:center">
          <select ${attributes} style="flex:1">
            ${options.map(([v, l]) => `<option value="${esc(v)}" ${String(current ?? "") === String(v) ? "selected" : ""}>${esc(l)}</option>`).join("")}
          </select>
          <button type="button" class="btn ghost btn-field-play" data-for="${esc(field.k)}" title="Escuchar el sonido elegido">▶</button>
        </div>${help}</div>`;
    }
    default:
      return `<div class="field"><label>${esc(field.label)}</label>
        <input type="text" ${attributes} value="${esc(current ?? "")}" placeholder="${esc(field.placeholder || "")}">${help}</div>`;
  }
}

/// Lee un campo de la pantalla y lo guarda en la acción.
function wfReadField(action, input) {
  const key = input.dataset.key;
  switch (input.dataset.type) {
    case "check": action.config[key] = input.checked; break;
    case "number": action.config[key] = Number(input.value) || 0; break;
    case "password": action.secret = input.value; break;
    case "cameras":
      action.config[key] = Array.from(input.querySelectorAll("[data-camera]"))
        .filter((c) => c.checked).map((c) => Number(c.dataset.camera));
      break;
    case "speakers":
      action.config[key] = Array.from(input.querySelectorAll("[data-speaker]"))
        .filter((c) => c.checked).map((c) => Number(c.dataset.speaker));
      break;
    case "doors":
      action.config[key] = Array.from(input.querySelectorAll("[data-door]"))
        .filter((c) => c.checked).map((c) => Number(c.dataset.door));
      break;
    case "panel":
    case "area":
    case "ptzcamera":
      action.config[key] = Number(input.value) || 0;
      break;
    default: action.config[key] = input.value; break;
  }
}

// ---------------------------------------------------------------------------
// Servidor de correo saliente
// ---------------------------------------------------------------------------

async function wfSmtpModal() {
  let settings;
  try { settings = await Api.get("/api/workflows/smtp"); }
  catch (err) { toast(err.error, true); return; }

  openModal(`
    <h3>Servidor de correo saliente</h3>
    <div id="wf-smtp-error"></div>
    <form id="wf-smtp-form">
      <label class="checkbox-row"><input type="checkbox" id="smtp-enabled" ${settings.enabled ? "checked" : ""}> Habilitado (las automatizaciones pueden enviar correos)</label>
      <div class="form-grid">
        <div class="field">
          <label>Servidor SMTP</label>
          <input id="smtp-host" value="${esc(settings.host)}" placeholder="smtp.office365.com">
        </div>
        <div class="field">
          <label>Puerto</label>
          <input id="smtp-port" type="number" min="1" max="65535" value="${settings.port}">
        </div>
      </div>
      <div class="field">
        <label>Seguridad</label>
        <select id="smtp-security">
          <option value="StartTls" ${settings.security === "StartTls" ? "selected" : ""}>STARTTLS (normalmente puerto 587)</option>
          <option value="Ssl" ${settings.security === "Ssl" ? "selected" : ""}>TLS implícito (normalmente puerto 465)</option>
          <option value="None" ${settings.security === "None" ? "selected" : ""}>Sin cifrado (servidor interno, puerto 25)</option>
        </select>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Usuario (vacío = sin autenticación)</label>
          <input id="smtp-username" value="${esc(settings.username)}" autocomplete="username">
        </div>
        <div class="field">
          <label>Contraseña${settings.hasPassword ? " (guardada; vacío = no cambiar)" : ""}</label>
          <input id="smtp-password" type="password" autocomplete="new-password">
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección del remitente</label>
          <input id="smtp-from" value="${esc(settings.fromAddress)}" placeholder="vms@empresa.cl">
        </div>
        <div class="field">
          <label>Nombre del remitente</label>
          <input id="smtp-fromname" value="${esc(settings.fromName)}">
        </div>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="smtp-cert" ${settings.allowInvalidCertificate ? "checked" : ""}> Aceptar certificado no verificable (servidor de correo interno)</label>
      <div class="field">
        <label>Enviar un correo de prueba a</label>
        <div class="row-actions" style="display:flex;gap:8px">
          <input id="smtp-test-to" placeholder="usted@empresa.cl" style="flex:1">
          <button class="btn ghost" type="button" id="smtp-test">Probar</button>
        </div>
        <div class="muted" style="font-size:11.5px;margin-top:4px">La prueba usa la configuración ya guardada.</div>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="smtp-cancel">Cerrar</button>
        <button class="btn" type="submit">Guardar</button>
      </div>
    </form>`);

  $("#smtp-cancel").addEventListener("click", closeModal);

  const read = () => ({
    enabled: $("#smtp-enabled").checked,
    host: $("#smtp-host").value.trim(),
    port: Number($("#smtp-port").value) || 587,
    security: $("#smtp-security").value,
    username: $("#smtp-username").value.trim(),
    password: $("#smtp-password").value || null,
    allowInvalidCertificate: $("#smtp-cert").checked,
    fromAddress: $("#smtp-from").value.trim(),
    fromName: $("#smtp-fromname").value.trim(),
  });

  $("#wf-smtp-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      await Api.put("/api/workflows/smtp", read());
      wfCatalogCache = null;   // cambia el aviso de "sin correo configurado"
      toast("Servidor de correo guardado.");
      closeModal();
      renderWorkflows();
    } catch (err) {
      $("#wf-smtp-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });

  $("#smtp-test").addEventListener("click", async () => {
    const to = $("#smtp-test-to").value.trim();
    if (!to) { toast("Indique a qué dirección enviar la prueba.", true); return; }
    const button = $("#smtp-test");
    button.disabled = true;
    button.textContent = "Enviando…";
    $("#wf-smtp-error").innerHTML = "";
    try {
      await Api.post("/api/workflows/smtp/test", { to });
      $("#wf-smtp-error").innerHTML = `<div class="info-box">Correo de prueba enviado a ${esc(to)}.</div>`;
    } catch (err) {
      $("#wf-smtp-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      button.disabled = false;
      button.textContent = "Probar";
    }
  });
}

// ---------------------------------------------------------------------------
// Sonidos para los parlantes IP
// ---------------------------------------------------------------------------

async function wfAudioModal() {
  const draw = async () => {
    let items, catalog;
    try { [items, catalog] = await Promise.all([Api.get("/api/workflows/audio"), wfCatalog()]); }
    catch (err) { toast(err.error, true); return; }

    openModal(`
      <h3>Sonidos para parlantes IP</h3>
      <div id="wf-audio-error"></div>
      ${catalog.ffmpegAvailable ? "" : `<div class="error-box">El servidor no tiene FFmpeg disponible: los sonidos no se pueden convertir al formato que exigen los parlantes.</div>`}
      <div class="info-box">
        Suba un archivo de audio (WAV, MP3...). El servidor lo convierte a G.711 8 kHz mono, que es lo que
        acepta el canal de audio de los equipos Hikvision. Máximo 8 MB; se recomiendan mensajes de pocos segundos.
      </div>
      ${items.length === 0 ? `<div class="muted" style="margin:12px 0">Aún no hay sonidos cargados.</div>` : `
        <div class="table-scroll"><table class="grid">
          <thead><tr><th>Sonido</th><th>Tamaño</th><th>Estado</th><th></th></tr></thead>
          <tbody>
            ${items.map((a) => `
              <tr>
                <td>${esc(a.displayName)}<div class="muted" style="font-size:11px">${esc(a.fileName)}</div></td>
                <td class="muted">${Math.round(a.bytes / 1024)} kB</td>
                <td>${a.ready ? `<span class="tag on">Listo</span>` : `<span class="tag off">Sin convertir</span>`}</td>
                <td class="row-actions">
                  <button class="btn ghost btn-audio-play" data-name="${esc(a.displayName)}" title="Escuchar">▶</button>
                  <button class="btn danger btn-audio-del" data-name="${esc(a.displayName)}">Eliminar</button>
                </td>
              </tr>`).join("")}
          </tbody>
        </table></div>`}
      <div class="field" style="margin-top:12px">
        <label>Agregar un sonido</label>
        <input type="file" id="wf-audio-file" accept="audio/*">
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="wf-audio-close">Cerrar</button>
        <button class="btn" type="button" id="wf-audio-upload">Subir</button>
      </div>`, "wider");

    $("#wf-audio-close").addEventListener("click", () => { wfStopPreview(); closeModal(); });
    $$(".btn-audio-play").forEach((b) => b.addEventListener("click", () => wfTogglePreview(b.dataset.name, b)));
    $$(".btn-audio-del").forEach((b) => b.addEventListener("click", async () => {
      if (!confirm(`¿Eliminar el sonido "${b.dataset.name}"?`)) return;
      wfStopPreview();
      try {
        await Api.delete(`/api/workflows/audio/${encodeURIComponent(b.dataset.name)}`);
        toast("Sonido eliminado.");
        draw();
      } catch (err) { toast(err.error, true); }
    }));

    $("#wf-audio-upload").addEventListener("click", async () => {
      const file = $("#wf-audio-file").files[0];
      if (!file) { toast("Elija un archivo de audio.", true); return; }
      const button = $("#wf-audio-upload");
      button.disabled = true;
      button.textContent = "Subiendo…";
      const form = new FormData();
      form.append("file", file);
      try {
        const response = await fetch("/api/workflows/audio", {
          method: "POST",
          headers: { Authorization: "Bearer " + Api.token },
          body: form,
        });
        const data = await response.json().catch(() => null);
        if (!response.ok) throw { error: (data && data.error) || `Error ${response.status}` };
        toast("Sonido subido y convertido.");
        wfStopPreview();
        draw();
      } catch (err) {
        $("#wf-audio-error").innerHTML = `<div class="error-box">${esc(err.error || "No se pudo subir el sonido.")}</div>`;
      } finally {
        button.disabled = false;
        button.textContent = "Subir";
      }
    });
  };
  await draw();
}
