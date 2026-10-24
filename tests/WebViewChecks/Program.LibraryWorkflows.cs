using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private const string WorkflowSource = "https://library-workflows.invalid/glpi/front/ticket.php";
    private const string WorkflowLiteral = "<b>Texte conservé</b>\nDeuxième ligne. null";
    private const string WorkflowOlderCopy = "Copie complète antérieure 45, à conserver après une lecture partielle.";

    private static async Task RunLibraryWorkflowsAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var preferences = new LibraryPreferences
        {
            LocalFolder = Path.Combine(_root, "WorkflowLibrary"), SourceTimeZone = "UTC",
            DisplayName = "Équipe synthétique", AutoSync = false
        };
        new LibraryPreferencesStore().Save(preferences);
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable,
            Args = ["--no-sandbox", "--disable-dev-shm-usage"]
        });
        await using var portal = await TicketLibraryCaptureBrowserFixture.OpenAsync(browser, WorkflowSource);
        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce
        });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(12000);
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0;
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch
            { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });

        var host = new LibraryHost();
        var requestOperations = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var observed = new ConcurrentQueue<(string Operation, JsonElement Arguments, JsonElement Result)>();
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var deliveryGate = new object();
        var delayEntries = false; string? heldReply = null;
        var heldReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Deliver(string json)
        {
            lock (deliveryGate) delivery = delivery.ContinueWith(async _ =>
            {
                if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json);
            }, TaskScheduler.Default).Unwrap();
        }
        void Post(string json)
        {
            using var document = JsonDocument.Parse(json); var message = document.RootElement;
            lock (deliveryGate)
            {
                if (delayEntries && message.TryGetProperty("id", out var id) && message.TryGetProperty("result", out _)
                    && requestOperations.TryGetValue(id.GetString()!, out var operation) && operation == "library.capture.entries")
                {
                    // Hold a genuine completed bridge reply in transit; no API result is substituted.
                    delayEntries = false; heldReply = json; heldReady.TrySetResult(); return;
                }
            }
            Deliver(json);
        }
        var settings = new AppSettings
        {
            PortalUrl = WorkflowSource, Theme = "dark", ListTimeoutSeconds = 5, DetailTimeoutSeconds = 5,
            MaxPages = 10, MaxRows = 1000, ExportFolder = Path.Combine(_root, "Exports")
        };
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings, portal.Service);
        var store = new TicketLibraryStore(preferences.DatabasePath);
        var seedRun = await SeedWorkflowCaptureAsync(store, portal, app.Settings, preferences);
        var team = new TeamWorkspaceStore(preferences.DatabasePath);
        var contribution = await SeedWorkflowConflictsAsync(team);
        var teamBefore = TeamWorkspaceCodec.Write((await team.GetAsync(contribution.Id))!);
        var teamCountsBefore = await team.CountsAsync();
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (operation, data, ct) =>
        {
            if (operation == "app.ready") return new { ready = true };
            if (operation == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            var result = await app.ExecuteAsync(operation, data, ct);
            observed.Enqueue((operation, data.Clone(), JsonSerializer.SerializeToElement(result, PilotApplication.Json)));
            return result;
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) =>
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("id", out var id) && document.RootElement.TryGetProperty("op", out var operation))
                requestOperations[id.GetString()!] = operation.GetString()!;
            bridge.Receive(json);
        });
        await page.AddInitScriptAsync("""
            (() => { const listeners = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, handler) => { if (name === 'message') listeners.add(handler); },
              removeEventListener: (name, handler) => listeners.delete(handler)
            }; window.__pilotDeliver = message => { for (const listener of listeners) listener({ data: message }); }; })();
            """);

        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator Modal(string name) => page.GetByRole(AriaRole.Dialog, new() { Name = name, Exact = true });
        ILocator CaptureSummary() => page.GetByRole(AriaRole.Region, new() { Name = "Dernière collecte conservée", Exact = true });
        ILocator CaptureDialog() => Modal("Détail et reprise de la collecte");
        ILocator VersionDialog() => Modal("Versions conservées");
        ILocator ComparisonDialog() => Modal("Comparer les versions de la contribution");
        async Task Ready(string id) => await WaitForNavigationTargetAsync(page, id);
        async Task OpenLibrary()
        {
            await ClickNavigationAsync(page, "library"); await Ready("library");
            await CaptureSummary().WaitForAsync();
        }
        async Task OpenCapture()
        {
            await CaptureSummary().GetByRole(AriaRole.Button, new() { Name = "Détail de la collecte", Exact = true }).ClickAsync();
            await CaptureDialog().Locator("tbody tr").First.WaitForAsync();
        }
        async Task Close(ILocator modal)
        {
            await modal.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).First.ClickAsync();
            await modal.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots");
            if (index < 0 || index + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
            await page.Locator("#notifications .toast button").EvaluateAllAsync<int>("buttons => { buttons.forEach(button=>button.click()); return buttons.length; }");
            await page.ScreenshotAsync(new()
            { Path = Path.Combine(directory, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        }
        async Task WaitCaptureFinished()
        {
            await WaitForPageAsync(page, "document.querySelector('.library-progress')?.hidden===true && !!document.querySelector('.library-capture-summary:not([hidden])')");
            await delivery;
        }

        await page.GotoAsync("https://glpi-pilot.local/index.html");
        await page.Locator("body[data-ready=true]").WaitForAsync();
        await OpenLibrary();
        var initial = await new TicketLibraryStore(preferences.DatabasePath).LatestCaptureAsync();
        Require(initial is { Status: "cancelled", Observed: 30, Targeted: 30, Reused: 4, Failed: 1, Partial: 1, Unconfirmed: 1, RetryCount: 26 }
            && initial.RunId == seedRun && (await CaptureSummary().InnerTextAsync()).Contains("Collecte arrêtée")
            && await CaptureSummary().Locator("dl").CountAsync() == 0
            && await CaptureSummary().GetByRole(AriaRole.Button, new() { Name = "Reprendre les 26 ticket(s) à relire", Exact = true }).IsEnabledAsync(),
            "A durable interrupted collection is shown compactly from SQLite with its genuine retry count, without claiming that the last read was complete.");
        await Capture("library-workflows-summary-dark");
        await delivery; await page.ReloadAsync(); await page.Locator("body[data-ready=true]").WaitForAsync(); await OpenLibrary();
        Require((await new TicketLibraryStore(preferences.DatabasePath).LatestCaptureAsync())?.RunId == seedRun
            && (await CaptureSummary().InnerTextAsync()).Contains("26 fiches à reprendre"),
            "Reloading the application page and reopening the local store retains the exact last collection and its resumable observations.");

        await OpenCapture();
        var capture = CaptureDialog();
        var retryFilter = capture.GetByRole(AriaRole.Checkbox, new() { Name = "Afficher uniquement les tickets à relire", Exact = true });
        var retryFirst = await capture.Locator("tbody .library-ticket-id").AllTextContentsAsync();
        await capture.GetByRole(AriaRole.Button, new() { Name = "Tickets suivants", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.library-capture-detail .library-pagination')?.textContent.includes('26–26')");
        var retryLast = await capture.Locator("tbody .library-ticket-id").AllTextContentsAsync();
        await retryFilter.UncheckAsync();
        await WaitForPageAsync(page, "document.querySelector('.library-capture-detail .library-pagination')?.textContent.includes('1–25 sur 30')");
        Require(retryFirst.Count == 25 && retryLast.Count == 1 && retryFirst.Concat(retryLast).Distinct().Count() == 26
            && retryFirst.All(id => !new[] { "#41", "#42", "#43", "#44" }.Contains(id))
            && await capture.Locator("tbody tr").CountAsync() == 25
            && (await capture.InnerTextAsync()).Contains("Lectures partielles"),
            "The collection detail paginates all retryable tickets exactly once, resets its page when the filter changes, and exposes the detailed read counters.");

        var partialRow = capture.Locator("tbody tr").Filter(new() { HasText = "#45" });
        Require((await partialRow.InnerTextAsync()).Contains("Lecture partielle")
            && (await partialRow.InnerTextAsync()).Contains("Une ancienne copie complète peut avoir été conservée"),
            "A partial attempt is described as a partial read and explicitly distinguishes it from a complete older local copy.");
        await partialRow.GetByRole(AriaRole.Button, new() { Name = "Lire le cache", Exact = true }).ClickAsync();
        var reader = page.Locator("dialog.library-ticket-modal");
        await reader.Locator(".library-detail:not([aria-busy])").WaitForAsync();
        Require((await reader.InnerTextAsync()).Contains(WorkflowOlderCopy)
            && (await store.GetAsync(portal.Seed("45").Ticket)) is { DetailComplete: true },
            "The collection detail opens the retained complete ticket through the real cached reader after a newer partial attempt.");
        await Close(reader); await Close(capture);

        delayEntries = true; heldReply = null; heldReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await CaptureSummary().GetByRole(AriaRole.Button, new() { Name = "Détail de la collecte", Exact = true }).ClickAsync();
        await heldReady.Task.WaitAsync(TimeSpan.FromSeconds(12));
        await page.Keyboard.PressAsync("Escape");
        await CaptureDialog().WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var focusReturned = await page.EvaluateAsync<bool>("document.activeElement?.textContent.includes('Détail de la collecte')===true");
        await ClickNavigationAsync(page, "home"); await Ready("home");
        Deliver(heldReply!); await delivery;
        Require(focusReturned && await page.GetByRole(AriaRole.Dialog).CountAsync() == 0
            && await page.Locator(".page-content[data-page=home]").CountAsync() == 1
            && await page.Locator(".notice.danger").CountAsync() == 0,
            "Closing the read dialog cancels its wait and restores focus; a genuine late bridge reply cannot reopen it or replace the next screen.");
        await OpenLibrary();

        portal.TicketIds.Clear(); portal.TicketIds.AddRange(["41", "45", "46"]);
        portal.PauseIndexNumber = portal.AllIndexRequests + 2;
        portal.DetailRequests.Clear();
        await CaptureSummary().GetByRole(AriaRole.Button, new() { Name = "Reprendre les 26 ticket(s) à relire", Exact = true }).ClickAsync();
        await portal.WaitForIndexAsync(portal.PauseIndexNumber);
        await Button("Annuler le traitement").ClickAsync();
        await WaitCaptureFinished();
        var stopped = (await store.LatestCaptureAsync())!;
        Require(stopped.RunId != seedRun && stopped.Mode == "retry" && stopped.Status == "cancelled"
            && stopped.Read == 2 && stopped.Targeted == 26 && !stopped.FinalScopeVerified && stopped.RetryCount >= 24
            && portal.DetailRequests.ToArray().Order().SequenceEqual(new[] { "45", "46" })
            && portal.Context.Pages.All(item => ReferenceEquals(item, portal.Page) || ReferenceEquals(item, portal.Service.TicketPage))
            && portal.Page.Url == WorkflowSource,
            "Cancelling a real retry during its final GLPI index check preserves received content, records a resumable cancellation, and leaves the user's search intact.");
        await Capture("library-workflows-cancelled");

        portal.PauseIndexNumber = 0; portal.DetailRequests.Clear();
        var retryButton = CaptureSummary().GetByRole(AriaRole.Button).Filter(new() { HasText = "ticket(s) à relire" });
        await retryButton.ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('.library-progress')?.hidden===false");
        await WaitCaptureFinished();
        var resumed = (await store.LatestCaptureAsync())!;
        var resumedEntries = await store.CaptureEntriesAsync(resumed.RunId, 0, 100);
        var retryInvocation = observed.Last(call => call.Operation == "library.capture");
        Require(resumed.Status == "completed" && resumed.Mode == "retry" && resumed.RunId != stopped.RunId
            && resumed.Observed == 3 && resumed.Targeted == 26 && resumed.Read == 2 && resumed.FinalScopeVerified
            && resumedEntries.Items.Count(entry => entry.State == "unavailable") == 24
            && portal.DetailRequests.ToArray().Order().SequenceEqual(new[] { "45", "46" })
            && retryInvocation.Arguments.GetProperty("runId").GetString() == stopped.RunId
            && (await store.GetAsync(portal.Seed("41").Ticket))?.DetailComplete == true
            && await store.GetAsync(portal.Seed("70").Ticket) is not null,
            "The retry button resumes the selected durable run, rereads only its still-accessible targets, and retains both confirmed and now-absent cached tickets.");

        await ClickNavigationAsync(page, "data"); await WaitForNavigationTargetAsync(page, "data");
        await page.Locator("[data-open-panel=team]").ClickAsync();
        await page.Locator("dialog[data-workspace-panel=team]").WaitForAsync();
        var card = page.Locator(".library-result[data-workspace-id='" + contribution.Id + "']");
        await card.GetByRole(AriaRole.Button, new() { Name = "Examiner les versions", Exact = true }).ClickAsync();
        var versions = VersionDialog();
        await versions.Locator(".library-detail-section").First.WaitForAsync();
        var firstPairs = observed.Last(call => call.Operation == "workspace.conflicts").Result.GetProperty("items")
            .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        await versions.GetByRole(AriaRole.Button, new() { Name = "Versions suivantes", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('dialog[aria-label=\"Versions conservées\"] .library-pagination')?.textContent.includes('6–8 sur 8')");
        var lastPairs = observed.Last(call => call.Operation == "workspace.conflicts").Result.GetProperty("items")
            .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        Require(firstPairs.Length == 5 && lastPairs.Length == 3 && firstPairs.Concat(lastPairs).Distinct().Count() == 8
            && await versions.Locator(".library-detail-section").CountAsync() == 3
            && (await versions.InnerTextAsync()).Contains("Versions concurrentes"),
            "All eight archived conflict pairs remain reachable through the real five-pair pagination with a readable French conflict reason.");

        await versions.Locator(".library-detail-section").First.Locator("summary").ClickAsync();
        await versions.Locator(".library-detail-section").First.GetByRole(AriaRole.Button, new() { Name = "Comparer tous les champs", Exact = true }).ClickAsync();
        var comparison = ComparisonDialog(); await comparison.Locator("tbody tr").First.WaitForAsync();
        var compared = observed.Last(call => call.Operation == "workspace.conflict.compare");
        var pair = (await team.GetConflictAsync(contribution.Id, compared.Arguments.GetProperty("conflictId").GetString()!))!;
        var search = comparison.GetByRole(AriaRole.Searchbox, new() { Name = "Filtrer les différences des versions", Exact = true });
        async Task<IReadOnlyList<string>> ComparedValues(string path)
        {
            await search.FillAsync(path);
            var row = comparison.Locator("tbody tr").Filter(new() { Has = page.Locator("code").Filter(new() { HasText = path }) });
            await row.WaitForAsync();
            return await row.Locator("td pre").AllTextContentsAsync();
        }
        var hours = await ComparedValues("/payload/schedule/0/start");
        var nullable = await ComparedValues("/payload/nullable");
        var empty = await ComparedValues("/payload/empty");
        var removed = await ComparedValues("/payload/onlyCurrent");
        var added = await ComparedValues("/payload/onlyIncoming");
        var literal = await ComparedValues("/payload/a~1b");
        Require(hours.SequenceEqual(new[] { "08:00", "09:30" })
            && nullable.SequenceEqual(new[] { "Valeur nulle", "Texte vide" })
            && empty.SequenceEqual(new[] { "Texte vide", "Valeur nulle" })
            && removed.SequenceEqual(new[] { "Avant", "Champ absent" })
            && added.SequenceEqual(new[] { "Champ absent", "Après" })
            && literal[0] == WorkflowLiteral && literal[1] == WorkflowLiteral.Replace("conservé", "concurrent", StringComparison.Ordinal)
            && await comparison.Locator("b").CountAsync() == 0,
            "Identical contribution summaries reveal actual business changes, distinguishing absent, null and empty fields while retaining literal markup and line breaks.");
        await Capture("library-workflows-comparison-literal-dark");

        await search.FillAsync("");
        var countBefore = await comparison.Locator("tbody tr").CountAsync();
        await comparison.GetByRole(AriaRole.Button, new() { Name = "Différences suivantes", Exact = true }).ClickAsync();
        var secondPaths = await comparison.Locator("tbody .library-field-path").AllTextContentsAsync();
        await search.FillAsync("onlyIncoming");
        var filterResets = await comparison.Locator("tbody tr").CountAsync() == 1
            && await comparison.GetByRole(AriaRole.Button, new() { Name = "Différences précédentes", Exact = true }).IsDisabledAsync();
        await ComparedValues("/payload/longText");
        Require(countBefore == 50 && secondPaths.Count > 0 && secondPaths.Distinct().Count() == secondPaths.Count && filterResets
            && (await comparison.InnerTextAsync()).Contains("Aperçu limité ; valeur entière dans l’export.")
            && !((await comparison.Locator("tbody tr").InnerTextAsync()).Contains("CURRENT_END", StringComparison.Ordinal)),
            "Large real comparisons paginate their differences, reset the page when filtering, and disclose bounded previews even when the differing suffix is outside the displayed text.");

        host.NextSave = Path.Combine(_root, "complete-conflict-versions.json");
        await comparison.GetByRole(AriaRole.Button, new() { Name = "Exporter les deux versions complètes", Exact = true }).ClickAsync();
        await Until(() => File.Exists(host.NextSave));
        using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(host.NextSave));
        var export = exported.RootElement;
        Require(export.GetProperty("schema").GetString() == "glpi-pilot.team-conflict"
            && export.GetProperty("itemId").GetString() == contribution.Id
            && export.GetProperty("currentHash").GetString() == TeamWorkspaceCodec.ContentHash(pair.Current)
            && export.GetProperty("incomingHash").GetString() == TeamWorkspaceCodec.ContentHash(pair.Incoming)
            && export.GetProperty("current").GetProperty("payload").GetProperty("longText").GetString()!.EndsWith("CURRENT_END", StringComparison.Ordinal)
            && export.GetProperty("incoming").GetProperty("payload").GetProperty("longText").GetString()!.EndsWith("INCOMING_END", StringComparison.Ordinal)
            && export.GetProperty("current").GetProperty("payload").GetProperty("a/b").GetString() == WorkflowLiteral
            && TeamWorkspaceCodec.Write((await team.GetAsync(contribution.Id))!) == teamBefore
            && await team.CountsAsync() == teamCountsBefore,
            "Explicit conflict export writes both complete archived payloads and exact hashes, including their unseen suffixes, without modifying a contribution or its synchronization queue.");

        await page.SetViewportSizeAsync(600, 850);
        await search.FillAsync("/payload/schedule/0/start"); await Capture("library-workflows-comparison-compact");
        var compact = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth+1 && document.querySelector('dialog[aria-label=\"Comparer les versions de la contribution\"]').getBoundingClientRect().width<=innerWidth");
        await page.Keyboard.PressAsync("Escape");
        var nativeSearchCleared = await search.InputValueAsync() == "" && await comparison.IsVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await comparison.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var previousDialogFocus = await versions.IsVisibleAsync()
            && await page.EvaluateAsync<bool>("document.activeElement?.textContent.includes('Comparer tous les champs')===true");
        await page.Keyboard.PressAsync("Escape"); await versions.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.SetViewportSizeAsync(1440, 1000);
        await page.Locator("dialog[data-workspace-panel=team]").GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).First.ClickAsync();
        await Button("Changer de thème").ClickAsync();
        await ClickNavigationAsync(page, "data"); await Ready("data");
        await page.Locator("[data-open-panel=team]").ClickAsync();
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        await Capture("library-workflows-team-light");
        Require(compact && nativeSearchCleared && previousDialogFocus && errors.IsEmpty && externalRequests == 0 && host.OpenedUrls.IsEmpty
            && await page.EvaluateAsync<bool>("document.documentElement.dataset.domeniaMotion==='off'")
            && !observed.Any(call => call.Operation is "workspace.save" or "workspace.remove" or "workplace.actions.confirm"),
            "The new dialogs remain usable at 600 pixels; Escape clears the native search then closes the dialog with focus restored, while reduced motion, themes and read-only behavior remain intact.");
        await delivery;
    }

    private static async Task<string> SeedWorkflowCaptureAsync(TicketLibraryStore store, TicketLibraryCaptureBrowserFixture portal,
        AppSettings settings, LibraryPreferences preferences)
    {
        await store.InitializeAsync();
        var ids = Enumerable.Range(41, 30).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var run = await store.BeginCaptureAsync(TicketLibraryCaptureContext.FromSettings(settings, preferences), "incremental");
        await store.SetCaptureIndexAsync(run, portal.Index(ids));
        foreach (var id in ids) await store.ObserveCaptureSeedAsync(run, portal.Seed(id));
        foreach (var id in ids.Take(4))
        {
            var seed = portal.Seed(id);
            await store.SaveDetailAsync(seed, LibraryDetail(seed, "Copie complète déjà confirmée " + id),
                new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true }, TicketLibraryAcquisition.ReaderSignature);
            await store.RecordCaptureReuseAsync(run, TicketLibraryRules.Key(seed.Ticket));
        }
        var partialSeed = portal.Seed("45");
        await store.SaveDetailAsync(partialSeed, LibraryDetail(partialSeed, WorkflowOlderCopy),
            new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true }, TicketLibraryAcquisition.ReaderSignature);
        var partial = LibraryDetail(partialSeed, "Nouvel essai partiel 45");
        partial.Sections[0].Data.Complete = false; partial.Tabs[0].Loaded = false;
        await store.RecordCaptureReadingAsync(run, TicketLibraryRules.Key(partialSeed.Ticket));
        await store.SaveCaptureDetailAsync(run, partialSeed, new()
        {
            Detail = partial, Coverage = new() { TabsDiscovered = 1, TabsLoaded = 0, Complete = false }
        }, (await store.GetAsync(partialSeed.Ticket))!.LocalRevision);
        await store.RecordCaptureFailureAsync(run, TicketLibraryRules.Key(portal.Seed("46").Ticket), "read-timeout");
        var unconfirmed = portal.Seed("47");
        await store.SaveCaptureDetailAsync(run, unconfirmed, new()
        {
            Detail = LibraryDetail(unconfirmed, "Corps reçu avant l’annulation"), Coverage = new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true }
        }, (await store.GetAsync(unconfirmed.Ticket))!.LocalRevision);
        await store.RecordCaptureReadingAsync(run, TicketLibraryRules.Key(portal.Seed("48").Ticket));
        await store.FinishCaptureAsync(run, "cancelled",
            ["Les pièces jointes sont référencées ; leurs fichiers binaires ne sont pas téléchargés.", "Observation synthétique <b>littérale</b>."]);
        TicketLibraryStore.ReleaseCapture(run);
        return run;
    }

    private static async Task<TeamWorkspaceItem> SeedWorkflowConflictsAsync(TeamWorkspaceStore store)
    {
        var current = new TeamWorkspaceItem
        {
            Id = Guid.NewGuid().ToString("N"), Kind = "statistics", Title = "Horaires et données de l’équipe synthétique",
            Body = "Même résumé pour les deux versions conservées.", Status = "published", Author = "Équipe synthétique",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, CentralRevision = 1, Payload = WorkflowPayload(false, 0)
        };
        for (var index = 1; index <= 8; index++)
        {
            current.CentralRevision = index;
            var incoming = current.Copy(); incoming.Payload = WorkflowPayload(true, index);
            await store.ApplyChangeAsync(new(index, current, incoming, "concurrent-edit"), default);
        }
        return current;
    }

    private static JsonElement WorkflowPayload(bool incoming, int variant)
    {
        var fields = Enumerable.Range(0, 55).ToDictionary(index => "field" + index.ToString("D2"),
            index => (object?)((incoming ? "Après " : "Avant ") + index));
        var payload = new Dictionary<string, object?>
        {
            ["schedule"] = new[] { new { start = incoming ? "09:30" : "08:00", end = "12:00" } },
            ["nullable"] = incoming ? "" : null, ["empty"] = incoming ? null : "",
            ["a/b"] = incoming ? WorkflowLiteral.Replace("conservé", "concurrent", StringComparison.Ordinal) : WorkflowLiteral,
            ["a~b"] = incoming ? "Après le tilde" : "Avant le tilde",
            ["longText"] = new string('x', TeamWorkspaceConflictComparison.MaximumValueCharacters - 1) + "🧰" + (incoming ? "INCOMING_END" : "CURRENT_END"),
            ["fields"] = fields, ["variant"] = variant
        };
        payload[incoming ? "onlyIncoming" : "onlyCurrent"] = incoming ? "Après" : "Avant";
        return JsonSerializer.SerializeToElement(payload, PilotApplication.Json);
    }
}
