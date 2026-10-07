using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Nodo del árbol "por ubicación" (página Recursos del servidor): sus hijos son
/// sububicaciones y cámaras. Las cámaras son los MISMOS <see cref="ChannelNode"/>
/// del árbol por equipo, así que el estado en línea, la marca ▶ de "en vivo" y
/// la selección se comparten entre los dos árboles.
/// </summary>
public sealed partial class LocationNode(LocationDto? location) : ObservableObject
{
    /// <summary>null = nodo "Sin ubicación" (las cámaras que nadie ubicó todavía).</summary>
    public LocationDto? Location { get; } = location;

    public string Header => Location?.Name ?? "Sin ubicación";

    /// <summary>Es una ubicación de verdad (no el nodo "Sin ubicación"): admite órdenes.</summary>
    public bool IsRealLocation => Location is not null;

    /// <summary>Admite órdenes Y está en el alcance de este usuario (las demás se ven, no se operan).</summary>
    public bool CanCommand => Location is { } location && Services.OperableScope.Current.CanOperateLocation(location.Id);

    /// <summary>Por qué no se puede dar órdenes sobre esta ubicación (null = sí se puede).</summary>
    public string? CommandHint => Location is not null && !CanCommand ? Services.OperableScope.DeniedHint : null;

    /// <summary>Cambió lo que se puede operar: se re-evalúa este nodo y sus sububicaciones.</summary>
    public void RefreshOperable()
    {
        OnPropertyChanged(nameof(CanCommand));
        OnPropertyChanged(nameof(CommandHint));
        foreach (var child in Children.OfType<LocationNode>()) child.RefreshOperable();
    }

    /// <summary>Glifo Segoe MDL2 del tipo de ubicación.</summary>
    public string Glyph => Location?.Kind switch
    {
        LocationKind.Site => "\uE707",
        LocationKind.Building => "\uEC06",
        LocationKind.Floor => "\uE81E",
        LocationKind.Sector => "\uE8B3",
        LocationKind.Point => "\uE81D",
        _ => "\uE9CE",
    };

    /// <summary>Sububicaciones (<see cref="LocationNode"/>) y luego cámaras (<see cref="ChannelNode"/>).</summary>
    public ObservableCollection<object> Children { get; } = [];

    /// <summary>Visible según el buscador del árbol (vacío = todos).</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Selección del TreeView (enlazada al contenedor).</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Cámaras de la ubicación y de todas sus sububicaciones.</summary>
    public IEnumerable<ChannelNode> AllChannels()
    {
        foreach (var child in Children)
        {
            if (child is ChannelNode channel)
                yield return channel;
            else if (child is LocationNode location)
                foreach (var nested in location.AllChannels())
                    yield return nested;
        }
    }

    /// <summary>Cámaras contando las sububicaciones (lo fija quien arma el árbol).</summary>
    public int ChannelCount { get; internal set; }

    public string ToolTipText => ChannelCount == 1
        ? "Doble clic para abrir su cámara"
        : $"Doble clic para abrir sus {ChannelCount} cámaras";
}
