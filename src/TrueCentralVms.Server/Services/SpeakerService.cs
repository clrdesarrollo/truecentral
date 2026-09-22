using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Módulo Parlantes IP. Tres responsabilidades:
/// <list type="bullet">
/// <item><b>Sondeo</b>: cada minuto lee el volumen de cada parlante habilitado
/// (una sola llamada) para saber si sigue en línea; los cambios de estado se
/// auditan y se empujan por el hub.</item>
/// <item><b>Reproducción</b>: sonidos del servidor (se transmiten por el canal
/// en vivo, en lock-step a todos los parlantes elegidos para que suenen
/// sincronizados), audios de la biblioteca de cada equipo (por nombre, para
/// que la misma orden valga en parlantes distintos) y texto a voz.</item>
/// <item><b>Voz en vivo</b>: sesiones de habla del operador hacia uno o más
/// parlantes; el PCM que manda el cliente se convierte a G.711 aquí y se
/// escribe en todos los canales a la vez.</item>
/// </list>
/// Un parlante atiende UNA cosa del VMS a la vez (<see cref="BusyOf"/>): la
/// voz del operador y los sonidos del servidor comparten el mismo canal del
/// equipo y no se mezclan.
/// </summary>
public sealed class SpeakerService(
    IServiceScopeFactory scopeFactory,
    SpeakerDriverRegistry drivers,
    CredentialProtector credentials,
    IHubContext<VmsHub> hub,
    AuditService audit,
    WorkflowStore store,
    IConfiguration config,
    ILogger<SpeakerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    /// <summary>G.711 a 8 kHz: 640 bytes = 80 ms. Trozo que se manda a todos los parlantes antes de esperar.</summary>
    private const int ChunkBytes = 640;
    private const int ChunkMilliseconds = 80;
    /// <summary>Tope de un sonido del servidor por reproducción.</summary>
    private const int MaxSoundSeconds = 120;

    private readonly ConcurrentDictionary<int, string> _busy = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _playing = new();
    private readonly SemaphoreSlim _wake = new(0);
    /// <summary>Apagado del servidor: corta los sonidos en bucle que sigan sonando.</summary>
    private CancellationToken _stopping = CancellationToken.None;

    /// <summary>Qué está ocupando el parlante ahora ("Voz: admin", "Sonido: sirena") o null si está libre.</summary>
    /// <summary>Ganancia de la voz del operador antes de codificar (Speakers:TalkGain; 1 = sin cambio). Los micrófonos suelen entregar poco nivel.</summary>
    private float TalkGain => Math.Clamp(config.GetValue("Speakers:TalkGain", 2.0f), 0.1f, 8f);

    public string? BusyOf(int speakerId) => _busy.TryGetValue(speakerId, out var what) ? what : null;

    /// <summary>La configuración cambió: sondear ahora en vez de esperar el ciclo.</summary>
    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public SpeakerConnectionInfo ConnectionOf(Speaker speaker) =>
        new(speaker.Host, speaker.Port, speaker.UseHttps, speaker.Username, credentials.Unprotect(speaker.PasswordCiphertext));

    public ISpeakerDriver DriverOf(Speaker speaker) =>
        drivers.Find(speaker.DriverKey)?.Create()
        ?? throw new DriverException($"El parlante '{speaker.Name}' usa un driver desconocido ('{speaker.DriverKey}').");

    public SpeakerDto ToDto(Speaker s) => new(s.Id, s.Name, s.DriverKey, s.Host, s.Port, s.UseHttps, s.Username,
        s.Model, s.SerialNumber, s.FirmwareVersion, s.GroupName, s.Enabled, s.Status, s.LastError, s.LastSeenAt, s.Volume,
        s.SupportsLibrary, s.SupportsTts, s.SupportsLiveAudio, BusyOf(s.Id), s.CreatedAt, s.UpdatedAt);

    // ------------------------------------------------------------------
    // Sondeo de estado
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        // Dar tiempo a que la API y el hub estén arriba antes del primer sondeo.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "El sondeo de parlantes IP falló."); }

            try { await _wake.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        List<Speaker> speakers;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            speakers = await db.Speakers.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);
        }
        if (speakers.Count == 0) return;

        using var limiter = new SemaphoreSlim(4);
        var results = await Task.WhenAll(speakers.Select(async speaker =>
        {
            await limiter.WaitAsync(ct);
            try { return (Speaker: speaker, Result: await PollOneAsync(speaker, ct)); }
            finally { limiter.Release(); }
        }));

        using var writeScope = scopeFactory.CreateScope();
        var writeDb = writeScope.ServiceProvider.GetRequiredService<VmsDbContext>();
        foreach (var (snapshot, result) in results)
        {
            var speaker = await writeDb.Speakers.FirstOrDefaultAsync(s => s.Id == snapshot.Id, ct);
            if (speaker is null) continue;
            var previous = speaker.Status;
            speaker.Status = result.Status;
            speaker.LastError = result.Error;
            if (result.Status == SpeakerStatus.Online)
            {
                speaker.LastSeenAt = DateTime.UtcNow;
                if (result.Volume is { } volume) speaker.Volume = volume;
            }
            if (previous == result.Status) continue;
            await writeDb.SaveChangesAsync(ct);

            if (result.Status == SpeakerStatus.Online && previous != SpeakerStatus.Unknown)
                await audit.LogSystemAsync("speakers", "speaker-online",
                    targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                    detail: $"El parlante '{speaker.Name}' ({speaker.Host}) recuperó la conexión.");
            else if (result.Status != SpeakerStatus.Online)
                await audit.LogSystemAsync("speakers", "speaker-offline",
                    targetType: "speaker", targetId: speaker.Id.ToString(), targetName: speaker.Name,
                    detail: $"El parlante '{speaker.Name}' ({speaker.Host}) no responde: {result.Error}", success: false);
            await hub.Clients.All.SendAsync(VmsHubContract.SpeakerStatusChanged, ToDto(speaker), ct);

            // Automatizaciones ("parlante sin conexión → avisar"). Resuelto al
            // vuelo: el motor contiene la acción de parlantes, que usa este servicio.
            if (previous != SpeakerStatus.Unknown || result.Status != SpeakerStatus.Online)
                writeScope.ServiceProvider.GetRequiredService<Workflows.WorkflowEngine>().Publish(
                    Workflows.WorkflowTrigger.FromDeviceStatus("speaker", speaker.Id, speaker.Name, speaker.Host, speaker.Model,
                        result.Status.ToString(), result.Error));
        }
        await writeDb.SaveChangesAsync(ct);
    }

    private async Task<(SpeakerStatus Status, string? Error, int? Volume)> PollOneAsync(Speaker speaker, CancellationToken ct)
    {
        try
        {
            int? volume = await DriverOf(speaker).GetVolumeAsync(ConnectionOf(speaker), ct);
            return (SpeakerStatus.Online, null, volume);
        }
        catch (DriverException ex)
        {
            bool auth = ex.Message.Contains("credenciales", StringComparison.OrdinalIgnoreCase);
            return (auth ? SpeakerStatus.AuthFailed : SpeakerStatus.Offline, ex.Message, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (SpeakerStatus.Offline, ex.Message, null);
        }
    }

    /// <summary>Marca el estado en línea tras una orden que sí llegó al equipo (evita esperar el sondeo).</summary>
    public async Task MarkOnlineAsync(int speakerId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == speakerId, ct);
        if (speaker is null) return;
        bool changed = speaker.Status != SpeakerStatus.Online;
        speaker.Status = SpeakerStatus.Online;
        speaker.LastError = null;
        speaker.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (changed) await hub.Clients.All.SendAsync(VmsHubContract.SpeakerStatusChanged, ToDto(speaker), ct);
    }

    // ------------------------------------------------------------------
    // Reproducción
    // ------------------------------------------------------------------

    private async Task<List<Speaker>> LoadAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var set = ids.Distinct().ToList();
        return await db.Speakers.AsNoTracking().Where(s => set.Contains(s.Id)).OrderBy(s => s.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Reproduce en los parlantes indicados. Devuelve un resultado por
    /// parlante (los que fallan no impiden que suenen los demás). Con
    /// <c>Source = server</c> la llamada dura lo que dura el sonido.
    /// </summary>
    public async Task<SpeakerOperationResultDto> PlayAsync(SpeakerPlayRequestDto request, string actor, CancellationToken ct)
    {
        var speakers = await LoadAsync(request.SpeakerIds, ct);
        if (speakers.Count == 0)
            return new SpeakerOperationResultDto(false, "No se indicó ningún parlante existente.", []);
        var disabled = speakers.Where(s => !s.Enabled).ToList();
        speakers = speakers.Where(s => s.Enabled).ToList();
        var results = disabled.Select(s => new SpeakerActionResultDto(s.Id, s.Name, false, "El parlante está desactivado.")).ToList();

        // Volumen pedido por la orden: se fija en cada parlante antes de sonar;
        // si un equipo no lo acepta se avisa, pero igual se reproduce.
        var volumeNotes = request.Volume is { } volume ? await SetVolumeQuietlyAsync(speakers, volume, ct) : [];

        string source = (request.Source ?? "").Trim().ToLowerInvariant();
        switch (source)
        {
            case SpeakerPlaySources.Server:
                results.AddRange(await PlayServerSoundAsync(speakers, request.Sound ?? "", Math.Clamp(request.Repeat, 0, 5), actor, ct));
                break;
            case SpeakerPlaySources.Library:
                results.AddRange(await PlayLibraryAsync(speakers, request.LibraryName ?? "", ct));
                break;
            case SpeakerPlaySources.Tts:
                results.AddRange(await PlayTtsAsync(speakers, request.Text ?? "", request.Language ?? "spanish", request.Voice ?? "female", ct));
                break;
            default:
                return new SpeakerOperationResultDto(false, $"Origen de audio desconocido: '{request.Source}'.", results);
        }

        if (volumeNotes.Count > 0)
            results = results.Select(r => volumeNotes.TryGetValue(r.SpeakerId, out var note) ? r with { Message = $"{r.Message} {note}" } : r).ToList();
        int ok = results.Count(r => r.Success);
        string summary = ok == results.Count ? $"Reproducido en {ok} parlante{(ok == 1 ? "" : "s")}."
            : ok == 0 ? "Ningún parlante reprodujo el audio."
            : $"Reproducido en {ok} de {results.Count} parlantes.";
        return new SpeakerOperationResultDto(ok > 0, summary, results);
    }

    private async Task<List<SpeakerActionResultDto>> PlayServerSoundAsync(List<Speaker> speakers, string sound, int repeat,
        string actor, CancellationToken ct)
    {
        var results = new List<SpeakerActionResultDto>();
        if (sound.Length == 0)
            return speakers.Select(s => new SpeakerActionResultDto(s.Id, s.Name, false, "No se indicó el sonido.")).ToList();
        if (store.FindAudio(sound) is null)
            return speakers.Select(s => new SpeakerActionResultDto(s.Id, s.Name, false, $"El sonido '{sound}' no existe en el servidor.")).ToList();

        // 1) Reservar los parlantes libres y abrir sus canales en paralelo.
        var sessions = new List<(Speaker Speaker, ISpeakerAudioSession Session, byte[] Payload)>();
        var claimed = new List<Speaker>();
        foreach (var speaker in speakers)
        {
            if (!speaker.SupportsLiveAudio)
            {
                results.Add(new(speaker.Id, speaker.Name, false, "El parlante no acepta audio en vivo."));
                continue;
            }
            if (!_busy.TryAdd(speaker.Id, $"Sonido: {sound}"))
            {
                results.Add(new(speaker.Id, speaker.Name, false, $"El parlante está ocupado ({BusyOf(speaker.Id)})."));
                continue;
            }
            claimed.Add(speaker);
        }

        bool loop = repeat == 0;
        bool streaming = false;
        try
        {
            var opened = await Task.WhenAll(claimed.Select(async speaker =>
            {
                try
                {
                    var session = await DriverOf(speaker).OpenLiveAudioAsync(ConnectionOf(speaker), ct);
                    string codec = session.Codec;
                    if (codec == "pcm16")
                    {
                        await session.DisposeAsync();
                        return (speaker, (ISpeakerAudioSession?)null, (byte[]?)null,
                            "El canal del parlante pide PCM; configure G.711 en el equipo para los sonidos del servidor.");
                    }
                    if (store.FindPayload(sound, codec) is not { } path)
                    {
                        await session.DisposeAsync();
                        return (speaker, null, null, $"El sonido '{sound}' no está convertido a G.711 {codec}: vuelva a subirlo.");
                    }
                    byte[] payload = await File.ReadAllBytesAsync(path, ct);
                    if (payload.Length > MaxSoundSeconds * G711.BytesPerSecond) payload = payload[..(MaxSoundSeconds * G711.BytesPerSecond)];
                    return (speaker, session, payload, (string?)null);
                }
                catch (DriverException ex) { return (speaker, null, null, ex.Message); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return (speaker, null, null, ex.Message); }
            }));
            foreach (var (speaker, session, payload, error) in opened)
            {
                if (session is null || payload is null)
                {
                    results.Add(new(speaker.Id, speaker.Name, false, error ?? "No se pudo abrir el canal de audio."));
                    _busy.TryRemove(speaker.Id, out _);
                    continue;
                }
                sessions.Add((speaker, session, payload));
            }
            if (sessions.Count == 0) return results;

            if (loop)
            {
                // En bucle hasta que alguien lo detenga (acción "Detener",
                // el operador o el apagado del servidor): la transmisión sigue
                // en segundo plano y quien pidió el sonido no se queda esperando.
                var claimedNow = sessions.Select(s => s.Speaker).ToList();
                streaming = true;
                _ = Task.Run(async () =>
                {
                    try { await StreamAsync(sessions, sound, 0, _stopping); }
                    catch (Exception ex) { logger.LogWarning(ex, "El sonido en bucle '{Sound}' terminó con error.", sound); }
                    finally
                    {
                        foreach (var (_, session, _) in sessions)
                        {
                            try { await session.DisposeAsync(); }
                            catch (Exception ex) { logger.LogDebug(ex, "No se pudo cerrar el canal de audio de un parlante."); }
                        }
                        foreach (var speaker in claimedNow) _busy.TryRemove(speaker.Id, out _);
                    }
                }, CancellationToken.None);
                results.AddRange(sessions.Select(s => new SpeakerActionResultDto(s.Speaker.Id, s.Speaker.Name, true,
                    $"Sonido '{sound}' en bucle hasta que se detenga.")));
                return results;
            }

            results.AddRange(await StreamAsync(sessions, sound, repeat, ct));
        }
        finally
        {
            if (!streaming)
            {
                foreach (var (_, session, _) in sessions)
                {
                    try { await session.DisposeAsync(); }
                    catch (Exception ex) { logger.LogDebug(ex, "No se pudo cerrar el canal de audio de un parlante."); }
                }
                foreach (var speaker in claimed) _busy.TryRemove(speaker.Id, out _);
            }
        }
        return results;
    }

    /// <summary>
    /// 2) Envía el mismo trozo a todos y recién entonces espera: así los
    /// parlantes van a la par (desfase = jitter de la red). <paramref name="repeat"/>
    /// 0 = sin fin, hasta que se cancele por <see cref="StopAsync"/>.
    /// </summary>
    private async Task<List<SpeakerActionResultDto>> StreamAsync(
        List<(Speaker Speaker, ISpeakerAudioSession Session, byte[] Payload)> sessions, string sound, int repeat, CancellationToken ct)
    {
        var results = new List<SpeakerActionResultDto>();
        using var stopper = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopper.Token);
        foreach (var (speaker, _, _) in sessions) _playing[speaker.Id] = stopper;
        var failures = new Dictionary<int, string>();
        int length = sessions.Max(s => s.Payload.Length);
        var started = DateTime.UtcNow;
        int chunkIndex = 0;
        int passes = 0;
        try
        {
            for (int pass = 0; (repeat == 0 || pass < repeat) && !linked.IsCancellationRequested; pass++, passes++)
            {
                for (int offset = 0; offset < length && !linked.IsCancellationRequested; offset += ChunkBytes, chunkIndex++)
                {
                    var writes = new List<Task>();
                    foreach (var (speaker, session, payload) in sessions)
                    {
                        if (failures.ContainsKey(speaker.Id) || offset >= payload.Length) continue;
                        int size = Math.Min(ChunkBytes, payload.Length - offset);
                        var frame = payload.AsMemory(offset, size);
                        writes.Add(WriteOrRecordAsync(session, speaker, frame, failures, linked.Token));
                    }
                    await Task.WhenAll(writes);
                    if (failures.Count == sessions.Count) break;
                    // Ritmo real: el trozo k debe salir k×80 ms después del inicio.
                    var due = started + TimeSpan.FromMilliseconds((chunkIndex + 1) * ChunkMilliseconds);
                    var wait = due - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, linked.Token);
                }
                if (failures.Count == sessions.Count) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Detenido por el operador o por una acción "Detener": se cierra normalmente.
        }
        catch (OperationCanceledException) when (repeat == 0)
        {
            // Apagado del servidor con un bucle en curso: se cierra normalmente.
        }
        bool stopped = stopper.IsCancellationRequested;
        foreach (var (speaker, session, payload) in sessions)
        {
            _playing.TryRemove(speaker.Id, out _);
            double seconds = Math.Round(payload.Length * Math.Max(passes, 1) / (double)G711.BytesPerSecond, 1);
            results.Add(failures.TryGetValue(speaker.Id, out var error)
                ? new(speaker.Id, speaker.Name, false, error)
                : new(speaker.Id, speaker.Name, true, stopped
                    ? $"Sonido '{sound}' detenido."
                    : $"Sonido '{sound}' reproducido ({seconds} s{(repeat > 1 ? $", {repeat} repeticiones" : "")})."));
        }
        return results;
    }

    /// <summary>
    /// Fija el volumen en cada parlante antes de reproducir. Devuelve, por
    /// parlante, la nota a agregar al resultado ("volumen 80 %" o el motivo
    /// por el que no se pudo); nunca impide la reproducción.
    /// </summary>
    private async Task<Dictionary<int, string>> SetVolumeQuietlyAsync(List<Speaker> speakers, int volume, CancellationToken ct)
    {
        volume = Math.Clamp(volume, 0, 100);
        var notes = new Dictionary<int, string>();
        await Task.WhenAll(speakers.Select(async speaker =>
        {
            try
            {
                await DriverOf(speaker).SetVolumeAsync(ConnectionOf(speaker), volume, ct);
                lock (notes) notes[speaker.Id] = $"Volumen fijado en {volume} %.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "No se pudo fijar el volumen del parlante '{Name}'.", speaker.Name);
                lock (notes) notes[speaker.Id] = $"No se pudo fijar el volumen: {ex.Message}";
            }
        }));
        return notes;
    }

    private static async Task WriteOrRecordAsync(ISpeakerAudioSession session, Speaker speaker, ReadOnlyMemory<byte> frame,
        Dictionary<int, string> failures, CancellationToken ct)
    {
        try { await session.WriteAsync(frame, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (failures) failures[speaker.Id] = ex.Message;
        }
    }

    private async Task<List<SpeakerActionResultDto>> PlayLibraryAsync(List<Speaker> speakers, string name, CancellationToken ct)
    {
        if (name.Trim().Length == 0)
            return speakers.Select(s => new SpeakerActionResultDto(s.Id, s.Name, false, "No se indicó el audio de la biblioteca.")).ToList();

        // Resolver el id en cada equipo primero y recién entonces dar la orden
        // a todos: así arrancan casi a la vez.
        var resolved = await Task.WhenAll(speakers.Select(async speaker =>
        {
            if (!speaker.SupportsLibrary) return (speaker, (SpeakerAudioItem?)null, "El parlante no tiene biblioteca de audios.");
            try
            {
                var library = await DriverOf(speaker).GetLibraryAsync(ConnectionOf(speaker), ct);
                var item = FindInLibrary(library, name);
                return item is null
                    ? (speaker, null, $"El parlante no tiene un audio llamado '{name}' en su biblioteca.")
                    : (speaker, item, (string?)null);
            }
            catch (DriverException ex) { return (speaker, null, ex.Message); }
        }));

        var results = new List<SpeakerActionResultDto>();
        var orders = new List<Task<SpeakerActionResultDto>>();
        foreach (var (speaker, item, error) in resolved)
        {
            if (item is null) { results.Add(new(speaker.Id, speaker.Name, false, error ?? "Audio no encontrado.")); continue; }
            orders.Add(PlayOneAsync(speaker, item, ct));
        }
        results.AddRange(await Task.WhenAll(orders));
        return results;
    }

    private async Task<SpeakerActionResultDto> PlayOneAsync(Speaker speaker, SpeakerAudioItem item, CancellationToken ct)
    {
        try
        {
            await DriverOf(speaker).PlayLibraryAsync(ConnectionOf(speaker), item.Id, ct);
            _ = MarkOnlineAsync(speaker.Id, CancellationToken.None);
            return new(speaker.Id, speaker.Name, true, $"Reproduciendo '{item.Name}' ({item.DurationSeconds} s).");
        }
        catch (DriverException ex) { return new(speaker.Id, speaker.Name, false, ex.Message); }
    }

    /// <summary>Busca por nombre exacto, sin extensión o sin distinguir mayúsculas.</summary>
    public static SpeakerAudioItem? FindInLibrary(IReadOnlyList<SpeakerAudioItem> library, string name)
    {
        string wanted = name.Trim();
        string bare = Path.GetFileNameWithoutExtension(wanted);
        return library.FirstOrDefault(i => string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase))
               ?? library.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i.Name), bare, StringComparison.OrdinalIgnoreCase))
               ?? library.FirstOrDefault(i => i.Name.Contains(bare, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<SpeakerActionResultDto>> PlayTtsAsync(List<Speaker> speakers, string text, string language, string voice,
        CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0)
            return speakers.Select(s => new SpeakerActionResultDto(s.Id, s.Name, false, "No se indicó el texto a leer.")).ToList();
        if (text.Length > 100) text = text[..100];   // tope del fabricante

        // Un mismo texto reutiliza el archivo ya generado (nombre derivado del
        // contenido): generar voz tarda segundos y llena la biblioteca.
        string name = "tcvms-tts-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{language}|{voice}|{text}")))[..10].ToLowerInvariant();

        var prepared = await Task.WhenAll(speakers.Select(async speaker =>
        {
            if (!speaker.SupportsTts) return (speaker, (SpeakerAudioItem?)null, "El parlante no genera texto a voz.");
            try
            {
                var driver = DriverOf(speaker);
                var conn = ConnectionOf(speaker);
                var existing = FindInLibrary(await driver.GetLibraryAsync(conn, ct), name);
                existing ??= await driver.CreateTtsAsync(conn, name, text, language, voice, ct);
                return (speaker, existing, (string?)null);
            }
            catch (DriverException ex) { return (speaker, null, ex.Message); }
        }));

        var results = new List<SpeakerActionResultDto>();
        var orders = new List<Task<SpeakerActionResultDto>>();
        foreach (var (speaker, item, error) in prepared)
        {
            if (item is null) { results.Add(new(speaker.Id, speaker.Name, false, error ?? "No se pudo generar la voz.")); continue; }
            orders.Add(PlayOneAsync(speaker, item, ct));
        }
        results.AddRange(await Task.WhenAll(orders));
        return results;
    }

    /// <summary>Detiene lo que el VMS esté transmitiendo y lo que el equipo esté reproduciendo de su biblioteca.</summary>
    public async Task<SpeakerOperationResultDto> StopAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var speakers = await LoadAsync(ids, ct);
        var results = await Task.WhenAll(speakers.Select(async speaker =>
        {
            bool cancelled = false;
            if (_playing.TryGetValue(speaker.Id, out var cts))
            {
                cancelled = true;
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* ya terminó */ }
            }
            try
            {
                if (speaker.SupportsLibrary) await DriverOf(speaker).StopAsync(ConnectionOf(speaker), ct);
                return new SpeakerActionResultDto(speaker.Id, speaker.Name, true, cancelled ? "Transmisión detenida." : "Reproducción detenida.");
            }
            catch (DriverException ex)
            {
                return new SpeakerActionResultDto(speaker.Id, speaker.Name, cancelled, cancelled ? "Transmisión detenida." : ex.Message);
            }
        }));
        int ok = results.Count(r => r.Success);
        return new SpeakerOperationResultDto(ok > 0 || results.Length == 0,
            results.Length == 0 ? "No se indicó ningún parlante." : $"Detenido en {ok} de {results.Length} parlantes.", results);
    }

    // ------------------------------------------------------------------
    // Voz en vivo
    // ------------------------------------------------------------------

    /// <summary>
    /// Abre el canal de voz hacia los parlantes indicados. Los que estén
    /// ocupados o fallen quedan informados en <see cref="TalkSession.Rejected"/>;
    /// si ninguno abre, lanza <see cref="DriverException"/>.
    /// </summary>
    public async Task<TalkSession> OpenTalkAsync(IReadOnlyList<int> ids, string actor, CancellationToken ct)
    {
        var speakers = await LoadAsync(ids, ct);
        if (speakers.Count == 0) throw new DriverException("No se indicó ningún parlante existente.");
        var rejected = new List<SpeakerActionResultDto>();
        var claimed = new List<Speaker>();
        foreach (var speaker in speakers)
        {
            if (!speaker.Enabled) { rejected.Add(new(speaker.Id, speaker.Name, false, "El parlante está desactivado.")); continue; }
            if (!speaker.SupportsLiveAudio) { rejected.Add(new(speaker.Id, speaker.Name, false, "El parlante no acepta audio en vivo.")); continue; }
            if (!_busy.TryAdd(speaker.Id, $"Voz: {actor}"))
            {
                rejected.Add(new(speaker.Id, speaker.Name, false, $"El parlante está ocupado ({BusyOf(speaker.Id)})."));
                continue;
            }
            claimed.Add(speaker);
        }

        var opened = await Task.WhenAll(claimed.Select(async speaker =>
        {
            try { return (speaker, (ISpeakerAudioSession?)await DriverOf(speaker).OpenLiveAudioAsync(ConnectionOf(speaker), ct), (string?)null); }
            catch (DriverException ex) { return (speaker, null, ex.Message); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (speaker, null, ex.Message); }
        }));
        var sessions = new List<(Speaker, ISpeakerAudioSession)>();
        foreach (var (speaker, session, error) in opened)
        {
            if (session is null)
            {
                _busy.TryRemove(speaker.Id, out _);
                rejected.Add(new(speaker.Id, speaker.Name, false, error ?? "No se pudo abrir el canal de audio."));
                continue;
            }
            sessions.Add((speaker, session));
        }
        if (sessions.Count == 0)
            throw new DriverException(rejected.Count > 0 ? rejected[0].Message : "Ningún parlante aceptó la voz en vivo.");
        return new TalkSession(this, sessions, rejected, logger);
    }

    /// <summary>Sesión de habla del operador hacia uno o más parlantes.</summary>
    public sealed class TalkSession : IAsyncDisposable
    {
        private readonly SpeakerService _owner;
        private readonly ILogger _log;
        private readonly List<(Speaker Speaker, ISpeakerAudioSession Session)> _sessions;
        private readonly Dictionary<int, string> _failures = [];
        private int _disposed;

        internal TalkSession(SpeakerService owner, List<(Speaker, ISpeakerAudioSession)> sessions, List<SpeakerActionResultDto> rejected, ILogger log)
        {
            _owner = owner;
            _log = log;
            _sessions = sessions;
            Rejected = rejected;
            StartedAt = DateTime.UtcNow;
        }

        public DateTime StartedAt { get; }
        public long BytesSent { get; private set; }
        public IReadOnlyList<SpeakerActionResultDto> Rejected { get; }
        public IReadOnlyList<Speaker> Speakers => _sessions.Select(s => s.Speaker).ToList();
        public IReadOnlyList<Speaker> Alive => _sessions.Where(s => !_failures.ContainsKey(s.Speaker.Id) && s.Session.IsAlive).Select(s => s.Speaker).ToList();
        public IReadOnlyDictionary<int, string> Failures => _failures;

        /// <summary>PCM 16 bits LE mono a 8 kHz: se convierte al códec de cada canal y se escribe en todos a la vez.</summary>
        public async Task WritePcmAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            if (_disposed != 0 || pcm16.Length < 2) return;
            float gain = _owner.TalkGain;
            if (Math.Abs(gain - 1f) > 0.001f)
            {
                var boosted = pcm16.ToArray();
                G711.ApplyGain(boosted, gain);
                pcm16 = boosted;
            }
            byte[]? alaw = null, ulaw = null;
            var writes = new List<Task>();
            foreach (var (speaker, session) in _sessions)
            {
                if (_failures.ContainsKey(speaker.Id)) continue;
                ReadOnlyMemory<byte> frame = session.Codec switch
                {
                    "alaw" => alaw ??= G711.Encode(pcm16.Span, aLaw: true),
                    "ulaw" => ulaw ??= G711.Encode(pcm16.Span, aLaw: false),
                    _ => pcm16,
                };
                writes.Add(WriteOrRecordAsync(session, speaker, frame, _failures, ct));
            }
            await Task.WhenAll(writes);
            BytesSent += pcm16.Length;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var (speaker, session) in _sessions)
            {
                try { await session.DisposeAsync(); }
                catch (Exception ex) { _log.LogDebug(ex, "No se pudo cerrar el canal de voz de un parlante."); }
                _owner._busy.TryRemove(speaker.Id, out _);
            }
        }
    }
}
