using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private static async Task CheckWeatherScopeRuntime()
    {
        var source = Data(1, Enumerable.Range(0, 104).Select(index => Row((index + 1).ToString(),
            index >= 100 ? "Clos" : "Nouveau", index < 42 ? "Agent A" : "Agent B",
            index < 6 ? "Support;Réseau" : index < 84 || index >= 100 ? "Support" : index < 96 ? "Réseau" : "Autre",
            "Ticket fictif " + (index + 1))).ToArray());
        var settings = Settings();
        settings.Dashboard.Business.WeatherRules = new()
        {
            new() { Id = "stock", Label = "Stock du service", OrangeAbove = 100, RedAbove = 200 },
            new() { Id = "support", Label = "Équipe Support", Kind = "team", Values = new() { "Support" }, OrangeAbove = 10, RedAbove = 20 },
            new() { Id = "network", Label = "Équipe Réseau", Kind = "team", Values = new() { "Réseau" }, OrangeAbove = 5, RedAbove = 10 },
            new() { Id = "other", Label = "Équipe Autre", Kind = "team", Values = new() { "Autre" }, OrangeAbove = 3, RedAbove = 6 },
            new() { Id = "empty", Label = "Équipe Vide", Kind = "team", Values = new() { "Vide" }, OrangeAbove = 3, RedAbove = 6 }
        };
        var heroId = BusinessDashboardTemplates.WeatherWidgetId("stock");
        var supportId = BusinessDashboardTemplates.WeatherWidgetId("support");
        var networkId = BusinessDashboardTemplates.WeatherWidgetId("network");
        var otherId = BusinessDashboardTemplates.WeatherWidgetId("other");
        var native = BusinessDashboardTemplates.Create("workspace", settings.Dashboard, BusinessDashboardTemplates.Observe(source, settings.Dashboard));
        var originalHero = native.Widgets.Single(widget => widget.Id == heroId);
        originalHero.Title = "Mon volume personnalisé"; originalHero.Height = 420; originalHero.TargetValue = 300;
        settings.Dashboard.Business.BoardOverrides.Add(native);
        var host = new AnalyticsTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings, snapshots: new SnapshotStore(Path.Combine(_root, "weather-scope-history")));
        app.LoadFixture(source); app.Settings.Save();
        var stored = JsonSerializer.Serialize(app.Settings, PilotApplication.Json);
        var profileBytes = File.ReadAllBytes(AppPaths.SettingsFile);
        JsonElement Card(JsonElement model, string id) => Widget(model, id);
        JsonElement Effective(JsonElement model, string id) => Card(model, id).GetProperty("settings");
        JsonElement Context(JsonElement model, string id) => Card(model, id).GetProperty("weatherContext");
        JsonElement Weather(JsonElement model, string id) => Card(model, id).GetProperty("content").GetProperty("volumeWeather");
        string State(JsonElement model, string id) => Str(Weather(model, id), "state");
        double Count(JsonElement model, string id) => Card(model, id).GetProperty("content").GetProperty("value").GetDouble();
        bool Missing(JsonElement model, string id, string property) => Effective(model, id).GetProperty(property).ValueKind == JsonValueKind.Null;
        JsonElement Raw(JsonElement model, string id) => model.GetProperty("layout").GetProperty("boards")[0].GetProperty("widgets").EnumerateArray().Single(widget => Str(widget, "id") == id);
        Task<JsonElement> Scope(params string[] keys) => Call(app, "dashboard.get", new { businessView = "workspace", focusKeys = keys });
        async Task<int[]> Rows(JsonElement model, string id)
        {
            var rows = await Call(app, "dashboard.rows", new { runId = Str(model, "runId"), revision = app.Revision, widgetId = id, kind = "metric", key = "active", limit = 100 });
            return rows.GetProperty("rows").EnumerateArray().Select(row => Int(row, "index")).ToArray();
        }
        var model = await Scope("team:support");
        Check(Str(model.GetProperty("weatherScope"), "mode") == "team" && Str(model.GetProperty("weatherScope"), "primaryRuleId") == "support"
            && Count(model, heroId) == 84 && State(model, heroId) == "rain"
            && Effective(model, heroId).GetProperty("warningValue").GetDouble() == 10 && Effective(model, heroId).GetProperty("criticalValue").GetDouble() == 20,
            "An exact 84-ticket team uses its own 10/20 stock weather in the real native workspace response.");
        Check(Context(model, supportId).GetProperty("representedByHero").GetBoolean()
            && !Context(model, networkId).GetProperty("applicable").GetBoolean() && State(model, networkId) == "unknown"
            && !Context(model, otherId).GetProperty("applicable").GetBoolean() && Missing(model, heroId, "targetValue"),
            "The identical team card is represented once, outside teams are neutral, and the service target is not reused for a team.");
        Check(Raw(model, heroId).GetProperty("warningValue").GetDouble() == 100 && Raw(model, heroId).GetProperty("criticalValue").GetDouble() == 200
            && Str(Raw(model, heroId), "title") == "Mon volume personnalisé" && Int(Effective(model, heroId), "height") == 420,
            "The returned editable layout retains service thresholds and the user's title and geometry while effective widget settings are projected.");
        Check((await Rows(model, heroId)).SequenceEqual(Enumerable.Range(0, 84)),
            "The hero drilldown retains exactly the original 84 active source rows without closed or outside tickets.");
        foreach (var keys in new[] { new[] { "group:Support" }, new[] { "team:support", "group:Support" } })
        {
            var equivalent = await Scope(keys);
            Check(Str(equivalent.GetProperty("weatherScope"), "mode") == "team" && State(equivalent, heroId) == "rain" && Count(equivalent, heroId) == 84,
                "Equivalent team/group selections preserve one exact weather population: " + string.Join(" + ", keys) + ".");
        }
        var multiple = await Scope("team:support", "team:network");
        Check(Str(multiple.GetProperty("weatherScope"), "mode") == "multiple" && Count(multiple, heroId) == 96 && State(multiple, heroId) == "unknown"
            && Missing(multiple, heroId, "warningValue") && Missing(multiple, heroId, "criticalValue") && Missing(multiple, heroId, "targetValue")
            && (await Rows(multiple, heroId)).SequenceEqual(Enumerable.Range(0, 96)),
            "The cumulative total is the 96-row union and carries no global, summed or capacity threshold.");
        Check(Count(multiple, supportId) == 84 && State(multiple, supportId) == "rain" && Count(multiple, networkId) == 12 && State(multiple, networkId) == "rain"
            && !Context(multiple, supportId).GetProperty("representedByHero").GetBoolean() && !Context(multiple, otherId).GetProperty("applicable").GetBoolean(),
            "Each complete selected team keeps its own weather and first-team precedence independently from the neutral cumulative total.");
        var overlap = await Scope("team:network");
        Check(Count(overlap, heroId) == 18 && State(overlap, heroId) == "unknown" && Count(overlap, networkId) == 12 && State(overlap, networkId) == "rain"
            && !Context(overlap, networkId).GetProperty("representedByHero").GetBoolean()
            && Str(overlap.GetProperty("weatherScope"), "reason").Contains("18") && Str(overlap.GetProperty("weatherScope"), "reason").Contains("12"),
            "An overlapping group explains its 18 selected rows versus 12 priority-rule rows instead of applying an unrelated team color to the hero.");
        var person = await Call(app, "dashboard.get", new { businessView = "workspace", focusKeys = new[] { "team:support" }, technicianKey = TechnicianDashboardService.IdentityKey("Agent A") });
        Check(Count(person, heroId) == 42 && State(person, heroId) == "unknown" && State(person, supportId) == "unknown"
            && Missing(person, supportId, "warningValue") && Missing(person, heroId, "targetValue"),
            "A technician subset keeps its 42 tickets but neutralizes both service and entire-team threshold comparisons.");
        var cross = await Call(app, "dashboard.get", new { businessView = "workspace", focusKeys = new[] { "team:support" },
            crossFilters = new[] { new DashboardScopeFilter { Kind = "distribution", SourceKey = "assignee", Value = "Agent A" } } });
        Check(Count(cross, heroId) == 42 && State(cross, heroId) == "unknown" && State(cross, supportId) == "unknown",
            "An additional cross-filter cannot make the remaining part of a team appear healthy against whole-team thresholds.");
        var legacy = await Call(app, "dashboard.get", new { businessView = "weather", focusKey = "group:Support" });
        Check(Count(legacy, heroId) == 84 && State(legacy, heroId) == "rain" && Context(legacy, supportId).GetProperty("representedByHero").GetBoolean(),
            "The compatible weather route and single focus key use the same exact-team projection as the unified workspace.");

        model = await Scope("team:support");
        string[]? csvRows = null;
        foreach (var format in new[] { "csv", "xlsx" })
        {
            host.SavePath = Path.Combine(_root, "weather-scope." + format);
            await Call(app, "reports.export", new { runId = Str(model, "runId"), revision = app.Revision, format });
            var exported = format == "csv" ? NativeCsvImportService.Read(host.SavePath, "", null, 10000, false, default) : XlsxImportService.Read(host.SavePath, 10000, default);
            var stock = exported.Rows.Where(row => row[0] == "Météo de stock").ToArray();
            var serialized = stock.Select(row => string.Join('\t', row)).ToArray();
            Check(stock.Length == 1 && stock[0][1] == "Équipe Support · tickets en cours" && stock[0][2] == "84" && stock[0][4] == "Sous tension"
                && stock[0][5].Contains("10") && stock[0][5].Contains("20") && (csvRows is null || csvRows.SequenceEqual(serialized))
                && exported.Rows.Any(row => row[0].StartsWith("Indice pondéré", StringComparison.Ordinal)),
                $"The {format.ToUpperInvariant()} report exports the exact 84-ticket red stock weather and its 10/20 method once, distinct from the weighted index.");
            csvRows = serialized;
        }
        host.SavePath = Path.Combine(_root, "weather-scope.html");
        await Call(app, "reports.export", new { runId = Str(model, "runId"), revision = app.Revision, format = "html" });
        var html = WebUtility.HtmlDecode(File.ReadAllText(host.SavePath));
        var stockStart = html.IndexOf("id=\"stock-weather\"", StringComparison.Ordinal);
        var stockEnd = html.IndexOf("id=\"metrics\"", stockStart, StringComparison.Ordinal);
        var stockHtml = html[stockStart..stockEnd];
        Check(stockHtml.Contains("Équipe Support · tickets en cours") && stockHtml.Contains("Sous tension") && stockHtml.Contains("> 10") && stockHtml.Contains("> 20")
            && !stockHtml.Contains("Équipe Réseau") && !stockHtml.Contains("Équipe Autre")
            && Enumerable.Range(1, 84).All(index => html.Contains($"id=\"ticket-row-{index}\"", StringComparison.Ordinal))
            && Enumerable.Range(85, 20).All(index => !html.Contains($"id=\"ticket-row-{index}\"", StringComparison.Ordinal)),
            "The HTML report matches the projected stock weather, omits outside/duplicate team cards and links exactly the original selected rows.");
        multiple = await Scope("team:support", "team:network");
        host.SavePath = Path.Combine(_root, "weather-multiple.csv");
        await Call(app, "reports.export", new { runId = Str(multiple, "runId"), revision = app.Revision, format = "csv" });
        var multipleStock = NativeCsvImportService.Read(host.SavePath, "", null, 10000, false, default).Rows.Where(row => row[0] == "Météo de stock").ToArray();
        Check(multipleStock.Length == 3 && multipleStock[0][2] == "96" && multipleStock[0][4].Contains("seuil global non défini")
            && multipleStock[1][2] == "84" && multipleStock[1][4] == "Sous tension" && multipleStock[2][2] == "12" && multipleStock[2][4] == "Sous tension",
            "A cumulative export retains a neutral 96-ticket total plus the independently evaluated 84- and 12-ticket teams.");
        var all = await Scope();
        Check(Str(all.GetProperty("weatherScope"), "mode") == "service" && State(all, heroId) == "sun" && Count(all, heroId) == 100
            && Effective(all, heroId).GetProperty("warningValue").GetDouble() == 100 && Effective(all, heroId).GetProperty("targetValue").GetDouble() == 300
            && JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == stored && profileBytes.SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile)),
            "Returning to all teams restores the original service weather and target without any memory or profile mutation from filters or exports.");
        var uncertain = Data(1, source.Rows.Concat(new[] { Row("105", "État inconnu", "Agent C", "Autre") }).ToArray());
        app.LoadFixture(uncertain); var uncertainTeam = await Scope("team:support");
        Check(Count(uncertainTeam, heroId) == 84 && State(uncertainTeam, heroId) == "partial",
            "A selected team's apparently complete rows do not hide an unknown status elsewhere in the source observation.");
        app.LoadFixture(source); host.OpenPath = Path.Combine(_root, "weather-simulation.csv"); ExportService.WriteCsv(source, host.OpenPath);
        await Call(app, "records.test.import"); var simulated = await Scope("team:support");
        Check(app.IsTestMode && simulated.GetProperty("simulation").GetBoolean() && Count(simulated, heroId) == 84 && State(simulated, heroId) == "rain"
            && Str(Weather(simulated, heroId), "title").StartsWith("Simulation", StringComparison.Ordinal),
            "The real local CSV simulation uses the same team thresholds while explicitly identifying the simulated stock weather.");
        await Call(app, "records.test.exit");
        Check(!app.IsTestMode && ReferenceEquals(app.Result, source) && JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == stored
            && profileBytes.SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile)),
            "Leaving the scoped CSV simulation restores the original source and every persisted weather/layout setting.");

        var customSettings = Settings();
        customSettings.Dashboard.Business.WeatherRules = settings.Dashboard.Business.WeatherRules.Select(rule => rule.Copy()).ToList();
        var customBoard = DashboardLayoutSettings.CreateBoard("Périmètre enregistré", "empty");
        customBoard.Filters.Add(new() { Kind = "matrix-row", SourceKey = "group-status", Values = new() { "Support" } });
        customBoard.Widgets.Add(new() { Id = "custom-weather", Kind = "metric", SourceKey = "active", Title = "Seuils locaux explicites", Presentation = "weather-volume",
            WarningValue = 70, CriticalValue = 90, TargetValue = 120, ShowMiniHistory = false });
        customSettings.Dashboard.Layout = new() { ActiveBoardId = customBoard.Id, Boards = new() { customBoard } };
        customSettings.Dashboard.PersonalLayout = customSettings.Dashboard.Layout.Copy();
        await using var custom = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, customSettings);
        custom.LoadFixture(source); var customBefore = JsonSerializer.Serialize(custom.Settings, PilotApplication.Json);
        var local = await Call(custom, "dashboard.get", new { boardId = customBoard.Id });
        Check(Count(local, "custom-weather") == 84 && State(local, "custom-weather") == "watch" && local.GetProperty("weatherScope").ValueKind == JsonValueKind.Null
            && Effective(local, "custom-weather").GetProperty("warningValue").GetDouble() == 70,
            "A saved custom composition retains explicit 70/90 thresholds for its persisted Support filter when no transient selection is added.");
        foreach (var arguments in new object[] { new { boardId = customBoard.Id, focusKeys = new[] { "team:support" } },
            new { boardId = customBoard.Id, crossFilters = new[] { new DashboardScopeFilter { Kind = "distribution", SourceKey = "assignee", Value = "Agent A" } } },
            new { boardId = customBoard.Id, technicianKey = TechnicianDashboardService.IdentityKey("Agent A") } })
        {
            var transient = await Call(custom, "dashboard.get", arguments);
            Check(State(transient, "custom-weather") == "unknown" && Missing(transient, "custom-weather", "warningValue") && Missing(transient, "custom-weather", "targetValue")
                && Raw(transient, "custom-weather").GetProperty("warningValue").GetDouble() == 70 && Raw(transient, "custom-weather").GetProperty("criticalValue").GetDouble() == 90,
                "A transient custom-board focus/cross/technician filter neutralizes weather without replacing custom thresholds with team defaults.");
        }
        Check(JsonSerializer.Serialize(custom.Settings, PilotApplication.Json) == customBefore,
            "Custom composition projection never changes its persisted filters, targets or weather thresholds.");
    }
}
