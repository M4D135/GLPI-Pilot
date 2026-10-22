using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckNavigationInterfaceAsync(string[] args)
    {
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        { Headless = true, ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
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

        await page.GotoAsync("https://glpi-pilot.local/index.html");
        await page.Locator("body[data-ready=true]").WaitForAsync();
        var defaultIds = await page.Locator(".nav-item").EvaluateAllAsync<string[]>("nodes => nodes.map(node=>node.dataset.page)");
        Require(defaultIds.Length == 26 && defaultIds.Distinct().Count() == 26
            && await page.Locator(".nav-section[data-nav-group=collect] .nav-item[data-page=library]").CountAsync() == 1
            && await page.Locator(".nav-section[data-nav-group=workspace] .nav-item[data-page=alerts]").CountAsync() == 1
            && await Button("Personnaliser la navigation").IsVisibleAsync()
            && await page.Locator(".hero").IsVisibleAsync(),
            "The default task-oriented menu presents every registered route once, groups the library and alerts coherently, and always exposes its customization entry.");
        await Capture("navigation-home-light");

        await OpenEditor();
        await Editor().GetByRole(AriaRole.Checkbox, new() { Name = "Afficher Aide", Exact = true }).UncheckAsync();
        await Editor().GetByRole(AriaRole.Combobox, new() { Name = "Groupe de Bibliothèque", Exact = true }).SelectOptionAsync("pinned");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer Ma journée", Exact = true }).PressAsync("ArrowUp");
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
        await Editor().Locator(".navigation-item-row[data-nav-page=library] .navigation-handle")
            .DragToAsync(Editor().Locator(".navigation-item-row[data-nav-page=day]"));
        await Editor().Locator(".navigation-group-card[data-nav-group=workspace] > .navigation-group-heading .navigation-handle")
            .DragToAsync(Editor().Locator(".navigation-group-card[data-nav-group=pinned] > .navigation-group-heading"));
        await SaveEditor(); persisted = AppSettings.Load().Interface;
        Require(!persisted.PinnedNavigation.Contains("library") && persisted.NavigationGroups.GetValueOrDefault("library") == "workspace"
            && persisted.NavigationOrder.IndexOf("library") < persisted.NavigationOrder.IndexOf("day")
            && persisted.NavigationGroupOrder.IndexOf("workspace") < persisted.NavigationGroupOrder.IndexOf("pinned"),
            "Real pointer drag-and-drop rehomes a pinned page before its target and moves a group without duplicating routes.");

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
        await page.Locator(".nav-item[data-page=settings]").ClickAsync(); await Ready("settings");
        await page.Locator(".page-content[data-page=settings]").GetByRole(AriaRole.Button, new() { Name = "Personnaliser la navigation", Exact = true }).ClickAsync();
        var sharedSettingsEditor = await page.Locator("dialog.navigation-modal").IsVisibleAsync()
            && await Editor().Locator(".navigation-item-row").CountAsync() == 26;
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await page.Locator(".nav-item[data-page=home]").ClickAsync(); await Ready("home");
        Require(sharedSettingsEditor && (await page.Locator(".nav-item").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.page)")).SequenceEqual(defaultIds)
            && persisted.NavigationGroups.Count == 0 && persisted.NavigationGroupOrder.Count == 0
            && persisted.PinnedNavigation.Count == 0 && persisted.HiddenNavigation.Count == 0 && persisted.NavigationOrder.Count == 0
            && persisted.Accent == "violet" && persisted.FrozenColumns == 3,
            "Reset restores the default menu without changing other preferences, and Réglages opens the same complete navigation editor.");

        var legacy = persisted.Copy(); legacy.NavigationOrder = ["records", "collect"]; legacy.HiddenNavigation = ["operations"]; legacy.PinnedNavigation = ["tickets"];
        await ApplyPreferences(legacy);
        Require(await page.Locator(".nav-section[data-nav-group=pinned] .nav-item[data-page=tickets]").CountAsync() == 1
            && await page.Locator(".nav-item[data-page=operations]").CountAsync() == 0
            && await page.Locator(".nav-section[data-nav-group=collect] .nav-item").First.GetAttributeAsync("data-page") == "records",
            "Existing flat order, hidden pages and pinned shortcuts are honored without requiring new grouping preferences.");
        await ApplyPreferences(persisted, "dark");
        await Call(app, "app.demo");
        await page.Locator(".nav-item[data-page=dashboard]").ClickAsync(); await Ready("dashboard");
        await Capture("navigation-dashboard-dark");
        await page.Locator(".nav-item[data-page=home]").ClickAsync(); await Ready("home");
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
        await OpenEditor();
        await Capture("navigation-editor-compact");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Déplacer Ma journée", Exact = true }).PressAsync("ArrowDown");
        var focusRetained = await page.EvaluateAsync<bool>("document.activeElement?.dataset.navigationFocus==='page:day'");
        Require(focusRetained && await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth+1 && document.querySelector('.navigation-modal').scrollWidth<=document.querySelector('.navigation-modal').clientWidth+1")
            && externalRequests == 0 && errors.IsEmpty,
            "The compact editor retains keyboard focus after moving a page, fits the viewport, and runs under the real CSP without external requests or browser errors.");
        await Editor().GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await delivery;
    }
}
