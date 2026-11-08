using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private static DashboardPresentationSettings PresentationDeck(string first = "total", string second = "status") => new()
    {
        Slides = new()
        {
            new() { Id = "overview", Title = "Volume et répartition", Items = new()
            {
                new() { Id = "volume-one", WidgetId = first, X = 0, Y = 0, Width = 490, Height = 1000 },
                new() { Id = "groups-one", WidgetId = second, X = 510, Y = 0, Width = 490, Height = 1000 }
            } },
            new() { Id = "volume-detail", Title = "Le même volume sur une autre diapositive", Items = new()
            { new() { Id = "volume-two", WidgetId = first, X = 0, Y = 0, Width = 1000, Height = 1000 } } }
        }
    };

    private static async Task CheckPresentationRuntime()
    {
        var settings = Settings(); var board = settings.Dashboard.Layout.Boards[0];
        foreach (var widget in board.Widgets) widget.ShowMiniHistory = false;
        board.Widgets.Single(widget => widget.Id == "status").SourceKey = "group";
        board.Widgets.Single(widget => widget.Id == "status").TopCount = 3;
        board.Widgets.Add(new() { Id = "presentation-hidden", Kind = "note", Hidden = true });
        settings.Save();
        var host = new AnalyticsTestHost { OpenPath = Path.Combine(_root, "presentation-input.csv") };
        var source = Data(0, Enumerable.Range(1, 38).Select(index => Row(index.ToString(), group: "Présentation " + index.ToString("D2"))).ToArray());
        ExportService.WriteCsv(source, host.OpenPath);
        await using var app = new PilotApplication(host, (_, _) => { }, settings);
        await Call(app, "records.import");
        var model = await Call(app, "dashboard.get"); var runId = Str(model, "runId"); var revision = app.Revision;
        Task<JsonElement> ReadDeck() => Call(app, "dashboard.presentation.get", new { runId, revision });
        Task<JsonElement> SaveDeck(object? deck, string fingerprint) => Call(app, "dashboard.presentation.save", new { runId, revision, deck, baseFingerprint = fingerprint });
        var initial = await ReadDeck(); var composition = PresentationDeck();
        Check(initial.GetProperty("deck").ValueKind == JsonValueKind.Null, "A dashboard without a saved composition explicitly returns no presentation deck.");
        var saved = await SaveDeck(composition, Str(initial, "fingerprint"));
        Check(app.Revision == revision && Str(await ReadDeck(), "fingerprint") == Str(saved, "fingerprint")
            && app.Settings.Dashboard.Layout.Boards[0].Presentation!.Slides.Count == 2,
            "Saving two composed slides persists their geometry without invalidating the existing indicator calculation.");
        Check(app.Settings.Dashboard.Layout.Boards[0].Presentation!.Slides[1].Items[0].WidgetId == "total"
            && Str(Widget(await Call(app, "reports.preview", new { runId, revision, selectedWidgetIds = new[] { "total", "status" }, presentation = composition }), "total"), "metricValue") == "38",
            "The same real indicator can appear on separate slides while its report data is calculated once.");
        await Reject<InvalidOperationException>(async () => await SaveDeck(composition, Str(initial, "fingerprint")), "A stale deck fingerprint cannot overwrite a newer presentation.");
        var persisted = app.Settings.Dashboard.Layout.Boards[0].Presentation!;
        var copied = app.Settings.Dashboard.Layout.Copy(); copied.Boards[0].Presentation!.Slides[0].Items[0].Width = 200;
        Check(persisted.Slides[0].Items[0].Width == 490, "Dashboard copies own independent slides and placements.");

        foreach (var (label, edit) in new (string, Action<DashboardPresentationSettings>)[]
        {
            ("a negative coordinate", deck => deck.Slides[0].Items[0].X = -1),
            ("a rectangle outside the usable slide", deck => deck.Slides[0].Items[0].X = 700),
            ("a block below the minimum size", deck => deck.Slides[0].Items[0].Height = 79),
            ("a repeated placement identity", deck => deck.Slides[1].Items[0].Id = deck.Slides[0].Items[0].Id),
            ("a repeated slide identity", deck => deck.Slides[1].Id = deck.Slides[0].Id),
            ("the same widget repeated within one slide", deck => deck.Slides[0].Items[1].WidgetId = "total"),
            ("an unknown widget", deck => deck.Slides[0].Items[0].WidgetId = "not-a-widget"),
            ("a hidden widget", deck => deck.Slides[0].Items[0].WidgetId = "presentation-hidden"),
            ("a future deck version", deck => deck.Version = 2),
            ("too many slides", deck => { while (deck.Slides.Count <= 40) deck.Slides.Add(new() { Id = Guid.NewGuid().ToString("N") }); })
        })
        {
            var invalid = composition.Copy(); edit(invalid);
            await Reject<InvalidDataException>(async () => await SaveDeck(invalid, Str(saved, "fingerprint")), "Presentation save rejects " + label + " before changing the saved composition.");
        }
        var malformed = JsonSerializer.SerializeToElement(new { version = 1, slides = new[] { new { id = "x", title = "x", items = new[] { new { id = "i", widgetId = "total", x = 0.5, y = 0, width = 500, height = 500 } } } } });
        await Reject<JsonException>(async () => await SaveDeck(malformed, Str(saved, "fingerprint")), "Fractional coordinates are refused instead of silently rounding a slide placement.");
        var extraProperty = JsonSerializer.SerializeToElement(new { version = 1, html = "<script>bad()</script>", slides = Array.Empty<object>() });
        await Reject<JsonException>(async () => await SaveDeck(extraProperty, Str(saved, "fingerprint")), "A presentation cannot smuggle client HTML or unrecognized properties into portable settings.");
        Check(Str(await ReadDeck(), "fingerprint") == Str(saved, "fingerprint"), "Rejected geometries and malformed payloads preserve the last valid deck exactly.");

        var preview = await Call(app, "reports.preview", new { runId, revision, selectedWidgetIds = new[] { "total", "status" }, presentation = composition });
        Check(preview.GetProperty("presentation").GetProperty("slides")[0].GetProperty("items").GetArrayLength() == 2
            && Widget(preview, "status").GetProperty("tables")[0].GetProperty("rows").GetArrayLength() == 38
            && preview.GetProperty("warnings").EnumerateArray().Any(warning => warning.GetString()!.Contains("exhaustivité GLPI non vérifiée"))
            && preview.GetProperty("context").EnumerateArray().Any(pair => Str(pair, "key") == "Réserves du calcul"),
            "The prepared presentation retains multiple widgets, detailed rows and source-quality warnings even when no quality widget was selected.");
        var moved = composition.Copy(); moved.Slides[0].Items[0].Width = 400;
        host.SavePath = Path.Combine(_root, "presentation-token.pdf");
        await Reject<InvalidOperationException>(async () => await Call(app, "reports.export", new { runId, revision, format = "pdf", selectedWidgetIds = new[] { "total", "status" }, presentation = moved, selectionToken = Str(preview, "selectionToken") }),
            "A PDF preview token is bound to the exact slide geometry, not merely to its widget list.");
        Check(!File.Exists(host.SavePath) && host.Prints == 0, "A mismatched PDF composition never reaches the print host or creates an output file.");
        await Reject<InvalidDataException>(async () => await Call(app, "reports.preview", new { runId, revision, selectedWidgetIds = new[] { "status", "total" }, presentation = composition }),
            "Presentation widget order must match its first appearance across slides.");
        var emptySlide = composition.Copy(); emptySlide.Slides.Add(new() { Id = "empty-slide" });
        await Reject<InvalidDataException>(async () => await Call(app, "reports.preview", new { runId, revision, selectedWidgetIds = new[] { "total", "status" }, presentation = emptySlide }),
            "Empty slides remain editable but cannot be exported as accidental blank pages.");

        host.SavePath = Path.Combine(_root, "presentation-composed.html");
        await Call(app, "reports.export", new { runId, revision, format = "html", selectedWidgetIds = new[] { "total", "status" }, presentation = composition });
        var html = WebUtility.HtmlDecode(File.ReadAllText(host.SavePath));
        Check(html.Split("class=\"presentation-slide\"").Length - 1 == 2 && html.Contains("left:51%;top:0%;width:49%;height:100%")
            && html.Split("data-widget-id=\"total\"").Length - 1 == 2 && html.Contains("Présentation 38") && html.Contains("Content-Security-Policy"),
            "The real HTML exporter writes two composed pages, exact relative geometry, repeated visual placement and complete selected tables.");
        host.SavePath = Path.Combine(_root, "presentation-composed.csv");
        await Call(app, "reports.export", new { runId, revision, format = "csv", selectedWidgetIds = new[] { "total", "status" }, presentation = composition });
        var csv = NativeCsvImportService.Read(host.SavePath, "", null, 10000, false, default);
        Check(csv.Rows.Any(row => row.Contains("Présentation 38")) && csv.Rows.Count(row => row.Contains("Indicateur") && row.Contains("38")) == 1,
            "CSV preserves complete selected data once, independently of repeated visual placements on slides.");

        var portable = Path.Combine(_root, "presentation-configuration.json"); ConfigurationTransferService.Export(portable, app.Settings);
        var imported = ConfigurationTransferService.Import(portable, Settings());
        Check(imported.Dashboard.Layout.Boards[0].Presentation!.Slides[0].Items[1].X == 510,
            "Portable configuration export and import preserve the real slide composition.");
        var application = Path.Combine(_root, "presentation-deployment-app"); var seededRoot = Path.Combine(_root, "presentation-deployment-new"); Directory.CreateDirectory(application);
        DeploymentConfigurationInitializer.SaveDefault(application, AppPaths.DataRoot, "3.10.0", app.Settings);
        var initialized = DeploymentConfigurationInitializer.TryInitialize(application, seededRoot, "3.10.0");
        Check(initialized.SeedStatus == "applied" && SettingsRecovery.Read(Path.Combine(seededRoot, "settings.json")).Dashboard.Layout.Boards[0].Presentation!.Slides.Count == 2,
            "Default deployment configuration installs the saved slides into a fresh profile.");
        var appended = DashboardLayoutSettings.AppendBoards(app.Settings.Dashboard.Layout, app.Settings.Dashboard.Layout);
        Check(appended.Layout.Boards.Count == 2 && appended.Layout.Boards[1].Presentation!.WidgetIds().SequenceEqual(new[] { "total", "status" }),
            "Additive layout import retains slide references while resolving only the colliding board identity.");

        File.Delete(host.OpenPath);
        await using (var restarted = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, AppSettings.Load()))
        {
            await Call(restarted, "cache.restore"); var restored = await Call(restarted, "dashboard.get");
            var restoredDeck = await Call(restarted, "dashboard.presentation.get", new { runId = Str(restored, "runId"), revision = restarted.Revision });
            Check(restarted.IsCachedSource && restoredDeck.GetProperty("deck").GetProperty("slides").GetArrayLength() == 2 && !File.Exists(host.OpenPath),
                "A fresh runtime restores the slide composition against the offline source cache without the original CSV.");
        }
        var realSettings = File.ReadAllBytes(AppPaths.SettingsFile);
        await Call(app, "app.demo"); var simulated = await Call(app, "dashboard.get", new { businessView = "workspace" });
        var simulatedRead = await Call(app, "dashboard.presentation.get", new { runId = Str(simulated, "runId"), revision = app.Revision });
        var simulationIds = simulated.GetProperty("widgets").EnumerateArray().Take(2).Select(widget => Str(widget, "id")).ToArray();
        await Call(app, "dashboard.presentation.save", new { runId = Str(simulated, "runId"), revision = app.Revision, deck = PresentationDeck(simulationIds[0], simulationIds[1]), baseFingerprint = Str(simulatedRead, "fingerprint") });
        Check(app.IsTestMode && realSettings.AsSpan().SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile)),
            "Editing the fictitious demonstration presentation leaves normal profile bytes unchanged.");
        await CheckPresentationBoardScopes();
    }

    private static async Task CheckPresentationBoardScopes()
    {
        var settings = Settings();
        settings.Dashboard.Business.WeatherRules.Add(new() { Id = "deck-stock", Label = "Stock de contrôle", OrangeAbove = 10, RedAbove = 20 });
        await using var app = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, settings);
        app.LoadFixture(Data(0, Row("scope-1")));
        async Task<JsonElement> SaveFor(JsonElement model, DashboardPresentationSettings deck)
        {
            var args = new { runId = Str(model, "runId"), revision = app.Revision };
            var before = await Call(app, "dashboard.presentation.get", args);
            return await Call(app, "dashboard.presentation.save", new { args.runId, args.revision, deck, baseFingerprint = Str(before, "fingerprint") });
        }
        var personal = await Call(app, "dashboard.get", new { technicianKey = TechnicianDashboardService.IdentityKey("Alex") });
        await SaveFor(personal, PresentationDeck());
        Check(app.Settings.Dashboard.PersonalLayout.Boards[0].Presentation?.Slides.Count == 2
            && app.Settings.Dashboard.Layout.Boards[0].Presentation is null,
            "A personal dashboard saves its own presentation without overwriting the common dashboard.");
        var native = await Call(app, "dashboard.get", new { businessView = "workspace" });
        var nativeIds = native.GetProperty("widgets").EnumerateArray().Take(2).Select(widget => Str(widget, "id")).ToArray();
        await SaveFor(native, PresentationDeck(nativeIds[0], nativeIds[1]));
        var nativeBoard = app.Settings.Dashboard.Business.BoardOverrides.Single(item => item.Id == "business-workspace");
        Check(nativeBoard.Presentation!.Slides.Count == 2 && app.Settings.Dashboard.Layout.Boards[0].Presentation is null,
            "The native workspace persists its own deck without borrowing a custom or personal dashboard composition.");
        var bootstrap = await Call(app, "dashboard.business.settings.get");
        var business = app.Settings.Dashboard.Business.Copy(); business.WeatherRules[0].OrangeAbove = 11;
        await Call(app, "dashboard.business.settings.save", new { business, baseFingerprint = Str(bootstrap, "fingerprint") });
        nativeBoard = app.Settings.Dashboard.Business.BoardOverrides.Single(item => item.Id == "business-workspace");
        Check(nativeBoard.Presentation!.Slides[0].Items[0].WidgetId == nativeIds[0]
            && nativeBoard.Presentation.Slides[0].Items[0].Width == 490,
            "Regenerating native weather cards preserves the saved slide positions and stable widget references.");
        var copy = await Call(app, "dashboard.business.copy", new { businessView = "workspace" });
        var copied = app.Settings.Dashboard.Layout.Boards.Single(item => item.Id == Str(copy, "boardId"));
        Check(copied.Presentation is not null && copied.Presentation.WidgetIds().All(id => copied.Widgets.Any(widget => widget.Id == id))
            && !copied.Presentation.WidgetIds().Intersect(nativeIds).Any(),
            "Copying a native workspace remaps presentation placements to the newly assigned widget identities.");
        app.Settings.Dashboard.Layout.ActiveBoardId = settings.Dashboard.Layout.ActiveBoardId;
        var stale = await Call(app, "dashboard.get"); var staleRevision = app.Revision;
        app.LoadFixture(Data(1, Row("scope-2")));
        await Reject<InvalidOperationException>(async () => await Call(app, "dashboard.presentation.get", new { runId = Str(stale, "runId"), revision = staleRevision }),
            "An old presentation session cannot use a calculation from a different source observation.");
        var damaged = copied.Copy(); damaged.Widgets.RemoveAll(widget => widget.Id == damaged.Presentation!.WidgetIds()[0]);
        var layout = new DashboardLayoutSettings { ActiveBoardId = damaged.Id, Boards = new() { damaged } }; layout.Validate();
        Check(damaged.Presentation!.Slides[0].Items.Count == 2,
            "Removing a widget retains its editable placeholder instead of silently deleting the saved slide placement.");
    }
}
