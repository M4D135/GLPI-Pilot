using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckSourceCacheInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.OnboardingCompleted = true;
        var csv = Path.Combine(_root, "reunion-sans-original.csv");
        await File.WriteAllTextAsync(csv, "ID;Titre;Statut;Attribué à - Technicien\n1701;Incident réunion <b>littéral</b>;Nouveau;Technicien fictif\n1702;Demande réunion;En cours;Technicien fictif\n");
        await using (var initial = new PilotApplication(new TestHost(_root) { NextOpen = csv }, (_, _) => { }, settings))
        {
            await Call(initial, "records.import");
            Require((await Call(initial, "cache.state")).GetProperty("available").GetBoolean(), "A real import produces a persistent cache through the application operation.");
        }
        File.Delete(csv);
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
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page);
        await ClickNavigationAsync(page, "home"); await WaitForNavigationTargetAsync(page, "home");
        await page.Locator("[data-source-cache=true]").WaitForAsync();
        Require(app.IsCachedSource && app.IsFileSource && !app.IsTestMode && app.Result!.Rows.Count == 2 && !File.Exists(csv),
            "A fresh application restores the real imported rows with the original file absent.");
        Require(await page.GetByText("Cache local consulté", new() { Exact = true }).IsVisibleAsync()
            && (await page.Locator("[data-source-cache=true]").InnerTextAsync()).Contains("reunion-sans-original.csv", StringComparison.Ordinal),
            "The home screen identifies the dated offline source instead of implying a live connection.");
        await Button("Consulter le pilotage hors ligne").ClickAsync(); await WaitForNavigationTargetAsync(page, "pilot");
        Require(app.Result!.Rows.Count == 2 && !operations.Any(op => op.StartsWith("collect.", StringComparison.Ordinal)),
            "Opening cached analytics does not initiate a GLPI collection.");
        await ClickNavigationAsync(page, "records"); await WaitForNavigationTargetAsync(page, "records");
        await page.Locator(".tickets-record-grid tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        var cells = page.GetByRole(AriaRole.Dialog, new() { Name = "Données source · ligne 1", Exact = true }); await cells.WaitForAsync();
        Require((await cells.InnerTextAsync()).Contains("Incident réunion <b>littéral</b>", StringComparison.Ordinal)
            && await cells.Locator("b").CountAsync() == 0 && await page.Locator(".work-ticket-reader").CountAsync() == 0,
            "Cached file rows retain literal values without inventing ticket details.");
        await cells.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await ClickNavigationAsync(page, "data"); await WaitForNavigationTargetAsync(page, "data");
        await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        Require(await Button("Choisir les tickets à collecter").IsDisabledAsync(), "Offline consultation disables the online ticket-library refresh.");
        await OpenDemonstrationFromSettingsAsync(page); await WaitForNavigationTargetAsync(page, "home");
        Require(app.IsTestMode && await page.Locator("[data-source-cache=true]").CountAsync() == 0,
            "The synthetic demonstration does not expose the real last-import cache.");
        await Call(app, "records.test.exit"); await page.ReloadAsync(); await ReadyAsync(page);
        await ClickNavigationAsync(page, "data"); await WaitForNavigationTargetAsync(page, "data");
        await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        Require(app.IsCachedSource && (await Call(app, "cache.state")).GetProperty("rows").GetInt32() == 2,
            "Leaving the demo restores the offline source and preserves its real cache.");
        await Button("Effacer la copie hors ligne").ClickAsync();
        var clear = page.GetByRole(AriaRole.Dialog, new() { Name = "Effacer la copie hors ligne", Exact = true });
        await clear.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require((await Call(app, "cache.state")).GetProperty("available").GetBoolean(), "Cancelling cache deletion preserves the stored source.");
        await Button("Effacer la copie hors ligne").ClickAsync();
        await clear.GetByRole(AriaRole.Button, new() { Name = "Effacer la copie", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "!document.querySelector('dialog') && !document.querySelector('[data-source-cache=true] button')");
        Require(!(await Call(app, "cache.state")).GetProperty("available").GetBoolean() && app.Result!.Rows.Count == 2,
            "Confirmed cache deletion removes only the persistent copy while displayed data remain available.");
        Require(errors.IsEmpty && externalRequests == 0 && !operations.Any(op => op.StartsWith("collect.", StringComparison.Ordinal)),
            "The complete cache, demo and deletion journey has no JavaScript errors or network request.");
        var capture = Array.IndexOf(args, "--screenshots");
        if (capture >= 0 && capture + 1 < args.Length)
        {
            Directory.CreateDirectory(args[capture + 1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[capture + 1], "source-cache.png"), FullPage = true });
        }
        // Reproduce the reported path: restore a GLPI list whose detailed bodies were
        // already collected, then open it through Rechercher without a browser session.
        var sourceUrl = "https://offline-library.test/glpi/front/ticket.php";
        var observedAt = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var source = new ExtractionResult
        {
            SourceUrl = sourceUrl, ExtractedAt = observedAt, Complete = true,
            Headers = ["ID", "Titre", "Statut"], Rows = [["1701", "Fiche conservée", "Nouveau"], ["1702", "Contenu non recueilli", "Nouveau"]],
            Provenance = new() { SourceKind = "browser", RequestedUrl = sourceUrl, SearchVerified = true, ScopeVerified = true }
        };
        var interpretation = settings.Copy(); interpretation.PortalUrl = sourceUrl;
        interpretation.Dashboard.ColumnMappings = new() { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut" };
        new LastSourceCacheStore().Save(source, interpretation);
        var reference = new TicketReference("1701", "https://offline-library.test/glpi/front/ticket.form.php?id=1701");
        var store = new TicketLibraryStore(new LibraryPreferencesStore().Load().DatabasePath);
        await store.SaveDetailAsync(new() { Ticket = reference, Title = "Fiche conservée", LastModifiedRaw = "Version à confirmer" }, new()
        {
            Ticket = reference, Title = "Fiche conservée", ReadAt = observedAt,
            Sections = [new() { Name = "Description", Kind = "text", Data = new() { Headers = ["Contenu"], Rows = [["Description conservée <b>littérale</b>"]], Complete = true, ExtractedAt = observedAt } },
                new() { Name = "Suivis", Kind = "timeline", Data = new() { Headers = ["Type", "Date", "Auteur", "Texte"], Rows = [["Suivi", "18/11/2026 12:00", "Technicien fictif", "Diagnostic déjà collecté hors connexion"]], Complete = false, ExtractedAt = observedAt } }],
            Tabs = [new() { Key = "documents", Name = "Documents non recueillis", Loaded = false }]
        }, new() { Complete = false, TabsDiscovered = 1, TabsLoaded = 0, Limitations = ["Version à confirmer"] }, "existing-reader");
        var originalDocument = JsonSerializer.Serialize(await store.GetAsync(reference), PilotApplication.Json);
        await Call(app, "cache.restore"); await page.ReloadAsync(); await ReadyAsync(page);
        await ClickNavigationAsync(page, "records"); await WaitForNavigationTargetAsync(page, "records");
        await page.Locator(".tickets-record-grid tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        await page.Locator(".work-ticket-reader .tickets-journal-event").WaitForAsync();
        Require(await page.GetByText("Contenu local · hors connexion", new() { Exact = true }).IsVisibleAsync()
            && (await page.Locator(".tickets-journal").InnerTextAsync()).Contains("Diagnostic déjà collecté hors connexion", StringComparison.Ordinal),
            "A dated GLPI source opens its previously collected partial content directly from Rechercher.");
        Require(await Button("Actualiser la fiche").IsDisabledAsync() && await Button("Afficher dans GLPI").IsDisabledAsync()
            && await Button("Actions assistées").IsDisabledAsync(), "Offline detail exposes local observations while disabling online commands.");
        await Button("Consulter les observations").ClickAsync();
        var observations = page.GetByRole(AriaRole.Dialog, new() { Name = "Observations du ticket", Exact = true });
        await observations.WaitForAsync();
        Require(await observations.GetByRole(AriaRole.Button, new() { Name = "Documents non recueillis", Exact = true }).IsDisabledAsync(),
            "An uncollected tab cannot trigger a browser read while offline.");
        await observations.Locator("select").First.SelectOptionAsync(new SelectOptionValue { Label = "Description (1)" });
        await WaitForPageAsync(page, "document.querySelector('.work-section-content')?.textContent.includes('Description conservée <b>littérale</b>')");
        Require(await observations.Locator(".work-section-content b").CountAsync() == 0,
            "Saved detail text remains literal through the real production reader and C# bridge.");
        await observations.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        if (capture >= 0 && capture + 1 < args.Length)
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[capture + 1], "source-cache-ticket-details.png"), FullPage = true });
        await Button("Retour à la liste").ClickAsync();
        await page.Locator(".tickets-record-grid tbody tr").Nth(1).GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.work-ticket-reader')?.textContent.includes('Aucun contenu détaillé conservé')");
        Require(await Button("Consulter les observations").IsDisabledAsync()
            && !(await page.Locator(".work-ticket-reader").InnerTextAsync()).Contains("Diagnostic déjà collecté hors connexion", StringComparison.Ordinal),
            "A row with no saved body shows its own source facts and never reuses the preceding ticket body.");
        Require(originalDocument == JsonSerializer.Serialize(await store.GetAsync(reference), PilotApplication.Json)
            && errors.IsEmpty && externalRequests == 0 && !operations.Any(op => op is "workplace.ticket.read" or "workplace.ticket.tab" or "workplace.ticket.browser" || op.StartsWith("collect.", StringComparison.Ordinal)),
            "Offline local detail preserves all stored observations, never rereads the collection, and issues no network request.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}
