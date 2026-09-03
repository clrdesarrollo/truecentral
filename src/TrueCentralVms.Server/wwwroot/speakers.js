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
  let speakers;
  try { speakers = await Api.get("/api/speakers"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Parlantes IP <span class="muted" style="font-weight:normal;font-size:12px">(el estado se actualiza solo)</span></h3>
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
          <th>Nombre</th><th>Grupo</th><th>Dirección</th><th>Modelo</th><th>Firmware</th>
          <th>Funciones</th><th>Volumen</th><th>Conexión</th><th>En uso</th><th></th>
        </tr></thead>
        <tbody>
          ${speakers.map((s) => `
            <tr data-id="${s.id}">
              <td>${esc(s.name)}${s.enabled ? "" : ` <span class="tag operator" title="Desactivado">pausado</span>`}</td>
              <td class="muted">${esc(s.groupName ?? "—")}</td>
              <td class="muted">${s.useHttps ? "https://" : ""}${esc(s.host)}:${s.port}</td>
              <td>${esc(s.model ?? "—")}<div class="muted" style="font-size:11px">${esc(s.serialNumber ?? "")}</div></td>
              <td class="muted">${esc(s.firmwareVersion ?? "—")}</td>
              <td>${speakerCapsTags(s)}</td>
              <td><button class="btn ghost sp-volume-btn" data-volume="${s.volume ?? 100}" title="Volumen de salida del parlante" style="padding:4px 9px">🔊 <span class="sp-volume-label">${s.volume ?? "—"}</span></button></td>
              <td class="sp-status">${speakerStatusCell(s)}</td>
              <td class="sp-busy">${speakerBusyCell(s)}</td>
              <td><div class="row-actions" style="flex-wrap:wrap">
                <button class="btn ghost btn-play" title="Reproducir un sonido, un audio del equipo o un texto">Reproducir</button>
                <button class="btn ghost btn-stop" title="Detener lo que esté sonando">Detener</button>
                ${s.supportsLibrary ? `<button class="btn ghost btn-library" title="Audios guardados en el propio parlante">Biblioteca</button>` : ""}
                ${isAdmin ? `<button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </div></td>
            </tr>
            <tr class="hidden" data-detail="${s.id}"><td colspan="10"></td></tr>`).join("")}
        </tbody>
      </table></div>`}`;

  const byRow = (e) => speakers.find((s) => s.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-speaker-new")?.addEventListener("click", () => speakerModal(null));
  $("#btn-speaker-play-all")?.addEventListener("click", () => speakerPlayModal(speakers, null));
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
  const isAdmin = Api.role === "Admin";
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
              <button class="btn ghost btn-lib-play" title="Reproducir EN EL PARLANTE">▶ Reproducir</button>
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
async function speakerPlayModal(speakers, fixed) {
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
        <div class="wf-check-grid" id="sp-play-targets">
          ${targets.map((s) => `<label class="checkbox-row"><input type="checkbox" data-speaker="${s.id}" checked> ${esc(s.name)}${s.groupName ? ` <span class="muted">· ${esc(s.groupName)}</span>` : ""}</label>`).join("")}
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
    : Array.from(document.querySelectorAll("#sp-play-targets [data-speaker]")).filter((c) => c.checked).map((c) => Number(c.dataset.speaker));
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
  const drivers = await getSpeakerDrivers();
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
          <label>Grupo (opcional)</label>
          <input id="sp-group" maxlength="64" value="${esc(speaker?.groupName ?? "")}" placeholder="Perímetro, Bodega…">
        </div>
      </div>
      <div class="field">
        <label>Marca / protocolo</label>
        <select id="sp-driver">
          ${drivers.map((dr) => `<option value="${esc(dr.key)}" ${driver0?.key === dr.key ? "selected" : ""}>${esc(dr.displayName)}</option>`).join("")}
        </select>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección (IP o hostname)</label>
          <input id="sp-host" required value="${esc(speaker?.host ?? "")}" placeholder="192.168.1.73">
        </div>
        <div class="field">
          <label>Puerto HTTP</label>
          <input id="sp-port" type="number" min="1" max="65535" required value="${speaker?.port ?? driver0?.defaultPort ?? 80}">
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Usuario del parlante</label>
          <input id="sp-username" required value="${esc(speaker?.username ?? "admin")}" placeholder="admin">
        </div>
        <div class="field">
          <label>Contraseña${isNew ? "" : " (vacío = no cambiar)"}</label>
          <input id="sp-password" type="password" autocomplete="new-password" ${isNew ? "required" : ""}>
        </div>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="sp-https" ${(speaker ? speaker.useHttps : driver0?.defaultHttps) ? "checked" : ""}> Usar HTTPS (certificado autofirmado aceptado)</label>
      <label class="checkbox-row"><input type="checkbox" id="sp-enabled" ${speaker ? (speaker.enabled ? "checked" : "") : "checked"}> Activo (sondeo de estado, disponible para operadores y automatizaciones)</label>
      <div class="info-box" style="margin-top:10px">Use un usuario <b>local</b> del parlante (normalmente <b>admin</b>). El grupo permite elegir varios parlantes de una vez en las automatizaciones; los sonidos del servidor suenan sincronizados en todos los elegidos.</div>
      <div id="sp-probe-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="sp-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="sp-probe">Probar conexión</button>
        <button class="btn" type="submit" id="sp-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`);

  $("#sp-cancel").addEventListener("click", closeModal);
  $("#sp-driver").addEventListener("change", () => {
    const dr = drivers.find((x) => x.key === $("#sp-driver").value);
    if (dr && isNew) { $("#sp-port").value = dr.defaultPort; $("#sp-https").checked = dr.defaultHttps; }
  });

  const readForm = () => ({
    name: $("#sp-name").value.trim() || "(sin nombre)",
    driverKey: $("#sp-driver").value,
    host: $("#sp-host").value.trim(),
    port: Number($("#sp-port").value),
    useHttps: $("#sp-https").checked,
    username: $("#sp-username").value.trim(),
    password: $("#sp-password").value || null,
    enabled: $("#sp-enabled").checked,
    groupName: $("#sp-group").value.trim() || null,
  });

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
      if (isNew) await Api.post("/api/speakers", body);
      else await Api.put(`/api/speakers/${speaker.id}`, body);
      closeModal();
      toast(isNew ? "Parlante agregado y validado." : "Parlante actualizado.");
      renderSpeakers();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}
