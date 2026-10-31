// Cuadro vigía en la vista en vivo del navegador (#/live): un cuadro que cambia
// solo a la cámara del último evento que le interesa, con una franja arriba que
// dice qué pasó, dónde y hace cuánto. Es el mismo vigía del cliente de escritorio
// (CellPanels.Watch.cs): mismas opciones en la clave «card-watch?…» de los
// tableros, mismas reglas (tiempo mínimo en pantalla, no cambiar mientras el
// operador usa el cuadro, varios vigías iguales se reparten los eventos).
//
// Vive sobre live.js: el vigía es un dato del cuadro (LvCell.watch) y abre la
// cámara del evento con lvOpenInCell(…, keepWatch = true). Los eventos llegan por
// el hub (mensaje LiveEvent), ya recortados por el servidor a lo que el usuario ve.
"use strict";

const LVW_CATEGORIES = [
  ["alert", "Avisos de automatizaciones"],
  ["alarm", "Alarmas de paneles"],
  ["alarm-system", "Armado, desarmado y fallas de paneles"],
  ["door-alarm", "Alarmas de puertas"],
  ["access-denied", "Accesos denegados"],
  ["access-granted", "Accesos concedidos"],
  ["video-analytics", "Analíticas de video (cruce de línea, intrusión…)"],
  ["video-motion", "Detección de movimiento"],
  ["video-health", "Pérdida de video y sabotaje"],
  ["plate-flagged", "Patentes no autorizadas o fuera del padrón"],
  ["plate", "Patentes autorizadas"],
  ["fence", "Alarmas de cerco eléctrico"],
  ["io-input", "Entradas de módulos de E/S"],
  ["intercom", "Llamadas de citofonía"],
];
const LVW_DEFAULT_CATS = ["alert", "alarm", "door-alarm", "access-denied", "video-analytics", "video-health",
  "plate-flagged", "fence", "io-input", "intercom"];
const LVW_SEVERITY = { Info: 0, Warning: 1, Critical: 2 };
const LVW_DEFAULTS = { sev: 1, hold: 20, pin: 5 };

const lvw = { timer: null, locations: null, locationsAt: 0, replayLead: 5 };

/** Bitácora del vigía (fijar/soltar, «Listo» en un crítico): a nombre del cuadro, sin cámara. */
function lvwAudit(action, title, detail) {
  Api.post("/api/audit/client-event", { action, deviceName: title, detail: `${detail} (panel web)` })
    .catch(() => { /* la auditoría nunca bloquea al operador */ });
}

const lvwSeverityOf = (e) => (typeof e.severity === "number" ? e.severity : LVW_SEVERITY[e.severity] ?? 0);

// ---------------------------------------------------------------------------
// Opciones (la clave «card-watch?cat=…&loc=…&sev=…&hold=…&idle=…&main=1&pin=…»)
// ---------------------------------------------------------------------------
function lvwParse(ref) {
  const q = new URLSearchParams(String(ref || "").split("?")[1] || "");
  const ids = (s) => (s || "").split(",").map(Number).filter((n) => n > 0);
  const num = (k, def, min, max) => { const n = parseInt(q.get(k) ?? "", 10); return Number.isFinite(n) ? Math.min(max, Math.max(min, n)) : def; };
  return {
    cats: q.has("cat") ? q.get("cat").split(",").filter(Boolean) : [...LVW_DEFAULT_CATS],
    locs: ids(q.get("loc")),
    sev: num("sev", LVW_DEFAULTS.sev, 0, 2),
    hold: num("hold", LVW_DEFAULTS.hold, 5, 120),
    idle: ids(q.get("idle"))[0] ?? null,
    main: q.get("main") === "1",
    pin: num("pin", LVW_DEFAULTS.pin, 0, 30),
  };
}

function lvwRef(o) {
  const parts = [];
  const sameCats = o.cats.length === LVW_DEFAULT_CATS.length && LVW_DEFAULT_CATS.every((c) => o.cats.includes(c));
  if (!sameCats) parts.push("cat=" + LVW_CATEGORIES.map(([k]) => k).filter((k) => o.cats.includes(k)).join(","));
  if (o.locs.length) parts.push("loc=" + [...o.locs].sort((a, b) => a - b).join(","));
  if (o.sev !== LVW_DEFAULTS.sev) parts.push(`sev=${o.sev}`);
  if (o.hold !== LVW_DEFAULTS.hold) parts.push(`hold=${o.hold}`);
  if (o.idle) parts.push(`idle=${o.idle}`);
  if (o.main) parts.push("main=1");
  if (o.pin !== LVW_DEFAULTS.pin) parts.push(`pin=${o.pin}`);
  return parts.length ? `card-watch?${parts.join("&")}` : "card-watch";
}

const lvwIsWatchItem = (item) => item?.kind === "card" && String(item.ref || "").split("?")[0] === "card-watch";

function lvwAccepts(o, e) {
  return o.cats.includes(e.category) && lvwSeverityOf(e) >= o.sev &&
    (!o.locs.length || (e.locationIds || []).some((id) => o.locs.includes(id)));
}

async function lvwLocations() {
  if (lvw.locations && Date.now() - lvw.locationsAt < 60000) return lvw.locations;
  let list = [];
  try { list = await Api.get("/api/locations"); } catch { /* sin ubicaciones */ }
  const byId = new Map(list.map((l) => [l.id, l]));
  const path = (l) => { const names = []; for (let at = l; at && names.length < 20; at = byId.get(at.parentId)) names.unshift(at.name); return names.join(" › "); };
  lvw.locations = list.map((l) => ({ id: l.id, path: path(l) })).sort((a, b) => a.path.localeCompare(b.path, "es"));
  lvw.locationsAt = Date.now();
  return lvw.locations;
}

function lvwTitle(o) {
  if (!o.locs.length) return "Vigía · todo lo que ve";
  const names = (lvw.locations || []).filter((l) => o.locs.includes(l.id)).map((l) => l.path.split(" › ").pop());
  return names.length ? `Vigía · ${names.join(", ")}` : "Vigía";
}

// ---------------------------------------------------------------------------
// Poner, quitar y mover el vigía de un cuadro
// ---------------------------------------------------------------------------
/** Pone un vigía (con las opciones de la clave) en el cuadro; lo que tenía se cierra. */
function lvwPut(cell, ref) {
  if (!cell) return;
  if (cell.watch) lvwRemove(cell);
  if (cell.feed) { if (lv.promoted === cell.feed) lvForgetPromotion(); cell.feed.dispose(); cell.feed = null; }
  const options = lvwParse(ref);
  const w = { options, ref: lvwRef(options), title: lvwTitle(options), current: null, queue: [], pinned: false,
    pinnedUntil: 0, shownAt: 0, lastTouch: 0, cams: [], camIdx: 0, el: document.createElement("div") };
  w.el.className = "lv-watch";
  lvwGive(cell, w);
  lvwGoIdle(cell);
  if (options.locs.length && !lvw.locations)
    lvwLocations().then(() => { if (cell.watch === w) { w.title = lvwTitle(options); lvRenderCell(cell); } });
  lvwEnsureTimer();
  lvScheduleLastSave();
}

/** Quita el vigía (la cámara que mostraba queda, salvo que se cierre el cuadro). */
function lvwRemove(cell) {
  const w = lvwTake(cell);
  if (w) lvRenderCell(cell);
}

/** Saca el vigía del cuadro sin perderlo (para moverlo con su contenido). */
function lvwTake(cell) {
  const w = cell?.watch;
  if (!w) return null;
  w.el.remove();
  cell.watch = null;
  cell.el.classList.remove("watching");
  return w;
}

function lvwGive(cell, w) {
  if (!cell || !w) return;
  cell.watch = w;
  cell.el.insertBefore(w.el, cell.body);
  cell.el.classList.add("watching");
  lvRenderCell(cell);
}

// ---------------------------------------------------------------------------
// Llegada de eventos y reparto entre vigías
// ---------------------------------------------------------------------------
function lvwOnEvent(e) {
  if (!lv.active || !e) return;
  const watching = lv.cells.filter((c) => c.watch && lvwAccepts(c.watch.options, e));
  // Un repetido: se actualiza donde ya esté (en pantalla o en cola).
  let refreshed = false;
  for (const c of watching) {
    const w = c.watch;
    if (w.current?.id === e.id) { w.current = e; w.shownAt = Date.now(); refreshed = true; lvwRender(c); }
    const i = w.queue.findIndex((q) => q.id === e.id);
    if (i >= 0) { w.queue[i] = e; refreshed = true; lvwRender(c); }
  }
  if (refreshed || e.repeats > 0) return;
  const groups = new Map();
  for (const c of watching) {
    const list = groups.get(c.watch.ref) || [];
    list.push(c);
    groups.set(c.watch.ref, list);
  }
  for (const group of groups.values()) {
    const target = group.find((c) => lvwIsFree(c))
      ?? group.filter((c) => lvwCanTake(c, e)).sort((a, b) =>
        lvwSeverityOf(a.watch.current) - lvwSeverityOf(b.watch.current) || a.watch.shownAt - b.watch.shownAt)[0];
    if (target) lvwShow(target, e);
    else lvwEnqueue(group.slice().sort((a, b) => a.watch.queue.length - b.watch.queue.length || a.index - b.index)[0], e);
  }
}

function lvwBusy(cell) {
  return Date.now() - cell.watch.lastTouch < 5000 || (cell.feed && cell.feed.zoom.s > 1.001);
}

function lvwIsFree(cell) { return !cell.watch.current && !cell.watch.pinned && !lvwBusy(cell); }

function lvwCanTake(cell, e) {
  const w = cell.watch;
  return !w.pinned && !lvwBusy(cell) && w.current &&
    (lvwSeverityOf(e) > lvwSeverityOf(w.current) || Date.now() - w.shownAt >= w.options.hold * 1000);
}

function lvwEnqueue(cell, e) {
  const q = cell.watch.queue.filter((x) => x.id !== e.id);
  let at = 0;
  while (at < q.length && lvwSeverityOf(q[at]) >= lvwSeverityOf(e)) at++;
  q.splice(at, 0, e);
  cell.watch.queue = q;
  lvwRender(cell);
}

function lvwShow(cell, e) {
  const w = cell.watch;
  // Uno más grave que llega antes de que el actual cumpla su tiempo lo desplaza: el
  // desplazado vuelve a la cola (no se pierde). «Listo» y «Siguiente» lo dan por visto.
  if (w.current && w.current.id !== e.id && Date.now() - w.shownAt < w.options.hold * 1000) lvwEnqueue(cell, w.current);
  w.current = e;
  w.shownAt = Date.now();
  w.queue = w.queue.filter((q) => q.id !== e.id);
  w.cams = (e.channelIds || []).map((id) => lv.channels.get(id)).filter(Boolean);
  w.camIdx = 0;
  lvwOpenCamera(cell);
  lvRenderCell(cell);
}

function lvwOpenCamera(cell) {
  const w = cell.watch;
  const ch = w.cams[w.camIdx];
  if (!ch) { lvwStopVideo(cell); return; }
  if (cell.feed?.channel.id === ch.id) return;
  lvOpenInCell(ch, cell, w.options.main ? "Main" : "Sub", true);
}

function lvwStopVideo(cell) {
  if (!cell.feed) return;
  if (lv.promoted === cell.feed) lvForgetPromotion();
  cell.feed.dispose();
  cell.feed = null;
  lvRenderCell(cell);
  lvRefreshTreeLive();
}

function lvwGoIdle(cell) {
  const w = cell.watch;
  w.current = null;
  w.cams = [];
  const idle = w.options.idle ? lv.channels.get(w.options.idle) : null;
  if (idle) { if (cell.feed?.channel.id !== idle.id) lvOpenInCell(idle, cell, w.options.main ? "Main" : "Sub", true); }
  else lvwStopVideo(cell);
  lvRenderCell(cell);
}

function lvwNextFromGroup(cell) {
  const donor = lv.cells.filter((c) => c !== cell && c.watch && c.watch.ref === cell.watch.ref && c.watch.queue.length)
    .sort((a, b) => lvwSeverityOf(b.watch.queue[0]) - lvwSeverityOf(a.watch.queue[0]))[0];
  if (!donor) return null;
  const next = donor.watch.queue.shift();
  lvwRender(donor);
  return next;
}

// ---------------------------------------------------------------------------
// Reloj: «hace N s», «Fijar» que se suelta solo y el paso al siguiente de la cola
// ---------------------------------------------------------------------------
function lvwEnsureTimer() {
  if (lvw.timer) return;
  lvw.timer = setInterval(lvwTick, 1000);
}

function lvwTick() {
  if (!lv.active) return;
  for (const cell of lv.cells) {
    const w = cell.watch;
    if (!w) continue;
    if (w.pinned && w.pinnedUntil && Date.now() >= w.pinnedUntil) {
      w.pinned = false;
      w.pinnedUntil = 0;
      lvwAudit("live-watch-pinned", w.title, `soltado solo; ${w.queue.length} evento(s) en cola`);
    }
    if (!w.pinned && w.queue.length && !lvwBusy(cell) && (!w.current || Date.now() - w.shownAt >= w.options.hold * 1000))
      lvwShow(cell, w.queue.shift());
    else lvwRender(cell);
  }
}

function lvwAgo(e) {
  const s = (Date.now() - new Date(e.at).getTime()) / 1000;
  if (s < 5) return "ahora";
  if (s < 60) return `hace ${Math.floor(s)} s`;
  if (s < 3600) return `hace ${Math.floor(s / 60)} min`;
  return `a las ${new Date(e.at).toLocaleTimeString("es-CL", { hour: "2-digit", minute: "2-digit" })}`;
}

// ---------------------------------------------------------------------------
// La franja (la llama lvRenderCell)
// ---------------------------------------------------------------------------
function lvwRender(cell) {
  const w = cell.watch;
  if (!w) return;
  const e = w.current;
  const level = !e ? "idle" : ["ok", "warn", "alarm"][lvwSeverityOf(e)];
  w.el.className = `lv-watch ${level}`;
  const where = e ? [e.resourceName, e.locationPath, e.detail].filter(Boolean).join(" · ") : "";
  const pinLeft = w.pinned && w.pinnedUntil ? Math.max(0, Math.round((w.pinnedUntil - Date.now()) / 1000)) : 0;
  const pinText = w.pinned ? (w.pinnedUntil ? `Fijado · ${Math.floor(pinLeft / 60)}:${String(pinLeft % 60).padStart(2, "0")}` : "Fijado") : "";
  w.el.innerHTML = `
    <div class="lv-watch-text">
      ${e ? `<div class="lv-watch-title">${esc(e.title)}${e.repeats > 0 ? ` <b>×${e.repeats + 1}</b>` : ""}</div>
             <div class="lv-watch-where"><b>${esc(lvwAgo(e))}</b> ${esc(where)}</div>`
          : `<div class="lv-watch-idle">${lvSvg(LV_ICO.views, 13)} Esperando eventos${w.options.locs.length ? " de estas ubicaciones" : ""}. Cuando pase algo, este cuadro cambia solo a su cámara.</div>`}
    </div>
    <div class="lv-watch-acts">
      ${pinText ? `<span class="lv-watch-pin muted">${esc(pinText)}</span>` : ""}
      ${w.queue.length ? `<span class="lv-watch-queue" title="Eventos esperando su turno en este vigía">+${w.queue.length} en cola</span>` : ""}
      ${w.cams.length > 1 ? `<button type="button" data-w="prev" title="Cámara anterior de este evento">‹</button><span class="muted">${w.camIdx + 1}/${w.cams.length}</span><button type="button" data-w="next-cam" title="Cámara siguiente de este evento">›</button>` : ""}
      ${e && w.cams.length && lvwCanReplay() ? `<button type="button" data-w="replay" title="Abre en otra pestaña la grabación de esta cámara, ${lvw.replayLead} s antes del evento (el vigía sigue vigilando)">Repetir</button>` : ""}
      ${e?.alertId ? `<button type="button" data-w="alert" title="Ver el aviso y confirmarlo">Ver aviso</button>` : ""}
      ${w.queue.length ? `<button type="button" data-w="next" title="Muestra ya el próximo evento de la cola">Siguiente</button>` : ""}
      ${e ? `<button type="button" data-w="done" title="Ya lo vi: pasa al siguiente o vuelve a esperar">Listo</button>` : ""}
      <button type="button" data-w="pin" class="${w.pinned ? "on" : ""}" title="${w.pinned ? "Fijado: no cambia con los eventos nuevos (quedan en cola). Clic para soltarlo" : "Fijar: deja de cambiar con los eventos nuevos (quedan en cola)"}">📌</button>
      <button type="button" data-w="gear" title="Qué vigila este cuadro">⚙</button>
      <button type="button" data-w="remove" title="Quitar el vigía de este cuadro">✕</button>
    </div>
    ${e && !w.cams.length ? `<div class="lv-watch-nocam muted">Este evento no tiene cámaras que usted pueda ver.</div>` : ""}`;
}

/** Botones de la franja (los engancha lvwInit sobre la grilla). */
function lvwOnClick(ev) {
  const b = ev.target.closest(".lv-watch [data-w]");
  if (!b) return;
  const cell = lvCellFromEvent(ev);
  const w = cell?.watch;
  if (!w) return;
  ev.stopPropagation();
  w.lastTouch = Date.now();
  switch (b.dataset.w) {
    case "done": {
      if (w.current && lvwSeverityOf(w.current) === 2)
        lvwAudit("live-watch-done", w.title, `«${w.current.title}» dado por visto`);
      w.lastTouch = 0;
      w.current = null;
      const next = w.queue.shift() ?? lvwNextFromGroup(cell);
      if (next) lvwShow(cell, next); else lvwGoIdle(cell);
      break;
    }
    case "next": { w.lastTouch = 0; const next = w.queue.shift(); if (next) { w.current = null; lvwShow(cell, next); } break; }
    case "pin":
      w.pinned = !w.pinned;
      w.pinnedUntil = w.pinned && w.options.pin > 0 ? Date.now() + w.options.pin * 60000 : 0;
      lvwAudit("live-watch-pinned", w.title, w.pinned
        ? `fijado${w.current ? ` mostrando «${w.current.title}»` : " en espera"}`
        : `soltado a mano; ${w.queue.length} evento(s) en cola`);
      lvwRender(cell);
      break;
    case "prev": w.camIdx = (w.camIdx - 1 + w.cams.length) % w.cams.length; lvwOpenCamera(cell); lvwRender(cell); break;
    case "next-cam": w.camIdx = (w.camIdx + 1) % w.cams.length; lvwOpenCamera(cell); lvwRender(cell); break;
    case "alert": lvwShowAlert(w.current?.alertId); break;
    case "replay": lvwReplay(cell); break;
    case "gear": lvwSettings(cell); break;
    case "remove":
      lvwRemove(cell);
      lvwStopVideo(cell);
      lvScheduleLastSave();
      break;
  }
}

function lvwCanReplay() { return Perms.can("playback.view") && Perms.licensed("module_playback"); }

/**
 * «Repetir»: la grabación de la cámara que se está mostrando, unos segundos antes
 * del evento (los «antes» de la repetición del evento del servidor). Va a otra
 * pestaña para que este cuadro siga vigilando.
 */
function lvwReplay(cell) {
  const w = cell.watch;
  const ch = w.cams[w.camIdx];
  if (!w.current || !ch) return;
  const at = new Date(w.current.at).getTime() - lvw.replayLead * 1000;
  window.open(`#/playback?ch=${ch.id}&at=${at}`, "_blank", "noopener");
}

// ---------------------------------------------------------------------------
// «Ver aviso»: lo esencial del aviso y el acuse, sin salir de la vista en vivo
// ---------------------------------------------------------------------------
async function lvwShowAlert(id) {
  if (!id) return;
  let a;
  try { a = await Api.get(`/api/workflows/alerts/${id}`); } catch (err) { toast(err.error, true); return; }
  const pending = a.requiresAck && !a.acknowledgedAt;
  openModal(`
    <h3>${esc(a.title)}</h3>
    <p class="muted">${esc(a.workflowName)} · ${new Date(a.raisedAt).toLocaleString("es-CL")}</p>
    ${a.locationPath ? `<p><b>Ubicación:</b> ${esc(a.locationPath)}</p>` : ""}
    ${a.instructions ? `<div class="notice"><b>Consignas:</b> ${esc(a.instructions)}</div>` : ""}
    <p>${esc(a.message)}</p>
    <p class="muted">${a.acknowledgedAt ? `Confirmada por ${esc(a.acknowledgedBy)} el ${new Date(a.acknowledgedAt).toLocaleString("es-CL")}.`
      : a.requiresAck ? "Pendiente: nadie se ha dado por enterado todavía." : "Informativa: no pide confirmación."}</p>
    <p><a href="#/event-center" target="_blank" rel="noopener">Abrir en el Centro de eventos (en otra pestaña) ↗</a></p>
    <div class="modal-actions">
      <button type="button" class="btn ghost" id="lvw-alert-close">Cerrar</button>
      ${pending ? `<button type="button" class="btn primary" id="lvw-alert-ack">Enterado</button>` : ""}
    </div>`);
  $("#lvw-alert-close").addEventListener("click", closeModal);
  $("#lvw-alert-ack")?.addEventListener("click", async (ev) => {
    ev.target.disabled = true;
    try {
      const done = await Api.post(`/api/workflows/alerts/${id}/ack`);
      toast(done.acknowledgedBy === Api.username ? "Alerta confirmada; quedó registrado a su nombre." : `La alerta ya la había confirmado ${done.acknowledgedBy}.`);
      closeModal();
    } catch (err) { toast(err.error, true); ev.target.disabled = false; }
  });
}

// ---------------------------------------------------------------------------
// Engranaje: qué vigila, dónde, gravedad, tiempo mínimo, reposo, stream y «Fijar»
// ---------------------------------------------------------------------------
async function lvwSettings(cell) {
  const w = cell.watch;
  const o = w.options;
  const locations = await lvwLocations();
  const cams = [...lv.channels.values()].sort((a, b) => `${a.device?.name} ${a.name}`.localeCompare(`${b.device?.name} ${b.name}`, "es"));
  const opt = (v, label, sel) => `<option value="${v}" ${sel ? "selected" : ""}>${esc(label)}</option>`;
  openModal(`
    <h3>Cuadro vigía</h3>
    <p class="muted">El cuadro cambia solo a la cámara del último evento que cumpla lo de abajo. No reemplaza a los avisos: solo muestra.</p>
    <div class="lvw-grid">
      <div>
        <div class="lvw-label">QUÉ VIGILA</div>
        <div class="lvw-list">${LVW_CATEGORIES.map(([k, label]) =>
          `<label class="lvw-check"><input type="checkbox" data-cat="${k}" ${o.cats.includes(k) ? "checked" : ""}> ${esc(label)}</label>`).join("")}</div>
      </div>
      <div>
        <div class="lvw-label">DÓNDE</div>
        <label class="lvw-check"><input type="radio" name="lvw-places" value="all" ${o.locs.length ? "" : "checked"}> Todo lo que veo</label>
        <label class="lvw-check"><input type="radio" name="lvw-places" value="some" ${o.locs.length ? "checked" : ""} ${locations.length ? "" : "disabled"}> Solo estas ubicaciones (y lo que contienen)</label>
        <div class="lvw-list">${locations.length ? locations.map((l) =>
          `<label class="lvw-check"><input type="checkbox" data-loc="${l.id}" ${o.locs.includes(l.id) ? "checked" : ""}> ${esc(l.path)}</label>`).join("")
          : `<span class="muted">No hay ubicaciones creadas: el vigía mira todo lo que usted ve.</span>`}</div>
      </div>
    </div>
    <div class="lvw-grid">
      <label>Gravedad mínima<select id="lvw-sev">${opt(0, "Informativa (todo)", o.sev === 0)}${opt(1, "Advertencia y crítica", o.sev === 1)}${opt(2, "Solo crítica", o.sev === 2)}</select></label>
      <label>Tiempo mínimo en pantalla<select id="lvw-hold">${[5, 10, 15, 20, 30, 45, 60, 90, 120].map((h) => opt(h, `${h} s`, o.hold === h)).join("")}</select></label>
      <label>En espera muestra<select id="lvw-idle">${opt(0, "Nada (queda esperando)", !o.idle)}${cams.map((c) => opt(c.id, `${c.device?.name ?? ""} · ${c.name}`, o.idle === c.id)).join("")}</select></label>
      <label>«Fijar» se suelta solo<select id="lvw-pin">${[1, 2, 5, 10, 15, 30, 0].map((p) => opt(p, p ? `A los ${p} min` : "Nunca", o.pin === p)).join("")}</select></label>
    </div>
    <label class="lvw-check"><input type="checkbox" id="lvw-main" ${o.main ? "checked" : ""}> Stream principal (más calidad; el secundario cambia de cámara más rápido)</label>
    <p class="error" id="lvw-error"></p>
    <div class="modal-actions">
      <button type="button" class="btn ghost" id="lvw-cancel">Cancelar</button>
      <button type="button" class="btn primary" id="lvw-apply">Aplicar</button>
    </div>`, true);
  $("#lvw-cancel").addEventListener("click", closeModal);
  $("#lvw-apply").addEventListener("click", () => {
    const cats = $$("#modal [data-cat]").filter((x) => x.checked).map((x) => x.dataset.cat);
    const some = $("#modal input[name=lvw-places][value=some]").checked;
    const locs = some ? $$("#modal [data-loc]").filter((x) => x.checked).map((x) => Number(x.dataset.loc)) : [];
    if (!cats.length) { $("#lvw-error").textContent = "Marque al menos un tipo de evento."; return; }
    if (some && !locs.length) { $("#lvw-error").textContent = "Marque al menos una ubicación, o elija «Todo lo que veo»."; return; }
    const next = { cats, locs, sev: Number($("#lvw-sev").value), hold: Number($("#lvw-hold").value),
      idle: Number($("#lvw-idle").value) || null, main: $("#lvw-main").checked, pin: Number($("#lvw-pin").value) };
    closeModal();
    if (cell.watch !== w) return;
    w.options = next;
    w.ref = lvwRef(next);
    w.title = lvwTitle(next);
    if (!w.current) lvwGoIdle(cell);
    else if (!lvwAccepts(next, w.current)) { const n = w.queue.shift(); if (n) lvwShow(cell, n); else lvwGoIdle(cell); }
    lvRenderCell(cell);
    lvScheduleLastSave();
  });
}

// ---------------------------------------------------------------------------
// Alta y baja con la página
// ---------------------------------------------------------------------------
function lvwInit() {
  VmsHub.on("LiveEvent", lvwOnEvent);
  $("#lv-grid")?.addEventListener("click", lvwOnClick, true);
  lvwEnsureTimer();
  if (lvwCanReplay())
    Api.get("/api/workflows/clip-settings")
      .then((c) => { lvw.replayLead = c.preSeconds; })
      .catch(() => { /* sin permiso de ver automatizaciones o servidor anterior: 5 s */ });
}

function lvwLeave() {
  VmsHub.off("LiveEvent", lvwOnEvent);
  clearInterval(lvw.timer);
  lvw.timer = null;
}

/** «Vigía» de la barra o «Vigilar esta ubicación»: en el cuadro seleccionado, o en el primero libre. */
function lvwAddToSelected(locationId) {
  const cell = lv.selected ?? lv.cells.find((c) => c.isEmpty) ?? lv.cells[0];
  if (!cell) return;
  lvwPut(cell, locationId ? `card-watch?loc=${locationId}` : "card-watch");
  lvSelectCell(cell);
  lvStatus(`El cuadro ${cell.index} es un Cuadro vigía: cambia solo a la cámara del último evento.`);
}
