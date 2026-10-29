namespace TrueCentralVms.Client.ViewModels;

/// <summary>Rectángulo de una celda dentro de la cuadrícula del layout
/// (columna/fila de origen y cuántas ocupa).</summary>
public readonly record struct LayoutCell(int Col, int Row, int ColSpan = 1, int RowSpan = 1);

/// <summary>Borde de la grilla donde se agrega una franja nueva.</summary>
public enum GridEdge { None, Left, Top, Right, Bottom }

/// <summary>Resultado de ampliar una división (ver <see cref="VideoLayout.Grow"/>).</summary>
public sealed record LayoutGrowth(VideoLayout Layout, IReadOnlyList<int> Placement, IReadOnlyList<int> StripCells);

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

    /// <summary>Cómo se nombra la división en los textos (vistas guardadas).</summary>
    public string Label => Key.StartsWith(CustomPrefix) ? $"a medida {Columns}×{Rows}" : Key;

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

    /// <summary>División del selector con esa clave (acepta las claves antiguas,
    /// las de una división a medida: "m43…" y las uniformes "C×R").</summary>
    public static VideoLayout? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith(CustomPrefix)) return Decode(key);
        if (LegacyKeys.TryGetValue(key, out var current)) key = current;
        if (Standard.FirstOrDefault(l => l.Key == key) is { } standard) return standard;
        // Las uniformes que no son del selector horizontal (las del vertical, "3×5").
        var parts = key.Split('×');
        return parts.Length == 2 && int.TryParse(parts[0], out int columns) && int.TryParse(parts[1], out int rows) &&
               columns is >= 1 and <= 16 && rows is >= 1 and <= 16
            ? Grid(columns, rows) : null;
    }

    // ------------------------------------------------------------------
    // Ampliar la grilla: soltar una cámara en un borde agrega una franja
    // (fila o columna) de ese lado. Los cuadros existentes conservan su
    // forma y su lugar relativo; los de la franja son de una celda.
    // ------------------------------------------------------------------

    /// <summary>Topes de la grilla ampliada: los de la división más grande del
    /// selector (8×8) y los 64 cuadros que aceptan las vistas guardadas.</summary>
    public const int MaxGrowColumns = 8, MaxGrowRows = 8, MaxGrowCells = 64;

    /// <summary>Por qué no se puede agregar una franja en ese borde (null = se puede).</summary>
    public string? GrowBlockedReason(GridEdge edge, int maxCells)
    {
        bool sideways = edge is GridEdge.Left or GridEdge.Right;
        if (sideways && Columns >= MaxGrowColumns)
            return $"La grilla ya tiene {MaxGrowColumns} columnas, el máximo.";
        if (!sideways && Rows >= MaxGrowRows)
            return $"La grilla ya tiene {MaxGrowRows} filas, el máximo.";
        int count = CellCount + (sideways ? Rows : Columns);
        if (count > Math.Min(maxCells, MaxGrowCells))
            return maxCells < MaxGrowCells
                ? $"Con {count} cuadros la grilla superaría las {maxCells} cámaras disponibles."
                : $"La grilla no puede pasar de {MaxGrowCells} cuadros.";
        return null;
    }

    /// <summary>
    /// La división con una franja más en <paramref name="edge"/>, o null si se
    /// pasa de los topes. Los cuadros quedan en orden de lectura; Placement[i]
    /// dice dónde quedó el cuadro i actual y StripCells son los de la franja,
    /// de izquierda a derecha o de arriba abajo.
    /// </summary>
    public LayoutGrowth? Grow(GridEdge edge, int maxCells)
    {
        if (edge == GridEdge.None || GrowBlockedReason(edge, maxCells) is not null) return null;
        bool sideways = edge is GridEdge.Left or GridEdge.Right;
        int columns = Columns + (sideways ? 1 : 0), rows = Rows + (sideways ? 0 : 1);
        int dx = edge == GridEdge.Left ? 1 : 0, dy = edge == GridEdge.Top ? 1 : 0;

        // Origen de cada cuadro: >= 0 un cuadro actual, < 0 la franja (~k).
        var all = Cells.Select((c, i) => (Cell: c with { Col = c.Col + dx, Row = c.Row + dy }, From: i)).ToList();
        int stripLength = sideways ? Rows : Columns;
        for (int k = 0; k < stripLength; k++)
            all.Add((sideways
                ? new LayoutCell(edge == GridEdge.Left ? 0 : Columns, k)
                : new LayoutCell(k, edge == GridEdge.Top ? 0 : Rows), ~k));
        var ordered = all.OrderBy(x => x.Cell.Row).ThenBy(x => x.Cell.Col).ToList();

        var placement = new int[CellCount];
        var strip = new int[stripLength];
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].From >= 0) placement[ordered[i].From] = i;
            else strip[~ordered[i].From] = i;
        }

        var layout = FromCells(columns, rows, ordered.Select(x => x.Cell).ToList());
        return layout is null ? null : new LayoutGrowth(layout, placement, strip);
    }

    // ------------------------------------------------------------------
    // Divisiones a medida (grilla ampliada, con cuadros unidos o con un
    // cuadro subdividido). Clave compacta: "m" + columnas + filas + 2
    // caracteres por cada cuadro de más de una celda (columna, fila, ancho-1
    // y alto-1 en 3 bits cada uno); las celdas libres son cuadros simples.
    // Cabe en los 64 caracteres de los tableros con hasta 30 cuadros grandes
    // (antes 32 y 14: subdividir un cuadro de una grilla de 16 deja 15). La
    // web (live.js) entiende la misma clave.
    // ------------------------------------------------------------------
    private const string CustomPrefix = "m";
    private const string CustomAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    public const int MaxBigCells = 30;

    /// <summary>
    /// La división de esos cuadros (quedan en orden de lectura): la del
    /// selector si coincide con una, "C×R" si es uniforme o una a medida; null
    /// si tiene más cuadros grandes de los que caben en la clave.
    /// </summary>
    public static VideoLayout? FromCells(int columns, int rows, List<LayoutCell> cells)
    {
        cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));
        var standard = Standard.FirstOrDefault(l => l.Columns == columns && l.Rows == rows && l.Cells.SequenceEqual(cells));
        if (standard is not null) return standard;
        var big = cells.Where(c => c.ColSpan > 1 || c.RowSpan > 1).ToList();
        if (big.Count == 0 && cells.Count == columns * rows) return Grid(columns, rows);
        if (big.Count > MaxBigCells || columns > 8 || rows > 8) return null;
        var key = new System.Text.StringBuilder(CustomPrefix).Append(columns).Append(rows);
        foreach (var c in big)
        {
            int v = c.Col << 9 | c.Row << 6 | (c.ColSpan - 1) << 3 | (c.RowSpan - 1);
            key.Append(CustomAlphabet[v >> 6]).Append(CustomAlphabet[v & 63]);
        }
        return new VideoLayout
        {
            Key = key.ToString(), Name = cells.Count.ToString(),
            Columns = columns, Rows = rows, Cells = cells,
        };
    }

    /// <summary>Rearma una división a medida desde su clave (null si no es válida).</summary>
    private static VideoLayout? Decode(string key)
    {
        if (key.Length < 3 || (key.Length - 3) % 2 != 0) return null;
        int columns = key[1] - '0', rows = key[2] - '0';
        if (columns is < 1 or > 8 || rows is < 1 or > 8) return null;
        var taken = new bool[columns, rows];
        var cells = new List<LayoutCell>();
        for (int i = 3; i < key.Length; i += 2)
        {
            int hi = CustomAlphabet.IndexOf(key[i]), lo = CustomAlphabet.IndexOf(key[i + 1]);
            if (hi < 0 || lo < 0) return null;
            int v = hi << 6 | lo;
            var c = new LayoutCell(v >> 9 & 7, v >> 6 & 7, (v >> 3 & 7) + 1, (v & 7) + 1);
            if (c.Col + c.ColSpan > columns || c.Row + c.RowSpan > rows) return null;
            for (int x = c.Col; x < c.Col + c.ColSpan; x++)
                for (int y = c.Row; y < c.Row + c.RowSpan; y++)
                {
                    if (taken[x, y]) return null;
                    taken[x, y] = true;
                }
            cells.Add(c);
        }
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
                if (!taken[x, y]) cells.Add(new LayoutCell(x, y));
        return FromCells(columns, rows, cells);
    }

    // ------------------------------------------------------------------
    // Unir y separar cuadros: seleccionar con el puntero de un cuadro a otro
    // une todo el rectángulo en un solo cuadro, siempre que no corte a otro
    // cuadro por la mitad (un cuadro grande que asoma afuera, o una L).
    // ------------------------------------------------------------------

    private static bool Overlaps(LayoutCell a, LayoutCell b) =>
        a.Col < b.Col + b.ColSpan && b.Col < a.Col + a.ColSpan && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;

    private static bool Contains(LayoutCell outer, LayoutCell inner) =>
        inner.Col >= outer.Col && inner.Row >= outer.Row &&
        inner.Col + inner.ColSpan <= outer.Col + outer.ColSpan && inner.Row + inner.RowSpan <= outer.Row + outer.RowSpan;

    /// <summary>
    /// Rectángulo que une los cuadros <paramref name="from"/> y <paramref name="to"/>:
    /// los cuadros que quedan adentro y los que lo cortan (si hay alguno, no
    /// se puede unir).
    /// </summary>
    public (LayoutCell Rect, List<int> Inside, List<int> Cut) MergeSelection(int from, int to)
    {
        var a = Cells[from];
        var b = Cells[to];
        int col = Math.Min(a.Col, b.Col), row = Math.Min(a.Row, b.Row);
        var rect = new LayoutCell(col, row,
            Math.Max(a.Col + a.ColSpan, b.Col + b.ColSpan) - col,
            Math.Max(a.Row + a.RowSpan, b.Row + b.RowSpan) - row);
        var inside = new List<int>();
        var cut = new List<int>();
        for (int i = 0; i < Cells.Count; i++)
        {
            if (Contains(rect, Cells[i])) inside.Add(i);
            else if (Overlaps(rect, Cells[i])) cut.Add(i);
        }
        return (rect, inside, cut);
    }

    /// <summary>
    /// Une los cuadros de <paramref name="from"/> a <paramref name="to"/>. Los
    /// cuadros quedan en orden de lectura: Placement[i] es el lugar nuevo del
    /// cuadro i (los que se unen van todos al cuadro unido, StripCells[0]).
    /// </summary>
    public LayoutGrowth? Merge(int from, int to, out string? reason)
    {
        var (rect, inside, cut) = MergeSelection(from, to);
        reason = cut.Count > 0 ? "La selección corta un cuadro por la mitad: tiene que formar un rectángulo completo."
            : inside.Count < 2 ? "Arrastre hasta otro cuadro para unirlos."
            : null;
        if (reason is not null) return null;
        var layout = FromCells(Columns, Rows, Cells.Where((_, i) => !inside.Contains(i)).Append(rect).ToList());
        if (layout is null)
        {
            reason = $"La grilla ya tiene {MaxBigCells} cuadros unidos, el máximo.";
            return null;
        }
        int merged = IndexOf(layout, rect);
        var placement = Cells.Select((c, i) => inside.Contains(i) ? merged : IndexOf(layout, c)).ToArray();
        return new LayoutGrowth(layout, placement, [merged]);
    }

    /// <summary>Separa un cuadro unido en cuadros simples (la cámara queda en
    /// el de arriba a la izquierda, StripCells[0]); null si ya es simple.</summary>
    public LayoutGrowth? Split(int index)
    {
        var big = Cells[index];
        if (big.ColSpan == 1 && big.RowSpan == 1) return null;
        var cells = Cells.Where((_, i) => i != index).ToList();
        for (int y = big.Row; y < big.Row + big.RowSpan; y++)
            for (int x = big.Col; x < big.Col + big.ColSpan; x++)
                cells.Add(new LayoutCell(x, y));
        var layout = FromCells(Columns, Rows, cells)!; // separar nunca agrega cuadros grandes
        var placement = Cells.Select((c, i) => IndexOf(layout, i == index ? new LayoutCell(big.Col, big.Row) : c)).ToArray();
        return new LayoutGrowth(layout, placement, [placement[index]]);
    }

    // ------------------------------------------------------------------
    // Subdividir un cuadro: el cuadro pasa a ser across×down cuadros del
    // mismo tamaño entre sí (para poner más de una cámara donde había una).
    // Si su tamaño no se parte justo, la cuadrícula entera se refina (cada
    // celda pasa a ser 2×2, por ejemplo) y los demás cuadros conservan su
    // forma y su lugar: son cuadros normales, se unen y se separan igual.
    // ------------------------------------------------------------------

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>
    /// La división con el cuadro <paramref name="index"/> partido en
    /// <paramref name="across"/>×<paramref name="down"/>, o null (con el
    /// porqué) si se pasa de los topes. Placement[i] es el lugar nuevo del
    /// cuadro i (el subdividido queda en su primer pedazo, arriba a la
    /// izquierda) y StripCells son los pedazos en orden de lectura.
    /// </summary>
    public LayoutGrowth? Subdivide(int index, int across, int down, out string? reason)
    {
        var cell = Cells[index];
        // Cuánto hay que refinar la cuadrícula para que el cuadro se parta justo.
        int fx = across / Gcd(cell.ColSpan, across), fy = down / Gcd(cell.RowSpan, down);
        int columns = Columns * fx, rows = Rows * fy;
        int count = CellCount - 1 + across * down;
        reason = columns > MaxGrowColumns || rows > MaxGrowRows
                ? $"La grilla quedaría de {columns}×{rows} celdas y el máximo es {MaxGrowColumns}×{MaxGrowRows}: " +
                  "subdivida un cuadro más grande o use una división con menos cuadros."
            : count > MaxGrowCells ? $"La grilla no puede pasar de {MaxGrowCells} cuadros."
            : null;
        if (reason is not null) return null;

        var scaled = Cells.Select(c => new LayoutCell(c.Col * fx, c.Row * fy, c.ColSpan * fx, c.RowSpan * fy)).ToList();
        var big = scaled[index];
        int w = big.ColSpan / across, h = big.RowSpan / down;
        var parts = new List<LayoutCell>();
        for (int y = 0; y < down; y++)
            for (int x = 0; x < across; x++)
                parts.Add(new LayoutCell(big.Col + x * w, big.Row + y * h, w, h));
        var layout = FromCells(columns, rows, scaled.Where((_, i) => i != index).Concat(parts).ToList());
        if (layout is null)
        {
            reason = $"La grilla quedaría con más de {MaxBigCells} cuadros de distinto tamaño, el máximo.";
            return null;
        }
        var placement = scaled.Select((c, i) => IndexOf(layout, i == index ? parts[0] : c)).ToArray();
        return new LayoutGrowth(layout, placement, parts.Select(p => IndexOf(layout, p)).ToList());
    }

    private static int IndexOf(VideoLayout layout, LayoutCell cell)
    {
        for (int i = 0; i < layout.Cells.Count; i++)
            if (layout.Cells[i] == cell) return i;
        throw new InvalidOperationException("El cuadro no está en la división nueva.");
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

    // ------------------------------------------------------------------
    // Monitores verticales: el selector cambia a divisiones más altas que
    // anchas (más filas que columnas, el cuadro principal arriba). Se guardan
    // con su clave de siempre: la del selector si coincide con una, "C×R" si
    // es uniforme o "m…" a medida, así los tableros y la web las entienden.
    // ------------------------------------------------------------------

    /// <summary>La división de esos cuadros con el número de cuadros como rótulo de la miniatura.</summary>
    private static VideoLayout Portrait(int columns, int rows, List<LayoutCell> cells)
    {
        var layout = FromCells(columns, rows, cells)
                     ?? throw new InvalidOperationException($"División vertical {columns}×{rows} sin clave.");
        // Las del selector horizontal se usan tal cual (la miniatura activa se marca por clave igual).
        return Standard.Contains(layout) ? layout : new VideoLayout
        {
            Key = layout.Key, Name = layout.CellCount.ToString(),
            Columns = layout.Columns, Rows = layout.Rows, Cells = layout.Cells,
        };
    }

    private static VideoLayout PortraitGrid(int columns, int rows) =>
        Portrait(columns, rows, Grid(columns, rows).Cells.ToList());

    private static VideoLayout PortraitMap(params string[] rows)
    {
        var map = Map("vertical", rows);
        return Portrait(map.Columns, map.Rows, map.Cells.ToList());
    }

    /// <summary>La misma división girada (columnas ↔ filas).</summary>
    private static VideoLayout Transposed(VideoLayout layout) =>
        Portrait(layout.Rows, layout.Columns,
            layout.Cells.Select(c => new LayoutCell(c.Row, c.Col, c.RowSpan, c.ColSpan)).ToList());

    /// <summary>Familias del selector para monitores verticales.</summary>
    public static readonly IReadOnlyList<VideoLayoutGroup> VerticalGroups = BuildVerticalGroups();

    /// <summary>
    /// Cada división aparece una sola vez (en la primera familia que la
    /// tiene: 2×3 es uniforme y también «6 en filas») y las combinadas giradas
    /// quedan solo si salen más altas que anchas.
    /// </summary>
    private static List<VideoLayoutGroup> BuildVerticalGroups()
    {
        var seen = new HashSet<string>();
        VideoLayoutGroup Group(string title, IEnumerable<VideoLayout> layouts) =>
            new() { Title = title, Layouts = layouts.Where(l => seen.Add(l.Key)).ToList() };
        return
        [
            Group("Uniformes",
            [
                PortraitGrid(1, 1), PortraitGrid(1, 2), PortraitGrid(1, 3), PortraitGrid(2, 2),
                PortraitGrid(2, 3), PortraitGrid(2, 4), PortraitGrid(3, 3), PortraitGrid(3, 4),
                PortraitGrid(3, 5), PortraitGrid(4, 4), PortraitGrid(3, 6), PortraitGrid(4, 6),
                PortraitGrid(4, 8), PortraitGrid(6, 8),
            ]),
            Group("Con cuadro principal arriba",
            [
                PortraitMap("AA", "AA", "BC"),
                PortraitMap("AAA", "AAA", "BCD"),
                PortraitMap("AA", "AA", "BC", "DE"),
                Groups[1].Layouts[0], // 6 con principal: sirve igual parado
                PortraitMap("AAA", "AAA", "BCD", "EFG"),
                Groups[1].Layouts[1], // 8 con principal
                PortraitMap("AAA", "AAA", "AAA", "BCD", "EFG", "HIJ"),
                PortraitMap("AAAA", "AAAA", "AAAA", "BCDE", "FGHI", "JKLM"),
                PortraitMap("AAAA", "AAAA", "BCDE", "FGHI", "JKLM", "NOPQ"),
            ]),
            Group("En filas", Groups[3].Layouts.Concat([PortraitGrid(1, 4), PortraitMap("AA", "AA", "BB", "BB", "CD")])),
            Group("Combinadas", Groups[4].Layouts.Select(Transposed).Where(l => l.Rows >= l.Columns)),
        ];
    }

    /// <summary>Familias del selector según la orientación del monitor.</summary>
    public static IReadOnlyList<VideoLayoutGroup> GroupsFor(bool vertical) => vertical ? VerticalGroups : Groups;

    /// <summary>División inicial de la grilla (2×2).</summary>
    public static VideoLayout Default => Standard[1];
}
