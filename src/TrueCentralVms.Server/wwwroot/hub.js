// Cliente SignalR del panel web (tiempo real servidor→cliente). Conexión única
// compartida entre vistas; reconexión automática. La autenticación del WebSocket
// usa ?access_token= (el middleware de tokens lo acepta, ver VmsHub).
const VmsHub = (() => {
  let conn = null;
  let starting = null;
  const handlers = new Map();          // evento -> Set(fn)
  const reconnected = new Set();       // callbacks al reconectar

  function build() {
    conn = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/vms", { accessTokenFactory: () => Api.token || "" })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 15000])
      .build();
    for (const [ev, set] of handlers) for (const fn of set) conn.on(ev, fn);
    conn.onreconnected(() => { for (const fn of reconnected) { try { fn(); } catch { /* noop */ } } });
    return conn;
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
