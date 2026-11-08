using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private static async Task CheckSelectionWeatherRuntime()
    {
        var source = Data(1, Enumerable.Range(0, 104).Select(index => Row((index + 1).ToString(), index >= 100 ? "Clos" : "Nouveau",
            index % 2 == 0 ? "Agent A" : "Agent B", index < 84 || index >= 100 ? "Support" : index < 96 ? "Réseau" : "Autre",
            index % 2 == 0 ? "Incident VPN" : "Demande poste")).ToArray());
        var settings = Settings(); settings.Dashboard.Business.WeatherRules = new()
        {
            new() { Id = "stock", Label = "Stock", OrangeAbove = 100, RedAbove = 200 },
            new() { Id = "support", Label = "Support", Kind = "team", Values = new() { "Support" }, OrangeAbove = 10, RedAbove = 20 },
            new() { Id = "network", Label = "Réseau", Kind = "team", Values = new() { "Réseau" }, OrangeAbove = 5, RedAbove = 10 },
            new() { Id = "other", Label = "Autre", Kind = "team", Values = new() { "Autre" }, OrangeAbove = 3, RedAbove = 6 }
        };
        settings.Queries.Queries.Add(new() { Id = "a817d5e0df9d4a2e9d8c150f4dfbb001", Name = "Recherche locale", Conditions = new() { new() { Field = "role:title", Operator = "contains", Value = "Incident" } } });
        var hero = BusinessDashboardTemplates.WeatherWidgetId("stock"); var host = new AnalyticsTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings, snapshots: new SnapshotStore(Path.Combine(_root, "selection-weather-history")));
        app.LoadFixture(source); app.Settings.Save();
        Task<JsonElement> Get(string[]? focus = null, DashboardScopeFilter[]? filters = null) => Call(app, "dashboard.get",
            new { businessView = "workspace", focusKeys = focus ?? new[] { "team:support", "team:network" }, crossFilters = filters ?? Array.Empty<DashboardScopeFilter>() });
        JsonElement Meta(JsonElement model, string? id = null) => Widget(model, id ?? hero).GetProperty("weatherSelection");
        JsonElement Weather(JsonElement model, string? id = null) => Widget(model, id ?? hero).GetProperty("content").GetProperty("volumeWeather");
        string State(JsonElement model, string? id = null) => Str(Weather(model, id), "state");
        double Count(JsonElement model, string? id = null) => Widget(model, id ?? hero).GetProperty("content").GetProperty("value").GetDouble();
        Task<JsonElement> Save(JsonElement model, double warning, double critical, string mode = "selection", string unit = "count",
            string denominator = "selection-active", bool lower = true, string? id = null) => Call(app, "dashboard.weatherSelection.save",
                new { runId = Str(model, "runId"), revision = app.Revision, widgetId = id ?? hero, mode, unit,
                    denominatorKey = denominator, warningValue = warning, criticalValue = critical, lowerIsBetter = lower });
        Task<JsonElement> Remove(JsonElement model, string mode = "selection") => Call(app, "dashboard.weatherSelection.remove",
            new { runId = Str(model, "runId"), revision = app.Revision, widgetId = hero, mode });

        var model = await Get(); var initialKey = Str(Meta(model), "key");
        Check(Count(model) == 96 && State(model) == "unknown" && !Meta(model).GetProperty("configured").GetBoolean(),
            "A multi-team stock remains neutral until an explicit selection threshold or percentage policy exists.");
        await Save(model, 80, 90); model = await Get();
        Check(Count(model) == 96 && State(model) == "rain" && Meta(model).GetProperty("configured").GetBoolean()
            && Str(model.GetProperty("weatherScope"), "reason").Contains("Seuils enregistrés"),
            "Saving exact-selection counts immediately evaluates the real 96 tickets and updates the scope explanation.");
        var savedBoard = app.Settings.Dashboard.Business.BoardOverrides.Single(board => board.Id == "business-workspace");
        Check(savedBoard.Widgets.Single(widget => widget.Id == hero) is { WarningValue: 100, CriticalValue: 200, SelectionThresholds.Count: 1 }
            && savedBoard.SavedSelection!.FocusKeys.Count == 2,
            "An exception retains original service thresholds and persists its filter definition and selected groups with the board.");
        model = await Get(new[] { "team:network", "team:support" });
        Check(Str(Meta(model), "key") == initialKey && State(model) == "rain" && Meta(model).GetProperty("configured").GetBoolean(),
            "Reversing the group selection retrieves the same exact-selection thresholds.");
        model = await Get(new[] { "group:Réseau", "group:Support" });
        Check(Str(Meta(model), "key") == initialKey && Meta(model).GetProperty("configured").GetBoolean(),
            "Equivalent explicit groups and configured team aliases resolve to one canonical population definition.");
        model = await Get(new[] { "team:support", "team:other" });
        Check(Count(model) == 88 && State(model) == "unknown" && !Meta(model).GetProperty("configured").GetBoolean(),
            "A different group union cannot borrow an exact-selection threshold even within the same widget.");

        var assignee = new DashboardScopeFilter { Kind = "distribution", SourceKey = "assignee", Value = "Agent A" };
        var date = new DashboardScopeFilter { Kind = "date", SourceKey = "created", Value = "2026-09-01", Value2 = "2026-09-30" };
        model = await Get(filters: new[] { assignee, date }); await Save(model, 20, 40); model = await Get(filters: new[] { date, assignee });
        Check(Count(model) == 48 && State(model) == "rain" && Meta(model).GetProperty("configured").GetBoolean(),
            "Conjunctive filter order does not change the saved selection, including dates and a named assignee.");
        var excluded = assignee.Copy(); excluded.Exclude = true;
        model = await Get(filters: new[] { date, excluded });
        Check(Count(model) == 48 && State(model) == "unknown" && !Meta(model).GetProperty("configured").GetBoolean(),
            "An exclusion with the same numeric total remains a different scope and does not recover inclusive thresholds.");
        var otherDate = date.Copy(); otherDate.Value2 = "2026-10-01";
        model = await Get(filters: new[] { assignee, otherDate });
        Check(Count(model) == 48 && !Meta(model).GetProperty("configured").GetBoolean(), "Different date bounds remain distinct even if they currently contain the same tickets.");
        model = await Get(filters: new[] { new DashboardScopeFilter { Kind = "metric", SourceKey = "query:a817d5e0df9d4a2e9d8c150f4dfbb001" } });
        await Save(model, 20, 40); model = await Get(filters: new[] { new DashboardScopeFilter { Kind = "metric", SourceKey = "query:a817d5e0df9d4a2e9d8c150f4dfbb001" } });
        var queryKey = Str(Meta(model), "key");
        var changedQuery = app.Settings.Queries.Queries.Single(query => query.Id == "a817d5e0df9d4a2e9d8c150f4dfbb001").Copy();
        changedQuery.Conditions[0].Operator = "equals"; await Call(app, "queries.save", new { query = changedQuery });
        model = await Get(filters: new[] { new DashboardScopeFilter { Kind = "metric", SourceKey = "query:a817d5e0df9d4a2e9d8c150f4dfbb001" } });
        Check(Str(Meta(model), "key") != queryKey && !Meta(model).GetProperty("configured").GetBoolean(),
            "A query's changed operator changes the scope identity instead of matching only its stable query ID.");
        changedQuery.Conditions[0].Operator = "contains"; await Call(app, "queries.save", new { query = changedQuery });
        var anotherSource = Data(2, source.Rows.ToArray()); anotherSource.Provenance.ScopeFingerprint = "entity-2"; app.LoadFixture(anotherSource);
        model = await Get(); Check(!Meta(model).GetProperty("configured").GetBoolean() && State(model) == "unknown",
            "Another verified GLPI search under the same portal cannot reuse thresholds from a different source perimeter.");
        app.LoadFixture(source); model = await Get(); await Remove(model); model = await Get();
        Check(!Meta(model).GetProperty("configured").GetBoolean() && State(model) == "unknown", "Removing the exact exception restores the conservative no-threshold behavior.");

        await Save(model, 70, 90, "widget", "percent", "source-active"); model = await Get();
        var evaluation = Meta(model).GetProperty("evaluation");
        Check(Count(model) == 96 && State(model) == "rain" && evaluation.GetProperty("evaluatedValue").GetDouble() == 96
            && evaluation.GetProperty("numerator").GetDouble() == 96 && evaluation.GetProperty("denominator").GetDouble() == 100
            && !Meta(model).GetProperty("configured").GetBoolean(),
            "A reusable percentage evaluates 96 / 100 active source tickets while leaving the displayed count at 96.");
        model = await Get(new[] { "team:support", "team:other" });
        Check(Count(model) == 88 && State(model) == "watch" && Meta(model).GetProperty("evaluation").GetProperty("evaluatedValue").GetDouble() == 88,
            "The same explicit percentage policy adapts to another selection without storing another count threshold.");
        model = await Get(new[] { "team:support", "team:other" }, new[] { assignee });
        Check(Count(model) == 44 && State(model) == "sun", "An additional assignee filter recalculates the percentage against the chosen full-source base.");
        await Save(model, 20, 60); model = await Get(new[] { "team:support", "team:other" }, new[] { assignee });
        Check(State(model) == "watch" && Str(Meta(model).GetProperty("evaluation"), "unit") == "count", "An exact count exception takes precedence over the reusable percentage policy.");
        await Remove(model); model = await Get(new[] { "team:support", "team:other" }, new[] { assignee });
        Check(State(model) == "sun" && Str(Meta(model).GetProperty("evaluation"), "unit") == "percent", "Removing an exact exception reveals the retained reusable percentage policy.");
        await Save(model, 44, 60, "widget", "percent", "source-active"); model = await Get(new[] { "team:support", "team:other" }, new[] { assignee });
        Check(State(model) == "sun", "A percentage exactly equal to its warning threshold stays in the previous state, matching count semantics.");
        await Save(model, 60, 30, "widget", "percent", "source-active", false); model = await Get(new[] { "team:support", "team:other" }, new[] { assignee });
        Check(State(model) == "watch", "Lower percentages can be configured as unfavorable without reversing the threshold comparison.");
        await Reject<InvalidDataException>(async () => { await Save(model, 101, 110, "widget", "percent", "source-active"); }, "Percentage thresholds outside 0..100 are rejected.");
        await Reject<InvalidDataException>(async () => { await Save(model, 1, 2, "widget", "count"); }, "Reusable count thresholds cannot silently replace careful filtered-scope behavior.");
        await Save(model, 70, 90, "widget", "percent", "source-active"); model = await Get();
        foreach (var format in new[] { "csv", "xlsx", "html" })
        {
            host.SavePath = Path.Combine(_root, "selection-percent." + format);
            await Call(app, "reports.export", new { runId = Str(model, "runId"), revision = app.Revision, format });
            if (format == "html")
            {
                var html = WebUtility.HtmlDecode(File.ReadAllText(host.SavePath));
                Check(html.Contains("96 %") && html.Contains("> 70 %") && html.Contains("Tickets actifs de la source entière") && html.Contains("96 / 100"),
                    "The HTML report displays the actual percentage, percentage thresholds and its explicit source denominator.");
            }
            else
            {
                var data = format == "csv" ? NativeCsvImportService.Read(host.SavePath, "", null, 10000, false, default) : XlsxImportService.Read(host.SavePath, 10000, default);
                Check(data.Rows.Any(row => row[0] == "Météo de stock · pourcentage" && row[2] == "96" && row[3] == "%" && row[4] == "100" && row[5].Contains("source entière"))
                    && data.Rows.Any(row => row[0] == "Météo de stock" && row[2] == "96" && row[3] == "tickets"),
                    $"The {format.ToUpperInvariant()} export preserves the 96-ticket count and separately exposes 96% of 100 source-active tickets.");
            }
        }
        var uncertain = Data(3, source.Rows.ToArray()); uncertain.Complete = false; app.LoadFixture(uncertain); model = await Get();
        Check(State(model) == "partial", "An incomplete observation never becomes a healthy percentage even when its arithmetic is available.");
        var empty = Data(4, source.Rows.Where(row => row[2] == "Clos").ToArray()); app.LoadFixture(empty); model = await Get(new[] { "team:support" });
        Check(State(model) == "unknown" && Meta(model).GetProperty("evaluation").GetProperty("evaluatedValue").ValueKind == JsonValueKind.Null,
            "A zero active denominator yields an unavailable percentage rather than zero percent or a healthy state.");
        app.LoadFixture(source); model = await Get();
        var outside = BusinessDashboardTemplates.WeatherWidgetId("other");
        Check(Str(Widget(model, outside).GetProperty("content").GetProperty("volumeWeather"), "state") == "unknown"
            && !Meta(model, outside).GetProperty("canEdit").GetBoolean(), "An unrelated team remains outside the perimeter when a global percentage policy is enabled.");

        await Save(model, 80, 95, unit: "percent", denominator: "selection-total"); model = await Get();
        Check(Meta(model).GetProperty("configured").GetBoolean() && Meta(model).GetProperty("evaluation").GetProperty("denominator").GetDouble() == 96
            && State(model) == "rain", "A selection-specific percentage uses all rows retained by the actual group-active filter (96), independently of its source-based fallback policy.");
        var configuration = Path.Combine(_root, "selection-weather-config.json"); ConfigurationTransferService.Export(configuration, app.Settings);
        var imported = ConfigurationTransferService.Import(configuration, Settings()); var portableBoard = imported.Dashboard.Business.BoardOverrides.Single(board => board.Id == "business-workspace");
        Check(portableBoard.Widgets.Single(widget => widget.Id == hero).SelectionThresholds.Count >= 2
            && portableBoard.Widgets.Single(widget => widget.Id == hero).WeatherThresholdPolicy?.Unit == "percent"
            && portableBoard.SavedSelection!.FocusKeys.SequenceEqual(new[] { "team:support", "team:network" }),
            "Portable configuration roundtrips every selection definition, its exceptions, the reusable percentage policy and current filters.");
        var application = Path.Combine(_root, "selection-deployment-app"); var newRoot = Path.Combine(_root, "selection-deployment-new"); Directory.CreateDirectory(application);
        DeploymentConfigurationInitializer.SaveDefault(application, AppPaths.DataRoot, "3.9.0", app.Settings);
        var initialized = DeploymentConfigurationInitializer.TryInitialize(application, newRoot, "3.9.0");
        var seeded = SettingsRecovery.Read(Path.Combine(newRoot, "settings.json"));
        Check(initialized.SeedStatus == "applied" && seeded.Dashboard.Business.BoardOverrides.Single(board => board.Id == "business-workspace")
            .Widgets.Single(widget => widget.Id == hero).WeatherThresholdPolicy?.Unit == "percent", "The actual default-configuration initializer installs selection and percentage settings on a new profile.");
        await using (var restarted = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, imported))
        {
            var remembered = await Call(restarted, "dashboard.selection.get", new { businessView = "workspace", personal = false });
            Check(remembered.GetProperty("selection").GetProperty("focusKeys").GetArrayLength() == 2, "A new runtime restores the configured selection before any source is loaded or local browser storage exists.");
            restarted.LoadFixture(source); var fresh = await Call(restarted, "dashboard.get", new { businessView = "workspace", focusKeys = new[] { "team:network", "team:support" } });
            Check(Meta(fresh).GetProperty("configured").GetBoolean() && State(fresh) == "rain", "A restarted runtime uses the saved thresholds with the original source and reversed selection order.");
        }
        var copied = imported.Copy(); copied.Dashboard.Business.BoardOverrides.Single(board => board.Id == "business-workspace").Widgets.Single(widget => widget.Id == hero)
            .SelectionThresholds[0].Scope.CommonFilters.Clear();
        Check(imported.Dashboard.Business.BoardOverrides.Single(board => board.Id == "business-workspace").Widgets.Single(widget => widget.Id == hero)
            .SelectionThresholds[0].Scope.CommonFilters.Count > 0, "Copies deeply isolate saved scope filters rather than sharing mutable lists.");
        await Call(app, "dashboard.selection.remember", new { businessView = "workspace", selection = new { focusKeys = new[] { "team:support" }, technicianKey = "Agent A", crossFilters = new[] { date } } });
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var noChange = await Call(app, "dashboard.selection.remember", new { businessView = "workspace", selection = new { focusKeys = new[] { "team:support" }, technicianKey = "Agent A", crossFilters = new[] { date } } });
        Check(!noChange.GetProperty("saved").GetBoolean() && before.SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile)), "Remembering the same selection is a no-op and does not rewrite the profile.");
        await Call(app, "app.demo"); var simulated = await Call(app, "dashboard.get", new { businessView = "workspace" });
        var simulatedHero = simulated.GetProperty("widgets").EnumerateArray().First(widget => Str(widget.GetProperty("settings"), "kind") == "metric"
            && Str(widget.GetProperty("settings"), "presentation") == "weather-volume");
        await Call(app, "dashboard.weatherSelection.save", new { runId = Str(simulated, "runId"), revision = simulated.GetProperty("revision").GetInt64(),
            widgetId = Str(simulatedHero, "id"), mode = "widget", unit = "percent", denominatorKey = "source-active", warningValue = 5, criticalValue = 10, lowerIsBetter = true });
        await Call(app, "records.test.exit");
        Check(before.SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile)), "Changing percentage thresholds in the full fictitious demo never writes the normal profile.");
        await CheckWidgetSelectionIdentity(settings, source);
        await CheckGeographicSelectionIdentity();
        await CheckPreparedWeatherBases(settings, source);
    }

    private static async Task CheckGeographicSelectionIdentity()
    {
        var settings = Settings(); var board = DashboardLayoutSettings.CreateBoard("Sélections géographiques", "empty");
        var query = new SavedQuery { Name = "Ville reconnue", Conditions = new() { new() { Field = "role:city", Operator = "equals", Value = "Lyon" } } };
        settings.Queries.Queries.Add(query);
        foreach (var (id, filter) in new[]
        {
            ("city", new DashboardScopeFilter { Kind = "metric", SourceKey = "city-matched" }),
            ("query", new DashboardScopeFilter { Kind = "metric", SourceKey = "query:" + query.Id }),
            ("group", new DashboardScopeFilter { Kind = "matrix-row", SourceKey = "group-status", Value = "Support" })
        })
            board.Widgets.Add(new() { Id = id, Kind = "metric", SourceKey = "active", Presentation = "weather-volume", Filters = new() { filter } });
        settings.Dashboard.Layout = new() { ActiveBoardId = board.Id, Boards = new() { board } };
        var source = Data(1, Row("1", title: "Lyon VPN"), Row("2", title: "Lyon VPN"), Row("3", title: "Incident Paris"), Row("4", title: "Incident Paris"));
        var places = CityDetectionService.Enrich(source, settings.Cities, settings.Dashboard);
        query.Conditions[0].Field = "column:" + places.CityColumnName;
        await using var app = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, settings); app.LoadFixture(places.Data, places);
        Task<JsonElement> Get() => Call(app, "dashboard.get", new { boardId = board.Id });
        JsonElement Meta(JsonElement model, string id) => Widget(model, id).GetProperty("weatherSelection");
        var model = await Get();
        foreach (var id in new[] { "city", "query", "group" })
        {
            await Call(app, "dashboard.weatherSelection.save", new { runId = Str(model, "runId"), revision = app.Revision, widgetId = id,
                mode = "selection", unit = "count", warningValue = 0, criticalValue = 1 });
            model = await Get();
        }
        var oldCity = Str(Meta(model, "city"), "key"); var oldQuery = Str(Meta(model, "query"), "key"); var oldGroup = Str(Meta(model, "group"), "key");
        var state = await Call(app, "terrain.reference.state"); var changedCity = app.Settings.Cities.Cities.Single().Copy(); changedCity.Aliases.Add("Lyonnais");
        state = await Call(app, "terrain.reference.upsert", new { draftVersion = Int(state, "draftVersion"), kind = "city", entry = changedCity });
        var preview = await Call(app, "terrain.reference.preview", new { revision = app.Revision, draftVersion = Int(state, "draftVersion") });
        await Call(app, "terrain.reference.apply", new { token = Str(preview, "token"), confirmed = true }); model = await Get();
        Check(Str(Meta(model, "city"), "key") != oldCity && !Meta(model, "city").GetProperty("configured").GetBoolean()
            && Widget(model, "city").GetProperty("content").GetProperty("value").GetDouble() == 2,
            "A geographical reference changed through the real preview/apply handlers invalidates the city exception even when the same two tickets still match.");
        Check(Str(Meta(model, "query"), "key") != oldQuery && !Meta(model, "query").GetProperty("configured").GetBoolean(),
            "A saved query over location fields also depends on the reference interpretation rather than only its unchanged query definition.");
        Check(Str(Meta(model, "group"), "key") == oldGroup && Meta(model, "group").GetProperty("configured").GetBoolean(),
            "An unrelated city-alias update leaves a native team-only selection and its thresholds intact.");
    }

    private static async Task CheckWidgetSelectionIdentity(AppSettings original, ExtractionResult source)
    {
        var settings = original.Copy(); var board = DashboardLayoutSettings.CreateBoard("Filtres météo", "empty");
        board.Widgets.Add(new() { Id = "selection-weather", Kind = "metric", SourceKey = "active", Presentation = "weather-volume",
            Filters = new() { new() { Kind = "distribution", SourceKey = "assignee", Value = "Agent A" },
                new() { Kind = "date", SourceKey = "created", Value = "2026-09-01", Value2 = "2026-09-30" } } });
        settings.Dashboard.Layout = new() { ActiveBoardId = board.Id, Boards = new() { board } };
        await using var app = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, settings); app.LoadFixture(source);
        Task<JsonElement> Get() => Call(app, "dashboard.get", new { boardId = board.Id });
        JsonElement Meta(JsonElement model) => Widget(model, "selection-weather").GetProperty("weatherSelection");
        var model = await Get();
        await Call(app, "dashboard.weatherSelection.save", new { runId = Str(model, "runId"), revision = app.Revision, widgetId = "selection-weather",
            mode = "selection", unit = "count", warningValue = 40, criticalValue = 60 });
        model = await Get(); var savedKey = Str(Meta(model), "key");
        var layout = app.Settings.Dashboard.Layout.Copy(); layout.Boards[0].Widgets[0].Filters.Reverse();
        await Call(app, "dashboard.layout.save", new { layout }); model = await Get();
        Check(Str(Meta(model), "key") == savedKey && Meta(model).GetProperty("configured").GetBoolean(),
            "Reordering filters inside a widget preserves its exact-selection exception.");
        layout = app.Settings.Dashboard.Layout.Copy(); var person = layout.Boards[0].Widgets[0].Filters.Single(filter => filter.SourceKey == "assignee");
        person.Value = "Agent B"; person.Exclude = true; await Call(app, "dashboard.layout.save", new { layout }); model = await Get();
        Check(Str(Meta(model), "key") != savedKey && !Meta(model).GetProperty("configured").GetBoolean()
            && Widget(model, "selection-weather").GetProperty("content").GetProperty("value").GetDouble() == 50,
            "A changed widget operator stays distinct even when excluding B currently returns the same 50 rows as including A.");
        await Call(app, "dashboard.weatherSelection.save", new { runId = Str(model, "runId"), revision = app.Revision, widgetId = "selection-weather",
            mode = "widget", unit = "percent", denominatorKey = "selection-active", warningValue = 40, criticalValue = 60 });
        model = await Get();
        Check(Meta(model).GetProperty("evaluation").GetProperty("evaluatedValue").GetDouble() == 50
            && Meta(model).GetProperty("evaluation").GetProperty("denominator").GetDouble() == 100,
            "A selection-active percentage uses the 100-ticket population before widget filters, not the widget's own 50-ticket count.");
        layout = app.Settings.Dashboard.Layout.Copy(); layout.Boards[0].Widgets[0].WeatherThresholdPolicy = null;
        layout.Boards[0].Widgets[0].SourceKey = "pending"; await Call(app, "dashboard.layout.save", new { layout }); model = await Get();
        Check(!Meta(model).GetProperty("configured").GetBoolean(), "Changing a widget's metric cannot reuse saved thresholds for its former indicator.");
    }

    private static async Task CheckPreparedWeatherBases(AppSettings original, ExtractionResult source)
    {
        var settings = original.Copy(); var board = DashboardLayoutSettings.CreateBoard("Bases météo", "empty");
        foreach (var key in new[] { "active", "pending", "high", "major", "old", "stale", "overdue", "soon", "age-median", "completed-share", "total" })
            board.Widgets.Add(new() { Id = key, Kind = "metric", SourceKey = key, Presentation = "weather-volume", ShowMiniHistory = false });
        settings.Dashboard.Layout = new() { ActiveBoardId = board.Id, Boards = new() { board } };
        var data = Data(1, source.Rows.Select(row => row.ToArray()).ToArray());
        data.Rows[0][10] = "priorité inconnue"; data.Rows[1][5] = "date inconnue"; data.Rows[2][6] = "date inconnue";
        await using var app = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, settings); app.LoadFixture(data);
        Task<JsonElement> Get() => Call(app, "dashboard.get", new { boardId = board.Id });
        JsonElement Meta(JsonElement model, string id) => Widget(model, id).GetProperty("weatherSelection");
        var model = await Get();
        Check(Str(Meta(model, "pending"), "recommendedDenominatorKey") == "selection-active"
            && new[] { "high", "major", "old", "stale", "overdue", "soon" }.All(key => Str(Meta(model, key), "recommendedDenominatorKey") == "selection-sample"),
            "Prepared bases recommend active tickets for pending counts and the actual eligible populations for priority and date indicators.");
        Check(!Meta(model, "age-median").GetProperty("percentageAvailable").GetBoolean()
            && !Meta(model, "completed-share").GetProperty("percentageAvailable").GetBoolean(),
            "Duration metrics and already-calculated percentages are not proposed as ticket-count percentages.");
        async Task Save(JsonElement current, string id, string denominator) => _ = await Call(app, "dashboard.weatherSelection.save",
            new { runId = Str(current, "runId"), revision = app.Revision, widgetId = id, mode = "widget", unit = "percent",
                denominatorKey = denominator, warningValue = 40, criticalValue = 80, lowerIsBetter = true });
        await Save(model, "high", "selection-sample"); model = await Get();
        Check(Meta(model, "high").GetProperty("evaluation").GetProperty("denominator").GetDouble() == 99
            && Meta(model, "high").GetProperty("evaluation").GetProperty("evaluatedValue").GetDouble() == 100
            && Str(Meta(model, "high").GetProperty("evaluation"), "denominatorLabel").Contains("priorité"),
            "The prepared high-priority base excludes an unrecognized priority: 99 high tickets / 99 eligible active tickets.");
        await Save(model, "old", "selection-sample"); model = await Get();
        Check(Meta(model, "old").GetProperty("evaluation").GetProperty("denominator").GetDouble() == 99,
            "The prepared age base excludes the active ticket whose creation date is unreadable.");
        await Save(model, "stale", "selection-sample"); model = await Get();
        Check(Meta(model, "stale").GetProperty("evaluation").GetProperty("denominator").GetDouble() == 99,
            "The prepared inactivity base excludes the active ticket whose update date is unreadable.");
        await Save(model, "total", "selection-active"); model = await Get();
        Check(Str(Widget(model, "total").GetProperty("content").GetProperty("volumeWeather"), "state") == "unknown"
            && Meta(model, "total").GetProperty("evaluation").GetProperty("evaluatedValue").ValueKind == JsonValueKind.Null,
            "A total including closed tickets cannot be divided by active-only tickets to fabricate a percentage above 100.");
        var withoutDue = Data(2, data.Rows.Select(row => row.Where((_, index) => index != 7).ToArray()).ToArray());
        withoutDue.Headers = data.Headers.Where((_, index) => index != 7).ToList(); app.LoadFixture(withoutDue); model = await Get();
        await Save(model, "overdue", "selection-sample"); model = await Get();
        Check(Str(Widget(model, "overdue").GetProperty("content").GetProperty("volumeWeather"), "state") == "unknown"
            && !Meta(model, "overdue").GetProperty("denominators").EnumerateArray().Single(item => Str(item, "key") == "selection-sample").GetProperty("available").GetBoolean(),
            "An absent due-date column leaves its prepared base unavailable; no other progress or duration column is substituted.");
    }
}
