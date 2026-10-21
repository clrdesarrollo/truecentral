// ---------------------------------------------------------------------------
// Panel de inicio: centro de mando con el estado general de la seguridad y del
// servidor. Todo sale de GET /api/dashboard (permisos y alcance ya aplicados
// por el servidor: lo que no le corresponde al usuario viene en null o en 0).
// Los gráficos son SVG propios, sin bibliotecas. Se refresca cada 30 s.
// ---------------------------------------------------------------------------
let dashboardTimer = null;
let dashboardClock = null;
let dashboardData = null;
// Series que el usuario ocultó en la leyenda de la actividad (por pestaña).
const dashboardHidden = new Set();

const DB_SERIES = [
  { key: "alarm", label: "Intrusión", color: "var(--danger)" },
  { key: "fence", label: "Cerco", color: "var(--warn)" },
  { key: "denied", label: "Accesos denegados", color: "#e8590c" },
  { key: "granted", label: "Accesos concedidos", color: "var(--accent)" },
  { key: "plate", label: "Patentes", color: "var(--violet)" },
  { key: "alert", label: "Alertas", color: "#0ea5a4" },
];

const DB_SOURCE_ICON = {
  alarm: "mdi-alert-octagram", fence: "mdi-flash", access: "mdi-account-key",
  plate: "mdi-car", alert: "mdi-bell-ring",
};

function dashboardLeave() {
  clearInterval(dashboardTimer);
  clearInterval(dashboardClock);
  dashboardTimer = dashboardClock = null;
}

async function renderDashboard() {
  $("#page-title").textContent = "Panel";
  const view = $("#view");
  if (!dashboardData) view.innerHTML = `<div class="db-empty">Cargando el resumen…</div>`;
  await dashboardRefresh(true);
  dashboardTimer = setInterval(() => dashboardRefresh(false), 30000);
  dashboardClock = setInterval(dashboardTick, 1000);
}

async function dashboardRefresh(first) {
  const [data, license, sessions] = await Promise.all([
    Api.get("/api/dashboard").catch(() => null),
    Api.get("/api/system/license").catch(() => null),
    Perms.can("sessions.manage") ? Api.get("/api/streams/active").catch(() => null) : null,
  ]);
  // El usuario pudo cambiar de página mientras se consultaba.
  if ((location.hash || "#/").split("?")[0] !== "#/") return;
  if (!data) {
    if (first) $("#view").innerHTML = `<div class="db"><div class="db-hero idle">${dbHeroOffline()}</div></div>`;
    return;
  }
  dashboardData = { ...data, license, sessions: Array.isArray(sessions) ? sessions.length : null };
  $("#view").innerHTML = dashboardHtml(dashboardData);
  dashboardBind();
  dashboardTick();
  // El anillo se llena con animación: se dibuja vacío y se completa en el cuadro siguiente.
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const bar = $("#view .db-ring .bar");
    if (bar) bar.style.strokeDashoffset = bar.dataset.offset;
  }));
}

function dashboardTick() {
  const now = new Date();
  const time = $("#db-time");
  if (!time) return;
  time.textContent = now.toLocaleTimeString("es-CL", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  $("#db-date").textContent = now.toLocaleDateString("es-CL", { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  const up = $("#db-uptime");
  if (up && dashboardData) {
    const seconds = dashboardData.server.uptimeSeconds + (Date.now() - new Date(dashboardData.generatedAt).getTime()) / 1000;
    up.textContent = dbDuration(seconds);
  }
}

// ---------------------------------------------------------------------------
// Estado general: qué hay en alarma, qué requiere atención y cuánto está en línea
// ---------------------------------------------------------------------------
function dbAssess(d) {
  const alarms = [], warnings = [];
  const i = d.intrusion, f = d.fence, e = d.equipment, s = d.server;
  if (i.inAlarm) alarms.push({ text: `${i.inAlarm} ${dbPlural(i.inAlarm, "área", "áreas")} en alarma`, href: "#/alarms", icon: "mdi-alert-octagram" });
  else if (i.zonesAlarm) alarms.push({ text: `${i.zonesAlarm} ${dbPlural(i.zonesAlarm, "zona", "zonas")} en alarma`, href: "#/alarms", icon: "mdi-alert-octagram" });
  if (f.inAlarm) alarms.push({ text: `Cerco en alarma (${f.inAlarm})`, href: "#/cerco", icon: "mdi-flash" });
  if (d.pendingAlerts) alarms.push({ text: `${d.pendingAlerts} ${dbPlural(d.pendingAlerts, "alerta", "alertas")} sin atender`, href: "#/event-center", icon: "mdi-bell-ring" });

  if (s.servicesFailed.length) warnings.push({ text: `Servicio caído: ${s.servicesFailed.join(", ")}`, href: "#/services", icon: "mdi-server-off" });
  const offline = [
    [e.cameras, "cámara", "cámaras", "#/devices"],
    [e.access, "equipo de acceso", "equipos de acceso", "#/access"],
    [e.alarmPanels, "panel de alarma", "paneles de alarma", "#/alarm-panels"],
    [e.fence, "panel de cerco", "paneles de cerco", "#/cerco-panels"],
    [e.intercoms, "citófono", "citófonos", "#/intercoms"],
    [e.speakers, "parlante", "parlantes", "#/speakers"],
    [e.ioModules, "módulo de E/S", "módulos de E/S", "#/io-modules"],
  ];
  for (const [g, one, many, href] of offline) {
    const n = g.total - g.online;
    if (n > 0) warnings.push({ text: `${n} ${dbPlural(n, one, many)} sin conexión`, href, icon: "mdi-lan-disconnect" });
  }
  if (f.faults) warnings.push({ text: `Falla en el cerco (${f.faults})`, href: "#/cerco", icon: "mdi-flash-off" });
  if (f.onBattery) warnings.push({ text: `Cerco a batería (${f.onBattery})`, href: "#/cerco", icon: "mdi-battery-alert" });
  if (i.tamper) warnings.push({ text: `Sabotaje en panel (${i.tamper})`, href: "#/alarms", icon: "mdi-alert-octagon" });
  if (i.acLoss) warnings.push({ text: `Panel sin corriente (${i.acLoss})`, href: "#/alarms", icon: "mdi-power-plug-off" });
  if (i.zonesTrouble) warnings.push({ text: `${i.zonesTrouble} ${dbPlural(i.zonesTrouble, "zona", "zonas")} con falla`, href: "#/alarms", icon: "mdi-alert" });
  if (d.license?.warning) warnings.push({ text: d.license.warning, href: "#/license", icon: "mdi-certificate" });

  // Disponibilidad: equipos en línea sobre el total (las cámaras cuentan por canal).
  const groups = [e.cameras, e.access, e.alarmPanels, e.fence, e.intercoms, e.speakers, e.ioModules];
  const total = groups.reduce((a, g) => a + g.total, 0);
  const online = groups.reduce((a, g) => a + g.online, 0);
  const availability = total ? Math.round((online / total) * 100) : null;
  const state = alarms.length ? "alarm" : warnings.length ? "warn" : total ? "ok" : "idle";
  return { alarms, warnings, availability, total, online, state };
}

function dbHeroOffline() {
  return `
    <div class="db-ring">${dbRingSvg(0)}<div class="db-ring-center"><i class="mdi mdi-lan-disconnect"></i><small>Sin datos</small></div></div>
    <div class="db-headline">
      <div class="eyebrow"><span class="dot"></span>Estado de las instalaciones</div>
      <h3>No se pudo leer el estado del servidor</h3>
      <p>El panel vuelve a intentarlo cada 30 segundos.</p>
    </div>
    <div class="db-clock"><div class="time" id="db-time"></div><div class="date" id="db-date"></div></div>`;
}

function dashboardHtml(d) {
  const a = dbAssess(d);
  const titles = {
    alarm: a.alarms.length === 1 ? "Alarma activa" : `${a.alarms.length} situaciones de alarma`,
    warn: `Operativo, con ${a.warnings.length} ${dbPlural(a.warnings.length, "observación", "observaciones")}`,
    ok: "Todo en orden",
    idle: "Sin equipos configurados",
  };
  const i = d.intrusion, f = d.fence;
  const summary = [];
  if (i.areas) summary.push(`${i.armed} de ${i.areas} ${dbPlural(i.areas, "área armada", "áreas armadas")}`);
  if (f.panels) summary.push(`${f.armed} de ${f.panels} ${dbPlural(f.panels, "panel de cerco armado", "paneles de cerco armados")}`);
  if (d.equipment.cameras.total) summary.push(`${d.equipment.cameras.online} de ${d.equipment.cameras.total} cámaras en línea`);
  const lead = {
    alarm: "Requiere atención inmediata del operador.",
    warn: "La seguridad está activa, pero hay puntos que revisar.",
    ok: "Todos los sistemas responden y no hay alarmas activas.",
    idle: "Agregue cámaras, paneles o equipos de acceso para ver aquí su estado.",
  }[a.state];
  const chips = [
    ...a.alarms.map((x) => ({ ...x, cls: "alarm" })),
    ...a.warnings.slice(0, a.alarms.length ? 4 : 6).map((x) => ({ ...x, cls: "warn" })),
  ];
  const hidden = a.warnings.length - (a.alarms.length ? 4 : 6);
  const ringIcon = { alarm: "mdi-alert-octagram", warn: "mdi-alert", ok: "mdi-security", idle: "mdi-shield-outline" }[a.state];

  return `
  <div class="db">
    <section class="db-hero ${a.state}" aria-label="Estado general">
      <div class="db-ring" title="Disponibilidad: equipos en línea sobre el total">
        ${dbRingSvg(a.availability ?? 0)}
        <div class="db-ring-center">
          <i class="mdi ${ringIcon}" aria-hidden="true"></i>
          <b>${a.availability == null ? "—" : a.availability + "%"}</b>
          <small>Disponibilidad</small>
        </div>
      </div>
      <div class="db-headline">
        <div class="eyebrow"><span class="dot"></span>Estado de las instalaciones</div>
        <h3>${esc(titles[a.state])}</h3>
        <p>${esc(lead)}${summary.length ? " " + esc(dbCapitalize(summary.join(" · "))) + "." : ""}</p>
        ${chips.length ? `<div class="db-issues">
          ${chips.map((c) => `<a class="db-issue ${c.cls}" href="${c.href}"><i class="mdi ${c.icon}" aria-hidden="true"></i>${esc(c.text)}</a>`).join("")}
          ${hidden > 0 ? `<span class="db-issue">+${hidden} más</span>` : ""}
        </div>` : a.state === "ok" ? `<div class="db-issues"><span class="db-issue ok"><i class="mdi mdi-check-circle" aria-hidden="true"></i>${a.online} equipos en línea</span></div>` : ""}
      </div>
      <div class="db-clock">
        <div class="time" id="db-time"></div>
        <div class="date" id="db-date"></div>
        <div class="meta">
          <span>Servidor <b>v${esc(d.server.version)}</b></span>
          <span>En marcha hace <b id="db-uptime"></b></span>
        </div>
      </div>
    </section>

    ${dbKpisHtml(d)}

    <div class="db-grid">
      ${d.activity ? dbActivityHtml(d) : ""}
      ${dbPerimeterHtml(d, d.activity ? "span-4" : "span-6")}
      ${dbHealthHtml(d)}
      ${dbEquipmentHtml(d, a)}
      ${dbFeedHtml(d)}
      ${dbOfflineHtml(d)}
      ${d.hotspots ? dbHotspotsHtml(d) : ""}
    </div>
    <div class="db-updated"><i class="mdi mdi-refresh" aria-hidden="true"></i>Actualizado ${new Date(d.generatedAt).toLocaleTimeString("es-CL", { hour: "2-digit", minute: "2-digit", second: "2-digit" })} · se refresca cada 30 s</div>
  </div>`;
}

// ---------------------------------------------------------------------------
// Indicadores de las últimas 24 h, con su curva y la comparación con el día anterior
// ---------------------------------------------------------------------------
function dbKpisHtml(d) {
  const s = d.activity?.series || {}, p = d.activity?.previous || {};
  const sum = (arr) => (arr || []).reduce((a, b) => a + b, 0);
  const add = (x, y) => (x || y) ? (x || new Array(24).fill(0)).map((v, k) => v + ((y || [])[k] || 0)) : null;
  const tiles = [];
  const cams = d.equipment.cameras;
  if (cams.total) tiles.push({
    label: "Cámaras en línea", icon: "mdi-camcorder", cls: cams.online === cams.total ? "ok" : "warn", href: "#/live",
    value: cams.online, unit: `/ ${cams.total}`,
    sub: cams.online === cams.total ? `<span class="good">Todas transmitiendo</span>` : `<span class="bad">${cams.total - cams.online} sin señal</span>`,
    meter: cams.online / cams.total,
  });
  if (s.alarm || s.fence) {
    const series = add(s.alarm, s.fence);
    tiles.push({ label: "Alarmas · 24 h", icon: "mdi-alert-octagram", cls: "danger", href: s.alarm ? "#/alarms" : "#/cerco",
      value: sum(series), series, sub: dbTrend(sum(series), (p.alarm || 0) + (p.fence || 0), true) });
  }
  if (s.granted) tiles.push({ label: "Accesos · 24 h", icon: "mdi-login-variant", cls: "", href: "#/access-events",
    value: sum(s.granted), series: s.granted, sub: dbTrend(sum(s.granted), p.granted, false) });
  if (s.denied) tiles.push({ label: "Accesos denegados", icon: "mdi-account-remove", cls: "warn", href: "#/access-events",
    value: sum(s.denied), series: s.denied, sub: dbTrend(sum(s.denied), p.denied, true) });
  if (s.plate) tiles.push({ label: "Patentes leídas", icon: "mdi-car", cls: "violet", href: "#/anpr",
    value: sum(s.plate), series: s.plate, sub: dbTrend(sum(s.plate), p.plate, false) });
  if (d.pendingAlerts != null) tiles.push({ label: "Alertas pendientes", icon: "mdi-bell-ring", cls: d.pendingAlerts ? "danger" : "teal",
    href: "#/event-center", value: d.pendingAlerts, series: s.alert,
    sub: `${sum(s.alert)} ${dbPlural(sum(s.alert), "alerta", "alertas")} en 24 h` });
  if (!tiles.length) return "";
  return `<div class="db-kpis">${tiles.map((t) => `
    <a class="db-kpi ${t.cls}" href="${t.href}">
      <div class="db-kpi-head"><span class="db-kpi-label">${esc(t.label)}</span><span class="db-kpi-ico"><i class="mdi ${t.icon}" aria-hidden="true"></i></span></div>
      <div class="db-kpi-value">${t.value.toLocaleString("es-CL")}${t.unit ? `<small>${esc(t.unit)}</small>` : ""}</div>
      <div class="db-kpi-sub">${t.sub || ""}</div>
      ${t.series ? dbSparkSvg(t.series) : t.meter != null ? `<div class="db-meter" style="margin-top:14px;height:6px"><i style="width:${Math.round(t.meter * 100)}%"></i></div>` : ""}
    </a>`).join("")}</div>`;
}

/** Comparación con las 24 h anteriores; en las malas noticias (alarmas, denegados) subir es lo malo. */
function dbTrend(now, before, upIsBad) {
  before = before || 0;
  if (!now && !before) return "Sin actividad";
  if (!before) return `<span class="${upIsBad ? "bad" : "good"}">Nuevo</span> respecto de ayer`;
  const pct = Math.round(((now - before) / before) * 100);
  if (pct === 0) return "Igual que ayer";
  const bad = upIsBad ? pct > 0 : pct < 0;
  return `<span class="${bad ? "bad" : "good"}">${pct > 0 ? "▲" : "▼"} ${Math.abs(pct)} %</span> respecto de ayer`;
}

function dbSparkSvg(series) {
  const w = 100, h = 30, max = Math.max(1, ...series);
  const pts = series.map((v, k) => [(k / (series.length - 1)) * w, h - 2 - (v / max) * (h - 4)]);
  const line = pts.map((p, k) => `${k ? "L" : "M"}${p[0].toFixed(1)},${p[1].toFixed(1)}`).join("");
  return `<svg class="db-spark" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">
    <path class="area" d="${line}L${w},${h}L0,${h}Z"/><path class="line" d="${line}"/></svg>`;
}

// ---------------------------------------------------------------------------
// Actividad de las últimas 24 h: barras apiladas por hora
// ---------------------------------------------------------------------------
function dbActivityHtml(d) {
  const present = DB_SERIES.filter((s) => d.activity.series[s.key]);
  return `
  <section class="db-card span-8">
    <div class="db-card-head">
      <h4><i class="mdi mdi-chart-bar" aria-hidden="true"></i>Actividad de las últimas 24 horas</h4>
      <div class="db-legend" role="group" aria-label="Series">
        ${present.map((s) => `<button type="button" class="db-legend-item${dashboardHidden.has(s.key) ? " off" : ""}" data-key="${s.key}" aria-pressed="${!dashboardHidden.has(s.key)}"><i style="background:${s.color}"></i>${esc(s.label)}</button>`).join("")}
      </div>
    </div>
    <div class="db-chart" id="db-chart">${dbBarsSvg(d)}</div>
  </section>`;
}

function dbBarsSvg(d, width) {
  const act = d.activity;
  const keys = DB_SERIES.filter((s) => act.series[s.key] && !dashboardHidden.has(s.key));
  // Al ancho real del contenedor: así los textos de los ejes no se deforman.
  const W = Math.max(320, Math.round(width || 720)), H = 230, L = 34, B = 22, T = 8;
  const totals = Array.from({ length: act.hours }, (_, k) => keys.reduce((a, s) => a + act.series[s.key][k], 0));
  const max = dbNiceMax(Math.max(1, ...totals));
  const plotH = H - B - T, slot = (W - L) / act.hours, bw = Math.max(4, slot * 0.62);
  const y = (v) => T + plotH - (v / max) * plotH;
  // Con escalas chicas la mitad no es entera y el eje repetiría "1, 1".
  const ticks = max >= 4 ? [0, max / 2, max] : [0, max];
  const start = new Date(act.from);
  let out = ticks.map((t) => `<line class="grid-line" x1="${L}" x2="${W}" y1="${y(t)}" y2="${y(t)}"/>
    <text class="axis" x="${L - 6}" y="${y(t) + 3.5}" text-anchor="end">${Math.round(t)}</text>`).join("");
  for (let k = 0; k < act.hours; k++) {
    const x = L + k * slot + (slot - bw) / 2;
    let acc = 0, rects = "";
    keys.forEach((s, idx) => {
      const v = act.series[s.key][k];
      if (!v) return;
      const top = y(acc + v), hgt = y(acc) - top;
      const isTop = !keys.slice(idx + 1).some((n) => act.series[n.key][k]);
      rects += `<rect x="${x.toFixed(1)}" y="${top.toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(1, hgt).toFixed(1)}" rx="${isTop ? 2.5 : 0}" fill="${s.color}"/>`;
      acc += v;
    });
    const hour = new Date(start.getTime() + k * 3600000);
    const every = W < 560 ? 6 : 3;
    const label = k % every === 0 || k === act.hours - 1
      ? `<text class="axis" x="${(L + k * slot + slot / 2).toFixed(1)}" y="${H - 6}" text-anchor="middle">${k === act.hours - 1 ? "Ahora" : String(hour.getHours()).padStart(2, "0") + ":00"}</text>` : "";
    out += `<g class="col" data-k="${k}"><rect class="hit" x="${(L + k * slot).toFixed(1)}" y="${T}" width="${slot.toFixed(1)}" height="${plotH}"/>${rects}</g>${label}`;
  }
  return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Eventos por hora en las últimas 24 horas">${out}</svg>`;
}

function dbNiceMax(v) {
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  const n = v / p;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p;
}

// ---------------------------------------------------------------------------
// Perímetro: un renglón por sistema con su estado
// ---------------------------------------------------------------------------
function dbPerimeterHtml(d, span) {
  const i = d.intrusion, f = d.fence, doors = d.doors, cams = d.equipment.cameras;
  const rows = [];
  if (i.panels || Perms.licensed("module_alarms") && Perms.modules) {
    const state = !i.panels ? "idle" : i.inAlarm || i.zonesAlarm ? "alarm" : i.panelsOnline < i.panels || i.tamper || i.acLoss ? "warn" : "ok";
    rows.push({ cls: state, icon: "mdi-security-home", href: "#/alarms", title: "Intrusión",
      text: !i.panels ? "Sin paneles de alarma" : i.inAlarm ? `${i.inAlarm} ${dbPlural(i.inAlarm, "área", "áreas")} en alarma`
        : `${i.zonesOpen} ${dbPlural(i.zonesOpen, "zona abierta", "zonas abiertas")} · ${i.zonesBypassed} ${dbPlural(i.zonesBypassed, "anulada", "anuladas")}`,
      num: i.areas ? `${i.armed}/${i.areas}` : "—", numLabel: "armadas",
      bar: i.areas ? [[i.inAlarm, "var(--danger)"], [i.armed - Math.min(i.armed, i.inAlarm), "var(--ok)"], [i.disarmed, "rgba(var(--muted-rgb), .45)"]].map(([v, c]) => [v / i.areas, c]) : null });
  }
  if (f.panels || Perms.licensed("module_fence") && Perms.modules) {
    const state = !f.panels ? "idle" : f.inAlarm ? "alarm" : f.online < f.panels || f.faults || f.onBattery ? "warn" : "ok";
    rows.push({ cls: state, icon: "mdi-flash", href: "#/cerco", title: "Cerco eléctrico",
      text: !f.panels ? "Sin paneles de cerco" : f.inAlarm ? `${f.inAlarm} en alarma` : f.faults ? `${f.faults} con falla de alta tensión` : `${f.online} de ${f.panels} conectados`,
      num: f.panels ? `${f.armed}/${f.panels}` : "—", numLabel: "armados",
      bar: f.panels ? [[f.armed / f.panels, f.inAlarm ? "var(--danger)" : "var(--ok)"]] : null });
  }
  if (doors.total) {
    rows.push({ cls: doors.remainOpen ? "warn" : "ok", icon: "mdi-lock", href: "#/access-monitor", title: "Puertas",
      text: doors.remainOpen ? `${doors.remainOpen} ${dbPlural(doors.remainOpen, "queda", "quedan")} abierta(s) por modo` : `${doors.open} ${dbPlural(doors.open, "abierta", "abiertas")} en este momento`,
      num: doors.total - doors.open, numLabel: `de ${doors.total} cerradas`,
      bar: [[(doors.total - doors.open) / doors.total, "var(--ok)"]] });
  }
  if (cams.total) {
    rows.push({ cls: cams.online < cams.total ? "warn" : "ok", icon: "mdi-camcorder", href: "#/live", title: "Videovigilancia",
      text: cams.online < cams.total ? `${cams.total - cams.online} sin señal` : "Todas las cámaras transmitiendo",
      num: `${cams.online}/${cams.total}`, numLabel: "en línea", bar: [[cams.online / cams.total, "var(--ok)"]] });
  }
  return `
  <section class="db-card ${span}">
    <div class="db-card-head"><h4><i class="mdi mdi-security" aria-hidden="true"></i>Seguridad perimetral</h4></div>
    ${rows.length ? `<div class="db-perim">${rows.map((r) => `
      <a class="db-sys ${r.cls}" href="${r.href}">
        <span class="db-sys-ico"><i class="mdi ${r.icon}" aria-hidden="true"></i></span>
        <span class="db-sys-body"><b>${esc(r.title)}</b><span>${esc(r.text)}</span>
          ${r.bar ? `<span class="db-sys-bar">${r.bar.map(([v, c]) => `<i style="width:${Math.max(0, v * 100).toFixed(1)}%;background:${c}"></i>`).join("")}</span>` : ""}
        </span>
        <span class="db-sys-num">${esc(String(r.num))}<small>${esc(r.numLabel)}</small></span>
      </a>`).join("")}</div>` : `<div class="db-empty">No hay sistemas de seguridad configurados.</div>`}
  </section>`;
}

// ---------------------------------------------------------------------------
// Salud del servidor: CPU, memoria y disco + servicios y licencia
// ---------------------------------------------------------------------------
function dbHealthHtml(d) {
  const s = d.server, lic = d.license;
  const gauge = (pct, label, sub) => {
    const v = Math.max(0, Math.min(100, pct || 0));
    const color = v >= 90 ? "var(--danger)" : v >= 75 ? "var(--warn)" : "var(--ok)";
    const len = Math.PI * 40;
    return `<div class="db-gauge">
      <svg viewBox="0 0 100 60" aria-label="${esc(label)} ${Math.round(v)} %">
        <path class="track" d="M10,52 A40,40 0 0 1 90,52" fill="none" stroke-width="9" stroke-linecap="round"/>
        <path d="M10,52 A40,40 0 0 1 90,52" fill="none" stroke="${color}" stroke-width="9" stroke-linecap="round"
          stroke-dasharray="${len}" stroke-dashoffset="${(len * (1 - v / 100)).toFixed(1)}"/>
        <text class="val" x="50" y="50" text-anchor="middle">${Math.round(v)}%</text>
      </svg>
      <div class="lbl">${esc(label)}</div><div class="sub">${esc(sub)}</div></div>`;
  };
  const failed = s.servicesFailed.length;
  return `
  <section class="db-card span-4">
    <div class="db-card-head"><h4><i class="mdi mdi-server" aria-hidden="true"></i>Salud del servidor</h4><a href="#/services">Servicios →</a></div>
    <div class="db-gauges">
      ${gauge(s.cpu, "CPU", "procesador")}
      ${gauge(s.ram, "Memoria", `${s.ramUsedGb.toFixed(1)} de ${s.ramTotalGb.toFixed(0)} GB`)}
      ${gauge(s.disk, "Disco", `${s.diskName || ""} ${s.diskUsedGb.toFixed(0)} de ${s.diskTotalGb.toFixed(0)} GB`.trim())}
    </div>
    <div class="db-health-list">
      <a class="db-health-row" href="#/services"><span><i class="mdi mdi-settings" aria-hidden="true"></i>Servicios</span>
        <b style="${failed ? "color:var(--danger)" : ""}">${s.servicesRunning} de ${s.servicesTotal} en ejecución${failed ? ` · ${failed} caído(s)` : ""}</b></a>
      ${lic ? `<a class="db-health-row" href="#/license"><span><i class="mdi mdi-certificate" aria-hidden="true"></i>Licencia</span>${licenseStateTag(lic.state)}</a>` : ""}
      ${d.sessions != null ? `<a class="db-health-row" href="#/sessions"><span><i class="mdi mdi-video" aria-hidden="true"></i>Sesiones de video</span><b>${d.sessions}</b></a>` : ""}
      <div class="db-health-row"><span><i class="mdi mdi-clock" aria-hidden="true"></i>Hora del servidor (UTC)</span><b>${new Date(d.generatedAt).toISOString().slice(11, 16)}</b></div>
    </div>
  </section>`;
}

// ---------------------------------------------------------------------------
// Equipos: dona de disponibilidad + desglose por tipo
// ---------------------------------------------------------------------------
function dbEquipmentHtml(d, a) {
  const e = d.equipment;
  const rows = [
    ["Cámaras", "mdi-camcorder", e.cameras, "#/devices"],
    ["Equipos de video", "mdi-harddisk", e.video, "#/devices"],
    ["Control de acceso", "mdi-account-card-details", e.access, "#/access"],
    ["Paneles de alarma", "mdi-security-home", e.alarmPanels, "#/alarm-panels"],
    ["Paneles de cerco", "mdi-flash", e.fence, "#/cerco-panels"],
    ["Citofonía", "mdi-phone-classic", e.intercoms, "#/intercoms"],
    ["Parlantes IP", "mdi-bullhorn", e.speakers, "#/speakers"],
    ["Módulos de E/S", "mdi-toggle-switch", e.ioModules, "#/io-modules"],
  ].filter((r) => r[2].total);
  const offline = a.total - a.online;
  const R = 52, C = 2 * Math.PI * R;
  const onLen = a.total ? (a.online / a.total) * C : 0;
  return `
  <section class="db-card span-4">
    <div class="db-card-head"><h4><i class="mdi mdi-monitor-multiple" aria-hidden="true"></i>Equipos</h4><a href="#/devices">Ver equipos →</a></div>
    ${rows.length ? `<div class="db-donut-wrap">
      <div class="db-donut">
        <svg viewBox="0 0 120 120" aria-label="${a.online} de ${a.total} equipos en línea">
          <circle cx="60" cy="60" r="${R}" fill="none" stroke="rgba(var(--muted-rgb), .15)" stroke-width="12"/>
          ${offline ? `<circle cx="60" cy="60" r="${R}" fill="none" stroke="var(--danger)" stroke-width="12" stroke-dasharray="${C - onLen} ${C}" stroke-dashoffset="${-onLen}"/>` : ""}
          <circle cx="60" cy="60" r="${R}" fill="none" stroke="var(--ok)" stroke-width="12" stroke-dasharray="${onLen} ${C}"/>
        </svg>
        <div class="db-donut-center"><b>${a.online}</b><small>de ${a.total} en línea</small></div>
      </div>
      <div class="db-breakdown">${rows.map(([label, icon, g, href]) => `
        <a class="db-breakdown-row" href="${href}">
          <div class="top"><span><i class="mdi ${icon}" aria-hidden="true"></i>${esc(label)}</span><b>${g.online}/${g.total}</b></div>
          <div class="db-meter"><i style="width:${((g.online / g.total) * 100).toFixed(1)}%"></i></div>
        </a>`).join("")}</div>
    </div>` : `<div class="db-empty">Todavía no hay equipos agregados.</div>`}
  </section>`;
}

// ---------------------------------------------------------------------------
// Últimos eventos relevantes (alarmas, cerco, denegados, patentes, alertas)
// ---------------------------------------------------------------------------
function dbFeedHtml(d) {
  if (!d.activity) return "";
  return `
  <section class="db-card span-4 db-feed-card">
    <div class="db-card-head"><h4><i class="mdi mdi-format-list-bulleted" aria-hidden="true"></i>Últimos eventos</h4>${Perms.can("events.attend") ? `<a href="#/event-center">Centro de eventos →</a>` : ""}</div>
    ${d.feed.length ? `<ul class="db-feed">${d.feed.map((f) => `
      <li class="db-src-${f.source}">
        <span class="db-feed-ico"><i class="mdi ${DB_SOURCE_ICON[f.source] || "mdi-information"}" aria-hidden="true"></i></span>
        <span class="db-feed-body"><b title="${esc(f.title)}">${esc(f.title || "Evento")}</b><span title="${esc(f.detail)}">${esc(f.detail || "")}</span></span>
        <time datetime="${f.at}" title="${esc(formatDate(f.at))}">${dbAgo(f.at)}</time>
      </li>`).join("")}</ul>` : `<div class="db-empty"><i class="mdi mdi-check-circle-outline" style="font-size:28px;color:var(--ok)"></i><br>Sin eventos relevantes recientes.</div>`}
  </section>`;
}

// ---------------------------------------------------------------------------
// Requieren atención: equipos sin conexión, lo caído hace menos primero
// ---------------------------------------------------------------------------
const DB_KIND_ICON = {
  video: "mdi-harddisk", camera: "mdi-camcorder", access: "mdi-account-card-details", alarm: "mdi-security-home",
  fence: "mdi-flash", intercom: "mdi-phone-classic", speaker: "mdi-bullhorn", io: "mdi-toggle-switch",
};

function dbOfflineHtml(d) {
  const o = d.offline;
  const more = o.total - o.items.length;
  return `
  <section class="db-card span-4">
    <div class="db-card-head">
      <h4><i class="mdi mdi-lan-disconnect" aria-hidden="true"></i>Requieren atención</h4>
      ${o.total ? `<span class="tag off">${o.total} sin conexión</span>` : ""}
    </div>
    ${o.total ? `<ul class="db-list">${o.items.map((x) => `
      <li><a href="${x.href}">
        <span class="db-list-ico off"><i class="mdi ${DB_KIND_ICON[x.kind] || "mdi-lan-disconnect"}" aria-hidden="true"></i></span>
        <span class="db-list-body"><b title="${esc(x.name)}">${esc(x.name)}</b><span title="${esc(x.detail)}">${esc(x.detail)}</span></span>
        <span class="db-list-side">${x.lastSeenAt
          ? `<time datetime="${x.lastSeenAt}" title="Última vez en línea: ${esc(formatDate(x.lastSeenAt))}">${dbAgo(x.lastSeenAt)}</time>`
          : `<span class="muted">sin registro</span>`}</span>
      </a></li>`).join("")}</ul>
      ${more > 0 ? `<div class="db-list-more">y ${more} ${dbPlural(more, "equipo más", "equipos más")} sin conexión</div>` : ""}`
    : `<div class="db-empty db-all-ok"><i class="mdi mdi-check-circle-outline" aria-hidden="true"></i><b>Todo en línea</b>Ningún equipo ha perdido la conexión.</div>`}
  </section>`;
}

// ---------------------------------------------------------------------------
// Puntos más activos de las últimas 24 h: dónde se concentra lo que pasa
// ---------------------------------------------------------------------------
const DB_SOURCE_COLOR = { alarm: "var(--danger)", fence: "var(--warn)", access: "var(--accent)", plate: "var(--violet)" };

function dbHotspotsHtml(d) {
  const list = d.hotspots;
  const max = Math.max(1, ...list.map((h) => h.count));
  return `
  <section class="db-card span-4">
    <div class="db-card-head"><h4><i class="mdi mdi-radar" aria-hidden="true"></i>Puntos más activos · 24 h</h4></div>
    ${list.length ? `<ol class="db-rank">${list.map((h, k) => `
      <li><a href="${h.href}" class="db-src-${h.source}">
        <span class="db-rank-pos">${k + 1}</span>
        <span class="db-rank-body">
          <span class="top"><b title="${esc(h.name)}">${esc(h.name || "—")}</b><span class="count">${h.count.toLocaleString("es-CL")}</span></span>
          <span class="db-rank-bar"><i style="width:${((h.count / max) * 100).toFixed(1)}%;background:${DB_SOURCE_COLOR[h.source] || "var(--accent)"}"></i></span>
          <span class="sub"><i class="mdi ${DB_SOURCE_ICON[h.source] || "mdi-information"}" aria-hidden="true"></i>${esc(h.detail || "")}${h.denied ? ` · <span class="bad">${h.denied} ${dbPlural(h.denied, "rechazo", "rechazos")}</span>` : ""}</span>
        </span>
      </a></li>`).join("")}</ol>`
    : `<div class="db-empty db-all-ok quiet"><i class="mdi mdi-radar" aria-hidden="true"></i><b>Sin actividad</b>No hubo eventos en las últimas 24 horas.</div>`}
  </section>`;
}

// ---------------------------------------------------------------------------
// Interacción: leyenda que oculta series y globo con el detalle de cada hora
// ---------------------------------------------------------------------------
function dashboardBind() {
  $$("#view .db-legend-item").forEach((btn) => btn.addEventListener("click", () => {
    const key = btn.dataset.key;
    if (dashboardHidden.has(key)) dashboardHidden.delete(key); else dashboardHidden.add(key);
    btn.classList.toggle("off", dashboardHidden.has(key));
    btn.setAttribute("aria-pressed", String(!dashboardHidden.has(key)));
    $("#db-chart").innerHTML = dbBarsSvg(dashboardData, $("#db-chart").clientWidth);
  }));
  const chart = $("#db-chart");
  if (!chart) return;
  chart.innerHTML = dbBarsSvg(dashboardData, chart.clientWidth);
  new ResizeObserver(() => {
    if (chart.isConnected && Math.abs(chart.clientWidth - (chart._w || 0)) > 4) {
      chart._w = chart.clientWidth;
      chart.innerHTML = dbBarsSvg(dashboardData, chart.clientWidth);
    }
  }).observe(chart);
  chart.addEventListener("mousemove", (ev) => {
    const col = ev.target.closest?.(".col");
    let tip = chart.querySelector(".db-tip");
    if (!col) { tip?.remove(); return; }
    const k = Number(col.dataset.k), act = dashboardData.activity;
    const start = new Date(new Date(act.from).getTime() + k * 3600000);
    const end = new Date(start.getTime() + 3600000);
    const rows = DB_SERIES.filter((s) => act.series[s.key] && !dashboardHidden.has(s.key));
    if (!tip) { tip = document.createElement("div"); tip.className = "db-tip"; chart.append(tip); }
    const hh = (t) => String(t.getHours()).padStart(2, "0") + ":00";
    tip.innerHTML = `<b>${hh(start)} – ${hh(end)}</b>` +
      rows.map((s) => `<div><span><i style="background:${s.color}"></i>${esc(s.label)}</span><span>${act.series[s.key][k]}</span></div>`).join("");
    const box = chart.getBoundingClientRect(), r = col.getBoundingClientRect();
    tip.style.left = Math.min(box.width - 80, Math.max(80, r.left - box.left + r.width / 2)) + "px";
    tip.style.top = (ev.clientY - box.top) + "px";
  });
  chart.addEventListener("mouseleave", () => chart.querySelector(".db-tip")?.remove());
}

// ---------------------------------------------------------------------------
// Utilidades
// ---------------------------------------------------------------------------
function dbRingSvg(pct) {
  const R = 64, C = 2 * Math.PI * R;
  return `<svg viewBox="0 0 148 148" aria-hidden="true">
    <circle class="track" cx="74" cy="74" r="${R}" fill="none" stroke-width="10"/>
    <circle class="bar" cx="74" cy="74" r="${R}" fill="none" stroke-width="10" stroke-linecap="round"
      stroke-dasharray="${C.toFixed(1)}" style="stroke-dashoffset:${C.toFixed(1)}" data-offset="${(C * (1 - pct / 100)).toFixed(1)}"/>
  </svg>`;
}

function dbPlural(n, one, many) { return n === 1 ? one : many; }
function dbCapitalize(s) { return s ? s[0].toUpperCase() + s.slice(1) : s; }

function dbDuration(seconds) {
  const d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
  return d ? `${d} d ${h} h` : h ? `${h} h ${m} min` : `${m} min`;
}

function dbAgo(iso) {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return "ahora";
  if (s < 3600) return `hace ${Math.floor(s / 60)} min`;
  if (s < 86400) return `hace ${Math.floor(s / 3600)} h`;
  return new Date(iso).toLocaleDateString("es-CL", { day: "numeric", month: "short" });
}
