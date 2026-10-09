// Capítulo 2 · Primeros pasos (panel web).
// Cada captura: archivo, antes(pagina) para dejar la pantalla lista, recorte
// (selector, rectángulo o toda la ventana) y marcas {sel, texto?, en?, etiqueta}.

export default [
  {
    archivo: "web-inicio-sesion.png",
    sinSesion: true,
    antes: async p => { await p.esperar("#auth-screen input[type=password]"); },
    recorte: "#auth-screen .auth-card",
    margen: 40,
    marcas: [
      { sel: "#auth-body input:not([type=password])", etiqueta: "Usuario" },
      { sel: "#auth-body input[type=password]", etiqueta: "Contraseña" },
      { sel: "#auth-body button[type=button]", en: "arriba-der", etiqueta: "Mostrar la contraseña" },
      { sel: "#auth-body button[type=submit]", etiqueta: "Ingresar" },
      { sel: "#auth-version", contenido: true, en: "der-fuera", etiqueta: "Versión del servidor" },
    ],
  },
  {
    archivo: "web-panel.png",
    antes: async p => {
      await p.ir("#/", "#view .cards");
      await p.clic("#btn-user-menu");
      await p.esperar("#user-menu:not(.hidden)");
    },
    marcas: [
      { sel: "#nav", en: "arriba", etiqueta: "Menú lateral: solo las páginas que su usuario puede abrir" },
      { sel: "#view .cards .card", etiqueta: "Resumen del sistema" },
      { sel: "#btn-user-menu", etiqueta: "Menú del usuario" },
      { sel: "#btn-logout", etiqueta: "Cerrar sesión" },
      { sel: ".sidebar-footer", etiqueta: "Conexión con el servidor" },
    ],
  },
];
