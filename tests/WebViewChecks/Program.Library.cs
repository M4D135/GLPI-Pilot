using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private const string LibrarySolution = "Réinitialiser le pilote <b>LOCAL</b>\nPuis tester l’imprimante. null";

    private static async Task CheckLibraryInterfaceAsync(string[] args)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var web = Path.Combine(repository, "web");
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(_root, "Library"), SourceTimeZone = "UTC", DisplayName = "Technicien synthétique" };
        new LibraryPreferencesStore().Save(preferences);
        var store = new TicketLibraryStore(preferences.DatabasePath);
        await store.InitializeAsync();
        var seed = LibrarySeed("7000001", "Imprimante : solution appliquée", 5);
        await store.SaveDetailAsync(seed, LibraryDetail(seed, LibrarySolution), new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true }, "fixture-v1");
        var active = LibrarySeed("7000002", "Imprimante : incident en cours", 2);
        await store.ObserveSeedAsync(active);
        var conflicting = LibrarySeed("7000003", "Imprimante : versions concurrentes", 2);
        await store.SaveDetailAsync(conflicting, LibraryDetail(conflicting, "Première observation synthétique"), new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true }, "fixture-v1");
        await store.SaveDetailAsync(conflicting, LibraryDetail(conflicting, "Autre observation au même date_mod"), new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true }, "fixture-v1");

        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0;
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new LibraryHost { NextFolder = preferences.LocalFolder };
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), new AppSettings { Theme = "dark", ExportFolder = Path.Combine(_root, "Exports") });
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
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator View(string name) => page.GetByRole(AriaRole.Navigation, new() { Name = "Vues de la bibliothèque", Exact = true }).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        async Task Ready(string id)
        {
            try { await WaitForPageAsync(page, "id => document.querySelector('.page-content')?.dataset.page===id && !document.querySelector('#content').hasAttribute('aria-busy')", id); }
            catch
            {
                Console.Error.WriteLine("Library route did not settle: expected=" + id
                    + "; actual=" + await page.Locator(".page-content").GetAttributeAsync("data-page")
                    + "; rows=" + app.Result?.Rows.Count
                    + "; notices=" + string.Join(" | ", await page.Locator(".notice.danger,.toast.danger").AllInnerTextsAsync())
                    + "; browserErrors=" + string.Join(" | ", errors));
                await Capture("library-route-failure");
                throw;
            }
        }
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
            await page.Locator("#notifications .toast button").EvaluateAllAsync<int>("buttons => { buttons.forEach(button=>button.click()); return buttons.length; }");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        }

        await page.GotoAsync("https://glpi-pilot.local/index.html");
        await page.Locator("body[data-ready=true]").WaitForAsync();
        await page.Locator(".nav-item[data-page=library]").ClickAsync(); await Ready("library");
        var first = await Call(app, "library.get");
        Require(first.GetProperty("stats").GetProperty("ticketCount").GetInt64() == 3
            && first.GetProperty("stats").GetProperty("conflictCount").GetInt64() > 0
            && await page.Locator(".library-result").CountAsync() == 3
            && await page.Locator(".library-result .library-conflict").CountAsync() == 1
            && await Button("Synchroniser").IsDisabledAsync(),
            "The library reads the real local store, exposes all three tickets and a preserved same-version conflict, and keeps central synchronization disabled until configured.");
        await Capture("library-local");

        await View("Résolution").ClickAsync();
        await page.GetByRole(AriaRole.Searchbox, new() { Name = "Rechercher dans la bibliothèque", Exact = true }).FillAsync("imprimante");
        await Button("Rechercher").ClickAsync();
        await WaitForPageAsync(page, "document.querySelectorAll('.library-result').length===1 && !document.querySelector('.library-results').hasAttribute('aria-busy')");
        var resultText = await page.Locator(".library-result").InnerTextAsync();
        Require(resultText.Contains("7000001") && resultText.Contains(LibrarySolution)
            && resultText.Contains("Solution effectivement recueillie") && resultText.Contains("Historique synthétique")
            && await page.Locator(".library-result blockquote b").CountAsync() == 0
            && !resultText.Contains("7000002") && !resultText.Contains("7000003"),
            "Resolution searches only the collected resolved tickets and presents the exact solution as literal text with its source section and observation date.");
        await Capture("library-resolution");

        await Button("Lire le contenu conservé").ClickAsync();
        var modal = page.GetByRole(AriaRole.Dialog);
        Require((await modal.InnerTextAsync()).Contains(LibrarySolution)
            && (await modal.InnerTextAsync()).Contains(seed.Ticket.Url)
            && await modal.Locator("b").CountAsync() == 0,
            "The conserved ticket reader displays exact content and the original validated GLPI reference without interpreting markup.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Ouvrir dans GLPI", Exact = true }).ClickAsync();
        await Until(() => host.OpenedUrls.Count == 1);
        Require(host.OpenedUrls.SequenceEqual([seed.Ticket.Url]),
            "Opening a library citation reaches the real validated host operation with the corresponding ticket URL.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

        await Button("Ajouter une note").ClickAsync();
        modal = page.GetByRole(AriaRole.Dialog);
        await modal.GetByRole(AriaRole.Textbox, new() { Name = "Titre", Exact = true }).FillAsync("Note d’équipe synthétique");
        await modal.GetByRole(AriaRole.Textbox, new() { Name = "Texte de la contribution", Exact = true }).FillAsync("Procédure locale <script>texte</script> à conserver.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Enregistrer la contribution", Exact = true }).ClickAsync();
        await modal.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await View("Équipe").ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Note d’équipe synthétique", Exact = true }).WaitForAsync();
        var team = await Call(app, "workspace.list", new { kind = "note", text = "Note d’équipe synthétique" });
        var saved = team.GetProperty("items")[0];
        Require(saved.GetProperty("ticketKey").GetString() == TicketLibraryRules.Key(seed.Ticket)
            && saved.GetProperty("body").GetString() == "Procédure locale <script>texte</script> à conserver."
            && await page.Locator(".library-result-excerpt script").CountAsync() == 0,
            "A note entered from a library ticket persists through the actual shared-workspace operation with its ticket link and literal content.");

        await Button("Modifier").ClickAsync();
        modal = page.GetByRole(AriaRole.Dialog);
        await Call(app, "workspace.save", new { item = new { id = saved.GetProperty("id").GetString(), kind = "note", title = "Note d’équipe synthétique", body = "Modification concurrente conservée", ticketKey = TicketLibraryRules.Key(seed.Ticket), status = "draft" }, expectedRevision = saved.GetProperty("revision").GetInt64() });
        await modal.GetByRole(AriaRole.Textbox, new() { Name = "Texte de la contribution", Exact = true }).FillAsync("Modification obsolète à refuser");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Enregistrer la contribution", Exact = true }).ClickAsync();
        await modal.Locator(".notice.danger").WaitForAsync();
        var retained = await Call(app, "workspace.list", new { text = "Note d’équipe synthétique" });
        Require(retained.GetProperty("items")[0].GetProperty("body").GetString() == "Modification concurrente conservée"
            && await modal.IsVisibleAsync(),
            "An obsolete team editor cannot overwrite a newer contribution and retains its unsaved text for the user to review.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

        var longBody = string.Concat(Enumerable.Repeat("Texte complet <b>littéral</b> de la connaissance.\n", 80));
        var currentNote = await Call(app, "workspace.get", new { id = saved.GetProperty("id").GetString() });
        await Call(app, "workspace.save", new { item = new { id = currentNote.GetProperty("id").GetString(), kind = "note", title = "Note d’équipe synthétique", body = longBody, ticketKey = TicketLibraryRules.Key(seed.Ticket), status = "draft" }, expectedRevision = currentNote.GetProperty("revision").GetInt64() });
        await View("Bibliothèque").ClickAsync(); await View("Équipe").ClickAsync();
        await Button("Lire la contribution").ClickAsync();
        modal = page.GetByRole(AriaRole.Dialog);
        var fullRead = await modal.Locator("pre").TextContentAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await Button("Modifier").ClickAsync();
        modal = page.GetByRole(AriaRole.Dialog);
        Require(fullRead == longBody && await modal.GetByRole(AriaRole.Textbox, new() { Name = "Texte de la contribution", Exact = true }).InputValueAsync() == longBody
            && await modal.Locator("b").CountAsync() == 0,
            "Opening or editing an existing long contribution fetches its full document rather than the truncated list preview, with markup preserved as text.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

        host.NextSave = Path.Combine(_root, "library-corpus.jsonl");
        await Button("Exporter le corpus RAG").ClickAsync();
        await Until(() => File.Exists(host.NextSave));
        var records = (await File.ReadAllLinesAsync(host.NextSave)).Select(line => JsonSerializer.Deserialize<JsonElement>(line, PilotApplication.Json)).ToArray();
        var exported = records.Where(record => record.GetProperty("recordType").GetString() == "ticket")
            .Select(record => record.GetProperty("document").Deserialize<TicketLibraryDocument>(PilotApplication.Json)!).ToList();
        var solutionChunk = records.Single(record => record.GetProperty("recordType").GetString() == "chunk"
            && record.GetProperty("citation").GetProperty("isSolution").GetBoolean());
        Require(records[0].GetProperty("protocol").GetString() == "glpi-pilot-ticket-library" && exported.Count == 3
            && exported.Single(document => document.Seed.Ticket.Id == seed.Ticket.Id).Detail!.Sections[0].Data.Rows[0][3] == LibrarySolution
            && solutionChunk.GetProperty("text").GetString() == LibrarySolution
            && solutionChunk.GetProperty("citation").GetProperty("url").GetString() == seed.Ticket.Url,
            "The corpus export writes a versioned manifest, complete tickets and source-linked RAG chunks retaining the exact collected solution.");

        await View("Connexion").ClickAsync();
        await Button("Choisir le dossier").ClickAsync();
        await Until(() => host.FolderPicks == 1);
        Require(await page.GetByRole(AriaRole.Textbox, new() { Name = "Dossier de la bibliothèque", Exact = true }).InputValueAsync() == preferences.LocalFolder && host.FolderPicks == 1,
            "The local cache folder selector uses the native host result without changing the active library before settings are saved.");
        await page.GetByLabel("Activer le central PostgreSQL", new() { Exact = true }).CheckAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Hôte PostgreSQL", Exact = true }).FillAsync("central-fixture.invalid");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Utilisateur PostgreSQL", Exact = true }).FillAsync("fixture");
        await page.GetByLabel("Synchroniser automatiquement", new() { Exact = true }).UncheckAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Format des dates GLPI", Exact = true }).SelectOptionAsync("dmy");
        const string sessionSecret = "Only-for-this-synthetic-session-32";
        await page.GetByLabel("Mot de passe PostgreSQL", new() { Exact = true }).FillAsync(sessionSecret);
        await Button("Enregistrer la connexion").ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('input[type=password]')?.value==='' && !document.querySelector('.library-settings button[aria-busy=true]')");
        var publicSettings = await Call(app, "library.get");
        var storedSettings = new LibraryPreferencesStore().Load();
        Require(publicSettings.GetProperty("settings").GetProperty("host").GetString() == "central-fixture.invalid"
            && !publicSettings.GetRawText().Contains(sessionSecret) && storedSettings.ProtectedPassword.Length == 0
            && storedSettings.TlsMode == "VerifyFull" && !storedSettings.AutoSync && storedSettings.SourceDateOrder == "dmy"
            && publicSettings.GetProperty("settings").GetProperty("sourceDateOrder").GetString() == "dmy"
            && await page.GetByLabel("Mot de passe PostgreSQL", new() { Exact = true }).InputValueAsync() == "",
            "Connection settings persist through C# while a session-only password is cleared from the form, omitted from public state, and never written as a saved secret.");
        await Capture("library-connection");
        await Button("Enregistrer la connexion").ClickAsync();
        await WaitForPageAsync(page, "!document.querySelector('.library-settings button[aria-busy=true]')");
        var preservedSecret = (await Call(app, "library.get")).GetProperty("settings").GetProperty("hasPassword").GetBoolean();
        await Button("Oublier le mot de passe").ClickAsync();
        await WaitForPageAsync(page, "!document.querySelector('.library-settings button[aria-busy=true]')");
        Require(preservedSecret && !(await Call(app, "library.get")).GetProperty("settings").GetProperty("hasPassword").GetBoolean(),
            "Saving an empty password field preserves the existing session secret until the explicit forget operation removes it.");
        await page.GetByLabel("Activer le central PostgreSQL", new() { Exact = true }).UncheckAsync();
        await Button("Enregistrer la connexion").ClickAsync();
        await WaitForPageAsync(page, "!document.querySelector('.library-settings button[aria-busy=true]')");

        await View("Bibliothèque").ClickAsync();
        await Button("Utiliser le cache comme relevé").ClickAsync(); await Ready("dashboard");
        Require(app.Result?.Rows.Count == 3 && app.Result.Headers.Contains("ID") && !app.Result.Complete,
            "The local library can populate a real analytical source while retaining its non-certified collection status.");
        var dashboard = await Call(app, "dashboard.get");
        var metrics = dashboard.GetProperty("snapshot").GetProperty("metrics").EnumerateArray().ToDictionary(metric => metric.GetProperty("key").GetString()!);
        Require(app.Result!.Headers.Contains("Priorité") && app.Result.Headers.Contains("Attribué à - Technicien")
            && metrics["high"].GetProperty("value").GetDouble() == 1
            && metrics["without-technician"].GetProperty("value").GetDouble() == 1,
            "The analytical projection preserves original French priority and technician columns and computes the expected nonzero indicators from the cached index.");
        await page.Locator(".nav-item[data-page=library]").ClickAsync(); await Ready("library");
        await page.SetViewportSizeAsync(760, 850);
        Require(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth+1") && externalRequests == 0 && errors.IsEmpty,
            "The library fits a compact viewport and runs with the real strict CSP without browser errors or external network requests.");
        await Capture("library-compact");
        await delivery;
    }

    private static TicketLibrarySeed LibrarySeed(string id, string title, int status) => new()
    {
        Ticket = new(id, "https://library-fixture.invalid/front/ticket.form.php?id=" + id), Title = title,
        Status = status == 5 ? "Résolu" : "En cours (attribué)", StatusCode = status, Category = "Impression",
        LastModifiedRaw = "2026-10-20 09:15:00", LastModifiedAt = new(2026, 10, 20, 9, 15, 0, TimeSpan.Zero),
        LastModifiedReliable = true, LastModifiedPrecision = "seconds", ObservedAt = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
        Fields = new() { ["Priorité"] = id == "7000002" ? "Haute" : "Moyenne", ["Attribué à - Technicien"] = id == "7000003" ? "" : "Technicien synthétique" },
        IndexHeaders = ["ID", "Titre", "Statut", "Dernière modification", "Priorité", "Attribué à - Technicien"],
        IndexValues = [id, title, status == 5 ? "Résolu" : "En cours (attribué)", "2026-10-20 09:15:00", id == "7000002" ? "Haute" : "Moyenne", id == "7000003" ? "" : "Technicien synthétique"]
    };
    private static TicketDetailResult LibraryDetail(TicketLibrarySeed seed, string solution) => new()
    {
        Ticket = seed.Ticket, Title = seed.Title, ReadAt = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
        Tabs = [new() { Key = "timeline", Name = "Historique synthétique", Category = "timeline", Loaded = true }],
        Sections = [new() { Name = "Historique synthétique", Kind = "timeline", TabKey = "timeline", Data = new()
        { Headers = ["Type", "Date", "Auteur", "Contenu"], Rows = [new[] { "Solution", "2026-10-20 09:15", "Technicien fictif", solution }], Complete = true } }]
    };

    private sealed class LibraryHost : IPilotHost
    {
        public string? NextSave { get; set; }
        public string? NextFolder { get; set; }
        public int FolderPicks { get; private set; }
        public ConcurrentQueue<string> OpenedUrls { get; } = new();
        public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult(NextSave);
        public Task<string?> PickFolderAsync(string title) { FolderPicks++; return Task.FromResult(NextFolder); }
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
        public Task OpenExternalAsync(string url) { OpenedUrls.Enqueue(url); return Task.CompletedTask; }
        public Task CloseAsync() => Task.CompletedTask;
        public Task<byte[]> CapturePngAsync() => Task.FromResult(Array.Empty<byte>());
        public Task PrintPdfAsync(string path) => throw new NotSupportedException();
        public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
    }
}
