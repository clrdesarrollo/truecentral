using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Las partes de un equipo que el VMS ofrece como recursos propios, incluidas
/// con el equipo (no ocupan licencia):
/// <list type="bullet">
/// <item><b>Cámara</b> de un terminal de acceso facial: un equipo de video más
/// (<see cref="Device.AccessDeviceId"/>): en vivo, tableros, grabación y cámara
/// principal de sus puertas.</item>
/// <item><b>Altavoz</b> de un terminal, una cámara o un grabador: un parlante más
/// (<see cref="Speaker.AccessDeviceId"/>, <see cref="Speaker.DeviceId"/>): voz en
/// vivo, sonidos, grupos de parlantes y automatizaciones.</item>
/// <item><b>Entradas y salidas de alarma</b> de cualquiera de ellos: un módulo de
/// E/S (<see cref="IoModule.AccessDeviceId"/>, <see cref="IoModule.DeviceId"/>):
/// estado en vivo, «Operar relé» y disparadores por entrada.</item>
/// </list>
/// El equipo manda: dirección, credenciales, nombre, ubicación y si está activo
/// se copian cada vez que se guarda o se revalida (nunca hay una clave vieja en
/// una parte que lo bloquee por intentos fallidos). Se borran con él.
/// </summary>
public static class EquipmentParts
{
    /// <summary>Driver de video con el que se ve la cámara de cada driver de acceso.</summary>
    private static readonly Dictionary<string, string> VideoDriverOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hikvision-isapi"] = "hikvision-netsdk",
    };

    /// <summary>
    /// Drivers (de acceso o de video) cuyas partes sabe ver el VMS, con el driver
    /// de parlante y el de E/S con que se usan: todos por ISAPI.
    /// </summary>
    private static readonly Dictionary<string, (string Speaker, string Io)> PartDriversOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hikvision-isapi"] = ("hikvision-isapi", HikvisionIoDriver.DriverKey),
        ["hikvision-netsdk"] = ("hikvision-isapi", HikvisionIoDriver.DriverKey),
    };

    /// <summary>Puerto de gestión del SDK de Hikvision (el de fábrica de los terminales).</summary>
    private const int SdkPort = 8000;

    /// <summary>Lectura de entradas del equipo: no más seguido (atiende de a una petición).</summary>
    private const int IoPollMs = 2000;

    /// <summary>¿Sabe el VMS ver las partes de un equipo con este driver?</summary>
    public static bool Supports(string driverKey) => PartDriversOf.ContainsKey(driverKey);

    /// <summary>¿Corresponde mirar las partes de este equipo de video? (no los móviles, ni la cámara de un terminal, ni ISUP).</summary>
    public static bool Applies(Device device) =>
        Supports(device.DriverKey) && !device.IsMobile && device.AccessDeviceId is null && device.IsupDeviceId is null;

    /// <summary>
    /// De quién son el altavoz y el módulo de E/S y con qué datos se crean:
    /// un terminal de acceso o un equipo de video.
    /// </summary>
    private sealed record Owner(int? AccessDeviceId, int? DeviceId, string Name, string Host, int Port, bool UseHttps,
        string Username, byte[] PasswordCiphertext, int? LocationId, bool Enabled, string? Model, string? SerialNumber,
        bool Reachable, string? WhyNot, string DriverKey);

    // ------------------------------------------------------------------
    // Terminales de acceso
    // ------------------------------------------------------------------

    /// <summary>
    /// Deja las partes del terminal como deben estar: las crea, las pone al día
    /// o las quita. Devuelve las altas y bajas para la bitácora («agregó su
    /// altavoz…»); la puesta al día no se anuncia (va con el cambio del
    /// terminal). No guarda: el llamador hace SaveChanges y, si hubo cambios,
    /// refresca MediaMTX y avisa a los servicios de parlantes y de E/S.
    /// <paramref name="previousLocationId"/>: dónde estaba el terminal antes de
    /// este cambio (el altavoz y el módulo que seguían ahí se mueven con él).
    /// El terminal tiene que tener Id (ya guardado).
    /// </summary>
    public static async Task<List<string>> SyncAsync(VmsDbContext db, AccessDevice terminal, int? previousLocationId,
        IServiceProvider services, CancellationToken ct)
    {
        var changes = new List<string>();
        var notes = new List<string>();
        bool reachable = terminal.IsupDeviceId is null && Supports(terminal.DriverKey);
        string? whyNot = terminal.IsupDeviceId is not null ? "por ISUP todavía no se puede usar"
            : !Supports(terminal.DriverKey) ? "su marca todavía no lo permite" : null;
        var owner = new Owner(terminal.Id, null, terminal.Name, terminal.Host, terminal.Port, terminal.UseHttps, terminal.Username,
            terminal.PasswordCiphertext, terminal.LocationId, terminal.Enabled, terminal.Model, terminal.SerialNumber,
            reachable, whyNot, terminal.DriverKey);

        if (await SyncCameraAsync(db, terminal, reachable, whyNot, notes, ct) is { } camera) changes.Add(camera);
        if (await SyncSpeakerAsync(db, owner, terminal.HasSpeaker && terminal.UseSpeaker,
                previousLocationId, notes, services, ct) is { } speaker) changes.Add(speaker);
        if (await SyncIoAsync(db, owner, terminal.IoInputs ?? 0, terminal.IoOutputs ?? 0, terminal.UseIo,
                previousLocationId, notes, ct) is { } io) changes.Add(io);
        string? note = Note(notes);
        if (terminal.PartsNote != note) terminal.PartsNote = note;
        return changes;
    }

    /// <summary>Sus partes se apagan y vuelven con el terminal (la cámara deja de servirse en MediaMTX).</summary>
    public static async Task FollowEnabledAsync(VmsDbContext db, AccessDevice terminal, CancellationToken ct)
    {
        foreach (var speaker in await db.Speakers.Where(s => s.AccessDeviceId == terminal.Id).ToListAsync(ct))
            if (speaker.Enabled != terminal.Enabled) { speaker.Enabled = terminal.Enabled; speaker.DisabledByLicense = false; }
        foreach (var module in await db.IoModules.Where(m => m.AccessDeviceId == terminal.Id).ToListAsync(ct))
            if (module.Enabled != terminal.Enabled) { module.Enabled = terminal.Enabled; module.DisabledByLicense = false; }
    }

    // ------------------------------------------------------------------
    // Cámaras y grabadores
    // ------------------------------------------------------------------

    /// <summary>
    /// Lo que la cámara o el grabador dijo de sus partes. Una lectura sin una
    /// parte no quita una ya conocida (un error pasajero del equipo se llevaría
    /// sus grupos y automatizaciones), salvo que sea otro equipo (cambió el modelo).
    /// </summary>
    public static void ApplyParts(Device device, string? model, DeviceParts? parts)
    {
        if (parts is null) return;
        bool sameDevice = device.PartsChecked && string.Equals(device.Model, model, StringComparison.OrdinalIgnoreCase);
        if (parts.HasSpeaker || !sameDevice) device.HasSpeaker = parts.HasSpeaker;
        if (parts.Io is not null || !sameDevice)
        {
            device.IoInputs = parts.Io?.Inputs;
            device.IoOutputs = parts.Io?.Outputs;
        }
        device.PartsChecked = true;
    }

    /// <summary>Pregunta al equipo de video qué partes tiene. Null si no corresponde o no contestó.</summary>
    public static async Task<DeviceParts?> ReadAsync(Device device, IDeviceDriver driver, CredentialProtector protector, CancellationToken ct)
    {
        if (!Applies(device)) return null;
        try { return await driver.ReadPartsAsync(DeviceConnections.Of(device, protector), device.HttpPort, ct); }
        catch (DriverException) { return null; }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>
    /// Igual que <see cref="SyncAsync(VmsDbContext, AccessDevice, int?, IServiceProvider, CancellationToken)"/>
    /// para una cámara o un grabador: su altavoz y sus entradas y salidas.
    /// </summary>
    public static async Task<List<string>> SyncAsync(VmsDbContext db, Device device, int? previousLocationId,
        IServiceProvider services, CancellationToken ct)
    {
        var changes = new List<string>();
        var notes = new List<string>();
        bool applies = Applies(device);
        // Las partes van donde está el equipo; un equipo sin ubicación, donde esté su cámara (una cámara IP tiene una).
        int? location = device.LocationId ?? device.Channels.OrderBy(c => c.ChannelNumber).FirstOrDefault()?.LocationId;
        var owner = new Owner(null, device.Id, device.Name, device.Host, device.HttpPort, false, device.Username,
            device.PasswordCiphertext, location, true, device.Model, device.SerialNumber,
            applies, applies ? null : "este equipo no lo permite", device.DriverKey);
        if (await SyncSpeakerAsync(db, owner, applies && device.HasSpeaker && device.UseSpeaker,
                previousLocationId, notes, services, ct) is { } speaker) changes.Add(speaker);
        if (await SyncIoAsync(db, owner, applies ? device.IoInputs ?? 0 : 0, applies ? device.IoOutputs ?? 0 : 0, device.UseIo,
                previousLocationId, notes, ct) is { } io) changes.Add(io);
        string? note = Note(notes);
        if (device.PartsNote != note) device.PartsNote = note;
        return changes;
    }

    // ------------------------------------------------------------------
    // Comunes
    // ------------------------------------------------------------------

    private static string? Note(List<string> notes)
    {
        string? note = notes.Count == 0 ? null : string.Join(" ", notes);
        return note is { Length: > 256 } ? note[..255] + "…" : note;
    }

    private static void Set<T>(T current, T value, Action<T> assign)
    {
        if (!EqualityComparer<T>.Default.Equals(current, value)) assign(value);
    }

    private static void CopyPassword(byte[] current, byte[] source, Action<byte[]> assign)
    {
        if (!current.AsSpan().SequenceEqual(source)) assign(source.ToArray());
    }

    // ------------------------------------------------------------------
    // Cámara (solo terminales)
    // ------------------------------------------------------------------

    private static async Task<string?> SyncCameraAsync(VmsDbContext db, AccessDevice terminal, bool reachable, string? whyNot,
        List<string> notes, CancellationToken ct)
    {
        var camera = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.AccessDeviceId == terminal.Id, ct);
        bool has = terminal.CameraRtspPort is not null;

        if (!has || !terminal.UseCamera || !reachable || !VideoDriverOf.ContainsKey(terminal.DriverKey))
        {
            if (has && terminal.UseCamera && whyNot is not null) notes.Add($"Su cámara: {whyNot}.");
            if (camera is null) return null;
            db.Devices.Remove(camera);
            return $"quitó su cámara \"{camera.Name}\"";
        }

        if (camera is null)
        {
            // Si alguien ya la agregó a mano en Fuentes de video, se usa esa (no se duplica el video).
            // Por puerto, no solo por dirección: tras un NAT varios equipos comparten la IP pública.
            int rtspPort = terminal.CameraRtspPort!.Value;
            var manual = await db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.AccessDeviceId == null && d.IsupDeviceId == null && d.Host == terminal.Host &&
                                          (d.SdkPort == SdkPort || d.RtspPort == rtspPort), ct);
            if (manual is not null)
            {
                notes.Add($"Su cámara ya está en Fuentes de video como \"{manual.Name}\": se usa esa (asóciela a la puerta en su ficha de Recursos).");
                return null;
            }
            // Sus partes son las del terminal: la cámara acompañante no las vuelve a ofrecer.
            camera = new Device { AccessDeviceId = terminal.Id, DeviceType = DeviceType.Camera, SdkPort = SdkPort, PartsChecked = true };
            ApplyCamera(camera, terminal);
            camera.Channels.Add(new Channel
            {
                ChannelNumber = 1,
                RtspChannel = 1,
                Name = terminal.Name,
                Enabled = true,
                IsOnline = true,
                LocationId = terminal.LocationId,
            });
            db.Devices.Add(camera);
            return $"agregó su cámara como \"{terminal.Name}\" (incluida: no ocupa canal de video de la licencia)";
        }

        // Al día con el terminal. El canal conserva el nombre que le hayan
        // puesto; si seguía con el del terminal, lo sigue.
        if (camera.Name != terminal.Name)
            foreach (var channel in camera.Channels.Where(c => c.Name == camera.Name))
                channel.Name = terminal.Name;
        if (camera.LocationId != terminal.LocationId)
            EquipmentLocation.Follow(camera.Channels, camera.LocationId, terminal.LocationId);
        ApplyCamera(camera, terminal);
        return null;
    }

    private static void ApplyCamera(Device camera, AccessDevice terminal)
    {
        Set(camera.Name, terminal.Name, v => camera.Name = v);
        Set(camera.DriverKey, VideoDriverOf[terminal.DriverKey], v => camera.DriverKey = v);
        Set(camera.Host, terminal.Host, v => camera.Host = v);
        Set(camera.RtspPort, terminal.CameraRtspPort ?? 554, v => camera.RtspPort = v);
        Set(camera.Username, terminal.Username, v => camera.Username = v);
        CopyPassword(camera.PasswordCiphertext, terminal.PasswordCiphertext, v => camera.PasswordCiphertext = v);
        Set(camera.LocationId, terminal.LocationId, v => camera.LocationId = v);
        Set(camera.Model, terminal.Model, v => camera.Model = v);
        Set(camera.SerialNumber, terminal.SerialNumber, v => camera.SerialNumber = v);
        Set(camera.FirmwareVersion, terminal.FirmwareVersion, v => camera.FirmwareVersion = v);
    }

    // ------------------------------------------------------------------
    // Altavoz
    // ------------------------------------------------------------------

    private static async Task<string?> SyncSpeakerAsync(VmsDbContext db, Owner owner, bool want,
        int? previousLocationId, List<string> notes, IServiceProvider services, CancellationToken ct)
    {
        int? aid = owner.AccessDeviceId, did = owner.DeviceId;
        var speaker = await db.Speakers.FirstOrDefaultAsync(s => (aid != null && s.AccessDeviceId == aid) || (did != null && s.DeviceId == did), ct);
        if (!want || !owner.Reachable)
        {
            if (want && owner.WhyNot is not null) notes.Add($"Su altavoz: {owner.WhyNot}.");
            if (speaker is null) return null;
            db.Speakers.Remove(speaker);
            return $"quitó su altavoz \"{speaker.Name}\"";
        }

        if (speaker is null)
        {
            var manual = await db.Speakers.AsNoTracking()
                .FirstOrDefaultAsync(s => s.AccessDeviceId == null && s.DeviceId == null && s.IsupDeviceId == null &&
                                          s.Host == owner.Host && s.Port == owner.Port, ct);
            if (manual is not null)
            {
                notes.Add($"Su altavoz ya está en Parlantes IP como \"{manual.Name}\": se usa ese.");
                return null;
            }
            speaker = new Speaker
            {
                AccessDeviceId = aid,
                DeviceId = did,
                DriverKey = PartDriversOf[owner.DriverKey].Speaker,
                Name = owner.Name,
                LocationId = owner.LocationId,
                Enabled = owner.Enabled,
                SupportsLiveAudio = true,
            };
            ApplySpeaker(speaker, owner);
            // Qué sabe hacer (voz en vivo, biblioteca, texto a voz, volumen): el mismo sondeo que un parlante IP.
            var registry = services.GetRequiredService<SpeakerDriverRegistry>();
            var protector = services.GetRequiredService<CredentialProtector>();
            try
            {
                var info = await registry.Find(speaker.DriverKey)!.Create().ProbeAsync(new SpeakerConnectionInfo(
                    owner.Host, owner.Port, owner.UseHttps, owner.Username, protector.Unprotect(owner.PasswordCiphertext)), ct);
                Api.SpeakersApi.ApplyProbe(speaker, info);
                // El equipo ya mostró su canal de audio bidireccional (aunque no conteste sus capacidades).
                speaker.SupportsLiveAudio = true;
            }
            catch (DriverException ex)
            {
                speaker.Status = SpeakerStatus.Offline;
                speaker.LastError = ex.Message;
            }
            db.Speakers.Add(speaker);
            return $"agregó su altavoz como parlante \"{owner.Name}\" (incluido: no ocupa parlante de la licencia)";
        }

        ApplySpeaker(speaker, owner);
        if (speaker.LocationId == previousLocationId && previousLocationId != owner.LocationId)
            speaker.LocationId = owner.LocationId;
        return null;
    }

    private static void ApplySpeaker(Speaker speaker, Owner owner)
    {
        Set(speaker.Name, owner.Name, v => speaker.Name = v);
        Set(speaker.Host, owner.Host, v => speaker.Host = v);
        Set(speaker.Port, owner.Port, v => speaker.Port = v);
        Set(speaker.UseHttps, owner.UseHttps, v => speaker.UseHttps = v);
        Set(speaker.Username, owner.Username, v => speaker.Username = v);
        CopyPassword(speaker.PasswordCiphertext, owner.PasswordCiphertext, v => speaker.PasswordCiphertext = v);
        Set(speaker.Model, owner.Model, v => speaker.Model = v);
        Set(speaker.SerialNumber, owner.SerialNumber, v => speaker.SerialNumber = v);
        if (speaker.Enabled != owner.Enabled) { speaker.Enabled = owner.Enabled; speaker.DisabledByLicense = false; }
    }

    // ------------------------------------------------------------------
    // Entradas y salidas de alarma
    // ------------------------------------------------------------------

    private static async Task<string?> SyncIoAsync(VmsDbContext db, Owner owner, int inputs, int outputs, bool use,
        int? previousLocationId, List<string> notes, CancellationToken ct)
    {
        int? aid = owner.AccessDeviceId, did = owner.DeviceId;
        var module = await db.IoModules.Include(m => m.Points)
            .FirstOrDefaultAsync(m => (aid != null && m.AccessDeviceId == aid) || (did != null && m.DeviceId == did), ct);
        bool has = inputs + outputs > 0;
        if (!has || !use || !owner.Reachable)
        {
            if (has && use && owner.WhyNot is not null) notes.Add($"Sus entradas y salidas: {owner.WhyNot}.");
            if (module is null) return null;
            db.IoModules.Remove(module);
            return $"quitó sus entradas y salidas de alarma \"{module.Name}\"";
        }

        string model = HikvisionIoDriver.ModelKeyFor(inputs, outputs);
        if (module is null)
        {
            if (await db.IoModules.AsNoTracking().FirstOrDefaultAsync(m => m.Host == owner.Host && m.Port == owner.Port, ct) is { } manual)
            {
                notes.Add($"Su dirección ya la usa el módulo de E/S \"{manual.Name}\".");
                return null;
            }
            module = new IoModule
            {
                AccessDeviceId = aid,
                DeviceId = did,
                DriverKey = PartDriversOf[owner.DriverKey].Io,
                Name = owner.Name,
                PollIntervalMs = IoPollMs,
                LocationId = owner.LocationId,
                Enabled = owner.Enabled,
            };
            ApplyIo(module, owner, model, inputs, outputs);
            db.IoModules.Add(module);
            return $"agregó sus {inputs} entrada(s) y {outputs} salida(s) de alarma como módulo de E/S \"{owner.Name}\" " +
                   "(incluido: no ocupa módulo de la licencia)";
        }

        ApplyIo(module, owner, model, inputs, outputs);
        if (module.LocationId == previousLocationId && previousLocationId != owner.LocationId)
            module.LocationId = owner.LocationId;
        return null;
    }

    private static void ApplyIo(IoModule module, Owner owner, string model, int inputs, int outputs)
    {
        Set(module.Name, owner.Name, v => module.Name = v);
        Set(module.Host, owner.Host, v => module.Host = v);
        Set(module.Port, owner.Port, v => module.Port = v);
        Set(module.UseHttps, owner.UseHttps, v => module.UseHttps = v);
        Set(module.Username, owner.Username, v => module.Username = v);
        CopyPassword(module.PasswordCiphertext, owner.PasswordCiphertext, v => module.PasswordCiphertext = v);
        Set(module.ModelKey, model, v => module.ModelKey = v);
        Set(module.Model, model, v => module.Model = v);
        Set(module.ReportedModel, model, v => module.ReportedModel = v);
        if (module.Enabled != owner.Enabled) { module.Enabled = owner.Enabled; module.DisabledByLicense = false; }

        // Una fila por entrada y salida que declara el equipo. El equipo dice
        // «activa» o «en reposo»: no hay lógica que invertir.
        module.Points.RemoveAll(p => p.Number >= (p.Kind == IoPointKind.Input ? inputs : outputs));
        void Ensure(IoPointKind kind, int count)
        {
            for (int n = 0; n < count; n++)
                if (!module.Points.Any(p => p.Kind == kind && p.Number == n))
                    module.Points.Add(new IoPoint { Kind = kind, Number = n, Name = DefaultPointName(kind, n), Inverted = false });
        }
        Ensure(IoPointKind.Input, inputs);
        Ensure(IoPointKind.Output, outputs);
    }

    /// <summary>Nombre de fábrica de una entrada o salida del equipo, como la numera él ("Entrada de alarma 1").</summary>
    public static string DefaultPointName(IoPointKind kind, int number) =>
        $"{(kind == IoPointKind.Input ? "Entrada" : "Salida")} de alarma {number + 1}";
}
