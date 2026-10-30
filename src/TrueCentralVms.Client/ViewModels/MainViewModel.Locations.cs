using TrueCentralVms.Client.Views;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// La lista de recursos ordenada según la página Recursos del servidor (sitio →
/// edificio → piso → sector → punto). La comparten Vista en vivo, Reproducción
/// (solo cámaras) y las pantallas auxiliares.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Raíces del árbol de la Vista en vivo ("Sin ubicación" al final, si hace falta).</summary>
    public ObservableCollection<LocationNode> LocationRoots { get; } = [];

    /// <summary>Alcance por ubicación de esta sesión, para el menú del usuario (null = sin restricción).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopeLabel))]
    private string? _scopeLabel;

    public bool HasScopeLabel => !string.IsNullOrEmpty(ScopeLabel);

    /// <summary>Lee el alcance de la sesión (al entrar y cuando un administrador lo cambia).</summary>
    private async Task LoadScopeAsync()
    {
        var scope = await _api.GetMyScopeAsync();
        ScopeLabel = scope is { Restricted: true }
            ? "Alcance: " + (scope.Locations.Count > 0 ? string.Join(", ", scope.Locations) : "ninguna ubicación") +
              (scope.ViewOutsideScope ? " (ve el resto, sin operarlo)" : "")
            : null;
    }

    private CancellationTokenSource? _operableReload;

    /// <summary>Relee lo que esta sesión puede operar (al entrar y con los avisos que lo cambian).</summary>
    private async Task LoadOperableAsync()
    {
        if (await _api.GetOperableAsync() is { } operable) OperableScope.Current.Update(operable);
    }

    /// <summary>Relee con una pausa corta: varios avisos seguidos hacen una sola consulta.</summary>
    private async void ScheduleOperableReload()
    {
        _operableReload?.Cancel();
        var cts = _operableReload = new CancellationTokenSource();
        try { await Task.Delay(400, cts.Token); }
        catch (TaskCanceledException) { return; }
        await LoadOperableAsync();
    }

    /// <summary>Cambió lo que se puede operar: el PTZ y las órdenes por ubicación se re-evalúan.</summary>
    private void OnOperableChanged()
    {
        UpdatePtzPanel();
        foreach (var root in LocationRoots.Concat(PlaybackTreeItems)) root.RefreshOperable();
    }

    /// <summary>
    /// La lista de la Vista en vivo y de las pantallas auxiliares: las
    /// ubicaciones con sus cámaras y demás recursos, "Sin ubicación" (agrupado
    /// por equipo) y, al final, las tarjetas de reporte. Ya no hay árbol "por
    /// equipo": cada equipo se ubica al darlo de alta (o queda en una ubicación
    /// con su nombre desde Recursos).
    /// </summary>
    public ObservableCollection<object> TreeItems { get; } = [];

    /// <summary>Árbol de Reproducción: las mismas ubicaciones, solo con cámaras.</summary>
    public ObservableCollection<LocationNode> PlaybackTreeItems { get; } = [];

    /// <summary>Orden en que lo lee una persona: "Cámara 2" antes que "Cámara 10".</summary>
    private static readonly StringComparer NaturalOrder =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    /// <summary>Rehace los árboles con los canales YA cargados en <see cref="Devices"/>
    /// y los recursos de <see cref="LoadResourcesAsync"/>.</summary>
    private async Task LoadLocationTreeAsync()
    {
        List<LocationDto> locations;
        try { locations = await _api.GetLocationsAsync(); }
        catch (ApiException) { locations = []; } // sin ubicaciones (o servidor anterior): todo queda "Sin ubicación"

        Fill(LocationRoots, locations, _locatedResources);
        Fill(PlaybackTreeItems, locations, []);
        TreeItems.Clear();
        foreach (var root in LocationRoots) TreeItems.Add(root);
        foreach (var group in ResourceGroups) TreeItems.Add(group);
        ApplySearchFilter(); // el texto del buscador vale también para lo recién cargado
    }

    /// <summary>
    /// Arma un árbol: los nodos de cámara son los mismos objetos de
    /// <see cref="Devices"/>. Las ubicaciones sin nada en su subárbol no se
    /// muestran; lo que nadie ubicó va a "Sin ubicación", agrupado por su
    /// equipo (un parlante, citófono o cerco es el equipo mismo y va suelto).
    /// </summary>
    private void Fill(ObservableCollection<LocationNode> target, List<LocationDto> locations,
        IReadOnlyList<ResourceNode> resources)
    {
        var nodes = locations.ToDictionary(l => l.Id, l => new LocationNode(l));
        var roots = new List<LocationNode>();
        foreach (var location in locations.OrderBy(l => l.Name, NaturalOrder))
        {
            var node = nodes[location.Id];
            if (location.ParentId is { } parentId && nodes.TryGetValue(parentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        var equipment = new Dictionary<string, LocationNode>(StringComparer.OrdinalIgnoreCase);
        LocationNode EquipmentNode(string name)
        {
            if (!equipment.TryGetValue(name, out var node)) equipment[name] = node = new LocationNode(null, name);
            return node;
        }
        var loose = new List<ResourceNode>();

        foreach (var channel in Devices.SelectMany(d => d.Channels).OrderBy(c => c.Header, NaturalOrder))
        {
            if (channel.Channel.LocationId is { } id && nodes.TryGetValue(id, out var owner))
                owner.Children.Add(channel);
            else
                EquipmentNode(channel.Device.Name).Children.Add(channel);
        }
        // Centro de mando: puertas, áreas, cercos… junto a las cámaras de su ubicación.
        foreach (var resource in resources.OrderBy(r => r.Kind).ThenBy(r => r.Name, NaturalOrder))
        {
            if (resource.LocationId is { } id && nodes.TryGetValue(id, out var owner))
                owner.Children.Add(resource);
            else if (resource.Equipment is { } name)
                EquipmentNode(name).Children.Add(resource);
            else
                loose.Add(resource);
        }

        target.Clear();
        foreach (var root in roots)
            if (Prune(root)) target.Add(root);

        var unassigned = new LocationNode(null);
        foreach (var node in equipment.Values.OrderBy(n => n.Header, NaturalOrder))
        {
            node.ChannelCount = node.AllChannels().Count();
            unassigned.Children.Add(node);
        }
        foreach (var resource in loose) unassigned.Children.Add(resource);
        unassigned.ChannelCount = unassigned.AllChannels().Count();
        if (unassigned.Children.Count > 0) target.Add(unassigned);

        // Cuenta las cámaras de cada subárbol y quita las ramas sin nada.
        static bool Prune(LocationNode node)
        {
            foreach (var child in node.Children.OfType<LocationNode>().ToList())
                if (!Prune(child)) node.Children.Remove(child);
            node.ChannelCount = node.AllChannels().Count();
            return node.ChannelCount > 0 || HasResources(node);
        }

        static bool HasResources(LocationNode node) =>
            node.Children.OfType<ResourceNode>().Any() || node.Children.OfType<LocationNode>().Any(HasResources);
    }

    /// <summary>Buscador en los árboles por ubicación: una cámara se ve si calza
    /// su nombre, el de su equipo o el de alguna ubicación que la contiene.</summary>
    private void ApplyLocationSearchFilter(string text)
    {
        foreach (var root in LocationRoots.Concat(PlaybackTreeItems)) Filter(root, ancestorMatch: false);

        bool Filter(LocationNode node, bool ancestorMatch)
        {
            bool match = text.Length == 0 || ancestorMatch || Matches(node.Header, text);
            bool anyVisible = false;
            foreach (var child in node.Children)
            {
                if (child is LocationNode location)
                {
                    anyVisible |= Filter(location, match);
                }
                else if (child is ChannelNode channel)
                {
                    channel.IsVisible = match || Matches(channel.Header, text) || Matches(channel.Device.Name, text);
                    anyVisible |= channel.IsVisible;
                }
                else if (child is ResourceNode resource)
                {
                    resource.IsVisible = match || Matches(resource.Name, text) || Matches(resource.Detail ?? "", text);
                    anyVisible |= resource.IsVisible;
                }
            }
            node.IsVisible = match || anyVisible;
            return node.IsVisible;
        }
    }

    /// <summary>Doble clic en una ubicación: abre todas sus cámaras, incluidas
    /// las de sus sububicaciones (misma apertura por tandas que un equipo).</summary>
    public Task OpenLocationAsync(LocationNode location) =>
        OpenChannelsAsync(location.Header, location.AllChannels().ToList());

    // ---------- Verificación de avisos (consignas y cámaras de la ficha) ----------

    private ChannelNode? FindChannel(int channelId) =>
        Devices.SelectMany(d => d.Channels).FirstOrDefault(c => c.Channel.Id == channelId);

    /// <summary>
    /// Recursos por los que llegó una alerta de automatización con acuse de
    /// recibo ("Zone:3" → cuándo llegó). Esa ventana ya muestra las consignas
    /// y las cámaras del recurso: su verificación no se abre aparte.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _alertedResources = new();
    /// <summary>Margen para que llegue la alerta de una automatización por el mismo evento.</summary>
    private static readonly TimeSpan VerificationGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>Llegó una alerta por un recurso: anota que cubre su verificación y retira la que esté a la vista.</summary>
    private void NoteAlertedResource(string resourceKey)
    {
        var now = DateTime.UtcNow;
        _alertedResources[resourceKey] = now;
        foreach (var pair in _alertedResources)
            if (now - pair.Value > TimeSpan.FromMinutes(5)) _alertedResources.TryRemove(pair.Key, out _);
        Application.Current.Dispatcher.InvokeAsync(() => Views.VerificationWindow.Supersede(resourceKey));
    }

    /// <summary>
    /// Un recurso avisó algo: se pide su resumen al servidor y, si su ficha
    /// tiene consignas o cámaras asociadas, se abre la ventana de verificación.
    /// <paramref name="query"/> identifica el origen del evento (ver
    /// /api/resources/briefing). Sin ficha útil, el aviso queda como estaba.
    /// </summary>
    private async Task VerifyAsync(string query, string title, string detail, bool critical, DateTime at)
    {
        // Una alerta del mismo recurso llegada justo antes también es de este evento.
        var since = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        var briefing = await _api.GetBriefingAsync(query);
        if (briefing is null || !briefing.HasGuidance) return;
        // Si una automatización avisa por el mismo evento, su ventana ya trae
        // las consignas y las cámaras: se le da un momento para llegar y, si
        // llegó, no se abre otra ventana por lo mismo (si llega más tarde,
        // retira esta; ver NoteAlertedResource).
        await Task.Delay(VerificationGrace);
        if (_alertedResources.TryGetValue(Views.VerificationWindow.Key(briefing), out var alertedAt) && alertedAt >= since)
            return;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var owner = Application.Current.MainWindow is { IsLoaded: true } window ? window : null;
            Views.VerificationWindow.Show(owner, _api, _settings, FindChannel, OpenCamerasAsync,
                new Views.VerificationWindow.Item(briefing, title, detail, critical, at));
        });
    }

    /// <summary>
    /// «Ver en vivo» de la tarjeta Últimos accesos: las cámaras de la puerta del
    /// paso (la del terminal y las de su ficha) en la ventana de verificación,
    /// sin tocar la grilla. Devuelve el motivo si no hay qué mostrar.
    /// </summary>
    public async Task<string?> OpenDoorCamerasAsync(AccessEventDto e)
    {
        if (e.DoorNumber is not { } door) return "El paso no dice por qué puerta fue.";
        var briefing = await _api.GetBriefingAsync($"accessDevice={e.DeviceId}&door={door}");
        if (briefing is null || briefing.Cameras.Count == 0)
            return "La puerta no tiene cámaras: se le asocian en su ficha (Recursos) o con la cámara del terminal.";
        var line = new AccessEventLine(e);
        string doorName = string.IsNullOrWhiteSpace(e.DoorName) ? $"puerta {door}" : e.DoorName!;
        var owner = Application.Current.MainWindow is { IsLoaded: true } window ? window : null;
        Views.VerificationWindow.Show(owner, _api, _settings, FindChannel, OpenCamerasAsync,
            new Views.VerificationWindow.Item(briefing, $"{line.What} · {line.Who}",
                $"{e.DeviceName} · {doorName} · {e.Timestamp.ToLocalTime():dd-MM-yyyy HH:mm:ss}",
                Critical: e.Kind is AccessEventKind.Denied or AccessEventKind.Alarm, e.Timestamp));
        return null;
    }

    /// <summary>Consulta de la ficha para un evento de panel de alarma (la zona y, si no, su área).</summary>
    private static string AlarmQuery(AlarmEventDto dto) =>
        $"alarmPanel={dto.PanelId}" +
        (dto.AreaNumber is { } area ? $"&area={area}" : "") +
        (dto.ZoneNumber is { } zone ? $"&zone={zone}" : "");

    /// <summary>Lleva cámaras a la grilla principal (botón de la ventana de verificación).</summary>
    public async Task OpenCamerasAsync(string label, IReadOnlyList<ChannelNode> cameras)
    {
        OpenLiveView();
        Application.Current.MainWindow?.Activate();
        await OpenChannelsAsync(label, cameras.ToList());
    }

    /// <summary>Último aviso flotante por puerta: una puerta que insiste no llena la pantalla.</summary>
    private readonly Dictionary<(int Device, int Door), DateTime> _doorAlarmShown = [];

    /// <summary>
    /// Alarma de una puerta (forzada, mantenida abierta, sabotaje, coacción):
    /// aviso flotante y verificación con la ficha de la puerta. Solo las
    /// recientes: al reconectarse, el servidor recupera eventos viejos del
    /// equipo, y esos no se vuelven a avisar.
    /// </summary>
    private void OnAccessEvent(AccessEventDto dto)
    {
        if (dto.Kind != AccessEventKind.Alarm || dto.DoorNumber is not { } door) return;
        if (DateTime.UtcNow - dto.Timestamp.ToUniversalTime() > TimeSpan.FromMinutes(5)) return;
        string doorName = string.IsNullOrWhiteSpace(dto.DoorName) ? $"puerta {door}" : dto.DoorName!;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var key = (dto.DeviceId, door);
            bool repeated = _doorAlarmShown.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1);
            _doorAlarmShown[key] = DateTime.UtcNow;
            StatusMessage = $"ALARMA DE PUERTA: {doorName} ({dto.DeviceName}) — {dto.Description}";
            if (!repeated && Application.Current.MainWindow is { IsLoaded: true } owner)
                Views.ToastWindow.ShowAlert(owner, $"Alarma de puerta · {doorName}",
                    $"{dto.Description}\n{dto.DeviceName}\n{dto.Timestamp.ToLocalTime():HH:mm:ss}");
        });
        _ = VerifyAsync($"accessDevice={dto.DeviceId}&door={door}", $"Alarma de puerta: {dto.Description}",
            $"{dto.DeviceName} · {doorName} · {dto.Timestamp.ToLocalTime():dd-MM-yyyy HH:mm:ss}",
            critical: true, dto.Timestamp);
    }

    // ---------- Órdenes sobre una ubicación (clic derecho en el árbol) ----------

    /// <summary>
    /// Arma o desarma todas las áreas de alarma de la ubicación (con las de sus
    /// sububicaciones). Primero muestra cuáles se tocarían y pide confirmación;
    /// el servidor ordena y audita área por área, igual que el módulo Alarmas.
    /// </summary>
    public async Task RunLocationCommandAsync(LocationNode node, string command)
    {
        if (node.Location is not { } location || !node.CanCommand) return;
        var owner = Application.Current.MainWindow;
        string verb = command switch { "arm-away" => "Armar total", "arm-stay" => "Armar parcial", _ => "Desarmar" };
        LocationCommandResultDto preview;
        try { preview = await _api.RunLocationCommandAsync(location.Id, command, dryRun: true); }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
            return;
        }
        if (preview.Items.Count == 0)
        {
            AppMessageBox.Show(owner, preview.Message, verb, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string list = string.Join("\n", preview.Items.Take(15).Select(i => $"• {i.Area} ({i.Panel})")) +
                      (preview.Items.Count > 15 ? $"\n… y {preview.Items.Count - 15} más" : "");
        if (AppMessageBox.Show(owner, $"{preview.Message}\n\n{list}\n\n¿Continuar?", $"{verb} · {location.Name}",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        StatusMessage = $"{verb}: enviando la orden a {preview.Items.Count} área(s)…";
        try
        {
            var result = await _api.RunLocationCommandAsync(location.Id, command, dryRun: false);
            StatusMessage = result.Message;
            var failed = result.Items.Where(i => !i.Success).ToList();
            if (failed.Count > 0)
                AppMessageBox.Show(owner,
                    result.Message + "\n\n" + string.Join("\n", failed.Select(f => $"• {f.Area} ({f.Panel}): {f.Error}")),
                    $"{verb} · {location.Name}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
            AppMessageBox.Show(owner, ex.Message, $"{verb} · {location.Name}", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
