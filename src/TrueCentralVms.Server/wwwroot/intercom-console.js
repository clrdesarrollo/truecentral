// CLR TrueCentral VMS — Aplicaciones → Citofonía (puesto de atención).
// Réplica en el panel de la pantalla "Citofonía" del cliente
// (Views\IntercomView.xaml + IntercomCallWindow.xaml): frentes con su estado y
// "Ver y hablar" / "Abrir puerta", las últimas llamadas, y la ventana de
// llamada (timbre, Contestar / Rechazar / Colgar, puertas y conversación).
//
// Voz: el mismo WebSocket que usa el cliente (/api/intercoms/{id}/voice, PCM16
// mono 8 kHz, tramas de 40 ms). El navegador solo entrega el micrófono en un
// contexto seguro (HTTPS o localhost); si no lo hay, se escucha al visitante
// pero no se le puede hablar (se mandan tramas de silencio, porque el servidor
// corta la conversación si no le llega audio).
// Video: el panel web no reproduce RTSP; se muestra la foto de la cámara del
// frente, renovada cada pocos segundos.
// La página Configuración → Dispositivos → Citofonía (intercoms.js) sigue
// siendo la del alta y edición; de ahí se reutilizan INTERCOM_CALL_STATES,
// intercomStatusCell e intercomDuration.
"use strict";

let intercomConsoleTimer = null;
const ICA_POLL_MS = 2000;
const ICA_PAGE = 50;
const icaState = { skip: 0, intercomId: "", state: "" };
let icaIntercoms = [];
let icaCameras = null;            // channelId → { deviceId, channelNumber }
let icaSilenced = new Set();      // llamadas cuyo timbre se silenció en este puesto
let icaDismissed = new Set();     // llamadas cuya ventana cerró el operador sin atender
let icaCallsKey = "";
let icaWindow = null;             // { intercomId, callId }

// ---------------------------------------------------------------------------
// Timbre (réplica de IntercomRinger: 440+480 Hz, dos ráfagas de 400 ms y pausa)
// ---------------------------------------------------------------------------
const icaRinger = {
  ctx: null, timer: null,
  start() {
    if (this.timer) return;
    try { this.ctx ??= new (window.AudioContext || window.webkitAudioContext)(); } catch { return; }
    const ring = () => {
      const ctx = this.ctx;
      if (ctx.state === "suspended") ctx.resume().catch(() => {});
      const t0 = ctx.currentTime + 0.02;
      [0, 0.6].forEach((offset) => {
        const g = ctx.createGain();
        g.gain.setValueAtTime(0.0001, t0 + offset);
        g.gain.exponentialRampToValueAtTime(0.18, t0 + offset + 0.02);
        g.gain.setValueAtTime(0.18, t0 + offset + 0.38);
        g.gain.exponentialRampToValueAtTime(0.0001, t0 + offset + 0.4);
        g.connect(ctx.destination);
        [440, 480].forEach((f) => {
          const o = ctx.createOscillator();
          o.frequency.value = f;
          o.connect(g);
          o.start(t0 + offset);
          o.stop(t0 + offset + 0.42);
        });
      });
    };
    ring();
    this.timer = setInterval(ring, 3000);
  },
  stop() { clearInterval(this.timer); this.timer = null; },
};

// ---------------------------------------------------------------------------
// Conversación (réplica de IntercomVoiceClient)
// ---------------------------------------------------------------------------
const icaVoice = {
  ws: null, ctx: null, stream: null, proc: null, gain: null, keepAlive: null,
  nextTime: 0, muted: false, volume: 1, pending: [], micAvailable: false, intercomId: null,
  onStatus: null, onEnded: null, onLevels: null,
  get active() { return this.ws && this.ws.readyState <= 1; },

  async start(intercomId) {
    if (this.active) return;
    this.intercomId = intercomId;
    this.ctx = new (window.AudioContext || window.webkitAudioContext)();
    this.gain = this.ctx.createGain();
    this.gain.gain.value = this.volume;
    this.gain.connect(this.ctx.destination);
    this.nextTime = 0;
    this.pending = [];

    // Micrófono: solo en contexto seguro. Sin él, se escucha y se manda silencio.
    this.micAvailable = false;
    if (window.isSecureContext && navigator.mediaDevices?.getUserMedia) {
      try {
        this.stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, channelCount: 1 } });
        this.micAvailable = true;
      } catch { this.stream = null; }
    }

    const proto = location.protocol === "https:" ? "wss" : "ws";
    const ws = new WebSocket(`${proto}://${location.host}/api/intercoms/${intercomId}/voice?access_token=${encodeURIComponent(Api.token || "")}`);
    ws.binaryType = "arraybuffer";
    this.ws = ws;
    let opened = false;

    await new Promise((resolve, reject) => {
      ws.onopen = () => { opened = true; resolve(); };
      ws.onerror = () => { if (!opened) reject(new Error("El servidor no abrió la conversación: otro operador está hablando con este frente, la llamada ya la tomó alguien o el frente no responde.")); };
      ws.onclose = () => { if (!opened) reject(new Error("El servidor no abrió la conversación.")); };
    }).catch((err) => { this.cleanup(); throw err; });

    ws.onmessage = (e) => {
      if (typeof e.data === "string") {
        let msg = null;
        try { msg = JSON.parse(e.data); } catch { return; }
        if (msg.type === "ready") this.onStatus?.(this.micAvailable ? "Conversación abierta."
          : window.isSecureContext
            ? "Conversación abierta SOLO PARA ESCUCHAR: no se pudo usar el micrófono (permiso denegado o sin micrófono conectado)."
            : "Conversación abierta SOLO PARA ESCUCHAR: el navegador no entrega el micrófono sin HTTPS.");
        else if (msg.type === "detached") this.onStatus?.(`El frente terminó la llamada (${msg.reason}); la voz sigue abierta.`);
        else if (msg.type === "closed") this.onStatus?.(`Conversación cerrada: ${msg.reason}${msg.seconds != null ? ` (${msg.seconds} s)` : ""}.`);
        return;
      }
      this.play(new Int16Array(e.data));
    };
    ws.onclose = () => { const had = !!this.ws; this.cleanup(); if (had) this.onEnded?.(); };

    if (this.micAvailable) this.startMic();
    else this.keepAlive = setInterval(() => this.send(new Int16Array(320)), 40);
  },

  startMic() {
    const ctx = this.ctx;
    const src = ctx.createMediaStreamSource(this.stream);
    const proc = ctx.createScriptProcessor(2048, 1, 1);
    const ratio = ctx.sampleRate / 8000;
    let carry = 0;
    proc.onaudioprocess = (e) => {
      const input = e.inputBuffer.getChannelData(0);
      // Baja a 8 kHz promediando cada tramo (filtro simple contra el aliasing).
      let sum = 0;
      for (let pos = carry; pos < input.length; pos += ratio) {
        const a = Math.floor(pos), b = Math.min(input.length, Math.floor(pos + ratio));
        let acc = 0;
        for (let i = a; i < Math.max(a + 1, b); i++) acc += input[i];
        const v = acc / Math.max(1, b - a);
        this.pending.push(v);
        sum += v * v;
        carry = pos + ratio - input.length;
      }
      this.onLevels?.({ mic: this.muted ? 0 : Math.min(100, Math.round(Math.sqrt(sum / Math.max(1, input.length / ratio)) * 300)) });
      while (this.pending.length >= 320) {
        const frame = new Int16Array(320);
        const chunk = this.pending.splice(0, 320);
        if (!this.muted) for (let i = 0; i < 320; i++) frame[i] = Math.max(-32768, Math.min(32767, Math.round(chunk[i] * 32767)));
        this.send(frame);
      }
    };
    const sink = ctx.createGain();
    sink.gain.value = 0;              // el procesador necesita estar conectado para correr
    src.connect(proc);
    proc.connect(sink);
    sink.connect(ctx.destination);
    this.proc = proc;
  },

  send(frame) { if (this.ws?.readyState === 1) this.ws.send(frame.buffer); },

  play(samples) {
    if (!this.ctx || !samples.length) return;
    const buffer = this.ctx.createBuffer(1, samples.length, 8000);
    const data = buffer.getChannelData(0);
    let sum = 0;
    for (let i = 0; i < samples.length; i++) { data[i] = samples[i] / 32768; sum += data[i] * data[i]; }
    const node = this.ctx.createBufferSource();
    node.buffer = buffer;
    node.connect(this.gain);
    const now = this.ctx.currentTime;
    // Búfer corto: si se atrasa más de medio segundo, se descarta el retraso.
    if (this.nextTime < now + 0.04 || this.nextTime > now + 0.5) this.nextTime = now + 0.08;
    node.start(this.nextTime);
    this.nextTime += buffer.duration;
    this.onLevels?.({ remote: Math.min(100, Math.round(Math.sqrt(sum / samples.length) * 300)) });
  },

  setVolume(v) { this.volume = v; if (this.gain) this.gain.gain.value = v; },

  stop() {
    if (this.ws?.readyState === 1) { try { this.ws.send("stop"); } catch { /* cerrado */ } }
    try { this.ws?.close(); } catch { /* cerrado */ }
    this.cleanup();
  },

  cleanup() {
    clearInterval(this.keepAlive); this.keepAlive = null;
    try { this.proc?.disconnect(); } catch { /* ya desconectado */ }
    this.stream?.getTracks().forEach((t) => t.stop());
    try { this.ctx?.close(); } catch { /* ya cerrado */ }
    this.ws = null; this.ctx = null; this.stream = null; this.proc = null; this.gain = null;
    this.muted = false;
  },
};

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
function icaCallText(i) {
  const c = i.activeCall;
  if (!i.enabled) return ["Pausado", "muted"];
  if (c?.state === "Ringing") return ["Sonando", "ringing"];
  if (c?.state === "InCall") return [`En conversación · ${c.answeredBy ?? ""}`, "incall"];
  if (!i.callCenterEnabled) return ["El botón no llama a la central", "muted"];
  return ["Libre", "muted"];
}

async function renderIntercomConsole() {
  $("#page-title").textContent = "Citofonía";
  icaCallsKey = "";
  $("#view").innerHTML = `
    ${window.isSecureContext ? "" : `<div class="info-box">Este panel está abierto sin HTTPS: puede contestar, escuchar al visitante y abrir puertas,
      pero el navegador no permite usar el micrófono para hablarle. Para conversar, use el cliente de escritorio o abra el panel por HTTPS.</div>`}
    <div class="ica-layout">
      <section class="ica-col">
        <div class="ica-col-head"><b>Frentes de citofonía</b><span class="muted" id="ica-count"></span></div>
        <div class="ica-list" id="ica-list"><div class="info-box" style="margin:10px">Cargando frentes…</div></div>
      </section>
      <section class="ica-col">
        <div class="ica-col-head"><b>Últimas llamadas</b>
          <div class="ica-filters">
            <select id="ica-f-intercom"><option value="">Todos los frentes</option></select>
            <select id="ica-f-state">
              <option value="">Todos los resultados</option>
              <option value="Completed">Contestadas</option>
              <option value="Missed">No contestadas</option>
              <option value="Rejected">Rechazadas</option>
            </select>
            <button class="btn ghost" id="ica-refresh">Actualizar</button>
          </div>
        </div>
        <div class="ica-history" id="ica-history"><div class="info-box" style="margin:10px">Cargando llamadas…</div></div>
      </section>
    </div>
    <div id="ica-call-host"></div>`;

  $("#ica-f-intercom").addEventListener("change", (e) => { icaState.intercomId = e.target.value; icaState.skip = 0; icaLoadHistory(); });
  $("#ica-f-state").addEventListener("change", (e) => { icaState.state = e.target.value; icaState.skip = 0; icaLoadHistory(); });
  $("#ica-refresh").addEventListener("click", () => icaLoadHistory());

  if (!icaCameras) {
    try {
      icaCameras = new Map((await Api.get("/api/workflows/cameras"))
        .filter((c) => c.supportsSnapshot).map((c) => [c.channelId, c]));
    } catch { icaCameras = new Map(); }
  }
  await icaPoll();
  icaLoadHistory();

  clearInterval(intercomConsoleTimer);
  intercomConsoleTimer = setInterval(icaPoll, ICA_POLL_MS);
}

async function icaPoll() {
  try { icaIntercoms = await Api.get("/api/intercoms"); } catch { return; }
  if (!$("#ica-list")) return;
  icaRenderList();

  // Filtro de frentes del historial.
  const sel = $("#ica-f-intercom");
  if (sel && sel.options.length !== icaIntercoms.length + 1) {
    sel.innerHTML = `<option value="">Todos los frentes</option>` +
      icaIntercoms.map((i) => `<option value="${i.id}" ${String(i.id) === icaState.intercomId ? "selected" : ""}>${esc(i.name)}</option>`).join("");
  }

  // Timbre: mientras alguna llamada suene y no se haya silenciado aquí.
  const ringing = icaIntercoms.filter((i) => i.activeCall?.state === "Ringing");
  if (ringing.some((i) => !icaSilenced.has(i.activeCall.id))) icaRinger.start(); else icaRinger.stop();

  // Una llamada que empieza a sonar abre su ventana (como el cliente).
  if (!icaWindow) {
    const next = ringing.find((i) => !icaDismissed.has(i.activeCall.id));
    if (next) icaOpenCall(next.id);
  } else {
    icaRenderCall();
  }

  // El historial se refresca cuando cambia alguna llamada en curso.
  const key = JSON.stringify(icaIntercoms.map((i) => [i.id, i.activeCall?.id, i.activeCall?.state]));
  if (key !== icaCallsKey) {
    if (icaCallsKey) icaLoadHistory();
    icaCallsKey = key;
  }
}

function icaRenderList() {
  const box = $("#ica-list");
  const online = icaIntercoms.filter((i) => i.status === "Online").length;
  $("#ica-count").textContent = `${icaIntercoms.length} frente(s) · ${online} en línea`;
  if (!icaIntercoms.length) {
    box.innerHTML = `<div class="ica-empty muted">No hay frentes de citofonía. Agréguelos en Dispositivos → Citofonía.</div>`;
    return;
  }
  box.innerHTML = icaIntercoms.map((i) => {
    const [text, cls] = icaCallText(i);
    const status = { Online: "En línea", Offline: "Sin conexión", AuthFailed: "Credenciales rechazadas" }[i.status] ?? "—";
    const detail = [i.model, i.host, i.groupName].filter(Boolean).join(" · ");
    return `
      <div class="ica-item ${cls === "ringing" ? "ringing" : ""}" data-id="${i.id}">
        <span class="dot ${i.status === "Online" ? "ok" : "bad"}"></span>
        <div class="ica-item-main">
          <div class="ica-row-top"><b>${esc(i.name)}</b><span class="ica-call ${cls}">${esc(text)}</span></div>
          ${detail ? `<div class="muted ica-small">${esc(detail)}</div>` : ""}
          <div class="muted ica-small">${status}${i.lastError ? ` · ${esc(i.lastError)}` : ""}</div>
          <div class="ica-item-actions">
            <button class="btn ${cls === "ringing" ? "" : "ghost"} ica-open">${cls === "ringing" ? "Atender" : "Ver y hablar"}</button>
            ${i.doorCount > 0 ? `<button class="btn ghost ica-door">Abrir puerta</button>` : ""}
          </div>
        </div>
      </div>`;
  }).join("");
  $$(".ica-item", box).forEach((row) => {
    const id = Number(row.dataset.id);
    $(".ica-open", row).addEventListener("click", () => { icaDismissed.delete(icaIntercoms.find((i) => i.id === id)?.activeCall?.id); icaOpenCall(id); });
    $(".ica-door", row)?.addEventListener("click", () => {
      const i = icaIntercoms.find((x) => x.id === id);
      if (confirm(`¿Abrir la puerta de "${i.name}"?`)) icaOpenDoor(id, 1);
    });
  });
}

async function icaOpenDoor(id, door) {
  try {
    const r = await Api.post(`/api/intercoms/${id}/doors/${door}/open`);
    toast(r.message || `Puerta ${door} abierta.`);
  } catch (err) { toast(err.error, true); }
}

async function icaLoadHistory() {
  const box = $("#ica-history");
  if (!box) return;
  const q = [`skip=${icaState.skip}`, `take=${ICA_PAGE}`];
  if (icaState.intercomId) q.push(`intercomId=${icaState.intercomId}`);
  if (icaState.state) q.push(`state=${icaState.state}`);
  let data;
  try { data = await Api.get(`/api/intercoms/calls?${q.join("&")}`); }
  catch (err) { box.innerHTML = `<div class="error-box" style="margin:10px">${esc(err.error)}</div>`; return; }
  if (!$("#ica-history")) return;
  if (!data.items.length) {
    box.innerHTML = `<div class="ica-empty muted">No hay llamadas registradas${icaState.intercomId || icaState.state ? " con esos filtros" : ""}.</div>`;
    return;
  }
  const page = Math.floor(icaState.skip / ICA_PAGE) + 1, pages = Math.max(1, Math.ceil(data.total / ICA_PAGE));
  box.innerHTML = `
    <div class="table-scroll"><table class="grid ica-table">
      <thead><tr><th>Inicio</th><th>Frente</th><th>Resultado</th><th>Atendió</th><th>Duración</th><th>Puerta</th><th>Detalle</th></tr></thead>
      <tbody>${data.items.map((c) => `
        <tr>
          <td class="muted" style="white-space:nowrap">${formatDateTime(c.startedAt)}</td>
          <td>${esc(c.intercomName)}</td>
          <td>${INTERCOM_CALL_STATES[c.state] ?? esc(c.state)}</td>
          <td>${esc(c.answeredBy || "—")}</td>
          <td class="muted">${c.state === "InCall" ? "en curso" : intercomDuration(c)}</td>
          <td>${c.doorOpened ? `<span class="tag on">Abierta</span> <span class="muted">${esc(c.doorOpenedBy || "")}</span>` : `<span class="muted">—</span>`}</td>
          <td class="muted ica-detail">${esc([c.origin, c.endReason].filter(Boolean).join(" · ") || "—")}</td>
        </tr>`).join("")}
      </tbody>
    </table></div>
    <div class="audit-pager" style="padding:0 12px 10px">
      <span class="muted">${data.total} llamada(s) · página ${page} de ${pages}</span>
      <div style="display:flex;gap:8px">
        <button class="btn ghost" id="ica-prev" ${page <= 1 ? "disabled" : ""}>« Anteriores</button>
        <button class="btn ghost" id="ica-next" ${page >= pages ? "disabled" : ""}>Siguientes »</button>
      </div>
    </div>`;
  $("#ica-prev")?.addEventListener("click", () => { icaState.skip = Math.max(0, icaState.skip - ICA_PAGE); icaLoadHistory(); });
  $("#ica-next")?.addEventListener("click", () => { icaState.skip += ICA_PAGE; icaLoadHistory(); });
}

// ---------------------------------------------------------------------------
// Ventana de llamada (réplica de IntercomCallWindow)
// ---------------------------------------------------------------------------
let icaTick = null;
let icaSnapTimer = null;
let icaCallRenderKey = "";
let icaVoiceStatus = "";
let icaAutoClose = null;

function icaOpenCall(intercomId) {
  const i = icaIntercoms.find((x) => x.id === intercomId);
  if (!i) return;
  if (icaWindow && icaWindow.intercomId !== intercomId) {
    if (icaVoice.active) { toast("Termine la conversación en curso antes de atender otro frente.", true); return; }
    icaCloseCall(false);
  }
  icaWindow = { intercomId, callId: i.activeCall?.id ?? null };
  icaCallRenderKey = "";
  icaVoiceStatus = "";
  const host = $("#ica-call-host");
  host.innerHTML = `
    <div class="ica-overlay">
      <div class="ica-window">
        <div class="ica-video">
          <img id="ica-snap" alt="" style="display:none">
          <div class="ica-novideo muted" id="ica-novideo"></div>
          <span class="ica-video-note">Foto del frente · se renueva cada pocos segundos (el video en vivo está en el cliente de escritorio)</span>
        </div>
        <div class="ica-side" id="ica-side"></div>
      </div>
    </div>`;
  icaSetupSnapshot(i);
  clearInterval(icaTick);
  icaTick = setInterval(icaUpdateTimer, 1000);
  icaRenderCall();
}

function icaSetupSnapshot(i) {
  clearInterval(icaSnapTimer);
  const cam = i.channelId ? icaCameras?.get(i.channelId) : null;
  const img = $("#ica-snap"), none = $("#ica-novideo");
  if (!cam) { none.textContent = i.channelId ? "La cámara del frente no entrega fotos." : "Este frente no tiene cámara vinculada."; return; }
  const load = () => {
    const next = new Image();
    next.onload = () => { if ($("#ica-snap")) { img.src = next.src; img.style.display = ""; none.textContent = ""; } };
    next.onerror = () => { if ($("#ica-novideo") && img.style.display === "none") none.textContent = "No se pudo obtener la imagen del frente."; };
    next.src = `/api/devices/${cam.deviceId}/snapshot/${cam.channelNumber}?access_token=${encodeURIComponent(Api.token || "")}&t=${Date.now()}`;
  };
  none.textContent = "Cargando imagen…";
  load();
  icaSnapTimer = setInterval(load, 3000);
}

function icaCurrent() {
  const i = icaIntercoms.find((x) => x.id === icaWindow?.intercomId);
  const call = i?.activeCall ?? null;
  // Se sigue la llamada de la ventana; si terminó, activeCall ya no la trae.
  if (call && icaWindow.callId == null) icaWindow.callId = call.id;
  return { i, call: call && call.id === icaWindow.callId ? call : null };
}

function icaRenderCall() {
  const side = $("#ica-side");
  if (!side || !icaWindow) return;
  const { i, call } = icaCurrent();
  if (!i) { icaCloseCall(false); return; }
  const mine = call?.state === "InCall" && call.answeredBy === Api.username;
  const endedCall = icaWindow.callId != null && !call;
  const key = JSON.stringify([i.status, i.doorCount, call, icaVoice.active, icaVoice.muted, icaVoiceStatus, icaSilenced.has(call?.id), endedCall]);
  if (key === icaCallRenderKey) return;
  icaCallRenderKey = key;

  let badge, badgeCls, panel;
  if (call?.state === "Ringing") {
    badge = "LLAMADA ENTRANTE"; badgeCls = "ringing";
    panel = `
      <div class="ica-actions">
        <button class="btn ica-answer" id="ica-answer">Contestar</button>
        <button class="btn danger" id="ica-reject">Rechazar</button>
      </div>
      <label class="ica-check"><input type="checkbox" id="ica-silence" ${icaSilenced.has(call.id) ? "checked" : ""}> Silenciar timbre en este puesto</label>`;
  } else if (call?.state === "InCall" && !mine) {
    badge = "ATENDIDA EN OTRO PUESTO"; badgeCls = "other";
    panel = `<div class="muted ica-small" style="white-space:normal">La contestó ${esc(call.answeredBy)}. Esta ventana se cerrará sola.</div>`;
    icaScheduleClose(3000);
  } else if (endedCall && !icaVoice.active) {
    badge = "LLAMADA TERMINADA"; badgeCls = "ended";
    panel = `<div class="muted ica-small">La llamada terminó. Esta ventana se cerrará sola.</div>`;
    icaScheduleClose(4000);
  } else if (mine || icaVoice.active) {
    badge = "EN CONVERSACIÓN"; badgeCls = "incall";
    panel = `
      <div class="ica-actions">
        <button class="btn danger" id="ica-hangup">Colgar</button>
        <button class="btn ghost ${icaVoice.muted ? "on" : ""}" id="ica-mute" ${icaVoice.micAvailable ? "" : "disabled"}>${icaVoice.muted ? "🎤 Micrófono silenciado" : "🎤 Silenciar micrófono"}</button>
      </div>
      <div class="ica-meters">
        <label>Micrófono</label><div class="ica-meter"><span id="ica-lvl-mic"></span></div>
        <label>Visitante</label><div class="ica-meter"><span id="ica-lvl-remote"></span></div>
        <label>Volumen</label><input type="range" id="ica-volume" min="0" max="100" value="${Math.round(icaVoice.volume * 100)}">
      </div>`;
  } else {
    badge = i.status === "Online" ? "EN VIVO" : "SIN CONEXIÓN"; badgeCls = i.status === "Online" ? "live" : "offline";
    panel = `
      <div class="ica-actions">
        <button class="btn" id="ica-talk" ${i.status === "Online" ? "" : "disabled"}>Hablar con el frente</button>
      </div>`;
  }

  const doors = Array.from({ length: i.doorCount }, (_, k) =>
    `<button class="btn ghost ica-door-btn" data-door="${k + 1}">🔓 Abrir puerta ${k + 1}</button>`).join("");
  side.innerHTML = `
    <div class="ica-side-head">
      <span class="ica-badge ${badgeCls}">${badge}</span>
      <button class="ica-close" id="ica-close" title="Cerrar${mine ? " (cuelga la llamada)" : ""}">✕</button>
    </div>
    <div class="ica-name">${esc(i.name)}</div>
    <div class="muted ica-small" style="white-space:normal">${esc(call?.origin || [i.model, i.groupName].filter(Boolean).join(" · ") || "")}</div>
    <div class="ica-timer" id="ica-timer"></div>
    ${panel}
    ${icaVoiceStatus ? `<div class="info-box" style="margin:10px 0 0">${esc(icaVoiceStatus)}</div>` : ""}
    ${doors ? `<div class="ica-section">Puertas</div><div class="ica-doors">${doors}</div>` : ""}`;
  icaUpdateTimer();

  $("#ica-close").addEventListener("click", () => icaCloseCall(true));
  $("#ica-answer")?.addEventListener("click", (e) => icaAnswer(call, e.currentTarget));
  $("#ica-reject")?.addEventListener("click", (e) => icaCommand(call, "reject", e.currentTarget, "Llamada rechazada."));
  $("#ica-silence")?.addEventListener("change", (e) => {
    if (e.target.checked) icaSilenced.add(call.id); else icaSilenced.delete(call.id);
    icaPoll();
  });
  $("#ica-hangup")?.addEventListener("click", (e) => icaHangup(call, e.currentTarget));
  $("#ica-mute")?.addEventListener("click", () => { icaVoice.muted = !icaVoice.muted; icaCallRenderKey = ""; icaRenderCall(); });
  $("#ica-volume")?.addEventListener("input", (e) => icaVoice.setVolume(Number(e.target.value) / 100));
  $("#ica-talk")?.addEventListener("click", (e) => icaStartVoice(i.id, e.currentTarget));
  $$(".ica-door-btn", side).forEach((b) => b.addEventListener("click", () => icaOpenDoor(i.id, Number(b.dataset.door))));
}

function icaUpdateTimer() {
  const el = $("#ica-timer");
  if (!el || !icaWindow) return;
  const { call } = icaCurrent();
  const from = call?.state === "Ringing" ? call.startedAt : call?.state === "InCall" ? call.answeredAt : null;
  if (!from) { el.textContent = ""; return; }
  const s = Math.max(0, Math.floor((Date.now() - new Date(from)) / 1000));
  el.textContent = `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

function icaScheduleClose(ms) {
  if (icaAutoClose) return;
  icaAutoClose = setTimeout(() => { icaAutoClose = null; icaCloseCall(false); }, ms);
}

async function icaCommand(call, action, button, okMessage) {
  if (button) button.disabled = true;
  try {
    const r = await Api.post(`/api/intercoms/calls/${call.id}/${action}`);
    toast(r.message || okMessage);
    await icaPoll();
    return true;
  } catch (err) {
    toast(err.error, true);
    if (button) button.disabled = false;
    await icaPoll();
    return false;
  }
}

async function icaAnswer(call, button) {
  if (!(await icaCommand(call, "answer", button, "Llamada contestada."))) return;
  await icaStartVoice(call.intercomId, null);
}

async function icaStartVoice(intercomId, button) {
  if (button) button.disabled = true;
  icaVoice.onStatus = (text) => { icaVoiceStatus = text; icaCallRenderKey = ""; icaRenderCall(); };
  icaVoice.onEnded = () => { icaVoiceStatus = ""; icaCallRenderKey = ""; icaPoll(); };
  icaVoice.onLevels = (l) => {
    if (l.mic != null) { const el = $("#ica-lvl-mic"); if (el) el.style.width = `${l.mic}%`; }
    if (l.remote != null) { const el = $("#ica-lvl-remote"); if (el) el.style.width = `${l.remote}%`; }
  };
  try {
    await icaVoice.start(intercomId);
  } catch (err) {
    icaVoiceStatus = err.message;
    toast(err.message, true);
  }
  icaCallRenderKey = "";
  icaRenderCall();
}

async function icaHangup(call, button) {
  if (button) button.disabled = true;
  icaVoice.stop();
  icaVoiceStatus = "";
  if (call?.state === "InCall") await icaCommand(call, "hangup", null, "Llamada terminada.");
  icaCallRenderKey = "";
  icaRenderCall();
}

/** Cierra la ventana; si el operador tenía la conversación, cuelga (como el cliente). */
async function icaCloseCall(byUser) {
  const { call } = icaWindow ? icaCurrent() : { call: null };
  if (byUser && call?.state === "Ringing") icaDismissed.add(call.id);
  if (icaVoice.active) {
    icaVoice.stop();
    if (call?.state === "InCall" && call.answeredBy === Api.username) {
      try { await Api.post(`/api/intercoms/calls/${call.id}/hangup`); } catch { /* ya terminó */ }
    }
  }
  clearInterval(icaTick); clearInterval(icaSnapTimer);
  clearTimeout(icaAutoClose); icaAutoClose = null;
  icaWindow = null;
  const host = $("#ica-call-host");
  if (host) host.innerHTML = "";
}

/** Al salir de la página: sin timbre ni conversación colgando en otra vista. */
function intercomConsoleLeave() {
  clearInterval(intercomConsoleTimer);
  icaRinger.stop();
  if (icaWindow) icaCloseCall(false);
}
