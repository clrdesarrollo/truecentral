namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Ubicaciones: la capa lógica del inventario. Los equipos físicos siguen en sus
// módulos (Dispositivos); lo que el operador usa —cámaras, puertas, áreas y
// zonas de alarma, cercos, parlantes, citófonos— son RECURSOS, y cada recurso
// vive en UNA ubicación del árbol (sitio → edificio → piso → sector → punto).
// Los enums viajan como texto en JSON.
// ---------------------------------------------------------------------------

/// <summary>Tipo de nodo del árbol. Solo cambia el ícono y cómo se nombra:
/// cualquier tipo puede colgar de cualquier otro.</summary>
public enum LocationKind { Site, Building, Floor, Sector, Point }

/// <summary>Qué es un recurso (y de qué tabla sale su Id).</summary>
public enum ResourceKind
{
    /// <summary>Canal de video (tabla Channels).</summary>
    Camera,
    /// <summary>Puerta de un equipo de control de acceso (AccessDoors).</summary>
    Door,
    /// <summary>Área (partición) de un panel de alarma (AlarmAreas).</summary>
    Partition,
    /// <summary>Zona (detector) de un panel de alarma (AlarmZones).</summary>
    Zone,
    /// <summary>Panel de cerco eléctrico (CercoPanels).</summary>
    Fence,
    /// <summary>Parlante IP (Speakers).</summary>
    Speaker,
    /// <summary>Frente de citofonía (Intercoms).</summary>
    Intercom,
}

/// <summary>Estado resumido de un recurso: el color del punto en las listas.</summary>
public enum ResourceHealth { Unknown, Ok, Warning, Alarm, Offline, Disabled }

/// <summary>Nodo del árbol de ubicaciones (la lista es plana: el árbol se
/// arma por <paramref name="ParentId"/>).</summary>
/// <param name="ResourceCount">Recursos ubicados directamente en este nodo
/// (sin contar los de sus sububicaciones).</param>
public sealed record LocationDto(
    int Id, int? ParentId, string Name, LocationKind Kind,
    string? Description, string? Address, double? Latitude, double? Longitude,
    int ResourceCount);

/// <summary>Alta o modificación de una ubicación. Cambiar
/// <paramref name="ParentId"/> la mueve con todo lo que tiene adentro.</summary>
public sealed record LocationSaveRequest(
    string Name, LocationKind Kind, int? ParentId,
    string? Description = null, string? Address = null,
    double? Latitude = null, double? Longitude = null);

/// <summary>Recurso tal como lo listan la página Recursos y el árbol por ubicación.</summary>
/// <param name="Source">Equipo físico que lo provee ("NVR bodega").</param>
/// <param name="SourceModule">Página del equipo en el panel web (ruta sin
/// "#/": devices, access, alarm-panels, cerco-panels, speakers, intercoms).</param>
/// <param name="SourceId">Id del equipo en su módulo.</param>
/// <param name="Detail">Dónde está dentro del equipo ("Canal 3", "Puerta 1").
/// Cuando el recurso es el equipo mismo (parlante, citófono, cerco), su
/// dirección o identificador.</param>
/// <param name="State">Estado en palabras ("En línea", "Armada", "Anulada").</param>
/// <param name="Enabled">Visible para los operadores (canales y puertas se
/// pueden deshabilitar sin borrarlos).</param>
public sealed record ResourceDto(
    ResourceKind Kind, int Id, string Name, int? LocationId,
    string Source, string SourceModule, int SourceId, string? Detail,
    ResourceHealth Health, string State, bool Enabled);

/// <summary>Referencia a un recurso de cualquier tipo.</summary>
public sealed record ResourceRef(ResourceKind Kind, int Id);

/// <summary>Ubica recursos en una ubicación; con <paramref name="LocationId"/>
/// null los devuelve a "Por ubicar".</summary>
public sealed record ResourceLocationRequest(int? LocationId, List<ResourceRef> Resources);

/// <summary>Resultado de ubicar recursos en bloque.</summary>
public sealed record ResourceLocationResult(int Updated, string Message);

// ---------------------------------------------------------------------------
// Ficha del recurso: lo que el VMS sabe de un recurso más allá de lo que
// informa su equipo. Pestañas General, Equipo, Cámaras asociadas,
// Automatizaciones e Historial (estas dos se piden aparte, al abrirlas).
// ---------------------------------------------------------------------------

/// <summary>Ficha de un recurso: pestañas General, Equipo y Cámaras asociadas.</summary>
/// <param name="LocationPath">Ruta legible de su ubicación ("Casa matriz › Edificio A"); null = por ubicar.</param>
/// <param name="Instructions">Consignas para el operador: qué hacer cuando este recurso avisa algo.</param>
/// <param name="CanRename">El nombre es del VMS y se puede cambiar desde la ficha.</param>
/// <param name="RenameNote">Por qué no se puede renombrar aquí (lo define el panel, se cambia en su módulo).</param>
/// <param name="SupportsCameras">El tipo admite cámaras asociadas (todos menos las cámaras mismas).</param>
/// <param name="SnapshotPath">Cámaras: ruta de la API que devuelve su imagen actual.</param>
public sealed record ResourceDetailDto(
    ResourceDto Resource,
    string? LocationPath,
    string? Description,
    string? Instructions,
    bool CanRename,
    string? RenameNote,
    ResourceSourceDto Source,
    IReadOnlyList<ResourceFactDto> Facts,
    bool SupportsCameras,
    IReadOnlyList<ResourceCameraDto> Cameras,
    string? SnapshotPath = null);

/// <summary>Equipo físico que provee el recurso (el mismo recurso, si es un equipo de un solo recurso).</summary>
/// <param name="Module">Página del equipo en el panel web (ruta sin "#/").</param>
/// <param name="Status">Estado de conexión en palabras.</param>
public sealed record ResourceSourceDto(
    string Module, int Id, string Name, string? Model, string? SerialNumber, string? Firmware,
    string? Address, string Status, DateTime? LastSeenAt);

/// <summary>Una característica del recurso tal como la conoce el VMS ("Tipo de zona" → "Perimetral").</summary>
public sealed record ResourceFactDto(string Label, string Value);

/// <summary>Cámara asociada a un recurso. La primera de la lista es la principal.</summary>
/// <param name="Fixed">No se quita desde la ficha: viene de la configuración del módulo
/// (p. ej. la cámara del frente de un citófono).</param>
public sealed record ResourceCameraDto(
    int ChannelId, string Name, int DeviceId, string DeviceName, int ChannelNumber, bool IsOnline, bool Fixed);

/// <summary>Cambios de la pestaña General.</summary>
/// <param name="Name">Nombre nuevo; null = no tocarlo (o el tipo no se renombra desde la ficha).</param>
/// <param name="LocationId">Ubicación del recurso; null = por ubicar.</param>
public sealed record ResourceProfileUpdateRequest(string? Name, int? LocationId, string? Description, string? Instructions);

/// <summary>Cámaras asociadas, en orden: la primera es la principal.</summary>
public sealed record ResourceCamerasRequest(List<int> ChannelIds);

/// <summary>Automatización que usa el recurso.</summary>
/// <param name="Role">"trigger" (lo dispara), "condition" (lo consulta) o "action" (actúa sobre él).</param>
/// <param name="Detail">Cómo lo usa, en palabras ("Abrir la puerta", "Eventos de esta zona").</param>
public sealed record ResourceWorkflowUseDto(int WorkflowId, string Name, bool Enabled, string Role, string Detail);

/// <summary>Automatizaciones que nombran al recurso, más las generales que también lo alcanzan.</summary>
/// <param name="GeneralCount">Automatizaciones sin equipo específico que igual se disparan con este recurso.</param>
/// <param name="GeneralNote">Qué significa ese número para este tipo de recurso.</param>
public sealed record ResourceWorkflowsDto(IReadOnlyList<ResourceWorkflowUseDto> Uses, int GeneralCount, string? GeneralNote);

/// <summary>
/// Resumen de un recurso para el puesto del operador cuando ese recurso avisa
/// algo: qué es, dónde está, qué hacer (consignas) y qué cámaras mirar (la
/// primera es la principal).
/// </summary>
/// <param name="OriginKey">
/// Recurso que originó el evento ("Zone:4"), el más específico: puede no ser
/// el del resumen (una zona sin ficha usa las consignas de su área). Es el que
/// decide quién ve el aviso (alcance por ubicación) y el que identifica al
/// evento entre la verificación y la alerta de una automatización.
/// </param>
public sealed record ResourceBriefingDto(
    ResourceKind Kind, int Id, string Name, string? LocationPath,
    string? Description, string? Instructions, IReadOnlyList<ResourceCameraDto> Cameras,
    string? OriginKey = null)
{
    /// <summary>Tiene algo que mostrarle al operador además de la ubicación.</summary>
    public bool HasGuidance => !string.IsNullOrWhiteSpace(Instructions) || Cameras.Count > 0;
}

/// <summary>Orden sobre todas las áreas de alarma de una ubicación (y de sus sububicaciones).</summary>
/// <param name="Command">"arm-away" (armar total), "arm-stay" (armar parcial) o "disarm".</param>
/// <param name="DryRun">true = solo decir qué áreas se tocarían, sin ordenar nada.</param>
public sealed record LocationCommandRequest(string Command, bool DryRun = false);

/// <summary>Resultado de la orden en un área.</summary>
public sealed record LocationCommandItemDto(string Area, string Panel, bool Success, string? Error);

/// <summary>Resultado de una orden por ubicación (o la lista de áreas que tocaría, si fue de prueba).</summary>
public sealed record LocationCommandResultDto(string Message, bool Executed, IReadOnlyList<LocationCommandItemDto> Items);

/// <summary>Entrada del historial del recurso.</summary>
/// <param name="Source">"event" (lo informó el equipo o el módulo), "alert" (aviso de una automatización
/// originado por el recurso, con su acuse de recibo) o "audit" (cambio u orden registrada en la bitácora).</param>
/// <param name="Level">"info", "warning" o "alarm": el color de la entrada.</param>
public sealed record ResourceHistoryItemDto(
    DateTime Timestamp, string Source, string Title, string? Detail, string? User, string Level);
