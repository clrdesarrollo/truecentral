// CLR TrueCentral VMS — panel: automatizaciones (workflows).
//
// Se carga ANTES que app.js: este archivo solo declara funciones (sin efectos
// al cargar) y app.js las referencia desde su tabla de rutas.
//
// Aquí viven el listado, las alertas, el historial, el correo saliente y los
// sonidos. El EDITOR es un diagrama de flujo aparte (workflow-editor.js,
// ruta #/workflows/edit); lo único compartido con él es CÓMO se pinta la
// configuración de cada acción (WF_FIELDS + wfFieldHtml + wfReadField).
"use strict";

let workflowsTimer = null;
let wfCatalogCache = null;
let wfCamerasCache = null;

async function wfCatalog() {
  if (!wfCatalogCache) {
    const catalog = await Api.get("/api/workflows/catalog");
    // Disparador de Control de rondas: si el servidor todavía no lo publica en
    // su catálogo (lo ejecuta igual: WorkflowTriggerTypes.PatrolEvent), se
    // agrega aquí para poder abrir y editar las automatizaciones que lo usan.
    if (!catalog.triggers.some((t) => t.key === WF_PATROL_TRIGGER.key) && Perms.licensed("module_patrol")) {
      catalog.triggers.push(WF_PATROL_TRIGGER);
    }
    wfCatalogCache = catalog;
  }
  return wfCatalogCache;
}

/** Disparador "patrol-event" (Control de rondas): rótulo, ayuda y marcas para los textos. */
const WF_PATROL_TRIGGER = {
  key: "patrol-event",
  label: "Evento de rondas",
  description: "Cuando una ronda no se inicia a tiempo, queda sin terminar o incompleta, se cumple o empieza; o cuando un bastón llega al 80 % de memoria o una base de descarga pierde la conexión.",
  placeholders: [
    { key: "ruta", label: "Nombre de la ruta" },
    { key: "detalle", label: "Detalle: puntos omitidos, causa, bastón…" },
    { key: "ubicacion", label: "Ubicación de la ruta" },
    { key: "horaprogramada", label: "Hora programada de la ronda (hh:mm)" },
    { key: "guardias", label: "Guardias que hicieron la ronda" },
  ],
  group: "Control de rondas",
};
let wfSpeakersCache = null;
async function wfSpeakers() {
  wfSpeakersCache ??= await Api.get("/api/workflows/speakers");
  return wfSpeakersCache;
}

async function wfCameras() {
  wfCamerasCache ??= await Api.get("/api/workflows/cameras");
  return wfCamerasCache;
}

const WF_KINDS = [["Alarm", "Alarma"], ["ZoneTriggered", "Sensor interrumpido"], ["ZoneRestored", "Sensor restablecido"], ["Restore", "Restauración"],
  ["Arm", "Armado"], ["Disarm", "Desarmado"], ["Bypass", "Anulación"], ["Trouble", "Falla"],
  ["System", "Sistema"], ["Info", "Información"]];
const WF_SEVERITIES = [["Critical", "Crítica"], ["Warning", "Advertencia"], ["Info", "Informativa"]];
const WF_STATUSES = [["Offline", "Sin conexión"], ["AuthFailed", "Credenciales rechazadas"], ["Online", "En línea"]];
const WF_SOURCES = [["panel", "Informado por el panel"], ["poll", "Detectado por sondeo"], ["vms", "Orden desde el VMS"]];
const WF_DAYS = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];
/** Orden en que se muestran los días (lunes primero); el valor guardado sigue siendo 0 = domingo. */
const WF_DAY_ORDER = [1, 2, 3, 4, 5, 6, 0];
const WF_DAY_ITEMS = () => WF_DAY_ORDER.map((d) => [d, WF_DAYS[d]]);

/**
 * Normaliza una hora escrita a mano a "HH:mm" en 24 h ("6" → "06:00",
 * "1830" → "18:30", "18:5" → "18:05", "24:00" = fin del día). Devuelve "" si no es una hora. Se usa
 * en vez de <input type="time"> porque ese control muestra AM/PM según el
 * idioma del navegador y no se puede forzar a 24 h.
 */
function wfNormalizeTime(value) {
  const v = String(value ?? "").trim().replace(/[.,;]/g, ":").replace(/\s+/g, "");
  if (v === "") return "";
  let h, m;
  const parts = v.split(":");
  if (parts.length === 2) { h = parts[0]; m = parts[1]; }
  else if (parts.length === 1 && /^\d{1,4}$/.test(v)) {
    if (v.length <= 2) { h = v; m = "0"; }
    else { h = v.slice(0, v.length - 2); m = v.slice(-2); }
  } else return "";
  if (!/^\d{1,2}$/.test(h) || !/^\d{1,2}$/.test(m)) return "";
  const hh = Number(h), mm = Number(m);
  if (hh === 24 && mm === 0) return "24:00"; // fin del día, válido solo como "hasta"
  if (hh > 23 || mm > 59) return "";
  return `${String(hh).padStart(2, "0")}:${String(mm).padStart(2, "0")}`;
}

/** Campo de hora en 24 h: texto con normalización al perder el foco. */
function wfTimeInput(id, value) {
  return `<input id="${id}" class="wf-time" inputmode="numeric" maxlength="5" placeholder="hh:mm" value="${esc(value || "")}" title="Hora local en formato 24 h (por ejemplo 18:00)">`;
}

const WF_EMAIL_BODY =
  "{tipo}: {evento}\n\n" +
  "Equipo: {equipo}\n" +
  "Fecha y hora: {fechahora}\n\n" +
  "Aviso automático de CLR TrueCentral VMS ({servidor}) — automatización «{workflow}».";

/** Acción de parlante del inventario con orden "reproducir" (no "detener"). */
function wfSpeakerPlays(c) {
  return (c.mode || "inventory") === "inventory" && (c.command || "play") === "play";
}

/** El aviso sale en la ventana de alarma de los operadores (lo de siempre, también sin destino). */
function wfShowsPopup(c) {
  return (c.destination || "popup") !== "screens";
}

/** El aviso sale en las pantallas de alarma de las casetas. */
function wfShowsOnScreens(c) {
  return c.destination === "screens" || c.destination === "both";
}

// Cómo se pinta la configuración de cada acción. `when` oculta campos según
// otro campo (el modo del parlante, el tipo de autenticación...).
const WF_FIELDS = {
  snapshot: [
    { k: "channelIds", t: "cameras", label: "Cámaras a capturar" },
    { k: "count", t: "number", label: "Fotos por cámara", def: 1, min: 1, max: 5 },
    { k: "intervalSeconds", t: "number", label: "Segundos entre fotos", def: 2, min: 0, max: 30 },
  ],
  email: [
    { k: "to", t: "text", label: "Para", placeholder: "guardia@empresa.cl, jefe@empresa.cl" },
    { k: "cc", t: "text", label: "Copia (opcional)" },
    { k: "subject", t: "text", label: "Asunto", def: "{tipo}: {evento}" },
    { k: "body", t: "textarea", label: "Mensaje", def: WF_EMAIL_BODY, rows: 8 },
    { k: "attachSnapshots", t: "check", label: "Adjuntar las fotos capturadas antes en esta ejecución", def: true },
  ],
  ftp: [
    { k: "host", t: "text", label: "Servidor FTP", placeholder: "ftp.empresa.cl" },
    { k: "port", t: "number", label: "Puerto", def: 21, min: 1, max: 65535 },
    { k: "security", t: "select", label: "Seguridad", def: "None",
      options: [["None", "Sin cifrar (FTP)"], ["Explicit", "FTPS explícito (AUTH TLS)"], ["Implicit", "FTPS implícito"]] },
    { k: "username", t: "text", label: "Usuario" },
    { k: "secret", t: "password", label: "Contraseña" },
    { k: "remoteDirectory", t: "text", label: "Carpeta remota", def: "/alarmas/{fecha}",
      help: "Admite marcas: se crea sola si no existe." },
    { k: "includeReport", t: "check", label: "Subir también un informe de texto con el evento" },
    { k: "allowInvalidCertificate", t: "check", label: "Aceptar certificado no verificable (FTPS con certificado propio)" },
  ],
  http: [
    { k: "method", t: "select", label: "Método", def: "POST",
      options: [["POST", "POST"], ["GET", "GET"], ["PUT", "PUT"], ["PATCH", "PATCH"], ["DELETE", "DELETE"]], rerender: true },
    { k: "url", t: "text", label: "URL", placeholder: "https://sistema.empresa.cl/api/alarmas" },
    { k: "contentType", t: "text", label: "Tipo de contenido", def: "application/json",
      when: (c) => (c.method || "POST") !== "GET" },
    { k: "body", t: "textarea", label: "Cuerpo", rows: 5, when: (c) => (c.method || "POST") !== "GET",
      placeholder: '{"equipo":"{equipo}","evento":"{evento}","hora":"{fechahora}"}' },
    { k: "headers", t: "textarea", label: "Cabeceras (una por línea: Nombre: valor)", rows: 2 },
    { k: "auth", t: "select", label: "Autenticación", def: "None",
      options: [["None", "Ninguna"], ["Basic", "Básica"], ["Digest", "Digest"]], rerender: true },
    { k: "username", t: "text", label: "Usuario", when: (c) => (c.auth || "None") !== "None" },
    { k: "secret", t: "password", label: "Contraseña", when: (c) => (c.auth || "None") !== "None" },
    { k: "timeoutSeconds", t: "number", label: "Tiempo máximo de espera (s)", def: 15, min: 1, max: 120 },
    { k: "allowInvalidCertificate", t: "check", label: "Aceptar certificado no verificable (HTTPS)" },
  ],
  speaker: [
    { k: "mode", t: "select", label: "Tipo de parlante", def: "inventory", rerender: true,
      options: [["inventory", "Parlantes del inventario (módulo Parlantes IP)"], ["hikvision", "Hikvision suelto (audio bidireccional ISAPI)"],
        ["axis", "Axis (clip por VAPIX)"], ["http", "Otra marca (URL que gatilla el mensaje)"]] },
    // --- Parlantes del inventario ---
    { k: "speakerIds", t: "speakers", label: "Parlantes", when: (c) => (c.mode || "inventory") === "inventory",
      help: "Se marcan uno o varios; si elige varios, los sonidos del servidor suenan sincronizados." },
    { k: "group", t: "text", label: "O bien todos los parlantes del grupo", placeholder: "Bodega, Perímetro…",
      when: (c) => (c.mode || "inventory") === "inventory",
      help: "Nombre de grupo tal como está en el mantenedor de parlantes; se suma a los marcados arriba." },
    { k: "command", t: "select", label: "Orden", def: "play", rerender: true,
      when: (c) => (c.mode || "inventory") === "inventory",
      options: [["play", "Reproducir"], ["stop", "Detener lo que esté sonando"]],
      help: "«Detener» es la pareja de un sonido en bucle: por ejemplo, botón presionado (sensor interrumpido) → reproducir en bucle; botón soltado (sensor restablecido) → detener." },
    { k: "source", t: "select", label: "Qué reproducir", def: "server", rerender: true,
      when: (c) => wfSpeakerPlays(c),
      options: [["server", "Sonido del servidor (Sonidos)"], ["library", "Audio de la biblioteca del parlante"], ["tts", "Texto leído en voz alta (TTS del parlante)"]] },
    { k: "audio", t: "audio", label: "Sonido a reproducir",
      when: (c) => wfSpeakerPlays(c) && (c.source || "server") === "server" },
    { k: "libraryName", t: "text", label: "Nombre del audio en el parlante", placeholder: "Siren, RestrictAreaKeepAway…",
      when: (c) => wfSpeakerPlays(c) && c.source === "library",
      help: "Se busca por nombre en la biblioteca de cada parlante (con o sin extensión)." },
    { k: "text", t: "textarea", label: "Texto a leer (máximo 100 caracteres)", rows: 2,
      placeholder: "Atención: intruso detectado en {zona}. Retírese del lugar.",
      when: (c) => wfSpeakerPlays(c) && c.source === "tts",
      help: "Admite las marcas {zona}, {panel}, {evento}… El parlante genera la voz y la guarda; un mismo texto se reutiliza." },
    { k: "language", t: "select", label: "Idioma de la voz", def: "spanish",
      when: (c) => wfSpeakerPlays(c) && c.source === "tts",
      options: [["spanish", "Español"], ["english", "Inglés"], ["brazilianPortuguese", "Portugués (Brasil)"], ["french", "Francés"]] },
    { k: "voice", t: "select", label: "Voz", def: "female",
      when: (c) => wfSpeakerPlays(c) && c.source === "tts",
      options: [["female", "Femenina"], ["male", "Masculina"]] },
    { k: "repeat", t: "number", label: "Repeticiones", def: 1, min: 0, max: 5,
      when: (c) => wfSpeakerPlays(c) && (c.source || "server") === "server",
      help: "0 = en bucle hasta que una acción «Detener» o el operador lo corte." },
    { k: "setVolume", t: "check", label: "Fijar el volumen del parlante antes de reproducir", rerender: true,
      when: (c) => wfSpeakerPlays(c) },
    { k: "volume", t: "number", label: "Volumen (%)", def: 80, min: 0, max: 100,
      when: (c) => wfSpeakerPlays(c) && !!c.setVolume,
      help: "Queda fijado en el equipo (no vuelve solo al valor anterior)." },
    // --- Equipo suelto por dirección (modos heredados) ---
    { k: "host", t: "text", label: "Dirección del parlante", placeholder: "192.168.1.90", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "port", t: "number", label: "Puerto", def: 80, min: 1, max: 65535, when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "useHttps", t: "check", label: "Usar HTTPS", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "username", t: "text", label: "Usuario", def: "admin", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "secret", t: "password", label: "Contraseña", when: (c) => !["inventory", "http"].includes(c.mode || "inventory") },
    { k: "audio", t: "audio", label: "Sonido a reproducir", when: (c) => c.mode === "hikvision" },
    { k: "channel", t: "number", label: "Canal de audio", def: 1, min: 1, max: 8, when: (c) => c.mode === "hikvision" },
    { k: "clip", t: "number", label: "Número de clip en el parlante", def: 1, min: 0, max: 99, when: (c) => c.mode === "axis" },
    { k: "volume", t: "number", label: "Volumen (%)", def: 50, min: 0, max: 100, when: (c) => c.mode === "axis" },
    { k: "repeat", t: "number", label: "Repeticiones", def: 1, min: 1, max: 5, when: (c) => ["hikvision", "axis"].includes(c.mode) },
    { k: "url", t: "text", label: "URL", placeholder: "http://192.168.1.90/api/play?msg=1", when: (c) => c.mode === "http" },
    { k: "method", t: "select", label: "Método", def: "GET", options: [["GET", "GET"], ["POST", "POST"]], when: (c) => c.mode === "http" },
  ],
  notify: [
    { k: "title", t: "text", label: "Título", def: "{tipo}: {equipo}" },
    { k: "message", t: "text", label: "Mensaje", def: "{evento} · {fechahora}",
      help: "Además de las marcas del disparador admite {ubicacion} (dónde está, según Recursos) y {consignas} (lo que debe hacer el operador con ese recurso)." },
    { k: "severity", t: "select", label: "Importancia", def: "Warning",
      options: [["Critical", "Crítica"], ["Warning", "Advertencia"], ["Info", "Informativa"]] },
    { k: "destination", t: "select", label: "Dónde se muestra", def: "popup", rerender: true,
      options: [["popup", "Ventana de alarma de los operadores"], ["screens", "Pantallas de alarma de las casetas"],
        ["both", "Ventana de alarma y pantallas de alarma"]],
      help: "Es un solo aviso: si un guardia lo marca como visto en una pantalla de la caseta, se cierra en la ventana de alarma de los operadores, y si un operador lo confirma, la pantalla deja de sonar." },
    { k: "screenIds", t: "screens", label: "Pantallas de alarma",
      when: (c) => wfShowsOnScreens(c),
      help: "Sin ninguna marcada, el aviso va a todas las pantallas aprobadas. Si no hay ninguna disponible, sale en la ventana de alarma para que nadie se lo pierda." },
    { k: "userIds", t: "users", label: "Destinatarios", when: (c) => wfShowsPopup(c),
      help: "Sin ninguno marcado, el aviso llega a todos los operadores conectados. Quien atiende alertas ve las dirigidas a él o a todos; el administrador las ve todas." },
    { k: "attachSnapshot", t: "check", label: "Mostrar la foto capturada en el aviso", def: true },
    { k: "requireAck", t: "check", label: "Exigir que un operador se dé por enterado", def: true,
      help: "El aviso queda en pantalla y en la lista de alertas hasta que alguien lo confirme; se registra quién y cuándo." },
    { k: "channelIds", t: "cameras", label: "Cámaras del video en vivo de la ventana de alarma", when: (c) => wfShowsPopup(c),
      help: "Si no marca ninguna, se usan las que capturaron foto en la misma ejecución y, si tampoco hubo fotos, las cámaras asociadas al recurso que la disparó (su ficha en Recursos)." },
    { k: "sound", t: "audio", label: "Alarma sonora", optional: true, system: true,
      def: "", rerender: true,
      help: "Los sonidos son los mismos que se cargan con el botón «Sonidos». En las pantallas de alarma suena este (el pitido del sistema usa el tono de la pantalla) y después el recordatorio que se configura en Pantallas de alarma." },
    { k: "soundRepeat", t: "number", label: "Repeticiones del sonido en el equipo del operador", def: 1, min: 0, max: 5,
      when: (c) => !!c.sound && wfShowsPopup(c),
      help: "0 = suena sin parar hasta que un operador confirme la alerta, la silencie o cierre la ventana. Solo si el aviso exige que un operador se dé por enterado: sin eso, 0 suena una vez." },
  ],
  door: [
    { k: "command", t: "select", label: "Orden", def: "Open",
      options: [["Open", "Abrir (pulso: abre y se vuelve a cerrar)"], ["RemainOpen", "Mantener abierta hasta nueva orden"],
        ["RemainLocked", "Bloquear (no entra nadie, ni con credencial)"], ["Close", "Cerrar / volver a normal"]] },
    { k: "doorIds", t: "doors", label: "Puertas" },
  ],
  panel: [
    { k: "panelId", t: "panel", label: "Panel de alarma", rerender: true },
    { k: "areaNumber", t: "area", label: "Área", def: 0 },
    { k: "command", t: "select", label: "Orden", def: "Arm", rerender: true,
      options: [["Arm", "Armar"], ["Disarm", "Desarmar"], ["ClearAlarm", "Borrar / silenciar la alarma"]] },
    { k: "mode", t: "select", label: "Modo de armado", def: "Away", when: (c) => (c.command || "Arm") === "Arm",
      options: [["Away", "Total (fuera de casa)"], ["Stay", "Parcial (en casa / perimetral)"]] },
  ],
  "ptz-preset": [
    { k: "channelId", t: "ptzcamera", label: "Cámara PTZ" },
    { k: "preset", t: "number", label: "Preset (1–300)", def: 1, min: 1, max: 300,
      help: "El preset se guarda en la propia cámara (desde la vista en vivo del cliente o su web)." },
  ],
  relay: [
    { k: "relayKeys", t: "relays", label: "Relés",
      help: "Salidas de los módulos de E/S (Dispositivos → Módulos de E/S); se pueden marcar varias." },
    { k: "command", t: "select", label: "Orden", def: "Pulse", rerender: true,
      options: [["Pulse", "Pulso: encender y soltar (barrera, portón)"], ["On", "Encender y dejar encendido"], ["Off", "Apagar"],
        ["Toggle", "Alternar"], ["Blink", "Parpadear (baliza)"]],
      help: "«Apagar» también corta un pulso o un parpadeo en curso: es la pareja de «Encender» y de un parpadeo sin fin." },
    { k: "seconds", t: "number", label: "Duración del pulso (segundos)", def: 1, min: 0.1, max: 3600, step: 0.1,
      when: (c) => (c.command || "Pulse") === "Pulse",
      help: "Lo que el relé queda energizado. Para la orden de subir de una barrera suele bastar 1 s." },
    { k: "blinkSeconds", t: "number", label: "Parpadear durante (segundos; 0 = hasta otra orden)", def: 10, min: 0, max: 3600,
      when: (c) => c.command === "Blink" },
    { k: "intervalMs", t: "number", label: "Alternar cada (ms)", def: 500, min: 100, max: 60000, step: 50,
      when: (c) => c.command === "Blink",
      help: "Parpadear seguido desgasta los contactos de un relé: para una baliza permanente conviene una con destellador propio." },
  ],
  "vehicle-access": [
    { k: "direction", t: "select", label: "Sentido", def: "Entry", options: [["Entry", "Ingreso"], ["Exit", "Salida"]] },
    { k: "accessPoint", t: "text", label: "Nombre del acceso", def: "Acceso principal",
      help: "Así aparece en el registro de accesos vehiculares y en sus reportes; use el mismo nombre en la automatización de ingreso y en la de salida." },
    { k: "result", t: "select", label: "Resultado que se anota", def: "AutoOpened",
      options: [["AutoOpened", "Apertura automática"], ["OutsideSchedule", "Fuera de horario: abre el guardia"],
        ["Rejected", "Rechazado (no registrado o no autorizado)"], ["OpenFailed", "Falló la apertura"], ["Logged", "Solo registrar"]],
      help: "Póngala al final de cada rama del diagrama con el resultado de esa rama; en la rama de error de los relés, «Falló la apertura». Quién es (persona y vehículo) lo toma del padrón." },
  ],
};

// --- Vista previa de sonidos ------------------------------------------------
// Suena en el navegador del que configura (no en el equipo del operador): el
// archivo se pide a la API con el token en la URL, que es lo que admite un
// elemento <audio>. Solo una reproducción a la vez.
let wfPreview = null;

function wfStopPreview() {
  if (!wfPreview) return;
  const { audio, button } = wfPreview;
  wfPreview = null;
  try { audio.pause(); } catch { /* ya terminó */ }
  if (button) button.textContent = button.dataset.playLabel || "▶";
}

function wfTogglePreview(name, button) {
  const playing = wfPreview && wfPreview.name === name;
  wfStopPreview();
  if (playing || !name) return;   // segundo clic = detener

  const audio = new Audio(
    `/api/workflows/audio/${encodeURIComponent(name)}/file?access_token=${encodeURIComponent(Api.token || "")}`);
  wfPreview = { audio, button, name };
  button.dataset.playLabel ??= button.textContent;
  button.textContent = "■";
  audio.addEventListener("ended", wfStopPreview);
  audio.addEventListener("error", () => {
    wfStopPreview();
    toast("El navegador no pudo reproducir ese sonido.", true);
  });
  audio.play().catch(() => {
    wfStopPreview();
    toast("El navegador no pudo reproducir ese sonido.", true);
  });
}

// ---------------------------------------------------------------------------
// Listado
// ---------------------------------------------------------------------------

// Grupos: solo ordenan el listado (el motor no los mira). Lo contraído se
// recuerda en este navegador; los nombres existentes los reutiliza el editor.
const WF_COLLAPSED_KEY = "tcvms.workflows.collapsed";
const WF_NO_GROUP = "";
let wfGroupsCache = null;
const wfCollator = new Intl.Collator("es", { numeric: true, sensitivity: "base" });

function wfLoadCollapsed() {
  try { return new Set(JSON.parse(localStorage.getItem(WF_COLLAPSED_KEY) || "[]")); } catch { return new Set(); }
}
function wfSaveCollapsed(set) {
  try { localStorage.setItem(WF_COLLAPSED_KEY, JSON.stringify([...set])); } catch { /* modo privado */ }
}

async function renderWorkflows() {
  $("#page-title").textContent = "Automatizaciones";
  let workflows, catalog, alerts = null;
  try {
    [workflows, catalog] = await Promise.all([Api.get("/api/workflows"), wfCatalog()]);
    alerts = await Api.get("/api/workflows/alerts?pending=true&take=1").catch(() => null);
  } catch (err) {
    $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }

  const isAdmin = Perms.can("workflows.manage");
  const actionLabel = (key) => catalog.actions.find((a) => a.key === key)?.label ?? key;
  const triggerLabel = (key) => catalog.triggers.find((t) => t.key === key)?.label ?? key;
  const stepsOf = (w) => (w.graph?.nodes || []).filter((n) => n.kind !== "trigger").length;
  const groupOf = (w) => w.group || WF_NO_GROUP;
  const groupNames = [...new Set(workflows.map(groupOf).filter((g) => g))].sort(wfCollator.compare);
  wfGroupsCache = groupNames;
  const ordered = [...groupNames, ...(workflows.some((w) => !w.group) ? [WF_NO_GROUP] : [])];
  const collapsed = wfLoadCollapsed();
  const cols = isAdmin ? 11 : 10;
  let filterGroup = null;
  try { filterGroup = sessionStorage.getItem("wf-group"); } catch { /* sin almacenamiento */ }
  if (filterGroup !== null && !ordered.includes(filterGroup)) filterGroup = null;

  const rowHtml = (w) => `
    <tr data-id="${w.id}" data-search="${esc([w.name, w.description || "", w.group || "", triggerLabel(w.triggerType), wfConditionsText(w),
      ...w.actions.map((a) => actionLabel(a.type)), w.enabled ? "activa" : "pausada"].join(" · ").toLowerCase())}">
      ${isAdmin ? `<td class="wf-check"><input type="checkbox" class="wf-sel" aria-label="Seleccionar «${esc(w.name)}»"></td>` : ""}
      <td>${esc(w.name)}${w.description ? `<div class="muted" style="font-size:11px">${esc(w.description)}</div>` : ""}</td>
      <td>${esc(triggerLabel(w.triggerType))}</td>
      <td class="muted" style="font-size:12px;max-width:260px">${esc(wfConditionsText(w))}</td>
      <td><div class="chip-row" style="margin:0">${w.actions.map((a) =>
        `<span class="chip" ${a.enabled ? "" : 'style="opacity:.5"'}>${esc(actionLabel(a.type))}</span>`).join("")}</div></td>
      <td class="muted">${stepsOf(w)}</td>
      <td class="muted">${w.cooldownSeconds > 0 ? `${w.cooldownSeconds} s` : "—"}</td>
      <td>${w.runCount}</td>
      <td class="muted">${w.lastRunAt ? wfDate(w.lastRunAt) : "—"}</td>
      <td>${w.enabled ? `<span class="tag on">Activa</span>` : w.disabledByLicense ? licenseWaitTag(w).trim() : `<span class="tag off">Pausada</span>`}</td>
      <td class="row-actions">
        ${isAdmin ? `<button class="btn ghost btn-wf-test" title="Ejecuta las acciones de verdad con un evento de ejemplo">Probar</button>
        <button class="btn ghost btn-wf-edit">Abrir</button>
        <button class="btn ghost btn-wf-duplicate" title="Crea una copia pausada y la abre en el editor para cambiar lo que haga falta">Duplicar</button>
        <button class="btn danger btn-wf-delete">Eliminar</button>` : `<button class="btn ghost btn-wf-edit">Ver</button>`}
      </td>
    </tr>`;

  const groupHtml = (g) => {
    const items = workflows.filter((w) => groupOf(w) === g).sort((a, b) => wfCollator.compare(a.name, b.name));
    const active = items.filter((w) => w.enabled).length;
    return `
      <tbody class="wf-group ${collapsed.has(g) ? "collapsed" : ""}" data-group="${esc(g)}">
        <tr class="wf-group-head"><td colspan="${cols}">
          <div class="wf-group-bar">
            ${isAdmin ? `<input type="checkbox" class="wf-sel-group" title="Seleccionar todas las de este grupo">` : ""}
            <button type="button" class="wf-group-toggle" aria-expanded="${!collapsed.has(g)}">
              <span class="caret" aria-hidden="true"></span>
              <i class="mdi ${g ? "mdi-folder-outline" : "mdi-folder-remove-outline"}" aria-hidden="true"></i>
              <b>${g ? esc(g) : "Sin grupo"}</b>
              <span class="muted wf-group-count" data-total="${items.length}">${items.length} · ${active} activa${active === 1 ? "" : "s"}</span>
            </button>
            ${isAdmin && g ? `<span class="wf-group-actions">
              <button type="button" class="btn ghost small btn-wf-newin" title="Nueva automatización en este grupo">+ Nueva aquí</button>
              <button type="button" class="btn ghost small btn-wf-rename">Renombrar</button></span>` : ""}
          </div>
        </td></tr>
        ${items.map(rowHtml).join("")}
      </tbody>`;
  };

  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <div class="row-actions">
        ${isAdmin ? `<button class="btn ghost" id="btn-wf-audio">Sonidos</button>
        <button class="btn" id="btn-wf-new">Nueva automatización</button>` : ""}
      </div>
    </div>
    ${!catalog.smtpConfigured ? `<div class="info-box" style="margin-bottom:12px">
      Aún no hay <b>servidor de correo</b> configurado: las acciones de correo fallarán hasta que se configure${isAdmin ? ` en <a href="#/mail">Sistema → Correo saliente</a>` : ""}.
    </div>` : ""}
    ${alerts?.pending > 0 ? `<div class="error-box" style="margin-bottom:12px">
      <b>${alerts.pending} alerta(s) sin confirmar.</b> <a href="#/workflow-alerts">Ver alertas</a>
    </div>` : ""}
    ${workflows.length === 0 ? `
      <div class="info-box">
        Todavía no hay automatizaciones. ${isAdmin
          ? "Con <b>Nueva automatización</b> se abre el editor de diagrama: parta de un disparador (una alarma de panel, una cámara caída, una patente leída, un acceso, una analítica de video, una hora del día o una llamada externa), agregue condiciones sí/no y conecte acciones: foto, correo, FTP, HTTP, parlante, aviso a los operadores, abrir puerta, armar/desarmar, mover un PTZ."
          : "Un administrador debe crearlas."}
      </div>` : `
      <div class="wf-listbar">
        <input type="search" id="wf-search" class="wf-filter" autocomplete="off"
               placeholder="Buscar por nombre, grupo, disparador, filtro, acción o estado…"
               title="Escriba parte del texto; se buscan todas las palabras en cualquier columna">
        <select id="wf-group-filter" title="Mostrar solo un grupo">
          <option value="*">Todos los grupos</option>
          ${ordered.map((g) => `<option value="${esc(g)}" ${filterGroup === g ? "selected" : ""}>${g ? esc(g) : "Sin grupo"}</option>`).join("")}
        </select>
        <span class="muted" id="wf-search-count" style="font-size:12px"></span>
        <span class="wf-listbar-end">
          <button type="button" class="btn ghost small" id="wf-expand" title="Desplegar todos los grupos">Desplegar todo</button>
          <button type="button" class="btn ghost small" id="wf-collapse" title="Contraer todos los grupos">Contraer todo</button>
          ${isAdmin ? `<button type="button" class="btn ghost small" id="wf-move" disabled>Mover a grupo…</button>` : ""}
        </span>
      </div>
      <div class="table-scroll"><table class="grid wf-table" id="wf-table">
        <thead><tr>
          ${isAdmin ? `<th class="wf-check"><input type="checkbox" id="wf-sel-all" title="Seleccionar las visibles"></th>` : ""}
          <th>Nombre</th><th>Cuándo</th><th>Filtro</th><th>Qué hace</th>
          <th>Pasos</th><th>Espera</th><th>Ejecuciones</th><th>Última</th><th>Estado</th><th></th>
        </tr></thead>
        ${ordered.map(groupHtml).join("")}
      </table></div>`}`;

  // Filtro: todas las palabras deben aparecer en la fila; el grupo elegido
  // acota. Mientras se busca, los grupos se muestran desplegados.
  const search = $("#wf-search");
  const apply = () => {
    if (!search) return;
    const words = search.value.trim().toLowerCase().split(/\s+/).filter((w) => w.length > 0);
    const only = $("#wf-group-filter").value;
    let visible = 0;
    $$("#wf-table tbody.wf-group").forEach((tb) => {
      const inGroup = only === "*" || tb.dataset.group === only;
      let shown = 0;
      tb.querySelectorAll("tr[data-id]").forEach((tr) => {
        const match = inGroup && words.every((w) => (tr.dataset.search || "").includes(w));
        tr.hidden = !match;
        if (match) shown++;
      });
      tb.hidden = shown === 0;
      tb.classList.toggle("searching", words.length > 0);
      const count = tb.querySelector(".wf-group-count");
      if (words.length) count.textContent = `${shown} de ${count.dataset.total}`;
      visible += shown;
    });
    $("#wf-table .wf-filter-empty")?.remove();
    if (visible === 0)
      $("#wf-table").insertAdjacentHTML("beforeend",
        `<tbody class="wf-filter-empty"><tr><td colspan="${cols}" class="muted">Ninguna automatización coincide con el filtro.</td></tr></tbody>`);
    $("#wf-search-count").textContent = words.length === 0 && only === "*"
      ? `${workflows.length} automatización${workflows.length === 1 ? "" : "es"} en ${ordered.length} grupo${ordered.length === 1 ? "" : "s"}`
      : `${visible} de ${workflows.length}`;
    try {
      sessionStorage.setItem("wf-search", search.value);
      if (only === "*") sessionStorage.removeItem("wf-group"); else sessionStorage.setItem("wf-group", only);
    } catch { /* sin almacenamiento */ }
    wfUpdateSelection();
  };
  if (search) {
    try { search.value = sessionStorage.getItem("wf-search") || ""; } catch { /* sin almacenamiento */ }
    search.addEventListener("input", apply);
    $("#wf-group-filter").addEventListener("change", apply);
    apply();
  }

  // Contraer / desplegar grupos.
  const setCollapsed = (tb, on) => {
    tb.classList.toggle("collapsed", on);
    tb.querySelector(".wf-group-toggle").setAttribute("aria-expanded", String(!on));
    if (on) collapsed.add(tb.dataset.group); else collapsed.delete(tb.dataset.group);
  };
  $$("#wf-table .wf-group-toggle").forEach((b) => b.addEventListener("click", () => {
    const tb = b.closest("tbody");
    setCollapsed(tb, !tb.classList.contains("collapsed"));
    wfSaveCollapsed(collapsed);
  }));
  $("#wf-expand")?.addEventListener("click", () => { $$("#wf-table tbody.wf-group").forEach((tb) => setCollapsed(tb, false)); wfSaveCollapsed(collapsed); });
  $("#wf-collapse")?.addEventListener("click", () => { $$("#wf-table tbody.wf-group").forEach((tb) => setCollapsed(tb, true)); wfSaveCollapsed(collapsed); });

  // Selección para mover de grupo.
  const visibleRows = () => $$("#wf-table tbody.wf-group:not([hidden]) tr[data-id]:not([hidden])");
  function wfUpdateSelection() {
    const move = $("#wf-move");
    if (!move) return;
    const n = $$("#wf-table .wf-sel:checked").length;
    move.disabled = n === 0;
    move.textContent = n ? `Mover a grupo… (${n})` : "Mover a grupo…";
    $$("#wf-table tbody.wf-group").forEach((tb) => {
      const boxes = [...tb.querySelectorAll("tr[data-id]:not([hidden]) .wf-sel")];
      const head = tb.querySelector(".wf-sel-group");
      if (!head) return;
      head.checked = boxes.length > 0 && boxes.every((c) => c.checked);
      head.indeterminate = !head.checked && boxes.some((c) => c.checked);
    });
  }
  $$("#wf-table .wf-sel").forEach((c) => c.addEventListener("change", wfUpdateSelection));
  $$("#wf-table .wf-sel-group").forEach((c) => c.addEventListener("change", () => {
    c.closest("tbody").querySelectorAll("tr[data-id]:not([hidden]) .wf-sel").forEach((box) => { box.checked = c.checked; });
    wfUpdateSelection();
  }));
  $("#wf-sel-all")?.addEventListener("change", (e) => {
    visibleRows().forEach((tr) => { tr.querySelector(".wf-sel").checked = e.target.checked; });
    wfUpdateSelection();
  });
  $("#wf-move")?.addEventListener("click", () => {
    const ids = $$("#wf-table .wf-sel:checked").map((c) => Number(c.closest("tr").dataset.id));
    wfGroupModal({
      title: `Mover ${ids.length} automatización${ids.length === 1 ? "" : "es"} a un grupo`,
      hint: "Escriba un grupo existente o uno nuevo. Vacío = sin grupo.",
      value: "", groups: groupNames, ok: "Mover",
      submit: (group) => Api.post("/api/workflows/group", { ids, group }),
      done: (r) => toast(r.group ? `${r.moved} automatización(es) movidas a «${r.group}».` : `${r.moved} automatización(es) quedaron sin grupo.`),
    });
  });
  $$("#wf-table .btn-wf-rename").forEach((b) => b.addEventListener("click", () => {
    const from = b.closest("tbody").dataset.group;
    wfGroupModal({
      title: `Renombrar el grupo «${from}»`,
      hint: "Si lo deja vacío, el grupo se disuelve: sus automatizaciones quedan sin grupo.",
      value: from, groups: groupNames.filter((g) => g !== from), ok: "Guardar",
      submit: (to) => Api.post("/api/workflows/group/rename", { from, to }),
      done: (r) => toast(r.group ? `Grupo renombrado a «${r.group}».` : "Grupo disuelto."),
    });
  }));
  $$("#wf-table .btn-wf-newin").forEach((b) => b.addEventListener("click", () => {
    location.hash = `#/workflows/edit?group=${encodeURIComponent(b.closest("tbody").dataset.group)}`;
  }));

  $("#btn-wf-new")?.addEventListener("click", () => {
    const only = $("#wf-group-filter")?.value;
    location.hash = only && only !== "*" && only !== WF_NO_GROUP ? `#/workflows/edit?group=${encodeURIComponent(only)}` : "#/workflows/edit";
  });
  $("#btn-wf-audio")?.addEventListener("click", () => { location.hash = "#/sounds"; });
  $$("#view .btn-wf-edit").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    location.hash = `#/workflows/edit?id=${id}`;
  }));
  $$("#view .btn-wf-duplicate").forEach((b) => b.addEventListener("click", async (e) => {
    const button = e.target;
    const id = Number(button.closest("tr").dataset.id);
    button.disabled = true;
    try {
      const copy = await Api.post(`/api/workflows/${id}/duplicate`);
      toast(`Copia creada (pausada): «${copy.name}». Cambie lo que necesite, actívela y guarde.`);
      location.hash = `#/workflows/edit?id=${copy.id}`;
    } catch (err) {
      toast(err.error, true);
      button.disabled = false;
    }
  }));
  $$("#view .btn-wf-delete").forEach((b) => b.addEventListener("click", async (e) => {
    const id = Number(e.target.closest("tr").dataset.id);
    const workflow = workflows.find((w) => w.id === id);
    if (!confirm(`¿Eliminar la automatización "${workflow.name}"? El historial de ejecuciones se conserva.`)) return;
    try {
      await Api.delete(`/api/workflows/${id}`);
      toast("Automatización eliminada.");
      renderWorkflows();
    } catch (err) { toast(err.error, true); }
  }));
  $$("#view .btn-wf-test").forEach((b) => b.addEventListener("click", async (e) => {
    const button = e.target;
    const id = Number(button.closest("tr").dataset.id);
    if (!confirm("La prueba ejecuta las acciones DE VERDAD (envía el correo, sube el archivo, abre la puerta, hace sonar el parlante) con un evento de ejemplo. ¿Continuar?")) return;
    button.disabled = true;
    button.textContent = "Probando…";
    try {
      const run = await Api.post(`/api/workflows/${id}/test`);
      toast(run.success ? "Prueba ejecutada correctamente." : "La prueba terminó con errores.", !run.success);
      wfRunModal(run);
    } catch (err) {
      toast(err.error, true);
    } finally {
      button.disabled = false;
      button.textContent = "Probar";
    }
  }));
}

/** Modal de un nombre de grupo (con los existentes como sugerencias). */
function wfGroupModal({ title, hint, value, groups, ok, submit, done }) {
  openModal(`
    <h3>${esc(title)}</h3>
    <div class="field"><label>Grupo</label>
      <input id="wfg-name" maxlength="64" list="wfg-list" value="${esc(value)}" placeholder="Ej.: Casa matriz · Accesos" autocomplete="off">
      <datalist id="wfg-list">${groups.map((g) => `<option value="${esc(g)}">`).join("")}</datalist>
      <div class="field-hint muted">${esc(hint)}</div></div>
    <div class="error-box" id="wfg-err" hidden></div>
    <div class="modal-actions">
      <button type="button" class="btn ghost" id="wfg-cancel">Cancelar</button>
      <button type="button" class="btn" id="wfg-ok">${esc(ok)}</button>
    </div>`);
  const input = $("#wfg-name");
  input.focus();
  input.select();
  $("#wfg-cancel").onclick = closeModal;
  const send = async () => {
    $("#wfg-ok").disabled = true;
    try {
      const result = await submit(input.value.trim());
      closeModal();
      done(result);
      renderWorkflows();
    } catch (err) {
      $("#wfg-err").textContent = err.error;
      $("#wfg-err").hidden = false;
      $("#wfg-ok").disabled = false;
    }
  };
  $("#wfg-ok").onclick = send;
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") send(); });
}

// ---------------------------------------------------------------------------
// Automatización → Alertas y → Ejecuciones (páginas propias: con muchas
// automatizaciones el listado ya no las deja a la vista)
// ---------------------------------------------------------------------------
const WF_PAGE = 50;

async function renderWorkflowAlerts() {
  $("#page-title").textContent = "Alertas de automatizaciones";
  let pendingOnly = false;
  try { pendingOnly = sessionStorage.getItem("wf-alerts-pending") === "1"; } catch { /* sin almacenamiento */ }
  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <label class="checkbox-row" style="margin:0"><input type="checkbox" id="wf-alerts-pending" ${pendingOnly ? "checked" : ""}> Solo sin confirmar</label>
    </div>
    <div id="wf-alerts"><div class="info-box">Cargando…</div></div>`;
  const state = { skip: 0 };
  const load = () => wfLoadAlerts({ skip: state.skip, pendingOnly: $("#wf-alerts-pending").checked, onPage: (skip) => { state.skip = skip; load(); } });
  $("#wf-alerts-pending").addEventListener("change", (e) => {
    state.skip = 0;
    try { sessionStorage.setItem("wf-alerts-pending", e.target.checked ? "1" : "0"); } catch { /* sin almacenamiento */ }
    load();
  });
  load();
  clearInterval(workflowsTimer);
  workflowsTimer = setInterval(() => { if (location.hash.startsWith("#/workflow-alerts") && state.skip === 0) load(); }, 10000);
}

async function renderWorkflowRuns() {
  $("#page-title").textContent = "Ejecuciones de automatizaciones";
  let workflows = [];
  try { workflows = await Api.get("/api/workflows"); } catch { /* el filtro queda solo con «Todas» */ }
  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      <div class="row-actions" style="align-items:center;gap:12px">
        <select id="wf-runs-workflow" title="Solo las de una automatización">
          <option value="">Todas las automatizaciones</option>
          ${[...workflows].sort((a, b) => wfCollator.compare(a.name, b.name)).map((w) =>
            `<option value="${w.id}">${esc(w.group ? `${w.group} · ${w.name}` : w.name)}</option>`).join("")}
        </select>
        <label class="checkbox-row" style="margin:0"><input type="checkbox" id="wf-runs-errors"> Solo con errores</label>
      </div>
    </div>
    <div id="wf-runs"><div class="info-box">Cargando…</div></div>`;
  const state = { skip: 0 };
  const load = () => wfLoadRuns({
    skip: state.skip, workflowId: Number($("#wf-runs-workflow").value) || null, onlyErrors: $("#wf-runs-errors").checked,
    onPage: (skip) => { state.skip = skip; load(); },
  });
  $("#wf-runs-workflow").addEventListener("change", () => { state.skip = 0; load(); });
  $("#wf-runs-errors").addEventListener("change", () => { state.skip = 0; load(); });
  load();
  clearInterval(workflowsTimer);
  workflowsTimer = setInterval(() => { if (location.hash.startsWith("#/workflow-runs") && state.skip === 0) load(); }, 10000);
}

/** Paginador «Anterior / Siguiente» bajo una tabla. */
function wfPager(total, skip, take, label, onPage) {
  const from = total ? skip + 1 : 0, to = Math.min(skip + take, total);
  const html = `<div class="audit-pager"><span class="muted">${from}–${to} de ${total} ${label}</span>
    <span class="row-actions"><button class="btn ghost small" data-page="prev" ${skip <= 0 ? "disabled" : ""}>« Anterior</button>
    <button class="btn ghost small" data-page="next" ${to >= total ? "disabled" : ""}>Siguiente »</button></span></div>`;
  return { html, wire: (root) => {
    root.querySelector('[data-page="prev"]')?.addEventListener("click", () => onPage(Math.max(0, skip - take)));
    root.querySelector('[data-page="next"]')?.addEventListener("click", () => onPage(skip + take));
  } };
}

/// Resumen legible del filtro del disparador (columna del listado).
function wfConditionsText(workflow) {
  const text = wfeConditionsSummary(workflow.conditions || {}, workflow.triggerType);
  return text || "Cualquier evento";
}

function wfDate(value) {
  const date = new Date(value);
  return date.toLocaleString("es-CL", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

// ---------------------------------------------------------------------------
// Alertas y acuse de recibo
// ---------------------------------------------------------------------------

function wfAgo(from, to) {
  const seconds = Math.max(0, Math.round((new Date(to) - new Date(from)) / 1000));
  if (seconds < 60) return `${seconds} s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ${seconds % 60} s`;
  return `${Math.floor(seconds / 3600)} h ${Math.floor((seconds % 3600) / 60)} min`;
}

async function wfLoadAlerts({ skip = 0, pendingOnly = false, onPage = null } = {}) {
  const container = $("#wf-alerts");
  if (!container) return;
  let data;
  try { data = await Api.get(`/api/workflows/alerts?take=${WF_PAGE}&skip=${skip}${pendingOnly ? "&pending=true" : ""}`); }
  catch (err) { container.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  if (data.total === 0) {
    container.innerHTML = `<div class="info-box">${pendingOnly ? "No hay alertas sin confirmar." : "Todavía no se ha emitido ninguna alerta."}</div>`;
    return;
  }
  const pager = wfPager(data.total, skip, WF_PAGE, "alerta(s)", onPage || (() => {}));
  container.innerHTML = `
    ${data.pending > 0 ? `<div class="error-box" style="margin-bottom:10px">
      <b>${data.pending} alerta(s) sin confirmar.</b> Mientras nadie se dé por enterado quedan pendientes y a la vista.
    </div>` : ""}
    <div class="table-scroll"><table class="grid">
      <thead><tr>
        <th>Emitida</th><th>Alerta</th><th>Qué la disparó</th><th>Estado</th><th>Confirmó</th><th></th>
      </tr></thead>
      <tbody>
        ${data.items.map((a) => `
          <tr data-alert="${a.id}">
            <td class="muted">${wfDate(a.raisedAt)}</td>
            <td>${esc(a.title)}<div class="muted" style="font-size:11px">${esc(a.message)}</div>
              <div class="muted" style="font-size:11px">Para: ${a.recipients ? esc(a.recipients) : "todos los operadores"}</div></td>
            <td class="muted" style="max-width:280px">${esc(a.triggerSummary)}</td>
            <td>${!a.requiresAck ? `<span class="tag operator">Informativa</span>`
              : a.acknowledgedAt ? `<span class="tag on">Confirmada</span>`
              : `<span class="tag off">PENDIENTE</span>`}</td>
            <td class="muted">${a.acknowledgedAt
              ? `${esc(a.acknowledgedBy)} · ${wfDate(a.acknowledgedAt)}<div style="font-size:11px">a los ${wfAgo(a.raisedAt, a.acknowledgedAt)} · ${a.acknowledgedFrom === "client" ? "cliente de escritorio" : "panel web"}</div>`
              : "—"}</td>
            <td class="row-actions">
              ${a.imagePath ? `<button class="btn ghost btn-alert-photo" title="Ver la foto">Foto</button>` : ""}
              ${a.requiresAck && !a.acknowledgedAt ? `<button class="btn btn-alert-ack">Enterado</button>` : ""}
            </td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    ${pager.html}`;
  pager.wire(container);

  $$("#wf-alerts .btn-alert-ack").forEach((b) => b.addEventListener("click", async (e) => {
    const id = e.target.closest("tr").dataset.alert;
    b.disabled = true;
    try {
      const alert = await Api.post(`/api/workflows/alerts/${id}/ack`);
      toast(alert.acknowledgedBy === Api.username
        ? "Alerta confirmada; quedó registrado a su nombre."
        : `La alerta ya la había confirmado ${alert.acknowledgedBy}.`);
      wfLoadAlerts({ skip, pendingOnly, onPage });
    } catch (err) {
      toast(err.error, true);
      b.disabled = false;
    }
  }));
  $$("#wf-alerts .btn-alert-photo").forEach((b) => b.addEventListener("click", (e) => {
    const id = Number(e.target.closest("tr").dataset.alert);
    const alert = data.items.find((a) => a.id === id);
    const token = encodeURIComponent(Api.token || "");
    const url = `/api/workflows/files/${alert.imagePath.split("/").map(encodeURIComponent).join("/")}?access_token=${token}`;
    openModal(`
      <h3>${esc(alert.title)}</h3>
      <div class="muted" style="margin-bottom:10px">${esc(alert.message)}</div>
      <div class="wf-thumbs"><a href="${url}" target="_blank"><img src="${url}" alt="Captura"></a></div>
      <div class="modal-actions"><button class="btn ghost" type="button" id="wf-photo-close">Cerrar</button></div>`, "wider");
    $("#wf-photo-close").addEventListener("click", closeModal);
  }));
}

// ---------------------------------------------------------------------------
// Historial de ejecuciones
// ---------------------------------------------------------------------------

async function wfLoadRuns({ skip = 0, workflowId = null, onlyErrors = false, onPage = null } = {}) {
  const container = $("#wf-runs");
  if (!container) return;
  let data;
  try {
    data = await Api.get(`/api/workflows/runs?take=${WF_PAGE}&skip=${skip}` +
      `${workflowId ? `&workflowId=${workflowId}` : ""}${onlyErrors ? "&onlyErrors=true" : ""}`);
  }
  catch (err) { container.innerHTML = `<div class="error-box">${esc(err.error)}</div>`; return; }

  if (data.total === 0) {
    container.innerHTML = `<div class="info-box">${workflowId || onlyErrors ? "Ninguna ejecución coincide con el filtro." : "Aún no se ha ejecutado ninguna automatización."}</div>`;
    return;
  }
  const pager = wfPager(data.total, skip, WF_PAGE, "ejecución(es)", onPage || (() => {}));
  container.innerHTML = `
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>Fecha</th><th>Automatización</th><th>Qué la disparó</th><th>Pasos</th><th>Resultado</th><th>Origen</th></tr></thead>
      <tbody>
        ${data.items.map((r) => `
          <tr class="audit-row" data-run="${r.id}">
            <td class="muted">${wfDate(r.startedAt)}</td>
            <td>${esc(r.workflowName)}</td>
            <td class="muted" style="max-width:320px">${esc(r.triggerSummary)}</td>
            <td>${r.steps.length}${r.steps.some((s) => s.files?.length) ? ` <span class="chip">con fotos</span>` : ""}</td>
            <td>${r.success ? `<span class="tag on">OK</span>` : `<span class="tag off">Con errores</span>`}</td>
            <td class="muted">${esc(r.startedBy)}</td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    ${pager.html}`;
  pager.wire(container);

  $$("#wf-runs tr[data-run]").forEach((row) => row.addEventListener("click", async () => {
    try { wfRunModal(await Api.get(`/api/workflows/runs/${row.dataset.run}`)); }
    catch (err) { toast(err.error, true); }
  }));
}

function wfRunModal(run) {
  const token = encodeURIComponent(Api.token || "");
  openModal(`
    <h3>Ejecución de «${esc(run.workflowName)}»</h3>
    <div class="audit-detail-grid">
      <div class="muted">Fecha</div><div>${wfDate(run.startedAt)}</div>
      <div class="muted">Disparo</div><div>${esc(run.triggerSummary)}</div>
      <div class="muted">Origen</div><div>${esc(run.startedBy)}</div>
      <div class="muted">Resultado</div><div>${run.success ? `<span class="tag on">Todos los pasos se ejecutaron</span>` : `<span class="tag off">${esc(run.error || "Con errores")}</span>`}</div>
    </div>
    <div class="table-scroll"><table class="grid">
      <thead><tr><th>#</th><th>Paso</th><th>Resultado</th><th>Detalle</th><th>Duración</th></tr></thead>
      <tbody>
        ${run.steps.map((s) => `
          <tr>
            <td>${s.order}</td>
            <td>${esc(s.label)}</td>
            <td>${s.success ? `<span class="tag on">OK</span>` : `<span class="tag off">Error</span>`}</td>
            <td style="max-width:420px">${esc(s.detail || "")}</td>
            <td class="muted">${(s.elapsedMs / 1000).toFixed(1)} s</td>
          </tr>`).join("")}
      </tbody>
    </table></div>
    ${run.steps.some((s) => s.files?.length) ? `
      <div class="field" style="margin-top:12px"><label>Fotos capturadas</label>
        <div class="wf-thumbs">
          ${run.steps.flatMap((s) => s.files || []).map((f) =>
            `<a href="/api/workflows/files/${f.split("/").map(encodeURIComponent).join("/")}?access_token=${token}" target="_blank">
               <img src="/api/workflows/files/${f.split("/").map(encodeURIComponent).join("/")}?access_token=${token}" alt="Captura">
             </a>`).join("")}
        </div>
      </div>` : ""}
    <div class="modal-actions"><button class="btn ghost" type="button" id="wf-run-close">Cerrar</button></div>`, "wider");
  $("#wf-run-close").addEventListener("click", closeModal);
}

// ---------------------------------------------------------------------------
// Campos de configuración de las acciones (compartidos con el editor)
// ---------------------------------------------------------------------------

/// HTML de un campo de configuración de acción. `lists` trae las listas del
/// servidor: { cameras, audio, speakers, doors, panels, catalog } (los
/// usuarios destinatarios vienen en catalog.users).
function wfFieldHtml(action, field, lists) {
  const { cameras = [], audio = [], speakers = [], doors = [], panels = [], ioModules = [], catalog = null } = lists;
  const users = catalog?.users || [];
  const value = action.config[field.k];
  const current = value === undefined ? field.def : value;
  const attributes = `data-key="${esc(field.k)}" data-type="${esc(field.t)}"`;
  const help = field.help ? `<div class="muted" style="font-size:11.5px;margin-top:3px">${esc(field.help)}</div>` : "";

  switch (field.t) {
    case "check":
      return `<label class="checkbox-row"><input type="checkbox" ${attributes} ${current ? "checked" : ""}> ${esc(field.label)}</label>`;
    case "textarea":
      return `<div class="field"><label>${esc(field.label)}</label>
        <textarea ${attributes} rows="${field.rows || 4}" placeholder="${esc(field.placeholder || "")}">${esc(current ?? "")}</textarea>${help}</div>`;
    case "number":
      return `<div class="field"><label>${esc(field.label)}</label>
        <input type="number" ${attributes} min="${field.min ?? 0}" max="${field.max ?? 999999}" step="${field.step ?? 1}" value="${current ?? field.def ?? 0}">${help}</div>`;
    case "password":
      return `<div class="field"><label>${esc(field.label)}${action.hasSecret ? " (guardada; vacío = no cambiar)" : ""}</label>
        <input type="password" autocomplete="new-password" ${attributes} value="${esc(action.secret || "")}">${help}</div>`;
    case "select":
      return `<div class="field"><label>${esc(field.label)}</label>
        <select ${attributes}>${field.options.map(([v, l]) =>
          `<option value="${esc(v)}" ${String(current ?? field.def) === String(v) ? "selected" : ""}>${esc(l)}</option>`).join("")}</select>${help}</div>`;
    case "cameras": {
      // El orden en que se guardan las cámaras es el orden en que se capturan
      // y en que las fotos aparecen en el aviso; se ajusta con las flechas.
      const selected = (current || []).map(String);
      const known = cameras.map((c) => String(c.channelId));
      const ordered = selected.filter((id) => known.includes(id));
      return `<div class="field"><label>${esc(field.label)}</label>
        ${cameras.length === 0 ? `<div class="muted">No hay cámaras configuradas.</div>` : `
          <div class="wf-cameras" ${attributes} data-order="${esc(ordered.join(","))}">
          <input type="text" class="wf-filter" placeholder="Filtrar cámaras por nombre…" autocomplete="off"
                 title="Escriba parte del nombre del equipo o de la cámara; las marcadas siempre se muestran">
          <div class="wf-check-grid">
            ${cameras.map((c) => `<label class="checkbox-row" ${wfPreviewAttr(c)} ${c.supportsSnapshot ? "" : 'title="El driver de este equipo no captura imágenes"'}>
              <input type="checkbox" data-camera="${c.channelId}" ${selected.includes(String(c.channelId)) ? "checked" : ""} ${c.supportsSnapshot ? "" : "disabled"}>
              ${esc(c.deviceName)} · ${esc(c.channelName)}</label>`).join("")}
          </div>
          <div class="wf-cam-order">${wfCameraOrderHtml(ordered, (id) => {
            const c = cameras.find((x) => String(x.channelId) === id);
            return c ? `${c.deviceName} · ${c.channelName}` : `Cámara ${id}`;
          })}</div>
          </div>`}${help}</div>`;
    }
    case "ptzcamera": {
      const ptz = cameras.filter((c) => c.supportsPtz);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${ptz.length === 0 ? `<div class="muted">Ninguna cámara informa PTZ.</div>` : `
          <select ${attributes}>
            <option value="">(elija la cámara)</option>
            ${ptz.map((c) => `<option value="${c.channelId}" ${String(current ?? "") === String(c.channelId) ? "selected" : ""}>${esc(c.deviceName)} · ${esc(c.channelName)}</option>`).join("")}
          </select>`}${help}</div>`;
    }
    case "speakers": {
      const selected = (current || []).map(String);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${speakers.length === 0 ? `<div class="muted">No hay parlantes IP configurados (Dispositivos → Parlantes IP).</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${speakers.map((s) => `<label class="checkbox-row" ${s.enabled ? "" : 'title="Parlante desactivado"'}>
              <input type="checkbox" data-speaker="${s.id}" ${selected.includes(String(s.id)) ? "checked" : ""}>
              ${esc(s.name)}${s.groupName ? ` <span class="muted">· ${esc(s.groupName)}</span>` : ""}</label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "screens": {
      const selected = (current || []).map(String);
      const screens = catalog?.screens || [];
      const unlicensed = catalog && !catalog.screensLicensed
        ? `<div class="muted wf-dest-hint" style="color:var(--warn)">Las pantallas de alarma no están incluidas en la licencia: mientras tanto el aviso sale en la ventana de alarma.</div>` : "";
      return `<div class="field"><label>${esc(field.label)}</label>${unlicensed}
        ${screens.length === 0 ? `<div class="muted">No hay pantallas de alarma emparejadas (Dispositivos → Pantallas de alarma).</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${screens.map((x) => `<label class="checkbox-row" ${x.enabled ? "" : 'title="Pantalla deshabilitada: no recibirá el aviso"'}>
              <input type="checkbox" data-screen="${x.id}" ${selected.includes(String(x.id)) ? "checked" : ""}>
              ${esc(x.name)} <span class="muted">· ${x.enabled ? (x.online ? "en línea" : "sin conexión") : "deshabilitada"}</span></label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "users": {
      const selected = (current || []).map(String);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${users.length === 0 ? `<div class="muted">No hay usuarios.</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${users.map((u) => `<label class="checkbox-row" ${u.enabled ? "" : 'title="Usuario desactivado: no recibirá el aviso"'}>
              <input type="checkbox" data-user="${u.id}" ${selected.includes(String(u.id)) ? "checked" : ""}>
              ${esc(u.username)} <span class="muted">· ${u.role === "Admin" ? "administrador" : "operador"}${u.enabled ? "" : " · desactivado"}</span></label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "doors": {
      const selected = (current || []).map(String);
      return `<div class="field"><label>${esc(field.label)}</label>
        ${doors.length === 0 ? `<div class="muted">No hay puertas (Dispositivos → Control de acceso).</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${doors.map((d) => `<label class="checkbox-row" ${d.enabled ? "" : 'title="Puerta desactivada"'}>
              <input type="checkbox" data-door="${d.doorId}" ${selected.includes(String(d.doorId)) ? "checked" : ""}>
              ${esc(d.deviceName)} · ${esc(d.name)}</label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "relays": {
      const selected = (current || []).map(String);
      const outputs = ioModules.flatMap((m) => m.outputs.map((o) => ({ key: `${m.id}:${o.number}`, module: m, output: o })));
      const known = new Set(outputs.map((o) => o.key));
      return `<div class="field"><label>${esc(field.label)}</label>
        ${outputs.length === 0 ? `<div class="muted">No hay módulos de E/S con salidas (Dispositivos → Módulos de E/S).</div>` : `
          <div class="wf-check-grid" ${attributes}>
            ${outputs.map((o) => `<label class="checkbox-row" ${o.module.enabled ? "" : 'title="Módulo pausado"'}>
              <input type="checkbox" data-relay="${esc(o.key)}" ${selected.includes(o.key) ? "checked" : ""}>
              ${esc(o.module.name)} · ${esc(o.output.name)}${o.output.name === o.output.code ? "" : ` <span class="muted">${esc(o.output.code)}</span>`}</label>`).join("")}
            ${selected.filter((k) => !known.has(k)).map((k) => `<label class="checkbox-row" title="Ese relé ya no existe">
              <input type="checkbox" data-relay="${esc(k)}" checked> ${esc(k)} <span class="muted">(ya no existe)</span></label>`).join("")}
          </div>`}${help}</div>`;
    }
    case "panel":
      return `<div class="field"><label>${esc(field.label)}</label>
        ${panels.length === 0 ? `<div class="muted">No hay paneles de alarma configurados.</div>` : `
          <select ${attributes}>
            <option value="">(elija el panel)</option>
            ${panels.map((p) => `<option value="${p.id}" ${String(current ?? "") === String(p.id) ? "selected" : ""}>${esc(p.name)}</option>`).join("")}
          </select>`}${help}</div>`;
    case "area": {
      const panel = panels.find((p) => p.id === Number(action.config.panelId));
      const areas = panel?.areas || [];
      return `<div class="field"><label>${esc(field.label)}</label>
        <select ${attributes}>
          <option value="0" ${!current ? "selected" : ""}>Todas las áreas</option>
          ${areas.map((a) => `<option value="${a.number}" ${Number(current) === a.number ? "selected" : ""}>${a.number} · ${esc(a.name)}</option>`).join("")}
        </select>${help}</div>`;
    }
    case "audio": {
      // Con `optional` la lista permite "sin sonido"; con `system`, el pitido
      // del sistema operativo, que no necesita ningún archivo cargado.
      const extra = [];
      if (field.optional) extra.push(["", "(sin sonido)"]);
      if (field.system) extra.push(["sistema", "Pitido del sistema del equipo"]);
      if (audio.length === 0 && extra.length === 0)
        return `<div class="field"><label>${esc(field.label)}</label>
          <div class="muted">Aún no hay sonidos: súbalos con el botón <b>Sonidos</b> del listado.</div>${help}</div>`;
      const options = extra.concat(audio.map((a) => [a.displayName, a.displayName + (a.ready ? "" : " (sin convertir)")]));
      return `<div class="field"><label>${esc(field.label)}</label>
        <div style="display:flex;gap:8px;align-items:center">
          <select ${attributes} style="flex:1">
            ${options.map(([v, l]) => `<option value="${esc(v)}" ${String(current ?? "") === String(v) ? "selected" : ""}>${esc(l)}</option>`).join("")}
          </select>
          <button type="button" class="btn ghost btn-field-play" data-for="${esc(field.k)}" title="Escuchar el sonido elegido">▶</button>
        </div>${help}</div>`;
    }
    default:
      return `<div class="field"><label>${esc(field.label)}</label>
        <input type="text" ${attributes} value="${esc(current ?? "")}" placeholder="${esc(field.placeholder || "")}">${help}</div>`;
  }
}

// --- Listas de cámaras: filtro y vista previa al pasar el mouse -------------
// Con decenas de canales, marcar la cámara correcta a ojo es lento: el filtro
// esconde las que no calzan con el texto (las marcadas siguen a la vista) y
// al dejar el mouse un momento sobre un nombre aparece una miniatura viva del
// canal (misma captura que usa el mantenedor de dispositivos).

/// Cuánto hay que dejar el mouse sobre el nombre para que aparezca la vista previa.
const WF_PREVIEW_DELAY_MS = 1500;

/// Atributos de vista previa de una cámara de /api/workflows/cameras.
function wfPreviewAttr(camera) {
  if (!camera.supportsSnapshot || camera.channelNumber === undefined) return "";
  return `data-preview="${camera.deviceId}/${camera.channelNumber}" data-preview-name="${esc(camera.deviceName)} · ${esc(camera.channelName)}"`;
}

/// Lista numerada de las cámaras marcadas en el orden de captura, con flechas
/// para subir o bajar cada una. `nameOf` da el nombre de una cámara por id.
function wfCameraOrderHtml(ids, nameOf) {
  if (ids.length < 2) return "";
  return `<div class="muted" style="font-size:11.5px;margin:8px 0 3px">Orden de captura (es el orden de las fotos en el aviso):</div>` +
    ids.map((id, i) => `<div class="wf-cam-order-row">
      <span class="wf-cam-order-n">${i + 1}</span><span class="wf-cam-order-name">${esc(nameOf(id))}</span>
      <button type="button" class="btn ghost wf-cam-move" data-id="${esc(id)}" data-move="-1" title="Subir" ${i === 0 ? "disabled" : ""}>▲</button>
      <button type="button" class="btn ghost wf-cam-move" data-id="${esc(id)}" data-move="1" title="Bajar" ${i === ids.length - 1 ? "disabled" : ""}>▼</button>
    </div>`).join("");
}

/// Vuelve a pintar la lista de orden de un campo de cámaras según su data-order.
function wfRefreshCameraOrder(wrapper) {
  const list = wrapper.querySelector(".wf-cam-order");
  if (!list) return;
  const ids = (wrapper.dataset.order || "").split(",").filter((id) => id.length > 0);
  list.innerHTML = wfCameraOrderHtml(ids, (id) => {
    const box = wrapper.querySelector(`[data-camera="${id}"]`);
    return box ? box.closest("label").textContent.trim() : `Cámara ${id}`;
  });
}

/// Activa el filtro, el orden y la vista previa en todas las listas de cámaras dentro de `root`.
function wfBindCameraLists(root) {
  // Flechas de orden: cambian data-order y avisan "change" al campo para que
  // se guarde igual que al marcar una casilla.
  $$(".wf-cameras", root).forEach((wrapper) => {
    wrapper.addEventListener("click", (e) => {
      const button = e.target.closest(".wf-cam-move");
      if (!button || button.disabled) return;
      e.preventDefault();
      const ids = (wrapper.dataset.order || "").split(",").filter((id) => id.length > 0);
      const from = ids.indexOf(button.dataset.id);
      const to = from + Number(button.dataset.move);
      if (from < 0 || to < 0 || to >= ids.length) return;
      [ids[from], ids[to]] = [ids[to], ids[from]];
      wrapper.dataset.order = ids.join(",");
      wfRefreshCameraOrder(wrapper);
      wrapper.dispatchEvent(new Event("change", { bubbles: true }));
    });
  });

  $$(".wf-filter", root).forEach((input) => {
    const grid = input.nextElementSibling;
    if (!grid || !grid.classList.contains("wf-check-grid")) return;
    input.addEventListener("input", () => {
      const text = input.value.trim().toLowerCase();
      let visible = 0;
      $$("label", grid).forEach((label) => {
        const checked = label.querySelector("input")?.checked;
        const show = !text || checked || label.textContent.toLowerCase().includes(text);
        label.hidden = !show;
        if (show) visible++;
      });
      grid.querySelector(".wf-filter-empty")?.remove();
      if (visible === 0) grid.insertAdjacentHTML("beforeend", `<div class="muted wf-filter-empty" style="font-size:12px">Ninguna cámara coincide con «${esc(input.value.trim())}».</div>`);
    });
  });

  $$("[data-preview]", root).forEach((label) => {
    let timer = null;
    label.addEventListener("mouseenter", () => {
      timer = setTimeout(() => wfShowPreview(label), WF_PREVIEW_DELAY_MS);
    });
    label.addEventListener("mouseleave", () => {
      clearTimeout(timer);
      wfHidePreview();
    });
  });
}

function wfShowPreview(label) {
  wfHidePreview();
  const [deviceId, channelNumber] = label.dataset.preview.split("/");
  const box = document.createElement("div");
  box.id = "wf-preview";
  box.innerHTML = `
    <div class="wf-preview-name">${esc(label.dataset.previewName || "")}</div>
    <div class="wf-preview-body"><span class="muted">Capturando…</span></div>`;
  document.body.appendChild(box);

  // A la derecha del nombre si cabe; si no, a la izquierda. Nunca fuera de la ventana.
  const rect = label.getBoundingClientRect();
  const width = 336, height = 230;
  let left = rect.right + 10;
  if (left + width > window.innerWidth - 8) left = rect.left - width - 10;
  if (left < 8) left = 8;
  let top = rect.top - 20;
  if (top + height > window.innerHeight - 8) top = window.innerHeight - height - 8;
  if (top < 8) top = 8;
  box.style.left = left + "px";
  box.style.top = top + "px";

  const img = new Image();
  img.alt = "";
  img.addEventListener("load", () => {
    if (!box.isConnected) return;
    box.querySelector(".wf-preview-body").replaceChildren(img);
  });
  img.addEventListener("error", () => {
    if (!box.isConnected) return;
    box.querySelector(".wf-preview-body").innerHTML = `<span class="muted">El equipo no entregó imagen.</span>`;
  });
  img.src = `/api/devices/${encodeURIComponent(deviceId)}/snapshot/${encodeURIComponent(channelNumber)}?access_token=${encodeURIComponent(Api.token || "")}&t=${Date.now()}`;
}

function wfHidePreview() {
  $("#wf-preview")?.remove();
}

/// Lee un campo de la pantalla y lo guarda en la acción.
function wfReadField(action, input) {
  const key = input.dataset.key;
  switch (input.dataset.type) {
    case "check": action.config[key] = input.checked; break;
    case "number": action.config[key] = Number(input.value) || 0; break;
    case "password": action.secret = input.value; break;
    case "cameras": {
      // Se respeta el orden ya definido; lo recién marcado va al final.
      const checked = Array.from(input.querySelectorAll("[data-camera]"))
        .filter((c) => c.checked).map((c) => c.dataset.camera);
      const previous = (input.dataset.order || "").split(",").filter((id) => id.length > 0);
      const ordered = previous.filter((id) => checked.includes(id)).concat(checked.filter((id) => !previous.includes(id)));
      input.dataset.order = ordered.join(",");
      action.config[key] = ordered.map(Number);
      wfRefreshCameraOrder(input);
      break;
    }
    case "speakers":
      action.config[key] = Array.from(input.querySelectorAll("[data-speaker]"))
        .filter((c) => c.checked).map((c) => Number(c.dataset.speaker));
      break;
    case "doors":
      action.config[key] = Array.from(input.querySelectorAll("[data-door]"))
        .filter((c) => c.checked).map((c) => Number(c.dataset.door));
      break;
    case "relays":
      action.config[key] = Array.from(input.querySelectorAll("[data-relay]"))
        .filter((c) => c.checked).map((c) => c.dataset.relay);
      break;
    case "users":
      action.config[key] = Array.from(input.querySelectorAll("[data-user]"))
        .filter((c) => c.checked).map((c) => Number(c.dataset.user));
      break;
    case "screens":
      action.config[key] = Array.from(input.querySelectorAll("[data-screen]"))
        .filter((c) => c.checked).map((c) => Number(c.dataset.screen));
      break;
    case "panel":
    case "area":
    case "ptzcamera":
      action.config[key] = Number(input.value) || 0;
      break;
    default: action.config[key] = input.value; break;
  }
}

// ---------------------------------------------------------------------------
// Sonidos para los parlantes IP
// ---------------------------------------------------------------------------

async function wfAudioModal() {
  const draw = async () => {
    let items, catalog;
    try { [items, catalog] = await Promise.all([Api.get("/api/workflows/audio"), wfCatalog()]); }
    catch (err) { toast(err.error, true); return; }

    openModal(`
      <h3>Sonidos para parlantes IP</h3>
      <div id="wf-audio-error"></div>
      ${catalog.ffmpegAvailable ? "" : `<div class="error-box">El servidor no tiene FFmpeg disponible: los sonidos no se pueden convertir al formato que exigen los parlantes.</div>`}
      <div class="info-box">
        Suba un archivo de audio (WAV, MP3...). El servidor lo convierte a G.711 8 kHz mono, que es lo que
        acepta el canal de audio de los equipos Hikvision. Máximo 8 MB; se recomiendan mensajes de pocos segundos.
      </div>
      ${items.length === 0 ? `<div class="muted" style="margin:12px 0">Aún no hay sonidos cargados.</div>` : `
        <div class="table-scroll"><table class="grid">
          <thead><tr><th>Sonido</th><th>Tamaño</th><th>Estado</th><th></th></tr></thead>
          <tbody>
            ${items.map((a) => `
              <tr>
                <td>${esc(a.displayName)}<div class="muted" style="font-size:11px">${esc(a.fileName)}</div></td>
                <td class="muted">${Math.round(a.bytes / 1024)} kB</td>
                <td>${a.ready ? `<span class="tag on">Listo</span>` : `<span class="tag off">Sin convertir</span>`}</td>
                <td class="row-actions">
                  <button class="btn ghost btn-audio-play" data-name="${esc(a.displayName)}" title="Escuchar">▶</button>
                  <button class="btn danger btn-audio-del" data-name="${esc(a.displayName)}">Eliminar</button>
                </td>
              </tr>`).join("")}
          </tbody>
        </table></div>`}
      <div class="field" style="margin-top:12px">
        <label>Agregar un sonido</label>
        <input type="file" id="wf-audio-file" accept="audio/*">
      </div>
      <div class="modal-actions">
        <button class="btn ghost" type="button" id="wf-audio-close">Cerrar</button>
        <button class="btn" type="button" id="wf-audio-upload">Subir</button>
      </div>`, "wider");

    $("#wf-audio-close").addEventListener("click", () => { wfStopPreview(); closeModal(); });
    $$(".btn-audio-play").forEach((b) => b.addEventListener("click", () => wfTogglePreview(b.dataset.name, b)));
    $$(".btn-audio-del").forEach((b) => b.addEventListener("click", async () => {
      if (!confirm(`¿Eliminar el sonido "${b.dataset.name}"?`)) return;
      wfStopPreview();
      try {
        await Api.delete(`/api/workflows/audio/${encodeURIComponent(b.dataset.name)}`);
        toast("Sonido eliminado.");
        draw();
      } catch (err) { toast(err.error, true); }
    }));

    $("#wf-audio-upload").addEventListener("click", async () => {
      const file = $("#wf-audio-file").files[0];
      if (!file) { toast("Elija un archivo de audio.", true); return; }
      const button = $("#wf-audio-upload");
      button.disabled = true;
      button.textContent = "Subiendo…";
      const form = new FormData();
      form.append("file", file);
      try {
        const response = await fetch("/api/workflows/audio", {
          method: "POST",
          headers: { Authorization: "Bearer " + Api.token },
          body: form,
        });
        const data = await response.json().catch(() => null);
        if (!response.ok) throw { error: (data && data.error) || `Error ${response.status}` };
        toast("Sonido subido y convertido.");
        wfStopPreview();
        draw();
      } catch (err) {
        $("#wf-audio-error").innerHTML = `<div class="error-box">${esc(err.error || "No se pudo subir el sonido.")}</div>`;
      } finally {
        button.disabled = false;
        button.textContent = "Subir";
      }
    });
  };
  await draw();
}

// ---------------------------------------------------------------------------
// Biblioteca de sonidos: todo lo que puede sonar en los parlantes IP. Los
// sonidos del servidor (se transmiten desde acá, sincronizados) con su nivel
// y el control de ganancia; y, solo lectura, los audios que cada parlante
// guarda en su propia biblioteca (los usa la acción "audio de la biblioteca").
// ---------------------------------------------------------------------------

function wfDb(value) {
  return value === null || value === undefined ? "—" : `${value > 0 ? "+" : ""}${Number(value).toFixed(1)} dB`;
}

/** Barra de nivel: pico en dBFS (−60 … 0) como porcentaje del ancho. */
function wfLevelBar(peakDb) {
  if (peakDb === null || peakDb === undefined) return "";
  const pct = Math.max(0, Math.min(100, Math.round((60 + peakDb) / 60 * 100)));
  const color = peakDb > -1 ? "var(--danger, #e5484d)" : peakDb > -6 ? "var(--accent)" : "var(--muted, #888)";
  return `<div title="Pico ${wfDb(peakDb)} (0 dB = techo digital)" style="height:6px;border-radius:3px;background:var(--border);overflow:hidden;min-width:90px">
    <div style="width:${pct}%;height:100%;background:${color}"></div></div>`;
}

async function renderSounds() {
  $("#page-title").textContent = "Biblioteca de sonidos";
  const isAdmin = Perms.can("sounds.manage");
  let items, catalog, speakers;
  try {
    [items, catalog, speakers] = await Promise.all([Api.get("/api/workflows/audio"), wfCatalog(), Api.get("/api/speakers").catch(() => [])]);
  } catch (err) {
    $("#view").innerHTML = `<div class="error-box">${esc(err.error)}</div>`;
    return;
  }

  $("#view").innerHTML = `
    <div class="toolbar page-toolbar">
      ${isAdmin ? `<div class="row-actions" style="align-items:center;gap:8px">
        <input type="file" id="wf-audio-file" accept="audio/*" style="max-width:260px">
        <button class="btn" id="wf-audio-upload">Subir</button>
      </div>` : ""}
    </div>
    <div id="wf-audio-error"></div>
    ${catalog.ffmpegAvailable ? "" : `<div class="error-box" style="margin-bottom:10px">El servidor no tiene FFmpeg disponible: los sonidos no se pueden convertir ni amplificar.</div>`}
    <div class="info-box" style="margin-bottom:12px">
      Cualquier archivo de audio (WAV, MP3…) se convierte a G.711 8 kHz mono, el formato del canal de audio de los parlantes.
      <b>Nivel</b> es el pico del sonido respecto del techo digital (0 dB): <b>Amplificar</b> lo sube justo hasta ese techo sin saturar
      (queda 0,3 dB por debajo). Los sonidos también sirven como alarma sonora en el equipo del operador.
    </div>
    <div id="wf-sounds-table">
    ${items.length === 0 ? `<div class="info-box">Aún no hay sonidos cargados.${isAdmin ? " Elija un archivo y pulse <b>Subir</b>." : ""}</div>` : `
      <div class="table-scroll"><table class="grid">
        <thead><tr><th>Sonido</th><th>Duración</th><th>Tamaño</th><th style="min-width:200px">Nivel (pico)</th><th>Medio</th><th>Estado</th><th></th></tr></thead>
        <tbody>
          ${items.map((a) => `
            <tr data-name="${esc(a.displayName)}">
              <td>${esc(a.displayName)}<div class="muted" style="font-size:11px">${esc(a.fileName)}</div></td>
              <td class="muted wf-snd-duration">…</td>
              <td class="muted">${Math.round(a.bytes / 1024)} kB</td>
              <td class="wf-snd-level"><span class="muted">midiendo…</span></td>
              <td class="muted wf-snd-mean">…</td>
              <td>${a.ready ? `<span class="tag on">Listo</span>` : `<span class="tag off">Sin convertir</span>`}</td>
              <td class="row-actions">
                <button class="btn ghost btn-audio-play" data-name="${esc(a.displayName)}" title="Escuchar en el navegador">▶</button>
                ${isAdmin ? `
                  <button class="btn ghost btn-audio-gain" data-name="${esc(a.displayName)}" disabled title="Sube el sonido hasta justo antes de saturar">Amplificar</button>
                  <button class="btn ghost btn-audio-gain-custom" data-name="${esc(a.displayName)}" title="Aplicar una ganancia a elección (dB); nunca por encima del techo">dB…</button>
                  <button class="btn danger btn-audio-del" data-name="${esc(a.displayName)}">Eliminar</button>` : ""}
              </td>
            </tr>`).join("")}
        </tbody>
      </table></div>`}
    </div>
    <h3 style="margin-top:22px">Audios en los parlantes <span class="muted" style="font-weight:normal;font-size:12px">(biblioteca de cada equipo; los reproduce el propio parlante)</span></h3>
    <div id="wf-speaker-libs">${speakers.length === 0 ? `<div class="info-box">No hay parlantes IP configurados.</div>` : `<div class="info-box">Cargando…</div>`}</div>`;

  // Nivel de cada sonido, en paralelo y sin bloquear la tabla.
  const rows = $$("#wf-sounds-table tr[data-name]");
  await Promise.all(rows.map(async (tr) => {
    const name = tr.dataset.name;
    try {
      const level = await Api.get(`/api/workflows/audio/${encodeURIComponent(name)}/level`);
      wfApplyLevel(tr, level);
    } catch {
      tr.querySelector(".wf-snd-level").innerHTML = `<span class="muted">sin medir</span>`;
      tr.querySelector(".wf-snd-duration").textContent = "—";
      tr.querySelector(".wf-snd-mean").textContent = "—";
    }
  }));

  $$(".btn-audio-play").forEach((b) => b.addEventListener("click", () => wfTogglePreview(b.dataset.name, b)));
  $$(".btn-audio-del").forEach((b) => b.addEventListener("click", async () => {
    if (!confirm(`¿Eliminar el sonido "${b.dataset.name}"? Las automatizaciones que lo usen quedarán sin sonido.`)) return;
    wfStopPreview();
    try {
      await Api.delete(`/api/workflows/audio/${encodeURIComponent(b.dataset.name)}`);
      toast("Sonido eliminado.");
      wfCatalogCache = null;
      renderSounds();
    } catch (err) { toast(err.error, true); }
  }));
  const applyGain = async (name, gainDb, button) => {
    wfStopPreview();
    const tr = button.closest("tr");
    tr.querySelectorAll("button").forEach((x) => (x.disabled = true));
    button.textContent = "Aplicando…";
    try {
      const data = await Api.post(`/api/workflows/audio/${encodeURIComponent(name)}/gain`, { gainDb });
      toast(`Sonido «${name}» amplificado ${wfDb(data.appliedGainDb)} y reconvertido.`);
      wfApplyLevel(tr, data.level);
      const item = (data.items || []).find((i) => i.displayName === name);
      if (item) tr.querySelector("td:first-child .muted").textContent = item.fileName;
    } catch (err) {
      toast(err.error, true);
    } finally {
      button.textContent = button.classList.contains("btn-audio-gain") ? "Amplificar" : "dB…";
      tr.querySelectorAll("button").forEach((x) => (x.disabled = false));
      wfRefreshGainButton(tr);
    }
  };
  $$(".btn-audio-gain").forEach((b) => b.addEventListener("click", () => applyGain(b.dataset.name, null, b)));
  $$(".btn-audio-gain-custom").forEach((b) => b.addEventListener("click", () => {
    const tr = b.closest("tr");
    const suggested = Number(tr.dataset.suggested || 0);
    const answer = prompt(`Ganancia a aplicar a «${b.dataset.name}», en dB (positivo amplifica, negativo atenúa).\nMáximo sin saturar: ${wfDb(suggested)}.`, suggested.toFixed(1));
    if (answer === null) return;
    const gain = Number(String(answer).replace(",", "."));
    if (!Number.isFinite(gain)) { toast("Escriba un número de decibeles.", true); return; }
    applyGain(b.dataset.name, gain, b);
  }));

  $("#wf-audio-upload")?.addEventListener("click", async () => {
    const file = $("#wf-audio-file").files[0];
    if (!file) { toast("Elija un archivo de audio.", true); return; }
    const button = $("#wf-audio-upload");
    button.disabled = true;
    button.textContent = "Subiendo…";
    const form = new FormData();
    form.append("file", file);
    try {
      const response = await fetch("/api/workflows/audio", {
        method: "POST", headers: { Authorization: "Bearer " + Api.token }, body: form,
      });
      const data = await response.json().catch(() => null);
      if (!response.ok) throw { error: (data && data.error) || `Error ${response.status}` };
      toast("Sonido subido y convertido.");
      wfStopPreview();
      wfCatalogCache = null;
      renderSounds();
    } catch (err) {
      $("#wf-audio-error").innerHTML = `<div class="error-box" style="margin-bottom:10px">${esc(err.error || "No se pudo subir el sonido.")}</div>`;
      button.disabled = false;
      button.textContent = "Subir";
    }
  });

  // Bibliotecas de los parlantes (una consulta por equipo; los que no
  // respondan quedan marcados sin tumbar la página).
  if (speakers.length > 0) {
    const blocks = await Promise.all(speakers.map(async (s) => {
      if (!s.supportsLibrary) return `<div class="info-box"><b>${esc(s.name)}</b>: el equipo no tiene biblioteca de audios.</div>`;
      try {
        const lib = await Api.get(`/api/speakers/${s.id}/library`);
        return `<h4 style="margin:14px 0 6px">${esc(s.name)} <span class="muted" style="font-weight:normal;font-size:12px">${esc(s.host || "")} · ${lib.length} audio${lib.length === 1 ? "" : "s"}</span></h4>
          ${lib.length === 0 ? `<div class="muted" style="font-size:12px">Sin audios en el equipo.</div>` : `
          <div class="table-scroll"><table class="grid">
            <thead><tr><th>Nombre</th><th>Formato</th><th>Duración</th><th>Tamaño</th><th>Origen</th></tr></thead>
            <tbody>${lib.map((i) => `<tr>
              <td>${esc(i.name)}</td><td class="muted">${esc(i.format || "")}</td>
              <td class="muted">${i.durationSeconds ? `${i.durationSeconds} s` : "—"}</td>
              <td class="muted">${i.bytes ? `${Math.round(i.bytes / 1024)} kB` : "—"}</td>
              <td>${i.builtIn ? `<span class="tag operator">de fábrica</span>` : `<span class="tag on">cargado</span>`}</td>
            </tr>`).join("")}</tbody>
          </table></div>`}`;
      } catch (err) {
        return `<div class="error-box" style="margin-top:10px"><b>${esc(s.name)}</b>: ${esc(err.error || "no respondió")}</div>`;
      }
    }));
    const box = $("#wf-speaker-libs");
    if (box) box.innerHTML = blocks.join("") + `<div class="muted" style="font-size:11.5px;margin-top:8px">Los audios de los parlantes se administran desde Dispositivos → Parlantes IP; la ganancia solo aplica a los sonidos del servidor.</div>`;
  }
}

function wfApplyLevel(tr, level) {
  tr.dataset.suggested = String(level?.suggestedGainDb ?? 0);
  tr.dataset.peak = level?.peakDb === null || level?.peakDb === undefined ? "" : String(level.peakDb);
  tr.querySelector(".wf-snd-duration").textContent = level?.durationSeconds ? `${level.durationSeconds} s` : "—";
  tr.querySelector(".wf-snd-mean").textContent = wfDb(level?.meanDb);
  tr.querySelector(".wf-snd-level").innerHTML = level?.peakDb === null || level?.peakDb === undefined
    ? `<span class="muted">sin medir</span>`
    : `<div style="display:flex;align-items:center;gap:8px">${wfLevelBar(level.peakDb)}<span class="muted" style="font-size:12px;white-space:nowrap">${wfDb(level.peakDb)}</span></div>`;
  wfRefreshGainButton(tr);
}

function wfRefreshGainButton(tr) {
  const button = tr.querySelector(".btn-audio-gain");
  if (!button) return;
  const suggested = Number(tr.dataset.suggested || 0);
  const measured = tr.dataset.peak !== "" && tr.dataset.peak !== undefined;
  button.disabled = !measured || suggested < 0.1;
  button.textContent = measured && suggested >= 0.1 ? `Amplificar ${wfDb(suggested)}` : "Al máximo";
  button.title = !measured ? "Sin medición" : suggested < 0.1 ? "El sonido ya está al máximo sin saturar" : "Sube el sonido hasta justo antes de saturar";
}
