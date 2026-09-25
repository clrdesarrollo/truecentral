// Alarma global de cerco eléctrico (panel web). Escucha el hub en TODAS las páginas:
// ante un evento crítico de un panel de cerco (caída del cerco, alarma de zona,
// pánico, sabotaje) muestra un aviso fijo arriba, hace sonar una sirena en este
// navegador hasta que el operador la reconozca, parpadea el título de la pestaña y,
// si el navegador lo permite, emite una notificación del sistema.
//
// La sirena se genera con Web Audio (no depende de archivos) y se programa en el
// reloj de audio, así sigue sonando aunque la pestaña esté en segundo plano (ahí los
// temporizadores de JS se ralentizan). Los navegadores exigen un gesto del usuario
// antes de reproducir sonido: el audio se desbloquea con el primer clic o tecla.
// Se carga después de hub.js y cerco.js (usa VmsHub y cercoNormalize).
"use strict";

const CercoAlarm = (() => {
  const ALARM_KINDS = new Set(["FenceCut", "Alarm", "Panic", "Tamper", "HvFault"]);
  const active = new Map();          // clave -> { panelId, panelName, description, at, sounding }
  let started = false;
  let ctx = null;                    // AudioContext compartido
  let siren = null;                  // { gain, timer } mientras suena
  let titleTimer = null, baseTitle = document.title;
  let notifAsked = false;

  // ---------- audio ----------
  function unlock() {
    try {
      ctx ??= new (window.AudioContext || window.webkitAudioContext)();
      // Si ya había una alarma esperando el desbloqueo, la sirena arranca ahora.
      Promise.resolve(ctx.state === "suspended" ? ctx.resume() : null)
        .then(() => { if (active.size) refresh(); })
        .catch(() => {});
    } catch { /* sin Web Audio */ }
    // La notificación del sistema también exige un gesto para pedir permiso.
    if (started && !notifAsked && "Notification" in window && window.isSecureContext && Notification.permission === "default") {
      notifAsked = true;
      Notification.requestPermission().catch(() => {});
    }
  }
  for (const ev of ["pointerdown", "keydown", "touchstart"]) window.addEventListener(ev, unlock, { passive: true });

  const audioReady = () => !!ctx && ctx.state === "running";

  // Sirena de dos tonos (960 / 770 Hz cada 0.45 s) programada 12 s hacia adelante en el
  // reloj de audio; un intervalo la extiende antes de que se acabe.
  function scheduleSiren(from) {
    const osc = ctx.createOscillator();
    osc.type = "square";
    const end = from + 12;
    for (let t = from, hi = true; t < end; t += 0.45, hi = !hi) osc.frequency.setValueAtTime(hi ? 960 : 770, t);
    osc.connect(siren.gain);
    osc.start(from);
    osc.stop(end);
    siren.until = end;
  }
  function startSiren() {
    if (siren || !audioReady()) return;
    const gain = ctx.createGain();
    gain.gain.value = 0.12;
    gain.connect(ctx.destination);
    siren = { gain, until: 0, timer: null };
    scheduleSiren(ctx.currentTime + 0.05);
    siren.timer = setInterval(() => {
      if (siren && siren.until - ctx.currentTime < 6) scheduleSiren(siren.until);
    }, 2000);
  }
  function stopSiren() {
    if (!siren) return;
    clearInterval(siren.timer);
    try { siren.gain.disconnect(); } catch { /* ya desconectado */ }
    siren = null;
  }
  function updateSound() {
    const anySounding = [...active.values()].some((a) => a.sounding);
    if (anySounding) startSiren(); else stopSiren();
  }

  // ---------- título parpadeante (pestaña en segundo plano) ----------
  function updateTitle() {
    if (active.size && !titleTimer) {
      baseTitle = document.title;
      let on = false;
      titleTimer = setInterval(() => { on = !on; document.title = on ? "🚨 ALARMA DE CERCO" : baseTitle; }, 1000);
    } else if (!active.size && titleTimer) {
      clearInterval(titleTimer); titleTimer = null; document.title = baseTitle;
    }
  }

  // ---------- aviso en pantalla ----------
  function bar() {
    let el = document.getElementById("cerco-alarm");
    if (!el) {
      el = document.createElement("div");
      el.id = "cerco-alarm";
      el.className = "cerco-alarm hidden";
      el.setAttribute("role", "alert");
      document.body.appendChild(el);
      el.addEventListener("click", onClick);
    }
    return el;
  }

  function render() {
    const el = bar();
    if (!active.size) { el.classList.add("hidden"); el.innerHTML = ""; return; }
    const list = [...active.values()].sort((a, b) => b.at - a.at);
    const panels = [...new Map(list.map((a) => [a.panelId, a.panelName])).entries()];
    el.innerHTML = `
      <div class="cerco-alarm-body">
        <b>🚨 Alarma de cerco eléctrico</b>
        <ul>${list.slice(0, 4).map((a) => `<li><b>${esc(a.panelName)}</b> — ${esc(a.description)}
          <span class="muted">${a.at.toLocaleTimeString()}</span></li>`).join("")}
          ${list.length > 4 ? `<li class="muted">y ${list.length - 4} más…</li>` : ""}</ul>
        ${list.some((a) => a.sounding) && !audioReady()
          ? `<div class="cerco-alarm-hint">🔇 Haga clic en cualquier parte de la página para activar el sonido de la alarma.</div>` : ""}
      </div>
      <div class="cerco-alarm-actions">
        ${panels.map(([id, name]) => `<button class="btn danger" data-silence="${id}" title="Apaga la sirena del panel ${esc(name)}">Silenciar sirena${panels.length > 1 ? ` · ${esc(name)}` : ""}</button>`).join("")}
        <button class="btn ghost" data-go="monitor">Ver monitor</button>
        <button class="btn" data-ack="1" title="Detiene el sonido en este navegador y cierra el aviso">Reconocer</button>
      </div>`;
    el.classList.remove("hidden");
  }

  async function onClick(e) {
    const b = e.target.closest("button");
    if (!b) return;
    if (b.dataset.ack) { active.clear(); refresh(); return; }
    if (b.dataset.go) { location.hash = "#/cerco"; return; }
    if (b.dataset.silence) {
      b.disabled = true;
      try { await Api.post(`/api/cerco/panels/${b.dataset.silence}/silence`); toast("Sirena silenciada."); }
      catch (err) { toast(err.error ?? "No se pudo silenciar la sirena.", true); b.disabled = false; }
    }
  }

  function refresh() { render(); updateSound(); updateTitle(); }

  // ---------- notificación del sistema ----------
  // Solo en contextos seguros (https o localhost): por http a una IP de la LAN el
  // navegador no la permite; el aviso en pantalla y el sonido funcionan igual.
  function systemNotify(a) {
    if (!("Notification" in window) || Notification.permission !== "granted" || !document.hidden) return;
    try {
      const n = new Notification(`Alarma de cerco: ${a.panelName}`, {
        body: a.description, tag: `cerco-${a.panelId}`, requireInteraction: true,
      });
      n.onclick = () => { window.focus(); location.hash = "#/cerco"; n.close(); };
    } catch { /* bloqueada por el navegador */ }
  }

  // ---------- eventos del hub ----------
  function onEvent(e) {
    cercoNormalize(e);
    if (ALARM_KINDS.has(e.kind)) {
      const a = {
        panelId: e.panelId, panelName: e.panelName, description: e.description,
        at: new Date(e.receivedAt ?? Date.now()), sounding: true,
      };
      active.set(`${e.panelId}:${e.kind}:${e.zoneNumber ?? ""}`, a);
      refresh();
      systemNotify(a);
    } else if (e.kind === "Disarmed") {
      // Desarmar atiende la alarma: se quitan las de ese panel.
      for (const [k, a] of active) if (a.panelId === e.panelId) active.delete(k);
      refresh();
    } else if (e.kind === "SirenOff") {
      // Alguien silenció la sirena del panel: se apaga el sonido aquí, el aviso sigue.
      for (const a of active.values()) if (a.panelId === e.panelId) a.sounding = false;
      refresh();
    }
  }

  return {
    /// Se llama al entrar a la aplicación (cualquier página). Idempotente.
    start() {
      if (started) return;
      started = true;
      VmsHub.on("CercoEventReceived", onEvent);
      VmsHub.ensureStarted();
    },
  };
})();
