// CLR TrueCentral VMS — Aplicaciones → ANPR (reconocimiento de patentes).
// Réplica en el panel de la pantalla LPR del cliente (Views\LprView.xaml):
// lista de lecturas a la izquierda y ficha de la seleccionada a la derecha,
// con la escena, el recuadro de la placa y todo lo que informó la cámara.
//
// El panel no escucha el hub: las lecturas nuevas se traen por sondeo cada
// pocos segundos (misma consulta con los filtros vigentes).
"use strict";

let anprTimer = null;
const ANPR_POLL_MS = 5000;
const anprState = { take: 100, follow: true };
let anprEvents = [];        // lecturas en pantalla, la más reciente primero
let anprSelectedId = null;
let anprSources = [];

/** Query string con los filtros vigentes. */
function anprFilterQuery() {
  const q = [];
  const add = (k, v) => { if (v) q.push(`${k}=${encodeURIComponent(v)}`); };
  add("deviceId", anprState.deviceId);
  add("plate", anprState.plate);
  add("from", anprState.from);
  add("to", anprState.to);
  q.push(`take=${anprState.take}`);
  return q.join("&");
}

/** URL de una foto de la lectura (el token va en la query: un <img> no manda cabeceras). */
function anprImageUrl(id, kind) {
  return `/api/anpr/events/${id}/${kind}?access_token=${encodeURIComponent(Api.token)}`;
}

/**
 * CapturedAt es la hora LOCAL del equipo y llega sin zona: se muestra tal
 * cual vino, sin pasar por Date (que la correría según el navegador).
 */
function anprParts(iso) {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?/.exec(iso || "");
  if (!m) return null;
  return { date: `${m[3]}-${m[2]}-${m[1]}`, time: `${m[4]}:${m[5]}:${m[6]}`, ms: (m[7] || "000").padEnd(3, "0").slice(0, 3) };
}
function anprTime(iso) { const p = anprParts(iso); return p ? `${p.time}.${p.ms}` : "—"; }
function anprDate(iso) { const p = anprParts(iso); return p ? p.date : "—"; }
function anprWeekday(iso) {
  const p = anprParts(iso);
  if (!p) return "";
  const [d, mo, y] = p.date.split("-").map(Number);
  return new Date(y, mo - 1, d).toLocaleDateString("es-CL", { weekday: "long" });
}

function anprSourceText(ev) {
  return ev.channelName && ev.channelName !== ev.deviceName ? `${ev.deviceName} · ${ev.channelName}` : ev.deviceName;
}
function anprConfidence(ev) { return ev.confidence < 0 ? "—" : `${ev.confidence}%`; }
function anprVehicleSummary(ev) {
  const parts = [];
  if (ev.vehicleType) parts.push(ev.vehicleType);
  if (ev.vehicleColor) parts.push(ev.vehicleColor.toLowerCase());
  if (ev.speedKmh != null) parts.push(`${ev.speedKmh} km/h`);
  return parts.join(" · ");
}

/** Pares etiqueta/valor de la ficha: solo lo que el equipo realmente envió (igual que el cliente). */
function anprDetailRows(ev) {
  const rows = [
    ["Fecha y hora del equipo", `${anprWeekday(ev.capturedAt)} ${anprDate(ev.capturedAt)} ${anprTime(ev.capturedAt)}`],
    ["Equipo", ev.deviceName],
    ["Canal", `${ev.channelName} (n.º ${ev.channelNumber})`],
    ["Confianza de lectura", anprConfidence(ev)],
  ];
  const add = (label, value) => { if (value != null && String(value).trim() !== "") rows.push([label, value]); };
  if (ev.charConfidences?.length) {
    add("Confianza por carácter", ev.charConfidences
      .map((c, i) => `${ev.plateNumber[i] ?? "?"} ${c}%`).join("  ·  "));
  }
  add("Tipo de vehículo", ev.vehicleType);
  add("Color del vehículo", ev.vehicleColor);
  add("Marca reconocida", ev.vehicleBrand);
  add("Observado en el vehículo", ev.vehicleAttributes);
  add("Velocidad", ev.speedKmh != null ? `${ev.speedKmh} km/h` : null);
  add("Largo del vehículo", ev.vehicleLengthCm != null ? `${(ev.vehicleLengthCm / 100).toLocaleString("es-CL", { maximumFractionDigits: 2 })} m` : null);
  add("Carril", ev.lane);
  add("Sentido", ev.direction);
  add("Disparo de la captura", ev.detectionMethod);
  add("Infracción", ev.violation);
  add("Color de la placa", ev.plateColor);
  add("Tipo de placa", ev.plateType);
  rows.push(["Recibido por el servidor", new Date(ev.receivedAt).toLocaleTimeString("es-CL")]);
  return rows;
}

// ---------------------------------------------------------------------------
// Recuadro de la placa sobre la escena (réplica de LprView.xaml.cs → ToFraction)
//
// El SDK declara el recuadro en 0..1, pero las cámaras ITS del parque lo
// entregan en PÍXELES DIVIDIDOS POR 1000. Se decide por evento: si algún
// borde pasa de 1, o los cuatro valores son milésimas exactas y el recuadro
// en píxeles cabe en la foto, son píxeles/1000; si no, fracción.
// ---------------------------------------------------------------------------
const ANPR_BOX_MARGIN_X = 0.30;
const ANPR_BOX_MARGIN_Y = 0.45;

function anprIsThousandth(v) { const s = v * 1000; return Math.abs(s - Math.round(s)) < 0.01; }

function anprPlateBox(ev, pixelWidth, pixelHeight) {
  let { plateX: bx, plateY: by, plateWidth: bw, plateHeight: bh } = ev;
  if (!(pixelWidth > 0 && pixelHeight > 0 && bw > 0 && bh > 0)) return null;
  const pixels = bx + bw > 1.001 || by + bh > 1.001 ||
    ([bx, by, bw, bh].every(anprIsThousandth) &&
      bx + bw <= pixelWidth / 1000 && by + bh <= pixelHeight / 1000);
  const sx = pixels ? 1000 / pixelWidth : 1;
  const sy = pixels ? 1000 / pixelHeight : 1;
  let w = bw * sx, h = bh * sy;
  const x = bx * sx - w * ANPR_BOX_MARGIN_X;
  const y = by * sy - h * ANPR_BOX_MARGIN_Y;
  w *= 1 + ANPR_BOX_MARGIN_X * 2;
  h *= 1 + ANPR_BOX_MARGIN_Y * 2;
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const left = clamp(x, 0, 1), top = clamp(y, 0, 1);
  w = clamp(w - (left - x), 0, 1 - left);
  h = clamp(h - (top - y), 0, 1 - top);
  return w > 0 && h > 0 ? { left, top, w, h } : null;
}

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
async function renderAnpr() {
  $("#page-title").textContent = "ANPR — Reconocimiento de patentes";

  try { anprSources = await Api.get("/api/anpr/sources"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const deviceOptions = anprSources
    .map((s) => `<option value="${s.deviceId}" ${String(anprState.deviceId) === String(s.deviceId) ? "selected" : ""}>${esc(s.deviceName)}${s.enabled ? "" : " (inactiva)"}</option>`)
    .join("");
  const takeOptions = [50, 100, 200, 500]
    .map((n) => `<option value="${n}" ${anprState.take === n ? "selected" : ""}>${n}</option>`).join("");

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Lecturas de patentes</h3>
      <div class="anpr-sources" id="anpr-sources"></div>
    </div>
    <div class="filter-bar">
      <div class="field"><label>Equipo</label>
        <select id="anf-device"><option value="">Todos</option>${deviceOptions}</select></div>
      <div class="field"><label>Patente</label>
        <input id="anf-plate" placeholder="parcial, ej. BB12" value="${esc(anprState.plate ?? "")}" autocomplete="off"></div>
      <div class="field"><label>Desde (hora del equipo)</label>
        <input type="datetime-local" id="anf-from" value="${esc(anprState.from ?? "")}"></div>
      <div class="field"><label>Hasta (hora del equipo)</label>
        <input type="datetime-local" id="anf-to" value="${esc(anprState.to ?? "")}"></div>
      <div class="field"><label>Mostrar</label>
        <select id="anf-take">${takeOptions}</select></div>
      <div class="filter-actions">
        <button class="btn" id="anf-search">Buscar</button>
        <button class="btn ghost" id="anf-clear" title="Quita todos los filtros">Limpiar</button>
      </div>
    </div>
    <div class="anpr-layout">
      <section class="anpr-list-panel">
        <div class="anpr-list-head">
          <span class="muted" id="anpr-count"></span>
          <label class="anpr-follow" title="Seleccionar automáticamente cada lectura nueva">
            <input type="checkbox" id="anpr-follow" ${anprState.follow ? "checked" : ""}> Seguir en vivo
          </label>
        </div>
        <div class="anpr-list" id="anpr-list"><div class="info-box">Cargando lecturas…</div></div>
      </section>
      <section class="anpr-detail" id="anpr-detail"></section>
    </div>`;

  renderAnprSources();

  const readFilters = () => {
    anprState.deviceId = $("#anf-device").value;
    anprState.plate = $("#anf-plate").value.trim();
    anprState.from = $("#anf-from").value || "";
    anprState.to = $("#anf-to").value || "";
    anprState.take = Number($("#anf-take").value) || 100;
  };
  const search = () => { readFilters(); anprSelectedId = null; loadAnprEvents(true); };
  $("#anf-search").addEventListener("click", search);
  $("#anf-plate").addEventListener("keydown", (e) => { if (e.key === "Enter") search(); });
  $("#anf-clear").addEventListener("click", () => {
    for (const k of ["deviceId", "plate", "from", "to"]) delete anprState[k];
    anprState.take = 100;
    anprSelectedId = null;
    renderAnpr();
  });
  $("#anpr-follow").addEventListener("change", (e) => { anprState.follow = e.target.checked; });

  // Flechas arriba/abajo recorren la lista, como en el cliente.
  $("#anpr-list").addEventListener("keydown", (e) => {
    if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    e.preventDefault();
    const i = anprEvents.findIndex((ev) => ev.id === anprSelectedId);
    const next = anprEvents[Math.min(anprEvents.length - 1, Math.max(0, i + (e.key === "ArrowDown" ? 1 : -1)))];
    if (next) selectAnprEvent(next.id, true);
  });

  await loadAnprEvents(true);

  clearInterval(anprTimer);
  anprTimer = setInterval(() => {
    // Con "Hasta" fijado la búsqueda mira al pasado: no hay nada nuevo que traer.
    if (!anprState.to) loadAnprEvents(false);
  }, ANPR_POLL_MS);
}

function renderAnprSources() {
  const box = $("#anpr-sources");
  if (!box) return;
  const active = anprSources.filter((s) => s.enabled);
  if (!active.length) {
    box.innerHTML = `<span class="tag warn" title="Active una cámara como fuente en Dispositivos → Fuentes de video">Sin cámaras activas como fuente</span>`;
    return;
  }
  box.innerHTML = active.map((s) => `
    <span class="anpr-source" title="${esc(s.live ? "Recibiendo lecturas" : (s.lastError || "Sin conexión de eventos con el equipo"))}${s.lastEventAt ? ` · última lectura ${esc(formatDateTime(s.lastEventAt))}` : ""}">
      <span class="dot ${s.live ? "ok" : "bad"}"></span>${esc(s.deviceName)}
    </span>`).join("");
}

/**
 * Trae las lecturas con los filtros vigentes. `reset` redibuja todo (nueva
 * búsqueda); el sondeo solo agrega arriba las que no estaban, para no
 * perder la selección ni recargar las miniaturas.
 */
async function loadAnprEvents(reset) {
  const list = $("#anpr-list");
  if (!list) return;
  let data;
  try {
    data = await Api.get(`/api/anpr/events?${anprFilterQuery()}`);
  } catch (err) {
    if (reset) list.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }
  if (!$("#anpr-list")) return; // se cambió de página mientras llegaba

  if (reset) {
    anprEvents = data;
    list.innerHTML = data.length
      ? data.map(anprRowHtml).join("")
      : `<div class="info-box">No hay lecturas que coincidan con los filtros.</div>`;
    bindAnprRows(list);
    const keep = data.find((ev) => ev.id === anprSelectedId) ?? data[0];
    if (keep) selectAnprEvent(keep.id, false);
    else renderAnprDetail(null);
  } else {
    const known = new Set(anprEvents.map((ev) => ev.id));
    const fresh = data.filter((ev) => !known.has(ev.id));
    if (fresh.length) {
      if (!anprEvents.length) list.innerHTML = "";
      list.insertAdjacentHTML("afterbegin", fresh.map(anprRowHtml).join(""));
      bindAnprRows(list);
      anprEvents = fresh.concat(anprEvents);
      // La lista no crece sin fin: se recorta a la cantidad pedida.
      while (anprEvents.length > anprState.take) {
        const gone = anprEvents.pop();
        $(`.anpr-row[data-id="${gone.id}"]`, list)?.remove();
      }
      if (anprState.follow || anprSelectedId == null) selectAnprEvent(fresh[0].id, false);
    }
  }
  const n = anprEvents.length;
  $("#anpr-count").textContent = `${n} lectura${n === 1 ? "" : "s"}${n >= anprState.take ? ` (últimas ${anprState.take})` : ""}`;
}

function anprRowHtml(ev) {
  const thumb = ev.hasPlateImage ? anprImageUrl(ev.id, "plate") : ev.hasSceneImage ? anprImageUrl(ev.id, "scene") : null;
  const summary = anprVehicleSummary(ev);
  return `
    <div class="anpr-row" data-id="${ev.id}" tabindex="-1">
      <div class="anpr-thumb">${thumb ? `<img src="${thumb}" alt="" loading="lazy">` : `<span class="muted">Sin foto</span>`}</div>
      <div class="anpr-row-main">
        <div class="anpr-row-top">
          <b class="anpr-plate-text">${esc(ev.plateNumber)}</b>
          <span class="muted">${anprConfidence(ev)}</span>
        </div>
        <div class="anpr-row-sub"><span>${anprTime(ev.capturedAt)}</span> <span class="muted">${esc(anprSourceText(ev))}</span></div>
        ${summary ? `<div class="anpr-row-sub muted">${esc(summary)}</div>` : ""}
      </div>
    </div>`;
}

function bindAnprRows(list) {
  $$(".anpr-row:not([data-bound])", list).forEach((row) => {
    row.dataset.bound = "1";
    row.addEventListener("click", () => {
      // Elegir a mano una lectura antigua suspende el seguimiento en vivo.
      if (anprEvents[0] && Number(row.dataset.id) !== anprEvents[0].id && anprState.follow) {
        anprState.follow = false;
        const cb = $("#anpr-follow");
        if (cb) cb.checked = false;
      }
      selectAnprEvent(Number(row.dataset.id), true);
    });
  });
}

function selectAnprEvent(id, focus) {
  anprSelectedId = id;
  $$("#anpr-list .anpr-row").forEach((r) => r.classList.toggle("selected", Number(r.dataset.id) === id));
  const row = $(`#anpr-list .anpr-row[data-id="${id}"]`);
  if (row) {
    row.scrollIntoView({ block: "nearest" });
    if (focus) row.focus({ preventScroll: true });
  }
  renderAnprDetail(anprEvents.find((ev) => ev.id === id) ?? null);
}

function renderAnprDetail(ev) {
  const box = $("#anpr-detail");
  if (!box) return;
  if (!ev) {
    delete box.dataset.id;
    box.innerHTML = `<div class="anpr-empty muted">Seleccione una lectura para ver el detalle.</div>`;
    return;
  }
  if (box.dataset.id === String(ev.id)) return; // ya está a la vista
  box.dataset.id = ev.id;

  const isAdmin = Api.role === "Admin";
  const fileBase = `${ev.plateNumber}_${anprDate(ev.capturedAt).split("-").reverse().join("")}_${anprTime(ev.capturedAt).slice(0, 8).replace(/:/g, "")}`;
  box.innerHTML = `
    <div class="anpr-detail-head">
      <div>
        <div class="anpr-plate-big">${esc(ev.plateNumber)}</div>
        <div class="muted">${anprDate(ev.capturedAt)} · ${anprTime(ev.capturedAt)} · ${esc(anprSourceText(ev))} · confianza ${anprConfidence(ev)}</div>
      </div>
      <div class="anpr-detail-actions">
        <button class="btn ghost" id="anpr-copy">Copiar patente</button>
        ${ev.hasSceneImage ? `<a class="btn ghost" href="${anprImageUrl(ev.id, "scene")}" download="${esc(fileBase)}_escena.jpg">Guardar escena</a>` : ""}
        ${ev.hasPlateImage ? `<a class="btn ghost" href="${anprImageUrl(ev.id, "plate")}" download="${esc(fileBase)}_placa.jpg">Guardar placa</a>` : ""}
        ${isAdmin ? `<button class="btn danger" id="anpr-delete" title="Elimina la lectura y sus fotos">Eliminar</button>` : ""}
      </div>
    </div>
    <div class="anpr-detail-body">
      <div class="anpr-scene">
        ${ev.hasSceneImage
          ? `<div class="anpr-scene-wrap"><img id="anpr-scene-img" src="${anprImageUrl(ev.id, "scene")}" alt="Escena"><div class="anpr-box hidden" id="anpr-box"></div></div>`
          : `<div class="anpr-empty muted">El equipo no envió la foto de la escena.</div>`}
      </div>
      <aside class="anpr-side">
        <div class="anpr-plate-img">
          ${ev.hasPlateImage ? `<img src="${anprImageUrl(ev.id, "plate")}" alt="Placa">` : `<span class="muted">Sin primer plano de la placa</span>`}
        </div>
        <dl class="anpr-facts">
          ${anprDetailRows(ev).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join("")}
        </dl>
      </aside>
    </div>`;

  // El recuadro se ubica en % de la foto: sigue a la imagen al cambiar el tamaño de la ventana.
  const img = $("#anpr-scene-img");
  if (img) {
    const place = () => {
      const b = anprPlateBox(ev, img.naturalWidth, img.naturalHeight);
      const el = $("#anpr-box");
      if (!b || !el) return;
      Object.assign(el.style, { left: `${b.left * 100}%`, top: `${b.top * 100}%`, width: `${b.w * 100}%`, height: `${b.h * 100}%` });
      el.classList.remove("hidden");
    };
    if (img.complete && img.naturalWidth) place(); else img.addEventListener("load", place);
    img.addEventListener("click", () => window.open(img.src, "_blank"));
  }

  $("#anpr-copy").addEventListener("click", async () => {
    try { await navigator.clipboard.writeText(ev.plateNumber); toast(`Patente ${ev.plateNumber} copiada.`); }
    catch { toast("No se pudo copiar al portapapeles.", true); }
  });
  $("#anpr-delete")?.addEventListener("click", async () => {
    if (!confirm(`¿Eliminar la lectura de ${ev.plateNumber} y sus fotos? No se puede deshacer.`)) return;
    try {
      await Api.delete(`/api/anpr/events/${ev.id}`);
      const i = anprEvents.findIndex((x) => x.id === ev.id);
      anprEvents.splice(i, 1);
      $(`#anpr-list .anpr-row[data-id="${ev.id}"]`)?.remove();
      delete $("#anpr-detail").dataset.id;
      const next = anprEvents[Math.min(i, anprEvents.length - 1)];
      if (next) selectAnprEvent(next.id, false); else renderAnprDetail(null);
      toast("Lectura eliminada.");
    } catch (err) { toast(err.error, true); }
  });
}
