// CLR TrueCentral VMS — panel: licenciamiento (Sistema → Licencia).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
"use strict";

const LICENSE_STATES = {
  Active:      { cls: "on",       label: "Activa" },
  Trial:       { cls: "admin",    label: "Período de prueba" },
  GracePeriod: { cls: "operator", label: "Período de gracia" },
  Restricted:  { cls: "off",      label: "Restringida" },
  Unlicensed:  { cls: "off",      label: "Sin licencia" },
};

function licenseStateTag(state) {
  const st = LICENSE_STATES[state] || { cls: "operator", label: state };
  return `<span class="tag ${st.cls}">${esc(st.label)}</span>`;
}

function licenseModeLabel(mode) {
  return { ONLINE: "En línea (heartbeat con gracia)", OFFLINE: "Sin conexión (archivo firmado)", TRIAL: "Prueba incorporada", NONE: "—" }[mode] || mode;
}

function licenseDate(iso) {
  return iso ? formatDate(iso) : "—";
}

/** Barra de uso de un cupo: verde bajo el 80 %, ámbar al acercarse, rojo al tope. */
function licenseUsageBar(m) {
  if (m.quota === null || m.quota === undefined) return `<span class="muted">—</span>`;
  const quota = m.quota, used = m.inUse || 0;
  const pct = quota > 0 ? Math.min(100, Math.round((used / quota) * 100)) : (used > 0 ? 100 : 0);
  const color = !m.enabled ? "var(--muted)" : pct >= 100 ? "var(--danger)" : pct >= 80 ? "var(--warn)" : "var(--ok)";
  return `
    <div style="display:flex;align-items:center;gap:10px;min-width:220px">
      <div style="flex:1;height:8px;border-radius:999px;background:rgba(139,152,165,.18);overflow:hidden">
        <div style="width:${pct}%;height:100%;background:${color}"></div>
      </div>
      <span style="font-size:12px;white-space:nowrap"><b>${used}</b> <span class="muted">de ${quota} ${esc(m.unit || "")}</span></span>
    </div>`;
}

function licenseModuleRow(m) {
  return `
    <tr>
      <td style="padding-left:28px">${esc(m.name)}</td>
      <td>${m.moduleKey === null
        ? `<span class="tag admin">Base</span>`
        : m.enabled ? `<span class="tag on">Incluido</span>` : `<span class="tag operator">No incluido</span>`}</td>
      <td>${licenseUsageBar(m)}</td>
    </tr>`;
}

/** Agrupa por área conservando el orden del catálogo. */
function licenseByArea(items, areaOf) {
  const groups = new Map();
  for (const it of items) {
    const area = areaOf(it) || "Otros";
    if (!groups.has(area)) groups.set(area, []);
    groups.get(area).push(it);
  }
  return [...groups.entries()];
}

function licenseKindTag(kind) {
  return kind === "Base" ? `<span class="tag admin">Base</span>` : `<span class="tag on">Expansión</span>`;
}

// Pestaña abierta y filtro del resumen: se recuerdan mientras la página está abierta.
const licenseView = { tab: "summary", onlyIncluded: true };

function licenseCardsHtml(s) {
  const remaining = s.daysRemaining === null || s.daysRemaining === undefined ? "—" : `${s.daysRemaining} día(s)`;
  return `
    <div class="cards">
      <div class="card">
        <div class="card-label">Estado</div>
        <div class="card-value small">${licenseStateTag(s.state)}</div>
        <div class="muted" style="font-size:12px">${esc(licenseModeLabel(s.mode))}</div>
      </div>
      <div class="card">
        <div class="card-label">Licencia</div>
        <div class="card-value small">${s.licenseKey ? `<code>${esc(s.licenseKey)}</code>` : "—"}</div>
        <div class="muted" style="font-size:12px">${esc(s.customerName || "")}${s.package ? ` · ${esc(s.package)}` : ""}</div>
      </div>
      <div class="card">
        <div class="card-label">${s.state === "Trial" ? "Prueba hasta" : "Vence"}</div>
        <div class="card-value small">${s.state === "Trial" ? licenseDate(s.expiresAt) : (s.expiresAt ? licenseDate(s.expiresAt) : (s.licenseKey ? "Perpetua" : "—"))}</div>
        <div class="muted" style="font-size:12px">${remaining === "—" ? "" : `${remaining} restantes`}</div>
      </div>
      <div class="card">
        <div class="card-label">Validación en línea</div>
        <div class="card-value small">${s.mode === "ONLINE" ? licenseDate(s.lastValidatedAt) : "No aplica"}</div>
        <div class="muted" style="font-size:12px">${s.mode === "ONLINE" && s.nextValidationAt ? `Próxima: ${licenseDate(s.nextValidationAt)} · gracia hasta ${licenseDate(s.graceEndsAt)}` : ""}</div>
      </div>
      <div class="card">
        <div class="card-label">Este equipo</div>
        <div class="card-value small"><code>${esc(s.hardwareId)}</code></div>
        <div class="muted" style="font-size:12px">${esc(s.hostname)} · v${esc(s.serverVersion)}</div>
      </div>
    </div>`;
}

async function renderLicense() {
  $("#page-title").textContent = "Licencia";
  const isAdmin = Perms.can("system.license");
  let s;
  try { s = await Api.get("/api/system/license"); }
  catch (e) { $("#view").innerHTML = `<div class="error-box">${esc(e.error)}</div>`; return; }

  const banner = s.warning
    ? `<div class="${s.operational ? "warn-box" : "error-box"}">${esc(s.warning)}</div>`
    : (s.message ? `<div class="info-box">${esc(s.message)}</div>` : "");
  const codes = s.codes || [];
  const tabs = [
    { key: "summary", label: "Resumen" },
    { key: "codes", label: `Códigos instalados${codes.length ? ` (${codes.length})` : ""}` },
  ];

  $("#view").innerHTML = `
    ${banner}
    ${licenseCardsHtml(s)}
    ${isAdmin ? `
      <div class="toolbar">
        <h3>Licencia de esta instalación</h3>
        <div style="display:flex;gap:6px;flex-wrap:wrap">
          <button class="btn" id="lic-add">Agregar códigos…</button>
          <button class="btn ghost" id="lic-refresh" ${s.licenseKey && s.mode === "ONLINE" && s.onlineConfigured ? "" : "disabled"}
            title="Contacta al servidor de licencias ahora: trae expansiones nuevas y renueva la gracia">Revalidar ahora</button>
          <button class="btn ghost danger" id="lic-deactivate" ${s.licenseKey ? "" : "disabled"}
            title="Libera el cupo de este equipo para migrar el servidor a otra máquina">Desactivar en este equipo</button>
        </div>
      </div>` : ""}
    <div class="res-tabs" role="tablist">
      ${tabs.map((t) => `<button type="button" role="tab" class="res-tab${t.key === licenseView.tab ? " on" : ""}"
        data-tab="${t.key}" aria-selected="${t.key === licenseView.tab}">${esc(t.label)}</button>`).join("")}
    </div>
    <div id="lic-body" class="res-tab-body"></div>`;

  $$("#view .res-tab").forEach((b) => b.addEventListener("click", () => {
    licenseView.tab = b.dataset.tab;
    $$("#view .res-tab").forEach((x) => {
      x.classList.toggle("on", x === b);
      x.setAttribute("aria-selected", String(x === b));
    });
    drawLicenseTab(s);
  }));
  drawLicenseTab(s);

  if (isAdmin) {
    $("#lic-add").addEventListener("click", () => openLicenseCodesDialog(s));
    $("#lic-refresh").addEventListener("click", async () => {
      await licenseCall(() => Api.post("/api/system/license/refresh"));
    });
    $("#lic-deactivate").addEventListener("click", async () => {
      if (!confirm(`¿Desactivar la licencia ${s.licenseKey} en este equipo? El sistema volverá al período de prueba (si queda) o quedará sin licencia hasta activar otra vez.`)) return;
      await licenseCall(() => Api.post("/api/system/license/deactivate"));
    });
  }
}

function drawLicenseTab(s) {
  const body = $("#lic-body");
  if (licenseView.tab === "codes") { body.innerHTML = licenseCodesHtml(s); bindLicenseCodes(s); return; }

  // Resumen: lo que la instalación tiene en total (base + expansiones), por área.
  const visible = s.modules.filter((m) => !licenseView.onlyIncluded || m.moduleKey === null || m.enabled);
  const rows = licenseByArea(visible, (m) => m.area).map(([area, items]) => `
      <tr class="lic-area"><td colspan="3"><b>${esc(area)}</b></td></tr>
      ${items.map(licenseModuleRow).join("")}`).join("");
  body.innerHTML = `
    <div style="display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:10px">
      <p class="muted res-tab-intro" style="margin:0">Totales de la licencia base más sus expansiones. Los cupos cuentan los
        elementos <b>habilitados</b>; al alcanzar uno, el sistema no deja habilitar más.</p>
      <label style="display:flex;align-items:center;gap:6px;margin:0;white-space:nowrap">
        <input type="checkbox" id="lic-only" ${licenseView.onlyIncluded ? "checked" : ""} style="width:auto">
        Solo lo incluido en la licencia</label>
    </div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Módulo / cupo</th><th>Licencia</th><th>Uso</th></tr></thead>
      <tbody>${rows || `<tr><td colspan="3" class="muted">La licencia no incluye ningún módulo.</td></tr>`}</tbody>
    </table></div>`;
  $("#lic-only").addEventListener("change", (e) => { licenseView.onlyIncluded = e.target.checked; drawLicenseTab(s); });
}

function licenseCodesHtml(s) {
  const codes = s.codes || [];
  if (!codes.length) {
    return `<div class="info-box">No hay ningún código instalado${s.state === "Trial" ? " (el sistema está en el período de prueba)" : ""}.
      Use «Agregar códigos…» para activar la licencia base y, si las compró, sus expansiones.</div>`;
  }
  return `
    <p class="muted res-tab-intro">Una licencia base por instalación; cada expansión (más canales, un módulo nuevo…) tiene su
      propio código y se suma a la base. Abra un código para ver lo que aporta.</p>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Código</th><th>Tipo</th><th>Paquete</th><th>Aporta</th><th>Vence</th></tr></thead>
      <tbody>${codes.map((c, i) => `
        <tr class="clickable" data-code="${i}" style="cursor:pointer" title="Ver lo que aporta este código">
          <td><code>${esc(c.licenseKey)}</code></td>
          <td>${licenseKindTag(c.kind)}</td>
          <td>${esc(c.package || "—")}</td>
          <td>${esc(c.summary)}</td>
          <td>${c.expiresAt ? licenseDate(c.expiresAt) : "Perpetua"}</td>
        </tr>`).join("")}</tbody>
    </table></div>`;
}

function bindLicenseCodes(s) {
  $$("#lic-body tr[data-code]").forEach((tr) => tr.addEventListener("click", () => {
    const c = s.codes[Number(tr.dataset.code)];
    const groups = licenseByArea(c.items, (it) => it.area);
    openModal(`
      <h3><code>${esc(c.licenseKey)}</code> ${licenseKindTag(c.kind)}</h3>
      <p class="muted" style="font-size:12px;margin:-8px 0 14px">
        ${c.package ? `Paquete ${esc(c.package)} · ` : ""}${c.issuedAt ? `emitida el ${licenseDate(c.issuedAt)} · ` : ""}${c.expiresAt ? `vence el ${licenseDate(c.expiresAt)}` : "perpetua"}
        ${c.kind === "Base" ? "<br>Sin lo que suman las expansiones (eso está en el resumen)." : ""}</p>
      <div class="table-scroll" style="max-height:60vh"><table class="grid">
        <thead><tr><th>${c.kind === "Base" ? "Incluye" : "Suma"}</th><th></th></tr></thead>
        <tbody>${groups.length ? groups.map(([area, items]) => `
          <tr class="lic-area"><td colspan="2"><b>${esc(area)}</b></td></tr>
          ${items.map((it) => `<tr><td style="padding-left:28px">${esc(it.name)}</td><td>${esc(it.value)}</td></tr>`).join("")}`).join("")
          : `<tr><td colspan="2" class="muted">Este código no aporta nada.</td></tr>`}</tbody>
      </table></div>
      <div class="modal-actions"><button class="btn" type="button" id="lic-code-close">Cerrar</button></div>`);
    $("#lic-code-close").addEventListener("click", closeModal);
  }));
}

/**
 * Ingreso único: la licencia base y sus expansiones en una sola lista (en
 * cualquier orden; el servidor reconoce cuál es cuál), con internet o con
 * archivos (.req → .lic).
 */
function openLicenseCodesDialog(s) {
  const state = { online: s.onlineConfigured, codes: [""] };

  const draw = () => {
    openModal(`
      <h3>Agregar códigos de licencia</h3>
      <div class="field">
        <label>¿Cómo se activa?</label>
        <div class="segmented">
          <label title="${s.onlineConfigured ? "" : "Este servidor no tiene configurado el servidor de licencias"}">
            <input type="radio" name="lic-way" value="online" ${state.online ? "checked" : ""} ${s.onlineConfigured ? "" : "disabled"}>
            Con internet</label>
          <label><input type="radio" name="lic-way" value="offline" ${state.online ? "" : "checked"}> Sin internet (con archivos)</label>
        </div>
        <div class="muted" style="font-size:12px;margin-top:6px">${state.online
          ? `Se confirma con el servidor de licencias (${esc(s.licenseServerUrl || "")}) y queda aplicada en el acto.`
          : "Para equipos sin acceso a internet: se descarga una solicitud de este equipo y se carga el archivo de licencia que devuelve CLRobotics."}</div>
      </div>
      <div class="field">
        <label>Códigos</label>
        <div id="lic-code-rows" style="display:flex;flex-direction:column;gap:6px">
          ${state.codes.map((c, i) => `
            <div style="display:flex;gap:6px">
              <input class="lic-code-input" data-i="${i}" value="${esc(c)}" placeholder="XXXXX-XXXXX-XXXXX-XXXXX-XXXXX"
                autocomplete="off" spellcheck="false" style="flex:1;font-family:var(--mono, monospace)">
              ${state.codes.length > 1 ? `<button class="btn ghost small" type="button" data-remove="${i}" title="Quitar">✕</button>` : ""}
            </div>`).join("")}
        </div>
        <button class="btn ghost small" type="button" id="lic-code-more" style="margin-top:6px">+ Otro código</button>
        <div class="muted" style="font-size:12px;margin-top:6px">La licencia base y sus expansiones, en cualquier orden: el
          sistema reconoce cuál es cuál.${s.licenseKey ? ` La base instalada es <code>${esc(s.licenseKey)}</code>; basta con las expansiones nuevas.` : ""}</div>
      </div>
      ${state.online ? "" : `
        <div class="field">
          <label>1. Solicitud de este equipo</label>
          <button class="btn ghost" type="button" id="lic-req">Descargar solicitud (.req)</button>
          <div class="muted" style="font-size:12px;margin-top:6px">Envíela a soporte@clrobotics.cl (o súbala en la licencia
            base del portal de licencias). Lleva los códigos de arriba y la identificación de este equipo
            (<code>${esc(s.hardwareId)}</code>).</div>
        </div>
        <div class="field">
          <label>2. Archivo de licencia recibido</label>
          <input type="file" id="lic-file" accept=".lic,.json,application/json">
          <div class="muted" style="font-size:12px;margin-top:6px">El archivo <code>.lic</code> de la licencia base ya trae
            sus expansiones. También sirve para cargar uno actualizado sin escribir códigos.</div>
        </div>`}
      <div id="lic-result"></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="lic-cancel">Cerrar</button>
        <button class="btn" type="button" id="lic-go">${state.online ? "Aplicar" : "Importar archivo"}</button>
      </div>`);

    const collect = () => { state.codes = $$(".lic-code-input").map((x) => x.value.trim()); };
    $$("input[name=lic-way]").forEach((r) => r.addEventListener("change", () => {
      collect(); state.online = r.value === "online"; draw();
    }));
    $("#lic-code-more").addEventListener("click", () => { collect(); state.codes.push(""); draw(); $$(".lic-code-input").at(-1).focus(); });
    $$("[data-remove]").forEach((b) => b.addEventListener("click", () => {
      collect(); state.codes.splice(Number(b.dataset.remove), 1); draw();
    }));
    $("#lic-cancel").addEventListener("click", closeModal);
    $("#lic-req")?.addEventListener("click", async () => {
      collect();
      const codes = state.codes.filter(Boolean);
      if (!codes.length && !s.licenseKey) { toast("Escriba al menos el código de la licencia base.", true); return; }
      try {
        const file = await Api.post("/api/system/license/request", { codes });
        downloadTextFile(file.fileName, file.content);
        toast(`Solicitud ${file.fileName} descargada.`);
      } catch (err) {
        toast(err.error || "No se pudo generar la solicitud.", true);
      }
    });
    $("#lic-go").addEventListener("click", async (e) => {
      collect();
      const button = e.currentTarget;
      if (state.online) {
        const codes = state.codes.filter(Boolean);
        if (!codes.length) { toast("Escriba al menos un código.", true); return; }
        button.disabled = true;
        button.textContent = "Aplicando…";
        let result;
        try { result = await Api.post("/api/system/license/apply", { codes }); }
        catch (err) { result = { ...(err.data || {}), error: err.error }; }
        await refreshLicenseBanner();
        if (!result.items || !result.items.length) {
          button.disabled = false;
          button.textContent = "Aplicar";
          toast(result.error || result.message || "La operación falló.", true);
          return;
        }
        const allOk = result.items.every((i) => i.success);
        if (allOk) { closeModal(); toast(result.message); await renderLicense(); return; }
        // Algo no entró: el diálogo queda abierto con el detalle por código.
        $("#lic-result").innerHTML = `
          <div class="${result.items.some((i) => i.success) ? "warn-box" : "error-box"}" style="margin-top:12px">
            <b>${esc(result.error || result.message)}</b>
            <ul style="margin:6px 0 0;padding-left:18px">${result.items.map((i) => `
              <li><code>${esc(i.code)}</code> — ${i.success ? (i.kind === "Base" ? "base" : "expansión") + " ✓" : "✗"} ${esc(i.message)}</li>`).join("")}</ul>
          </div>`;
        button.disabled = false;
        button.textContent = "Aplicar";
        renderLicense();
        return;
      }
      const input = $("#lic-file");
      if (!input.files || !input.files[0]) { toast("Seleccione el archivo .lic recibido.", true); return; }
      const text = await input.files[0].text();
      try {
        const result = await Api.post("/api/system/license/import", { licenseFile: text });
        closeModal();
        toast(result.message);
      } catch (err) {
        toast(err.error || "No se pudo importar el archivo.", true);
        return;
      }
      await renderLicense();
      await refreshLicenseBanner();
    });
  };
  draw();
}

async function licenseCall(fn) {
  try {
    const result = await fn();
    toast(result.message);
  } catch (err) {
    toast(err.error || "La operación falló.", true);
  }
  await renderLicense();
  await refreshLicenseBanner();
}

function downloadTextFile(name, content) {
  const blob = new Blob([content], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/**
 * Franja de aviso bajo la barra superior (prueba por vencer, gracia,
 * restricción). Se consulta al entrar y cuando el servidor avisa por el hub.
 */
async function refreshLicenseBanner() {
  const el = $("#license-banner");
  if (!el) return;
  let s;
  try { s = await Api.get("/api/system/license"); }
  catch { el.classList.add("hidden"); return; }
  if (!s.warning) { el.classList.add("hidden"); return; }
  el.className = `license-banner ${s.operational ? "warn" : "danger"}`;
  el.innerHTML = `<span>${esc(s.warning)}</span> <a href="#/license">Ver licencia</a>`;
}

let licenseBannerStarted = false;

/** Muestra la franja y la mantiene al día: cambios de licencia por el hub y los días restantes cada hora. */
function startLicenseBanner() {
  refreshLicenseBanner();
  if (licenseBannerStarted) return;
  licenseBannerStarted = true;
  VmsHub.on("ConfigChanged", topic => { if (topic === "license") refreshLicenseBanner(); });
  setInterval(() => { if (Api.token) refreshLicenseBanner(); }, 60 * 60 * 1000);
}
