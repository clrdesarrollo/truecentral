// CLR TrueCentral VMS — panel: Sistema → Respaldos.
//
// Respaldos cifrados de la base y los archivos del sistema: diario, antes de cada
// actualización y a pedido, con retención por días/semanas/meses y un segundo
// destino opcional. Restaurar NO se hace desde aquí: es por consola con el
// servicio detenido (se explica en la página).
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

const BACKUP_KIND_LABELS = { "diario": "Diario", "manual": "A pedido", "antes-de-actualizar": "Antes de actualizar" };

function backupSize(bytes) {
  if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GB`;
  if (bytes >= 1 << 20) return `${(bytes / (1 << 20)).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

async function renderBackups() {
  $("#page-title").textContent = "Respaldos";
  let d;
  try { d = await Api.get("/api/system/backups"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const s = d.settings;
  const st = d.state || {};

  $("#view").innerHTML = `
    <div class="${d.healthy ? "info-box" : "error-box"}" style="margin-bottom:12px">
      <b>${d.healthy ? "Respaldos al día." : "Atención:"}</b> ${esc(d.health)}
      ${st.lastSecondaryError && d.healthy ? `<div>${esc(st.lastSecondaryError)}</div>` : ""}
    </div>
    ${s.hasPassword ? "" : `
    <div class="warn-box" style="margin-bottom:12px">
      <b>Sin contraseña de respaldo.</b> Los respaldos se cifran con la llave de este equipo: sirven si se daña la base o falla
      una actualización, pero <b>no se pueden restaurar en otro equipo</b> (si se pierde este servidor, se pierden con él).
      Defina una contraseña de respaldo y guárdela fuera del sistema.
    </div>`}
    <div class="muted" style="font-size:12px;margin:-6px 0 12px">Carpeta: ${esc(d.directory)}</div>
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        <button class="btn ghost" id="bk-password">${s.hasPassword ? "Cambiar contraseña de respaldo…" : "Definir contraseña de respaldo…"}</button>
        <button class="btn" id="bk-run" ${d.busy ? "disabled" : ""}>${d.busy ? "Respaldando…" : "Respaldar ahora"}</button>
      </div>
    </div>
    ${d.files.length === 0 ? `<div class="info-box">Todavía no hay respaldos en esta carpeta.</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Fecha</th><th>Tipo</th><th>Tamaño</th><th>Versión</th><th>Cifrado</th>
        ${s.secondaryPath ? "<th>Segundo destino</th>" : ""}<th></th></tr></thead>
      <tbody>${d.files.map((f) => `
        <tr data-name="${esc(f.name)}">
          <td>${esc(formatDate(f.createdUtc))}</td>
          <td>${esc(BACKUP_KIND_LABELS[f.kind] ?? f.kind)}${f.includesAnpr ? ` <span class="muted">(con patentes)</span>` : ""}</td>
          <td>${backupSize(f.size)}</td>
          <td class="muted">${esc(f.serverVersion)}</td>
          <td>${f.keyMode === "password" ? `<span class="tag on">Contraseña</span>`
            : `<span class="tag operator" title="Solo se puede restaurar en este equipo">Llave del equipo</span>`}</td>
          ${s.secondaryPath ? `<td>${f.onSecondary === true ? `<span class="tag on">Copiado</span>`
            : f.onSecondary === false ? `<span class="tag off">Falta</span>` : `<span class="muted">—</span>`}</td>` : ""}
          <td class="row-actions">
            <button class="btn ghost bk-verify" title="Lo descifra, verifica cada archivo y restaura la base en una base de prueba que luego se borra">Probar</button>
            <button class="btn ghost bk-download">Descargar</button>
          </td>
        </tr>`).join("")}
      </tbody>
    </table></div>`}

    <div class="toolbar" style="margin-top:18px"><h3>Configuración</h3></div>
    <div class="card" style="max-width:820px">
      <div id="bk-error"></div>
      <form id="bk-form">
        <label class="checkbox-row"><input type="checkbox" id="bk-enabled" ${s.enabled ? "checked" : ""}> Respaldos automáticos activados</label>
        <div class="form-grid">
          <div class="field">
            <label>Hora del respaldo diario</label>
            <input id="bk-time" type="time" value="${esc(s.dailyTime)}">
          </div>
          <div class="field">
            <label>&nbsp;</label>
            <label class="checkbox-row"><input type="checkbox" id="bk-anpr" ${s.includeAnpr ? "checked" : ""}> Incluir las fotos de patentes (pueden pesar varios GB)</label>
          </div>
        </div>
        <div class="field"><label>Cuántos se conservan</label></div>
        <div class="form-grid" style="grid-template-columns:repeat(4,minmax(0,1fr))">
          <div class="field"><label>Diarios</label><input id="bk-daily" type="number" min="1" max="60" value="${s.keepDaily}"></div>
          <div class="field"><label>Semanales</label><input id="bk-weekly" type="number" min="0" max="52" value="${s.keepWeekly}"></div>
          <div class="field"><label>Mensuales</label><input id="bk-monthly" type="number" min="0" max="120" value="${s.keepMonthly}"></div>
          <div class="field"><label>Antes de actualizar</label><input id="bk-pre" type="number" min="1" max="50" value="${s.keepPreUpdate}"></div>
        </div>
        <div class="field" style="margin-top:6px">
          <label>Segundo destino (recomendado: otra unidad o una carpeta de red)</label>
          <input id="bk-secondary" value="${esc(s.secondaryPath ?? "")}" placeholder="D:\\Respaldos  o  \\\\servidor\\recurso\\carpeta">
          <div class="muted" style="font-size:11.5px;margin-top:4px">Cada respaldo se copia también aquí, con la misma retención. Un respaldo
            que solo está en el disco del servidor se pierde junto con él.</div>
        </div>
        <div class="form-grid">
          <div class="field">
            <label>Usuario del recurso de red (vacío = la cuenta del servicio)</label>
            <input id="bk-suser" value="${esc(s.secondaryUser ?? "")}" placeholder="DOMINIO\\usuario" autocomplete="off">
          </div>
          <div class="field">
            <label>Contraseña del recurso${s.hasSecondaryPassword ? " (guardada; vacío = no cambiar)" : ""}</label>
            <input id="bk-spass" type="password" autocomplete="new-password">
          </div>
        </div>
        <div class="modal-actions">
          <button class="btn ghost" type="button" id="bk-test-secondary" ${s.secondaryPath ? "" : "disabled"}>Probar segundo destino</button>
          <button class="btn" type="submit">Guardar</button>
        </div>
      </form>
    </div>

    <div class="toolbar" style="margin-top:18px"><h3>Cómo restaurar</h3></div>
    <div class="info-box">
      Por seguridad, restaurar no se hace desde la web. En el servidor, con el servicio detenido, ejecute como administrador:
      <pre style="margin:8px 0;white-space:pre-wrap">sc stop CLRTrueCentralVMS
"C:\\Program Files\\CLR TrueCentral VMS\\Server\\TrueCentralVms.Server.exe" --restore "ruta\\del\\respaldo.tcbak"
sc start CLRTrueCentralVMS</pre>
      Pide la contraseña de respaldo, verifica el archivo completo antes de tocar nada y guarda lo actual con el sufijo «antes»
      para poder volver. Al arrancar se aplican las migraciones que falten y la restauración queda en la bitácora.
    </div>`;

  $("#bk-run").addEventListener("click", async (e) => {
    e.target.disabled = true;
    e.target.textContent = "Respaldando…";
    try {
      const r = await Api.post("/api/system/backups/run");
      toast(r.secondaryError ? `Respaldo creado, pero no se copió al segundo destino: ${r.secondaryError}` : "Respaldo creado.", !!r.secondaryError);
    } catch (err) { toast(err.error ?? "No se pudo respaldar.", true); }
    renderBackups();
  });
  $("#bk-password").addEventListener("click", () => backupPasswordModal(s.hasPassword));

  $$("#view .bk-verify").forEach((b) => b.addEventListener("click", async (e) => {
    const name = e.target.closest("tr").dataset.name;
    e.target.disabled = true;
    e.target.textContent = "Probando…";
    try {
      const r = await Api.post(`/api/system/backups/${encodeURIComponent(name)}/verify`);
      openModal(`
        <h3>${r.ok ? "Respaldo correcto" : "El respaldo NO pasó la prueba"}</h3>
        <div class="${r.ok ? "info-box" : "error-box"}">${esc(r.message)}</div>
        <div class="modal-actions"><button class="btn" type="button" id="bk-close">Cerrar</button></div>`);
      $("#bk-close").addEventListener("click", closeModal);
    } catch (err) { toast(err.error ?? "No se pudo probar.", true); }
    e.target.disabled = false;
    e.target.textContent = "Probar";
  }));
  $$("#view .bk-download").forEach((b) => b.addEventListener("click", (e) =>
    backupDownloadModal(e.target.closest("tr").dataset.name)));

  const read = () => ({
    enabled: $("#bk-enabled").checked,
    dailyTime: $("#bk-time").value,
    keepDaily: Number($("#bk-daily").value),
    keepWeekly: Number($("#bk-weekly").value),
    keepMonthly: Number($("#bk-monthly").value),
    keepPreUpdate: Number($("#bk-pre").value),
    includeAnpr: $("#bk-anpr").checked,
    secondaryPath: $("#bk-secondary").value.trim() || null,
    secondaryUser: $("#bk-suser").value.trim() || null,
    secondaryPassword: $("#bk-spass").value || null,
  });
  $("#bk-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    $("#bk-error").innerHTML = "";
    const body = read();
    if (!body.enabled && !confirm("¿Desactivar los respaldos automáticos? El sistema quedará sin respaldos diarios.")) return;
    try {
      await Api.put("/api/system/backups/settings", body);
      toast("Configuración de respaldos guardada.");
      renderBackups();
    } catch (err) {
      $("#bk-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
  $("#bk-test-secondary").addEventListener("click", async (e) => {
    e.target.disabled = true;
    try {
      const r = await Api.post("/api/system/backups/secondary/test");
      $("#bk-error").innerHTML = `<div class="${r.ok ? "info-box" : "error-box"}">${esc(r.message)}</div>`;
    } catch (err) { toast(err.error, true); }
    e.target.disabled = false;
  });
}

function backupPasswordModal(hasPassword) {
  openModal(`
    <h3>${hasPassword ? "Cambiar" : "Definir"} la contraseña de respaldo</h3>
    <div class="info-box">
      Con esta contraseña se cifran los respaldos y <b>solo con ella se pueden restaurar</b>. Guárdela fuera del sistema
      (por ejemplo, en la caja fuerte o en un gestor de contraseñas): si se pierde, los respaldos no se pueden abrir.
      ${hasPassword ? "Los respaldos anteriores siguen abriéndose con la contraseña anterior." : ""}
    </div>
    <div id="bkp-error"></div>
    <form id="bkp-form">
      <div class="field"><label>Contraseña de respaldo (mínimo 12 caracteres; mejor una frase)</label>
        <input id="bkp-pass" type="password" autocomplete="new-password" minlength="12" required></div>
      <div class="field"><label>Repítala</label>
        <input id="bkp-confirm" type="password" autocomplete="new-password" required></div>
      <div class="field"><label>Su propia contraseña (la de su cuenta), para confirmar</label>
        <input id="bkp-account" type="password" autocomplete="current-password" required></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="bkp-cancel">Cancelar</button>
        <button class="btn" type="submit">Guardar</button>
      </div>
    </form>`);
  $("#bkp-cancel").addEventListener("click", closeModal);
  $("#bkp-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    try {
      await Api.put("/api/system/backups/password", {
        password: $("#bkp-pass").value, confirm: $("#bkp-confirm").value, accountPassword: $("#bkp-account").value,
      });
      closeModal();
      toast("Contraseña de respaldo guardada. Los próximos respaldos se cifran con ella.");
      renderBackups();
    } catch (err) {
      $("#bkp-error").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    }
  });
}

function backupDownloadModal(name) {
  openModal(`
    <h3>Descargar el respaldo</h3>
    <div class="info-box">Un respaldo contiene <b>toda la información del sistema</b> (cifrada). Guárdelo en un lugar seguro.
      <div class="muted" style="margin-top:4px">${esc(name)}</div></div>
    <div id="bkd-error"></div>
    <form id="bkd-form">
      <div class="field"><label>Su contraseña (la de su cuenta), para confirmar</label>
        <input id="bkd-account" type="password" autocomplete="current-password" required></div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="bkd-cancel">Cancelar</button>
        <button class="btn" type="submit">Descargar</button>
      </div>
    </form>`);
  $("#bkd-cancel").addEventListener("click", closeModal);
  $("#bkd-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = e.target.querySelector("button[type=submit]");
    button.disabled = true;
    button.textContent = "Descargando…";
    try {
      // Con fetch y la cabecera (no con el token en la URL): el archivo llega como blob y se guarda desde aquí.
      const res = await fetch(`/api/system/backups/${encodeURIComponent(name)}/download`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${Api.token}` },
        body: JSON.stringify({ accountPassword: $("#bkd-account").value }),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => null);
        throw new Error(data?.error || `Error ${res.status}`);
      }
      const blob = await res.blob();
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = name;
      document.body.appendChild(a);
      a.click();
      setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
      closeModal();
      toast("Respaldo descargado.");
    } catch (err) {
      $("#bkd-error").innerHTML = `<div class="error-box">${esc(err.message)}</div>`;
      button.disabled = false;
      button.textContent = "Descargar";
    }
  });
}
