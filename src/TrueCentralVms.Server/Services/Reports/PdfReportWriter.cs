using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace TrueCentralVms.Server.Services.Reports;

/// <summary>
/// Escribe un <see cref="ReportTable"/> como PDF con MigraDoc (PDFsharp, MIT):
/// A4 apaisado, logo y título arriba, quién lo sacó y con qué filtros, la
/// tabla con su fila de títulos repetida en cada página y "Página X de Y" al
/// pie. Las fuentes son las de Windows (el servidor siempre corre en Windows).
/// </summary>
public static class PdfReportWriter
{
    private const string Font = "Arial";
    private static readonly Color Navy = new(0x1F, 0x3A, 0x5F);
    private static readonly Color Stripe = new(0xF3, 0xF5, 0xF8);
    private static readonly Color Grid = new(0xD0, 0xD7, 0xDE);
    private static readonly Color Green = new(0x1E, 0x7B, 0x34);
    private static readonly Color Red = new(0xC0, 0x39, 0x2B);
    private static readonly Color Gray = new(0x55, 0x55, 0x55);

    private static readonly object FontSetup = new();
    private static bool _fontsReady;

    public static byte[] Write(ReportTable table, string? logoPath)
    {
        lock (FontSetup)
        {
            if (!_fontsReady)
            {
                GlobalFontSettings.UseWindowsFontsUnderWindows = true;
                _fontsReady = true;
            }
        }

        var doc = new Document();
        doc.Info.Title = table.Title;
        doc.Info.Author = "CLR TrueCentral VMS";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 8;

        var section = doc.AddSection();
        var setup = section.PageSetup;
        setup.PageFormat = PageFormat.A4;
        setup.Orientation = table.Landscape ? Orientation.Landscape : Orientation.Portrait;
        setup.LeftMargin = setup.RightMargin = Unit.FromCentimeter(1.5);
        setup.TopMargin = Unit.FromCentimeter(1.4);
        setup.BottomMargin = Unit.FromCentimeter(1.5);
        setup.FooterDistance = Unit.FromCentimeter(0.7);

        // Pie: generado + página X de Y.
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Gray;
        footer.AddText($"{table.Title} · generado por {table.Meta.GeneratedBy} el {table.Meta.GeneratedAt:dd-MM-yyyy HH:mm} · CLR TrueCentral VMS");
        footer.AddTab();
        footer.AddText("Página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();
        double usable = (table.Landscape ? 29.7 : 21.0) - 3.0;
        footer.Format.TabStops.AddTabStop(Unit.FromCentimeter(usable), TabAlignment.Right);

        // Encabezado: logo + título, y debajo los filtros y los totales.
        var head = section.AddTable();
        head.AddColumn(Unit.FromCentimeter(2.0));
        head.AddColumn(Unit.FromCentimeter(usable - 2.0));
        var headRow = head.AddRow();
        headRow.VerticalAlignment = VerticalAlignment.Center;
        if (logoPath is not null && File.Exists(logoPath))
        {
            var image = headRow.Cells[0].AddImage(logoPath);
            image.Height = Unit.FromCentimeter(1.2);
            image.LockAspectRatio = true;
        }
        var title = headRow.Cells[1].AddParagraph(table.Title);
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Navy;
        var by = headRow.Cells[1].AddParagraph($"Generado por {table.Meta.GeneratedBy} el {table.Meta.GeneratedAt:dd-MM-yyyy HH:mm}");
        by.Format.Font.Size = 8;
        by.Format.Font.Color = Gray;

        var filters = section.AddParagraph();
        filters.Format.SpaceBefore = Unit.FromPoint(8);
        filters.AddFormattedText("Filtros: ", TextFormat.Bold);
        filters.AddText(table.Meta.Filters);

        var totals = section.AddParagraph();
        totals.Format.SpaceBefore = Unit.FromPoint(3);
        for (int i = 0; i < table.Totals.Count; i++)
        {
            if (i > 0) totals.AddText("     ");
            totals.AddText(table.Totals[i].Label + ": ");
            totals.AddFormattedText(table.Totals[i].Value, TextFormat.Bold);
        }

        if (table.Meta.Truncated is { } truncated)
        {
            var warn = section.AddParagraph(truncated);
            warn.Format.SpaceBefore = Unit.FromPoint(3);
            warn.Format.Font.Color = Red;
        }

        var spacer = section.AddParagraph();
        spacer.Format.SpaceAfter = Unit.FromPoint(6);

        // La tabla, solo con las columnas que tienen ancho en PDF.
        var columns = table.Columns.Select((c, i) => (Column: c, Index: i)).Where(x => x.Column.PdfWidthCm > 0).ToList();
        double declared = columns.Sum(c => c.Column.PdfWidthCm);
        double scale = declared > usable ? usable / declared : 1.0;

        var grid = section.AddTable();
        grid.Borders.Color = Grid;
        grid.Borders.Width = 0.5;
        grid.LeftPadding = grid.RightPadding = Unit.FromPoint(3);
        grid.TopPadding = grid.BottomPadding = Unit.FromPoint(2);
        foreach (var (column, _) in columns)
        {
            var c = grid.AddColumn(Unit.FromCentimeter(column.PdfWidthCm * scale));
            c.Format.Alignment = column.Kind is ReportCellKind.Int ? ParagraphAlignment.Right
                : column.Kind is ReportCellKind.Time or ReportCellKind.Duration ? ParagraphAlignment.Center
                : ParagraphAlignment.Left;
        }

        var header = grid.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Navy;
        header.Format.Font.Bold = true;
        header.Format.Font.Color = Colors.White;
        header.VerticalAlignment = VerticalAlignment.Center;
        for (int c = 0; c < columns.Count; c++) header.Cells[c].AddParagraph(columns[c].Column.Header);

        int n = 0;
        foreach (var values in table.Rows)
        {
            var row = grid.AddRow();
            if (n++ % 2 == 1) row.Shading.Color = Stripe;
            for (int c = 0; c < columns.Count; c++)
            {
                var (column, index) = columns[c];
                string text = Format(column.Kind, values[index]);
                var paragraph = row.Cells[c].AddParagraph();
                var lines = text.Split('\n');
                for (int l = 0; l < lines.Length; l++)
                {
                    if (l > 0) paragraph.AddLineBreak();
                    // La segunda línea (el equipo bajo la puerta) va más chica y gris.
                    if (l == 0) paragraph.AddText(lines[l]);
                    else
                    {
                        var small = paragraph.AddFormattedText(lines[l]);
                        small.Size = 6.5;
                        small.Color = Gray;
                    }
                }
                if (column.Kind == ReportCellKind.Result)
                {
                    if (text.StartsWith("Acceso concedido")) paragraph.Format.Font.Color = Green;
                    else if (text.StartsWith("Acceso denegado") || text.StartsWith("Alarma")) paragraph.Format.Font.Color = Red;
                }
            }
        }

        if (table.Rows.Count == 0)
        {
            var empty = section.AddParagraph("No hay registros que coincidan con los filtros.");
            empty.Format.SpaceBefore = Unit.FromPoint(8);
            empty.Format.Font.Color = Gray;
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        return output.ToArray();
    }

    private static string Format(ReportCellKind kind, object? value) => (kind, value) switch
    {
        (_, null) => "",
        (ReportCellKind.DateTime, DateTime dt) => dt.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        (ReportCellKind.Date, DateTime d) => d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        (ReportCellKind.Time, TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        (ReportCellKind.Duration, TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}",
        (ReportCellKind.Int, int i) => i.ToString("N0", CultureInfo.GetCultureInfo("es-CL")),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "",
    };
}
