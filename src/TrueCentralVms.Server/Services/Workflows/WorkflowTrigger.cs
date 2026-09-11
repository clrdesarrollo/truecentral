using System.Text;
using System.Text.Json;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Lo que ocurrió y disparó (o puede disparar) una automatización. Es un
/// objeto plano y ya resuelto: el motor no vuelve a la base para evaluar
/// condiciones ni para reemplazar las marcas de los textos.
///
/// <see cref="Fields"/> son las marcas disponibles para las plantillas
/// ({panel}, {zona}, {patente}, {persona}...): las llena quien construye el
/// disparo, de modo que un disparador nuevo solo tiene que aportar sus propios
/// campos. Las propiedades tipadas son las que usan las condiciones.
/// </summary>
public sealed record WorkflowTrigger(
    string Type,
    string Summary,
    IReadOnlyDictionary<string, string> Fields,
    DateTime At,
    // --- Paneles de alarma ---
    int? PanelId = null,
    AlarmEventKind? Kind = null,
    AlarmSeverity? Severity = null,
    int? AreaNumber = null,
    int? ZoneNumber = null,
    string? Code = null,
    string? Source = null,
    string? Text = null,
    AlarmPanelStatus? PanelStatus = null,
    // --- Equipos (cámaras/grabadores, terminales de acceso, parlantes) ---
    /// <summary>"video" | "access" | "speaker".</summary>
    string? DeviceKind = null,
    int? DeviceId = null,
    int? AccessDeviceId = null,
    int? SpeakerId = null,
    /// <summary>"Online" | "Offline" | "AuthFailed".</summary>
    string? DeviceStatus = null,
    // --- Video ---
    int? ChannelId = null,
    VideoEventKind? VideoKind = null,
    // --- Patentes ---
    string? Plate = null,
    int? Confidence = null,
    // --- Control de acceso ---
    AccessEventKind? AccessKind = null,
    AccessCredentialKind? Credential = null,
    int? DoorNumber = null,
    string? EmployeeNo = null,
    /// <summary>Foto que llegó con el evento (analíticas con captura), disponible para las acciones.</summary>
    byte[]? Image = null)
{
    /// <summary>Etiquetas en español de la naturaleza de un evento de panel.</summary>
    public static string KindLabel(AlarmEventKind kind) => kind switch
    {
        AlarmEventKind.Alarm => "Alarma",
        AlarmEventKind.Restore => "Restauración",
        AlarmEventKind.Arm => "Armado",
        AlarmEventKind.Disarm => "Desarmado",
        AlarmEventKind.Bypass => "Anulación",
        AlarmEventKind.Trouble => "Falla",
        AlarmEventKind.System => "Sistema",
        AlarmEventKind.ZoneTriggered => "Sensor interrumpido",
        _ => "Información",
    };

    public static string SeverityLabel(AlarmSeverity severity) => severity switch
    {
        AlarmSeverity.Critical => "Crítica",
        AlarmSeverity.Warning => "Advertencia",
        _ => "Informativa",
    };

    public static string StatusLabel(AlarmPanelStatus status) => status switch
    {
        AlarmPanelStatus.Online => "En línea",
        AlarmPanelStatus.Offline => "Sin conexión",
        AlarmPanelStatus.AuthFailed => "Credenciales rechazadas",
        _ => "Desconocido",
    };

    /// <summary>Etiqueta del estado de conexión de cualquier equipo ("Online" | "Offline" | "AuthFailed").</summary>
    public static string DeviceStatusLabel(string status) => status switch
    {
        "Online" => "En línea",
        "Offline" => "Sin conexión",
        "AuthFailed" => "Credenciales rechazadas",
        _ => "Desconocido",
    };

    public static string SourceLabel(string? source) => source switch
    {
        "panel" => "informado por el panel",
        "poll" => "detectado por sondeo",
        "vms" => "orden dada desde el VMS",
        _ => source ?? "",
    };

    public static string VideoKindLabel(VideoEventKind kind) => kind switch
    {
        VideoEventKind.Motion => "Detección de movimiento",
        VideoEventKind.LineCrossing => "Cruce de línea",
        VideoEventKind.Intrusion => "Intrusión",
        VideoEventKind.RegionEntrance => "Entrada a región",
        VideoEventKind.RegionExit => "Salida de región",
        VideoEventKind.Loitering => "Merodeo",
        VideoEventKind.ObjectLeftOrTaken => "Objeto abandonado o retirado",
        VideoEventKind.Parking => "Estacionamiento indebido",
        VideoEventKind.FastMoving => "Movimiento rápido",
        VideoEventKind.Crowd => "Aglomeración",
        VideoEventKind.FaceDetection => "Detección de rostro",
        VideoEventKind.PeopleCounting => "Conteo de personas",
        VideoEventKind.VideoLoss => "Pérdida de video",
        VideoEventKind.Tamper => "Cámara tapada / sabotaje",
        VideoEventKind.VideoException => "Anomalía de video",
        VideoEventKind.AudioException => "Anomalía de audio",
        VideoEventKind.AlarmInput => "Entrada de alarma",
        VideoEventKind.DeviceFault => "Falla del equipo",
        _ => "Evento del equipo",
    };

    public static string AccessKindLabel(AccessEventKind kind) => kind switch
    {
        AccessEventKind.Granted => "Acceso concedido",
        AccessEventKind.Denied => "Acceso denegado",
        AccessEventKind.DoorOpen => "Puerta abierta",
        AccessEventKind.DoorClose => "Puerta cerrada",
        AccessEventKind.Alarm => "Alarma de puerta",
        _ => "Otro",
    };

    public static string CredentialLabel(AccessCredentialKind credential) => credential switch
    {
        AccessCredentialKind.Card => "Tarjeta",
        AccessCredentialKind.Fingerprint => "Huella",
        AccessCredentialKind.Face => "Rostro",
        AccessCredentialKind.Pin => "Clave",
        AccessCredentialKind.Remote => "Orden remota",
        AccessCredentialKind.ExitButton => "Botón de salida",
        AccessCredentialKind.Qr => "Código QR",
        AccessCredentialKind.Plate => "Patente",
        _ => "",
    };

    // ------------------------------------------------------------------
    // Paneles de alarma
    // ------------------------------------------------------------------

    /// <summary>Disparo a partir de un evento de panel ya guardado en el historial.</summary>
    public static WorkflowTrigger FromAlarmEvent(AlarmEvent evt)
    {
        // Las horas se muestran SIEMPRE en hora local del servidor: los
        // correos y los reportes los lee gente, no máquinas.
        var local = DateTime.SpecifyKind(evt.ReceivedAt, DateTimeKind.Utc).ToLocalTime();
        var fields = NewFields(local);
        fields["panel"] = evt.PanelName;
        fields["panelid"] = evt.AlarmPanelId.ToString();
        fields["equipo"] = evt.PanelName;
        fields["evento"] = evt.Description;
        fields["tipo"] = KindLabel(evt.Kind);
        fields["severidad"] = SeverityLabel(evt.Severity);
        fields["codigo"] = evt.Code ?? "";
        fields["area"] = evt.AreaName ?? "";
        fields["areanumero"] = evt.AreaNumber?.ToString() ?? "";
        fields["zona"] = evt.ZoneName ?? "";
        fields["zonanumero"] = evt.ZoneNumber?.ToString() ?? "";
        fields["operador"] = evt.Operator ?? "";
        fields["origen"] = SourceLabel(evt.Source);
        fields["estado"] = "";

        var summary = new StringBuilder($"Panel '{evt.PanelName}' · {KindLabel(evt.Kind)}: {evt.Description}");
        if (evt.ZoneName is { Length: > 0 }) summary.Append($" (zona '{evt.ZoneName}')");
        else if (evt.AreaName is { Length: > 0 }) summary.Append($" (área '{evt.AreaName}')");

        return new WorkflowTrigger(WorkflowTriggerTypes.AlarmEvent, Truncate(summary.ToString(), 256), fields, local,
            PanelId: evt.AlarmPanelId, Kind: evt.Kind, Severity: evt.Severity,
            AreaNumber: evt.AreaNumber, ZoneNumber: evt.ZoneNumber, Code: evt.Code,
            Source: evt.Source, Text: evt.Description);
    }

    /// <summary>Disparo por cambio de conexión del servidor con un panel.</summary>
    public static WorkflowTrigger FromPanelStatus(AlarmPanel panel, AlarmPanelStatus status, string? message)
    {
        var local = DateTime.Now;
        string label = StatusLabel(status);
        var fields = NewFields(local);
        fields["panel"] = panel.Name;
        fields["panelid"] = panel.Id.ToString();
        fields["equipo"] = panel.Name;
        fields["evento"] = message is { Length: > 0 } ? $"{label}: {message}" : label;
        fields["tipo"] = "Conexión";
        fields["severidad"] = status == AlarmPanelStatus.Online ? SeverityLabel(AlarmSeverity.Info) : SeverityLabel(AlarmSeverity.Warning);
        fields["codigo"] = "";
        fields["area"] = "";
        fields["areanumero"] = "";
        fields["zona"] = "";
        fields["zonanumero"] = "";
        fields["operador"] = "";
        fields["origen"] = "detectado por el servidor";
        fields["estado"] = label;

        return new WorkflowTrigger(WorkflowTriggerTypes.PanelStatus,
            Truncate($"Panel '{panel.Name}' · {label}" + (message is { Length: > 0 } ? $": {message}" : ""), 256),
            fields, local,
            PanelId: panel.Id,
            Severity: status == AlarmPanelStatus.Online ? AlarmSeverity.Info : AlarmSeverity.Warning,
            Source: "poll", Text: message, PanelStatus: status);
    }

    // ------------------------------------------------------------------
    // Conexión de equipos
    // ------------------------------------------------------------------

    /// <summary>
    /// Disparo por cambio de conexión de un equipo. <paramref name="kind"/>:
    /// "video" (cámara/grabador), "access" (terminal de acceso) o "speaker"
    /// (parlante). <paramref name="status"/>: "Online" | "Offline" | "AuthFailed".
    /// </summary>
    public static WorkflowTrigger FromDeviceStatus(string kind, int id, string name, string host, string? model,
        string status, string? error)
    {
        var local = DateTime.Now;
        string label = DeviceStatusLabel(status);
        string kindLabel = DeviceKindLabel(kind);
        var fields = NewFields(local);
        fields["equipo"] = name;
        fields["equipoid"] = id.ToString();
        fields["equipotipo"] = kindLabel;
        fields["direccion"] = host;
        fields["modelo"] = model ?? "";
        fields["estado"] = label;
        fields["evento"] = error is { Length: > 0 } && status != "Online" ? $"{label}: {error}" : label;
        fields["tipo"] = "Conexión";
        fields["severidad"] = status == "Online" ? SeverityLabel(AlarmSeverity.Info) : SeverityLabel(AlarmSeverity.Warning);
        fields["origen"] = "detectado por el servidor";

        return new WorkflowTrigger(WorkflowTriggerTypes.DeviceStatus,
            Truncate($"{kindLabel} '{name}' · {label}" + (error is { Length: > 0 } && status != "Online" ? $": {error}" : ""), 256),
            fields, local,
            Severity: status == "Online" ? AlarmSeverity.Info : AlarmSeverity.Warning,
            Text: error,
            DeviceKind: kind,
            DeviceId: kind == "video" ? id : null,
            AccessDeviceId: kind == "access" ? id : null,
            SpeakerId: kind == "speaker" ? id : null,
            DeviceStatus: status);
    }

    public static string DeviceKindLabel(string? kind) => kind switch
    {
        "video" => "Cámara/grabador",
        "access" => "Terminal de acceso",
        "speaker" => "Parlante IP",
        _ => "Equipo",
    };

    // ------------------------------------------------------------------
    // Eventos de cámara (analíticas)
    // ------------------------------------------------------------------

    public static WorkflowTrigger FromVideoEvent(Device device, Channel? channel, Core.Drivers.DeviceEvent evt)
    {
        var local = DateTime.Now;
        string kind = VideoKindLabel(evt.Kind);
        string camera = channel?.Name ?? (evt.ChannelNumber > 0 ? $"Canal {evt.ChannelNumber}" : device.Name);
        var fields = NewFields(local);
        fields["equipo"] = device.Name;
        fields["equipoid"] = device.Id.ToString();
        fields["camara"] = camera;
        fields["canal"] = evt.ChannelNumber > 0 ? evt.ChannelNumber.ToString() : "";
        fields["regla"] = evt.RuleName ?? "";
        fields["entrada"] = evt.AlarmInput?.ToString() ?? "";
        fields["evento"] = evt.Description;
        fields["tipo"] = kind;
        fields["severidad"] = SeverityLabel(evt.Kind is VideoEventKind.VideoLoss or VideoEventKind.Tamper or VideoEventKind.DeviceFault
            ? AlarmSeverity.Warning : AlarmSeverity.Critical);
        fields["origen"] = "informado por el equipo";
        fields["horaequipo"] = evt.At.ToString("dd-MM-yyyy HH:mm:ss");

        return new WorkflowTrigger(WorkflowTriggerTypes.VideoEvent,
            Truncate($"Cámara '{camera}' ({device.Name}) · {evt.Description}", 256), fields, local,
            Severity: AlarmSeverity.Critical, Text: evt.Description,
            DeviceKind: "video", DeviceId: device.Id, ChannelId: channel?.Id, VideoKind: evt.Kind, Image: evt.Image);
    }

    // ------------------------------------------------------------------
    // Patentes
    // ------------------------------------------------------------------

    public static WorkflowTrigger FromPlate(PlateEvent evt, string deviceName, string channelName, int? channelId)
    {
        var local = DateTime.Now;
        var fields = NewFields(local);
        fields["patente"] = evt.PlateNumber;
        fields["confianza"] = evt.Confidence >= 0 ? evt.Confidence.ToString() : "";
        fields["equipo"] = deviceName;
        fields["equipoid"] = evt.DeviceId.ToString();
        fields["camara"] = channelName;
        fields["canal"] = evt.ChannelNumber.ToString();
        fields["colorpatente"] = evt.PlateColor ?? "";
        fields["tipovehiculo"] = evt.VehicleType ?? "";
        fields["colorvehiculo"] = evt.VehicleColor ?? "";
        fields["marca"] = evt.VehicleBrand ?? "";
        fields["velocidad"] = evt.SpeedKmh?.ToString() ?? "";
        fields["carril"] = evt.Lane?.ToString() ?? "";
        fields["direccion"] = evt.Direction ?? "";
        fields["infraccion"] = evt.Violation ?? "";
        fields["horaequipo"] = evt.CapturedAt.ToString("dd-MM-yyyy HH:mm:ss");
        fields["evento"] = $"Patente {evt.PlateNumber}" + (evt.Direction is { Length: > 0 } ? $" ({evt.Direction})" : "");
        fields["tipo"] = "Lectura de patente";
        fields["severidad"] = SeverityLabel(AlarmSeverity.Info);
        fields["origen"] = "informado por la cámara";

        return new WorkflowTrigger(WorkflowTriggerTypes.PlateRecognized,
            Truncate($"Patente {evt.PlateNumber} en '{deviceName}' · {channelName}" +
                     (evt.Confidence >= 0 ? $" ({evt.Confidence}%)" : ""), 256),
            fields, local,
            Severity: AlarmSeverity.Info, Text: fields["evento"],
            DeviceKind: "video", DeviceId: evt.DeviceId, ChannelId: channelId,
            Plate: evt.PlateNumber, Confidence: evt.Confidence >= 0 ? evt.Confidence : null);
    }

    // ------------------------------------------------------------------
    // Control de acceso
    // ------------------------------------------------------------------

    public static WorkflowTrigger FromAccessEvent(AccessEvent evt)
    {
        var local = DateTime.SpecifyKind(evt.Timestamp, DateTimeKind.Utc).ToLocalTime();
        string kind = AccessKindLabel(evt.Kind);
        var fields = NewFields(local);
        fields["equipo"] = evt.DeviceName;
        fields["equipoid"] = evt.AccessDeviceId.ToString();
        fields["puerta"] = evt.DoorName ?? (evt.DoorNumber is { } n ? $"Puerta {n}" : "");
        fields["puertanumero"] = evt.DoorNumber?.ToString() ?? "";
        fields["persona"] = evt.PersonName ?? "";
        fields["personaid"] = evt.EmployeeNo ?? "";
        fields["tarjeta"] = evt.CardNumber ?? "";
        fields["credencial"] = CredentialLabel(evt.Credential);
        fields["resultado"] = kind;
        fields["evento"] = evt.Description;
        fields["tipo"] = kind;
        fields["severidad"] = SeverityLabel(evt.Kind switch
        {
            AccessEventKind.Alarm => AlarmSeverity.Critical,
            AccessEventKind.Denied => AlarmSeverity.Warning,
            _ => AlarmSeverity.Info,
        });
        fields["origen"] = "informado por el equipo";

        var summary = new StringBuilder($"{kind} en '{evt.DeviceName}'");
        if (fields["puerta"].Length > 0) summary.Append($" · {fields["puerta"]}");
        if (evt.PersonName is { Length: > 0 }) summary.Append($" · {evt.PersonName}");
        else if (evt.CardNumber is { Length: > 0 }) summary.Append($" · tarjeta {evt.CardNumber}");
        summary.Append($": {evt.Description}");

        return new WorkflowTrigger(WorkflowTriggerTypes.AccessEvent, Truncate(summary.ToString(), 256), fields, local,
            Severity: evt.Kind == AccessEventKind.Alarm ? AlarmSeverity.Critical
                : evt.Kind == AccessEventKind.Denied ? AlarmSeverity.Warning : AlarmSeverity.Info,
            Text: evt.Description,
            DeviceKind: "access", AccessDeviceId: evt.AccessDeviceId,
            AccessKind: evt.Kind, Credential: evt.Credential, DoorNumber: evt.DoorNumber, EmployeeNo: evt.EmployeeNo);
    }

    // ------------------------------------------------------------------
    // Horario y llamada externa
    // ------------------------------------------------------------------

    /// <summary>Tic del reloj del motor (una vez por minuto): cada automatización de horario decide si es su hora.</summary>
    public static WorkflowTrigger FromSchedule(DateTime local)
    {
        var fields = NewFields(local);
        fields["evento"] = $"Hora programada {local:HH:mm}";
        fields["tipo"] = "Horario";
        fields["severidad"] = SeverityLabel(AlarmSeverity.Info);
        fields["origen"] = "reloj del servidor";
        return new WorkflowTrigger(WorkflowTriggerTypes.Schedule, $"Hora programada · {local:dd-MM-yyyy HH:mm}", fields, local,
            Severity: AlarmSeverity.Info, Text: fields["evento"]);
    }

    /// <summary>
    /// Llamada HTTP de otro sistema. Los campos de primer nivel del cuerpo JSON
    /// quedan como marcas ({campo}); el cuerpo completo queda en {cuerpo}.
    /// </summary>
    public static WorkflowTrigger FromWebhook(Workflow workflow, JsonElement? body, string? remoteIp)
    {
        var local = DateTime.Now;
        var fields = NewFields(local);
        string raw = "";
        if (body is { ValueKind: JsonValueKind.Object } obj)
        {
            raw = obj.GetRawText();
            foreach (var property in obj.EnumerateObject())
            {
                string key = property.Name.Trim().ToLowerInvariant();
                if (key.Length == 0 || key.Length > 40) continue;
                fields[key] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => property.Value.GetRawText(),
                };
            }
        }
        else if (body is { } other && other.ValueKind != JsonValueKind.Undefined)
        {
            raw = other.GetRawText();
        }
        fields["cuerpo"] = Truncate(raw, 4000);
        fields["ip"] = remoteIp ?? "";
        if (fields["evento"].Length == 0)
            fields["evento"] = fields.TryGetValue("mensaje", out var m) && m.Length > 0 ? m : $"Llamada externa a '{workflow.Name}'";
        if (fields["tipo"].Length == 0) fields["tipo"] = "Llamada externa";
        if (fields["severidad"].Length == 0) fields["severidad"] = SeverityLabel(AlarmSeverity.Info);
        fields["origen"] = remoteIp is { Length: > 0 } ? $"llamada HTTP desde {remoteIp}" : "llamada HTTP";

        return new WorkflowTrigger(WorkflowTriggerTypes.Webhook,
            Truncate($"Llamada externa · {fields["evento"]}", 256), fields, local,
            Severity: AlarmSeverity.Info, Text: fields["evento"]);
    }

    // ------------------------------------------------------------------
    // Prueba manual
    // ------------------------------------------------------------------

    /// <summary>
    /// Disparo de ejemplo para la prueba manual del editor: ejecuta las
    /// acciones reales (correo, FTP, parlante) con datos inventados, sin
    /// esperar a que el hecho ocurra de verdad.
    /// </summary>
    public static WorkflowTrigger Sample(Workflow workflow, AlarmPanel? panel, Device? device, Channel? channel,
        AccessDevice? accessDevice, AccessDoor? door)
    {
        var local = DateTime.Now;
        string cameraName = channel?.Name ?? "Cámara de prueba";
        string deviceName = device?.Name ?? "Equipo de prueba";
        switch (workflow.TriggerType)
        {
            case WorkflowTriggerTypes.DeviceStatus:
                return FromDeviceStatus("video", device?.Id ?? 0, deviceName, device?.Host ?? "192.168.1.10",
                    device?.Model, "Offline", "prueba manual") with
                    { Summary = $"Prueba manual · {deviceName} · Sin conexión" };

            case WorkflowTriggerTypes.VideoEvent:
            {
                var evt = new Core.Drivers.DeviceEvent(channel?.ChannelNumber ?? 1, VideoEventKind.LineCrossing,
                    "Cruce de línea (prueba manual)", local, RuleName: "Regla de prueba");
                var dev = device ?? new Device { Id = 0, Name = deviceName, Host = "192.168.1.10" };
                return FromVideoEvent(dev, channel, evt) with { Summary = $"Prueba manual · {cameraName} · Cruce de línea" };
            }

            case WorkflowTriggerTypes.PlateRecognized:
            {
                var evt = new PlateEvent
                {
                    DeviceId = device?.Id ?? 0, ChannelNumber = channel?.ChannelNumber ?? 1, PlateNumber = "ABCD12",
                    CapturedAt = local, ReceivedAt = DateTime.UtcNow, Confidence = 95, VehicleType = "Automóvil",
                    VehicleColor = "Blanco", Direction = "Entrada",
                };
                return FromPlate(evt, deviceName, cameraName, channel?.Id) with
                    { Summary = $"Prueba manual · patente ABCD12 en {deviceName}" };
            }

            case WorkflowTriggerTypes.AccessEvent:
            {
                var evt = new AccessEvent
                {
                    AccessDeviceId = accessDevice?.Id ?? 0, DeviceName = accessDevice?.Name ?? "Terminal de prueba",
                    DoorNumber = door?.Number ?? 1, DoorName = door?.Name ?? "Puerta de prueba",
                    Timestamp = DateTime.UtcNow, Kind = AccessEventKind.Granted, Credential = AccessCredentialKind.Card,
                    Description = "Acceso concedido con tarjeta (prueba manual)", EmployeeNo = "1001",
                    PersonName = "Persona de prueba", CardNumber = "12345678",
                };
                return FromAccessEvent(evt) with { Summary = $"Prueba manual · acceso concedido en {evt.DeviceName}" };
            }

            case WorkflowTriggerTypes.Schedule:
                return FromSchedule(local) with { Summary = $"Prueba manual · hora programada {local:HH:mm}" };

            case WorkflowTriggerTypes.Webhook:
            {
                using var doc = JsonDocument.Parse("{\"mensaje\":\"Llamada de prueba\",\"origen\":\"prueba manual\"}");
                return FromWebhook(workflow, doc.RootElement.Clone(), "127.0.0.1") with
                    { Summary = $"Prueba manual · llamada externa a '{workflow.Name}'" };
            }
        }

        bool status = workflow.TriggerType == WorkflowTriggerTypes.PanelStatus;
        var fields = NewFields(local);
        fields["panel"] = panel?.Name ?? "Panel de prueba";
        fields["panelid"] = panel?.Id.ToString() ?? "0";
        fields["equipo"] = fields["panel"];
        fields["evento"] = status ? "Sin conexión: prueba manual" : "Alarma de intrusión (prueba manual)";
        fields["tipo"] = status ? "Conexión" : KindLabel(AlarmEventKind.Alarm);
        fields["severidad"] = SeverityLabel(status ? AlarmSeverity.Warning : AlarmSeverity.Critical);
        fields["codigo"] = status ? "" : "1130";
        fields["area"] = panel?.Areas.FirstOrDefault()?.Name ?? "Área de prueba";
        fields["areanumero"] = panel?.Areas.FirstOrDefault()?.Number.ToString() ?? "1";
        fields["zona"] = status ? "" : panel?.Zones.FirstOrDefault()?.Name ?? "Zona de prueba";
        fields["zonanumero"] = status ? "" : panel?.Zones.FirstOrDefault()?.Number.ToString() ?? "1";
        fields["operador"] = "";
        fields["origen"] = "prueba manual";
        fields["estado"] = status ? StatusLabel(AlarmPanelStatus.Offline) : "";

        return new WorkflowTrigger(workflow.TriggerType,
            $"Prueba manual · {fields["panel"]} · {fields["evento"]}", fields, local,
            PanelId: panel?.Id,
            Kind: status ? null : AlarmEventKind.Alarm,
            Severity: status ? AlarmSeverity.Warning : AlarmSeverity.Critical,
            AreaNumber: panel?.Areas.FirstOrDefault()?.Number,
            ZoneNumber: status ? null : panel?.Zones.FirstOrDefault()?.Number,
            Code: status ? null : "1130",
            Source: "vms", Text: fields["evento"],
            PanelStatus: status ? AlarmPanelStatus.Offline : null);
    }

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------

    /// <summary>Diccionario de marcas con las comunes a todos los disparadores ya puestas.</summary>
    private static Dictionary<string, string> NewFields(DateTime local)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fecha"] = local.ToString("dd-MM-yyyy"),
            ["hora"] = local.ToString("HH:mm:ss"),
            ["fechahora"] = local.ToString("dd-MM-yyyy HH:mm:ss"),
            ["servidor"] = Environment.MachineName,
            ["evento"] = "",
            ["tipo"] = "",
            ["severidad"] = "",
            ["origen"] = "",
            ["equipo"] = "",
        };
        return fields;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Reemplaza las marcas {clave} del texto por los datos del evento. Las
    /// marcas desconocidas se dejan tal cual: es más fácil ver el error en el
    /// correo que perseguir un campo que desapareció sin explicación.
    /// <paramref name="escape"/> transforma cada valor reemplazado (en las URL
    /// se escapa, para que un nombre de zona con espacios no la rompa).
    /// </summary>
    public string Render(string? template, string workflowName, Func<string, string>? escape = null)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var result = new StringBuilder(template.Length + 32);
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') { result.Append(template[i]); continue; }
            int close = template.IndexOf('}', i + 1);
            if (close < 0) { result.Append(template[i..]); break; }
            string key = template[(i + 1)..close];
            if (key.Equals("workflow", StringComparison.OrdinalIgnoreCase)) result.Append(escape?.Invoke(workflowName) ?? workflowName);
            else if (Fields.TryGetValue(key, out var value)) result.Append(escape?.Invoke(value) ?? value);
            else result.Append(template[i..(close + 1)]);
            i = close;
        }
        return result.ToString();
    }
}
