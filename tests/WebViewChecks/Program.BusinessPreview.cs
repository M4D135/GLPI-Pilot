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
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0;
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
        await Call(app, "dashboard.business.settings.save", new { business, baseFingerprint = bootstrap.GetProperty("fingerprint").GetString(),
            sites = new[] { new SiteDefinition { Name = "Site exemple Nord" }, new SiteDefinition { Name = "Site exemple Sud" }, new SiteDefinition { Name = "Site exemple sans ticket" } } });
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
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
        async Task Ready(string id) => await WaitForPageAsync(page, "id => document.querySelector('.page-content')?.dataset.page===id && !document.querySelector('#content').hasAttribute('aria-busy')", id);
        async Task Navigate(string id) { await page.Locator($".nav-item[data-page='{id}']").ClickAsync(); await Ready(id); }
        async Task Capture(string name)
        {
            var argument = Array.IndexOf(args, "--screenshots"); if (argument < 0 || argument + 1 >= args.Length) return;
            Directory.CreateDirectory(args[argument + 1]);
            await page.EvaluateAsync("document.querySelector('.workspace').scrollTop=0");
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + "-viewport.png") });
            var height = await page.EvaluateAsync<int>("Math.ceil(document.querySelector('.workspace').scrollHeight + 100)");
            await page.SetViewportSizeAsync(1380, Math.Min(12000, Math.Max(960, height)));
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png"), FullPage = true });
            await page.SetViewportSizeAsync(1380, 960);
        }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("body[data-ready=true]").WaitForAsync();
        await Navigate("pilot-weather");
        Require(await page.Locator(".business-weather-card").CountAsync() == 14
            && await page.Locator("[data-section-key=categories] .business-weather-card").CountAsync() == 8
            && await page.Locator("[data-section-key=teams] .business-weather-card").CountAsync() == 5,
            "The native weather displays the global stock, eight category cards and five visible team cards without importing a dashboard JSON.");
        Require(!await page.Locator(".business-dashboard-tools").EvaluateAsync<bool>("node=>node.open")
            && !await page.Locator(".analytics-reception-issue").First.IsVisibleAsync(),
            "Advanced dashboard tools are collapsed and a fully read simulation file does not create a false collection failure.");
        var global = await page.Locator(".business-weather-hero").BoundingBoxAsync();
        var categories = await page.Locator("[data-section-key=categories] .business-weather-card").First.BoundingBoxAsync();
        Require(global is { Y: < 420, Height: < 180 } && categories is { Y: < 650 },
            $"The weather summary starts before 420 px and its global card stays compact (y={global?.Y}, height={global?.Height}, categories={categories?.Y}).");
        Require(await page.Locator(".business-weather-card[data-weather-state=sun]").CountAsync() > 0
            && await page.Locator(".business-weather-card[data-weather-state=watch]").CountAsync() > 0
            && await page.Locator(".business-weather-card[data-weather-state=rain]").CountAsync() > 0,
            "Synthetic configured thresholds produce visible sun, watch and rain cards in the real renderer.");
        await page.Locator(".analytics-provenance>summary").ClickAsync();
        Require((await page.Locator(".analytics-provenance").InnerTextAsync()).Contains("couverture GLPI non vérifiée", StringComparison.Ordinal),
            "The compact provenance still states that the local simulation does not verify GLPI coverage.");
        await page.Locator(".analytics-provenance>summary").ClickAsync(); await Capture("native-weather-14-cards");
        await page.Locator(".business-dashboard-tools>summary").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Modifier le dashboard", Exact = true }).ClickAsync();
        Require(await page.Locator(".analytics-editor-toolbar").IsVisibleAsync(), "The collapsed tools still expose the complete native layout editor.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Quitter l’édition", Exact = true }).ClickAsync();
        await Navigate("pilot-summary");
        Require(await page.GetByRole(AriaRole.Heading, new() { Name = "10 premiers groupes par statut", Exact = true }).IsVisibleAsync()
            && await page.GetByRole(AriaRole.Heading, new() { Name = "8 principales catégories", Exact = true }).IsVisibleAsync()
            && await page.GetByRole(AriaRole.Heading, new() { Name = "En cours · attribués", Exact = true }).IsVisibleAsync()
            && await page.GetByRole(AriaRole.Heading, new() { Name = "En cours · planifiés", Exact = true }).IsVisibleAsync(),
            "Pilotage itself contains both original leading charts and separate assigned and planned stocks.");
        Require((await page.Locator(".business-stock-caption").InnerTextAsync()).StartsWith("8 tickets résolus ou clos", StringComparison.Ordinal), "The report identifies resolved and closed tickets excluded from active stock.");
        await Capture("native-pilotage-complete");
        await Navigate("pilot-participants");
        Require(await page.GetByRole(AriaRole.Columnheader, new() { Name = "Clé GLPI", Exact = true }).IsVisibleAsync(), "Agents actifs exposes the identity key as a visible column.");
        await Navigate("pilot-sites");
        await page.GetByRole(AriaRole.Button, new() { Name = "Tickets actifs · demandeur puis site", Exact = true }).ClickAsync();
        var rows = page.Locator("dialog .analytics-row-dialog"); await rows.WaitForAsync();
        Require(await rows.Locator("tbody tr").CountAsync() > 0, "Sites exposes the complete active-ticket detail sorted by requester and site.");
        await page.Keyboard.PressAsync("Escape");
        Require(externalRequests == 0 && errors.IsEmpty, "Native views render without an external request or a JavaScript exception."
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
