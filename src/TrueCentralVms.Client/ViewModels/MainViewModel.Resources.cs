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

    /// <summary>El cuadro que muestra un recurso: su panel según el tipo.</summary>
    public CellPanel? CreatePanel(ResourceNode node) => node.CardKind switch
    {
        CardKinds.Equipment => new EquipmentCardPanel(_api),
        CardKinds.Server => new ServerCardPanel(_api),
        CardKinds.Alerts => new AlertsCardPanel(_api, _hub, OpenAlert),
        CardKinds.Plates => new PlatesCardPanel(_api, _hub),
        CardKinds.Access => new AccessCardPanel(_api, _hub),
        _ => CreateResourcePanel(node),
    };

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
