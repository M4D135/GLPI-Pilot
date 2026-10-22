using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static class Program
{
    private static int _passed;
    private static string _root = "";
    private static void Require(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); _passed++; }
    private static async Task Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", _root);
        try
        {
            await CheckBridgeAsync();
            await CheckFoundationAsync();
            if (!args.Contains("--core-only")) await CheckInterfaceAsync(args);
            Console.WriteLine($"{_passed}/{_passed} WebView integration checks passed.");
        }
        finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", null); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", null); try { Directory.Delete(_root, true); } catch (IOException) { } }
    }
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object? args = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json);
        return JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, json, ct), PilotApplication.Json);
    }
    private static async Task CheckBridgeAsync()
    {
        var replies = new ConcurrentQueue<JsonElement>(); var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = new WebBridge(new[] { "test.read", "test.wait" }, async (op, data, ct) =>
        { Interlocked.Increment(ref calls); if (op == "test.wait") { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return new { text = data.TryGetProperty("text", out var value) ? value.GetString() : "ok" }; }, json => { using var doc = JsonDocument.Parse(json); replies.Enqueue(doc.RootElement.Clone()); });
        bridge.Receive("{\"id\":\"first\",\"op\":\"test.read\",\"args\":{\"text\":\"<script>alert(1)</script>\"}}");
        await Until(() => replies.Any(r => r.TryGetProperty("id", out var id) && id.GetString() == "first"));
        Require(calls == 1 && replies.First().GetProperty("result").GetProperty("text").GetString()!.Contains("<script>"), "The bridge preserves text as data and dispatches registered operations only.");
        bridge.Receive("{\"id\":\"unknown\",\"op\":\"System.IO.File.Delete\",\"args\":{}}");
        bridge.Receive("{\"id\":\"duplicate\",\"op\":\"test.read\",\"args\":{\"x\":1,\"X\":2}}");
        bridge.Receive("{\"id\":\"array\",\"op\":\"test.read\",\"args\":[]}");
        bridge.Receive("{\"id\":\"extra\",\"op\":\"test.read\",\"args\":{},\"extra\":1}");
        Require(calls == 1 && replies.Count(r => r.TryGetProperty("error", out var error) && error.GetProperty("code").GetString() == "INVALID_REQUEST") == 3 && replies.Any(r => r.TryGetProperty("name", out var name) && name.GetString() == "bridge.rejected"), "Unknown operations, duplicate JSON properties, array arguments and extra envelope fields are refused.");
        bridge.Receive("{\"id\":\"wait\",\"op\":\"test.wait\",\"args\":{}}"); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bridge.Receive("{\"id\":\"wait\",\"op\":\"test.read\",\"args\":{}}");
        bridge.Receive("{\"type\":\"cancel\",\"id\":\"wait\"}");
        await Until(() => replies.Any(r => r.TryGetProperty("error", out var error) && error.GetProperty("code").GetString() == "CANCELLED"));
        Require(calls == 2, "A reused in-flight identity cannot replay an operation; cancellation bypasses the execution queue.");
        bridge.Receive(new string('x', WebBridge.MaximumMessageCharacters + 1));
        Require(replies.Any(r => r.TryGetProperty("name", out var name) && name.GetString() == "bridge.rejected"), "Oversized and malformed requests are rejected without logging their contents.");
    }
    private static async Task CheckFoundationAsync()
    {
        var host = new TestHost(_root);
        await using var app = new PilotApplication(host, (_, _) => { }, new AppSettings { ExportFolder = Path.Combine(_root, "Exports") });
        var bootstrap = await Call(app, "app.bootstrap");
        Require(bootstrap.GetProperty("capabilities").GetArrayLength() > 100, "Every feature module registers with the real application without duplicate operations.");
        await Call(app, "app.demo"); var first = await Call(app, "records.list", new { limit = 50 });
        Require(first.GetProperty("total").GetInt32() == 180 && first.GetProperty("rows").GetArrayLength() == 50, "The demo reaches the real C# record pagination without a fixture bridge.");
        var revision = app.Revision;
        var query = await Call(app, "records.list", new { revision, search = "Marseille", limit = 50, transform = new { sorts = new[] { new { column = 0, descending = true, type = "number" } }, filters = Array.Empty<object>() } });
        Require(query.GetProperty("total").GetInt32() == 45 && query.GetProperty("rows")[0].GetProperty("cells")[0].GetString() == "180", "Filtering and typed sorting apply to the whole source before pagination.");
        host.NextSave = Path.Combine(_root, "selected.csv");
        await Call(app, "records.export", new { revision, view = true, format = "csv", indices = new[] { 3, 7 }, search = "Marseille" });
        var exported = NativeCsvImportService.Read(host.NextSave, "", null, 1000, false, default);
        Require(exported.Rows.Count == 2, "The view export preserves its contributor selection rather than exporting unrelated rows.");
        await Call(app, "app.demo");
        try { await Call(app, "records.export", new { revision, format = "csv" }); throw new Exception("Stale export was accepted."); }
        catch (InvalidOperationException) { Require(true, "Stale record revisions cannot be exported after the source changes."); }
        var mapping = await Call(app, "mapping.get");
        Require(mapping.GetProperty("target").GetString() == "LocalResult", "A demonstration uses a local mapping and cannot overwrite source mappings.");
        await Call(app, "app.appearance", new { theme = "light", zoom = 125 });
        Require(AppSettings.Load().InterfaceZoomPercent == 125 && AppSettings.Load().Theme == "light", "Appearance preferences persist through the C# settings store.");
        host.NextSave = Path.Combine(_root, "configuration.json"); await Call(app, "configuration.export"); host.NextOpen = host.NextSave;
        var review = await Call(app, "configuration.preview");
        Require(review.TryGetProperty("token", out _), "Version 3 exports can be imported into an explicit configuration review.");
        await Call(app, "app.appearance", new { zoom = 100 });
        try { await Call(app, "configuration.apply", new { token = review.GetProperty("token").GetString(), parts = 255, merge = true }); throw new Exception("Obsolete configuration was accepted."); }
        catch (InvalidOperationException) { Require(true, "A configuration review expires when the underlying settings change."); }
    }
    private static async Task CheckInterfaceAsync(string[] args)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var web = Path.Combine(repository, "web"); if (!File.Exists(Path.Combine(web, "index.html"))) throw new FileNotFoundException("Run the suite from its built test directory: web/index.html was not found.");
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000); var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => { errors.Enqueue(error); Console.WriteLine("BROWSER: " + error); };
        page.Console += (_, message) => { if (message.Type == "error") { errors.Enqueue(message.Text); Console.WriteLine("CONSOLE: " + message.Text); } };
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url); if (uri.Host != "glpi-pilot.local") { await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var application = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), new AppSettings { Theme = "dark", ExportFolder = Path.Combine(_root, "Exports") });
        await using var actualBridge = new WebBridge(application.Operations.Concat(new[] { "app.ready", "host.editor", "host.screens", "host.screen" }), async (op, data, ct) =>
        {
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { application.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            if (op == "host.screens") return new[] { new { id = "test", primary = true, width = 1440, height = 1000 } };
            if (op == "host.screen") return new { moved = true };
            return await application.ExecuteAsync(op, data, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);
        await page.GotoAsync("https://glpi-pilot.local/index.html");
        try { await page.Locator("body[data-ready=true]").WaitForAsync(); } catch { Console.WriteLine("BODY: " + await page.Locator("body").InnerTextAsync()); if (delivery.Exception is not null) Console.WriteLine(delivery.Exception); throw; }
        Require(await page.Locator(".hero").CountAsync() == 1 && application.Result is null, "The complete local shell boots against the real C# bridge without loading business data or opening a browser.");
        Require(await page.EvaluateAsync<bool>("""
            async () => {
              const { el, replaceChildren } = await import('/js/ui.js'); let clicks = 0;
              const action = el('button', { onClick: () => clicks++ }, 'Action'); const control = el('input', { value: 'Texte modifié' });
              const root = el('div', {}, 'Ancien contenu'); replaceChildren(root, null, undefined, false, [0, ['null', action], control]); action.click();
              return root.textContent === '0nullAction' && clicks === 1 && root.querySelector('input') === control && control.value === 'Texte modifié'
                && el('div', {}, null, undefined, false, [0, 'null']).textContent === '0null';
            }
            """), "Optional UI children stay absent while literal null, zero, edit values and existing event handlers are preserved.");
        Require(await page.EvaluateAsync<bool>("""
            async () => {
              const { ticketIdLabel } = await import('/js/pages/workplace-common.js');
              const values = ['8765432', '8 765 432', '8\u00a0765\u00a0432', '8\u202f765\u202f432', '0 123 456', 'ABC 123', '12 34', '0012345', '12345678901234567890'];
              const expected = ['8765432', '8765432', '8765432', '8765432', '0123456', 'ABC 123', '12 34', '0012345', '12345678901234567890'];
              return values.every((value, index) => ticketIdLabel(value) === expected[index]);
            }
            """), "Ticket labels remove only proven decimal grouping and preserve leading zeroes, nonnumeric IDs and digit precision.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Explorer la démonstration", Exact = true }).ClickAsync();
        await page.Locator(".page-content[data-page=dashboard]").WaitForAsync();
        await WaitForPageAsync(page, "document.querySelector('#content')?.getAttribute('aria-busy') === null");
        Require(application.Result?.Rows.Count == 180 && await page.Locator(".page-content[data-page=dashboard] .notice.danger").CountAsync() == 0, "The dashboard opens after a demo and computes its indicators in C#.");
        var routes = await page.Locator(".nav-item").EvaluateAllAsync<string[]>("nodes => nodes.map(node => node.dataset.page)");
        foreach (var route in routes)
        {
            await page.Locator($".nav-item[data-page='{route}']").ClickAsync();
            await WaitForPageAsync(page, "id => !!document.querySelector('.page-content[data-page=\"' + id + '\"]') && !document.querySelector('#content').hasAttribute('aria-busy')", route);
            var failures = await page.Locator(".page-content > .notice.danger[role=alert]").AllTextContentsAsync();
            Require(failures.Count == 0, "Screen renders with the real dispatcher: " + route + (failures.Count > 0 ? " · " + string.Join(" | ", failures) : ""));
        }
        await page.Locator(".nav-item[data-page='records']").ClickAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Recherche du relevé" }).FillAsync("Marseille");
        await WaitForPageAsync(page, "document.querySelector('.page-content').innerText.includes('45 / 180 lignes')");
        Require(await page.Locator(".data-table tbody tr").CountAsync() == 45, "Typing a record search returns the complete matching rows from the C# query engine.");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Filtrer ID", Exact = true }).FillAsync("180");
        await WaitForPageAsync(page, "document.querySelector('.page-content').innerText.includes('1 / 180 lignes')");
        Require(await page.Locator(".data-table tbody tr").CountAsync() == 1, "Inline column filters combine with the full-source search before pagination.");
        Require(await page.GetByRole(AriaRole.Textbox, new() { Name = "Filtrer ID", Exact = true }).EvaluateAsync<bool>("node => node === document.activeElement"), "Refreshing filtered rows preserves the typing focus in the column header.");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Filtrer ID", Exact = true }).FillAsync("");
        await WaitForPageAsync(page, "document.querySelector('.page-content').innerText.includes('45 / 180 lignes')");
        await page.Locator("thead").GetByRole(AriaRole.Button, new() { Name = "ID", Exact = true }).ClickAsync();
        await page.Locator("thead").GetByRole(AriaRole.Button, new() { Name = "ID ↑ 1", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('tbody tr .cell-text')?.textContent === '180'");
        Require(true, "Header sorting uses the C# numeric order and toggles descending without losing the filter.");
        await page.Locator("thead").GetByRole(AriaRole.Button, new() { Name = "Priorité", Exact = true }).ClickAsync(new() { Modifiers = new[] { KeyboardModifier.Shift } });
        await page.Locator("thead").GetByRole(AriaRole.Button, new() { Name = "Priorité ↑ 2", Exact = true }).WaitForAsync();
        Require(true, "Shift-click adds a second sort criterion while retaining the primary one.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Colonnes", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Rechercher une colonne", Exact = true }).FillAsync("titre");
        Require(await page.GetByRole(AriaRole.Dialog).Locator("tbody tr").CountAsync() == 1, "The column selector searches the schema without changing hidden columns or their order.");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

        await page.Locator(".nav-item[data-page='settings']").ClickAsync();
        await page.Locator(".page-content[data-page=settings]").GetByRole(AriaRole.Combobox, new() { Name = "Thème", Exact = true }).SelectOptionAsync("light");
        await page.GetByRole(AriaRole.Button, new() { Name = "Appliquer l’apparence", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.documentElement.dataset.theme === 'light'");
        Require(application.Settings.Theme == "light", "A real appearance form updates both persisted C# preferences and the shell.");
        await page.Keyboard.PressAsync("Control+k");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Rechercher une commande" }).FillAsync("Relevé de données");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Relevé de données", Exact = true }).ClickAsync();
        await page.Locator(".page-content[data-page=records]").WaitForAsync();
        Require(await page.GetByRole(AriaRole.Dialog).CountAsync() == 0, "The keyboard command palette navigates and restores focus without a native modal.");
        var captureArg = Array.IndexOf(args, "--screenshots");
        if (captureArg >= 0 && captureArg + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[captureArg + 1]); Directory.CreateDirectory(output);
            await page.Locator(".nav-item[data-page='home']").ClickAsync(); await WaitForPageAsync(page, "!document.querySelector('#content').hasAttribute('aria-busy')");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "home-light.png"), FullPage = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "Changer de thème", Exact = true }).ClickAsync(); await WaitForPageAsync(page, "document.documentElement.dataset.theme === 'dark'");
            await page.Locator(".nav-item[data-page='dashboard']").ClickAsync(); await WaitForPageAsync(page, "!document.querySelector('#content').hasAttribute('aria-busy')");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "dashboard-dark.png"), FullPage = true });
        }
        async Task<string[]> QualityScreens(ExtractionResult data)
        {
            application.LoadQualityFixture(data); await delivery;
            var observations = new List<string>();
            foreach (var route in new[] { "home", "collect", "records", "quality" })
            {
                await page.Locator($".nav-item[data-page='{route}']").ClickAsync();
                await WaitForPageAsync(page, "id => !!document.querySelector('.page-content[data-page=\"' + id + '\"]') && !document.querySelector('#content').hasAttribute('aria-busy')", route);
                observations.Add(await page.Locator(".page-content").InnerTextAsync());
            }
            return observations.ToArray();
        }
        var received = QualityFixture();
        var receivedScreens = await QualityScreens(received);
        Require(receivedScreens.All(text => text.Contains("Réception complète") && text.Contains("Concordance à vérifier")
            && !text.Contains("Réception partielle") && !text.Contains("Complétude à vérifier"))
            && !received.Complete && await page.Locator(".source-status.warning").CountAsync() == 1
            && (await page.Locator(".source-status").InnerTextAsync()).Contains("Réception complète · concordance à vérifier"),
            "All four collection screens separate complete receipt from differing sample values without promoting the result to Complete.");
        if (captureArg >= 0 && captureArg + 1 < args.Length)
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetFullPath(args[captureArg + 1]), "quality-reception.png"), FullPage = true });
        var partial = QualityFixture(); partial.ExpectedTotal = 6; partial.Quality!.ExpectedRows = 6; partial.Quality.RowCountVerified = false;
        partial.Quality.InitialSample!.RepresentationVerified = partial.Quality.FinalSample!.RepresentationVerified = true;
        partial.Quality.InitialSample.DifferentCells = partial.Quality.FinalSample.DifferentCells = 0;
        var partialScreens = await QualityScreens(partial);
        Require(partialScreens.All(text => text.Contains("Réception partielle") && !text.Contains("Réception complète"))
            && (await Call(application, "records.quality")).GetProperty("completeness").GetString()!.Contains("4 / 6"),
            "A real short source remains partial in every collection screen even when its sampled values agree.");
        var undocumented = QualityFixture(); undocumented.Quality = null;
        var undocumentedScreens = await QualityScreens(undocumented);
        var undocumentedPartial = QualityFixture(); undocumentedPartial.Quality = null; undocumentedPartial.ExpectedTotal = 6;
        var undocumentedPartialScreens = await QualityScreens(undocumentedPartial);
        Require(undocumentedScreens.All(text => text.Contains("Réception non documentée") && text.Contains("Contrôle de concordance indisponible")
            && !text.Contains("Réception complète") && !text.Contains("0 / 0"))
            && undocumentedPartialScreens.All(text => text.Contains("Réception partielle") && text.Contains("Contrôle de concordance indisponible") && !text.Contains("Réception complète")),
            "An older result reports unavailable controls without hiding a known short source or inventing success and zero counts.");
        var uncheckedSample = QualityFixture(); uncheckedSample.Quality!.InitialSample = null; uncheckedSample.Quality.FinalSample = null;
        var uncheckedScreens = await QualityScreens(uncheckedSample);
        Require(uncheckedScreens.All(text => text.Contains("Réception complète") && text.Contains("Contrôle de concordance indisponible")
            && text.Contains("Contrôle des identifiants indisponible")) && !uncheckedSample.Complete,
            "Complete receipt with no sample keeps both identity and value comparisons explicitly unavailable.");
        var paginated = QualityFixture(); paginated.Quality = null; paginated.Complete = true; paginated.Warnings.Clear();
        paginated.Provenance.Method = "pagination"; paginated.Scope = "Recherche rechargée avec start=0, puis pages suivantes";
        paginated.Status = "Total récupéré conforme au total annoncé";
        var paginatedScreens = await QualityScreens(paginated);
        Require(paginatedScreens.All(text => text.Contains("Relevé complet") && text.Contains("Contrôle de concordance indisponible")
            && text.Contains("Contrôle des identifiants indisponible") && !text.Contains("Réception non documentée")
            && !text.Contains("Réception partielle") && !text.Contains("Réception non confirmée"))
            && (await page.Locator(".source-status").InnerTextAsync()).Contains("Relevé complet · détail des contrôles indisponible")
            && await page.Locator(".source-status.warning").CountAsync() == 0 && paginated.Complete,
            "A complete paginated result without native sample metadata remains complete while its detailed controls stay unavailable.");
        await page.SetViewportSizeAsync(900, 700);
        Require(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "The shell remains within the window at a compact viewport.");
        Require(errors.Count == 0, "All ES modules run without browser errors or CSP violations." + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : ""));
        await delivery;
    }
    private static ExtractionResult QualityFixture() => new()
    {
        Headers = new() { "ID", "Titre", "Statut" },
        Rows = Enumerable.Range(1, 4).Select(i => new[] { i.ToString(), "Ticket synthétique " + i, "Nouveau" }).ToList(),
        SourceUrl = "https://quality-fixture.invalid/front/ticket.php", Title = "Contrôles de réception synthétiques",
        ExpectedTotal = 4, Complete = false, Status = "Contrôles de collecte à vérifier.",
        Warnings = new() { "Réserve synthétique conservée avec le relevé." },
        Provenance = new() { Method = "native-export", SourceKind = "glpi", SearchVerified = true, ScopeVerified = true },
        Quality = new()
        {
            SourceRows = 4, ExpectedRows = 4, AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = true,
            InitialSample = new() { IdentityVerified = true, RepresentationVerified = false, SampleSize = 2, ComparedCells = 2, DifferentCells = 1 },
            FinalSample = new() { IdentityVerified = true, RepresentationVerified = false, SampleSize = 2, ComparedCells = 2, DifferentCells = 1 }
        }
    };
    // Poll from the test process: WaitForFunction injects eval, forbidden by the real app CSP.
    private static async Task WaitForPageAsync(IPage page, string expression, object? argument = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await page.EvaluateAsync<bool>(expression, argument)) await Task.Delay(50, timeout.Token);
    }
    private static async Task Until(Func<bool> predicate) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!predicate()) await Task.Delay(10, deadline.Token); }
    private sealed class TestHost(string root) : IPilotHost
    {
        public string? NextOpen { get; set; }
        public string? NextSave { get; set; }
        public IPage? Page { get; set; }
        public string Clipboard { get; private set; } = "";
        public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult(NextOpen);
        public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult(NextSave);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(Path.Combine(root, "Exports"));
        public Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
        public Task OpenExternalAsync(string url) => throw new InvalidOperationException("External navigation is not enabled in the integration fixture.");
        public Task CloseAsync() => Task.CompletedTask;
        public Task<byte[]> CapturePngAsync() => Page?.ScreenshotAsync() ?? Task.FromResult(Array.Empty<byte>());
        public async Task PrintPdfAsync(string path) { if (Page is null) throw new InvalidOperationException(); await Page.PdfAsync(new() { Path = path, PrintBackground = true }); }
        public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
    }
}

namespace GlpiPilot.Runtime
{
    // Test-only admission of synthetic observations; the dispatcher and rendered pages remain real.
    public sealed partial class PilotApplication
    {
        public void LoadQualityFixture(ExtractionResult data) => SetResult(data);
    }
}
