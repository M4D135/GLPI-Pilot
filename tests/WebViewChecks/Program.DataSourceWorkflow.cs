using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckDataSourceWorkflowInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        var settings = new AppSettings { Theme = "light", PortalUrl = "https://glpi-fixture.invalid/front/ticket.php?criteria=incidents", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.OnboardingCompleted = true; settings.Interface.FollowWindowsTheme = false;
        var csv = Path.Combine(_root, "extrait-local.csv");
        await File.WriteAllTextAsync(csv, "ID;Titre;Statut;Attribué à - Technicien\n31101;Incident fictif A;Nouveau;Technicien exemple\n31102;Incident fictif B;En cours;Technicien exemple\n");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await CheckStartupTicketNavigationInterfaceAsync(browser, web, settings);
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var operations = new ConcurrentQueue<string>(); var rejected = new ConcurrentQueue<string>();
        var campaigns = new ConcurrentQueue<JsonElement>(); var externalRequests = 0; var blockedLiveCalls = 0;
        var waitForCancellation = false; var returnFailedCampaign = false;
        var failSingleCollection = false;
        var startupRuns = 0; var startupUiReady = false; AppSettings? startupSettings = null;
        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialCardEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialCardRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? singleCollectionEntered = null, singleCollectionRelease = null, singleCollectionStopped = null;
        var cancellationPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long cancellationRevision = 0;
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
        var host = new TestHost(_root) { Page = page, NextOpen = csv }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
        var restoreStartupFixture = app.ConfigureStartupCollectionFixture(async (_, ct) =>
        {
            Interlocked.Increment(ref startupRuns); startupSettings = app.Settings.Copy();
            startupUiReady = await page.EvaluateAsync<bool>("() => document.body.dataset.ready==='true'&&!!document.querySelector('.page-content[data-page=home]')&&!document.querySelector('#content').hasAttribute('aria-busy')");
            bridge?.Publish("operation.progress", new { message = "Collecte synthétique au démarrage en cours…" });
            startupEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return new { collected = true }; }
            finally { startupStopped.TrySetResult(); }
        });
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            operations.Enqueue(op);
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            // Real-mode UI checks never open even the reserved .invalid fixture.
            // Successful collections below use the real isolated C# demo engine.
            if (!app.IsTestMode && op is "collect.run" or "collect.connect" or "collect.campaign")
            { Interlocked.Increment(ref blockedLiveCalls); throw new InvalidOperationException("Unexpected real portal operation in the isolated UI fixture."); }
            if (op == "collect.run" && app.IsTestMode)
            {
                if (failSingleCollection) throw new InvalidOperationException("Échec de collecte synthétique du contrôle de navigation.");
                if (singleCollectionRelease is { } release)
                {
                    bridge?.Publish("operation.state", new { busy = true, operation = op });
                    singleCollectionEntered!.TrySetResult();
                    try { await release.Task.WaitAsync(ct); }
                    finally { bridge?.Publish("operation.state", new { busy = false, operation = op }); singleCollectionStopped!.TrySetResult(); }
                }
            }
            if (op == "collect.campaign" && waitForCancellation)
            {
                // Controlled pause after genuine simulated work has already
                // changed the active profile and result revision. This catches
                // accidental retries with a pre-cancellation preparation.
                var first = app.Settings.Sources.First(source => source.IncludedInCampaign);
                await app.ExecuteAsync("sources.select", JsonSerializer.SerializeToElement(new { id = first.Id, revision = app.Revision }, PilotApplication.Json), ct);
                await app.ExecuteAsync("collect.run", JsonSerializer.SerializeToElement(new { }, PilotApplication.Json), ct);
                cancellationRevision = app.Revision;
                bridge?.Publish("campaign.progress", new[] { new { first.Name, State = "Terminé", Rows = app.Result!.Rows.Count } });
                cancellationPrepared.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            // Explicit UI outcome fixture, separate from the genuine campaign
            // exercised below: a server may return a completed all-failed batch.
            if (op == "collect.campaign" && returnFailedCampaign)
                return new { sources = app.Settings.Sources.Where(source => source.IncludedInCampaign).Select(source => new { source.Id, source.Name, State = "Échec", Rows = 0, Detail = "Échec synthétique du scénario de contrôle." }).ToArray(), path = "" };
            try
            {
                var result = await app.ExecuteAsync(op, data, ct);
                if (op == "calendar.summary" && initialCardEntered.TrySetResult())
                    await initialCardRelease.Task.WaitAsync(ct);
                if (op == "collect.campaign") campaigns.Enqueue(JsonSerializer.SerializeToElement(result, PilotApplication.Json));
                return result;
            }
            catch { rejected.Enqueue(op); throw; }
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
        ILocator Group() => page.GetByRole(AriaRole.Dialog, new() { Name = "Collecter plusieurs recherches", Exact = true });
        async Task Idle() => await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending&&!document.querySelector('#content').hasAttribute('aria-busy')&&!document.querySelector('button[aria-busy=true]')");
        async Task<bool> OnPage(string id) => await page.Locator(".page-content").GetAttributeAsync("data-page") == id;
        async Task ShowLiveSourceControl()
        {
            // Only the shell source label is projected as Live to exercise its
            // global refresh branch. Every successful collect.run still uses
            // the genuine isolated C# demo engine; no GLPI portal is contacted.
            var bootstrap = await Call(app, "app.bootstrap");
            var live = bootstrap.GetProperty("session").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
            foreach (var flag in new[] { "demo", "testMode", "cachedSource", "historical", "fileSource" })
                live[flag] = JsonSerializer.SerializeToElement(false);
            live["sourceKind"] = JsonSerializer.SerializeToElement("live");
            bridge!.Publish("session.changed", live); await delivery;
            await WaitForPageAsync(page, "document.querySelector('.source-mode-picker')?.value==='live'");
        }
        async Task DataPage()
        {
            await ClickNavigationAsync(page, "data"); await WaitForNavigationTargetAsync(page, "data");
            await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        }
        async Task CloseDialog(ILocator dialog)
        { await dialog.Locator(":scope > .modal-heading button").ClickAsync(); await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached }); }
        var capture = Array.IndexOf(args, "--screenshots");
        var captureDirectory = capture >= 0 && capture + 1 < args.Length ? args[capture + 1] : "";
        async Task Capture(string name)
        {
            if (captureDirectory.Length == 0) return;
            Directory.CreateDirectory(captureDirectory);
            // Let the normal notifications finish before capturing the page;
            // screenshots show the UI as it settles after the rapid test steps.
            var latestNotice = page.Locator("#notifications .toast").Last;
            if (await latestNotice.CountAsync() > 0) await latestNotice.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 20000 });
            await page.ScreenshotAsync(new() { Path = Path.Combine(captureDirectory, name + ".png"), FullPage = true });
            await GuideCaptureAsync(page, name);
        }

        async Task OpenApplication(bool reload = false)
        {
            var stage = "navigation";
            try
            {
                // Commit guarantees the new document. Its modules and C# bootstrap
                // then use the existing application-ready budget, not the short
                // interaction timeout attached to the browser's load event.
                if (reload) await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Commit });
                else await page.GotoAsync("https://glpi-pilot.local/index.html", new() { WaitUntil = WaitUntilState.Commit });
                stage = "application-ready";
                await ReadyAsync(page);
            }
            catch
            {
                Console.WriteLine("SOURCE STARTUP: " + JsonSerializer.Serialize(new { stage, reload, operations = operations.TakeLast(30).ToArray(), errors = errors.Take(10).ToArray() }));
                try
                {
                    var dom = await page.EvaluateAsync<JsonElement>("""
                        () => ({document:document.readyState, ready:document.body?.dataset.ready??null,
                          loading:!!document.querySelector('#app.app-loading'), page:document.querySelector('.page-content')?.dataset.page??null,
                          startupError:!!document.querySelector('#app > main .notice.danger')})
                        """).WaitAsync(TimeSpan.FromSeconds(3));
                    Console.WriteLine("SOURCE STARTUP DOM: " + dom.GetRawText());
                }
                catch (Exception diagnosticError) { Console.WriteLine("SOURCE STARTUP DOM unavailable: " + diagnosticError.GetType().Name); }
                throw;
            }
        }

        await OpenApplication();
        await initialCardEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        var waitedForInitialCards = startupRuns == 0 && !operations.Contains("collect.startup");
        initialCardRelease.TrySetResult();
        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(15)); await delivery;
        var startupOperations = operations.ToList();
        Require(waitedForInitialCards && startupUiReady && startupRuns == 1 && startupOperations.IndexOf("app.ready") < startupOperations.IndexOf("collect.startup")
            && startupSettings is { } observedStartup && observedStartup.PortalUrl == settings.PortalUrl && observedStartup.ActiveSourceId == settings.ActiveSourceId
            && !startupOperations.Any(operation => operation is "collect.campaign" or "automation.recipe" or "library.capture" or "library.sync" or "library.loadRecords"),
            "Startup waits for the initial Home cards without blocking app.ready, then enters the real token-guarded C# handler once for the active saved search without a campaign or library collection.");
        await WaitForPageAsync(page, "document.querySelector('.status-text')?.textContent.includes('Collecte synthétique au démarrage')");
        Require(await page.Locator(".statusbar progress").IsVisibleAsync()
            && await page.Locator(".statusbar").GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).IsVisibleAsync()
            && await page.GetByLabel("Source des données", new() { Exact = true }).IsDisabledAsync()
            && await page.Locator(".page-heading button[aria-label=Actualiser]").IsDisabledAsync(),
            "Startup collection uses the normal visible progress and cancellation controls while preventing a second source refresh.");
        await page.Locator(".statusbar").GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await startupStopped.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Idle();
        await WaitForPageAsync(page, "document.querySelector('#app')?.dataset.busy!=='true'");
        restoreStartupFixture();
        Require(app.Result is null && await OnPage("home") && operations.Contains("operations.cancel")
            && await page.Locator("#notifications .toast.danger").CountAsync() == 0
            && !(await page.Locator("#notifications").InnerTextAsync()).Contains("terminée", StringComparison.Ordinal),
            "Cancelling startup through the real bridge stops its collection without a false completion, failure toast or page change.");
        await OpenApplication(reload: true); await Idle();
        var startupAfterReload = (await Call(app, "app.bootstrap")).GetProperty("startupCollection");
        Require(startupRuns == 1 && operations.Count(operation => operation == "collect.startup") == 1
            && startupAfterReload.GetProperty("attempted").GetBoolean() && !startupAfterReload.GetProperty("eligible").GetBoolean(),
            "Reloading and bootstrapping the same application after cancellation never re-arms startup collection.");
        await ClickNavigationAsync(page, "settings"); await WaitForNavigationTargetAsync(page, "settings");
        await page.Locator(".settings-workspace[data-ready=true]").WaitForAsync();
        await page.Locator("#cfg-search").FillAsync("Collecter au démarrage");
        await page.Locator("#cfg-search-list [role=option]").Filter(new() { HasText = "Collecter au démarrage" }).ClickAsync();
        await page.Locator("[data-open-panel=automation]").ClickAsync();
        var automation = page.GetByRole(AriaRole.Dialog, new() { Name = "Programmer les collectes", Exact = true });
        var startupChoice = automation.GetByRole(AriaRole.Checkbox, new() { Name = "Collecter au démarrage", Exact = true });
        await startupChoice.WaitForAsync();
        Require(await startupChoice.IsCheckedAsync() && app.Settings.Automation.CollectOnStartup
            && (await automation.InnerTextAsync()).Contains("recherche GLPI active avec ses filtres enregistrés", StringComparison.Ordinal),
            "Settings search reaches the enabled-by-default startup option and explains its active-search scope.");
        await startupChoice.UncheckAsync();
        await automation.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Idle();
        await CloseDialog(automation);
        await page.Locator("[data-open-panel=automation]").ClickAsync(); await startupChoice.WaitForAsync();
        Require(!await startupChoice.IsCheckedAsync() && !app.Settings.Automation.CollectOnStartup && !AppSettings.Load().Automation.CollectOnStartup
            && app.Settings.PortalUrl == settings.PortalUrl && startupRuns == 1,
            "Disabling startup collection persists through the real automation settings and reopening, without altering the search or launching another collection.");
        await CloseDialog(automation);
        await ClickNavigationAsync(page, "home"); await WaitForNavigationTargetAsync(page, "home"); await Idle();
        await Button("Collecter depuis GLPI").ClickAsync(); await WaitForNavigationTargetAsync(page, "data");
        await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        Require(await page.Locator(".workspace-live-source").GetByRole(AriaRole.Heading, new() { Name = "GLPI en direct", Exact = true }).IsVisibleAsync()
            && await Button("Lancer la collecte").EvaluateAsync<bool>("node=>node.classList.contains('primary')&&node.getBoundingClientRect().bottom<innerHeight-35")
            && await Button("Importer CSV / Excel").EvaluateAsync<bool>("node=>!node.classList.contains('primary')")
            && await Button("Se connecter").CountAsync() == 0
            && await page.GetByLabel("Colonnes avant la collecte", new() { Exact = true }).InputValueAsync() == "supervision"
            && await page.Locator("select[name=columnPreparationMode] option:checked").InnerTextAsync() == "Colonnes utiles à ma configuration"
            && await page.Locator(".workspace-live-source").EvaluateAsync<bool>("node=>!!(node.compareDocumentPosition(document.querySelector('[data-source-method=file]'))&Node.DOCUMENT_POSITION_FOLLOWING)&&node.getBoundingClientRect().top<document.querySelector('.workspace-data-metrics').getBoundingClientRect().top"),
            "GLPI is the first data workflow with useful configuration columns selected by default and a visible primary collection action; file import remains available as a secondary action.");
        await Capture("data-glpi-first-light");
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        Require(await Group().GetByText("Aucune recherche mémorisée", new() { Exact = true }).IsVisibleAsync()
            && !operations.Contains("collect.campaign") && await Group().GetByRole(AriaRole.Alert).CountAsync() == 0,
            "An empty saved-search list opens understandable preparation without attempting a campaign or displaying a backend error.");
        await Group().GetByRole(AriaRole.Button, new() { Name = "Ouvrir Recherches mémorisées", Exact = true }).ClickAsync();
        var sources = page.Locator("dialog[data-workspace-panel=sources]"); await sources.WaitForAsync();
        await sources.GetByLabel("Nom de la source", new() { Exact = true }).FillAsync("Support Marseille");
        await sources.GetByRole(AriaRole.Button, new() { Name = "Créer depuis la configuration actuelle", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('dialog[data-workspace-panel=sources]')?.textContent.includes('Support Marseille')");
        var firstId = app.Settings.Sources.Single().Id;
        Require(app.Settings.Sources.Single().Url == settings.PortalUrl && AppSettings.Load().Sources.Single().Id == firstId,
            "The guided saved-search creation uses the actual C# capture operation and preserves the full GLPI search URL on disk.");
        await sources.GetByLabel("Nom de la source", new() { Exact = true }).FillAsync("Support Aubagne");
        await sources.GetByRole(AriaRole.Button, new() { Name = "Créer depuis la configuration actuelle", Exact = true }).ClickAsync();
        await WaitForPageAsync(page, "document.querySelector('dialog[data-workspace-panel=sources]')?.textContent.includes('Support Aubagne')");
        var secondId = app.Settings.Sources.Single(source => source.Id != firstId).Id;
        await CloseDialog(sources);
        Require(await page.GetByLabel("Recherche mémorisée", new() { Exact = true }).InputValueAsync() == secondId,
            "Returning from saved-search management updates the connection selector without requiring an unrelated page reload.");
        await Button("Recherches mémorisées").ClickAsync(); await sources.WaitForAsync();
        await sources.GetByRole(AriaRole.Button, new() { Name = "Collecter plusieurs recherches", Exact = true }).ClickAsync(); await Group().WaitForAsync();
        await Group().Locator($"[data-grouped-source='{secondId}']").UncheckAsync();
        await Group().GetByRole(AriaRole.Button, new() { Name = "Enregistrer la sélection", Exact = true }).ClickAsync();
        await Group().GetByText("Sélection enregistrée.", new() { Exact = true }).WaitForAsync(); await CloseDialog(Group());
        await sources.GetByRole(AriaRole.Row).Filter(new() { HasText = "Support Aubagne" }).GetByText("Non sélectionnée", new() { Exact = true }).WaitForAsync();
        await sources.GetByRole(AriaRole.Row).Filter(new() { HasText = "Support Marseille" }).GetByRole(AriaRole.Button, new() { Name = "Utiliser", Exact = true }).ClickAsync();
        await sources.GetByRole(AriaRole.Row).Filter(new() { HasText = "Support Marseille" }).GetByText("Active", new() { Exact = true }).WaitForAsync();
        Require(app.Settings.ActiveSourceId == firstId && !app.Settings.Sources.Single(source => source.Id == secondId).IncludedInCampaign
            && await sources.GetByRole(AriaRole.Alert).CountAsync() == 0,
            "Saving a grouped choice from Recherches mémorisées refreshes its badges and action revision before another saved search is selected.");
        await CloseDialog(sources);
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        await Group().Locator($"[data-grouped-source='{firstId}']").UncheckAsync();
        await Group().Locator($"[data-grouped-source='{secondId}']").CheckAsync();
        var choiceRevision = app.Revision;
        await Call(app, "app.appearance", new { theme = "dark" });
        Require(app.Revision == choiceRevision, "The fixture changes an interface preference without changing the session revision.");
        await Group().GetByRole(AriaRole.Button, new() { Name = "Enregistrer la sélection", Exact = true }).ClickAsync();
        await Group().GetByText("Sélection enregistrée.", new() { Exact = true }).WaitForAsync();
        Require(app.Settings.Theme == "dark" && !app.Settings.Sources.Single(source => source.Id == firstId).IncludedInCampaign
            && app.Settings.Sources.Single(source => source.Id == secondId).IncludedInCampaign
            && AppSettings.Load().Theme == "dark" && !AppSettings.Load().Sources.Single(source => source.Id == firstId).IncludedInCampaign,
            "Atomic search-choice persistence changes only inclusion flags and preserves a concurrent theme preference, including in the real settings file.");
        await CloseDialog(Group());
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        Require(!await Group().Locator($"[data-grouped-source='{firstId}']").IsCheckedAsync()
            && await Group().Locator($"[data-grouped-source='{secondId}']").IsCheckedAsync(),
            "Reopening preparation restores the exact saved choice instead of implicitly collecting every search.");
        await Group().GetByRole(AriaRole.Button, new() { Name = "Tout désélectionner", Exact = true }).ClickAsync();
        Require(await Group().GetByRole(AriaRole.Button, new() { Name = "Collecter les recherches sélectionnées", Exact = true }).IsDisabledAsync()
            && !operations.Contains("collect.campaign"), "No selected search keeps grouped execution disabled without losing access to the choices.");
        await Group().Locator($"[data-grouped-source='{firstId}']").CheckAsync();
        var staleRevision = app.Revision;
        var changed = app.Settings.Sources.Single(source => source.Id == firstId).Copy(); changed.Name = "Support Marseille corrigé"; changed.Url = "https://glpi-fixture.invalid/front/ticket.php?criteria=changed";
        await Call(app, "sources.save", new { revision = app.Revision, source = changed });
        await Group().GetByRole(AriaRole.Button, new() { Name = "Collecter les recherches sélectionnées", Exact = true }).ClickAsync();
        await Group().GetByRole(AriaRole.Alert).WaitForAsync();
        Require(rejected.Contains("collect.selection") && !operations.Contains("collect.campaign")
            && app.Settings.Sources.Single(source => source.Id == firstId).Url == changed.Url
            && !app.Settings.Sources.Single(source => source.Id == firstId).IncludedInCampaign,
            "A renamed or changed search invalidates the open preparation before saving or collecting a silently changed perimeter.");
        try { await Call(app, "collect.campaign", new { revision = staleRevision }); throw new Exception("A stale campaign launch was accepted."); }
        catch (InvalidOperationException) { Require(true, "The real campaign operation also refuses an obsolete launch revision before any browser access."); }
        var protectedSettings = JsonSerializer.Serialize(app.Settings, PilotApplication.Json);
        try { await Call(app, "collect.selection", new { revision = app.Revision, ids = new[] { "missing-search" } }); throw new Exception("An unknown search was accepted."); }
        catch (InvalidDataException) { Require(JsonSerializer.Serialize(app.Settings, PilotApplication.Json) == protectedSettings, "An unknown search identity cannot partially deselect or overwrite the saved searches."); }
        await CloseDialog(Group());

        await Button("Importer CSV / Excel").ClickAsync(); await WaitForNavigationTargetAsync(page, "pilot-weather");
        Require(app.IsFileSource && !app.IsTestMode && app.Result!.Rows.Count == 2, "The secondary CSV action still imports a real file through the production C# importer.");
        await DataPage();
        Require((await page.Locator("[data-current-origin]").InnerTextAsync()).Contains("Source CSV / Excel", StringComparison.Ordinal)
            && await Button("Lancer la collecte").IsEnabledAsync(), "A loaded CSV keeps its explicit provenance while the live GLPI collection action remains available.");
        await Capture("data-csv-provenance-dark");
        await Call(app, "cache.restore"); File.Delete(csv);
        await OpenApplication(reload: true); await DataPage();
        Require(app.IsCachedSource && (await page.Locator("[data-current-origin]").InnerTextAsync()).Contains("Cache local", StringComparison.Ordinal),
            "An offline cached file is identified honestly with its original file absent, even though the GLPI form is presented first.");
        Require(await page.GetByRole(AriaRole.Combobox, new() { Name = "Source des données" }).Locator("option").CountAsync() == 4,
            "Live, cache, CSV/Excel and simulation remain accessible through the global source switch.");
        Require(await page.Locator(".page-heading button[title='Actualiser cet espace']").CountAsync() == 1,
            "The global refresh button retains its stable selector after a cache session change.");
        Require(await page.GetByRole(AriaRole.Combobox, new() { Name = "Source des données" }).InputValueAsync() == "cache",
            "The global source selector follows the restored offline context.");
        Require(await Button("Revenir au GLPI Live").IsVisibleAsync()
            && await Button("Configurer la collecte").IsVisibleAsync(),
            "The restored offline observation offers a prominent way back to GLPI without deleting the cached data.");
        var realSettingsBytes = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        await OpenDemonstrationFromSettingsAsync(page); await WaitForNavigationTargetAsync(page, "home"); await DataPage();
        Require(await Button("Collecter la simulation").IsVisibleAsync() && await page.Locator("[data-source-method=glpi]").CountAsync() == 0
            && await page.Locator(".local-test-banner .local-test-copy > strong").InnerTextAsync() == "Démonstration · données fictives",
            "The demo labels its main action as a simulation and never advertises a live GLPI connection.");
        Require(await OnPage("data") && await Button("Connecter la source simulée").CountAsync() == 0,
            "Data exposes one collection action without a redundant connection step, including in simulation.");
        await Button("Collecter la simulation").ClickAsync(); await Idle();
        Require(await OnPage("data") && app.IsTestMode && app.Result!.Rows.Count == 60 && operations.Contains("collect.run"),
            "The primary collection refreshes Data in place with the actual C# result instead of opening Pilotage.");

        var cleanCollectionCount = operations.Count(operation => operation == "collect.run");
        await Call(app, "collect.run"); await delivery; await Idle();
        var backgroundRevision = app.Revision;
        await Button("Collecter la simulation").ClickAsync(); await Idle();
        Require(app.Revision > backgroundRevision && operations.Count(operation => operation == "collect.run") == cleanCollectionCount + 1
            && await page.Locator(".workspace-live-source > .notice.danger[role=alert]").CountAsync() == 0,
            "A clean Data connection form follows a completed background observation revision and permits exactly one explicit collection without stale-form failure.");

        var sourceAddress = page.GetByLabel("Adresse de la source simulée", new() { Exact = true });
        var unchangedAddress = app.Settings.PortalUrl;
        const string pendingAddress = "https://simulation.glpi.local/front/ticket.php?draft=unapplied";
        await sourceAddress.FillAsync(pendingAddress);
        await Call(app, "collect.run"); await delivery; await Idle();
        var dirtyCollectionCount = operations.Count(operation => operation == "collect.run");
        await Button("Collecter la simulation").ClickAsync(); await Idle();
        Require(app.Settings.PortalUrl == unchangedAddress && await sourceAddress.InputValueAsync() == pendingAddress
            && operations.Count(operation => operation == "collect.run") == dirtyCollectionCount
            && (await page.Locator(".workspace-live-source > .notice.danger[role=alert]").InnerTextAsync()).Contains("Le relevé a changé", StringComparison.Ordinal),
            "A dirty Data connection form retains its original revision and values after background collection, refusing its obsolete save without replay or another collection.");
        await DataPage(); await Idle();

        // Explicit presentation fixtures use the production C# receipt and
        // camel-case bootstrap serialization; they never touch a GLPI view.
        var originalColumnReceipt = app.Result!.Provenance.ColumnPreparation;
        async Task ShowColumnReceipt(ColumnPreparationReceipt? receipt)
        {
            app.Result!.Provenance.ColumnPreparation = receipt;
            var bootstrap = await Call(app, "app.bootstrap");
            bridge!.Publish("session.changed", bootstrap.GetProperty("session")); await delivery;
            await DataPage(); await Idle();
        }
        var columnReceipt = new ColumnPreparationReceipt
        {
            Mode = "supervision", State = "prepared", AddedCount = 3, ColumnCount = 7, TemporaryView = true,
            Message = "Trois champs utiles ajoutés pour la collecte synthétique.",
            RestorationState = "restored", RestoredCount = 3, RestorationMessage = "Vue initiale relue après retrait des colonnes temporaires."
        };
        await ShowColumnReceipt(columnReceipt);
        var receiptCard = page.Locator("[data-column-preparation-receipt]");
        Require(await receiptCard.GetByText("Vue GLPI restaurée", new() { Exact = true }).IsVisibleAsync()
            && await receiptCard.Locator("[data-column-restoration=restored] .badge.success").CountAsync() == 1
            && await receiptCard.Locator("[data-restored-columns='3']").InnerTextAsync() == "3 colonnes temporaires retirées."
            && (await receiptCard.InnerTextAsync()).Contains(columnReceipt.RestorationMessage, StringComparison.Ordinal),
            "A confirmed C# restoration receipt displays its success badge, actual removal count and engine message.");
        columnReceipt.RestorationState = "recovery-pending"; columnReceipt.RecoveryPending = true; columnReceipt.RestoredCount = 1;
        columnReceipt.RestorationMessage = "Connexion interrompue avant la confirmation finale.";
        var confirmedQuality = new ExtractionQuality
        {
            SourceRows = 17, ExpectedRows = 17, AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = true, ScopeStable = true,
            InitialSample = new() { IdentityVerified = true, RepresentationVerified = true, SampleSize = 17, ComparedCells = 442 },
            FinalSample = new() { IdentityVerified = true, RepresentationVerified = true, SampleSize = 17, ComparedCells = 442 }
        };
        async Task<JsonElement> ReadReceiptStatus(bool complete) => await page.EvaluateAsync<JsonElement>(
            "async session => (await import('/js/ui.js')).extractionStatus(session)", JsonSerializer.SerializeToElement(
                new { rows = 17, expectedTotal = 17, complete, quality = confirmedQuality, provenance = new { columnPreparation = columnReceipt } }, PilotApplication.Json));
        var confirmedReceipt = await ReadReceiptStatus(true);
        Require(confirmedReceipt.GetProperty("label").GetString() == "Réception complète"
            && confirmedReceipt.GetProperty("scope").GetString() == "Périmètre stable"
            && confirmedReceipt.GetProperty("coverage").GetString() == "Couverture du relevé vérifiée"
            && confirmedReceipt.GetProperty("restorationPending").GetBoolean()
            && confirmedReceipt.GetProperty("restoration").GetString() == "Vue GLPI : restauration en attente",
            "A verified 17-row native receipt reports complete reception, stable scope and verified coverage independently from pending GLPI view restoration.");
        confirmedQuality.ScopeStable = false;
        var changedReceipt = await ReadReceiptStatus(false);
        Require(changedReceipt.GetProperty("label").GetString() == "Collecte à vérifier"
            && changedReceipt.GetProperty("scope").GetString() == "Périmètre à vérifier"
            && changedReceipt.GetProperty("tone").GetString() == "warning",
            "A complete row count never hides a changed perimeter even when a separate view-restoration warning is present.");
        await ShowColumnReceipt(columnReceipt);
        Require(await receiptCard.Locator("[data-column-restoration=pending].notice.warning[role=alert]").IsVisibleAsync()
            && (await receiptCard.InnerTextAsync()).Contains("elle doit être terminée avant la prochaine collecte", StringComparison.Ordinal)
            && (await receiptCard.InnerTextAsync()).Contains(columnReceipt.RestorationMessage, StringComparison.Ordinal)
            && await receiptCard.Locator(".badge.success").CountAsync() == 0
            && await receiptCard.Locator("[data-restored-columns='1']").InnerTextAsync() == "1 colonne temporaire retirée.",
            "Pending partial recovery remains an explicit warning before another collection without claiming the GLPI view is restored.");
        columnReceipt.RestorationState = "restored"; await ShowColumnReceipt(columnReceipt);
        Require(await receiptCard.Locator("[data-column-restoration=pending]").CountAsync() == 1
            && await receiptCard.Locator(".badge.success").CountAsync() == 0,
            "An outstanding recovery flag takes precedence over a contradictory restoration label.");
        await ShowColumnReceipt(new() { Mode = "supervision", State = "prepared", Message = "Ancien reçu de préparation sans preuve de restauration." });
        Require(await receiptCard.Locator("[data-column-restoration], .badge.success").CountAsync() == 0,
            "An older preparation receipt without restoration evidence does not acquire a fabricated success badge.");
        await ShowColumnReceipt(originalColumnReceipt);

        var previousDataResult = app.Result;
        failSingleCollection = true;
        await Button("Collecter la simulation").ClickAsync(); await Idle(); failSingleCollection = false;
        var collectionError = page.Locator(".workspace-live-source > .notice.danger[role=alert]");
        var previousErrorVisible = await collectionError.CountAsync() == 1
            && (await collectionError.InnerTextAsync()).Contains("Échec de collecte synthétique", StringComparison.Ordinal);
        await page.Locator(".workspace-source-alternatives").EvaluateAsync("""
            node=>{const alert=document.createElement('p');alert.className='notice danger';alert.setAttribute('role','alert');
              alert.dataset.retryScopeFixture='true';alert.textContent='Autre erreur à conserver';node.append(alert);}
            """);
        singleCollectionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        singleCollectionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        singleCollectionStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Button("Collecter la simulation").ClickAsync();
        await singleCollectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var retryClearedOnlyOldError = await collectionError.CountAsync() == 0
            && await page.Locator("[data-retry-scope-fixture]").CountAsync() == 1 && ReferenceEquals(previousDataResult, app.Result);
        singleCollectionRelease.TrySetException(new InvalidOperationException("Nouvel échec synthétique de la tentative de collecte."));
        await singleCollectionStopped.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Idle(); singleCollectionRelease = null;
        Require(previousErrorVisible && retryClearedOnlyOldError && ReferenceEquals(previousDataResult, app.Result)
            && await collectionError.CountAsync() == 1
            && (await collectionError.InnerTextAsync()).Contains("Nouvel échec synthétique", StringComparison.Ordinal)
            && await page.Locator("[data-retry-scope-fixture]").CountAsync() == 1,
            "Retrying Data collection clears only its previous card error while pending, keeps the last data, and displays a new failure normally.");

        await ClickNavigationAsync(page, "home"); await WaitForNavigationTargetAsync(page, "home"); await Idle();
        foreach (var entry in new[] { "source-selector", "global-refresh", "command-palette" })
        {
            var before = operations.Count(op => op == "collect.run"); var beforeRevision = app.Revision;
            await page.Locator(".page-content").EvaluateAsync("node=>node.dataset.beforeCollection='true'");
            if (entry == "source-selector") await page.GetByLabel("Source des données", new() { Exact = true }).SelectOptionAsync("live");
            else if (entry == "global-refresh")
            {
                await ShowLiveSourceControl();
                await page.Locator(".page-heading button[aria-label=Actualiser]").ClickAsync();
            }
            else
            {
                await Button("Rechercher un écran, une action ou un ticket").ClickAsync();
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync("Simuler une collecte");
                await page.Locator("[data-command-id='action:collect']").ClickAsync();
            }
            await Idle();
            Require(await OnPage("home") && operations.Count(op => op == "collect.run") == before + 1
                && app.Revision > beforeRevision && await page.Locator("[data-before-collection]").CountAsync() == 0
                && (await page.Locator(".home-source-line").InnerTextAsync()).Contains("60 lignes", StringComparison.Ordinal),
                "A successful collection from Home updates its actual data and stays on Home via " + entry + ".");
        }

        await ShowLiveSourceControl(); failSingleCollection = true;
        await page.Locator(".page-content").EvaluateAsync("node=>node.dataset.beforeCollection='true'");
        var unchangedResult = app.Result; var failedRevision = app.Revision;
        await page.Locator(".page-heading button[aria-label=Actualiser]").ClickAsync(); await Idle(); failSingleCollection = false;
        Require(await OnPage("home") && ReferenceEquals(unchangedResult, app.Result) && app.Revision == failedRevision
            && await page.Locator("[data-before-collection]").CountAsync() == 1
            && (await page.Locator("#notifications").InnerTextAsync()).Contains("Échec de collecte synthétique", StringComparison.Ordinal),
            "A failed collection keeps Home and its previous data visible and reports the failure.");
        await page.Locator("#notifications .toast.danger").GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();

        singleCollectionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        singleCollectionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        singleCollectionStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.Locator(".page-heading button[aria-label=Actualiser]").ClickAsync();
        await singleCollectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await page.Locator(".statusbar").GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await singleCollectionStopped.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Idle();
        singleCollectionRelease = null;
        Require(await OnPage("home") && ReferenceEquals(unchangedResult, app.Result)
            && await page.Locator("[data-before-collection]").CountAsync() == 1
            && await page.Locator("#notifications .toast.danger").CountAsync() == 0,
            "Cancelling collection through the real bridge keeps Home and its data without a spurious failure or navigation.");

        await ShowLiveSourceControl();
        singleCollectionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        singleCollectionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        singleCollectionStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.Locator(".page-heading button[aria-label=Actualiser]").ClickAsync();
        await singleCollectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // The collection is deliberately pending: await the destination DOM,
        // not the usual navigation helper's all-requests-idle condition.
        await ClickNavigationAsync(page, "data");
        await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        await WaitForPageAsync(page, "document.querySelector('.page-content')?.dataset.page==='data'&&!document.querySelector('#content').hasAttribute('aria-busy')");
        const string draftUrl = "https://simulation.glpi.local/front/ticket.php?draft=retained";
        await page.Locator(".workspace-live-source input[name=portalUrl]").FillAsync(draftUrl);
        await page.Locator(".page-content").EvaluateAsync("node=>node.dataset.navigatedDuringCollection='true'");
        singleCollectionRelease.TrySetResult(); await Idle(); singleCollectionRelease = null;
        Require(!ReferenceEquals(unchangedResult, app.Result) && app.Revision > failedRevision
            && await OnPage("data") && await page.Locator("[data-navigated-during-collection]").CountAsync() == 1
            && await page.Locator(".workspace-live-source input[name=portalUrl]").InputValueAsync() == draftUrl,
            "A late collection completion preserves the page and unsaved form chosen while collection was running.");
        // Discard the synthetic unsaved draft by an explicit navigation.
        await DataPage(); await Idle();
        await Button("Lire le pilotage").ClickAsync(); await WaitForNavigationTargetAsync(page, "pilot"); await Idle();
        var composition = await page.GetByLabel("Composition du pilotage", new() { Exact = true }).InputValueAsync();
        await page.GetByLabel("Source des données", new() { Exact = true }).SelectOptionAsync("live"); await Idle();
        Require(await OnPage("pilot") && await page.GetByLabel("Composition du pilotage", new() { Exact = true }).InputValueAsync() == composition,
            "Pilotage remains available by explicit navigation and collecting there preserves its selected composition.");
        var demoSettings = app.Settings.Copy();
        const string demoA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", demoB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        demoSettings.Sources = [new SourceProfile { Id = demoA, Name = "Simulation A", Url = "https://simulation.glpi.local/front/ticket.php?a=1", IncludedInCampaign = false },
            new SourceProfile { Id = demoB, Name = "Simulation B", Url = "https://simulation.glpi.local/front/ticket.php?b=2", IncludedInCampaign = false }];
        demoSettings.PortalUrl = demoSettings.Sources[0].Url; demoSettings.ActiveSourceId = demoA;
        await Call(app, "settings.save", new { revision = app.Revision, settings = demoSettings });
        var staleDemoRevision = app.Revision; var updatedDemoSearch = app.Settings.Sources[0].Copy(); updatedDemoSearch.Mode = "pagination";
        await Call(app, "sources.save", new { revision = app.Revision, source = updatedDemoSearch });
        try { await Call(app, "collect.campaign", new { revision = staleDemoRevision }); throw new Exception("The simulated adapter accepted an obsolete selection."); }
        catch (InvalidOperationException) { Require(true, "The simulated grouped adapter enforces the same stale-revision guard as live collection."); }
        try { await Call(app, "collect.campaign", new { revision = app.Revision }); throw new Exception("The simulated adapter ignored an empty selection."); }
        catch (InvalidDataException) { Require(true, "The simulated grouped adapter also refuses an empty selection instead of inventing a single local search."); }
        await DataPage(); var dataBeforeCampaign = await page.Locator(".data-workspace").ElementHandleAsync();
        await Button("Recherches mémorisées").ClickAsync(); await sources.WaitForAsync();
        await sources.GetByRole(AriaRole.Button, new() { Name = "Collecter plusieurs recherches", Exact = true }).ClickAsync(); await Group().WaitForAsync();
        await Group().GetByRole(AriaRole.Button, new() { Name = "Tout sélectionner", Exact = true }).ClickAsync();
        await Capture("grouped-searches-preparation");
        await Group().GetByRole(AriaRole.Button, new() { Name = "Collecter les recherches simulées", Exact = true }).ClickAsync();
        var summary = page.GetByRole(AriaRole.Dialog, new() { Name = "Bilan de la collecte groupée", Exact = true }); await summary.WaitForAsync();
        var actual = campaigns.Single(); var received = actual.GetProperty("sources").EnumerateArray().ToArray();
        Require(received.Select(source => source.GetProperty("id").GetString()).SequenceEqual(new[] { demoA, demoB }) && received.All(source => source.GetProperty("rows").GetInt32() == 60)
            && received.All(source => source.GetProperty("state").GetString() is "Terminé" or "À vérifier") && app.Result!.Rows.Count == 60,
            "A genuine C# grouped demo collection processes both selected searches as separate 60-row results without fabricating a 120-row merged dataset.");
        var reportPath = actual.GetProperty("path").GetString()!;
        using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
            Require(report.RootElement.GetProperty("Sources").GetArrayLength() == 2 && File.Exists(Path.ChangeExtension(reportPath, ".csv"))
                && Path.GetDirectoryName(reportPath) == app.Settings.ExportFolder && !reportPath.StartsWith(_root, StringComparison.Ordinal),
                "The actual grouped collection writes its JSON and CSV reports inside the isolated simulation export folder.");
        var realSettingsAfterDemo = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        Require(realSettingsBytes.SequenceEqual(realSettingsAfterDemo), "The grouped demo choice and its execution leave the real profile settings byte-for-byte unchanged.");
        await Capture("grouped-searches-complete");
        await CloseDialog(summary); await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        Require(!await dataBeforeCampaign!.EvaluateAsync<bool>("node=>node.isConnected") && await sources.CountAsync() == 0
            && (await page.Locator("[data-current-origin]").InnerTextAsync()).Contains("60 lignes", StringComparison.Ordinal),
            "Closing a completed group launched from Recherches mémorisées closes that panel cleanly and refreshes the underlying data provenance and counts.");

        returnFailedCampaign = true;
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        await Group().GetByRole(AriaRole.Button, new() { Name = "Collecter les recherches simulées", Exact = true }).ClickAsync(); await summary.WaitForAsync();
        Require((await summary.InnerTextAsync()).Contains("Aucun nouveau relevé reçu.", StringComparison.Ordinal)
            && (await summary.InnerTextAsync()).Contains("2 recherche(s) en échec.", StringComparison.Ordinal),
            "The explicit all-failed response fixture reports zero new observations and two failures instead of claiming a successful refresh.");
        returnFailedCampaign = false; await CloseDialog(summary); await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        waitForCancellation = true;
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        var beforeCancellationRevision = app.Revision;
        await Group().GetByRole(AriaRole.Button, new() { Name = "Collecter les recherches simulées", Exact = true }).ClickAsync();
        await cancellationPrepared.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Group().GetByRole(AriaRole.Button, new() { Name = "Annuler la collecte", Exact = true }).WaitForAsync();
        await Group().GetByRole(AriaRole.Button, new() { Name = "Annuler la collecte", Exact = true }).ClickAsync();
        var stopped = page.GetByRole(AriaRole.Dialog, new() { Name = "Collecte groupée arrêtée", Exact = true }); await stopped.WaitForAsync();
        Require(cancellationRevision > beforeCancellationRevision && await stopped.GetByRole(AriaRole.Alert).CountAsync() == 0
            && await stopped.GetByRole(AriaRole.Button, new() { Name = "Collecter les recherches simulées", Exact = true }).CountAsync() == 0
            && !await stopped.Locator(":scope > .modal-heading button").IsDisabledAsync(),
            "Cancellation after actual simulated work propagates through the WebBridge token and requires fresh preparation instead of reusing an obsolete revision.");
        waitForCancellation = false; await stopped.GetByRole(AriaRole.Button, new() { Name = "Fermer et actualiser", Exact = true }).ClickAsync(); await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        await Group().GetByRole(AriaRole.Button, new() { Name = "Collecter les recherches simulées", Exact = true }).ClickAsync(); await summary.WaitForAsync();
        Require(campaigns.Count == 2 && await summary.GetByRole(AriaRole.Alert).CountAsync() == 0,
            "Reopening preparation after cancellation launches the genuine two-search simulation successfully with its current revision.");
        await CloseDialog(summary); await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        await Call(app, "records.test.exit"); await OpenApplication(reload: true); await DataPage();
        var realSettingsAfterExit = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        Require(app.IsCachedSource && app.Result!.Rows.Count == 2 && realSettingsBytes.SequenceEqual(realSettingsAfterExit)
            && app.Settings.Sources.Any(source => source.Id == firstId) && app.Settings.Sources.All(source => source.Id is not (demoA or demoB)),
            "Leaving the grouped demonstration restores the original cached CSV and real saved-search choices.");
        await File.WriteAllTextAsync(csv, "ID;Titre;Statut\n31103;Extrait de contrôle;Nouveau\n");
        await Call(app, "records.test.import"); await OpenApplication(reload: true); await DataPage();
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        Require(await page.GetByRole(AriaRole.Heading, new() { Name = "Fichier en simulation", Exact = true }).CountAsync() == 1
            && (await Group().InnerTextAsync()).Contains("Simulation locale : ces collectes réutilisent les données du fichier chargé.", StringComparison.Ordinal)
            && !(await Group().InnerTextAsync()).Contains("données fictives", StringComparison.Ordinal)
            && await page.Locator(".local-test-banner .local-test-copy > strong").InnerTextAsync() == "Simulation · fichier local"
            && !(await page.Locator(".local-test-banner").InnerTextAsync()).Contains("fictives", StringComparison.Ordinal),
            "A simulation imported from a local file is described as that file, without relabelling its real contents as fictitious demo data.");
        await CloseDialog(Group()); await Call(app, "records.test.exit");
        Require(errors.IsEmpty && externalRequests == 0 && blockedLiveCalls == 0,
            "The complete source workflow has no JavaScript error, external request or attempted real-portal collection in the isolated test.");
        await actualBridge.DisposeAsync(); await delivery;
    }

    private static async Task CheckStartupTicketNavigationInterfaceAsync(IBrowser browser, string web, AppSettings settings)
    {
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
        var observed = new ExtractionResult
        {
            Headers = ["ID", "Titre", "Statut"], Rows = [new[] { "41101", "Ticket conservé pendant la collecte", "Nouveau" }],
            SourceUrl = settings.PortalUrl, Complete = true,
            Provenance = new() { SourceKind = "glpi", RequestedUrl = settings.PortalUrl, SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "startup-navigation-fixture" }
        };
        app.LoadStartupTicketSourceFixture(observed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupRuns = 0; var completedReads = 0;
        app.ConfigureStartupCollectionFixture(async (_, ct) =>
        {
            Interlocked.Increment(ref startupRuns); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return null; }
            finally { restoring.TrySetResult(); await restoreRelease.Task; }
        });
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            operations.Enqueue(op);
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            if (op is "collect.run" or "collect.connect" or "collect.campaign") throw new InvalidOperationException("Unexpected portal operation in startup navigation fixture.");
            var result = await app.ExecuteAsync(op, data, ct);
            if (op == "records.list") Interlocked.Increment(ref completedReads);
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
        try
        {
            await page.GotoAsync("https://glpi-pilot.local/index.html", new() { WaitUntil = WaitUntilState.Commit });
            await ReadyAsync(page); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); await delivery;
            Require(startupRuns == 1 && app.IsBusy && app.CurrentOperation == "collect.startup" && ReferenceEquals(observed, app.Result),
                "Automatic startup starts with the preceding GLPI observation intact and the Home interface ready.");
            // Do not use the helper that waits for the destination to finish:
            // restoration is deliberately held until the gate assertion below.
            await page.Locator(".nav-item[data-page='records']").ClickAsync();
            await restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(operations.Contains("collect.startup.interrupt") && !operations.Contains("operations.cancel")
                && app.IsBusy && app.CurrentOperation == "collect.startup" && completedReads == 0 && ReferenceEquals(observed, app.Result),
                "Opening Rechercher sends the startup-only interruption before workspace reads and retains the execution gate until restoration finishes.");
        }
        finally { restoreRelease.TrySetResult(); }
        await WaitForNavigationTargetAsync(page, "records");
        await WaitForPageAsync(page, "() => !!document.querySelector('.tickets-record-grid tbody tr')&&!document.querySelector('#content').hasAttribute('aria-busy')");
        var rows = await page.Locator(".tickets-record-grid").InnerTextAsync();
        var cleanInterruption = await page.Locator("#notifications .toast.danger").CountAsync() == 0
            && !(await page.Locator("#notifications").InnerTextAsync()).Contains("terminée", StringComparison.Ordinal);
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Commit }); await ReadyAsync(page);
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending"); await delivery;
        Require(rows.Contains("Ticket conservé pendant la collecte", StringComparison.Ordinal) && completedReads > 0
            && startupRuns == 1 && operations.Count(operation => operation == "collect.startup") == 1
            && cleanInterruption && ReferenceEquals(observed, app.Result) && !app.Browser.IsOpen && externalRequests == 0 && errors.IsEmpty
            && await page.Locator("#notifications .toast.danger").CountAsync() == 0,
            "Ticket navigation displays the conserved observation after cleanup, without a portal read, error toast or automatic restart after interface reload.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void LoadStartupTicketSourceFixture(ExtractionResult data) => SetResult(data);
        public Action ConfigureStartupCollectionFixture(Func<JsonElement, CancellationToken, Task<object?>> collect)
        {
            var previous = _handlers["collect.run"]; var browserAvailable = _startupBrowserAvailable; var networkAvailable = _networkAvailable;
            _handlers["collect.run"] = collect; _startupBrowserAvailable = _ => true; _networkAvailable = () => true;
            return () => { _handlers["collect.run"] = previous; _startupBrowserAvailable = browserAvailable; _networkAvailable = networkAvailable; };
        }
    }
}
