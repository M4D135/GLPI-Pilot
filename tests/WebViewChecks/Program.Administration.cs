using System.Collections.Concurrent;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckAdministrationInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(_root, "AdminUiLibrary"), SourceTimeZone = "UTC", DisplayName = "Responsable fictif" };
        new LibraryPreferencesStore().Save(preferences);
        var store = new TicketLibraryStore(preferences.DatabasePath);
        await store.ObserveSeedAsync(new TicketLibrarySeed { Ticket = new("73", "https://synthetic.invalid/front/ticket.form.php?id=73"), Title = "Ticket de maintenance fictif" });
        var team = new TeamWorkspaceStore(preferences.DatabasePath);
        await team.SaveAsync(new TeamWorkspaceItem { Id = Guid.NewGuid().ToString("N"), Kind = "note", Title = "Équipe conservée", Body = "Fictif" }, null);
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") }; settings.Interface.OnboardingCompleted = true;
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
        async Task Idle() => await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        async Task Connection()
        {
            await ClickNavigationAsync(page, "data"); await WaitForNavigationTargetAsync(page, "data");
            await page.Locator(".page-content [data-open-panel='connection']").First.ClickAsync();
            await page.Locator("dialog[data-workspace-panel='connection']").WaitForAsync(); await Idle();
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page); await Connection();
        Require(!operations.Any(op => op.StartsWith("admin.", StringComparison.Ordinal)), "Opening connection settings performs no administrative operation.");
        if (!AdministrationCapabilities.Enabled)
        {
            Require(await Button("Réinitialiser les tickets locaux").CountAsync() == 0 && await Button("Réinitialiser toute la base locale").CountAsync() == 0,
                "Standard has no local database maintenance controls in its actual settings panel.");
            await page.GetByLabel("Mode de partage", new() { Exact = true }).SelectOptionAsync("postgres");
            Require(await Button("Préparer la base distante").CountAsync() == 0, "Standard never exposes PostgreSQL provisioning even when that sharing mode is selected.");
            var refusal = await page.EvaluateAsync<string>("async()=>{try{await (await import('/js/bridge.js')).api.call('admin.local.preview',{});return 'accepted';}catch(error){return error.message;}}");
            Require(refusal.Contains("Standard", StringComparison.Ordinal) && (await store.GetStateAsync()).TicketCount == 1,
                "A direct administrative request through the real web bridge is refused without clearing Standard data.");
        }
        else
        {
            Require(await Button("Réinitialiser les tickets locaux").IsVisibleAsync() && await Button("Réinitialiser toute la base locale").IsVisibleAsync(),
                "The compiled Admin build displays both scoped reset actions in the actual settings panel.");
            await Button("Réinitialiser les tickets locaux").ClickAsync();
            var confirmation = page.GetByRole(AriaRole.Dialog, new() { Name = "Réinitialiser la base locale ?", Exact = true });
            await confirmation.WaitForAsync();
            Require((await confirmation.InnerTextAsync()).Contains("1 tickets", StringComparison.Ordinal) && (await store.GetStateAsync()).TicketCount == 1,
                "Reset previews the real ticket count and preserves data until the confirmation button is used.");
            await confirmation.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync(); await Idle();
            Require((await store.GetStateAsync()).TicketCount == 1 && !operations.Contains("admin.local.reset"), "Cancelling the reset dialog leaves the database intact and never calls the reset command.");
            await Button("Réinitialiser les tickets locaux").ClickAsync();
            await page.GetByRole(AriaRole.Dialog, new() { Name = "Réinitialiser la base locale ?", Exact = true })
                .GetByRole(AriaRole.Button, new() { Name = "Sauvegarder et réinitialiser", Exact = true }).ClickAsync();
            var completed = page.GetByRole(AriaRole.Dialog, new() { Name = "Base locale réinitialisée", Exact = true }); await completed.WaitForAsync(); await Idle();
            var backup = await completed.Locator("pre").InnerTextAsync();
            Require((await store.GetStateAsync()).TicketCount == 0 && (await new TicketLibraryStore(backup).GetStateAsync()).TicketCount == 1
                && (await Call(app, "library.get")).GetProperty("team").GetProperty("items").GetInt64() == 1,
                "The UI resets only tickets after creating a readable backup, and keeps the team notes.");
            await page.Keyboard.PressAsync("Escape");
            await page.GetByLabel("Mode de partage", new() { Exact = true }).SelectOptionAsync("postgres");
            await Button("Préparer la base distante").ClickAsync();
            var provision = page.GetByRole(AriaRole.Dialog, new() { Name = "Administration PostgreSQL", Exact = true }); await provision.WaitForAsync();
            await provision.GetByLabel("Compte administrateur PostgreSQL", new() { Exact = true }).FillAsync("admin-fictif");
            await provision.GetByLabel("Mot de passe administrateur", new() { Exact = true }).FillAsync("SECRET-FICTIF-NON-ENREGISTRE");
            await provision.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync(); await Idle();
            Require(!operations.Contains("admin.postgres.prepare") && await page.GetByLabel("Mot de passe administrateur", new() { Exact = true }).CountAsync() == 0
                && !File.ReadAllText(Path.Combine(AppPaths.DataRoot, "library.local.json")).Contains("SECRET-FICTIF", StringComparison.Ordinal),
                "Cancelling provisioning removes its temporary credential fields without calling the server or saving the administrative password.");
        }
        await page.Keyboard.PressAsync("Escape");
        await ClickNavigationAsync(page, "updates"); await WaitForNavigationTargetAsync(page, "updates"); await Idle();
        Require((await Button("Exporter un patch de sites").CountAsync() > 0) == AdministrationCapabilities.Enabled,
            "Reference patch export follows the compiled Admin capability in the update interface.");
        var capture = Array.IndexOf(args, "--screenshots");
        if (capture >= 0 && capture + 1 < args.Length)
        {
            Directory.CreateDirectory(args[capture + 1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[capture + 1], AdministrationCapabilities.Enabled ? "administration-admin.png" : "administration-standard.png"), FullPage = true });
        }
        Require(errors.IsEmpty && externalRequests == 0, "The administrative UI checks finish without JavaScript errors, external requests or real server changes.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}
