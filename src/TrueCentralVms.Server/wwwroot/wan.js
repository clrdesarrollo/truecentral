// CLR TrueCentral VMS — panel: Sistema → Acceso desde Internet.
//
// Dirección pública, redes locales, escenarios y la tabla de puertos que hay
// que reenviar en el router. Quien entra desde una red local recibe las
// direcciones internas; el resto, la pública con el puerto del router. Solo
// entran desde Internet los usuarios con «Entrar desde Internet» en su rol.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

async function renderWan() {
  $("#page-title").textContent = "Acceso desde Internet";
  let w;
  try { w = await Api.get("/api/system/wan"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const scenarios = new Set(w.scenarios);

  const certWarning = w.enabled && !w.certificate.coversPublicHost ? `
    <div class="warn-box" style="margin-bottom:12px">
      <b>Certificado.</b> El certificado HTTPS no incluye <b>${esc(w.publicHost)}</b>: desde Internet el navegador y el cliente
      mostrarán un aviso. Cargue un certificado emitido para ese nombre (por ejemplo de Let's Encrypt) en
      <a href="#/certificate">Sistema → Certificado HTTPS</a>. La CA propia del servidor solo firma direcciones privadas.
    </div>` : "";

  $("#view").innerHTML = `
    <div class="${w.enabled ? "warn-box" : "info-box"}" style="margin-bottom:12px">
      ${w.enabled
        ? `<b>Acceso desde Internet encendido</b> en <b>${esc(w.publicHost)}</b>. Solo entran desde fuera de las redes locales los
           usuarios cuyo rol tiene <i>Entrar desde Internet</i>.`
        : `<b>Acceso desde Internet apagado.</b> El sistema funciona solo en la red local. Si necesita acceso remoto, una VPN
           sigue siendo la opción más segura: con ella no hace falta abrir puertos en el router.`}
      <div class="muted" style="font-size:12px;margin-top:4px">Usted se conecta desde ${esc(w.you.ip ?? "—")}
        (${w.you.remote ? "Internet" : "red local"}).</div>
    </div>
    ${certWarning}
    <div class="card" style="max-width:1000px">
      <div id="wan-error"></div>
      <div class="form-grid">
        <div class="field">
          <label>Dirección pública</label>
          <input id="wan-host" value="${esc(w.publicHost)}" placeholder="vms.miempresa.cl o 200.55.209.84" autocomplete="off" spellcheck="false">
          <div class="muted field-hint">IP fija del router o nombre DNS (puede ser de un servicio DDNS). Sin http:// ni puerto.</div>
        </div>
        <div class="field">
          <label>Redes locales</label>
          <textarea id="wan-networks" rows="4" spellcheck="false">${esc(w.localNetworks.join("\n"))}</textarea>
          <div class="muted field-hint">Una por línea (ej. 192.168.0.0/16). Quien se conecta desde ellas recibe las direcciones internas.
            <a href="#" id="wan-networks-default">Usar las redes privadas</a></div>
        </div>
      </div>

      <h4 style="margin:16px 0 6px">Qué se usa desde Internet</h4>
      <div class="wan-scenarios">
        ${w.catalog.map((s) => `
          <label class="wan-scenario">
            <input type="checkbox" class="wan-sc" value="${esc(s.key)}" ${scenarios.has(s.key) ? "checked" : ""}>
            <span><b>${esc(s.name)}</b><span class="muted">${esc(s.description)}</span></span>
          </label>`).join("")}
      </div>

      <h4 style="margin:16px 0 6px">Puertos que hay que reenviar en el router</h4>
      <div class="muted" style="font-size:12px;margin-bottom:6px">En el router, cada puerto público se reenvía al mismo puerto
        interno de este servidor. Los marcados como fijos deben tener el mismo número afuera y adentro.</div>
      <div class="table-scroll"><table class="grid" id="wan-ports">
        <thead><tr><th>Puerto</th><th>Protocolo</th><th>Interno (este servidor)</th><th>Público (router)</th></tr></thead>
        <tbody></tbody>
      </table></div>

      <div class="row-actions" style="margin-top:14px">
        <button class="btn" id="wan-save">Guardar</button>
        <button class="btn ghost" id="wan-export">Exportar tabla para el técnico de red</button>
      </div>
    </div>

    <div class="danger-zone" id="wan-zone" style="max-width:1000px">
      <h4>Encender o apagar</h4>
      <div class="dz-item">
        <div class="dz-row">
          <div>
            <b>${w.enabled ? `<span class="tag operator">Encendido</span>` : `<span class="tag on">Apagado</span>`}
              Permitir conexiones desde Internet</b>
            <div class="dz-help"><span class="dz-risk">Riesgo:</span> con los puertos abiertos, cualquiera en Internet puede intentar
              entrar. El sistema bloquea las cuentas tras intentos fallidos, todo viaja cifrado y solo entran los usuarios con
              <i>Entrar desde Internet</i>, pero cada puerto abierto es una puerta más. Abra solo los escenarios que usa.</div>
          </div>
          ${w.enabled
            ? `<button class="btn" type="button" id="wan-off">Apagar</button>`
            : `<button class="btn dz-open" type="button">Encender…</button>`}
        </div>
        <div class="dz-confirm hidden">
          <label>Para confirmar que conoce el riesgo, escriba <b>HABILITAR</b>:</label>
          <div class="dz-confirm-row">
            <input class="dz-input" autocomplete="off" spellcheck="false" placeholder="HABILITAR">
            <button class="btn danger dz-go" type="button" disabled>Entiendo el riesgo: encender</button>
            <button class="btn ghost dz-cancel" type="button">Cancelar</button>
          </div>
        </div>
      </div>
    </div>
    ${w.updatedUtc ? `<div class="muted" style="font-size:12px;margin-top:8px">Último cambio: ${esc(formatDate(w.updatedUtc))} por ${esc(w.updatedBy ?? "—")}.</div>` : ""}`;

  const drawPorts = () => {
    const chosen = new Set($$("#view .wan-sc").filter((c) => c.checked).map((c) => c.value));
    const rows = w.ports.filter((p) => p.scenarios.some((s) => chosen.has(s)));
    $("#wan-ports tbody").innerHTML = rows.length === 0
      ? `<tr><td colspan="4" class="muted">Elija al menos un escenario.</td></tr>`
      : rows.map((p) => `
        <tr data-key="${esc(p.key)}">
          <td>${esc(p.name)}</td>
          <td>${esc(p.protocol)}</td>
          <td>${p.internal}</td>
          <td>${p.fixedPublic
            ? `${p.internal} <span class="muted" style="font-size:11px">(fijo)</span>`
            : `<input type="number" min="1" max="65535" class="wan-port" value="${p.publicPort}" style="width:110px">`}</td>
        </tr>`).join("");
  };
  drawPorts();
  $$("#view .wan-sc").forEach((c) => c.addEventListener("change", drawPorts));
  $("#wan-networks-default").addEventListener("click", (e) => {
    e.preventDefault();
    $("#wan-networks").value = w.defaultLocalNetworks.join("\n");
  });

  const read = (enabled, confirm) => {
    const publicPorts = {};
    $$("#wan-ports tbody tr[data-key]").forEach((tr) => {
      const input = tr.querySelector(".wan-port");
      if (input) publicPorts[tr.dataset.key] = Number(input.value);
    });
    // Los puertos de escenarios no elegidos conservan lo que tenían.
    w.ports.forEach((p) => { if (!(p.key in publicPorts) && p.publicPort !== p.internal) publicPorts[p.key] = p.publicPort; });
    return {
      enabled,
      publicHost: $("#wan-host").value.trim(),
      localNetworks: $("#wan-networks").value.split(/\r?\n/).map((s) => s.trim()).filter(Boolean),
      scenarios: $$("#view .wan-sc").filter((c) => c.checked).map((c) => c.value),
      publicPorts,
      confirm,
    };
  };
  const save = async (enabled, confirm, btn, message) => {
    btn.disabled = true;
    try {
      await Api.put("/api/system/wan", read(enabled, confirm));
      toast(message);
      renderWan();
    } catch (err) {
      $("#wan-error").innerHTML = `<div class="error-box">${esc(err.error ?? "No se pudo guardar.")}</div>`;
      $("#wan-error").scrollIntoView({ block: "nearest" });
      btn.disabled = false;
    }
  };

  $("#wan-save").addEventListener("click", (e) => save(w.enabled, null, e.target, "Acceso desde Internet guardado."));
  $("#wan-export").addEventListener("click", () => {
    window.location.href = `/api/system/wan/ports.csv?access_token=${encodeURIComponent(Api.token)}`;
  });
  $("#wan-off")?.addEventListener("click", (e) => {
    if (!confirm("¿Apagar el acceso desde Internet? Quienes estén conectados desde afuera dejarán de poder operar.")) return;
    save(false, null, e.target, "Acceso desde Internet apagado.");
  });
  const zone = $("#wan-zone");
  const confirmBox = zone.querySelector(".dz-confirm");
  const input = zone.querySelector(".dz-input");
  const go = zone.querySelector(".dz-go");
  zone.querySelector(".dz-open")?.addEventListener("click", () => { confirmBox.classList.remove("hidden"); input.focus(); });
  zone.querySelector(".dz-cancel").addEventListener("click", () => { confirmBox.classList.add("hidden"); input.value = ""; go.disabled = true; });
  input.addEventListener("input", () => { go.disabled = input.value.trim() !== "HABILITAR"; });
  go.addEventListener("click", () => save(true, input.value.trim(), go, "Acceso desde Internet encendido."));
}
