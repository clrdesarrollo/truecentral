// CLR TrueCentral VMS — panel: control de acceso, operación.
//
// Cinco páginas colgadas del mismo módulo, en el orden en que se usan:
//   #/access-monitor    puertas en vivo (en access-monitor.js)
//   #/access-persons    padrón: quién es cada uno y con qué entra
//   #/access-levels     niveles de acceso: por dónde y cuándo
//   #/access-schedules  horarios: los "cuándo"
//   #/access-events     registros de acceso (en access-monitor.js)
//
// El administrador de EQUIPOS vive aparte, en access.js (#/access).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

let accessDoorsTimer = null;
let accessEventsTimer = null;
let accessSyncTimer = null;
let accessSyncLastDone = null;

const ACCESS_DAYS = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];
const ACCESS_DAYS_SHORT = ["Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb"];

const ACCESS_EVENT_KINDS = {
  Granted: { label: "Acceso concedido", tag: "on" },
  Denied: { label: "Acceso denegado", tag: "off" },
  DoorOpen: { label: "Puerta abierta", tag: "operator" },
  DoorClose: { label: "Puerta cerrada", tag: "operator" },
  Alarm: { label: "Alarma", tag: "off" },
  Other: { label: "Otro", tag: "operator" },
};

const ACCESS_CREDENTIALS = {
  Card: "Tarjeta", Fingerprint: "Huella", Face: "Rostro", Pin: "Clave",
  Remote: "Remoto", ExitButton: "Botón de salida", Qr: "Código QR", Plate: "Patente",
  Unknown: "—",
};

// Los diez dedos, en el mismo orden que AccessFingers del servidor: el número
// de dedo es el que usan los equipos, así que las dos listas no pueden diferir.
const ACCESS_FINGERS = [
  "Pulgar derecho", "Índice derecho", "Medio derecho", "Anular derecho", "Meñique derecho",
  "Pulgar izquierdo", "Índice izquierdo", "Medio izquierdo", "Anular izquierdo", "Meñique izquierdo",
];

// Orden en que conviene enrolar: los índices primero (los más cómodos en un
// lector), después medio y pulgar, alternando las manos.
const ACCESS_FINGER_ORDER = [2, 7, 3, 8, 1, 6, 4, 9, 5, 10];

/** El siguiente dedo sugerido que todavía no tiene huella (a partir del que sigue a `after`). */
function accessSuggestedFinger(fingerprints, after) {
  const has = new Set(fingerprints.map((f) => f.number));
  const start = after == null ? 0 : ACCESS_FINGER_ORDER.indexOf(after) + 1;
  const order = [...ACCESS_FINGER_ORDER.slice(start), ...ACCESS_FINGER_ORDER.slice(0, start)];
  return order.find((n) => !has.has(n)) ?? null;
}

// Las manos del enrolamiento, palmas hacia abajo como apoyadas en la mesa
// (la izquierda, a la izquierda). Cada dedo se dibuja como una cápsula que
// nace de la palma: base, largo y giro, en el dibujo de la mano IZQUIERDA; la
// derecha es la misma figura reflejada. `left`/`right` son los números de dedo
// de los equipos (ACCESS_FINGERS).
const ACCESS_HAND_FINGERS = [
  { left: 10, right: 5, x: 34, y: 104, len: 44, angle: -14 }, // meñique
  { left: 9, right: 4, x: 57, y: 98, len: 58, angle: -5 },    // anular
  { left: 8, right: 3, x: 81, y: 96, len: 64, angle: 2 },     // medio
  { left: 7, right: 2, x: 104, y: 100, len: 56, angle: 9 },   // índice
  { left: 6, right: 1, x: 114, y: 142, len: 44, angle: 60 },  // pulgar
];

/** Calidad de una plantilla: "buena" desde 60, "regular" desde 40 (el mismo criterio de la captura). */
const accessFingerQuality = (q) => q == null ? null : q >= 60 ? ["ok", "buena"] : q >= 40 ? ["warn", "regular"] : ["bad", "baja"];

/** Las dos manos con el estado de cada dedo: sin huella, enrolada, recién capturada, elegido. */
function accessHandsSvg(fingerprints, selected) {
  const W = 19;
  const finger = (number, f) => {
    const fp = fingerprints.find((x) => x.number === number);
    const weak = fp?.quality != null && fp.quality < 60;
    const cls = ["hand-finger", fp ? (fp.isNew ? "new" : "has") : "", weak ? "low" : "", number === selected ? "sel" : ""]
      .filter(Boolean).join(" ");
    const name = ACCESS_FINGERS[number - 1];
    const state = !fp ? "sin huella"
      : `${fp.isNew ? "recién capturada" : "enrolada"}${fp.quality != null ? `, calidad ${fp.quality}/100` : ""}`;
    const top = f.y - f.len;
    return `
      <g class="${cls}" data-finger="${number}" tabindex="0" role="button" aria-pressed="${number === selected}"
         aria-label="${esc(name)}: ${esc(state)}" transform="rotate(${f.angle} ${f.x} ${f.y})">
        <title>${esc(name)}: ${esc(state)}</title>
        <rect x="${f.x - W / 2}" y="${top}" width="${W}" height="${f.len + 14}" rx="${W / 2}"/>
        ${fp ? `<ellipse class="print" cx="${f.x}" cy="${top + 13}" rx="5" ry="6.5"/>
                <ellipse class="print" cx="${f.x}" cy="${top + 13}" rx="2.2" ry="3"/>` : ""}
        ${weak ? `<circle class="warn-dot" cx="${f.x + W / 2 - 2}" cy="${top + 4}" r="3.8"/>` : ""}
      </g>`;
  };
  // La palma (ancha en los nudillos, angosta hacia la muñeca) va encima:
  // tapa el nacimiento de los dedos.
  const hand = (side) => ACCESS_HAND_FINGERS.map((f) => finger(f[side], f)).join("") +
    `<path class="hand-palm" d="M 34 94 C 24 94 21 101 22 112 L 25 146 C 27 166 40 176 60 177 L 84 177
       C 104 176 116 166 118 148 L 121 112 C 122 101 117 94 108 94 Z"/>`;
  return `
    <svg class="hands" viewBox="0 0 340 196" role="group" aria-label="Huellas, dedo por dedo">
      <g>${hand("left")}</g>
      <g transform="translate(340 0) scale(-1 1)">${hand("right")}</g>
      <text x="71" y="191" text-anchor="middle">Mano izquierda</text>
      <text x="269" y="191" text-anchor="middle">Mano derecha</text>
    </svg>`;
}

/** Ficha del dedo elegido: su estado, la calidad y sus dos órdenes. */
function accessFingerDetailHtml(fingerprints, number) {
  const fp = fingerprints.find((f) => f.number === number);
  const quality = accessFingerQuality(fp?.quality);
  return `
    <div class="hand-detail-name">${esc(ACCESS_FINGERS[number - 1])}</div>
    <div>${!fp ? `<span class="muted">Sin huella enrolada.</span>`
      : fp.isNew ? `<span class="tag on">nueva</span> <span class="muted">se guarda al terminar</span>`
      : `<span class="tag admin">enrolada</span>`}</div>
    ${quality ? `
      <div class="hand-quality ${quality[0]}"><span style="width:${Math.max(4, Math.min(100, fp.quality))}%"></span></div>
      <div class="muted hand-quality-text">Calidad ${fp.quality}/100 · ${quality[1]}${fp.quality < 60
        ? ": conviene recapturarla con el dedo limpio y bien centrado." : "."}</div>` : ""}
    <div class="row-actions" style="margin-top:12px">
      <button type="button" class="btn ${fp ? "ghost" : ""} pw-finger-capture">${fp ? "Recapturar" : "Capturar"}</button>
      ${fp ? `<button type="button" class="btn ghost pw-finger-del" title="Quitarle esta huella">Quitar</button>` : ""}
    </div>`;
}

const ACCESS_SYNC_STATES = {
  Synced: { label: "Al día", tag: "on" },
  Pending: { label: "Pendiente", tag: "operator" },
  Failed: { label: "Con problemas", tag: "off" },
  NotApplicable: { label: "Sin niveles", tag: "operator" },
};

/** Minutos desde medianoche → "08:30" (lo que muestra y acepta un <input type=time>). */
const accessHhmm = (minutes) =>
  `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;

/** "08:30" → minutos desde medianoche; null si no es una hora. */
function accessMinutes(text) {
  const match = /^(\d{1,2}):(\d{2})$/.exec((text || "").trim());
  if (!match) return null;
  const hours = Number(match[1]), minutes = Number(match[2]);
  if (hours > 23 || minutes > 59) return null;
  return hours * 60 + minutes;
}

/** El horario en una línea, juntando los días que tienen los mismos tramos. */
function accessScheduleSummary(schedule) {
  if (!schedule.segments.length) return "sin tramos (no deja pasar a nadie)";
  const byDay = new Map();
  for (let day = 0; day < 7; day++) {
    const tramos = schedule.segments.filter((s) => s.day === day)
      .sort((a, b) => a.startMinutes - b.startMinutes)
      .map((s) => `${accessHhmm(s.startMinutes)}-${accessHhmm(s.endMinutes)}`).join(", ");
    if (tramos) byDay.set(day, tramos);
  }
  // Días seguidos con el mismo horario se muestran como un rango ("Lun a Vie").
  const parts = [];
  let run = null;
  for (let day = 0; day < 7; day++) {
    const tramos = byDay.get(day);
    if (run && run.tramos === tramos) { run.to = day; continue; }
    if (run) parts.push(run);
    run = tramos ? { from: day, to: day, tramos } : null;
  }
  if (run) parts.push(run);
  return parts.map((p) => {
    const days = p.from === p.to
      ? ACCESS_DAYS_SHORT[p.from]
      : `${ACCESS_DAYS_SHORT[p.from]} a ${ACCESS_DAYS_SHORT[p.to]}`;
    return `${days} ${p.tramos}`;
  }).join(" · ");
}

/** Deja de refrescar cuando el operador se fue de la página o abrió un modal. */
function accessPollGuard(hash, timer) {
  if (location.hash !== hash || $("#app-shell").classList.contains("hidden")) {
    clearInterval(timer);
    return false;
  }
  return $("#modal-backdrop").classList.contains("hidden");
}

// ===========================================================================
// Monitoreo en tiempo real: puertas + lo que va pasando
// ===========================================================================

// El monitoreo en vivo (tarjetas, órdenes por lote, lo que va pasando) vive
// en access-monitor.js. Renombrar una puerta es de su ficha en Recursos, y
// pausarla, de la página de su equipo (Conexión → Puertas del equipo).

// ===========================================================================
// Horarios
// ===========================================================================

async function renderAccessSchedules() {
  $("#page-title").textContent = "Control de acceso · Horarios";
  const isAdmin = Perms.can("persons.manage");
  let schedules;
  try { schedules = await Api.get("/api/access/schedules"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Horarios</h3>
      ${isAdmin ? `<button class="btn" id="btn-schedule-new">Crear horario</button>` : ""}
    </div>
    <div class="info-box">
      Un horario dice <b>cuándo</b> se puede pasar. No da permiso por sí solo: se le pone a un
      <a href="#/access-levels">nivel de acceso</a>, que es el que junta el horario con las puertas.
    </div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Nombre</th><th>Cuándo deja pasar</th><th>Niveles que lo usan</th><th>Ranura</th><th></th></tr></thead>
      <tbody>
        ${schedules.map((s) => `
          <tr data-id="${s.id}">
            <td>${esc(s.name)}${s.isBuiltIn ? ` <span class="tag operator" title="Lo trae el sistema">de fábrica</span>` : ""}
              ${s.description ? `<div class="muted" style="font-size:11px">${esc(s.description)}</div>` : ""}</td>
            <td class="muted">${esc(accessScheduleSummary(s))}</td>
            <td>${s.levelCount}</td>
            <td class="muted" title="Número que ocupa este horario en los equipos">${s.planNumber}</td>
            <td><div class="row-actions">
              ${isAdmin && !s.isBuiltIn ? `
                <button class="btn ghost btn-schedule-edit">Editar</button>
                <button class="btn danger btn-schedule-delete">Eliminar</button>` : ""}
            </div></td>
          </tr>`).join("")}
      </tbody>
    </table></div>`;

  const byRow = (e) => schedules.find((s) => s.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-schedule-new")?.addEventListener("click", () => accessScheduleModal(null));
  $$("#view .btn-schedule-edit").forEach((b) => b.addEventListener("click", (e) => accessScheduleModal(byRow(e))));
  $$("#view .btn-schedule-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const schedule = byRow(e);
    if (!confirm(`¿Eliminar el horario "${schedule.name}"?`)) return;
    try {
      await Api.delete(`/api/access/schedules/${schedule.id}`);
      toast("Horario eliminado.");
      renderAccessSchedules();
    } catch (err) { toast(err.error, true); }
  }));
}

/** Editor semanal: una fila por día, con sus tramos y botones para agregar y copiar. */
function accessScheduleModal(schedule) {
  const isNew = !schedule;
  const segments = new Map();   // día → [{start, end}]
  for (let day = 0; day < 7; day++) segments.set(day, []);
  for (const s of schedule?.segments ?? []) segments.get(s.day).push({ start: s.startMinutes, end: s.endMinutes });
  if (isNew) {
    // Un horario nuevo arranca con la jornada más común, para no partir de cero.
    for (let day = 1; day <= 5; day++) segments.get(day).push({ start: 8 * 60, end: 18 * 60 });
  }

  const dayRow = (day) => `
    <div class="access-day" data-day="${day}">
      <div class="access-day-name">${ACCESS_DAYS[day]}</div>
      <div class="access-day-segments">
        ${segments.get(day).map((s, i) => `
          <span class="access-segment" data-i="${i}">
            <input type="time" class="seg-start" value="${accessHhmm(s.start)}">
            <span class="muted">a</span>
            <input type="time" class="seg-end" value="${accessHhmm(s.end)}">
            <button type="button" class="btn ghost seg-del" title="Quitar este tramo">✕</button>
          </span>`).join("") || `<span class="muted">No deja pasar</span>`}
      </div>
      <div class="access-day-actions">
        <button type="button" class="btn ghost seg-add" title="Agregar un tramo a este día">+ tramo</button>
        <button type="button" class="btn ghost seg-copy" title="Copiar los tramos de este día a los otros seis">Copiar a todos</button>
      </div>
    </div>`;

  const redraw = () => {
    $("#as-days").innerHTML = [1, 2, 3, 4, 5, 6, 0].map(dayRow).join("");
    bindDays();
  };

  /** Lee del formulario lo que el operador tiene escrito, para no perderlo al redibujar. */
  const readDays = () => {
    $$("#as-days .access-day").forEach((row) => {
      const day = Number(row.dataset.day);
      segments.set(day, $$(".access-segment", row).map((span) => ({
        start: accessMinutes($(".seg-start", span).value) ?? 0,
        end: accessMinutes($(".seg-end", span).value) ?? 0,
      })));
    });
  };

  const bindDays = () => {
    $$("#as-days .seg-add").forEach((b) => b.addEventListener("click", (e) => {
      readDays();
      const day = Number(e.target.closest(".access-day").dataset.day);
      const list = segments.get(day);
      // El tramo nuevo empieza donde terminó el anterior: casi siempre es lo que se quiere.
      const last = list[list.length - 1];
      list.push(last ? { start: Math.min(last.end + 60, 23 * 60), end: Math.min(last.end + 120, 23 * 60 + 59) }
                     : { start: 8 * 60, end: 18 * 60 });
      redraw();
    }));
    $$("#as-days .seg-del").forEach((b) => b.addEventListener("click", (e) => {
      readDays();
      const day = Number(e.target.closest(".access-day").dataset.day);
      const index = Number(e.target.closest(".access-segment").dataset.i);
      segments.get(day).splice(index, 1);
      redraw();
    }));
    $$("#as-days .seg-copy").forEach((b) => b.addEventListener("click", (e) => {
      readDays();
      const day = Number(e.target.closest(".access-day").dataset.day);
      const source = segments.get(day).map((s) => ({ ...s }));
      for (let other = 0; other < 7; other++)
        if (other !== day) segments.set(other, source.map((s) => ({ ...s })));
      redraw();
    }));
  };

  openModal(`
    <h3>${isNew ? "Crear horario" : "Editar horario"}</h3>
    <div id="as-error"></div>
    <form id="access-schedule-form">
      <div class="form-grid">
        <div class="field">
          <label>Nombre</label>
          <input id="as-name" required maxlength="128" value="${esc(schedule?.name ?? "")}"
            placeholder="Jornada de oficina, Turno de noche…">
        </div>
        <div class="field">
          <label>Descripción (opcional)</label>
          <input id="as-description" maxlength="512" value="${esc(schedule?.description ?? "")}">
        </div>
      </div>
      <div class="field" style="margin-top:6px">
        <label>Cuándo deja pasar</label>
        <div id="as-days"></div>
        <div class="muted" style="font-size:12px;margin-top:6px">
          Hasta 8 tramos por día (es el tope de los equipos). Un día sin tramos no deja pasar a nadie.
        </div>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="as-cancel">Cancelar</button>
        <button class="btn" type="submit" id="as-save">${isNew ? "Crear" : "Guardar cambios"}</button>
      </div>
    </form>`, "wider");

  redraw();
  $("#as-cancel").addEventListener("click", closeModal);
  $("#access-schedule-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    readDays();
    const body = {
      name: $("#as-name").value.trim(),
      description: $("#as-description").value.trim() || null,
      segments: [...segments.entries()].flatMap(([day, list]) =>
        list.map((s) => ({ day, startMinutes: s.start, endMinutes: s.end }))),
    };
    const save = $("#as-save");
    save.disabled = true;
    try {
      if (isNew) await Api.post("/api/access/schedules", body);
      else await Api.put(`/api/access/schedules/${schedule.id}`, body);
      closeModal();
      toast(isNew ? "Horario creado." : "Horario actualizado. Se está reescribiendo en los equipos.");
      renderAccessSchedules();
    } catch (err) {
      $("#as-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
    }
  });
}

// ===========================================================================
// Niveles de acceso
// ===========================================================================

async function renderAccessLevels() {
  $("#page-title").textContent = "Control de acceso · Niveles";
  const isAdmin = Perms.can("persons.manage");
  let levels, doors, schedules;
  try {
    [levels, doors, schedules] = await Promise.all([
      Api.get("/api/access/levels"), Api.get("/api/access/doors"), Api.get("/api/access/schedules"),
    ]);
  } catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Niveles de acceso</h3>
      ${isAdmin ? `<button class="btn" id="btn-level-new" ${doors.length ? "" : "disabled"}>Crear nivel</button>` : ""}
    </div>
    <div class="info-box">
      Un nivel de acceso junta <b>puertas</b> con un <b>horario</b>: "por acá y a estas horas".
      Después se les asigna a las personas. Alguien con dos niveles pasa por las puertas de los dos,
      y si una puerta le llega por ambos, vale la suma de sus horarios.
    </div>
    ${!doors.length ? `<div class="warn-box">No hay puertas todavía: agregue un equipo en
      <a href="#/access">Dispositivos → Control de acceso</a>.</div>` : ""}
    ${!levels.length ? `<div class="info-box">Aún no hay niveles de acceso.</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Nombre</th><th>Horario</th><th>Puertas</th><th>Personas</th><th>Estado</th><th></th></tr></thead>
      <tbody>
        ${levels.map((l) => `
          <tr data-id="${l.id}">
            <td>${esc(l.name)}
              ${l.description ? `<div class="muted" style="font-size:11px">${esc(l.description)}</div>` : ""}</td>
            <td class="muted">${esc(l.scheduleName)}</td>
            <td><span title="${esc(l.doorNames.join(", "))}">${l.doorIds.length}</span>
              <div class="muted" style="font-size:11px;max-width:260px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"
                >${esc(l.doorNames.join(" · "))}</div></td>
            <td>${l.personCount}</td>
            <td>${l.enabled ? `<span class="tag on">Activo</span>` : `<span class="tag off">Desactivado</span>`}</td>
            <td><div class="row-actions" style="flex-wrap:wrap">
              ${isAdmin ? `
                <button class="btn ghost btn-level-assign" title="Elegir quiénes tienen este nivel">Asignar personas</button>
                <button class="btn ghost btn-level-edit">Editar</button>
                <button class="btn danger btn-level-delete">Eliminar</button>` : ""}
            </div></td>
          </tr>`).join("")}
      </tbody>
    </table></div>`}`;

  const byRow = (e) => levels.find((l) => l.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-level-new")?.addEventListener("click", () => accessLevelModal(null, doors, schedules));
  $$("#view .btn-level-edit").forEach((b) =>
    b.addEventListener("click", (e) => accessLevelModal(byRow(e), doors, schedules)));
  $$("#view .btn-level-assign").forEach((b) => b.addEventListener("click", (e) => accessAssignModal(byRow(e))));
  $$("#view .btn-level-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const level = byRow(e);
    const warning = level.personCount > 0
      ? `\n\n${level.personCount} persona(s) pierden ese permiso y se les va a quitar de los equipos.`
      : "";
    if (!confirm(`¿Eliminar el nivel de acceso "${level.name}"?${warning}`)) return;
    try {
      await Api.delete(`/api/access/levels/${level.id}`);
      toast("Nivel de acceso eliminado.");
      renderAccessLevels();
    } catch (err) { toast(err.error, true); }
  }));
}

/** Selector de puertas agrupadas por equipo (así se ven como están en terreno). */
function accessDoorPicker(doors, selected) {
  const groups = new Map();
  for (const door of doors) {
    if (!groups.has(door.deviceId)) groups.set(door.deviceId, { name: door.deviceName, location: door.location, doors: [] });
    groups.get(door.deviceId).doors.push(door);
  }
  return [...groups.values()].map((group) => `
    <div class="access-door-group">
      <div class="access-door-group-name">${esc(group.name)}${group.location ? ` <span class="muted">· ${esc(group.location)}</span>` : ""}</div>
      <div class="wf-check-grid" style="max-height:none">
        ${group.doors.map((d) => `
          <label class="checkbox-row" ${d.enabled ? "" : `title="Puerta pausada: no da permiso aunque se la incluya"`}>
            <input type="checkbox" class="al-door" value="${d.id}" ${selected.has(d.id) ? "checked" : ""}>
            ${esc(d.name)}${d.enabled ? "" : ` <span class="muted">(pausada)</span>`}
          </label>`).join("")}
      </div>
    </div>`).join("");
}

function accessLevelModal(level, doors, schedules) {
  const isNew = !level;
  const selected = new Set(level?.doorIds ?? []);

  openModal(`
    <h3>${isNew ? "Crear nivel de acceso" : "Editar nivel de acceso"}</h3>
    <div id="al-error"></div>
    <form id="access-level-form">
      <div class="form-grid">
        <div class="field">
          <label>Nombre</label>
          <input id="al-name" required maxlength="128" value="${esc(level?.name ?? "")}"
            placeholder="Oficinas, Bodega, Todo el edificio…">
        </div>
        <div class="field">
          <label>Horario</label>
          <select id="al-schedule">
            ${schedules.map((s) => `<option value="${s.id}" ${level?.scheduleId === s.id ? "selected" : ""}>${esc(s.name)}</option>`).join("")}
          </select>
        </div>
      </div>
      <div class="field">
        <label>Descripción (opcional)</label>
        <input id="al-description" maxlength="512" value="${esc(level?.description ?? "")}">
      </div>
      <div class="field">
        <label>Puertas <span class="muted" id="al-count">(${selected.size} elegida${selected.size === 1 ? "" : "s"})</span></label>
        <div id="al-doors">${accessDoorPicker(doors, selected)}</div>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="al-enabled" ${level ? (level.enabled ? "checked" : "") : "checked"}>
        Activo (desactivarlo le quita el permiso a todos los que lo tengan, sin borrar la configuración)</label>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="al-cancel">Cancelar</button>
        <button class="btn" type="submit" id="al-save">${isNew ? "Crear" : "Guardar cambios"}</button>
      </div>
    </form>`, true);

  const updateCount = () => {
    const count = $$("#al-doors .al-door:checked").length;
    $("#al-count").textContent = `(${count} elegida${count === 1 ? "" : "s"})`;
  };
  $$("#al-doors .al-door").forEach((c) => c.addEventListener("change", updateCount));
  $("#al-cancel").addEventListener("click", closeModal);

  $("#access-level-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const body = {
      name: $("#al-name").value.trim(),
      description: $("#al-description").value.trim() || null,
      scheduleId: Number($("#al-schedule").value),
      doorIds: $$("#al-doors .al-door:checked").map((c) => Number(c.value)),
      enabled: $("#al-enabled").checked,
    };
    const save = $("#al-save");
    save.disabled = true;
    try {
      if (isNew) await Api.post("/api/access/levels", body);
      else await Api.put(`/api/access/levels/${level.id}`, body);
      closeModal();
      toast(isNew ? "Nivel de acceso creado." : "Nivel actualizado. Se está reescribiendo en los equipos.");
      renderAccessLevels();
    } catch (err) {
      $("#al-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
    }
  });
}

/** "Asignar nivel de acceso": marcar quiénes lo tienen, con buscador. */
async function accessAssignModal(level) {
  let persons;
  try { persons = (await Api.get(`/api/access/persons?pageSize=500`)).items; }
  catch (err) { toast(err.error, true); return; }

  const selected = new Set(persons.filter((p) => p.levelIds.includes(level.id)).map((p) => p.id));

  const rows = (filter) => persons
    .filter((p) => {
      const term = filter.trim().toLowerCase();
      return !term || p.fullName.toLowerCase().includes(term) ||
             p.employeeNo.toLowerCase().includes(term) ||
             (p.department ?? "").toLowerCase().includes(term);
    })
    .map((p) => `
      <label class="checkbox-row">
        <input type="checkbox" class="ap-person" value="${p.id}" ${selected.has(p.id) ? "checked" : ""}>
        ${esc(p.fullName)} <span class="muted">· ${esc(p.employeeNo)}${p.department ? ` · ${esc(p.department)}` : ""}</span>
      </label>`).join("");

  openModal(`
    <h3>Quién tiene el nivel "${esc(level.name)}"</h3>
    <div id="ap-error"></div>
    <div class="field">
      <label>Buscar</label>
      <input id="ap-search" placeholder="nombre, identificador o departamento…">
    </div>
    <div class="field">
      <label>Personas <span class="muted" id="ap-count">(${selected.size} elegida${selected.size === 1 ? "" : "s"})</span></label>
      <div class="wf-check-grid" id="ap-list" style="max-height:340px">${rows("")}</div>
      ${persons.length ? "" : `<div class="info-box">Todavía no hay personas en el padrón.</div>`}
    </div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="ap-cancel">Cancelar</button>
      <button class="btn" type="button" id="ap-save">Guardar</button>
    </div>`, true);

  const updateCount = () => {
    $("#ap-count").textContent = `(${selected.size} elegida${selected.size === 1 ? "" : "s"})`;
  };
  // La selección vive en el Set, no en el DOM: filtrar la lista no puede
  // perder lo que el operador ya marcó fuera del filtro.
  const bind = () => $$("#ap-list .ap-person").forEach((c) => c.addEventListener("change", () => {
    if (c.checked) selected.add(Number(c.value)); else selected.delete(Number(c.value));
    updateCount();
  }));
  bind();
  $("#ap-search").addEventListener("input", (e) => { $("#ap-list").innerHTML = rows(e.target.value); bind(); });
  $("#ap-cancel").addEventListener("click", closeModal);
  $("#ap-save").addEventListener("click", async () => {
    const save = $("#ap-save");
    save.disabled = true;
    try {
      await Api.put(`/api/access/levels/${level.id}/persons`, { personIds: [...selected] });
      closeModal();
      toast("Asignación guardada. Se está escribiendo en los equipos.");
      renderAccessLevels();
    } catch (err) {
      $("#ap-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
    }
  });
}

// ===========================================================================
// Personas
// ===========================================================================

const accessPersonState = { page: 1, q: "", department: "", levelId: "", state: "" };

function accessSyncTag(person) {
  const state = ACCESS_SYNC_STATES[person.syncState] ?? ACCESS_SYNC_STATES.NotApplicable;
  // Sin equipos de por medio no hay nada que mostrar: el estado queda quieto.
  if (!person.devices?.length) {
    const detail = person.syncError ? ` title="${esc(person.syncError)}"` : "";
    return `<span class="tag ${state.tag}"${detail}>${esc(state.label)}</span>`;
  }
  // Con equipos, el estado se abre: es la pregunta que sigue a un "Con
  // problemas" —¿en cuál y por qué?—, y la respuesta no cabe en un tooltip.
  return `<button type="button" class="tag ${state.tag} tag-link btn-person-sync-detail"
    title="Ver qué pasó en cada equipo">${esc(state.label)} ›</button>`;
}

/**
 * Un error del sincronizador partido en dos: lo que le pasó a la persona, en
 * palabras, y lo que contestó el equipo, tal cual. El driver deja la respuesta
 * del equipo entre paréntesis y con sus partes separadas por "·"
 * ("(Invalid Content · badJsonContent · …)"); juntas en un solo párrafo, el
 * motivo técnico tapaba la explicación.
 */
function accessSplitSyncError(error) {
  if (!error) return { text: "", raw: [] };
  const raw = [];
  const text = error
    .replace(/\(([^()]*·[^()]*)\)/g, (_, inner) => { raw.push(inner.trim()); return ""; })
    .replace(/\s+([.,:;])/g, "$1")
    .replace(/\s{2,}/g, " ")
    .trim();
  return { text, raw };
}

/** Las credenciales que lleva la persona, en una línea: lo que se intentó escribir. */
function accessCredentialSummary(person) {
  const parts = [
    person.cards.length ? `${person.cards.length} tarjeta${person.cards.length === 1 ? "" : "s"}` : null,
    person.fingerprints.length
      ? `${person.fingerprints.length} huella${person.fingerprints.length === 1 ? "" : "s"}` : null,
    person.face ? "rostro" : null,
    person.hasPin ? "clave" : null,
  ].filter(Boolean);
  return parts.join(" · ");
}

/**
 * Detalle de la escritura de una persona en cada uno de sus equipos: el estado
 * de cada uno, qué pasó y qué contestó el equipo. Es a donde lleva el estado
 * de la lista ("Con problemas"); antes había que abrir la edición y bajar
 * hasta el tercer paso para encontrar el motivo.
 */
function accessPersonSyncModal(person, levels) {
  const isAdmin = Perms.can("persons.manage");
  // Primero lo que falla, después lo que espera y al final lo que está al día.
  const ORDER = { Failed: 0, Pending: 1, NotApplicable: 2, Synced: 3 };

  // Lo que la persona lleva y ESTE equipo no usa, así que no se le mandó: un
  // terminal facial sin lector de huella está al día sin sus huellas, y decir
  // "todas sus credenciales" ahí haría creer que con el dedo también entra.
  const notCarried = (d, p) => [
    p.fingerprints.length && d.supportsFingerprint === false
      ? `No lleva ${p.fingerprints.length === 1 ? "su huella" : "sus huellas"} porque el equipo no tiene lector de huellas.`
      : null,
    p.face && d.supportsFace === false ? "No lleva su rostro porque el equipo no reconoce rostros." : null,
  ].filter(Boolean);

  const deviceBlock = (d, p) => {
    const state = ACCESS_SYNC_STATES[d.state] ?? ACCESS_SYNC_STATES.NotApplicable;
    const { text, raw } = accessSplitSyncError(d.error);
    const missing = notCarried(d, p);
    const body = d.state !== "Synced"
      ? `<div>${esc(text || "Sin detalle del equipo.")}</div>`
      : missing.length
        ? `<div class="muted">Quedó escrita con lo que este equipo usa. ${esc(missing.join(" "))}</div>`
        : `<div class="muted">Quedó escrita con todas sus credenciales.</div>`;
    return `
      <div class="sync-device ${d.state === "Failed" ? "failed" : d.state === "Pending" ? "pending" : ""}">
        <div class="sync-device-head">
          <strong>${esc(d.deviceName)}</strong>
          <span class="tag ${state.tag}">${esc(state.label)}</span>
          <span class="muted sync-device-when">Última escritura correcta:
            ${d.syncedAt ? formatDateTime(d.syncedAt) : "nunca"}</span>
        </div>
        ${body}
        ${raw.length ? `
          <div class="sync-device-raw">
            <div class="muted">Respuesta del equipo</div>
            ${raw.map((r) => `<code>${esc(r)}</code>`).join("")}
          </div>` : ""}
      </div>`;
  };

  const render = (p) => {
    const state = ACCESS_SYNC_STATES[p.syncState] ?? ACCESS_SYNC_STATES.NotApplicable;
    const devices = [...p.devices].sort((a, b) =>
      (ORDER[a.state] ?? 9) - (ORDER[b.state] ?? 9) || a.deviceName.localeCompare(b.deviceName));
    const failed = devices.filter((d) => d.state === "Failed").length;
    const credentials = accessCredentialSummary(p);

    openModal(`
      <h3>Escritura en los equipos</h3>
      <div class="sync-person-head">
        <div>
          <strong>${esc(p.fullName)}</strong> <span class="muted">· identificador ${esc(p.employeeNo)}</span>
          <div class="muted" style="font-size:12px;margin-top:2px">
            ${credentials ? `Lleva ${esc(credentials)}` : "No tiene credenciales"} ·
            vigente hasta ${accessDay(p.validTo)}${p.levelNames.length ? ` · ${esc(p.levelNames.join(", "))}` : ""}
          </div>
        </div>
        <span class="tag ${state.tag}">${esc(state.label)}</span>
      </div>
      ${failed ? `<div class="muted" style="font-size:12px;margin-bottom:10px">
        ${failed === 1 ? "Un equipo no aceptó" : `${failed} equipos no aceptaron`} todo lo que se le mandó.
        Corrija lo que indique el equipo y vuelva a escribirla.</div>` : ""}
      <div id="psd-error"></div>
      <div class="sync-device-list">${devices.map((d) => deviceBlock(d, p)).join("")}</div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="psd-close">Cerrar</button>
        <div style="flex:1"></div>
        ${isAdmin ? `
          <button class="btn ghost" type="button" id="psd-edit">Editar persona</button>
          <button class="btn" type="button" id="psd-retry"
            title="Volver a escribirla ahora en todos sus equipos">Volver a escribir</button>` : ""}
      </div>`, "wider");

    $("#psd-close").addEventListener("click", closeModal);
    $("#psd-edit")?.addEventListener("click", () => {
      closeModal();
      accessPersonModal(p, levels);
    });
    $("#psd-retry")?.addEventListener("click", async (e) => {
      const button = e.currentTarget;
      button.disabled = true;
      button.textContent = "Escribiendo…";
      try {
        const updated = await Api.post(`/api/access/persons/${p.id}/sync`);
        render(updated);
        toast(updated.syncState === "Synced"
          ? `${updated.fullName} quedó al día en sus equipos.`
          : "Se volvió a escribir; revise el detalle de cada equipo.", updated.syncState === "Failed");
        loadAccessPersons(levels);
      } catch (err) {
        $("#psd-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
        button.disabled = false;
        button.textContent = "Volver a escribir";
      }
    });
  };

  render(person);
}

async function renderAccessPersons() {
  $("#page-title").textContent = "Control de acceso · Personas";
  const isAdmin = Perms.can("persons.manage");
  let levels, departments;
  try {
    [levels, departments] = await Promise.all([
      Api.get("/api/access/levels"), Api.get("/api/access/departments"),
    ]);
  } catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Personas</h3>
      <div class="row-actions">
        ${isAdmin ? `<button class="btn ghost" id="btn-person-sync"
          title="Escribe ahora en los equipos todo lo que esté pendiente">Escribir pendientes</button>
        <button class="btn ghost" id="btn-person-resend"
          title="Reescribe el padrón COMPLETO en todos los equipos, aunque el VMS los crea al día">Reenviar todo</button>
        <button class="btn" id="btn-person-new">Agregar persona</button>` : ""}
      </div>
    </div>
    <div id="access-sync-progress" class="hidden"></div>
    <div class="filter-bar">
      <div class="field"><label>Buscar</label>
        <input id="pf-q" placeholder="nombre, identificador o tarjeta…" value="${esc(accessPersonState.q)}"></div>
      <div class="field"><label>Departamento</label>
        <select id="pf-department"><option value="">Todos</option>
          ${departments.map((d) => `<option ${accessPersonState.department === d ? "selected" : ""}>${esc(d)}</option>`).join("")}
        </select></div>
      <div class="field"><label>Nivel de acceso</label>
        <select id="pf-level"><option value="">Todos</option>
          ${levels.map((l) => `<option value="${l.id}" ${String(accessPersonState.levelId) === String(l.id) ? "selected" : ""}>${esc(l.name)}</option>`).join("")}
        </select></div>
      <div class="field"><label>En los equipos</label>
        <select id="pf-state"><option value="">Todos</option>
          ${Object.entries(ACCESS_SYNC_STATES).map(([key, s]) =>
            `<option value="${key}" ${accessPersonState.state === key ? "selected" : ""}>${esc(s.label)}</option>`).join("")}
        </select></div>
      <div class="filter-actions">
        <button class="btn" id="pf-search">Buscar</button>
        <button class="btn ghost" id="pf-clear">Limpiar</button>
      </div>
    </div>
    <div id="access-person-results"><div class="info-box">Cargando…</div></div>`;

  const readFilters = () => {
    accessPersonState.q = $("#pf-q").value.trim();
    accessPersonState.department = $("#pf-department").value;
    accessPersonState.levelId = $("#pf-level").value;
    accessPersonState.state = $("#pf-state").value;
    accessPersonState.page = 1;
  };
  $("#pf-search").addEventListener("click", () => { readFilters(); loadAccessPersons(levels); });
  $("#pf-q").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { readFilters(); loadAccessPersons(levels); }
  });
  $("#pf-clear").addEventListener("click", () => {
    Object.assign(accessPersonState, { page: 1, q: "", department: "", levelId: "", state: "" });
    renderAccessPersons();
  });
  $("#btn-person-new")?.addEventListener("click", () => accessPersonModal(null, levels));
  $("#btn-person-resend")?.addEventListener("click", (e) => resendAccessPadron(e.currentTarget, levels));
  $("#btn-person-sync")?.addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    button.textContent = "Escribiendo…";
    try {
      const result = await Api.post("/api/access/sync");
      toast(result.processed
        ? `${result.processed} persona(s) escritas en los equipos.`
        : "No había nada pendiente.");
      loadAccessPersons(levels);
    } catch (err) { toast(err.error, true); }
    finally { button.disabled = false; button.textContent = "Escribir pendientes"; }
  });

  await loadAccessPersons(levels);

  // La barra de avance se sondea cada 3 s mientras la página esté a la vista;
  // cuando la pasada avanzó, la tabla se repinta sola (sin modal abierto).
  accessSyncLastDone = null;
  await refreshAccessSyncProgress(levels);
  clearInterval(accessSyncTimer);
  accessSyncTimer = setInterval(() => {
    if (!accessPollGuard("#/access-persons", accessSyncTimer)) return;
    refreshAccessSyncProgress(levels);
  }, 3000);
}

function accessDuration(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return "";
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const m = Math.floor(seconds / 60);
  if (m < 60) return `${m} min`;
  return `${Math.floor(m / 60)} h ${m % 60} min`;
}

/** Franja con el avance de la escritura en los equipos. Se oculta cuando no hay nada pendiente ni fallido. */
async function refreshAccessSyncProgress(levels) {
  const box = $("#access-sync-progress");
  if (!box) return;
  let s;
  try { s = await Api.get("/api/access/sync/status"); } catch { return; }
  const p = s.progress;
  const busy = p.running;
  const show = busy || s.pending > 0 || s.failed > 0;

  if (!show) {
    box.classList.add("hidden");
    box.innerHTML = "";
  } else {
    const total = Math.max(p.total, 1);
    const pct = busy ? Math.round((100 * p.done) / total) : (s.pending > 0 ? 0 : 100);
    const eta = busy && p.secondsPerPerson ? accessDuration((p.total - p.done) * p.secondsPerPerson) : "";
    const title = busy ? "Escribiendo en los equipos"
      : s.pending > 0 ? "Pendiente de escribir en los equipos"
      : "Escritura terminada";
    const detail = busy
      ? `${p.done} de ${p.total} persona${p.total === 1 ? "" : "s"}${eta ? ` · faltan ${eta}` : ""}`
      : `${s.pending} pendiente${s.pending === 1 ? "" : "s"}`;
    const failed = s.failed
      ? ` · <a href="#" id="sync-show-failed" title="Ver solo las personas con error">${s.failed} con error</a>` : "";
    box.classList.remove("hidden");
    box.innerHTML = `
      <div class="sync-strip${busy ? " running" : ""}">
        <div class="sync-head"><strong>${title}</strong><span class="muted">${detail}${failed}</span></div>
        <div class="sync-bar"><div class="sync-fill" style="width:${pct}%"></div></div>
        ${busy && p.currentPerson
          ? `<div class="muted sync-now">Ahora: ${esc(p.currentPerson)}${p.currentDevice ? ` → ${esc(p.currentDevice)}` : ""}</div>`
          : (!busy && s.pending > 0
            ? `<div class="muted sync-now">Se escriben solas en la próxima pasada (o pulse «Escribir pendientes»).</div>` : "")}
      </div>`;
    $("#sync-show-failed")?.addEventListener("click", (e) => {
      e.preventDefault();
      accessPersonState.state = "Failed";
      accessPersonState.page = 1;
      const select = $("#pf-state");
      if (select) select.value = "Failed";
      loadAccessPersons(levels);
    });
  }

  // Los botones de escritura esperan a que termine la pasada en curso.
  const syncButton = $("#btn-person-sync");
  const resendButton = $("#btn-person-resend");
  if (syncButton) syncButton.disabled = busy;
  if (resendButton) resendButton.disabled = busy;

  // Repintar la tabla solo cuando la pasada avanzó (y no bajo un modal).
  const mark = `${p.done}/${p.running}/${s.pending}`;
  if (accessSyncLastDone !== null && mark !== accessSyncLastDone &&
      $("#modal-backdrop").classList.contains("hidden")) loadAccessPersons(levels);
  accessSyncLastDone = mark;
}

/**
 * Reenvía el padrón COMPLETO a todos los equipos. Distinto de "Escribir
 * pendientes": no mira si algo cambió, reescribe igual. Es lo que hace falta
 * cuando el equipo perdió lo suyo sin que el VMS se entere (se reemplazó el
 * terminal, se lo volvió a fábrica, o alguien borró personas desde su pantalla).
 */
async function resendAccessPadron(button, levels) {
  const aviso = [
    "¿Reenviar el padrón completo a TODOS los equipos?",
    "",
    "Se reescriben todas las personas, sus credenciales y sus horarios, aunque el VMS ya los dé por",
    "escritos. Con muchas personas puede demorar un rato.",
  ].join("\n");
  if (!confirm(aviso)) return;
  const label = button.textContent;
  button.disabled = true;
  button.textContent = "Reenviando…";
  try {
    const r = await Api.post("/api/access/sync/full");
    toast(r.resent
      ? `Reenvío terminado: ${r.processed} de ${r.resent} persona(s) escritas. Revise la columna "En los equipos".`
      : "No hay personas con niveles de acceso que reenviar.");
    loadAccessPersons(levels);
  } catch (err) {
    toast(err.error, true);
  } finally {
    button.disabled = false;
    button.textContent = label;
  }
}

async function loadAccessPersons(levels) {
  const box = $("#access-person-results");
  if (!box) return;
  const isAdmin = Perms.can("persons.manage");
  const query = new URLSearchParams({ page: accessPersonState.page, pageSize: 50 });
  if (accessPersonState.q) query.set("q", accessPersonState.q);
  if (accessPersonState.department) query.set("department", accessPersonState.department);
  if (accessPersonState.levelId) query.set("levelId", accessPersonState.levelId);
  if (accessPersonState.state) query.set("state", accessPersonState.state);

  let data;
  try { data = await Api.get(`/api/access/persons?${query}`); }
  catch (err) { box.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  if (!data.items.length) {
    box.innerHTML = `<div class="info-box">No hay personas que coincidan con los filtros.</div>`;
    return;
  }

  const totalPages = Math.max(1, Math.ceil(data.total / data.pageSize));
  box.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>Persona</th><th>Identificador</th><th>Departamento</th><th>Credenciales</th>
        <th>Niveles de acceso</th><th>Vigencia</th><th>En los equipos</th><th></th>
      </tr></thead>
      <tbody>
        ${data.items.map((p) => `
          <tr data-id="${p.id}">
            <td>${esc(p.fullName)}${p.enabled ? "" : ` <span class="tag operator">desactivada</span>`}
              ${p.position ? `<div class="muted" style="font-size:11px">${esc(p.position)}</div>` : ""}</td>
            <td class="muted">${esc(p.employeeNo)}</td>
            <td class="muted">${esc(p.department ?? "—")}</td>
            <td class="muted">${esc(accessCredentialSummary(p)) || "—"}</td>
            <td>${p.levelNames.length
              ? `<span title="${esc(p.levelNames.join(", "))}">${esc(p.levelNames.join(", "))}</span>`
              : `<span class="tag off" title="Sin niveles no entra por ninguna puerta">ninguno</span>`}</td>
            <td class="muted" style="white-space:nowrap" title="Desde ${accessDay(p.validFrom)}">${accessDay(p.validTo)}</td>
            <td>${accessSyncTag(p)}</td>
            <td><div class="row-actions" style="flex-wrap:wrap">
              ${isAdmin ? `
                <button class="btn ghost btn-person-edit">Editar</button>
                ${p.syncState === "Failed" || p.syncState === "Pending"
                  ? `<button class="btn ghost btn-person-retry" title="Volver a escribirla en sus equipos">Reintentar</button>` : ""}
                <button class="btn danger btn-person-delete">Eliminar</button>` : ""}
            </div></td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    <div class="audit-pager">
      <span class="muted">${data.total} persona${data.total === 1 ? "" : "s"} · página ${data.page} de ${totalPages}</span>
      <div style="display:flex;gap:8px">
        <button class="btn ghost" id="person-prev" ${data.page <= 1 ? "disabled" : ""}>« Anterior</button>
        <button class="btn ghost" id="person-next" ${data.page >= totalPages ? "disabled" : ""}>Siguiente »</button>
      </div>
    </div>`;

  const byRow = (e) => data.items.find((p) => p.id === Number(e.target.closest("tr").dataset.id));
  $("#person-prev")?.addEventListener("click", () => { accessPersonState.page--; loadAccessPersons(levels); });
  $("#person-next")?.addEventListener("click", () => { accessPersonState.page++; loadAccessPersons(levels); });
  $$("#access-person-results .btn-person-edit").forEach((b) =>
    b.addEventListener("click", (e) => accessPersonModal(byRow(e), levels)));
  $$("#access-person-results .btn-person-sync-detail").forEach((b) =>
    b.addEventListener("click", (e) => accessPersonSyncModal(byRow(e), levels)));
  $$("#access-person-results .btn-person-retry").forEach((b) => b.addEventListener("click", async (e) => {
    const person = byRow(e);
    const button = e.currentTarget;
    button.disabled = true;
    button.textContent = "Escribiendo…";
    try {
      const updated = await Api.post(`/api/access/persons/${person.id}/sync`);
      toast(updated.syncState === "Synced"
        ? `${updated.fullName} quedó al día en sus equipos.`
        : `Sigue pendiente: ${updated.syncError ?? "sin detalle"}`, updated.syncState === "Failed");
      loadAccessPersons(levels);
    } catch (err) { toast(err.error, true); button.disabled = false; button.textContent = "Reintentar"; }
  }));
  $$("#access-person-results .btn-person-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const person = byRow(e);
    if (!confirm(`¿Eliminar a "${person.fullName}" del padrón? Se le borra también de los equipos donde esté.`)) return;
    try {
      await Api.delete(`/api/access/persons/${person.id}`);
      toast("Persona eliminada.");
      loadAccessPersons(levels);
    } catch (err) { toast(err.error, true); }
  }));
}

/** Solo la fecha, en hora local: la vigencia se piensa en días, no en horas. */
const accessDay = (iso) => (iso ? new Date(iso).toLocaleDateString("es-CL") : "—");

/**
 * Fecha → "YYYY-MM-DDTHH:mm:ss" en hora LOCAL para un <input type=datetime-local>.
 * No usar toISOString: va en UTC, y en Chile un fin a las 23:59:59 se leería
 * como el día siguiente (y cada edición correría la vigencia un día).
 */
const accessDateInput = (date) => {
  const d = new Date(date);
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
    + `T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
};

/** Vigencia por defecto de una persona nueva: hoy 00:00:00 al 31/12/2099 23:59:59. */
const accessDefaultValidFrom = () => { const d = new Date(); d.setHours(0, 0, 0, 0); return d; };
const accessDefaultValidTo = () => new Date(2099, 11, 31, 23, 59, 59);

/**
 * Alta y edición de una persona, en tres pasos: primero QUIÉN ES, después CON
 * QUÉ SE IDENTIFICA y al final POR DÓNDE PASA. De una sola vez son veinte
 * campos y un muro; separados, cada paso es una pregunta con respuesta obvia.
 *
 * Lo que el operador escribe vive en `draft`, no en el DOM: al cambiar de paso
 * se redibuja el cuerpo del modal y sin eso se perderían los campos del paso
 * anterior. Se guarda UNA sola vez, al final: así cancelar a mitad de camino no
 * deja personas a medio crear.
 */
function accessPersonModal(person, levels) {
  const isNew = !person;
  const draft = {
    firstName: person?.firstName ?? "",
    lastName: person?.lastName ?? "",
    employeeNo: person?.employeeNo ?? "",
    department: person?.department ?? "",
    position: person?.position ?? "",
    email: person?.email ?? "",
    phone: person?.phone ?? "",
    notes: person?.notes ?? "",
    validFrom: accessDateInput(person?.validFrom ?? accessDefaultValidFrom()),
    validTo: accessDateInput(person?.validTo ?? accessDefaultValidTo()),
    enabled: person ? person.enabled : true,
    pin: "",
    clearPin: false,
    cards: (person?.cards ?? []).map((c) => c.number),
    // Huella: la plantilla solo existe acá si se acaba de capturar. Las que ya
    // estaban vienen sin plantilla (el servidor nunca la devuelve) y viajan
    // así, que es como se le dice "dejá la que tenés".
    fingerprints: (person?.fingerprints ?? []).map((f) => ({
      number: f.number, name: f.name, quality: f.quality, source: f.source, template: null, isNew: false,
    })),
    levelIds: new Set(person?.levelIds ?? []),
    // El rostro: `image` en base64 solo cuando se acaba de elegir una foto
    // nueva; `has` dice si la persona ya tiene una guardada, que no se
    // descarga salvo para mostrarla.
    face: {
      has: !!person?.face,
      bytes: person?.face?.bytes ?? 0,
      image: null,
      contentType: null,
      clear: false,
    },
  };

  const STEPS = [
    { title: "Datos de la persona", hint: "Quién es" },
    { title: "Credenciales", hint: "Con qué se identifica" },
    { title: "Accesos", hint: "Por dónde y cuándo pasa" },
  ];
  const LAST = STEPS.length - 1;
  let step = 0;
  let visited = 0;   // hasta qué paso llegó: no se salta a uno que no vio
  // Dedo elegido en las manos de "Credenciales" (el que muestra la ficha):
  // de entrada, el primero sugerido que falta.
  let selectedFinger = accessSuggestedFinger(draft.fingerprints, null) ?? ACCESS_FINGER_ORDER[0];

  // ---- Cuerpo de cada paso ------------------------------------------------

  const stepDatos = () => `
    <div class="form-grid">
      <div class="field">
        <label>Nombre</label>
        <input id="pw-first" maxlength="64" value="${esc(draft.firstName)}" autofocus>
      </div>
      <div class="field">
        <label>Apellido</label>
        <input id="pw-last" maxlength="64" value="${esc(draft.lastName)}">
      </div>
    </div>
    <div class="form-grid">
      <div class="field">
        <label>Identificador en los equipos${isNew ? " (opcional)" : ""}</label>
        <input id="pw-employee" maxlength="32" value="${esc(draft.employeeNo)}" ${isNew ? "" : "disabled"}
          placeholder="lo asigna el sistema">
        <div class="muted" style="font-size:12px;margin-top:4px">
          ${isNew ? "Si lo deja vacío, el sistema le pone el siguiente número libre."
                  : "No se puede cambiar: es con lo que los equipos la reconocen."}
        </div>
      </div>
      <div class="field">
        <label>Departamento</label>
        <input id="pw-department" maxlength="128" value="${esc(draft.department)}" placeholder="Operaciones, Casino…">
      </div>
    </div>
    <div class="form-grid">
      <div class="field">
        <label>Cargo (opcional)</label>
        <input id="pw-position" maxlength="128" value="${esc(draft.position)}">
      </div>
      <div class="field">
        <label>Teléfono (opcional)</label>
        <input id="pw-phone" maxlength="32" value="${esc(draft.phone)}">
      </div>
    </div>
    <div class="form-grid">
      <div class="field">
        <label>Correo (opcional)</label>
        <input id="pw-email" type="email" maxlength="255" value="${esc(draft.email)}">
      </div>
    </div>
    <div class="field">
      <label>Vigencia</label>
      <div style="display:flex;gap:8px;align-items:center">
        <input id="pw-from" type="datetime-local" step="1" style="flex:1;min-width:0" value="${esc(draft.validFrom)}">
        <span class="muted">a</span>
        <input id="pw-to" type="datetime-local" step="1" style="flex:1;min-width:0" value="${esc(draft.validTo)}">
      </div>
      <div class="muted" style="font-size:12px;margin-top:4px">
        Los equipos la respetan solos: fuera de esas fechas y horas no la dejan pasar.
        Los terminales Hikvision llegan hasta el 31-12-2037: una vigencia posterior queda en ese tope.
      </div>
    </div>
    <div class="field">
      <label>Notas (opcional)</label>
      <input id="pw-notes" maxlength="1024" value="${esc(draft.notes)}">
    </div>
    <label class="checkbox-row"><input type="checkbox" id="pw-enabled" ${draft.enabled ? "checked" : ""}>
      Activa (al desactivarla se la borra de los equipos, pero se conserva su historial)</label>`;

  const cardRows = () => draft.cards.map((number, i) => `
    <span class="access-segment" data-i="${i}">
      <input class="pw-card" maxlength="32" value="${esc(number)}" placeholder="0012345678">
      <button type="button" class="btn ghost pw-card-del" title="Quitar esta tarjeta">✕</button>
    </span>`).join("") || `<span class="muted">Sin tarjetas</span>`;

  const stepCredenciales = () => `
    <div class="field">
      <label>Tarjetas</label>
      <div id="pw-cards">${cardRows()}</div>
      <div class="row-actions" style="margin-top:8px">
        <button type="button" class="btn ghost" id="pw-card-add">+ tarjeta</button>
        <button type="button" class="btn ghost" id="pw-card-read">Leer tarjeta en un lector</button>
      </div>
      <div class="muted" style="font-size:12px;margin-top:6px">
        El número tal como lo lee el equipo. Una tarjeta no puede estar en dos personas a la vez.
      </div>
    </div>
    <div class="form-grid">
      <div class="field">
        <label>Clave de teclado${person?.hasPin ? " (vacío = no cambiar)" : " (opcional)"}</label>
        <input id="pw-pin" type="password" inputmode="numeric" autocomplete="new-password"
          placeholder="4 a 8 dígitos" value="${esc(draft.pin)}">
      </div>
      <div class="field">
        <label>&nbsp;</label>
        ${person?.hasPin
          ? `<label class="checkbox-row"><input type="checkbox" id="pw-clear-pin" ${draft.clearPin ? "checked" : ""}>
               Quitarle la clave</label>`
          : `<div class="muted" style="font-size:12px">Solo si el terminal tiene teclado.</div>`}
      </div>
    </div>
    <div class="field">
      <label>Huellas <span class="muted" id="pw-finger-count">(${draft.fingerprints.length} de ${ACCESS_FINGERS.length})</span></label>
      <div id="pw-fingers">${fingerRows()}</div>
      <div class="muted" style="font-size:12px;margin-top:6px">
        Se capturan con el lector USB conectado a ESTE equipo, con el complemento de enrolamiento.
      </div>
    </div>
    <div class="field">
      <label>Rostro</label>
      <div id="pw-face">${faceBlock()}</div>
    </div>`;

  /**
   * El rostro: una foto que se manda a los terminales con cámara. El modelo lo
   * arma el propio equipo, así que acá solo se elige y se muestra la imagen; si
   * la cara no le sirve, el equipo lo dice al sincronizar y se ve en el estado
   * de la persona.
   */
  const faceBlock = () => {
    const preview = draft.face.image
      ? `<img src="data:${draft.face.contentType};base64,${draft.face.image}" alt="Rostro elegido">`
      : (draft.face.has && !draft.face.clear)
        // El token va en la query, como en los snapshots de las cámaras: un
        // <img> no manda la cabecera de sesión y el servidor devolvería 401.
        ? `<img src="/api/access/persons/${person.id}/face?access_token=${encodeURIComponent(Api.token)}" alt="Rostro enrolado">`
        : `<div class="access-face-empty">sin foto</div>`;
    const medida = draft.face.width
      ? ` <span class="muted">${draft.face.width}×${draft.face.height} px</span>` : "";
    const estado = draft.face.image
      ? `<span class="tag on" title="Elegida recién; se guarda al terminar">nueva</span>${medida}`
      : (draft.face.has && !draft.face.clear)
        ? `<span class="tag admin">enrolado</span>`
        : "";
    const puedeQuitar = draft.face.image || (draft.face.has && !draft.face.clear);
    return `
      <div class="access-face">
        <div class="access-face-photo">${preview}</div>
        <div class="access-face-actions">
          <div>${estado}${draft.face.clear ? `<span class="muted">se quitará al guardar</span>` : ""}</div>
          <input type="file" id="pw-face-file" accept="image/jpeg,image/png" hidden>
          <div class="row-actions">
            <button type="button" class="btn ghost" id="pw-face-pick">
              ${puedeQuitar ? "Cambiar foto" : "Elegir foto"}
            </button>
            ${puedeQuitar ? `<button type="button" class="btn ghost" id="pw-face-del" title="Quitarle el rostro">✕</button>` : ""}
          </div>
          <div class="muted" style="font-size:12px">
            JPG o PNG, de frente y bien iluminada, de al menos 300 px de lado.
            Solo va a los terminales con cámara.
          </div>
        </div>
      </div>`;
  };

  /** Las dos manos (se elige el dedo en el dibujo) y la ficha del dedo elegido. */
  const fingerRows = () => `
    <div class="hands-box">
      <div>
        ${accessHandsSvg(draft.fingerprints, selectedFinger)}
        <div class="hands-legend">
          <span><i class="has"></i>enrolada</span><span><i class="new"></i>recién capturada</span>
          <span><i></i>sin huella</span><span><i class="low"></i>calidad para mejorar</span>
        </div>
      </div>
      <div class="hand-detail" id="pw-finger-detail">${accessFingerDetailHtml(draft.fingerprints, selectedFinger)}</div>
    </div>
    ${draft.fingerprints.length < 2 ? `<div class="muted" style="font-size:12px;margin-top:8px">
      Conviene enrolar al menos dos dedos, uno de cada mano: si se lastima uno, la persona igual puede pasar.</div>` : ""}`;

  const stepAccesos = () => {
    const chosen = levels.filter((l) => draft.levelIds.has(l.id));
    const devices = !isNew && person.devices.length ? `
      <div class="field">
        <label>En los equipos</label>
        <div class="table-scroll"><table class="grid">
          <thead><tr><th>Equipo</th><th>Estado</th><th>Detalle</th><th>Última escritura</th></tr></thead>
          <tbody>${person.devices.map((d) => {
            const state = ACCESS_SYNC_STATES[d.state] ?? ACCESS_SYNC_STATES.NotApplicable;
            const { text, raw } = accessSplitSyncError(d.error);
            return `<tr>
              <td>${esc(d.deviceName)}</td>
              <td><span class="tag ${state.tag}">${esc(state.label)}</span></td>
              <td class="muted" style="max-width:280px">${esc(text || "—")}
                ${raw.map((r) => `<code class="sync-raw-inline">${esc(r)}</code>`).join("")}</td>
              <td class="muted">${d.syncedAt ? formatDateTime(d.syncedAt) : "—"}</td>
            </tr>`;
          }).join("")}</tbody>
        </table></div>
      </div>` : "";

    return `
      <div class="field">
        <label>Niveles de acceso
          <span class="muted" id="pw-level-count">(${chosen.length} elegido${chosen.length === 1 ? "" : "s"})</span></label>
        ${levels.length ? `<div class="wf-check-grid">
          ${levels.map((l) => `
            <label class="checkbox-row">
              <input type="checkbox" class="pw-level" value="${l.id}" ${draft.levelIds.has(l.id) ? "checked" : ""}>
              ${esc(l.name)} <span class="muted">· ${esc(l.scheduleName)}</span>
            </label>`).join("")}
        </div>` : `<div class="warn-box">No hay niveles de acceso todavía: cree uno en
          <a href="#/access-levels">Niveles</a>. Sin nivel, la persona no entra por ninguna puerta.</div>`}
      </div>
      <div id="pw-summary"></div>
      ${devices}`;
  };

  const BODIES = [stepDatos, stepCredenciales, stepAccesos];

  /** Enumeración natural: "A", "A y B", "A, B y C". */
  const enumerar = (items) => items.length <= 1 ? (items[0] ?? "")
    : `${items.slice(0, -1).join(", ")} y ${items.at(-1)}`;

  /** Lo que va a quedar, en una línea: es la última mirada antes de guardar. */
  const summary = () => {
    const chosen = levels.filter((l) => draft.levelIds.has(l.id));
    const nombre = `${draft.firstName} ${draft.lastName}`.trim() || "(sin nombre)";
    const tarjetas = draft.cards.filter((c) => c.trim()).length;
    const conClave = draft.pin.trim() ? true : (person?.hasPin && !draft.clearPin);
    const huellas = draft.fingerprints.length;
    const partes = [
      tarjetas ? `${tarjetas} ${tarjetas === 1 ? "tarjeta" : "tarjetas"}` : null,
      huellas ? `${huellas} ${huellas === 1 ? "huella" : "huellas"}` : null,
      conClave ? "clave de teclado" : null,
    ].filter(Boolean);
    const credenciales = partes.length
      ? `con ${enumerar(partes)}`
      : "aunque todavía no tiene con qué identificarse";
    return chosen.length
      ? `<div class="info-box"><b>${esc(nombre)}</b> va a entrar por las puertas de
           ${esc(enumerar(chosen.map((l) => l.name)))}, ${esc(credenciales)}.</div>`
      : `<div class="warn-box"><b>${esc(nombre)}</b> queda <b>sin ningún nivel de acceso</b>:
           no va a entrar por ninguna puerta. Puede agregárselos ahora o después.</div>`;
  };

  // ---- Guardar lo escrito antes de cambiar de paso ------------------------

  const readStep = () => {
    if (step === 0) {
      draft.firstName = $("#pw-first").value.trim();
      draft.lastName = $("#pw-last").value.trim();
      if (isNew) draft.employeeNo = $("#pw-employee").value.trim();
      draft.department = $("#pw-department").value.trim();
      draft.position = $("#pw-position").value.trim();
      draft.phone = $("#pw-phone").value.trim();
      draft.email = $("#pw-email").value.trim();
      draft.validFrom = $("#pw-from").value;
      draft.validTo = $("#pw-to").value;
      draft.notes = $("#pw-notes").value.trim();
      draft.enabled = $("#pw-enabled").checked;
    } else if (step === 1) {
      draft.cards = $$("#pw-cards .access-segment").map((s) => $(".pw-card", s).value.trim());
      draft.pin = $("#pw-pin").value;
      draft.clearPin = $("#pw-clear-pin")?.checked ?? false;
    } else {
      draft.levelIds = new Set($$(".pw-level:checked").map((c) => Number(c.value)));
    }
  };

  /** Lo que impide seguir desde el paso actual; null si está todo bien. */
  const validateStep = () => {
    if (step === 0) {
      if (!draft.firstName) return "El nombre es obligatorio.";
      if (!draft.lastName) return "El apellido es obligatorio.";
      if (!draft.validFrom || !draft.validTo) return "La vigencia necesita fecha de inicio y de término.";
      if (draft.validTo < draft.validFrom) return "El fin de la vigencia tiene que ser posterior a su comienzo.";
    }
    if (step === 1) {
      const cards = draft.cards.map((c) => c.replace(/\s+/g, "")).filter((c) => c);
      if (new Set(cards).size !== cards.length) return "La misma tarjeta está repetida en la lista.";
      const pin = draft.pin.trim();
      if (pin && (pin.length < 4 || pin.length > 8 || !/^\d+$/.test(pin)))
        return "La clave de teclado son entre 4 y 8 dígitos.";
    }
    return null;
  };

  // ---- Dibujado -----------------------------------------------------------

  const renderSteps = () => {
    $("#pw-steps").innerHTML = STEPS.map((s, i) => `
      <button type="button" class="wizard-step${i === step ? " active" : ""}${i < step ? " done" : ""}"
        data-step="${i}" ${i <= visited || !isNew ? "" : "disabled"}>
        <span class="wizard-step-n">${i + 1}</span>
        <span>
          <span class="wizard-step-title">${esc(s.title)}</span>
          <span class="wizard-step-hint">${esc(s.hint)}</span>
        </span>
      </button>`).join("");
    $$("#pw-steps .wizard-step").forEach((b) => b.addEventListener("click", () => goTo(Number(b.dataset.step))));
  };

  const renderActions = () => {
    const saveLabel = isNew ? "Agregar persona" : "Guardar cambios";
    $("#pw-actions").innerHTML = `
      <button class="btn ghost" type="button" id="pw-cancel">Cancelar</button>
      <div style="flex:1"></div>
      ${step > 0 ? `<button class="btn ghost" type="button" id="pw-back">« Atrás</button>` : ""}
      ${step < LAST ? `<button class="btn" type="button" id="pw-next">Siguiente »</button>` : ""}
      ${step === LAST || !isNew ? `<button class="btn" type="submit" id="pw-save">${saveLabel}</button>` : ""}`;
    $("#pw-cancel").addEventListener("click", closeModal);
    $("#pw-back")?.addEventListener("click", () => goTo(step - 1));
    $("#pw-next")?.addEventListener("click", () => goTo(step + 1));
  };

  const renderBody = () => {
    $("#pw-body").innerHTML = BODIES[step]();
    if (step === 1) {
      bindCards();
      bindFingers();
      bindFace();
      $("#pw-card-read")?.addEventListener("click", cardCaptureDialog);
    }
    if (step === 2) {
      $("#pw-summary").innerHTML = summary();
      $$(".pw-level").forEach((c) => c.addEventListener("change", () => {
        readStep();
        const count = draft.levelIds.size;
        $("#pw-level-count").textContent = `(${count} elegido${count === 1 ? "" : "s"})`;
        $("#pw-summary").innerHTML = summary();
      }));
    }
    $("#pw-body input")?.focus?.();
  };

  const bindCards = () => {
    $("#pw-card-add").addEventListener("click", () => {
      readStep();
      draft.cards.push("");
      $("#pw-cards").innerHTML = cardRows();
      bindCardRows();
      $$("#pw-cards .pw-card").at(-1)?.focus();
    });
    bindCardRows();
  };

  /** Redibuja solo el bloque de huellas (las tarjetas y la clave no se tocan). */
  const redrawFingers = () => {
    readStep();
    $("#pw-fingers").innerHTML = fingerRows();
    $("#pw-finger-count").textContent = `(${draft.fingerprints.length} de ${ACCESS_FINGERS.length})`;
    bindFingers();
  };

  const captureFinger = (number) => {
    const name = ACCESS_FINGERS[number - 1];
    const who = `${draft.firstName} ${draft.lastName}`.trim() || "la persona";
    // La captura abre su propia capa por encima del asistente: lo que ya se
    // llenó tiene que seguir ahí cuando el diálogo se cierre.
    fingerprintCaptureDialog(who, number, name, (result) => {
      readStep();
      const finger = { number, name, quality: result.quality, source: result.source,
                       template: result.template, isNew: true };
      const at = draft.fingerprints.findIndex((f) => f.number === number);
      if (at >= 0) draft.fingerprints[at] = finger; else draft.fingerprints.push(finger);
      draft.fingerprints.sort((a, b) => a.number - b.number);
      // Queda elegido el siguiente dedo sugerido que falta: se puede seguir
      // enrolando sin buscarlo. Con calidad baja se queda en el mismo, para
      // recapturarlo.
      if (result.quality >= 60) selectedFinger = accessSuggestedFinger(draft.fingerprints, number) ?? number;
      redrawFingers();
      toast(`${name} capturada (calidad ${result.quality}/100).`);
    });
  };

  /** Cambia el dedo elegido sin redibujar las manos (así el doble clic llega al mismo dedo). */
  const selectFinger = (number) => {
    selectedFinger = number;
    $$("#pw-fingers .hand-finger").forEach((g) => {
      const on = Number(g.dataset.finger) === number;
      g.classList.toggle("sel", on);
      g.setAttribute("aria-pressed", on ? "true" : "false");
    });
    $("#pw-finger-detail").innerHTML = accessFingerDetailHtml(draft.fingerprints, number);
    bindFingerDetail();
  };

  const bindFingerDetail = () => {
    $("#pw-finger-detail .pw-finger-capture")?.addEventListener("click", () => captureFinger(selectedFinger));
    $("#pw-finger-detail .pw-finger-del")?.addEventListener("click", () => {
      draft.fingerprints = draft.fingerprints.filter((f) => f.number !== selectedFinger);
      redrawFingers();
    });
  };

  const bindFingers = () => {
    $$("#pw-fingers .hand-finger").forEach((g) => {
      const number = Number(g.dataset.finger);
      g.addEventListener("click", () => selectFinger(number));
      g.addEventListener("dblclick", () => { selectFinger(number); captureFinger(number); });
      g.addEventListener("keydown", (e) => {
        if (e.key !== "Enter" && e.key !== " ") return;
        e.preventDefault();
        // Enter sobre el dedo ya elegido abre la captura; si no, lo elige.
        if (e.key === "Enter" && number === selectedFinger) captureFinger(number); else selectFinger(number);
      });
    });
    bindFingerDetail();
  };

  const redrawFace = () => {
    readStep();
    $("#pw-face").innerHTML = faceBlock();
    bindFace();
  };

  /** Tope de la foto, el mismo que valida el servidor (AccessFacePhoto). */
  const FACE_MAX_BYTES = 2 * 1024 * 1024;

  /** Lado mínimo por debajo del cual se avisa que el terminal puede rechazarla. */
  const FACE_MIN_SIDE = 300;

  const bindFace = () => {
    const file = $("#pw-face-file");
    $("#pw-face-pick")?.addEventListener("click", () => file.click());
    file?.addEventListener("change", () => {
      const chosen = file.files?.[0];
      if (!chosen) return;
      if (!["image/jpeg", "image/png"].includes(chosen.type)) {
        toast("La foto tiene que ser JPG o PNG.", true);
        return;
      }
      if (chosen.size > FACE_MAX_BYTES) {
        toast(`La foto pesa ${Math.round(chosen.size / 1024)} KB; el máximo es ${FACE_MAX_BYTES / 1024} KB.`, true);
        return;
      }
      const reader = new FileReader();
      reader.onload = () => {
        // Medida real de la imagen: los terminales rechazan las fotos chicas
        // (probado: 135x189 no la modela, la misma al doble sí). No se bloquea
        // —el umbral exacto lo pone cada equipo— pero se avisa antes de que el
        // rechazo aparezca recién al sincronizar.
        const probe = new Image();
        probe.onload = () => {
          draft.face.width = probe.naturalWidth;
          draft.face.height = probe.naturalHeight;
          if (Math.min(probe.naturalWidth, probe.naturalHeight) < FACE_MIN_SIDE)
            toast(`La foto es de ${probe.naturalWidth}×${probe.naturalHeight} px: puede ser chica ` +
                  `para el terminal. Si la rechaza, use una más grande.`, true);
          redrawFace();
        };
        probe.src = String(reader.result);
        // El lector entrega "data:image/jpeg;base64,XXXX": al servidor va solo
        // la parte de después de la coma.
        draft.face.image = String(reader.result).split(",")[1] ?? null;
        draft.face.contentType = chosen.type;
        draft.face.clear = false;
      };
      reader.onerror = () => toast("No se pudo leer la foto elegida.", true);
      reader.readAsDataURL(chosen);
    });
    $("#pw-face-del")?.addEventListener("click", () => {
      // Si la foto era nueva, alcanza con olvidarla; si ya estaba guardada,
      // hay que pedirle al servidor que la quite.
      draft.face.image = null;
      draft.face.contentType = null;
      draft.face.clear = draft.face.has;
      redrawFace();
    });
  };

  /**
   * Deja un terminal esperando que pasen una tarjeta y escribe su número en la
   * lista. Evita tipearlo a mano, que es de donde salen los números mal
   * copiados que después no abren la puerta.
   *
   * El equipo espera unos diez segundos por pedido y contesta "no vino nadie";
   * por eso se vuelve a pedir en un bucle mientras el diálogo esté abierto, y
   * se corta apenas se cierra.
   */
  const cardCaptureDialog = async () => {
    let devices = [];
    try {
      devices = (await Api.get("/api/access/devices"))
        .filter((d) => d.enabled && d.supportsCards && d.status === "Online");
    } catch {
      toast("No se pudo pedir la lista de equipos.", true);
      return;
    }
    if (!devices.length) {
      toast("No hay equipos de control de acceso en línea para leer la tarjeta.", true);
      return;
    }

    const root = document.createElement("div");
    root.className = "modal-backdrop";
    root.innerHTML = `
      <div class="modal" style="max-width:520px">
        <h3>Leer una tarjeta</h3>
        <div class="field">
          <label>¿En qué equipo?</label>
          <select id="cc-device">
            ${devices.map((d) => `<option value="${d.id}">${esc(d.name)}${d.location ? " · " + esc(d.location) : ""}</option>`).join("")}
          </select>
        </div>
        <div id="cc-state" class="muted" style="margin:10px 0">
          Elegí el equipo y apretá «Esperar tarjeta».
        </div>
        <div class="row-actions" style="justify-content:flex-end">
          <button type="button" class="btn ghost" id="cc-cancel">Cancelar</button>
          <button type="button" class="btn" id="cc-start">Esperar tarjeta</button>
        </div>
      </div>`;
    document.body.appendChild(root);

    let alive = true;
    const close = () => { alive = false; root.remove(); };
    $("#cc-cancel", root).addEventListener("click", close);

    $("#cc-start", root).addEventListener("click", async () => {
      const id = Number($("#cc-device", root).value);
      $("#cc-start", root).disabled = true;
      $("#cc-device", root).disabled = true;
      $("#cc-state", root).textContent = "Pasá la tarjeta por el lector del equipo…";

      while (alive) {
        let answer;
        try {
          answer = await Api.post(`/api/access/devices/${id}/capture-card`, {});
        } catch (err) {
          if (!alive) return;
          $("#cc-state", root).innerHTML = `<span class="error-box">${esc(err?.error || "El equipo no pudo leer la tarjeta.")}</span>`;
          $("#cc-start", root).disabled = false;
          $("#cc-device", root).disabled = false;
          return;
        }
        if (!alive) return;
        // Sin cuerpo = no pasó nadie: se vuelve a pedir, PERO con un respiro.
        // No todos los equipos esperan lo mismo: el DS-K1T321MFWX se queda unos
        // diez segundos con la petición abierta, y el DS-K1T804AMF V1.4.0
        // contesta al instante (medido: 843 respuestas en 140 s). Sin esta
        // pausa, con el segundo el panel le haría seis pedidos por segundo al
        // mismo terminal que está atendiendo eventos y sincronización.
        if (!answer?.cardNo) {
          await new Promise((r) => setTimeout(r, 1200));
          continue;
        }
        {
          readStep();
          if (draft.cards.some((c) => c.replace(/\s+/g, "") === answer.cardNo)) {
            $("#cc-state", root).textContent = `Esa tarjeta (${answer.cardNo}) ya está en la lista. Pasá otra…`;
            await new Promise((r) => setTimeout(r, 1200));
            continue;
          }
          draft.cards.push(answer.cardNo);
          $("#pw-cards").innerHTML = cardRows();
          bindCardRows();
          close();
          toast(`Tarjeta ${answer.cardNo} leída.`);
          return;
        }
      }
    });
  };

  const bindCardRows = () => $$("#pw-cards .pw-card-del").forEach((b) => b.addEventListener("click", (e) => {
    readStep();
    draft.cards.splice(Number(e.target.closest(".access-segment").dataset.i), 1);
    $("#pw-cards").innerHTML = cardRows();
    bindCardRows();
  }));

  /** Cambia de paso, sin dejar avanzar con el paso actual mal llenado. */
  const goTo = (target) => {
    readStep();
    const error = target > step ? validateStep() : null;
    if (error) { $("#pw-error").innerHTML = `<div class="error-box">${esc(error)}</div>`; return; }
    $("#pw-error").innerHTML = "";
    step = Math.max(0, Math.min(target, LAST));
    visited = Math.max(visited, step);
    renderSteps();
    renderBody();
    renderActions();
  };

  openModal(`
    <h3>${isNew ? "Agregar persona" : `Editar a ${esc(person.fullName)}`}</h3>
    <div id="pw-error"></div>
    <form id="access-person-form">
      <div class="wizard-steps" id="pw-steps"></div>
      <div id="pw-body"></div>
      <div class="modal-actions" id="pw-actions"></div>
    </form>`, "wider");

  renderSteps();
  renderBody();
  renderActions();

  $("#access-person-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    readStep();
    // Enter en un paso intermedio de un alta avanza en vez de guardar a medias.
    if (isNew && step < LAST) { goTo(step + 1); return; }

    // Al guardar se revisan TODOS los pasos, no solo el que está a la vista.
    const current = step;
    for (step = 0; step <= LAST; step++) {
      const error = validateStep();
      if (error) {
        goTo(step);
        $("#pw-error").innerHTML = `<div class="error-box">${esc(error)}</div>`;
        return;
      }
    }
    step = current;

    const body = {
      firstName: draft.firstName,
      lastName: draft.lastName,
      employeeNo: isNew ? (draft.employeeNo || null) : null,
      department: draft.department || null,
      position: draft.position || null,
      email: draft.email || null,
      phone: draft.phone || null,
      notes: draft.notes || null,
      // El día de fin se toma completo: vence al terminar esa jornada, no al empezarla.
      validFrom: new Date(draft.validFrom).toISOString(),
      validTo: new Date(draft.validTo).toISOString(),
      enabled: draft.enabled,
      pinCode: draft.pin.trim() || null,
      clearPin: draft.clearPin,
      // Sin espacios, igual que las guarda el servidor: los equipos devuelven
      // el número pegado y tiene que coincidir para reconocer a la persona.
      cards: draft.cards.map((c) => c.replace(/\s+/g, "")).filter((c) => c.length > 0),
      // template null = "dejá la que ya está"; un dedo ausente de la lista se borra.
      fingerprints: draft.fingerprints.map((f) => ({
        number: f.number, template: f.template, quality: f.quality, source: f.source,
      })),
      levelIds: [...draft.levelIds],
      // Rostro: solo viaja si se eligió una foto NUEVA (la guardada no se
      // descarga, así que no se puede reenviar); clearFace es para quitarlo.
      face: draft.face.image
        ? { image: draft.face.image, contentType: draft.face.contentType, source: "Archivo cargado desde el panel" }
        : null,
      clearFace: draft.face.clear,
    };
    const save = $("#pw-save");
    save.disabled = true;
    save.textContent = "Guardando…";
    try {
      if (isNew) await Api.post("/api/access/persons", body);
      else await Api.put(`/api/access/persons/${person.id}`, body);
      closeModal();
      toast(isNew ? "Persona agregada. Se está escribiendo en los equipos." : "Persona actualizada.");
      loadAccessPersons(levels);
    } catch (err) {
      $("#pw-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
      save.textContent = isNew ? "Agregar persona" : "Guardar cambios";
    }
  });
}

// El historial (buscador de registros de acceso y reportes) vive en
// access-monitor.js.
