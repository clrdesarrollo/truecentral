using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Recurso que no es cámara en la lista del Centro de mando (puerta, panel de
/// alarma, cerco, citófono, parlante, módulo de E/S…). Se arrastra a un cuadro
/// o se abre con doble clic, igual que una cámara; el cuadro muestra su
/// <see cref="CellPanel"/>.
/// </summary>
public sealed partial class ResourceNode : ObservableObject
{
    public ResourceNode(ResourceKind kind, int id, string name, string? detail, int? locationId,
        int sourceId = 0, ResourceHealth health = ResourceHealth.Unknown, string state = "")
    {
        Kind = kind;
        Id = id;
        Name = name;
        Detail = detail;
        LocationId = locationId;
        SourceId = sourceId;
        _health = health;
        _state = state;
    }

    /// <summary>Tarjeta de reporte (no es un equipo): estado de equipos, servidor, últimas alertas…</summary>
    public static ResourceNode Card(string cardKind, string name, string glyph, string description) =>
        new(ResourceKind.Camera, 0, name, description, null) { CardKind = cardKind, CustomGlyph = glyph };

    /// <summary>Las puertas suelen llamarse igual en cada equipo («Puerta 1»): su detalle es el equipo.</summary>
    public static ResourceNode From(ResourceDto dto) =>
        new(dto.Kind, dto.Id, dto.Name, dto.Kind == ResourceKind.Door ? dto.Source : dto.Detail,
            dto.LocationId, dto.SourceId, dto.Health, dto.State)
        {
            // Puertas y áreas son parte de un equipo; un parlante, citófono,
            // cerco o módulo de E/S es el equipo mismo.
            Equipment = dto.Kind is ResourceKind.Door or ResourceKind.Partition or ResourceKind.Zone ? dto.Source : null,
        };

    public ResourceKind Kind { get; }

    /// <summary>Id del recurso (puerta, área, cerco…). En un panel de alarma
    /// entero (grupo «Paneles de alarma») es el id del panel.</summary>
    public int Id { get; }

    /// <summary>Equipo del que sale (panel de un área, equipo de una puerta).</summary>
    public int SourceId { get; }

    public string Name { get; }
    public string? Detail { get; }
    public int? LocationId { get; }

    /// <summary>Equipo del que es parte (controladora de una puerta, panel de
    /// un área); null si el recurso es el equipo mismo.</summary>
    public string? Equipment { get; init; }

    /// <summary>Clave de la tarjeta (<see cref="CardKinds"/>); null = es un equipo.</summary>
    public string? CardKind { get; init; }

    private string? CustomGlyph { get; init; }

    /// <summary>Panel de alarma entero (no un área suelta).</summary>
    public bool IsWholePanel { get; init; }

    /// <summary>Panel de alarma al que pertenece (áreas y paneles enteros).</summary>
    public int AlarmPanelId => IsWholePanel ? Id : SourceId;

    [ObservableProperty] private ResourceHealth _health;
    [ObservableProperty] private string _state;

    /// <summary>Visible según el buscador de la lista.</summary>
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;

    public string Glyph => CustomGlyph ?? GlyphOf(Kind);

    public string TreeToolTip => CardKind is not null ? $"{Detail}. Doble clic o arrastre a un cuadro"
        : string.Join(" · ", new[] { Detail, State }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } text
        ? $"{text}. Doble clic o arrastre a un cuadro para operarlo"
        : "Doble clic o arrastre a un cuadro para operarlo";

    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(TreeToolTip));

    public static string GlyphOf(ResourceKind kind) => kind switch
    {
        ResourceKind.Door => "\uE8D7",
        ResourceKind.Partition or ResourceKind.Zone => "\uE730",
        ResourceKind.Fence => "\uE945",
        ResourceKind.Speaker => "\uE767",
        ResourceKind.Intercom => "\uE717",
        ResourceKind.IoModule => "\uE950",
        _ => "\uE714",
    };
}

/// <summary>Grupo de la lista por tipo de recurso («Puertas», «Paneles de alarma»…).</summary>
public sealed partial class ResourceGroupNode(string title, string glyph) : ObservableObject
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public ObservableCollection<ResourceNode> Children { get; } = [];

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;

    public string Header => $"{Title} ({Children.Count})";
}
