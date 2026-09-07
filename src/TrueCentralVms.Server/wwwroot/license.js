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
  const color = !m.enabled ? "var(--muted)" : pct >= 100 ? "var(--danger)" : pct >= 80 ? "#F59E0B" : "var(--ok)";
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
      <td><b>${esc(m.name)}</b></td>
      <td>${m.moduleKey === null
        ? `<span class="tag admin">Base</span>`
        : m.enabled ? `<span class="tag on">Incluido</span>` : `<span class="tag operator">No incluido</span>`}</td>
      <td>${licenseUsageBar(m)}</td>
    </tr>`;
}

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
  const isAdmin = Api.role === "Admin";
  let s;
  try { s = await Api.get("/api/system/license"); }
  catch (e) { $("#view").innerHTML = `<div class="error-box">${esc(e.error)}</div>`; return; }

  const banner = s.warning
    ? `<div class="${s.operational ? "warn-box" : "error-box"}">${esc(s.warning)}</div>`
    : (s.message ? `<div class="info-box">${esc(s.message)}</div>` : "");

  const addons = s.addons.length
    ? `<div class="toolbar"><h3>Expansiones incluidas</h3></div>
       <div class="table-scroll"><table class="grid">
         <thead><tr><th>Código</th><th>Pack</th><th>Aporta</th><th>Vence</th></tr></thead>
         <tbody>${s.addons.map((a) => `<tr>
           <td><code>${esc(a.licenseKey)}</code></td><td>${esc(a.package || "—")}</td>
           <td>${esc(a.summary)}</td><td>${a.expiresAt ? licenseDate(a.expiresAt) : "Perpetua"}</td></tr>`).join("")}</tbody>
       </table></div>`
    : "";

  $("#view").innerHTML = `
    ${banner}
    ${licenseCardsHtml(s)}
    ${isAdmin ? licenseActionsHtml(s) : ""}
    <div class="toolbar"><h3>Módulos y cupos</h3></div>
    <div class="info-box">Los cupos cuentan los elementos <b>habilitados</b>. Al alcanzar un cupo, el sistema no permite
      habilitar más (los canales de un equipo nuevo que no caben entran deshabilitados). Las expansiones compradas se
      suman solas en la siguiente revalidación en línea o al importar el archivo .lic actualizado.</div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Módulo / cupo</th><th>Licencia</th><th>Uso</th></tr></thead>
      <tbody>${s.modules.map(licenseModuleRow).join("")}</tbody>
    </table></div>
    ${addons}`;

  if (isAdmin) bindLicenseActions(s);
}

function licenseActionsHtml(s) {
  return `
    <div class="toolbar"><h3>Administrar licencia</h3></div>
    <div class="cards" style="grid-template-columns:repeat(auto-fill,minmax(320px,1fr))">
      <div class="card">
        <div class="card-label">Activación en línea</div>
        <p class="muted" style="font-size:12px;margin:6px 0 10px">Código del certificado de licencia (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX).
          ${s.onlineConfigured ? `Servidor: ${esc(s.licenseServerUrl)}` : "<b>Este servidor no tiene configurado el servidor de licencias</b>: use la activación sin conexión."}</p>
        <input id="lic-code" placeholder="Código de activación" value="${esc(s.licenseKey || "")}" style="width:100%;margin-bottom:8px">
        <div class="row-actions" style="display:flex;gap:6px;flex-wrap:wrap">
          <button class="btn" id="lic-activate" ${s.onlineConfigured ? "" : "disabled"}>Activar en línea</button>
          <button class="btn ghost" id="lic-request" title="Descarga un archivo .req con el identificador de este equipo para enviarlo a soporte">Generar solicitud (.req)</button>
        </div>
      </div>
      <div class="card">
        <div class="card-label">Activación sin conexión</div>
        <p class="muted" style="font-size:12px;margin:6px 0 10px">Importe el archivo <code>.lic</code> que soporte de CLRobotics emite a partir de la solicitud
          <code>.req</code> de este equipo (también sirve para cargar un .lic actualizado con expansiones).</p>
        <input type="file" id="lic-file" accept=".lic,.json,application/json" style="margin-bottom:8px">
        <div class="row-actions" style="display:flex;gap:6px;flex-wrap:wrap">
          <button class="btn" id="lic-import">Importar archivo .lic</button>
        </div>
      </div>
      <div class="card">
        <div class="card-label">Mantenimiento</div>
        <p class="muted" style="font-size:12px;margin:6px 0 10px">Revalidar contacta al servidor de licencias ahora (trae expansiones nuevas y renueva la gracia).
          Desactivar libera el cupo de este equipo para migrar el servidor a otra máquina.</p>
        <div class="row-actions" style="display:flex;gap:6px;flex-wrap:wrap">
          <button class="btn ghost" id="lic-refresh" ${s.licenseKey && s.mode === "ONLINE" && s.onlineConfigured ? "" : "disabled"}>Revalidar ahora</button>
          <button class="btn danger" id="lic-deactivate" ${s.licenseKey ? "" : "disabled"}>Desactivar en este equipo</button>
        </div>
      </div>
    </div>`;
}

function bindLicenseActions(s) {
  $("#lic-activate")?.addEventListener("click", async () => {
    const code = $("#lic-code").value.trim();
    if (!code) { toast("Ingrese el código de activación.", true); return; }
    await licenseCall(() => Api.post("/api/system/license/activate", { activationCode: code }));
  });
  $("#lic-request")?.addEventListener("click", async () => {
    const code = $("#lic-code").value.trim();
    try {
      const file = await Api.post("/api/system/license/request", { activationCode: code });
      downloadTextFile(file.fileName, file.content);
      toast(`Solicitud ${file.fileName} generada: envíela a soporte@clrobotics.cl.`);
    } catch (err) {
      toast(err.error || "No se pudo generar la solicitud.", true);
    }
  });
  $("#lic-import")?.addEventListener("click", async () => {
    const input = $("#lic-file");
    if (!input.files || !input.files[0]) { toast("Seleccione el archivo .lic.", true); return; }
    const text = await input.files[0].text();
    await licenseCall(() => Api.post("/api/system/license/import", { licenseFile: text }));
  });
  $("#lic-refresh")?.addEventListener("click", async () => {
    await licenseCall(() => Api.post("/api/system/license/refresh"));
  });
  $("#lic-deactivate")?.addEventListener("click", async () => {
    if (!confirm(`¿Desactivar la licencia ${s.licenseKey} en este equipo? El sistema volverá al período de prueba (si queda) o quedará sin licencia hasta activar otra vez.`)) return;
    await licenseCall(() => Api.post("/api/system/license/deactivate"));
  });
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
