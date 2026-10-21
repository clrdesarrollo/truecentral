// CLR TrueCentral VMS — panel: Mi cuenta (menú del usuario).
//
// Lo que el usuario conectado puede saber de sí mismo (roles, alcance,
// contraseña, sesión y últimos ingresos) y el cambio de su contraseña. Las
// reglas de la contraseña se muestran como píldoras que pasan de gris a verde
// a medida que se cumplen; el servidor revalida siempre (y el historial).
//
// Se carga ANTES que app.js: solo declara funciones.
"use strict";

const ORIGIN_LABELS = { web: "panel web", client: "cliente de escritorio" };

function accountLogin(login) {
  if (!login) return `<span class="muted">—</span>`;
  const origin = ORIGIN_LABELS[login.origin] ?? login.origin ?? "";
  // IPv4 que llega mapeada en IPv6 (::ffff:192.168.1.10) se muestra como IPv4.
  const ip = (login.ip || "—").replace(/^::ffff:/i, "");
  return `${esc(formatDate(login.at))} <span class="muted">desde ${esc(ip)}${origin ? ` (${esc(origin)})` : ""}</span>`;
}

async function renderAccount() {
  $("#page-title").textContent = "Mi cuenta";
  let a;
  try { a = await Api.get("/api/auth/account"); }
  catch (err) { $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  const expires = a.passwordExpiresAt ? new Date(a.passwordExpiresAt) : null;
  const daysLeft = expires ? Math.ceil((expires - Date.now()) / 86400000) : null;

  $("#view").innerHTML = `
    <div class="account-grid">
      <div class="card">
        <div class="account-head">
          <i class="mdi mdi-account-circle" aria-hidden="true"></i>
          <div>
            <div class="account-name">${esc(a.username)}</div>
            <div class="muted">${a.isSuperAdmin ? "Superadministrador" : a.isAdmin ? "Administrador" : "Usuario"}</div>
          </div>
        </div>
        <table class="kv">
          <tr><th>Roles</th><td>${a.roles.length ? a.roles.map((r) => `<span class="tag">${esc(r)}</span>`).join(" ") : `<span class="muted">—</span>`}</td></tr>
          <tr><th>Alcance</th><td>${a.restricted
            ? (a.scope.length ? a.scope.map((s) => `<div>${esc(s)}</div>`).join("") : `<span class="muted">Sin ubicaciones asignadas</span>`)
            : "Todas las ubicaciones"}</td></tr>
          <tr><th>Cuenta creada</th><td>${esc(formatDate(a.createdAt))}</td></tr>
          <tr><th>Sesión actual</th><td>${accountLogin(a.currentLogin)}
            <div class="muted" style="font-size:12px">Vence ${esc(formatDate(a.sessionExpiresAt))}</div></td></tr>
          <tr><th>Ingreso anterior</th><td>${accountLogin(a.previousLogin)}</td></tr>
          <tr><th>Intentos fallidos</th><td>${a.failedSincePrevious === 0
            ? `<span class="muted">Ninguno desde su ingreso anterior</span>`
            : `<span class="tag operator">${a.failedSincePrevious}</span> desde su ingreso anterior
               <div class="muted" style="font-size:12px">Si no fue usted, cambie su contraseña y avise al administrador.</div>`}</td></tr>
        </table>
      </div>

      <div class="card">
        <h3 style="margin-top:0">Cambiar contraseña</h3>
        <p class="muted" style="margin-top:0">
          Última modificación: ${esc(formatDate(a.passwordChangedAt))} ·
          ${expires ? (daysLeft > 0 ? `vence en ${daysLeft} día(s).` : `<b>vencida.</b>`) : "no vence."}
          Al cambiarla se cierran sus otras sesiones abiertas.
        </p>
        <div id="acc-error"></div>
        <form id="acc-form" autocomplete="off">
          <input type="text" autocomplete="username" value="${esc(a.username)}" hidden>
          <div class="field">
            <label>Contraseña actual</label>
            <input id="acc-current" type="password" autocomplete="current-password" required>
          </div>
          <div class="field">
            <label>Contraseña nueva</label>
            <input id="acc-new" type="password" autocomplete="new-password" required>
          </div>
          <div class="field">
            <label>Repita la contraseña nueva</label>
            <input id="acc-new2" type="password" autocomplete="new-password" required>
          </div>
          <div class="pw-pills" id="acc-pills">
            ${PASSWORD_RULES.map((r) => `<span class="pw-pill" data-rule="${r.id}">${esc(r.text)}</span>`).join("")}
            <span class="pw-pill" data-rule="different">Distinta de la actual</span>
            <span class="pw-pill" data-rule="match">Las dos coinciden</span>
          </div>
          <button class="btn" type="submit" id="acc-save" disabled>Cambiar contraseña</button>
        </form>
      </div>
    </div>`;

  const current = $("#acc-current"), next = $("#acc-new"), again = $("#acc-new2"), save = $("#acc-save");
  const update = () => {
    const p = next.value;
    const checks = Object.fromEntries(PASSWORD_RULES.map((r) => [r.id, r.test(p)]));
    checks.different = p.length > 0 && p !== current.value;
    checks.match = p.length > 0 && p === again.value;
    $$("#acc-pills .pw-pill").forEach((pill) => pill.classList.toggle("ok", !!checks[pill.dataset.rule]));
    save.disabled = !(current.value && Object.values(checks).every(Boolean));
  };
  [current, next, again].forEach((input) => input.addEventListener("input", update));
  update();

  $("#acc-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    if (save.disabled) return;
    save.disabled = true;
    $("#acc-error").innerHTML = "";
    try {
      const login = await Api.post("/api/auth/change-password", {
        username: a.username,
        currentPassword: current.value,
        newPassword: next.value,
      });
      // El servidor revoca las sesiones anteriores y entrega una nueva: se sigue con ella.
      Api.saveSession(login);
      toast("Contraseña cambiada. Sus otras sesiones se cerraron.");
      renderAccount();
    } catch (err) {
      $("#acc-error").innerHTML = `<div class="error-box">${esc(err.error ?? "No se pudo cambiar la contraseña.")}</div>`;
      update();
    }
  });
}
