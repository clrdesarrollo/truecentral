// CLR TrueCentral VMS — panel: control de acceso (administrador de dispositivos).
//
// Primera etapa del módulo: alta, edición, prueba, revalidación y baja de
// terminales y controladoras, más la tabla de equipos en línea (SADP) filtrada
// a lo COMPATIBLE. Las órdenes sobre puertas, el padrón de personas y el
// historial de accesos llegan después y se cuelgan de esta misma página.
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

let accessDriversCache = null;
async function getAccessDrivers() {
  accessDriversCache ??= await Api.get("/api/access/drivers");
  return accessDriversCache;
}

let accessTimer = null;

const ACCESS_KIND_LABELS = {
  Terminal: "Terminal",
  Controller: "Controladora",
  Turnstile: "Torniquete",
  Unknown: "—",
};

/** Marca visible a partir del driver con el que se administra el equipo. */
const ACCESS_BRANDS = {
  "hikvision-isapi": "Hikvision",
  "dahua-http": "Dahua",
  "zkteco-tcp": "ZKTeco",
};
const accessBrandOf = (driverKey) => ACCESS_BRANDS[driverKey] ?? driverKey;

/**
 * Marcas cuyos equipos aceptan el padrón del VMS (personas, credenciales y
 * horarios). En las demás el padrón se carga en el propio equipo, así que no se
 * ofrece un botón que iba a fallar. El servidor lo verifica igual.
 */
const ACCESS_PADRON_DRIVERS = ["hikvision-isapi"];

function accessStatusTag(device) {
  switch (device.status) {
    case "Online": return `<span class="tag on">En línea</span>`;
    case "Offline": return `<span class="tag off">Sin conexión</span>`;
    case "AuthFailed": return `<span class="tag off">Credenciales</span>`;
    default: return `<span class="tag operator">—</span>`;
  }
}

function accessStatusCell(d) {
  return `${accessStatusTag(d)}${d.lastError
    ? `<div class="muted" style="font-size:11px;max-width:240px" title="${esc(d.lastError)}">${esc(d.lastError)}</div>`
    : ""}`;
}

function accessCapsTags(d) {
  const tags = [];
  if (d.supportsRemoteControl) tags.push(`<span class="tag on" title="Acepta órdenes remotas de apertura de puerta">Apertura remota</span>`);
  if (d.supportsEvents) tags.push(`<span class="tag admin" title="Entrega eventos y el historial de accesos">Eventos</span>`);
  if (d.supportsCards) tags.push(`<span class="tag admin" title="Administra tarjetas">Tarjeta</span>`);
  if (d.supportsFingerprint) tags.push(`<span class="tag admin" title="Lector de huella">Huella</span>`);
  if (d.supportsFace) tags.push(`<span class="tag admin" title="Reconocimiento facial">Rostro</span>`);
  return tags.join(" ") || `<span class="muted">—</span>`;
}

function accessDoorsCell(d) {
  if (!d.doors.length) return `<span class="muted">—</span>`;
  const names = d.doors.map((door) => `${door.number}. ${door.name}`).join(" · ");
  return `<span title="${esc(names)}">${d.doors.length}</span>
    <div class="muted" style="font-size:11px;max-width:220px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(names)}</div>`;
}

/**
 * Actualización en sitio (sin redibujar): solo cambia la celda de conexión de
 * cada fila, para no cerrar lo que el administrador tenga abierto. Devuelve
 * false si cambió el conjunto de equipos (hay que redibujar).
 */
async function refreshAccessDevicesInPlace() {
  const devices = await Api.get("/api/access/devices");
  const rows = $$("#view tr[data-id]");
  const ids = rows.map((r) => Number(r.dataset.id)).sort().join(",");
  if (ids !== devices.map((d) => d.id).sort().join(",")) return false;
  for (const d of devices) {
    const cell = $(`#view tr[data-id="${d.id}"] .ac-status`);
    const html = accessStatusCell(d);
    if (cell && cell.innerHTML !== html) cell.innerHTML = html;
  }
  return true;
}

async function renderAccessDevices() {
  $("#page-title").textContent = "Control de acceso";
  let devices;
  try { devices = await Api.get("/api/access/devices"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  // El último sondeo es global: solo sirve si es el de ESTA página (si no, la
  // tabla mostraría cámaras hasta que termine el primer sondeo de acceso).
  const scanReady = discoveryOptions === ACCESS_DISCOVERY && lastScan;
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Equipos de control de acceso <span class="muted" style="font-weight:normal;font-size:12px">(el estado se actualiza solo)</span></h3>
      ${isAdmin ? `<button class="btn" id="btn-access-new">Agregar equipo</button>` : ""}
    </div>
    ${devices.length === 0 ? `
      <div class="info-box">
        Aún no hay equipos de control de acceso. ${isAdmin
          ? "Use <b>Agregar equipo</b> o la tabla de equipos en línea de más abajo: al guardar se validan las credenciales contra el equipo y se leen su modelo, firmware, capacidades y las puertas que administra."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Ubicación</th><th>Marca</th><th>Tipo</th><th>Dirección</th><th>Modelo</th>
          <th>Firmware</th><th>Puertas</th><th>Funciones</th><th>Conexión</th><th></th>
        </tr></thead>
        <tbody>
          ${devices.map((d) => `
            <tr data-id="${d.id}">
              <td>${esc(d.name)}${d.enabled ? "" : ` <span class="tag operator" title="Desactivado">pausado</span>`}</td>
              <td class="muted">${esc(d.location ?? "—")}</td>
              <td class="muted">${esc(accessBrandOf(d.driverKey))}</td>
              <td>${esc(ACCESS_KIND_LABELS[d.kind] ?? d.kind)}</td>
              <td class="muted">${d.useHttps ? "https://" : ""}${esc(d.host)}:${d.port}</td>
              <td>${esc(d.model ?? "—")}<div class="muted" style="font-size:11px">${esc(d.serialNumber ?? "")}</div></td>
              <td class="muted">${esc(d.firmwareVersion ?? "—")}</td>
              <td>${accessDoorsCell(d)}</td>
              <td>${accessCapsTags(d)}</td>
              <td class="ac-status">${accessStatusCell(d)}</td>
              <td><div class="row-actions" style="flex-wrap:wrap">
                ${isAdmin ? `<button class="btn ghost btn-ac-revalidate" title="Volver a sondear el equipo (modelo, firmware, capacidades y puertas)">Revalidar</button>
                ${ACCESS_PADRON_DRIVERS.includes(d.driverKey) ? `<button class="btn ghost btn-ac-resync"
                  title="Reescribe en este equipo todas las personas, credenciales y horarios que le corresponden">Reenviar padrón</button>` : ""}
                <button class="btn ghost btn-ac-edit">Editar</button>
                <button class="btn danger btn-ac-delete">Eliminar</button>` : ""}
              </div></td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}
    ${isAdmin ? `
    <div class="toolbar" style="margin-top:28px">
      <h3>Equipos en línea <span class="muted" style="font-weight:normal;font-size:12px">(SADP · DHDiscover · ZKTeco en la red local · se actualiza cada 30 s)</span></h3>
      <button class="btn ghost" id="btn-access-scan">Buscar</button>
    </div>
    <div id="access-online">
      ${scanReady ? "" : `<div class="info-box">Sondeando el segmento de red del servidor…
        Solo se listan equipos de <b>control de acceso compatibles</b>: Hikvision DS-K (SADP), Dahua ASI/ASC/ASG
        (DHDiscover) y ZKTeco (protocolo propio, puerto 4370). Las cámaras, los videoporteros y las alarmas se
        administran en sus propias páginas.</div>`}
    </div>` : ""}`;

  const byRow = (e) => devices.find((d) => d.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-access-new")?.addEventListener("click", () => accessDeviceModal(null));
  $("#btn-access-scan")?.addEventListener("click", () => runDiscovery(devices));
  if (scanReady) renderOnlineDevices(devices);
  if (isAdmin) startDiscoveryPolling(devices, ACCESS_DISCOVERY);

  $$("#view .btn-ac-edit").forEach((b) => b.addEventListener("click", (e) => accessDeviceModal(byRow(e))));
  $$("#view .btn-ac-resync").forEach((b) => b.addEventListener("click", async (e) => {
    const device = byRow(e);
    const button = e.currentTarget;
    const aviso = [
      `¿Reenviar el padrón completo al equipo "${device.name}"?`,
      "",
      "Se le reescriben todas las personas que tienen permiso en sus puertas, con sus credenciales y",
      "horarios, aunque el VMS ya lo dé por escrito. Sirve cuando el equipo perdió lo suyo: se lo",
      "reemplazó, se lo volvió a fábrica, o alguien le borró personas desde su pantalla.",
    ].join("\n");
    if (!confirm(aviso)) return;

    button.disabled = true;
    button.textContent = "Reenviando…";
    try {
      const r = await Api.post(`/api/access/devices/${device.id}/resync`);
      toast(r.resent
        ? `Reenvío a "${device.name}" terminado: ${r.processed} de ${r.resent} persona(s) procesada(s).`
        : `No hay personas con permiso en las puertas de "${device.name}".`);
    } catch (err) {
      toast(err.error, true);
    } finally {
      button.disabled = false;
      button.textContent = "Reenviar padrón";
    }
  }));
  $$("#view .btn-ac-revalidate").forEach((b) => b.addEventListener("click", async (e) => {
    const device = byRow(e);
    const button = e.currentTarget;
    button.disabled = true;
    button.textContent = "Sondeando…";
    try {
      await Api.post(`/api/access/devices/${device.id}/revalidate`);
      toast("Equipo revalidado: información, capacidades y puertas actualizadas.");
      renderAccessDevices();
    } catch (err) {
      toast(err.error, true);
      button.disabled = false;
      button.textContent = "Revalidar";
    }
  }));
  $$("#view .btn-ac-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const device = byRow(e);
    if (!confirm(`¿Eliminar el equipo "${device.name}" y sus ${device.doors.length} puerta(s)?`)) return;
    try {
      await Api.delete(`/api/access/devices/${device.id}`);
      toast("Equipo eliminado.");
      renderAccessDevices();
    } catch (err) { toast(err.error, true); }
  }));

  // El estado lo actualiza el sondeo del servidor: cada 15 s se refrescan las
  // celdas en sitio y solo se redibuja si aparece o desaparece un equipo.
  clearInterval(accessTimer);
  accessTimer = setInterval(async () => {
    if (location.hash !== "#/access" || $("#app-shell").classList.contains("hidden")) {
      clearInterval(accessTimer);
      return;
    }
    if ($("#modal-backdrop").classList.contains("hidden") === false) return; // hay un modal abierto
    try {
      if (!(await refreshAccessDevicesInPlace())) renderAccessDevices();
    } catch { /* un refresco fallido no molesta al usuario: se reintenta */ }
  }, 15000);
}

/** Sondeo de red de la página Control de acceso: SOLO equipos compatibles, alta con el modal del módulo. */
const ACCESS_DISCOVERY = {
  container: "#access-online",
  button: "#btn-access-scan",
  kind: "access",
  portLabel: "Puerto HTTP",
  portOf: (d) => d.httpPort || 80,
  emptyText: "No se encontraron equipos de control de acceso compatibles en este segmento de red. " +
    "Los sondeos son de difusión y no cruzan routers ni VPN: solo ven el segmento del servidor. " +
    "Un equipo fuera de él se agrega a mano con su dirección.",
  onUse: (d) => accessDeviceModal(null, {
    name: d.model || d.ip,
    host: d.ip,
    port: d.httpPort || 80,
    driverKey: d.driverKey || "hikvision-isapi",
  }),
};

/**
 * Campos de credencial del formulario: cambian con la marca. Hikvision y Dahua
 * piden el usuario y la contraseña del equipo (y admiten HTTPS); ZKTeco no
 * tiene usuario sino una clave de comunicación numérica, y no habla HTTP.
 */
function accessCredentialFields(driver, device, isNew) {
  const hint = driver?.hint ? `<div class="info-box" style="margin-top:10px">${esc(driver.hint)}</div>` : "";
  if (driver?.authMode === "CommKey") {
    return `
      <div class="field">
        <label>Clave de comunicación${isNew ? "" : " (vacío = no cambiar)"}</label>
        <input id="ac-password" type="password" inputmode="numeric" autocomplete="new-password" placeholder="0">
      </div>
      ${hint}`;
  }
  return `
      <div class="form-grid">
        <div class="field">
          <label>Usuario del equipo</label>
          <input id="ac-username" required value="${esc(device?.username ?? "admin")}" placeholder="admin">
        </div>
        <div class="field">
          <label>Contraseña${isNew ? "" : " (vacío = no cambiar)"}</label>
          <input id="ac-password" type="password" autocomplete="new-password" ${isNew ? "required" : ""}>
        </div>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="ac-https" ${(device ? device.useHttps : driver?.defaultHttps) ? "checked" : ""}> Usar HTTPS (certificado autofirmado aceptado)</label>
      ${hint}`;
}

const accessPortLabel = (driver) => driver?.authMode === "CommKey" ? "Puerto del equipo" : "Puerto HTTP";

async function accessDeviceModal(device, prefill) {
  const isNew = !device;
  const seed = isNew ? (prefill || {}) : {};
  let drivers;
  try { drivers = await getAccessDrivers(); }
  catch (err) { toast(err.error, true); return; }
  const driver0 = drivers.find((d) => d.key === (device?.driverKey ?? seed.driverKey)) ?? drivers[0];

  openModal(`
    <h3>${isNew ? "Agregar equipo de control de acceso" : "Editar equipo de control de acceso"}</h3>
    <div id="ac-modal-error"></div>
    <form id="access-form">
      <div class="form-grid">
        <div class="field">
          <label>Nombre</label>
          <input id="ac-name" required maxlength="128" value="${esc(device?.name ?? seed.name ?? "")}" placeholder="Portería principal, Torniquete casino…">
        </div>
        <div class="field">
          <label>Ubicación (opcional)</label>
          <input id="ac-location" maxlength="128" value="${esc(device?.location ?? "")}" placeholder="Edificio A, Planta 2…">
        </div>
      </div>
      <div class="field">
        <label>Marca / protocolo</label>
        <select id="ac-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${driver0?.key === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección (IP o hostname)</label>
          <input id="ac-host" required value="${esc(device?.host ?? seed.host ?? "")}" placeholder="192.168.1.65">
        </div>
        <div class="field">
          <label id="ac-port-label">${accessPortLabel(driver0)}</label>
          <input id="ac-port" type="number" min="1" max="65535" required value="${device?.port ?? seed.port ?? driver0?.defaultPort ?? 80}">
        </div>
      </div>
      <div id="ac-credentials">${accessCredentialFields(driver0, device, isNew)}</div>
      <label class="checkbox-row"><input type="checkbox" id="ac-enabled" ${device ? (device.enabled ? "checked" : "") : "checked"}> Activo (sondeo de estado; sus puertas ocupan cupo de la licencia)</label>
      <div class="info-box" style="margin-top:10px">Al guardar se leen del equipo el modelo, el firmware, las capacidades y las puertas que administra.</div>
      <div id="ac-probe-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="ac-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="ac-probe">Probar conexión</button>
        <button class="btn" type="submit" id="ac-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);

  $("#ac-cancel").addEventListener("click", closeModal);
  $("#ac-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#ac-driver").value);
    if (!dr) return;
    if (isNew) $("#ac-port").value = dr.defaultPort;
    $("#ac-port-label").textContent = accessPortLabel(dr);
    // La credencial no es la misma en todas las marcas: se redibuja el bloque.
    $("#ac-credentials").innerHTML = accessCredentialFields(dr, device, isNew);
  });

  const readForm = () => ({
    name: $("#ac-name").value.trim() || "(sin nombre)",
    driverKey: $("#ac-driver").value,
    host: $("#ac-host").value.trim(),
    port: Number($("#ac-port").value),
    // ZKTeco no tiene ni usuario ni HTTPS: sus campos no existen en el formulario.
    useHttps: $("#ac-https")?.checked ?? false,
    username: $("#ac-username")?.value.trim() ?? "",
    password: $("#ac-password").value || null,
    enabled: $("#ac-enabled").checked,
    location: $("#ac-location").value.trim() || null,
  });

  $("#ac-probe").addEventListener("click", async () => {
    const errorBox = $("#ac-modal-error");
    const resultBox = $("#ac-probe-result");
    errorBox.innerHTML = "";
    resultBox.innerHTML = `<div class="info-box">Conectando con el equipo…</div>`;
    const probeButton = $("#ac-probe");
    probeButton.disabled = true;
    try {
      const r = await Api.post(`/api/access/probe${isNew ? "" : `?deviceId=${device.id}`}`, readForm());
      if (!r.success) {
        resultBox.innerHTML = `<div class="error-box">${esc(r.error)}</div>`;
        return;
      }
      const yesNo = (v) => v ? "sí" : "no";
      resultBox.innerHTML = `
        <div class="probe-box">
          <div class="probe-title">✔ Conexión validada</div>
          <div class="probe-grid">
            <span>Modelo</span><b>${esc(r.model ?? "—")}</b>
            <span>Tipo</span><b>${esc(ACCESS_KIND_LABELS[r.kind] ?? r.kind)}</b>
            <span>N° de serie</span><b>${esc(r.serialNumber ?? "—")}</b>
            <span>Firmware</span><b>${esc(r.firmwareVersion ?? "—")}</b>
            <span>Puertas</span><b>${r.doorCount}${r.doorNames.length ? ` (${esc(r.doorNames.join(", "))})` : ""}</b>
            <span>Apertura remota</span><b>${yesNo(r.supportsRemoteControl)}</b>
            <span>Eventos</span><b>${yesNo(r.supportsEvents)}</b>
            <span>Credenciales</span><b>${[r.supportsCards ? "tarjeta" : null, r.supportsFingerprint ? "huella" : null,
              r.supportsFace ? "rostro" : null].filter(Boolean).join(", ") || "—"}</b>
            <span>Cupos del equipo</span><b>${r.userCapacity ?? "—"} personas · ${r.cardCapacity ?? "—"} tarjetas</b>
          </div>
        </div>`;
    } catch (err) {
      resultBox.innerHTML = "";
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      probeButton.disabled = false;
    }
  });

  $("#access-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#ac-modal-error");
    errorBox.innerHTML = "";
    const saveButton = $("#ac-save");
    saveButton.disabled = true;
    saveButton.textContent = "Validando…";
    try {
      const body = readForm();
      if (isNew) await Api.post("/api/access/devices", body);
      else await Api.put(`/api/access/devices/${device.id}`, body);
      closeModal();
      toast(isNew ? "Equipo agregado." : "Equipo actualizado.");
      renderAccessDevices();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      saveButton.disabled = false;
      saveButton.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}
