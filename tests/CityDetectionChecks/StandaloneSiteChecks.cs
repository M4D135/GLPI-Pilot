using System.Text.Json;
using GlpiPilot.Core;

internal static class StandaloneSiteChecks
{
    private static void Check(bool value, string reason = "Assertion failed") { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(Action run) { try { run(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Expected InvalidDataException"); }
    private static SiteDefinition Site(string name = "Atelier Central", params string[] aliases) => new() { Name = name, Aliases = aliases.ToList() };
    private static CitySettings Settings(params SiteDefinition[] sites) => new() { Sites = sites.ToList() };
    private static ExtractionResult Source(params string[] titles) => new()
    {
        Headers = new() { "ID", "Titre", "Statut" }, Rows = titles.Select((title, index) => new[] { (index + 1).ToString(), title, "Nouveau" }).ToList(),
        SourceUrl = "https://reference.example.invalid/glpi/front/ticket.php", Complete = true, ExtractedAt = DateTimeOffset.UtcNow
    };
    private static ExtractionResult Csv(string[] headers, params string[][] rows) => new() { Headers = headers.ToList(), Rows = rows.ToList() };
    private static CityEnrichmentResult Detect(ExtractionResult source, CitySettings settings) => CityDetectionService.Enrich(source, settings, new());

    public static void Register(Action<string, Action> test)
    {
        test("Standalone sites retain identity aliases and buildings without creating a city", () =>
        {
            var site = Site("Atelier Central", "AC"); var settings = Settings(site); settings.Buildings.Add(new() { SiteId = site.Id, Name = "Pavillon Bleu" });
            settings.Validate(); var copy = settings.Copy(); copy.Normalize();
            var restored = JsonSerializer.Deserialize<CitySettings>(JsonSerializer.Serialize(copy))!; restored.Validate();
            Check(restored.Cities.Count == 0 && restored.Sites.Single().CityId == "" && restored.Sites[0].Id == site.Id && restored.Sites[0].Aliases.Single() == "AC");
            Check(restored.Buildings.Single().SiteId == site.Id && restored.Sites[0].Latitude == null && restored.Sites[0].Longitude == null);
        });
        test("Standalone sites allow empty parent but reject unknown null or whitespace parents", () =>
        {
            var site = Site(); var settings = Settings(site); settings.Validate();
            foreach (var invalid in new[] { Guid.NewGuid().ToString("N"), " ", null }) { site.CityId = invalid!; Reject(settings.Validate); }
            site.CityId = ""; settings.Validate();
        });
        test("Minimal site CSV imports true sites with no municipality requirement", () =>
        {
            var csv = Csv(new[] { "Type", "Nom", "Alias" }, new[] { "Site", "Atelier Central", "AC; Atelier" }, new[] { "Site", "Annexe Est", "AE" });
            var current = new CitySettings(); var preview = CityReferenceService.Preview(csv, current);
            Check(current.Sites.Count == 0 && preview.Settings.Cities.Count == 0 && preview.Settings.Sites.Count == 2);
            Check(preview.Settings.Sites.All(site => site.CityId.Length == 0) && preview.Changes.All(change => change.Type == "Site" && change.Detail.Contains("commune non renseignée")));
            var result = Detect(Source("AC imprimante", "AE connexion"), preview.Settings);
            Check(result.Applied && result.SiteOnlyCount == 2 && result.SiteMatchedCount == 2 && result.MatchedCount == 0);
        });
        test("Standalone reference CSV round trip retains site and building identifiers", () =>
        {
            var site = Site("Atelier Central", "AC"); site.Latitude = 42; site.Longitude = -3; var settings = Settings(site);
            settings.Buildings.Add(new() { SiteId = site.Id, Name = "Pavillon Bleu", Floor = "RDC" });
            var csv = CityReferenceService.Export(settings); var restored = CityReferenceService.Preview(csv, new()).Settings;
            Check(restored.Cities.Count == 0 && restored.Sites.Single().Id == site.Id && restored.Sites[0].CityId == "" && restored.Sites[0].Longitude == -3);
            Check(restored.Buildings.Single().SiteId == site.Id && restored.Buildings[0].Floor == "RDC");
            Check(CityReferenceService.Preview(csv, restored).Changes.All(change => change.Kind == "Identique"));
        });
        test("Omitted site parent preserves relationship while explicit empty parent detaches", () =>
        {
            var city = new CityDefinition { Name = "Ville Exemple" }; var site = Site("Atelier Central", "Ancien"); site.CityId = city.Id;
            var settings = Settings(site); settings.Cities.Add(city);
            var minimal = Csv(new[] { "Type", "Nom", "Alias" }, new[] { "Site", site.Name, "Nouveau" });
            var retained = CityReferenceService.Preview(minimal, settings).Settings.Sites.Single();
            Check(retained.Id == site.Id && retained.CityId == city.Id && retained.Aliases.SequenceEqual(new[] { "Nouveau" }));
            Check(site.Aliases.Single() == "Ancien");
            var explicitEmpty = Csv(new[] { "Type", "Id", "Nom", "VilleId" }, new[] { "Site", site.Id, site.Name, "" });
            var detached = CityReferenceService.Preview(explicitEmpty, settings).Settings.Sites.Single();
            Check(detached.Id == site.Id && detached.CityId == "" && detached.Aliases.Single() == "Ancien" && site.CityId == city.Id);
        });
        test("A standalone site can later attach to a city without losing site identity", () =>
        {
            var site = Site("Atelier Central", "AC"); var settings = Settings(site); var city = new CityDefinition { Name = "Ville Exemple" }; settings.Cities.Add(city);
            var source = Source("AC incident"); Check(Detect(source, settings).Matches.Single().State == CityMatchState.SiteOnly);
            var csv = Csv(new[] { "Type", "Id", "VilleId", "Nom" }, new[] { "Site", site.Id, city.Name, site.Name });
            var attached = CityReferenceService.Preview(csv, settings).Settings; var match = Detect(source, attached).Matches.Single();
            Check(match.State == CityMatchState.Matched && match.CityId == city.Id && match.SiteId == site.Id && attached.Sites[0].Aliases.Single() == "AC");
            Check(settings.Sites[0].CityId == "" && attached.Cities.Count == 1);
        });
        test("Ambiguous minimal import never chooses between homonymous sites", () =>
        {
            var city = new CityDefinition { Name = "Ville Exemple" }; var settings = Settings(Site(), Site()); settings.Cities.Add(city); settings.Sites[1].CityId = city.Id;
            var csv = Csv(new[] { "Type", "Nom", "Alias" }, new[] { "Site", "Atelier Central", "AC" });
            Reject(() => CityReferenceService.Preview(csv, settings)); Check(settings.Sites.All(site => site.Aliases.Count == 0));
        });
        test("Standalone matching preserves source cells and recognizes its building", () =>
        {
            var site = Site(); var settings = Settings(site); var building = new BuildingDefinition { SiteId = site.Id, Name = "Pavillon Bleu", Aliases = new() { "PB" } }; settings.Buildings.Add(building);
            var source = Source("PB imprimante"); var result = Detect(source, settings); var match = result.Matches.Single();
            Check(match.State == CityMatchState.SiteOnly && match.CityId == null && match.CityName == null && match.CandidateIds.Count == 0 && match.SiteId == site.Id && match.BuildingId == building.Id);
            Check(result.Data.Rows[0].Take(source.Headers.Count).SequenceEqual(source.Rows[0]));
            Check(result.Data.Rows[0][result.Data.Headers.IndexOf(result.CityColumnName)] == "" && result.Data.Rows[0][result.Data.Headers.IndexOf(result.SiteColumnName)] == site.Name);
            Check(result.Data.Rows[0][result.Data.Headers.IndexOf(result.StateColumnName)] == "Commune non renseignée");
        });
        test("A city word in the title does not invent a parent for a standalone site", () =>
        {
            var site = Site(); var settings = Settings(site); settings.Cities.Add(new() { Name = "Ville Exemple" });
            var match = Detect(Source("Ville Exemple Atelier Central"), settings).Matches.Single();
            Check(match.State == CityMatchState.SiteOnly && match.SiteId == site.Id && match.CityId == null && settings.Sites[0].CityId == "");
        });
        test("Multiple standalone or mixed sites remain ambiguous without a fabricated city", () =>
        {
            var settings = Settings(Site("Atelier Bleu", "Atelier"), Site("Atelier Vert", "Atelier"));
            var match = Detect(Source("Atelier incident"), settings).Matches.Single();
            Check(match.State == CityMatchState.Ambiguous && match.SiteId == null && match.CityId == null && match.SiteState == "ambiguous");
            var city = new CityDefinition { Name = "Ville Exemple" }; settings.Cities.Add(city); settings.Sites[1].CityId = city.Id;
            match = Detect(Source("Atelier incident"), settings).Matches.Single();
            Check(match.State == CityMatchState.Ambiguous && match.SiteId == null && match.CityId == null);
        });
        test("Longest standalone aliases resolve overlapping names without approximate guessing", () =>
        {
            var shortSite = Site("Atelier", "AT"); var longSite = Site("Centre Atelier"); var settings = Settings(shortSite, longSite);
            var result = Detect(Source("Centre Atelier imprimante", "Atelier puis Centre Atelier", "CATALOGUE"), settings);
            Check(result.Matches[0].SiteId == longSite.Id && result.Matches[1].State == CityMatchState.Ambiguous && result.Matches[2].State == CityMatchState.Unmatched);
        });
        test("Priority GLPI field recognizes standalone site without falling through to a title city", () =>
        {
            var site = Site(); var settings = Settings(site); settings.Cities.Add(new() { Name = "Ville Exemple" }); settings.LocationColumn = "Lieu"; settings.SourcePriority = new() { "location", "title" };
            var source = Source("Ville Exemple"); source.Headers.Add("Lieu"); source.Rows[0] = source.Rows[0].Append(site.Name).ToArray();
            var match = Detect(source, settings).Matches.Single(); Check(match.State == CityMatchState.SiteOnly && match.SiteId == site.Id && match.CityId == null);
        });
        test("Standalone probes count sites without increasing city coverage or double counting rows", () =>
        {
            var settings = Settings(Site()); settings.Cities.AddRange(new[] { new CityDefinition { Name = "Rivage" }, new CityDefinition { Name = "Montagne" } });
            var result = Detect(Source("Rivage", "Atelier Central", "Rivage Montagne", "Inconnu"), settings);
            var snapshot = DashboardService.Calculate(result.Data, new()); CityDetectionService.AppendDashboardProbes(snapshot, result);
            Check(result.MatchedCount == 1 && result.SiteOnlyCount == 1 && result.AmbiguousCount == 1 && result.UnmatchedCount == 1);
            Check(snapshot.Metric("city-matched")!.Value == 1 && snapshot.Metric("site-without-city")!.Value == 1 && snapshot.Metric("site-matched")!.Value == 1 && snapshot.Metric("city-coverage")!.Value == 25);
            var rows = snapshot.Distributions.Single(item => item.Key == "city-detection").Buckets.SelectMany(bucket => bucket.Rows).Order().ToArray();
            Check(rows.SequenceEqual(new[] { 0, 1, 2, 3 }));
        });
        test("Explicit standalone site corrections persist without a municipality and invalidate removed references", () =>
        {
            var site = Site(); var settings = Settings(site); var source = Source("Sans localisation");
            var correction = new CityTicketOverride { SiteId = site.Id, Reason = "Site confirmé" };
            var store = new CityOverrideStore().With(correction, CityOverrideStore.SourceKey(source.SourceUrl), "1"); store.Validate();
            var restored = JsonSerializer.Deserialize<CityOverrideStore>(JsonSerializer.Serialize(store))!; restored.Validate();
            var match = CityDetectionService.Enrich(source, settings, new(), overrides: restored.Overrides).Matches.Single();
            Check(match.State == CityMatchState.SiteOnly && match.SiteId == site.Id && match.CityId == null && match.IsOverride);
            settings.Sites[0].Id = Guid.NewGuid().ToString("N");
            match = CityDetectionService.Enrich(source, settings, new(), overrides: restored.Overrides).Matches.Single();
            Check(match.State == CityMatchState.Ambiguous && match.SiteId == null && match.CityId == null && match.IsOverride);
            Reject(() => new CityOverrideStore().With(new(), CityOverrideStore.SourceKey(source.SourceUrl), "1"));
        });
    }
}
