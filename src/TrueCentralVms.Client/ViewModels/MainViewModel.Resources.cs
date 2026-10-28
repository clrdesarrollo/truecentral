using System.Collections.ObjectModel;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Centro de mando: la lista ya no es solo de cámaras. Los recursos de todo
/// tipo que ve esta sesión (/api/resources, ya filtrado por alcance) cuelgan de
/// su ubicación junto a las cámaras, y al final van las tarjetas de reporte.
/// Cada uno se abre en un cuadro como su <see cref="CellPanel"/>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Grupos que van al final de la lista, fuera de las ubicaciones (las «Tarjetas»).</summary>
    public ObservableCollection<ResourceGroupNode> ResourceGroups { get; } = [];

    /// <summary>Recursos para el árbol por ubicación (sin cámaras ni zonas).</summary>
    private List<ResourceNode> _locatedResources = [];

    /// <summary>Tipos que se muestran y si los permisos de la sesión dejan verlos.</summary>
    private bool ShowsKind(ResourceKind kind) => kind switch
    {
        ResourceKind.Door => PermissionScope.Current.HasAny(Permissions.AccessDoors, Permissions.AccessMonitor),
        ResourceKind.Partition => CanAlarms,
        ResourceKind.Fence => CanCerco,
        ResourceKind.Intercom => CanIntercom,
        ResourceKind.Speaker => SpeakersViewModel.CanUse,
        ResourceKind.IoModule => PermissionScope.Current.HasAny(Permissions.IoOperate, Permissions.IoConfigure),
        _ => false,
    };

    private async Task LoadResourcesAsync()
    {
        List<ResourceDto> all;
        try { all = await _api.GetResourcesAsync(); }
        catch (ApiException) { all = []; } // servidor anterior o sin acceso: solo cámaras

        _locatedResources = all.Where(r => r.Enabled && ShowsKind(r.Kind)).Select(ResourceNode.From).ToList();

        // Tarjetas de reporte: no son equipos, van siempre al final y en su orden.
        ResourceGroups.Clear();
        var cards = new ResourceGroupNode("Tarjetas", "\uE9D2");
        cards.Children.Add(ResourceNode.Card(CardKinds.Equipment, "Estado de los equipos", "\uE9D9",
            "En línea por tipo de equipo y los que están sin conexión"));
        cards.Children.Add(ResourceNode.Card(CardKinds.Server, "Servidor", "\uE977",
            "CPU, memoria, disco y servicios del servidor"));
        if (CanEvents)
            cards.Children.Add(ResourceNode.Card(CardKinds.Alerts, "Últimas alertas", "\uE7BA",
                "Alertas de las automatizaciones, con las pendientes de confirmar"));
        if (CanLpr)
            cards.Children.Add(ResourceNode.Card(CardKinds.Plates, "Últimas patentes", "\uE804",
                "Reconocimientos de patente en vivo"));
        if (PermissionScope.Current.Has(Permissions.AccessMonitor))
            cards.Children.Add(ResourceNode.Card(CardKinds.Access, "Últimos accesos", "\uE8D7",
                "Pasos por todas las puertas en vivo"));
        ResourceGroups.Add(cards);

        // Páginas web: las de la lista del servidor y, con permiso, «Otra dirección…».
        List<WebLinkDto> links;
        try { links = await _api.GetWebLinksAsync(); }
        catch (ApiException) { links = []; } // servidor anterior: sin lista
        var web = new ResourceGroupNode("Páginas web", "\uE774");
        foreach (var link in links) web.Children.Add(ResourceNode.Web(link));
        if (CanOpenAnyUrl) web.Children.Add(ResourceNode.WebCustom());
        if (web.Children.Count > 0) ResourceGroups.Add(web);
    }

    /// <summary>Buscador en los grupos de recursos (nombre, detalle o grupo).</summary>
    private void ApplyResourceSearchFilter(string text)
    {
        foreach (var group in ResourceGroups)
        {
            bool groupMatch = Matches(group.Title, text);
            bool any = false;
            foreach (var node in group.Children)
            {
                node.IsVisible = text.Length == 0 || groupMatch || Matches(node.Name, text) || Matches(node.Detail ?? "", text);
                any |= node.IsVisible;
            }
            group.IsVisible = text.Length == 0 || groupMatch || any;
        }
    }

    /// <summary>El cuadro que muestra un recurso: su panel según el tipo (null si no se pudo o se canceló).</summary>
    public CellPanel? CreatePanel(ResourceNode node)
    {
        var panel = node.CardKind switch
        {
            CardKinds.Equipment => new EquipmentCardPanel(_api),
            CardKinds.Server => new ServerCardPanel(_api),
            CardKinds.Alerts => new AlertsCardPanel(_api, _hub, OpenAlert),
            CardKinds.Plates => new PlatesCardPanel(_api, _hub),
            CardKinds.Access => new AccessCardPanel(_api, _hub),
            CardKinds.Web when node.Url is { } url => new WebCellPanel(url, node.Name, node.Id, _api),
            CardKinds.WebCustom => AskWebPage(),
            _ => CreateResourcePanel(node),
        };
        // Lo que se guarda en los tableros y en la última sesión.
        if (panel is not null && panel.Content is null)
            panel.Content = node.CardKind is { } card
                ? new CellContentRef(DashboardItemKinds.Card, null, card)
                : new CellContentRef(DashboardItemKinds.Resource, node.Id, node.Kind.ToString(), node.Name);
        return panel;
    }

    /// <summary>«Otra dirección…»: pide la página (null si se canceló).</summary>
    private CellPanel? AskWebPage()
    {
        if (!CanOpenAnyUrl) return null;
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                    ?? System.Windows.Application.Current.MainWindow;
        return Views.WebUrlWindow.Ask(owner) is { } page ? new WebCellPanel(page.Url, page.Title, null, _api) : null;
    }

    /// <summary>
    /// Vuelve a armar lo que guardó un tablero o la última sesión en un cuadro
    /// que no es cámara. Null si ya no existe o esta sesión no lo puede ver
    /// (sus permisos o su alcance): el cuadro queda libre.
    /// </summary>
    public CellPanel? PanelFor(string? kind, int? refId, string? reference, string? title)
    {
        switch (kind)
        {
            case DashboardItemKinds.Resource:
                if (!Enum.TryParse<ResourceKind>(reference, out var resourceKind)) return null;
                var node = _locatedResources.FirstOrDefault(r => r.Kind == resourceKind && r.Id == refId);
                return node is null ? null : CreatePanel(node);
            case DashboardItemKinds.Card:
                var card = ResourceGroups.SelectMany(g => g.Children).FirstOrDefault(n => n.CardKind == reference);
                return card is null ? null : CreatePanel(card);
            case DashboardItemKinds.Web:
                // Una de la lista se busca al día (pudieron cambiarle la dirección).
                if (refId is { } linkId)
                    return ResourceGroups.SelectMany(g => g.Children)
                        .FirstOrDefault(n => n.CardKind == CardKinds.Web && n.Id == linkId) is { Url: { } url } link
                        ? new WebCellPanel(url, link.Name, linkId, _api)
                        : null;
                return Uri.TryCreate(reference, UriKind.Absolute, out var uri) &&
                       (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                    ? new WebCellPanel(uri.ToString(), string.IsNullOrWhiteSpace(title) ? uri.Host : title, null, _api)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Lo que hay en un cuadro, en el formato de los tableros (null = libre o no se guarda).</summary>
    public static LiveViewItemDto? DashboardItemOf(VideoCellViewModel cell, int index) =>
        cell.AssignedChannel is { } channel
            ? new LiveViewItemDto(index, channel.Channel.Id, (int)cell.Profile, DashboardItemKinds.Camera)
            : cell.Panel?.Content is { } content
                ? new LiveViewItemDto(index, 0, 0, content.Kind, content.RefId, content.Ref, content.Title)
                : null;

    private CellPanel? CreateResourcePanel(ResourceNode node) => node.Kind switch
    {
        ResourceKind.Door => new DoorCellPanel(_api, _hub, node),
        ResourceKind.Partition => new AlarmCellPanel(_api, Alarms, node.AlarmPanelId, node.IsWholePanel ? node.Name : node.Equipment ?? node.Detail ?? node.Name),
        ResourceKind.Fence => new CercoCellPanel(Cerco, node.Id, node.Name),
        ResourceKind.Intercom => new IntercomCellPanel(_api, Intercom, OpenIntercomWindow, node.Id, node.Name),
        ResourceKind.Speaker => new SpeakerCellPanel(Speakers, node.Id, node.Name),
        ResourceKind.IoModule => new IoCellPanel(_api, _hub, node.Id, node.Name),
        _ => null,
    };

    /// <summary>Abre un recurso en un cuadro concreto (arrastrar desde la lista).</summary>
    public async Task OpenResourceInCellAsync(ResourceNode node, VideoCellViewModel cell)
    {
        if (CreatePanel(node) is not { } panel) return;
        await cell.ShowPanelAsync(panel);
        SelectedCell = cell;
        StatusMessage = $"\"{panel.Title}\" en el cuadro {cell.Index}.";
    }

    /// <summary>Doble clic en un recurso: en el cuadro seleccionado, o en el primero libre.</summary>
    public async Task OpenResourceAsync(ResourceNode node)
    {
        var cell = SelectedCell ?? Cells.FirstOrDefault(c => c.IsEmpty) ?? Cells.FirstOrDefault();
        if (cell is not null) await OpenResourceInCellAsync(node, cell);
    }

    /// <summary>«Ver» desde la tarjeta de alertas: la ventana de alarma de siempre, con esa alerta.</summary>
    public void OpenAlert(WorkflowAlertDto alert)
    {
        if (System.Windows.Application.Current.MainWindow is not { IsLoaded: true } owner) return;
        Views.AlertWindow.Show(owner, _api, _settings, _alertSound,
            id => Devices.SelectMany(d => d.Channels).FirstOrDefault(c => c.Channel.Id == id),
            [alert], alert.Id);
    }

    /// <summary>«Contestar» desde un cuadro de citófono: la ventana de llamada de siempre.</summary>
    public void OpenIntercomWindow(int intercomId)
    {
        if (Intercom.Intercoms.FirstOrDefault(i => i.Id == intercomId) is { } item)
            ShowIntercomWindow(item.Intercom, item.Intercom.ActiveCall);
    }
}
