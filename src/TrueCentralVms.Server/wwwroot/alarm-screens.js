// CLR TrueCentral VMS — panel: pantallas de alarma de las casetas.
//
// Raspberry Pi con un monitor (táctil o no) en cada caseta de guardia: muestran
// los avisos de las automatizaciones dirigidos a las pantallas, con sonido,
// foto y consignas, y dejan marcarlos como vistos. Marcar uno en una pantalla
// lo cierra en todas y en la ventana de alarma de los operadores.
//
// La pantalla se empareja sola: busca el servidor en la red, muestra un código
// de 6 dígitos y aparece aquí esperando aprobación. Al aprobarla con su nombre,
// recibe su credencial y desde ahí arranca sola.
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

/** Suscripción al hub de esta página (se suelta al salir). */
let screensHubBound = null;

function alarmScreensDetachHub() {
  if (!screensHubBound) return;
  VmsHub.off("AlarmScreensChanged", screensHubBound);
  screensHubBound = null;
}

const SCREEN_STATUS_LABELS = {
  pending: "Esperando aprobación",
  approved: "Aprobada",
  rejected: "Rechazada",
  disabled: "Deshabilitada",
};

function screenStatusCell(s) {
  switch (s.status) {
    case "pending": return `<span class="tag warn">Esperando aprobación</span>`;
    case "rejected": return `<span class="tag off">Rechazada</span>`;
    case "disabled": return `<span class="tag operator">Deshabilitada</span>`;
  }
  if (!s.paired) return `<span class="tag warn" title="Aprobada: falta que la pantalla reclame su credencial con el código">Emparejando…</span>`;
  return s.online
    ? `<span class="tag on">En línea</span>`
    : `<span class="tag off">Sin conexión</span>${s.lastSeenAt ? `<div class="muted" style="font-size:11px">vista ${esc(formatDate(s.lastSeenAt))}</div>` : ""}`;
}

function screenLayoutLabel(layout) {
  return layout === "vertical" ? "En pie" : "Apaisada";
}

/** Tarjeta de una pantalla que pidió emparejarse: el código grande, para compararlo con el de la caseta. */
function screenPendingCardHtml(s) {
  return `
    <div class="scr-pending" data-id="${s.id}">
      <div class="scr-code" title="Debe coincidir con el código que muestra la pantalla">${esc(s.pairingCode ?? "······")}</div>
      <div class="scr-pending-info">
        <b>${esc(s.hostname)}</b>
        <div class="muted">${esc(s.lastIp ?? "IP desconocida")} · app ${esc(s.version ?? "—")} · pidió ${esc(formatDate(s.createdAt))}</div>
      </div>
      <div class="row-actions">
        <button class="btn scr-approve">Aprobar</button>
        <button class="btn danger scr-reject">Rechazar</button>
      </div>
    </div>`;
}

async function renderAlarmScreens() {
  alarmScreensDetachHub();
  $("#page-title").textContent = "Pantallas de alarma";
  let data;
  try { data = await Api.get("/api/alarm-screens"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const screens = data.items;
  const pending = screens.filter((s) => s.status === "pending");
  const paired = screens.filter((s) => s.status !== "pending");
  const licenseLine = data.licensed
    ? `<span class="muted">Licencia: ${data.inUse} de ${data.quota} pantalla(s) aprobada(s).</span>`
    : `<span class="tag off">Las pantallas de alarma no están incluidas en la licencia</span>`;

  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        <button class="btn ghost" id="btn-scr-settings">Sonidos y ajustes</button>
      </div>
    </div>
    <div class="scr-license">${licenseLine}${data.clientVersion
      ? ` <span class="muted">· App publicada para la actualización remota: ${esc(data.clientVersion)}</span>` : ""}</div>

    ${pending.length > 0 ? `
      <div class="scr-section-title">Piden emparejarse (${pending.length})</div>
      <div class="info-box" style="margin-bottom:10px">
        Apruebe solo si el código coincide con el que muestra la pantalla en la caseta. Al aprobarla le pone el nombre
        con que aparecerá en las automatizaciones y en el registro de quién marcó cada aviso.
      </div>
      <div class="scr-pending-list">${pending.map(screenPendingCardHtml).join("")}</div>` : ""}

    ${paired.length === 0 && pending.length === 0 ? `
      <div class="info-box">
        Aún no hay pantallas. Instale la app de la pantalla en la Raspberry Pi de la caseta (vea el manual): al
        arrancar busca este servidor en la red —o se le escribe la dirección— y muestra un código de 6 dígitos. En ese
        momento aparece aquí para aprobarla. Después, en las automatizaciones, la acción <b>Avisar a los operadores</b>
        permite elegir si el aviso sale en la ventana de alarma, en las pantallas o en ambas.
      </div>` : ""}

    ${paired.length > 0 ? `
      <div class="scr-section-title">Pantallas</div>
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Conexión</th><th>Equipo</th><th>App</th><th>Volumen</th><th>Monitor</th>
          <th title="Avisos dirigidos a la pantalla que siguen sin marcar">Sin ver</th><th></th>
        </tr></thead>
        <tbody>
          ${paired.map((s) => `
            <tr data-id="${s.id}">
              <td><b>${esc(s.name)}</b>${s.approvedBy ? `<div class="muted" style="font-size:11px">aprobada por ${esc(s.approvedBy)}</div>` : ""}</td>
              <td>${screenStatusCell(s)}</td>
              <td class="muted">${esc(s.hostname)}<div style="font-size:11px">${esc(s.lastIp ?? "")}</div></td>
              <td class="muted">${esc(s.version ?? "—")}</td>
              <td>${s.volume}%</td>
              <td>${screenLayoutLabel(s.layout)}<div class="muted" style="font-size:11px">${esc(s.audioOutput ?? "salida del sistema")}</div></td>
              <td>${s.pendingAlerts > 0 ? `<span class="tag warn">${s.pendingAlerts}</span>` : `<span class="muted">0</span>`}</td>
              <td><div class="row-actions" style="flex-wrap:wrap">
                ${s.status === "approved" ? `<button class="btn ghost scr-edit">Editar</button>
                <button class="btn ghost scr-reboot" ${s.online ? "" : "disabled title=\"La pantalla no está conectada\""}>Reiniciar equipo</button>
                <button class="btn ghost scr-disable" title="Deja de recibir avisos hasta que se habilite">Deshabilitar</button>` : ""}
                ${s.status === "disabled" ? `<button class="btn ghost scr-enable">Habilitar</button>` : ""}
                <button class="btn ghost scr-reset" title="Descarta su credencial: la pantalla muestra un código nuevo y hay que aprobarla de nuevo">Reiniciar emparejamiento</button>
                <button class="btn danger scr-delete">Quitar</button>
              </div></td>
            </tr>`).join("")}
        </tbody>
      </table></div>` : ""}`;

  const byRow = (e) => screens.find((s) => s.id === Number(e.target.closest("[data-id]").dataset.id));
  const act = async (e, path, message, confirmText) => {
    const s = byRow(e);
    if (confirmText && !confirm(confirmText(s))) return;
    e.target.disabled = true;
    try {
      await Api.post(`/api/alarm-screens/${s.id}/${path}`, {});
      toast(message(s));
      renderAlarmScreens();
    } catch (err) { toast(err.error, true); e.target.disabled = false; }
  };

  $("#btn-scr-settings").addEventListener("click", alarmScreenSettingsModal);
  $$("#view .scr-approve").forEach((b) => b.addEventListener("click", (e) => alarmScreenApproveModal(byRow(e))));
  $$("#view .scr-reject").forEach((b) => b.addEventListener("click", (e) => act(e, "reject",
    (s) => `Se rechazó el equipo «${s.hostname}».`,
    (s) => `¿Rechazar el equipo «${s.hostname}» (${s.lastIp ?? "IP desconocida"})? No podrá volver a pedir emparejarse hasta que lo quite de la lista.`)));
  $$("#view .scr-edit").forEach((b) => b.addEventListener("click", (e) => alarmScreenEditModal(byRow(e))));
  $$("#view .scr-reboot").forEach((b) => b.addEventListener("click", (e) => act(e, "reboot",
    (s) => `Se ordenó reiniciar «${s.name}».`,
    (s) => `¿Reiniciar el equipo de la pantalla «${s.name}»? Estará sin mostrar avisos mientras arranca (alrededor de un minuto).`)));
  $$("#view .scr-disable").forEach((b) => b.addEventListener("click", (e) => act(e, "disable",
    (s) => `«${s.name}» quedó deshabilitada.`,
    (s) => `¿Deshabilitar la pantalla «${s.name}»? Deja de recibir avisos hasta que se vuelva a habilitar.`)));
  $$("#view .scr-enable").forEach((b) => b.addEventListener("click", (e) => act(e, "enable",
    (s) => `«${s.name}» volvió a quedar habilitada.`)));
  $$("#view .scr-reset").forEach((b) => b.addEventListener("click", (e) => act(e, "reset-pairing",
    (s) => `«${s.name}» mostrará un código nuevo: apruébela de nuevo cuando aparezca arriba.`,
    (s) => `¿Reiniciar el emparejamiento de «${s.name}»? Su credencial deja de valer: la pantalla mostrará un código nuevo y habrá que aprobarla otra vez.`)));
  $$("#view .scr-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const s = byRow(e);
    if (!confirm(`¿Quitar la pantalla «${s.name}»? Las automatizaciones que la eligen dejarán de mostrarle avisos. Si el equipo vuelve a anunciarse, aparecerá de nuevo esperando aprobación.`)) return;
    try {
      await Api.delete(`/api/alarm-screens/${s.id}`);
      toast("Pantalla quitada.");
      renderAlarmScreens();
    } catch (err) { toast(err.error, true); }
  }));

  // Pedidos de emparejamiento, conexiones y caídas llegan por el hub.
  screensHubBound = () => {
    if (!location.hash.startsWith("#/alarm-screens")) return;
    if ($("#modal-backdrop") && !$("#modal-backdrop").classList.contains("hidden")) return;
    renderAlarmScreens();
  };
  VmsHub.on("AlarmScreensChanged", screensHubBound);
}

function alarmScreenApproveModal(screen) {
  openModal(`
    <h3>Aprobar pantalla de alarma</h3>
    <div id="scr-modal-error"></div>
    <form id="scr-approve-form">
      <div class="scr-code" style="margin:0 auto 12px">${esc(screen.pairingCode ?? "")}</div>
      <p class="muted" style="text-align:center;margin-top:0">Confirme que la pantalla de la caseta muestra este mismo código
        <b>y esta huella del servidor</b>: <code id="scr-fingerprint">…</code></p>
      <p class="muted" style="text-align:center;margin-top:0;font-size:12px">Si la huella de la pantalla es otra, NO la apruebe:
        alguien está interceptando la red entre la caseta y el servidor.</p>
      <div class="field">
        <label>Nombre de la pantalla</label>
        <input id="scr-name" required maxlength="128" value="${esc(screen.name === screen.hostname ? "" : screen.name)}" placeholder="Caseta acceso principal">
        <div class="muted field-hint">Es el nombre que verá en las automatizaciones y en el registro de quién marcó cada aviso.</div>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="scr-cancel">Cancelar</button>
        <button class="btn" type="submit">Aprobar</button>
      </div>
    </form>`);
  $("#scr-cancel").addEventListener("click", closeModal);
  $("#scr-name").focus();
  // Primeros 8 bytes de la huella de la llave, igual que los muestra la pantalla.
  Api.get("/api/system/certificate").then((c) => {
    $("#scr-fingerprint").textContent = c.spki.slice(0, 16).toUpperCase().match(/.{2}/g).join(":");
  }).catch(() => { $("#scr-fingerprint").textContent = "(no disponible)"; });
  $("#scr-approve-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      await Api.post(`/api/alarm-screens/${screen.id}/approve`, { name: $("#scr-name").value.trim() });
      closeModal();
      toast("Pantalla aprobada: en unos segundos recibe su credencial y queda en línea.");
      renderAlarmScreens();
    } catch (err) { $("#scr-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
  });
}

function alarmScreenEditModal(screen) {
  const outputs = [...new Set([...(screen.audioOutputs || []), ...(screen.audioOutput ? [screen.audioOutput] : [])])];
  openModal(`
    <h3>Pantalla «${esc(screen.name)}»</h3>
    <div id="scr-modal-error"></div>
    <form id="scr-edit-form">
      <div class="field">
        <label>Nombre</label>
        <input id="scr-name" required maxlength="128" value="${esc(screen.name)}">
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Volumen: <b id="scr-volume-label">${screen.volume}%</b></label>
          <input id="scr-volume" type="range" min="0" max="100" step="5" value="${screen.volume}">
          <div class="muted field-hint">Se aplica al mezclador del equipo, también a los tonos propios de la pantalla.</div>
        </div>
        <div class="field">
          <label>Monitor</label>
          <select id="scr-layout">
            <option value="horizontal" ${screen.layout !== "vertical" ? "selected" : ""}>Apaisado: lista a la izquierda, detalle a la derecha</option>
            <option value="vertical" ${screen.layout === "vertical" ? "selected" : ""}>En pie: detalle arriba, lista abajo</option>
          </select>
        </div>
      </div>
      <div class="field">
        <label>Salida de audio</label>
        <select id="scr-output">
          <option value="">La del sistema</option>
          ${outputs.map((o) => `<option value="${esc(o)}" ${o === screen.audioOutput ? "selected" : ""}>${esc(o)}</option>`).join("")}
        </select>
        <div class="muted field-hint">${outputs.length === 0
          ? "La pantalla todavía no informó sus salidas (lo hace al conectarse)."
          : "Las salidas las informa la propia pantalla al conectarse (HDMI, jack, parlante USB)."}</div>
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="scr-cancel">Cancelar</button>
        <button class="btn" type="submit">Guardar cambios</button>
      </div>
    </form>`, true);
  $("#scr-cancel").addEventListener("click", closeModal);
  $("#scr-volume").addEventListener("input", () => { $("#scr-volume-label").textContent = `${$("#scr-volume").value}%`; });
  $("#scr-edit-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      await Api.put(`/api/alarm-screens/${screen.id}`, {
        name: $("#scr-name").value.trim(),
        volume: Number($("#scr-volume").value),
        layout: $("#scr-layout").value,
        audioOutput: $("#scr-output").value || null,
      });
      closeModal();
      toast("Cambios guardados: la pantalla los aplica en el acto.");
      renderAlarmScreens();
    } catch (err) { $("#scr-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
  });
}

async function alarmScreenSettingsModal() {
  let data;
  try { data = await Api.get("/api/alarm-screens/settings"); }
  catch (err) { toast(err.error, true); return; }
  const s = data.settings;
  const soundSelect = (id, value, empty) => `
    <select id="${id}">
      <option value="">${esc(empty)}</option>
      ${data.sounds.map((n) => `<option value="${esc(n)}" ${n === value ? "selected" : ""}>${esc(n)}</option>`).join("")}
      ${value && !data.sounds.includes(value) ? `<option value="${esc(value)}" selected>${esc(value)} (ya no está cargado)</option>` : ""}
    </select>`;

  openModal(`
    <h3>Sonidos y ajustes de las pantallas</h3>
    <div id="scr-modal-error"></div>
    <form id="scr-settings-form">
      <div class="field">
        <label>Nombre de la instalación</label>
        <input id="scr-site" maxlength="128" value="${esc(s.siteName)}" placeholder="${esc(data.defaultSiteName)}">
        <div class="muted field-hint">Lo muestran las pantallas en su encabezado. Vacío = el nombre del servidor.</div>
      </div>
      <div class="info-box" style="margin-bottom:10px">
        Los sonidos son los de <b>Automatizaciones → Sonidos</b> y son iguales en todas las pantallas: un guardia que
        cambia de caseta tiene que oír lo mismo. Un aviso con alarma sonora propia suena con la suya.
      </div>
      <div class="form-grid">
        <div class="field"><label>Aviso nuevo</label>${soundSelect("scr-sound-alarm", s.alarmSound, "Tono propio de la pantalla")}</div>
        <div class="field"><label>Aviso crítico</label>${soundSelect("scr-sound-high", s.highPrioritySound, "El mismo del aviso nuevo")}</div>
      </div>
      <div class="form-grid">
        <div class="field"><label>Recordatorio</label>${soundSelect("scr-sound-reminder", s.reminderSound, "Tono propio de la pantalla")}</div>
        <div class="field">
          <label>Recordar cada (segundos)</label>
          <input id="scr-repeat" type="number" min="0" max="600" value="${s.repeatBeepSeconds}">
          <div class="muted field-hint">Mientras quede algún aviso sin marcar. 0 = solo suena al llegar.</div>
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Insistir hasta (veces)</label>
          <input id="scr-max" type="number" min="0" max="1000" value="${s.maxReminders}">
          <div class="muted field-hint">0 = sin límite: suena hasta que se marque todo. Con un tope, un aviso sin atender termina en silencio.</div>
        </div>
        <div class="field">
          <label>Al reconectarse, traer los avisos de las últimas (horas)</label>
          <input id="scr-resync" type="number" min="1" max="168" value="${s.resyncHours}">
        </div>
      </div>
      <div class="muted" style="font-size:12px">
        Actualización remota: ${s.publishedClientVersion
          ? `está publicada la versión <b>${esc(s.publishedClientVersion)}</b> de la app; cada pantalla la instala sola.`
          : "no hay ninguna versión de la app publicada."}
        Los paquetes van en <code>${esc(data.packageDirectory)}</code>.
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="scr-cancel">Cancelar</button>
        <button class="btn" type="submit">Guardar</button>
      </div>
    </form>`, true);
  $("#scr-cancel").addEventListener("click", closeModal);
  $("#scr-settings-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      await Api.put("/api/alarm-screens/settings", {
        siteName: $("#scr-site").value.trim(),
        repeatBeepSeconds: Number($("#scr-repeat").value) || 0,
        maxReminders: Number($("#scr-max").value) || 0,
        alarmSound: $("#scr-sound-alarm").value || null,
        highPrioritySound: $("#scr-sound-high").value || null,
        reminderSound: $("#scr-sound-reminder").value || null,
        resyncHours: Number($("#scr-resync").value) || 24,
      });
      closeModal();
      toast("Ajustes guardados: las pantallas los aplican en el acto.");
    } catch (err) { $("#scr-modal-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
  });
}
