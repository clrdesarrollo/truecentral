// Informes del control de rondas (#/patrol-reports): un formulario por informe
// (periodo con atajos y los filtros que apliquen) y descarga en PDF o Excel.
// Los genera el servidor (GET /api/patrol/reports/{kind}); exige patrol.export.
"use strict";

const PATROL_REPORTS = [
  {
    key: "summary", label: "Resumen ejecutivo", icon: "mdi-chart-bar",
    intro: "Una página para entregar al cliente: % de rondas cumplidas, incompletas, no iniciadas y sin terminar, la tendencia del periodo y las rutas y puntos con más fallas.",
    filters: ["location", "route", "checkpoint", "guard"],
  },
  {
    key: "compliance", label: "Cumplimiento de rutas", icon: "mdi-clipboard-check",
    intro: "Cada ronda con su hora programada, salida y vuelta de la base, guardias, estado y causa; debajo, cada punto con hora esperada, margen, hora real y estado.",
    filters: ["location", "route", "checkpoint", "guard"],
  },
  {
    key: "incidents", label: "Incidencias", icon: "mdi-alert-outline",
    intro: "Solo lo que falló: rondas no iniciadas, incompletas o sin terminar, con su causa y los puntos fallidos. Para la reunión de supervisión.",
    filters: ["location", "route", "checkpoint", "guard"],
  },
  {
    key: "guards", label: "Desempeño por guardia", icon: "mdi-account-star",
    intro: "Ranking del periodo: rondas hechas, % de cumplimiento, demora promedio en salir respecto de la hora programada, puntos a tiempo, tarde y omitidos.",
    filters: ["location", "route", "guard"],
  },
  {
    key: "guard-log", label: "Hoja de servicio", icon: "mdi-calendar-clock",
    intro: "Todos los pasos de un guardia en el periodo, en orden y agrupados por turno, con la ruta y el estado de cada punto.",
    filters: ["guard", "location", "checkpoint"], guardRequired: true,
  },
  {
    key: "coverage", label: "Cobertura por punto", icon: "mdi-map-marker-radius",
    intro: "Visitas por punto, intervalo mínimo, máximo y promedio entre visitas, ventanas sin cobertura mayores que el umbral y un mapa de calor de visitas por hora del día.",
    filters: ["location", "route", "checkpoint", "guard", "gap"],
  },
];

const PATROL_REPORT_PERIODS = [
  ["last-night", "Anoche (18:00 a 08:00)"],
  ["today", "Hoy"],
  ["yesterday", "Ayer"],
  ["7d", "Últimos 7 días"],
  ["this-week", "Esta semana"],
  ["last-week", "Semana pasada"],
  ["this-month", "Este mes"],
  ["last-month", "Mes pasado"],
  ["30d", "Últimos 30 días"],
  ["custom", "Personalizado"],
];

const patrolReportsState = {
  tab: "summary",
  period: "7d",
  from: "", to: "",
  locationId: "", routeId: "", checkpointId: "", guardId: "", gapMinutes: 60,
};

/** Fecha local → texto para input datetime-local (sin segundos). */
function prLocal(d) {
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** Rango [desde, hasta) de un atajo de periodo, en hora local del navegador. */
function prPeriodRange(key) {
  const now = new Date();
  const day = (offset) => { const d = new Date(now); d.setHours(0, 0, 0, 0); d.setDate(d.getDate() + offset); return d; };
  const monday = (weeks) => { const d = day(0); d.setDate(d.getDate() - ((d.getDay() + 6) % 7) + 7 * weeks); return d; };
  const at = (d, h) => { const x = new Date(d); x.setHours(h, 0, 0, 0); return x; };
  switch (key) {
    // Turno de noche: de ayer a las 18:00 a hoy a las 08:00 (antes de las 08:00, es el turno en curso).
    case "last-night": return [at(day(-1), 18), at(day(0), 8)];
    case "today": return [day(0), now];
    case "yesterday": return [day(-1), day(0)];
    case "this-week": return [monday(0), now];
    case "last-week": return [monday(-1), monday(0)];
    case "this-month": return [new Date(now.getFullYear(), now.getMonth(), 1), now];
    case "last-month": return [new Date(now.getFullYear(), now.getMonth() - 1, 1), new Date(now.getFullYear(), now.getMonth(), 1)];
    case "30d": return [day(-30), now];
    default: return [day(-7), now];
  }
}

async function renderPatrolReports() {
  $("#page-title").textContent = "Informes de rondas";
  const s = patrolReportsState;
  const asked = new URLSearchParams((location.hash.split("?")[1]) || "").get("report");
  if (asked && PATROL_REPORTS.some((r) => r.key === asked)) s.tab = asked;
  if (!s.from || !s.to) {
    const [a, b] = prPeriodRange(s.period);
    s.from = prLocal(a); s.to = prLocal(b);
  }

  $("#view").innerHTML = `<div class="info-box">Cargando…</div>`;
  const load = (path) => Api.get(path).catch(() => []);
  const [routes, guards, checkpoints, locations] = await Promise.all([
    load("/api/patrol/routes"), load("/api/patrol/guards"), load("/api/patrol/checkpoints"), load("/api/locations"),
  ]);
  if (!location.hash.startsWith("#/patrol-reports")) return;

  $("#view").innerHTML = `
    <div class="res-tabs" role="tablist">
      ${PATROL_REPORTS.map((r) => `
        <button type="button" role="tab" class="res-tab${r.key === s.tab ? " on" : ""}" data-tab="${r.key}">
          <i class="mdi ${r.icon}" aria-hidden="true"></i>${esc(r.label)}</button>`).join("")}
    </div>
    <div id="pr-body"></div>`;

  $$("#view .res-tab").forEach((b) => b.addEventListener("click", () => {
    prReadForm();
    s.tab = b.dataset.tab;
    $$("#view .res-tab").forEach((x) => x.classList.toggle("on", x === b));
    history.replaceState(null, "", `#/patrol-reports?report=${s.tab}`);
    prRenderForm(routes, guards, checkpoints, locations);
  }));
  prRenderForm(routes, guards, checkpoints, locations);
}

function prLocationOptions(locations, selected) {
  // Árbol de ubicaciones con sangría (el padre primero).
  const children = new Map();
  for (const l of locations) {
    const key = l.parentId && locations.some((x) => x.id === l.parentId) ? l.parentId : 0;
    if (!children.has(key)) children.set(key, []);
    children.get(key).push(l);
  }
  const out = [];
  const walk = (parent, depth) => {
    for (const l of (children.get(parent) || []).sort(compareLocations)) {
      out.push(`<option value="${l.id}" ${String(selected) === String(l.id) ? "selected" : ""}>${"  ".repeat(depth)}${esc(l.name)}</option>`);
      if (depth < 20) walk(l.id, depth + 1);
    }
  };
  walk(0, 0);
  return out.join("");
}

function prRenderForm(routes, guards, checkpoints, locations) {
  const s = patrolReportsState;
  const report = PATROL_REPORTS.find((r) => r.key === s.tab) || PATROL_REPORTS[0];
  const has = (f) => report.filters.includes(f);
  const body = $("#pr-body");
  body.innerHTML = `
    <div class="card" style="max-width:980px">
      <p class="res-tab-intro muted" style="margin-bottom:14px">${esc(report.intro)}</p>
      <div class="filter-bar">
        <div class="field"><label for="pr-period">Periodo</label>
          <select id="pr-period">
            ${PATROL_REPORT_PERIODS.map(([k, label]) => `<option value="${k}" ${k === s.period ? "selected" : ""}>${esc(label)}</option>`).join("")}
          </select></div>
        <div class="field"><label for="pr-from">Desde</label>
          <input id="pr-from" type="datetime-local" value="${esc(s.from)}"></div>
        <div class="field"><label for="pr-to">Hasta</label>
          <input id="pr-to" type="datetime-local" value="${esc(s.to)}"></div>
      </div>
      <div class="filter-bar">
        ${has("guard") ? `<div class="field"><label for="pr-guard">Guardia${report.guardRequired ? " *" : ""}</label>
          <select id="pr-guard"><option value="">${report.guardRequired ? "Elija un guardia…" : "Todos"}</option>
            ${guards.map((g) => `<option value="${g.id}" ${String(s.guardId) === String(g.id) ? "selected" : ""}>${esc(g.fullName)}${g.active ? "" : " (desvinculado)"}</option>`).join("")}
          </select></div>` : ""}
        ${has("location") ? `<div class="field"><label for="pr-location">Ubicación</label>
          <select id="pr-location"><option value="">Todas</option>${prLocationOptions(locations, s.locationId)}</select></div>` : ""}
        ${has("route") ? `<div class="field"><label for="pr-route">Ruta</label>
          <select id="pr-route"><option value="">Todas</option>
            ${routes.map((r) => `<option value="${r.id}" ${String(s.routeId) === String(r.id) ? "selected" : ""}>${esc(r.name)}${r.enabled ? "" : " (deshabilitada)"}</option>`).join("")}
          </select></div>` : ""}
        ${has("checkpoint") ? `<div class="field"><label for="pr-checkpoint">Punto</label>
          <select id="pr-checkpoint"><option value="">Todos</option>
            ${checkpoints.map((c) => `<option value="${c.id}" ${String(s.checkpointId) === String(c.id) ? "selected" : ""}>${esc(c.name)}</option>`).join("")}
          </select></div>` : ""}
        ${has("gap") ? `<div class="field"><label for="pr-gap" title="Se informa todo lapso sin visita a un punto mayor que este umbral">Sin cobertura más de (min)</label>
          <input id="pr-gap" type="number" min="1" max="10080" step="1" value="${esc(s.gapMinutes)}"></div>` : ""}
      </div>
      <div class="row-actions" style="display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-top:6px">
        <button class="btn" id="pr-pdf" data-format="pdf"><i class="mdi mdi-file-pdf-box" aria-hidden="true"></i> PDF</button>
        <button class="btn ghost" id="pr-xlsx" data-format="xlsx"><i class="mdi mdi-file-excel" aria-hidden="true"></i> Excel</button>
        <span id="pr-status" class="muted" style="font-size:12.5px"></span>
      </div>
    </div>`;

  $("#pr-period").addEventListener("change", (e) => {
    s.period = e.target.value;
    if (s.period === "custom") return;
    const [a, b] = prPeriodRange(s.period);
    $("#pr-from").value = prLocal(a);
    $("#pr-to").value = prLocal(b);
  });
  for (const id of ["#pr-from", "#pr-to"])
    $(id).addEventListener("change", () => { s.period = "custom"; $("#pr-period").value = "custom"; });
  $("#pr-pdf").addEventListener("click", (e) => prGenerate(report, "pdf", e.currentTarget));
  $("#pr-xlsx").addEventListener("click", (e) => prGenerate(report, "xlsx", e.currentTarget));
}

function prReadForm() {
  const s = patrolReportsState;
  const val = (id) => $(id)?.value;
  if ($("#pr-from")) { s.from = val("#pr-from"); s.to = val("#pr-to"); s.period = val("#pr-period"); }
  if ($("#pr-guard")) s.guardId = val("#pr-guard");
  if ($("#pr-location")) s.locationId = val("#pr-location");
  if ($("#pr-route")) s.routeId = val("#pr-route");
  if ($("#pr-checkpoint")) s.checkpointId = val("#pr-checkpoint");
  if ($("#pr-gap")) s.gapMinutes = val("#pr-gap");
}

async function prGenerate(report, format, button) {
  prReadForm();
  const s = patrolReportsState;
  if (!s.from || !s.to) { toast("Indique el periodo completo.", true); return; }
  if (new Date(s.from) >= new Date(s.to)) { toast("La fecha \"Desde\" debe ser anterior a \"Hasta\".", true); return; }
  if (report.guardRequired && !s.guardId) { toast("Elija el guardia.", true); return; }
  const q = new URLSearchParams({ format, from: s.from + ":00", to: s.to + ":00" });
  const has = (f) => report.filters.includes(f);
  if (has("guard") && s.guardId) q.set("guardId", s.guardId);
  if (has("location") && s.locationId) q.set("locationId", s.locationId);
  if (has("route") && s.routeId) q.set("routeId", s.routeId);
  if (has("checkpoint") && s.checkpointId) q.set("checkpointId", s.checkpointId);
  if (has("gap")) q.set("gapMinutes", String(Math.max(1, parseInt(s.gapMinutes, 10) || 60)));

  const buttons = $$("#pr-pdf, #pr-xlsx");
  buttons.forEach((b) => { b.disabled = true; });
  const status = $("#pr-status");
  if (status) status.textContent = `Generando ${format === "pdf" ? "PDF" : "Excel"}…`;
  try {
    const res = await fetch(`/api/patrol/reports/${report.key}?${q}`, { headers: { Authorization: `Bearer ${Api.token}` } });
    if (!res.ok) {
      if (res.status === 401) { window.dispatchEvent(new Event("tcvms:unauthorized")); Api.clearSession(); }
      const data = await res.json().catch(() => null);
      throw { error: data?.error || `Error ${res.status}` };
    }
    const disposition = res.headers.get("Content-Disposition") || "";
    const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(disposition);
    const blob = await res.blob();
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = match ? decodeURIComponent(match[1] || match[2]) : `${report.key}.${format}`;
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
    if (status) status.textContent = "";
    toast("Informe generado.");
  } catch (err) {
    if (status) status.textContent = "";
    toast(err.error || "No se pudo generar el informe.", true);
  } finally {
    buttons.forEach((b) => { b.disabled = false; });
  }
}
