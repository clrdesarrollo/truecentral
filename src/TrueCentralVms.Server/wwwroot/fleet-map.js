// CLR TrueCentral VMS — panel: Flota → Mapa (#/fleet/map).
//
// Vehículos sobre el mapa con su última posición GPS (llega por el hub cada pocos segundos), lista con filtros, ficha del
// vehículo con sus cámaras en vivo (las mismas sesiones WebRTC de la vista en vivo), seguir al vehículo y su recorrido de las
// últimas horas (permiso aparte: es dato personal del conductor).
//
// Mapa: MapLibre GL (BSD-3, vendor/maplibre, módulo ES cargado bajo demanda). Mapa base según los ajustes del SERVIDOR (uno solo
// para todos; los cambia quien tiene fleet.manage): ninguno (fondo liso), teselas de un proveedor en línea (intermediadas por
// el servidor en /api/fleet/map/tiles: la clave del proveedor no llega al navegador), o un archivo PMTiles del servidor (sin
// internet; vendor/pmtiles + estilo de vendor/protomaps, por ahora sin nombres de calles porque las fuentes no se incluyen
// todavía). Los tiles públicos de OpenStreetMap NO se pueden usar en un producto distribuido.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

const fm = {
  active: false,
  map: null,
  lib: null,
  settings: null,
  vehicles: new Map(),     // vehicleId → VehicleDto
  positions: new Map(),    // vehicleId → FleetLivePositionDto
  markers: new Map(),      // vehicleId → maplibregl.Marker
  selected: null,
  follow: false,
  filter: "all",
  search: "",
  feeds: [],               // LvSession de la ficha
  hub: null,
  tick: null,
  fitted: false,
};

const FM_MOVING_KMH = 3;

function fmState(p, v) {
  if (!p || !p.online || v?.status === "Offline") return "off";
  return p.speedKmh >= FM_MOVING_KMH ? "move" : "stop";
}

function fmAgo(iso) {
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `hace ${s} s`;
  if (s < 3600) return `hace ${Math.round(s / 60)} min`;
  if (s < 86400) return `hace ${Math.round(s / 3600)} h`;
  return new Date(iso).toLocaleString("es-CL");
}

function fmHeading(deg) {
  return ["N", "NE", "E", "SE", "S", "SO", "O", "NO"][Math.round(((deg % 360) + 360) % 360 / 45) % 8];
}

async function fmLoadLib() {
  if (fm.lib) return fm.lib;
  if (!document.querySelector('link[href^="vendor/maplibre/maplibre-gl.css"]')) {
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = "vendor/maplibre/maplibre-gl.css?v=6.12.0";
    document.head.append(link);
  }
  const mod = await import("./vendor/maplibre/maplibre-gl.mjs");
  fm.lib = mod.default ?? mod;
  return fm.lib;
}

function fmLoadScript(src, global) {
  if (window[global]) return Promise.resolve(window[global]);
  return new Promise((ok, fail) => {
    const s = document.createElement("script");
    s.src = src;
    s.onload = () => ok(window[global]);
    s.onerror = () => fail(new Error(`No se pudo cargar ${src}`));
    document.head.append(s);
  });
}

/** Estilo del mapa base según los ajustes. */
async function fmStyle(settings) {
  const bg = { id: "fondo", type: "background", paint: { "background-color": "#e7eaee" } };
  if (settings.mode === "raster") {
    // Por el servidor (con la sesión: ver fmTransformRequest), no directo al proveedor.
    return {
      version: 8,
      sources: { base: { type: "raster", tiles: [`${location.origin}/api/fleet/map/tiles/{z}/{x}/{y}`], tileSize: 256, maxzoom: 19, attribution: settings.attribution || "" } },
      layers: [bg, { id: "base", type: "raster", source: "base" }],
    };
  }
  if (settings.mode === "pmtiles" && settings.pmtilesAvailable) {
    const [pm, bm] = await Promise.all([
      fmLoadScript("vendor/pmtiles/pmtiles.js?v=4.5.0", "pmtiles"),
      fmLoadScript("vendor/protomaps/basemaps.js?v=5.7.2", "basemaps"),
    ]);
    const url = `${location.origin}/api/fleet/map/basemap.pmtiles`;
    if (!fm.pmProtocol) {
      fm.pmProtocol = new pm.Protocol();
      fm.lib.addProtocol("pmtiles", fm.pmProtocol.tile);
    }
    // El archivo lo entrega el servidor con la sesión del usuario: se pide con su token.
    fm.pmProtocol.add(new pm.PMTiles(new pm.FetchSource(url, new Headers({ Authorization: `Bearer ${Api.token}` }))));
    const layers = bm.layers("protomaps", bm.namedFlavor("light"), { lang: "es" }).filter((l) => l.type !== "symbol");
    return {
      version: 8,
      sources: { protomaps: { type: "vector", url: `pmtiles://${url}`, attribution: "© OpenStreetMap" } },
      layers,
    };
  }
  return { version: 8, sources: {}, layers: [bg] };
}

/** Las teselas en línea las entrega el servidor con la sesión del usuario: se piden con su token. */
function fmTransformRequest(url) {
  return url.startsWith(`${location.origin}/api/fleet/map/tiles/`)
    ? { url, headers: { Authorization: `Bearer ${Api.token}` } }
    : undefined;
}

async function renderFleetMap() {
  fleetMapLeave();
  fm.active = true;
  fm.selected = null;
  fm.follow = false;
  fm.fitted = false;
  $("#page-title").textContent = "Mapa de flota";
  $("#view").className = "view-live";
  const canManage = Perms.can("fleet.manage");
  $("#view").innerHTML = `
    <div class="fm-root">
      <aside class="fm-side">
        <div class="fm-side-head">
          <div class="fme-tabs" id="fm-tabs"><button type="button" class="on">Vehículos</button></div>
          ${canManage ? `<button type="button" class="lv-icon-btn" id="fm-settings" title="Ajustes del mapa: mapa base, posición inicial y retención de posiciones">⚙</button>` : ""}
        </div>
        <div class="fm-pane" id="fm-vehicles-pane">
          <input id="fm-search" class="lv-search" type="search" placeholder="Buscar código o patente…" autocomplete="off">
          <div class="fm-filters" id="fm-filters">
            <button type="button" data-f="all" class="on">Todos <b id="fm-n-all">0</b></button>
            <button type="button" data-f="move">En marcha <b id="fm-n-move">0</b></button>
            <button type="button" data-f="stop">Detenidos <b id="fm-n-stop">0</b></button>
            <button type="button" data-f="off">Sin conexión <b id="fm-n-off">0</b></button>
          </div>
          <div class="fm-list" id="fm-list"><div class="muted lv-small" style="padding:10px 14px">Cargando vehículos…</div></div>
        </div>
        <div class="fm-pane" id="fm-other-pane" hidden></div>
      </aside>
      <div class="fm-main">
        <div class="fm-map" id="fm-map"></div>
        <div class="fm-legend">
          <span><i class="fm-dot move"></i>En marcha</span><span><i class="fm-dot stop"></i>Detenido</span><span><i class="fm-dot off"></i>Sin conexión</span>
        </div>
        <div class="fm-card" id="fm-card" hidden></div>
        <div class="fm-note" id="fm-note" hidden></div>
      </div>
    </div>`;

  let vehicles, positions, settings;
  try {
    [vehicles, positions, settings] = await Promise.all([
      Api.get("/api/fleet/vehicles"), Api.get("/api/fleet/positions"), Api.get("/api/fleet/map/settings"),
    ]);
  } catch (err) { $("#view").innerHTML = `<div class="error-box" style="margin:24px">${esc(err.error)}</div>`; return; }
  if (!fm.active) return;
  fm.settings = settings;
  fm.vehicles = new Map(vehicles.map((v) => [v.id, v]));
  fm.positions = new Map(positions.map((p) => [p.vehicleId, p]));

  $("#fm-search").addEventListener("input", (e) => { fm.search = e.target.value; fmRenderList(); });
  $$("#fm-filters [data-f]").forEach((b) => b.addEventListener("click", () => {
    fm.filter = b.dataset.f;
    $$("#fm-filters [data-f]").forEach((x) => x.classList.toggle("on", x === b));
    fmRenderList();
  }));
  $("#fm-list").addEventListener("click", (e) => {
    const row = e.target.closest("[data-v]");
    if (row) fmSelect(Number(row.dataset.v), true);
  });
  $("#fm-settings")?.addEventListener("click", fmSettingsModal);

  try {
    const lib = await fmLoadLib();
    if (!fm.active) return;
    fm.map = new lib.Map({
      container: "fm-map",
      style: await fmStyle(settings),
      center: [settings.centerLongitude, settings.centerLatitude],
      zoom: settings.zoom,
      attributionControl: { compact: true },
      transformRequest: fmTransformRequest,
    });
    fm.map.addControl(new lib.NavigationControl({ showCompass: false }), "top-left");
    fm.map.addControl(new lib.ScaleControl({ unit: "metric" }), "bottom-right");
    fm.map.on("dragstart", () => { if (fm.follow) { fm.follow = false; fmRenderCard(); } });
    fm.map.on("load", () => { fmEnsureTrackLayer(); fmFitAll(); if (typeof fmeAttach === "function") fmeAttach(); });
  } catch (err) {
    $("#fm-map").innerHTML = `<div class="error-box" style="margin:24px">No se pudo iniciar el mapa: ${esc(err.message)}</div>`;
  }
  if (settings.mode === "none" || (settings.mode === "pmtiles" && !settings.pmtilesAvailable)) {
    $("#fm-note").hidden = false;
    $("#fm-note").textContent = settings.mode === "pmtiles"
      ? "Falta el archivo del mapa base (basemap.pmtiles en la carpeta maps de los datos del servidor): se muestran solo los vehículos."
      : "Sin mapa base configurado: se muestran solo los vehículos." + (canManage ? " Configúrelo en Ajustes del mapa." : "");
  }
  fmRenderAll();

  fm.hub = {
    positions: (batch) => {
      if (!fm.active) return;
      for (const p of batch.positions || []) fm.positions.set(p.vehicleId, p);
      fmRenderAll();
    },
    config: (topic) => {
      if (!fm.active) return;
      if (topic === "fleet-map") renderFleetMap();
      else if (topic === "fleet") fmReloadVehicles();
    },
    status: (dto) => {
      // El hub manda los enums como números (la API, como texto): DeviceStatus { Unknown, Online, Offline, AuthFailed }.
      const status = typeof dto.status === "number" ? ["Unknown", "Online", "Offline", "AuthFailed"][dto.status] ?? "Unknown" : dto.status;
      for (const v of fm.vehicles.values()) if (v.deviceId === dto.id) {
        v.status = status;
        const p = fm.positions.get(v.id);
        if (p) p.online = status === "Online";
      }
      fmRenderAll();
    },
  };
  VmsHub.on("FleetPositions", fm.hub.positions);
  VmsHub.on("ConfigChanged", fm.hub.config);
  VmsHub.on("DeviceStatusChanged", fm.hub.status);
  fm.tick = setInterval(() => { if (fm.active) { fmRenderList(); fmRenderCard(); } }, 10000);
}

/** Suelta el mapa, el video de la ficha y el hub al salir de la página. */
function fleetMapLeave() {
  if (!fm.active && !fm.map) return;
  fm.active = false;
  fmCloseFeeds();
  clearInterval(fm.tick);
  if (typeof fmeDetach === "function") fmeDetach();
  if (fm.hub) {
    VmsHub.off("FleetPositions", fm.hub.positions);
    VmsHub.off("ConfigChanged", fm.hub.config);
    VmsHub.off("DeviceStatusChanged", fm.hub.status);
    fm.hub = null;
  }
  fm.markers.forEach((m) => m.remove());
  fm.markers.clear();
  try { fm.map?.remove(); } catch { /* ya liberado */ }
  fm.map = null;
}

async function fmReloadVehicles() {
  try {
    const vehicles = await Api.get("/api/fleet/vehicles");
    fm.vehicles = new Map(vehicles.map((v) => [v.id, v]));
    fmRenderAll();
  } catch { /* se reintenta con el próximo aviso */ }
}

function fmRenderAll() {
  fmRenderMarkers();
  fmRenderList();
  fmRenderCard();
  if (fm.follow && fm.selected != null) {
    const p = fm.positions.get(fm.selected);
    if (p && fm.map) fm.map.easeTo({ center: [p.longitude, p.latitude], duration: 800 });
  }
}

function fmMatches(v) {
  const q = (fm.search || "").trim().toLowerCase();
  if (q && !(v.code.toLowerCase().includes(q) || (v.plate || "").toLowerCase().includes(q))) return false;
  return fm.filter === "all" || fmState(fm.positions.get(v.id), v) === fm.filter;
}

function fmRenderList() {
  const host = $("#fm-list");
  if (!host) return;
  const all = [...fm.vehicles.values()].filter((v) => v.enabled);
  const counts = { all: all.length, move: 0, stop: 0, off: 0 };
  for (const v of all) counts[fmState(fm.positions.get(v.id), v)]++;
  for (const k of Object.keys(counts)) { const el = $(`#fm-n-${k}`); if (el) el.textContent = counts[k]; }
  const rows = all.filter(fmMatches).sort((a, b) => a.code.localeCompare(b.code, "es", { numeric: true }));
  if (!rows.length) {
    host.innerHTML = `<div class="muted lv-small" style="padding:10px 14px">${all.length ? "Ningún vehículo coincide." : "Todavía no hay vehículos."}</div>`;
    return;
  }
  host.innerHTML = rows.map((v) => {
    const p = fm.positions.get(v.id);
    const st = fmState(p, v);
    const right = st === "move" ? `${Math.round(p.speedKmh)} km/h` : st === "stop" ? "Detenido" : "—";
    const sub = [v.plate, p ? fmAgo(p.at) : "sin posición"].filter(Boolean).join(" · ");
    const alarm = typeof fmeAlarmed === "function" && fmeAlarmed(v.id);
    return `<div class="fm-row ${fm.selected === v.id ? "sel" : ""}" data-v="${v.id}" title="${p ? "Clic para verlo en el mapa" : "Este vehículo aún no informa posición"}">
      <i class="fm-dot ${alarm ? "alarm" : st}"></i>
      <div class="fm-row-text"><div class="fm-row-top"><b>${esc(v.code)}</b><span>${esc(right)}</span></div><div class="muted">${esc(sub)}</div></div>
    </div>`;
  }).join("");
}

function fmMarkerHtml(v, p) {
  const st = fmState(p, v);
  const arrow = st === "move"
    ? `<svg viewBox="0 0 24 24" width="22" height="22" style="transform:rotate(${Math.round(p.headingDeg)}deg)"><path d="M12 2l7 18-7-4-7 4z"/></svg>`
    : `<svg viewBox="0 0 24 24" width="16" height="16"><circle cx="12" cy="12" r="8"/></svg>`;
  const alarm = typeof fmeAlarmed === "function" && fmeAlarmed(v.id) ? "alarm" : "";
  return `<div class="fm-mk ${st} ${alarm} ${fm.selected === v.id ? "sel" : ""}">${arrow}<span>${esc(v.code)}</span></div>`;
}

function fmRenderMarkers() {
  if (!fm.map || !fm.lib) return;
  const seen = new Set();
  for (const v of fm.vehicles.values()) {
    const p = fm.positions.get(v.id);
    if (!v.enabled || !p) continue;
    seen.add(v.id);
    let m = fm.markers.get(v.id);
    if (!m) {
      const el = document.createElement("div");
      el.addEventListener("click", (e) => { e.stopPropagation(); fmSelect(v.id, false); });
      m = new fm.lib.Marker({ element: el, anchor: "center" }).setLngLat([p.longitude, p.latitude]).addTo(fm.map);
      fm.markers.set(v.id, m);
    }
    m.setLngLat([p.longitude, p.latitude]);
    m.getElement().innerHTML = fmMarkerHtml(v, p);
    m.getElement().title = `${v.code}${v.plate ? ` · ${v.plate}` : ""}`;
  }
  for (const [id, m] of fm.markers) if (!seen.has(id)) { m.remove(); fm.markers.delete(id); }
}

/** Al abrir: encuadra todos los vehículos con posición (una vez). */
function fmFitAll() {
  if (fm.fitted || !fm.map) return;
  const pts = [...fm.positions.values()];
  if (!pts.length) return;
  fm.fitted = true;
  if (pts.length === 1) { fm.map.jumpTo({ center: [pts[0].longitude, pts[0].latitude], zoom: 15 }); return; }
  const lons = pts.map((p) => p.longitude), lats = pts.map((p) => p.latitude);
  fm.map.fitBounds([[Math.min(...lons), Math.min(...lats)], [Math.max(...lons), Math.max(...lats)]], { padding: 80, maxZoom: 15, duration: 0 });
}

function fmSelect(id, fly) {
  if (fm.selected !== id) { fmCloseFeeds(); fmClearTrack(); }
  fm.selected = id;
  const p = fm.positions.get(id);
  if (fly && p && fm.map) fm.map.flyTo({ center: [p.longitude, p.latitude], zoom: Math.max(fm.map.getZoom(), 14) });
  fmRenderAll();
}

function fmRenderCard() {
  const card = $("#fm-card");
  if (!card) return;
  const v = fm.selected != null ? fm.vehicles.get(fm.selected) : null;
  if (!v) { card.hidden = true; return; }
  const p = fm.positions.get(v.id);
  const st = fmState(p, v);
  const canLive = Perms.can("fleet.live");
  const canHistory = Perms.can("fleet.history");
  const stats = p ? `
      <div class="fm-stats">
        <div><span class="muted">Velocidad</span><b>${Math.round(p.speedKmh)} km/h</b></div>
        <div><span class="muted">Rumbo</span><b>${fmHeading(p.headingDeg)}</b></div>
        <div><span class="muted">Satélites</span><b>${p.satellites}</b></div>
      </div>
      <div class="muted fm-small">${st === "off" ? "Sin conexión · última posición" : st === "move" ? "En marcha" : "Detenido"} ${fmAgo(p.at)}</div>`
    : `<div class="muted fm-small">Este vehículo todavía no informa posición.</div>`;
  const keep = card.querySelector(".fm-cams");
  if (!card.hidden && card.dataset.v === String(v.id) && keep) {
    // Actualizar sin rehacer los videos.
    card.querySelector(".fm-card-info").innerHTML = stats;
    card.querySelector("#fm-follow")?.classList.toggle("on", fm.follow);
    return;
  }
  card.dataset.v = String(v.id);
  card.hidden = false;
  card.innerHTML = `
    <div class="fm-card-head">
      <div><b>${esc(v.code)}</b><div class="muted fm-small">${esc([v.plate, v.kind, v.location].filter(Boolean).join(" · ") || "—")}</div></div>
      <button type="button" class="lv-icon-btn" id="fm-close" title="Cerrar">✕</button>
    </div>
    <div class="fm-card-info">${stats}</div>
    <div class="fm-cams">${canLive && v.channels.some((c) => c.enabled)
      ? v.channels.filter((c) => c.enabled).slice(0, 4).map((c) => `<div class="fm-cam" data-ch="${c.id}"><video muted playsinline></video><span>${esc(c.name)}</span></div>`).join("")
      : `<div class="muted fm-small">${canLive ? "Sin cámaras habilitadas." : "Sus roles no incluyen el video de vehículos."}</div>`}</div>
    <div class="fm-card-actions">
      ${canLive ? `<button type="button" class="btn small" id="fm-live" title="Abre sus cámaras aquí, en secundario">Ver cámaras</button>
      <button type="button" class="btn ghost small" id="fm-grid" title="Abre sus cámaras en Flota en vivo">Abrir en grilla</button>` : ""}
      <button type="button" class="btn ghost small ${fm.follow ? "on" : ""}" id="fm-follow" title="El mapa sigue al vehículo">Seguir</button>
      ${canHistory ? `<button type="button" class="btn ghost small" id="fm-track" title="Recorrido de las últimas 2 horas">Recorrido 2 h</button>` : ""}
    </div>`;
  $("#fm-close").addEventListener("click", () => { fmCloseFeeds(); fmClearTrack(); fm.selected = null; fm.follow = false; fmRenderAll(); });
  $("#fm-live")?.addEventListener("click", () => fmOpenFeeds(v));
  $("#fm-grid")?.addEventListener("click", () => {
    // En el cliente de escritorio (WebView2) la grilla es la ventana nativa Flota en vivo: se le pide a él.
    if (window.chrome?.webview) window.chrome.webview.postMessage({ type: "fleet-open", deviceId: v.deviceId });
    else location.hash = `#/fleet/live?dev=${v.deviceId}`;
  });
  $("#fm-follow").addEventListener("click", () => { fm.follow = !fm.follow; fmRenderAll(); });
  $("#fm-track")?.addEventListener("click", () => fmShowTrack(v));
}

/** Cámaras del vehículo en la ficha (secundario, sin audio). */
async function fmOpenFeeds(v) {
  fmCloseFeeds();
  if (typeof LvSession !== "function") return;
  for (const tile of $$("#fm-card .fm-cam")) {
    const ch = v.channels.find((c) => c.id === Number(tile.dataset.ch));
    if (!ch) continue;
    const s = new LvSession({ deviceId: v.deviceId, rtspChannel: ch.rtspChannel, name: ch.name }, "Sub");
    fm.feeds.push(s);
    const video = tile.querySelector("video");
    tile.classList.add("loading");
    s.open().then(() => {
      video.srcObject = s.stream;
      video.play().catch(() => { /* sin gesto: igual se ve en silencio */ });
      tile.classList.remove("loading");
    }).catch((err) => {
      tile.classList.remove("loading");
      if (!s.closed) tile.querySelector("span").textContent = `${ch.name} · ${err?.error || "sin video"}`;
    });
  }
}

function fmCloseFeeds() {
  fm.feeds.forEach((s) => s.close());
  fm.feeds = [];
}

function fmEnsureTrackLayer() {
  if (!fm.map || fm.map.getSource("track")) return;
  fm.map.addSource("track", { type: "geojson", data: { type: "FeatureCollection", features: [] } });
  fm.map.addLayer({ id: "track", type: "line", source: "track", paint: { "line-color": "#1f6feb", "line-width": 4, "line-opacity": 0.85 },
    layout: { "line-cap": "round", "line-join": "round" } });
}

function fmClearTrack() {
  fm.map?.getSource("track")?.setData({ type: "FeatureCollection", features: [] });
}

async function fmShowTrack(v) {
  const to = new Date(), from = new Date(to.getTime() - 2 * 3600 * 1000);
  try {
    const pts = await Api.get(`/api/fleet/vehicles/${v.id}/track?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`);
    fmEnsureTrackLayer();
    if (pts.length < 2) { toast(`${v.code} no tiene recorrido en las últimas 2 horas (${pts.length} posición/es).`); return; }
    fm.map.getSource("track").setData({ type: "Feature", geometry: { type: "LineString", coordinates: pts.map((p) => [p.longitude, p.latitude]) } });
    const lons = pts.map((p) => p.longitude), lats = pts.map((p) => p.latitude);
    fm.map.fitBounds([[Math.min(...lons), Math.min(...lats)], [Math.max(...lons), Math.max(...lats)]], { padding: 80, maxZoom: 16 });
  } catch (err) { toast(err.error, true); }
}

/** Ajustes del mapa (Configuración del módulo): mapa base, posición inicial y retención de posiciones. */
function fmSettingsModal() {
  const s = fm.settings;
  openModal(`
    <h3>Ajustes del mapa de flota</h3>
    <div id="fm-set-error"></div>
    <div class="field"><label>Mapa base</label>
      <select id="fm-mode">
        <option value="none" ${s.mode === "none" ? "selected" : ""}>Sin mapa base (solo los vehículos)</option>
        <option value="pmtiles" ${s.mode === "pmtiles" ? "selected" : ""}>Archivo del servidor, sin internet (PMTiles)</option>
        <option value="raster" ${s.mode === "raster" ? "selected" : ""}>Teselas de un proveedor en línea</option>
      </select></div>
    <div class="muted fm-small" id="fm-mode-hint"></div>
    <div class="field" id="fm-raster-fields">
      <label>Plantilla de teselas</label>
      <input id="fm-raster" value="${esc(s.rasterUrl ?? "")}" placeholder="https://proveedor.example/tiles/{z}/{x}/{y}.png?key=…">
      <label style="margin-top:6px">Atribución que exige el proveedor</label>
      <input id="fm-attrib" value="${esc(s.attribution ?? "")}" placeholder="© Proveedor © OpenStreetMap">
    </div>
    <div class="form-grid">
      <div class="field"><label>Posición inicial (lat, long)</label><input id="fm-center" value="${s.centerLatitude.toFixed(5)}, ${s.centerLongitude.toFixed(5)}"></div>
      <div class="field"><label>Zoom inicial</label><input id="fm-zoom" type="number" min="2" max="18" value="${s.zoom}"></div>
    </div>
    <button type="button" class="btn ghost small" id="fm-here">Usar la vista actual del mapa</button>
    <div class="field" style="margin-top:10px"><label>Guardar las posiciones GPS durante (días)</label>
      <input id="fm-retention" type="number" min="7" max="3650" value="${s.positionRetentionDays}">
      <div class="muted fm-small">Los recorridos son datos personales de los conductores: guarde solo lo necesario.</div></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="fm-set-cancel">Cancelar</button>
      <button class="btn" type="button" id="fm-set-save">Guardar</button>
    </div>`, true);
  const hint = () => {
    const m = $("#fm-mode").value;
    $("#fm-raster-fields").hidden = m !== "raster";
    $("#fm-mode-hint").textContent = m === "pmtiles"
      ? (s.pmtilesAvailable ? "Se usa el archivo basemap.pmtiles del servidor." : "Copie el archivo basemap.pmtiles (extracto de Protomaps de su región) en la carpeta maps de los datos del servidor.")
      : m === "raster" ? "El servidor pide las teselas al proveedor y las entrega a cada operador: la clave queda solo en el servidor (que necesita salida a internet). Teselas raster de 256 px ({z}/{x}/{y}, {s} opcional). Los servidores públicos de OpenStreetMap (tile.openstreetmap.org) no se permiten en un producto: use un proveedor con clave."
      : "";
  };
  $("#fm-mode").addEventListener("change", hint);
  hint();
  $("#fm-here").addEventListener("click", () => {
    if (!fm.map) return;
    const c = fm.map.getCenter();
    $("#fm-center").value = `${c.lat.toFixed(5)}, ${c.lng.toFixed(5)}`;
    $("#fm-zoom").value = Math.round(fm.map.getZoom());
  });
  $("#fm-set-cancel").addEventListener("click", closeModal);
  $("#fm-set-save").addEventListener("click", async () => {
    const [lat, lon] = $("#fm-center").value.split(",").map((x) => Number(x.trim()));
    const body = {
      mode: $("#fm-mode").value, rasterUrl: $("#fm-raster").value.trim() || null, attribution: $("#fm-attrib").value.trim() || null,
      centerLatitude: lat, centerLongitude: lon, zoom: Number($("#fm-zoom").value), positionRetentionDays: Number($("#fm-retention").value),
    };
    try {
      await Api.put("/api/fleet/map/settings", body);
      closeModal();
      toast("Ajustes del mapa guardados.");
      renderFleetMap();
    } catch (err) { $("#fm-set-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
  });
}
