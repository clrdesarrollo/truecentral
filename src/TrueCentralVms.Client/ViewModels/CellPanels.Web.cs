using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Página web en un cuadro (mapa, noticias, un canal de TV, un sistema
/// externo…): de la lista del servidor o, con "Abrir cualquier página web",
/// una dirección escrita a mano. La dibuja <c>Views.WebCellView</c>, con un
/// navegador incrustado de perfil propio: no comparte cookies ni sesión con el
/// mapa de Flota ni con el panel web del servidor. Abrirla y cerrarla queda en
/// la bitácora del servidor (como comenzar y dejar de ver una cámara). Su zoom
/// se guarda con el cuadro (última sesión y tableros).
/// </summary>
public sealed partial class WebCellPanel : CellPanel
{
    private readonly ApiClient? _api;
    private DateTime? _openedAt;

    public WebCellPanel(string url, string title, int? linkId, ApiClient? api = null, int? zoom = null)
    {
        _api = api;
        Url = url;
        Title = title;
        LinkId = linkId;
        _zoom = DashboardItemKinds.CleanZoom(zoom) ?? 100;
        Content = linkId is { } id
            ? new CellContentRef(DashboardItemKinds.Web, id, null, title, DashboardItemKinds.CleanZoom(_zoom))
            : new CellContentRef(DashboardItemKinds.Web, null, url, title, DashboardItemKinds.CleanZoom(_zoom));
        Status = "Cargando…";
    }

    public override string Glyph => "\uE774";
    public override string Kind => "web";

    /// <summary>Dirección con la que abrió (la página puede navegar a otras).</summary>
    public string Url { get; }

    /// <summary>Página de la lista del servidor (null = dirección libre).</summary>
    public int? LinkId { get; }

    /// <summary>Dirección que muestra ahora (cambia si la página navega).</summary>
    [ObservableProperty] private string _currentUrl = "";

    /// <summary>Error que impide mostrarla (sin WebView2, sin red…).</summary>
    [ObservableProperty] private string _error = "";

    /// <summary>La vista vuelve a cargar la página (botón de la barra).</summary>
    public event Action? ReloadRequested;

    [RelayCommand]
    private void Reload() => ReloadRequested?.Invoke();

    /// <summary>Pasos del zoom (los de un navegador).</summary>
    private static readonly int[] ZoomSteps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];

    /// <summary>Zoom de la página en porcentaje (también cambia con Ctrl + rueda dentro de ella).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private int _zoom;

    public string ZoomLabel => $"{Zoom} %";

    /// <summary>El zoom va en lo que se guarda del cuadro: el cuadro avisa y se guarda la sesión.</summary>
    partial void OnZoomChanged(int value)
    {
        if (Content is { } content)
        {
            Content = content with { Zoom = DashboardItemKinds.CleanZoom(value) };
            OnPropertyChanged(nameof(Content));
        }
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = ZoomSteps.FirstOrDefault(s => s > Zoom, ZoomSteps[^1]);

    [RelayCommand]
    private void ZoomOut() => Zoom = ZoomSteps.LastOrDefault(s => s < Zoom, ZoomSteps[0]);

    [RelayCommand]
    private void ZoomReset() => Zoom = 100;

    /// <summary>El cuadro la recibió: queda en la bitácora (sin esperar al servidor).</summary>
    public override Task LoadAsync()
    {
        _openedAt = DateTime.UtcNow;
        Report("live-web-opened", Url + (LinkId is null ? ", dirección escrita a mano" : ", de la lista del servidor"));
        return Task.CompletedTask;
    }

    /// <summary>Se cerró (cuadro liberado, reemplazado, otro tablero o el cliente se cierra).</summary>
    public override void Dispose()
    {
        if (_openedAt is not { } openedAt) return;
        _openedAt = null;
        var open = DateTime.UtcNow - openedAt;
        Report("live-web-closed", $"{Url}; abierta {(int)open.TotalMinutes} min {open.Seconds} s");
    }

    private void Report(string action, string detail) =>
        _ = _api?.ReportAuditEventAsync(new ClientAuditEventDto(action, null, Title, null, null, null, detail));
}
