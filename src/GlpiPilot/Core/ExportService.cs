using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace GlpiPilot.Core;

/// <summary>Exports local extraction results without Excel or an additional package.</summary>
public static class ExportService
{
    private const int ExcelMaxRows = 1_048_576;
    private const int ExcelMaxColumns = 16_384;
    private const int ExcelMaxTextLength = 32_767;
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocumentRelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static ExportResult WriteXlsx(ExtractionResult data, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateData(data);
        if (data.Headers.Count > ExcelMaxColumns)
            throw new InvalidOperationException("Le tableau dépasse la limite Excel de 16 384 colonnes. Utilisez le CSV.");
        if (data.Rows.Count > ExcelMaxRows - 1)
            throw new InvalidOperationException("Le tableau dépasse la limite Excel de 1 048 575 lignes de données. Utilisez le CSV.");
        if (data.Warnings.Count > ExcelMaxRows - 32)
            throw new InvalidOperationException("Le nombre d’avertissements dépasse la capacité de la feuille Informations.");

        var counters = new TextCounters();
        string destination = WriteAtomically(path, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            WriteContentTypes(archive);
            WritePackageRelationships(archive);
            WriteWorkbook(archive);
            WriteWorkbookRelationships(archive);
            WriteStyles(archive);
            WriteDataSheet(archive, data, counters, ct);
            WriteInformationSheet(archive, data, counters);
            WriteDocumentProperties(archive, data.ExtractedAt);
            ct.ThrowIfCancellationRequested();
        });
        return new ExportResult(destination, data.Rows.Count, data.Headers.Count, counters.Truncated);
    }

    public static ExportResult WriteCsv(ExtractionResult data, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateData(data);
        string destination = WriteAtomically(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 65_536, leaveOpen: true)
            {
                NewLine = "\r\n"
            };
            WriteCsvRow(writer, data.Headers, data.Headers.Count);
            foreach (string[] row in data.Rows)
            {
                ct.ThrowIfCancellationRequested();
                WriteCsvRow(writer, row ?? Array.Empty<string>(), data.Headers.Count);
            }
            ct.ThrowIfCancellationRequested();
        });
        return new ExportResult(destination, data.Rows.Count, data.Headers.Count, 0);
    }

    private static void ValidateData(ExtractionResult data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Headers is null || data.Headers.Count == 0)
            throw new InvalidOperationException("Aucune colonne à exporter.");
        if (data.Rows is null || data.Warnings is null)
            throw new InvalidOperationException("Le résultat d’extraction est incomplet.");
        for (int i = 0; i < data.Rows.Count; i++)
        {
            if (data.Rows[i] is { } row && row.Length > data.Headers.Count)
                throw new InvalidOperationException($"La ligne {i + 1} contient plus de cellules que d’en-têtes. Export interrompu pour éviter une perte de données.");
        }
    }

    private static string WriteAtomically(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string destination = System.IO.Path.GetFullPath(path);
        string directory = System.IO.Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Dossier d’export invalide.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Le dossier d’export n’existe pas : {directory}");
        string temporary = System.IO.Path.Combine(directory, $".glpi-export-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void WriteCsvRow(TextWriter writer, IReadOnlyList<string> values, int columns)
    {
        for (int i = 0; i < columns; i++)
        {
            if (i != 0) writer.Write(';');
            string value = i < values.Count ? values[i] ?? "" : "";
            if (NeedsCsvFormulaProtection(value)) value = "'" + value;
            writer.Write('"');
            writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
            writer.Write('"');
        }
        writer.WriteLine();
    }

    private static bool NeedsCsvFormulaProtection(string value)
    {
        if (value.Length == 0) return false;
        if (value[0] is '\t' or '\r' or '\n') return true;
        int i = 0;
        while (i < value.Length && (char.IsWhiteSpace(value[i]) || char.IsControl(value[i]) || char.GetUnicodeCategory(value[i]) == UnicodeCategory.Format)) i++;
        return i < value.Length && value[i] is '=' or '+' or '-' or '@';
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        using var x = EntryWriter(archive, "[Content_Types].xml");
        x.WriteStartElement("Types", ns);
        EmptyElement(x, "Default", ns, ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
        EmptyElement(x, "Default", ns, ("Extension", "xml"), ("ContentType", "application/xml"));
        EmptyElement(x, "Override", ns, ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
        EmptyElement(x, "Override", ns, ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
        for (int n = 1; n <= 2; n++)
            EmptyElement(x, "Override", ns, ("PartName", $"/xl/worksheets/sheet{n}.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
        EmptyElement(x, "Override", ns, ("PartName", "/docProps/core.xml"), ("ContentType", "application/vnd.openxmlformats-package.core-properties+xml"));
        EmptyElement(x, "Override", ns, ("PartName", "/docProps/app.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.extended-properties+xml"));
        x.WriteEndElement();
    }

    private static void WritePackageRelationships(ZipArchive archive)
    {
        using var x = EntryWriter(archive, "_rels/.rels");
        x.WriteStartElement("Relationships", RelationshipsNs);
        Relationship(x, "rId1", DocumentRelationshipsNs + "/officeDocument", "xl/workbook.xml");
        Relationship(x, "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        Relationship(x, "rId3", DocumentRelationshipsNs + "/extended-properties", "docProps/app.xml");
        x.WriteEndElement();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        using var x = EntryWriter(archive, "xl/_rels/workbook.xml.rels");
        x.WriteStartElement("Relationships", RelationshipsNs);
        Relationship(x, "rId1", DocumentRelationshipsNs + "/worksheet", "worksheets/sheet1.xml");
        Relationship(x, "rId2", DocumentRelationshipsNs + "/worksheet", "worksheets/sheet2.xml");
        Relationship(x, "rId3", DocumentRelationshipsNs + "/styles", "styles.xml");
        x.WriteEndElement();
    }

    private static void Relationship(XmlWriter x, string id, string type, string target) =>
        EmptyElement(x, "Relationship", RelationshipsNs, ("Id", id), ("Type", type), ("Target", target));

    private static void WriteWorkbook(ZipArchive archive)
    {
        using var x = EntryWriter(archive, "xl/workbook.xml");
        x.WriteStartElement("workbook", SpreadsheetNs);
        x.WriteAttributeString("xmlns", "r", null, DocumentRelationshipsNs);
        x.WriteStartElement("bookViews", SpreadsheetNs);
        EmptyElement(x, "workbookView", SpreadsheetNs, ("activeTab", "0"));
        x.WriteEndElement();
        x.WriteStartElement("sheets", SpreadsheetNs);
        string[] names = { "Données", "Informations" };
        for (int i = 0; i < names.Length; i++)
        {
            x.WriteStartElement("sheet", SpreadsheetNs);
            x.WriteAttributeString("name", names[i]);
            x.WriteAttributeString("sheetId", (i + 1).ToString(Invariant));
            x.WriteAttributeString("r", "id", DocumentRelationshipsNs, $"rId{i + 1}");
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void WriteStyles(ZipArchive archive)
    {
        using var x = EntryWriter(archive, "xl/styles.xml");
        x.WriteStartElement("styleSheet", SpreadsheetNs);
        x.WriteStartElement("fonts", SpreadsheetNs); x.WriteAttributeString("count", "3");
        Font(x, "FF18253A", false);
        Font(x, "FFFFFFFF", true);
        Font(x, "FF243B5A", true);
        x.WriteEndElement();
        x.WriteStartElement("fills", SpreadsheetNs); x.WriteAttributeString("count", "4");
        Fill(x, "none", null); Fill(x, "gray125", null); Fill(x, "solid", "FF2165C5"); Fill(x, "solid", "FFF0F4FA");
        x.WriteEndElement();
        x.WriteStartElement("borders", SpreadsheetNs); x.WriteAttributeString("count", "1");
        x.WriteStartElement("border", SpreadsheetNs);
        foreach (string edge in new[] { "left", "right", "top", "bottom", "diagonal" }) EmptyElement(x, edge, SpreadsheetNs);
        x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("cellStyleXfs", SpreadsheetNs); x.WriteAttributeString("count", "1");
        EmptyElement(x, "xf", SpreadsheetNs, ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"));
        x.WriteEndElement();
        x.WriteStartElement("cellXfs", SpreadsheetNs); x.WriteAttributeString("count", "4");
        CellFormat(x, 0, 0, false); CellFormat(x, 1, 2, true); CellFormat(x, 0, 0, true); CellFormat(x, 2, 3, true);
        x.WriteEndElement();
        x.WriteStartElement("cellStyles", SpreadsheetNs); x.WriteAttributeString("count", "1");
        EmptyElement(x, "cellStyle", SpreadsheetNs, ("name", "Normal"), ("xfId", "0"), ("builtinId", "0"));
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Font(XmlWriter x, string rgb, bool bold)
    {
        x.WriteStartElement("font", SpreadsheetNs);
        if (bold) EmptyElement(x, "b", SpreadsheetNs);
        EmptyElement(x, "sz", SpreadsheetNs, ("val", "11"));
        EmptyElement(x, "color", SpreadsheetNs, ("rgb", rgb));
        EmptyElement(x, "name", SpreadsheetNs, ("val", "Segoe UI"));
        EmptyElement(x, "family", SpreadsheetNs, ("val", "2"));
        x.WriteEndElement();
    }

    private static void Fill(XmlWriter x, string pattern, string? color)
    {
        x.WriteStartElement("fill", SpreadsheetNs);
        x.WriteStartElement("patternFill", SpreadsheetNs); x.WriteAttributeString("patternType", pattern);
        if (color is not null)
        {
            EmptyElement(x, "fgColor", SpreadsheetNs, ("rgb", color));
            EmptyElement(x, "bgColor", SpreadsheetNs, ("indexed", "64"));
        }
        x.WriteEndElement(); x.WriteEndElement();
    }

    private static void CellFormat(XmlWriter x, int fontId, int fillId, bool wrap)
    {
        x.WriteStartElement("xf", SpreadsheetNs);
        x.WriteAttributeString("numFmtId", "49");
        x.WriteAttributeString("fontId", fontId.ToString(Invariant));
        x.WriteAttributeString("fillId", fillId.ToString(Invariant));
        x.WriteAttributeString("borderId", "0"); x.WriteAttributeString("xfId", "0");
        x.WriteAttributeString("applyNumberFormat", "1");
        x.WriteAttributeString("applyAlignment", "1");
        if (fontId != 0) x.WriteAttributeString("applyFont", "1");
        if (fillId != 0) x.WriteAttributeString("applyFill", "1");
        x.WriteStartElement("alignment", SpreadsheetNs); x.WriteAttributeString("vertical", "top");
        if (wrap) x.WriteAttributeString("wrapText", "1");
        x.WriteEndElement(); x.WriteEndElement();
    }

    private static void WriteDataSheet(ZipArchive archive, ExtractionResult data, TextCounters counters, CancellationToken ct)
    {
        using var x = EntryWriter(archive, "xl/worksheets/sheet1.xml");
        int columns = data.Headers.Count;
        string range = $"A1:{ColumnName(columns - 1)}{data.Rows.Count + 1}";
        StartSheet(x, range);
        x.WriteStartElement("cols", SpreadsheetNs);
        for (int column = 0; column < columns; column++)
        {
            int length = MeasureWidth(data.Headers[column]);
            foreach (string[] row in data.Rows.Take(150))
                if (row is not null && column < row.Length) length = Math.Max(length, MeasureWidth(row[column]));
            string number = (column + 1).ToString(Invariant);
            EmptyElement(x, "col", SpreadsheetNs, ("min", number), ("max", number), ("width", Math.Clamp(length + 3, 12, 52).ToString(Invariant)), ("customWidth", "1"));
        }
        x.WriteEndElement();
        x.WriteStartElement("sheetData", SpreadsheetNs);
        StartRow(x, 1, 30);
        for (int i = 0; i < columns; i++) WriteCell(x, i, 1, PrepareText(data.Headers[i], counters), 1);
        x.WriteEndElement();
        for (int rowNumber = 0; rowNumber < data.Rows.Count; rowNumber++)
        {
            ct.ThrowIfCancellationRequested();
            string[] row = data.Rows[rowNumber] ?? Array.Empty<string>();
            int excelRow = rowNumber + 2;
            StartRow(x, excelRow);
            for (int column = 0; column < columns; column++)
                WriteCell(x, column, excelRow, PrepareText(column < row.Length ? row[column] : "", counters), 0);
            x.WriteEndElement();
        }
        x.WriteEndElement();
        EmptyElement(x, "autoFilter", SpreadsheetNs, ("ref", range));
        EndSheet(x);
    }

    private static void WriteInformationSheet(ZipArchive archive, ExtractionResult data, TextCounters counters)
    {
        var values = new List<(string Key, string Value)>
        {
            ("Application", "GLPI Pilot"),
            ("Tableau", data.Title),
            ("Source", data.SourceUrl),
            ("Date de l’extraction", data.ExtractedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", Invariant)),
            ("Périmètre demandé", data.Scope),
            ("Tri observé", string.IsNullOrEmpty(data.Provenance.ObservedSort) ? "Non exposé par la page" : data.Provenance.ObservedSort),
            ("Reprise", data.Provenance.Resumed ? $"Pages conservées depuis {data.Provenance.ResumeStartedAt:O} ; contrôles du périmètre et des pages limites effectués" : "Nouvelle collecte"),
            ("Durées par étape (s)", string.Join(" ; ", data.Provenance.StageSeconds.Select(pair => pair.Key + "=" + pair.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))),
            ("État", data.Complete ? "Complet pour le périmètre demandé" : "Partiel / exhaustivité non confirmée"),
            ("Détail", data.Status),
            ("Pages lues", data.Pages.ToString(Invariant)),
            ("Lignes exportées", data.Rows.Count.ToString(Invariant)),
            ("Colonnes exportées", data.Headers.Count.ToString(Invariant)),
            ("Total annoncé", data.ExpectedTotal?.ToString(Invariant) ?? "Non renseigné"),
            ("Type des cellules", "Texte : les identifiants, zéros initiaux et valeurs commençant par = sont conservés sans exécution de formule.")
        };
        for (int i = 0; i < data.Warnings.Count; i++) values.Add(($"Avertissement {i + 1}", data.Warnings[i]));
        var prepared = values.Select(v => (Key: PrepareText(v.Key, counters), Value: PrepareText(v.Value, counters))).ToList();
        prepared.Add(("Cellules tronquées", counters.Truncated.ToString(Invariant)));
        prepared.Add(("Cellules nettoyées", counters.Cleaned.ToString(Invariant)));
        prepared.Add(("Cellules avec caractères encodés", counters.Escaped.ToString(Invariant)));
        if (counters.Truncated != 0)
            prepared.Add(("Limite de longueur", "Excel limite chaque cellule à 32 767 caractères UTF-16. Les cellules trop longues ont été raccourcies. Le CSV conserve les textes complets."));
        if (counters.Cleaned != 0)
            prepared.Add(("Unicode invalide", "Des unités UTF-16 isolées, sans caractère Unicode valide, ont été retirées du XLSX. Les caractères de contrôle valides sont conservés avec l’encodage SpreadsheetML."));
        if (counters.Escaped != 0)
            prepared.Add(("Caractères de contrôle", "Les caractères non représentables directement en XML et les retours chariot sont conservés avec les séquences normalisées SpreadsheetML _xHHHH_. Le CSV natif reste inchangé."));

        using var x = EntryWriter(archive, "xl/worksheets/sheet2.xml");
        StartSheet(x, $"A1:B{prepared.Count + 1}");
        x.WriteStartElement("cols", SpreadsheetNs);
        EmptyElement(x, "col", SpreadsheetNs, ("min", "1"), ("max", "1"), ("width", "29"), ("customWidth", "1"));
        EmptyElement(x, "col", SpreadsheetNs, ("min", "2"), ("max", "2"), ("width", "100"), ("customWidth", "1"));
        x.WriteEndElement();
        x.WriteStartElement("sheetData", SpreadsheetNs);
        StartRow(x, 1, 30); WriteCell(x, 0, 1, "Information", 1); WriteCell(x, 1, 1, "Valeur", 1); x.WriteEndElement();
        for (int i = 0; i < prepared.Count; i++)
        {
            var item = prepared[i];
            int row = i + 2;
            int lineEstimate = Math.Max(1, item.Value.Length / 90 + 1 + item.Value.Count(c => c == '\n'));
            StartRow(x, row, Math.Clamp(lineEstimate * 16 + 8, 25, 120));
            WriteCell(x, 0, row, item.Key, 3); WriteCell(x, 1, row, item.Value, 2); x.WriteEndElement();
        }
        x.WriteEndElement();
        EndSheet(x);
    }

    private static void StartSheet(XmlWriter x, string range)
    {
        x.WriteStartElement("worksheet", SpreadsheetNs);
        EmptyElement(x, "dimension", SpreadsheetNs, ("ref", range));
        x.WriteStartElement("sheetViews", SpreadsheetNs);
        x.WriteStartElement("sheetView", SpreadsheetNs); x.WriteAttributeString("workbookViewId", "0"); x.WriteAttributeString("showGridLines", "0");
        EmptyElement(x, "pane", SpreadsheetNs, ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
        EmptyElement(x, "selection", SpreadsheetNs, ("pane", "bottomLeft"), ("activeCell", "A2"), ("sqref", "A2"));
        x.WriteEndElement(); x.WriteEndElement();
        EmptyElement(x, "sheetFormatPr", SpreadsheetNs, ("defaultRowHeight", "18"));
    }

    private static void EndSheet(XmlWriter x)
    {
        EmptyElement(x, "pageMargins", SpreadsheetNs, ("left", "0.3"), ("right", "0.3"), ("top", "0.5"), ("bottom", "0.5"), ("header", "0.2"), ("footer", "0.2"));
        x.WriteEndElement();
    }

    private static void StartRow(XmlWriter x, int number, int? height = null)
    {
        x.WriteStartElement("row", SpreadsheetNs); x.WriteAttributeString("r", number.ToString(Invariant));
        if (height is { } h) { x.WriteAttributeString("ht", h.ToString(Invariant)); x.WriteAttributeString("customHeight", "1"); }
    }

    private static void WriteCell(XmlWriter x, int column, int row, string value, int style)
    {
        x.WriteStartElement("c", SpreadsheetNs);
        x.WriteAttributeString("r", ColumnName(column) + row.ToString(Invariant));
        x.WriteAttributeString("s", style.ToString(Invariant));
        x.WriteAttributeString("t", "inlineStr");
        x.WriteStartElement("is", SpreadsheetNs);
        x.WriteStartElement("t", SpreadsheetNs);
        x.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        x.WriteString(EncodeSpreadsheetText(value));
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
    }

    private static int MeasureWidth(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        int width = 0, line = 0;
        foreach (char c in value.Take(200))
        {
            if (c is '\r' or '\n') { width = Math.Max(width, line); line = 0; }
            else line++;
        }
        return Math.Min(60, Math.Max(width, line));
    }

    private static string PrepareText(string? value, TextCounters counters)
    {
        value ??= "";
        StringBuilder? clean = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool pair = char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]);
            // Preserve valid Unicode control characters: SpreadsheetML can encode
            // them even when XML 1.0 cannot. Only isolated UTF-16 surrogates lack
            // a valid Unicode value and still require explicitly reported cleanup.
            bool valid = pair || !char.IsSurrogate(c);
            if (!valid)
            {
                clean ??= new StringBuilder(value.Length).Append(value, 0, i);
            }
            else if (clean is not null)
            {
                clean.Append(c);
                if (pair) clean.Append(value[i + 1]);
            }
            if (pair) i++;
        }
        if (clean is not null) { value = clean.ToString(); counters.Cleaned++; }
        if (value.Length > ExcelMaxTextLength)
        {
            int length = ExcelMaxTextLength;
            if (char.IsHighSurrogate(value[length - 1])) length--;
            value = value[..length];
            counters.Truncated++;
        }
        if (value.Any(c => c == '\r' || (!char.IsSurrogate(c) && !XmlConvert.IsXmlChar(c)))) counters.Escaped++;
        return value;
    }

    private static string EncodeSpreadsheetText(string value)
    {
        // ST_Xstring: escape forbidden XML characters, CR and the leading underscore
        // of literal _xHHHH_ text in one pass, without re-escaping generated sequences.
        // Microsoft Office implementation notes: MS-OE376, 2.1.1127.
        StringBuilder? encoded = null;
        for (int i = 0; i < value.Length; i++)
        {
            bool literalEscape = i + 6 < value.Length && value[i] == '_' && value[i + 1] == 'x' && value[i + 6] == '_'
                && IsHex(value[i + 2]) && IsHex(value[i + 3]) && IsHex(value[i + 4]) && IsHex(value[i + 5]);
            if (literalEscape)
            {
                encoded ??= new StringBuilder(value.Length + 12).Append(value, 0, i);
                encoded.Append("_x005F_");
            }
            else if (value[i] == '\r' || (!char.IsSurrogate(value[i]) && !XmlConvert.IsXmlChar(value[i])))
            {
                encoded ??= new StringBuilder(value.Length + 12).Append(value, 0, i);
                encoded.Append("_x").Append(((int)value[i]).ToString("X4", Invariant)).Append('_');
            }
            else if (encoded is not null) encoded.Append(value[i]);
        }
        return encoded?.ToString() ?? value;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string ColumnName(int zeroBased)
    {
        Span<char> buffer = stackalloc char[8];
        int n = zeroBased + 1, start = buffer.Length;
        do { n--; buffer[--start] = (char)('A' + n % 26); n /= 26; } while (n > 0);
        return new string(buffer[start..]);
    }

    private static void WriteDocumentProperties(ZipArchive archive, DateTimeOffset extractedAt)
    {
        const string cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string terms = "http://purl.org/dc/terms/";
        const string xsi = "http://www.w3.org/2001/XMLSchema-instance";
        using (var x = EntryWriter(archive, "docProps/core.xml"))
        {
            x.WriteStartElement("cp", "coreProperties", cp);
            x.WriteAttributeString("xmlns", "dc", null, dc); x.WriteAttributeString("xmlns", "dcterms", null, terms); x.WriteAttributeString("xmlns", "xsi", null, xsi);
            x.WriteElementString("dc", "creator", dc, "GLPI Pilot");
            x.WriteElementString("cp", "lastModifiedBy", cp, "GLPI Pilot");
            x.WriteStartElement("dcterms", "created", terms);
            x.WriteAttributeString("xsi", "type", xsi, "dcterms:W3CDTF");
            x.WriteString(extractedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant)); x.WriteEndElement();
            x.WriteEndElement();
        }
        using (var x = EntryWriter(archive, "docProps/app.xml"))
        {
            const string ns = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
            x.WriteStartElement("Properties", ns);
            x.WriteElementString("Application", ns, "GLPI Pilot");
            x.WriteElementString("AppVersion", ns, "1.0");
            x.WriteEndElement();
        }
    }

    private static XmlWriter EntryWriter(ZipArchive archive, string entry)
    {
        var stream = archive.CreateEntry(entry, CompressionLevel.Optimal).Open();
        return XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = true,
            Indent = false,
            NewLineHandling = NewLineHandling.Entitize,
            CheckCharacters = true
        });
    }

    private static void EmptyElement(XmlWriter x, string name, string ns, params (string Name, string Value)[] attributes)
    {
        x.WriteStartElement(name, ns);
        foreach (var (key, value) in attributes) x.WriteAttributeString(key, value);
        x.WriteEndElement();
    }

    private sealed class TextCounters
    {
        public int Truncated { get; set; }
        public int Cleaned { get; set; }
        public int Escaped { get; set; }
    }
}
