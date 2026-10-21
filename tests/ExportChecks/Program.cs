using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GlpiPilot.Core;

string temporary = Path.Combine(Path.GetTempPath(), "GlpiPilot-ExportChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var checks = new List<(string Name, Action Run)>
{
    ("XLSX: valeurs littérales, Unicode, filtres et métadonnées", CheckXlsx),
    ("XLSX: longueurs et Unicode invalide signalés, contrôles conservés", CheckLongText),
    ("XLSX: contrôles C0/C1 et séquences littérales conservés après décodage", CheckControlRoundTrip),
    ("CSV: BOM, guillemets, retours à la ligne et protection des formules", CheckCsv),
    ("Échec d’export: fichier existant conservé et temporaires nettoyés", CheckAtomicFailure),
    ("Dimensions: aucune cellule supplémentaire perdue silencieusement", CheckDimensions)
};
int failures = 0;
try
{
    foreach (var check in checks)
    {
        try { check.Run(); Console.WriteLine("PASS " + check.Name); }
        catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + check.Name + "\n" + e); }
    }
    Console.WriteLine($"{checks.Count - failures}/{checks.Count} vérifications réussies.");
}
finally
{
    Directory.Delete(temporary, recursive: true);
}
return failures == 0 ? 0 : 1;

ExtractionResult Sample() => new()
{
    Headers = new() { "Identifiant", "Libellé", "Description" },
    Rows = new()
    {
        new[] { "00042", "=1+1", "école & <Paris> \"test\"\r\nligne\t2" },
        new[] { "00200", "😃", "_x000A_ et _x005F_" },
        new[] { "z", "  @SUM(A1:A2)", "0" }
    },
    SourceUrl = "https://glpi.example.invalid/front/ticket.php?criteria=ouvert&start=0",
    Title = "Tickets à traiter",
    ExtractedAt = new DateTimeOffset(2026, 10, 21, 10, 20, 30, TimeSpan.FromHours(2)),
    Scope = "Toutes les pages",
    Complete = false,
    Status = "Interruption à la page 2",
    Pages = 2,
    ExpectedTotal = 100,
    Warnings = new() { "Le total récupéré diffère du total annoncé." }
};

void CheckXlsx()
{
    var data = Sample();
    string path = Path.Combine(temporary, "literal.xlsx");
    var result = ExportService.WriteXlsx(data, path);
    Equal(3, result.Rows); Equal(3, result.Columns); Equal(0, result.TruncatedCells);
    using var archive = ZipFile.OpenRead(path);
    foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
    {
        using var stream = entry.Open();
        _ = XDocument.Load(stream);
    }
    var document = ReadXml(archive, "xl/worksheets/sheet1.xml");
    XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var cells = document.Descendants(n + "c").ToDictionary(c => (string)c.Attribute("r")!, c => c);
    Equal(12, cells.Count);
    True(cells.Values.All(c => (string?)c.Attribute("t") == "inlineStr"), "Toutes les cellules doivent rester du texte.");
    Equal(0, document.Descendants(n + "f").Count());
    Equal("00042", Cell(cells["A2"]));
    Equal("=1+1", Cell(cells["B2"]));
    Equal(data.Rows[0][2], DecodeSpreadsheet(Cell(cells["C2"])));
    Equal("😃", Cell(cells["B3"]));
    Equal("_x005F_x000A_ et _x005F_x005F_", Cell(cells["C3"]));
    Equal("A1:C4", (string?)document.Descendants(n + "autoFilter").Single().Attribute("ref"));
    Equal("frozen", (string?)document.Descendants(n + "pane").Single().Attribute("state"));
    var info = Information(archive);
    Equal("Partiel / exhaustivité non confirmée", info["État"]);
    Equal("100", info["Total annoncé"]);
    Equal("3", info["Lignes exportées"]);
    Equal(data.SourceUrl, info["Source"]);
    Equal(data.Status, info["Détail"]);
    Equal("2026-10-21 10:20:30 +02:00", info["Date de l’extraction"]);
    var workbook = ReadXml(archive, "xl/workbook.xml");
    Equal("Données,Informations", string.Join(',', workbook.Descendants(n + "sheet").Select(s => (string?)s.Attribute("name"))));
}

void CheckLongText()
{
    var data = new ExtractionResult
    {
        Headers = new() { "Exact", "Long", "Paire", "Contrôle" },
        Rows = new() { new[] { new string('a', 32_767), new string('b', 32_768), new string('c', 32_766) + "😀", "a\u0001b\uD800c" } },
        Warnings = new() { new string('w', 32_768) },
        Complete = true
    };
    var result = ExportService.WriteXlsx(data, Path.Combine(temporary, "long.xlsx"));
    Equal(3, result.TruncatedCells);
    using var archive = ZipFile.OpenRead(result.Path);
    XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var cells = ReadXml(archive, "xl/worksheets/sheet1.xml").Descendants(n + "c").ToDictionary(c => (string)c.Attribute("r")!, c => c);
    Equal(32_767, Cell(cells["A2"]).Length);
    Equal(32_767, Cell(cells["B2"]).Length);
    Equal(32_766, Cell(cells["C2"]).Length);
    Equal("a_x0001_bc", Cell(cells["D2"]));
    Equal("a\u0001bc", DecodeSpreadsheet(Cell(cells["D2"])));
    var info = Information(archive);
    Equal("3", info["Cellules tronquées"]);
    Equal("1", info["Cellules nettoyées"]);
    Equal("1", info["Cellules avec caractères encodés"]);
    Equal("Complet pour le périmètre demandé", info["État"]);
    True(info.ContainsKey("Limite de longueur"), "La troncature doit être signalée dans le classeur.");
}

void CheckControlRoundTrip()
{
    string controls = string.Concat(Enumerable.Range(0, 32).Select(i => (char)i))
        + "\u007F\u0081\u0085\u009D\u009F\uFFFE\uFFFF";
    string[] values =
    {
        "avant\vtexte\faprès\r\nfin\u0085suite\u009D",
        "_x000B_ _x000b_ _x005F_ _x005F_x000B_ _x000B_x000C_ __x000D_",
        controls,
        "😀 <test> & tab\t\r\n _xD83D_ _xDE00_",
        new string('a', 32_760) + "_x000B_"
    };
    var data = new ExtractionResult
    {
        Headers = new() { "Texte\v_x000B_" },
        Rows = values.Select(v => new[] { v }).ToList(),
        Warnings = new() { "Diagnostic\f_x000C_" },
        Complete = true
    };
    var result = ExportService.WriteXlsx(data, Path.Combine(temporary, "controls.xlsx"));
    Equal(0, result.TruncatedCells);
    using var archive = ZipFile.OpenRead(result.Path);
    foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
    {
        using var stream = entry.Open();
        _ = XDocument.Load(stream);
    }
    XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var cells = ReadXml(archive, "xl/worksheets/sheet1.xml").Descendants(n + "c").ToList();
    Equal(data.Headers[0], DecodeSpreadsheet(Cell(cells[0])));
    for (int i = 0; i < values.Length; i++) Equal(values[i], DecodeSpreadsheet(Cell(cells[i + 1])));
    var info = Information(archive);
    Equal("0", info["Cellules nettoyées"]);
    Equal("5", info["Cellules avec caractères encodés"]);
    Equal(data.Warnings[0], DecodeSpreadsheet(info["Avertissement 1"]));
    True(Cell(cells[1]).Contains("_x000B_") && Cell(cells[1]).Contains("_x000C_") && Cell(cells[1]).Contains("_x000D_"), "Le texte XML doit employer les échappements SpreadsheetML.");
}

void CheckCsv()
{
    var data = Sample();
    data.Headers[1] = "=En-tête";
    data.Rows.Add(new[] { "  =2+2", "+123", "\tvaleur" });
    data.Rows.Add(new[] { "\uFEFF=2+2", "-45", "semi;colonnes\n\"entre guillemets\"" });
    data.Rows.Add(new[] { "00123" });
    var result = ExportService.WriteCsv(data, Path.Combine(temporary, "export.csv"));
    byte[] bytes = File.ReadAllBytes(result.Path);
    True(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "Le CSV doit inclure le BOM UTF-8.");
    var records = ReadCsv(Encoding.UTF8.GetString(bytes.AsSpan(3)));
    Equal(data.Rows.Count + 1, records.Count);
    True(records.All(r => r.Count == 3), "Chaque ligne doit garder le même nombre de colonnes.");
    Equal("'=En-tête", records[0][1]);
    Equal("00042", records[1][0]);
    Equal("'=1+1", records[1][1]);
    Equal(data.Rows[0][2], records[1][2]);
    Equal("'  @SUM(A1:A2)", records[3][1]);
    Equal("'  =2+2", records[4][0]);
    Equal("'+123", records[4][1]);
    Equal("'\tvaleur", records[4][2]);
    Equal("'\uFEFF=2+2", records[5][0]);
    Equal("'-45", records[5][1]);
    Equal(data.Rows[4][2], records[5][2]);
    Equal("", records[6][2]);
    Equal(0, result.TruncatedCells);
}

void CheckAtomicFailure()
{
    string target = Path.Combine(temporary, "preserve.csv");
    File.WriteAllText(target, "FICHIER INITIAL", Encoding.UTF8);
    var data = Sample();
    data.Rows.Add(new[] { "1", "2", "3", "cellule supplémentaire" });
    Throws<InvalidOperationException>(() => ExportService.WriteCsv(data, target));
    Equal("FICHIER INITIAL", File.ReadAllText(target, Encoding.UTF8));

    string folderTarget = Path.Combine(temporary, "existing-directory.xlsx");
    Directory.CreateDirectory(folderTarget);
    File.WriteAllText(Path.Combine(folderTarget, "sentinel.txt"), "original");
    Throws<IOException>(() => ExportService.WriteXlsx(Sample(), folderTarget));
    Equal("original", File.ReadAllText(Path.Combine(folderTarget, "sentinel.txt")));
    Equal(0, Directory.GetFiles(temporary, ".glpi-export-*.tmp").Length);

    ExportService.WriteCsv(Sample(), target);
    True(File.ReadAllText(target).Contains("00042", StringComparison.Ordinal), "Un export réussi doit remplacer le fichier précédent.");
}

void CheckDimensions()
{
    var invalid = Sample(); invalid.Headers.Clear();
    Throws<InvalidOperationException>(() => ExportService.WriteXlsx(invalid, Path.Combine(temporary, "no-columns.xlsx")));
    var wide = new ExtractionResult { Headers = Enumerable.Range(0, 16_385).Select(i => "C" + i).ToList() };
    Throws<InvalidOperationException>(() => ExportService.WriteXlsx(wide, Path.Combine(temporary, "too-wide.xlsx")));
    True(!File.Exists(Path.Combine(temporary, "too-wide.xlsx")), "Aucun classeur invalide ne doit être laissé.");
    var empty = new ExtractionResult { Headers = new() { "A", "B" }, Complete = true };
    var result = ExportService.WriteXlsx(empty, Path.Combine(temporary, "empty.xlsx"));
    using var archive = ZipFile.OpenRead(result.Path);
    XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    Equal(1, ReadXml(archive, "xl/worksheets/sheet1.xml").Descendants(n + "row").Count());
}

static string Cell(XElement cell)
{
    XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    return string.Concat(cell.Descendants(n + "t").Select(t => t.Value));
}

// Single-pass ST_Xstring decoding: an escaped underscore must not cause the
// literal sequence following it to be decoded a second time.
static string DecodeSpreadsheet(string value) => Regex.Replace(value, "_x([0-9a-fA-F]{4})_",
    match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());

static XDocument ReadXml(ZipArchive archive, string name)
{
    using var stream = (archive.GetEntry(name) ?? throw new Exception("Entrée ZIP manquante : " + name)).Open();
    return XDocument.Load(stream);
}

static Dictionary<string, string> Information(ZipArchive archive)
{
    XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    return ReadXml(archive, "xl/worksheets/sheet2.xml").Descendants(n + "row").Skip(1)
        .ToDictionary(r => Cell(r.Elements(n + "c").First()), r => Cell(r.Elements(n + "c").Last()));
}

static List<List<string>> ReadCsv(string text)
{
    var records = new List<List<string>>(); var row = new List<string>(); var value = new StringBuilder(); bool quoted = false;
    for (int i = 0; i < text.Length; i++)
    {
        char c = text[i];
        if (c == '"')
        {
            if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
            else quoted = !quoted;
        }
        else if (!quoted && c == ';') { row.Add(value.ToString()); value.Clear(); }
        else if (!quoted && c is '\r' or '\n')
        {
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            row.Add(value.ToString()); value.Clear(); records.Add(row); row = new();
        }
        else value.Append(c);
    }
    if (quoted) throw new Exception("Champ CSV non terminé.");
    if (value.Length != 0 || row.Count != 0) { row.Add(value.ToString()); records.Add(row); }
    return records;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Attendu : {expected}\nObtenu : {actual}");
}

static void True(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Exception attendue : " + typeof(T).Name);
}
