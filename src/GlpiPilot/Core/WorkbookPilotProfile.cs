using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GlpiPilot.Core;

public sealed class WorkbookPilotProfile
{
    public string SourceFile { get; set; } = "";
    public string ParameterSheet { get; set; } = "";
    public List<BusinessWeatherRule> WeatherRules { get; set; } = new();
    public List<SiteDefinition> Sites { get; set; } = new();
    public string Notes { get; set; } = "";
    public int? FreshnessWarningHours { get; set; } = 48;
    public List<string> Warnings { get; set; } = new();

    /// <summary>Applies an explicitly selected workbook profile, preserving existing geography and other boards.</summary>
    public AppSettings ApplyTo(AppSettings current)
    {
        var next = current.Copy();
        if (WeatherRules.Count > 0)
        {
            next.Dashboard.Business.WeatherRules = WeatherRules.Select(rule => rule.Copy()).ToList();
            next.Dashboard.Business.Notes = Notes;
            next.Dashboard.Business.FreshnessWarningHours = FreshnessWarningHours;
            next.Dashboard.Business.BoardOverrides.RemoveAll(board => board.Id == "business-weather");
        }
        foreach (var imported in Sites)
        {
            var sameId = next.Cities.Sites.SingleOrDefault(site => site.Id == imported.Id);
            var sameNames = next.Cities.Sites.Where(site => WorkbookPilotImportService.Fold(site.Name) == WorkbookPilotImportService.Fold(imported.Name)).ToArray();
            if (sameId is null && sameNames.Length > 1)
                throw new InvalidDataException($"Plusieurs sites portent le nom « {imported.Name} ». Résolvez ce doublon avant de reprendre le référentiel du classeur.");
            var existing = sameId ?? sameNames.SingleOrDefault();
            if (existing is null) next.Cities.Sites.Add(imported.Copy());
            else
            {
                // Names are matched exactly after the same word normalization used
                // by the site engine. Coordinates, town and building identities stay intact.
                existing.Aliases = existing.Aliases.Concat(imported.Aliases)
                    .Where(alias => WorkbookPilotImportService.Fold(alias) != WorkbookPilotImportService.Fold(existing.Name))
                    .GroupBy(WorkbookPilotImportService.Fold, StringComparer.Ordinal).Select(group => group.First()).ToList();
            }
        }
        next.Dashboard.Business.Validate(); next.Cities.Validate();
        return next;
    }
}

public sealed record WorkbookPilotInspection(IReadOnlyList<string> Sheets, string SuggestedSheet, int SuggestedHeaderRow,
    WorkbookPilotProfile? Profile, IReadOnlyList<string> Warnings);

/// <summary>Reads the report's explicit parameter cells and saved drawing labels, never VBA or executable formulas.</summary>
public static class WorkbookPilotImportService
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relations = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace DocumentRelations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal static string Fold(string value) => string.Join(' ', CityText.Tokens(value)).ToLowerInvariant();
    private static string StableId(string kind, string value) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("GLPI-Pilot/workbook/" + kind + "/" + Fold(value)))[..16]).ToString("N");

    public static WorkbookPilotInspection Inspect(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new WorkbookReader(path, ct);
        var names = reader.Sheets.Keys.ToArray();
        var suggested = names.First(); var header = 0; var best = 0;
        var candidates = names.OrderByDescending(name => Fold(name) is "tickets" or "incidents").Take(32);
        foreach (var name in candidates)
        {
            var sheet = reader.ReadSheet(name, 64, 128);
            foreach (var row in sheet.Cells.GroupBy(pair => pair.Key.Row))
            {
                var words = row.Select(pair => Fold(pair.Value)).ToHashSet(StringComparer.Ordinal);
                if (!words.Contains("id") && !words.Contains("identifiant")) continue;
                var score = new[] { "id", "titre", "statut", "date d ouverture", "derniere modification", "priorite", "categorie", "description", "type" }.Count(words.Contains);
                if (score < 2 || score <= best) continue;
                suggested = name; header = row.Key; best = score;
            }
            if (best >= 7) break;
        }
        var warnings = new List<string>(); WorkbookPilotProfile? profile = null;
        foreach (var name in names.Where(name => Fold(name) is "parametrage" or "parametres" or "parameters").Take(4))
        {
            var sheet = reader.ReadSheet(name, 8192, 32);
            try
            {
                profile = ParseProfile(Path.GetFileName(path), name, sheet);
                if (profile is null) continue;
                var labels = reader.VisibleWeatherLabels(names.Where(value => Fold(value).Contains("meteo", StringComparison.Ordinal)));
                var rules = profile.WeatherRules.Where(rule => rule.Kind is "category" or "team").ToArray();
                var matches = rules.Count(rule => labels.Contains(Fold(rule.Label)));
                // Only a substantially complete saved report proves that an absent
                // card is hidden. Cached counts are never imported as current data.
                if (rules.Length >= 3 && matches >= Math.Max(3, rules.Length - 2))
                {
                    foreach (var rule in rules) rule.Visible &= labels.Contains(Fold(rule.Label));
                    profile.Warnings.Add("La visibilité des cartes est reprise des libellés de la météo enregistrée ; leurs anciens volumes ne sont pas importés.");
                }
                break;
            }
            catch (InvalidDataException error)
            {
                warnings.Add($"Paramètres de « {name} » non repris : {error.Message}"); profile = null;
            }
        }
        return new(names, suggested, header, profile, warnings);
    }

    private static WorkbookPilotProfile? ParseProfile(string file, string name, SheetCells sheet)
    {
        var profile = new WorkbookPilotProfile { SourceFile = file, ParameterSheet = name };
        var weatherHeader = sheet.Cells.FirstOrDefault(pair => Fold(pair.Value) == "perimetre"
            && Fold(sheet.At(pair.Key.Row, pair.Key.Column + 1)).Contains("etiquette", StringComparison.Ordinal));
        if (!weatherHeader.Equals(default(KeyValuePair<(int Row, int Column), string>)))
        {
            var column = weatherHeader.Key.Column;
            for (var row = weatherHeader.Key.Row + 1; row <= weatherHeader.Key.Row + 100; row++)
            {
                var kind = Fold(sheet.At(row, column));
                if (kind is not ("global" or "categorie" or "groupe" or "type")) break;
                if (profile.WeatherRules.Count == 48) throw new InvalidDataException("Le profil dépasse 48 cartes météo.");
                var label = sheet.At(row, column + 1).Trim();
                if (label.Length == 0) throw new InvalidDataException($"La carte de la ligne {row} n’a pas de libellé.");
                var values = sheet.At(row, column + 2).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
                var enabled = Fold(sheet.At(row, column + 3));
                if (enabled is not ("oui" or "non" or "true" or "false" or "1" or "0")) throw new InvalidDataException($"L’activation météo de la ligne {row} est inconnue.");
                var orange = Threshold(sheet.At(row, column + 4), row); var red = Threshold(sheet.At(row, column + 5), row);
                if (orange.HasValue != red.HasValue || orange.HasValue && red <= orange)
                    throw new InvalidDataException($"La ligne {row} exige deux seuils entiers, avec le rouge supérieur à l’orange.");
                var rule = new BusinessWeatherRule
                {
                    Id = StableId("weather", kind + " " + label), Label = label,
                    Kind = kind switch { "categorie" => "category", "groupe" => "team", "type" => "type", _ => "metric" },
                    SourceKey = "active", Values = kind == "global" ? new() : values,
                    OrangeAbove = orange, RedAbove = red, WeatherEnabled = enabled is "oui" or "true" or "1",
                    IncludeDescendants = kind == "categorie", Visible = !sheet.HiddenRows.Contains(row), Note = sheet.At(row, column + 6).Trim()
                };
                rule.Validate(); profile.WeatherRules.Add(rule);
            }
        }
        var sitesHeader = sheet.Cells.FirstOrDefault(pair => Fold(pair.Value) is "site affiche" or "nom du site"
            && Fold(sheet.At(pair.Key.Row, pair.Key.Column + 1)).Contains("reconnues", StringComparison.Ordinal));
        if (!sitesHeader.Equals(default(KeyValuePair<(int Row, int Column), string>)))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var row = sitesHeader.Key.Row + 1; row <= sitesHeader.Key.Row + CitySettings.MaximumSites; row++)
            {
                var siteName = sheet.At(row, sitesHeader.Key.Column).Trim();
                var rawAliases = sheet.At(row, sitesHeader.Key.Column + 1).Trim();
                if (siteName.Length == 0 && rawAliases.Length == 0) break;
                if (!CityText.ValidText(siteName, false) || !names.Add(Fold(siteName))) throw new InvalidDataException($"Nom de site absent, invalide ou répété à la ligne {row}.");
                var aliases = rawAliases.Split(new[] { ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(value => Fold(value) != Fold(siteName)).GroupBy(Fold, StringComparer.Ordinal).Select(group => group.First()).ToList();
                if (!CityText.ValidTerms(aliases)) throw new InvalidDataException($"Les variantes du site de la ligne {row} dépassent les limites autorisées.");
                profile.Sites.Add(new() { Id = StableId("site", siteName), Name = siteName, Aliases = aliases });
            }
        }
        if (profile.WeatherRules.Count == 0 && profile.Sites.Count == 0) return null;
        var analysis = sheet.Cells.FirstOrDefault(pair => Fold(pair.Value).StartsWith("analyse et actions", StringComparison.Ordinal));
        if (!analysis.Equals(default(KeyValuePair<(int Row, int Column), string>)))
            profile.Notes = sheet.At(analysis.Key.Row + 1, analysis.Key.Column + 1).Trim();
        if (profile.Notes.Length > 4000) throw new InvalidDataException("La note d’analyse dépasse 4 000 caractères.");
        // The audited report stores its freshness setting in B6, alongside its
        // import metadata. The signature prevents interpreting an unrelated cell.
        if (sheet.Cells.Values.Any(value => Fold(value).StartsWith("parametres meteo", StringComparison.Ordinal))
            && int.TryParse(sheet.At(6, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) && hours is >= 1 and <= 8760)
            profile.FreshnessWarningHours = hours;
        return profile;
    }

    private static double? Threshold(string text, int row)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)
            || value < 0 || value > 1000000000 || Math.Truncate(value) != value)
            throw new InvalidDataException($"Le seuil météo de la ligne {row} n’est pas un entier positif ou nul enregistré.");
        return value;
    }
    private sealed record SheetCells(Dictionary<(int Row, int Column), string> Cells, HashSet<int> HiddenRows)
    { public string At(int row, int column) => Cells.GetValueOrDefault((row, column), ""); }

    private sealed class WorkbookReader : IDisposable
    {
        private readonly FileStream _file;
        private readonly ZipArchive _archive;
        private readonly CancellationToken _ct;
        private readonly List<string> _strings = new();
        public Dictionary<string, string> Sheets { get; } = new(StringComparer.Ordinal);
        public WorkbookReader(string path, CancellationToken ct)
        {
            _ct = ct; _file = File.OpenRead(path);
            try
            {
                if (_file.Length > MaximumBytes) throw new InvalidDataException("Le classeur dépasse la limite autorisée.");
                _archive = new(_file, ZipArchiveMode.Read, leaveOpen: true);
                if (_archive.Entries.Count > 20000 || _archive.Entries.Sum(entry => entry.Length) > MaximumBytes) throw new InvalidDataException("Le contenu décompressé du classeur dépasse la limite autorisée.");
                var workbook = Load("xl/workbook.xml"); var relations = Load("xl/_rels/workbook.xml.rels");
                foreach (var sheet in workbook.Descendants(Main + "sheet").Where(sheet => (string?)sheet.Attribute("state") is null or "visible"))
                {
                    var relation = relations.Descendants(Relations + "Relationship").SingleOrDefault(item => (string?)item.Attribute("Id") == (string?)sheet.Attribute(DocumentRelations + "id"));
                    if (relation is null || (string?)relation.Attribute("TargetMode") == "External") continue;
                    var name = (string?)sheet.Attribute("name") ?? "";
                    if (name.Length == 0 || !Sheets.TryAdd(name, ResolvePart("xl/workbook.xml", (string?)relation.Attribute("Target") ?? "", "xl/worksheets/")))
                        throw new InvalidDataException("Les feuilles visibles doivent avoir des noms distincts.");
                }
                if (Sheets.Count == 0) throw new InvalidDataException("Le classeur ne contient aucune feuille visible.");
                if (_archive.GetEntry("xl/sharedStrings.xml") is { } entry)
                {
                    using var stream = entry.Open(); using var reader = CreateReader(stream);
                    while (!reader.EOF)
                    {
                        _ct.ThrowIfCancellationRequested();
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") { reader.Read(); continue; }
                        var value = (XElement)XNode.ReadFrom(reader);
                        _strings.Add(Decode(string.Concat(value.Descendants(Main + "t").Select(text => text.Value))));
                        if (_strings.Count > 1000000) throw new InvalidDataException("Le classeur contient trop de chaînes partagées.");
                    }
                }
            }
            catch { _archive?.Dispose(); _file.Dispose(); throw; }
        }
        public SheetCells ReadSheet(string name, int maximumRow, int maximumColumn)
        {
            using var stream = Entry(Sheets[name]).Open(); using var reader = CreateReader(stream);
            var cells = new Dictionary<(int Row, int Column), string>(); var hidden = new HashSet<int>();
            while (!reader.EOF)
            {
                _ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") { reader.Read(); continue; }
                var row = (XElement)XNode.ReadFrom(reader); var number = (int?)row.Attribute("r") ?? 0;
                if (number > maximumRow) break;
                if (number < 1) continue;
                if ((string?)row.Attribute("hidden") is "1" or "true") hidden.Add(number);
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var address = (string?)cell.Attribute("r") ?? ""; var column = 0;
                    foreach (var letter in address.TakeWhile(char.IsAsciiLetterUpper)) column = checked(column * 26 + letter - 'A' + 1);
                    if (column < 1 || column > maximumColumn) continue;
                    var type = (string?)cell.Attribute("t"); var raw = cell.Element(Main + "v")?.Value ?? "";
                    var text = type == "inlineStr" ? Decode(string.Concat(cell.Descendants(Main + "t").Select(value => value.Value)))
                        : type == "s" ? int.TryParse(raw, out var index) && index >= 0 && index < _strings.Count ? _strings[index] : throw new InvalidDataException("Chaîne partagée invalide.")
                        : Decode(raw);
                    if (text.Length > 1000000 || !cells.TryAdd((number, column), text)) throw new InvalidDataException("Cellule de paramétrage invalide ou répétée.");
                }
            }
            return new(cells, hidden);
        }
        public HashSet<string> VisibleWeatherLabels(IEnumerable<string> names)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names.Take(4))
            {
                var part = Sheets[name]; var relationPath = "xl/worksheets/_rels/" + Path.GetFileName(part) + ".rels";
                if (_archive.GetEntry(relationPath) is null) continue;
                foreach (var relation in Load(relationPath).Descendants(Relations + "Relationship").Where(value => ((string?)value.Attribute("Type"))?.EndsWith("/drawing", StringComparison.Ordinal) == true))
                {
                    _ct.ThrowIfCancellationRequested();
                    if ((string?)relation.Attribute("TargetMode") == "External") continue;
                    var drawing = Load(ResolvePart(part, (string?)relation.Attribute("Target") ?? "", "xl/drawings/"));
                    foreach (var shape in drawing.Descendants().Where(value => value.Name.LocalName == "sp"))
                    {
                        if (shape.Descendants().Any(value => value.Name.LocalName == "cNvPr" && (string?)value.Attribute("hidden") is "1" or "true")) continue;
                        foreach (var paragraph in shape.Descendants(Drawing + "p"))
                        {
                            var text = string.Concat(paragraph.Descendants(Drawing + "t").Select(value => value.Value));
                            if (text.Length <= 1000) result.Add(Fold(text));
                        }
                    }
                }
            }
            return result;
        }
        private ZipArchiveEntry Entry(string path) => _archive.GetEntry(path) ?? throw new InvalidDataException("Composant interne du classeur absent.");
        private XDocument Load(string path) { using var stream = Entry(path).Open(); using var reader = CreateReader(stream); return XDocument.Load(reader); }
        private static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
        private static string Decode(string text) => Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
        private static string ResolvePart(string owner, string target, string prefix)
        {
            if (target.Length == 0 || target.Contains('\\') || target.Contains(':') || target.Contains('?') || target.Contains('#')) throw new InvalidDataException("Relation de classeur non prise en charge.");
            var resolved = new Uri(new Uri("https://workbook.invalid/" + owner), target);
            var part = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
            if (resolved.Host != "workbook.invalid" || !part.StartsWith(prefix, StringComparison.Ordinal) || part.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("Une relation sort de son composant interne autorisé.");
            return part;
        }
        public void Dispose() { _archive.Dispose(); _file.Dispose(); }
    }
}
