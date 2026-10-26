// CLR TrueCentral VMS — panel: control de rondas.
//
// Los guardias recorren la instalación con un bastón lector y lo apoyan en
// cada punto de control (un tag). La base de descarga (un PC de la guardia
// con el agente truecentral-patrol) vacía el bastón cuando vuelve y sube las
// lecturas; el servidor las arma en pasos de la bitácora (punto + guardia).
//
// Pestañas: bitácora, guardias, llaveros, puntos de control y bastones/bases.
// La lectura desde el navegador (Web Serial) está en patrol-serial.js.
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

const PATROL_TABS = [
  { key: "log", label: "Bitácora", icon: "mdi-format-list-bulleted" },
  { key: "guards", label: "Guardias", icon: "mdi-account-multiple" },
  { key: "tags", label: "Llaveros", icon: "mdi-key-variant" },
  { key: "checkpoints", label: "Puntos de control", icon: "mdi-map-marker-radius" },
  { key: "readers", label: "Bastones y bases", icon: "mdi-nfc-variant" },
];

/** Qué pestañas se recargan con cada aviso PatrolChanged del hub. */
const PATROL_TOPIC_TABS = {
  passes: ["log"],
  guards: ["guards", "tags", "log"],
  checkpoints: ["checkpoints", "log"],
  readers: ["readers"],
  agents: ["readers"],
  downloads: ["readers", "checkpoints", "log", "guards", "tags"],
};

/** Bloques de memoria del bastón (Captor 4): para mostrar el uso en %. */
const PATROL_MEMORY_BLOCKS = 12000;

const patrolState = {
  tab: "log",
  log: null,          // filtros de la bitácora (se inicializan la primera vez)
};

let patrolHubBound = null;
let patrolReloadTimer = null;

function patrolDetachHub() {
  clearTimeout(patrolReloadTimer);
  if (!patrolHubBound) return;
  VmsHub.off("PatrolChanged", patrolHubBound);
  patrolHubBound = null;
}

// ---------------------------------------------------------------------------
// Utilitarios

/** Fecha → "yyyy-MM-ddTHH:mm:ss" en hora LOCAL (para inputs y filtros de la API). */
function patrolLocalText(d) {
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

/** Valor de un datetime-local con segundos (el navegador los omite cuando son :00). */
function patrolInputValue(value) {
  if (!value) return "";
  return value.length === 16 ? value + ":00" : value.slice(0, 19);
}

/** Fecha de la API (UTC) en hora local. "0001-01-01…" (DateTime vacío del servidor) cuenta como sin fecha. */
function patrolWhen(iso) { return iso && !String(iso).startsWith("0001-") ? formatDateTime(iso) : "—"; }

function patrolModalOpen() {
  return !$("#modal-backdrop").classList.contains("hidden");
}

function patrolErrorBox(err) {
  return `<div class="error-box">${esc(err?.error ?? err?.message ?? String(err))}</div>`;
}

/** Guarda una respuesta de la API como archivo (con el token en la cabecera, no en la URL). */
async function patrolDownload(path, fallbackName) {
  const res = await fetch(path, { headers: { Authorization: `Bearer ${Api.token}` } });
  if (!res.ok) {
    const data = await res.json().catch(() => null);
    throw { error: data?.error || `Error ${res.status}` };
  }
  const disposition = res.headers.get("Content-Disposition") || "";
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  const blob = await res.blob();
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = match ? decodeURIComponent(match[1]) : fallbackName;
  document.body.appendChild(a);
  a.click();
  setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
}

/** Modal con un motivo obligatorio. Devuelve una promesa con el texto o null si se canceló. */
function patrolReasonModal({ title, intro, label = "Motivo", confirmText, danger, extraHtml = "", onSubmit }) {
  openModal(`
    <h3>${esc(title)}</h3>
    ${intro ? `<div class="${danger ? "warn-box" : "info-box"}">${intro}</div>` : ""}
    <div id="pt-reason-error"></div>
    <form id="pt-reason-form">
      ${extraHtml}
      <div class="field">
        <label for="pt-reason">${esc(label)}</label>
        <textarea id="pt-reason" rows="3" maxlength="512" required placeholder="Obligatorio: queda en la auditoría"></textarea>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="pt-reason-cancel">Cancelar</button>
        <button class="btn${danger ? " danger" : ""}" type="submit" id="pt-reason-ok">${esc(confirmText)}</button>
      </div>
    </form>`);
  $("#pt-reason-cancel").addEventListener("click", closeModal);
  $("#pt-reason").focus();
  $("#pt-reason-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const reason = $("#pt-reason").value.trim();
    if (!reason) { $("#pt-reason-error").innerHTML = `<div class="error-box">Indique el motivo.</div>`; return; }
    const button = $("#pt-reason-ok");
    button.disabled = true;
    try {
      await onSubmit(reason);
      closeModal();
    } catch (err) {
      $("#pt-reason-error").innerHTML = patrolErrorBox(err);
      button.disabled = false;
    }
  });
}

// ---------------------------------------------------------------------------
// Página

async function renderPatrol() {
  patrolDetachHub();
  $("#page-title").textContent = "Control de rondas";
  const query = new URLSearchParams((location.hash.split("?")[1]) || "");
  const asked = query.get("tab");
  if (asked && PATROL_TABS.some((t) => t.key === asked)) patrolState.tab = asked;

  const canRead = Perms.can("patrol.read");
  const hasSerial = "serial" in navigator;
  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        ${canRead && hasSerial ? `<button class="btn ghost" id="btn-patrol-serial"
          title="Conecte el bastón por USB a este equipo y léalo desde el navegador"><i class="mdi mdi-usb" aria-hidden="true"></i> Leer bastón desde este PC</button>` : ""}
      </div>
    </div>
    <div class="res-tabs" role="tablist">
      ${PATROL_TABS.map((t) => `
        <button type="button" role="tab" class="res-tab${t.key === patrolState.tab ? " on" : ""}" data-tab="${t.key}">
          <i class="mdi ${t.icon}" aria-hidden="true"></i>${esc(t.label)}</button>`).join("")}
    </div>
    <div id="patrol-body" class="res-tab-body"></div>`;

  $("#btn-patrol-serial")?.addEventListener("click", () => patrolSerialModal());
  $$("#view .res-tab").forEach((b) => b.addEventListener("click", () => {
    patrolState.tab = b.dataset.tab;
    $$("#view .res-tab").forEach((x) => x.classList.toggle("on", x === b));
    history.replaceState(null, "", `#/patrol?tab=${patrolState.tab}`);
    patrolRenderTab();
  }));

  patrolHubBound = (topic) => {
    if (!location.hash.startsWith("#/patrol")) return;
    const tabs = PATROL_TOPIC_TABS[String(topic)] ?? PATROL_TABS.map((t) => t.key);
    if (tabs.includes(patrolState.tab)) patrolScheduleReload();
  };
  VmsHub.on("PatrolChanged", patrolHubBound);

  await patrolRenderTab();
}

/** Recarga la pestaña tras un aviso del hub (agrupa ráfagas; espera si hay un formulario abierto). */
function patrolScheduleReload() {
  clearTimeout(patrolReloadTimer);
  patrolReloadTimer = setTimeout(() => {
    if (!location.hash.startsWith("#/patrol") || !$("#patrol-body")) return;
    if (patrolModalOpen()) { patrolScheduleReload(); return; }
    patrolRenderTab(true);
  }, patrolModalOpen() ? 2000 : 400);
}

async function patrolRenderTab(quiet) {
  const body = $("#patrol-body");
  if (!body) return;
  if (!quiet) body.innerHTML = `<div class="info-box">Cargando…</div>`;
  switch (patrolState.tab) {
    case "guards": return patrolRenderGuards(body);
    case "tags": return patrolRenderTags(body);
    case "checkpoints": return patrolRenderCheckpoints(body);
    case "readers": return patrolRenderReaders(body);
    default: return patrolRenderLog(body, quiet);
  }
}

// ---------------------------------------------------------------------------
// 1. Bitácora

async function patrolRenderLog(body, quiet) {
  if (!patrolState.log) {
    const now = new Date();
    patrolState.log = {
      from: patrolLocalText(new Date(now.getTime() - 24 * 3600 * 1000)),
      to: patrolLocalText(new Date(now.getTime() + 60 * 1000)),
      guardId: "", checkpointId: "", includeVoided: true, page: 1,
    };
  }
  const s = patrolState.log;
  let guards, checkpoints;
  try {
    [guards, checkpoints] = await Promise.all([Api.get("/api/patrol/guards"), Api.get("/api/patrol/checkpoints")]);
  } catch (err) { body.innerHTML = patrolErrorBox(err); return; }

  // Una recarga silenciosa (hub) no rehace los filtros: solo la tabla.
  if (quiet && $("#pl-results")) { await patrolLoadPasses(guards); return; }

  body.innerHTML = `
    <div class="filter-bar">
      <div class="field"><label for="pl-from">Desde</label>
        <input id="pl-from" type="datetime-local" step="1" value="${esc(s.from)}"></div>
      <div class="field"><label for="pl-to">Hasta</label>
        <input id="pl-to" type="datetime-local" step="1" value="${esc(s.to)}"></div>
      <div class="field"><label for="pl-guard">Guardia</label>
        <select id="pl-guard"><option value="">Todos</option>
          ${guards.map((g) => `<option value="${g.id}" ${String(s.guardId) === String(g.id) ? "selected" : ""}>${esc(g.fullName)}${g.active ? "" : " (desvinculado)"}</option>`).join("")}
        </select></div>
      <div class="field"><label for="pl-checkpoint">Punto</label>
        <select id="pl-checkpoint"><option value="">Todos</option>
          ${checkpoints.map((c) => `<option value="${c.id}" ${String(s.checkpointId) === String(c.id) ? "selected" : ""}>${esc(c.name)}</option>`).join("")}
        </select></div>
      <div class="field"><label class="checkbox-row" style="margin-bottom:9px"><input type="checkbox" id="pl-voided" ${s.includeVoided ? "checked" : ""}> Mostrar anulados</label></div>
      <div class="filter-actions">
        <button class="btn" id="pl-search">Buscar</button>
        <button class="btn ghost" id="pl-reset" title="Últimas 24 horas, todos los guardias y puntos">Limpiar</button>
        ${Perms.can("patrol.export") ? `<button class="btn ghost" id="pl-export" title="Descarga los pasos de los filtros (incluye anulados) en CSV para Excel"><i class="mdi mdi-file-delimited" aria-hidden="true"></i> Exportar CSV</button>` : ""}
      </div>
    </div>
    <div id="pl-results"><div class="info-box">Cargando…</div></div>`;

  const readFilters = () => {
    s.from = patrolInputValue($("#pl-from").value);
    s.to = patrolInputValue($("#pl-to").value);
    s.guardId = $("#pl-guard").value;
    s.checkpointId = $("#pl-checkpoint").value;
    s.includeVoided = $("#pl-voided").checked;
    s.page = 1;
  };
  $("#pl-search").addEventListener("click", () => { readFilters(); patrolLoadPasses(guards); });
  $("#pl-voided").addEventListener("change", () => { readFilters(); patrolLoadPasses(guards); });
  $("#pl-reset").addEventListener("click", () => { patrolState.log = null; patrolRenderLog(body); });
  $("#pl-export")?.addEventListener("click", async (e) => {
    readFilters();
    const button = e.currentTarget;
    button.disabled = true;
    try {
      await patrolDownload(`/api/patrol/passes/export?${patrolLogQuery(false)}`, "rondas.csv");
      toast("Bitácora exportada.");
    } catch (err) { toast(err.error, true); }
    finally { button.disabled = false; }
  });

  await patrolLoadPasses(guards);
}

function patrolLogQuery(withPage) {
  const s = patrolState.log;
  const q = new URLSearchParams();
  if (s.from) q.set("from", s.from);
  if (s.to) q.set("to", s.to);
  if (s.guardId) q.set("guardId", s.guardId);
  if (s.checkpointId) q.set("checkpointId", s.checkpointId);
  if (withPage) {
    if (s.includeVoided) q.set("includeVoided", "true");
    q.set("page", String(s.page));
  }
  return q.toString();
}

function patrolPassState(p) {
  const parts = [];
  if (p.voided) {
    parts.push(`<span class="tag off" title="${esc(p.voidReason ?? "")}">Anulado</span>
      <div class="muted pt-note">${esc(p.voidReason ?? "")}${p.editedBy ? ` · ${esc(p.editedBy)}` : ""}</div>`);
  } else if (p.editedBy) {
    parts.push(`<span class="tag warn" title="${esc(`${p.editReason ?? ""} (${patrolWhen(p.editedAt)})`)}">Corregido</span>
      <div class="muted pt-note">por ${esc(p.editedBy)}: ${esc(p.editReason ?? "")}</div>`);
  } else {
    parts.push(`<span class="tag on">Válido</span>`);
  }
  if (p.source === "Manual") parts.push(`<div class="muted pt-note">Ingreso manual</div>`);
  if (p.source === "Access") parts.push(`<div class="muted pt-note">Terminal de acceso</div>`);
  return parts.join("");
}

async function patrolLoadPasses(guards) {
  const box = $("#pl-results");
  if (!box) return;
  const s = patrolState.log;
  let data;
  try { data = await Api.get(`/api/patrol/passes?${patrolLogQuery(true)}`); }
  catch (err) { box.innerHTML = patrolErrorBox(err); return; }
  const totalPages = Math.max(1, Math.ceil(data.total / 100));
  if (s.page > totalPages) { s.page = totalPages; return patrolLoadPasses(guards); }
  const canEdit = Perms.can("patrol.edit");
  if (data.items.length === 0) {
    box.innerHTML = `<div class="info-box">No hay pasos en ese período con esos filtros. Los pasos aparecen cuando una base
      descarga un bastón (o al leerlo desde este PC).</div>`;
    return;
  }
  box.innerHTML = `
    <div class="table-scroll"><table class="grid pt-grid">
      <thead><tr>
        <th>Fecha y hora</th><th>Punto</th><th>Ubicación</th><th>Guardia</th><th>Llavero</th><th>Bastón</th><th>Estado</th>
        ${canEdit ? "<th></th>" : ""}
      </tr></thead>
      <tbody>
        ${data.items.map((p) => `
          <tr data-id="${p.id}" class="${p.voided ? "pt-voided" : ""}">
            <td style="white-space:nowrap">${esc(patrolWhen(p.readAt))}</td>
            <td>${esc(p.checkpointName)}</td>
            <td class="muted">${esc(p.location ?? "—")}</td>
            <td>${p.guardName
              ? `${esc(p.guardName)}${p.guardEmployeeNo ? `<div class="muted pt-note">${esc(p.guardEmployeeNo)}</div>` : ""}`
              : `<span class="tag operator">Sin guardia</span>`}</td>
            <td class="muted pt-mono">${esc(p.guardTagId ?? "—")}</td>
            <td class="muted">${esc(p.readerName ?? "—")}</td>
            <td>${patrolPassState(p)}</td>
            ${canEdit ? `<td><div class="row-actions">
              ${p.voided ? "" : `<button class="btn ghost small pl-fix">Corregir guardia</button>
              <button class="btn danger small pl-void">Anular</button>`}
            </div></td>` : ""}
          </tr>`).join("")}
      </tbody>
    </table></div>
    <div class="audit-pager">
      <span class="muted">${data.total.toLocaleString("es-CL")} paso${data.total === 1 ? "" : "s"} · página ${s.page} de ${totalPages}</span>
      <div style="display:flex;gap:8px">
        <button class="btn ghost" id="pl-prev" ${s.page <= 1 ? "disabled" : ""}>« Anterior</button>
        <button class="btn ghost" id="pl-next" ${s.page >= totalPages ? "disabled" : ""}>Siguiente »</button>
      </div>
    </div>`;

  const byRow = (e) => data.items.find((p) => p.id === Number(e.target.closest("tr").dataset.id));
  $("#pl-prev")?.addEventListener("click", () => { s.page--; patrolLoadPasses(guards); });
  $("#pl-next")?.addEventListener("click", () => { s.page++; patrolLoadPasses(guards); });
  $$("#pl-results .pl-fix").forEach((b) => b.addEventListener("click", (e) => patrolFixPassModal(byRow(e), guards)));
  $$("#pl-results .pl-void").forEach((b) => b.addEventListener("click", (e) => {
    const p = byRow(e);
    patrolReasonModal({
      title: "Anular paso",
      intro: `El paso de <b>${esc(p.checkpointName)}</b> del ${esc(patrolWhen(p.readAt))} deja de contar en la bitácora.
        No se borra: queda visible como anulado, con el motivo y quién lo anuló.`,
      confirmText: "Anular", danger: true,
      onSubmit: async (reason) => {
        await Api.post(`/api/patrol/passes/${p.id}/void`, { reason });
        toast("Paso anulado.");
        patrolLoadPasses(guards);
      },
    });
  }));
}

function patrolFixPassModal(p, guards) {
  patrolReasonModal({
    title: "Corregir guardia",
    intro: `Paso de <b>${esc(p.checkpointName)}</b> (${esc(patrolWhen(p.readAt))}), registrado a nombre de
      <b>${esc(p.guardName ?? "nadie (sin guardia)")}</b>. La corrección queda en la bitácora con su nombre y el motivo.`,
    confirmText: "Guardar corrección",
    extraHtml: `
      <div class="field">
        <label for="pt-guard">Guardia correcto</label>
        <select id="pt-guard">
          <option value="">Sin guardia</option>
          ${guards.map((g) => `<option value="${g.id}" ${g.id === p.guardId ? "selected" : ""}>${esc(g.fullName)} · ${esc(g.employeeNo)}${g.active ? "" : " (desvinculado)"}</option>`).join("")}
        </select>
      </div>`,
    onSubmit: async (reason) => {
      const value = $("#pt-guard").value;
      await Api.put(`/api/patrol/passes/${p.id}`, { guardId: value ? Number(value) : null, reason });
      toast("Paso corregido.");
      patrolLoadPasses(guards);
    },
  });
}

// ---------------------------------------------------------------------------
// 2. Guardias

async function patrolRenderGuards(body) {
  let guards, tags;
  try { [guards, tags] = await Promise.all([Api.get("/api/patrol/guards"), Api.get("/api/patrol/tags")]); }
  catch (err) { body.innerHTML = patrolErrorBox(err); return; }
  const canConfigure = Perms.can("patrol.configure");
  const canReactivate = canConfigure && Perms.can("persons.manage");
  const canOpenPerson = Perms.can("persons.view") && Perms.licensed("module_access");

  body.innerHTML = `
    ${canConfigure ? `<div class="row-actions pt-tab-actions"><button class="btn" id="pg-new">Agregar guardia</button></div>` : ""}
    ${guards.length === 0 ? `<div class="info-box">Todavía no hay guardias. ${canConfigure
      ? "Agréguelos con <b>Agregar guardia</b> y asígneles su llavero: así cada paso de la bitácora queda con el nombre de quien hizo la ronda."
      : "Un administrador debe agregarlos."}</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Guardia</th><th>RUT / N°</th><th>Llaveros</th><th>Último paso</th><th>Estado</th><th></th></tr></thead>
      <tbody>
        ${guards.map((g) => `
          <tr data-id="${g.id}">
            <td>${esc(g.fullName)}${g.notes ? `<div class="muted pt-note">${esc(g.notes)}</div>` : ""}</td>
            <td class="muted">${esc(g.employeeNo || "—")}</td>
            <td>${g.tags.length ? g.tags.map((t) => `<span class="tag operator pt-mono" title="${esc(t.label ?? "")}">${esc(t.tagId)}</span>`).join(" ") : `<span class="muted">—</span>`}</td>
            <td class="muted" style="white-space:nowrap">${esc(patrolWhen(g.lastPassAt))}</td>
            <td>${g.active
              ? `<span class="tag on">Activo</span>`
              : `<span class="tag off">Desvinculado</span>
                 <div class="muted pt-note">${esc(patrolWhen(g.deactivatedAt))}${g.deactivatedBy ? ` · ${esc(g.deactivatedBy)}` : ""}</div>
                 ${g.deactivationReason ? `<div class="muted pt-note">${esc(g.deactivationReason)}</div>` : ""}`}</td>
            <td><div class="row-actions" style="flex-wrap:wrap">
              ${canOpenPerson ? `<button class="btn ghost small pg-person" title="Abrir su ficha en Control de acceso → Personas">Ficha</button>` : ""}
              ${canConfigure && g.active ? `<button class="btn ghost small pg-edit">Llaveros y notas</button>
                <button class="btn danger small pg-deactivate">Desvincular</button>` : ""}
              ${canConfigure && !g.active ? `<button class="btn ghost small pg-reactivate" ${canReactivate ? "" : "disabled title=\"Reincorporar reactiva a la persona en el control de acceso: requiere además el permiso de administrar personas.\""}>Reincorporar</button>` : ""}
            </div></td>
          </tr>`).join("")}
      </tbody>
    </table></div>`}`;

  const byRow = (e) => guards.find((g) => g.id === Number(e.target.closest("tr").dataset.id));
  $("#pg-new")?.addEventListener("click", () => patrolGuardModal());
  $$("#patrol-body .pg-person").forEach((b) => b.addEventListener("click", (e) => {
    const g = byRow(e);
    // Personas no abre una ficha por dirección: se llega a la lista filtrada por su RUT / N°.
    Object.assign(accessPersonState, { page: 1, q: g.employeeNo || g.fullName, department: "", levelId: "", state: "" });
    location.hash = "#/access-persons";
  }));
  $$("#patrol-body .pg-edit").forEach((b) => b.addEventListener("click", (e) => patrolGuardTagsModal(byRow(e), tags)));
  $$("#patrol-body .pg-deactivate").forEach((b) => b.addEventListener("click", (e) => {
    const g = byRow(e);
    patrolReasonModal({
      title: "Desvincular guardia",
      intro: `<b>${esc(g.fullName)}</b> queda desactivado en <b>todo el sistema</b>: la persona se desactiva en el control
        de acceso y sale de los terminales (deja de poder entrar), y se liberan sus llaveros
        ${g.tags.length ? `(${g.tags.map((t) => esc(t.tagId)).join(", ")})` : ""} para entregárselos a otro guardia.
        Su historial de rondas se conserva. Se puede reincorporar más adelante.`,
      confirmText: "Desvincular", danger: true,
      onSubmit: async (reason) => {
        await Api.post(`/api/patrol/guards/${g.id}/deactivate`, { reason });
        toast(`${g.fullName} quedó desvinculado.`);
        patrolRenderTab(true);
      },
    });
  }));
  $$("#patrol-body .pg-reactivate").forEach((b) => b.addEventListener("click", async (e) => {
    const g = byRow(e);
    if (!confirm(`¿Reincorporar a ${g.fullName}? La persona se vuelve a activar en el control de acceso (vuelve a los terminales con los niveles que tenga). Después asígnele un llavero.`)) return;
    try {
      await Api.post(`/api/patrol/guards/${g.id}/reactivate`);
      toast(`${g.fullName} quedó reincorporado.`);
      patrolRenderTab(true);
    } catch (err) { toast(err.error, true); }
  }));
}

function patrolGuardModal() {
  openModal(`
    <h3>Agregar guardia</h3>
    <div id="pg-error"></div>
    <form id="pg-form">
      <div class="form-grid">
        <div class="field"><label for="pg-first">Nombre</label>
          <input id="pg-first" required maxlength="64" placeholder="Juan"></div>
        <div class="field"><label for="pg-last">Apellido</label>
          <input id="pg-last" maxlength="64" placeholder="Pérez"></div>
      </div>
      <div class="field"><label for="pg-employee">RUT o N° de empleado (opcional)</label>
        <input id="pg-employee" maxlength="32" placeholder="12.345.678-9">
        <div class="muted field-hint">Si ese RUT o número ya está en el padrón de personas (Control de acceso), se usa esa
          misma persona y no se crea otra. Si no, se crea sin niveles de acceso, así que no se envía a los terminales.
          Sin RUT se le asigna un número correlativo.</div></div>
      <div class="field"><label for="pg-notes">Notas</label>
        <textarea id="pg-notes" rows="2" maxlength="1024" placeholder="Turno, empresa contratista…"></textarea></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="pg-cancel">Cancelar</button>
        <button class="btn" type="submit" id="pg-save">Guardar</button>
      </div>
    </form>`);
  $("#pg-cancel").addEventListener("click", closeModal);
  $("#pg-first").focus();
  $("#pg-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = $("#pg-save");
    button.disabled = true;
    try {
      const g = await Api.post("/api/patrol/guards", {
        accessPersonId: null,
        firstName: $("#pg-first").value.trim(),
        lastName: $("#pg-last").value.trim(),
        employeeNo: $("#pg-employee").value.trim() || null,
        notes: $("#pg-notes").value.trim() || null,
      });
      closeModal();
      toast(`${g.fullName} agregado como guardia. Asígnele su llavero.`);
      patrolRenderTab(true);
    } catch (err) {
      $("#pg-error").innerHTML = patrolErrorBox(err);
      button.disabled = false;
    }
  });
}

/** Llaveros (los suyos y los libres) y notas de un guardia. */
function patrolGuardTagsModal(g, tags) {
  const mine = new Set(g.tags.map((t) => t.id));
  const choices = tags.filter((t) => mine.has(t.id) || t.guardId == null);
  openModal(`
    <h3>${esc(g.fullName)}</h3>
    <div id="pg-error"></div>
    <form id="pg-form">
      <div class="field"><label>Llaveros</label>
        ${choices.length === 0 ? `<div class="muted">No hay llaveros libres. Agréguelos en la pestaña <b>Llaveros</b>
          (o desde un tag leído sin dar de alta, en <b>Puntos de control</b>).</div>` :
          choices.map((t) => `<label class="checkbox-row" style="margin-bottom:6px">
            <input type="checkbox" value="${t.id}" ${mine.has(t.id) ? "checked" : ""}>
            <span class="pt-mono">${esc(t.tagId)}</span>${t.label ? ` <span class="muted">· ${esc(t.label)}</span>` : ""}
            ${t.enabled ? "" : ` <span class="tag operator">deshabilitado</span>`}</label>`).join("")}
        <div class="muted field-hint">Se muestran sus llaveros y los que no tienen guardia. Los pasos ya registrados no cambian.</div>
      </div>
      <div class="field"><label for="pg-notes">Notas</label>
        <textarea id="pg-notes" rows="2" maxlength="1024">${esc(g.notes ?? "")}</textarea></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="pg-cancel">Cancelar</button>
        <button class="btn" type="submit" id="pg-save">Guardar cambios</button>
      </div>
    </form>`);
  $("#pg-cancel").addEventListener("click", closeModal);
  $("#pg-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = $("#pg-save");
    button.disabled = true;
    try {
      await Api.put(`/api/patrol/guards/${g.id}`, {
        notes: $("#pg-notes").value.trim() || null,
        tagIds: $$("#pg-form input[type=checkbox]:checked").map((c) => Number(c.value)),
      });
      closeModal();
      toast("Guardia actualizado.");
      patrolRenderTab(true);
    } catch (err) {
      $("#pg-error").innerHTML = patrolErrorBox(err);
      button.disabled = false;
    }
  });
}

// ---------------------------------------------------------------------------
// 3. Llaveros

async function patrolRenderTags(body) {
  let tags;
  try { tags = await Api.get("/api/patrol/tags"); }
  catch (err) { body.innerHTML = patrolErrorBox(err); return; }
  const canConfigure = Perms.can("patrol.configure");
  body.innerHTML = `
    <p class="muted res-tab-intro">El llavero es el tag que lleva cada guardia: lo lee con el bastón al empezar la ronda y
      así los pasos que siguen quedan a su nombre.</p>
    ${canConfigure ? `<div class="row-actions pt-tab-actions"><button class="btn" id="pk-new">Agregar llavero</button></div>` : ""}
    ${tags.length === 0 ? `<div class="info-box">No hay llaveros dados de alta.</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Tag</th><th>Etiqueta</th><th>Guardia</th><th>Estado</th><th>Último uso</th>${canConfigure ? "<th></th>" : ""}</tr></thead>
      <tbody>
        ${tags.map((t) => `
          <tr data-id="${t.id}">
            <td class="pt-mono">${esc(t.tagId)}</td>
            <td>${esc(t.label ?? "—")}</td>
            <td>${t.guardName ? esc(t.guardName) : `<span class="muted">Libre</span>`}</td>
            <td>${t.enabled ? `<span class="tag on">Habilitado</span>` : `<span class="tag operator">Deshabilitado</span>`}</td>
            <td class="muted" style="white-space:nowrap">${esc(patrolWhen(t.lastReadAt))}</td>
            ${canConfigure ? `<td><div class="row-actions">
              <button class="btn ghost small pk-edit">Editar</button>
              <button class="btn danger small pk-delete">Eliminar</button>
            </div></td>` : ""}
          </tr>`).join("")}
      </tbody>
    </table></div>`}`;
  const byRow = (e) => tags.find((t) => t.id === Number(e.target.closest("tr").dataset.id));
  $("#pk-new")?.addEventListener("click", () => patrolTagModal(null));
  $$("#patrol-body .pk-edit").forEach((b) => b.addEventListener("click", (e) => patrolTagModal(byRow(e))));
  $$("#patrol-body .pk-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const t = byRow(e);
    if (!confirm(`¿Eliminar el llavero ${t.tagId}? Los pasos ya registrados conservan el nombre del guardia; las lecturas nuevas de este tag quedarán sin dar de alta.`)) return;
    try {
      await Api.delete(`/api/patrol/tags/${t.id}`);
      toast("Llavero eliminado.");
      patrolRenderTab(true);
    } catch (err) { toast(err.error, true); }
  }));
}

/** Alta o edición de un llavero. `prefillTag` = tag leído sin dar de alta. */
async function patrolTagModal(tag, prefillTag) {
  let guards;
  try { guards = await Api.get("/api/patrol/guards"); } catch (err) { toast(err.error, true); return; }
  const isNew = !tag;
  const active = guards.filter((g) => g.active);
  openModal(`
    <h3>${isNew ? "Agregar llavero" : `Llavero ${esc(tag.tagId)}`}</h3>
    <div id="pk-error"></div>
    <form id="pk-form">
      <div class="field"><label for="pk-tag">Tag</label>
        <input id="pk-tag" class="pt-mono" required maxlength="10" pattern="[0-9A-Fa-f]{10}" placeholder="0D00D6712D"
          value="${esc(tag?.tagId ?? prefillTag ?? "")}" ${isNew ? "" : "disabled"}>
        <div class="muted field-hint">${isNew ? "10 caracteres hexadecimales, tal como los lee el bastón." : "El tag no se cambia: elimine el llavero y agregue otro."}</div></div>
      <div class="field"><label for="pk-label">Etiqueta</label>
        <input id="pk-label" maxlength="128" placeholder="Llavero 1" value="${esc(tag?.label ?? "")}"></div>
      <div class="field"><label for="pk-guard">Guardia</label>
        <select id="pk-guard"><option value="">Libre (sin guardia)</option>
          ${active.map((g) => `<option value="${g.id}" ${g.id === tag?.guardId ? "selected" : ""}>${esc(g.fullName)} · ${esc(g.employeeNo)}</option>`).join("")}
        </select></div>
      <label class="checkbox-row"><input type="checkbox" id="pk-enabled" ${tag ? (tag.enabled ? "checked" : "") : "checked"}> Habilitado</label>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="pk-cancel">Cancelar</button>
        <button class="btn" type="submit" id="pk-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);
  $("#pk-cancel").addEventListener("click", closeModal);
  $("#pk-tag").addEventListener("input", (e) => { e.target.value = e.target.value.toUpperCase().replace(/[^0-9A-F]/g, ""); });
  $("#pk-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = $("#pk-save");
    button.disabled = true;
    const guard = $("#pk-guard").value;
    const body = {
      tagId: isNew ? $("#pk-tag").value.trim().toUpperCase() : tag.tagId,
      label: $("#pk-label").value.trim() || null,
      guardId: guard ? Number(guard) : null,
      enabled: $("#pk-enabled").checked,
    };
    try {
      if (isNew) await Api.post("/api/patrol/tags", body);
      else await Api.put(`/api/patrol/tags/${tag.id}`, body);
      closeModal();
      toast(isNew ? "Llavero agregado." : "Llavero actualizado.");
      patrolRenderTab(true);
    } catch (err) {
      $("#pk-error").innerHTML = patrolErrorBox(err);
      button.disabled = false;
    }
  });
}

// ---------------------------------------------------------------------------
// 4. Puntos de control

async function patrolRenderCheckpoints(body) {
  const canConfigure = Perms.can("patrol.configure");
  let checkpoints, unknown = [];
  try {
    [checkpoints, unknown] = await Promise.all([
      Api.get("/api/patrol/checkpoints"),
      canConfigure ? Api.get("/api/patrol/unknown-tags") : Promise.resolve([]),
    ]);
  } catch (err) { body.innerHTML = patrolErrorBox(err); return; }

  body.innerHTML = `
    ${canConfigure ? `<div class="row-actions pt-tab-actions"><button class="btn" id="pc-new">Agregar punto</button></div>` : ""}
    ${checkpoints.length === 0 ? `<div class="info-box">No hay puntos de control. ${canConfigure
      ? "Agréguelos con su tag o, más fácil: pase el bastón por cada punto, descárguelo y délos de alta desde <b>Tags leídos sin dar de alta</b>."
      : ""}</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Punto</th><th>Tag</th><th>Ubicación</th><th>Estado</th><th>Último paso</th>${canConfigure ? "<th></th>" : ""}</tr></thead>
      <tbody>
        ${checkpoints.map((c) => `
          <tr data-id="${c.id}">
            <td>${esc(c.name)}${c.notes ? `<div class="muted pt-note">${esc(c.notes)}</div>` : ""}</td>
            <td class="pt-mono">${c.kind === "Tag" ? esc(c.tagId ?? "—") : `<span class="muted">Terminal de acceso</span>`}</td>
            <td>${locationLineHtml(c.location)}</td>
            <td>${c.disabledByLicense ? licenseWaitTag(c).trim()
              : c.enabled ? `<span class="tag on">Habilitado</span>` : `<span class="tag operator">Deshabilitado</span>`}</td>
            <td class="muted" style="white-space:nowrap">${esc(patrolWhen(c.lastPassAt))}</td>
            ${canConfigure ? `<td><div class="row-actions">
              <button class="btn ghost small pc-edit">Editar</button>
              <button class="btn danger small pc-delete">Eliminar</button>
            </div></td>` : ""}
          </tr>`).join("")}
      </tbody>
    </table></div>`}
    ${canConfigure ? `
      <h4 class="pt-section-title">Tags leídos sin dar de alta</h4>
      <p class="muted res-tab-intro">Tags que algún bastón leyó y que todavía no son ni punto ni llavero. Para dar de alta
        un punto nuevo, páselo con el bastón, descárguelo y elíjalo aquí: sus lecturas anteriores se suman a la bitácora.</p>
      ${unknown.length === 0 ? `<div class="info-box">No hay tags pendientes.</div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr><th>Tag</th><th>Lecturas</th><th>Primera</th><th>Última</th><th>Bastón</th><th></th></tr></thead>
        <tbody>
          ${unknown.map((u) => `
            <tr data-tag="${esc(u.tagId)}">
              <td class="pt-mono">${esc(u.tagId)}</td>
              <td>${u.reads}</td>
              <td class="muted" style="white-space:nowrap">${esc(patrolWhen(u.firstReadAt))}</td>
              <td class="muted" style="white-space:nowrap">${esc(patrolWhen(u.lastReadAt))}</td>
              <td class="muted">${esc(u.lastReaderName ?? "—")}</td>
              <td><div class="row-actions">
                <button class="btn ghost small pu-checkpoint">Dar de alta como punto</button>
                <button class="btn ghost small pu-tag">Dar de alta como llavero</button>
              </div></td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}` : ""}`;

  const byRow = (e) => checkpoints.find((c) => c.id === Number(e.target.closest("tr").dataset.id));
  const tagOf = (e) => e.target.closest("tr").dataset.tag;
  $("#pc-new")?.addEventListener("click", () => patrolCheckpointModal(null));
  $$("#patrol-body .pc-edit").forEach((b) => b.addEventListener("click", (e) => patrolCheckpointModal(byRow(e))));
  $$("#patrol-body .pc-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const c = byRow(e);
    if (!confirm(`¿Eliminar el punto "${c.name}"? Si ya tiene pasos en la bitácora no se puede: deshabilítelo.`)) return;
    try {
      await Api.delete(`/api/patrol/checkpoints/${c.id}`);
      toast("Punto eliminado.");
      patrolRenderTab(true);
    } catch (err) { toast(err.error, true); }
  }));
  $$("#patrol-body .pu-checkpoint").forEach((b) => b.addEventListener("click", (e) => patrolCheckpointModal(null, tagOf(e))));
  $$("#patrol-body .pu-tag").forEach((b) => b.addEventListener("click", (e) => patrolTagModal(null, tagOf(e))));
}

async function patrolCheckpointModal(c, prefillTag) {
  const isNew = !c;
  const places = await loadLocationChoices();
  openModal(`
    <h3>${isNew ? "Agregar punto de control" : "Editar punto de control"}</h3>
    <div id="pc-error"></div>
    <form id="pc-form">
      <div class="field"><label for="pc-name">Nombre</label>
        <input id="pc-name" required maxlength="128" placeholder="Bodega norte" value="${esc(c?.name ?? "")}"></div>
      <div class="form-grid">
        <div class="field"><label for="pc-kind">Tipo</label>
          <select id="pc-kind">
            <option value="Tag" selected>Tag (bastón)</option>
            <option value="Access" disabled>Terminal de acceso (próximamente)</option>
          </select></div>
        <div class="field"><label for="pc-tag">Tag</label>
          <input id="pc-tag" class="pt-mono" required maxlength="10" pattern="[0-9A-Fa-f]{10}" placeholder="3C007BC71D"
            value="${esc(c?.tagId ?? prefillTag ?? "")}">
          <div class="muted field-hint">10 caracteres hexadecimales.</div></div>
      </div>
      ${locationFieldHtml("pc-location", places, c?.locationId ?? null, "Los operadores con alcance restringido ven solo los puntos (y sus pasos) de sus ubicaciones.")}
      <label class="checkbox-row"><input type="checkbox" id="pc-enabled" ${c ? (c.enabled ? "checked" : "") : "checked"}> Habilitado (sus lecturas cuentan como pasos)</label>
      <div class="field"><label for="pc-notes">Notas</label>
        <textarea id="pc-notes" rows="2" maxlength="1024">${esc(c?.notes ?? "")}</textarea></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="pc-cancel">Cancelar</button>
        <button class="btn" type="submit" id="pc-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);
  $("#pc-cancel").addEventListener("click", closeModal);
  $("#pc-tag").addEventListener("input", (e) => { e.target.value = e.target.value.toUpperCase().replace(/[^0-9A-F]/g, ""); });
  $("#pc-name").focus();
  $("#pc-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = $("#pc-save");
    button.disabled = true;
    const body = {
      name: $("#pc-name").value.trim(),
      kind: "Tag",
      tagId: $("#pc-tag").value.trim().toUpperCase(),
      accessDoorId: null,
      locationId: locationFieldValue("pc-location"),
      enabled: $("#pc-enabled").checked,
      notes: $("#pc-notes").value.trim() || null,
    };
    try {
      if (isNew) await Api.post("/api/patrol/checkpoints", body);
      else await Api.put(`/api/patrol/checkpoints/${c.id}`, body);
      closeModal();
      toast(isNew ? "Punto agregado." : "Punto actualizado.");
      patrolRenderTab(true);
    } catch (err) {
      $("#pc-error").innerHTML = patrolErrorBox(err);
      button.disabled = false;
    }
  });
}

// ---------------------------------------------------------------------------
// 5. Bastones y bases

const PATROL_READER_STATUS = {
  Pending: `<span class="tag warn" title="Apareció en una base: apruébelo para que la base lo vacíe tras cada descarga">Pendiente</span>`,
  Approved: `<span class="tag on">Aprobado</span>`,
  Rejected: `<span class="tag off" title="Sus lecturas se guardan, pero nunca se vacía">Rechazado</span>`,
};

function patrolAgentStatus(a) {
  switch (a.status) {
    case "Online": return `<span class="tag on">En línea</span>`;
    case "Offline": return `<span class="tag off">Sin conexión</span>`;
    default: return `<span class="tag warn">Pendiente de enrolar</span>${a.enrollmentPending
      ? `<div class="muted pt-note">Código vigente hasta ${esc(patrolWhen(a.enrollmentExpiresAt))}</div>` : ""}`;
  }
}

function patrolMemoryCell(r) {
  if (r.lastMemoryBlocks == null) return `<span class="muted">—</span>`;
  const pct = Math.min(100, Math.round((r.lastMemoryBlocks / PATROL_MEMORY_BLOCKS) * 100));
  const title = `${r.lastMemoryBlocks.toLocaleString("es-CL")} de ${PATROL_MEMORY_BLOCKS.toLocaleString("es-CL")} bloques en la última lectura`;
  return pct >= 80
    ? `<span class="tag off" title="${title}. Vacíelo pronto: lleno deja de registrar.">${pct} %</span>`
    : `<span title="${title}">${pct} %</span>`;
}

async function patrolRenderReaders(body) {
  let readers, agents;
  try { [readers, agents] = await Promise.all([Api.get("/api/patrol/readers"), Api.get("/api/patrol/agents")]); }
  catch (err) { body.innerHTML = patrolErrorBox(err); return; }
  const canConfigure = Perms.can("patrol.configure");
  const canRead = Perms.can("patrol.read");
  const canErase = Perms.can("patrol.erase");

  const readerRow = (r) => {
    const noAgent = r.agentId == null;
    const busy = r.hasPendingCommand;
    const readTitle = noAgent ? "Nunca se conectó a una base: léalo desde el navegador" : busy ? "Ya hay una lectura pendiente" : "";
    return `
      <tr data-id="${r.id}">
        <td class="pt-mono">${esc(r.serialText)}</td>
        <td>${esc(r.name)}${licenseWaitTag(r)}${r.enabled ? "" : ` <span class="tag operator">deshabilitado</span>`}
          ${r.location ? `<div class="muted pt-note">${esc(r.location)}</div>` : ""}</td>
        <td>${PATROL_READER_STATUS[r.status] ?? esc(r.status)}</td>
        <td>${r.docked ? `<span class="tag on">Sí</span>` : `<span class="tag operator">No</span>`}
          ${r.dockedChangedAt ? `<div class="muted pt-note">desde ${esc(patrolWhen(r.dockedChangedAt))}</div>` : ""}</td>
        <td class="muted">${esc(r.agentName ?? "—")}</td>
        <td class="muted" style="white-space:nowrap">${esc(patrolWhen(r.lastDownloadAt))}</td>
        <td class="muted" style="white-space:nowrap">${esc(patrolWhen(r.lastClockSetAt))}</td>
        <td>${patrolMemoryCell(r)}</td>
        <td>${r.lastError ? `<span class="pt-error" title="${esc(r.lastError)}">${esc(r.lastError)}</span>` : `<span class="muted">—</span>`}</td>
        <td>${busy ? `<span class="tag warn" title="La base lo hará en cuanto el bastón esté en ella">Lectura pendiente</span>` : `<span class="muted">—</span>`}</td>
        <td><div class="row-actions" style="flex-wrap:wrap">
          ${canConfigure && r.status !== "Approved" ? `<button class="btn ghost small pr-approve">Aprobar</button>` : ""}
          ${canConfigure && r.status !== "Rejected" ? `<button class="btn ghost small pr-reject">Rechazar</button>` : ""}
          ${canConfigure ? `<button class="btn ghost small pr-edit">Editar</button>` : ""}
          ${canRead ? `<button class="btn ghost small pr-read" ${noAgent || busy ? `disabled title="${readTitle}"` : `title="Pide a la base que lo lea ahora (sin vaciarlo)"`}>Leer ahora</button>` : ""}
          ${canErase ? `<button class="btn ghost small pr-erase" ${noAgent || busy ? `disabled title="${readTitle}"` : `title="Pide a la base que lo lea y luego vacíe su memoria"`}>Leer y vaciar</button>` : ""}
          <button class="btn ghost small pr-history">Descargas</button>
          ${canConfigure ? `<button class="btn danger small pr-delete">Eliminar</button>` : ""}
        </div></td>
      </tr>`;
  };

  body.innerHTML = `
    ${canRead && !("serial" in navigator) ? `<div class="info-box">Para leer un bastón conectado por USB a este equipo
      (sin base de descarga) use Chrome o Edge de escritorio y abra el panel por HTTPS: este navegador no permite el
      acceso al puerto serie.</div>` : ""}
    <h4 class="pt-section-title">Bastones</h4>
    <p class="muted res-tab-intro">Un bastón aparece solo la primera vez que una base (o este PC) lo lee. Mientras no se
      apruebe, sus lecturas se guardan pero la base no lo vacía.</p>
    ${readers.length === 0 ? `<div class="info-box">Todavía no se ha leído ningún bastón.</div>` : `
    <div class="table-scroll"><table class="grid pt-grid">
      <thead><tr><th>N°</th><th>Nombre</th><th>Estado</th><th>En la base</th><th>Base</th><th>Última descarga</th>
        <th>Última puesta en hora</th><th>Memoria</th><th>Último error</th><th>Orden</th><th></th></tr></thead>
      <tbody>${readers.map(readerRow).join("")}</tbody>
    </table></div>`}

    <h4 class="pt-section-title">Bases de descarga</h4>
    <p class="muted res-tab-intro">Una base es el PC de la guardia donde se apoya el bastón: el programa
      <b>truecentral-patrol</b> lo descarga, lo pone en hora y lo vacía solo cada vez que vuelve.</p>
    ${canConfigure ? `<div class="row-actions pt-tab-actions"><button class="btn" id="pa-new">Agregar base</button></div>` : ""}
    ${agents.length === 0 ? `<div class="info-box">No hay bases de descarga. ${canConfigure ? "Agregue una y genere su código de enrolamiento." : ""}</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Nombre</th><th>Estado</th><th>Equipo</th><th>Versión</th><th>Última conexión</th><th>Último error</th>${canConfigure ? "<th></th>" : ""}</tr></thead>
      <tbody>
        ${agents.map((a) => `
          <tr data-id="${a.id}">
            <td>${esc(a.name)}${a.location ? `<div class="muted pt-note">${esc(a.location)}</div>` : ""}</td>
            <td>${patrolAgentStatus(a)}</td>
            <td class="muted">${esc(a.machineName ?? "—")}</td>
            <td class="muted">${esc(a.version ?? "—")}</td>
            <td class="muted" style="white-space:nowrap">${esc(patrolWhen(a.lastSeenAt))}</td>
            <td>${a.lastError ? `<span class="pt-error" title="${esc(a.lastError)}">${esc(a.lastError)}</span>` : `<span class="muted">—</span>`}</td>
            ${canConfigure ? `<td><div class="row-actions" style="flex-wrap:wrap">
              <button class="btn ghost small pa-enroll">Generar código de enrolamiento</button>
              <button class="btn ghost small pa-edit">Renombrar</button>
              <button class="btn danger small pa-delete">Eliminar</button>
            </div></td>` : ""}
          </tr>`).join("")}
      </tbody>
    </table></div>`}`;

  const readerOf = (e) => readers.find((r) => r.id === Number(e.target.closest("tr").dataset.id));
  const agentOf = (e) => agents.find((a) => a.id === Number(e.target.closest("tr").dataset.id));
  const act = (selector, fn) => $$(`#patrol-body ${selector}`).forEach((b) => b.addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    try { await fn(e); } catch (err) { toast(err.error ?? String(err), true); }
    finally { if (button.isConnected) button.disabled = false; }
  }));

  act(".pr-approve", async (e) => {
    const r = readerOf(e);
    await Api.post(`/api/patrol/readers/${r.id}/approve`);
    toast(`${r.name} aprobado: la base lo vaciará después de cada descarga.`);
    patrolRenderTab(true);
  });
  act(".pr-reject", async (e) => {
    const r = readerOf(e);
    if (!confirm(`¿Rechazar el bastón ${r.serialText}? Sus lecturas se siguen guardando, pero ninguna base lo vaciará.`)) return;
    await Api.post(`/api/patrol/readers/${r.id}/reject`);
    toast("Bastón rechazado.");
    patrolRenderTab(true);
  });
  act(".pr-edit", async (e) => patrolReaderModal(readerOf(e)));
  act(".pr-read", async (e) => patrolRequestRead(readerOf(e), false));
  act(".pr-erase", async (e) => {
    const r = readerOf(e);
    if (!confirm(`¿Leer y vaciar el bastón ${r.serialText}? Después de guardar las lecturas en el servidor se borra su memoria.`)) return;
    await patrolRequestRead(r, true);
  });
  act(".pr-history", async (e) => patrolDownloadsModal(readerOf(e)));
  act(".pr-delete", async (e) => {
    const r = readerOf(e);
    if (!confirm(`¿Eliminar el bastón ${r.serialText}? Si ya tiene lecturas guardadas no se puede: deshabilítelo.`)) return;
    await Api.delete(`/api/patrol/readers/${r.id}`);
    toast("Bastón eliminado.");
    patrolRenderTab(true);
  });

  $("#pa-new")?.addEventListener("click", () => patrolAgentModal(null));
  act(".pa-edit", async (e) => patrolAgentModal(agentOf(e)));
  act(".pa-delete", async (e) => {
    const a = agentOf(e);
    if (!confirm(`¿Eliminar la base "${a.name}"? Su conexión deja de valer y el PC de la guardia ya no podrá subir descargas.`)) return;
    await Api.delete(`/api/patrol/agents/${a.id}`);
    toast("Base eliminada.");
    patrolRenderTab(true);
  });
  act(".pa-enroll", async (e) => {
    const a = agentOf(e);
    if (a.status !== "Pending" && !confirm(`La base "${a.name}" ya está enrolada. Un código nuevo revoca su conexión actual hasta que se vuelva a enrolar con él. ¿Continuar?`)) return;
    const r = await Api.post(`/api/patrol/agents/${a.id}/enrollment`);
    patrolEnrollmentModal(a, r);
    patrolRenderTab(true);
  });
}

async function patrolRequestRead(r, erase) {
  const res = await Api.post(`/api/patrol/readers/${r.id}/read`, { erase });
  if (res?.docked === false) {
    toast(`El bastón ${r.serialText} no está en la base: ${erase ? "se leerá y vaciará" : "se leerá"} cuando vuelva a ella.`, false, { warning: true });
  } else {
    toast(`Orden enviada a ${r.agentName ?? "la base"}: ${erase ? "leer y vaciar" : "leer"} el bastón ${r.serialText}.`);
  }
  patrolRenderTab(true);
}

async function patrolReaderModal(r) {
  const places = await loadLocationChoices();
  openModal(`
    <h3>Bastón ${esc(r.serialText)}</h3>
    <div id="pr-error"></div>
    <form id="pr-form">
      <div class="field"><label for="pr-name">Nombre</label>
        <input id="pr-name" required maxlength="128" value="${esc(r.name)}"></div>
      ${locationFieldHtml("pr-location", places, r.locationId ?? null)}
      <label class="checkbox-row"><input type="checkbox" id="pr-enabled" ${r.enabled ? "checked" : ""}> Habilitado</label>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="pr-cancel">Cancelar</button>
        <button class="btn" type="submit" id="pr-save">Guardar cambios</button>
      </div>
    </form>`);
  $("#pr-cancel").addEventListener("click", closeModal);
  $("#pr-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = $("#pr-save");
    button.disabled = true;
    try {
      await Api.put(`/api/patrol/readers/${r.id}`, {
        name: $("#pr-name").value.trim(),
        locationId: locationFieldValue("pr-location"),
        enabled: $("#pr-enabled").checked,
      });
      closeModal();
      toast("Bastón actualizado.");
      patrolRenderTab(true);
    } catch (err) {
      $("#pr-error").innerHTML = patrolErrorBox(err);
      button.disabled = false;
    }
  });
}

const PATROL_ORIGINS = { Dock: "Base (automática)", DockOnDemand: "Base (a pedido)", Browser: "Navegador" };
const PATROL_DOWNLOAD_STATUS = {
  Running: `<span class="tag warn">En curso</span>`,
  Completed: `<span class="tag on">Completa</span>`,
  Failed: `<span class="tag off">Falló</span>`,
};

async function patrolDownloadsModal(r) {
  const rows = await Api.get(`/api/patrol/downloads?readerId=${r.id}`);
  openModal(`
    <h3>Descargas del bastón ${esc(r.serialText)}</h3>
    ${rows.length === 0 ? `<div class="info-box">Este bastón todavía no tiene descargas.</div>` : `
    <div class="table-scroll" style="max-height:60vh"><table class="grid">
      <thead><tr><th>Inicio</th><th>Origen</th><th>Quién</th><th>Bloques</th><th>Lecturas</th><th>Nuevas</th><th>Pasos</th><th>Hora</th><th>Vaciado</th><th>Estado</th></tr></thead>
      <tbody>
        ${rows.map((d) => `
          <tr>
            <td style="white-space:nowrap">${esc(patrolWhen(d.startedAt))}</td>
            <td>${esc(PATROL_ORIGINS[d.origin] ?? d.origin)}</td>
            <td class="muted">${esc(d.actor ?? "—")}</td>
            <td>${d.blocks}</td><td>${d.reads}</td><td>${d.newReads}</td><td>${d.newPasses}</td>
            <td>${d.clockSet ? "Sí" : "No"}</td>
            <td>${d.erased ? "Sí" : "No"}</td>
            <td>${PATROL_DOWNLOAD_STATUS[d.status] ?? esc(d.status)}${d.error ? `<div class="pt-error pt-note" title="${esc(d.error)}">${esc(d.error)}</div>` : ""}</td>
          </tr>`).join("")}
      </tbody>
    </table></div>`}
    <div class="modal-actions"><button class="btn ghost" type="button" id="pd-close">Cerrar</button></div>`, "wider");
  $("#pd-close").addEventListener("click", closeModal);
}

async function patrolAgentModal(a) {
  const isNew = !a;
  const places = await loadLocationChoices();
  openModal(`
    <h3>${isNew ? "Agregar base de descarga" : "Renombrar base"}</h3>
    <div id="pa-error"></div>
    <form id="pa-form">
      <div class="field"><label for="pa-name">Nombre</label>
        <input id="pa-name" required maxlength="128" placeholder="Garita acceso principal" value="${esc(a?.name ?? "")}"></div>
      ${locationFieldHtml("pa-location", places, a?.locationId ?? null)}
      ${isNew ? `<div class="muted field-hint">Después de guardar, use <b>Generar código de enrolamiento</b> para conectar el PC de la guardia.</div>` : ""}
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="pa-cancel">Cancelar</button>
        <button class="btn" type="submit" id="pa-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);
  $("#pa-cancel").addEventListener("click", closeModal);
  $("#pa-name").focus();
  $("#pa-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = $("#pa-save");
    button.disabled = true;
    const body = { name: $("#pa-name").value.trim(), locationId: locationFieldValue("pa-location") };
    try {
      const saved = isNew ? await Api.post("/api/patrol/agents", body) : await Api.put(`/api/patrol/agents/${a.id}`, body);
      closeModal();
      toast(isNew ? "Base agregada." : "Base actualizada.");
      patrolRenderTab(true);
      if (isNew && confirm("¿Generar ahora el código de enrolamiento para el PC de la guardia?")) {
        const r = await Api.post(`/api/patrol/agents/${saved.id}/enrollment`);
        patrolEnrollmentModal(saved, r);
      }
    } catch (err) {
      if ($("#pa-error")) $("#pa-error").innerHTML = patrolErrorBox(err);
      else toast(err.error, true);
      button.disabled = false;
    }
  });
}

/** El código se ve UNA sola vez: el servidor guarda solo su huella. */
function patrolEnrollmentModal(a, r) {
  const command = `truecentral-patrol enroll --server ${location.origin} --code ${r.code}`;
  openModal(`
    <h3>Código de enrolamiento · ${esc(a.name)}</h3>
    <div class="warn-box">Anótelo ahora: este código <b>no se vuelve a mostrar</b>. Vence en 24 horas
      (${esc(patrolWhen(r.expiresAt))}) y sirve una sola vez.</div>
    <div class="pt-code">${esc(r.code)}</div>
    <div class="field">
      <label>En el PC de la guardia, abra una consola como administrador y ejecute:</label>
      <div class="pt-command">
        <code id="pe-command" class="pt-mono">${esc(command)}</code>
        <button class="btn ghost" type="button" id="pe-copy" title="Copiar el comando"><i class="mdi mdi-content-copy" aria-hidden="true"></i> Copiar</button>
      </div>
      <div class="muted field-hint">Si la base ya estaba conectada, su conexión anterior quedó revocada: se reconecta al
        enrolarla con este código.</div>
    </div>
    <div class="modal-actions"><button class="btn" type="button" id="pe-close">Listo</button></div>`);
  $("#pe-close").addEventListener("click", closeModal);
  $("#pe-copy").addEventListener("click", async () => {
    try { await navigator.clipboard.writeText(command); toast("Comando copiado."); }
    catch { toast("No se pudo copiar: seleccione el comando y cópielo a mano.", true); }
  });
}
