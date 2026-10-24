using System.Collections.Concurrent;
using System.IO.Compression;
using System.Globalization;
using System.Security;
using System.Text;
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
        var host = new TestHost(_root) { Page = page, NextOpen = CreateLocalFileTestWorkbook() };
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), new AppSettings { Theme = "dark", ExportFolder = Path.Combine(_root, "Exports") });
        app.LoadQualityFixture(QualityFixture()); var original = app.Result;
        var originalLibrary = (await Call(app, "library.get")).GetProperty("stats").GetProperty("ticketCount").GetInt32();
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (app.IsTestMode && op.StartsWith("library.", StringComparison.Ordinal)) offlineLibraryRequests.Enqueue(op);
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
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
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        async Task Ready(string id) => await WaitForPageAsync(page, "id => document.querySelector('.page-content')?.dataset.page===id && !document.querySelector('#content').hasAttribute('aria-busy')", id);
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
        await Call(app, "settings.save", new { revision = app.Revision, settings = simulationSettings });
        host.NextOpen = csvPath;
        await Navigate("home"); await Button("Importer en simulation").ClickAsync(); await Ready("pilot-weather");
        Require(app.IsTestMode && app.Result!.Provenance.Method == "csv" && app.Result.Headers.Take(19).SequenceEqual(LocalTestHeaders)
            && app.Result.Rows.Count == 2 && app.Settings.Cities.Sites.Single().Address == "12 rue de l’Essai, Commune fictive",
            "The real CSV chooser imports all nineteen fields and keeps the configured reference before the complete navigation journey.");

        var allPages = (await page.Locator(".nav-item[data-page]").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.page)")).Concat(OptionalNavigationRoutes).Distinct().ToArray();
        var businessPages = new[] { "pilot-weather", "pilot-summary", "pilot-groups", "pilot-categories", "pilot-oldest", "pilot-followup", "pilot-participants", "pilot-sites", "pilot-settings" };
        Require(businessPages.All(allPages.Contains), "Every workbook business section and its settings is a native navigation entry.");
        foreach (var id in allPages)
        {
            await Navigate(id);
            var content = await page.Locator(".page-content").InnerTextAsync();
            Require(await page.Locator(".page-content .notice.danger").CountAsync() == 0
                && !content.Contains("Disponible après le mode test", StringComparison.Ordinal)
                && !content.Contains("Collecte en pause", StringComparison.Ordinal),
                $"The {id} page is usable after the real CSV import, without the former restricted-mode screen.");
        }

        await Navigate("pilot-sites");
        await page.Locator(".business-dashboard-tools > summary").ClickAsync();
        await Button("Effectifs des sites (facultatif)").ClickAsync(); await Ready("populations");
        Require(await Button("+ Période d’effectif").IsEnabledAsync()
            && (await page.Locator(".page-content").InnerTextAsync()).Contains("Option facultative", StringComparison.Ordinal),
            "The optional site-population settings open from Sites with a usable form for an imported simulation.");
        await Button("+ Période d’effectif").ClickAsync();
        var populationEditor = page.GetByRole(AriaRole.Dialog, new() { Name = "Ajouter un effectif daté", Exact = true });
        await populationEditor.GetByLabel("Effectif entier positif", new() { Exact = true }).FillAsync("200");
        await populationEditor.GetByLabel("Référence de l’inventaire", new() { Exact = true }).FillAsync("Inventaire fictif de simulation");
        await populationEditor.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await populationEditor.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.GetByRole(AriaRole.Cell, new() { Name = "Inventaire fictif de simulation", Exact = true }).WaitForAsync();
        var populationState = await Call(app, "populations.list");
        var withRatio = await Call(app, "dashboard.get", new { revision = app.Revision, businessView = "sites" });
        var ratio = withRatio.GetProperty("snapshot").GetProperty("metrics").EnumerateArray().Single(metric => metric.GetProperty("key").GetString() == "site-per100-people");
        Require(populationState.GetProperty("rows")[0].GetProperty("count").GetInt32() == 200 && ratio.GetProperty("value").GetDouble() == .5
            && !app.Result!.Provenance.SearchVerified && !app.Result.Provenance.ScopeVerified,
            "Saving the real form computes one localized ticket per 200 people without certifying GLPI provenance.");
        await Capture("simulation-site-populations");
        await Button("Retour aux sites").ClickAsync(); await Ready("pilot-sites");
        Require(await page.Locator(".analytics-sites").CountAsync() > 0, "The optional population settings return directly to Sites.");

        await page.GetByRole(AriaRole.Button, new() { Name = "Google Maps · Site fictif Nord", Exact = true }).ClickAsync();
        var mapPreview = page.GetByRole(AriaRole.Dialog, new() { Name = "Google Maps · lieux et parcours", Exact = true });
        await mapPreview.WaitForAsync();
        Require(host.GoogleMapUrls.Count == 0 && host.GoogleMapFrames.Count == 0
            && await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Ouvrir Google Maps", Exact = true }).IsEnabledAsync()
            && await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Afficher la carte intégrée", Exact = true }).IsDisabledAsync(),
            "The Sites map preview stays local and offers a working no-key link while clearly requiring a key for the embedded map.");
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Ouvrir Google Maps", Exact = true }).ClickAsync();
        await Until(() => host.GoogleMapUrls.Count > 0);
        Require(host.GoogleMapUrls.Count == 1 && Uri.UnescapeDataString(host.GoogleMapUrls.Single()).Contains("12 rue de l’Essai, Commune fictive", StringComparison.Ordinal)
            && !host.GoogleMapUrls.Single().Contains("Agent", StringComparison.Ordinal),
            "The explicit map click delegates only the reference address from the CSV simulation to the native host.");
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Réglages Google Maps", Exact = true }).ClickAsync();
        var mapSettings = page.GetByRole(AriaRole.Dialog, new() { Name = "Réglages Google Maps", Exact = true });
        await mapSettings.GetByLabel("Clé Maps Embed API", new() { Exact = true }).FillAsync("SYNTHETIC_UI_MAP_KEY");
        Require(await mapSettings.GetByLabel("Mémoriser la clé pour ce compte Windows", new() { Exact = true }).IsDisabledAsync(),
            "A map key entered during simulation cannot be persisted into the normal Windows profile.");
        await mapSettings.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await mapSettings.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Afficher la carte intégrée", Exact = true }).ClickAsync();
        await Until(() => host.GoogleMapFrames.Count > 0);
        Require(host.GoogleMapFrames.Count == 1 && host.GoogleMapFrames.Single().Simulation && host.GoogleMapFrames.Single().EmbedUrl.Contains("/maps/embed/v1/place?", StringComparison.Ordinal),
            "The dedicated embedded-map request is created only after its explicit button and uses the documented API.");
        await Capture("simulation-google-maps-preview");
        await CheckGoogleMapFrameAsync(browser, web, host.GoogleMapFrames.Single());
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

        await Navigate("tours");
        await Button("Google Maps · ordre des communes").ClickAsync(); await mapPreview.WaitForAsync();
        Require((await mapPreview.InnerTextAsync()).Contains("Ordre des communes", StringComparison.Ordinal)
            && (await mapPreview.InnerTextAsync()).Contains("44,5", StringComparison.Ordinal),
            "The current city projection opens its own map preview with its explicit city-level precision.");
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await Call(app, "terrain.tours.select", new { revision = app.Revision, rows = new[] { 0 }, selected = true });
        await Call(app, "terrain.tours.create", new { revision = app.Revision, name = "Tournée cartographique fictive", date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await Navigate("home"); await Navigate("tours");
        await Button("Planning daté").ClickAsync(); await Button("Google Maps · tournée").ClickAsync(); await mapPreview.WaitForAsync();
        Require((await mapPreview.InnerTextAsync()).Contains("12 rue de l’Essai, Commune fictive", StringComparison.Ordinal)
            && !(await mapPreview.InnerTextAsync()).Contains("Agent fictif", StringComparison.Ordinal),
            "The real tour button opens its ordered site itinerary from the imported CSV without participant or ticket text.");
        await mapPreview.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

        await Navigate("records");
        var openRow = page.Locator(".page-content").GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true });
        Require(await openRow.CountAsync() == 2, "Each imported row provides an actual ticket reader in simulation.");
        await openRow.First.ClickAsync(); await Ready("tickets");
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
        await WaitForPageAsync(page, "document.querySelector('.work-ticket-browser')?.textContent.includes('Résolu')");
        Require((await page.Locator(".work-ticket-browser").InnerTextAsync()).Contains("Résolu", StringComparison.Ordinal),
            "Confirming the simulation really updates the row and the visible ticket list, and retains an explicit simulated receipt.");
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
        await Button("Synchroniser le central simulé").ClickAsync();
        await page.Locator(".library-outcome[aria-label='Bilan de la bibliothèque']").WaitForAsync();
        Require(await page.Locator(".page-content .notice.danger").CountAsync() == 0,
            "The shared-library synchronization produces a visible local result through the simulation adapter.");
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

        await Navigate("collect");
        await Button("Connecter la source simulée").ClickAsync();
        await page.Locator(".page-content").GetByRole(AriaRole.Button, new() { Name = "Inspecter", Exact = true }).ClickAsync();
        await page.Locator(".page-content").GetByRole(AriaRole.Button, new() { Name = "Choisir", Exact = true }).ClickAsync();
        await Button("Extraire cette page seulement").ClickAsync(); await Ready("records");
        Require(app.Result!.Rows.Count == 2 && app.Result.Rows[0][2] == "Résolu",
            "The full connect-inspect-select-collect workflow preserves the current simulation rows and their applied changes.");

        await Navigate("updates");
        var installed = (await Call(app, "update.state")).GetProperty("version").GetString();
        await Button("Utiliser le pack de démonstration").ClickAsync();
        await Button("Vérifier le ZIP local").ClickAsync();
        await Button("Simuler l’installation de cette version").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Appliquer la simulation", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.terrain-status')?.textContent.includes('Installation simulée terminée')");
        var update = await Call(app, "update.state");
        Require(update.GetProperty("version").GetString() != installed && update.GetProperty("outcome").ValueKind == System.Text.Json.JsonValueKind.Object
            && await page.GetByRole(AriaRole.Heading, new() { Name = "Dernière opération", Exact = true }).IsVisibleAsync(),
            "The simulated installer changes its virtual version and redraws the outcome without closing the application.");
        await Capture("simulation-update-result");

        await Button("Quitter la simulation").ClickAsync(); await Ready("pilot-weather");
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
        Require(externalRequests == 0 && errors.IsEmpty, "The complete real simulation journey completes without an external WebView request or JavaScript error."
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
