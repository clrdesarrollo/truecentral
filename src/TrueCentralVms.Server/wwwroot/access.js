// CLR TrueCentral VMS — panel: control de acceso (administrador de dispositivos).
//
// Primera etapa del módulo: alta, edición, prueba, revalidación y baja de
// terminales y controladoras, más la tabla de equipos en línea (SADP) filtrada
// a lo COMPATIBLE. Las órdenes sobre puertas, el padrón de personas y el
// historial de accesos llegan después y se cuelgan de esta misma página.
//
// El alta es un modal; todo lo demás de un equipo (conexión, parámetros de
// sus puertas y de sus lectores, hora y mantenimiento) vive en su PÁGINA
// (#/access/device?id=N), al final de este archivo.
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
              <td><a href="#/access/device?id=${d.id}" class="acd-link" title="Abrir la página del equipo">${esc(d.name)}</a>${d.enabled ? "" : ` <span class="tag operator" title="Desactivado">pausado</span>`}</td>
              <td class="muted">${d.location ? esc(d.location) : `<span class="loc-line none">Por ubicar</span>`}</td>
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
                <a class="btn ghost" href="#/access/device?id=${d.id}" title="Conexión, puertas, lectores, hora y mantenimiento">Configurar</a>
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

/**
 * Alta de un equipo (modal). La edición ya no pasa por acá: cada equipo tiene
 * su página, con espacio para todo lo que se le configura. `device` queda
 * admitido por compatibilidad (el formulario es el mismo).
 */
async function accessDeviceModal(device, prefill) {
  const isNew = !device;
  const seed = isNew ? (prefill || {}) : {};
  let drivers, places;
  try { [drivers, places] = await Promise.all([getAccessDrivers(), loadLocationChoices()]); }
  catch (err) { toast(err.error, true); return; }
  const driver0 = drivers.find((d) => d.key === (device?.driverKey ?? seed.driverKey)) ?? drivers[0];

  openModal(`
    <h3>${isNew ? "Agregar equipo de control de acceso" : "Editar equipo de control de acceso"}</h3>
    <div id="ac-modal-error"></div>
    <form id="access-form">
      <div class="field">
        <label>Nombre</label>
        <input id="ac-name" required maxlength="128" value="${esc(device?.name ?? seed.name ?? "")}" placeholder="Portería principal, Torniquete casino…">
      </div>
      ${locationFieldHtml("ac-location", places, device?.locationId ?? null,
        "Sus puertas la heredan; las que ubique aparte en Recursos se quedan donde están.")}
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
      ${isNew ? "" : maintDeviceSectionHtml()}
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="ac-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="ac-probe">Probar conexión</button>
        <button class="btn" type="submit" id="ac-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`, true);

  $("#ac-cancel").addEventListener("click", closeModal);
  if (!isNew) maintFillDeviceSection("access", device.id);
  $("#ac-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#ac-driver").value);
    if (!dr) return;
    if (isNew) $("#ac-port").value = dr.defaultPort;
    $("#ac-port-label").textContent = accessPortLabel(dr);
    // La credencial no es la misma en todas las marcas: se redibuja el bloque.
    $("#ac-credentials").innerHTML = accessCredentialFields(dr, device, isNew);
  });

  const readForm = accessReadConnectionForm;

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
      resultBox.innerHTML = accessProbeResultHtml(r);
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

// ---------------------------------------------------------------------------
// Formulario de conexión y resultado de la prueba: los comparten el modal de
// alta y la página del equipo (mismos ids de campos).
// ---------------------------------------------------------------------------

function accessReadConnectionForm() {
  return {
    name: $("#ac-name").value.trim() || "(sin nombre)",
    driverKey: $("#ac-driver").value,
    host: $("#ac-host").value.trim(),
    port: Number($("#ac-port").value),
    // ZKTeco no tiene ni usuario ni HTTPS: sus campos no existen en el formulario.
    useHttps: $("#ac-https")?.checked ?? false,
    username: $("#ac-username")?.value.trim() ?? "",
    password: $("#ac-password").value || null,
    enabled: $("#ac-enabled").checked,
    locationId: locationFieldValue("ac-location"),
  };
}

function accessProbeResultHtml(r) {
  const yesNo = (v) => v ? "sí" : "no";
  return `
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
}

// ---------------------------------------------------------------------------
// Página del equipo (#/access/device?id=7&tab=doors). Reemplaza al modal de
// edición, que quedaba angosto para todo lo que un equipo permite configurar:
//   · Conexión: lo que antes estaba en el modal (nombre, ubicación, marca,
//     dirección, credenciales, activo) con prueba de conexión, y las puertas
//     del equipo en el VMS: activa o pausada, y su ficha de Recursos (donde se
//     les cambia el nombre).
//   · Puertas y Lectores: los parámetros PROPIOS del equipo (contacto de
//     puerta, tiempos de apertura, umbrales del reconocimiento facial, LED
//     del lector…), leídos del equipo al abrir la pestaña y escritos de
//     vuelta al guardar cada bloque. El VMS no los guarda.
//   · Capacidades: lo que el equipo DECLARA saber hacer (sus rutas de
//     capacidades oficiales), leído al validarlo; el VMS lo usa para elegir
//     cómo escribirle.
//   · Hora y mantenimiento: el mismo apartado que tienen las fichas.
// ---------------------------------------------------------------------------

const ACD_TABS = [
  { key: "connection", label: "Conexión" },
  { key: "doors", label: "Puertas" },
  { key: "readers", label: "Lectores" },
  { key: "capabilities", label: "Capacidades" },
  { key: "maintenance", label: "Hora y mantenimiento" },
];

let acdState = null;

async function renderAccessDevicePage() {
  $("#page-title").textContent = "Control de acceso";
  const params = new URLSearchParams(location.hash.split("?")[1] || "");
  const id = Number(params.get("id"));
  if (!(id > 0)) { location.hash = "#/access"; return; }
  const back = `<div class="acd-back"><a class="btn ghost small" href="#/access">← Volver a la lista</a></div>`;
  $("#view").innerHTML = `${back}<div class="muted">Cargando el equipo…</div>`;
  let device;
  try { device = await Api.get(`/api/access/devices/${id}`); }
  catch (err) {
    $("#view").innerHTML = `${back}<div class="error-box">${esc(err.status === 404
      ? "Ese equipo ya no existe (se eliminó o está fuera de su alcance)." : err.error)}</div>`;
    return;
  }
  const tab = ACD_TABS.some((t) => t.key === params.get("tab")) ? params.get("tab") : "connection";
  acdState = { id, device, tab, settings: null, settingsError: null, loadingSettings: false };
  acdDrawPage();
}

function acdDrawPage() {
  const { device: d, tab } = acdState;
  $("#page-title").textContent = `Control de acceso · ${d.name}`;
  const isAdmin = Api.role === "Admin";
  const meta = [
    accessBrandOf(d.driverKey),
    ACCESS_KIND_LABELS[d.kind] && d.kind !== "Unknown" ? ACCESS_KIND_LABELS[d.kind] : null,
    d.model,
    d.firmwareVersion ? `firmware ${d.firmwareVersion}` : null,
    `${d.useHttps ? "https://" : ""}${d.host}:${d.port}`,
    d.location || "Por ubicar",
  ].filter(Boolean).map(esc).join(" · ");
  $("#view").innerHTML = `
    <div class="acd-back"><a class="btn ghost small" href="#/access">← Volver a la lista</a></div>
    <div class="acd-head">
      <div class="acd-title">
        <h3>${esc(d.name)}${d.enabled ? "" : ` <span class="tag operator" title="Desactivado">pausado</span>`}</h3>
        <div class="muted">${meta}</div>
        <div class="acd-tags">${accessCapsTags(d)} ${d.doors.length
          ? `<span class="tag operator" title="${esc(d.doors.map((x) => `${x.number}. ${x.name}`).join(" · "))}">${d.doors.length} puerta(s)</span>` : ""}</div>
      </div>
      <div class="acd-status">
        ${accessStatusCell(d)}
        ${isAdmin ? `<button class="btn ghost small" type="button" id="acd-revalidate"
          title="Volver a sondear el equipo (modelo, firmware, capacidades y puertas)">Revalidar</button>` : ""}
      </div>
    </div>
    <div class="res-tabs" role="tablist">
      ${ACD_TABS.map((t) => `
        <button type="button" role="tab" class="res-tab${t.key === tab ? " on" : ""}" data-tab="${t.key}"
                aria-selected="${t.key === tab}">${t.label}</button>`).join("")}
    </div>
    <div id="acd-body" class="res-tab-body"></div>`;
  $$("#view .res-tab").forEach((b) => b.addEventListener("click", () => acdTab(b.dataset.tab)));
  $("#acd-revalidate")?.addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    button.textContent = "Sondeando…";
    try {
      acdState.device = await Api.post(`/api/access/devices/${acdState.id}/revalidate`);
      acdState.settings = null;   // las puertas y lectores pueden haber cambiado
      toast("Equipo revalidado: información, capacidades y puertas actualizadas.");
      acdDrawPage();
    } catch (err) {
      toast(err.error, true);
      button.disabled = false;
      button.textContent = "Revalidar";
    }
  });
  acdDrawTab();
}

function acdTab(tab) {
  acdState.tab = tab;
  history.replaceState(null, "", `#/access/device?id=${acdState.id}${tab === "connection" ? "" : `&tab=${tab}`}`);
  $$("#view .res-tab").forEach((b) => {
    b.classList.toggle("on", b.dataset.tab === tab);
    b.setAttribute("aria-selected", String(b.dataset.tab === tab));
  });
  acdDrawTab();
}

async function acdDrawTab() {
  const body = $("#acd-body");
  switch (acdState.tab) {
    case "doors":
    case "readers":
      await acdDrawSettings(body, acdState.tab === "doors" ? "door" : "reader");
      break;
    case "capabilities":
      await acdDrawCapabilities(body);
      break;
    case "maintenance":
      body.innerHTML = `<div class="acd-narrow"><div id="mt-section"><div class="muted" id="mt-section-body">Leyendo la hora del equipo…</div></div></div>`;
      maintFillDeviceSection("access", acdState.id);
      break;
    default:
      await acdDrawConnection(body);
  }
}

// ---------- Capacidades ----------
// La ficha se guarda al validar o revalidar el equipo: abrir la pestaña no le
// pregunta nada al equipo.

const ACD_CAP_STATE = {
  Supported: { icon: "✓", cls: "ok", title: "El equipo declara que lo soporta" },
  NotSupported: { icon: "✕", cls: "no", title: "El equipo declara que no lo soporta" },
  NotDeclared: { icon: "?", cls: "unk", title: "El equipo no lo declara: el VMS lo prueba al usarlo" },
};

async function acdDrawCapabilities(body) {
  body.innerHTML = `<div class="muted">Cargando las capacidades…</div>`;
  let data;
  try { data = await Api.get(`/api/access/devices/${acdState.id}/capabilities`); }
  catch (err) {
    if (acdState.tab === "capabilities") body.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }
  if (acdState.tab !== "capabilities") return;   // cambió de pestaña mientras cargaba
  const isAdmin = Api.role === "Admin";
  const p = data.profile;
  if (!p) {
    body.innerHTML = `<div class="acd-narrow"><div class="info-box">Todavía no se leyeron las capacidades de este
      equipo: se validó antes de que el VMS las leyera, o su marca no las informa.${isAdmin
        ? " Use <b>Revalidar</b> para leerlas ahora." : ""}</div></div>`;
    return;
  }
  const when = new Date(p.readAtUtc).toLocaleString("es-CL");
  body.innerHTML = `
    <div class="acd-narrow">
      <p class="muted res-tab-intro">Lo que el equipo declara saber hacer según sus propias capacidades
        (leídas el ${esc(when)}). El VMS lo usa para elegir cómo escribirle y para no pedirle lo que no tiene.${isAdmin
          ? " Para releerlo (por ejemplo, después de actualizar el firmware) use <b>Revalidar</b>." : ""}</p>
      ${p.notes.length ? `<div class="info-box">${p.notes.map(esc).join("<br>")}</div>` : ""}
      <div class="acd-cap-legend muted">${Object.values(ACD_CAP_STATE).map((s) =>
        `<span><span class="acd-cap-icon ${s.cls}">${s.icon}</span> ${esc(s.title)}</span>`).join("")}</div>
      ${p.groups.map((g) => `
        <section class="acd-card">
          <div class="acd-card-head"><h4>${esc(g.title)}</h4></div>
          <ul class="acd-cap-list">${g.items.map(acdCapItemHtml).join("")}</ul>
        </section>`).join("")}
    </div>`;
}

function acdCapItemHtml(item) {
  const s = ACD_CAP_STATE[item.state] ?? ACD_CAP_STATE.NotDeclared;
  return `
    <li>
      <span class="acd-cap-icon ${s.cls}" title="${esc(s.title)}">${s.icon}</span>
      <div class="acd-cap-text">
        <div>${esc(item.label)}</div>
        ${item.detail ? `<div class="muted acd-cap-detail">${esc(item.detail)}</div>` : ""}
        ${item.source ? `<div class="acd-cap-source">${esc(item.source)}</div>` : ""}
      </div>
    </li>`;
}

// ---------- Conexión ----------

async function acdDrawConnection(body) {
  const d = acdState.device;
  const isAdmin = Api.role === "Admin";
  let drivers, places;
  try { [drivers, places] = await Promise.all([getAccessDrivers(), loadLocationChoices()]); }
  catch (err) { body.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  if (acdState.tab !== "connection") return;   // cambió de pestaña mientras cargaba
  const driver0 = drivers.find((x) => x.key === d.driverKey) ?? drivers[0];

  body.innerHTML = `
    <form id="acd-form" class="acd-narrow">
      <fieldset ${isAdmin ? "" : "disabled"} class="acd-fieldset">
      <div id="ac-modal-error"></div>
      <div class="form-grid">
        <div class="field">
          <label>Nombre</label>
          <input id="ac-name" required maxlength="128" value="${esc(d.name)}" placeholder="Portería principal, Torniquete casino…">
        </div>
        ${locationFieldHtml("ac-location", places, d.locationId ?? null,
          "Sus puertas la heredan; las que ubique aparte en Recursos se quedan donde están.")}
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Marca / protocolo</label>
          <select id="ac-driver">
            ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${driver0?.key === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
          </select>
        </div>
        <div class="form-grid">
          <div class="field">
            <label>Dirección (IP o hostname)</label>
            <input id="ac-host" required value="${esc(d.host)}" placeholder="192.168.1.65">
          </div>
          <div class="field">
            <label id="ac-port-label">${accessPortLabel(driver0)}</label>
            <input id="ac-port" type="number" min="1" max="65535" required value="${d.port}">
          </div>
        </div>
      </div>
      <div id="ac-credentials">${accessCredentialFields(driver0, d, false)}</div>
      <label class="checkbox-row"><input type="checkbox" id="ac-enabled" ${d.enabled ? "checked" : ""}> Activo (sondeo de estado; sus puertas ocupan cupo de la licencia)</label>
      <div class="info-box">Si cambia la dirección, el puerto, el usuario o la contraseña, al guardar se vuelve a validar contra
        el equipo y se releen su modelo, firmware, capacidades y puertas.</div>
      <div id="ac-probe-result"></div>
      ${isAdmin ? `<div class="acd-actions">
        <button class="btn ghost" type="button" id="ac-probe">Probar conexión</button>
        <button class="btn" type="submit" id="ac-save">Guardar cambios</button>
      </div>` : ""}
      </fieldset>
    </form>
    ${acdDoorsHtml(d, isAdmin)}`;

  acdBindDoors();

  $("#ac-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#ac-driver").value);
    if (!dr) return;
    $("#ac-port-label").textContent = accessPortLabel(dr);
    $("#ac-credentials").innerHTML = accessCredentialFields(dr, d, false);
  });

  $("#ac-probe")?.addEventListener("click", async () => {
    const errorBox = $("#ac-modal-error");
    const resultBox = $("#ac-probe-result");
    errorBox.innerHTML = "";
    resultBox.innerHTML = `<div class="info-box">Conectando con el equipo…</div>`;
    const probeButton = $("#ac-probe");
    probeButton.disabled = true;
    try {
      const r = await Api.post(`/api/access/probe?deviceId=${d.id}`, accessReadConnectionForm());
      resultBox.innerHTML = r.success ? accessProbeResultHtml(r) : `<div class="error-box">${esc(r.error)}</div>`;
    } catch (err) {
      resultBox.innerHTML = "";
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      probeButton.disabled = false;
    }
  });

  $("#acd-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#ac-modal-error");
    errorBox.innerHTML = "";
    const saveButton = $("#ac-save");
    saveButton.disabled = true;
    saveButton.textContent = "Validando…";
    try {
      acdState.device = await Api.put(`/api/access/devices/${d.id}`, accessReadConnectionForm());
      acdState.settings = null;
      toast("Equipo actualizado.");
      acdDrawPage();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      saveButton.disabled = false;
      saveButton.textContent = "Guardar cambios";
    }
  });
}

// ---------- Puertas del equipo en el VMS (activa o pausada) ----------
// Pausar una puerta es del VMS, no del equipo: deja de ocupar cupo de la
// licencia y se la saca de los niveles de acceso en los equipos. El nombre se
// cambia en su ficha de Recursos, que es donde se administra cada recurso.

function acdDoorsHtml(d, isAdmin) {
  if (!d.doors.length) return "";
  return `
    <section class="acd-card acd-narrow" id="acd-doors">
      <div class="acd-card-head"><h4>Puertas del equipo</h4>
        <span class="muted">Activa: el VMS la ofrece y da permisos en ella. Pausada: deja de ocupar cupo de la licencia
          y se la quita de los niveles de acceso en los equipos.</span></div>
      <div id="acd-doors-error"></div>
      <ul class="acd-door-list">
        ${d.doors.map((door) => `
          <li data-id="${door.id}">
            <span class="acd-door-name"><span class="muted">${door.number}.</span> ${esc(door.name)}
              ${door.enabled ? "" : `<span class="tag operator">pausada</span>`}</span>
            <a class="btn ghost small" href="#/resources?r=Door:${door.id}"
               title="Abrir su ficha en Recursos (nombre, ubicación, consignas y cámaras)">Ficha en Recursos</a>
            <label class="checkbox-row acd-door-toggle"><input type="checkbox" class="acd-door-enabled"
              ${door.enabled ? "checked" : ""} ${isAdmin ? "" : "disabled"}> Activa</label>
          </li>`).join("")}
      </ul>
    </section>`;
}

function acdBindDoors() {
  $$("#acd-doors .acd-door-enabled").forEach((box) => box.addEventListener("change", async () => {
    const id = Number(box.closest("li").dataset.id);
    const door = acdState.device.doors.find((x) => x.id === id);
    if (!door) return;
    const enabled = box.checked;
    if (!enabled && !confirm(`¿Pausar la puerta "${door.name}"? Se la quita de los niveles de acceso en los equipos.`)) {
      box.checked = true;
      return;
    }
    box.disabled = true;
    $("#acd-doors-error").innerHTML = "";
    try {
      const updated = await Api.put(`/api/access/doors/${id}`, { name: door.name, enabled });
      Object.assign(door, { enabled: updated.enabled, name: updated.name });
      toast(enabled ? `Puerta "${door.name}" activada.` : `Puerta "${door.name}" pausada.`);
      acdDrawPage();
    } catch (err) {
      box.checked = !enabled;
      box.disabled = false;
      $("#acd-doors-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  }));
}

// ---------- Puertas y lectores (configuración propia del equipo) ----------

async function acdLoadSettings() {
  if (acdState.settings || acdState.loadingSettings) return;
  acdState.loadingSettings = true;
  acdState.settingsError = null;
  try { acdState.settings = await Api.get(`/api/access/devices/${acdState.id}/settings`); }
  catch (err) { acdState.settingsError = err; }
  finally { acdState.loadingSettings = false; }
}

async function acdDrawSettings(body, kind) {
  const state = acdState;
  if (Api.role !== "Admin") {
    body.innerHTML = `<div class="info-box">Solo un administrador puede ver y cambiar la configuración propia del equipo
      (parámetros de sus puertas y de sus lectores).</div>`;
    return;
  }
  if (!state.settings) {
    body.innerHTML = `<div class="muted">Leyendo la configuración del equipo… (puede tardar unos segundos)</div>`;
    await acdLoadSettings();
    if (acdState !== state || !["doors", "readers"].includes(state.tab)) return;
  }
  if (state.settingsError) {
    const err = state.settingsError;
    body.innerHTML = `
      <div class="error-box">${esc(err.error)}</div>
      ${err.status === 501 ? "" : `<button class="btn ghost small" type="button" id="acd-retry">Volver a intentar</button>`}`;
    $("#acd-retry")?.addEventListener("click", () => { state.settingsError = null; acdDrawTab(); });
    return;
  }
  const sections = state.settings.sections.filter((s) => s.kind === kind);
  const intro = kind === "door"
    ? "Parámetros de cada puerta tal como están en el equipo: se leen al abrir esta pestaña y se escriben al guardar. Son los mismos que muestra la página web del equipo o HikCentral."
    : "Parámetros de cada lector del equipo. Un terminal trae su propio lector (rostro, tarjeta, huella según el modelo) y una entrada para un lector externo; una controladora, los lectores cableados a ella.";
  body.innerHTML = `
    <p class="muted res-tab-intro">${intro}${Api.role === "Admin" ? "" : " Solo un administrador puede cambiarlos."}</p>
    ${state.settings.notes.length ? `<div class="info-box">${state.settings.notes.map(esc).join("<br>")}</div>` : ""}
    ${sections.length ? sections.map(acdSectionHtml).join("")
      : `<div class="info-box">El equipo no informó ${kind === "door" ? "puertas" : "lectores"} configurables.</div>`}`;
  sections.forEach((s) => acdBindSection(s));
}

function acdSectionHtml(s) {
  const isAdmin = Api.role === "Admin";
  return `
    <section class="acd-card" data-section="${esc(s.key)}">
      <div class="acd-card-head">
        <h4>${esc(s.title)}</h4>
        ${s.subtitle ? `<div class="muted">${esc(s.subtitle)}</div>` : ""}
      </div>
      <form class="acd-settings" autocomplete="off">
        <fieldset ${isAdmin ? "" : "disabled"} class="acd-fieldset">
          <div class="acd-error"></div>
          ${s.settings.length ? `<div class="acd-grid">${s.settings.map(acdFieldHtml).join("")}</div>`
            : `<div class="muted">El equipo no entrega parámetros editables de este bloque.</div>`}
          ${s.others.length ? `
          <details class="acd-others">
            <summary>Otros parámetros que informa el equipo (${s.others.length})</summary>
            <dl class="res-dl">${s.others.map((o) => `<dt>${esc(o.key)}</dt><dd>${esc(o.value ?? "—")}</dd>`).join("")}</dl>
          </details>` : ""}
          ${isAdmin && s.settings.some((f) => f.type !== "Info") ? `
          <div class="acd-actions">
            <span class="muted acd-dirty hidden">Hay cambios sin guardar.</span>
            <button class="btn" type="submit" disabled>Guardar</button>
          </div>` : ""}
        </fieldset>
      </form>
    </section>`;
}

function acdFieldHtml(f) {
  const key = esc(f.key);
  const help = f.help ? `<div class="muted field-hint">${esc(f.help)}</div>` : "";
  switch (f.type) {
    case "Boolean":
      return `
        <div class="field acd-field">
          <label class="checkbox-row acd-check"><input type="checkbox" data-key="${key}" data-type="Boolean" ${f.value === "true" ? "checked" : ""}> ${esc(f.label)}</label>
          ${help}
        </div>`;
    case "Integer": {
      const range = f.min != null || f.max != null ? ` <span class="muted">(${f.min ?? "…"}–${f.max ?? "…"})</span>` : "";
      return `
        <div class="field acd-field">
          <label>${esc(f.label)}${range}</label>
          <div class="acd-num">
            <input type="number" data-key="${key}" data-type="Integer" value="${esc(f.value ?? "")}"
                   ${f.min != null ? `min="${f.min}"` : ""} ${f.max != null ? `max="${f.max}"` : ""} step="1">
            ${f.unit ? `<span class="acd-unit">${esc(f.unit)}</span>` : ""}
          </div>
          ${help}
        </div>`;
    }
    case "Choice":
      return `
        <div class="field acd-field">
          <label>${esc(f.label)}</label>
          <select data-key="${key}" data-type="Choice">
            ${(f.options ?? []).map((o) => `<option value="${esc(o.value)}" ${o.value === f.value ? "selected" : ""}>${esc(o.label)}</option>`).join("")}
          </select>
          ${help}
        </div>`;
    case "Password":
      return `
        <div class="field acd-field">
          <label>${esc(f.label)}</label>
          <input type="password" data-key="${key}" data-type="Password" autocomplete="new-password" placeholder="vacío = no cambiar">
          ${help}
        </div>`;
    case "Info":
      return `
        <div class="field acd-field">
          <label>${esc(f.label)}</label>
          <div class="acd-ro">${esc(f.value ?? "—")}</div>
          ${help}
        </div>`;
    default:
      return `
        <div class="field acd-field">
          <label>${esc(f.label)}</label>
          <input type="text" data-key="${key}" data-type="Text" maxlength="64" value="${esc(f.value ?? "")}">
          ${help}
        </div>`;
  }
}

/** Valor actual de un campo del formulario, como texto comparable con el que mandó el equipo. */
function acdFieldValue(input) {
  switch (input.dataset.type) {
    case "Boolean": return input.checked ? "true" : "false";
    case "Password": return input.value;          // vacío = sin cambio
    default: return input.value.trim();
  }
}

function acdBindSection(s) {
  const card = $(`#view .acd-card[data-section="${CSS.escape(s.key)}"]`);
  if (!card) return;
  const form = card.querySelector("form");
  const inputs = Array.from(form.querySelectorAll("[data-key]"));
  const original = Object.fromEntries(s.settings.map((f) => [f.key, f.type === "Password" ? "" : (f.value ?? "")]));
  const changed = () => inputs.filter((i) => acdFieldValue(i) !== original[i.dataset.key]);
  const refresh = () => {
    const dirty = changed().length > 0;
    form.querySelector(".acd-dirty")?.classList.toggle("hidden", !dirty);
    const save = form.querySelector("button[type=submit]");
    if (save) save.disabled = !dirty;
  };
  form.addEventListener("input", refresh);
  form.addEventListener("change", refresh);
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = form.querySelector(".acd-error");
    errorBox.innerHTML = "";
    const diff = changed();
    if (!diff.length) return;
    for (const input of diff) {
      if (input.dataset.type === "Integer" && !input.checkValidity()) {
        input.reportValidity();
        return;
      }
    }
    const values = Object.fromEntries(diff.map((i) => [i.dataset.key, acdFieldValue(i)]));
    const save = form.querySelector("button[type=submit]");
    save.disabled = true;
    save.textContent = "Guardando…";
    try {
      const fresh = await Api.put(`/api/access/devices/${acdState.id}/settings/${encodeURIComponent(s.key)}`, { values });
      // Se redibuja el bloque con lo que el equipo DEJÓ (puede acotar o redondear).
      const index = acdState.settings.sections.findIndex((x) => x.key === s.key);
      if (index >= 0) acdState.settings.sections[index] = fresh;
      card.outerHTML = acdSectionHtml(fresh);
      acdBindSection(fresh);
      toast(`${fresh.title}: cambios guardados en el equipo.`);
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
      save.textContent = "Guardar";
    }
  });
}
