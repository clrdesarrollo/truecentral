// Módulo Paneles de cerco eléctrico (mantenedor + monitor del guardia).
// Tiempo real por SignalR (VmsHub): las tarjetas y filas se actualizan en el
// sitio ante CercoPanelStateChanged / CercoEventReceived, sin re-render completo
// (sin parpadeo). Ver hub.js.

let cercoHubBound = [];       // [{ev, fn}] suscripciones activas de esta vista
let cercoReconnectUnsub = null;
const CERCO_MAX_EVENTS = 10;   // el monitor muestra solo los últimos; el historial queda en la BD

// Suelta suscripciones del hub Y las delegaciones de click, para no apilar
// handlers al navegar entre vistas (evita doble ejecución de comandos).
function cercoDetachHub() {
  for (const { ev, fn } of cercoHubBound) VmsHub.off(ev, fn);
  cercoHubBound = [];
  if (cercoReconnectUnsub) { cercoReconnectUnsub(); cercoReconnectUnsub = null; }
  const view = $("#view");
  view?.removeEventListener("click", onCercoAdminClick);
  view?.removeEventListener("click", onCercoCmdClick);
}
// El hub (SignalR) serializa los enums como NÚMERO; la API REST, como texto. El
// cliente WPF depende de los números del hub, así que se normaliza aquí a texto
// (mismo orden que los enums de CercoDtos.cs).
const CERCO_ENUMS = {
  status: ["Unknown", "Online", "Offline"],
  kind: ["Boot", "Armed", "Disarmed", "Alarm", "FenceCut", "HvFault", "Arc", "SirenOn", "SirenOff",
         "RfRemote", "Tamper", "ArmFailed", "ZoneRestore", "Panic", "RfLearned", "RfLearnTimeout",
         "PowerLost", "PowerRestored"],
  severity: ["Info", "Warning", "Critical"],
  action: ["None", "Arm", "Disarm", "Toggle", "Panic", "Silence"],
};
function cercoEnums(o) {
  if (o && typeof o === "object")
    for (const f of Object.keys(CERCO_ENUMS))
      if (typeof o[f] === "number") o[f] = CERCO_ENUMS[f][o[f]] ?? String(o[f]);
  return o;
}
function cercoNormalize(payload) {
  cercoEnums(payload);
  if (Array.isArray(payload?.remotes)) payload.remotes.forEach(cercoEnums);
  return payload;
}
function cercoBind(ev, fn) {
  const h = (payload) => fn(cercoNormalize(payload));
  VmsHub.on(ev, h); cercoHubBound.push({ ev, fn: h });
}

function cercoConnDot(p) {
  if (!p.enabled) return `<span class="tag operator" title="Deshabilitado">pausado</span>`;
  return p.connected
    ? `<span class="dot ok" title="Conectado"></span>`
    : `<span class="dot bad" title="Sin conexión"></span>`;
}

// ---------------------------------------------------------------- mantenedor
async function renderCercoPanels() {
  cercoDetachHub();
  $("#page-title").textContent = "Paneles de cerco";
  let panels;
  try { panels = await Api.get("/api/cerco/panels"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Paneles de cerco <span class="muted" style="font-weight:normal;font-size:12px">(tiempo real)</span></h3>
      ${isAdmin ? `<button class="btn" id="btn-cerco-new">Agregar panel</button>` : ""}
    </div>
    ${panels.length === 0 ? `
      <div class="info-box">
        Aún no hay paneles de cerco. ${isAdmin
          ? "Use <b>Agregar panel</b>: el sistema genera un <b>ID de equipo</b> y una <b>clave</b> que debe cargar en el panel con la herramienta de provisioning. Desde que el panel se conecta, el guardia puede armar, desarmar y silenciar."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>ID de equipo</th><th>Sitio</th><th>Firmware</th>
          <th>Armado</th><th>Sirena</th><th>Nivel AV</th><th>Conexión</th><th></th>
        </tr></thead>
        <tbody id="cerco-rows">
          ${panels.map((p) => cercoRow(p, isAdmin)).join("")}
        </tbody>
      </table></div>`}`;

  // Delegación de acciones (sobrevive a las actualizaciones incrementales).
  $("#view").addEventListener("click", onCercoAdminClick);

  // Tiempo real: actualizar la fila del panel que cambió (y el modal de configuración si está abierto).
  cercoBind("CercoPanelStateChanged", (p) => {
    const tr = document.getElementById(`cerco-row-${p.id}`);
    if (tr) tr.outerHTML = cercoRow(p, isAdmin);
    cercoCfgOnState(p);
  });
  cercoBind("CercoRemotesChanged", (m) => cercoCfgOnRemotes(m.panelId, m.remotes));
  cercoBind("CercoEventReceived", (e) => cercoCfgOnEvent(e));
  cercoReconnectUnsub = VmsHub.onReconnected(() => { if (location.hash === "#/cerco-panels") renderCercoPanels(); });
}

function cercoRow(p, isAdmin) {
  return `
    <tr id="cerco-row-${p.id}" data-id="${p.id}" data-name="${esc(p.name)}">
      <td>${esc(p.name)}</td>
      <td class="muted">${esc(p.deviceId)}</td>
      <td class="muted">${esc(p.site ?? "—")}</td>
      <td class="muted">${esc(p.firmware ?? "—")}</td>
      <td>${p.armed ? `<span class="tag ok">armado</span>` : `<span class="tag">desarmado</span>`}</td>
      <td>${p.siren ? `<span class="tag danger">sonando</span>` : "—"}</td>
      <td class="muted">${p.voltage ?? "—"}</td>
      <td>${cercoConnDot(p)}</td>
      <td class="row-actions">
        ${isAdmin ? `<button class="btn btn-config" title="Potencia, sirena, zona, llave y controles remotos">Configurar</button>
        <button class="btn ghost btn-key" title="Rotar la clave (hay que re-provisionar)">Rotar clave</button>
        <button class="btn ghost btn-edit">Editar</button>
        <button class="btn danger btn-delete">Eliminar</button>` : ""}
      </td>
    </tr>`;
}

async function onCercoAdminClick(e) {
  const btn = e.target.closest("button");
  if (!btn) return;
  if (btn.id === "btn-cerco-new") return cercoPanelModal(null);
  const tr = btn.closest("tr[data-id]");
  if (!tr) return;
  const id = Number(tr.dataset.id), name = tr.dataset.name;
  if (btn.classList.contains("btn-config")) {
    try { cercoConfigModal(await Api.get(`/api/cerco/panels/${id}`)); } catch (err) { toast(err.error, true); }
  } else if (btn.classList.contains("btn-edit")) {
    try { cercoPanelModal(await Api.get(`/api/cerco/panels/${id}`)); } catch (err) { toast(err.error, true); }
  } else if (btn.classList.contains("btn-delete")) {
    if (!confirm(`¿Eliminar el panel "${name}"? El historial de eventos se conserva.`)) return;
    try { await Api.delete(`/api/cerco/panels/${id}`); toast("Panel eliminado."); document.getElementById(`cerco-row-${id}`)?.remove(); }
    catch (err) { toast(err.error, true); }
  } else if (btn.classList.contains("btn-key")) {
    if (!confirm(`¿Rotar la clave de "${name}"? El panel dejará de conectar hasta que lo re-provisione con la nueva clave.`)) return;
    try { cercoCredentialsModal(await Api.post(`/api/cerco/panels/${id}/rotate-key`), name); }
    catch (err) { toast(err.error, true); }
  }
}

function cercoPanelModal(panel) {
  const editing = !!panel;
  openModal(`
    <h3>${editing ? "Editar" : "Agregar"} panel de cerco</h3>
    <div class="field"><label>Nombre</label><input id="c-name" maxlength="128" value="${editing ? esc(panel.name) : ""}" placeholder="Cerco perímetro norte"></div>
    <div class="field"><label>Sitio</label><input id="c-site" maxlength="255" value="${editing ? esc(panel.site ?? "") : ""}" placeholder="Bodega central"></div>
    <div class="field checkbox"><label><input type="checkbox" id="c-enabled" ${!editing || panel.enabled ? "checked" : ""}> Habilitado</label></div>
    ${editing ? "" : `<div class="info-box">Al guardar, el sistema genera el <b>ID de equipo</b> y la <b>clave (PSK)</b>. Se muestran una sola vez: cárguelos en el panel con la herramienta de provisioning.</div>`}
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="c-cancel">Cancelar</button>
      <button class="btn" type="button" id="c-save">${editing ? "Guardar" : "Crear y generar clave"}</button>
    </div>`);
  $("#c-cancel").addEventListener("click", closeModal);
  $("#c-save").addEventListener("click", async () => {
    const body = { name: $("#c-name").value.trim(), site: $("#c-site").value.trim() || null, enabled: $("#c-enabled").checked };
    if (!body.name) { toast("El nombre es obligatorio.", true); return; }
    try {
      if (editing) {
        const dto = await Api.put(`/api/cerco/panels/${panel.id}`, body); closeModal(); toast("Panel actualizado.");
        const tr = document.getElementById(`cerco-row-${panel.id}`);
        if (tr) tr.outerHTML = cercoRow(dto, Api.role === "Admin");
      } else {
        const res = await Api.post("/api/cerco/panels", body); closeModal();
        cercoCredentialsModal(res.credentials, body.name);
        const tbody = document.getElementById("cerco-rows");
        if (tbody) tbody.insertAdjacentHTML("beforeend", cercoRow(res.panel, true)); else renderCercoPanels();
      }
    } catch (err) { toast(err.error, true); }
  });
}

// Credenciales estilo ISUP: se muestran UNA vez.
function cercoCredentialsModal(creds, name) {
  openModal(`
    <h3>Credenciales de "${esc(name)}"</h3>
    <div class="info-box"><b>Anótelas ahora: no se vuelven a mostrar.</b> Cárguelas en el panel con la herramienta de
      provisioning (SoftAP). Si las pierde, rote la clave y re-provisione.</div>
    <div class="field"><label>ID de equipo</label><input readonly id="cr-id" value="${esc(creds.deviceId)}"></div>
    <div class="field"><label>Clave (PSK)</label><input readonly id="cr-psk" value="${esc(creds.psk)}"></div>
    <div class="field"><label>URL WebSocket</label><input readonly id="cr-ws" value="${esc(creds.wsUrlHint)}"></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="cr-copy">Copiar todo</button>
      <button class="btn" type="button" id="cr-close">Listo</button>
    </div>`, true);
  $("#cr-close").addEventListener("click", closeModal);
  $("#cr-copy").addEventListener("click", async () => {
    const txt = `device_id=${creds.deviceId}\npsk=${creds.psk}\nws_url=${creds.wsUrlHint}`;
    try { await navigator.clipboard.writeText(txt); toast("Credenciales copiadas."); }
    catch { toast("No se pudo copiar; cópielas a mano.", true); }
  });
}

// --------------------------------------------------- configuración + controles RF
// Modal por panel: potencia, sirena, retardo, llave, zona 0 y administrador de
// controles RF. Se actualiza en vivo por el hub mientras está abierto.
const CERCO_KEY_MODES = [
  ["Off", "Deshabilitada"],
  ["Level", "Nivel: cerrada = armado, abierta = desarmado"],
  ["Toggle", "Pulso: cada activación arma o desarma"],
];
const CERCO_ZONE_MODES = [
  ["Off", "Deshabilitada"],
  ["Instant", "Instantánea: alarma solo con el cerco armado"],
  ["H24", "24 horas: alarma siempre (pánico / sabotaje)"],
];
const CERCO_RF_ACTIONS = [
  ["Arm", "Armar"], ["Disarm", "Desarmar"], ["Toggle", "Armar / desarmar"],
  ["Panic", "Pánico (sirena)"], ["Silence", "Silenciar sirena"],
];
const cercoOpts = (list, sel) => list.map(([v, t]) => `<option value="${v}" ${v === sel ? "selected" : ""}>${esc(t)}</option>`).join("");
const cercoActName = (a) => (CERCO_RF_ACTIONS.find(([v]) => v === a) ?? [a, a])[1];
let cercoCfg = null;   // { panelId, learnTimer, learnLeft } mientras el modal está abierto

async function cercoConfigModal(panel) {
  let config, remotes;
  try {
    [config, remotes] = await Promise.all([
      Api.get(`/api/cerco/panels/${panel.id}/config`),
      Api.get(`/api/cerco/panels/${panel.id}/remotes`),
    ]);
  } catch (err) { toast(err.error, true); return; }

  openModal(`
    <h3>Configurar "${esc(panel.name)}"</h3>
    <div id="cfg-sync" class="muted" style="font-size:12px;margin-bottom:8px"></div>

    <h4 style="margin:12px 0 4px">Energizador</h4>
    <div class="field">
      <label>Potencia (Nivel Voltaje): <b id="cfg-level-v">${config.hvLevel}</b> / 21</label>
      <input type="range" id="cfg-level" min="7" max="21" step="1" value="${config.hvLevel}">
      <div class="muted" style="font-size:11px">Más nivel = más tiempo de carga = pulso más fuerte. El panel no mide kV.</div>
    </div>

    <h4 style="margin:12px 0 4px">Sirena y armado</h4>
    <div class="field"><label>Duración de la sirena de alarma (segundos, 10–900)</label>
      <input type="number" id="cfg-siren" min="10" max="900" value="${config.sirenSeconds}"></div>
    <div class="field"><label>Retardo de salida antes de energizar (segundos, 0–120)</label>
      <input type="number" id="cfg-exit" min="0" max="120" value="${config.exitDelaySeconds}"></div>
    <div class="field checkbox"><label><input type="checkbox" id="cfg-chirp" ${config.chirp ? "checked" : ""}> Chirp de sirena al armar/desarmar</label></div>

    <h4 style="margin:12px 0 4px">Llave</h4>
    <div class="field"><select id="cfg-key">${cercoOpts(CERCO_KEY_MODES, config.keyMode)}</select>
      <div class="muted" style="font-size:11px">Estado actual: <b id="cfg-key-now">${panel.keyOn ? "cerrada" : "abierta"}</b></div></div>

    <h4 style="margin:12px 0 4px">Zona 0 (entrada cableada)</h4>
    <div class="field"><select id="cfg-z0">${cercoOpts(CERCO_ZONE_MODES, config.zone0Mode)}</select></div>
    <div class="field checkbox"><label><input type="checkbox" id="cfg-z0block" ${config.zone0BlocksArm ? "checked" : ""}> Si está abierta, no permitir armar</label></div>
    <div class="muted" style="font-size:11px">Lectura actual: <b id="cfg-z0adc">—</b> (normal entre 384 y 895; fuera = corto o línea abierta)</div>

    <div class="modal-actions">
      <button class="btn" type="button" id="cfg-save">Guardar configuración</button>
    </div>

    <h4 style="margin:18px 0 4px">Controles remotos (433 MHz)</h4>
    <div class="muted" style="font-size:11px;margin-bottom:6px">Cada botón se programa con su acción. Un control de 2 botones = 2 entradas.</div>
    <div class="toolbar" style="gap:6px;flex-wrap:wrap">
      <input id="rf-name" maxlength="64" placeholder="Nombre (ej. Control Juan - botón A)" style="flex:1;min-width:180px">
      <select id="rf-action">${cercoOpts(CERCO_RF_ACTIONS, "Toggle")}</select>
      <button class="btn" type="button" id="rf-learn">Programar botón</button>
    </div>
    <div id="rf-learning" class="info-box hidden"></div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>#</th><th>Nombre</th><th>Acción</th><th>Código</th><th></th></tr></thead>
      <tbody id="rf-rows"></tbody>
    </table></div>

    <div class="modal-actions">
      <button class="btn danger ghost" type="button" id="rf-clear">Eliminar todos los controles</button>
      <button class="btn ghost" type="button" id="cfg-close">Cerrar</button>
    </div>`, "wider");

  cercoCfg = { panelId: panel.id, learnTimer: null, learnLeft: 0 };
  cercoCfgOnState(panel);
  cercoCfgRenderRemotes(remotes);

  $("#cfg-level").addEventListener("input", (e) => { $("#cfg-level-v").textContent = e.target.value; });
  $("#cfg-close").addEventListener("click", () => { cercoCfgStopLearn(); cercoCfg = null; closeModal(); });

  $("#cfg-save").addEventListener("click", async () => {
    const body = {
      hvLevel: Number($("#cfg-level").value),
      sirenSeconds: Number($("#cfg-siren").value),
      exitDelaySeconds: Number($("#cfg-exit").value),
      chirp: $("#cfg-chirp").checked,
      keyMode: $("#cfg-key").value,
      zone0Mode: $("#cfg-z0").value,
      zone0BlocksArm: $("#cfg-z0block").checked,
    };
    try {
      const res = await Api.put(`/api/cerco/panels/${panel.id}/config`, body);
      toast(res.sent ? "Configuración enviada al panel." : "Guardada. Se aplicará cuando el panel se conecte.");
    } catch (err) { toast(err.error, true); }
  });

  $("#rf-learn").addEventListener("click", async () => {
    const name = $("#rf-name").value.trim();
    if (!name) { toast("Ponle un nombre al botón.", true); return; }
    try {
      await Api.post(`/api/cerco/panels/${panel.id}/remotes/learn`, { name, action: $("#rf-action").value });
      cercoCfgStartLearn(name);
    } catch (err) { toast(err.error, true); }
  });

  $("#rf-clear").addEventListener("click", async () => {
    if (!confirm("¿Eliminar TODOS los controles remotos de este panel? Dejarán de funcionar de inmediato.")) return;
    try { await Api.delete(`/api/cerco/panels/${panel.id}/remotes`); toast("Eliminando controles…"); }
    catch (err) { toast(err.error, true); }
  });

  // Acciones por fila (renombrar / eliminar).
  $("#rf-rows").addEventListener("click", async (e) => {
    const b = e.target.closest("button[data-slot]");
    if (!b) return;
    const slot = Number(b.dataset.slot);
    if (b.classList.contains("rf-del")) {
      if (!confirm(`¿Eliminar el botón "${b.dataset.name}"?`)) return;
      try { await Api.delete(`/api/cerco/panels/${panel.id}/remotes/${slot}`); } catch (err) { toast(err.error, true); }
    } else if (b.classList.contains("rf-save")) {
      const input = document.getElementById(`rf-n-${slot}`);
      try { await Api.put(`/api/cerco/panels/${panel.id}/remotes/${slot}`, { name: input.value.trim() }); toast("Nombre guardado."); }
      catch (err) { toast(err.error, true); }
    }
  });
}

function cercoCfgRenderRemotes(remotes) {
  const tb = document.getElementById("rf-rows");
  if (!tb) return;
  tb.innerHTML = remotes.length === 0
    ? `<tr><td colspan="5" class="muted">No hay controles programados.</td></tr>`
    : remotes.map((r) => `
      <tr>
        <td class="muted">${r.slot + 1}</td>
        <td><input id="rf-n-${r.slot}" maxlength="64" value="${esc(r.name)}" style="width:100%"></td>
        <td>${esc(cercoActName(r.action))}</td>
        <td class="muted" title="${r.bits} bits">${esc(r.code)}</td>
        <td class="row-actions">
          <button class="btn ghost rf-save" data-slot="${r.slot}">Guardar</button>
          <button class="btn danger rf-del" data-slot="${r.slot}" data-name="${esc(r.name)}">Eliminar</button>
        </td>
      </tr>`).join("");
}

function cercoCfgOnState(p) {
  if (!cercoCfg || cercoCfg.panelId !== p.id) return;
  const sync = document.getElementById("cfg-sync");
  if (sync) sync.innerHTML = !p.connected
    ? `<span class="tag">panel desconectado</span> los cambios se aplicarán cuando se conecte`
    : p.configSynced ? `<span class="tag ok">configuración aplicada en el panel</span>`
                     : `<span class="tag operator">aplicando…</span>`;
  const adc = document.getElementById("cfg-z0adc");
  if (adc) adc.textContent = p.zone0Adc == null ? "—"
    : `${p.zone0Adc} → ${p.zone0Adc >= 384 && p.zone0Adc <= 895 ? "normal" : "abierta/corto"}`;
  const key = document.getElementById("cfg-key-now");
  if (key) key.textContent = p.keyOn ? "cerrada" : "abierta";
}

function cercoCfgOnRemotes(panelId, remotes) {
  if (!cercoCfg || cercoCfg.panelId !== panelId) return;
  cercoCfgRenderRemotes(remotes);
}

function cercoCfgOnEvent(e) {
  if (!cercoCfg || cercoCfg.panelId !== e.panelId) return;
  if (e.kind === "RfLearned") { cercoCfgStopLearn(); toast(e.description); $("#rf-name").value = ""; }
  else if (e.kind === "RfLearnTimeout") { cercoCfgStopLearn(); toast(e.description, true); }
}

function cercoCfgStartLearn(name) {
  cercoCfgStopLearn();
  const box = document.getElementById("rf-learning");
  cercoCfg.learnLeft = 30;
  const paint = () => { if (box) box.innerHTML = `Presiona ahora el botón del control para <b>${esc(name)}</b>… <b>${cercoCfg.learnLeft}</b> s`; };
  box?.classList.remove("hidden"); paint();
  $("#rf-learn").disabled = true;
  cercoCfg.learnTimer = setInterval(() => {
    if (!cercoCfg) return;
    cercoCfg.learnLeft = Math.max(0, cercoCfg.learnLeft - 1); paint();
    if (cercoCfg.learnLeft === 0) cercoCfgStopLearn();   // el panel manda rf_learn_timeout
  }, 1000);
}

function cercoCfgStopLearn() {
  if (cercoCfg?.learnTimer) { clearInterval(cercoCfg.learnTimer); cercoCfg.learnTimer = null; }
  document.getElementById("rf-learning")?.classList.add("hidden");
  const b = document.getElementById("rf-learn"); if (b) b.disabled = false;
}

// ------------------------------------------------------------------- monitor
async function renderCercoMonitor() {
  cercoDetachHub();
  $("#page-title").textContent = "Cerco eléctrico";
  let panels, events;
  try { [panels, events] = await Promise.all([Api.get("/api/cerco/panels"), Api.get(`/api/cerco/events?take=${CERCO_MAX_EVENTS}`)]); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  $("#view").innerHTML = `
    <div class="toolbar"><h3>Monitor de cercos <span class="muted" style="font-weight:normal;font-size:12px">(tiempo real)</span></h3>
      <button class="btn ghost" id="cerco-history" title="Todos los eventos guardados, con filtros y exportación">Historial</button></div>
    ${panels.length === 0 ? `<div class="info-box">No hay paneles de cerco configurados.</div>` : `
    <div class="card-grid" id="cerco-cards">
      ${panels.map((p) => cercoCard(p)).join("")}
    </div>`}
    <h3 style="margin-top:20px">Eventos recientes</h3>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Hora</th><th>Panel</th><th>Evento</th><th>Zona</th></tr></thead>
      <tbody id="cerco-events">
        ${events.map(cercoEventRow).join("") || `<tr id="cerco-ev-empty"><td colspan="4" class="muted">Sin eventos aún.</td></tr>`}
      </tbody>
    </table></div>`;

  // Delegación de comandos.
  $("#view").addEventListener("click", onCercoCmdClick);
  $("#cerco-history").addEventListener("click", () => cercoHistoryModal(panels));

  // Tiempo real: tarjeta que cambió + eventos que llegan.
  cercoBind("CercoPanelStateChanged", (p) => {
    const card = document.getElementById(`cerco-card-${p.id}`);
    if (card) card.outerHTML = cercoCard(p);
    else document.getElementById("cerco-cards")?.insertAdjacentHTML("beforeend", cercoCard(p));
  });
  cercoBind("CercoEventReceived", (e) => {
    const tb = document.getElementById("cerco-events");
    if (!tb) return;
    document.getElementById("cerco-ev-empty")?.remove();
    tb.insertAdjacentHTML("afterbegin", cercoEventRow(e));
    while (tb.children.length > CERCO_MAX_EVENTS) tb.lastElementChild.remove();
  });
  // Al reconectar, recargar una vez para no perder cambios ocurridos offline.
  cercoReconnectUnsub = VmsHub.onReconnected(() => { if (location.hash === "#/cerco") renderCercoMonitor(); });
}

async function onCercoCmdClick(e) {
  const btn = e.target.closest(".btn-cmd");
  if (!btn) return;
  const id = Number(btn.dataset.id), cmd = btn.dataset.cmd;
  btn.disabled = true;
  try { await Api.post(`/api/cerco/panels/${id}/${cmd}`); toast("Comando enviado."); }
  catch (err) { toast(err.error ?? "No se pudo enviar el comando.", true); btn.disabled = false; }
  // El estado real llega por el hub; no re-render aquí (evita parpadeo).
}

function cercoCard(p) {
  const off = !p.connected;
  return `
    <div class="mon-card ${p.siren ? "alarm" : ""}" id="cerco-card-${p.id}">
      <div class="mon-head"><b>${esc(p.name)}</b>${cercoConnDot(p)}</div>
      <div class="muted" style="font-size:12px">${esc(p.site ?? p.deviceId)}</div>
      <div class="mon-stats">
        ${p.arming ? `<span class="tag operator">Armando…</span>`
                   : `<span class="${p.armed ? "tag ok" : "tag"}">${p.armed ? "Armado" : "Desarmado"}</span>`}
        ${(p.zones ?? []).filter((z) => z.inAlarm).map((z) => `<span class="tag danger">Zona ${z.number} en alarma</span>`).join("")}
        ${p.keyOn ? `<span class="tag" title="Entrada de llave cerrada">Llave</span>` : ""}
        ${p.siren ? `<span class="tag danger">Sirena</span>` : ""}
        ${p.armed ? (p.hvOk ? `<span class="tag ok">Pulso OK</span>` : `<span class="tag danger">Sin retorno</span>`) : ""}
        ${!p.fenceOk ? `<span class="tag danger">Cerco caído</span>` : ""}
        ${p.voltage != null ? `<span class="tag" title="Nivel Voltaje configurado (7–21): tiempo de carga del energizador. El panel no mide kV.">Nivel ${p.voltage}/21</span>` : ""}
      </div>
      <div class="mon-actions">
        ${p.armed
          ? `<button class="btn ghost btn-cmd" data-id="${p.id}" data-cmd="disarm" ${off ? "disabled" : ""}>Desarmar</button>`
          : `<button class="btn btn-cmd" data-id="${p.id}" data-cmd="arm" ${off ? "disabled" : ""}>Armar</button>`}
        <button class="btn danger btn-cmd" data-id="${p.id}" data-cmd="silence" ${off || !p.siren ? "disabled" : ""}>Silenciar</button>
      </div>
      ${off ? `<div class="muted" style="font-size:11px;margin-top:6px">Sin conexión — comandos deshabilitados.</div>` : ""}
    </div>`;
}

// --------------------------------------------------------------- historial
// Todos los eventos guardados en la BD, con filtros, paginación y CSV (admin).
const CERCO_KIND_LABELS = [
  ["Armed", "Cerco armado"], ["Disarmed", "Cerco desarmado"], ["ArmFailed", "Armado rechazado"],
  ["FenceCut", "Caída/corte del cerco"], ["Alarm", "Alarma de zona"], ["ZoneRestore", "Zona normal"],
  ["Panic", "Pánico"], ["SirenOn", "Sirena activada"], ["SirenOff", "Sirena silenciada"],
  ["RfRemote", "Silenciada desde control"], ["RfLearned", "Control programado"],
  ["RfLearnTimeout", "Programación sin respuesta"], ["Boot", "Arranque del panel"],
];
const CERCO_HIST_PAGE = 50;

function cercoHistoryModal(panels) {
  const isAdmin = Api.role === "Admin";
  openModal(`
    <h3>Historial de eventos de cerco</h3>
    <div class="toolbar" style="gap:6px;flex-wrap:wrap;align-items:flex-end">
      <div class="field" style="margin:0"><label>Panel</label>
        <select id="h-panel"><option value="">Todos</option>${panels.map((p) => `<option value="${p.id}">${esc(p.name)}</option>`).join("")}</select></div>
      <div class="field" style="margin:0"><label>Evento</label>
        <select id="h-kind"><option value="">Todos</option>${cercoOpts(CERCO_KIND_LABELS, "")}</select></div>
      <div class="field" style="margin:0"><label>Desde</label><input type="datetime-local" id="h-from"></div>
      <div class="field" style="margin:0"><label>Hasta</label><input type="datetime-local" id="h-to"></div>
      <button class="btn" type="button" id="h-search">Buscar</button>
      ${isAdmin ? `<button class="btn ghost" type="button" id="h-export" title="Descarga los eventos que coinciden con los filtros (máximo 100.000)">Exportar CSV</button>` : ""}
    </div>
    <div id="h-results"><div class="info-box">Cargando…</div></div>
    <div class="modal-actions"><button class="btn ghost" type="button" id="h-close">Cerrar</button></div>`, "wider");

  let page = 1;
  const query = () => {
    const q = new URLSearchParams();
    for (const [id, key] of [["h-panel", "panelId"], ["h-kind", "kind"], ["h-from", "from"], ["h-to", "to"]]) {
      const v = document.getElementById(id).value;
      if (v) q.set(key, v);
    }
    return q;
  };
  const load = async () => {
    const box = document.getElementById("h-results");
    const q = query(); q.set("page", page); q.set("pageSize", CERCO_HIST_PAGE);
    let data;
    try { data = await Api.get(`/api/cerco/events/history?${q}`); }
    catch (err) { box.innerHTML = `<div class="error-box">${esc(err.error ?? "No se pudo cargar el historial.")}</div>`; return; }
    const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
    box.innerHTML = data.total === 0 ? `<div class="info-box">No hay eventos que coincidan con los filtros.</div>` : `
      <div class="table-scroll" style="max-height:55vh"><table class="grid">
        <thead><tr><th>Hora</th><th>Panel</th><th>Evento</th><th>Zona</th></tr></thead>
        <tbody>${data.items.map(cercoEventRow).join("")}</tbody>
      </table></div>
      <div class="toolbar" style="margin-top:8px">
        <span class="muted" style="font-size:12px">${data.total} eventos · página ${data.page} de ${pages}</span>
        <span>
          <button class="btn ghost" type="button" id="h-prev" ${data.page <= 1 ? "disabled" : ""}>Anterior</button>
          <button class="btn ghost" type="button" id="h-next" ${data.page >= pages ? "disabled" : ""}>Siguiente</button>
        </span>
      </div>`;
    document.getElementById("h-prev")?.addEventListener("click", () => { page--; load(); });
    document.getElementById("h-next")?.addEventListener("click", () => { page++; load(); });
  };

  $("#h-close").addEventListener("click", closeModal);
  $("#h-search").addEventListener("click", () => { page = 1; load(); });
  $("#h-export")?.addEventListener("click", () => {
    const q = query(); q.set("access_token", Api.token);
    window.open(`/api/cerco/events/export?${q}`);
  });
  load();
}

function cercoEventRow(e) {
  return `<tr>
    <td class="muted">${new Date(e.receivedAt).toLocaleString()}</td>
    <td>${esc(e.panelName)}</td>
    <td>${cercoEvTag(e)}</td>
    <td class="muted">${e.zoneNumber ?? "—"}</td>
  </tr>`;
}

function cercoEvTag(e) {
  const cls = e.severity === "Critical" ? "danger" : e.severity === "Warning" ? "operator" : "";
  const v = e.verified ? "" : ` <span class="muted" title="HMAC no verificado">⚠</span>`;
  return `<span class="tag ${cls}">${esc(e.description)}</span>${v}`;
}
