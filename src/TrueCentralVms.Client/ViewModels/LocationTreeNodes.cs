using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Nodo del árbol por ubicación (página Recursos del servidor): sus hijos son
/// sububicaciones, cámaras y los demás recursos. Las cámaras son los MISMOS
/// <see cref="ChannelNode"/> de <see cref="MainViewModel.Devices"/>, así que el
/// estado en línea, la marca ▶ de "en vivo" y la selección se comparten entre
/// el árbol de Vista en vivo y el de Reproducción.
/// </summary>
/// <param name="location">null = nodo "Sin ubicación" o, con
/// <paramref name="equipment"/>, un equipo dentro de él.</param>
/// <param name="equipment">Nombre del equipo cuyos recursos sin ubicar agrupa
/// (dentro de "Sin ubicación", para que no sea una lista plana).</param>
public sealed partial class LocationNode(LocationDto? location, string? equipment = null) : ObservableObject
{
    /// <summary>null = nodo "Sin ubicación" (lo que nadie ubicó todavía) o un equipo dentro de él.</summary>
    public LocationDto? Location { get; } = location;

    /// <summary>Agrupa lo sin ubicar de un equipo (no es una ubicación).</summary>
    public bool IsEquipment => equipment is not null;

    public string Header => Location?.Name ?? equipment ?? "Sin ubicación";

    /// <summary>Es una ubicación de verdad (no el nodo "Sin ubicación"): admite órdenes.</summary>
    public bool IsRealLocation => Location is not null;

    /// <summary>Sus roles incluyen "Órdenes por ubicación".</summary>
    private static bool MayCommand => Services.PermissionScope.Current.Has(Core.Domain.Permissions.LocationsCommand);

    /// <summary>Admite órdenes, sus roles lo permiten Y está en el alcance de este usuario (las demás se ven, no se operan).</summary>
    public bool CanCommand => Location is { } location && MayCommand && Services.OperableScope.Current.CanOperateLocation(location.Id);

    /// <summary>Por qué no se puede dar órdenes sobre esta ubicación (null = sí se puede).</summary>
    public string? CommandHint => Location is null || CanCommand ? null
        : !MayCommand ? "Sus roles no incluyen \"Órdenes por ubicación\"."
        : Services.OperableScope.DeniedHint;

    /// <summary>Cambió lo que se puede operar: se re-evalúa este nodo y sus sububicaciones.</summary>
    public void RefreshOperable()
    {
        OnPropertyChanged(nameof(CanCommand));
        OnPropertyChanged(nameof(CommandHint));
        foreach (var child in Children.OfType<LocationNode>()) child.RefreshOperable();
    }

    /// <summary>Glifo Segoe MDL2 del tipo de ubicación.</summary>
    public string Glyph => IsEquipment ? "\uE7F4" : Location?.Kind switch
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

    /// <summary>Recursos de todo tipo contando las sububicaciones (el número
    /// junto al nombre, como en Recursos del panel web).</summary>
    public int ItemCount => Children.Sum(c => c is LocationNode location ? location.ItemCount : 1);

    public string ToolTipText => ChannelCount switch
    {
        0 => "Sin cámaras: arrastre sus recursos a un cuadro",
        1 => "Doble clic para abrir su cámara",
        _ => $"Doble clic para abrir sus {ChannelCount} cámaras",
    };
}
