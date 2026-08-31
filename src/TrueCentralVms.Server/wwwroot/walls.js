// CLR TrueCentral VMS — panel: decodificadores y muros de video.
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

// ---------------------------------------------------------------------------
// Decodificadores
// ---------------------------------------------------------------------------
let decoderDriversCache = null;
async function getDecoderDrivers() {
  decoderDriversCache ??= await Api.get("/api/decoders/drivers");
  return decoderDriversCache;
}

async function renderDecoders() {
  $("#page-title").textContent = "Decodificadores";
  let decoders;
  try { decoders = await Api.get("/api/decoders"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Decodificadores de muro</h3>
      ${isAdmin ? `<button class="btn" id="btn-decoder-new">Agregar decodificador</button>` : ""}
    </div>
    ${decoders.length === 0 ? `
      <div class="info-box">
        Aún no hay decodificadores. ${isAdmin
          ? "Use <b>Agregar decodificador</b>: al guardar se validan las credenciales contra el equipo y se leen sus salidas físicas y canales de decodificación."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr>
          <th>Nombre</th><th>Driver</th><th>Dirección</th><th>Modelo / serie</th><th>Estado</th><th></th>
        </tr></thead>
        <tbody>
          ${decoders.map((d) => `
            <tr data-id="${d.id}">
              <td>${esc(d.name)}</td>
              <td class="muted">${esc(d.driverKey)}</td>
              <td class="muted">${esc(d.host)}:${d.port}</td>
              <td>${esc(d.model ?? "—")}</td>
              <td>${d.enabled ? `<span class="tag on">Activo</span>` : `<span class="tag off">Inactivo</span>`}</td>
              <td class="row-actions">
                <button class="btn ghost btn-test" title="Conectar y leer salidas y canales del equipo">Probar</button>
                <button class="btn ghost btn-diag" title="Volcado del estado real del equipo (muro, ventanas, decodificación)">Diagnóstico</button>
                ${isAdmin ? `<button class="btn ghost btn-edit">Editar</button>
                <button class="btn danger btn-delete">Eliminar</button>` : ""}
              </td>
            </tr>
            <tr class="hidden" data-detail="${d.id}"><td colspan="6"></td></tr>`).join("")}
        </tbody>
      </table></div>`}`;

  const detailCell = (id) => $(`#view tr[data-detail="${id}"] td`);
  const showDetail = (id, html) => {
    $(`#view tr[data-detail="${id}"]`).classList.remove("hidden");
    detailCell(id).innerHTML = html;
  };

  $("#btn-decoder-new")?.addEventListener("click", () => decoderModal(null));

  $$("#view .btn-test").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    e.target.disabled = true;
    showDetail(id, `<span class="muted">Conectando con el decodificador…</span>`);
    try {
      const result = await Api.post(`/api/decoders/${id}/test`);
      if (!result.success) { showDetail(id, `<div class="error-box">${esc(result.error)}</div>`); return; }
      const caps = result.capabilities;
      showDetail(id, `
        <b>${caps.decodeChannelCount}</b> canales de decodificación (desde el ${caps.decodeChannelStart})
        · N° serie: ${esc(caps.serialNumber ?? "n/d")}
        <div class="chip-row">
          ${caps.displays.map((o) => `<span class="chip">${esc(o.label)} → canal display ${o.channelNo}</span>`).join("")}
        </div>`);
    } catch (err) {
      showDetail(id, `<div class="error-box">${esc(err.error)}</div>`);
    } finally { e.target.disabled = false; }
  }));

  $$("#view .btn-diag").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    e.target.disabled = true;
    showDetail(id, `<span class="muted">Consultando el estado del equipo…</span>`);
    try {
      // El diagnóstico responde texto plano, no JSON.
      const response = await fetch(`/api/decoders/${id}/diagnostics`, {
        headers: { Authorization: "Bearer " + Api.token },
      });
      const text = await response.text();
      showDetail(id, response.ok
        ? `<pre class="diag">${esc(text)}</pre>`
        : `<div class="error-box">${esc(text)}</div>`);
    } finally { e.target.disabled = false; }
  }));

  $$("#view .btn-edit").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    decoderModal(decoders.find((d) => d.id === id));
  }));

  $$("#view .btn-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const decoder = decoders.find((d) => d.id === id);
    if (!confirm(`¿Eliminar el decodificador "${decoder.name}"?`)) return;
    try {
      await Api.delete(`/api/decoders/${id}`);
      toast("Decodificador eliminado.");
      renderDecoders();
    } catch (err) { toast(err.error, true); }
  }));
}

async function decoderModal(decoder) {
  let drivers;
  try { drivers = await getDecoderDrivers(); }
  catch (err) { toast(err.error, true); return; }

  const d = decoder || { name: "", driverKey: drivers[0]?.driverKey ?? "hikvision-netsdk",
    host: "", port: 8000, username: "admin", enabled: true, notes: "" };

  openModal(`
    <h3>${decoder ? "Editar decodificador" : "Nuevo decodificador"}</h3>
    <div class="field">
      <label>Nombre</label>
      <input id="dm-name" value="${esc(d.name)}" placeholder="Ej: Decoder sala de control">
    </div>
    <div class="field">
      <label>Driver</label>
      <select id="dm-driver">
        ${drivers.map((x) => `<option value="${esc(x.driverKey)}" ${x.driverKey === d.driverKey ? "selected" : ""}>${esc(x.displayName)}</option>`).join("")}
      </select>
    </div>
    <div class="form-grid">
      <div class="field"><label>Dirección (IP o host)</label><input id="dm-host" value="${esc(d.host)}"></div>
      <div class="field"><label>Puerto SDK</label><input id="dm-port" type="number" min="1" max="65535" value="${d.port}"></div>
      <div class="field"><label>Usuario</label><input id="dm-user" value="${esc(d.username)}"></div>
      <div class="field">
        <label>Contraseña</label>
        <input id="dm-pass" type="password" placeholder="${decoder ? "sin cambios" : ""}">
      </div>
    </div>
    <div class="field"><label>Notas</label><input id="dm-notes" value="${esc(d.notes ?? "")}"></div>
    <div class="checkbox-row"><input id="dm-enabled" type="checkbox" ${d.enabled ? "checked" : ""}><label for="dm-enabled">Activo</label></div>
    <div id="dm-error"></div>
    <p class="muted" style="font-size:12px">Al guardar se conecta con el equipo para validar las credenciales y leer sus capacidades.</p>
    <div class="modal-actions">
      <button class="btn ghost" id="dm-cancel">Cancelar</button>
      <button class="btn" id="dm-save">Guardar</button>
    </div>`);

  $("#dm-cancel").addEventListener("click", closeModal);
  $("#dm-save").addEventListener("click", async () => {
    const body = {
      name: $("#dm-name").value.trim(),
      driverKey: $("#dm-driver").value,
      host: $("#dm-host").value.trim(),
      port: Number($("#dm-port").value) || 8000,
      username: $("#dm-user").value.trim(),
      password: $("#dm-pass").value || null,
      enabled: $("#dm-enabled").checked,
      notes: $("#dm-notes").value.trim() || null,
    };
    const save = $("#dm-save");
    save.disabled = true;
    save.textContent = "Conectando…";
    try {
      if (decoder) await Api.put(`/api/decoders/${decoder.id}`, body);
      else await Api.post("/api/decoders", body);
      closeModal();
      toast("Decodificador guardado.");
      renderDecoders();
    } catch (err) {
      $("#dm-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
      save.textContent = "Guardar";
    }
  });
}

// ---------------------------------------------------------------------------
// Muros de video
// ---------------------------------------------------------------------------
let wallsTimer = null;          // sondeo del estado (otros clientes lo cambian)
const busyScreens = new Set();  // pantallas con una operación en curso

async function renderWalls() {
  $("#page-title").textContent = "Muro de video";
  clearInterval(wallsTimer);
  let walls;
  try { walls = await Api.get("/api/walls"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const isAdmin = Api.role === "Admin";
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Muros de video</h3>
      ${isAdmin ? `<button class="btn" id="btn-wall-new">Crear muro</button>` : ""}
    </div>
    ${walls.length === 0 ? `
      <div class="info-box">
        Aún no hay muros configurados. ${isAdmin
          ? "Registre primero un decodificador y luego use <b>Crear muro</b>: se define la grilla de monitores, qué salida física alimenta cada uno y en cuántas ventanas se divide."
          : "Un administrador debe configurarlos."}
      </div>` : ""}
    <div id="walls-list"></div>`;

  const list = $("#walls-list");
  walls.forEach((wall) => list.appendChild(wallCardElement(wall, isAdmin)));
  $("#btn-wall-new")?.addEventListener("click", () => wallEditorModal(null));

  // Refresco periódico: las asignaciones también cambian desde el cliente WPF.
  wallsTimer = setInterval(async () => {
    if (!$("#walls-list")) { clearInterval(wallsTimer); return; }
    try {
      const fresh = await Api.get("/api/walls");
      fresh.forEach((w) => {
        const card = $(`#wall-card-${w.id}`);
        if (card) card.replaceWith(wallCardElement(w, isAdmin));
      });
    } catch { /* el sondeo no debe molestar */ }
  }, 5000);
}

function wallCardElement(wall, isAdmin) {
  const div = document.createElement("div");
  div.className = "card wall-card";
  div.id = `wall-card-${wall.id}`;
  div.innerHTML = `
    <div class="toolbar">
      <h3>${esc(wall.name)}
        <span class="muted" style="font-weight:normal;font-size:12px">
          · ${wall.rows}×${wall.columns} · ${esc(wall.decoderName)}</span></h3>
      <div class="row-actions">
        <button class="btn ghost" data-layouts>Layouts</button>
        <button class="btn ghost" data-sync title="Reenviar al decodificador la división de ventanas configurada">Sincronizar</button>
        <button class="btn ghost" data-clear-all>Limpiar todo</button>
        ${isAdmin ? `<button class="btn ghost" data-edit>Editar</button>
        <button class="btn danger" data-delete>Eliminar</button>` : ""}
      </div>
    </div>
    <div class="wall-grid" style="grid-template-columns: repeat(${wall.columns}, minmax(150px, 1fr))"></div>
    <p class="muted" style="font-size:12px;margin:12px 0 0">
      Clic en una ventana para asignarle una cámara · doble clic para pantalla completa · clic derecho para liberarla.
    </p>`;

  const grid = $(".wall-grid", div);
  for (let r = 0; r < wall.rows; r++) {
    for (let c = 0; c < wall.columns; c++) {
      grid.appendChild(screenCellElement(wall, wall.screens.find((s) => s.row === r && s.col === c), r, c));
    }
  }

  $("[data-clear-all]", div).addEventListener("click", async () => {
    if (!confirm(`¿Detener la decodificación de todas las ventanas de "${wall.name}"?`)) return;
    try { await Api.post(`/api/walls/${wall.id}/clear-all`); toast("Muro limpiado."); renderWalls(); }
    catch (err) { toast(err.error, true); }
  });

  $("[data-sync]", div).addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    try {
      const results = await Api.post(`/api/walls/${wall.id}/sync`);
      const failed = Object.values(results).filter((x) => !x.success);
      toast(failed.length === 0
        ? "División de ventanas enviada al decodificador."
        : `Sincronización con ${failed.length} error(es): ${failed[0].error}`, failed.length > 0);
    } catch (err) { toast(err.error, true); }
    finally { button.disabled = false; }
  });

  $("[data-layouts]", div).addEventListener("click", () => layoutsModal(wall));
  $("[data-edit]", div)?.addEventListener("click", () => wallEditorModal(wall));
  $("[data-delete]", div)?.addEventListener("click", async () => {
    if (!confirm(`¿Eliminar el muro "${wall.name}"?`)) return;
    try { await Api.delete(`/api/walls/${wall.id}`); toast("Muro eliminado."); renderWalls(); }
    catch (err) { toast(err.error, true); }
  });

  return div;
}

/** Una pantalla física del muro con su mosaico de ventanas. */
function screenCellElement(wall, screen, row, col) {
  const cell = document.createElement("div");
  cell.className = "wall-cell";
  if (!screen) {
    cell.classList.add("unused");
    cell.innerHTML = `<span class="cell-pos">F${row + 1} · C${col + 1}</span><span class="muted">sin salida</span>`;
    return cell;
  }

  cell.innerHTML = `
    <span class="cell-pos">F${row + 1} · C${col + 1} — ${esc(screen.label || `Salida ${screen.displayChannel}`)}
      ${screen.windowMode > 1 ? `· ${screen.windowMode} ventanas` : ""}
      ${screen.fullscreen ? `<span class="tag on">completa</span>` : ""}</span>
    <div class="win-grid"></div>
    ${busyScreens.has(screen.id) ? `<div class="screen-overlay">Aplicando cambio…</div>` : ""}`;

  const winGrid = $(".win-grid", cell);
  // En pantalla completa solo se dibuja la cámara que ocupa el monitor.
  const shown = screen.fullscreen
    ? screen.windows.filter((w) => w.id === screen.fullscreenWindowId)
    : screen.windows;
  const cols = screen.fullscreen ? 1 : Math.ceil(Math.sqrt(Math.max(1, screen.windowMode)));
  winGrid.style.gridTemplateColumns = `repeat(${cols}, 1fr)`;

  shown.forEach((win) => {
    const w = document.createElement("div");
    w.className = "win-cell" + (win.assignment ? " busy" : "");
    if (!screen.fullscreen) {
      w.style.gridRow = `${Math.floor(win.windowIndex / cols) + 1} / span ${Math.max(1, win.spanRows)}`;
      w.style.gridColumn = `${(win.windowIndex % cols) + 1} / span ${Math.max(1, win.spanCols)}`;
    }
    const a = win.assignment;
    w.title = a
      ? `${a.channelName} · ${a.deviceName} canal ${a.channelNumber}${a.streamType ? " (sub)" : ""} — doble clic: pantalla completa`
      : `Ventana ${win.windowIndex + 1} · vacía`;
    w.innerHTML = a
      ? `<span class="win-name">${esc(a.channelName)}</span>
         <span class="win-detail">${esc(a.deviceName)}${a.streamType ? " · sub" : ""}</span>`
      : `<span class="win-free">${win.windowIndex + 1}</span>`;

    // Clic simple asigna; se retrasa para no dispararse durante el doble clic.
    let clickTimer = null;
    w.addEventListener("click", () => {
      if (clickTimer) return;
      clickTimer = setTimeout(() => { clickTimer = null; assignModal(wall, screen, win); }, 220);
    });
    w.addEventListener("dblclick", () => {
      if (clickTimer) { clearTimeout(clickTimer); clickTimer = null; }
      toggleScreenFullscreen(wall, screen, win);
    });
    w.addEventListener("contextmenu", async (e) => {
      e.preventDefault();
      if (!win.assignment) return;
      try { await Api.post(`/api/walls/${wall.id}/clear`, { windowId: win.id }); toast("Ventana liberada."); renderWalls(); }
      catch (err) { toast(err.error, true); }
    });
    winGrid.appendChild(w);
  });

  return cell;
}

async function toggleScreenFullscreen(wall, screen, win) {
  if (busyScreens.has(screen.id)) return;
  if (!screen.fullscreen && !win.assignment) {
    toast("Doble clic sobre una ventana con cámara para verla en pantalla completa.", true);
    return;
  }
  busyScreens.add(screen.id);
  try {
    if (screen.fullscreen) await Api.post(`/api/walls/${wall.id}/screens/${screen.id}/exit-fullscreen`);
    else await Api.post(`/api/walls/${wall.id}/fullscreen`, { windowId: win.id });
    toast(screen.fullscreen ? "Layout del monitor restaurado." : "Cámara en pantalla completa.");
  } catch (err) {
    toast(err.error, true);
  } finally {
    busyScreens.delete(screen.id);
    renderWalls();
  }
}

/** Asignación de una cámara del inventario a una ventana del muro. */
async function assignModal(wall, screen, win) {
  let devices;
  try { devices = await Api.get("/api/devices"); }
  catch (err) { toast(err.error, true); return; }
  if (devices.length === 0) { toast("No hay dispositivos registrados en el inventario.", true); return; }

  const current = win.assignment;
  openModal(`
    <h3>${esc(screen.label || "Pantalla")} — F${screen.row + 1}·C${screen.col + 1}${screen.windowMode > 1 ? ` · ventana ${win.windowIndex + 1}` : ""}</h3>
    <div class="field">
      <label>Dispositivo</label>
      <select id="am-device">
        ${devices.map((d) => `<option value="${d.id}" ${current && current.deviceId === d.id ? "selected" : ""}>${esc(d.name)} (${esc(d.host)})</option>`).join("")}
      </select>
    </div>
    <div class="field">
      <label>Canal</label>
      <select id="am-channel"><option>Cargando…</option></select>
    </div>
    <div class="field">
      <label>Stream</label>
      <select id="am-stream">
        <option value="0" ${!current || current.streamType === 0 ? "selected" : ""}>Principal</option>
        <option value="1" ${current && current.streamType === 1 ? "selected" : ""}>Secundario (sub)</option>
      </select>
    </div>
    <p class="muted" id="am-hint" style="font-size:12px"></p>
    <div id="am-error"></div>
    <div class="modal-actions">
      ${current ? `<button class="btn danger" id="am-clear">Liberar</button>` : ""}
      <button class="btn ghost" id="am-cancel">Cancelar</button>
      <button class="btn" id="am-apply">Mostrar en el muro</button>
    </div>`);

  async function loadChannels() {
    const deviceId = Number($("#am-device").value);
    const select = $("#am-channel");
    select.innerHTML = `<option>Cargando…</option>`;
    try {
      const channels = (await Api.get(`/api/devices/${deviceId}/channels`)).filter((c) => c.enabled);
      if (channels.length === 0) {
        select.innerHTML = `<option value="">(sin canales habilitados)</option>`;
        $("#am-hint").textContent = "El dispositivo no tiene canales habilitados.";
        return;
      }
      const preselect = current && current.deviceId === deviceId ? current.channelId : channels[0].id;
      select.innerHTML = channels.map((c) =>
        `<option value="${c.id}" ${c.id === preselect ? "selected" : ""}>${c.channelNumber} — ${esc(c.name)}${c.isOnline ? "" : " (sin señal)"}</option>`).join("");
      $("#am-hint").textContent = `${channels.length} canal(es) habilitado(s); ${channels.filter((c) => c.isOnline).length} con señal.`;
    } catch (err) {
      select.innerHTML = `<option value="">(error)</option>`;
      $("#am-hint").textContent = err.error;
    }
  }
  $("#am-device").addEventListener("change", loadChannels);
  await loadChannels();

  $("#am-cancel").addEventListener("click", closeModal);
  $("#am-apply").addEventListener("click", async () => {
    const channelId = Number($("#am-channel").value);
    if (!channelId) { toast("Elija un canal.", true); return; }
    const apply = $("#am-apply");
    apply.disabled = true;
    apply.textContent = "Decodificando…";
    try {
      await Api.post(`/api/walls/${wall.id}/assign`, {
        windowId: win.id, channelId, streamType: Number($("#am-stream").value),
      });
      closeModal();
      toast("Cámara asignada.");
      renderWalls();
    } catch (err) {
      $("#am-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      apply.disabled = false;
      apply.textContent = "Mostrar en el muro";
    }
  });

  $("#am-clear")?.addEventListener("click", async () => {
    try {
      await Api.post(`/api/walls/${wall.id}/clear`, { windowId: win.id });
      closeModal();
      toast("Ventana liberada.");
      renderWalls();
    } catch (err) { toast(err.error, true); }
  });
}

/** Layouts guardados de un muro (foto del estado: divisiones + cámaras). */
async function layoutsModal(wall) {
  let layouts;
  try { layouts = await Api.get(`/api/walls/${wall.id}/layouts`); }
  catch (err) { toast(err.error, true); return; }

  openModal(`
    <h3>Layouts de "${esc(wall.name)}"</h3>
    <div class="field">
      <label>Guardar el estado actual como</label>
      <input id="lm-name" placeholder="Ej: Turno noche">
    </div>
    <button class="btn" id="lm-save">Guardar layout</button>
    <h3 style="margin-top:22px">Guardados</h3>
    ${layouts.length === 0 ? `<p class="muted">No hay layouts guardados.</p>` : `
      <table class="grid"><tbody>
        ${layouts.map((l) => `
          <tr data-id="${l.id}">
            <td>${esc(l.name)} <span class="muted">· ${l.items.length} cámara(s)</span></td>
            <td class="row-actions">
              <button class="btn ghost btn-apply">Aplicar</button>
              <button class="btn danger btn-del">Eliminar</button>
            </td>
          </tr>`).join("")}
      </tbody></table>`}
    <div class="modal-actions"><button class="btn ghost" id="lm-close">Cerrar</button></div>`);

  $("#lm-close").addEventListener("click", closeModal);
  $("#lm-save").addEventListener("click", async () => {
    const name = $("#lm-name").value.trim();
    if (!name) { toast("Escriba un nombre para el layout.", true); return; }
    try {
      await Api.post(`/api/walls/${wall.id}/layouts`, { name, screens: null, items: null });
      toast("Layout guardado.");
      layoutsModal(wall);
    } catch (err) { toast(err.error, true); }
  });

  $$("#modal .btn-apply").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    e.target.disabled = true;
    e.target.textContent = "Aplicando…";
    try {
      const results = await Api.post(`/api/walls/${wall.id}/layouts/${id}/apply`);
      const failed = Object.values(results).filter((x) => !x.success).length;
      closeModal();
      toast(failed === 0 ? "Layout aplicado." : `Layout aplicado con ${failed} error(es).`, failed > 0);
      renderWalls();
    } catch (err) {
      toast(err.error, true);
      e.target.disabled = false;
      e.target.textContent = "Aplicar";
    }
  }));

  $$("#modal .btn-del").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    if (!confirm("¿Eliminar este layout?")) return;
    try { await Api.delete(`/api/walls/${wall.id}/layouts/${id}`); layoutsModal(wall); }
    catch (err) { toast(err.error, true); }
  }));
}

/**
 * Editor del muro: grilla de monitores, salida física de cada uno y en cuántas
 * ventanas se divide. Los canales de decodificación los reparte el servidor.
 */
async function wallEditorModal(wall) {
  let decoders;
  try { decoders = (await Api.get("/api/decoders")).filter((d) => d.enabled); }
  catch (err) { toast(err.error, true); return; }
  if (decoders.length === 0) { toast("Primero registre un decodificador.", true); return; }

  const w = wall || { name: "", decoderId: decoders[0].id, rows: 2, columns: 2, screens: [] };
  let caps = null; // capacidades leídas del decodificador seleccionado

  openModal(`
    <h3>${wall ? "Editar muro de video" : "Nuevo muro de video"}</h3>
    <div class="field"><label>Nombre</label>
      <input id="we-name" value="${esc(w.name)}" placeholder="Ej: Muro sala de monitoreo"></div>
    <div class="field"><label>Decodificador</label>
      <select id="we-decoder">
        ${decoders.map((d) => `<option value="${d.id}" ${d.id === w.decoderId ? "selected" : ""}>${esc(d.name)} (${esc(d.host)})</option>`).join("")}
      </select>
    </div>
    <div class="form-grid">
      <div class="field"><label>Filas</label><input id="we-rows" type="number" min="1" max="8" value="${w.rows}"></div>
      <div class="field"><label>Columnas</label><input id="we-cols" type="number" min="1" max="8" value="${w.columns}"></div>
    </div>
    <button class="btn ghost" id="we-caps">Leer salidas del decodificador</button>
    <span class="muted" id="we-caps-status" style="font-size:12px;margin-left:8px"></span>
    <p class="muted" style="font-size:12px;margin-bottom:6px">
      Para cada posición elija la salida física del decodificador y en cuántas ventanas se divide.
      Cada ventana consume un canal de decodificación; el servidor los asigna solo.
    </p>
    <div id="we-grid" class="wall-grid editing"></div>
    <div id="we-error"></div>
    <div class="modal-actions">
      <button class="btn ghost" id="we-cancel">Cancelar</button>
      <button class="btn" id="we-save">Guardar muro</button>
    </div>`, true);

  const gridEl = $("#we-grid");

  const displayOptions = (selected) => {
    const base = caps ? caps.displays.map((o) => ({ value: o.channelNo, label: `${o.label} (canal ${o.channelNo})` })) : [];
    if (selected && !base.some((o) => o.value === selected))
      base.unshift({ value: selected, label: `Canal display ${selected}` });
    return [{ value: 0, label: "— sin usar —" }, ...base];
  };

  const windowModesFor = (displayChannel) => {
    const fallback = [1, 2, 4, 6, 8, 9, 12, 16, 25, 36];
    if (!caps) return fallback;
    const display = caps.displays.find((o) => o.channelNo === displayChannel);
    return display && display.windowModes.length ? display.windowModes : fallback;
  };

  const windowModeOptions = (displayChannel, selected) => {
    const modes = [...windowModesFor(displayChannel)];
    if (selected && !modes.includes(selected)) modes.push(selected);
    modes.sort((a, b) => a - b);
    return modes.map((m) =>
      `<option value="${m}" ${m === selected ? "selected" : ""}>${m === 1 ? "1 (completa)" : `${m} ventanas`}</option>`).join("");
  };

  function redrawGrid() {
    const rows = Math.max(1, Number($("#we-rows").value) || 1);
    const cols = Math.max(1, Number($("#we-cols").value) || 1);
    gridEl.style.gridTemplateColumns = `repeat(${cols}, minmax(150px, 1fr))`;
    gridEl.innerHTML = "";
    for (let r = 0; r < rows; r++) {
      for (let c = 0; c < cols; c++) {
        const existing = w.screens.find((s) => s.row === r && s.col === c);
        const auto = caps && caps.displays[r * cols + c] ? caps.displays[r * cols + c].channelNo : 0;
        const display = existing ? existing.displayChannel : auto;
        const mode = existing ? existing.windowMode : 1;
        const cell = document.createElement("div");
        cell.className = "wall-cell";
        cell.dataset.row = r;
        cell.dataset.col = c;
        cell.innerHTML = `
          <span class="cell-pos">F${r + 1} · C${c + 1}</span>
          <label class="mini">Salida
            <select class="we-display">
              ${displayOptions(display).map((o) => `<option value="${o.value}" ${o.value === display ? "selected" : ""}>${esc(o.label)}</option>`).join("")}
            </select>
          </label>
          <label class="mini">Ventanas
            <select class="we-windows">${windowModeOptions(display, mode)}</select>
          </label>`;
        // Cada salida soporta sus propias divisiones: al cambiarla se recalculan.
        $(".we-display", cell).addEventListener("change", (e) => {
          const select = $(".we-windows", cell);
          select.innerHTML = windowModeOptions(Number(e.target.value), Number(select.value) || 1);
        });
        gridEl.appendChild(cell);
      }
    }
  }

  $("#we-caps").addEventListener("click", async () => {
    const status = $("#we-caps-status");
    status.textContent = "Conectando…";
    try {
      const result = await Api.post(`/api/decoders/${Number($("#we-decoder").value)}/test`);
      if (!result.success) { caps = null; status.textContent = `No se pudo leer: ${result.error}`; return; }
      caps = result.capabilities;
      status.textContent = `${caps.displays.length} salidas · ${caps.decodeChannelCount} canales de decodificación (desde el ${caps.decodeChannelStart}).`;
      redrawGrid();
    } catch (err) {
      caps = null;
      status.textContent = `No se pudo leer: ${err.error}`;
    }
  });
  $("#we-rows").addEventListener("change", redrawGrid);
  $("#we-cols").addEventListener("change", redrawGrid);
  $("#we-decoder").addEventListener("change", () => { caps = null; $("#we-caps-status").textContent = ""; redrawGrid(); });
  redrawGrid();

  $("#we-cancel").addEventListener("click", closeModal);
  $("#we-save").addEventListener("click", async () => {
    const rows = Math.max(1, Number($("#we-rows").value) || 1);
    const columns = Math.max(1, Number($("#we-cols").value) || 1);
    const screens = [];
    $$(".wall-cell", gridEl).forEach((cell) => {
      const displaySelect = $(".we-display", cell);
      const displayChannel = Number(displaySelect.value);
      if (!displayChannel) return; // celda sin usar
      screens.push({
        row: Number(cell.dataset.row),
        col: Number(cell.dataset.col),
        label: displaySelect.selectedOptions[0].textContent.split(" (")[0],
        displayChannel,
        windowMode: Number($(".we-windows", cell).value) || 1,
      });
    });

    const body = { name: $("#we-name").value.trim(), decoderId: Number($("#we-decoder").value), rows, columns, screens };
    if (!body.name) { toast("El nombre es obligatorio.", true); return; }
    if (screens.length === 0) { toast("Configure al menos una pantalla.", true); return; }

    const save = $("#we-save");
    save.disabled = true;
    save.textContent = "Guardando…";
    try {
      const result = wall
        ? await Api.put(`/api/walls/${wall.id}`, body)
        : await Api.post("/api/walls", body);
      closeModal();
      toast("Muro de video guardado.");
      if (result.warning) toast(result.warning, true);
      const syncFailed = Object.values(result.sync || {}).filter((x) => !x.success);
      if (syncFailed.length > 0)
        toast(`La división de ventanas no llegó al decodificador (${syncFailed.length} pantalla(s)): use "Sincronizar" cuando esté en línea.`, true);
      renderWalls();
    } catch (err) {
      $("#we-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
      save.textContent = "Guardar muro";
    }
  });
}
