// CLR TrueCentral VMS — panel: control de acceso, monitoreo en vivo y
// registros de acceso (buscador + reportes en Excel y PDF).
//
//   #/access-monitor  puertas en vivo: estado de hoja y cerradura, órdenes
//                     de a una o por lote, filtros, y lo que va pasando con
//                     la foto de la persona para compararla en la puerta.
//   #/access-events   registros de acceso: filtros por período, puntos de
//                     acceso, resultado, credencial, área y persona/ID/
//                     tarjeta, con reportes en Excel y PDF (detalle,
//                     asistencia y resumen por puerta).
//
// Se carga ANTES que app.js, junto a access-catalog.js (que declara los
// temporizadores, ACCESS_EVENT_KINDS, ACCESS_CREDENTIALS y accessDoorModal).
"use strict";

// El hub (SignalR) manda los enums como número; la API REST, como texto.
const ACCESS_KIND_NAMES = ["Other", "Granted", "Denied", "DoorOpen", "DoorClose", "Alarm"];
const ACCESS_CREDENTIAL_NAMES = ["Unknown", "Card", "Fingerprint", "Face", "Pin", "Remote", "ExitButton", "Qr", "Plate"];
const ACCESS_DEVICE_STATUS_NAMES = ["Unknown", "Online", "Offline", "AuthFailed"];

// ---------------------------------------------------------------------------
// Íconos: trazos simples en SVG en línea (mismo estilo que el ojo de la
// contraseña en app.js), sin librerías. Heredan el color del texto.
// ---------------------------------------------------------------------------
const ACCESS_ICON_PATHS = {
  door: '<path d="M6 21V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v17"/><path d="M3 21h18"/><path d="M14.5 12h.01"/>',
  doorOpen: '<path d="M13 4h4a1 1 0 0 1 1 1v16"/><path d="M3 21h18"/><path d="M6 21V5.6a1 1 0 0 1 .76-.97l5-1.25A1 1 0 0 1 13 4.35V21"/><path d="M10 12h.01"/>',
  lock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
  unlock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 7.8-1.3"/>',
  shield: '<path d="M12 3l7 3v5c0 5-3.5 8.5-7 10-3.5-1.5-7-5-7-10V6z"/><path d="M9 12l2 2 4-4"/>',
  ban: '<circle cx="12" cy="12" r="9"/><path d="M5.6 5.6l12.8 12.8"/>',
  pencil: '<path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>',
  offline: '<path d="M2 2l20 20"/><path d="M8.5 16.5a5 5 0 0 1 7 0"/><path d="M5 12.9a10 10 0 0 1 5.2-2.8"/><path d="M19 12.9a10 10 0 0 0-2.3-1.6"/><path d="M2 8.8a15 15 0 0 1 4.2-2.7"/><path d="M22 8.8A15 15 0 0 0 10.7 5"/><path d="M12 20h.01"/>',
  pause: '<rect x="6" y="4" width="4" height="16" rx="1"/><rect x="14" y="4" width="4" height="16" rx="1"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/>',
  filter: '<path d="M3 5h18l-7 8v6l-4 2v-8z"/>',
  check: '<circle cx="12" cy="12" r="9"/><path d="M8 12l3 3 5-6"/>',
  cross: '<circle cx="12" cy="12" r="9"/><path d="M15 9l-6 6M9 9l6 6"/>',
  alert: '<path d="M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 16v-4M12 8h.01"/>',
  card: '<rect x="2" y="5" width="20" height="14" rx="2"/><path d="M2 10h20"/><path d="M6 15h4"/>',
  face: '<path d="M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2"/><path d="M8.5 14.5s1.3 1.5 3.5 1.5 3.5-1.5 3.5-1.5"/><path d="M9 9h.01M15 9h.01"/>',
  fingerprint: '<path d="M6.5 6.5A8 8 0 0 1 20 12c0 2.8-.4 5.4-1.3 8"/><path d="M4.4 9.5A8 8 0 0 0 4 12c0 1.5-.3 3-1 4.3"/><path d="M8 12a4 4 0 0 1 8 0c0 3.3-.5 6.3-1.6 9"/><path d="M12 12c0 3.6-.7 6.5-2.2 9"/>',
  keypad: '<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M8.5 7.5h.01M12 7.5h.01M15.5 7.5h.01M8.5 11.5h.01M12 11.5h.01M15.5 11.5h.01M8.5 15.5h.01M12 15.5h.01M15.5 15.5h.01"/>',
  qr: '<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 14h3v3h-3zM21 14v.01M14 21h.01M17.5 21H21v-3.5"/>',
  remote: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
  button: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4"/>',
  plate: '<rect x="2" y="7" width="20" height="10" rx="2"/><path d="M6 12h3M11 12h7"/>',
  calendar: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 10h18"/>',
  building: '<rect x="4" y="3" width="16" height="18" rx="1"/><path d="M9 7h.01M15 7h.01M9 11h.01M15 11h.01M9 15h.01M15 15h.01M10 21v-3h4v3"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/><path d="M12 7v5l3 2"/>',
  sheet: '<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><path d="M14 3v6h6"/><path d="M8 13h8M8 17h8M12 13v4"/>',
  pdf: '<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><path d="M14 3v6h6"/><path d="M8 13h8M8 17h5"/>',
  report: '<path d="M3 3v18h18"/><path d="M7 15l4-4 3 3 5-6"/>',
  close: '<path d="M18 6L6 18M6 6l12 12"/>',
  credential: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="11" r="2.5"/><path d="M5.5 17a3.5 3.5 0 0 1 7 0M15 9h3M15 13h3"/>',
};

/** Ícono SVG en línea; mide 1em (sigue al tamaño de la letra) y toma el color del texto. */
function accessIcon(name, cls = "") {
  const paths = ACCESS_ICON_PATHS[name];
  if (!paths) return "";
  return `<svg class="ico ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
    stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths}</svg>`;
}

const ACCESS_KIND_ICONS = { Granted: "check", Denied: "cross", DoorOpen: "doorOpen", DoorClose: "door", Alarm: "alert", Other: "info" };
const ACCESS_CREDENTIAL_ICONS = {
  Card: "card", Fingerprint: "fingerprint", Face: "face", Pin: "keypad", Remote: "remote",
  ExitButton: "button", Qr: "qr", Plate: "plate",
};

/** Etiqueta de resultado con su ícono. */
function accessKindBadge(kind) {
  const k = ACCESS_EVENT_KINDS[kind] ?? ACCESS_EVENT_KINDS.Other;
  return `<span class="tag ${k.tag} with-ico">${accessIcon(ACCESS_KIND_ICONS[kind] ?? "info")}${esc(k.label)}</span>`;
}

/** Credencial con su ícono ("—" si el equipo no la informa). */
function accessCredential(credential) {
  const label = ACCESS_CREDENTIALS[credential] ?? credential;
  const icon = ACCESS_CREDENTIAL_ICONS[credential];
  return icon ? `<span class="with-ico">${accessIcon(icon)}${esc(label)}</span>` : esc(label);
}

function accessNormalizeEvent(e) {
  return {
    ...e,
    kind: typeof e.kind === "number" ? ACCESS_KIND_NAMES[e.kind] ?? "Other" : e.kind,
    credential: typeof e.credential === "number" ? ACCESS_CREDENTIAL_NAMES[e.credential] ?? "Unknown" : e.credential,
  };
}

function accessNormalizeDoor(d) {
  return {
    ...d,
    deviceStatus: typeof d.deviceStatus === "number" ? ACCESS_DEVICE_STATUS_NAMES[d.deviceStatus] ?? "Unknown" : d.deviceStatus,
  };
}

/** Foto del padrón (o las iniciales, si la persona no tiene foto o el evento no es de una persona). */
function accessPhoto(e, size = "") {
  const initials = (e.personName || "").split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w[0]).join("").toUpperCase();
  if (e.personId && e.hasFace)
    return `<img class="acm-photo ${size}" loading="lazy" alt=""
      src="/api/access/persons/${e.personId}/face?access_token=${encodeURIComponent(Api.token)}">`;
  return `<span class="acm-photo ${size} empty">${esc(initials || "—")}</span>`;
}

// Las alarmas y rechazos son lo que el guardia tiene que ver primero.
const accessKindTag = (kind) => (ACCESS_EVENT_KINDS[kind] ?? ACCESS_EVENT_KINDS.Other);

// ===========================================================================
// Monitoreo en vivo
// ===========================================================================

const ACCESS_DOOR_MODES = {
  Normal: { label: "Normal", tag: "on", icon: "shield", hint: "Abre solo con credencial válida" },
  RemainOpen: { label: "Mantenida abierta", tag: "warn", icon: "unlock", hint: "Pasa cualquiera sin identificarse" },
  RemainLocked: { label: "Bloqueada", tag: "off", icon: "ban", hint: "No entra nadie, ni con credencial válida" },
  Unknown: { label: "Modo sin informar", tag: "operator", icon: "info", hint: "El equipo no informa en qué modo está" },
};

const ACCESS_DOOR_FILTERS = {
  all: { label: "Todas las puertas", test: () => true },
  online: { label: "Equipo en línea", test: (d) => d.deviceStatus === "Online" },
  offline: { label: "Equipo sin conexión", test: (d) => d.deviceStatus !== "Online" },
  open: { label: "Hoja abierta", test: (d) => d.open === true },
  closed: { label: "Hoja cerrada", test: (d) => d.open === false },
  unlocked: { label: "Cerradura liberada", test: (d) => d.locked === false },
  remainOpen: { label: "Mantenidas abiertas", test: (d) => d.mode === "RemainOpen" },
  remainLocked: { label: "Bloqueadas", test: (d) => d.mode === "RemainLocked" },
};

const accessMonitor = {
  doors: [],
  events: [],
  selected: new Set(),
  filter: "all",
  q: "",
  pinned: null,       // evento fijado en el panel lateral (null = sigue al último)
  paused: false,
  showSystem: false,  // eventos "Otro" (operaciones y avisos del equipo), ocultos por omisión
  hubBound: [],
  reconnectUnsub: null,
};

/** Suelta los handlers del hub al salir de la página (lo llama navigate() de app.js). */
function accessDetachHub() {
  for (const { ev, fn } of accessMonitor.hubBound) VmsHub.off(ev, fn);
  accessMonitor.hubBound = [];
  accessMonitor.reconnectUnsub?.();
  accessMonitor.reconnectUnsub = null;
}

function accessHubOn(ev, fn) {
  const handler = (payload) => { if (location.hash === "#/access-monitor") fn(payload); };
  VmsHub.on(ev, handler);
  accessMonitor.hubBound.push({ ev, fn: handler });
}

function accessDoorState(door) {
  const sensor = door.open === true ? `<span class="acm-state warn" title="Sensor de la hoja">${accessIcon("doorOpen")}Hoja abierta</span>`
    : door.open === false ? `<span class="acm-state ok" title="Sensor de la hoja">${accessIcon("door")}Hoja cerrada</span>`
    : `<span class="acm-state muted" title="El equipo no informa el sensor de la hoja">${accessIcon("door")}Hoja: —</span>`;
  const lock = door.locked === true ? `<span class="acm-state ok" title="Relé de la cerradura">${accessIcon("lock")}Cerradura trabada</span>`
    : door.locked === false ? `<span class="acm-state warn" title="Relé de la cerradura">${accessIcon("unlock")}Cerradura liberada</span>`
    : `<span class="acm-state muted" title="El equipo no informa la cerradura">${accessIcon("lock")}Cerradura: —</span>`;
  return sensor + lock;
}

/**
 * Ícono grande de la tarjeta: la puerta como se ve desde el puesto. El color
 * resume lo importante: gris sin conexión, rojo bloqueada, ámbar si quedó
 * abierta o sin trabar, verde si está cerrada y en normal.
 */
function accessDoorHero(door) {
  const offline = door.deviceStatus !== "Online";
  const icon = offline ? "offline"
    : door.mode === "RemainLocked" ? "lock"
    : door.open === true || door.mode === "RemainOpen" ? "doorOpen"
    : "door";
  const tone = offline ? "muted"
    : door.mode === "RemainLocked" ? "danger"
    : door.open === true || door.mode === "RemainOpen" || door.locked === false ? "warn"
    : door.mode === "Normal" || door.open === false ? "ok"
    : "muted";
  return `<span class="acm-hero ${tone}">${accessIcon(icon)}</span>`;
}

function accessDoorCard(door, isAdmin) {
  const mode = ACCESS_DOOR_MODES[door.mode] ?? ACCESS_DOOR_MODES.Unknown;
  const offline = door.deviceStatus !== "Online";
  // Sin apertura remota no se ofrecen botones que el equipo va a rechazar.
  const canCommand = door.supportsRemoteControl && !offline && door.enabled;
  const selected = accessMonitor.selected.has(door.id);
  // Alcance por ubicación: una puerta ajena se ve, pero no se opera ni entra en los lotes.
  const op = `data-op="door:${door.id}"`;
  return `
    <div class="card access-door ${selected ? "selected" : ""} ${offline ? "offline" : ""}" data-id="${door.id}">
      <div class="acm-door-head">
        <label class="acm-check" title="Seleccionar para órdenes por lote">
          <input type="checkbox" class="acm-sel" ${op} ${selected ? "checked" : ""} ${canCommand ? "" : "disabled"}>
        </label>
        ${accessDoorHero(door)}
        <div style="min-width:0">
          <div class="card-label" style="margin-bottom:2px">${esc(door.deviceName)}${door.location ? ` · ${esc(door.location)}` : ""}</div>
          <div class="card-value small acm-door-name">${esc(door.name)}</div>
        </div>
      </div>
      <div class="chip-row">
        ${offline ? `<span class="tag off with-ico" title="${esc(door.deviceStatus)}">${accessIcon("offline")}Equipo sin conexión</span>`
          : `<span class="tag ${mode.tag} with-ico" title="${esc(mode.hint)}">${accessIcon(mode.icon)}${esc(mode.label)}</span>`}
        ${door.enabled ? "" : `<span class="tag operator with-ico">${accessIcon("pause")}Puerta pausada</span>`}
      </div>
      ${offline ? "" : `<div class="acm-states">${accessDoorState(door)}</div>`}
      <div class="row-actions acm-door-actions">
        <button class="btn btn-door" ${op} data-cmd="Open" ${canCommand ? "" : "disabled"}
          title="Pulso de apertura: abre y se cierra sola">${accessIcon("doorOpen")}Abrir</button>
        <button class="btn ghost btn-door" ${op} data-cmd="RemainOpen" ${canCommand ? "" : "disabled"}
          title="Deja la puerta abierta hasta nueva orden">${accessIcon("unlock")}Mantener abierta</button>
        <button class="btn ghost btn-door" ${op} data-cmd="Close" ${canCommand ? "" : "disabled"}
          title="Vuelve al modo normal">${accessIcon("shield")}Normal</button>
        ${isAdmin ? `<button class="btn danger btn-door" ${op} data-cmd="RemainLocked" ${canCommand ? "" : "disabled"}
          title="Bloquea la puerta: no entra nadie">${accessIcon("lock")}Bloquear</button>` : ""}
        ${isAdmin ? `<button class="btn ghost btn-door-edit btn-icon" title="Renombrar o pausar esta puerta" aria-label="Editar puerta">${accessIcon("pencil")}</button>` : ""}
      </div>
    </div>`;
}

function accessVisibleDoors() {
  const test = (ACCESS_DOOR_FILTERS[accessMonitor.filter] ?? ACCESS_DOOR_FILTERS.all).test;
  const q = accessMonitor.q.toLowerCase();
  return accessMonitor.doors.filter((d) => test(d) &&
    (!q || `${d.name} ${d.deviceName} ${d.location || ""}`.toLowerCase().includes(q)));
}

async function renderAccessMonitor() {
  $("#page-title").textContent = "Control de acceso · Monitoreo";
  const isAdmin = Api.role === "Admin";
  let doors;
  try { doors = (await Api.get("/api/access/doors")).map(accessNormalizeDoor); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  if (!doors.length) {
    $("#view").innerHTML = `<div class="info-box">
      Todavía no hay puertas. Las puertas las declaran los propios equipos: agregue uno en
      <a href="#/access">Dispositivos → Control de acceso</a> y aparecerán solas.</div>`;
    return;
  }
  accessMonitor.doors = doors;
  // Lo seleccionado sobrevive al refresco, pero no a puertas que ya no están.
  accessMonitor.selected = new Set([...accessMonitor.selected].filter((id) => doors.some((d) => d.id === id)));

  $("#view").innerHTML = `
    <div class="acm-toolbar">
      <label class="checkbox-row" style="margin:0" title="Selecciona las puertas visibles que se pueden operar">
        <input type="checkbox" id="acm-all"> Seleccionar todas
      </label>
      <div class="acm-batch">
        <span class="muted" id="acm-count">Ninguna seleccionada</span>
        <button class="btn btn-batch" data-cmd="Open" disabled>${accessIcon("doorOpen")}Abrir</button>
        <button class="btn ghost btn-batch" data-cmd="RemainOpen" disabled>${accessIcon("unlock")}Mantener abiertas</button>
        <button class="btn ghost btn-batch" data-cmd="Close" disabled>${accessIcon("shield")}Normal</button>
        ${isAdmin ? `<button class="btn danger btn-batch" data-cmd="RemainLocked" disabled>${accessIcon("lock")}Bloquear</button>` : ""}
      </div>
      <div class="acm-filters">
        <span class="acm-filter-ico" title="Filtrar">${accessIcon("filter")}</span>
        <select id="acm-filter">
          ${Object.entries(ACCESS_DOOR_FILTERS).map(([key, f]) =>
            `<option value="${key}" ${accessMonitor.filter === key ? "selected" : ""}>${esc(f.label)}</option>`).join("")}
        </select>
        <div class="input-ico">${accessIcon("search")}<input id="acm-q" placeholder="Buscar puerta o equipo…" value="${esc(accessMonitor.q)}"></div>
      </div>
    </div>
    <div class="muted" id="acm-summary" style="font-size:12px;margin:-4px 0 10px"></div>
    <div class="cards acm-doors" id="access-door-cards"></div>
    <div class="acm-live">
      <div class="acm-live-main">
        <div class="toolbar">
          <h3>Lo que va pasando <span class="muted" style="font-weight:normal;font-size:12px">(en vivo)</span></h3>
          <div style="display:flex;gap:10px;align-items:center">
            <label class="checkbox-row" style="margin:0" title="Operaciones y avisos del equipo que no son pasos por la puerta">
              <input type="checkbox" id="acm-system"> ${accessIcon("info", "muted")}Eventos del sistema</label>
            <label class="checkbox-row" style="margin:0"><input type="checkbox" id="acm-pause"> ${accessIcon("pause", "muted")}Pausar</label>
            <a class="btn ghost" href="#/access-events">${accessIcon("search")}Buscar registros</a>
          </div>
        </div>
        <div id="access-live"><div class="info-box">Cargando…</div></div>
      </div>
      <aside class="acm-last" id="acm-last"></aside>
    </div>`;

  renderAccessDoorCards(isAdmin);

  $("#acm-filter").addEventListener("change", (e) => { accessMonitor.filter = e.target.value; renderAccessDoorCards(isAdmin); });
  $("#acm-q").addEventListener("input", (e) => { accessMonitor.q = e.target.value.trim(); renderAccessDoorCards(isAdmin); });
  $("#acm-all").addEventListener("change", (e) => {
    for (const d of accessVisibleDoors())
      if (d.supportsRemoteControl && d.deviceStatus === "Online" && d.enabled && Operable.can("door", d.id)) {
        if (e.target.checked) accessMonitor.selected.add(d.id); else accessMonitor.selected.delete(d.id);
      }
    renderAccessDoorCards(isAdmin);
  });
  $("#acm-system").checked = accessMonitor.showSystem;
  $("#acm-system").addEventListener("change", (e) => { accessMonitor.showSystem = e.target.checked; accessRenderLive(); });
  $("#acm-pause").checked = accessMonitor.paused;
  $("#acm-pause").addEventListener("change", (e) => {
    accessMonitor.paused = e.target.checked;
    if (!accessMonitor.paused) refreshAccessLive();
  });
  $$("#view .btn-batch").forEach((b) => b.addEventListener("click", () => accessBatchCommand(b, isAdmin)));

  // Delegación: las tarjetas se repintan solas y no deben perder los handlers.
  const cards = $("#access-door-cards");
  cards.addEventListener("change", (e) => {
    if (!e.target.classList.contains("acm-sel")) return;
    const id = Number(e.target.closest(".access-door").dataset.id);
    if (e.target.checked) accessMonitor.selected.add(id); else accessMonitor.selected.delete(id);
    e.target.closest(".access-door").classList.toggle("selected", e.target.checked);
    accessUpdateBatchBar();
  });
  cards.addEventListener("click", (e) => {
    const button = e.target.closest("button");
    if (!button) return;
    const door = accessMonitor.doors.find((d) => d.id === Number(button.closest(".access-door").dataset.id));
    if (!door) return;
    if (button.classList.contains("btn-door-edit")) accessDoorModal(door);
    else if (button.classList.contains("btn-door")) accessSingleCommand(door, button, isAdmin);
  });

  await refreshAccessLive(true);

  // En vivo por el hub; el sondeo queda de respaldo (hub caído, equipo que
  // no empuja) y es más espaciado.
  accessDetachHub();
  accessHubOn("AccessDoorStateChanged", (raw) => {
    const door = accessNormalizeDoor(raw);
    const index = accessMonitor.doors.findIndex((d) => d.id === door.id);
    if (index < 0) return;
    accessMonitor.doors[index] = door;
    accessRepaintDoor(door, isAdmin);
  });
  accessHubOn("AccessEventReceived", (raw) => {
    if (accessMonitor.paused) return;
    const event = accessNormalizeEvent(raw);
    if (accessMonitor.events.some((e) => e.id === event.id)) return;
    accessMonitor.events = [event, ...accessMonitor.events].slice(0, 60);
    accessRenderLive(event.id);
  });
  accessMonitor.reconnectUnsub = VmsHub.onReconnected(() => {
    if (location.hash === "#/access-monitor") { accessRefreshDoors(isAdmin); refreshAccessLive(); }
  });

  clearInterval(accessDoorsTimer);
  accessDoorsTimer = setInterval(async () => {
    if (!accessPollGuard("#/access-monitor", accessDoorsTimer)) return;
    await accessRefreshDoors(isAdmin);
    if (!accessMonitor.paused) await refreshAccessLive();
  }, 15000);
}

async function accessRefreshDoors(isAdmin) {
  let fresh;
  try { fresh = (await Api.get("/api/access/doors")).map(accessNormalizeDoor); }
  catch { return; } // un refresco fallido no molesta: se reintenta
  if (fresh.length !== accessMonitor.doors.length) { renderAccessMonitor(); return; }
  for (const door of fresh) {
    const old = accessMonitor.doors.find((d) => d.id === door.id);
    if (!old || JSON.stringify(old) !== JSON.stringify(door)) accessRepaintDoor(door, isAdmin);
  }
  accessMonitor.doors = fresh;
  accessUpdateSummary();
}

/** Repinta UNA tarjeta: si no, el operador perdería el botón bajo el cursor. */
function accessRepaintDoor(door, isAdmin) {
  const card = $(`#access-door-cards .access-door[data-id="${door.id}"]`);
  if (!card) { renderAccessDoorCards(isAdmin); return; }
  const html = accessDoorCard(door, isAdmin);
  const tmp = document.createElement("div");
  tmp.innerHTML = html.trim();
  applyOperable(tmp); // igual que la tarjeta en pantalla: si nada cambió, no se reemplaza
  if (card.outerHTML !== tmp.firstElementChild.outerHTML) card.replaceWith(tmp.firstElementChild);
  accessUpdateSummary();
}

function renderAccessDoorCards(isAdmin) {
  const box = $("#access-door-cards");
  if (!box) return;
  const visible = accessVisibleDoors();
  box.innerHTML = visible.length
    ? visible.map((d) => accessDoorCard(d, isAdmin)).join("")
    : `<div class="info-box" style="grid-column:1/-1">Ninguna puerta coincide con el filtro.</div>`;
  accessUpdateBatchBar();
  accessUpdateSummary();
}

function accessUpdateSummary() {
  const box = $("#acm-summary");
  if (!box) return;
  const d = accessMonitor.doors;
  const count = (test) => d.filter(test).length;
  const parts = [
    `${d.length} puerta${d.length === 1 ? "" : "s"}`,
    `${count((x) => x.deviceStatus !== "Online")} sin conexión`,
    `${count((x) => x.open === true)} con la hoja abierta`,
    `${count((x) => x.mode === "RemainOpen")} mantenidas abiertas`,
    `${count((x) => x.mode === "RemainLocked")} bloqueadas`,
  ];
  box.textContent = parts.join(" · ");
}

function accessUpdateBatchBar() {
  const n = accessMonitor.selected.size;
  const label = $("#acm-count");
  if (label) label.textContent = n ? `${n} seleccionada${n === 1 ? "" : "s"}:` : "Ninguna seleccionada";
  $$("#view .btn-batch").forEach((b) => { b.disabled = n === 0; });
  const all = $("#acm-all");
  if (all) {
    const operable = accessVisibleDoors().filter((d) => d.supportsRemoteControl && d.deviceStatus === "Online" && d.enabled
      && Operable.can("door", d.id));
    all.checked = operable.length > 0 && operable.every((d) => accessMonitor.selected.has(d.id));
  }
}

const ACCESS_COMMAND_TEXT = {
  Open: { done: "abierta", many: "abiertas", confirm: null },
  RemainOpen: { done: "mantenida abierta", many: "mantenidas abiertas", confirm: "Va a poder pasar cualquiera sin identificarse." },
  Close: { done: "en modo normal", many: "en modo normal", confirm: null },
  RemainLocked: { done: "bloqueada", many: "bloqueadas", confirm: "No va a entrar nadie, ni con credencial válida." },
};

async function accessSingleCommand(door, button, isAdmin) {
  const command = button.dataset.cmd;
  const text = ACCESS_COMMAND_TEXT[command];
  if (command === "RemainOpen" && !confirm(`¿Dejar "${door.name}" abierta hasta nueva orden? ${text.confirm}`)) return;
  if (command === "RemainLocked" && !confirm(`¿Bloquear "${door.name}"? ${text.confirm}`)) return;

  const label = button.textContent;
  button.disabled = true;
  button.textContent = "Enviando…";
  try {
    const updated = accessNormalizeDoor(await Api.post(`/api/access/doors/${door.id}/command`, { command }));
    Object.assign(door, updated);
    accessRepaintDoor(door, isAdmin);
    toast(`Puerta "${door.name}" ${text.done}.`);
  } catch (err) {
    toast(err.error, true);
    button.disabled = false;
    button.textContent = label;
  }
}

/**
 * Orden a todas las puertas seleccionadas. Va puerta por puerta (la misma
 * API de una puerta), así cada orden queda auditada con su puerta y un equipo
 * que la rechaza no frena a los demás.
 */
async function accessBatchCommand(button, isAdmin) {
  const command = button.dataset.cmd;
  const text = ACCESS_COMMAND_TEXT[command];
  const doors = accessMonitor.doors.filter((d) => accessMonitor.selected.has(d.id));
  if (!doors.length) return;
  const names = doors.length <= 5 ? doors.map((d) => `"${d.name}"`).join(", ") : `${doors.length} puertas`;
  if (text.confirm || doors.length > 1) {
    const verb = { Open: "Abrir", RemainOpen: "Dejar abiertas", Close: "Volver a modo normal", RemainLocked: "Bloquear" }[command];
    if (!confirm(`¿${verb} ${names}?${text.confirm ? " " + text.confirm : ""}`)) return;
  }

  const buttons = $$("#view .btn-batch");
  buttons.forEach((b) => { b.disabled = true; });
  const label = button.textContent;
  button.textContent = "Enviando…";
  const results = await Promise.all(doors.map(async (door) => {
    try {
      const updated = accessNormalizeDoor(await Api.post(`/api/access/doors/${door.id}/command`, { command }));
      Object.assign(door, updated);
      accessRepaintDoor(door, isAdmin);
      return null;
    } catch (err) { return `${door.name}: ${err.error}`; }
  }));
  button.textContent = label;
  const failed = results.filter(Boolean);
  const ok = doors.length - failed.length;
  if (failed.length) toast(`${ok} de ${doors.length} puertas quedaron ${text.many}. Fallaron: ${failed.join(" · ")}`, true);
  else toast(doors.length === 1 ? `Puerta "${doors[0].name}" ${text.done}.` : `${doors.length} puertas ${text.many}.`);
  accessUpdateBatchBar();
}

/** Lo que se muestra en vivo: sin los eventos del sistema, salvo que se pidan. */
const accessLiveEvents = () => accessMonitor.showSystem
  ? accessMonitor.events
  : accessMonitor.events.filter((e) => e.kind !== "Other");

/** Trae los últimos accesos (al entrar, al reconectar el hub y de respaldo). */
async function refreshAccessLive(first) {
  if (!$("#access-live")) return;
  let data;
  try { data = await Api.get("/api/access/events?page=1&pageSize=60"); }
  catch { if (first) $("#access-live").innerHTML = `<div class="info-box">No se pudieron leer los accesos.</div>`; return; }
  const newest = accessMonitor.events[0]?.id;
  accessMonitor.events = data.items;
  accessRenderLive(data.items[0]?.id !== newest ? data.items[0]?.id : null);
}

function accessRenderLive(flashId) {
  const box = $("#access-live");
  if (!box) return;
  const events = accessLiveEvents();
  box.innerHTML = events.length ? `
    <div class="table-scroll"><table class="grid acm-events">
      <thead><tr>
        <th></th><th>Persona</th><th>Área</th><th>Evento</th><th>Credencial</th><th>Punto de acceso</th><th>Hora</th><th>Resultado</th>
      </tr></thead>
      <tbody>${events.map((e) => {
        const kind = accessKindTag(e.kind);
        const pinned = accessMonitor.pinned === e.id;
        return `
        <tr class="acm-row ${e.id === flashId ? "flash" : ""} ${pinned ? "pinned" : ""}" data-id="${e.id}">
          <td style="width:44px">${accessPhoto(e)}</td>
          <td>${esc(e.personName || "—")}
            <div class="muted" style="font-size:11px">${esc(e.employeeNo || e.cardNumber || "")}</div></td>
          <td class="muted">${esc(e.department || "—")}</td>
          <td style="max-width:300px">${esc(e.description || kind.label)}</td>
          <td class="muted">${accessCredential(e.credential)}${e.cardNumber ? `<div style="font-size:11px">${esc(e.cardNumber)}</div>` : ""}</td>
          <td>${esc(e.doorName || (e.doorNumber ? `Puerta ${e.doorNumber}` : "—"))}
            <div class="muted" style="font-size:11px">${esc(e.deviceName)}</div></td>
          <td class="muted" style="white-space:nowrap">${formatDateTime(e.timestamp)}</td>
          <td>${accessKindBadge(e.kind)}</td>
        </tr>`;
      }).join("")}</tbody>
    </table></div>`
    : `<div class="info-box">Todavía no hay accesos registrados. Aparecen acá apenas alguien pase por una puerta.</div>`;

  $$("#access-live .acm-row").forEach((row) => row.addEventListener("click", () => {
    const id = Number(row.dataset.id);
    accessMonitor.pinned = accessMonitor.pinned === id ? null : id;
    accessRenderLive();
  }));
  accessRenderLast();
}

/**
 * Panel lateral con el último acceso de una PERSONA (o el fijado con un
 * clic): foto grande para que el guardia compare con quien está en la puerta.
 */
function accessRenderLast() {
  const box = $("#acm-last");
  if (!box) return;
  const events = accessLiveEvents();
  const e = (accessMonitor.pinned && events.find((x) => x.id === accessMonitor.pinned))
    || events.find((x) => x.personName && (x.kind === "Granted" || x.kind === "Denied"))
    || events[0];
  if (!e) { box.innerHTML = `<div class="muted" style="text-align:center;padding:30px 0">Sin accesos todavía.</div>`; return; }
  box.innerHTML = `
    <div class="acm-last-title">${accessMonitor.pinned ? "Registro seleccionado" : "Último acceso"}
      ${accessMonitor.pinned ? `<button class="btn ghost" id="acm-unpin" style="padding:2px 8px;font-size:11px">${accessIcon("history")}Seguir al último</button>` : ""}</div>
    <div class="acm-last-photo">${accessPhoto(e, "big")}</div>
    <div class="acm-last-name">${esc(e.personName || "Sin persona")}</div>
    <div style="text-align:center;margin:6px 0 12px">${accessKindBadge(e.kind)}</div>
    <dl class="acm-last-data">
      <dt>${accessIcon("user")}ID</dt><dd>${esc(e.employeeNo || "—")}</dd>
      <dt>${accessIcon("building")}Área</dt><dd>${esc(e.department || "—")}</dd>
      <dt>${accessIcon("credential")}Cargo</dt><dd>${esc(e.position || "—")}</dd>
      <dt>${accessIcon("card")}Tarjeta</dt><dd>${esc(e.cardNumber || "—")}</dd>
      <dt>${accessIcon(ACCESS_CREDENTIAL_ICONS[e.credential] ?? "credential")}Credencial</dt><dd>${esc(ACCESS_CREDENTIALS[e.credential] ?? e.credential)}</dd>
      <dt>${accessIcon("door")}Puerta</dt><dd>${esc(e.doorName || "—")}<div class="muted" style="font-size:11px">${esc(e.deviceName)}</div></dd>
      <dt>${accessIcon("clock")}Hora</dt><dd>${formatDateTime(e.timestamp)}</dd>
      <dt>${accessIcon("info")}Evento</dt><dd>${esc(e.description || "—")}</dd>
    </dl>
    ${e.personId ? `<a class="btn ghost" style="width:100%;text-align:center;margin-top:8px"
      href="#/access-events" id="acm-person-history">${accessIcon("history")}Ver sus registros</a>` : ""}`;
  $("#acm-unpin")?.addEventListener("click", () => { accessMonitor.pinned = null; accessRenderLive(); });
  $("#acm-person-history")?.addEventListener("click", () => {
    Object.assign(accessRecords, accessRecordsDefaults(), {
      personId: e.personId, personName: e.personName, period: "7d",
    });
  });
}

// ===========================================================================
// Registros de acceso: buscador + reportes
// ===========================================================================

const ACCESS_PERIODS = {
  today: "Hoy",
  yesterday: "Ayer",
  "7d": "Últimos 7 días",
  "30d": "Últimos 30 días",
  month: "Este mes",
  lastMonth: "Mes anterior",
  custom: "Personalizado",
};

const ACCESS_REPORTS = {
  detail: { label: "Detalle de registros", hint: "Cada registro, tal como se ve en la tabla." },
  attendance: { label: "Asistencia por persona y día", hint: "Primer y último acceso concedido de cada persona por día, permanencia y rechazos." },
  doors: { label: "Resumen por puerta", hint: "Concedidos, denegados, alarmas y personas distintas por puerta." },
};

const accessRecordsDefaults = () => ({
  page: 1, pageSize: 100, period: "today", from: "", to: "",
  doorIds: [], kinds: [], credentials: [], department: "", mode: "all", q: "",
  personId: null, personName: "", sort: "time", dir: "desc",
});
const accessRecords = accessRecordsDefaults();

/** Rango [desde, hasta) del período elegido, en hora local del navegador. */
function accessPeriodRange(state) {
  const now = new Date();
  const day = (offset) => { const d = new Date(now); d.setHours(0, 0, 0, 0); d.setDate(d.getDate() + offset); return d; };
  switch (state.period) {
    case "today": return [day(0), null];
    case "yesterday": return [day(-1), day(0)];
    case "7d": return [day(-6), null];
    case "30d": return [day(-29), null];
    case "month": return [new Date(now.getFullYear(), now.getMonth(), 1), null];
    case "lastMonth": return [new Date(now.getFullYear(), now.getMonth() - 1, 1), new Date(now.getFullYear(), now.getMonth(), 1)];
    default: return [state.from ? new Date(state.from) : null, state.to ? new Date(state.to) : null];
  }
}

function accessRecordsQuery(extra = {}) {
  const s = accessRecords;
  const q = new URLSearchParams();
  const [from, to] = accessPeriodRange(s);
  if (from) q.set("from", from.toISOString());
  if (to) q.set("to", to.toISOString());
  if (s.doorIds.length) q.set("doorIds", s.doorIds.join(","));
  if (s.kinds.length) q.set("kinds", s.kinds.join(","));
  if (s.credentials.length) q.set("credentials", s.credentials.join(","));
  if (s.department) q.set("department", s.department);
  if (s.personId) q.set("personId", s.personId);
  if (s.q) { q.set("q", s.q); q.set("mode", s.mode); }
  q.set("sort", s.sort);
  q.set("dir", s.dir);
  for (const [k, v] of Object.entries(extra)) q.set(k, v);
  return q;
}

async function renderAccessEvents() {
  $("#page-title").textContent = "Control de acceso · Registros de acceso";
  let doors, departments;
  try {
    [doors, departments] = await Promise.all([
      Api.get("/api/access/doors").then((list) => list.map(accessNormalizeDoor)),
      Api.get("/api/access/departments").catch(() => []),
    ]);
  } catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const s = accessRecords;

  // Puntos de acceso agrupados por equipo: se marca un equipo entero o puertas sueltas.
  const byDevice = new Map();
  for (const d of doors) {
    if (!byDevice.has(d.deviceId)) byDevice.set(d.deviceId, { name: d.deviceName, doors: [] });
    byDevice.get(d.deviceId).doors.push(d);
  }
  const departmentNames = (departments || []).map((d) => (typeof d === "string" ? d : d.name ?? d.department)).filter(Boolean);

  $("#view").innerHTML = `
    <div class="acr-layout">
      <aside class="acr-filters">
        <div class="field">
          <label class="with-ico">${accessIcon("calendar")}Período</label>
          <select id="acr-period">
            ${Object.entries(ACCESS_PERIODS).map(([k, label]) => `<option value="${k}" ${s.period === k ? "selected" : ""}>${label}</option>`).join("")}
          </select>
        </div>
        <div id="acr-custom" class="${s.period === "custom" ? "" : "hidden"}">
          <div class="field"><label>Desde</label><input type="datetime-local" id="acr-from" value="${esc(s.from)}"></div>
          <div class="field"><label>Hasta</label><input type="datetime-local" id="acr-to" value="${esc(s.to)}"></div>
        </div>

        <div class="field">
          <label class="with-ico">${accessIcon("door")}Puntos de acceso <span class="muted" id="acr-doors-count"></span></label>
          <div class="input-ico" style="margin-bottom:6px">${accessIcon("search")}<input id="acr-door-q" placeholder="Buscar puerta…"></div>
          <div class="acr-tree" id="acr-tree">
            <label class="acr-node"><input type="checkbox" id="acr-door-all"> <b>Todas</b></label>
            ${[...byDevice.entries()].map(([id, dev]) => `
              <div class="acr-group" data-device="${id}">
                <label class="acr-node"><input type="checkbox" class="acr-dev" data-device="${id}"> ${accessIcon("building", "muted")}${esc(dev.name)}</label>
                ${dev.doors.map((d) => `
                  <label class="acr-node acr-leaf" data-text="${esc(`${d.name} ${dev.name}`.toLowerCase())}">
                    <input type="checkbox" class="acr-door" value="${d.id}" ${s.doorIds.includes(d.id) ? "checked" : ""}> ${accessIcon("door", "muted")}${esc(d.name)}
                  </label>`).join("")}
              </div>`).join("")}
          </div>
        </div>

        <div class="field">
          <label class="with-ico">${accessIcon("check")}Resultado</label>
          <div class="acr-checks">
            ${Object.entries(ACCESS_EVENT_KINDS).map(([k, kind]) => `
              <label class="acr-node"><input type="checkbox" class="acr-kind" value="${k}" ${s.kinds.includes(k) ? "checked" : ""}> ${accessIcon(ACCESS_KIND_ICONS[k], "kind-" + kind.tag)}${esc(kind.label)}</label>`).join("")}
          </div>
        </div>

        <div class="field">
          <label class="with-ico">${accessIcon("credential")}Credencial</label>
          <div class="acr-checks">
            ${Object.entries(ACCESS_CREDENTIALS).filter(([k]) => k !== "Unknown").map(([k, label]) => `
              <label class="acr-node"><input type="checkbox" class="acr-cred" value="${k}" ${s.credentials.includes(k) ? "checked" : ""}> ${accessIcon(ACCESS_CREDENTIAL_ICONS[k], "muted")}${esc(label)}</label>`).join("")}
          </div>
        </div>

        ${departmentNames.length ? `
        <div class="field">
          <label class="with-ico">${accessIcon("building")}Área</label>
          <select id="acr-dept"><option value="">Todas</option>
            ${departmentNames.map((d) => `<option ${s.department === d ? "selected" : ""}>${esc(d)}</option>`).join("")}
          </select>
        </div>` : ""}

        <div class="field">
          <label class="with-ico">${accessIcon("user")}Buscar por</label>
          <div class="segmented" role="radiogroup" style="margin-bottom:6px">
            ${[["all", "Todo"], ["person", "Persona"], ["employee", "ID"], ["card", "Tarjeta"]].map(([k, label]) =>
              `<label><input type="radio" name="acr-mode" value="${k}" ${s.mode === k ? "checked" : ""}> ${label}</label>`).join("")}
          </div>
          <div class="input-ico">${accessIcon("search")}<input id="acr-q" placeholder="Nombre, ID o N° de tarjeta…" value="${esc(s.q)}"></div>
          ${s.personId ? `<div class="chip-row"><span class="tag admin with-ico">${accessIcon("user")}${esc(s.personName || s.personId)}
            <a href="#" id="acr-person-clear" class="with-ico" style="margin-left:6px;color:inherit" title="Quitar" aria-label="Quitar persona">${accessIcon("close")}</a></span></div>` : ""}
        </div>

        <div class="acr-filter-actions">
          <button class="btn" id="acr-search">${accessIcon("search")}Buscar</button>
          <button class="btn ghost" id="acr-clear">${accessIcon("close")}Limpiar</button>
        </div>
      </aside>

      <section class="acr-results">
        <div class="toolbar acr-toolbar">
          <div class="muted" id="acr-total"></div>
          <div class="acr-export">
            <span class="acm-filter-ico" title="Reporte">${accessIcon("report")}</span>
            <select id="acr-report" title="Qué reporte exportar">
              ${Object.entries(ACCESS_REPORTS).map(([k, r]) => `<option value="${k}" title="${esc(r.hint)}">${esc(r.label)}</option>`).join("")}
            </select>
            <button class="btn ghost" id="acr-xlsx" title="Descargar en Excel con los filtros actuales">${accessIcon("sheet", "xlsx")}Excel</button>
            <button class="btn ghost" id="acr-pdf" title="Descargar en PDF con los filtros actuales">${accessIcon("pdf", "pdf")}PDF</button>
          </div>
        </div>
        <div class="muted" id="acr-report-hint" style="font-size:12px;margin:-6px 0 10px;text-align:right"></div>
        <div id="acr-table"><div class="info-box">Cargando…</div></div>
      </section>
    </div>`;

  // Árbol de puertas: equipo marca/desmarca sus puertas; "Todas" = sin filtro.
  const syncTree = () => {
    const checked = $$(".acr-door").filter((c) => c.checked);
    $("#acr-door-all").checked = checked.length === 0 || checked.length === $$(".acr-door").length;
    $$(".acr-group").forEach((g) => {
      const leaves = [...g.querySelectorAll(".acr-door")];
      const box = g.querySelector(".acr-dev");
      box.checked = leaves.length > 0 && leaves.every((c) => c.checked);
      box.indeterminate = !box.checked && leaves.some((c) => c.checked);
    });
    $("#acr-doors-count").textContent = checked.length ? `(${checked.length})` : "(todas)";
  };
  syncTree();
  $("#acr-tree").addEventListener("change", (e) => {
    if (e.target.id === "acr-door-all") $$(".acr-door").forEach((c) => { c.checked = false; });
    else if (e.target.classList.contains("acr-dev"))
      e.target.closest(".acr-group").querySelectorAll(".acr-door").forEach((c) => { c.checked = e.target.checked; });
    syncTree();
  });
  $("#acr-door-q").addEventListener("input", (e) => {
    const q = e.target.value.trim().toLowerCase();
    $$(".acr-leaf").forEach((l) => l.classList.toggle("hidden", !!q && !l.dataset.text.includes(q)));
  });

  $("#acr-period").addEventListener("change", (e) => $("#acr-custom").classList.toggle("hidden", e.target.value !== "custom"));
  const showHint = () => { $("#acr-report-hint").textContent = ACCESS_REPORTS[$("#acr-report").value].hint; };
  $("#acr-report").addEventListener("change", showHint);
  showHint();

  const readFilters = () => {
    s.period = $("#acr-period").value;
    s.from = $("#acr-from").value || "";
    s.to = $("#acr-to").value || "";
    s.doorIds = $$(".acr-door").filter((c) => c.checked).map((c) => Number(c.value));
    if (s.doorIds.length === $$(".acr-door").length) s.doorIds = [];
    s.kinds = $$(".acr-kind").filter((c) => c.checked).map((c) => c.value);
    s.credentials = $$(".acr-cred").filter((c) => c.checked).map((c) => c.value);
    s.department = $("#acr-dept")?.value || "";
    s.mode = $("input[name=acr-mode]:checked")?.value || "all";
    s.q = $("#acr-q").value.trim();
    s.page = 1;
  };
  const search = () => {
    readFilters();
    if (s.period === "custom" && s.from && s.to && new Date(s.from) > new Date(s.to)) {
      toast("La fecha \"Desde\" es posterior a \"Hasta\".", true);
      return;
    }
    loadAccessRecords();
  };
  $("#acr-search").addEventListener("click", search);
  $("#acr-q").addEventListener("keydown", (e) => { if (e.key === "Enter") search(); });
  $("#acr-clear").addEventListener("click", () => { Object.assign(accessRecords, accessRecordsDefaults()); renderAccessEvents(); });
  $("#acr-person-clear")?.addEventListener("click", (e) => {
    e.preventDefault();
    s.personId = null; s.personName = "";
    renderAccessEvents();
  });

  const exportReport = (format) => {
    readFilters();
    const query = accessRecordsQuery({ format, report: $("#acr-report").value, access_token: Api.token });
    window.open(`/api/access/events/export?${query}`);
  };
  $("#acr-xlsx").addEventListener("click", () => exportReport("xlsx"));
  $("#acr-pdf").addEventListener("click", () => exportReport("pdf"));

  await loadAccessRecords();

  // Con el período que llega hasta ahora y en la primera página, la tabla se
  // refresca sola; en otra página o un período cerrado no se mueve.
  clearInterval(accessEventsTimer);
  accessEventsTimer = setInterval(() => {
    if (!accessPollGuard("#/access-events", accessEventsTimer)) return;
    const [, to] = accessPeriodRange(accessRecords);
    if (accessRecords.page !== 1 || to) return;
    loadAccessRecords({ quiet: true });
  }, 15000);
}

const ACCESS_RECORD_COLUMNS = [
  { key: null, label: "" },
  { key: "person", label: "Persona" },
  { key: "employee", label: "ID" },
  { key: "card", label: "Tarjeta" },
  { key: null, label: "Área" },
  { key: "time", label: "Fecha y hora" },
  { key: "door", label: "Punto de acceso" },
  { key: null, label: "Credencial" },
  { key: "kind", label: "Resultado" },
  { key: null, label: "Detalle" },
];

async function loadAccessRecords(options = {}) {
  const box = $("#acr-table");
  if (!box) return;
  const s = accessRecords;
  let data;
  try { data = await Api.get(`/api/access/events?${accessRecordsQuery({ page: s.page, pageSize: s.pageSize })}`); }
  catch (err) {
    if (!options.quiet) box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }

  const [from, to] = accessPeriodRange(s);
  const range = from || to
    ? `${from ? from.toLocaleString("es-CL") : "el inicio"} a ${to ? to.toLocaleString("es-CL") : "ahora"}`
    : "todo el historial";
  $("#acr-total").textContent = `${data.total.toLocaleString("es-CL")} registro${data.total === 1 ? "" : "s"} · ${range}`;

  if (!data.items.length) {
    box.innerHTML = `<div class="info-box">No hay registros que coincidan con los filtros.</div>`;
    return;
  }

  const totalPages = Math.max(1, Math.ceil(data.total / data.pageSize));
  const arrow = (key) => (s.sort === key ? (s.dir === "asc" ? " ▲" : " ▼") : "");
  box.innerHTML = `
    <div class="table-scroll"><table class="grid acr-grid">
      <thead><tr>${ACCESS_RECORD_COLUMNS.map((c) => c.key
        ? `<th class="sortable" data-sort="${c.key}" title="Ordenar">${c.label}${arrow(c.key)}</th>`
        : `<th>${c.label}</th>`).join("")}</tr></thead>
      <tbody>${data.items.map((e) => {
        return `
        <tr>
          <td style="width:44px">${accessPhoto(e)}</td>
          <td>${e.personId
            ? `<a href="#" class="acr-person" data-id="${e.personId}" data-name="${esc(e.personName || "")}" title="Ver solo sus registros">${esc(e.personName || "—")}</a>`
            : esc(e.personName || "—")}</td>
          <td class="muted">${esc(e.employeeNo || "—")}</td>
          <td class="muted">${esc(e.cardNumber || "—")}</td>
          <td class="muted">${esc(e.department || "—")}</td>
          <td class="muted" style="white-space:nowrap">${formatDateTime(e.timestamp)}</td>
          <td>${esc(e.doorName || (e.doorNumber ? `Puerta ${e.doorNumber}` : "—"))}
            <div class="muted" style="font-size:11px">${esc(e.deviceName)}</div></td>
          <td class="muted">${accessCredential(e.credential)}</td>
          <td>${accessKindBadge(e.kind)}</td>
          <td class="muted" style="max-width:300px">${esc(e.description)}</td>
        </tr>`;
      }).join("")}</tbody>
    </table></div>
    <div class="audit-pager">
      <div style="display:flex;gap:8px;align-items:center">
        <span class="muted">Página ${data.page} de ${totalPages}</span>
        <select id="acr-size" style="width:auto">
          ${[50, 100, 200, 500].map((n) => `<option value="${n}" ${s.pageSize === n ? "selected" : ""}>${n} por página</option>`).join("")}
        </select>
      </div>
      <div style="display:flex;gap:8px">
        <button class="btn ghost" id="acr-first" ${data.page <= 1 ? "disabled" : ""}>«</button>
        <button class="btn ghost" id="acr-prev" ${data.page <= 1 ? "disabled" : ""}>‹ Anterior</button>
        <button class="btn ghost" id="acr-next" ${data.page >= totalPages ? "disabled" : ""}>Siguiente ›</button>
        <button class="btn ghost" id="acr-last" ${data.page >= totalPages ? "disabled" : ""}>»</button>
      </div>
    </div>`;

  $$("#acr-table th.sortable").forEach((th) => th.addEventListener("click", () => {
    const key = th.dataset.sort;
    if (s.sort === key) s.dir = s.dir === "asc" ? "desc" : "asc";
    else { s.sort = key; s.dir = key === "time" ? "desc" : "asc"; }
    s.page = 1;
    loadAccessRecords();
  }));
  $$("#acr-table .acr-person").forEach((a) => a.addEventListener("click", (e) => {
    e.preventDefault();
    s.personId = Number(a.dataset.id);
    s.personName = a.dataset.name;
    s.page = 1;
    renderAccessEvents();
  }));
  $("#acr-size").addEventListener("change", (e) => { s.pageSize = Number(e.target.value); s.page = 1; loadAccessRecords(); });
  $("#acr-first")?.addEventListener("click", () => { s.page = 1; loadAccessRecords(); });
  $("#acr-prev")?.addEventListener("click", () => { s.page--; loadAccessRecords(); });
  $("#acr-next")?.addEventListener("click", () => { s.page++; loadAccessRecords(); });
  $("#acr-last")?.addEventListener("click", () => { s.page = totalPages; loadAccessRecords(); });
}
