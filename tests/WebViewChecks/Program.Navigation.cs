using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static readonly string[] OptionalNavigationRoutes = ["assistant", "enrichment", "populations"];

    private static async Task ClickNavigationAsync(IPage page, string route)
    {
        var item = page.Locator($".nav-item[data-page='{route}']");
        if (await item.CountAsync() == 0)
        {
            var search = route switch
            {
                "assistant" => "Assistant", "enrichment" => "Lectures ciblées", "populations" => "Effectifs",
                _ => throw new InvalidOperationException("Missing navigation shortcut: " + route)
            };
            await page.GetByRole(AriaRole.Button, new() { Name = "Rechercher un écran, une action ou un ticket", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync(search);
            await page.Locator($".command-palette [data-command-id='page:{route}']").ClickAsync();
            return;
        }
        if (!await page.Locator(".sidebar").IsVisibleAsync())
            await page.GetByRole(AriaRole.Button, new() { Name = "Navigation", Exact = true }).ClickAsync();
        var toggle = page.Locator($".nav-section:has(.nav-item[data-page='{route}']) .nav-group-toggle");
        if (await toggle.CountAsync() > 0 && await toggle.GetAttributeAsync("aria-expanded") == "false") await toggle.ClickAsync();
        await item.ClickAsync();
    }

    private static async Task CheckNavigationInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        Console.WriteLine("Navigation browser version: " + browser.Version);
        await using var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.NoPreference });
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
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
            var mime = Path.GetExtension(path) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        var host = new TestHost(_root) { Page = page }; WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string json)
        {
            lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", json); }, TaskScheduler.Default).Unwrap();
        }
        var settings = new AppSettings { Theme = "light", ExportFolder = Path.Combine(_root, "Exports") };
        settings.Interface.Accent = "violet"; settings.Interface.FrozenColumns = 3;
        await using var app = new PilotApplication(host, (name, data) => bridge?.Publish(name, data), settings);
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
        await page.AddInitScriptAsync("""
            (() => {
              window.__navigationDragTrace = [];
              window.__navigationDragTraceEnabled = false;
              const describe = node => node instanceof Element ? {
                tag: node.tagName.toLowerCase(), class: node.getAttribute('class'),
                page: node.closest('[data-nav-page]')?.dataset.navPage || '',
                group: node.closest('[data-nav-group]')?.dataset.navGroup || '',
                focus: node.dataset.navigationFocus || ''
              } : null;
              for (const type of ['pointerdown', 'dragstart', 'dragenter', 'dragover', 'drop', 'dragend', 'pointerup']) {
                document.addEventListener(type, event => {
                  if (!window.__navigationDragTraceEnabled || window.__navigationDragTrace.length >= 160) return;
                  const entry = {type, trusted: event.isTrusted, target: describe(event.target),
                    x: event.clientX, y: event.clientY, focus: describe(document.activeElement),
                    scroll: document.querySelector('dialog.navigation-modal .modal-body')?.scrollTop};
                  window.__navigationDragTrace.push(entry);
                  // Observe after the complete event dispatch, including target/bubble listeners.
                  setTimeout(() => { entry.prevented = event.defaultPrevented; }, 0);
                }, true);
              }
            })();
            """);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        ILocator Editor() => page.GetByRole(AriaRole.Dialog, new() { Name = "Personnaliser la navigation", Exact = true });
        async Task OpenEditor()
        {
            await Button("Personnaliser la navigation").ClickAsync();
            await Editor().WaitForAsync();
        }
        async Task SaveEditor()
        {
            await Editor().GetByRole(AriaRole.Button, new() { Name = "Enregistrer le menu", Exact = true }).ClickAsync();
            await Editor().WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
        async Task Ready(string id) => await WaitForPageAsync(page,
            "id => document.querySelector('.page-content')?.dataset.page===id && !document.querySelector('#content').hasAttribute('aria-busy')", id);
        async Task Capture(string name)
        {
            var index = Array.IndexOf(args, "--screenshots"); if (index < 0 || index + 1 >= args.Length) return;
            var directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
            await page.Locator("#notifications .toast button").EvaluateAllAsync<int>("buttons => { buttons.forEach(button=>button.click()); return buttons.length; }");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
            Console.WriteLine("CAPTURE " + name + ": " + await page.EvaluateAsync<string>("""
                JSON.stringify(['dialog[open]','.analytics','.hero'].map(selector=>{
                  const node=document.querySelector(selector); if(!node)return null;
                  const style=getComputedStyle(node);
                  return {selector,opacity:style.opacity,backgroundColor:style.backgroundColor,backdropFilter:style.backdropFilter};
                }).filter(Boolean))
                """));
        }
        async Task SelectHiddenRoute(string search, string title)
        {
            await Button("Rechercher un écran, une action ou un ticket").ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher une commande ou un ticket", Exact = true }).FillAsync(search);
            await page.Locator(".command-palette [role=option]").Filter(new() { Has = page.GetByText(title, new() { Exact = true }) }).ClickAsync();
        }
        async Task ApplyPreferences(InterfacePreferences preferences, string? theme = null)
        {
            await Call(app, "app.appearance", new { preferences, theme });
            await delivery;
        }
        async Task DragDiagnostics(string phase)
        {
            Console.WriteLine("NAVIGATION DRAG " + phase + ": " + await page.EvaluateAsync<string>("""
                JSON.stringify({
                  events: window.__navigationDragTrace,
                  dialogs: [...document.querySelectorAll('dialog[open]')].map(node=>node.getAttribute('aria-label')),
                  groups: [...document.querySelectorAll('.navigation-group-card')].map(node=>({
                    group:node.dataset.navGroup,pages:[...node.querySelectorAll('[data-nav-page]')].map(row=>row.dataset.navPage)
                  })),
                  focus: document.activeElement?.dataset.navigationFocus || document.activeElement?.tagName,
                  scroll: document.querySelector('dialog.navigation-modal .modal-body')?.scrollTop,
                  status: document.querySelector('.navigation-editor-status')?.textContent,
                  transform: document.querySelector('dialog.navigation-modal') && getComputedStyle(document.querySelector('dialog.navigation-modal')).transform
                })
                """));
        }
        async Task SettleNavigationGeometry(params string[] selectors)
        {
            // Native DragTo disables waitAfter. Finish the visible dialog animation,
            // then observe stable frames after paint() schedules its scrollIntoView.
            await Editor().EvaluateAsync("""
                async (modal, selectors) => {
                  let deadline;
                  try {
                    await Promise.race([
                      (async () => {
                        const finite = modal.getAnimations({subtree:true}).filter(animation =>
                          Number.isFinite(animation.effect?.getComputedTiming().endTime));
                        await Promise.all(finite.map(animation => animation.finished.catch(() => {})));
                        let previous = '', stableFrames = 0;
                        while (stableFrames < 2) {
                          await new Promise(resolve => requestAnimationFrame(resolve));
                          if (!modal.isConnected) throw new Error('Navigation editor closed before the drag geometry settled.');
                          const scroller = modal.querySelector('.modal-body');
                          const rectangles = selectors.map(selector => {
                            const node = modal.querySelector(selector);
                            if (!node) throw new Error('Navigation drag endpoint disappeared: ' + selector);
                            const rect = node.getBoundingClientRect();
                            return [rect.x, rect.y, rect.width, rect.height];
                          });
                          const current = JSON.stringify([scroller.scrollTop, scroller.scrollLeft, rectangles]);
                          stableFrames = current === previous ? stableFrames + 1 : 0;
                          previous = current;
                        }
                      })(),
                      new Promise((_, reject) => { deadline = setTimeout(() => reject(new Error('Navigation drag geometry did not settle within 5 seconds.')), 5000); })
                    ]);
                  } finally { clearTimeout(deadline); }
                }
                """, selectors);
        }

        await page.GotoAsync("https://glpi-pilot.local/index.html");
        await page.Locator("body[data-ready=true]").WaitForAsync();
        var defaultIds = await page.Locator(".nav-item").EvaluateAllAsync<string[]>("nodes => nodes.map(node=>node.dataset.page)");
        Require(defaultIds.Length == 32 && defaultIds.Distinct().Count() == 32 && !defaultIds.Intersect(OptionalNavigationRoutes).Any()
            && await page.Locator(".nav-section[data-nav-group=collect] .nav-item[data-page=library]").CountAsync() == 1
            && await page.Locator(".nav-section[data-nav-group=workspace] .nav-item[data-page=alerts]").CountAsync() == 1
            && await Button("Personnaliser la navigation").IsVisibleAsync()
            && await page.Locator(".hero").IsVisibleAsync(),
            "The default task-oriented menu keeps 32 unique shortcuts, hides the optional assistant, targeted reads and populations, and always exposes customization.");
        Require(await page.EvaluateAsync<bool>("""
            () => [...document.querySelectorAll('.nav-section')].every(section => {
              const toggle = section.querySelector('.nav-group-toggle');
              const items = section.querySelector('.nav-group-items');
              const expanded = ['workspace', 'pinned'].includes(section.dataset.navGroup);
              return toggle?.tagName === 'BUTTON' && items?.id === toggle.getAttribute('aria-controls')
                && toggle.getAttribute('aria-expanded') === String(expanded) && items.hidden === !expanded;
            })
            """), "Existing navigation groups expose linked accessible toggles and start folded except the daily workspace and pinned shortcuts.");
        await Capture("navigation-home-light");

        var collectToggle = page.Locator(".nav-group-toggle[data-nav-group-toggle=collect]");
        await collectToggle.ClickAsync();
        Require(await collectToggle.GetAttributeAsync("aria-expanded") == "true" && await page.Locator(".nav-item[data-page=collect]").IsVisibleAsync(),
            "Clicking an existing group heading reveals its shortcuts without introducing a new menu section.");
        await collectToggle.PressAsync("Enter");
        var folded = await collectToggle.GetAttributeAsync("aria-expanded") == "false" && !await page.Locator(".nav-item[data-page=collect]").IsVisibleAsync();
        await collectToggle.PressAsync("Space");
        Require(folded && await collectToggle.GetAttributeAsync("aria-expanded") == "true"
            && await collectToggle.EvaluateAsync<bool>("node => node === document.activeElement"),
            "Enter and Space toggle the same group while retaining keyboard focus on its heading.");
        await ClickNavigationAsync(page, "collect"); await Ready("collect");
        await collectToggle.ClickAsync();
        await ClickNavigationAsync(page, "home"); await Ready("home");
        Require(await collectToggle.GetAttributeAsync("aria-expanded") == "false",
            "A group manually folded by the user stays folded when another page is visited in the same UI session.");
        await SelectHiddenRoute("Collecte GLPI", "Collecte GLPI"); await Ready("collect");
        Require(await collectToggle.GetAttributeAsync("aria-expanded") == "true" && await page.Locator(".nav-item[data-page=collect]").IsVisibleAsync(),
            "Navigating through the palette automatically opens the current page's folded group.");
        await ClickNavigationAsync(page, "home"); await Ready("home");

        await OpenEditor();
        Require(await Editor().Locator("[data-nav-page]").CountAsync() == 35
            && await Editor().Locator("[data-nav-page=assistant] input[type=checkbox], [data-nav-page=enrichment] input[type=checkbox], [data-nav-page=populations] input[type=checkbox]").EvaluateAllAsync<bool>("nodes => nodes.length===3 && nodes.every(node=>!node.checked)"),
            "The navigation editor retains all 35 routes and presents the three optional defaults as reversible visibility choices.");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Tout afficher", Exact = true }).ClickAsync();
        await SaveEditor(); await page.ReloadAsync(); await page.Locator("body[data-ready=true]").WaitForAsync();
        Require(await page.Locator(".nav-item").CountAsync() == 35 && AppSettings.Load().Interface.HiddenNavigation.Count == 0
            && AppSettings.Load().Interface.NavigationDefaultsVersion == 1,
            "Show all survives a fresh frontend load and the versioned defaults do not hide deliberately restored routes again.");
        await OpenEditor();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Réinitialiser le menu", Exact = true }).ClickAsync();
        await SaveEditor();

        await OpenEditor();
        await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Aide", Exact = true }).UncheckAsync();
        await Editor().GetByRole(AriaRole.Combobox, new() { Name = "Groupe de Bibliothèque", Exact = true }).SelectOptionAsync("pinned");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer Mon espace", Exact = true }).PressAsync("ArrowUp");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer le groupe Tickets et équipe", Exact = true }).PressAsync("ArrowUp");
        var protectedControls = await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Accueil", Exact = true }).IsDisabledAsync()
            && await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Réglages", Exact = true }).IsDisabledAsync();
        await SaveEditor();
        var persisted = AppSettings.Load().Interface;
        Require(protectedControls && persisted.HiddenNavigation.Contains("help") && persisted.PinnedNavigation.Contains("library")
            && persisted.NavigationOrder.IndexOf("day") < persisted.NavigationOrder.IndexOf("home")
            && persisted.NavigationGroupOrder.IndexOf("work") < persisted.NavigationGroupOrder.IndexOf("analyze")
            && persisted.Accent == "violet" && persisted.FrozenColumns == 3,
            "Visibility, pinning, page order and group order save through the real C# settings store; keyboard moves preserve unrelated preferences and the two permanent landmarks.");

        await page.ReloadAsync(); await page.Locator("body[data-ready=true]").WaitForAsync();
        await SelectHiddenRoute("Guide", "Guide et raccourcis"); await Ready("help");
        Require(await page.Locator(".nav-item[data-page=help]").CountAsync() == 0
            && await page.Locator(".nav-section[data-nav-group=pinned] .nav-item[data-page=library]").CountAsync() == 1
            && (await page.Locator(".nav-section[data-nav-group=workspace] .nav-item").First.GetAttributeAsync("data-page")) == "day",
            "Saved navigation survives a fresh frontend load, and a hidden screen still opens through the command palette.");

        var beforeCancel = JsonSerializer.Serialize(AppSettings.Load().Interface, PilotApplication.Json);
        await OpenEditor();
        await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Collecte GLPI", Exact = true }).UncheckAsync();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Réinitialiser le menu", Exact = true }).ClickAsync();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        Require(JsonSerializer.Serialize(AppSettings.Load().Interface, PilotApplication.Json) == beforeCancel
            && await page.Locator(".nav-item[data-page=collect]").CountAsync() == 1
            && await page.Locator(".nav-item[data-page=help]").CountAsync() == 0,
            "Cancel discards both visibility edits and a draft reset without changing the saved menu.");

        await OpenEditor();
        await page.EvaluateAsync("window.__navigationDragTraceEnabled=true");
        try
        {
            await SettleNavigationGeometry(".navigation-item-row[data-nav-page=library] .navigation-handle", ".navigation-item-row[data-nav-page=day]");
            await DragDiagnostics("before page gesture");
            await Editor().Locator(".navigation-item-row[data-nav-page=library] .navigation-handle")
                .DragToAsync(Editor().Locator(".navigation-item-row[data-nav-page=day]"));
            await DragDiagnostics("after page gesture");
            await WaitForPageAsync(page, "document.querySelector('.navigation-item-row[data-nav-page=library]')?.closest('[data-nav-group]')?.dataset.navGroup==='workspace'");
            await SettleNavigationGeometry(".navigation-group-card[data-nav-group=workspace] > .navigation-group-heading .navigation-handle", ".navigation-group-card[data-nav-group=pinned] > .navigation-group-heading");
            Require(await Editor().Locator(".navigation-group-card[data-nav-group=pinned] [data-nav-page=library]").CountAsync() == 0
                && await Editor().Locator(".navigation-group-card[data-nav-group=workspace] [data-nav-page=library]").CountAsync() == 1
                && await page.EvaluateAsync<bool>("window.__navigationDragTrace.some(event=>event.type==='dragstart' && event.trusted && event.target?.page==='library') && window.__navigationDragTrace.some(event=>event.type==='drop' && event.trusted && event.target?.page==='day' && event.prevented)"),
                "The real page drag removes Bibliothèque from pinned shortcuts and places it once in Mon quotidien before another gesture starts.");
            var pageOrder = await Editor().Locator(".navigation-group-card[data-nav-group=workspace] [data-nav-page]").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.navPage)");
            Require(Array.IndexOf(pageOrder, "library") < Array.IndexOf(pageOrder, "day"),
                "The real page drop inserts Bibliothèque before Mon espace at the requested target.");
            await Editor().Locator(".navigation-group-card[data-nav-group=workspace] > .navigation-group-heading .navigation-handle")
                .DragToAsync(Editor().Locator(".navigation-group-card[data-nav-group=pinned] > .navigation-group-heading"));
            await DragDiagnostics("after group gesture");
            await WaitForPageAsync(page, "(() => {const groups=[...document.querySelectorAll('.navigation-group-card')].map(node=>node.dataset.navGroup);return groups.indexOf('workspace')>=0 && groups.indexOf('workspace')<groups.indexOf('pinned');})()");
            await SettleNavigationGeometry(".navigation-group-card[data-nav-group=workspace]", ".navigation-group-card[data-nav-group=pinned]");
            Require(await Editor().Locator("[data-nav-page]").CountAsync() == 35
                && await Editor().Locator("[data-nav-page]").EvaluateAllAsync<bool>("nodes=>new Set(nodes.map(node=>node.dataset.navPage)).size===35")
                && await page.EvaluateAsync<bool>("window.__navigationDragTrace.some(event=>event.type==='dragstart' && event.trusted && event.target?.focus==='group:workspace') && window.__navigationDragTrace.some(event=>event.type==='drop' && event.trusted && event.target?.group==='pinned' && event.prevented)"),
                "The real group drag moves Mon quotidien before Épinglés without losing or duplicating a route.");
            await SaveEditor(); persisted = AppSettings.Load().Interface;
            Require(!persisted.PinnedNavigation.Contains("library") && persisted.NavigationGroups.GetValueOrDefault("library") == "workspace"
                && persisted.NavigationOrder.IndexOf("library") < persisted.NavigationOrder.IndexOf("day")
                && persisted.NavigationGroupOrder.IndexOf("workspace") < persisted.NavigationGroupOrder.IndexOf("pinned"),
                "Saving both real pointer moves retains their observed page membership and page/group orders in C# preferences.");
        }
        catch
        {
            await DragDiagnostics("failure");
            await Capture("navigation-drag-failure");
            Console.Error.WriteLine("NAVIGATION SAVED PREFERENCES: " + JsonSerializer.Serialize(AppSettings.Load().Interface, PilotApplication.Json));
            throw;
        }
        finally { await page.EvaluateAsync("window.__navigationDragTraceEnabled=false"); }

        await OpenEditor();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Masquer les écrans facultatifs", Exact = true }).ClickAsync();
        await SaveEditor();
        await SelectHiddenRoute("Bibliothèque", "Bibliothèque de tickets"); await Ready("library");
        Require(await page.Locator(".nav-item").CountAsync() == 2
            && await page.Locator(".nav-item[data-page=home],.nav-item[data-page=settings]").CountAsync() == 2
            && await Button("Personnaliser la navigation").IsVisibleAsync(),
            "Hiding every optional shortcut leaves Accueil, Réglages and the permanent customization action available while hidden business routes remain usable.");

        await OpenEditor();
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Réinitialiser le menu", Exact = true }).ClickAsync();
        await SaveEditor(); persisted = AppSettings.Load().Interface;
        await ClickNavigationAsync(page, "settings"); await Ready("settings");
        await page.Locator(".page-content[data-page=settings]").GetByRole(AriaRole.Button, new() { Name = "Personnaliser la navigation", Exact = true }).ClickAsync();
        var sharedSettingsEditor = await page.Locator("dialog.navigation-modal").IsVisibleAsync()
            && await Editor().Locator(".navigation-item-row").CountAsync() == 35;
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await ClickNavigationAsync(page, "home"); await Ready("home");
        Require(sharedSettingsEditor && (await page.Locator(".nav-item").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.page)")).SequenceEqual(defaultIds)
            && persisted.NavigationGroups.Count == 0 && persisted.NavigationGroupOrder.Count == 0
            && persisted.PinnedNavigation.Count == 0 && persisted.HiddenNavigation.Order().SequenceEqual(OptionalNavigationRoutes.Order()) && persisted.NavigationOrder.Count == 0
            && persisted.NavigationDefaultsVersion == 1
            && persisted.Accent == "violet" && persisted.FrozenColumns == 3,
            "Reset restores the default menu without changing other preferences, and Réglages opens the same complete navigation editor.");

        var legacy = persisted.Copy(); legacy.NavigationOrder = ["records", "collect"]; legacy.HiddenNavigation = ["operations"]; legacy.PinnedNavigation = ["tickets"];
        legacy.NavigationGroupOrder = ["pinned", "workspace", "collect", "analyze", "work", "terrain", "admin"];
        await ApplyPreferences(legacy);
        Require(await page.Locator(".nav-section[data-nav-group=pinned] .nav-item[data-page=tickets]").CountAsync() == 1
            && await page.Locator(".nav-item[data-page=operations]").CountAsync() == 0
            && await page.Locator(".nav-section[data-nav-group=collect] .nav-item").First.GetAttributeAsync("data-page") == "records",
            "Existing flat order, hidden pages and pinned shortcuts are honored without requiring new grouping preferences.");
        var upgradedGroups = await page.Locator(".nav-section").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.navGroup)");
        var pilotPosition = Array.IndexOf(upgradedGroups, "pilot");
        var upgradeVisible = pilotPosition == Array.IndexOf(upgradedGroups, "workspace") + 1
            && pilotPosition < Array.IndexOf(upgradedGroups, "collect")
            && await page.Locator(".nav-section[data-nav-group=pilot] .nav-item").CountAsync() == 9;
        legacy.NavigationGroupOrder.Add("pilot"); await ApplyPreferences(legacy);
        Require(upgradeVisible && (await page.Locator(".nav-section").Last.GetAttributeAsync("data-nav-group")) == "pilot",
            "An old saved menu receives the nine pilot pages beside the daily workspace, while an explicit later group position is preserved.");
        await ApplyPreferences(persisted, "dark");
        await Call(app, "app.demo");
        await ClickNavigationAsync(page, "dashboard"); await Ready("dashboard");
        await Capture("navigation-dashboard-dark");
        await ClickNavigationAsync(page, "home"); await Ready("home");
        var normalMotion = await page.EvaluateAsync<bool>("document.documentElement.dataset.domeniaMotion==='on' && getComputedStyle(document.body,'::before').animationName!=='none'");
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        var reducedMotion = await page.EvaluateAsync<bool>("getComputedStyle(document.body,'::before').animationName==='none'");
        await Capture("navigation-home-reduced");
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        var contrast = persisted.Copy(); contrast.HighContrast = true;
        await ApplyPreferences(contrast);
        await WaitForPageAsync(page, "document.documentElement.dataset.domeniaMotion==='off'");
        Require(normalMotion && reducedMotion && await page.Locator(".hero").IsVisibleAsync()
            && await page.EvaluateAsync<bool>("getComputedStyle(document.body,'::before').animationName==='none'"),
            "Domenia decorations animate in the normal theme, stop for reduced motion and high contrast, and never hide the actual home content.");
        await Capture("navigation-home-contrast");
        await ApplyPreferences(persisted);

        await page.SetViewportSizeAsync(600, 850);
        await Button("Navigation").ClickAsync();
        await Capture("navigation-groups-compact");
        await OpenEditor();
        await Capture("navigation-editor-compact");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer Mon espace", Exact = true }).PressAsync("ArrowDown");
        var focusRetained = await page.EvaluateAsync<bool>("document.activeElement?.dataset.navigationFocus==='page:day'");
        Require(focusRetained && await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth+1 && document.querySelector('.navigation-modal').scrollWidth<=document.querySelector('.navigation-modal').clientWidth+1")
            && externalRequests == 0 && errors.IsEmpty,
            "The compact editor retains keyboard focus after moving a page, fits the viewport, and runs under the real CSP without external requests or browser errors.");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await Button("Navigation").ClickAsync();
        await page.Locator(".nav-group-toggle[data-nav-group-toggle=workspace]").PressAsync("ArrowLeft");
        await page.Keyboard.PressAsync("Escape");
        await Button("Navigation").ClickAsync();
        var compactFocus = await page.EvaluateAsync<string>("document.activeElement?.dataset.navGroupToggle || document.activeElement?.tagName || 'none'");
        Require(compactFocus == "workspace" && !await page.Locator(".nav-item[data-page=home]").IsVisibleAsync(),
            "Opening compact navigation with the current page folded focuses its visible group heading (actual focus: " + compactFocus + ").");
        await page.Keyboard.PressAsync("Escape");
        await ClickNavigationAsync(page, "populations"); await Ready("populations");
        await Button("Navigation").ClickAsync();
        Require(await page.EvaluateAsync<bool>("document.querySelector('.sidebar').contains(document.activeElement) && document.activeElement.getClientRects().length>0")
            && await page.Locator(".nav-item[data-page=populations]").CountAsync() == 0,
            "Opening compact navigation on a hidden optional page focuses an available control inside the modal sidebar.");
        await page.Keyboard.PressAsync("Escape");
        await delivery;
    }
}
