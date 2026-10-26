namespace TrueCentralVms.Server.Services.Reports;

/// <summary>
/// Cómo se escribe el valor de una columna (en Excel decide el formato de la celda).
/// <c>Percent</c> va como fracción (0,25 = 25 %), <c>Decimal</c> con un decimal y
/// <c>Heat</c> es un entero que se sombrea según su valor (mapa de calor).
/// </summary>
public enum ReportCellKind { Text, Int, DateTime, Date, Time, Duration, Result, Percent, Decimal, Heat }

/// <summary>
/// Estilo de un renglón. <c>Group</c> es un renglón de agrupación: su primer
/// valor (texto) ocupa todo el ancho, en negrita y sombreado, y los renglones
/// que le siguen son su detalle.
/// </summary>
public enum ReportRowStyle { Normal, Group }

/// <summary>Tono con que se pinta un resultado.</summary>
public enum ReportTone { None, Good, Bad, Warn }

/// <summary>
/// Columna de un reporte. Un ancho 0 la deja fuera de ese formato: el PDF no
/// tiene lugar para todo lo que cabe en una planilla, y en la planilla no hace
/// falta juntar dos datos en una celda para ahorrar espacio.
/// </summary>
public sealed record ReportColumn(string Header, ReportCellKind Kind, double PdfWidthCm, double ExcelWidth);

/// <summary>Datos comunes del encabezado: quién lo sacó, cuándo y con qué filtros.</summary>
public sealed record ReportMeta(string GeneratedBy, DateTime GeneratedAt, string Filters, string? Truncated);

/// <summary>
/// Tabla adicional de un reporte: en PDF va debajo de la principal con su
/// título; en Excel, en una hoja propia. <see cref="PdfOnly"/> y
/// <see cref="ExcelOnly"/> la dejan en un solo formato.
/// </summary>
public sealed record ReportSection(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<object?[]> Rows)
{
    public string? Note { get; init; }
    public IReadOnlyList<ReportRowStyle>? RowStyles { get; init; }
    public bool PdfOnly { get; init; }
    public bool ExcelOnly { get; init; }
    /// <summary>En PDF, empieza en una página nueva.</summary>
    public bool PageBreak { get; init; }
    /// <summary>Nombre de la hoja de Excel (si no, el título).</summary>
    public string? SheetName { get; init; }
    /// <summary>Texto si no hay renglones.</summary>
    public string? EmptyText { get; init; }
    /// <summary>En PDF, la sección se dibuja como este gráfico en vez de la tabla (en Excel va la tabla).</summary>
    public ReportChart? Chart { get; init; }
}

/// <summary>Serie de un gráfico: nombre (leyenda), un valor por categoría y color (RRGGBB).</summary>
public sealed record ReportChartSeries(string Name, IReadOnlyList<double> Values, string ColorHex);

/// <summary>Gráfico de columnas (apiladas o no) para el PDF.</summary>
public sealed record ReportChart(IReadOnlyList<string> Categories, IReadOnlyList<ReportChartSeries> Series,
    double HeightCm = 6.5, bool Stacked = true);

/// <summary>
/// Un reporte listo para escribir en Excel o PDF: título, columnas, renglones
/// (valores en el orden de las columnas; DateTime en hora LOCAL, TimeSpan para
/// horas y duraciones) y unos totales para el encabezado. Opcionalmente, un
/// subtítulo, estilos por renglón y tablas adicionales (<see cref="Sections"/>).
/// </summary>
public sealed record ReportTable(
    string Title,
    string FileName,
    ReportMeta Meta,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    IReadOnlyList<(string Label, string Value)> Totals,
    bool Landscape = true)
{
    /// <summary>Línea bajo el título (p. ej. el periodo del informe).</summary>
    public string? Subtitle { get; init; }
    /// <summary>Estilo de cada renglón de <see cref="Rows"/> (null = todos normales).</summary>
    public IReadOnlyList<ReportRowStyle>? RowStyles { get; init; }
    public IReadOnlyList<ReportSection> Sections { get; init; } = [];
    /// <summary>Título de la tabla principal cuando hay secciones (opcional).</summary>
    public string? MainHeading { get; init; }
    /// <summary>Nombre de la hoja principal en Excel (si no, el título).</summary>
    public string? SheetName { get; init; }
    /// <summary>Texto si la tabla principal no tiene renglones.</summary>
    public string? EmptyText { get; init; }

    /// <summary>Tono con que se pinta un resultado (verde, rojo, ámbar o nada).</summary>
    public static ReportTone ToneOf(string text) =>
        text.StartsWith("Acceso concedido") || text.StartsWith("Apertura automática") || text is "Cumplida" or "A tiempo" or "Marcado" ? ReportTone.Good
        : text.StartsWith("Acceso denegado") || text.StartsWith("Alarma") || text.StartsWith("Rechazado") || text.StartsWith("Falló")
          || text is "No iniciada" or "Incompleta" or "Sin terminar" or "No marcado" ? ReportTone.Bad
        : text is "Tarde" or "Temprano" or "Fuera de orden" ? ReportTone.Warn
        : ReportTone.None;
}
