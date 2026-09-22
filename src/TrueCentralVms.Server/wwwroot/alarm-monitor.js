// CLR TrueCentral VMS — Aplicaciones → Alarmas (monitoreo de paneles de intrusión).
// Réplica en el panel de la pantalla "Paneles de alarma" del cliente
// (Views\AlarmsView.xaml): lista de paneles | detalle con áreas y zonas |
// flujo de eventos. La página de Configuración → Dispositivos → Paneles de
// alarma (alarms.js) sigue siendo la del alta y edición de los equipos.
//
// El panel no escucha el hub: estado y eventos nuevos se traen por sondeo.
// Se carga después de alarms.js (usa alarmAddress / alarmAddressHint).
"use strict";

let alarmMonTimer = null;
const ALARM_MON_POLL_MS = 5000;
const alarmMonState = { kind: "", onlySelected: false, take: 200 };
let alarmMonPanels = [];
let alarmMonPanelId = null;
let alarmMonEvents = [];
let alarmMonDetailKey = "";   // lo último dibujado en el detalle (evita redibujar sin cambios)

const ALARM_MON_KINDS = [
  ["", "Todos los eventos"],
  ["Alarm", "Alarmas"],
  ["Arm", "Armados"],
  ["Disarm", "Desarmados"],
  ["Bypass", "Anulaciones"],
  ["ZoneTriggered,ZoneRestored", "Sensores"],
  ["Trouble", "Fallas"],
  ["Restore", "Restauraciones"],
  ["System", "Sistema"],
];

const ALARM_MON_KIND_LABELS = {
  Alarm: "Alarma", Restore: "Restauración", Arm: "Armado", Disarm: "Desarmado", Bypass: "Anulación",
  Trouble: "Falla", System: "Sistema", Info: "Información", ZoneTriggered: "Sensor activado", ZoneRestored: "Sensor normal",
};

const ALARM_MON_AREA_LABELS = {
  Disarmed: ["Desarmada", "disarmed"],
  Away: ["Armada · total", "armed"],
  Stay: ["Armada · parcial", "partial"],
  Vacation: ["Armada · vacaciones", "armed"],
  Arming: ["Armando…", "partial"],
  Unknown: ["—", "disarmed"],
};

const ALARM_MON_ZONE_LABELS = {
  Normal: ["Normal", "armed"],
  Triggered: ["Activada", "triggered"],
  Fault: ["Falla", "triggered"],
  Offline: ["Sin comunicación", "triggered"],
  NotConfigured: ["Sin área", "disarmed"],
  Unknown: ["—", "disarmed"],
};

const alarmMonIsArmed = (a) => a.armState === "Away" || a.armState === "Stay" || a.armState === "Vacation";

// ---------- Textos del panel (réplica de AlarmPanelItemViewModel) ----------

function alarmMonStatusText(p) {
  switch (p.status) {
    case "Online": return p.live ? "En línea · recibiendo eventos" : "En línea";
    case "Offline": return "Sin conexión";
    case "AuthFailed": return "Credenciales rechazadas";
    default: return p.enabled ? "Conectando…" : "Monitoreo desactivado";
  }
}

/** Resumen del armado y su color: [texto, nivel]. */
function alarmMonArm(p) {
  const n = p.areas.length;
  const armed = p.areas.filter(alarmMonIsArmed).length;
  const arming = p.areas.some((a) => a.armState === "Arming");
  let text;
  if (n === 0) text = "Sin áreas";
  else if (armed === n) text = "Armado";
  else if (arming) text = armed === 0 ? "Armando…" : `Armando… (${armed}/${n})`;
  else if (armed === 0) text = "Desarmado";
  else text = `Parcial (${armed}/${n})`;
  const level = p.inAlarm ? "alarm" : n === 0 ? "disarmed" : armed === n ? "armed" : armed > 0 || arming ? "partial" : "disarmed";
  return [text, level];
}

// ---------- Textos del evento (réplica de AlarmEventItemViewModel) ----------

function alarmMonDescribePanelUser(user) {
  const u = (user ?? "").trim();
  switch (u.toUpperCase()) {
    case "": return "Informado por el panel";
    case "ISUP": case "OTAP": case "EHOME": return `Orden remota por ${u} (receptora / VMS)`;
    case "ISAPI": return "Orden remota por ISAPI (VMS)";
    case "HIK-CONNECT": case "HIKCONNECT": case "CLOUD": return "Orden desde la app (Hik-Connect)";
    case "KEYPAD": case "KEYFOB": return `Orden desde ${u.toLowerCase()} (teclado/llavero)`;
    default: return `Usuario del panel: ${u}`;
  }
}

function alarmMonOrigin(ev) {
  const who = ev.source === "vms" ? `Operador: ${ev.operator ?? "—"}`
    : ev.source === "panel" ? alarmMonDescribePanelUser(ev.operator)
    : "Detectado por sondeo";
  return ev.code ? `${who}  ·  código ${ev.code}` : who;
}

function alarmMonWhere(ev) {
  return [ev.panelName, ev.areaName, ev.zoneName].filter((s) => s && String(s).trim()).join(" · ");
}

/** Los instantes viajan en UTC; si llegaran sin zona se toman como UTC igual. */
function alarmMonDate(iso) {
  if (!iso) return "—";
  return formatDateTime(/[zZ]|[+-]\d{2}:?\d{2}$/.test(iso) ? iso : iso + "Z");
}

function alarmMonSeverityClass(ev) {
  if (ev.kind === "Arm") return "ok";
  return ev.severity === "Critical" ? "crit" : ev.severity === "Warning" ? "warn" : "info";
}

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
async function renderAlarmMonitor() {
  $("#page-title").textContent = "Alarmas";
  alarmMonDetailKey = "";

  try { alarmMonPanels = await Api.get("/api/alarms/panels"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  if (!alarmMonPanels.some((p) => p.id === alarmMonPanelId)) alarmMonPanelId = alarmMonPanels[0]?.id ?? null;

  const kindOptions = ALARM_MON_KINDS
    .map(([v, l]) => `<option value="${v}" ${alarmMonState.kind === v ? "selected" : ""}>${l}</option>`).join("");

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3 style="display:flex;align-items:center;gap:8px"><span class="dot" id="almon-dot"></span><span id="almon-summary"></span></h3>
    </div>
    <div class="filter-bar">
      <div class="field"><label>Tipo de evento</label><select id="almf-kind">${kindOptions}</select></div>
      <div class="field"><label>Buscar texto</label>
        <input id="almf-text" placeholder="descripción, zona, área, operador" value="${esc(alarmMonState.text ?? "")}"></div>
      <div class="field"><label>Desde</label>
        <input type="datetime-local" id="almf-from" value="${esc(alarmMonState.from ?? "")}"></div>
      <div class="field"><label>Hasta</label>
        <input type="datetime-local" id="almf-to" value="${esc(alarmMonState.to ?? "")}"></div>
      <div class="field"><label>&nbsp;</label>
        <label class="almon-check"><input type="checkbox" id="almf-only" ${alarmMonState.onlySelected ? "checked" : ""}> Solo el panel seleccionado</label></div>
      <div class="filter-actions">
        <button class="btn" id="almf-search">Buscar</button>
        <button class="btn ghost" id="almf-clear" title="Quita todos los filtros">Limpiar</button>
      </div>
    </div>
    <div class="almon-layout">
      <section class="almon-col almon-panels" id="almon-panels"></section>
      <section class="almon-col almon-detail" id="almon-detail"></section>
      <section class="almon-col almon-events">
        <div class="almon-col-head"><b>Eventos</b><span class="muted" id="almon-count"></span></div>
        <div class="almon-event-list" id="almon-events"><div class="info-box">Cargando eventos…</div></div>
      </section>
    </div>`;

  const readFilters = () => {
    alarmMonState.kind = $("#almf-kind").value;
    alarmMonState.text = $("#almf-text").value.trim();
    alarmMonState.from = $("#almf-from").value || "";
    alarmMonState.to = $("#almf-to").value || "";
    alarmMonState.onlySelected = $("#almf-only").checked;
  };
  const search = () => { readFilters(); loadAlarmMonEvents(true); };
  $("#almf-search").addEventListener("click", search);
  // Tipo y "solo el seleccionado" filtran al instante, como en el cliente.
  $("#almf-kind").addEventListener("change", search);
  $("#almf-only").addEventListener("change", search);
  $("#almf-text").addEventListener("keydown", (e) => { if (e.key === "Enter") search(); });
  $("#almf-clear").addEventListener("click", () => {
    for (const k of ["text", "from", "to"]) delete alarmMonState[k];
    alarmMonState.kind = "";
    alarmMonState.onlySelected = false;
    renderAlarmMonitor();
  });

  renderAlarmMonPanels();
  await loadAlarmMonEvents(true);

  clearInterval(alarmMonTimer);
  alarmMonTimer = setInterval(async () => {
    try { alarmMonPanels = await Api.get("/api/alarms/panels"); } catch { return; }
    if (!$("#almon-panels")) return;
    renderAlarmMonPanels();
    // Con "Hasta" fijado la búsqueda mira al pasado: no hay nada nuevo que traer.
    if (!alarmMonState.to) loadAlarmMonEvents(false);
  }, ALARM_MON_POLL_MS);
}

function alarmMonEventsQuery() {
  const q = [];
  const add = (k, v) => { if (v) q.push(`${k}=${encodeURIComponent(v)}`); };
  if (alarmMonState.onlySelected && alarmMonPanelId) add("panelId", alarmMonPanelId);
  add("kind", alarmMonState.kind);
  add("text", alarmMonState.text);
  // El filtro de fechas del servidor es en UTC: se traduce la hora local del navegador.
  if (alarmMonState.from) add("from", new Date(alarmMonState.from).toISOString());
  if (alarmMonState.to) add("to", new Date(alarmMonState.to).toISOString());
  q.push(`take=${alarmMonState.take}`);
  return q.join("&");
}

// ---------- Columna izquierda: paneles ----------

function renderAlarmMonPanels() {
  const box = $("#almon-panels");
  if (!box) return;
  const inAlarm = alarmMonPanels.filter((p) => p.inAlarm).length;
  $("#almon-summary").textContent = `${alarmMonPanels.length} panel(es) · ${inAlarm} en alarma`;
  $("#almon-dot").className = "dot " + (inAlarm ? "bad almon-pulse" : alarmMonPanels.length ? "ok" : "");

  if (!alarmMonPanels.length) {
    box.innerHTML = `<div class="almon-empty muted">No hay paneles de alarma. Agréguelos en Dispositivos → Paneles de alarma.</div>`;
    renderAlarmMonDetail();
    return;
  }
  box.innerHTML = alarmMonPanels.map((p) => {
    const [arm, level] = alarmMonArm(p);
    return `
      <div class="almon-panel-row ${p.id === alarmMonPanelId ? "selected" : ""} ${p.inAlarm ? "in-alarm" : ""}" data-id="${p.id}">
        <div class="almon-row-top"><b>${esc(p.name)}</b><span class="almon-pill ${level}">${esc(arm)}</span></div>
        <div class="almon-row-sub"><span class="dot ${p.status === "Online" ? "ok" : "bad"}"></span> ${esc(alarmMonStatusText(p))}</div>
        <div class="almon-row-sub muted">${p.areas.length} área(s) · ${p.zones.length} zona(s)</div>
      </div>`;
  }).join("");
  $$(".almon-panel-row", box).forEach((row) => row.addEventListener("click", () => {
    alarmMonPanelId = Number(row.dataset.id);
    $$(".almon-panel-row", box).forEach((r) => r.classList.toggle("selected", r === row));
    renderAlarmMonDetail();
    if (alarmMonState.onlySelected) loadAlarmMonEvents(true);
  }));
  renderAlarmMonDetail();
}

// ---------- Columna central: detalle del panel ----------

function renderAlarmMonDetail() {
  const box = $("#almon-detail");
  if (!box) return;
  const p = alarmMonPanels.find((x) => x.id === alarmMonPanelId);
  const key = p ? JSON.stringify(p) : "";
  if (key === alarmMonDetailKey && box.childElementCount) return; // sin cambios: no molestar al operador
  alarmMonDetailKey = key;
  if (!p) { box.innerHTML = `<div class="almon-empty muted">Seleccione un panel.</div>`; return; }

  const [arm, level] = alarmMonArm(p);
  const detail = [p.model, p.serialNumber, p.firmwareVersion].filter((s) => s && s.trim()).join("  ·  ");
  const warnings = [
    p.panelTamper ? "Tapa del panel abierta (sabotaje): el equipo no permitirá armar hasta cerrarla." : null,
    p.acLoss ? "Falla de corriente de red: el panel está funcionando en batería." : null,
  ].filter(Boolean);
  const anyArming = p.areas.some((a) => a.armState === "Arming");
  const allArmed = p.areas.length > 0 && p.areas.every(alarmMonIsArmed);

  const areaCard = (a) => {
    const [label, lvl] = ALARM_MON_AREA_LABELS[a.armState] ?? ALARM_MON_AREA_LABELS.Unknown;
    const zones = p.zones.filter((z) => z.areaNumber === a.number).length;
    return `
      <div class="almon-card ${a.inAlarm ? "in-alarm" : ""}">
        <div class="almon-row-top">
          <div><b>${esc(a.name)}</b><div class="muted almon-small">Área ${a.number} · ${zones} zona(s)${a.enabled ? "" : " · deshabilitada"}</div></div>
          <span class="almon-pill ${a.inAlarm ? "alarm" : lvl}">${esc(label)}</span>
        </div>
        ${a.inAlarm ? `<div class="almon-alarm-text">¡ALARMA ACTIVA!</div>` : ""}
        <div class="almon-actions">
          <button class="btn almon-arm" data-area="${a.number}" data-mode="Away" ${alarmMonIsArmed(a) ? "disabled" : ""}>Armar total</button>
          <button class="btn ghost almon-arm" data-area="${a.number}" data-mode="Stay" ${alarmMonIsArmed(a) ? "disabled" : ""}>Parcial</button>
          <button class="btn ghost almon-disarm" data-area="${a.number}" ${a.armState === "Disarmed" ? "disabled" : ""}>${a.armState === "Arming" ? "Cancelar" : "Desarmar"}</button>
          ${a.inAlarm ? `<button class="btn danger almon-clear" data-area="${a.number}">Silenciar</button>` : ""}
        </div>
      </div>`;
  };

  const zoneTile = (z) => {
    const [label, lvl] = z.inAlarm ? ["¡ALARMA!", "alarm"] : (ALARM_MON_ZONE_LABELS[z.status] ?? ALARM_MON_ZONE_LABELS.Unknown);
    const area = p.areas.find((a) => a.number === z.areaNumber);
    const flags = [z.bypassed && "anulada", z.tamper && "tamper", z.lowBattery && "batería baja", z.armed && "armada"].filter(Boolean).join(" · ");
    const info = [z.detectorType, z.zoneType, z.model, z.signal != null ? `señal ${z.signal}` : null].filter(Boolean).join(" · ");
    return `
      <div class="almon-zone ${z.inAlarm ? "in-alarm" : ""} ${z.bypassed ? "bypassed" : ""}">
        <div class="almon-row-top"><b title="${esc(z.name)}">${esc(z.name)}</b><span class="almon-pill ${z.bypassed && !z.inAlarm ? "bypassed" : lvl}">${esc(label)}</span></div>
        <div class="muted almon-small">${area ? esc(area.name) : "Sin área"} · zona ${z.number}</div>
        ${flags ? `<div class="almon-small almon-flags">${esc(flags)}</div>` : ""}
        ${info ? `<div class="muted almon-small" title="${esc(info)}">${esc(info)}</div>` : ""}
        <button class="btn ghost almon-bypass" data-zone="${z.number}" data-on="${z.bypassed ? 1 : 0}">${z.bypassed ? "Restituir" : "Anular"}</button>
      </div>`;
  };

  box.innerHTML = `
    <div class="almon-detail-head">
      <div class="almon-row-top" style="justify-content:flex-start;gap:10px">
        <span class="almon-title">${esc(p.name)}</span><span class="almon-pill ${level}">${esc(arm)}</span>
      </div>
      <div class="muted almon-small">${detail ? esc(detail) + "  ·  " : ""}<span title="${esc(alarmAddressHint(p))}">${alarmAddress(p)}</span></div>
      <div class="muted almon-small">${esc(alarmMonStatusText(p))} · ${p.lastStateAt ? `Estado leído a las ${new Date(p.lastStateAt).toLocaleTimeString("es-CL")}` : "Estado aún no leído"}</div>
      ${p.lastError ? `<div class="almon-small" style="color:var(--danger)">${esc(p.lastError)}</div>` : ""}
      ${warnings.length ? `<div class="error-box" style="margin:8px 0 0">⚠ ${warnings.map(esc).join("<br>")}</div>` : ""}
      <div class="almon-actions" style="margin-top:10px">
        ${p.areas.length > 1 ? `
          <button class="btn almon-arm" data-area="0" data-mode="Away" ${allArmed ? "disabled" : ""}>Armar todo</button>
          <button class="btn ghost almon-arm" data-area="0" data-mode="Stay" ${allArmed ? "disabled" : ""}>Armar parcial (todo)</button>
          <button class="btn ghost almon-disarm" data-area="0">${anyArming ? "Cancelar armado" : "Desarmar todo"}</button>` : ""}
        ${p.inAlarm ? `<button class="btn danger almon-clear" data-area="0">Silenciar alarmas</button>` : ""}
        <button class="btn ghost almon-refresh">Actualizar</button>
      </div>
    </div>
    <div class="almon-section">Áreas</div>
    ${p.areas.length ? `<div class="almon-cards">${p.areas.map(areaCard).join("")}</div>` : `<div class="info-box">El panel no reportó áreas todavía.</div>`}
    <div class="almon-section">Zonas</div>
    ${p.zones.length ? `<div class="almon-zones">${p.zones.map(zoneTile).join("")}</div>` : `<div class="info-box">El panel no reportó zonas todavía.</div>`}`;

  const run = async (btn, path, body, okMessage) => {
    btn.disabled = true;
    try {
      const updated = await Api.post(path, body);
      const i = alarmMonPanels.findIndex((x) => x.id === updated.id);
      if (i >= 0) alarmMonPanels[i] = updated;
      toast(okMessage);
      renderAlarmMonPanels();
      loadAlarmMonEvents(false);
    } catch (err) { toast(err.error, true); btn.disabled = false; }
  };
  const base = `/api/alarms/panels/${p.id}`;
  $$(".almon-arm", box).forEach((b) => b.addEventListener("click", () =>
    run(b, `${base}/areas/${b.dataset.area}/arm`, { mode: b.dataset.mode },
      b.dataset.mode === "Stay" ? "Orden de armado parcial enviada." : "Orden de armado enviada.")));
  $$(".almon-disarm", box).forEach((b) => b.addEventListener("click", () =>
    run(b, `${base}/areas/${b.dataset.area}/disarm`, undefined, "Orden de desarmado enviada.")));
  $$(".almon-clear", box).forEach((b) => b.addEventListener("click", () =>
    run(b, `${base}/areas/${b.dataset.area}/clear-alarm`, undefined, "Alarma silenciada.")));
  $$(".almon-bypass", box).forEach((b) => b.addEventListener("click", () => {
    const bypassed = b.dataset.on !== "1";
    run(b, `${base}/zones/${b.dataset.zone}/bypass`, { bypassed }, bypassed ? "Zona anulada." : "Zona restituida.");
  }));
  $(".almon-refresh", box)?.addEventListener("click", (e) => run(e.currentTarget, `${base}/refresh`, undefined, "Estado actualizado."));
}

// ---------- Columna derecha: flujo de eventos ----------

async function loadAlarmMonEvents(reset) {
  const list = $("#almon-events");
  if (!list) return;
  let data;
  try { data = await Api.get(`/api/alarms/events?${alarmMonEventsQuery()}`); }
  catch (err) {
    if (reset) list.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }
  if (!$("#almon-events")) return; // se cambió de página mientras llegaba

  if (reset) {
    alarmMonEvents = data;
    list.innerHTML = data.length ? data.map(alarmMonEventHtml).join("")
      : `<div class="almon-empty muted">Aún no hay eventos que coincidan con los filtros.</div>`;
  } else {
    const known = new Set(alarmMonEvents.map((e) => e.id));
    const fresh = data.filter((e) => !known.has(e.id));
    if (fresh.length) {
      if (!alarmMonEvents.length) list.innerHTML = "";
      list.insertAdjacentHTML("afterbegin", fresh.map((e) => alarmMonEventHtml(e, true)).join(""));
      alarmMonEvents = fresh.concat(alarmMonEvents);
      while (alarmMonEvents.length > alarmMonState.take) {
        const gone = alarmMonEvents.pop();
        $(`.almon-event[data-id="${gone.id}"]`, list)?.remove();
      }
      const critical = fresh.find((e) => e.severity === "Critical");
      if (critical) toast(`ALARMA: ${critical.description} — ${alarmMonWhere(critical)}`, true);
    }
  }
  $("#almon-count").textContent = `${alarmMonEvents.length} en pantalla`;
}

function alarmMonEventHtml(ev, isNew) {
  const sev = alarmMonSeverityClass(ev);
  return `
    <div class="almon-event ${isNew ? "fresh" : ""}" data-id="${ev.id}">
      <span class="almon-sev ${sev}"></span>
      <div class="almon-event-main">
        <div class="almon-row-top">
          <b>${esc(ev.description)}</b>
          <span class="almon-kind ${sev}">${esc(ALARM_MON_KIND_LABELS[ev.kind] ?? ev.kind)}</span>
        </div>
        <div class="almon-small">${esc(alarmMonWhere(ev))}</div>
        <div class="muted almon-small">${alarmMonDate(ev.timestamp)} · ${esc(alarmMonOrigin(ev))}</div>
      </div>
    </div>`;
}
