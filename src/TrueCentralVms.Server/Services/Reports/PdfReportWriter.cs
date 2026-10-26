using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes.Charts;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace TrueCentralVms.Server.Services.Reports;

/// <summary>
/// Escribe un <see cref="ReportTable"/> como PDF con MigraDoc (PDFsharp, MIT):
/// A4 (apaisado o vertical), logo y título arriba, quién lo sacó y con qué
/// filtros, la tabla con su fila de títulos repetida en cada página y
/// "Página X de Y" al pie. Si el reporte trae secciones, van debajo con su
/// título. Las fuentes son las de Windows (el servidor siempre corre en Windows).
/// </summary>
public static class PdfReportWriter
{
    private const string Font = "Arial";
    private static readonly Color Navy = new(0x1F, 0x3A, 0x5F);
    private static readonly Color Stripe = new(0xF3, 0xF5, 0xF8);
    private static readonly Color GroupFill = new(0xE3, 0xE9, 0xF1);
    private static readonly Color Grid = new(0xD0, 0xD7, 0xDE);
    private static readonly Color Green = new(0x1E, 0x7B, 0x34);
    private static readonly Color Red = new(0xC0, 0x39, 0x2B);
    private static readonly Color Amber = new(0xB9, 0x77, 0x0E);
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
        if (table.Subtitle is { Length: > 0 } subtitle)
        {
            var sub = headRow.Cells[1].AddParagraph(subtitle);
            sub.Format.Font.Size = 10;
            sub.Format.Font.Color = Navy;
        }
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

        if (table.MainHeading is { } mainHeading) AddHeading(section, mainHeading, null);
        AddGrid(section, usable, table.Columns, table.Rows, table.RowStyles,
            table.EmptyText ?? "No hay registros que coincidan con los filtros.");

        foreach (var extra in table.Sections.Where(s => !s.ExcelOnly))
        {
            if (extra.PageBreak) section.AddPageBreak();
            AddHeading(section, extra.Title, extra.Note);
            if (extra.Chart is { } chart && extra.Rows.Count > 0) AddChart(section, usable, chart);
            else AddGrid(section, usable, extra.Columns, extra.Rows, extra.RowStyles, extra.EmptyText ?? "Sin datos.");
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        return output.ToArray();
    }

    private static void AddHeading(Section section, string text, string? note)
    {
        var heading = section.AddParagraph(text);
        heading.Format.SpaceBefore = Unit.FromPoint(10);
        heading.Format.SpaceAfter = Unit.FromPoint(note is null ? 4 : 1);
        heading.Format.Font.Size = 11;
        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = Navy;
        heading.Format.KeepWithNext = true;
        if (note is not null)
        {
            var n = section.AddParagraph(note);
            n.Format.SpaceAfter = Unit.FromPoint(4);
            n.Format.Font.Size = 7.5;
            n.Format.Font.Color = Gray;
            n.Format.KeepWithNext = true;
        }
    }

    /// <summary>Una tabla, solo con las columnas que tienen ancho en PDF.</summary>
    private static void AddGrid(Section section, double usable, IReadOnlyList<ReportColumn> allColumns,
        IReadOnlyList<object?[]> rows, IReadOnlyList<ReportRowStyle>? rowStyles, string emptyText)
    {
        var columns = allColumns.Select((c, i) => (Column: c, Index: i)).Where(x => x.Column.PdfWidthCm > 0).ToList();
        if (columns.Count == 0) return;
        if (rows.Count == 0)
        {
            var empty = section.AddParagraph(emptyText);
            empty.Format.SpaceBefore = Unit.FromPoint(4);
            empty.Format.Font.Color = Gray;
            return;
        }
        double declared = columns.Sum(c => c.Column.PdfWidthCm);
        double scale = declared > usable ? usable / declared : 1.0;

        // Mapa de calor: el sombreado se reparte entre 0 y el máximo de la tabla.
        int heatMax = 0;
        foreach (var values in rows)
            foreach (var (column, index) in columns)
                if (column.Kind == ReportCellKind.Heat && values[index] is int h && h > heatMax) heatMax = h;

        var grid = section.AddTable();
        grid.Borders.Color = Grid;
        grid.Borders.Width = 0.5;
        grid.LeftPadding = grid.RightPadding = Unit.FromPoint(3);
        grid.TopPadding = grid.BottomPadding = Unit.FromPoint(2);
        foreach (var (column, _) in columns)
        {
            var c = grid.AddColumn(Unit.FromCentimeter(column.PdfWidthCm * scale));
            c.Format.Alignment = column.Kind is ReportCellKind.Int or ReportCellKind.Percent or ReportCellKind.Decimal ? ParagraphAlignment.Right
                : column.Kind is ReportCellKind.Time or ReportCellKind.Duration or ReportCellKind.Heat ? ParagraphAlignment.Center
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
        for (int r = 0; r < rows.Count; r++)
        {
            var values = rows[r];
            var row = grid.AddRow();
            if (rowStyles is not null && r < rowStyles.Count && rowStyles[r] == ReportRowStyle.Group)
            {
                // Renglón de agrupación: un solo texto a todo el ancho, unido a su detalle.
                var cell = row.Cells[0];
                cell.MergeRight = columns.Count - 1;
                row.Shading.Color = GroupFill;
                var groupText = cell.AddParagraph();
                groupText.Format.Alignment = ParagraphAlignment.Left;
                AddLines(groupText, Convert.ToString(values[0], CultureInfo.CurrentCulture) ?? "", bold: true);
                int detail = 0;
                for (int k = r + 1; k < rows.Count && (rowStyles.Count <= k || rowStyles[k] != ReportRowStyle.Group); k++) detail++;
                row.KeepWith = Math.Min(detail, 3);
                n = 0;
                continue;
            }
            if (n++ % 2 == 1) row.Shading.Color = Stripe;
            for (int c = 0; c < columns.Count; c++)
            {
                var (column, index) = columns[c];
                string text = Format(column.Kind, values[index]);
                var paragraph = row.Cells[c].AddParagraph();
                AddLines(paragraph, text, bold: false);
                if (column.Kind == ReportCellKind.Result)
                {
                    switch (ReportTable.ToneOf(text))
                    {
                        case ReportTone.Good: paragraph.Format.Font.Color = Green; break;
                        case ReportTone.Bad: paragraph.Format.Font.Color = Red; break;
                        case ReportTone.Warn: paragraph.Format.Font.Color = Amber; break;
                    }
                }
                else if (column.Kind == ReportCellKind.Heat && values[index] is int heat && heat > 0 && heatMax > 0)
                {
                    row.Cells[c].Shading.Color = HeatColor(heat / (double)heatMax);
                    if (heat / (double)heatMax > 0.6) paragraph.Format.Font.Color = Colors.White;
                }
            }
        }
    }

    private static void AddChart(Section section, double usable, ReportChart spec)
    {
        var chart = section.AddChart(spec.Stacked ? ChartType.ColumnStacked2D : ChartType.Column2D);
        chart.Width = Unit.FromCentimeter(usable);
        chart.Height = Unit.FromCentimeter(spec.HeightCm);
        chart.Format.Font.Size = 6.5;
        chart.Format.Font.Name = Font;
        foreach (var serie in spec.Series)
        {
            var s = chart.SeriesCollection.AddSeries();
            s.Name = serie.Name;
            s.Add(serie.Values.ToArray());
            s.FillFormat.Color = Hex(serie.ColorHex);
            s.LineFormat.Color = Colors.White;
            s.LineFormat.Width = 0.25;
        }
        chart.XValues.AddXSeries().Add(spec.Categories.ToArray());
        chart.XAxis.MajorTickMark = TickMarkType.Outside;
        chart.XAxis.LineFormat.Color = Gray;
        chart.YAxis.MajorTickMark = TickMarkType.Outside;
        chart.YAxis.HasMajorGridlines = true;
        chart.YAxis.MajorGridlines.LineFormat.Color = Grid;
        chart.YAxis.MajorGridlines.LineFormat.Width = 0.5;
        chart.YAxis.LineFormat.Color = Gray;
        chart.YAxis.TickLabels.Format = "0";
        chart.PlotArea.LineFormat.Width = 0;
        var legend = chart.BottomArea.AddLegend();
        legend.Format.Font.Size = 7;
        chart.BottomArea.Height = Unit.FromCentimeter(0.7);
    }

    private static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Color(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    private static void AddLines(Paragraph paragraph, string text, bool bold)
    {
        var lines = text.Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            if (l > 0) paragraph.AddLineBreak();
            // La segunda línea (el equipo bajo la puerta, la causa bajo la ronda) va más chica y gris.
            if (l == 0)
            {
                if (bold) paragraph.AddFormattedText(lines[l], TextFormat.Bold);
                else paragraph.AddText(lines[l]);
            }
            else
            {
                var small = paragraph.AddFormattedText(lines[l]);
                small.Size = 6.5;
                small.Color = Gray;
            }
        }
    }

    /// <summary>De crema a rojo oscuro, pasando por naranjo.</summary>
    private static Color HeatColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        (int r, int g, int b) from = t < 0.5 ? (0xFF, 0xF4, 0xE0) : (0xF5, 0x9E, 0x42);
        (int r, int g, int b) to = t < 0.5 ? (0xF5, 0x9E, 0x42) : (0xB0, 0x2A, 0x1E);
        double u = t < 0.5 ? t / 0.5 : (t - 0.5) / 0.5;
        byte Mix(int a, int z) => (byte)Math.Round(a + (z - a) * u);
        return new Color(Mix(from.r, to.r), Mix(from.g, to.g), Mix(from.b, to.b));
    }

    private static readonly CultureInfo Chile = CultureInfo.GetCultureInfo("es-CL");

    private static string Format(ReportCellKind kind, object? value) => (kind, value) switch
    {
        (_, null) => "",
        (ReportCellKind.DateTime, DateTime dt) => dt.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        (ReportCellKind.Date, DateTime d) => d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        (ReportCellKind.Time, TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        (ReportCellKind.Duration, TimeSpan t) => $"{(t < TimeSpan.Zero ? "-" : "")}{(int)Math.Abs(t.TotalHours)}:{Math.Abs(t.Minutes):00}",
        (ReportCellKind.Int or ReportCellKind.Heat, int i) => i.ToString("N0", Chile),
        (ReportCellKind.Percent, double p) => (p * 100).ToString("0.0", Chile) + " %",
        (ReportCellKind.Decimal, double d) => d.ToString("0.0", Chile),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "",
    };
}
