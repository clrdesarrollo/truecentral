// CLR TrueCentral VMS — panel: paneles de alarma (centrales de intrusión).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

let alarmDriversCache = null;
// Dirección que se muestra de un panel: su IP, o su ID ISUP si es el panel el que
// se conecta al servidor (entonces el sistema no conoce ni usa su IP).
function alarmAddress(p) {
  if (p.isupDeviceId) return isupAddressHtml(p.isupDeviceId);
  return `${p.useHttps ? "https://" : ""}${esc(p.host)}:${p.port}`;
}

function alarmAddressHint(p) {
  return p.isupDeviceId
    ? `El panel se conecta al servidor por ISUP con el ID "${p.isupDeviceId}".`
    : `Dirección del panel: ${p.useHttps ? "https://" : ""}${p.host}:${p.port}`;
}

async function getAlarmDrivers() {
  alarmDriversCache ??= await Api.get("/api/alarms/drivers");
  return alarmDriversCache;
}

let alarmsTimer = null;

const ARM_LABELS = {
  Disarmed: ["Desarmada", "off"],
  Away: ["Armada total", "on"],
  Stay: ["Armada parcial", "on"],
  Vacation: ["Armada vacaciones", "on"],
  Arming: ["Armando…", "on"],
  Unknown: ["—", "operator"],
};

const ZONE_LABELS = {
  Normal: ["Normal", "on"],
  Triggered: ["Activada", "off"],
  Fault: ["Falla", "off"],
  Offline: ["Sin comunicación", "off"],
  NotConfigured: ["Sin área", "operator"],
  Unknown: ["—", "operator"],
};

function alarmStatusTag(panel) {
  switch (panel.status) {
    case "Online": return `<span class="tag on">En línea</span>`;
    case "Offline": return `<span class="tag off">Sin conexión</span>`;
    case "AuthFailed": return `<span class="tag off">Credenciales</span>`;
    default: return `<span class="tag operator">—</span>`;
  }
}

function armSummary(panel) {
  if (!panel.areas.length) return `<span class="muted">Sin áreas</span>`;
  const armed = panel.areas.filter((a) => a.armState !== "Disarmed" && a.armState !== "Unknown").length;
  const alarm = panel.inAlarm ? ` <span class="tag off">¡ALARMA!</span>` : "";
  if (armed === 0) return `<span class="tag off">Desarmado</span>${alarm}`;
  if (armed === panel.areas.length) return `<span class="tag on">Armado</span>${alarm}`;
  return `<span class="tag admin">Parcial (${armed}/${panel.areas.length})</span>${alarm}`;
}

// Último estado leído de cada panel de la lista: los botones de cada fila lo buscan aquí al hacer clic (el refresco lo actualiza).
let alarmPanelsById = new Map();
let alarmsRefreshing = false;

/**
 * Celdas de datos de la fila de un panel (todas menos la de acciones). El refresco las compara con las que están dibujadas y
 * reemplaza solo las que cambiaron: así la lista se actualiza sin parpadear ni cerrar el estado que el usuario tiene abierto.
 */
function alarmRowCells(p) {
  return [
    `<td>${esc(p.name)}${p.disabledByLicense ? licenseWaitTag(p) : p.enabled ? "" : ` <span class="tag operator" title="Monitoreo desactivado">pausado</span>`}${locationLineHtml(p.location)}</td>`,
    `<td class="muted" title="${esc(alarmAddressHint(p))}">${alarmAddress(p)}</td>`,
    `<td>${esc(p.model ?? "—")}</td>`,
    `<td class="muted">${esc(p.serialNumber ?? "—")}</td>`,
    `<td class="muted">${esc(p.firmwareVersion ?? "—")}</td>`,
    `<td>${p.areas.length}</td>`,
    `<td>${p.zones.length}</td>`,
    `<td>${armSummary(p)}</td>`,
    `<td>${alarmStatusTag(p)}${p.lastError ? `<div class="muted" style="font-size:11px;max-width:220px" title="${esc(p.lastError)}">${esc(p.lastError)}</div>` : ""}</td>`,
    `<td>${p.live ? `<span class="dot ok" title="Canal de eventos abierto"></span>` : `<span class="dot bad" title="Canal de eventos cerrado"></span>`}</td>`,
  ];
}

/** El botón Estado abre y cierra el detalle: mientras está abierto se ve presionado y dice «Ocultar». */
function setAlarmStateButton(button, open) {
  button.classList.toggle("ghost", !open);
  button.setAttribute("aria-pressed", String(open));
  button.textContent = open ? "Ocultar" : "Estado";
  button.title = open ? "Ocultar áreas y zonas" : "Ver áreas y zonas, armar y desarmar";
}

function toggleAlarmDetail(id) {
  const detail = $(`#view tr[data-detail="${id}"]`);
  const button = $(`#view tr[data-id="${id}"] .btn-state`);
  if (!detail || !button) return;
  const open = detail.classList.contains("hidden");
  detail.classList.toggle("hidden", !open);
  setAlarmStateButton(button, open);
  if (open && alarmPanelsById.has(id)) renderAlarmDetail(alarmPanelsById.get(id));
}

/** Actualiza en su lugar la fila de un panel (solo las celdas que cambiaron) y su detalle si está abierto. */
function updateAlarmPanel(p) {
  alarmPanelsById.set(p.id, p);
  const row = $(`#view tr[data-id="${p.id}"]`);
  if (!row) return;
  const cells = alarmRowCells(p);
  cells.forEach((html, i) => { if (row._cells?.[i] !== html) row.children[i].outerHTML = html; });
  row._cells = cells;
  const detail = $(`#view tr[data-detail="${p.id}"]`);
  if (detail && !detail.classList.contains("hidden")) renderAlarmDetail(p);
}

/**
 * Refresco periódico por AJAX: lee la lista y cambia solo lo que cambió. Si se agregó, eliminó o reordenó un panel, redibuja la
 * lista (conservando los detalles abiertos).
 */
async function refreshAlarmPanels() {
  if (alarmsRefreshing || document.hidden) return;
  alarmsRefreshing = true;
  try {
    let panels;
    try { panels = await Api.get("/api/alarms/panels"); } catch { return; }   // se reintenta en el próximo ciclo
    if (location.hash !== "#/alarm-panels") return;
    const ids = $$("#view tr[data-id]").map((r) => Number(r.dataset.id));
    if (ids.length !== panels.length || panels.some((p, i) => p.id !== ids[i])) { await renderAlarmPanels(); return; }
    panels.forEach(updateAlarmPanel);
  } finally { alarmsRefreshing = false; }
}

async function renderAlarmPanels() {
  $("#page-title").textContent = "Paneles de alarma";
  // Al redibujar (panel agregado o eliminado) quedan abiertos los detalles que el usuario tenía abiertos.
  const open = new Set($$("#view tr[data-detail]").filter((r) => !r.classList.contains("hidden")).map((r) => Number(r.dataset.detail)));
  let panels;
  try { panels = await Api.get("/api/alarms/panels"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  alarmPanelsById = new Map(panels.map((p) => [p.id, p]));

  const isAdmin = Perms.can("alarms.configure");
  $("#view").innerHTML = `
    ${isAdmin ? `<div class="toolbar page-toolbar"><button class="btn" id="btn-alarm-new">Agregar panel</button></div>` : ""}
    ${panels.length === 0 ? `
      <div class="info-box">
        Aún no hay paneles de alarma. ${isAdmin
          ? "Use <b>Agregar panel</b>: por IP, al guardar se validan las credenciales contra la central y se leen sus áreas y zonas; por ISUP, el panel se conecta solo al servidor con su ID y su clave. Desde ese momento el servidor recibe sus eventos y el cliente de escritorio permite armar y desarmar."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Dirección</th><th>Modelo</th><th>N° serie</th><th>Firmware</th>
          <th>Áreas</th><th>Zonas</th><th>Armado</th><th>Conexión</th><th>Eventos</th><th></th>
        </tr></thead>
        <tbody>
          ${panels.map((p) => `
            <tr data-id="${p.id}">
              ${alarmRowCells(p).join("")}
              <td class="row-actions">
                <button class="btn ghost btn-state" aria-pressed="false" title="Ver áreas y zonas, armar y desarmar">Estado</button>
                ${isAdmin ? `<button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </td>
            </tr>
            <tr class="hidden" data-detail="${p.id}"><td colspan="11"></td></tr>`).join("")}
        </tbody>
      </table></div>`}`;
  for (const p of panels) {
    $(`#view tr[data-id="${p.id}"]`)._cells = alarmRowCells(p);
    if (open.has(p.id)) toggleAlarmDetail(p.id);
  }

  $("#btn-alarm-new")?.addEventListener("click", () => alarmPanelModal(null));
  // Un solo manejador para los botones de las filas: sigue sirviendo aunque el refresco reemplace celdas.
  $("#view tbody")?.addEventListener("click", async (e) => {
    const button = e.target.closest("button");
    const row = button?.closest("tr[data-id]");
    if (!row) return;
    const id = Number(row.dataset.id);
    const panel = alarmPanelsById.get(id);
    if (button.classList.contains("btn-state")) toggleAlarmDetail(id);
    else if (button.classList.contains("btn-edit")) alarmPanelModal(panel);
    else if (button.classList.contains("btn-delete")) {
      if (!confirm(`¿Eliminar el panel "${panel.name}"? El historial de eventos se conserva.`)) return;
      try {
        await Api.delete(`/api/alarms/panels/${id}`);
        toast("Panel eliminado.");
        renderAlarmPanels();
      } catch (err) { toast(err.error, true); }
    }
  });

  // El estado cambia solo (eventos del panel): cada 10 s se lee la lista por AJAX y se cambia solo lo que cambió.
  clearInterval(alarmsTimer);
  alarmsTimer = setInterval(() => {
    if (location.hash !== "#/alarm-panels" || $("#app-shell").classList.contains("hidden")) {
      clearInterval(alarmsTimer);
      return;
    }
    refreshAlarmPanels();
  }, 10000);
}

/**
 * Detalle desplegable: áreas con sus botones y zonas con su estado. Si nada cambió desde la última vez no se toca (el botón bajo el
 * cursor sigue ahí y no parpadea); la hora de la última lectura se actualiza aparte.
 */
function renderAlarmDetail(panel) {
  const cell = $(`#view tr[data-detail="${panel.id}"] td`);
  if (!cell) return;
  const zoneRows = (areaNumber) => panel.zones.filter((z) => z.areaNumber === areaNumber);
  const orphanZones = panel.zones.filter((z) => !panel.areas.some((a) => a.number === z.areaNumber));

  const zoneChip = (z) => {
    const [label, cls] = ZONE_LABELS[z.status] ?? ZONE_LABELS.Unknown;
    const flags = [
      z.inAlarm ? "EN ALARMA" : "",
      z.bypassed ? "anulada" : "",
      z.tamper ? "tamper" : "",
      z.lowBattery ? "batería baja" : "",
    ].filter(Boolean).join(" · ");
    return `<span class="chip" title="${esc([z.detectorType, z.zoneType, z.model].filter(Boolean).join(" · "))}">
      ${esc(z.name)} <span class="tag ${z.inAlarm ? "off" : cls}" style="margin-left:4px">${z.inAlarm ? "¡Alarma!" : label}</span>
      ${flags ? `<span class="muted" style="margin-left:4px">${esc(flags)}</span>` : ""}
      <button class="btn ghost btn-bypass" data-op="zone:${panel.id}/${z.number}" data-zone="${z.number}" data-on="${z.bypassed ? 1 : 0}"
              style="padding:1px 7px;font-size:11px;margin-left:6px" title="${z.bypassed ? "Restituir la zona" : "Anular (bypass) la zona"}">${z.bypassed ? "Restituir" : "Anular"}</button>
    </span>`;
  };

  const areaBlock = (a) => {
    const [label, cls] = ARM_LABELS[a.armState] ?? ARM_LABELS.Unknown;
    const op = `data-op="area:${panel.id}/${a.number}"`;
    return `
      <div class="probe-box" style="margin-bottom:10px">
        <div class="toolbar" style="margin-bottom:8px">
          <div><b>${esc(a.name)}</b> <span class="muted">(área ${a.number})</span>
            <span class="tag ${cls}" style="margin-left:8px">${label}</span>
            ${a.inAlarm ? `<span class="tag off" style="margin-left:6px">¡ALARMA!</span>` : ""}
            ${a.enabled ? "" : `<span class="tag operator" style="margin-left:6px">deshabilitada</span>`}
          </div>
          <div class="row-actions">
            <button class="btn btn-arm" ${op} data-area="${a.number}" data-mode="Away">Armar total</button>
            <button class="btn ghost btn-arm" ${op} data-area="${a.number}" data-mode="Stay">Armar parcial</button>
            <button class="btn ghost btn-disarm" ${op} data-area="${a.number}">Desarmar</button>
            ${a.inAlarm ? `<button class="btn danger btn-clear" ${op} data-area="${a.number}">Silenciar alarma</button>` : ""}
          </div>
        </div>
        <div class="chip-row">${zoneRows(a.number).map(zoneChip).join("") || `<span class="muted">Sin zonas asociadas</span>`}</div>
      </div>`;
  };

  const html = `
    <div style="padding:8px 4px">
      ${(() => { const w = [panel.panelTamper ? "Tapa del panel abierta (sabotaje): no se podrá armar hasta cerrarla." : null, panel.acLoss ? "Falla de corriente de red: el panel está en batería." : null].filter(Boolean); return w.length ? `<div class="error-box" style="margin-bottom:8px">⚠ ${w.map(esc).join("<br>")}</div>` : ""; })()}
      ${panel.areas.length === 0 ? `<div class="info-box">El panel no reportó áreas todavía.</div>` : ""}
      ${panel.areas.length > 1 ? `
        <div class="row-actions" style="margin-bottom:10px">
          <button class="btn btn-arm" data-op="panel:${panel.id}" data-area="0" data-mode="Away">Armar todo</button>
          <button class="btn ghost btn-disarm" data-op="panel:${panel.id}" data-area="0">Desarmar todo</button>
        </div>` : ""}
      ${panel.areas.map(areaBlock).join("")}
      ${orphanZones.length ? `<div class="probe-box"><div class="muted" style="margin-bottom:6px">Zonas sin área</div>
        <div class="chip-row">${orphanZones.map(zoneChip).join("")}</div></div>` : ""}
      <div class="muted" style="font-size:11px;margin-top:6px">
        Última lectura: <span class="alarm-read-at"></span>
        · <a href="#" class="btn-refresh">actualizar ahora</a>
      </div>
    </div>`;
  const readAt = panel.lastStateAt ? formatDate(panel.lastStateAt) : "—";
  if (cell._html === html) { $(".alarm-read-at", cell).textContent = readAt; return; }
  cell._html = html;
  cell.innerHTML = html;
  $(".alarm-read-at", cell).textContent = readAt;

  // Tras una orden se actualizan en el acto el detalle y la fila (columna Armado), sin esperar al refresco.
  const run = async (path, body, okMessage) => {
    try {
      const updated = await Api.post(path, body);
      toast(okMessage);
      updateAlarmPanel(updated);
    } catch (err) { toast(err.error, true); }
  };
  $$(".btn-arm", cell).forEach((b) => b.addEventListener("click", () =>
    run(`/api/alarms/panels/${panel.id}/areas/${b.dataset.area}/arm`, { mode: b.dataset.mode },
      b.dataset.mode === "Stay" ? "Orden de armado parcial enviada." : "Orden de armado enviada.")));
  $$(".btn-disarm", cell).forEach((b) => b.addEventListener("click", () =>
    run(`/api/alarms/panels/${panel.id}/areas/${b.dataset.area}/disarm`, undefined, "Orden de desarmado enviada.")));
  $$(".btn-clear", cell).forEach((b) => b.addEventListener("click", () =>
    run(`/api/alarms/panels/${panel.id}/areas/${b.dataset.area}/clear-alarm`, undefined, "Alarma silenciada.")));
  $$(".btn-bypass", cell).forEach((b) => b.addEventListener("click", () => {
    const bypassed = b.dataset.on !== "1";
    run(`/api/alarms/panels/${panel.id}/zones/${b.dataset.zone}/bypass`, { bypassed },
      bypassed ? "Zona anulada." : "Zona restituida.");
  }));
  $(".btn-refresh", cell)?.addEventListener("click", (e) => {
    e.preventDefault();
    run(`/api/alarms/panels/${panel.id}/refresh`, undefined, "Estado actualizado.");
  });
}

async function alarmPanelModal(panel) {
  const isNew = !panel;
  const [drivers, places] = await Promise.all([getAlarmDrivers(), loadLocationChoices()]);
  const driver0 = drivers.find((d) => d.key === panel?.driverKey) ?? drivers[0];
  openModal(`
    <h3>${isNew ? "Agregar panel de alarma" : "Editar panel de alarma"}</h3>
    <div id="al-modal-error"></div>
    <form id="alarm-form">
      <div class="field">
        <label>Nombre</label>
        <input id="al-name" required maxlength="128" value="${esc(panel?.name ?? "")}" placeholder="Panel bodega, Central oficina...">
      </div>
      ${locationFieldHtml("al-location", places, panel?.locationId ?? null,
        "Sus áreas y zonas la heredan; las que ubique aparte en Recursos se quedan donde están.", { equipment: true, isNew })}
      <div class="field">
        <label>Marca / conexión</label>
        <select id="al-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${driver0?.key === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <!-- Por IP: el servidor llama al panel. -->
      <div id="al-ip-box">
        <div class="form-grid">
          <div class="field">
            <label>Dirección del panel (IP o hostname)</label>
            <input id="al-host" value="${esc(panel?.isupDeviceId ? "" : panel?.host ?? "")}" placeholder="192.168.1.50">
          </div>
          <div class="field">
            <label>Puerto HTTP</label>
            <input id="al-port" type="number" min="1" max="65535" value="${panel?.isupDeviceId ? 80 : panel?.port ?? driver0?.defaultPort ?? 80}">
          </div>
        </div>
        <div class="form-grid">
          <div class="field">
            <label>Usuario del panel</label>
            <input id="al-username" value="${esc(panel?.isupDeviceId ? "admin" : panel?.username ?? "admin")}" placeholder="admin">
          </div>
          <div class="field">
            <label>Contraseña del panel${isNew ? "" : " (vacío = no cambiar)"}</label>
            <input id="al-password" type="password" autocomplete="new-password">
          </div>
        </div>
        <label class="checkbox-row"><input type="checkbox" id="al-https" ${(panel ? panel.useHttps : driver0?.defaultHttps) ? "checked" : ""}> Usar HTTPS (certificado autofirmado aceptado)</label>
        <div class="info-box" style="margin-top:10px">Use un usuario <b>local</b> del panel (el creado al activarlo, normalmente
          <b>admin</b>), no la cuenta de la nube Hik-Connect. Tras varios intentos fallidos el panel bloquea el acceso por 30 minutos.</div>
      </div>
      <!-- Por ISUP: el panel llama al servidor con su ID y su clave. -->
      <div id="al-isup-wrap">${isupFieldsHtml("al", panel, isNew, "panel")}</div>
      <label class="checkbox-row"><input type="checkbox" id="al-enabled" ${panel ? (panel.enabled ? "checked" : "") : "checked"}> Monitoreo activo (sondeo de estado y recepción de eventos)</label>
      <div id="al-probe-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="al-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="al-probe">Probar conexión</button>
        <button class="btn" type="submit" id="al-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`, true);

  $("#al-cancel").addEventListener("click", closeModal);
  wireIsupFields("al", "panel");

  const usesIsup = () => !!drivers.find((x) => x.key === $("#al-driver").value)?.usesIsup;
  const applyDriver = () => {
    const isup = usesIsup();
    $("#al-ip-box").hidden = isup;
    $("#al-isup-wrap").hidden = !isup;
  };
  $("#al-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#al-driver").value);
    if (dr && isNew && !dr.usesIsup) { $("#al-port").value = dr.defaultPort; $("#al-https").checked = dr.defaultHttps; }
    applyDriver();
  });
  applyDriver();

  const readForm = () => {
    const isup = usesIsup();
    return {
      name: $("#al-name").value.trim() || "(sin nombre)",
      driverKey: $("#al-driver").value,
      host: isup ? "" : $("#al-host").value.trim(),
      port: isup ? 0 : Number($("#al-port").value),
      useHttps: !isup && $("#al-https").checked,
      username: isup ? "" : $("#al-username").value.trim(),
      password: isup ? null : ($("#al-password").value || null),
      enabled: $("#al-enabled").checked,
      ...(isup ? readIsupFields("al") : { isupDeviceId: null, isupKey: null }),
      locationId: locationFieldValue("al-location"),
    };
  };

  $("#al-probe").addEventListener("click", async () => {
    const errorBox = $("#al-modal-error");
    const resultBox = $("#al-probe-result");
    errorBox.innerHTML = "";
    resultBox.innerHTML = `<div class="info-box">Conectando con el panel…</div>`;
    const probeButton = $("#al-probe");
    probeButton.disabled = true;
    try {
      const query = !isNew ? `?panelId=${panel.id}` : "";
      const r = await Api.post(`/api/alarms/panels/probe${query}`, readForm());
      if (!r.success) {
        resultBox.innerHTML = `<div class="error-box">${esc(r.error)}</div>`;
        return;
      }
      resultBox.innerHTML = `
        <div class="probe-box">
          <div class="probe-title">✔ Conexión validada</div>
          <div class="probe-grid">
            <span>Modelo</span><b>${esc(r.model ?? "—")}</b>
            <span>N° de serie</span><b>${esc(r.serialNumber ?? "—")}</b>
            <span>Firmware</span><b>${esc(r.firmwareVersion ?? "—")}</b>
            <span>Áreas</span><b>${r.areas.length}</b>
            <span>Zonas</span><b>${r.zones.length}</b>
          </div>
          ${r.areas.length ? `<div class="probe-channels">${r.areas.map((a) =>
            `<span class="tag ${a.armState === "Disarmed" ? "off" : "on"}" title="Área ${a.number}">${esc(a.name)} · ${(ARM_LABELS[a.armState] ?? ARM_LABELS.Unknown)[0]}</span>`).join(" ")}</div>` : ""}
          ${r.zones.length ? `<div class="probe-channels">${r.zones.map((z) =>
            `<span class="tag ${z.status === "Normal" ? "on" : "operator"}" title="Zona ${z.number}">${esc(z.name)}</span>`).join(" ")}</div>` : ""}
        </div>`;
    } catch (err) {
      resultBox.innerHTML = "";
      $("#al-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      probeButton.disabled = false;
    }
  });

  $("#alarm-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#al-modal-error");
    errorBox.innerHTML = "";
    const saveButton = $("#al-save");
    saveButton.disabled = true;
    saveButton.textContent = "Validando…";
    try {
      const body = readForm();
      await locationFieldCommit("al-location", body);
      const saved = isNew ? await Api.post("/api/alarms/panels", body) : await Api.put(`/api/alarms/panels/${panel.id}`, body);
      closeModal();
      // Por ISUP el panel puede no haberse conectado todavía: se guarda igual y se avisa qué falta.
      toast(saved?.isupDeviceId && saved.status !== "Online"
        ? "Panel guardado: queda esperando que se conecte por ISUP."
        : (isNew ? "Panel agregado y validado." : "Panel actualizado."));
      renderAlarmPanels();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}
