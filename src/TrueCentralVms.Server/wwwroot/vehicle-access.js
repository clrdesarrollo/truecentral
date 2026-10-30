// Acceso vehicular por patente: el padrón de vehículos de cada persona y el
// registro de ingresos y salidas que anotan las automatizaciones («Registrar
// paso vehicular»). El padrón se administra con los permisos de personas; el
// registro se ve con «Ver patentes» y se exporta con «Exportar registros de acceso».

const VA_DIRECTIONS = { Entry: "Ingreso", Exit: "Salida" };
const VA_RESULTS = {
  AutoOpened: { label: "Apertura automática", tag: "on" },
  OutsideSchedule: { label: "Fuera de horario: abre el guardia", tag: "warn" },
  Rejected: { label: "Rechazado", tag: "off" },
  OpenFailed: { label: "Falló la apertura", tag: "off" },
  Logged: { label: "Registrado", tag: "operator" },
};
const VA_STATUS = {
  authorized: { label: "Autorizada", tag: "on" },
  unauthorized: { label: "No autorizada", tag: "warn" },
  unregistered: { label: "No registrada", tag: "off" },
};
const VA_PERIODS = { today: "Hoy", yesterday: "Ayer", week: "Últimos 7 días", month: "Últimos 30 días", all: "Todo", custom: "Personalizado" };

const vaState = { period: "today", from: "", to: "", directions: [], results: [], plate: "", q: "", accessPoint: "", page: 1 };
let vaTimer = null;

const vaTag = (map, key) => {
  const item = map[key];
  return item ? `<span class="tag ${item.tag}">${esc(item.label)}</span>` : esc(key ?? "—");
};

// ---------------------------------------------------------------------------
// Vehículos de una persona (modal desde Control de acceso → Personas)
// ---------------------------------------------------------------------------

/** Lista y edición de los vehículos de una persona; cada cambio se guarda al momento. */
async function accessVehiclesModal(person) {
  const canManage = Perms.can("persons.manage");
  let vehicles = [];
  let editing = null;   // null = nada; {} = alta; vehículo = edición

  const load = async () => {
    try { vehicles = await Api.get(`/api/access/vehicles?personId=${person.id}`); }
    catch (err) { toast(err.error, true); vehicles = []; }
  };

  const form = (v) => `
    <div class="wfe-band" style="margin-top:10px">
      <div class="wfe-band-head"><span>${v.id ? `Editar ${esc(v.plate)}` : "Nuevo vehículo"}</span></div>
      <div class="form-grid">
        <div class="field"><label>Patente</label>
          <input id="va-plate" maxlength="12" value="${esc(v.plate ?? "")}" placeholder="ABCD12" style="text-transform:uppercase" autofocus></div>
        <div class="field"><label>Nombre (opcional)</label>
          <input id="va-label" maxlength="64" value="${esc(v.label ?? "")}" placeholder="Camioneta 3, auto particular…"></div>
      </div>
      <div class="form-grid">
        <div class="field"><label>Marca</label><input id="va-brand" maxlength="64" value="${esc(v.brand ?? "")}"></div>
        <div class="field"><label>Modelo</label><input id="va-model" maxlength="64" value="${esc(v.model ?? "")}"></div>
        <div class="field"><label>Color</label><input id="va-color" maxlength="32" value="${esc(v.color ?? "")}"></div>
      </div>
      <div class="form-grid">
        <div class="field"><label>Vigente desde (opcional)</label><input id="va-from" type="date" value="${v.validFrom ? esc(accessDateInput(v.validFrom).slice(0, 10)) : ""}"></div>
        <div class="field"><label>Vigente hasta (opcional)</label><input id="va-to" type="date" value="${v.validTo ? esc(accessDateInput(v.validTo).slice(0, 10)) : ""}"></div>
      </div>
      <div class="field"><label>Notas (opcional)</label><input id="va-notes" maxlength="512" value="${esc(v.notes ?? "")}"></div>
      <label class="checkbox-row"><input type="checkbox" id="va-enabled" ${v.enabled === false ? "" : "checked"}> Habilitado</label>
      <div class="row-actions" style="margin-top:10px">
        <button class="btn" id="va-save">Guardar vehículo</button>
        <button class="btn ghost" id="va-cancel">Cancelar</button>
      </div>
    </div>`;

  const draw = () => {
    openModal(`
      <h3>Vehículos de ${esc(person.fullName)}</h3>
      <p class="muted" style="margin-top:-4px">La patente es una credencial de la persona: las automatizaciones de patente la buscan en
        este padrón. Para abrir sola, el vehículo y la persona deben estar habilitados y vigentes.</p>
      ${vehicles.length ? `<div class="table-scroll"><table class="grid">
        <thead><tr><th>Patente</th><th>Vehículo</th><th>Vigencia</th><th>Estado</th><th></th></tr></thead>
        <tbody>${vehicles.map((v) => `
          <tr data-id="${v.id}">
            <td><b>${esc(v.plate)}</b></td>
            <td>${esc(v.label ?? "")}${[v.brand, v.model, v.color].filter(Boolean).length
              ? `<div class="muted" style="font-size:11px">${esc([v.brand, v.model, v.color].filter(Boolean).join(" · "))}</div>` : ""}</td>
            <td class="muted" style="white-space:nowrap">${v.validFrom || v.validTo
              ? `${v.validFrom ? accessDay(v.validFrom) : "…"} – ${v.validTo ? accessDay(v.validTo) : "…"}` : "sin límite"}</td>
            <td title="${esc(v.statusReason ?? "")}">${vaTag(VA_STATUS, v.status)}</td>
            <td><div class="row-actions">${canManage ? `
              <button class="btn ghost va-edit">Editar</button>
              <button class="btn danger va-del">Eliminar</button>` : ""}</div></td>
          </tr>`).join("")}</tbody>
      </table></div>` : `<div class="info-box">Sin vehículos registrados.</div>`}
      ${editing ? form(editing) : canManage ? `<div class="row-actions" style="margin-top:10px"><button class="btn" id="va-add">+ Agregar vehículo</button></div>` : ""}
      <div class="modal-actions"><button class="btn ghost" id="va-close">Cerrar</button></div>`, true);

    $("#va-close").addEventListener("click", closeModal);
    $("#va-add")?.addEventListener("click", () => { editing = {}; draw(); });
    $("#va-cancel")?.addEventListener("click", () => { editing = null; draw(); });
    const byRow = (e) => vehicles.find((v) => v.id === Number(e.target.closest("tr").dataset.id));
    $$("#modal .va-edit").forEach((b) => b.addEventListener("click", (e) => { editing = byRow(e); draw(); }));
    $$("#modal .va-del").forEach((b) => b.addEventListener("click", async (e) => {
      const v = byRow(e);
      if (!confirm(`¿Eliminar del padrón la patente ${v.plate}? Sus pasos ya registrados se conservan.`)) return;
      try { await Api.delete(`/api/access/vehicles/${v.id}`); toast("Vehículo eliminado."); await load(); draw(); }
      catch (err) { toast(err.error, true); }
    }));
    $("#va-save")?.addEventListener("click", async () => {
      const day = (id, endOfDay) => {
        const value = $(`#${id}`).value;
        if (!value) return null;
        const [y, m, d] = value.split("-").map(Number);
        return (endOfDay ? new Date(y, m - 1, d, 23, 59, 59) : new Date(y, m - 1, d, 0, 0, 0)).toISOString();
      };
      const body = {
        accessPersonId: person.id,
        plate: $("#va-plate").value.trim(),
        label: $("#va-label").value.trim() || null,
        brand: $("#va-brand").value.trim() || null,
        model: $("#va-model").value.trim() || null,
        color: $("#va-color").value.trim() || null,
        notes: $("#va-notes").value.trim() || null,
        enabled: $("#va-enabled").checked,
        validFrom: day("va-from", false),
        validTo: day("va-to", true),
      };
      try {
        if (editing.id) await Api.put(`/api/access/vehicles/${editing.id}`, body);
        else await Api.post("/api/access/vehicles", body);
        toast(editing.id ? "Vehículo guardado." : "Vehículo agregado.");
        editing = null;
        await load();
        draw();
      } catch (err) { toast(err.error, true); }
    });
  };

  await load();
  draw();
}

// ---------------------------------------------------------------------------
// Registro de accesos vehiculares (#/vehicle-access)
// ---------------------------------------------------------------------------

function vaRange() {
  const s = vaState;
  const start = new Date(); start.setHours(0, 0, 0, 0);
  const day = 86400000;
  switch (s.period) {
    case "today": return { from: start };
    case "yesterday": return { from: new Date(start - day), to: new Date(start - 1) };
    case "week": return { from: new Date(start - 6 * day) };
    case "month": return { from: new Date(start - 29 * day) };
    case "custom": return { from: s.from ? new Date(s.from) : null, to: s.to ? new Date(s.to) : null };
    default: return {};
  }
}

function vaQuery(extra) {
  const s = vaState;
  const q = new URLSearchParams();
  const { from, to } = vaRange();
  if (from) q.set("from", from.toISOString());
  if (to) q.set("to", to.toISOString());
  if (s.directions.length) q.set("directions", s.directions.join(","));
  if (s.results.length) q.set("results", s.results.join(","));
  if (s.plate) q.set("plate", s.plate);
  if (s.q) q.set("q", s.q);
  if (s.accessPoint) q.set("accessPoint", s.accessPoint);
  Object.entries(extra || {}).forEach(([k, v]) => q.set(k, v));
  return q.toString();
}

async function renderVehicleAccess() {
  $("#page-title").textContent = "Accesos vehiculares";
  clearInterval(vaTimer);
  const canExport = Perms.can("access.records.export");
  const points = await Api.get("/api/vehicle-access/access-points").catch(() => []);
  const s = vaState;

  $("#view").innerHTML = `
    <div class="filter-bar">
      <div class="field"><label>Período</label>
        <select id="vaf-period">${Object.entries(VA_PERIODS).map(([k, l]) => `<option value="${k}" ${s.period === k ? "selected" : ""}>${l}</option>`).join("")}</select></div>
      <div class="field ${s.period === "custom" ? "" : "hidden"}" data-custom><label>Desde</label><input type="datetime-local" id="vaf-from" value="${esc(s.from)}"></div>
      <div class="field ${s.period === "custom" ? "" : "hidden"}" data-custom><label>Hasta</label><input type="datetime-local" id="vaf-to" value="${esc(s.to)}"></div>
      ${points.length > 1 ? `<div class="field"><label>Acceso</label><select id="vaf-point"><option value="">Todos</option>
        ${points.map((p) => `<option ${s.accessPoint === p ? "selected" : ""}>${esc(p)}</option>`).join("")}</select></div>` : ""}
      <div class="field"><label>Sentido</label><select id="vaf-direction"><option value="">Ambos</option>
        ${Object.entries(VA_DIRECTIONS).map(([k, l]) => `<option value="${k}" ${s.directions[0] === k ? "selected" : ""}>${l}</option>`).join("")}</select></div>
      <div class="field"><label>Resultado</label><select id="vaf-result"><option value="">Todos</option>
        ${Object.entries(VA_RESULTS).map(([k, r]) => `<option value="${k}" ${s.results[0] === k ? "selected" : ""}>${esc(r.label)}</option>`).join("")}</select></div>
      <div class="field"><label>Patente</label><input id="vaf-plate" value="${esc(s.plate)}" placeholder="parcial" autocomplete="off"></div>
      <div class="field"><label>Persona, ID o área</label><input id="vaf-q" value="${esc(s.q)}" autocomplete="off"></div>
      <div class="filter-actions">
        <button class="btn" id="vaf-search">Buscar</button>
        <button class="btn ghost" id="vaf-clear">Limpiar</button>
        ${canExport ? `<button class="btn ghost" id="vaf-xlsx" title="Descargar en Excel con los filtros actuales">Excel</button>
          <button class="btn ghost" id="vaf-pdf" title="Descargar en PDF con los filtros actuales">PDF</button>` : ""}
      </div>
    </div>
    <div id="va-results"><div class="info-box">Cargando…</div></div>`;

  const read = () => {
    s.period = $("#vaf-period").value;
    s.from = $("#vaf-from").value;
    s.to = $("#vaf-to").value;
    s.accessPoint = $("#vaf-point")?.value || "";
    s.directions = $("#vaf-direction").value ? [$("#vaf-direction").value] : [];
    s.results = $("#vaf-result").value ? [$("#vaf-result").value] : [];
    s.plate = $("#vaf-plate").value.trim();
    s.q = $("#vaf-q").value.trim();
    s.page = 1;
  };
  $("#vaf-period").addEventListener("change", () => $$("[data-custom]").forEach((el) => el.classList.toggle("hidden", $("#vaf-period").value !== "custom")));
  $("#vaf-search").addEventListener("click", () => { read(); loadVehicleAccess(); });
  ["#vaf-plate", "#vaf-q"].forEach((id) => $(id).addEventListener("keydown", (e) => { if (e.key === "Enter") { read(); loadVehicleAccess(); } }));
  $("#vaf-clear").addEventListener("click", () => {
    Object.assign(vaState, { period: "today", from: "", to: "", directions: [], results: [], plate: "", q: "", accessPoint: "", page: 1 });
    renderVehicleAccess();
  });
  const exportAs = (format) => { read(); Api.download(`/api/vehicle-access/records/export?${vaQuery({ format })}`); };
  $("#vaf-xlsx")?.addEventListener("click", () => exportAs("xlsx"));
  $("#vaf-pdf")?.addEventListener("click", () => exportAs("pdf"));

  await loadVehicleAccess();
  // La primera página se refresca sola: el guardia la deja abierta para ver quién entra.
  vaTimer = setInterval(() => {
    if (!location.hash.startsWith("#/vehicle-access")) { clearInterval(vaTimer); return; }
    if (vaState.page === 1 && !$("#modal-backdrop:not(.hidden)")) loadVehicleAccess();
  }, 10000);
}

async function loadVehicleAccess() {
  const box = $("#va-results");
  if (!box) return;
  let data;
  try { data = await Api.get(`/api/vehicle-access/records?${vaQuery({ page: vaState.page, pageSize: 50 })}`); }
  catch (err) { box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  if (!data.items.length) {
    box.innerHTML = `<div class="info-box">No hay pasos registrados con estos filtros. Los anota la acción «Registrar paso vehicular» de las automatizaciones de patente.</div>`;
    return;
  }
  const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Fecha y hora</th><th>Acceso</th><th>Sentido</th><th>Patente</th><th>Persona</th><th>Padrón</th><th>Resultado</th><th>Cámara</th></tr></thead>
      <tbody>${data.items.map((r) => `
        <tr>
          <td style="white-space:nowrap">${esc(formatDateTime(r.at))}</td>
          <td>${esc(r.accessPoint)}</td>
          <td>${esc(VA_DIRECTIONS[r.direction] ?? r.direction)}</td>
          <td style="white-space:nowrap">${r.hasPlateImage
            ? `<img class="va-plate-img" src="${Api.mediaUrl(`/api/vehicle-access/records/${r.id}/plate`)}" alt="" loading="lazy" style="height:22px;vertical-align:middle;margin-right:6px;border-radius:3px">` : ""}<b>${esc(r.plate)}</b>${r.confidence != null ? ` <span class="muted" style="font-size:11px">${r.confidence}%</span>` : ""}</td>
          <td>${r.personName ? `${esc(r.personName)}${r.vehicleLabel ? ` <span class="muted">· ${esc(r.vehicleLabel)}</span>` : ""}
            <div class="muted" style="font-size:11px">${esc([r.employeeNo, r.department].filter(Boolean).join(" · "))}</div>` : `<span class="muted">—</span>`}</td>
          <td title="${esc(r.detail ?? "")}">${vaTag(VA_STATUS, r.plateStatus)}</td>
          <td>${vaTag(VA_RESULTS, r.result)}</td>
          <td class="muted">${esc(r.cameraName)}</td>
        </tr>`).join("")}</tbody>
    </table></div>
    <div class="audit-pager">
      <span class="muted">${data.total} paso${data.total === 1 ? "" : "s"} · página ${data.page} de ${pages}</span>
      <div style="display:flex;gap:8px">
        <button class="btn ghost" id="va-prev" ${data.page <= 1 ? "disabled" : ""}>« Anterior</button>
        <button class="btn ghost" id="va-next" ${data.page >= pages ? "disabled" : ""}>Siguiente »</button>
      </div>
    </div>`;
  $("#va-prev")?.addEventListener("click", () => { vaState.page--; loadVehicleAccess(); });
  $("#va-next")?.addEventListener("click", () => { vaState.page++; loadVehicleAccess(); });
}
