// CLR TrueCentral VMS — panel: Sistema → Integraciones.
//
// Aplicaciones integradas: sistemas externos (el kiosco del casino, un sistema
// de remuneraciones) que leen el padrón por la API de integración con su llave y
// su secreto, sin ser usuarios del VMS. El secreto se muestra una sola vez, al
// crearla o al cambiarlo. Cada aplicación habilitada consume un cupo de licencia.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

async function renderIntegrations() {
  $("#page-title").textContent = "Integraciones";
  let page;
  try { page = await Api.get("/api/integrations"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const apps = page.apps;
  const scopeLabel = (key) => page.scopes.find((s) => s.key === key)?.label ?? key;

  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        <button class="btn" id="btn-int-new">Agregar aplicación</button>
      </div>
    </div>
    <div class="info-box" style="margin-bottom:12px">
      Una <b>aplicación integrada</b> es un sistema externo que lee el padrón de personas por la
      <b>API de integración</b> (<code>/api/integration/v1</code>), con su llave y su secreto y sin ser un usuario del sistema:
      por ejemplo el kiosco del casino, que necesita la empresa y la colación de cada persona.
      ${page.quota == null ? "" : `<div class="muted" style="margin-top:4px">Licencia: ${page.inUse} de ${page.quota}
        aplicaciones habilitadas.</div>`}
    </div>
    ${apps.length === 0 ? `<div class="muted" style="margin:12px 0">Todavía no hay aplicaciones integradas.</div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr><th>Aplicación</th><th>Llave</th><th>Permisos</th><th>Desde</th><th>Vence</th><th>Último uso</th><th>Estado</th><th></th></tr></thead>
        <tbody>${apps.map((a) => `
          <tr data-id="${a.id}">
            <td><b>${esc(a.name)}</b>${a.description ? `<div class="muted" style="font-size:12px">${esc(a.description)}</div>` : ""}</td>
            <td><code>${esc(a.appKey)}</code></td>
            <td>${a.scopes.map((s) => `<span class="tag operator">${esc(scopeLabel(s))}</span>`).join(" ")}</td>
            <td class="muted">${a.allowedIps.length ? a.allowedIps.map(esc).join("<br>") : "Cualquier dirección"}</td>
            <td class="muted">${a.expiresAt ? esc(new Date(a.expiresAt).toLocaleDateString("es-CL")) : "No vence"}</td>
            <td class="muted">${a.lastUsedAt ? `${esc(new Date(a.lastUsedAt).toLocaleString("es-CL"))}<div>${esc(a.lastUsedIp ?? "")}</div>` : "Nunca"}</td>
            <td>${integrationStatusTag(a)}</td>
            <td><div class="row-actions">
              <button class="btn ghost small int-edit">Editar</button>
              <button class="btn ghost small int-rotate" title="Genera un secreto nuevo: el anterior deja de valer al instante">Cambiar secreto</button>
              <button class="btn danger small int-delete">Eliminar</button>
            </div></td>
          </tr>`).join("")}</tbody>
      </table></div>`}`;

  const byRow = (e) => apps.find((a) => a.id === Number(e.target.closest("tr").dataset.id));
  $("#btn-int-new").addEventListener("click", () => integrationAppModal(null, page));
  $$("#view .int-edit").forEach((b) => b.addEventListener("click", (e) => integrationAppModal(byRow(e), page)));
  $$("#view .int-rotate").forEach((b) => b.addEventListener("click", async (e) => {
    const a = byRow(e);
    if (!confirm(`¿Cambiar el secreto de «${a.name}»? El actual deja de valer al instante: hay que cargar el nuevo en el sistema externo.`)) return;
    try {
      const result = await Api.post(`/api/integrations/${a.id}/rotate-secret`);
      integrationSecretModal(result, page.server, false);
    } catch (err) { toast(err.error, true); }
  }));
  $$("#view .int-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const a = byRow(e);
    if (!confirm(`¿Eliminar la aplicación «${a.name}»? Su llave deja de valer y el sistema externo ya no podrá leer el padrón.`)) return;
    try {
      await Api.delete(`/api/integrations/${a.id}`);
      toast("Aplicación eliminada.");
      renderIntegrations();
    } catch (err) { toast(err.error, true); }
  }));
}

function integrationStatusTag(a) {
  if (a.disabledByLicense) return `<span class="tag warn" title="La licencia no alcanza para todas las aplicaciones habilitadas">Sin cupo de licencia</span>`;
  if (!a.enabled) return `<span class="tag off">Deshabilitada</span>`;
  if (a.expiresAt && new Date(a.expiresAt) <= new Date()) return `<span class="tag off">Vencida</span>`;
  return `<span class="tag on">Habilitada</span>`;
}

/** Fecha de vencimiento del formulario ("aaaa-mm-dd") → fin de ese día en la hora local, en ISO. */
function integrationExpiryIso(value) {
  if (!value) return null;
  const [y, m, d] = value.split("-").map(Number);
  return new Date(y, m - 1, d, 23, 59, 59).toISOString();
}

/** Alta o edición de una aplicación integrada. */
function integrationAppModal(app, page) {
  const isNew = !app;
  const scopes = new Set(app?.scopes ?? ["persons.read"]);
  const expires = app?.expiresAt ? new Date(app.expiresAt) : null;
  const pad = (n) => String(n).padStart(2, "0");
  const expiresValue = expires ? `${expires.getFullYear()}-${pad(expires.getMonth() + 1)}-${pad(expires.getDate())}` : "";
  openModal(`
    <h3>${isNew ? "Agregar aplicación integrada" : `Editar «${esc(app.name)}»`}</h3>
    <div id="int-error"></div>
    <form id="int-form">
      <div class="form-grid">
        <div class="field">
          <label>Nombre</label>
          <input id="int-name" required maxlength="64" value="${esc(app?.name ?? "")}" placeholder="Kiosco del casino">
        </div>
        <div class="field">
          <label>Descripción (opcional)</label>
          <input id="int-desc" maxlength="256" value="${esc(app?.description ?? "")}" placeholder="Para qué la usa y quién la administra">
        </div>
      </div>
      <div class="field">
        <label>Permisos</label>
        ${page.scopes.map((s) => `
          <label class="checkbox-row"><input type="checkbox" class="int-scope" value="${esc(s.key)}" ${scopes.has(s.key) ? "checked" : ""}>
            <span><b>${esc(s.label)}</b> <span class="muted">— ${esc(s.description)}</span></span></label>`).join("")}
      </div>
      <div class="form-grid">
        <div class="field">
          <label>Direcciones permitidas (opcional)</label>
          <textarea id="int-ips" rows="3" spellcheck="false" placeholder="192.168.1.50&#10;10.0.0.0/24">${esc((app?.allowedIps ?? []).join("\n"))}</textarea>
          <div class="muted field-hint">Una IP o red por línea. Vacío: desde cualquier dirección. Recomendado: la IP del sistema externo.</div>
        </div>
        <div class="field">
          <label>Vence (opcional)</label>
          <input id="int-expires" type="date" value="${esc(expiresValue)}">
          <div class="muted field-hint">Desde ese día se rechaza. Vacío: no vence.</div>
        </div>
      </div>
      <label class="checkbox-row"><input type="checkbox" id="int-enabled" ${(app?.enabled ?? true) ? "checked" : ""}> Habilitada</label>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="int-cancel">Cancelar</button>
        <button class="btn" type="submit" id="int-save">${isNew ? "Agregar" : "Guardar cambios"}</button>
      </div>
    </form>`, "wider");

  $("#int-cancel").addEventListener("click", closeModal);
  $("#int-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const body = {
      name: $("#int-name").value.trim(),
      description: $("#int-desc").value.trim() || null,
      scopes: $$("#modal .int-scope").filter((c) => c.checked).map((c) => c.value),
      allowedIps: $("#int-ips").value.split("\n").map((s) => s.trim()).filter((s) => s),
      expiresAt: integrationExpiryIso($("#int-expires").value),
      enabled: $("#int-enabled").checked,
    };
    const save = $("#int-save");
    save.disabled = true;
    try {
      if (isNew) {
        const result = await Api.post("/api/integrations", body);
        integrationSecretModal(result, page.server, true);
      } else {
        await Api.put(`/api/integrations/${app.id}`, body);
        closeModal();
        toast("Aplicación actualizada.");
        renderIntegrations();
      }
    } catch (err) {
      $("#int-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
      save.disabled = false;
    }
  });
}

/** Llave y secreto recién emitidos: lo único que hay que cargar en el sistema externo. Se muestran una sola vez. */
function integrationSecretModal(result, server, created) {
  const host = location.hostname;
  const url = server.httpsPort === 443 ? `https://${host}` : `https://${host}:${server.httpsPort}`;
  const rows = [
    ["Servidor", url],
    ["Llave (AppKey)", result.app.appKey],
    ["Secreto (AppSecret)", result.appSecret],
    ["Huella del certificado (SPKI)", server.certificateSpki ?? "—"],
  ];
  openModal(`
    <h3>${created ? "Aplicación creada" : "Secreto nuevo"}: ${esc(result.app.name)}</h3>
    <div class="warn-box" style="margin-bottom:12px"><b>Copie el secreto ahora:</b> no se vuelve a mostrar. Si se pierde,
      use <i>Cambiar secreto</i> y cargue el nuevo en el sistema externo.</div>
    <table class="grid">
      <tbody>${rows.map(([label, value], i) => `
        <tr><td style="white-space:nowrap"><b>${esc(label)}</b></td>
          <td><code style="word-break:break-all">${esc(value)}</code></td>
          <td><button class="btn ghost small int-copy" data-i="${i}">Copiar</button></td></tr>`).join("")}</tbody>
    </table>
    <div class="muted" style="font-size:12px;margin-top:8px">La huella sirve para que el sistema externo verifique que habla con
      este servidor. Si se carga otro certificado HTTPS (Sistema → Red → Certificado HTTPS), la huella cambia y hay que
      actualizarla allá.</div>
    <div class="modal-actions">
      <button class="btn ghost" type="button" id="int-copy-all">Copiar todo</button>
      <button class="btn" type="button" id="int-done">Listo</button>
    </div>`, "wider");

  const copy = async (text, what) => {
    try { await navigator.clipboard.writeText(text); toast(`${what} copiado.`); }
    catch { toast("No se pudo copiar: selecciónelo y cópielo a mano.", true); }
  };
  $$("#modal .int-copy").forEach((b) => b.addEventListener("click", () => {
    const [label, value] = rows[Number(b.dataset.i)];
    copy(value, label);
  }));
  $("#int-copy-all").addEventListener("click", () => copy(rows.map(([l, v]) => `${l}: ${v}`).join("\n"), "Todo"));
  $("#int-done").addEventListener("click", () => { closeModal(); renderIntegrations(); });
}
