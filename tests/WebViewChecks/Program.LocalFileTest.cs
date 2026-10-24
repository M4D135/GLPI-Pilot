using System.Collections.Concurrent;
using System.IO.Compression;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckLocalFileTestInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1380, Height = 960 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var offlineLibraryRequests = new ConcurrentQueue<string>(); var externalRequests = 0;
        var tileRequests = new ConcurrentQueue<string>(); var blockedTileRequests = new ConcurrentQueue<string>(); PilotApplication? mapApplication = null;
        var failMapTiles = false; var failedMapTileRequests = 0;
        var contextCalls = new ConcurrentQueue<(string Operation, bool Simulation, JsonElement Args)>();
        var personalSaves = new ConcurrentQueue<(string Notes, bool Simulation)>();
        (TaskCompletionSource Started, TaskCompletionSource Release)? heldPersonalSave = null;
        string? changeFileBeforeCommit = null;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (EmbeddedMapsService.IsTileUrl(route.Request.Url))
            {
                // Reproduce the native image gate, including late image jobs
                // which were queued before a layer or its lease was removed.
                if (mapApplication?.AllowsMapTileRequest(route.Request.Url) != true)
                { blockedTileRequests.Enqueue(route.Request.Url); await route.AbortAsync(); return; }
                tileRequests.Enqueue(route.Request.Url);
                if (failMapTiles) { Interlocked.Increment(ref failedMapTileRequests); await route.AbortAsync(); }
                else await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=") });
                return;
            }
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page, NextOpen = CreateLocalFileTestWorkbook() };
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), new AppSettings
        { Theme = "dark", PortalUrl = QualityFixture().SourceUrl, ExportFolder = Path.Combine(_root, "Exports") });
        mapApplication = app;
        app.LoadQualityFixture(QualityFixture()); var original = app.Result;
        var originalLibrary = (await Call(app, "library.get")).GetProperty("stats").GetProperty("ticketCount").GetInt32();
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op is "records.test.import" or "records.test.exit" or "app.demo" or "workplace.ticket.position" or "workplace.personal.save")
                contextCalls.Enqueue((op, app.IsTestMode, data.Clone()));
            if (op == "workplace.personal.save" && heldPersonalSave is { } held)
            {
                heldPersonalSave = null;
                held.Started.TrySetResult();
                // Delay admission, not the result: this remains the real C#
                // write and exposes a replacement sent before reader cleanup.
                await held.Release.Task.WaitAsync(ct);
            }
            if (op == "records.test.import" && data.TryGetProperty("token", out _) && !data.TryGetProperty("cancelled", out _)
                && changeFileBeforeCommit is { } changedPath)
            {
                changeFileBeforeCommit = null;
                await File.AppendAllTextAsync(changedPath, "\r\n", ct);
            }
            if (app.IsTestMode && op.StartsWith("library.", StringComparison.Ordinal)) offlineLibraryRequests.Enqueue(op);
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            var result = await app.ExecuteAsync(op, data, ct);
            if (op == "workplace.personal.save" && data.TryGetProperty("notes", out var notes))
                personalSaves.Enqueue((notes.GetString()!, app.IsTestMode));
            return result;
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        async Task Ready(string id) => await WaitForNavigationTargetAsync(page, id);
        async Task Capture(string name)
        {
            var argument = Array.IndexOf(args, "--screenshots"); if (argument < 0 || argument + 1 >= args.Length) return;
            Directory.CreateDirectory(args[argument + 1]); await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png"), FullPage = true });
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("body[data-ready=true]").WaitForAsync(); await Ready("home");
        async Task Navigate(string id)
        {
            await ClickNavigationAsync(page, id); await Ready(id);
        }
        await Button("Importer en simulation").ClickAsync();
        var chooser = page.GetByRole(AriaRole.Dialog, new() { Name = "Choisir le tableau Excel", Exact = true }); await chooser.WaitForAsync();
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original) && await chooser.Locator("select option").CountAsync() == 3,
            "The real Excel chooser lists visible sheets before changing the current observation.");
        Require(await chooser.GetByLabel("Feuille contenant les incidents", new() { Exact = true }).InputValueAsync() == "Tickets"
            && await chooser.GetByLabel("Numéro de la ligne d’en-têtes", new() { Exact = true }).InputValueAsync() == "4"
            && await chooser.GetByRole(AriaRole.Checkbox).IsCheckedAsync(),
            "The workbook inspection suggests Tickets/header 4 and offers its weather/team/site profile enabled by default.");
        await chooser.GetByLabel("Feuille contenant les incidents", new() { Exact = true }).SelectOptionAsync("Dashboard");
        Require(await chooser.GetByLabel("Numéro de la ligne d’en-têtes", new() { Exact = true }).InputValueAsync() == "",
            "Changing worksheets clears the header suggestion rather than imposing the Tickets row on another sheet.");
        await chooser.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "!document.querySelector('dialog') && !document.querySelector('.hero button[aria-busy=true]')");
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original), "Cancelling the Excel chooser keeps the previous observation and mode.");
        await Button("Importer en simulation").ClickAsync(); await chooser.WaitForAsync();
        await Capture("simulation-workbook-profile");
        await chooser.GetByRole(AriaRole.Button, new() { Name = "Ouvrir la simulation", Exact = true }).ClickAsync(); await Ready("pilot-weather");
        Require(app.IsTestMode && app.Result!.Rows.Count == 2 && app.Result.Rows[0][1] == "Incident <b>littéral</b> · Atelier Nord"
            && app.Result.Provenance.Method == "xlsm" && !app.Result.Complete && !app.Result.Provenance.SearchVerified
            && app.Result.Headers.Take(19).SequenceEqual(LocalTestHeaders),
            "The real chooser reads all nineteen XLSM source fields as literal values while keeping source verification unconfirmed.");
        Require(app.Settings.Cities.Sites.Any(site => site.Name == "Site fictif Nord" && site.Aliases.Contains("Atelier Nord"))
            && app.Settings.Dashboard.Business.WeatherRules.Single().Label == "Stock fictif",
            "The explicit workbook profile configures the isolated simulation directly, including site aliases and the weather rule.");
        Require(await page.Locator(".local-test-banner").IsVisibleAsync() && await page.Locator(".local-test-banner").InnerTextAsync() is { } summary
            && summary.Contains("Fichier entièrement lu", StringComparison.Ordinal) && summary.Contains("Simulation", StringComparison.Ordinal),
            "The persistent banner identifies the global simulation and its actual file receipt.");
        await page.Locator(".business-weather-hero .analytics-metric-value").WaitForAsync();
        Require((await page.Locator(".business-weather-hero .analytics-metric-value").InnerTextAsync()).Trim() == "2",
            "The imported rows appear immediately in the native weather overview.");
        await Capture("simulation-native-weather");

        // Keep the Excel-selection checks above, then exercise every screen on
        // a real CSV import, with the reference already configured locally.
        var workbookPath = host.NextOpen;
        var csvPath = Path.Combine(_root, "source-incidents.csv");
        ExportService.WriteCsv(new ExtractionResult { Headers = app.Result!.Headers.Take(19).ToList(),
            Rows = app.Result.Rows.Select(row => row.Take(19).ToArray()).ToList() }, csvPath);
        var simulationSettings = app.Settings.Copy();
        var mapCity = new CityDefinition { Name = "Commune fictive", Latitude = 44, Longitude = 5 };
        simulationSettings.Cities.Cities.Add(mapCity);
        simulationSettings.Cities.Sites.Single().CityId = mapCity.Id;
        simulationSettings.Cities.Sites.Single().Address = "12 rue de l’Essai, Commune fictive";
        simulationSettings.Cities.Sites.Single().Latitude = 44.1;
        simulationSettings.Cities.Sites.Single().Longitude = 5.1;
        await Call(app, "settings.save", new { revision = app.Revision, settings = simulationSettings });
        host.NextOpen = csvPath;
        await Navigate("home"); await Button("Importer en simulation").ClickAsync(); await Ready("pilot-weather");
        Require(app.IsTestMode && app.Result!.Provenance.Method == "csv" && app.Result.Headers.Take(19).SequenceEqual(LocalTestHeaders)
            && app.Result.Rows.Count == 2 && app.Settings.Cities.Sites.Single().Address == "12 rue de l’Essai, Commune fictive",
            "The real CSV chooser imports all nineteen fields and keeps the configured reference before the complete navigation journey.");

        var allPages = (await NavigationRouteIdsAsync(page)).Distinct().ToArray();
        var sections = await page.Locator(".pilot-block[data-section-key]").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.sectionKey)");
        Require(allPages.SequenceEqual(WorkspaceRoutes) && new[] { "weather", "flow", "attention", "teams", "analysis", "people", "actions" }.All(sections.Contains)
            && await page.Locator(".workspace-view").CountAsync() == 0,
            "The six new workspaces expose one complete Pilotage composition with every workbook analysis and no old-view tab strip.");
        foreach (var id in allPages)
        {
            await Navigate(id);
            var content = await page.Locator(".page-content").InnerTextAsync();
            Require(await page.Locator(".page-content .notice.danger").CountAsync() == 0
                && !content.Contains("Disponible après le mode test", StringComparison.Ordinal)
                && !content.Contains("Collecte en pause", StringComparison.Ordinal),
                $"The {id} page is usable after the real CSV import, without the former restricted-mode screen.");
        }

        await Navigate("settings");
        foreach (var label in new[] { "Assistant de langage", "Lectures ciblées", "Effectifs des sites" })
            await page.GetByRole(AriaRole.Checkbox, new() { Name = label, Exact = true }).CheckAsync();
        await Button("Appliquer les fonctions visibles").ClickAsync();
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        var contextRoutes = new[] { "queries", "history", "technicians", "planning", "assistant", "sources", "collect", "library", "quality", "mapping", "enrichment", "terrain-reference", "terrain-review", "populations", "tours", "automation", "updates", "help", "operations", "pilot-settings" };
        foreach (var route in contextRoutes)
        {
            await Navigate(route);
            var tool = page.Locator("dialog[open][data-workspace-panel]").Last;
            Require(await tool.Locator(".notice.danger").CountAsync() == 0 && await page.Locator(".workspace-view").CountAsync() == 0,
                "The " + route + " capability opens as a usable contextual tool of its new workspace after a real CSV import.");
        }

        await Navigate("terrain-map");
        await page.Locator(".maps-terrain-tools > summary").ClickAsync();
        await Button("Effectifs des sites · facultatif").ClickAsync();
        var terrainPanel = page.Locator("dialog.terrain-workspace-panel");
        await terrainPanel.Locator(".terrain-function-panel[data-ready=true]").WaitForAsync();
        Require(await terrainPanel.GetByRole(AriaRole.Button, new() { Name = "+ Période d’effectif", Exact = true }).IsEnabledAsync()
            && (await terrainPanel.InnerTextAsync()).Contains("Option facultative", StringComparison.Ordinal),
            "Optional site-population controls open inside Terrain with a usable form for the imported simulation, without an extra navigation space.");
        await terrainPanel.GetByRole(AriaRole.Button, new() { Name = "+ Période d’effectif", Exact = true }).ClickAsync();
        var populationEditor = page.GetByRole(AriaRole.Dialog, new() { Name = "Ajouter un effectif daté", Exact = true });
        await populationEditor.GetByLabel("Effectif entier positif", new() { Exact = true }).FillAsync("200");
        await populationEditor.GetByLabel("Référence de l’inventaire", new() { Exact = true }).FillAsync("Inventaire fictif de simulation");
        await populationEditor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await populationEditor.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await terrainPanel.GetByRole(AriaRole.Cell, new() { Name = "Inventaire fictif de simulation", Exact = true }).WaitForAsync();
        var populationState = await Call(app, "populations.list");
        var withRatio = await Call(app, "dashboard.get", new { revision = app.Revision, businessView = "sites" });
        var ratio = withRatio.GetProperty("snapshot").GetProperty("metrics").EnumerateArray().Single(metric => metric.GetProperty("key").GetString() == "site-per100-people");
        Require(populationState.GetProperty("rows")[0].GetProperty("count").GetInt32() == 200 && ratio.GetProperty("value").GetDouble() == .5
            && !app.Result!.Provenance.SearchVerified && !app.Result.Provenance.ScopeVerified,
            "Saving the real form computes one localized ticket per 200 people without certifying GLPI provenance.");
        await Capture("simulation-site-populations");
        await terrainPanel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await terrainPanel.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await WaitForPageAsync(page, "document.querySelector('.maps-inline-host .maps-canvas')?.dataset.tiles==='online'");
        Require(await page.Locator(".maps-inline-host .maps-workspace").CountAsync() == 1
            && await page.Locator(".page-content").GetAttributeAsync("data-page") == "terrain-map",
            "Closing the optional population panel returns directly to the central Terrain map.");

        var mapPreview = page.Locator(".maps-inline-host .maps-workspace");
        await Until(() => !tileRequests.IsEmpty);
        Require(app.AllowsMapTileRequest("https://tile.openstreetmap.org/0/0/0.png")
            && await mapPreview.Locator(".maps-marker").CountAsync() == 1 && await mapPreview.Locator("iframe").CountAsync() == 0 && context.Pages.Count == 1,
            "Opening Terrain after a real CSV import loads its detailed embedded map directly in simulation, without an iframe, extra window, API key or external navigation.");
        await mapPreview.Locator(".maps-place[data-located=true]").ClickAsync();
        Require(await mapPreview.Locator(".maps-place[aria-pressed=true]").CountAsync() == 1
            && (await mapPreview.Locator(".maps-popup").InnerTextAsync()).Contains("12 rue de l’Essai, Commune fictive", StringComparison.Ordinal),
            "Selecting the site synchronizes the map popup, reference address and scoped ticket list.");
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Fond hors ligne", Exact = true }).ClickAsync();
        var requestsBeforeFailure = tileRequests.Count;
        var failuresBefore = Volatile.Read(ref failedMapTileRequests);
        failMapTiles = true;
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Afficher les rues", Exact = true }).ClickAsync();
        // Reopening the layer may reuse decoded images for its original zoom.
        // Request a fresh zoom through the real control to exercise a network
        // failure instead of relying on the browser to refetch the same tiles.
        await mapPreview.Locator(".leaflet-control-zoom-in").ClickAsync();
        try
        {
            await WaitForPageAsync(page, "document.querySelector('.maps-inline-host .maps-state')?.textContent.includes('indisponible') === true");
            await Until(() => !app.AllowsMapTileRequest("https://tile.openstreetmap.org/0/0/0.png"));
        }
        catch
        {
            Console.WriteLine("MAP_TILE_FALLBACK " + JsonSerializer.Serialize(new
            {
                state = await mapPreview.Locator(".maps-state").TextContentAsync(),
                tiles = await mapPreview.Locator(".leaflet-tile").CountAsync(),
                requests = tileRequests.Count - requestsBeforeFailure,
                rejected = Volatile.Read(ref failedMapTileRequests) - failuresBefore,
                deniedByLease = blockedTileRequests.Count,
                leaseActive = app.AllowsMapTileRequest("https://tile.openstreetmap.org/0/0/0.png")
            }));
            await Capture("simulation-map-tile-fallback-failure");
            throw;
        }
        Require(Volatile.Read(ref failedMapTileRequests) > failuresBefore
            && await mapPreview.Locator(".maps-marker").CountAsync() == 1 && await mapPreview.Locator(".leaflet-tile").CountAsync() == 0,
            "A failed tile request automatically restores the offline vector map and preserves its real ticket marker.");
        failMapTiles = false;
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Afficher les rues", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.maps-state')?.textContent === 'Rues · OpenStreetMap'");
        Require(app.AllowsMapTileRequest(tileRequests.Last()) && tileRequests.All(EmbeddedMapsService.IsTileUrl)
            && !tileRequests.Any(value => value.Contains("Agent", StringComparison.Ordinal) || value.Contains("?", StringComparison.Ordinal))
            && await mapPreview.EvaluateAsync<bool>("node => { const land=node.querySelector('.leaflet-maps-land-pane'), tiles=node.querySelector('.leaflet-tile-pane'); return !!land?.querySelector('.maps-land') && Number(getComputedStyle(land).zIndex) < Number(getComputedStyle(tiles).zIndex); }"),
            "The streets layer appears above the local land and grants only standard OSM tile URLs, containing no ticket, agent or address text.");
        await mapPreview.Locator(".maps-attribution a").ClickAsync();
        Require(await mapPreview.Locator(".maps-sources").GetAttributeAsync("open") is not null && context.Pages.Count == 1,
            "Visible OpenStreetMap attribution opens its local licence notice and keeps the user inside the application.");
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Fond hors ligne", Exact = true }).ClickAsync();
        await Until(() => !app.AllowsMapTileRequest("https://tile.openstreetmap.org/0/0/0.png"));
        Require(await mapPreview.Locator(".leaflet-tile").CountAsync() == 0 && await mapPreview.Locator(".maps-marker").CountAsync() == 1,
            "Returning offline removes all remote tiles and revokes the lease while preserving the ticket marker and local map.");
        await Capture("simulation-central-terrain-map");
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Afficher les rues", Exact = true }).ClickAsync();
        await Navigate("home");
        await Until(() => !app.AllowsMapTileRequest("https://tile.openstreetmap.org/0/0/0.png"));
        Require(await page.Locator(".maps-canvas").CountAsync() == 0,
            "Leaving Terrain destroys the Leaflet surface and releases its native tile permission.");

        await CheckEmbeddedMapOverviewAsync(page, app, Navigate, Capture);
        await CheckEmbeddedMapRendererAsync(page);
        await Navigate("terrain-map");
        await page.Locator(".maps-terrain-tools > summary").ClickAsync();
        await Button("Panier et ordre des communes").ClickAsync();
        await terrainPanel.Locator(".terrain-function-panel[data-ready=true]").WaitForAsync();
        await terrainPanel.GetByRole(AriaRole.Button, new() { Name = "Carte · ordre des communes", Exact = true }).ClickAsync();
        var contextualMap = page.Locator("dialog.maps-dialog"); await contextualMap.WaitForAsync();
        Require((await contextualMap.InnerTextAsync()).Contains("Ordre des communes", StringComparison.Ordinal)
            && (await contextualMap.InnerTextAsync()).Contains("44, 5", StringComparison.Ordinal)
            && await contextualMap.Locator(".maps-marker").CountAsync() == 1,
            "The municipality projection remains available from a focused Terrain panel with honest city-level map precision.");
        await contextualMap.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await terrainPanel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await terrainPanel.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Sélectionner le ticket 42", Exact = true }).CheckAsync();
        await Button("Préparer une tournée").ClickAsync();
        var tourEditor = page.GetByRole(AriaRole.Dialog, new() { Name = "Préparer une tournée", Exact = true });
        await tourEditor.GetByLabel("Nom de la tournée", new() { Exact = true }).FillAsync("Tournée cartographique fictive");
        await tourEditor.GetByRole(AriaRole.Button, new() { Name = "Voir l’aperçu", Exact = true }).ClickAsync();
        var tourPreview = page.GetByRole(AriaRole.Dialog, new() { Name = "Vérifier le brouillon de tournée", Exact = true }); await tourPreview.WaitForAsync();
        var beforeMapApply = await Call(app, "terrain.tours.state");
        Require(beforeMapApply.GetProperty("draft").ValueKind == JsonValueKind.Null && (await tourPreview.InnerTextAsync()).Contains("1 ticket", StringComparison.Ordinal),
            "The real map selection opens a stop preview before creating any planning draft.");
        await tourPreview.GetByRole(AriaRole.Button, new() { Name = "Créer le brouillon", Exact = true }).ClickAsync();
        await terrainPanel.Locator(".terrain-function-panel[data-terrain-panel=planning][data-ready=true]").WaitForAsync();
        Require(await terrainPanel.Locator(".terrain-tabs button").CountAsync() == 0
            && (await terrainPanel.InnerTextAsync()).Contains("Tournée cartographique fictive", StringComparison.Ordinal)
            && await page.Locator(".page-content").GetAttributeAsync("data-page") == "terrain-map",
            "Confirming the map preview opens its functional planning panel inside Terrain without restoring legacy navigation tabs.");
        await terrainPanel.GetByRole(AriaRole.Button, new() { Name = "Carte de la tournée", Exact = true }).ClickAsync(); await contextualMap.WaitForAsync();
        Require((await contextualMap.InnerTextAsync()).Contains("12 rue de l’Essai, Commune fictive", StringComparison.Ordinal)
            && !(await contextualMap.InnerTextAsync()).Contains("Agent fictif", StringComparison.Ordinal),
            "The contextual tour map reuses its ordered site coordinates and address inside the main application.");
        await Capture("simulation-embedded-tour-map");
        await contextualMap.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await terrainPanel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await terrainPanel.WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        await Navigate("records");
        var openRow = page.Locator(".page-content").GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true });
        Require(await openRow.CountAsync() == 2, "Each imported row provides an actual ticket reader in simulation.");
        await openRow.First.ClickAsync(); await page.Locator(".tickets-detail-pane .work-ticket-heading h2").WaitForAsync();
        Require((await page.Locator(".work-ticket-heading h2").InnerTextAsync()).Contains("#42", StringComparison.Ordinal)
            && await Button("Afficher la fiche simulée").IsEnabledAsync(),
            "Opening the first imported row reaches its usable simulated ticket reader.");
        await Button("Afficher la fiche simulée").ClickAsync();
        await Button("Actions assistées").ClickAsync();
        await Button("Inspecter les formulaires simulés").ClickAsync();
        var adapters = page.GetByLabel("Adaptateur de formulaire", new() { Exact = true });
        var statusKey = await adapters.Locator("option").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("statut", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).GetAttributeAsync("value");
        Require(!string.IsNullOrEmpty(statusKey), "A status change is exposed as a selectable simulation action.");
        await adapters.SelectOptionAsync(statusKey!);
        await page.GetByLabel("Statut simulé", new() { Exact = true }).SelectOptionAsync("5");
        await Button("Prévisualiser l’action…").ClickAsync();
        var preview = page.GetByRole(AriaRole.Dialog, new() { Name = "Relire la modification simulée", Exact = true }); await preview.WaitForAsync();
        Require(app.Result!.Rows[0][2] == "En attente", "Previewing a simulated status change leaves the row unchanged before confirmation.");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Appliquer dans la simulation…", Exact = true }).ClickAsync();
        var confirmation = page.GetByRole(AriaRole.Dialog, new() { Name = "Modifier le ticket simulé ?", Exact = true }); await confirmation.WaitForAsync();
        await confirmation.GetByRole(AriaRole.Button, new() { Name = "Appliquer la simulation", Exact = true }).ClickAsync();
        await Until(() => app.Result!.Rows[0][2] == "Résolu");
        await page.GetByText("Modification simulée appliquée", new() { Exact = true }).WaitForAsync();
        var actionsPanel = page.GetByRole(AriaRole.Dialog, new() { Name = "Actions assistées", Exact = true });
        await actionsPanel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await actionsPanel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await Button("Retour à la liste").ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.tickets-record-grid')?.textContent.includes('Résolu')");
        Require((await page.Locator(".tickets-record-grid").InnerTextAsync()).Contains("Résolu", StringComparison.Ordinal),
            "Confirming the simulation really updates the row and its integrated source list, and retains an explicit simulated receipt.");
        await Capture("simulation-applied-action");
        await Navigate("pilot-weather");
        Require((await page.Locator(".business-weather-hero .analytics-metric-value").InnerTextAsync()).Trim() == "1",
            "Returning to native weather recomputes the active total after the simulated ticket status changes.");
        await page.Locator(".business-weather-hero .analytics-metric-value").ClickAsync();
        var weatherRows = page.Locator("dialog").Filter(new() { Has = page.Locator(".analytics-row-dialog") }); await weatherRows.WaitForAsync();
        await weatherRows.GetByRole(AriaRole.Button, new() { Name = "Ticket", Exact = true }).ClickAsync(); await Ready("tickets");
        Require((await page.Locator(".work-ticket-heading h2").InnerTextAsync()).Contains("#43", StringComparison.Ordinal),
            "The weather total drills down to the remaining active row and opens its actual simulated ticket.");

        await Navigate("terrain-reference");
        await page.GetByLabel("Type", new() { Exact = true }).SelectOptionAsync("site");
        var siteRow = page.Locator(".terrain-body tbody tr").Filter(new() { HasText = "Site fictif Nord" });
        await siteRow.GetByRole(AriaRole.Button, new() { Name = "Modifier", Exact = true }).ClickAsync();
        var siteEditor = page.GetByRole(AriaRole.Dialog, new() { Name = "Modifier · Site", Exact = true }); await siteEditor.WaitForAsync();
        await siteEditor.GetByLabel("Nom", new() { Exact = true }).FillAsync("Site fictif Nord modifié");
        await siteEditor.GetByRole(AriaRole.Button, new() { Name = "Ajouter au brouillon", Exact = true }).ClickAsync();
        await siteEditor.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Button("Simuler et enregistrer le référentiel").ClickAsync();
        var impact = page.GetByRole(AriaRole.Dialog, new() { Name = "Impact sur le relevé complet", Exact = true }); await impact.WaitForAsync();
        await impact.GetByRole(AriaRole.Button, new() { Name = "Enregistrer et appliquer", Exact = true }).ClickAsync();
        await impact.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        Require(app.Settings.Cities.Sites.Any(site => site.Name == "Site fictif Nord modifié"),
            "Sites imported from Paramétrage remain editable and their changes apply to the simulation.");

        await Navigate("library");
        Require(await page.Locator(".library-result").CountAsync() == 2 && !offlineLibraryRequests.IsEmpty,
            "The real library is populated from the imported rows inside the isolated simulation.");
        await Navigate("data");
        await page.Locator(".page-content [data-open-panel=connection]").ClickAsync();
        await page.Locator("dialog[data-workspace-panel=connection]").WaitForAsync();
        await Button("Synchroniser le central simulé").ClickAsync();
        await page.Locator(".library-outcome[aria-label='Bilan de la bibliothèque']").WaitForAsync();
        Require(await page.Locator(".page-content .notice.danger").CountAsync() == 0,
            "The shared-library synchronization produces a visible local result through the simulation adapter.");
        var connectionPanel = page.Locator("dialog[data-workspace-panel=connection]");
        await connectionPanel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await connectionPanel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.Keyboard.PressAsync("Control+k");
        var palette = page.GetByRole(AriaRole.Dialog, new() { Name = "Rechercher un écran, une action ou un ticket", Exact = true }); await palette.WaitForAsync();
        await palette.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync("42");
        await palette.Locator("[data-ticket-key]").First.WaitForAsync();
        Require(await palette.Locator(".command-palette-scope").IsVisibleAsync()
            && await palette.GetByRole(AriaRole.Button, new() { Name = "Ouvrir la bibliothèque filtrée", Exact = true }).IsVisibleAsync(),
            "Ctrl+K searches actual simulation tickets and offers the same scoped library route as normal mode.");
        await palette.Locator("[data-ticket-key]").First.ClickAsync();
        var libraryTicket = page.Locator("dialog.library-ticket-modal"); await libraryTicket.WaitForAsync();
        await libraryTicket.Locator(".library-ticket-content").WaitForAsync();
        Require((await libraryTicket.InnerTextAsync()).Contains("Simulation", StringComparison.Ordinal),
            "A palette ticket opens the library reader with the simulation context retained.");
        await page.Keyboard.PressAsync("Escape"); await libraryTicket.WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        await Navigate("data");
        await Button("Connecter la source simulée").ClickAsync();
        await Button("Inspection et reprise").ClickAsync();
        var collectPanel = page.Locator("dialog[data-workspace-panel=collect]");
        await collectPanel.GetByRole(AriaRole.Button, new() { Name = "Détecter les tableaux", Exact = true }).ClickAsync();
        await collectPanel.GetByRole(AriaRole.Button, new() { Name = "Utiliser ce tableau", Exact = true }).ClickAsync();
        await collectPanel.GetByRole(AriaRole.Button, new() { Name = "Extraire cette page", Exact = true }).ClickAsync(); await Ready("records");
        Require(app.Result!.Rows.Count == 2 && app.Result.Rows[0][2] == "Résolu",
            "The full connect-inspect-select-collect workflow preserves the current simulation rows and their applied changes.");

        await Navigate("updates");
        var installed = (await Call(app, "update.state")).GetProperty("version").GetString();
        await Button("Utiliser le pack de démonstration").ClickAsync();
        await Button("Vérifier le ZIP local").ClickAsync();
        await Button("Simuler l’installation de cette version").ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Installer le pack vérifié", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Appliquer la simulation", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.terrain-status')?.textContent.includes('Installation simulée terminée')");
        var update = await Call(app, "update.state");
        Require(update.GetProperty("version").GetString() != installed && update.GetProperty("outcome").ValueKind == System.Text.Json.JsonValueKind.Object
            && await page.GetByRole(AriaRole.Heading, new() { Name = "Dernière opération", Exact = true }).IsVisibleAsync(),
            "The simulated installer changes its virtual version and redraws the outcome without closing the application.");
        await Capture("simulation-update-result");

        var updatePanel = page.Locator("dialog[data-workspace-panel=updates]");
        await updatePanel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await updatePanel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await PaletteAction("test-exit"); await Ready("pilot-weather");
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original) && !await page.Locator(".local-test-banner").IsVisibleAsync()
            && app.Result!.Rows[0][2] == "Nouveau" && !app.Settings.Cities.Sites.Any(site => site.Name.StartsWith("Site fictif Nord", StringComparison.Ordinal))
            && (await Call(app, "library.get")).GetProperty("stats").GetProperty("ticketCount").GetInt32() == originalLibrary,
            "Leaving the simulation restores the exact prior observation, sites and operational library after all those local mutations.");
        await Navigate("pilot-settings");
        host.NextOpen = workbookPath;
        var settingsBeforeProfile = System.Text.Json.JsonSerializer.Serialize(app.Settings, PilotApplication.Json);
        await Button("Reprendre les paramètres d’un classeur Excel").ClickAsync();
        var profilePreview = page.GetByRole(AriaRole.Dialog, new() { Name = "Vérifier les paramètres du classeur", Exact = true }); await profilePreview.WaitForAsync();
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original)
            && System.Text.Json.JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == settingsBeforeProfile
            && (await profilePreview.InnerTextAsync()).Contains("1 règles météo", StringComparison.Ordinal),
            "Previewing a workbook profile from native settings leaves the normal observation and its saved interpretation unchanged.");
        await profilePreview.GetByRole(AriaRole.Button, new() { Name = "Appliquer les paramètres", Exact = true }).ClickAsync();
        await profilePreview.WaitForAsync(new() { State = WaitForSelectorState.Hidden }); await Ready("pilot-settings");
        await Navigate("pilot-weather");
        var persisted = AppSettings.Load();
        Require(!app.IsTestMode && persisted.Dashboard.Business.WeatherRules.Single().Label == "Stock fictif"
            && persisted.Cities.Sites.Any(site => site.Name == "Site fictif Nord" && site.Aliases.Contains("Atelier Nord"))
            && app.Settings.Dashboard.Business.WeatherRules.Single().Label == "Stock fictif"
            && (await page.Locator(".business-weather-hero .analytics-metric-value").InnerTextAsync()).Trim() == "4",
            "Explicitly applying the workbook profile persists its weather/site settings in the normal workspace and recalculates weather from the original four rows.");
        await Capture("workbook-profile-applied-normal");
        await CheckNormalMapDefaultAsync(page, app, Navigate);

        async Task PaletteAction(string command)
        {
            await page.Keyboard.PressAsync("Control+k");
            var commands = page.Locator("dialog.command-palette"); await commands.WaitForAsync();
            await commands.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync(command switch { "test-file" => "Simuler avec un fichier", "test-exit" => "Quitter la simulation", _ => "Explorer la simulation" });
            await commands.Locator($"[data-command-id='action:{command}']").ClickAsync();
        }
        async Task BridgeIdle() => await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        async Task<string> AnnotateReader(string text, string readingFilter)
        {
            await Navigate("records");
            await page.Locator(".tickets-record-grid tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
            await page.Locator(".tickets-detail-pane .work-ticket-heading h2").WaitForAsync();
            await Button("Notes et rappels").ClickAsync();
            await page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync(text);
            await Button("Enregistrer mon travail").ClickAsync(); await BridgeIdle();
            var work = await Call(app, "workplace.get"); var key = work.GetProperty("activeTicketKey").GetString()!;
            if (work.GetProperty("openTickets").EnumerateArray().Single(ticket => ticket.GetProperty("key").GetString() == key).GetProperty("notes").GetString() != text)
                throw new InvalidOperationException("The real personal editor did not save the context-test note.");
            if (app.IsTestMode)
            {
                await Button("Consulter les observations").ClickAsync();
                await page.GetByPlaceholder("Filtrer cette rubrique", new() { Exact = true }).FillAsync(readingFilter);
                await CloseObservations();
            }
            return key;
        }
        async Task CloseObservations()
        {
            var observations = page.GetByRole(AriaRole.Dialog, new() { Name = "Observations du ticket", Exact = true });
            await observations.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
            await observations.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
        async Task<bool> TransitionWithPendingSave(string command, string note)
        {
            await Button("Notes et rappels").ClickAsync();
            await page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync(note);
            var held = (Started: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Release: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            heldPersonalSave = held;
            await Button("Enregistrer mon travail").ClickAsync();
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var oldSource = app.Result; var start = contextCalls.Count;
            try
            {
                await PaletteAction(command);
                await WaitForPageAsync(page, "document.querySelector('.workspace')?.inert === true");
                // The UI has begun the replacement, but its pending explicit
                // save must finish before any mutating replacement reaches C#.
                return ReferenceEquals(app.Result, oldSource) && !personalSaves.Any(value => value.Notes == note)
                    && !contextCalls.Skip(start).Any(value => value.Operation is "app.demo" or "records.test.exit"
                        || value.Operation == "records.test.import" && value.Args.TryGetProperty("token", out _) && !value.Args.TryGetProperty("cancelled", out _));
            }
            finally { held.Release.TrySetResult(); }
        }

        await AnnotateReader("Note habituelle à conserver", "");
        var normalWork = await File.ReadAllBytesAsync(Path.Combine(_root, "workplace.json"));
        host.NextOpen = csvPath;
        await PaletteAction("test-file"); await Ready("pilot-weather");
        await AnnotateReader("Annotation du premier fichier de simulation", "Position conservée après annulation");
        await page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync("Saisie personnelle non enregistrée à conserver");
        var sourceBeforeCancel = app.Result; var cancelStart = contextCalls.Count;
        await page.EvaluateAsync("window.__simulationReader=document.querySelector('.tickets-detail-pane')");
        host.NextOpen = null;
        await PaletteAction("test-file"); await BridgeIdle();
        var cancelPreservesDom = await page.EvaluateAsync<bool>("window.__simulationReader===document.querySelector('.tickets-detail-pane') && window.__simulationReader.isConnected");
        var cancelDidNotFlush = !contextCalls.Skip(cancelStart).Any(call => call.Operation is "workplace.ticket.position" or "workplace.personal.save");
        await Button("Notes et rappels").ClickAsync();
        Require(ReferenceEquals(app.Result, sourceBeforeCancel) && cancelPreservesDom && cancelDidNotFlush
            && await page.GetByLabel("Mes notes", new() { Exact = true }).InputValueAsync() == "Saisie personnelle non enregistrée à conserver",
            "Cancelling the CSV chooser keeps the same live ticket reader, its source and unsaved annotation; no save or cleanup runs and the editor remains usable.");

        await Button("Consulter les observations").ClickAsync();
        await page.GetByPlaceholder("Filtrer cette rubrique", new() { Exact = true }).FillAsync("Position conservée après refus");
        await CloseObservations();
        var changingCsv = Path.Combine(_root, "changing-source.csv"); File.Copy(csvPath, changingCsv);
        host.NextOpen = changingCsv; changeFileBeforeCommit = changingCsv;
        var waitedBeforeRejection = await TransitionWithPendingSave("test-file", "Note enregistrée avant le refus d’empreinte");
        await WaitForPageAsync(page, "document.querySelector('#notifications')?.textContent.includes('Le fichier a changé')");
        await Ready("records");
        await page.Locator(".tickets-record-grid tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        await Button("Consulter les observations").ClickAsync();
        Require(ReferenceEquals(app.Result, sourceBeforeCancel) && waitedBeforeRejection
            && personalSaves.Any(value => value is ("Note enregistrée avant le refus d’empreinte", true))
            && await page.GetByPlaceholder("Filtrer cette rubrique", new() { Exact = true }).InputValueAsync() == "Position conservée après refus"
            && (await File.ReadAllBytesAsync(Path.Combine(_root, "workplace.json"))).SequenceEqual(normalWork),
            "A changed CSV is rejected by the real fingerprint guard after waiting for the explicit note save; the same simulation is remounted and its reading position is usable without writing to the normal profile.");

        await page.GetByPlaceholder("Filtrer cette rubrique", new() { Exact = true }).FillAsync("Avant remplacement CSV");
        await CloseObservations(); host.NextOpen = csvPath;
        var importWaited = await TransitionWithPendingSave("test-file", "Note enregistrée avant remplacement CSV"); await Ready("pilot-weather");
        Require(app.IsTestMode && !ReferenceEquals(app.Result, sourceBeforeCancel) && importWaited
            && personalSaves.Any(value => value is ("Note enregistrée avant remplacement CSV", true))
            && (await File.ReadAllBytesAsync(Path.Combine(_root, "workplace.json"))).SequenceEqual(normalWork),
            "Replacing an annotated CSV simulation awaits its pending explicit save before committing the prepared import and preserves the original personal store byte for byte.");

        await AnnotateReader("Annotation avant démonstration", "Avant démonstration");
        var demoWaited = await TransitionWithPendingSave("demo", "Note enregistrée avant démonstration"); await Ready("pilot-weather");
        Require(app.IsTestMode && app.Result!.Rows.Count == 180 && demoWaited
            && personalSaves.Any(value => value is ("Note enregistrée avant démonstration", true))
            && (await File.ReadAllBytesAsync(Path.Combine(_root, "workplace.json"))).SequenceEqual(normalWork),
            "Starting the demonstration from an annotated simulation awaits the old explicit save and opens a new isolated fictitious source without changing real notes.");

        await AnnotateReader("Annotation dans la démonstration", "Avant sortie de simulation");
        var exitWaited = await TransitionWithPendingSave("test-exit", "Note enregistrée avant sortie de simulation"); await Ready("pilot-weather");
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original) && exitWaited
            && personalSaves.Any(value => value is ("Note enregistrée avant sortie de simulation", true))
            && (await File.ReadAllBytesAsync(Path.Combine(_root, "workplace.json"))).SequenceEqual(normalWork),
            "Leaving a live annotated reader waits for its explicit save inside the simulation before disposal and restores the exact original source and personal store.");
        Require(externalRequests == 0 && errors.IsEmpty, "The complete real CSV journey has no unauthorized external request or JavaScript error; explicitly requested map tiles were intercepted locally."
            + (errors.IsEmpty ? "" : "\n" + string.Join("\n", errors)));
        await delivery;
    }

    private static readonly string[] LocalTestHeaders =
    [
        "ID", "Titre", "Statut", "Dernière modification", "Date d'ouverture", "Priorité", "Demandeur - Demandeur", "Attribué à - Technicien",
        "Catégorie", "Suivis - Rédacteur", "Tâches - Rédacteur", "TTR + Progression", "Statistiques - Délai de prise en compte", "Type",
        "Attribué à - Groupe de techniciens", "Âge (jours)", "Nombre de groupes", "Nombre de rédacteurs", "Description"
    ];

    private static string CreateLocalFileTestWorkbook()
    {
        var path = Path.Combine(_root, "source-incidents.xlsm");
        using var output = File.Create(path); using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        void Add(string name, string text) { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static string Row(int number, params string[] values) => $"<row r='{number}'>" + string.Concat(values.Select((value, index) =>
            $"<c r='{(char)('A' + index)}{number}' t='inlineStr'><is><t>{SecurityElement.Escape(value)}</t></is></c>")) + "</row>";
        string Sheet(params string[] rows) => "<worksheet xmlns='" + ns + "'><sheetData>" + string.Concat(rows) + "</sheetData></worksheet>";
        var modified = DateTime.Today.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var opened = DateTime.Today.AddDays(-4).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        Add("xl/workbook.xml", "<workbook xmlns='" + ns + "' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Dashboard' sheetId='1' r:id='r1'/><sheet name='Tickets' sheetId='2' r:id='r2'/><sheet name='Paramétrage' sheetId='3' r:id='r3'/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Target='worksheets/sheet1.xml'/><Relationship Id='r2' Target='worksheets/sheet2.xml'/><Relationship Id='r3' Target='worksheets/sheet3.xml'/></Relationships>");
        Add("xl/worksheets/sheet1.xml", Sheet(Row(1, "Indicateur")));
        Add("xl/worksheets/sheet2.xml", Sheet(Row(1, "Liste source"), Row(4, LocalTestHeaders),
            Row(5, "42", "Incident <b>littéral</b> · Atelier Nord", "En attente", modified, opened, "Haute", "Demandeur fictif", "Agent fictif (7)", "Assistance > Poste", "Agent fictif (7)", "Agent fictif (7)", "25 %", "15 min", "Incident", "Groupe fictif terrain", "4", "1", "1", "Description <script>strictement littérale</script>"),
            Row(6, "43", "Demande synthétique", "Nouveau", modified, opened, "Moyenne", "Demandeur fictif", "", "Assistance", "", "", "0 %", "", "Demande", "Groupe fictif terrain", "4", "1", "0", "Description du second ticket")));
        Add("xl/worksheets/sheet3.xml", Sheet(Row(1, "Paramètres météo"), Row(6, "Fraîcheur", "48"),
            Row(16, "Périmètre", "Étiquette", "Valeurs", "Activée", "Orange", "Rouge", "Note"), Row(17, "Global", "Stock fictif", "", "Oui", "0", "3", "Règle synthétique de test"),
            Row(24, "Site affiché", "Variantes reconnues"), Row(25, "Site fictif Nord", "Atelier Nord;Antenne Nord")));
        Add("xl/vbaProject.bin", "MACRO SYNTHETIQUE NON EXECUTABLE : CETTE ENTREE NE DOIT PAS ETRE LUE");
        return path;
    }
}
