namespace TrueCentralVms.Server.Services.Reports;

/// <summary>Cómo se escribe el valor de una columna (en Excel decide el formato de la celda).</summary>
public enum ReportCellKind { Text, Int, DateTime, Date, Time, Duration, Result }

/// <summary>
/// Columna de un reporte. Un ancho 0 la deja fuera de ese formato: el PDF no
/// tiene lugar para todo lo que cabe en una planilla, y en la planilla no hace
/// falta juntar dos datos en una celda para ahorrar espacio.
/// </summary>
public sealed record ReportColumn(string Header, ReportCellKind Kind, double PdfWidthCm, double ExcelWidth);

/// <summary>Datos comunes del encabezado: quién lo sacó, cuándo y con qué filtros.</summary>
public sealed record ReportMeta(string GeneratedBy, DateTime GeneratedAt, string Filters, string? Truncated);

/// <summary>
/// Un reporte listo para escribir en Excel o PDF: título, columnas, renglones
/// (valores en el orden de las columnas; DateTime en hora LOCAL, TimeSpan para
/// horas y duraciones) y unos totales para el encabezado.
/// </summary>
public sealed record ReportTable(
    string Title,
    string FileName,
    ReportMeta Meta,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    IReadOnlyList<(string Label, string Value)> Totals,
    bool Landscape = true);
