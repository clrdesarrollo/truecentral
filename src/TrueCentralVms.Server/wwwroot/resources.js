// CLR TrueCentral VMS — panel: Recursos (árbol de ubicaciones).
//
// La capa lógica del inventario. Los equipos físicos se siguen administrando
// en Dispositivos; aquí se ordena lo que el operador usa —cámaras, puertas,
// áreas y zonas de alarma, cercos, parlantes y citófonos— según DÓNDE está:
// sitio → edificio → piso → sector → punto. Cada recurso vive en una sola
// ubicación; lo que descubre un equipo nuevo cae en "Por ubicar".
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

const RES_KINDS = {
  Camera:    { label: "Cámara",         plural: "Cámaras",         icon: "camera" },
  Door:      { label: "Puerta",         plural: "Puertas",         icon: "door" },
  Partition: { label: "Área de alarma", plural: "Áreas de alarma", icon: "shield" },
  Zone:      { label: "Zona",           plural: "Zonas",           icon: "sensor" },
  Fence:     { label: "Cerco",          plural: "Cercos",          icon: "fence" },
  Speaker:   { label: "Parlante",       plural: "Parlantes",       icon: "speaker" },
  Intercom:  { label: "Citófono",       plural: "Citófonos",       icon: "intercom" },
};
const RES_KIND_ORDER = Object.keys(RES_KINDS);

const LOC_KINDS = {
  Site:     { label: "Sitio",    icon: "pin" },
  Building: { label: "Edificio", icon: "building" },
  Floor:    { label: "Piso",     icon: "layers" },
  Sector:   { label: "Sector",   icon: "sector" },
  Point:    { label: "Punto",    icon: "point" },
};
// Tipo sugerido para una sububicación según el tipo de la ubicación que la contiene.
const LOC_NEXT_KIND = { root: "Site", Site: "Building", Building: "Floor", Floor: "Sector", Sector: "Point", Point: "Point" };

const RES_ICONS = {
  pin: '<path d="M12 21s-6-5.3-6-11a6 6 0 1 1 12 0c0 5.7-6 11-6 11z"/><circle cx="12" cy="10" r="2"/>',
  building: '<rect x="5" y="3" width="14" height="18" rx="1"/><path d="M9 7h1M14 7h1M9 11h1M14 11h1M9 15h1M14 15h1M11 21v-3h2v3"/>',
  layers: '<path d="M12 4l8 4-8 4-8-4z"/><path d="M4 12l8 4 8-4"/><path d="M4 16l8 4 8-4"/>',
  sector: '<rect x="4" y="4" width="16" height="16" rx="2" stroke-dasharray="3 2.5"/>',
  point: '<circle cx="12" cy="12" r="3"/><circle cx="12" cy="12" r="8"/>',
  camera: '<rect x="3" y="7" width="12" height="10" rx="2"/><path d="M15 11l6-3v8l-6-3"/>',
  door: '<path d="M6 21V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v17"/><path d="M4 21h16"/><path d="M14.5 12h.01"/>',
  shield: '<path d="M12 3l7 3v5c0 5-3.5 8.5-7 10-3.5-1.5-7-5-7-10V6z"/>',
  sensor: '<circle cx="12" cy="12" r="2"/><path d="M8.5 8.5a5 5 0 0 0 0 7M15.5 8.5a5 5 0 0 1 0 7M5.6 5.6a9 9 0 0 0 0 12.8M18.4 5.6a9 9 0 0 1 0 12.8"/>',
  fence: '<path d="M6 21V7l2-3 2 3v14M14 21V7l2-3 2 3v14M3 10h18M3 16h18"/>',
  speaker: '<path d="M5 9h3l5-4v14l-5-4H5z"/><path d="M16 9a4 4 0 0 1 0 6M18.5 6.5a8 8 0 0 1 0 11"/>',
  intercom: '<rect x="6" y="3" width="12" height="18" rx="2"/><circle cx="12" cy="9" r="2"/><path d="M10 15h4M10 18h4"/>',
  inbox: '<path d="M3 13h5l1 3h6l1-3h5"/><path d="M5.5 5h13L21 13v6H3v-6z"/>',
  all: '<path d="M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01"/>',
  caret: '<path d="M9 6l6 6-6 6"/>',
  link: '<path d="M14 4h6v6M20 4l-9 9"/><path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
};

function resIcon(name, size = 15, cls = "") {
  return `<svg class="res-ico ${cls}" viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${RES_ICONS[name] || ""}</svg>`;
}

const resCollator = new Intl.Collator("es", { numeric: true, sensitivity: "base" });
const resFold = (s) => String(s ?? "").normalize("NFD").replace(/\p{Diacritic}/gu, "").toLowerCase();
const resKey = (r) => `${r.kind}:${r.id}`;

/** Estado de la página: sobrevive a salir y volver (selección, filtros, nodos plegados). */
const resState = {
  locations: [],
  byId: new Map(),
  children: new Map(),      // id del padre ("root" = raíz) → ubicaciones hijas ordenadas
  resources: [],
  selected: "all",          // "all" | "pending" | id de ubicación
  kind: "",                 // filtro por tipo de recurso ("" = todos)
  source: "",               // filtro por equipo "módulo:id" ("" = todos)
  text: "",
  checked: new Set(),       // claves "Tipo:Id" marcadas
  collapsed: new Set(),     // ubicaciones plegadas en el árbol
  hubBound: [],
  reconnectUnsub: null,
  reloadTimer: null,
  pollTimer: null,
  signature: "",            // última respuesta dibujada: sin cambios no se redibuja
  dragging: false,          // mientras se arrastra no se redibuja (cancelaría el arrastre)
  sheet: null,              // ficha abierta: { kind, id, tab, detail, workflows, history }
};

try {
  for (const id of JSON.parse(localStorage.getItem("tcvms.res.collapsed") || "[]")) resState.collapsed.add(id);
} catch { /* preferencia corrupta: se ignora */ }

/** Suelta hub y sondeo al salir de la página (lo llama navigate() de app.js). */
function resourcesDetachHub() {
  for (const { ev, fn } of resState.hubBound) VmsHub.off(ev, fn);
  resState.hubBound = [];
  resState.reconnectUnsub?.();
  resState.reconnectUnsub = null;
  clearInterval(resState.pollTimer);
  clearTimeout(resState.reloadTimer);
}

const RES_TOPICS = new Set(["locations", "devices", "channels", "access-devices", "alarm-panels", "speakers", "intercoms"]);

function resScheduleReload() {
  clearTimeout(resState.reloadTimer);
  resState.reloadTimer = setTimeout(() => {
    if (location.hash !== "#/resources" || $("#app-shell").classList.contains("hidden")) return;
    if (resState.dragging) { resScheduleReload(); return; } // se reintenta al soltar
    resLoad({ quiet: true });
  }, 400);
}

// ---------------------------------------------------------------------------
// Datos
// ---------------------------------------------------------------------------

function resIndex() {
  resState.byId = new Map(resState.locations.map((l) => [l.id, l]));
  resState.children = new Map();
  for (const l of resState.locations) {
    const key = l.parentId ?? "root";
    if (!resState.children.has(key)) resState.children.set(key, []);
    resState.children.get(key).push(l);
  }
  for (const list of resState.children.values()) list.sort((a, b) => resCollator.compare(a.name, b.name));
  // Una selección que ya no existe (otra persona borró la ubicación) vuelve a "Todos".
  if (typeof resState.selected === "number" && !resState.byId.has(resState.selected)) resState.selected = "all";
  const alive = new Set(resState.resources.map(resKey));
  for (const key of [...resState.checked]) if (!alive.has(key)) resState.checked.delete(key);
}

/** La ubicación y todas sus sububicaciones. */
function resSubtree(id) {
  const out = new Set();
  const stack = [id];
  while (stack.length) {
    const current = stack.pop();
    if (out.has(current)) continue;
    out.add(current);
    for (const child of resState.children.get(current) || []) stack.push(child.id);
  }
  return out;
}

/** Nombres desde la raíz hasta la ubicación (incluida). */
function resPath(id) {
  const parts = [];
  const seen = new Set();
  let current = resState.byId.get(id);
  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    parts.unshift(current.name);
    current = current.parentId != null ? resState.byId.get(current.parentId) : null;
  }
  return parts;
}

/** Recursos por ubicación contando sus sububicaciones. */
function resSubtreeCounts() {
  const direct = new Map();
  for (const r of resState.resources)
    if (r.locationId != null) direct.set(r.locationId, (direct.get(r.locationId) || 0) + 1);
  const total = new Map();
  const sum = (id) => {
    if (total.has(id)) return total.get(id);
    total.set(id, 0); // corta ciclos imposibles
    let n = direct.get(id) || 0;
    for (const child of resState.children.get(id) || []) n += sum(child.id);
    total.set(id, n);
    return n;
  };
  for (const l of resState.locations) sum(l.id);
  return total;
}

/** Recursos de la selección del árbol, antes de los filtros de la lista. */
function resInSelection() {
  if (resState.selected === "all") return resState.resources;
  if (resState.selected === "pending") return resState.resources.filter((r) => r.locationId == null);
  const ids = resSubtree(resState.selected);
  return resState.resources.filter((r) => r.locationId != null && ids.has(r.locationId));
}

function resFiltered(list) {
  const needle = resFold(resState.text.trim());
  return list
    .filter((r) => !resState.kind || r.kind === resState.kind)
    .filter((r) => !resState.source || `${r.sourceModule}:${r.sourceId}` === resState.source)
    .filter((r) => !needle || resFold(`${r.name} ${r.source} ${r.detail ?? ""} ${r.state}`).includes(needle))
    .sort((a, b) => RES_KIND_ORDER.indexOf(a.kind) - RES_KIND_ORDER.indexOf(b.kind) || resCollator.compare(a.name, b.name));
}

/** Carga árbol y recursos. quiet = refresco de fondo: sin cambios no se toca
 *  el DOM (no se pierden el foco, el desplazamiento ni la fila bajo el mouse). */
async function resLoad({ quiet = false } = {}) {
  let locations, resources;
  try {
    [locations, resources] = await Promise.all([Api.get("/api/locations"), Api.get("/api/resources")]);
  } catch (err) {
    if (quiet) return; // el próximo refresco reintenta
    $("#res-main") ? toast(err.error, true) : ($("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`);
    return;
  }
  const signature = JSON.stringify([locations, resources]);
  if (quiet && signature === resState.signature) return;
  resState.signature = signature;
  resState.locations = locations;
  resState.resources = resources;
  resIndex();
  resDraw();
}

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------

async function renderResources() {
  $("#page-title").textContent = "Recursos";
  resourcesDetachHub();
  const isAdmin = Api.role === "Admin";
  // #/resources?r=Door:12&tab=history abre la ficha de ese recurso (se puede enlazar).
  const params = new URLSearchParams(location.hash.split("?")[1] || "");
  const [openKind, openId] = (params.get("r") || "").split(":");
  resState.sheet = RES_KINDS[openKind] && Number(openId) > 0
    ? { kind: openKind, id: Number(openId), tab: params.get("tab") || "general", detail: null, workflows: null, history: null }
    : null;

  $("#view").innerHTML = `
    <div class="res-layout">
      <aside class="res-tree" id="res-tree" aria-label="Ubicaciones"></aside>
      <section id="res-main">${resState.sheet ? `<div id="res-sheet"><div class="muted">Cargando la ficha…</div></div>` : `
        <div class="res-head" id="res-head"></div>
        <div class="res-chips" id="res-chips"></div>
        <div class="res-filters">
          <input type="search" id="res-text" placeholder="Buscar por nombre, equipo o estado…" value="${esc(resState.text)}">
          <select id="res-source" title="Solo los recursos de un equipo"></select>
        </div>
        <div id="res-bulk"></div>
        <div id="res-table"></div>`}
      </section>
    </div>`;

  if (!resState.sheet) {
    $("#res-text").addEventListener("input", (e) => { resState.text = e.target.value; resDrawList(); });
    $("#res-source").addEventListener("change", (e) => { resState.source = e.target.value; resDrawList(); });
    resBindList(isAdmin);
  }
  resBindTree(isAdmin);

  // Tiempo real: cualquier cambio de ubicaciones o de inventario rehace la
  // página; el estado de cada recurso (en línea, armado…) se refresca cada 15 s.
  const onConfig = (topic) => { if (RES_TOPICS.has(topic)) resScheduleReload(); };
  VmsHub.on("ConfigChanged", onConfig);
  resState.hubBound.push({ ev: "ConfigChanged", fn: onConfig });
  resState.reconnectUnsub = VmsHub.onReconnected(resScheduleReload);
  resState.pollTimer = setInterval(resScheduleReload, 15000);

  $("#res-tree").innerHTML = `<div class="muted" style="padding:8px">Cargando…</div>`;
  await resLoad();
  if (resState.sheet) await resOpenSheet();
}

function resDraw() {
  resDrawTree();
  if (resState.sheet) { resSheetRefreshState(); return; } // con la ficha abierta, la lista no está en pantalla
  resDrawHead();
  resDrawSourceFilter();
  resDrawList();
}

function resDrawTree() {
  const isAdmin = Api.role === "Admin";
  const counts = resSubtreeCounts();
  const pending = resState.resources.filter((r) => r.locationId == null).length;
  const sel = resState.selected;
  const drag = isAdmin ? ' draggable="true"' : "";
  const scrollTop = $("#res-tree").scrollTop; // redibujar no debe devolver el árbol al inicio

  const branch = (parentKey, depth) => (resState.children.get(parentKey) || []).map((l) => {
    const kids = resState.children.get(l.id) || [];
    const open = !resState.collapsed.has(l.id);
    const kind = LOC_KINDS[l.kind] || LOC_KINDS.Sector;
    return `
      <div class="res-node${sel === l.id ? " active" : ""}" data-loc="${l.id}"${drag}
           style="padding-left:${6 + depth * 16}px" title="${esc(kind.label)}${l.description ? " · " + esc(l.description) : ""}">
        <span class="res-caret-btn" data-toggle="${kids.length ? l.id : ""}">${resIcon("caret", 13, "res-caret" + (kids.length ? (open ? " open" : "") : " none"))}</span>
        ${resIcon(kind.icon)}
        <span class="res-name">${esc(l.name)}</span>
        <span class="res-count">${counts.get(l.id) || 0}</span>
      </div>
      ${kids.length && open ? branch(l.id, depth + 1) : ""}`;
  }).join("");

  $("#res-tree").innerHTML = `
    <div class="res-tree-head">
      <span>Ubicaciones</span>
      ${isAdmin ? `<button class="btn ghost small" id="res-new-root" title="Nueva ubicación en la raíz del árbol">+ Nueva</button>` : ""}
    </div>
    <div class="res-node${sel === "all" ? " active" : ""}" data-special="all" title="Todos los recursos del sistema${isAdmin ? ". Soltar aquí una ubicación la deja en la raíz del árbol." : ""}">
      ${resIcon("all")}<span class="res-name">Todos los recursos</span><span class="res-count">${resState.resources.length}</span>
    </div>
    ${resState.locations.length ? branch("root", 0) : `<div class="muted res-empty-tree">Aún no hay ubicaciones.</div>`}
    <div class="res-sep"></div>
    <div class="res-node pending${sel === "pending" ? " active" : ""}" data-special="pending" title="Recursos que todavía no tienen ubicación${isAdmin ? ". Soltar aquí un recurso le quita la ubicación." : ""}">
      ${resIcon("inbox")}<span class="res-name">Por ubicar</span><span class="res-count${pending ? " has" : ""}">${pending}</span>
    </div>
    ${isAdmin ? `<p class="muted res-hint">Arrastre recursos desde la lista hasta una ubicación, o ubicaciones entre sí para reordenar el árbol.</p>` : ""}`;
  $("#res-tree").scrollTop = scrollTop;
  $("#res-new-root")?.addEventListener("click", () => resLocationModal(null, null));
}

function resDrawHead() {
  const isAdmin = Api.role === "Admin";
  const sel = resState.selected;
  let crumb = "", title, kindTag = "", actions = "", note = "";
  if (sel === "all") {
    title = "Todos los recursos";
    note = "Lo que el operador usa, de todos los equipos. Para ordenarlo por lugar, cree ubicaciones y asígnelas.";
  } else if (sel === "pending") {
    title = "Por ubicar";
    note = "Recursos sin ubicación. Aquí llega lo que descubre un equipo nuevo: márquelos y use «Mover a…», o arrástrelos al árbol.";
  } else {
    const loc = resState.byId.get(sel);
    const path = resPath(sel);
    crumb = path.slice(0, -1).map(esc).join(" › ");
    title = loc.name;
    kindTag = `<span class="tag operator">${esc((LOC_KINDS[loc.kind] || LOC_KINDS.Sector).label)}</span>`;
    const extra = [loc.description, loc.address, loc.latitude != null ? `${loc.latitude}, ${loc.longitude}` : null].filter(Boolean);
    note = extra.map(esc).join(" · ");
    if (isAdmin) actions = `
      <button class="btn ghost small" id="res-loc-child">+ Sububicación</button>
      <button class="btn ghost small" id="res-loc-edit">Editar</button>
      <button class="btn danger small" id="res-loc-delete">Eliminar</button>`;
  }
  // Órdenes sobre la ubicación entera: las áreas de alarma que contiene.
  const areas = typeof sel === "number" ? resInSelection().filter((r) => r.kind === "Partition").length : 0;
  $("#res-head").innerHTML = `
    ${crumb ? `<div class="res-crumb">${crumb}</div>` : ""}
    <div class="res-title">
      <h3>${esc(title)} ${kindTag}</h3>
      <div class="row-actions">${actions}</div>
    </div>
    ${note ? `<div class="muted res-note">${note}</div>` : ""}
    ${areas ? `
    <div class="res-cmdbar">
      ${resIcon("shield", 14)}<span class="muted">${areas} área(s) de alarma en esta ubicación:</span>
      <button class="btn ghost small" type="button" data-op="location:${sel}" data-cmd="arm-away">Armar total</button>
      <button class="btn ghost small" type="button" data-op="location:${sel}" data-cmd="arm-stay">Armar parcial</button>
      <button class="btn ghost small" type="button" data-op="location:${sel}" data-cmd="disarm">Desarmar</button>
    </div>` : ""}`;
  if (typeof sel === "number") {
    $("#res-loc-child")?.addEventListener("click", () => resLocationModal(null, sel));
    $("#res-loc-edit")?.addEventListener("click", () => resLocationModal(resState.byId.get(sel), null));
    $("#res-loc-delete")?.addEventListener("click", () => resDeleteLocation(resState.byId.get(sel)));
    $$("#res-head [data-cmd]").forEach((b) => b.addEventListener("click", () => resLocationCommand(sel, b.dataset.cmd)));
  }
}

/**
 * Arma o desarma todas las áreas de alarma de la ubicación (con sus
 * sububicaciones). Primero pregunta al servidor cuáles tocaría y pide
 * confirmación; cada área se ordena y se audita como en el módulo Alarmas.
 */
async function resLocationCommand(locationId, command) {
  const url = `/api/locations/${locationId}/command`;
  let preview;
  try { preview = await Api.post(url, { command, dryRun: true }); }
  catch (err) { toast(err.error, true); return; }
  if (!preview.items.length) { toast(preview.message); return; }
  const list = preview.items.slice(0, 15).map((i) => `• ${i.area} (${i.panel})`).join("\n") +
    (preview.items.length > 15 ? `\n… y ${preview.items.length - 15} más` : "");
  if (!confirm(`${preview.message}\n\n${list}\n\n¿Continuar?`)) return;
  try {
    const result = await Api.post(url, { command, dryRun: false });
    const failed = result.items.filter((i) => !i.success);
    if (failed.length) {
      openModal(`
        <h3>Resultado de la orden</h3>
        <div class="info-box">${esc(result.message)}</div>
        <ul class="res-fail-list">${failed.map((f) =>
          `<li><b>${esc(f.area)}</b> (${esc(f.panel)}): ${esc(f.error || "sin detalle")}</li>`).join("")}</ul>
        <div class="modal-actions"><button class="btn" type="button" id="res-cmd-ok">Cerrar</button></div>`);
      $("#res-cmd-ok").addEventListener("click", closeModal);
    } else {
      toast(result.message);
    }
    resScheduleReload();
  } catch (err) {
    toast(err.error, true);
  }
}

function resDrawSourceFilter() {
  const sources = new Map();
  for (const r of resInSelection()) {
    const key = `${r.sourceModule}:${r.sourceId}`;
    if (!sources.has(key)) sources.set(key, r.source);
  }
  if (resState.source && !sources.has(resState.source)) resState.source = "";
  const options = [...sources.entries()].sort((a, b) => resCollator.compare(a[1], b[1]));
  $("#res-source").innerHTML = `<option value="">Todos los equipos</option>` +
    options.map(([key, name]) => `<option value="${esc(key)}"${key === resState.source ? " selected" : ""}>${esc(name)}</option>`).join("");
}

function resStateTag(r) {
  const cls = { Ok: "on", Warning: "warn", Alarm: "off", Offline: "off", Disabled: "operator", Unknown: "operator" }[r.health] || "operator";
  return `<span class="tag ${cls}${r.health === "Alarm" ? " res-alarm" : ""}">${esc(r.state)}</span>`;
}

/** Dónde está el recurso, relativo a la ubicación seleccionada (vacío si está justo ahí). */
function resWhere(r) {
  if (r.locationId == null) return `<span class="muted">Por ubicar</span>`;
  const path = resPath(r.locationId);
  if (typeof resState.selected === "number") {
    const base = resPath(resState.selected).length;
    const rest = path.slice(base);
    return rest.length ? esc(rest.join(" › ")) : `<span class="muted">—</span>`;
  }
  return esc(path.join(" › "));
}

function resDrawList() {
  const isAdmin = Api.role === "Admin";
  const inSelection = resInSelection();

  // Chips por tipo, con cuántos hay en la selección (antes de los demás filtros).
  const byKind = new Map();
  for (const r of inSelection) byKind.set(r.kind, (byKind.get(r.kind) || 0) + 1);
  if (resState.kind && !byKind.has(resState.kind)) resState.kind = "";
  $("#res-chips").innerHTML =
    `<button type="button" class="res-chip${resState.kind ? "" : " on"}" data-kind="">Todos ${inSelection.length}</button>` +
    RES_KIND_ORDER.filter((k) => byKind.has(k)).map((k) =>
      `<button type="button" class="res-chip${resState.kind === k ? " on" : ""}" data-kind="${k}">${resIcon(RES_KINDS[k].icon, 13)} ${RES_KINDS[k].plural} ${byKind.get(k)}</button>`).join("");

  const list = resFiltered(inSelection);
  const showWhere = resState.selected !== "pending";
  const allChecked = list.length > 0 && list.every((r) => resState.checked.has(resKey(r)));

  if (resState.resources.length === 0) {
    $("#res-table").innerHTML = `<div class="info-box">Todavía no hay recursos: aparecen solos al agregar equipos en <b>Dispositivos</b> (canales de video, puertas, áreas y zonas de alarma, cercos, parlantes y citófonos).</div>`;
  } else if (inSelection.length === 0) {
    $("#res-table").innerHTML = resState.selected === "pending"
      ? `<div class="info-box">Todo está ubicado.</div>`
      : `<div class="info-box">No hay recursos en esta ubicación.${isAdmin ? " Márquelos en <b>Por ubicar</b> (o en otra ubicación) y use <b>Mover a…</b>, o arrástrelos hasta aquí en el árbol." : ""}</div>`;
  } else if (list.length === 0) {
    $("#res-table").innerHTML = `<div class="info-box">Ningún recurso coincide con los filtros.</div>`;
  } else {
    $("#res-table").innerHTML = `
      <div class="table-scroll"><table class="grid res-grid">
        <thead><tr>
          ${isAdmin ? `<th class="res-check"><input type="checkbox" id="res-check-all" title="Marcar todos los de la lista"${allChecked ? " checked" : ""}></th>` : ""}
          <th>Recurso</th><th>Equipo físico</th>${showWhere ? "<th>Ubicación</th>" : ""}<th>Estado</th>
        </tr></thead>
        <tbody>
          ${list.map((r) => `
            <tr class="res-row" data-key="${resKey(r)}"${isAdmin ? ' draggable="true"' : ""}>
              ${isAdmin ? `<td class="res-check"><input type="checkbox" class="res-check-one"${resState.checked.has(resKey(r)) ? " checked" : ""}></td>` : ""}
              <td>
                <div class="res-cell-name">${resIcon(RES_KINDS[r.kind]?.icon || "point")}
                  <a class="res-open" href="#/resources?r=${r.kind}:${r.id}" title="Abrir la ficha" draggable="false">${esc(r.name)}</a></div>
                <div class="res-sub">${esc(RES_KINDS[r.kind]?.label || r.kind)}${r.detail ? " · " + esc(r.detail) : ""}</div>
              </td>
              <td><a class="res-source" href="#/${esc(r.sourceModule)}" title="Ir a la página del equipo">${esc(r.source)} ${resIcon("link", 12)}</a></td>
              ${showWhere ? `<td>${resWhere(r)}</td>` : ""}
              <td>${resStateTag(r)}</td>
            </tr>`).join("")}
        </tbody>
      </table></div>`;
  }
  resDrawBulk(list);
}

function resDrawBulk(list) {
  const isAdmin = Api.role === "Admin";
  const checked = resState.resources.filter((r) => resState.checked.has(resKey(r)));
  if (!isAdmin || checked.length === 0) { $("#res-bulk").innerHTML = ""; return; }
  const hidden = checked.length - list.filter((r) => resState.checked.has(resKey(r))).length;
  $("#res-bulk").innerHTML = `
    <div class="res-bulk">
      <span class="grow"><b>${checked.length}</b> marcado(s)${hidden > 0 ? ` <span class="muted">(${hidden} fuera de la vista actual)</span>` : ""}</span>
      <button class="btn small" id="res-move">Mover a…</button>
      ${checked.some((r) => r.locationId != null) ? `<button class="btn ghost small" id="res-unlocate">Quitar ubicación</button>` : ""}
      <button class="btn ghost small" id="res-clear">Desmarcar</button>
    </div>`;
  $("#res-move").addEventListener("click", () => resMoveModal(checked));
  $("#res-unlocate")?.addEventListener("click", () => resAssign(null, checked));
  $("#res-clear").addEventListener("click", () => { resState.checked.clear(); resDrawList(); });
}

// ---------------------------------------------------------------------------
// Interacción: árbol (selección, plegado, arrastrar y soltar) y lista
// ---------------------------------------------------------------------------

const RES_DRAG_RESOURCES = "application/x-tcvms-resources";
const RES_DRAG_LOCATION = "application/x-tcvms-location";

function resSelect(value) {
  resState.selected = value;
  resState.kind = "";
  resState.source = "";
  resDraw();
}

function resBindTree(isAdmin) {
  const tree = $("#res-tree");
  tree.addEventListener("click", (e) => {
    const toggle = e.target.closest(".res-caret-btn");
    if (toggle && toggle.dataset.toggle) {
      const id = Number(toggle.dataset.toggle);
      resState.collapsed.has(id) ? resState.collapsed.delete(id) : resState.collapsed.add(id);
      try { localStorage.setItem("tcvms.res.collapsed", JSON.stringify([...resState.collapsed])); } catch { /* sin almacenamiento */ }
      resDrawTree();
      return;
    }
    const node = e.target.closest(".res-node");
    if (!node) return;
    const value = node.dataset.loc ? Number(node.dataset.loc) : node.dataset.special;
    if (resState.sheet) {
      // Con una ficha abierta, elegir una ubicación vuelve a la lista de esa ubicación.
      resState.selected = value;
      resState.kind = "";
      resState.source = "";
      location.hash = "#/resources";
      return;
    }
    resSelect(value);
  });
  if (!isAdmin) return;

  tree.addEventListener("dragstart", (e) => {
    const node = e.target.closest(".res-node[data-loc]");
    if (!node) return;
    e.dataTransfer.setData(RES_DRAG_LOCATION, node.dataset.loc);
    e.dataTransfer.effectAllowed = "move";
    resState.dragging = true;
  });
  tree.addEventListener("dragend", () => { resState.dragging = false; });
  const target = (e) => e.target.closest(".res-node");
  const accepts = (e, node) => {
    const types = [...e.dataTransfer.types];
    if (types.includes(RES_DRAG_RESOURCES)) return Boolean(node.dataset.loc) || node.dataset.special === "pending";
    if (types.includes(RES_DRAG_LOCATION)) return Boolean(node.dataset.loc) || node.dataset.special === "all";
    return false;
  };
  tree.addEventListener("dragover", (e) => {
    const node = target(e);
    if (!node || !accepts(e, node)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    $$(".res-node.drop", tree).forEach((n) => n !== node && n.classList.remove("drop"));
    node.classList.add("drop");
  });
  tree.addEventListener("dragleave", (e) => {
    const node = target(e);
    if (node && !node.contains(e.relatedTarget)) node.classList.remove("drop");
  });
  tree.addEventListener("drop", async (e) => {
    const node = target(e);
    $$(".res-node.drop", tree).forEach((n) => n.classList.remove("drop"));
    if (!node || !accepts(e, node)) return;
    e.preventDefault();
    resState.dragging = false;
    const destination = node.dataset.loc ? Number(node.dataset.loc) : null;
    const resources = e.dataTransfer.getData(RES_DRAG_RESOURCES);
    if (resources) {
      const keys = new Set(JSON.parse(resources));
      await resAssign(destination, resState.resources.filter((r) => keys.has(resKey(r))));
      return;
    }
    const moved = Number(e.dataTransfer.getData(RES_DRAG_LOCATION));
    if (moved && moved !== destination) await resMoveLocation(resState.byId.get(moved), destination);
  });
}

function resBindList(isAdmin) {
  $("#res-chips").addEventListener("click", (e) => {
    const chip = e.target.closest(".res-chip");
    if (!chip) return;
    resState.kind = chip.dataset.kind;
    resDrawList();
  });
  if (!isAdmin) return;
  const table = $("#res-table");
  table.addEventListener("change", (e) => {
    if (e.target.id === "res-check-all") {
      for (const r of resFiltered(resInSelection()))
        e.target.checked ? resState.checked.add(resKey(r)) : resState.checked.delete(resKey(r));
      resDrawList();
    } else if (e.target.classList.contains("res-check-one")) {
      const key = e.target.closest("tr").dataset.key;
      e.target.checked ? resState.checked.add(key) : resState.checked.delete(key);
      resDrawBulk(resFiltered(resInSelection()));
      const all = $("#res-check-all");
      if (all) all.checked = resFiltered(resInSelection()).every((r) => resState.checked.has(resKey(r)));
    }
  });
  // Arrastrar una fila marcada lleva todas las marcadas; una sin marcar, solo esa.
  table.addEventListener("dragstart", (e) => {
    const row = e.target.closest("tr.res-row");
    if (!row) return;
    const key = row.dataset.key;
    const keys = resState.checked.has(key) ? [...resState.checked] : [key];
    e.dataTransfer.setData(RES_DRAG_RESOURCES, JSON.stringify(keys));
    e.dataTransfer.effectAllowed = "move";
    resState.dragging = true;
    const ghost = document.createElement("div");
    ghost.className = "res-drag-ghost";
    ghost.textContent = keys.length === 1 ? (resState.resources.find((r) => resKey(r) === key)?.name ?? "1 recurso") : `${keys.length} recursos`;
    document.body.appendChild(ghost);
    e.dataTransfer.setDragImage(ghost, 12, 12);
    setTimeout(() => ghost.remove(), 0);
  });
  table.addEventListener("dragend", () => { resState.dragging = false; });
}

// ---------------------------------------------------------------------------
// Acciones (todas pasan por la API: quedan en la bitácora)
// ---------------------------------------------------------------------------

async function resAssign(locationId, resources) {
  if (!resources.length) return;
  try {
    const result = await Api.put("/api/resources/location", {
      locationId,
      resources: resources.map((r) => ({ kind: r.kind, id: r.id })),
    });
    toast(result.message);
    for (const r of resources) resState.checked.delete(resKey(r));
    await resLoad();
  } catch (err) {
    toast(err.error, true);
  }
}

function resLocationPayload(loc, overrides) {
  return {
    name: loc.name, kind: loc.kind, parentId: loc.parentId ?? null,
    description: loc.description ?? null, address: loc.address ?? null,
    latitude: loc.latitude ?? null, longitude: loc.longitude ?? null,
    ...overrides,
  };
}

async function resMoveLocation(loc, parentId) {
  if (!loc || (loc.parentId ?? null) === parentId) return;
  try {
    await Api.put(`/api/locations/${loc.id}`, resLocationPayload(loc, { parentId }));
    if (parentId != null) resState.collapsed.delete(parentId); // que se vea dónde quedó
    toast(`"${loc.name}" quedó en ${parentId == null ? "la raíz" : resPath(parentId).join(" › ")}.`);
    await resLoad();
  } catch (err) {
    toast(err.error, true);
  }
}

async function resDeleteLocation(loc) {
  if (!loc) return;
  const kids = resState.children.get(loc.id) || [];
  if (kids.length) {
    toast(`"${loc.name}" tiene ${kids.length} sububicación(es): muévalas o elimínelas antes.`, true);
    return;
  }
  const inside = resState.resources.filter((r) => r.locationId === loc.id).length;
  if (!confirm(`¿Eliminar la ubicación "${loc.name}"?` +
    (inside ? `\n\nSus ${inside} recurso(s) quedarán por ubicar (no se borra ningún equipo).` : ""))) return;
  try {
    const result = await Api.delete(`/api/locations/${loc.id}`);
    toast(result.message);
    resState.selected = loc.parentId ?? "all";
    await resLoad();
  } catch (err) {
    toast(err.error, true);
  }
}

/** Opciones de ubicación con sangría por nivel (excluye un subárbol, para no mover dentro de sí misma). */
function resLocationOptions(selectedId, excluded) {
  const out = [];
  const walk = (key, depth) => {
    for (const l of resState.children.get(key) || []) {
      if (excluded?.has(l.id)) continue;
      out.push(`<option value="${l.id}"${l.id === selectedId ? " selected" : ""}>${"   ".repeat(depth)}${esc(l.name)}</option>`);
      walk(l.id, depth + 1);
    }
  };
  walk("root", 0);
  return out.join("");
}

function resLocationModal(existing, parentId) {
  const editing = Boolean(existing);
  const parent = editing ? existing.parentId : parentId;
  const parentLoc = parent != null ? resState.byId.get(parent) : null;
  const kind = editing ? existing.kind : LOC_NEXT_KIND[parentLoc?.kind ?? "root"];
  const excluded = editing ? resSubtree(existing.id) : null;
  openModal(`
    <h3>${editing ? "Editar ubicación" : "Nueva ubicación"}</h3>
    <div id="loc-error"></div>
    <div class="form-grid">
      <div class="field"><label for="loc-name">Nombre</label>
        <input id="loc-name" maxlength="128" value="${esc(existing?.name ?? "")}" placeholder="Acceso principal"></div>
      <div class="field"><label for="loc-kind">Tipo</label>
        <select id="loc-kind">${Object.entries(LOC_KINDS).map(([k, v]) =>
          `<option value="${k}"${k === kind ? " selected" : ""}>${v.label}</option>`).join("")}</select></div>
    </div>
    <div class="field"><label for="loc-parent">Dentro de</label>
      <select id="loc-parent"><option value="">— En la raíz del árbol —</option>${resLocationOptions(parent ?? null, excluded)}</select></div>
    <div class="field"><label for="loc-desc">Descripción <span class="muted">(opcional)</span></label>
      <input id="loc-desc" maxlength="500" value="${esc(existing?.description ?? "")}" placeholder="Entrada de vehículos y peatones"></div>
    <div class="field"><label for="loc-address">Dirección <span class="muted">(opcional)</span></label>
      <input id="loc-address" maxlength="255" value="${esc(existing?.address ?? "")}" placeholder="Calle, número, comuna"></div>
    <div class="form-grid">
      <div class="field"><label for="loc-lat">Latitud <span class="muted">(opcional)</span></label>
        <input id="loc-lat" inputmode="decimal" value="${existing?.latitude ?? ""}" placeholder="-33.4489"></div>
      <div class="field"><label for="loc-lon">Longitud <span class="muted">(opcional)</span></label>
        <input id="loc-lon" inputmode="decimal" value="${existing?.longitude ?? ""}" placeholder="-70.6693"></div>
    </div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="loc-cancel">Cancelar</button>
      <button class="btn" type="button" id="loc-save">${editing ? "Guardar" : "Crear"}</button>
    </div>`, true);
  $("#loc-name").focus();
  $("#loc-cancel").addEventListener("click", closeModal);
  $("#loc-name").addEventListener("keydown", (e) => { if (e.key === "Enter") $("#loc-save").click(); });
  $("#loc-save").addEventListener("click", async (e) => {
    const number = (id) => {
      const raw = $(id).value.trim().replace(",", ".");
      return raw === "" ? null : Number(raw);
    };
    const lat = number("#loc-lat"), lon = number("#loc-lon");
    if (Number.isNaN(lat) || Number.isNaN(lon)) {
      $("#loc-error").innerHTML = `<div class="error-box">Latitud y longitud van como número decimal (ej. -33.4489).</div>`;
      return;
    }
    const body = {
      name: $("#loc-name").value.trim(),
      kind: $("#loc-kind").value,
      parentId: $("#loc-parent").value ? Number($("#loc-parent").value) : null,
      description: $("#loc-desc").value.trim() || null,
      address: $("#loc-address").value.trim() || null,
      latitude: lat, longitude: lon,
    };
    e.target.disabled = true;
    try {
      const saved = editing
        ? await Api.put(`/api/locations/${existing.id}`, body)
        : await Api.post("/api/locations", body);
      closeModal();
      toast(editing ? "Ubicación actualizada." : `Ubicación "${saved.name}" creada.`);
      if (body.parentId != null) resState.collapsed.delete(body.parentId);
      if (!editing) resState.selected = saved.id;
      await resLoad();
    } catch (err) {
      e.target.disabled = false;
      $("#loc-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

function resMoveModal(resources) {
  if (!resState.locations.length) {
    toast("Primero cree una ubicación (botón «+ Nueva» del árbol).", true);
    return;
  }
  const options = [];
  const walk = (key, depth) => {
    for (const l of resState.children.get(key) || []) {
      const kind = LOC_KINDS[l.kind] || LOC_KINDS.Sector;
      options.push(`
        <label style="padding-left:${6 + depth * 18}px">
          <input type="radio" name="res-target" value="${l.id}"${l.id === resState.selected ? " checked" : ""}>
          ${resIcon(kind.icon)} ${esc(l.name)} <span class="muted" style="font-size:11.5px">${esc(kind.label)}</span>
        </label>`);
      walk(l.id, depth + 1);
    }
  };
  walk("root", 0);
  openModal(`
    <h3>Mover ${resources.length} recurso(s)</h3>
    <p class="muted" style="margin-top:-8px">Elija la ubicación de destino. Cada recurso queda en una sola ubicación.</p>
    <div class="res-loc-pick">${options.join("")}</div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="res-move-cancel">Cancelar</button>
      <button class="btn" type="button" id="res-move-ok">Mover</button>
    </div>`, true);
  $("#res-move-cancel").addEventListener("click", closeModal);
  $("#res-move-ok").addEventListener("click", async () => {
    const picked = document.querySelector('input[name="res-target"]:checked');
    if (!picked) { toast("Elija una ubicación.", true); return; }
    closeModal();
    await resAssign(Number(picked.value), resources);
  });
}

// ---------------------------------------------------------------------------
// Ficha del recurso: General, Equipo, Cámaras asociadas, Automatizaciones e
// Historial. Se abre desde el nombre de un recurso en la lista y tiene su
// propia dirección (#/resources?r=Door:12), así se puede enlazar.
// ---------------------------------------------------------------------------

const RES_TABS = [
  { key: "general", label: "General" },
  { key: "device", label: "Equipo" },
  { key: "cameras", label: "Cámaras asociadas" },
  { key: "workflows", label: "Automatizaciones" },
  { key: "history", label: "Historial" },
];

const resSheetUrl = (sheet) => `/api/resources/${sheet.kind.toLowerCase()}/${sheet.id}`;
const resSnapshotSrc = (path) => `${path}?access_token=${encodeURIComponent(Api.token)}&t=${Date.now()}`;

async function resOpenSheet() {
  const sheet = resState.sheet;
  try {
    sheet.detail = await Api.get(resSheetUrl(sheet));
  } catch (err) {
    $("#res-sheet").innerHTML = `
      <a class="btn ghost small" href="#/resources">← Volver a la lista</a>
      <div class="error-box" style="margin-top:12px">${esc(err.status === 404
        ? "Ese recurso ya no existe: se eliminó su equipo o una revalidación ya no lo trae."
        : err.error)}</div>`;
    return;
  }
  if (!sheet.detail.supportsCameras && sheet.tab === "cameras") sheet.tab = "general";
  resDrawSheet();
}

/** Refresco de fondo con la ficha abierta: solo el estado y el nombre del encabezado. */
function resSheetRefreshState() {
  const sheet = resState.sheet;
  if (!sheet?.detail || !$("#rs-state")) return;
  const fresh = resState.resources.find((r) => r.kind === sheet.kind && r.id === sheet.id);
  if (!fresh) return;
  sheet.detail.resource = { ...sheet.detail.resource, health: fresh.health, state: fresh.state };
  $("#rs-state").innerHTML = resStateTag(sheet.detail.resource);
}

function resDrawSheet() {
  const { detail, tab } = resState.sheet;
  const r = detail.resource;
  const kind = RES_KINDS[r.kind];
  const tabs = RES_TABS.filter((t) => t.key !== "cameras" || detail.supportsCameras);
  $("#page-title").textContent = `Recursos · ${r.name}`;
  $("#res-sheet").innerHTML = `
    <div class="res-sheet-back"><a class="btn ghost small" href="#/resources">← Volver a la lista</a></div>
    <div class="res-sheet-head">
      <div class="res-sheet-icon">${resIcon(kind?.icon || "point", 22)}</div>
      <div class="res-sheet-title">
        <h3>${esc(r.name)}</h3>
        <div class="muted">${esc(kind?.label || r.kind)}${r.detail ? " · " + esc(r.detail) : ""} ·
          ${detail.locationPath ? esc(detail.locationPath) : "Por ubicar"}</div>
      </div>
      <div id="rs-state">${resStateTag(r)}</div>
    </div>
    <div class="res-tabs" role="tablist">
      ${tabs.map((t) => `
        <button type="button" role="tab" class="res-tab${t.key === tab ? " on" : ""}" data-tab="${t.key}"
                aria-selected="${t.key === tab}">${t.label}${t.key === "cameras"
                  ? ` <span class="res-count">${detail.cameras.length}</span>` : ""}</button>`).join("")}
    </div>
    <div id="rs-body" class="res-tab-body"></div>`;
  $$("#res-sheet .res-tab").forEach((b) => b.addEventListener("click", () => resSheetTab(b.dataset.tab)));
  resDrawSheetTab();
}

function resSheetTab(tab) {
  const sheet = resState.sheet;
  sheet.tab = tab;
  // La pestaña queda en la dirección (para enlazarla) sin volver a dibujar la página.
  history.replaceState(null, "", `#/resources?r=${sheet.kind}:${sheet.id}${tab === "general" ? "" : `&tab=${tab}`}`);
  $$("#res-sheet .res-tab").forEach((b) => {
    b.classList.toggle("on", b.dataset.tab === tab);
    b.setAttribute("aria-selected", String(b.dataset.tab === tab));
  });
  resDrawSheetTab();
}

async function resDrawSheetTab() {
  const sheet = resState.sheet;
  const body = $("#rs-body");
  const load = async (what, url) => {
    body.innerHTML = `<div class="muted">Cargando…</div>`;
    try { sheet[what] = await Api.get(url); }
    catch (err) { body.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return false; }
    // Si el usuario cambió de pestaña o de ficha mientras cargaba, no se pisa lo nuevo.
    return resState.sheet === sheet && sheet.tab === what;
  };
  switch (sheet.tab) {
    case "device":
      body.innerHTML = resSheetDeviceHtml(sheet.detail);
      resBindSheetDevice();
      break;
    case "cameras":
      body.innerHTML = resSheetCamerasHtml(sheet.detail);
      resBindSheetCameras();
      break;
    case "workflows":
      if (await load("workflows", resSheetUrl(sheet) + "/workflows")) body.innerHTML = resSheetWorkflowsHtml(sheet.workflows);
      break;
    case "history":
      if (await load("history", resSheetUrl(sheet) + "/history?take=80")) body.innerHTML = resSheetHistoryHtml(sheet.history);
      break;
    default:
      body.innerHTML = resSheetGeneralHtml(sheet.detail);
      resBindSheetGeneral();
  }
}

// ---------- General ----------

function resSheetGeneralHtml(d) {
  const r = d.resource;
  if (Api.role !== "Admin") {
    return `
      <dl class="res-dl wide">
        <dt>Ubicación</dt><dd>${d.locationPath ? esc(d.locationPath) : "Por ubicar"}</dd>
        <dt>Descripción</dt><dd>${d.description ? esc(d.description) : `<span class="muted">—</span>`}</dd>
        <dt>Consignas para el operador</dt>
        <dd class="res-pre">${d.instructions ? esc(d.instructions) : `<span class="muted">Sin consignas.</span>`}</dd>
      </dl>`;
  }
  return `
    <div id="rs-error"></div>
    <div class="form-grid">
      <div class="field"><label for="rs-name">Nombre</label>
        <input id="rs-name" maxlength="128" value="${esc(r.name)}"${d.canRename ? "" : " disabled"}>
        ${d.canRename ? "" : `<div class="muted res-field-note">${esc(d.renameNote || "")}</div>`}</div>
      <div class="field"><label for="rs-location">Ubicación</label>
        <select id="rs-location"><option value="">— Por ubicar —</option>${resLocationOptions(r.locationId ?? null, null)}</select></div>
    </div>
    <div class="field"><label for="rs-desc">Descripción <span class="muted">(qué es y dónde está exactamente)</span></label>
      <textarea id="rs-desc" rows="2" maxlength="1000"
        placeholder="Puerta de vidrio doble, lado estacionamiento">${esc(d.description ?? "")}</textarea></div>
    <div class="field"><label for="rs-instr">Consignas para el operador <span class="muted">(qué hacer cuando este recurso avisa algo)</span></label>
      <textarea id="rs-instr" rows="6" maxlength="4000"
        placeholder="Si queda abierta más de 30 s: revisar la cámara exterior, llamar al supervisor y anotarlo en el libro del turno.">${esc(d.instructions ?? "")}</textarea></div>
    <div class="res-sheet-actions"><button class="btn" type="button" id="rs-save">Guardar</button></div>`;
}

function resBindSheetGeneral() {
  const save = $("#rs-save");
  if (!save) return;
  save.addEventListener("click", async () => {
    const sheet = resState.sheet;
    const body = {
      name: sheet.detail.canRename ? $("#rs-name").value.trim() : null,
      locationId: $("#rs-location").value ? Number($("#rs-location").value) : null,
      description: $("#rs-desc").value.trim() || null,
      instructions: $("#rs-instr").value.trim() || null,
    };
    save.disabled = true;
    try {
      sheet.detail = await Api.put(resSheetUrl(sheet), body);
      toast("Ficha guardada.");
      resDrawSheet();
    } catch (err) {
      save.disabled = false;
      $("#rs-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

// ---------- Equipo ----------

function resSheetDeviceHtml(d) {
  const s = d.source;
  const row = (label, value) => value ? `<dt>${esc(label)}</dt><dd>${esc(value)}</dd>` : "";
  return `
    <div class="res-cards">
      <section class="res-card">
        <h4>Equipo físico</h4>
        <dl class="res-dl">
          ${row("Nombre", s.name)}${row("Modelo", s.model)}${row("N° de serie", s.serialNumber)}
          ${row("Firmware", s.firmware)}${row("Dirección", s.address)}${row("Conexión", s.status)}
          ${s.lastSeenAt ? row("Visto por última vez", formatDateTime(s.lastSeenAt)) : ""}
        </dl>
        <a class="btn ghost small" href="#/${esc(s.module)}">Ir al equipo ${resIcon("link", 12)}</a>
      </section>
      <section class="res-card">
        <h4>Características</h4>
        <dl class="res-dl">${d.facts.map((f) => row(f.label, f.value)).join("")}</dl>
      </section>
      ${d.snapshotPath ? `
      <section class="res-card res-card-wide">
        <h4>Imagen actual</h4>
        <img id="rs-snapshot" class="res-snapshot" alt="Imagen actual de la cámara" src="${resSnapshotSrc(d.snapshotPath)}">
        <div class="muted hidden" id="rs-snapshot-error">El equipo no entregó una imagen (sin conexión o sin señal).</div>
        <button class="btn ghost small" type="button" id="rs-snapshot-refresh">Actualizar imagen</button>
      </section>` : ""}
    </div>`;
}

function resBindSheetDevice() {
  const img = $("#rs-snapshot");
  if (!img) return;
  // El equipo puede tardar en contestar: si el usuario ya cambió de pestaña,
  // la imagen y su aviso ya no están en la página.
  img.addEventListener("error", () => {
    if (!img.isConnected) return;
    img.classList.add("hidden");
    $("#rs-snapshot-error")?.classList.remove("hidden");
  });
  img.addEventListener("load", () => {
    if (!img.isConnected) return;
    img.classList.remove("hidden");
    $("#rs-snapshot-error")?.classList.add("hidden");
  });
  $("#rs-snapshot-refresh").addEventListener("click", () => { img.src = resSnapshotSrc(resState.sheet.detail.snapshotPath); });
}

// ---------- Cámaras asociadas ----------

function resSheetCamerasHtml(d) {
  const isAdmin = Api.role === "Admin";
  const editable = d.cameras.filter((c) => !c.fixed);
  const cards = d.cameras.map((c, i) => {
    const position = editable.indexOf(c);
    return `
      <div class="res-cam" data-channel="${c.channelId}">
        <div class="res-cam-thumb">
          ${resIcon("camera", 22, "res-cam-placeholder")}
          <img loading="lazy" alt="" onerror="this.remove()"
               src="/api/devices/${c.deviceId}/snapshot/${c.channelNumber}?access_token=${encodeURIComponent(Api.token)}">
        </div>
        <div class="res-cam-info">
          <div class="res-cam-name">${esc(c.name)}</div>
          <div class="res-sub res-sub-flat">${esc(c.deviceName)} · canal ${c.channelNumber}</div>
          <div class="res-cam-tags">
            ${i === 0 ? `<span class="tag admin">Principal</span>` : ""}
            ${c.fixed ? `<span class="tag operator" title="Se cambia en Dispositivos → Citofonía">Cámara del frente</span>` : ""}
            ${c.isOnline ? "" : `<span class="tag off">Sin señal</span>`}
          </div>
        </div>
        ${isAdmin && !c.fixed ? `
        <div class="res-cam-actions">
          <button class="btn ghost small" type="button" data-act="up" title="Subir"${position === 0 ? " disabled" : ""}>↑</button>
          <button class="btn ghost small" type="button" data-act="down" title="Bajar"${position === editable.length - 1 ? " disabled" : ""}>↓</button>
          <button class="btn danger small" type="button" data-act="remove">Quitar</button>
        </div>` : ""}
      </div>`;
  }).join("");
  return `
    <p class="muted res-tab-intro">Las cámaras que muestran lo que pasa en este recurso. La primera es la principal.</p>
    ${d.cameras.length ? `<div class="res-cams">${cards}</div>` : `<div class="info-box">Todavía no tiene cámaras asociadas.</div>`}
    ${isAdmin ? `
    <div class="res-cam-add">
      <input type="search" id="rs-cam-search" placeholder="Buscar una cámara para asociar (nombre o equipo)…" autocomplete="off">
      <div id="rs-cam-results" class="res-cam-results"></div>
    </div>` : ""}`;
}

function resBindSheetCameras() {
  const sheet = resState.sheet;
  const editableIds = () => sheet.detail.cameras.filter((c) => !c.fixed).map((c) => c.channelId);
  $$("#rs-body .res-cam-actions .btn").forEach((b) => b.addEventListener("click", async () => {
    const id = Number(b.closest(".res-cam").dataset.channel);
    const ids = editableIds();
    const i = ids.indexOf(id);
    if (b.dataset.act === "remove") ids.splice(i, 1);
    else {
      const j = b.dataset.act === "up" ? i - 1 : i + 1;
      if (j < 0 || j >= ids.length) return;
      [ids[i], ids[j]] = [ids[j], ids[i]];
    }
    await resSaveCameras(ids);
  }));

  const search = $("#rs-cam-search");
  if (!search) return;
  const draw = () => {
    const needle = resFold(search.value.trim());
    const taken = new Set(sheet.detail.cameras.map((c) => c.channelId));
    const options = resState.resources
      .filter((r) => r.kind === "Camera" && r.enabled && !taken.has(r.id))
      .filter((r) => !needle || resFold(`${r.name} ${r.source}`).includes(needle))
      .sort((a, b) => resCollator.compare(a.name, b.name))
      .slice(0, 12);
    $("#rs-cam-results").innerHTML = options.length
      ? options.map((r) => `
          <button type="button" class="res-cam-option" data-id="${r.id}">
            ${resIcon("camera", 14)} <span>${esc(r.name)}</span>
            <span class="muted">${esc(r.source)} · ${esc(r.detail ?? "")}</span>
            <span class="res-cam-add-label">+ Asociar</span>
          </button>`).join("")
      : `<div class="muted res-cam-empty">No hay cámaras que coincidan.</div>`;
  };
  search.addEventListener("input", draw);
  search.addEventListener("focus", draw);
  $("#rs-cam-results").addEventListener("click", async (e) => {
    const option = e.target.closest(".res-cam-option");
    if (!option) return;
    await resSaveCameras([...editableIds(), Number(option.dataset.id)]);
    $("#rs-cam-search")?.focus(); // para seguir agregando
  });
}

async function resSaveCameras(ids) {
  const sheet = resState.sheet;
  try {
    sheet.detail = await Api.put(resSheetUrl(sheet) + "/cameras", { channelIds: ids });
    resDrawSheet();
  } catch (err) {
    toast(err.error, true);
  }
}

// ---------- Automatizaciones ----------

function resSheetWorkflowsHtml(w) {
  const isAdmin = Api.role === "Admin";
  const role = { trigger: "La dispara", condition: "La consulta", action: "Actúa sobre él" };
  const general = w.generalCount > 0
    ? `<p class="muted res-tab-intro">Además, ${w.generalCount} automatización(es) ${esc(w.generalNote)}: también lo alcanzan aunque no lo nombren.</p>`
    : "";
  if (!w.uses.length) {
    return `<div class="info-box">Ninguna automatización nombra a este recurso.${isAdmin
      ? ` <a href="#/workflows/edit">Crear una automatización</a>.` : ""}</div>${general}`;
  }
  return `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Automatización</th><th>Cómo lo usa</th><th>Estado</th></tr></thead>
      <tbody>${w.uses.map((u) => `
        <tr>
          <td>${isAdmin ? `<a href="#/workflows/edit?id=${u.workflowId}">${esc(u.name)}</a>` : esc(u.name)}</td>
          <td><span class="tag operator">${role[u.role] || esc(u.role)}</span> ${esc(u.detail)}</td>
          <td>${u.enabled ? `<span class="tag on">Activa</span>` : `<span class="tag operator">Pausada</span>`}</td>
        </tr>`).join("")}
      </tbody>
    </table></div>
    ${general}`;
}

// ---------- Historial ----------

// Origen de cada entrada: [clase de la etiqueta, texto]. "alert" = aviso de una
// automatización originado por el recurso (con quién lo atendió).
const RES_HIST_SOURCES = { event: ["admin", "Evento"], alert: ["warn", "Aviso"], audit: ["operator", "Bitácora"] };

function resSheetHistoryHtml(items) {
  if (!items.length) return `<div class="info-box">Todavía no hay nada registrado para este recurso.</div>`;
  return `
    <div class="res-hist">${items.map((h) => `
      <div class="res-hist-item ${esc(h.level)}">
        <div class="res-hist-time">${esc(formatDateTime(h.timestamp))}</div>
        <div class="res-hist-main">
          <div><span class="tag ${RES_HIST_SOURCES[h.source]?.[0] ?? "operator"}">${RES_HIST_SOURCES[h.source]?.[1] ?? "Bitácora"}</span>
            ${esc(h.title)}</div>
          ${h.detail ? `<div class="res-sub res-sub-flat">${esc(h.detail)}</div>` : ""}
        </div>
        <div class="res-hist-user muted">${esc(h.user ?? "")}</div>
      </div>`).join("")}
    </div>
    <p class="muted res-tab-intro">Las ${items.length} entradas más recientes.${Api.role === "Admin"
      ? ` La bitácora completa está en <a href="#/audit">Seguridad → Auditoría</a>.` : ""}</p>`;
}
