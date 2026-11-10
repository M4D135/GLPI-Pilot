using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private static async Task CheckUnavailableWidgetFilters()
    {
        var settings = Settings();
        settings.Dashboard.Business.WeatherRules = new()
        {
            new() { Id = "stock", Label = "Stock du service", OrangeAbove = 10, RedAbove = 20 },
            new() { Id = "support", Label = "Équipe Support", Kind = "team", Values = new() { "Support" }, OrangeAbove = 1, RedAbove = 2 }
        };
        var teamId = BusinessDashboardTemplates.WeatherWidgetId("support");
        var heroId = BusinessDashboardTemplates.WeatherWidgetId("stock");
        var source = Data(0, Row("1"), Row("2", group: "Autre"), Row("3", "Clos"));
        var snapshots = new SnapshotStore(Path.Combine(_root, "unavailable-filter-history"));
        snapshots.Save(source, settings, default);
        var native = BusinessDashboardTemplates.Create("workspace", settings.Dashboard, BusinessDashboardTemplates.Observe(source, settings.Dashboard));
        native.Widgets.Single(widget => widget.Id == teamId).ShowMiniHistory = true;
        settings.Dashboard.Business.BoardOverrides.Add(native);
        var board = settings.Dashboard.Layout.Boards[0]; board.Widgets.Clear();
        board.Widgets.Add(new() { Id = "valid", Kind = "metric", SourceKey = "total", ShowMiniHistory = false });
        var groupFilter = new DashboardScopeFilter { Kind = "matrix-row", SourceKey = "group-status", Value = "Support" };
        foreach (var kind in new[] { "metric", "history", "map", "note" })
            board.Widgets.Add(new() { Id = "missing-" + kind, Kind = kind, SourceKey = kind == "map" ? "active" : kind == "note" ? "" : "total",
                Filters = new() { groupFilter.Copy() }, ShowMiniHistory = true, Note = "Note à périmètre inconnu" });
        var host = new AnalyticsTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings, snapshots: snapshots);
        var saved = JsonSerializer.Serialize(app.Settings.Dashboard, PilotApplication.Json);
        foreach (var variant in new[] { "missing-group", "ambiguous-group", "missing-status", "unknown-status" })
        {
            var data = Data(1, Row("1"), Row("2", group: "Autre"), Row("3", "Clos"));
            if (variant == "missing-group") data.Headers[4] = "Champ sans correspondance";
            if (variant == "ambiguous-group") data.Headers[9] = "Groupe attribué";
            if (variant == "missing-status") data.Headers[2] = "État non mappé";
            if (variant == "unknown-status") foreach (var row in data.Rows) row[2] = "Statut non reconnu";
            app.LoadFixture(data);
            var model = await Call(app, "dashboard.get", new { businessView = "workspace" });
            Check(Metric(model, "total").GetProperty("value").GetDouble() == 3
                && model.GetProperty("widgets").GetArrayLength() > 4, variant + ": Pilotage remains available with the actual source total.");
            var card = Widget(model, teamId);
            Check(!card.GetProperty("available").GetBoolean() && card.GetProperty("content").ValueKind == JsonValueKind.Null
                && card.GetProperty("rowCount").ValueKind == JsonValueKind.Null && Str(card, "unavailableReason").Contains("Groupes × statuts"),
                variant + ": an unknown team population is unavailable, never zero or the whole service.");
            if (variant.EndsWith("group", StringComparison.Ordinal))
                Check(Widget(model, heroId).GetProperty("content").GetProperty("value").GetDouble() == 2,
                    variant + ": the independent service metric retains its two active tickets.");
            var runId = Str(model, "runId"); var revision = app.Revision;
            foreach (var operation in new[] { "dashboard.rows", "dashboard.copyIds", "dashboard.data" })
                await Reject<InvalidDataException>(async () => { await Call(app, operation, new { runId, revision, widgetId = teamId, kind = "metric", key = "active" }); },
                    variant + ": " + operation + " cannot substitute rows from another population.");
            var history = await Call(app, "dashboard.history", new { runId, revision });
            Check(Widget(history, teamId).GetProperty("history").ValueKind == JsonValueKind.Null,
                variant + ": a formerly valid archived team does not supply a current unknown population.");
            var preview = await Call(app, "reports.preview", new { runId, revision, selectedWidgetIds = new[] { heroId, teamId } });
            var section = preview.GetProperty("widgets")[1];
            Check(section.GetProperty("tables").GetArrayLength() == 0 && section.GetProperty("chart").GetArrayLength() == 0
                && Str(section, "scope") == "Périmètre indisponible" && section.GetProperty("notes")[0].GetString()!.Contains("Groupes × statuts"),
                variant + ": the report keeps the selected unavailable card and its explanation without invented figures.");
            host.SavePath = Path.Combine(_root, variant + "-meeting.csv");
            await Call(app, "reports.export", new { runId, revision, format = "csv", selectedWidgetIds = new[] { teamId } });
            Check(File.ReadAllText(host.SavePath).Contains("Périmètre indisponible"),
                variant + ": the exported file retains the unknown scope explicitly.");
            host.SavePath = Path.Combine(_root, variant + "-stock.csv");
            await Call(app, "reports.export", new { runId, revision, format = "csv" });
            Check(File.ReadAllText(host.SavePath).Contains("Donnée indisponible"),
                variant + ": the full stock export also marks the team unknown.");
            var custom = await Call(app, "dashboard.get");
            Check(Widget(custom, "valid").GetProperty("content").GetProperty("value").GetDouble() == 3
                && board.Widgets.Skip(1).All(widget => !Widget(custom, widget.Id).GetProperty("available").GetBoolean()),
                variant + ": the same isolation applies to custom metrics, history, maps and notes.");
            await Reject<InvalidDataException>(async () => { await Call(app, "dashboard.get", new { crossFilters = new[] { groupFilter } }); },
                variant + ": a common filter remains strict and never widens the entire dashboard.");
            var activeBoard = app.Settings.Dashboard.Layout.Boards[0];
            activeBoard.ComparisonEnabled = true; activeBoard.ComparisonFilters = new() { groupFilter.Copy() };
            await Reject<InvalidDataException>(async () => { await Call(app, "dashboard.get"); },
                variant + ": an unavailable comparison cannot silently use the full source.");
            activeBoard.ComparisonEnabled = false; activeBoard.ComparisonFilters.Clear();
        }
        Check(JsonSerializer.Serialize(app.Settings.Dashboard, PilotApplication.Json) == saved,
            "Unavailable widgets preserve saved filters, thresholds, visibility and geometry.");
        app.LoadFixture(source);
        var restored = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(Widget(restored, teamId).GetProperty("available").GetBoolean()
            && Widget(restored, teamId).GetProperty("content").GetProperty("value").GetDouble() == 1,
            "Restoring the expected columns automatically restores the original one-ticket team filter.");
        var rows = await Call(app, "dashboard.rows", new { runId = Str(restored, "runId"), revision = app.Revision, widgetId = teamId, kind = "metric", key = "active" });
        Check(Int(rows, "total") == 1 && rows.GetProperty("rows")[0].GetProperty("index").GetInt32() == 0,
            "Recovered drilldown retains the exact original team row and excludes other groups and closed tickets.");
        var empty = Data(2, Row("2", group: "Autre")); app.LoadFixture(empty);
        var zero = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(Widget(zero, teamId).GetProperty("available").GetBoolean()
            && Widget(zero, teamId).GetProperty("content").GetProperty("value").GetDouble() == 0,
            "A genuinely empty team with usable columns remains a known zero, distinct from an unavailable filter.");
    }
}
