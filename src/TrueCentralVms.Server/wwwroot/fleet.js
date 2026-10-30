// CLR TrueCentral VMS — panel: Flota → vehículos (Configuración → Dispositivos → Vehículos).
//
// Cada vehículo tiene un grabador móvil (MDVR) que se conecta solo al servidor por ISUP (4G): aquí se da de alta con su
// código interno, su patente y el ID ISUP del grabador. Las cámaras se leen del grabador en cuanto se conecta. Licencia: un
// vehículo habilitado consume un cupo de Flota; sus cámaras no consumen canales de video.
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos al cargar) y app.js las referencia desde su
// tabla de rutas. La grilla en vivo de Flota es la de live.js en modo Flota (renderFleetLive).
"use strict";

let flHubBound = null;

function fleetVehiclesDetachHub() {
  if (!flHubBound) return;
  VmsHub.off("ConfigChanged", flHubBound.config);
  VmsHub.off("DeviceStatusChanged", flHubBound.status);
  flHubBound = null;
}

function flStatusTag(v) {
  if (v.disabledByLicense) return licenseWaitTag(v).trim();
  if (!v.enabled) return `<span class="tag operator">Deshabilitado</span>`;
  if (v.status === "Online") return `<span class="tag on">Conectado</span>`;
  if (!v.channels.length) return `<span class="tag operator" title="Todavía no se conecta: sus cámaras se leen en el primer contacto">Esperando conexión</span>`;
  return `<span class="tag off">Sin conexión</span>`;
}

function flLastSeen(v) {
  if (v.status === "Online") return v.position ? `${Math.round(v.position.speedKmh)} km/h · ${v.position.satellites} satélites` : "en línea";
  return v.lastSeenAt ? `visto ${new Date(v.lastSeenAt).toLocaleString("es-CL")}` : "nunca conectado";
}

function flChannelsHtml(v) {
  if (!v.channels.length) return `<span class="muted">—</span>`;
  return `<div class="fl-chans">${v.channels.map((c) => `
    <span class="fl-chan ${c.enabled ? "" : "off"}" title="Canal ${c.channelNumber}${c.enabled ? "" : " (deshabilitado)"}">
      <span class="dot ${c.isOnline ? "ok" : "bad"}"></span>${esc(c.name)}</span>`).join("")}</div>`;
}

async function renderFleetVehicles() {
  fleetVehiclesDetachHub();
  $("#page-title").textContent = "Vehículos";
  let vehicles, detected = [];
  try {
    [vehicles, detected] = await Promise.all([
      Api.get("/api/fleet/vehicles"),
      Api.get("/api/fleet/detected").catch(() => []),
    ]);
  } catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const usage = await flLicenseUsage();

  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        <button class="btn" id="btn-fl-new">Agregar vehículo</button>
        <button class="btn ghost" id="btn-fl-import">Importar CSV…</button>
      </div>
    </div>
    ${usage ? `<div class="muted fl-hint" style="margin-bottom:8px">Licencia: ${usage.inUse} de ${usage.quota} vehículos habilitados.
      Las cámaras de los vehículos no consumen canales de video (hasta 16 por vehículo).</div>` : ""}
    ${detected.length ? `<div class="info-box">
      <b>Grabadores conectándose sin estar dados de alta:</b>
      ${detected.map((d) => `<button class="btn ghost small fl-detected" data-id="${esc(d.isupDeviceId)}"
        title="Intentó registrarse ${d.count} vez/veces; la última, ${new Date(d.lastAt).toLocaleString("es-CL")}">${esc(d.isupDeviceId)} · agregar</button>`).join(" ")}
    </div>` : ""}
    ${vehicles.length === 0 ? `
      <div class="info-box">
        Aún no hay vehículos. Use <b>Agregar vehículo</b> con el código interno (por ejemplo <b>UUBB-0387</b>), la patente y el
        <b>ID ISUP</b> configurado en su grabador móvil, o <b>Importar CSV…</b> para cargar la flota completa. En el grabador hay
        que apuntar la plataforma ISUP (EHome) a la dirección de este servidor; sus cámaras aparecen solas cuando se conecta.
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr><th>Vehículo</th><th>Patente</th><th>Grabador</th><th>Estado</th><th>Cámaras</th><th>Ubicación</th><th></th></tr></thead>
        <tbody>${vehicles.map((v) => `
          <tr data-id="${v.id}">
            <td><b>${esc(v.code)}</b>${v.kind ? `<div class="muted">${esc(v.kind)}</div>` : ""}</td>
            <td>${v.plate ? `<span class="fl-plate">${esc(v.plate)}</span>` : `<span class="muted">—</span>`}</td>
            <td>ISUP <b>${esc(v.isupDeviceId ?? "—")}</b>${v.model ? `<div class="muted">${esc(v.model)}</div>` : ""}</td>
            <td>${flStatusTag(v)}<div class="muted">${esc(flLastSeen(v))}</div></td>
            <td>${flChannelsHtml(v)}</td>
            <td>${v.location ? esc(v.location) : `<span class="muted">Por ubicar</span>`}</td>
            <td class="row-actions">
              <button class="btn ghost small fl-edit">Editar</button>
              <button class="btn ghost small fl-cams" ${v.channels.length ? "" : "disabled"}>Cámaras</button>
              <button class="btn ghost small fl-reval" ${v.status === "Online" ? "" : "disabled"} title="Volver a leer los datos y cámaras del grabador">Releer</button>
              <button class="btn danger small fl-delete">Eliminar</button>
            </td>
          </tr>`).join("")}</tbody>
      </table></div>`}`;

  const byRow = (e) => vehicles.find((v) => v.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-fl-new").addEventListener("click", () => openFormPage("#/fleet/vehicles/new"));
  $("#btn-fl-import").addEventListener("click", flImportModal);
  $$("#view .fl-detected").forEach((b) => b.addEventListener("click", () =>
    openFormPage(`#/fleet/vehicles/new?detected=${encodeURIComponent(b.dataset.id)}`)));
  $$("#view .fl-edit").forEach((b) => b.addEventListener("click", (e) => flVehicleModal(byRow(e))));
  $$("#view .fl-cams").forEach((b) => b.addEventListener("click", (e) => flCamerasModal(byRow(e))));
  $$("#view .fl-reval").forEach((b) => b.addEventListener("click", async (e) => {
    const v = byRow(e);
    b.disabled = true;
    try {
      await Api.post(`/api/devices/${v.deviceId}/revalidate`);
      toast(`Grabador de ${v.code} releído.`);
      renderFleetVehicles();
    } catch (err) { toast(err.error, true); b.disabled = false; }
  }));
  $$("#view .fl-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const v = byRow(e);
    if (!confirm(`¿Eliminar el vehículo ${v.code}? Se borran su grabador y sus cámaras (y salen de las vistas guardadas).`)) return;
    try {
      await Api.delete(`/api/fleet/vehicles/${v.id}`);
      toast("Vehículo eliminado.");
      renderFleetVehicles();
    } catch (err) { toast(err.error, true); }
  }));

  const here = () => location.hash.startsWith("#/fleet/vehicles");
  const modalOpen = () => $("#modal-backdrop") && !$("#modal-backdrop").classList.contains("hidden");
  let reload = null;
  const later = () => { clearTimeout(reload); reload = setTimeout(() => { if (here() && !modalOpen()) renderFleetVehicles(); }, 800); };
  flHubBound = {
    config: (topic) => { if (["fleet", "devices", "channels"].includes(topic)) later(); },
    status: (dto) => { if (vehicles.some((v) => v.deviceId === dto.id)) later(); },
  };
  VmsHub.on("ConfigChanged", flHubBound.config);
  VmsHub.on("DeviceStatusChanged", flHubBound.status);
}

/** Cupo de vehículos de la licencia (null si no se pudo leer). */
async function flLicenseUsage() {
  try {
    const status = await Api.get("/api/system/license");
    const m = (status.modules || []).find((x) => x.quotaKey === "fleet_vehicles");
    return m && m.quota != null ? { quota: m.quota, inUse: m.inUse ?? 0 } : null;
  } catch { return null; }
}

/** Alta / edición de un vehículo. <paramref name="detectedId"/> precarga el ID de un grabador que intenta conectarse. */
async function flVehicleModal(vehicle, detectedId) {
  const isNew = !vehicle;
  let places, conn = null;
  try { [places, conn] = await Promise.all([loadLocationChoices(), Api.get("/api/isup/connection").catch(() => null)]); }
  catch (err) { toast(err.error, true); return; }
  const where = conn?.address ? `<b>${esc(conn.address)}</b>, puerto <b>${conn.port}</b>` : "la dirección pública de este servidor (puerto 7660)";

  openModal(`
    <h3>${isNew ? "Agregar vehículo" : `Editar vehículo ${esc(vehicle.code)}`}</h3>
    <div id="fl-modal-error"></div>
    <form id="fl-form">
      <div class="form-grid">
        <div class="field">
          <label>Código interno</label>
          <input id="fl-code" required maxlength="64" value="${esc(vehicle?.code ?? "")}" placeholder="UUBB-0387">
        </div>
        <div class="field">
          <label>Patente</label>
          <input id="fl-plate" maxlength="16" value="${esc(vehicle?.plate ?? "")}" placeholder="JKLP23" style="text-transform:uppercase">
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Tipo</label>
          <input id="fl-kind" maxlength="64" value="${esc(vehicle?.kind ?? "")}" placeholder="Camioneta, bus, furgón…">
        </div>
        ${locationFieldHtml("fl-location", places, vehicle?.locationId ?? null)}
      </div>
      <div class="form-grid">
        <div class="field">
          <label>ID ISUP del grabador</label>
          <input id="fl-isup" required maxlength="32" value="${esc(vehicle?.isupDeviceId ?? detectedId ?? "")}" placeholder="123456">
        </div>
        <div class="field">
          <label>Clave ISUP ${vehicle?.hasIsupKey ? "(guardada: vacía la conserva)" : "(opcional)"}</label>
          <input id="fl-key" type="password" maxlength="64" autocomplete="new-password" placeholder="${vehicle?.hasIsupKey ? "••••••" : "Solo ISUP 5.0"}">
        </div>
      </div>
      <div class="muted fl-hint">En el grabador: plataforma ISUP/EHome habilitada, servidor ${where} y este mismo ID. Los grabadores
        ISUP 2.0/4.0 no usan clave y requieren la compatibilidad 2.0/4.0 encendida en Sistema → Conexión ISUP.</div>
      <div class="field">
        <label>Notas</label>
        <textarea id="fl-notes" maxlength="1024" rows="2">${esc(vehicle?.notes ?? "")}</textarea>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="fl-enabled" ${vehicle ? (vehicle.enabled ? "checked" : "") : "checked"}>
        Habilitado (consume un vehículo de la licencia y sus cámaras se pueden ver)</label>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="fl-cancel">Cancelar</button>
        <button class="btn" type="submit" id="fl-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`, true);

  $("#fl-cancel").addEventListener("click", closeModal);
  $("#fl-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const body = {
      code: $("#fl-code").value.trim(),
      plate: $("#fl-plate").value.trim() || null,
      kind: $("#fl-kind").value.trim() || null,
      notes: $("#fl-notes").value.trim() || null,
      isupDeviceId: $("#fl-isup").value.trim(),
      isupKey: $("#fl-key").value || null,
      locationId: locationFieldValue("fl-location"),
      enabled: $("#fl-enabled").checked,
    };
    $("#fl-save").disabled = true;
    try {
      const saved = isNew ? await Api.post("/api/fleet/vehicles", body) : await Api.put(`/api/fleet/vehicles/${vehicle.id}`, body);
      closeModal();
      toast(saved.warning || (isNew ? `Vehículo ${saved.code} agregado.` : "Cambios guardados."), !!saved.warning);
      renderFleetVehicles();
    } catch (err) {
      $("#fl-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      $("#fl-save").disabled = false;
    }
  });
}

/** Nombres y habilitado de las cámaras de un vehículo (los nombres del grabador suelen ser "Canal N"). */
function flCamerasModal(vehicle) {
  openModal(`
    <h3>Cámaras de ${esc(vehicle.code)}</h3>
    <div id="fl-cam-error"></div>
    <div class="muted fl-hint" style="margin-bottom:8px">Un nombre claro ayuda en la grilla: «${esc(vehicle.code)} cabina», «${esc(vehicle.code)} carga»…
      Hasta 16 cámaras habilitadas por vehículo.</div>
    <form id="fl-cam-form">
      ${vehicle.channels.map((c) => `
        <div class="form-grid" data-ch="${c.id}">
          <div class="field"><label>Canal ${c.channelNumber}</label><input data-name maxlength="128" value="${esc(c.name)}"></div>
          <div class="field"><label>&nbsp;</label><label class="checkbox-row"><input type="checkbox" data-enabled ${c.enabled ? "checked" : ""}> Habilitada</label></div>
        </div>`).join("")}
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="fl-cam-cancel">Cancelar</button>
        <button class="btn" type="submit" id="fl-cam-save">Guardar</button>
      </div>
    </form>`, true);
  $("#fl-cam-cancel").addEventListener("click", closeModal);
  $("#fl-cam-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    $("#fl-cam-save").disabled = true;
    try {
      for (const row of $$("#fl-cam-form [data-ch]")) {
        const c = vehicle.channels.find((x) => x.id === Number(row.dataset.ch));
        const name = row.querySelector("[data-name]").value.trim();
        const enabled = row.querySelector("[data-enabled]").checked;
        if (name === c.name && enabled === c.enabled) continue;
        await Api.put(`/api/devices/${vehicle.deviceId}/channels/${c.id}`, { name, enabled, supportsPtz: false, useFfmpegProxy: false });
      }
      closeModal();
      toast("Cámaras guardadas.");
      renderFleetVehicles();
    } catch (err) {
      $("#fl-cam-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      $("#fl-cam-save").disabled = false;
    }
  });
}

/** Importación de la flota desde CSV: primero se revisa (sin guardar) y después se importa. */
async function flImportModal() {
  let places;
  try { places = await loadLocationChoices(); } catch (err) { toast(err.error, true); return; }
  openModal(`
    <h3>Importar vehículos desde CSV</h3>
    <div class="muted fl-hint">Una fila por vehículo, separada por punto y coma o coma:
      <code>código;patente;tipo;id_isup;clave_isup</code> (la clave es opcional; la primera fila puede ser de encabezados).
      Por ejemplo: <code>UUBB-0387;JKLP23;Camioneta;123456</code>.</div>
    <div class="field" style="margin-top:8px"><label>Archivo</label><input type="file" id="fl-csv-file" accept=".csv,.txt,text/csv"></div>
    <div class="field"><label>O pegue el contenido</label><textarea id="fl-csv" rows="6" placeholder="código;patente;tipo;id_isup"></textarea></div>
    ${locationFieldHtml("fl-imp-location", places, null)}
    <div id="fl-imp-result" class="fl-import-result"></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="fl-imp-cancel">Cerrar</button>
      <button class="btn ghost" type="button" id="fl-imp-check">Revisar</button>
      <button class="btn" type="button" id="fl-imp-go">Importar</button>
    </div>`, true);
  $("#fl-imp-cancel").addEventListener("click", () => { closeModal(); renderFleetVehicles(); });
  $("#fl-csv-file").addEventListener("change", async (e) => {
    const file = e.target.files?.[0];
    if (file) $("#fl-csv").value = await file.text();
  });
  const run = async (dryRun) => {
    const csv = $("#fl-csv").value;
    if (!csv.trim()) { $("#fl-imp-result").innerHTML = `<div class="error-box">Elija un archivo o pegue el contenido.</div>`; return; }
    $("#fl-imp-check").disabled = $("#fl-imp-go").disabled = true;
    $("#fl-imp-result").innerHTML = `<div class="muted">${dryRun ? "Revisando" : "Importando"}…</div>`;
    try {
      const r = await Api.post("/api/fleet/vehicles/import", { csv, dryRun, locationId: locationFieldValue("fl-imp-location") });
      $("#fl-imp-result").innerHTML = `
        <div><b>${r.created}</b> ${dryRun ? "listos para importar" : "agregados"} · <b>${r.failed}</b> con error</div>
        <table class="grid"><tbody>${r.rows.filter((x) => !x.ok || dryRun).map((x) => `
          <tr><td>Línea ${x.line}</td><td>${esc(x.code)}</td><td>${esc(x.isupDeviceId ?? "")}</td>
          <td class="${x.ok ? "" : "bad"}">${x.ok ? "OK" : esc(x.error)}</td></tr>`).join("")}</tbody></table>`;
      if (!dryRun && r.created) toast(`${r.created} vehículo(s) importados.`);
    } catch (err) {
      $("#fl-imp-result").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      $("#fl-imp-check").disabled = $("#fl-imp-go").disabled = false;
    }
  };
  $("#fl-imp-check").addEventListener("click", () => run(true));
  $("#fl-imp-go").addEventListener("click", () => run(false));
}
