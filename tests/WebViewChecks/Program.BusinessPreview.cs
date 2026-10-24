using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckBusinessPreviewAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1380, Height = 960 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var dashboardGets = new ConcurrentQueue<string>(); var externalRequests = 0;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page, NextOpen = CreateBusinessPreviewCsv() };
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") });
        await Call(app, "records.test.import");
        var bootstrap = await Call(app, "dashboard.business.settings.get");
        var business = new BusinessDashboardSettings { Notes = "Exemple fictif · Priorité aux demandes d’accès et aux tickets les plus anciens. Revue des interventions prévue demain.", FreshnessWarningHours = 48 };
        business.WeatherRules.Add(new() { Id = "synthetic-stock", Label = "Tickets en cours", OrangeAbove = 180, RedAbove = 200, Note = "Incidents et demandes, attente et planification incluses." });
        for (var index = 1; index <= 8; index++) business.WeatherRules.Add(new()
        {
            Id = "synthetic-category-" + index, Kind = "category", Label = "Catégorie " + index, Values = new() { "Catégorie " + index }, IncludeDescendants = true,
            OrangeAbove = index % 3 == 0 ? 24 : index % 3 == 1 ? 12 : 8, RedAbove = index % 3 == 2 ? 16 : 30
        });
        for (var index = 1; index <= 6; index++) business.WeatherRules.Add(new()
        {
            Id = "synthetic-team-" + index, Kind = "team", Label = "Équipe " + index, Values = new() { "Groupe " + index }, Visible = index != 1,
            OrangeAbove = index % 3 == 0 ? 40 : index % 3 == 1 ? 20 : 10, RedAbove = index % 3 == 2 ? 20 : 50
        });
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            if (op == "dashboard.get") dashboardGets.Enqueue(data.GetRawText());
            return await app.ExecuteAsync(op, data, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);
        async Task Ready(string id) => await WaitForNavigationTargetAsync(page, id);
        async Task Navigate(string id) { await ClickNavigationAsync(page, id); await Ready(id); }
        async Task Settled() => await WaitForPageAsync(page, "async()=>!document.querySelector('.pilot-workspace')?.hasAttribute('aria-busy')&&!(await import('/js/bridge.js')).api.pending");
        async Task Capture(string name)
        {
            var argument = Array.IndexOf(args, "--screenshots"); if (argument < 0 || argument + 1 >= args.Length) return;
            Directory.CreateDirectory(args[argument + 1]);
            await page.Locator("#notifications .toast button").EvaluateAllAsync<int>("buttons=>{buttons.forEach(button=>button.click());return buttons.length;}");
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png") });
        }
        ILocator Stock() => page.Locator(".business-weather-hero .analytics-metric-value");
        ILocator Picker() => page.GetByRole(AriaRole.Dialog, new() { Name = "Choisir plusieurs équipes", Exact = true });
        async Task StockValue(string value) => await WaitForPageAsync(page, "value=>document.querySelector('.business-weather-hero .analytics-metric-value')?.textContent.trim()===value", value);
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("body[data-ready=true]").WaitForAsync();
        await Navigate("pilot"); await Settled();
        Require(await page.Locator(".business-weather-card").CountAsync() == 17
            && await page.Locator(".business-weather-card[data-weather-state=unknown], .business-weather-card[data-weather-state=partial]").CountAsync() == 0
            && await page.Locator(".business-weather-card[data-weather-state=watch]").CountAsync() > 0
            && await page.Locator(".business-weather-card[data-weather-state=rain]").CountAsync() > 0
            && await page.Locator(".workspace-weather-defaults").IsVisibleAsync(),
            "A plain CSV immediately supplies all 17 stock, type, category and team weather cards with explicit starter thresholds.");
        Require(await page.Locator(".pilot-block").CountAsync() == 7 && await page.Locator(".workspace-views,.workspace-view,.business-dashboard-tools").CountAsync() == 0,
            "Pilotage is one seven-section composition with shared controls, without old pages or local view tabs.");
        await page.SetViewportSizeAsync(1600, 1000); await page.Locator(".workspace").EvaluateAsync("node=>node.scrollTop=0");
        await Capture("pilot-starter-light-1600");
        await Navigate("pilot-settings");
        ILocator Threshold(string colour, string label) => page.GetByLabel("Seuil " + colour + " de " + label, new() { Exact = true });
        Require(await Threshold("orange", "Tickets en cours").InputValueAsync() == "100" && await Threshold("rouge", "Tickets en cours").InputValueAsync() == "200"
            && await Threshold("orange", "Incidents en cours").InputValueAsync() == "50" && await Threshold("rouge", "Demandes en cours").InputValueAsync() == "100"
            && await Threshold("orange", "Catégorie 1").InputValueAsync() == "10" && await Threshold("rouge", "Groupe 1").InputValueAsync() == "20",
            "The generic threshold pairs remain editable in the contextual weather settings.");
        await Threshold("orange", "Tickets en cours").FillAsync(""); await Threshold("rouge", "Tickets en cours").FillAsync("");
        await Threshold("orange", "Incidents en cours").FillAsync(""); await Threshold("rouge", "Incidents en cours").FillAsync("5");
        await Threshold("orange", "Demandes en cours").FillAsync("300"); await Threshold("rouge", "Demandes en cours").FillAsync("");
        await Threshold("orange", "Catégorie 1").FillAsync("0"); await Threshold("rouge", "Catégorie 1").FillAsync("");
        await Threshold("orange", "Catégorie 2").FillAsync(""); await Threshold("rouge", "Catégorie 2").FillAsync("");
        await page.GetByRole(AriaRole.Button, new() { Name = "Remplir les seuils vides", Exact = true }).ClickAsync();
        Require(await Threshold("orange", "Tickets en cours").InputValueAsync() == "100" && await Threshold("rouge", "Tickets en cours").InputValueAsync() == "200"
            && await Threshold("orange", "Incidents en cours").InputValueAsync() == "" && await Threshold("rouge", "Incidents en cours").InputValueAsync() == "5"
            && await Threshold("orange", "Demandes en cours").InputValueAsync() == "300" && await Threshold("rouge", "Demandes en cours").InputValueAsync() == ""
            && await Threshold("orange", "Catégorie 1").InputValueAsync() == "0" && await Threshold("rouge", "Catégorie 1").InputValueAsync() == "20"
            && await Threshold("orange", "Catégorie 2").InputValueAsync() == "10" && await Threshold("rouge", "Catégorie 2").InputValueAsync() == "20"
            && (await page.Locator(".business-settings-status").InnerTextAsync()).Contains("2 règle(s) à compléter manuellement", StringComparison.Ordinal)
            && (await Call(app, "dashboard.business.settings.get")).GetProperty("usingWeatherDefaults").GetBoolean(),
            "Fill-empty remains a local draft: zero and custom limits survive, incompatible partial pairs require explicit correction.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer les paramètres", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "async()=>document.querySelector('.business-settings-status')?.textContent===''&&!(await import('/js/bridge.js')).api.pending");
        await Navigate("pilot"); await Settled();
        var explicitlySaved = await Call(app, "dashboard.business.settings.get");
        Require(!explicitlySaved.GetProperty("usingWeatherDefaults").GetBoolean()
            && await page.Locator(".workspace-weather-defaults").CountAsync() == 0
            && await page.Locator(".business-weather-card[data-weather-state=unknown]").CountAsync() == 2,
            "Explicitly saved incomplete thresholds stay unknown and no longer claim to be starter settings.");
        await Call(app, "dashboard.business.settings.save", new { business, baseFingerprint = explicitlySaved.GetProperty("fingerprint").GetString(),
            sites = new[] { new SiteDefinition { Name = "Site exemple Nord" }, new SiteDefinition { Name = "Site exemple Sud" }, new SiteDefinition { Name = "Site exemple sans ticket" } } });
        await Navigate("pilot"); await Settled();
        Require(await page.Locator(".business-weather-card").CountAsync() == 14
            && await page.Locator("[data-section-key=weather] .business-weather-card").CountAsync() == 14
            && await page.Locator(".business-weather-card[data-weather-state=sun]").CountAsync() > 0
            && await page.Locator(".business-weather-card[data-weather-state=watch]").CountAsync() > 0
            && await page.Locator(".business-weather-card[data-weather-state=rain]").CountAsync() > 0,
            "The unified weather preserves the complete configured stock, eight categories and five visible teams, with real sun/watch/rain results.");
        Require(await Stock().InnerTextAsync() == "168"
            && (await page.Locator(".workspace-closed-stock").InnerTextAsync()).StartsWith("8 résolus ou clos", StringComparison.Ordinal)
            && await page.Locator(".workspace-operation-status .notice.danger").CountAsync() == 0,
            "The shared scope reports 168 active tickets and explicitly excludes the eight closed or solved rows without inventing a collection failure.");
        await page.Locator(".workspace-provenance>summary").ClickAsync();
        Require((await page.Locator(".workspace-provenance").InnerTextAsync()).Contains("couverture GLPI non vérifiée", StringComparison.Ordinal),
            "Source provenance still states that a fully imported local CSV does not verify GLPI coverage.");
        await page.Locator(".workspace-provenance>summary").ClickAsync();
        foreach (var title in new[] { "10 premiers groupes par statut", "8 principales catégories", "En cours · attribués", "En cours · planifiés",
            "Les cinq plus anciens par groupe", "Les dix plus anciens du périmètre", "Tickets rédigés et tickets attribués", "Suivis, tâches, intersection et union" })
            Require(await page.GetByRole(AriaRole.Heading, new() { Name = title, Exact = true }).CountAsync() == 1,
                "The redesigned composition retains the complete analytical block: " + title + ".");
        Require(await page.Locator("[data-section-key=people]").GetByRole(AriaRole.Columnheader, new() { Name = "Clé GLPI", Exact = true }).CountAsync() >= 1,
            "The contribution section keeps the explicit GLPI identity column.");
        Require((await page.Locator("[data-widget-id=workspace-stock-history]").InnerTextAsync()).Contains("Aucune observation comparable", StringComparison.Ordinal),
            "A lone simulation import has no fabricated trend or comparison curve.");
        await page.Locator(".workspace").EvaluateAsync("node=>node.scrollTop=0");
        await Capture("pilot-complete-light-1600");
        Require(await page.Locator(".workspace-commandbar").EvaluateAsync<bool>("""
            node=>{const controls=[...node.querySelectorAll(':scope>.workspace-filter-cluster>*,:scope>.workspace-action-cluster>button,:scope>.workspace-action-cluster>.workspace-menu>summary')].map(n=>n.getBoundingClientRect());
              return controls.every(r=>Math.abs(r.height-controls[0].height)<1.1)&&controls.every((r,i)=>controls.every((s,j)=>i===j||r.right<=s.left+1||s.right<=r.left+1||r.bottom<=s.top+1||s.bottom<=r.top+1));}
            """), "Pilotage commands have equal heights and non-overlapping hit targets.");
        var before = dashboardGets.Count;
        await page.Locator("[data-filter-teams]").ClickAsync();
        await Picker().GetByRole(AriaRole.Checkbox, new() { Name = "Équipe 1", Exact = true }).CheckAsync();
        await Picker().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(dashboardGets.Count == before && await Stock().InnerTextAsync() == "168"
            && await page.Locator("[data-filter-teams]").EvaluateAsync<bool>("node=>document.activeElement===node"),
            "Cancelling a draft team choice leaves figures unchanged and returns keyboard focus to its trigger.");
        await page.Locator("[data-filter-teams]").ClickAsync();
        await Picker().GetByRole(AriaRole.Checkbox, new() { Name = "Équipe 1", Exact = true }).CheckAsync();
        var search = Picker().GetByRole(AriaRole.Searchbox, new() { Name = "Rechercher une équipe", Exact = true });
        await search.FillAsync("equipe 2");
        Require(await Picker().GetByRole(AriaRole.Checkbox).CountAsync() == 1
            && await Picker().GetByRole(AriaRole.Checkbox, new() { Name = "Équipe 2", Exact = true }).IsVisibleAsync(),
            "The cumulative team picker searches accent-insensitively while preserving hidden selections.");
        await Picker().GetByRole(AriaRole.Checkbox, new() { Name = "Équipe 2", Exact = true }).CheckAsync();
        await search.FillAsync("");
        Require(await Picker().GetByRole(AriaRole.Checkbox, new() { Checked = true }).CountAsync() == 2 && dashboardGets.Count == before
            && await Picker().GetByRole(AriaRole.Checkbox).CountAsync() == 6,
            "Each team and its equivalent raw group share one choice; two checked teams remain a draft until Apply.");
        await Capture("pilot-team-picker");
        await Picker().GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await StockValue("56"); await Settled();
        Require(dashboardGets.Count == before + 1 && (await page.Locator(".workspace-scope-summary").InnerTextAsync()).Contains("Équipe 1 + Équipe 2", StringComparison.Ordinal)
            && (await page.Locator("[data-widget-id=business-pilotage-0] .analytics-metric-value").InnerTextAsync()).Trim() == "56",
            "One recalculation applies the same deduplicated 56-ticket perimeter to the weather and analytical stock.");
        await Stock().ClickAsync(); var rows = page.Locator("dialog .analytics-row-dialog");
        await WaitForPageAsync(page, "document.querySelectorAll('dialog .analytics-row-dialog tbody tr').length===50");
        async Task<int[]> RowIds() => await rows.Locator("tbody tr td:nth-child(2)").EvaluateAllAsync<int[]>("cells=>cells.map(cell=>Number(cell.textContent.trim()))");
        var first = await RowIds();
        Require(await rows.Locator("tbody tr").EvaluateAllAsync<bool>("nodes=>nodes.every(node=>node.getBoundingClientRect().height<110)")
            && await rows.Locator("th").EvaluateAllAsync<bool>("nodes=>nodes.every(node=>node.getBoundingClientRect().height<90)"),
            "Raw-result previews keep bounded row and heading heights for all source fields.");
        await Capture("pilot-source-preview");
        await rows.GetByRole(AriaRole.Button, new() { Name = "Suivant", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelectorAll('dialog .analytics-row-dialog tbody tr').length===6");
        var ids = first.Concat(await RowIds()).ToArray();
        Require(ids.SequenceEqual(Enumerable.Range(1, 168).Where(id => (id - 1) % 6 < 2)) && ids.Distinct().Count() == 56,
            "The drilldown pages through all 56 exact source IDs once for the two unique team choices.");
        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".pilot-section-index").GetByRole(AriaRole.Button, new() { Name = "Suivre la contribution", Exact = true }).ClickAsync();
        Require(await Stock().InnerTextAsync() == "56" && dashboardGets.Count == before + 1,
            "Moving through the analytical sections keeps the common perimeter without replacing the workspace or recalculating.");
        await Navigate("home"); await Navigate("pilot"); await StockValue("56"); await Settled();
        Require((await page.Locator(".workspace-scope-summary").InnerTextAsync()).Contains("Équipe 1 + Équipe 2", StringComparison.Ordinal),
            "Returning to Pilotage restores the unified scope for the current simulation.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Réinitialiser", Exact = true }).ClickAsync(); await StockValue("168"); await Settled();
        Require((await page.Locator(".workspace-scope-summary").InnerTextAsync()).Contains("Toutes les équipes", StringComparison.Ordinal),
            "Reset restores the full population for every section together.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Filtres", Exact = true }).ClickAsync();
        var filters = page.GetByRole(AriaRole.Dialog, new() { Name = "Périmètre commun à toutes les analyses", Exact = true });
        var primaryFilters = filters.Locator(".analytics-filter-editor").First;
        await primaryFilters.GetByRole(AriaRole.Button, new() { Name = "+ Condition", Exact = true }).ClickAsync();
        await primaryFilters.GetByLabel("Source", new() { Exact = true }).SelectOptionAsync("status");
        await primaryFilters.GetByLabel("Valeurs acceptées · une par ligne", new() { Exact = true }).FillAsync("Nouveau");
        await filters.GetByLabel("Nom du contexte à conserver (facultatif)", new() { Exact = true }).FillAsync("Nouveaux à prendre en charge");
        await filters.Locator("details>summary").ClickAsync();
        await filters.GetByRole(AriaRole.Checkbox, new() { Name = "Comparer avec un second périmètre indépendant", Exact = true }).CheckAsync();
        await filters.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync(); await StockValue("42"); await Settled();
        await Navigate("home"); await Navigate("pilot"); await StockValue("42"); await Settled();
        Require(await page.GetByRole(AriaRole.Button, new() { Name = "Filtres · 1", Exact = true }).CountAsync() == 1
            && await page.GetByRole(AriaRole.Heading, new() { Name = "Comparer les périmètres", Exact = true }).CountAsync() == 1,
            "A saved composition filter remains visible after navigation, and the independent comparison appears in the new workspace.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Filtres · 1", Exact = true }).ClickAsync();
        Require(await filters.GetByLabel("Valeurs acceptées · une par ligne", new() { Exact = true }).InputValueAsync() == "Nouveau"
            && await filters.GetByLabel("Contexte enregistré", new() { Exact = true }).Locator("option").CountAsync() == 2,
            "The editor exposes the actual persistent filter and the named saved context instead of leaving hidden constraints active.");
        await filters.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Réinitialiser", Exact = true }).ClickAsync(); await StockValue("168"); await Settled();
        Require((await Call(app, "dashboard.layout.get", new { businessView = "workspace" })).GetProperty("boards")[0].GetProperty("filters").GetArrayLength() == 0,
            "Reset removes persistent composition filters from storage as well as transient scope controls.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Composer", Exact = true }).ClickAsync();
        await page.Locator(".business-weather-hero").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        var widgetEditor = page.GetByRole(AriaRole.Dialog, new() { Name = "Configurer le widget", Exact = true });
        await widgetEditor.GetByLabel("Seuil de vigilance", new() { Exact = true }).FillAsync("20");
        await widgetEditor.GetByLabel("Seuil critique", new() { Exact = true }).FillAsync("10");
        await widgetEditor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await widgetEditor.Locator(".notice.danger").WaitForAsync();
        await widgetEditor.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await page.Locator(".business-weather-hero").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        Require(await widgetEditor.GetByLabel("Seuil de vigilance", new() { Exact = true }).InputValueAsync() == "180"
            && await widgetEditor.GetByLabel("Seuil critique", new() { Exact = true }).InputValueAsync() == "200",
            "A refused widget save leaves no invalid threshold in memory when the editor is reopened.");
        await widgetEditor.GetByRole(AriaRole.Checkbox, new() { Name = "Hauteur automatique", Exact = true }).UncheckAsync();
        await widgetEditor.GetByLabel("Hauteur", new() { Exact = true }).FillAsync("360");
        await widgetEditor.GetByLabel("Largeur", new() { Exact = true }).SelectOptionAsync("12");
        await widgetEditor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await widgetEditor.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        Require(await page.Locator(".business-weather-hero").GetAttributeAsync("data-auto-height") == "false"
            && await page.Locator(".business-weather-hero").EvaluateAsync<bool>("node=>node.style.getPropertyValue('--widget-height')==='360px'")
            && await Stock().InnerTextAsync() == "168", "The new Composer persists a deliberate card height without changing the calculated stock.");
        var storedWorkspace = (await Call(app, "dashboard.layout.get", new { businessView = "workspace" })).GetProperty("boards")[0];
        var mainWeatherId = await page.Locator(".business-weather-hero").GetAttributeAsync("data-widget-id");
        var savedWeather = storedWorkspace.GetProperty("widgets").EnumerateArray().Single(item => item.GetProperty("id").GetString() == mainWeatherId);
        Require(savedWeather.GetProperty("warningValue").GetDouble() == 180 && savedWeather.GetProperty("criticalValue").GetDouble() == 200
            && await page.Locator(".business-weather-card").CountAsync() == 14,
            "A later valid layout edit does not accidentally commit the rejected thresholds or duplicate the weather cards.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Terminer la composition", Exact = true }).ClickAsync();
        await Navigate("home"); await Navigate("pilot"); await Settled();
        Require(await page.Locator(".business-weather-hero").GetAttributeAsync("data-auto-height") == "false",
            "The personal composition survives leaving and reopening the new workspace.");
        host.NextSave = Path.Combine(_root, "pilot-composition.csv");
        async Task ExportCsv()
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Exporter", Exact = true }).ClickAsync();
            var export = page.GetByRole(AriaRole.Dialog, new() { Name = "Exporter ce périmètre", Exact = true });
            await export.GetByLabel("Format", new() { Exact = true }).SelectOptionAsync("csv");
            await export.GetByRole(AriaRole.Button, new() { Name = "Exporter", Exact = true }).ClickAsync();
        }
        await ExportCsv();
        await page.Locator(".pilot-workspace .analytics-error .notice.danger").WaitForAsync();
        Require(!File.Exists(host.NextSave)
            && (await page.Locator(".pilot-workspace .analytics-error").InnerTextAsync()).Contains("export séparé du profil habituel", StringComparison.Ordinal),
            "A protected simulation export is refused with a visible workspace error after the export dialog has closed.");
        var exportedCsv = _root + "-pilot-export.csv"; host.NextSave = exportedCsv;
        try
        {
            await ExportCsv(); await Until(() => File.Exists(exportedCsv)); await Settled();
            Require(new FileInfo(exportedCsv).Length > 500,
                "The workspace exports a real analytical CSV outside the protected profile through the existing report service.");
        }
        finally { if (File.Exists(exportedCsv)) File.Delete(exportedCsv); }
        var beforeImport = await Call(app, "dashboard.layout.get", new { personal = false });
        var importedBoard = DashboardLayoutSettings.CreateBoard("Composition importée pour contrôle", "overview");
        var importedLayout = new DashboardLayoutSettings { ActiveBoardId = importedBoard.Id, Boards = new() { importedBoard } };
        host.NextOpen = Path.Combine(_root, "composition-to-import.json");
        await File.WriteAllTextAsync(host.NextOpen, System.Text.Json.JsonSerializer.Serialize(importedLayout, PilotApplication.Json));
        await page.Locator(".workspace-menu>summary").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Importer une composition", Exact = true }).ClickAsync();
        var importPreview = page.GetByRole(AriaRole.Dialog, new() { Name = "Vérifier les compositions importées", Exact = true });
        await importPreview.WaitForAsync();
        Require((await Call(app, "dashboard.layout.get", new { personal = false })).GetProperty("boards").GetArrayLength() == beforeImport.GetProperty("boards").GetArrayLength(),
            "Importing a composition presents a genuine preview and does not mutate existing layouts before confirmation.");
        await importPreview.GetByRole(AriaRole.Button, new() { Name = "Ajouter à mes compositions", Exact = true }).ClickAsync();
        await importPreview.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        await WaitForPageAsync(page, "id=>document.querySelector('[aria-label=\"Composition du pilotage\"]')?.value===id", importedBoard.Id);
        var afterImport = await Call(app, "dashboard.layout.get", new { personal = false });
        Require(afterImport.GetProperty("boards").GetArrayLength() == beforeImport.GetProperty("boards").GetArrayLength() + 1
            && beforeImport.GetProperty("boards").EnumerateArray().All(old => afterImport.GetProperty("boards").EnumerateArray().Any(current => current.GetProperty("id").GetString() == old.GetProperty("id").GetString())),
            "The new import action appends a selected composition while retaining every pre-existing board.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Composer", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Ajouter une section", Exact = true }).ClickAsync();
        var sectionEditor = page.GetByRole(AriaRole.Dialog, new() { Name = "Créer une section", Exact = true });
        await sectionEditor.GetByLabel("Titre de la section", new() { Exact = true }).FillAsync("Revue du service");
        await sectionEditor.GetByLabel("Description de la section", new() { Exact = true }).FillAsync("Section de contrôle conservée même vide.");
        await sectionEditor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer la section", Exact = true }).ClickAsync();
        await sectionEditor.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Settled();
        var savedComposition = (await Call(app, "dashboard.layout.get", new { personal = false })).GetProperty("boards").EnumerateArray().Single(item => item.GetProperty("id").GetString() == importedBoard.Id);
        Require(savedComposition.GetProperty("sections").EnumerateArray().Any(item => item.GetProperty("title").GetString() == "Revue du service")
            && await page.GetByRole(AriaRole.Heading, new() { Name = "Revue du service", Exact = true }).CountAsync() == 1,
            "Composer creates and persists an editable empty section through the real new workflow.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Terminer la composition", Exact = true }).ClickAsync();
        await page.GetByLabel("Composition du pilotage", new() { Exact = true }).SelectOptionAsync("service"); await StockValue("168"); await Settled();
        await Call(app, "app.appearance", new { theme = "dark" }); await delivery;
        await page.SetViewportSizeAsync(1280, 800); await page.Locator(".workspace").EvaluateAsync("node=>node.scrollTop=0");
        await Capture("pilot-complete-dark-1280");
        Require(await page.Locator(".page-content").EvaluateAsync<bool>("node=>node.scrollWidth<=node.clientWidth+1")
            && await page.Locator(".workspace-commandbar").EvaluateAsync<bool>("node=>node.scrollWidth<=node.clientWidth+1"),
            "The complete desktop workspace and its wrapped command bar remain within a 1280-pixel window.");
        Require(externalRequests == 0 && errors.IsEmpty, "The complete redesigned Pilotage operates on local data without external reads or JavaScript errors."
            + (errors.IsEmpty ? "" : "\n" + string.Join("\n", errors)));
        await delivery;
    }

    private static string CreateBusinessPreviewCsv()
    {
        var path = Path.Combine(_root, "synthetic-pilot-report.csv");
        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var rows = new List<string> { string.Join(';', LocalTestHeaders.Select(Csv)) };
        var now = DateTime.Today;
        for (var index = 1; index <= 176; index++)
        {
            var site = index % 7 == 0 ? "Lieu non renseigné" : index % 2 == 0 ? "Site exemple Nord" : "Site exemple Sud";
            var group = "Groupe " + ((index - 1) % 6 + 1);
            var row = new[] { index.ToString(CultureInfo.InvariantCulture), $"Ticket fictif {index:000} · {site}", index > 168 ? index % 2 == 0 ? "Résolu" : "Clos" : new[] { "Nouveau", "En cours (attribué)", "En cours (planifié)", "En attente" }[(index - 1) % 4],
                now.AddDays(-index % 10).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), now.AddDays(-index - 1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), index % 4 == 0 ? "Haute" : "Moyenne",
                "Demandeur fictif " + index % 5, "Agent fictif " + index % 4 + " (" + (101 + index % 4) + ")", "Catégorie " + ((index - 1) % 8 + 1) + " > Détail",
                "Agent fictif " + index % 4 + " (" + (101 + index % 4) + ")", index % 3 == 0 ? "Agent fictif 2 (103)" : "", "", "", index % 3 == 0 ? "Incident" : "Demande", group,
                (index + 1).ToString(CultureInfo.InvariantCulture), "1", index % 3 == 0 ? "2" : "1", "Description synthétique pour vérifier les rubriques natives." };
            rows.Add(string.Join(';', row.Select(Csv)));
        }
        File.WriteAllLines(path, rows, new UTF8Encoding(true)); return path;
    }
}
