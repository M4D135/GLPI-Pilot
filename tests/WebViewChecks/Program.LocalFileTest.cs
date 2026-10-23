using System.Collections.Concurrent;
using System.IO.Compression;
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
        await Call(app, "app.demo"); var original = app.Result;
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
        await Button("Importer un fichier de test").ClickAsync();
        var chooser = page.GetByRole(AriaRole.Dialog, new() { Name = "Choisir le tableau Excel", Exact = true }); await chooser.WaitForAsync();
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original) && await chooser.Locator("select option").CountAsync() == 2,
            "The real Excel chooser lists visible sheets before changing the current observation.");
        await chooser.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "!document.querySelector('dialog') && !document.querySelector('.hero button[aria-busy=true]')");
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original), "Cancelling the Excel chooser keeps the previous observation and mode.");
        await Button("Importer un fichier de test").ClickAsync(); await chooser.WaitForAsync();
        await chooser.GetByLabel("Feuille contenant les incidents", new() { Exact = true }).SelectOptionAsync("Incidents");
        await chooser.GetByLabel("Numéro de la ligne d’en-têtes", new() { Exact = true }).FillAsync("4");
        await Capture("offline-import-worksheet-choice");
        await chooser.GetByRole(AriaRole.Button, new() { Name = "Importer en mode test", Exact = true }).ClickAsync(); await Ready("records");
        Require(app.IsTestMode && app.Result!.Rows.Count == 2 && app.Result.Rows[0][1] == "Incident <b>littéral</b>" && app.Result.Provenance.Method == "xlsm",
            "The real chooser reads the selected XLSM sheet and header row without evaluating markup or macros.");
        Require(await page.Locator(".local-test-banner").IsVisibleAsync() && await page.Locator(".local-test-banner").InnerTextAsync() is { } summary && summary.Contains("Fichier entièrement lu", StringComparison.Ordinal),
            "The persistent banner identifies the local simulation and its actual file receipt.");
        Require(await page.Locator(".page-content").GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).CountAsync() == 0,
            "The offline table omits ticket actions that cannot operate on a local test file.");
        await Capture("offline-import-records");
        await page.Locator(".nav-item[data-page=dashboard]").ClickAsync(); await Ready("dashboard");
        Require(app.IsTestMode && await page.Locator(".local-test-banner").IsVisibleAsync() && await page.Locator(".page-content .notice.danger").CountAsync() == 0,
            "The imported incident rows feed the real C# dashboard while the simulation remains visible.");
        await page.Locator(".nav-item[data-page=library]").ClickAsync(); await Ready("library");
        Require(await page.GetByRole(AriaRole.Heading, new() { Name = "Disponible après le mode test", Exact = true }).IsVisibleAsync()
            && await Button("Revenir au dashboard du fichier").IsVisibleAsync() && await page.Locator(".page-content .notice.danger").CountAsync() == 0
            && offlineLibraryRequests.IsEmpty,
            "Opening an unavailable workspace explains the test mode and offers a usable route without reading the operational library.");
        await page.Keyboard.PressAsync("Control+k");
        var palette = page.GetByRole(AriaRole.Dialog, new() { Name = "Rechercher un écran, une action ou un ticket", Exact = true }); await palette.WaitForAsync();
        await palette.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync("42");
        // Permit a mistakenly re-enabled debounce to run before asserting that
        // neither the source list nor ticket search accessed the real library.
        await page.WaitForTimeoutAsync(350);
        Require((await palette.Locator(".command-palette-status").InnerTextAsync()).Contains("Pour rechercher les lignes du fichier, ouvrez le relevé.", StringComparison.Ordinal)
            && !await palette.Locator(".command-palette-scope").IsVisibleAsync()
            && !await palette.GetByRole(AriaRole.Button, new() { Name = "Ouvrir la bibliothèque filtrée", Exact = true }).IsVisibleAsync()
            && await palette.Locator("[data-ticket-key]").CountAsync() == 0 && offlineLibraryRequests.IsEmpty,
            "Ctrl+K in a file test keeps local commands, explains row search and never requests the operational ticket library.");
        await page.Keyboard.PressAsync("Escape"); await palette.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Button("Revenir au dashboard du fichier").ClickAsync(); await Ready("dashboard");
        await page.Locator(".nav-item[data-page=collect]").ClickAsync(); await Ready("collect");
        Require(await page.GetByRole(AriaRole.Heading, new() { Name = "Collecte en pause", Exact = true }).IsVisibleAsync()
            && await Button("Lancer la collecte").CountAsync() == 0,
            "The collection page offers file exploration and shows that collection is paused during a test.");
        await Button("Quitter le mode test").ClickAsync(); await Ready("records");
        Require(!app.IsTestMode && ReferenceEquals(app.Result, original) && !await page.Locator(".local-test-banner").IsVisibleAsync(),
            "Leaving through the persistent banner restores the exact previous observation and normal mode.");
        Require(externalRequests == 0 && errors.IsEmpty, "The real offline import route, analysis and restoration complete without an external request or JavaScript error.");
        await delivery;
    }

    private static string CreateLocalFileTestWorkbook()
    {
        var path = Path.Combine(_root, "source-incidents.xlsm");
        using var output = File.Create(path); using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        void Add(string name, string text) { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Add("xl/workbook.xml", "<workbook xmlns='" + ns + "' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Dashboard' sheetId='1' r:id='r1'/><sheet name='Incidents' sheetId='2' r:id='r2'/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Target='worksheets/sheet1.xml'/><Relationship Id='r2' Target='worksheets/sheet2.xml'/></Relationships>");
        Add("xl/worksheets/sheet1.xml", "<worksheet xmlns='" + ns + "'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>Indicateur</t></is></c></row></sheetData></worksheet>");
        Add("xl/worksheets/sheet2.xml", "<worksheet xmlns='" + ns + "'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>Liste source</t></is></c></row><row r='4'><c r='A4' t='inlineStr'><is><t>ID</t></is></c><c r='B4' t='inlineStr'><is><t>Titre</t></is></c><c r='C4' t='inlineStr'><is><t>Statut</t></is></c><c r='D4' t='inlineStr'><is><t>Type</t></is></c></row><row r='5'><c r='A5'><v>42</v></c><c r='B5' t='inlineStr'><is><t>Incident &lt;b&gt;littéral&lt;/b&gt;</t></is></c><c r='C5' t='inlineStr'><is><t>En attente</t></is></c><c r='D5' t='inlineStr'><is><t>Incident</t></is></c></row><row r='6'><c r='A6'><v>43</v></c><c r='B6' t='inlineStr'><is><t>Demande synthétique</t></is></c><c r='C6' t='inlineStr'><is><t>Nouveau</t></is></c><c r='D6' t='inlineStr'><is><t>Demande</t></is></c></row></sheetData></worksheet>");
        Add("xl/vbaProject.bin", "MACRO SYNTHETIQUE NON EXECUTABLE : CETTE ENTREE NE DOIT PAS ETRE LUE");
        return path;
    }
}
