// Dispositivos → Hora y mantenimiento: el reloj de cada equipo (hora, zona,
// horario de verano, NTP y desfase), la política que aplica la supervisión,
// poner en hora (uno o en bloque), reiniciar y restablecer.
"use strict";

let maintTimer = null;   // refresco de los datos (cada 15 s)
let maintTick = null;    // los relojes avanzan solos, segundo a segundo
let maintData = null;    // la última respuesta de /api/maintenance/clocks
let maintFetchedAt = 0;  // performance.now() de esa respuesta
const maintSelected = new Set(); // "access:12"

const MAINT_KIND_LABELS = { access: "Control de acceso" };

const MAINT_HEALTH = {
  ok: { label: "En hora", tag: "on" },
  drift: { label: "Desfasado", tag: "warn" },
  zone: { label: "Otra zona horaria", tag: "warn" },
  error: { label: "No se pudo leer", tag: "off" },
  offline: { label: "Sin conexión", tag: "operator" },
  unknown: { label: "Sin leer todavía", tag: "operator" },
  unsupported: { label: "No disponible", tag: "operator" },
};

const maintKey = (d) => `${d.kind}:${d.id}`;

/** "2026-10-07T13:42:10" → milisegundos tratando la hora como UTC (solo para sumarle segundos y mostrarla). */
function maintBaseMs(text) {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})/.exec(text || "");
  return m ? Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]) : null;
}

/** La hora "de pared" que corresponde a esa base más lo que pasó desde que se leyó. */
function maintWallClock(baseMs) {
  if (baseMs == null) return "—";
  const d = new Date(baseMs + (performance.now() - maintFetchedAt));
  const p = (n) => String(n).padStart(2, "0");
  return `${p(d.getUTCDate())}-${p(d.getUTCMonth() + 1)}-${d.getUTCFullYear()} ` +
         `${p(d.getUTCHours())}:${p(d.getUTCMinutes())}:${p(d.getUTCSeconds())}`;
}

/** "3 min 20 s", "11 h". */
function maintSpan(seconds) {
  const s = Math.abs(Math.round(seconds));
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return s % 60 ? `${m} min ${s % 60} s` : `${m} min`;
  const h = Math.floor(m / 60);
  if (h < 48) return m % 60 ? `${h} h ${m % 60} min` : `${h} h`;
  return `${Math.floor(h / 24)} días`;
}

function maintDriftHtml(d, threshold) {
  if (d.driftSeconds == null) return `<span class="muted">—</span>`;
  const s = d.driftSeconds;
  if (Math.abs(s) < 1) return `<span class="muted">al segundo</span>`;
  const late = s < 0;
  const over = Math.abs(s) > threshold;
  return `<span class="${over ? "tag warn" : "muted"}" title="${late ? "Atrasado" : "Adelantado"} respecto de la hora real">
    ${late ? "−" : "+"}${esc(maintSpan(s))}</span>`;
}

/** "UTC−4 con horario de verano (+1 h desde …)" → lo de antes del paréntesis; el resto va al tooltip. */
function maintZoneHtml(description, fallback = "zona no informada") {
  if (!description) return esc(fallback);
  const short = description.split(" (")[0];
  return short === description ? esc(description) : `<span title="${esc(description)}">${esc(short)}</span>`;
}

function maintSourceText(clock) {
  if (!clock) return "—";
  if (clock.mode === "Ntp") return `NTP ${clock.ntpServer ? esc(clock.ntpServer) : ""}${clock.ntpIntervalMinutes ? ` · cada ${clock.ntpIntervalMinutes} min` : ""}`;
  if (clock.mode === "Manual") return "Fijada (sin NTP)";
  return "—";
}

function maintHealthHtml(d) {
  const h = MAINT_HEALTH[d.health] ?? MAINT_HEALTH.unknown;
  const detail = d.health === "zone" ? d.zoneProblem
    : d.health === "error" ? d.error
    : null;
  return `<span class="tag ${h.tag}">${esc(h.label)}</span>${detail
    ? `<div class="muted" style="font-size:11px;max-width:260px">${esc(detail)}</div>` : ""}`;
}

function maintLastCorrectionHtml(d) {
  if (!d.lastCorrectionAt) return "";
  return `<div class="muted" style="font-size:11px;max-width:260px" title="${esc(d.lastCorrectionDetail ?? "")}">
    ${d.lastCorrectionOk ? "Puesto en hora" : "Falló la puesta en hora"} ${formatDateTime(d.lastCorrectionAt)}</div>`;
}

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------

async function renderDeviceMaintenance() {
  $("#page-title").textContent = "Hora y mantenimiento";
  try { maintData = await Api.get("/api/maintenance/clocks"); maintFetchedAt = performance.now(); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Perms.can("maintenance.manage");
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Hora y mantenimiento <span class="muted" style="font-weight:normal;font-size:12px">(se actualiza solo)</span></h3>
      <div class="row-actions">
        <button class="btn ghost" id="mt-check" title="Pregunta ya la hora a los equipos, sin cambiar nada">Leer ahora</button>
        ${isAdmin ? `<button class="btn ghost" id="mt-policy">Política…</button>
        <button class="btn" id="mt-sync" title="Deja a los equipos elegidos (o a todos) con la zona y la hora de la política">Poner en hora</button>` : ""}
      </div>
    </div>
    <div id="mt-cards"></div>
    <div id="mt-table"></div>`;

  $("#mt-check").addEventListener("click", (e) => maintCheckNow(e.currentTarget));
  $("#mt-policy")?.addEventListener("click", () => maintPolicyModal());
  $("#mt-sync")?.addEventListener("click", (e) => maintSyncSelected(e.currentTarget));
  $("#mt-table").addEventListener("click", onMaintTableClick);
  $("#mt-table").addEventListener("change", onMaintTableChange);
  maintDraw();

  clearInterval(maintTimer);
  maintTimer = setInterval(async () => {
    if (location.hash !== "#/device-maintenance" || $("#app-shell").classList.contains("hidden")) {
      clearInterval(maintTimer); clearInterval(maintTick); return;
    }
    if (!$("#modal-backdrop").classList.contains("hidden")) return;
    try { maintData = await Api.get("/api/maintenance/clocks"); maintFetchedAt = performance.now(); maintDraw(); }
    catch { /* se reintenta en la próxima vuelta */ }
  }, 15000);
  clearInterval(maintTick);
  maintTick = setInterval(() => {
    $$("#view [data-clock]").forEach((el) => { el.textContent = maintWallClock(Number(el.dataset.clock)); });
  }, 1000);
}

function maintDraw() {
  const data = maintData;
  const p = data.policy;
  const devices = data.devices;
  const count = (h) => devices.filter((d) => d.health === h).length;
  const bad = count("drift") + count("zone");

  $("#mt-cards").innerHTML = `
    <div class="cards">
      <div class="card">
        <div class="card-label">Hora del servidor</div>
        <div class="card-value small mt-clock" data-clock="${maintBaseMs(data.serverLocalTime)}">${maintWallClock(maintBaseMs(data.serverLocalTime))}</div>
        <div class="muted" style="font-size:12px">${maintZoneHtml(data.serverZoneDescription)}</div>
      </div>
      <div class="card">
        <div class="card-label">Cómo deben estar</div>
        <div class="card-value small">${p.timeZoneId ? esc(p.timeZoneName) : "La zona del servidor"}</div>
        <div class="muted" style="font-size:12px">${maintZoneHtml(p.zoneDescription)} · ${p.mode === "Ntp"
          ? `hora por NTP de ${esc(p.ntpServer ?? "")}` : "hora del servidor"}</div>
      </div>
      <div class="card">
        <div class="card-label">Supervisión</div>
        <div class="card-value small">${p.autoCorrect ? "Corrige sola" : "Solo avisa"}</div>
        <div class="muted" style="font-size:12px">Revisa cada ${p.checkMinutes} min · tolera ${maintSpan(p.thresholdSeconds)} de desfase</div>
      </div>
      <div class="card">
        <div class="card-label">Equipos</div>
        <div class="card-value" style="color:${bad ? "#F59E0B" : "var(--ok)"}">${count("ok")}
          <span class="muted" style="font-size:13px">de ${devices.length} en hora</span></div>
        <div class="muted" style="font-size:12px">${[
          bad ? `${bad} fuera de hora o de zona` : null,
          count("offline") ? `${count("offline")} sin conexión` : null,
          count("error") ? `${count("error")} sin leer` : null,
        ].filter(Boolean).join(" · ") || "Todo en orden"}</div>
      </div>
    </div>`;

  if (!devices.length) {
    $("#mt-table").innerHTML = `<div class="info-box">No hay equipos que administrar todavía. Por ahora esta página cubre
      los equipos de <a href="#/access">Control de acceso</a>; las fuentes de video, los paneles y la citofonía se suman después.</div>`;
    return;
  }

  const isAdmin = Perms.can("maintenance.manage");
  const selectable = devices.filter((d) => d.supportsClock);
  const allChecked = selectable.length > 0 && selectable.every((d) => maintSelected.has(maintKey(d)));
  $("#mt-table").innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        ${isAdmin ? `<th style="width:28px"><input type="checkbox" class="mt-all" ${allChecked ? "checked" : ""} title="Elegir todos"></th>` : ""}
        <th>Equipo</th><th>Hora del equipo</th><th>Desfase</th><th>Origen de la hora</th><th>Estado</th>
        ${isAdmin ? `<th title="La supervisión lo pone en hora sola cuando se desfasa">Corrección automática</th><th></th>` : ""}
      </tr></thead>
      <tbody>
        ${devices.map((d) => `
          <tr data-key="${maintKey(d)}">
            ${isAdmin ? `<td>${d.supportsClock ? `<input type="checkbox" class="mt-pick" ${maintSelected.has(maintKey(d)) ? "checked" : ""}>` : ""}</td>` : ""}
            <td>${esc(d.name)}
              <div class="muted" style="font-size:11px">${esc(MAINT_KIND_LABELS[d.kind] ?? d.kind)} · ${esc(d.model ?? "")} · ${esc(d.host)}${d.location ? ` · ${esc(d.location)}` : ""}</div></td>
            <td>${d.clock
              ? `<span class="mt-clock" data-clock="${maintBaseMs(d.clock.localTime)}">${maintWallClock(maintBaseMs(d.clock.localTime))}</span>
                 <div class="muted" style="font-size:11px;max-width:280px">${maintZoneHtml(d.clock.zoneDescription)}</div>`
              : `<span class="muted">—</span>`}</td>
            <td>${maintDriftHtml(d, data.policy.thresholdSeconds)}</td>
            <td class="muted">${maintSourceText(d.clock)}</td>
            <td>${maintHealthHtml(d)}${maintLastCorrectionHtml(d)}</td>
            ${isAdmin ? `
              <td>${d.supportsClock ? `<label class="checkbox-row" style="margin:0"><input type="checkbox" class="mt-auto" ${d.autoCorrect ? "checked" : ""}> sí</label>` : ""}</td>
              <td><div class="row-actions" style="flex-wrap:wrap">
                ${d.supportsClock ? `<button class="btn ghost mt-adjust" ${d.status === "Online" ? "" : "disabled"}>Ajustar…</button>` : ""}
                ${d.supportsReboot || d.resets.length ? `<button class="btn ghost mt-risk" ${d.status === "Online" ? "" : "disabled"}
                  title="Reiniciar o restablecer el equipo">Reiniciar / restablecer…</button>` : ""}
              </div></td>` : ""}
          </tr>`).join("")}
      </tbody>
    </table></div>
    <div class="muted" style="font-size:12px;margin-top:8px">
      La hora de cada equipo importa más de lo que parece: los terminales de acceso evalúan los horarios de los niveles y la
      vigencia de las personas con <b>su</b> hora local. «Desfase» compara su hora real con la del servidor; una zona distinta
      se marca aparte aunque la hora esté bien.
    </div>`;
}

const maintDeviceOf = (el) => maintData.devices.find((d) => maintKey(d) === el.closest("tr")?.dataset.key);

function onMaintTableClick(e) {
  const button = e.target.closest("button");
  if (!button) return;
  const device = maintDeviceOf(button);
  if (!device) return;
  if (button.classList.contains("mt-adjust")) maintClockModal(device);
  else if (button.classList.contains("mt-risk")) maintRiskModal(device);
}

async function onMaintTableChange(e) {
  const box = e.target;
  if (box.classList.contains("mt-all")) {
    maintData.devices.filter((d) => d.supportsClock).forEach((d) =>
      box.checked ? maintSelected.add(maintKey(d)) : maintSelected.delete(maintKey(d)));
    maintDraw();
    return;
  }
  const device = maintDeviceOf(box);
  if (!device) return;
  if (box.classList.contains("mt-pick")) {
    if (box.checked) maintSelected.add(maintKey(device)); else maintSelected.delete(maintKey(device));
    return;
  }
  if (box.classList.contains("mt-auto")) {
    box.disabled = true;
    try {
      await Api.put(`/api/maintenance/devices/${device.kind}/${device.id}/auto-correct`, { enabled: box.checked });
      device.autoCorrect = box.checked;
      toast(box.checked ? `"${device.name}" se pondrá en hora solo cuando se desfase.`
                        : `"${device.name}" ya no se pone en hora solo.`);
    } catch (err) {
      box.checked = !box.checked;
      toast(err.error, true);
    } finally { box.disabled = false; }
  }
}

async function maintCheckNow(button) {
  button.disabled = true;
  button.textContent = "Leyendo…";
  try {
    const devices = [...maintSelected].map((k) => { const [kind, id] = k.split(":"); return { kind, id: Number(id) }; });
    maintData = await Api.post("/api/maintenance/clocks/check", { devices });
    maintFetchedAt = performance.now();
    maintDraw();
  } catch (err) { toast(err.error, true); }
  finally { button.disabled = false; button.textContent = "Leer ahora"; }
}

async function maintSyncSelected(button) {
  const picked = maintData.devices.filter((d) => maintSelected.has(maintKey(d)));
  const targets = picked.length ? picked : maintData.devices.filter((d) => d.supportsClock && d.status === "Online");
  if (!targets.length) { toast("No hay equipos en línea que poner en hora.", true); return; }
  const p = maintData.policy;
  const aviso = [
    `¿Poner en hora ${picked.length ? `${targets.length} equipo(s) elegido(s)` : `los ${targets.length} equipos en línea`}?`,
    "",
    `Quedan en ${p.zoneDescription}, ${p.mode === "Ntp" ? `tomando la hora de ${p.ntpServer}` : "con la hora del servidor"}.`,
  ].join("\n");
  if (!confirm(aviso)) return;

  button.disabled = true;
  button.textContent = "Poniendo en hora…";
  try {
    const results = await Api.post("/api/maintenance/clocks/sync",
      { devices: targets.map((d) => ({ kind: d.kind, id: d.id })) });
    const failed = results.filter((r) => !r.ok);
    toast(failed.length
      ? `${results.length - failed.length} en hora; ${failed.length} con problemas: ${failed.map((r) => `${r.name} (${r.message})`).join("; ")}`
      : `${results.length} equipo(s) en hora.`, failed.length > 0);
    maintData = await Api.get("/api/maintenance/clocks");
    maintFetchedAt = performance.now();
    maintDraw();
  } catch (err) { toast(err.error, true); }
  finally { button.disabled = false; button.textContent = "Poner en hora"; }
}

// ---------------------------------------------------------------------------
// Zonas horarias (se piden una vez)
// ---------------------------------------------------------------------------

let maintZones = null;
async function maintTimeZones() {
  if (!maintZones) maintZones = await Api.get("/api/maintenance/time-zones");
  return maintZones;
}

function maintZoneOptions(zones, selectedId, withServerOption, serverZoneName) {
  return (withServerOption ? `<option value="" ${selectedId ? "" : "selected"}>La del servidor (${esc(serverZoneName)})</option>` : "") +
    zones.map((z) => `<option value="${esc(z.id)}" ${z.id === selectedId ? "selected" : ""}>${esc(z.name)}</option>`).join("");
}

// ---------------------------------------------------------------------------
// Ajustar la hora de un equipo
// ---------------------------------------------------------------------------

/**
 * Hora de un equipo. Se puede abrir desde la página o desde la ficha del
 * equipo; `onDone` refresca lo que corresponda a quien lo abrió.
 */
async function maintClockModal(device, onDone) {
  let zones, overview;
  try { [zones, overview] = await Promise.all([maintTimeZones(), Api.get("/api/maintenance/clocks")]); }
  catch (err) { toast(err.error, true); return; }
  const policy = overview.policy;
  const zoneId = policy.timeZoneId ?? overview.serverTimeZoneId;
  const now = new Date();
  const p = (n) => String(n).padStart(2, "0");
  const localNow = `${now.getFullYear()}-${p(now.getMonth() + 1)}-${p(now.getDate())}T${p(now.getHours())}:${p(now.getMinutes())}:${p(now.getSeconds())}`;
  const c = device.clock;

  openModal(`
    <h3>Ajustar la hora de «${esc(device.name)}»</h3>
    <div id="mc-error"></div>
    ${c ? `<div class="info-box">Ahora tiene <b>${esc(maintWallClock(maintBaseMs(c.localTime)))}</b> ·
      ${maintZoneHtml(c.zoneDescription)} · ${maintSourceText(c)}.</div>` : ""}
    <div class="field">
      <label>Zona horaria</label>
      <select id="mc-zone">${maintZoneOptions(zones, zoneId, false)}</select>
      <div class="muted" id="mc-zone-desc" style="font-size:12px;margin-top:4px"></div>
    </div>
    <div class="field">
      <label>¿De dónde saca la hora?</label>
      <label class="checkbox-row"><input type="radio" name="mc-mode" value="server" checked>
        La del servidor <span class="muted">(se le fija ahora)</span></label>
      <label class="checkbox-row"><input type="radio" name="mc-mode" value="manual"> Fijarla a mano:
        <input id="mc-local" type="datetime-local" step="1" value="${localNow}" style="margin-left:6px" disabled></label>
      <label class="checkbox-row"><input type="radio" name="mc-mode" value="ntp" ${device.supportsNtp ? "" : "disabled"}>
        Un servidor NTP${device.supportsNtp ? "" : ` <span class="muted">(este equipo no lo admite)</span>`}</label>
      <div class="form-grid" id="mc-ntp" style="margin-left:24px">
        <div class="field"><label>Servidor</label>
          <input id="mc-ntp-host" value="${esc(policy.ntpServer ?? c?.ntpServer ?? "")}" placeholder="pool.ntp.org o 192.168.1.10" disabled></div>
        <div class="field"><label>Cada (minutos)</label>
          <input id="mc-ntp-every" type="number" min="1" max="10080" value="${policy.ntpIntervalMinutes ?? 60}" disabled></div>
      </div>
    </div>
    <div class="muted" style="font-size:12px">Si la supervisión corrige sola, después de esto esperará una hora antes de volver
      a tocar el equipo. Si la zona o el origen no coinciden con la política, lo va a volver a poner como dice la política:
      apague su corrección automática si este equipo debe quedar distinto.</div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="mc-cancel">Cancelar</button>
      <button class="btn" type="button" id="mc-apply">Aplicar</button>
    </div>`, "wide");

  const describe = () => {
    const z = zones.find((x) => x.id === $("#mc-zone").value);
    $("#mc-zone-desc").textContent = z ? z.description : "";
  };
  describe();
  $("#mc-zone").addEventListener("change", describe);
  const modeOf = () => document.querySelector('input[name="mc-mode"]:checked').value;
  document.querySelectorAll('input[name="mc-mode"]').forEach((r) => r.addEventListener("change", () => {
    $("#mc-local").disabled = modeOf() !== "manual";
    $("#mc-ntp-host").disabled = $("#mc-ntp-every").disabled = modeOf() !== "ntp";
  }));
  $("#mc-cancel").addEventListener("click", closeModal);
  $("#mc-apply").addEventListener("click", async () => {
    const mode = modeOf();
    const body = {
      timeZoneId: $("#mc-zone").value,
      mode: mode === "ntp" ? "Ntp" : "Manual",
      localTime: mode === "manual" ? $("#mc-local").value : null,
      ntpServer: mode === "ntp" ? $("#mc-ntp-host").value.trim() : null,
      ntpIntervalMinutes: mode === "ntp" ? Number($("#mc-ntp-every").value) : null,
    };
    if (mode === "manual" && !body.localTime) { $("#mc-error").innerHTML = `<div class="error-box">Indique la hora.</div>`; return; }
    if (mode === "ntp" && !body.ntpServer) { $("#mc-error").innerHTML = `<div class="error-box">Indique el servidor NTP.</div>`; return; }
    const button = $("#mc-apply");
    button.disabled = true;
    button.textContent = "Aplicando…";
    try {
      const r = await Api.put(`/api/maintenance/devices/${device.kind}/${device.id}/clock`, body);
      closeModal();
      toast(r.note ? `Hora ajustada en "${device.name}". Nota: ${r.note}` : `Hora ajustada en "${device.name}".`);
      onDone ? onDone(r.device) : renderDeviceMaintenance();
    } catch (err) {
      $("#mc-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      button.disabled = false;
      button.textContent = "Aplicar";
    }
  });
}

// ---------------------------------------------------------------------------
// Política
// ---------------------------------------------------------------------------

async function maintPolicyModal() {
  let zones;
  try { zones = await maintTimeZones(); } catch (err) { toast(err.error, true); return; }
  const p = maintData.policy;
  openModal(`
    <h3>Cómo deben estar los equipos</h3>
    <div id="mp-error"></div>
    <div class="field">
      <label>Zona horaria</label>
      <select id="mp-zone">${maintZoneOptions(zones, p.timeZoneId, true, maintData.serverTimeZoneName)}</select>
      <div class="muted" style="font-size:12px;margin-top:4px">Lo normal es la del servidor: el VMS y sus equipos están en el mismo lugar.
        Incluye el horario de verano, que el VMS les escribe a los equipos que lo admiten.</div>
    </div>
    <div class="field">
      <label>Origen de la hora</label>
      <label class="checkbox-row"><input type="radio" name="mp-mode" value="Manual" ${p.mode !== "Ntp" ? "checked" : ""}>
        La del servidor: el VMS los pone en hora</label>
      <label class="checkbox-row"><input type="radio" name="mp-mode" value="Ntp" ${p.mode === "Ntp" ? "checked" : ""}>
        Un servidor NTP <span class="muted">(los equipos que no saben NTP reciben la hora del servidor)</span></label>
      <div class="form-grid" style="margin-left:24px">
        <div class="field"><label>Servidor NTP</label><input id="mp-ntp" value="${esc(p.ntpServer ?? "")}" placeholder="pool.ntp.org o 192.168.1.10"></div>
        <div class="field"><label>Sincronizar cada (minutos)</label><input id="mp-ntp-every" type="number" min="1" max="10080" value="${p.ntpIntervalMinutes}"></div>
      </div>
    </div>
    <div class="field">
      <label>Supervisión</label>
      <label class="checkbox-row"><input type="checkbox" id="mp-auto" ${p.autoCorrect ? "checked" : ""}>
        Ponerlos en hora solos cuando se desfasan o tienen otra zona <span class="muted">(queda en la bitácora)</span></label>
      <div class="form-grid">
        <div class="field"><label>Desfase tolerado (segundos)</label><input id="mp-threshold" type="number" min="5" max="86400" value="${p.thresholdSeconds}"></div>
        <div class="field"><label>Revisar cada (minutos)</label><input id="mp-every" type="number" min="1" max="1440" value="${p.checkMinutes}"></div>
      </div>
    </div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="mp-cancel">Cancelar</button>
      <button class="btn" type="button" id="mp-save">Guardar</button>
    </div>`, "wide");

  $("#mp-cancel").addEventListener("click", closeModal);
  $("#mp-save").addEventListener("click", async () => {
    const body = {
      timeZoneId: $("#mp-zone").value || null,
      mode: document.querySelector('input[name="mp-mode"]:checked').value,
      ntpServer: $("#mp-ntp").value.trim() || null,
      ntpIntervalMinutes: Number($("#mp-ntp-every").value),
      autoCorrect: $("#mp-auto").checked,
      thresholdSeconds: Number($("#mp-threshold").value),
      checkMinutes: Number($("#mp-every").value),
    };
    const button = $("#mp-save");
    button.disabled = true;
    try {
      await Api.put("/api/maintenance/clock-policy", body);
      closeModal();
      toast("Política guardada. Los equipos se revisan ahora.");
      renderDeviceMaintenance();
    } catch (err) {
      $("#mp-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      button.disabled = false;
    }
  });
}

// ---------------------------------------------------------------------------
// Reiniciar y restablecer (zona de riesgo)
// ---------------------------------------------------------------------------

const MAINT_RISKS = [
  {
    act: "reboot", title: "Reiniciar", button: "Reiniciar",
    help: "Reinicia el equipo. No borra nada: personas, horarios y configuración quedan como están.",
    risk: "Queda fuera de servicio uno o dos minutos mientras arranca: en ese lapso no abre puertas ni registra pasadas.",
    typeName: false, available: (d) => d.supportsReboot,
  },
  {
    act: "KeepNetwork", title: "Restablecer la configuración", button: "Restablecer",
    help: "Vuelve la configuración a la de fábrica pero conserva la red y las cuentas de usuario: el equipo vuelve en la misma dirección y con la misma clave, y el VMS le reescribe solo las personas y los horarios.",
    risk: "Se pierde todo ajuste hecho en el propio equipo (pantalla, sonidos, modos de verificación…). Mientras se reescribe el padrón, puede no reconocer a algunas personas.",
    typeName: true, available: (d) => d.resets.includes("KeepNetwork"),
  },
  {
    act: "Full", title: "De fábrica completo", button: "Volver a fábrica",
    help: "Borra TODO, incluidas la red y las cuentas. El equipo queda desactivado, como recién sacado de la caja.",
    risk: "El VMS lo pierde hasta que alguien lo vuelva a activar en la red (Dispositivos → búsqueda en la red) con la misma contraseña. Hasta entonces no abre puertas para nadie.",
    typeName: true, available: (d) => d.resets.includes("Full"),
  },
];

/** Zona de riesgo de un equipo: reiniciar y los restablecimientos que admite. */
function maintRiskModal(device, onDone) {
  const items = MAINT_RISKS.filter((r) => r.available(device));
  openModal(`
    <h3>Reiniciar o restablecer «${esc(device.name)}»</h3>
    <div class="danger-zone" id="mt-danger" style="margin-top:0">
      <h4>Zona de riesgo</h4>
      <div class="dz-intro muted">${esc(device.model ?? "")} · ${esc(device.host)}. Todo queda en la bitácora.</div>
      ${items.map((d) => `
        <div class="dz-item" data-act="${d.act}">
          <div class="dz-row">
            <div>
              <b>${esc(d.title)}</b>
              <div class="dz-help">${esc(d.help)}</div>
              <div class="dz-help"><span class="dz-risk">Riesgo:</span> ${esc(d.risk)}</div>
            </div>
            <button class="btn dz-open" type="button">${esc(d.button)}</button>
          </div>
          <div class="dz-confirm hidden">
            ${d.typeName
              ? `<label>Para confirmar, escriba el nombre del equipo: <b>${esc(device.name)}</b></label>
                 <div class="dz-confirm-row">
                   <input class="dz-input" autocomplete="off" spellcheck="false" placeholder="${esc(device.name)}">
                   <button class="btn danger dz-go" type="button" disabled>Entiendo el riesgo: ${esc(d.button.toLowerCase())}</button>
                   <button class="btn ghost dz-cancel" type="button">Cancelar</button>
                 </div>`
              : `<div class="dz-confirm-row">
                   <button class="btn danger dz-go" type="button">Sí, ${esc(d.button.toLowerCase())} ahora</button>
                   <button class="btn ghost dz-cancel" type="button">Cancelar</button>
                 </div>`}
          </div>
        </div>`).join("")}
    </div>
    <div class="modal-actions"><button class="btn ghost" type="button" id="mt-risk-close">Cerrar</button></div>`, "wide");

  $("#mt-risk-close").addEventListener("click", closeModal);
  const zone = $("#mt-danger");
  const close = (item) => {
    item.querySelector(".dz-confirm").classList.add("hidden");
    const input = item.querySelector(".dz-input");
    if (input) { input.value = ""; item.querySelector(".dz-go").disabled = true; }
  };
  zone.addEventListener("input", (e) => {
    if (!e.target.classList.contains("dz-input")) return;
    e.target.closest(".dz-item").querySelector(".dz-go").disabled = e.target.value.trim() !== device.name.trim();
  });
  zone.addEventListener("click", async (e) => {
    const btn = e.target.closest("button");
    const item = btn?.closest(".dz-item");
    if (!item) return;
    const d = MAINT_RISKS.find((x) => x.act === item.dataset.act);
    if (btn.classList.contains("dz-open")) {
      zone.querySelectorAll(".dz-item").forEach((it) => { if (it !== item) close(it); });
      item.querySelector(".dz-confirm").classList.remove("hidden");
      item.querySelector(".dz-input")?.focus();
    } else if (btn.classList.contains("dz-cancel")) {
      close(item);
    } else if (btn.classList.contains("dz-go")) {
      const typed = item.querySelector(".dz-input")?.value.trim();
      if (d.typeName && typed !== device.name.trim()) return;
      btn.disabled = true;
      try {
        const r = d.act === "reboot"
          ? await Api.post(`/api/maintenance/devices/${device.kind}/${device.id}/reboot`)
          : await Api.post(`/api/maintenance/devices/${device.kind}/${device.id}/reset`, { mode: d.act, confirm: typed });
        closeModal();
        toast(r.message);
        onDone ? onDone() : (location.hash === "#/device-maintenance" && renderDeviceMaintenance());
      } catch (err) {
        toast(err.error ?? "El equipo no aceptó la orden.", true);
        btn.disabled = false;
      }
    }
  });
}

// ---------------------------------------------------------------------------
// Apartado "Hora y mantenimiento" en la ficha de un equipo
// ---------------------------------------------------------------------------

/**
 * Bloque para la ficha (modal de edición) de un equipo: su hora en una línea y
 * los botones de mantenimiento. Se llena solo, después de abrir la ficha.
 */
function maintDeviceSectionHtml() {
  return `<div class="field" id="mt-section">
    <label>Hora y mantenimiento</label>
    <div class="muted" id="mt-section-body" style="font-size:12px">Leyendo la hora del equipo…</div>
  </div>`;
}

async function maintFillDeviceSection(kind, id) {
  const box = $("#mt-section-body");
  if (!box) return;
  let overview;
  try { overview = await Api.get("/api/maintenance/clocks"); maintFetchedAt = performance.now(); }
  catch { box.textContent = "No se pudo consultar la hora del equipo."; return; }
  const d = overview.devices.find((x) => x.kind === kind && x.id === id);
  if (!$("#mt-section-body")) return;   // la ficha se cerró mientras tanto
  if (!d) { box.textContent = "—"; return; }
  if (!d.supportsClock && !d.supportsReboot && !d.resets.length) {
    box.textContent = "Este equipo no admite ajustes de hora ni mantenimiento desde el VMS.";
    return;
  }
  const isAdmin = Perms.can("maintenance.manage");
  box.innerHTML = `
    <div>${d.clock
      ? `<b class="mt-clock" data-clock="${maintBaseMs(d.clock.localTime)}">${maintWallClock(maintBaseMs(d.clock.localTime))}</b>
         · ${maintZoneHtml(d.clock.zoneDescription)} · ${maintSourceText(d.clock)}`
      : "Todavía sin leer: la supervisión lo revisa en su próxima vuelta."}
      ${d.driftSeconds != null ? ` · desfase ${maintDriftHtml(d, overview.policy.thresholdSeconds)}` : ""}</div>
    <div style="margin-top:4px">${maintHealthHtml(d)}</div>
    ${isAdmin ? `<div class="row-actions" style="margin-top:8px">
      ${d.supportsClock ? `<button type="button" class="btn ghost" id="mt-s-adjust" ${d.status === "Online" ? "" : "disabled"}>Ajustar hora…</button>` : ""}
      ${d.supportsReboot || d.resets.length ? `<button type="button" class="btn ghost" id="mt-s-risk" ${d.status === "Online" ? "" : "disabled"}>Reiniciar / restablecer…</button>` : ""}
      <a href="#/device-maintenance" class="muted" style="align-self:center">Ver todos los equipos</a>
    </div>` : ""}`;
  // Ajustar o reiniciar reemplaza a la ficha (hay un solo modal a la vez).
  $("#mt-s-adjust")?.addEventListener("click", () => maintClockModal(d, () => {}));
  $("#mt-s-risk")?.addEventListener("click", () => maintRiskModal(d, () => {}));
  $("#mt-section a")?.addEventListener("click", () => closeModal());

  // El reloj de la ficha también avanza, mientras la ficha esté abierta.
  const timer = setInterval(() => {
    const clock = $("#mt-section-body [data-clock]");
    if (!clock) { clearInterval(timer); return; }
    clock.textContent = maintWallClock(Number(clock.dataset.clock));
  }, 1000);
}
