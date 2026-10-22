using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckResolutionInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(_root, "ResolutionLibrary"), SourceTimeZone = "UTC" };
        new LibraryPreferencesStore().Save(preferences); var store = new TicketLibraryStore(preferences.DatabasePath);
        var source = LibrarySeed("81001", "Imprimante laser E-1234", 2);
        var detail = LibraryDetail(source, "Observation source");
        detail.Sections[0].Data.Rows = Enumerable.Range(1, 62).Select(index => new[] { "Suivi", "2026-10-20 09:15", "Technicien synthétique", "Observation source " + index }).ToList();
        await store.SaveDetailAsync(source, detail, new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, "fixture-v1");
        const string original = "<b>Remplacer le pilote</b>\u0092\nPuis tester exactement la file d’impression.";
        var precedent = LibrarySeed("876543210123456789", "Imprimante laser E-1234", 5);
        var saved = await store.SaveDetailAsync(precedent, LibraryDetail(precedent, original), new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, "fixture-v1");
        var secondary = LibrarySeed("81003", "Imprimante laser hors ligne", 5);
        await store.SaveDetailAsync(secondary, LibraryDetail(secondary, "Relancer le service d’impression."), new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, "fixture-v1");
        var foreign = LibrarySeed("81004", "Imprimante laser E-1234", 5); foreign.Ticket = new("81004", "https://other-resolution.invalid/front/ticket.form.php?id=81004");
        await store.SaveDetailAsync(foreign, LibraryDetail(foreign, "Ne pas proposer cette autre installation."), new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, "fixture-v1");
        var insufficient = LibrarySeed("81005", "Demande de ticket", 2); await store.ObserveSeedAsync(insufficient);
        var absent = LibrarySeed("81006", "Imprimante laser absente du cache local", 2);
        var workplace = new WorkplaceDocument();
        var absentPersonal = WorkplaceStore.Remember(workplace, absent.Ticket, absent.Title);
        absentPersonal.ReadingPosition.PageIndex = 2;
        var sourcePersonal = WorkplaceStore.Remember(workplace, source.Ticket, source.Title);
        sourcePersonal.ReadingPosition.PageIndex = 2;
        WorkplaceStore.Save(Path.Combine(_root, "workplace.json"), workplace);

        using var playwright = await Playwright.CreateAsync(); var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var calls = new ConcurrentQueue<string>(); var externalRequests = 0;
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
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
        var host = new LibraryHost(); WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings { Theme = "dark", PortalUrl = "https://library-fixture.invalid/front/ticket.php", ExportFolder = Path.Combine(_root, "Exports") }; settings.Interface.FollowWindowsTheme = false;
        settings.TicketDetails.AutoLoadOnSelection = false;
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
        await Call(app, "app.demo"); var revision = app.Revision; var receipt = app.Result;
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            calls.Enqueue(op);
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            var result = await app.ExecuteAsync(op, data, ct);
            if (op == "library.resolution" && data.TryGetProperty("text", out var query) && query.GetString() == "lente")
            { slowStarted.TrySetResult(); await slowRelease.Task.WaitAsync(TimeSpan.FromSeconds(15)); slowFinished.TrySetResult(); }
            return result;
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);
        ILocator Modal() => page.Locator("dialog.library-ticket-modal");
        ILocator Button(string label) => Modal().GetByRole(AriaRole.Button, new() { Name = label, Exact = true });
        ILocator Manual() => Modal().GetByRole(AriaRole.Searchbox, new() { Name = "Affiner la recherche dans les précédents", Exact = true });
        async Task Settled() => await Modal().Locator(".library-resolution-results:not([aria-busy])").WaitForAsync();
        async Task Open(string id)
        {
            await page.Keyboard.PressAsync("Control+k");
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync(id);
            await page.Locator("dialog.command-palette .command-palette-results[aria-busy=false]").WaitForAsync();
            await page.Locator("dialog.command-palette [data-ticket-key]").First.ClickAsync();
            await Modal().Locator(".library-detail:not([aria-busy])").WaitForAsync();
        }
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        }
        try
        {
            await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("body[data-ready=true]").WaitForAsync();
            await page.Locator(".nav-item[data-page=settings]").ClickAsync();
            await WaitForPageAsync(page, "document.querySelector('.page-content')?.dataset.page==='settings' && !document.querySelector('#content').hasAttribute('aria-busy')");
            var input = page.GetByRole(AriaRole.Spinbutton, new() { Name = "Pages maximales", Exact = true });
            await input.FillAsync("321"); await input.FocusAsync(); var inputNode = await input.ElementHandleAsync();
            await Open(source.Ticket.Id);
            await Button("Lignes suivantes").ClickAsync(); var originalView = await Modal().Locator(".library-ticket-content").ElementHandleAsync();
            await Button("Retrouver des précédents résolus").ClickAsync(); await Settled();
            var cards = Modal().Locator(".library-resolution-card");
            Require(await cards.CountAsync() == 2 && await cards.First.GetAttributeAsync("data-resolution-key") == saved.Document.Key
                && !(await Modal().InnerTextAsync()).Contains(foreign.Ticket.Id) && await page.Locator("dialog[open]").CountAsync() == 1,
                "A cached ticket opens confirmed same-installation predecessors inside its single reader dialog, with the distinctive-code match first.");
            var first = cards.First;
            Require((await first.Locator("blockquote").AllTextContentsAsync()).Contains(original)
                && await first.Locator("blockquote b").CountAsync() == 0
                && (await first.Locator(".library-resolution-reasons").InnerTextAsync()).Contains("E-1234")
                && (await first.InnerTextAsync()).Contains(precedent.Ticket.Url),
                "Predecessor cards explain shared terms and expose literal collected solution text, C1, source section and original ticket URL.");
            var completeSummary = await Modal().Locator(".library-resolution-limits").InnerTextAsync();
            Require(completeSummary.Contains("2 candidat(s) examiné(s) dans le cache · 2 précédent(s) affiché(s).")
                && !completeSummary.Contains("limitée") && !completeSummary.Contains("exhaustive")
                && !(await Modal().InnerTextAsync()).Contains("%")
                && await Modal().Locator(".library-resolution-source-warning").CountAsync() == 0,
                "An untruncated cache result reports examined and displayed counts without an unnecessary partial-selection warning, confidence percentage or obsolete-source warning for confirmed input.");
            await Capture("resolution-dark");
            var preservedResults = await Modal().Locator(".library-resolution").ElementHandleAsync();
            var queryBefore = await Manual().InputValueAsync(); var searchesBefore = calls.Count(operation => operation == "library.resolution");
            await first.GetByRole(AriaRole.Button, new() { Name = "Lire ce précédent", Exact = true }).ClickAsync();
            await Modal().Locator(".library-ticket-id").Filter(new() { HasText = precedent.Ticket.Id }).WaitForAsync();
            Require(await page.Locator("dialog[open]").CountAsync() == 1 && (await Modal().Locator(".library-detail-text").AllTextContentsAsync()).Contains(original)
                && app.Revision == revision && ReferenceEquals(app.Result, receipt),
                "Reading a predecessor retains exact content in the same dialog and leaves the active report untouched.");
            await Button("Retour aux précédents").ClickAsync(); await Settled();
            Require(await page.EvaluateAsync<bool>("node => node.isConnected", preservedResults)
                && await Manual().InputValueAsync() == queryBefore && calls.Count(operation => operation == "library.resolution") == searchesBefore,
                "Returning from a predecessor restores the existing result view and manual terms without repeating the search.");
            await cards.First.GetByRole(AriaRole.Button, new() { Name = "Ouvrir ce précédent dans GLPI", Exact = true }).ClickAsync();
            await Until(() => host.OpenedUrls.Count == 1);
            Require(host.OpenedUrls.SequenceEqual([precedent.Ticket.Url]), "The explicit GLPI action opens only the validated source URL of the selected predecessor.");
            await Button("Retour à la fiche").ClickAsync();
            Require(await page.EvaluateAsync<bool>("node => node.isConnected", originalView)
                && (await Modal().InnerTextAsync()).Contains("Observation source 51") && !(await Modal().InnerTextAsync()).Contains("Observation source 1\n"),
                "Returning to the original ticket preserves its already paged content instead of rebuilding the source reader.");
            await page.Keyboard.PressAsync("Escape");
            Require(await input.InputValueAsync() == "321" && await page.EvaluateAsync<bool>("node => node===document.activeElement && node.isConnected", inputNode)
                && app.Revision == revision && ReferenceEquals(app.Result, receipt),
                "Closing the resolution reader returns to the unchanged screen, unsaved field, focus and loaded report.");

            await Open(insufficient.Ticket.Id); await Button("Retrouver des précédents résolus").ClickAsync(); await Settled();
            Require(await cards.CountAsync() == 0 && (await Modal().InnerTextAsync()).Contains("titre conservé contient trop peu")
                && await Modal().Locator(".library-resolution-source-warning").IsVisibleAsync() && await Manual().IsVisibleAsync(),
                "An insufficient unconfirmed source explains the limitation and offers a bounded manual search in the same reader.");
            await Manual().FillAsync("lente"); await Manual().PressAsync("Enter"); await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Manual().FillAsync("laser"); await Manual().PressAsync("Enter"); await Settled();
            slowRelease.TrySetResult(); await slowFinished.Task.WaitAsync(TimeSpan.FromSeconds(5)); await delivery;
            Require(await cards.CountAsync() == 2 && !(await Modal().InnerTextAsync()).Contains(foreign.Ticket.Id),
                "A manual one-term query still isolates the source installation, and a late older result cannot replace it.");
            await Call(app, "app.appearance", new { theme = "light" }); await delivery;
            await Capture("resolution-light"); await page.SetViewportSizeAsync(600, 850); await Capture("resolution-compact");
            Require(await Modal().EvaluateAsync<bool>("node => node.getBoundingClientRect().left>=0 && node.getBoundingClientRect().right<=innerWidth && node.scrollWidth<=node.clientWidth+1")
                && await cards.Locator(".library-resolution-ticket-id").Filter(new() { HasText = precedent.Ticket.Id }).EvaluateAsync<bool>("node => {const range=document.createRange();range.selectNodeContents(node);return getComputedStyle(node).whiteSpace==='nowrap' && range.getClientRects().length===1;}")
                && (await Modal().Locator(".library-resolution-source-warning").InnerTextAsync()).Contains("vos termes saisis")
                && !(await Modal().Locator(".library-resolution-source-warning").InnerTextAsync()).Contains("son titre conservé sert")
                && await page.EvaluateAsync<bool>("document.documentElement.dataset.motion==='reduce' && !document.getAnimations().some(animation=>animation.playState==='running')")
                && errors.IsEmpty && externalRequests == 0 && !calls.Any(operation => operation is "library.capture" or "library.sync" or "library.loadRecords"),
                "Compact reduced-motion resolution names the manually entered terms, keeps a long ticket number on one line and respects the real CSP without hidden acquisition, synchronization or source replacement.");
            await Button("Fermer").ClickAsync(); await delivery;

            // These identical numbers belong to another GLPI installation. They
            // must neither replace a known source nor fill in a missing one.
            var sourceCollision = LibrarySeed(source.Ticket.Id, "Serveur stockage X-987 étranger", 2);
            sourceCollision.Ticket = new(source.Ticket.Id, "https://other-resolution.invalid/front/ticket.form.php?id=" + source.Ticket.Id);
            await store.SaveDetailAsync(sourceCollision, LibraryDetail(sourceCollision, "Contenu étranger distinct."), new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, "fixture-v1");
            var absentCollision = LibrarySeed(absent.Ticket.Id, "Imprimante laser E-1234 étrangère", 5);
            absentCollision.Ticket = new(absent.Ticket.Id, "https://other-resolution.invalid/front/ticket.form.php?id=" + absent.Ticket.Id);
            await store.SaveDetailAsync(absentCollision, LibraryDetail(absentCollision, "Autre installation uniquement."), new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, "fixture-v1");
            var invalidReferenceRefused = false;
            try { await Call(app, "library.reference", new { id = source.Ticket.Id, url = precedent.Ticket.Url }); }
            catch (InvalidDataException) { invalidReferenceRefused = true; }
            Require(invalidReferenceRefused, "The work-ticket cache lookup refuses an ID paired with another ticket URL before any resolution search.");

            await page.SetViewportSizeAsync(1440, 1000);
            await page.Locator(".nav-item[data-page=tickets]").ClickAsync();
            await WaitForPageAsync(page, "document.querySelector('.page-content')?.dataset.page==='tickets' && !document.querySelector('#content').hasAttribute('aria-busy')");
            var notes = page.GetByRole(AriaRole.Textbox, new() { Name = "Mes notes", Exact = true });
            const string unsavedNote = "Observation encore en cours de saisie, sans enregistrement.";
            await notes.FillAsync(unsavedNote); var noteNode = await notes.ElementHandleAsync();
            var workButton = page.GetByRole(AriaRole.Button, new() { Name = "Chercher des solutions similaires", Exact = true });
            await workButton.ClickAsync(); await Settled();
            Require(await cards.CountAsync() == 2 && await cards.First.GetAttributeAsync("data-resolution-key") == saved.Document.Key
                && !(await Modal().InnerTextAsync()).Contains("étranger")
                && await page.EvaluateAsync<bool>("document.querySelector('.page-content')?.dataset.page==='tickets'")
                && await notes.InputValueAsync() == unsavedNote && app.Revision == revision && ReferenceEquals(app.Result, receipt),
                "The real work-ticket button opens the same confirmed predecessors through its validated ID and installation while the work screen, note draft and report remain intact despite a foreign ID collision.");
            await Capture("resolution-work-ticket"); await Button("Fermer").ClickAsync();
            Require(await page.EvaluateAsync<bool>("node => node.isConnected", noteNode) && await notes.InputValueAsync() == unsavedNote
                && await workButton.EvaluateAsync<bool>("node => node===document.activeElement")
                && WorkplaceStore.Load(Path.Combine(_root, "workplace.json")).Tickets.Single(value => value.Key == sourcePersonal.Key).Notes.Length == 0,
                "Closing work-ticket predecessors restores the initiating button and the same unsaved personal editor without persisting its draft.");

            await page.Locator(".work-open-ticket").GetByRole(AriaRole.Button, new() { Name = absent.Ticket.Id + " · " + absent.Title, Exact = true }).ClickAsync();
            // A browser click dispatches the asynchronous open; wait for the new
            // ticket identity before typing, rather than editing the previous DOM.
            await page.Locator(".work-ticket-heading h2").Filter(new() { HasText = "#" + absent.Ticket.Id + " · " + absent.Title }).WaitForAsync();
            await notes.FillAsync("Saisie de la fiche absente conservée."); var missingNoteNode = await notes.ElementHandleAsync();
            var resolutionCount = calls.Count(operation => operation == "library.resolution");
            await workButton.ClickAsync();
            var missingDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Ticket absent de la bibliothèque", Exact = true });
            await missingDialog.WaitForAsync();
            var missingChecks = new Dictionary<string, bool>
            {
                ["own installation"] = (await missingDialog.InnerTextAsync()).Contains("https://library-fixture.invalid"),
                ["no fallback query"] = calls.Count(operation => operation == "library.resolution") == resolutionCount && await Modal().CountAsync() == 0,
                ["same editor"] = await page.EvaluateAsync<bool>("node => node.isConnected", missingNoteNode),
                ["unsaved note"] = await notes.InputValueAsync() == "Saisie de la fiche absente conservée.",
                ["same screen"] = await page.EvaluateAsync<bool>("document.querySelector('.page-content')?.dataset.page==='tickets'"),
                ["no hidden operation"] = !calls.Any(operation => operation is "library.capture" or "library.sync" or "library.loadRecords" or "workplace.ticket.read"),
                ["no runtime/network error"] = errors.IsEmpty && externalRequests == 0
            };
            if (missingChecks.Any(pair => !pair.Value)) Console.WriteLine("Missing-source failure: " + string.Join(", ", missingChecks.Where(pair => !pair.Value).Select(pair => pair.Key)));
            Require(missingChecks.Values.All(value => value),
                "A work ticket absent from its own installation gives an explicit local-cache message, never substitutes a foreign ticket with the same number, and keeps the screen and unsaved note without hidden collection or navigation.");
            await page.Keyboard.PressAsync("Escape"); await delivery;
        }
        finally { slowRelease.TrySetResult(); }
    }
}
