namespace TrueCentralVms.Client.ViewModels;

/// <summary>Rectángulo de una celda dentro de la cuadrícula del layout
/// (columna/fila de origen y cuántas ocupa).</summary>
public readonly record struct LayoutCell(int Col, int Row, int ColSpan = 1, int RowSpan = 1);

/// <summary>Familia de divisiones del selector (un encabezado y sus miniaturas).</summary>
public sealed class VideoLayoutGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<VideoLayout> Layouts { get; init; }
}

/// <summary>
/// División de pantalla de la grilla de video: cuadrículas uniformes y
/// divisiones asimétricas (un cuadro principal, columnas, filas y
/// combinaciones de cuadros de distinto tamaño). El cuadro i de la grilla
/// ocupa Cells[i].
/// </summary>
public sealed class VideoLayout
{
    /// <summary>Identificador legible y ESTABLE de la división: es lo que se
    /// guarda en la última división usada y en las vistas guardadas (y lo que
    /// lee la bitácora), así que no se cambia una vez publicado.</summary>
    public required string Key { get; init; }
    /// <summary>Rótulo corto de la miniatura y del botón del selector (la
    /// cantidad de cuadros, o columnas×filas en una grilla a medida).</summary>
    public required string Name { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required IReadOnlyList<LayoutCell> Cells { get; init; }

    public int CellCount => Cells.Count;

    /// <summary>Cuadrícula uniforme de columnas × filas en orden de lectura.</summary>
    private static VideoLayout Uniform(int columns, int rows, string? key = null)
    {
        var cells = new List<LayoutCell>(columns * rows);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
                cells.Add(new LayoutCell(col, row));
        string name = (columns * rows).ToString();
        return new VideoLayout { Key = key ?? name, Name = name, Columns = columns, Rows = rows, Cells = cells };
    }

    /// <summary>1 cuadro grande de (side-1)×(side-1) arriba a la izquierda y
    /// el resto de la L en cuadros simples (6, 8, 10, 12 y 16 con principal).</summary>
    private static VideoLayout OneBig(int side)
    {
        var cells = new List<LayoutCell> { new(0, 0, side - 1, side - 1) };
        for (int row = 0; row < side - 1; row++)
            cells.Add(new LayoutCell(side - 1, row));
        for (int col = 0; col < side; col++)
            cells.Add(new LayoutCell(col, side - 1));
        return new VideoLayout
        {
            Key = $"{2 * side} con principal", Name = (2 * side).ToString(),
            Columns = side, Rows = side, Cells = cells,
        };
    }

    /// <summary>
    /// División dibujada como mapa de caracteres: cada fila del arreglo es una
    /// fila de la cuadrícula y cada carácter distinto un cuadro (que debe
    /// formar un rectángulo). El orden de los cuadros es el de lectura según
    /// su esquina superior izquierda, así el principal queda primero.
    /// </summary>
    private static VideoLayout Map(string key, params string[] rows)
    {
        int columns = rows[0].Length;
        var order = new List<char>();
        var bounds = new Dictionary<char, (int Col, int Row, int MaxCol, int MaxRow)>();
        for (int row = 0; row < rows.Length; row++)
        {
            if (rows[row].Length != columns)
                throw new InvalidOperationException($"División '{key}': la fila {row} no mide {columns}.");
            for (int col = 0; col < columns; col++)
            {
                char c = rows[row][col];
                if (bounds.TryGetValue(c, out var b))
                    bounds[c] = (Math.Min(b.Col, col), b.Row, Math.Max(b.MaxCol, col), row);
                else
                {
                    order.Add(c);
                    bounds[c] = (col, row, col, row);
                }
            }
        }
        var cells = new List<LayoutCell>(order.Count);
        foreach (char c in order)
        {
            var b = bounds[c];
            int colSpan = b.MaxCol - b.Col + 1, rowSpan = b.MaxRow - b.Row + 1;
            for (int row = b.Row; row <= b.MaxRow; row++)
                if (rows[row].Substring(b.Col, colSpan).Any(x => x != c))
                    throw new InvalidOperationException($"División '{key}': el cuadro '{c}' no es un rectángulo.");
            cells.Add(new LayoutCell(b.Col, b.Row, colSpan, rowSpan));
        }
        return new VideoLayout
        {
            Key = key, Name = cells.Count.ToString(),
            Columns = columns, Rows = rows.Length, Cells = cells,
        };
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
        return Grid(bestCols, bestRows);
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
            Key = $"{columns}×{rows}",
            Name = $"{columns}×{rows}",
            Columns = columns,
            Rows = rows,
            Cells = cells,
        };
    }

    /// <summary>Claves con que se guardaron divisiones antes de que el selector
    /// tuviera familias (vistas guardadas y client.json de versiones previas).</summary>
    private static readonly Dictionary<string, string> LegacyKeys = new()
    {
        ["6"] = "6 con principal",
        ["8"] = "8 con principal",
        ["13"] = "13 combinada",
    };

    /// <summary>División del selector con esa clave (acepta las claves antiguas).</summary>
    public static VideoLayout? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (LegacyKeys.TryGetValue(key, out var current)) key = current;
        return Standard.FirstOrDefault(l => l.Key == key);
    }

    /// <summary>
    /// Rearma la división guardada en una vista: si la clave es una de las del
    /// selector se usa esa (conserva las asimétricas), y si no —una grilla a
    /// medida de las que produce <see cref="FitFor"/>— se reconstruye del
    /// tamaño guardado.
    /// </summary>
    public static VideoLayout Restore(string? key, int columns, int rows) =>
        Find(key) ?? Grid(columns, rows);

    /// <summary>
    /// División del selector más chica que da abasto a <paramref name="count"/>
    /// cuadros, para abrir un equipo completo cuando no se usa la grilla a
    /// medida. Solo las uniformes y las de cuadro principal: las de columnas,
    /// filas y combinadas dejan cuadros de tamaños muy dispares para mostrar
    /// un equipo entero.
    /// </summary>
    public static VideoLayout SmallestFor(int count) =>
        Groups[0].Layouts.Concat(Groups[1].Layouts)
            .Where(l => l.CellCount >= count)
            .MinBy(l => l.CellCount)
        ?? Groups[0].Layouts[^1];

    /// <summary>Familias del selector, en su orden de presentación.</summary>
    public static readonly IReadOnlyList<VideoLayoutGroup> Groups =
    [
        new()
        {
            Title = "Uniformes",
            Layouts =
            [
                Uniform(1, 1), Uniform(2, 2), Uniform(3, 3), Uniform(4, 4),
                Uniform(5, 5), Uniform(6, 6), Uniform(8, 8),
            ],
        },
        new()
        {
            Title = "Con cuadro principal",
            Layouts =
            [
                OneBig(3),                                           // 6
                OneBig(4),                                           // 8
                Map("9 con principal", "AABC", "AADE", "FGHI"),
                OneBig(5),                                           // 10
                OneBig(6),                                           // 12
                OneBig(8),                                           // 16
                Map("17 con principal", "AAABC", "AAADE", "AAAFG", "HIJKL", "MNOPQ"),
            ],
        },
        new()
        {
            Title = "En columnas",
            Layouts =
            [
                Uniform(2, 1, "2 en columnas"),
                Map("3 en columnas", "AB", "AC"),
                Map("5 en columnas", "AABC", "AADE"),
                Uniform(3, 2, "6 en columnas"),
                Uniform(4, 2, "8 en columnas"),
            ],
        },
        new()
        {
            Title = "En filas",
            Layouts =
            [
                Uniform(1, 2, "2 en filas"),
                Map("3 en filas", "AA", "BC"),
                Map("5 en filas", "AA", "AA", "BC", "DE"),
                Uniform(2, 3, "6 en filas"),
                Uniform(2, 4, "8 en filas"),
            ],
        },
        new()
        {
            Title = "Combinadas",
            Layouts =
            [
                Map("4 combinada A", "AB", "AC", "AD"),
                Map("4 combinada B", "AAA", "BCD"),
                Map("5 combinada", "AB", "AC", "AD", "AE"),
                Map("6 combinada", "AB", "AB", "CD", "EF"),
                Map("7 combinada A", "AABB", "AABB", "CCDE", "CCFG"),
                Map("7 combinada B",
                    "AAABBBCC",
                    "AAABBBCC",
                    "AAABBBDD",
                    "EEEFFFDD",
                    "EEEFFFGG",
                    "EEEFFFGG"),
                Uniform(3, 4, "12 combinada"),
                Map("13 combinada", "ABCD", "EFFG", "HFFI", "JKLM"),
                Map("24 combinada",
                    "AABBCCDD",
                    "AABBCCDD",
                    "EEFFGGHH",
                    "EEFFGGHH",
                    "IJKLMNOP",
                    "QRSTUVWX"),
                Map("32 combinada",
                    "AABBCDE",
                    "AABBFGH",
                    "IIJJJKL",
                    "IIJJJMN",
                    "OPJJJQR",
                    "STUVWXY",
                    "Zabcdef"),
                Map("36 combinada",
                    "AABBCCDD",
                    "AABBCCDD",
                    "EFGHIJKL",
                    "MNOPQRST",
                    "UVWXYZab",
                    "cdefghij"),
                Uniform(8, 6, "48 combinada"),
            ],
        },
    ];

    /// <summary>Todas las divisiones del selector, en su orden.</summary>
    public static readonly IReadOnlyList<VideoLayout> Standard =
        Groups.SelectMany(g => g.Layouts).ToList();

    /// <summary>División inicial de la grilla (2×2).</summary>
    public static VideoLayout Default => Standard[1];
}
