using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace TrueCentralVms.Server.Services.Reports;

/// <summary>
/// Escribe un <see cref="ReportTable"/> como planilla .xlsx (Office Open XML)
/// sin librerías: la tabla principal en la primera hoja (encabezado del
/// reporte arriba, la fila de títulos fija y con autofiltro) y cada sección en
/// una hoja propia. Las fechas y horas van como VALORES de Excel (no texto),
/// para que se puedan ordenar, filtrar y sumar; las columnas de mapa de calor
/// llevan una escala de color.
/// </summary>
public static class XlsxWriter
{
    // Índices de cellXfs en styles.xml (ver Styles()).
    private const int SHeader = 1, SDateTime = 2, SDate = 3, STime = 4, SDuration = 5, SText = 6, SInt = 7,
                      STitle = 8, SMeta = 9, SGranted = 10, SDenied = 11, SGroup = 12, SPercent = 13, SDecimal = 14,
                      SWarn = 15, SHeat = 16;

    private sealed record Sheet(string Name, string Xml, int HeaderRow, int LastRow, string LastCol);

    public static byte[] Write(ReportTable table)
    {
        var meta = new List<(string Text, int Style)>
        {
            (table.Title, STitle),
        };
        if (table.Subtitle is { Length: > 0 } subtitle) meta.Add((subtitle, SMeta));
        meta.Add(($"Generado por {table.Meta.GeneratedBy} el {table.Meta.GeneratedAt:dd-MM-yyyy HH:mm} · CLR TrueCentral VMS", SMeta));
        meta.Add(($"Filtros: {table.Meta.Filters}", SMeta));
        meta.Add((string.Join("   ·   ", table.Totals.Select(t => $"{t.Label}: {t.Value}")), SMeta));
        if (table.Meta.Truncated is { } truncated) meta.Add((truncated, SMeta));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sheets = new List<Sheet>
        {
            BuildSheet(UniqueName(table.SheetName ?? table.Title, used), meta, table.Columns, table.Rows, table.RowStyles),
        };
        foreach (var section in table.Sections.Where(s => !s.PdfOnly))
        {
            var sectionMeta = new List<(string Text, int Style)> { (section.Title, STitle) };
            if (section.Note is { } note) sectionMeta.Add((note, SMeta));
            sectionMeta.Add(($"{table.Title} · Filtros: {table.Meta.Filters}", SMeta));
            sheets.Add(BuildSheet(UniqueName(section.SheetName ?? section.Title, used), sectionMeta, section.Columns, section.Rows,
                section.RowStyles));
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var types = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
            for (int i = 0; i < sheets.Count; i++)
                types.Append($"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
            types.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add(zip, "[Content_Types].xml", types.ToString());
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");

            var workbook = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
            for (int i = 0; i < sheets.Count; i++)
                workbook.Append($"""<sheet name="{Escape(sheets[i].Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
            workbook.Append("</sheets><definedNames>");
            for (int i = 0; i < sheets.Count; i++)
            {
                var s = sheets[i];
                workbook.Append($"""<definedName name="_xlnm._FilterDatabase" localSheetId="{i}" hidden="1">'{Escape(s.Name.Replace("'", "''"))}'!$A${s.HeaderRow}:${s.LastCol}${Math.Max(s.LastRow, s.HeaderRow)}</definedName>""");
            }
            workbook.Append("</definedNames></workbook>");
            Add(zip, "xl/workbook.xml", workbook.ToString());

            var rels = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            for (int i = 0; i < sheets.Count; i++)
                rels.Append($"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""");
            rels.Append($"""<Relationship Id="rId{sheets.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
            Add(zip, "xl/styles.xml", Styles());
            for (int i = 0; i < sheets.Count; i++) Add(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i].Xml);
        }
        return output.ToArray();
    }

    private static Sheet BuildSheet(string name, List<(string Text, int Style)> meta, IReadOnlyList<ReportColumn> allColumns,
        IReadOnlyList<object?[]> rows, IReadOnlyList<ReportRowStyle>? rowStyles)
    {
        var columns = allColumns.Where(c => c.ExcelWidth > 0).ToList();
        var indexes = allColumns.Select((c, i) => (c, i)).Where(x => x.c.ExcelWidth > 0).Select(x => x.i).ToList();
        if (columns.Count == 0) { columns = [new ReportColumn("", ReportCellKind.Text, 0, 10)]; indexes = [0]; }

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        int headerRow = meta.Count + 2;

        sheet.Append($"""<sheetViews><sheetView workbookViewId="0"><pane ySplit="{headerRow}" topLeftCell="A{headerRow + 1}" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sheet.Append("<cols>");
        for (int i = 0; i < columns.Count; i++)
            sheet.Append($"""<col min="{i + 1}" max="{i + 1}" width="{columns[i].ExcelWidth.ToString(CultureInfo.InvariantCulture)}" customWidth="1"/>""");
        sheet.Append("</cols><sheetData>");

        for (int r = 0; r < meta.Count; r++)
            sheet.Append($"""<row r="{r + 1}">{TextCell(Ref(0, r + 1), meta[r].Text, meta[r].Style)}</row>""");

        sheet.Append($"""<row r="{headerRow}">""");
        for (int c = 0; c < columns.Count; c++) sheet.Append(TextCell(Ref(c, headerRow), columns[c].Header, SHeader));
        sheet.Append("</row>");

        var merges = new List<string>();
        int rowNumber = headerRow;
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            rowNumber++;
            sheet.Append($"""<row r="{rowNumber}">""");
            if (rowStyles is not null && r < rowStyles.Count && rowStyles[r] == ReportRowStyle.Group)
            {
                sheet.Append(TextCell(Ref(0, rowNumber), (Convert.ToString(row[0], CultureInfo.CurrentCulture) ?? "").Replace('\n', ' '), SGroup));
                for (int c = 1; c < columns.Count; c++) sheet.Append($"""<c r="{Ref(c, rowNumber)}" s="{SGroup}"/>""");
                if (columns.Count > 1) merges.Add($"{Ref(0, rowNumber)}:{Ref(columns.Count - 1, rowNumber)}");
            }
            else
            {
                for (int c = 0; c < columns.Count; c++)
                    sheet.Append(Cell(Ref(c, rowNumber), columns[c].Kind, indexes[c] < row.Length ? row[indexes[c]] : null));
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData>");

        string lastCol = ColumnName(columns.Count - 1);
        sheet.Append($"""<autoFilter ref="A{headerRow}:{lastCol}{Math.Max(rowNumber, headerRow)}"/>""");
        if (merges.Count > 0)
        {
            sheet.Append($"""<mergeCells count="{merges.Count}">""");
            foreach (var m in merges) sheet.Append($"""<mergeCell ref="{m}"/>""");
            sheet.Append("</mergeCells>");
        }
        // Mapa de calor: una escala de color sobre todas las columnas Heat juntas.
        var heat = columns.Select((c, i) => (c, i)).Where(x => x.c.Kind == ReportCellKind.Heat).Select(x => x.i).ToList();
        if (heat.Count > 0 && rowNumber > headerRow)
        {
            string range = $"{Ref(heat.Min(), headerRow + 1)}:{Ref(heat.Max(), rowNumber)}";
            sheet.Append($"""<conditionalFormatting sqref="{range}"><cfRule type="colorScale" priority="1"><colorScale><cfvo type="min"/><cfvo type="percentile" val="50"/><cfvo type="max"/><color rgb="FFFFF4E0"/><color rgb="FFF59E42"/><color rgb="FFB02A1E"/></colorScale></cfRule></conditionalFormatting>""");
        }
        sheet.Append("""<pageMargins left="0.4" right="0.4" top="0.5" bottom="0.5" header="0.3" footer="0.3"/>""");
        sheet.Append("""<pageSetup orientation="landscape" fitToWidth="1" fitToHeight="0"/>""");
        sheet.Append("</worksheet>");
        return new Sheet(name, sheet.ToString(), headerRow, rowNumber, lastCol);
    }

    private static string Cell(string reference, ReportCellKind kind, object? value)
    {
        if (value is null || value is string { Length: 0 }) return "";
        string Number(double n, int style) =>
            $"""<c r="{reference}" s="{style}"><v>{n.ToString("R", CultureInfo.InvariantCulture)}</v></c>""";
        return (kind, value) switch
        {
            (ReportCellKind.DateTime, DateTime dt) => Number(dt.ToOADate(), SDateTime),
            (ReportCellKind.Date, DateTime d) => Number(d.Date.ToOADate(), SDate),
            (ReportCellKind.Time, TimeSpan t) => Number(t.TotalDays, STime),
            (ReportCellKind.Duration, TimeSpan t) => Number(t.TotalDays, SDuration),
            (ReportCellKind.Int, int i) => Number(i, SInt),
            (ReportCellKind.Heat, int i) => Number(i, SHeat),
            (ReportCellKind.Percent, double p) => Number(p, SPercent),
            (ReportCellKind.Decimal, double d) => Number(d, SDecimal),
            (ReportCellKind.Result, string s) => TextCell(reference, s, ReportTable.ToneOf(s) switch
            {
                ReportTone.Good => SGranted,
                ReportTone.Bad => SDenied,
                ReportTone.Warn => SWarn,
                _ => SText,
            }),
            _ => TextCell(reference, Convert.ToString(value, CultureInfo.CurrentCulture) ?? "", SText),
        };
    }

    private static string TextCell(string reference, string text, int style) =>
        $"""<c r="{reference}" t="inlineStr" s="{style}"><is><t xml:space="preserve">{Escape(text)}</t></is></c>""";

    private static string Ref(int column, int row) => ColumnName(column) + row;

    private static string ColumnName(int index)
    {
        string name = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>Escapa para XML y quita los caracteres de control que XML no admite.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\t' or '\n' or '\r': sb.Append(ch); break;
                case < ' ': break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string UniqueName(string title, HashSet<string> used)
    {
        var clean = new string(title.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
        if (clean.Length == 0) clean = "Reporte";
        if (clean.Length > 31) clean = clean[..31].TrimEnd();
        string name = clean;
        for (int n = 2; !used.Add(name); n++)
        {
            string suffix = $" ({n})";
            name = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        return name;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes);
    }

    private static string Styles() =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
        """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
        """<numFmts count="6"><numFmt numFmtId="164" formatCode="dd\-mm\-yyyy\ hh:mm:ss"/><numFmt numFmtId="165" formatCode="dd\-mm\-yyyy"/><numFmt numFmtId="166" formatCode="hh:mm:ss"/><numFmt numFmtId="167" formatCode="[h]:mm"/><numFmt numFmtId="168" formatCode="0.0%"/><numFmt numFmtId="169" formatCode="0.0"/></numFmts>""" +
        """<fonts count="8">""" +
        """<font><sz val="10"/><name val="Calibri"/></font>""" +
        """<font><b/><sz val="10"/><name val="Calibri"/></font>""" +
        """<font><b/><sz val="14"/><color rgb="FF1F3A5F"/><name val="Calibri"/></font>""" +
        """<font><sz val="9"/><color rgb="FF555555"/><name val="Calibri"/></font>""" +
        """<font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font>""" +
        """<font><sz val="10"/><color rgb="FF1E7B34"/><name val="Calibri"/></font>""" +
        """<font><sz val="10"/><color rgb="FFC0392B"/><name val="Calibri"/></font>""" +
        """<font><sz val="10"/><color rgb="FFB9770E"/><name val="Calibri"/></font>""" +
        """</fonts>""" +
        """<fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F3A5F"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE3E9F1"/><bgColor indexed="64"/></patternFill></fill></fills>""" +
        """<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color rgb="FFD0D7DE"/></left><right style="thin"><color rgb="FFD0D7DE"/></right><top style="thin"><color rgb="FFD0D7DE"/></top><bottom style="thin"><color rgb="FFD0D7DE"/></bottom><diagonal/></border></borders>""" +
        """<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""" +
        """<cellXfs count="17">""" +
        """<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>""" +
        """<xf numFmtId="0" fontId="4" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>""" +
        """<xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" horizontal="left"/></xf>""" +
        """<xf numFmtId="165" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" horizontal="left"/></xf>""" +
        """<xf numFmtId="166" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" horizontal="center"/></xf>""" +
        """<xf numFmtId="167" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" horizontal="center"/></xf>""" +
        """<xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="3" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +
        """<xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +
        """<xf numFmtId="0" fontId="5" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="0" fontId="6" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="0" fontId="1" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="168" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="169" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="0" fontId="7" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment vertical="top"/></xf>""" +
        """<xf numFmtId="3" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" horizontal="center"/></xf>""" +
        """</cellXfs>""" +
        """<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>""" +
        """</styleSheet>""";
}
