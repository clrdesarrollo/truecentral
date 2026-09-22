// CLR TrueCentral VMS — panel: editor visual de automatizaciones.
//
// Un diagrama de flujo, como en Bizagi o Lucid: un punto de partida (el
// disparador), y desde ahí se van agregando pasos —condiciones sí/no,
// esperas, acciones y fines— conectados con flechas. Se arma con el catálogo
// del servidor (/api/workflows/catalog) y las listas de equipos; lo que se
// dibuja viaja tal cual al servidor (nodos con posición + conexiones), que lo
// valida y lo ejecuta recorriendo las flechas.
//
// Se carga después de workflows.js (usa WF_FIELDS, wfFieldHtml, wfReadField y
// las listas de etiquetas) y antes de app.js, que lo referencia desde su tabla
// de rutas (#/workflows/edit?id=N).
"use strict";

// --- Estado del editor -----------------------------------------------------
let wfEd = null;

const WFE_NODE_WIDTH = 220;
const WFE_ICONS = {
  trigger: "⚡", condition: "❓", delay: "⏱", end: "⏹",
  snapshot: "📷", email: "✉️", ftp: "📤", http: "🌐", speaker: "🔊", notify: "🔔",
  door: "🚪", panel: "🛡️", "ptz-preset": "🎯",
};
const WFE_TRIGGER_ICONS = {
  "alarm-event": "🚨", "panel-status": "📡", "device-status": "🔌", "video-event": "🎥",
  "plate-recognized": "🚗", "access-event": "🪪", schedule: "🕒", webhook: "🔗",
};

const WFE_VIDEO_KINDS = [
  ["Motion", "Detección de movimiento"], ["LineCrossing", "Cruce de línea"], ["Intrusion", "Intrusión"],
  ["RegionEntrance", "Entrada a región"], ["RegionExit", "Salida de región"], ["Loitering", "Merodeo"],
  ["ObjectLeftOrTaken", "Objeto abandonado / retirado"], ["Parking", "Estacionamiento indebido"],
  ["FastMoving", "Movimiento rápido"], ["Crowd", "Aglomeración"], ["FaceDetection", "Detección de rostro"],
  ["PeopleCounting", "Conteo de personas"], ["VideoLoss", "Pérdida de video"], ["Tamper", "Cámara tapada / sabotaje"],
  ["VideoException", "Anomalía de video"], ["AudioException", "Anomalía de audio"],
  ["AlarmInput", "Entrada de alarma (contacto)"], ["DeviceFault", "Falla del equipo"], ["Other", "Otro"],
];
const WFE_DEVICE_KINDS = [["video", "Cámaras y grabadores"], ["access", "Terminales de acceso"], ["speaker", "Parlantes IP"]];
const WFE_DEVICE_STATUSES = [["Offline", "Sin conexión"], ["AuthFailed", "Credenciales rechazadas"], ["Online", "En línea (recuperado)"]];
const WFE_ACCESS_KINDS = [["Granted", "Acceso concedido"], ["Denied", "Acceso denegado"], ["Alarm", "Alarma de puerta (forzada, mantenida abierta, sabotaje)"],
  ["DoorOpen", "Puerta abierta"], ["DoorClose", "Puerta cerrada"], ["Other", "Otro"]];
const WFE_CREDENTIALS = [["Card", "Tarjeta"], ["Fingerprint", "Huella"], ["Face", "Rostro"], ["Pin", "Clave"],
  ["Qr", "Código QR"], ["Plate", "Patente"], ["Remote", "Orden remota"], ["ExitButton", "Botón de salida"], ["Unknown", "Desconocida"]];

// ---------------------------------------------------------------------------
// Página
// ---------------------------------------------------------------------------

async function renderWorkflowEditor() {
  const params = new URLSearchParams((location.hash.split("?")[1]) || "");
  const id = Number(params.get("id")) || 0;
  $("#page-title").textContent = id ? "Editar automatización" : "Nueva automatización";
  $("#view").classList.add("view-editor");
  $("#view").innerHTML = `<div class="info-box" style="margin:24px">Cargando el editor…</div>`;

  if (Api.role !== "Admin") {
    $("#view").innerHTML = `<div class="error-box" style="margin:24px">Solo un administrador puede crear o editar automatizaciones.</div>`;
    return;
  }

  let catalog, cameras, devices, doors, panels, speakers, audio, accessDevices, workflow = null;
  try {
    [catalog, cameras, devices, doors, panels, speakers, audio, accessDevices] = await Promise.all([
      wfCatalog(), wfCameras(), Api.get("/api/workflows/devices"), Api.get("/api/workflows/doors"),
      Api.get("/api/alarms/panels"), wfSpeakers(), Api.get("/api/workflows/audio"), Api.get("/api/access/devices"),
    ]);
    if (id) workflow = await Api.get(`/api/workflows/${id}`);
  } catch (err) {
    $("#view").innerHTML = `<div class="error-box" style="margin:24px">${esc(err.error)}</div>`;
    return;
  }

  const lists = { catalog, cameras, devices, doors, panels, speakers, audio, accessDevices };
  wfEd = {
    id, lists,
    name: workflow?.name ?? "",
    description: workflow?.description ?? "",
    enabled: workflow ? workflow.enabled : true,
    triggerType: workflow?.triggerType ?? catalog.triggers[0].key,
    cooldownSeconds: workflow?.cooldownSeconds ?? 60,
    nodes: [], edges: [],
    selected: null,          // { kind: "node", id } | { kind: "edge", index }
    view: { x: 40, y: 30, scale: 1 },
    results: {},             // nodeId → { success, detail } (última prueba)
    seq: 1,
  };

  if (workflow?.graph?.nodes?.length) {
    wfEd.nodes = workflow.graph.nodes.map((n) => ({
      id: n.id, kind: n.kind, x: n.x, y: n.y, label: n.label || "",
      type: n.type || null, config: JSON.parse(JSON.stringify(n.config ?? {})),
      enabled: n.enabled !== false, delaySeconds: n.delaySeconds || 0,
      conditions: n.kind === "trigger"
        ? JSON.parse(JSON.stringify(workflow.conditions ?? {}))
        : JSON.parse(JSON.stringify(n.conditions ?? {})),
      secret: "", hasSecret: !!n.hasSecret, actionId: n.actionId || 0,
    }));
    wfEd.edges = (workflow.graph.edges || []).map((e) => ({ from: e.from, to: e.to, port: e.port || "next" }));
    // Los identificadores nuevos no deben chocar con los guardados.
    wfEd.seq = Math.max(1, ...wfEd.nodes.map((n) => Number((n.id.match(/\d+$/) || [0])[0]) + 1));
  } else {
    wfEd.nodes = [{ id: "trigger", kind: "trigger", x: 300, y: 30, label: "", conditions: {}, config: {} }];
  }

  wfeRenderShell();
  wfeDraw();
  wfeSelect(id ? null : { kind: "node", id: "trigger" });
  if (!workflow?.graph?.nodes?.some((n) => n.x || n.y)) wfeAutoLayout(false);
  wfeFit();
}

function wfeRenderShell() {
  const { catalog } = wfEd.lists;
  const groups = new Map();
  catalog.actions.forEach((a) => {
    const g = a.group || "Acciones";
    if (!groups.has(g)) groups.set(g, []);
    groups.get(g).push(a);
  });

  $("#view").innerHTML = `
    <div class="wfe">
      <div class="wfe-top">
        <button class="btn ghost" id="wfe-back" title="Volver al listado">← Volver</button>
        <input id="wfe-name" class="wfe-name" maxlength="128" placeholder="Nombre de la automatización" value="${esc(wfEd.name)}">
        <input id="wfe-description" class="wfe-description" maxlength="512" placeholder="Descripción (opcional)" value="${esc(wfEd.description)}">
        <label class="wfe-inline" title="Tiempo mínimo entre dos ejecuciones: un sensor que rebota no ejecuta veinte veces">
          Mínimo entre ejecuciones <input id="wfe-cooldown" type="number" min="0" max="86400" value="${wfEd.cooldownSeconds}"> s
        </label>
        <label class="wfe-inline"><input type="checkbox" id="wfe-enabled" ${wfEd.enabled ? "checked" : ""}> Activa</label>
        <span class="wfe-spacer"></span>
        <button class="btn ghost" id="wfe-layout" title="Ordenar los pasos automáticamente">Ordenar</button>
        <button class="btn ghost" id="wfe-fit" title="Ajustar el diagrama a la ventana">Ajustar</button>
        <button class="btn ghost" id="wfe-test" title="Guarda y ejecuta las acciones DE VERDAD con un evento de ejemplo">Probar</button>
        <button class="btn" id="wfe-save">Guardar</button>
      </div>
      <div id="wfe-error"></div>
      <div class="wfe-body">
        <aside class="wfe-palette">
          <div class="wfe-palette-title">Pasos</div>
          <div class="wfe-palette-hint">Haga clic para agregar después del paso seleccionado, o arrástrelo al lienzo.</div>
          <div class="wfe-palette-group">Flujo</div>
          <button class="wfe-item kind-condition" draggable="true" data-kind="condition" title="Pregunta sí/no sobre el evento: cada respuesta sigue por su propia flecha">${WFE_ICONS.condition} Condición (sí / no)</button>
          <button class="wfe-item kind-delay" draggable="true" data-kind="delay" title="Espera unos segundos antes de seguir">${WFE_ICONS.delay} Espera</button>
          <button class="wfe-item kind-end" draggable="true" data-kind="end" title="Termina la rama (opcional)">${WFE_ICONS.end} Fin</button>
          ${[...groups.entries()].map(([group, actions]) => `
            <div class="wfe-palette-group">${esc(group)}</div>
            ${actions.map((a) => `<button class="wfe-item kind-action" draggable="true" data-kind="action" data-type="${esc(a.key)}" title="${esc(a.description)}">${WFE_ICONS[a.key] || "▶"} ${esc(a.label)}</button>`).join("")}`).join("")}
        </aside>
        <div class="wfe-canvas" id="wfe-canvas" tabindex="0">
          <svg class="wfe-svg" id="wfe-svg">
            <defs>
              <marker id="wfe-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">
                <path d="M 0 0 L 10 5 L 0 10 z" fill="currentColor"></path>
              </marker>
            </defs>
            <g id="wfe-edge-layer"></g>
            <path id="wfe-temp-edge" class="wfe-edge temp" d="" hidden></path>
          </svg>
          <div class="wfe-nodes" id="wfe-nodes"></div>
          <div class="wfe-zoom">
            <button class="btn ghost" id="wfe-zoom-in" title="Acercar">+</button>
            <button class="btn ghost" id="wfe-zoom-out" title="Alejar">−</button>
          </div>
        </div>
        <aside class="wfe-props" id="wfe-props"></aside>
      </div>
    </div>`;

  $("#wfe-back").addEventListener("click", () => { location.hash = "#/workflows"; });
  $("#wfe-name").addEventListener("input", (e) => { wfEd.name = e.target.value; });
  $("#wfe-description").addEventListener("input", (e) => { wfEd.description = e.target.value; });
  $("#wfe-cooldown").addEventListener("change", (e) => { wfEd.cooldownSeconds = Number(e.target.value) || 0; });
  $("#wfe-enabled").addEventListener("change", (e) => { wfEd.enabled = e.target.checked; });
  $("#wfe-layout").addEventListener("click", () => { wfeAutoLayout(true); wfeFit(); });
  $("#wfe-fit").addEventListener("click", wfeFit);
  $("#wfe-save").addEventListener("click", () => wfeSave(false));
  $("#wfe-test").addEventListener("click", () => wfeSave(true));
  $("#wfe-zoom-in").addEventListener("click", () => wfeZoomBy(1.2));
  $("#wfe-zoom-out").addEventListener("click", () => wfeZoomBy(1 / 1.2));

  // Paleta: clic = agregar tras el seleccionado; arrastre = soltar donde se quiera.
  $$(".wfe-palette .wfe-item").forEach((item) => {
    item.addEventListener("click", () => wfeAddFromPalette(item.dataset.kind, item.dataset.type, null));
    item.addEventListener("dragstart", (e) => {
      e.dataTransfer.setData("text/plain", JSON.stringify({ kind: item.dataset.kind, type: item.dataset.type || null }));
      e.dataTransfer.effectAllowed = "copy";
    });
  });
  const canvas = $("#wfe-canvas");
  canvas.addEventListener("dragover", (e) => { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; });
  canvas.addEventListener("drop", (e) => {
    e.preventDefault();
    let data;
    try { data = JSON.parse(e.dataTransfer.getData("text/plain")); } catch { return; }
    const p = wfeCanvasPoint(e.clientX, e.clientY);
    wfeAddFromPalette(data.kind, data.type, { x: p.x - WFE_NODE_WIDTH / 2, y: p.y - 20 });
  });

  wfeBindCanvas(canvas);
}

// ---------------------------------------------------------------------------
// Modelo: nodos y conexiones
// ---------------------------------------------------------------------------

function wfeNode(id) { return wfEd.nodes.find((n) => n.id === id); }

function wfeNewId(prefix) {
  let id;
  do { id = `${prefix}${wfEd.seq++}`; } while (wfeNode(id));
  return id;
}

/// Puertos de salida de un nodo, en orden de dibujo.
function wfeOutPorts(node) {
  switch (node.kind) {
    case "condition": return [["yes", "Sí"], ["no", "No"]];
    case "action": return [["next", "Siguiente"], ["error", "Si falla"]];
    case "end": return [];
    default: return [["next", "Siguiente"]];
  }
}

function wfeDefaultConfig(type) {
  const config = {};
  (WF_FIELDS[type] || []).forEach((f) => { if (f.def !== undefined && f.k !== "secret") config[f.k] = f.def; });
  return config;
}

/// Agrega un paso. Sin posición, va debajo del seleccionado (o del último) y
/// se conecta solo desde su primer puerto libre.
function wfeAddFromPalette(kind, type, position) {
  const node = {
    id: wfeNewId(kind === "action" ? "a" : kind[0]),
    kind, x: 0, y: 0, label: "",
    type: kind === "action" ? type : null,
    config: kind === "action" ? wfeDefaultConfig(type) : {},
    enabled: true, delaySeconds: kind === "delay" ? 10 : 0,
    conditions: {}, secret: "", hasSecret: false, actionId: 0,
  };

  let anchor = wfEd.selected?.kind === "node" ? wfeNode(wfEd.selected.id) : null;
  if (!anchor && !position) anchor = wfEd.nodes[wfEd.nodes.length - 1];

  if (position) {
    node.x = Math.round(position.x); node.y = Math.round(position.y);
  } else {
    // Debajo del ancla; si ya hay algo ahí, a la derecha.
    node.x = anchor.x; node.y = anchor.y + 150;
    while (wfEd.nodes.some((n) => Math.abs(n.x - node.x) < WFE_NODE_WIDTH && Math.abs(n.y - node.y) < 100)) node.x += WFE_NODE_WIDTH + 40;
  }
  wfEd.nodes.push(node);

  if (anchor && anchor.kind !== "end" && !position) {
    const ports = wfeOutPorts(anchor);
    const free = ports.find(([p]) => !wfEd.edges.some((e) => e.from === anchor.id && e.port === p)) || ports[0];
    if (free) wfEd.edges.push({ from: anchor.id, to: node.id, port: free[0] });
  }
  wfeDraw();
  wfeSelect({ kind: "node", id: node.id });
}

/// Agrega un paso conectado a un puerto concreto (menú del puerto).
function wfeAddAfterPort(fromId, port, kind, type) {
  const from = wfeNode(fromId);
  const siblings = wfEd.edges.filter((e) => e.from === fromId).length;
  const node = {
    id: wfeNewId(kind === "action" ? "a" : kind[0]),
    kind, x: from.x + siblings * (WFE_NODE_WIDTH + 40), y: from.y + 150, label: "",
    type: kind === "action" ? type : null,
    config: kind === "action" ? wfeDefaultConfig(type) : {},
    enabled: true, delaySeconds: kind === "delay" ? 10 : 0,
    conditions: {}, secret: "", hasSecret: false, actionId: 0,
  };
  while (wfEd.nodes.some((n) => Math.abs(n.x - node.x) < WFE_NODE_WIDTH && Math.abs(n.y - node.y) < 100)) node.x += WFE_NODE_WIDTH + 40;
  wfEd.nodes.push(node);
  wfEd.edges.push({ from: fromId, to: node.id, port });
  wfeDraw();
  wfeSelect({ kind: "node", id: node.id });
}

function wfeRemoveSelected() {
  const s = wfEd.selected;
  if (!s) return;
  if (s.kind === "edge") {
    wfEd.edges.splice(s.index, 1);
  } else {
    const node = wfeNode(s.id);
    if (!node || node.kind === "trigger") { toast("El punto de partida no se puede eliminar.", true); return; }
    wfEd.nodes = wfEd.nodes.filter((n) => n.id !== s.id);
    wfEd.edges = wfEd.edges.filter((e) => e.from !== s.id && e.to !== s.id);
  }
  wfEd.selected = null;
  wfeDraw();
  wfeProps();
}

function wfeConnect(fromId, port, toId) {
  if (fromId === toId) return;
  const to = wfeNode(toId);
  if (!to || to.kind === "trigger") { toast("Nada puede conectarse hacia el punto de partida.", true); return; }
  if (wfEd.edges.some((e) => e.from === fromId && e.port === port && e.to === toId)) return;
  wfEd.edges.push({ from: fromId, to: toId, port });
  wfeDraw();
  wfeSelect({ kind: "edge", index: wfEd.edges.length - 1 });
}

// ---------------------------------------------------------------------------
// Dibujo
// ---------------------------------------------------------------------------

function wfeTitle(node) {
  const { catalog } = wfEd.lists;
  switch (node.kind) {
    case "trigger": {
      const t = catalog.triggers.find((x) => x.key === wfEd.triggerType);
      return `${WFE_TRIGGER_ICONS[wfEd.triggerType] || "⚡"} ${t?.label ?? wfEd.triggerType}`;
    }
    case "condition": return `${WFE_ICONS.condition} ${node.label || "Condición"}`;
    case "delay": return `${WFE_ICONS.delay} ${node.label || "Espera"}`;
    case "end": return `${WFE_ICONS.end} ${node.label || "Fin"}`;
    default: {
      const a = catalog.actions.find((x) => x.key === node.type);
      return `${WFE_ICONS[node.type] || "▶"} ${node.label || a?.label || node.type}`;
    }
  }
}

/// Resumen corto de lo que hace el paso (segunda línea de la tarjeta).
function wfeSubtitle(node) {
  const L = wfEd.lists;
  const c = node.config || {};
  const n = (list) => (list || []).length;
  switch (node.kind) {
    case "trigger": return wfeConditionsSummary(node.conditions, wfEd.triggerType) || "Cualquier evento";
    case "condition": return wfeConditionsSummary(node.conditions, wfEd.triggerType) || "(sin condiciones: siempre «Sí»)";
    case "delay": return `${node.delaySeconds || 0} s`;
    case "end": return "";
  }
  switch (node.type) {
    case "snapshot": return n(c.channelIds) ? `${n(c.channelIds)} cámara(s)${c.count > 1 ? ` × ${c.count}` : ""}` : "sin cámaras";
    case "email": return c.to ? `a ${c.to}` : "sin destinatarios";
    case "ftp": return c.host ? `${c.host}${c.remoteDirectory ? " · " + c.remoteDirectory : ""}` : "sin servidor";
    case "http": return c.url ? `${c.method || "POST"} ${c.url}` : "sin URL";
    case "speaker": {
      const mode = c.mode || "inventory";
      if (mode === "inventory") return `${n(c.speakerIds)} parlante(s)${c.group ? " + grupo " + c.group : ""} · ${{ server: c.audio || "sonido", library: c.libraryName || "biblioteca", tts: "voz" }[c.source || "server"]}`;
      return `${mode} · ${c.host || c.url || ""}`;
    }
    case "notify": {
      const who = (c.userIds || []).map((id) => (L.catalog?.users || []).find((u) => u.id === id)?.username || id);
      return `${c.title || "aviso"} → ${who.length ? who.join(", ") : "todos los operadores"}`;
    }
    case "door": {
      const verbs = { Open: "Abrir", Close: "Cerrar", RemainOpen: "Mantener abierta", RemainLocked: "Bloquear" };
      const names = (c.doorIds || []).map((id) => L.doors.find((d) => d.doorId === id)?.name || id);
      return `${verbs[c.command] || "Abrir"}: ${names.length ? names.join(", ") : "sin puertas"}`;
    }
    case "panel": {
      const verbs = { Arm: "Armar", Disarm: "Desarmar", ClearAlarm: "Borrar alarma" };
      const panel = L.panels.find((p) => p.id === c.panelId);
      const area = !c.areaNumber ? "todas las áreas" : (panel?.areas?.find((a) => a.number === c.areaNumber)?.name || `área ${c.areaNumber}`);
      return `${verbs[c.command] || "Armar"} ${panel?.name || "(elija panel)"} · ${area}`;
    }
    case "ptz-preset": {
      const cam = L.cameras.find((x) => x.channelId === c.channelId);
      return cam ? `${cam.channelName} → preset ${c.preset || 1}` : "elija la cámara";
    }
    default: return "";
  }
}

function wfeDraw() {
  const layer = $("#wfe-nodes");
  const { x, y, scale } = wfEd.view;
  layer.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
  $("#wfe-edge-layer").setAttribute("transform", `translate(${x} ${y}) scale(${scale})`);

  layer.innerHTML = wfEd.nodes.map((node) => {
    const selected = wfEd.selected?.kind === "node" && wfEd.selected.id === node.id;
    const result = wfEd.results[node.id];
    const outs = wfeOutPorts(node);
    return `
      <div class="wfe-node kind-${node.kind} ${node.kind === "action" ? "type-" + esc(node.type) : ""} ${selected ? "selected" : ""}
           ${result ? (result.success ? "ran-ok" : "ran-fail") : ""} ${node.kind === "action" && node.enabled === false ? "disabled" : ""}"
           data-id="${esc(node.id)}" style="left:${node.x}px;top:${node.y}px;width:${WFE_NODE_WIDTH}px">
        ${node.kind !== "trigger" ? `<div class="wfe-port in" data-port="in" title="Entrada"></div>` : ""}
        <div class="wfe-node-title">${esc(wfeTitle(node))}</div>
        <div class="wfe-node-sub">${esc(wfeSubtitle(node))}</div>
        ${result ? `<div class="wfe-node-result">${result.success ? "✔" : "✖"} ${esc(result.detail || "")}</div>` : ""}
        ${outs.length ? `<div class="wfe-outs">${outs.map(([p, label]) =>
          `<div class="wfe-out"><div class="wfe-port out port-${p}" data-port="${p}" title="${esc(label)}: arrastre hasta otro paso, o haga clic para agregar uno"></div><span class="wfe-port-label">${esc(label)}</span></div>`).join("")}</div>` : ""}
      </div>`;
  }).join("");

  wfeDrawEdges();
}

/// Centro de un puerto, en coordenadas del lienzo (sin escala).
function wfePortPoint(nodeId, port) {
  const el = $(`#wfe-nodes .wfe-node[data-id="${CSS.escape(nodeId)}"]`);
  const node = wfeNode(nodeId);
  if (!el || !node) return null;
  const p = el.querySelector(`.wfe-port[data-port="${port}"]`);
  if (!p) return { x: node.x + WFE_NODE_WIDTH / 2, y: node.y + (port === "in" ? 0 : el.offsetHeight) };
  return { x: node.x + p.offsetLeft + p.offsetWidth / 2, y: node.y + p.offsetTop + p.offsetHeight / 2 };
}

function wfeEdgePath(a, b) {
  const dy = Math.max(40, Math.abs(b.y - a.y) / 2);
  return `M ${a.x} ${a.y} C ${a.x} ${a.y + dy}, ${b.x} ${b.y - dy}, ${b.x} ${b.y}`;
}

function wfeDrawEdges() {
  const g = $("#wfe-edge-layer");
  g.innerHTML = wfEd.edges.map((e, i) => {
    const a = wfePortPoint(e.from, e.port);
    const b = wfePortPoint(e.to, "in");
    if (!a || !b) return "";
    const selected = wfEd.selected?.kind === "edge" && wfEd.selected.index === i;
    const d = wfeEdgePath(a, b);
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    const label = e.port === "yes" ? "Sí" : e.port === "no" ? "No" : e.port === "error" ? "si falla" : "";
    return `
      <g class="wfe-edge-group port-${e.port} ${selected ? "selected" : ""}" data-index="${i}">
        <path class="wfe-edge-hit" d="${d}"></path>
        <path class="wfe-edge" d="${d}" marker-end="url(#wfe-arrow)"></path>
        ${label ? `<text class="wfe-edge-label" x="${mid.x}" y="${mid.y}">${esc(label)}</text>` : ""}
      </g>`;
  }).join("");
}

// ---------------------------------------------------------------------------
// Interacción con el lienzo: mover, conectar, desplazar, acercar
// ---------------------------------------------------------------------------

function wfeCanvasPoint(clientX, clientY) {
  const rect = $("#wfe-canvas").getBoundingClientRect();
  const { x, y, scale } = wfEd.view;
  return { x: (clientX - rect.left - x) / scale, y: (clientY - rect.top - y) / scale };
}

function wfeZoomBy(factor, clientX, clientY) {
  const canvas = $("#wfe-canvas");
  const rect = canvas.getBoundingClientRect();
  const cx = clientX === undefined ? rect.width / 2 : clientX - rect.left;
  const cy = clientY === undefined ? rect.height / 2 : clientY - rect.top;
  const v = wfEd.view;
  const next = Math.min(2, Math.max(0.35, v.scale * factor));
  // El punto bajo el cursor se queda quieto.
  v.x = cx - (cx - v.x) * (next / v.scale);
  v.y = cy - (cy - v.y) * (next / v.scale);
  v.scale = next;
  wfeDraw();
}

function wfeFit() {
  const canvas = $("#wfe-canvas");
  if (!canvas || wfEd.nodes.length === 0) return;
  const rect = canvas.getBoundingClientRect();
  const minX = Math.min(...wfEd.nodes.map((n) => n.x));
  const minY = Math.min(...wfEd.nodes.map((n) => n.y));
  const maxX = Math.max(...wfEd.nodes.map((n) => n.x + WFE_NODE_WIDTH));
  const maxY = Math.max(...wfEd.nodes.map((n) => n.y + 110));
  const w = maxX - minX + 80, h = maxY - minY + 80;
  const scale = Math.min(1, Math.max(0.35, Math.min(rect.width / w, rect.height / h)));
  wfEd.view = {
    scale,
    x: (rect.width - (maxX - minX) * scale) / 2 - minX * scale,
    y: 30 - minY * scale,
  };
  wfeDraw();
}

function wfeBindCanvas(canvas) {
  let drag = null; // { kind: "node"|"pan"|"connect", ... }

  canvas.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    const port = e.target.closest(".wfe-port.out");
    const nodeEl = e.target.closest(".wfe-node");
    if (port && nodeEl) {
      drag = { kind: "connect", from: nodeEl.dataset.id, port: port.dataset.port, moved: false, startX: e.clientX, startY: e.clientY };
      const a = wfePortPoint(drag.from, drag.port);
      const temp = $("#wfe-temp-edge");
      temp.setAttribute("d", wfeEdgePath(a, a));
      temp.hidden = false;
      e.preventDefault();
      return;
    }
    if (nodeEl && !e.target.closest(".wfe-port")) {
      const node = wfeNode(nodeEl.dataset.id);
      const p = wfeCanvasPoint(e.clientX, e.clientY);
      drag = { kind: "node", node, offsetX: p.x - node.x, offsetY: p.y - node.y, moved: false, startX: e.clientX, startY: e.clientY };
      e.preventDefault();
      return;
    }
    const edge = e.target.closest(".wfe-edge-group");
    if (edge) { wfeSelect({ kind: "edge", index: Number(edge.dataset.index) }); e.preventDefault(); return; }
    drag = { kind: "pan", startX: e.clientX - wfEd.view.x, startY: e.clientY - wfEd.view.y, moved: false, originX: e.clientX, originY: e.clientY };
    canvas.classList.add("panning");
  });

  // Un clic con el mouse (o un touchpad) casi nunca es perfectamente quieto:
  // menos de este umbral sigue siendo un clic, no un arrastre.
  const DRAG_THRESHOLD = 4;
  const far = (ax, ay, bx, by) => Math.abs(ax - bx) + Math.abs(ay - by) > DRAG_THRESHOLD;

  window.addEventListener("mousemove", (e) => {
    if (!drag) return;
    if (drag.kind === "node") {
      if (!drag.moved && !far(e.clientX, e.clientY, drag.startX, drag.startY)) return;
      drag.moved = true;
      const p = wfeCanvasPoint(e.clientX, e.clientY);
      const nx = Math.round(p.x - drag.offsetX), ny = Math.round(p.y - drag.offsetY);
      drag.node.x = nx; drag.node.y = ny;
      const el = $(`#wfe-nodes .wfe-node[data-id="${CSS.escape(drag.node.id)}"]`);
      if (el) { el.style.left = nx + "px"; el.style.top = ny + "px"; }
      wfeDrawEdges();
    } else if (drag.kind === "pan") {
      if (!drag.moved && !far(e.clientX, e.clientY, drag.originX, drag.originY)) return;
      drag.moved = true;
      wfEd.view.x = e.clientX - drag.startX;
      wfEd.view.y = e.clientY - drag.startY;
      wfeDraw();
    } else if (drag.kind === "connect") {
      if (far(e.clientX, e.clientY, drag.startX, drag.startY)) drag.moved = true;
      const a = wfePortPoint(drag.from, drag.port);
      const b = wfeCanvasPoint(e.clientX, e.clientY);
      $("#wfe-temp-edge").setAttribute("d", wfeEdgePath(a, b));
      const target = document.elementFromPoint(e.clientX, e.clientY)?.closest(".wfe-node");
      $$("#wfe-nodes .wfe-node.drop-target").forEach((n) => n.classList.remove("drop-target"));
      if (target && target.dataset.id !== drag.from) target.classList.add("drop-target");
    }
  });

  window.addEventListener("mouseup", (e) => {
    if (!drag) return;
    const current = drag;
    drag = null;
    canvas.classList.remove("panning");
    if (current.kind === "node") {
      if (!current.moved) wfeSelect({ kind: "node", id: current.node.id });
      else wfeDraw();
    } else if (current.kind === "pan") {
      if (!current.moved) wfeSelect(null);
    } else if (current.kind === "connect") {
      $("#wfe-temp-edge").hidden = true;
      $$("#wfe-nodes .wfe-node.drop-target").forEach((n) => n.classList.remove("drop-target"));
      const target = document.elementFromPoint(e.clientX, e.clientY)?.closest(".wfe-node");
      if (current.moved && target) wfeConnect(current.from, current.port, target.dataset.id);
      else if (!current.moved) wfePortMenu(current.from, current.port, e.clientX, e.clientY);
    }
  });

  canvas.addEventListener("wheel", (e) => {
    e.preventDefault();
    wfeZoomBy(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX, e.clientY);
  }, { passive: false });

  canvas.addEventListener("keydown", (e) => {
    if (e.key === "Delete" || e.key === "Backspace") {
      if (e.target.closest("input, textarea, select")) return;
      wfeRemoveSelected();
      e.preventDefault();
    } else if (e.key === "Escape") {
      wfeSelect(null);
      wfeCloseMenu();
    }
  });
  canvas.addEventListener("dblclick", (e) => {
    // Doble clic en el vacío: acercar al punto.
    if (e.target === canvas || e.target.closest(".wfe-svg")) wfeZoomBy(1.3, e.clientX, e.clientY);
  });
}

/// Menú flotante al hacer clic en un puerto de salida: agrega y conecta un paso.
function wfePortMenu(fromId, port, clientX, clientY) {
  wfeCloseMenu();
  const { catalog } = wfEd.lists;
  const menu = document.createElement("div");
  menu.className = "wfe-menu";
  menu.id = "wfe-menu";
  menu.innerHTML = `
    <div class="wfe-menu-title">Agregar después (${esc({ yes: "Sí", no: "No", error: "si falla" }[port] || "siguiente")})</div>
    <button data-kind="condition">${WFE_ICONS.condition} Condición (sí / no)</button>
    <button data-kind="delay">${WFE_ICONS.delay} Espera</button>
    <button data-kind="end">${WFE_ICONS.end} Fin</button>
    <div class="wfe-menu-sep"></div>
    ${catalog.actions.map((a) => `<button data-kind="action" data-type="${esc(a.key)}">${WFE_ICONS[a.key] || "▶"} ${esc(a.label)}</button>`).join("")}`;
  document.body.appendChild(menu);
  const w = menu.offsetWidth, h = menu.offsetHeight;
  menu.style.left = Math.min(clientX, window.innerWidth - w - 8) + "px";
  menu.style.top = Math.min(clientY, window.innerHeight - h - 8) + "px";
  menu.querySelectorAll("button").forEach((b) => b.addEventListener("click", () => {
    wfeCloseMenu();
    wfeAddAfterPort(fromId, port, b.dataset.kind, b.dataset.type || null);
  }));
  setTimeout(() => document.addEventListener("mousedown", wfeMenuOutside), 0);
}

function wfeMenuOutside(e) {
  if (e.target.closest("#wfe-menu")) return;
  wfeCloseMenu();
}

function wfeCloseMenu() {
  $("#wfe-menu")?.remove();
  document.removeEventListener("mousedown", wfeMenuOutside);
}

// ---------------------------------------------------------------------------
// Orden automático: capas desde el disparador
// ---------------------------------------------------------------------------

function wfeAutoLayout(redraw) {
  const depth = new Map();
  const trigger = wfEd.nodes.find((n) => n.kind === "trigger");
  if (!trigger) return;
  depth.set(trigger.id, 0);
  const queue = [trigger.id];
  while (queue.length) {
    const id = queue.shift();
    const d = depth.get(id);
    wfEd.edges.filter((e) => e.from === id).forEach((e) => {
      if (!depth.has(e.to)) { depth.set(e.to, d + 1); queue.push(e.to); }
    });
  }
  // Los sueltos van al final, para que se vean.
  let extra = Math.max(0, ...depth.values()) + 1;
  wfEd.nodes.forEach((n) => { if (!depth.has(n.id)) depth.set(n.id, extra); });

  const layers = new Map();
  wfEd.nodes.forEach((n) => {
    const d = depth.get(n.id);
    if (!layers.has(d)) layers.set(d, []);
    layers.get(d).push(n);
  });
  // Dentro de la capa, según el orden de los padres (y del puerto: Sí antes que No).
  const order = new Map();
  [...layers.keys()].sort((a, b) => a - b).forEach((d) => {
    const layer = layers.get(d);
    layer.sort((a, b) => {
      const pa = wfEd.edges.find((e) => e.to === a.id), pb = wfEd.edges.find((e) => e.to === b.id);
      const ka = pa ? (order.get(pa.from) ?? 0) * 10 + (pa.port === "no" || pa.port === "error" ? 1 : 0) : 99;
      const kb = pb ? (order.get(pb.from) ?? 0) * 10 + (pb.port === "no" || pb.port === "error" ? 1 : 0) : 99;
      return ka - kb;
    });
    const width = layer.length * (WFE_NODE_WIDTH + 60) - 60;
    layer.forEach((n, i) => {
      order.set(n.id, i);
      n.x = Math.round(400 - width / 2 + i * (WFE_NODE_WIDTH + 60));
      n.y = 30 + d * 160;
    });
  });
  if (redraw) wfeDraw();
}

// ---------------------------------------------------------------------------
// Selección y panel de propiedades
// ---------------------------------------------------------------------------

function wfeSelect(selection) {
  wfEd.selected = selection;
  wfeDraw();
  wfeProps();
}

function wfeProps() {
  const box = $("#wfe-props");
  if (!box) return;
  const s = wfEd.selected;
  const L = wfEd.lists;

  if (!s) {
    box.innerHTML = `
      <div class="wfe-props-title">Cómo armar el diagrama</div>
      <ol class="wfe-steps">
        <li>Haga clic en el <b>punto de partida</b> para elegir qué lo dispara y con qué filtro.</li>
        <li>Haga clic en el <b>círculo de salida</b> de un paso para agregar el siguiente, o arrástrelo hasta otro paso para conectarlos.</li>
        <li>Una <b>condición</b> tiene dos salidas: <b>Sí</b> y <b>No</b>. Una acción tiene <b>Siguiente</b> y <b>Si falla</b>.</li>
        <li>Seleccione un paso o una flecha y pulse <b>Supr</b> para eliminarlo. Rueda del mouse = zoom; arrastre el fondo para moverse.</li>
      </ol>
      <div class="muted" style="font-size:12px">Las ramas que salen de un mismo punto se ejecutan una tras otra, de izquierda a derecha.</div>`;
    return;
  }

  if (s.kind === "edge") {
    const e = wfEd.edges[s.index];
    if (!e) { wfEd.selected = null; wfeProps(); return; }
    const from = wfeNode(e.from), to = wfeNode(e.to);
    box.innerHTML = `
      <div class="wfe-props-title">Conexión</div>
      <div class="muted" style="margin-bottom:12px">
        <b>${esc(wfeTitle(from))}</b> <span class="chip">${esc({ yes: "Sí", no: "No", error: "si falla", next: "siguiente" }[e.port])}</span>
        → <b>${esc(wfeTitle(to))}</b>
      </div>
      <button class="btn danger" id="wfe-edge-delete">Eliminar conexión</button>`;
    $("#wfe-edge-delete").addEventListener("click", wfeRemoveSelected);
    return;
  }

  const node = wfeNode(s.id);
  if (!node) { wfEd.selected = null; wfeProps(); return; }
  const catalog = L.catalog;

  const labelField = (placeholder) => `
    <div class="field"><label>Rótulo (opcional)</label>
      <input id="wfe-label" maxlength="64" value="${esc(node.label || "")}" placeholder="${esc(placeholder)}"></div>`;
  const bindLabel = () => $("#wfe-label")?.addEventListener("input", (e) => { node.label = e.target.value; wfeDrawKeepProps(); });

  switch (node.kind) {
    case "trigger": {
      const groups = new Map();
      catalog.triggers.forEach((t) => {
        const g = t.group || "Otros";
        if (!groups.has(g)) groups.set(g, []);
        groups.get(g).push(t);
      });
      const trigger = catalog.triggers.find((t) => t.key === wfEd.triggerType);
      box.innerHTML = `
        <div class="wfe-props-title">${WFE_ICONS.trigger} Punto de partida</div>
        <div class="field"><label>¿Cuándo se ejecuta?</label>
          <select id="wfe-trigger">
            ${[...groups.entries()].map(([g, items]) => `<optgroup label="${esc(g)}">${items.map((t) =>
              `<option value="${esc(t.key)}" ${t.key === wfEd.triggerType ? "selected" : ""}>${esc(t.label)}</option>`).join("")}</optgroup>`).join("")}
          </select>
          <div class="muted" style="font-size:12px;margin-top:4px">${esc(trigger?.description ?? "")}</div>
        </div>
        <div class="wfe-props-section">Filtro del disparador <span class="muted">(nada marcado = cualquiera)</span></div>
        <div id="wfe-conditions"></div>
        <div class="wfe-props-section">Marcas disponibles en los textos</div>
        <div class="wfe-placeholders">
          ${catalog.placeholders.concat(trigger?.placeholders || []).map((p) => `<code title="${esc(p.label)}">{${esc(p.key)}}</code>`).join(" ")}
        </div>`;
      $("#wfe-trigger").addEventListener("change", (e) => {
        wfEd.triggerType = e.target.value;
        // Cambiar el disparador cambia los campos del filtro: se empieza limpio.
        node.conditions = {};
        wfEd.nodes.filter((n) => n.kind === "condition").forEach((n) => { n.conditions = {}; });
        wfeDraw();
        wfeProps();
      });
      wfeConditionsForm($("#wfe-conditions"), node, true);
      break;
    }

    case "condition":
      box.innerHTML = `
        <div class="wfe-props-title">${WFE_ICONS.condition} Condición (sí / no)</div>
        ${labelField("¿Es de noche? / ¿Es la patente de un residente?")}
        <div class="muted" style="font-size:12px;margin-bottom:10px">
          Se evalúa sobre el mismo evento que disparó la automatización. Si se cumple TODO lo marcado sigue por <b>Sí</b>; si no, por <b>No</b>.
        </div>
        <div id="wfe-conditions"></div>`;
      bindLabel();
      wfeConditionsForm($("#wfe-conditions"), node, false);
      break;

    case "delay":
      box.innerHTML = `
        <div class="wfe-props-title">${WFE_ICONS.delay} Espera</div>
        ${labelField("Dar tiempo a que abra el portón")}
        <div class="field"><label>Segundos</label>
          <input id="wfe-delay" type="number" min="1" max="3600" value="${node.delaySeconds || 10}"></div>`;
      bindLabel();
      $("#wfe-delay").addEventListener("change", (e) => { node.delaySeconds = Number(e.target.value) || 1; wfeDrawKeepProps(); });
      break;

    case "end":
      box.innerHTML = `
        <div class="wfe-props-title">${WFE_ICONS.end} Fin</div>
        ${labelField("Fin")}
        <div class="muted" style="font-size:12px">Termina esta rama. Es opcional: una rama sin salida también termina.</div>`;
      bindLabel();
      break;

    case "action": {
      const info = catalog.actions.find((a) => a.key === node.type);
      const action = { config: node.config, secret: node.secret, hasSecret: node.hasSecret, type: node.type };
      const fields = (WF_FIELDS[node.type] || []).filter((f) => !f.when || f.when(node.config));
      box.innerHTML = `
        <div class="wfe-props-title">${WFE_ICONS[node.type] || "▶"} ${esc(info?.label ?? node.type)}</div>
        <div class="muted" style="font-size:12px;margin-bottom:10px">${esc(info?.description ?? "")}</div>
        ${labelField(info?.label ?? "")}
        <div id="wfe-fields">${fields.map((f) => wfFieldHtml(action, f, L)).join("")}</div>
        <div class="wfe-props-section">Ejecución</div>
        <div class="field"><label>Esperar antes de ejecutar (s)</label>
          <input id="wfe-adelay" type="number" min="0" max="600" value="${node.delaySeconds || 0}"></div>
        <label class="checkbox-row"><input type="checkbox" id="wfe-aenabled" ${node.enabled !== false ? "checked" : ""}> Acción activa (desactivada = se salta y sigue)</label>
        <div class="muted" style="font-size:12px">Si la acción falla, el flujo sigue por la salida <b>Si falla</b> (si está conectada); si termina bien, por <b>Siguiente</b>.</div>`;
      bindLabel();
      $("#wfe-fields").querySelectorAll("[data-key]").forEach((input) => {
        input.addEventListener("change", () => {
          wfReadField(action, input);
          node.config = action.config; node.secret = action.secret;
          const field = (WF_FIELDS[node.type] || []).find((f) => f.k === input.dataset.key);
          if (field?.rerender) wfeProps(); else wfeDrawKeepProps();
        });
      });
      $("#wfe-fields").querySelectorAll(".btn-field-play").forEach((play) => play.addEventListener("click", () => {
        const select = $("#wfe-fields").querySelector(`[data-key="${play.dataset.for}"]`);
        const name = select?.value || "";
        if (!name || name === "sistema") { toast("Ese aviso no usa un archivo de sonido."); return; }
        wfTogglePreview(name, play);
      }));
      $("#wfe-adelay").addEventListener("change", (e) => { node.delaySeconds = Number(e.target.value) || 0; });
      $("#wfe-aenabled").addEventListener("change", (e) => { node.enabled = e.target.checked; wfeDrawKeepProps(); });
      wfBindCameraLists($("#wfe-fields"));
      break;
    }
  }
}

/// Redibuja el lienzo (títulos, subtítulos) sin tocar el panel de propiedades.
function wfeDrawKeepProps() { wfeDraw(); }

// ---------------------------------------------------------------------------
// Formulario de condiciones (filtro del disparador y nodos de condición)
// ---------------------------------------------------------------------------

function wfeCheckList(items, selected, prefix, emptyText) {
  if (items.length === 0) return `<div class="muted" style="font-size:12px">${esc(emptyText || "Sin elementos.")}</div>`;
  const marked = (selected || []).map(String);
  return `<div class="wf-check-grid">${items.map(([value, label, title, extra]) => `
    <label class="checkbox-row" ${title ? `title="${esc(title)}"` : ""} ${extra || ""}><input type="checkbox" data-${prefix}="${esc(String(value))}"
      ${marked.includes(String(value)) ? "checked" : ""}> ${esc(label)}</label>`).join("")}</div>`;
}

/// Pinta el formulario del filtro para el disparador actual y guarda cada cambio en node.conditions.
function wfeConditionsForm(container, node, isTrigger) {
  const L = wfEd.lists;
  const t = wfEd.triggerType;
  // OJO: read() reemplaza node.conditions por un objeto nuevo; cada redibujo
  // debe leer el actual, no una referencia capturada al armar el formulario.
  node.conditions ||= {};
  const marked = (prefix) => $$(`[data-${prefix}]`, container).filter((i) => i.checked).map((i) => i.dataset[prefix]);
  const strings = (id) => ($(`#${id}`, container)?.value || "").split(/[,\n;]/).map((v) => v.trim()).filter((v) => v.length > 0);

  const draw = () => {
    const c = node.conditions;
    const parts = [];
    const field = (label, inner, help) => parts.push(`<div class="field"><label>${label}</label>${inner}${help ? `<div class="muted" style="font-size:11.5px;margin-top:3px">${help}</div>` : ""}</div>`);

    // Equipos que disparan: chips de lo elegido + árbol para elegir. Lo
    // guardado por número (automatizaciones viejas, sin clave por panel) se
    // pasa a claves "panel:número" sobre los paneles marcados o todos.
    const sources = (help) => { const spec = WFE_SOURCE_FIELDS[t]; if (spec) field(spec.title, wfeSourcesHtml(t, c), help); };
    if (t === "alarm-event") {
      const legacy = (keys, numbers, pick) => keys?.length || !numbers?.length
        ? (keys || [])
        : (c.panelIds?.length ? L.panels.filter((p) => c.panelIds.includes(p.id)) : L.panels)
          .flatMap((p) => numbers.filter((n) => (pick(p) || []).some((x) => x.number === n)).map((n) => `${p.id}:${n}`));
      c.zoneKeys = legacy(c.zoneKeys, c.zoneNumbers, (p) => p.zones);
      c.areaKeys = legacy(c.areaKeys, c.areaNumbers, (p) => p.areas);
    }

    switch (t) {
      case "alarm-event":
      case "panel-status":
        sources(t === "alarm-event"
          ? "Un panel completo, o solo algunas de sus zonas o áreas. Marcar una zona ya acota el panel."
          : null);
        if (t === "alarm-event") {
          field("Tipo de evento", wfeCheckList(WF_KINDS, c.kinds, "kind"));
          field("Severidad", wfeCheckList(WF_SEVERITIES, c.severities, "severity"),
            "«Sensor interrumpido» y «Sensor restablecido» no tienen severidad propia (se detectan por sondeo con el área desarmada): este filtro no los afecta.");
          parts.push(`<div class="form-grid">
            <div class="field"><label>Códigos del evento (coma)</label><input id="wfe-codes" value="${esc((c.codes || []).join(", "))}" placeholder="1130, 1120"></div>
            <div class="field"><label>La descripción contiene</label><input id="wfe-text" value="${esc(c.textContains || "")}" placeholder="intrusión"></div>
          </div>`);
          field("Cómo se supo", wfeCheckList(WF_SOURCES, c.sources, "source"));
          if (isTrigger)
            field("Solo si la condición se sostiene (segundos)",
              `<input id="wfe-sustained" type="number" min="0" max="600" value="${c.sustainedSeconds || 0}">`,
              "0 = de inmediato. Con un valor mayor el servidor vigila la zona ese tiempo y ejecuta solo si el sensor sigue interrumpido (tolera los pulsos cortos del detector).");
        } else {
          field("Estado de conexión que dispara", wfeCheckList(WF_STATUSES, c.statuses, "status"));
        }
        break;

      case "device-status":
        field("Clase de equipo", wfeCheckList(WFE_DEVICE_KINDS, c.deviceKinds, "dkind"));
        field("Estado que dispara", wfeCheckList(WFE_DEVICE_STATUSES, c.deviceStatuses, "dstatus"));
        sources("Con equipos marcados, solo esos disparan; sin ninguno marcado, cualquiera de la clase elegida.");
        break;

      case "video-event":
        field("Tipo de evento", wfeCheckList(WFE_VIDEO_KINDS, c.videoEventKinds, "vkind"));
        sources("Un grabador completo o solo algunas de sus cámaras. El servidor se suscribe solo a los eventos de los equipos que pida alguna automatización; las reglas (movimiento, cruce de línea…) se configuran en la web del propio equipo. Hoy reciben eventos los equipos Hikvision por SDK.");
        break;

      case "plate-recognized":
        sources();
        field("Patentes", `<select id="wfe-platematch" style="margin-bottom:6px">
            <option value="any" ${(c.plateMatch || (c.plates?.length ? "listed" : "any")) === "any" ? "selected" : ""}>Cualquier patente</option>
            <option value="listed" ${(c.plateMatch || (c.plates?.length ? "listed" : "any")) === "listed" ? "selected" : ""}>Solo las de la lista (lista blanca)</option>
            <option value="unlisted" ${c.plateMatch === "unlisted" ? "selected" : ""}>Todas menos las de la lista (lista negra)</option>
          </select>
          <textarea id="wfe-plates" rows="4" placeholder="ABCD12&#10;XY*&#10;KL?T88">${esc((c.plates || []).join("\n"))}</textarea>`,
          "Una por línea (o separadas por coma). * = cualquier cosa, ? = un carácter. Sin distinguir mayúsculas ni guiones.");
        field("Confianza mínima de la lectura (0–100)", `<input id="wfe-confidence" type="number" min="0" max="100" value="${c.minConfidence ?? ""}" placeholder="sin mínimo">`);
        break;

      case "access-event":
        field("Resultado", wfeCheckList(WFE_ACCESS_KINDS, c.accessKinds, "akind"));
        field("Credencial usada", wfeCheckList(WFE_CREDENTIALS, c.credentials, "cred"));
        sources("Un terminal completo o solo algunas de sus puertas (se filtra por el número de puerta en el equipo).");
        parts.push(`<div class="form-grid">
          <div class="field"><label>Identificadores de persona (coma)</label><input id="wfe-employees" value="${esc((c.employeeNos || []).join(", "))}" placeholder="1001, 1002"></div>
          <div class="field"><label>La descripción contiene</label><input id="wfe-text" value="${esc(c.textContains || "")}" placeholder="forzada"></div>
        </div>`);
        break;

      case "schedule":
        if (isTrigger)
          field("Horas en que se ejecuta", `<input id="wfe-times" value="${esc((c.scheduleTimes || []).join(", "))}" placeholder="07:30, 22:00">`,
            "Formato hh:mm, separadas por coma. Se ejecuta a esa hora los días marcados abajo (hora local del servidor).");
        break;

      case "webhook":
        if (isTrigger) {
          const url = `${location.origin}/api/workflows/hook/${c.hookKey || "(se genera al guardar)"}`;
          field("URL que debe llamar el otro sistema (POST)", `
            <div style="display:flex;gap:6px;align-items:center">
              <input id="wfe-hookurl" readonly value="${esc(url)}" style="flex:1;font-size:12px">
              <button class="btn ghost" type="button" id="wfe-hookcopy" title="Copiar">Copiar</button>
              <button class="btn ghost" type="button" id="wfe-hooknew" title="Genera una clave nueva; la anterior deja de servir al guardar">Nueva clave</button>
            </div>`,
            "La clave de la URL es la credencial: no la comparta más de lo necesario. El cuerpo JSON de la llamada (si viene) queda disponible como marcas {campo}; por ejemplo {mensaje}.");
        }
        break;
    }

    // Ventana horaria: sirve en todos los disparadores. Fuera del disparador
    // de horario admite varias franjas (basta que una se cumpla): la principal
    // va en daysOfWeek/fromTime/toTime y las demás en timeBands.
    if (t === "schedule") {
      field("Días de la semana", wfeCheckList(WF_DAY_ITEMS(), c.daysOfWeek, "day"));
    } else {
      // Cada franja va en su propia tarjeta; la principal es la 1 y no se quita.
      const bandCard = (title, action, checklist, fromId, fromValue, toId, toValue, attrs) => `<div class="wfe-band" ${attrs || ""}>
        <div class="wfe-band-head"><span>${title}</span>${action || ""}</div>
        <div class="field"><label>Solo estos días de la semana</label>${checklist}</div>
        <div class="form-grid">
          <div class="field"><label>Desde (hora local)</label>${wfTimeInput(fromId, fromValue)}</div>
          <div class="field"><label>Hasta (si es menor, cruza la medianoche)</label>${wfTimeInput(toId, toValue)}</div>
        </div>
      </div>`;
      parts.push(bandCard("Franja horaria 1", "", wfeCheckList(WF_DAY_ITEMS(), c.daysOfWeek, "day"), "wfe-from", c.fromTime, "wfe-to", c.toTime));
      (c.timeBands || []).forEach((b, i) => parts.push(bandCard(`Franja horaria ${i + 2}`,
        `<button class="btn ghost" type="button" data-band-remove="${i}" title="Quitar esta franja">Quitar</button>`,
        wfeCheckList(WF_DAY_ITEMS(), b.daysOfWeek, `band${i}day`), `wfe-band-from-${i}`, b.fromTime, `wfe-band-to-${i}`, b.toTime, `data-band="${i}"`)));
      parts.push(`<div class="field"><button class="btn ghost" type="button" id="wfe-band-add">+ Agregar franja horaria</button>
        <div class="muted" style="font-size:11.5px;margin-top:3px">La automatización rige si se cumple cualquiera de las franjas. Horas en formato 24 h; sin horas (o de 00:00 a 24:00) la franja vale todo el día. Ejemplo: lunes a viernes de 18:00 a 06:00 en la primera y sábado y domingo sin horas (todo el día) en la segunda. Una ventana que cruza la medianoche pertenece al día en que empieza: para que la noche del domingo siga hasta el lunes a las 06:00, marque también el domingo en la primera franja.</div></div>`);
    }

    container.innerHTML = parts.join("");

    $("#wfe-band-add", container)?.addEventListener("click", () => {
      read();
      node.conditions.timeBands = [...(node.conditions.timeBands || []), { daysOfWeek: [], fromTime: null, toTime: null }];
      draw(); wfeDraw();
    });
    $$("[data-band-remove]", container).forEach((x) => x.addEventListener("click", () => {
      read();
      node.conditions.timeBands = (node.conditions.timeBands || []).filter((_, i) => i !== Number(x.dataset.bandRemove));
      draw(); wfeDraw();
    }));

    $$("input:not(.wf-filter), select, textarea", container).forEach((el) => el.addEventListener("change", () => { read(); wfeDraw(); }));
    // Selector de equipos: el árbol escribe en node.conditions; después se redibuja el filtro y el diagrama.
    $("#wfe-pick-sources", container)?.addEventListener("click", () => {
      read();
      wfeOpenSourcePicker(t, node.conditions, () => { draw(); wfeDraw(); });
    });
    $$("[data-unpick]", container).forEach((x) => x.addEventListener("click", () => {
      read();
      const [field, value] = wfeSourceKeyParts(x.dataset.unpick);
      node.conditions[field] = (node.conditions[field] || []).filter((v) => String(v) !== value);
      draw(); wfeDraw();
    }));
    $("#wfe-hookcopy", container)?.addEventListener("click", () => {
      navigator.clipboard?.writeText($("#wfe-hookurl", container).value).then(() => toast("URL copiada."), () => toast("No se pudo copiar.", true));
    });
    $("#wfe-hooknew", container)?.addEventListener("click", async () => {
      try {
        const { key } = await Api.post("/api/workflows/hook-key");
        node.conditions.hookKey = key;
        draw();
        toast("Clave nueva generada: regirá al guardar.");
      } catch (err) { toast(err.error, true); }
    });
  };

  const read = () => {
    const number = (id) => { const v = $(`#${id}`, container)?.value; return v === "" || v === undefined ? null : Number(v); };
    // Horas en 24 h: se normalizan y se reescriben en el campo ("6" → "06:00").
    const time = (id) => {
      const el = $(`#${id}`, container);
      if (!el) return null;
      el.value = wfNormalizeTime(el.value);
      return el.value || null;
    };
    const next = {
      daysOfWeek: marked("day").map(Number),
      fromTime: time("wfe-from"),
      toTime: time("wfe-to"),
      timeBands: $$("[data-band]", container).map((el) => {
        const i = el.dataset.band;
        return {
          daysOfWeek: marked(`band${i}day`).map(Number),
          fromTime: time(`wfe-band-from-${i}`),
          toTime: time(`wfe-band-to-${i}`),
        };
      }),
    };
    // Los equipos no están en el formulario: los mantiene el selector en node.conditions.
    (WFE_SOURCE_FIELDS[t]?.fields || []).forEach((f) => { next[f] = node.conditions[f] || []; });
    switch (t) {
      case "alarm-event": {
        const numbersOf = (keys) => [...new Set(keys.map((k) => Number(k.split(":")[1])))];
        Object.assign(next, {
          kinds: marked("kind"), severities: marked("severity"),
          zoneNumbers: numbersOf(next.zoneKeys), areaNumbers: numbersOf(next.areaKeys),
          codes: strings("wfe-codes"), textContains: $("#wfe-text", container)?.value.trim() || null,
          sources: marked("source"), sustainedSeconds: isTrigger ? (number("wfe-sustained") || 0) : 0,
        });
        break;
      }
      case "panel-status":
        Object.assign(next, { statuses: marked("status") });
        break;
      case "device-status":
        Object.assign(next, { deviceKinds: marked("dkind"), deviceStatuses: marked("dstatus") });
        break;
      case "video-event":
        Object.assign(next, { videoEventKinds: marked("vkind") });
        break;
      case "plate-recognized":
        Object.assign(next, {
          plates: strings("wfe-plates").map((p) => p.toUpperCase()),
          plateMatch: $("#wfe-platematch", container)?.value || "any",
          minConfidence: number("wfe-confidence"),
        });
        break;
      case "access-event":
        Object.assign(next, {
          accessKinds: marked("akind"), credentials: marked("cred"), employeeNos: strings("wfe-employees"),
          textContains: $("#wfe-text", container)?.value.trim() || null,
        });
        break;
      case "schedule":
        if (isTrigger) next.scheduleTimes = strings("wfe-times");
        break;
      case "webhook":
        if (isTrigger) next.hookKey = node.conditions.hookKey || null;
        break;
    }
    node.conditions = next;
  };

  draw();
}

// ---------------------------------------------------------------------------
// Selector de equipos del disparador (árbol con búsqueda)
// ---------------------------------------------------------------------------
// En vez de una lista plana de casillas por tipo de equipo, el filtro muestra
// chips con lo elegido y un botón que abre un árbol: equipo → sus zonas,
// áreas, cámaras o puertas. Se marca el equipo completo o solo algunos hijos.

/// Campos del filtro que son "equipos" en cada disparador.
const WFE_SOURCE_FIELDS = {
  "alarm-event": { title: "Paneles, zonas y áreas", fields: ["panelIds", "zoneKeys", "areaKeys"], any: "Cualquier panel, zona o área", button: "Elegir paneles y zonas…" },
  "panel-status": { title: "Paneles", fields: ["panelIds"], any: "Cualquier panel", button: "Elegir paneles…" },
  "device-status": { title: "Equipos", fields: ["deviceIds", "accessDeviceIds", "speakerIds"], any: "Cualquier equipo de la clase marcada", button: "Elegir equipos…" },
  "video-event": { title: "Cámaras", fields: ["deviceIds", "channelIds"], any: "Cualquier cámara", button: "Elegir cámaras…" },
  "plate-recognized": { title: "Cámaras ANPR", fields: ["deviceIds"], any: "Cualquier cámara con reconocimiento de patentes", button: "Elegir cámaras…" },
  "access-event": { title: "Terminales y puertas", fields: ["accessDeviceIds", "doorNumbers"], any: "Cualquier terminal o puerta", button: "Elegir terminales y puertas…" },
};
/// Nombre de cada campo para las chips y el resumen: [singular, plural].
const WFE_SOURCE_WORDS = {
  panelIds: ["panel", "paneles"], zoneKeys: ["zona", "zonas"], areaKeys: ["área", "áreas"],
  deviceIds: ["equipo", "equipos"], channelIds: ["cámara", "cámaras"], accessDeviceIds: ["terminal", "terminales"],
  speakerIds: ["parlante", "parlantes"], doorNumbers: ["puerta", "puertas"],
};

const wfeSourceKey = (field, value) => `${field}|${value}`;
const wfeSourceKeyParts = (key) => { const i = key.indexOf("|"); return [key.slice(0, i), key.slice(i + 1)]; };

/// Árbol de equipos del disparador: { field, value, label, hint, children }.
/// Los nodos sin `field` solo agrupan (no se marcan).
function wfeSourceTree(triggerType) {
  const L = wfEd.lists;
  const leaf = (field, value, label, hint) => ({ field, value, label, hint: hint || "", children: [] });
  const group = (label, children) => ({ label, children });
  switch (triggerType) {
    case "alarm-event":
      return L.panels.map((p) => ({
        field: "panelIds", value: p.id, label: p.name, hint: "todo el panel",
        children: [
          group("Zonas / sensores", (p.zones || []).map((z) => leaf("zoneKeys", `${p.id}:${z.number}`, `${z.number} · ${z.name}`))),
          group("Áreas", (p.areas || []).map((a) => leaf("areaKeys", `${p.id}:${a.number}`, `${a.number} · ${a.name}`))),
        ].filter((g) => g.children.length > 0),
      }));
    case "panel-status":
      return L.panels.map((p) => leaf("panelIds", p.id, p.name));
    case "device-status":
      return [
        group("Cámaras y grabadores", L.devices.map((d) => leaf("deviceIds", d.id, d.name))),
        group("Terminales de acceso", L.accessDevices.map((d) => leaf("accessDeviceIds", d.id, d.name))),
        group("Parlantes IP", L.speakers.map((s) => leaf("speakerIds", s.id, s.name))),
      ].filter((g) => g.children.length > 0);
    case "video-event":
      return L.devices.map((d) => ({
        field: "deviceIds", value: d.id, label: d.name, hint: d.supportsEvents ? "todas sus cámaras" : "el driver no recibe eventos",
        children: L.cameras.filter((c) => c.deviceId === d.id).map((c) => leaf("channelIds", c.channelId, c.channelName)),
      }));
    case "plate-recognized":
      return L.devices.filter((d) => d.supportsAnpr).map((d) =>
        leaf("deviceIds", d.id, d.name, d.anprEnabled ? "" : "no está marcada como fuente de patentes"));
    case "access-event":
      return L.accessDevices.map((d) => ({
        field: "accessDeviceIds", value: d.id, label: d.name, hint: "todas sus puertas",
        children: L.doors.filter((x) => x.deviceId === d.id).map((x) => leaf("doorNumbers", x.number, `${x.name} (n.º ${x.number})`)),
      }));
    default:
      return [];
  }
}

/// Etiqueta de un elemento elegido con su equipo delante ("Panel Caseta · 0 · Recepción").
function wfeSourceLabel(tree, field, value) {
  const walk = (nodes, path) => {
    for (const n of nodes) {
      const here = n.field ? path.concat(n.label) : path;
      if (n.field === field && String(n.value) === String(value)) return here.join(" · ");
      const found = walk(n.children, here);
      if (found) return found;
    }
    return null;
  };
  return walk(tree, []);
}

/// Chips de los equipos elegidos + botón del árbol (va dentro del formulario del filtro).
function wfeSourcesHtml(triggerType, conditions) {
  const spec = WFE_SOURCE_FIELDS[triggerType];
  if (!spec) return "";
  const tree = wfeSourceTree(triggerType);
  const chips = [];
  spec.fields.forEach((f) => (conditions[f] || []).forEach((v) => chips.push({
    field: f, value: v, label: wfeSourceLabel(tree, f, v) ?? `${v} (ya no existe)`,
  })));
  return `<div class="wfe-sources">
    ${chips.length === 0
      ? `<div class="wfe-sources-any">${esc(spec.any)}</div>`
      : `<div class="wfe-sources-chips">${chips.map((ch) => `<span class="chip" title="${esc(ch.label)}">
          <span class="chip-kind">${esc(WFE_SOURCE_WORDS[ch.field][0])}</span>${esc(ch.label)}
          <button type="button" data-unpick="${esc(wfeSourceKey(ch.field, ch.value))}" title="Quitar" aria-label="Quitar">×</button></span>`).join("")}</div>`}
    <button type="button" class="btn ghost" id="wfe-pick-sources">${esc(spec.button)}</button>
  </div>`;
}

/// Resumen "1 panel · 3 zonas" de un conjunto de claves campo|valor.
function wfeSourceSummary(set, fields) {
  const counts = {};
  set.forEach((key) => { const [f] = wfeSourceKeyParts(key); counts[f] = (counts[f] || 0) + 1; });
  const parts = fields.filter((f) => counts[f]).map((f) => `${counts[f]} ${WFE_SOURCE_WORDS[f][counts[f] === 1 ? 0 : 1]}`);
  return parts.length ? `Marcado: ${parts.join(" · ")}.` : "Nada marcado: dispara cualquiera.";
}

/// Abre el árbol de equipos; al aceptar escribe los campos en `conditions` y llama a `onDone`.
function wfeOpenSourcePicker(triggerType, conditions, onDone) {
  const spec = WFE_SOURCE_FIELDS[triggerType];
  const tree = wfeSourceTree(triggerType);
  const set = new Set();
  spec.fields.forEach((f) => (conditions[f] || []).forEach((v) => set.add(wfeSourceKey(f, v))));

  // Identificador por nodo para desplegar/plegar; los agrupadores parten
  // abiertos, los equipos solo si tienen algo marcado adentro (o son pocos).
  let seq = 0;
  const open = new Set();
  const countLeaves = (nodes) => nodes.reduce((n, x) => n + (x.field ? 1 : 0) + countLeaves(x.children), 0);
  const few = countLeaves(tree) <= 40;
  const hasPicked = (n) => (n.field && set.has(wfeSourceKey(n.field, n.value))) || n.children.some(hasPicked);
  const prepare = (nodes) => nodes.forEach((n) => {
    n._id = `n${seq++}`;
    if (n.children.length && (!n.field || few || hasPicked(n))) open.add(n._id);
    prepare(n.children);
  });
  prepare(tree);
  const allIds = [];
  const collect = (nodes) => nodes.forEach((n) => { if (n.children.length) allIds.push(n._id); collect(n.children); });
  collect(tree);

  openModal(`
    <h3>${esc(spec.title)}</h3>
    <div class="muted" style="font-size:12.5px;margin-bottom:10px">Marque un equipo completo o solo algunos de sus elementos. Sin nada marcado, dispara cualquiera.</div>
    <div class="wfe-tree-search">
      <input id="wfe-tree-q" type="text" placeholder="Buscar por nombre…" autocomplete="off">
      <button type="button" class="btn ghost" id="wfe-tree-expand">Desplegar todo</button>
      <button type="button" class="btn ghost" id="wfe-tree-clear">Desmarcar todo</button>
    </div>
    <div class="wfe-tree" id="wfe-tree"></div>
    <div class="wfe-tree-summary" id="wfe-tree-summary"></div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="wfe-tree-cancel">Cancelar</button>
      <button class="btn" type="button" id="wfe-tree-ok">Aceptar</button>
    </div>`, true);

  const box = $("#wfe-tree");
  const summary = () => { $("#wfe-tree-summary").textContent = wfeSourceSummary(set, spec.fields); };
  const paint = () => {
    const q = $("#wfe-tree-q").value.trim().toLowerCase();
    const matches = (n) => !q || n.label.toLowerCase().includes(q);
    const visible = (n) => matches(n) || n.children.some(visible);
    const rows = [];
    const row = (n, level) => {
      if (!visible(n)) return;
      const expandable = n.children.length > 0;
      const isOpen = q ? true : open.has(n._id);
      const caret = `<span class="caret ${expandable ? (isOpen ? "open" : "") : "leaf"}" data-toggle="${n._id}">▶</span>`;
      if (!n.field) {
        rows.push(`<div class="wfe-tree-row group level-${level}">${caret}<span class="tree-title">${esc(n.label)}</span></div>`);
      } else {
        const key = wfeSourceKey(n.field, n.value);
        rows.push(`<div class="wfe-tree-row level-${level} ${set.has(key) ? "picked" : ""}">${caret}
          <label><input type="checkbox" data-pick="${esc(key)}" ${set.has(key) ? "checked" : ""}>
          <span class="tree-name">${esc(n.label)}</span>${n.hint ? `<span class="tree-hint">· ${esc(n.hint)}</span>` : ""}</label></div>`);
      }
      if (expandable && isOpen) n.children.forEach((ch) => row(ch, Math.min(level + 1, 3)));
    };
    tree.forEach((n) => row(n, 0));
    box.innerHTML = rows.length ? rows.join("") : `<div class="wfe-tree-empty">${q ? `Nada coincide con «${esc(q)}».` : "No hay equipos de este tipo configurados."}</div>`;
    summary();
  };

  box.addEventListener("click", (e) => {
    const caret = e.target.closest("[data-toggle]");
    if (!caret || caret.classList.contains("leaf")) return;
    const id = caret.dataset.toggle;
    if (open.has(id)) open.delete(id); else open.add(id);
    paint();
  });
  box.addEventListener("change", (e) => {
    const input = e.target.closest("[data-pick]");
    if (!input) return;
    if (input.checked) set.add(input.dataset.pick); else set.delete(input.dataset.pick);
    input.closest(".wfe-tree-row").classList.toggle("picked", input.checked);
    summary();
  });
  $("#wfe-tree-q").addEventListener("input", paint);
  $("#wfe-tree-expand").addEventListener("click", () => { allIds.forEach((id) => open.add(id)); paint(); });
  $("#wfe-tree-clear").addEventListener("click", () => { set.clear(); paint(); });
  $("#wfe-tree-cancel").addEventListener("click", closeModal);
  $("#wfe-tree-ok").addEventListener("click", () => {
    spec.fields.forEach((f) => { conditions[f] = []; });
    set.forEach((key) => {
      const [field, value] = wfeSourceKeyParts(key);
      if (spec.fields.includes(field)) conditions[field].push(field.endsWith("Keys") ? value : Number(value));
    });
    closeModal();
    onDone();
  });
  paint();
  $("#wfe-tree-q").focus();
}

/// Resumen legible de un conjunto de condiciones (tarjetas y listado).
function wfeConditionsSummary(c, triggerType) {
  c = c || {};
  const L = wfEd?.lists;
  const parts = [];
  const names = (list, table) => (list || []).map((v) => (table.find((t) => String(t[0]) === String(v)) || [v, v])[1]).join(", ");
  const count = (list, singular, plural) => (list?.length ? `${list.length} ${list.length === 1 ? singular : plural}` : null);
  const push = (v) => { if (v) parts.push(v); };
  push(count(c.panelIds, "panel", "paneles"));
  if (c.kinds?.length) push(names(c.kinds, WF_KINDS));
  if (c.severities?.length) push(names(c.severities, WF_SEVERITIES));
  if (c.statuses?.length) push(names(c.statuses, WF_STATUSES));
  // Zonas y áreas con su nombre (y el panel, si hay varios) cuando el editor
  // tiene las listas a mano; si no, los números.
  const nameOf = (keys, pick, word) => {
    if (!keys?.length) return null;
    const panels = L?.panels || [];
    const names = keys.map((k) => {
      const [pid, n] = k.split(":");
      const panel = panels.find((p) => String(p.id) === pid);
      const item = panel && (pick(panel) || []).find((z) => String(z.number) === n);
      return item ? (panels.length > 1 ? `${panel.name} · ${item.name}` : item.name) : `${word} ${n}`;
    });
    return names.length > 2 ? `${names.slice(0, 2).join(", ")} y ${names.length - 2} más` : names.join(", ");
  };
  if (c.areaKeys?.length) push(nameOf(c.areaKeys, (p) => p.areas, "área"));
  else if (c.areaNumbers?.length) push(`áreas ${c.areaNumbers.join(", ")}`);
  if (c.zoneKeys?.length) push(nameOf(c.zoneKeys, (p) => p.zones, "zona"));
  else if (c.zoneNumbers?.length) push(`zonas ${c.zoneNumbers.join(", ")}`);
  if (c.codes?.length) push(`códigos ${c.codes.join(", ")}`);
  if (c.sources?.length) push(names(c.sources, WF_SOURCES));
  if (c.textContains) push(`contiene «${c.textContains}»`);
  if (c.sustainedSeconds > 0) push(`sostenido ≥ ${c.sustainedSeconds} s`);
  if (c.deviceKinds?.length) push(names(c.deviceKinds, WFE_DEVICE_KINDS));
  if (c.deviceStatuses?.length) push(names(c.deviceStatuses, WFE_DEVICE_STATUSES));
  push(count(c.deviceIds, "equipo", "equipos"));
  push(count(c.accessDeviceIds, "terminal", "terminales"));
  push(count(c.speakerIds, "parlante", "parlantes"));
  if (c.videoEventKinds?.length) push(names(c.videoEventKinds, WFE_VIDEO_KINDS));
  push(count(c.channelIds, "cámara", "cámaras"));
  if (c.plates?.length) push(`${c.plateMatch === "unlisted" ? "excepto" : "patentes"} ${c.plates.slice(0, 3).join(", ")}${c.plates.length > 3 ? "…" : ""}`);
  if (c.minConfidence > 0) push(`confianza ≥ ${c.minConfidence}%`);
  if (c.accessKinds?.length) push(names(c.accessKinds, WFE_ACCESS_KINDS));
  if (c.credentials?.length) push(names(c.credentials, WFE_CREDENTIALS));
  if (c.doorNumbers?.length) push(`puertas ${c.doorNumbers.join(", ")}`);
  if (c.employeeNos?.length) push(`personas ${c.employeeNos.join(", ")}`);
  if (c.scheduleTimes?.length) push(`a las ${c.scheduleTimes.join(", ")}`);
  const band = (days, from, to) => {
    const bits = [];
    if (days?.length && days.length < 7) bits.push(WF_DAY_ORDER.filter((d) => days.includes(d)).map((d) => WF_DAYS[d].slice(0, 3)).join("/"));
    if (from || to) bits.push(`${from || "00:00"}–${to || "24:00"}`);
    return bits.join(" ");
  };
  const bands = [band(c.daysOfWeek, c.fromTime, c.toTime), ...(c.timeBands || []).map((b) => band(b.daysOfWeek, b.fromTime, b.toTime))].filter((b) => b);
  if (bands.length) push(bands.join(" ó "));
  void L; void triggerType;
  return parts.join(" · ");
}

// ---------------------------------------------------------------------------
// Guardar y probar
// ---------------------------------------------------------------------------

function wfePayload() {
  const trigger = wfEd.nodes.find((n) => n.kind === "trigger");
  return {
    name: wfEd.name.trim(),
    description: wfEd.description.trim() || null,
    enabled: wfEd.enabled,
    triggerType: wfEd.triggerType,
    cooldownSeconds: wfEd.cooldownSeconds,
    conditions: trigger?.conditions || {},
    graph: {
      nodes: wfEd.nodes.map((n) => ({
        id: n.id, kind: n.kind, x: n.x, y: n.y, label: n.label || null,
        type: n.kind === "action" ? n.type : null,
        config: n.kind === "action" ? n.config : null,
        enabled: n.enabled !== false,
        delaySeconds: n.delaySeconds || 0,
        conditions: n.kind === "condition" ? n.conditions : null,
        secret: n.kind === "action" ? (n.secret || null) : null,
        actionId: n.actionId || 0,
      })),
      edges: wfEd.edges.map((e) => ({ from: e.from, to: e.to, port: e.port })),
    },
  };
}

/// Revisión previa en el navegador: lo que el servidor rechazaría igual, pero con el paso señalado.
function wfeLocalCheck() {
  if (!wfEd.name.trim()) return "Ponga un nombre a la automatización.";
  if (!wfEd.nodes.some((n) => n.kind === "action")) return "Agregue al menos una acción.";
  const reachable = new Set(["trigger"]);
  const trigger = wfEd.nodes.find((n) => n.kind === "trigger");
  reachable.add(trigger.id);
  const queue = [trigger.id];
  while (queue.length) {
    const id = queue.shift();
    wfEd.edges.filter((e) => e.from === id).forEach((e) => { if (!reachable.has(e.to)) { reachable.add(e.to); queue.push(e.to); } });
  }
  const loose = wfEd.nodes.find((n) => !reachable.has(n.id));
  if (loose) {
    wfeSelect({ kind: "node", id: loose.id });
    return `El paso «${wfeTitle(loose)}» no está conectado al flujo: conéctelo o elimínelo.`;
  }
  return null;
}

async function wfeSave(thenTest) {
  const errorBox = $("#wfe-error");
  errorBox.innerHTML = "";
  const local = wfeLocalCheck();
  if (local) { errorBox.innerHTML = `<div class="error-box" style="margin:8px 16px 0">${esc(local)}</div>`; return; }

  const save = $("#wfe-save"), test = $("#wfe-test");
  save.disabled = test.disabled = true;
  try {
    const payload = wfePayload();
    const saved = wfEd.id
      ? await Api.put(`/api/workflows/${wfEd.id}`, payload)
      : await Api.post("/api/workflows", payload);
    const created = !wfEd.id;
    wfEd.id = saved.id;
    // Lo guardado vuelve con ids de acción y contraseñas ya en el servidor.
    saved.graph.nodes.forEach((n) => {
      const node = wfeNode(n.id);
      if (!node) return;
      node.actionId = n.actionId || 0;
      node.hasSecret = !!n.hasSecret;
      node.secret = "";
    });
    const trigger = wfEd.nodes.find((n) => n.kind === "trigger");
    if (trigger) trigger.conditions = JSON.parse(JSON.stringify(saved.conditions || {}));
    if (created) history.replaceState(null, "", `#/workflows/edit?id=${saved.id}`);
    $("#page-title").textContent = "Editar automatización";
    toast(created ? "Automatización creada." : "Automatización guardada.");
    wfStopPreview();

    if (thenTest) {
      if (!confirm("La prueba ejecuta las acciones DE VERDAD (envía el correo, abre la puerta, hace sonar el parlante) con un evento de ejemplo. ¿Continuar?")) return;
      test.textContent = "Probando…";
      const run = await Api.post(`/api/workflows/${wfEd.id}/test`);
      wfEd.results = {};
      run.steps.forEach((s) => { if (s.nodeId) wfEd.results[s.nodeId] = { success: s.success, detail: s.detail }; });
      wfeDraw();
      toast(run.success ? "Prueba ejecutada correctamente." : "La prueba terminó con errores.", !run.success);
      wfRunModal(run);
    } else {
      location.hash = "#/workflows";
    }
  } catch (err) {
    errorBox.innerHTML = `<div class="error-box" style="margin:8px 16px 0">${esc(err.error)}</div>`;
  } finally {
    save.disabled = test.disabled = false;
    test.textContent = "Probar";
  }
}
