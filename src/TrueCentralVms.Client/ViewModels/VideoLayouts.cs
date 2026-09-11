namespace TrueCentralVms.Client.ViewModels;

/// <summary>Rectángulo de una celda dentro de la cuadrícula del layout
/// (columna/fila de origen y cuántas ocupa).</summary>
public readonly record struct LayoutCell(int Col, int Row, int ColSpan = 1, int RowSpan = 1);

/// <summary>
/// División de pantalla de la grilla de video, estilo iVMS-4200: además de
/// las cuadrículas uniformes (1/4/9/16/25/36/64) hay divisiones asimétricas
/// con un cuadro grande y varios chicos (6, 8, 13). El cuadro i de la grilla
/// ocupa Cells[i].
/// </summary>
public sealed class VideoLayout
{
    public required string Name { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required IReadOnlyList<LayoutCell> Cells { get; init; }

    public int CellCount => Cells.Count;

    /// <summary>Cuadrícula uniforme n×n en orden de lectura.</summary>
    private static VideoLayout Uniform(int side)
    {
        var cells = new List<LayoutCell>(side * side);
        for (int row = 0; row < side; row++)
            for (int col = 0; col < side; col++)
                cells.Add(new LayoutCell(col, row));
        return new VideoLayout { Name = (side * side).ToString(), Columns = side, Rows = side, Cells = cells };
    }

    /// <summary>1 cuadro grande de (side-1)×(side-1) arriba a la izquierda y
    /// el resto de la L en cuadros simples (divisiones 6 y 8 de iVMS).</summary>
    private static VideoLayout OneBig(int side)
    {
        var cells = new List<LayoutCell> { new(0, 0, side - 1, side - 1) };
        for (int row = 0; row < side - 1; row++)
            cells.Add(new LayoutCell(side - 1, row));
        for (int col = 0; col < side; col++)
            cells.Add(new LayoutCell(col, side - 1));
        return new VideoLayout { Name = (2 * side).ToString(), Columns = side, Rows = side, Cells = cells };
    }

    /// <summary>4×4 con un cuadro grande de 2×2 al centro (división 13 de iVMS).</summary>
    private static VideoLayout ThirteenCentered()
    {
        var cells = new List<LayoutCell>();
        for (int col = 0; col < 4; col++) cells.Add(new LayoutCell(col, 0));
        cells.Add(new LayoutCell(0, 1));
        cells.Add(new LayoutCell(1, 1, 2, 2)); // el grande, al centro
        cells.Add(new LayoutCell(3, 1));
        cells.Add(new LayoutCell(0, 2));
        cells.Add(new LayoutCell(3, 2));
        for (int col = 0; col < 4; col++) cells.Add(new LayoutCell(col, 3));
        return new VideoLayout { Name = "13", Columns = 4, Rows = 4, Cells = cells };
    }

    /// <summary>
    /// Cuadrícula a medida para abrir un equipo completo sin cuadros de sobra:
    /// la combinación columnas×filas con el mínimo de celdas sobrantes que
    /// contenga <paramref name="count"/> cuadros, prefiriendo formas parejas
    /// (algo más anchas que altas, como los monitores). Ej.: 40 → 8×5 exacto,
    /// 15 → 5×3 exacto, 13 → 5×3 con 2 libres.
    /// </summary>
    public static VideoLayout FitFor(int count)
    {
        count = Math.Clamp(count, 1, 64);
        int bestCols = 1, bestRows = count;
        long bestScore = long.MaxValue;
        for (int cols = 1; cols <= count; cols++)
        {
            int rows = (count + cols - 1) / cols;
            if (cols < rows) continue;         // pantallas anchas: columnas >= filas
            if (cols > rows * 2 + 1) continue; // sin tiras demasiado alargadas
            long score = (cols * rows - count) * 100L + (cols - rows); // 1º mínimo sobrante, 2º lo más cuadrado
            if (score < bestScore)
            {
                bestScore = score;
                bestCols = cols;
                bestRows = rows;
            }
        }
        var cells = new List<LayoutCell>(bestCols * bestRows);
        for (int row = 0; row < bestRows; row++)
            for (int col = 0; col < bestCols; col++)
                cells.Add(new LayoutCell(col, row));
        return new VideoLayout { Name = $"{bestCols}×{bestRows}", Columns = bestCols, Rows = bestRows, Cells = cells };
    }

    /// <summary>Cuadrícula uniforme de columnas × filas, en orden de lectura.</summary>
    public static VideoLayout Grid(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 16);
        rows = Math.Clamp(rows, 1, 16);
        var cells = new List<LayoutCell>(columns * rows);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
                cells.Add(new LayoutCell(col, row));
        return new VideoLayout
        {
            Name = $"{columns}×{rows}",
            Columns = columns,
            Rows = rows,
            Cells = cells,
        };
    }

    /// <summary>
    /// Rearma la división guardada en una vista: si el nombre es una de las
    /// estándar se usa esa (conserva las asimétricas 6/8/13), y si no —una
    /// grilla a medida de las que produce <see cref="FitFor"/>— se reconstruye
    /// del tamaño guardado.
    /// </summary>
    public static VideoLayout Restore(string? name, int columns, int rows) =>
        Standard.FirstOrDefault(l => l.Name == name) ?? Grid(columns, rows);

    /// <summary>Divisiones estándar, en el orden del selector.</summary>
    public static readonly IReadOnlyList<VideoLayout> Standard =
    [
        Uniform(1),          // 1
        Uniform(2),          // 4
        OneBig(3),           // 6 = 1 grande + 5
        OneBig(4),           // 8 = 1 grande + 7
        Uniform(3),          // 9
        ThirteenCentered(),  // 13 = 1 grande centrado + 12
        Uniform(4),          // 16
        Uniform(5),          // 25
        Uniform(6),          // 36
        Uniform(8),          // 64
    ];

    /// <summary>División inicial de la grilla (2×2).</summary>
    public static VideoLayout Default => Standard[1];
}
