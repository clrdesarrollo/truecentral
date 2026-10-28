using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Página web en un cuadro (mapa, noticias, un canal de TV, un sistema
/// externo…): de la lista del servidor o, con "Abrir cualquier página web",
/// una dirección escrita a mano. La dibuja <c>Views.WebCellView</c>, con un
/// navegador incrustado de perfil propio: no comparte cookies ni sesión con el
/// mapa de Flota ni con el panel web del servidor.
/// </summary>
public sealed partial class WebCellPanel : CellPanel
{
    public WebCellPanel(string url, string title, int? linkId)
    {
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
}
