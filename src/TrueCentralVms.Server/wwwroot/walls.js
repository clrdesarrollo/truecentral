// CLR TrueCentral VMS — panel: decodificadores y muros de video (configuración).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
//
//   · Decodificadores (#/decoders): la lista y los equipos en línea.
//   · Página del decodificador (#/decoders/device?id=N): Conexión, Salidas
//     (resolución, monitor conectado y qué muro usa cada una), Entradas
//     locales y Diagnóstico.
//   · Muros (#/walls): un plano por muro con qué salida alimenta cada
//     monitor. La operación (cámaras, layouts, pantalla completa) vive en
//     Aplicaciones → Videowall.
//   · Editor del muro (#/walls/edit?id=N, o sin id para uno nuevo): tamaño,
//     salidas arrastradas a cada posición y división de cada monitor.
//   · layoutsModal: lo usa también videowall.js.
"use strict";

// ---------------------------------------------------------------------------
// Comunes
// ---------------------------------------------------------------------------
let decoderDriversCache = null;
async function getDecoderDrivers() {
  decoderDriversCache ??= await Api.get("/api/decoders/drivers");
  return decoderDriversCache;
}

let wallsTimer = null;   // app.js lo limpia al cambiar de página

/** Divisiones por omisión cuando el equipo no informa las suyas. */
const WALL_DEFAULT_MODES = [1, 2, 4, 6, 8, 9, 12, 16, 25, 36];

/** Orden al ubicar salidas solas: las digitales primero (BNC es para monitores analógicos). */
const OUTPUT_TYPE_ORDER = { Hdmi: 0, Dvi: 1, Vga: 2, Bnc: 3, Other: 4 };
const sortOutputs = (list) => [...list].sort((a, b) =>
  (OUTPUT_TYPE_ORDER[a.type] ?? 9) - (OUTPUT_TYPE_ORDER[b.type] ?? 9) || a.index - b.index);

const wallPos = (row, col) => `F${row + 1}·C${col + 1}`;

/** Salida reconstruida desde la etiqueta guardada en el muro ("HDMI 3"), sin datos del equipo. */
function outputFromLabel(label, channelNo) {
  const [type = "Other", index = "0"] = String(label || "").split(" ");
  return { channelNo, label, index: Number(index) || 0, type: type.charAt(0) + type.slice(1).toLowerCase(),
    windowModes: [], resolution: null, connected: null, wall: null };
}

function outputBadge(o) {
  const type = String(o?.type || "Other");
  return `<span class="dw-type dw-type-${esc(type.toLowerCase())}">${esc(type === "Other" ? "OTRA" : type.toUpperCase())}</span>`;
}

/** "[HDMI] 3": el conector como insignia y el número de la salida. */
function outputName(o) {
  return `${outputBadge(o)} <b>${esc(o.index || o.label)}</b>`;
}

/** Monitor conectado según el equipo (null = el equipo no lo informa: no se dice nada). */
function outputMonitor(o) {
  if (o?.connected === true) return `<span class="dw-mon on" title="El decodificador detecta un monitor en esta salida">con monitor</span>`;
  if (o?.connected === false) return `<span class="dw-mon off" title="El decodificador no detecta un monitor en esta salida">sin monitor</span>`;
  return "";
}

/** Resumen en vivo de un decodificador: salidas con su estado, entradas y uso por muros. */
const decoderOverview = (id) => Api.get(`/api/decoders/${id}/overview`);

/** Muestra el número de salida en los monitores físicos durante unos segundos. */
async function identifyDecoderOutputs(decoderId, button) {
  if (button) { button.disabled = true; button.dataset.label ??= button.textContent; button.textContent = "Mostrando…"; }
  try {
    const result = await Api.post(`/api/decoders/${decoderId}/identify`);
    toast(`Cada monitor muestra el número de su salida durante ${result.seconds} s.`);
  } catch (err) {
    toast(err.error, true);
  } finally {
    if (button) setTimeout(() => { button.disabled = false; button.textContent = button.dataset.label; }, 1500);
  }
}

// ---------------------------------------------------------------------------
// Decodificadores: lista
// ---------------------------------------------------------------------------
async function renderDecoders() {
  $("#page-title").textContent = "Decodificadores";
  let decoders, walls;
  try { [decoders, walls] = await Promise.all([Api.get("/api/decoders"), Api.get("/api/walls")]); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Perms.can("devices.manage");
  const wallsOf = (id) => walls.filter((w) => w.decoderId === id);
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Decodificadores de muro</h3>
      ${isAdmin ? `<button class="btn" id="btn-decoder-new">Agregar decodificador</button>` : ""}
    </div>
    ${decoders.length === 0 ? `
      <div class="info-box">
        Aún no hay decodificadores. ${isAdmin
          ? "Use <b>Agregar decodificador</b>: al guardar se validan las credenciales contra el equipo y se leen sus salidas físicas y canales de decodificación."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Dirección</th><th>Modelo / serie</th><th>Muros</th><th>Estado</th><th></th>
        </tr></thead>
        <tbody>
          ${decoders.map((d) => {
            const ws = wallsOf(d.id);
            return `
            <tr data-id="${d.id}">
              <td><a class="acd-link" href="#/decoders/device?id=${d.id}" title="Abrir la página del decodificador">${esc(d.name)}</a></td>
              <td class="muted">${esc(d.host)}:${d.port}</td>
              <td>${esc(d.model ?? "—")}</td>
              <td>${ws.length ? ws.map((w) => esc(w.name)).join(", ") : `<span class="muted">sin muro</span>`}</td>
              <td>${d.enabled ? `<span class="tag on">Activo</span>` : `<span class="tag off">Inactivo</span>`}</td>
              <td class="row-actions">
                <a class="btn ghost" href="#/decoders/device?id=${d.id}" title="Conexión, salidas, entradas y diagnóstico">Configurar</a>
                ${isAdmin ? `<button class="btn danger btn-delete">Eliminar</button>` : ""}
              </td>
            </tr>`;
          }).join("")}
        </tbody>
      </table></div>`}
    ${isAdmin ? `
    <div class="toolbar" style="margin-top:28px">
      <h3>Decodificadores en línea <span class="muted" style="font-weight:normal;font-size:12px">(SADP · Dahua en la red local · se actualiza cada 30 s)</span></h3>
      <button class="btn ghost" id="btn-decoder-scan">Buscar</button>
    </div>
    <div id="online-decoders">
      <div class="info-box">Sondeando el segmento de red del servidor… Solo se listan decodificadores y controladores de muro
        (Hikvision DS-64xx/69xx/C10, Dahua NVD); cámaras, grabadores y otros equipos se omiten.</div>
    </div>` : ""}`;

  $("#btn-decoder-new")?.addEventListener("click", () => decoderModal());
  // Decodificadores en línea (mismo sondeo que Fuentes de video, filtrado a decodificadores).
  const knownDecoders = decoders.map((d) => ({ host: d.host }));
  $("#btn-decoder-scan")?.addEventListener("click", () => runDiscovery(knownDecoders));
  if (isAdmin) startDiscoveryPolling(knownDecoders, DECODER_DISCOVERY);

  $$("#view .btn-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const decoder = decoders.find((d) => d.id === id);
    if (!confirm(`¿Eliminar el decodificador "${decoder.name}"?`)) return;
    try {
      await Api.delete(`/api/decoders/${id}`);
      toast("Decodificador eliminado.");
      renderDecoders();
    } catch (err) { toast(err.error, true); }
  }));
}

/** Sondeo de red de la página Decodificadores: solo decodificadores, alta con el modal de decodificador. */
const DECODER_DISCOVERY = {
  container: "#online-decoders",
  button: "#btn-decoder-scan",
  kind: "decoders",
  emptyText: "No se encontraron decodificadores de muro en este segmento de red. " +
    "Los sondeos son de difusión y no cruzan routers ni VPN: solo ven el segmento del servidor. " +
    "Un decodificador fuera de él se agrega a mano con su dirección (Agregar decodificador).",
  onUse: (d) => decoderModal({
    name: d.model || d.ip,
    host: d.ip,
    port: d.commandPort || 8000,
    driverKey: d.driverKey || "hikvision-netsdk",
  }),
};

/** Campos de conexión de un decodificador (alta y pestaña Conexión). */
function decoderFieldsHtml(d, drivers, editing) {
  return `
    <div class="field">
      <label>Nombre</label>
      <input id="dm-name" value="${esc(d.name)}" placeholder="Ej: Decoder sala de control">
    </div>
    <div class="field">
      <label>Driver</label>
      <select id="dm-driver">
        ${drivers.map((x) => `<option value="${esc(x.driverKey)}" ${x.driverKey === d.driverKey ? "selected" : ""}>${esc(x.displayName)}</option>`).join("")}
      </select>
    </div>
    <div class="form-grid">
      <div class="field"><label>Dirección (IP o host)</label><input id="dm-host" value="${esc(d.host)}"></div>
      <div class="field"><label>Puerto SDK</label><input id="dm-port" type="number" min="1" max="65535" value="${d.port}"></div>
      <div class="field"><label>Usuario</label><input id="dm-user" value="${esc(d.username)}"></div>
      <div class="field">
        <label>Contraseña</label>
        <input id="dm-pass" type="password" placeholder="${editing ? "sin cambios" : ""}" autocomplete="new-password">
      </div>
    </div>
    <div class="field"><label>Notas</label><input id="dm-notes" value="${esc(d.notes ?? "")}"></div>
    <div class="checkbox-row"><input id="dm-enabled" type="checkbox" ${d.enabled ? "checked" : ""}><label for="dm-enabled">Activo</label></div>`;
}

function decoderFieldsBody() {
  return {
    name: $("#dm-name").value.trim(),
    driverKey: $("#dm-driver").value,
    host: $("#dm-host").value.trim(),
    port: Number($("#dm-port").value) || 8000,
    username: $("#dm-user").value.trim(),
    password: $("#dm-pass").value || null,
    enabled: $("#dm-enabled").checked,
    notes: $("#dm-notes").value.trim() || null,
  };
}

/** Alta de un decodificador; al guardar abre su página en la pestaña Salidas. */
async function decoderModal(prefill = {}) {
  let drivers;
  try { drivers = await getDecoderDrivers(); }
  catch (err) { toast(err.error, true); return; }

  const d = { name: prefill.name ?? "", driverKey: prefill.driverKey ?? drivers[0]?.driverKey ?? "hikvision-netsdk",
    host: prefill.host ?? "", port: prefill.port ?? 8000, username: "admin", enabled: true, notes: "" };

  openModal(`
    <h3>Nuevo decodificador</h3>
    ${decoderFieldsHtml(d, drivers, false)}
    <div id="dm-error"></div>
    <p class="muted" style="font-size:12px">Al guardar se conecta con el equipo para validar las credenciales y leer sus capacidades.</p>
    <div class="modal-actions">
      <button class="btn ghost" id="dm-cancel">Cancelar</button>
      <button class="btn" id="dm-save">Guardar</button>
    </div>`);

  $("#dm-cancel").addEventListener("click", closeModal);
  $("#dm-save").addEventListener("click", async () => {
    const save = $("#dm-save");
    save.disabled = true;
    save.textContent = "Conectando…";
    try {
      const result = await Api.post("/api/decoders", decoderFieldsBody());
      closeModal();
      toast("Decodificador guardado.");
      location.hash = `#/decoders/device?id=${result.decoder.id}&tab=outputs`;
    } catch (err) {
      $("#dm-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
      save.textContent = "Guardar";
    }
  });
}

// ---------------------------------------------------------------------------
// Página del decodificador (#/decoders/device?id=N&tab=outputs)
// ---------------------------------------------------------------------------
const DCD_TABS = [
  { key: "connection", label: "Conexión" },
  { key: "outputs", label: "Salidas" },
  { key: "inputs", label: "Entradas" },
  { key: "diagnostics", label: "Diagnóstico" },
];

let dcdState = null;

async function renderDecoderPage() {
  $("#page-title").textContent = "Decodificadores";
  const params = new URLSearchParams(location.hash.split("?")[1] || "");
  const id = Number(params.get("id"));
  if (!(id > 0)) { location.hash = "#/decoders"; return; }
  const back = `<div class="acd-back"><a class="btn ghost small" href="#/decoders">← Volver a la lista</a></div>`;
  $("#view").innerHTML = `${back}<div class="muted">Cargando el decodificador…</div>`;
  let decoder, drivers;
  try { [decoder, drivers] = await Promise.all([Api.get(`/api/decoders/${id}`), getDecoderDrivers()]); }
  catch (err) {
    $("#view").innerHTML = `${back}<div class="error-box">${esc(err.status === 404 ? "Ese decodificador ya no existe." : err.error)}</div>`;
    return;
  }
  const tab = DCD_TABS.some((t) => t.key === params.get("tab")) ? params.get("tab") : "connection";
  dcdState = { id, decoder, drivers, tab, overview: null, loading: null };
  dcdDrawPage();
  dcdLoadOverview();   // la conexión con el equipo corre aparte: la página no la espera
}

/** Lee (o vuelve a leer) el estado en vivo del equipo y repinta lo que depende de él. */
function dcdLoadOverview(force = false) {
  const state = dcdState;
  if (state.loading && !force) return state.loading;
  if (force) state.overview = null;
  dcdDrawStatus();
  const run = (async () => {
    let overview;
    try { overview = await decoderOverview(state.id); }
    catch (err) { overview = { online: false, error: err.error, outputs: [], inputs: null, walls: [], canIdentify: false }; }
    if (state.loading !== run) return;   // una lectura más nueva la reemplazó
    state.overview = overview;
    state.loading = null;
    if (dcdState !== state || !$("#dcd-body")) return;   // se cambió de página
    dcdDrawStatus();
    // La pestaña Conexión tiene un formulario: solo se repinta su resumen.
    if (state.tab === "connection") dcdDrawSummary();
    else if (state.tab !== "diagnostics") dcdDrawTab();
  })();
  state.loading = run;
  return run;
}

function dcdDrawPage() {
  const { decoder: d, tab, drivers } = dcdState;
  $("#page-title").textContent = `Decodificadores · ${d.name}`;
  const driver = drivers.find((x) => x.driverKey === d.driverKey)?.displayName ?? d.driverKey;
  $("#view").innerHTML = `
    <div class="acd-back"><a class="btn ghost small" href="#/decoders">← Volver a la lista</a></div>
    <div class="acd-head">
      <div class="acd-title">
        <h3>${esc(d.name)}${d.enabled ? "" : ` <span class="tag operator" title="Desactivado">pausado</span>`}</h3>
        <div class="muted">${[driver, d.model, `${d.host}:${d.port}`].filter(Boolean).map(esc).join(" · ")}</div>
      </div>
      <div class="acd-status" id="dcd-status"></div>
    </div>
    <div class="res-tabs" role="tablist">
      ${DCD_TABS.map((t) => `
        <button type="button" role="tab" class="res-tab${t.key === tab ? " on" : ""}" data-tab="${t.key}"
                aria-selected="${t.key === tab}">${t.label}</button>`).join("")}
    </div>
    <div id="dcd-body" class="res-tab-body"></div>`;
  $$("#view .res-tab").forEach((b) => b.addEventListener("click", () => dcdTab(b.dataset.tab)));
  dcdDrawStatus();
  dcdDrawTab();
}

function dcdDrawStatus() {
  const box = $("#dcd-status");
  if (!box) return;
  const o = dcdState.overview;
  box.innerHTML = `
    ${!o ? `<span class="muted">Conectando con el equipo…</span>`
      : o.online ? `<span class="tag on" title="El servidor inició sesión en el equipo y leyó su estado">En línea</span>`
      : `<span class="tag off" title="${esc(o.error || "")}">Sin conexión</span>`}
    <button class="btn ghost small" type="button" id="dcd-refresh" ${o ? "" : "disabled"}
      title="Volver a leer salidas, entradas y canales del equipo">Actualizar</button>`;
  $("#dcd-refresh").addEventListener("click", () => dcdLoadOverview(true));
}

function dcdTab(tab) {
  dcdState.tab = tab;
  history.replaceState(null, "", `#/decoders/device?id=${dcdState.id}${tab === "connection" ? "" : `&tab=${tab}`}`);
  $$("#view .res-tab").forEach((b) => {
    b.classList.toggle("on", b.dataset.tab === tab);
    b.setAttribute("aria-selected", String(b.dataset.tab === tab));
  });
  dcdDrawTab();
}

function dcdDrawTab() {
  const body = $("#dcd-body");
  if (!body) return;
  switch (dcdState.tab) {
    case "outputs": dcdDrawOutputs(body); break;
    case "inputs": dcdDrawInputs(body); break;
    case "diagnostics": dcdDrawDiagnostics(body); break;
    default: dcdDrawConnection(body);
  }
}

// ---------- Conexión ----------
function dcdDrawConnection(body) {
  const { decoder: d, drivers } = dcdState;
  const isAdmin = Perms.can("devices.manage");
  body.innerHTML = `
    <div class="acd-narrow">
      <div class="acd-card">
        <div class="acd-card-head"><h4>Conexión</h4>
          <span class="muted">Al guardar cambios de dirección o credenciales se valida contra el equipo.</span></div>
        <fieldset class="acd-fieldset" ${isAdmin ? "" : "disabled"}>${decoderFieldsHtml(d, drivers, true)}</fieldset>
        <div id="dcd-conn-msg"></div>
        <div class="acd-actions">
          <button class="btn ghost" type="button" id="dcd-test" title="Iniciar sesión en el equipo y leer sus capacidades">Probar conexión</button>
          ${isAdmin ? `<button class="btn" type="button" id="dcd-save">Guardar</button>` : ""}
        </div>
      </div>
      <div class="acd-card" id="dcd-summary"></div>
    </div>`;
  dcdDrawSummary();

  $("#dcd-test").addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    $("#dcd-conn-msg").innerHTML = `<div class="muted">Conectando con el decodificador…</div>`;
    try {
      const result = await Api.post(`/api/decoders/${dcdState.id}/test`);
      const caps = result.capabilities;
      $("#dcd-conn-msg").innerHTML = result.success
        ? `<div class="info-box">Conexión correcta: ${caps.displays.length} salida(s) y ${caps.decodeChannelCount} canales de decodificación.</div>`
        : `<div class="error-box">${esc(result.error)}</div>`;
      if (result.success) dcdLoadOverview(true);
    } catch (err) {
      $("#dcd-conn-msg").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally { button.disabled = false; }
  });

  $("#dcd-save")?.addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    button.textContent = "Guardando…";
    try {
      dcdState.decoder = await Api.put(`/api/decoders/${dcdState.id}`, decoderFieldsBody());
      toast("Decodificador guardado.");
      dcdDrawPage();
      dcdLoadOverview(true);
    } catch (err) {
      $("#dcd-conn-msg").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      button.disabled = false;
      button.textContent = "Guardar";
    }
  });
}

/** Resumen de la pestaña Conexión: identidad del equipo, canales y muros que lo usan. */
function dcdDrawSummary() {
  const box = $("#dcd-summary");
  if (!box) return;
  const o = dcdState.overview;
  if (!o) { box.innerHTML = `<div class="muted">Leyendo el equipo…</div>`; return; }
  const capacity = o.decodeChannelCount;
  const model = o.model || dcdState.decoder.model;
  // Hikvision no informa el modelo aparte: la serie lo trae al comienzo (DS-6908UDI0120…).
  const identity = model && model === o.serialNumber
    ? `<dt>Modelo y serie</dt><dd>${esc(model)}</dd>`
    : `<dt>Modelo</dt><dd>${esc(model || "—")}</dd><dt>N° de serie</dt><dd>${esc(o.serialNumber || "—")}</dd>`;
  box.innerHTML = `
    <div class="acd-card-head"><h4>Equipo</h4>
      ${o.online ? "" : `<span class="muted">Sin conexión: ${esc(o.error || "el equipo no respondió")}</span>`}</div>
    <dl class="res-dl">
      ${identity}
      <dt>Salidas</dt><dd>${o.online ? `${o.outputs.length} (${dcdOutputSummary(o.outputs)})` : "—"}</dd>
      <dt>Canales de decodificación</dt><dd>${capacity != null
        ? `${o.channelsInUse} en uso de ${capacity} <span class="muted">(desde el ${o.decodeChannelStart})</span>`
        : `${o.channelsInUse} en uso`}</dd>
      <dt>Muros</dt><dd>${o.walls.length
        ? o.walls.map((w) => `<a href="#/walls/edit?id=${w.id}">${esc(w.name)}</a> <span class="muted">(${w.rows}×${w.columns})</span>`).join(", ")
        : `<span class="muted">ninguno · <a href="#/walls/edit?decoder=${dcdState.id}">crear un muro con este equipo</a></span>`}</dd>
    </dl>`;
}

/** "8 HDMI, 2 BNC" */
function dcdOutputSummary(outputs) {
  const counts = new Map();
  outputs.forEach((o) => counts.set(o.type, (counts.get(o.type) || 0) + 1));
  return sortOutputs([...counts.keys()].map((type) => ({ type, index: 0 })))
    .map((x) => `${counts.get(x.type)} ${x.type === "Other" ? "otras" : x.type.toUpperCase()}`).join(", ");
}

// ---------- Salidas ----------
function dcdDrawOutputs(body) {
  const o = dcdState.overview;
  if (!o) { body.innerHTML = `<div class="muted">Leyendo las salidas del equipo…</div>`; return; }
  const isAdmin = Perms.can("devices.manage");
  const withMonitor = o.outputs.filter((x) => x.connected === true).length;
  const used = o.outputs.filter((x) => x.wall).length;
  body.innerHTML = `
    ${o.online ? "" : `<div class="warn-box">No se pudo conectar con el decodificador (${esc(o.error || "sin respuesta")}).
      Solo se listan las salidas que usan los muros configurados.</div>`}
    <div class="dw-toolbar">
      <span class="muted">${o.outputs.length} salida(s)${o.outputs.some((x) => x.connected != null) ? ` · ${withMonitor} con monitor` : ""} · ${used} en muros</span>
      ${o.canIdentify && isAdmin ? `<button class="btn ghost small" type="button" id="dcd-identify"
        title="Cada monitor muestra en pantalla el número de la salida que lo alimenta">Identificar monitores</button>` : ""}
    </div>
    ${o.outputs.length === 0 ? `<div class="info-box">El equipo no informó salidas de video.</div>` : `
    <div class="dw-outputs">
      ${o.outputs.map((out) => `
        <div class="dw-output${out.wall ? " used" : ""}">
          <div class="dw-output-head">${outputName(out)} ${outputMonitor(out)}</div>
          <div class="dw-output-res">${out.resolution ? esc(out.resolution) : `<span class="muted">resolución no informada</span>`}</div>
          <div class="dw-output-use">${out.wall
            ? `Monitor ${wallPos(out.wall.row, out.wall.col)} de <a href="#/walls/edit?id=${out.wall.id}">${esc(out.wall.name)}</a>`
            : `<span class="muted">Libre</span>`}</div>
        </div>`).join("")}
    </div>`}
    <p class="muted dw-note">La resolución y el monitor conectado los informa el propio equipo; la salida de cada monitor del muro se elige en el editor del muro.</p>`;
  $("#dcd-identify")?.addEventListener("click", (e) => identifyDecoderOutputs(dcdState.id, e.currentTarget));
}

// ---------- Entradas ----------
function dcdDrawInputs(body) {
  const o = dcdState.overview;
  if (!o) { body.innerHTML = `<div class="muted">Leyendo las entradas del equipo…</div>`; return; }
  if (!o.online) { body.innerHTML = `<div class="warn-box">No se pudo conectar con el decodificador (${esc(o.error || "sin respuesta")}).</div>`; return; }
  if (o.inputs == null) {
    body.innerHTML = `<div class="info-box">Este decodificador no informa entradas de señal locales.</div>`;
    return;
  }
  if (o.inputs.length === 0) {
    body.innerHTML = `<div class="info-box">El equipo no tiene entradas de señal locales (HDMI, VGA o DVI para conectar un PC u otra fuente).</div>`;
    return;
  }
  body.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Entrada</th><th>Tipo</th><th>Señal</th><th>Resolución</th></tr></thead>
      <tbody>${o.inputs.map((i) => `
        <tr>
          <td>${esc(i.name)}</td>
          <td>${outputBadge(i)}</td>
          <td>${i.signal === true ? `<span class="dw-mon on">con señal</span>` : i.signal === false ? `<span class="dw-mon off">sin señal</span>` : `<span class="muted">—</span>`}</td>
          <td>${i.resolution ? esc(i.resolution) : `<span class="muted">—</span>`}</td>
        </tr>`).join("")}
      </tbody>
    </table></div>
    <p class="muted dw-note">Las entradas locales son señales cableadas al propio decodificador (por ejemplo, el PC de un operador).</p>`;
}

// ---------- Diagnóstico ----------
function dcdDrawDiagnostics(body) {
  body.innerHTML = `
    <div class="dw-toolbar">
      <span class="muted">Volcado del estado real del equipo: muro, salidas, ventanas y decodificación de cada una.</span>
      <button class="btn ghost small" type="button" id="dcd-diag">Generar volcado</button>
    </div>
    <div id="dcd-diag-out"></div>`;
  $("#dcd-diag").addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    $("#dcd-diag-out").innerHTML = `<div class="muted">Consultando el estado del equipo…</div>`;
    try {
      // El diagnóstico responde texto plano, no JSON.
      const response = await fetch(`/api/decoders/${dcdState.id}/diagnostics`, {
        headers: { Authorization: "Bearer " + Api.token },
      });
      const text = await response.text();
      $("#dcd-diag-out").innerHTML = response.ok
        ? `<pre class="diag">${esc(text)}</pre>`
        : `<div class="error-box">${esc(text)}</div>`;
    } finally { button.disabled = false; }
  });
}

// ---------------------------------------------------------------------------
// Muros de video: lista (plano de cada muro)
// ---------------------------------------------------------------------------
async function renderWalls() {
  $("#page-title").textContent = "Muro de video";
  clearInterval(wallsTimer);
  let walls;
  try { walls = await Api.get("/api/walls"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Perms.can("devices.manage");
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Muros de video</h3>
      ${isAdmin ? `<a class="btn" href="#/walls/edit">Crear muro</a>` : ""}
    </div>
    ${walls.length === 0 ? `
      <div class="info-box">
        Aún no hay muros configurados. ${isAdmin
          ? "Registre primero un decodificador y luego use <b>Crear muro</b>: se elige el tamaño, qué salida física alimenta cada monitor y en cuántas ventanas se divide."
          : "Un administrador debe configurarlos."}
      </div>` : ""}
    <div id="walls-list"></div>`;

  const list = $("#walls-list");
  walls.forEach((wall) => list.appendChild(wallPlanCard(wall, isAdmin)));

  // Resolución y monitor conectado de cada salida: una lectura por
  // decodificador, en segundo plano (el plano ya se ve sin esperar al equipo).
  [...new Set(walls.map((w) => w.decoderId))].forEach(async (decoderId) => {
    let overview;
    try { overview = await decoderOverview(decoderId); } catch { return; }
    walls.filter((w) => w.decoderId === decoderId).forEach((wall) => {
      const card = $(`#wall-card-${wall.id}`);
      if (!card) return;
      if (overview.canIdentify) $("[data-identify]", card)?.classList.remove("hidden");
      if (!overview.online) {
        $(".dw-plan-status", card).innerHTML = `<span class="tag off" title="${esc(overview.error || "")}">decodificador sin conexión</span>`;
        return;
      }
      $(".dw-plan-status", card).innerHTML = `<span class="tag on">decodificador en línea</span>`;
      wall.screens.forEach((s) => {
        const out = overview.outputs.find((o) => o.channelNo === s.displayChannel);
        const meta = $(`[data-screen-meta="${s.id}"]`, card);
        if (meta && out) meta.innerHTML = [out.resolution ? esc(out.resolution) : "", outputMonitor(out)].filter(Boolean).join(" ");
      });
    });
  });
}

/** Plano de un muro: qué salida alimenta cada monitor y en cuántas ventanas se divide. */
function wallPlanCard(wall, isAdmin) {
  const div = document.createElement("div");
  div.className = "card wall-card";
  div.id = `wall-card-${wall.id}`;
  const cameras = wall.screens.reduce((n, s) => n + s.windows.filter((w) => w.assignment).length, 0)
    + (wall.floating ?? []).filter((f) => f.assignment).length;
  div.innerHTML = `
    <div class="toolbar">
      <h3>${esc(wall.name)}
        <span class="muted" style="font-weight:normal;font-size:12px">
          · ${wall.rows}×${wall.columns} · ${esc(wall.decoderName)} · ${cameras} cámara(s) en pantalla</span></h3>
      <div class="row-actions">
        <a class="btn ghost" href="#/videowall?wall=${wall.id}" title="Poner cámaras, layouts y pantalla completa">Operar</a>
        <button class="btn ghost hidden" data-identify title="Cada monitor muestra el número de la salida que lo alimenta">Identificar monitores</button>
        <button class="btn ghost" data-sync title="Reenviar al decodificador la división de ventanas configurada">Sincronizar</button>
        ${isAdmin ? `<a class="btn ghost" href="#/walls/edit?id=${wall.id}">Editar</a>
        <button class="btn danger" data-delete>Eliminar</button>` : ""}
      </div>
    </div>
    <div class="dw-plan-status"></div>
    <div class="dw-plan" style="grid-template-columns: repeat(${wall.columns}, minmax(0, 210px))"></div>`;

  const plan = $(".dw-plan", div);
  for (let r = 0; r < wall.rows; r++) {
    for (let c = 0; c < wall.columns; c++) {
      const s = wall.screens.find((x) => x.row === r && x.col === c);
      const cell = document.createElement("div");
      cell.className = "dw-plan-cell" + (s ? "" : " empty");
      if (!s) {
        cell.innerHTML = `<span class="dw-plan-pos">${wallPos(r, c)}</span><span class="muted">sin monitor</span>`;
      } else {
        const busy = s.windows.filter((w) => w.assignment).length;
        cell.innerHTML = `
          <span class="dw-plan-pos">${wallPos(r, c)}</span>
          <div class="dw-plan-out">${outputName(outputFromLabel(s.label, s.displayChannel))}</div>
          <div class="dw-plan-meta" data-screen-meta="${s.id}"></div>
          <div class="dw-plan-win muted">${s.windowMode === 1 ? "1 ventana" : `${s.windowMode} ventanas`}${busy ? ` · ${busy} con cámara` : ""}</div>`;
      }
      plan.appendChild(cell);
    }
  }

  $("[data-identify]", div).addEventListener("click", (e) => identifyDecoderOutputs(wall.decoderId, e.currentTarget));
  $("[data-sync]", div).addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    try {
      const results = await Api.post(`/api/walls/${wall.id}/sync`);
      const failed = Object.values(results).filter((x) => !x.success);
      toast(failed.length === 0
        ? "División de ventanas enviada al decodificador."
        : `Sincronización con ${failed.length} error(es): ${failed[0].error}`, failed.length > 0);
    } catch (err) { toast(err.error, true); }
    finally { button.disabled = false; }
  });
  $("[data-delete]", div)?.addEventListener("click", async () => {
    if (!confirm(`¿Eliminar el muro "${wall.name}"?`)) return;
    try { await Api.delete(`/api/walls/${wall.id}`); toast("Muro eliminado."); renderWalls(); }
    catch (err) { toast(err.error, true); }
  });
  return div;
}

// ---------------------------------------------------------------------------
// Editor del muro (#/walls/edit?id=N · #/walls/edit?decoder=N para uno nuevo)
// ---------------------------------------------------------------------------
// El plano del muro a la derecha y las salidas del decodificador a la
// izquierda: se arrastra una salida a una posición (o se elige con el botón),
// se arrastra un monitor sobre otro para intercambiarlos y se suelta sobre la
// lista para quitarlo. Los monitores que no cambian conservan sus cámaras.
const WALL_MAX_SIZE = 8;
let weState = null;

async function renderWallEditor() {
  $("#page-title").textContent = "Muro de video";
  const params = new URLSearchParams(location.hash.split("?")[1] || "");
  const id = Number(params.get("id")) || null;
  const back = `<div class="acd-back"><a class="btn ghost small" href="#/walls">← Volver a los muros</a></div>`;
  if (!Perms.can("devices.manage")) { location.hash = "#/walls"; return; }
  $("#view").innerHTML = `${back}<div class="muted">Cargando…</div>`;

  let decoders, wall = null;
  try {
    decoders = (await Api.get("/api/decoders")).filter((d) => d.enabled);
    if (id) wall = await Api.get(`/api/walls/${id}`);
  } catch (err) {
    $("#view").innerHTML = `${back}<div class="error-box">${esc(err.status === 404 ? "Ese muro ya no existe." : err.error)}</div>`;
    return;
  }
  if (wall && !decoders.some((d) => d.id === wall.decoderId))
    decoders = [...decoders, { id: wall.decoderId, name: wall.decoderName, host: "", enabled: false }];
  if (decoders.length === 0) {
    $("#view").innerHTML = `${back}<div class="info-box">Primero registre un decodificador en
      <a href="#/decoders">Configuración → Videowalls → Decodificadores</a>.</div>`;
    return;
  }

  const wantedDecoder = Number(params.get("decoder"));
  const decoderId = wall?.decoderId ?? (decoders.some((d) => d.id === wantedDecoder) ? wantedDecoder : decoders[0].id);
  // Posiciones del plano: "fila,col" → { channelNo, label, windowMode }.
  const positions = new Map();
  (wall?.screens ?? []).forEach((s) => positions.set(`${s.row},${s.col}`,
    { channelNo: s.displayChannel, label: s.label, windowMode: s.windowMode }));

  weState = {
    wall, decoders, decoderId, positions,
    rows: wall?.rows ?? 2, cols: wall?.columns ?? 2,
    overview: null, loadingFor: null,
  };
  weDrawPage();
  weLoadOverview();
}

/** Salidas conocidas: las que informa el equipo y, si no responde, las que ya usa el muro. */
function weOutputs() {
  const o = weState.overview;
  const list = o ? [...o.outputs] : [];
  for (const p of weState.positions.values())
    if (!list.some((x) => x.channelNo === p.channelNo)) list.push(outputFromLabel(p.label, p.channelNo));
  return list;
}

/** Dónde está una salida en ESTE plano ("fila,col") o null. */
function weWhere(channelNo) {
  for (const [key, p] of weState.positions) if (p.channelNo === channelNo) return key;
  return null;
}

/** La salida alimenta otro muro del mismo decodificador (no se puede usar aquí). */
const weTakenBy = (out) => out.wall && out.wall.id !== weState.wall?.id ? out.wall : null;

async function weLoadOverview() {
  const decoderId = weState.decoderId;
  weState.loadingFor = decoderId;
  weState.overview = null;
  weDrawOutputs();
  weDrawBudget();
  let overview;
  try { overview = await decoderOverview(decoderId); }
  catch (err) { overview = { online: false, error: err.error, outputs: [], walls: [], canIdentify: false }; }
  if (!weState || weState.loadingFor !== decoderId) return;   // cambió el decodificador o la página
  weState.overview = overview;
  weDrawOutputs();
  weDrawGrid();
  weDrawBudget();
}

function weDrawPage() {
  const s = weState;
  $("#page-title").textContent = s.wall ? `Muro de video · ${s.wall.name}` : "Muro de video · nuevo";
  $("#view").innerHTML = `
    <div class="acd-back"><a class="btn ghost small" href="#/walls">← Volver a los muros</a></div>
    <div class="we-head">
      <div class="field"><label>Nombre</label>
        <input id="we-name" value="${esc(s.wall?.name ?? "")}" placeholder="Ej: Muro sala de monitoreo"></div>
      <div class="field"><label>Decodificador</label>
        <select id="we-decoder">${s.decoders.map((d) => `<option value="${d.id}" ${d.id === s.decoderId ? "selected" : ""}>${esc(d.name)}${d.host ? ` (${esc(d.host)})` : ""}</option>`).join("")}</select></div>
      <div class="field we-size-field"><label>Tamaño del muro</label>
        <div class="we-size">
          <input id="we-rows" type="number" min="1" max="${WALL_MAX_SIZE}" value="${s.rows}" aria-label="Filas">
          <span class="muted">filas ×</span>
          <input id="we-cols" type="number" min="1" max="${WALL_MAX_SIZE}" value="${s.cols}" aria-label="Columnas">
          <span class="muted">columnas</span>
        </div>
        <div class="we-picker" id="we-picker" title="Pase el mouse para elegir el tamaño y haga clic"></div>
      </div>
    </div>
    <div class="we-body">
      <aside class="we-side">
        <div class="we-side-head"><b>Salidas del decodificador</b><span class="muted" id="we-out-status"></span></div>
        <div class="we-out-list" id="we-out-list"></div>
        <div class="we-side-actions">
          <button class="btn ghost small" type="button" id="we-auto" title="Llena las posiciones libres con las salidas libres, en orden">Ubicar en orden</button>
          <button class="btn ghost small" type="button" id="we-clear">Quitar todas</button>
          <button class="btn ghost small hidden" type="button" id="we-identify"
            title="Cada monitor muestra en pantalla el número de la salida que lo alimenta">Identificar monitores</button>
        </div>
        <p class="muted we-help">Arrastre una salida a una posición del muro. Arrastre un monitor sobre otro para
          intercambiarlos, o de vuelta a esta lista para quitarlo. ¿No sabe qué monitor es cuál? Use <b>Identificar monitores</b>.</p>
      </aside>
      <section class="we-canvas">
        <div class="we-grid" id="we-grid"></div>
        <div class="we-budget" id="we-budget"></div>
      </section>
    </div>
    <div id="we-error"></div>
    <div class="acd-actions">
      <a class="btn ghost" href="#/walls">Cancelar</a>
      <button class="btn" type="button" id="we-save">${s.wall ? "Guardar cambios" : "Crear muro"}</button>
    </div>`;

  const resize = () => {
    const clamp = (v) => Math.min(WALL_MAX_SIZE, Math.max(1, Number(v) || 1));
    weSetSize(clamp($("#we-rows").value), clamp($("#we-cols").value));
  };
  $("#we-rows").addEventListener("change", resize);
  $("#we-cols").addEventListener("change", resize);
  $("#we-decoder").addEventListener("change", (e) => {
    const next = Number(e.target.value);
    if (weState.positions.size && !confirm("Las salidas ubicadas son del decodificador anterior y se quitarán del plano. ¿Continuar?")) {
      e.target.value = weState.decoderId;
      return;
    }
    weState.decoderId = next;
    weState.positions.clear();
    weDrawGrid();
    weLoadOverview();
  });
  $("#we-auto").addEventListener("click", weAutoPlace);
  $("#we-clear").addEventListener("click", () => {
    if (!weState.positions.size) return;
    weState.positions.clear();
    weRedraw();
  });
  $("#we-identify").addEventListener("click", (e) => identifyDecoderOutputs(weState.decoderId, e.currentTarget));
  $("#we-save").addEventListener("click", weSave);

  // Soltar un monitor sobre la lista de salidas lo quita del plano.
  const side = $("#we-out-list");
  side.addEventListener("dragover", (e) => {
    if (weDragging?.startsWith("cell:")) { e.preventDefault(); side.classList.add("drop"); }
  });
  side.addEventListener("dragleave", () => side.classList.remove("drop"));
  side.addEventListener("drop", (e) => {
    side.classList.remove("drop");
    const data = e.dataTransfer.getData("text/plain");
    if (!data.startsWith("cell:")) return;
    e.preventDefault();
    weState.positions.delete(data.slice(5));
    weRedraw();
  });

  weDrawPicker();
  weDrawGrid();
  weDrawOutputs();
  weDrawBudget();
}

function weRedraw() { weDrawGrid(); weDrawOutputs(); weDrawBudget(); }

/** Lo que se arrastra: durante dragover los navegadores no dejan leer los datos, solo el tipo. */
let weDragging = null;

function weSetSize(rows, cols) {
  const s = weState;
  const outside = [...s.positions.keys()].filter((k) => { const [r, c] = k.split(",").map(Number); return r >= rows || c >= cols; });
  if (outside.length && !confirm(`Al achicar el muro quedan fuera ${outside.length} monitor(es) ya ubicados y se quitarán. ¿Continuar?`)) {
    $("#we-rows").value = s.rows;
    $("#we-cols").value = s.cols;
    return;
  }
  outside.forEach((k) => s.positions.delete(k));
  s.rows = rows;
  s.cols = cols;
  $("#we-rows").value = rows;
  $("#we-cols").value = cols;
  weDrawPicker();
  weRedraw();
}

/** Selector visual del tamaño: una grilla de 8×8 que se pinta al pasar el mouse. */
function weDrawPicker() {
  const picker = $("#we-picker");
  const paint = (rows, cols) => {
    $$(".we-picker-cell", picker).forEach((cell) => {
      cell.classList.toggle("on", Number(cell.dataset.r) < rows && Number(cell.dataset.c) < cols);
    });
    picker.dataset.hint = `${rows} × ${cols}`;
  };
  if (!picker.children.length) {   // se arma una vez; después solo se repinta
    for (let r = 0; r < WALL_MAX_SIZE; r++) {
      for (let c = 0; c < WALL_MAX_SIZE; c++) {
        const cell = document.createElement("button");
        cell.type = "button";
        cell.className = "we-picker-cell";
        cell.dataset.r = r;
        cell.dataset.c = c;
        cell.setAttribute("aria-label", `${r + 1} filas × ${c + 1} columnas`);
        cell.addEventListener("mouseenter", () => paint(r + 1, c + 1));
        cell.addEventListener("click", () => weSetSize(r + 1, c + 1));
        picker.appendChild(cell);
      }
    }
    picker.addEventListener("mouseleave", () => paint(weState.rows, weState.cols));
  }
  paint(weState.rows, weState.cols);
}

/** ¿Este monitor quedó igual que en el muro guardado? (entonces conserva sus cámaras) */
function weUnchanged(key, p) {
  const saved = weState.wall?.screens.find((s) => `${s.row},${s.col}` === key);
  return saved && saved.displayChannel === p.channelNo && saved.windowMode === p.windowMode
    && weState.wall.decoderId === weState.decoderId ? saved : null;
}

function weModesFor(channelNo) {
  const out = weOutputs().find((o) => o.channelNo === channelNo);
  return out?.windowModes?.length ? out.windowModes : WALL_DEFAULT_MODES;
}

function weDrawGrid() {
  const s = weState;
  const grid = $("#we-grid");
  if (!grid) return;
  grid.style.gridTemplateColumns = `repeat(${s.cols}, minmax(0, 250px))`;
  grid.innerHTML = "";
  const outputs = weOutputs();
  for (let r = 0; r < s.rows; r++) {
    for (let c = 0; c < s.cols; c++) {
      const key = `${r},${c}`;
      const p = s.positions.get(key);
      const cell = document.createElement("div");
      cell.className = "we-cell" + (p ? " bound" : "");
      cell.dataset.key = key;
      if (p) {
        const out = outputs.find((o) => o.channelNo === p.channelNo) ?? { label: p.label, type: "Other" };
        const modes = [...new Set([...weModesFor(p.channelNo), p.windowMode])].sort((a, b) => a - b);
        const saved = weUnchanged(key, p);
        cell.draggable = true;
        cell.innerHTML = `
          <div class="we-cell-top"><span class="we-cell-pos">${wallPos(r, c)}</span>
            <button type="button" class="we-x" title="Quitar este monitor del muro" aria-label="Quitar">✕</button></div>
          <div class="we-cell-out">${outputName(out)}</div>
          <div class="we-cell-meta">${[out.resolution ? esc(out.resolution) : "", outputMonitor(out)].filter(Boolean).join(" ") || "&nbsp;"}</div>
          <label class="we-cell-mode">División
            <select>${modes.map((m) => `<option value="${m}" ${m === p.windowMode ? "selected" : ""}>${m === 1 ? "1 (completa)" : `${m} ventanas`}</option>`).join("")}</select>
          </label>
          ${saved && saved.windows.some((w) => w.assignment) ? `<div class="we-cell-keep" title="Al guardar, este monitor sigue mostrando lo mismo">conserva sus cámaras</div>` : ""}`;
        $(".we-x", cell).addEventListener("click", () => { s.positions.delete(key); weRedraw(); });
        $("select", cell).addEventListener("change", (e) => { p.windowMode = Number(e.target.value); weDrawGrid(); weDrawBudget(); });
        cell.addEventListener("dragstart", (e) => {
          weDragging = `cell:${key}`;
          e.dataTransfer.setData("text/plain", weDragging);
          e.dataTransfer.effectAllowed = "move";
        });
        cell.addEventListener("dragend", () => { weDragging = null; });
      } else {
        const free = sortOutputs(outputs.filter((o) => !weWhere(o.channelNo) && !weTakenBy(o)));
        cell.innerHTML = `
          <div class="we-cell-top"><span class="we-cell-pos">${wallPos(r, c)}</span></div>
          <div class="we-cell-empty">
            <span class="muted">${free.length ? "Suelte una salida aquí" : "No quedan salidas libres"}</span>
            ${free.length ? `<select class="we-pick" aria-label="Elegir la salida de ${wallPos(r, c)}">
              <option value="">Elegir salida…</option>
              ${free.map((o) => `<option value="${o.channelNo}">${esc(o.label)}${o.connected === false ? " (sin monitor)" : ""}</option>`).join("")}
            </select>` : ""}
          </div>`;
        $(".we-pick", cell)?.addEventListener("change", (e) => {
          const out = outputs.find((o) => o.channelNo === Number(e.target.value));
          if (out) wePlace(key, out);
        });
      }
      cell.addEventListener("dragover", (e) => {
        if (!weDragging || weDragging === `cell:${key}`) return;
        e.preventDefault();
        cell.classList.add("drop");
      });
      cell.addEventListener("dragleave", () => cell.classList.remove("drop"));
      cell.addEventListener("drop", (e) => {
        cell.classList.remove("drop");
        const data = e.dataTransfer.getData("text/plain");
        e.preventDefault();
        if (data.startsWith("out:")) {
          const out = outputs.find((o) => o.channelNo === Number(data.slice(4)));
          if (out) wePlace(key, out);
        } else if (data.startsWith("cell:")) {
          const from = data.slice(5);
          if (from === key) return;
          const a = s.positions.get(from), b = s.positions.get(key);
          s.positions.delete(from);
          s.positions.delete(key);
          if (a) s.positions.set(key, a);
          if (b) s.positions.set(from, b);
          weRedraw();
        }
      });
      grid.appendChild(cell);
    }
  }
}

/** Ubica una salida en una posición (si ya estaba en otra, se mueve). */
function wePlace(key, out) {
  const s = weState;
  if (weTakenBy(out)) { toast(`${out.label} ya alimenta un monitor del muro "${out.wall.name}".`, true); return; }
  const previous = weWhere(out.channelNo);
  const moved = previous ? s.positions.get(previous) : null;
  if (previous) s.positions.delete(previous);
  s.positions.set(key, { channelNo: out.channelNo, label: out.label, windowMode: moved?.windowMode ?? 1 });
  weRedraw();
}

/** Llena las posiciones libres, en orden de lectura, con las salidas libres (digitales primero). */
function weAutoPlace() {
  const s = weState;
  const free = sortOutputs(weOutputs().filter((o) => !weWhere(o.channelNo) && !weTakenBy(o)));
  if (!free.length) { toast("No quedan salidas libres en este decodificador.", true); return; }
  let placed = 0;
  for (let r = 0; r < s.rows && free.length; r++) {
    for (let c = 0; c < s.cols && free.length; c++) {
      if (s.positions.has(`${r},${c}`)) continue;
      const out = free.shift();
      s.positions.set(`${r},${c}`, { channelNo: out.channelNo, label: out.label, windowMode: 1 });
      placed++;
    }
  }
  if (!placed) { toast("El muro no tiene posiciones libres: agrándelo o quite algún monitor.", true); return; }
  weRedraw();
}

function weDrawOutputs() {
  const s = weState;
  const list = $("#we-out-list");
  if (!list) return;
  const o = s.overview;
  $("#we-identify")?.classList.toggle("hidden", !o?.canIdentify);
  $("#we-out-status").textContent = !o ? " · leyendo el equipo…" : o.online ? "" : " · sin conexión";
  if (!o) { list.innerHTML = `<div class="muted we-small">Conectando con el decodificador…</div>`; return; }
  const outputs = weOutputs();
  list.innerHTML = `
    ${o.online ? "" : `<div class="warn-box we-small" title="${esc(o.error || "")}">No se pudo leer el equipo: solo se muestran las salidas que el muro ya usa.</div>`}
    ${outputs.length === 0 ? `<div class="muted we-small">El equipo no informó salidas.</div>` : ""}
    ${outputs.map((out) => {
      const where = weWhere(out.channelNo);
      const taken = weTakenBy(out);
      const [r, c] = where ? where.split(",").map(Number) : [];
      return `
        <div class="we-out${where ? " placed" : ""}${taken ? " taken" : ""}" data-ch="${out.channelNo}" ${taken ? "" : `draggable="true"`}
             title="${taken ? `Alimenta un monitor del muro "${esc(taken.name)}"` : where ? "Arrástrela a otra posición para moverla" : "Arrástrela a una posición del muro"}">
          <div class="we-out-name">${outputName(out)} ${outputMonitor(out)}</div>
          <div class="we-out-meta">
            <span>${out.resolution ? esc(out.resolution) : ""}</span>
            <span class="we-out-where">${taken ? `en ${esc(taken.name)}` : where ? `monitor ${wallPos(r, c)}` : "libre"}</span>
          </div>
        </div>`;
    }).join("")}`;
  $$(".we-out[draggable]", list).forEach((el) => {
    el.addEventListener("dragstart", (e) => {
      weDragging = `out:${el.dataset.ch}`;
      e.dataTransfer.setData("text/plain", weDragging);
      e.dataTransfer.effectAllowed = "move";
    });
    el.addEventListener("dragend", () => { weDragging = null; });
  });
}

/** Canales de decodificación que usará el equipo con este plano. */
function weDrawBudget() {
  const box = $("#we-budget");
  if (!box) return;
  const s = weState;
  const o = s.overview;
  let here = 0;
  for (const [key, p] of s.positions) {
    const saved = weUnchanged(key, p);
    here += saved ? saved.windows.length : p.windowMode;   // un monitor sin cambios conserva sus ventanas (agrupadas o subdivididas)
  }
  const floating = s.wall && s.wall.decoderId === s.decoderId ? (s.wall.floating ?? []).length : 0;
  const others = (o?.walls ?? []).filter((w) => w.id !== s.wall?.id).reduce((n, w) => n + w.windows + w.floating, 0);
  const total = here + floating + others;
  const capacity = o?.decodeChannelCount;
  const over = capacity != null && total > capacity;
  const pct = capacity ? Math.min(100, Math.round(total * 100 / capacity)) : 0;
  box.innerHTML = `
    ${capacity ? `<div class="we-bar${over ? " over" : ""}"><span style="width:${pct}%"></span></div>` : ""}
    <div class="we-small ${over ? "we-over" : "muted"}">${s.positions.size} monitor(es) · ${here} ventana(s)${
      floating ? ` + ${floating} flotante(s)` : ""}${others ? ` · otros muros del equipo usan ${others}` : ""}${
      capacity != null ? ` — <b>${total} de ${capacity}</b> canales de decodificación${over ? ": reduzca la división de algún monitor." : "."}`
        : " · capacidad del equipo desconocida (sin conexión)."}</div>`;
  const save = $("#we-save");
  if (save) save.disabled = over;
}

async function weSave() {
  const s = weState;
  const outputs = weOutputs();
  const body = {
    name: $("#we-name").value.trim(),
    decoderId: s.decoderId,
    rows: s.rows,
    columns: s.cols,
    screens: [...s.positions].map(([key, p]) => {
      const [row, col] = key.split(",").map(Number);
      return { row, col, label: outputs.find((o) => o.channelNo === p.channelNo)?.label ?? p.label,
        displayChannel: p.channelNo, windowMode: p.windowMode };
    }),
  };
  const error = (message) => { $("#we-error").innerHTML = `<div class="error-box">${esc(message)}</div>`; };
  if (!body.name) { error("El nombre del muro es obligatorio."); $("#we-name").focus(); return; }
  if (!body.screens.length) { error("Ubique al menos una salida del decodificador en el muro."); return; }
  $("#we-error").innerHTML = "";

  const save = $("#we-save");
  save.disabled = true;
  save.textContent = "Guardando…";
  try {
    const result = s.wall
      ? await Api.put(`/api/walls/${s.wall.id}`, body)
      : await Api.post("/api/walls", body);
    toast(s.wall ? "Muro guardado." : "Muro creado.");
    if (result.warning) toast(result.warning, true);
    const syncFailed = Object.values(result.sync || {}).filter((x) => !x.success);
    if (syncFailed.length > 0)
      toast(`La división de ventanas no llegó al decodificador (${syncFailed.length} monitor(es)): use "Sincronizar" cuando esté en línea.`, true);
    location.hash = "#/walls";
  } catch (err) {
    error(err.error);
    save.disabled = false;
    save.textContent = s.wall ? "Guardar cambios" : "Crear muro";
  }
}

// ---------------------------------------------------------------------------
// Layouts guardados (los usa también Aplicaciones → Videowall)
// ---------------------------------------------------------------------------
/** Layouts guardados de un muro (foto del estado: divisiones + cámaras).
 *  `onApplied` redibuja la página que la abrió. */
async function layoutsModal(wall, onApplied = renderWalls) {
  let layouts;
  try { layouts = await Api.get(`/api/walls/${wall.id}/layouts`); }
  catch (err) { toast(err.error, true); return; }

  // Guardar y eliminar exigen "Guardar diseños del muro"; aplicar basta con operar el muro.
  const canSave = Perms.can("wall.layouts");
  openModal(`
    <h3>Layouts de "${esc(wall.name)}"</h3>
    ${canSave ? `<div class="field">
      <label>Guardar el estado actual como</label>
      <input id="lm-name" placeholder="Ej: Turno noche">
    </div>
    <button class="btn" id="lm-save">Guardar layout</button>` : ""}
    <h3 style="margin-top:22px">Guardados</h3>
    ${layouts.length === 0 ? `<p class="muted">No hay layouts guardados.</p>` : `
      <table class="grid"><tbody>
        ${layouts.map((l) => `
          <tr data-id="${l.id}">
            <td>${esc(l.name)} <span class="muted">· ${l.items.length} cámara(s)</span></td>
            <td class="row-actions">
              <button class="btn ghost btn-apply">Aplicar</button>
              ${canSave ? `<button class="btn danger btn-del">Eliminar</button>` : ""}
            </td>
          </tr>`).join("")}
      </tbody></table>`}
    <div class="modal-actions"><button class="btn ghost" id="lm-close">Cerrar</button></div>`);

  $("#lm-close").addEventListener("click", closeModal);
  $("#lm-save")?.addEventListener("click", async () => {
    const name = $("#lm-name").value.trim();
    if (!name) { toast("Escriba un nombre para el layout.", true); return; }
    try {
      await Api.post(`/api/walls/${wall.id}/layouts`, { name, screens: null, items: null });
      toast("Layout guardado.");
      layoutsModal(wall, onApplied);
    } catch (err) { toast(err.error, true); }
  });

  $$("#modal .btn-apply").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    e.target.disabled = true;
    e.target.textContent = "Aplicando…";
    try {
      const results = await Api.post(`/api/walls/${wall.id}/layouts/${id}/apply`);
      const failed = Object.values(results).filter((x) => !x.success).length;
      closeModal();
      toast(failed === 0 ? "Layout aplicado." : `Layout aplicado con ${failed} error(es).`, failed > 0);
      onApplied();
    } catch (err) {
      toast(err.error, true);
      e.target.disabled = false;
      e.target.textContent = "Aplicar";
    }
  }));

  $$("#modal .btn-del").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    if (!confirm("¿Eliminar este layout?")) return;
    try { await Api.delete(`/api/walls/${wall.id}/layouts/${id}`); layoutsModal(wall, onApplied); }
    catch (err) { toast(err.error, true); }
  }));
}
