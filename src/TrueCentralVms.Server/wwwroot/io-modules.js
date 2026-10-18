// CLR TrueCentral VMS — panel: módulos de entradas y salidas (E/S).
//
// Módulos de E/S digital por red (Advantech ADAM-6000): sus salidas (relés)
// mueven barreras, portones y balizas; sus entradas leen pulsadores, lazos y
// sensores. Una tarjeta por módulo con el estado en vivo de cada entrada y
// salida (llega por el hub) y los botones para operar los relés a mano.
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

let ioDriversCache = null;
async function getIoDrivers() {
  ioDriversCache ??= await Api.get("/api/io/drivers");
  return ioDriversCache;
}

/** Suscripciones al hub de esta página (se sueltan al salir). */
let ioHubBound = null;

function ioModulesDetachHub() {
  if (!ioHubBound) return;
  VmsHub.off("IoModuleChanged", ioHubBound.changed);
  VmsHub.off("ConfigChanged", ioHubBound.config);
  ioHubBound = null;
}

// El hub manda los enums como números (la API, como texto): mismos nombres que el servidor.
const IO_POINT_KIND_NAMES = ["Input", "Output"];
const IO_STATUS_NAMES = ["Unknown", "Online", "Offline"];

/** El módulo tal como lo manda el hub, con los enums en texto como los entrega la API. */
function ioNormalize(m) {
  const point = (p) => ({ ...p, kind: typeof p.kind === "number" ? IO_POINT_KIND_NAMES[p.kind] ?? "Input" : p.kind });
  return {
    ...m,
    status: typeof m.status === "number" ? IO_STATUS_NAMES[m.status] ?? "Unknown" : m.status,
    inputs: (m.inputs || []).map(point),
    outputs: (m.outputs || []).map(point),
  };
}

function ioStatusTag(m) {
  if (m.disabledByLicense) return licenseWaitTag(m).trim();
  if (!m.enabled) return `<span class="tag operator">Pausado</span>`;
  switch (m.status) {
    case "Online": return `<span class="tag on">En línea</span>`;
    case "Offline": return `<span class="tag off">Sin conexión</span>`;
    default: return `<span class="tag operator">Conectando…</span>`;
  }
}

function ioStatusCell(m) {
  return `${ioStatusTag(m)}${m.lastError && m.status !== "Online" ? `<div class="muted io-error" title="${esc(m.lastError)}">${esc(m.lastError)}</div>` : ""}`;
}

/** Estado de una entrada o salida en palabras ("Activa", "Apagada", "Sin lectura"). */
function ioStateLabel(point) {
  return point.kind === "Input"
    ? (point.active === true ? "Activa" : point.active === false ? "En reposo" : "Sin lectura")
    : (point.active === true ? "Encendida" : point.active === false ? "Apagada" : "Sin lectura");
}

/** Punto de estado de una entrada o salida: encendido, apagado o sin lectura. */
function ioDotHtml(point) {
  const cls = point.active === true ? "on" : point.active === false ? "off" : "unknown";
  const raw = point.raw === null || point.raw === undefined ? "sin lectura" : `nivel ${point.raw ? 1 : 0}`;
  return `<span class="io-dot ${cls}" title="${esc(ioStateLabel(point))} (${esc(raw)})"></span>`;
}

function ioStateHtml(point) {
  return `<span class="io-state${point.active === true ? " on" : ""}">${esc(ioStateLabel(point))}</span>`;
}

function ioInputsHtml(m) {
  if (m.inputs.length === 0) return `<div class="muted">Este modelo no tiene entradas.</div>`;
  return `<div class="io-inputs">${m.inputs.map((p) => `
    <div class="io-input" data-point="Input:${p.number}">
      ${ioDotHtml(p)}
      <span class="io-name" title="${esc(p.name)} · ${esc(p.code)} · ${p.inverted ? "activa al cerrar el contacto" : "activa al abrir el contacto"}">${esc(p.name)}</span>
      <span class="io-code">${esc(p.code)}</span>
      ${ioStateHtml(p)}
    </div>`).join("")}</div>`;
}

function ioOutputsHtml(m, canOperate) {
  if (m.outputs.length === 0) return `<div class="muted">Este modelo no tiene salidas.</div>`;
  return `<div class="io-outputs">${m.outputs.map((p) => `
    <div class="io-output" data-point="Output:${p.number}">
      <div class="io-output-head">${ioDotHtml(p)}<span class="io-name" title="${esc(p.name)} · ${esc(p.code)}">${esc(p.name)}</span><span class="io-code">${esc(p.code)}</span>${ioStateHtml(p)}</div>
      ${canOperate ? `<div class="io-output-actions">
        <input type="number" class="io-seconds" min="0.1" max="3600" step="0.1" value="1" title="Duración del pulso (segundos)">
        <button class="btn ghost io-cmd" data-cmd="Pulse" data-op="io:${m.id}" title="Encender y soltar después de los segundos indicados (barrera, portón)">Pulso</button>
        <button class="btn ghost io-cmd" data-cmd="On" data-op="io:${m.id}" title="Encender y dejar encendida">Encender</button>
        <button class="btn ghost io-cmd" data-cmd="Off" data-op="io:${m.id}" title="Apagar (también corta un pulso o parpadeo)">Apagar</button>
      </div>` : ""}
    </div>`).join("")}</div>`;
}

function ioCardHtml(m, canOperate, canConfigure) {
  const model = m.model ? `${esc(m.model)}${m.modelKey ? ` <span class="muted" title="Elegido a mano">(a mano)</span>` : ""}` : "—";
  return `
    <div class="io-card" data-id="${m.id}">
      <div class="io-card-head">
        <div class="io-card-title">
          <b>${esc(m.name)}</b>
          ${locationLineHtml(m.location)}
        </div>
        <div class="io-card-meta">
          <span class="muted">${esc(m.host)}:${m.port}</span>
          <span>${model}</span>
          <span class="io-status">${ioStatusCell(m)}</span>
        </div>
        ${canConfigure ? `<div class="row-actions">
          <button class="btn ghost io-edit">Editar</button>
          <button class="btn danger io-delete">Eliminar</button>
        </div>` : ""}
      </div>
      <div class="io-card-body">
        <div class="io-section">
          <div class="io-section-title">Entradas</div>
          <div class="io-inputs-wrap">${ioInputsHtml(m)}</div>
        </div>
        <div class="io-section">
          <div class="io-section-title">Salidas (relés)</div>
          <div class="io-outputs-wrap">${ioOutputsHtml(m, canOperate)}</div>
        </div>
      </div>
    </div>`;
}

/** Actualiza en sitio la tarjeta de un módulo (sin tocar los segundos que se están escribiendo). */
function ioUpdateCard(m) {
  const card = $(`#view .io-card[data-id="${m.id}"]`);
  if (!card) return false;
  card.querySelector(".io-status").innerHTML = ioStatusCell(m);
  for (const p of [...m.inputs, ...m.outputs]) {
    const row = card.querySelector(`[data-point="${p.kind}:${p.number}"]`);
    if (!row) return false; // cambió el modelo: hay que redibujar
    const holder = document.createElement("span");
    holder.innerHTML = ioDotHtml(p) + ioStateHtml(p);
    row.querySelector(".io-dot").replaceWith(holder.firstElementChild);
    row.querySelector(".io-state").replaceWith(holder.firstElementChild);
  }
  return true;
}

async function renderIoModules() {
  ioModulesDetachHub();
  $("#page-title").textContent = "Módulos de E/S";
  let modules;
  try { modules = await Api.get("/api/io/modules"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const canConfigure = Perms.can("io.configure");
  const canOperate = Perms.can("io.operate");
  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Módulos de entradas y salidas <span class="muted" style="font-weight:normal;font-size:12px">(el estado se actualiza solo)</span></h3>
      <div class="row-actions">
        ${canConfigure ? `<button class="btn" id="btn-io-new">Agregar módulo</button>` : ""}
      </div>
    </div>
    ${modules.length === 0 ? `
      <div class="info-box">
        Aún no hay módulos de E/S. ${canConfigure
          ? "Use <b>Agregar módulo</b> con la dirección del equipo (Advantech ADAM-6000 por Modbus TCP, puerto 502). Al guardar se lee el modelo y se arman sus entradas y relés, que después puede nombrar («Barrera estacionamiento», «Lazo de salida»). Desde ese momento las automatizaciones pueden operar sus relés (acción <b>Operar relé</b>) y reaccionar a sus entradas."
          : "Un administrador debe agregarlos."}
      </div>` : `
      <div class="io-cards">${modules.map((m) => ioCardHtml(m, canOperate, canConfigure)).join("")}</div>
      ${canConfigure ? `<div class="info-box io-watchdog">
        <b>Antes de cablear una barrera o una cerradura</b>, configure el <b>watchdog de comunicación</b> del propio módulo
        (valores de seguridad FSV, desde su página web o el Adam.NET Utility). Los pulsos los temporiza el servidor: si
        el servidor se detiene de golpe a mitad de un pulso, el relé queda energizado hasta que el watchdog del módulo
        lo suelte.
      </div>` : ""}`}`;

  const byCard = (e) => modules.find((m) => m.id === Number(e.target.closest(".io-card").dataset.id));
  $("#btn-io-new")?.addEventListener("click", () => ioModuleModal(null));
  $$("#view .io-edit").forEach((b) => b.addEventListener("click", (e) => ioModuleModal(byCard(e))));
  $$("#view .io-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const m = byCard(e);
    if (!confirm(`¿Eliminar el módulo "${m.name}"? Las automatizaciones que usan sus relés o entradas dejarán de funcionar.`)) return;
    try {
      await Api.delete(`/api/io/modules/${m.id}`);
      toast("Módulo eliminado.");
      renderIoModules();
    } catch (err) { toast(err.error, true); }
  }));
  // Delegado en la grilla (se rehace con cada redibujo; #view no, y acumularía manejadores).
  $("#view .io-cards")?.addEventListener("click", async (e) => {
    const button = e.target.closest(".io-cmd");
    if (!button || button.disabled) return;
    const card = button.closest(".io-card");
    const row = button.closest(".io-output");
    const number = Number(row.dataset.point.split(":")[1]);
    const body = { command: button.dataset.cmd };
    if (body.command === "Pulse") body.seconds = Number(row.querySelector(".io-seconds").value) || 1;
    button.disabled = true;
    try {
      // El estado nuevo llega por el hub en cuanto el servidor relee el módulo.
      const r = await Api.post(`/api/io/modules/${card.dataset.id}/outputs/${number}`, body);
      toast(r.message);
    } catch (err) { toast(err.error, true); }
    finally { button.disabled = false; }
  });

  // Estado en vivo por el hub; si cambió la configuración, se redibuja.
  ioHubBound = {
    changed: (raw) => {
      if (!location.hash.startsWith("#/io-modules")) return;
      const m = ioNormalize(raw);
      const index = modules.findIndex((x) => x.id === m.id);
      if (index >= 0) modules[index] = m;
      if (!ioUpdateCard(m)) renderIoModules();
    },
    config: (topic) => {
      if (topic !== "io-modules" || !location.hash.startsWith("#/io-modules")) return;
      if ($("#modal-backdrop") && !$("#modal-backdrop").classList.contains("hidden")) return;
      renderIoModules();
    },
  };
  VmsHub.on("IoModuleChanged", ioHubBound.changed);
  VmsHub.on("ConfigChanged", ioHubBound.config);
}

/** Alta / edición de un módulo, con los nombres de sus entradas y salidas. */
async function ioModuleModal(module) {
  const isNew = !module;
  let drivers, places;
  try { [drivers, places] = await Promise.all([getIoDrivers(), loadLocationChoices()]); }
  catch (err) { toast(err.error, true); return; }
  const driver0 = drivers.find((d) => d.key === module?.driverKey) ?? drivers[0];
  if (!driver0) { toast("El servidor no tiene drivers de módulos de E/S.", true); return; }

  // Nombres y lógica que se van escribiendo: sobreviven a que cambie el modelo.
  const names = new Map();
  const inverted = new Map();
  for (const p of [...(module?.inputs ?? []), ...(module?.outputs ?? [])]) {
    names.set(`${p.kind}:${p.number}`, p.name === p.code ? "" : p.name);
    if (p.kind === "Input") inverted.set(p.number, p.inverted);
  }
  // Modelo cuyas entradas y salidas se muestran: el elegido, el que informó la prueba o el actual.
  let probedModel = null;
  const shownModel = () => {
    const driver = drivers.find((d) => d.key === $("#io-driver").value) ?? driver0;
    const chosen = $("#io-model").value;
    return driver.models.find((m) => m.key === chosen)
      ?? driver.models.find((m) => m.name === probedModel)
      ?? driver.models.find((m) => m.key === module?.model)
      ?? null;
  };

  openModal(`
    <h3>${isNew ? "Agregar módulo de E/S" : "Editar módulo de E/S"}</h3>
    <div id="io-modal-error"></div>
    <form id="io-form">
      <div class="field">
        <label>Nombre</label>
        <input id="io-name" required maxlength="128" value="${esc(module?.name ?? "")}" placeholder="Gabinete acceso vehicular">
      </div>
      ${locationFieldHtml("io-location", places, module?.locationId ?? null)}
      <div class="form-grid">
        <div class="field">
          <label>Marca / protocolo</label>
          <select id="io-driver">
            ${drivers.map((d) => `<option value="${esc(d.key)}" ${driver0.key === d.key ? "selected" : ""}>${esc(d.displayName)}</option>`).join("")}
          </select>
        </div>
        <div class="field">
          <label>Modelo</label>
          <select id="io-model"></select>
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección (IP o hostname)</label>
          <input id="io-host" required value="${esc(module?.host ?? "")}" placeholder="192.168.10.90">
        </div>
        <div class="field">
          <label>Puerto Modbus TCP</label>
          <input id="io-port" type="number" min="1" max="65535" required value="${module?.port ?? driver0.defaultPort}">
        </div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Leer las entradas cada (ms)</label>
          <input id="io-poll" type="number" min="100" max="5000" step="50" value="${module?.pollIntervalMs ?? 250}">
          <div class="muted field-hint">250 ms detecta un pulsador o el paso de un vehículo sin cargar la red.</div>
        </div>
        <div class="field">
          <label>&nbsp;</label>
          <label class="checkbox-row"><input type="checkbox" id="io-enabled" ${module ? (module.enabled ? "checked" : "") : "checked"}> Activo (conexión abierta, disponible para operadores y automatizaciones)</label>
        </div>
      </div>
      <div id="io-probe-result"></div>
      <div id="io-points"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="io-cancel">Cancelar</button>
        <button class="btn ghost" type="button" id="io-probe">Probar conexión</button>
        <button class="btn" type="submit" id="io-save">${isNew ? "Guardar" : "Guardar cambios"}</button>
      </div>
    </form>`, true);

  const fillModels = () => {
    const driver = drivers.find((d) => d.key === $("#io-driver").value) ?? driver0;
    $("#io-model").innerHTML = `<option value="">Automático (lo informa el equipo)</option>` +
      driver.models.map((m) => `<option value="${esc(m.key)}" ${module?.modelKey === m.key ? "selected" : ""}>${esc(m.name)} · ${esc(m.description)}</option>`).join("");
  };

  const keepTyped = () => {
    $$("#io-points [data-name]").forEach((input) => names.set(input.dataset.name, input.value.trim()));
    $$("#io-points [data-logic]").forEach((select) => inverted.set(Number(select.dataset.logic), select.value === "closed"));
  };

  const renderPoints = () => {
    keepTyped();
    const model = shownModel();
    if (!model) {
      $("#io-points").innerHTML = `<div class="muted io-points-hint">Use <b>Probar conexión</b> (o elija el modelo) para nombrar las entradas y los relés.</div>`;
      return;
    }
    const range = (n) => Array.from({ length: n }, (_, i) => i);
    $("#io-points").innerHTML = `
      <div class="io-points">
        ${model.inputs > 0 ? `<div class="io-points-title">Entradas (${model.inputs})</div>
        ${range(model.inputs).map((n) => `
          <div class="io-point-row">
            <span class="io-code">DI${n}</span>
            <input data-name="Input:${n}" maxlength="64" placeholder="DI${n}" value="${esc(names.get(`Input:${n}`) ?? "")}">
            <select data-logic="${n}" title="Cuándo se considera activa">
              <option value="closed" ${(inverted.get(n) ?? true) ? "selected" : ""}>Activa al cerrar el contacto</option>
              <option value="open" ${(inverted.get(n) ?? true) ? "" : "selected"}>Activa al abrir el contacto</option>
            </select>
          </div>`).join("")}` : ""}
        ${model.outputs > 0 ? `<div class="io-points-title">Salidas / relés (${model.outputs})</div>
        ${range(model.outputs).map((n) => `
          <div class="io-point-row">
            <span class="io-code">DO${n}</span>
            <input data-name="Output:${n}" maxlength="64" placeholder="DO${n}" value="${esc(names.get(`Output:${n}`) ?? "")}">
            <span></span>
          </div>`).join("")}` : ""}
        <div class="muted field-hint">Contacto seco: el módulo lee 1 con el contacto abierto. Un pulsador, un lazo o un sensor normal abierto queda «activo» al cerrarse.</div>
      </div>`;
  };

  fillModels();
  renderPoints();
  $("#io-cancel").addEventListener("click", closeModal);
  $("#io-driver").addEventListener("change", () => {
    const driver = drivers.find((d) => d.key === $("#io-driver").value);
    if (driver && isNew) $("#io-port").value = driver.defaultPort;
    fillModels();
    renderPoints();
  });
  $("#io-model").addEventListener("change", renderPoints);

  const readForm = () => {
    keepTyped();
    const model = shownModel();
    const points = [];
    if (model) {
      for (let n = 0; n < model.inputs; n++)
        points.push({ kind: "Input", number: n, name: names.get(`Input:${n}`) || null, inverted: inverted.get(n) ?? true });
      for (let n = 0; n < model.outputs; n++)
        points.push({ kind: "Output", number: n, name: names.get(`Output:${n}`) || null, inverted: false });
    }
    return {
      name: $("#io-name").value.trim(),
      driverKey: $("#io-driver").value,
      host: $("#io-host").value.trim(),
      port: Number($("#io-port").value),
      modelKey: $("#io-model").value || null,
      pollIntervalMs: Number($("#io-poll").value) || 250,
      enabled: $("#io-enabled").checked,
      locationId: locationFieldValue("io-location"),
      points,
    };
  };

  $("#io-probe").addEventListener("click", async () => {
    const errorBox = $("#io-modal-error");
    const resultBox = $("#io-probe-result");
    errorBox.innerHTML = "";
    resultBox.innerHTML = `<div class="info-box">Conectando con el módulo…</div>`;
    const button = $("#io-probe");
    button.disabled = true;
    try {
      const r = await Api.post("/api/io/modules/probe", readForm());
      if (!r.success) { resultBox.innerHTML = `<div class="error-box">${esc(r.error)}</div>`; return; }
      probedModel = r.model;
      resultBox.innerHTML = `
        <div class="probe-box">
          <div class="probe-title">✔ Conexión validada</div>
          <div class="probe-grid">
            <span>El equipo informa</span><b>${esc(r.reportedModel ?? "(no informa su modelo)")}</b>
            <span>Se trabajará como</span><b>${esc(r.model)}</b>
            <span>Entradas / salidas</span><b>${r.inputs} / ${r.outputs}</b>
          </div>
          ${r.warning ? `<div class="muted" style="margin-top:6px;color:var(--warn)">${esc(r.warning)}</div>` : ""}
        </div>`;
      renderPoints();
    } catch (err) {
      resultBox.innerHTML = "";
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      button.disabled = false;
    }
  });

  $("#io-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errorBox = $("#io-modal-error");
    errorBox.innerHTML = "";
    const button = $("#io-save");
    button.disabled = true;
    button.textContent = "Validando…";
    try {
      const body = readForm();
      const r = isNew ? await Api.post("/api/io/modules", body) : await Api.put(`/api/io/modules/${module.id}`, body);
      closeModal();
      toast(r.warning ?? (isNew ? "Módulo agregado y validado." : "Módulo actualizado."), false);
      renderIoModules();
    } catch (err) {
      errorBox.innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      button.disabled = false;
      button.textContent = isNew ? "Guardar" : "Guardar cambios";
    }
  });
}
