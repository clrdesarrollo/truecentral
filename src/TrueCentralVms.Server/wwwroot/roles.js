// CLR TrueCentral VMS — Seguridad → Roles.
// Un rol dice QUÉ se puede hacer (permisos) y DÓNDE (alcance): en todo el
// sistema, o solo en ciertas ubicaciones (cada una con sus sububicaciones) y en
// recursos sueltos —una cámara, una puerta, un área… o un equipo completo—, de
// cualquier ubicación. Un usuario puede tener varios roles y suma permisos y
// alcances; además puede tener un límite propio por ubicación (Usuarios).
// Nadie otorga lo que no tiene: los permisos y recursos que esta sesión no
// posee no se ofrecen, y el servidor lo valida igual.
"use strict";

const rolesState = {
  catalog: null,      // { groups, permissions, templates, grantable }
  roles: [],
  locations: [],      // árbol de ubicaciones (lista plana)
  scopeCatalog: null, // recursos elegibles para el alcance (se pide al abrir un rol limitado)
  selected: null,     // id del rol abierto, "new" o null
  draft: null,        // { name, description, permissions:Set, restrict, viewOutside, locations:Set, items:Set("Kind:Id") }
  saved: "",          // huella del borrador guardado (para avisar cambios sin guardar)
  filter: "",
  scopeQuery: "",     // buscador de recursos sueltos
  scopeKind: "",      // filtro por tipo de recurso
  hubBound: null,
};

/** Tipos del alcance (mismo orden y nombres que ScopeKinds del servidor). */
const ROLE_SCOPE_KINDS = [
  ["VideoDevice", "Equipo de video"], ["Camera", "Cámara"],
  ["AccessDevice", "Equipo de acceso"], ["Door", "Puerta"],
  ["AlarmPanel", "Panel de alarma"], ["Partition", "Área de alarma"], ["Zone", "Zona"],
  ["Fence", "Cerco"], ["Speaker", "Parlante"], ["Intercom", "Citófono"],
];
const roleKindLabel = (k) => ROLE_SCOPE_KINDS.find(([key]) => key === k)?.[1] ?? k;
const ROLE_SCOPE_MAX_RESULTS = 200;

function rolesDetachHub() {
  if (rolesState.hubBound) VmsHub.off("ConfigChanged", rolesState.hubBound);
  rolesState.hubBound = null;
}

const rolePermByKey = () => new Map(rolesState.catalog.permissions.map((p) => [p.key, p]));
const roleDraftPrint = (d) => d ? JSON.stringify([d.name.trim(), d.description.trim(), [...d.permissions].sort(),
  d.restrict, d.restrict && d.viewOutside, d.restrict ? [...d.locations].sort((a, b) => a - b) : [],
  d.restrict ? [...d.items].sort() : []]) : "";
const roleIsDirty = () => rolesState.draft && roleDraftPrint(rolesState.draft) !== rolesState.saved;

function roleSelected() {
  return typeof rolesState.selected === "number" ? rolesState.roles.find((r) => r.id === rolesState.selected) : null;
}

/** Borrador de un rol (o vacío, o desde una plantilla/copia). */
function roleDraftOf(src) {
  return {
    name: src?.name ?? "",
    description: src?.description ?? "",
    permissions: new Set(src?.permissions ?? []),
    restrict: !!(src?.restrictScope ?? src?.restrict),
    viewOutside: !!(src?.viewOutsideScope ?? src?.viewOutside),
    locations: new Set(src?.locationIds ?? src?.locations ?? []),
    items: new Set((src?.items ?? []).map((i) => (typeof i === "string" ? i : `${i.kind}:${i.id}`))),
    names: new Map((src?.items ?? []).filter((i) => typeof i !== "string").map((i) => [`${i.kind}:${i.id}`, i.name])),
  };
}

/** Marcar un permiso marca lo que supone; desmarcarlo desmarca lo que depende de él. */
function roleToggle(perms, key, on) {
  const byKey = rolePermByKey();
  if (on) {
    const stack = [key];
    while (stack.length) {
      const k = stack.pop();
      if (perms.has(k)) continue;
      perms.add(k);
      stack.push(...(byKey.get(k)?.requires ?? []));
    }
  } else {
    const stack = [key];
    while (stack.length) {
      const k = stack.pop();
      if (!perms.delete(k)) continue;
      for (const p of rolesState.catalog.permissions) if (p.requires.includes(k)) stack.push(p.key);
    }
  }
}

async function renderRoles() {
  $("#page-title").textContent = "Roles";
  if (!Perms.can("roles.manage")) {
    $("#view").innerHTML = `<div class="warn-box">Sus roles no incluyen la administración de roles.</div>`;
    return;
  }
  try {
    [rolesState.catalog, rolesState.roles, rolesState.locations] = await Promise.all([
      Api.get("/api/roles/catalog"), Api.get("/api/roles"), Api.get("/api/locations").catch(() => []),
    ]);
  } catch (err) {
    $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }
  rolesState.scopeCatalog = null;
  // El rol abierto viaja en la dirección (#/roles?r=5) para volver a él.
  const wanted = Number(new URLSearchParams(location.hash.split("?")[1] ?? "").get("r"));
  const first = rolesState.roles[0]?.id ?? null;
  rolesOpen(rolesState.roles.some((r) => r.id === wanted) ? wanted : (rolesState.selected === "new" ? first : rolesState.selected ?? first), true);

  rolesDetachHub();
  rolesState.hubBound = async (topic) => {
    if (location.hash.split("?")[0] !== "#/roles") return;
    // Equipos, canales o ubicaciones cambiaron: el catálogo del alcance se vuelve a pedir.
    if (["devices", "channels", "locations", "alarm-panels", "access-devices", "speakers", "intercoms"].includes(topic)) {
      rolesState.scopeCatalog = null;
      if (topic === "locations") rolesState.locations = await Api.get("/api/locations").catch(() => rolesState.locations);
      return;
    }
    if (topic !== "roles") return;
    try { rolesState.roles = await Api.get("/api/roles"); } catch { return; }
    // Lo que se está editando no se pisa: se refresca la lista y, si no hay cambios, también el rol.
    if (roleIsDirty()) rolesDrawList();
    else rolesOpen(rolesState.roles.some((r) => r.id === rolesState.selected) ? rolesState.selected : rolesState.roles[0]?.id, true);
  };
  VmsHub.on("ConfigChanged", rolesState.hubBound);
}

/** Abre un rol (id), uno nuevo ("new", con plantilla opcional) o una copia. */
function rolesOpen(target, force, seed) {
  if (!force && roleIsDirty() && !confirm("Hay cambios sin guardar en este rol. ¿Descartarlos?")) return;
  rolesState.selected = target;
  const role = roleSelected();
  rolesState.draft = target === "new" ? roleDraftOf(seed) : role ? roleDraftOf(role) : null;
  rolesState.saved = target === "new" ? roleDraftPrint(roleDraftOf(null)) : roleDraftPrint(rolesState.draft);
  if (typeof target === "number" && location.hash !== `#/roles?r=${target}`) history.replaceState(null, "", `#/roles?r=${target}`);
  $("#view").innerHTML = `
    <div class="rl-layout">
      <aside class="rl-list" id="rl-list"></aside>
      <section class="rl-editor" id="rl-editor"></section>
    </div>`;
  rolesDrawList();
  rolesDrawEditor();
}

function rolesDrawList() {
  const box = $("#rl-list");
  if (!box) return;
  const total = rolesState.catalog.permissions.length;
  box.innerHTML = `
    <div class="rl-list-head">
      <span>Roles</span>
      <button class="btn small" type="button" id="rl-new">+ Nuevo rol</button>
    </div>
    ${rolesState.roles.map((r) => `
      <button type="button" class="rl-card${r.id === rolesState.selected ? " active" : ""}" data-id="${r.id}">
        <span class="rl-card-name">${esc(r.name)}${r.systemKey ? ` <span class="tag operator">Sistema</span>` : ""}</span>
        <span class="rl-meter" title="${r.permissions.length} de ${total} permisos"><i style="width:${Math.round(100 * r.permissions.length / Math.max(total, 1))}%"></i></span>
        <span class="rl-card-meta">${r.systemKey === "admin" ? "Todos los permisos" : `${r.permissions.length} permiso(s)`}
          · ${r.userCount === 0 ? "sin usuarios" : `${r.userCount} usuario(s)`}</span>
        <span class="rl-card-scope${r.restrictScope ? " limited" : ""}" title="${esc(r.scopeSummary || "")}">${r.restrictScope
          ? `Limitado: ${esc(r.scopeSummary || "")}` : "Todo el sistema"}</span>
      </button>`).join("")}
    ${rolesState.selected === "new" ? `<div class="rl-card active rl-card-new"><span class="rl-card-name">Rol nuevo</span>
      <span class="rl-card-meta">sin guardar</span></div>` : ""}`;
  $("#rl-new").addEventListener("click", () => rolesNewModal());
  $$(".rl-card[data-id]", box).forEach((b) => b.addEventListener("click", () => rolesOpen(Number(b.dataset.id))));
}

/** Rol nuevo: en blanco o a partir de una plantilla (se copia y se ajusta). */
function rolesNewModal() {
  if (roleIsDirty() && !confirm("Hay cambios sin guardar en este rol. ¿Descartarlos?")) return;
  const grantable = new Set(rolesState.catalog.grantable);
  const options = [{ key: "", name: "En blanco", description: "Sin permisos: márquelos uno a uno.", permissions: [] },
    ...rolesState.catalog.templates];
  openModal(`
    <h3>Nuevo rol</h3>
    <p class="muted">Elija un punto de partida. Los permisos se copian al rol y después se ajustan; la plantilla no queda vinculada.</p>
    <div class="rl-templates">
      ${options.map((t, i) => {
        const blocked = t.permissions.filter((p) => !grantable.has(p)).length;
        return `<label class="rl-template">
          <input type="radio" name="rl-tpl" value="${esc(t.key)}" ${i === 0 ? "checked" : ""}>
          <span><b>${esc(t.name)}</b> <span class="muted">· ${t.permissions.length} permiso(s)</span><br>
            <span class="muted small">${esc(t.description)}</span>
            ${blocked ? `<br><span class="small warn-text">${blocked} permiso(s) de la plantilla no los tiene usted: quedarán fuera.</span>` : ""}</span>
        </label>`;
      }).join("")}
    </div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="rl-tpl-cancel">Cancelar</button>
      <button class="btn" type="button" id="rl-tpl-ok">Continuar</button>
    </div>`);
  $("#rl-tpl-cancel").addEventListener("click", closeModal);
  $("#rl-tpl-ok").addEventListener("click", () => {
    const key = $("input[name=rl-tpl]:checked")?.value ?? "";
    const t = options.find((o) => o.key === key);
    closeModal();
    rolesOpen("new", true, {
      name: key ? t.name : "",
      description: key ? t.description : "",
      permissions: t.permissions.filter((p) => grantable.has(p)),
    });
  });
}

function rolesDrawEditor() {
  const box = $("#rl-editor");
  if (!box) return;
  const role = roleSelected();
  const d = rolesState.draft;
  if (!d) { box.innerHTML = `<div class="info-box">Elija un rol de la lista o cree uno nuevo.</div>`; return; }
  const isNew = rolesState.selected === "new";
  const isAdminRole = role?.systemKey === "admin";
  const editable = isNew || !!role?.editable;
  const grantable = new Set(rolesState.catalog.grantable);
  const total = rolesState.catalog.permissions.length;
  const q = rolesState.filter.toLowerCase();
  const matches = (p) => !q || (p.label + " " + p.description + " " + p.group).toLowerCase().includes(q);
  const byKey = rolePermByKey();

  const note = isAdminRole
    ? `<div class="info-box">El rol <b>Administrador</b> tiene todos los permisos, también los que traigan versiones futuras, y ve y opera todo el sistema. No se modifica ni se elimina; debe quedar siempre al menos un usuario habilitado con él.</div>`
    : !editable
      ? `<div class="warn-box">Este rol tiene permisos o un alcance que usted no tiene: puede verlo, pero no modificarlo.</div>`
      : role?.systemKey === "operator"
        ? `<div class="muted rl-intro">Rol de sistema para la operación diaria: se puede ajustar, no eliminar.</div>`
        : "";

  box.innerHTML = `
    ${note}
    <div class="rl-head">
      <div class="field">
        <label for="rl-name">Nombre</label>
        <input id="rl-name" maxlength="64" value="${esc(d.name)}" ${editable && !role?.systemKey ? "" : "disabled"} placeholder="Ej.: Guardia de portería">
      </div>
      <div class="field">
        <label for="rl-desc">Descripción</label>
        <input id="rl-desc" maxlength="256" value="${esc(d.description)}" ${editable && !isAdminRole ? "" : "disabled"}
          placeholder="Para qué es este rol (se ve al asignarlo)">
      </div>
    </div>
    <div class="rl-scope" id="rl-scope"></div>
    <div class="rl-toolbar">
      <input type="search" id="rl-filter" placeholder="Buscar permiso…" value="${esc(rolesState.filter)}">
      <span class="rl-summary" id="rl-summary"></span>
    </div>
    <div class="rl-groups">
      ${rolesState.catalog.groups.map((g) => {
        const items = rolesState.catalog.permissions.filter((p) => p.group === g && matches(p));
        if (!items.length) return "";
        const all = rolesState.catalog.permissions.filter((p) => p.group === g);
        const on = all.filter((p) => isAdminRole || d.permissions.has(p.key)).length;
        return `
          <div class="rl-group">
            <div class="rl-group-head">
              <span class="rl-group-name">${esc(g)}</span>
              <span class="muted">${on} de ${all.length}</span>
              ${editable && !isAdminRole ? `<span class="rl-group-actions">
                <button type="button" class="btn ghost small" data-group-all="${esc(g)}">Todos</button>
                <button type="button" class="btn ghost small" data-group-none="${esc(g)}">Ninguno</button></span>` : ""}
            </div>
            ${items.map((p) => {
              const checked = isAdminRole || d.permissions.has(p.key);
              const locked = !isAdminRole && editable && !grantable.has(p.key) && !checked;
              const requires = p.requires.map((k) => byKey.get(k)?.label ?? k);
              return `
                <label class="rl-perm${checked ? " on" : ""}${locked ? " locked" : ""}" title="${locked ? "Usted no tiene este permiso: no puede otorgarlo" : ""}">
                  <input type="checkbox" class="rl-switch" data-perm="${esc(p.key)}" ${checked ? "checked" : ""}
                    ${!editable || isAdminRole || locked ? "disabled" : ""}>
                  <span class="rl-perm-text">
                    <span class="rl-perm-label">${esc(p.label)}${p.sensitive ? ` <span class="tag warn" title="Da control sobre la seguridad del sistema">Seguridad</span>` : ""}</span>
                    <span class="muted small">${esc(p.description)}${requires.length ? ` <i>Incluye: ${requires.map(esc).join(", ")}.</i>` : ""}</span>
                  </span>
                </label>`;
            }).join("")}
          </div>`;
      }).join("") || `<div class="muted">Ningún permiso coincide con la búsqueda.</div>`}
    </div>
    ${role ? `<div class="rl-users"><span class="muted">Usuarios con este rol:</span>
      ${role.users.length ? role.users.map((u) => `<span class="tag operator">${esc(u)}</span>`).join(" ") : `<span class="muted">ninguno</span>`}</div>` : ""}
    <div class="rl-actions">
      ${role && !role.systemKey && editable ? `<button class="btn danger" type="button" id="rl-delete">Eliminar</button>` : ""}
      <span class="rl-spacer"></span>
      ${role && !isAdminRole ? `<button class="btn ghost" type="button" id="rl-copy" title="Crea un rol nuevo con estos mismos permisos y alcance">Duplicar</button>` : ""}
      ${isNew ? `<button class="btn ghost" type="button" id="rl-cancel">Cancelar</button>` : ""}
      ${editable && !isAdminRole ? `<button class="btn" type="button" id="rl-save">${isNew ? "Crear rol" : "Guardar cambios"}</button>` : ""}
    </div>`;

  rolesDrawScope();
  rolesUpdateSummary(total, isAdminRole);
  $("#rl-name").addEventListener("input", (e) => { d.name = e.target.value; rolesUpdateSummary(total, isAdminRole); });
  $("#rl-desc").addEventListener("input", (e) => { d.description = e.target.value; rolesUpdateSummary(total, isAdminRole); });
  $("#rl-filter").addEventListener("input", (e) => {
    rolesState.filter = e.target.value;
    const pos = e.target.selectionStart;
    rolesDrawEditor();
    const input = $("#rl-filter");
    input.focus();
    input.setSelectionRange(pos, pos);
  });
  $$(".rl-switch", box).forEach((c) => c.addEventListener("change", () => {
    roleToggle(d.permissions, c.dataset.perm, c.checked);
    rolesDrawEditor();
  }));
  $$("[data-group-all]", box).forEach((b) => b.addEventListener("click", () => {
    for (const p of rolesState.catalog.permissions)
      if (p.group === b.dataset.groupAll && grantable.has(p.key)) roleToggle(d.permissions, p.key, true);
    rolesDrawEditor();
  }));
  $$("[data-group-none]", box).forEach((b) => b.addEventListener("click", () => {
    for (const p of rolesState.catalog.permissions)
      if (p.group === b.dataset.groupNone) roleToggle(d.permissions, p.key, false);
    rolesDrawEditor();
  }));
  $("#rl-save")?.addEventListener("click", () => rolesSave());
  $("#rl-cancel")?.addEventListener("click", () => rolesOpen(rolesState.roles[0]?.id ?? null));
  $("#rl-copy")?.addEventListener("click", () => {
    const grantableNow = new Set(rolesState.catalog.grantable);
    rolesOpen("new", false, {
      ...roleDraftOf(role),
      name: `${role.name} (copia)`.slice(0, 64),
      permissions: role.permissions.filter((p) => grantableNow.has(p)),
      items: role.items ?? [],
    });
  });
  $("#rl-delete")?.addEventListener("click", () => rolesDelete(role));
}

// ---------------------------------------------------------------------------
// Alcance del rol: todo el sistema, o ubicaciones + recursos sueltos
// ---------------------------------------------------------------------------

/** Nombre legible de un recurso suelto ("Cámara · DVR Norte · Acceso"). */
function roleItemLabel(key) {
  const [kind, id] = key.split(":");
  const item = rolesState.scopeCatalog?.find((x) => x.kind === kind && String(x.id) === id);
  const name = item ? (item.equipment ? `${item.equipment} · ${item.name}` : item.name)
    : rolesState.draft?.names.get(key) ?? `#${id} (ya no existe)`;
  return { kind: roleKindLabel(kind), name, location: item?.location ?? null };
}

function rolesDrawScope() {
  const box = $("#rl-scope");
  if (!box) return;
  const role = roleSelected();
  const d = rolesState.draft;
  const isAdminRole = role?.systemKey === "admin";
  const editable = (rolesState.selected === "new" || !!role?.editable) && !isAdminRole;
  const dis = editable ? "" : "disabled";
  const summary = isAdminRole || !d.restrict ? "Todo el sistema"
    : `${d.locations.size} ubicación(es), ${d.items.size} recurso(s) suelto(s)${d.viewOutside ? " · ve el resto" : ""}`;

  box.innerHTML = `
    <div class="rl-group-head">
      <span class="rl-group-name">Alcance: dónde valen estos permisos</span>
      <span class="muted">${esc(summary)}</span>
    </div>
    ${isAdminRole ? `<div class="muted small">El Administrador ve y opera todo el sistema.</div>` : `
    <label class="radio-row"><input type="radio" name="rl-scope-mode" value="all" ${d.restrict ? "" : "checked"} ${dis}>
      Todo el sistema</label>
    <label class="radio-row"><input type="radio" name="rl-scope-mode" value="some" ${d.restrict ? "checked" : ""} ${dis}>
      Solo en estas ubicaciones y recursos</label>
    ${d.restrict ? `
    <div class="rl-scope-pick">
      <div class="rl-scope-col">
        <div class="rl-scope-col-title">Ubicaciones <span class="muted">(cada una con sus sububicaciones y todo lo que hay adentro)</span></div>
        <div class="scope-tree" id="rl-scope-tree">${userScopeTreeHtml(rolesState.locations, d.locations)}</div>
      </div>
      <div class="rl-scope-col">
        <div class="rl-scope-col-title">Recursos sueltos <span class="muted">(de cualquier ubicación)</span></div>
        <div class="rl-scope-chips" id="rl-scope-chips"></div>
        ${editable ? `
        <div class="rl-scope-search">
          <select id="rl-scope-kind">
            <option value="">Todos los tipos</option>
            ${ROLE_SCOPE_KINDS.map(([k, label]) => `<option value="${k}" ${rolesState.scopeKind === k ? "selected" : ""}>${esc(label)}</option>`).join("")}
          </select>
          <input type="search" id="rl-scope-q" placeholder="Buscar cámara, puerta, equipo, ubicación…" value="${esc(rolesState.scopeQuery)}">
        </div>
        <div class="rl-scope-results" id="rl-scope-results"><div class="muted small">Cargando recursos…</div></div>` : ""}
      </div>
    </div>
    <label class="checkbox-row" style="margin-top:8px">
      <input type="checkbox" id="rl-view-outside" ${d.viewOutside ? "checked" : ""} ${dis}>
      <span>Puede <b>ver</b> el resto, sin operarlo (supervisión)</span>
    </label>` : ""}
    <div class="muted small rl-scope-hint">Un usuario suma el alcance de todos sus roles: basta un rol con "Todo el sistema" para que vea
      todo. Un equipo completo incluye también los canales, puertas o áreas que se le agreguen después.</div>`}`;

  if (isAdminRole) return;
  $$("input[name=rl-scope-mode]", box).forEach((r) => r.addEventListener("change", () => {
    d.restrict = r.value === "some";
    rolesDrawScope();
    rolesUpdateSummary();
  }));
  if (!d.restrict) return;
  const tree = $("#rl-scope-tree");
  userScopeSync(tree);
  if (!editable) $$("input", tree).forEach((b) => { b.disabled = true; });
  tree.addEventListener("change", () => {
    userScopeSync(tree);
    // Solo las marcadas a mano: las incluidas por una de arriba ya van con ella.
    d.locations = new Set($$("input[data-loc]", tree).filter((b) => b.checked && b.dataset.inherited !== "1").map((b) => Number(b.dataset.loc)));
    rolesUpdateSummary();
  });
  $("#rl-view-outside").addEventListener("change", (e) => { d.viewOutside = e.target.checked; rolesUpdateSummary(); });
  rolesDrawChips(editable);
  if (!editable) return;
  $("#rl-scope-kind").addEventListener("change", (e) => { rolesState.scopeKind = e.target.value; rolesDrawResults(); });
  $("#rl-scope-q").addEventListener("input", (e) => { rolesState.scopeQuery = e.target.value; rolesDrawResults(); });
  $("#rl-scope-results").addEventListener("change", (e) => {
    const box2 = e.target.closest("input[data-item]");
    if (!box2) return;
    if (box2.checked) d.items.add(box2.dataset.item); else d.items.delete(box2.dataset.item);
    rolesDrawChips(true);
    rolesUpdateSummary();
  });
  rolesLoadScopeCatalog();
}

async function rolesLoadScopeCatalog() {
  if (!rolesState.scopeCatalog) {
    try { rolesState.scopeCatalog = await Api.get("/api/roles/scope-catalog"); }
    catch (err) {
      const box = $("#rl-scope-results");
      if (box) box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      return;
    }
    rolesDrawChips(true);
  }
  rolesDrawResults();
}

/** Lo elegido, como etiquetas con "quitar". */
function rolesDrawChips(editable) {
  const box = $("#rl-scope-chips");
  if (!box) return;
  const d = rolesState.draft;
  const keys = [...d.items].sort((a, b) => roleItemLabel(a).name.localeCompare(roleItemLabel(b).name, "es"));
  box.innerHTML = keys.length
    ? keys.map((k) => {
      const l = roleItemLabel(k);
      return `<span class="rl-chip" title="${esc(l.location ? `Ubicación: ${l.location}` : "Por ubicar")}">
        <span class="muted">${esc(l.kind)}</span> ${esc(l.name)}
        ${editable ? `<button type="button" data-remove="${esc(k)}" title="Quitar del alcance">×</button>` : ""}</span>`;
    }).join("")
    : `<span class="muted small">Ninguno: marque abajo cámaras, puertas, áreas o equipos completos.</span>`;
  $$("[data-remove]", box).forEach((b) => b.addEventListener("click", () => {
    d.items.delete(b.dataset.remove);
    rolesDrawChips(true);
    rolesDrawResults();
    rolesUpdateSummary();
  }));
}

/** Resultados del buscador de recursos sueltos (con tope, para catálogos grandes). */
function rolesDrawResults() {
  const box = $("#rl-scope-results");
  if (!box || !rolesState.scopeCatalog) return;
  const d = rolesState.draft;
  const norm = (s) => String(s ?? "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
  const q = norm(rolesState.scopeQuery.trim());
  const list = rolesState.scopeCatalog.filter((x) =>
    (!rolesState.scopeKind || x.kind === rolesState.scopeKind) &&
    (!q || norm(`${x.name} ${x.equipment ?? ""} ${x.location ?? ""} ${roleKindLabel(x.kind)}`).includes(q)));
  const shown = list.slice(0, ROLE_SCOPE_MAX_RESULTS);
  box.innerHTML = shown.length ? shown.map((x) => {
    const key = `${x.kind}:${x.id}`;
    return `<label class="rl-scope-row">
      <input type="checkbox" data-item="${esc(key)}" ${d.items.has(key) ? "checked" : ""}>
      <span class="rl-scope-kind">${esc(roleKindLabel(x.kind))}</span>
      <span class="rl-scope-name" title="${esc(x.equipment ? `${x.equipment} · ${x.name}` : x.name)}">${esc(x.name)}${x.equipment ? ` <span class="muted small">· ${esc(x.equipment)}</span>` : ""}</span>
      <span class="muted small rl-scope-loc">${esc(x.location ?? "Por ubicar")}</span>
    </label>`;
  }).join("") + (list.length > shown.length
    ? `<div class="muted small" style="padding:6px">Hay ${list.length - shown.length} más: afine la búsqueda.</div>` : "")
    : `<div class="muted small" style="padding:6px">Ningún recurso coincide.</div>`;
}

function rolesUpdateSummary(total, isAdminRole) {
  const el = $("#rl-summary");
  if (!el) return;
  total ??= rolesState.catalog.permissions.length;
  isAdminRole ??= roleSelected()?.systemKey === "admin";
  const n = isAdminRole ? total : rolesState.draft.permissions.size;
  el.innerHTML = `${n} de ${total} permisos${roleIsDirty() ? ` · <span class="warn-text">sin guardar</span>` : ""}`;
}

async function rolesSave() {
  const d = rolesState.draft;
  const isNew = rolesState.selected === "new";
  const body = {
    name: d.name.trim(), description: d.description.trim(), permissions: [...d.permissions],
    restrictScope: d.restrict,
    viewOutsideScope: d.restrict && d.viewOutside,
    locationIds: d.restrict ? [...d.locations] : [],
    items: d.restrict ? [...d.items].map((k) => { const [kind, id] = k.split(":"); return { kind, id: Number(id) }; }) : [],
  };
  if (body.name.length < 2) { toast("El nombre del rol debe tener al menos 2 caracteres.", true); return; }
  if (d.restrict && !body.locationIds.length && !body.items.length && !body.viewOutsideScope) {
    toast("Elija al menos una ubicación o un recurso, o marque que puede ver el resto.", true);
    return;
  }
  try {
    const saved = isNew ? await Api.post("/api/roles", body) : await Api.put(`/api/roles/${rolesState.selected}`, body);
    toast(isNew ? `Rol "${saved.name}" creado.` : `Rol "${saved.name}" guardado.` +
      (saved.userCount ? ` Sus ${saved.userCount} usuario(s) ya tienen los cambios.` : ""));
    rolesState.roles = await Api.get("/api/roles");
    rolesOpen(saved.id, true);
  } catch (err) { toast(err.error, true); }
}

async function rolesDelete(role) {
  if (role.userCount > 0) {
    toast(`"${role.name}" está asignado a ${role.users.join(", ")}: quíteselo antes de eliminarlo.`, true);
    return;
  }
  if (!confirm(`¿Eliminar el rol "${role.name}"? Esta acción no se puede deshacer.`)) return;
  try {
    await Api.delete(`/api/roles/${role.id}`);
    toast("Rol eliminado.");
    rolesState.roles = await Api.get("/api/roles");
    rolesOpen(rolesState.roles[0]?.id ?? null, true);
  } catch (err) { toast(err.error, true); }
}
