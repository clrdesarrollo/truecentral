// CLR TrueCentral VMS — panel: Configuración → Tableros.
//
// Los tableros compartidos del Centro de mando (cliente de escritorio): qué
// ve cada rol en su grilla —cámaras, puertas, alarmas, tarjetas de reporte y
// páginas web— sin que el guardia pueda cambiarlo. Y la lista de páginas web
// que cualquiera puede poner en un cuadro (escribir una dirección libre pide
// el permiso "Abrir cualquier página web").
//
// Se carga ANTES que app.js (solo declara funciones) y DESPUÉS de live.js y
// resources.js, de los que usa las divisiones (LV_LAYOUT_GROUPS) y los íconos.
"use strict";

const BD_CARDS = [
  { key: "card-equipment", label: "Estado de los equipos", detail: "En línea por tipo y los que están sin conexión" },
  { key: "card-server", label: "Servidor", detail: "CPU, memoria, disco y servicios" },
  { key: "card-alerts", label: "Últimas alertas", detail: "Alertas de las automatizaciones" },
  { key: "card-plates", label: "Últimas patentes", detail: "Reconocimientos de patente en vivo" },
  { key: "card-access", label: "Últimos accesos", detail: "Pasos por las puertas, sin eventos del sistema" },
];
/** Tipos de recurso que puede llevar un cuadro (las zonas se ven dentro de su panel). */
const BD_RESOURCE_KINDS = ["Camera", "Door", "Partition", "Fence", "Intercom", "Speaker", "IoModule"];
const BD_PICK_LIMIT = 40;

/** Estado de la página (lo que se cargó del servidor y el tablero que se está editando). */
const bdState = { views: [], links: [], roles: [], resources: [], speakerGroups: [], byKey: new Map(), hubBound: [], editing: null };

function boardsDetachHub() {
  for (const { ev, fn } of bdState.hubBound) VmsHub.off(ev, fn);
  bdState.hubBound = [];
}

async function renderBoards() {
  $("#page-title").textContent = "Tableros";
  boardsDetachHub();
  if (!Perms.can("dashboards.manage")) {
    $("#view").innerHTML = `<div class="warn-box">Sus roles no incluyen «Administrar tableros compartidos».</div>`;
    return;
  }
  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <button class="btn" id="bd-new">Nuevo tablero</button>
      <button class="btn ghost" id="bd-link-new">Agregar página web</button>
    </div>
    <div class="info-box bd-intro">
      Los tableros compartidos aparecen en el <b>Centro de mando</b> del cliente de escritorio de los usuarios a los que
      se asignan, con candado: solo quien administra los tableros los cambia o los borra. Para que un rol no arme
      tableros propios, quítele <b>Crear tableros propios</b> en <a href="#/roles">Roles</a>.
    </div>
    <h3 class="bd-h">Tableros compartidos</h3>
    <div id="bd-views"><div class="muted">Cargando…</div></div>
    <h3 class="bd-h">Páginas web</h3>
    <p class="muted bd-note">Las que cualquiera con video en vivo puede poner en un cuadro (mapas, noticias, un sistema
      externo…). Escribir otra dirección a mano pide el permiso <b>Abrir cualquier página web</b>.</p>
    <div id="bd-links"></div>`;
  $("#bd-new").addEventListener("click", () => bdEditor(null));
  $("#bd-link-new").addEventListener("click", () => bdLinkModal(null));
  const onConfig = (topic) => { if (topic === "live-views" || topic === "web-links" || topic === "speaker-groups") bdLoad(true); };
  VmsHub.on("ConfigChanged", onConfig);
  bdState.hubBound.push({ ev: "ConfigChanged", fn: onConfig });
  await bdLoad(false);
}

async function bdLoad(quiet) {
  try {
    const [views, links, roles, resources, speakerGroups] = await Promise.all([
      Api.get("/api/live-views"), Api.get("/api/web-links"), Api.get("/api/live-views/roles"), Api.get("/api/resources"),
      Api.get("/api/speakers/groups").catch(() => []),
    ]);
    // Las de «Mis páginas» de cada usuario no son de la lista compartida que se administra aquí.
    Object.assign(bdState, { views: views.filter((v) => v.shared), links: links.filter((l) => !l.personal), roles, resources, speakerGroups });
    bdState.byKey = new Map(resources.map((r) => [`${r.kind}:${r.id}`, r]));
  } catch (err) {
    if (!quiet && $("#bd-views")) $("#bd-views").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }
  bdDrawViews();
  bdDrawLinks();
}

// ---------------------------------------------------------------------------
// Listas
// ---------------------------------------------------------------------------

function bdContents(items) {
  const n = {};
  for (const i of items) n[i.kind || "camera"] = (n[i.kind || "camera"] || 0) + 1;
  return [n.camera && `${n.camera} cámara(s)`, n.resource && `${n.resource} recurso(s)`,
    n.card && `${n.card} tarjeta(s)`, n.web && `${n.web} página(s) web`,
    n["speaker-group"] && `${n["speaker-group"]} grupo(s) de parlantes`].filter(Boolean).join(" · ") || "vacío";
}

function bdAudience(v) {
  if (!v.restrictToRoles) return `<span class="tag on">Todos</span>`;
  if (!v.roles?.length) return `<span class="tag warn" title="Sus roles se borraron: lo ve solo quien administra los tableros">Sin roles</span>`;
  return v.roles.map((r) => `<span class="tag operator">${esc(r)}</span>`).join(" ");
}

function bdLayoutLabel(v) {
  const layout = lvFindLayout(v.layoutName);
  return layout ? lvLayoutLabel(layout) : `${v.columns}×${v.rows} (del cliente)`;
}

function bdDrawViews() {
  const box = $("#bd-views");
  if (!box) return;
  if (!bdState.views.length) {
    box.innerHTML = `<div class="info-box">Todavía no hay tableros compartidos. Use <b>Nuevo tablero</b>, o guarde uno
      como compartido desde el Centro de mando del cliente de escritorio.</div>`;
    return;
  }
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Nombre</th><th>Lo ven</th><th>División</th><th>Contenido</th><th>Actualizado</th><th></th></tr></thead>
      <tbody>${bdState.views.map((v) => `
        <tr data-id="${v.id}">
          <td><b>${esc(v.name)}</b>${v.owner ? `<div class="muted bd-sub">creado por ${esc(v.owner)}</div>` : ""}</td>
          <td>${bdAudience(v)}</td>
          <td>${esc(bdLayoutLabel(v))}</td>
          <td>${esc(bdContents(v.items))}</td>
          <td>${esc(new Date(v.updatedAt).toLocaleString("es-CL", { dateStyle: "short", timeStyle: "short" }))}</td>
          <td class="row-actions">
            <button class="btn ghost small" data-act="edit">Editar</button>
            <button class="btn danger small" data-act="delete">Eliminar</button>
          </td>
        </tr>`).join("")}</tbody>
    </table></div>`;
  $$("#bd-views [data-act]").forEach((b) => b.addEventListener("click", () => {
    const v = bdState.views.find((x) => x.id === Number(b.closest("tr").dataset.id));
    if (!v) return;
    if (b.dataset.act === "edit") bdEditor(v);
    else bdDeleteView(v);
  }));
}

async function bdDeleteView(v) {
  if (!confirm(`¿Eliminar el tablero compartido "${v.name}"? Desaparece del Centro de mando de todos los que lo ven.`)) return;
  try {
    await Api.delete(`/api/live-views/${v.id}`);
    toast(`Tablero "${v.name}" eliminado.`);
    await bdLoad(true);
  } catch (err) { toast(err.error, true); }
}

function bdDrawLinks() {
  const box = $("#bd-links");
  if (!box) return;
  if (!bdState.links.length) {
    box.innerHTML = `<div class="info-box">Todavía no hay páginas en la lista. Use <b>Agregar página web</b>.</div>`;
    return;
  }
  const uses = (id) => bdState.views.filter((v) => v.items.some((i) => i.kind === "web" && i.refId === id)).length;
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Nombre</th><th>Dirección</th><th>Descripción</th><th>En tableros</th><th></th></tr></thead>
      <tbody>${bdState.links.map((l) => `
        <tr data-id="${l.id}">
          <td><b>${esc(l.name)}</b></td>
          <td class="bd-url"><a href="${esc(l.url)}" target="_blank" rel="noopener noreferrer">${esc(l.url)}</a></td>
          <td>${esc(l.description || "")}</td>
          <td>${uses(l.id) || "—"}</td>
          <td class="row-actions">
            <button class="btn ghost small" data-act="edit">Editar</button>
            <button class="btn danger small" data-act="delete">Eliminar</button>
          </td>
        </tr>`).join("")}</tbody>
    </table></div>`;
  $$("#bd-links [data-act]").forEach((b) => b.addEventListener("click", () => {
    const l = bdState.links.find((x) => x.id === Number(b.closest("tr").dataset.id));
    if (!l) return;
    if (b.dataset.act === "edit") bdLinkModal(l);
    else bdDeleteLink(l, uses(l.id));
  }));
}

function bdLinkModal(link) {
  openModal(`
    <h3>${link ? "Editar página web" : "Agregar página web"}</h3>
    <div id="bd-link-error"></div>
    <div class="field"><label for="bd-link-name">Nombre</label>
      <input id="bd-link-name" maxlength="128" value="${esc(link?.name ?? "")}" placeholder="Mapa del sector, Tránsito en vivo…"></div>
    <div class="field"><label for="bd-link-url">Dirección</label>
      <input id="bd-link-url" type="url" maxlength="2048" value="${esc(link?.url ?? "")}" placeholder="https://…">
      <div class="muted field-hint">Algunos sitios no se dejan mostrar dentro de otra aplicación; pruébela en un cuadro antes de usarla en un tablero.</div></div>
    <div class="field"><label for="bd-link-desc">Descripción (opcional)</label>
      <input id="bd-link-desc" maxlength="500" value="${esc(link?.description ?? "")}"></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="bd-link-cancel">Cancelar</button>
      <button class="btn" type="button" id="bd-link-save">Guardar</button>
    </div>`, true);
  $("#bd-link-cancel").addEventListener("click", closeModal);
  $("#bd-link-save").addEventListener("click", async () => {
    const body = { name: $("#bd-link-name").value.trim(), url: $("#bd-link-url").value.trim(), description: $("#bd-link-desc").value.trim() || null };
    try {
      if (link) await Api.put(`/api/web-links/${link.id}`, body);
      else await Api.post("/api/web-links", body);
      closeModal();
      toast(link ? "Página actualizada." : `Página "${body.name}" agregada a la lista.`);
      await bdLoad(true);
    } catch (err) {
      $("#bd-link-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

async function bdDeleteLink(link, uses) {
  if (!confirm(`¿Quitar "${link.name}" de la lista?` +
    (uses ? `\n\nEstá en ${uses} tablero(s): esos cuadros quedarán libres.` : ""))) return;
  try {
    await Api.delete(`/api/web-links/${link.id}`);
    toast(`"${link.name}" quitada de la lista.`);
    await bdLoad(true);
  } catch (err) { toast(err.error, true); }
}

// ---------------------------------------------------------------------------
// Editor de un tablero: nombre, con quién se comparte, división y qué va en
// cada cuadro (se elige en la grilla y se busca en la lista de la derecha).
// ---------------------------------------------------------------------------

function bdEditor(view) {
  const known = view ? lvFindLayout(view.layoutName) : lvFindLayout("4");
  const ed = bdState.editing = {
    id: view?.id ?? null,
    layout: known,
    // División del cliente que el panel web no dibuja (más de 16 cuadros):
    // aquí se cambian el nombre y con quién se comparte; los cuadros se conservan.
    locked: !!view && !known,
    raw: view ? { layoutName: view.layoutName, columns: view.columns, rows: view.rows } : null,
    items: new Map((view?.items ?? []).map((i) => [i.cellIndex, { ...i }])),
    selected: null,
    text: "",
  };
  const restricted = !!view?.restrictToRoles;
  const groups = LV_LAYOUT_GROUPS.map((g) => `
    <optgroup label="${esc(g.title)}">${g.layouts.map((l) =>
      `<option value="${esc(l.key)}"${known?.key === l.key ? " selected" : ""}>${esc(l.key)}</option>`).join("")}</optgroup>`).join("");
  const custom = known && !LV_LAYOUTS.includes(known)
    ? `<option value="${esc(known.key)}" selected>${esc(lvLayoutLabel(known))}</option>` : "";
  openModal(`
    <h3>${view ? "Editar tablero compartido" : "Nuevo tablero compartido"}</h3>
    <div id="bd-error"></div>
    <div class="form-grid">
      <div class="field"><label for="bd-name">Nombre</label>
        <input id="bd-name" maxlength="128" value="${esc(view?.name ?? "")}" placeholder="Portería, Turno noche, Perímetro…"></div>
      <div class="field"><label for="bd-layout">División</label>
        <select id="bd-layout"${ed.locked ? " disabled" : ""}>
          ${ed.locked ? `<option selected>${esc(`${view.columns}×${view.rows} (del cliente)`)}</option>` : custom + groups}
        </select></div>
    </div>
    <div class="field">
      <label>Lo ven</label>
      <div class="bd-aud">
        <label class="bd-radio"><input type="radio" name="bd-aud" value="all"${restricted ? "" : " checked"}> Todos los usuarios con video en vivo</label>
        <label class="bd-radio"><input type="radio" name="bd-aud" value="roles"${restricted ? " checked" : ""}> Solo estos roles:</label>
      </div>
      <div class="bd-roles" id="bd-roles">${bdState.roles.map((r) => `
        <label class="bd-check"><input type="checkbox" value="${r.id}"${view?.roleIds?.includes(r.id) ? " checked" : ""}> ${esc(r.name)}</label>`).join("")}
      </div>
    </div>
    <div class="bd-editor">
      <div class="bd-grid" id="bd-grid"></div>
      <div class="bd-picker" id="bd-picker"></div>
    </div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="bd-cancel">Cancelar</button>
      <button class="btn" type="button" id="bd-save">Guardar</button>
    </div>`, "wider");
  const syncRoles = () => {
    const on = document.querySelector('input[name="bd-aud"]:checked')?.value === "roles";
    $$("#bd-roles input").forEach((cb) => { cb.disabled = !on; });
    $("#bd-roles").classList.toggle("off", !on);
  };
  $$('input[name="bd-aud"]').forEach((r) => r.addEventListener("change", syncRoles));
  syncRoles();
  $("#bd-layout").addEventListener("change", (e) => {
    const layout = lvFindLayout(e.target.value);
    if (!layout) return;
    ed.layout = layout;
    if (ed.selected != null && ed.selected >= layout.cells.length) ed.selected = null;
    bdDrawGrid();
    bdDrawPicker();
  });
  $("#bd-cancel").addEventListener("click", closeModal);
  $("#bd-save").addEventListener("click", bdSave);
  bdDrawGrid();
  bdDrawPicker();
}

/** Ícono, nombre y detalle de lo que hay en un cuadro. */
function bdItemInfo(it) {
  switch (it.kind || "camera") {
    case "camera": {
      const r = bdState.byKey.get(`Camera:${it.channelId}`);
      return { icon: resIcon("camera", 14), name: r?.name ?? `Cámara #${it.channelId}`, sub: r?.source ?? "ya no está en el inventario" };
    }
    case "resource": {
      const r = bdState.byKey.get(`${it.ref}:${it.refId}`);
      const kind = RES_KINDS[it.ref];
      return { icon: resIcon(kind?.icon || "point", 14), name: r?.name ?? it.title ?? "(ya no existe)", sub: [kind?.label, r?.source].filter(Boolean).join(" · ") };
    }
    case "speaker-group": {
      const group = bdState.speakerGroups.find((g) => g.id === it.refId);
      return { icon: resIcon("speaker", 14), name: group?.name ?? it.title ?? "(grupo borrado)",
        sub: group ? `Grupo de parlantes · ${group.speakerIds.length}` : "Grupo de parlantes" };
    }
    case "card": {
      // La clave puede traer opciones de la tarjeta, elegidas en el cliente ("card-plates?devices=3,7": las
      // cámaras ANPR; "card-access?devices=3,7&system=1": los equipos de acceso y el interruptor «Eventos del
      // sistema»; "card-alerts?pending=1": el interruptor «Solo sin confirmar»).
      const [key, options = ""] = (it.ref || "").split("?");
      const card = BD_CARDS.find((c) => c.key === key);
      const devices = /(^|&)devices=([\d,]+)/.exec(options)?.[2].split(",").length ?? 0;
      const notes = [];
      if (devices) notes.push(key === "card-access" ? `${devices} equipo${devices === 1 ? "" : "s"}` : `${devices} cámara${devices === 1 ? "" : "s"}`);
      if (/(^|&)pending=1/.test(options)) notes.push("solo sin confirmar");
      if (/(^|&)system=1/.test(options)) notes.push("con eventos del sistema");
      return { icon: `<i class="mdi mdi-chart-bar" aria-hidden="true"></i>`, name: card?.label ?? it.ref,
        sub: ["Tarjeta", ...notes].join(" · ") };
    }
    default:
      return { icon: `<i class="mdi mdi-web" aria-hidden="true"></i>`, name: it.title || it.ref, sub: it.refId ? "Página de la lista" : it.ref };
  }
}

function bdDrawGrid() {
  const ed = bdState.editing, grid = $("#bd-grid");
  if (!ed || !grid) return;
  if (ed.locked) {
    grid.className = "bd-grid locked";
    grid.innerHTML = `<div class="info-box">Su división (${ed.raw.columns}×${ed.raw.rows}) es de las que solo arma el cliente de
      escritorio: aquí se cambian el nombre y con quién se comparte, y sus ${ed.items.size} cuadro(s) se conservan.</div>`;
    return;
  }
  const L = ed.layout;
  const outside = [...ed.items.keys()].filter((i) => i >= L.cells.length).length;
  grid.className = "bd-grid";
  grid.style.gridTemplateColumns = `repeat(${L.columns}, minmax(0, 1fr))`;
  grid.style.gridTemplateRows = `repeat(${L.rows}, minmax(0, 1fr))`;
  grid.innerHTML = L.cells.map((c, i) => {
    const it = ed.items.get(i), info = it ? bdItemInfo(it) : null;
    return `
      <button type="button" class="bd-cell${ed.selected === i ? " sel" : ""}${it ? " full" : ""}" data-cell="${i}"
              style="grid-column:${c.col + 1} / span ${c.colSpan};grid-row:${c.row + 1} / span ${c.rowSpan}"
              title="${it ? esc(`${info.name} — ${info.sub}`) : "Cuadro libre: elija qué muestra"}">
        <span class="bd-cell-n">${i + 1}</span>
        ${it ? `<span class="bd-cell-ico">${info.icon}</span><span class="bd-cell-name">${esc(info.name)}</span>
               <span class="bd-cell-x" data-clear="${i}" title="Dejar libre">×</span>` : `<span class="muted">Libre</span>`}
      </button>`;
  }).join("") + (outside ? `<div class="bd-outside warn-box">${outside} cuadro(s) quedan fuera de esta división y no se guardarán.</div>` : "");
  $$("#bd-grid .bd-cell").forEach((b) => b.addEventListener("click", (e) => {
    const i = Number(b.dataset.cell);
    if (e.target.closest("[data-clear]")) ed.items.delete(i);
    ed.selected = i;
    bdDrawGrid();
    bdDrawPicker();
  }));
}

/** Opciones del buscador, por grupo, con el texto ya aplicado. */
function bdPickGroups(text) {
  const t = resFold(text);
  const hit = (...parts) => !t || parts.some((p) => resFold(p).includes(t));
  const groups = BD_RESOURCE_KINDS.map((kind) => ({
    title: RES_KINDS[kind].plural,
    options: bdState.resources.filter((r) => r.kind === kind && r.enabled !== false && hit(r.name, r.source, r.detail))
      .sort((a, b) => resCollator.compare(a.name, b.name))
      .map((r) => ({ key: `${kind}:${r.id}`, icon: resIcon(RES_KINDS[kind].icon, 14), name: r.name, sub: r.source })),
  }));
  groups.push({
    title: "Grupos de parlantes",
    options: bdState.speakerGroups.filter((g) => hit(g.name, g.description, "parlantes"))
      .map((g) => ({ key: `speaker-group:${g.id}`, icon: resIcon("speaker", 14), name: g.name, sub: `${g.speakerIds.length} parlante(s)` })),
  });
  groups.push({
    title: "Tarjetas",
    options: BD_CARDS.filter((c) => hit(c.label, c.detail, "tarjeta"))
      .map((c) => ({ key: `card:${c.key}`, icon: `<i class="mdi mdi-chart-bar" aria-hidden="true"></i>`, name: c.label, sub: c.detail })),
  });
  groups.push({
    title: "Páginas web",
    options: bdState.links.filter((l) => hit(l.name, l.url, l.description, "web"))
      .map((l) => ({ key: `web:${l.id}`, icon: `<i class="mdi mdi-web" aria-hidden="true"></i>`, name: l.name, sub: l.url })),
  });
  return groups.filter((g) => g.options.length);
}

function bdDrawPicker() {
  const ed = bdState.editing, box = $("#bd-picker");
  if (!ed || !box) return;
  if (ed.locked) { box.innerHTML = ""; return; }
  if (ed.selected == null) {
    box.innerHTML = `<div class="muted bd-pick-hint">Elija un cuadro de la grilla para decidir qué muestra: una cámara,
      un recurso (puerta, alarma, cerco…), un grupo de parlantes, una tarjeta de reporte o una página web.</div>`;
    return;
  }
  const canUrl = Perms.can("dashboards.weburl");
  box.innerHTML = `
    <div class="bd-pick-head"><b>Cuadro ${ed.selected + 1}</b></div>
    <input type="search" id="bd-pick-text" placeholder="Buscar cámara, puerta, tarjeta, página…" value="${esc(ed.text)}">
    <div class="bd-pick-list" id="bd-pick-list"></div>
    ${canUrl ? `
    <div class="bd-pick-url">
      <div class="muted bd-sub">Otra dirección (no queda en la lista):</div>
      <input type="url" id="bd-url" maxlength="2048" placeholder="https://…">
      <div class="bd-pick-url-row">
        <input id="bd-url-title" maxlength="128" placeholder="Rótulo (opcional)">
        <button class="btn small" type="button" id="bd-url-add">Poner</button>
      </div>
    </div>` : ""}`;
  const draw = () => {
    const groups = bdPickGroups(ed.text);
    $("#bd-pick-list").innerHTML = groups.length ? groups.map((g) => `
      <div class="bd-pick-group">${esc(g.title)}</div>
      ${g.options.slice(0, BD_PICK_LIMIT).map((o) => `
        <button type="button" class="bd-opt" data-opt="${esc(o.key)}" title="${esc(o.sub || "")}">
          ${o.icon}<span class="bd-opt-name">${esc(o.name)}</span><span class="muted bd-opt-sub">${esc(o.sub || "")}</span>
        </button>`).join("")}
      ${g.options.length > BD_PICK_LIMIT ? `<div class="muted bd-sub">… y ${g.options.length - BD_PICK_LIMIT} más: afine la búsqueda.</div>` : ""}`).join("")
      : `<div class="muted">Nada coincide con la búsqueda.</div>`;
    $$("#bd-pick-list .bd-opt").forEach((b) => b.addEventListener("click", () => bdAssign(b.dataset.opt)));
  };
  $("#bd-pick-text").addEventListener("input", (e) => { ed.text = e.target.value; draw(); });
  $("#bd-url-add")?.addEventListener("click", () => {
    const url = $("#bd-url").value.trim();
    if (!/^https?:\/\/\S+$/i.test(url)) { toast("Escriba una dirección completa (https://…).", true); return; }
    let title = $("#bd-url-title").value.trim();
    if (!title) { try { title = new URL(url).host; } catch { title = url; } }
    bdPut({ kind: "web", ref: url, title });
  });
  draw();
  $("#bd-pick-text").focus();
}

function bdAssign(key) {
  const [type, id] = [key.slice(0, key.indexOf(":")), key.slice(key.indexOf(":") + 1)];
  const ed = bdState.editing;
  if (type === "Camera") {
    // Con muchos cuadros, el secundario (menos ancho de banda y decodificación).
    bdPut({ kind: "camera", channelId: Number(id), streamType: ed.layout.cells.length <= 4 ? 0 : 1 });
  } else if (type === "card") {
    bdPut({ kind: "card", ref: id });
  } else if (type === "speaker-group") {
    const group = bdState.speakerGroups.find((g) => g.id === Number(id));
    bdPut({ kind: "speaker-group", refId: Number(id), title: group?.name });
  } else if (type === "web") {
    const link = bdState.links.find((l) => l.id === Number(id));
    bdPut({ kind: "web", refId: Number(id), title: link?.name });
  } else {
    const r = bdState.byKey.get(key);
    bdPut({ kind: "resource", ref: type, refId: Number(id), title: r?.name });
  }
}

/** Pone algo en el cuadro elegido y pasa al siguiente libre (armar un tablero es ir llenando cuadros). */
function bdPut(item) {
  const ed = bdState.editing;
  ed.items.set(ed.selected, { cellIndex: ed.selected, channelId: 0, streamType: 0, ...item });
  const next = ed.layout.cells.findIndex((_, i) => i > ed.selected && !ed.items.has(i));
  ed.selected = next >= 0 ? next : ed.selected;
  ed.text = "";
  bdDrawGrid();
  bdDrawPicker();
}

async function bdSave() {
  const ed = bdState.editing;
  const name = $("#bd-name").value.trim();
  const error = (text) => { $("#bd-error").innerHTML = `<div class="error-box">${esc(text)}</div>`; };
  if (!name) { error("Escriba un nombre para el tablero."); return; }
  const byRoles = document.querySelector('input[name="bd-aud"]:checked')?.value === "roles";
  const roleIds = byRoles ? [...$$("#bd-roles input:checked")].map((cb) => Number(cb.value)) : [];
  if (byRoles && !roleIds.length) { error("Marque al menos un rol, o elija «Todos los usuarios»."); return; }
  const items = [...ed.items.values()].filter((i) => ed.locked || i.cellIndex < ed.layout.cells.length);
  const body = {
    name, shared: true, roleIds, items,
    layoutName: ed.locked ? ed.raw.layoutName : ed.layout.key,
    columns: ed.locked ? ed.raw.columns : ed.layout.columns,
    rows: ed.locked ? ed.raw.rows : ed.layout.rows,
  };
  try {
    if (ed.id) await Api.put(`/api/live-views/${ed.id}`, body);
    else await Api.post("/api/live-views", body);
    closeModal();
    toast(`Tablero "${name}" guardado.`);
    await bdLoad(true);
  } catch (err) { error(err.error); }
}
