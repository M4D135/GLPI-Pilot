using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class SimulationChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Simulation: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("all stores and settings are isolated and the original bytes survive exit", IsolatedStores);
        Add("followups, tasks, assignments, solutions and statuses mutate the imported copy and its indicators", TicketActions);
        Add("duplicate and missing IDs keep independent editable source occurrences", DuplicateOccurrences);
        Add("preview tokens reject replay, wrong tickets, stale data and cancellation", ActionTokens);
        Add("library capture and team synchronization operate on real local outboxes", LibraryAndTeam);
        Add("archives compare simulated edits without changing the real history", History);
        Add("optional site populations can be edited and calculated for an imported file without certifying GLPI", PopulationReferences);
        Add("terrain planning persists an exercise route and weather without network", Terrain);
        Add("the assistant ignores credentials and generates locally from the imported stock", Assistant);
        Add("update, installation and rollback form a virtual stateful scenario", Updates);
        Add("scheduled and recipe collection stay in the child application", Automation);
        Add("explicit exports cannot overwrite the real profile or the source file", ExportBoundaries);
        Add("a second import and a fresh application cannot adopt temporary simulation data", RestartAndReimport);
        return tests;
    }

    private static void Check(bool value, string message = "Simulation contract failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> action, string text)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        { Check(error.Message.Contains(text, StringComparison.OrdinalIgnoreCase), error.Message); return; }
        throw new InvalidOperationException("Expected refusal: " + text);
    }
    private static string SimulationRoot(SessionFixture f) => Directory.GetParent(f.App.Settings.ExportFolder)!.FullName;
    private static Dictionary<string, string> Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    private static void Same(Dictionary<string, string> before, Dictionary<string, string> after)
        => Check(before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value), "A real profile file changed during simulation.");
    private static async Task<string> Import(SessionFixture f, int count = 2)
    { var path = await OfflineImportChecks.WriteCsv(f, count: count); await f.Call("records.test.import"); return path; }
    private static async Task<string> Open(SessionFixture f, int row = 0)
        => (await f.Call("workplace.ticket.open", new { rowIndex = row, revision = f.App.Revision })).GetProperty("personal").GetProperty("key").GetString()!;
    private static async Task<JsonElement> Apply(SessionFixture f, string key, string field, string value)
    {
        var inspected = await f.Call("workplace.actions.inspect", new { ticketKey = key });
        Check(inspected.GetProperty("canWrite").GetBoolean() && inspected.GetProperty("simulated").GetBoolean());
        var candidate = inspected.GetProperty("candidates").EnumerateArray().Single(candidate => candidate.GetProperty("key").GetString() == "simulation-" + field);
        var prepared = await f.Call("workplace.actions.preview", new { ticketKey = key, adapterKey = candidate.GetProperty("key").GetString(), value });
        return await f.Call("workplace.actions.confirm", new { ticketKey = key, token = prepared.GetProperty("token").GetString(), confirmed = true });
    }

    private static async Task IsolatedStores(SessionFixture f)
    {
        await f.Call("app.appearance", new { theme = "light" });
        var original = f.App.Result; var saved = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json);
        var realNote = await Open(f); await f.Call("workplace.personal.save", new { ticketKey = realNote, notes = "REAL_NOTE" });
        var realContribution = await f.Call("workspace.save", new { item = new { title = "Real note", body = "REAL_BODY", kind = "note" } });
        await OfflineImportChecks.WriteCsv(f); var before = Fingerprints(f.Root);
        await f.Call("records.test.import"); var sandbox = SimulationRoot(f);
        Check(sandbox != f.Root && sandbox.StartsWith(Path.GetTempPath(), StringComparison.Ordinal));
        Check((await f.Call("records.schema", new { revision = f.App.Revision, accept = true })).GetProperty("available").GetBoolean());
        Check((await f.Call("workplace.get")).GetRawText().Contains("REAL_NOTE", StringComparison.Ordinal) == false);
        Check((await f.Call("workspace.list")).GetProperty("total").GetInt64() == 0);
        var settings = f.App.Settings.Copy(); settings.Theme = "dark"; settings.ExportFolder = Path.Combine(f.Root, "unwanted-exports"); settings.History.RestoreLastOnStartup = true;
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        Check(f.App.Settings.ExportFolder == Path.Combine(sandbox, "Exports") && !f.App.Settings.History.RestoreLastOnStartup);
        await f.Call("workplace.personal.save", new { ticketKey = await Open(f), notes = "SIMULATION_ONLY" });
        await f.Call("workspace.save", new { item = new { title = "Simulation note", body = "SIMULATION_ONLY", kind = "note" } });
        var query = new SavedQuery { Name = "Simulation query", BaseProbe = "active" }; await f.Call("queries.save", new { query });
        var library = (await f.Call("library.get")).GetProperty("settings").Deserialize<LibraryPreferences>(PilotApplication.Json)!;
        library.LocalFolder = Path.Combine(f.Root, "outside-library"); library.Host = "real-server.invalid";
        await f.Call("library.settings.save", new { settings = library, password = "TEST_SECRET", rememberPassword = true });
        var state = await f.Call("library.get");
        Check(!state.GetProperty("settings").GetProperty("hasPassword").GetBoolean() && state.GetProperty("settings").GetProperty("localFolder").GetString()!.StartsWith(sandbox, StringComparison.Ordinal));
        Check(!File.ReadAllText(Path.Combine(sandbox, "library.local.json")).Contains("TEST_SECRET", StringComparison.Ordinal));
        Same(before, Fingerprints(f.Root));
        await f.Call("records.test.exit");
        Check(!f.App.IsTestMode && ReferenceEquals(f.App.Result, original) && saved == JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json));
        Check(!Directory.Exists(sandbox)); Same(before, Fingerprints(f.Root));
        Check((await f.Call("workspace.get", new { id = realContribution.GetProperty("id").GetString() })).GetProperty("body").GetString() == "REAL_BODY");
    }

    private static async Task TicketActions(SessionFixture f)
    {
        await Import(f); var originalCells = f.App.Result!.Rows.Select(row => row.ToArray()).ToArray(); var key = await Open(f);
        var view = await f.Call("workplace.ticket.read", new { ticketKey = key }); Check(view.GetProperty("loaded").GetBoolean());
        var revision = f.App.Revision;
        foreach (var (field, value) in new[] { ("followup", "Diagnostic local confirmé"), ("task", "Tester le branchement"), ("solution", "Réinstaller le pilote et confirmer l’impression") })
            Check((await Apply(f, key, field, value)).GetProperty("state").GetString() == "verified");
        var inspected = await f.Call("workplace.actions.inspect", new { ticketKey = key });
        var assignment = inspected.GetProperty("candidates").EnumerateArray().Single(value => value.GetProperty("key").GetString() == "simulation-assignee");
        var choice = assignment.GetProperty("choices").EnumerateArray().Last();
        await Apply(f, key, "assignee", choice.GetProperty("value").GetString()!);
        var receipt = await Apply(f, key, "status", "5");
        Check(receipt.GetProperty("simulated").GetBoolean() && f.App.Revision > revision);
        var source = f.App.Result!; var roles = DashboardService.ResolveColumns(source.Headers, f.App.EffectiveSettings.Dashboard);
        var statusColumn = roles.Single(role => role.Role == "status").Index!.Value;
        Check(source.Rows.Count == 2 && source.Rows[0][statusColumn] == "Résolu" && source.Rows[1].Take(originalCells[1].Length).SequenceEqual(originalCells[1]));
        Check(source.Rows[0][roles.Single(role => role.Role == "assignee").Index!.Value] == choice.GetProperty("label").GetString());
        var snapshot = DashboardService.Calculate(source, f.App.EffectiveSettings.Dashboard);
        Check(snapshot.Metric("active")!.Value == 1 && snapshot.Metric("resolved")!.Value == 1);
        var detail = await f.Call("workplace.ticket.read", new { ticketKey = key });
        var section = detail.GetProperty("sections").EnumerateArray().Single(value => value.GetProperty("kind").GetString() == "timeline");
        var timeline = await f.Call("workplace.ticket.section", new { ticketKey = key, sectionIndex = section.GetProperty("index").GetInt32() });
        Check(timeline.GetProperty("total").GetInt32() == 3 && timeline.GetProperty("rows").EnumerateArray().Any(row => row.GetProperty("values")[3].GetString() == "Diagnostic local confirmé"));
        var library = await f.Call("library.search", new { resolvedOnly = true });
        Check(library.GetProperty("total").GetInt64() == 1 && library.GetProperty("rows")[0].GetProperty("detailsCurrent").GetBoolean());
        var browser = await f.Call("workplace.ticket.browser", new { ticketKey = key }); Check(browser.GetProperty("simulated").GetBoolean() && browser.GetProperty("ticketKey").GetString() == key);
        Check(f.Events.Any(value => value.Name == "session.changed"));
    }

    private static async Task DuplicateOccurrences(SessionFixture f)
    {
        f.Host.OpenPath = Path.Combine(f.Root, "duplicates.csv");
        await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut\n42;First duplicate;Nouveau\n42;Second duplicate;Nouveau\n;No ID;En attente\n");
        await f.Call("records.test.import"); var first = await Open(f); var second = await Open(f, 1); var absent = await Open(f, 2);
        Check(new[] { first, second, absent }.Distinct().Count() == 3 && f.App.Result!.Rows.Count == 3);
        var added = await f.Call("workplace.queue.add", new { revision = f.App.Revision, rows = new[] { 0, 1, 2 } }); Check(added.GetProperty("added").GetInt32() == 3);
        await Apply(f, second, "status", "6");
        Check(f.App.Result!.Rows[0][0] == "42" && f.App.Result.Rows[0][2] == "Nouveau" && f.App.Result.Rows[1][0] == "42" && f.App.Result.Rows[1][2] == "Clos" && f.App.Result.Rows[2][0] == "");
        var library = await f.Call("library.get"); Check(library.GetProperty("stats").GetProperty("ticketCount").GetInt64() == 3);
        var warnings = (await f.Call("workplace.ticket.read", new { ticketKey = second })).GetProperty("warnings").EnumerateArray().Select(value => value.GetString()).ToArray();
        Check(warnings.Any(value => value!.Contains("identifiant source conservé", StringComparison.Ordinal)));
    }

    private static async Task ActionTokens(SessionFixture f)
    {
        await Import(f); var first = await Open(f); var other = await Open(f, 1);
        async Task<JsonElement> Preview()
        {
            await f.Call("workplace.actions.inspect", new { ticketKey = first });
            return await f.Call("workplace.actions.preview", new { ticketKey = first, adapterKey = "simulation-status", value = "5" });
        }
        var preview = await Preview(); var token = preview.GetProperty("token").GetString();
        await Refuse(() => f.Call("workplace.actions.confirm", new { ticketKey = other, token, confirmed = true }), "absent");
        Check(f.App.Result!.Rows.All(row => row[2] == "En attente"));
        preview = await Preview(); token = preview.GetProperty("token").GetString();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await f.Call("workplace.actions.confirm", new { ticketKey = first, token, confirmed = true }, cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        Check(f.App.Result!.Rows.All(row => row[2] == "En attente"));
        await f.Call("collect.run"); await Refuse(() => f.Call("workplace.actions.confirm", new { ticketKey = first, token, confirmed = true }), "absent");
        preview = await Preview(); token = preview.GetProperty("token").GetString();
        await f.Call("workplace.actions.confirm", new { ticketKey = first, token, confirmed = true });
        await Refuse(() => f.Call("workplace.actions.confirm", new { ticketKey = first, token, confirmed = true }), "absent");
        Check(f.App.Result!.Rows.Count == 2);
    }

    private static async Task LibraryAndTeam(SessionFixture f)
    {
        await Import(f); var capture = await f.Call("library.capture", new { mode = "all" });
        Check(capture.GetProperty("status").GetString() == "completed" && capture.GetProperty("read").GetInt32() == 2);
        Check((await f.Call("library.capture.latest")).GetProperty("runId").GetString() == capture.GetProperty("runId").GetString());
        var key = await Open(f); await f.Call("workplace.personal.save", new { ticketKey = key, notes = "Note partagée simulée" });
        await f.Call("workspace.publishPersonalNote", new { ticketKey = key });
        var contribution = await f.Call("workspace.save", new { item = new { title = "Exercice d’équipe", kind = "knowledge", body = "Connaissance locale", status = "published" } });
        var first = await f.Call("library.sync");
        Check(first.GetProperty("simulated").GetBoolean() && first.GetProperty("tickets").GetProperty("pushed").GetInt32() >= 2 && first.GetProperty("team").GetProperty("pushed").GetInt32() == 2);
        Check(first.GetProperty("pending").GetInt64() == 0);
        var second = await f.Call("library.sync");
        Check(second.GetProperty("tickets").GetProperty("pushed").GetInt32() == 0 && second.GetProperty("team").GetProperty("pushed").GetInt32() == 0);
        var stored = await f.Call("workspace.get", new { id = contribution.GetProperty("id").GetString() });
        Check(!stored.GetProperty("pending").GetBoolean() && stored.GetProperty("centralRevision").GetInt64() > 0);
        var source = f.App.Result; await f.Call("library.loadRecords"); Check(f.App.Result!.Rows.Count == source!.Rows.Count && f.App.Result.Rows[0][0] == "0042");
    }

    private static async Task History(SessionFixture f)
    {
        await Import(f); var before = f.App.Snapshots.List().Single(); var key = await Open(f);
        await Apply(f, key, "status", "5"); var after = f.App.Snapshots.List().First();
        var comparison = await f.Call("history.compare", new { beforeId = before.Id, afterId = after.Id });
        Check(comparison.GetProperty("compatible").GetBoolean() && comparison.GetProperty("resolutionsObserved").GetInt32() == 1);
        Check((await f.Call("history.list")).GetProperty("currentSourceKey").GetString() == after.SourceKey);
        var analytics = await f.Call("history.analyze", new { snapshotId = after.Id, count = 20 }); Check(analytics.GetProperty("snapshotCount").GetInt32() == 2);
        await f.Call("history.open", new { id = before.Id }); Check(f.App.IsHistorical && f.App.Result!.Rows[0][2] == "En attente");
        await f.Call("collect.run"); Check(!f.App.IsHistorical && f.App.Result!.Rows[0][2] == "Résolu");
        await f.Call("records.test.exit"); Check(f.App.Snapshots.List().Count == 0);
    }

    private static async Task Terrain(SessionFixture f)
    {
        await f.Call("app.demo"); var source = f.App.Result!;
        Check(OfflineImportChecks.IncidentHeaders.All(source.Headers.Contains) && source.Rows.Count == 180 && f.App.Settings.Cities.Sites.Count == 4);
        await f.Call("terrain.tours.select", new { revision = f.App.Revision, rows = new[] { 0, 1 }, selected = true });
        var state = await f.Call("terrain.tours.create", new { revision = f.App.Revision, name = "Tournée d’exercice", date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), technician = "ALEX EXEMPLE" });
        var fields = state.GetProperty("draft").Deserialize<Dictionary<string, JsonElement>>(PilotApplication.Json)!;
        fields["start"] = JsonSerializer.SerializeToElement(new TourPoint { Label = "Départ fictif", Latitude = 44, Longitude = 5 }, PilotApplication.Json);
        fields["end"] = fields["start"];
        state = await f.Call("terrain.tours.edit", new { revision = f.App.Revision, draftVersion = state.GetProperty("draftVersion").GetInt32(), fields });
        var route = await f.Call("terrain.tours.route", new { revision = f.App.Revision, draftVersion = state.GetProperty("draftVersion").GetInt32() });
        Check(route.GetProperty("simulated").GetBoolean() && route.GetProperty("segments").GetInt32() >= 2);
        state = route.GetProperty("state");
        var weather = await f.Call("terrain.tours.weather", new { revision = f.App.Revision, draftVersion = state.GetProperty("draftVersion").GetInt32() });
        Check(weather.GetProperty("simulated").GetBoolean() && weather.GetProperty("forecasts").GetInt32() == 2);
        state = weather.GetProperty("state"); await f.Call("terrain.tours.save", new { revision = f.App.Revision, draftVersion = state.GetProperty("draftVersion").GetInt32() });
        var saved = ScheduledTourStore.Load(Path.Combine(SimulationRoot(f), "scheduled-tours.json")).Tours.Single();
        Check(saved.RoadLegs.Count >= 2 && saved.Weather.Count == 2 && saved.RoadLegs.All(leg => leg.Provider.Contains("Simulation", StringComparison.Ordinal)));
        await f.Call("workspace.publishTour", new { id = saved.Id });
        var headers = source.Headers.ToArray(); var site = f.App.Settings.Cities.Sites[1];
        await f.Call("terrain.review.correct", new { revision = f.App.Revision, row = 0, cityId = site.CityId, siteId = site.Id, reason = "Correction locale d’exercice" });
        await Apply(f, await Open(f), "followup", "Vérification de la localisation corrigée");
        await f.Call("collect.run"); await f.Call("collect.run"); await f.Call("automation.recipe");
        Check(f.App.Result!.Headers.SequenceEqual(headers), "Repeated simulation actions and collections must not duplicate derived geographic columns.");
        Check(f.App.Locations!.Matches.Single(match => match.RowIndex == 0) is { IsOverride: true } match && match.SiteId == site.Id,
            "A manual correction must survive simulated actions, re-enrichment and collection.");
    }

    private static async Task PopulationReferences(SessionFixture f)
    {
        var site = new SiteDefinition { Name = "Atelier fictif", Aliases = new() { "AF" } };
        var settings = f.App.Settings.Copy(); settings.Cities.Sites.Add(site);
        await f.Call("settings.save", new { revision = f.App.Revision, settings });
        var realPath = Path.Combine(f.Root, "site-populations.json");
        var realEntry = new SitePopulationEntry { SourceKey = SnapshotStore.SourceKey(f.App.Result!), SiteId = site.Id, SiteLabel = site.Name,
            Unit = "people", Count = 900, FromDate = "2020-01-01", UntilDate = "2099-12-31", Reference = "Référence habituelle fictive" };
        new AnalyticsPopulationStore(realPath).Save(new[] { realEntry });
        var realBytes = await File.ReadAllBytesAsync(realPath);
        f.Host.OpenPath = Path.Combine(f.Root, "sites-for-ratios.csv");
        await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut\n1;AF imprimante;Nouveau\n2;AF connexion;En attente\n");
        await f.Call("records.test.import");
        var list = await f.Call("populations.list");
        var sourceKey = list.GetProperty("sourceKey").GetString()!;
        Check(sourceKey.Length > 0 && list.GetProperty("total").GetInt32() == 0 && list.GetProperty("canEdit").GetBoolean()
            && list.GetProperty("simulation").GetBoolean(),
            "An imported simulation needs its own usable population context, without borrowing real denominators.");
        Check(!f.App.Result!.Provenance.SearchVerified && !f.App.Result.Provenance.ScopeVerified && SnapshotStore.SourceKey(f.App.Result) == "",
            "A usable simulation context must not certify the original file as a GLPI observation.");
        var date = f.App.Result.ExtractedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var entry = new SitePopulationEntry { SourceKey = sourceKey, SiteId = site.Id, SiteLabel = site.Name, Unit = "people", Count = 200,
            FromDate = date, UntilDate = date, Reference = "Effectif saisi pour l’essai" };
        await f.Call("populations.upsert", new { revision = f.App.Revision, entry });
        var storage = new AnalyticsPopulationStore(Path.Combine(SimulationRoot(f), "site-populations.json"));
        var unchanged = await File.ReadAllBytesAsync(realPath);
        Check(storage.Load().Single().Count == 200 && realBytes.SequenceEqual(unchanged));
        static JsonElement Ratio(JsonElement dashboard) => dashboard.GetProperty("snapshot").GetProperty("metrics").EnumerateArray()
            .Single(metric => metric.GetProperty("key").GetString() == "site-per100-people");
        var dashboard = await f.Call("dashboard.get", new { revision = f.App.Revision, businessView = "sites" });
        Check(Ratio(dashboard).GetProperty("value").GetDouble() == 1, "Two local tickets and 200 declared people must produce one ticket per 100 people.");
        // The initial imported observation was already archived before this inventory.
        // A fresh simulated collection captures the new denominator without rewriting it.
        await f.Call("collect.run");
        var archiveReceipt = await f.Call("history.archive", new { revision = f.App.Revision });
        var archive = f.App.Snapshots.Load(archiveReceipt.GetProperty("id").GetString()!);
        Check(archive.Populations?.Single().Count == 200 && archive.Metadata.SourceKey == sourceKey);
        var replacement = entry.Copy(); replacement.Count = 100;
        await f.Call("populations.upsert", new { revision = f.App.Revision, entry = replacement, previous = entry });
        dashboard = await f.Call("dashboard.get", new { revision = f.App.Revision, businessView = "sites" });
        Check(Ratio(dashboard).GetProperty("value").GetDouble() == 2 && f.App.Snapshots.Load(archive.Metadata.Id).Populations!.Single().Count == 200);
        await f.Call("populations.delete", new { entry = replacement });
        dashboard = await f.Call("dashboard.get", new { revision = f.App.Revision, businessView = "sites" });
        Check(Ratio(dashboard).GetProperty("value").ValueKind == JsonValueKind.Null, "Missing denominators must stay unavailable, not become zero.");
        await f.Call("records.test.exit");
        unchanged = await File.ReadAllBytesAsync(realPath);
        var restored = await f.Call("populations.list");
        Check(realBytes.SequenceEqual(unchanged) && restored.GetProperty("rows")[0].GetProperty("count").GetInt32() == 900);
        var unverified = SessionFixture.Data(); unverified.Provenance.SearchVerified = false; unverified.Provenance.ScopeVerified = false;
        f.App.LoadSessionFixture(unverified);
        await Refuse(() => f.Call("populations.upsert", new { revision = f.App.Revision, entry = realEntry }), "confirmé");
    }

    private static async Task Assistant(SessionFixture f)
    {
        await Import(f);
        var settings = await f.Call("assistant.config.save", new { provider = "compatible", endpoint = "https://never-contact.invalid/v1/chat/completions", model = "example", rememberKey = true, apiKey = "TEST_SECRET" });
        Check(!settings.GetProperty("hasSavedKey").GetBoolean());
        var plan = await f.Call("assistant.prepare", new { revision = f.App.Revision, operation = "query", prompt = "Tickets actifs", provider = "compatible", externalEnabled = true, endpoint = "https://never-contact.invalid/v1/chat/completions", model = "example" });
        Check(plan.GetProperty("provider").GetString() == "local");
        var generated = await f.Call("assistant.generate", new { revision = f.App.Revision, preparationId = plan.GetProperty("preparationId").GetString(), confirmed = true });
        Check(generated.GetProperty("kind").GetString() == "query" && generated.GetProperty("query").ValueKind == JsonValueKind.Object);
        Check(!Directory.GetFiles(SimulationRoot(f), "*.json", SearchOption.AllDirectories).Any(path => File.ReadAllText(path).Contains("TEST_SECRET", StringComparison.Ordinal)));
    }

    private static async Task Updates(SessionFixture f)
    {
        await Import(f); var first = await f.Call("update.state"); var original = first.GetProperty("version").GetString();
        var release = await f.Call("update.findOfficial", new { consent = true, edition = "PROD" });
        var plan = await f.Call("update.downloadOfficial", new { consent = true, token = release.GetProperty("token").GetString() });
        var installed = await f.Call("update.install", new { token = plan.GetProperty("token").GetString(), version = plan.GetProperty("version").GetString(), confirmed = true });
        Check(!installed.GetProperty("closing").GetBoolean() && (await f.Call("update.state")).GetProperty("version").GetString() != original);
        var rollback = await f.Call("update.prepareRollback", new { confirmed = true });
        await f.Call("update.install", new { token = rollback.GetProperty("token").GetString(), version = rollback.GetProperty("version").GetString(), confirmed = true });
        Check((await f.Call("update.state")).GetProperty("version").GetString() == original);
        Check(!Directory.GetFiles(SimulationRoot(f), "*.exe", SearchOption.AllDirectories).Any());
    }

    private static async Task Automation(SessionFixture f)
    {
        var realInvoked = false; f.App.ForceScheduledFixture((a, ct) => { realInvoked = true; return Task.FromResult<object?>(null); });
        await Import(f); var revision = f.App.Revision;
        await f.Call("alerts.rules", new { rules = new[] { new AlertRule { Name = "Stock local à examiner", MetricKey = "active", Threshold = 0 } } });
        var incidents = await f.Call("alerts.evaluate"); var incident = incidents.EnumerateArray().Single();
        Check(incident.GetProperty("runId").GetString() == f.App.Result!.Provenance.RunId);
        var contributors = await f.Call("alerts.rows", new { id = incident.GetProperty("id").GetString() });
        Check(contributors.GetProperty("rows").EnumerateArray().Select(row => row.GetInt32()).SequenceEqual(new[] { 0, 1 }), "The alert must open contributors from the exact visible local observation.");
        f.App.DueSimulationTickForChecks(); await f.Call("automation.tick");
        Check(!realInvoked && f.App.Revision > revision && f.App.Result!.Rows.Count == 2);
        await f.Call("automation.recipe"); Check(!realInvoked && f.App.IsTestMode);
        var journal = await f.Call("operations.list");
        Check(journal.GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("code").GetString() == "SIMULATED")
            && journal.GetProperty("health").GetArrayLength() > 0, "Simulated collection must populate the isolated operation journal and collection health.");
    }

    private static async Task ExportBoundaries(SessionFixture f)
    {
        var input = await Import(f); var before = await File.ReadAllBytesAsync(input);
        f.Host.SavePath = input; await Refuse(() => f.Call("records.export", new { revision = f.App.Revision, format = "csv" }), "séparé");
        f.Host.SavePath = Path.Combine(f.Root, "settings.json"); await Refuse(() => f.Call("configuration.export"), "séparé");
        Check((await File.ReadAllBytesAsync(input)).SequenceEqual(before));
        f.Host.SavePath = Path.Combine(SimulationRoot(f), "exercise.csv");
        await f.Call("records.export", new { revision = f.App.Revision, format = "csv" }); Check(File.Exists(f.Host.SavePath));
        var key = await Open(f); var links = await f.Call("workplace.ticket.links", new { ticketKey = key, kind = "document" });
        f.Host.SavePath = Path.Combine(SimulationRoot(f), "exercise-ticket.txt");
        var copied = await f.Call("workplace.document.download", new { ticketKey = key, index = links.GetProperty("rows")[0].GetProperty("index").GetInt32() });
        Check(copied.GetProperty("simulated").GetBoolean() && File.ReadAllText(f.Host.SavePath).Contains("SIMULATION LOCALE", StringComparison.Ordinal));
    }

    private static async Task RestartAndReimport(SessionFixture f)
    {
        await f.Call("app.appearance", new { theme = "light" }); var actual = f.App.Result; var realSettings = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json);
        await Import(f); var firstRoot = SimulationRoot(f); var key = await Open(f); await Apply(f, key, "status", "5");
        await using (var restarted = new PilotApplication(new SessionHost(), (_, _) => { }))
        {
            Check(!restarted.IsTestMode && restarted.Result is null && JsonSerializer.Serialize(restarted.Settings, PilotApplication.Json) == realSettings);
        }
        await OfflineImportChecks.WriteCsv(f, "replacement.csv", 3); await f.Call("records.import");
        Check(f.App.Result!.Rows.Count == 3 && f.App.Result.Rows.All(row => row[2] == "En attente") && SimulationRoot(f) != firstRoot && !Directory.Exists(firstRoot));
        await f.Call("records.test.exit"); Check(ReferenceEquals(f.App.Result, actual) && JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json) == realSettings);
    }
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void DueSimulationTickForChecks()
        {
            var child = _simulation ?? throw new InvalidOperationException("No simulation fixture");
            child._automaticPolicyStamp = JsonSerializer.Serialize(child.Settings.Automation, Json); child._nextAutomatic = DateTimeOffset.Now.AddMinutes(-1);
        }
    }
}
