// CLR TrueCentral VMS — panel: Sistema → Respaldos.
//
// Respaldos cifrados de la base y los archivos del sistema: diario, antes de cada
// actualización y a pedido, con retención por días/semanas/meses y un segundo
// destino opcional. Restaurar NO se hace desde aquí: es por consola con el
// servicio detenido (se explica en la página).
//
// El respaldo en curso (a pedido, el diario o el de antes de actualizar) se sigue
// con /api/system/backups/progress: paso, barra, bytes, tiempos y un aviso si deja
// de avanzar. Vive en el servidor, así que recargar la página no lo pierde.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

const BACKUP_KIND_LABELS = { "diario": "Diario", "manual": "A pedido", "antes-de-actualizar": "Antes de actualizar" };

// Etapas del respaldo (BackupProgress en el servidor) y el paso en que van: 1 escribir, 2 verificar, 3 segundo destino.
const BACKUP_STAGES = {
  prepare: { step: 1, label: "Preparando el respaldo" },
  dump: { step: 1, label: "Copiando la base de datos" },
  files: { step: 1, label: "Agregando la llave de datos y los archivos del sistema" },
  verify: { step: 2, label: "Verificando: se relee el respaldo completo" },
  retention: { step: 2, label: "Borrando los respaldos que ya no se conservan" },
  secondary: { step: 3, label: "Copiando al segundo destino" },
};

let backupsTimer = null;   // sondeo del avance: se corta solo al dejar la página

function backupSize(bytes) {
  if (!bytes) return "0 MB";
  if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GB`;
  if (bytes >= 1 << 20) return `${(bytes / (1 << 20)).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function backupDuration(seconds) {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min ${String(s % 60).padStart(2, "0")} s`;
  return `${Math.floor(s / 3600)} h ${String(Math.floor(s % 3600 / 60)).padStart(2, "0")} min`;
}

function backupTime(iso) {
  return new Date(iso).toLocaleTimeString("es-CL", { hour: "2-digit", minute: "2-digit" });
}

async function renderBackups() {
  $("#page-title").textContent = "Respaldos";
  let d;
  try { d = await Api.get("/api/system/backups"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const s = d.settings;

  $("#view").innerHTML = `
    <div id="bk-top"></div>
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        <button class="btn ghost" id="bk-password">${s.hasPassword ? "Cambiar contraseña de respaldo…" : "Definir contraseña de respaldo…"}</button>
        <button class="btn" id="bk-run">Respaldar ahora</button>
      </div>
    </div>
    <div id="bk-list"></div>

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
  backupsShow(d);

  // Vuelve enseguida: el respaldo sigue en el servidor y su avance se ve en el cuadro de arriba.
  $("#bk-run").addEventListener("click", async (e) => {
    const button = e.currentTarget;
    button.disabled = true;
    button.textContent = "Respaldando…";
    try {
      const p = await Api.post("/api/system/backups/run");
      backupShowProgress(p);
      backupsWatch(true);
    } catch (err) {
      toast(err.error ?? "No se pudo respaldar.", true);
      backupsRefresh();
    }
  });
  $("#bk-password").addEventListener("click", () => backupPasswordModal(s.hasPassword));

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
  backupsWatch(!!d.progress?.busy);
}

// Lo que cambia con un respaldo (salud, avance, lista y el botón), sin tocar la configuración: al terminar uno se
// vuelve a dibujar solo esto, y no se pierde lo que se esté escribiendo más abajo.
function backupsShow(d) {
  const s = d.settings;
  const st = d.state || {};
  $("#bk-top").innerHTML = `
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
    <div id="bk-run-box"></div>
    <div class="muted" style="font-size:12px;margin:-6px 0 12px">Carpeta: ${esc(d.directory)}</div>`;
  $("#bk-list").innerHTML = d.files.length === 0 ? `<div class="info-box">Todavía no hay respaldos en esta carpeta.</div>` : `
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
    </table></div>`;

  $$("#bk-list .bk-verify").forEach((b) => b.addEventListener("click", async (e) => {
    const button = e.currentTarget;
    const name = button.closest("tr").dataset.name;
    button.disabled = true;
    button.textContent = "Probando…";
    backupsWatch(true);   // el botón de respaldar se apaga y el cuadro avisa la prueba en curso
    try {
      const r = await Api.post(`/api/system/backups/${encodeURIComponent(name)}/verify`);
      openModal(`
        <h3>${r.ok ? "Respaldo correcto" : "El respaldo NO pasó la prueba"}</h3>
        <div class="${r.ok ? "info-box" : "error-box"}">${esc(r.message)}</div>
        <div class="modal-actions"><button class="btn" type="button" id="bk-close">Cerrar</button></div>`);
      $("#bk-close").addEventListener("click", closeModal);
    } catch (err) { toast(err.error ?? "No se pudo probar.", true); }
    button.disabled = false;
    button.textContent = "Probar";
  }));
  $$("#bk-list .bk-download").forEach((b) => b.addEventListener("click", (e) =>
    backupDownloadModal(e.currentTarget.closest("tr").dataset.name)));

  backupShowProgress(d.progress);
}

async function backupsRefresh() {
  let d;
  try { d = await Api.get("/api/system/backups"); } catch { backupsWatch(true); return; }
  if (!$("#bk-top")) return;
  backupsShow(d);
  backupsWatch(!!d.progress?.busy);
}

// Sondeo del avance: cada 2 s mientras hay un respaldo o una prueba en curso, y cada 15 s si no (así también aparece el
// diario que arranca con la página abierta). Al terminar, vuelve a pedir la lista y la salud.
function backupsWatch(busy) {
  // Solo mientras se está en la página y con sesión (al volver a ingresar, la página se dibuja y lo arranca de nuevo).
  const here = () => location.hash === "#/backups" && !!$("#bk-run-box") && !$("#app-shell").classList.contains("hidden");
  clearTimeout(backupsTimer);
  backupsTimer = setTimeout(async () => {
    if (!here()) return;
    let p = null;
    try { p = await Api.get("/api/system/backups/progress"); } catch { /* sin conexión por un momento: se reintenta */ }
    if (!here()) return;
    if (!p) { backupsWatch(busy); return; }
    if (busy && !p.busy) {
      // Solo se avisa el final del respaldo que se estaba mostrando (no el de antes, al terminar una prueba).
      const watched = $("#bk-run-box .bk-run")?.dataset.started;
      const r = p.run;
      if (watched && r?.startedUtc === watched) {
        if (r.success) toast(r.secondaryError ? `Respaldo creado, pero no se copió al segundo destino: ${r.secondaryError}` : "Respaldo creado.", !!r.secondaryError);
        else toast(`El respaldo falló: ${r.error}`, true);
      }
      backupsRefresh();
      return;
    }
    backupShowProgress(p);
    backupsWatch(p.busy);
  }, busy ? 2000 : 15000);
}

// Cuadro del respaldo en curso (paso, barra, bytes, tiempos y aviso si no avanza) o del último, si terminó hace poco.
// El de en curso se arma una vez y después solo se actualizan sus textos y la barra (así la barra se desliza).
function backupShowProgress(p) {
  const box = $("#bk-run-box");
  if (!box) return;
  const button = $("#bk-run");
  if (button) {
    button.disabled = !!p?.busy;
    button.textContent = p?.run?.running ? "Respaldando…" : "Respaldar ahora";
  }
  const r = p?.run;
  const testing = p?.testing
    ? `<div class="info-box">Probando el respaldo ${esc(p.testing)}: se descifra, se verifica y se restaura en una base de prueba…</div>`
    : "";
  if (!r?.running) {
    box.innerHTML = (r ? backupRunResultHtml(r) : "") + testing;
    return;
  }

  let card = box.querySelector(".bk-run");
  if (!card || card.dataset.started !== r.startedUtc) {
    box.innerHTML = `
      <div class="card bk-run" data-started="${esc(r.startedUtc)}">
        <div class="bk-run-head"><b class="bk-run-title"></b><span class="muted bk-run-when"></span></div>
        <div class="bk-run-stage"></div>
        <div class="bk-bar"><span></span></div>
        <div class="muted bk-run-meta"></div>
        <div class="warn-box bk-run-stall hidden"></div>
      </div>`;
    card = box.querySelector(".bk-run");
  }
  const kind = (BACKUP_KIND_LABELS[r.kind] ?? r.kind).toLowerCase();
  const stage = BACKUP_STAGES[r.stage] ?? BACKUP_STAGES.prepare;
  card.querySelector(".bk-run-title").textContent = `Respaldo ${kind} en curso`;
  card.querySelector(".bk-run-when").textContent = [r.startedBy ? `pedido por ${r.startedBy}` : "",
    `empezó a las ${backupTime(r.startedUtc)}`, `lleva ${backupDuration(r.elapsedSeconds)}`].filter(Boolean).join(" · ");
  card.querySelector(".bk-run-stage").textContent = `Paso ${stage.step} de ${r.hasSecondary ? 3 : 2} · ${stage.label}`;

  // La base y los archivos se comparan con el respaldo anterior (una estimación); la verificación y la copia, con el
  // tamaño exacto del archivo.
  let percent = null;
  let detail = "";
  if (r.stage === "dump" || r.stage === "files") {
    if (r.expectedBytes > 0 && r.bytes <= r.expectedBytes) {
      percent = Math.min(99, Math.floor(r.bytes * 100 / r.expectedBytes));
      detail = `${backupSize(r.bytes)} de unos ${backupSize(r.expectedBytes)} (según el respaldo anterior)`;
    } else if (r.expectedBytes > 0) {
      detail = `${backupSize(r.bytes)} escritos: ya pesa más que el respaldo anterior (${backupSize(r.expectedBytes)})`;
    } else {
      detail = `${backupSize(r.bytes)} escritos (no hay un respaldo anterior con qué comparar)`;
    }
  } else if ((r.stage === "verify" || r.stage === "secondary") && r.expectedBytes > 0) {
    percent = Math.min(100, Math.floor(r.bytes * 100 / r.expectedBytes));
    detail = `${backupSize(r.bytes)} de ${backupSize(r.expectedBytes)}`;
  } else if (r.stage === "retention") {
    percent = 100;
  }
  const parts = [detail];
  if (percent !== null && r.stage !== "retention") parts.push(`${percent} %`);
  // Lo que queda de este paso, al ritmo que lleva (en "files" los bytes siguen sumando sobre la base: no se estima).
  if (percent !== null && percent >= 3 && percent < 99 && r.stage !== "files" && r.stageSeconds >= 15 && r.bytes > 0) {
    const left = r.stageSeconds * (r.expectedBytes - r.bytes) / r.bytes;
    parts.push(left < 60 ? "queda menos de un minuto en este paso" : `quedan unos ${Math.ceil(left / 60)} min en este paso`);
  }
  card.querySelector(".bk-run-meta").textContent = parts.filter(Boolean).join(" · ");

  const bar = card.querySelector(".bk-bar");
  bar.classList.toggle("indeterminate", percent === null);
  bar.firstElementChild.style.width = percent === null ? "" : `${percent}%`;
  bar.setAttribute("role", "progressbar");
  bar.setAttribute("aria-label", stage.label);
  if (percent === null) bar.removeAttribute("aria-valuenow");
  else bar.setAttribute("aria-valuenow", String(percent));

  const stall = card.querySelector(".bk-run-stall");
  const stalled = r.idleSeconds >= (p.stallAfterSeconds || 120);
  stall.classList.toggle("hidden", !stalled);
  if (stalled) {
    stall.textContent = `Lleva ${backupDuration(r.idleSeconds)} sin avanzar. Puede ser el disco lleno o muy lento, la base ` +
      `de datos ocupada por otra tarea o una carpeta de red que no responde. El servidor sigue atendiendo normalmente; si ` +
      `el respaldo no se mueve, se detiene solo al cumplir ${p.timeoutMinutes || 180} min.`;
  }
  if (testing) box.querySelector(".bk-run").insertAdjacentHTML("afterend", testing);
}

function backupRunResultHtml(r) {
  const kind = (BACKUP_KIND_LABELS[r.kind] ?? r.kind).toLowerCase();
  const who = r.startedBy ? `, pedido por ${esc(r.startedBy)}` : "";
  if (r.success) {
    return `<div class="info-box bk-run-done">
      <b>Respaldo ${esc(kind)} terminado</b> a las ${esc(backupTime(r.finishedUtc))}${who}, en ${esc(backupDuration(r.elapsedSeconds))}:
      ${esc(r.file?.name ?? "")} (${backupSize(r.file?.size ?? 0)}).
      ${r.secondaryError ? `<div class="bk-run-warn">No se copió al segundo destino: ${esc(r.secondaryError)}</div>` : ""}
    </div>`;
  }
  return `<div class="error-box bk-run-done">
    <b>El respaldo ${esc(kind)} falló</b> a las ${esc(backupTime(r.finishedUtc))}${who}, después de ${esc(backupDuration(r.elapsedSeconds))}:
    ${esc(r.error ?? "error desconocido")}
  </div>`;
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
