// CLR TrueCentral VMS — panel: parlantes IP (altavoces de red).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

let speakerDriversCache = null;
async function getSpeakerDrivers() {
  speakerDriversCache ??= await Api.get("/api/speakers/drivers");
  return speakerDriversCache;
}

let speakersTimer = null;

/** TrueCentral Speaker: Raspberry Pi o PC con Windows con el agente instalado, que se conecta solo al servidor. */
function isAgentSpeaker(s) { return s?.driverKey === "truecentral-agent"; }

function speakerAddressHtml(s) {
  if (s.isupDeviceId) return isupAddressHtml(s.isupDeviceId);
  if (isAgentSpeaker(s)) {
    if (!s.agentId) return s.enrollmentExpiresAt
      ? `<span class="tag operator" title="Hay un código vigente hasta ${esc(new Date(s.enrollmentExpiresAt).toLocaleTimeString())}">por enrolar</span>`
      : `<span class="tag operator">sin enrolar</span>`;
    return `<span title="TrueCentral Speaker: el equipo se conecta al servidor">agente</span> · ${esc(s.host || "—")}`;
  }
  return `${s.useHttps ? "https://" : ""}${esc(s.host)}:${s.port}`;
}

/** Genera un código de enrolamiento y lo muestra (una sola vez) con las instrucciones para el equipo. */
async function speakerEnrollModal(speaker) {
  let r;
  try { r = await Api.post(`/api/speakers/${speaker.id}/agent/enrollment`, {}); }
  catch (err) { toast(err.error, true); return; }
  const server = `${location.protocol}//${location.host}`;
  openModal(`
    <h3>Enrolar «${esc(speaker.name)}»</h3>
    <p>Escriba este código en el TrueCentral Speaker (Raspberry Pi o PC con Windows). Vence a las
      <b>${esc(new Date(r.expiresAt).toLocaleTimeString())}</b> y sirve una sola vez.</p>
    <div style="font:600 34px/1.2 monospace;letter-spacing:4px;text-align:center;padding:14px;border:1px dashed var(--border);border-radius:8px;margin:10px 0">${esc(r.code)}</div>
    <div class="info-box">
      <b>Desde la página del equipo:</b> abra <code>http://&lt;ip-del-equipo&gt;:8088</code>, ingrese el servidor
      <code>${esc(server)}</code> y el código.<br>
      <b>Por consola (SSH en la Pi):</b><br>
      <code>sudo truecentral-speaker enroll --server ${esc(server)} --code ${esc(r.code)}</code>
      ${speaker.agentId ? `<br><br>Este parlante ya tiene un equipo enrolado: seguirá funcionando hasta que otro equipo use este código y lo reemplace.` : ""}
    </div>
    <div class="modal-actions"><button class="btn" type="button" id="sp-enroll-close">Listo</button></div>`);
  $("#sp-enroll-close").addEventListener("click", () => { closeModal(); renderSpeakers(); });
}

function speakerStatusTag(speaker) {
  switch (speaker.status) {
    case "Online": return `<span class="tag on">En línea</span>`;
    case "Offline": return `<span class="tag off">Sin conexión</span>`;
    case "AuthFailed": return `<span class="tag off">Credenciales</span>`;
    default: return `<span class="tag operator">—</span>`;
  }
}

function speakerCapsTags(speaker) {
  const tags = [];
  if (speaker.supportsLiveAudio) tags.push(`<span class="tag on" title="Acepta voz en vivo y sonidos del servidor">Voz</span>`);
  if (speaker.supportsLibrary) tags.push(`<span class="tag admin" title="Biblioteca de audios propia">Biblioteca</span>`);
  if (speaker.supportsTts) tags.push(`<span class="tag admin" title="Genera voz a partir de texto">TTS</span>`);
  return tags.join(" ") || `<span class="muted">—</span>`;
}

function speakerStatusCell(s) {
  return `${speakerStatusTag(s)}${s.lastError ? `<div class="muted" style="font-size:11px;max-width:220px" title="${esc(s.lastError)}">${esc(s.lastError)}</div>` : ""}`;
}

function speakerBusyCell(s) {
  return s.busyWith ? `<span class="tag admin">${esc(s.busyWith)}</span>` : `<span class="muted">libre</span>`;
}

/**
 * Actualización en sitio (sin redibujar): solo cambian las celdas de
 * conexión, "en uso" y volumen de cada fila. Así no se pierde la biblioteca
 * abierta, el audio que se está escuchando ni el deslizador que se arrastra.
 * Devuelve false si cambió el conjunto de parlantes (hay que redibujar).
 */
async function refreshSpeakersInPlace() {
  const speakers = await Api.get("/api/speakers");
  const rows = $$("#view tr[data-id]");
  const ids = rows.map((r) => Number(r.dataset.id)).sort().join(",");
  if (ids !== speakers.map((s) => s.id).sort().join(",")) return false;
  for (const s of speakers) {
    const row = $(`#view tr[data-id="${s.id}"]`);
    if (!row) continue;
    const status = row.querySelector(".sp-status");
    const busy = row.querySelector(".sp-busy");
    const button = row.querySelector(".sp-volume-btn");
    const label = row.querySelector(".sp-volume-label");
    const statusHtml = speakerStatusCell(s);
    if (status && status.innerHTML !== statusHtml) status.innerHTML = statusHtml;
    const busyHtml = speakerBusyCell(s);
    if (busy && busy.innerHTML !== busyHtml) busy.innerHTML = busyHtml;
    // No se pisa el valor mientras el mini modal de ese parlante está abierto.
    if (button && s.volume != null && !(window.speakerVolumePop && window.speakerVolumePop.id === s.id)) {
      button.dataset.volume = s.volume;
      if (label) label.textContent = s.volume;
    }
  }
  return true;
}

async function renderSpeakers() {
  closeSpeakerVolumePopover();
  $("#page-title").textContent = "Parlantes IP";
  let speakers, groups;
  try { [speakers, groups] = await Promise.all([Api.get("/api/speakers"), Api.get("/api/speakers/groups").catch(() => [])]); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Perms.can("speakers.configure");
  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        ${speakers.length ? `<button class="btn" id="btn-speaker-play-all" title="Reproducir en varios parlantes a la vez (sincronizado)">Reproducir en varios…</button>` : ""}
        ${isAdmin ? `<button class="btn" id="btn-speaker-new">Agregar parlante</button>` : ""}
      </div>
    </div>
    ${speakers.length === 0 ? `
      <div class="info-box">
        Aún no hay parlantes IP. ${isAdmin
          ? "Use <b>Agregar parlante</b>: al guardar se validan las credenciales contra el equipo y se leen sus capacidades (voz en vivo, biblioteca de audios, texto a voz). Desde ese momento el cliente de escritorio permite hablar por él, las automatizaciones pueden hacerlo sonar y varios parlantes pueden sonar sincronizados."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Grupos</th><th>Dirección</th><th>Modelo</th><th>Firmware</th>
          <th>Funciones</th><th>Volumen</th><th>Conexión</th><th>En uso</th><th></th>
        </tr></thead>
        <tbody>
          ${speakers.map((s) => `
            <tr data-id="${s.id}">
              <td>${esc(s.name)}${s.disabledByLicense ? licenseWaitTag(s) : s.enabled ? "" : ` <span class="tag operator" title="Desactivado">pausado</span>`}${s.accessDeviceId
                ? `<div class="muted field-hint">Altavoz de un terminal de acceso · incluido, no ocupa licencia</div>`
                : s.deviceId ? `<div class="muted field-hint">Altavoz de una cámara o grabador · incluido, no ocupa licencia</div>` : ""}${locationLineHtml(s.location)}</td>
              <td class="muted">${esc(s.groupName ?? "—")}</td>
              <td class="muted">${speakerAddressHtml(s)}</td>
              <td>${esc(s.model ?? "—")}<div class="muted" style="font-size:11px">${esc(s.serialNumber ?? "")}</div></td>
              <td class="muted">${esc(s.firmwareVersion ?? "—")}</td>
              <td>${speakerCapsTags(s)}</td>
              <td><button class="btn ghost sp-volume-btn" data-op="speaker:${s.id}" data-volume="${s.volume ?? 100}" title="Volumen de salida del parlante" style="padding:4px 9px">🔊 <span class="sp-volume-label">${s.volume ?? "—"}</span></button></td>
              <td class="sp-status">${speakerStatusCell(s)}</td>
              <td class="sp-busy">${speakerBusyCell(s)}</td>
              <td><div class="row-actions" style="flex-wrap:wrap">
                <button class="btn ghost btn-play" data-op="speaker:${s.id}" title="Reproducir un sonido, un audio del equipo o un texto">Reproducir</button>
                <button class="btn ghost btn-stop" data-op="speaker:${s.id}" title="Detener lo que esté sonando">Detener</button>
                ${s.supportsLibrary ? `<button class="btn ghost btn-library" title="Audios guardados en el propio parlante">Biblioteca</button>` : ""}
                ${isAgentSpeaker(s) && s.agentId ? `<button class="btn ghost btn-tone" title="Tono de prueba de 3 s en el altavoz">Tono</button>` : ""}
                ${isAdmin && isAgentSpeaker(s) ? `<button class="btn ghost btn-enroll" title="Generar un código para enrolar (o reemplazar) el equipo">${s.agentId ? "Reemplazar equipo…" : "Enrolar…"}</button>` : ""}
                ${isAdmin && isAgentSpeaker(s) && (s.agentId || s.enrollmentExpiresAt) ? `<button class="btn ghost btn-revoke" title="El equipo deja de poder conectarse hasta enrolarlo otra vez">Revocar</button>` : ""}
                ${isAdmin && s.accessDeviceId ? `<a class="btn ghost" href="#/access/device?id=${s.accessDeviceId}"
                  title="Su dirección y credenciales son las del terminal: se administra en Control de acceso">Ver terminal</a>` : ""}
                ${isAdmin && s.deviceId ? `<a class="btn ghost" href="#/devices"
                  title="Su dirección y credenciales son las de la cámara: se administra en Fuentes de video">Ver equipo</a>` : ""}
                ${isAdmin && !s.accessDeviceId && !s.deviceId ? `<button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </div></td>
            </tr>
            <tr class="hidden" data-detail="${s.id}"><td colspan="10"></td></tr>`).join("")}
        </tbody>
      </table></div>`}
    ${speakers.length || groups.length ? speakerGroupsSectionHtml(groups, speakers, isAdmin) : ""}`;
  speakerGroupsBind(groups, speakers);

  const byRow = (e) => speakers.find((s) => s.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-speaker-new")?.addEventListener("click", () => openFormPage("#/speakers/new"));
  $("#btn-speaker-play-all")?.addEventListener("click", () => speakerPlayModal(speakers, null, groups));
  $$("#view .btn-edit").forEach((b) => b.addEventListener("click", (e) => speakerModal(byRow(e))));
  $$("#view .btn-play").forEach((b) => b.addEventListener("click", (e) => speakerPlayModal(speakers, byRow(e))));
  $$("#view .btn-stop").forEach((b) => b.addEventListener("click", async (e) => {
    const speaker = byRow(e);
    try {
      const r = await Api.post("/api/speakers/stop", { speakerIds: [speaker.id] });
      toast(r.results[0]?.message ?? r.message, !r.success);
    } catch (err) { toast(err.error, true); }
  }));
  $$("#view .btn-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const speaker = byRow(e);
    if (!confirm(`¿Eliminar el parlante "${speaker.name}"?`)) return;
    try {
      await Api.delete(`/api/speakers/${speaker.id}`);
      toast("Parlante eliminado.");
      renderSpeakers();
    } catch (err) { toast(err.error, true); }
  }));
  $$("#view .btn-enroll").forEach((b) => b.addEventListener("click", (e) => speakerEnrollModal(byRow(e))));
  $$("#view .btn-tone").forEach((b) => b.addEventListener("click", async (e) => {
    try { toast((await Api.post(`/api/speakers/${byRow(e).id}/test-tone`, {})).message); }
    catch (err) { toast(err.error, true); }
  }));
  $$("#view .btn-revoke").forEach((b) => b.addEventListener("click", async (e) => {
    const speaker = byRow(e);
    if (!confirm(`¿Revocar el equipo de "${speaker.name}"? Dejará de poder conectarse hasta enrolarlo con un código nuevo.`)) return;
    try {
      await Api.delete(`/api/speakers/${speaker.id}/agent`);
      toast("Equipo revocado.");
      renderSpeakers();
    } catch (err) { toast(err.error, true); }
  }));
  // Volumen de salida: botón compacto que abre un mini modal con el deslizador.
  $$("#view .sp-volume-btn").forEach((b) => b.addEventListener("click", (e) => speakerVolumePopover(byRow(e), e.currentTarget)));
  $$("#view .btn-library").forEach((b) => b.addEventListener("click", (e) => {
    const speaker = byRow(e);
    const row = $(`#view tr[data-detail="${speaker.id}"]`);
    if (!row.classList.contains("hidden")) { row.classList.add("hidden"); return; }
    row.classList.remove("hidden");
    renderSpeakerLibrary(speaker);
  }));

  // El estado cambia solo (sondeo del servidor): cada 15 s se actualizan las
  // celdas en sitio; la página completa solo se redibuja si aparece o
  // desaparece un parlante.
  clearInterval(speakersTimer);
  speakersTimer = setInterval(async () => {
    if (location.hash !== "#/speakers" || $("#app-shell").classList.contains("hidden")) {
      clearInterval(speakersTimer);
      return;
    }
    if ($("#modal-backdrop") && !$("#modal-backdrop").classList.contains("hidden")) return;
    try {
      if (!await refreshSpeakersInPlace()) renderSpeakers();
    } catch { /* se reintenta en el próximo ciclo */ }
  }, 15000);
}

// ---------------------------------------------------------------------------
// Grupos de parlantes: para hablar o hacer sonar varios de una vez desde el
// Centro de mando (lista y tableros) y desde las automatizaciones. Un
// parlante puede estar en varios grupos.
// ---------------------------------------------------------------------------

function speakerGroupsSectionHtml(groups, speakers, isAdmin) {
  const byId = new Map(speakers.map((s) => [s.id, s]));
  return `
    <div class="sp-groups-head">
      <h3>Grupos de parlantes</h3>
      ${isAdmin ? `<button class="btn ghost small" id="btn-speaker-group-new">Nuevo grupo</button>` : ""}
    </div>
    <p class="muted sp-groups-intro">En el Centro de mando del cliente se le habla a todo el grupo de una vez (desde la
      lista o un cuadro de un tablero), y las automatizaciones pueden hacerlo sonar.</p>
    ${groups.length ? `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Grupo</th><th>Parlantes</th><th>Descripción</th><th></th></tr></thead>
      <tbody>${groups.map((g) => `
        <tr data-group="${g.id}">
          <td><b>${esc(g.name)}</b></td>
          <td>${g.speakerIds.length ? g.speakerIds.map((id) => esc(byId.get(id)?.name ?? `#${id}`)).join(", ") : `<span class="muted">vacío</span>`}</td>
          <td class="muted">${esc(g.description ?? "")}</td>
          <td><div class="row-actions">
            <button class="btn ghost btn-group-play" title="Reproducir en los parlantes del grupo">Reproducir</button>
            ${isAdmin ? `<button class="btn ghost btn-group-edit">Editar</button>
            <button class="btn danger btn-group-delete">Eliminar</button>` : ""}
          </div></td>
        </tr>`).join("")}</tbody>
    </table></div>` : `<div class="info-box">Todavía no hay grupos.${isAdmin ? " Use <b>Nuevo grupo</b>." : ""}</div>`}`;
}

function speakerGroupsBind(groups, speakers) {
  const groupOf = (e) => groups.find((g) => g.id === Number(e.target.closest("tr").dataset.group));
  $("#btn-speaker-group-new")?.addEventListener("click", () => speakerGroupModal(null, speakers));
  $$("#view .btn-group-edit").forEach((b) => b.addEventListener("click", (e) => speakerGroupModal(groupOf(e), speakers)));
  $$("#view .btn-group-play").forEach((b) => b.addEventListener("click", (e) => {
    const group = groupOf(e);
    speakerPlayModal(speakers.filter((s) => group.speakerIds.includes(s.id)), null, [group]);
  }));
  $$("#view .btn-group-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const group = groupOf(e);
    let used = 0;
    try { used = (await Api.delete(`/api/speakers/groups/${group.id}?dryRun=true`)).workflowActions; } catch { /* sin datos */ }
    if (!confirm(`¿Eliminar el grupo "${group.name}"? Los parlantes no se tocan.` +
      (used ? `\n\n${used} acción(es) de automatizaciones lo nombran y ya no harán sonar nada por él.` : ""))) return;
    try {
      await Api.delete(`/api/speakers/groups/${group.id}`);
      toast(`Grupo "${group.name}" eliminado.`);
      renderSpeakers();
    } catch (err) { toast(err.error, true); }
  }));
}

function speakerGroupModal(group, speakers) {
  const members = new Set(group?.speakerIds ?? []);
  openModal(`
    <h3>${group ? "Editar grupo de parlantes" : "Nuevo grupo de parlantes"}</h3>
    <div id="sp-group-error"></div>
    <div class="form-grid">
      <div class="field"><label for="sp-group-name">Nombre</label>
        <input id="sp-group-name" maxlength="64" value="${esc(group?.name ?? "")}" placeholder="Perímetro, Bodega, Todos…"></div>
      <div class="field"><label for="sp-group-desc">Descripción (opcional)</label>
        <input id="sp-group-desc" maxlength="255" value="${esc(group?.description ?? "")}"></div>
    </div>
    <div class="field">
      <label>Parlantes del grupo</label>
      <div class="wf-check-grid" id="sp-group-members">
        ${speakers.map((s) => `<label class="checkbox-row"><input type="checkbox" value="${s.id}"${members.has(s.id) ? " checked" : ""}>
          ${esc(s.name)}${s.enabled ? "" : ` <span class="muted">(pausado)</span>`}</label>`).join("") || `<span class="muted">No hay parlantes.</span>`}
      </div>
      ${group ? `<div class="muted field-hint">Si cambia el nombre, las automatizaciones que usan este grupo pasan solas al nombre nuevo.</div>` : ""}
    </div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="sp-group-cancel">Cancelar</button>
      <button class="btn" type="button" id="sp-group-save">Guardar</button>
    </div>`, true);
  $("#sp-group-cancel").addEventListener("click", closeModal);
  $("#sp-group-save").addEventListener("click", async () => {
    const body = {
      name: $("#sp-group-name").value.trim(),
      description: $("#sp-group-desc").value.trim() || null,
      speakerIds: [...$$("#sp-group-members input:checked")].map((cb) => Number(cb.value)),
    };
    try {
      if (group) await Api.put(`/api/speakers/groups/${group.id}`, body);
      else await Api.post("/api/speakers/groups", body);
      closeModal();
      toast(`Grupo "${body.name}" guardado.`);
      renderSpeakers();
    } catch (err) {
      $("#sp-group-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

/**
 * Mini modal de volumen anclado al botón de la fila: deslizador 0-100 que se
 * aplica al soltar; se cierra al hacer clic afuera o con Escape.
 */
function speakerVolumePopover(speaker, button) {
  closeSpeakerVolumePopover();
  const current = Number(button.dataset.volume ?? 100);
  const pop = document.createElement("div");
  pop.className = "probe-box";
  pop.style.cssText = "position:absolute;z-index:1000;padding:10px 12px;margin:0;min-width:230px;background:var(--panel-2);border-color:var(--border)";
  pop.innerHTML = `
    <div style="display:flex;align-items:center;gap:8px;margin-bottom:6px">
      <b style="flex:1">${esc(speaker.name)}</b>
      <span class="muted" style="font-size:11px">volumen</span>
      <b class="sp-pop-value" style="width:28px;text-align:right">${current}</b>
    </div>
    <input type="range" class="sp-pop-slider" min="0" max="100" step="5" value="${current}" style="width:100%">
    <div class="muted" style="font-size:11px;margin-top:4px">Se aplica al soltar · Esc cierra</div>`;
  document.body.appendChild(pop);
  const rect = button.getBoundingClientRect();
  pop.style.top = `${window.scrollY + rect.bottom + 6}px`;
  pop.style.left = `${Math.max(8, Math.min(window.scrollX + rect.left, window.scrollX + window.innerWidth - pop.offsetWidth - 8))}px`;

  const slider = pop.querySelector(".sp-pop-slider");
  const value = pop.querySelector(".sp-pop-value");
  slider.addEventListener("input", () => { value.textContent = slider.value; });
  slider.addEventListener("change", async () => {
    try {
      const updated = await Api.put(`/api/speakers/${speaker.id}/volume`, { volume: Number(slider.value) });
      const applied = updated.volume ?? Number(slider.value);
      button.dataset.volume = applied;
      button.querySelector(".sp-volume-label").textContent = applied;
      value.textContent = applied;
      toast(`Volumen de "${speaker.name}" en ${applied}.`);
    } catch (err) { toast(err.error, true); }
  });
  const onDocClick = (e) => { if (!pop.contains(e.target) && e.target !== button && !button.contains(e.target)) closeSpeakerVolumePopover(); };
  const onKey = (e) => { if (e.key === "Escape") closeSpeakerVolumePopover(); };
  setTimeout(() => { document.addEventListener("click", onDocClick); document.addEventListener("keydown", onKey); }, 0);
  window.speakerVolumePop = { id: speaker.id, element: pop, cleanup: () => { document.removeEventListener("click", onDocClick); document.removeEventListener("keydown", onKey); } };
  slider.focus();
}

function closeSpeakerVolumePopover() {
  const open = window.speakerVolumePop;
  if (!open) return;
  open.cleanup();
  open.element.remove();
  window.speakerVolumePop = null;
}

/** Detalle desplegable: biblioteca de audios del propio parlante. */
async function renderSpeakerLibrary(speaker) {
  const cell = $(`#view tr[data-detail="${speaker.id}"] td`);
  if (!cell) return;
  const isAdmin = Perms.can("speakers.configure");
  cell.innerHTML = `<div class="info-box" style="margin:8px 4px">Leyendo la biblioteca del parlante…</div>`;
  let items;
  try { items = await Api.get(`/api/speakers/${speaker.id}/library`); }
  catch (err) { cell.innerHTML = `<div class="error-box" style="margin:8px 4px">${esc(err.error)}</div>`; return; }

  const seconds = (n) => n ? `${n} s` : "—";
  cell.innerHTML = `
    <div style="padding:8px 4px">
      <div class="toolbar" style="margin-bottom:8px">
        <div><b>Biblioteca de ${esc(speaker.name)}</b> <span class="muted">(${items.length} audios en el equipo)</span></div>
        ${isAdmin ? `<div class="row-actions">
          <button class="btn ghost btn-lib-upload">Subir archivo…</button>
          ${speaker.supportsTts ? `<button class="btn ghost btn-lib-tts">Crear desde texto…</button>` : ""}
        </div>` : ""}
      </div>
      ${items.length === 0 ? `<div class="muted">El parlante no tiene audios guardados.</div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr><th>Nombre</th><th>Formato</th><th>Duración</th><th>Tamaño</th><th></th></tr></thead>
        <tbody>${items.map((a) => `
          <tr data-audio="${a.id}">
            <td>${esc(a.name)}${a.builtIn ? ` <span class="tag operator">de fábrica</span>` : ""}</td>
            <td class="muted">${esc(a.format)}</td>
            <td class="muted">${seconds(a.durationSeconds)}</td>
            <td class="muted">${Math.round(a.bytes / 1024)} kB</td>
            <td><div class="row-actions" style="flex-wrap:wrap">
              <button class="btn ghost btn-lib-listen" title="Escuchar en este navegador (no suena en el parlante)">🔈 Escuchar</button>
              <a class="btn ghost" title="Descargar el archivo desde el parlante a este equipo"
                 href="/api/speakers/${speaker.id}/library/${a.id}/file?access_token=${encodeURIComponent(Api.token || "")}"
                 download="${esc(a.name.includes(".") ? a.name : a.name + "." + a.format)}">⬇ Descargar</a>
              <button class="btn ghost btn-lib-play" data-op="speaker:${speaker.id}" title="Reproducir EN EL PARLANTE">▶ Reproducir</button>
              ${isAdmin ? `<button class="btn ghost btn-lib-rename" title="Cambiar el nombre con que el parlante muestra este audio">Renombrar</button>` : ""}
              ${isAdmin && !a.builtIn ? `<button class="btn danger btn-lib-delete">Borrar</button>` : ""}
            </div></td>
          </tr>`).join("")}</tbody>
      </table></div>`}
    </div>`;

  const audioOf = (e) => items.find((a) => a.id === Number(e.target.closest("tr").dataset.audio));
  // Escucha local: el archivo se descarga del parlante y suena en el navegador
  // del que configura, nunca en el equipo de terreno. Un segundo clic detiene.
  $$(".btn-lib-listen", cell).forEach((b) => b.addEventListener("click", (e) => {
    const audio = audioOf(e);
    const button = e.currentTarget;
    if (window.speakerPreview && window.speakerPreview.id === audio.id) {
      window.speakerPreview.audio.pause();
      window.speakerPreview = null;
      button.textContent = "🔈 Escuchar";
      return;
    }
    if (window.speakerPreview) {
      window.speakerPreview.audio.pause();
      window.speakerPreview.button.textContent = "🔈 Escuchar";
    }
    const player = new Audio(
      `/api/speakers/${speaker.id}/library/${audio.id}/file?access_token=${encodeURIComponent(Api.token || "")}`);
    window.speakerPreview = { id: audio.id, audio: player, button };
    button.textContent = "■ Detener";
    const done = () => { if (window.speakerPreview?.id === audio.id) window.speakerPreview = null; button.textContent = "🔈 Escuchar"; };
    player.addEventListener("ended", done);
    player.addEventListener("error", () => { done(); toast("El navegador no pudo reproducir ese audio.", true); });
    player.play().catch(() => { done(); toast("El navegador no pudo reproducir ese audio.", true); });
  }));
  $$(".btn-lib-play", cell).forEach((b) => b.addEventListener("click", async (e) => {
    const audio = audioOf(e);
    try {
      const r = await Api.post("/api/speakers/play", { speakerIds: [speaker.id], source: "library", libraryName: audio.name });
      toast(r.results[0]?.message ?? r.message, !r.success);
    } catch (err) { toast(err.error, true); }
  }));
  $$(".btn-lib-rename", cell).forEach((b) => b.addEventListener("click", async (e) => {
    const audio = audioOf(e);
    const name = prompt(`Nombre nuevo para "${audio.name}":`, audio.name);
    if (name === null || !name.trim() || name.trim() === audio.name) return;
    try {
      await Api.put(`/api/speakers/${speaker.id}/library/${audio.id}`, { name: name.trim() });
      toast("Audio renombrado en el parlante.");
      renderSpeakerLibrary(speaker);
    } catch (err) { toast(err.error, true); }
  }));
  $$(".btn-lib-delete", cell).forEach((b) => b.addEventListener("click", async (e) => {
    const audio = audioOf(e);
    if (!confirm(`¿Borrar "${audio.name}" de la biblioteca del parlante?`)) return;
    try {
      await Api.delete(`/api/speakers/${speaker.id}/library/${audio.id}`);
      toast("Audio borrado del parlante.");
      renderSpeakerLibrary(speaker);
    } catch (err) { toast(err.error, true); }
  }));
  $(".btn-lib-upload", cell)?.addEventListener("click", () => speakerUploadModal(speaker));
  $(".btn-lib-tts", cell)?.addEventListener("click", () => speakerTtsModal(speaker));
}

/** Subir un archivo a la biblioteca del parlante (mp3/wav/aac). */
function speakerUploadModal(speaker) {
  openModal(`
    <h3>Subir audio a ${esc(speaker.name)}</h3>
    <div id="sp-up-error"></div>
    <form id="sp-up-form">
      <div class="field">
        <label>Archivo (mp3, wav, aac o mp2; máximo 20 MB)</label>
        <input id="sp-up-file" type="file" accept=".mp3,.wav,.aac,.mp2,audio/*" required>
      </div>
      <div class="field">
        <label>Nombre en el parlante (vacío = nombre del archivo)</label>
        <input id="sp-up-name" maxlength="120" placeholder="Aviso zona restringida">
      </div>
      <div class="info-box">El archivo queda guardado en el propio parlante y se puede reproducir por nombre desde el cliente y las automatizaciones sin transmitir audio.</div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="sp-up-cancel">Cancelar</button>
        <button class="btn" type="submit" id="sp-up-save">Subir</button>
      </div>
    </form>`);
  $("#sp-up-cancel").addEventListener("click", closeModal);
  $("#sp-up-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const file = $("#sp-up-file").files[0];
    if (!file) return;
    const button = $("#sp-up-save");
    button.disabled = true;
    button.textContent = "Subiendo…";
    try {
      const form = new FormData();
      form.append("file", file);
      const name = $("#sp-up-name").value.trim();
      const query = name ? `?name=${encodeURIComponent(name)}` : "";
      const response = await fetch(`/api/speakers/${speaker.id}/library${query}`, {
        method: "POST",
        headers: { Authorization: "Bearer " + Api.token },
        body: form,
      });
      const data = await response.json().catch(() => null);
      if (!response.ok) throw { error: data?.error ?? `Error ${response.status}` };
      closeModal();
      toast("Audio guardado en el parlante.");
      renderSpeakerLibrary(speaker);
    } catch (err) {
      $("#sp-up-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      button.disabled = false;
      button.textContent = "Subir";
    }
  });
}

/** Generar un audio de texto a voz en el parlante. */
async function speakerTtsModal(speaker) {
  let languages = [];
  try { languages = await Api.get("/api/speakers/tts-languages"); } catch { /* lista por defecto abajo */ }
  if (!languages.length) languages = [{ key: "spanish", label: "Español" }, { key: "english", label: "Inglés" }];
  openModal(`
    <h3>Crear audio desde texto en ${esc(speaker.name)}</h3>
    <div id="sp-tts-error"></div>
    <form id="sp-tts-form">
      <div class="field">
        <label>Nombre del audio</label>
        <input id="sp-tts-name" required maxlength="120" placeholder="Aviso de retiro">
      </div>
      <div class="field">
        <label>Texto (máximo 100 caracteres)</label>
        <textarea id="sp-tts-text" rows="3" maxlength="100" required placeholder="Atención: esta es un área restringida. Retírese del lugar."></textarea>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Idioma</label>
          <select id="sp-tts-lang">${languages.map((l) => `<option value="${esc(l.key)}" ${l.key === "spanish" ? "selected" : ""}>${esc(l.label)}</option>`).join("")}</select>
        </div>
        <div class="field">
          <label>Voz</label>
          <select id="sp-tts-voice"><option value="female">Femenina</option><option value="male">Masculina</option></select>
        </div>
      </div>
      <div class="info-box">La voz la genera el propio parlante y tarda unos segundos; el audio queda en su biblioteca.</div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="sp-tts-cancel">Cancelar</button>
        <button class="btn" type="submit" id="sp-tts-save">Generar</button>
      </div>
    </form>`);
  $("#sp-tts-cancel").addEventListener("click", closeModal);
  $("#sp-tts-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = $("#sp-tts-save");
    button.disabled = true;
    button.textContent = "Generando…";
    try {
      await Api.post(`/api/speakers/${speaker.id}/library/tts`, {
        name: $("#sp-tts-name").value.trim(),
        text: $("#sp-tts-text").value.trim(),
        language: $("#sp-tts-lang").value,
        voice: $("#sp-tts-voice").value,
      });
      closeModal();
      toast("Audio generado en el parlante.");
      renderSpeakerLibrary(speaker);
    } catch (err) {
      $("#sp-tts-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      button.disabled = false;
      button.textContent = "Generar";
    }
  });
}

/** Reproducir en uno (fijo) o varios parlantes (elegibles). */
async function speakerPlayModal(speakers, fixed, groups = []) {
  let sounds = [];
  try { sounds = await Api.get("/api/speakers/sounds"); } catch { /* sin sonidos del servidor */ }
  const targets = fixed ? [fixed] : speakers.filter((s) => s.enabled);
  openModal(`
    <h3>${fixed ? `Reproducir en ${esc(fixed.name)}` : "Reproducir en varios parlantes"}</h3>
    <div id="sp-play-error"></div>
    <form id="sp-play-form">
      ${fixed ? "" : `
      <div class="field">
        <label>Parlantes (los sonidos del servidor suenan sincronizados)</label>
        ${groups.length ? `<div class="sp-group-chips">Marcar solo el grupo:
          ${groups.map((g) => `<button type="button" class="res-chip" data-group-ids="${g.speakerIds.join(",")}">${esc(g.name)}</button>`).join("")}</div>` : ""}
        <div class="wf-check-grid" id="sp-play-targets">
          ${targets.map((s) => `<label class="checkbox-row"><input type="checkbox" data-speaker="${s.id}" data-op="speaker:${s.id}" ${Operable.can("speaker", s.id) ? "checked" : ""}> ${esc(s.name)}${s.groupName ? ` <span class="muted">· ${esc(s.groupName)}</span>` : ""}</label>`).join("")}
        </div>
      </div>`}
      <div class="field">
        <label>Qué reproducir</label>
        <select id="sp-play-source">
          <option value="server">Sonido del servidor (Automatizaciones → Sonidos)</option>
          <option value="library">Audio de la biblioteca del parlante (por nombre)</option>
          <option value="tts">Texto leído en voz alta (TTS del parlante)</option>
        </select>
      </div>
      <div class="field" data-source="server">
        <label>Sonido</label>
        ${sounds.length ? `<div style="display:flex;gap:8px;align-items:center">
            <select id="sp-play-sound" style="flex:1">${sounds.map((a) => `<option value="${esc(a.displayName)}">${esc(a.displayName)}${a.ready ? "" : " (sin convertir)"}</option>`).join("")}</select>
            <button type="button" class="btn ghost" id="sp-play-sound-listen" title="Escuchar en este navegador (no suena en los parlantes)">🔈</button>
          </div>`
          : `<div class="muted">No hay sonidos en el servidor: súbalos en Automatizaciones → Sonidos.</div>`}
      </div>
      <div class="field" data-source="server">
        <label>Repeticiones</label>
        <input id="sp-play-repeat" type="number" min="1" max="5" value="1">
      </div>
      <div class="field hidden" data-source="library">
        <label>${fixed ? "Audio de la biblioteca del parlante" : "Nombre del audio en el parlante (se busca en cada uno)"}</label>
        ${fixed ? `<div style="display:flex;gap:8px;align-items:center">
            <select id="sp-play-library" style="flex:1"><option value="">Cargando la biblioteca…</option></select>
            <button type="button" class="btn ghost" id="sp-play-library-listen" title="Escuchar en este navegador (no suena en el parlante)">🔈</button>
          </div>` : `<input id="sp-play-library" placeholder="Siren, ThisIsRestrictArea…">`}
      </div>
      <div class="field hidden" data-source="tts">
        <label>Texto (máximo 100 caracteres)</label>
        <textarea id="sp-play-text" rows="2" maxlength="100" placeholder="Atención: retírese del lugar."></textarea>
      </div>
      <div class="form-grid hidden" data-source="tts">
        <div class="field"><label>Idioma</label>
          <select id="sp-play-lang"><option value="spanish">Español</option><option value="english">Inglés</option><option value="brazilianPortuguese">Portugués (Brasil)</option><option value="french">Francés</option></select></div>
        <div class="field"><label>Voz</label>
          <select id="sp-play-voice"><option value="female">Femenina</option><option value="male">Masculina</option></select></div>
      </div>
      <div id="sp-play-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="sp-play-cancel">Cerrar</button>
        <button class="btn ghost" type="button" id="sp-play-stop">Detener</button>
        <button class="btn" type="submit" id="sp-play-go">Reproducir</button>
      </div>
    </form>`);
  const syncSource = () => {
    const source = $("#sp-play-source").value;
    $$("#sp-play-form [data-source]").forEach((el) => el.classList.toggle("hidden", el.dataset.source !== source));
  };
  $("#sp-play-source").addEventListener("change", syncSource);
  syncSource();
  $$("#sp-play-form [data-group-ids]").forEach((chip) => chip.addEventListener("click", () => {
    const ids = new Set(chip.dataset.groupIds.split(",").filter(Boolean).map(Number));
    $$("#sp-play-targets input[data-speaker]").forEach((cb) => {
      cb.checked = ids.has(Number(cb.dataset.speaker)) && Operable.can("speaker", Number(cb.dataset.speaker));
    });
  }));
  // Escucha local (navegador del que configura): mismo reproductor que Automatizaciones → Sonidos.
  $("#sp-play-sound-listen")?.addEventListener("click", (e) => wfTogglePreview($("#sp-play-sound").value, e.currentTarget));
  if (fixed) {
    Api.get(`/api/speakers/${fixed.id}/library`).then((items) => {
      const select = $("#sp-play-library");
      if (!select) return;
      select.innerHTML = items.length
        ? items.map((a) => `<option value="${esc(a.name)}" data-id="${a.id}">${esc(a.name)} (${a.durationSeconds || "?"} s)</option>`).join("")
        : `<option value="">El parlante no tiene audios</option>`;
    }).catch((err) => toast(err.error, true));
    let libraryPlayer = null;
    $("#sp-play-library-listen")?.addEventListener("click", (e) => {
      const button = e.currentTarget;
      if (libraryPlayer) { libraryPlayer.pause(); libraryPlayer = null; button.textContent = "🔈"; return; }
      const option = $("#sp-play-library").selectedOptions[0];
      if (!option?.dataset.id) return;
      libraryPlayer = new Audio(`/api/speakers/${fixed.id}/library/${option.dataset.id}/file?access_token=${encodeURIComponent(Api.token || "")}`);
      button.textContent = "■";
      const done = () => { libraryPlayer = null; button.textContent = "🔈"; };
      libraryPlayer.addEventListener("ended", done);
      libraryPlayer.addEventListener("error", () => { done(); toast("El navegador no pudo reproducir ese audio.", true); });
      libraryPlayer.play().catch(() => { done(); toast("El navegador no pudo reproducir ese audio.", true); });
    });
  }
  const chosen = () => fixed ? [fixed.id]
    : Array.from(document.querySelectorAll("#sp-play-targets [data-speaker]")).filter((c) => c.checked && !c.disabled).map((c) => Number(c.dataset.speaker));
  const show = (r) => {
    $("#sp-play-result").innerHTML = `<div class="${r.success ? "probe-box" : "error-box"}"><div class="probe-title">${esc(r.message)}</div>
      ${r.results.map((x) => `<div>${x.success ? "✔" : "✖"} ${esc(x.speakerName)}: ${esc(x.message)}</div>`).join("")}</div>`;
  };
  $("#sp-play-cancel").addEventListener("click", () => { wfStopPreview(); closeModal(); });
  $("#sp-play-stop").addEventListener("click", async () => {
    const ids = chosen();
    if (!ids.length) return;
    try { show(await Api.post("/api/speakers/stop", { speakerIds: ids })); } catch (err) { toast(err.error, true); }
  });
  $("#sp-play-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const ids = chosen();
    if (!ids.length) { $("#sp-play-error").innerHTML = `<div class="error-box">Marque al menos un parlante.</div>`; return; }
    $("#sp-play-error").innerHTML = "";
    const source = $("#sp-play-source").value;
    const body = { speakerIds: ids, source };
    if (source === "server") { body.sound = $("#sp-play-sound")?.value ?? ""; body.repeat = Number($("#sp-play-repeat").value) || 1; }
    if (source === "library") body.libraryName = $("#sp-play-library").value.trim();
    if (source === "tts") { body.text = $("#sp-play-text").value.trim(); body.language = $("#sp-play-lang").value; body.voice = $("#sp-play-voice").value; }
    const button = $("#sp-play-go");
    button.disabled = true;
    button.textContent = "Enviando…";
    try { show(await Api.post("/api/speakers/play", body)); }
    catch (err) { $("#sp-play-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; }
    finally { button.disabled = false; button.textContent = "Reproducir"; }
  });
}

/** Alta / edición de un parlante. */
async function speakerModal(speaker) {
  const isNew = !speaker;
  const [drivers, places] = await Promise.all([getSpeakerDrivers(), loadLocationChoices()]);
  const driver0 = drivers.find((d) => d.key === speaker?.driverKey) ?? drivers[0];
  openModal(`
    <h3>${isNew ? "Agregar parlante IP" : "Editar parlante IP"}</h3>
    <div id="sp-modal-error"></div>
    <form id="speaker-form">
      <div class="form-grid">
        <div class="field">
          <label>Nombre</label>
          <input id="sp-name" required maxlength="128" value="${esc(speaker?.name ?? "")}" placeholder="Acceso proveedores, Patio bodega...">
        </div>
        <div class="field">
          <label>Grupos</label>
          <div class="muted sp-groups-note">${speaker?.groupName ? esc(speaker.groupName) : "Ninguno"} · se arman en
            <b>Grupos de parlantes</b>, al final de esta página.</div>
        </div>
      </div>
      ${locationFieldHtml("sp-location", places, speaker?.locationId ?? null, null, { equipment: true, isNew })}
      <div class="field">
        <label>Marca / protocolo</label>
        <select id="sp-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${driver0?.key === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <div id="sp-ip-box">
        <div class="form-grid">
          <div class="field">
            <label>Dirección (IP o hostname)</label>
            <input id="sp-host" required value="${esc(speaker?.isupDeviceId || isAgentSpeaker(speaker) ? "" : speaker?.host ?? "")}" placeholder="192.168.1.73">
          </div>
          <div class="field">
            <label id="sp-port-label">Puerto HTTP</label>
            <input id="sp-port" type="number" min="1" max="65535" required value="${speaker?.isupDeviceId || isAgentSpeaker(speaker) ? 80 : speaker?.port || driver0?.defaultPort || 80}">
          </div>
        </div>
        <div class="form-grid">
          <div class="field">
            <label>Usuario del parlante</label>
            <input id="sp-username" required value="${esc(speaker?.isupDeviceId || isAgentSpeaker(speaker) ? "admin" : speaker?.username ?? "admin")}" placeholder="admin">
          </div>
          <div class="field">
            <label>Contraseña${isNew || speaker?.isupDeviceId ? "" : " (vacío = no cambiar)"}</label>
            <input id="sp-password" type="password" autocomplete="new-password" ${isNew || speaker?.isupDeviceId ? "required" : ""}>
          </div>
        </div>
        <label class="checkbox-row"><input type="checkbox" id="sp-https" ${(speaker ? speaker.useHttps : driver0?.defaultHttps) ? "checked" : ""}> Usar HTTPS (certificado autofirmado aceptado)</label>
      </div>
      <div id="sp-isup-wrap">${isupFieldsHtml("sp", speaker, isNew, "parlante")}</div>
      <div id="sp-agent-box" class="info-box" hidden>
        <b>TrueCentral Speaker</b>: una Raspberry Pi o un PC con Windows con el agente instalado. No se pide dirección ni
        clave: ${isNew ? "al guardar se genera un <b>código de enrolamiento</b> que se escribe en el equipo" : "el equipo se conecta solo al servidor; para cambiarlo use <b>Reemplazar equipo…</b>"}.
        Funciona detrás de NAT o 4G, porque es el equipo el que llama al servidor.
      </div>
      <label class="checkbox-row"><input type="checkbox" id="sp-enabled" ${speaker ? (speaker.enabled ? "checked" : "") : "checked"}> Activo (sondeo de estado, disponible para operadores y automatizaciones)</label>
      <div class="info-box" id="sp-local-user-hint" style="margin-top:10px">Use un usuario <b>local</b> del parlante (normalmente <b>admin</b>). El grupo permite elegir varios parlantes de una vez en las automatizaciones; los sonidos del servidor suenan sincronizados en todos los elegidos.</div>
      <div id="sp-probe-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="sp-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="sp-probe">Probar conexión</button>
        <button class="btn" type="submit" id="sp-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);

  $("#sp-cancel").addEventListener("click", closeModal);
  // Por IP se piden dirección y credenciales; por ISUP, el ID y la clave. Lo que no corresponde se oculta y deja de ser obligatorio.
  // Un TrueCentral Speaker no pide nada de conexión: se enrola con un código.
  const toggleIsup = (dr) => {
    const isup = !!dr?.usesIsup;
    const agent = !!dr?.usesAgent;
    $("#sp-ip-box").hidden = isup || agent;
    $("#sp-isup-wrap").hidden = !isup;
    $("#sp-agent-box").hidden = !agent;
    $("#sp-local-user-hint").hidden = agent;
    $("#sp-probe").hidden = agent && isNew;
    // La voz por el SDK va al puerto del equipo (8000), no al HTTP.
    $("#sp-port-label").textContent = dr?.key === "hikvision-sdk-voice" ? "Puerto del equipo (SDK)" : "Puerto HTTP";
    for (const el of $$("#sp-ip-box input, #sp-isup-wrap input")) {
      const hide = el.closest("[hidden]") != null;
      if (hide) { if (el.required) el.dataset.req = "1"; el.required = false; }
      else if (el.dataset.req) el.required = true;
    }
  };
  wireIsupFields("sp", "parlante");
  toggleIsup(driver0);
  $("#sp-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#sp-driver").value);
    if (dr && isNew && !dr.usesIsup && !dr.usesAgent) { $("#sp-port").value = dr.defaultPort; $("#sp-https").checked = dr.defaultHttps; }
    toggleIsup(dr);
  });

  const readForm = () => {
    const common = {
      name: $("#sp-name").value.trim() || "(sin nombre)",
      driverKey: $("#sp-driver").value,
      enabled: $("#sp-enabled").checked,
      locationId: locationFieldValue("sp-location"),
    };
    if (!$("#sp-agent-box").hidden)
      return { ...common, host: "", port: 0, useHttps: false, username: "", password: null };
    if (!$("#sp-isup-wrap").hidden)
      return { ...common, host: "", port: 0, useHttps: false, username: "", password: null, ...readIsupFields("sp") };
    return {
      ...common,
      host: $("#sp-host").value.trim(),
      port: Number($("#sp-port").value),
      useHttps: $("#sp-https").checked,
      username: $("#sp-username").value.trim(),
      password: $("#sp-password").value || null,
    };
  };

  $("#sp-probe").addEventListener("click", async () => {
    const errorBox = $("#sp-modal-error");
    const resultBox = $("#sp-probe-result");
    errorBox.innerHTML = "";
    resultBox.innerHTML = `<div class="info-box">Conectando con el parlante…</div>`;
    const probeButton = $("#sp-probe");
    probeButton.disabled = true;
    try {
      const query = !isNew ? `?speakerId=${speaker.id}` : "";
      const r = await Api.post(`/api/speakers/probe${query}`, readForm());
      if (!r.success) {
        resultBox.innerHTML = `<div class="error-box">${esc(r.error)}</div>`;
        return;
      }
      const yesNo = (v) => v ? "sí" : "no";
      resultBox.innerHTML = `
        <div class="probe-box">
          <div class="probe-title">✔ Conexión validada</div>
          <div class="probe-grid">
            <span>Modelo</span><b>${esc(r.model ?? "—")}</b>
            <span>N° de serie</span><b>${esc(r.serialNumber ?? "—")}</b>
            <span>Firmware</span><b>${esc(r.firmwareVersion ?? "—")}</b>
            <span>Voz en vivo</span><b>${yesNo(r.supportsLiveAudio)}</b>
            <span>Biblioteca</span><b>${yesNo(r.supportsLibrary)}${r.supportsLibrary ? ` (${r.libraryCount} audios)` : ""}</b>
            <span>Texto a voz</span><b>${yesNo(r.supportsTts)}</b>
            <span>Volumen</span><b>${r.volume ?? "—"}</b>
          </div>
        </div>`;
    } catch (err) {
      resultBox.innerHTML = "";
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      probeButton.disabled = false;
    }
  });

  $("#speaker-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#sp-modal-error");
    errorBox.innerHTML = "";
    const saveButton = $("#sp-save");
    saveButton.disabled = true;
    saveButton.textContent = "Validando…";
    try {
      const body = readForm();
      await locationFieldCommit("sp-location", body);
      const saved = isNew ? await Api.post("/api/speakers", body) : await Api.put(`/api/speakers/${speaker.id}`, body);
      closeModal();
      // Un TrueCentral Speaker nuevo queda esperando su equipo: se muestra al tiro el código para enrolarlo.
      if (isNew && isAgentSpeaker(saved)) { await speakerEnrollModal(saved); return; }
      // Por ISUP el parlante puede no haberse conectado todavía: se guarda igual y se completa en su primer contacto.
      if (saved?.isupDeviceId && saved.status !== "Online")
        toast(`Parlante guardado: queda esperando que se conecte por ISUP. ${saved.lastError ?? ""}`, false, { warning: true });
      else toast(isNew ? "Parlante agregado y validado." : "Parlante actualizado.");
      renderSpeakers();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}
