using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Api;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;
using TrueCentralVms.Server.Services.Workflows;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Eventos en vivo para el Cuadro vigía: lo que pasa (alarmas, accesos,
/// analíticas, patentes, cerco, citofonía, avisos), ya resuelto a su recurso,
/// su ubicación y sus cámaras, para que el cuadro cambie solo a la cámara.
///
/// Escucha lo que ya llega (no abre suscripciones nuevas a los equipos): los
/// disparos de las automatizaciones (<see cref="WorkflowEngine.Publish"/>, que
/// recibe alarmas, accesos, video, patentes y E/S), el cerco, la citofonía y
/// los avisos de la acción «Avisar». <see cref="Offer"/> no bloquea a quien
/// llama: deja el evento en una cola acotada y el resto se hace aquí.
///
/// Antirrebote: un evento por categoría y recurso cada 10 s (movimiento, cada
/// 30 s); los repetidos vuelven a salir con el mismo Id y el contador subido.
/// No guarda nada en la base: cada módulo tiene su historial. Los últimos 300
/// quedan en memoria para que un puesto que conecta (o reconecta) se ponga al día.
///
/// Cada usuario recibe solo lo suyo: con permiso de vista en vivo, con el
/// permiso del tipo de evento (monitorear accesos para un acceso denegado…),
/// si ve el recurso de origen, y con las cámaras recortadas a las que ve.
/// </summary>
public sealed class LiveEventFeed(IServiceScopeFactory scopes, ScopedHub hub, ILogger<LiveEventFeed> logger)
    : BackgroundService
{
    private const int RecentCapacity = 300;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MotionDebounce = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>Lo que llega, sin resolver todavía.</summary>
    private sealed record Raw(string Category, AlarmSeverity Severity, string Title, string? Detail, DateTime AtUtc,
        ResourceBriefings.EventRef Ref, IReadOnlyList<int>? Channels = null, string? ResourceKey = null,
        long? AlertId = null, string? Recipients = null, string? DebounceKey = null);

    /// <summary>Un evento emitido, con lo necesario para decidir a quién va.</summary>
    private sealed class Item(LiveEventDto dto, string? recipients)
    {
        public LiveEventDto Dto { get; set; } = dto;
        public string? Recipients { get; } = recipients;
        public DateTime LastAt { get; set; } = DateTime.UtcNow;
    }

    private readonly System.Threading.Channels.Channel<Raw> _incoming = System.Threading.Channels.Channel.CreateBounded<Raw>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Lock _sync = new();
    private readonly LinkedList<Item> _recent = new();
    private readonly Dictionary<string, Item> _byDebounceKey = new();
    private readonly Dictionary<string, (DateTime At, Resolved? Value)> _resolveCache = new();
    private (DateTime At, Dictionary<int, int?> Parents)? _locations;
    private long _sequence;

    private sealed record Resolved(string? Key, string? Name, string? Path, IReadOnlyList<int> LocationIds,
        IReadOnlyList<int> Cameras);

    // ------------------------------------------------------------------
    // Entradas
    // ------------------------------------------------------------------

    /// <summary>Un disparo de automatización (se llama desde <see cref="WorkflowEngine.Publish"/>).</summary>
    public void Offer(WorkflowTrigger t)
    {
        try
        {
            if (Classify(t) is { } raw) _incoming.Writer.TryWrite(raw);
        }
        catch (Exception ex)
        {
            // El vigía nunca debe frenar a las automatizaciones.
            logger.LogDebug(ex, "No se pudo clasificar el evento '{Summary}' para el vigía.", t.Summary);
        }
    }

    /// <summary>Un evento de cerco eléctrico (solo las alarmas).</summary>
    public void OfferCerco(CercoEvent e)
    {
        if (e.Severity != CercoSeverity.Critical) return;
        _incoming.Writer.TryWrite(new Raw(LiveEventCategories.Fence, AlarmSeverity.Critical, e.Description,
            e.ZoneName ?? (e.ZoneNumber is { } z ? $"Zona {z}" : null), DateTime.UtcNow,
            new ResourceBriefings.EventRef(CercoPanelId: e.CercoPanelId)));
    }

    /// <summary>Una llamada de citofonía que empieza a sonar.</summary>
    public void OfferIntercomCall(int intercomId, string intercomName, int? channelId) =>
        _incoming.Writer.TryWrite(new Raw(LiveEventCategories.Intercom, AlarmSeverity.Warning, "Llamada de citofonía",
            intercomName, DateTime.UtcNow, new ResourceBriefings.EventRef(IntercomId: intercomId),
            Channels: channelId is { } c ? [c] : null));

    /// <summary>Un aviso de la acción «Avisar» (ya trae sus cámaras y su recurso).</summary>
    public void OfferAlert(WorkflowAlert alert, IReadOnlyList<int> channels) =>
        _incoming.Writer.TryWrite(new Raw(LiveEventCategories.Alert, alert.Severity, alert.Title, alert.Message,
            alert.RaisedAt, new ResourceBriefings.EventRef(), Channels: channels, ResourceKey: alert.ResourceKey,
            AlertId: alert.Id, Recipients: alert.RecipientUserIds, DebounceKey: $"alert:{alert.Id}"));

    /// <summary>Lo que el disparo dice del evento, o null si no le interesa al vigía.</summary>
    private static Raw? Classify(WorkflowTrigger t)
    {
        DateTime at = t.At.Kind == DateTimeKind.Utc ? t.At : t.At.ToUniversalTime();
        string? detail = t.Text;
        switch (t.Type)
        {
            case WorkflowTriggerTypes.AlarmEvent when t.PanelId is { } panel:
            {
                string? category = t.Kind switch
                {
                    AlarmEventKind.Alarm or AlarmEventKind.ZoneTriggered => LiveEventCategories.Alarm,
                    AlarmEventKind.Arm or AlarmEventKind.Disarm or AlarmEventKind.Trouble or AlarmEventKind.Bypass
                        => LiveEventCategories.AlarmSystem,
                    _ => null,
                };
                if (category is null) return null;
                var severity = category == LiveEventCategories.Alarm ? AlarmSeverity.Critical
                    : t.Kind == AlarmEventKind.Trouble ? AlarmSeverity.Warning : AlarmSeverity.Info;
                string title = t.Kind is { } kind ? WorkflowTrigger.KindLabel(kind) : "Evento de alarma";
                return new Raw(category, severity, title, Field(t, "zona") ?? detail, at,
                    new ResourceBriefings.EventRef(AlarmPanelId: panel, AreaNumber: t.AreaNumber, ZoneNumber: t.ZoneNumber));
            }
            case WorkflowTriggerTypes.AccessEvent when t.AccessDeviceId is { } device:
            {
                string? category = t.AccessKind switch
                {
                    AccessEventKind.Denied => LiveEventCategories.AccessDenied,
                    AccessEventKind.Granted => LiveEventCategories.AccessGranted,
                    AccessEventKind.Alarm => LiveEventCategories.DoorAlarm,
                    _ => null,
                };
                if (category is null) return null;
                var severity = category switch
                {
                    LiveEventCategories.DoorAlarm => AlarmSeverity.Critical,
                    LiveEventCategories.AccessDenied => AlarmSeverity.Warning,
                    _ => AlarmSeverity.Info,
                };
                string who = Field(t, "persona") ?? (Field(t, "tarjeta") is { } card ? $"tarjeta {card}" : "");
                string credential = Field(t, "credencial") ?? "";
                string person = string.Join(" · ", new[] { who, credential }.Where(x => x.Length > 0));
                return new Raw(category, severity, t.AccessKind is { } k ? WorkflowTrigger.AccessKindLabel(k) : "Acceso",
                    person.Length > 0 ? person : detail, at,
                    new ResourceBriefings.EventRef(AccessDeviceId: device, DoorNumber: t.DoorNumber));
            }
            case WorkflowTriggerTypes.VideoEvent when t.ChannelId is { } channel && t.VideoKind is { } video:
            {
                string? category = video switch
                {
                    VideoEventKind.Motion => LiveEventCategories.VideoMotion,
                    VideoEventKind.VideoLoss or VideoEventKind.Tamper or VideoEventKind.VideoException
                        => LiveEventCategories.VideoHealth,
                    VideoEventKind.DeviceFault or VideoEventKind.AudioException or VideoEventKind.PeopleCounting => null,
                    _ => LiveEventCategories.VideoAnalytics,
                };
                if (category is null) return null;
                var severity = category switch
                {
                    LiveEventCategories.VideoAnalytics => AlarmSeverity.Critical,
                    LiveEventCategories.VideoHealth => AlarmSeverity.Warning,
                    _ => AlarmSeverity.Info,
                };
                return new Raw(category, severity, WorkflowTrigger.VideoKindLabel(video), Field(t, "regla") ?? detail, at,
                    new ResourceBriefings.EventRef(ChannelId: channel), Channels: [channel]);
            }
            case WorkflowTriggerTypes.PlateRecognized when t.ChannelId is { } channel:
            {
                bool flagged = t.PlateStatus is PlateRegistryStatuses.Unauthorized or PlateRegistryStatuses.Unregistered;
                return new Raw(flagged ? LiveEventCategories.PlateFlagged : LiveEventCategories.Plate,
                    flagged ? AlarmSeverity.Warning : AlarmSeverity.Info, $"Patente {t.Plate}",
                    PlateRegistryStatuses.Label(t.PlateStatus), at,
                    new ResourceBriefings.EventRef(ChannelId: channel), Channels: [channel],
                    DebounceKey: $"plate:{t.Plate}:{channel}");
            }
            case WorkflowTriggerTypes.IoInput when t.IoModuleId is { } module && t.IoActive == true:
                return new Raw(LiveEventCategories.IoInput, AlarmSeverity.Warning,
                    Field(t, "entrada") is { } input ? $"Entrada activa: {input}" : "Entrada activa", Field(t, "modulo"), at,
                    new ResourceBriefings.EventRef(IoModuleId: module), DebounceKey: $"io:{module}:{t.IoInput}");
            default:
                return null;
        }
    }

    private static string? Field(WorkflowTrigger t, string key) =>
        t.Fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    // ------------------------------------------------------------------
    // Proceso
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var raw in _incoming.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await HandleAsync(raw, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Un evento que no se pudo resolver no detiene a los siguientes.
                logger.LogWarning(ex, "No se pudo publicar el evento en vivo '{Title}'.", raw.Title);
            }
        }
    }

    private async Task HandleAsync(Raw raw, CancellationToken ct)
    {
        var resolved = await ResolveAsync(raw, ct);
        string resourceKey = raw.ResourceKey ?? resolved?.Key
                             ?? (raw.Channels is { Count: > 0 } c ? $"Camera:{c[0]}" : raw.Title);
        string debounceKey = raw.DebounceKey ?? $"{raw.Category}:{resourceKey}";
        var window = raw.Category == LiveEventCategories.VideoMotion ? MotionDebounce : Debounce;

        Item item;
        lock (_sync)
        {
            // Repetido dentro de la ventana: el mismo evento con el contador subido.
            if (_byDebounceKey.TryGetValue(debounceKey, out var existing) && DateTime.UtcNow - existing.LastAt < window)
            {
                existing.LastAt = DateTime.UtcNow;
                existing.Dto = existing.Dto with { Repeats = existing.Dto.Repeats + 1, At = raw.AtUtc };
                item = existing;
            }
            else
            {
                // Cámaras: las que trae el evento primero y luego las del recurso, sin repetir.
                var cameras = (raw.Channels ?? []).Concat(resolved?.Cameras ?? []).Distinct().ToList();
                var dto = new LiveEventDto($"le{++_sequence}", raw.AtUtc, raw.Category, raw.Severity, raw.Title,
                    raw.Detail, raw.ResourceKey ?? resolved?.Key, resolved?.Name, resolved?.Path,
                    resolved?.LocationIds ?? [], cameras, raw.AlertId);
                item = new Item(dto, raw.Recipients);
                _byDebounceKey[debounceKey] = item;
                _recent.AddFirst(item);
                while (_recent.Count > RecentCapacity)
                {
                    var last = _recent.Last!.Value;
                    _recent.RemoveLast();
                    foreach (var key in _byDebounceKey.Where(p => ReferenceEquals(p.Value, last)).Select(p => p.Key).ToList())
                        _byDebounceKey.Remove(key);
                }
            }
        }
        var snapshot = item.Dto;
        await hub.SendEachAsync(VmsHubContract.LiveEvent, snapshot, (scope, dto) => For(scope, dto, item.Recipients), ct);
    }

    /// <summary>Los últimos eventos que este usuario puede ver (el más reciente primero).</summary>
    public IReadOnlyList<LiveEventDto> Recent(UserScope scope, int take)
    {
        List<Item> items;
        lock (_sync) items = _recent.Take(Math.Clamp(take, 1, RecentCapacity)).ToList();
        if (!scope.Has(Permissions.LiveView)) return [];
        return items.Select(i => For(scope, i.Dto, i.Recipients)).OfType<LiveEventDto>().ToList();
    }

    /// <summary>La versión del evento para un usuario, o null si no le corresponde.</summary>
    private static LiveEventDto? For(UserScope scope, LiveEventDto dto, string? recipients)
    {
        // Avisos: los mismos destinatarios que la ventana de alarma.
        if (dto.Category == LiveEventCategories.Alert)
        {
            if (!WorkflowsApi.ReceivesAlert(scope, recipients, dto.ResourceKey)) return null;
        }
        else
        {
            if (PermissionOf(dto.Category) is { } needed && !scope.Has(needed)) return null;
            if (!scope.CanViewResourceKey(dto.ResourceKey)) return null;
        }
        if (!scope.FiltersView) return dto;
        var cameras = dto.ChannelIds.Where(id => scope.CanViewChannel(id)).ToList();
        return cameras.Count == dto.ChannelIds.Count ? dto : dto with { ChannelIds = cameras };
    }

    /// <summary>El permiso que exige ver cada tipo de evento (además de la vista en vivo).</summary>
    private static string? PermissionOf(string category) => category switch
    {
        LiveEventCategories.Alarm or LiveEventCategories.AlarmSystem => Permissions.AlarmsMonitor,
        LiveEventCategories.AccessDenied or LiveEventCategories.AccessGranted or LiveEventCategories.DoorAlarm
            => Permissions.AccessMonitor,
        LiveEventCategories.Plate or LiveEventCategories.PlateFlagged => Permissions.AnprView,
        LiveEventCategories.Fence => Permissions.CercoMonitor,
        LiveEventCategories.Intercom => Permissions.IntercomAnswer,
        _ => null, // video y E/S: basta la vista en vivo y el alcance
    };

    /// <summary>
    /// Recurso de origen, su ubicación y las cámaras: las del primer candidato
    /// que tenga (una zona sin cámaras usa las de su área). Con caché, para que
    /// una ráfaga de eventos de la misma puerta no consulte la base cada vez.
    /// </summary>
    private async Task<Resolved?> ResolveAsync(Raw raw, CancellationToken ct)
    {
        var e = raw.Ref;
        if (e == new ResourceBriefings.EventRef() && raw.ResourceKey is null) return null;
        string cacheKey = raw.ResourceKey ?? e.ToString();
        lock (_sync)
        {
            if (_resolveCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Value;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        List<ResourceRef> candidates = raw.ResourceKey is { } key && ParseKey(key) is { } fromKey
            ? [fromKey]
            : await ResourceBriefings.CandidatesAsync(db, e, ct);

        string? originKey = null, name = null, path = null;
        int? locationId = null;
        IReadOnlyList<int> cameras = [];
        foreach (var candidate in candidates)
        {
            var detail = await ResourceDetails.LoadAsync(db, candidate.Kind, candidate.Id, ct);
            if (detail is null) continue;
            if (originKey is null)
            {
                originKey = ResourceCatalog.Key(candidate.Kind, candidate.Id);
                name = detail.Resource.Name;
                path = detail.LocationPath;
                locationId = detail.Resource.LocationId;
            }
            if (detail.Cameras.Count > 0)
            {
                cameras = detail.Cameras.Select(c => c.ChannelId).ToList();
                break;
            }
        }
        var resolved = originKey is null ? null
            : new Resolved(originKey, name, path, await AncestorsAsync(db, locationId, ct), cameras);
        lock (_sync)
        {
            _resolveCache[cacheKey] = (DateTime.UtcNow, resolved);
            if (_resolveCache.Count > 1000)
                foreach (var old in _resolveCache.Where(p => DateTime.UtcNow - p.Value.At > CacheFor).Select(p => p.Key).ToList())
                    _resolveCache.Remove(old);
        }
        return resolved;
    }

    private static ResourceRef? ParseKey(string key)
    {
        var parts = key.Split(':');
        return parts.Length == 2 && Enum.TryParse<ResourceKind>(parts[0], out var kind) && int.TryParse(parts[1], out int id)
            ? new ResourceRef(kind, id)
            : null;
    }

    /// <summary>La ubicación y todas las que la contienen (un vigía de «Bodega» ve lo de «Bodega › Patio»).</summary>
    private async Task<IReadOnlyList<int>> AncestorsAsync(VmsDbContext db, int? locationId, CancellationToken ct)
    {
        if (locationId is null) return [];
        Dictionary<int, int?> parents;
        if (_locations is { } cached && DateTime.UtcNow - cached.At < CacheFor) parents = cached.Parents;
        else
        {
            parents = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.ParentId, ct);
            _locations = (DateTime.UtcNow, parents);
        }
        var list = new List<int>();
        for (int? id = locationId; id is { } current && !list.Contains(current); id = parents.GetValueOrDefault(current))
            list.Add(current);
        return list;
    }
}
