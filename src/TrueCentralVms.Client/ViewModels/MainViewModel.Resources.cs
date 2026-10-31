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
        // El servidor solo manda su estado a quien puede ver Sistema \u2192 Servicios.
        if (PermissionScope.Current.Has(Permissions.SystemServices))
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
                "Pasos por las puertas en vivo, sin eventos del sistema"));
        ResourceGroups.Add(cards);

        // Grupos de parlantes (los arma el administrador en el panel web).
        if (SpeakersViewModel.CanUse)
        {
            List<SpeakerGroupDto> speakerGroups;
            try { speakerGroups = await _api.GetSpeakerGroupsAsync(); }
            catch (ApiException) { speakerGroups = []; } // servidor anterior: sin grupos
            if (speakerGroups.Count > 0)
            {
                var groups = new ResourceGroupNode("Grupos de parlantes", ResourceNode.GlyphOf(ResourceKind.Speaker));
                foreach (var group in speakerGroups) groups.Children.Add(ResourceNode.SpeakerGroup(group));
                ResourceGroups.Add(groups);
            }
        }

        // Páginas web: las de la lista del servidor, las de «Mis páginas» (las que el
        // usuario escribió y quedaron guardadas) y, con permiso, «Otra dirección…».
        List<WebLinkDto> links;
        try { links = await _api.GetWebLinksAsync(); }
        catch (ApiException) { links = []; } // servidor anterior: sin lista
        var web = new ResourceGroupNode("Páginas web", "\uE774");
        foreach (var link in links) web.Children.Add(ResourceNode.Web(link, RemovePersonalWebLinkAsync));
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
    /// <param name="cardRef">Clave guardada de una tarjeta con sus opciones (las cámaras de
    /// «Últimas patentes», el «Solo sin confirmar» de «Últimas alertas», los equipos y los eventos
    /// del sistema de «Últimos accesos»); null = la tarjeta como sale de la lista.</param>
    public CellPanel? CreatePanel(ResourceNode node, string? cardRef = null)
    {
        var panel = node.CardKind switch
        {
            CardKinds.Equipment => new EquipmentCardPanel(_api),
            CardKinds.Server => new ServerCardPanel(_api),
            CardKinds.Alerts => new AlertsCardPanel(_api, _hub, OpenAlert, CardKinds.AlertsPendingOnly(cardRef)),
            CardKinds.Plates => new PlatesCardPanel(_api, _hub, CardKinds.PlatesDevices(cardRef)),
            CardKinds.Access => new AccessCardPanel(_api, _hub, CardKinds.AccessDevices(cardRef), CardKinds.AccessIncludesSystem(cardRef),
                OpenDoorCamerasAsync),
            // Las de «Mis páginas» se guardan en el cuadro como dirección: son solo de este usuario.
            CardKinds.Web when node.Url is { } url => new WebCellPanel(url, node.Name, node.IsPersonalWeb ? null : node.Id, _api),
            CardKinds.WebCustom => AskWebPage(),
            CardKinds.SpeakerGroup => new SpeakerGroupCellPanel(_api, Speakers, node.Id, node.Name)
            {
                Content = new CellContentRef(DashboardItemKinds.SpeakerGroup, node.Id, null, node.Name),
            },
            _ => CreateResourcePanel(node),
        };
        // Lo que se guarda en los tableros y en la última sesión.
        if (panel is not null && panel.Content is null)
            panel.Content = node.CardKind is { } card
                ? new CellContentRef(DashboardItemKinds.Card, null, cardRef ?? card)
                : new CellContentRef(DashboardItemKinds.Resource, node.Id, node.Kind.ToString(), node.Name);
        return panel;
    }

    /// <summary>
    /// «Otra dirección…»: pide la página (null si se canceló). Si el operador
    /// deja marcado «Guardar en Mis páginas», queda en la lista para abrirla
    /// otra vez sin escribirla.
    /// </summary>
    private CellPanel? AskWebPage()
    {
        if (!CanOpenAnyUrl) return null;
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                    ?? System.Windows.Application.Current.MainWindow;
        if (Views.WebUrlWindow.Ask(owner) is not { } page) return null;
        if (page.Save) _ = SavePersonalWebLinkAsync(page.Title, page.Url);
        return new WebCellPanel(page.Url, page.Title, null, _api);
    }

    private async Task SavePersonalWebLinkAsync(string name, string url)
    {
        try
        {
            var link = await _api.SavePersonalWebLinkAsync(name, url);
            StatusMessage = $"\"{link.Name}\" quedó en la lista de páginas web (Mis páginas).";
            await LoadTreeAsync();
        }
        catch (ApiException ex) { StatusMessage = $"No se pudo guardar la página en la lista: {ex.Message}"; }
    }

    /// <summary>Clic derecho → «Quitar de Mis páginas». Los cuadros que la muestran siguen abiertos.</summary>
    private async Task RemovePersonalWebLinkAsync(ResourceNode node)
    {
        try
        {
            await _api.DeleteWebLinkAsync(node.Id);
            StatusMessage = $"\"{node.Name}\" se quitó de Mis páginas.";
            await LoadTreeAsync();
        }
        catch (ApiException ex) { StatusMessage = $"No se pudo quitar la página: {ex.Message}"; }
    }

    /// <summary>
    /// Vuelve a armar lo que guardó un tablero o la última sesión en un cuadro
    /// que no es cámara. Null si ya no existe o esta sesión no lo puede ver
    /// (sus permisos o su alcance): el cuadro queda libre.
    /// </summary>
    public CellPanel? PanelFor(string? kind, int? refId, string? reference, string? title, int? zoom = null)
    {
        switch (kind)
        {
            case DashboardItemKinds.Resource:
                if (!Enum.TryParse<ResourceKind>(reference, out var resourceKind)) return null;
                var node = _locatedResources.FirstOrDefault(r => r.Kind == resourceKind && r.Id == refId);
                return node is null ? null : CreatePanel(node);
            case DashboardItemKinds.Card:
                // La clave puede traer opciones: "card-plates?devices=3,7".
                string key = DashboardItemKinds.CardKey(reference);
                var card = ResourceGroups.SelectMany(g => g.Children).FirstOrDefault(n => n.CardKind == key);
                return card is null ? null : CreatePanel(card, reference);
            case DashboardItemKinds.SpeakerGroup:
                var speakerGroup = ResourceGroups.SelectMany(g => g.Children)
                    .FirstOrDefault(n => n.CardKind == CardKinds.SpeakerGroup && n.Id == refId);
                return speakerGroup is null ? null : CreatePanel(speakerGroup);
            case DashboardItemKinds.Web:
                // Una de la lista se busca al día (pudieron cambiarle la dirección).
                if (refId is { } linkId)
                    return ResourceGroups.SelectMany(g => g.Children)
                        .FirstOrDefault(n => n.CardKind == CardKinds.Web && n.Id == linkId) is { Url: { } url } link
                        ? new WebCellPanel(url, link.Name, linkId, _api, zoom)
                        : null;
                return Uri.TryCreate(reference, UriKind.Absolute, out var uri) &&
                       (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                    ? new WebCellPanel(uri.ToString(), string.IsNullOrWhiteSpace(title) ? uri.Host : title, null, _api, zoom)
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
                ? new LiveViewItemDto(index, 0, 0, content.Kind, content.RefId, content.Ref, content.Title, content.Zoom)
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
            [alert], alert.Id, AlertReplay());
    }

    /// <summary>«Contestar» desde un cuadro de citófono: la ventana de llamada de siempre.</summary>
    public void OpenIntercomWindow(int intercomId)
    {
        if (Intercom.Intercoms.FirstOrDefault(i => i.Id == intercomId) is { } item)
            ShowIntercomWindow(item.Intercom, item.Intercom.ActiveCall);
    }
}
