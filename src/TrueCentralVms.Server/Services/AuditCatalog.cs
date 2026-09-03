namespace TrueCentralVms.Server.Services;

/// <summary>
/// Catálogo de categorías y acciones de la bitácora. Las CLAVES son estables
/// (van a la base y a los filtros de la API); las etiquetas en español son
/// solo presentación y las consume el panel vía /api/audit/catalog. Registrar
/// una acción nueva aquí basta para que aparezca en los filtros del panel.
/// </summary>
public static class AuditCatalog
{
    public sealed record ActionInfo(string Key, string Label);
    public sealed record CategoryInfo(string Key, string Label, IReadOnlyList<ActionInfo> Actions);

    public static readonly IReadOnlyList<CategoryInfo> Categories =
    [
        new("auth", "Autenticación",
        [
            new("login", "Inicio de sesión"),
            new("login-failed", "Inicio de sesión rechazado"),
            new("login-blocked", "Login bloqueado (clave vencida)"),
            new("logout", "Cierre de sesión"),
            new("password-changed", "Cambio de contraseña"),
            new("setup-admin", "Creación del primer administrador"),
        ]),
        new("users", "Usuarios",
        [
            new("user-created", "Usuario creado"),
            new("user-updated", "Usuario modificado"),
            new("user-deleted", "Usuario eliminado"),
        ]),
        new("devices", "Dispositivos",
        [
            new("device-created", "Dispositivo agregado"),
            new("device-updated", "Dispositivo modificado"),
            new("device-deleted", "Dispositivo eliminado"),
            new("device-revalidated", "Dispositivo revalidado"),
            new("device-probed", "Prueba de conexión"),
            new("channel-updated", "Canal modificado"),
            new("discovery-scan", "Búsqueda de equipos en la red"),
        ]),
        new("live", "Video en vivo",
        [
            new("view-started", "Comenzó a ver un canal"),
            new("view-stopped", "Dejó de ver un canal"),
            new("session-kicked", "Sesión de video expulsada"),
            new("snapshot-web", "Miniatura solicitada desde el panel"),
            new("snapshot-client", "Captura guardada (cliente)"),
            new("clip-started", "Grabación local iniciada (cliente)"),
            new("clip-saved", "Grabación local guardada (cliente)"),
        ]),
        new("ptz", "Control PTZ",
        [
            new("ptz-moved", "Operó el PTZ"),
            new("ptz-preset-goto", "Fue a un preset"),
            new("ptz-preset-saved", "Guardó un preset"),
            new("ptz-preset-deleted", "Borró un preset"),
        ]),
        new("playback", "Reproducción",
        [
            new("search", "Búsqueda de grabaciones"),
            new("play", "Reproducción de grabación"),
            new("export", "Exportación de grabación (servidor)"),
            new("export-saved", "Exportación guardada (cliente)"),
            new("export-cancelled", "Exportación cancelada (cliente)"),
            new("export-failed", "Exportación fallida (cliente)"),
            new("snapshot-saved", "Captura de reproducción (cliente)"),
            new("clip-saved", "Cápsula de reproducción (cliente)"),
        ]),
        new("wall", "Muro de video",
        [
            new("decoder-created", "Decodificador agregado"),
            new("decoder-updated", "Decodificador modificado"),
            new("decoder-deleted", "Decodificador eliminado"),
            new("decoder-tested", "Decodificador probado"),
            new("wall-created", "Muro creado"),
            new("wall-updated", "Muro modificado"),
            new("wall-deleted", "Muro eliminado"),
            new("wall-synced", "Muro sincronizado"),
            new("window-assigned", "Cámara asignada a ventana"),
            new("external-assigned", "Fuente externa asignada"),
            new("window-cleared", "Ventana limpiada"),
            new("wall-cleared", "Muro limpiado completo"),
            new("windows-swapped", "Ventanas intercambiadas"),
            new("window-mode-changed", "División de monitor cambiada"),
            new("windows-grouped", "Ventanas agrupadas"),
            new("window-ungrouped", "Ventana desagrupada"),
            new("window-subdivided", "Ventana subdividida"),
            new("fullscreen-entered", "Pantalla completa (monitor)"),
            new("fullscreen-exited", "Salió de pantalla completa (monitor)"),
            new("wall-fullscreen-entered", "Pantalla completa (muro)"),
            new("wall-fullscreen-exited", "Salió de pantalla completa (muro)"),
            new("floating-created", "Ventana flotante creada"),
            new("floating-moved", "Ventana flotante movida"),
            new("floating-assigned", "Cámara asignada a flotante"),
            new("floating-fullscreen", "Flotante a pantalla completa"),
            new("floating-deleted", "Ventana flotante eliminada"),
            new("layout-saved", "Layout guardado"),
            new("layout-deleted", "Layout eliminado"),
            new("layout-applied", "Layout aplicado"),
        ]),
        new("anpr", "Patentes (ANPR)",
        [
            new("source-enabled", "Fuente de patentes activada"),
            new("source-disabled", "Fuente de patentes desactivada"),
            new("event-deleted", "Reconocimiento eliminado"),
            new("search", "Búsqueda en el historial"),
        ]),
        new("alarms", "Paneles de alarma",
        [
            new("panel-created", "Panel agregado"),
            new("panel-updated", "Panel modificado"),
            new("panel-deleted", "Panel eliminado"),
            new("panel-probed", "Prueba de conexión con panel"),
            new("panel-refreshed", "Estado del panel actualizado a pedido"),
            new("area-armed", "Área armada"),
            new("area-disarmed", "Área desarmada"),
            new("alarm-cleared", "Alarma borrada/silenciada"),
            new("zone-bypassed", "Zona anulada (bypass)"),
            new("zone-restored", "Zona restituida"),
            new("alarm-received", "Alarma recibida del panel"),
            new("panel-offline", "Panel sin conexión"),
            new("panel-online", "Panel recuperó conexión"),
            new("arc-started", "Receptor de alarmas (SIA DC-09) iniciado"),
            new("arc-rejected", "Reporte SIA DC-09 rechazado"),
            new("search", "Consulta del historial de eventos"),
        ]),
        new("speakers", "Parlantes IP",
        [
            new("speaker-created", "Parlante agregado"),
            new("speaker-updated", "Parlante modificado"),
            new("speaker-deleted", "Parlante eliminado"),
            new("speaker-probed", "Prueba de conexión con parlante"),
            new("play", "Reproducción en parlante"),
            new("play-failed", "Reproducción rechazada por el parlante"),
            new("stop", "Reproducción detenida"),
            new("talk-started", "Voz en vivo iniciada"),
            new("talk-stopped", "Voz en vivo terminada"),
            new("talk-rejected", "Voz en vivo rechazada"),
            new("volume-changed", "Volumen cambiado"),
            new("library-uploaded", "Audio subido a la biblioteca del parlante"),
            new("library-deleted", "Audio borrado de la biblioteca del parlante"),
            new("library-renamed", "Audio renombrado en la biblioteca del parlante"),
            new("tts-created", "Audio de texto a voz creado"),
            new("speaker-offline", "Parlante sin conexión"),
            new("speaker-online", "Parlante recuperó conexión"),
        ]),
        new("workflows", "Automatizaciones",
        [
            new("workflow-created", "Automatización creada"),
            new("workflow-updated", "Automatización modificada"),
            new("workflow-deleted", "Automatización eliminada"),
            new("workflow-tested", "Automatización probada a mano"),
            new("workflow-executed", "Automatización ejecutada"),
            new("workflow-failed", "Automatización con acciones fallidas"),
            new("alert-acknowledged", "Alerta confirmada por un operador"),
            new("smtp-updated", "Correo saliente configurado"),
            new("smtp-tested", "Correo de prueba enviado"),
            new("audio-uploaded", "Sonido de parlante subido"),
            new("audio-deleted", "Sonido de parlante eliminado"),
            new("search", "Consulta del historial de ejecuciones"),
        ]),
        new("system", "Sistema",
        [
            new("server-started", "Servidor iniciado"),
            new("audit-purged", "Bitácora purgada por retención"),
            new("audit-exported", "Bitácora exportada a CSV"),
        ]),
    ];

    /// <summary>Acciones que el cliente de escritorio puede reportar, y a qué
    /// categoría/acción de la bitácora se traducen. Todo lo demás se rechaza:
    /// un cliente no puede inventar eventos de otras categorías.</summary>
    public static readonly IReadOnlyDictionary<string, (string Category, string Action, string Detail)> ClientActions =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["live-snapshot"] = ("live", "snapshot-client", "Guardó una captura de video en vivo"),
            ["live-clip-start"] = ("live", "clip-started", "Inició una grabación local de video en vivo"),
            ["live-clip-saved"] = ("live", "clip-saved", "Guardó una grabación local de video en vivo"),
            ["playback-snapshot"] = ("playback", "snapshot-saved", "Guardó una captura durante la reproducción"),
            ["playback-clip-saved"] = ("playback", "clip-saved", "Guardó una cápsula durante la reproducción"),
            ["playback-export-saved"] = ("playback", "export-saved", "Guardó una exportación de grabación"),
            ["playback-export-cancelled"] = ("playback", "export-cancelled", "Canceló una exportación de grabación"),
            ["playback-export-failed"] = ("playback", "export-failed", "Falló una exportación de grabación"),
        };
}
