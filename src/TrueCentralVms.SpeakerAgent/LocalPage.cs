namespace TrueCentralVms.SpeakerAgent;

/// <summary>La página local, en un solo archivo (sin recursos externos: el equipo puede no tener internet).</summary>
internal static class LocalPage
{
    public const string Html = """
<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>TrueCentral Speaker</title>
<style>
  :root { --bg:#f4f6f9; --card:#fff; --text:#1d2433; --muted:#68758a; --border:#d9dee7; --accent:#1f6feb; --ok:#1a7f37; --bad:#c62828; }
  @media (prefers-color-scheme: dark) { :root { --bg:#0f141b; --card:#18202b; --text:#e6ebf2; --muted:#94a0b3; --border:#2a3545; --accent:#4c8dff; --ok:#3fb950; --bad:#ff6b6b; } }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
  main { max-width:560px; margin:0 auto; padding:24px 16px 48px; }
  h1 { font-size:20px; margin:0 0 4px; }
  .sub { color:var(--muted); font-size:13px; margin-bottom:18px; }
  section { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:16px; margin-bottom:14px; }
  h2 { font-size:14px; text-transform:uppercase; letter-spacing:.04em; color:var(--muted); margin:0 0 10px; }
  label { display:block; font-size:13px; color:var(--muted); margin:10px 0 4px; }
  input, select { width:100%; padding:9px 10px; border:1px solid var(--border); border-radius:7px; background:transparent; color:var(--text); font:inherit; }
  input[type=range] { padding:0; }
  button { margin-top:12px; padding:9px 14px; border:0; border-radius:7px; background:var(--accent); color:#fff; font:inherit; cursor:pointer; }
  button.ghost { background:transparent; color:var(--text); border:1px solid var(--border); }
  button:disabled { opacity:.6; cursor:default; }
  .row { display:flex; gap:8px; flex-wrap:wrap; align-items:center; }
  .pill { display:inline-block; padding:2px 9px; border-radius:99px; font-size:12px; font-weight:600; }
  .ok { background:color-mix(in srgb, var(--ok) 18%, transparent); color:var(--ok); }
  .bad { background:color-mix(in srgb, var(--bad) 18%, transparent); color:var(--bad); }
  .msg { margin-top:10px; font-size:13px; }
  .msg.err { color:var(--bad); } .msg.good { color:var(--ok); }
  dl { display:grid; grid-template-columns:auto 1fr; gap:4px 12px; margin:0; font-size:14px; }
  dt { color:var(--muted); } dd { margin:0; overflow-wrap:anywhere; }
  .hidden { display:none; }
</style>
</head>
<body>
<main>
  <h1>TrueCentral Speaker</h1>
  <div class="sub" id="sub">Cargando…</div>

  <section id="pw-box" class="hidden">
    <h2 id="pw-title">Definir clave de esta página</h2>
    <p id="pw-help" style="margin:0;color:var(--muted);font-size:13px">Es la primera vez que se abre: defina una clave (mínimo 8 caracteres). Sin ella nadie en la red puede cambiar la configuración del equipo.</p>
    <form id="pw-form">
      <label for="pw">Clave</label>
      <input id="pw" type="password" autocomplete="current-password" required>
      <button type="submit" id="pw-btn">Guardar</button>
      <div class="msg err" id="pw-msg"></div>
    </form>
  </section>

  <div id="app" class="hidden">
    <section>
      <h2>Conexión con TrueCentral</h2>
      <dl>
        <dt>Estado</dt><dd id="st-conn">—</dd>
        <dt>Servidor</dt><dd id="st-server">—</dd>
        <dt>Parlante</dt><dd id="st-name">—</dd>
        <dt>Detalle</dt><dd id="st-error">—</dd>
      </dl>
    </section>

    <section>
      <h2 id="enroll-title">Enrolar en un servidor</h2>
      <p style="margin:0;color:var(--muted);font-size:13px">En TrueCentral: <b>Parlantes IP → Agregar parlante → TrueCentral Speaker</b>; al guardar aparece un código de 8 caracteres que vale una vez y por 15 minutos.</p>
      <form id="enroll-form">
        <label for="server">Servidor</label>
        <input id="server" placeholder="https://192.168.1.10" required>
        <label for="code">Código de enrolamiento</label>
        <input id="code" placeholder="ABCD-EFGH" autocomplete="off" required style="text-transform:uppercase;letter-spacing:2px">
        <div class="row">
          <button type="submit" id="enroll-btn">Enrolar</button>
          <button type="button" class="ghost hidden" id="forget-btn">Olvidar enrolamiento</button>
        </div>
        <div class="msg" id="enroll-msg"></div>
      </form>
    </section>

    <section>
      <h2>Audio</h2>
      <label for="device">Salida</label>
      <select id="device"></select>
      <label for="volume">Volumen: <b id="volume-label">—</b></label>
      <input id="volume" type="range" min="0" max="100" step="1">
      <div class="row">
        <button type="button" id="tone-btn">Tono de prueba (3 s)</button>
        <span id="playing" style="margin-top:12px;color:var(--muted);font-size:13px"></span>
      </div>
      <div class="msg" id="audio-msg"></div>
    </section>

    <section>
      <h2>Equipo</h2>
      <dl>
        <dt>Hardware</dt><dd id="eq-hw">—</dd>
        <dt>Nombre de red</dt><dd id="eq-host">—</dd>
        <dt>Versión</dt><dd id="eq-version">—</dd>
        <dt>Id</dt><dd id="eq-id" style="font-family:monospace;font-size:12px">—</dd>
      </dl>
      <div class="row">
        <button type="button" class="ghost" id="logout-btn">Cerrar sesión</button>
      </div>
    </section>
  </div>
</main>
<script>
"use strict";
const $ = (s) => document.querySelector(s);
async function call(method, url, body) {
  const r = await fetch(url, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  const data = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(data.error || `Error ${r.status}`);
  return data;
}
function msg(el, text, good) { el.textContent = text || ""; el.className = "msg " + (good ? "good" : "err"); }
let devicesLoaded = false, draggingVolume = false;

async function refresh() {
  let s;
  try { s = await call("GET", "/api/status"); } catch { $("#sub").textContent = "Sin respuesta del agente."; return; }
  $("#sub").textContent = `${s.hostname} · ${s.hardware} · versión ${s.version}`;
  if (s.needsPassword || !s.authenticated) {
    $("#app").classList.add("hidden");
    $("#pw-box").classList.remove("hidden");
    $("#pw-title").textContent = s.needsPassword ? "Definir clave de esta página" : "Iniciar sesión";
    $("#pw-help").classList.toggle("hidden", !s.needsPassword);
    $("#pw-btn").textContent = s.needsPassword ? "Guardar" : "Entrar";
    $("#pw-form").dataset.mode = s.needsPassword ? "set" : "login";
    return;
  }
  $("#pw-box").classList.add("hidden");
  $("#app").classList.remove("hidden");
  $("#st-conn").innerHTML = s.connected ? `<span class="pill ok">Conectado</span>` : s.enrolled ? `<span class="pill bad">Sin conexión</span>` : `<span class="pill bad">Sin enrolar</span>`;
  $("#st-server").textContent = s.server || "—";
  $("#st-name").textContent = s.speakerName || "—";
  $("#st-error").textContent = s.lastError || (s.connected ? "Listo para recibir audio." : "—");
  $("#enroll-title").textContent = s.enrolled ? "Enrolar en otro servidor (o de nuevo)" : "Enrolar en un servidor";
  $("#forget-btn").classList.toggle("hidden", !s.enrolled);
  if (!$("#server").value && s.server) $("#server").value = s.server;
  if (!draggingVolume) { $("#volume").value = s.volume; $("#volume-label").textContent = s.volume; }
  $("#playing").textContent = s.playing ? `Sonando: ${s.playing}` : "";
  if (s.outputError) msg($("#audio-msg"), `Salida de audio: ${s.outputError}`);
  $("#eq-hw").textContent = s.hardware; $("#eq-host").textContent = s.hostname; $("#eq-version").textContent = s.version; $("#eq-id").textContent = s.agentId;
  if (!devicesLoaded) {
    devicesLoaded = true;
    const devices = await call("GET", "/api/devices").catch(() => []);
    $("#device").innerHTML = devices.map((d) => `<option value="${d.id.replace(/"/g, "&quot;")}">${d.name.replace(/</g, "&lt;")}</option>`).join("");
    $("#device").value = s.outputDevice;
  }
}

$("#pw-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  try {
    await call("POST", e.target.dataset.mode === "set" ? "/api/password" : "/api/login", { password: $("#pw").value });
    $("#pw").value = ""; msg($("#pw-msg"), "");
    refresh();
  } catch (err) { msg($("#pw-msg"), err.message); }
});
$("#enroll-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const btn = $("#enroll-btn"); btn.disabled = true; btn.textContent = "Enrolando…";
  try {
    const r = await call("POST", "/api/enroll", { server: $("#server").value, code: $("#code").value });
    $("#code").value = ""; msg($("#enroll-msg"), r.message, true);
    setTimeout(refresh, 1500);
  } catch (err) { msg($("#enroll-msg"), err.message); }
  finally { btn.disabled = false; btn.textContent = "Enrolar"; }
});
$("#forget-btn").addEventListener("click", async () => {
  if (!confirm("¿Olvidar el enrolamiento? El equipo dejará de conectarse hasta enrolarlo otra vez.")) return;
  try { await call("POST", "/api/forget"); refresh(); } catch (err) { msg($("#enroll-msg"), err.message); }
});
$("#volume").addEventListener("input", () => { draggingVolume = true; $("#volume-label").textContent = $("#volume").value; });
$("#volume").addEventListener("change", async () => {
  try { await call("POST", "/api/volume", { volume: Number($("#volume").value) }); } catch (err) { msg($("#audio-msg"), err.message); }
  draggingVolume = false;
});
$("#device").addEventListener("change", async () => {
  try { await call("POST", "/api/device", { device: $("#device").value }); msg($("#audio-msg"), "Salida cambiada.", true); }
  catch (err) { msg($("#audio-msg"), err.message); }
});
$("#tone-btn").addEventListener("click", async () => {
  try { await call("POST", "/api/test-tone"); msg($("#audio-msg"), "Sonando el tono de prueba…", true); }
  catch (err) { msg($("#audio-msg"), err.message); }
});
$("#logout-btn").addEventListener("click", async () => { await call("POST", "/api/logout").catch(() => {}); refresh(); });
refresh();
setInterval(refresh, 3000);
</script>
</body>
</html>
""";
}
