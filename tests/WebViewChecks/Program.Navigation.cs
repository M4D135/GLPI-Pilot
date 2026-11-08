using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static readonly string[] WorkspaceRoutes = ["home", "records", "interventions", "pilot", "data", "settings"];
    private static readonly string[] OptionalNavigationRoutes = ["assistant", "enrichment", "populations"];
    private static Task DismissCaptureNotificationsAsync(IPage page) => page.Locator("#notifications .toast").EvaluateAllAsync<int>("""
        notices => {
          // Capture cleanup uses the observed nodes, so an expiring toast cannot retarget a locator.
          // notify() removes a toast synchronously through its real close handler.
          for (const notice of notices) {
            if (!notice.isConnected) continue;
            const close = [...notice.querySelectorAll('button')].find(button =>
              (button.getAttribute('aria-label') || button.textContent || '').trim() === 'Fermer');
            if (!close || close.disabled) throw new Error('Capture cleanup: observed notification has no enabled Fermer button.');
            close.click();
            if (notice.isConnected) throw new Error('Capture cleanup: observed notification remained after Fermer.');
          }
          return notices.length;
        }
        """);
    private static async Task<string[]> NavigationRouteIdsAsync(IPage page)
    {
        if (!await page.Locator(".sidebar").IsVisibleAsync())
            await page.GetByRole(AriaRole.Button, new() { Name = "Navigation", Exact = true }).ClickAsync();
        await page.Locator(".sidebar .nav-customize").ClickAsync();
        var editor = page.Locator("dialog.navigation-modal"); await editor.WaitForAsync();
        var routes = await editor.Locator("[data-nav-page]").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.navPage)");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached }); return routes;
    }
    private static Task<JsonElement> NavigationTargetAsync(IPage page, string route) => page.EvaluateAsync<JsonElement>(
        "async route => (await import('/js/navigation.js')).navigationTarget(route)", route);
    private static async Task WaitForNavigationTargetAsync(IPage page, string route)
    {
        var target = await NavigationTargetAsync(page, route); var id = target.GetProperty("id").GetString()!;
        await WaitForPageAsync(page, "id=>document.querySelector('.page-content')?.dataset.page===id&&!document.querySelector('#content').hasAttribute('aria-busy')", id);
        if (target.GetProperty("params").TryGetProperty("panel", out var panelValue))
        {
            var panel = panelValue.GetString()!;
            if (id != "home" && panel != "compositions" && !(id == "interventions" && target.GetProperty("params").TryGetProperty("tab", out var tab) && tab.GetString() == "tours"))
                await page.Locator($"dialog[open][data-workspace-panel='{panel}']").WaitForAsync();
        }
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
    }
    private static async Task ClickNavigationAsync(IPage page, string route)
    {
        var panels = page.Locator("dialog[open][data-workspace-panel]");
        while (await panels.CountAsync() > 0)
        {
            var panel = panels.Last;
            await panel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).First.ClickAsync();
            await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
        var target = await NavigationTargetAsync(page, route); var id = target.GetProperty("id").GetString()!;
        var entry = page.Locator($".nav-item[data-page='{id}']");
        if (await entry.CountAsync() > 0)
        {
            if (!await page.Locator(".sidebar").IsVisibleAsync())
                await page.GetByRole(AriaRole.Button, new() { Name = "Navigation", Exact = true }).ClickAsync();
            await entry.ClickAsync();
        }
        else
        {
            await page.Keyboard.PressAsync("Control+k");
            var palette = page.Locator("dialog.command-palette");
            var label = await page.EvaluateAsync<string>("async id=>(await import('/js/navigation.js')).navigationSpaces.find(space=>space.id===id).label", id);
            await palette.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync(label);
            await palette.Locator($"[data-command-id='page:{id}']").ClickAsync();
        }
        await WaitForPageAsync(page, "id=>document.querySelector('.page-content')?.dataset.page===id&&!document.querySelector('#content').hasAttribute('aria-busy')", id);
        var parameters = target.GetProperty("params");
        if (id == "interventions" && parameters.TryGetProperty("tab", out var tabValue))
        {
            var tab = tabValue.GetString()!;
            var control = page.Locator($"[data-calendar-tab='{tab}']");
            if (await control.GetAttributeAsync("aria-current") != "page") await control.ClickAsync();
            await WaitForPageAsync(page,"async()=>!(await import('/js/bridge.js')).api.pending");
        }
        if (id == "interventions" && parameters.TryGetProperty("ticketMap", out var sourceMap) && sourceMap.GetBoolean()
            && await page.Locator(".terrain-map-page").CountAsync() == 0)
        {
            await page.Locator("[data-calendar-action=ticket-map]").ClickAsync();
            await page.Locator(".terrain-map-page[data-ready=true]").WaitForAsync();
        }
        if (parameters.TryGetProperty("panel", out var panelValue))
        {
            var panel = panelValue.GetString()!;
            if (panel != "compositions" && !(id == "interventions" && parameters.TryGetProperty("tab", out var panelTab) && panelTab.GetString() == "tours"))
            {
                var control = page.Locator($".page-content [data-open-panel='{panel}']").First;
                if (await control.CountAsync() == 0) throw new InvalidOperationException("Missing contextual action for " + route + " in " + id + ": " + panel);
                var details = control.Locator("xpath=ancestor::details");
                for (var index = 0; index < await details.CountAsync(); index++)
                    if (await details.Nth(index).GetAttributeAsync("open") is null) await details.Nth(index).Locator(":scope > summary").ClickAsync();
                await control.ClickAsync();
            }
        }
        else if (parameters.TryGetProperty("focusSection", out var focus))
        {
            var block = page.Locator($".pilot-block[data-section-key='{focus.GetString()}']");
            if (await block.CountAsync() > 0)
            {
                var title = await block.Locator(":scope > header h2").InnerTextAsync();
                await page.Locator(".pilot-section-index").GetByRole(AriaRole.Button, new() { Name = title, Exact = true }).ClickAsync();
            }
        }
    }

    // The demonstration is opened only from its Réglages card, then confirmed in its modal.
    private static async Task OpenDemonstrationFromSettingsAsync(IPage page)
    {
        await ClickNavigationAsync(page, "settings");
        await WaitForNavigationTargetAsync(page, "settings");
        var card = page.Locator("[data-settings-section=demo]");
        await card.WaitForAsync();
        await card.GetByRole(AriaRole.Button, new() { Name = "Ouvrir la démonstration", Exact = true }).ClickAsync();
        var confirmation = page.GetByRole(AriaRole.Dialog, new() { Name = "Ouvrir la démonstration", Exact = true });
        await confirmation.WaitForAsync();
        await confirmation.GetByRole(AriaRole.Button, new() { Name = "Ouvrir la démonstration", Exact = true }).ClickAsync();
    }

    private static async Task CheckNavigationInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1600, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.NoPreference });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var externalRequests = 0; PilotApplication? mapApplication = null;
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        await context.RouteAsync("**/*", async route =>
        {
            if (EmbeddedMapsService.IsTileUrl(route.Request.Url) && mapApplication?.AllowsMapTileRequest(route.Request.Url) == true)
            {
                await route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=") }); return;
            }
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { Interlocked.Increment(ref externalRequests); await route.AbortAsync(); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); if (relative.Length == 0) relative = "index.html";
            var path = Path.GetFullPath(Path.Combine(web, relative));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.Accent = "violet"; settings.Interface.FrozenColumns = 3; settings.Interface.FollowWindowsMotion = false;
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings); mapApplication = app;
        // Delay delivery of one genuine reply after SQLite has returned; the rest of the bridge remains live.
        var startupClock = System.Diagnostics.Stopwatch.StartNew();
        var startupTrace = new ConcurrentQueue<string>();
        void TraceStartup(string phase) => startupTrace.Enqueue($"{startupClock.ElapsedMilliseconds} ms · {phase}");
        var calendarSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliverCalendar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op == "app.ready") { TraceStartup("app.ready received"); return new { ready = true }; }
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            var trace = (op is "app.bootstrap" or "calendar.summary") && !deliverCalendar.Task.IsCompleted;
            if (trace) TraceStartup($"{op} received");
            try
            {
                var result = await app.ExecuteAsync(op, data, ct);
                if (trace) TraceStartup($"{op} returned");
                if (op == "calendar.summary" && calendarSeen.TrySetResult()) await deliverCalendar.Task;
                return result;
            }
            catch (Exception error) when (trace)
            {
                TraceStartup($"{op} failed: {error.GetType().Name}");
                if (op == "calendar.summary") calendarSeen.TrySetException(error);
                throw;
            }
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (()=>{const listeners=new Set();window.chrome||={};window.chrome.webview={
              postMessage:message=>{if(message.op==="calendar.summary" && !window.__firstHomeCalendar)window.__firstHomeCalendar=message.id;return window.__pilotSend(JSON.stringify(message));},
              addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},
              removeEventListener:(name,handler)=>listeners.delete(handler)
            };window.__pilotDeliver=message=>{for(const listener of listeners)listener({data:message});if(message.id && message.id===window.__firstHomeCalendar)window.__homeCalendarDelivered=message.ok===true;};})();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator Editor() => page.Locator("dialog.navigation-modal");
        async Task OpenEditor()
        {
            if (!await page.Locator(".sidebar").IsVisibleAsync()) await Button("Navigation").ClickAsync();
            await page.Locator(".sidebar .nav-customize").ClickAsync(); await Editor().WaitForAsync();
        }
        async Task SaveEditor() { await Editor().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le menu", Exact = true }).ClickAsync(); await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached }); }
        async Task Navigate(string route) { await ClickNavigationAsync(page, route); await WaitForNavigationTargetAsync(page, route); }
        async Task Capture(string name)
        {
            var argument = Array.IndexOf(args, "--screenshots"); if (argument < 0 || argument + 1 >= args.Length) return;
            Directory.CreateDirectory(args[argument + 1]);
            await WaitForPageAsync(page, "!document.getAnimations().some(animation=>animation.playState==='running'&&animation.effect?.getComputedTiming().endTime<=2000)");
            await DismissCaptureNotificationsAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png"), Animations = ScreenshotAnimations.Allow });
        }
        async Task Apply(InterfacePreferences preferences) { await Call(app, "app.appearance", new { preferences }); await delivery; }
        await page.GotoAsync("https://glpi-pilot.local/index.html");
        try
        {
            TraceStartup("document loaded");
            await ReadyAsync(page);
            TraceStartup("home ready");
            var calendarMetric = page.Locator("[data-metric-kind=calendar]");
            Require(await calendarMetric.IsDisabledAsync() && await calendarMetric.GetAttributeAsync("data-metric-known") == "false"
                && (await calendarMetric.Locator(".metric-value").InnerTextAsync()).Trim() == "—",
                "The real home shell starts while a calendar reply is pending; an unknown calendar count stays unavailable.");
            // Start the calendar guard after observing readiness. Leaving before SQLite has
            // finished could cancel the read instead of exercising delivery of a late success.
            await calendarSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Navigate("settings");
            Require(await page.Locator(".page-content[data-page=settings]").IsVisibleAsync(),
                "Settings opens before the first home calendar reply is delivered.");
        }
        catch
        {
            string renderer;
            try
            {
                renderer = await page.EvaluateAsync<string>("""
                    () => JSON.stringify({
                      ready: document.body?.dataset.ready, route: document.querySelector('.page-content')?.dataset.page,
                      calendarSent: !!window.__firstHomeCalendar, calendarKnown: document.querySelector('[data-metric-kind=calendar]')?.dataset.metricKnown
                    })
                    """).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception error) { renderer = $"unavailable ({error.GetType().Name})"; }
            Console.Error.WriteLine($"Navigation startup: {string.Join(" | ", startupTrace)}; at {startupClock.ElapsedMilliseconds} ms: "
                + $"renderer={renderer}; engine={app.CurrentOperation}; browser errors={string.Join(" | ", errors.Take(4))}");
            throw;
        }
        finally { deliverCalendar.TrySetResult(); }
        await WaitForPageAsync(page, "()=>typeof window.__homeCalendarDelivered==='boolean'");
        Require(await page.EvaluateAsync<bool>("window.__homeCalendarDelivered===true")
            && await page.Locator(".page-content[data-page=settings]").IsVisibleAsync() && await page.Locator(".home-counters").CountAsync() == 0,
            "The late successful calendar reply cannot repaint the departed home page.");
        await Navigate("home");
        var inventory = await NavigationRouteIdsAsync(page);
        Require(inventory.SequenceEqual(WorkspaceRoutes) && await page.Locator(".nav-item").CountAsync() == 6
            && await page.Locator(".nav-group-toggle,.workspace-view").CountAsync() == 0
            && await page.EvaluateAsync<bool>("""
                ()=>{const rows=[...document.querySelectorAll('.nav-space-list .nav-item')];return rows.every((node,index)=>{
                  const r=node.getBoundingClientRect(),copy=node.querySelector('.nav-space-copy').getBoundingClientRect();
                  return r.height>=46&&copy.top>=r.top&&copy.bottom<=r.bottom&&(!index||r.top>=rows[index-1].getBoundingClientRect().bottom);
                });}
                """),
            "Four workspaces and two utility destinations replace the old screen inventory, with legible labels and descriptions that fit without overlapping; no legacy group or local-view tab remains.");
        foreach (var route in WorkspaceRoutes)
        {
            await Navigate(route);
            var label = await page.Locator($".nav-item[data-page='{route}'] .nav-label").InnerTextAsync();
            Require(await page.Locator(".nav-item[aria-current=page]").CountAsync() == 1
                && (await page.Locator(".breadcrumb-home").InnerTextAsync()).Trim() == "GLPI Pilot"
                && (await page.Locator("#breadcrumb-page").InnerTextAsync()).Trim() == label.Trim()
                && await page.Locator(".page-content > .notice.danger").CountAsync() == 0,
                "The " + route + " workspace opens through its real main action with an accurate breadcrumb and no page failure.");
        }
        await Navigate("home");
        Require(await page.Locator("[data-home-section=priorities]").CountAsync() == 1 && await page.Locator("[data-home-section=alerts]").CountAsync() == 1
            && await page.Locator("[data-home-section=tours]").CountAsync() == 1 && await Button("Carte des tickets").IsVisibleAsync(),
            "Accueil directly integrates the personal queue, active alerts and today's tours, with a direct map action.");
        await Capture("workspace-home-light");
        await OpenEditor();
        Require(await Editor().Locator("[data-nav-page]").CountAsync() == 6
            && await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Accueil", Exact = true }).IsDisabledAsync()
            && await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Réglages", Exact = true }).IsDisabledAsync()
            && await Editor().Locator(".navigation-group-card").CountAsync() == 0,
            "Customization controls the six spaces themselves and protects Accueil/Réglages without exposing old screens as choices.");
        await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Données", Exact = true }).UncheckAsync();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Épingler Interventions", Exact = true }).ClickAsync();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer Tickets", Exact = true }).PressAsync("ArrowUp");
        await SaveEditor(); var saved = AppSettings.Load().Interface;
        Require(saved.NavigationDefaultsVersion == 3 && saved.HiddenNavigation.Contains("data") && saved.PinnedNavigation.Contains("interventions")
            && saved.NavigationOrder.IndexOf("records") < saved.NavigationOrder.IndexOf("pilot") && saved.Accent == "violet" && saved.FrozenColumns == 3,
            "Space visibility, favorites and keyboard order persist in C# without resetting unrelated appearance or table preferences.");
        await page.ReloadAsync(); await ReadyAsync(page);
        await Navigate("data");
        Require(await page.Locator(".nav-item[data-page=data]").CountAsync() == 0
            && await page.Locator(".nav-item").First.GetAttributeAsync("data-page") == "interventions"
            && await page.Locator("#breadcrumb-page").InnerTextAsync() == "Données",
            "The saved order survives a fresh frontend load, and a hidden space remains accessible from the real command palette.");
        var beforeCancel = JsonSerializer.Serialize(AppSettings.Load().Interface, PilotApplication.Json);
        await OpenEditor(); await Editor().GetByRole(AriaRole.Button, new() { Name = "Réinitialiser le menu", Exact = true }).ClickAsync();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(JsonSerializer.Serialize(AppSettings.Load().Interface, PilotApplication.Json) == beforeCancel,
            "Cancelling a draft reset preserves the saved six-space arrangement exactly.");
        await OpenEditor();
        await page.EvaluateAsync("window.__navRealDrop=false;document.addEventListener('drop',event=>{if(event.isTrusted&&event.target.closest('[data-nav-page]'))window.__navRealDrop=true;},{once:true})");
        await Editor().Locator("[data-nav-page=settings] .navigation-handle").DragToAsync(Editor().Locator("[data-nav-page=data]"));
        await SaveEditor();
        saved = AppSettings.Load().Interface;
        Require(await page.EvaluateAsync<bool>("window.__navRealDrop") && saved.NavigationOrder.IndexOf("settings") < saved.NavigationOrder.IndexOf("data")
            && saved.NavigationOrder.Distinct().Count() == 6,
            "A real pointer drag reorders a workspace once, without dropping or duplicating a destination.");
        await OpenEditor(); await Editor().GetByRole(AriaRole.Button, new() { Name = "Réinitialiser le menu", Exact = true }).ClickAsync(); await SaveEditor();
        saved = AppSettings.Load().Interface;
        Require((await page.Locator(".nav-item").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.page)")).SequenceEqual(WorkspaceRoutes)
            && saved.NavigationOrder.Count == 0 && saved.PinnedNavigation.Count == 0 && saved.HiddenNavigation.Order().SequenceEqual(OptionalNavigationRoutes.Order())
            && saved.Accent == "violet" && saved.FrozenColumns == 3,
            "Reset restores the six main spaces while retaining optional-tool choices and all nonnavigation preferences.");
        var legacy = saved.Copy(); legacy.NavigationDefaultsVersion = 1; legacy.NavigationOrder = ["library", "tickets", "home"];
        legacy.PinnedNavigation = ["library"]; legacy.HiddenNavigation = ["assistant", "enrichment", "populations", "queries"];
        legacy.NavigationGroups = new() { ["library"] = "work" }; legacy.NavigationGroupOrder = ["work", "workspace", "collect", "terrain", "admin"];
        await Apply(legacy); var migrated = AppSettings.Load().Interface;
        Require(migrated.NavigationDefaultsVersion == 3 && migrated.PinnedNavigation.SequenceEqual(new[] { "data" })
            && migrated.NavigationOrder.Count == 6 && !migrated.HiddenNavigation.Contains("pilot")
            && await page.Locator(".nav-item").CountAsync() == 6 && await page.Locator(".nav-utilities .nav-item").First.GetAttributeAsync("data-page") == "data",
            "A real legacy settings update migrates old favorites and grouping into six destinations without hiding Pilotage because one old query tool was hidden.");
        await Apply(saved);
        Require(await page.EvaluateAsync<bool>("""
            async()=>{
              const {navigationTarget}=await import('/js/navigation.js');
              const routes=['day','alerts','pilot-weather','pilot-summary','pilot-groups','pilot-categories','pilot-oldest','pilot-followup','pilot-participants','dashboard','queries','history','technicians','planning','assistant','tickets','collect','sources','library','quality','mapping','enrichment','terrain-reference','terrain-review','pilot-sites','populations','tours','automation','updates','help','operations','pilot-settings'];
              const payload={rowIndex:2,revision:19,ticketKey:'opaque',indices:[1,2],focusKeys:['team:a'],boardId:'board-a'};
              return routes.every(route=>{const result=navigationTarget(route,payload);return ['home','pilot','records','interventions','data','settings'].includes(result.id)&&Object.entries(payload).every(([key,value])=>JSON.stringify(result.params[key])===JSON.stringify(value));})
                &&navigationTarget('tickets').params.resumeTicket===true
                &&navigationTarget('tours',{tourId:'a'}).params.panel==='planning'
                &&navigationTarget('pilot-summary').params.focusSection==='flow'
                &&navigationTarget('pilot-categories').params.focusSection==='analysis';
            }
            """), "Every legacy deep link maps to a new space without losing revision, row selection, ticket identity, team scope or composition parameters.");
        bridge.Publish("app.navigate", new { page = "day", @params = new { panel = "priorities" } }); await delivery;
        await WaitForNavigationTargetAsync(page, "home");
        Require(await page.Locator("[data-home-section=priorities]").IsVisibleAsync() && await page.Locator(".workspace-view").CountAsync() == 0
            && await page.Locator("[data-home-section=priorities]").EvaluateAsync<bool>("node=>{const r=node.getBoundingClientRect(),host=node.closest('.workspace').getBoundingClientRect();return r.top>=host.top-1&&r.top<host.bottom-60;}"),
            "A native legacy day link opens the new integrated Accueil instead of reviving an old page.");
        await Navigate("data");
        await OpenDemonstrationFromSettingsAsync(page); await WaitForNavigationTargetAsync(page, "home");
        await Navigate("pilot");
        await Capture("workspace-pilot-light");
        bridge.Publish("app.navigate", new { page = "tickets", @params = new { rowIndex = 0, revision = app.Revision } }); await delivery;
        await WaitForNavigationTargetAsync(page, "records");
        await page.Locator(".tickets-detail-pane .work-ticket-heading h2").WaitForAsync();
        Require((await page.Locator(".work-ticket-heading h2").InnerTextAsync()).StartsWith("#1", StringComparison.Ordinal)
            && await page.Locator(".nav-item[data-page=records][aria-current=page]").CountAsync() == 1,
            "A native ticket deep link reaches the exact simulated occurrence in the unified Tickets panel and highlights its single main space.");
        await Navigate("data"); await Capture("workspace-data-light");
        await Navigate("settings");
        await page.Locator(".page-content").GetByRole(AriaRole.Button, new() { Name = "Organiser le menu", Exact = true }).ClickAsync();
        Require(await Editor().Locator("[data-nav-page]").CountAsync() == 6, "Réglages opens the same six-space customization controls as the permanent sidebar action.");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await Navigate("home");
        var fold = page.Locator(".sidebar > .nav-fold");
        Task<bool> Folded() => page.Locator(".shell").EvaluateAsync<bool>("node=>node.classList.contains('nav-collapsed')");
        Task SettledAsync() => WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        Require(await fold.EvaluateAsync<bool>("""
            node=>{const b=node.getBoundingClientRect(),s=node.parentElement.getBoundingClientRect(),label=node.querySelector('.nav-fold-label');return node===node.parentElement.lastElementChild&&b.height>=32&&b.height<=40&&b.width>=s.width-40&&label.getClientRects().length>0&&label.textContent==='Replier le menu'&&b.bottom<=innerHeight&&s.bottom-b.bottom<=16;}
            """) && await fold.GetAttributeAsync("aria-label") == "Replier le menu" && await fold.GetAttributeAsync("aria-keyshortcuts") == "Control+B"
            && !await Button("Navigation").IsVisibleAsync(),
            "As agreed for Domenia, Pesto, FILPRO and GLPI, the unfolded menu ends with a labelled Replier le menu row pinned at the very bottom of the rail, without a second fold control in the top bar.");
        await fold.ClickAsync(); await SettledAsync();
        var visibleSpaces = await page.Locator(".sidebar .nav-item").EvaluateAllAsync<int>("nodes=>nodes.filter(node=>node.getClientRects().length&&node.getAttribute('aria-label')).length");
        Require(await Folded() && await fold.GetAttributeAsync("aria-expanded") == "false" && await fold.GetAttributeAsync("aria-label") == "Déplier le menu"
            && await fold.EvaluateAsync<bool>("node=>{const b=node.getBoundingClientRect(),s=node.parentElement.getBoundingClientRect();return Math.round(b.width)===32&&Math.round(b.height)===32&&Math.abs((b.left-s.left)-(s.right-b.right))<=1.5&&!node.querySelector('.nav-fold-label').getClientRects().length;}"),
            "Folding turns the bottom row into a centred 32 px icon alone, offering Déplier le menu.");
        Require(visibleSpaces == 6, $"The folded rail keeps the six named navigation controls as icons (visible: {visibleSpaces}).");
        Require(app.Settings.Interface.NavigationCollapsed, "The folded choice is saved in the settings of the running C# application (the isolated demonstration profile at this point).");
        await page.Locator(".sidebar .nav-item").Nth(1).HoverAsync();
        await WaitForPageAsync(page, "getComputedStyle(document.querySelector('.rail-tip')).opacity==='1'");
        Require(await page.Locator(".rail-tip").EvaluateAsync<bool>("""
            tip=>{const item=document.querySelectorAll('.sidebar .nav-item')[1],t=tip.getBoundingClientRect(),i=item.getBoundingClientRect(),rail=document.querySelector('.sidebar').getBoundingClientRect();return tip.textContent===item.getAttribute('aria-label')&&t.left>=rail.right&&t.right<=innerWidth&&Math.abs((t.top+t.bottom)/2-(i.top+i.bottom)/2)<=2;}
            """), "Hovering a folded rail icon shows its label beside the rail, as in Domenia and Pesto.");
        await page.Mouse.MoveAsync(700, 400);
        await WaitForPageAsync(page, "getComputedStyle(document.querySelector('.rail-tip')).opacity==='0'");
        await page.Keyboard.PressAsync("F11"); await WaitForPageAsync(page, "document.body.classList.contains('focus-mode')");
        await Button("Navigation").ClickAsync(); await WaitForPageAsync(page, "!document.body.classList.contains('focus-mode')"); await SettledAsync();
        Require(await Folded() && app.Settings.Interface.NavigationCollapsed, "Leaving focus mode brings back the saved folded rail instead of unfolding it.");
        await page.ReloadAsync(); await ReadyAsync(page);
        Require(await Folded() && await fold.IsVisibleAsync(), "The folded menu is restored after a reload, with its unfold control still at the bottom of the rail.");
        await page.Keyboard.PressAsync("Control+b"); await SettledAsync();
        Require(!await Folded() && await fold.GetAttributeAsync("aria-label") == "Replier le menu" && !app.Settings.Interface.NavigationCollapsed,
            "Ctrl+B unfolds the menu as in Domenia and Pesto and saves the expanded state.");
        await page.Keyboard.PressAsync("F11"); await WaitForPageAsync(page, "document.body.classList.contains('focus-mode')");
        await Button("Navigation").ClickAsync(); await WaitForPageAsync(page, "!document.body.classList.contains('focus-mode')");
        Require(await page.Locator(".sidebar").IsVisibleAsync(), "The navigation action exits focus mode and restores access to all main spaces.");
        await page.SetViewportSizeAsync(600, 850); await Button("Navigation").ClickAsync();
        await page.Keyboard.PressAsync("Tab");
        var trapped = await page.EvaluateAsync<bool>("document.querySelector('.sidebar').contains(document.activeElement)");
        await page.Keyboard.PressAsync("Escape");
        Require(trapped && await Button("Navigation").GetAttributeAsync("aria-expanded") == "false"
            && await Button("Navigation").EvaluateAsync<bool>("node=>node===document.activeElement"),
            "The compact drawer traps keyboard focus, closes with Escape and returns focus to its launcher.");
        await OpenEditor(); await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer Tickets", Exact = true }).PressAsync("ArrowDown");
        Require(await page.EvaluateAsync<bool>("document.activeElement?.dataset.navigationFocus==='page:records'&&document.documentElement.scrollWidth<=innerWidth+1&&document.querySelector('.navigation-modal').scrollWidth<=document.querySelector('.navigation-modal').clientWidth+1"),
            "At a narrow width the six-space editor keeps keyboard focus after reordering and fits the viewport.");
        await Capture("workspace-navigation-compact");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(externalRequests == 0 && errors.IsEmpty,
            "The redesigned shell, migration and real navigation journeys produce no unauthorized request or browser error." + (errors.IsEmpty ? "" : "\n" + string.Join("\n", errors)));
        await delivery;
    }
}
