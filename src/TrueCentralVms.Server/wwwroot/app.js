// CLR TrueCentral VMS — panel de administración (SPA sin framework).
"use strict";

const $ = (sel, root) => (root || document).querySelector(sel);
const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------------------------------------------------------------------------
// Política de contraseñas (réplica de Core\Auth\PasswordPolicy.cs; el servidor
// es la fuente de verdad y revalida siempre)
// ---------------------------------------------------------------------------
const PASSWORD_RULES = [
  { id: "len",     text: "Al menos 8 caracteres",              test: (p) => p.length >= 8 },
  { id: "upper",   text: "Una letra mayúscula",                test: (p) => /[A-ZÁÉÍÓÚÑÜ]/.test(p) },
  { id: "lower",   text: "Una letra minúscula",                test: (p) => /[a-záéíóúñü]/.test(p) },
  { id: "digit",   text: "Un número",                          test: (p) => /[0-9]/.test(p) },
  { id: "special", text: "Un carácter especial (. , ! $ % #)", test: (p) => /[^A-Za-z0-9ÁÉÍÓÚÑÜáéíóúñü]/.test(p) },
];

function policyListHtml(id) {
  return `<ul class="policy-list" id="${id}">` +
    PASSWORD_RULES.map((r) => `<li data-rule="${r.id}">${esc(r.text)}</li>`).join("") +
    "</ul>";
}

function bindPolicyList(input, listId) {
  const update = () => {
    const value = input.value || "";
    $$(`#${listId} li`).forEach((li) => {
      const rule = PASSWORD_RULES.find((r) => r.id === li.dataset.rule);
      li.classList.toggle("ok", rule.test(value));
    });
  };
  input.addEventListener("input", update);
  update();
}

function passwordOk(p) { return PASSWORD_RULES.every((r) => r.test(p || "")); }

// ---------------------------------------------------------------------------
// Utilitarios de interfaz
// ---------------------------------------------------------------------------
let toastTimer = null;
function toast(message, isError) {
  const el = $("#toast");
  el.textContent = message;
  el.classList.toggle("error", !!isError);
  el.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add("hidden"), 3500);
}

function openModal(html, wide) {
  // `wide` lo usan los formularios con grilla (editor de muros).
  $("#modal").className = "modal" + (wide ? " wide" : "");
  $("#modal").innerHTML = html;
  $("#modal-backdrop").classList.remove("hidden");
}
function closeModal() { $("#modal-backdrop").classList.add("hidden"); }
$("#modal-backdrop")?.addEventListener("click", (e) => {
  if (e.target.id === "modal-backdrop") closeModal();
});

function formatDate(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  return d.toLocaleDateString("es-CL") + " " + d.toLocaleTimeString("es-CL", { hour: "2-digit", minute: "2-digit" });
}

// ---------------------------------------------------------------------------
// Pantallas de acceso
// ---------------------------------------------------------------------------
function showAuthScreen() {
  $("#app-shell").classList.add("hidden");
  $("#auth-screen").classList.remove("hidden");
}
function showAppShell() {
  $("#auth-screen").classList.add("hidden");
  $("#app-shell").classList.remove("hidden");
  $("#current-user").textContent = `${Api.username} (${Api.role === "Admin" ? "Administrador" : "Operador"})`;
}

function renderSetup(status) {
  showAuthScreen();
  $("#auth-subtitle").textContent = "Configuración inicial del servidor";
  const body = $("#auth-body");

  if (!status.isLocalRequest) {
    body.innerHTML = `
      <div class="warn-box">
        El servidor aún no está inicializado. Por seguridad, la creación del
        primer administrador solo puede realizarse <b>desde la propia máquina del
        servidor</b>: abra <code>http://localhost:5090</code> en ese equipo.
      </div>`;
    return;
  }

  body.innerHTML = `
    <div class="info-box">
      Bienvenido. El sistema viene desactivado de fábrica: cree la cuenta del
      primer <b>administrador</b> para comenzar.
    </div>
    <div id="setup-error"></div>
    <form id="setup-form">
      <div class="field">
        <label>Nombre de usuario</label>
        <input id="setup-username" autocomplete="username" required minlength="3" maxlength="64">
      </div>
      <div class="field">
        <label>Contraseña</label>
        <input id="setup-password" type="password" autocomplete="new-password" required>
      </div>
      ${policyListHtml("setup-policy")}
      <div class="field">
        <label>Confirmar contraseña</label>
        <input id="setup-password2" type="password" autocomplete="new-password" required>
      </div>
      <button class="btn block" type="submit">Crear administrador</button>
    </form>`;

  bindPolicyList($("#setup-password"), "setup-policy");
  $("#setup-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#setup-error");
    errorBox.innerHTML = "";
    const password = $("#setup-password").value;
    if (!passwordOk(password)) {
      errorBox.innerHTML = `<div class="error-box">La contraseña no cumple la política de seguridad.</div>`;
      return;
    }
    if (password !== $("#setup-password2").value) {
      errorBox.innerHTML = `<div class="error-box">Las contraseñas no coinciden.</div>`;
      return;
    }
    try {
      const login = await Api.post("/api/setup/admin", {
        username: $("#setup-username").value.trim(),
        password,
      });
      Api.saveSession(login);
      toast("Administrador creado. ¡Bienvenido!");
      enterApp();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

function renderLogin(message) {
  showAuthScreen();
  $("#auth-subtitle").textContent = "Gestión de video multimarca";
  $("#auth-body").innerHTML = `
    <div id="login-error">${message ? `<div class="info-box">${esc(message)}</div>` : ""}</div>
    <form id="login-form">
      <div class="field">
        <label>Usuario</label>
        <input id="login-username" autocomplete="username" required>
      </div>
      <div class="field">
        <label>Contraseña</label>
        <input id="login-password" type="password" autocomplete="current-password" required>
      </div>
      <button class="btn block" type="submit">Ingresar</button>
    </form>`;

  $("#login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const username = $("#login-username").value.trim();
    try {
      const login = await Api.post("/api/auth/login", {
        username,
        password: $("#login-password").value,
      });
      Api.saveSession(login);
      enterApp();
    } catch (err) {
      if (err.data && err.data.setupRequired) { init(); return; }
      if (err.data && err.data.mustChangePassword) { renderChangePassword(username); return; }
      $("#login-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

function renderChangePassword(username) {
  showAuthScreen();
  $("#auth-subtitle").textContent = "Renovación de contraseña";
  $("#auth-body").innerHTML = `
    <div class="warn-box">
      La contraseña de <b>${esc(username)}</b> está vencida: defina una nueva
      para continuar. No se puede reutilizar ninguna contraseña anterior.
    </div>
    <div id="change-error"></div>
    <form id="change-form">
      <div class="field">
        <label>Contraseña actual</label>
        <input id="change-current" type="password" autocomplete="current-password" required>
      </div>
      <div class="field">
        <label>Contraseña nueva</label>
        <input id="change-new" type="password" autocomplete="new-password" required>
      </div>
      ${policyListHtml("change-policy")}
      <div class="field">
        <label>Confirmar contraseña nueva</label>
        <input id="change-new2" type="password" autocomplete="new-password" required>
      </div>
      <button class="btn block" type="submit">Cambiar contraseña</button>
      <button class="btn ghost block" type="button" id="change-back" style="margin-top:10px">Volver al login</button>
    </form>`;

  bindPolicyList($("#change-new"), "change-policy");
  $("#change-back").addEventListener("click", () => renderLogin());
  $("#change-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#change-error");
    errorBox.innerHTML = "";
    const newPassword = $("#change-new").value;
    if (!passwordOk(newPassword)) {
      errorBox.innerHTML = `<div class="error-box">La contraseña nueva no cumple la política de seguridad.</div>`;
      return;
    }
    if (newPassword !== $("#change-new2").value) {
      errorBox.innerHTML = `<div class="error-box">Las contraseñas no coinciden.</div>`;
      return;
    }
    try {
      const login = await Api.post("/api/auth/change-password", {
        username,
        currentPassword: $("#change-current").value,
        newPassword,
      });
      Api.saveSession(login);
      toast("Contraseña actualizada.");
      enterApp();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

// ---------------------------------------------------------------------------
// Vistas de la aplicación
// ---------------------------------------------------------------------------
async function renderDashboard() {
  $("#page-title").textContent = "Panel";
  let health = null;
  try { health = await Api.get("/api/health"); } catch { /* la sonda periódica marca el estado */ }
  let devices = [];
  try { devices = await Api.get("/api/devices"); } catch { /* sin sesión aún */ }
  let usersCount = "—";
  let sessionsCount = "—";
  if (Api.role === "Admin") {
    try { usersCount = (await Api.get("/api/users")).length; } catch { /* sin permiso */ }
    try { sessionsCount = (await Api.get("/api/streams/active")).length; } catch { /* sin permiso */ }
  }
  const online = devices.filter((d) => d.status === "Online").length;
  $("#view").innerHTML = `
    <div class="cards">
      <div class="card">
        <div class="card-label">Estado del servidor</div>
        <div class="card-value">${health ? "En línea" : "Sin conexión"}</div>
      </div>
      <div class="card">
        <div class="card-label">Versión</div>
        <div class="card-value">${esc(health?.version ?? "—")}</div>
      </div>
      <div class="card">
        <div class="card-label">Dispositivos</div>
        <div class="card-value">${devices.length}<span class="muted" style="font-size:13px"> (${online} en línea)</span></div>
      </div>
      ${Api.role === "Admin" ? `
      <div class="card">
        <div class="card-label">Sesiones de video activas</div>
        <div class="card-value">${esc(sessionsCount)}</div>
      </div>` : ""}
      <div class="card">
        <div class="card-label">Usuarios</div>
        <div class="card-value">${esc(usersCount)}</div>
      </div>
      <div class="card">
        <div class="card-label">Hora del servidor (UTC)</div>
        <div class="card-value small">${health ? formatDate(health.time) : "—"}</div>
      </div>
    </div>`;
}

// ---------------------------------------------------------------------------
// Sesiones de streaming activas (quién ve qué) + expulsión
// ---------------------------------------------------------------------------
let sessionsTimer = null;

function sessionDuration(startedAt) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000));
  const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), s = seconds % 60;
  return (h ? `${h} h ` : "") + (h || m ? `${m} min ` : "") + `${s} s`;
}

async function renderSessions() {
  $("#page-title").textContent = "Sesiones";
  if (Api.role !== "Admin") {
    $("#view").innerHTML = `<div class="warn-box">Requiere rol administrador.</div>`;
    return;
  }

  const load = async () => {
    let sessions;
    try { sessions = await Api.get("/api/streams/active"); }
    catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

    $("#view").innerHTML = `
      <div class="toolbar">
        <h3>Sesiones de video activas <span class="muted" style="font-weight:normal;font-size:12px">(se actualiza cada 5 s)</span></h3>
      </div>
      ${sessions.length === 0 ? `<div class="info-box">Nadie está viendo video en este momento.</div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Usuario</th><th>Dispositivo</th><th>Canal</th><th>Perfil</th>
          <th>IP del espectador</th><th>Inicio</th><th>Duración</th><th></th>
        </tr></thead>
        <tbody>
          ${sessions.map((s) => `
            <tr data-id="${s.id}">
              <td>${esc(s.username)}</td>
              <td>${esc(s.deviceName)}</td>
              <td>${s.rtspChannel}</td>
              <td><span class="tag ${s.profile === "main" ? "admin" : "operator"}">${s.profile === "main" ? "Principal" : "Secundario"}</span></td>
              <td class="muted">${esc(s.clientIp)}</td>
              <td class="muted">${formatDate(s.startedAt)}</td>
              <td class="muted">${sessionDuration(s.startedAt)}</td>
              <td class="row-actions">
                <button class="btn danger btn-kick" title="Cortar esta sesión de video (el usuario puede volver a conectarse)">Expulsar</button>
              </td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}`;

    $$("#view .btn-kick").forEach((b) => b.addEventListener("click", async (e) => {
      const row = e.target.closest("tr");
      const id = Number(row.dataset.id);
      const s = sessions.find((x) => x.id === id);
      if (!confirm(`¿Cortar la sesión de "${s.username}" sobre ${s.deviceName} canal ${s.rtspChannel}?`)) return;
      e.target.disabled = true;
      try {
        await Api.post(`/api/streams/${id}/kick`);
        toast("Sesión expulsada.");
        load();
      } catch (err) {
        toast(err.error, true);
        e.target.disabled = false;
      }
    }));
  };

  await load();
  clearInterval(sessionsTimer);
  sessionsTimer = setInterval(() => {
    // Sigue sondeando solo mientras la página esté visible y activa.
    if (location.hash !== "#/sessions" || $("#app-shell").classList.contains("hidden")) {
      clearInterval(sessionsTimer);
      return;
    }
    load();
  }, 5000);
}

// ---------------------------------------------------------------------------
// Dispositivos
// ---------------------------------------------------------------------------
const DEVICE_TYPES = [
  { value: "Camera", label: "Cámara" },
  { value: "Dvr", label: "DVR" },
  { value: "Nvr", label: "NVR" },
  { value: "Xvr", label: "XVR" },
];
const typeLabel = (v) => (DEVICE_TYPES.find((t) => t.value === v) || { label: v }).label;

function statusTag(status) {
  switch (status) {
    case "Online": return `<span class="tag on">En línea</span>`;
    case "Offline": return `<span class="tag off">Sin conexión</span>`;
    case "AuthFailed": return `<span class="tag off">Credenciales</span>`;
    default: return `<span class="tag operator">—</span>`;
  }
}

let driversCache = null;
async function getDrivers() {
  driversCache ??= await Api.get("/api/drivers");
  return driversCache;
}

let lastScan = null; // resultados del último sondeo (persisten al re-renderizar)

async function renderDevices() {
  $("#page-title").textContent = "Dispositivos";
  let devices;
  try { devices = await Api.get("/api/devices"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Dispositivos administrados</h3>
      ${isAdmin ? `<button class="btn" id="btn-device-new">Agregar dispositivo</button>` : ""}
    </div>
    ${devices.length === 0 ? `
      <div class="info-box">
        Aún no hay dispositivos. ${isAdmin
          ? "Use <b>Agregar dispositivo</b> o el descubrimiento de red de más abajo: al guardar se validan las credenciales contra el equipo y se obtienen modelo, número de serie, firmware y canales."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Tipo</th><th>Marca</th><th>Dirección</th><th>Modelo</th>
          <th>N° serie</th><th>Firmware</th><th>Canales</th><th>Estado</th><th></th>
        </tr></thead>
        <tbody>
          ${devices.map((d) => `
            <tr data-id="${d.id}">
              <td>${esc(d.name)}</td>
              <td>${esc(typeLabel(d.deviceType))}</td>
              <td class="muted">${esc(d.driverKey)}</td>
              <td class="muted">${esc(d.host)}:${d.sdkPort}</td>
              <td>${esc(d.model ?? "—")}</td>
              <td class="muted">${esc(d.serialNumber ?? "—")}</td>
              <td class="muted">${esc(d.firmwareVersion ?? "—")}</td>
              <td>${d.channelCount}</td>
              <td>${statusTag(d.status)}</td>
              <td class="row-actions">
                <button class="btn ghost btn-channels">Canales</button>
                ${isAdmin ? `<button class="btn ghost btn-revalidate" title="Volver a sondear el equipo (modelo, firmware, canales, puerto RTSP)">Revalidar</button>
                <button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}
    ${isAdmin ? `
    <div class="toolbar" style="margin-top:28px">
      <h3>Equipos en línea <span class="muted" style="font-weight:normal;font-size:12px">(SADP · Dahua · ONVIF en la red local)</span></h3>
      <button class="btn ghost" id="btn-device-scan">Buscar</button>
    </div>
    <div id="online-devices">
      ${lastScan ? "" : `<div class="info-box">Use <b>Buscar</b> para sondear el segmento de red del servidor
        (SADP para Hikvision, DHDiscover para Dahua y WS-Discovery para cualquier marca ONVIF).
        Solo se listan equipos de video (cámaras, DVR, NVR, decodificadores); los controles de acceso y alarmas se omiten.</div>`}
    </div>` : ""}`;

  $("#btn-device-new")?.addEventListener("click", () => deviceModal(null));
  $("#btn-device-scan")?.addEventListener("click", () => runDiscovery(devices));
  if (lastScan) renderOnlineDevices(devices);
  $$("#view .btn-revalidate").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    e.target.disabled = true;
    e.target.textContent = "Sondeando…";
    try {
      await Api.post(`/api/devices/${id}/revalidate`);
      toast("Equipo revalidado: información y canales actualizados.");
      renderDevices();
    } catch (err) {
      toast(err.error, true);
      e.target.disabled = false;
      e.target.textContent = "Revalidar";
    }
  }));
  $$("#view .btn-edit").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    deviceModal(devices.find((d) => d.id === id));
  }));
  $$("#view .btn-channels").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    channelsModal(devices.find((d) => d.id === id));
  }));
  $$("#view .btn-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const device = devices.find((d) => d.id === id);
    if (!confirm(`¿Eliminar el dispositivo "${device.name}" y todos sus canales?`)) return;
    try {
      await Api.delete(`/api/devices/${id}`);
      toast("Dispositivo eliminado.");
      renderDevices();
    } catch (err) { toast(err.error, true); }
  }));
}

async function runDiscovery(devices) {
  const box = $("#online-devices");
  const scanButton = $("#btn-device-scan");
  scanButton.disabled = true;
  scanButton.textContent = "Buscando…";
  box.innerHTML = `<div class="info-box">Sondeando la red… (unos segundos)</div>`;
  try {
    lastScan = await Api.get("/api/discovery/scan");
    renderOnlineDevices(devices);
  } catch (err) {
    box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
  } finally {
    scanButton.disabled = false;
    scanButton.textContent = "Buscar";
  }
}

/// Tabla de equipos descubiertos, al estilo "Online Device" de HikCentral.
function renderOnlineDevices(devices) {
  const box = $("#online-devices");
  if (!lastScan || !lastScan.length) {
    box.innerHTML = `<div class="warn-box">No se encontraron equipos de video en este segmento de red.</div>`;
    return;
  }
  const knownHosts = new Set(devices.map((d) => d.host));
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>IP</th><th>Marca</th><th>Tipo</th><th>Modelo</th><th>N° serie</th><th>Puerto SDK</th>
        <th>MAC</th><th>Estado</th><th></th>
      </tr></thead>
      <tbody>${lastScan.map((d, i) => {
        const added = knownHosts.has(d.ip);
        return `
        <tr>
          <td>${esc(d.ip)}</td>
          <td>${esc(d.brand)}</td>
          <td>${esc(d.category)}</td>
          <td>${esc(d.model)}</td>
          <td class="muted">${esc(d.serial)}</td>
          <td class="muted">${d.commandPort}</td>
          <td class="muted">${esc(d.mac)}</td>
          <td>${added ? `<span class="tag on">Agregado</span>`
                : d.activated === false ? `<span class="tag off">Sin activar</span>`
                : `<span class="tag operator">Nuevo</span>`}</td>
          <td class="row-actions">
            <button class="btn ghost scan-use" data-i="${i}" ${added ? "disabled" : ""}>Agregar</button>
          </td>
        </tr>`;
      }).join("")}
      </tbody>
    </table></div>`;
  $$(".scan-use").forEach((b) => b.addEventListener("click", () => {
    const d = lastScan[Number(b.dataset.i)];
    deviceModal(null, {
      name: d.model || d.ip,
      host: d.ip,
      sdkPort: d.commandPort || 8000,
      driverKey: d.driverKey || "hikvision-netsdk",
    });
  }));
}

async function deviceModal(device, prefill) {
  const isNew = !device;
  const seed = isNew ? (prefill || {}) : {};
  const drivers = await getDrivers();
  openModal(`
    <h3>${isNew ? "Agregar dispositivo" : "Editar dispositivo"}</h3>
    <div id="dev-modal-error"></div>
    <form id="device-form">
      <div class="field">
        <label>Nombre</label>
        <input id="df-name" required maxlength="128" value="${esc(device?.name ?? seed.name ?? "")}" placeholder="NVR Bodega, Cámara acceso...">
      </div>
      <div class="field">
        <label>Marca / protocolo</label>
        <select id="df-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${(device?.driverKey ?? seed.driverKey) === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección (IP o hostname)</label>
          <input id="df-host" required value="${esc(device?.host ?? seed.host ?? "")}" placeholder="192.168.1.64">
        </div>
        <div class="field">
          <label>Puerto SDK</label>
          <input id="df-sdkport" type="number" min="1" max="65535" required value="${device?.sdkPort ?? seed.sdkPort ?? drivers[0]?.defaultSdkPort ?? 8000}">
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Puerto RTSP</label>
          <input id="df-rtspport" type="number" min="1" max="65535" required value="${device?.rtspPort ?? drivers[0]?.defaultRtspPort ?? 554}">
        </div>
        <div class="field">
          <label>Usuario del equipo</label>
          <input id="df-username" required value="${esc(device?.username ?? "")}" placeholder="admin">
        </div>
      </div>
      <div class="field">
        <label>${isNew ? "Contraseña del equipo" : "Contraseña (vacío = no cambiar)"}</label>
        <input id="df-password" type="password" autocomplete="new-password" ${isNew ? "required" : ""}>
      </div>
      <div id="probe-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="df-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="df-probe">Probar conexión</button>
        <button class="btn" type="submit" id="df-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);

  $("#df-cancel").addEventListener("click", closeModal);
  $("#df-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#df-driver").value);
    if (dr && isNew) { $("#df-sdkport").value = dr.defaultSdkPort; $("#df-rtspport").value = dr.defaultRtspPort; }
  });

  const readForm = () => ({
    name: $("#df-name").value.trim() || "(sin nombre)",
    driverKey: $("#df-driver").value,
    host: $("#df-host").value.trim(),
    sdkPort: Number($("#df-sdkport").value),
    rtspPort: Number($("#df-rtspport").value),
    username: $("#df-username").value.trim(),
    password: $("#df-password").value || null,
  });

  $("#df-probe").addEventListener("click", async () => {
    const errorBox = $("#dev-modal-error");
    const resultBox = $("#probe-result");
    errorBox.innerHTML = "";
    resultBox.innerHTML = `<div class="info-box">Conectando con el dispositivo…</div>`;
    const probeButton = $("#df-probe");
    probeButton.disabled = true;
    try {
      const query = !isNew ? `?deviceId=${device.id}` : "";
      const r = await Api.post(`/api/devices/probe${query}`, readForm());
      if (!r.success) {
        resultBox.innerHTML = `<div class="error-box">${esc(r.error)}</div>`;
        return;
      }
      if (r.detectedRtspPort) $("#df-rtspport").value = r.detectedRtspPort;
      resultBox.innerHTML = `
        <div class="probe-box">
          <div class="probe-title">✔ Conexión validada</div>
          <div class="probe-grid">
            <span>Tipo detectado</span><b>${esc(r.suggestedType ? typeLabel(r.suggestedType) : "—")}</b>
            <span>Modelo</span><b>${esc(r.model ?? "—")}</b>
            <span>N° de serie</span><b>${esc(r.serialNumber ?? "—")}</b>
            <span>Firmware</span><b>${esc(r.firmwareVersion ?? "—")}</b>
            <span>Canales</span><b>${r.analogChannelCount} analógicos, ${r.ipChannelCount} IP</b>
            <span>Puerto RTSP</span><b>${r.detectedRtspPort ? r.detectedRtspPort + " (detectado por SDK)" : "no reportado"}</b>
          </div>
          ${r.channels.length ? `<div class="probe-channels">${r.channels.map((c) =>
            `<span class="tag ${c.isOnline ? "on" : "operator"}" title="Canal ${c.channelNumber}">${esc(c.name)}</span>`).join(" ")}</div>` : ""}
        </div>`;
    } catch (err) {
      resultBox.innerHTML = "";
      $("#dev-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      probeButton.disabled = false;
    }
  });

  $("#device-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#dev-modal-error");
    errorBox.innerHTML = "";
    const saveButton = $("#df-save");
    saveButton.disabled = true;
    saveButton.textContent = "Validando…";
    try {
      const body = readForm();
      if (isNew) await Api.post("/api/devices", body);
      else await Api.put(`/api/devices/${device.id}`, body);
      closeModal();
      toast(isNew ? "Dispositivo agregado y validado." : "Dispositivo actualizado.");
      renderDevices();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}

async function channelsModal(device) {
  let channels;
  try { channels = await Api.get(`/api/devices/${device.id}/channels`); }
  catch (err) { toast(err.error, true); return; }

  const isAdmin = Api.role === "Admin";
  openModal(`
    <h3>Canales — ${esc(device.name)}</h3>
    <div id="ch-modal-error"></div>
    ${channels.length === 0 ? `<div class="info-box">El equipo no reportó canales.</div>` : `
      <div class="channel-list">
        ${channels.map((c) => `
          <div class="channel-row" data-id="${c.id}">
            <img class="channel-thumb" loading="lazy" alt=""
                 src="/api/devices/${device.id}/snapshot/${c.channelNumber}?access_token=${encodeURIComponent(Api.token)}"
                 onerror="this.classList.add('hidden')">
            <div class="channel-info">
              <input class="ch-name" value="${esc(c.name)}" maxlength="128" ${isAdmin ? "" : "disabled"}>
              <div class="muted" style="font-size:11.5px">Canal ${c.channelNumber} · RTSP ${c.rtspChannel} ·
                ${c.isOnline ? '<span class="tag on">Con señal</span>' : '<span class="tag operator">Sin señal</span>'}</div>
            </div>
            <label class="checkbox-row" style="margin:0" title="Visible para los operadores">
              <input type="checkbox" class="ch-enabled" ${c.enabled ? "checked" : ""} ${isAdmin ? "" : "disabled"}> Habilitado
            </label>
            <label class="checkbox-row" style="margin:0"
                   title="Mostrar control PTZ. Se detecta automático; márquelo a mano para domos que el equipo no reporta (ej. conectados por ONVIF al DVR).">
              <input type="checkbox" class="ch-ptz" ${c.supportsPtz ? "checked" : ""} ${isAdmin ? "" : "disabled"}> PTZ
            </label>
            <label class="checkbox-row" style="margin:0"
                   title="Para cámaras que anuncian mal su audio (SDP inválido que el servidor de media rechaza): el video pasa por un relé FFmpeg y se ve SIN audio. Márquelo si el canal da error de reproducción pese a estar en línea.">
              <input type="checkbox" class="ch-proxy" ${c.useFfmpegProxy ? "checked" : ""} ${isAdmin ? "" : "disabled"}> Proxy
            </label>
            ${isAdmin ? `<button class="btn ghost ch-save">Guardar</button>` : ""}
          </div>`).join("")}
      </div>`}
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="ch-close">Cerrar</button>
    </div>`);

  $("#ch-close").addEventListener("click", closeModal);
  $$(".ch-save").forEach((b) => b.addEventListener("click", async (e) => {
    const row = e.target.closest(".channel-row");
    const channelId = Number(row.dataset.id);
    try {
      await Api.put(`/api/devices/${device.id}/channels/${channelId}`, {
        name: row.querySelector(".ch-name").value.trim(),
        enabled: row.querySelector(".ch-enabled").checked,
        supportsPtz: row.querySelector(".ch-ptz").checked,
        useFfmpegProxy: row.querySelector(".ch-proxy").checked,
      });
      toast("Canal actualizado.");
    } catch (err) {
      $("#ch-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  }));
}

async function renderUsers() {
  $("#page-title").textContent = "Usuarios";
  if (Api.role !== "Admin") {
    $("#view").innerHTML = `<div class="warn-box">Requiere rol administrador.</div>`;
    return;
  }
  let users;
  try { users = await Api.get("/api/users"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Usuarios del sistema</h3>
      <button class="btn" id="btn-user-new">Agregar usuario</button>
    </div>
    <table class="grid">
      <thead><tr>
        <th>Usuario</th><th>Rol</th><th>Estado</th><th>Creado</th><th>Última clave</th><th></th>
      </tr></thead>
      <tbody>
        ${users.map((u) => `
          <tr data-id="${u.id}">
            <td>${esc(u.username)}</td>
            <td><span class="tag ${u.role === "Admin" ? "admin" : "operator"}">${u.role === "Admin" ? "Administrador" : "Operador"}</span></td>
            <td><span class="tag ${u.enabled ? "on" : "off"}">${u.enabled ? "Habilitado" : "Deshabilitado"}</span></td>
            <td class="muted">${formatDate(u.createdAt)}</td>
            <td class="muted">${formatDate(u.passwordChangedAt)}</td>
            <td class="row-actions">
              <button class="btn ghost btn-edit">Editar</button>
              <button class="btn danger btn-delete">Eliminar</button>
            </td>
          </tr>`).join("")}
      </tbody>
    </table>`;

  $("#btn-user-new").addEventListener("click", () => userModal(null));
  $$("#view .btn-edit").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    userModal(users.find((u) => u.id === id));
  }));
  $$("#view .btn-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const user = users.find((u) => u.id === id);
    if (!confirm(`¿Eliminar al usuario "${user.username}"? Esta acción no se puede deshacer.`)) return;
    try {
      await Api.delete(`/api/users/${id}`);
      toast("Usuario eliminado.");
      renderUsers();
    } catch (err) { toast(err.error, true); }
  }));
}

function userModal(user) {
  const isNew = !user;
  openModal(`
    <h3>${isNew ? "Agregar usuario" : "Editar usuario"}</h3>
    <div id="user-modal-error"></div>
    <form id="user-form">
      <div class="field">
        <label>Nombre de usuario</label>
        <input id="uf-username" required minlength="3" maxlength="64" value="${esc(user?.username ?? "")}">
      </div>
      <div class="field">
        <label>Rol</label>
        <select id="uf-role">
          <option value="Operator" ${user?.role === "Operator" ? "selected" : ""}>Operador</option>
          <option value="Admin" ${user?.role === "Admin" ? "selected" : ""}>Administrador</option>
        </select>
      </div>
      <div class="checkbox-row">
        <input type="checkbox" id="uf-enabled" ${user?.enabled !== false ? "checked" : ""}>
        <label for="uf-enabled" style="margin:0">Habilitado</label>
      </div>
      <div class="field">
        <label>${isNew ? "Contraseña" : "Contraseña nueva (vacío = no cambiar)"}</label>
        <input id="uf-password" type="password" autocomplete="new-password" ${isNew ? "required" : ""}>
      </div>
      ${policyListHtml("uf-policy")}
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="uf-cancel">Cancelar</button>
        <button class="btn" type="submit">${isNew ? "Crear" : "Guardar"}</button>
      </div>
    </form>`);

  bindPolicyList($("#uf-password"), "uf-policy");
  $("#uf-cancel").addEventListener("click", closeModal);
  $("#user-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#user-modal-error");
    errorBox.innerHTML = "";
    const password = $("#uf-password").value;
    if ((isNew || password) && !passwordOk(password)) {
      errorBox.innerHTML = `<div class="error-box">La contraseña no cumple la política de seguridad.</div>`;
      return;
    }
    const body = {
      username: $("#uf-username").value.trim(),
      password: password || null,
      role: $("#uf-role").value,
      enabled: $("#uf-enabled").checked,
    };
    try {
      if (isNew) await Api.post("/api/users", body);
      else await Api.put(`/api/users/${user.id}`, body);
      closeModal();
      toast(isNew ? "Usuario creado." : "Usuario actualizado.");
      renderUsers();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

// ---------------------------------------------------------------------------
// Router y arranque
// ---------------------------------------------------------------------------
const routes = {
  "": renderDashboard,
  "#/": renderDashboard,
  "#/devices": renderDevices,
  "#/decoders": renderDecoders,
  "#/walls": renderWalls,
  "#/sessions": renderSessions,
  "#/users": renderUsers,
};

function navigate() {
  clearInterval(sessionsTimer); // el sondeo de sesiones vive solo en su página
  clearInterval(wallsTimer);    // ídem el del estado de los muros
  const hash = location.hash || "#/";
  const render = routes[hash] || renderDashboard;
  $$("#nav a").forEach((a) => a.classList.toggle("active", a.getAttribute("href") === hash));
  render();
}

function enterApp() {
  showAppShell();
  if (!location.hash || !routes[location.hash]) location.hash = "#/";
  navigate();
}

// Sonda de conexión (igual que el producto videowall: /api/health periódico).
setInterval(async () => {
  if ($("#app-shell").classList.contains("hidden")) return;
  let ok = true;
  try { await Api.get("/api/health"); } catch { ok = false; }
  $("#conn-dot").className = "dot " + (ok ? "ok" : "bad");
  $("#conn-text").textContent = ok ? "Conectado" : "Sin conexión";
}, 4000);

window.addEventListener("hashchange", () => {
  if (!$("#app-shell").classList.contains("hidden")) navigate();
});

window.addEventListener("tcvms:unauthorized", () => {
  renderLogin("La sesión expiró. Ingrese nuevamente.");
});

$("#btn-logout").addEventListener("click", async () => {
  try { await Api.post("/api/auth/logout"); } catch { /* la sesión local se limpia igual */ }
  Api.clearSession();
  renderLogin();
});

async function init() {
  let status = null;
  try { status = await Api.get("/api/setup/status"); }
  catch { /* servidor caído: se muestra el login, la sonda avisará */ }

  $("#auth-version").textContent = status ? `v${status.serverVersion}` : "";

  if (status && status.setupRequired) { renderSetup(status); return; }
  if (Api.token) {
    try {
      await Api.get("/api/auth/me");
      enterApp();
      return;
    } catch { Api.clearSession(); }
  }
  renderLogin();
}

init();
