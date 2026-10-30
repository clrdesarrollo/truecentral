// Cliente HTTP del panel: token Bearer en localStorage, errores normalizados y
// tickets de URL para las imágenes, el audio y las descargas.
"use strict";

const Api = {
  get token() { return localStorage.getItem("tcvms_token"); },
  set token(value) {
    if (value) localStorage.setItem("tcvms_token", value);
    else localStorage.removeItem("tcvms_token");
  },
  get username() { return localStorage.getItem("tcvms_user"); },
  set username(value) {
    if (value) localStorage.setItem("tcvms_user", value);
    else localStorage.removeItem("tcvms_user");
  },
  get role() { return localStorage.getItem("tcvms_role"); },
  set role(value) {
    if (value) localStorage.setItem("tcvms_role", value);
    else localStorage.removeItem("tcvms_role");
  },

  /**
   * Solicitud a la API. Devuelve el JSON de respuesta; en error lanza un
   * objeto { status, error, data } con el mensaje del servidor si existe.
   */
  async request(method, path, body) {
    const headers = {};
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (this.token) headers["Authorization"] = "Bearer " + this.token;

    let response;
    try {
      response = await fetch(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      throw { status: 0, error: "Sin conexión con el servidor." };
    }

    let data = null;
    const text = await response.text();
    if (text) { try { data = JSON.parse(text); } catch { /* respuesta no JSON */ } }

    if (!response.ok) {
      // Sesión vencida o revocada: volver al login (excepto en el propio login).
      // Primero el aviso (anota el motivo para las otras pestañas) y después se limpia.
      if (response.status === 401 && this.token && !path.startsWith("/api/auth/")) {
        window.dispatchEvent(new Event("tcvms:unauthorized"));
        this.clearSession();
      }
      throw { status: response.status, error: (data && data.error) || `Error ${response.status}`, data };
    }
    return data;
  },

  get(path) { return this.request("GET", path); },
  post(path, body) { return this.request("POST", path, body); },
  put(path, body) { return this.request("PUT", path, body); },
  delete(path) { return this.request("DELETE", path); },

  clearSession() { this.token = null; this.username = null; this.role = null; this._media = null; },

  saveSession(login) {
    this.token = login.token;
    this.username = login.username;
    this.role = login.role;
  },

  // -------------------------------------------------------------------------
  // Tickets de URL. Un <img>, un <audio> o una descarga no mandan la cabecera
  // Authorization. En vez del token de la sesión (que en la dirección queda en
  // el historial, en la lista de descargas y en cualquier enlace copiado)
  // llevan ?ticket=: una credencial de un minuto que solo sirve para eso (ver
  // UrlTicketService en el servidor).
  // -------------------------------------------------------------------------
  _media: null,          // { ticket, expiresAt (ms), token }: el de imágenes y audio
  _mediaPending: null,

  _withParam(path, key, value) {
    return `${path}${path.includes("?") ? "&" : "?"}${key}=${encodeURIComponent(value)}`;
  },

  /** El ticket de imágenes y audio vigente; pide otro si al actual le queda menos de medio minuto. */
  ensureMediaTicket() {
    const token = this.token;
    if (!token) return Promise.resolve(null);
    const m = this._media;
    if (m && m.token === token && m.expiresAt - Date.now() > 30000) return Promise.resolve(m.ticket);
    this._mediaPending ??= this.post("/api/auth/ticket", { purpose: "media" })
      .then((r) => {
        if (this.token !== token) return null;   // se cerró la sesión o entró otro usuario entretanto
        this._media = { ticket: r.ticket, expiresAt: Date.now() + r.expiresIn * 1000, token };
        return r.ticket;
      })
      .catch(() => null)                         // sin sesión: ya lo avisa SessionWatch
      .finally(() => { this._mediaPending = null; });
    return this._mediaPending;
  },

  /**
   * Dirección de una imagen o un audio de la API para el src de un <img> o un
   * <audio>, con el ticket vigente. Se arma al momento de usarla: guardada,
   * deja de valer en un minuto (las imágenes que tardan en cargar se reintentan
   * solas, ver retryMedia).
   */
  mediaUrl(path) {
    this.ensureMediaTicket();                    // renueva en segundo plano si está por vencer
    const m = this._media;
    return m && m.token === this.token ? this._withParam(path, "ticket", m.ticket) : path;
  },

  /**
   * Un <img> de la API no cargó. Si fue por un ticket vencido (la imagen tardó
   * en cargar, la pestaña estuvo oculta) se vuelve a pedir, una vez, con uno
   * vigente. true = se reintenta: el error no cuenta.
   */
  retryMedia(img) {
    let url;
    try { url = new URL(img.getAttribute("src") || "", location.href); } catch { return false; }
    const used = url.searchParams.get("ticket");
    if (url.origin !== location.origin || !used || img.dataset.ticketRetry === used) return false;
    const m = this._media;
    if (m && m.ticket === used && m.expiresAt - Date.now() > 5000) return false;   // ticket vigente: el error es otro
    img.dataset.ticketRetry = used;
    this.ensureMediaTicket().then((ticket) => {
      if (!ticket || !img.isConnected) return;
      url.searchParams.set("ticket", ticket);
      img.src = url.pathname + url.search;
    });
    return true;
  },

  /**
   * Baja un archivo de la API (exportación, instalador, grabación) con un ticket
   * de un solo uso para esa dirección exacta. El nombre lo da el servidor, salvo
   * que se indique otro. false (y el aviso) si no se pudo empezar.
   */
  async download(url, fileName = "") {
    let ticket;
    try { ({ ticket } = await this.post("/api/auth/ticket", { purpose: "download", url })); }
    catch (err) {
      if (typeof toast === "function") toast(err.error || "No se pudo iniciar la descarga.", true);
      return false;
    }
    const link = document.createElement("a");
    link.href = this._withParam(url, "ticket", ticket);
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    return true;
  },
};

// El ticket de imágenes y audio se renueva solo mientras hay sesión y la pestaña está a la vista.
setInterval(() => { if (Api.token && !document.hidden) Api.ensureMediaTicket(); }, 15000);
document.addEventListener("visibilitychange", () => { if (Api.token && !document.hidden) Api.ensureMediaTicket(); });

// Imágenes de la API que no cargan: primero el reintento con un ticket vigente
// (detiene el evento: el error no llega al <img>); si de verdad no hay imagen,
// lo que pida el <img> con data-img-error: "hide" lo oculta, "remove" lo quita.
// Reemplaza a los onerror="" en línea, que la política de contenido no deja correr.
document.addEventListener("error", (e) => {
  const img = e.target;
  if (!(img instanceof HTMLImageElement)) return;
  if (Api.retryMedia(img)) { e.stopPropagation(); return; }
  if (img.dataset.imgError === "hide") img.classList.add("hidden");
  else if (img.dataset.imgError === "remove") img.remove();
}, true);

// Enlaces a una imagen de la API (abrir en tamaño completo, en otra pestaña):
// el ticket se pone al usarlos, no al dibujar la página (ya habría vencido).
const refreshMediaLink = (e) => {
  const link = e.target.closest?.("a[data-media-href]");
  if (link) link.href = Api.mediaUrl(link.dataset.mediaHref);
};
document.addEventListener("mousedown", refreshMediaLink, true);
document.addEventListener("click", refreshMediaLink, true);
