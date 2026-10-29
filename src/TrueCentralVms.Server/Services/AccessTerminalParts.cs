using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Las partes de un terminal de acceso que el VMS ofrece como recursos propios,
/// incluidas con el terminal (no ocupan licencia):
/// <list type="bullet">
/// <item><b>Cámara</b> (faciales): un equipo de video más
/// (<see cref="Device.AccessDeviceId"/>): en vivo, tableros, grabación y cámara
/// principal de sus puertas.</item>
/// <item><b>Altavoz</b>: un parlante más (<see cref="Speaker.AccessDeviceId"/>):
/// voz en vivo, sonidos, grupos de parlantes y automatizaciones.</item>
/// <item><b>Entradas y salidas de alarma</b>: un módulo de E/S
/// (<see cref="IoModule.AccessDeviceId"/>): estado en vivo, «Operar relé» y
/// disparadores por entrada.</item>
/// </list>
/// El terminal manda: dirección, credenciales, nombre, ubicación y si está
/// activo se copian cada vez que se guarda o se revalida (nunca hay una clave
/// vieja en una parte que bloquee al terminal por intentos fallidos). Se borran
/// con él.
/// </summary>
public static class AccessTerminalParts
{
    /// <summary>Driver de video con el que se ve la cámara de cada driver de acceso.</summary>
    private static readonly Dictionary<string, string> VideoDriverOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hikvision-isapi"] = "hikvision-netsdk",
    };

    /// <summary>Driver de parlante con el que suena su altavoz (el de los parlantes IP Hikvision: mismo firmware).</summary>
    private static readonly Dictionary<string, string> SpeakerDriverOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hikvision-isapi"] = "hikvision-isapi",
    };

    /// <summary>Driver de E/S con el que se leen y operan sus entradas y salidas de alarma.</summary>
    private static readonly Dictionary<string, string> IoDriverOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hikvision-isapi"] = HikvisionIoDriver.DriverKey,
    };

    /// <summary>Puerto de gestión del SDK de Hikvision (el de fábrica de los terminales).</summary>
    private const int SdkPort = 8000;

    /// <summary>Lectura de entradas del terminal: no más seguido (atiende de a una petición y lleva el acceso).</summary>
    private const int IoPollMs = 2000;

    /// <summary>¿Sabe el VMS ver las partes de un equipo con este driver?</summary>
    public static bool Supports(string driverKey) => VideoDriverOf.ContainsKey(driverKey);

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

        if (await SyncCameraAsync(db, terminal, reachable, whyNot, notes, ct) is { } camera) changes.Add(camera);
        if (await SyncSpeakerAsync(db, terminal, reachable, whyNot, previousLocationId, notes, services, ct) is { } speaker) changes.Add(speaker);
        if (await SyncIoAsync(db, terminal, reachable, whyNot, previousLocationId, notes, ct) is { } io) changes.Add(io);
        string? note = notes.Count == 0 ? null : string.Join(" ", notes);
        if (note is { Length: > 256 }) note = note[..255] + "…";
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

    private static void Set<T>(T current, T value, Action<T> assign)
    {
        if (!EqualityComparer<T>.Default.Equals(current, value)) assign(value);
    }

    private static void CopyPassword(byte[] current, AccessDevice terminal, Action<byte[]> assign)
    {
        if (!current.AsSpan().SequenceEqual(terminal.PasswordCiphertext)) assign(terminal.PasswordCiphertext.ToArray());
    }

    // ------------------------------------------------------------------
    // Cámara
    // ------------------------------------------------------------------

    private static async Task<string?> SyncCameraAsync(VmsDbContext db, AccessDevice terminal, bool reachable, string? whyNot,
        List<string> notes, CancellationToken ct)
    {
        var camera = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.AccessDeviceId == terminal.Id, ct);
        bool has = terminal.CameraRtspPort is not null;

        if (!has || !terminal.UseCamera || !reachable)
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
            camera = new Device { AccessDeviceId = terminal.Id, DeviceType = DeviceType.Camera, SdkPort = SdkPort };
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
        CopyPassword(camera.PasswordCiphertext, terminal, v => camera.PasswordCiphertext = v);
        Set(camera.LocationId, terminal.LocationId, v => camera.LocationId = v);
        Set(camera.Model, terminal.Model, v => camera.Model = v);
        Set(camera.SerialNumber, terminal.SerialNumber, v => camera.SerialNumber = v);
        Set(camera.FirmwareVersion, terminal.FirmwareVersion, v => camera.FirmwareVersion = v);
    }

    // ------------------------------------------------------------------
    // Altavoz
    // ------------------------------------------------------------------

    private static async Task<string?> SyncSpeakerAsync(VmsDbContext db, AccessDevice terminal, bool reachable, string? whyNot,
        int? previousLocationId, List<string> notes, IServiceProvider services, CancellationToken ct)
    {
        var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.AccessDeviceId == terminal.Id, ct);
        if (!terminal.HasSpeaker || !terminal.UseSpeaker || !reachable)
        {
            if (terminal.HasSpeaker && terminal.UseSpeaker && whyNot is not null) notes.Add($"Su altavoz: {whyNot}.");
            if (speaker is null) return null;
            db.Speakers.Remove(speaker);
            return $"quitó su altavoz \"{speaker.Name}\"";
        }

        if (speaker is null)
        {
            var manual = await db.Speakers.AsNoTracking()
                .FirstOrDefaultAsync(s => s.AccessDeviceId == null && s.IsupDeviceId == null && s.Host == terminal.Host && s.Port == terminal.Port, ct);
            if (manual is not null)
            {
                notes.Add($"Su altavoz ya está en Parlantes IP como \"{manual.Name}\": se usa ese.");
                return null;
            }
            speaker = new Speaker
            {
                AccessDeviceId = terminal.Id,
                DriverKey = SpeakerDriverOf[terminal.DriverKey],
                Name = terminal.Name,
                LocationId = terminal.LocationId,
                Enabled = terminal.Enabled,
                SupportsLiveAudio = true,
            };
            ApplySpeaker(speaker, terminal);
            // Qué sabe hacer (voz en vivo, biblioteca, texto a voz, volumen): el mismo sondeo que un parlante IP.
            var registry = services.GetRequiredService<SpeakerDriverRegistry>();
            var protector = services.GetRequiredService<CredentialProtector>();
            try
            {
                var info = await registry.Find(speaker.DriverKey)!.Create().ProbeAsync(new SpeakerConnectionInfo(
                    terminal.Host, terminal.Port, terminal.UseHttps, terminal.Username, protector.Unprotect(terminal.PasswordCiphertext)), ct);
                Api.SpeakersApi.ApplyProbe(speaker, info);
            }
            catch (DriverException ex)
            {
                speaker.Status = SpeakerStatus.Offline;
                speaker.LastError = ex.Message;
            }
            db.Speakers.Add(speaker);
            return $"agregó su altavoz como parlante \"{terminal.Name}\" (incluido: no ocupa parlante de la licencia)";
        }

        ApplySpeaker(speaker, terminal);
        if (speaker.LocationId == previousLocationId && previousLocationId != terminal.LocationId)
            speaker.LocationId = terminal.LocationId;
        return null;
    }

    private static void ApplySpeaker(Speaker speaker, AccessDevice terminal)
    {
        Set(speaker.Name, terminal.Name, v => speaker.Name = v);
        Set(speaker.Host, terminal.Host, v => speaker.Host = v);
        Set(speaker.Port, terminal.Port, v => speaker.Port = v);
        Set(speaker.UseHttps, terminal.UseHttps, v => speaker.UseHttps = v);
        Set(speaker.Username, terminal.Username, v => speaker.Username = v);
        CopyPassword(speaker.PasswordCiphertext, terminal, v => speaker.PasswordCiphertext = v);
        Set(speaker.Model, terminal.Model, v => speaker.Model = v);
        Set(speaker.SerialNumber, terminal.SerialNumber, v => speaker.SerialNumber = v);
        if (speaker.Enabled != terminal.Enabled) { speaker.Enabled = terminal.Enabled; speaker.DisabledByLicense = false; }
    }

    // ------------------------------------------------------------------
    // Entradas y salidas de alarma
    // ------------------------------------------------------------------

    private static async Task<string?> SyncIoAsync(VmsDbContext db, AccessDevice terminal, bool reachable, string? whyNot,
        int? previousLocationId, List<string> notes, CancellationToken ct)
    {
        var module = await db.IoModules.Include(m => m.Points).FirstOrDefaultAsync(m => m.AccessDeviceId == terminal.Id, ct);
        int inputs = terminal.IoInputs ?? 0, outputs = terminal.IoOutputs ?? 0;
        bool has = inputs + outputs > 0;
        if (!has || !terminal.UseIo || !reachable)
        {
            if (has && terminal.UseIo && whyNot is not null) notes.Add($"Sus entradas y salidas: {whyNot}.");
            if (module is null) return null;
            db.IoModules.Remove(module);
            return $"quitó sus entradas y salidas de alarma \"{module.Name}\"";
        }

        string model = HikvisionIoDriver.ModelKeyFor(inputs, outputs);
        if (module is null)
        {
            if (await db.IoModules.AsNoTracking().FirstOrDefaultAsync(m => m.Host == terminal.Host && m.Port == terminal.Port, ct) is { } manual)
            {
                notes.Add($"Su dirección ya la usa el módulo de E/S \"{manual.Name}\".");
                return null;
            }
            module = new IoModule
            {
                AccessDeviceId = terminal.Id,
                DriverKey = IoDriverOf[terminal.DriverKey],
                Name = terminal.Name,
                PollIntervalMs = IoPollMs,
                LocationId = terminal.LocationId,
                Enabled = terminal.Enabled,
            };
            ApplyIo(module, terminal, model, inputs, outputs);
            db.IoModules.Add(module);
            return $"agregó sus {inputs} entrada(s) y {outputs} salida(s) de alarma como módulo de E/S \"{terminal.Name}\" " +
                   "(incluido: no ocupa módulo de la licencia)";
        }

        ApplyIo(module, terminal, model, inputs, outputs);
        if (module.LocationId == previousLocationId && previousLocationId != terminal.LocationId)
            module.LocationId = terminal.LocationId;
        return null;
    }

    private static void ApplyIo(IoModule module, AccessDevice terminal, string model, int inputs, int outputs)
    {
        Set(module.Name, terminal.Name, v => module.Name = v);
        Set(module.Host, terminal.Host, v => module.Host = v);
        Set(module.Port, terminal.Port, v => module.Port = v);
        Set(module.UseHttps, terminal.UseHttps, v => module.UseHttps = v);
        Set(module.Username, terminal.Username, v => module.Username = v);
        CopyPassword(module.PasswordCiphertext, terminal, v => module.PasswordCiphertext = v);
        Set(module.ModelKey, model, v => module.ModelKey = v);
        Set(module.Model, model, v => module.Model = v);
        Set(module.ReportedModel, model, v => module.ReportedModel = v);
        if (module.Enabled != terminal.Enabled) { module.Enabled = terminal.Enabled; module.DisabledByLicense = false; }

        // Una fila por entrada y salida que declara el terminal. El terminal dice
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

    /// <summary>Nombre de fábrica de una entrada o salida del terminal, como la numera el equipo ("Entrada de alarma 1").</summary>
    public static string DefaultPointName(IoPointKind kind, int number) =>
        $"{(kind == IoPointKind.Input ? "Entrada" : "Salida")} de alarma {number + 1}";
}
