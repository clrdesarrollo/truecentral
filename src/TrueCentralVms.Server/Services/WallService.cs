using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Orquesta la operación del muro de video: traduce canales del inventario a
/// StreamSource del driver del decodificador, ejecuta los comandos en el
/// equipo, persiste el estado y notifica a todos los clientes por SignalR.
/// </summary>
public sealed class WallService
{
    private readonly VmsDbContext _db;
    private readonly DecoderSessionManager _sessions;
    private readonly DriverRegistry _drivers;
    private readonly CredentialProtector _credentials;
    private readonly IHubContext<VmsHub> _hub;
    private readonly ILogger<WallService> _logger;

    public WallService(VmsDbContext db, DecoderSessionManager sessions, DriverRegistry drivers,
        CredentialProtector credentials, IHubContext<VmsHub> hub, ILogger<WallService> logger)
    {
        _db = db;
        _sessions = sessions;
        _drivers = drivers;
        _credentials = credentials;
        _hub = hub;
        _logger = logger;
    }

    private IQueryable<VideoWall> WallsWithState => _db.Walls
        .Include(w => w.Decoder)
        .Include(w => w.Screens).ThenInclude(s => s.Windows).ThenInclude(x => x.AssignedChannel!).ThenInclude(c => c.Device)
        .Include(w => w.Floating).ThenInclude(f => f.AssignedChannel!).ThenInclude(c => c.Device)
        .AsSplitQuery();

    public async Task<WallDto?> GetWallDtoAsync(int wallId)
    {
        var wall = await WallsWithState.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wallId);
        return wall is null ? null : ToDto(wall);
    }

    public async Task<List<WallDto>> GetWallDtosAsync()
    {
        var walls = await WallsWithState.AsNoTracking().OrderBy(w => w.Name).ToListAsync();
        return walls.Select(ToDto).ToList();
    }

    /// <summary>
    /// Lo que muestra una ventana, resuelto para que el cliente no consulte el
    /// árbol de dispositivos: un canal del inventario o una fuente externa por
    /// URL (proyección de la pantalla de un operador).
    /// </summary>
    private static AssignmentDto? ToAssignment(Channel? channel, string? externalUrl, string? externalLabel, int streamType)
    {
        if (externalUrl is not null)
            return new AssignmentDto(0, 0, "Proyección", 0, externalLabel ?? "Fuente externa", streamType, externalUrl);
        return channel is null
            ? null
            : new AssignmentDto(
                channel.Id, channel.DeviceId, channel.Device?.Name ?? $"Dispositivo {channel.DeviceId}",
                channel.ChannelNumber, channel.Name, streamType);
    }

    public static WallDto ToDto(VideoWall wall) => new(
        wall.Id, wall.Name, wall.DecoderId,
        wall.Decoder?.Name ?? "", wall.Decoder?.DriverKey ?? "",
        wall.Rows, wall.Columns,
        wall.Screens
            .OrderBy(s => s.Row).ThenBy(s => s.Col)
            .Select(s => new ScreenDto(
                s.Id, s.Row, s.Col, s.Label, s.DisplayChannel, s.WindowMode,
                s.FullscreenWindowId,
                s.Windows
                    .OrderBy(x => x.WindowIndex)
                    .Select(x => new WindowDto(
                        x.Id, x.WindowIndex, x.DecodeChannel,
                        Math.Max(1, x.SpanCols), Math.Max(1, x.SpanRows),
                        ToAssignment(x.AssignedChannel, x.ExternalUrl, x.ExternalLabel, x.AssignedStreamType)))
                    .ToList()))
            .ToList(),
        wall.Floating
            .OrderBy(f => f.Id)
            .Select(f => new FloatingWindowDto(
                f.Id, f.X, f.Y, f.W, f.H, f.DecodeChannel,
                ToAssignment(f.AssignedChannel, f.ExternalUrl, f.ExternalLabel, f.AssignedStreamType),
                Fullscreen: f.HomeX is not null))
            .ToList(),
        wall.FullscreenWindowId);

    /// <summary>
    /// Clave del driver de dispositivos cuyo protocolo privado entiende el
    /// decodificador Hikvision de forma nativa. El resto de las marcas viajan
    /// por URL RTSP.
    /// </summary>
    private const string HikvisionDeviceDriverKey = "hikvision-netsdk";

    /// <summary>
    /// Traduce un canal del inventario del VMS a la descripción de stream que
    /// el decodificador debe reproducir.
    ///
    /// El decoder se conecta DIRECTO al equipo, no a MediaMTX: las concesiones
    /// de streaming del VMS son tokens de 60 s pensados para espectadores, y
    /// un muro decodifica durante días. Con Hikvision se usa el protocolo
    /// privado del fabricante (arranque más rápido y cambio de stream sin
    /// re-negociar RTSP); con Dahua/ONVIF, la URL RTSP que arma su propio
    /// driver de dispositivo.
    /// </summary>
    public StreamSource BuildStreamSource(Channel channel, int streamType)
    {
        var device = channel.Device
            ?? throw new InvalidOperationException($"El canal {channel.Id} no tiene dispositivo cargado.");
        string password = _credentials.Unprotect(device.PasswordCiphertext);

        if (string.Equals(device.DriverKey, HikvisionDeviceDriverKey, StringComparison.OrdinalIgnoreCase))
            return new StreamSource
            {
                Mode = StreamSourceMode.DeviceChannel,
                Host = device.Host,
                Port = device.SdkPort,
                Username = device.Username,
                Password = password,
                Channel = channel.ChannelNumber,
                StreamType = streamType,
                TransportProtocol = 0,
            };

        var factory = _drivers.Find(device.DriverKey)
            ?? throw new InvalidOperationException(
                $"El dispositivo '{device.Name}' usa el driver '{device.DriverKey}', que no está registrado en este servidor.");

        var conn = new DeviceConnectionInfo(device.Host, device.SdkPort, device.Username, password);
        var profile = streamType == 1 ? StreamProfile.Sub : StreamProfile.Main;

        // ONVIF guarda la URL real resuelta con GetStreamUri (sin credenciales:
        // se inyectan aquí); las marcas con plantilla la construye el driver.
        string? stored = profile == StreamProfile.Main ? channel.RtspMainUrl : channel.RtspSubUrl;
        string url = stored is { Length: > 0 }
            ? InjectCredentials(stored, conn)
            : factory.Create().BuildRtspUrl(conn, device.RtspPort, channel.RtspChannel, profile);

        return new StreamSource { Mode = StreamSourceMode.Url, Url = url, TransportProtocol = 0 };
    }

    /// <summary>Stream de una fuente externa por URL (proyección del operador).</summary>
    private static StreamSource ExternalStream(string url) =>
        new() { Mode = StreamSourceMode.Url, Url = url, TransportProtocol = 0 };

    /// <summary>Inserta usuario y contraseña en una URL RTSP guardada sin credenciales.</summary>
    private static string InjectCredentials(string rtspUrl, DeviceConnectionInfo conn)
    {
        if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri))
            return rtspUrl;
        string credentials = $"{Uri.EscapeDataString(conn.Username)}:{Uri.EscapeDataString(conn.Password)}";
        return $"{uri.Scheme}://{credentials}@{uri.Host}:{(uri.IsDefaultPort ? 554 : uri.Port)}{uri.PathAndQuery}";
    }

    /// <summary>Canal del inventario con su dispositivo cargado (lo necesita <see cref="BuildStreamSource"/>).</summary>
    private async Task<Channel> LoadChannelAsync(int channelId) =>
        await _db.Channels.Include(c => c.Device).FirstOrDefaultAsync(c => c.Id == channelId)
            ?? throw new KeyNotFoundException($"No existe el canal {channelId} en el inventario.");

    private async Task<(VideoWall Wall, ScreenWindow Window)> FindWindowAsync(int wallId, int windowId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var window = wall.Screens.SelectMany(s => s.Windows).FirstOrDefault(x => x.Id == windowId)
            ?? throw new KeyNotFoundException($"No existe la ventana {windowId} en el wall {wallId}.");
        return (wall, window);
    }

    /// <summary>
    /// Ejecuta una operación de decodificación; si el driver indica que no
    /// tiene el mapa de ventanas (sesión nueva tras reinicio), sincroniza el
    /// layout con el equipo y reintenta una vez.
    /// </summary>
    private async Task WithSyncRetryAsync(VideoWall wall, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (WallSyncRequiredException)
        {
            _logger.LogInformation("El driver requiere sincronizar el wall {Wall}; sincronizando y reintentando", wall.Name);
            await ResyncWallAsync(wall);
            await action();
        }
    }

    /// <summary>
    /// Re-sincroniza el layout con el equipo. La sincronización recoloca las
    /// ventanas por roaming (no corta la decodificación) y re-aplica las
    /// pantallas completas vigentes, así el muro queda igual que antes del
    /// reinicio del servidor.
    /// </summary>
    private Task ResyncWallAsync(VideoWall wall) => PushLayoutAsync(wall);

    /// <summary>
    /// Precalienta las sesiones de los decoders al arrancar el servidor:
    /// sincroniza cada wall (reconstruye el mapa canal→ventana del driver) para
    /// que el primer comando del operador —p. ej. la pantalla completa por doble
    /// clic— sea instantáneo en vez de pagar la sincronización perezosa.
    /// </summary>
    public async Task WarmUpDecodersAsync(CancellationToken ct = default)
    {
        var walls = await WallsWithState.ToListAsync(ct);
        foreach (var wall in walls)
        {
            if (wall.Decoder is null || wall.Screens.Count == 0) continue;
            try
            {
                await ResyncWallAsync(wall);
                _logger.LogInformation("Wall {Wall} sincronizado al arranque; sesión del decoder lista", wall.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo precalentar el wall {Wall} al arranque", wall.Name);
            }
        }
    }

    public async Task<OperationResultDto> AssignAsync(int wallId, AssignRequest request)
    {
        var (wall, window) = await FindWindowAsync(wallId, request.WindowId);
        var channel = await LoadChannelAsync(request.ChannelId);

        try
        {
            var stream = BuildStreamSource(channel, request.StreamType);
            await WithSyncRetryAsync(wall, () =>
                _sessions.WithDriverAsync(wall.Decoder!, d => d.StartDecodingAsync(window.DecodeChannel, stream)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo asignando el canal {Channel} a la ventana {Window} del muro {Wall}",
                channel.Name, window.Id, wall.Name);
            return new OperationResultDto(false, ex.Message);
        }

        window.AssignedChannelId = channel.Id;
        window.AssignedStreamType = request.StreamType;
        window.ExternalUrl = null;
        window.ExternalLabel = null;
        await _db.SaveChangesAsync();

        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>
    /// Pone en una ventana una fuente externa por URL: el caso real es la
    /// proyección de la pantalla de un operador, cuyo PC publica un RTSP local
    /// que el decodificador consume. No pasa por el inventario porque no es un
    /// equipo administrado (existe solo mientras dura la transmisión).
    /// </summary>
    public async Task<OperationResultDto> AssignExternalAsync(int wallId, ExternalAssignRequest request)
    {
        var (wall, window) = await FindWindowAsync(wallId, request.WindowId);
        if (string.IsNullOrWhiteSpace(request.Url))
            return new OperationResultDto(false, "La URL de la fuente externa es obligatoria.");

        try
        {
            await WithSyncRetryAsync(wall, () => _sessions.WithDriverAsync(wall.Decoder!,
                d => d.StartDecodingAsync(window.DecodeChannel, ExternalStream(request.Url))));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo proyectando {Label} en la ventana {Window} del muro {Wall}",
                request.Label, window.Id, wall.Name);
            return new OperationResultDto(false, ex.Message);
        }

        window.AssignedChannelId = null;
        window.AssignedStreamType = 0;
        window.ExternalUrl = request.Url;
        window.ExternalLabel = string.IsNullOrWhiteSpace(request.Label) ? "Proyección" : request.Label.Trim();
        await _db.SaveChangesAsync();

        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    public async Task<OperationResultDto> ClearAsync(int wallId, int windowId)
    {
        var (wall, window) = await FindWindowAsync(wallId, windowId);

        try
        {
            await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(window.DecodeChannel));
        }
        catch (Exception ex)
        {
            // Si el canal ya estaba detenido lo tratamos como éxito lógico:
            // igual limpiamos el estado persistido para no dejar la UI trabada.
            _logger.LogWarning(ex, "StopDecoding falló en ventana {Window} del wall {Wall}; se limpia el estado igualmente",
                window.Id, wall.Name);
        }

        window.AssignedChannelId = null;
        window.AssignedStreamType = 0;
        window.ExternalUrl = null;
        window.ExternalLabel = null;
        await _db.SaveChangesAsync();

        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>
    /// Intercambia las cámaras de dos ventanas del wall (arrastrar y soltar en
    /// el cliente). Solo se re-decodifican los dos canales afectados; el resto
    /// del muro no se toca. Si una ventana estaba vacía, la cámara se mueve y
    /// la ventana de origen queda libre.
    /// </summary>
    public async Task<OperationResultDto> SwapWindowsAsync(int wallId, int windowAId, int windowBId)
    {
        if (windowAId == windowBId) return new OperationResultDto(true, null);

        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var windows = wall.Screens.SelectMany(s => s.Windows).ToList();
        var a = windows.FirstOrDefault(x => x.Id == windowAId)
            ?? throw new KeyNotFoundException($"No existe la ventana {windowAId} en el wall {wallId}.");
        var b = windows.FirstOrDefault(x => x.Id == windowBId)
            ?? throw new KeyNotFoundException($"No existe la ventana {windowBId} en el wall {wallId}.");

        (a.AssignedChannelId, b.AssignedChannelId) = (b.AssignedChannelId, a.AssignedChannelId);
        (a.AssignedStreamType, b.AssignedStreamType) = (b.AssignedStreamType, a.AssignedStreamType);
        (a.ExternalUrl, b.ExternalUrl) = (b.ExternalUrl, a.ExternalUrl);
        (a.ExternalLabel, b.ExternalLabel) = (b.ExternalLabel, a.ExternalLabel);

        var channelIds = new[] { a.AssignedChannelId, b.AssignedChannelId }
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var channels = await _db.Channels.Include(c => c.Device)
            .Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);

        string? error = null;
        foreach (var window in new[] { a, b })
        {
            try
            {
                if (window.ExternalUrl is { } externalUrl)
                {
                    await WithSyncRetryAsync(wall, () => _sessions.WithDriverAsync(wall.Decoder!,
                        d => d.StartDecodingAsync(window.DecodeChannel, ExternalStream(externalUrl))));
                }
                else if (window.AssignedChannelId is int chId && channels.TryGetValue(chId, out var channel))
                {
                    var stream = BuildStreamSource(channel, window.AssignedStreamType);
                    await WithSyncRetryAsync(wall, () =>
                        _sessions.WithDriverAsync(wall.Decoder!, d => d.StartDecodingAsync(window.DecodeChannel, stream)));
                }
                else
                {
                    // La ventana quedó vacía tras el intercambio: detener su canal.
                    try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(window.DecodeChannel)); }
                    catch (Exception ex) { _logger.LogDebug(ex, "StopDecoding tras swap en ventana {Window}", window.Id); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallo re-decodificando la ventana {Window} tras el intercambio", window.Id);
                error = ex.Message;
            }
        }

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(error is null, error);
    }

    public async Task<Dictionary<int, OperationResultDto>> ClearAllAsync(int wallId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");

        var results = new Dictionary<int, OperationResultDto>();
        foreach (var window in wall.Screens.SelectMany(s => s.Windows))
        {
            try
            {
                await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(window.DecodeChannel));
                results[window.Id] = new OperationResultDto(true, null);
            }
            catch (Exception ex)
            {
                results[window.Id] = new OperationResultDto(true, $"Aviso: {ex.Message}");
            }
            window.AssignedChannelId = null;
            window.AssignedStreamType = 0;
            window.ExternalUrl = null;
            window.ExternalLabel = null;
        }

        // Las ventanas flotantes también se limpian: se detienen y se quitan del muro.
        var floats = wall.Floating.ToList();
        foreach (var floating in floats)
        {
            try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(floating.DecodeChannel)); }
            catch (Exception ex) { _logger.LogDebug(ex, "StopDecoding de la flotante {Floating} en limpiar-todo", floating.Id); }
            try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.CloseWindowAsync(floating.DecodeChannel)); }
            catch (Exception ex) { _logger.LogDebug(ex, "CloseWindow de la flotante {Floating} en limpiar-todo", floating.Id); }
            wall.Floating.Remove(floating);
            _db.WallFloatingWindows.Remove(floating);
        }
        if (floats.Count > 0)
            await PushLayoutAsync(wall);

        // Sin cámaras no tiene sentido dejar una ventana (ya negra) tapando
        // todo el muro: se devuelve a su sub-celda.
        await RestoreWallFullscreenAsync(wall);

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return results;
    }

    private static List<ScreenWindowLayout> BuildLayout(VideoWall wall) => wall.Screens
        .OrderBy(s => s.Row).ThenBy(s => s.Col)
        .Select(s =>
        {
            var ordered = s.Windows.OrderBy(x => x.WindowIndex).ToList();
            return new ScreenWindowLayout(
                s.DisplayChannel, s.Row, s.Col,
                ordered.Select(x => x.DecodeChannel).ToList(),
                ordered.Select(x => new WallWindowSlot(x.WindowIndex, Math.Max(1, x.SpanCols), Math.Max(1, x.SpanRows))).ToList(),
                Math.Max(1, s.WindowMode));
        })
        .ToList();

    private static List<FloatingWindowLayout> BuildFloatingLayout(VideoWall wall) => wall.Floating
        .OrderBy(f => f.Id)
        .Select(f => new FloatingWindowLayout(f.DecodeChannel, f.X, f.Y, f.W, f.H))
        .ToList();

    /// <summary>
    /// Empuja al decoder el layout completo del wall (vinculación de salidas,
    /// división de ventanas y canales por ventana).
    /// </summary>
    public async Task<Dictionary<int, OperationResultDto>> SyncToDecoderAsync(int wallId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");

        // Una re-sincronización reconstruye el mosaico, así que cancela cualquier
        // pantalla completa vigente (la ventana vuelve a su sub-celda).
        bool changed = false;
        if (wall.FullscreenWindowId is not null)
        {
            wall.FullscreenWindowId = null;
            changed = true;
        }
        foreach (var s in wall.Screens.Where(s => s.FullscreenWindowId is not null))
        {
            s.FullscreenWindowId = null;
            changed = true;
        }
        if (changed) await _db.SaveChangesAsync();

        // La sincronización manual es la vía de "verificar todo": descarta el
        // caché del driver para que reconcilie contra el estado real del equipo.
        await _sessions.WithDriverAsync(wall.Decoder!, d =>
        {
            d.InvalidateWallCache();
            return Task.CompletedTask;
        });

        return await PushLayoutAsync(wall);
    }

    private async Task<Dictionary<int, OperationResultDto>> PushLayoutAsync(VideoWall wall)
    {
        var results = new Dictionary<int, OperationResultDto>();
        var layout = BuildLayout(wall);
        if (layout.Count == 0) return results;
        var floating = BuildFloatingLayout(wall);

        IReadOnlyDictionary<int, string?> driverResults;
        try
        {
            driverResults = await _sessions.WithDriverAsync(wall.Decoder!, d => d.ConfigureWallAsync(layout, floating));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo sincronizar el wall {Wall} con el decoder", wall.Name);
            foreach (var screen in wall.Screens)
                results[screen.Id] = new OperationResultDto(false, ex.Message);
            return results;
        }

        foreach (var screen in wall.Screens)
        {
            string? error = driverResults.TryGetValue(screen.DisplayChannel, out var e) ? e : null;
            if (error is not null)
                _logger.LogWarning("No se pudo sincronizar la pantalla {Screen} del wall {Wall}: {Error}",
                    screen.Label, wall.Name, error);
            results[screen.Id] = new OperationResultDto(error is null, error);
        }

        // Empujar el mosaico devuelve TODA ventana a su sub-celda, así que
        // vuelve a agrandar las que la base dice que están en pantalla
        // completa. Sin esto el equipo y la base quedan a la deriva: la app
        // muestra el mosaico y el muro sigue con una cámara tapando el
        // monitor, o al revés.
        await ReapplyFullscreenAsync(wall);
        return results;
    }

    /// <summary>
    /// Re-aplica las pantallas completas vigentes según la base. Best-effort:
    /// un fallo se registra pero no invalida la sincronización del mosaico.
    /// </summary>
    private async Task ReapplyFullscreenAsync(VideoWall wall)
    {
        foreach (var screen in wall.Screens.Where(s => s.FullscreenWindowId is not null))
        {
            var window = screen.Windows.FirstOrDefault(x => x.Id == screen.FullscreenWindowId);
            if (window is null) continue;
            try
            {
                await _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowAsync(window.DecodeChannel, true));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo re-aplicar la pantalla completa en {Screen} del wall {Wall}",
                    screen.Label, wall.Name);
            }
        }

        // El muro completo va al final: su ventana debe quedar por encima de
        // cualquier pantalla completa por monitor re-aplicada arriba.
        if (wall.FullscreenWindowId is int wallWinId)
        {
            var window = wall.Screens.SelectMany(s => s.Windows).FirstOrDefault(x => x.Id == wallWinId);
            if (window is null)
            {
                // La ventana ya no existe (cambio de layout la eliminó).
                wall.FullscreenWindowId = null;
                return;
            }
            try
            {
                await _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowToWallAsync(window.DecodeChannel, true));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo re-aplicar el muro completo en el wall {Wall}", wall.Name);
            }
        }
    }

    /// <summary>
    /// Cambia el layout de un monitor (cantidad de ventanas) desde el cliente
    /// de operación, al estilo HikCentral: las ventanas que sobreviven
    /// conservan canal y cámara y el equipo solo las recoloca por roaming (su
    /// video no se corta y el resto del muro no se toca). La estructura del
    /// wall (filas × columnas) no cambia.
    /// </summary>
    public Task<Dictionary<int, OperationResultDto>> ChangeScreenWindowModeAsync(int wallId, int screenId, int windowMode)
    {
        windowMode = Math.Max(1, windowMode);
        // Un cambio manual de layout cancela el modo pantalla completa y
        // preserva las asignaciones que quepan en el nuevo modo.
        return RebuildScreenAsync(wallId, screenId, windowMode, clearFullscreen: true);
    }

    /// <summary>
    /// Pantalla completa instantánea: agranda la ventana de esa cámara para
    /// cubrir todo el monitor, sin tocar el resto de las decodificaciones (las
    /// otras ventanas siguen vivas por debajo). El layout no cambia.
    /// </summary>
    public async Task<Dictionary<int, OperationResultDto>> EnterFullscreenAsync(int wallId, int windowId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var screen = wall.Screens.FirstOrDefault(s => s.Windows.Any(x => x.Id == windowId))
            ?? throw new KeyNotFoundException($"No existe la ventana {windowId} en el wall {wallId}.");
        var window = screen.Windows.First(x => x.Id == windowId);

        if (window.AssignedChannelId is null && window.ExternalUrl is null)
            throw new InvalidOperationException("La ventana no tiene una cámara asignada.");

        // Si había una cámara ocupando el muro completo, primero vuelve a su
        // sub-celda: la pantalla completa por monitor la reemplaza.
        await RestoreWallFullscreenAsync(wall);

        // Si otra ventana de la pantalla estaba en pantalla completa, primero
        // vuelve a su sub-celda (cambiar de cámara sin pasar por "salir").
        if (screen.FullscreenWindowId is int previousId && previousId != windowId)
        {
            var previous = screen.Windows.FirstOrDefault(x => x.Id == previousId);
            if (previous is not null)
            {
                try
                {
                    await _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowAsync(previous.DecodeChannel, false));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo devolver la ventana {Window} a su sub-celda", previousId);
                }
            }
        }

        await WithSyncRetryAsync(wall, () =>
            _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowAsync(window.DecodeChannel, true)));

        screen.FullscreenWindowId = windowId;
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new Dictionary<int, OperationResultDto> { [screen.Id] = new OperationResultDto(true, null) };
    }

    /// <summary>Sale de pantalla completa: devuelve la ventana agrandada a su sub-celda.</summary>
    public async Task<Dictionary<int, OperationResultDto>> ExitFullscreenAsync(int wallId, int screenId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var screen = wall.Screens.FirstOrDefault(s => s.Id == screenId)
            ?? throw new KeyNotFoundException($"No existe la pantalla {screenId} en el wall {wallId}.");

        if (screen.FullscreenWindowId is not int windowId)
        {
            await BroadcastWallStateAsync(wallId);
            return new Dictionary<int, OperationResultDto> { [screenId] = new OperationResultDto(true, null) };
        }

        var window = screen.Windows.FirstOrDefault(x => x.Id == windowId);
        if (window is not null)
            await WithSyncRetryAsync(wall, () =>
                _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowAsync(window.DecodeChannel, false)));

        screen.FullscreenWindowId = null;
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new Dictionary<int, OperationResultDto> { [screenId] = new OperationResultDto(true, null) };
    }

    /// <summary>
    /// Muro completo instantáneo: agranda la ventana de esa cámara para cubrir
    /// TODAS las pantallas del wall como si fueran una sola, sin tocar el resto
    /// de las decodificaciones (siguen vivas por debajo). El layout no cambia.
    /// </summary>
    public async Task<OperationResultDto> EnterWallFullscreenAsync(int wallId, int windowId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var window = wall.Screens.SelectMany(s => s.Windows).FirstOrDefault(x => x.Id == windowId)
            ?? throw new KeyNotFoundException($"No existe la ventana {windowId} en el wall {wallId}.");

        if (window.AssignedChannelId is null && window.ExternalUrl is null)
            throw new InvalidOperationException("La ventana no tiene una cámara asignada.");

        // Si otra ventana ya ocupaba el muro, vuelve a su sub-celda primero
        // (cambiar de cámara sin pasar por "salir").
        if (wall.FullscreenWindowId != windowId)
            await RestoreWallFullscreenAsync(wall);

        await WithSyncRetryAsync(wall, () =>
            _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowToWallAsync(window.DecodeChannel, true)));

        wall.FullscreenWindowId = windowId;
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>Sale del muro completo: la ventana agrandada vuelve a su sub-celda.</summary>
    public async Task<OperationResultDto> ExitWallFullscreenAsync(int wallId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");

        if (wall.FullscreenWindowId is int windowId)
        {
            var window = wall.Screens.SelectMany(s => s.Windows).FirstOrDefault(x => x.Id == windowId);
            if (window is not null)
                await WithSyncRetryAsync(wall, () =>
                    _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowToWallAsync(window.DecodeChannel, false)));

            wall.FullscreenWindowId = null;
            // Las pantallas completas por monitor vigentes vuelven a su capa
            // superior (quedaron por debajo mientras el muro estaba ocupado).
            await ReapplyFullscreenAsync(wall);
        }

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>
    /// Devuelve a su sub-celda la ventana que ocupaba el muro completo (si la
    /// hay) y limpia el estado. Best-effort: se usa antes de operaciones que lo
    /// reemplazan; el caller persiste y difunde.
    /// </summary>
    private async Task RestoreWallFullscreenAsync(VideoWall wall)
    {
        if (wall.FullscreenWindowId is not int windowId) return;

        var window = wall.Screens.SelectMany(s => s.Windows).FirstOrDefault(x => x.Id == windowId);
        if (window is not null)
        {
            try
            {
                await _sessions.WithDriverAsync(wall.Decoder!, d => d.ZoomWindowToWallAsync(window.DecodeChannel, false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo devolver la ventana {Window} del muro completo a su sub-celda", windowId);
            }
        }
        wall.FullscreenWindowId = null;
    }

    /// <summary>
    /// Reconstruye una pantalla a un modo de ventanas dado, al estilo
    /// HikCentral: las primeras ventanas (en orden de lectura) sobreviven con
    /// su canal de decodificación y su cámara — el equipo solo las recoloca
    /// por roaming, sin cortar su video —, las que sobran se detienen y
    /// cierran, y las casillas nuevas nacen como ventanas vacías con canales
    /// libres del pool. Nada se re-decodifica y el resto del muro no se toca.
    /// </summary>
    private async Task<Dictionary<int, OperationResultDto>> RebuildScreenAsync(
        int wallId, int screenId, int windowMode, bool clearFullscreen)
    {
        windowMode = Math.Max(1, windowMode);

        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var screen = wall.Screens.FirstOrDefault(s => s.Id == screenId)
            ?? throw new KeyNotFoundException($"No existe la pantalla {screenId} en el wall {wallId}.");

        if (clearFullscreen)
            screen.FullscreenWindowId = null;

        // Renumeración densa solo DENTRO del monitor; el canal de cada ventana
        // superviviente no se toca (esa identidad es la que permite el roaming).
        // Un cambio de modo disuelve las agrupaciones (span 1×1).
        screen.WindowMode = windowMode;
        var ordered = screen.Windows.OrderBy(x => x.WindowIndex).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].WindowIndex = i;
            ordered[i].SpanCols = 1;
            ordered[i].SpanRows = 1;
        }
        var removedChannels = new List<int>();
        foreach (var extra in ordered.Skip(windowMode))
        {
            if (extra.DecodeChannel > 0) removedChannels.Add(extra.DecodeChannel);
            screen.Windows.Remove(extra);
            _db.ScreenWindows.Remove(extra);
        }
        var newWindows = new List<ScreenWindow>();
        for (int i = ordered.Count; i < windowMode; i++)
        {
            var w = new ScreenWindow { WindowIndex = i, DecodeChannel = 0 };
            newWindows.Add(w);
            screen.Windows.Add(w);
        }

        // Detener y cerrar lo eliminado ANTES de tocar el pool: así esos
        // canales quedan realmente libres y limpios para reutilizarse.
        await ReleaseChannelsAsync(wall.Decoder!, removedChannels);

        // Canales libres para las casillas nuevas (nacen vacías: nada decodifica).
        await AllocateFreeChannelsAsync(wall, newWindows,
            $"El decoder no tiene canales de decodificación libres para poner {windowMode} ventanas en este monitor.");

        // El diff del driver recoloca por roaming las ventanas que siguen,
        // abre las nuevas vacías y no toca las demás pantallas.
        var results = await PushLayoutAsync(wall);

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return results;
    }

    /// <summary>
    /// Agrupa varias ventanas contiguas de un monitor en UNA ventana grande
    /// (layout personalizado). Las ventanas deben formar un rectángulo completo
    /// en la grilla base. Sobrevive la cámara de la ventana superior-izquierda
    /// (o la primera asignada del grupo si aquella estaba vacía); las demás se
    /// liberan.
    /// </summary>
    public async Task<Dictionary<int, OperationResultDto>> GroupWindowsAsync(
        int wallId, int screenId, IReadOnlyList<int> windowIds)
    {
        var distinctIds = (windowIds ?? Array.Empty<int>()).Distinct().ToList();
        if (distinctIds.Count < 2)
            throw new InvalidOperationException("Seleccione al menos dos ventanas para agrupar.");

        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var screen = wall.Screens.FirstOrDefault(s => s.Id == screenId)
            ?? throw new KeyNotFoundException($"No existe la pantalla {screenId} en el wall {wallId}.");

        var selected = screen.Windows.Where(w => distinctIds.Contains(w.Id)).ToList();
        if (selected.Count != distinctIds.Count)
            throw new KeyNotFoundException("Alguna de las ventanas seleccionadas no existe en ese monitor.");
        if (selected.Any(w => w.SpanCols > 1 || w.SpanRows > 1))
            throw new InvalidOperationException("Hay ventanas ya agrupadas en la selección; desagrúpelas primero.");

        int cols = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, screen.WindowMode)));
        var slots = selected.Select(w => w.WindowIndex).ToHashSet();
        int minRow = slots.Min(s => s / cols), maxRow = slots.Max(s => s / cols);
        int minCol = slots.Min(s => s % cols), maxCol = slots.Max(s => s % cols);
        int spanCols = maxCol - minCol + 1, spanRows = maxRow - minRow + 1;
        if (spanCols * spanRows != slots.Count)
            throw new InvalidOperationException("Las ventanas seleccionadas deben formar un rectángulo completo.");
        for (int r = minRow; r <= maxRow; r++)
            for (int c = minCol; c <= maxCol; c++)
                if (!slots.Contains(r * cols + c))
                    throw new InvalidOperationException("Las ventanas seleccionadas deben formar un rectángulo completo.");

        var oldChannels = wall.Screens.SelectMany(s => s.Windows).Select(x => x.DecodeChannel).ToHashSet();

        int anchorSlot = minRow * cols + minCol;
        var anchor = selected.First(w => w.WindowIndex == anchorSlot);
        if (anchor.AssignedChannelId is null && anchor.ExternalUrl is null)
        {
            var donor = selected.OrderBy(w => w.WindowIndex)
                .FirstOrDefault(w => w.AssignedChannelId is not null || w.ExternalUrl is not null);
            if (donor is not null)
            {
                anchor.AssignedChannelId = donor.AssignedChannelId;
                anchor.AssignedStreamType = donor.AssignedStreamType;
                anchor.ExternalUrl = donor.ExternalUrl;
                anchor.ExternalLabel = donor.ExternalLabel;
            }
        }
        foreach (var extra in selected.Where(w => w.Id != anchor.Id).ToList())
        {
            screen.Windows.Remove(extra);
            _db.ScreenWindows.Remove(extra);
        }
        anchor.SpanCols = spanCols;
        anchor.SpanRows = spanRows;
        screen.FullscreenWindowId = null;

        // Sin re-asignar canales: la ventana ancla CRECE por roaming (su video
        // no se corta), las absorbidas se cierran y se liberan sus canales. El
        // resto del muro no se toca.
        var removedChannels = oldChannels
            .Except(wall.Screens.SelectMany(s => s.Windows).Select(x => x.DecodeChannel))
            .ToList();
        var results = await PushLayoutAsync(wall);
        await ReleaseChannelsAsync(wall.Decoder!, removedChannels);

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return results;
    }

    /// <summary>Deshace una agrupación: la ventana vuelve a 1×1 y las casillas liberadas quedan como ventanas vacías.</summary>
    public async Task<Dictionary<int, OperationResultDto>> UngroupWindowAsync(int wallId, int windowId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var screen = wall.Screens.FirstOrDefault(s => s.Windows.Any(w => w.Id == windowId))
            ?? throw new KeyNotFoundException($"No existe la ventana {windowId} en el wall {wallId}.");
        var window = screen.Windows.First(w => w.Id == windowId);

        if (window.SpanCols <= 1 && window.SpanRows <= 1)
            return new Dictionary<int, OperationResultDto> { [screen.Id] = new OperationResultDto(true, null) };

        int cols = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, screen.WindowMode)));
        int baseRow = window.WindowIndex / cols, baseCol = window.WindowIndex % cols;
        for (int r = baseRow; r < baseRow + window.SpanRows; r++)
        {
            for (int c = baseCol; c < baseCol + window.SpanCols; c++)
            {
                int slot = r * cols + c;
                if (slot == window.WindowIndex || slot >= screen.WindowMode) continue;
                screen.Windows.Add(new ScreenWindow { WindowIndex = slot, DecodeChannel = 0 });
            }
        }
        window.SpanCols = 1;
        window.SpanRows = 1;
        screen.FullscreenWindowId = null;

        // Canales libres del rango del decoder para las ventanas nuevas (van
        // vacías, nada se re-decodifica); la ventana agrupada se ENCOGE por
        // roaming con su video intacto.
        await AllocateFreeChannelsAsync(wall,
            screen.Windows.Where(x => x.DecodeChannel == 0).ToList(),
            "El decoder no tiene canales de decodificación libres para desagrupar esta ventana.");

        var results = await PushLayoutAsync(wall);
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return results;
    }

    /// <summary>Rango de canales de decodificación del decoder (o el vigente si no responde).</summary>
    private async Task<(int Start, int? Capacity)> GetChannelRangeAsync(VideoWall wall)
    {
        try
        {
            var caps = await _sessions.WithDriverAsync(wall.Decoder!, d => d.GetCapabilitiesAsync());
            return (caps.DecodeChannelStart, caps.DecodeChannelCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer capacidades del decoder {Decoder}; se usa el rango vigente",
                wall.Decoder!.Name);
            int start = wall.Screens.SelectMany(s => s.Windows)
                .Select(x => x.DecodeChannel).Where(c => c > 0).DefaultIfEmpty(1).Min();
            return (start, null);
        }
    }

    /// <summary>Canales ocupados por las ventanas del mosaico y las flotantes.</summary>
    private static HashSet<int> UsedChannels(VideoWall wall) => wall.Screens
        .SelectMany(s => s.Windows).Select(x => x.DecodeChannel)
        .Concat(wall.Floating.Select(f => f.DecodeChannel))
        .Where(c => c > 0)
        .ToHashSet();

    /// <summary>
    /// Asigna canales libres del rango del decoder a las ventanas nuevas
    /// (DecodeChannel == 0) sin renumerar ninguna existente: así ninguna
    /// ventana viva se re-decodifica.
    /// </summary>
    private async Task AllocateFreeChannelsAsync(VideoWall wall, IReadOnlyList<ScreenWindow> newWindows, string errorMessage)
    {
        var (start, capacity) = await GetChannelRangeAsync(wall);
        var used = UsedChannels(wall);
        int candidate = start;
        foreach (var w in newWindows.OrderBy(x => x.WindowIndex))
        {
            while (used.Contains(candidate)) candidate++;
            if (capacity is int cap && candidate >= start + cap)
                throw new InvalidOperationException(errorMessage);
            w.DecodeChannel = candidate;
            used.Add(candidate);
        }
    }

    /// <summary>Reserva UN canal libre del rango del decoder (ventanas flotantes).</summary>
    private async Task<int> AllocateFreeChannelAsync(VideoWall wall, string errorMessage)
    {
        var (start, capacity) = await GetChannelRangeAsync(wall);
        var used = UsedChannels(wall);
        int candidate = start;
        while (used.Contains(candidate)) candidate++;
        if (capacity is int cap && candidate >= start + cap)
            throw new InvalidOperationException(errorMessage);
        return candidate;
    }

    /// <summary>
    /// Subdivide UNA ventana del mosaico en 4/9/16 sub-ventanas (estilo
    /// HikCentral): la grilla base del monitor se refina ×f en cada eje (los
    /// rects de las demás ventanas no cambian ni un píxel), la ventana elegida
    /// conserva su cámara y se ENCOGE por roaming a la primera sub-celda (su
    /// video no se corta) y las sub-celdas restantes nacen como ventanas vacías
    /// con canales libres.
    /// </summary>
    public async Task<Dictionary<int, OperationResultDto>> SubdivideWindowAsync(int wallId, int windowId, int parts)
    {
        int f = parts switch
        {
            4 => 2,
            9 => 3,
            16 => 4,
            _ => throw new InvalidOperationException("La subdivisión admite 4, 9 o 16 partes."),
        };

        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var screen = wall.Screens.FirstOrDefault(s => s.Windows.Any(x => x.Id == windowId))
            ?? throw new KeyNotFoundException($"No existe la ventana {windowId} en el wall {wallId}.");
        var window = screen.Windows.First(x => x.Id == windowId);

        int mode = Math.Max(1, screen.WindowMode);
        int cols = (int)Math.Ceiling(Math.Sqrt(mode));
        if (cols * cols != mode)
            throw new InvalidOperationException(
                "Para subdividir una ventana, el monitor debe estar en una división cuadrada (1, 4, 9, 16, 25 o 36).");
        int newCols = cols * f;
        if (newCols > 8)
            throw new InvalidOperationException(
                $"La subdivisión pedida deja la grilla del monitor en {newCols}×{newCols} casillas; el máximo es 8×8.");
        int newMode = newCols * newCols;

        // Reindexar todas las ventanas del monitor a la grilla fina. Las que no
        // se subdividen multiplican su span: su rect en el muro queda idéntico.
        foreach (var w in screen.Windows)
        {
            int r = w.WindowIndex / cols, c = w.WindowIndex % cols;
            w.WindowIndex = r * f * newCols + c * f;
            if (w.Id != windowId)
            {
                w.SpanCols = Math.Max(1, w.SpanCols) * f;
                w.SpanRows = Math.Max(1, w.SpanRows) * f;
            }
        }

        // La ventana objetivo conserva su span numérico: en la grilla fina eso
        // es exactamente 1/f² de su región anterior (la sub-celda 1). El resto
        // de la región se llena con f²−1 ventanas nuevas del mismo tamaño.
        int spanCols = Math.Max(1, window.SpanCols), spanRows = Math.Max(1, window.SpanRows);
        int baseRow = window.WindowIndex / newCols, baseCol = window.WindowIndex % newCols;
        var newWindows = new List<ScreenWindow>();
        for (int bi = 0; bi < f; bi++)
        {
            for (int bj = 0; bj < f; bj++)
            {
                if (bi == 0 && bj == 0) continue;
                newWindows.Add(new ScreenWindow
                {
                    WindowIndex = (baseRow + bi * spanRows) * newCols + (baseCol + bj * spanCols),
                    SpanCols = spanCols,
                    SpanRows = spanRows,
                    DecodeChannel = 0,
                });
            }
        }
        foreach (var w in newWindows) screen.Windows.Add(w);
        screen.WindowMode = newMode;
        screen.FullscreenWindowId = null;

        await AllocateFreeChannelsAsync(wall, newWindows,
            "El decoder no tiene canales de decodificación libres para subdividir esta ventana.");

        var results = await PushLayoutAsync(wall);
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return results;
    }

    // -----------------------------------------------------------------------
    // Ventanas flotantes: rect libre dibujado ENCIMA del mosaico
    // -----------------------------------------------------------------------

    /// <summary>Encaja el rect pedido dentro del muro (unidades de celda).</summary>
    private static (double X, double Y, double W, double H) ClampFloatingRect(
        VideoWall wall, double x, double y, double w, double h)
    {
        double cols = Math.Max(1, wall.Columns), rows = Math.Max(1, wall.Rows);
        w = Math.Clamp(w, 0.1, cols);
        h = Math.Clamp(h, 0.1, rows);
        x = Math.Clamp(x, 0, cols - w);
        y = Math.Clamp(y, 0, rows - h);
        return (x, y, w, h);
    }

    /// <summary>
    /// Crea una ventana flotante (dibujada por el operador sobre el muro) con
    /// su cámara: abre la ventana en la capa superior y comienza a decodificar.
    /// </summary>
    public async Task<OperationResultDto> CreateFloatingAsync(int wallId, FloatingCreateRequest request)
    {
        var camera = await LoadChannelAsync(request.ChannelId);
        return await CreateFloatingCoreAsync(wallId, request.X, request.Y, request.W, request.H,
            floating =>
            {
                floating.AssignedChannelId = camera.Id;
                floating.AssignedStreamType = request.StreamType;
            },
            () => BuildStreamSource(camera, request.StreamType));
    }

    /// <summary>Ventana flotante que muestra una fuente externa por URL (proyección).</summary>
    public Task<OperationResultDto> CreateFloatingExternalAsync(int wallId, FloatingExternalCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return Task.FromResult(new OperationResultDto(false, "La URL de la fuente externa es obligatoria."));

        return CreateFloatingCoreAsync(wallId, request.X, request.Y, request.W, request.H,
            floating =>
            {
                floating.ExternalUrl = request.Url;
                floating.ExternalLabel = string.IsNullOrWhiteSpace(request.Label) ? "Proyección" : request.Label.Trim();
            },
            () => ExternalStream(request.Url));
    }

    /// <summary>
    /// Cuerpo común de la creación de una flotante: reserva un canal de
    /// decodificación, abre la ventana en la capa superior del muro y arranca
    /// el video. Si algo falla, la ventana se cierra en el equipo y no queda
    /// nada persistido.
    /// </summary>
    private async Task<OperationResultDto> CreateFloatingCoreAsync(int wallId, double rx, double ry, double rw, double rh,
        Action<WallFloatingWindow> applySource, Func<StreamSource> buildStream)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");

        var (x, y, w, h) = ClampFloatingRect(wall, rx, ry, rw, rh);
        int channel = await AllocateFreeChannelAsync(wall,
            "El decoder no tiene canales de decodificación libres para abrir una ventana flotante.");

        var floating = new WallFloatingWindow
        {
            VideoWallId = wall.Id,
            X = x, Y = y, W = w, H = h,
            DecodeChannel = channel,
        };
        applySource(floating);
        wall.Floating.Add(floating);

        try
        {
            await PushLayoutAsync(wall); // abre la ventana en el muro (capa superior)
            var stream = buildStream();
            await WithSyncRetryAsync(wall, () =>
                _sessions.WithDriverAsync(wall.Decoder!, d => d.StartDecodingAsync(channel, stream)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo abriendo la ventana flotante en el wall {Wall}", wall.Name);
            wall.Floating.Remove(floating);
            try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.CloseWindowAsync(channel)); }
            catch (Exception cleanup) { _logger.LogDebug(cleanup, "CloseWindow al revertir la flotante fallida"); }
            return new OperationResultDto(false, ex.Message);
        }

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>
    /// Mueve o redimensiona una ventana flotante. La ventana se recoloca por
    /// roaming: su video no se corta mientras se arrastra por el muro.
    /// </summary>
    public async Task<OperationResultDto> MoveFloatingAsync(int wallId, int floatingId, FloatingMoveRequest request)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var floating = wall.Floating.FirstOrDefault(f => f.Id == floatingId)
            ?? throw new KeyNotFoundException($"No existe la ventana flotante {floatingId} en el wall {wallId}.");

        (floating.X, floating.Y, floating.W, floating.H) =
            ClampFloatingRect(wall, request.X, request.Y, request.W, request.H);
        // Mover o redimensionar a mano cancela la pantalla completa: el rect
        // que fija el operador pasa a ser el vigente, no una posición temporal.
        floating.HomeX = floating.HomeY = floating.HomeW = floating.HomeH = null;

        var results = await PushLayoutAsync(wall);
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        string? error = results.Values.Select(r => r.Error).FirstOrDefault(e => e is not null);
        return new OperationResultDto(error is null, error);
    }

    /// <summary>
    /// Doble clic en una flotante: la agranda hasta cubrir TODAS las pantallas
    /// sobre las que está presente (el bounding box de las celdas que toca) y,
    /// al repetir, la devuelve exactamente a su rect original. Igual que el
    /// arrastre, es roaming: su video no se corta.
    /// </summary>
    public async Task<OperationResultDto> ToggleFloatingFullscreenAsync(int wallId, int floatingId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var floating = wall.Floating.FirstOrDefault(f => f.Id == floatingId)
            ?? throw new KeyNotFoundException($"No existe la ventana flotante {floatingId} en el wall {wallId}.");

        if (floating is { HomeX: double hx, HomeY: double hy, HomeW: double hw, HomeH: double hh })
        {
            // Volver: restaurar el rect guardado antes de agrandar.
            (floating.X, floating.Y, floating.W, floating.H) = ClampFloatingRect(wall, hx, hy, hw, hh);
            floating.HomeX = floating.HomeY = floating.HomeW = floating.HomeH = null;
        }
        else
        {
            // Celdas del muro que la flotante toca. Un roce menor al 2% de una
            // celda no cuenta como "estar presente" en esa pantalla: al
            // arrastrar es fácil quedar apenas montado sobre la vecina.
            const double edge = 0.02;
            int c1 = (int)Math.Clamp(Math.Floor(floating.X + edge), 0, wall.Columns - 1);
            int r1 = (int)Math.Clamp(Math.Floor(floating.Y + edge), 0, wall.Rows - 1);
            int c2 = (int)Math.Clamp(Math.Ceiling(floating.X + floating.W - edge) - 1, c1, wall.Columns - 1);
            int r2 = (int)Math.Clamp(Math.Ceiling(floating.Y + floating.H - edge) - 1, r1, wall.Rows - 1);

            (floating.HomeX, floating.HomeY, floating.HomeW, floating.HomeH) =
                (floating.X, floating.Y, floating.W, floating.H);
            (floating.X, floating.Y, floating.W, floating.H) = (c1, r1, c2 - c1 + 1.0, r2 - r1 + 1.0);
        }

        var results = await PushLayoutAsync(wall);
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        string? error = results.Values.Select(r => r.Error).FirstOrDefault(e => e is not null);
        return new OperationResultDto(error is null, error);
    }

    /// <summary>Cambia la cámara de una ventana flotante (drop de un canal encima).</summary>
    public async Task<OperationResultDto> AssignFloatingAsync(int wallId, int floatingId, FloatingAssignRequest request)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var floating = wall.Floating.FirstOrDefault(f => f.Id == floatingId)
            ?? throw new KeyNotFoundException($"No existe la ventana flotante {floatingId} en el wall {wallId}.");
        var camera = await LoadChannelAsync(request.ChannelId);

        try
        {
            var stream = BuildStreamSource(camera, request.StreamType);
            await WithSyncRetryAsync(wall, () =>
                _sessions.WithDriverAsync(wall.Decoder!, d => d.StartDecodingAsync(floating.DecodeChannel, stream)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo asignando el canal {Channel} a la ventana flotante {Floating}",
                camera.Name, floatingId);
            return new OperationResultDto(false, ex.Message);
        }

        floating.AssignedChannelId = camera.Id;
        floating.AssignedStreamType = request.StreamType;
        floating.ExternalUrl = null;
        floating.ExternalLabel = null;
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>Pone una fuente externa por URL (proyección) en una ventana flotante existente.</summary>
    public async Task<OperationResultDto> AssignFloatingExternalAsync(int wallId, int floatingId,
        FloatingExternalAssignRequest request)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var floating = wall.Floating.FirstOrDefault(f => f.Id == floatingId)
            ?? throw new KeyNotFoundException($"No existe la ventana flotante {floatingId} en el wall {wallId}.");
        if (string.IsNullOrWhiteSpace(request.Url))
            return new OperationResultDto(false, "La URL de la fuente externa es obligatoria.");

        try
        {
            await WithSyncRetryAsync(wall, () => _sessions.WithDriverAsync(wall.Decoder!,
                d => d.StartDecodingAsync(floating.DecodeChannel, ExternalStream(request.Url))));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo proyectando {Label} en la ventana flotante {Floating}", request.Label, floatingId);
            return new OperationResultDto(false, ex.Message);
        }

        floating.AssignedChannelId = null;
        floating.AssignedStreamType = 0;
        floating.ExternalUrl = request.Url;
        floating.ExternalLabel = string.IsNullOrWhiteSpace(request.Label) ? "Proyección" : request.Label.Trim();
        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>Cierra una ventana flotante: detiene su video y la quita del muro.</summary>
    public async Task<OperationResultDto> DeleteFloatingAsync(int wallId, int floatingId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var floating = wall.Floating.FirstOrDefault(f => f.Id == floatingId)
            ?? throw new KeyNotFoundException($"No existe la ventana flotante {floatingId} en el wall {wallId}.");

        int channel = floating.DecodeChannel;
        wall.Floating.Remove(floating);
        _db.WallFloatingWindows.Remove(floating);

        try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(channel)); }
        catch (Exception ex) { _logger.LogDebug(ex, "StopDecoding de la flotante {Floating}", floatingId); }
        try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.CloseWindowAsync(channel)); }
        catch (Exception ex) { _logger.LogDebug(ex, "CloseWindow de la flotante {Floating}", floatingId); }
        // Si la sesión del driver era nueva y no conocía la ventana, la
        // sincronización la reconcilia (queda como sobrante y se cierra).
        await PushLayoutAsync(wall);

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wallId);
        return new OperationResultDto(true, null);
    }

    /// <summary>
    /// Aplica un layout guardado reconstruyendo el wall completo: restaura el
    /// modo de ventanas de cada monitor, reasigna las cámaras por posición
    /// estable (fila/columna/índice), empuja el mosaico y decodifica todo.
    /// </summary>
    public async Task<Dictionary<int, OperationResultDto>> ApplyLayoutAsync(int wallId, int layoutId)
    {
        var wall = await WallsWithState.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new KeyNotFoundException($"No existe el wall {wallId}.");
        var layout = await _db.WallLayouts.Include(l => l.Items).Include(l => l.Screens)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.Id == layoutId && l.VideoWallId == wallId)
            ?? throw new KeyNotFoundException($"No existe el layout {layoutId} en el wall {wallId}.");

        var modeByPos = layout.Screens.ToDictionary(s => (s.Row, s.Col), s => Math.Max(1, s.WindowMode));
        var asgByKey = layout.Items.ToDictionary(
            i => (i.Row, i.Col, i.WindowIndex), i => (i.ChannelId, i.StreamType));

        var oldChannels = wall.Screens.SelectMany(s => s.Windows).Select(x => x.DecodeChannel).ToHashSet();

        // Foto de las asignaciones vigentes ANTES de mutar: permite decodificar
        // después solo lo que realmente cambió de cámara (el resto sigue vivo y
        // el equipo solo lo recoloca por roaming).
        var before = wall.Screens.SelectMany(s => s.Windows)
            .Where(w => w.AssignedChannelId is not null || w.ExternalUrl is not null)
            .ToDictionary(w => w, w => (ChannelId: w.AssignedChannelId ?? 0, w.ExternalUrl, w.AssignedStreamType));

        // 1) Restaurar modo de ventanas y asignaciones de cada monitor. Se
        //    parte de una grilla densa 1×1 y luego se re-crean las agrupaciones
        //    guardadas en el layout.
        wall.FullscreenWindowId = null; // aplicar un layout cancela el muro completo
        foreach (var screen in wall.Screens)
        {
            screen.FullscreenWindowId = null;
            int mode = modeByPos.TryGetValue((screen.Row, screen.Col), out var m) ? m : screen.WindowMode;
            screen.WindowMode = mode;

            var ordered = screen.Windows.OrderBy(x => x.WindowIndex).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].WindowIndex = i;
                ordered[i].SpanCols = 1;
                ordered[i].SpanRows = 1;
            }
            foreach (var extra in ordered.Skip(mode))
            {
                screen.Windows.Remove(extra);
                _db.ScreenWindows.Remove(extra);
            }
            for (int i = ordered.Count; i < mode; i++)
                screen.Windows.Add(new ScreenWindow { WindowIndex = i, DecodeChannel = 0 });

            foreach (var w in screen.Windows)
            {
                if (asgByKey.TryGetValue((screen.Row, screen.Col, w.WindowIndex), out var a))
                {
                    w.AssignedChannelId = a.ChannelId;
                    w.AssignedStreamType = a.StreamType;
                    w.ExternalUrl = null;
                    w.ExternalLabel = null;
                }
                else
                {
                    w.AssignedChannelId = null;
                    w.AssignedStreamType = 0;
                    w.ExternalUrl = null;
                    w.ExternalLabel = null;
                }
            }

            // Agrupaciones del layout: eliminar las ventanas cubiertas y dar el
            // span al ancla (solo las ventanas con cámara guardan su span).
            int cols = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, mode)));
            foreach (var item in layout.Items.Where(i =>
                         i.Row == screen.Row && i.Col == screen.Col && (i.SpanCols > 1 || i.SpanRows > 1)))
            {
                var anchor = screen.Windows.FirstOrDefault(w => w.WindowIndex == item.WindowIndex);
                if (anchor is null || anchor.SpanCols > 1 || anchor.SpanRows > 1) continue;
                int baseRow = item.WindowIndex / cols, baseCol = item.WindowIndex % cols;
                for (int r = baseRow; r < baseRow + item.SpanRows; r++)
                {
                    for (int c = baseCol; c < baseCol + item.SpanCols && c < cols; c++)
                    {
                        int slot = r * cols + c;
                        if (slot == item.WindowIndex) continue;
                        var covered = screen.Windows.FirstOrDefault(w => w.WindowIndex == slot);
                        if (covered is not null)
                        {
                            screen.Windows.Remove(covered);
                            _db.ScreenWindows.Remove(covered);
                        }
                    }
                }
                anchor.SpanCols = Math.Max(1, item.SpanCols);
                anchor.SpanRows = Math.Max(1, item.SpanRows);
            }
        }

        var results = await FinalizeWallRebuildAsync(wall, oldChannels, before);
        return results;
    }

    /// <summary>
    /// Cierre común de una reconstrucción de wall, al estilo HikCentral: las
    /// ventanas que sobreviven CONSERVAN su canal de decodificación (el equipo
    /// solo las recoloca por roaming, sin cortar su video), las nuevas reciben
    /// canales libres del pool y solo se (re)decodifica lo que cambió de
    /// cámara respecto de <paramref name="before"/> (null = decodificar todas
    /// las asignadas). Si una decodificación falla, la asignación SE CONSERVA
    /// en la base — el operador la reintenta con "Sincronizar" — y el error se
    /// reporta en el resultado de su pantalla.
    /// </summary>
    private async Task<Dictionary<int, OperationResultDto>> FinalizeWallRebuildAsync(
        VideoWall wall, HashSet<int> oldChannels,
        IReadOnlyDictionary<ScreenWindow, (int ChannelId, string? ExternalUrl, int StreamType)>? before)
    {
        // Detener y cerrar lo eliminado ANTES de tocar el pool: así esos
        // canales quedan realmente libres y limpios para reutilizarse.
        var keptChannels = wall.Screens.SelectMany(s => s.Windows)
            .Select(x => x.DecodeChannel).Where(c => c > 0).ToHashSet();
        await ReleaseChannelsAsync(wall.Decoder!, oldChannels.Except(keptChannels));

        var newWindows = wall.Screens.SelectMany(s => s.Windows)
            .Where(x => x.DecodeChannel == 0)
            .ToList();
        await AllocateFreeChannelsAsync(wall, newWindows,
            "El decoder no tiene canales de decodificación libres para aplicar este layout.");

        // El diff del driver recoloca por roaming lo que cambió de rect, abre
        // las ventanas nuevas y no toca las que quedaron igual.
        var results = await PushLayoutAsync(wall);

        // Ventanas que tenían cámara y quedaron vacías: detener su canal (la
        // ventana sigue abierta, en negro, como cualquier casilla vacía).
        if (before is not null)
        {
            foreach (var w in wall.Screens.SelectMany(s => s.Windows)
                         .Where(w => w.AssignedChannelId is null && w.ExternalUrl is null && before.ContainsKey(w)))
            {
                try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(w.DecodeChannel)); }
                catch (Exception ex) { _logger.LogDebug(ex, "StopDecoding de la ventana vaciada {Window}", w.Id); }
            }
        }

        // (Re)decodificar SOLO las ventanas nuevas o cuya cámara cambió.
        var channelIds = wall.Screens.SelectMany(s => s.Windows)
            .Where(w => w.AssignedChannelId is not null)
            .Select(w => w.AssignedChannelId!.Value).Distinct().ToList();
        var cameras = await _db.Channels.Include(c => c.Device)
            .Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);

        foreach (var screen in wall.Screens)
        {
            int failures = 0;
            string? firstError = null;
            foreach (var w in screen.Windows.OrderBy(x => x.WindowIndex))
            {
                if (w.AssignedChannelId is null && w.ExternalUrl is null) continue;
                bool unchanged = before is not null &&
                    before.TryGetValue(w, out var prev) &&
                    prev == (w.AssignedChannelId ?? 0, w.ExternalUrl, w.AssignedStreamType);
                if (unchanged) continue; // sigue decodificando; el roaming ya la recolocó

                Channel? camera = null;
                if (w.ExternalUrl is null &&
                    (w.AssignedChannelId is not int camId || !cameras.TryGetValue(camId, out camera)))
                {
                    failures++;
                    firstError ??= "El canal asignado ya no existe en el inventario.";
                    continue;
                }

                // Al abrir muchas sesiones de golpe, un DVR puede rebotar
                // alguna (límite de vistas en vivo por canal): reintentar con
                // una pausa suele bastar para que la sesión se libere.
                Exception? lastError = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0) await Task.Delay(2000);
                    try
                    {
                        var stream = w.ExternalUrl is { } externalUrl
                            ? ExternalStream(externalUrl)
                            : BuildStreamSource(camera!, w.AssignedStreamType);
                        await WithSyncRetryAsync(wall, () =>
                            _sessions.WithDriverAsync(wall.Decoder!, d => d.StartDecodingAsync(w.DecodeChannel, stream)));
                        lastError = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }
                if (lastError is not null)
                {
                    _logger.LogWarning(lastError, "No se pudo decodificar la ventana {Window} al aplicar el layout", w.Id);
                    failures++;
                    firstError ??= lastError.Message;
                }
            }
            if (failures > 0)
                results[screen.Id] = new OperationResultDto(false,
                    $"{failures} cámara(s) no pudieron decodificarse ({firstError}); reasigne esas cámaras para reintentar.");
        }

        await _db.SaveChangesAsync();
        await BroadcastWallStateAsync(wall.Id);
        return results;
    }

    /// <summary>
    /// Detiene la decodificación y cierra la ventana (en equipos de familia
    /// video wall) de canales que dejaron de usarse. Best-effort: los fallos
    /// se registran pero no interrumpen la operación.
    /// </summary>
    public async Task ReleaseChannelsAsync(Decoder decoder, IEnumerable<int> channels)
    {
        foreach (int channel in channels)
        {
            try { await _sessions.WithDriverAsync(decoder, d => d.StopDecodingAsync(channel)); }
            catch (Exception ex) { _logger.LogDebug(ex, "StopDecoding del canal liberado {Channel} falló", channel); }
            try { await _sessions.WithDriverAsync(decoder, d => d.CloseWindowAsync(channel)); }
            catch (Exception ex) { _logger.LogDebug(ex, "CloseWindow del canal liberado {Channel} falló", channel); }
        }
    }

    /// <summary>
    /// Libera del muro las ventanas que muestran canales de un dispositivo que
    /// está por eliminarse. Sin esto el borrado en cascada dejaría la ventana
    /// vacía en la base pero el decodificador seguiría pintando esa cámara.
    /// Best-effort: un decodificador caído no impide eliminar el dispositivo.
    /// </summary>
    public async Task ReleaseDeviceAsync(int deviceId)
    {
        var channelIds = await _db.Channels.Where(c => c.DeviceId == deviceId).Select(c => c.Id).ToListAsync();
        if (channelIds.Count == 0) return;

        var walls = await WallsWithState.ToListAsync();
        foreach (var wall in walls)
        {
            bool touched = false;

            foreach (var window in wall.Screens.SelectMany(s => s.Windows)
                         .Where(x => x.AssignedChannelId is int id && channelIds.Contains(id)))
            {
                try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(window.DecodeChannel)); }
                catch (Exception ex) { _logger.LogDebug(ex, "StopDecoding al eliminar el dispositivo {Device}", deviceId); }
                window.AssignedChannelId = null;
                window.AssignedStreamType = 0;
                touched = true;
            }

            // Una flotante sin cámara no tiene razón de existir: se cierra.
            foreach (var floating in wall.Floating
                         .Where(f => f.AssignedChannelId is int id && channelIds.Contains(id)).ToList())
            {
                try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.StopDecodingAsync(floating.DecodeChannel)); }
                catch (Exception ex) { _logger.LogDebug(ex, "StopDecoding de la flotante {Floating}", floating.Id); }
                try { await _sessions.WithDriverAsync(wall.Decoder!, d => d.CloseWindowAsync(floating.DecodeChannel)); }
                catch (Exception ex) { _logger.LogDebug(ex, "CloseWindow de la flotante {Floating}", floating.Id); }
                wall.Floating.Remove(floating);
                _db.WallFloatingWindows.Remove(floating);
                touched = true;
            }

            if (!touched) continue;
            await _db.SaveChangesAsync();
            await BroadcastWallStateAsync(wall.Id);
        }
    }

    public async Task BroadcastWallStateAsync(int wallId)
    {
        var dto = await GetWallDtoAsync(wallId);
        if (dto is not null)
            await _hub.Clients.All.SendAsync(VmsHubContract.WallStateChanged, dto);
    }

    public Task BroadcastConfigChangedAsync() =>
        _hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "walls");
}
