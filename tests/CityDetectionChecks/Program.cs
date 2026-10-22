using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

var checks = new List<(string Name, Action Run)>();
void Test(string name, Action run) => checks.Add((name, run));
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
CityDefinition City(string name, params string[] aliases) => new() { Name = name, Aliases = aliases.ToList() };
CitySettings Settings(params CityDefinition[] cities) => new() { Cities = cities.ToList() };
ExtractionResult Source(params string?[] titles) => new()
{
    Headers = new() { "ID", "Titre", "Statut" },
    Rows = titles.Select((title, index) => new[] { (index + 1).ToString(), title!, "Nouveau" }).ToList(),
    Complete = true, ExpectedTotal = titles.Length, Pages = 3, Scope = "Toutes les pages",
    SourceUrl = "https://example.invalid/front/ticket.php?criteria=test", Title = "Liste synthétique",
    Status = "Complet", ExtractedAt = new DateTimeOffset(2026, 10, 21, 10, 30, 0, TimeSpan.FromHours(2)),
    Warnings = new() { "Avertissement synthétique conservé." }
};
CityEnrichmentResult Detect(ExtractionResult source, CitySettings settings, DashboardSettings? dashboard = null)
    => CityDetectionService.Enrich(source, settings, dashboard ?? new());
DashboardSnapshot Probes(CityEnrichmentResult report, params int[] indices)
{
    var snapshot = DashboardService.CalculateSubset(report.Data, new(), indices, "Sous-ensemble synthétique", report.Data.ExtractedAt);
    CityDetectionService.AppendDashboardProbes(snapshot, report);
    return snapshot;
}

Test("MA, Mars and Marseille map to one canonical Marseille", () =>
{
    var city = City("Marseille", "MA", "Mars", "Marseille");
    var result = Detect(Source("MA - imprimante", "Mars / connexion", "Marseille, incident"), Settings(city));
    Assert(result.MatchedCount == 3 && result.Matches.All(match => match.CityId == city.Id && match.CityName == "Marseille"));
    Assert(result.Data.Rows.All(row => row[^2] == "Marseille" && row[^1] == "Reconnue"));
});
Test("Short aliases are whole words, not internal substrings or prefixes", () =>
{
    var result = Detect(Source("ordinateurMAxx", "Marsupilami", "marseillais", "MA2", "2MA"), Settings(City("Marseille", "MA", "Mars")));
    Assert(result.MatchedCount == 0 && result.UnmatchedCount == 5);
});
Test("Aliases remain case-insensitive, including a short ordinary word", () =>
{
    var result = Detect(Source("ma", "Ma", "mArS"), Settings(City("Marseille", "MA", "Mars")));
    Assert(result.MatchedCount == 3);
});
Test("Accents, decomposed accents, spaces, hyphens and punctuation normalize", () =>
{
    var result = Detect(Source("L’HAŸ-LES-ROSES", "l hay les roses", "L\u2019HAY\u0308 / LES\tROSES", "[ÉVRY-Courcouronnes]"),
        Settings(City("L’Haÿ-les-Roses"), City("Évry Courcouronnes")));
    Assert(result.MatchedCount == 4 && result.Matches[0].CityName == "L’Haÿ-les-Roses" && result.Matches[3].CityName == "Évry Courcouronnes");
});
Test("Aliases may contain multiple words", () =>
{
    var result = Detect(Source("Intervention vieux port aujourd’hui"), Settings(City("Marseille", "Vieux-Port")));
    Assert(result.MatchedCount == 1 && result.Data.Rows[0][^2] == "Marseille");
});
Test("A repeated city, canonical name and aliases form one match", () =>
{
    var city = City("Marseille", "MA", "Mars", "MARSEILLE", "mà");
    var result = Detect(Source("MA Marseille Mars Marseille MA"), Settings(city));
    Assert(result.MatchedCount == 1 && result.Matches[0].CandidateIds.SequenceEqual(new[] { city.Id }));
});
Test("Two distinct cities are ambiguous and do not populate the inferred city", () =>
{
    var first = City("Marseille"); var second = City("Lyon");
    var result = Detect(Source("Marseille et Lyon"), Settings(first, second));
    var match = result.Matches.Single();
    Assert(match.State == CityMatchState.Ambiguous && match.CityId is null && match.CityName is null);
    Assert(match.CandidateIds.Order().SequenceEqual(new[] { first.Id, second.Id }.Order()) && result.Data.Rows[0][^2] == "");
});
Test("A shared alias is ambiguous even when two cities have different names", () =>
{
    var result = Detect(Source("Intervention CENTRE"), Settings(City("Lyon", "Centre"), City("Marseille", "Centre")));
    Assert(result.AmbiguousCount == 1 && result.Matches[0].CandidateIds.Count == 2);
});
Test("Identical canonical names with different identities remain ambiguous", () =>
{
    var result = Detect(Source("Saint-Martin"), Settings(City("Saint-Martin"), City("Saint Martin")));
    Assert(result.AmbiguousCount == 1);
});
Test("A composite city supersedes only its overlapping shorter city", () =>
{
    var result = Detect(Source("Saint-Malo imprimante", "Saint Malo puis Malo"), Settings(City("Malo"), City("Saint-Malo")));
    Assert(result.Matches[0].State == CityMatchState.Matched && result.Matches[0].CityName == "Saint-Malo");
    Assert(result.Matches[1].State == CityMatchState.Ambiguous && result.Matches[1].CandidateIds.Count == 2);
});
Test("The longest nested composite supersedes overlapping intermediate names", () =>
{
    var result = Detect(Source("SAINT JEAN DE LUZ"), Settings(City("Luz"), City("Jean de Luz"), City("Saint-Jean-de-Luz")));
    Assert(result.MatchedCount == 1 && result.Matches[0].CityName == "Saint-Jean-de-Luz");
});
Test("Equal-length overlapping terms remain ambiguous instead of using order", () =>
{
    var result = Detect(Source("Alpha Beta Gamma"), Settings(City("Alpha Beta"), City("Beta Gamma")));
    Assert(result.AmbiguousCount == 1);
});
Test("A longer occurrence cannot hide another city elsewhere in the title", () =>
{
    var result = Detect(Source("Luz et Saint-Jean-de-Luz"), Settings(City("Luz"), City("Saint-Jean-de-Luz")));
    Assert(result.AmbiguousCount == 1);
});
Test("No fuzzy abbreviation or spelling correction is inferred", () =>
{
    var result = Detect(Source("St Malo", "Marseile", "Sant Malo"), Settings(City("Saint-Malo"), City("Marseille")));
    Assert(result.UnmatchedCount == 3 && result.MatchedCount == 0);
});
Test("City order and alias order cannot change the result or candidates", () =>
{
    var first = City("Marseille", "MA", "Mars"); var second = City("Lyon", "LY");
    var source = Source("MA et LY", "Mars", "Inconnu");
    var before = Detect(source, Settings(first, second));
    var reversedFirst = first.Copy(); reversedFirst.Aliases.Reverse();
    var after = Detect(source, Settings(second, reversedFirst));
    Assert(before.Matches.Select(match => (match.CityId, match.State)).SequenceEqual(after.Matches.Select(match => (match.CityId, match.State))));
    Assert(before.Matches[0].CandidateIds.SequenceEqual(after.Matches[0].CandidateIds));
});
Test("The explicit city title column takes precedence over dashboard mapping", () =>
{
    var source = Source("Lyon"); source.Headers.Add("Objet local"); source.Rows[0] = new[] { "1", "Lyon", "Nouveau", "Marseille" };
    var settings = Settings(City("Lyon"), City("Marseille")); settings.TitleColumn = "Objet local";
    var result = Detect(source, settings, new() { ColumnMappings = new() { ["title"] = "Titre" } });
    Assert(result.Matches[0].CityName == "Marseille" && result.TitleColumnName == "Objet local");
});
Test("A missing explicit title column never falls back to another title", () =>
{
    var settings = Settings(City("Lyon")); settings.TitleColumn = "Disparue";
    var result = Detect(Source("Lyon"), settings);
    Assert(result.Applied && result.TitleUnavailableCount == 1 && result.MatchedCount == 0 && result.TitleColumnName == "");
});
Test("Dashboard exact title override is honored when city override is empty", () =>
{
    var source = Source("Lyon"); source.Headers[1] = "Champ personnalisé";
    var result = Detect(source, Settings(City("Lyon")), new() { ColumnMappings = new() { ["title"] = "Champ personnalisé" } });
    Assert(result.MatchedCount == 1 && result.TitleColumnName == "Champ personnalisé");
});
Test("A broken dashboard title override also does not fall back", () =>
{
    var result = Detect(Source("Lyon"), Settings(City("Lyon")), new() { ColumnMappings = new() { ["title"] = "Absente" } });
    Assert(result.TitleUnavailableCount == 1 && result.MatchedCount == 0);
});
Test("Qualified ticket title headers use the conservative dashboard resolver", () =>
{
    var source = Source("Lyon"); source.Headers[1] = "Ticket - Titre";
    Assert(Detect(source, Settings(City("Lyon"))).MatchedCount == 1);
    source.Headers[1] = "Demandeur - Titre";
    Assert(Detect(source, Settings(City("Lyon"))).TitleUnavailableCount == 1);
});
Test("Ambiguous title aliases produce no arbitrary city", () =>
{
    var source = Source("Lyon"); source.Headers.Add("Sujet"); source.Rows[0] = new[] { "1", "Lyon", "Nouveau", "Marseille" };
    var result = Detect(source, Settings(City("Lyon"), City("Marseille")));
    Assert(result.TitleUnavailableCount == 1 && result.Message.Contains("plusieurs colonnes", StringComparison.Ordinal));
});
Test("Duplicate exact title headers cannot be disambiguated by an override", () =>
{
    var source = Source("Lyon"); source.Headers.Add("Titre"); source.Rows[0] = new[] { "1", "Lyon", "Nouveau", "Marseille" };
    var settings = Settings(City("Lyon")); settings.TitleColumn = "Titre";
    Assert(Detect(source, settings).TitleUnavailableCount == 1);
});
Test("Explicit title headers preserve meaningful spaces and exact case", () =>
{
    var source = Source("Lyon"); source.Headers[1] = " Titre local ";
    var settings = Settings(City("Lyon")); settings.TitleColumn = " Titre local ";
    Assert(Detect(source, settings).MatchedCount == 1);
    settings.TitleColumn = "titre local";
    Assert(Detect(source, settings).TitleUnavailableCount == 1);
});
Test("Reordered source columns keep their actual data and title role", () =>
{
    var source = Source("Lyon"); source.Headers = new() { "Statut", "ID", "Titre" }; source.Rows[0] = new[] { "Nouveau", "1", "Lyon" };
    var result = Detect(source, Settings(City("Lyon")));
    Assert(result.MatchedCount == 1 && result.Data.Rows[0].Take(3).SequenceEqual(source.Rows[0]));
});
Test("Disabled detection is a zero-copy pass-through without fabricated states", () =>
{
    var source = Source("Lyon"); var settings = Settings(City("Lyon")); settings.Enabled = false;
    var result = Detect(source, settings);
    Assert(ReferenceEquals(result.Data, source) && ReferenceEquals(result.Source, source) && !result.Applied && result.Matches.Count == 0);
});
Test("An empty city list is a zero-copy pass-through", () =>
{
    var source = Source("Lyon"); var result = Detect(source, new());
    Assert(ReferenceEquals(result.Data, source) && !result.Applied && result.Matches.Count == 0 && result.CityColumnName == "");
});
Test("An empty source with known title still gets the two inferred columns", () =>
{
    var result = Detect(Source(), Settings(City("Lyon")));
    Assert(result.Applied && result.Data.Headers.Count == 5 && result.Data.Rows.Count == 0 && result.Matches.Count == 0);
});
Test("Absent title column adds only empty cities and explicit unavailable states", () =>
{
    var source = Source("Lyon"); source.Headers[1] = "Description";
    var result = Detect(source, Settings(City("Lyon")));
    Assert(result.Applied && result.Data.Rows[0][^2] == "" && result.Data.Rows[0][^1] == "Titre indisponible");
});
Test("Blank titles and nonmatching titles are distinct states", () =>
{
    var result = Detect(Source(null, "", " \t\r\n ", "...", "Aucun lieu"), Settings(City("Lyon")));
    Assert(result.TitleUnavailableCount == 3 && result.UnmatchedCount == 2);
    Assert(result.MatchedCount + result.UnmatchedCount + result.AmbiguousCount + result.TitleUnavailableCount == 5);
});
Test("Short, empty and null rows are padded without changing their indices", () =>
{
    var source = Source("Lyon", "Lyon", "Lyon", "Lyon");
    source.Rows[0] = Array.Empty<string>(); source.Rows[1] = new[] { "2" }; source.Rows[2] = null!;
    var result = Detect(source, Settings(City("Lyon")));
    Assert(result.Data.Rows.All(row => row.Length == result.Data.Headers.Count));
    Assert(result.Matches.Select(match => match.RowIndex).SequenceEqual(new[] { 0, 1, 2, 3 }) && result.Matches[3].CityName == "Lyon");
});
Test("Cells beyond the source headers are never silently discarded", () =>
{
    var source = Source("Lyon"); source.Rows[0] = new[] { "1", "Lyon", "Nouveau", "extra" };
    var result = Detect(source, Settings(City("Lyon")));
    Assert(!result.Applied && ReferenceEquals(result.Data, source) && result.Data.Rows[0][3] == "extra");
});
Test("Output metadata is copied completely while collections remain independent", () =>
{
    var source = Source("Lyon", "Ailleurs"); source.Complete = false; source.ExpectedTotal = 27;
    var result = Detect(source, Settings(City("Lyon")));
    Assert(result.Source == source && result.Data != source && result.Data.Headers != source.Headers && result.Data.Rows != source.Rows);
    Assert(result.Data.SourceUrl == source.SourceUrl && result.Data.Title == source.Title && result.Data.ExtractedAt == source.ExtractedAt);
    Assert(result.Data.Pages == 3 && result.Data.ExpectedTotal == 27 && result.Data.Scope == source.Scope && !result.Data.Complete && result.Data.Status == source.Status);
    Assert(result.Data.Warnings.SequenceEqual(source.Warnings) && result.Data.Warnings != source.Warnings);
    result.Data.Rows[0][1] = "Modifié"; result.Data.Warnings.Add("Autre"); result.Data.Headers[0] = "Autre";
    Assert(source.Rows[0][1] == "Lyon" && source.Headers[0] == "ID" && source.Warnings.Count == 1);
});
Test("Original text, including controls and leading spaces, is not normalized in data", () =>
{
    var title = "  LYON\u0085 [test]\r\n "; var source = Source(title);
    var result = Detect(source, Settings(City("Lyon")));
    Assert(result.Data.Rows[0][1] == title && source.Rows[0][1] == title && result.MatchedCount == 1);
});
Test("A malformed Unicode title cannot crash inference or erase original data", () =>
{
    var title = "Lyon\ud800 suite"; var result = Detect(Source(title), Settings(City("Lyon")));
    Assert(result.MatchedCount == 1 && result.Data.Rows[0][1] == title);
});
Test("Added headers are unique even against case and accent variants", () =>
{
    var source = Source("Lyon"); source.Headers.AddRange(new[] { "VILLE DETECTEE", "Ville détectée (2)", "REPÉRAGE VILLE" });
    source.Rows[0] = new[] { "1", "Lyon", "Nouveau", "native-city", "native-other", "native-state" };
    var result = Detect(source, Settings(City("Lyon")));
    Assert(result.CityColumnName == "Ville détectée (3)" && result.StateColumnName == "Repérage ville (2)");
    Assert(result.Data.Rows[0][3] == "native-city" && result.Data.Rows[0][^2] == "Lyon" && source.Headers.Count == 6);
});
Test("Repeated enrichment from the original source never accumulates columns", () =>
{
    var source = Source("Lyon"); var settings = Settings(City("Lyon"));
    var first = Detect(source, settings); var second = Detect(first.Source, settings);
    Assert(first.Data.Headers.SequenceEqual(second.Data.Headers) && second.Data.Headers.Count == 5 && source.Headers.Count == 3);
});
Test("Matching beyond ten thousand rows still preserves original indices", () =>
{
    var source = Source(Enumerable.Range(0, 12007).Select(index => index == 12000 ? "Lyon" : "Ailleurs").ToArray());
    var result = Detect(source, Settings(City("Lyon")));
    Assert(result.MatchedCount == 1 && result.Matches.Single(match => match.State == CityMatchState.Matched).RowIndex == 12000);
});
Test("Empty, whitespace and punctuation-only city names fail strict validation", () =>
{
    foreach (var name in new[] { "", "  ", "---" }) Throws<InvalidDataException>(() => Settings(City(name)).Validate());
});
Test("Invalid or duplicate identities fail validation without repairing input", () =>
{
    var city = City("Lyon"); city.Id = "not-a-guid";
    Throws<InvalidDataException>(() => Settings(city).Validate()); Assert(city.Id == "not-a-guid");
    city.Id = Guid.Empty.ToString("N"); Throws<InvalidDataException>(() => Settings(city).Validate());
    var first = City("Lyon"); var second = City("Marseille"); second.Id = Guid.Parse(first.Id).ToString("D").ToUpperInvariant();
    Throws<InvalidDataException>(() => Settings(first, second).Validate());
});
Test("All text limits are checked before normalization", () =>
{
    var longText = new string('x', 121);
    foreach (var modify in new Action<CitySettings>[]
    {
        settings => settings.TitleColumn = longText,
        settings => settings.Cities[0].Name = longText,
        settings => settings.Cities[0].Aliases.Add(longText),
        settings => settings.Cities[0].PostalCode = longText
    })
    { var settings = Settings(City("Lyon")); modify(settings); Throws<InvalidDataException>(settings.Validate); }
});
Test("More than twelve aliases or one thousand cities fail validation", () =>
{
    var settings = Settings(City("Lyon", Enumerable.Range(0, 13).Select(index => "Alias " + index).ToArray()));
    Throws<InvalidDataException>(settings.Validate);
    settings = Settings(Enumerable.Range(0, 1001).Select(index => City("Ville " + index)).ToArray());
    Throws<InvalidDataException>(settings.Validate);
});
Test("Invalid null collections and control characters fail strict validation", () =>
{
    var settings = Settings(City("Lyon")); settings.Cities[0].Aliases = null!; Throws<InvalidDataException>(settings.Validate);
    settings = Settings(City("Lyon", "")); Throws<InvalidDataException>(settings.Validate);
    settings = Settings(City("Ly\u0000on")); Throws<InvalidDataException>(settings.Validate);
    settings = Settings(City("Lyon")); settings.Cities = null!; Throws<InvalidDataException>(settings.Validate);
    settings = Settings(new CityDefinition[] { null! }); Throws<InvalidDataException>(settings.Validate);
    settings = Settings(City("Lyon\ud800")); Throws<InvalidDataException>(settings.Validate);
});
Test("Coordinates accept legal endpoints and an entirely absent pair", () =>
{
    foreach (var (latitude, longitude) in new (double?, double?)[] { (null, null), (-90, -180), (90, 180), (0, 0), (45.764, 4.8357) })
    { var city = City("Lyon"); city.Latitude = latitude; city.Longitude = longitude; Settings(city).Validate(); }
});
Test("Coordinates reject partial, out-of-range and nonfinite pairs", () =>
{
    foreach (var (latitude, longitude) in new (double?, double?)[]
        { (1, null), (null, 1), (-91, 0), (91, 0), (0, -181), (0, 181), (double.NaN, 0), (0, double.PositiveInfinity), (double.NegativeInfinity, 0) })
    { var city = City("Lyon"); city.Latitude = latitude; city.Longitude = longitude; Throws<InvalidDataException>(() => Settings(city).Validate()); }
});
Test("Postal code is optional portable text without country guessing", () =>
{
    var city = City("Lyon"); city.PostalCode = "06900"; var settings = Settings(city); settings.Validate();
    Assert(settings.Copy().Cities[0].PostalCode == "06900");
    city.PostalCode = "A1A 1A1"; settings.Validate();
});
Test("Normalization repairs corrupt local values without throwing at startup", () =>
{
    var duplicate = City(" Lyon ", " LY ", "ly", "", "---", null!); duplicate.Id = "bad"; duplicate.Latitude = double.NaN;
    var settings = Settings(duplicate, City("---"), null!); settings.TitleColumn = null!;
    settings.Normalize(); settings.Validate();
    Assert(settings.Cities.Count == 1 && settings.Cities[0].Name == "Lyon" && settings.TitleColumn == "");
    Assert(settings.Cities[0].Aliases.SequenceEqual(new[] { "LY" }) && !settings.Cities[0].Latitude.HasValue && !settings.Cities[0].Longitude.HasValue);
});
Test("Normalization is bounded and preserves valid stable identities", () =>
{
    var first = City(new string('a', 150), Enumerable.Range(0, 20).Select(index => "Alias " + index).ToArray());
    var id = first.Id;
    var settings = Settings(new[] { first }.Concat(Enumerable.Range(0, 1100).Select(index => City("Ville " + index))).ToArray());
    settings.Normalize(); settings.Validate();
    Assert(settings.Cities.Count == 1000 && settings.Cities[0].Aliases.Count == 12 && settings.Cities[0].Name.Length == 120 && settings.Cities[0].Id == id);
    settings.Normalize(); Assert(settings.Cities[0].Id == id);
});
Test("Copy is deep and normalized without mutating original settings", () =>
{
    var city = City(" Lyon ", "LY"); city.Latitude = 4; var settings = Settings(city);
    var copy = settings.Copy();
    Assert(settings.Cities[0].Name == " Lyon " && settings.Cities[0].Latitude == 4 && copy.Cities[0].Name == "Lyon" && copy.Cities[0].Latitude is null);
    copy.Cities[0].Aliases.Add("Autre"); copy.Cities[0].Name = "Autre";
    Assert(settings.Cities[0].Aliases.Count == 1 && settings.Cities[0].Name == " Lyon " && copy.Cities[0].Id == city.Id);
});
Test("Repeated references in a local draft normalize to distinct stable identities", () =>
{
    var city = City("Lyon"); var settings = Settings(city, city);
    settings.Normalize(); settings.Validate();
    Assert(!ReferenceEquals(settings.Cities[0], settings.Cities[1]) && settings.Cities[0].Id != settings.Cities[1].Id);
    var ids = settings.Cities.Select(item => item.Id).ToArray(); settings.Normalize();
    Assert(settings.Cities.Select(item => item.Id).SequenceEqual(ids));
});
Test("Portable JSON round trip retains IDs, aliases, postal code and coordinates", () =>
{
    var city = City("Marseille", "MA", "Vieux Port"); city.PostalCode = "13000"; city.Latitude = 43.2965; city.Longitude = 5.3698;
    var settings = Settings(city); settings.TitleColumn = "Titre";
    var restored = JsonSerializer.Deserialize<CitySettings>(JsonSerializer.Serialize(settings))!; restored.Validate();
    Assert(restored.Cities[0].Id == city.Id && restored.Cities[0].Aliases.SequenceEqual(city.Aliases));
    Assert(restored.Cities[0].PostalCode == "13000" && restored.Cities[0].Latitude == city.Latitude && restored.Cities[0].Longitude == city.Longitude);
});
Test("Dashboard probes partition exactly a scoped subset with original indices", () =>
{
    var report = Detect(Source("Lyon", "Marseille", "Lyon Marseille", "Autre", ""), Settings(City("Lyon"), City("Marseille")));
    var snapshot = Probes(report, 4, 2, 0);
    Assert(snapshot.Metric("city-matched")!.Rows.SequenceEqual(new[] { 0 }));
    Assert(snapshot.Metric("city-ambiguous")!.Rows.SequenceEqual(new[] { 2 }));
    Assert(snapshot.Metric("city-no-title")!.Rows.SequenceEqual(new[] { 4 }));
    Assert(snapshot.Metric("city-unmatched")!.Value == 0 && snapshot.Metric("city-coverage")!.Sample == 3);
    Assert(Math.Abs(snapshot.Metric("city-coverage")!.Value!.Value - 100d / 3) < 0.000001);
    var distribution = snapshot.Distributions.Single(item => item.Key == "city-detection");
    Assert(distribution.Available && distribution.Denominator == 3 && distribution.Buckets.SelectMany(bucket => bucket.Rows).Order().SequenceEqual(new[] { 0, 2, 4 }));
});
Test("Empty scoped dashboards keep zero counts and undefined coverage", () =>
{
    var report = Detect(Source("Lyon"), Settings(City("Lyon")));
    var snapshot = Probes(report);
    Assert(snapshot.Metric("city-matched")!.Value == 0 && snapshot.Metric("city-unmatched")!.Value == 0 && snapshot.Metric("city-ambiguous")!.Value == 0 && snapshot.Metric("city-no-title")!.Value == 0);
    Assert(snapshot.Metric("city-coverage")!.Value is null && snapshot.Metric("city-coverage")!.Sample == 0);
});
Test("A missing title column gives an honest 0 percent and all no-title rows", () =>
{
    var source = Source("Lyon", "Lyon"); source.Headers[1] = "Description";
    var report = Detect(source, Settings(City("Lyon"))); var snapshot = Probes(report, 0, 1);
    Assert(snapshot.Metric("city-coverage")!.Value == 0 && snapshot.Metric("city-no-title")!.Value == 2 && snapshot.Metric("city-matched")!.Value == 0);
});
Test("Disabled or unconfigured city probes are unavailable rather than zero", () =>
{
    var source = Source("Lyon"); var snapshot = Probes(Detect(source, new()), 0);
    Assert(snapshot.Metrics.Where(metric => metric.Key.StartsWith("city-", StringComparison.Ordinal)).All(metric => metric.Value is null && metric.Rows.Length == 0));
    Assert(!snapshot.Distributions.Single(item => item.Key == "city-detection").Available);
});
Test("Appending probes twice is idempotent and keeps unrelated dashboard data", () =>
{
    var report = Detect(Source("Lyon"), Settings(City("Lyon"))); var snapshot = Probes(report, 0);
    var total = snapshot.Metric("total"); var metricCount = snapshot.Metrics.Count;
    CityDetectionService.AppendDashboardProbes(snapshot, report);
    Assert(snapshot.Metrics.Count == metricCount && snapshot.Metric("total") == total && snapshot.Distributions.Count(item => item.Key == "city-detection") == 1);
});
Test("Inference does not change incomplete-source truth or the export scope", () =>
{
    var source = Source("Lyon", "Autre"); source.ExpectedTotal = 50; source.Complete = false;
    var report = Detect(source, Settings(City("Lyon"))); var snapshot = Probes(report, 0);
    Assert(!snapshot.Complete && snapshot.ExpectedTotal is null && snapshot.SourceExpectedTotal == 50 && snapshot.Metric("city-coverage")!.Value == 100);
    var exported = DashboardService.ToExport(snapshot);
    Assert(exported.Rows.Any(row => row.Contains("Couverture des villes") && row.Contains("100")));
});
Test("An inconsistent externally supplied city report cannot claim coverage", () =>
{
    var source = Source("Lyon"); var report = new CityEnrichmentResult { Source = source, Data = source, Applied = true };
    Assert(Probes(report, 0).Metric("city-matched")!.Value is null);
});
Test("Cancelled inference and disabled pass-through both honor cancellation", () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    foreach (var settings in new[] { new CitySettings(), Settings(City("Lyon")) })
        Throws<OperationCanceledException>(() => CityDetectionService.Enrich(Source("Lyon"), settings, new(), cancellation.Token));
});
Test("Cancellation while consuming a city report does not publish partial probes", () =>
{
    using var cancellation = new CancellationTokenSource();
    var source = Source("Lyon"); var snapshot = DashboardService.Calculate(source, new(), source.ExtractedAt);
    var matches = new CancelOnAccessMatches(new CityMatch(0, "id", "Lyon", CityMatchState.Matched, new[] { "id" }), cancellation);
    var report = new CityEnrichmentResult { Source = source, Data = source, Applied = true, Matches = matches };
    Throws<OperationCanceledException>(() => CityDetectionService.AppendDashboardProbes(snapshot, report, cancellation.Token));
    Assert(snapshot.Metric("city-matched") is null);
});
Test("Previously cancelled dashboard probe calculation leaves snapshot unchanged", () =>
{
    var report = Detect(Source("Lyon"), Settings(City("Lyon"))); var snapshot = DashboardService.Calculate(report.Data, new(), report.Data.ExtractedAt);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => CityDetectionService.AppendDashboardProbes(snapshot, report, cancellation.Token));
    Assert(snapshot.Metric("city-matched") is null);
});


Test("Contextual abbreviation requires every word and does not fall back to the simple alias", () =>
{
    var city = City("Marseille", "MA"); city.Rules.Add(new() { Term = "MA", RequiredTerms = new() { "Agence", "Sud" }, ExcludedTerms = new() { "formation" } });
    var settings = Settings(city); var report = Detect(Source("MA agence Sud panne", "MA ordinateur", "MA agence sud formation"), settings);
    Assert(report.Matches.Select(match => match.State).SequenceEqual(new[] { CityMatchState.Matched, CityMatchState.Unmatched, CityMatchState.Unmatched }));
    Assert(report.Matches[0].Confidence == "contexte" && report.Matches[1].Evidence.Single().Decision == "Écartée");
});
Test("A missing or ambiguous contextual column never confirms an abbreviation", () =>
{
    var city = City("Marseille", "MA"); city.Rules.Add(new() { Term = "MA", ContextColumn = "Entité", RequiredTerms = new() { "Provence" } });
    var settings = Settings(city); Assert(Detect(Source("MA"), settings).Matches[0].State == CityMatchState.Ambiguous);
    var source = Source("MA", "MA"); source.Headers.Add("Entité"); source.Rows[0] = new[] { "1", "MA", "Nouveau", "PROVENCE" }; source.Rows[1] = new[] { "2", "MA", "Nouveau", "Paris" };
    var report = Detect(source, settings); Assert(report.Matches[0].CityId == city.Id && report.Matches[1].State == CityMatchState.Unmatched);
});
Test("Explicit priorities resolve competing city rules while ties stay ambiguous", () =>
{
    var a = City("Alpha"); var b = City("Beta"); a.Rules.Add(new() { Term = "Centre", Priority = 10 }); b.Rules.Add(new() { Term = "Centre", Priority = 20 });
    var report = Detect(Source("Centre"), Settings(a, b)); Assert(report.Matches[0].CityId == b.Id);
    b.Rules[0].Priority = 10; Assert(Detect(Source("Centre"), Settings(a, b)).AmbiguousCount == 1);
    a.ExcludedTerms.Add("exercice"); b.ExcludedTerms.Add("exercice"); Assert(Detect(Source("Centre exercice"), Settings(a, b)).UnmatchedCount == 1);
});
Test("A refused long contextual match cannot suppress a shorter approved city", () =>
{
    var a = City("Malo"); var b = City("Saint Malo"); b.Rules.Add(new() { Term = "Saint Malo", RequiredTerms = new() { "Agence" } });
    Assert(Detect(Source("Saint Malo"), Settings(a, b)).Matches[0].CityId == a.Id);
});
Test("A unique site infers its city and conflicts with a different named city", () =>
{
    var a = City("Lyon"); var b = City("Marseille"); var settings = Settings(a, b);
    settings.Sites.Add(new() { CityId = a.Id, Name = "Agence Centrale", Aliases = new() { "LYC" }, Latitude = 45.7, Longitude = 4.8 });
    var report = Detect(Source("LYC imprimante", "LYC Marseille"), settings);
    Assert(report.Matches[0].CityId == a.Id && report.Matches[0].SiteId == settings.Sites[0].Id && report.Matches[0].Confidence == "site");
    Assert(report.Matches[1].State == CityMatchState.Ambiguous && report.Matches[1].SiteId == null);
});
Test("Two sites in one city retain the known city but leave the precise site ambiguous", () =>
{
    var city = City("Lyon"); var settings = Settings(city); settings.Sites.Add(new() { CityId = city.Id, Name = "Bâtiment A" }); settings.Sites.Add(new() { CityId = city.Id, Name = "Bâtiment B" });
    var match = Detect(Source("Bâtiment A et bâtiment B"), settings).Matches[0];
    Assert(match.CityId == city.Id && match.SiteId == null && match.SiteState == "ambiguous");
});
Test("Hierarchy columns are explicit and preserve every original cell", () =>
{
    var city = City("Lyon"); city.Region = "Auvergne Rhône Alpes"; city.Department = "69"; city.Sector = "Est";
    var source = Source("Lyon", "Paris"); var report = Detect(source, Settings(city));
    Assert(report.Data.Rows[0][report.Data.Headers.IndexOf(report.RegionColumnName)] == city.Region && report.Data.Rows[1][report.Data.Headers.IndexOf(report.SectorColumnName)] == "");
    Assert(report.Data.Rows[0].Take(3).SequenceEqual(source.Rows[0]));
    var snapshot = Probes(report, 0, 1); Assert(snapshot.Distributions.Single(item => item.Key == "sector-reference").Buckets.Sum(bucket => bucket.Count) == 2);
});
Test("Simulation and production use identical contexts and explain the retained rule", () =>
{
    var city = City("Marseille"); city.Rules.Add(new() { Term = "MA", ContextColumn = "Entité", RequiredTerms = new() { "PACA" } }); var settings = Settings(city);
    var match = CityDetectionService.ExplainTitle("MA panne", settings, new Dictionary<string, string> { ["Entité"] = "PACA" });
    Assert(match.CityId == city.Id && match.Evidence.Single().RuleId == city.Rules[0].Id);
    Assert(CityDetectionService.InspectDictionary(settings).Any(warning => warning.Contains("court")));
});
Test("Manual corrections are installation scoped and keyed by unique ticket ID", () =>
{
    var a = City("Lyon"); var b = City("Marseille"); var settings = Settings(a, b); var source = Source("Lyon", "Lyon");
    var correction = new CityTicketOverride { SourceKey = CityOverrideStore.SourceKey(source.SourceUrl), TicketId = "1", CityId = b.Id };
    var report = CityDetectionService.Enrich(source, settings, new(), overrides: new[] { correction });
    Assert(report.Matches[0].CityId == b.Id && report.Matches[0].IsOverride && report.Matches[1].CityId == a.Id);
    source.Rows[1][0] = "1"; Assert(CityDetectionService.Enrich(source, settings, new(), overrides: new[] { correction }).Matches.All(match => !match.IsOverride));
    source.SourceUrl = "https://other.invalid/front/ticket.php"; Assert(CityDetectionService.Enrich(source, settings, new(), overrides: new[] { correction }).Matches.All(match => !match.IsOverride));
});
Test("A correction referencing a removed place cannot silently fall back to inference", () =>
{
    var city = City("Lyon"); var source = Source("Lyon"); var correction = new CityTicketOverride { SourceKey = CityOverrideStore.SourceKey(source.SourceUrl), TicketId = "1", CityId = Guid.NewGuid().ToString("N") };
    var match = CityDetectionService.Enrich(source, Settings(city), new(), overrides: new[] { correction }).Matches[0];
    Assert(match.IsOverride && match.CityId == null && match.State == CityMatchState.Ambiguous);
});
Test("Correction files contain no URL credentials or search arguments and persist atomically", () =>
{
    var source = CityOverrideStore.SourceKey("https://user:secret@example.invalid/glpi/front/ticket.php?token=secret#fragment"); Assert(source == "https://example.invalid/glpi");
    var path = Path.Combine(Path.GetTempPath(), "glpi-city-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var store = new CityOverrideStore().With(new() { CityId = Guid.NewGuid().ToString("N") }, source, "23"); store.Save(path);
        var read = CityOverrideStore.Load(path); Assert(read.Overrides.Single().TicketId == "23"); Assert(read.With(null, source, "23").Overrides.Count == 0);
        read.Overrides[0].SourceKey = null!; Throws<InvalidDataException>(read.Validate);
    }
    finally { File.Delete(path); }
});
Test("Reference CSV merges preserve identities, absent fields, rules and unrelated cities", () =>
{
    var a = City("Lyon", "LY"); a.Rules.Add(new() { Term = "LY", RequiredTerms = new() { "Agence" } }); a.Region = "Est";
    var b = City("Marseille"); var settings = Settings(a, b);
    var csv = new ExtractionResult { Headers = new() { "Nom", "Alias" }, Rows = new() { new[] { "Lyon", "LY ; LYO" }, new[] { "Paris", "PA" } } };
    var preview = CityReferenceService.Preview(csv, settings); var changed = preview.Settings.Cities.Single(city => city.Id == a.Id);
    Assert(preview.Settings.Cities.Count == 3 && changed.Aliases.Count == 2 && changed.Region == "Est" && changed.Rules.Count == 1);
    Assert(settings.Cities.Count == 2 && a.Aliases.Count == 1 && preview.Changes.Count(item => item.Kind == "Ajout") == 1);
});
Test("Reference CSV round trip retains sites, coordinates, rules and hierarchy", () =>
{
    var city = City("Lyon"); city.Sector = "Nord"; city.Rules.Add(new() { Term = "LY", ContextColumn = "Entité", RequiredTerms = new() { "France" } });
    var settings = Settings(city); settings.Sites.Add(new() { CityId = city.Id, Name = "Agence", Address = "1 rue locale", Latitude = 45, Longitude = -4 });
    var imported = CityReferenceService.Preview(CityReferenceService.Export(settings), new()).Settings;
    Assert(imported.Cities.Single().Id == city.Id && imported.Cities[0].Rules[0].ContextColumn == "Entité" && imported.Sites.Single().Latitude == 45);
    Assert(CityReferenceService.Preview(CityReferenceService.Export(settings), settings).Changes.All(change => change.Kind == "Identique"));
    var protectedCsv = CityReferenceService.Export(settings); protectedCsv.Rows[1][protectedCsv.Headers.IndexOf("Longitude")] = "'-4";
    Assert(CityReferenceService.Preview(protectedCsv, settings).Settings.Sites.Single().Longitude == -4);
});
Test("Invalid references or malformed context definitions fail before mutation", () =>
{
    var city = City("Lyon"); var settings = Settings(city); var csv = new ExtractionResult { Headers = new() { "Type", "VilleId", "Nom" }, Rows = new() { new[] { "Site", "Absente", "Agence" } } };
    Throws<InvalidDataException>(() => CityReferenceService.Preview(csv, settings)); Assert(settings.Sites.Count == 0);
    city.Rules.Add(new() { Term = "LY", Priority = -1 }); Throws<InvalidDataException>(settings.Validate);
    settings.Normalize(); Assert(settings.Cities.Count == 0);
});
Test("Advanced city copies keep independent rules and site aliases", () =>
{
    var city = City("Lyon"); city.Rules.Add(new() { Term = "LY", RequiredTerms = new() { "Agence" } }); var settings = Settings(city); settings.Sites.Add(new() { CityId = city.Id, Name = "Centre", Aliases = new() { "CTR" } });
    var copy = settings.Copy(); copy.Cities[0].Rules[0].RequiredTerms[0] = "Autre"; copy.Sites[0].Aliases[0] = "AUT";
    Assert(city.Rules[0].RequiredTerms[0] == "Agence" && settings.Sites[0].Aliases[0] == "CTR");
});

CityTerrainChecks.Register(Test);
var passed = 0;
foreach (var (name, run) in checks)
    try { run(); passed++; Console.WriteLine("PASS  " + name); }
    catch (Exception exception) { Console.WriteLine("FAIL  " + name + " · " + exception); }
Console.WriteLine($"{passed}/{checks.Count} vérifications réussies.");
return passed == checks.Count ? 0 : 1;

sealed class CancelOnAccessMatches(CityMatch match, CancellationTokenSource cancellation) : IReadOnlyList<CityMatch>
{
    public int Count => 1;
    public CityMatch this[int index] { get { cancellation.Cancel(); return match; } }
    public IEnumerator<CityMatch> GetEnumerator() { yield return this[0]; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
