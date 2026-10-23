// CLR TrueCentral VMS — panel: Flota → Recorridos (#/fleet/track?v=ID).
//
// Por dónde anduvo un vehículo entre dos horas: la línea coloreada por velocidad, las detenciones, el resumen (distancia,
// velocidad máxima, tiempo en marcha), una animación que mueve el vehículo por el recorrido (1× a 64×) y, en cualquier punto,
// el video grabado en el grabador del vehículo a esa hora (las mismas sesiones de Reproducción). Exporta CSV, KML y GPX.
// Permiso «Ver recorridos» (fleet.history): el recorrido es dato personal del conductor y la consulta queda en la bitácora.
//
// Usa el mapa de fleet-map.js (fmLoadLib, fmStyle) y la sesión de reproducción de playback.js (PbSession).
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

const ft = {
  active: false,
  map: null,
  vehicles: [],
  vehicle: null,
  points: [],          // { at (ms), lat, lon, speed, heading }
  stops: [],
  marker: null,
  idx: 0,
  playing: false,
  rate: 8,
  timer: null,
  feeds: [],
  videoSeq: 0,         // cerrar o pedir otro video deja de abrir las cámaras que faltaban
};

const FT_STOP_MIN_SECONDS = 180;   // detención: 3 min o más casi sin moverse
const FT_STOP_RADIUS_M = 60;
const FT_SPEED_COLORS = [[0, "#8a8f98"], [5, "#2f9e44"], [50, "#e8a33d"], [90, "#e03131"]]; // km/h desde → color

function ftDist(a, b) {
  const R = 6371000, toRad = (d) => d * Math.PI / 180;
  const dLat = toRad(b.lat - a.lat), dLon = toRad(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(toRad(a.lat)) * Math.cos(toRad(b.lat)) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.sqrt(h));
}

function ftLocalInput(d) {
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

function ftFmtTime(ms) { return new Date(ms).toLocaleString("es-CL", { dateStyle: "short", timeStyle: "medium" }); }
function ftFmtDur(s) { const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60); return h ? `${h} h ${m} min` : `${m} min`; }

async function renderFleetTrack() {
  fleetTrackLeave();
  ft.active = true;
  $("#page-title").textContent = "Recorridos";
  $("#view").className = "view-live";
  const params = new URLSearchParams(location.hash.split("?")[1] || "");
  const now = new Date(), from = new Date(now.getTime() - 4 * 3600 * 1000);
  $("#view").innerHTML = `
    <div class="fm-root">
      <aside class="fm-side ft-side">
        <div class="fm-side-head"><span class="lv-cap">RECORRIDO</span></div>
        <div class="ft-form">
          <label>Vehículo<select id="ft-vehicle"></select></label>
          <label>Desde<input type="datetime-local" id="ft-from" value="${ftLocalInput(from)}"></label>
          <label>Hasta<input type="datetime-local" id="ft-to" value="${ftLocalInput(now)}"></label>
          <button type="button" class="btn" id="ft-load">Ver recorrido</button>
        </div>
        <div class="ft-summary" id="ft-summary"><div class="muted fm-small">Elija un vehículo y el rango (hasta 7 días).</div></div>
        <div class="ft-stops" id="ft-stops"></div>
        <div class="ft-export" id="ft-export" hidden>
          <span class="muted fm-small">Exportar:</span>
          <button type="button" class="btn ghost small" data-fmt="csv">CSV</button>
          <button type="button" class="btn ghost small" data-fmt="kml">KML</button>
          <button type="button" class="btn ghost small" data-fmt="gpx">GPX</button>
        </div>
      </aside>
      <div class="fm-main">
        <div class="fm-map" id="ft-map"></div>
        <div class="ft-video" id="ft-video" hidden></div>
        <div class="ft-bar" id="ft-bar" hidden>
          <button type="button" class="btn small" id="ft-play" title="Reproducir el recorrido">▶</button>
          <select id="ft-rate" title="Velocidad de la animación">${[1, 4, 8, 16, 32, 64].map((r) => `<option value="${r}" ${r === ft.rate ? "selected" : ""}>${r}×</option>`).join("")}</select>
          <input type="range" id="ft-slider" min="0" max="0" value="0">
          <span class="ft-now" id="ft-now"></span>
          <button type="button" class="btn ghost small" id="ft-video-btn" title="Video grabado en el vehículo a esta hora">Ver video aquí</button>
        </div>
      </div>
    </div>`;

  try { ft.vehicles = (await Api.get("/api/fleet/vehicles")).filter((v) => v.enabled); }
  catch (err) { $("#view").innerHTML = `<div class="error-box" style="margin:24px">${esc(err.error)}</div>`; return; }
  if (!ft.active) return;
  $("#ft-vehicle").innerHTML = ft.vehicles.map((v) => `<option value="${v.id}">${esc(v.code)}${v.plate ? ` · ${esc(v.plate)}` : ""}</option>`).join("");
  if (params.get("v")) $("#ft-vehicle").value = params.get("v");
  $("#ft-load").addEventListener("click", ftLoad);
  $("#ft-play").addEventListener("click", ftTogglePlay);
  $("#ft-rate").addEventListener("change", (e) => { ft.rate = Number(e.target.value); });
  $("#ft-slider").addEventListener("input", (e) => { ftPause(); ftSeek(Number(e.target.value)); });
  $("#ft-video-btn").addEventListener("click", ftOpenVideo);
  $$("#ft-export [data-fmt]").forEach((b) => b.addEventListener("click", () => ftExport(b.dataset.fmt)));
  $("#ft-stops").addEventListener("click", (e) => {
    const row = e.target.closest("[data-i]");
    if (row) { ftPause(); ftSeek(Number(row.dataset.i)); ft.map?.flyTo({ center: [ft.points[ft.idx].lon, ft.points[ft.idx].lat], zoom: 16 }); }
  });

  try {
    const lib = await fmLoadLib();
    const settings = await Api.get("/api/fleet/map/settings");
    if (!ft.active) return;
    ft.map = new lib.Map({ container: "ft-map", style: await fmStyle(settings), center: [settings.centerLongitude, settings.centerLatitude],
      zoom: settings.zoom, attributionControl: { compact: true } });
    ft.map.addControl(new lib.NavigationControl({ showCompass: false }), "top-left");
    ft.map.addControl(new lib.ScaleControl({ unit: "metric" }), "bottom-right");
    ft.map.on("load", () => { if (params.get("v")) ftLoad(); });
  } catch (err) {
    $("#ft-map").innerHTML = `<div class="error-box" style="margin:24px">No se pudo iniciar el mapa: ${esc(err.message)}</div>`;
  }
}

function fleetTrackLeave() {
  if (!ft.active && !ft.map) return;
  ft.active = false;
  ftPause();
  ftCloseVideo();
  try { ft.map?.remove(); } catch { /* ya liberado */ }
  ft.map = null;
  ft.marker = null;
}

async function ftLoad() {
  ftPause();
  ftCloseVideo();
  const id = Number($("#ft-vehicle").value);
  ft.vehicle = ft.vehicles.find((v) => v.id === id);
  const from = new Date($("#ft-from").value), to = new Date($("#ft-to").value);
  if (!ft.vehicle || isNaN(from) || isNaN(to) || to <= from) { toast("Elija el vehículo y un rango válido.", true); return; }
  $("#ft-summary").innerHTML = `<div class="muted fm-small">Buscando posiciones…</div>`;
  let pts;
  try { pts = await Api.get(`/api/fleet/vehicles/${id}/track?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`); }
  catch (err) { $("#ft-summary").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  ft.points = pts.map((p) => ({ at: new Date(p.at).getTime(), lat: p.latitude, lon: p.longitude, speed: p.speedKmh, heading: p.headingDeg }));
  ft.stops = ftFindStops(ft.points);
  ftRenderSummary();
  ftDraw();
  $("#ft-bar").hidden = ft.points.length < 2;
  $("#ft-export").hidden = ft.points.length === 0;
  $("#ft-slider").max = Math.max(0, ft.points.length - 1);
  ftSeek(0);
}

/** Detenciones: tramos de 3 min o más en que el vehículo no se alejó más de 60 m. */
function ftFindStops(pts) {
  const stops = [];
  let i = 0;
  while (i < pts.length) {
    let j = i;
    while (j + 1 < pts.length && ftDist(pts[i], pts[j + 1]) <= FT_STOP_RADIUS_M) j++;
    const secs = (pts[j].at - pts[i].at) / 1000;
    if (secs >= FT_STOP_MIN_SECONDS) { stops.push({ i, j, from: pts[i].at, to: pts[j].at, lat: pts[i].lat, lon: pts[i].lon, secs }); i = j + 1; }
    else i++;
  }
  return stops;
}

function ftRenderSummary() {
  const p = ft.points;
  if (!p.length) {
    $("#ft-summary").innerHTML = `<div class="muted fm-small">Sin posiciones en ese rango.</div>`;
    $("#ft-stops").innerHTML = "";
    return;
  }
  let dist = 0, moving = 0, max = 0;
  for (let k = 1; k < p.length; k++) {
    const d = ftDist(p[k - 1], p[k]);
    const dt = (p[k].at - p[k - 1].at) / 1000;
    if (dt > 0 && dt < 600 && d / dt < 70) { dist += d; if (p[k].speed >= 3) moving += dt; }   // saltos imposibles (>250 km/h) no suman
    max = Math.max(max, p[k].speed);
  }
  $("#ft-summary").innerHTML = `
    <div class="fm-stats">
      <div><span class="muted">Distancia</span><b>${(dist / 1000).toFixed(1)} km</b></div>
      <div><span class="muted">Máxima</span><b>${Math.round(max)} km/h</b></div>
      <div><span class="muted">En marcha</span><b>${ftFmtDur(moving)}</b></div>
    </div>
    <div class="muted fm-small">${p.length} posiciones · ${ftFmtTime(p[0].at)} a ${ftFmtTime(p[p.length - 1].at)}</div>`;
  $("#ft-stops").innerHTML = ft.stops.length
    ? `<div class="lv-cap" style="padding:8px 0 4px">DETENCIONES (${ft.stops.length})</div>` + ft.stops.map((s) => `
      <div class="fm-row" data-i="${s.i}"><i class="fm-dot stop"></i><div class="fm-row-text">
        <div class="fm-row-top"><b>${new Date(s.from).toLocaleTimeString("es-CL", { timeStyle: "short" })} – ${new Date(s.to).toLocaleTimeString("es-CL", { timeStyle: "short" })}</b><span>${ftFmtDur(s.secs)}</span></div>
      </div></div>`).join("")
    : `<div class="muted fm-small" style="padding-top:8px">Sin detenciones de 3 minutos o más.</div>`;
}

function ftColor(speed) {
  let c = FT_SPEED_COLORS[0][1];
  for (const [from, color] of FT_SPEED_COLORS) if (speed >= from) c = color;
  return c;
}

function ftDraw() {
  if (!ft.map) return;
  const segs = [];
  for (let k = 1; k < ft.points.length; k++) {
    const a = ft.points[k - 1], b = ft.points[k];
    segs.push({ type: "Feature", properties: { color: ftColor(b.speed) }, geometry: { type: "LineString", coordinates: [[a.lon, a.lat], [b.lon, b.lat]] } });
  }
  const stops = ft.stops.map((s) => ({ type: "Feature", properties: {}, geometry: { type: "Point", coordinates: [s.lon, s.lat] } }));
  const set = (id, data, layer) => {
    if (ft.map.getSource(id)) ft.map.getSource(id).setData(data);
    else { ft.map.addSource(id, { type: "geojson", data }); ft.map.addLayer(layer); }
  };
  set("ft-line", { type: "FeatureCollection", features: segs },
    { id: "ft-line", type: "line", source: "ft-line", paint: { "line-color": ["get", "color"], "line-width": 5 }, layout: { "line-cap": "round", "line-join": "round" } });
  set("ft-stops", { type: "FeatureCollection", features: stops },
    { id: "ft-stops", type: "circle", source: "ft-stops", paint: { "circle-radius": 7, "circle-color": "#e8a33d", "circle-stroke-color": "#fff", "circle-stroke-width": 2 } });
  if (ft.points.length) {
    const lons = ft.points.map((p) => p.lon), lats = ft.points.map((p) => p.lat);
    ft.map.fitBounds([[Math.min(...lons), Math.min(...lats)], [Math.max(...lons), Math.max(...lats)]], { padding: 80, maxZoom: 16, duration: 0 });
  }
  if (!ft.marker && fm.lib) {
    const el = document.createElement("div");
    el.className = "fm-mk move sel";
    ft.marker = new fm.lib.Marker({ element: el, anchor: "center" }).setLngLat([0, 0]).addTo(ft.map);
  }
}

function ftSeek(i) {
  if (!ft.points.length) return;
  ft.idx = Math.max(0, Math.min(i, ft.points.length - 1));
  const p = ft.points[ft.idx];
  $("#ft-slider").value = ft.idx;
  $("#ft-now").textContent = `${ftFmtTime(p.at)} · ${Math.round(p.speed)} km/h`;
  if (ft.marker) {
    ft.marker.setLngLat([p.lon, p.lat]);
    ft.marker.getElement().innerHTML = `<svg viewBox="0 0 24 24" width="24" height="24" style="transform:rotate(${Math.round(p.heading)}deg)"><path d="M12 2l7 18-7-4-7 4z"/></svg><span>${esc(ft.vehicle?.code ?? "")}</span>`;
  }
}

/** La animación avanza por tiempo real del recorrido multiplicado por la velocidad elegida (un tic cada 200 ms). */
function ftTogglePlay() { ft.playing ? ftPause() : ftPlay(); }

function ftPlay() {
  if (ft.points.length < 2) return;
  if (ft.idx >= ft.points.length - 1) ftSeek(0);
  ft.playing = true;
  $("#ft-play").textContent = "⏸";
  let clock = ft.points[ft.idx].at;
  ft.timer = setInterval(() => {
    clock += 200 * ft.rate;
    let i = ft.idx;
    while (i + 1 < ft.points.length && ft.points[i + 1].at <= clock) i++;
    // Un hueco largo (vehículo apagado) se salta en vez de esperar.
    if (i === ft.idx && ft.idx + 1 < ft.points.length && ft.points[ft.idx + 1].at - clock > 300000) { i++; clock = ft.points[i].at; }
    if (i !== ft.idx) ftSeek(i);
    if (ft.idx >= ft.points.length - 1) ftPause();
  }, 200);
}

function ftPause() {
  ft.playing = false;
  clearInterval(ft.timer);
  const b = $("#ft-play");
  if (b) b.textContent = "▶";
}

/** Video grabado en el vehículo a la hora del punto actual: sus cámaras (hasta 4) desde ese momento, a 1×. */
function ftOpenVideo() {
  ftCloseVideo();
  const v = ft.vehicle, p = ft.points[ft.idx];
  if (!v || !p) return;
  if (!Perms.can("playback.view")) { toast("Sus roles no incluyen ver grabaciones.", true); return; }
  if (typeof PbSession !== "function") return;
  const chans = v.channels.filter((c) => c.enabled).slice(0, 4);
  const box = $("#ft-video");
  box.hidden = false;
  box.innerHTML = `<div class="fm-card-head"><b>${esc(v.code)} · grabación desde ${ftFmtTime(p.at)}</b>
      <button type="button" class="lv-icon-btn" id="ft-video-close" title="Cerrar">✕</button></div>
    <div class="fm-cams">${chans.map((c) => `<div class="fm-cam loading" data-ch="${c.id}"><video muted playsinline></video><span>${esc(c.name)}</span></div>`).join("")}</div>
    <div class="muted fm-small">El video viene del disco del grabador por su 4G: puede tardar unos segundos.</div>`;
  $("#ft-video-close").addEventListener("click", ftCloseVideo);
  ftOpenFeeds(chans, v, p.at, ft.videoSeq);
}

/** De a una cámara: el grabador 2.0/4.0 atiende una reproducción a la vez hasta abrir su enlace (el servidor las pone en fila). */
async function ftOpenFeeds(chans, v, at, seq) {
  for (const c of chans) {
    if (seq !== ft.videoSeq) return;
    const tile = $("#ft-video").querySelector(`.fm-cam[data-ch="${c.id}"]`);
    const s = new PbSession({ deviceId: v.deviceId, rtspChannel: c.rtspChannel, name: c.name }, at, at + 30 * 60 * 1000, 1);
    ft.feeds.push(s);
    try {
      await s.open();
      const video = tile.querySelector("video");
      video.srcObject = s.stream;
      video.play().catch(() => { /* sin gesto: igual se ve en silencio */ });
      tile.classList.remove("loading");
    } catch (err) {
      tile.classList.remove("loading");
      if (!s.closed) tile.querySelector("span").textContent = `${c.name} · ${err?.error || "sin grabación"}`;
    }
  }
}

function ftCloseVideo() {
  ft.videoSeq++;
  ft.feeds.forEach((s) => s.close());
  ft.feeds = [];
  const box = $("#ft-video");
  if (box) { box.hidden = true; box.innerHTML = ""; }
}

/** Exportación local del recorrido (lo que se ve: mismos puntos). */
function ftExport(fmt) {
  const v = ft.vehicle, p = ft.points;
  if (!v || !p.length) return;
  const iso = (ms) => new Date(ms).toISOString();
  let text, type;
  if (fmt === "csv") {
    text = "hora_utc;latitud;longitud;velocidad_kmh;rumbo\n" + p.map((x) => `${iso(x.at)};${x.lat.toFixed(6)};${x.lon.toFixed(6)};${x.speed.toFixed(1)};${Math.round(x.heading)}`).join("\n");
    type = "text/csv";
  } else if (fmt === "kml") {
    text = `<?xml version="1.0" encoding="UTF-8"?>\n<kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>${esc(v.code)}</name>` +
      `<Placemark><name>${esc(v.code)} ${iso(p[0].at)}</name><LineString><tessellate>1</tessellate><coordinates>` +
      p.map((x) => `${x.lon.toFixed(6)},${x.lat.toFixed(6)},0`).join(" ") + `</coordinates></LineString></Placemark></Document></kml>`;
    type = "application/vnd.google-earth.kml+xml";
  } else {
    text = `<?xml version="1.0" encoding="UTF-8"?>\n<gpx version="1.1" creator="TrueCentral VMS" xmlns="http://www.topografix.com/GPX/1/1"><trk><name>${esc(v.code)}</name><trkseg>` +
      p.map((x) => `<trkpt lat="${x.lat.toFixed(6)}" lon="${x.lon.toFixed(6)}"><time>${iso(x.at)}</time></trkpt>`).join("") + `</trkseg></trk></gpx>`;
    type = "application/gpx+xml";
  }
  const name = `recorrido_${v.code}_${new Date(p[0].at).toISOString().slice(0, 16).replace(/[:T]/g, "-")}.${fmt}`;
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([text], { type }));
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  Api.post("/api/audit/client-event", { action: "fleet-track-exported", deviceName: v.code, filePath: name,
    detail: `${p.length} posiciones, panel web` }).catch(() => { /* la auditoría nunca bloquea */ });
}
