using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckSharedUpdatesInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var share = Path.Combine(_root, "Exports"); Directory.CreateDirectory(share);
        const string siteId = "11111111111111111111111111111111", retainedId = "22222222222222222222222222222222";
        var settings = new AppSettings { Theme = "light", ExportFolder = share };
        settings.Interface.OnboardingCompleted = true;
        settings.Cities.Sites = [new() { Id = siteId, Name = "Site local", Address = "Adresse locale à examiner" }, new() { Id = retainedId, Name = "Site local absent du patch" }];
        var incoming = settings.Cities.Copy(); incoming.Sites.RemoveAll(site => site.Id == retainedId);
        incoming.Sites[0].Name = "Site partagé <b>littéral</b>"; incoming.Sites[0].Address = "Adresse diffusée";
        for (var index = 0; index < 51; index++) incoming.Sites.Add(new() { Id = (index + 100).ToString("x32"), Name = $"Nouveau site {index:00}" });
        SitePatchFormat.Write(incoming, Path.Combine(share, SharedUpdateSource.SiteFile));
        var siteHash = UpdateService.HashFile(Path.Combine(share, SharedUpdateSource.SiteFile));
        await File.WriteAllTextAsync(Path.Combine(share, SharedUpdateSource.SiteHashes), siteHash + "  " + SharedUpdateSource.SiteFile + "\n");
        const string prod = "GLPI-Pilot_99.0.0_PROD_Windows-x64.zip", older = "GLPI-Pilot_98.0.0_PROD_Windows-x64.zip", dev = "GLPI-Pilot_100.0.0_DEV_Windows-x64.zip";
        foreach (var name in new[] { prod, older, dev }) await File.WriteAllTextAsync(Path.Combine(share, name), "synthetic-pack");
        var packHash = UpdateService.HashFile(Path.Combine(share, prod));
        await File.WriteAllTextAsync(Path.Combine(share, "SHA256SUMS.txt"), string.Join("\n", new[] { prod, older, dev }.Select(name => packHash + "  " + name)) + "\n");
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
        ILocator PatchCard() => page.Locator("section.card").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Patch des sites", Exact = true }) });
        async Task Idle() => await WaitForPageAsync(page, "!document.querySelector('.terrain-page[aria-busy=true]') && !document.querySelector('.terrain-page button[aria-busy=true]')");
        async Task GoUpdates() { await ClickNavigationAsync(page, "updates"); await WaitForNavigationTargetAsync(page, "updates"); await Idle(); }
        async Task ApplyPatch(bool accept)
        {
            await Button("Appliquer le patch de sites").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Appliquer le référentiel partagé", Exact = true });
            await dialog.GetByRole(AriaRole.Button, new() { Name = accept ? "Sauvegarder et appliquer" : "Annuler", Exact = true }).ClickAsync(); await Idle();
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page); await GoUpdates();
        Require(!operations.Any(op => op.StartsWith("update.shared.") || op.StartsWith("update.sites.")) && await Button("Copier et vérifier le pack réseau").IsDisabledAsync(),
            "Opening Updates never discovers a share or patch automatically and cannot prepare an unreviewed network pack.");
        Require(await Button("Exporter un patch de sites").CountAsync() == 0, "The Standard interface does not expose the Admin reference export command.");
        await Button("Choisir le dossier…").ClickAsync(); await Idle();
        await Button("Rechercher le pack réseau").ClickAsync(); await Idle();
        Require(await page.GetByText("Enregistrez le dossier modifié avant de consulter sa publication.", new() { Exact = true }).IsVisibleAsync()
            && !operations.Contains("update.shared.find"), "A picked but unsaved folder cannot trigger a lookup in the previously saved share.");
        await Button("Enregistrer le dossier").ClickAsync(); await Idle();
        Require((await Call(app, "update.state")).GetProperty("sharedFolder").GetString() == share, "Saving the selected folder persists the real backend setting.");
        await Button("Rechercher le pack réseau").ClickAsync(); await Idle();
        Require(await page.GetByText(prod, new() { Exact = true }).IsVisibleAsync() && await Button("Copier et vérifier le pack réseau").IsEnabledAsync()
            && await page.GetByText(dev, new() { Exact = true }).CountAsync() == 0, "Network discovery presents the newest PROD and ignores a newer DEV archive.");
        await page.GetByLabel("Dossier de diffusion", new() { Exact = true }).FillAsync(share + "-changed");
        Require(await Button("Copier et vérifier le pack réseau").IsDisabledAsync() && await page.GetByText(prod, new() { Exact = true }).CountAsync() == 0,
            "Editing the distribution folder immediately invalidates its displayed offer and copy action.");
        await page.GetByLabel("Dossier de diffusion", new() { Exact = true }).FillAsync(share);
        await Button("Rechercher le pack réseau").ClickAsync(); await Idle();
        await File.WriteAllTextAsync(Path.Combine(share, prod), "changed--pack");
        await Button("Copier et vérifier le pack réseau").ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Préparer la mise à jour réseau", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Copier et vérifier", Exact = true }).ClickAsync(); await Idle();
        Require((await page.Locator(".terrain-errors").InnerTextAsync()).Length > 0 && (await Call(app, "update.state")).GetProperty("prepared").ValueKind == JsonValueKind.Null,
            "A changed network archive is rejected by the real backend before any package can be installed.");
        await Button("Comparer le patch de sites").ClickAsync(); await Idle();
        Require(await PatchCard().GetByText("Site partagé <b>littéral</b>", new() { Exact = true }).IsVisibleAsync()
            && await PatchCard().GetByText("Adresse locale à examiner", new() { Exact = true }).IsVisibleAsync()
            && await PatchCard().GetByText("Adresse diffusée", new() { Exact = true }).IsVisibleAsync() && await PatchCard().Locator("b").CountAsync() == 0,
            "The patch review renders exact before/after values as text, including literal markup, without modifying the live reference.");
        Require(app.Settings.Cities.Sites.Single(site => site.Id == siteId).Name == "Site local" && await PatchCard().GetByRole(AriaRole.Button, new() { Name = "Suivant", Exact = true }).IsEnabledAsync(),
            "The reviewed reference remains unchanged and a full multi-page patch is available for inspection.");
        await PatchCard().GetByRole(AriaRole.Button, new() { Name = "Suivant", Exact = true }).ClickAsync(); await Idle();
        Require(await PatchCard().GetByText("Nouveau site 50", new() { Exact = true }).IsVisibleAsync(), "The final patch page is loaded through the backend token rather than truncating the review.");
        await ApplyPatch(false);
        Require(app.Settings.Cities.Sites.Count == 2 && app.Settings.Cities.Sites[0].Name == "Site local", "Cancelling the patch confirmation preserves every original reference value.");
        await ApplyPatch(true);
        var backupPath = await PatchCard().Locator("dt").Filter(new() { HasText = "Sauvegarde du référentiel précédent" }).Locator("xpath=following-sibling::dd[1]").InnerTextAsync();
        Require(app.Settings.Cities.Sites.Count == 53 && app.Settings.Cities.Sites.Single(site => site.Id == siteId).Name == "Site partagé <b>littéral</b>"
            && app.Settings.Cities.Sites.Any(site => site.Id == retainedId) && File.Exists(backupPath),
            "Confirmed patch application preserves stable IDs and absent local sites, applies all pages, and exposes a real backup.");
        var saved = AppSettings.Load();
        Require(saved.Cities.Sites.Count == 53 && saved.Cities.Sites.Single(site => site.Id == siteId).Address == "Adresse diffusée",
            "The confirmed reference is persisted by the application settings store.");
        await Button("Comparer le patch de sites").ClickAsync(); await Idle();
        Require(await Button("Appliquer le patch de sites").IsDisabledAsync() && app.Settings.Cities.Sites.Count == 53,
            "Replaying the same patch reports identical entries and offers no redundant application.");
        await page.SetViewportSizeAsync(1000, 800);
        Require(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "The network and patch review remain inside a compact viewport.");
        var capture = Array.IndexOf(args, "--screenshots");
        if (capture >= 0 && capture + 1 < args.Length)
        {
            Directory.CreateDirectory(args[capture + 1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[capture + 1], "shared-updates-sites.png"), FullPage = true });
        }
        await Call(app, "app.demo"); await GoUpdates();
        Require(await Button("Rechercher le pack réseau").IsDisabledAsync() && await Button("Comparer le patch de sites").IsDisabledAsync()
            && await page.GetByLabel("Dossier de diffusion", new() { Exact = true }).IsDisabledAsync(),
            "The demonstration explicitly disables real share and site-patch access.");
        Require(errors.IsEmpty && externalRequests == 0, "The complete network and reference journey has no JavaScript errors or external web requests.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}
