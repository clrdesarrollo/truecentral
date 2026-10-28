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
/// la bitácora del servidor (como comenzar y dejar de ver una cámara).
/// </summary>
public sealed partial class WebCellPanel : CellPanel
{
    private readonly ApiClient? _api;
    private DateTime? _openedAt;

    public WebCellPanel(string url, string title, int? linkId, ApiClient? api = null)
    {
        _api = api;
        Url = url;
        Title = title;
        LinkId = linkId;
        Content = linkId is { } id
            ? new CellContentRef(DashboardItemKinds.Web, id, null, title)
            : new CellContentRef(DashboardItemKinds.Web, null, url, title);
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
