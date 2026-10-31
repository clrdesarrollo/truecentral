// CLR TrueCentral VMS — Aplicaciones → Reproducción (panel web).
//
// Réplica en el navegador del módulo Reproducción del cliente de escritorio
// (Views\PlaybackView.xaml + ViewModels\PlaybackViewModel.cs): árbol de
// cámaras por equipo o por ubicación, día con calendario que marca los días
// con grabación, hasta 4 canales sincronizados a la misma hora del equipo,
// línea de tiempo con los tramos por tipo (continua, movimiento, alarma…),
// zoom de 24 h a 1 min, recorte de un tramo para exportarlo, transporte
// (pausa, saltos, velocidad), captura, cápsula local y pantalla completa.
//
// Las grabaciones viven en el equipo: el servidor busca los tramos por el
// driver y, para reproducir, monta una ruta de un solo uso en MediaMTX
// (/api/playback/webrtc: mismas validaciones, alcance y auditoría que el RTSP
// del cliente). Saltar, reanudar o cambiar la velocidad pide otra ruta; el
// video viaja por WebRTC como en la vista en vivo (usa LvSession de live.js).
//
// Las horas son SIEMPRE la hora local del equipo: se manejan como fecha y
// hora "de pared" (sin zona) y se mandan sin zona al servidor.
"use strict";

const PB_MAX = 4;
const PB_ZOOMS = [1440, 720, 360, 180, 60, 30, 10, 5, 2, 1]; // minutos visibles en la línea de tiempo
// Rótulos y marcas de la regla por zoom (segundos).
const PB_RULER = { 1440: [7200, 3600], 720: [3600, 1800], 360: [1800, 600], 180: [900, 300], 60: [600, 120],
  30: [300, 60], 10: [60, 30], 5: [60, 15], 2: [30, 10], 1: [15, 5] };
const PB_SPEEDS = [0.25, 0.5, 1, 2, 4, 8];
const PB_KINDS = {
  Continuous: { label: "Continua", color: "#2E6BC4" },
  Motion: { label: "Movimiento", color: "#F59E0B" },
  Alarm: { label: "Alarma", color: "#EF4444" },
  Manual: { label: "Manual", color: "#A78BFA" },
  Other: { label: "Otra", color: "#6B7280" },
};
const PB_DAY = 86400000;
const PB_LOCK_MS = 10000; // tope del bloqueo de la línea de tiempo mientras abre un salto
const PB_PREFS_KEY = "tcvms.playback";

const pb = {
  active: false,
  prefs: { byLocation: false, collapsed: [], treeCollapsed: false, zoom: 4 },
  devices: [],
  channels: new Map(),
  locRoots: [],
  search: "",
  selChannelId: null,
  day: 0,               // ms de las 00:00 del día elegido
  gridSize: 1,
  cells: [],
  focused: 0,
  playhead: 0,          // ms (hora de pared del equipo)
  playing: false,
  paused: false,
  speed: 1,
  seq: 0,               // cada salto invalida las aperturas anteriores
  lock: null,           // { seq, until }: línea de tiempo bloqueada mientras abre un salto
  zoom: 4,
  sel: null,            // { a, b } tramo marcado
  cut: false,           // modo recorte
  drag: null,
  viewCenter: null,     // centro de la línea de tiempo mientras se arrastra
  calMonth: null,       // { y, m } del calendario abierto
  daysCache: new Map(),
  timers: [],
  loadingSegs: 0,
};

// ---------------------------------------------------------------------------
// Hora de pared
// ---------------------------------------------------------------------------
const pb2 = (n) => String(n).padStart(2, "0");
function pbDayStart(ms) { const d = new Date(ms); d.setHours(0, 0, 0, 0); return d.getTime(); }
function pbAddDays(dayMs, n) { const d = new Date(dayMs); d.setDate(d.getDate() + n); return pbDayStart(d.getTime()); }
function pbIso(ms) {
  const d = new Date(ms);
  return `${d.getFullYear()}-${pb2(d.getMonth() + 1)}-${pb2(d.getDate())}T${pb2(d.getHours())}:${pb2(d.getMinutes())}:${pb2(d.getSeconds())}`;
}
function pbDate(ms) { const d = new Date(ms); return `${pb2(d.getDate())}-${pb2(d.getMonth() + 1)}-${d.getFullYear()}`; }
function pbHms(ms) { const d = new Date(ms); return `${pb2(d.getHours())}:${pb2(d.getMinutes())}:${pb2(d.getSeconds())}`; }
function pbStamp(ms) { const d = new Date(ms); return `${d.getFullYear()}-${pb2(d.getMonth() + 1)}-${pb2(d.getDate())} ${pb2(d.getHours())}.${pb2(d.getMinutes())}.${pb2(d.getSeconds())}`; }
function pbDayLabel(ms) {
  const s = new Date(ms).toLocaleDateString("es-CL", { weekday: "long" });
  return `${s} ${pbDate(ms)}`;
}
function pbToday() { return pbDayStart(Date.now()); }
/** "2026-10-20T08:00:00" (sin zona) → ms de esa hora de pared en este navegador. */
function pbParse(s) { return new Date(String(s).replace(/Z$/, "")).getTime(); }

// ---------------------------------------------------------------------------
// Sesión WebRTC de un tramo grabado
// ---------------------------------------------------------------------------
class PbSession extends LvSession {
  constructor(channel, startMs, endMs, speed) {
    super(channel, "Main");
    this.startMs = startMs;
    this.endMs = endMs;
    this.speed = speed;
    this.path = null;
    this.exactSeek = true;
  }
  async negotiate(offer) {
    const res = await Api.post("/api/playback/webrtc", {
      deviceId: this.channel.deviceId, rtspChannel: this.channel.rtspChannel,
      startLocal: pbIso(this.startMs), endLocal: pbIso(this.endMs), speed: this.speed, offer,
    });
    this.path = res.path;
    this.exactSeek = res.exactSeek !== false;
    return res;
  }
}

/** Mismo tramo por MSE (/api/playback/mse): grabaciones H.265 con B-frames, que WebRTC no admite. */
class PbMseSession extends LvMseSession {
  constructor(channel, startMs, endMs, speed) {
    super(channel, "Main");
    this.startMs = startMs;
    this.endMs = endMs;
    this.speed = speed;
    this.path = null;
    this.exactSeek = true;
  }
  url() {
    const q = new URLSearchParams({ deviceId: this.channel.deviceId, rtspChannel: this.channel.rtspChannel,
      start: pbIso(this.startMs), end: pbIso(this.endMs), speed: this.speed, access_token: Api.token || "" });
    return `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/api/playback/mse?${q}`;
  }
}

/** Las grabaciones de un canal pueden ir por MSE aunque su vivo no (otro stream, otro códec). */
function pbMseKey(channel) { return `rec:${channel.deviceId}/${channel.rtspChannel}`; }

// ---------------------------------------------------------------------------
// Un cuadro de la grilla
// ---------------------------------------------------------------------------
class PbCell {
  constructor(index) {
    this.index = index;
    this.channel = null;
    this.segments = [];
    this.segsLoaded = false;
    this.session = null;
    this.video = null;
    this.rangeStart = 0;
    this.t0 = 0;           // currentTime del <video> en el primer cuadro
    this.speed = 1;
    this.opening = false;
    this.status = "";
    this.error = "";
    this.audio = false;
    this.rec = null;
    this.el = document.createElement("div");
    this.el.className = "lv-cell pb-cell empty";
    this.el.innerHTML = `
      <div class="lv-bar">
        <span class="lv-num">${index + 1}</span>
        <span class="lv-title"></span>
        <span class="lv-spin" hidden></span>
        <span class="lv-status"></span>
        <span class="lv-actions">
          <button type="button" class="lv-act" data-act="audio" title="Audio de este cuadro (se silencia el resto)"></button>
          <button type="button" class="lv-act" data-act="snap" title="Guardar una captura del cuadro que se está viendo">${lvSvg(LV_ICO.snap, 13)}</button>
          <button type="button" class="lv-act lv-rec" data-act="rec" title="Grabar una cápsula de lo que se está reproduciendo">●</button>
          <span class="lv-rectime" hidden></span>
          <button type="button" class="lv-act lv-close" data-act="close" title="Quitar el canal de la reproducción">✕</button>
        </span>
      </div>
      <div class="lv-body">
        <div class="lv-empty">Posición libre</div>
        <div class="lv-error" hidden></div>
        <canvas class="pb-poster" hidden></canvas>
      </div>`;
    this.body = this.el.querySelector(".lv-body");
    this.el._pbCell = this;
  }

  get title() { return this.channel ? `${this.channel.device?.name ?? ""} · ${this.channel.name}` : ""; }

  /** Instante que muestra el video (hora de pared del equipo), o null si no hay imagen. */
  get position() {
    if (!this.session || this.session.closed || !this.video || this.video.readyState < 2) return null;
    return this.rangeStart + Math.max(0, this.video.currentTime - this.t0) * 1000 * this.speed;
  }

  /** Abre el canal desde el instante t (hasta el fin del día) a la velocidad del módulo. */
  async openAt(t, seq) {
    if (!this.channel) return;
    const end = pb.day + PB_DAY;
    if (t >= end) return;
    const channel = this.channel;
    this.opening = true;
    this.error = "";
    this.status = "Abriendo la grabación en el equipo…";
    pbRenderCell(this);
    const session = lvMseChannels.has(pbMseKey(channel))
      ? new PbMseSession(channel, t, end, pb.speed)
      : new PbSession(channel, t, end, pb.speed);
    const video = document.createElement("video");
    video.className = "lv-v";
    video.autoplay = true;
    video.playsInline = true;
    video.muted = !this.audio;
    video.srcObject = session.stream;
    this.body.prepend(video);
    try {
      await session.open();
      if (session.kind === "mse") video.srcObject = session.stream; // la pista llega con la primera imagen
      await lvWaitFrame(video, session, 30000);
    } catch (err) {
      session.close();
      video.remove();
      if (this.channel !== channel) return;
      if (err?.cancelled || seq !== pb.seq) { if (seq === pb.seq || !pb.playing) this.idle(); return; }
      // Grabación en un formato que este navegador no recibe por WebRTC (H.265 en
      // Edge) pero sí decodifica por MSE: se aprende el canal y se reabre por ahí.
      const codec = lvCodecRejected(err) || err?.codec;
      if (codec && session.kind !== "mse" && lvHevc.mse && !lvMseChannels.has(pbMseKey(channel))) {
        lvMarkMse(pbMseKey(channel));
        this.status = "Este navegador no recibe H.265 por WebRTC: abriendo por la entrega alternativa…";
        pbRenderCell(this);
        return this.openAt(t, seq);
      }
      this.opening = false;
      this.error = codec ? lvCodecMessage(err) : await pbExplain(session, err);
      this.status = "";
      pbRenderCell(this);
      return;
    }
    if (seq !== pb.seq || this.channel !== channel || !pb.active) {
      session.close();
      video.remove();
      if (this.channel === channel && !pb.playing) this.idle();
      return;
    }
    // El cuadro anterior se suelta recién ahora: no hay pantallazo negro entre saltos.
    this.dropSession();
    this.session = session;
    this.video = video;
    this.rangeStart = t;
    this.speed = pb.speed;
    this.t0 = video.currentTime;
    video.classList.add("front");
    this.body.querySelector(".pb-poster").hidden = true;
    this.opening = false;
    this.status = session.exactSeek ? "" : "El equipo ONVIF reproduce desde el inicio de su grabación: no permite posicionarse por hora.";
    session.onDrop = () => { if (this.session === session) { this.status = "Se cortó la reproducción."; pbRenderCell(this); } };
    session.onAudio = () => pbRenderCell(this);
    pbRenderCell(this);
    if (session.kind !== "mse") this.watchHevc(seq, session);
  }

  /** Grabación H.265 con B-frames por WebRTC: se aprende el canal y se reabre por MSE en el mismo instante. */
  async watchHevc(seq, session) {
    if (!(await lvLooksLikeHevcBframes(session)) || seq !== pb.seq || session !== this.session) return;
    lvMarkMse(pbMseKey(this.channel));
    const at = this.position ?? this.rangeStart;
    this.status = "H.265 con B-frames: cambiando a la entrega alternativa…";
    pbRenderCell(this);
    this.openAt(at, pb.seq);
  }

  /** Deja la última imagen congelada (pausa) y suelta la sesión con el equipo. */
  freeze() {
    const v = this.video;
    if (v && v.videoWidth) {
      const c = this.body.querySelector(".pb-poster");
      c.width = v.videoWidth;
      c.height = v.videoHeight;
      c.getContext("2d").drawImage(v, 0, 0);
      c.hidden = false;
    }
    this.dropSession();
  }

  /** Sin apertura en curso (se detuvo o se pausó mientras abría). */
  idle() {
    this.opening = false;
    this.status = "";
    pbRenderCell(this);
  }

  dropSession() {
    this.stopRec(true);
    this.session?.close();
    this.session = null;
    this.video?.remove();
    this.video = null;
  }

  clear() {
    this.dropSession();
    this.channel = null;
    this.segments = [];
    this.segsLoaded = false;
    this.opening = false;
    this.error = "";
    this.status = "";
    this.audio = false;
    this.body.querySelector(".pb-poster").hidden = true;
    pbRenderCell(this);
  }

  stopRec(discard) {
    const r = this.rec;
    if (!r) return;
    this.rec = null;
    r.discard = discard;
    try { r.recorder.stop(); } catch { /* ya detenida */ }
  }
}

/** Motivo accionable de una reproducción que no abrió (lo que contestó el equipo, si se sabe). */
async function pbExplain(session, err) {
  if (session.path) {
    try {
      const f = await Api.get(`/api/playback/failure/${encodeURIComponent(session.path)}`);
      if (f?.message) return f.message;
    } catch { /* sin detalle */ }
  }
  return err?.error || "No se pudo abrir la grabación.";
}

function pbRenderCell(cell) {
  const el = cell.el;
  const has = !!cell.channel;
  el.classList.toggle("empty", !has);
  el.classList.toggle("sel", pb.focused === cell.index && pb.cells.filter((c) => c.channel).length > 1);
  el.hidden = cell.index >= pb.gridSize;
  const title = el.querySelector(".lv-title");
  title.textContent = has ? cell.title : "Posición libre";
  title.classList.toggle("muted", !has);
  el.querySelector(".lv-spin").hidden = !cell.opening;
  const status = el.querySelector(".lv-status");
  status.textContent = cell.status;
  status.title = cell.status;
  el.querySelector(".lv-actions").hidden = !has;
  const err = el.querySelector(".lv-error");
  err.hidden = !cell.error;
  err.textContent = cell.error;
  el.querySelector(".lv-empty").textContent = pb.cells.some((c) => c.channel)
    ? "Posición libre"
    : "Doble clic en un canal del árbol, o arrástrelo hasta la posición donde quiera verlo.";
  if (!has) return;
  const audio = el.querySelector('[data-act="audio"]');
  audio.innerHTML = lvSvg(cell.audio ? LV_ICO.audio : LV_ICO.mute, 13);
  audio.classList.toggle("on", cell.audio);
  const rec = el.querySelector('[data-act="rec"]');
  rec.classList.toggle("on", !!cell.rec);
  rec.title = cell.rec ? "Grabando… (clic para detener y guardar)" : "Grabar una cápsula de lo que se está reproduciendo";
  const time = el.querySelector(".lv-rectime");
  time.hidden = !cell.rec;
  if (cell.rec) time.textContent = lvElapsed(cell.rec.startedAt);
}

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
function pbLoadPrefs() {
  try { return { ...pb.prefs, ...JSON.parse(localStorage.getItem(PB_PREFS_KEY) || "{}") }; }
  catch { return { ...pb.prefs }; }
}
function pbSavePrefs() {
  try { localStorage.setItem(PB_PREFS_KEY, JSON.stringify(pb.prefs)); } catch { /* sin almacenamiento */ }
}

async function renderPlayback() {
  pb.prefs = pbLoadPrefs();
  pb.active = true;
  pb.day = pbToday();
  pb.gridSize = 1;
  pb.focused = 0;
  pb.playhead = pb.day;
  pb.playing = pb.paused = false;
  pb.speed = 1;
  pb.zoom = Math.min(Math.max(Number(pb.prefs.zoom) || 4, 0), PB_ZOOMS.length - 1);
  pb.sel = null;
  pb.cut = false;
  pb.search = "";
  pb.cells = Array.from({ length: PB_MAX }, (_, i) => new PbCell(i));
  $("#page-title").textContent = "Reproducción";
  $("#view").className = "view-live";

  if (typeof RTCPeerConnection === "undefined") {
    $("#view").innerHTML = `<div class="info-box" style="margin:24px">Este navegador no admite WebRTC: use Chrome, Edge o Firefox actualizados para ver grabaciones.</div>`;
    return;
  }
  const canExport = Perms.can("playback.export");
  const legend = Object.values(PB_KINDS).map((k) => `<span class="pb-chip"><i style="background:${k.color}"></i>${k.label}</span>`).join("");
  $("#view").innerHTML = `
    <div class="lv-root pb-root ${pb.prefs.treeCollapsed ? "tree-collapsed" : ""}" id="pb-root">
      <aside class="lv-side">
        <div class="lv-side-head">
          <span class="lv-cap">GRABACIONES</span>
          <span class="lv-seg">
            <button type="button" data-tree="device" title="Cámaras agrupadas por equipo (DVR, NVR, cámara)">Equipo</button>
            <button type="button" data-tree="location" title="Cámaras agrupadas por ubicación (sitio, edificio, sector…)">Ubicación</button>
          </span>
        </div>
        <input id="pb-search" class="lv-search" type="search" placeholder="Buscar canal…" autocomplete="off">
        <div class="lv-tree" id="pb-tree"><div class="muted lv-small" style="padding:10px 14px">Cargando cámaras…</div></div>
      </aside>
      <button type="button" class="lv-handle" id="pb-handle" title="Mostrar u ocultar la lista de dispositivos"></button>
      <div class="lv-main">
        <div class="lv-toolbar pb-toolbar">
          <div class="lv-tb-left">
            <span class="muted">Día:</span>
            <button type="button" class="btn ghost lv-tb pb-sq" id="pb-prev" title="Día anterior">◀</button>
            <span class="lv-pop-anchor">
              <button type="button" class="btn ghost lv-tb" id="pb-date" title="Elegir fecha en el calendario"></button>
              <div class="lv-pop pb-cal" id="pb-cal" hidden></div>
            </span>
            <button type="button" class="btn ghost lv-tb pb-sq" id="pb-next" title="Día siguiente">▶</button>
            <button type="button" class="btn ghost lv-tb" id="pb-today">Hoy</button>
          </div>
          <div class="lv-tb-center">
            <span class="lv-seg pb-gridseg">
              <button type="button" data-grid="1" title="Un solo canal a pantalla completa (cierra los demás)">1 canal</button>
              <button type="button" data-grid="4" title="Mosaico de 4 posiciones: hasta cuatro cámaras sincronizadas a la misma hora">4 posiciones</button>
            </span>
            <span class="muted lv-small" id="pb-count"></span>
            <span class="lv-bulk" id="pb-loading" hidden><i></i></span>
          </div>
          <div class="lv-tb-right pb-legend">${legend}</div>
        </div>
        <div class="lv-gridwrap" id="pb-gridwrap"><div class="lv-grid pb-grid" id="pb-grid"></div></div>
        <div class="pb-bottom">
          <div class="pb-row">
            <div class="pb-transport">
              <button type="button" class="btn ghost lv-tb pb-sq" id="pb-play" title="Pausar / reanudar">${lvSvg(LV_ICO.play, 14)}</button>
              <button type="button" class="btn ghost lv-tb pb-sq" id="pb-stop" title="Detener la reproducción">■</button>
              <span class="pb-sep"></span>
              <button type="button" class="btn ghost lv-tb" data-skip="-300">−5 m</button>
              <button type="button" class="btn ghost lv-tb" data-skip="-30">−30 s</button>
              <button type="button" class="btn ghost lv-tb" data-skip="30">+30 s</button>
              <button type="button" class="btn ghost lv-tb" data-skip="300">+5 m</button>
              <span class="pb-sep"></span>
              <button type="button" class="btn ghost lv-tb pb-sq" id="pb-slower" title="Más lento">«</button>
              <b class="pb-speed" id="pb-speed">1×</b>
              <button type="button" class="btn ghost lv-tb pb-sq" id="pb-faster" title="Más rápido">»</button>
            </div>
            <b class="pb-clock" id="pb-clock"></b>
            <div class="pb-tools">
              <button type="button" class="btn ghost lv-tb lv-toggle" id="pb-cut" title="Modo recorte: arrastrar sobre la línea de tiempo marca el tramo a exportar">Recorte</button>
              <button type="button" class="btn ghost lv-tb" id="pb-full" title="Pantalla completa (doble clic al centro del video; Esc para salir)">${lvSvg(LV_ICO.full, 14)}</button>
              <span class="pb-sep"></span>
              <button type="button" class="btn ghost lv-tb pb-sq" id="pb-zout" title="Menos detalle (abarcar más horas)">−</button>
              <span class="pb-zoomlabel" id="pb-zoomlabel"></span>
              <button type="button" class="btn ghost lv-tb pb-sq" id="pb-zin" title="Más detalle (acercar la línea de tiempo)">+</button>
            </div>
          </div>
          <div class="pb-timeline" id="pb-timeline"><canvas id="pb-canvas"></canvas><span class="pb-chipdrag" id="pb-chipdrag" hidden></span></div>
          <div class="pb-row pb-selrow">
            <span class="muted">Tramo:</span>
            <span id="pb-seltext" class="pb-seltext"></span>
            <button type="button" class="lv-icon-btn" id="pb-selclear" title="Borrar la marca">✕</button>
            <span class="pb-status" id="pb-status"></span>
            <button type="button" class="btn ghost lv-tb" id="pb-playsel">Reproducir tramo</button>
            ${canExport ? `<button type="button" class="btn lv-tb" id="pb-export">Exportar…</button>` : ""}
          </div>
        </div>
      </div>
    </div>`;

  const grid = $("#pb-grid");
  pb.cells.forEach((c) => grid.append(c.el));
  pbWire();
  pbApplyGrid(1);
  pbRenderToolbar();
  pbStatus("Elija un canal del árbol (doble clic) para ver sus grabaciones.");
  pb.timers.push(setInterval(pbTick, 250));
  new ResizeObserver(() => pbDraw()).observe($("#pb-timeline"));
  await pbLoadTree();
}

function playbackLeave() {
  if (!pb.active) return;
  pb.active = false;
  pb.seq++;
  pb.cells.forEach((c) => c.dropSession());
  pb.timers.forEach((t) => clearInterval(t));
  pb.timers = [];
  document.removeEventListener("keydown", pbOnKey, true);
  document.removeEventListener("pointerdown", pbOnOutside, true);
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
}

window.addEventListener("pagehide", () => { if (pb.active) pb.cells.forEach((c) => c.dropSession()); });

function pbStatus(text, isError) {
  const el = $("#pb-status");
  if (!el) return;
  el.textContent = text || "";
  el.classList.toggle("error", !!isError);
}

function pbOpenCells() { return pb.cells.filter((c) => c.channel && c.index < pb.gridSize); }

// ---------------------------------------------------------------------------
// Árbol (mismo armado que la vista en vivo)
// ---------------------------------------------------------------------------
async function pbLoadTree() {
  let devices;
  try { devices = await Api.get("/api/devices"); }
  catch (err) { $("#pb-tree").innerHTML = `<div class="error-box" style="margin:10px">${esc(err.error)}</div>`; return; }
  const lists = await Promise.all(devices.map((d) => Api.get(`/api/devices/${d.id}/channels`).catch(() => [])));
  let locations = [];
  try { locations = await Api.get("/api/locations"); } catch { /* sin ubicaciones */ }
  if (!pb.active) return;
  pb.channels = new Map();
  pb.devices = devices.map((d, i) => {
    const dev = { id: d.id, name: d.name, status: d.status, channels: [] };
    dev.channels = lists[i].filter((c) => c.enabled).map((c) => {
      const ch = { ...c, device: dev };
      pb.channels.set(ch.id, ch);
      return ch;
    });
    return dev;
  }).filter((d) => d.channels.length);
  const nodes = new Map(locations.map((l) => [l.id, { loc: l, children: [], channels: [] }]));
  const roots = [];
  for (const l of [...locations].sort(compareLocations)) {
    const node = nodes.get(l.id);
    const parent = l.parentId != null ? nodes.get(l.parentId) : null;
    (parent ? parent.children : roots).push(node);
  }
  const unassigned = { loc: null, children: [], channels: [] };
  for (const ch of [...pb.channels.values()].sort(compareInLocation))
    (nodes.get(ch.locationId)?.channels ?? unassigned.channels).push(ch);
  if (unassigned.channels.length) roots.push(unassigned);
  const count = (n) => (n.total = n.channels.length + n.children.reduce((s, c) => s + count(c), 0));
  roots.forEach(count);
  const prune = (list) => list.filter((n) => n.total > 0).map((n) => { n.children = prune(n.children); return n; });
  pb.locRoots = prune(roots);
  pbRenderTree();
}

function pbRenderTree() {
  const host = $("#pb-tree");
  if (!host) return;
  $$(".pb-root .lv-seg [data-tree]").forEach((b) => b.classList.toggle("on", (b.dataset.tree === "location") === !!pb.prefs.byLocation));
  const q = lvNorm(pb.search.trim());
  const open = new Map(pbOpenCells().map((c) => [c.channel.id, c.index + 1]));
  const collapsed = (key) => !q && pb.prefs.collapsed.includes(key);
  const chHtml = (ch, depth) => {
    const where = open.get(ch.id);
    return `<div class="lv-node lv-ch ${where ? "live" : ""} ${pb.selChannelId === ch.id ? "sel" : ""}" draggable="true"
      data-ch="${ch.id}" style="--d:${depth}" title="Doble clic: suma el canal a la reproducción sincronizada (hasta 4; con la grilla llena reemplaza el cuadro seleccionado). Ctrl + doble clic deja solo ese canal.">
      <span class="lv-ico ${lvChannelOnline(ch) ? "ok" : ""}">${where ? "▶" : "◉"}</span><span class="lv-label">${esc(ch.name)}</span></div>`;
  };
  let html = "";
  if (!pb.prefs.byLocation) {
    for (const d of pb.devices) {
      const devMatch = !q || lvNorm(d.name).includes(q);
      const chans = devMatch ? d.channels : d.channels.filter((c) => lvNorm(c.name).includes(q));
      if (q && !devMatch && !chans.length) continue;
      const key = `d${d.id}`;
      html += `<div class="lv-node lv-dev" data-key="${key}" style="--d:0">
        <span class="lv-caret ${collapsed(key) ? "" : "open"}" data-caret></span>
        <span class="dot ${d.status === "Online" ? "ok" : "bad"}"></span><b class="lv-label">${esc(d.name)}</b></div>`;
      if (!collapsed(key)) html += chans.map((c) => chHtml(c, 1)).join("");
    }
  } else {
    const walk = (node, depth, ancestorMatch) => {
      const name = node.loc ? node.loc.name : "Sin ubicación";
      const selfMatch = ancestorMatch || !q || lvNorm(name).includes(q);
      const chans = selfMatch ? node.channels : node.channels.filter((c) => lvNorm(c.name).includes(q) || lvNorm(c.device?.name).includes(q));
      const kids = node.children.map((c) => walk(c, depth + 1, selfMatch)).filter(Boolean).join("");
      if (q && !selfMatch && !chans.length && !kids) return "";
      const key = node.loc ? `l${node.loc.id}` : "l0";
      const kind = node.loc && typeof LOC_KINDS !== "undefined" ? LOC_KINDS[node.loc.kind] : null;
      const icon = typeof resIcon === "function" ? resIcon(node.loc ? (kind?.icon ?? "pin") : "inbox", 13) : "";
      return `<div class="lv-node lv-loc" data-key="${key}" style="--d:${depth}">
          <span class="lv-caret ${collapsed(key) ? "" : "open"}" data-caret></span>
          <span class="lv-locico">${icon}</span><b class="lv-label">${esc(name)}</b><span class="lv-count">${node.total}</span></div>` +
        (collapsed(key) ? "" : kids + chans.map((c) => chHtml(c, depth + 1)).join(""));
    };
    html = pb.locRoots.map((n) => walk(n, 0, false)).join("");
  }
  if (!pb.channels.size) html = `<div class="muted lv-small" style="padding:10px 14px">No hay cámaras disponibles.</div>`;
  else if (q && !html.includes("lv-ch")) html = `<div class="muted lv-small" style="padding:10px 14px">Ningún canal coincide con la búsqueda.</div>`;
  host.innerHTML = html;
}

// ---------------------------------------------------------------------------
// Canales en la grilla
// ---------------------------------------------------------------------------
function pbApplyGrid(size) {
  if (size === 1) {
    // Volver a un canal cierra los demás (queda el seleccionado).
    const keep = pb.cells[pb.focused]?.channel ? pb.cells[pb.focused] : pb.cells.find((c) => c.channel);
    if (keep && keep.index !== 0) {
      const first = pb.cells[0];
      first.clear();
      pbMoveChannel(keep, first);
    }
    pb.cells.slice(1).forEach((c) => c.clear());
    pb.focused = 0;
  }
  pb.gridSize = size;
  $("#pb-grid").className = `lv-grid pb-grid g${size}`;
  $$(".pb-gridseg [data-grid]").forEach((b) => b.classList.toggle("on", Number(b.dataset.grid) === size));
  pb.cells.forEach(pbRenderCell);
  pbRenderToolbar();
  pbRenderTree();
  pbDraw();
}

/** Lleva el canal (con su video) de un cuadro a otro vacío. */
function pbMoveChannel(from, to) {
  for (const k of ["channel", "segments", "segsLoaded", "session", "video", "rangeStart", "t0", "speed", "status", "error", "audio"]) to[k] = from[k];
  if (to.video) to.body.prepend(to.video);
  from.channel = null; from.session = null; from.video = null; from.segments = []; from.status = ""; from.error = ""; from.audio = false;
  pbRenderCell(from);
  pbRenderCell(to);
}

async function pbAddChannel(ch, { only = false, target = null } = {}) {
  if (!only && pb.cells.some((c) => c.channel?.id === ch.id && c.index < pb.gridSize)) {
    toast(`"${ch.name}" ya está en la grilla.`);
    return;
  }
  let cell = target;
  if (only) {
    pb.cells.forEach((c) => c.clear());
    pbApplyGrid(1);
    cell = pb.cells[0];
  } else if (!cell) {
    cell = pb.cells.slice(0, pb.gridSize).find((c) => !c.channel);
    if (!cell && pb.gridSize === 1) { pbApplyGrid(4); cell = pb.cells[1]; }
    if (!cell) cell = pb.cells[pb.focused];
  } else if (cell.index >= pb.gridSize) {
    pbApplyGrid(4);
  }
  cell.clear();
  cell.channel = ch;
  pb.focused = cell.index;
  pbRenderCell(cell);
  pbRenderTree();
  pbRenderToolbar();
  await pbLoadSegments(cell);
  if (!pb.active || cell.channel !== ch) return;
  // Se suma a la hora que se está viendo; si no hay nada andando parte en el
  // instante marcado o en su primera grabación del día.
  if (pb.playing) cell.openAt(pb.playhead, pb.seq);
  else if (cell.segments.length) {
    const inside = cell.segments.some((s) => pb.playhead >= s.s && pb.playhead < s.e);
    pbSeek(inside ? pb.playhead : cell.segments[0].s);
  }
}

async function pbLoadSegments(cell) {
  const ch = cell.channel;
  const day = pb.day;
  cell.segsLoaded = false;
  cell.status = "";
  pb.loadingSegs++;
  pbRenderToolbar();
  pbStatus(`Consultando grabaciones de "${ch.name}" del ${pbDate(day)}…`);
  try {
    const list = await Api.get(`/api/playback/${ch.deviceId}/${ch.channelNumber}/segments?date=${pbIso(day).slice(0, 10)}`);
    if (cell.channel !== ch || pb.day !== day) return;
    cell.segments = list.map((s) => ({ s: pbParse(s.start), e: pbParse(s.end), kind: s.kind }))
      .filter((s) => s.e > s.s).sort((a, b) => a.s - b.s);
    cell.error = "";
    pbStatus(cell.segments.length
      ? "Clic en la línea de tiempo para reproducir desde esa hora; arrastre para recorrerla. Para marcar un tramo, active «Recorte»."
      : `Sin grabaciones el ${pbDate(day)}. El equipo conserva según su propio disco y política.`);
    if (!cell.segments.length) cell.status = "Sin grabaciones este día.";
  } catch (err) {
    if (cell.channel !== ch) return;
    cell.segments = [];
    cell.error = err.error || "No se pudieron consultar las grabaciones.";
  } finally {
    pb.loadingSegs--;
    cell.segsLoaded = true;
    pbRenderCell(cell);
    pbRenderToolbar();
    pbDraw();
  }
}

// ---------------------------------------------------------------------------
// Transporte (sincronizado: todos los canales a la misma hora del equipo)
// ---------------------------------------------------------------------------
async function pbSeek(t) {
  const cells = pbOpenCells();
  if (!cells.length) return;
  t = Math.min(Math.max(t, pb.day), pb.day + PB_DAY - 1000);
  const inAny = cells.some((c) => c.segments.some((s) => t >= s.s && t < s.e));
  if (!inAny) {
    const next = Math.min(...cells.flatMap((c) => c.segments.filter((s) => s.s > t).map((s) => s.s)));
    if (!isFinite(next)) { pbStatus("No hay grabación desde esa hora en adelante."); return; }
    t = next;
  }
  const seq = ++pb.seq;
  pb.playhead = t;
  pb.playing = true;
  pb.paused = false;
  // Hasta que llegue la imagen nueva (o PB_LOCK_MS) la línea de tiempo no se
  // puede volver a mover: el operador ve el cabezal quieto en la hora elegida.
  pb.lock = { seq, until: Date.now() + PB_LOCK_MS };
  pbStatus(`Abriendo grabación de las ${pbHms(t)}…`);
  pbRenderToolbar();
  pbDraw();
  await Promise.all(cells.filter((c) => c.segments.length).map((c) => c.openAt(t, seq)));
  if (pb.lock?.seq === seq) pb.lock = null;
  if (seq === pb.seq && pb.active && pbOpenCells().some((c) => c.session)) pbStatus("");
}

/** Salto en curso: la línea de tiempo no acepta clics ni arrastres (como mucho PB_LOCK_MS). */
function pbTimelineLocked() {
  return !!pb.lock && pb.lock.seq === pb.seq && Date.now() < pb.lock.until;
}

function pbTogglePause() {
  if (pb.playing) {
    pb.seq++;
    pb.playing = false;
    pb.paused = true;
    pbOpenCells().forEach((c) => { c.freeze(); c.idle(); });
    pbStatus("En pausa.");
  } else if (pbOpenCells().length) {
    pbSeek(pb.playhead);
  }
  pbRenderToolbar();
}

function pbStop() {
  pb.seq++;
  pb.playing = false;
  pb.paused = false;
  pbOpenCells().forEach((c) => { c.dropSession(); c.body.querySelector(".pb-poster").hidden = true; c.idle(); });
  pbStatus("Reproducción detenida.");
  pbRenderToolbar();
}

function pbSkip(seconds) {
  if (!pbOpenCells().length) return;
  pbSeek(Math.min(Math.max(pb.playhead + seconds * 1000, pb.day), pb.day + PB_DAY - 1000));
}

function pbSetSpeed(dir) {
  const i = PB_SPEEDS.indexOf(pb.speed);
  const next = PB_SPEEDS[Math.min(Math.max(i + dir, 0), PB_SPEEDS.length - 1)];
  if (next === pb.speed) return;
  pb.speed = next;
  pbRenderToolbar();
  pbStatus(`Velocidad ${String(next).replace(".", ",")}×.`);
  if (pb.playing) pbSeek(pb.playhead);
}

async function pbSetDay(day) {
  day = pbDayStart(day);
  if (day > pbToday()) return;
  const wasPlaying = pb.playing;
  const timeOfDay = pb.playhead - pb.day;
  pb.seq++;
  pbOpenCells().forEach((c) => { c.dropSession(); c.body.querySelector(".pb-poster").hidden = true; });
  pb.day = day;
  pb.playhead = day + (wasPlaying ? timeOfDay : 0);
  pb.playing = pb.paused = false;
  pb.sel = null;
  pbRenderToolbar();
  await Promise.all(pbOpenCells().map((c) => pbLoadSegments(c)));
  if (!pb.active || pb.day !== day) return;
  const cells = pbOpenCells();
  if (!cells.some((c) => c.segments.length)) { pbDraw(); return; }
  if (wasPlaying) pbSeek(pb.playhead);
  else pbSeek(Math.min(...cells.flatMap((c) => c.segments.map((s) => s.s))));
}

function pbTick() {
  if (!pb.active) return;
  if (pb.playing) {
    // Un cuadro que está abriendo sigue mostrando el tramo anterior hasta la
    // primera imagen nueva: su posición haría retroceder el cabezal.
    const cell = pbOpenCells().find((c) => !c.opening && c.position != null);
    if (cell) pb.playhead = cell.position;
    if (pb.playhead >= pb.day + PB_DAY - 1000) {
      pbStop();
      pbStatus("Fin del tramo.");
    }
  }
  pbOpenCells().forEach((c) => { if (c.rec) pbRenderCell(c); });
  const clock = $("#pb-clock");
  if (clock) {
    const t = pb.drag?.moved && !pb.cut ? pb.viewCenter : pb.playhead;
    clock.textContent = pbOpenCells().length ? `${pbIso(t).replace("T", " ")}` : "";
  }
  pbDraw();
}

function pbRenderToolbar() {
  if (!$("#pb-date")) return;
  $("#pb-date").innerHTML = `${esc(pbDayLabel(pb.day))} <span class="lv-caret-down">▾</span>`;
  $("#pb-next").disabled = pb.day >= pbToday();
  const n = pbOpenCells().length;
  $("#pb-count").textContent = `${n} de ${pb.gridSize} ${pb.gridSize === 1 ? "canal" : "canales"}`;
  $("#pb-loading").hidden = pb.loadingSegs <= 0;
  const play = $("#pb-play");
  play.innerHTML = pb.playing ? `<b>❚❚</b>` : lvSvg(LV_ICO.play, 14);
  play.disabled = !n;
  $("#pb-stop").disabled = !(pb.playing || pb.paused);
  $$("[data-skip]").forEach((b) => { b.disabled = !n; });
  $("#pb-speed").textContent = `${String(pb.speed).replace(".", ",")}×`;
  $("#pb-slower").disabled = pb.speed <= PB_SPEEDS[0];
  $("#pb-faster").disabled = pb.speed >= PB_SPEEDS[PB_SPEEDS.length - 1];
  $("#pb-cut").classList.toggle("on", pb.cut);
  const z = PB_ZOOMS[pb.zoom];
  $("#pb-zoomlabel").textContent = z >= 60 ? `${z / 60} h` : `${z} min`;
  $("#pb-zout").disabled = pb.zoom <= 0;
  $("#pb-zin").disabled = pb.zoom >= PB_ZOOMS.length - 1;
  const sel = pb.sel;
  $("#pb-seltext").textContent = sel
    ? `${pbHms(sel.a)} – ${pbHms(sel.b)}  (${Math.max(1, Math.round((sel.b - sel.a) / 60000))} min)`
    : "Arrastre sobre la línea de tiempo para marcar un tramo";
  $("#pb-seltext").classList.toggle("muted", !sel);
  $("#pb-selclear").hidden = !sel;
  $("#pb-playsel").disabled = !sel || !n;
  const exp = $("#pb-export");
  if (exp) exp.disabled = !n;
}

// ---------------------------------------------------------------------------
// Línea de tiempo: el cabezal queda al centro y la franja se mueve debajo
// ---------------------------------------------------------------------------
function pbView() {
  const canvas = $("#pb-canvas");
  const w = canvas?.clientWidth || 1;
  const span = PB_ZOOMS[pb.zoom] * 60000;
  const center = pb.viewCenter ?? pb.playhead;
  return { w, span, center, from: center - span / 2, msPerPx: span / w };
}

function pbTimeAt(x) { const v = pbView(); return v.from + x * v.msPerPx; }

function pbDraw() {
  const canvas = $("#pb-canvas");
  if (!canvas || !pb.active) return;
  const host = $("#pb-timeline");
  host.classList.toggle("locked", pbTimelineLocked());
  const rows = pbOpenCells();
  const rowCount = Math.max(1, rows.length);
  const RULER = 22, ROWH = rowCount === 1 ? 26 : 13, PAD = 4;
  const h = RULER + rowCount * ROWH + (rowCount - 1) * 2 + PAD * 2;
  const w = host.clientWidth;
  const dpr = window.devicePixelRatio || 1;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    canvas.style.width = `${w}px`;
    canvas.style.height = `${h}px`;
  }
  const g = canvas.getContext("2d");
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  const css = getComputedStyle(document.documentElement);
  const cText = css.getPropertyValue("--muted").trim() || "#7b8798";
  const cLine = css.getPropertyValue("--border").trim() || "#e2e7ee";
  g.clearRect(0, 0, w, h);
  g.fillStyle = "rgba(10,14,19,.92)";
  g.fillRect(0, 0, w, h);
  const v = pbView();
  const x = (t) => (t - v.from) / v.msPerPx;

  // Fuera del día elegido: oscurecido.
  const d0 = x(pb.day), d1 = x(pb.day + PB_DAY);
  g.fillStyle = "rgba(0,0,0,.55)";
  if (d0 > 0) g.fillRect(0, 0, Math.min(d0, w), h);
  if (d1 < w) g.fillRect(Math.max(d1, 0), 0, w - Math.max(d1, 0), h);

  // Regla
  const [labelS, tickS] = PB_RULER[PB_ZOOMS[pb.zoom]];
  g.font = "11px " + (css.getPropertyValue("--font").trim() || "sans-serif");
  g.textBaseline = "top";
  const startTick = Math.floor(v.from / (tickS * 1000)) * tickS * 1000;
  for (let t = startTick; t <= v.from + v.span; t += tickS * 1000) {
    const px = Math.round(x(t)) + 0.5;
    const local = t - pbDayStart(t) - 0; // posición dentro de su día (para rotular cada labelS)
    const isLabel = Math.round(local / 1000) % labelS === 0;
    g.strokeStyle = isLabel ? "rgba(255,255,255,.35)" : "rgba(255,255,255,.15)";
    g.beginPath(); g.moveTo(px, isLabel ? 12 : 16); g.lineTo(px, RULER); g.stroke();
    if (isLabel) {
      g.fillStyle = "rgba(255,255,255,.65)";
      const d = new Date(t);
      const text = labelS >= 60 ? `${pb2(d.getHours())}:${pb2(d.getMinutes())}` : `${pb2(d.getHours())}:${pb2(d.getMinutes())}:${pb2(d.getSeconds())}`;
      g.fillText(text, px + 3, 1);
    }
  }
  void cText; void cLine;

  // Una fila por canal abierto
  rows.forEach((cell, i) => {
    const y = RULER + PAD + i * (ROWH + 2);
    g.fillStyle = "rgba(255,255,255,.05)";
    g.fillRect(0, y, w, ROWH);
    for (const s of cell.segments) {
      const a = x(s.s), b = x(s.e);
      if (b < 0 || a > w) continue;
      g.fillStyle = (PB_KINDS[s.kind] ?? PB_KINDS.Other).color;
      g.fillRect(Math.max(a, 0), y, Math.max(1, Math.min(b, w) - Math.max(a, 0)), ROWH);
    }
    if (rows.length > 1 && ROWH >= 13) {
      g.fillStyle = "rgba(255,255,255,.8)";
      g.font = "10px sans-serif";
      g.textBaseline = "middle";
      g.fillText(cell.channel.name, 6, y + ROWH / 2 + 0.5);
    }
  });
  if (!rows.length) {
    g.fillStyle = "rgba(255,255,255,.45)";
    g.textBaseline = "middle";
    g.fillText("Sin canales: elija uno en el árbol", 10, RULER + PAD + ROWH / 2);
  }

  // Tramo marcado
  if (pb.sel) {
    const a = x(pb.sel.a), b = x(pb.sel.b);
    g.fillStyle = "rgba(76,160,255,.28)";
    g.fillRect(a, RULER, b - a, h - RULER);
    g.strokeStyle = "#4CA0FF";
    g.lineWidth = 1;
    g.beginPath(); g.moveTo(a + 0.5, RULER); g.lineTo(a + 0.5, h); g.moveTo(b - 0.5, RULER); g.lineTo(b - 0.5, h); g.stroke();
  }

  // Cabezal (siempre al centro)
  const cx = Math.round(w / 2) + 0.5;
  g.strokeStyle = "#FF4D4D";
  g.lineWidth = 1.6;
  g.beginPath(); g.moveTo(cx, 6); g.lineTo(cx, h); g.stroke();
  g.fillStyle = "#FF4D4D";
  g.beginPath(); g.arc(cx, 6, 3.5, 0, Math.PI * 2); g.fill();
  g.lineWidth = 1;
}

function pbWireTimeline() {
  const host = $("#pb-timeline");
  host.title = "Clic o arrastre: reproducir desde esa hora · rueda: acercar/alejar · clic derecho: borrar la marca";
  host.addEventListener("pointerdown", (e) => {
    if (e.button !== 0 || !pbOpenCells().length || pbTimelineLocked()) return;
    host.setPointerCapture(e.pointerId);
    const rect = host.getBoundingClientRect();
    pb.drag = { x0: e.clientX, t0: pbTimeAt(e.clientX - rect.left), center0: pb.playhead, moved: false };
  });
  host.addEventListener("pointermove", (e) => {
    const d = pb.drag;
    if (!d) return;
    const dx = e.clientX - d.x0;
    if (Math.abs(dx) >= 3) d.moved = true;
    if (!d.moved) return;
    const rect = host.getBoundingClientRect();
    if (pb.cut) {
      const t = pbTimeAt(e.clientX - rect.left);
      const a = Math.max(Math.min(d.t0, t), pb.day), b = Math.min(Math.max(d.t0, t), pb.day + PB_DAY);
      pb.sel = b > a ? { a, b } : null;
      pbRenderToolbar();
    } else {
      pb.viewCenter = Math.min(Math.max(d.center0 - dx * pbView().msPerPx, pb.day), pb.day + PB_DAY - 1000);
      const chip = $("#pb-chipdrag");
      chip.hidden = false;
      chip.textContent = pbHms(pb.viewCenter);
    }
    pbDraw();
  });
  const end = (e) => {
    const d = pb.drag;
    if (!d) return;
    pb.drag = null;
    $("#pb-chipdrag").hidden = true;
    const rect = host.getBoundingClientRect();
    if (!d.moved) { pb.viewCenter = null; pbSeek(pbTimeAt(e.clientX - rect.left)); return; }
    if (pb.cut) { pbDraw(); return; }
    const target = pb.viewCenter;
    pb.viewCenter = null;
    if (target != null) pbSeek(target);
  };
  host.addEventListener("pointerup", end);
  host.addEventListener("pointercancel", () => { pb.drag = null; pb.viewCenter = null; $("#pb-chipdrag").hidden = true; pbDraw(); });
  host.addEventListener("wheel", (e) => {
    e.preventDefault();
    pbZoom(e.deltaY < 0 ? 1 : -1);
  }, { passive: false });
  host.addEventListener("contextmenu", (e) => {
    e.preventDefault();
    pb.sel = null;
    pbRenderToolbar();
    pbDraw();
  });
}

function pbZoom(dir) {
  const z = Math.min(Math.max(pb.zoom + dir, 0), PB_ZOOMS.length - 1);
  if (z === pb.zoom) return;
  pb.zoom = pb.prefs.zoom = z;
  pbSavePrefs();
  pbRenderToolbar();
  pbDraw();
}

// ---------------------------------------------------------------------------
// Calendario: marca los días con grabación del canal seleccionado
// ---------------------------------------------------------------------------
const PB_MONTHS = ["Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"];

function pbCalChannel() { return pb.cells[pb.focused]?.channel ?? pbOpenCells()[0]?.channel ?? null; }

async function pbCalDays(ch, y, m) {
  if (!ch) return new Set();
  const key = `${ch.deviceId}/${ch.channelNumber}/${y}/${m}`;
  const now = new Date();
  const current = y === now.getFullYear() && m === now.getMonth() + 1;
  const hit = pb.daysCache.get(key);
  // Los meses cerrados no cambian; el actual se vuelve a preguntar cada 5 min.
  if (hit && (!current || Date.now() - hit.at < 300000)) return hit.days;
  let days = [];
  try { days = await Api.get(`/api/playback/${ch.deviceId}/${ch.channelNumber}/days?year=${y}&month=${m}`); } catch { /* sin marcas */ }
  const set = new Set(days);
  pb.daysCache.set(key, { days: set, at: Date.now() });
  return set;
}

async function pbRenderCal() {
  const pop = $("#pb-cal");
  const { y, m } = pb.calMonth;
  const first = new Date(y, m - 1, 1);
  const lead = (first.getDay() + 6) % 7; // semana desde el lunes
  const today = pbToday();
  const now = new Date();
  const isFutureMonth = (yy, mm) => yy > now.getFullYear() || (yy === now.getFullYear() && mm > now.getMonth() + 1);
  const draw = (marks, loading) => {
    let cells = "";
    for (let i = 0; i < 42; i++) {
      const d = new Date(y, m - 1, 1 - lead + i);
      const ms = d.getTime();
      const inMonth = d.getMonth() === m - 1;
      const cls = [inMonth ? "" : "out", ms === today ? "today" : "", ms === pb.day ? "on" : "",
        inMonth && marks.has(d.getDate()) ? "rec" : ""].join(" ");
      cells += `<button type="button" class="pb-day ${cls}" data-day="${ms}" ${ms > today ? "disabled" : ""}>${d.getDate()}</button>`;
    }
    const ch = pbCalChannel();
    pop.innerHTML = `
      <div class="pb-cal-head">
        <button type="button" class="lv-icon-btn" data-cal="-1" title="Mes anterior">◀</button>
        <b>${PB_MONTHS[m - 1]} ${y}</b>
        <button type="button" class="lv-icon-btn" data-cal="1" title="Mes siguiente" ${isFutureMonth(m === 12 ? y + 1 : y, m === 12 ? 1 : m + 1) ? "disabled" : ""}>▶</button>
      </div>
      <div class="pb-cal-grid">${["L", "M", "M", "J", "V", "S", "D"].map((d) => `<span>${d}</span>`).join("")}${cells}</div>
      <div class="pb-cal-foot"><span class="pb-dot"></span> con grabación${ch ? ` · ${esc(ch.name)}` : ""}
        ${loading ? `<span class="lv-bulk"><i></i></span>` : ""}</div>`;
  };
  draw(new Set(), !!pbCalChannel());
  const marks = await pbCalDays(pbCalChannel(), y, m);
  if (pb.calMonth?.y === y && pb.calMonth?.m === m && !pop.hidden) draw(marks, false);
}

function pbOnOutside(e) {
  if (!e.target.closest?.("#pb-cal, #pb-date")) { const p = $("#pb-cal"); if (p) p.hidden = true; }
}

// ---------------------------------------------------------------------------
// Captura, cápsula y exportación
// ---------------------------------------------------------------------------
function pbAuditClient(action, cell, fileName, at) {
  const ch = cell.channel;
  Api.post("/api/audit/client-event", {
    action, deviceId: ch.deviceId, deviceName: ch.device?.name ?? null,
    channelNumber: ch.channelNumber, channelName: ch.name, filePath: fileName,
    detail: `instante grabado: ${pbDate(at)} ${pbHms(at)} · panel web, carpeta de descargas del navegador`,
  }).catch(() => { /* la auditoría nunca bloquea al operador */ });
}

function pbSnapshot(cell) {
  const v = cell.video;
  const at = cell.position;
  if (!v || !v.videoWidth || at == null) { toast("Este cuadro todavía no tiene imagen.", true); return; }
  const c = document.createElement("canvas");
  c.width = v.videoWidth;
  c.height = v.videoHeight;
  c.getContext("2d").drawImage(v, 0, 0);
  const name = `${lvSafeName(cell.title)} ${pbStamp(at)}.png`;
  c.toBlob((blob) => {
    if (!blob) return;
    lvDownload(blob, name);
    pbAuditClient("playback-snapshot", cell, name, at);
    toast(`Captura guardada: ${name}`);
  }, "image/png");
}

function pbToggleRec(cell) {
  if (cell.rec) { cell.stopRec(false); pbRenderCell(cell); return; }
  const at = cell.position;
  if (!cell.session || at == null || typeof MediaRecorder === "undefined") { toast("Este cuadro todavía no tiene imagen.", true); return; }
  const type = LV_REC_TYPES.find((t) => MediaRecorder.isTypeSupported(t)) || "";
  const recorder = new MediaRecorder(cell.session.stream, type ? { mimeType: type } : undefined);
  const chunks = [];
  const rec = { recorder, startedAt: Date.now(), at, discard: false };
  recorder.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
  recorder.onstop = () => {
    if (rec.discard || !chunks.length) return;
    const ext = (recorder.mimeType || type).includes("mp4") ? "mp4" : "webm";
    const name = `${lvSafeName(cell.title)} ${pbStamp(rec.at)}.${ext}`;
    lvDownload(new Blob(chunks, { type: recorder.mimeType || type || "video/webm" }), name);
    pbAuditClient("playback-clip-saved", cell, name, rec.at);
    toast(`Cápsula guardada: ${name}`);
  };
  recorder.start(1000);
  cell.rec = rec;
  pbRenderCell(cell);
}

function pbExportDialog() {
  const open = pbOpenCells();
  if (!open.length) return;
  const focused = pb.cells[pb.focused]?.channel ? pb.cells[pb.focused] : open[0];
  const a = pb.sel?.a ?? (pb.playhead || pb.day + 12 * 3600000);
  const b = pb.sel?.b ?? Math.min(a + 3600000, pb.day + PB_DAY - 1000);
  const local = (ms) => pbIso(ms);
  openModal(`
    <h3>Exportar grabaciones</h3>
    <div class="field"><label>Cámara</label>
      <select id="pbx-ch">${open.map((c) => `<option value="${c.index}" ${c === focused ? "selected" : ""}>${esc(c.title)}</option>`).join("")}</select></div>
    <div class="form-grid">
      <div class="field"><label>Inicio (fecha y hora)</label><input type="datetime-local" step="1" id="pbx-a" value="${local(a)}"></div>
      <div class="field"><label>Fin (fecha y hora)</label><input type="datetime-local" step="1" id="pbx-b" value="${local(b)}"></div>
    </div>
    <div class="field"><label>Formato de exportación</label>
      <label class="radio-row"><input type="radio" name="pbx-fmt" value="mp4" checked> MP4 — compatible con cualquier reproductor (audio convertido a AAC)</label>
      <label class="radio-row"><input type="radio" name="pbx-fmt" value="mkv"> MKV — copia exacta del equipo (video y audio originales)</label>
    </div>
    <p class="muted field-hint">Una sola cápsula de hasta 2 horas. El servidor la saca del equipo y el navegador la guarda en su carpeta de descargas (puede tardar en empezar mientras el equipo responde).</p>
    <div class="error-box" id="pbx-err" hidden></div>
    <div class="modal-actions">
      <button type="button" class="btn ghost" id="pbx-cancel">Cancelar</button>
      <button type="button" class="btn" id="pbx-ok">Descargar</button>
    </div>`, true);
  $("#pbx-cancel").onclick = closeModal;
  $("#pbx-ok").onclick = () => {
    const err = $("#pbx-err");
    const start = new Date($("#pbx-a").value).getTime(), end = new Date($("#pbx-b").value).getTime();
    const fail = (m) => { err.textContent = m; err.hidden = false; };
    if (!isFinite(start) || !isFinite(end)) return fail("Complete el inicio y el fin.");
    if (end <= start) return fail("El fin debe ser posterior al inicio.");
    if (end - start > 2 * 3600000) return fail("Una sola cápsula admite hasta 2 horas: acorte el tramo.");
    const cell = pb.cells[Number($("#pbx-ch").value)];
    const ch = cell.channel;
    const fmt = $('input[name="pbx-fmt"]:checked').value;
    const url = `/api/playback/${ch.deviceId}/${ch.rtspChannel}/download?start=${encodeURIComponent(pbIso(start))}` +
      `&end=${encodeURIComponent(pbIso(end))}&format=${fmt}`;
    Api.download(url, `${lvSafeName(ch.device?.name)}_${lvSafeName(ch.name)}_${pbIso(start).replace(/[-:]/g, "").replace("T", "_")}.${fmt}`);
    closeModal();
    toast(`Preparando la descarga de ${Math.round((end - start) / 60000)} min en el equipo… aparecerá en las descargas del navegador.`);
  };
}

// ---------------------------------------------------------------------------
// Eventos
// ---------------------------------------------------------------------------
function pbOnKey(e) {
  if (e.key === "Escape" && !$("#pb-cal")?.hidden) $("#pb-cal").hidden = true;
}

function pbWire() {
  $$(".pb-root .lv-seg [data-tree]").forEach((b) => b.addEventListener("click", () => {
    pb.prefs.byLocation = b.dataset.tree === "location";
    pbSavePrefs();
    pbRenderTree();
  }));
  $("#pb-search").addEventListener("input", (e) => { pb.search = e.target.value; pbRenderTree(); });
  $("#pb-handle").addEventListener("click", () => {
    pb.prefs.treeCollapsed = !pb.prefs.treeCollapsed;
    $("#pb-root").classList.toggle("tree-collapsed", pb.prefs.treeCollapsed);
    pbSavePrefs();
    setTimeout(pbDraw, 50);
  });

  const tree = $("#pb-tree");
  tree.addEventListener("click", (e) => {
    const node = e.target.closest(".lv-node");
    if (!node) return;
    if (e.target.closest("[data-caret]") && node.dataset.key) {
      const list = pb.prefs.collapsed, i = list.indexOf(node.dataset.key);
      if (i >= 0) list.splice(i, 1); else list.push(node.dataset.key);
      pbSavePrefs();
      pbRenderTree();
      return;
    }
    if (node.dataset.ch) { pb.selChannelId = Number(node.dataset.ch); pbRenderTree(); }
  });
  tree.addEventListener("dblclick", (e) => {
    const node = e.target.closest(".lv-ch");
    if (!node) return;
    const ch = pb.channels.get(Number(node.dataset.ch));
    if (ch) pbAddChannel(ch, { only: e.ctrlKey });
  });
  tree.addEventListener("dragstart", (e) => {
    const node = e.target.closest?.(".lv-ch");
    if (!node) { e.preventDefault(); return; }
    e.dataTransfer.effectAllowed = "copy";
    e.dataTransfer.setData("text/x-tcvms-channel", node.dataset.ch);
  });

  const grid = $("#pb-grid");
  grid.addEventListener("click", (e) => {
    const cell = e.target.closest(".pb-cell")?._pbCell;
    if (!cell) return;
    const act = e.target.closest("[data-act]")?.dataset.act;
    if (act === "close") {
      cell.clear();
      if (!pbOpenCells().length) pbStop();
      pbRenderTree(); pbRenderToolbar(); pbDraw();
      return;
    }
    if (act === "snap") return pbSnapshot(cell);
    if (act === "rec") return pbToggleRec(cell);
    if (act === "audio") {
      const on = !cell.audio;
      // Audio de un solo cuadro a la vez.
      pb.cells.forEach((c) => { c.audio = false; if (c.video) c.video.muted = true; pbRenderCell(c); });
      cell.audio = on;
      if (cell.video) cell.video.muted = !on;
      pbRenderCell(cell);
      return;
    }
    pb.focused = cell.index;
    pb.cells.forEach(pbRenderCell);
  });
  grid.addEventListener("dblclick", (e) => {
    const cell = e.target.closest(".pb-cell")?._pbCell;
    if (!cell?.channel || e.target.closest(".lv-bar")) return;
    // Tercio izquierdo −30 s, derecho +30 s, centro pantalla completa.
    const r = cell.body.getBoundingClientRect();
    const f = (e.clientX - r.left) / r.width;
    if (f < 1 / 3) pbSkip(-30);
    else if (f > 2 / 3) pbSkip(30);
    else pbToggleFull();
  });
  grid.addEventListener("dragover", (e) => {
    if (!e.dataTransfer.types.includes("text/x-tcvms-channel")) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "copy";
    $$(".pb-cell.drop").forEach((c) => c.classList.remove("drop"));
    e.target.closest(".pb-cell")?.classList.add("drop");
  });
  grid.addEventListener("dragleave", (e) => { if (!grid.contains(e.relatedTarget)) $$(".pb-cell.drop").forEach((c) => c.classList.remove("drop")); });
  grid.addEventListener("drop", (e) => {
    e.preventDefault();
    $$(".pb-cell.drop").forEach((c) => c.classList.remove("drop"));
    const ch = pb.channels.get(Number(e.dataTransfer.getData("text/x-tcvms-channel")));
    const cell = e.target.closest(".pb-cell")?._pbCell;
    if (ch && cell) pbAddChannel(ch, { target: cell });
  });

  $$(".pb-gridseg [data-grid]").forEach((b) => b.addEventListener("click", () => pbApplyGrid(Number(b.dataset.grid))));
  $("#pb-prev").onclick = () => pbSetDay(pbAddDays(pb.day, -1));
  $("#pb-next").onclick = () => pbSetDay(pbAddDays(pb.day, 1));
  $("#pb-today").onclick = () => pbSetDay(pbToday());
  $("#pb-date").onclick = () => {
    const pop = $("#pb-cal");
    pop.hidden = !pop.hidden;
    if (!pop.hidden) { const d = new Date(pb.day); pb.calMonth = { y: d.getFullYear(), m: d.getMonth() + 1 }; pbRenderCal(); }
  };
  $("#pb-cal").addEventListener("click", (e) => {
    const nav = e.target.closest("[data-cal]");
    if (nav) {
      let { y, m } = pb.calMonth;
      m += Number(nav.dataset.cal);
      if (m < 1) { m = 12; y--; } else if (m > 12) { m = 1; y++; }
      pb.calMonth = { y, m };
      pbRenderCal();
      return;
    }
    const day = e.target.closest("[data-day]");
    if (day && !day.disabled) { $("#pb-cal").hidden = true; pbSetDay(Number(day.dataset.day)); }
  });
  $("#pb-play").onclick = pbTogglePause;
  $("#pb-stop").onclick = pbStop;
  $$("[data-skip]").forEach((b) => b.addEventListener("click", () => pbSkip(Number(b.dataset.skip))));
  $("#pb-slower").onclick = () => pbSetSpeed(-1);
  $("#pb-faster").onclick = () => pbSetSpeed(1);
  $("#pb-cut").onclick = () => {
    pb.cut = !pb.cut;
    $("#pb-timeline").title = pb.cut
      ? "Modo recorte: arrastre para marcar el tramo a exportar · clic derecho borra la marca"
      : "Clic o arrastre: reproducir desde esa hora · rueda: acercar/alejar · clic derecho: borrar la marca";
    pbRenderToolbar();
  };
  $("#pb-full").onclick = pbToggleFull;
  $("#pb-zout").onclick = () => pbZoom(-1);
  $("#pb-zin").onclick = () => pbZoom(1);
  $("#pb-selclear").onclick = () => { pb.sel = null; pbRenderToolbar(); pbDraw(); };
  $("#pb-playsel").onclick = () => { if (pb.sel) pbSeek(pb.sel.a); };
  $("#pb-export")?.addEventListener("click", pbExportDialog);
  pbWireTimeline();
  document.addEventListener("keydown", pbOnKey, true);
  document.addEventListener("pointerdown", pbOnOutside, true);
}

function pbToggleFull() {
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  else $("#pb-gridwrap")?.requestFullscreen?.().catch(() => {});
}
