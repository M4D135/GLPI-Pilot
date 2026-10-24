using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static readonly string[] WorkspaceRoutes = ["home", "pilot", "records", "terrain-map", "data", "settings"];
    private static readonly string[] OptionalNavigationRoutes = ["assistant", "enrichment", "populations"];
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
            if (id != "home" && panel != "compositions")
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
        if (parameters.TryGetProperty("panel", out var panelValue))
        {
            var panel = panelValue.GetString()!;
            if (panel != "compositions")
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
        await using var actualBridge = new WebBridge(app.Operations.Concat(["app.ready", "host.editor"]), async (op, data, ct) =>
        {
            if (op == "app.ready") return new { ready = true };
            if (op == "host.editor") { app.EditorOpen = data.GetProperty("open").GetBoolean(); return new { updated = true }; }
            return await app.ExecuteAsync(op, data, ct);
        }, Post); bridge = actualBridge;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (()=>{const listeners=new Set();window.chrome||={};window.chrome.webview={
              postMessage:message=>window.__pilotSend(JSON.stringify(message)),
              addEventListener:(name,handler)=>{if(name==='message')listeners.add(handler);},
              removeEventListener:(name,handler)=>listeners.delete(handler)
            };window.__pilotDeliver=message=>{for(const listener of listeners)listener({data:message});};})();
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
            var notices = page.Locator("#notifications .toast");
            while (await notices.CountAsync() > 0) await notices.First.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(args[argument + 1], name + ".png"), Animations = ScreenshotAnimations.Allow });
        }
        async Task Apply(InterfacePreferences preferences) { await Call(app, "app.appearance", new { preferences }); await delivery; }
        await page.GotoAsync("https://glpi-pilot.local/index.html"); await page.Locator("body[data-ready=true]").WaitForAsync();
        var inventory = await NavigationRouteIdsAsync(page);
        Require(inventory.SequenceEqual(WorkspaceRoutes) && await page.Locator(".nav-item").CountAsync() == 6
            && await page.Locator(".nav-group-toggle,.workspace-view").CountAsync() == 0
            && await page.EvaluateAsync<bool>("""
                ()=>{const rows=[...document.querySelectorAll('.nav-space-list .nav-item')];return rows.every((node,index)=>{
                  const r=node.getBoundingClientRect(),copy=node.querySelector('.nav-space-copy').getBoundingClientRect();
                  return r.height>=76&&copy.top>=r.top&&copy.bottom<=r.bottom&&(!index||r.top>=rows[index-1].getBoundingClientRect().bottom);
                });}
                """),
            "Six purpose-built spaces replace the old screen inventory, with legible labels and descriptions that fit without overlapping; no legacy group or local-view tab remains.");
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
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Épingler Terrain", Exact = true }).ClickAsync();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer Tickets", Exact = true }).PressAsync("ArrowUp");
        await SaveEditor(); var saved = AppSettings.Load().Interface;
        Require(saved.NavigationDefaultsVersion == 2 && saved.HiddenNavigation.Contains("data") && saved.PinnedNavigation.Contains("terrain-map")
            && saved.NavigationOrder.IndexOf("records") < saved.NavigationOrder.IndexOf("pilot") && saved.Accent == "violet" && saved.FrozenColumns == 3,
            "Space visibility, favorites and keyboard order persist in C# without resetting unrelated appearance or table preferences.");
        await page.ReloadAsync(); await page.Locator("body[data-ready=true]").WaitForAsync();
        await Navigate("data");
        Require(await page.Locator(".nav-item[data-page=data]").CountAsync() == 0
            && await page.Locator(".nav-item").First.GetAttributeAsync("data-page") == "terrain-map"
            && await page.Locator("#breadcrumb-page").InnerTextAsync() == "Données",
            "The saved order survives a fresh frontend load, and a hidden space remains accessible from the real command palette.");
        var beforeCancel = JsonSerializer.Serialize(AppSettings.Load().Interface, PilotApplication.Json);
        await OpenEditor(); await Editor().GetByRole(AriaRole.Button, new() { Name = "Réinitialiser le menu", Exact = true }).ClickAsync();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(JsonSerializer.Serialize(AppSettings.Load().Interface, PilotApplication.Json) == beforeCancel,
            "Cancelling a draft reset preserves the saved six-space arrangement exactly.");
        await OpenEditor();
        await page.EvaluateAsync("window.__navRealDrop=false;document.addEventListener('drop',event=>{if(event.isTrusted&&event.target.closest('[data-nav-page]'))window.__navRealDrop=true;},{once:true})");
        await Editor().Locator("[data-nav-page=data] .navigation-handle").DragToAsync(Editor().Locator("[data-nav-page=home]"));
        await SaveEditor();
        saved = AppSettings.Load().Interface;
        Require(await page.EvaluateAsync<bool>("window.__navRealDrop") && saved.NavigationOrder.IndexOf("data") < saved.NavigationOrder.IndexOf("home")
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
        Require(migrated.NavigationDefaultsVersion == 2 && migrated.PinnedNavigation.SequenceEqual(new[] { "data" })
            && migrated.NavigationOrder.Count == 6 && !migrated.HiddenNavigation.Contains("pilot")
            && await page.Locator(".nav-item").CountAsync() == 6 && await page.Locator(".nav-item").First.GetAttributeAsync("data-page") == "data",
            "A real legacy settings update migrates old favorites and grouping into six destinations without hiding Pilotage because one old query tool was hidden.");
        await Apply(saved);
        Require(await page.EvaluateAsync<bool>("""
            async()=>{
              const {navigationTarget}=await import('/js/navigation.js');
              const routes=['day','alerts','pilot-weather','pilot-summary','pilot-groups','pilot-categories','pilot-oldest','pilot-followup','pilot-participants','dashboard','queries','history','technicians','planning','assistant','tickets','collect','sources','library','quality','mapping','enrichment','terrain-reference','terrain-review','pilot-sites','populations','tours','automation','updates','help','operations','pilot-settings'];
              const payload={rowIndex:2,revision:19,ticketKey:'opaque',indices:[1,2],focusKeys:['team:a'],boardId:'board-a'};
              return routes.every(route=>{const result=navigationTarget(route,payload);return ['home','pilot','records','terrain-map','data','settings'].includes(result.id)&&Object.entries(payload).every(([key,value])=>JSON.stringify(result.params[key])===JSON.stringify(value));})
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
        await Button("Essayer la simulation").ClickAsync(); await WaitForNavigationTargetAsync(page, "home");
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
        await page.Locator(".page-content").GetByRole(AriaRole.Button, new() { Name = "Organiser les six espaces", Exact = true }).ClickAsync();
        Require(await Editor().Locator("[data-nav-page]").CountAsync() == 6, "Réglages opens the same six-space customization controls as the permanent sidebar action.");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await Navigate("home");
        await Button("Navigation").ClickAsync();
        Require(await page.Locator(".shell").EvaluateAsync<bool>("node=>node.classList.contains('nav-collapsed')")
            && await Button("Navigation").GetAttributeAsync("aria-expanded") == "false", "The wide sidebar collapses while retaining the six named navigation controls.");
        await Button("Navigation").ClickAsync();
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
