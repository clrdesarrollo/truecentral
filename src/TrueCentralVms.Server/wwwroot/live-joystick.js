// CLR TrueCentral VMS — Vista en vivo: joystick PTZ (panel web).
//
// Mueve la cámara PTZ del cuadro seleccionado en la ventana principal con un
// joystick USB que Windows reconoce como mando de juego HID: el navegador lo
// lee directo por la Gamepad API, sin el complemento local. La palanca
// (ejes 0 y 1) mueve con velocidad proporcional a cuánto se incline y el giro
// (eje 2) hace zoom, todo a la vez; los botones se asignan a acciones en
// Ajustes de este navegador (se guarda por joystick, en este navegador).
//
// La palanca y el giro viajan juntos como velocidades (pan, tilt y zoom en
// −1..1) por /ptz/move: el servidor usa la orden nativa del equipo (ONVIF
// ContinuousMove, ISAPI continuous de Hikvision por la SDK, MOVE_CONTINUOUSLY
// de Dahua) o la traduce a órdenes de dirección y zoom. Mientras se mueve se
// renueva cada segundo: sin noticias por 3 s el servidor detiene la cámara.
// Foco e iris (botones) van por /ptz como en el panel. Permiso live.ptz y
// alcance, como siempre. Solo actúa con la ventana enfocada y visible, y se
// detiene al soltar, al perder el foco, al abrir un diálogo o al salir de la
// vista.
//
// Depende de live.js (lv, lvPtzTarget, lvPtzAvailable, lvPtzSend…).

const LV_JOY_KEY = "tcvms.live.joystick";
const LV_JOY_DEAD = 0.18;      // zona muerta de la palanca (fracción del recorrido)
const LV_JOY_DEAD_ZOOM = 0.25; // el giro suele volver menos centrado
const LV_JOY_GAP_MS = 80;      // pausa mínima entre órdenes seguidas (un cambio de velocidad no satura al equipo)
const LV_JOY_KEEPALIVE_MS = 1000; // renovación del movimiento (hombre muerto del servidor: 3 s)
const LV_JOY_PRECISION = 0.2;  // modo precisión: fracción de la velocidad
// Al soltar, el resorte rebota y pasa un instante por la dirección contraria:
// justo después de detenerse, un movimiento nuevo debe sostenerse un poco.
const LV_JOY_SETTLE_MS = 300;
const LV_JOY_HOLD_MS = 150;

/** Acciones de los botones. ptz: orden que dura mientras se sostiene; el resto se dispara al apretar. */
const LV_JOY_ACTIONS = [
  { key: "zoomIn", label: "Zoom +", ptz: "ZoomIn" },
  { key: "zoomOut", label: "Zoom −", ptz: "ZoomOut" },
  { key: "focusNear", label: "Foco cerca", ptz: "FocusNear" },
  { key: "focusFar", label: "Foco lejos", ptz: "FocusFar" },
  { key: "irisOpen", label: "Iris: abrir", ptz: "IrisOpen" },
  { key: "irisClose", label: "Iris: cerrar", ptz: "IrisClose" },
  { key: "precision", label: "Precisión (velocidad mínima mientras se sostiene)" },
  ...Array.from({ length: 10 }, (_, d) => ({ key: `d${d}`, label: `Dígito ${d} (número de preset)` })),
  { key: "presetGoto", label: "Ir al preset tecleado" },
  { key: "presetSet", label: "Guardar la posición en el preset tecleado" },
  { key: "presetClear", label: "Borrar el número tecleado" },
  { key: "next", label: "Seleccionar el cuadro siguiente" },
  { key: "prev", label: "Seleccionar el cuadro anterior" },
  { key: "maximize", label: "Maximizar el cuadro / volver a la grilla" },
];

const lvJoy = {
  cfgs: null,         // { [id del joystick]: { enabled, invertY, invertZoom, buttons: { [índice]: acción } } }
  raf: 0,
  index: null,        // joystick en uso (navigator.getGamepads()[index])
  pressed: [],        // botones apretados en el cuadro anterior (flancos)
  sent: null,         // última orden enviada: { ch, pan, tilt, zoom } o { ch, command, speed } (foco, iris)
  sentAt: 0,
  want: null,         // orden que corresponde al estado actual del joystick
  busy: false,
  stoppedAt: null,    // cuándo se soltó (rebote)
  pendingSince: 0,
  entry: "",          // número de preset tecleado con los dígitos
  precision: false,
  learning: null,     // acción que espera su botón (diálogo de asignación)
  pill: null,
};

function lvJoyConfig(id) {
  if (!lvJoy.cfgs) {
    try { lvJoy.cfgs = JSON.parse(localStorage.getItem(LV_JOY_KEY) || "{}") || {}; } catch { lvJoy.cfgs = {}; }
  }
  return lvJoy.cfgs[id] ??= { enabled: true, invertY: false, invertZoom: false, background: false, buttons: {} };
}

function lvJoySave() {
  try { localStorage.setItem(LV_JOY_KEY, JSON.stringify(lvJoy.cfgs)); } catch { /* modo privado */ }
}

/** El joystick en uso: el último que se tocó, o el primero con palanca. */
function lvJoyPad() {
  let pads = [];
  try { pads = [...navigator.getGamepads()].filter((g) => g && g.connected && g.axes.length >= 2); } catch { return null; }
  return pads.find((g) => g.index === lvJoy.index) ?? pads[0] ?? null;
}

function lvJoyStart() {
  if (lv.aux || !("getGamepads" in navigator)) return;
  lvJoy.pill = null; // el panel PTZ se volvió a dibujar
  window.addEventListener("gamepadconnected", lvJoyOnConnect);
  window.addEventListener("gamepaddisconnected", lvJoyOnDisconnect);
  document.addEventListener("visibilitychange", lvJoyRelease);
  // Volver a la vista sin recargar: el navegador ya lo expone y no avisa otra vez.
  if (lvJoyPad()) lvJoyLoop();
}

function lvJoyStop() {
  window.removeEventListener("gamepadconnected", lvJoyOnConnect);
  window.removeEventListener("gamepaddisconnected", lvJoyOnDisconnect);
  document.removeEventListener("visibilitychange", lvJoyRelease);
  cancelAnimationFrame(lvJoy.raf);
  lvJoy.raf = 0;
  lvJoy.learning = null;
  lvJoyRelease();
}

function lvJoyOnConnect(e) {
  if (e.gamepad.axes.length < 2) return;
  lvJoy.index = e.gamepad.index;
  lvStatus(`Joystick conectado: ${e.gamepad.id}.`);
  lvJoyLoop();
  if (!$("#lv-settings-pop")?.hidden) lvRenderSettingsPop();
}

function lvJoyOnDisconnect(e) {
  if (e.gamepad.index !== lvJoy.index && lvJoyPad()) return;
  lvJoy.index = null;
  lvJoyRelease();
  lvJoyUpdatePill();
  lvStatus("Se desconectó el joystick.", true);
}

function lvJoyLoop() {
  if (!lvJoy.raf && lv.active) lvJoy.raf = requestAnimationFrame(lvJoyTick);
}

/** Suelta lo que el joystick tenga en marcha (la cámara se detiene) y olvida los botones. */
function lvJoyRelease() {
  lvJoy.pressed = [];
  lvJoyHalt();
}

/** La ventana perdió el foco: se suelta, salvo que el joystick siga en segundo plano. */
function lvJoyOnBlur() {
  const pad = lvJoyPad();
  if (!pad || !lvJoyConfig(pad.id).background) lvJoyRelease();
}

/** Detiene la cámara sin olvidar los botones: uno que sigue apretado no se dispara de nuevo al volver. */
function lvJoyHalt() {
  if (lvJoy.precision) { lvJoy.precision = false; lvSetPrecision(false); }
  lvJoySet(null);
}

function lvJoyTick() {
  lvJoy.raf = 0;
  if (!lv.active) return;
  const pad = lvJoyPad();
  if (!pad) { lvJoyRelease(); lvJoyUpdatePill(); return; } // vuelve con gamepadconnected
  lvJoy.raf = requestAnimationFrame(lvJoyTick);
  lvJoy.index = pad.index;
  const cfg = lvJoyConfig(pad.id);
  const pressed = pad.buttons.map((b) => b.pressed);
  const rising = pressed.map((p, i) => p && !lvJoy.pressed[i]);
  lvJoy.pressed = pressed;
  if (lvJoy.learning) {
    const i = rising.indexOf(true);
    if (i >= 0) lvJoyAssign(cfg, i, lvJoy.learning);
    lvJoyHalt();
    return;
  }
  lvJoyUpdatePill(cfg);
  // En segundo plano basta con que la página esté a la vista (otra ventana encima o enfocada no la detiene).
  const can = cfg.enabled && !document.hidden && (cfg.background || document.hasFocus()) && lvPtzAvailable() &&
    $("#modal-backdrop").classList.contains("hidden");
  if (!can) { lvJoyHalt(); return; }

  let held = null, precision = false;
  pressed.forEach((p, i) => {
    const action = LV_JOY_ACTIONS.find((a) => a.key === cfg.buttons[i]);
    if (!action) return;
    if (p && action.ptz) held = action.ptz;
    if (p && action.key === "precision") precision = true;
    if (rising[i] && !action.ptz && action.key !== "precision") lvJoyAction(action.key);
  });
  if (precision !== lvJoy.precision) { lvJoy.precision = precision; lvSetPrecision(precision); }
  lvJoySet(lvJoyDebounce(lvJoyDesired(pad, cfg, held)));
}

/** Filtra el rebote de la palanca al soltarla (ver LV_JOY_SETTLE_MS). */
function lvJoyDebounce(want) {
  const now = performance.now();
  if (!want) {
    if (lvJoy.sent || lvJoy.want) lvJoy.stoppedAt = now;
    lvJoy.pendingSince = 0;
    return null;
  }
  if (lvJoy.sent || now - (lvJoy.stoppedAt ?? -Infinity) > LV_JOY_SETTLE_MS) return want;
  lvJoy.pendingSince ||= now;
  return now - lvJoy.pendingSince >= LV_JOY_HOLD_MS ? want : null;
}

/**
 * Lo que corresponde al joystick: foco o iris sostenidos (una orden de /ptz,
 * como en el panel), o las velocidades de pan, tilt y zoom juntas (palanca,
 * giro y botones de zoom).
 */
function lvJoyDesired(pad, cfg, held) {
  const ch = lvPtzTarget();
  if (!ch) return null;
  const scale = lvJoy.precision || lv.shift ? LV_JOY_PRECISION : 1;
  if (held && !held.startsWith("Zoom")) return { ch, command: held, speed: scale < 1 ? 1 : lv.prefs.ptzSpeed };
  const x = pad.axes[0] ?? 0;
  const y = (pad.axes[1] ?? 0) * (cfg.invertY ? -1 : 1);
  // Zona muerta radial: fuera de ella la velocidad crece de 0 a 1 con la inclinación.
  const mag = Math.min(1, Math.hypot(x, y));
  const k = mag > LV_JOY_DEAD ? (mag - LV_JOY_DEAD) / (1 - LV_JOY_DEAD) / mag : 0;
  let zoom = 0;
  if (held) {
    zoom = (held === "ZoomIn" ? 1 : -1) * lv.prefs.ptzSpeed / 7;
  } else if (pad.axes.length > 2) {
    const z = (pad.axes[2] ?? 0) * (cfg.invertZoom ? -1 : 1);
    if (Math.abs(z) > LV_JOY_DEAD_ZOOM) zoom = Math.sign(z) * Math.min(1, (Math.abs(z) - LV_JOY_DEAD_ZOOM) / (1 - LV_JOY_DEAD_ZOOM));
  }
  // Pasos de 0,05: un temblor de la mano no genera una orden por cuadro. En la Gamepad API "arriba" es negativo.
  const q = (v) => Math.round(Math.max(-1, Math.min(1, v * scale)) * 20) / 20 || 0;
  const move = { ch, pan: q(x * k), tilt: q(-y * k), zoom: q(zoom) };
  return move.pan || move.tilt || move.zoom ? move : null;
}

const lvJoySame = (a, b) => (!a && !b) ||
  (!!a && !!b && a.ch.id === b.ch.id && a.command === b.command && a.speed === b.speed &&
    a.pan === b.pan && a.tilt === b.tilt && a.zoom === b.zoom);

function lvJoySet(want) {
  lvJoy.want = want;
  if (lvJoy.busy) return;
  // Un movimiento sostenido se renueva: el servidor detiene la cámara si deja de oírlo.
  const renew = lvJoy.sent?.pan !== undefined && performance.now() - lvJoy.sentAt >= LV_JOY_KEEPALIVE_MS;
  if (renew || !lvJoySame(want, lvJoy.sent)) lvJoyFlush();
}

/**
 * Lleva la cámara al estado pedido con UNA orden en vuelo a la vez: lo que
 * cambió mientras tanto se resume en la orden siguiente (sin cola). Lo que se
 * estaba haciendo se detiene al cambiar de cámara o de tipo de orden, y el
 * "detener" va a la cámara que se movía aunque la selección cambie.
 */
async function lvJoyFlush() {
  lvJoy.busy = true;
  try {
    let renew = !!lvJoy.sent && lvJoySame(lvJoy.want, lvJoy.sent);
    while (renew || !lvJoySame(lvJoy.want, lvJoy.sent)) {
      renew = false;
      const want = lvJoy.want, sent = lvJoy.sent;
      if (sent && (!want || want.ch.id !== sent.ch.id || want.command !== sent.command))
        await (sent.command ? lvPtzSend(sent.ch, sent.command, true, sent.speed) : lvJoyMoveSend({ ch: sent.ch, pan: 0, tilt: 0, zoom: 0 }));
      if (want) await (want.command ? lvPtzSend(want.ch, want.command, false, want.speed) : lvJoyMoveSend(want));
      lvJoy.sent = want;
      lvJoy.sentAt = performance.now();
      if (lvJoy.want) await new Promise((r) => setTimeout(r, LV_JOY_GAP_MS));
    }
  } finally {
    lvJoy.busy = false;
  }
}

/** Velocidades a /ptz/move, en la misma fila que las órdenes del panel y el teclado. */
function lvJoyMoveSend(m) {
  return lv.ptzChain = lv.ptzChain.then(() =>
    Api.post(`/api/devices/${m.ch.deviceId}/channels/${m.ch.channelNumber}/ptz/move`, { pan: m.pan, tilt: m.tilt, zoom: m.zoom })
      .catch((err) => lvStatus(`PTZ: ${err.error}`, true)));
}

function lvJoyAction(key) {
  if (/^d\d$/.test(key)) {
    lvJoy.entry = (lvJoy.entry + key[1]).replace(/^0+/, "").slice(-3);
    const input = $("#lv-preset");
    if (input && lvJoy.entry) input.value = lvJoy.entry;
    lvStatus(lvJoy.entry ? `Preset ${lvJoy.entry}: apriete «Ir al preset» en el joystick para llamarlo.` : "Número de preset borrado.");
    return;
  }
  switch (key) {
    case "presetClear":
      lvJoy.entry = "";
      lvStatus("Número de preset borrado.");
      break;
    case "presetGoto":
    case "presetSet":
      // El mismo botón del panel: usa su número, valida el permiso y muestra el resultado.
      $(`#lv-ptz-body [data-preset="${key === "presetGoto" ? "Goto" : "Set"}"]`)?.click();
      lvJoy.entry = "";
      break;
    case "next":
    case "prev": {
      const cells = lv.cells.filter((c) => c.feed && !c.el.hidden);
      if (!cells.length) return;
      const i = cells.indexOf(lv.selected);
      lvSelectCell(cells[(i + (key === "next" ? 1 : -1) + cells.length) % cells.length]);
      break;
    }
    case "maximize":
      if (lv.selected?.feed) lvToggleMaximize(lv.selected);
      break;
  }
}

/** Indicador "JOYSTICK" en el título del panel PTZ: hay joystick y mueve la cámara seleccionada. */
function lvJoyUpdatePill(cfg) {
  const pad = lvJoyPad();
  const on = !!pad && (cfg ?? lvJoyConfig(pad.id)).enabled && lvPtzAvailable();
  if (on === lvJoy.pill) return;
  lvJoy.pill = on;
  const pill = $("#lv-joy-pill");
  if (pill) pill.hidden = !on;
}

// ---------------------------------------------------------------------------
// Ajustes y asignación de botones
// ---------------------------------------------------------------------------
function lvJoySettingsHtml() {
  if (!("getGamepads" in navigator)) return "";
  const pad = lvJoyPad();
  const head = `<div class="lv-pop-title" style="margin-top:12px">Joystick PTZ</div>`;
  if (!pad)
    return `${head}<div class="muted lv-small">No se detecta ningún joystick. Conéctelo por USB y muévalo o apriete
      un botón con esta ventana activa: el navegador lo muestra recién ahí.</div>`;
  const cfg = lvJoyConfig(pad.id);
  const assigned = Object.keys(cfg.buttons).length;
  return `${head}
    <div class="muted lv-small" title="${esc(pad.id)}">${esc(pad.id)} · ${pad.axes.length} ejes, ${pad.buttons.length} botones</div>
    <label class="lv-set-check"><input type="checkbox" id="lv-joy-on" ${cfg.enabled ? "checked" : ""}>
      <span>Mover la cámara PTZ seleccionada con el joystick</span></label>
    <label class="lv-set-check"><input type="checkbox" id="lv-joy-invy" ${cfg.invertY ? "checked" : ""}>
      <span>Invertir arriba y abajo</span></label>
    <label class="lv-set-check"><input type="checkbox" id="lv-joy-invz" ${cfg.invertZoom ? "checked" : ""}>
      <span>Invertir el zoom del giro</span></label>
    <label class="lv-set-check"><input type="checkbox" id="lv-joy-bg" ${cfg.background ? "checked" : ""}>
      <span>Mover la cámara aunque esta ventana no esté en primer plano (si el navegador entrega el joystick)</span></label>
    <div style="margin-top:8px"><button type="button" class="btn ghost small" id="lv-joy-buttons">Asignar botones…</button>
      <span class="muted lv-small">${assigned ? `${assigned} asignados` : "ninguno asignado"}</span></div>`;
}

function lvJoyWireSettings() {
  const pad = lvJoyPad();
  if (!pad || !$("#lv-joy-on")) return;
  const cfg = lvJoyConfig(pad.id);
  const bind = (id, prop) => $(id).addEventListener("change", (e) => {
    cfg[prop] = e.target.checked;
    lvJoySave();
    lvJoyRelease();
    lvJoyUpdatePill(cfg);
  });
  bind("#lv-joy-on", "enabled");
  bind("#lv-joy-invy", "invertY");
  bind("#lv-joy-invz", "invertZoom");
  bind("#lv-joy-bg", "background");
  $("#lv-joy-buttons").addEventListener("click", () => { lvClosePops(); lvJoyOpenButtons(); });
}

/** Un botón hace una sola acción y cada acción tiene un solo botón. */
function lvJoyAssign(cfg, index, key) {
  for (const [i, k] of Object.entries(cfg.buttons)) if (k === key || Number(i) === index) delete cfg.buttons[i];
  cfg.buttons[index] = key;
  lvJoySave();
  lvJoy.learning = null;
  lvJoyRenderButtons();
}

function lvJoyOpenButtons() {
  const pad = lvJoyPad();
  if (!pad) return;
  openModal(`
    <h3>Botones del joystick</h3>
    <p class="muted field-hint">${esc(pad.id)}. Elija «Asignar» en una acción y apriete el botón del joystick que la hará.
      Los dígitos arman el número de preset que llaman «Ir al preset» y «Guardar» (como en un teclado PTZ).</p>
    <div class="table-scroll" style="max-height:55vh"><table class="grid" id="lv-joy-table"></table></div>
    <div class="modal-actions">
      <button type="button" class="btn ghost" id="lv-joy-reset">Quitar todas</button>
      <button type="button" class="btn" id="lv-joy-close">Cerrar</button>
    </div>`, true);
  $("#lv-joy-close").onclick = () => { lvJoy.learning = null; closeModal(); };
  $("#lv-joy-reset").onclick = () => {
    lvJoyConfig(pad.id).buttons = {};
    lvJoySave();
    lvJoy.learning = null;
    lvJoyRenderButtons();
  };
  $("#lv-joy-table").addEventListener("click", (e) => {
    const b = e.target.closest("[data-learn],[data-unset]");
    if (!b) return;
    const cfg = lvJoyConfig(pad.id);
    if (b.dataset.unset) {
      for (const [i, k] of Object.entries(cfg.buttons)) if (k === b.dataset.unset) delete cfg.buttons[i];
      lvJoySave();
      lvJoy.learning = null;
    } else {
      lvJoy.learning = lvJoy.learning === b.dataset.learn ? null : b.dataset.learn;
    }
    lvJoyRenderButtons();
  });
  lvJoyLoop();
  lvJoyRenderButtons();
}

function lvJoyRenderButtons() {
  const table = $("#lv-joy-table");
  const pad = lvJoyPad();
  if (!table || !pad) return;
  const cfg = lvJoyConfig(pad.id);
  const buttonOf = (key) => Object.entries(cfg.buttons).find(([, k]) => k === key)?.[0];
  table.innerHTML = `<thead><tr><th>Acción</th><th>Botón</th><th></th></tr></thead><tbody>${LV_JOY_ACTIONS.map((a) => {
    const n = buttonOf(a.key);
    const learning = lvJoy.learning === a.key;
    return `<tr><td>${esc(a.label)}</td>
      <td>${learning ? "<b>Apriete el botón…</b>" : n != null ? `Botón ${Number(n) + 1}` : `<span class="muted">—</span>`}</td>
      <td style="white-space:nowrap;text-align:right">
        <button type="button" class="btn ghost small" data-learn="${a.key}">${learning ? "Cancelar" : "Asignar"}</button>
        ${n != null ? `<button type="button" class="btn ghost small" data-unset="${a.key}">Quitar</button>` : ""}</td></tr>`;
  }).join("")}</tbody>`;
}
