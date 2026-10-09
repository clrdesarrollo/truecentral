namespace TrueCentralVms.Core.Domain;

/// <summary>
/// Catálogo de permisos (como código: la clave es estable y es lo que se guarda
/// en la base). Un rol es un conjunto de estas claves; un usuario puede tener
/// varios roles y obtiene la UNIÓN de sus permisos. El rol de sistema
/// Administrador tiene todos, también los que se agreguen en versiones futuras.
/// El alcance por ubicación (qué recursos) es aparte, en el usuario: el permiso
/// dice QUÉ puede hacer y el alcance, DÓNDE.
/// </summary>
public static class Permissions
{
    // Video
    public const string LiveView = "live.view";
    public const string LivePtz = "live.ptz";
    public const string LiveViewsManage = "live.views";
    public const string PlaybackView = "playback.view";
    public const string PlaybackExport = "playback.export";
    public const string WallOperate = "wall.operate";
    public const string WallLayouts = "wall.layouts";

    // Monitoreo y operación
    public const string EventsAttend = "events.attend";
    public const string LocationsCommand = "locations.command";
    public const string AlarmsMonitor = "alarms.monitor";
    public const string AlarmsOperate = "alarms.operate";
    public const string CercoMonitor = "cerco.monitor";
    public const string CercoOperate = "cerco.operate";
    public const string AccessMonitor = "access.monitor";
    public const string AccessDoors = "access.doors";
    public const string AccessRecordsExport = "access.records.export";
    public const string AnprView = "anpr.view";
    public const string AnprDelete = "anpr.delete";
    public const string SpeakersPlay = "speakers.play";
    public const string IntercomAnswer = "intercom.answer";

    // Personas
    public const string PersonsView = "persons.view";
    public const string PersonsManage = "persons.manage";

    // Configuración
    public const string DevicesManage = "devices.manage";
    public const string AccessConfigure = "access.configure";
    public const string AlarmsConfigure = "alarms.configure";
    public const string CercoConfigure = "cerco.configure";
    public const string SpeakersConfigure = "speakers.configure";
    public const string SoundsManage = "sounds.manage";
    public const string IntercomConfigure = "intercom.configure";
    public const string ResourcesManage = "resources.manage";
    public const string WorkflowsView = "workflows.view";
    public const string WorkflowsManage = "workflows.manage";
    public const string MaintenanceView = "maintenance.view";
    public const string MaintenanceManage = "maintenance.manage";

    // Sistema y seguridad
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string AuditView = "audit.view";
    public const string SessionsManage = "sessions.manage";
    public const string SystemServices = "system.services";
    public const string SystemLicense = "system.license";

    /// <summary>Un permiso del catálogo.</summary>
    /// <param name="Requires">Permisos que este supone (se agregan solos al marcarlo).</param>
    /// <param name="Sensitive">Da control sobre la seguridad del sistema: la pantalla lo resalta.</param>
    public sealed record Definition(string Key, string Group, string Label, string Description,
        string[] Requires, bool Sensitive = false);

    /// <summary>Grupos en el orden en que se muestran.</summary>
    public static readonly string[] Groups =
        ["Video", "Monitoreo y operación", "Personas", "Configuración", "Sistema y seguridad"];

    public static readonly IReadOnlyList<Definition> All =
    [
        new(LiveView, "Video", "Ver video en vivo",
            "Abrir cámaras en vivo y sus imágenes instantáneas.", []),
        new(LivePtz, "Video", "Mover cámaras PTZ",
            "Mover, acercar y llamar posiciones guardadas de las cámaras PTZ.", [LiveView]),
        new(LiveViewsManage, "Video", "Guardar vistas",
            "Crear, modificar y borrar vistas (grupos de cámaras con su distribución).", [LiveView]),
        new(PlaybackView, "Video", "Ver grabaciones",
            "Buscar y reproducir video grabado.", []),
        new(PlaybackExport, "Video", "Exportar grabaciones",
            "Descargar tramos de video grabado al equipo del operador.", [PlaybackView]),
        new(WallOperate, "Video", "Operar el muro de video",
            "Poner cámaras en el muro, limpiarlo y aplicar diseños guardados.", [LiveView]),
        new(WallLayouts, "Video", "Guardar diseños del muro",
            "Guardar y borrar diseños del muro de video.", [WallOperate]),

        new(EventsAttend, "Monitoreo y operación", "Atender alertas",
            "Ver el centro de eventos y dar por atendidas las alertas.", []),
        new(LocationsCommand, "Monitoreo y operación", "Órdenes por ubicación",
            "Armar, desarmar o abrir todo lo de una ubicación con una sola orden.", []),
        new(AlarmsMonitor, "Monitoreo y operación", "Ver alarmas",
            "Ver el estado de los paneles de alarma y sus eventos.", []),
        new(AlarmsOperate, "Monitoreo y operación", "Operar alarmas",
            "Armar, desarmar, reponer alarmas y anular zonas.", [AlarmsMonitor]),
        new(CercoMonitor, "Monitoreo y operación", "Ver cercos eléctricos",
            "Ver el estado de los cercos eléctricos y sus eventos, y exportar su historial a CSV.", []),
        new(CercoOperate, "Monitoreo y operación", "Operar cercos eléctricos",
            "Armar, desarmar, silenciar y activar la sirena de los cercos.", [CercoMonitor]),
        new(AccessMonitor, "Monitoreo y operación", "Ver control de acceso",
            "Ver puertas, su estado y los registros de acceso.", []),
        new(AccessDoors, "Monitoreo y operación", "Abrir y cerrar puertas",
            "Abrir, cerrar o dejar fijas las puertas a distancia.", [AccessMonitor]),
        new(AccessRecordsExport, "Monitoreo y operación", "Exportar registros de acceso",
            "Descargar los registros de acceso en PDF o Excel.", [AccessMonitor]),
        new(AnprView, "Monitoreo y operación", "Ver patentes",
            "Ver las lecturas de patentes con sus fotos.", []),
        new(AnprDelete, "Monitoreo y operación", "Borrar lecturas de patentes",
            "Eliminar lecturas de patentes guardadas.", [AnprView]),
        new(SpeakersPlay, "Monitoreo y operación", "Usar parlantes",
            "Reproducir audios, hablar y ajustar el volumen de los parlantes IP.", []),
        new(IntercomAnswer, "Monitoreo y operación", "Atender citofonía",
            "Contestar, rechazar y cortar llamadas, y abrir la puerta del frente.", []),

        new(PersonsView, "Personas", "Ver personas",
            "Ver las personas registradas, sus fotos, tarjetas y niveles de acceso.", []),
        new(PersonsManage, "Personas", "Administrar personas",
            "Crear, modificar y borrar personas, sus credenciales, niveles de acceso y horarios.", [PersonsView]),

        new(DevicesManage, "Configuración", "Fuentes de video",
            "Agregar y configurar cámaras, grabadores, decodificadores y muros; buscar equipos en la red.", []),
        new(AccessConfigure, "Configuración", "Equipos de acceso",
            "Agregar y configurar terminales y puertas, y sincronizarlos.", []),
        new(AlarmsConfigure, "Configuración", "Paneles de alarma",
            "Agregar y configurar paneles de alarma y la receptora.", []),
        new(CercoConfigure, "Configuración", "Paneles de cerco",
            "Agregar y configurar paneles de cerco, sus controles remotos y su firmware.", []),
        new(SpeakersConfigure, "Configuración", "Parlantes IP",
            "Agregar y configurar parlantes IP y su biblioteca de audios.", []),
        new(SoundsManage, "Configuración", "Biblioteca de sonidos",
            "Subir, ajustar y borrar los sonidos que usan las automatizaciones.", []),
        new(IntercomConfigure, "Configuración", "Citofonía",
            "Agregar y configurar frentes y monitores de citofonía.", []),
        new(ResourcesManage, "Configuración", "Recursos y ubicaciones",
            "Crear el árbol de ubicaciones, ubicar los recursos y escribir sus fichas.", []),
        new(WorkflowsView, "Configuración", "Ver automatizaciones",
            "Ver las automatizaciones y su historial de ejecuciones.", []),
        new(WorkflowsManage, "Configuración", "Editar automatizaciones",
            "Crear, modificar, probar y borrar automatizaciones; correo de salida.", [WorkflowsView]),
        new(MaintenanceView, "Configuración", "Ver hora y mantenimiento",
            "Ver la hora de los equipos y revisarla.", []),
        new(MaintenanceManage, "Configuración", "Mantenimiento de equipos",
            "Corregir la hora, reiniciar y restablecer equipos.", [MaintenanceView]),

        new(UsersManage, "Sistema y seguridad", "Usuarios",
            "Crear, modificar y deshabilitar usuarios, y asignarles roles y alcance " +
            "(nunca más permisos ni más ubicaciones de los que tiene quien los asigna).", [], Sensitive: true),
        new(RolesManage, "Sistema y seguridad", "Roles",
            "Crear y modificar roles (solo con permisos que tiene quien los edita).", [], Sensitive: true),
        new(AuditView, "Sistema y seguridad", "Bitácora de auditoría",
            "Consultar y exportar la bitácora de auditoría.", [], Sensitive: true),
        new(SessionsManage, "Sistema y seguridad", "Sesiones",
            "Ver quién está conectado, cortar transmisiones y liberar puestos.", []),
        new(SystemServices, "Sistema y seguridad", "Servicios del servidor",
            "Detener, iniciar y reiniciar los servicios y el servidor.", [], Sensitive: true),
        new(SystemLicense, "Sistema y seguridad", "Licencia",
            "Activar, renovar y desactivar la licencia del sistema.", [], Sensitive: true),
    ];

    private static readonly Dictionary<string, Definition> ByKey = All.ToDictionary(d => d.Key);

    public static bool IsValid(string? key) => key is not null && ByKey.ContainsKey(key);

    public static Definition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static string LabelOf(string key) => ByKey.TryGetValue(key, out var d) ? d.Label : key;

    /// <summary>Las claves válidas, sin repetir y con lo que cada una supone.</summary>
    public static HashSet<string> Normalize(IEnumerable<string>? keys)
    {
        var result = new HashSet<string>();
        var pending = new Stack<string>((keys ?? []).Where(IsValid));
        while (pending.TryPop(out var key))
            if (result.Add(key))
                foreach (var required in ByKey[key].Requires)
                    pending.Push(required);
        return result;
    }

    // -----------------------------------------------------------------------
    // Roles de sistema y plantillas
    // -----------------------------------------------------------------------

    /// <summary>Permisos del rol de sistema Operador al instalar (los que tenía el operador antes de los roles).</summary>
    public static readonly string[] OperatorDefaults =
    [
        LiveView, LivePtz, LiveViewsManage, PlaybackView, PlaybackExport, WallOperate, WallLayouts,
        EventsAttend, LocationsCommand, AlarmsMonitor, AlarmsOperate, CercoMonitor, CercoOperate,
        AccessMonitor, AccessDoors, AccessRecordsExport, AnprView, SpeakersPlay, IntercomAnswer,
        PersonsView, WorkflowsView, MaintenanceView,
    ];

    /// <summary>Punto de partida para crear un rol (no se guardan: se copian al formulario).</summary>
    public sealed record Template(string Key, string Name, string Description, string[] Permissions);

    public static readonly IReadOnlyList<Template> Templates =
    [
        new("guard", "Guardia",
            "Mira el video en vivo, atiende alertas y citofonía, y abre puertas.",
            [LiveView, LivePtz, EventsAttend, AlarmsMonitor, CercoMonitor, AccessMonitor, AccessDoors,
             AnprView, SpeakersPlay, IntercomAnswer]),
        new("monitoring", "Operador de central",
            "Opera todo lo que monitorea: alarmas, cercos, puertas, muro y órdenes por ubicación.",
            OperatorDefaults),
        new("investigator", "Investigador",
            "Revisa grabaciones, registros y patentes, y los exporta; no opera equipos.",
            [LiveView, PlaybackView, PlaybackExport, AccessMonitor, AccessRecordsExport, AnprView,
             PersonsView, EventsAttend]),
        new("access-admin", "Administrador de acceso",
            "Mantiene personas, credenciales, niveles y horarios, y monitorea las puertas.",
            [PersonsView, PersonsManage, AccessMonitor, AccessDoors, AccessRecordsExport]),
        new("technician", "Técnico",
            "Instala y configura equipos y su mantenimiento; no administra personas ni usuarios.",
            [LiveView, LivePtz, PlaybackView, DevicesManage, AccessConfigure, AlarmsConfigure, CercoConfigure,
             SpeakersConfigure, IntercomConfigure, ResourcesManage, MaintenanceView, MaintenanceManage,
             AlarmsMonitor, CercoMonitor, AccessMonitor, SessionsManage]),
        new("auditor", "Auditor",
            "Solo consulta: bitácora, registros y grabaciones, sin operar nada.",
            [AuditView, PlaybackView, AccessMonitor, AnprView, AlarmsMonitor, CercoMonitor, WorkflowsView,
             MaintenanceView]),
    ];
}
