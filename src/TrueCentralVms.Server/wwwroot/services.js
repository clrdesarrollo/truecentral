// CLR TrueCentral VMS — panel: supervisor de servicios (watchdog).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

let servicesTimer = null;
let servicesRowsCache = {}; // id → JSON de la última fila dibujada (refresco en sitio)

const SERVICE_STATES = {
  Running:  { cls: "on",       label: "En ejecución" },
  Starting: { cls: "admin",    label: "Iniciando" },
  Stopping: { cls: "admin",    label: "Deteniendo" },
  Stopped:  { cls: "operator", label: "Detenido" },
  Failed:   { cls: "off",      label: "Caído" },
  Disabled: { cls: "operator", label: "Deshabilitado" },
};

function serviceStateLabel(state) {
  return (SERVICE_STATES[state] || { label: state }).label;
}

function serviceStateTag(s) {
  const st = SERVICE_STATES[s.state] || { cls: "operator", label: s.state };
  return `<span class="tag ${st.cls}">${esc(st.label)}</span>`;
}

function formatUptime(seconds) {
  seconds = Math.max(0, Math.floor(seconds));
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  if (d) return `${d} d ${h} h`;
  if (h) return `${h} h ${m} min`;
  if (m) return `${m} min ${s} s`;
  return `${s} s`;
}

function serviceSinceCell(s) {
  if (!s.sinceUtc) return `<span class="muted">—</span>`;
  const secs = (Date.now() - new Date(s.sinceUtc).getTime()) / 1000;
  return `${formatUptime(secs)}<div class="muted" style="font-size:11px">${formatDate(s.sinceUtc)}</div>`;
}

function serviceErrorCell(s) {
  if (!s.lastError) return `<span class="muted">—</span>`;
  let retry = "";
  if (s.state === "Failed" && s.nextRetryAtUtc) {
    const wait = Math.max(0, Math.ceil((new Date(s.nextRetryAtUtc).getTime() - Date.now()) / 1000));
    retry = `<div class="muted" style="font-size:11px">Reintento automático en ${wait} s</div>`;
  }
  return `<div style="font-size:12px;color:var(--danger);max-width:320px" title="${esc(s.lastError)}">${esc(s.lastError)}</div>
    <div class="muted" style="font-size:11px">${formatDate(s.lastErrorAtUtc)}</div>${retry}`;
}

function serviceRowHtml(s, isAdmin) {
  const busy = s.state === "Starting" || s.state === "Stopping";
  const disabled = s.state === "Disabled";
  const canStart = !busy && !disabled && (s.state === "Stopped" || s.state === "Failed");
  const canStop = !busy && !disabled && s.canStop && s.state !== "Stopped";
  const canRestart = !busy && !disabled;
  const kind = s.kind === "process"
    ? `<span class="tag admin" title="Proceso hijo del servidor">Proceso</span>`
    : `<span class="tag operator" title="Subsistema interno del servidor">Interno</span>`;
  return `
    <td><b>${esc(s.name)}</b><div class="muted" style="font-size:11.5px;max-width:300px">${esc(s.description)}</div></td>
    <td>${kind}</td>
    <td>${serviceStateTag(s)}${s.detail ? `<div class="muted" style="font-size:11px;max-width:260px">${esc(s.detail)}</div>` : ""}</td>
    <td class="svc-since">${serviceSinceCell(s)}</td>
    <td>${s.restartCount}${s.failedAttempts ? `<div class="muted" style="font-size:11px">${s.failedAttempts} intento(s) fallido(s)</div>` : ""}</td>
    <td class="svc-error">${serviceErrorCell(s)}</td>
    <td><label class="checkbox-row" title="${disabled ? "" : "Si cae, el supervisor lo reinicia solo (con esperas crecientes)"}">
      <input type="checkbox" data-act="auto" ${s.autoRestart ? "checked" : ""} ${isAdmin && !disabled ? "" : "disabled"}> Auto</label></td>
    <td class="row-actions">${isAdmin ? `
      <button class="btn ghost" data-act="start" ${canStart ? "" : "disabled"}>Iniciar</button>
      <button class="btn ghost" data-act="stop" ${canStop ? "" : "disabled"} title="${s.canStop ? "" : "Servicio esencial: solo se puede reiniciar"}">Detener</button>
      <button class="btn ghost" data-act="restart" ${canRestart ? "" : "disabled"}>Reiniciar</button>` : `<span class="muted">Solo administradores</span>`}</td>`;
}

function serverCardsHtml(server, services) {
  const running = services.filter((s) => s.state === "Running").length;
  const failed = services.filter((s) => s.state === "Failed").length;
  const supervised = services.filter((s) => s.state !== "Disabled").length;
  return `
    <div class="cards">
      <div class="card">
        <div class="card-label">Servidor</div>
        <div class="card-value small">${server.isWindowsService
          ? `Servicio de Windows <span class="muted">(${esc(server.serviceName)})</span>`
          : "Consola (desarrollo)"}</div>
        <div class="muted" style="font-size:12px">v${esc(server.version)} · PID ${server.processId}</div>
      </div>
      <div class="card">
        <div class="card-label">En ejecución desde</div>
        <div class="card-value small">${formatUptime(server.uptimeSeconds)}</div>
        <div class="muted" style="font-size:12px">${formatDate(server.startedAtUtc)}</div>
      </div>
      <div class="card">
        <div class="card-label">Servicios</div>
        <div class="card-value" style="color:${failed ? "var(--danger)" : "var(--ok)"}">${running}
          <span class="muted" style="font-size:13px">de ${supervised} en ejecución</span></div>
        ${failed ? `<div style="font-size:12px;color:var(--danger)">${failed} caído(s)</div>` : ""}
      </div>
      <div class="card">
        <div class="card-label">Memoria del servidor</div>
        <div class="card-value">${server.workingSetMb} <span class="muted" style="font-size:13px">MB</span></div>
      </div>
      <div class="card">
        <div class="card-label">Supervisión</div>
        <div class="card-value small">cada ${server.checkIntervalSeconds} s</div>
        <div class="muted" style="font-size:12px">Comprobación de salud y reinicio automático</div>
      </div>
    </div>`;
}

async function renderServices() {
  $("#page-title").textContent = "Servicios";
  const isAdmin = Api.role === "Admin";
  let data;
  try { data = await Api.get("/api/system/services"); }
  catch (e) { $("#view").innerHTML = `<div class="error-box">${esc(e.error)}</div>`; return; }

  servicesRowsCache = {};
  $("#view").innerHTML = `
    <div id="svc-cards">${serverCardsHtml(data.server, data.services)}</div>
    <div class="toolbar">
      <h3>Supervisor de servicios</h3>
      ${isAdmin ? `<button class="btn danger" id="btn-server-restart" ${data.server.canRestart ? "" : "disabled"}
        title="${esc(data.server.restartHint || "Detiene y vuelve a iniciar el servicio de Windows completo")}">Reiniciar servidor completo</button>` : ""}
    </div>
    <div class="info-box">El supervisor comprueba cada servicio y, si uno cae, lo reinicia solo con esperas crecientes
      (5, 10, 20, 40, 60 s) hasta agotar los reintentos. Un servicio detenido a mano NO se reinicia hasta que un
      administrador lo inicie. La base de datos es esencial: solo se puede reiniciar. Cada caída, reinicio y orden
      queda en la bitácora de auditoría.</div>
    <div class="table-scroll"><table class="grid" id="svc-table">
      <thead><tr>
        <th>Servicio</th><th>Tipo</th><th>Estado</th><th>Desde</th><th>Reinicios</th>
        <th>Último error</th><th>Auto-reinicio</th><th></th>
      </tr></thead>
      <tbody>${data.services.map((s) => `<tr data-id="${esc(s.id)}">${serviceRowHtml(s, isAdmin)}</tr>`).join("")}</tbody>
    </table></div>`;
  data.services.forEach((s) => { servicesRowsCache[s.id] = JSON.stringify(s); });

  $("#svc-table").addEventListener("click", onServiceAction);
  $("#svc-table").addEventListener("change", onServiceAutoToggle);
  $("#btn-server-restart")?.addEventListener("click", restartServer);

  servicesTimer = setInterval(refreshServices, 3000);
}

/**
 * Refresco en sitio: las tarjetas se redibujan (no tienen controles), y de
 * la tabla solo las filas cuyo estado cambió; el tiempo "desde" y la cuenta
 * regresiva del reintento se actualizan siempre. Devuelve false si cambió el
 * conjunto de servicios (entonces se redibuja la página completa).
 */
async function refreshServices() {
  let data;
  try { data = await Api.get("/api/system/services"); }
  catch { return; }
  if (!$("#svc-table")) return;
  const isAdmin = Api.role === "Admin";

  const cards = $("#svc-cards");
  if (cards) cards.innerHTML = serverCardsHtml(data.server, data.services);

  const known = Object.keys(servicesRowsCache).sort().join(",");
  if (known !== data.services.map((s) => s.id).sort().join(",")) {
    clearInterval(servicesTimer);
    renderServices();
    return;
  }
  for (const s of data.services) {
    const row = $(`#svc-table tr[data-id="${s.id}"]`);
    if (!row) continue;
    const json = JSON.stringify(s);
    if (json !== servicesRowsCache[s.id]) {
      row.innerHTML = serviceRowHtml(s, isAdmin);
      servicesRowsCache[s.id] = json;
    } else {
      const since = row.querySelector(".svc-since");
      if (since) since.innerHTML = serviceSinceCell(s);
      const error = row.querySelector(".svc-error");
      if (error) error.innerHTML = serviceErrorCell(s);
    }
  }
}

async function onServiceAction(e) {
  const btn = e.target.closest("button[data-act]");
  if (!btn) return;
  const row = btn.closest("tr[data-id]");
  const id = row.dataset.id;
  const act = btn.dataset.act;
  const name = row.querySelector("td b")?.textContent || id;
  if (act === "stop" && !confirm(`¿Detener «${name}»? El supervisor no lo volverá a iniciar hasta que lo haga a mano.`)) return;
  if (act === "restart" && !confirm(`¿Reiniciar «${name}»? Las operaciones en curso de este servicio pueden fallar durante unos segundos.`)) return;
  row.querySelectorAll("button").forEach((b) => { b.disabled = true; });
  try {
    const result = await Api.post(`/api/system/services/${encodeURIComponent(id)}/${act}`);
    toast(result.message);
  } catch (err) {
    toast(err.error || "La operación falló.", true);
  }
  await refreshServices();
}

async function onServiceAutoToggle(e) {
  const input = e.target.closest("input[data-act='auto']");
  if (!input) return;
  const id = input.closest("tr[data-id]").dataset.id;
  try {
    const result = await Api.put(`/api/system/services/${encodeURIComponent(id)}/auto-restart`, { enabled: input.checked });
    toast(result.message);
  } catch (err) {
    toast(err.error || "No se pudo cambiar el auto-reinicio.", true);
    input.checked = !input.checked;
  }
  await refreshServices();
}

async function restartServer() {
  if (!confirm("¿Reiniciar el servidor completo? Se cortarán todas las sesiones de video, los clientes se " +
    "desconectarán y el sistema tardará unos segundos en volver.")) return;
  try {
    const result = await Api.post("/api/system/restart");
    toast(result.message);
  } catch (err) {
    toast(err.error || "No se pudo solicitar el reinicio.", true);
  }
}
