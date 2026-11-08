using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckWeatherSelectionInterfaceAsync(string[] args)
    {
        var source = CreateWeatherScopeCsv();
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.OnboardingCompleted = true;
        settings.Dashboard.Business.WeatherRules = new()
        {
            new() { Id = "scope-global", Label = "Stock fictif du service", OrangeAbove = 100, RedAbove = 200 },
            new() { Id = "scope-support", Kind = "team", Label = "Support", Values = ["Support"], OrangeAbove = 10, RedAbove = 20 },
            new() { Id = "scope-network", Kind = "team", Label = "Réseau", Values = ["Réseau"], OrangeAbove = 2, RedAbove = 8 }
        };
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        string savedKey;
        await using (var ui = await WeatherSelectionUi.CreateAsync(browser, settings, source))
        {
            await ui.Navigate(); await ui.ChooseTeams("Support", "Réseau"); await ui.Stock(96);
            var baselineRules = JsonSerializer.Serialize(ui.App.Settings.Dashboard.Business.WeatherRules, PilotApplication.Json);
            Require(await ui.Page.Locator(".business-weather-card").CountAsync() > 0
                && await ui.Page.Locator(".business-weather-card").CountAsync() == await ui.Page.Locator(".business-weather-card [data-weather-selection]").CountAsync()
                && await ui.Hero.GetAttributeAsync("data-weather-state") == "unknown",
                "Every visible stock-weather widget exposes its own selection thresholds; the unconfigured multi-team total remains neutral.");
            var untouched = JsonSerializer.Serialize(ui.App.Settings, PilotApplication.Json);
            await ui.OpenEditor();
            Require(await ui.Editor.GetByLabel("Unité des seuils", new() { Exact = true }).InputValueAsync() == "count"
                && (await ui.Editor.Locator(".weather-selection-scope").InnerTextAsync()).Contains("Support", StringComparison.Ordinal)
                && await ui.Editor.GetByLabel("Seuil de vigilance", new() { Exact = true }).InputValueAsync() == "",
                "The direct editor retains Number as the existing default, identifies the selection and does not invent missing thresholds.");
            await ui.Capture(args, "weather-selection-editor-number");
            await ui.Editor.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
            Require(JsonSerializer.Serialize(ui.App.Settings, PilotApplication.Json) == untouched
                && !ui.Operations.Any(item => item.Operation == "dashboard.weatherSelection.save"),
                "Cancelling the real thresholds panel leaves settings unchanged and performs no save operation.");
            await ui.OpenEditor(); await ui.FillThresholds(90, 80);
            await ui.Editor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer les seuils", Exact = true }).ClickAsync();
            Require(await ui.Editor.GetByRole(AriaRole.Alert).IsVisibleAsync()
                && !ui.Operations.Any(item => item.Operation == "dashboard.weatherSelection.save"),
                "Reversed number thresholds are explained inline before any C# mutation is sent.");
            await ui.FillThresholds(80, 100); await ui.SaveEditor();
            var unionKey = ui.Selection.GetProperty("key").GetString();
            Require(ui.Selection.GetProperty("configured").GetBoolean() && await ui.Hero.GetAttributeAsync("data-weather-state") == "watch"
                && ui.HeroModel.GetProperty("content").GetProperty("value").GetDouble() == 96,
                "Saving 80/100 applies strict vigilance to the exact 96-ticket union while preserving its count.");
            await ui.ChooseTeams("Réseau", "Support");
            Require(ui.Selection.GetProperty("key").GetString() == unionKey && ui.Selection.GetProperty("configured").GetBoolean()
                && await ui.Hero.GetAttributeAsync("data-weather-state") == "watch",
                "Selecting the same teams in reverse order retrieves the same recorded thresholds.");
            await ui.ChooseTeams("Support", "Groupe isolé"); await ui.Stock(88);
            Require(!ui.Selection.GetProperty("configured").GetBoolean() && await ui.Hero.GetAttributeAsync("data-weather-state") == "unknown",
                "Another multi-team selection keeps its 88 tickets and remains neutral instead of borrowing the saved union thresholds.");
            await ui.ChooseTeams("Support", "Réseau"); await ui.OpenEditor();
            Require(await ui.Editor.GetByLabel("Seuil de vigilance", new() { Exact = true }).InputValueAsync() == "80"
                && await ui.Editor.GetByLabel("Seuil de tension", new() { Exact = true }).InputValueAsync() == "100",
                "Reopening the original selection loads the exact persisted values into its editor.");
            await ui.FillThresholds(50, 90); await ui.SaveEditor();
            Require(await ui.Hero.GetAttributeAsync("data-weather-state") == "rain"
                && JsonSerializer.Serialize(ui.App.Settings.Dashboard.Business.WeatherRules, PilotApplication.Json) == baselineRules,
                "Editing a selection changes its weather without rewriting the established service and team rules.");
            await ui.OpenEditor(); await ui.DeleteEditor();
            Require(!ui.Selection.GetProperty("configured").GetBoolean() && await ui.Hero.GetAttributeAsync("data-weather-state") == "unknown",
                "Deleting the only selection-specific thresholds restores the prudent neutral fallback.");

            await ui.OpenEditor();
            await ui.Editor.GetByLabel("Unité des seuils", new() { Exact = true }).SelectOptionAsync("percent");
            await ui.Editor.GetByLabel("Portée des seuils", new() { Exact = true }).SelectOptionAsync("widget");
            await ui.Editor.GetByLabel("Base du pourcentage", new() { Exact = true }).SelectOptionAsync("source-active");
            await ui.FillThresholds(70, 90); await ui.Capture(args, "weather-selection-editor-percent"); await ui.SaveEditor();
            var evaluation = ui.Selection.GetProperty("evaluation");
            Require(evaluation.GetProperty("numerator").GetDouble() == 96 && evaluation.GetProperty("denominator").GetDouble() == 100
                && evaluation.GetProperty("evaluatedValue").GetDouble() == 96 && await ui.Hero.GetAttributeAsync("data-weather-state") == "rain"
                && (await ui.Hero.Locator(".weather-selection-ratio").InnerTextAsync()).Contains("96 %", StringComparison.Ordinal)
                && (await ui.Hero.Locator(".pilot-weather-threshold-labels").InnerTextAsync()).Contains("70 %", StringComparison.Ordinal),
                "A reusable percent policy displays the real 96/100 base and percent thresholds while the primary number remains 96 tickets.");
            await ui.ChooseTeams("Support", "Groupe isolé"); await ui.Stock(88);
            Require(ui.Selection.GetProperty("evaluation").GetProperty("evaluatedValue").GetDouble() == 88
                && !ui.Selection.GetProperty("configured").GetBoolean() && await ui.Hero.GetAttributeAsync("data-weather-state") == "watch",
                "The reusable percent policy follows a different selection and recomputes 88/100 without requiring another saved filter exception.");
            await ui.OpenEditor();
            await ui.Editor.GetByLabel("Portée des seuils", new() { Exact = true }).SelectOptionAsync("selection");
            await ui.Editor.GetByLabel("Unité des seuils", new() { Exact = true }).SelectOptionAsync("count");
            await ui.FillThresholds(10, 20); await ui.SaveEditor();
            Require(ui.Selection.GetProperty("configured").GetBoolean() && await ui.Hero.GetAttributeAsync("data-weather-state") == "rain"
                && await ui.Hero.Locator(".weather-selection-ratio").CountAsync() == 0,
                "A number-based exception for this selection takes precedence over the reusable percentage without showing mixed units.");
            await ui.OpenEditor(); await ui.DeleteEditor();
            Require(!ui.Selection.GetProperty("configured").GetBoolean() && await ui.Hero.GetAttributeAsync("data-weather-state") == "watch"
                && (await ui.Hero.Locator(".weather-selection-ratio").InnerTextAsync()).Contains("88 %", StringComparison.Ordinal),
                "Removing the exception reveals the reusable percentage policy instead of deleting or converting it.");

            await ui.TypeFilter(false); await ui.Stock(44);
            Require(await ui.Hero.GetAttributeAsync("data-weather-state") == "sun"
                && ui.Selection.GetProperty("evaluation").GetProperty("evaluatedValue").GetDouble() == 44,
                "An incident-only filter recalculates the percentage from 44 contributing tickets and the same 100-ticket source base.");
            await ui.OpenEditor(); await ui.Editor.GetByLabel("Portée des seuils", new() { Exact = true }).SelectOptionAsync("selection");
            await ui.Editor.GetByLabel("Unité des seuils", new() { Exact = true }).SelectOptionAsync("count");
            await ui.FillThresholds(20, 60); await ui.SaveEditor(); savedKey = ui.Selection.GetProperty("key").GetString()!;
            await ui.TypeFilter(true); await ui.Stock(44);
            Require(ui.Selection.GetProperty("key").GetString() != savedKey && !ui.Selection.GetProperty("configured").GetBoolean()
                && await ui.Hero.GetAttributeAsync("data-weather-state") == "sun",
                "Excluding Incident instead of including it produces the same count but a different scope key and does not reuse the exception.");
            await ui.TypeFilter(false); await ui.Stock(44);
            Require(ui.Selection.GetProperty("key").GetString() == savedKey && ui.Selection.GetProperty("configured").GetBoolean()
                && await ui.Hero.GetAttributeAsync("data-weather-state") == "watch",
                "Restoring the exact operator and filters retrieves the saved 20/60 exception.");
            await ui.Capture(args, "weather-selection-number");
            Require(ui.Errors.IsEmpty && ui.ExternalRequests == 0, "The initial selection journey has no JavaScript error or external request.");
        }

        File.Delete(source);
        await using (var ui = await WeatherSelectionUi.CreateAsync(browser))
        {
            await ui.Navigate(); await ui.Stock(44);
            Require(ui.App.IsCachedSource && !ui.App.IsTestMode && !File.Exists(source)
                && await ui.Page.EvaluateAsync<int>("window.__initialWeatherStorage") == 0
                && ui.Selection.GetProperty("key").GetString() == savedKey && ui.Selection.GetProperty("configured").GetBoolean()
                && await ui.Hero.GetAttributeAsync("data-weather-state") == "watch"
                && !ui.Operations.Any(item => item.Operation.StartsWith("collect.", StringComparison.Ordinal)),
                "A new C# application and empty WebView profile restore teams, operator, thresholds and the offline cache after the original CSV is removed.");
            await ui.OpenEditor(); await ui.DeleteEditor();
            Require((await ui.Hero.Locator(".weather-selection-ratio").InnerTextAsync()).Contains("44 %", StringComparison.Ordinal),
                "The reusable percentage remains available after a real offline restart and local exception removal.");
            await ui.CheckPresentationAndExports(args);
            var beforeDemo = JsonSerializer.Serialize(ui.App.Settings, PilotApplication.Json);
            var persistedBeforeDemo = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
            await Call(ui.App, "app.demo"); await ui.Flush();
            await ui.Page.ReloadAsync(); await ReadyAsync(ui.Page);
            ui.Publish("app.navigate", new { page = "pilot", @params = new { focusKeys = Array.Empty<string>(), technicianKey = "", crossFilters = Array.Empty<object>() } });
            await ui.Flush(); await WaitForNavigationTargetAsync(ui.Page, "pilot"); await ui.Idle();
            await ui.OpenEditor(); await ui.Editor.GetByLabel("Unité des seuils", new() { Exact = true }).SelectOptionAsync("percent");
            await ui.Editor.GetByLabel("Portée des seuils", new() { Exact = true }).SelectOptionAsync("widget");
            await ui.Editor.GetByLabel("Base du pourcentage", new() { Exact = true }).SelectOptionAsync("source-active");
            await ui.FillThresholds(91, 99); await ui.SaveEditor();
            Require(ui.App.IsTestMode && (await File.ReadAllBytesAsync(AppPaths.SettingsFile)).SequenceEqual(persistedBeforeDemo)
                && ui.Selection.GetProperty("policy").GetProperty("warningValue").GetDouble() == 91,
                "The same editor works in the fictitious demonstration while all normal-profile settings stay byte-for-byte unchanged.");
            await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Quitter la simulation", Exact = true }).ClickAsync();
            await WaitForNavigationTargetAsync(ui.Page, "pilot"); await ui.Idle(); await ui.Stock(44);
            Require(!ui.App.IsTestMode && ui.App.IsCachedSource && JsonSerializer.Serialize(ui.App.Settings, PilotApplication.Json) == beforeDemo
                && ui.Selection.GetProperty("policy").GetProperty("warningValue").GetDouble() == 70,
                "Leaving the demonstration restores the normal cached source, selection and reusable 70/90 percentage policy.");
            var capability = await Call(ui.App, "app.bootstrap");
            Require(capability.GetProperty("session").GetProperty("administration").GetBoolean() == AdministrationCapabilities.Enabled
                && ui.Selection.GetProperty("canEdit").GetBoolean() && !ui.Operations.Any(item => item.Operation.StartsWith("admin.", StringComparison.Ordinal)),
                "Threshold editing respects the compiled Standard/Admin mode and never invokes or grants an administrative operation.");
            await ui.Capture(args, "weather-selection-percent-offline");
            Require(ui.Errors.IsEmpty && ui.ExternalRequests == 0, "Offline, presentation, PDF and demonstration flows finish without JavaScript errors or external requests.");
        }
    }

    private sealed class WeatherSelectionUi : IAsyncDisposable
    {
        private readonly object _gate = new();
        private JsonElement _model;
        private Task _delivery = Task.CompletedTask;
        private WebBridge _bridge = null!;
        private IBrowserContext _context = null!;
        private string _pdfText = "";
        public string PdfText => _pdfText;
        public IPage Page { get; private set; } = null!;
        public PilotApplication App { get; private set; } = null!;
        public TestHost Host { get; private set; } = null!;
        public ConcurrentQueue<string> Errors { get; } = new();
        public ConcurrentQueue<(string Operation, JsonElement Args)> Operations { get; } = new();
        public int ExternalRequests;
        public JsonElement Model { get { lock (_gate) return _model.Clone(); } }
        public JsonElement HeroModel => Model.GetProperty("widgets").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == Model.GetProperty("weatherScope").GetProperty("heroWidgetId").GetString());
        public JsonElement Selection => HeroModel.GetProperty("weatherSelection");
        public ILocator Hero => Page.Locator(".business-weather-hero");
        public ILocator Editor => Page.GetByRole(AriaRole.Dialog, new() { Name = "Seuils pour cette sélection", Exact = true });

        public static async Task<WeatherSelectionUi> CreateAsync(IBrowser browser, AppSettings? settings = null, string? source = null)
        {
            var ui = new WeatherSelectionUi();
            ui._context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
            ui.Page = await ui._context.NewPageAsync(); ui.Page.SetDefaultTimeout(15000);
            ui.Page.PageError += (_, error) => ui.Errors.Enqueue(error);
            var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
            await ui._context.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url);
                if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref ui.ExternalRequests); await route.AbortAsync(); return; }
                var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
                var path = Path.GetFullPath(Path.Combine(web, relative));
                if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404 }); return; }
                var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
                await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
            });
            ui.Host = new TestHost(_root) { Page = ui.Page, NextOpen = source };
            ui.App = new PilotApplication(ui.Host, (name, data) => ui._bridge?.Publish(name, data), settings);
            if (source is not null) await Call(ui.App, "records.import");
            ui._bridge = new WebBridge(ui.App.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
            {
                ui.Operations.Enqueue((op, data.Clone()));
                if (op == "app.ready") return new { ready = true };
                if (op == "host.editor") { ui.App.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
                if (op == "reports.export" && data.GetProperty("format").GetString() == "pdf")
                    ui._pdfText = await ui.Page.Locator(".presentation-export-document").InnerTextAsync();
                var result = await ui.App.ExecuteAsync(op, data, ct);
                if (op == "dashboard.get") lock (ui._gate) ui._model = JsonSerializer.SerializeToElement(result, PilotApplication.Json);
                return result;
            }, ui.Post);
            await ui.Page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => ui._bridge.Receive(json));
            await ui.Page.AddInitScriptAsync("""
                (() => { window.__initialWeatherStorage=localStorage.length; const listeners=new Set(); window.chrome||={}; window.chrome.webview={
                  postMessage:message=>window.__pilotSend(JSON.stringify(message)),
                  addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},
                  removeEventListener:(name,handler)=>listeners.delete(handler)
                };window.__pilotDeliver=message=>{for(const handler of listeners)handler({data:message});};})();
                """);
            await ui.Page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(ui.Page);
            return ui;
        }
        private void Post(string json)
        {
            lock (_gate) _delivery = _delivery.ContinueWith(async _ =>
            { if (!Page.IsClosed) await Page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        public void Publish(string name, object data) => _bridge.Publish(name, data);
        public Task Flush() { lock (_gate) return _delivery; }
        public Task Idle() => WaitForPageAsync(Page, "async()=>!(await import('/js/bridge.js')).api.pending&&!document.querySelector('.pilot-workspace[aria-busy=true]')");
        public async Task Navigate() { await ClickNavigationAsync(Page, "pilot"); await WaitForNavigationTargetAsync(Page, "pilot"); await Idle(); }
        public Task Stock(int value) => WaitForPageAsync(Page, "value=>document.querySelector('.business-weather-hero .analytics-metric-value')?.textContent.trim()===String(value)", value);
        public async Task ChooseTeams(params string[] labels)
        {
            await Page.Locator("[data-filter-teams]").ClickAsync();
            var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Choisir plusieurs équipes", Exact = true });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Toutes", Exact = true }).ClickAsync();
            foreach (var label in labels) await dialog.GetByRole(AriaRole.Checkbox, new() { Name = label, Exact = true }).CheckAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await Idle();
        }
        public async Task OpenEditor() { await Hero.GetByRole(AriaRole.Button, new() { Name = "Seuils pour cette sélection", Exact = true }).ClickAsync(); await Editor.WaitForAsync(); }
        public async Task FillThresholds(int warning, int critical)
        {
            await Editor.GetByLabel("Seuil de vigilance", new() { Exact = true }).FillAsync(warning.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Editor.GetByLabel("Seuil de tension", new() { Exact = true }).FillAsync(critical.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        public async Task SaveEditor() { await Editor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer les seuils", Exact = true }).ClickAsync(); await Editor.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle(); }
        public async Task DeleteEditor() { await Editor.GetByRole(AriaRole.Button, new() { Name = "Supprimer ces seuils", Exact = true }).ClickAsync(); await Editor.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle(); }
        public async Task TypeFilter(bool exclude)
        {
            await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^Filtres(?: · \d+)?$") }).ClickAsync();
            var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Périmètre commun à toutes les analyses", Exact = true });
            var editor = dialog.Locator(".analytics-filter-editor").First;
            if (await editor.Locator(".analytics-filter-row").CountAsync() == 0) await editor.GetByRole(AriaRole.Button, new() { Name = "+ Condition", Exact = true }).ClickAsync();
            await editor.GetByLabel("Source", new() { Exact = true }).SelectOptionAsync("type");
            await editor.GetByLabel("Valeurs acceptées · une par ligne", new() { Exact = true }).FillAsync("Incident");
            await editor.GetByRole(AriaRole.Checkbox, new() { Name = "Exclure", Exact = true }).SetCheckedAsync(exclude);
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await Idle();
        }
        public async Task Capture(string[] args, string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            Directory.CreateDirectory(args[index + 1]); await DismissCaptureNotificationsAsync(Page);
            await Page.Locator(".workspace").EvaluateAsync("node=>node.scrollTop=0");
            await Page.ScreenshotAsync(new() { Path = Path.Combine(args[index + 1], name + ".png"), FullPage = true });
            await GuideCaptureAsync(Page, name);
        }
        public async Task CheckPresentationAndExports(string[] args)
        {
            var id = HeroModel.GetProperty("id").GetString();
            await Page.GetByRole(AriaRole.Button, new() { Name = "Présenter", Exact = true }).ClickAsync();
            var picker = Page.GetByRole(AriaRole.Dialog, new() { Name = "Préparer la présentation", Exact = true });
            await picker.Locator("[data-presentation-editor][data-ready=true]").WaitForAsync();
            await picker.GetByRole(AriaRole.Button, new() { Name = "Supprimer la diapo", Exact = true }).ClickAsync();
            await picker.Locator($"[data-presentation-add='{id}']").ClickAsync();
            await picker.GetByRole(AriaRole.Button, new() { Name = "Répartir les widgets de cette diapo", Exact = true }).ClickAsync();
            await picker.GetByRole(AriaRole.Button, new() { Name = "Présenter", Exact = true }).ClickAsync();
            var slides = Page.GetByRole(AriaRole.Dialog, new() { Name = "Présentation du pilotage", Exact = true });
            Require((await slides.Locator(".weather-selection-ratio").InnerTextAsync()).Contains("44 %", StringComparison.Ordinal)
                && await slides.Locator(".presentation-canvas-content [data-weather-selection]").CountAsync() == 0,
                "Presentation preserves the actual percentage and denominator while removing editing controls.");
            var export = Page.GetByRole(AriaRole.Dialog, new() { Name = "Exporter la présentation", Exact = true });
            var html = Path.Combine(_root, "weather-selection.html"); Host.NextSave = html;
            await slides.GetByRole(AriaRole.Button, new() { Name = "Exporter la présentation", Exact = true }).ClickAsync();
            await export.GetByRole(AriaRole.Button, new() { Name = "Exporter", Exact = true }).ClickAsync();
            await Until(() => Operations.Any(item => item.Operation == "reports.export" && item.Args.GetProperty("format").GetString() == "html"));
            await Idle(); await Until(() => File.Exists(html));
            var report = WebUtility.HtmlDecode(await File.ReadAllTextAsync(html));
            Require(Regex.IsMatch(report, @"70\s*%") && Regex.IsMatch(report, @"90\s*%") && report.Contains("100", StringComparison.Ordinal),
                "The real HTML report exports percentage thresholds and the documented numeric base.");
            var pdf = Path.Combine(_root, "weather-selection.pdf"); Host.NextSave = pdf;
            await slides.GetByRole(AriaRole.Button, new() { Name = "Exporter la présentation", Exact = true }).ClickAsync();
            await export.GetByLabel("Format", new() { Exact = true }).SelectOptionAsync("pdf");
            await export.GetByRole(AriaRole.Button, new() { Name = "Exporter", Exact = true }).ClickAsync();
            await Until(() => Operations.Any(item => item.Operation == "reports.export" && item.Args.GetProperty("format").GetString() == "pdf"));
            await Idle(); await Until(() => File.Exists(pdf));
            await WaitForPageAsync(Page, "!document.querySelector('.presentation-export-document')");
            Require(new FileInfo(pdf).Length > 1000 && _pdfText.Contains("44 %", StringComparison.Ordinal)
                && _pdfText.Contains("70 %", StringComparison.Ordinal) && _pdfText.Contains("100", StringComparison.Ordinal)
                && await Page.Locator(".presentation-export-document").CountAsync() == 0,
                "The actual PDF export prints the same ratio, percent thresholds and base, then removes its temporary presentation document.");
            await Capture(args, "weather-selection-presentation");
            var index = Array.IndexOf(args, "--screenshots");
            if (index >= 0 && index + 1 < args.Length)
            { File.Copy(html, Path.Combine(args[index + 1], "weather-selection.html"), true); File.Copy(pdf, Path.Combine(args[index + 1], "weather-selection.pdf"), true); }
            await slides.GetByRole(AriaRole.Button, new() { Name = "Revenir à la préparation", Exact = true }).ClickAsync();
            await picker.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync(); await Idle();
        }
        public async ValueTask DisposeAsync()
        {
            await Flush(); await _bridge.DisposeAsync(); await App.DisposeAsync(); await _context.CloseAsync();
        }
    }
}
