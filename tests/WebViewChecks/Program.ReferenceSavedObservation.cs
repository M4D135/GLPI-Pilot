using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckReferenceSavedObservationInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.OnboardingCompleted = true;
        settings.Cities = new() { Enabled = true };
        settings.Save();
        var tickets = Path.Combine(_root, "releve-conserve.csv");
        await File.WriteAllTextAsync(tickets, "ID;Titre;Statut;Attribué à - Technicien\n1701;Incident Site importé;Nouveau;Technicien fictif\n");
        string archiveId;
        await using (var initial = new PilotApplication(new TestHost(_root) { NextOpen = tickets }, (_, _) => { }, settings))
        {
            await Call(initial, "records.import");
            archiveId = initial.Snapshots.Save(initial.Result!, initial.EffectiveSettings, locations: initial.Locations).Id;
        }
        File.Delete(tickets);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var operations = new ConcurrentQueue<string>(); var externalRequests = 0;
        page.PageError += (_, error) => errors.Enqueue(error);
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404 }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            operations.Enqueue(op);
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
        ILocator Reference() => page.GetByRole(AriaRole.Dialog, new() { Name = "Référentiel des lieux", Exact = true });
        ILocator SaveReview() => page.GetByRole(AriaRole.Dialog, new() { Name = "Enregistrer le référentiel de travail", Exact = true });
        string PreservedReferenceNotice(string observation) => $"L’enregistrement de ce référentiel ne recalcule pas {observation}. Les cellules et décisions capturées à l’origine restent conservées. Le référentiel enregistré servira aux prochains imports et collectes et sera inclus dans votre configuration par défaut.";
        async Task Idle() => await WaitForPageAsync(page, "!document.querySelector('.terrain-page[aria-busy=true]')");
        async Task OpenReference()
        {
            await ClickNavigationAsync(page, "data"); await WaitForNavigationTargetAsync(page, "data");
            await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
            await page.Locator("button[data-open-panel=sites]").ClickAsync();
            await Reference().Locator("[data-terrain-panel=reference][data-ready=true]").WaitForAsync(); await Idle();
        }
        async Task CloseReference()
        {
            await Idle(); await Reference().Locator(":scope > .modal-heading > button").ClickAsync();
            await Reference().WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
        async Task ImportSite(string name)
        {
            var path = Path.Combine(_root, "referentiel.csv");
            await File.WriteAllTextAsync(path, "Type;Nom;Alias;Adresse\nSite;" + name + ";Alias " + name + ";Adresse fictive\n");
            host.NextOpen = path;
            await Reference().GetByRole(AriaRole.Button, new() { Name = "Importer un CSV…", Exact = true }).ClickAsync();
            var review = page.GetByRole(AriaRole.Dialog, new() { Name = "Comparatif d’import CSV", Exact = true });
            await review.WaitForAsync();
            await review.GetByRole(AriaRole.Button, new() { Name = "Fusionner dans le brouillon", Exact = true }).ClickAsync();
            await review.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle();
        }
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            Directory.CreateDirectory(args[index + 1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[index + 1], name + ".png"), FullPage = true });
        }
        string SavedFiles(string directory) => JsonSerializer.Serialize(Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => new { path, bytes = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }));
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page);
        Require(app.IsCachedSource && app.IsHistorical && app.Result!.Rows.Count == 1 && !File.Exists(tickets),
            "A real import restored after restart reproduces the saved observation that previously blocked reference imports.");
        var resultBefore = app.Result; var locationsBefore = app.Locations; var historicalBefore = app.HistoricalDocument;
        var effectiveBefore = JsonSerializer.Serialize(app.EffectiveSettings, PilotApplication.Json);
        var resultJson = JsonSerializer.Serialize(app.Result, PilotApplication.Json);
        var cacheBefore = SavedFiles(Path.Combine(_root, "LastSource")); var historyBefore = SavedFiles(Path.Combine(_root, "History"));
        await OpenReference();
        Require(await Reference().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le référentiel de travail…", Exact = true }).IsVisibleAsync()
            && (await Reference().InnerTextAsync()).Contains("Vous pouvez enregistrer le référentiel de travail", StringComparison.Ordinal)
            && (await Reference().InnerTextAsync()).Contains("L’enregistrement du référentiel ne recalcule pas ce relevé.", StringComparison.Ordinal)
            && (await Reference().InnerTextAsync()).Contains("Une correction manuelle reste possible pour les tickets du cache.", StringComparison.Ordinal)
            && !(await Reference().InnerTextAsync()).Contains("Fermez-la avant", StringComparison.Ordinal),
            "The cached observation exposes an enabled working-reference save and explains that the saved observation is preserved.");
        await ImportSite("Site importé");
        Require(app.Settings.Cities.Sites.Count == 0 && (await Call(app, "terrain.reference.state")).GetProperty("dirty").GetBoolean(),
            "The reference CSV merges only into the draft before explicit save.");
        await Reference().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le référentiel de travail…", Exact = true }).ClickAsync();
        await SaveReview().WaitForAsync();
        Require(await SaveReview().Locator(".notice.info").InnerTextAsync() == PreservedReferenceNotice("le relevé du cache local")
            && await SaveReview().Locator("table").CountAsync() == 0,
            "The confirmation explains the cache remains frozen without displaying a misleading empty localization comparison.");
        await SaveReview().GetByRole(AriaRole.Button, new() { Name = "Conserver le brouillon", Exact = true }).ClickAsync(); await Idle();
        Require(app.Settings.Cities.Sites.Count == 0 && (await Call(app, "terrain.reference.state")).GetProperty("dirty").GetBoolean(),
            "Cancelling the save retains the imported draft and does not modify the working reference.");
        await Reference().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le référentiel de travail…", Exact = true }).ClickAsync();
        await SaveReview().WaitForAsync(); await Capture("reference-cache-confirmation");
        await SaveReview().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le référentiel de travail", Exact = true }).ClickAsync();
        await SaveReview().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle();
        Require(app.Settings.Cities.Sites.Single().Name == "Site importé"
            && SettingsRecovery.Read(AppPaths.SettingsFile).Cities.Sites.Single().Name == "Site importé"
            && (await Reference().Locator(".terrain-status").InnerTextAsync()).Contains("Le relevé conservé reste inchangé", StringComparison.Ordinal),
            "Confirming saves the working reference on disk and reports preservation rather than recalculation.");
        Require(ReferenceEquals(app.Result, resultBefore) && ReferenceEquals(app.Locations, locationsBefore) && ReferenceEquals(app.HistoricalDocument, historicalBefore)
            && JsonSerializer.Serialize(app.EffectiveSettings, PilotApplication.Json) == effectiveBefore
            && JsonSerializer.Serialize(app.Result, PilotApplication.Json) == resultJson && app.IsCachedSource,
            "Saving the reference retains the exact displayed cached rows, location evidence and captured interpretation.");
        Require(SavedFiles(Path.Combine(_root, "LastSource")) == cacheBefore && SavedFiles(Path.Combine(_root, "History")) == historyBefore,
            "Saving from the cached observation leaves persistent cache and archive bytes unchanged.");
        await Capture("reference-cache-saved"); await CloseReference();
        var pack = Path.Combine(_root, "pack-configure"); Directory.CreateDirectory(pack); app.SetDeploymentFixture(pack, "9.9.9");
        await ClickNavigationAsync(page, "settings"); await WaitForNavigationTargetAsync(page, "settings");
        await page.Locator("[data-settings-nav=deploiement]").ClickAsync();
        await page.Locator(".deployment-settings").WaitForAsync();
        await Button("Définir comme configuration par défaut").ClickAsync();
        var confirmDefault = page.GetByRole(AriaRole.Dialog, new() { Name = "Configuration par défaut du pack", Exact = true });
        await confirmDefault.GetByRole(AriaRole.Button, new() { Name = "Enregistrer comme défaut", Exact = true }).ClickAsync();
        var defaultSaved = page.GetByRole(AriaRole.Dialog, new() { Name = "Configuration par défaut enregistrée", Exact = true }); await defaultSaved.WaitForAsync();
        var defaultPath = DeploymentConfigurationInitializer.DefaultPath(pack);
        Require(DeploymentConfigurationService.Read(defaultPath, _root).Configuration.Settings.Cities.Sites.Single().Name == "Site importé"
            && app.IsCachedSource && ReferenceEquals(app.Result, resultBefore),
            "Define as default includes the saved imported site while the cached observation stays open.");
        await defaultSaved.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        var recipient = Path.Combine(_root, "nouveau-profil");
        Require(DeploymentConfigurationInitializer.TryInitialize(pack, recipient, "9.9.9").SeedStatus == "applied"
            && SettingsRecovery.Read(Path.Combine(recipient, "settings.json")).Cities.Sites.Single().Name == "Site importé",
            "The prepared configuration supplies the imported site to a fresh recipient profile.");

        await Call(app, "history.open", new { id = archiveId }); await delivery; await page.ReloadAsync(); await ReadyAsync(page);
        Require(app.IsHistorical && !app.IsCachedSource, "A real saved archive is opened separately from the offline cache.");
        var archive = app.HistoricalDocument; var archivedRows = app.Result; var archiveSettings = JsonSerializer.Serialize(app.EffectiveSettings, PilotApplication.Json);
        await OpenReference(); await ImportSite("Site archive");
        await Reference().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le référentiel de travail…", Exact = true }).ClickAsync(); await SaveReview().WaitForAsync();
        Require(await SaveReview().Locator(".notice.info").InnerTextAsync() == PreservedReferenceNotice("l’archive ouverte")
            && await SaveReview().Locator("table").CountAsync() == 0,
            "The same confirmation identifies a saved archive accurately instead of calling every frozen observation a cache.");
        await SaveReview().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le référentiel de travail", Exact = true }).ClickAsync();
        await SaveReview().WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle();
        Require(app.Settings.Cities.Sites.Count == 2 && ReferenceEquals(app.HistoricalDocument, archive) && ReferenceEquals(app.Result, archivedRows)
            && JsonSerializer.Serialize(app.EffectiveSettings, PilotApplication.Json) == archiveSettings
            && SavedFiles(Path.Combine(_root, "History")) == historyBefore,
            "Saving a reference while an archive is open changes working settings only and preserves the archive and its stored bytes.");
        await CloseReference();
        await File.WriteAllTextAsync(tickets, "ID;Titre;Statut;Attribué à - Technicien\n1701;Incident Site importé;Nouveau;Technicien fictif\n");
        host.NextOpen = tickets; await Call(app, "records.import"); await delivery; await page.ReloadAsync(); await ReadyAsync(page);
        Require(!app.IsHistorical && !app.IsCachedSource && app.Locations!.Matches.Single().SiteName == "Site importé",
            "A subsequent real import uses the new working reference while the earlier observation remained frozen.");
        await OpenReference();
        Require(await Reference().GetByRole(AriaRole.Button, new() { Name = "Simuler et enregistrer le référentiel", Exact = true }).IsVisibleAsync(),
            "A current observation retains the original simulate-and-apply workflow.");
        await Reference().GetByRole(AriaRole.Button, new() { Name = "Simuler et enregistrer le référentiel", Exact = true }).ClickAsync();
        var liveReview = page.GetByRole(AriaRole.Dialog, new() { Name = "Impact sur le relevé complet", Exact = true }); await liveReview.WaitForAsync();
        Require(await liveReview.GetByRole(AriaRole.Button, new() { Name = "Enregistrer et appliquer", Exact = true }).IsVisibleAsync()
            && (await liveReview.InnerTextAsync()).Contains("1 lignes examinées", StringComparison.Ordinal),
            "The current-source preview continues to examine all source rows and offers application with recalculation.");
        await liveReview.GetByRole(AriaRole.Button, new() { Name = "Enregistrer et appliquer", Exact = true }).ClickAsync();
        await liveReview.WaitForAsync(new() { State = WaitForSelectorState.Detached }); await Idle();
        Require((await Reference().Locator(".terrain-status").InnerTextAsync()).Contains("relevé recalculé", StringComparison.Ordinal)
            && !app.IsHistorical && app.Locations!.Matches.Single().SiteName == "Site importé",
            "Applying a working reference to a current observation still recalculates and reports the current result.");
        Require(errors.IsEmpty && !operations.Any(op => op.StartsWith("collect.", StringComparison.Ordinal)),
            "The full cached/archive/current-reference journey has no JavaScript error and never starts collection.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}
