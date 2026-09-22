// CLR TrueCentral VMS — panel: citofonía (frentes de videoportero).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas. Las llamadas se
// contestan desde el cliente de escritorio; aquí se administran los frentes y
// se consulta el historial.
"use strict";

let intercomDriversCache = null;
async function getIntercomDrivers() {
  intercomDriversCache ??= await Api.get("/api/intercoms/drivers");
  return intercomDriversCache;
}

let intercomsTimer = null;

const INTERCOM_CALL_STATES = {
  Ringing: `<span class="tag admin">Sonando</span>`,
  InCall: `<span class="tag on">En conversación</span>`,
  Completed: `<span class="tag on">Contestada</span>`,
  Missed: `<span class="tag off">No contestada</span>`,
  Rejected: `<span class="tag operator">Rechazada</span>`,
};

function intercomStatusCell(i) {
  const tag = {
    Online: `<span class="tag on">En línea</span>`,
    Offline: `<span class="tag off">Sin conexión</span>`,
    AuthFailed: `<span class="tag off">Credenciales</span>`,
  }[i.status] ?? `<span class="tag operator">—</span>`;
  return `${tag}${i.lastError ? `<div class="muted" style="font-size:11px;max-width:240px" title="${esc(i.lastError)}">${esc(i.lastError)}</div>` : ""}`;
}

function intercomCallCell(i) {
  if (i.activeCall) {
    const c = i.activeCall;
    return `${INTERCOM_CALL_STATES[c.state] ?? esc(c.state)}${c.answeredBy ? ` <span class="muted">${esc(c.answeredBy)}</span>` : ""}`;
  }
  return i.callCenterEnabled
    ? `<span class="muted">libre</span>`
    : `<span class="tag off" title="El botón del frente no llama a la central: las llamadas no llegan al VMS. Edite el frente y marque 'Configurar el botón'.">No llama a la central</span>`;
}

function intercomDuration(c) {
  if (!c.answeredAt || !c.endedAt) return "—";
  const s = Math.round((new Date(c.endedAt) - new Date(c.answeredAt)) / 1000);
  return s >= 60 ? `${Math.floor(s / 60)} min ${s % 60} s` : `${s} s`;
}

async function renderIntercoms() {
  $("#page-title").textContent = "Citofonía";
  let intercoms;
  try { intercoms = await Api.get("/api/intercoms"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Frentes de citofonía <span class="muted" style="font-weight:normal;font-size:12px">(el estado se actualiza solo)</span></h3>
      <div class="row-actions">
        ${isAdmin ? `<button class="btn" id="btn-intercom-new">Agregar frente</button>` : ""}
      </div>
    </div>
    ${intercoms.length === 0 ? `
      <div class="info-box">
        Aún no hay frentes de citofonía. ${isAdmin
          ? "Use <b>Agregar frente</b>: al guardar se validan las credenciales contra el equipo y se configura su botón para que llame a la central. Desde ese momento, cuando un visitante toca el timbre, la llamada suena en el cliente de escritorio y el guardia puede contestar con video, hablar y abrir la puerta."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Grupo</th><th>Dirección</th><th>Modelo</th><th>Cámara</th><th>Puertas</th><th>Conexión</th><th>Llamada</th><th></th>
        </tr></thead>
        <tbody>
          ${intercoms.map((i) => `
            <tr data-id="${i.id}">
              <td>${esc(i.name)}${i.enabled ? "" : ` <span class="tag operator" title="Desactivado">pausado</span>`}</td>
              <td class="muted">${esc(i.groupName ?? "—")}</td>
              <td class="muted">${esc(i.host)}:${i.port} · HTTP ${i.httpPort}</td>
              <td>${esc(i.model ?? "—")}<div class="muted" style="font-size:11px">${esc(i.firmwareVersion ?? "")}</div></td>
              <td>${i.channelName ? esc(i.channelName) : `<span class="muted" title="Sin cámara asociada: al sonar no se verá video">sin video</span>`}</td>
              <td>${i.doorCount}</td>
              <td class="ic-status">${intercomStatusCell(i)}</td>
              <td class="ic-call">${intercomCallCell(i)}</td>
              <td><div class="row-actions" style="flex-wrap:wrap">
                <button class="btn ghost btn-history">Historial</button>
                ${isAdmin ? `<button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </div></td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}
    <div class="toolbar" style="margin-top:22px">
      <h3>Historial de llamadas</h3>
      <div class="row-actions">
        <select id="ic-filter-intercom">
          <option value="">Todos los frentes</option>
          ${intercoms.map((i) => `<option value="${i.id}">${esc(i.name)}</option>`).join("")}
        </select>
        <select id="ic-filter-state">
          <option value="">Todas</option>
          <option value="Completed">Contestadas</option>
          <option value="Missed">No contestadas</option>
          <option value="Rejected">Rechazadas</option>
        </select>
      </div>
    </div>
    <div id="ic-history"></div>`;

  const byRow = (e) => intercoms.find((i) => i.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-intercom-new")?.addEventListener("click", () => intercomModal(null));
  $$("#view .btn-edit").forEach((b) => b.addEventListener("click", (e) => intercomModal(byRow(e))));
  $$("#view .btn-history").forEach((b) => b.addEventListener("click", (e) => {
    $("#ic-filter-intercom").value = String(byRow(e).id);
    renderIntercomHistory(0);
    $("#ic-history").scrollIntoView({ behavior: "smooth" });
  }));
  $$("#view .btn-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const intercom = byRow(e);
    if (!confirm(`¿Eliminar el frente "${intercom.name}"? Su historial de llamadas se conserva.`)) return;
    try {
      await Api.delete(`/api/intercoms/${intercom.id}`);
      toast("Frente eliminado.");
      renderIntercoms();
    } catch (err) { toast(err.error, true); }
  }));
  $("#ic-filter-intercom").addEventListener("change", () => renderIntercomHistory(0));
  $("#ic-filter-state").addEventListener("change", () => renderIntercomHistory(0));
  renderIntercomHistory(0);

  // Estado y llamada en curso: se actualizan en sitio cada 5 s.
  clearInterval(intercomsTimer);
  intercomsTimer = setInterval(async () => {
    if (location.hash !== "#/intercoms" || $("#app-shell").classList.contains("hidden")) {
      clearInterval(intercomsTimer);
      return;
    }
    if ($("#modal-backdrop") && !$("#modal-backdrop").classList.contains("hidden")) return;
    try {
      const fresh = await Api.get("/api/intercoms");
      if (fresh.map((i) => i.id).join(",") !== intercoms.map((i) => i.id).join(",")) { renderIntercoms(); return; }
      let callChanged = false;
      for (const i of fresh) {
        const row = $(`#view tr[data-id="${i.id}"]`);
        if (!row) continue;
        const status = intercomStatusCell(i);
        if (row.querySelector(".ic-status").innerHTML !== status) row.querySelector(".ic-status").innerHTML = status;
        const call = intercomCallCell(i);
        if (row.querySelector(".ic-call").innerHTML !== call) { row.querySelector(".ic-call").innerHTML = call; callChanged = true; }
      }
      if (callChanged) renderIntercomHistory(Number($("#ic-history").dataset.skip ?? 0));
    } catch { /* se reintenta en el próximo ciclo */ }
  }, 5000);
}

async function renderIntercomHistory(skip) {
  const box = $("#ic-history");
  if (!box) return;
  const take = 50;
  const params = new URLSearchParams({ skip, take });
  if ($("#ic-filter-intercom").value) params.set("intercomId", $("#ic-filter-intercom").value);
  if ($("#ic-filter-state").value) params.set("state", $("#ic-filter-state").value);
  let page;
  try { page = await Api.get(`/api/intercoms/calls?${params}`); }
  catch (err) { box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  box.dataset.skip = skip;
  if (page.total === 0) { box.innerHTML = `<div class="info-box">Sin llamadas registradas.</div>`; return; }
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Inicio</th><th>Frente</th><th>Resultado</th><th>Atendió</th><th>Duración</th><th>Puerta</th><th>Detalle</th></tr></thead>
      <tbody>
        ${page.items.map((c) => `
          <tr>
            <td>${formatDateTime(c.startedAt)}</td>
            <td>${esc(c.intercomName)}</td>
            <td>${INTERCOM_CALL_STATES[c.state] ?? esc(c.state)}</td>
            <td>${esc(c.answeredBy ?? "—")}</td>
            <td>${intercomDuration(c)}</td>
            <td>${c.doorOpened ? `<span class="tag on" title="Abrió ${esc(c.doorOpenedBy ?? "")}">Abierta</span> <span class="muted">${esc(c.doorOpenedBy ?? "")}</span>` : `<span class="muted">—</span>`}</td>
            <td class="muted">${esc([c.origin, c.endReason].filter(Boolean).join(" · ") || "—")}</td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    <div class="row-actions" style="justify-content:flex-end;margin-top:8px">
      <span class="muted">${skip + 1}–${Math.min(skip + take, page.total)} de ${page.total}</span>
      <button class="btn ghost" id="ic-prev" ${skip === 0 ? "disabled" : ""}>Anteriores</button>
      <button class="btn ghost" id="ic-next" ${skip + take >= page.total ? "disabled" : ""}>Siguientes</button>
    </div>`;
  $("#ic-prev").addEventListener("click", () => renderIntercomHistory(Math.max(0, skip - take)));
  $("#ic-next").addEventListener("click", () => renderIntercomHistory(skip + take));
}

async function intercomModal(intercom) {
  const isNew = !intercom;
  const [drivers, channels] = await Promise.all([getIntercomDrivers(), Api.get("/api/intercoms/channels").then((c) => c ?? [])]);
  const driver0 = drivers.find((d) => d.key === intercom?.driverKey) ?? drivers[0];
  const channelOptions = (selected) => `<option value="">— Sin video —</option>` + channels.map((c) =>
    `<option value="${c.id}" ${selected === c.id ? "selected" : ""}>${esc(c.name)} (${esc(c.host)})${c.enabled ? "" : " — deshabilitado"}</option>`).join("");
  openModal(`
    <h3>${isNew ? "Agregar frente de citofonía" : "Editar frente de citofonía"}</h3>
    <div id="ic-modal-error"></div>
    <form id="intercom-form">
      <div class="form-grid">
        <div class="field">
          <label>Nombre</label>
          <input id="ic-name" required maxlength="128" value="${esc(intercom?.name ?? "")}" placeholder="Portería principal, Acceso vehicular...">
        </div>
        <div class="field">
          <label>Grupo (opcional)</label>
          <input id="ic-group" maxlength="64" value="${esc(intercom?.groupName ?? "")}" placeholder="Portería, Torre A…">
        </div>
      </div>
      <div class="field">
        <label>Marca / protocolo</label>
        <select id="ic-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${driver0?.key === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección (IP o hostname)</label>
          <input id="ic-host" required value="${esc(intercom?.host ?? "")}" placeholder="192.168.1.61">
        </div>
        <div class="field">
          <label>Puerto SDK / Puerto HTTP</label>
          <div style="display:flex;gap:6px">
            <input id="ic-port" type="number" min="1" max="65535" required value="${intercom?.port ?? driver0?.defaultPort ?? 8000}" title="Puerto del SDK: llamadas y voz">
            <input id="ic-http-port" type="number" min="1" max="65535" required value="${intercom?.httpPort ?? driver0?.defaultHttpPort ?? 80}" title="Puerto HTTP (ISAPI): estado y apertura de puerta">
          </div>
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Usuario del frente</label>
          <input id="ic-username" required value="${esc(intercom?.username ?? "admin")}" placeholder="admin">
        </div>
        <div class="field">
          <label>Contraseña${isNew ? "" : " (vacío = no cambiar)"}</label>
          <input id="ic-password" type="password" autocomplete="new-password" ${isNew ? "required" : ""}>
        </div>
      </div>
      <div class="field">
        <label>Cámara del frente (video al sonar)</label>
        <select id="ic-channel">${channelOptions(intercom?.channelId ?? null)}</select>
        <div class="muted" style="font-size:11px;margin-top:3px">El video sale de un canal normal del VMS: agregue el frente también en <b>Fuentes de video</b> (como cámara Hikvision) y elíjalo aquí.</div>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="ic-callcenter" checked> Configurar el botón del frente para que llame a la central (necesario para recibir las llamadas)</label>
      <label class="checkbox-row"><input type="checkbox" id="ic-optimize" checked> Ajustar el video del frente para que la imagen aparezca al instante (un cuadro completo por segundo)</label>
      <label class="checkbox-row"><input type="checkbox" id="ic-enabled" ${intercom ? (intercom.enabled ? "checked" : "") : "checked"}> Activo (el servidor recibe sus llamadas y lo ofrece a los operadores)</label>
      <div id="ic-probe-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="ic-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="ic-probe">Probar conexión</button>
        <button class="btn" type="submit" id="ic-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);

  $("#ic-cancel").addEventListener("click", closeModal);
  $("#ic-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#ic-driver").value);
    if (dr && isNew) { $("#ic-port").value = dr.defaultPort; $("#ic-http-port").value = dr.defaultHttpPort; }
  });

  const readForm = () => ({
    name: $("#ic-name").value.trim() || "(sin nombre)",
    driverKey: $("#ic-driver").value,
    host: $("#ic-host").value.trim(),
    port: Number($("#ic-port").value),
    httpPort: Number($("#ic-http-port").value),
    username: $("#ic-username").value.trim(),
    password: $("#ic-password").value || null,
    enabled: $("#ic-enabled").checked,
    groupName: $("#ic-group").value.trim() || null,
    channelId: $("#ic-channel").value ? Number($("#ic-channel").value) : null,
    configureCallCenter: $("#ic-callcenter").checked,
    optimizeVideo: $("#ic-optimize").checked,
  });

  $("#ic-probe").addEventListener("click", async () => {
    const errorBox = $("#ic-modal-error");
    const resultBox = $("#ic-probe-result");
    errorBox.innerHTML = "";
    resultBox.innerHTML = `<div class="info-box">Conectando con el frente…</div>`;
    const probeButton = $("#ic-probe");
    probeButton.disabled = true;
    try {
      const query = !isNew ? `?intercomId=${intercom.id}` : "";
      const r = await Api.post(`/api/intercoms/probe${query}`, readForm());
      if (!r.success) {
        resultBox.innerHTML = `<div class="error-box">${esc(r.error)}</div>`;
        return;
      }
      if (r.suggestedChannelId && !$("#ic-channel").value) $("#ic-channel").value = String(r.suggestedChannelId);
      resultBox.innerHTML = `
        <div class="probe-box">
          <div class="probe-title">✔ Conexión validada</div>
          <div class="probe-grid">
            <span>Modelo</span><b>${esc(r.model ?? "—")}</b>
            <span>Nombre en el equipo</span><b>${esc(r.deviceName ?? "—")}</b>
            <span>N° de serie</span><b>${esc(r.serialNumber ?? "—")}</b>
            <span>Firmware</span><b>${esc(r.firmwareVersion ?? "—")}</b>
            <span>Puertas</span><b>${r.doorCount}</b>
            <span>Voz</span><b>${esc({ ulaw: "G.711 µ-law", alaw: "G.711 A-law" }[r.audioCodec] ?? "no soportada")}</b>
            <span>Botón → central</span><b>${r.callCenterEnabled ? "sí" : "no (se configurará al guardar)"}</b>
            <span>Cuadro completo</span><b>${r.keyFrameSeconds == null ? "—" : `cada ${r.keyFrameSeconds} s`}${r.keyFrameSeconds > 2 ? ` <span class="tag off" title="Hasta recibir un cuadro completo no se ve imagen: la llamada tardaría eso en mostrar el video">lento</span> (se ajustará a 1 s al guardar)` : ""}</b>
            <span>Cámara</span><b>${r.suggestedChannelId ? "encontrada en Fuentes de video (seleccionada)" : "no registrada como fuente de video"}</b>
          </div>
        </div>`;
    } catch (err) {
      resultBox.innerHTML = "";
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      probeButton.disabled = false;
    }
  });

  $("#intercom-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#ic-modal-error");
    errorBox.innerHTML = "";
    const saveButton = $("#ic-save");
    saveButton.disabled = true;
    saveButton.textContent = "Validando…";
    try {
      const body = readForm();
      const saved = isNew ? await Api.post("/api/intercoms", body) : await Api.put(`/api/intercoms/${intercom.id}`, body);
      closeModal();
      toast(isNew ? "Frente agregado y validado." : "Frente actualizado.");
      if (body.configureCallCenter && !saved.callCenterEnabled)
        toast("No se pudo configurar el botón del frente para llamar a la central; revise la bitácora.", true);
      renderIntercoms();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}
