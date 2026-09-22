// CLR TrueCentral VMS — Aplicaciones → Videowall (puesto de operación del muro).
// Réplica en el panel de la pantalla "Muro de video" del cliente
// (Views\WallView.xaml): selector de muro, el muro dibujado a escala con sus
// monitores y ventanas, las ventanas flotantes encima y el panel de cámaras
// para arrastrarlas. La página Configuración → Videowalls → Muro de video
// (walls.js) sigue siendo la del alta y edición de la estructura.
//
// Gestos (los mismos del cliente):
//   arrastrar una cámara a una ventana → asignarla · arrastrar una ventana a
//   otra → intercambiarlas · clic → seleccionar (Ctrl+clic: varias) ·
//   doble clic → pantalla completa del monitor · Ctrl+doble clic → todo el
//   muro · clic derecho → menú · ✕ → cerrar el video.
// Con una ventana seleccionada, un clic en una cámara del panel la asigna.
//
// El panel no escucha el hub: el estado del muro se trae por sondeo. La
// proyección de la pantalla del operador existe solo en el cliente.
// Se carga después de walls.js (usa layoutsModal).
"use strict";

let videowallTimer = null;
const VW_POLL_MS = 4000;
const VW_PREVIEW_MS = 30000;
const VW_SPLITS = [1, 2, 4, 6, 8, 9, 12, 16, 25, 36];
const VW_PREFS_KEY = "tcvms.vw.prefs";

let vwWalls = [];
let vwWall = null;
let vwWallKey = "";
let vwSources = [];              // [{ device, channels: [...] }] solo canales habilitados y con señal
let vwSelected = new Set();      // ids de ventanas seleccionadas
let vwBusyScreens = new Set();
let vwWallBusy = false;
let vwDragging = false;          // arrastre en curso: el sondeo no redibuja
let vwFloatMode = false;
let vwPreviewBucket = 0;
const vwPrefs = (() => {
  try { return { wallId: null, preview: false, collapsed: false, ...JSON.parse(localStorage.getItem(VW_PREFS_KEY) || "{}") }; }
  catch { return { wallId: null, preview: false, collapsed: false }; }
})();
function vwSavePrefs() { try { localStorage.setItem(VW_PREFS_KEY, JSON.stringify(vwPrefs)); } catch { /* modo privado */ } }

function vwStatus(message, isError) {
  const el = $("#vw-status");
  if (!el) return;
  el.textContent = `${new Date().toLocaleTimeString("es-CL")} — ${message}`;
  el.classList.toggle("error", !!isError);
}

function vwSnapshotUrl(a) {
  return `/api/devices/${a.deviceId}/snapshot/${a.channelNumber}?access_token=${encodeURIComponent(Api.token || "")}&t=${vwPreviewBucket}`;
}

function vwSubtitle(a) {
  return a.isExternal ? "transmisión de este puesto" : `${a.deviceName} · ch ${a.channelNumber}${a.streamType ? " · sub" : ""}`;
}

/** Busca una ventana (del mosaico o flotante) por id. */
function vwFindWindow(id) {
  for (const s of vwWall?.screens ?? []) {
    const w = s.windows.find((x) => x.id === id);
    if (w) return { screen: s, win: w };
  }
  const f = (vwWall?.floating ?? []).find((x) => x.id === id);
  return f ? { floating: f, win: f } : null;
}

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------
async function renderVideowall() {
  $("#page-title").textContent = "Videowall";
  vwWallKey = "";
  vwSelected.clear();
  vwFloatMode = false;

  try { vwWalls = await Api.get("/api/walls"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  if (!vwWalls.length) {
    $("#view").innerHTML = `<div class="info-box">No hay muros de video configurados. ${Api.role === "Admin"
      ? "Créelos en Configuración → Dispositivos → Videowalls → Muro de video." : "Un administrador debe configurarlos."}</div>`;
    return;
  }
  vwWall = vwWalls.find((w) => w.id === vwPrefs.wallId) ?? vwWalls[0];

  $("#view").innerHTML = `
    <div class="vw-toolbar">
      <label class="vw-wall-pick">Muro:
        <select id="vw-wall">${vwWalls.map((w) => `<option value="${w.id}" ${w.id === vwWall.id ? "selected" : ""}>${esc(w.name)}</option>`).join("")}</select>
      </label>
      <button class="btn ghost" id="vw-refresh" title="Vuelve a leer el muro y la lista de cámaras">Actualizar</button>
      <button class="btn ghost" id="vw-layouts">Layouts</button>
      <button class="btn ghost" id="vw-sync" title="Reenviar al decodificador la división de ventanas configurada">Sincronizar</button>
      <span class="vw-sep"></span>
      <button class="btn ghost vw-toggle ${vwPrefs.preview ? "on" : ""}" id="vw-preview" title="Muestra una foto de cada cámara en su ventana (se renueva cada 30 s)">👁 Vista previa</button>
      <button class="btn ghost vw-toggle" id="vw-float" title="Dibuje un rectángulo sobre el muro para abrir una ventana flotante">▣ Flotante</button>
      <span class="vw-sep"></span>
      <button class="btn danger" id="vw-clear-all">Limpiar todo</button>
    </div>
    <div class="vw-body ${vwPrefs.collapsed ? "collapsed" : ""}" id="vw-body">
      <section class="vw-wall-area">
        <div class="vw-wall-title" id="vw-title"></div>
        <div class="vw-wall-frame" id="vw-frame"></div>
        <div class="vw-hint muted">Arrastre una cámara a una ventana · arrastre una ventana sobre otra para intercambiarlas ·
          doble clic: pantalla completa · Ctrl+doble clic: todo el muro · clic derecho: más opciones</div>
      </section>
      <button class="vw-collapse" id="vw-collapse" title="Mostrar u ocultar el panel de cámaras">${vwPrefs.collapsed ? "«" : "»"}</button>
      <aside class="vw-sources">
        <div class="vw-sources-head"><b>Cámaras</b></div>
        <input id="vw-search" placeholder="Buscar canal…" autocomplete="off">
        <div class="vw-source-list" id="vw-sources"><div class="muted vw-small" style="padding:10px">Cargando cámaras…</div></div>
        <div class="muted vw-small vw-sources-help">Arrastre una cámara a una ventana del muro, o seleccione la ventana y haga clic en la cámara.</div>
      </aside>
    </div>
    <div class="vw-status muted" id="vw-status"></div>`;

  $("#vw-wall").addEventListener("change", (e) => {
    vwWall = vwWalls.find((w) => w.id === Number(e.target.value));
    vwPrefs.wallId = vwWall.id; vwSavePrefs();
    vwSelected.clear(); vwWallKey = "";
    renderVwWall();
  });
  $("#vw-refresh").addEventListener("click", async () => { await vwReload(true); vwLoadSources(); });
  $("#vw-layouts").addEventListener("click", () => layoutsModal(vwWall, () => vwReload(true)));
  $("#vw-sync").addEventListener("click", async (e) => {
    const b = e.currentTarget; b.disabled = true;
    try {
      const results = await Api.post(`/api/walls/${vwWall.id}/sync`);
      const failed = Object.values(results).filter((x) => !x.success);
      const msg = failed.length ? `Sincronización con ${failed.length} error(es): ${failed[0].error}` : "División de ventanas enviada al decodificador.";
      toast(msg, failed.length > 0); vwStatus(msg, failed.length > 0);
    } catch (err) { toast(err.error, true); }
    finally { b.disabled = false; }
  });
  $("#vw-preview").addEventListener("click", (e) => {
    vwPrefs.preview = !vwPrefs.preview; vwSavePrefs();
    e.currentTarget.classList.toggle("on", vwPrefs.preview);
    vwPreviewBucket = Math.floor(Date.now() / VW_PREVIEW_MS);
    vwWallKey = ""; renderVwWall();
  });
  $("#vw-float").addEventListener("click", (e) => {
    vwFloatMode = !vwFloatMode;
    e.currentTarget.classList.toggle("on", vwFloatMode);
    $("#vw-frame").classList.toggle("float-mode", vwFloatMode);
    vwStatus(vwFloatMode ? "Modo flotante: dibuje un rectángulo sobre el muro." : "Modo flotante desactivado.");
  });
  $("#vw-clear-all").addEventListener("click", () => {
    if (!confirm(`¿Detener la decodificación de todas las ventanas de "${vwWall.name}"?`)) return;
    vwRun(null, () => Api.post(`/api/walls/${vwWall.id}/clear-all`), "Muro limpiado.");
  });
  $("#vw-collapse").addEventListener("click", (e) => {
    vwPrefs.collapsed = !vwPrefs.collapsed; vwSavePrefs();
    $("#vw-body").classList.toggle("collapsed", vwPrefs.collapsed);
    e.currentTarget.textContent = vwPrefs.collapsed ? "«" : "»";
  });
  $("#vw-search").addEventListener("input", renderVwSources);

  vwPreviewBucket = Math.floor(Date.now() / VW_PREVIEW_MS);
  renderVwWall();
  vwLoadSources();
  vwStatus(`Muro "${vwWall.name}" cargado.`);

  clearInterval(videowallTimer);
  videowallTimer = setInterval(() => {
    if (!$("#vw-frame")) return;
    const bucket = Math.floor(Date.now() / VW_PREVIEW_MS);
    if (vwPrefs.preview && bucket !== vwPreviewBucket) { vwPreviewBucket = bucket; vwWallKey = ""; }
    vwReload(false);
  }, VW_POLL_MS);
}

/** Relee el muro elegido; `force` redibuja aunque no haya cambios. */
async function vwReload(force) {
  if (!vwWall) return;
  try {
    const fresh = await Api.get(`/api/walls/${vwWall.id}`);
    if (!$("#vw-frame") || fresh.id !== vwWall.id) return;
    vwWall = fresh;
    const i = vwWalls.findIndex((w) => w.id === fresh.id);
    if (i >= 0) vwWalls[i] = fresh;
    if (force) vwWallKey = "";
    if (!vwDragging) renderVwWall();
  } catch (err) {
    if (force) toast(err.error, true);
  }
}

/**
 * Ejecuta una operación sobre el muro. `screenId` marca ese monitor como
 * ocupado ("Aplicando cambio…"); null bloquea el muro completo.
 */
async function vwRun(screenId, op, okMessage) {
  if (screenId != null) vwBusyScreens.add(screenId); else vwWallBusy = true;
  vwWallKey = ""; renderVwWall();
  try {
    const result = await op();
    // Las operaciones por lote devuelven un diccionario de resultados; las simples, { success, error }.
    const results = result && typeof result === "object" && !("success" in result) ? Object.values(result) : result ? [result] : [];
    const failed = results.filter((r) => r && r.success === false);
    const msg = failed.length ? `${okMessage.replace(/\.$/, "")} con ${failed.length} error(es): ${failed[0].error ?? ""}` : okMessage;
    vwStatus(msg, failed.length > 0);
    if (failed.length) toast(msg, true);
  } catch (err) {
    vwStatus(err.error, true);
    toast(err.error, true);
  } finally {
    if (screenId != null) vwBusyScreens.delete(screenId); else vwWallBusy = false;
    await vwReload(true);
  }
}

const vwApi = {
  assign: (win, channelId, streamType = 0) => Api.post(`/api/walls/${vwWall.id}/assign`, { windowId: win, channelId, streamType }),
  clear: (win) => Api.post(`/api/walls/${vwWall.id}/clear`, { windowId: win }),
  swap: (a, b) => Api.post(`/api/walls/${vwWall.id}/swap`, { windowAId: a, windowBId: b }),
};

// ---------------------------------------------------------------------------
// Dibujo del muro
// ---------------------------------------------------------------------------
function renderVwWall() {
  const frame = $("#vw-frame");
  if (!frame || !vwWall) return;
  const key = JSON.stringify(vwWall) + [...vwSelected].join(",") + [...vwBusyScreens].join(",") + vwWallBusy + vwPrefs.preview + vwPreviewBucket;
  if (key === vwWallKey) return;
  vwWallKey = key;

  const w = vwWall;
  $("#vw-title").innerHTML = `<b>${esc(w.name)}</b> <span class="muted">— ${w.rows}×${w.columns} · Decodificador: ${esc(w.decoderName)}</span>`;
  frame.style.setProperty("--vw-rows", w.rows);
  frame.style.setProperty("--vw-cols", w.columns);
  // A escala 16:9 por monitor, sin pasarse del alto disponible.
  frame.style.aspectRatio = `${w.columns * 16} / ${w.rows * 9}`;
  frame.style.width = `min(100%, calc((100vh - 270px) * ${(w.columns * 16) / (w.rows * 9)}))`;
  frame.classList.toggle("float-mode", vwFloatMode);
  frame.innerHTML = "";

  const grid = document.createElement("div");
  grid.className = "vw-grid";
  frame.appendChild(grid);

  if (w.fullscreen) {
    // Una cámara ocupa TODO el muro: una sola celda grande.
    const found = vwFindWindow(w.fullscreenWindowId);
    const cell = document.createElement("div");
    cell.className = "vw-screen vw-wall-full";
    cell.style.gridArea = `1 / 1 / span ${w.rows} / span ${w.columns}`;
    cell.innerHTML = `<div class="vw-screen-head"><span class="vw-badge">MURO COMPLETO</span>
      <span class="muted vw-small">Doble clic para volver al mosaico</span></div>`;
    const body = document.createElement("div");
    body.className = "vw-win-grid";
    body.style.gridTemplateColumns = "1fr";
    body.appendChild(vwWindowTile(found?.screen ?? null, found?.win ?? { id: w.fullscreenWindowId, windowIndex: 0 }, { wallFull: true }));
    cell.appendChild(body);
    grid.appendChild(cell);
  } else {
    for (let r = 0; r < w.rows; r++) {
      for (let c = 0; c < w.columns; c++) {
        grid.appendChild(vwScreenCell(w.screens.find((s) => s.row === r && s.col === c), r, c));
      }
    }
  }

  // Ventanas flotantes encima del mosaico.
  const layer = document.createElement("div");
  layer.className = "vw-float-layer";
  frame.appendChild(layer);
  if (!w.fullscreen) (w.floating ?? []).forEach((f) => layer.appendChild(vwFloatingElement(f)));
  vwBindFloatDrawing(layer);

  if (vwWallBusy) {
    const ov = document.createElement("div");
    ov.className = "vw-overlay";
    ov.innerHTML = `<div>Aplicando cambios en el muro…</div><div class="vw-progress"></div>`;
    frame.appendChild(ov);
  }
  vwHighlightSources();
}

function vwScreenCell(screen, row, col) {
  const cell = document.createElement("div");
  cell.className = "vw-screen";
  if (!screen) {
    cell.classList.add("unused");
    cell.innerHTML = `<div class="vw-unused">F${row + 1}·C${col + 1}<br>sin salida</div>`;
    return cell;
  }
  const selectedHere = screen.windows.filter((x) => vwSelected.has(x.id));
  const selectedGrouped = selectedHere.find((x) => x.spanCols > 1 || x.spanRows > 1);

  cell.innerHTML = `
    <div class="vw-screen-head">
      <span class="vw-screen-label" title="F${row + 1}·C${col + 1} · salida ${screen.displayChannel}">${esc(screen.label || `Salida ${screen.displayChannel}`)}</span>
      ${screen.fullscreen ? `<span class="vw-badge">PANTALLA COMPLETA</span>` : ""}
      <span class="vw-head-actions">
        ${selectedHere.length >= 2 ? `<button class="vw-mini" data-act="group">⊞ Agrupar ${selectedHere.length}</button>` : ""}
        ${selectedGrouped ? `<button class="vw-mini" data-act="ungroup">⊟ Desagrupar</button>` : ""}
        <select class="vw-split" title="División del monitor" ${screen.fullscreen ? "disabled" : ""}>
          ${VW_SPLITS.map((n) => `<option value="${n}" ${n === screen.windowMode ? "selected" : ""}>${n}</option>`).join("")}
        </select>
      </span>
    </div>`;
  const body = document.createElement("div");
  body.className = "vw-win-grid";
  const shown = screen.fullscreen ? screen.windows.filter((x) => x.id === screen.fullscreenWindowId) : screen.windows;
  const cols = screen.fullscreen ? 1 : Math.ceil(Math.sqrt(Math.max(1, screen.windowMode)));
  const rows = screen.fullscreen ? 1 : Math.ceil(Math.max(1, screen.windowMode) / cols);
  body.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
  body.style.gridTemplateRows = `repeat(${rows}, minmax(0, 1fr))`;
  shown.forEach((win) => {
    const tile = vwWindowTile(screen, win, {});
    if (!screen.fullscreen) {
      tile.style.gridRow = `${Math.floor(win.windowIndex / cols) + 1} / span ${Math.max(1, win.spanRows)}`;
      tile.style.gridColumn = `${(win.windowIndex % cols) + 1} / span ${Math.max(1, win.spanCols)}`;
    }
    body.appendChild(tile);
  });
  cell.appendChild(body);

  if (vwBusyScreens.has(screen.id)) {
    const ov = document.createElement("div");
    ov.className = "vw-overlay";
    ov.innerHTML = `<div>Aplicando cambio…</div><div class="vw-progress"></div>`;
    cell.appendChild(ov);
  }

  $(".vw-split", cell).addEventListener("change", (e) => {
    const mode = Number(e.target.value);
    if (mode < screen.windowMode) {
      const lost = screen.windows.filter((x) => x.windowIndex >= mode && x.assignment).length;
      if (!confirm(`¿Reducir "${screen.label || `Salida ${screen.displayChannel}`}" a ${mode} ventana(s)?` +
        (lost ? ` Se cerrarán ${lost} video(s) que quedan fuera.` : ""))) { e.target.value = screen.windowMode; return; }
    }
    vwSelected.clear();
    vwRun(screen.id, () => Api.put(`/api/walls/${vwWall.id}/screens/${screen.id}/window-mode`, { windowMode: mode }), `Monitor dividido en ${mode}.`);
  });
  $('[data-act="group"]', cell)?.addEventListener("click", () => {
    const ids = selectedHere.map((x) => x.id);
    vwSelected.clear();
    vwRun(screen.id, () => Api.post(`/api/walls/${vwWall.id}/screens/${screen.id}/group`, { windowIds: ids }), "Ventanas agrupadas.");
  });
  $('[data-act="ungroup"]', cell)?.addEventListener("click", () => {
    vwSelected.clear();
    vwRun(screen.id, () => Api.post(`/api/walls/${vwWall.id}/windows/${selectedGrouped.id}/ungroup`), "Ventana desagrupada.");
  });
  return cell;
}

function vwWindowTile(screen, win, { wallFull = false }) {
  const tile = document.createElement("div");
  const a = win.assignment;
  tile.className = "vw-win" + (a ? " assigned" : "") + (vwSelected.has(win.id) ? " selected" : "");
  tile.dataset.id = win.id;
  tile.draggable = !!a && !wallFull;
  const preview = vwPrefs.preview && a && !a.isExternal;
  tile.innerHTML = a
    ? `${preview ? `<img class="vw-snap" src="${vwSnapshotUrl(a)}" alt="" onerror="this.remove()">` : ""}
       <div class="vw-win-text ${preview ? "strip" : ""}">
         <span class="vw-win-name">${esc(a.channelName)}</span>
         <span class="vw-win-sub">${esc(vwSubtitle(a))}</span>
       </div>
       ${wallFull ? "" : `<button class="vw-x" title="Cerrar este video">✕</button>`}`
    : `<span class="vw-win-free">${win.windowIndex + 1}</span>`;
  tile.title = a ? `${a.channelName} · ${vwSubtitle(a)}` : `Ventana ${win.windowIndex + 1} · vacía`;

  if (wallFull) {
    tile.addEventListener("dblclick", () =>
      vwRun(null, () => Api.post(`/api/walls/${vwWall.id}/exit-wall-fullscreen`), "Muro restaurado."));
    return tile;
  }

  tile.addEventListener("click", (e) => {
    if (e.target.closest(".vw-x")) return;
    if (e.ctrlKey || e.metaKey) {
      if (vwSelected.has(win.id)) vwSelected.delete(win.id); else vwSelected.add(win.id);
    } else {
      const only = vwSelected.size === 1 && vwSelected.has(win.id);
      vwSelected.clear();
      if (!only) vwSelected.add(win.id);
    }
    renderVwWall();
  });
  tile.addEventListener("dblclick", (e) => {
    if (e.ctrlKey || e.metaKey) {
      if (!a) { toast("Ctrl+doble clic sobre una ventana con cámara para llevarla a todo el muro.", true); return; }
      vwRun(null, () => Api.post(`/api/walls/${vwWall.id}/wall-fullscreen`, { windowId: win.id }), "Cámara en todo el muro.");
      return;
    }
    if (screen.fullscreen) {
      vwRun(screen.id, () => Api.post(`/api/walls/${vwWall.id}/screens/${screen.id}/exit-fullscreen`), "Layout del monitor restaurado.");
    } else if (a) {
      vwRun(screen.id, () => Api.post(`/api/walls/${vwWall.id}/fullscreen`, { windowId: win.id }), "Cámara en pantalla completa.");
    }
  });
  $(".vw-x", tile)?.addEventListener("click", () =>
    vwRun(screen.id, () => vwApi.clear(win.id), "Video cerrado."));
  tile.addEventListener("contextmenu", (e) => { e.preventDefault(); vwContextMenu(e, screen, win); });

  // Arrastrar y soltar: cámara del panel → asignar; ventana → intercambiar.
  tile.addEventListener("dragstart", (e) => {
    vwDragging = true;
    e.dataTransfer.setData("text/vw-window", String(win.id));
    e.dataTransfer.effectAllowed = "move";
  });
  tile.addEventListener("dragend", () => { vwDragging = false; });
  tile.addEventListener("dragover", (e) => {
    const t = e.dataTransfer.types;
    if (t.includes("text/vw-channel") || t.includes("text/vw-window")) { e.preventDefault(); tile.classList.add("drop"); }
  });
  tile.addEventListener("dragleave", () => tile.classList.remove("drop"));
  tile.addEventListener("drop", (e) => {
    e.preventDefault();
    tile.classList.remove("drop");
    vwDragging = false;
    const channelId = Number(e.dataTransfer.getData("text/vw-channel"));
    const fromWindow = Number(e.dataTransfer.getData("text/vw-window"));
    if (channelId) vwRun(screen.id, () => vwApi.assign(win.id, channelId), "Cámara asignada.");
    else if (fromWindow && fromWindow !== win.id) vwRun(screen.id, () => vwApi.swap(fromWindow, win.id), "Ventanas intercambiadas.");
  });
  return tile;
}

function vwContextMenu(e, screen, win) {
  $("#vw-menu")?.remove();
  const a = win.assignment;
  const grouped = win.spanCols > 1 || win.spanRows > 1;
  const items = [
    a ? ["wall", "🖵 Ocupar todo el muro"] : null,
    a ? ["full", screen.fullscreen ? "⊡ Volver al mosaico" : "⛶ Pantalla completa del monitor"] : null,
    ["sub4", "⊞ Dividir esta ventana en 4"],
    ["sub9", "⊞ Dividir esta ventana en 9"],
    ["sub16", "⊞ Dividir esta ventana en 16"],
    grouped ? ["ungroup", "⊟ Desagrupar"] : null,
    a ? ["clear", "✕ Cerrar este video"] : null,
  ].filter(Boolean);
  const menu = document.createElement("div");
  menu.id = "vw-menu";
  menu.className = "vw-menu";
  menu.innerHTML = items.map(([k, l]) => `<button data-k="${k}">${l}</button>`).join("");
  menu.style.left = `${Math.min(e.clientX, window.innerWidth - 240)}px`;
  menu.style.top = `${Math.min(e.clientY, window.innerHeight - items.length * 34 - 10)}px`;
  document.body.appendChild(menu);
  const close = () => { menu.remove(); document.removeEventListener("mousedown", outside, true); };
  const outside = (ev) => { if (!menu.contains(ev.target)) close(); };
  setTimeout(() => document.addEventListener("mousedown", outside, true));
  menu.addEventListener("click", (ev) => {
    const k = ev.target.closest("button")?.dataset.k;
    if (!k) return;
    close();
    const base = `/api/walls/${vwWall.id}`;
    if (k === "wall") vwRun(null, () => Api.post(`${base}/wall-fullscreen`, { windowId: win.id }), "Cámara en todo el muro.");
    else if (k === "full") vwRun(screen.id, () => screen.fullscreen
      ? Api.post(`${base}/screens/${screen.id}/exit-fullscreen`)
      : Api.post(`${base}/fullscreen`, { windowId: win.id }), screen.fullscreen ? "Layout del monitor restaurado." : "Cámara en pantalla completa.");
    else if (k.startsWith("sub")) {
      const parts = Number(k.slice(3));
      vwRun(screen.id, () => Api.post(`${base}/windows/${win.id}/subdivide`, { parts }), `Ventana dividida en ${parts}.`);
    } else if (k === "ungroup") vwRun(screen.id, () => Api.post(`${base}/windows/${win.id}/ungroup`), "Ventana desagrupada.");
    else if (k === "clear") vwRun(screen.id, () => vwApi.clear(win.id), "Video cerrado.");
  });
}

// ---------------------------------------------------------------------------
// Ventanas flotantes
// ---------------------------------------------------------------------------
function vwFloatingElement(f) {
  const w = vwWall;
  const el = document.createElement("div");
  el.className = "vw-float" + (f.assignment ? " assigned" : "");
  const place = (x, y, fw, fh) => Object.assign(el.style, {
    left: `${(x / w.columns) * 100}%`, top: `${(y / w.rows) * 100}%`,
    width: `${(fw / w.columns) * 100}%`, height: `${(fh / w.rows) * 100}%`,
  });
  place(f.x, f.y, f.w, f.h);
  const a = f.assignment;
  const preview = vwPrefs.preview && a && !a.isExternal;
  el.innerHTML = `
    ${preview ? `<img class="vw-snap" src="${vwSnapshotUrl(a)}" alt="" onerror="this.remove()">` : ""}
    <div class="vw-float-head"><span class="vw-badge">FLOTANTE${f.fullscreen ? " · PANTALLA COMPLETA" : ""}</span>
      <button class="vw-x" title="Cerrar la ventana flotante">✕</button></div>
    <div class="vw-win-text ${preview ? "strip" : ""}">
      ${a ? `<span class="vw-win-name">${esc(a.channelName)}</span><span class="vw-win-sub">${esc(vwSubtitle(a))}</span>`
          : `<span class="vw-win-free">Sin cámara · arrastre una aquí</span>`}
    </div>
    <span class="vw-resize" title="Arrastre para cambiar el tamaño"></span>`;
  el.title = "Arrastre para mover · esquina para cambiar el tamaño · doble clic: pantalla completa";

  const base = `/api/walls/${w.id}/floating/${f.id}`;
  $(".vw-x", el).addEventListener("click", (e) => {
    e.stopPropagation();
    vwRun(null, () => Api.delete(base), "Ventana flotante cerrada.");
  });
  el.addEventListener("dblclick", () => vwRun(null, () => Api.post(`${base}/fullscreen`), "Pantalla completa de la flotante alternada."));
  el.addEventListener("dragover", (e) => { if (e.dataTransfer.types.includes("text/vw-channel")) { e.preventDefault(); el.classList.add("drop"); } });
  el.addEventListener("dragleave", () => el.classList.remove("drop"));
  el.addEventListener("drop", (e) => {
    e.preventDefault(); el.classList.remove("drop"); vwDragging = false;
    const channelId = Number(e.dataTransfer.getData("text/vw-channel"));
    if (channelId) vwRun(null, () => Api.post(`${base}/assign`, { channelId, streamType: 0 }), "Cámara asignada a la flotante.");
  });

  // Mover (arrastrando el cuerpo) y cambiar el tamaño (esquina): se envía al soltar.
  el.addEventListener("pointerdown", (e) => {
    if (e.button !== 0 || e.target.closest(".vw-x")) return;
    const resizing = !!e.target.closest(".vw-resize");
    const frame = $("#vw-frame");
    const ux = frame.clientWidth / w.columns, uy = frame.clientHeight / w.rows;
    const start = { px: e.clientX, py: e.clientY, x: f.x, y: f.y, w: f.w, h: f.h };
    let cur = { ...start }, moved = false;
    el.setPointerCapture(e.pointerId);
    vwDragging = true;
    const onMove = (ev) => {
      const dx = (ev.clientX - start.px) / ux, dy = (ev.clientY - start.py) / uy;
      if (Math.abs(ev.clientX - start.px) + Math.abs(ev.clientY - start.py) > 3) moved = true;
      if (resizing) {
        cur.w = Math.min(w.columns - start.x, Math.max(0.2, start.w + dx));
        cur.h = Math.min(w.rows - start.y, Math.max(0.2, start.h + dy));
      } else {
        cur.x = Math.min(w.columns - start.w, Math.max(0, start.x + dx));
        cur.y = Math.min(w.rows - start.h, Math.max(0, start.y + dy));
      }
      place(cur.x, cur.y, cur.w, cur.h);
    };
    const onUp = () => {
      el.removeEventListener("pointermove", onMove);
      el.removeEventListener("pointerup", onUp);
      vwDragging = false;
      if (!moved) return;
      const r = (v) => Math.round(v * 100) / 100;
      vwRun(null, () => Api.put(base, { x: r(cur.x), y: r(cur.y), w: r(cur.w), h: r(cur.h) }),
        resizing ? "Tamaño de la flotante actualizado." : "Flotante movida.");
    };
    el.addEventListener("pointermove", onMove);
    el.addEventListener("pointerup", onUp);
  });
  return el;
}

/** Modo flotante: dibujar un rectángulo sobre el muro y elegir la cámara. */
function vwBindFloatDrawing(layer) {
  layer.addEventListener("pointerdown", (e) => {
    if (!vwFloatMode || e.button !== 0 || e.target !== layer) return;
    const rect = layer.getBoundingClientRect();
    const sx = e.clientX - rect.left, sy = e.clientY - rect.top;
    const band = document.createElement("div");
    band.className = "vw-band";
    layer.appendChild(band);
    layer.setPointerCapture(e.pointerId);
    vwDragging = true;
    let ex = sx, ey = sy;
    const onMove = (ev) => {
      ex = Math.min(rect.width, Math.max(0, ev.clientX - rect.left));
      ey = Math.min(rect.height, Math.max(0, ev.clientY - rect.top));
      Object.assign(band.style, { left: `${Math.min(sx, ex)}px`, top: `${Math.min(sy, ey)}px`, width: `${Math.abs(ex - sx)}px`, height: `${Math.abs(ey - sy)}px` });
    };
    const onUp = () => {
      layer.removeEventListener("pointermove", onMove);
      layer.removeEventListener("pointerup", onUp);
      vwDragging = false;
      band.remove();
      const pw = Math.abs(ex - sx), ph = Math.abs(ey - sy);
      if (pw < 20 || ph < 20) { vwStatus("Rectángulo demasiado chico: dibuje la ventana flotante arrastrando sobre el muro."); return; }
      const ux = rect.width / vwWall.columns, uy = rect.height / vwWall.rows;
      const r = (v) => Math.round(v * 100) / 100;
      vwFloatCameraModal({ x: r(Math.min(sx, ex) / ux), y: r(Math.min(sy, ey) / uy), w: r(pw / ux), h: r(ph / uy) });
    };
    layer.addEventListener("pointermove", onMove);
    layer.addEventListener("pointerup", onUp);
  });
}

function vwFloatCameraModal(rect) {
  const options = vwSources.map(({ device, channels }) =>
    `<optgroup label="${esc(device.name)}">${channels.map((c) => `<option value="${c.id}">${c.channelNumber} — ${esc(c.name)}</option>`).join("")}</optgroup>`).join("");
  if (!options) { toast("No hay cámaras con señal para abrir en la flotante.", true); return; }
  openModal(`
    <h3>Nueva ventana flotante</h3>
    <p class="muted" style="font-size:12.5px">Posición ${rect.x}·${rect.y}, tamaño ${rect.w}×${rect.h} (en monitores).</p>
    <div class="field"><label>Cámara</label><select id="vwf-channel">${options}</select></div>
    <div class="field"><label>Stream</label>
      <select id="vwf-stream"><option value="0">Principal</option><option value="1">Secundario (sub)</option></select></div>
    <div class="modal-actions">
      <button class="btn ghost" id="vwf-cancel">Cancelar</button>
      <button class="btn" id="vwf-ok">Abrir en el muro</button>
    </div>`);
  $("#vwf-cancel").addEventListener("click", closeModal);
  $("#vwf-ok").addEventListener("click", () => {
    const channelId = Number($("#vwf-channel").value), streamType = Number($("#vwf-stream").value);
    closeModal();
    vwRun(null, () => Api.post(`/api/walls/${vwWall.id}/floating`, { ...rect, channelId, streamType }), "Ventana flotante abierta.");
  });
}

// ---------------------------------------------------------------------------
// Panel de cámaras
// ---------------------------------------------------------------------------
const VW_DEVICE_TYPES = { Dvr: "DVR", Nvr: "NVR", Xvr: "XVR", Camera: "Cámara" };

async function vwLoadSources() {
  try {
    const devices = (await Api.get("/api/devices")).sort((a, b) => a.name.localeCompare(b.name, "es"));
    const lists = await Promise.all(devices.map((d) => Api.get(`/api/devices/${d.id}/channels`).catch(() => [])));
    vwSources = devices.map((device, i) => ({ device, channels: lists[i].filter((c) => c.enabled && c.isOnline) }));
  } catch (err) {
    const box = $("#vw-sources");
    if (box) box.innerHTML = `<div class="error-box" style="margin:10px">${esc(err.error)}</div>`;
    return;
  }
  renderVwSources();
}

function renderVwSources() {
  const box = $("#vw-sources");
  if (!box) return;
  const q = ($("#vw-search")?.value || "").trim().toLowerCase();
  const openBefore = new Set($$("details[open]", box).map((d) => d.dataset.id));
  const groups = vwSources.map(({ device, channels }) => {
    const deviceHit = !q || device.name.toLowerCase().includes(q);
    const shown = deviceHit ? channels : channels.filter((c) => c.name.toLowerCase().includes(q) || String(c.channelNumber) === q);
    if (q && !shown.length) return "";
    const open = q || openBefore.has(String(device.id)) || vwSources.length === 1;
    return `
      <details data-id="${device.id}" ${open ? "open" : ""}>
        <summary>${esc(device.name)} <span class="muted">· ${VW_DEVICE_TYPES[device.deviceType] ?? esc(device.deviceType)}</span></summary>
        ${shown.length ? shown.map((c) => `
          <div class="vw-chip" draggable="true" data-channel="${c.id}" data-device="${device.id}" data-number="${c.channelNumber}"
               title="${esc(device.name)} · canal ${c.channelNumber}">
            <span class="vw-chip-num">${c.channelNumber}</span> ${esc(c.name)}
          </div>`).join("") : `<div class="muted vw-small" style="padding:4px 10px 8px">Sin canales con señal.</div>`}
      </details>`;
  }).join("");
  box.innerHTML = groups || `<div class="muted vw-small" style="padding:10px">${q ? "Ningún canal coincide." : "No hay cámaras con señal."}</div>`;

  $$(".vw-chip", box).forEach((chip) => {
    chip.addEventListener("dragstart", (e) => {
      vwDragging = true;
      e.dataTransfer.setData("text/vw-channel", chip.dataset.channel);
      e.dataTransfer.effectAllowed = "copy";
    });
    chip.addEventListener("dragend", () => { vwDragging = false; });
    chip.addEventListener("click", () => {
      const target = vwSelected.size === 1 ? vwFindWindow([...vwSelected][0]) : null;
      if (!target) { vwStatus("Seleccione primero UNA ventana del muro, o arrastre la cámara sobre ella."); return; }
      const channelId = Number(chip.dataset.channel);
      if (target.floating) vwRun(null, () => Api.post(`/api/walls/${vwWall.id}/floating/${target.win.id}/assign`, { channelId, streamType: 0 }), "Cámara asignada a la flotante.");
      else vwRun(target.screen.id, () => vwApi.assign(target.win.id, channelId), "Cámara asignada.");
    });
  });
  vwHighlightSources();
}

/** Marca en el panel la cámara de la ventana seleccionada (como el cliente). */
function vwHighlightSources() {
  const ids = new Set([...vwSelected].map((id) => vwFindWindow(id)?.win.assignment?.channelId).filter(Boolean));
  $$("#vw-sources .vw-chip").forEach((c) => c.classList.toggle("active", ids.has(Number(c.dataset.channel))));
}
