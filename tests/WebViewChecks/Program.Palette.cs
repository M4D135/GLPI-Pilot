using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckCommandPaletteInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(_root, "PaletteLibrary"), SourceTimeZone = "UTC" };
        new LibraryPreferencesStore().Save(preferences);
        var store = new TicketLibraryStore(preferences.DatabasePath);
        var seed = LibrarySeed("42", "Imprimante conservée", 5);
        const string original = "Solution locale <b>littérale</b>\nApostrophe C1 : \u0092 ; aucun appel GLPI.";
        var saved = await store.SaveDetailAsync(seed, LibraryDetail(seed, original), new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, "fixture-v1");
        var other = LibrarySeed("42", "Autre installation", 2);
        other.Ticket = new("42", "https://other-library-fixture.invalid/glpi/front/ticket.form.php?id=42");
        var otherSaved = await store.ObserveSeedAsync(other);
        await store.ObserveSeedAsync(LibrarySeed("7", "Métadonnées seules", 2));
        await store.ObserveSeedAsync(LibrarySeed("9", "Recherche lente synthétique", 2));
        for (var index = 100; index < 112; index++) await store.ObserveSeedAsync(LibrarySeed(index.ToString(), "42 42 42 42", 2));

        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
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
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new LibraryHost(); WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings { Theme = "dark", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.FollowWindowsTheme = false;
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
        app.LoadQualityFixture(QualityFixture()); var revision = app.Revision; var report = app.Result;
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            calls.Enqueue(op);
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            var result = await app.ExecuteAsync(op, data, ct);
            if (op == "library.search" && data.GetProperty("text").GetString() == "lente")
            {
                // Delay a genuine local-store reply to exercise a late response after cancellation.
                slowStarted.TrySetResult(); await slowRelease.Task.WaitAsync(TimeSpan.FromSeconds(15)); slowFinished.TrySetResult();
            }
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
        ILocator Search() => page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true });
        ILocator Palette() => page.Locator("dialog.command-palette");
        ILocator Sources() => Palette().GetByRole(AriaRole.Combobox, new() { Name = "Installation des tickets conservés", Exact = true });
        ILocator Result(string id) => Palette().Locator("[role=option]").Filter(new() { Has = page.Locator("strong").Filter(new() { HasText = "#" + id + " ·" }) });
        async Task Open() { await page.Keyboard.PressAsync("Control+k"); await Palette().WaitForAsync(); }
        async Task Ready(string id) => await WaitForPageAsync(page,
            "id => document.querySelector('.page-content')?.dataset.page===id && !document.querySelector('#content').hasAttribute('aria-busy')", id);
        async Task Query(string text)
        {
            await Search().FillAsync(text);
            await Palette().Locator(".command-palette-results[aria-busy=false]").WaitForAsync();
        }
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        }
        try
        {
            await page.GotoAsync("https://glpi-pilot.local/index.html");
            await page.Locator("body[data-ready=true]").WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Rechercher un écran, une action ou un ticket", Exact = true }).FocusAsync();
            await Open();
            Require(await Palette().Locator("[data-command-id^='page:']").CountAsync() == 35
                && await Palette().Locator("[data-command-id^='action:']").CountAsync() == 7
                && await Palette().Locator("[data-command-id='action:test-file']").CountAsync() == 1
                && await Palette().Locator("[data-command-id='action:test-exit']").CountAsync() == 0
                && !calls.Contains("library.search")
                && await Search().EvaluateAsync<bool>("node => document.activeElement===node && node.getAttribute('aria-controls')===document.querySelector('.command-palette-results').id"),
                "The singleton combobox exposes all screens, explicit actions and local test import without silently searching the cache on opening.");
            await Search().PressAsync("ArrowUp");
            Require(await Palette().Locator("[role=option]").Last.GetAttributeAsync("aria-selected") == "true", "Up wraps from the first command to the final command.");
            await Search().PressAsync("ArrowDown");
            Require(await Palette().Locator("[role=option]").First.GetAttributeAsync("aria-selected") == "true"
                && await Search().EvaluateAsync<bool>("node => document.getElementById(node.getAttribute('aria-activedescendant'))?.getAttribute('aria-selected')==='true'"),
                "Down wraps back to the first command while accessible selection remains on the focused combobox.");
            await page.Keyboard.PressAsync("Control+k");
            Require(await page.Locator("dialog[open]").CountAsync() == 0
                && await page.Locator(".search-command").EvaluateAsync<bool>("node => document.activeElement===node"),
                "Repeated Ctrl K closes the sole palette and restores its triggering control.");

            await Open(); await Query("repartition");
            Require(await Palette().Locator("[data-command-id='page:planning']").CountAsync() == 1, "Screen lookup ignores French accents without changing the stored ticket values.");
            await Search().PressAsync("Enter"); await Ready("planning");
            Require(await page.Locator("dialog[open]").CountAsync() == 0, "Enter executes the selected screen command and closes the palette.");
            await page.GetByRole(AriaRole.Button, new() { Name = "Personnaliser la navigation", Exact = true }).ClickAsync();
            var editor = page.Locator("dialog.navigation-modal"); await editor.WaitForAsync();
            var editorFocus = await page.EvaluateHandleAsync("document.activeElement");
            await page.Keyboard.PressAsync("Control+k");
            Require(await editor.IsVisibleAsync() && await page.Locator("dialog[open]").CountAsync() == 1
                && await page.EvaluateAsync<bool>("node => node===document.activeElement", editorFocus),
                "Ctrl K leaves a modal editor and its focus intact instead of stacking another dialog.");
            await editor.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

            await ClickNavigationAsync(page, "settings"); await Ready("settings");
            var retained = page.GetByRole(AriaRole.Spinbutton, new() { Name = "Pages maximales", Exact = true });
            await retained.FillAsync("321"); await retained.FocusAsync();
            var retainedNode = await retained.ElementHandleAsync();
            var beforeScroll = await page.Locator(".workspace").EvaluateAsync<double>("node => node.scrollTop");
            await Open(); await Query("42");
            var keys = await Palette().Locator("[data-ticket-key]").EvaluateAllAsync<string[]>("nodes => nodes.map(node=>node.dataset.ticketKey)");
            Require(keys.Length == 8 && keys.Take(2).ToHashSet().SetEquals(new[] { saved.Document.Key, otherSaved.Document.Key })
                && await Result("42").CountAsync() == 2,
                "The exact ticket ID leads the bounded results and equal IDs remain visibly qualified by their installations.");
            await Capture("palette-tickets-dark");
            await Palette().Locator("[data-ticket-key='" + saved.Document.Key + "']").ClickAsync();
            var ticket = page.Locator("dialog.library-ticket-modal");
            await ticket.Locator(".library-detail[aria-busy]").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            Require(await ticket.Locator(".library-detail-text").AllTextContentsAsync() is { } texts && texts.Contains(original)
                && await ticket.Locator(".library-detail-text b").CountAsync() == 0
                && app.Revision == revision && ReferenceEquals(app.Result, report),
                "The cached ticket reader keeps literal HTML and C1 characters and leaves the loaded report unchanged.");
            await page.Keyboard.PressAsync("Escape");
            Require(await retained.InputValueAsync() == "321"
                && await page.EvaluateAsync<bool>("node => node===document.activeElement && node.isConnected", retainedNode)
                && Math.Abs(await page.Locator(".workspace").EvaluateAsync<double>("node => node.scrollTop") - beforeScroll) <= 1,
                "Closing the cached reader preserves the original screen, unsaved input, focused element and reading position.");

            await Open(); await Search().FillAsync("lente");
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Query("7"); slowRelease.TrySetResult(); await slowFinished.Task.WaitAsync(TimeSpan.FromSeconds(5)); await delivery;
            Require(await Result("7").CountAsync() == 1 && await Result("9").CountAsync() == 0,
                "A late genuine local-search response cannot replace the newer query, including a single-digit ticket number.");
            await Result("7").ClickAsync();
            await page.Locator("dialog.library-ticket-modal").GetByText("Aucun contenu recueilli", new() { Exact = true }).WaitForAsync();
            Require(await page.Locator("dialog.library-ticket-modal .library-detail-text").CountAsync() == 0, "An index-only ticket remains explicitly without collected content instead of displaying a fabricated empty detail.");
            await page.Keyboard.PressAsync("Escape");

            await Open(); await Query("00042");
            await WaitForPageAsync(page, "document.querySelector('dialog.command-palette select')?.options.length===3");
            await Sources().SelectOptionAsync(otherSaved.Document.Installation);
            await Palette().Locator(".command-palette-results[aria-busy=false]").WaitForAsync();
            Require(await Palette().Locator("[data-ticket-key]").CountAsync() == 1
                && await Palette().Locator("[data-ticket-key]").GetAttributeAsync("data-ticket-key") == otherSaved.Document.Key,
                "The explicit installation filter applies to an exact numeric lookup with leading zeroes.");
            await Palette().GetByRole(AriaRole.Button, new() { Name = "Ouvrir la bibliothèque filtrée", Exact = true }).ClickAsync();
            await Ready("library");
            Require(await page.GetByRole(AriaRole.Searchbox, new() { Name = "Rechercher dans la bibliothèque", Exact = true }).InputValueAsync() == "00042"
                && await page.GetByRole(AriaRole.Combobox, new() { Name = "Installation GLPI", Exact = true }).InputValueAsync() == otherSaved.Document.Installation
                && await page.Locator(".library-result").CountAsync() == 1,
                "Opening the full library carries the entered query and installation as explicit filters.");

            await Call(app, "app.appearance", new { theme = "light" }); await delivery;
            await Open(); await Query("42"); await Capture("palette-tickets-light");
            await page.SetViewportSizeAsync(600, 850); await Capture("palette-compact");
            Require(await Palette().EvaluateAsync<bool>("node => node.getBoundingClientRect().left>=0 && node.getBoundingClientRect().right<=innerWidth && node.scrollWidth<=node.clientWidth+1")
                && await page.EvaluateAsync<bool>("document.documentElement.dataset.motion==='reduce' && !document.getAnimations().some(animation=>animation.playState==='running')")
                && errors.IsEmpty && externalRequests == 0
                && !calls.Any(op => op is "library.capture" or "library.sync" or "collect.run" or "library.loadRecords")
                && host.OpenedUrls.IsEmpty,
                "The compact reduced-motion palette remains usable under the strict CSP without network reads, background capture, synchronization or report replacement.");
            await page.Keyboard.PressAsync("Escape"); await delivery;
        }
        finally { slowRelease.TrySetResult(); }
    }
}
