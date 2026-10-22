using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GlpiPilot.Core;

/// <summary>Reads cached SpreadsheetML values. Never evaluates formulas or follows external relationships.</summary>
public static class XlsxImportService
{
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    public static ExtractionResult Read(string path, int maximumRows = 100000, CancellationToken ct = default)
    {
        if (maximumRows is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        using var file = File.OpenRead(path);
        if (file.Length > MaximumExpandedBytes) throw new InvalidDataException("Le classeur dépasse la limite de taille autorisée.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count > 20000 || archive.Entries.Sum(entry => entry.Length) > MaximumExpandedBytes)
            throw new InvalidDataException("Le contenu décompressé du classeur dépasse la limite autorisée.");
        var workbook = Load(archive, "xl/workbook.xml");
        var sheet = workbook.Descendants(Ns + "sheet").FirstOrDefault(element => (string?)element.Attribute("state") is null or "visible")
            ?? throw new InvalidDataException("Le classeur ne possède aucune feuille visible.");
        var id = (string?)sheet.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"));
        var relationships = Load(archive, "xl/_rels/workbook.xml.rels");
        var relation = relationships.Descendants(Rel + "Relationship").FirstOrDefault(element => (string?)element.Attribute("Id") == id)
            ?? throw new InvalidDataException("La feuille référencée est absente du classeur.");
        if ((string?)relation.Attribute("TargetMode") == "External") throw new InvalidDataException("Une feuille externe ne peut pas être importée.");
        var target = (string?)relation.Attribute("Target") ?? "";
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        if (sheetPath.Contains("..", StringComparison.Ordinal) || sheetPath.Contains('\\') || !sheetPath.StartsWith("xl/worksheets/", StringComparison.Ordinal))
            throw new InvalidDataException("Le chemin de la feuille n’est pas un composant interne reconnu.");
        var strings = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is { } stringsEntry)
        {
            using var stream = stringsEntry.Open(); using var reader = Reader(stream);
            while (!reader.EOF)
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") { reader.Read(); continue; }
                var element = (XElement)XNode.ReadFrom(reader);
                strings.Add(Decode(string.Concat(element.Descendants(Ns + "t").Select(text => text.Value))));
                if (strings.Count > 1000000) throw new InvalidDataException("Le classeur contient trop de chaînes partagées.");
            }
        }
        var formats = new Dictionary<int, string>(); var styles = new List<int>();
        if (archive.GetEntry("xl/styles.xml") != null)
        {
            var style = Load(archive, "xl/styles.xml");
            foreach (var format in style.Descendants(Ns + "numFmt"))
                if (int.TryParse((string?)format.Attribute("numFmtId"), out var number)) formats[number] = (string?)format.Attribute("formatCode") ?? "";
            styles.AddRange(style.Root?.Element(Ns + "cellXfs")?.Elements(Ns + "xf").Select(element => (int?)element.Attribute("numFmtId") ?? 0) ?? Array.Empty<int>());
        }
        var date1904 = (string?)workbook.Root?.Element(Ns + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var result = new ExtractionResult { SourceUrl = "", Title = Path.GetFileName(path), Scope = "Classeur local · provenance GLPI non confirmée", Complete = false,
            Provenance = new() { SourceKind = "file", Method = "xlsx", OriginalFileName = Path.GetFileName(path) } };
        int count = 0, formulas = 0; bool headerRead = false;
        using var sheetStream = (archive.GetEntry(sheetPath) ?? throw new InvalidDataException("La feuille du classeur est introuvable.")).Open();
        using var xml = Reader(sheetStream);
        while (!xml.EOF)
        {
            ct.ThrowIfCancellationRequested();
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "row") { xml.Read(); continue; }
            var row = (XElement)XNode.ReadFrom(xml); var values = new SortedDictionary<int, string>();
            foreach (var cell in row.Elements(Ns + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? ""; int column = Column(reference);
                if (column < 0 || column >= 16384) throw new InvalidDataException("Une adresse de cellule dépasse les limites du classeur.");
                if (cell.Element(Ns + "f") != null) formulas++;
                var value = cell.Element(Ns + "v")?.Value ?? ""; var type = (string?)cell.Attribute("t");
                if (type == "s")
                {
                    if (!int.TryParse(value, out var index) || index < 0 || index >= strings.Count) throw new InvalidDataException("Une chaîne partagée du classeur est introuvable.");
                    value = strings[index];
                }
                else if (type == "inlineStr") value = Decode(string.Concat(cell.Descendants(Ns + "t").Select(text => text.Value)));
                else if (type == "b") value = value == "1" ? "Vrai" : "Faux";
                else if (type is null or "n")
                {
                    var styleIndex = (int?)cell.Attribute("s") ?? 0; var formatId = styleIndex >= 0 && styleIndex < styles.Count ? styles[styleIndex] : 0;
                    var format = formats.GetValueOrDefault(formatId, "");
                    if (IsDateFormat(formatId, format) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                    {
                        try { value = DateTime.FromOADate(serial + (date1904 ? 1462 : 0)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
                        catch (ArgumentException) { }
                    }
                    else if (Regex.IsMatch(format, "^0{2,32}$") && ulong.TryParse(value, out var padded)) value = padded.ToString(new string('0', format.Length), CultureInfo.InvariantCulture);
                }
                else value = Decode(value);
                if (value.Length > 1000000) throw new InvalidDataException("Une cellule du classeur dépasse la taille autorisée.");
                if (!values.TryAdd(column, value)) throw new InvalidDataException("Une ligne contient plusieurs fois la même adresse de cellule.");
            }
            if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace)) continue;
            if (!headerRead)
            {
                var width = values.Keys.Max() + 1;
                result.Headers = Enumerable.Range(0, width).Select(column => values.GetValueOrDefault(column, "").Trim()).Select((text, index) => text.Length == 0 ? $"Colonne {index + 1}" : text).ToList();
                headerRead = true; continue;
            }
            count++;
            if (values.Keys.Max() >= result.Headers.Count) throw new InvalidDataException("Une ligne possède des cellules au-delà des colonnes d’en-tête ; import interrompu pour éviter une perte.");
            if (result.Rows.Count < maximumRows) result.Rows.Add(Enumerable.Range(0, result.Headers.Count).Select(column => values.GetValueOrDefault(column, "")).ToArray());
        }
        if (!headerRead) throw new InvalidDataException("La première feuille visible ne contient pas de tableau.");
        result.Pages = 1;
        result.Warnings.Add($"Feuille lue : {(string?)sheet.Attribute("name") ?? "sans nom"}. La première ligne non vide sert d’en-tête. La portée GLPI n’est pas confirmée.");
        if (count > maximumRows) result.Warnings.Add($"{count:N0} lignes dans la feuille, {maximumRows:N0} conservées selon la limite d’import.");
        if (formulas > 0) result.Warnings.Add($"{formulas:N0} cellules contiennent une formule : seules leurs valeurs enregistrées ont été lues, sans recalcul.");
        result.Status = $"Classeur local importé · {result.Rows.Count:N0} lignes · complétude GLPI non confirmée";
        result.Provenance.FinishedAt = DateTimeOffset.Now;
        return result;
    }
    private static XDocument Load(ZipArchive archive, string name)
    {
        using var stream = (archive.GetEntry(name) ?? throw new InvalidDataException("Composant du classeur absent : " + name)).Open();
        using var reader = Reader(stream); return XDocument.Load(reader);
    }
    private static XmlReader Reader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumExpandedBytes });
    private static int Column(string reference)
    {
        int value = 0, letters = 0;
        foreach (var letter in reference)
        { if (letter is < 'A' or > 'Z') break; value = checked(value * 26 + letter - 'A' + 1); if (++letters > 3) return -1; }
        return letters == 0 ? -1 : value - 1;
    }
    private static string Decode(string text) => Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
    private static bool IsDateFormat(int id, string format)
    {
        if (id is >= 14 and <= 22 or >= 45 and <= 47) return true;
        var stripped = Regex.Replace(format, "\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", "");
        return Regex.IsMatch(stripped, "[ydhs]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
