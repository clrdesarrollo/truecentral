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
         "PowerLost", "PowerRestored", "Reconnected", "FirmwareUpdated"],
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

  const isAdmin = Perms.can("cerco.configure");
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Paneles de cerco <span class="muted" style="font-weight:normal;font-size:12px">(tiempo real)</span></h3>
      ${isAdmin ? `<button class="btn" id="btn-cerco-new">Agregar panel</button>` : ""}
    </div>
    ${panels.length === 0 ? `
      <div class="info-box">
        Aún no hay paneles de cerco. ${isAdmin
          ? "Use <b>Agregar panel</b>: el sistema genera un <b>ID de equipo</b> (6 dígitos) y un <b>código de enrolamiento</b> (8 dígitos) que el instalador carga en el panel. Desde que el panel se conecta, el guardia puede armar, desarmar y silenciar."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>ID de equipo</th><th>Ubicación</th><th>Firmware</th>
          <th>Armado</th><th>Sirena</th><th>Nivel AV</th><th>Señal</th><th>Conexión</th><th></th>
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
  cercoBind("CercoOtaProgress", (m) => cercoCfgOnOta(m));
  cercoReconnectUnsub = VmsHub.onReconnected(() => { if (location.hash === "#/cerco-panels") renderCercoPanels(); });
}

// Señal WiFi del panel (RSSI que reporta en cada "state"). Umbrales para el ESP8266:
// >= -67 dBm buena, -68..-75 regular, < -75 débil (pierde la asociación de vez en cuando).
function cercoSignal(p, compact) {
  if (p.rssi == null || !p.connected) return compact ? `<span class="muted">—</span>` : "";
  const r = p.rssi;
  const [cls, label, bars] = r >= -67 ? ["ok", "buena", 4] : r >= -75 ? ["warn", "regular", 3] : r >= -82 ? ["danger", "débil", 2] : ["danger", "muy débil", 1];
  const svg = `<svg class="sig-bars" viewBox="0 0 16 12" aria-hidden="true">${[0, 1, 2, 3].map((i) =>
    `<rect x="${i * 4}" y="${9 - i * 3}" width="3" height="${3 + i * 3}" rx="0.6" opacity="${i < bars ? 1 : 0.25}"/>`).join("")}</svg>`;
  return `<span class="tag ${cls} sig" title="Señal WiFi ${label}: ${r} dBm. Con menos de -75 dBm el panel puede perder la conexión de vez en cuando; revise la antena o acerque el punto de acceso.">${svg}${compact ? "" : "Señal "}${r} dBm</span>`;
}

function cercoRow(p, isAdmin) {
  return `
    <tr id="cerco-row-${p.id}" data-id="${p.id}" data-name="${esc(p.name)}">
      <td>${esc(p.name)}</td>
      <td class="muted">${esc(p.deviceId)}${p.enrolled ? "" : ` <span class="tag warn" title="Esperando que el instalador cargue el código de enrolamiento en el equipo">sin enrolar</span>`}</td>
      <td class="muted">${p.location ? esc(p.location) : `<span class="loc-line none">Por ubicar</span>`}</td>
      <td class="muted">${esc(p.firmware ?? "—")}</td>
      <td>${p.armed ? `<span class="tag ok">armado</span>` : `<span class="tag">desarmado</span>`}</td>
      <td>${p.siren ? `<span class="tag danger">sonando</span>` : "—"}</td>
      <td class="muted">${p.voltage ?? "—"}</td>
      <td>${cercoSignal(p, true)}</td>
      <td>${cercoConnDot(p)}</td>
      <td class="row-actions">
        ${isAdmin ? `<button class="btn btn-config" title="Potencia, sirena, zona, llave, firmware, controles remotos y zona de riesgo (rotar clave, reiniciar, restaurar)">Configurar</button>
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
  }
}

async function cercoPanelModal(panel) {
  const editing = !!panel;
  const places = await loadLocationChoices();
  openModal(`
    <h3>${editing ? "Editar" : "Agregar"} panel de cerco</h3>
    <div class="field"><label>Nombre</label><input id="c-name" maxlength="128" value="${editing ? esc(panel.name) : ""}" placeholder="Cerco perímetro norte"></div>
    ${locationFieldHtml("c-location", places, editing ? panel.locationId ?? null : null)}
    <div class="field checkbox"><label><input type="checkbox" id="c-enabled" ${!editing || panel.enabled ? "checked" : ""}> Habilitado</label></div>
    ${editing ? "" : `<div class="info-box">Al guardar, el sistema genera el <b>ID de equipo</b> (6 dígitos) y el <b>código de enrolamiento</b> (8 dígitos, válido 48 h). Se muestran una sola vez: el instalador los carga en el panel.</div>`}
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="c-cancel">Cancelar</button>
      <button class="btn" type="button" id="c-save">${editing ? "Guardar" : "Crear y generar clave"}</button>
    </div>`);
  $("#c-cancel").addEventListener("click", closeModal);
  $("#c-save").addEventListener("click", async () => {
    const body = { name: $("#c-name").value.trim(), locationId: locationFieldValue("c-location"), enabled: $("#c-enabled").checked };
    if (!body.name) { toast("El nombre es obligatorio.", true); return; }
    try {
      if (editing) {
        const dto = await Api.put(`/api/cerco/panels/${panel.id}`, body); closeModal(); toast("Panel actualizado.");
        const tr = document.getElementById(`cerco-row-${panel.id}`);
        if (tr) tr.outerHTML = cercoRow(dto, Perms.can("cerco.configure"));
      } else {
        const res = await Api.post("/api/cerco/panels", body); closeModal();
        cercoCredentialsModal(res.credentials, body.name);
        const tbody = document.getElementById("cerco-rows");
        if (tbody) tbody.insertAdjacentHTML("beforeend", cercoRow(res.panel, true)); else renderCercoPanels();
      }
    } catch (err) { toast(err.error, true); }
  });
}

// Credenciales estilo ISUP: se muestran UNA vez. La PSK no existe todavía: se deriva
// del código en la primera conexión del panel.
function cercoCredentialsModal(creds, name) {
  const exp = creds.expiresAt ? new Date(creds.expiresAt).toLocaleString() : "";
  openModal(`
    <h3>Credenciales de "${esc(name)}"</h3>
    <div class="info-box"><b>Anótelas ahora: no se vuelven a mostrar.</b> El instalador carga el ID y el código en el
      panel (CLR Cerco Provisioner o portal del equipo). El código sirve una sola vez${exp ? ` y vence el ${esc(exp)}` : ""}.
      Si se pierde, rote la clave para generar otro.</div>
    <div class="field"><label>ID de equipo</label><input readonly id="cr-id" value="${esc(creds.deviceId)}"></div>
    <div class="field"><label>Código de enrolamiento</label><input readonly id="cr-code" value="${esc(creds.enrollCode)}"></div>
    <div class="field"><label>URL WebSocket</label><input readonly id="cr-ws" value="${esc(creds.wsUrlHint)}"></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="cr-copy">Copiar todo</button>
      <button class="btn" type="button" id="cr-close">Listo</button>
    </div>`, true);
  $("#cr-close").addEventListener("click", closeModal);
  $("#cr-copy").addEventListener("click", async () => {
    const txt = `device_id=${creds.deviceId}\nenroll_code=${creds.enrollCode}\nws_url=${creds.wsUrlHint}`;
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

    <h4 style="margin:18px 0 4px">Firmware</h4>
    <div class="muted" style="font-size:12px">Versión instalada: <b id="fw-current">${esc(panel.firmware ?? "—")}</b></div>
    <div class="toolbar" style="gap:6px;flex-wrap:wrap;margin-top:6px">
      <input type="file" id="fw-file" accept=".bin" style="flex:1;min-width:180px">
      <button class="btn" type="button" id="fw-send">Actualizar firmware</button>
    </div>
    <div id="fw-status" class="info-box hidden"></div>
    <div class="muted" style="font-size:11px">Solo este panel, y solo desarmado. Tarda alrededor de un minuto y el panel se reinicia.
      No hay vuelta atrás automática: pruebe cada versión en banco antes de subirla a un cerco en servicio.</div>

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
    </div>

    ${cercoDangerZoneHtml(panel)}

    <div class="modal-actions">
      <button class="btn ghost" type="button" id="cfg-close">Cerrar</button>
    </div>`, "wider");

  cercoCfg = { panelId: panel.id, learnTimer: null, learnLeft: 0 };
  cercoCfgOnState(panel);
  cercoCfgRenderRemotes(remotes);

  $("#cfg-level").addEventListener("input", (e) => { $("#cfg-level-v").textContent = e.target.value; });
  $("#cfg-close").addEventListener("click", () => { cercoCfgStopLearn(); cercoCfgStopOta(); cercoCfg = null; closeModal(); });

  $("#fw-send").addEventListener("click", async () => {
    const file = $("#fw-file").files[0];
    if (!file) { toast("Elija el archivo .bin del firmware.", true); return; }
    if (!confirm(`¿Actualizar el firmware de "${panel.name}" con ${file.name}?\n\nEl panel debe estar desarmado. Se reinicia al terminar (alrededor de un minuto).`)) return;
    const btn = $("#fw-send");
    btn.disabled = true;
    cercoCfgOtaStatus("Subiendo el archivo al servidor…", 0);
    try {
      const form = new FormData();
      form.append("file", file);
      const response = await fetch(`/api/cerco/panels/${panel.id}/firmware`, {
        method: "POST", headers: { Authorization: "Bearer " + Api.token }, body: form,
      });
      const data = await response.json().catch(() => null);
      if (!response.ok) throw { error: (data && data.error) || `Error ${response.status}` };
      cercoCfgStopOta();
      cercoCfg.ota = { target: data.version, timer: setTimeout(() => {
        cercoCfgOtaStatus(`No se confirmó la versión ${esc(data.version)}. Revise el historial del panel; si sigue con la anterior, reintente.`, null, true);
        cercoCfgStopOta();
      }, 180000) };
      cercoCfgOtaStatus(`Orden enviada: ${esc(data.current ?? "?")} → <b>${esc(data.version)}</b>. Esperando que el panel descargue…`, 0);
    } catch (err) {
      cercoCfgOtaStatus(esc(err.error ?? "No se pudo enviar el firmware."), null, true);
      btn.disabled = false;
    }
  });

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

  cercoDangerZoneBind(panel);

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

// ---- zona de riesgo ----
// Acciones que cortan la comunicación con el equipo. Cada una exige escribir el
// ID de equipo para habilitar el botón final (como la "Danger Zone" de GitHub);
// el servidor las audita (cerco/panel-key-rotated y cerco/command).
const CERCO_DANGER = [
  {
    act: "rotate", title: "Rotar clave", button: "Rotar clave",
    help: "Genera un código de enrolamiento nuevo (8 dígitos, válido 48 h) e invalida la clave actual del equipo.",
    risk: "El panel se desconecta de inmediato y NO vuelve a conectar hasta que el instalador cargue el código nuevo en el equipo (CLR Cerco Provisioner o portal del equipo). Mientras tanto no llegan alarmas ni se puede armar o desarmar desde la central.",
    needsConnection: false,
  },
  {
    act: "reboot", title: "Reiniciar el equipo", button: "Reiniciar",
    help: "Reinicia el panel de forma remota. No borra la configuración, la clave ni los controles remotos.",
    risk: "El panel queda fuera de línea mientras arranca y vuelve a conectarse (normalmente menos de un minuto; más si la señal WiFi es débil). En ese lapso no se reciben eventos ni se pueden enviar comandos. Si no reconecta, hay que ir a terreno.",
    needsConnection: true,
  },
  {
    act: "factory", title: "Restaurar de fábrica", button: "Restaurar",
    help: "Borra el WiFi, el servidor, la clave y la contraseña del equipo, y lo devuelve al portal de provisioning. Conserva los ajustes del cerco y los controles remotos.",
    risk: "Se pierde la comunicación de forma PERMANENTE: el panel no vuelve a conectar hasta que se provisione de nuevo en terreno (WiFi, servidor y código de enrolamiento). No se puede deshacer desde la central.",
    needsConnection: true,
  },
];

function cercoDangerZoneHtml(panel) {
  return `
    <div class="danger-zone" id="cfg-danger">
      <h4>Zona de riesgo</h4>
      <div class="dz-intro muted">Cualquiera de estas acciones corta la comunicación con el equipo. Para continuar hay que
        confirmar escribiendo el ID de equipo <b>${esc(panel.deviceId)}</b>.</div>
      ${CERCO_DANGER.map((d) => `
        <div class="dz-item" data-act="${d.act}">
          <div class="dz-row">
            <div>
              <b>${esc(d.title)}</b>
              <div class="dz-help">${esc(d.help)}</div>
              <div class="dz-help"><span class="dz-risk">Riesgo:</span> ${esc(d.risk)}</div>
            </div>
            <button class="btn dz-open" type="button">${esc(d.button)}</button>
          </div>
          <div class="dz-confirm hidden">
            <label>Para confirmar <b>${esc(d.title.toLowerCase())}</b> de «${esc(panel.name)}», escriba el ID de equipo <b>${esc(panel.deviceId)}</b>:</label>
            <div class="dz-confirm-row">
              <input class="dz-input" autocomplete="off" spellcheck="false" inputmode="numeric" placeholder="${esc(panel.deviceId)}">
              <button class="btn danger dz-go" type="button" disabled>Entiendo el riesgo: ${esc(d.button.toLowerCase())}</button>
              <button class="btn ghost dz-cancel" type="button">Cancelar</button>
            </div>
          </div>
        </div>`).join("")}
    </div>`;
}

function cercoDangerZoneBind(panel) {
  const zone = document.getElementById("cfg-danger");
  if (!zone) return;
  const close = (item) => {
    item.querySelector(".dz-confirm").classList.add("hidden");
    item.querySelector(".dz-input").value = "";
    item.querySelector(".dz-go").disabled = true;
  };
  zone.addEventListener("input", (e) => {
    if (!e.target.classList.contains("dz-input")) return;
    e.target.closest(".dz-item").querySelector(".dz-go").disabled = e.target.value.trim() !== String(panel.deviceId);
  });
  zone.addEventListener("click", async (e) => {
    const btn = e.target.closest("button");
    const item = btn?.closest(".dz-item");
    if (!item) return;
    const d = CERCO_DANGER.find((x) => x.act === item.dataset.act);
    if (btn.classList.contains("dz-open")) {
      zone.querySelectorAll(".dz-item").forEach((it) => { if (it !== item) close(it); });
      item.querySelector(".dz-confirm").classList.remove("hidden");
      item.querySelector(".dz-input").focus();
    } else if (btn.classList.contains("dz-cancel")) {
      close(item);
    } else if (btn.classList.contains("dz-go")) {
      if (item.querySelector(".dz-input").value.trim() !== String(panel.deviceId)) return;
      btn.disabled = true;
      try {
        if (d.act === "rotate") {
          const creds = await Api.post(`/api/cerco/panels/${panel.id}/rotate-key`);
          // Las credenciales se muestran una sola vez: reemplazan al modal de configuración.
          cercoCfgStopLearn(); cercoCfgStopOta(); cercoCfg = null;
          cercoCredentialsModal(creds, panel.name);
          return;
        }
        await Api.post(`/api/cerco/panels/${panel.id}/${d.act}`);
        toast(d.act === "reboot" ? "Reinicio enviado: el panel vuelve en alrededor de un minuto."
                                 : "Restauración enviada: el panel vuelve al portal de provisioning.");
        close(item);
      } catch (err) {
        toast(err.error ?? "No se pudo enviar la orden.", true);
        btn.disabled = false;
      }
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
  const fw = document.getElementById("fw-current");
  if (fw) fw.textContent = p.firmware ?? "—";
  // Reiniciar y restaurar viajan por la conexión: sin panel conectado no se pueden entregar.
  for (const d of CERCO_DANGER.filter((x) => x.needsConnection)) {
    const open = document.querySelector(`#cfg-danger .dz-item[data-act="${d.act}"] .dz-open`);
    if (!open) continue;
    open.disabled = !p.connected;
    open.title = p.connected ? "" : "El panel no está conectado: la orden no se puede entregar.";
  }
  // OTA confirmada: el panel volvió con la versión pedida.
  if (cercoCfg.ota && p.connected && p.firmware === cercoCfg.ota.target) {
    cercoCfgOtaStatus(`Actualizado: el panel volvió con la versión <b>${esc(p.firmware)}</b>.`, null);
    cercoCfgStopOta();
  }
}

// ---- actualización de firmware (OTA) ----
const CERCO_OTA_ERRORS = {
  armed: "el panel está armado; desármelo y reintente",
  busy: "ya hay una actualización en curso",
  url: "dirección de descarga inválida",
  size: "la imagen no cabe en el panel o su tamaño no coincide",
  sha: "la imagen llegó distinta (huella SHA-256); no se instaló",
  incomplete: "la descarga se cortó; no se instaló",
  begin: "el panel no pudo preparar la flash",
  write: "falló la escritura en la flash",
  sign: "el archivo no tiene una firma válida de CLRobotics; no se instaló",
  sign_key: "el panel no pudo cargar su clave de verificación",
};
function cercoOtaError(code) {
  if (!code) return "error desconocido";
  if (CERCO_OTA_ERRORS[code]) return CERCO_OTA_ERRORS[code];
  if (code.startsWith("http_")) return `el panel no pudo descargar el archivo (HTTP ${code.slice(5)}). Revise que el firewall del servidor permita el puerto 5093.`;
  if (code.startsWith("end_")) return `la verificación final falló (código ${code.slice(4)}); no se instaló`;
  return code;
}
function cercoCfgOtaStatus(html, pct, isError) {
  const box = document.getElementById("fw-status");
  if (!box) return;
  box.classList.remove("hidden");
  box.style.borderColor = isError ? "var(--danger)" : "";
  box.innerHTML = html + (pct == null ? "" :
    `<div style="height:6px;background:rgba(139,152,165,.25);border-radius:3px;margin-top:6px;overflow:hidden">
       <div style="height:100%;width:${pct}%;background:var(--accent);transition:width .3s"></div></div>`);
}
function cercoCfgStopOta() {
  if (cercoCfg?.ota?.timer) clearTimeout(cercoCfg.ota.timer);
  if (cercoCfg) cercoCfg.ota = null;
  const btn = document.getElementById("fw-send");
  if (btn) btn.disabled = false;
}
function cercoCfgOnOta(m) {
  if (!cercoCfg || cercoCfg.panelId !== m.panelId) return;
  const target = cercoCfg.ota?.target ? ` a <b>${esc(cercoCfg.ota.target)}</b>` : "";
  if (m.state === "start") cercoCfgOtaStatus(`Descargando el firmware${target}…`, 0);
  else if (m.state === "progress") cercoCfgOtaStatus(`Descargando y escribiendo${target}… ${m.pct}%`, m.pct);
  else if (m.state === "done") cercoCfgOtaStatus(`Imagen verificada e instalada${target}. El panel se está reiniciando…`, 100);
  else if (m.state === "error") {
    cercoCfgOtaStatus(`No se actualizó: ${esc(cercoOtaError(m.err))}. El panel sigue con su versión actual.`, null, true);
    cercoCfgStopOta();
  }
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
      <div class="muted" style="font-size:12px">${esc(p.location ?? p.deviceId)}</div>
      <div class="mon-stats">
        ${p.arming ? `<span class="tag operator">Armando…</span>`
                   : `<span class="${p.armed ? "tag ok" : "tag"}">${p.armed ? "Armado" : "Desarmado"}</span>`}
        ${(p.zones ?? []).filter((z) => z.inAlarm).map((z) => `<span class="tag danger">Zona ${z.number} en alarma</span>`).join("")}
        ${p.keyOn ? `<span class="tag" title="Entrada de llave cerrada">Llave</span>` : ""}
        ${p.siren ? `<span class="tag danger">Sirena</span>` : ""}
        ${p.armed ? (p.hvOk ? `<span class="tag ok">Pulso OK</span>` : `<span class="tag danger">Sin retorno</span>`) : ""}
        ${!p.fenceOk ? `<span class="tag danger">Cerco caído</span>` : ""}
        ${p.voltage != null ? `<span class="tag" title="Nivel Voltaje configurado (7–21): tiempo de carga del energizador. El panel no mide kV.">Nivel ${p.voltage}/21</span>` : ""}
        ${cercoSignal(p, false)}
      </div>
      <div class="mon-actions">
        ${p.armed
          ? `<button class="btn ghost btn-cmd" data-op="fence:${p.id}" data-id="${p.id}" data-cmd="disarm" ${off ? "disabled" : ""}>Desarmar</button>`
          : `<button class="btn btn-cmd" data-op="fence:${p.id}" data-id="${p.id}" data-cmd="arm" ${off ? "disabled" : ""}>Armar</button>`}
        <button class="btn danger btn-cmd" data-op="fence:${p.id}" data-id="${p.id}" data-cmd="silence" ${off || !p.siren ? "disabled" : ""}>Silenciar</button>
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
  ["Reconnected", "Reconexión con el servidor"], ["FirmwareUpdated", "Firmware actualizado"],
];
const CERCO_HIST_PAGE = 50;

function cercoHistoryModal(panels) {
  const isAdmin = Perms.can("cerco.monitor");
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
