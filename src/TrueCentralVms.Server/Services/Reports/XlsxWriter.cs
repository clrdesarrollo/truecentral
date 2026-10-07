using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace TrueCentralVms.Server.Services.Reports;

/// <summary>
/// Escribe un <see cref="ReportTable"/> como planilla .xlsx (Office Open XML)
/// sin librerías: un libro con una hoja, encabezado del reporte arriba, la
/// fila de títulos fija y con autofiltro, y las fechas y horas como VALORES de
/// Excel (no texto), para que se puedan ordenar, filtrar y sumar.
/// </summary>
public static class XlsxWriter
{
    // Índices de cellXfs en styles.xml (ver Styles()).
    private const int SHeader = 1, SDateTime = 2, SDate = 3, STime = 4, SDuration = 5, SText = 6, SInt = 7,
                      STitle = 8, SMeta = 9, SGranted = 10, SDenied = 11;

    public static byte[] Write(ReportTable table)
    {
        var columns = table.Columns.Where(c => c.ExcelWidth > 0).ToList();
        var indexes = table.Columns.Select((c, i) => (c, i)).Where(x => x.c.ExcelWidth > 0).Select(x => x.i).ToList();

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");

        // Encabezado del reporte: título, quién/cuándo, filtros, totales.
        var meta = new List<(string Text, int Style)>
        {
            (table.Title, STitle),
            ($"Generado por {table.Meta.GeneratedBy} el {table.Meta.GeneratedAt:dd-MM-yyyy HH:mm} · CLR TrueCentral VMS", SMeta),
            ($"Filtros: {table.Meta.Filters}", SMeta),
            (string.Join("   ·   ", table.Totals.Select(t => $"{t.Label}: {t.Value}")), SMeta),
        };
        if (table.Meta.Truncated is { } truncated) meta.Add((truncated, SMeta));
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

        int rowNumber = headerRow;
        foreach (var row in table.Rows)
        {
            rowNumber++;
            sheet.Append($"""<row r="{rowNumber}">""");
            for (int c = 0; c < columns.Count; c++)
                sheet.Append(Cell(Ref(c, rowNumber), columns[c].Kind, row[indexes[c]]));
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData>");

        string lastCol = ColumnName(columns.Count - 1);
        string filterRange = $"A{headerRow}:{lastCol}{Math.Max(rowNumber, headerRow)}";
        sheet.Append($"""<autoFilter ref="{filterRange}"/>""");
        sheet.Append("""<pageMargins left="0.4" right="0.4" top="0.5" bottom="0.5" header="0.3" footer="0.3"/>""");
        sheet.Append("""<pageSetup orientation="landscape" fitToWidth="1" fitToHeight="0"/>""");
        sheet.Append("</worksheet>");

        string sheetName = SheetName(table.Title);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets><definedNames><definedName name="_xlnm._FilterDatabase" localSheetId="0" hidden="1">'{Escape(sheetName.Replace("'", "''"))}'!$A${headerRow}:${lastCol}${Math.Max(rowNumber, headerRow)}</definedName></definedNames></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml", Styles());
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return output.ToArray();
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
            (ReportCellKind.Result, string s) => TextCell(reference, s, s.StartsWith("Acceso concedido") ? SGranted
                : s.StartsWith("Acceso denegado") || s.StartsWith("Alarma") ? SDenied : SText),
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

    private static string SheetName(string title)
    {
        var clean = new string(title.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
        return clean.Length == 0 ? "Reporte" : clean.Length > 31 ? clean[..31] : clean;
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
        """<numFmts count="4"><numFmt numFmtId="164" formatCode="dd\-mm\-yyyy\ hh:mm:ss"/><numFmt numFmtId="165" formatCode="dd\-mm\-yyyy"/><numFmt numFmtId="166" formatCode="hh:mm:ss"/><numFmt numFmtId="167" formatCode="[h]:mm"/></numFmts>""" +
        """<fonts count="7">""" +
        """<font><sz val="10"/><name val="Calibri"/></font>""" +
        """<font><b/><sz val="10"/><name val="Calibri"/></font>""" +
        """<font><b/><sz val="14"/><color rgb="FF1F3A5F"/><name val="Calibri"/></font>""" +
        """<font><sz val="9"/><color rgb="FF555555"/><name val="Calibri"/></font>""" +
        """<font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font>""" +
        """<font><sz val="10"/><color rgb="FF1E7B34"/><name val="Calibri"/></font>""" +
        """<font><sz val="10"/><color rgb="FFC0392B"/><name val="Calibri"/></font>""" +
        """</fonts>""" +
        """<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F3A5F"/><bgColor indexed="64"/></patternFill></fill></fills>""" +
        """<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color rgb="FFD0D7DE"/></left><right style="thin"><color rgb="FFD0D7DE"/></right><top style="thin"><color rgb="FFD0D7DE"/></top><bottom style="thin"><color rgb="FFD0D7DE"/></bottom><diagonal/></border></borders>""" +
        """<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""" +
        """<cellXfs count="12">""" +
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
        """</cellXfs>""" +
        """<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>""" +
        """</styleSheet>""";
}
