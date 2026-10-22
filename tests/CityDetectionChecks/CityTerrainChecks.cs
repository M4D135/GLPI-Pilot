using System.Text.Json;
using GlpiPilot.Core;

internal static class CityTerrainChecks
{
    private static void Check(bool value, string reason = "Assertion failed") { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static ExtractionResult Source(params string[] titles) => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Localisation" }, Rows = titles.Select((title, index) => new[] { (index + 1).ToString(), title, "Nouveau", "" }).ToList(),
        SourceUrl = "https://terrain.example.invalid/glpi/front/ticket.php", Complete = true
    };
    private static CitySettings Settings() => new() { Cities = new() { new() { Name = "Paris" }, new() { Name = "Lyon" } } };
    private static CityEnrichmentResult Detect(ExtractionResult source, CitySettings settings, IReadOnlyList<CityTicketOverride>? changes = null)
        => CityDetectionService.Enrich(source, settings, new(), overrides: changes);

    public static void Register(Action<string, Action> test)
    {
        test("Terrain hierarchy includes named buildings and preserves source cells", () =>
        {
            var settings = Settings(); settings.Cities[0].TerritoryPath = "Europe / France / Île-de-France / Nord";
            var site = new SiteDefinition { CityId = settings.Cities[0].Id, Name = "Agence centrale" }; settings.Sites.Add(site);
            var building = new BuildingDefinition { SiteId = site.Id, Name = "Pavillon Alpha", Aliases = new() { "PA1" }, Floor = "2" }; settings.Buildings.Add(building);
            var source = Source("PA1 imprimante"); var result = Detect(source, settings); var match = result.Matches.Single();
            Check(match.CityId == settings.Cities[0].Id && match.SiteId == site.Id && match.BuildingId == building.Id);
            Check(result.Data.Rows[0].Take(source.Headers.Count).SequenceEqual(source.Rows[0]) && result.Data.Headers.Contains(result.BuildingColumnName));
            Check(result.Data.Rows[0][result.Data.Headers.IndexOf(result.TerritoryColumnName)] == settings.Cities[0].TerritoryPath);
        });
        test("Terrain reference CSV round trips buildings hierarchy calendars contacts and regex", () =>
        {
            var settings = Settings(); settings.Cities[0].TerritoryPath = "France / Nord";
            settings.Cities[0].Rules.Add(new() { Term = "PAR", Position = "token", TokenPosition = 2, TitlePattern = "^IT PAR [0-9]+$" });
            var site = new SiteDefinition { CityId = settings.Cities[0].Id, Name = "Agence", Contact = "Accueil : 0102030405", Equipment = "Badge", Openings = new() { new() { Day = DayOfWeek.Monday, From = "09:00", Until = "12:00" } }, ClosedDates = new() { "2026-10-26" } }; settings.Sites.Add(site);
            settings.Buildings.Add(new() { SiteId = site.Id, Name = "Bâtiment A", Floor = "RDC", Aliases = new() { "BAT-A" } });
            var read = CityReferenceService.Preview(CityReferenceService.Export(settings), new()).Settings;
            Check(read.Buildings.Single().Floor == "RDC" && read.Cities[0].TerritoryPath == "France / Nord" && read.Cities[0].Rules.Single().TokenPosition == 2);
            Check(read.Sites.Single().Openings.Single().From == "09:00" && read.Sites[0].ClosedDates.Single() == "2026-10-26" && read.Sites[0].Contact == site.Contact);
            Check(CityReferenceService.Preview(CityReferenceService.Export(settings), settings).Changes.All(value => value.Kind == "Identique"));
        });
        test("Terrain settings deep copy building and opening collections", () =>
        {
            var settings = Settings(); var site = new SiteDefinition { CityId = settings.Cities[0].Id, Name = "Agence", Openings = new() { new() { Day = DayOfWeek.Monday } } }; settings.Sites.Add(site);
            settings.Buildings.Add(new() { SiteId = site.Id, Name = "Tour", Aliases = new() { "TR" } }); var copy = settings.Copy();
            copy.Buildings[0].Aliases[0] = "AUT"; copy.Sites[0].Openings[0].From = "10:00"; copy.SourcePriority.Reverse();
            Check(settings.Buildings[0].Aliases[0] == "TR" && site.Openings[0].From == "08:00" && settings.SourcePriority[0] == "manual");
        });
        test("Terrain reference validation rejects orphan buildings excessive hierarchy and invalid openings", () =>
        {
            var settings = Settings(); settings.Buildings.Add(new() { SiteId = Guid.NewGuid().ToString("N"), Name = "Bâtiment" }); Reject<InvalidDataException>(settings.Validate);
            settings.Buildings.Clear(); settings.Cities[0].TerritoryPath = string.Join('/', Enumerable.Repeat("Niveau", 13)); Reject<InvalidDataException>(settings.Validate);
            settings.Cities[0].TerritoryPath = ""; settings.Sites.Add(new() { CityId = settings.Cities[0].Id, Name = "Agence", Openings = new() { new() { From = "14:00", Until = "12:00" } } }); Reject<InvalidDataException>(settings.Validate);
        });
        test("Priority allows GLPI location before or after title with explicit explanation", () =>
        {
            var source = Source("Paris incident"); source.Rows[0][3] = "Lyon"; var settings = Settings(); settings.LocationColumn = "Localisation"; settings.SourcePriority = new() { "location", "title" };
            var location = Detect(source, settings).Matches.Single(); Check(location.CityName == "Lyon" && location.Explanation.Contains("champ GLPI"));
            settings.SourcePriority.Reverse(); Check(Detect(source, settings).Matches.Single().CityName == "Paris");
        });
        test("Priority missing location falls back but ambiguous location requires review", () =>
        {
            var source = Source("Paris incident", "Paris incident"); source.Rows[1][3] = "Paris Lyon"; var settings = Settings(); settings.LocationColumn = "Localisation"; settings.SourcePriority = new() { "location", "title" };
            var result = Detect(source, settings); Check(result.Matches[0].CityName == "Paris" && result.Matches[1].State == CityMatchState.Ambiguous);
        });
        test("Priority location works without title column and rejects duplicate configuration entries", () =>
        {
            var source = Source("Unused"); source.Headers[1] = "Autre"; source.Rows[0][3] = "Lyon"; var settings = Settings(); settings.LocationColumn = "Localisation"; settings.SourcePriority = new() { "location" };
            Check(Detect(source, settings).Matches.Single().CityName == "Lyon"); settings.SourcePriority.Add("location"); Reject<InvalidDataException>(settings.Validate);
        });
        test("Manual priority is configurable and correction reasons survive local persistence", () =>
        {
            var source = Source("Paris"); var settings = Settings(); var key = CityOverrideStore.SourceKey(source.SourceUrl);
            var store = new CityOverrideStore().With(new() { CityId = settings.Cities[1].Id, Reason = "Site confirmé par le demandeur" }, key, "1");
            var path = Path.Combine(Path.GetTempPath(), "city-reason-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                store.Save(path); var read = CityOverrideStore.Load(path); var corrected = Detect(source, settings, read.Overrides).Matches.Single();
                Check(corrected.CityName == "Lyon" && corrected.Explanation.Contains("confirmé par le demandeur") && source.Rows[0][1] == "Paris");
                settings.SourcePriority = new() { "title", "manual" }; Check(Detect(source, settings, read.Overrides).Matches.Single().CityName == "Paris");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        test("Position rules distinguish beginning end and a one based token offset", () =>
        {
            var settings = Settings(); var rule = new CityAliasRule { Term = "PX", Position = "start" }; settings.Cities[0].Rules.Add(rule);
            var source = Source("PX panne", "panne PX", "ticket PX panne"); Check(Detect(source, settings).Matches.Select(value => value.State == CityMatchState.Matched).SequenceEqual(new[] { true, false, false }));
            rule.Position = "end"; Check(Detect(source, settings).Matches.Select(value => value.State == CityMatchState.Matched).SequenceEqual(new[] { false, true, false }));
            rule.Position = "token"; rule.TokenPosition = 2; Check(Detect(source, settings).Matches.Select(value => value.State == CityMatchState.Matched).SequenceEqual(new[] { false, true, true }));
            rule.TokenPosition = 0; Reject<InvalidDataException>(settings.Validate);
        });
        test("Position rule checks a later eligible occurrence even when earlier one fails", () =>
        {
            var settings = Settings(); settings.Cities[0].Rules.Add(new() { Term = "PX", Position = "end" });
            Check(Detect(Source("PX intervention PX"), settings).Matches.Single().CityName == "Paris");
        });
        test("Regex title format is a constraint and malformed formats fail validation", () =>
        {
            var settings = Settings(); var rule = new CityAliasRule { Term = "PX", TitlePattern = "^IT PX [0-9]{3}$" }; settings.Cities[0].Rules.Add(rule);
            var result = Detect(Source("IT PX 123", "PX ticket"), settings); Check(result.Matches[0].CityName == "Paris" && result.Matches[1].State != CityMatchState.Matched);
            rule.TitlePattern = "("; Reject<InvalidDataException>(settings.Validate);
        });
        test("Regex pathological titles are bounded and never accepted on timeout", () =>
        {
            var settings = Settings(); settings.Cities[0].Rules.Add(new() { Term = "PX", TitlePattern = "^PX (a+)+$" }); var source = Source("PX " + new string('a', 15000) + "!");
            var watch = System.Diagnostics.Stopwatch.StartNew(); var result = Detect(source, settings); Check(result.Matches.Single().State != CityMatchState.Matched && watch.Elapsed < TimeSpan.FromSeconds(3));
        });
        test("Global preview counts all source rows while bounding displayed changes", () =>
        {
            var settings = Settings(); var source = Source(Enumerable.Repeat("PX", 2005).ToArray()); var changed = CityDetectionReview.Promote(settings, settings.Cities[0].Id, "PX");
            var preview = CityDetectionReview.Compare(Detect(source, settings), Detect(source, changed));
            Check(preview.TotalRows == 2005 && preview.ChangedRows == 2005 && preview.Changes.Count == 1000 && settings.Cities[0].Rules.Count == 0);
        });
        test("Global preview catches disabling detection and refuses another source or cancellation", () =>
        {
            var settings = Settings(); var source = Source("Paris"); var before = Detect(source, settings); var disabled = settings.Copy(); disabled.Enabled = false;
            Check(CityDetectionReview.Compare(before, Detect(source, disabled)).ChangedRows == 1);
            Reject<InvalidDataException>(() => CityDetectionReview.Compare(before, Detect(Source("Paris"), settings)));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Reject<OperationCanceledException>(() => CityDetectionReview.Compare(before, Detect(source, disabled), cancelled.Token));
        });
        test("Matched term highlighting retains accents Unicode offsets and excludes rejected evidence", () =>
        {
            const string title = "🔧 Évry-Courcouronnes puis Evry Courcouronnes";
            var evidence = new[] { new CityMatchEvidence("x", "Évry", "Evry Courcouronnes", "", 0, "Retenue", ""), new CityMatchEvidence("y", "Puis", "puis", "", 0, "Rejetée", "") };
            var ranges = CityDetectionReview.Highlights(title, evidence); Check(ranges.Count == 2 && title.Substring(ranges[0].Start, ranges[0].Length) == "Évry-Courcouronnes" && title.Substring(ranges[1].Start, ranges[1].Length) == "Evry Courcouronnes");
        });
        test("Correction promotion remains a draft and supports an exact context column", () =>
        {
            var settings = Settings(); var source = Source("PX", "PX"); source.Rows[0][3] = "Équipe Nord"; source.Rows[1][3] = "Sud";
            var draft = CityDetectionReview.Promote(settings, settings.Cities[0].Id, "PX", "Nord", "Localisation"); var result = Detect(source, draft);
            Check(settings.Cities[0].Rules.Count == 0 && result.Matches[0].CityName == "Paris" && result.Matches[1].State != CityMatchState.Matched);
        });
    }
}
