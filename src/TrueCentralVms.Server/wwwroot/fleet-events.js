// CLR TrueCentral VMS — panel: Flota → Mapa, fase 4: eventos de los vehículos y geocercas.
//
// Eventos: pestaña «Eventos» del mapa (pendientes primero, atender con nota, ver el vehículo), aviso inmediato de los críticos
// (pánico, choque, volcamiento) y marcador en rojo mientras el vehículo tenga eventos sin atender. Llegan por el hub
// (FleetEvent / FleetEventAcknowledged), recortados al alcance.
// Geocercas: se dibujan sobre el mapa (polígono, círculo, corredor); quien administra la flota (fleet.manage) las crea y edita
// aquí mismo haciendo clic en el mapa. El servidor las evalúa con cada posición (FleetEventService).
//
// Lo usa fleet-map.js (fme* = «fleet map events»). Se carga ANTES que app.js: solo declara funciones.
"use strict";

const fme = {
  tab: "vehicles",
  events: [],
  pendingOnly: true,
  pending: 0,
  fences: [],
  hub: null,
  draw: null,       // { kind, points: [[lon,lat]], fence (al redibujar) }
};

const FME_SEV = ["info", "warning", "critical"];   // AlarmSeverity { Info, Warning, Critical } (número en el hub, texto en la API)
const fmeSev = (s) => (typeof s === "number" ? FME_SEV[s] : String(s || "").toLowerCase()) || "info";

/** Lo llama renderFleetMap una vez armado el mapa: carga eventos y geocercas y se suscribe al hub. */
async function fmeAttach() {
  fme.tab = "vehicles";
  fme.draw = null;
  try {
    const [page, fences] = await Promise.all([Api.get("/api/fleet/events?take=200"), Api.get("/api/fleet/geofences")]);
    fme.events = page.events;
    fme.pending = page.pending;
    fme.fences = fences;
  } catch { fme.events = []; fme.fences = []; }
  if (!fm.active) return;
  fmeRenderTabs();
  fmeDrawFences();
  fmRenderAll();   // marcadores en rojo de los vehículos con eventos sin atender
  fme.hub = {
    event: (e) => {
      if (!fm.active) return;
      fme.events = [e, ...fme.events.filter((x) => x.id !== e.id)].slice(0, 500);
      if (!e.acknowledgedAt) fme.pending++;
      if (fmeSev(e.severity) === "critical")
        toast(`${e.label} · ${e.vehicleCode}${e.speedKmh != null ? ` · ${Math.round(e.speedKmh)} km/h` : ""}`, true);
      fmeRenderTabs();
      fmRenderAll();
    },
    acked: (e) => {
      if (!fm.active) return;
      const i = fme.events.findIndex((x) => x.id === e.id);
      if (i >= 0 && !fme.events[i].acknowledgedAt) fme.pending = Math.max(0, fme.pending - 1);
      if (i >= 0) fme.events[i] = e;
      fmeRenderTabs();
      fmRenderAll();
    },
    config: async (topic) => {
      if (!fm.active || topic !== "fleet-geofences") return;
      try { fme.fences = await Api.get("/api/fleet/geofences"); fmeDrawFences(); fmeRenderTabs(); } catch { /* próximo aviso */ }
    },
  };
  VmsHub.on("FleetEvent", fme.hub.event);
  VmsHub.on("FleetEventAcknowledged", fme.hub.acked);
  VmsHub.on("ConfigChanged", fme.hub.config);
}

function fmeDetach() {
  if (!fme.hub) return;
  VmsHub.off("FleetEvent", fme.hub.event);
  VmsHub.off("FleetEventAcknowledged", fme.hub.acked);
  VmsHub.off("ConfigChanged", fme.hub.config);
  fme.hub = null;
  fme.draw = null;
}

/** Vehículos con algún evento sin atender (marcador en rojo). */
function fmeAlarmed(vehicleId) {
  return fme.events.some((e) => e.vehicleId === vehicleId && !e.acknowledgedAt && fmeSev(e.severity) !== "info");
}

// ---------------------------------------------------------------------------
// Pestañas del costado: Vehículos | Eventos | Geocercas
// ---------------------------------------------------------------------------

function fmeRenderTabs() {
  const tabs = $("#fm-tabs");
  if (!tabs) return;
  const canManage = Perms.can("fleet.manage");
  tabs.innerHTML = `
    <button type="button" data-tab="vehicles" class="${fme.tab === "vehicles" ? "on" : ""}">Vehículos</button>
    <button type="button" data-tab="events" class="${fme.tab === "events" ? "on" : ""}">Eventos${fme.pending ? ` <b class="fme-badge">${fme.pending}</b>` : ""}</button>
    <button type="button" data-tab="fences" class="${fme.tab === "fences" ? "on" : ""}">Geocercas</button>`;
  tabs.querySelectorAll("[data-tab]").forEach((b) => b.addEventListener("click", () => { fme.tab = b.dataset.tab; fmeRenderTabs(); }));
  $("#fm-vehicles-pane").hidden = fme.tab !== "vehicles";
  const pane = $("#fm-other-pane");
  pane.hidden = fme.tab === "vehicles";
  if (fme.tab === "events") fmeRenderEvents(pane);
  else if (fme.tab === "fences") fmeRenderFences(pane, canManage);
}

function fmeRenderEvents(pane) {
  const list = fme.events.filter((e) => !fme.pendingOnly || !e.acknowledgedAt);
  pane.innerHTML = `
    <label class="checkbox-row fme-only"><input type="checkbox" id="fme-pending" ${fme.pendingOnly ? "checked" : ""}> Solo sin atender (${fme.pending})</label>
    <div class="fm-list">${list.length ? list.map((e) => `
      <div class="fme-row ${fmeSev(e.severity)} ${e.acknowledgedAt ? "done" : ""}" data-e="${e.id}">
        <div class="fm-row-top"><b>${esc(e.label)}</b><span>${new Date(e.at).toLocaleTimeString("es-CL", { timeStyle: "short" })}</span></div>
        <div class="muted">${esc(e.vehicleCode)} · ${esc(e.description)}</div>
        ${e.acknowledgedAt ? `<div class="muted fm-small">Atendido por ${esc(e.acknowledgedBy || "el sistema")}${e.ackNote ? `: ${esc(e.ackNote)}` : ""}</div>` : ""}
        <div class="fme-actions">
          ${e.latitude != null ? `<button type="button" class="btn ghost small" data-act="see">Ver</button>` : ""}
          ${e.acknowledgedAt ? "" : `<button type="button" class="btn small" data-act="ack">Atender</button>`}
        </div>
      </div>`).join("") : `<div class="muted lv-small" style="padding:10px 14px">${fme.pendingOnly ? "No hay eventos sin atender." : "Sin eventos recientes."}</div>`}</div>`;
  $("#fme-pending").addEventListener("change", (ev) => { fme.pendingOnly = ev.target.checked; fmeRenderTabs(); });
  pane.querySelectorAll("[data-act]").forEach((b) => b.addEventListener("click", () => {
    const e = fme.events.find((x) => x.id === Number(b.closest("[data-e]").dataset.e));
    if (!e) return;
    if (b.dataset.act === "see") {
      fmSelect(e.vehicleId, false);
      fm.map?.flyTo({ center: [e.longitude, e.latitude], zoom: Math.max(fm.map.getZoom(), 15) });
    } else fmeAck(e);
  }));
}

function fmeAck(e) {
  openModal(`
    <h3>Atender «${esc(e.label)}»</h3>
    <div class="muted">${esc(e.vehicleCode)} · ${new Date(e.at).toLocaleString("es-CL")} · ${esc(e.description)}</div>
    <div id="fme-ack-error"></div>
    <div class="field" style="margin-top:10px"><label>Nota (opcional)</label>
      <textarea id="fme-note" rows="3" maxlength="512" placeholder="Qué se hizo: llamada al conductor, aviso a carabineros…"></textarea></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="fme-ack-cancel">Cancelar</button>
      <button class="btn" type="button" id="fme-ack-ok">Marcar atendido</button>
    </div>`);
  $("#fme-ack-cancel").addEventListener("click", closeModal);
  $("#fme-ack-ok").addEventListener("click", async () => {
    try {
      const done = await Api.post(`/api/fleet/events/${e.id}/ack`, { note: $("#fme-note").value.trim() || null });
      fme.hub?.acked(done);
      closeModal();
    } catch (err) { $("#fme-ack-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
  });
}

// ---------------------------------------------------------------------------
// Geocercas: dibujo en el mapa
// ---------------------------------------------------------------------------

/** Círculo como polígono de 64 lados (para dibujarlo). */
function fmeCircle(center, radiusM) {
  const [lon, lat] = center, pts = [];
  const dLat = radiusM / 110540, dLon = radiusM / (111320 * Math.cos(lat * Math.PI / 180));
  for (let i = 0; i <= 64; i++) { const a = (i / 64) * 2 * Math.PI; pts.push([lon + dLon * Math.cos(a), lat + dLat * Math.sin(a)]); }
  return pts;
}

function fmeFeatures() {
  const area = [], lines = [];
  for (const f of fme.fences) {
    const props = { id: f.id, name: f.name, color: f.color, enabled: f.enabled ? 1 : 0 };
    if (f.kind === "corridor") {
      // Ancho real en metros: el grosor de la línea se escala con el zoom (metros por píxel en esa latitud).
      lines.push({ type: "Feature", properties: { ...props, widthM: f.radiusM, lat: f.points[0][1] }, geometry: { type: "LineString", coordinates: f.points } });
    } else {
      const ring = f.kind === "circle" ? fmeCircle(f.points[0], f.radiusM) : [...f.points, f.points[0]];
      area.push({ type: "Feature", properties: props, geometry: { type: "Polygon", coordinates: [ring] } });
    }
  }
  return { area: { type: "FeatureCollection", features: area }, lines: { type: "FeatureCollection", features: lines } };
}

function fmeDrawFences() {
  const map = fm.map;
  if (!map) return;
  // Mientras el estilo termina de cargar (sus fuentes, los marcadores) no se pueden agregar capas: se reintenta al quedar quieto.
  if (!map.isStyleLoaded()) { map.once("idle", fmeDrawFences); return; }
  const { area, lines } = fmeFeatures();
  if (map.getSource("fme-area")) {
    map.getSource("fme-area").setData(area);
    map.getSource("fme-corr").setData(lines);
  } else {
    map.addSource("fme-area", { type: "geojson", data: area });
    map.addSource("fme-corr", { type: "geojson", data: lines });
    const before = map.getLayer("track") ? "track" : undefined;
    map.addLayer({ id: "fme-fill", type: "fill", source: "fme-area",
      paint: { "fill-color": ["get", "color"], "fill-opacity": ["case", ["==", ["get", "enabled"], 1], 0.14, 0.05] } }, before);
    map.addLayer({ id: "fme-edge", type: "line", source: "fme-area",
      paint: { "line-color": ["get", "color"], "line-width": 2, "line-dasharray": [3, 2] } }, before);
    // metros → píxeles: m / (156543.03 · cos(lat) / 2^zoom); interpolación exponencial base 2 entre zoom 0 y 22.
    const px = (z) => ["/", ["*", ["get", "widthM"], Math.pow(2, z)], ["*", 156543.03, ["cos", ["*", ["get", "lat"], Math.PI / 180]]]];
    map.addLayer({ id: "fme-corr", type: "line", source: "fme-corr",
      layout: { "line-cap": "round", "line-join": "round" },
      paint: { "line-color": ["get", "color"], "line-opacity": 0.22, "line-width": ["interpolate", ["exponential", 2], ["zoom"], 0, px(0), 22, px(22)] } }, before);
    map.addLayer({ id: "fme-corr-axis", type: "line", source: "fme-corr",
      paint: { "line-color": ["get", "color"], "line-width": 1.5, "line-dasharray": [3, 2] } }, before);
    map.addSource("fme-draft", { type: "geojson", data: { type: "FeatureCollection", features: [] } });
    map.addLayer({ id: "fme-draft-line", type: "line", source: "fme-draft", paint: { "line-color": "#e8590c", "line-width": 2.5 } });
    map.addLayer({ id: "fme-draft-pts", type: "circle", source: "fme-draft", filter: ["==", ["geometry-type"], "Point"],
      paint: { "circle-radius": 5, "circle-color": "#e8590c", "circle-stroke-color": "#fff", "circle-stroke-width": 2 } });
    map.on("click", fmeMapClick);
  }
}

function fmeRenderFences(pane, canManage) {
  pane.innerHTML = `
    ${canManage ? `<div class="fme-new"><span class="muted fm-small">Nueva:</span>
      <button type="button" class="btn ghost small" data-new="polygon">Polígono</button>
      <button type="button" class="btn ghost small" data-new="circle">Círculo</button>
      <button type="button" class="btn ghost small" data-new="corridor">Corredor</button></div>` : ""}
    ${fme.draw ? `<div class="fme-drawing">${fmeDrawHint()}
      <div class="fme-actions"><button type="button" class="btn ghost small" id="fme-draw-cancel">Cancelar</button>
      <button type="button" class="btn ghost small" id="fme-draw-undo">Deshacer punto</button>
      <button type="button" class="btn small" id="fme-draw-done">Listo</button></div></div>` : ""}
    <div class="fm-list">${fme.fences.length ? fme.fences.map((f) => `
      <div class="fm-row" data-f="${f.id}"><i class="fm-dot" style="background:${esc(f.color)}"></i><div class="fm-row-text">
        <div class="fm-row-top"><b>${esc(f.name)}</b><span>${f.enabled ? "" : "deshabilitada"}</span></div>
        <div class="muted">${esc(fmeDescribe(f))}</div>
        ${canManage ? `<div class="fme-actions"><button type="button" class="btn ghost small" data-act="edit">Editar</button>
          <button type="button" class="btn ghost small" data-act="redraw">Redibujar</button>
          <button type="button" class="btn ghost small danger" data-act="del">Eliminar</button></div>` : ""}
      </div></div>`).join("") : `<div class="muted lv-small" style="padding:10px 14px">No hay geocercas.${canManage ? " Cree una con los botones de arriba y haciendo clic en el mapa." : ""}</div>`}</div>`;
  pane.querySelectorAll("[data-new]").forEach((b) => b.addEventListener("click", () => fmeStartDraw(b.dataset.new, null)));
  $("#fme-draw-cancel")?.addEventListener("click", () => { fme.draw = null; fmeDraft(); fmeRenderTabs(); });
  $("#fme-draw-undo")?.addEventListener("click", () => { fme.draw?.points.pop(); fmeDraft(); fmeRenderTabs(); });
  $("#fme-draw-done")?.addEventListener("click", fmeFinishDraw);
  pane.querySelectorAll("[data-f]").forEach((row) => row.addEventListener("click", (ev) => {
    const f = fme.fences.find((x) => x.id === Number(row.dataset.f));
    const act = ev.target.closest("[data-act]")?.dataset.act;
    if (!f) return;
    if (act === "edit") fmeFenceModal(f.kind, f.points, f);
    else if (act === "redraw") fmeStartDraw(f.kind, f);
    else if (act === "del") fmeDelete(f);
    else fmeFit(f);
  }));
}

function fmeDescribe(f) {
  const shape = f.kind === "circle" ? `Círculo de ${Math.round(f.radiusM)} m` : f.kind === "corridor" ? `Corredor de ${Math.round(f.radiusM)} m` : `Polígono de ${f.points.length} vértices`;
  const alerts = [f.alertOnEnter && "entrada", f.alertOnExit && "salida", f.speedLimitKmh && `máx. ${f.speedLimitKmh} km/h`].filter(Boolean);
  const who = f.vehicleIds.length ? `${f.vehicleIds.length} vehículo(s)` : "toda la flota";
  return `${shape} · ${alerts.length ? "avisa " + alerts.join(", ") : "sin avisos"} · ${who}`;
}

function fmeFit(f) {
  if (!fm.map) return;
  const pts = f.kind === "circle" ? fmeCircle(f.points[0], f.radiusM) : f.points;
  const lons = pts.map((p) => p[0]), lats = pts.map((p) => p[1]);
  fm.map.fitBounds([[Math.min(...lons), Math.min(...lats)], [Math.max(...lons), Math.max(...lats)]], { padding: 80, maxZoom: 17 });
}

function fmeDrawHint() {
  const d = fme.draw, n = d.points.length;
  if (d.kind === "circle") return n === 0 ? "Haga clic en el CENTRO del círculo." : n === 1 ? "Haga clic en el BORDE (define el radio)." : "Círculo listo.";
  if (d.kind === "corridor") return `Haga clic a lo largo del camino (${n} punto/s; mínimo 2). Al terminar, «Listo».`;
  return `Haga clic en cada vértice (${n}; mínimo 3). Al terminar, «Listo».`;
}

function fmeStartDraw(kind, fence) {
  fme.tab = "fences";
  fme.draw = { kind, points: [], fence };
  fmeDraft();
  fmeRenderTabs();
  toast(kind === "circle" ? "Clic en el centro y después en el borde." : "Haga clic en el mapa para marcar los puntos.");
}

function fmeMapClick(ev) {
  const d = fme.draw;
  if (!d) return;
  if (d.kind === "circle" && d.points.length >= 2) return;
  d.points.push([Number(ev.lngLat.lng.toFixed(7)), Number(ev.lngLat.lat.toFixed(7))]);
  fmeDraft();
  fmeRenderTabs();
  if (d.kind === "circle" && d.points.length === 2) fmeFinishDraw();
}

function fmeDraft() {
  const src = fm.map?.getSource("fme-draft");
  if (!src) return;
  const d = fme.draw, feats = [];
  if (d) {
    for (const p of d.points) feats.push({ type: "Feature", properties: {}, geometry: { type: "Point", coordinates: p } });
    if (d.kind === "circle" && d.points.length === 2) {
      feats.push({ type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: fmeCircle(d.points[0], fmeMeters(d.points[0], d.points[1])) } });
    } else if (d.points.length >= 2) {
      const line = d.kind === "polygon" && d.points.length >= 3 ? [...d.points, d.points[0]] : d.points;
      feats.push({ type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: line } });
    }
  }
  src.setData({ type: "FeatureCollection", features: feats });
}

function fmeMeters(a, b) {
  const R = 6371000, r = (x) => x * Math.PI / 180;
  const h = Math.sin(r(b[1] - a[1]) / 2) ** 2 + Math.cos(r(a[1])) * Math.cos(r(b[1])) * Math.sin(r(b[0] - a[0]) / 2) ** 2;
  return 2 * R * Math.asin(Math.sqrt(h));
}

function fmeFinishDraw() {
  const d = fme.draw;
  if (!d) return;
  const need = d.kind === "polygon" ? 3 : 2;
  if (d.points.length < need) { toast(d.kind === "polygon" ? "Marque al menos 3 vértices." : "Marque al menos 2 puntos.", true); return; }
  let points = d.points, radius = d.fence?.radiusM ?? (d.kind === "corridor" ? 50 : 0);
  if (d.kind === "circle") { radius = Math.max(1, Math.round(fmeMeters(points[0], points[1]))); points = [points[0]]; }
  fmeFenceModal(d.kind, points, d.fence, radius);
}

/** Datos de la geocerca: nombre, avisos, velocidad, vehículos y color. Con <fence> edita (y si trae puntos nuevos, la redibuja). */
function fmeFenceModal(kind, points, fence, radius) {
  const r = radius ?? fence?.radiusM ?? 0;
  const vehicles = [...fm.vehicles.values()].sort((a, b) => a.code.localeCompare(b.code, "es", { numeric: true }));
  const chosen = new Set(fence?.vehicleIds ?? []);
  const kindLabel = kind === "circle" ? "círculo" : kind === "corridor" ? "corredor" : "polígono";
  openModal(`
    <h3>${fence ? "Geocerca «" + esc(fence.name) + "»" : "Nueva geocerca"} (${kindLabel})</h3>
    <div id="fme-f-error"></div>
    <div class="form-grid">
      <div class="field"><label>Nombre</label><input id="fme-name" maxlength="80" value="${esc(fence?.name ?? "")}" placeholder="Bodega Quilicura"></div>
      <div class="field"><label>Color</label><input id="fme-color" type="color" value="${esc(fence?.color ?? "#3b82f6")}"></div>
    </div>
    ${kind === "polygon" ? "" : `<div class="field"><label>${kind === "circle" ? "Radio (m)" : "Ancho del corredor (m)"}</label>
      <input id="fme-radius" type="number" min="1" max="${kind === "circle" ? 500000 : 10000}" value="${Math.round(r)}"></div>`}
    <label class="checkbox-row"><input type="checkbox" id="fme-enter" ${fence ? (fence.alertOnEnter ? "checked" : "") : "checked"}> Avisar cuando un vehículo ENTRA</label>
    <label class="checkbox-row"><input type="checkbox" id="fme-exit" ${fence ? (fence.alertOnExit ? "checked" : "") : "checked"}> Avisar cuando un vehículo SALE${kind === "corridor" ? " (se desvía de la ruta)" : ""}</label>
    <div class="field"><label>Velocidad máxima dentro (km/h, vacío = sin límite)</label>
      <input id="fme-speed" type="number" min="1" max="300" value="${fence?.speedLimitKmh ?? ""}"></div>
    <div class="field"><label>Vehículos (nada marcado = toda la flota)</label>
      <div class="fme-vehicles">${vehicles.map((v) => `<label class="checkbox-row"><input type="checkbox" data-veh="${v.id}" ${chosen.has(v.id) ? "checked" : ""}> ${esc(v.code)}${v.plate ? ` · ${esc(v.plate)}` : ""}</label>`).join("") || `<span class="muted">Sin vehículos.</span>`}</div></div>
    <label class="checkbox-row"><input type="checkbox" id="fme-enabled" ${fence?.enabled === false ? "" : "checked"}> Habilitada</label>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="fme-f-cancel">Cancelar</button>
      <button class="btn" type="button" id="fme-f-save">Guardar</button>
    </div>`, true);
  $("#fme-f-cancel").addEventListener("click", () => { closeModal(); fme.draw = null; fmeDraft(); fmeRenderTabs(); });
  $("#fme-f-save").addEventListener("click", async () => {
    const speed = $("#fme-speed").value.trim();
    const body = {
      name: $("#fme-name").value.trim(), kind, points, radiusM: kind === "polygon" ? 0 : Number($("#fme-radius").value),
      color: $("#fme-color").value, alertOnEnter: $("#fme-enter").checked, alertOnExit: $("#fme-exit").checked,
      speedLimitKmh: speed ? Number(speed) : null,
      vehicleIds: $$(".fme-vehicles [data-veh]").filter((c) => c.checked).map((c) => Number(c.dataset.veh)),
      enabled: $("#fme-enabled").checked,
    };
    try {
      const saved = fence ? await Api.put(`/api/fleet/geofences/${fence.id}`, body) : await Api.post("/api/fleet/geofences", body);
      fme.fences = [...fme.fences.filter((x) => x.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name, "es"));
      closeModal();
      fme.draw = null;
      fmeDraft();
      fmeDrawFences();
      fmeRenderTabs();
      toast(`Geocerca «${saved.name}» guardada.`);
    } catch (err) { $("#fme-f-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
  });
}

async function fmeDelete(f) {
  if (!confirm(`¿Eliminar la geocerca «${f.name}»? Las automatizaciones que la usen dejarán de dispararse con ella.`)) return;
  try {
    await Api.delete(`/api/fleet/geofences/${f.id}`);
    fme.fences = fme.fences.filter((x) => x.id !== f.id);
    fmeDrawFences();
    fmeRenderTabs();
  } catch (err) { toast(err.error, true); }
}
