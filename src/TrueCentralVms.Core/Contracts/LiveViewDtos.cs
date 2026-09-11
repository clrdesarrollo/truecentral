namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Vistas guardadas del monitoreo en vivo (las "Custom View" de iVMS-4200):
// la división de la grilla + qué canal estaba en cada cuadro, para volver a
// dejar el puesto como estaba con un clic.
// ---------------------------------------------------------------------------

/// <summary>Un cuadro de la vista: qué canal ocupaba y con qué stream.</summary>
/// <param name="CellIndex">Posición en la grilla (0 = primer cuadro).</param>
/// <param name="StreamType">0 = principal, 1 = secundario (<c>StreamProfile</c>).</param>
public sealed record LiveViewItemDto(int CellIndex, int ChannelId, int StreamType);

/// <summary>Vista guardada tal como la ve el puesto que la pide.</summary>
/// <param name="LayoutName">Nombre de la división estándar ("16", "13") o a
/// medida ("8×5"); <paramref name="Columns"/> y <paramref name="Rows"/>
/// permiten rearmarla aunque no sea una de las del selector.</param>
/// <param name="Shared">Visible para todos los puestos (no solo su dueño).</param>
/// <param name="Owner">Usuario que la creó (vacío si se perdió la cuenta).</param>
/// <param name="CanEdit">El usuario que consulta puede modificarla o borrarla.</param>
public sealed record LiveViewDto(
    int Id, string Name, string LayoutName, int Columns, int Rows,
    bool Shared, string Owner, bool CanEdit, DateTime UpdatedAt,
    IReadOnlyList<LiveViewItemDto> Items);

/// <summary>Alta o actualización de una vista (foto de la grilla del puesto).</summary>
public sealed record LiveViewSaveRequest(
    string Name, string LayoutName, int Columns, int Rows,
    bool Shared, List<LiveViewItemDto> Items);
