// Cliente HTTP del panel: token Bearer en localStorage y errores normalizados.
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
      if (response.status === 401 && this.token && !path.startsWith("/api/auth/")) {
        this.clearSession();
        window.dispatchEvent(new Event("tcvms:unauthorized"));
      }
      throw { status: response.status, error: (data && data.error) || `Error ${response.status}`, data };
    }
    return data;
  },

  get(path) { return this.request("GET", path); },
  post(path, body) { return this.request("POST", path, body); },
  put(path, body) { return this.request("PUT", path, body); },
  delete(path) { return this.request("DELETE", path); },

  clearSession() { this.token = null; this.username = null; this.role = null; },

  saveSession(login) {
    this.token = login.token;
    this.username = login.username;
    this.role = login.role;
  },
};
