// CLR TrueCentral VMS — panel: Sistema → Red → Hora (NTP).
//
// Una sola fuente de hora para todo: el servidor la toma de servidores NTP
// externos (o, en una red aislada, de su propio reloj), corrige el reloj de
// Windows —la base guarda todo en UTC con esa hora— y se la da a los equipos
// como servidor NTP. El horario de verano no viaja por NTP: lo aplica cada
// equipo con la zona que le escribe Hora y mantenimiento.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

let timeData = null;
let timeFetchedAt = 0;
let timeTick = null;
let timeRefresh = null;

async function renderTime() {
  $("#page-title").textContent = "Hora oficial (NTP)";
  try { await timeLoad(); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  timeDraw();

  clearInterval(timeTick);
  timeTick = setInterval(() => {
    if (location.hash !== "#/time") { clearInterval(timeTick); return; }
    $$("#view [data-time-clock]").forEach((el) => { el.textContent = timeClock(el.dataset.timeClock); });
  }, 1000);
  // El estado (última consulta, equipos que piden la hora) se refresca solo.
  clearInterval(timeRefresh);
  timeRefresh = setInterval(async () => {
    if (location.hash !== "#/time" || $("#app-shell").classList.contains("hidden")) { clearInterval(timeRefresh); return; }
    if (!$("#modal-backdrop").classList.contains("hidden") || $("#view").contains(document.activeElement)) return;
    try { await timeLoad(); timeDraw(); } catch { /* el próximo intento */ }
  }, 30000);
}

async function timeLoad() {
  const started = performance.now();
  timeData = await Api.get("/api/system/time");
  // La hora del servidor corresponde a la mitad del viaje de ida y vuelta.
  timeFetchedAt = (started + performance.now()) / 2;
}

/** "14:05:09" de la hora oficial, local de la zona ("local") o UTC ("utc"), avanzando con el reloj del navegador. */
function timeClock(kind) {
  if (!timeData) return "—";
  const elapsed = performance.now() - timeFetchedAt;
  const base = kind === "utc" ? Date.parse(timeData.status.serverUtc) : maintBaseMs(timeData.zone.localTime);
  const d = new Date(base + elapsed);
  const p = (n) => String(n).padStart(2, "0");
  return `${p(d.getUTCHours())}:${p(d.getUTCMinutes())}:${p(d.getUTCSeconds())}`;
}

function timeAgo(iso) {
  if (!iso) return "nunca";
  const s = Math.max(0, (Date.parse(timeData.status.serverUtc) - Date.parse(iso)) / 1000);
  if (s < 60) return "hace un momento";
  if (s < 3600) return `hace ${Math.round(s / 60)} min`;
  if (s < 86400) return `hace ${Math.round(s / 3600)} h`;
  return formatDateTime(iso);
}

function timeMs(ms) {
  if (ms == null) return "—";
  const a = Math.abs(ms);
  if (a < 1000) return `${ms > 0 ? "+" : ms < 0 ? "−" : ""}${a.toFixed(a < 10 ? 1 : 0)} ms`;
  return `${ms > 0 ? "+" : "−"}${(a / 1000).toFixed(a < 60000 ? 1 : 0)} s`;
}

/** Retardo: siempre positivo, sin signo. */
function timeDelay(ms) { return ms == null ? "—" : `${ms.toFixed(ms < 10 ? 1 : 0)} ms`; }

function timeDraw() {
  const t = timeData, s = t.status, st = t.settings, z = t.zone, l = s.listener, w = s.windowsTime;
  // Desfase del computador de quien mira, por curiosidad útil: si está muy corrido, sus fechas escritas a mano también.
  const browserSkew = Date.now() - (Date.parse(s.serverUtc) + (performance.now() - timeFetchedAt));

  const state = s.isolated
    ? { box: "warn-box", text: `<b>Red aislada.</b> Sin fuentes externas: los equipos reciben la hora del reloj de este servidor
        (estrato ${s.stratum}). Todos quedan con la misma hora, pero nadie la contrasta con la hora oficial.` }
    : s.synchronized
      ? { box: "info-box", text: `<b>Hora oficial sincronizada</b> con <b>${esc(s.syncSource)}</b> ${timeAgo(s.lastSyncUtc)}
          (estrato ${s.stratum} para los equipos).` }
      : { box: "warn-box", text: `<b>Sin hora oficial.</b> ${s.syncError ? esc(s.syncError) + "." : "Todavía no se consultó ninguna fuente."}
          ${st.serveWhenUnsynchronized ? "Los equipos reciben mientras tanto la hora del reloj del servidor." : "El servidor NTP no responde hasta obtenerla."}` };

  const warnings = [];
  if (w.installed && w.running)
    warnings.push(`<b>El servicio Hora de Windows está en marcha.</b> También corrige el reloj de este servidor y suele ocupar el puerto
      UDP 123: dos programas ajustando el mismo reloj se pelean.
      <div style="margin-top:6px"><button class="btn small" type="button" id="time-w32">Detener y deshabilitar Hora de Windows</button></div>`);
  else if (w.installed && !w.disabled && st.adjustSystemClock)
    warnings.push(`El servicio Hora de Windows está detenido pero no deshabilitado: Windows puede volver a iniciarlo solo.
      <a href="#" id="time-w32">Deshabilitarlo</a>`);
  if (l.enabled && !l.listening && l.error) warnings.push(`<b>Los equipos no pueden pedir la hora:</b> ${esc(l.error)}.`);
  if (s.adjustError) warnings.push(`<b>El reloj del servidor está corrido ${timeMs(s.lastOffsetMs)}</b> y no se pudo corregir:
    ${esc(s.adjustError)}. Los equipos reciben igual la hora correcta, pero lo que se guarda en la base lleva la hora de Windows.`);
  if (!st.adjustSystemClock && Math.abs(s.correctionMs) > 1000) warnings.push(`El reloj del servidor está corrido ${timeMs(s.correctionMs)}
    y está configurado para no corregirlo: lo que se guarda en la base no coincide con la hora oficial.`);
  if (!z.sameAsWindows) warnings.push(`La zona de los equipos (${esc(z.name)}) no es la de Windows en este servidor (${esc(z.windowsName)}).
    Los reportes y las fechas que arma el servidor usan la de Windows.`);
  if (l.port !== 123) warnings.push(`El servidor NTP atiende en el puerto UDP ${l.port} (configuración de pruebas): los equipos solo consultan el 123.`);

  const devicesUseServer = t.devices.mode === "Ntp" && t.devices.ntpFromServer;
  const devicesText = devicesUseServer ? "Toman la hora de este servidor por NTP"
    : t.devices.mode === "Ntp" ? `Toman la hora de otro servidor NTP (${esc(t.devices.ntpServer ?? "—")})`
    : "El VMS les fija la hora del servidor";
  const canPolicy = Perms.can("maintenance.manage");

  const transitions = z.transitions.length
    ? `<ul style="margin:6px 0 0 18px;padding:0">${z.transitions.map((x) => `<li>${esc(x.text)}</li>`).join("")}</ul>`
    : `<div class="muted">Esta zona no tiene horario de verano: la hora no cambia en el año.</div>`;

  $("#view").innerHTML = `
    <div class="${state.box}" style="margin-bottom:12px">${state.text}</div>
    ${warnings.map((x) => `<div class="warn-box" style="margin-bottom:12px">${x}</div>`).join("")}

    <div class="cards">
      <div class="card">
        <div class="card-label">Hora oficial</div>
        <div class="card-value" data-time-clock="local">${timeClock("local")}</div>
        <div class="muted" style="font-size:12px">${esc(z.offsetLabel)}${z.daylightNow ? " · horario de verano" : ""} ·
          <span data-time-clock="utc">${timeClock("utc")}</span> UTC</div>
      </div>
      <div class="card">
        <div class="card-label">Fuente</div>
        <div class="card-value small">${s.isolated ? "Reloj del servidor" : esc(s.syncSource ?? "—")}</div>
        <div class="muted" style="font-size:12px">${s.isolated ? "Red aislada"
          : `${s.syncAddress ? esc(s.syncAddress) + " · " : ""}retardo ${timeDelay(s.lastDelayMs)} · consultada ${timeAgo(s.lastAttemptUtc)}`}</div>
      </div>
      <div class="card">
        <div class="card-label">Reloj del servidor</div>
        <div class="card-value small">${s.adjustError ? "Corrido " + timeMs(s.lastOffsetMs) : s.lastOffsetMs == null ? "Sin medir" : "En hora"}</div>
        <div class="muted" style="font-size:12px">${s.adjustError ? "No se pudo corregir (ver el aviso)"
          : s.lastStepUtc ? `Última corrección: ${timeMs(s.lastStepMs)} ${timeAgo(s.lastStepUtc)}`
          : st.adjustSystemClock ? "No ha necesitado corrección" : "No se corrige (ajuste apagado)"}</div>
      </div>
      <div class="card">
        <div class="card-label">Servidor NTP para los equipos</div>
        <div class="card-value small">${!l.enabled ? "Apagado" : l.answering ? `Atendiendo en UDP ${l.port}` : l.listening ? "Sin dar la hora" : "Sin atender"}</div>
        <div class="muted" style="font-size:12px">${t.serverAddress ? `Dirección para los equipos: <b>${esc(t.serverAddress)}</b> · ` : ""}${l.served} respuesta${l.served === 1 ? "" : "s"}</div>
      </div>
    </div>

    <div class="card" style="max-width:1000px;margin-bottom:16px">
      <h4 style="margin:0 0 6px">Fuentes de la hora</h4>
      <div id="time-error"></div>
      <div class="form-grid">
        <div class="field">
          <label>Servidores NTP</label>
          <textarea id="time-sources" rows="4" spellcheck="false" placeholder="Vacío = red aislada">${esc(st.sources.join("\n"))}</textarea>
          <div class="muted field-hint">Uno por línea, nombre o IP. Se usa la mediana de lo que responden: una fuente equivocada no
            mueve la hora. <a href="#" id="time-defaults">Usar ${esc(t.defaultSources.join(" y "))}</a> (Hora Oficial de Chile del SHOA y el
            pool mundial). En una red sin Internet, déjelo vacío o ponga el NTP de la red.</div>
        </div>
        <div class="field">
          <label>Consultar cada (minutos)</label>
          <input id="time-poll" type="number" min="1" max="1440" value="${st.pollMinutes}">
          <label class="checkbox-row" style="margin-top:10px"><input type="checkbox" id="time-adjust" ${st.adjustSystemClock ? "checked" : ""}>
            <span>Corregir el reloj de este servidor <span class="muted">(recomendado: es la hora que se guarda en la base)</span></span></label>
          <label class="checkbox-row"><input type="checkbox" id="time-server" ${st.serverEnabled ? "checked" : ""}>
            <span>Dar la hora a los equipos por NTP (UDP 123)</span></label>
          <label class="checkbox-row"><input type="checkbox" id="time-unsync" ${st.serveWhenUnsynchronized ? "checked" : ""}>
            <span>Darla aunque no haya fuente disponible <span class="muted">(todos con la hora del servidor antes que cada uno con la suya)</span></span></label>
        </div>
      </div>
      <div class="modal-actions" style="justify-content:flex-start">
        <button class="btn" type="button" id="time-save">Guardar</button>
        <button class="btn ghost" type="button" id="time-sync" ${s.isolated ? "disabled" : ""}>Consultar ahora</button>
      </div>
      ${s.sources.length ? `
      <div class="table-scroll" style="margin-top:12px"><table class="grid">
        <thead><tr><th>Fuente</th><th>Dirección</th><th>Desfase</th><th>Retardo</th><th>Estrato</th><th>Estado</th></tr></thead>
        <tbody>${s.sources.map((r) => `<tr>
          <td>${esc(r.host)}</td><td>${esc(r.address ?? "—")}</td><td>${timeMs(r.offsetMs)}</td><td>${timeDelay(r.delayMs)}</td>

          <td>${r.stratum ?? "—"}</td>
          <td>${r.error ? `<span class="tag off">${esc(r.error)}</span>` : `<span class="tag on">Responde</span>`}</td></tr>`).join("")}
        </tbody></table></div>` : ""}
    </div>

    <div class="card" style="max-width:1000px;margin-bottom:16px">
      <h4 style="margin:0 0 6px">Zona horaria y cambio de hora</h4>
      <div><b>${esc(z.name)}</b> — ${esc(z.description)}.</div>
      <div style="margin-top:8px">Próximos cambios de hora:</div>
      ${transitions}
      <div class="muted" style="font-size:12px;margin-top:10px">NTP solo lleva la hora UTC; el cambio de hora lo hace cada equipo con su
        zona. Lo que guarda el sistema queda en UTC y se muestra en la hora local de quien lo mira, así que un cambio de hora no
        corre ni duplica registros. Si el gobierno cambia las fechas, basta con actualizar Windows en este servidor: la regla de la
        zona viene de ahí y el VMS se la vuelve a escribir a los equipos.</div>
    </div>

    <div class="card" style="max-width:1000px;margin-bottom:16px">
      <h4 style="margin:0 0 6px">Equipos</h4>
      <div>${devicesText}${devicesUseServer ? "" : "."} Zona: ${esc(z.description)}.
        ${t.devices.autoCorrect ? "La supervisión los corrige sola." : "La supervisión solo avisa."}
        <a href="#/device-maintenance">Hora y mantenimiento</a></div>
      ${!devicesUseServer && canPolicy ? `<div style="margin-top:8px">
        <button class="btn small" type="button" id="time-devices">Que los equipos tomen la hora de este servidor</button></div>` : ""}
      <div class="muted" style="font-size:12px;margin-top:8px">Hora y mantenimiento ajusta solo los equipos de control de acceso.
        A cámaras, grabadores, paneles y citofonía configúreles como servidor NTP
        ${t.serverAddress ? `<b>${esc(t.serverAddress)}</b>` : "la IP de este servidor"} (puerto 123) desde su página web.</div>
      <h4 style="margin:16px 0 6px">Equipos que pidieron la hora (últimas 24 h)</h4>
      ${t.clients.length ? `<div class="table-scroll"><table class="grid">
        <thead><tr><th>Dirección</th><th>Equipo</th><th>Último pedido</th><th>Pedidos</th></tr></thead>
        <tbody>${t.clients.map((c) => `<tr><td>${esc(c.address)}</td><td>${c.device ? esc(c.device) : `<span class="muted">No registrado</span>`}</td>
          <td>${timeAgo(c.lastUtc)}</td><td>${c.requests}</td></tr>`).join("")}</tbody></table></div>`
        : `<div class="muted">Ningún equipo ha pedido la hora todavía.</div>`}
    </div>
    ${Math.abs(browserSkew) > 2000 ? `<div class="muted" style="font-size:12px">El reloj de este computador está
      ${browserSkew > 0 ? "adelantado" : "atrasado"} ${timeMs(Math.abs(browserSkew))} respecto de la hora oficial.</div>` : ""}`;

  $("#time-defaults").addEventListener("click", (e) => { e.preventDefault(); $("#time-sources").value = t.defaultSources.join("\n"); });
  $("#time-save").addEventListener("click", timeSave);
  $("#time-sync").addEventListener("click", async (e) => {
    e.target.disabled = true;
    try { timeData = await Api.post("/api/system/time/sync"); timeFetchedAt = performance.now(); timeDraw(); toast("Hora oficial consultada."); }
    catch (err) { toast(err.error, true); e.target.disabled = false; }
  });
  $("#time-w32")?.addEventListener("click", timeDisableWindowsTime);
  $("#time-devices")?.addEventListener("click", timeDevicesUseServer);
}

async function timeSave() {
  const body = {
    sources: $("#time-sources").value.split(/[\r\n,;]+/).map((x) => x.trim()).filter(Boolean),
    adjustSystemClock: $("#time-adjust").checked,
    serverEnabled: $("#time-server").checked,
    serveWhenUnsynchronized: $("#time-unsync").checked,
    pollMinutes: Number($("#time-poll").value),
  };
  if (!body.serverEnabled && !confirm("¿Apagar el servidor NTP? Los equipos que toman la hora de este servidor se quedan sin fuente y su reloj empezará a correrse."))
    return;
  const button = $("#time-save");
  button.disabled = true;
  try {
    timeData = await Api.put("/api/system/time", body);
    timeFetchedAt = performance.now();
    timeDraw();
    toast("Ajustes de la hora guardados.");
  } catch (err) {
    $("#time-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    button.disabled = false;
  }
}

async function timeDisableWindowsTime(e) {
  e.preventDefault();
  if (!confirm("¿Detener y deshabilitar el servicio Hora de Windows (W32Time)?\n\nDesde ahora la hora de este servidor la maneja " +
    "solo TrueCentral. Para volver atrás: services.msc → Hora de Windows → Tipo de inicio Manual.")) return;
  try {
    timeData = await Api.post("/api/system/time/windows-time/disable");
    timeFetchedAt = performance.now();
    timeDraw();
    toast("Hora de Windows detenido y deshabilitado.");
  } catch (err) { toast(err.error, true); }
}

async function timeDevicesUseServer(e) {
  const button = e.target;
  if (!confirm("¿Que los equipos tomen la hora de este servidor por NTP?\n\nLa supervisión de Hora y mantenimiento les escribe " +
    "la dirección de este servidor (y la zona horaria) en su próxima vuelta. Los que no saben NTP siguen recibiendo la hora del servidor.")) return;
  button.disabled = true;
  try {
    const p = (await Api.get("/api/maintenance/clocks")).policy;
    await Api.put("/api/maintenance/clock-policy", {
      timeZoneId: p.timeZoneId, mode: "Ntp", ntpFromServer: true, ntpServer: p.ntpServer,
      ntpIntervalMinutes: p.ntpIntervalMinutes, autoCorrect: p.autoCorrect,
      thresholdSeconds: p.thresholdSeconds, checkMinutes: p.checkMinutes,
    });
    await timeLoad();
    timeDraw();
    toast("Listo: los equipos tomarán la hora de este servidor.");
  } catch (err) { toast(err.error, true); button.disabled = false; }
}
