using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private static ExtractionResult SupervisionCompatibilitySource() => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Groupe", "Ouverture", "Technicien", "Suivis-Rédacteur", "Tâches-Rédacteur", "Priorite\u0301", "Catégorie", "Type", "Dernière modif" },
        Rows = new()
        {
            new[] { "101", "Ouverture la plus ancienne", "Nouveau", "Alpha", "01/09/2026 08:00", "Agent A (1)", "Agent A (1)", "Agent A (1)", "Haute", "Accès", "Incident", "01/10/2026" },
            new[] { "102", "Demande en attente", "En attente", "Alpha", "02/09/2026 08:00", "Agent B (2)", "Agent A (1)", "Agent B (2)", "Moyenne", "Accès", "Demande", "01/10/2026" },
            new[] { "103", "Incident attribué", "En cours (attribué)", "Bêta", "03/09/2026 08:00", "Agent A (1)", "Agent B (2)", "Agent A (1)", "Basse", "Matériel", "Incident", "01/10/2026" },
            new[] { "104", "Ticket résolu hors stock", "Résolu", "Alpha", "04/09/2026 08:00", "Agent A (1)", "Agent A (1)", "Agent A (1)", "Haute", "Accès", "Incident", "01/10/2026" },
            new[] { "105", "Demande dans deux groupes", "Nouveau", "Alpha | Bêta", "05/09/2026 08:00", "Agent B (2)", "Agent A (1)", "Agent A (1)", "Très haute", "Matériel", "Demande", "01/10/2026" },
            new[] { "106", "Intervention sans date", "En cours (planifié)", "Bêta", "", "Agent A (1)", "", "", "Basse", "Autre", "Incident", "01/10/2026" }
        },
        ExtractedAt = Clock, Complete = true, ExpectedTotal = 6,
        Provenance = new() { SourceKind = "file", Method = "csv", OriginalFileName = "supervision-compatibilite.csv" }
    };

    private static AppSettings SupervisionCompatibilitySettings()
    {
        var settings = new AppSettings { ExportFolder = Path.Combine(_root, "supervision-exports"), Queries = new() };
        foreach (var pair in new[] { ("status", "Statut"), ("group", "Attribué à - Groupe de techniciens"), ("created", "Date d'ouverture"),
            ("assignee", "Attribué à - Technicien"), ("followupAuthor", "Suivis - Rédacteur"), ("taskAuthor", "Tâches - Rédacteur"),
            ("priority", "Priorité"), ("updated", "Dernière modification") })
            settings.Dashboard.ColumnMappings[pair.Item1] = pair.Item2;
        return settings;
    }

    private static JsonElement SupervisionWidget(JsonElement dashboard, string title)
        => dashboard.GetProperty("widgets").EnumerateArray().Single(widget => Str(widget.GetProperty("settings"), "title") == title);

    private static async Task CheckSupervisionCompatibility()
    {
        var settings = SupervisionCompatibilitySettings();
        var host = new AnalyticsTestHost { OpenPath = Path.Combine(_root, "supervision-compatibilite.csv") };
        ExportService.WriteCsv(SupervisionCompatibilitySource(), host.OpenPath);
        await using var app = new PilotApplication(host, (_, _) => { }, settings,
            snapshots: new SnapshotStore(Path.Combine(_root, "supervision-compatibility-history")));
        await Call(app, "records.import");
        var dashboard = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(dashboard.GetProperty("fileSource").GetBoolean() && !app.IsTestMode && Metric(dashboard, "active").GetProperty("value").GetDouble() == 5,
            "The real CSV import retains five active tickets with the established short headers and older explicit mappings.");
        var titles = new[] { "10 premiers groupes par statut", "Priorités actives", "Les cinq plus anciens par groupe",
            "Les dix plus anciens du périmètre", "Stock actif par groupe", "Tickets rédigés et tickets attribués", "Suivis, tâches, intersection et union" };
        foreach (var title in titles)
        {
            var widget = SupervisionWidget(dashboard, title);
            Check(widget.GetProperty("available").GetBoolean() && widget.GetProperty("content").GetProperty("available").GetBoolean(),
                "The supervision widget contains available calculated data, not only a heading: " + title + ".");
        }
        var matrix = SupervisionWidget(dashboard, titles[0]).GetProperty("content");
        var totals = matrix.GetProperty("rowTotals").EnumerateArray().ToDictionary(row => Str(row, "label"), row => row.GetProperty("value").GetDouble());
        Check(totals.Count == 2 && totals["Alpha"] == 3 && totals["Bêta"] == 3
            && SupervisionWidget(dashboard, "Stock actif par groupe").GetProperty("content").GetProperty("cells").GetRawText() == matrix.GetProperty("cells").GetRawText(),
            "Both group/status views retain the same three Alpha and three Bêta memberships while the shared ticket counts once globally.");
        var priorities = SupervisionWidget(dashboard, "Priorités actives").GetProperty("content").GetProperty("buckets").EnumerateArray()
            .ToDictionary(bucket => Str(bucket, "label"), bucket => Int(bucket, "count"));
        Check(priorities.Count == 4 && priorities["Basse"] == 2 && priorities["Haute"] == 1 && priorities["Moyenne"] == 1 && priorities["Très haute"] == 1,
            "Active priorities use the decomposed-accent source header and exclude the solved ticket.");
        var global = SupervisionWidget(dashboard, "Les dix plus anciens du périmètre");
        var ranking = global.GetProperty("content");
        Check(Int(ranking, "count") == 5 && Int(ranking, "datedCount") == 4 && Int(ranking, "missingDateCount") == 1
            && ranking.GetProperty("groups")[0].GetProperty("oldest").EnumerateArray().Select(ticket => Str(ticket, "id")).SequenceEqual(new[] { "101", "102", "103", "105" }),
            "The global oldest ranking orders the four dated active tickets once and keeps the undated active ticket separately.");
        var perGroup = SupervisionWidget(dashboard, "Les cinq plus anciens par groupe").GetProperty("content").GetProperty("groups");
        Check(perGroup.GetArrayLength() == 2 && perGroup.EnumerateArray().All(group => Int(group, "count") == 3)
            && perGroup.EnumerateArray().Single(group => Str(group, "label") == "Alpha").GetProperty("oldest").EnumerateArray().Select(ticket => Str(ticket, "id")).SequenceEqual(new[] { "101", "102", "105" }),
            "The per-group oldest ranking preserves group membership and exact source ticket order.");
        var authors = SupervisionWidget(dashboard, "Suivis, tâches, intersection et union");
        var actor = authors.GetProperty("content").GetProperty("participants").EnumerateArray().Single(person => Str(person, "identity") == "ID:1");
        Check(Int(actor, "followups") == 3 && Int(actor, "tasks") == 3 && Int(actor, "both") == 2 && Int(actor, "writers") == 4 && Int(actor, "assigned") == 3,
            "Participation computes followups 3, tasks 3, intersection 2, union 4 and assignment 3 from real active source occurrences.");
        var drilldown = await Call(app, "dashboard.rows", new { runId = Str(dashboard, "runId"), revision = app.Revision,
            widgetId = Str(authors, "id"), kind = "participation", key = Str(actor, "key"), part = "both" });
        Check(drilldown.GetProperty("rows").EnumerateArray().Select(row => Int(row, "index")).SequenceEqual(new[] { 0, 4 }),
            "Participation intersection drilldown resolves the exact two original rows.");
        var filtered = await Call(app, "dashboard.get", new { businessView = "workspace", focusKeys = new[] { "group:Alpha" } });
        Check(Metric(filtered, "active").GetProperty("value").GetDouble() == 3
            && titles.All(title => SupervisionWidget(filtered, title).GetProperty("available").GetBoolean())
            && SupervisionWidget(filtered, "Les dix plus anciens du périmètre").GetProperty("content").GetProperty("groups")[0]
                .GetProperty("oldest").EnumerateArray().Select(ticket => Str(ticket, "id")).SequenceEqual(new[] { "101", "102", "105" }),
            "One shared group filter leaves all supervision calculations available on the exact three-ticket population.");

        await using var restored = new PilotApplication(host, (_, _) => { }, settings);
        await Call(restored, "cache.restore");
        var cached = await Call(restored, "dashboard.get", new { businessView = "workspace" });
        Check(restored.IsCachedSource && titles.All(title => SupervisionWidget(cached, title).GetProperty("available").GetBoolean())
            && Metric(cached, "active").GetProperty("value").GetDouble() == 5,
            "Restarting from the saved source retains the same supervision data and compatible column interpretation.");
        await CheckOldestWithoutGroups();
    }

    private static async Task CheckOldestWithoutGroups()
    {
        static ExtractionResult Without(ExtractionResult source, string header)
        {
            var position = source.Headers.IndexOf(header);
            source.Headers.RemoveAt(position);
            source.Rows = source.Rows.Select(row => row.Where((_, index) => index != position).ToArray()).ToList();
            return source;
        }
        var source = Without(SupervisionCompatibilitySource(), "Groupe");
        var settings = SupervisionCompatibilitySettings(); var host = new AnalyticsTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings);
        app.LoadFixture(source);
        var dashboard = await Call(app, "dashboard.get", new { businessView = "workspace" });
        var global = SupervisionWidget(dashboard, "Les dix plus anciens du périmètre");
        var groups = SupervisionWidget(dashboard, "Les cinq plus anciens par groupe");
        var catalog = dashboard.GetProperty("catalog").GetProperty("oldest").EnumerateArray().ToDictionary(row => Str(row, "key"), row => row.GetProperty("available").GetBoolean());
        Check(global.GetProperty("available").GetBoolean() && global.GetProperty("content").GetProperty("available").GetBoolean()
            && !groups.GetProperty("available").GetBoolean() && !groups.GetProperty("content").GetProperty("available").GetBoolean()
            && catalog["tickets"] && !catalog["groups"] && groups.GetProperty("content").GetProperty("groups").GetArrayLength() == 0,
            "A missing group column leaves the global oldest ranking available without inventing group assignments or enabling the group widget.");
        Check(Str(SupervisionWidget(dashboard, "Stock actif par groupe"), "unavailableReason").Contains("Groupe attribué", StringComparison.Ordinal)
            && Str(SupervisionWidget(dashboard, "Stock actif par groupe"), "unavailableReason").Contains("absente", StringComparison.Ordinal),
            "An unavailable group/status widget names the absent assignment-group mapping rather than a generic data error.");
        var rows = await Call(app, "dashboard.rows", new { runId = Str(dashboard, "runId"), revision = app.Revision,
            widgetId = Str(global, "id"), kind = "oldest", key = "scope:", part = "dated" });
        Check(rows.GetProperty("rows").EnumerateArray().Select(row => Int(row, "index")).SequenceEqual(new[] { 0, 1, 2, 4 }),
            "The global oldest drilldown returns the four dated active source rows even when no group column exists.");
        await Reject<InvalidDataException>(async () => await Call(app, "dashboard.rows", new { runId = Str(dashboard, "runId"), revision = app.Revision,
            widgetId = Str(groups, "id"), kind = "oldest", key = "unassigned:", part = "stock" }),
            "A missing group column cannot be drilled into a fabricated unassigned-group population.");
        var selected = await Call(app, "reports.preview", new { runId = Str(dashboard, "runId"), revision = app.Revision,
            selectedWidgetIds = new[] { Str(global, "id"), Str(groups, "id") } });
        Check(Widget(selected, Str(global, "id")).GetProperty("tables").GetArrayLength() == 2
            && Widget(selected, Str(global, "id")).GetProperty("tables")[1].GetProperty("rows").GetArrayLength() == 4
            && Widget(selected, Str(groups, "id")).GetProperty("tables").GetArrayLength() == 0,
            "Meeting report preview exports the complete global ranking and an explicit unavailable group block.");
        foreach (var format in new[] { "html", "csv" })
        {
            host.SavePath = Path.Combine(_root, "oldest-without-group." + format);
            await Call(app, "reports.export", new { runId = Str(dashboard, "runId"), revision = app.Revision, format });
            var text = File.ReadAllText(host.SavePath);
            Check(text.Contains("Ouverture la plus ancienne", StringComparison.Ordinal)
                && text.Contains(format == "html" ? "Classement global du périmètre" : "Plus anciens · périmètre", StringComparison.Ordinal)
                && text.Contains("classement par groupe", StringComparison.Ordinal),
                "The complete " + format.ToUpperInvariant() + " export retains global oldest data and explains only the unavailable group ranking.");
        }
        var snapshot = DashboardService.Calculate(source, settings.Dashboard);
        Check(DashboardService.ToExport(snapshot).Rows.Count == DashboardService.EstimatedExportRows(snapshot),
            "The detailed export row estimate includes the available global ranking and unavailable group warning exactly.");
        app.LoadFixture(Without(source, "Ouverture"));
        var noDates = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(!SupervisionWidget(noDates, "Les dix plus anciens du périmètre").GetProperty("available").GetBoolean(),
            "An absent opening-date column still disables the global ranking rather than inventing ages.");
        app.LoadFixture(Without(SupervisionCompatibilitySource(), "Priorite\u0301"));
        var noPriority = await Call(app, "dashboard.get", new { businessView = "workspace" });
        var unavailablePriority = SupervisionWidget(noPriority, "Priorités actives");
        Check(!unavailablePriority.GetProperty("available").GetBoolean()
            && Str(unavailablePriority, "unavailableReason").Contains("Priorité", StringComparison.Ordinal)
            && Str(unavailablePriority, "unavailableReason").Contains("absente", StringComparison.Ordinal),
            "A genuinely absent priority column stays unavailable and identifies the exact missing field.");
        var unknown = SupervisionCompatibilitySource();
        foreach (var row in unknown.Rows) row[2] = "Statut inconnu";
        app.LoadFixture(unknown);
        var noKnownStatus = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(!SupervisionWidget(noKnownStatus, "Priorités actives").GetProperty("available").GetBoolean()
            && Str(SupervisionWidget(noKnownStatus, "Stock actif par groupe"), "unavailableReason").Contains("aucune valeur reconnue", StringComparison.Ordinal),
            "Unknown status values remain unknown and receive a status-specific diagnosis without fabricating an empty active stock.");
    }
}
