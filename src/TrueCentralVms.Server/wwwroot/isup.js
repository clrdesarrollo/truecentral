// CLR TrueCentral VMS — panel: conexión ISUP.
//
// Equipos Hikvision que no se alcanzan por IP (detrás de NAT, 4G o MPLS) se
// conectan ellos al servidor por ISUP con su ID y su clave. Aquí vive lo común
// a todos los formularios de equipos (el bloque ID + clave y qué escribir en el
// equipo) y la página Sistema → Conexión ISUP.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

let isupConnectionCache = null;   // { at, data }

/** Dirección y puerto que hay que escribir en el equipo (cacheado 1 minuto). */
async function getIsupConnection() {
  if (isupConnectionCache && Date.now() - isupConnectionCache.at < 60000) return isupConnectionCache.data;
  let data = null;
  try { data = await Api.get("/api/isup/connection"); } catch { /* sin datos: la ayuda queda genérica */ }
  isupConnectionCache = { at: Date.now(), data };
  return data;
}

/** Cómo se muestra dónde está un equipo ISUP (no tiene IP: lo identifica su ID). */
function isupAddressHtml(isupDeviceId) {
  return `<span title="El equipo se conecta al servidor por ISUP con este ID; el sistema no usa su IP.">ISUP ${esc(isupDeviceId)}</span>`;
}

/**
 * Bloque de formulario ID + clave ISUP. `prefix` distingue los campos, `current`
 * es el equipo en edición (isupDeviceId, hasIsupKey) y `noun` cómo se llama el
 * equipo en los textos ("panel", "terminal", "cámara"...).
 */
function isupFieldsHtml(prefix, current, isNew, noun) {
  const keyLabel = isNew || !current?.hasIsupKey ? "Clave ISUP" : "Clave ISUP (vacío = no cambiar)";
  return `
    <div id="${prefix}-isup-box">
      <div class="form-grid">
        <div class="field">
          <label>ID ISUP del ${esc(noun)}</label>
          <input id="${prefix}-isup-id" maxlength="32" value="${esc(current?.isupDeviceId ?? "")}" placeholder="Ej.: 1001, PANEL-BODEGA"
                 title="El «ID del dispositivo» que tiene configurado en su plataforma ISUP: letras, números, guion y guion bajo.">
        </div>
        <div class="field">
          <label>${keyLabel}</label>
          <input id="${prefix}-isup-key" type="password" autocomplete="new-password" maxlength="32"
                 placeholder="La «clave de cifrado» del equipo"
                 title="Hasta 32 caracteres, sin espacios. Los equipos ISUP 2.0/4.0 no usan clave: déjela vacía.">
        </div>
      </div>
      <div class="info-box" id="${prefix}-isup-help">
        El ${esc(noun)} se conecta al servidor por ISUP: no hace falta su IP ni que el servidor lo alcance.
        Configure en el equipo la plataforma ISUP (en Hikvision: <b>Red → Acceso a plataforma</b> o <b>Comunicación → ISUP</b>).
      </div>
    </div>`;
}

/** Completa la ayuda del bloque ISUP con la dirección y el puerto reales del servidor. */
async function wireIsupFields(prefix, noun) {
  $(`#${prefix}-isup-id`)?.addEventListener("input", (e) => {
    const el = e.target;
    const clean = el.value.replace(/[^A-Za-z0-9_-]/g, "");
    if (clean !== el.value) el.value = clean;
  });
  const c = await getIsupConnection();
  const help = $(`#${prefix}-isup-help`);
  if (!help || !c) return;
  if (!c.enabled) {
    help.innerHTML = `<b>El servicio ISUP está deshabilitado en este servidor</b> (Isup:Enabled): el ${esc(noun)} no podrá conectarse.`;
    help.className = "error-box";
    return;
  }
  help.innerHTML = `
    El ${esc(noun)} se conecta al servidor por ISUP: no hace falta su IP ni que el servidor lo alcance. En el equipo
    (Hikvision: <b>Red → Acceso a plataforma</b> o <b>Comunicación → ISUP</b>) configure:
    <div class="probe-grid" style="margin-top:6px">
      <span>Protocolo</span><b>ISUP 5.0</b>
      <span>Dirección del servidor</span><b>${esc(c.address ?? (c.addresses?.length > 1
        ? `la IP de este servidor en la red del ${noun}: ${c.addresses.join(" o ")}` : "(la IP de este servidor)"))}</b>
      <span>Puerto</span><b>${c.port}</b>
      <span>ID del dispositivo</span><b>el mismo ID de arriba</b>
      <span>Clave de cifrado</span><b>la misma clave de arriba (ISUP 2.0/4.0: sin clave)</b>
    </div>
    ${c.running ? "" : `<div class="muted" style="margin-top:6px">Aviso: el servicio ISUP no está en ejecución ahora (Sistema → Servicios).</div>`}
    Desde internet, el equipo tiene que llegar a esa dirección y a los puertos 7660 a 7667 del servidor.`;
}

/** Valores del bloque ISUP para el cuerpo de la petición. */
function readIsupFields(prefix) {
  return {
    isupDeviceId: $(`#${prefix}-isup-id`)?.value.trim() || null,
    isupKey: $(`#${prefix}-isup-key`)?.value || null,
  };
}

// ---------------------------------------------------------------------------
// Sistema → Conexión ISUP
// ---------------------------------------------------------------------------

const ISUP_KIND_LABELS = {
  video: ["Video", "#/devices"],
  access: ["Control de acceso", "#/access-devices"],
  alarm: ["Panel de alarma", "#/alarm-panels"],
  speaker: ["Parlante", "#/speakers"],
  intercom: ["Citofonía", "#/intercoms"],
};

let isupTimer = null;

async function renderIsupStatus() {
  $("#page-title").textContent = "Conexión ISUP";
  let s;
  try { s = await Api.get("/api/isup/status"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const canManage = Perms.can("system.services");
  const equipment = (d) => d.equipment.length
    ? d.equipment.map((e) => `<a href="${(ISUP_KIND_LABELS[e.kind] ?? ["", "#/"])[1]}">${esc(e.name)}</a> <span class="muted">(${esc((ISUP_KIND_LABELS[e.kind] ?? [e.kind])[0])})</span>`).join("<br>")
    : `<span class="muted">—</span>`;
  const since = (d) => d.onlineSince ? new Date(d.onlineSince).toLocaleString("es-CL") : "—";
  const locals = s.localAddresses ?? [];
  const serverAddress = !s.perDeviceAddress ? `<b>${esc(s.publicAddress ?? "—")}</b>`
    : locals.length > 1 ? `<b>de este servidor en la red del equipo</b> (${locals.map((a) => `<b>${esc(a)}</b>`).join(" o ")})`
    : `<b>${esc(locals[0] ?? s.publicAddress ?? "—")}</b>`;
  $("#view").innerHTML = `
    ${!s.enabled ? `<div class="error-box">El servicio ISUP está deshabilitado en la configuración (Isup:Enabled = false).</div>` : ""}
    ${s.error ? `<div class="error-box">${esc(s.error)}</div>` : ""}
    ${isupNetworksWarningHtml(s)}
    <div class="info-box">
      Los equipos Hikvision que el servidor no alcanza por IP (detrás de NAT, 4G o MPLS) se conectan ellos por <b>ISUP</b>:
      en el equipo se configura la dirección ${serverAddress}, el puerto
      <b>${s.listeners.find((l) => l.name === "Registro")?.port ?? 7660}</b>, su ID y su clave. Se agregan en cada módulo eligiendo
      la conexión «ISUP». Desde internet deben estar abiertos hacia el servidor los puertos 7660 a 7667 (TCP; el 7660 y el 7663 también UDP).
    </div>
    <div class="toolbar"><h3>Puertos</h3></div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Uso</th><th>Puerto</th><th>Estado</th></tr></thead>
      <tbody>${s.listeners.map((l) => `
        <tr><td>${esc(l.name)}</td><td>${l.port}</td>
          <td>${l.open ? `<span class="tag on">Abierto</span>` : `<span class="tag off">No se pudo abrir</span> <span class="muted">${esc(l.error ?? "")}</span>`}</td></tr>`).join("")}
      </tbody>
    </table></div>
    <div class="toolbar" style="margin-top:14px"><h3>Equipos</h3></div>
    ${s.devices.length === 0 ? `<div class="info-box">Todavía no hay equipos ISUP. Se agregan desde cada módulo (Dispositivos, Control de acceso, Paneles de alarma, Parlantes, Citofonía).</div>` : `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>ID ISUP</th><th>Equipo en el VMS</th><th>Estado</th><th>Dirección</th><th title="IP de este servidor que se le indicó al equipo para alarmas, video y voz">Servidor anunciado</th><th>Protocolo</th><th>Conectado desde</th><th>Alarmas</th><th></th></tr></thead>
      <tbody>${s.devices.map((d) => `
        <tr data-id="${esc(d.deviceId)}">
          <td><b>${esc(d.deviceId)}</b></td>
          <td>${equipment(d)}</td>
          <td>${d.online ? (d.asleep ? `<span class="tag operator">En reposo</span>` : `<span class="tag on">Conectado</span>`) : `<span class="tag off">Sin conexión</span>`}</td>
          <td class="muted">${esc(d.address ?? "—")}</td>
          <td class="muted">${esc(d.announcedAddress ?? "—")}</td>
          <td class="muted">${d.protocol ? `ISUP ${esc(d.protocol)}` : "—"}${d.firmware ? `<div style="font-size:11px">${esc(d.firmware)}</div>` : ""}</td>
          <td class="muted">${since(d)}</td>
          <td>${d.online && d.protocol === "5.0" ? (d.alarmLinkUp ? `<span class="dot ok" title="Enlace de alarmas abierto"></span>` : `<span class="dot bad" title="El equipo no abrió su enlace de alarmas"></span>`) : `<span class="muted">—</span>`}</td>
          <td class="row-actions">${canManage && d.online ? `<button class="btn ghost btn-isup-reconnect" title="El equipo vuelve a registrarse (útil tras cambiar su configuración)">Reconectar</button>` : ""}
            ${canManage && d.asleep ? `<button class="btn ghost btn-isup-wake">Despertar</button>` : ""}</td>
        </tr>`).join("")}
      </tbody>
    </table></div>`}
    ${isupLegacyHtml(s, canManage)}
    <div class="toolbar" style="margin-top:14px"><h3>Intentos rechazados</h3></div>
    ${s.rejections.length === 0 ? `<div class="muted">Ningún equipo desconocido intentó conectarse.</div>` : `
    <div class="info-box">Equipos que llaman al servidor pero no están dados de alta en el VMS (o traen otra clave). Para
      aceptarlos, agréguelos en su módulo con ese ID ISUP y la misma clave que tienen configurada.</div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>ID ISUP</th><th>Motivo</th><th>Intentos</th><th>Primero</th><th>Último</th></tr></thead>
      <tbody>${s.rejections.map((r) => `
        <tr><td><b>${esc(r.deviceId)}</b></td><td>${esc(r.reason)}</td><td>${r.count}</td>
          <td class="muted">${new Date(r.firstAt).toLocaleString("es-CL")}</td><td class="muted">${new Date(r.lastAt).toLocaleString("es-CL")}</td></tr>`).join("")}
      </tbody>
    </table></div>`}`;

  $$("#view .btn-isup-reconnect").forEach((b) => b.addEventListener("click", async (e) => {
    const id = e.target.closest("tr").dataset.id;
    try { await Api.post(`/api/isup/devices/${encodeURIComponent(id)}/reconnect`); toast("El equipo volverá a registrarse en unos segundos."); }
    catch (err) { toast(err.error, true); }
  }));
  $$("#view .btn-isup-wake").forEach((b) => b.addEventListener("click", async (e) => {
    const id = e.target.closest("tr").dataset.id;
    try { await Api.post(`/api/isup/devices/${encodeURIComponent(id)}/wake`); toast("Orden enviada: el equipo tarda alrededor de un minuto en despertar."); }
    catch (err) { toast(err.error, true); }
  }));

  wireIsupLegacy();

  clearInterval(isupTimer);
  isupTimer = setInterval(() => {
    if ($("#isup-legacy .dz-confirm:not(.hidden)")) return;   // no borrar la confirmación a medio escribir
    if (location.hash !== "#/isup" || $("#app-shell").classList.contains("hidden")) { clearInterval(isupTimer); return; }
    if ($("#modal-backdrop") && !$("#modal-backdrop").classList.contains("hidden")) return;
    renderIsupStatus();
  }, 10000);
}

/**
 * Aviso de un servidor con más de una red (cable y Wi-Fi, una red de cámaras, una VPN): cada equipo tiene que apuntar a la IP de
 * este servidor que está en SU red. Sin dirección fija, el servicio le contesta a cada uno por la IP por la que llegó; con una
 * dirección fija privada, los equipos de las otras redes no la alcanzan.
 */
function isupNetworksWarningHtml(s) {
  const locals = s.localAddresses ?? [];
  if (!s.running || locals.length < 2) return "";
  const list = locals.map((a) => `<b>${esc(a)}</b>`).join(", ");
  if (s.perDeviceAddress) {
    return `<div class="warn-box">Este servidor tiene más de una red (${list}). A cada equipo se le anuncia la IP de este servidor
      por la que llegó: en cada equipo configure la de su red (la columna «Servidor anunciado» muestra la que recibió). Un equipo
      que se conecta desde internet necesita la dirección pública: defínala en Sistema → Acceso desde Internet.</div>`;
  }
  if (locals.includes(s.publicAddress)) {
    return `<div class="warn-box">Este servidor tiene más de una red (${list}) y la dirección ISUP está fija en
      <b>${esc(s.publicAddress)}</b> (Isup:PublicAddress): los equipos de las otras redes no la alcanzan. Si no hay equipos que se
      conecten desde internet, quite Isup:PublicAddress de appsettings.Local.json y reinicie el servicio: a cada equipo se le
      anunciará la IP por la que llegó.</div>`;
  }
  return "";
}

/**
 * Compatibilidad ISUP 2.0/4.0. Esas versiones se registran sin probar su clave: con solo conocer el ID, cualquiera
 * en internet se hace pasar por el equipo. Viene apagada; encenderla exige escribir HABILITAR (zona de riesgo).
 */
function isupLegacyHtml(s, canManage) {
  const on = s.allowLegacy;
  return `
    <div class="danger-zone" id="isup-legacy" style="margin-top:14px">
      <h4>Equipos ISUP 2.0/4.0 (compatibilidad)</h4>
      <div class="dz-item">
        <div class="dz-row">
          <div>
            <b>${on ? `<span class="tag operator">Habilitado</span>` : `<span class="tag on">Deshabilitado</span>`}
              Aceptar equipos que se conectan con ISUP 2.0 o 4.0</b>
            <div class="dz-help">Solo para equipos antiguos que no tienen ISUP 5.0. Si el equipo lo permite, configúrelo en ISUP 5.0 con su clave.</div>
            <div class="dz-help"><span class="dz-risk">Riesgo:</span> ISUP 2.0/4.0 no prueba la clave del equipo al registrarse.
              Con solo conocer su ID, alguien desde internet puede hacerse pasar por él: recibir sus órdenes, mostrar video falso
              e inyectar o tapar alarmas. Aun habilitado, un equipo conectado por ISUP 5.0 nunca se reemplaza por uno 2.0/4.0.</div>
          </div>
          ${canManage ? (on
            ? `<button class="btn" type="button" id="isup-legacy-off">Deshabilitar</button>`
            : `<button class="btn dz-open" type="button">Habilitar…</button>`) : ""}
        </div>
        <div class="dz-confirm hidden">
          <label>Para confirmar que conoce el riesgo, escriba <b>HABILITAR</b>:</label>
          <div class="dz-confirm-row">
            <input class="dz-input" autocomplete="off" spellcheck="false" placeholder="HABILITAR">
            <button class="btn danger dz-go" type="button" disabled>Entiendo el riesgo: habilitar</button>
            <button class="btn ghost dz-cancel" type="button">Cancelar</button>
          </div>
        </div>
      </div>
    </div>`;
}

function wireIsupLegacy() {
  const zone = $("#isup-legacy");
  if (!zone) return;
  const confirmBox = zone.querySelector(".dz-confirm");
  const input = zone.querySelector(".dz-input");
  const go = zone.querySelector(".dz-go");
  const send = async (enabled, btn) => {
    btn.disabled = true;
    try {
      await Api.put("/api/isup/legacy", { enabled, confirm: enabled ? input.value.trim() : null });
      toast(enabled ? "Equipos ISUP 2.0/4.0 habilitados. El servicio ISUP se reinició."
                    : "Equipos ISUP 2.0/4.0 deshabilitados. El servicio ISUP se reinició.");
      renderIsupStatus();
    } catch (err) {
      toast(err.error ?? "No se pudo cambiar el ajuste.", true);
      btn.disabled = false;
    }
  };
  zone.querySelector(".dz-open")?.addEventListener("click", () => { confirmBox.classList.remove("hidden"); input.focus(); });
  zone.querySelector(".dz-cancel").addEventListener("click", () => { confirmBox.classList.add("hidden"); input.value = ""; go.disabled = true; });
  input.addEventListener("input", () => { go.disabled = input.value.trim() !== "HABILITAR"; });
  go.addEventListener("click", () => send(true, go));
  zone.querySelector("#isup-legacy-off")?.addEventListener("click", (e) => send(false, e.target));
}
