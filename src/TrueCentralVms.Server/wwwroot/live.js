// CLR TrueCentral VMS — Aplicaciones → Vista en vivo (panel web).
//
// Réplica en el navegador de la Vista en vivo del cliente de escritorio
// (Views\LiveView.xaml): árbol de cámaras por equipo o por ubicación con
// buscador, grilla con las MISMAS divisiones (ViewModels\VideoLayouts.cs),
// barra por cuadro (audio, P/S, captura, grabación local, cerrar), doble clic
// para maximizar (sube a principal y vuelve a secundario al restaurar),
// arrastrar del árbol al cuadro y de un cuadro a otro, zoom digital, vistas
// guardadas, pantalla completa, pantallas auxiliares (ventanas del
// navegador), PTZ con teclado y el panel de parlantes IP.
//
// El video llega por WebRTC: el navegador manda su oferta a
// /api/streams/webrtc (mismas validaciones, alcance y auditoría que el RTSP
// del cliente) y el medio viaja directo desde el MediaMTX del servidor por su
// puerto ICE. Cada cámara puesta en un cuadro (LvFeed) tiene DOS <video>: los
// cambios de stream se abren en el de atrás y se intercambian cuando ya hay
// imagen, sin pantallazo negro (el doble buffer del cliente).
//
// Se carga después de resources.js (usa LOC_KINDS y resIcon).
"use strict";

// ---------------------------------------------------------------------------
// Divisiones de pantalla: las mismas del cliente y con las MISMAS claves (las
// guardan las vistas guardadas, compartidas entre el cliente y la web).
// ---------------------------------------------------------------------------
function lvCellRect(col, row, colSpan = 1, rowSpan = 1) { return { col, row, colSpan, rowSpan }; }

function lvUniform(columns, rows, key) {
  const cells = [];
  for (let r = 0; r < rows; r++) for (let c = 0; c < columns; c++) cells.push(lvCellRect(c, r));
  const name = String(columns * rows);
  return { key: key ?? name, name, columns, rows, cells };
}

/** Un cuadro grande de (lado-1)×(lado-1) arriba a la izquierda y la L en cuadros simples. */
function lvOneBig(side) {
  const cells = [lvCellRect(0, 0, side - 1, side - 1)];
  for (let r = 0; r < side - 1; r++) cells.push(lvCellRect(side - 1, r));
  for (let c = 0; c < side; c++) cells.push(lvCellRect(c, side - 1));
  return { key: `${2 * side} con principal`, name: String(2 * side), columns: side, rows: side, cells };
}

/** División dibujada como mapa de caracteres (cada letra, un cuadro rectangular). */
function lvMap(key, ...rows) {
  const columns = rows[0].length, order = [], bounds = new Map();
  rows.forEach((line, r) => [...line].forEach((ch, c) => {
    const b = bounds.get(ch);
    if (b) { b.col = Math.min(b.col, c); b.maxCol = Math.max(b.maxCol, c); b.maxRow = r; }
    else { order.push(ch); bounds.set(ch, { col: c, row: r, maxCol: c, maxRow: r }); }
  }));
  const cells = order.map((ch) => {
    const b = bounds.get(ch);
    return lvCellRect(b.col, b.row, b.maxCol - b.col + 1, b.maxRow - b.row + 1);
  });
  return { key, name: String(cells.length), columns, rows: rows.length, cells };
}

/** Cuadrícula a medida (las de "abrir un equipo completo"): clave "8×5". */
function lvGrid(columns, rows) {
  columns = Math.min(Math.max(columns | 0, 1), 16);
  rows = Math.min(Math.max(rows | 0, 1), 16);
  const layout = lvUniform(columns, rows);
  layout.key = layout.name = `${columns}×${rows}`;
  return layout;
}

/** Grilla con el mínimo de cuadros sobrantes, algo más ancha que alta (VideoLayout.FitFor). */
function lvFitFor(count) {
  count = Math.min(Math.max(count, 1), 64);
  let best = [1, count], bestScore = Infinity;
  for (let cols = 1; cols <= count; cols++) {
    const rows = Math.ceil(count / cols);
    if (cols < rows || cols > rows * 2 + 1) continue;
    const score = (cols * rows - count) * 100 + (cols - rows);
    if (score < bestScore) { bestScore = score; best = [cols, rows]; }
  }
  return lvGrid(best[0], best[1]);
}

const LV_LAYOUT_GROUPS = [
  { title: "Uniformes", layouts: [lvUniform(1, 1), lvUniform(2, 2), lvUniform(3, 3), lvUniform(4, 4), lvUniform(5, 5), lvUniform(6, 6), lvUniform(8, 8)] },
  {
    title: "Con cuadro principal", layouts: [
      lvOneBig(3), lvOneBig(4), lvMap("9 con principal", "AABC", "AADE", "FGHI"), lvOneBig(5), lvOneBig(6), lvOneBig(8),
      lvMap("17 con principal", "AAABC", "AAADE", "AAAFG", "HIJKL", "MNOPQ"),
    ],
  },
  {
    title: "En columnas", layouts: [
      lvUniform(2, 1, "2 en columnas"), lvMap("3 en columnas", "AB", "AC"), lvMap("5 en columnas", "AABC", "AADE"),
      lvUniform(3, 2, "6 en columnas"), lvUniform(4, 2, "8 en columnas"),
    ],
  },
  {
    title: "En filas", layouts: [
      lvUniform(1, 2, "2 en filas"), lvMap("3 en filas", "AA", "BC"), lvMap("5 en filas", "AA", "AA", "BC", "DE"),
      lvUniform(2, 3, "6 en filas"), lvUniform(2, 4, "8 en filas"),
    ],
  },
  {
    title: "Combinadas", layouts: [
      lvMap("4 combinada A", "AB", "AC", "AD"),
      lvMap("4 combinada B", "AAA", "BCD"),
      lvMap("5 combinada", "AB", "AC", "AD", "AE"),
      lvMap("6 combinada", "AB", "AB", "CD", "EF"),
      lvMap("7 combinada A", "AABB", "AABB", "CCDE", "CCFG"),
      lvMap("7 combinada B", "AAABBBCC", "AAABBBCC", "AAABBBDD", "EEEFFFDD", "EEEFFFGG", "EEEFFFGG"),
      lvUniform(3, 4, "12 combinada"),
      lvMap("13 combinada", "ABCD", "EFFG", "HFFI", "JKLM"),
      lvMap("24 combinada", "AABBCCDD", "AABBCCDD", "EEFFGGHH", "EEFFGGHH", "IJKLMNOP", "QRSTUVWX"),
      lvMap("32 combinada", "AABBCDE", "AABBFGH", "IIJJJKL", "IIJJJMN", "OPJJJQR", "STUVWXY", "Zabcdef"),
      lvMap("36 combinada", "AABBCCDD", "AABBCCDD", "EFGHIJKL", "MNOPQRST", "UVWXYZab", "cdefghij"),
      lvUniform(8, 6, "48 combinada"),
    ],
  },
];
const LV_LAYOUTS = LV_LAYOUT_GROUPS.flatMap((g) => g.layouts);
const LV_LEGACY_KEYS = { "6": "6 con principal", "8": "8 con principal", "13": "13 combinada" };

function lvFindLayout(key) {
  if (!key) return null;
  key = LV_LEGACY_KEYS[key] ?? key;
  return LV_LAYOUTS.find((l) => l.key === key) ?? null;
}
/** División de una vista guardada: la del selector, o una grilla a medida del tamaño guardado. */
function lvRestoreLayout(key, columns, rows) { return lvFindLayout(key) ?? lvGrid(columns || 2, rows || 2); }
/** La división estándar más chica (uniformes o con principal) donde quepan count cuadros. */
function lvSmallestFor(count) {
  const pool = [...LV_LAYOUT_GROUPS[0].layouts, ...LV_LAYOUT_GROUPS[1].layouts]
    .filter((l) => l.cells.length >= count).sort((a, b) => a.cells.length - b.cells.length);
  return pool[0] ?? LV_LAYOUT_GROUPS[0].layouts.at(-1);
}

/** Miniatura de una división (rectángulos proporcionales). */
function lvLayoutPreview(layout, w, h) {
  return `<span class="lv-lp" style="width:${w}px;height:${h}px;grid-template-columns:repeat(${layout.columns},1fr);grid-template-rows:repeat(${layout.rows},1fr)">${
    layout.cells.map((c) => `<i style="grid-column:${c.col + 1}/span ${c.colSpan};grid-row:${c.row + 1}/span ${c.rowSpan}"></i>`).join("")}</span>`;
}

// ---------------------------------------------------------------------------
// Preferencias por navegador (lo que en el cliente es client.json)
// ---------------------------------------------------------------------------
const LV_PREFS_KEY = "tcvms.live.prefs";
const LV_LAST_KEY = "tcvms.live.last";
const LV_PREFS_DEFAULTS = {
  layout: "4", byLocation: false, treeCollapsed: false, collapsedNodes: [],
  ptzOpen: false, ptzSpeed: 4, spkOpen: false, speakers: [], tone: true, mic: "",
  profile: "auto", fitDevice: true, stretch: false, reopen: false, snapFormat: "jpg",
};

function lvLoadPrefs(aux) {
  let prefs = { ...LV_PREFS_DEFAULTS };
  try { prefs = { ...prefs, ...JSON.parse(localStorage.getItem(LV_PREFS_KEY) || "{}") }; } catch { /* ilegible */ }
  // Las pantallas auxiliares recuerdan su propia división.
  if (aux) {
    try { prefs.layout = JSON.parse(localStorage.getItem(`${LV_PREFS_KEY}.aux${aux}`) || "{}").layout ?? "4"; } catch { prefs.layout = "4"; }
  }
  return prefs;
}

function lvSavePrefs() {
  const p = lv.prefs;
  try {
    if (lv.aux) {
      localStorage.setItem(`${LV_PREFS_KEY}.aux${lv.aux}`, JSON.stringify({ layout: p.layout }));
      // Lo demás (ajustes) es común a todas las ventanas: se guarda sin pisar la división de la principal.
      const main = JSON.parse(localStorage.getItem(LV_PREFS_KEY) || "{}");
      localStorage.setItem(LV_PREFS_KEY, JSON.stringify({ ...p, layout: main.layout ?? LV_PREFS_DEFAULTS.layout }));
    } else {
      localStorage.setItem(LV_PREFS_KEY, JSON.stringify(p));
    }
  } catch { /* modo privado */ }
}

// ---------------------------------------------------------------------------
// Íconos
// ---------------------------------------------------------------------------
function lvSvg(paths, size = 13, extra = "") {
  return `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" ${extra}>${paths}</svg>`;
}
const LV_ICO = {
  mute: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M17 9l4 6M21 9l-4 6"/>',
  audio: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 9a4 4 0 0 1 0 6M19 6.5a8 8 0 0 1 0 11"/>',
  snap: '<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/>',
  zoom: '<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4.3-4.3M11 8v6M8 11h6"/>',
  views: '<rect x="3" y="4" width="8" height="7" rx="1"/><rect x="13" y="4" width="8" height="7" rx="1"/><rect x="3" y="13" width="8" height="7" rx="1"/><rect x="13" y="13" width="8" height="7" rx="1"/>',
  aux: '<rect x="2" y="4" width="13" height="10" rx="1"/><path d="M9 18h7M12 14v4"/><rect x="17" y="8" width="5" height="9" rx="1"/>',
  full: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13.5 6.5l4 4"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
  play: '<path d="M7 5l12 7-12 7z"/>',
  ear: '<path d="M7 9a5 5 0 0 1 10 0c0 3-3 4-3 7a3 3 0 0 1-6 0"/><path d="M10 9a2 2 0 0 1 4 0"/>',
  download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
  checkAll: '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M8 12l3 3 5-6"/>',
  checkNone: '<rect x="3" y="3" width="18" height="18" rx="2"/>',
  chevDown: '<path d="M6 9l6 6 6-6"/>',
  chevUp: '<path d="M6 15l6-6 6 6"/>',
};

// ---------------------------------------------------------------------------
// Estado de la página (una vista en vivo por ventana)
// ---------------------------------------------------------------------------
const lv = {
  active: false,
  aux: 0,                  // 0 = ventana principal; 1..3 = pantalla auxiliar
  windowId: Math.random().toString(36).slice(2),
  prefs: { ...LV_PREFS_DEFAULTS },
  devices: [],             // [{ id, name, status, channels: [ch] }]
  channels: new Map(),     // id de canal → ch (con .device)
  locRoots: [],
  layout: null,
  cells: [],               // LvCell en el orden de la grilla
  selected: null,
  maximized: -1,
  promoted: null,          // feed subido a principal por estar maximizado
  promotedChannel: null,
  zoomMode: false,
  search: "",
  selChannelId: null,
  dropCell: null,
  bulk: null,              // texto de la apertura masiva en curso
  views: [],
  activeView: "",
  ptzChain: Promise.resolve(),
  ptzHeld: null,
  keyHeld: null,
  shift: false,
  auxWindows: [],
  timers: [],
  bc: null,
  speakers: [],
  sounds: [],
  library: [],
  talk: null,
  preview: null,
  treeReload: null,
  lastSaveTimer: null,
};

// ---------------------------------------------------------------------------
// Sesión WebRTC con el media server (una por stream abierto)
// ---------------------------------------------------------------------------
const lvCancelled = () => ({ cancelled: true });

function lvIceGathered(pc, timeoutMs) {
  if (pc.iceGatheringState === "complete") return Promise.resolve();
  return new Promise((resolve) => {
    const check = () => { if (pc.iceGatheringState === "complete") done(); };
    const done = () => { clearTimeout(timer); pc.removeEventListener("icegatheringstatechange", check); resolve(); };
    const timer = setTimeout(done, timeoutMs);
    pc.addEventListener("icegatheringstatechange", check);
  });
}

/** Libera la sesión en el servidor aunque la página se esté cerrando (keepalive). */
function lvDeleteSession(id) {
  try {
    fetch(`/api/streams/webrtc/${encodeURIComponent(id)}`, {
      method: "DELETE", keepalive: true,
      headers: Api.token ? { Authorization: "Bearer " + Api.token } : {},
    }).catch(() => { /* MediaMTX la cerrará igual al perder al lector */ });
  } catch { /* noop */ }
}

class LvSession {
  constructor(channel, profile) {
    this.channel = channel;
    this.profile = profile;
    this.pc = null;
    this.stream = new MediaStream();
    this.id = null;
    this.closed = false;
    this.hasAudio = false;
    this.icePort = null;
    this.onDrop = null;
    this.onAudio = null;
  }

  /** Negocia con el servidor; resuelve con la respuesta aplicada (la imagen llega después). */
  async open() {
    const pc = this.pc = new RTCPeerConnection({ bundlePolicy: "max-bundle" });
    pc.addTransceiver("video", { direction: "recvonly" });
    pc.addTransceiver("audio", { direction: "recvonly" });
    pc.ontrack = (e) => {
      this.stream.addTrack(e.track);
      // MediaMTX anuncia una pista de audio aunque la cámara no tenga: hay
      // audio de verdad recién cuando llegan paquetes (la pista se "desmuta").
      if (e.track.kind === "audio") {
        const mark = () => { this.hasAudio = true; this.onAudio?.(); };
        if (!e.track.muted) mark(); else e.track.addEventListener("unmute", mark, { once: true });
      }
    };
    pc.onconnectionstatechange = () => {
      if (!this.closed && (pc.connectionState === "failed" || pc.connectionState === "closed")) this.onDrop?.();
    };
    await pc.setLocalDescription(await pc.createOffer());
    // Sin STUN los candidatos locales salen al instante: la oferta va completa (sin trickle).
    await lvIceGathered(pc, 1500);
    if (this.closed) throw lvCancelled();
    const res = await Api.post("/api/streams/webrtc", {
      deviceId: this.channel.deviceId, rtspChannel: this.channel.rtspChannel,
      profile: this.profile, offer: pc.localDescription.sdp,
    });
    this.id = res.session;
    if (this.closed) { this.release(); throw lvCancelled(); }
    const port = /a=candidate:\S+ \d+ \S+ \d+ \S+ (\d+) typ host/.exec(res.answer);
    this.icePort = port ? port[1] : null;
    await pc.setRemoteDescription({ type: "answer", sdp: res.answer });
  }

  /** Bytes y cuadros de video recibidos (vigía de señal). */
  async videoStats() {
    if (!this.pc || this.closed) return null;
    try {
      const stats = await this.pc.getStats();
      for (const r of stats.values())
        if (r.type === "inbound-rtp" && r.kind === "video") return { bytes: r.bytesReceived || 0, frames: r.framesDecoded || 0 };
    } catch { /* cerrada entretanto */ }
    return { bytes: 0, frames: 0 };
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    try { this.pc?.close(); } catch { /* ya cerrada */ }
    this.stream.getTracks().forEach((t) => t.stop());
    this.release();
  }

  release() {
    if (!this.id) return;
    const id = this.id;
    this.id = null;
    lvDeleteSession(id);
  }
}

/** Resuelve cuando el <video> ya tiene imagen; rechaza si la conexión se cae o vence el plazo. */
function lvWaitFrame(video, session, timeoutMs) {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const tick = () => {
      if (session.closed) return reject(lvCancelled());
      if (video.readyState >= 2 && video.videoWidth > 0) return resolve();
      const state = session.pc?.connectionState;
      const ice = session.pc?.iceConnectionState;
      if (state === "failed" || Date.now() - started > timeoutMs) {
        const blocked = state === "failed" || ice === "new" || ice === "checking";
        return reject({
          error: blocked
            ? `El video no llegó al navegador: revise que el puerto WebRTC del servidor${session.icePort ? ` (${session.icePort} UDP/TCP)` : ""} esté abierto en el firewall y alcanzable desde este equipo.`
            : "La cámara no entregó imagen a tiempo.",
        });
      }
      setTimeout(tick, 150);
    };
    tick();
  });
}

// ---------------------------------------------------------------------------
// Una cámara puesta en un cuadro. Viaja con su cámara al arrastrarla a otro
// cuadro (lo que se mueve es el escenario con sus <video>: el video no se corta).
// ---------------------------------------------------------------------------
const LV_REC_TYPES = [
  "video/mp4;codecs=avc1,opus", "video/mp4;codecs=avc1,mp4a.40.2", "video/mp4",
  "video/webm;codecs=vp9,opus", "video/webm;codecs=vp8,opus", "video/webm",
];

class LvFeed {
  constructor(channel, profile) {
    this.channel = channel;
    this.profile = profile;           // "Main" | "Sub"
    this.cell = null;
    this.session = null;
    this.pending = null;              // cambio de stream en vuelo
    this.pendingTarget = null;
    this.parked = null;               // secundario estacionado al maximizar
    this.front = 0;
    this.audioOn = false;
    this.connecting = false;
    this.status = "";
    this.error = "";                  // mensaje grande sobre el cuadro (sin video)
    this.seq = 0;
    this.switchSeq = 0;
    this.retryTimer = null;
    this.retryDelay = 5000;
    this.lastBytes = -1;
    this.stall = 0;
    this.rec = null;
    this.flashTimer = null;
    this.zoom = { s: 1, x: 0, y: 0 }; // x, y: desplazamiento en fracción del cuadro
    this.disposed = false;
    this.stage = document.createElement("div");
    this.stage.className = "lv-stage";
    this.stage.innerHTML = `<video class="lv-v front" autoplay playsinline muted></video><video class="lv-v" autoplay playsinline muted></video>`;
    this.videos = [...this.stage.querySelectorAll("video")];
  }

  get title() { return `${this.channel.device?.name ?? "Equipo"} · ${this.channel.name}`; }
  frontVideo() { return this.videos[this.front]; }
  backVideo() { return this.videos[this.front ^ 1]; }

  render() { if (this.cell) lvRenderCell(this.cell); }

  applyFront() {
    this.videos.forEach((v, i) => {
      v.classList.toggle("front", i === this.front);
      v.muted = i !== this.front || !this.audioOn;
    });
    const v = this.frontVideo();
    if (v.paused) v.play().catch(() => { /* sin gesto del usuario: queda en silencio */ });
  }

  setStatus(text) { this.status = text || ""; this.render(); }

  /** Aviso pasajero en la barra del cuadro. */
  flash(text, ms = 6000) {
    clearTimeout(this.flashTimer);
    this.setStatus(text);
    this.flashTimer = setTimeout(() => { if (this.status === text) this.setStatus(""); }, ms);
  }

  clearRetry() { clearTimeout(this.retryTimer); this.retryTimer = null; }

  scheduleRetry(delay) {
    this.clearRetry();
    const wait = delay ?? this.retryDelay;
    this.retryDelay = Math.min(this.retryDelay * 2, 30000);
    this.retryTimer = setTimeout(() => { if (!this.disposed) this.open(); }, wait);
  }

  /** Apertura dura: corta lo que hubiera y negocia de nuevo. Resuelve al tener la respuesta del servidor. */
  async open() {
    const seq = ++this.seq;
    this.switchSeq++;
    this.clearRetry();
    this.pending?.close(); this.pending = null; this.pendingTarget = null;
    this.releaseParked();
    this.session?.close(); this.session = null;
    this.videos.forEach((v) => { v.srcObject = null; });
    this.connecting = true;
    this.render();
    const s = new LvSession(this.channel, this.profile);
    s.onAudio = () => this.render();
    this.session = s;
    try {
      await s.open();
      if (seq !== this.seq) { s.close(); return; }
      const v = this.frontVideo();
      v.srcObject = s.stream;
      this.applyFront();
    } catch (err) {
      this.failed(seq, s, err);
      return;
    }
    this.awaitFirstFrame(seq, s);
  }

  async awaitFirstFrame(seq, s) {
    try {
      await lvWaitFrame(this.frontVideo(), s, 20000);
    } catch (err) {
      this.failed(seq, s, err);
      return;
    }
    if (seq !== this.seq) return;
    this.connecting = false;
    this.error = "";
    this.retryDelay = 5000;
    this.lastBytes = -1; this.stall = 0;
    s.onDrop = () => this.dropped(s);
    this.status = this.status.startsWith("Señal perdida") || this.status.includes("Reintentando") ? "" : this.status;
    this.render();
    lvRefreshTreeLive();
  }

  failed(seq, s, err) {
    if (err?.cancelled || seq !== this.seq || this.disposed) return;
    s.close();
    if (this.session === s) this.session = null;
    this.connecting = false;
    const message = err?.error || err?.message || "No se pudo abrir el video.";
    const detail = String(err?.data?.detail || "").toLowerCase();
    // Lo que no se arregla reintentando: sin permiso, fuera de alcance, canal
    // inexistente o deshabilitado, o un formato que este navegador no decodifica.
    const fatal = err?.status === 403 || err?.status === 404 || detail.includes("codec") ||
      (err?.status === 422 && message.includes("deshabilitado")) || err?.status === 503 && message.includes("deshabilitada");
    this.error = message;
    this.status = fatal ? "Sin video" : "Reintentando…";
    if (!fatal) this.scheduleRetry();
    this.render();
  }

  dropped(s) {
    if (s !== this.session || this.disposed) return;
    this.session = null;
    s.close();
    this.frontVideo().srcObject = null;
    if (this.rec) this.stopRecording();
    this.connecting = true;
    this.status = "Señal perdida. Reconectando…";
    this.render();
    this.scheduleRetry(2000);
  }

  /** Vigía: sin bytes de video por ~12 s = señal perdida (MediaMTX no siempre avisa). */
  async watch() {
    const s = this.session;
    if (!s || this.connecting || this.disposed) return;
    const st = await s.videoStats();
    if (!st || s !== this.session) return;
    if (st.bytes === this.lastBytes) {
      if (++this.stall >= 3) this.dropped(s);
    } else {
      this.stall = 0;
      this.lastBytes = st.bytes;
    }
  }

  /**
   * Cambio suave de stream (P/S, maximizar): el destino se abre en el <video>
   * de atrás y se intercambia cuando ya hay imagen. Dos intentos (el primero
   * deja el pull tibio en MediaMTX). Sin éxito se conserva el actual, salvo
   * hardFallback (la vuelta a secundario al restaurar es obligatoria).
   */
  async switchProfile(target, { hardFallback = false, keepForRestore = false } = {}) {
    if (!this.session || this.connecting) {
      // Todavía conectando: se reabre directo con el perfil pedido.
      this.profile = target;
      return this.open();
    }
    if (this.pendingTarget) {
      if (this.pendingTarget === target) return;            // ya va en camino
      if (target === this.profile) { this.cancelSwitch(); return; } // "quédate como estabas"
    }
    if (target === this.profile) return;
    this.releaseParked();
    const seq = ++this.switchSeq, openSeq = this.seq;
    this.pendingTarget = target;
    this.status = target === "Main" ? "Cambiando a principal…" : "Cambiando a secundario…";
    this.render();
    const stale = () => seq !== this.switchSeq || openSeq !== this.seq || this.disposed;
    for (let attempt = 0; attempt < 2; attempt++) {
      const s = new LvSession(this.channel, target);
      s.onAudio = () => this.render();
      this.pending = s;
      const back = this.backVideo();
      try {
        await s.open();
        if (stale()) { s.close(); return; }
        back.srcObject = s.stream;
        back.muted = true;
        back.play().catch(() => {});
        await lvWaitFrame(back, s, 15000);
        if (stale()) { s.close(); back.srcObject = null; return; }
      } catch (err) {
        s.close();
        if (this.pending === s) back.srcObject = null;
        if (err?.cancelled || stale()) return;
        continue;
      }
      if (this.rec) this.stopRecording();
      const old = this.session, oldProfile = this.profile, oldVideo = this.frontVideo();
      this.front ^= 1;
      this.session = s;
      this.profile = target;
      this.pending = null;
      this.pendingTarget = null;
      this.status = "";
      s.onDrop = () => this.dropped(s);
      this.lastBytes = -1; this.stall = 0;
      this.applyFront();
      if (keepForRestore) {
        // Restaurar el maximizado vuelve a este al instante.
        old.onDrop = () => { if (this.parked?.session === old) this.releaseParked(); };
        this.parked = { session: old, profile: oldProfile };
        oldVideo.muted = true;
      } else {
        old.close();
        oldVideo.srcObject = null;
      }
      this.render();
      lvScheduleLastSave();
      return;
    }
    this.pending = null;
    this.pendingTarget = null;
    if (hardFallback) {
      this.profile = target;
      this.open();
    } else {
      this.flash("El stream destino no entregó imagen: se mantiene el actual.");
    }
  }

  cancelSwitch() {
    this.switchSeq++;
    if (this.pending) {
      this.pending.close();
      this.backVideo().srcObject = null;
    }
    this.pending = null;
    this.pendingTarget = null;
    this.status = "";
    this.render();
  }

  /** Vuelve al secundario estacionado (instantáneo). false si ya no está. */
  restoreParked() {
    const p = this.parked;
    this.parked = null;
    if (!p || p.session.closed) return false;
    if (this.rec) this.stopRecording();
    const cur = this.session, curVideo = this.frontVideo();
    this.switchSeq++;
    this.front ^= 1;
    this.session = p.session;
    this.profile = p.profile;
    p.session.onDrop = () => this.dropped(p.session);
    this.lastBytes = -1; this.stall = 0;
    this.applyFront();
    cur?.close();
    curVideo.srcObject = null;
    this.render();
    lvScheduleLastSave();
    return true;
  }

  releaseParked() {
    if (!this.parked) return;
    this.parked.session.close();
    this.parked = null;
    if (!this.pending) this.backVideo().srcObject = null;
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    if (this.rec) this.stopRecording();
    this.clearRetry();
    clearTimeout(this.flashTimer);
    this.seq++; this.switchSeq++;
    this.pending?.close();
    this.releaseParked();
    this.session?.close();
    this.videos.forEach((v) => { v.srcObject = null; });
    this.stage.remove();
  }

  // --- Herramientas del operador ---------------------------------------

  async snapshot() {
    const v = this.frontVideo();
    if (!v.videoWidth) { this.flash("Todavía no hay imagen para capturar."); return; }
    const canvas = document.createElement("canvas");
    canvas.width = v.videoWidth;
    canvas.height = v.videoHeight;
    canvas.getContext("2d").drawImage(v, 0, 0);
    const png = lv.prefs.snapFormat === "png";
    const name = lvFileName(this.channel, png ? "png" : "jpg");
    canvas.toBlob((blob) => {
      if (!blob) { this.flash("No se pudo generar la captura."); return; }
      lvDownload(blob, name);
      lvAuditClient("live-snapshot", this.channel, name);
      lvStatus(`Captura guardada en Descargas: ${name}`);
    }, png ? "image/png" : "image/jpeg", 0.92);
  }

  toggleRecording() {
    if (this.rec) { this.stopRecording(); return; }
    if (!this.session || this.connecting) { this.flash("Espere a que haya imagen para grabar."); return; }
    if (typeof MediaRecorder === "undefined") { this.flash("Este navegador no permite grabar video."); return; }
    const mime = LV_REC_TYPES.find((t) => MediaRecorder.isTypeSupported(t)) || "";
    let recorder;
    try { recorder = new MediaRecorder(this.session.stream, mime ? { mimeType: mime, videoBitsPerSecond: 4000000 } : undefined); }
    catch (err) { this.flash(`No se pudo iniciar la grabación: ${err.message}`); return; }
    const ext = (recorder.mimeType || mime).includes("mp4") ? "mp4" : "webm";
    const name = lvFileName(this.channel, ext);
    const chunks = [];
    const channel = this.channel;
    recorder.ondataavailable = (e) => { if (e.data && e.data.size) chunks.push(e.data); };
    recorder.onstop = () => {
      if (!chunks.length) return;
      lvDownload(new Blob(chunks, { type: recorder.mimeType || `video/${ext}` }), name);
      lvAuditClient("live-clip-saved", channel, name);
      lvStatus(`Grabación guardada en Descargas: ${name}`);
    };
    recorder.start(1000);
    this.rec = { recorder, startedAt: Date.now(), name };
    lvAuditClient("live-clip-start", channel, name);
    this.render();
  }

  stopRecording() {
    const r = this.rec;
    this.rec = null;
    if (r && r.recorder.state !== "inactive") { try { r.recorder.stop(); } catch { /* ya detenido */ } }
    this.render();
  }

  // --- Zoom digital (recorta la imagen recibida; no mueve la cámara) ---

  applyZoom() {
    const z = this.zoom;
    this.stage.style.transform = z.s > 1.001 ? `translate(${z.x * 100}%, ${z.y * 100}%) scale(${z.s})` : "";
    this.render();
  }

  clampZoom() {
    const z = this.zoom;
    if (z.s <= 1.001) { z.s = 1; z.x = 0; z.y = 0; return; }
    z.x = Math.min(0, Math.max(1 - z.s, z.x));
    z.y = Math.min(0, Math.max(1 - z.s, z.y));
  }

  /** Acerca o aleja manteniendo fijo el punto (fx, fy) del cuadro (fracciones 0..1). */
  zoomAt(fx, fy, factor) {
    const z = this.zoom;
    const s = Math.min(Math.max(z.s * factor, 1), 8);
    z.x = fx - (fx - z.x) * (s / z.s);
    z.y = fy - (fy - z.y) * (s / z.s);
    z.s = s;
    this.clampZoom();
    this.applyZoom();
  }

  /** Lleva el rectángulo marcado (fracciones del cuadro) a todo el cuadro. */
  zoomToRect(x0, y0, x1, y1) {
    const w = Math.abs(x1 - x0), h = Math.abs(y1 - y0);
    if (w < 0.02 || h < 0.02) return;
    const z = this.zoom;
    const s = Math.min(Math.max(z.s * Math.min(1 / w, 1 / h), 1), 8);
    const cx = ((x0 + x1) / 2 - z.x) / z.s, cy = ((y0 + y1) / 2 - z.y) / z.s;
    z.s = s;
    z.x = 0.5 - cx * s;
    z.y = 0.5 - cy * s;
    this.clampZoom();
    this.applyZoom();
  }

  resetZoom() { this.zoom = { s: 1, x: 0, y: 0 }; this.applyZoom(); }
}

// ---------------------------------------------------------------------------
// Cuadros de la grilla (la posición; la cámara es su LvFeed)
// ---------------------------------------------------------------------------
class LvCell {
  constructor() {
    this.index = 0;
    this.feed = null;
    this.el = document.createElement("div");
    this.el.className = "lv-cell empty";
    this.el.innerHTML = `
      <div class="lv-bar" title="">
        <span class="lv-num"></span>
        <span class="lv-title"></span>
        <span class="lv-spin" hidden></span>
        <span class="lv-status"></span>
        <span class="lv-zoomtag" hidden></span>
        <span class="lv-actions">
          <button type="button" class="lv-act" data-act="audio"></button>
          <button type="button" class="lv-act lv-prof" data-act="profile"></button>
          <button type="button" class="lv-act" data-act="snap" title="Guardar una captura de imagen de este cuadro">${lvSvg(LV_ICO.snap, 13)}</button>
          <button type="button" class="lv-act lv-rec" data-act="rec">●</button>
          <span class="lv-rectime" hidden></span>
          <button type="button" class="lv-act lv-close" data-act="close" title="Cerrar el video de este cuadro">✕</button>
        </span>
      </div>
      <div class="lv-body">
        <div class="lv-empty">Clic para seleccionar este cuadro y doble clic en un canal</div>
        <div class="lv-error" hidden></div>
        <div class="lv-zoomrect" hidden></div>
      </div>`;
    this.bar = this.el.querySelector(".lv-bar");
    this.body = this.el.querySelector(".lv-body");
    this.el._lvCell = this;
  }
  get isEmpty() { return !this.feed; }

  /** Pone una cámara (o ninguna) en este cuadro; la anterior se libera. */
  attach(feed) {
    if (this.feed && this.feed !== feed) this.feed.dispose();
    this.feed = feed;
    if (feed) {
      feed.cell = this;
      this.body.prepend(feed.stage);
    }
    lvRenderCell(this);
  }

  /** Suelta la cámara sin liberarla (para moverla a otro cuadro). */
  detach() {
    const feed = this.feed;
    this.feed = null;
    if (feed) feed.cell = null;
    return feed;
  }

  clear() {
    if (lv.promoted && lv.promoted === this.feed) { lv.promoted = null; lv.promotedChannel = null; }
    this.feed?.dispose();
    this.feed = null;
    lvRenderCell(this);
  }
}

function lvRenderCell(cell) {
  const f = cell.feed;
  const el = cell.el;
  el.classList.toggle("empty", !f);
  el.classList.toggle("sel", lv.selected === cell);
  el.classList.toggle("drop", lv.dropCell === cell);
  el.querySelector(".lv-num").textContent = cell.index;
  const title = el.querySelector(".lv-title");
  title.textContent = f ? f.title : "Cuadro libre";
  title.classList.toggle("muted", !f);
  cell.bar.draggable = !!f;
  cell.bar.title = f ? "Arrastre este cuadro sobre otro para cambiar la cámara de ubicación (si el otro tiene video, se intercambian)" : "";
  el.querySelector(".lv-spin").hidden = !(f && (f.connecting || f.pendingTarget));
  const status = el.querySelector(".lv-status");
  status.textContent = f?.status ?? "";
  status.title = f?.error || f?.status || "";
  const ztag = el.querySelector(".lv-zoomtag");
  ztag.hidden = !(f && f.zoom.s > 1.001);
  if (f) ztag.textContent = `${f.zoom.s.toFixed(1).replace(".", ",")}×`;
  el.querySelector(".lv-actions").hidden = !f;
  const errBox = el.querySelector(".lv-error");
  // El motivo queda a la vista mientras no haya imagen (también entre reintentos).
  errBox.hidden = !(f && f.error && (f.connecting || !f.session));
  if (f) errBox.textContent = f.error;
  if (!f) return;
  const audio = el.querySelector('[data-act="audio"]');
  audio.innerHTML = lvSvg(f.audioOn ? LV_ICO.audio : LV_ICO.mute, 13);
  audio.classList.toggle("on", f.audioOn);
  audio.title = f.session && !f.connecting && !f.session.hasAudio
    ? "Esta cámara no envía audio"
    : "Audio de este cuadro (se silencia el resto)";
  const prof = el.querySelector('[data-act="profile"]');
  const shown = f.pendingTarget ?? f.profile;
  prof.textContent = shown === "Main" ? "P" : "S";
  prof.classList.toggle("on", shown === "Main");
  prof.title = shown === "Main"
    ? "Stream principal (máxima calidad) — clic para cambiar al secundario"
    : "Stream secundario (liviano) — clic para cambiar al principal";
  const rec = el.querySelector('[data-act="rec"]');
  rec.classList.toggle("on", !!f.rec);
  rec.title = f.rec ? "Grabando… (clic para detener y guardar)" : "Grabar una cápsula de video local (clic para iniciar)";
  const time = el.querySelector(".lv-rectime");
  time.hidden = !f.rec;
  if (f.rec) time.textContent = lvElapsed(f.rec.startedAt);
}

function lvElapsed(since) {
  const s = Math.floor((Date.now() - since) / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

// ---------------------------------------------------------------------------
// Utilidades
// ---------------------------------------------------------------------------
function lvStatus(message, isError) {
  const el = $("#lv-status");
  if (!el) return;
  el.textContent = message || "";
  el.classList.toggle("error", !!isError);
}

function lvNorm(s) { return String(s ?? "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase(); }

function lvSafeName(s) { return String(s ?? "").normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/[^A-Za-z0-9._-]+/g, "_").replace(/^_+|_+$/g, "") || "camara"; }

function lvFileName(channel, ext) {
  const d = new Date(), p = (n) => String(n).padStart(2, "0");
  const stamp = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
  return `${lvSafeName(channel.device?.name)}_${lvSafeName(channel.name)}_${stamp}.${ext}`;
}

function lvDownload(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

/** Bitácora (ISO 27001): capturas y grabaciones que quedan en el equipo del operador. */
function lvAuditClient(action, channel, fileName) {
  Api.post("/api/audit/client-event", {
    action, deviceId: channel.deviceId, deviceName: channel.device?.name ?? null,
    channelNumber: channel.channelNumber, channelName: channel.name,
    filePath: fileName, detail: "panel web, carpeta de descargas del navegador",
  }).catch(() => { /* la auditoría nunca bloquea al operador */ });
}

function lvDefaultProfile() {
  if (lv.prefs.profile === "main") return "Main";
  if (lv.prefs.profile === "sub") return "Sub";
  return lv.cells.length <= 4 ? "Main" : "Sub";
}

function lvAllFeeds() { return lv.cells.map((c) => c.feed).filter(Boolean); }

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
const LV_DOC_TITLE = document.title;

async function renderLiveView() {
  const params = new URLSearchParams(location.hash.split("?")[1] || "");
  lv.aux = Math.min(Math.max(Number(params.get("aux")) || 0, 0), 3);
  lv.prefs = lvLoadPrefs(lv.aux);
  lv.active = true;
  lv.cells = [];
  lv.selected = null;
  lv.maximized = -1;
  lv.promoted = null;
  lv.zoomMode = false;
  lv.search = "";
  lv.activeView = "";
  $("#page-title").textContent = lv.aux ? `Pantalla auxiliar ${lv.aux}` : "Vista en vivo";
  if (lv.aux) document.title = `Pantalla auxiliar ${lv.aux} — TrueCentral VMS`;
  document.body.classList.toggle("live-aux", !!lv.aux);
  $("#view").className = "view-live";

  if (typeof RTCPeerConnection === "undefined") {
    $("#view").innerHTML = `<div class="info-box" style="margin:24px">Este navegador no admite WebRTC: use Chrome, Edge o Firefox actualizados para ver video en vivo.</div>`;
    return;
  }

  const canPtz = Perms.can("live.ptz");
  $("#view").innerHTML = `
    <div class="lv-root ${lv.prefs.treeCollapsed ? "tree-collapsed" : ""}" id="lv-root">
      <aside class="lv-side">
        <div class="lv-side-head">
          <span class="lv-cap">CÁMARAS</span>
          <span class="lv-seg">
            <button type="button" data-tree="device" title="Cámaras agrupadas por equipo (DVR, NVR, cámara)">Equipo</button>
            <button type="button" data-tree="location" title="Cámaras agrupadas por ubicación (sitio, edificio, sector…), como en Recursos">Ubicación</button>
          </span>
        </div>
        <input id="lv-search" class="lv-search" type="search" placeholder="Buscar canal…" autocomplete="off">
        <div class="lv-tree" id="lv-tree"><div class="muted lv-small" style="padding:10px 14px">Cargando cámaras…</div></div>
        ${lv.aux ? "" : `
        <section class="lv-panel" id="lv-spk" hidden></section>
        <section class="lv-panel ${lv.prefs.ptzOpen ? "open" : ""}" id="lv-ptz">
          <div class="lv-panel-head">
            <span class="lv-cap lv-ptz-title">PTZ — <b id="lv-ptz-name" class="off">sin cámara PTZ</b></span>
            <span class="lv-pill" id="lv-precision" hidden title="Modo precisión: movimiento PTZ a velocidad mínima (suelte Shift para salir)">PRECISIÓN</span>
            <button type="button" class="lv-chev" id="lv-ptz-toggle" title="Minimizar / restaurar el control PTZ"></button>
          </div>
          <div class="lv-panel-body" id="lv-ptz-body">
            <div class="lv-ptz-pad">
              <button type="button" data-ptz="UpLeft">↖</button><button type="button" data-ptz="TiltUp">↑</button><button type="button" data-ptz="UpRight">↗</button>
              <button type="button" data-ptz="PanLeft">←</button><button type="button" disabled>·</button><button type="button" data-ptz="PanRight">→</button>
              <button type="button" data-ptz="DownLeft">↙</button><button type="button" data-ptz="TiltDown">↓</button><button type="button" data-ptz="DownRight">↘</button>
            </div>
            <div class="lv-ptz-rows">
              <span>Zoom</span><button type="button" data-ptz="ZoomOut">−</button><button type="button" data-ptz="ZoomIn">+</button>
              <span>Foco</span><button type="button" data-ptz="FocusNear">Cerca</button><button type="button" data-ptz="FocusFar">Lejos</button>
              <span>Iris</span><button type="button" data-ptz="IrisOpen">Abrir</button><button type="button" data-ptz="IrisClose">Cerrar</button>
            </div>
            <div class="lv-ptz-preset">
              <span>Preset</span>
              <input type="number" id="lv-preset" min="1" max="300" value="1">
              <button type="button" data-preset="Clear" title="Eliminar este preset en la cámara">Borrar</button>
              <button type="button" data-preset="Set" title="Guardar la posición actual como este preset">Guardar</button>
              <button type="button" data-preset="Goto" title="Mover la cámara a este preset">Ir</button>
            </div>
            <div class="lv-ptz-speed">
              <span>Velocidad</span>
              <input type="range" id="lv-speed" min="1" max="7" step="1" value="${lv.prefs.ptzSpeed}">
              <b id="lv-speed-val">${lv.prefs.ptzSpeed}</b>
            </div>
            ${canPtz ? "" : `<div class="muted lv-small" style="margin-top:6px">Sus roles no incluyen mover cámaras PTZ.</div>`}
          </div>
        </section>`}
      </aside>
      <button type="button" class="lv-handle" id="lv-handle" title="Mostrar u ocultar la lista de dispositivos"></button>
      <div class="lv-main">
        <div class="lv-toolbar">
          <div class="lv-tb-left">
            <span class="muted">División:</span>
            <span class="lv-pop-anchor">
              <button type="button" class="btn ghost lv-tb" id="lv-layout-btn"></button>
              <div class="lv-pop lv-layout-pop" id="lv-layout-pop" hidden></div>
            </span>
            <button type="button" class="btn ghost lv-tb lv-toggle" id="lv-zoom-btn" title="Zoom digital: arrastre sobre el video para marcar el área a acercar (clic derecho vuelve a 1×). La rueda del mouse acerca en cualquier momento.">${lvSvg(LV_ICO.zoom, 14)}<span>Zoom</span></button>
            <button type="button" class="btn ghost lv-tb" id="lv-clear-all">Limpiar todo</button>
            <span class="lv-bulk" id="lv-bulk" hidden><i></i></span>
          </div>
          <div class="lv-tb-center">
            <span class="lv-pop-anchor">
              <button type="button" class="btn ghost lv-tb" id="lv-views-btn" title="Vistas guardadas: deje la grilla como la armó y vuelva a ella con un clic">${lvSvg(LV_ICO.views, 14)}<span>Vistas</span><b class="lv-active-view" id="lv-active-view"></b><span class="lv-caret-down">▾</span></button>
              <div class="lv-pop lv-views-pop" id="lv-views-pop" hidden></div>
            </span>
          </div>
          <div class="lv-tb-right">
            ${lv.aux ? "" : `<button type="button" class="btn ghost lv-tb" id="lv-aux-btn" title="Abrir una pantalla auxiliar (hasta 3): otra ventana de vista en vivo para llevar a otro monitor">${lvSvg(LV_ICO.aux, 14)}<span>Pantalla auxiliar</span></button>`}
            <button type="button" class="btn ghost lv-tb" id="lv-full-btn" title="Pantalla completa de la grilla — presione Esc para salir">${lvSvg(LV_ICO.full, 14)}<span>Pantalla completa</span></button>
            <span class="lv-pop-anchor">
              <button type="button" class="btn ghost lv-tb lv-icon-only" id="lv-settings-btn" title="Ajustes de la vista en vivo de este navegador">${lvSvg(LV_ICO.gear, 15)}</button>
              <div class="lv-pop lv-settings-pop" id="lv-settings-pop" hidden></div>
            </span>
          </div>
        </div>
        <div class="lv-gridwrap ${lv.prefs.stretch ? "stretch" : ""}" id="lv-gridwrap"><div class="lv-grid" id="lv-grid"></div></div>
        <div class="lv-statusbar" id="lv-status"></div>
      </div>
    </div>`;

  lvWirePage();
  lvApplyLayout(lvFindLayout(lv.prefs.layout) ?? LV_LAYOUTS[1]);
  lvRenderLayoutButton();
  lvUpdatePtz();
  lvStatus("Clic en la barra de un cuadro para seleccionarlo (borde azul); doble clic en un canal del árbol lo abre ahí.");

  await lvLoadTree();
  if (!lv.active) return;
  if (!lv.aux) lvLoadSpeakers();
  if (!lv.aux && lv.prefs.reopen) await lvReopenLast();
}

/** Suelta todo al salir de la página (video, grabaciones, teclado, hub). */
function liveViewLeave() {
  if (!lv.active) return;
  lv.active = false;
  lvSaveLastNow();
  lv.cells.forEach((c) => c.feed?.dispose());
  lv.cells = [];
  lv.timers.forEach((t) => clearInterval(t));
  lv.timers = [];
  clearTimeout(lv.treeReload);
  clearTimeout(lv.lastSaveTimer);
  lvStopTalk();
  lvStopPreview();
  if (lv.ptzHeld) lvPtzRelease();
  if (lv.keyHeld) { lvPtz(lv.keyHeld, true); lv.keyHeld = null; }
  document.removeEventListener("keydown", lvOnKeyDown, true);
  document.removeEventListener("keyup", lvOnKeyUp, true);
  document.removeEventListener("pointerdown", lvOnOutsidePointer, true);
  window.removeEventListener("blur", lvOnWindowBlur);
  VmsHub.off("DeviceStatusChanged", lvOnDeviceStatus);
  VmsHub.off("ConfigChanged", lvOnConfigChanged);
  lv.bc?.close();
  lv.bc = null;
  $("#lv-ctx")?.remove();
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  if (lv.aux) {
    document.body.classList.remove("live-aux");
    document.title = LV_DOC_TITLE;
  }
}

// La pestaña o la ventana se cierra: los streams se liberan en el servidor al
// instante (keepalive) y queda anotada la grilla para "volver a abrir".
window.addEventListener("pagehide", () => {
  if (!lv.active) return;
  lvSaveLastNow();
  lv.cells.forEach((c) => c.feed?.dispose());
});

function lvWirePage() {
  // Árbol: modo, buscador, colapsar.
  $$(".lv-seg [data-tree]").forEach((b) => {
    b.classList.toggle("on", (b.dataset.tree === "location") === lv.prefs.byLocation);
    b.addEventListener("click", () => {
      lv.prefs.byLocation = b.dataset.tree === "location";
      lvSavePrefs();
      $$(".lv-seg [data-tree]").forEach((x) => x.classList.toggle("on", x === b));
      lvRenderTree();
    });
  });
  $("#lv-search").addEventListener("input", (e) => { lv.search = e.target.value; lvRenderTree(); });
  $("#lv-handle").addEventListener("click", () => {
    lv.prefs.treeCollapsed = !lv.prefs.treeCollapsed;
    lvSavePrefs();
    $("#lv-root").classList.toggle("tree-collapsed", lv.prefs.treeCollapsed);
  });
  lvWireTree();
  lvWireGrid();

  // Barra de herramientas.
  $("#lv-layout-btn").addEventListener("click", () => lvTogglePop("lv-layout-pop", lvRenderLayoutPop));
  $("#lv-zoom-btn").addEventListener("click", () => {
    lv.zoomMode = !lv.zoomMode;
    $("#lv-zoom-btn").classList.toggle("on", lv.zoomMode);
    $("#lv-grid").classList.toggle("zoom-mode", lv.zoomMode);
    lvStatus(lv.zoomMode
      ? "Zoom digital: arrastre sobre el video para marcar el área a acercar; clic derecho vuelve a 1×."
      : "Zoom digital desactivado (la rueda del mouse sigue acercando; clic derecho vuelve a 1×).");
  });
  $("#lv-clear-all").addEventListener("click", () => {
    lvForgetPromotion();
    lv.maximized = -1;
    lv.cells.forEach((c) => c.clear());
    lv.activeView = "";
    lvRenderViewsButton();
    lvApplyGridGeometry();
    lvRefreshTreeLive();
    lvUpdatePtz();
    lvScheduleLastSave();
  });
  $("#lv-views-btn").addEventListener("click", () => lvTogglePop("lv-views-pop", lvRenderViewsPop));
  $("#lv-aux-btn")?.addEventListener("click", lvOpenAux);
  $("#lv-full-btn").addEventListener("click", () => {
    $("#lv-gridwrap").requestFullscreen?.().catch((err) => lvStatus(`No se pudo pasar a pantalla completa: ${err.message}`, true));
  });
  $("#lv-settings-btn").addEventListener("click", () => lvTogglePop("lv-settings-pop", lvRenderSettingsPop));

  // PTZ.
  if (!lv.aux) lvWirePtz();

  // Teclado (PTZ) y cierre de los desplegables con clic afuera.
  document.addEventListener("keydown", lvOnKeyDown, true);
  document.addEventListener("keyup", lvOnKeyUp, true);
  document.addEventListener("pointerdown", lvOnOutsidePointer, true);
  window.addEventListener("blur", lvOnWindowBlur);

  // Tiempo real: estado de equipos y cambios de configuración.
  VmsHub.on("DeviceStatusChanged", lvOnDeviceStatus);
  VmsHub.on("ConfigChanged", lvOnConfigChanged);

  // Mover cámaras entre ventanas (principal ↔ auxiliares).
  if ("BroadcastChannel" in window) {
    lv.bc = new BroadcastChannel("tcvms-live");
    lv.bc.onmessage = (e) => {
      const m = e.data;
      if (m?.type === "moved" && m.window === lv.windowId) {
        const cell = lv.cells[m.index];
        if (cell?.feed && cell.feed.channel.id === m.channelId) {
          cell.clear();
          lvRefreshTreeLive();
          lvUpdatePtz();
          lvStatus(`La cámara del cuadro ${m.index + 1} se movió a otra ventana.`);
        }
      }
    };
  }

  // Reloj de grabaciones y vigía de señal.
  lv.timers.push(setInterval(() => {
    for (const c of lv.cells) if (c.feed?.rec) { const t = c.el.querySelector(".lv-rectime"); if (t) t.textContent = lvElapsed(c.feed.rec.startedAt); }
  }, 1000));
  lv.timers.push(setInterval(() => { for (const f of lvAllFeeds()) f.watch(); }, 4000));
}

function lvOnWindowBlur() {
  // Una tecla o un botón sostenidos al cambiar de ventana no deben dejar la cámara moviéndose.
  if (lv.keyHeld) { lvPtz(lv.keyHeld, true); lv.keyHeld = null; }
  if (lv.ptzHeld) lvPtzRelease();
  lvSetPrecision(false);
}

// ---------------------------------------------------------------------------
// Desplegables de la barra
// ---------------------------------------------------------------------------
function lvTogglePop(id, render) {
  const pop = $("#" + id);
  const open = pop.hidden;
  $$(".lv-pop").forEach((p) => { p.hidden = true; });
  if (open) { render(); pop.hidden = false; }
}

function lvClosePops() { $$(".lv-pop").forEach((p) => { p.hidden = true; }); }

function lvOnOutsidePointer(e) {
  if (!e.target.closest?.(".lv-pop-anchor")) lvClosePops();
  if (!e.target.closest?.("#lv-ctx")) $("#lv-ctx")?.remove();
}

function lvRenderLayoutButton() {
  const l = lv.layout;
  const btn = $("#lv-layout-btn");
  if (!btn || !l) return;
  btn.innerHTML = `${lvLayoutPreview(l, 22, 15)}<span>${esc(l.name)}</span><span class="lv-caret-down">▾</span>`;
  btn.title = l.key;
}

function lvRenderLayoutPop() {
  $("#lv-layout-pop").innerHTML = `
    <div class="lv-pop-title">División de pantalla</div>
    ${LV_LAYOUT_GROUPS.map((g) => `
      <div class="lv-lgroup">
        <div class="lv-lgroup-title">${esc(g.title)}</div>
        <div class="lv-lgrid">${g.layouts.map((l) => `
          <button type="button" class="lv-lbtn ${lv.layout?.key === l.key ? "on" : ""}" data-layout="${esc(l.key)}" title="${esc(l.key)}">
            ${lvLayoutPreview(l, 36, 26)}<span>${esc(l.name)}</span>
          </button>`).join("")}</div>
      </div>`).join("")}`;
  $$("#lv-layout-pop [data-layout]").forEach((b) => b.addEventListener("click", () => {
    const layout = lvFindLayout(b.dataset.layout);
    lvClosePops();
    if (!layout) return;
    lvApplyLayout(layout);
    lv.prefs.layout = layout.key;
    lvSavePrefs();
    lvScheduleLastSave();
  }));
}

function lvRenderSettingsPop() {
  const p = lv.prefs;
  $("#lv-settings-pop").innerHTML = `
    <div class="lv-pop-title">Ajustes de este navegador</div>
    <label class="lv-set-row"><span>Stream al abrir una cámara</span>
      <select id="lv-set-profile">
        <option value="auto" ${p.profile === "auto" ? "selected" : ""}>Automático (principal hasta 4 cuadros)</option>
        <option value="main" ${p.profile === "main" ? "selected" : ""}>Siempre principal</option>
        <option value="sub" ${p.profile === "sub" ? "selected" : ""}>Siempre secundario</option>
      </select></label>
    <label class="lv-set-row"><span>Formato de las capturas</span>
      <select id="lv-set-snap">
        <option value="jpg" ${p.snapFormat !== "png" ? "selected" : ""}>JPG</option>
        <option value="png" ${p.snapFormat === "png" ? "selected" : ""}>PNG</option>
      </select></label>
    <label class="lv-set-check"><input type="checkbox" id="lv-set-fit" ${p.fitDevice ? "checked" : ""}>
      <span>Al abrir un equipo completo, grilla a la medida de sus canales</span></label>
    <label class="lv-set-check"><input type="checkbox" id="lv-set-stretch" ${p.stretch ? "checked" : ""}>
      <span>Estirar el video al tamaño del cuadro (sin mantener la proporción)</span></label>
    ${lv.aux ? "" : `<label class="lv-set-check"><input type="checkbox" id="lv-set-reopen" ${p.reopen ? "checked" : ""}>
      <span>Volver a abrir las cámaras de la última sesión al entrar</span></label>`}
    <div class="muted lv-small" style="margin-top:8px">Las capturas y grabaciones se guardan en la carpeta de descargas del navegador.</div>`;
  $("#lv-set-profile").addEventListener("change", (e) => { p.profile = e.target.value; lvSavePrefs(); });
  $("#lv-set-snap").addEventListener("change", (e) => { p.snapFormat = e.target.value; lvSavePrefs(); });
  $("#lv-set-fit").addEventListener("change", (e) => { p.fitDevice = e.target.checked; lvSavePrefs(); });
  $("#lv-set-stretch").addEventListener("change", (e) => {
    p.stretch = e.target.checked; lvSavePrefs();
    $("#lv-gridwrap").classList.toggle("stretch", p.stretch);
  });
  $("#lv-set-reopen")?.addEventListener("change", (e) => { p.reopen = e.target.checked; lvSavePrefs(); });
}

// ---------------------------------------------------------------------------
// Grilla
// ---------------------------------------------------------------------------
/** Cambia la división: los cuadros que siguen conservan su video; los que sobran se liberan. */
function lvApplyLayout(layout) {
  lvForgetPromotion();
  lv.maximized = -1;
  lv.layout = layout;
  const grid = $("#lv-grid");
  const count = layout.cells.length;
  while (lv.cells.length > count) {
    const cell = lv.cells.pop();
    cell.clear();
    cell.el.remove();
  }
  while (lv.cells.length < count) {
    const cell = new LvCell();
    lv.cells.push(cell);
    grid.append(cell.el);
  }
  lv.cells.forEach((c, i) => { c.index = i + 1; lvRenderCell(c); });
  if (lv.selected && !lv.cells.includes(lv.selected)) lv.selected = null;
  lvApplyGridGeometry();
  lvRenderLayoutButton();
  lvRefreshTreeLive();
  lvUpdatePtz();
}

function lvApplyGridGeometry() {
  const grid = $("#lv-grid");
  const l = lv.layout;
  if (!grid || !l) return;
  const maxed = lv.maximized >= 0;
  grid.classList.toggle("maxed", maxed);
  grid.style.gridTemplateColumns = maxed ? "1fr" : `repeat(${l.columns}, minmax(0, 1fr))`;
  grid.style.gridTemplateRows = maxed ? "1fr" : `repeat(${l.rows}, minmax(0, 1fr))`;
  lv.cells.forEach((c, i) => {
    const r = l.cells[i];
    if (maxed) {
      c.el.style.display = i === lv.maximized ? "" : "none";
      c.el.style.gridColumn = c.el.style.gridRow = i === lv.maximized ? "1 / -1" : "";
    } else {
      c.el.style.display = "";
      c.el.style.gridColumn = `${r.col + 1} / span ${r.colSpan}`;
      c.el.style.gridRow = `${r.row + 1} / span ${r.rowSpan}`;
    }
  });
}

function lvSelectCell(cell) {
  if (lv.selected === cell) return;
  const prev = lv.selected;
  lv.selected = cell;
  if (prev) lvRenderCell(prev);
  if (cell) lvRenderCell(cell);
  // Selección grilla → árbol: el canal del cuadro queda marcado.
  lv.selChannelId = cell?.feed?.channel.id ?? lv.selChannelId;
  lvMarkTreeSelection(true);
  lvUpdatePtz();
}

function lvCellFromEvent(e) { return e.target.closest?.(".lv-cell")?._lvCell ?? null; }

function lvWireGrid() {
  const grid = $("#lv-grid");

  grid.addEventListener("pointerdown", (e) => {
    const cell = lvCellFromEvent(e);
    if (!cell) return;
    lvSelectCell(cell);
    if (e.button !== 0 || !e.target.closest(".lv-body") || !cell.feed) return;
    const f = cell.feed;
    const rect = cell.body.getBoundingClientRect();
    const fx = (e.clientX - rect.left) / rect.width, fy = (e.clientY - rect.top) / rect.height;
    if (lv.zoomMode) {
      // Marcar el rectángulo a acercar.
      const box = cell.body.querySelector(".lv-zoomrect");
      const move = (ev) => {
        const x = Math.min(Math.max((ev.clientX - rect.left) / rect.width, 0), 1);
        const y = Math.min(Math.max((ev.clientY - rect.top) / rect.height, 0), 1);
        Object.assign(box.style, {
          left: `${Math.min(fx, x) * 100}%`, top: `${Math.min(fy, y) * 100}%`,
          width: `${Math.abs(x - fx) * 100}%`, height: `${Math.abs(y - fy) * 100}%`,
        });
        box.hidden = false;
        box._end = [x, y];
      };
      const up = () => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        box.hidden = true;
        if (box._end) f.zoomToRect(fx, fy, box._end[0], box._end[1]);
        box._end = null;
      };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
      e.preventDefault();
    } else if (f.zoom.s > 1.001) {
      // Con zoom, arrastrar desplaza la imagen.
      let lastX = e.clientX, lastY = e.clientY;
      const move = (ev) => {
        f.zoom.x += (ev.clientX - lastX) / rect.width;
        f.zoom.y += (ev.clientY - lastY) / rect.height;
        lastX = ev.clientX; lastY = ev.clientY;
        f.clampZoom();
        f.applyZoom();
      };
      const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
      e.preventDefault();
    }
  });

  grid.addEventListener("click", (e) => {
    const btn = e.target.closest(".lv-act");
    const cell = lvCellFromEvent(e);
    if (!btn || !cell?.feed) return;
    const f = cell.feed;
    switch (btn.dataset.act) {
      case "audio": lvToggleAudio(f); break;
      case "profile": f.switchProfile((f.pendingTarget ?? f.profile) === "Main" ? "Sub" : "Main"); break;
      case "snap": f.snapshot(); break;
      case "rec": f.toggleRecording(); break;
      case "close":
        if (lv.maximized === lv.cells.indexOf(cell)) { lv.maximized = -1; lvApplyGridGeometry(); }
        cell.clear();
        lvRefreshTreeLive();
        lvUpdatePtz();
        lvScheduleLastSave();
        break;
    }
  });

  grid.addEventListener("dblclick", (e) => {
    if (e.target.closest(".lv-bar")) return;
    const cell = lvCellFromEvent(e);
    if (cell) lvToggleMaximize(cell);
  });

  grid.addEventListener("wheel", (e) => {
    const cell = lvCellFromEvent(e);
    if (!cell?.feed || !e.target.closest(".lv-body")) return;
    e.preventDefault();
    const rect = cell.body.getBoundingClientRect();
    cell.feed.zoomAt((e.clientX - rect.left) / rect.width, (e.clientY - rect.top) / rect.height, e.deltaY < 0 ? 1.2 : 1 / 1.2);
  }, { passive: false });

  grid.addEventListener("contextmenu", (e) => {
    const cell = lvCellFromEvent(e);
    if (!cell?.feed || !e.target.closest(".lv-body")) return;
    e.preventDefault();
    cell.feed.resetZoom();
  });

  // Arrastre de un cuadro sobre otro.
  grid.addEventListener("dragstart", (e) => {
    const bar = e.target.closest?.(".lv-bar");
    const cell = lvCellFromEvent(e);
    if (!bar || !cell?.feed) { if (e.target.closest?.(".lv-cell")) e.preventDefault(); return; }
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/x-tcvms-cell", JSON.stringify({
      window: lv.windowId, index: lv.cells.indexOf(cell), channelId: cell.feed.channel.id, profile: cell.feed.profile,
    }));
  });
  grid.addEventListener("dragover", (e) => {
    const types = e.dataTransfer?.types ?? [];
    if (!types.includes("text/x-tcvms-cell") && !types.includes("text/x-tcvms-channel")) return;
    const cell = lvCellFromEvent(e);
    if (!cell) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = types.includes("text/x-tcvms-cell") ? "move" : "copy";
    if (lv.dropCell !== cell) {
      const prev = lv.dropCell;
      lv.dropCell = cell;
      if (prev) lvRenderCell(prev);
      lvRenderCell(cell);
    }
  });
  grid.addEventListener("dragleave", (e) => {
    const cell = lvCellFromEvent(e);
    if (cell && cell === lv.dropCell && !cell.el.contains(e.relatedTarget)) {
      lv.dropCell = null;
      lvRenderCell(cell);
    }
  });
  grid.addEventListener("drop", (e) => {
    const target = lvCellFromEvent(e);
    const prev = lv.dropCell;
    lv.dropCell = null;
    if (prev) lvRenderCell(prev);
    if (!target) return;
    e.preventDefault();
    const channelId = Number(e.dataTransfer.getData("text/x-tcvms-channel"));
    if (channelId) {
      const ch = lv.channels.get(channelId);
      if (ch) lvOpenInCell(ch, target, lvDefaultProfile());
      return;
    }
    let moved = null;
    try { moved = JSON.parse(e.dataTransfer.getData("text/x-tcvms-cell") || "null"); } catch { /* otro contenido */ }
    if (!moved) return;
    if (moved.window === lv.windowId) {
      const source = lv.cells[moved.index];
      if (source) lvSwapCells(source, target);
    } else {
      // Desde otra ventana: se abre aquí y la otra suelta su cuadro.
      const ch = lv.channels.get(moved.channelId);
      if (!ch) { lvStatus("Esa cámara no está en el árbol de esta ventana.", true); return; }
      lvOpenInCell(ch, target, moved.profile || lvDefaultProfile());
      lv.bc?.postMessage({ type: "moved", window: moved.window, index: moved.index, channelId: moved.channelId });
    }
  });
}

/** Arrastrar un cuadro sobre otro: si el destino tiene video se intercambian; si no, la cámara se muda. */
function lvSwapCells(source, target) {
  if (source === target || (!source.feed && !target.feed)) return;
  const exchange = !!target.feed;
  const moved = source.feed?.title ?? "", displaced = target.feed?.title ?? "";
  const a = source.detach(), b = target.detach();
  target.attach(a);
  source.attach(b);
  lvSelectCell(target);
  lvRefreshTreeLive();
  lvScheduleLastSave();
  lvStatus(exchange
    ? `Cuadros ${source.index} y ${target.index} intercambiados: "${moved}" ↔ "${displaced}".`
    : `"${moved}" movida del cuadro ${source.index} al ${target.index}.`);
}

/** Doble clic: ver ese cuadro solo (sube a principal) o volver a la grilla (vuelve a secundario). */
function lvToggleMaximize(cell) {
  const index = lv.cells.indexOf(cell);
  if (index < 0) return;
  if (lv.maximized === index) {
    lv.maximized = -1;
    const f = cell.feed;
    if (f && lv.promoted === f && f.channel === lv.promotedChannel) {
      if (f.profile === "Main") { if (!f.restoreParked()) f.switchProfile("Sub", { hardFallback: true }); }
      else f.cancelSwitch();
    }
    lv.promoted = null;
    lv.promotedChannel = null;
    lvApplyGridGeometry();
    return;
  }
  if (!cell.feed) return;
  lvForgetPromotion();
  lv.maximized = index;
  lvSelectCell(cell);
  lvApplyGridGeometry();
  const f = cell.feed;
  if (f.profile === "Sub") {
    lv.promoted = f;
    lv.promotedChannel = f.channel;
    f.switchProfile("Main", { keepForRestore: true });
  }
}

/** Cambiar de división deshace el maximizado sin volver a secundario: el estacionado se libera. */
function lvForgetPromotion() {
  lv.promoted?.releaseParked();
  lv.promoted = null;
  lv.promotedChannel = null;
}

/** Audio exclusivo: encender uno silencia los demás. */
function lvToggleAudio(feed) {
  if (feed.audioOn) {
    feed.audioOn = false;
  } else {
    if (feed.session && !feed.connecting && !feed.session.hasAudio) { feed.flash("Esta cámara no envía audio."); return; }
    for (const f of lvAllFeeds()) if (f !== feed && f.audioOn) { f.audioOn = false; f.applyFront(); f.render(); }
    feed.audioOn = true;
  }
  feed.applyFront();
  feed.render();
}

/** Abre un canal: en el cuadro seleccionado, el primero libre o el primero; la selección avanza. */
function lvOpenChannel(ch) {
  const cell = lv.selected ?? lv.cells.find((c) => c.isEmpty) ?? lv.cells[0];
  if (!cell) return;
  const next = lv.cells.indexOf(cell) + 1;
  lvOpenInCell(ch, cell, lvDefaultProfile());
  lvSelectCell(next < lv.cells.length ? lv.cells[next] : null);
}

function lvOpenInCell(ch, cell, profile) {
  if (lv.maximized >= 0 && lv.cells[lv.maximized] !== cell) { lv.maximized = -1; lvApplyGridGeometry(); }
  if (lv.promoted === cell.feed) lvForgetPromotion();
  const feed = new LvFeed(ch, profile);
  cell.attach(feed);
  const opened = feed.open();
  lvRefreshTreeLive();
  if (lv.selected === cell) lvUpdatePtz();
  lvScheduleLastSave();
  return opened;
}

/** Apertura masiva (equipo, ubicación, vista guardada): de a 4 negociaciones a la vez. */
async function lvOpenMany(label, items) {
  const bulk = $("#lv-bulk");
  const total = items.length;
  let done = 0;
  lv.bulk = label;
  // El avance va en la barra de estado; en la de herramientas, solo la barra animada.
  const show = () => {
    const text = `Abriendo canales de "${label}"… ${done}/${total}`;
    if (bulk) { bulk.hidden = false; bulk.title = text; }
    lvStatus(text);
  };
  show();
  let next = 0;
  const worker = async () => {
    while (next < items.length && lv.active) {
      const { ch, cell, profile } = items[next++];
      try { await lvOpenInCell(ch, cell, profile); } catch { /* el cuadro muestra su error */ }
      done++;
      show();
    }
  };
  await Promise.all([worker(), worker(), worker(), worker()]);
  lv.bulk = null;
  if (bulk) bulk.hidden = true;
  lvStatus(`${total} canal(es) de "${label}" en pantalla.`);
}

/** Doble clic en un equipo o "Abrir sus cámaras" de una ubicación. */
async function lvOpenAll(label, channels) {
  if (!channels.length) { lvStatus(`"${label}" no tiene canales habilitados.`); return; }
  if (channels.length > 64) {
    lvStatus(`"${label}" tiene ${channels.length} canales: se abren los primeros 64.`);
    channels = channels.slice(0, 64);
  }
  if (lv.prefs.fitDevice) {
    lvApplyLayout(lvFitFor(channels.length)); // a medida: no queda como división recordada
  } else {
    const layout = lvSmallestFor(channels.length);
    if (lv.layout !== layout) { lvApplyLayout(layout); lv.prefs.layout = layout.key; lvSavePrefs(); }
  }
  lv.cells.forEach((c) => c.clear());
  lvSelectCell(null);
  const profile = lvDefaultProfile();
  const n = Math.min(channels.length, lv.cells.length);
  await lvOpenMany(label, channels.slice(0, n).map((ch, i) => ({ ch, cell: lv.cells[i], profile })));
}

// ---------------------------------------------------------------------------
// Árbol de cámaras
// ---------------------------------------------------------------------------
async function lvLoadTree() {
  let devices;
  try { devices = await Api.get("/api/devices"); }
  catch (err) { $("#lv-tree").innerHTML = `<div class="error-box" style="margin:10px">${esc(err.error)}</div>`; return; }
  const lists = await Promise.all(devices.map((d) => Api.get(`/api/devices/${d.id}/channels`).catch(() => [])));
  let locations = [];
  try { locations = await Api.get("/api/locations"); } catch { /* sin ubicaciones: todo "Sin ubicación" */ }
  if (!lv.active) return;

  lv.channels = new Map();
  lv.devices = devices.map((d, i) => {
    const dev = { id: d.id, name: d.name, status: d.status, channels: [] };
    dev.channels = lists[i].filter((c) => c.enabled).map((c) => {
      const ch = { ...c, device: dev };
      lv.channels.set(ch.id, ch);
      return ch;
    });
    return dev;
  });
  lvBuildLocationTree(locations);

  // Los cuadros abiertos toman los datos nuevos del canal (PTZ marcado, nombre…).
  for (const f of lvAllFeeds()) {
    const fresh = lv.channels.get(f.channel.id);
    if (fresh) { f.channel = fresh; f.render(); }
  }
  lvRenderTree();
  lvUpdatePtz();
}

const lvCollator = new Intl.Collator("es", { numeric: true, sensitivity: "base" });

function lvBuildLocationTree(locations) {
  const nodes = new Map(locations.map((l) => [l.id, { loc: l, children: [], channels: [] }]));
  const roots = [];
  for (const l of [...locations].sort((a, b) => lvCollator.compare(a.name, b.name))) {
    const node = nodes.get(l.id);
    const parent = l.parentId != null ? nodes.get(l.parentId) : null;
    (parent ? parent.children : roots).push(node);
  }
  const unassigned = { loc: null, children: [], channels: [] };
  const all = [...lv.channels.values()].sort((a, b) => lvCollator.compare(a.name, b.name));
  for (const ch of all) (nodes.get(ch.locationId)?.channels ?? unassigned.channels).push(ch);
  if (unassigned.channels.length) roots.push(unassigned);
  // Solo las ubicaciones con alguna cámara en su subárbol (este árbol es para ver video).
  const count = (n) => (n.total = n.channels.length + n.children.reduce((s, c) => s + count(c), 0));
  roots.forEach(count);
  const prune = (list) => list.filter((n) => n.total > 0).map((n) => { n.children = prune(n.children); return n; });
  lv.locRoots = prune(roots);
}

function lvChannelOnline(ch) { return ch.isOnline && ch.device?.status === "Online"; }

function lvIsCollapsed(key) { return lv.prefs.collapsedNodes.includes(key); }

function lvRenderTree() {
  const host = $("#lv-tree");
  if (!host) return;
  const q = lvNorm(lv.search.trim());
  const live = lvLiveMap();
  const chHtml = (ch, depth) => {
    const where = live.get(ch.id);
    const tip = where ? `En pantalla: cuadro ${where.join(", ")}` : `${ch.device?.name ?? ""} · canal ${ch.channelNumber}`;
    return `<div class="lv-node lv-ch ${where ? "live" : ""} ${lv.selChannelId === ch.id ? "sel" : ""}" draggable="true"
      data-ch="${ch.id}" style="--d:${depth}" title="${esc(tip)} — doble clic para abrirla">
      <span class="lv-ico ${lvChannelOnline(ch) ? "ok" : ""}">${where ? "▶" : "◉"}</span><span class="lv-label">${esc(ch.name)}</span></div>`;
  };
  let html = "";
  if (!lv.prefs.byLocation) {
    for (const d of lv.devices) {
      const devMatch = !q || lvNorm(d.name).includes(q);
      const chans = devMatch ? d.channels : d.channels.filter((c) => lvNorm(c.name).includes(q));
      if (q && !devMatch && !chans.length) continue;
      const key = `d${d.id}`;
      const collapsed = !q && lvIsCollapsed(key);
      html += `<div class="lv-node lv-dev" data-dev="${d.id}" data-key="${key}" style="--d:0" title="Doble clic para abrir todos sus canales">
        <span class="lv-caret ${collapsed ? "" : "open"}" data-caret></span>
        <span class="dot ${d.status === "Online" ? "ok" : "bad"}"></span><b class="lv-label">${esc(d.name)}</b></div>`;
      if (!collapsed) html += chans.map((c) => chHtml(c, 1)).join("");
    }
    if (!lv.devices.length) html = `<div class="muted lv-small" style="padding:10px 14px">No hay cámaras disponibles.</div>`;
  } else {
    const walk = (node, depth, ancestorMatch) => {
      const name = node.loc ? node.loc.name : "Sin ubicación";
      const selfMatch = ancestorMatch || !q || lvNorm(name).includes(q);
      const chans = selfMatch ? node.channels : node.channels.filter((c) => lvNorm(c.name).includes(q) || lvNorm(c.device?.name).includes(q));
      const kids = node.children.map((c) => walk(c, depth + 1, selfMatch)).filter(Boolean).join("");
      if (q && !selfMatch && !chans.length && !kids) return "";
      const key = node.loc ? `l${node.loc.id}` : "l0";
      const collapsed = !q && lvIsCollapsed(key);
      const kind = node.loc ? (typeof LOC_KINDS !== "undefined" ? LOC_KINDS[node.loc.kind] : null) : null;
      const icon = typeof resIcon === "function" ? resIcon(node.loc ? (kind?.icon ?? "pin") : "inbox", 13) : "";
      return `<div class="lv-node lv-loc" data-loc="${node.loc?.id ?? 0}" data-key="${key}" style="--d:${depth}"
          title="${esc(kind?.label ?? "Cámaras sin ubicación")} — doble clic o clic derecho para abrir sus cámaras">
          <span class="lv-caret ${collapsed ? "" : "open"}" data-caret></span>
          <span class="lv-locico">${icon}</span><b class="lv-label">${esc(name)}</b><span class="lv-count">${node.total}</span></div>` +
        (collapsed ? "" : kids + chans.map((c) => chHtml(c, depth + 1)).join(""));
    };
    html = lv.locRoots.map((n) => walk(n, 0, false)).join("") ||
      `<div class="muted lv-small" style="padding:10px 14px">No hay cámaras disponibles.</div>`;
  }
  if (q && !html.includes("lv-ch")) html = `<div class="muted lv-small" style="padding:10px 14px">Ningún canal coincide con la búsqueda.</div>`;
  host.innerHTML = html;
}

/** Canal → números de cuadro donde está en pantalla. */
function lvLiveMap() {
  const map = new Map();
  for (const c of lv.cells) if (c.feed) {
    const list = map.get(c.feed.channel.id) ?? [];
    list.push(c.index);
    map.set(c.feed.channel.id, list);
  }
  return map;
}

function lvRefreshTreeLive() { lvRenderTree(); }

function lvMarkTreeSelection(scroll) {
  $$("#lv-tree .lv-ch.sel").forEach((n) => n.classList.remove("sel"));
  if (lv.selChannelId == null) return;
  const node = $(`#lv-tree .lv-ch[data-ch="${lv.selChannelId}"]`);
  if (!node) return;
  node.classList.add("sel");
  if (scroll) node.scrollIntoView({ block: "nearest" });
}

function lvLocationChannels(id) {
  const out = [];
  const find = (list) => {
    for (const n of list) {
      if ((n.loc?.id ?? 0) === id) { collect(n); return true; }
      if (find(n.children)) return true;
    }
    return false;
  };
  const collect = (n) => { out.push(...n.channels); n.children.forEach(collect); };
  find(lv.locRoots);
  return out;
}

function lvWireTree() {
  const tree = $("#lv-tree");
  tree.addEventListener("click", (e) => {
    const node = e.target.closest(".lv-node");
    if (!node) return;
    if (e.target.closest("[data-caret]") && node.dataset.key) {
      const key = node.dataset.key, list = lv.prefs.collapsedNodes;
      const i = list.indexOf(key);
      if (i >= 0) list.splice(i, 1); else list.push(key);
      lvSavePrefs();
      lvRenderTree();
      return;
    }
    if (node.dataset.ch) { lv.selChannelId = Number(node.dataset.ch); lvMarkTreeSelection(false); }
  });
  tree.addEventListener("dblclick", (e) => {
    const node = e.target.closest(".lv-node");
    if (!node || e.target.closest("[data-caret]")) return;
    if (node.dataset.ch) {
      const ch = lv.channels.get(Number(node.dataset.ch));
      if (ch) lvOpenChannel(ch);
    } else if (node.dataset.dev) {
      const d = lv.devices.find((x) => x.id === Number(node.dataset.dev));
      if (d) lvOpenAll(d.name, d.channels);
    } else if (node.dataset.loc != null) {
      const id = Number(node.dataset.loc);
      lvOpenAll(node.querySelector(".lv-label").textContent, lvLocationChannels(id));
    }
  });
  tree.addEventListener("dragstart", (e) => {
    const node = e.target.closest?.(".lv-ch");
    if (!node) { e.preventDefault(); return; }
    e.dataTransfer.effectAllowed = "copyMove";
    e.dataTransfer.setData("text/x-tcvms-channel", node.dataset.ch);
  });
  tree.addEventListener("contextmenu", (e) => {
    const node = e.target.closest(".lv-loc");
    if (!node) return;
    e.preventDefault();
    lvLocationMenu(node, e.clientX, e.clientY);
  });
}

/** Menú de una ubicación: abrir sus cámaras y órdenes a sus áreas de alarma. */
function lvLocationMenu(node, x, y) {
  $("#lv-ctx")?.remove();
  const id = Number(node.dataset.loc);
  const name = node.querySelector(".lv-label").textContent;
  const canCmd = id > 0 && Perms.can("locations.command") && Operable.can("location", id);
  const menu = document.createElement("div");
  menu.id = "lv-ctx";
  menu.className = "lv-ctx";
  menu.innerHTML = `
    <button type="button" data-cmd="open">Abrir sus cámaras</button>
    ${id > 0 ? `<hr>
    <button type="button" data-cmd="arm-away" ${canCmd ? "" : "disabled"}>Armar total sus áreas de alarma…</button>
    <button type="button" data-cmd="arm-stay" ${canCmd ? "" : "disabled"}>Armar parcial sus áreas de alarma…</button>
    <button type="button" data-cmd="disarm" ${canCmd ? "" : "disabled"}>Desarmar sus áreas de alarma…</button>
    ${canCmd ? "" : `<div class="muted lv-small" style="padding:4px 12px 6px">Sus roles o su alcance no permiten ordenar en esta ubicación.</div>`}` : ""}`;
  document.body.append(menu);
  const r = menu.getBoundingClientRect();
  menu.style.left = `${Math.min(x, innerWidth - r.width - 8)}px`;
  menu.style.top = `${Math.min(y, innerHeight - r.height - 8)}px`;
  menu.addEventListener("click", (e) => {
    const b = e.target.closest("[data-cmd]");
    if (!b || b.disabled) return;
    menu.remove();
    if (b.dataset.cmd === "open") lvOpenAll(name, lvLocationChannels(id));
    else lvLocationCommand(id, name, b.dataset.cmd);
  });
}

async function lvLocationCommand(locationId, name, command) {
  const verb = { "arm-away": "Armar total", "arm-stay": "Armar parcial" }[command] ?? "Desarmar";
  const url = `/api/locations/${locationId}/command`;
  let preview;
  try { preview = await Api.post(url, { command, dryRun: true }); }
  catch (err) { lvStatus(err.error, true); return; }
  if (!preview.items.length) { toast(preview.message); return; }
  const list = preview.items.slice(0, 15).map((i) => `• ${i.area} (${i.panel})`).join("\n") +
    (preview.items.length > 15 ? `\n… y ${preview.items.length - 15} más` : "");
  if (!confirm(`${verb} · ${name}\n\n${preview.message}\n\n${list}\n\n¿Continuar?`)) return;
  lvStatus(`${verb}: enviando la orden a ${preview.items.length} área(s)…`);
  try {
    const result = await Api.post(url, { command, dryRun: false });
    lvStatus(result.message);
    const failed = result.items.filter((i) => !i.success);
    if (failed.length) toast(`${result.message} Fallaron: ${failed.map((f) => `${f.area} (${f.error || "sin detalle"})`).join("; ")}`, true);
    else toast(result.message);
  } catch (err) {
    lvStatus(err.error, true);
    toast(err.error, true);
  }
}

function lvOnDeviceStatus(dto) {
  const d = lv.devices.find((x) => x.id === dto.id);
  if (!d) return;
  d.status = dto.status;
  lvRenderTree();
}

function lvOnConfigChanged(topic) {
  if (["devices", "channels", "locations"].includes(topic)) {
    clearTimeout(lv.treeReload);
    lv.treeReload = setTimeout(() => { if (lv.active) lvLoadTree(); }, 800);
  } else if (topic === "speakers" && !lv.aux) {
    lvLoadSpeakers();
  }
  // Lo operable (alcance) se relee en app.js: el PTZ se re-evalúa después.
  setTimeout(() => { if (lv.active) lvUpdatePtz(); }, 1500);
}

// ---------------------------------------------------------------------------
// Vistas guardadas (las mismas del cliente de escritorio: viven en el servidor)
// ---------------------------------------------------------------------------
function lvRenderViewsButton() {
  const el = $("#lv-active-view");
  if (el) el.textContent = lv.activeView || "";
}

async function lvRenderViewsPop() {
  const pop = $("#lv-views-pop");
  const canSave = Perms.can("live.views");
  pop.innerHTML = `
    <div class="lv-pop-title">VISTAS GUARDADAS</div>
    <div class="lv-views-list" id="lv-views-list"><div class="muted lv-small">Cargando…</div></div>
    ${canSave ? `
    <hr>
    <div class="lv-views-save">
      <input type="text" id="lv-view-name" maxlength="80" placeholder="Nombre de la vista…">
      <button type="button" class="btn small" id="lv-view-save" title="Guarda la división y las cámaras que hay ahora en la grilla">Guardar</button>
    </div>
    <label class="lv-set-check"><input type="checkbox" id="lv-view-shared"><span>Compartir con todos los puestos</span></label>` : ""}
    <div class="muted lv-small" id="lv-views-msg"></div>`;
  $("#lv-view-save")?.addEventListener("click", lvSaveView);
  $("#lv-view-name")?.addEventListener("keydown", (e) => { if (e.key === "Enter") lvSaveView(); });
  try { lv.views = await Api.get("/api/live-views"); }
  catch (err) { $("#lv-views-list").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  lvRenderViewsList();
}

function lvViewDetail(v) {
  const layout = lvFindLayout(v.layoutName)?.key ?? v.layoutName;
  const owner = v.shared ? ` · compartida${v.owner && v.owner !== Api.username ? ` por ${v.owner}` : ""}` : "";
  return `${v.items.length} cámara(s) · división ${layout}${owner}`;
}

function lvRenderViewsList() {
  const list = $("#lv-views-list");
  if (!list) return;
  if (!lv.views.length) {
    list.innerHTML = `<div class="muted lv-small">Todavía no hay vistas. Arme la grilla y guárdela con un nombre.</div>`;
    return;
  }
  list.innerHTML = lv.views.map((v) => `
    <div class="lv-view-row" data-view="${v.id}" title="Cargar esta vista en la grilla">
      <div class="lv-view-text"><b>${esc(v.name)}</b><span class="muted">${esc(lvViewDetail(v))}</span></div>
      ${v.canEdit ? `<span class="lv-view-actions">
        <button type="button" class="lv-icon-btn" data-vact="update" title="Actualizar esta vista con la grilla actual">${lvSvg(LV_ICO.edit, 13)}</button>
        <button type="button" class="lv-icon-btn danger" data-vact="delete" title="Eliminar esta vista">${lvSvg(LV_ICO.trash, 13)}</button>
      </span>` : ""}
    </div>`).join("");
  $$("#lv-views-list .lv-view-row").forEach((row) => row.addEventListener("click", (e) => {
    const v = lv.views.find((x) => x.id === Number(row.dataset.view));
    if (!v) return;
    const act = e.target.closest("[data-vact]")?.dataset.vact;
    if (act === "update") lvUpdateView(v);
    else if (act === "delete") lvDeleteView(v);
    else lvApplyView(v);
  }));
}

function lvViewRequest(name, shared) {
  return {
    name, layoutName: lv.layout.key, columns: lv.layout.columns, rows: lv.layout.rows, shared,
    items: lv.cells.map((c, i) => (c.feed ? { cellIndex: i, channelId: c.feed.channel.id, streamType: c.feed.profile === "Main" ? 0 : 1 } : null)).filter(Boolean),
  };
}

function lvViewsMsg(text, isError) {
  const el = $("#lv-views-msg");
  if (el) { el.textContent = text; el.classList.toggle("error", !!isError); }
}

async function lvSaveView() {
  const name = $("#lv-view-name").value.trim();
  if (!name) { lvViewsMsg("Escriba un nombre para la vista.", true); return; }
  const req = lvViewRequest(name, $("#lv-view-shared").checked);
  if (!req.items.length) { lvViewsMsg("La grilla está vacía: abra al menos una cámara antes de guardar.", true); return; }
  try {
    const saved = await Api.post("/api/live-views", req);
    lv.views = [saved, ...lv.views.filter((v) => v.id !== saved.id)].sort((a, b) => lvCollator.compare(a.name, b.name));
    lv.activeView = saved.name;
    lvRenderViewsButton();
    $("#lv-view-name").value = "";
    lvRenderViewsList();
    lvViewsMsg(`Vista "${saved.name}" guardada.`);
  } catch (err) { lvViewsMsg(err.error, true); }
}

async function lvUpdateView(v) {
  const req = lvViewRequest(v.name, v.shared);
  if (!req.items.length) { lvViewsMsg("La grilla está vacía: no hay nada que guardar en la vista.", true); return; }
  if (!confirm(`¿Reemplazar la vista "${v.name}" con la grilla actual (${req.items.length} cámara(s), división ${lv.layout.key})?`)) return;
  try {
    const saved = await Api.put(`/api/live-views/${v.id}`, req);
    lv.views = lv.views.map((x) => (x.id === saved.id ? saved : x));
    lvRenderViewsList();
    lvViewsMsg(`Vista "${saved.name}" actualizada.`);
  } catch (err) { lvViewsMsg(err.error, true); }
}

async function lvDeleteView(v) {
  if (!confirm(`¿Eliminar la vista "${v.name}"?${v.shared ? " Está compartida: desaparece para todos los puestos." : ""}`)) return;
  try {
    await Api.delete(`/api/live-views/${v.id}`);
    lv.views = lv.views.filter((x) => x.id !== v.id);
    if (lv.activeView === v.name) { lv.activeView = ""; lvRenderViewsButton(); }
    lvRenderViewsList();
    lvViewsMsg(`Vista "${v.name}" eliminada.`);
  } catch (err) { lvViewsMsg(err.error, true); }
}

async function lvApplyView(v) {
  lvClosePops();
  let view = v;
  try { view = await Api.post(`/api/live-views/${v.id}/apply`); } // la versión vigente + bitácora
  catch (err) { lvStatus(err.error, true); return; }
  const layout = lvRestoreLayout(view.layoutName, view.columns, view.rows);
  lvApplyLayout(layout);
  if (lvFindLayout(layout.key)) { lv.prefs.layout = layout.key; lvSavePrefs(); }
  lv.cells.forEach((c) => c.clear());
  lvSelectCell(null);
  lv.activeView = view.name;
  lvRenderViewsButton();
  const items = [];
  let missing = 0;
  for (const item of view.items) {
    const ch = lv.channels.get(item.channelId);
    const cell = lv.cells[item.cellIndex];
    if (!ch || !cell) { missing++; continue; }
    items.push({ ch, cell, profile: item.streamType === 0 ? "Main" : "Sub" });
  }
  await lvOpenMany(view.name, items);
  if (missing) lvStatus(`Vista "${view.name}": ${missing} cámara(s) ya no están disponibles y se omitieron.`, true);
}

// ---------------------------------------------------------------------------
// "Volver a abrir las cámaras de la última sesión" (solo la ventana principal)
// ---------------------------------------------------------------------------
function lvScheduleLastSave() {
  clearTimeout(lv.lastSaveTimer);
  lv.lastSaveTimer = setTimeout(lvSaveLastNow, 800);
}

function lvSaveLastNow() {
  if (lv.aux || !lv.layout) return;
  try {
    localStorage.setItem(LV_LAST_KEY, JSON.stringify({
      layout: lv.layout.key, columns: lv.layout.columns, rows: lv.layout.rows,
      items: lv.cells.map((c, i) => (c.feed ? { i, ch: c.feed.channel.id, p: c.feed.profile } : null)).filter(Boolean),
    }));
  } catch { /* modo privado */ }
}

async function lvReopenLast() {
  let last = null;
  try { last = JSON.parse(localStorage.getItem(LV_LAST_KEY) || "null"); } catch { /* ilegible */ }
  if (!last?.items?.length) return;
  lvApplyLayout(lvRestoreLayout(last.layout, last.columns, last.rows));
  const items = last.items
    .map((x) => ({ ch: lv.channels.get(x.ch), cell: lv.cells[x.i], profile: x.p === "Main" ? "Main" : "Sub" }))
    .filter((x) => x.ch && x.cell);
  if (items.length) await lvOpenMany("la última sesión", items);
}

// ---------------------------------------------------------------------------
// Pantallas auxiliares: otra ventana del navegador con su propia grilla.
// ---------------------------------------------------------------------------
async function lvOpenAux() {
  lv.auxWindows = lv.auxWindows.filter((w) => w.win && !w.win.closed);
  if (lv.auxWindows.length >= 3) {
    lvStatus("Ya hay 3 pantallas auxiliares abiertas: cierre una para abrir otra.", true);
    lv.auxWindows.at(-1).win.focus();
    return;
  }
  const n = [1, 2, 3].find((k) => !lv.auxWindows.some((w) => w.n === k));
  const w = Math.round(screen.availWidth * 0.85), h = Math.round(screen.availHeight * 0.85);
  const win = window.open(`${location.pathname}#/live?aux=${n}`, `tcvms-aux-${n}`, `popup=yes,width=${w},height=${h}`);
  if (!win) { lvStatus("El navegador bloqueó la ventana emergente: permita las ventanas emergentes de este sitio.", true); return; }
  lv.auxWindows.push({ n, win });
  lvStatus(`Pantalla auxiliar ${n} abierta. Arrástrela al monitor que quiera y use "Pantalla completa".`);
  // Con el permiso de administración de ventanas, se lleva sola a un monitor libre.
  try {
    if (!("getScreenDetails" in window)) return;
    const perm = await navigator.permissions.query({ name: "window-management" }).catch(() => null);
    if (perm?.state === "denied") return;
    const details = await window.getScreenDetails();
    const busy = new Set([details.currentScreen]);
    const target = details.screens.find((s) => !busy.has(s)) ?? null;
    if (target && !win.closed) {
      win.moveTo(target.availLeft, target.availTop);
      win.resizeTo(target.availWidth, target.availHeight);
    }
  } catch { /* sin permiso o un solo monitor: queda donde la abrió el navegador */ }
}

// ---------------------------------------------------------------------------
// PTZ (panel y teclado)
// ---------------------------------------------------------------------------
function lvPtzTarget() {
  const f = lv.selected?.feed;
  if (!f) return null;
  const ch = lv.channels.get(f.channel.id) ?? f.channel;
  return ch.supportsPtz ? ch : null;
}

function lvPtzAvailable() {
  const ch = lvPtzTarget();
  return !!ch && !lv.aux && Perms.can("live.ptz") && Operable.can("channel", ch.id);
}

function lvUpdatePtz() {
  const panel = $("#lv-ptz");
  if (!panel) return;
  const ch = lvPtzTarget();
  const operable = !!ch && Operable.can("channel", ch.id);
  const ok = lvPtzAvailable();
  const name = $("#lv-ptz-name");
  name.textContent = !ch ? "sin cámara PTZ" : operable ? ch.name : `${ch.name} (fuera de su alcance)`;
  name.classList.toggle("off", !ok);
  panel.classList.toggle("disabled", !ok);
  $$("#lv-ptz-body button, #lv-ptz-body input").forEach((el) => {
    if (el.id === "lv-speed") return;
    if (el.textContent === "·") return;
    el.disabled = !ok;
  });
  if (!ok && lv.ptzHeld) lvPtzRelease();
}

/** Las órdenes van en fila: un "detener" nunca debe adelantarse a su "mover". */
function lvPtz(command, stop, speed) {
  const ch = lvPtzTarget();
  if (!ch || !lvPtzAvailable()) return;
  const body = { command, speed: speed ?? (lv.shift ? 1 : lv.prefs.ptzSpeed), stop };
  lv.ptzChain = lv.ptzChain.then(() =>
    Api.post(`/api/devices/${ch.deviceId}/channels/${ch.channelNumber}/ptz`, body)
      .catch((err) => lvStatus(`PTZ: ${err.error}`, true)));
}

function lvPtzRelease() {
  const held = lv.ptzHeld;
  lv.ptzHeld = null;
  if (held) lvPtz(held, true);
}

function lvWirePtz() {
  const panel = $("#lv-ptz");
  const body = $("#lv-ptz-body");
  $("#lv-ptz-toggle").addEventListener("click", () => {
    lv.prefs.ptzOpen = !lv.prefs.ptzOpen;
    lvSavePrefs();
    panel.classList.toggle("open", lv.prefs.ptzOpen);
  });
  // Presionar mantiene el movimiento; soltar (o salir del botón) lo detiene.
  body.addEventListener("pointerdown", (e) => {
    const b = e.target.closest("[data-ptz]");
    if (!b || b.disabled || e.button !== 0) return;
    b.setPointerCapture(e.pointerId);
    if (lv.ptzHeld) lvPtzRelease();
    lv.ptzHeld = b.dataset.ptz;
    b.classList.add("held");
    lvPtz(b.dataset.ptz, false);
  });
  const release = (e) => {
    const b = e.target.closest?.("[data-ptz]");
    b?.classList.remove("held");
    if (lv.ptzHeld) lvPtzRelease();
  };
  body.addEventListener("pointerup", release);
  body.addEventListener("pointercancel", release);
  body.addEventListener("lostpointercapture", release);
  body.addEventListener("click", async (e) => {
    const b = e.target.closest("[data-preset]");
    if (!b || b.disabled) return;
    const ch = lvPtzTarget();
    if (!ch) return;
    const input = $("#lv-preset");
    const index = Math.min(Math.max(Number(input.value) || 1, 1), 300);
    input.value = index;
    const action = b.dataset.preset;
    try {
      await Api.post(`/api/devices/${ch.deviceId}/channels/${ch.channelNumber}/ptz-preset`, { action, index });
      lvStatus(action === "Goto" ? `PTZ: moviéndose al preset ${index}.`
        : action === "Set" ? `PTZ: posición actual guardada como preset ${index}.`
          : `PTZ: preset ${index} eliminado.`);
    } catch (err) { lvStatus(`PTZ: ${err.error}`, true); }
  });
  $("#lv-speed").addEventListener("input", (e) => {
    lv.prefs.ptzSpeed = Number(e.target.value);
    $("#lv-speed-val").textContent = lv.prefs.ptzSpeed;
    lvSavePrefs();
  });
}

const LV_KEY_PTZ = {
  ArrowUp: "TiltUp", ArrowDown: "TiltDown", ArrowLeft: "PanLeft", ArrowRight: "PanRight",
  "+": "ZoomIn", "=": "ZoomIn", Add: "ZoomIn", "-": "ZoomOut", Subtract: "ZoomOut",
};

function lvKeyTargetIsInput(e) {
  const t = e.target;
  return t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
}

function lvSetPrecision(on) {
  lv.shift = on;
  const pill = $("#lv-precision");
  if (pill) pill.hidden = !on || !lvPtzAvailable();
}

/** Teclado: flechas = pan/tilt, +/− = zoom, Shift sostenido = modo precisión (velocidad 1). */
function lvOnKeyDown(e) {
  if (!lv.active) return;
  if (e.key === "Escape") { lvClosePops(); $("#lv-ctx")?.remove(); return; }
  if (lv.aux) return;
  if (e.key === "Shift") { lvSetPrecision(true); return; }
  if (lvKeyTargetIsInput(e) || !$("#modal-backdrop").classList.contains("hidden")) return;
  const command = LV_KEY_PTZ[e.key] ?? (e.code === "NumpadAdd" ? "ZoomIn" : e.code === "NumpadSubtract" ? "ZoomOut" : null);
  if (!command || !lvPtzAvailable()) return;
  e.preventDefault();
  if (e.repeat || lv.keyHeld === command) return;
  if (lv.keyHeld) lvPtz(lv.keyHeld, true);
  lv.keyHeld = command;
  lvPtz(command, false);
}

function lvOnKeyUp(e) {
  if (!lv.active || lv.aux) return;
  if (e.key === "Shift") { lvSetPrecision(false); return; }
  const command = LV_KEY_PTZ[e.key] ?? (e.code === "NumpadAdd" ? "ZoomIn" : e.code === "NumpadSubtract" ? "ZoomOut" : null);
  if (command && lv.keyHeld === command) {
    lv.keyHeld = null;
    lvPtz(command, true);
  }
}

// ---------------------------------------------------------------------------
// Parlantes IP (hablar, sonidos del servidor, biblioteca del equipo, texto a voz)
// ---------------------------------------------------------------------------
async function lvLoadSpeakers() {
  const panel = $("#lv-spk");
  if (!panel) return;
  if (!Perms.can("speakers.play")) { panel.hidden = true; return; }
  let speakers;
  try { speakers = await Api.get("/api/speakers"); } catch { panel.hidden = true; return; }
  lv.speakers = speakers.filter((s) => s.enabled);
  if (!lv.speakers.length) { panel.hidden = true; return; }
  try { lv.sounds = await Api.get("/api/speakers/sounds"); } catch { lv.sounds = []; }
  const ids = new Set(lv.speakers.map((s) => s.id));
  lv.prefs.speakers = (lv.prefs.speakers || []).filter((id) => ids.has(id));
  panel.hidden = false;
  lvRenderSpeakers();
  lvLoadLibrary();
}

function lvSelectedSpeakers() { return lv.speakers.filter((s) => lv.prefs.speakers.includes(s.id)); }

/** Se puede marcar si está en su alcance (uno sin conexión se marca igual: el servidor dirá qué pasó). */
function lvSpeakerOk(s) { return Operable.can("speaker", s.id); }

function lvRenderSpeakers() {
  const panel = $("#lv-spk");
  if (!panel) return;
  const chosen = lvSelectedSpeakers();
  const summary = chosen.length === 0 ? "ninguno" : chosen.length === 1 ? chosen[0].name : `${chosen.length} parlantes`;
  const usable = lv.speakers.filter(lvSpeakerOk);
  const all = usable.length > 0 && usable.every((s) => lv.prefs.speakers.includes(s.id));
  const volume = chosen.find((s) => s.volume != null)?.volume ?? 50;
  const secure = window.isSecureContext && !!navigator.mediaDevices?.getUserMedia;
  const hasTts = chosen.some((s) => s.supportsTts);
  panel.className = `lv-panel ${lv.prefs.spkOpen ? "open" : ""}`;
  panel.innerHTML = `
    <div class="lv-panel-head">
      <span class="lv-cap">PARLANTES — <b class="${chosen.length ? "" : "off"}">${esc(summary)}</b></span>
      <button type="button" class="lv-icon-btn lv-only-open" id="lv-spk-all" title="${all ? "Desmarcar" : "Marcar"} todos los parlantes">${lvSvg(all ? LV_ICO.checkAll : LV_ICO.checkNone, 14)}</button>
      <button type="button" class="lv-chev" id="lv-spk-toggle" title="Minimizar / restaurar el panel de parlantes"></button>
    </div>
    <div class="lv-panel-body">
      <div class="lv-spk-list">${lv.speakers.map((s) => `
        <label class="lv-spk ${lvSpeakerOk(s) ? "" : "off"}" title="${esc([s.location, s.model, s.volume != null ? `Volumen ${s.volume}` : null, s.lastError].filter(Boolean).join(" · "))}">
          <input type="checkbox" data-spk="${s.id}" ${lv.prefs.speakers.includes(s.id) ? "checked" : ""} ${lvSpeakerOk(s) ? "" : "disabled"}>
          <span class="dot ${s.status === "Online" ? "ok" : "bad"}"></span><span>${esc(s.name)}</span>
          ${s.busyWith ? `<em>${esc(s.busyWith)}</em>` : ""}
        </label>`).join("")}</div>
      <div class="lv-spk-row" title="Volumen de salida del parlante (0-100); con varios marcados se aplica a todos">
        <span>Volumen</span><input type="range" id="lv-spk-vol" min="0" max="100" step="5" value="${volume}" ${chosen.length ? "" : "disabled"}><b id="lv-spk-vol-val">${volume}</b>
      </div>
      ${secure ? `
      <div class="lv-spk-row">
        <select id="lv-spk-mic" title="Micrófono con el que se habla por los parlantes (se recuerda)"><option value="">Micrófono predeterminado</option></select>
        <button type="button" class="btn ghost small" id="lv-spk-test" title="Captura el micrófono elegido sin transmitir: el nivel de abajo debe moverse al hablar">Probar</button>
      </div>
      <div class="lv-level"><i id="lv-spk-level"></i></div>` : ""}
      <div class="lv-spk-row">
        <button type="button" class="btn lv-talk" id="lv-spk-talk" ${chosen.length && secure ? "" : "disabled"}
          title="${secure ? "Mantenga presionado para hablar por los parlantes marcados (micrófono de este equipo)" : "Para hablar desde el navegador, el panel debe abrirse por HTTPS o desde el propio servidor (http://localhost)."}">Mantener para hablar</button>
        <button type="button" class="btn ghost small" id="lv-spk-stop" ${chosen.length ? "" : "disabled"} title="Detener lo que esté sonando en los parlantes marcados">Detener</button>
      </div>
      ${secure ? `<label class="lv-set-check"><input type="checkbox" id="lv-spk-tone" ${lv.prefs.tone ? "checked" : ""}><span>Tono al abrir el canal (dos pitidos antes de la voz)</span></label>` : ""}
      ${lv.sounds.length ? `
      <div class="lv-spk-row">
        <select id="lv-spk-sound" title="Sonidos subidos al servidor (Automatizaciones → Sonidos)">${lv.sounds.map((s) => `<option value="${esc(s.displayName)}">${esc(s.displayName)}</option>`).join("")}</select>
        <button type="button" class="lv-icon-btn" id="lv-spk-sound-preview" title="Escuchar en ESTE equipo (no suena en los parlantes); otro clic detiene">${lvSvg(LV_ICO.ear, 14)}</button>
        <button type="button" class="lv-icon-btn" id="lv-spk-sound-play" ${chosen.length ? "" : "disabled"} title="Reproducir el sonido del servidor en los PARLANTES marcados (sincronizado)">${lvSvg(LV_ICO.play, 13)}</button>
      </div>` : ""}
      <div class="lv-spk-row" id="lv-spk-lib-row" hidden>
        <select id="lv-spk-lib"></select>
        <button type="button" class="lv-icon-btn" id="lv-spk-lib-dl" title="Descargar el archivo desde el parlante a este equipo">${lvSvg(LV_ICO.download, 14)}</button>
        <button type="button" class="lv-icon-btn" id="lv-spk-lib-preview" title="Escuchar en ESTE equipo (se descarga del parlante, no suena afuera); otro clic detiene">${lvSvg(LV_ICO.ear, 14)}</button>
        <button type="button" class="lv-icon-btn" id="lv-spk-lib-play" title="Reproducir el audio guardado en el PARLANTE (se busca por nombre en cada uno)">${lvSvg(LV_ICO.play, 13)}</button>
      </div>
      ${hasTts ? `
      <div class="lv-spk-row">
        <input type="text" id="lv-spk-tts" maxlength="100" placeholder="Texto a leer en voz alta…" title="Texto a leer (máximo 100 caracteres)">
        <button type="button" class="lv-icon-btn" id="lv-spk-tts-play" title="El parlante lee el texto en voz alta">${lvSvg(LV_ICO.play, 13)}</button>
      </div>` : ""}
      <div class="muted lv-small" id="lv-spk-msg"></div>
    </div>`;
  lvWireSpeakers();
  lvRenderLibrary();
}

function lvSpkMsg(text, isError) {
  const el = $("#lv-spk-msg");
  if (el) { el.textContent = text; el.classList.toggle("error", !!isError); }
}

function lvWireSpeakers() {
  $("#lv-spk-toggle").addEventListener("click", () => {
    lv.prefs.spkOpen = !lv.prefs.spkOpen;
    lvSavePrefs();
    $("#lv-spk").classList.toggle("open", lv.prefs.spkOpen);
  });
  $("#lv-spk-all").addEventListener("click", () => {
    const usable = lv.speakers.filter(lvSpeakerOk).map((s) => s.id);
    lv.prefs.speakers = usable.every((id) => lv.prefs.speakers.includes(id)) ? [] : usable;
    lvSavePrefs();
    lvRenderSpeakers();
    lvLoadLibrary();
  });
  $$("#lv-spk [data-spk]").forEach((cb) => cb.addEventListener("change", () => {
    const id = Number(cb.dataset.spk);
    lv.prefs.speakers = cb.checked ? [...new Set([...lv.prefs.speakers, id])] : lv.prefs.speakers.filter((x) => x !== id);
    lvSavePrefs();
    lvRenderSpeakers();
    lvLoadLibrary();
  }));
  // Volumen: se aplica al soltar, a todos los marcados.
  const vol = $("#lv-spk-vol");
  vol.addEventListener("input", () => { $("#lv-spk-vol-val").textContent = vol.value; });
  vol.addEventListener("change", async () => {
    const targets = lvSelectedSpeakers();
    const volume = Number(vol.value);
    let ok = 0;
    for (const s of targets) {
      try {
        const updated = await Api.put(`/api/speakers/${s.id}/volume`, { volume });
        Object.assign(s, updated);
        ok++;
      } catch (err) { lvSpkMsg(`${s.name}: ${err.error}`, true); }
    }
    if (ok) lvSpkMsg(`Volumen ${volume} aplicado a ${ok} parlante${ok === 1 ? "" : "s"}.`);
  });
  $("#lv-spk-stop").addEventListener("click", async () => {
    try {
      const r = await Api.post("/api/speakers/stop", { speakerIds: lvSelectedSpeakers().map((s) => s.id) });
      lvSpkMsg(r.message);
    } catch (err) { lvSpkMsg(err.error, true); }
  });
  $("#lv-spk-tone")?.addEventListener("change", (e) => { lv.prefs.tone = e.target.checked; lvSavePrefs(); });

  // Micrófono: lista (los nombres aparecen tras el primer permiso) y prueba.
  const mic = $("#lv-spk-mic");
  if (mic) {
    navigator.mediaDevices.enumerateDevices().then((devices) => {
      const inputs = devices.filter((d) => d.kind === "audioinput" && d.deviceId && d.deviceId !== "default");
      mic.innerHTML = `<option value="">Micrófono predeterminado</option>` +
        inputs.map((d, i) => `<option value="${esc(d.deviceId)}" ${d.deviceId === lv.prefs.mic ? "selected" : ""}>${esc(d.label || `Micrófono ${i + 1}`)}</option>`).join("");
    }).catch(() => { /* sin acceso a dispositivos */ });
    mic.addEventListener("change", () => { lv.prefs.mic = mic.value; lvSavePrefs(); });
    $("#lv-spk-test").addEventListener("click", () => {
      if (lv.talk?.testing) { lvStopTalk(); return; }
      lvStartTalk(true);
    });
  }

  // Hablar: mantener presionado (como el PTZ).
  const talk = $("#lv-spk-talk");
  talk.addEventListener("pointerdown", (e) => {
    if (talk.disabled || e.button !== 0) return;
    talk.setPointerCapture(e.pointerId);
    lvStartTalk(false);
  });
  const stopTalk = () => { if (lv.talk && !lv.talk.testing) lvStopTalk(); };
  talk.addEventListener("pointerup", stopTalk);
  talk.addEventListener("pointercancel", stopTalk);
  talk.addEventListener("lostpointercapture", stopTalk);

  $("#lv-spk-sound-play")?.addEventListener("click", () =>
    lvSpeakerPlay({ source: "server", sound: $("#lv-spk-sound").value }));
  $("#lv-spk-sound-preview")?.addEventListener("click", () =>
    lvPreview(`/api/workflows/audio/${encodeURIComponent($("#lv-spk-sound").value)}/file`, $("#lv-spk-sound").value));
  $("#lv-spk-tts-play")?.addEventListener("click", () => {
    const text = $("#lv-spk-tts").value.trim();
    if (!text) { lvSpkMsg("Escriba el texto a leer.", true); return; }
    lvSpeakerPlay({ source: "tts", text, language: "spanish", voice: "female" });
  });
  $("#lv-spk-lib-play").addEventListener("click", () => {
    const item = lv.library.find((x) => String(x.id) === $("#lv-spk-lib").value);
    if (item) lvSpeakerPlay({ source: "library", libraryName: item.name });
  });
  $("#lv-spk-lib-preview").addEventListener("click", () => {
    const item = lv.library.find((x) => String(x.id) === $("#lv-spk-lib").value);
    if (item) lvPreview(`/api/speakers/${lv.library.owner}/library/${item.id}/file`, item.name);
  });
  $("#lv-spk-lib-dl").addEventListener("click", async () => {
    const item = lv.library.find((x) => String(x.id) === $("#lv-spk-lib").value);
    if (!item) return;
    try {
      const blob = await lvFetchBlob(`/api/speakers/${lv.library.owner}/library/${item.id}/file`);
      lvDownload(blob, `${lvSafeName(item.name)}.${(item.format || "wav").toLowerCase()}`);
    } catch (err) { lvSpkMsg(err.message, true); }
  });
}

async function lvSpeakerPlay(request) {
  const ids = lvSelectedSpeakers().map((s) => s.id);
  if (!ids.length) { lvSpkMsg("Marque al menos un parlante.", true); return; }
  try {
    const r = await Api.post("/api/speakers/play", { speakerIds: ids, ...request });
    lvSpkMsg(r.message, !r.success);
  } catch (err) { lvSpkMsg(err.error, true); }
}

/** Biblioteca: la del primer parlante marcado que tenga (en los demás se busca por nombre). */
async function lvLoadLibrary() {
  const owner = lvSelectedSpeakers().find((s) => s.supportsLibrary);
  lv.library = [];
  if (owner) {
    try { lv.library = await Api.get(`/api/speakers/${owner.id}/library`); } catch { lv.library = []; }
    lv.library.owner = owner.id;
    lv.library.ownerName = owner.name;
  }
  lvRenderLibrary();
}

function lvRenderLibrary() {
  const row = $("#lv-spk-lib-row");
  if (!row) return;
  row.hidden = !lv.library.length;
  if (!lv.library.length) return;
  const sel = $("#lv-spk-lib");
  sel.title = `Biblioteca de ${lv.library.ownerName}`;
  sel.innerHTML = lv.library.map((x) => `<option value="${x.id}">${esc(x.name)}</option>`).join("");
}

async function lvFetchBlob(url) {
  const res = await fetch(url, { headers: Api.token ? { Authorization: "Bearer " + Api.token } : {} });
  if (!res.ok) throw new Error(res.status === 404 ? "El archivo no está disponible." : `Error ${res.status} al descargar.`);
  return res.blob();
}

/** Escucha local (no suena en los parlantes); otro clic detiene. */
async function lvPreview(url, label) {
  if (lv.preview) { lvStopPreview(); return; }
  try {
    const blob = await lvFetchBlob(url);
    const src = URL.createObjectURL(blob);
    const audio = new Audio(src);
    lv.preview = { audio, src };
    audio.onended = lvStopPreview;
    await audio.play();
    lvSpkMsg(`Escuchando "${label}" en este equipo (no suena en los parlantes).`);
  } catch (err) {
    lvStopPreview();
    lvSpkMsg(err.message || "No se pudo reproducir el audio.", true);
  }
}

function lvStopPreview() {
  const p = lv.preview;
  lv.preview = null;
  if (!p) return;
  try { p.audio.pause(); } catch { /* noop */ }
  URL.revokeObjectURL(p.src);
}

/** Dos pitidos de 1 kHz (PCM 16 bits a 8 kHz) que avisan que el canal ya está abierto. */
function lvToneFrames() {
  const rate = 8000, beep = 0.12, gap = 0.08;
  const total = Math.round(rate * (beep * 2 + gap * 2));
  const pcm = new Int16Array(total);
  for (let i = 0; i < total; i++) {
    const t = i / rate;
    const inBeep = t < beep || (t >= beep + gap && t < beep * 2 + gap);
    pcm[i] = inBeep ? Math.round(Math.sin(2 * Math.PI * 1000 * t) * 9000) : 0;
  }
  return pcm.buffer;
}

/**
 * Voz en vivo: micrófono → PCM 16 bits mono a 8 kHz → WebSocket
 * /api/speakers/talk (el servidor convierte a G.711 y escribe en todos los
 * marcados). testing = solo medir el nivel, sin transmitir.
 */
async function lvStartTalk(testing) {
  if (lv.talk) lvStopTalk();
  const ids = lvSelectedSpeakers().map((s) => s.id);
  if (!testing && !ids.length) { lvSpkMsg("Marque al menos un parlante.", true); return; }
  const state = { testing, ws: null, ready: false, stream: null, ctx: null, node: null, rest: new Float32Array(0) };
  lv.talk = state;
  const btn = testing ? $("#lv-spk-test") : $("#lv-spk-talk");
  btn?.classList.add("on");
  if (!testing) btn.textContent = "● Hablando… (suelte para terminar)";
  try {
    state.stream = await navigator.mediaDevices.getUserMedia({
      audio: { deviceId: lv.prefs.mic ? { exact: lv.prefs.mic } : undefined, echoCancellation: true, noiseSuppression: true, channelCount: 1 },
    });
  } catch (err) {
    lvStopTalk();
    lvSpkMsg(`No se pudo usar el micrófono: ${err.message}`, true);
    return;
  }
  if (lv.talk !== state) { state.stream.getTracks().forEach((t) => t.stop()); return; }
  // El contexto va a la tasa del equipo (no todos los navegadores conectan un
  // micrófono a un contexto de 8 kHz): se baja a 8 kHz aquí, promediando cada
  // ventana (filtro simple contra el aliasing, suficiente para voz).
  state.ctx = new AudioContext();
  const ratio = state.ctx.sampleRate / 8000;
  const source = state.ctx.createMediaStreamSource(state.stream);
  state.node = state.ctx.createScriptProcessor(2048, 1, 1);
  state.node.onaudioprocess = (e) => {
    const input = e.inputBuffer.getChannelData(0);
    const buf = new Float32Array(state.rest.length + input.length);
    buf.set(state.rest);
    buf.set(input, state.rest.length);
    const n = Math.floor(buf.length / ratio);
    const pcm = new Int16Array(n);
    let sum = 0;
    for (let k = 0; k < n; k++) {
      const a = Math.floor(k * ratio), b = Math.max(a + 1, Math.floor((k + 1) * ratio));
      let acc = 0;
      for (let i = a; i < b; i++) acc += buf[i];
      const v = Math.max(-1, Math.min(1, acc / (b - a)));
      sum += v * v;
      pcm[k] = v < 0 ? v * 0x8000 : v * 0x7fff;
    }
    state.rest = buf.slice(Math.floor(n * ratio));
    const level = n ? Math.min(100, Math.sqrt(sum / n) * 300) : 0;
    const bar = $("#lv-spk-level");
    if (bar) { bar.style.width = `${level}%`; bar.classList.toggle("hot", level > 85); }
    if (state.ready && state.ws?.readyState === WebSocket.OPEN) state.ws.send(pcm.buffer);
  };
  source.connect(state.node);
  state.node.connect(state.ctx.destination);
  if (testing) { lvSpkMsg("Prueba de micrófono: hable y mire el nivel (no se transmite)."); return; }

  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  const ws = new WebSocket(`${proto}//${location.host}/api/speakers/talk?ids=${ids.join(",")}&access_token=${encodeURIComponent(Api.token || "")}`);
  ws.binaryType = "arraybuffer";
  state.ws = ws;
  ws.onmessage = (e) => {
    if (typeof e.data !== "string") return;
    let msg = null;
    try { msg = JSON.parse(e.data); } catch { return; }
    if (msg?.type !== "ready") return;
    const names = (msg.speakers || []).map((s) => s.name).join(", ");
    const rejected = (msg.rejected || []).map((r) => `${r.speakerName} (${r.message})`).join("; ");
    lvSpkMsg(`Hablando por ${names || "los parlantes"}${rejected ? `. No aceptaron: ${rejected}` : ""}.`, !!rejected && !names);
    if (lv.prefs.tone) ws.send(lvToneFrames());
    state.ready = true;
  };
  ws.onerror = () => lvSpkMsg("No se pudo abrir la voz en vivo con el servidor.", true);
  ws.onclose = (e) => { if (lv.talk === state) { lvStopTalk(); if (e.code !== 1000 && e.reason) lvSpkMsg(e.reason, true); } };
}

function lvStopTalk() {
  const state = lv.talk;
  lv.talk = null;
  if (!state) return;
  try { if (state.ws?.readyState === WebSocket.OPEN) { state.ws.send("stop"); state.ws.close(1000); } } catch { /* noop */ }
  try { state.node?.disconnect(); } catch { /* noop */ }
  state.ctx?.close().catch(() => {});
  state.stream?.getTracks().forEach((t) => t.stop());
  const talk = $("#lv-spk-talk");
  if (talk) { talk.classList.remove("on"); talk.textContent = "Mantener para hablar"; }
  $("#lv-spk-test")?.classList.remove("on");
  const bar = $("#lv-spk-level");
  if (bar) bar.style.width = "0";
  if (!state.testing) lvSpkMsg("Voz en vivo terminada.");
}
