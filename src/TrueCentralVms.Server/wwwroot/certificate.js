// CLR TrueCentral VMS — panel: Sistema → Certificado HTTPS.
//
// Con qué certificado se presenta el servidor por HTTPS. Por omisión es el
// PROPIO (lo genera el servidor con su nombre y sus IP; el navegador avisa que
// no lo conoce). Se puede cargar uno del cliente (.pfx de su CA o de una CA
// pública) y volver al propio. Las huellas sirven para comparar con la que
// muestran el cliente de escritorio, las pantallas y los agentes la primera vez.
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

/** Huella en grupos de dos (ab:cd:…) para leerla en voz alta. */
function certFingerprint(hex) {
  return (hex || "").toUpperCase().match(/.{1,2}/g)?.join(":") ?? "";
}

async function renderCertificate() {
  $("#page-title").textContent = "Certificado HTTPS";
  let c;
  try { c = await Api.get("/api/system/certificate"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }
  const canManage = Perms.can("system.certificate");
  const daysLeft = Math.floor((new Date(c.notAfter) - Date.now()) / 86400000);

  $("#view").innerHTML = `
    <div class="${c.custom ? "info-box" : "warn-box"}" style="margin-bottom:12px">
      ${c.custom
        ? `<b>Certificado cargado.</b> El servidor se presenta con el certificado de su organización.`
        : `<b>Certificado propio del servidor.</b> Lo emite la CA propia de este servidor. Las conexiones van cifradas, pero
           hasta que esa CA se instale como raíz de confianza en los equipos que usan el panel, los navegadores avisan que no
           la conocen (y ese aviso no distingue al servidor de un impostor). Instálela (ver abajo) o cargue un certificado
           emitido por la CA de su organización.`}
    </div>
    ${daysLeft < 30 ? `<div class="error-box" style="margin-bottom:12px">Vence en ${Math.max(daysLeft, 0)} día(s).</div>` : ""}
    <div class="toolbar">
      <h3>Certificado en uso</h3>
      <div class="row-actions">
        <button class="btn ghost" id="cert-download-server">Descargar certificado</button>
        ${canManage && c.custom ? `<button class="btn ghost" id="cert-remove">Volver al propio</button>` : ""}
      </div>
    </div>
    <div class="card" style="max-width:900px">
      <table class="kv">
        <tr><th>Emitido para</th><td>${esc(c.subject)}</td></tr>
        <tr><th>Emitido por</th><td>${esc(c.issuer)}</td></tr>
        <tr><th>Vigencia</th><td>${esc(formatDate(c.notBefore))} — ${esc(formatDate(c.notAfter))}</td></tr>
        <tr><th>Nombres</th><td>${c.names.map((n) => `<span class="tag">${esc(n)}</span>`).join(" ")}</td></tr>
        <tr><th>Huella de la llave (SHA-256)</th><td><code class="cert-fp">${esc(certFingerprint(c.spki))}</code>
          <div class="muted" style="font-size:12px">La que muestran el cliente de escritorio, las pantallas y los agentes al conectarse por primera vez.</div></td></tr>
        <tr><th>Huella del certificado (SHA-256)</th><td><code class="cert-fp">${esc(certFingerprint(c.sha256))}</code>
          <div class="muted" style="font-size:12px">La que muestra el navegador en los detalles del certificado.</div></td></tr>
        ${c.excludedNames.length ? `<tr><th>Fuera del certificado</th><td><span class="muted">${c.excludedNames.map(esc).join(", ")}
          — la CA propia solo firma los nombres de este equipo y direcciones privadas.</span></td></tr>` : ""}
        <tr><th>Puertos</th><td>HTTPS ${c.httpsPort}${c.httpPort > 0
          ? ` · HTTP ${c.httpPort} <span class="muted">(solo equipos sin TLS: paneles de alarma y terminales ISUP)</span>` : ""}</td></tr>
      </table>
    </div>
    <div class="toolbar" style="margin-top:18px">
      <h3>CA propia del servidor</h3>
      <div class="row-actions"><button class="btn" id="cert-download-ca">Descargar CA</button></div>
    </div>
    <div class="card" style="max-width:900px">
      <table class="kv">
        <tr><th>Nombre</th><td>${esc(c.authority.subject)}</td></tr>
        <tr><th>Vence</th><td>${esc(formatDate(c.authority.notAfter))}</td></tr>
        <tr><th>Huella (SHA-256)</th><td><code class="cert-fp">${esc(certFingerprint(c.authority.sha256))}</code></td></tr>
      </table>
      <p style="margin-bottom:6px">Instálela como <b>entidad de certificación raíz de confianza</b> en cada equipo que use el panel o
        el cliente de escritorio. Después de instalarla, compruebe que la huella coincida con la de arriba.</p>
      <ul class="muted" style="margin-top:0">
        <li>Un equipo: doble clic en el archivo → <i>Instalar certificado</i> → <i>Equipo local</i> → almacén
          <i>Entidades de certificación raíz de confianza</i>. O, como administrador:
          <code>certutil -addstore Root truecentral-ca-….cer</code></li>
        <li>Toda la organización: directiva de grupo → <i>Configuración del equipo › Directivas › Configuración de Windows ›
          Configuración de seguridad › Directivas de clave pública › Entidades de certificación raíz de confianza</i> → Importar.</li>
        <li>Es segura de instalar: solo puede firmar para los nombres de este servidor, <i>localhost</i> y direcciones IP privadas,
          así que no sirve para suplantar ningún otro sitio.</li>
      </ul>
    </div>
    ${canManage ? `
    <div class="toolbar" style="margin-top:18px"><h3>Cargar el certificado de su organización</h3></div>
    <div class="card" style="max-width:900px">
      <div id="cert-error"></div>
      <p class="muted" style="margin-top:0">
        Archivo .pfx o .p12 con la llave privada, emitido para el nombre o la IP con que se entra al servidor. Si cambia la
        huella de la llave, el cliente de escritorio, las pantallas y los agentes que confiaron en el propio pedirán volver a confiar.
      </p>
      <div class="form-grid">
        <div class="field"><label>Archivo</label><input type="file" id="cert-file" accept=".pfx,.p12"></div>
        <div class="field"><label>Contraseña del archivo</label><input type="password" id="cert-password" autocomplete="off"></div>
      </div>
      <button class="btn" id="cert-upload">Cargar y usar</button>
    </div>` : ""}`;

  const download = (kind) => {
    window.location.href = `/api/system/certificate/download?kind=${kind}&access_token=${encodeURIComponent(Api.token)}`;
  };
  $("#cert-download-server").addEventListener("click", () => download("server"));
  $("#cert-download-ca").addEventListener("click", () => download("ca"));

  $("#cert-remove")?.addEventListener("click", async () => {
    if (!confirm("¿Quitar el certificado cargado y volver al propio del servidor?\n\nLos navegadores volverán a mostrar el aviso y los equipos que solo confiaban en el cargado pedirán volver a confiar.")) return;
    try {
      await Api.delete("/api/system/certificate");
      toast("El servidor volvió a su certificado propio.");
      renderCertificate();
    } catch (err) { toast(err.error ?? "No se pudo quitar el certificado.", true); }
  });

  $("#cert-upload")?.addEventListener("click", async () => {
    const file = $("#cert-file").files[0];
    if (!file) { $("#cert-error").innerHTML = `<div class="error-box">Elija el archivo .pfx o .p12.</div>`; return; }
    const btn = $("#cert-upload");
    btn.disabled = true;
    try {
      const form = new FormData();
      form.append("file", file);
      form.append("password", $("#cert-password").value);
      const response = await fetch("/api/system/certificate", {
        method: "POST", headers: { Authorization: "Bearer " + Api.token }, body: form,
      });
      const data = await response.json().catch(() => null);
      if (!response.ok) throw { error: (data && data.error) || `Error ${response.status}` };
      toast("Certificado cargado: las conexiones nuevas ya lo usan.");
      renderCertificate();
    } catch (err) {
      $("#cert-error").innerHTML = `<div class="error-box">${esc(err.error ?? "No se pudo cargar el certificado.")}</div>`;
      btn.disabled = false;
    }
  });
}
