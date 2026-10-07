using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Arma la ficha de un recurso (pestañas General, Equipo y Cámaras asociadas):
/// el recurso en su forma común, su equipo físico, sus características según
/// el tipo y lo que el VMS guarda de él en <see cref="ResourceProfile"/>.
/// </summary>
public static class ResourceDetails
{
    private static string YesNo(bool value) => value ? "Sí" : "No";

    private static string OnlineText<TStatus>(TStatus status, bool enabled = true) where TStatus : struct, Enum =>
        !enabled ? "Pausado" : status.ToString() switch
        {
            "Online" => "En línea",
            "Offline" => "Sin conexión",
            "AuthFailed" => "Credenciales rechazadas",
            _ => "Sin información todavía",
        };

    private static string Address(string host, int port, bool https = false) => $"{(https ? "https://" : "")}{host}:{port}";

    /// <summary>La ficha completa, o null si el recurso ya no existe.</summary>
    public static async Task<ResourceDetailDto?> LoadAsync(VmsDbContext db, ResourceKind kind, int id, CancellationToken ct)
    {
        var resource = await ResourceCatalog.FindAsync(db, kind, id, ct);
        if (resource is null) return null;

        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
        var path = ResourceCatalog.PathNames(resource.LocationId, locations);

        var profile = await ResourceProfile.Of(db.ResourceProfiles.AsNoTracking(), kind, id)
            .Include(p => p.Cameras).ThenInclude(c => c.Channel).ThenInclude(ch => ch.Device)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);

        var part = kind switch
        {
            ResourceKind.Camera => await CameraAsync(db, id, ct),
            ResourceKind.Door => await DoorAsync(db, id, ct),
            ResourceKind.Partition => await PartitionAsync(db, id, ct),
            ResourceKind.Zone => await ZoneAsync(db, id, ct),
            ResourceKind.Fence => await FenceAsync(db, id, ct),
            ResourceKind.Speaker => await SpeakerAsync(db, id, ct),
            ResourceKind.Intercom => await IntercomAsync(db, id, ct),
            _ => null,
        };
        if (part is null) return null;

        // Las fijas (configuradas en el módulo) van primero; después las de la ficha.
        var cameras = new List<ResourceCameraDto>(part.FixedCameras);
        foreach (var c in profile?.Cameras.OrderBy(c => c.Position) ?? Enumerable.Empty<ResourceProfileCamera>())
        {
            if (cameras.Any(x => x.ChannelId == c.ChannelId)) continue;
            cameras.Add(CameraDto(c.Channel, isFixed: false));
        }

        return new ResourceDetailDto(
            resource,
            path.Count > 0 ? string.Join(" › ", path) : null,
            profile?.Description,
            profile?.Instructions,
            part.CanRename,
            part.RenameNote,
            part.Source,
            part.Facts,
            SupportsCameras: kind != ResourceKind.Camera,
            cameras,
            part.SnapshotPath);
    }

    public static ResourceCameraDto CameraDto(Channel channel, bool isFixed) => new(
        channel.Id, channel.Name, channel.DeviceId, channel.Device.Name, channel.ChannelNumber,
        channel.IsOnline && channel.Device.Status == DeviceStatus.Online, isFixed);

    private sealed record Part(
        ResourceSourceDto Source,
        List<ResourceFactDto> Facts,
        bool CanRename,
        string? RenameNote,
        List<ResourceCameraDto> FixedCameras,
        string? SnapshotPath = null);

    private static void Add(List<ResourceFactDto> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) facts.Add(new ResourceFactDto(label, value));
    }

    private static async Task<Part?> CameraAsync(VmsDbContext db, int id, CancellationToken ct)
    {
        var c = await db.Channels.AsNoTracking().Include(x => x.Device).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        var d = c.Device;
        var facts = new List<ResourceFactDto>();
        Add(facts, "Tipo de equipo", d.DeviceType switch
        {
            DeviceType.Camera => "Cámara IP",
            DeviceType.Dvr => "Grabador DVR",
            DeviceType.Nvr => "Grabador NVR",
            DeviceType.Xvr => "Grabador XVR",
            _ => d.DeviceType.ToString(),
        });
        Add(facts, "Canal en el equipo", c.RtspChannel != c.ChannelNumber
            ? $"{c.ChannelNumber} (RTSP {c.RtspChannel})" : c.ChannelNumber.ToString());
        Add(facts, "Señal", c.IsOnline && d.Status == DeviceStatus.Online ? "Con señal" : "Sin señal");
        Add(facts, "Visible para los operadores",
            c.Enabled ? "Sí" : c.DisabledByLicense ? "No (esperando cupo de licencia)" : "No");
        Add(facts, "Control PTZ", YesNo(c.SupportsPtz));
        Add(facts, "Relé FFmpeg", c.UseFfmpegProxy ? "Sí (el video se ve sin audio)" : "No");
        if (d.AnprEnabled) Add(facts, "Patentes (ANPR)", "El equipo es fuente de patentes");
        return new Part(
            new ResourceSourceDto("devices", d.Id, d.Name, d.Model, d.SerialNumber, d.FirmwareVersion,
                Address(d.Host, d.SdkPort), OnlineText(d.Status), d.LastSeenAt),
            facts, CanRename: true, RenameNote: null, FixedCameras: [],
            SnapshotPath: $"/api/devices/{d.Id}/snapshot/{c.ChannelNumber}");
    }

    private static async Task<Part?> DoorAsync(VmsDbContext db, int id, CancellationToken ct)
    {
        var door = await db.AccessDoors.AsNoTracking().Include(x => x.AccessDevice).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (door?.AccessDevice is not { } d) return null;
        var facts = new List<ResourceFactDto>();
        Add(facts, "Tipo de equipo", d.Kind switch
        {
            AccessDeviceKind.Terminal => "Terminal de acceso",
            AccessDeviceKind.Controller => "Controladora de puertas",
            AccessDeviceKind.Turnstile => "Torniquete o barrera",
            _ => "Equipo de control de acceso",
        });
        Add(facts, "Puerta en el equipo", door.Number.ToString());
        Add(facts, "Habilitada", YesNo(door.Enabled));
        Add(facts, "Modo", door.Mode switch
        {
            AccessDoorMode.Normal => "Normal (abre con credencial)",
            AccessDoorMode.RemainOpen => "Siempre abierta",
            AccessDoorMode.RemainLocked => "Siempre cerrada",
            _ => "Sin información todavía",
        });
        Add(facts, "Hoja", door.IsOpen switch { true => "Abierta", false => "Cerrada", _ => "El equipo no lo informa" });
        Add(facts, "Cerradura", door.IsLocked switch { true => "Trabada", false => "Destrabada", _ => "El equipo no lo informa" });
        var credentials = new List<string>();
        if (d.SupportsCards) credentials.Add("tarjeta");
        if (d.SupportsFingerprint) credentials.Add("huella");
        if (d.SupportsFace) credentials.Add("rostro");
        Add(facts, "Credenciales del equipo", credentials.Count > 0 ? string.Join(", ", credentials) : null);
        Add(facts, "Origen del nombre", door.NameFromDevice ? "Lo informa el equipo" : "Puesto en el VMS");
        return new Part(
            new ResourceSourceDto("access", d.Id, d.Name, d.Model, d.SerialNumber, d.FirmwareVersion,
                Address(d.Host, d.Port, d.UseHttps), OnlineText(d.Status, d.Enabled), d.LastSeenAt),
            facts, CanRename: true, RenameNote: null, FixedCameras: []);
    }

    private static string ArmText(AlarmArmState state) => state switch
    {
        AlarmArmState.Disarmed => "Desarmada",
        AlarmArmState.Away => "Armada (total)",
        AlarmArmState.Stay => "Armada (parcial)",
        AlarmArmState.Vacation => "Armada (vacaciones)",
        AlarmArmState.Arming => "Armando…",
        _ => "Sin información todavía",
    };

    private static ResourceSourceDto PanelSource(AlarmPanel p) =>
        new("alarm-panels", p.Id, p.Name, p.Model, p.SerialNumber, p.FirmwareVersion,
            Address(p.Host, p.Port, p.UseHttps) + (p.GatewayDeviceId is { } g ? $" · equipo {g} en la receptora" : ""),
            OnlineText(p.Status, p.Enabled), p.LastSeenAt);

    private static async Task<Part?> PartitionAsync(VmsDbContext db, int id, CancellationToken ct)
    {
        var area = await db.AlarmAreas.AsNoTracking().Include(x => x.AlarmPanel).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (area is null) return null;
        int zones = await db.AlarmZones.CountAsync(z => z.AlarmPanelId == area.AlarmPanelId && z.AreaNumber == area.Number, ct);
        var facts = new List<ResourceFactDto>();
        Add(facts, "Área en el panel", area.Number.ToString());
        Add(facts, "Habilitada", YesNo(area.Enabled));
        Add(facts, "Estado", ArmText(area.ArmState));
        Add(facts, "En alarma", YesNo(area.InAlarm));
        Add(facts, "Zonas del área", zones.ToString());
        return new Part(PanelSource(area.AlarmPanel), facts, CanRename: false,
            RenameNote: "El nombre del área lo define el panel: se cambia en el propio panel.", FixedCameras: []);
    }

    private static async Task<Part?> ZoneAsync(VmsDbContext db, int id, CancellationToken ct)
    {
        var zone = await db.AlarmZones.AsNoTracking().Include(x => x.AlarmPanel).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (zone is null) return null;
        string? areaName = zone.AreaNumber is { } n
            ? await db.AlarmAreas.Where(a => a.AlarmPanelId == zone.AlarmPanelId && a.Number == n)
                .Select(a => a.Name).FirstOrDefaultAsync(ct)
            : null;
        var facts = new List<ResourceFactDto>();
        Add(facts, "Zona en el panel", zone.Number.ToString());
        Add(facts, "Área", zone.AreaNumber is { } an ? $"{an}{(areaName is null ? "" : $" · {areaName}")}" : "Sin área");
        Add(facts, "Tipo de zona", zone.ZoneType);
        Add(facts, "Detector", zone.DetectorType);
        Add(facts, "Modelo", zone.Model);
        Add(facts, "Estado", zone.Status switch
        {
            AlarmZoneStatus.Normal => "Normal",
            AlarmZoneStatus.Triggered => "Activada",
            AlarmZoneStatus.Fault => "Falla",
            AlarmZoneStatus.Offline => "Sin comunicación",
            AlarmZoneStatus.NotConfigured => "Sin configurar en el panel",
            _ => "Sin información todavía",
        });
        Add(facts, "Anulada", YesNo(zone.Bypassed));
        Add(facts, "Sabotaje", YesNo(zone.Tamper));
        Add(facts, "Batería baja", YesNo(zone.LowBattery));
        if (zone.Signal is { } signal) Add(facts, "Señal inalámbrica", signal.ToString());
        return new Part(PanelSource(zone.AlarmPanel), facts, CanRename: false,
            RenameNote: "El nombre de la zona lo define el panel: se cambia en el propio panel.", FixedCameras: []);
    }

    private static async Task<Part?> FenceAsync(VmsDbContext db, int id, CancellationToken ct)
    {
        var p = await db.CercoPanels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var facts = new List<ResourceFactDto>();
        Add(facts, "ID del panel", p.DeviceId);
        Add(facts, "Enrolado", YesNo(p.Enrolled));
        Add(facts, "Armado", YesNo(p.Armed));
        Add(facts, "Sirena", p.Siren ? "Sonando" : "Apagada");
        Add(facts, "Alta tensión", p.HvOk ? "Normal" : "Falla");
        Add(facts, "Línea del cerco", p.FenceOk ? "Normal" : "Cortada o a tierra");
        Add(facts, "Falla de arco", YesNo(p.ArcFault));
        Add(facts, "Nivel de voltaje configurado", $"{p.HvLevel} / 21");
        Add(facts, "Energía", p.PowerSource switch
        {
            CercoPowerSource.Mains => "Red eléctrica",
            CercoPowerSource.Battery => "Batería",
            _ => null,
        });
        if (p.Rssi is { } rssi) Add(facts, "Señal WiFi", $"{rssi} dBm");
        return new Part(
            new ResourceSourceDto("cerco-panels", p.Id, p.Name, p.Model, null, p.Firmware, p.Mac,
                !p.Enabled ? "Pausado" : p.Status switch
                {
                    CercoPanelStatus.Online => "En línea",
                    CercoPanelStatus.Offline => "Sin conexión",
                    _ => "Sin información todavía",
                }, p.LastSeenAt),
            facts, CanRename: false,
            RenameNote: "El nombre del panel de cerco se cambia en Dispositivos → Paneles de cerco.", FixedCameras: []);
    }

    private static async Task<Part?> SpeakerAsync(VmsDbContext db, int id, CancellationToken ct)
    {
        var s = await db.Speakers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;
        var facts = new List<ResourceFactDto>();
        Add(facts, "Volumen", s.Volume?.ToString() ?? "Sin información todavía");
        Add(facts, "Voz en vivo", YesNo(s.SupportsLiveAudio));
        Add(facts, "Biblioteca de audios propia", YesNo(s.SupportsLibrary));
        Add(facts, "Texto a voz", YesNo(s.SupportsTts));
        Add(facts, "Grupo", s.GroupName);
        return new Part(
            new ResourceSourceDto("speakers", s.Id, s.Name, s.Model, s.SerialNumber, s.FirmwareVersion,
                Address(s.Host, s.Port, s.UseHttps), OnlineText(s.Status, s.Enabled), s.LastSeenAt),
            facts, CanRename: true, RenameNote: null, FixedCameras: []);
    }

    private static async Task<Part?> IntercomAsync(VmsDbContext db, int id, CancellationToken ct)
    {
        var i = await db.Intercoms.AsNoTracking()
            .Include(x => x.Channel).ThenInclude(c => c!.Device)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return null;
        var facts = new List<ResourceFactDto>();
        Add(facts, "Puertas que abre", i.DoorCount.ToString());
        Add(facts, "Llamadas a la central", i.CallCenterEnabled ? "Activadas" : "Desactivadas");
        Add(facts, "Cámara del frente", i.Channel?.Name ?? "Sin cámara");
        Add(facts, "Grupo", i.GroupName);
        // La cámara del frente se configura en Citofonía: en la ficha aparece fija.
        List<ResourceCameraDto> fixedCameras = i.Channel is { } channel ? [CameraDto(channel, isFixed: true)] : [];
        return new Part(
            new ResourceSourceDto("intercoms", i.Id, i.Name, i.Model, i.SerialNumber, i.FirmwareVersion,
                Address(i.Host, i.Port), OnlineText(i.Status, i.Enabled), i.LastSeenAt),
            facts, CanRename: true, RenameNote: null, FixedCameras: fixedCameras);
    }
}
