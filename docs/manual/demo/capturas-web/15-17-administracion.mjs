// Capítulos 15 a 17 · Recursos y ubicaciones, Usuarios y roles, Sistema (panel web).
// Cada captura: archivo, antes(pagina) para dejar la pantalla lista, recorte
// (selector, rectángulo o toda la ventana) y marcas {sel, texto?, en?, etiqueta}.
//
// Solo se marcan elementos que caben en la ventana: marcar uno de más abajo la
// desplaza y corre las marcas anteriores.

import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const aqui = dirname(fileURLToPath(import.meta.url));
const demo = JSON.parse(readFileSync(join(aqui, "..", "demo.json"), "utf8"));
const ffmpeg = [
  join(aqui, "..", "..", "..", "..", "tools", "ffmpeg", "bin", "ffmpeg.exe"),
  join(process.env.LOCALAPPDATA ?? "", "TrueCentralDemo", "tools", "ffmpeg", "bin", "ffmpeg.exe"),
].find(existsSync);

const pausa = ms => new Promise(r => setTimeout(r, ms));

// ---------------------------------------------------------------------------
// Sesiones: dos puestos de cliente de escritorio (uno conectado y otro con la
// sesión colgada) y video abierto, para que la página tenga qué mostrar. Son
// sesiones en memoria del servidor: después de la captura se liberan los
// puestos y se corta el video.
// ---------------------------------------------------------------------------

const lectores = [];  // ffmpeg que miran video mientras se captura
const puestos = [];   // puestos de cliente abiertos para la captura

/** Ingresa como lo hace el cliente de escritorio (ocupa un puesto); devuelve el token. */
async function entrarComoCliente(p, usuario, equipo, version, conectado) {
  const clave = demo.usuarios.find(u => u.usuario === usuario).clave;
  const r = await p.evaluar(async (u, c, e, v, hub) => {
    const res = await fetch("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-TCVMS-Client": `wpf/${v} (${e})` },
      body: JSON.stringify({ username: u, password: c }),
    });
    const cuerpo = await res.json();
    if (!res.ok) return { error: cuerpo.error ?? res.status };
    if (hub) {
      // El canal en tiempo real abierto es lo que marca al puesto como «Conectado».
      const conexion = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/vms", { accessTokenFactory: () => cuerpo.token }).build();
      await conexion.start();
      (window.__puestosCaptura ??= []).push(conexion);
    }
    return { token: cuerpo.token };
  }, usuario, clave, equipo, version, conectado);
  puestos.push(equipo);
  if (r.error) throw new Error(`No se pudo ingresar como ${usuario}: ${r.error}`);
  return r.token;
}

/** Pide la concesión de video con el token de un usuario y la abre con ffmpeg (sin mostrarla). */
async function mirar(p, token, dispositivo, perfil) {
  const r = await p.evaluar(async (t, d, pf) => {
    const res = await fetch("/api/streams/request", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${t}` },
      body: JSON.stringify({ deviceId: d, rtspChannel: 1, profile: pf }),
    });
    return { estado: res.status, cuerpo: await res.json() };
  }, token, dispositivo, perfil);
  if (r.estado !== 200) throw new Error(`Sin video del equipo ${dispositivo}: ${JSON.stringify(r.cuerpo)}`);
  // -t 120: si algo falla y nadie lo cierra, se corta solo a los dos minutos.
  lectores.push(spawn(ffmpeg, ["-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp",
    "-i", r.cuerpo.rtspUrl, "-map", "0:v", "-c", "copy", "-t", "120", "-f", "null", "-"],
    { stdio: "ignore", windowsHide: true }));
}

async function deshacerSesiones(p) {
  while (lectores.length) lectores.pop().kill();
  await p.evaluar(async () => {
    for (const c of window.__puestosCaptura ?? []) await c.stop().catch(() => { });
    window.__puestosCaptura = [];
  }).catch(() => { });
  while (puestos.length) await p.api("POST", "/api/system/desktop-seats/release", { seat: puestos.pop() }).catch(() => { });
}

// ---------------------------------------------------------------------------
// Servicios: uno detenido a mano y vuelto a iniciar después de la captura.
// Es el de los paneles de alarma, que en la demo no tiene paneles que atender.
// ---------------------------------------------------------------------------

const SERVICIO_DETENIDO = "Paneles de alarma";
let servicioDetenido = null;

const reanudarServicio = async p => {
  if (!servicioDetenido) return;
  await p.api("POST", `/api/system/services/${encodeURIComponent(servicioDetenido)}/start`);
  servicioDetenido = null;
};

export default [
  // ------------------------------------------------------------- Capítulo 15
  {
    archivo: "web-recursos.png",
    antes: async p => {
      await p.ir("#/resources", "#res-tree .res-node");
      await p.clic("#res-tree .res-node .res-name", { texto: "Casa matriz" });
      await p.esperar("#res-loc-child");
      await p.esperarTexto("#res-table", "Pasillo oficinas");
    },
    recorte: ".res-layout",
    margen: 16,
    marcas: [
      { sel: "#res-tree .res-tree-head", etiqueta: "Árbol de ubicaciones, con cuántos recursos tiene cada una" },
      { sel: "#res-new-root", etiqueta: "+ Nueva: ubicación de primer nivel" },
      { sel: "#res-tree .res-node.pending", etiqueta: "Por ubicar" },
      { sel: "#res-head h3", etiqueta: "Ubicación elegida, con su tipo y su dirección" },
      { sel: "#res-loc-child", etiqueta: "+ Sububicación, Editar y Eliminar" },
      { sel: "#res-chips", etiqueta: "Etiquetas por tipo" },
      { sel: "#res-text", etiqueta: "Buscador y lista Todos los equipos" },
      { sel: "#res-table .res-open", etiqueta: "Nombre del recurso: abre su ficha" },
    ],
  },
  {
    archivo: "web-recursos-ficha.png",
    antes: async p => {
      await p.ir("#/resources", "#res-tree .res-node");
      await p.clic("#res-tree .res-node[data-special=all] .res-name");
      await p.esperarTexto("#res-table", "Acceso principal");
      await p.clic("#res-table .res-open", { texto: "Acceso principal" });
      await p.esperar("#rs-instr");
      // Que la imagen actual termine de llegar.
      await p.evaluar(async () => {
        for (let i = 0; i < 60; i++) {
          const img = document.querySelector("#rs-snapshot");
          if (img?.complete && img.naturalWidth > 0) return;
          await new Promise(r => setTimeout(r, 250));
        }
      });
    },
    recorte: ".res-layout",
    margen: 16,
    marcas: [
      { sel: ".res-sheet-back a", etiqueta: "← Volver a la lista" },
      { sel: "#res-sheet .res-tabs", etiqueta: "Pestañas de la ficha" },
      { sel: "#rs-location", etiqueta: "Ubicación" },
      { sel: "#rs-instr", etiqueta: "Consignas para el operador" },
      { sel: "#rs-snap", en: "arriba-izq", etiqueta: "Imagen actual, con Actualizar cada y Actualizar ahora" },
      { sel: "#rs-save", etiqueta: "Guardar" },
    ],
  },

  // ------------------------------------------------------------- Capítulo 16
  {
    archivo: "web-usuarios.png",
    antes: async p => {
      await p.ir("#/users", "#view table.grid tbody tr");
      await p.esperarTexto("#view table.grid", "tecnico");
    },
    recorte: "#view",
    marcas: [
      { sel: "#btn-user-new", etiqueta: "Agregar usuario" },
      { sel: "#view td .tag.admin", texto: "Superadministrador", etiqueta: "Superadministrador" },
      { sel: "#view tr td:nth-child(3) span", texto: "Centro de distribución", etiqueta: "Alcance limitado por su rol" },
      { sel: "#view td .tag.off", texto: "Deshabilitado", etiqueta: "Usuario deshabilitado" },
      { sel: "#view th", texto: "Última clave", etiqueta: "Última clave: cuándo cambió su contraseña" },
      { sel: "#view tr .btn-edit", texto: "Editar", etiqueta: "Editar y Eliminar" },
    ],
  },
  {
    archivo: "web-roles.png",
    antes: async p => {
      await p.ir("#/roles", "#rl-list .rl-card");
      await p.clic("#rl-list .rl-card", { texto: "Guardia centro de distribución" });
      await p.esperar("#rl-scope-tree");
      await p.esperar("#rl-scope-results .rl-scope-row");
    },
    recorte: ".rl-layout",
    margen: 16,
    marcas: [
      { sel: "#rl-list .rl-card.active", etiqueta: "Lista de roles: permisos, usuarios y alcance de cada uno" },
      { sel: "#rl-new", etiqueta: "+ Nuevo rol" },
      { sel: "#rl-name", etiqueta: "Nombre y Descripción" },
      { sel: "#rl-scope input[value=some]", etiqueta: "Alcance: solo en estas ubicaciones y recursos" },
      { sel: "#rl-scope-tree .scope-node", texto: "Centro de distribución", etiqueta: "Ubicaciones del rol, con sus sububicaciones" },
      { sel: "#rl-scope-kind", etiqueta: "Recursos sueltos: tipo y buscador" },
      { sel: "#rl-view-outside", etiqueta: "Puede ver el resto, sin operarlo" },
      { sel: "#rl-filter", etiqueta: "Buscar permiso…" },
    ],
  },
  {
    archivo: "web-auditoria.png",
    ancho: 1500,
    antes: async p => {
      await p.ir("#/audit", "#af-category");
      await p.escribir("#af-category", "users");
      await p.clic("#af-search");
      await p.esperar("#audit-results .audit-row");
    },
    recorte: "#view",
    marcas: [
      { sel: "#af-category", etiqueta: "Filtros (aquí, la categoría Usuarios)" },
      { sel: "#af-search", etiqueta: "Buscar y Limpiar" },
      { sel: "#audit-export", etiqueta: "Exportar CSV" },
      { sel: "#audit-results .audit-row", etiqueta: "Un evento: clic para ver el detalle completo" },
      { sel: "#audit-results .tag.on", etiqueta: "Resultado: Éxito o Fallo" },
      { sel: ".audit-pager", etiqueta: "Total de eventos y paginador" },
    ],
  },

  // ------------------------------------------------------------- Capítulo 17
  {
    archivo: "web-servicios.png",
    alto: 1300,
    antes: async p => {
      const { cuerpo } = await p.api("GET", "/api/system/services");
      const s = cuerpo.services.find(x => x.name === SERVICIO_DETENIDO);
      if (!s) throw new Error(`No existe el servicio «${SERVICIO_DETENIDO}»`);
      if (s.state === "Running") {
        servicioDetenido = s.id;
        await p.api("POST", `/api/system/services/${encodeURIComponent(s.id)}/stop`);
      }
      try {
        await p.ir("#/services", "#svc-table");
        await p.esperarTexto(`#svc-table tr[data-id="${s.id}"]`, "Detenido");
      } catch (error) {
        await reanudarServicio(p);
        throw error;
      }
    },
    despues: reanudarServicio,
    recorte: "#view",
    marcas: [
      { sel: "#svc-cards .card", etiqueta: "Tarjetas del servidor" },
      { sel: "#btn-server-restart", etiqueta: "Reiniciar servidor completo (deshabilitado si corre como consola)" },
      { sel: "#svc-table .tag", texto: "Detenido", etiqueta: "Servicio detenido a mano" },
      { sel: "#svc-table .tag", texto: "Deshabilitado", etiqueta: "Servicio apagado en la configuración" },
      { sel: "#svc-table input[data-act=auto]", etiqueta: "Auto: reinicio automático" },
      { sel: "#svc-table button[data-act=start]:not([disabled])", etiqueta: "Iniciar, Detener y Reiniciar" },
    ],
  },
  {
    archivo: "web-sesiones.png",
    antes: async p => {
      // Fuera los puestos que haya (por ejemplo, el del capturador del cliente,
      // que lleva el nombre real de este PC): solo quedan los ficticios de abajo.
      const { cuerpo: actuales } = await p.api("GET", "/api/system/desktop-seats");
      for (const s of actuales?.seats ?? []) await p.api("POST", "/api/system/desktop-seats/release", { seat: s.seat });
      try {
        const { cuerpo: servicios } = await p.api("GET", "/api/system/services");
        const version = servicios.server.version;
        const { cuerpo: equipos } = await p.api("GET", "/api/devices");
        const id = nombre => {
          const e = equipos.find(x => x.name === nombre);
          if (!e) throw new Error(`No existe el equipo «${nombre}»`);
          return e.id;
        };
        const operador = await entrarComoCliente(p, "jperez", "PUESTO-CENTRAL-01", version, true);
        await entrarComoCliente(p, "guardia.cd", "PORTERIA-CD", version, false);
        await mirar(p, operador, id("Cámara 01"), "Main");
        await mirar(p, operador, id("Cámara 03"), "Sub");
        await mirar(p, operador, id("Domo PTZ 08"), "Main");
        // Y el propio administrador, desde el panel web.
        await mirar(p, await p.evaluar(() => localStorage.getItem("tcvms_token")), id("Cámara 05"), "Sub");
        await p.ir("#/sessions", "#view");
        for (let i = 0; i < 40; i++) {
          const n = await p.evaluar(() => ({
            video: document.querySelectorAll("#view .btn-kick").length,
            puestos: document.querySelectorAll("#view .btn-seat-release").length,
          }));
          if (n.video >= 4 && n.puestos >= 2) break;
          await pausa(500);
        }
        await p.esperarTexto("#view", "Conectado");
      } catch (error) {
        await deshacerSesiones(p);
        throw error;
      }
    },
    despues: deshacerSesiones,
    recorte: "#view",
    marcas: [
      { sel: "#view h3 span", texto: "puestos", contenido: true, etiqueta: "Puestos de la licencia en uso" },
      { sel: "#view .tag.on", texto: "Conectado", etiqueta: "Cliente abierto y conectado" },
      { sel: "#view .tag.warn", texto: "Sin conexión", etiqueta: "Sesión colgada: el cliente se cerró sin cerrar sesión" },
      { sel: "#view .btn-seat-release", etiqueta: "Desconectar o Liberar puesto" },
      { sel: "#view h3", texto: "Sesiones de video activas", etiqueta: "Sesiones de video activas: una fila por cámara abierta" },
      { sel: "#view .btn-kick", etiqueta: "Expulsar: corta esa transmisión" },
    ],
  },
  {
    archivo: "web-licencia.png",
    antes: async p => {
      await p.ir("#/license", "#lic-import");
      // El nombre del PC donde corre la demo no va en el manual.
      await p.evaluar(() => {
        for (const el of document.querySelectorAll("#view .card .muted")) {
          const t = el.textContent;
          if (/ · v\d/.test(t) && el.closest(".card").textContent.includes("Este equipo"))
            el.textContent = t.replace(/^.*( · v\d)/, "SERVIDOR-VMS$1");
        }
      });
    },
    recorte: "#view",
    marcas: [
      { sel: "#view .card .tag", etiqueta: "Estado y modalidad" },
      { sel: "#view .card code", etiqueta: "Identificador de este equipo" },
      { sel: "#lic-activate", etiqueta: "Activar en línea y Generar solicitud (.req)" },
      { sel: "#lic-import", etiqueta: "Importar archivo .lic" },
      { sel: "#lic-refresh", etiqueta: "Revalidar ahora y Desactivar en este equipo" },
      { sel: "#view h3", texto: "Módulos y cupos", etiqueta: "Módulos y cupos (sigue más abajo)" },
    ],
  },
];
