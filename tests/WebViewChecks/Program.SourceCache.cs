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
        Require(await Button("Actualiser le cache").IsDisabledAsync(), "Offline consultation disables the online ticket-library refresh.");
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
        await actualBridge.DisposeAsync(); await delivery;
    }
}
