using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class StandaloneSiteBridgeChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        const string url = "https://standalone.example.invalid/front/ticket.php";
        var settings = new AppSettings { PortalUrl = url, Cities = new() };
        var source = new ExtractionResult
        {
            SourceUrl = url, Complete = true, ExtractedAt = DateTimeOffset.UtcNow,
            Headers = new() { "ID", "Titre", "Statut" }, Rows = new() {
                new[] { "1", "AC imprimante", "Nouveau" }, new[] { "2", "AC réseau", "Nouveau" },
                new[] { "3", "AE connexion", "Nouveau" }, new[] { "4", "Inconnu", "Nouveau" }
            }
        };
        var host = new TerrainTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings); app.LoadFixture(source);
        async Task<JsonElement> Call(string operation, object? args = null) => JsonSerializer.SerializeToElement(
            await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json), CancellationToken.None), PilotApplication.Json);
        static int Draft(JsonElement value) => value.GetProperty("draftVersion").GetInt32();
        var state = await Call("terrain.reference.state");
        host.OpenPath = Path.Combine(root, "standalone-import.csv");
        File.WriteAllText(host.OpenPath, "Type;Nom;Alias\nSite;Atelier Central;AC\nSite;Annexe Est;AE\n");
        var imported = await Call("terrain.reference.import", new { draftVersion = Draft(state) });
        check(imported.GetProperty("additions").GetInt32() == 2 && app.Settings.Cities.Sites.Count == 0, "standalone CSV preview adds two real sites without changing saved settings");
        state = await Call("terrain.reference.importApply", new { token = imported.GetProperty("token").GetString(), confirmed = true });
        check(state.GetProperty("sites").GetInt32() == 2 && state.GetProperty("sitesWithoutCity").GetInt32() == 2 && state.GetProperty("cities").GetInt32() == 0,
            "standalone CSV draft exposes two sites and zero fabricated cities");
        var list = await Call("terrain.reference.list", new { kind = "site" });
        check(list.GetProperty("items").EnumerateArray().All(item => item.GetProperty("parent").GetString() == "Commune non renseignée"), "standalone site list labels its missing commune explicitly");
        var simulated = await Call("terrain.reference.simulate", new { draftVersion = Draft(state), title = "AC imprimante" });
        check(simulated.GetProperty("match").GetProperty("state").GetInt32() == (int)CityMatchState.SiteOnly && simulated.GetProperty("match").GetProperty("cityId").ValueKind == JsonValueKind.Null,
            "standalone simulator reports a real site without inventing a city identifier");
        var preview = await Call("terrain.reference.preview", new { draftVersion = Draft(state), revision = app.Revision });
        state = await Call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true });
        var report = app.Locations!; var geography = app.Settings.Cities;
        var first = geography.Sites.Single(site => site.Name == "Atelier Central"); var second = geography.Sites.Single(site => site.Name == "Annexe Est");
        check(report.SiteOnlyCount == 3 && report.MatchedCount == 0 && geography.Cities.Count == 0 && app.Result!.Rows.Select(row => row.Take(3)).Zip(source.Rows).All(pair => pair.First.SequenceEqual(pair.Second)),
            "standalone apply retains every ticket cell and records site decisions independently of cities");
        var reviewQueue = await Call("terrain.review.list", new { revision = app.Revision, filter = "review" });
        var recognized = await Call("terrain.review.list", new { revision = app.Revision, filter = "matched" });
        check(reviewQueue.GetProperty("total").GetInt32() == 1 && recognized.GetProperty("total").GetInt32() == 3,
            "a recognized standalone site does not create an unnecessary review alert");
        var choices = await Call("terrain.reference.lookup", new { kind = "place" });
        check(choices.GetProperty("items").GetArrayLength() == 2 && choices.GetProperty("items").EnumerateArray().All(item => item.GetProperty("detail").GetString() == "Commune non renseignée"),
            "standalone place picker works without a city dictionary entry");

        var snapshot = DashboardService.Calculate(report.Data, app.EffectiveSettings.Dashboard); CityDetectionService.AppendDashboardProbes(snapshot, report);
        DashboardGeography.Append(snapshot, report, geography);
        check(snapshot.Geography.Count == 0 && snapshot.Metric("city-matched")!.Value == 0 && snapshot.Metric("site-matched")!.Value == 3,
            "sites without coordinates produce no fabricated map point or city coverage");
        var sourceKey = CityOverrideStore.SourceKey(url); var day = snapshot.ExtractedAt.ToString("yyyy-MM-dd");
        var populations = new[] {
            new SitePopulationEntry { SourceKey = sourceKey, SiteId = first.Id, SiteLabel = first.Name, Count = 20, FromDate = day, UntilDate = day },
            new SitePopulationEntry { SourceKey = sourceKey, SiteId = second.Id, SiteLabel = second.Name, Count = 10, FromDate = day, UntilDate = day }
        };
        AnalyticsPopulationStore.Append(snapshot, report, geography, sourceKey, populations);
        check(snapshot.Metric("site-per100-people")!.Value == 10 && snapshot.Metric("site-per100-people")!.Rows.SequenceEqual(new[] { 0, 1, 2 })
            && snapshot.Distributions.Single(item => item.Key == "site-per100-people").Buckets.Sum(bucket => bucket.Count) == 3,
            "standalone site population statistics use each ticket once with its declared denominator");
        var evidence = SnapshotLocationEvidence.Capture(report.Data, report)!;
        var restoredEvidence = JsonSerializer.Deserialize<SnapshotLocationEvidence>(JsonSerializer.Serialize(evidence))!;
        var historical = restoredEvidence.ToReport(report.Data); var historicSnapshot = DashboardService.Calculate(report.Data, new());
        CityDetectionService.AppendDashboardProbes(historicSnapshot, historical);
        check(historical.Matches.Count(match => match.State == CityMatchState.SiteOnly) == 3 && historicSnapshot.Metric("site-without-city")!.Value == 3 && historicSnapshot.Metric("city-matched")!.Value == 0,
            "archived standalone location evidence preserves site counts without recomputing a city");
        var coordinateDraft = geography.Copy(); coordinateDraft.Sites.Single(site => site.Id == first.Id).Latitude = 43; coordinateDraft.Sites.Single(site => site.Id == first.Id).Longitude = 5;
        var mapped = DashboardService.Calculate(report.Data, new()); DashboardGeography.Append(mapped, report, coordinateDraft);
        check(mapped.Geography.Count == 1 && mapped.Geography[0].Rows.SequenceEqual(new[] { 0, 1 }), "only explicitly provided standalone coordinates create a map point");
        var portablePath = Path.Combine(root, "standalone-configuration.json"); ConfigurationTransferService.Export(portablePath, app.Settings);
        var portable = ConfigurationTransferService.Import(portablePath, new());
        check(portable.Cities.Cities.Count == 0 && portable.Cities.Sites.Count == 2 && portable.Cities.Sites.All(site => site.CityId == ""), "portable configuration transfer preserves standalone references for other installations");

        var context = TourPlanningService.Prepare(report, geography, app.EffectiveSettings.Dashboard);
        check(ScheduledTourService.PlaceChoices(context).Count == 2 && ScheduledTourService.PlaceChoices(context).All(choice => choice.Label.Contains("commune non renseignée")),
            "tour place choices do not index a missing city");
        var tour = ScheduledTourService.Create(context, new[] { 0, 2 }, "Sites à visiter", new DateOnly(2030, 1, 7)); tour.Validate();
        var schedule = ScheduledTourService.Calculate(tour, context);
        check(tour.Stops.Count == 2 && tour.Stops.All(stop => stop.CityId == "" && stop.SiteId.Length > 0) && schedule.Visits.Count == 2 && schedule.Visits.All(visit => visit.Place.Contains("commune non renseignée") && visit.StraightLineKm == null),
            "scheduled visits accept standalone sites while keeping unavailable distances empty");
        var savedTour = Path.Combine(root, "standalone-tours.json"); new ScheduledTourStore().With(tour).Save(savedTour);
        check(ScheduledTourStore.Load(savedTour).Tours.Single().Stops.All(stop => stop.CityId.Length == 0), "standalone scheduled stops survive persistence without a synthetic city");

        await reject(() => Call("terrain.review.correct", new { revision = app.Revision, row = 3, cityId = "", siteId = first.Id, reason = "" }), "standalone correction still requires a reason");
        var correction = await Call("terrain.review.correct", new { revision = app.Revision, row = 3, cityId = "", siteId = first.Id, reason = "Site confirmé" });
        check(!correction.GetProperty("removed").GetBoolean() && app.Locations!.Matches[3].State == CityMatchState.SiteOnly && app.Locations.Matches[3].IsOverride,
            "standalone correction is saved rather than mistaken for a remove command");
        await Call("terrain.review.correct", new { revision = app.Revision, row = 3, cityId = "", siteId = "", reason = "" });
        check(app.Locations!.Matches[3].State == CityMatchState.Unmatched, "explicitly clearing site and city removes the standalone correction");

        var city = new CityDefinition { Name = "Commune Exemple" }; state = await Call("terrain.reference.state");
        state = await Call("terrain.reference.upsert", new { draftVersion = Draft(state), kind = "city", entry = city });
        var attached = first.Copy(); attached.CityId = city.Id;
        state = await Call("terrain.reference.upsert", new { draftVersion = Draft(state), kind = "site", entry = attached });
        preview = await Call("terrain.reference.preview", new { draftVersion = Draft(state), revision = app.Revision });
        await Call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true });
        check(app.Locations!.MatchedCount == 2 && app.Locations.SiteOnlyCount == 1 && app.Locations.Matches[0].CityId == city.Id && app.Locations.Matches[0].SiteId == first.Id
            && app.Settings.Cities.Sites.Single(site => site.Id == second.Id).CityId == "", "later explicit city attachment recalculates only the relevant site and retains all identities");
    }
}
