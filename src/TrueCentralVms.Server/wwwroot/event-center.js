// CLR TrueCentral VMS — Aplicaciones → Centro de eventos.
// Réplica en el panel del "Centro de eventos" del cliente (Views\EventCenterView.xaml)
// junto con su ventana de alarma (Views\AlertWindow.xaml): a la izquierda las
// alertas que emitieron las automatizaciones con su estado de acuse; a la
// derecha la ficha de la elegida (origen, descripción, acuse de recibo, fotos
// y los pasos que ejecutó el sistema).
//
// El panel no escucha el hub: alertas nuevas y confirmaciones se traen por
// sondeo. El video en vivo de las cámaras vinculadas queda en el cliente (el
// panel web no reproduce streaming). Se carga después de workflows.js (usa
// wfDate / wfAgo).
"use strict";

let eventCenterTimer = null;
const EVC_POLL_MS = 5000;
const EVC_PAGE_SIZE = 50;
const evcState = { onlyPending: false, page: 1 };
let evcAlerts = [];
let evcSelectedId = null;
let evcDetailKey = "";
let evcKnownIds = null;       // ids ya vistos, para detectar alertas nuevas (sonido)
let evcPhotoIndex = 0;
let evcTab = "photos";
let evcListKey = "";          // última respuesta dibujada (el sondeo no redibuja si no cambió)

const EVC_SOUND_KEY = "tcvms.evc.sound";
const EVC_SYSTEM_SOUND = "sistema";

function evcSoundEnabled() {
  try { return localStorage.getItem(EVC_SOUND_KEY) === "1"; } catch { return false; }
}

function evcFileUrl(path) {
  return `/api/workflows/files/${path.split("/").map(encodeURIComponent).join("/")}?access_token=${encodeURIComponent(Api.token || "")}`;
}

function evcPhotos(a) {
  return a.imagePaths?.length ? a.imagePaths : a.imagePath ? [a.imagePath] : [];
}

/** Estado del acuse: [texto, clase de la etiqueta]. */
function evcAckState(a) {
  if (a.acknowledgedAt) return ["Confirmada", "on"];
  if (a.requiresAck) return ["PENDIENTE", "off"];
  return ["Informativa", "operator"];
}

function evcAckLine(a) {
  if (a.acknowledgedAt) {
    return `${a.acknowledgedBy} · ${wfDate(a.acknowledgedAt)} · a los ${a.responseSeconds ?? "?"} s · ${a.acknowledgedFrom === "client" ? "cliente" : "panel web"}`;
  }
  return a.requiresAck ? "Nadie se ha dado por enterado" : "No requiere confirmación";
}

const EVC_SEVERITY = { Critical: "Crítica", Warning: "Advertencia", Info: "Informativa" };

// ---------------------------------------------------------------------------
// Alarma sonora (réplica de AlertSoundPlayer): suena en ESTE navegador al
// llegar una alerta nueva que la tenga configurada, si el operador lo activó.
// soundRepeat 0 = repetir hasta que alguien la confirme; si no, 1..5 veces.
// ---------------------------------------------------------------------------
let evcSound = null;   // { alertId, stop() }

function evcStopSound() {
  if (!evcSound) return;
  const s = evcSound;
  evcSound = null;
  s.stop();
  evcUpdateMuteButton();
}

function evcPlaySound(alert) {
  evcStopSound();
  if (!alert.sound) return;
  const loop = alert.soundRepeat === 0 && alert.requiresAck;
  let remaining = loop ? Infinity : Math.min(5, Math.max(1, alert.soundRepeat || 1));
  let stopped = false;
  let audio = null;
  let timer = null;

  const beep = () => new Promise((resolve) => {
    try {
      const ctx = new (window.AudioContext || window.webkitAudioContext)();
      const osc = ctx.createOscillator();
      osc.frequency.value = 880;
      osc.connect(ctx.destination);
      osc.start();
      setTimeout(() => { osc.stop(); ctx.close(); resolve(); }, 350);
    } catch { resolve(); }
  });
  const playOnce = () => alert.sound === EVC_SYSTEM_SOUND ? beep() : new Promise((resolve) => {
    audio = new Audio(`/api/workflows/audio/${encodeURIComponent(alert.sound)}/file?access_token=${encodeURIComponent(Api.token || "")}`);
    audio.addEventListener("ended", resolve);
    audio.addEventListener("error", resolve);
    audio.play().catch(resolve);
  });
  const next = async () => {
    if (stopped || remaining-- <= 0) { if (evcSound?.alertId === alert.id) { evcSound = null; evcUpdateMuteButton(); } return; }
    await playOnce();
    if (!stopped) timer = setTimeout(next, 600);
  };

  evcSound = {
    alertId: alert.id,
    stop() { stopped = true; clearTimeout(timer); try { audio?.pause(); } catch { /* ya terminó */ } },
  };
  evcUpdateMuteButton();
  next();
}

function evcUpdateMuteButton() {
  const b = $("#evc-mute");
  if (!b) return;
  b.disabled = !evcSound;
  b.textContent = evcSound ? "Silenciar" : "Silenciada";
}

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
async function renderEventCenter() {
  $("#page-title").textContent = "Centro de eventos";
  evcDetailKey = "";
  evcListKey = "";
  evcKnownIds = null;

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3 id="evc-summary">Centro de eventos</h3>
      <div class="evc-toolbar">
        <label class="evc-check"><input type="checkbox" id="evc-only" ${evcState.onlyPending ? "checked" : ""}> Solo sin confirmar</label>
        <label class="evc-check" title="Reproduce en este navegador el sonido configurado en cada alerta nueva">
          <input type="checkbox" id="evc-sound" ${evcSoundEnabled() ? "checked" : ""}> Sonar alertas nuevas</label>
        <button class="btn ghost" id="evc-refresh">Actualizar</button>
      </div>
    </div>
    <div class="evc-layout">
      <section class="evc-list-panel">
        <div class="evc-list" id="evc-list"><div class="info-box">Cargando alertas…</div></div>
        <div class="evc-pager" id="evc-pager"></div>
      </section>
      <section class="evc-detail" id="evc-detail"></section>
    </div>`;

  $("#evc-only").addEventListener("change", (e) => {
    evcState.onlyPending = e.target.checked;
    evcState.page = 1;
    loadEventCenter();
  });
  $("#evc-sound").addEventListener("change", (e) => {
    try { localStorage.setItem(EVC_SOUND_KEY, e.target.checked ? "1" : "0"); } catch { /* modo privado */ }
    if (!e.target.checked) evcStopSound();
  });
  $("#evc-refresh").addEventListener("click", () => { evcListKey = ""; loadEventCenter(); });

  await loadEventCenter();

  clearInterval(eventCenterTimer);
  eventCenterTimer = setInterval(() => loadEventCenter(), EVC_POLL_MS);
}

async function loadEventCenter() {
  const list = $("#evc-list");
  if (!list) return;
  let data;
  try {
    const skip = (evcState.page - 1) * EVC_PAGE_SIZE;
    data = await Api.get(`/api/workflows/alerts?take=${EVC_PAGE_SIZE}&skip=${skip}${evcState.onlyPending ? "&pending=true" : ""}`);
  } catch (err) {
    list.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }
  if (!$("#evc-list")) return; // se cambió de página mientras llegaba

  // Alertas nuevas desde la última consulta: se seleccionan y, si corresponde, suenan.
  const fresh = evcKnownIds ? data.items.filter((a) => !evcKnownIds.has(a.id)) : [];
  evcKnownIds ??= new Set();
  data.items.forEach((a) => evcKnownIds.add(a.id));
  if (fresh.length && evcState.page === 1) {
    evcSelectedId = fresh[0].id;
    const loud = fresh.find((a) => a.sound);
    if (loud && evcSoundEnabled()) evcPlaySound(loud);
    toast(`Nueva alerta: ${fresh[0].title}`, fresh[0].severity !== "Info");
  }
  // Si otro puesto confirmó la alerta que está sonando, se calla.
  if (evcSound) {
    const ringing = data.items.find((a) => a.id === evcSound.alertId);
    if (ringing?.acknowledgedAt) evcStopSound();
  }

  const listKey = JSON.stringify(data) + "|" + evcState.onlyPending + "|" + evcState.page;
  if (listKey === evcListKey && !fresh.length) return;
  evcListKey = listKey;

  evcAlerts = data.items;
  $("#evc-summary").innerHTML = data.total === 0 ? "Centro de eventos"
    : `${data.total} alerta(s) · ${data.pending > 0 ? `<span style="color:var(--danger)">${data.pending} sin confirmar</span>` : "todas confirmadas"}`;

  if (!evcAlerts.length) {
    list.innerHTML = `<div class="evc-empty muted">${evcState.onlyPending ? "No hay alertas pendientes de confirmar." : "Todavía no se ha emitido ninguna alerta."}</div>`;
    $("#evc-pager").innerHTML = "";
    evcSelectedId = null;
    renderEvcDetail(null);
    return;
  }
  if (!evcAlerts.some((a) => a.id === evcSelectedId)) evcSelectedId = evcAlerts[0].id;

  const scroll = list.scrollTop;
  list.innerHTML = evcAlerts.map((a) => {
    const [state, cls] = evcAckState(a);
    const critical = a.severity !== "Info";
    return `
      <div class="evc-card ${a.id === evcSelectedId ? "selected" : ""} ${a.pending ? "pending" : ""}" data-id="${a.id}">
        <span class="evc-icon ${critical ? "crit" : "info"}">${critical ? "!" : "i"}</span>
        <div class="evc-card-main">
          <div class="evc-row-top"><b>${esc(a.title)}</b><span class="tag ${cls}">${state}</span></div>
          ${a.message ? `<div class="evc-msg">${esc(a.message)}</div>` : ""}
          <div class="muted evc-small">${wfDate(a.raisedAt)} · ${esc(a.workflowName)}${a.triggerSummary ? ` · ${esc(a.triggerSummary)}` : ""}</div>
          <div class="muted evc-small">Acuse de recibo: ${esc(evcAckLine(a))}</div>
        </div>
        <div class="evc-card-actions">
          ${a.pending ? `<button class="btn danger evc-ack" data-id="${a.id}">Enterado</button>` : ""}
          ${evcPhotos(a).length ? `<span class="muted evc-small" title="Tiene fotos">📷 ${evcPhotos(a).length}</span>` : ""}
        </div>
      </div>`;
  }).join("");
  list.scrollTop = scroll;

  $$(".evc-card", list).forEach((card) => card.addEventListener("click", (e) => {
    if (e.target.closest(".evc-ack")) return;
    evcSelectedId = Number(card.dataset.id);
    $$(".evc-card", list).forEach((c) => c.classList.toggle("selected", c === card));
    renderEvcDetail(evcAlerts.find((a) => a.id === evcSelectedId));
  }));
  $$(".evc-ack", list).forEach((b) => b.addEventListener("click", () => evcAcknowledge(Number(b.dataset.id), b)));

  const pages = Math.max(1, Math.ceil(data.total / EVC_PAGE_SIZE));
  $("#evc-pager").innerHTML = pages <= 1 ? "" : `
    <span class="muted">Página ${evcState.page} de ${pages}</span>
    <div style="display:flex;gap:6px">
      <button class="btn ghost" id="evc-prev" ${evcState.page <= 1 ? "disabled" : ""}>« Anterior</button>
      <button class="btn ghost" id="evc-next" ${evcState.page >= pages ? "disabled" : ""}>Siguiente »</button>
    </div>`;
  $("#evc-prev")?.addEventListener("click", () => { evcState.page--; loadEventCenter(); });
  $("#evc-next")?.addEventListener("click", () => { evcState.page++; loadEventCenter(); });

  renderEvcDetail(evcAlerts.find((a) => a.id === evcSelectedId));
}

async function evcAcknowledge(id, button) {
  if (button) button.disabled = true;
  try {
    const alert = await Api.post(`/api/workflows/alerts/${id}/ack`);
    toast(alert.acknowledgedBy === Api.username
      ? "Alerta confirmada; quedó registrado a su nombre."
      : `La alerta ya la había confirmado ${alert.acknowledgedBy}.`);
    if (evcSound?.alertId === id) evcStopSound();
    evcListKey = "";
    await loadEventCenter();
  } catch (err) {
    toast(err.error, true);
    if (button) button.disabled = false;
  }
}

// ---------------------------------------------------------------------------
// Ficha de la alerta (réplica de AlertWindow)
// ---------------------------------------------------------------------------
function renderEvcDetail(a) {
  const box = $("#evc-detail");
  if (!box) return;
  const key = a ? JSON.stringify(a) : "";
  if (key === evcDetailKey && box.childElementCount) { evcUpdateMuteButton(); return; }
  const sameAlert = a && evcDetailKey && JSON.parse(evcDetailKey).id === a.id;
  evcDetailKey = key;
  if (!a) { box.innerHTML = `<div class="evc-empty muted">Seleccione una alerta para ver el detalle.</div>`; return; }
  if (!sameAlert) { evcPhotoIndex = 0; evcTab = "photos"; }

  const critical = a.severity !== "Info";
  const [ackState, ackCls] = evcAckState(a);
  const ackDetail = a.acknowledgedAt
    ? `Por ${esc(a.acknowledgedBy)} el ${wfDate(a.acknowledgedAt)} (${a.responseSeconds ?? wfAgo(a.raisedAt, a.acknowledgedAt)} s después de emitida), desde ${a.acknowledgedFrom === "client" ? "el cliente de escritorio" : "el panel web"}.`
    : a.requiresAck ? "Nadie se ha dado por enterado todavía." : "Este aviso no exige confirmación.";
  const sound = a.sound
    ? (a.sound === EVC_SYSTEM_SOUND ? "Pitido del sistema" : a.sound) + (a.soundRepeat > 1 ? ` (×${a.soundRepeat})` : a.soundRepeat === 0 ? " (hasta confirmar)" : "")
    : "Sin sonido";

  box.innerHTML = `
    <div class="evc-detail-head">
      <span class="evc-icon big ${critical ? "crit" : "info"}">${critical ? "!" : "i"}</span>
      <div style="min-width:0">
        <div class="evc-title">${esc(a.title)}</div>
        <div class="muted evc-small">${esc(a.workflowName)} · ${wfDate(a.raisedAt)}</div>
      </div>
    </div>
    <div class="evc-detail-body">
      <div class="evc-info">
        <div class="evc-section">Origen</div>
        <dl class="evc-facts">
          <dt>Automatización</dt><dd>${esc(a.workflowName)}</dd>
          <dt>Qué la disparó</dt><dd>${esc(a.triggerSummary || "—")}</dd>
          <dt>Severidad</dt><dd>${EVC_SEVERITY[a.severity] ?? esc(a.severity)}</dd>
          <dt>Hora del evento</dt><dd>${wfDate(a.raisedAt)}</dd>
          <dt>Ejecución</dt><dd>${a.runId ? `N° ${a.runId}` : "—"}</dd>
          <dt>Alarma sonora</dt><dd>${esc(sound)}</dd>
          <dt>Dirigida a</dt><dd>${a.recipients ? esc(a.recipients) : "todos los operadores"}</dd>
        </dl>
        <div class="evc-section">Descripción</div>
        <div class="evc-box message">${esc(a.message || "—")}</div>
        <div class="evc-section">Acuse de recibo</div>
        <div class="evc-box">
          <b class="evc-ack-state ${ackCls}">${ackState}</b>
          <div class="muted evc-small" style="white-space:normal;margin-top:3px">${ackDetail}</div>
        </div>
        <div class="evc-detail-actions">
          ${a.pending ? `<button class="btn danger" id="evc-ack-detail">Enterado</button>` : ""}
          <button class="btn ghost" id="evc-mute" title="Detiene el sonido sin confirmar la alerta">Silenciada</button>
        </div>
        <div class="muted evc-small" style="white-space:normal">${a.pending
          ? "Queda registrado quién confirma, a qué hora y desde dónde."
          : a.acknowledgedAt ? "Esta alerta ya tiene acuse de recibo; el registro no se modifica." : ""}</div>
      </div>
      <div class="evc-media">
        <div class="evc-tabs">
          <button class="evc-tab" data-tab="photos">Fotos${evcPhotos(a).length ? ` (${evcPhotos(a).length})` : ""}</button>
          <button class="evc-tab" data-tab="cameras">Cámaras${a.channelIds?.length ? ` (${a.channelIds.length})` : ""}</button>
          <button class="evc-tab" data-tab="steps">Qué hizo el sistema</button>
        </div>
        <div class="evc-tab-body" id="evc-tab-body"></div>
      </div>
    </div>`;

  $("#evc-ack-detail")?.addEventListener("click", (e) => evcAcknowledge(a.id, e.currentTarget));
  $("#evc-mute").addEventListener("click", evcStopSound);
  evcUpdateMuteButton();
  $$(".evc-tab", box).forEach((t) => t.addEventListener("click", () => { evcTab = t.dataset.tab; renderEvcTab(a); }));
  renderEvcTab(a);
}

function renderEvcTab(a) {
  const body = $("#evc-tab-body");
  if (!body) return;
  $$(".evc-tab").forEach((t) => t.classList.toggle("active", t.dataset.tab === evcTab));

  if (evcTab === "photos") {
    const photos = evcPhotos(a);
    if (!photos.length) { body.innerHTML = `<div class="evc-empty muted">Esta alerta no tiene fotos.</div>`; return; }
    evcPhotoIndex = Math.min(evcPhotoIndex, photos.length - 1);
    const url = evcFileUrl(photos[evcPhotoIndex]);
    body.innerHTML = `
      <div class="evc-photo">
        <a href="${url}" target="_blank" title="Abrir en tamaño completo"><img src="${url}" alt="Foto ${evcPhotoIndex + 1}"></a>
        ${photos.length > 1 ? `
          <button class="evc-nav prev" ${evcPhotoIndex === 0 ? "disabled" : ""}>‹</button>
          <button class="evc-nav next" ${evcPhotoIndex === photos.length - 1 ? "disabled" : ""}>›</button>
          <span class="evc-counter">${evcPhotoIndex + 1}/${photos.length}</span>` : ""}
      </div>
      ${photos.length > 1 ? `<div class="evc-thumbs">${photos.map((p, i) =>
        `<img src="${evcFileUrl(p)}" data-i="${i}" class="${i === evcPhotoIndex ? "active" : ""}" alt="" loading="lazy">`).join("")}</div>` : ""}`;
    const go = (i) => { evcPhotoIndex = i; renderEvcTab(a); };
    $(".evc-nav.prev", body)?.addEventListener("click", () => go(evcPhotoIndex - 1));
    $(".evc-nav.next", body)?.addEventListener("click", () => go(evcPhotoIndex + 1));
    $$(".evc-thumbs img", body).forEach((img) => img.addEventListener("click", () => go(Number(img.dataset.i))));
    return;
  }

  if (evcTab === "cameras") {
    body.innerHTML = a.channelIds?.length
      ? `<div class="info-box">Esta alerta tiene ${a.channelIds.length} cámara(s) vinculada(s) (canales ${a.channelIds.map(esc).join(", ")}).
           El video en vivo se ve desde la ventana de alarma del cliente de escritorio.</div>`
      : `<div class="evc-empty muted">Esta alerta no tiene cámara vinculada.</div>`;
    return;
  }

  // Pasos de la ejecución que generó la alerta.
  if (!a.runId) { body.innerHTML = `<div class="evc-empty muted">Esta alerta no está asociada a una ejecución.</div>`; return; }
  body.innerHTML = `<div class="evc-empty muted">Cargando…</div>`;
  Api.get(`/api/workflows/runs/${a.runId}`).then((run) => {
    if (evcTab !== "steps" || !$("#evc-tab-body") || evcSelectedId !== a.id) return;
    body.innerHTML = run.steps.map((s) => `
      <div class="evc-step">
        <div class="evc-step-head ${s.success ? "ok" : "bad"}">${s.order}. ${esc(s.label)} <span>${s.success ? "✓" : "✕"}</span>
          <span class="muted">${(s.elapsedMs / 1000).toFixed(1)} s</span></div>
        ${s.detail ? `<div class="evc-small" style="white-space:normal;margin-top:4px">${esc(s.detail)}</div>` : ""}
      </div>`).join("") || `<div class="evc-empty muted">La ejecución no registró pasos.</div>`;
  }).catch(() => {
    if (evcTab === "steps" && $("#evc-tab-body")) body.innerHTML = `<div class="evc-empty muted">No se pudo leer el detalle de la ejecución.</div>`;
  });
}
