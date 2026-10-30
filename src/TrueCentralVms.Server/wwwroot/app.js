// CLR TrueCentral VMS — panel de administración (SPA sin framework).
"use strict";

const $ = (sel, root) => (root || document).querySelector(sel);
const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------------------------------------------------------------------------
// Campos de contraseña con botón "mostrar/ocultar"
// ---------------------------------------------------------------------------
// Todo <input type="password"> que aparezca en la página (login, equipos,
// usuarios, claves de paneles, flujos...) recibe solo el ojo: un observador lo
// envuelve apenas se inserta, así ningún formulario nuevo queda sin él. El
// input conserva id, valor y eventos; solo cambia de contenedor.
const EYE_ICON =`<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7S1 12 1 12z"/><circle cx="12" cy="12" r="3"/></svg>`;
const EYE_OFF_ICON = `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94"/><path d="M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19"/><path d="M14.12 14.12a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></svg>`;

/// Envuelve el campo en .pw-wrap y le agrega el ojo (si ya lo tiene, no hace nada).
function addPasswordToggle(input) {
  if (input.closest(".pw-wrap")) return;
  const focused = document.activeElement === input;   // moverlo de nodo le quita el foco
  const wrap = document.createElement("div");
  wrap.className = "pw-wrap";
  input.before(wrap);
  wrap.append(input);
  wrap.insertAdjacentHTML("beforeend",
    `<button type="button" class="pw-toggle" title="Mostrar contraseña" aria-label="Mostrar contraseña" aria-pressed="false">${EYE_ICON}</button>`);
  if (focused) input.focus();
}

function addPasswordToggles(root) {
  if (root.matches('input[type="password"]')) addPasswordToggle(root);
  else root.querySelectorAll('input[type="password"]').forEach(addPasswordToggle);
}

addPasswordToggles(document.body);
new MutationObserver((mutations) => {
  for (const mutation of mutations)
    for (const node of mutation.addedNodes)
      if (node.nodeType === Node.ELEMENT_NODE) addPasswordToggles(node);
}).observe(document.body, { childList: true, subtree: true });

document.addEventListener("click", (e) => {
  const button = e.target.closest(".pw-toggle");
  const input = button?.parentElement.querySelector("input");
  if (!input || input.disabled) return;
  const show = input.type === "password";
  input.type = show ? "text" : "password";
  button.innerHTML = show ? EYE_OFF_ICON : EYE_ICON;
  button.title = button.ariaLabel = show ? "Ocultar contraseña" : "Mostrar contraseña";
  button.setAttribute("aria-pressed", String(show));
  input.focus();
});

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

// ---------------------------------------------------------------------------
// Ubicación de un equipo en su formulario de alta o edición. Sus recursos
// (canales, áreas y zonas, puertas) la heredan; los que se ubicaron aparte en
// Recursos se quedan donde están.
// ---------------------------------------------------------------------------

/** El árbol en orden de lectura (padres antes que hijos, hermanos por nombre),
 *  o null si no se pudo leer: el formulario conserva entonces la ubicación actual. */
async function loadLocationChoices() {
  let list;
  try { list = await Api.get("/api/locations"); } catch { return null; }
  const byParent = new Map();
  for (const l of list) {
    const key = l.parentId ?? 0;
    if (!byParent.has(key)) byParent.set(key, []);
    byParent.get(key).push(l);
  }
  const out = [], seen = new Set();
  const walk = (parent, depth) => {
    for (const l of (byParent.get(parent) || []).sort((x, y) => x.name.localeCompare(y.name, "es"))) {
      if (seen.has(l.id)) continue;
      seen.add(l.id);
      out.push({ id: l.id, name: l.name, depth });
      walk(l.id, depth + 1);
    }
  };
  walk(0, 0);
  return out;
}

/**
 * Campo "Ubicación" con el árbol indentado; "" = por ubicar. En el alta y la
 * edición de un equipo (equipment) ofrece además «Nueva, con el nombre del
 * equipo» dentro de la ubicación que se elija (solo a quien administra
 * Recursos); en el alta es la opción por omisión, así lo nuevo no se acumula
 * en "Por ubicar". La crea locationFieldCommit al guardar.
 */
function locationFieldHtml(id, choices, selectedId, hint, { equipment = false, isNew = false } = {}) {
  if (!choices) {
    return `
      <div class="field">
        <label for="${id}">Ubicación</label>
        <select id="${id}" data-keep="1" disabled><option>No se pudo leer el árbol: se conserva la actual</option></select>
      </div>`;
  }
  const offerNew = equipment && Perms.can("resources.manage");
  const startNew = offerNew && isNew;
  const options = (selected) => choices.map((c) =>
    `<option value="${c.id}" ${c.id === selected ? "selected" : ""}>${"   ".repeat(c.depth)}${esc(c.name)}</option>`).join("");
  return `
    <div class="field">
      <label for="${id}">Ubicación</label>
      <select id="${id}"${offerNew ? ` data-loc-new="${id}-new"` : ""}>
        ${offerNew ? `<option value="new"${startNew ? " selected" : ""}>Nueva, con el nombre del equipo</option>` : ""}
        <option value=""${!startNew && selectedId == null ? " selected" : ""}>Por ubicar</option>
        ${options(startNew ? null : selectedId)}
      </select>
      ${offerNew ? `
      <div class="loc-new" id="${id}-new"${startNew ? "" : " hidden"}>
        <label for="${id}-parent">Dentro de</label>
        <select id="${id}-parent">
          <option value="">La raíz del árbol</option>
          ${options(null)}
        </select>
        <div class="muted field-hint">Si ahí ya hay una ubicación con ese nombre, se usa esa.</div>
      </div>` : ""}
      ${hint ? `<div class="muted field-hint">${esc(hint)}</div>` : ""}
    </div>`;
}

// «Nueva, con el nombre del equipo» muestra dónde crearla.
document.addEventListener("change", (e) => {
  const target = e.target.dataset?.locNew;
  if (target) $("#" + target).hidden = e.target.value !== "new";
});

/**
 * Lo que va al servidor: el id elegido, 0 = por ubicar, null = conservar la
 * actual (también con «Nueva, con el nombre del equipo», hasta que
 * locationFieldCommit la cree al guardar: probar la conexión no crea nada).
 */
function locationFieldValue(id) {
  const select = $("#" + id);
  if (!select || select.dataset.keep || select.value === "new") return null;
  return select.value ? Number(select.value) : 0;
}

/**
 * Justo antes de guardar el equipo: con «Nueva, con el nombre del equipo»,
 * crea la ubicación con body.name (o toma la que ya se llama así en ese
 * lugar) y deja su id en body.locationId.
 */
async function locationFieldCommit(id, body) {
  const select = $("#" + id);
  if (!select || select.value !== "new") return;
  const parent = $(`#${id}-parent`)?.value;
  const result = await Api.post("/api/locations/ensure", { name: body.name, parentId: parent ? Number(parent) : null });
  body.locationId = result.id;
}

/**
 * Etiqueta de un recurso que la licencia dejó apagado por pasarse del cupo
 * (o porque su módulo salió de la licencia): el servidor lo vuelve a encender
 * solo cuando haya cupo. Vacío si no es el caso.
 */
function licenseWaitTag(item) {
  return item?.disabledByLicense
    ? ` <span class="tag warn" title="Quedó apagado por el cupo de la licencia: se enciende solo cuando haya cupo.">Esperando cupo de licencia</span>`
    : "";
}

/** Ruta de la ubicación bajo el nombre del equipo en las listas. */
function locationLineHtml(path) {
  return path
    ? `<div class="loc-line" title="Ubicación en Recursos">${esc(path)}</div>`
    : `<div class="loc-line none" title="Sin ubicación: los operadores con alcance restringido no lo ven">Por ubicar</div>`;
}

function openModal(html, size) {
  // El alta de un equipo abierta desde su ruta (#/devices/new…) se dibuja como página;
  // si se fue de ella antes de que el formulario cargara, el formulario se descarta.
  if (formPage && !formPage.drawn) {
    if (formPage.left) { formPage = null; return; }
    if (location.hash === formPage.hash) { drawFormPage(html); return; }
  }
  // `size`: true = "wide" (formularios con grilla, editor de muros) o el
  // nombre de una clase de ancho ("wider" para tablas dentro del modal).
  $("#modal").className = "modal" + (size === true ? " wide" : size ? " " + size : "");
  $("#modal").innerHTML = html;
  $("#modal-backdrop").classList.remove("hidden");
}
function closeModal() {
  const backdrop = $("#modal-backdrop");
  if (!backdrop.classList.contains("hidden")) { backdrop.classList.add("hidden"); return; }
  // Sin modal a la vista, cerrar el alta como página es volver a su lista. Con replace: el
  // formulario no queda en el historial (Atrás no lo vuelve a abrir vacío).
  if (formPage?.drawn && location.hash === formPage.hash) {
    const back = formPage.back;
    formPage = null;
    location.replace(back);
  }
}

// ---------------------------------------------------------------------------
// Alta de equipos como página. Los formularios son los mismos de siempre (los
// abre openModal y los cierra closeModal, y también se usan para editar, en
// modal); abiertos desde su ruta, el primer openModal los dibuja en la página:
// el título en la cabecera y en las migas, el formulario en una tarjeta y sus
// botones en una barra que queda a la vista al bajar. Guardar o Cancelar
// vuelven a la lista; un diálogo que el formulario abra encima sigue siendo modal.
// ---------------------------------------------------------------------------
let formPage = null;         // { hash, back, icon, hint, drawn, left }
let formPagePrefill = null;  // { base, data }: el equipo elegido en Equipos en línea

/** Abre el alta como página; `prefill` = los datos del equipo encontrado en la red. */
function openFormPage(newHash, prefill) {
  formPagePrefill = prefill ? { base: newHash.split("?")[0], data: prefill } : null;
  location.hash = newHash;
}

/** Ruta de un alta: `back` = su lista, `icon`/`hint` = la columna de al lado; `open(prefill, query)` llama al formulario de siempre. */
function formPageRoute({ back, icon, hint }, open) {
  return async () => {
    const hash = location.hash;
    const base = hash.split("?")[0];
    const prefill = formPagePrefill?.base === base ? formPagePrefill.data : null;
    formPagePrefill = null;
    const me = { hash, back, icon, hint, drawn: false, left: false };
    formPage = me;
    $("#view").innerHTML = `<div class="muted">Cargando el formulario…</div>`;
    await open(prefill, new URLSearchParams(hash.split("?")[1] ?? ""));
    if (formPage !== me || me.drawn) return;
    formPage = null;
    // El formulario no se pudo armar (el error ya se avisó): queda el camino de vuelta.
    if (location.hash === hash)
      $("#view").innerHTML = `<div class="error-box">No se pudo abrir el formulario.
        <a href="${esc(back)}">Volver a la lista</a></div>`;
  };
}

/** Al dejar la página: un alta a medio cargar se descarta cuando llegue (ver openModal). */
function leaveFormPage() {
  if (formPage && !formPage.drawn) formPage.left = true;
  else formPage = null;
}

function drawFormPage(html) {
  formPage.drawn = true;
  const box = document.createElement("div");
  box.innerHTML = html;
  const heading = box.querySelector("h3");
  const title = heading?.textContent.trim() || "Agregar";
  heading?.remove();
  const nav = $("#nav a.active");
  const listName = nav?.textContent.trim() || "la lista";
  $("#page-title").textContent = title;
  // Migas: la lista pasa a ser un enlace y el alta queda al final.
  const crumbs = $("#breadcrumb");
  const last = crumbs?.lastElementChild;
  if (last && !last.querySelector("a")) {
    const link = document.createElement("a");
    link.href = formPage.back;
    link.textContent = last.textContent;
    last.replaceChildren(link);
    const li = document.createElement("li");
    li.textContent = title;
    crumbs.append(li);
  }
  // Al lado del formulario, de qué sección es y qué hace el sistema al guardar.
  const page = document.createElement("div");
  page.className = "form-page";
  page.innerHTML = `
    <a class="form-page-back" href="${esc(formPage.back)}"><span aria-hidden="true">←</span> Volver a ${esc(listName)}</a>
    <div class="form-page-layout">
      <aside class="form-page-aside">
        <div class="form-page-icon"><i class="mdi ${esc(formPage.icon)}" aria-hidden="true"></i></div>
        <div>
          <div class="form-page-section">${esc(listName)}</div>
          <p>${esc(formPage.hint)}</p>
        </div>
      </aside>
      <div class="form-page-card"></div>
    </div>`;
  const card = page.querySelector(".form-page-card");
  card.append(...box.childNodes);
  $("#view").replaceChildren(page);
  // La página la desplaza el documento: así la barra de botones queda a la vista al bajar.
  $("#view").classList.add("view-form-page");
  page.querySelector(".form-page-back").addEventListener("click", (e) => { e.preventDefault(); closeModal(); });
  card.querySelector("input:not([type=hidden]):not([disabled]), select")?.focus({ preventScroll: true });
}
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
  showCurrentUser();
  showUserScope();
}

/** Nombre del usuario conectado, en la barra superior (sus roles, al pasar el mouse). */
function showCurrentUser() {
  const roles = Perms.roles.length ? Perms.roles.join(", ") : (Api.role === "Admin" ? "Administrador" : "Operador");
  $("#current-user").textContent = Api.username;
  $("#btn-user-menu").title = `Roles: ${roles}`;
}

// ---------------------------------------------------------------------------
// Qué puede HACER esta sesión según sus roles (la unión de sus permisos). La
// interfaz oculta las páginas y botones que el servidor rechazaría; el servidor
// valida igual cada solicitud. Las páginas preguntan con Perms.can("clave") o,
// si basta con uno de varios, Perms.can("clave1|clave2"). Los enlaces del menú
// llevan data-perm con la misma sintaxis.
// Además, lo de un módulo que la licencia no incluye se oculta a todos (también
// al administrador): los enlaces llevan data-module ("module_x", o varios con |
// si basta con uno) y las páginas preguntan con Perms.licensed("module_x").
// Sin licencia vigente no hay módulos y se ocultan también los enlaces con
// data-needs-license: queda solo lo necesario para regularizarla.
// ---------------------------------------------------------------------------
const Perms = {
  admin: false,
  keys: new Set(),
  roles: [],
  modules: null,  // módulos licenciados; null = no se sabe (servidor anterior): no se oculta nada
  operational: true, // false = licencia no vigente: solo lo necesario para regularizarla
  loaded: false,
  async load() {
    let me;
    try { me = await Api.get("/api/auth/me"); } catch { return false; }
    const p = me.permissions;
    if (p) {
      this.admin = !!p.isAdmin;
      this.keys = new Set(p.permissions ?? []);
      this.roles = p.roles ?? [];
      this.modules = Array.isArray(p.licensedModules) ? new Set(p.licensedModules) : null;
      this.operational = p.licenseOperational !== false;
    } else {
      // Servidor anterior a los roles: el administrador puede todo y el resto, lo de siempre.
      this.admin = me.role === "Admin";
      this.keys = new Set();
      this.roles = [];
      this.modules = null;
      this.operational = true;
    }
    this.loaded = true;
    applyNavPermissions();
    showCurrentUser();
    return true;
  },
  can(spec) {
    if (this.admin || !spec) return true;
    if (!this.loaded) return Api.role === "Admin";
    return String(spec).split("|").some((k) => this.keys.has(k.trim()));
  },
  /** true si la licencia incluye el módulo (o uno de "module_a|module_b"). */
  licensed(spec) {
    if (!spec || !this.modules) return true;
    return String(spec).split("|").some((k) => this.modules.has(k.trim()));
  },
  /** true si el enlace o la página se puede usar con la licencia actual. */
  allowedByLicense(el) {
    if (!el) return true;
    if (el.hasAttribute?.("data-needs-license") && !this.operational) return false;
    return this.licensed(el.dataset?.module);
  },
  reset() { this.admin = false; this.keys = new Set(); this.roles = []; this.modules = null; this.operational = true; this.loaded = false; },
};

/** Oculta del menú lo que sus roles no permiten o la licencia no incluye (y los grupos y títulos que quedan vacíos). */
function applyNavPermissions() {
  $$("#nav a[data-perm], #nav a[data-module], #nav a[data-needs-license]").forEach((a) => {
    a.hidden = !Perms.can(a.dataset.perm) || !Perms.allowedByLicense(a);
  });
  // Grupos de adentro hacia afuera: uno anidado vacío deja vacío a su padre.
  [...$$("#nav .nav-group")].reverse().forEach((g) => {
    g.hidden = !g.querySelector(":scope > .nav-children a:not([hidden])");
  });
  // Un título de sección sin nada visible hasta el próximo título, también se oculta.
  const items = [...$("#nav").children];
  items.forEach((el, i) => {
    if (!el.classList.contains("nav-divider")) return;
    let any = false;
    for (let j = i + 1; j < items.length && !items[j].classList.contains("nav-divider"); j++)
      if (!items[j].hidden) { any = true; break; }
    el.hidden = !any;
  });
}

/** Los permisos del usuario cambiaron (sus roles o un rol suyo): se rehace menú y página. */
async function onPermissionsChanged() {
  if (!(await Perms.load())) return;
  Operable.schedule();
  navigate();
  toast("Sus permisos cambiaron: la pantalla se actualizó.");
}

/**
 * Cambió la licencia (se activó, se importó, llegó una expansión o venció): el
 * menú muestra solo los módulos que incluye. La página actual se rehace solo si
 * su módulo entró o salió de la licencia, para no interrumpir al que está
 * trabajando en otra.
 */
async function onLicenseChanged() {
  const signature = () => `${Perms.operational}|${Perms.modules ? [...Perms.modules].sort().join(",") : ""}`;
  const before = signature();
  const active = $("#nav a.active");
  const wasAllowed = Perms.allowedByLicense(active);
  if (!(await Perms.load())) return;
  if (before === signature()) return;
  if (active && Perms.allowedByLicense(active) !== wasAllowed) navigate();
  toast("La licencia cambió: el menú muestra los módulos que incluye.");
}

// ---------------------------------------------------------------------------
// Qué puede OPERAR esta sesión (armar, abrir, mover un PTZ, hablar…), no solo
// ver: lo dice el servidor (/api/auth/operable) con las mismas reglas con que
// valida cada orden. Los controles de una orden llevan data-op="tipo:id" (varios
// separados por espacio = todos tienen que poder operarse) y se deshabilitan
// solos, con la explicación en su tooltip, aunque el módulo los vuelva a dibujar.
// Tipos: location, channel, area y zone ("panel/número"), panel (todo el panel),
// door, fence, speaker, intercom, io (módulo de E/S). Mientras no se haya leído no se bloquea nada:
// el servidor valida igual.
// ---------------------------------------------------------------------------
const OP_DENIED = "Fuera de su alcance: puede verlo, pero no operarlo.";
const OP_TOPICS = new Set(["scope", "locations", "devices", "channels", "alarm-panels", "access-devices", "speakers", "intercoms", "io-modules"]);
const OP_KINDS = {
  location: "locations", channel: "channels", area: "areas", zone: "zones", panel: "wholePanels",
  door: "doors", fence: "fences", speaker: "speakers", intercom: "intercoms", io: "ioModules",
};
const Operable = {
  all: true,
  sets: {},
  timer: null,
  async load() {
    let dto;
    try { dto = await Api.get("/api/auth/operable"); } catch { return; } // sin respuesta: se deja como estaba
    this.all = !!dto.all;
    this.sets = {};
    if (!this.all) for (const [kind, prop] of Object.entries(OP_KINDS)) this.sets[kind] = new Set((dto[prop] ?? []).map(String));
    applyOperable(document);
  },
  /** Relee con una pausa corta: varios avisos seguidos del hub hacen una sola consulta. */
  schedule() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.load(), 400);
  },
  reset() { this.all = true; this.sets = {}; applyOperable(document); },
  /** ¿Puede operar el recurso? kind según OP_KINDS; id tal cual lo nombra la API. */
  can(kind, id) { return this.all || !!this.sets[kind]?.has(String(id)); },
  /** "door:12 door:13" → todos tienen que poder operarse. */
  allows(spec) {
    return this.all || String(spec).split(/\s+/).filter(Boolean).every((part) => {
      const i = part.indexOf(":");
      return i > 0 && this.can(part.slice(0, i), part.slice(i + 1));
    });
  },
};

/** Aplica lo operable a los controles marcados con data-op dentro de root; los
 *  avisos con data-op-hint="…" se muestran solo cuando algo de eso NO se puede operar. */
function applyOperable(root) {
  if (!root?.querySelectorAll) return;
  const hints = [...root.querySelectorAll("[data-op-hint]")];
  if (root.matches?.("[data-op-hint]")) hints.push(root);
  for (const el of hints) el.hidden = Operable.allows(el.dataset.opHint);
  const nodes = [...root.querySelectorAll("[data-op]")];
  if (root.matches?.("[data-op]")) nodes.push(root);
  for (const el of nodes) {
    const ok = Operable.allows(el.dataset.op);
    const off = el.classList.contains("op-off");
    if (!ok && !off) {
      el.dataset.opWasDisabled = el.disabled ? "1" : "";
      el.dataset.opTitle = el.getAttribute("title") ?? "";
      el.disabled = true;
      el.classList.add("op-off");
      el.title = OP_DENIED;
    } else if (ok && off) {
      el.classList.remove("op-off");
      el.disabled = el.dataset.opWasDisabled === "1";
      if (el.dataset.opTitle) el.title = el.dataset.opTitle; else el.removeAttribute("title");
    }
  }
}

// Lo que las páginas dibujan después también se revisa (re-render de tarjetas,
// filas que llegan por el hub, modales).
new MutationObserver((mutations) => {
  if (Operable.all && !document.querySelector(".op-off")) return; // nada que bloquear (los avisos nacen ocultos)
  for (const m of mutations) for (const node of m.addedNodes) if (node.nodeType === 1) applyOperable(node);
}).observe(document.body, { childList: true, subtree: true });

// Lo operable cambia con el alcance del usuario y con dónde está cada recurso
// (y con los recursos nuevos, que heredan la ubicación de su equipo).
function onOperableTopic(topic) {
  if (OP_TOPICS.has(topic)) Operable.schedule();
  if (topic === "scope") showUserScope();
  if (topic === "permissions") onPermissionsChanged();
  if (topic === "license") onLicenseChanged();
}
function onOperableReconnected() { Operable.schedule(); }

// Red de seguridad: si un módulo rehabilitó un control fuera de alcance, el clic no pasa.
document.addEventListener("click", (e) => {
  const el = e.target.closest?.(".op-off");
  if (!el) return;
  e.preventDefault();
  e.stopImmediatePropagation();
  toast(OP_DENIED, true);
}, true);

/** Alcance por ubicación del usuario conectado, en su menú (el servidor ya filtra todo lo demás). */
async function showUserScope() {
  const box = $("#user-scope");
  if (!box) return;
  try {
    const scope = (await Api.get("/api/auth/me")).scope;
    box.classList.toggle("hidden", !scope?.restricted);
    if (!scope?.restricted) return;
    box.innerHTML = `<div class="muted">Su alcance</div>
      <div>${scope.locations.length ? scope.locations.map(esc).join("<br>") : "Ninguna ubicación"}</div>
      ${scope.viewOutsideScope ? `<div class="muted">Ve el resto, sin operarlo</div>` : ""}`;
  } catch { /* sin alcance que mostrar */ }
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

function renderLogin(message, tone = "info") {
  showAuthScreen();
  $("#auth-subtitle").textContent = "Gestión de video multimarca";
  $("#auth-body").innerHTML = `
    <div id="login-error">${message ? `<div class="${tone === "warn" ? "warn-box" : "info-box"}">${esc(message)}</div>` : ""}</div>
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
// renderDashboard (panel de inicio) vive en dashboard.js.

// ---------------------------------------------------------------------------
// Sesiones de streaming activas (quién ve qué) + expulsión
// ---------------------------------------------------------------------------
let sessionsTimer = null;

function sessionDuration(startedAt) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000));
  const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), s = seconds % 60;
  return (h ? `${h} h ` : "") + (h || m ? `${m} min ` : "") + `${s} s`;
}

/**
 * Puestos de cliente de escritorio: lo que cuenta el cupo de clientes de la
 * licencia. Un puesto es un EQUIPO; "sin conexión" es una sesión colgada (el
 * cliente se cerró sin cerrar sesión) que igual ocupa su puesto hasta vencer.
 */
function desktopSeatsHtml(data) {
  const limit = data.limit > 0 ? data.limit : null;
  const full = limit !== null && data.inUse >= limit;
  return `
    <div class="toolbar">
      <h3>Clientes de escritorio <span class="muted" style="font-weight:normal;font-size:12px">
        (${data.inUse}${limit !== null ? ` de ${limit}` : ""} puesto${data.inUse === 1 ? "" : "s"} de la licencia en uso)</span></h3>
    </div>
    ${full ? `<div class="warn-box">Están todos los puestos de cliente ocupados: nadie más puede ingresar con el cliente de
      escritorio. Libere uno que ya no se use (las sesiones «sin conexión» quedaron colgadas).</div>` : ""}
    ${data.seats.length === 0 ? `<div class="info-box">No hay clientes de escritorio con sesión abierta.</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>Equipo</th><th>Usuario</th><th>Versión del cliente</th><th>Ingresó</th><th>Vence</th><th>Estado</th><th></th>
      </tr></thead>
      <tbody>
        ${data.seats.map((s) => `
          <tr data-seat="${esc(s.seat)}">
            <td>${esc(s.seat)}</td>
            <td>${esc(s.username)} <span class="muted" style="font-size:11px">${s.role === "Admin" ? "administrador" : "operador"}</span></td>
            <td class="muted">${esc(s.version ?? "—")}</td>
            <td class="muted">${formatDate(s.startedAt)}</td>
            <td class="muted">${formatDate(s.expiresAt)}</td>
            <td>${s.connected
              ? `<span class="tag on">Conectado</span>`
              : `<span class="tag warn" title="El cliente se cerró sin cerrar sesión: el puesto sigue ocupado hasta que la sesión venza">Sin conexión (sesión colgada)</span>`}</td>
            <td class="row-actions">
              <button class="btn ${s.connected ? "danger" : "ghost"} btn-seat-release"
                title="Cierra la sesión de ese equipo y libera su puesto">${s.connected ? "Desconectar" : "Liberar puesto"}</button>
            </td>
          </tr>`).join("")}
      </tbody>
    </table></div>`}`;
}

async function renderSessions() {
  $("#page-title").textContent = "Sesiones";
  if (!Perms.can("sessions.manage")) {
    $("#view").innerHTML = `<div class="warn-box">Sus roles no incluyen las sesiones de video.</div>`;
    return;
  }

  const load = async () => {
    let sessions, seats;
    try {
      [sessions, seats] = await Promise.all([Api.get("/api/streams/active"), Api.get("/api/system/desktop-seats")]);
    }
    catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

    $("#view").innerHTML = `
      ${desktopSeatsHtml(seats)}
      <div class="toolbar page-toolbar" style="margin-top:22px">
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

    $$("#view .btn-seat-release").forEach((b) => b.addEventListener("click", async (e) => {
      const seat = seats.seats.find((x) => x.seat === e.target.closest("tr").dataset.seat);
      const aviso = seat.connected
        ? `¿Desconectar el cliente de "${seat.seat}" (${seat.username}) y liberar su puesto?\n\n` +
          "El cliente está abierto: vuelve a la pantalla de ingreso, se corta su video y no puede volver a entrar durante un minuto."
        : `¿Liberar el puesto de "${seat.seat}" (${seat.username})?\n\n` +
          "La sesión quedó colgada: el cliente se cerró sin cerrar sesión. Liberarla no afecta a nadie.";
      if (!confirm(aviso)) return;
      e.target.disabled = true;
      try {
        await Api.post("/api/system/desktop-seats/release", { seat: seat.seat });
        toast(`Puesto de "${seat.seat}" liberado.`);
        load();
      } catch (err) {
        toast(err.error, true);
        e.target.disabled = false;
      }
    }));

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
const ANPR_DRIVERS = ["hikvision-netsdk", "dahua-netsdk", "hikvision-isup"];

async function renderDevices() {
  $("#page-title").textContent = "Dispositivos";
  let devices, drivers;
  try {
    // Los drivers dan el nombre visible de la marca; si no llegan, la columna
    // cae a la clave técnica en vez de dejar la página sin tabla.
    [devices, drivers] = await Promise.all([Api.get("/api/devices"), getDrivers().catch(() => [])]);
  }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const brandOf = (key) => drivers.find((dr) => dr.key === key)?.displayName ?? key;

  const isAdmin = Perms.can("devices.manage");
  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
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
              <td>${esc(d.name)}${d.accessDeviceId ? `<div class="muted field-hint">Cámara de un terminal de acceso · incluida, no ocupa canal de video</div>` : ""}${devicePartsLine(d)}${locationLineHtml(d.location)}</td>
              <td>${esc(typeLabel(d.deviceType))}</td>
              <td class="muted">${esc(brandOf(d.driverKey))}</td>
              <td class="muted">${d.isupDeviceId ? isupAddressHtml(d.isupDeviceId)
                : d.accessDeviceId ? `${esc(d.host)} · RTSP ${d.rtspPort}` : `${esc(d.host)}:${d.sdkPort}`}</td>
              <td>${esc(d.model ?? "—")}</td>
              <td class="muted">${esc(d.serialNumber ?? "—")}</td>
              <td class="muted">${esc(d.firmwareVersion ?? "—")}</td>
              <td>${channelCountCell(d)}</td>
              <td>${d.accessDeviceId ? `<span class="muted">—</span>` : anprCell(d, isAdmin)}</td>
              <td>${statusTag(d.status)}</td>
              <td class="row-actions">
                <button class="btn ghost btn-channels">Canales</button>
                ${isAdmin && d.accessDeviceId ? `<a class="btn ghost" href="#/access/device?id=${d.accessDeviceId}"
                  title="Su dirección y credenciales son las del terminal: se administra en Control de acceso">Ver terminal</a>` : ""}
                ${isAdmin && !d.accessDeviceId ? `<button class="btn ghost btn-revalidate" title="Volver a sondear el equipo (modelo, firmware, canales, puerto RTSP)">Revalidar</button>
                <button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}
    ${isAdmin ? `
    <div class="toolbar" style="margin-top:28px">
      <h3>Equipos en línea <span class="muted hint">Buscando equipos compatibles en la red local; se actualiza cada 30 segundos.</span></h3>
      <button class="btn ghost" id="btn-device-scan" title="Volver a buscar ahora, sin esperar los 30 segundos">Actualizar</button>
    </div>
    <div id="online-devices">
      ${lastScan ? "" : `<div class="info-box">Sondeando el segmento de red del servidor…
        (Hikvision, Dahua y cualquier marca ONVIF).
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
  $("#btn-device-new")?.addEventListener("click", () => openFormPage("#/devices/new"));
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

/** Drivers de video cuyas partes (altavoz, entradas y salidas de alarma) sabe ver el VMS. */
const DEVICE_PARTS_DRIVERS = ["hikvision-netsdk"];

/** Debajo del nombre: las partes del equipo que se ofrecen como recursos. */
function devicePartsLine(d) {
  const parts = [];
  if (d.hasSpeaker && d.useSpeaker !== false) parts.push("altavoz");
  const io = (d.ioInputs ?? 0) + (d.ioOutputs ?? 0);
  if (io > 0 && d.useIo !== false) parts.push(`${d.ioInputs} entrada(s) y ${d.ioOutputs} salida(s) de alarma`);
  return parts.length ? `<div class="muted field-hint">Con ${esc(parts.join(" y "))} · incluidos, no ocupan licencia</div>` : "";
}

/** Partes del equipo en su formulario: puerto HTTP (ISAPI) y qué se ofrece como recurso. */
function devicePartsFieldHtml(device) {
  const hint = (text) => `<div class="muted field-hint acd-part-hint">${text}</div>`;
  const known = (has, use, yes, off) => !device?.partsChecked ? ""
    : has && use !== false ? hint(yes) : has ? hint(off) : hint("No se le detectó (se vuelve a mirar al revalidarlo).");
  const io = device ? (device.ioInputs ?? 0) + (device.ioOutputs ?? 0) : 0;
  return `
      <div class="acd-parts" id="df-parts">
        <div class="acd-parts-title">Partes del equipo <span class="muted">(incluidas: no ocupan licencia)</span></div>
        <div class="form-grid">
          <div class="field">
            <label>Puerto HTTP (ISAPI)</label>
            <input id="df-httpport" type="number" min="1" max="65535" value="${device?.httpPort ?? 80}">
            <div class="muted field-hint">Por ahí se leen y se operan su altavoz y sus entradas y salidas.</div>
          </div>
        </div>
        <label class="checkbox-row" title="Su salida de audio (audio bidireccional): voz en vivo y sonidos desde el Centro de mando, grupos de parlantes y automatizaciones.">
          <input type="checkbox" id="df-speaker" ${device?.useSpeaker === false ? "" : "checked"}> Su altavoz, como un parlante más</label>
        ${known(device?.hasSpeaker, device?.useSpeaker, `Está en Parlantes IP como «${esc(device?.name ?? "")}».`, "Tiene altavoz, pero no se está ofreciendo.")}
        <label class="checkbox-row" title="Sus entradas y salidas de alarma: estado en vivo, «Operar relé» y disparadores de automatizaciones.">
          <input type="checkbox" id="df-io" ${device?.useIo === false ? "" : "checked"}> Sus entradas y salidas de alarma, como un módulo de E/S</label>
        ${known(io > 0, device?.useIo,
          `${device?.ioInputs ?? 0} entrada(s) y ${device?.ioOutputs ?? 0} salida(s) en Módulos de E/S como «${esc(device?.name ?? "")}».`,
          `Tiene ${device?.ioInputs ?? 0} entrada(s) y ${device?.ioOutputs ?? 0} salida(s), pero no se están ofreciendo.`)}
        ${device?.partsNote ? `<div class="field-hint" style="color:var(--warn-text)">${esc(device.partsNote)}</div>` : ""}
      </div>`;
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
 * ventana que espera el servidor por la búsqueda en la red).
 */
// Opciones por defecto: la página Fuentes de video. Decodificadores reutiliza
// el mismo sondeo con kind=decoders y su propio contenedor/modal.
const DEVICE_DISCOVERY = {
  container: "#online-devices",
  button: "#btn-device-scan",
  kind: "",
  emptyText: "No se encontraron equipos de video en este segmento de red. " +
    "Los sondeos son de difusión y no cruzan routers ni VPN: solo ven el segmento del servidor. " +
    "Un equipo fuera de él se agrega a mano con su dirección.",
  onUse: (d) => openFormPage("#/devices/new", {
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
  if (scanButton) { scanButton.disabled = true; scanButton.textContent = "Actualizando…"; }
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
    if (btn) { btn.disabled = false; btn.textContent = "Actualizar"; }
  }
}

/// «Ocultar los ya agregados» de la tabla de equipos en línea: uno solo para todas
/// las páginas (video, control de acceso, citofonía, decodificadores), recordado en
/// este navegador.
const DISCOVERY_HIDE_ADDED_KEY = "tcvms_discovery_hide_added";
let discoveryHideAdded = (() => {
  try { return localStorage.getItem(DISCOVERY_HIDE_ADDED_KEY) === "1"; } catch { return false; /* sin almacenamiento */ }
})();

/// Tabla de equipos descubiertos, al estilo "Online Device" de HikCentral.
function renderOnlineDevices(devices) {
  const opt = discoveryOptions;
  const box = $(opt.container);
  if (!box) return; // la página cambió mientras corría el sondeo
  const hideAdded = discoveryHideAdded;
  // El refresco automático no debe repintar la tabla si nada cambió.
  const signature = JSON.stringify(lastScan) + "|" + devices.map((d) => d.host).join(",") + "|" + hideAdded;
  if (box.dataset.signature === signature) return;
  box.dataset.signature = signature;
  if (!lastScan || !lastScan.length) {
    box.innerHTML = `<div class="warn-box">${esc(opt.emptyText)}</div>`;
    return;
  }
  const knownHosts = new Set(devices.map((d) => d.host));
  const addedCount = lastScan.filter((d) => knownHosts.has(d.ip)).length;
  // El índice es el de lastScan: los botones lo usan para encontrar su equipo.
  const rows = lastScan.map((d, i) => ({ d, i })).filter(({ d }) => !hideAdded || !knownHosts.has(d.ip));
  // Lo que el equipo no informa se muestra con raya, no como celda vacía.
  const cell = (v) => (v == null || String(v).trim() === "" ? "—" : esc(v));
  box.innerHTML = `
    <div class="discovery-bar">
      <label class="ui-switch" title="Deja en la lista solo los equipos que todavía no están en el sistema">
        <input type="checkbox" id="discovery-hide-added" ${hideAdded ? "checked" : ""}>
        <span class="ui-switch-track"></span>Ocultar los ya agregados${addedCount ? ` (${addedCount})` : ""}
      </label>
    </div>
    ${rows.length === 0 ? `<div class="info-box">Todos los equipos encontrados ya están agregados.</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>IP</th><th>Marca</th><th>Tipo</th><th>Modelo</th><th>N° serie</th><th>${esc(opt.portLabel ?? "Puerto SDK")}</th>
        <th>MAC</th><th>Estado</th><th></th>
      </tr></thead>
      <tbody>${rows.map(({ d, i }) => {
        const added = knownHosts.has(d.ip);
        return `
        <tr>
          <td>${cell(d.ip)}</td>
          <td>${cell(d.brand)}</td>
          <td>${cell(d.category)}</td>
          <td>${cell(d.model)}</td>
          <td class="muted">${cell(d.serial)}</td>
          <td class="muted">${cell(opt.portOf ? opt.portOf(d) : d.commandPort)}</td>
          <td class="muted">${cell(d.mac)}</td>
          <td>${added ? `<span class="tag on">Agregado</span>`
                : d.canInitialize ? `<span class="tag off">${d.brand === "Hikvision" ? "Sin activar" : "Sin inicializar"}</span>`
                : d.activated === false ? `<span class="tag off">Sin activar</span>`
                : `<span class="tag operator">Nuevo</span>`}</td>
          <td class="row-actions">
            ${d.canChangeIp && !added
              ? `<button class="btn ghost scan-ip" data-i="${i}" title="Cambia la IP del equipo sin entrar a él (como Change IP de SmartPSS)">Cambiar IP</button>`
              : ""}
            ${d.canInitialize && !added
              ? (d.brand === "Hikvision"
                ? `<button class="btn scan-init" data-i="${i}" title="Fija la contraseña del usuario admin del equipo de fábrica">Activar</button>`
                : `<button class="btn scan-init" data-i="${i}" title="Crea el usuario admin del equipo de fábrica">Inicializar</button>`)
              : `<button class="btn ghost scan-use" data-i="${i}" ${added ? "disabled" : ""}
                  ${d.reachable === false ? `title="El equipo está en otra subred: cámbiele la IP antes de agregarlo"` : ""}>Agregar</button>`}
          </td>
        </tr>`;
      }).join("")}
      </tbody>
    </table></div>`}`;
  $("#discovery-hide-added")?.addEventListener("change", (e) => {
    discoveryHideAdded = e.target.checked;
    try { localStorage.setItem(DISCOVERY_HIDE_ADDED_KEY, discoveryHideAdded ? "1" : "0"); } catch { /* sin almacenamiento */ }
    box.dataset.signature = "";
    renderOnlineDevices(devices);
  });
  $$(".scan-use").forEach((b) => b.addEventListener("click", () => opt.onUse(lastScan[Number(b.dataset.i)])));
  $$(".scan-init").forEach((b) => b.addEventListener("click", () => initModal(lastScan[Number(b.dataset.i)], devices)));
  $$(".scan-ip").forEach((b) => b.addEventListener("click", () => changeIpModal(lastScan[Number(b.dataset.i)], devices)));
}

/** Validación rápida de IPv4 en el panel (el servidor revisa lo mismo y más). */
const isIPv4 = (s) => /^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$/.test(s);

/**
 * Cambia la red de un equipo Dahua o Hikvision sin entrar a él (multicast por
 * MAC), como "Change IP" de las herramientas del fabricante: IP fija o DHCP, y los DNS
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
  // ZKTeco (protocolo C3): la orden va por MAC, sin usuario ni clave, y solo
  // lleva IP fija, máscara y puerta de enlace (sin DHCP, DNS ni hora).
  const zk = d.brand === "ZKTeco";

  openModal(`
    <h3>Cambiar IP</h3>
    <p class="muted" style="margin-top:0">
      ${esc(d.brand)} ${esc(d.model)} · ${esc(d.mac)}<br>
      IP actual: <b>${esc(d.ip)}</b>${d.dhcp ? " (por DHCP)" : ""}${d.reachable === false
        ? ` — fuera de la red de este servidor${net ? ` (${esc(net.address)})` : ""}: no se puede agregar hasta cambiarla.` : ""}
    </p>
    <div id="dip-error"></div>
    <form id="dip-form">
      <div class="field ${zk ? "hidden" : ""}">
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
            <div id="dip-ip-check" style="font-size:12px;margin-top:4px"></div>
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
        ${zk ? "" : `<div class="form-grid">
          <div class="field">
            <label>DNS preferido (opcional)</label>
            <input id="dip-dns1" value="${esc(dns1)}" placeholder="vacío = no cambiar">
          </div>
          <div class="field">
            <label>DNS alternativo (opcional)</label>
            <input id="dip-dns2" value="${esc(dns2)}">
          </div>
        </div>`}
      </div>
      <div id="dip-dhcp-note" class="muted hidden" style="font-size:12px;margin:-4px 0 12px">
        El equipo tomará IP, máscara, puerta de enlace y DNS del servidor DHCP de la red. TrueCentral lo
        busca después por su MAC para mostrar la IP que le tocó.
      </div>
      ${knownPassword || zk ? "" : `
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
      ${zk ? "" : `<label class="check-row" style="display:flex;gap:8px;align-items:flex-start;margin:0 0 12px">
        <input type="checkbox" id="dip-time" ${knownPassword ? "checked" : ""}>
        <span>Sincronizar fecha, hora y zona horaria (con horario de verano) con este servidor</span>
      </label>`}
      <div class="muted" style="font-size:12px;margin:-4px 0 12px" id="dip-static-note">
        ${zk
          ? `Se comprueba antes que la IP nueva no esté ocupada. La orden va por la MAC del equipo y no pide clave;
             la hora se ajusta después de agregarlo (Hora y mantenimiento).`
          : `Se comprueba antes que la IP nueva no esté ocupada. Los DNS se configuran entrando al equipo
             una vez que está en su IP nueva (con el mismo usuario y contraseña).`}
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

  // Al salir del campo "IP nueva", si cambió, se avisa si ya hay algo en esa
  // IP (otro equipo de la búsqueda, o algo que responde al ping o al ARP).
  // Es solo un aviso: Aplicar lo vuelve a comprobar en el servidor.
  let checkedIp = d.ip;
  $("#dip-ip").addEventListener("blur", async () => {
    const value = $("#dip-ip").value.trim();
    const box = $("#dip-ip-check");
    if (value === checkedIp) return;
    checkedIp = value;
    if (!isIPv4(value) || value === d.ip) { box.innerHTML = ""; return; }
    const neighbor = (lastScan || []).find((x) => x.ip === value && x.mac !== d.mac);
    if (neighbor) {
      box.innerHTML = `<span style="color:var(--danger, #e5534b)">En uso por ${esc(neighbor.brand)} ${esc(neighbor.model)} (${esc(neighbor.mac)}).</span>`;
      return;
    }
    box.innerHTML = `<span class="muted">Comprobando si está libre…</span>`;
    try {
      const r = await Api.get(`/api/discovery/ip-in-use?ip=${encodeURIComponent(value)}`);
      if (checkedIp !== value) return; // el usuario ya escribió otra
      box.innerHTML = r.inUse
        ? `<span style="color:var(--danger, #e5534b)">La IP ${esc(value)} ya está en uso por otro equipo de la red.</span>`
        : `<span style="color:var(--ok, #3fb950)">La IP ${esc(value)} está libre.</span>`;
    } catch {
      if (checkedIp === value) box.innerHTML = `<span class="muted">No se pudo comprobar la IP.</span>`;
    }
  });

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
      dns1: $("#dip-dns1")?.value.trim() ?? "",
      dns2: $("#dip-dns2")?.value.trim() ?? "",
      username: knownPassword || zk ? "admin" : $("#dip-user").value.trim(),
      password: knownPassword ?? $("#dip-pass")?.value ?? "",
      syncTime: $("#dip-time")?.checked ?? false,
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
        ${r.timeSynced && !r.timeNote ? `<div class="info-box" style="margin-top:10px">Fecha, hora y zona horaria sincronizadas con el servidor.</div>` : ""}
        ${r.timeSynced && r.timeNote ? `<div class="warn-box" style="margin-top:10px">Fecha y hora sincronizadas, pero ${esc(r.timeNote)}</div>` : ""}
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

/** Política de activación Hikvision (la misma que revisa el servidor). Devuelve el problema o null. */
function hikvisionPasswordProblem(p) {
  if (!p || p.length < 8 || p.length > 16) return "La contraseña debe tener entre 8 y 16 caracteres.";
  if (p.includes(" ")) return "La contraseña no puede llevar espacios.";
  if (/admin/i.test(p)) return "La contraseña no puede contener el nombre de usuario (admin).";
  const kinds = [/[A-Z]/, /[a-z]/, /[0-9]/, /[^A-Za-z0-9]/].filter((r) => r.test(p)).length;
  return kinds < 2
    ? "La contraseña debe combinar al menos dos tipos de caracteres: mayúsculas, minúsculas, números o símbolos."
    : null;
}

/**
 * Deja listo un equipo de fábrica (cualquier tipo: cámara, grabador, control
 * de acceso, citofonía...): Dahua se inicializa (se le crea el usuario admin,
 * como "Initialize" de SmartPSS) y Hikvision se activa (se fija la contraseña
 * de admin, como la activación de las herramientas del fabricante). Al terminar ofrece agregarlo de
 * inmediato, o cambiarle la IP si quedó fuera de la red del servidor.
 */
function initModal(d, devices) {
  const opt = discoveryOptions;
  const hik = d.brand === "Hikvision";
  const verb = hik ? "Activar" : "Inicializar";
  const maxLength = hik ? 16 : 32;
  openModal(`
    <h3>${verb} equipo</h3>
    <p class="muted" style="margin-top:0">
      ${esc(d.model)} · ${esc(d.ip)} · ${esc(d.mac)}<br>
      ${hik
        ? "El equipo está de fábrica, sin activar. Se fijará esta contraseña al usuario <b>admin</b>."
        : "El equipo está de fábrica y no tiene usuarios. Se le creará el usuario <b>admin</b> con esta contraseña."}
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
          <input id="di-pass" type="password" autocomplete="new-password" required maxlength="${maxLength}">
        </div>
        <div class="field">
          <label>Confirmar contraseña</label>
          <input id="di-pass2" type="password" autocomplete="new-password" required maxlength="${maxLength}">
        </div>
      </div>
      <div class="muted" style="font-size:12px;margin:-4px 0 12px">
        ${hik
          ? "8 a 16 caracteres, combinando al menos dos tipos (mayúsculas, minúsculas, números o símbolos), sin espacios y sin la palabra admin."
          : "8 a 32 caracteres, combinando al menos dos tipos (mayúsculas, minúsculas, números o símbolos), sin ' \" ; : &amp; ni espacios."}
      </div>
      ${hik ? "" : `
      <div class="field">
        <label>Correo para recuperar la contraseña${d.initNeedsEmail ? "" : " (opcional)"}</label>
        <input id="di-email" type="email" maxlength="63" ${d.initNeedsEmail ? "required" : ""}>
      </div>`}
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="di-cancel">Cancelar</button>
        <button class="btn" type="submit" id="di-save">${verb}</button>
      </div>
    </form>`);

  $("#di-cancel").addEventListener("click", closeModal);
  $("#di-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const password = $("#di-pass").value;
    const confirmPassword = $("#di-pass2").value;
    const showError = (msg) => { $("#di-error").innerHTML = `<div class="error-box">${esc(msg)}</div>`; };
    const problem = (hik ? hikvisionPasswordProblem(password) : dahuaPasswordProblem(password))
      ?? (password !== confirmPassword ? "Las contraseñas no coinciden." : null);
    if (problem) { showError(problem); return; }

    const button = $("#di-save");
    button.disabled = true;
    button.textContent = hik ? "Activando…" : "Inicializando…";
    $("#di-error").innerHTML = "";
    try {
      const r = hik
        ? await Api.post("/api/discovery/hikvision/activate", { mac: d.mac, password, confirmPassword })
        : await Api.post("/api/discovery/dahua/initialize", {
            mac: d.mac, password, confirmPassword, email: $("#di-email").value,
          });
      // El equipo ya tiene contraseña: la próxima búsqueda lo mostrará como "Nuevo".
      lastScan = null;
      runDiscovery(devices, false);
      const ready = { ...d, activated: true, canInitialize: false, canChangeIp: true, username: r.username };
      openModal(`
        <h3>Equipo ${hik ? "activado" : "inicializado"}</h3>
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
      showError(err.error || `No se pudo ${verb.toLowerCase()} el equipo.`);
      button.disabled = false;
      button.textContent = verb;
    }
  });
}

async function deviceModal(device, prefill) {
  const isNew = !device;
  const seed = isNew ? (prefill || {}) : {};
  const [drivers, places] = await Promise.all([getDrivers(), loadLocationChoices()]);
  openModal(`
    <h3>${isNew ? "Agregar dispositivo" : "Editar dispositivo"}</h3>
    <div id="dev-modal-error"></div>
    <form id="device-form">
      <div class="field">
        <label>Nombre</label>
        <input id="df-name" required maxlength="128" value="${esc(device?.name ?? seed.name ?? "")}" placeholder="NVR Bodega, Cámara acceso...">
      </div>
      ${locationFieldHtml("df-location", places, device?.locationId ?? null,
        "Sus canales la heredan; los que ubique aparte en Recursos se quedan donde están.", { equipment: true, isNew })}
      <div class="field">
        <label>Marca / protocolo</label>
        <select id="df-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${(device?.driverKey ?? seed.driverKey) === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <div id="df-ip-box">
        <div class="form-grid">
          <div class="field">
            <label>Dirección (IP o hostname)</label>
            <input id="df-host" required value="${esc(device?.isupDeviceId ? "" : device?.host ?? seed.host ?? "")}" placeholder="192.168.1.64">
          </div>
          <div class="field">
            <label>Puerto SDK</label>
            <input id="df-sdkport" type="number" min="1" max="65535" required value="${device?.isupDeviceId ? 8000 : device?.sdkPort ?? seed.sdkPort ?? drivers[0]?.defaultSdkPort ?? 8000}">
          </div>
        </div>
        <div class="form-grid">
          <div class="field">
            <label>Puerto RTSP</label>
            <input id="df-rtspport" type="number" min="1" max="65535" required value="${device?.isupDeviceId ? 554 : device?.rtspPort ?? drivers[0]?.defaultRtspPort ?? 554}">
          </div>
          <div class="field">
            <label>Usuario del equipo</label>
            <input id="df-username" required value="${esc(device?.username ?? seed.username ?? "")}" placeholder="admin">
          </div>
        </div>
        <div class="field">
          <label>${isNew || device?.isupDeviceId ? "Contraseña del equipo" : "Contraseña (vacío = no cambiar)"}</label>
          <input id="df-password" type="password" autocomplete="new-password" ${isNew || device?.isupDeviceId ? "required" : ""}>
        </div>
        ${devicePartsFieldHtml(device)}
      </div>
      <div id="df-isup-wrap">${isupFieldsHtml("df", device, isNew, "equipo")}</div>
      <div id="probe-result"></div>
      <div id="channel-picker"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="df-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="df-probe">Probar conexión</button>
        <button class="btn" type="submit" id="df-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);

  $("#df-cancel").addEventListener("click", closeModal);
  // Por IP se piden dirección, puertos y credenciales; por ISUP, el ID y la clave del equipo. Los campos que no
  // corresponden se ocultan y dejan de ser obligatorios (si no, el navegador no deja guardar).
  const toggleIsup = (dr) => {
    const isup = !!dr?.usesIsup;
    $("#df-ip-box").hidden = isup;
    $("#df-isup-wrap").hidden = !isup;
    for (const el of $$("#df-ip-box input")) {
      if (isup) { if (el.required) el.dataset.req = "1"; el.required = false; }
      else if (el.dataset.req) el.required = true;
    }
  };
  wireIsupFields("df", "equipo");
  toggleIsup(drivers.find((x) => x.key === $("#df-driver").value));
  // Las partes (altavoz, entradas y salidas) solo en las marcas que el VMS sabe ver.
  const toggleParts = () => { $("#df-parts").hidden = !DEVICE_PARTS_DRIVERS.includes($("#df-driver").value); };
  toggleParts();
  $("#df-driver").addEventListener("change", toggleParts);
  $("#df-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#df-driver").value);
    if (dr && isNew && !dr.usesIsup) { $("#df-sdkport").value = dr.defaultSdkPort; $("#df-rtspport").value = dr.defaultRtspPort; }
    toggleIsup(dr);
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
    const isup = !$("#df-isup-wrap").hidden;
    const body = isup ? {
      name: $("#df-name").value.trim() || "(sin nombre)",
      driverKey: $("#df-driver").value,
      host: "", sdkPort: 0, rtspPort: 0, username: "", password: null,
      locationId: locationFieldValue("df-location"),
      ...readIsupFields("df"),
    } : {
      name: $("#df-name").value.trim() || "(sin nombre)",
      driverKey: $("#df-driver").value,
      host: $("#df-host").value.trim(),
      sdkPort: Number($("#df-sdkport").value),
      rtspPort: Number($("#df-rtspport").value),
      username: $("#df-username").value.trim(),
      password: $("#df-password").value || null,
      locationId: locationFieldValue("df-location"),
    };
    if (!isup && !$("#df-parts").hidden) {
      body.httpPort = Number($("#df-httpport").value) || 80;
      body.useSpeaker = $("#df-speaker").checked;
      body.useIo = $("#df-io").checked;
    }
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
            <span>Puerto RTSP</span><b>${$("#df-isup-wrap").hidden ? (r.detectedRtspPort ? r.detectedRtspPort + " (detectado por SDK)" : "no reportado") : "no aplica (el video llega por ISUP)"}</b>
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
      await locationFieldCommit("df-location", body);
      const result = isNew
        ? await Api.post("/api/devices", body)
        : await Api.put(`/api/devices/${device.id}`, body);
      closeModal();
      // Por ISUP el equipo puede no haberse conectado todavía: se guarda igual y se completa en su primer contacto.
      toastDeviceSaved(result, result?.isupDeviceId && result.status !== "Online"
        ? (isNew ? "Dispositivo agregado." : "Dispositivo actualizado.")
        : (isNew ? "Dispositivo agregado y validado." : "Dispositivo actualizado."));
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

  const isAdmin = Perms.can("devices.manage");
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
                   title="Mostrar control PTZ (mover, zoom, foco). Se detecta automático cuando el equipo lo informa; márquelo a mano para domos y cámaras con lente varifocal motorizado conectados por coaxial a un DVR (se controlan por el cable, pero el grabador no lo informa) o por ONVIF al grabador.">
              <input type="checkbox" class="ch-ptz" ${c.supportsPtz ? "checked" : ""} ${isAdmin ? "" : "disabled"}> PTZ / lente
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
  if (!Perms.can("users.manage")) {
    $("#view").innerHTML = `<div class="warn-box">Sus roles no incluyen la administración de usuarios.</div>`;
    return;
  }
  let users, locations, unassigned, roles;
  try {
    // El árbol de ubicaciones y lo "por ubicar" sirven para el alcance de cada
    // usuario; los roles, para elegirlos (con lo que esta sesión puede asignar).
    [users, locations, unassigned, roles] = await Promise.all([
      Api.get("/api/users"),
      Api.get("/api/locations").catch(() => []),
      Api.get("/api/resources?unassigned=true").catch(() => []),
      Api.get("/api/roles"),
    ]);
  }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const scopeCtx = { locations, unassigned: unassigned.length, roles };

  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <button class="btn" id="btn-user-new">Agregar usuario</button>
    </div>
    <table class="grid">
      <thead><tr>
        <th>Usuario</th><th>Roles</th><th>Alcance</th><th>Estado</th><th>Creado</th><th>Última clave</th><th></th>
      </tr></thead>
      <tbody>
        ${users.map((u) => `
          <tr data-id="${u.id}">
            <td>${esc(u.username)}${u.isSuperAdmin ? ` <span class="tag admin" title="Creado al activar la plataforma: acceso total sin importar los roles. Solo él modifica su usuario.">Superadministrador</span>` : ""}</td>
            <td>${userRolesHtml(u)}</td>
            <td>${userScopeHtml(u)}</td>
            <td>${u.disabledByLicense ? licenseWaitTag(u).trim() : `<span class="tag ${u.enabled ? "on" : "off"}">${u.enabled ? "Habilitado" : "Deshabilitado"}</span>`}</td>
            <td class="muted">${formatDate(u.createdAt)}</td>
            <td class="muted">${formatDate(u.passwordChangedAt)}</td>
            <td class="row-actions">${u.editable === false
              ? `<span class="muted" title="${u.isSuperAdmin ? "Solo el superadministrador modifica su usuario" : "Tiene permisos o alcance que usted no tiene"}">Sin acceso</span>`
              : `<button class="btn ghost btn-edit">Editar</button>
              ${u.isSuperAdmin ? "" : `<button class="btn danger btn-delete">Eliminar</button>`}`}
            </td>
          </tr>`).join("")}
      </tbody>
    </table>`;

  $("#btn-user-new").addEventListener("click", () => userModal(null, scopeCtx));
  $$("#view .btn-edit").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    userModal(users.find((u) => u.id === id), scopeCtx);
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

// ---------------------------------------------------------------------------
// Alcance por ubicación de un usuario (qué ve y qué opera)
// ---------------------------------------------------------------------------

/** La celda "Roles" de la tabla de usuarios. */
function userRolesHtml(u) {
  const names = u.roleNames ?? (u.role === "Admin" ? ["Administrador"] : ["Operador"]);
  if (!names.length) return `<span class="muted">Sin roles</span>`;
  return names.map((n) => `<span class="tag ${n === "Administrador" && u.role === "Admin" ? "admin" : "operator"}">${esc(n)}</span>`).join(" ");
}

/** Casillas de roles del formulario de usuario: los que esta sesión no puede asignar quedan bloqueados. */
function userRolesFieldHtml(roles, selected) {
  if (!roles?.length) return `<div class="muted">No se pudieron leer los roles.</div>`;
  return `<div class="role-pick">${roles.map((r) => {
    const on = selected.has(r.id);
    const locked = !r.assignable && !on;
    const count = r.systemKey === "admin" ? "todos los permisos" : `${r.permissions.length} permiso(s)`;
    return `<label class="role-pick-item${locked ? " locked" : ""}" title="${locked ? "Tiene permisos que usted no tiene" : esc(r.description || "")}">
      <input type="checkbox" data-role="${r.id}" data-system="${esc(r.systemKey ?? "")}" ${on ? "checked" : ""} ${locked ? "disabled" : ""}>
      <span><b>${esc(r.name)}</b> <span class="muted">· ${count}</span>
        ${r.description ? `<br><span class="muted small">${esc(r.description)}</span>` : ""}</span>
    </label>`;
  }).join("")}</div>
  <div class="muted field-hint">Con varios roles, el usuario suma los permisos de todos.</div>`;
}

/** La celda "Alcance" de la tabla de usuarios: lo que efectivamente ve y opera (sus roles + su límite propio). */
function userScopeHtml(u) {
  if (u.isSuperAdmin) return `<span class="muted">Todo (superadministrador)</span>`;
  if (u.role === "Admin") return `<span class="muted">Todo (administrador)</span>`;
  const text = u.effectiveScope || "Todo el sistema";
  if (text === "Todo el sistema") return `<span class="muted">Todo el sistema</span>`;
  const short = text.length > 70 ? text.slice(0, 68) + "…" : text;
  return `<span title="${esc(text)}">${esc(short)}</span>` +
    (u.restrictToLocations ? ` <span class="tag operator" title="Tiene un límite propio por ubicación además de sus roles">límite propio</span>` : "");
}

/** Árbol de casillas: marcar una ubicación incluye sus sububicaciones (quedan marcadas y bloqueadas). */
function userScopeTreeHtml(locations, selected) {
  const children = new Map();
  for (const l of locations) {
    const key = l.parentId ?? "root";
    if (!children.has(key)) children.set(key, []);
    children.get(key).push(l);
  }
  const collator = new Intl.Collator("es", { numeric: true, sensitivity: "base" });
  for (const list of children.values()) list.sort((a, b) => collator.compare(a.name, b.name));
  const branch = (key) => {
    const list = children.get(key) || [];
    if (!list.length) return "";
    return `<ul>${list.map((l) => `
      <li><label class="scope-node"><input type="checkbox" data-loc="${l.id}" ${selected.has(l.id) ? "checked" : ""}>
        <span>${esc(l.name)}</span></label>${branch(l.id)}</li>`).join("")}</ul>`;
  };
  return locations.length
    ? branch("root")
    : `<div class="muted">Todavía no hay ubicaciones: créelas en Configuración → Recursos.</div>`;
}

/**
 * Una casilla marcada deja marcadas y bloqueadas sus sububicaciones (ya están
 * incluidas); al desmarcarla, cada una vuelve a lo que el usuario tenía marcado.
 * El orden del documento recorre cada padre antes que sus hijas.
 */
function userScopeSync(tree) {
  for (const box of $$("input[data-loc]", tree)) {
    const parentBox = box.closest("ul")?.closest("li")?.querySelector(":scope > label > input[data-loc]");
    const inherited = parentBox ? (parentBox.checked || parentBox.dataset.inherited === "1") : false;
    const was = box.dataset.inherited === "1";
    if (inherited && !was) box.dataset.own = box.checked ? "1" : "0";
    if (!inherited && was) box.checked = box.dataset.own === "1";
    box.dataset.inherited = inherited ? "1" : "0";
    box.disabled = inherited;
    if (inherited) box.checked = true;
    box.closest("label").title = inherited ? "Incluida por la ubicación de arriba" : "";
  }
}

function userModal(user, scopeCtx = { locations: [], unassigned: 0, roles: [] }) {
  const isNew = !user;
  const operatorRole = scopeCtx.roles?.find((r) => r.systemKey === "operator");
  const roleIds = new Set(user?.roleIds ?? (operatorRole?.assignable ? [operatorRole.id] : []));
  const selected = new Set(user?.locationIds || []);
  const restricted = !!user?.restrictToLocations;
  const superAdmin = !!user?.isSuperAdmin;
  openModal(`
    <h3>${isNew ? "Agregar usuario" : "Editar usuario"}</h3>
    <div id="user-modal-error"></div>
    <form id="user-form">
      <div class="field">
        <label>Nombre de usuario</label>
        <input id="uf-username" required minlength="3" maxlength="64" value="${esc(user?.username ?? "")}">
      </div>
      ${superAdmin ? `
      <div class="info-box"><b>Superadministrador de la plataforma.</b> Se creó al activar el sistema y tiene acceso total:
        no le afectan los roles ni los alcances, no se deshabilita ni se elimina, y solo él puede cambiar su nombre y su contraseña.</div>` : `
      <div class="field">
        <label>Roles</label>
        <div id="uf-roles">${userRolesFieldHtml(scopeCtx.roles, roleIds)}</div>
      </div>
      ${user?.effectiveScope ? `<div class="muted small" style="margin:-4px 0 10px">Alcance actual: ${esc(user.effectiveScope)}</div>` : ""}
      <div class="field" id="uf-scope">
        <label>Límite propio por ubicación (opcional)</label>
        <div class="muted scope-admin-note" id="uf-scope-admin">Los administradores ven y operan todo.</div>
        <div id="uf-scope-edit">
          <div class="muted scope-hint" style="margin:0 0 6px">El alcance lo dan sus roles (Seguridad → Roles: ubicaciones,
            cámaras, puertas, equipos…). Aquí se puede limitar además a este usuario.</div>
          <label class="radio-row"><input type="radio" name="uf-scope-mode" value="all" ${restricted ? "" : "checked"}>
            Sin límite propio: lo que le den sus roles</label>
          <label class="radio-row"><input type="radio" name="uf-scope-mode" value="some" ${restricted ? "checked" : ""}>
            De eso, solo lo que esté en estas ubicaciones (cada una con sus sububicaciones)</label>
          <div id="uf-scope-pick">
            <div class="scope-tree" id="uf-scope-tree">${userScopeTreeHtml(scopeCtx.locations, selected)}</div>
            <label class="checkbox-row" style="margin-top:8px">
              <input type="checkbox" id="uf-view-outside" ${user?.viewOutsideScope ? "checked" : ""}>
              <span>Puede <b>ver</b> el resto, sin operarlo (supervisión)</span>
            </label>
            <div class="muted scope-hint">Fuera de su alcance no ve listas, eventos ni video, ni puede dar órdenes.
              ${scopeCtx.unassigned > 0
                ? `Hay ${scopeCtx.unassigned} recurso(s) por ubicar: quedan fuera de un límite propio.`
                : "Lo que esté por ubicar queda fuera de un límite propio."}</div>
          </div>
        </div>
      </div>
      <div class="checkbox-row">
        <input type="checkbox" id="uf-enabled" ${user?.enabled !== false ? "checked" : ""}>
        <label for="uf-enabled" style="margin:0">Habilitado</label>
      </div>`}
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
  if (superAdmin) {
    // Solo nombre y contraseña: el servidor conserva su rol y su acceso total.
    $("#user-form").addEventListener("submit", async (e) => {
      e.preventDefault();
      const errorBox = $("#user-modal-error");
      errorBox.innerHTML = "";
      const password = $("#uf-password").value;
      if (password && !passwordOk(password)) {
        errorBox.innerHTML = `<div class="error-box">La contraseña no cumple la política de seguridad.</div>`;
        return;
      }
      try {
        await Api.put(`/api/users/${user.id}`, {
          username: $("#uf-username").value.trim(), password: password || null,
          roleIds: user.roleIds, enabled: true,
        });
        closeModal();
        toast("Usuario actualizado.");
        renderUsers();
      } catch (err) { errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
    });
    return;
  }
  // Límite propio: se ve según el rol (el administrador no tiene) y el modo elegido.
  const scopeTree = $("#uf-scope-tree");
  const isAdminPicked = () => $$("#uf-roles input[data-role]").some((b) => b.checked && b.dataset.system === "admin");
  const refreshScope = () => {
    const admin = isAdminPicked();
    const some = $("input[name=uf-scope-mode]:checked")?.value === "some";
    $("#uf-scope-admin").classList.toggle("hidden", !admin);
    $("#uf-scope-edit").classList.toggle("hidden", admin);
    $("#uf-scope-pick").classList.toggle("hidden", !some);
    userScopeSync(scopeTree);
  };
  $("#uf-roles").addEventListener("change", refreshScope);
  $$("input[name=uf-scope-mode]").forEach((r) => r.addEventListener("change", refreshScope));
  scopeTree.addEventListener("change", () => userScopeSync(scopeTree));
  refreshScope();

  $("#user-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#user-modal-error");
    errorBox.innerHTML = "";
    const password = $("#uf-password").value;
    if ((isNew || password) && !passwordOk(password)) {
      errorBox.innerHTML = `<div class="error-box">La contraseña no cumple la política de seguridad.</div>`;
      return;
    }
    const pickedRoles = $$("#uf-roles input[data-role]").filter((b) => b.checked).map((b) => Number(b.dataset.role));
    if (pickedRoles.length === 0) {
      errorBox.innerHTML = `<div class="error-box">Marque al menos un rol.</div>`;
      return;
    }
    const restrict = !isAdminPicked() && $("input[name=uf-scope-mode]:checked")?.value === "some";
    // Solo las marcadas a mano: las incluidas por una de arriba ya van con ella.
    const locationIds = restrict
      ? $$("input[data-loc]", scopeTree).filter((b) => b.checked && b.dataset.inherited !== "1").map((b) => Number(b.dataset.loc))
      : [];
    const viewOutside = restrict && $("#uf-view-outside").checked;
    if (restrict && locationIds.length === 0 && !viewOutside) {
      errorBox.innerHTML = `<div class="error-box">Marque al menos una ubicación, o "puede ver el resto" si solo debe supervisar.</div>`;
      return;
    }
    const body = {
      username: $("#uf-username").value.trim(),
      password: password || null,
      roleIds: pickedRoles,
      enabled: $("#uf-enabled").checked,
      restrictToLocations: restrict,
      viewOutsideScope: viewOutside,
      locationIds,
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
// Auditoría (bitácora ISO 27001, permiso "Bitácora de auditoría")
// ---------------------------------------------------------------------------
let auditCatalog = null;           // catálogo de categorías/acciones (se pide una vez)
const auditState = { page: 1 };    // filtros vigentes entre búsquedas

const AUDIT_ORIGINS = { web: "Panel web", client: "Cliente", server: "Servidor", screen: "Pantalla de alarma" };

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
  if (!Perms.can("audit.view")) {
    $("#view").innerHTML = `<div class="warn-box">Sus roles no incluyen la bitácora de auditoría.</div>`;
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
    <div class="toolbar page-toolbar">
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
  "#/access/device": renderAccessDevicePage,
  // Alta de equipos como página (los mismos formularios que editan en modal).
  "#/devices/new": formPageRoute({ back: "#/devices", icon: "mdi-video",
    hint: "Al guardar se validan las credenciales contra el equipo y se leen su modelo, número de serie, firmware y canales." },
    (prefill) => deviceModal(null, prefill)),
  "#/access/new": formPageRoute({ back: "#/access", icon: "mdi-key-variant",
    hint: "Al guardar se validan las credenciales contra el equipo y se leen su modelo, firmware, capacidades y las puertas que administra." },
    (prefill) => accessDeviceModal(null, prefill)),
  "#/alarm-panels/new": formPageRoute({ back: "#/alarm-panels", icon: "mdi-shield",
    hint: "Por IP, al guardar se validan las credenciales contra la central y se leen sus áreas y zonas. Por ISUP, el panel se conecta solo al servidor con su ID y su clave." },
    () => alarmPanelModal(null)),
  "#/cerco-panels/new": formPageRoute({ back: "#/cerco-panels", icon: "mdi-flash",
    hint: "Al guardar, el sistema genera el ID de equipo y el código de enrolamiento que el instalador carga en el panel. Se muestran una sola vez." },
    () => cercoPanelModal(null)),
  "#/speakers/new": formPageRoute({ back: "#/speakers", icon: "mdi-bullhorn",
    hint: "Al guardar se validan las credenciales contra el equipo y se leen sus capacidades: voz en vivo, biblioteca de audios y texto a voz." },
    () => speakerModal(null)),
  "#/io-modules/new": formPageRoute({ back: "#/io-modules", icon: "mdi-toggle-switch",
    hint: "Al guardar se lee el modelo y se arman sus entradas y relés, que después puede nombrar." },
    () => ioModuleModal(null)),
  "#/intercoms/new": formPageRoute({ back: "#/intercoms", icon: "mdi-deskphone",
    hint: "Al guardar se validan las credenciales contra el equipo y se configura su botón para que llame a la central." },
    (prefill) => intercomModal(null, prefill)),
  "#/decoders/new": formPageRoute({ back: "#/decoders", icon: "mdi-monitor-multiple",
    hint: "Al guardar se validan las credenciales contra el equipo y se leen sus salidas físicas y canales de decodificación; después se abre su página." },
    (prefill) => decoderModal(prefill ?? {})),
  "#/fleet/vehicles/new": formPageRoute({ back: "#/fleet/vehicles", icon: "mdi-bus",
    hint: "En el grabador móvil hay que apuntar la plataforma ISUP (EHome) a este servidor con el mismo ID ISUP: sus cámaras aparecen solas cuando se conecta." },
    (_, query) => flVehicleModal(null, query.get("detected") ?? undefined)),
  "#/access-monitor": renderAccessMonitor,
  "#/access-persons": renderAccessPersons,
  "#/access-levels": renderAccessLevels,
  "#/access-schedules": renderAccessSchedules,
  "#/access-events": renderAccessEvents,
  "#/speakers": renderSpeakers,
  "#/io-modules": renderIoModules,
  "#/patrol": renderPatrol,
  "#/patrol-reports": renderPatrolReports,
  "#/alarm-screens": renderAlarmScreens,
  "#/intercoms": renderIntercoms,
  "#/device-maintenance": renderDeviceMaintenance,
  "#/workflows": renderWorkflows,
  "#/workflows/edit": renderWorkflowEditor,
  "#/workflow-alerts": renderWorkflowAlerts,
  "#/workflow-runs": renderWorkflowRuns,
  "#/sounds": renderSounds,
  "#/anpr": renderAnpr,
  "#/vehicle-access": renderVehicleAccess,
  "#/alarms": renderAlarmMonitor,
  "#/cerco": renderCercoMonitor,
  "#/cerco-panels": renderCercoPanels,
  "#/live": renderLiveView,
  "#/fleet/live": renderFleetLive,
  "#/fleet/map": renderFleetMap,
  "#/fleet/track": renderFleetTrack,
  "#/fleet/vehicles": renderFleetVehicles,
  "#/playback": renderPlayback,
  "#/event-center": renderEventCenter,
  "#/videowall": renderVideowall,
  "#/intercom-console": renderIntercomConsole,
  "#/decoders": renderDecoders,
  "#/decoders/device": renderDecoderPage,
  "#/walls": renderWalls,
  "#/walls/edit": renderWallEditor,
  "#/resources": renderResources,
  "#/boards": renderBoards,
  "#/sessions": renderSessions,
  "#/users": renderUsers,
  "#/roles": renderRoles,
  "#/audit": renderAudit,
  "#/services": renderServices,
  "#/isup": renderIsupStatus,
  "#/mail": renderMailSettings,
  "#/backups": renderBackups,
  "#/certificate": renderCertificate,
  "#/account": renderAccount,
  "#/wan": renderWan,
  "#/time": renderTime,

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
      // Lo que se acaba de desplegar queda a la vista (sin perder su título).
      if (group.classList.contains("open")) requestAnimationFrame(() => keepNavVisible(group));
    });
  });
  syncNavAria();
}

function syncNavAria() {
  $$("#nav .nav-group").forEach((g) => {
    g.querySelector(".nav-toggle")?.setAttribute("aria-expanded", g.classList.contains("open") ? "true" : "false");
  });
}

// Deja visible el enlace activo abriendo los nodos que lo contienen y
// desplazando el menú hasta él (el menú tiene su propio scroll).
function revealActiveNav(link) {
  $$("#nav .nav-group").forEach((g) => g.classList.remove("has-active"));
  if (!link) return;
  for (let g = link.closest(".nav-group"); g; g = g.parentElement?.closest(".nav-group")) {
    g.classList.add("open", "has-active");
  }
  saveNavState();
  syncNavAria();
  requestAnimationFrame(() => keepNavVisible(link));
}

/**
 * Desplaza SOLO el menú lateral (nunca la página) hasta que el elemento quede
 * a la vista, con un margen. Si es más alto que el menú, manda su comienzo.
 */
function keepNavVisible(el) {
  const nav = $("#nav");
  if (!nav || !el) return;
  const box = nav.getBoundingClientRect(), r = el.getBoundingClientRect();
  const margin = 28;
  if (r.top < box.top + margin) nav.scrollTop -= box.top + margin - r.top;
  else if (r.bottom > box.bottom - margin)
    nav.scrollTop += Math.min(r.bottom - (box.bottom - margin), r.top - (box.top + margin));
}

/**
 * Migas de pan bajo el título: el camino del enlace activo en el menú
 * (Inicio › Configuración › Dispositivos › Fuentes de video). Los títulos de
 * sección y los nodos no son páginas, así que solo Inicio es enlace.
 */
function showBreadcrumb(link) {
  const ol = $("#breadcrumb");
  if (!ol) return;
  const trail = [];
  if (link && link.getAttribute("href") !== "#/") {
    // Título de sección: el último divisor antes del elemento de primer nivel.
    let top = link;
    while (top.parentElement && top.parentElement.id !== "nav") top = top.parentElement;
    for (let el = top.previousElementSibling; el; el = el.previousElementSibling) {
      if (el.classList.contains("nav-divider")) { trail.push(el.textContent.trim()); break; }
    }
    const groups = [];
    for (let g = link.closest(".nav-group"); g; g = g.parentElement?.closest(".nav-group"))
      groups.unshift(g.querySelector(":scope > .nav-toggle")?.textContent.trim());
    trail.push(...groups.filter(Boolean), link.textContent.trim());
  }
  ol.replaceChildren();
  const home = document.createElement("li");
  if (trail.length) {
    const a = document.createElement("a");
    a.href = "#/";
    a.textContent = "Inicio";
    home.append(a);
  } else home.textContent = "Inicio";
  ol.append(home);
  trail.forEach((text) => {
    const li = document.createElement("li");
    li.textContent = text;
    ol.append(li);
  });
}

/** Suelta lo que la página actual dejó corriendo: sondeos, hub, sonidos y menús. */
/** Acciones principales de la página (botones), en la cabecera a la altura del título. */
function setPageActions(html) {
  $("#page-actions").innerHTML = html || "";
}

// La barra principal de una página (.page-toolbar) no repite el título: sus
// botones y filtros suben a la cabecera, a la altura del título. Mover los nodos
// conserva sus eventos; también vale cuando la página se vuelve a dibujar.
new MutationObserver(() => {
  const bar = document.querySelector("#view .page-toolbar");
  if (!bar) return;
  $("#page-actions").replaceChildren(...bar.childNodes);
  bar.remove();
}).observe(document.querySelector("#view"), { childList: true, subtree: true });

function leaveCurrentPage() {
  setPageActions("");            // las acciones son de la página que se deja
  leaveFormPage();               // el alta de un equipo como página
  clearInterval(sessionsTimer);  // el sondeo de sesiones vive solo en su página
  dashboardLeave();              // ídem el refresco y el reloj del panel de inicio
  clearInterval(wallsTimer);     // ídem el del estado de los muros
  clearInterval(discoveryTimer); // ídem el de equipos en línea
  clearInterval(alarmsTimer);    // ídem el de paneles de alarma
  clearInterval(speakersTimer);  // ídem el de parlantes IP
  clearInterval(intercomsTimer); // ídem el de citofonía
  clearInterval(accessTimer);    // ídem el de control de acceso
  clearInterval(maintTimer);     // ídem el de hora y mantenimiento
  clearInterval(maintTick);      // y sus relojes
  clearInterval(accessDoorsTimer);  // ídem el del monitoreo de puertas
  clearInterval(accessEventsTimer); // ídem el del historial de accesos
  clearInterval(workflowsTimer); // ídem el del historial de automatizaciones
  clearInterval(servicesTimer);  // ídem el del supervisor de servicios
  clearInterval(anprTimer);      // ídem el de lecturas de patentes
  clearInterval(alarmMonTimer);  // ídem el del monitoreo de alarmas
  if (typeof cercoDetachHub === "function") cercoDetachHub(); // suelta hub + delegaciones de cerco
  if (typeof accessDetachHub === "function") accessDetachHub(); // ídem el monitoreo de puertas
  if (typeof resourcesDetachHub === "function") resourcesDetachHub(); // ídem la página Recursos
  if (typeof boardsDetachHub === "function") boardsDetachHub();       // ídem la página Tableros
  if (typeof rolesDetachHub === "function") rolesDetachHub(); // ídem la página Roles
  if (typeof ioModulesDetachHub === "function") ioModulesDetachHub(); // ídem los módulos de E/S
  if (typeof patrolDetachHub === "function") patrolDetachHub(); // ídem el control de rondas
  if (typeof fleetVehiclesDetachHub === "function") fleetVehiclesDetachHub(); // ídem los vehículos de Flota
  if (typeof alarmScreensDetachHub === "function") alarmScreensDetachHub(); // ídem las pantallas de alarma
  clearInterval(eventCenterTimer); // ídem el del centro de eventos
  evcStopSound();                // y su alarma sonora no sigue en otra página
  clearInterval(videowallTimer); // ídem el del puesto de videowall
  document.getElementById("vw-menu")?.remove();
  intercomConsoleLeave();        // timbre, conversación y sondeo de la citofonía
  liveViewLeave();               // video en vivo (WebRTC), grabaciones locales, PTZ y voz
  if (typeof fleetMapLeave === "function") fleetMapLeave(); // mapa de flota: MapLibre, video de la ficha y hub
  if (typeof fleetTrackLeave === "function") fleetTrackLeave(); // recorridos: MapLibre, animación y video grabado
  playbackLeave();              // reproducción de grabaciones (WebRTC) y su línea de tiempo
}

// Capítulo del manual (sitio HTML en /manual) de cada página del panel. Las
// que no tienen capítulo abren la portada; un capítulo que la edición no trae
// también lleva a la portada (lo resuelve el servidor).
const MANUAL_CHAPTERS = {
  "#/live": "03-vista-en-vivo", "#/playback": "04-reproduccion", "#/event-center": "05-centro-de-eventos",
  "#/alarms": "06-paneles-de-alarma", "#/alarm-panels": "06-paneles-de-alarma",
  "#/cerco": "07-cerco-electrico", "#/cerco-panels": "07-cerco-electrico",
  "#/access": "08-control-de-acceso", "#/access-events": "08-control-de-acceso", "#/access-levels": "08-control-de-acceso",
  "#/access-monitor": "08-control-de-acceso", "#/access-persons": "08-control-de-acceso",
  "#/access-schedules": "08-control-de-acceso", "#/vehicle-access": "08-control-de-acceso",
  "#/intercoms": "09-citofonia", "#/intercom-console": "09-citofonia",
  "#/speakers": "10-parlantes-ip", "#/sounds": "10-parlantes-ip", "#/io-modules": "10b-modulos-de-es",
  "#/alarm-screens": "10c-pantallas-de-alarma",
  "#/walls": "11-muro-de-video", "#/videowall": "11-muro-de-video", "#/decoders": "11-muro-de-video",
  "#/anpr": "12-reconocimiento-de-patentes",
  "#/workflows": "13-automatizaciones", "#/workflow-runs": "13-automatizaciones", "#/workflow-alerts": "13-automatizaciones",
  "#/devices": "14-fuentes-de-video", "#/device-maintenance": "14-fuentes-de-video",
  "#/resources": "15-recursos-y-ubicaciones", "#/boards": "03-vista-en-vivo",
  "#/users": "16-usuarios-y-roles", "#/roles": "16-usuarios-y-roles", "#/audit": "16-usuarios-y-roles",
  "#/sessions": "17-sistema", "#/services": "17-sistema", "#/isup": "17-sistema", "#/license": "17-sistema",
  "#/backups": "17-sistema", "#/mail": "17-sistema", "#/time": "17-sistema", "#/wan": "17-sistema",
  "#/certificate": "17-sistema",
};

function manualHref(base) {
  const route = Object.keys(MANUAL_CHAPTERS).find((r) => base === r || base.startsWith(r + "/"));
  return route ? `manual/${MANUAL_CHAPTERS[route]}.html` : "manual/";
}

function navigate() {
  leaveCurrentPage();
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
  showBreadcrumb(active);
  const help = active?.dataset.help ?? "";
  $("#page-help").textContent = help;
  $("#page-help").classList.toggle("hidden", !help);
  $("#btn-manual").href = manualHref(base);
  // Una página que sus roles no permiten (enlace guardado, historial, o le
  // quitaron el permiso estando en ella) no se dibuja: el servidor la
  // rechazaría a medias, solicitud por solicitud.
  const routePerm = active?.dataset.perm;
  if (routePerm && !Perms.can(routePerm)) {
    $("#view").className = "";
    $("#page-title").textContent = active.textContent.trim();
    $("#view").innerHTML = `<div class="info-box" style="margin:24px">Sus roles no incluyen el acceso a esta página.
      Si lo necesita, pídaselo a quien administra los usuarios.</div>`;
    return;
  }
  // Ídem un módulo que la licencia no incluye (enlace guardado o la licencia cambió).
  if (active && !Perms.allowedByLicense(active)) {
    const where = Perms.can("system.license") ? ` (<a href="#/license">Sistema → Licencia</a>)` : "";
    $("#view").className = "";
    $("#page-title").textContent = active.textContent.trim();
    $("#view").innerHTML = Perms.operational
      ? `<div class="info-box" style="margin:24px">Este módulo no está incluido en la licencia del sistema.
          Para habilitarlo hay que ampliar la licencia${where}.</div>`
      : `<div class="error-box" style="margin:24px">La licencia del sistema no está vigente: esta página no está
          disponible hasta regularizarla${where}.</div>`;
    return;
  }
  // Las páginas a pantalla completa (editor de automatizaciones) cambian la
  // clase del contenedor; cada navegación parte limpia.
  $("#view").className = "";
  // El menú lateral que el usuario abrió a mano en el editor no se arrastra a otra página.
  $("#app-shell").classList.remove("sidebar-open");
  $("#btn-menu").setAttribute("aria-expanded", "false");
  render();
}

// ---------------------------------------------------------------------------
// Fin de la sesión. Una sesión vence a las horas de haber ingresado y se corta
// antes si un administrador cambia la cuenta o si el servidor se reinicia (las
// sesiones viven en su memoria). El panel no espera a que el usuario haga algo
// para darse cuenta: revisa al volver a la pestaña o a la ventana, cada minuto
// mientras está a la vista y a la hora exacta del vencimiento. Si terminó,
// lleva al ingreso diciendo por qué.
// ---------------------------------------------------------------------------
const SessionWatch = {
  active: false,
  expiresAt: 0,   // ms; 0 = todavía no se sabe
  lastCheck: 0,
  timer: null,
  ticker: null,

  start() {
    this.active = true;
    this.lastCheck = 0;
    clearInterval(this.ticker);
    this.ticker = setInterval(() => { if (!document.hidden) this.check(); }, 60000);
    this.check(true);
  },

  stop() {
    this.active = false;
    clearTimeout(this.timer);
    clearInterval(this.ticker);
    this.timer = this.ticker = null;
  },

  /** Pregunta al servidor si la sesión sigue (a lo más una vez cada 5 s, salvo que se fuerce). */
  async check(force) {
    if (!this.active || !Api.token) return;
    const now = Date.now();
    if (!force && now - this.lastCheck < 5000) return;
    this.lastCheck = now;
    let session;
    try { session = await Api.get("/api/auth/session"); }
    catch (err) {
      if (err.status === 401) this.end();
      return; // sin conexión no se puede saber: ya lo dice la sonda de conexión
    }
    this.expiresAt = Date.parse(session.expiresAt) || 0;
    clearTimeout(this.timer);
    if (this.expiresAt) {
      // Apenas después del vencimiento (los navegadores demoran los
      // temporizadores de las pestañas ocultas; al volver se revisa igual).
      const wait = Math.min(Math.max(this.expiresAt - Date.now() + 1500, 1000), 2 ** 31 - 1);
      this.timer = setTimeout(() => this.check(true), wait);
    }
  },

  /**
   * La sesión terminó: se suelta todo y se lleva al ingreso con el motivo.
   * cause: "other-tab" (cerrada en otra pestaña), "expired", "closed" o nada
   * (se deduce: si ya pasó la hora de vencimiento, venció; si no, la cerraron).
   */
  end(cause) {
    if (!this.active) return;
    if (!cause) cause = this.expiresAt > 0 && Date.now() >= this.expiresAt - 2000 ? "expired" : "closed";
    const at = this.expiresAt
      ? new Date(this.expiresAt).toLocaleTimeString("es-CL", { hour: "2-digit", minute: "2-digit", hour12: false }) : null;
    this.stop();
    leaveCurrentPage();
    CercoAlarm.reset();
    closeModal();
    // Las demás pestañas se enteran al borrarse el token: el motivo va antes.
    if (cause !== "other-tab") SessionWatch.mark(cause);
    Api.clearSession();
    renderLogin(cause === "other-tab"
      ? "Se cerró la sesión desde otra pestaña de este navegador. Ingrese de nuevo para seguir."
      : cause === "expired"
        ? `Su sesión venció${at ? ` a las ${at}` : ""}. Ingrese de nuevo para seguir.`
        : "Su sesión se cerró: un administrador cambió su cuenta o el servidor se reinició. Ingrese de nuevo para seguir.",
      "warn");
  },

  /** Deja anotado por qué terminó la sesión, para las otras pestañas de este navegador. */
  mark(cause) {
    try { localStorage.setItem("tcvms_session_end", JSON.stringify({ cause, at: Date.now() })); } catch { /* sin almacenamiento */ }
  },

  /** El motivo que dejó otra pestaña (si es reciente); el cierre a mano se informa como tal. */
  causeFromOtherTab() {
    let marker = null;
    try { marker = JSON.parse(localStorage.getItem("tcvms_session_end") || "null"); } catch { /* ilegible */ }
    return marker && Date.now() - marker.at < 15000 && marker.cause !== "logout" ? marker.cause : "other-tab";
  },
};

// Volver a la pestaña o a la ventana, o recuperar la red: ¿la sesión sigue?
document.addEventListener("visibilitychange", () => { if (!document.hidden) SessionWatch.check(); });
window.addEventListener("focus", () => SessionWatch.check());
window.addEventListener("online", () => SessionWatch.check(true));
// Otra pestaña de este navegador cerró la sesión o entró con otro usuario
// (el token se comparte): esta se pone al día.
window.addEventListener("storage", (e) => {
  if (e.key !== "tcvms_token" || !SessionWatch.active) return;
  if (!e.newValue) SessionWatch.end(SessionWatch.causeFromOtherTab());
  else location.reload();
});

async function enterApp() {
  Perms.reset();
  await Perms.load();          // qué puede hacer: el menú y las páginas se adaptan
  showAppShell();
  SessionWatch.start();        // avisa apenas termine la sesión, aunque nadie toque nada
  Operable.load();             // qué puede operar: lo demás se deshabilita
  VmsHub.on("ConfigChanged", onOperableTopic);
  VmsHub.onReconnected(onOperableReconnected);
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

// Una llamada a la API respondió 401: la sesión terminó.
window.addEventListener("tcvms:unauthorized", () => {
  if (SessionWatch.active) SessionWatch.end();
  else renderLogin("Su sesión terminó. Ingrese de nuevo para seguir.", "warn");
});

// Botón ☰: en pantallas angostas y en las páginas a pantalla completa el menú
// lateral se muestra encima del contenido (sidebar-open); en el resto se
// contrae a una columna de íconos (sidebar-mini, recordado por navegador).
const SIDEBAR_MINI_KEY = "tcvms.sidebar.mini";
try { if (localStorage.getItem(SIDEBAR_MINI_KEY) === "1") $("#app-shell").classList.add("sidebar-mini"); } catch { /* sin almacenamiento */ }
$("#btn-menu").addEventListener("click", () => {
  const shell = $("#app-shell");
  const overlay = matchMedia("(max-width: 900px)").matches || !!$("#view.view-editor");
  if (overlay) {
    const open = shell.classList.toggle("sidebar-open");
    $("#btn-menu").setAttribute("aria-expanded", String(open));
    return;
  }
  const mini = shell.classList.toggle("sidebar-mini");
  try { localStorage.setItem(SIDEBAR_MINI_KEY, mini ? "1" : "0"); } catch { /* modo privado */ }
});
// Con el menú encima del contenido, elegir una página o tocar fuera lo cierra.
document.addEventListener("click", (e) => {
  const shell = $("#app-shell");
  if (!shell.classList.contains("sidebar-open") || e.target.closest("#btn-menu")) return;
  if (e.target.closest(".sidebar") && !e.target.closest("#nav a")) return;
  shell.classList.remove("sidebar-open");
  $("#btn-menu").setAttribute("aria-expanded", "false");
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

// Instalador del cliente de escritorio: la suite lo deja publicado en el
// servidor (la descarga queda en la bitácora como system/client-installer-downloaded).
$("#btn-download-client").addEventListener("click", async () => {
  setUserMenu(false);
  let info = null;
  try { info = await Api.get("/api/system/client-installer/info"); } catch { /* servidor viejo o caído */ }
  if (!info?.available) {
    toast("El instalador del cliente no está publicado en este servidor (carpeta client-setup).", true);
    return;
  }
  window.location.href = `/api/system/client-installer?access_token=${encodeURIComponent(Api.token)}`;
  toast(`Descargando el cliente${info.version ? ` ${info.version}` : ""} (${info.sizeMb} MB)…`);
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

// "Acerca de": producto, fabricante, versión/build del servidor, plataforma y
// estado de la licencia (lo que soporte pide primero).
$("#btn-account").addEventListener("click", () => {
  setUserMenu(false);
  location.hash = "#/account";
});

$("#btn-about").addEventListener("click", async () => {
  setUserMenu(false);
  let a;
  try { a = await Api.get("/api/system/about"); } catch (err) { toast(err.error ?? "No se pudo leer la información del sistema.", true); return; }
  const l = a.license ?? {};
  const row = (k, v) => `<tr><th>${esc(k)}</th><td>${v ?? `<span class="muted">—</span>`}</td></tr>`;
  const licensed = l.state === "Active" || l.state === "GracePeriod";
  openModal(`
    <div class="about-head">
      <div class="about-logo" aria-hidden="true">TC</div>
      <div>
        <h3 style="margin:0">${esc(a.product)}</h3>
        <div class="muted">Versión <b>${esc(a.version)}</b>${a.build ? ` · build <b>${esc(a.build)}</b>` : ""}</div>
      </div>
    </div>
    <div class="about-license ${licensed ? "ok" : l.state === "Trial" ? "trial" : "bad"}">
      ${licenseStateTag(l.state)}
      <span>${licensed ? "Producto licenciado" : l.state === "Trial" ? "Versión de evaluación: aún no está licenciado" : "Producto sin licencia vigente"}${
        l.daysRemaining != null ? ` · ${l.daysRemaining} día(s) restantes` : ""}</span>
    </div>
    <table class="about-table">
      ${row("Fabricante", esc(a.manufacturer))}
      ${row("Versión", esc(a.version))}
      ${row("Build", a.build ? esc(a.build) : null)}
      ${row("Versión de archivo", a.fileVersion ? esc(a.fileVersion) : null)}
      ${row("Compilado", a.builtAt ? esc(formatDate(a.builtAt)) : null)}
      ${row("Licencia", l.licenseKey ? `<code>${esc(l.licenseKey)}</code>` : null)}
      ${row("Cliente", l.customerName ? esc(l.customerName) : null)}
      ${row("Paquete", l.package ? esc(l.package) : null)}
      ${row("Modalidad", esc(licenseModeLabel(l.mode ?? "NONE")))}
      ${row("Vence", l.expiresAt ? esc(formatDate(l.expiresAt)) : null)}
      ${row("ID de hardware", l.hardwareId ? `<code>${esc(l.hardwareId)}</code>` : null)}
      ${row("Servidor", esc(a.hostname))}
      ${row("Sistema operativo", esc(a.os))}
      ${row("Plataforma", esc(a.runtime))}
      ${row("En ejecución desde", esc(formatDate(a.startedAt)))}
    </table>
    ${l.warning ? `<div class="info-box" style="margin-top:10px">${esc(l.warning)}</div>` : ""}
    <div class="muted" style="font-size:11px;margin-top:10px">${esc(a.copyright ?? "")}</div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="about-copy">Copiar datos</button>
      ${Perms.can("system.license") ? `<button class="btn ghost" type="button" id="about-license">Ver licencia</button>` : ""}
      <button class="btn" type="button" id="about-close">Cerrar</button>
    </div>`);
  $("#about-close").addEventListener("click", closeModal);
  $("#about-license")?.addEventListener("click", () => { closeModal(); location.hash = "#/license"; });
  $("#about-copy").addEventListener("click", async () => {
    const txt = [
      `${a.product} ${a.version}${a.build ? ` (build ${a.build})` : ""}`,
      `Fabricante: ${a.manufacturer}`,
      `Licencia: ${(LICENSE_STATES[l.state] || { label: l.state }).label}${l.licenseKey ? ` (${l.licenseKey})` : ""}`,
      `ID de hardware: ${l.hardwareId ?? "—"}`,
      `Servidor: ${a.hostname} · ${a.os} · ${a.runtime}`,
    ].join("\n");
    try { await navigator.clipboard.writeText(txt); toast("Datos copiados."); }
    catch { toast("No se pudo copiar.", true); }
  });
});

$("#btn-logout").addEventListener("click", async () => {
  setUserMenu(false);
  SessionWatch.stop();
  SessionWatch.mark("logout");
  try { await Api.post("/api/auth/logout"); } catch { /* la sesión local se limpia igual */ }
  leaveCurrentPage();
  CercoAlarm.reset();
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
    } catch (err) {
      // La que había guardada ya no vale (venció, la cerraron o el servidor se
      // reinició): se dice, en vez de mostrar el ingreso como si nada.
      if (err.status === 401) {
        Api.clearSession();
        renderLogin("Su sesión anterior terminó. Ingrese de nuevo para seguir.", "warn");
        return;
      }
      // Sin conexión: la sesión puede seguir viva; se conserva para el próximo intento.
      renderLogin("No se pudo hablar con el servidor: revise la conexión e ingrese de nuevo.", "warn");
      return;
    }
  }
  renderLogin();
}

// Página incrustada en el cliente de escritorio (?embed=1, WebView2 con la sesión del cliente): solo el contenido, sin
// menú ni barra superior. El cliente atiende lo que la página le pide por window.chrome.webview.postMessage.
if (new URLSearchParams(location.search).get("embed") === "1") document.body.classList.add("embedded");

init();
