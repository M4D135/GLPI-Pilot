using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckLocationCorrectionInterfaceAsync(string[] args)
    {
        // Fictional reference: names, aliases and points have no organizational provenance.
        var city = new CityDefinition { Name = "Commune fictive Cobalt", Latitude = 10, Longitude = 20 };
        var site = new SiteDefinition { Name = "Atelier Cobalt", CityId = city.Id, Aliases = ["ATX", "Siège"], Address = "Adresse fictive du test", Latitude = 10.1, Longitude = 20.1 };
        var alternateCity = new CityDefinition { Name = "Commune fictive Ambre", Latitude = 11, Longitude = 21 };
        var alternate = new SiteDefinition { Name = "Annexe fictive", CityId = alternateCity.Id, Aliases = ["ANNEXE"], Latitude = 11.1, Longitude = 21.1 };
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports"), Cities = new CitySettings { Cities = [city, alternateCity], Sites = [site, alternate] } };
        settings.Interface.OnboardingCompleted = true;
        var source = Path.Combine(_root, "localisation-source.csv");
        await File.WriteAllTextAsync(source, "ID;Titre;Statut;Type;Attribué à - Technicien;Attribué à - Groupe de techniciens\n"
            + string.Join("\n", Enumerable.Range(1, 46).Select(i => $"{7000+i};ATX et ANNEXE - Ticket fictif {i};Nouveau;Incident;Technicien fictif;Support"))
            + "\n8001;ATX et ANNEXE - Demande hors filtre;Nouveau;Demande;Technicien fictif;Support\n8002;ATX et ANNEXE - Ticket clos;Clos;Incident;Technicien fictif;Support\n");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using (var ui = await LocationCorrectionUi.CreateAsync(browser, settings, source))
        {
            await ui.Navigate();
            await ui.Page.GetByLabel("Type de ticket", new() { Exact = true }).SelectOptionAsync("incident"); await ui.Idle();
            await ui.Page.GetByLabel("Rechercher dans les tickets du périmètre").FillAsync("ATX");
            await ui.Page.Locator(".maps-ticket-panel").GetByRole(AriaRole.Button, new() { Name = "Rechercher", Exact = true }).ClickAsync(); await ui.Idle();
            Require(await ui.Page.Locator(".maps-ticket-rows").GetAttributeAsync("data-total") == "46"
                && await ui.Page.Locator("[data-correct-row]").CountAsync() == 40, "Every displayed ambiguous row exposes its direct site correction while the 46-ticket filtered scope remains paginated.");
            await ui.Page.Locator("[data-correct-row='0']").ClickAsync(); await ui.Editor.WaitForAsync();
            await ui.FindSite("ATX", "Atelier Cobalt");
            Require((await ui.Editor.Locator(".maps-correction-picked").InnerTextAsync()).Contains("Commune fictive Cobalt", StringComparison.Ordinal)
                && await ui.Editor.GetByLabel("Motif de la correction", new() { Exact = true }).InputValueAsync() == "Lieu confirmé manuellement", "Searching the saved reference by ATX finds Atelier Cobalt and its municipality with a useful editable reason already filled in.");
            await ui.Capture(args, "location-correction-single");
            await Call(ui.App, "app.appearance", new { theme = "dark" }); await ui.Flush(); await ui.Capture(args, "location-correction-single-dark");
            await Call(ui.App, "app.appearance", new { theme = "light" }); await ui.Flush();
            await ui.Editor.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
            Require(!ui.Operations.Any(op => op == "terrain.correction.apply"), "Cancelling the integrated site editor performs no correction.");
            await ui.Page.GetByRole(AriaRole.Checkbox, new() { Name = "Sélectionner le ticket 7001", Exact = true }).CheckAsync(); await ui.Idle();
            await ui.Page.Locator(".maps-ticket-rows").GetByRole(AriaRole.Button, new() { Name = "Suivant", Exact = true }).ClickAsync(); await ui.Idle();
            await ui.Page.GetByRole(AriaRole.Checkbox, new() { Name = "Sélectionner le ticket 7046", Exact = true }).CheckAsync(); await ui.Idle();
            await ui.Page.Locator("[data-correct-selection]").ClickAsync(); await ui.Editor.WaitForAsync();
            await ui.Editor.Locator(".maps-correction-tickets summary").ClickAsync();
            Require(await ui.Editor.GetAttributeAsync("data-correction-count") == "2"
                && (await ui.Editor.Locator(".maps-correction-tickets").InnerTextAsync()).Contains("#7001", StringComparison.Ordinal)
                && (await ui.Editor.Locator(".maps-correction-tickets").InnerTextAsync()).Contains("#7046", StringComparison.Ordinal), "A correction prepared from checked tickets includes both pages and displays the exact two identities before any write.");
            await ui.FindSite("ATX", "Atelier Cobalt"); await ui.Save("Appliquer aux 2 tickets");
            Require(await ui.Page.GetByLabel("Type de ticket", new() { Exact = true }).InputValueAsync() == "incident"
                && await ui.Page.GetByLabel("Rechercher dans les tickets du périmètre").InputValueAsync() == "ATX"
                && (await ui.Page.Locator(".maps-selection-count").InnerTextAsync()).StartsWith("2 ", StringComparison.Ordinal)
                && (await ui.Page.Locator("[data-correct-row='45']").Locator("..").InnerTextAsync()).Contains("Correction manuelle", StringComparison.Ordinal), "The real C# batch correction immediately refreshes the list, retaining the ticket type, search, page and two-ticket selection.");
            var map = await Call(ui.App, "maps.workspace.get", new { type = "incident" });
            Require(map.GetProperty("locations").EnumerateArray().Any(location => location.GetProperty("count").GetInt32() == 2
                && location.GetProperty("place").GetProperty("label").GetString()!.Contains("Atelier Cobalt", StringComparison.Ordinal)), "The actual map groups exactly the two corrected tickets under Atelier Cobalt while the other ambiguous tickets remain separate.");
            await ui.Page.Locator("[data-correct-row='45']").ClickAsync(); await ui.FindSite("ANNEXE", "Annexe fictive");
            var prepared = await Call(ui.App, "terrain.correction.prepare", new { revision = ui.App.Revision, rows = new[] { 0 } });
            await Call(ui.App, "terrain.correction.apply", new { token = prepared.GetProperty("token").GetString(), siteId = site.Id, reason = "Concurrent explicit correction", remove = false });
            await ui.Editor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer la correction", Exact = true }).ClickAsync();
            await ui.Editor.Locator(".maps-correction-errors:not(:empty)").WaitForAsync();
            Require((await ui.Editor.Locator(".maps-correction-picked").InnerTextAsync()).Contains("Annexe fictive", StringComparison.Ordinal)
                && await ui.Editor.GetByLabel("Motif de la correction", new() { Exact = true }).InputValueAsync() == "Lieu confirmé manuellement", "An expired preparation is rejected by the real runtime without closing the editor or losing the selected site and reason.");
            await ui.Editor.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
            Require(ui.Errors.IsEmpty && ui.ExternalRequests == 0, "Single, batch, cancellation and stale correction flows produce no JavaScript error and no external request.");
        }
        File.Delete(source);
        await using (var ui = await LocationCorrectionUi.CreateAsync(browser))
        {
            await ui.Navigate();
            Require(ui.App.IsCachedSource && !File.Exists(source)
                && await ui.Page.Locator("[data-correct-row='0']").InnerTextAsync() == "Modifier la correction", "A fresh runtime and WebView restore the manual site from the offline cache after the original CSV has been removed.");
            await ui.Page.Locator("[data-correct-row='0']").ClickAsync(); await ui.Editor.WaitForAsync();
            await ui.Save("Retirer la correction");
            Require((await ui.Page.Locator("[data-correct-row='0']").Locator("..").InnerTextAsync()).Contains("Localisation ambiguë", StringComparison.Ordinal)
                && await ui.Page.Locator("[data-correct-row='0']").InnerTextAsync() == "Corriger le site", "Removing the correction offline restores that ticket's captured ambiguous decision and exposes the direct correction action again.");
            await ui.Page.Locator("[data-correct-row='1']").ClickAsync(); await ui.FindSite("ATX", "Atelier Cobalt"); await ui.Save("Enregistrer la correction");
            Require((await ui.Page.Locator("[data-correct-row='1']").Locator("..").InnerTextAsync()).Contains("Correction manuelle", StringComparison.Ordinal)
                && ui.App.IsCachedSource, "A new manual correction also succeeds offline without reopening the missing source file.");
            await ui.Capture(args, "location-correction-offline");
            var persisted = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !path.Contains("demo", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(path => path, File.ReadAllBytes);
            await Call(ui.App, "app.demo"); await ui.Flush(); await ui.UseLocalMap(); await ui.Page.ReloadAsync(); await ReadyAsync(ui.Page); await ui.Navigate();
            await ui.Page.Locator("[data-correct-row]").First.ClickAsync(); await ui.Editor.WaitForAsync();
            await ui.Editor.Locator("[data-correction-site]").First.ClickAsync(); await ui.Save("Enregistrer la correction");
            Require(ui.App.IsTestMode && persisted.All(pair => File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "The same correction editor works in the fictional demonstration while every existing normal-profile JSON document remains byte-for-byte unchanged.");
            await Call(ui.App, "records.test.exit"); await ui.Flush(); await ui.Page.ReloadAsync(); await ReadyAsync(ui.Page); await ui.Navigate();
            Require(ui.App.IsCachedSource && !ui.App.IsTestMode
                && (await ui.Page.Locator("[data-correct-row='1']").Locator("..").InnerTextAsync()).Contains("Correction manuelle", StringComparison.Ordinal), "Leaving the demonstration restores the genuine offline source and its normal-profile corrections.");
            var bootstrap = await Call(ui.App, "app.bootstrap");
            Require(bootstrap.GetProperty("session").GetProperty("administration").GetBoolean() == AdministrationCapabilities.Enabled
                && !ui.Operations.Any(op => op.StartsWith("admin.", StringComparison.Ordinal)), "Manual site correction works in the compiled Standard/Admin mode without granting or calling administrative operations.");
            Console.WriteLine("LOCATION DIAGNOSTICS: " + JsonSerializer.Serialize(new { errors = ui.Errors.ToArray(), externalRequests = ui.ExternalRequests }));
            Require(ui.Errors.IsEmpty && ui.ExternalRequests == 0, "Offline correction, removal, restart and isolated demonstration finish without JavaScript errors or external requests.");
        }
    }

    private sealed class LocationCorrectionUi : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task _delivery = Task.CompletedTask;
        private WebBridge _bridge = null!;
        private IBrowserContext _context = null!;
        public IPage Page { get; private set; } = null!;
        public PilotApplication App { get; private set; } = null!;
        public ConcurrentQueue<string> Errors { get; } = new();
        public ConcurrentQueue<string> Operations { get; } = new();
        public int ExternalRequests;
        public ILocator Editor => Page.Locator("dialog.maps-correction-dialog");
        public static async Task<LocationCorrectionUi> CreateAsync(IBrowser browser, AppSettings? settings = null, string? source = null)
        {
            var ui = new LocationCorrectionUi();
            // This fixture is an existing configured profile, as in the reported case.
            settings?.Save();
            ui._context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
            ui.Page = await ui._context.NewPageAsync(); ui.Page.SetDefaultTimeout(15000); ui.Page.PageError += (_, error) => ui.Errors.Enqueue(error);
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
            var host = new TestHost(_root) { Page = ui.Page, NextOpen = source };
            ui.App = new PilotApplication(host, (name, data) => ui._bridge?.Publish(name, data), settings);
            if (source is not null) await Call(ui.App, "records.import");
            ui._bridge = new WebBridge(ui.App.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
            {
                ui.Operations.Enqueue(op);
                if (op == "app.ready") return new { ready = true };
                if (op == "host.editor") { ui.App.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
                return await ui.App.ExecuteAsync(op, data, ct);
            }, ui.Post);
            await ui.Page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => ui._bridge.Receive(json));
            await ui.Page.AddInitScriptAsync("""
                (() => { for(const mode of ['normal','simulation']) localStorage.setItem(`glpi-pilot:view:3:${mode}:unselected:map`, JSON.stringify({viewport:{streets:false}}));
                  const listeners=new Set(); window.chrome||={}; window.chrome.webview={postMessage:message=>window.__pilotSend(JSON.stringify(message)),
                  addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},removeEventListener:(name,handler)=>listeners.delete(handler)};
                  window.__pilotDeliver=message=>{for(const handler of listeners)handler({data:message});};})();
                """);
            await ui.Page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(ui.Page);
            return ui;
        }
        private void Post(string json) { lock (_gate) _delivery = _delivery.ContinueWith(async _ => { if (!Page.IsClosed) await Page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap(); }
        public Task Flush() { lock (_gate) return _delivery; }
        public Task Idle() => WaitForPageAsync(Page, "async()=>!(await import('/js/bridge.js')).api.pending&&!document.querySelector('.terrain-map-page[aria-busy=true]')");
        public async Task UseLocalMap()
        {
            var current = await Call(App, "app.bootstrap");
            await Page.EvaluateAsync("async json=>{const state=JSON.parse(json),{readWorkspaceView,saveWorkspaceView}=await import('/js/work-context.js');const view=readWorkspaceView(state,'map');saveWorkspaceView(state,'map',{...view,viewport:{...view.viewport,streets:false}});}", current.GetRawText());
        }
        public async Task Navigate() { await UseLocalMap(); _bridge.Publish("app.navigate", new { page = "terrain-map" }); await Flush(); await WaitForNavigationTargetAsync(Page, "terrain-map"); await Page.Locator(".terrain-map-page[data-ready=true]").WaitForAsync(); await Idle(); }
        public async Task FindSite(string search, string name)
        {
            await Editor.WaitForAsync(); await Editor.GetByLabel("Rechercher un site", new() { Exact = true }).FillAsync(search);
            await Editor.GetByRole(AriaRole.Button, new() { Name = "Rechercher", Exact = true }).ClickAsync(); await Idle();
            await Editor.Locator("[data-correction-site]").Filter(new() { HasText = name }).ClickAsync();
        }
        public async Task Save(string label) { await Editor.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).ClickAsync(); await Editor.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle(); }
        public async Task Capture(string[] args, string name)
        { var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return; Directory.CreateDirectory(args[index + 1]); await Page.ScreenshotAsync(new() { Path = Path.Combine(args[index + 1], name + ".png"), FullPage = true }); }
        public async ValueTask DisposeAsync() { await Flush(); await _bridge.DisposeAsync(); await App.DisposeAsync(); await _context.CloseAsync(); }
    }
}
