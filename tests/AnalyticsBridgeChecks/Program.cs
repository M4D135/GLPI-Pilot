using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private static int _passed;
    private static string _root = "";
    private static readonly DateTimeOffset Clock = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); _passed++; }
    private static async Task Reject<T>(Func<Task> action, string label) where T : Exception
    { try { await action(); } catch (T) { Check(true, label); return; } throw new Exception("FAIL: " + label + " (expected " + typeof(T).Name + ")"); }
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object? args = null, CancellationToken ct = default)
        => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json), ct), PilotApplication.Json);
    private static string Str(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static JsonElement Widget(JsonElement model, string id) => model.GetProperty("widgets").EnumerateArray().Single(w => Str(w, "id") == id);
    private static JsonElement Metric(JsonElement model, string key) => model.GetProperty("snapshot").GetProperty("metrics").EnumerateArray().Single(m => Str(m, "key") == key);
    private static string[] Row(string id, string status = "Nouveau", string person = "Alex", string group = "Support", string title = "Incident VPN", string cost = "10")
        => new[] { id, title, status, person, group, "01/09/2026 10:00", "01/10/2026 10:00", "03/10/2026 10:00", cost, "Réseau", "Haute" };
    private static ExtractionResult Data(int day, params string[][] rows) => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Attribué à - Technicien", "Attribué à - Groupe de techniciens", "Date d’ouverture", "Date de modification", "Échéance", "Coût", "Catégorie", "Priorité" },
        Rows = rows.ToList(), ExtractedAt = Clock.AddDays(day), SourceUrl = "https://glpi.example.test/front/ticket.php", Scope = "Recherche vérifiée", Complete = true, ExpectedTotal = rows.Length,
        Provenance = new() { RequestedUrl = "https://glpi.example.test/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=all", SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "entity-1", Method = "native-csv", SourceKind = "glpi" }
    };
    private static AppSettings Settings()
    {
        var city = new CityDefinition { Name = "Lyon", Latitude = 45.75, Longitude = 4.85 };
        var site = new SiteDefinition { CityId = city.Id, Name = "Campus", Latitude = 45.76, Longitude = 4.86 };
        var settings = new AppSettings { PortalUrl = Data(0).Provenance.RequestedUrl, Queries = new(), ExportFolder = Path.Combine(_root, "Exports"),
            History = new() { RetentionDays = 3650, MaximumSnapshots = 500 }, Cities = new() { Cities = new() { city }, Sites = new() { site } } };
        var board = DashboardLayoutSettings.CreateBoard("Analyse de contrôle", "empty");
        board.Widgets.Add(new() { Id = "total", Kind = "metric", SourceKey = "total", Title = "Volume observé", ShowMiniHistory = true });
        board.Widgets.Add(new() { Id = "filtered", Kind = "metric", SourceKey = "total", Title = "Nouveaux de la personne", Filters = new() { new() { Kind = "distribution", SourceKey = "status", Value = "Nouveau" } } });
        board.Widgets.Add(new() { Id = "status", Kind = "distribution", SourceKey = "status", Title = "Statuts observés", Presentation = "pareto" });
        board.Widgets.Add(new() { Id = "map", Kind = "map", SourceKey = "active", Title = "Sites des tickets actifs" });
        board.Widgets.Add(new() { Id = "population", Kind = "metric", SourceKey = "site-per100-people", Title = "Tickets pour 100 personnes" });
        settings.Dashboard.Layout = new() { Boards = new() { board }, ActiveBoardId = board.Id };
        settings.Dashboard.PersonalLayout = settings.Dashboard.Layout.Copy();
        return settings;
    }
    private static CityEnrichmentResult Locations(ExtractionResult data, AppSettings settings) => new()
    {
        Source = data, Data = data, Applied = true, MatchedCount = data.Rows.Count, SiteMatchedCount = data.Rows.Count,
        Matches = data.Rows.Select((_, row) => new CityMatch(row, settings.Cities.Cities[0].Id, "Lyon", CityMatchState.Matched, new[] { settings.Cities.Cities[0].Id })
        { SiteId = settings.Cities.Sites[0].Id, SiteName = "Campus", SiteState = "matched", Confidence = "high" }).ToArray()
    };
    private static SitePopulationEntry Population(ExtractionResult data, AppSettings settings, int count = 100) => new()
    { SourceKey = SnapshotStore.SourceKey(data), SiteId = settings.Cities.Sites[0].Id, SiteLabel = "Campus", Count = count, FromDate = "2026-01-01", UntilDate = "2026-12-31", Reference = "Inventaire daté" };
    private static async Task Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Analytics-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", _root);
        try
        {
            await CheckRuntime(); await CheckLayoutImports(); await CheckWeatherSourceReliability(); CheckScopedHistory();
            if (args.Contains("--web")) await CheckWeb(args);
            Console.WriteLine($"{_passed}/{_passed} analytics bridge and scoped-history checks passed.");
        }
        finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", null); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", null); try { Directory.Delete(_root, true); } catch (IOException) { } }
    }
    private static async Task CheckRuntime()
    {
        var settings = Settings(); var host = new AnalyticsTestHost(); var snapshots = new SnapshotStore(Path.Combine(_root, "bridge-history"));
        await using var app = new PilotApplication(host, (_, _) => { }, settings, snapshots: snapshots);
        Check(app.Operations.Count >= 45 && app.Operations.Distinct().Count() == app.Operations.Count, "Analytics operations are registered once through the closed C# dispatcher.");
        await Reject<InvalidOperationException>(async () => { await Call(app, "dashboard.get"); }, "An absent source is not rendered as an empty successful dashboard.");
        var before = Data(0, Row("1", person: "Alex"), Row("2", "En attente", "Bob"), Row("4", person: "Alex"));
        var current = Data(1, Row("3", "Résolu", "Bob"), Row("1", "Résolu", "Bob"), Row("2", person: "Alex"));
        var oldPopulation = Population(before, settings); var populations = new AnalyticsPopulationStore(Path.Combine(AppPaths.DataRoot, "site-populations.json")); populations.Save(new[] { oldPopulation });
        var a = snapshots.Save(before, settings, default, Locations(before, settings), new[] { oldPopulation });
        var b = snapshots.Save(current, settings, default, Locations(current, settings), new[] { Population(current, settings, 200) });
        app.LoadFixture(current, Locations(current, settings)); var revision = app.Revision;
        var dashboard = await Call(app, "dashboard.get"); var runId = Str(dashboard, "runId");
        Check(Metric(dashboard, "total").GetProperty("value").GetDouble() == 3 && Widget(dashboard, "filtered").GetProperty("content").GetProperty("value").GetDouble() == 1, "Common and widget metrics use actual independent row populations.");
        Check(Widget(dashboard, "map").GetProperty("content").GetProperty("points").GetArrayLength() == 1, "Offline maps use supplied city/site coordinates and observed matches.");
        var mapKey = Str(Widget(dashboard, "map").GetProperty("content").GetProperty("points")[0], "key");
        var mapRows = await Call(app, "dashboard.rows", new { runId, revision, widgetId = "map", kind = "map", key = mapKey, active = true });
        Check(Int(mapRows, "total") == 1 && mapRows.GetProperty("rows")[0].GetProperty("index").GetInt32() == 2, "An active map selection resolves only active original rows, not all site rows.");
        var thin = JsonSerializer.Serialize(dashboard, PilotApplication.Json); Check(!thin.Contains("scopeRows") && !thin.Contains("\"rows\":[0"), "Dashboard messages omit raw row-index arrays and raw ticket content.");
        var history = await Call(app, "dashboard.history", new { runId, revision });
        var points = Widget(history, "total").GetProperty("history").GetProperty("points");
        Check(points.GetArrayLength() == 2 && points.EnumerateArray().All(p => p.GetProperty("value").GetDouble() == 3), "Dashboard mini-history uses both comparable stored observations.");
        var person = await Call(app, "dashboard.get", new { technicianKey = "Alex" });
        Check(Metric(person, "total").GetProperty("value").GetDouble() == 1, "Personal dashboards resolve identity from the current assignee values.");
        var personHistory = await Call(app, "dashboard.history", new { runId = Str(person, "runId"), revision });
        Check(Widget(personHistory, "total").GetProperty("history").GetProperty("points").EnumerateArray().Select(p => p.GetProperty("value").GetDouble()).SequenceEqual(new[] { 2d, 1d }), "Personal mini-history replays identity through reassignment and reordered rows.");
        var layout = JsonSerializer.Deserialize<DashboardLayoutSettings>((await Call(app, "dashboard.layout.get")).GetRawText(), PilotApplication.Json)!;
        var originalName = layout.Boards[0].Name; layout.Boards[0].Name = "Disposition modifiée";
        await Call(app, "dashboard.layout.save", new { layout }); await Call(app, "dashboard.layout.undo");
        Check(app.Settings.Dashboard.Layout.Boards[0].Name == originalName, "Undo restores the exact persisted dashboard layout.");
        await Call(app, "dashboard.layout.redo"); Check(AppSettings.Load().Dashboard.Layout.Boards[0].Name == "Disposition modifiée", "Redo persists the restored layout through the real settings store.");
        await Reject<InvalidOperationException>(async () => { await Call(app, "dashboard.rows", new { runId, revision }); }, "A changed calculation cannot be used for stale drill-downs.");
        layout.Boards[0].Widgets.Add(layout.Boards[0].Widgets[0].Copy());
        await Reject<InvalidDataException>(async () => { await Call(app, "dashboard.layout.save", new { layout }); }, "Duplicate widget identities are rejected before persistence.");
        Check(app.Settings.Dashboard.Layout.Boards[0].Widgets.Count == 5, "A rejected layout does not mutate the working configuration.");
        var query = new SavedQuery { Name = "Coûts par groupe et statut", Aggregate = "sum", AggregateField = "column:Coût", GroupBy = "role:group", GroupBySecondary = "role:status" };
        var queryPreview = await Call(app, "queries.preview", new { revision, query }); var qid = Str(queryPreview, "runId");
        Check(queryPreview.GetProperty("available").GetBoolean() && queryPreview.GetProperty("metric").GetProperty("value").GetDouble() == 30, "Typed query preview computes the real numeric aggregate without saving it.");
        Check(app.Settings.Queries.Queries.Count == 0, "Query preview leaves the saved library unchanged.");
        var groups = await Call(app, "queries.results", new { revision, runId = qid, hierarchy = true });
        Check(Int(groups, "total") == 3 && groups.GetProperty("rows").EnumerateArray().Any(g => g.GetProperty("path").GetArrayLength() == 1 && g.GetProperty("value").GetDouble() == 30), "Hierarchical subtotals use unique underlying rows.");
        host.SavePath = Path.Combine(_root, "query.csv"); await Call(app, "queries.results.export", new { revision, runId = qid, format = "csv" });
        Check(NativeCsvImportService.Read(host.SavePath, "", null, 1000, false, default).Rows.Count == 3, "Query export writes all source contributors through the real CSV exporter.");
        host.SavePath = Path.Combine(_root, "hierarchy.xlsx"); await Call(app, "queries.results.export", new { revision, runId = qid, hierarchy = true });
        Check(new FileInfo(host.SavePath).Length > 500, "Hierarchical results export to a real XLSX document.");
        await Call(app, "queries.save", new { query }); Check(AppSettings.Load().Queries.Queries.Single().Name == query.Name, "Explicit query save persists the validated DSL definition.");
        var packs = await Call(app, "queries.list"); var packName = Str(packs.GetProperty("packs")[0], "name");
        var pack = await Call(app, "queries.import.preview", new { builtIn = packName });
        Check(app.Settings.Queries.Queries.Count == 1 && Int(pack, "added") > 0, "A built-in pack is a preview until the user commits it.");
        await Call(app, "queries.import.commit", new { previewId = Str(pack, "previewId") });
        await Reject<InvalidOperationException>(async () => { await Call(app, "queries.import.commit", new { previewId = Str(pack, "previewId") }); }, "A consumed import token cannot be replayed.");
        revision = app.Revision;
        var comparison = await Call(app, "history.compare", new { beforeId = a.Id, afterId = b.Id });
        Check(comparison.GetProperty("compatible").GetBoolean() && Int(comparison, "absent") == 1 && Int(comparison, "resolutionsObserved") == 1, "Snapshot comparison distinguishes absence from an observed resolution.");
        var refs = await Call(app, "history.absent.references", new { revision, comparisonId = Str(comparison, "comparisonId") });
        Check(refs.GetProperty("references").GetArrayLength() == 1 && Str(refs.GetProperty("references")[0], "id") == "4", "Absent-ticket verification prepares only safe original references without changing archives.");
        var analysis = await Call(app, "history.analyze", new { snapshotId = b.Id, count = 30, cadenceHours = 24 });
        var movements = await Call(app, "history.analysis.page", new { reportId = Str(analysis, "reportId"), section = "movements" });
        Check(Int(analysis, "comparablePairs") == 1 && Int(movements, "total") == 1, "Longitudinal analysis uses bounded comparable archives through its real engine.");
        var population = Population(current, settings, 200);
        await Call(app, "populations.upsert", new { revision, entry = population, previous = oldPopulation });
        Check(populations.Load().Single().Count == 200, "A dated population edit persists a real denominator with optimistic concurrency.");
        await Reject<InvalidOperationException>(async () => { await Call(app, "populations.upsert", new { revision, entry = oldPopulation, previous = oldPopulation }); }, "Concurrent population edits cannot overwrite a changed reference.");
        var imported = Population(current, settings, 250); var file = Path.Combine(_root, "population-import.json"); AnalyticsPopulationStore.Write(file, new[] { imported }); host.OpenPath = file;
        var popPreview = await Call(app, "populations.import.preview"); Check(populations.Load().Single().Count == 200 && Int(popPreview, "replaced") == 1, "Population import reports replacements before changing the inventory.");
        await Call(app, "populations.import.commit", new { previewId = Str(popPreview, "previewId") }); Check(populations.Load().Single().Count == 250, "The confirmed import replaces only the same dated population identity.");
        var originalArchive = snapshots.Load(a.Id); Check(originalArchive.Populations!.Single().Count == 100, "Inventory changes leave archived population evidence unchanged.");
        await Call(app, "history.open", new { id = a.Id });
        var frozen = await Call(app, "dashboard.get");
        Check(app.IsHistorical && app.Locations?.Matches.Count == 3 && Metric(frozen, "site-per100-people").GetProperty("value").GetDouble() == 3, "Opening an archive restores its own location evidence, rules and denominator.");
        await Reject<InvalidOperationException>(async () => { await Call(app, "history.archive", new { revision = app.Revision }); }, "Historical data cannot silently become a new live archive.");
        host.SavePath = Path.Combine(_root, "archive.csv"); await Call(app, "history.export", new { id = a.Id, format = "csv" });
        Check(NativeCsvImportService.Read(host.SavePath, "", null, 1000, false, default).Rows[0][0] == "1", "Full archive export preserves the original stored row ordering.");
        var frozenRun = Str(frozen, "runId"); host.SavePath = Path.Combine(_root, "report.html"); await Call(app, "reports.export", new { runId = frozenRun, revision = app.Revision, format = "html" });
        var html = File.ReadAllText(host.SavePath); Check(html.Contains("Incident VPN") && html.Contains("2026") && html.Contains("Content-Security-Policy"), "The autonomous report includes real ticket rows, dated provenance and a restrictive content policy.");
        await Reject<NotSupportedException>(async () => { await Call(app, "reports.export", new { runId = frozenRun, revision = app.Revision, format = "pdf" }); }, "A native PDF host failure is returned rather than simulated as success.");
        app.LoadFixture(current, Locations(current, settings)); revision = app.Revision;
        var countBefore = app.Settings.Queries.Queries.Count;
        var plan = await Call(app, "assistant.prepare", new { revision, operation = "query", prompt = "tickets actifs par catégorie", provider = "local" });
        Check(Int(plan, "tickets") == 0 && !Str(plan, "preview").Contains("Incident VPN"), "Query assistance prepares only the schema and no ticket content.");
        var proposal = await Call(app, "assistant.generate", new { revision, preparationId = Str(plan, "preparationId") });
        Check(Str(proposal, "kind") == "query" && app.Settings.Queries.Queries.Count == countBefore, "Local generation returns a validated unsaved DSL draft.");
        var draft = await Call(app, "assistant.draft", new { revision, draftId = Str(proposal, "draftId") });
        Check(!draft.GetProperty("saved").GetBoolean() && Str(draft.GetProperty("query"), "baseProbe") == "active", "Opening the assistant draft does not execute or save it.");
        var summaryPlan = await Call(app, "assistant.prepare", new { revision, operation = "summary", prompt = "synthèse par statut", provider = "local", ticketIds = new[] { "2" }, roles = new[] { "id", "title", "status" } });
        var summary = await Call(app, "assistant.generate", new { revision, preparationId = Str(summaryPlan, "preparationId") });
        Check(summary.GetProperty("claims").EnumerateArray().SelectMany(c => c.GetProperty("citations").EnumerateArray()).All(c => Str(c, "ticketId") == "2" && Int(c, "rowIndex") == 2), "Every local summary citation points to the explicitly supplied original row.");
        var remote = await Call(app, "assistant.prepare", new { revision, operation = "query", prompt = "tickets actifs", provider = "compatible", endpoint = "https://provider.invalid/v1/chat/completions", model = "chosen", externalEnabled = false });
        await Reject<InvalidOperationException>(async () => { await Call(app, "assistant.generate", new { revision, preparationId = Str(remote, "preparationId"), confirmed = true }); }, "An external endpoint cannot be contacted without explicit preparation consent.");
        await Call(app, "assistant.config.save", new { provider = "local", externalEnabled = true, apiKey = "must-not-persist" });
        var profile = File.ReadAllText(Path.Combine(AppPaths.DataRoot, "language-assistant.local.json")); Check(!profile.Contains("must-not-persist") && !profile.Contains("\"ExternalEnabled\":true", StringComparison.OrdinalIgnoreCase), "Session secrets and external activation are not silently persisted.");
        app.LoadFixture(Data(2, Row("8"))); await Reject<InvalidOperationException>(async () => { await Call(app, "assistant.generate", new { revision, preparationId = Str(plan, "preparationId") }); }, "A changed source invalidates prepared assistant payloads.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Reject<OperationCanceledException>(async () => { await Call(app, "dashboard.get", ct: cancel.Token); }, "Cancelled calculations do not publish successful results.");
        var restoreSettings = settings.Copy(); restoreSettings.History.RestoreLastOnStartup = true;
        await using var restored = new PilotApplication(host, (_, _) => { }, restoreSettings, snapshots: snapshots);
        var archivesBefore = snapshots.List().Count; Check(await restored.RestoreLastHistoryOnStartupAsync() && restored.IsHistorical && restored.Result!.Rows[0][0] == "3", "Startup restoration opens the latest faithful archive only when enabled.");
        Check(snapshots.List().Count == archivesBefore && !await restored.RestoreLastHistoryOnStartupAsync(), "Startup restoration neither rewrites archives nor overwrites an existing session.");
        current.ExpectedTotal = 4;
        var context = LanguageAssistantService.BuildContext(current, settings.Dashboard, "query", Array.Empty<int>(), "schema", Array.Empty<string>(), 20);
        Check(!context.Complete && context.Limitations.Any(l => l.Contains("incomplète")), "Assistant provenance does not declare completeness when the announced total disagrees.");
    }
    private static void CheckScopedHistory()
    {
        var settings = Settings(); var store = new SnapshotStore(Path.Combine(_root, "scoped-history"));
        var d0 = Data(0, Row("1"), Row("2", "En attente", "Bob")); var d1 = Data(1, Row("2", person: "Alex"), Row("1", "Résolu", "Bob"));
        store.Save(d0, settings); store.Save(d1, settings);
        var partial = Data(2, Row("2")); partial.Complete = false; store.Save(partial, settings);
        var changed = settings.Copy(); changed.Dashboard.StaleDays++;
        store.Save(Data(3, Row("2")), changed);
        var schema = Data(4, Row("2")); schema.Headers.Add("Nouvelle colonne"); schema.Rows[0] = schema.Rows[0].Append("x").ToArray(); store.Save(schema, settings);
        var last = Data(5, Row("2", "Résolu", "Bob")); store.Save(last, settings);
        var baseline = TechnicianDashboardService.CalculateForTechnician(last, settings.Dashboard, "Alex", last.ExtractedAt);
        var engine = new SnapshotScopedHistory(store, last, settings, baseline, "Alex");
        var scope = new DashboardHistoryScope("active-new", new[] { new DashboardScopeFilter { Kind = "metric", SourceKey = "active" } }, new[] { new DashboardScopeFilter { Kind = "distribution", SourceKey = "status", Value = "Nouveau" } });
        var series = engine.Calculate(new[] { scope })[scope.Key].Single(s => s.Key == "total");
        var real = series.Points.Where(p => p.SnapshotId.Length > 0).ToArray();
        Check(real.Length == 6 && real[0].Value == 1 && real[1].Value == 1, "Historical filters intersect within the person's historical scope rather than current row positions.");
        Check(real.Skip(2).Take(3).All(p => !p.Comparable && p.Value is null), "Partial archives, changed rules and changed schemas remain explicit unknown points.");
        Check(real[^1].Comparable && real[^1].Value == 0, "An observed empty personal scope remains a known zero.");
        var missing = new DashboardHistoryScope("missing", new[] { new DashboardScopeFilter { Kind = "distribution", SourceKey = "removed-probe", Value = "x" } }, Array.Empty<DashboardScopeFilter>());
        Check(engine.Calculate(new[] { missing })[missing.Key].Single(s => s.Key == "total").Points.All(p => !p.Comparable && p.Value is null), "A missing historical filter source never falls back to all tickets.");
        var many = Enumerable.Range(0, 12).Select(i => new DashboardHistoryScope("scope-" + i, new[] { new DashboardScopeFilter { Kind = "distribution", SourceKey = "status", Value = "status-" + i } }, Array.Empty<DashboardScopeFilter>())).ToArray();
        var first = engine.Calculate(many.Take(8).ToArray()); var second = engine.Calculate(many);
        Check(first.Count == 8 && second.Count == 12 && second.Values.All(s => s.Count > 0), "Bounded cache eviction preserves results needed by a mixed cached/new scope request.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { engine.Calculate(new[] { scope }, cancelled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { Check(true, "Historical replay checks cancellation before using its cached data."); }
        var duplicate = Data(6, Row("2"), Row("2")); var duplicateBase = DashboardService.Calculate(duplicate, settings.Dashboard, duplicate.ExtractedAt);
        var duplicateEngine = new SnapshotScopedHistory(store, duplicate, settings, duplicateBase);
        Check(duplicateEngine.Calculate(new[] { new DashboardHistoryScope("all", Array.Empty<DashboardScopeFilter>(), Array.Empty<DashboardScopeFilter>()) })["all"].Single(s => s.Key == "total").Points.All(p => !p.Comparable), "Ambiguous current identifiers disable comparable histories rather than joining repeated tickets.");
        var large = Data(7, Enumerable.Range(1, 40000).Select(i => Row(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        store.Save(large, settings);
        var bounded = new SnapshotScopedHistory(store, large, settings, DashboardService.Calculate(large, settings.Dashboard, large.ExtractedAt));
        var scopes = Enumerable.Range(0, 60).Select(i => new DashboardHistoryScope("budget-" + i, new[] { new DashboardScopeFilter { Kind = "distribution", SourceKey = "status", Value = "requested-" + i } }, Array.Empty<DashboardScopeFilter>())).ToArray();
        var boundedResult = bounded.Calculate(scopes);
        Check(boundedResult.Count == 60 && boundedResult.Values.All(series => series.Single(s => s.Key == "total").Points.Any(p => p.CapturedAt == large.ExtractedAt && p.Value is null && p.Note.Contains("Budget"))), "Historical replay stops before exceeding its cumulative row budget and publishes explicit gaps for every requested scope.");
        try { bounded.Calculate(Enumerable.Range(0, 65).Select(i => new DashboardHistoryScope("too-many-" + i, Array.Empty<DashboardScopeFilter>(), Array.Empty<DashboardScopeFilter>())).ToArray()); throw new Exception("Scope bound ignored."); }
        catch (InvalidDataException) { Check(true, "A request exceeding 64 historical scopes is rejected before reading archives."); }
        var invalidInventory = Path.Combine(_root, "invalid-population.json"); File.WriteAllText(invalidInventory, "{\"version\":99,\"entries\":[]}");
        var unusable = new AnalyticsPopulationStore(invalidInventory).LoadOrEmpty(out var populationWarning);
        Check(unusable.Count == 0 && populationWarning.Contains("indisponible"), "An unsupported population document yields an explicit unavailable-inventory warning rather than corrupting dashboard calculations.");
    }
}
