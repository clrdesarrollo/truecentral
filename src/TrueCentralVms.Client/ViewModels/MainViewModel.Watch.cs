using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Cuadro vigía: lo que el shell le presta (cámaras, avisos, ubicaciones) y cómo se arma.</summary>
public partial class MainViewModel
{
    /// <summary>Eventos en vivo del puesto (los reparte <see cref="WatchCoordinator"/> entre los vigías).</summary>
    public LiveEventHub LiveEvents { get; private set; } = null!;

    private (DateTime At, IReadOnlyList<(int Id, string Path)> List)? _locationPaths;

    private void InitializeLiveEvents()
    {
        LiveEvents = new LiveEventHub(_api, _hub);
        WatchCoordinator.Initialize(LiveEvents);
        _ = LoadReplayLeadAsync();
    }

    /// <summary>«Repetir» del vigía abre la grabación con los segundos «antes» de la repetición del servidor.</summary>
    private async Task LoadReplayLeadAsync()
    {
        try { WatchCellPanel.ReplayLeadSeconds = (await _api.GetClipSettingsAsync()).PreSeconds; }
        catch (ApiException) { /* sin permiso de ver automatizaciones o servidor anterior: 5 s */ }
    }

    /// <summary>Un Cuadro vigía con estas opciones (la clave guardada, o las de omisión).</summary>
    public WatchCellPanel CreateWatchPanel(WatchOptions options) => new(options, new WatchContext(
        id => Devices.SelectMany(d => d.Channels).FirstOrDefault(c => c.Channel.Id == id),
        OpenAlertByIdAsync,
        LocationPathsAsync,
        () => Devices.SelectMany(d => d.Channels).ToList(),
        (action, title, detail) => _ = _api.ReportAuditEventAsync(
            new ClientAuditEventDto(action, null, title, null, null, null, detail)),
        CanPlayback ? OpenPlaybackAtAsync : null));

    /// <summary>«Ver aviso» desde un vigía: la ventana de alarma con ese aviso.</summary>
    private async Task OpenAlertByIdAsync(long id)
    {
        if (await _api.GetAlertAsync(id) is { } alert) OpenAlert(alert);
    }

    /// <summary>Las ubicaciones con su ruta («Casa matriz › Bodega»), para el engranaje del vigía. Un minuto de caché.</summary>
    private async Task<IReadOnlyList<(int Id, string Path)>> LocationPathsAsync()
    {
        if (_locationPaths is { } cached && DateTime.Now - cached.At < TimeSpan.FromMinutes(1)) return cached.List;
        var all = await _api.GetLocationsAsync();
        var byId = all.ToDictionary(l => l.Id);
        string PathOf(LocationDto l)
        {
            var names = new List<string>();
            for (LocationDto? at = l; at is not null && names.Count < 20;
                 at = at.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null)
                names.Insert(0, at.Name);
            return string.Join(" › ", names);
        }
        IReadOnlyList<(int Id, string Path)> list = all.Select(l => (l.Id, PathOf(l))).ToList();
        _locationPaths = (DateTime.Now, list);
        return list;
    }

    /// <summary>Clic derecho en una ubicación → «Vigilar esta ubicación»: un vigía filtrado a ella
    /// en el cuadro seleccionado (o en el primero libre).</summary>
    public async Task WatchLocationAsync(LocationNode location)
    {
        if (location.Location is not { } place) return;
        var panel = CreateWatchPanel(WatchOptions.Default with { LocationIds = [place.Id] });
        var cell = SelectedCell ?? Cells.FirstOrDefault(c => c.IsEmpty) ?? Cells.FirstOrDefault();
        if (cell is null) return;
        await cell.ShowPanelAsync(panel);
        SelectedCell = cell;
        StatusMessage = $"El cuadro {cell.Index} vigila «{place.Name}».";
    }
}
