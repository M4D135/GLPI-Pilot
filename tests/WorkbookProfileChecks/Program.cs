using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GlpiPilot.Core;

var passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Workbook-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "pilot.xlsm"); CreateWorkbook(path);
    var original = File.ReadAllBytes(path);
    var inspection = WorkbookPilotImportService.Inspect(path);
    Check(inspection.SuggestedSheet == "Tickets" && inspection.SuggestedHeaderRow == 4, "The incident table is suggested despite a report preceding it and a hidden decoy sheet.");
    Check(inspection.Sheets.SequenceEqual(new[] { "Météo", "Tickets", "Paramétrage" }), "Only visible sheets are proposed.");
    var profile = inspection.Profile ?? throw new Exception("Expected workbook profile.");
    Check(profile.WeatherRules.Count == 5 && profile.WeatherRules.Count(rule => rule.Visible) == 4, "Saved report labels preserve the hidden team without deleting its ordered rule.");
    Check(profile.WeatherRules.Where(rule => rule.Kind == "team").Select(rule => rule.Label).SequenceEqual(new[] { "Équipe Alpha", "Équipe réserve" }), "The source order of exclusive team rules is preserved.");
    Check(profile.WeatherRules.Single(rule => rule.Label == "Applications").IncludeDescendants, "Category branches retain descendant matching.");
    var reserve = profile.WeatherRules.Single(rule => rule.Label == "Équipe réserve");
    Check(!reserve.Visible && reserve.WeatherEnabled, "Hidden cards remain enabled for assignment priority.");
    Check(!profile.WeatherRules.Single(rule => rule.Label == "Réseau").WeatherEnabled, "An explicitly disabled weather colour remains distinct from visibility.");
    Check(profile.WeatherRules[1].OrangeAbove == 2, "Only the saved formula value is read; no formula is evaluated.");
    Check(profile.FreshnessWarningHours == 48 && profile.Notes == "Analyse synthétique du service.", "Freshness and the editable analysis note are recovered.");
    Check(profile.Sites.Count == 2 && profile.Sites.Sum(site => site.Aliases.Count) == 2, "Site aliases are normalized without counting canonical names or accent variants twice.");
    var city = new CityDefinition { Name = "Ville de test", Latitude = 42, Longitude = 2 };
    var existing = new SiteDefinition { Name = "Campus Nord", CityId = city.Id, Latitude = 42.1, Longitude = 2.1, Aliases = new() { "CN" } };
    var building = new BuildingDefinition { Name = "Bâtiment témoin", SiteId = existing.Id };
    var current = new AppSettings { Cities = new() { Cities = new() { city }, Sites = new() { existing }, Buildings = new() { building } } };
    current.Dashboard.Business.Notes = "Note précédente";
    current.Normalize();
    var initial = JsonSerializer.Serialize(current);
    var merged = profile.ApplyTo(current);
    Check(merged.Cities.Sites.Count == 2, "Matching an existing site by its exact normalized name avoids a duplicate.");
    var retained = merged.Cities.Sites.Single(site => site.Name == "Campus Nord");
    Check(retained.Id == existing.Id && retained.CityId == city.Id && retained.Latitude == 42.1 && merged.Cities.Buildings[0].SiteId == existing.Id, "Import preserves site identity, town, coordinates and buildings.");
    Check(retained.Aliases.Order().SequenceEqual(new[] { "CN", "NORD" }.Order()), "Existing aliases are merged with the workbook aliases.");
    Check(JsonSerializer.Serialize(current) == initial, "Preparing a workbook profile does not mutate the current configuration.");
    Check(profile.ApplyTo(merged).Cities.Sites.Count == 2, "Repeating the same workbook import is idempotent for sites.");
    Check(merged.Dashboard.Layout.Boards.Count == current.Dashboard.Layout.Boards.Count, "Native pilot parameters do not consume or replace user dashboard slots.");
    Check(File.ReadAllBytes(path).SequenceEqual(original), "Inspection leaves the XLSM byte-for-byte unchanged.");

    var missing = Path.Combine(root, "missing-cache.xlsx"); CreateWorkbook(missing, missingFormulaCache: true);
    var missingInspection = WorkbookPilotImportService.Inspect(missing);
    Check(missingInspection.Profile is null && missingInspection.Warnings.Count == 1 && missingInspection.SuggestedHeaderRow == 4, "An unavailable threshold does not become zero; the table remains importable with a profile warning.");
    var external = Path.Combine(root, "external.xlsx"); CreateWorkbook(external, externalParameters: true);
    Check(WorkbookPilotImportService.Inspect(external).Profile is null, "An external parameter relationship is never followed.");
    var copy = Path.Combine(root, "renamed-copy.xlsx"); File.Copy(path, copy);
    Check(WorkbookPilotImportService.Inspect(copy).Profile!.Sites.Select(site => site.Id).SequenceEqual(profile.Sites.Select(site => site.Id)), "Stable site identities do not depend on the workbook filename.");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { WorkbookPilotImportService.Inspect(path, cancelled.Token); throw new Exception("Expected cancellation."); }
    catch (OperationCanceledException) { Check(true, "Cancellation is observed before opening or applying the workbook."); }
    Console.WriteLine($"{passed}/{passed} workbook profile checks passed.");
    var inspectAt = Array.IndexOf(args, "--inspect-workbook");
    if (inspectAt >= 0 && inspectAt + 1 < args.Length)
    {
        var actual = WorkbookPilotImportService.Inspect(args[inspectAt + 1]);
        Console.WriteLine(JsonSerializer.Serialize(new { actual.SuggestedSheet, actual.SuggestedHeaderRow,
            Rules = actual.Profile?.WeatherRules.Count, Visible = actual.Profile?.WeatherRules.Count(rule => rule.Visible),
            Sites = actual.Profile?.Sites.Count, Aliases = actual.Profile?.Sites.Sum(site => site.Aliases.Count),
            actual.Warnings, ProfileWarnings = actual.Profile?.Warnings }));
    }
}
finally { Directory.Delete(root, true); }

static void CreateWorkbook(string path, bool missingFormulaCache = false, bool externalParameters = false)
{
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    XNamespace doc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    XNamespace xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    XNamespace draw = "http://schemas.openxmlformats.org/drawingml/2006/main";
    var cells = new Dictionary<string, string>
    {
        ["A1"] = "PARAMETRES METEO V4.2 STOCK", ["B6"] = "48", ["A13"] = "Analyse et actions en cours (texte libre)", ["B14"] = "Analyse synthétique du service.",
        ["A19"] = "Périmètre", ["B19"] = "Étiquette affichée", ["C19"] = "Correspondance GLPI (séparateur |)", ["D19"] = "Météo active", ["E19"] = "Orange au-delà de", ["F19"] = "Rouge au-delà de",
        ["B80"] = "Site affiché", ["C80"] = "Écritures reconnues (séparateur ;)", ["B81"] = "Campus Nord", ["C81"] = "NORD;Campus Nord;nord;Nörd", ["B82"] = "Agence Est", ["C82"] = "EST"
    };
    void Rule(int row, string kind, string label, string matches, string enabled, string orange, string red)
    { var values = new[] { kind, label, matches, enabled, orange, red }; for (var column = 0; column < values.Length; column++) cells[((char)('A' + column)).ToString() + row] = values[column]; }
    Rule(20, "GLOBAL", "Situation générale", "", "Oui", "10", "20");
    Rule(21, "CATEGORIE", "Applications", "Applications", "Oui", "2", "8");
    Rule(22, "CATEGORIE", "Réseau", "Réseau", "Non", "3", "9");
    Rule(23, "GROUPE", "Équipe Alpha", "Groupe Alpha|Groupe Deux", "Oui", "4", "10");
    Rule(24, "GROUPE", "Équipe réserve", "Groupe Réserve", "Oui", "5", "11");
    XElement Sheet(Dictionary<string, string> values) => new(ns + "worksheet", new XElement(ns + "sheetData",
        values.GroupBy(pair => int.Parse(new string(pair.Key.Where(char.IsDigit).ToArray()))).OrderBy(group => group.Key).Select(group => new XElement(ns + "row", new XAttribute("r", group.Key),
            group.Select(pair => new XElement(ns + "c", new XAttribute("r", pair.Key), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", pair.Value))))))));
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    void Write(string name, XElement value) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(new XDocument(value).ToString(SaveOptions.DisableFormatting)); }
    Write("xl/workbook.xml", new(ns + "workbook", new XElement(ns + "sheets",
        new XElement(ns + "sheet", new XAttribute("name", "Météo"), new XAttribute("sheetId", "1"), new XAttribute(doc + "id", "r1")),
        new XElement(ns + "sheet", new XAttribute("name", "Tickets"), new XAttribute("sheetId", "2"), new XAttribute(doc + "id", "r2")),
        new XElement(ns + "sheet", new XAttribute("name", "Paramétrage"), new XAttribute("sheetId", "3"), new XAttribute(doc + "id", "r3")),
        new XElement(ns + "sheet", new XAttribute("name", "Cachée"), new XAttribute("state", "hidden"), new XAttribute("sheetId", "4"), new XAttribute(doc + "id", "r4")))));
    Write("xl/_rels/workbook.xml.rels", new(rel + "Relationships", Enumerable.Range(1, 4).Select(index => new XElement(rel + "Relationship", new XAttribute("Id", "r" + index),
        new XAttribute("Type", doc.NamespaceName + "/worksheet"), new XAttribute("Target", index == 3 && externalParameters ? "https://external.invalid/parameters.xml" : "worksheets/sheet" + index + ".xml"),
        index == 3 && externalParameters ? new XAttribute("TargetMode", "External") : null))));
    Write("xl/worksheets/sheet1.xml", Sheet(new() { ["A1"] = "Météo" }));
    Write("xl/worksheets/sheet2.xml", Sheet(new() { ["A1"] = "Liste d’incidents", ["A4"] = "ID", ["B4"] = "Titre", ["C4"] = "Statut", ["D4"] = "Catégorie", ["A5"] = "1001", ["B5"] = "Test Campus Nord", ["C5"] = "Nouveau", ["D5"] = "Applications" }));
    var parameters = Sheet(cells);
    parameters.Descendants(ns + "c").Single(cell => (string?)cell.Attribute("r") == "E21").ReplaceWith(new XElement(ns + "c", new XAttribute("r", "E21"), new XElement(ns + "f", "1+1"), missingFormulaCache ? null : new XElement(ns + "v", "2")));
    Write("xl/worksheets/sheet3.xml", parameters);
    Write("xl/worksheets/sheet4.xml", Sheet(new() { ["A1"] = "ID", ["B1"] = "Titre", ["C1"] = "Statut", ["D1"] = "Catégorie", ["E1"] = "Priorité" }));
    Write("xl/worksheets/_rels/sheet1.xml.rels", new(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Id", "d1"), new XAttribute("Type", doc.NamespaceName + "/drawing"), new XAttribute("Target", "../drawings/drawing1.xml"))));
    Write("xl/drawings/drawing1.xml", new(xdr + "wsDr", new[] { "Applications", "Réseau", "Équipe Alpha" }.Select(label => new XElement(xdr + "twoCellAnchor", new XElement(xdr + "sp", new XElement(xdr + "nvSpPr", new XElement(xdr + "cNvPr", new XAttribute("id", "1"), new XAttribute("name", label))), new XElement(xdr + "txBody", new XElement(draw + "p", new XElement(draw + "r", new XElement(draw + "t", label)))))))));
    using var macro = zip.CreateEntry("xl/vbaProject.bin").Open(); macro.Write(Encoding.ASCII.GetBytes("SYNTHETIC INERT CONTENT"));
}
