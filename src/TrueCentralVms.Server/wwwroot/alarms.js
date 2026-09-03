// CLR TrueCentral VMS — panel: paneles de alarma (centrales de intrusión).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

let alarmDriversCache = null;
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

async function renderAlarmPanels() {
  $("#page-title").textContent = "Paneles de alarma";
  let panels;
  try { panels = await Api.get("/api/alarms/panels"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Paneles de alarma <span class="muted" style="font-weight:normal;font-size:12px">(el estado se actualiza solo)</span></h3>
      ${isAdmin ? `<button class="btn" id="btn-alarm-new">Agregar panel</button>` : ""}
    </div>
    ${panels.length === 0 ? `
      <div class="info-box">
        Aún no hay paneles de alarma. ${isAdmin
          ? "Use <b>Agregar panel</b>: al guardar se validan las credenciales contra la central y se leen sus áreas y zonas. Desde ese momento el servidor recibe sus eventos y el cliente de escritorio permite armar y desarmar."
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
              <td>${esc(p.name)}${p.enabled ? "" : ` <span class="tag operator" title="Monitoreo desactivado">pausado</span>`}</td>
              <td class="muted">${p.useHttps ? "https://" : ""}${esc(p.host)}:${p.port}${p.deviceId ? ` · ${esc(p.deviceId)}` : ""}</td>
              <td>${esc(p.model ?? "—")}</td>
              <td class="muted">${esc(p.serialNumber ?? "—")}</td>
              <td class="muted">${esc(p.firmwareVersion ?? "—")}</td>
              <td>${p.areas.length}</td>
              <td>${p.zones.length}</td>
              <td>${armSummary(p)}</td>
              <td>${alarmStatusTag(p)}${p.lastError ? `<div class="muted" style="font-size:11px;max-width:220px" title="${esc(p.lastError)}">${esc(p.lastError)}</div>` : ""}</td>
              <td>${p.live ? `<span class="dot ok" title="Canal de eventos abierto"></span>` : `<span class="dot bad" title="Canal de eventos cerrado"></span>`}</td>
              <td class="row-actions">
                <button class="btn ghost btn-state" title="Ver áreas y zonas, armar y desarmar">Estado</button>
                ${isAdmin ? `<button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </td>
            </tr>
            <tr class="hidden" data-detail="${p.id}"><td colspan="11"></td></tr>`).join("")}
        </tbody>
      </table></div>`}`;

  $("#btn-alarm-new")?.addEventListener("click", () => alarmPanelModal(null));
  $$("#view .btn-edit").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    alarmPanelModal(panels.find((p) => p.id === id));
  }));
  $$("#view .btn-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const panel = panels.find((p) => p.id === id);
    if (!confirm(`¿Eliminar el panel "${panel.name}"? El historial de eventos se conserva.`)) return;
    try {
      await Api.delete(`/api/alarms/panels/${id}`);
      toast("Panel eliminado.");
      renderAlarmPanels();
    } catch (err) { toast(err.error, true); }
  }));
  $$("#view .btn-state").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const row = $(`#view tr[data-detail="${id}"]`);
    if (!row.classList.contains("hidden")) { row.classList.add("hidden"); return; }
    row.classList.remove("hidden");
    renderAlarmDetail(panels.find((p) => p.id === id));
  }));

  // El estado cambia solo (eventos del panel): refrescar la tabla cada 10 s
  // conservando los detalles abiertos.
  clearInterval(alarmsTimer);
  alarmsTimer = setInterval(async () => {
    if (location.hash !== "#/alarm-panels" || $("#app-shell").classList.contains("hidden")) {
      clearInterval(alarmsTimer);
      return;
    }
    if ($("#modal-backdrop") && !$("#modal-backdrop").classList.contains("hidden")) return;
    const open = $$("#view tr[data-detail]").filter((r) => !r.classList.contains("hidden")).map((r) => Number(r.dataset.detail));
    await renderAlarmPanels();
    for (const id of open) {
      const row = $(`#view tr[data-detail="${id}"]`);
      if (!row) continue;
      row.classList.remove("hidden");
      try { renderAlarmDetail(await Api.get(`/api/alarms/panels/${id}`)); } catch { /* se reintenta en el próximo ciclo */ }
    }
  }, 10000);
}

/** Detalle desplegable: áreas con sus botones y zonas con su estado. */
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
      <button class="btn ghost btn-bypass" data-zone="${z.number}" data-on="${z.bypassed ? 1 : 0}"
              style="padding:1px 7px;font-size:11px;margin-left:6px" title="${z.bypassed ? "Restituir la zona" : "Anular (bypass) la zona"}">${z.bypassed ? "Restituir" : "Anular"}</button>
    </span>`;
  };

  const areaBlock = (a) => {
    const [label, cls] = ARM_LABELS[a.armState] ?? ARM_LABELS.Unknown;
    return `
      <div class="probe-box" style="margin-bottom:10px">
        <div class="toolbar" style="margin-bottom:8px">
          <div><b>${esc(a.name)}</b> <span class="muted">(área ${a.number})</span>
            <span class="tag ${cls}" style="margin-left:8px">${label}</span>
            ${a.inAlarm ? `<span class="tag off" style="margin-left:6px">¡ALARMA!</span>` : ""}
            ${a.enabled ? "" : `<span class="tag operator" style="margin-left:6px">deshabilitada</span>`}
          </div>
          <div class="row-actions">
            <button class="btn btn-arm" data-area="${a.number}" data-mode="Away">Armar total</button>
            <button class="btn ghost btn-arm" data-area="${a.number}" data-mode="Stay">Armar parcial</button>
            <button class="btn ghost btn-disarm" data-area="${a.number}">Desarmar</button>
            ${a.inAlarm ? `<button class="btn danger btn-clear" data-area="${a.number}">Silenciar alarma</button>` : ""}
          </div>
        </div>
        <div class="chip-row">${zoneRows(a.number).map(zoneChip).join("") || `<span class="muted">Sin zonas asociadas</span>`}</div>
      </div>`;
  };

  cell.innerHTML = `
    <div style="padding:8px 4px">
      ${(() => { const w = [panel.panelTamper ? "Tapa del panel abierta (sabotaje): no se podrá armar hasta cerrarla." : null, panel.acLoss ? "Falla de corriente de red: el panel está en batería." : null].filter(Boolean); return w.length ? `<div class="error-box" style="margin-bottom:8px">⚠ ${w.map(esc).join("<br>")}</div>` : ""; })()}
      ${panel.areas.length === 0 ? `<div class="info-box">El panel no reportó áreas todavía.</div>` : ""}
      ${panel.areas.length > 1 ? `
        <div class="row-actions" style="margin-bottom:10px">
          <button class="btn btn-arm" data-area="0" data-mode="Away">Armar todo</button>
          <button class="btn ghost btn-disarm" data-area="0">Desarmar todo</button>
        </div>` : ""}
      ${panel.areas.map(areaBlock).join("")}
      ${orphanZones.length ? `<div class="probe-box"><div class="muted" style="margin-bottom:6px">Zonas sin área</div>
        <div class="chip-row">${orphanZones.map(zoneChip).join("")}</div></div>` : ""}
      <div class="muted" style="font-size:11px;margin-top:6px">
        Última lectura: ${panel.lastStateAt ? formatDate(panel.lastStateAt) : "—"}
        · <a href="#" class="btn-refresh">actualizar ahora</a>
      </div>
    </div>`;

  const run = async (path, body, okMessage) => {
    try {
      const updated = await Api.post(path, body);
      toast(okMessage);
      renderAlarmDetail(updated);
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
  const drivers = await getAlarmDrivers();
  const driver0 = drivers.find((d) => d.key === panel?.driverKey) ?? drivers[0];
  openModal(`
    <h3>${isNew ? "Agregar panel de alarma" : "Editar panel de alarma"}</h3>
    <div id="al-modal-error"></div>
    <form id="alarm-form">
      <div class="field">
        <label>Nombre</label>
        <input id="al-name" required maxlength="128" value="${esc(panel?.name ?? "")}" placeholder="Panel bodega, Central oficina...">
      </div>
      <div class="field">
        <label>Marca / protocolo</label>
        <select id="al-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${driver0?.key === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección (IP o hostname)</label>
          <input id="al-host" required value="${esc(panel?.host ?? "")}" placeholder="192.168.1.50">
        </div>
        <div class="field">
          <label>Puerto HTTP</label>
          <input id="al-port" type="number" min="1" max="65535" required value="${panel?.port ?? driver0?.defaultPort ?? 80}">
        </div>
      </div>
      <div class="field" id="al-device-field" ${driver0?.needsDeviceId ? "" : "hidden"}>
        <label>Equipo dentro de la pasarela (uuid, serie, cuenta o ID ISUP)</label>
        <input id="al-device" value="${esc(panel?.deviceId ?? "")}" placeholder="PA02, DS-PHA64-W4M2022…, o el uuid de la pasarela">
      </div>
      <div class="form-grid">
        <div class="field">
          <label id="al-username-label">${driver0?.needsDeviceId ? "Usuario del IP Receiver Pro" : "Usuario del panel"}</label>
          <input id="al-username" required value="${esc(panel?.username ?? "admin")}" placeholder="admin">
        </div>
        <div class="field">
          <label id="al-password-label">${(driver0?.needsDeviceId ? "Contraseña del IP Receiver Pro" : "Contraseña del panel") + (isNew ? "" : " (vacío = no cambiar)")}</label>
          <input id="al-password" type="password" autocomplete="new-password" ${isNew ? "required" : ""}>
        </div>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="al-https" ${(panel ? panel.useHttps : driver0?.defaultHttps) ? "checked" : ""}> Usar HTTPS (certificado autofirmado aceptado)</label>
      <label class="checkbox-row"><input type="checkbox" id="al-enabled" ${panel ? (panel.enabled ? "checked" : "") : "checked"}> Monitoreo activo (sondeo de estado y recepción de eventos)</label>
      <div class="info-box" style="margin-top:10px" id="al-cred-help">${driver0?.needsDeviceId
        ? "Estas credenciales son las del <b>IP Receiver Pro</b> (la pasarela), no las del panel. El panel se identifica por su equipo dentro de la pasarela (arriba) y se comunica con ella por su clave EHome/ISUP, configurada en el propio panel. Requiere que en la pasarela esté habilitado <b>Automation Output → Protocol → Private</b>."
        : "Use un usuario <b>local</b> del panel (el creado al activarlo, normalmente <b>admin</b>), no la cuenta de la nube Hik-Connect. Tras varios intentos fallidos el panel bloquea el acceso por 30 minutos."}</div>
      <div id="al-probe-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="al-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="al-probe">Probar conexión</button>
        <button class="btn" type="submit" id="al-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);

  $("#al-cancel").addEventListener("click", closeModal);
  $("#al-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#al-driver").value);
    if (dr && isNew) { $("#al-port").value = dr.defaultPort; $("#al-https").checked = dr.defaultHttps; }
    const gw = !!dr?.needsDeviceId;
    $("#al-device-field").hidden = !gw;
    $("#al-username-label").textContent = gw ? "Usuario del IP Receiver Pro" : "Usuario del panel";
    $("#al-password-label").textContent = (gw ? "Contraseña del IP Receiver Pro" : "Contraseña del panel") + (isNew ? "" : " (vacío = no cambiar)");
    $("#al-cred-help").innerHTML = gw
      ? "Estas credenciales son las del <b>IP Receiver Pro</b> (la pasarela), no las del panel. El panel se identifica por su equipo dentro de la pasarela (arriba) y se comunica con ella por su clave EHome/ISUP, configurada en el propio panel. Requiere que en la pasarela esté habilitado <b>Automation Output → Protocol → Private</b>."
      : "Use un usuario <b>local</b> del panel (el creado al activarlo, normalmente <b>admin</b>), no la cuenta de la nube Hik-Connect. Tras varios intentos fallidos el panel bloquea el acceso por 30 minutos.";
  });

  const readForm = () => ({
    name: $("#al-name").value.trim() || "(sin nombre)",
    driverKey: $("#al-driver").value,
    host: $("#al-host").value.trim(),
    port: Number($("#al-port").value),
    useHttps: $("#al-https").checked,
    username: $("#al-username").value.trim(),
    password: $("#al-password").value || null,
    enabled: $("#al-enabled").checked,
    deviceId: $("#al-device").value.trim() || null,
  });

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
      if (isNew) await Api.post("/api/alarms/panels", body);
      else await Api.put(`/api/alarms/panels/${panel.id}`, body);
      closeModal();
      toast(isNew ? "Panel agregado y validado." : "Panel actualizado.");
      renderAlarmPanels();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}
