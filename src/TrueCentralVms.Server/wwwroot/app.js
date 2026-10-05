// CLR TrueCentral VMS — panel de administración (SPA sin framework).
"use strict";

const $ = (sel, root) => (root || document).querySelector(sel);
const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------------------------------------------------------------------------
// Campo de contraseña con botón "mostrar/ocultar"
// ---------------------------------------------------------------------------
const EYE_ICON = `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7S1 12 1 12z"/><circle cx="12" cy="12" r="3"/></svg>`;
const EYE_OFF_ICON = `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94"/><path d="M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19"/><path d="M14.12 14.12a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></svg>`;

/// Un <input type="password"> con el ojo para ver lo escrito. `attrs` son los
/// atributos del input (id, autocomplete, required...). Activar con bindPasswordToggles().
function passwordInputHtml(attrs) {
  return `<div class="pw-wrap">
    <input type="password" ${attrs}>
    <button type="button" class="pw-toggle" title="Mostrar contraseña" aria-label="Mostrar contraseña" aria-pressed="false">${EYE_ICON}</button>
  </div>`;
}

/// Activa los botones de mostrar/ocultar de todos los campos de contraseña dentro de `root`.
function bindPasswordToggles(root) {
  $$(".pw-toggle", root).forEach((button) => {
    const input = button.previousElementSibling;
    if (!input) return;
    button.addEventListener("click", () => {
      const show = input.type === "password";
      input.type = show ? "text" : "password";
      button.innerHTML = show ? EYE_OFF_ICON : EYE_ICON;
      button.title = button.ariaLabel = show ? "Ocultar contraseña" : "Mostrar contraseña";
      button.setAttribute("aria-pressed", String(show));
      input.focus();
    });
  });
}

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
function toast(message, isError, options = {}) {
  const el = $("#toast");
  el.textContent = message;
  el.classList.toggle("error", !!isError);
  el.classList.toggle("warning", !!options.warning);
  el.classList.remove("hidden");
  clearTimeout(toastTimer);
  // Un aviso (p. ej. canales fuera del cupo de licencia) se deja más tiempo en pantalla.
  toastTimer = setTimeout(() => el.classList.add("hidden"), options.warning ? 9000 : 3500);
}

/** Resultado de alta/edición/revalidación de un equipo: el mensaje normal, o
 *  el aviso del servidor (canales deshabilitados por cupo) si lo hay. */
function toastDeviceSaved(result, message) {
  if (result && result.warning) toast(`${message} ${result.warning}`, false, { warning: true });
  else toast(message);
}

/** Celda "Canales": habilitados / total. Solo se destaca si hay canales CON
 *  SEÑAL deshabilitados (cámaras que los operadores no ven); las entradas sin
 *  cámara de un DVR/NVR son normales y quedan en gris. */
function channelCountCell(d) {
  const total = d.channelCount ?? 0;
  const enabled = d.enabledChannelCount ?? total;
  if (total === 0) return `<span class="muted">0</span>`;
  if (enabled === total) return `${total}`;
  const hidden = d.disabledWithSignalCount ?? 0;
  if (hidden === 0) {
    return `<span class="muted" style="white-space:nowrap" title="${enabled} de ${total} canales habilitados. Los otros ${total - enabled} no tienen señal (entradas sin cámara${d.status === "Online" ? "" : " o equipo sin conexión"}).">${enabled} / ${total}</span>`;
  }
  const cls = enabled === 0 ? "off" : "warn";
  return `<span class="tag ${cls}" style="white-space:nowrap" title="${hidden} canal(es) con señal están deshabilitados: los operadores no los ven y no tienen ruta de streaming. Habilítelos desde &quot;Canales&quot;.">${enabled} / ${total}</span>`;
}

function openModal(html, size) {
  // `size`: true = "wide" (formularios con grilla, editor de muros) o el
  // nombre de una clase de ancho ("wider" para tablas dentro del modal).
  $("#modal").className = "modal" + (size === true ? " wide" : size ? " " + size : "");
  $("#modal").innerHTML = html;
  $("#modal-backdrop").classList.remove("hidden");
}
function closeModal() { $("#modal-backdrop").classList.add("hidden"); }
// Un clic en el fondo NO cierra el modal: acá casi todos son formularios (alta
// de un equipo, asistente de personas, editor de horarios) y un clic al lado
// perdía todo lo escrito sin preguntar. Se cierran por su botón Cancelar o
// Cerrar, que todos tienen.

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
        El servidor no ha sido inicializado aún, por seguridad este proceso
        solo puede realizarse desde la propia máquina del servidor.
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
        ${passwordInputHtml(`id="login-password" autocomplete="current-password" required`)}
      </div>
      <button class="btn block" type="submit">Ingresar</button>
    </form>`;

  bindPasswordToggles($("#login-form"));

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
  let license = null;
  try { license = await Api.get("/api/system/license"); } catch { /* sin sesión aún */ }
  let servicesRunning = "—", servicesTotal = "—", servicesFailed = 0;
  try {
    const overview = await Api.get("/api/system/services");
    const supervised = overview.services.filter((s) => s.state !== "Disabled");
    servicesTotal = supervised.length;
    servicesRunning = supervised.filter((s) => s.state === "Running").length;
    servicesFailed = supervised.filter((s) => s.state === "Failed").length;
  } catch { /* sin sesión aún */ }
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
      ${license ? `
      <a class="card" href="#/license" style="text-decoration:none;color:inherit">
        <div class="card-label">Licencia</div>
        <div class="card-value small">${licenseStateTag(license.state)}</div>
        <div class="muted" style="font-size:12px">${esc(license.licenseKey || license.message || "")}</div>
        ${license.warning ? `<div style="font-size:12px;color:${license.operational ? "#F59E0B" : "var(--danger)"}">${esc(license.warning)}</div>` : ""}
      </a>` : ""}
      <a class="card" href="#/services" style="text-decoration:none;color:inherit">
        <div class="card-label">Servicios del servidor</div>
        <div class="card-value" style="color:${servicesFailed ? "var(--danger)" : "inherit"}">${servicesRunning}<span class="muted" style="font-size:13px"> de ${servicesTotal} en ejecución</span></div>
        ${servicesFailed ? `<div style="font-size:12px;color:var(--danger)">${servicesFailed} caído(s): revisar</div>` : ""}
      </a>
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

/** Drivers que entregan reconocimientos de patentes (ver DriverCapabilities.SupportsAnpr). */
const ANPR_DRIVERS = ["hikvision-netsdk"];

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
          <th>N° serie</th><th>Firmware</th><th>Canales</th><th>Patentes</th><th>Estado</th><th></th>
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
              <td>${channelCountCell(d)}</td>
              <td>${anprCell(d, isAdmin)}</td>
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
      <h3>Equipos en línea <span class="muted" style="font-weight:normal;font-size:12px">(SADP · Dahua · ONVIF en la red local · se actualiza cada 30 s)</span></h3>
      <button class="btn ghost" id="btn-device-scan">Buscar</button>
    </div>
    <div id="online-devices">
      ${lastScan ? "" : `<div class="info-box">Sondeando el segmento de red del servidor…
        (SADP para Hikvision, DHDiscover para Dahua y WS-Discovery para cualquier marca ONVIF).
        Solo se listan equipos de video (cámaras, DVR, NVR, decodificadores); los controles de acceso y alarmas se omiten.</div>`}
    </div>` : ""}`;

  $$("#view .btn-anpr").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const enabled = e.target.dataset.on !== "1";
    e.target.disabled = true;
    try {
      await Api.put(`/api/anpr/sources/${id}`, { enabled });
      toast(enabled
        ? "Equipo encendido como fuente de patentes."
        : "Equipo apagado como fuente de patentes.");
      renderDevices();
    } catch (err) { toast(err.error, true); e.target.disabled = false; }
  }));
  $("#btn-device-new")?.addEventListener("click", () => deviceModal(null));
  $("#btn-device-scan")?.addEventListener("click", () => runDiscovery(devices));
  if (lastScan) renderOnlineDevices(devices);
  if (isAdmin) startDiscoveryPolling(devices);
  $$("#view .btn-revalidate").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    e.target.disabled = true;
    e.target.textContent = "Sondeando…";
    try {
      const result = await Api.post(`/api/devices/${id}/revalidate`);
      toastDeviceSaved(result, "Equipo revalidado: información y canales actualizados.");
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

/**
 * Celda "Patentes": interruptor de la fuente ANPR. Solo tiene sentido en
 * equipos cuyo driver sabe entregar lecturas (hoy, Hikvision por SDK); en el
 * resto se muestra un guion en vez de un botón que fallaría.
 */
function anprCell(device, isAdmin) {
  if (!ANPR_DRIVERS.includes(device.driverKey)) return `<span class="muted">—</span>`;
  if (!isAdmin) return device.anprEnabled ? "Sí" : `<span class="muted">No</span>`;
  return `<button class="btn ghost btn-anpr" data-on="${device.anprEnabled ? 1 : 0}"
    title="Recibir los reconocimientos de patentes de este equipo">${device.anprEnabled ? "Activo" : "Activar"}</button>`;
}

let discoveryTimer = null;
/** Opciones (página) del sondeo en curso, o null. */
let discoveryBusy = null;
let lastScanAt = 0;

/**
 * Los equipos entran y salen de la red sin avisar, así que la lista se sondea
 * al abrir la página y se refresca sola cada 30 s (el sondeo dura ~4 s: es la
 * ventana que espera el servidor por SADP, DHDiscover y WS-Discovery).
 */
// Opciones por defecto: la página Fuentes de video. Decodificadores reutiliza
// el mismo sondeo con kind=decoders y su propio contenedor/modal.
const DEVICE_DISCOVERY = {
  container: "#online-devices",
  button: "#btn-device-scan",
  kind: "",
  emptyText: "No se encontraron equipos de video en este segmento de red.",
  onUse: (d) => deviceModal(null, {
    name: d.model || d.ip,
    host: d.ip,
    sdkPort: d.commandPort || 8000,
    driverKey: d.driverKey || "hikvision-netsdk",
    username: d.username, // viene solo si se acaba de inicializar desde acá
  }),
};
let discoveryOptions = DEVICE_DISCOVERY;

function startDiscoveryPolling(devices, options = DEVICE_DISCOVERY) {
  clearInterval(discoveryTimer);
  // Cambiar de página (o de tipo de sondeo) invalida el resultado anterior.
  if (discoveryOptions !== options) { lastScan = null; lastScanAt = 0; }
  discoveryOptions = options;
  // renderDevices() se repite al agregar, editar o revalidar un equipo: ahí no
  // se vuelve a sondear si el último resultado todavía está fresco.
  if (!lastScan || Date.now() - lastScanAt >= 30000) {
    runDiscovery(devices, !lastScan); // con resultados en pantalla, el refresco es silencioso
  }
  discoveryTimer = setInterval(() => {
    if (!$(discoveryOptions.container)) { clearInterval(discoveryTimer); return; }
    runDiscovery(devices, false);
  }, 30000);
}

/** @param showProgress muestra el aviso de "sondeando" y los errores; falso en los refrescos automáticos. */
async function runDiscovery(devices, showProgress = true) {
  const opt = discoveryOptions;
  // No encimar el sondeo automático con el del botón DE LA MISMA página. Uno
  // de otra página (se cambió de Fuentes de video a Control de acceso a mitad
  // de sondeo) no bloquea: su resultado se descarta al volver.
  if (discoveryBusy === opt) return;
  if (!$(opt.container)) return;
  discoveryBusy = opt;
  const scanButton = $(opt.button);
  if (scanButton) { scanButton.disabled = true; scanButton.textContent = "Buscando…"; }
  if (showProgress) {
    const box = $(opt.container);
    box.innerHTML = `<div class="info-box">Sondeando la red… (unos segundos)</div>`;
    box.dataset.signature = ""; // se reemplazó la tabla: hay que volver a pintarla aunque el resultado repita
  }
  try {
    const scan = await Api.get(`/api/discovery/scan${opt.kind ? `?kind=${encodeURIComponent(opt.kind)}` : ""}`);
    // Mientras tanto se cambió de página: este resultado es de otro tipo de
    // equipos (cámaras en la tabla de control de acceso). Se descarta.
    if (discoveryOptions !== opt) return;
    lastScan = scan;
    lastScanAt = Date.now();
    renderOnlineDevices(devices);
  } catch (err) {
    if (discoveryOptions !== opt) return;
    // En el refresco automático se conserva la última lista buena: un aviso
    // cada 30 s por un sondeo fallido sería solo ruido.
    const box = showProgress ? $(opt.container) : null;
    if (box) { box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; box.dataset.signature = ""; }
  } finally {
    if (discoveryBusy === opt) discoveryBusy = null;
    const btn = $(opt.button);
    if (btn) { btn.disabled = false; btn.textContent = "Buscar"; }
  }
}

/// Tabla de equipos descubiertos, al estilo "Online Device" de HikCentral.
function renderOnlineDevices(devices) {
  const opt = discoveryOptions;
  const box = $(opt.container);
  if (!box) return; // la página cambió mientras corría el sondeo
  // El refresco automático no debe repintar la tabla si nada cambió.
  const signature = JSON.stringify(lastScan) + "|" + devices.map((d) => d.host).join(",");
  if (box.dataset.signature === signature) return;
  box.dataset.signature = signature;
  if (!lastScan || !lastScan.length) {
    box.innerHTML = `<div class="warn-box">${esc(opt.emptyText)}</div>`;
    return;
  }
  const knownHosts = new Set(devices.map((d) => d.host));
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>IP</th><th>Marca</th><th>Tipo</th><th>Modelo</th><th>N° serie</th><th>${esc(opt.portLabel ?? "Puerto SDK")}</th>
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
          <td class="muted">${opt.portOf ? opt.portOf(d) : d.commandPort}</td>
          <td class="muted">${esc(d.mac)}</td>
          <td>${added ? `<span class="tag on">Agregado</span>`
                : d.canInitialize ? `<span class="tag off">Sin inicializar</span>`
                : d.activated === false ? `<span class="tag off">Sin activar</span>`
                : `<span class="tag operator">Nuevo</span>`}</td>
          <td class="row-actions">
            ${d.canChangeIp && !added
              ? `<button class="btn ghost scan-ip" data-i="${i}" title="Cambia la IP del equipo sin entrar a él (como Change IP de SmartPSS)">Cambiar IP</button>`
              : ""}
            ${d.canInitialize && !added
              ? `<button class="btn scan-init" data-i="${i}" title="Crea el usuario admin del equipo de fábrica">Inicializar</button>`
              : `<button class="btn ghost scan-use" data-i="${i}" ${added ? "disabled" : ""}
                  ${d.reachable === false ? `title="El equipo está en otra subred: cámbiele la IP antes de agregarlo"` : ""}>Agregar</button>`}
          </td>
        </tr>`;
      }).join("")}
      </tbody>
    </table></div>`;
  $$(".scan-use").forEach((b) => b.addEventListener("click", () => opt.onUse(lastScan[Number(b.dataset.i)])));
  $$(".scan-init").forEach((b) => b.addEventListener("click", () => dahuaInitModal(lastScan[Number(b.dataset.i)], devices)));
  $$(".scan-ip").forEach((b) => b.addEventListener("click", () => changeIpModal(lastScan[Number(b.dataset.i)], devices)));
}

/** Validación rápida de IPv4 en el panel (el servidor revisa lo mismo y más). */
const isIPv4 = (s) => /^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$/.test(s);

/**
 * Cambia la red de un equipo Dahua o Hikvision sin entrar a él (multicast por
 * MAC), como "Change IP" de SmartPSS o SADP Tool: IP fija o DHCP, y los DNS
 * (que el servidor aplica después por la API web del equipo). Si el equipo
 * está fuera de la subred del servidor se propone una IP de la red del servidor.
 * @param {string} [knownPassword] la que se acaba de elegir al inicializarlo (no se vuelve a pedir).
 */
async function changeIpModal(d, devices, knownPassword) {
  const opt = discoveryOptions;
  let networks = [];
  try { networks = await Api.get("/api/discovery/local-networks"); } catch { /* se llena a mano */ }
  // Equipo en la red del servidor: se conserva su configuración. Si no, se
  // propone la red principal del servidor (la que tiene puerta de enlace).
  const net = networks[0];
  const keep = d.reachable !== false || !net;
  const ip = keep ? d.ip : net.address.split(".").slice(0, 3).join(".") + ".";
  const mask = keep ? (d.subnetMask || "255.255.255.0") : net.mask;
  const gateway = keep ? (d.gateway || "") : net.gateway;
  // DNS propuestos: la puerta de enlace (casi siempre resuelve) y uno público.
  const dns1 = gateway || "";
  const dns2 = "8.8.8.8";

  openModal(`
    <h3>Cambiar IP</h3>
    <p class="muted" style="margin-top:0">
      ${esc(d.brand)} ${esc(d.model)} · ${esc(d.mac)}<br>
      IP actual: <b>${esc(d.ip)}</b>${d.dhcp ? " (por DHCP)" : ""}${d.reachable === false
        ? ` — fuera de la red de este servidor${net ? ` (${esc(net.address)})` : ""}: no se puede agregar hasta cambiarla.` : ""}
    </p>
    <div id="dip-error"></div>
    <form id="dip-form">
      <div class="field">
        <label>Modo</label>
        <div class="segmented" role="radiogroup">
          <label><input type="radio" name="dip-mode" value="static" ${d.dhcp ? "" : "checked"}> IP fija</label>
          <label><input type="radio" name="dip-mode" value="dhcp" ${d.dhcp ? "checked" : ""}> DHCP</label>
        </div>
      </div>
      <div id="dip-static">
        <div class="form-grid">
          <div class="field">
            <label>IP nueva</label>
            <input id="dip-ip" value="${esc(ip)}" placeholder="192.168.10.60">
          </div>
          <div class="field">
            <label>Máscara de subred</label>
            <input id="dip-mask" value="${esc(mask)}">
          </div>
        </div>
        <div class="field">
          <label>Puerta de enlace (opcional)</label>
          <input id="dip-gw" value="${esc(gateway)}">
        </div>
        <div class="form-grid">
          <div class="field">
            <label>DNS preferido (opcional)</label>
            <input id="dip-dns1" value="${esc(dns1)}" placeholder="vacío = no cambiar">
          </div>
          <div class="field">
            <label>DNS alternativo (opcional)</label>
            <input id="dip-dns2" value="${esc(dns2)}">
          </div>
        </div>
      </div>
      <div id="dip-dhcp-note" class="muted hidden" style="font-size:12px;margin:-4px 0 12px">
        El equipo tomará IP, máscara, puerta de enlace y DNS del servidor DHCP de la red. TrueCentral lo
        busca después por su MAC para mostrar la IP que le tocó.
      </div>
      ${knownPassword ? "" : `
      <div class="form-grid">
        <div class="field">
          <label>Usuario del equipo</label>
          <input id="dip-user" value="admin" required>
        </div>
        <div class="field">
          <label>Contraseña del equipo</label>
          <input id="dip-pass" type="password" autocomplete="current-password" required>
        </div>
      </div>`}
      <label class="check-row" style="display:flex;gap:8px;align-items:flex-start;margin:0 0 12px">
        <input type="checkbox" id="dip-time" ${knownPassword ? "checked" : ""}>
        <span>Sincronizar fecha, hora y zona horaria (con horario de verano) con este servidor</span>
      </label>
      <div class="muted" style="font-size:12px;margin:-4px 0 12px" id="dip-static-note">
        Se comprueba antes que la IP nueva no esté ocupada. Los DNS se configuran entrando al equipo
        una vez que está en su IP nueva (con el mismo usuario y contraseña).
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="dip-cancel">Cancelar</button>
        <button class="btn" type="submit" id="dip-save">Aplicar</button>
      </div>
    </form>`);

  const isDhcp = () => $("input[name=dip-mode]:checked").value === "dhcp";
  const syncMode = () => {
    $("#dip-static").classList.toggle("hidden", isDhcp());
    $("#dip-static-note").classList.toggle("hidden", isDhcp());
    $("#dip-dhcp-note").classList.toggle("hidden", !isDhcp());
  };
  $$("input[name=dip-mode]").forEach((r) => r.addEventListener("change", syncMode));
  syncMode();

  if (!isDhcp()) {
    // El cursor queda al final de la IP propuesta, listo para completar el último número.
    const ipInput = $("#dip-ip");
    ipInput.focus();
    ipInput.setSelectionRange(ipInput.value.length, ipInput.value.length);
  }

  $("#dip-cancel").addEventListener("click", closeModal);
  $("#dip-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const showError = (msg) => { $("#dip-error").innerHTML = `<div class="error-box">${esc(msg)}</div>`; };
    const dhcp = isDhcp();
    const body = {
      brand: d.brand,
      mac: d.mac,
      dhcp,
      ip: $("#dip-ip").value.trim(),
      mask: $("#dip-mask").value.trim(),
      gateway: $("#dip-gw").value.trim(),
      dns1: $("#dip-dns1").value.trim(),
      dns2: $("#dip-dns2").value.trim(),
      username: knownPassword ? "admin" : $("#dip-user").value.trim(),
      password: knownPassword ?? $("#dip-pass").value,
      syncTime: $("#dip-time").checked,
    };
    if (!dhcp) {
      if (!isIPv4(body.ip)) { showError("Escriba la IP nueva completa (por ejemplo 192.168.10.60)."); return; }
      if (!isIPv4(body.mask)) { showError("La máscara de subred no es válida."); return; }
      if (body.gateway && !isIPv4(body.gateway)) { showError("La puerta de enlace no es válida."); return; }
      if (body.dns1 && !isIPv4(body.dns1)) { showError("El DNS preferido no es válido."); return; }
      if (body.dns2 && !isIPv4(body.dns2)) { showError("El DNS alternativo no es válido."); return; }
      if (body.dns2 && !body.dns1) { showError("Indique primero el DNS preferido."); return; }
    }

    const button = $("#dip-save");
    button.disabled = true;
    button.textContent = "Aplicando… (puede tardar un minuto)";
    $("#dip-error").innerHTML = "";
    try {
      const r = await Api.post("/api/discovery/change-ip", body);
      lastScan = null;
      runDiscovery(devices, false);
      const ready = { ...d, ip: r.ip, reachable: r.reachable, dhcp: r.dhcp, username: r.username };
      openModal(`
        <h3>Red del equipo cambiada</h3>
        <div class="info-box">${esc(r.model)} ${r.dhcp ? "quedó en DHCP y" : "ahora"} está en <b>${esc(r.ip)}</b>.</div>
        ${r.confirmed ? "" : `<div class="warn-box" style="margin-top:10px">
          ${r.dhcp
            ? "El equipo aceptó el cambio pero todavía no aparece con la IP que le dio el DHCP; búsquelo en la próxima búsqueda por su MAC."
            : "El equipo aceptó el cambio pero todavía no responde en la IP nueva; algunos tardan hasta un minuto en reiniciar la red. Si no aparece en la próxima búsqueda, revise el cableado y la IP elegida."}</div>`}
        ${r.dnsApplied ? `<div class="info-box" style="margin-top:10px">DNS configurados.</div>` : ""}
        ${r.dnsError ? `<div class="warn-box" style="margin-top:10px">No se pudieron configurar los DNS: ${esc(r.dnsError)}</div>` : ""}
        ${r.timeSynced ? `<div class="info-box" style="margin-top:10px">Fecha, hora y zona horaria sincronizadas con el servidor.</div>` : ""}
        ${r.timeError ? `<div class="warn-box" style="margin-top:10px">No se pudo sincronizar la fecha y la hora: ${esc(r.timeError)}</div>` : ""}
        ${r.reachable ? "" : `<div class="warn-box" style="margin-top:10px">
          La IP ${esc(r.ip)} no está en la red de este servidor: no se podrá agregar desde acá.</div>`}
        <div class="modal-actions">
          <button class="btn ghost" type="button" id="dip-close">Cerrar</button>
          ${r.reachable ? `<button class="btn" type="button" id="dip-add">Agregar ahora</button>` : ""}
        </div>`);
      $("#dip-close").addEventListener("click", closeModal);
      $("#dip-add")?.addEventListener("click", () => opt.onUse(ready));
    } catch (err) {
      showError(err.error || "No se pudo cambiar la IP.");
      button.disabled = false;
      button.textContent = "Aplicar";
    }
  });
}

/** Política de contraseñas Dahua (la misma que revisa el servidor). Devuelve el problema o null. */
function dahuaPasswordProblem(p) {
  if (!p || p.length < 8 || p.length > 32) return "La contraseña debe tener entre 8 y 32 caracteres.";
  if (/['";:& ]/.test(p)) return "La contraseña no puede llevar espacios ni los caracteres ' \" ; : &.";
  const kinds = [/[A-Z]/, /[a-z]/, /[0-9]/, /[^A-Za-z0-9]/].filter((r) => r.test(p)).length;
  return kinds < 2
    ? "La contraseña debe combinar al menos dos tipos de caracteres: mayúsculas, minúsculas, números o símbolos."
    : null;
}

/**
 * Inicializa un equipo Dahua de fábrica (le crea el usuario admin), como el
 * botón "Initialize" de SmartPSS. Al terminar ofrece agregarlo de inmediato.
 */
function dahuaInitModal(d, devices) {
  const opt = discoveryOptions;
  openModal(`
    <h3>Inicializar equipo</h3>
    <p class="muted" style="margin-top:0">
      ${esc(d.model)} · ${esc(d.ip)} · ${esc(d.mac)}<br>
      El equipo está de fábrica y no tiene usuarios. Se le creará el usuario <b>admin</b> con esta contraseña.
    </p>
    <div id="di-error"></div>
    <form id="di-form">
      <div class="field">
        <label>Usuario</label>
        <input value="admin" disabled>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Contraseña</label>
          <input id="di-pass" type="password" autocomplete="new-password" required maxlength="32">
        </div>
        <div class="field">
          <label>Confirmar contraseña</label>
          <input id="di-pass2" type="password" autocomplete="new-password" required maxlength="32">
        </div>
      </div>
      <div class="muted" style="font-size:12px;margin:-4px 0 12px">
        8 a 32 caracteres, combinando al menos dos tipos (mayúsculas, minúsculas, números o símbolos), sin ' " ; : &amp; ni espacios.
      </div>
      <div class="field">
        <label>Correo para recuperar la contraseña${d.initNeedsEmail ? "" : " (opcional)"}</label>
        <input id="di-email" type="email" maxlength="63" ${d.initNeedsEmail ? "required" : ""}>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="di-cancel">Cancelar</button>
        <button class="btn" type="submit" id="di-save">Inicializar</button>
      </div>
    </form>`);

  $("#di-cancel").addEventListener("click", closeModal);
  $("#di-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const password = $("#di-pass").value;
    const confirmPassword = $("#di-pass2").value;
    const showError = (msg) => { $("#di-error").innerHTML = `<div class="error-box">${esc(msg)}</div>`; };
    const problem = dahuaPasswordProblem(password)
      ?? (password !== confirmPassword ? "Las contraseñas no coinciden." : null);
    if (problem) { showError(problem); return; }

    const button = $("#di-save");
    button.disabled = true;
    button.textContent = "Inicializando…";
    $("#di-error").innerHTML = "";
    try {
      const r = await Api.post("/api/discovery/dahua/initialize", {
        mac: d.mac, password, confirmPassword, email: $("#di-email").value,
      });
      // El equipo ya tiene usuario: la próxima búsqueda lo mostrará como "Nuevo".
      lastScan = null;
      runDiscovery(devices, false);
      const ready = { ...d, activated: true, canInitialize: false, username: r.username };
      openModal(`
        <h3>Equipo inicializado</h3>
        <div class="info-box">
          ${esc(r.model)} (${esc(r.ip)}) ya tiene el usuario <b>${esc(r.username)}</b> con la contraseña elegida.
        </div>
        ${r.reachable ? "" : `<div class="warn-box" style="margin-top:10px">
          El equipo sigue en la IP ${esc(r.ip)}, que no está en la red de este servidor: para agregarlo
          hay que cambiarle la IP a una del mismo segmento.</div>`}
        <div class="modal-actions">
          <button class="btn ghost" type="button" id="di-close">Cerrar</button>
          ${r.reachable
            ? `<button class="btn" type="button" id="di-add">Agregar ahora</button>`
            : `<button class="btn" type="button" id="di-ip">Cambiar IP ahora</button>`}
        </div>`);
      $("#di-close").addEventListener("click", closeModal);
      $("#di-add")?.addEventListener("click", () => opt.onUse(ready));
      // La contraseña recién elegida se reutiliza: no se vuelve a pedir.
      $("#di-ip")?.addEventListener("click", () =>
        changeIpModal({ ...ready, reachable: false }, devices, password));
    } catch (err) {
      showError(err.error || "No se pudo inicializar el equipo.");
      button.disabled = false;
      button.textContent = "Inicializar";
    }
  });
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
          <input id="df-username" required value="${esc(device?.username ?? seed.username ?? "")}" placeholder="admin">
        </div>
      </div>
      <div class="field">
        <label>${isNew ? "Contraseña del equipo" : "Contraseña (vacío = no cambiar)"}</label>
        <input id="df-password" type="password" autocomplete="new-password" ${isNew ? "required" : ""}>
      </div>
      <div id="probe-result"></div>
      <div id="channel-picker"></div>
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

  // Selector de canales del alta: aparece solo cuando el equipo reporta más
  // canales activos que los que deja libres la licencia. El servidor exige
  // la selección en ese caso (a lo sumo `available` canales).
  let pickerLimit = 0;
  const pickerActive = () => isNew && $("#channel-picker").querySelector("input[type=checkbox]") !== null;
  const pickedChannels = () => $$("#channel-picker input[type=checkbox]:checked").map((c) => Number(c.value));

  const renderChannelPicker = (channels, available, message) => {
    pickerLimit = available;
    const box = $("#channel-picker");
    if (!isNew || !channels.length || available <= 0) { box.innerHTML = ""; return; }
    // Preselección: los primeros canales activos hasta completar el cupo.
    let left = available;
    const preselected = new Set();
    for (const c of channels) { if (left > 0 && c.isOnline) { preselected.add(c.channelNumber); left--; } }
    box.innerHTML = `
      <div class="picker-box">
        <div class="picker-title">Seleccione los canales a habilitar</div>
        <div class="muted picker-note">${esc(message)}</div>
        <div class="picker-tools">
          <span id="picker-count"></span>
          <button class="btn ghost small" type="button" id="picker-clear">Quitar todos</button>
        </div>
        <div class="picker-list">
          ${channels.map((c) => `
            <label class="picker-row">
              <input type="checkbox" value="${c.channelNumber}" ${preselected.has(c.channelNumber) ? "checked" : ""}>
              <span class="picker-num">${c.channelNumber}</span>
              <span class="picker-name">${esc(c.name)}</span>
              <span class="tag ${c.isOnline ? "on" : "operator"}">${c.isOnline ? "activo" : "sin señal"}</span>
            </label>`).join("")}
        </div>
      </div>`;
    const refresh = () => {
      const picked = pickedChannels().length;
      $("#picker-count").textContent = `${picked} de ${available} disponibles seleccionados`;
      $("#picker-count").classList.toggle("over", picked > available);
      // Al llenar el cupo se bloquean los no marcados; desmarcar libera.
      $$("#channel-picker input[type=checkbox]").forEach((cb) => { cb.disabled = !cb.checked && picked >= available; });
      $("#df-save").disabled = picked === 0;
    };
    $$("#channel-picker input[type=checkbox]").forEach((cb) => cb.addEventListener("change", refresh));
    $("#picker-clear").addEventListener("click", () => {
      $$("#channel-picker input[type=checkbox]").forEach((cb) => { cb.checked = false; });
      refresh();
    });
    refresh();
  };

  const readForm = () => {
    const body = {
      name: $("#df-name").value.trim() || "(sin nombre)",
      driverKey: $("#df-driver").value,
      host: $("#df-host").value.trim(),
      sdkPort: Number($("#df-sdkport").value),
      rtspPort: Number($("#df-rtspport").value),
      username: $("#df-username").value.trim(),
      password: $("#df-password").value || null,
    };
    if (pickerActive()) body.enabledChannels = pickedChannels();
    return body;
  };

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
      const active = r.channels.filter((c) => c.isOnline).length;
      const needsPicker = isNew && r.availableVideoChannels > 0 && active > r.availableVideoChannels;
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
            <span>Cupo de licencia</span><b>${r.availableVideoChannels} canales disponibles (${r.videoChannelsInUse} de ${r.videoChannelQuota} en uso)</b>
          </div>
          ${r.channels.length && !needsPicker ? `<div class="probe-channels">${r.channels.map((c) =>
            `<span class="tag ${c.isOnline ? "on" : "operator"}" title="Canal ${c.channelNumber}">${esc(c.name)}</span>`).join(" ")}</div>` : ""}
        </div>`;
      $("#df-save").disabled = false;
      if (isNew && r.availableVideoChannels <= 0) {
        // Sin cupo el servidor rechaza el alta: se avisa antes de intentarlo.
        $("#channel-picker").innerHTML = "";
        errorBox.innerHTML = `<div class="error-box">No hay canales de video disponibles en la licencia (${r.videoChannelsInUse} de ${r.videoChannelQuota} en uso). Amplíe la licencia o deshabilite canales en otros equipos antes de agregar este.</div>`;
        $("#df-save").disabled = true;
      } else if (needsPicker) {
        renderChannelPicker(r.channels, r.availableVideoChannels,
          `El equipo reporta ${active} canales activos y la licencia solo tiene ${r.availableVideoChannels} disponibles. Los demás quedan deshabilitados y pueden habilitarse después desde "Canales" si se amplía la licencia.`);
      } else {
        $("#channel-picker").innerHTML = "";
      }
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
      const result = isNew
        ? await Api.post("/api/devices", body)
        : await Api.put(`/api/devices/${device.id}`, body);
      closeModal();
      toastDeviceSaved(result, isNew ? "Dispositivo agregado y validado." : "Dispositivo actualizado.");
      renderDevices();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      // El equipo tiene más canales activos que cupo: el servidor devuelve la
      // lista para elegir; al reenviar el formulario viaja la selección.
      if (err.data && err.data.channelSelectionRequired)
        renderChannelPicker(err.data.channels, err.data.availableVideoChannels, err.error);
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
  // Hubo cambios: al cerrar se vuelve a pintar la tabla (contador habilitados / total).
  let changed = false;
  const pendingOnline = channels.filter((c) => !c.enabled && c.isOnline).length;
  openModal(`
    <h3>Canales — ${esc(device.name)}</h3>
    <div id="ch-modal-error"></div>
    ${isAdmin && pendingOnline > 0 ? `
      <div class="info-box" style="display:flex;align-items:center;justify-content:space-between;gap:12px">
        <span>${pendingOnline} canal(es) con señal están deshabilitados.</span>
        <button class="btn" type="button" id="ch-enable-online" style="flex:none;white-space:nowrap"
                title="Habilita todos los canales con señal hasta donde alcance la licencia. Los que no quepan quedan esperando cupo y se habilitan solos cuando lo haya.">Habilitar canales con señal</button>
      </div>` : ""}
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
                ${c.isOnline ? '<span class="tag on">Con señal</span>' : '<span class="tag operator">Sin señal</span>'}
                ${c.disabledByLicense ? '<span class="tag warn" title="Quedó deshabilitado por el cupo de canales de la licencia: se habilita solo cuando haya cupo.">Esperando cupo de licencia</span>' : ""}</div>
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
    </div>`, "wider");

  $("#ch-close").addEventListener("click", () => {
    closeModal();
    if (changed) renderDevices();
  });
  $("#ch-enable-online")?.addEventListener("click", async (e) => {
    e.target.disabled = true;
    try {
      const result = await Api.post(`/api/devices/${device.id}/channels/enable-online`);
      toast(result.message, false, result.leftWithoutQuota > 0 ? { warning: true } : undefined);
      renderDevices();
      await channelsModal(device); // recarga los estados de cada canal
    } catch (err) {
      e.target.disabled = false;
      $("#ch-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
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
      changed = true;
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
// Auditoría (bitácora ISO 27001, solo administradores)
// ---------------------------------------------------------------------------
let auditCatalog = null;           // catálogo de categorías/acciones (se pide una vez)
const auditState = { page: 1 };    // filtros vigentes entre búsquedas

const AUDIT_ORIGINS = { web: "Panel web", client: "Cliente", server: "Servidor" };

function formatDateTime(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  return d.toLocaleDateString("es-CL") + " " +
    d.toLocaleTimeString("es-CL", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function auditLabels(ev) {
  const cat = (auditCatalog?.categories || []).find((c) => c.key === ev.category);
  const act = cat?.actions.find((a) => a.key === ev.action);
  return { category: cat?.label ?? ev.category, action: act?.label ?? ev.action };
}

/** Query string con los filtros vigentes (sin paginación). */
function auditFilterQuery() {
  const q = [];
  const add = (k, v) => { if (v) q.push(`${k}=${encodeURIComponent(v)}`); };
  add("from", auditState.from);
  add("to", auditState.to);
  add("category", auditState.category);
  add("action", auditState.action);
  add("username", auditState.username);
  add("text", auditState.text);
  if (auditState.success === "1") q.push("success=true");
  if (auditState.success === "0") q.push("success=false");
  return q.join("&");
}

async function renderAudit() {
  $("#page-title").textContent = "Auditoría";
  if (Api.role !== "Admin") {
    $("#view").innerHTML = `<div class="warn-box">Requiere rol administrador.</div>`;
    return;
  }

  try { auditCatalog ??= await Api.get("/api/audit/catalog"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const categoryOptions = auditCatalog.categories
    .map((c) => `<option value="${esc(c.key)}" ${auditState.category === c.key ? "selected" : ""}>${esc(c.label)}</option>`)
    .join("");
  const userOptions = (auditCatalog.usernames || [])
    .map((u) => `<option value="${esc(u)}" ${auditState.username === u ? "selected" : ""}>${esc(u)}</option>`)
    .join("");

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Bitácora de auditoría</h3>
      <button class="btn ghost" id="audit-export" title="Descarga los eventos que coinciden con los filtros (máximo 100.000)">Exportar CSV</button>
    </div>
    <div class="filter-bar">
      <div class="field"><label>Desde</label>
        <input type="datetime-local" id="af-from" value="${esc(auditState.from ?? "")}"></div>
      <div class="field"><label>Hasta</label>
        <input type="datetime-local" id="af-to" value="${esc(auditState.to ?? "")}"></div>
      <div class="field"><label>Categoría</label>
        <select id="af-category"><option value="">Todas</option>${categoryOptions}</select></div>
      <div class="field"><label>Subcategoría</label>
        <select id="af-action"><option value="">Todas</option></select></div>
      <div class="field"><label>Usuario</label>
        <select id="af-username"><option value="">Todos</option>${userOptions}</select></div>
      <div class="field"><label>Resultado</label>
        <select id="af-success">
          <option value="">Todos</option>
          <option value="1" ${auditState.success === "1" ? "selected" : ""}>Éxito</option>
          <option value="0" ${auditState.success === "0" ? "selected" : ""}>Fallo</option>
        </select></div>
      <div class="field"><label>Buscar texto</label>
        <input id="af-text" placeholder="detalle, objeto, IP..." value="${esc(auditState.text ?? "")}"></div>
      <div class="filter-actions">
        <button class="btn" id="af-search">Buscar</button>
        <button class="btn ghost" id="af-clear" title="Quita todos los filtros">Limpiar</button>
      </div>
    </div>
    <div id="audit-results"><div class="info-box">Cargando eventos…</div></div>`;

  // La lista de subcategorías depende de la categoría elegida.
  const fillActions = () => {
    const category = $("#af-category").value;
    const cat = auditCatalog.categories.find((c) => c.key === category);
    $("#af-action").innerHTML = `<option value="">Todas</option>` + (cat
      ? cat.actions.map((a) =>
          `<option value="${esc(a.key)}" ${auditState.action === a.key ? "selected" : ""}>${esc(a.label)}</option>`).join("")
      : "");
    if (!cat) auditState.action = "";
  };
  fillActions();
  $("#af-category").addEventListener("change", () => { auditState.action = ""; fillActions(); });

  const readFilters = () => {
    auditState.from = $("#af-from").value || "";
    auditState.to = $("#af-to").value || "";
    auditState.category = $("#af-category").value;
    auditState.action = $("#af-action").value;
    auditState.username = $("#af-username").value;
    auditState.success = $("#af-success").value;
    auditState.text = $("#af-text").value.trim();
  };

  $("#af-search").addEventListener("click", () => { readFilters(); auditState.page = 1; loadAuditPage(); });
  $("#af-text").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { readFilters(); auditState.page = 1; loadAuditPage(); }
  });
  $("#af-clear").addEventListener("click", () => {
    Object.keys(auditState).forEach((k) => delete auditState[k]);
    auditState.page = 1;
    renderAudit();
  });
  $("#audit-export").addEventListener("click", () => {
    readFilters();
    const query = auditFilterQuery();
    window.open(`/api/audit/export?${query}${query ? "&" : ""}access_token=${encodeURIComponent(Api.token)}`);
  });

  await loadAuditPage();
}

async function loadAuditPage() {
  const box = $("#audit-results");
  if (!box) return;
  let data;
  try {
    const query = auditFilterQuery();
    data = await Api.get(`/api/audit?${query}${query ? "&" : ""}page=${auditState.page}&pageSize=50`);
  } catch (err) {
    box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }

  if (!data.items.length) {
    box.innerHTML = `<div class="info-box">No hay eventos que coincidan con los filtros.</div>`;
    return;
  }

  const totalPages = Math.max(1, Math.ceil(data.total / data.pageSize));
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>Fecha</th><th>Usuario</th><th>Origen</th><th>IP</th>
        <th>Categoría</th><th>Acción</th><th>Objeto</th><th>Detalle</th><th>Resultado</th>
      </tr></thead>
      <tbody>
        ${data.items.map((ev, i) => {
          const labels = auditLabels(ev);
          return `
          <tr class="audit-row" data-i="${i}" title="Clic para ver el detalle completo">
            <td class="muted" style="white-space:nowrap">${formatDateTime(ev.timestamp)}</td>
            <td>${esc(ev.username || "—")}</td>
            <td class="muted">${esc(AUDIT_ORIGINS[ev.origin] ?? ev.origin)}</td>
            <td class="muted">${esc(ev.clientIp || "—")}</td>
            <td><span class="tag operator">${esc(labels.category)}</span></td>
            <td>${esc(labels.action)}</td>
            <td>${esc(ev.targetName ?? "—")}</td>
            <td class="muted" style="max-width:340px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(ev.detail ?? "")}</td>
            <td>${ev.success ? `<span class="tag on">Éxito</span>` : `<span class="tag off">Fallo</span>`}</td>
          </tr>`;
        }).join("")}
      </tbody>
    </table></div>
    <div class="audit-pager">
      <span class="muted">${data.total} evento${data.total === 1 ? "" : "s"} · página ${data.page} de ${totalPages}</span>
      <div style="display:flex;gap:8px">
        <button class="btn ghost" id="audit-prev" ${data.page <= 1 ? "disabled" : ""}>« Anterior</button>
        <button class="btn ghost" id="audit-next" ${data.page >= totalPages ? "disabled" : ""}>Siguiente »</button>
      </div>
    </div>`;

  $("#audit-prev")?.addEventListener("click", () => { auditState.page--; loadAuditPage(); });
  $("#audit-next")?.addEventListener("click", () => { auditState.page++; loadAuditPage(); });
  $$("#audit-results .audit-row").forEach((row) => row.addEventListener("click", () => {
    auditDetailModal(data.items[Number(row.dataset.i)]);
  }));
}

function auditDetailModal(ev) {
  const labels = auditLabels(ev);
  let dataPretty = "";
  if (ev.dataJson) {
    try { dataPretty = JSON.stringify(JSON.parse(ev.dataJson), null, 2); }
    catch { dataPretty = ev.dataJson; }
  }
  openModal(`
    <h3>Evento #${ev.id}</h3>
    <div class="audit-detail-grid">
      <span>Fecha</span><b>${formatDateTime(ev.timestamp)}</b>
      <span>Usuario</span><b>${esc(ev.username || "—")}${ev.role ? ` (${esc(ev.role)})` : ""}</b>
      <span>Origen</span><b>${esc(AUDIT_ORIGINS[ev.origin] ?? ev.origin)}</b>
      <span>Dirección IP</span><b>${esc(ev.clientIp || "—")}</b>
      <span>Categoría</span><b>${esc(labels.category)}</b>
      <span>Subcategoría</span><b>${esc(labels.action)}</b>
      <span>Objeto</span><b>${esc(ev.targetName ?? "—")}${ev.targetId ? ` <span class="muted">(${esc(ev.targetType ?? "")} ${esc(ev.targetId)})</span>` : ""}</b>
      <span>Resultado</span><b>${ev.success ? "Éxito" : "Fallo"}</b>
    </div>
    ${ev.detail ? `<div class="info-box">${esc(ev.detail)}</div>` : ""}
    ${dataPretty ? `<div class="field"><label>Datos adicionales</label><pre class="audit-json">${esc(dataPretty)}</pre></div>` : ""}
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="audit-detail-close">Cerrar</button>
    </div>`);
  $("#audit-detail-close").addEventListener("click", closeModal);
}

// ---------------------------------------------------------------------------
// Router y arranque
// ---------------------------------------------------------------------------
const routes = {
  "": renderDashboard,
  "#/": renderDashboard,
  "#/devices": renderDevices,
  "#/alarm-panels": renderAlarmPanels,
  "#/access": renderAccessDevices,
  "#/access-monitor": renderAccessMonitor,
  "#/access-persons": renderAccessPersons,
  "#/access-levels": renderAccessLevels,
  "#/access-schedules": renderAccessSchedules,
  "#/access-events": renderAccessEvents,
  "#/speakers": renderSpeakers,
  "#/intercoms": renderIntercoms,
  "#/workflows": renderWorkflows,
  "#/workflows/edit": renderWorkflowEditor,
  "#/sounds": renderSounds,
  "#/anpr": renderAnpr,
  "#/alarms": renderAlarmMonitor,
  "#/cerco": renderCercoMonitor,
  "#/cerco-panels": renderCercoPanels,
  "#/event-center": renderEventCenter,
  "#/videowall": renderVideowall,
  "#/intercom-console": renderIntercomConsole,
  "#/decoders": renderDecoders,
  "#/walls": renderWalls,
  "#/sessions": renderSessions,
  "#/users": renderUsers,
  "#/audit": renderAudit,
  "#/services": renderServices,
  "#/license": renderLicense,
};

// --- Menú lateral: nodos desplegables -------------------------------------
// El estado abierto/cerrado se guarda por navegador para no reabrir a mano
// los mismos nodos en cada visita.
const NAV_STATE_KEY = "tcvms.nav.open";

function loadNavState() {
  try { return new Set(JSON.parse(localStorage.getItem(NAV_STATE_KEY) || "[]")); }
  catch { return new Set(); }
}
function saveNavState() {
  const open = $$("#nav .nav-group.open").map((g) => g.dataset.group);
  try { localStorage.setItem(NAV_STATE_KEY, JSON.stringify(open)); } catch { /* modo privado */ }
}

let navReady = false;

function setupNav() {
  if (navReady) return; // enterApp() puede repetirse tras un nuevo login
  navReady = true;
  const open = loadNavState();
  $$("#nav .nav-group").forEach((g) => g.classList.toggle("open", open.has(g.dataset.group)));
  $$("#nav .nav-toggle").forEach((btn) => {
    btn.addEventListener("click", () => {
      const group = btn.closest(".nav-group");
      group.classList.toggle("open");
      btn.setAttribute("aria-expanded", group.classList.contains("open") ? "true" : "false");
      saveNavState();
    });
  });
  syncNavAria();
}

function syncNavAria() {
  $$("#nav .nav-group").forEach((g) => {
    g.querySelector(".nav-toggle")?.setAttribute("aria-expanded", g.classList.contains("open") ? "true" : "false");
  });
}

// Deja visible el enlace activo abriendo los nodos que lo contienen.
function revealActiveNav(link) {
  $$("#nav .nav-group").forEach((g) => g.classList.remove("has-active"));
  if (!link) return;
  for (let g = link.closest(".nav-group"); g; g = g.parentElement?.closest(".nav-group")) {
    g.classList.add("open", "has-active");
  }
  saveNavState();
  syncNavAria();
}

function navigate() {
  clearInterval(sessionsTimer);  // el sondeo de sesiones vive solo en su página
  clearInterval(wallsTimer);     // ídem el del estado de los muros
  clearInterval(discoveryTimer); // ídem el de equipos en línea
  clearInterval(alarmsTimer);    // ídem el de paneles de alarma
  clearInterval(speakersTimer);  // ídem el de parlantes IP
  clearInterval(intercomsTimer); // ídem el de citofonía
  clearInterval(accessTimer);    // ídem el de control de acceso
  clearInterval(accessDoorsTimer);  // ídem el del monitoreo de puertas
  clearInterval(accessEventsTimer); // ídem el del historial de accesos
  clearInterval(workflowsTimer); // ídem el del historial de automatizaciones
  clearInterval(servicesTimer);  // ídem el del supervisor de servicios
  clearInterval(anprTimer);      // ídem el de lecturas de patentes
  clearInterval(alarmMonTimer);  // ídem el del monitoreo de alarmas
  if (typeof cercoDetachHub === "function") cercoDetachHub(); // suelta hub + delegaciones de cerco
  clearInterval(eventCenterTimer); // ídem el del centro de eventos
  evcStopSound();                // y su alarma sonora no sigue en otra página
  clearInterval(videowallTimer); // ídem el del puesto de videowall
  document.getElementById("vw-menu")?.remove();
  intercomConsoleLeave();        // timbre, conversación y sondeo de la citofonía
  // La ruta puede llevar parámetros (#/workflows/edit?id=7): la tabla se
  // consulta sin ellos, y el enlace del menú se marca también en las
  // subrutas (#/workflows/edit resalta "Automatizaciones").
  const hash = location.hash || "#/";
  const base = hash.split("?")[0];
  const render = routes[base] || renderDashboard;
  let active = null;
  $$("#nav a").forEach((a) => {
    const href = a.getAttribute("href");
    const on = href === base || (href !== "#/" && base.startsWith(href + "/"));
    a.classList.toggle("active", on);
    if (on) active = a;
  });
  revealActiveNav(active);
  // Las páginas a pantalla completa (editor de automatizaciones) cambian la
  // clase del contenedor; cada navegación parte limpia.
  $("#view").className = "";
  // El menú lateral que el usuario abrió a mano en el editor no se arrastra a otra página.
  $("#app-shell").classList.remove("sidebar-open");
  $("#btn-menu").setAttribute("aria-expanded", "false");
  render();
}

function enterApp() {
  showAppShell();
  CercoAlarm.start();          // alarma de cerco con sonido, en cualquier página
  setupNav();
  startLicenseBanner();        // aviso de licencia (prueba o licencia por vencer) en todas las páginas
  if (!location.hash || !routes[location.hash.split("?")[0]]) location.hash = "#/";
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

$("#btn-menu").addEventListener("click", () => {
  const open = $("#app-shell").classList.toggle("sidebar-open");
  $("#btn-menu").setAttribute("aria-expanded", String(open));
});

// Menú del usuario: clic en el nombre despliega la lista; clic fuera o Esc la cierra.
function setUserMenu(open) {
  $("#user-menu").classList.toggle("hidden", !open);
  $("#btn-user-menu").setAttribute("aria-expanded", String(open));
}
$("#btn-user-menu").addEventListener("click", (e) => {
  e.stopPropagation();
  setUserMenu($("#user-menu").classList.contains("hidden"));
});
document.addEventListener("click", (e) => {
  if (!e.target.closest(".topbar-user")) setUserMenu(false);
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") setUserMenu(false);
});

// Instalador del complemento de enrolamiento: lo publica el propio servidor
// (la descarga queda en la bitácora como access/webcontrol-downloaded).
$("#btn-download-addon").addEventListener("click", async () => {
  setUserMenu(false);
  let info = null;
  try { info = await Api.get("/api/webcontrol/info"); } catch { /* servidor viejo o caído */ }
  if (!info?.available) {
    toast("El instalador del complemento no está publicado en este servidor (carpeta webcontrol).", true);
    return;
  }
  // La descarga la inicia el navegador: el token va por query porque no
  // puede mandar la cabecera Authorization.
  window.location.href = `/api/webcontrol/installer?access_token=${encodeURIComponent(Api.token)}`;
  toast(`Descargando el complemento (${info.sizeMb} MB)…`);
});

$("#btn-logout").addEventListener("click", async () => {
  setUserMenu(false);
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
