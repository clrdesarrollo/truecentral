// Cliente SignalR del panel web (tiempo real servidor→cliente). Conexión única
// compartida entre vistas; reconexión automática. La autenticación del WebSocket
// usa ?access_token= (el middleware de tokens lo acepta, ver VmsHub).
const VmsHub = (() => {
  let conn = null;
  let starting = null;
  let lastRestart = 0;
  const handlers = new Map();          // evento -> Set(fn)
  const reconnected = new Set();       // callbacks al reconectar

  function build() {
    conn = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/vms", { accessTokenFactory: () => Api.token || "" })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 15000])
      .build();
    for (const [ev, set] of handlers) for (const fn of set) conn.on(ev, fn);
    conn.onreconnected(() => { for (const fn of reconnected) { try { fn(); } catch { /* noop */ } } });
    conn.onclose(onClosed);
    return conn;
  }

  // El servidor corta la conexión al revocar la sesión (usuario deshabilitado o
  // eliminado, contraseña o rol cambiados, cierre de sesión) sin dejar que se
  // reconecte sola, y una sesión revocada recibe 401 al reconectar. Se consulta
  // la sesión: vencida o revocada → al login, como ante cualquier 401 de la API;
  // vigente → se vuelve a conectar, a lo más una vez cada 10 s (nunca en bucle).
  async function onClosed() {
    const token = Api.token;
    if (!token) return;                // cierre de sesión en curso
    try {
      await Api.get("/api/auth/me");
    } catch (err) {
      // /api/auth/* no dispara el aviso de api.js: se hace aquí, si el token
      // sigue siendo el mismo (no lo cambió un cierre o un login entretanto).
      // Primero el aviso (deja el motivo para las otras pestañas) y después se limpia.
      if (err.status === 401 && Api.token === token) {
        window.dispatchEvent(new Event("tcvms:unauthorized"));
        Api.clearSession();
      }
      return;                          // sin servidor: lo retoma la próxima vista
    }
    if (Date.now() - lastRestart < 10000) return;
    lastRestart = Date.now();
    ensureStarted();
  }

  async function ensureStarted() {
    if (!Api.token) return;
    if (!conn) build();
    if (conn.state === "Connected" || conn.state === "Connecting" || conn.state === "Reconnecting") {
      if (starting) await starting;
      return;
    }
    if (!starting) starting = conn.start().finally(() => { starting = null; });
    try { await starting; } catch { /* reintenta el auto-reconnect */ }
  }

  return {
    /// Suscribe un handler a un evento del hub y asegura la conexión.
    on(ev, fn) {
      if (!handlers.has(ev)) handlers.set(ev, new Set());
      handlers.get(ev).add(fn);
      if (conn) conn.on(ev, fn);
      ensureStarted();
    },
    off(ev, fn) {
      handlers.get(ev)?.delete(fn);
      if (conn) conn.off(ev, fn);
    },
    onReconnected(fn) { reconnected.add(fn); return () => reconnected.delete(fn); },
    ensureStarted,
  };
})();
