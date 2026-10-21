// CLR TrueCentral VMS — panel: Sistema → Correo saliente.
//
// Servidor SMTP con que el sistema envía correos (hoy, las acciones de correo de
// las automatizaciones). Vive en Sistema porque es configuración del servidor,
// no de una automatización en particular.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

async function renderMailSettings() {
  $("#page-title").textContent = "Correo saliente";
  let settings;
  try { settings = await Api.get("/api/workflows/smtp"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  $("#view").innerHTML = `
    <div class="toolbar">
      <h3>Servidor de correo saliente <span class="muted" style="font-weight:normal;font-size:12px">(SMTP con que el servidor envía correos)</span></h3>
    </div>
    <div class="info-box" style="margin-bottom:12px">
      Lo usan las automatizaciones con la acción <b>Enviar correo</b>. Sin un servidor configurado y habilitado, esas acciones fallan.
    </div>
    <div class="card" style="max-width:760px">
      <div id="mail-error"></div>
      <form id="mail-form">
        <label class="checkbox-row"><input type="checkbox" id="smtp-enabled" ${settings.enabled ? "checked" : ""}> Habilitado (el sistema puede enviar correos)</label>
        <div class="form-grid">
          <div class="field">
            <label>Servidor SMTP</label>
            <input id="smtp-host" value="${esc(settings.host)}" placeholder="smtp.office365.com">
          </div>
          <div class="field">
            <label>Puerto</label>
            <input id="smtp-port" type="number" min="1" max="65535" value="${settings.port}">
          </div>
        </div>
        <div class="field">
          <label>Seguridad</label>
          <select id="smtp-security">
            <option value="StartTls" ${settings.security === "StartTls" ? "selected" : ""}>STARTTLS (normalmente puerto 587)</option>
            <option value="Ssl" ${settings.security === "Ssl" ? "selected" : ""}>TLS implícito (normalmente puerto 465)</option>
            <option value="None" ${settings.security === "None" ? "selected" : ""}>Sin cifrado (servidor interno, puerto 25)</option>
          </select>
        </div>
        <div class="form-grid">
          <div class="field">
            <label>Usuario (vacío = sin autenticación)</label>
            <input id="smtp-username" value="${esc(settings.username)}" autocomplete="username">
          </div>
          <div class="field">
            <label>Contraseña${settings.hasPassword ? " (guardada; vacío = no cambiar)" : ""}</label>
            <input id="smtp-password" type="password" autocomplete="new-password">
          </div>
        </div>
        <div class="form-grid">
          <div class="field">
            <label>Dirección del remitente</label>
            <input id="smtp-from" value="${esc(settings.fromAddress)}" placeholder="vms@empresa.cl">
          </div>
          <div class="field">
            <label>Nombre del remitente</label>
            <input id="smtp-fromname" value="${esc(settings.fromName)}">
          </div>
        </div>
        <label class="checkbox-row"><input type="checkbox" id="smtp-cert" ${settings.allowInvalidCertificate ? "checked" : ""}> Aceptar certificado no verificable (servidor de correo interno)</label>
        <div class="modal-actions">
          <button class="btn" type="submit">Guardar</button>
        </div>
      </form>
      <div class="field" style="margin-top:14px;border-top:1px solid var(--border);padding-top:14px">
        <label>Enviar un correo de prueba a</label>
        <div style="display:flex;gap:8px">
          <input id="smtp-test-to" placeholder="usted@empresa.cl" style="flex:1">
          <button class="btn ghost" type="button" id="smtp-test">Probar</button>
        </div>
        <div class="muted" style="font-size:11.5px;margin-top:4px">La prueba usa la configuración ya guardada.</div>
      </div>
    </div>`;

  const read = () => ({
    enabled: $("#smtp-enabled").checked,
    host: $("#smtp-host").value.trim(),
    port: Number($("#smtp-port").value) || 587,
    security: $("#smtp-security").value,
    username: $("#smtp-username").value.trim(),
    password: $("#smtp-password").value || null,
    allowInvalidCertificate: $("#smtp-cert").checked,
    fromAddress: $("#smtp-from").value.trim(),
    fromName: $("#smtp-fromname").value.trim(),
  });

  $("#mail-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    $("#mail-error").innerHTML = "";
    try {
      await Api.put("/api/workflows/smtp", read());
      if (typeof wfCatalogCache !== "undefined") wfCatalogCache = null;   // el aviso "sin correo" de Automatizaciones
      toast("Servidor de correo guardado.");
      renderMailSettings();
    } catch (err) {
      $("#mail-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });

  $("#smtp-test").addEventListener("click", async () => {
    const to = $("#smtp-test-to").value.trim();
    if (!to) { toast("Indique a qué dirección enviar la prueba.", true); return; }
    const button = $("#smtp-test");
    button.disabled = true;
    button.textContent = "Enviando…";
    $("#mail-error").innerHTML = "";
    try {
      await Api.post("/api/workflows/smtp/test", { to });
      $("#mail-error").innerHTML = `<div class="info-box">Correo de prueba enviado a ${esc(to)}.</div>`;
    } catch (err) {
      $("#mail-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    } finally {
      button.disabled = false;
      button.textContent = "Probar";
    }
  });
}
