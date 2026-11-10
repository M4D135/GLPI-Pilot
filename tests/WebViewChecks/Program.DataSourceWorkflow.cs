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
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var operations = new ConcurrentQueue<string>(); var rejected = new ConcurrentQueue<string>();
        var campaigns = new ConcurrentQueue<JsonElement>(); var externalRequests = 0; var blockedLiveCalls = 0;
        var waitForCancellation = false; var returnFailedCampaign = false;
        var failSingleCollection = false;
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

        await page.GotoAsync("https://glpi-pilot.local/index.html"); await ReadyAsync(page);
        await Button("Collecter depuis GLPI").ClickAsync(); await WaitForNavigationTargetAsync(page, "data");
        await page.Locator(".data-workspace[data-ready=true]").WaitForAsync();
        Require(await page.Locator(".workspace-live-source").GetByRole(AriaRole.Heading, new() { Name = "GLPI en direct", Exact = true }).IsVisibleAsync()
            && await Button("Lancer la collecte").EvaluateAsync<bool>("node=>node.classList.contains('primary')&&node.getBoundingClientRect().bottom<innerHeight-35")
            && await Button("Importer CSV / Excel").EvaluateAsync<bool>("node=>!node.classList.contains('primary')")
            && await page.Locator(".workspace-live-source").EvaluateAsync<bool>("node=>!!(node.compareDocumentPosition(document.querySelector('[data-source-method=file]'))&Node.DOCUMENT_POSITION_FOLLOWING)&&node.getBoundingClientRect().top<document.querySelector('.workspace-data-metrics').getBoundingClientRect().top"),
            "GLPI is the first data workflow with a visible primary collection action; file import remains available as a secondary action.");
        await Capture("data-glpi-first-light");
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        Require(await Group().GetByText("Aucune recherche mémorisée", new() { Exact = true }).IsVisibleAsync()
            && !operations.Contains("collect.campaign") && await Group().GetByRole(AriaRole.Alert).CountAsync() == 0,
            "An empty saved-search list opens understandable preparation without attempting a campaign or displaying a backend error.");
        await Group().GetByRole(AriaRole.Button, new() { Name = "Ouvrir Mes recherches", Exact = true }).ClickAsync();
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
        await Button("Mes recherches").ClickAsync(); await sources.WaitForAsync();
        await sources.GetByRole(AriaRole.Button, new() { Name = "Collecter plusieurs recherches", Exact = true }).ClickAsync(); await Group().WaitForAsync();
        await Group().Locator($"[data-grouped-source='{secondId}']").UncheckAsync();
        await Group().GetByRole(AriaRole.Button, new() { Name = "Enregistrer la sélection", Exact = true }).ClickAsync();
        await Group().GetByText("Sélection enregistrée.", new() { Exact = true }).WaitForAsync(); await CloseDialog(Group());
        await sources.GetByRole(AriaRole.Row).Filter(new() { HasText = "Support Aubagne" }).GetByText("Non sélectionnée", new() { Exact = true }).WaitForAsync();
        await sources.GetByRole(AriaRole.Row).Filter(new() { HasText = "Support Marseille" }).GetByRole(AriaRole.Button, new() { Name = "Utiliser", Exact = true }).ClickAsync();
        await sources.GetByRole(AriaRole.Row).Filter(new() { HasText = "Support Marseille" }).GetByText("Active", new() { Exact = true }).WaitForAsync();
        Require(app.Settings.ActiveSourceId == firstId && !app.Settings.Sources.Single(source => source.Id == secondId).IncludedInCampaign
            && await sources.GetByRole(AriaRole.Alert).CountAsync() == 0,
            "Saving a grouped choice from Mes recherches refreshes its badges and action revision before another saved search is selected.");
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
        await page.ReloadAsync(); await ReadyAsync(page); await DataPage();
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
        Require(await Button("Collecter la simulation").IsVisibleAsync() && await page.Locator("[data-source-method=glpi]").CountAsync() == 0,
            "The demo labels its main action as a simulation and never advertises a live GLPI connection.");
        await Button("Connecter la source simulée").ClickAsync(); await Idle();
        Require(await OnPage("data") && operations.Contains("collect.connect"),
            "Connecting the source leaves the Data workspace open.");
        await Button("Collecter la simulation").ClickAsync(); await Idle();
        Require(await OnPage("data") && app.IsTestMode && app.Result!.Rows.Count == 60 && operations.Contains("collect.run"),
            "The primary collection refreshes Data in place with the actual C# result instead of opening Pilotage.");

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
        await page.GetByLabel("Adresse de la recherche GLPI", new() { Exact = true }).FillAsync(draftUrl);
        await page.Locator(".page-content").EvaluateAsync("node=>node.dataset.navigatedDuringCollection='true'");
        singleCollectionRelease.TrySetResult(); await Idle(); singleCollectionRelease = null;
        Require(await OnPage("data") && await page.Locator("[data-navigated-during-collection]").CountAsync() == 1
            && await page.GetByLabel("Adresse de la recherche GLPI", new() { Exact = true }).InputValueAsync() == draftUrl,
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
        await Button("Mes recherches").ClickAsync(); await sources.WaitForAsync();
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
            "Closing a completed group launched from Mes recherches closes that panel cleanly and refreshes the underlying data provenance and counts.");

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
        await Call(app, "records.test.exit"); await page.ReloadAsync(); await ReadyAsync(page); await DataPage();
        var realSettingsAfterExit = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        Require(app.IsCachedSource && app.Result!.Rows.Count == 2 && realSettingsBytes.SequenceEqual(realSettingsAfterExit)
            && app.Settings.Sources.Any(source => source.Id == firstId) && app.Settings.Sources.All(source => source.Id is not (demoA or demoB)),
            "Leaving the grouped demonstration restores the original cached CSV and real saved-search choices.");
        await File.WriteAllTextAsync(csv, "ID;Titre;Statut\n31103;Extrait de contrôle;Nouveau\n");
        await Call(app, "records.test.import"); await page.ReloadAsync(); await ReadyAsync(page); await DataPage();
        await Button("Collecter plusieurs recherches").ClickAsync(); await Group().WaitForAsync();
        Require(await page.GetByRole(AriaRole.Heading, new() { Name = "Votre fichier, en simulation", Exact = true }).CountAsync() == 1
            && (await Group().InnerTextAsync()).Contains("Simulation locale : ces collectes réutilisent les données du fichier chargé.", StringComparison.Ordinal)
            && !(await Group().InnerTextAsync()).Contains("données fictives", StringComparison.Ordinal),
            "A simulation imported from a local file is described as that file, without relabelling its real contents as fictitious demo data.");
        await CloseDialog(Group()); await Call(app, "records.test.exit");
        Require(errors.IsEmpty && externalRequests == 0 && blockedLiveCalls == 0,
            "The complete source workflow has no JavaScript error, external request or attempted real-portal collection in the isolated test.");
        await actualBridge.DisposeAsync(); await delivery;
    }
}
