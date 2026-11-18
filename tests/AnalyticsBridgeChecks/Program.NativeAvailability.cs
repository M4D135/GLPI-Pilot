using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static ExtractionResult NativeAvailabilitySource(bool completeColumns, bool empty = false)
    {
        var source = SupervisionCompatibilitySource();
        if (!completeColumns)
        {
            var columns = new[] { 0, 1, 2, 4 };
            source.Headers = columns.Select(index => source.Headers[index]).ToList();
            source.Rows = source.Rows.Select(row => columns.Select(index => row[index]).ToArray()).ToList();
            foreach (var row in source.Rows) row[2] = "Résolu";
        }
        if (empty) source.Rows.Clear();
        source.ExpectedTotal = source.Rows.Count;
        return source;
    }

    private static DashboardBoardSettings NativeAvailabilityBoard()
    {
        var board = new DashboardBoardSettings
        {
            Id = "business-workspace", Name = "Supervision personnalisée conservée", HideUnavailableWidgets = false,
            Description = "Disposition de contrôle", Density = "comfortable",
            Sections = new() { new() { Key = "stock", Title = "Stock et colonnes" }, new() { Key = "notes", Title = "Notes locales" } },
            Filters = new() { new() { Kind = "metric", SourceKey = "total" } }
        };
        foreach (var (id, kind, key, title) in new[]
        {
            ("native-zero", "metric", "active", "Actifs mesurés"),
            ("native-priority", "metric", "high", "Priorités hautes"),
            ("native-no-author", "metric", "active-no-author", "Sans rédacteur visible"),
            ("native-groups", "matrix", "group-status", "Groupes par statut"),
            ("native-priorities", "distribution", "active-priority", "Priorités actives"),
            ("native-oldest", "oldest", "groups", "Plus anciens par groupe"),
            ("native-participation", "participation", "authors", "Participation visible")
        }) board.Widgets.Add(new() { Id = id, Kind = kind, SourceKey = key, Title = title, Section = "stock", Span = 4, Height = 320,
            ShowMiniHistory = false, AutoHeight = false, Accent = "teal" });
        board.Widgets[0].GridX = 4; board.Widgets[0].GridY = 0;
        board.Widgets.Add(new() { Id = "native-note", Kind = "note", Title = "Note conservée", Note = "Conserver les choix du tableau.", Section = "notes", Span = 12 });
        board.Widgets.Add(new() { Id = "native-hidden", Kind = "metric", SourceKey = "total", Title = "Masquage volontaire", Section = "stock", Hidden = true, ShowMiniHistory = false });
        return board;
    }

    private static async Task CheckNativeAvailabilityRuntime()
    {
        var source = NativeAvailabilitySource(false); var settings = SupervisionCompatibilitySettings();
        var observed = BusinessDashboardTemplates.Observe(source, settings.Dashboard);
        Check(BusinessDashboardTemplates.Views.All(view => BusinessDashboardTemplates.Create(view.Key, settings.Dashboard, observed)
                is { HideUnavailableWidgets: true, UnavailableWidgetsPreferenceSet: true }),
            "Every generated native dashboard automatically hides unavailable widgets while keeping their definitions.");

        var saved = NativeAvailabilityBoard(); settings.Dashboard.Business.BoardOverrides.Add(saved);
        var serialized = JsonSerializer.Serialize(saved, PilotApplication.Json);
        await using var app = new PilotApplication(new AnalyticsTestHost(), (_, _) => { }, settings); app.LoadFixture(source);
        var model = await Call(app, "dashboard.get", new { businessView = "workspace" });
        var board = model.GetProperty("layout").GetProperty("boards")[0];
        Check(board.GetProperty("hideUnavailableWidgets").GetBoolean()
            && board.GetProperty("unavailableWidgetsPreferenceSet").GetBoolean()
            && JsonSerializer.Serialize(app.Settings.Dashboard.Business.BoardOverrides.Single(), PilotApplication.Json) == serialized
            && board.GetProperty("widgets").GetRawText() == JsonSerializer.Serialize(saved.Widgets, PilotApplication.Json),
            "A legacy native composition adopts automatic visibility in memory without rewriting stored geometry, order, filters or explicit widget hiding.");
        Check(Widget(model, "native-zero").GetProperty("available").GetBoolean()
            && Widget(model, "native-zero").GetProperty("content").GetProperty("value").GetDouble() == 0
            && new[] { "native-priority", "native-no-author", "native-groups", "native-priorities", "native-oldest", "native-participation" }
                .All(id => !Widget(model, id).GetProperty("available").GetBoolean())
            && board.GetProperty("widgets").GetArrayLength() == saved.Widgets.Count,
            "A measured zero remains available while cards missing priority, group or author columns remain defined but unavailable.");

        var projected = board.Deserialize<DashboardBoardSettings>(PilotApplication.Json)!;
        var copied = projected.Copy(); var roundTrip = JsonSerializer.Deserialize<DashboardBoardSettings>(JsonSerializer.Serialize(copied))!;
        Check(copied.UnavailableWidgetsPreferenceSet && roundTrip.UnavailableWidgetsPreferenceSet && roundTrip.HideUnavailableWidgets
            && JsonSerializer.Serialize(roundTrip.Widgets, PilotApplication.Json) == JsonSerializer.Serialize(saved.Widgets, PilotApplication.Json),
            "Copy and JSON retain the native visibility preference without changing any widget definition.");
        await Call(app, "dashboard.layout.save", new { businessView = "workspace", layout = model.GetProperty("layout") });
        Check(app.Settings.Dashboard.Business.BoardOverrides.Single() is { HideUnavailableWidgets: true, UnavailableWidgetsPreferenceSet: true }
            && AppSettings.Load().Dashboard.Business.BoardOverrides.Single() is { HideUnavailableWidgets: true, UnavailableWidgetsPreferenceSet: true },
            "The next explicit layout save durably records the migrated native default.");

        var layout = JsonNode.Parse(model.GetProperty("layout").GetRawText())!;
        layout["boards"]![0]!["hideUnavailableWidgets"] = false;
        layout["boards"]![0]!["unavailableWidgetsPreferenceSet"] = true;
        await Call(app, "dashboard.layout.save", new { businessView = "workspace", layout });
        var visible = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(!visible.GetProperty("layout").GetProperty("boards")[0].GetProperty("hideUnavailableWidgets").GetBoolean()
            && AppSettings.Load().Dashboard.Business.BoardOverrides.Single() is { HideUnavailableWidgets: false, UnavailableWidgetsPreferenceSet: true },
            "An explicit show-unavailable choice survives a fresh native calculation and the settings store.");
        var configuration = Path.Combine(_root, "native-availability-config.json");
        ConfigurationTransferService.Export(configuration, app.Settings);
        var imported = ConfigurationTransferService.Import(configuration, SupervisionCompatibilitySettings());
        Check(BusinessDashboardTemplates.Create("workspace", imported.Dashboard, observed)
                is { HideUnavailableWidgets: false, UnavailableWidgetsPreferenceSet: true },
            "Portable configuration preserves an explicit native show-unavailable preference.");

        layout["boards"]![0]!["hideUnavailableWidgets"] = true;
        await Call(app, "dashboard.layout.save", new { businessView = "workspace", layout });
        app.LoadFixture(NativeAvailabilitySource(true));
        var restored = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(new[] { "native-priority", "native-no-author", "native-groups", "native-priorities", "native-oldest", "native-participation" }
                .All(id => Widget(restored, id).GetProperty("available").GetBoolean())
            && restored.GetProperty("layout").GetProperty("boards")[0].GetProperty("widgets").GetRawText()
                == JsonSerializer.Serialize(saved.Widgets, PilotApplication.Json),
            "Restoring source columns makes the same saved native widgets available again without reconstructing or unhiding them.");
        app.LoadFixture(NativeAvailabilitySource(false, empty: true));
        var empty = await Call(app, "dashboard.get", new { businessView = "workspace" });
        Check(Int(empty.GetProperty("snapshot"), "rowCount") == 0
            && Widget(empty, "native-zero").GetProperty("available").GetBoolean()
            && Widget(empty, "native-zero").GetProperty("content").GetProperty("value").GetDouble() == 0
            && !Widget(empty, "native-groups").GetProperty("available").GetBoolean()
            && app.Settings.Dashboard.Business.BoardOverrides.Single().Widgets.Count == saved.Widgets.Count,
            "An empty observation with a known status column keeps a measured zero and preserves cards awaiting absent columns.");

        var custom = app.Settings.Dashboard.Layout.Copy(); custom.Boards[0].HideUnavailableWidgets = false;
        custom.Boards[0].UnavailableWidgetsPreferenceSet = false;
        await Call(app, "dashboard.layout.save", new { layout = custom });
        var personal = await Call(app, "dashboard.get", new { boardId = custom.ActiveBoardId });
        Check(!personal.GetProperty("layout").GetProperty("boards")[0].GetProperty("hideUnavailableWidgets").GetBoolean()
            && !personal.GetProperty("layout").GetProperty("boards")[0].GetProperty("unavailableWidgetsPreferenceSet").GetBoolean(),
            "Legacy custom dashboards keep their own show-unavailable setting without the native migration.");

        foreach (var view in BusinessDashboardTemplates.Views.Where(view => view.Key != "workspace"))
        {
            var previous = BusinessDashboardTemplates.Create(view.Key, settings.Dashboard, observed);
            previous.HideUnavailableWidgets = false; previous.UnavailableWidgetsPreferenceSet = false;
            var isolated = settings.Dashboard.Copy(); isolated.Business.BoardOverrides.Add(previous);
            var migrated = BusinessDashboardTemplates.Create(view.Key, isolated, observed);
            Check(migrated.HideUnavailableWidgets && migrated.UnavailableWidgetsPreferenceSet
                && !previous.HideUnavailableWidgets && !previous.UnavailableWidgetsPreferenceSet,
                "The saved native " + view.Key + " view adopts automatic visibility without rewriting its stored preference.");
        }
    }

    private static async Task CheckNativeAvailabilityWeb(PilotApplication app, IPage page, Func<string, object?, Task> open, string[] args)
    {
        await page.SetViewportSizeAsync(1440, 1000);
        await page.EvaluateAsync("""
            async () => { for (const name of ['workspaces','workspace-grid','pilot-toolbar','work-context','ergonomics']) {
              if (document.querySelector(`link[href="/css/${name}.css"]`)) continue;
              await new Promise((resolve,reject)=>{const link=document.createElement('link');link.rel='stylesheet';link.href=`/css/${name}.css`;
                link.onload=resolve;link.onerror=reject;document.head.append(link);}); } }
            """);
        var saved = NativeAvailabilityBoard();
        app.Settings.Dashboard.Business.BoardOverrides.RemoveAll(board => board.Id == saved.Id);
        app.Settings.Dashboard.Business.BoardOverrides.Add(saved);
        var widgetsBefore = JsonSerializer.Serialize(saved.Widgets, PilotApplication.Json);
        app.LoadFixture(NativeAvailabilitySource(false));
        var parameters = new { boardId = "service", focusKeys = Array.Empty<string>(), technicianKey = "", crossFilters = Array.Empty<object>() };
        async Task Open() => await open("pilot", parameters);
        ILocator Button(string label) => page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });
        var ids = new[] { "native-priority", "native-no-author", "native-groups", "native-priorities", "native-oldest", "native-participation" };
        ILocator Affected() => page.Locator(string.Join(",", ids.Select(id => "[data-widget-id=" + id + "]")));
        async Task Capture(string name)
        {
            var outputIndex = Array.IndexOf(args, "--screenshots");
            if (outputIndex < 0 || outputIndex + 1 >= args.Length) return;
            var output = Path.GetFullPath(args[outputIndex + 1]); Directory.CreateDirectory(output);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, name), FullPage = true });
        }
        async Task SetShowUnavailable(bool show)
        {
            await Button("Personnaliser la vue").ClickAsync();
            await Button("Cartes et sections visibles").ClickAsync();
            var modal = page.GetByRole(AriaRole.Dialog, new() { Name = "Choisir les informations visibles", Exact = true });
            await modal.GetByLabel("Afficher les widgets indisponibles", new() { Exact = true }).SetCheckedAsync(show);
            await modal.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync();
            await modal.WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Button("Terminer la composition").ClickAsync();
        }

        await Open();
        Check(await Affected().CountAsync() == 0
            && await page.Locator("[data-widget-id=native-zero] .analytics-metric-value").TextContentAsync() == "0"
            && await page.Locator("[data-widget-id=native-hidden]").CountAsync() == 0
            && JsonSerializer.Serialize(app.Settings.Dashboard.Business.BoardOverrides.Single(board => board.Id == saved.Id).Widgets, PilotApplication.Json) == widgetsBefore,
            "The real Supervision workspace hides unavailable native cards, preserves a measured zero and leaves saved visibility and geometry intact.");
        await Capture("supervision-unavailable-hidden.png");
        await Button("Personnaliser la vue").ClickAsync();
        Check(await Affected().CountAsync() == ids.Length
            && await page.Locator("[data-widget-id=native-groups][data-available=false]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).IsEnabledAsync(),
            "Supervision editing reveals automatically hidden native cards with their real correction controls.");
        await Button("Terminer la composition").ClickAsync();
        await SetShowUnavailable(true); await Open();
        Check(await Affected().CountAsync() == ids.Length
            && AppSettings.Load().Dashboard.Business.BoardOverrides.Single(board => board.Id == saved.Id)
                is { HideUnavailableWidgets: false, UnavailableWidgetsPreferenceSet: true }
            && await page.Locator("[data-widget-id=native-zero] .analytics-metric-value").TextContentAsync() == "0",
            "The explicit show-unavailable checkbox persists on the real native workspace and survives reopening.");
        await Capture("supervision-show-unavailable.png");
        await SetShowUnavailable(false);
        Check(await Affected().CountAsync() == 0
            && JsonSerializer.Serialize(app.Settings.Dashboard.Business.BoardOverrides.Single(board => board.Id == saved.Id).Widgets, PilotApplication.Json) == widgetsBefore,
            "Returning Supervision to automatic hiding changes neither card order, positions, filters nor deliberate Hidden flags.");

        app.LoadFixture(NativeAvailabilitySource(true)); await Open();
        Check(await Affected().CountAsync() == ids.Length
            && await Affected().EvaluateAllAsync<bool>("nodes=>nodes.every(node=>node.dataset.available==='true')")
            && await page.Locator("[data-widget-id=native-zero] .analytics-metric-value").TextContentAsync() == "5"
            && await page.Locator("[data-widget-id=native-hidden]").CountAsync() == 0,
            "The same native cards reappear automatically when the next observation restores their required columns.");
        await Capture("supervision-columns-restored.png");
        app.LoadFixture(NativeAvailabilitySource(false, empty: true)); await Open();
        Check(await Affected().CountAsync() == 0
            && await page.Locator("[data-widget-id=native-zero] .analytics-metric-value").TextContentAsync() == "0"
            && app.Settings.Dashboard.Business.BoardOverrides.Single(board => board.Id == saved.Id).Widgets.Count == saved.Widgets.Count,
            "An empty observation still renders the calculable zero and retains unavailable Supervision cards for a later observation.");
    }
}
