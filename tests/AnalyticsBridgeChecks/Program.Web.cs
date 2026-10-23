using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private static async Task CheckWeb(string[] args)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")); var web = Path.Combine(repository, "web");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var external = new ConcurrentQueue<string>(); page.PageError += (_, e) => errors.Enqueue(e);
        page.Console += (_, m) => { if (m.Type == "error") errors.Enqueue(m.Text); };
        var host = new AnalyticsTestHost { Page = page }; var settings = Settings(); var store = new SnapshotStore(Path.Combine(_root, "ui-history"));
        var older = Data(0, Row("1"), Row("2", "En attente", "Bob"), Row("4", person: "Alex"));
        var source = Data(1, Row("3", "Résolu", "Bob"), Row("1", "Résolu", "Bob"), Row("2", person: "Alex"));
        var population = Population(source, settings); new AnalyticsPopulationStore(Path.Combine(AppPaths.DataRoot, "site-populations.json")).Save(new[] { population });
        store.Save(older, settings, default, Locations(older, settings), new[] { population }); store.Save(source, settings, default, Locations(source, settings), new[] { population });
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string message) { lock (gate) delivery = delivery.ContinueWith(async _ => { if (!page.IsClosed) await page.EvaluateAsync("json => window.__pilotDeliver(JSON.parse(json))", message); }, TaskScheduler.Default).Unwrap(); }
        await using var app = new PilotApplication(host, (name, value) => bridge?.Publish(name, value), settings, snapshots: store); app.LoadFixture(source, Locations(source, settings));
        await using var dispatcher = new WebBridge(app.Operations, app.ExecuteAsync, Post); bridge = dispatcher;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (() => { const handlers = new Set(); window.chrome ||= {}; window.chrome.webview = {
              postMessage: message => window.__pilotSend(JSON.stringify(message)),
              addEventListener: (name, listener) => { if (name === 'message') handlers.add(listener); }, removeEventListener: (name, listener) => handlers.delete(listener)
            }; window.__pilotDeliver = message => { for (const listener of handlers) listener({data:message}); }; })();
            """);
        var initial = JsonSerializer.Serialize(new { settings = app.Settings, session = new { loaded = true, revision = app.Revision, rows = source.Rows.Count, complete = true, historical = false, demo = false } }, PilotApplication.Json);
        var harness = """
            import {api} from '/js/bridge.js'; import {pages} from '/js/pages/analytics.js';
            const root=document.querySelector('main'), state=INITIAL; let abort,cleanup;
            window.navigationEvents=[];window.notices=[];
            api.on('session.changed', session=>state.session=session); api.on('settings.changed', event=>state.settings=event.settings);
            window.openAnalytics=async(id,params={})=>{
              if(!pages.some(page=>page.id===id)){window.navigationEvents.push({id,params});return;}
              abort?.abort();cleanup?.();abort=new AbortController();root.replaceChildren();root.dataset.page=id;root.dataset.ready='false';
              try{cleanup=await pages.find(page=>page.id===id).render({root,api,state,params,signal:abort.signal,navigate:window.openAnalytics,refresh:()=>window.openAnalytics(id,params),notify:message=>window.notices.push(message)});root.dataset.ready='true';}
              catch(error){if(error.name!=='AbortError'){root.textContent=error.message;console.error(error.message);}}
            };
            await window.openAnalytics('dashboard');
            """.Replace("INITIAL", initial, StringComparison.Ordinal);
        const string html = """
            <!doctype html><html lang="fr" data-theme="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'none'; base-uri 'none'; form-action 'none'; object-src 'none'">
            <link rel="stylesheet" href="/css/app.css"><link rel="stylesheet" href="/css/analytics.css"><title>Analytics integration checks</title></head>
            <body><main class="page-content"></main><script type="module" src="/analytics-check.js"></script></body></html>
            """;
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url); if (uri.Host != "glpi-pilot.local") { external.Enqueue(uri.Host); await route.AbortAsync(); return; }
            if (uri.AbsolutePath == "/analytics-check.html") { await route.FulfillAsync(new() { ContentType = "text/html", Body = html }); return; }
            if (uri.AbsolutePath == "/analytics-check.js") { await route.FulfillAsync(new() { ContentType = "text/javascript", Body = harness }); return; }
            var path = Path.GetFullPath(Path.Combine(web, Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/')));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404, Body = "absent" }); return; }
            await route.FulfillAsync(new() { ContentType = Path.GetExtension(path) switch { ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" }, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        async Task Until(string expression, object? argument = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!await page.EvaluateAsync<bool>(expression, argument)) await Task.Delay(50, deadline.Token);
        }
        async Task Ready(string id)
        {
            try { await Until("id => document.querySelector('main').dataset.page===id && document.querySelector('main').dataset.ready==='true'", id); }
            catch { Console.WriteLine("UI errors: " + string.Join(" | ", errors)); Console.WriteLine("UI text: " + (await page.Locator("body").InnerTextAsync())[..Math.Min(5000, (await page.Locator("body").InnerTextAsync()).Length)]); if (delivery.IsFaulted) Console.WriteLine(delivery.Exception); throw; }
        }
        async Task Open(string id, object? parameters = null) { await page.EvaluateAsync("x => window.openAnalytics(x.id,x.params)", new { id, @params = parameters ?? new { } }); await Ready(id); }
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        async Task Drag(ILocator sourceHandle, ILocator destination)
        {
            // Start the real native drag before scrolling to a distant section. A single
            // DragTo jump across a scroll emits no dragstart in Chromium.
            await sourceHandle.ScrollIntoViewIfNeededAsync(); var from = (await sourceHandle.BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(from.X + from.Width / 2, from.Y + from.Height / 2); await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(from.X + from.Width / 2 + 15, from.Y + from.Height / 2 + 15, new() { Steps = 4 });
            await destination.ScrollIntoViewIfNeededAsync(); var to = (await destination.BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(to.X + to.Width / 2, to.Y + to.Height / 2, new() { Steps = 6 });
            await page.Mouse.MoveAsync(to.X + to.Width / 2 + 1, to.Y + to.Height / 2 + 1); await page.Mouse.UpAsync();
        }
        async Task CloseDialogs() { while (await page.GetByRole(AriaRole.Dialog).CountAsync() > 0) await page.Keyboard.PressAsync("Escape"); }
        await page.GotoAsync("https://glpi-pilot.local/analytics-check.html"); await Ready("dashboard");
        await Until("document.querySelectorAll('.analytics-mini-history svg').length>0");
        Check(await page.Locator(".analytics-widget").CountAsync() == 5 && await page.Locator("[data-widget-id=map] svg").CountAsync() >= 1, "The real WebView dashboard renders metrics, comparable mini-curves, map and population widgets.");
        Check(await page.Locator(".analytics-column-chart polyline").CountAsync() == 1 && await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.analytics-widget')).gridColumnStart") != "auto", "Pareto renders a real cumulative curve and CSSOM spans work under strict CSP.");
        await page.GetByLabel("Périmètre personnel", new() { Exact = true }).SelectOptionAsync(TechnicianDashboardService.IdentityKey("Alex")); await Ready("dashboard");
        await Until("document.querySelector('.analytics-history-status').textContent.includes('recalculés')");
        Check(await page.Locator("[data-widget-id=total] .analytics-metric-value").TextContentAsync() == "1", "Selecting a technician rerenders a computed personal dashboard.");
        await Button("Modifier le dashboard").ClickAsync(); await page.Locator(".analytics-widget-controls").First.WaitForAsync();
        Check(await page.Locator(".analytics-widget [data-drag-kind=widget][draggable=true]").CountAsync() == 5, "Editor mode enables dedicated widget handles while keeping charts and text selectable.");
        await Button("+ Widget").ClickAsync();
        var availableOnly = page.GetByLabel("Disponibles seulement", new() { Exact = true });
        var initiallyAvailable = await availableOnly.IsCheckedAsync() && await page.GetByRole(AriaRole.Dialog).GetByText("source indisponible", new() { Exact = false }).CountAsync() == 0;
        await availableOnly.UncheckAsync();
        Check(initiallyAvailable && await page.GetByRole(AriaRole.Dialog).GetByText("source indisponible", new() { Exact = false }).CountAsync() > 0, "The widget catalogue filters by actual C# availability and exposes unavailable sources on request."); await CloseDialogs();
        await page.Locator("[data-widget-id=total]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await page.GetByLabel("Titre facultatif", new() { Exact = true }).FillAsync("Indicateur personnalisé"); await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready("dashboard");
        Check(app.Settings.Dashboard.PersonalLayout.Boards[0].Widgets.Single(w => w.Id == "total").Title == "Indicateur personnalisé", "Widget editor changes the persisted personal layout through C# validation.");
        await page.GetByText("Autres actions", new() { Exact = true }).ClickAsync(); await Button("Annuler la modification").ClickAsync(); await Ready("dashboard"); Check(app.Settings.Dashboard.PersonalLayout.Boards[0].Widgets.Single(w => w.Id == "total").Title == "Volume observé", "The WebView undo action restores the previous widget definition.");
        await Open("dashboard"); await Button("Exporter").ClickAsync(); await page.GetByLabel("Format", new() { Exact = true }).SelectOptionAsync("pdf"); host.SavePath = Path.Combine(_root, "dashboard-web.pdf");
        await Button("Choisir le fichier et exporter").ClickAsync(); await Until("window.notices.some(text=>text.includes('dashboard-web.pdf'))");
        Check(File.ReadAllBytes(host.SavePath).AsSpan(0, 5).SequenceEqual("%PDF-"u8) && await page.GetByRole(AriaRole.Dialog).CountAsync() == 0, "The visible dashboard exports a valid Chromium PDF after closing its export dialog.");
        await Button("Exporter").ClickAsync(); await page.GetByLabel("Format", new() { Exact = true }).SelectOptionAsync("png"); host.SavePath = Path.Combine(_root, "dashboard-web.png");
        await Button("Choisir le fichier et exporter").ClickAsync(); await Until("window.notices.some(text=>text.includes('dashboard-web.png'))");
        Check(File.ReadAllBytes(host.SavePath).AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) && await page.Locator(".analytics-provenance").IsVisibleAsync(), "PNG capture is real and includes the dated dashboard cartouche.");
        await Button("Modifier le dashboard").ClickAsync();
        await page.Locator("[data-widget-id=status]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await page.GetByLabel("Type", new() { Exact = true }).SelectOptionAsync("matrix");
        await page.GetByLabel("Source", new() { Exact = true }).SelectOptionAsync("category-status");
        await page.GetByLabel("Présentation", new() { Exact = true }).SelectOptionAsync("heatmap");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready("dashboard");
        var matrix = page.Locator("[data-widget-id=status]");
        await matrix.Locator(".analytics-heat-cell").First.WaitForAsync();
        Check(await matrix.Locator("thead th").CountAsync() == 4 && await matrix.Locator("thead th").Last.TextContentAsync() == "Total"
            && await matrix.Locator(".analytics-heat-cell").AllTextContentsAsync() is var cells && cells.Order().SequenceEqual(new[] { "1", "2" }), "The two-axis heatmap displays the actual category/status matrix, including both observed counts and the row total.");
        await matrix.GetByRole(AriaRole.Button, new() { Name = "2", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).Locator("tbody tr").First.WaitForAsync();
        Check(await page.GetByRole(AriaRole.Dialog).Locator("tbody tr").CountAsync() == 2, "Selecting a matrix cell resolves its two original source rows."); await CloseDialogs();
        await matrix.GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await page.GetByLabel("Présentation", new() { Exact = true }).SelectOptionAsync("stacked");
        await page.GetByLabel("Empilage", new() { Exact = true }).SelectOptionAsync("percent");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready("dashboard");
        await page.Locator("[data-widget-id=status] .analytics-stack-row").First.WaitForAsync();
        Check(await page.EvaluateAsync<bool>("(() => { const row=document.querySelector('[data-widget-id=status] .analytics-stack-row'); const widths=[...row.querySelectorAll('.analytics-stack > button')].map(item=>parseFloat(item.style.width)).sort((a,b)=>a-b); return widths.length===2 && Math.abs(widths[0]-100/3)<.001 && Math.abs(widths[1]-200/3)<.001 && row.querySelector('strong').textContent.startsWith('3'); })()"), "Percentage stacks use the C# row denominator and preserve the exact count beside the chart.");
        await page.GetByText("Autres actions", new() { Exact = true }).ClickAsync(); await Button("Annuler la modification").ClickAsync(); await Ready("dashboard");
        await page.Locator("[data-widget-id=status] .analytics-heat-cell").First.WaitForAsync();
        await page.GetByText("Autres actions", new() { Exact = true }).ClickAsync(); await Button("Annuler la modification").ClickAsync(); await Ready("dashboard");
        await page.Locator("[data-widget-id=status] .analytics-column-chart polyline").WaitForAsync();
        await Open("queries");
        await page.GetByLabel("Nom", new() { Exact = true }).FillAsync("Actifs de contrôle");
        await page.GetByLabel("Population de départ", new() { Exact = true }).SelectOptionAsync("active");
        await Button("Prévisualiser").ClickAsync(); await page.GetByText("Prévisualisation calculée", new() { Exact = true }).WaitForAsync();
        Check(await page.GetByText("1 lignes retenues", new() { Exact = false }).CountAsync() == 1 && app.Settings.Queries.Queries.Count == 0, "The query form computes a real preview without implicitly saving it.");
        await Button("Enregistrer").ClickAsync(); await Ready("queries"); Check(app.Settings.Queries.Queries.Any(q => q.Name == "Actifs de contrôle"), "Explicit query save persists the form definition.");
        await Open("history"); await Button("Comparer").First.ClickAsync(); await Button("Comparer").Last.ClickAsync();
        await page.GetByText("Relevés comparables", new() { Exact = true }).WaitForAsync();
        Check(await page.GetByText("Absent du relevé", new() { Exact = true }).CountAsync() > 0, "The history view displays actual changes and explicitly labelled absences.");
        await Button("Analyses historiques").ClickAsync(); await Button("Analyser").ClickAsync(); await page.GetByText("Analyse terminée.", new() { Exact = true }).WaitForAsync();
        await page.GetByLabel("Analyse", new() { Exact = true }).SelectOptionAsync("calendar"); await page.Locator(".analytics-activity-calendar").WaitForAsync();
        Check(await page.Locator(".analytics-activity-day").CountAsync() == 2, "The activity calendar renders observed collection days from the history engine.");
        await Button("Historique d’un ticket").ClickAsync(); await page.GetByLabel("Identifiant", new() { Exact = true }).FillAsync("1"); await Button("Rechercher les observations").ClickAsync();
        await page.GetByText("2 observations indexées.", new() { Exact = false }).WaitForAsync(); Check(true, "The ticket-history selector is populated and uses the chosen archive context.");
        await Open("populations"); await Button("+ Période d’effectif").ClickAsync();
        Check(await page.GetByRole(AriaRole.Dialog).GetByText("Unité", new() { Exact = true }).CountAsync() == 1, "Dated population editing exposes the real unit and date interval form."); await CloseDialogs();
        await Open("assistant");
        await page.GetByLabel("Votre demande", new() { Exact = true }).FillAsync("tickets actifs par catégorie");
        var queryCount = app.Settings.Queries.Queries.Count; await Button("Préparer et voir le contenu exact").ClickAsync();
        await page.GetByText("Contenu préparé · pas encore envoyé", new() { Exact = true }).WaitForAsync();
        Check(await page.Locator(".analytics-prepared-content").CountAsync() > 0 && app.Settings.Queries.Queries.Count == queryCount, "Assistant preparation displays the exact payload without executing a query.");
        await Button("Générer depuis cette préparation").ClickAsync(); await page.GetByText("Proposition de requête · brouillon", new() { Exact = true }).WaitForAsync();
        Check(app.Settings.Queries.Queries.Count == queryCount, "Local generation leaves the saved library unchanged in the real UI flow.");
        await Button("Prévisualiser les résultats").ClickAsync(); await page.GetByText("Prévisualisation disponible", new() { Exact = true }).WaitForAsync();
        await Button("Ouvrir le brouillon dans l’atelier").ClickAsync(); await Ready("queries");
        Check(app.Settings.Queries.Queries.Count == queryCount && await page.GetByText("Brouillon", new() { Exact = false }).CountAsync() > 0, "Opening an assistant proposal enters the ordinary query editor as an unsaved draft.");
        // Exercise presentation operations through the rendered controls and the real C# bridge.
        var layoutBeforeEditing = app.Settings.Dashboard.Layout.Copy();
        await Open("dashboard", new { editing = true });
        await Button("+ Section").ClickAsync();
        await page.GetByLabel("Titre de la section", new() { Exact = true }).FillAsync("Interventions");
        await page.GetByLabel("Description de la section", new() { Exact = true }).FillAsync("Organisation du jour");
        await page.GetByLabel("Couleur de la section", new() { Exact = true }).SelectOptionAsync("teal");
        await Button("Enregistrer la section").ClickAsync(); await Ready("dashboard"); await CloseDialogs();
        var sectionKey = app.Settings.Dashboard.Layout.Boards[0].Sections.Single(section => section.Title == "Interventions").Key;
        ILocator Section() => page.Locator("[data-section-key='" + sectionKey + "']");
        Check(await Section().Locator(".analytics-drop-zone").CountAsync() == 1 && app.Settings.Dashboard.Layout.Boards[0].Sections.Single(section => section.Key == sectionKey).Description == "Organisation du jour",
            "An empty named section with description and accent is persisted before receiving any widget.");
        await page.EvaluateAsync("window.editorDragEvents=[];for(const type of ['dragstart','dragover','drop','dragend'])document.addEventListener(type,event=>{if(window.editorDragEvents.length<30)window.editorDragEvents.push([type,event.target.tagName,event.target.closest('[data-section-key]')?.dataset.sectionKey,event.defaultPrevented]);});");
        await Drag(page.Locator("[data-widget-id=total] [data-drag-kind=widget]"), Section().Locator(".analytics-drop-zone"));
        try { await Until("key => document.querySelector('[data-widget-id=total]')?.closest('[data-section-key]')?.dataset.sectionKey===key && document.querySelector('main').dataset.ready==='true'", sectionKey); }
        catch { Console.WriteLine("EDITOR DRAG " + await page.EvaluateAsync<string>("JSON.stringify({events:window.editorDragEvents,errors:[...document.querySelectorAll('[role=alert]')].map(node=>node.textContent),status:document.querySelector('.analytics-edit-status')?.textContent,sections:[...document.querySelectorAll('[data-section-key]')].map(node=>[node.dataset.sectionKey,node.getBoundingClientRect().toJSON()])})")); throw; }
        Check(app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Section == sectionKey && app.Settings.Dashboard.Layout.Boards[0].Widgets.Count == 5,
            "A native drag transfers the exact widget into an empty section without losing or duplicating any widget.");
        var totalWidget = page.Locator("[data-widget-id=total]"); var resizeHandle = totalWidget.Locator("[data-resize-widget]");
        await resizeHandle.ScrollIntoViewIfNeededAsync(); var resizeBox = (await resizeHandle.BoundingBoxAsync())!;
        var gridBox = (await totalWidget.Locator("..").BoundingBoxAsync())!;
        var beforeSize = app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Copy();
        await page.Mouse.MoveAsync(resizeBox.X + resizeBox.Width / 2, resizeBox.Y + resizeBox.Height / 2); await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(resizeBox.X + resizeBox.Width / 2 + gridBox.Width / 12, resizeBox.Y + resizeBox.Height / 2 + 60, new() { Steps = 6 }); await page.Mouse.UpAsync();
        await Ready("dashboard");
        var afterSize = app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Copy();
        Check(afterSize.Span == beforeSize.Span + 1 && !afterSize.AutoHeight && afterSize.Height is >= 160 and <= 960 && await page.GetByRole(AriaRole.Dialog).CountAsync() == 0,
            "Pointer resizing snaps the real card to twelve columns, persists manual height and does not open a dialog after dragging.");
        await totalWidget.Locator("[data-resize-widget]").FocusAsync(); await page.Keyboard.PressAsync("ArrowDown"); await Ready("dashboard");
        Check(app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Height == Math.Min(960, afterSize.Height + 20)
            && await totalWidget.Locator("[data-resize-widget]").EvaluateAsync<bool>("node=>node===document.activeElement"),
            "Keyboard resizing persists one twenty-pixel step and restores focus to the same widget handle.");
        var beforeCancel = app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Copy();
        resizeHandle = totalWidget.Locator("[data-resize-widget]"); await resizeHandle.ScrollIntoViewIfNeededAsync(); resizeBox = (await resizeHandle.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(resizeBox.X + 8, resizeBox.Y + 8); await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(resizeBox.X + 80, resizeBox.Y + 100, new() { Steps = 4 }); await page.Keyboard.PressAsync("Escape"); await page.Mouse.UpAsync();
        Check(app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Height == beforeCancel.Height
            && await totalWidget.EvaluateAsync<string>("node=>node.style.getPropertyValue('--widget-height')") == beforeCancel.Height + "px",
            "Escape cancels a resize preview and leaves the saved dimensions intact.");
        await totalWidget.GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByLabel("Hauteur", new() { Exact = true }).FillAsync("1001");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).Locator(".notice[role=alert]").WaitForAsync(); await Ready("dashboard");
        await page.GetByRole(AriaRole.Dialog).GetByLabel("Hauteur", new() { Exact = true }).FillAsync("280");
        await page.GetByLabel("Titre facultatif", new() { Exact = true }).FillAsync("Volume ajusté");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready("dashboard"); await CloseDialogs();
        Check(app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Height == 280
            && app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Title == "Volume ajusté" && app.Settings.Dashboard.Layout.Boards[0].Widgets.Count == 5,
            "A rejected save can be corrected in the same dialog and still addresses the original widget by identity.");
        await Section().GetByRole(AriaRole.Button, new() { Name = "Régler la section", Exact = true }).ClickAsync();
        await page.GetByLabel("Titre de la section", new() { Exact = true }).FillAsync("Mon espace terrain");
        await Button("Enregistrer la section").ClickAsync(); await Ready("dashboard"); await CloseDialogs();
        Check(app.Settings.Dashboard.Layout.Boards[0].Sections.Single(section => section.Key == sectionKey).Title == "Mon espace terrain"
            && app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Section == sectionKey,
            "Renaming a section preserves its identity and every widget association.");
        await Drag(Section().Locator("[data-drag-kind=section]"), page.Locator("[data-section-key=''] .analytics-section-heading"));
        await Ready("dashboard");
        Check(app.Settings.Dashboard.Layout.Boards[0].Sections[0].Key == sectionKey
            && await page.Locator(".analytics-board-section").First.GetAttributeAsync("data-section-key") == sectionKey,
            "A section drag changes the persisted section order together with its contents.");
        await totalWidget.GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await page.GetByLabel("Verrouiller déplacement et actions groupées", new() { Exact = true }).CheckAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready("dashboard"); await CloseDialogs();
        Check(await totalWidget.Locator("[data-drag-kind=widget]").IsDisabledAsync() && await totalWidget.Locator("[data-resize-widget]").CountAsync() == 0,
            "Locking a widget disables movement and removes its resize handle without hiding its settings.");
        await Section().GetByRole(AriaRole.Button, new() { Name = "Régler la section", Exact = true }).ClickAsync();
        await page.GetByLabel("Masquer cette section et ses widgets en lecture", new() { Exact = true }).CheckAsync();
        await Button("Enregistrer la section").ClickAsync(); await Ready("dashboard"); await CloseDialogs();
        await Button("Quitter l’édition").ClickAsync();
        Check(await Section().CountAsync() == 0 && app.Settings.Dashboard.Layout.Boards[0].Widgets.Count == 5 && !app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "total").Hidden,
            "Hiding a section affects reading only and preserves the independent visibility and content of its widgets.");
        await Open("dashboard", new { editing = true });
        Check(await Section().Locator(".analytics-widget").CountAsync() == 1 && await Section().GetByText("Section masquée", new() { Exact = true }).CountAsync() == 1,
            "Reloading retains section order, title and hidden state while allowing the hidden section to be edited.");
        await page.Locator("[data-widget-id=filtered]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByLabel("Hauteur", new() { Exact = true }).FillAsync("1001");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).Locator(".notice[role=alert]").WaitForAsync(); await Ready("dashboard");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Supprimer", Exact = true }).ClickAsync(); await Ready("dashboard"); await CloseDialogs();
        Check(app.Settings.Dashboard.Layout.Boards[0].Widgets.All(widget => widget.Id != "filtered") && app.Settings.Dashboard.Layout.Boards[0].Widgets.Any(widget => widget.Id == "population"),
            "Deleting after a rejected save removes the chosen widget and never the last unrelated widget.");
        await Call(app, "dashboard.layout.save", new { personal = false, layout = layoutBeforeEditing });
        var receivedWithDifferences = Data(1, source.Rows.Select(row => row.ToArray()).ToArray()); receivedWithDifferences.Complete = false;
        receivedWithDifferences.Quality = new() { SourceRows = 3, ExpectedRows = 3, AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = true, C1ControlCount = 2, UnusualControlCount = 2,
            InitialSample = new() { IdentityVerified = true, RepresentationVerified = false, SampleSize = 3, ComparedCells = 6, DifferentCells = 1 },
            FinalSample = new() { IdentityVerified = true, RepresentationVerified = false, SampleSize = 3, ComparedCells = 6, DifferentCells = 1 } };
        receivedWithDifferences.Warnings.Add("Deux contrôles C1 conservés dans le fichier natif synthétique.");
        app.LoadFixture(receivedWithDifferences); await Open("dashboard");
        var controlsInitiallyQuiet = !(await page.Locator("body").InnerTextAsync()).Contains(receivedWithDifferences.Warnings[0])
            && await page.Locator(".analytics-reception-issue:visible").CountAsync() == 0;
        await Button("Détails des contrôles").ClickAsync();
        Check(controlsInitiallyQuiet && await page.GetByRole(AriaRole.Dialog).GetByText("1 / 6 à vérifier", new() { Exact = true }).CountAsync() == 1
            && await page.GetByRole(AriaRole.Dialog).GetByText("3 / 3 rapprochés", new() { Exact = true }).CountAsync() == 1 && !app.Result!.Complete,
            "Dashboard presentation differences stay in an explicit detail dialog while receipt and sample evidence remain intact without certifying the snapshot.");
        Check(await page.GetByRole(AriaRole.Dialog).GetByText("Deux contrôles C1 conservés dans le fichier natif synthétique.", new() { Exact = true }).CountAsync() == 1,
            "The control dialog preserves a source warning once instead of repeating it across the dashboard.");
        await CloseDialogs();
        receivedWithDifferences.Quality.ExpectedRows = 4; receivedWithDifferences.ExpectedTotal = 4; receivedWithDifferences.Quality.RowCountVerified = false;
        app.LoadFixture(receivedWithDifferences); await Open("dashboard");
        Check(await page.GetByText("Réception partielle ou non confirmée", new() { Exact = true }).CountAsync() == 1 && await page.GetByText("Réception complète", new() { Exact = true }).CountAsync() == 0,
            "A real receipt deficit remains visibly partial even when every sampled identifier is present.");

        var limited = Data(1, Row("11", "Résolu"), Row("12", "Résolu"));
        limited.Headers = limited.Headers.Take(3).ToList(); limited.Rows = limited.Rows.Select(row => row.Take(3).ToArray()).ToList();
        var availabilityBoard = DashboardLayoutSettings.CreateBoard("Disponibilité synthétique", "empty");
        availabilityBoard.Widgets.Add(new() { Id = "valid-zero", Kind = "metric", SourceKey = "active", Title = "Actifs observés", ShowMiniHistory = false });
        availabilityBoard.Widgets.Add(new() { Id = "missing-metric", Kind = "metric", SourceKey = "overdue", Title = "Échéances indisponibles", ShowMiniHistory = false });
        availabilityBoard.Widgets.Add(new() { Id = "missing-distribution", Kind = "distribution", SourceKey = "priority", Title = "Priorités indisponibles" });
        availabilityBoard.Widgets.Add(new() { Id = "compact-quality", Kind = "quality", Title = "Qualité du périmètre", Span = 12 });
        var availabilityLayout = new DashboardLayoutSettings { Boards = new() { availabilityBoard }, ActiveBoardId = availabilityBoard.Id };
        app.LoadFixture(limited); await Call(app, "dashboard.layout.save", new { personal = false, layout = availabilityLayout });
        var availability = await Call(app, "dashboard.get"); await Open("dashboard");
        Check(Widget(availability, "valid-zero").GetProperty("available").GetBoolean()
            && !Widget(availability, "missing-metric").GetProperty("available").GetBoolean()
            && !Widget(availability, "missing-distribution").GetProperty("available").GetBoolean()
            && Str(Widget(availability, "missing-metric"), "unavailableReason").Length > 0
            && await page.Locator("[data-widget-id=valid-zero] .analytics-metric-value").TextContentAsync() == "0"
            && await page.Locator("[data-widget-id=missing-metric],[data-widget-id=missing-distribution]").CountAsync() == 0
            && app.Settings.Dashboard.Layout.Boards[0].HideUnavailableWidgets && app.Settings.Dashboard.Layout.Boards[0].Widgets.All(widget => !widget.Hidden),
            "Reading hides genuinely unavailable metrics and distributions without hiding a measured zero or mutating saved widget visibility.");
        var availabilityCapture = Array.IndexOf(args, "--screenshots");
        if (availabilityCapture >= 0 && availabilityCapture + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[availabilityCapture + 1]); Directory.CreateDirectory(output);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-dashboard-availability.png"), FullPage = true });
        }
        var compactQuality = page.Locator("[data-widget-id=compact-quality]");
        var qualityInitiallyCompact = await compactQuality.Locator(".analytics-quality-overview").IsVisibleAsync()
            && await compactQuality.Locator(".analytics-quality-optional").CountAsync() > 0
            && await compactQuality.Locator(".analytics-quality-optional[open]").CountAsync() == 0;
        await compactQuality.Locator(".analytics-quality-optional > summary").ClickAsync();
        var missingColumn = Widget(availability, "compact-quality").GetProperty("content").GetProperty("columns").EnumerateArray().Single(column => Str(column, "role") == "priority");
        Check(qualityInitiallyCompact && Str(missingColumn, "state") == "missing" && Str(missingColumn, "stateLabel") == "Aucune colonne reconnue"
            && (await compactQuality.InnerTextAsync()).Contains("Aucune colonne reconnue")
            && !System.Text.RegularExpressions.Regex.IsMatch(await compactQuality.InnerTextAsync(), @"\b(missing|auto|mapped|ignored)\b"),
            "Quality stays compact until its optional roles are opened and presents French correspondence labels while preserving stable machine states.");
        await Button("Modifier le dashboard").ClickAsync();
        Check(await page.Locator("[data-widget-id=missing-metric][data-available=false]").CountAsync() == 1
            && await page.Locator("[data-widget-id=missing-distribution][data-available=false]").CountAsync() == 1
            && await page.Locator("[data-widget-id=missing-metric]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).IsEnabledAsync(),
            "Editor mode reveals automatically hidden widgets with their controls so their source can still be corrected.");
        await Button("Quitter l’édition").ClickAsync();
        await page.GetByLabel("Afficher les widgets indisponibles", new() { Exact = true }).CheckAsync(); await Ready("dashboard");
        await Open("dashboard");
        Check(await page.GetByLabel("Afficher les widgets indisponibles", new() { Exact = true }).IsCheckedAsync()
            && await page.Locator("[data-widget-id=missing-metric],[data-widget-id=missing-distribution]").CountAsync() == 2
            && !AppSettings.Load().Dashboard.Layout.Boards[0].HideUnavailableWidgets
            && await page.Locator("[data-widget-id=valid-zero] .analytics-metric-value").TextContentAsync() == "0",
            "The explicit show-unavailable option persists through the real settings store and reloading without changing valid zero values.");
        if (availabilityCapture >= 0 && availabilityCapture + 1 < args.Length)
            await page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetFullPath(args[availabilityCapture + 1]), "analytics-dashboard-show-unavailable.png"), FullPage = true });
        await page.GetByText("Autres actions", new() { Exact = true }).ClickAsync(); await Button("Annuler la modification").ClickAsync(); await Ready("dashboard");
        await Until("document.querySelector('main').dataset.ready==='true' && !document.querySelector('[data-widget-id=missing-metric]') && !document.querySelector('[data-widget-id=missing-distribution]')");
        Check(app.Settings.Dashboard.Layout.Boards[0].HideUnavailableWidgets
            && await page.Locator("[data-widget-id=missing-metric],[data-widget-id=missing-distribution]").CountAsync() == 0
            && app.Settings.Dashboard.Layout.Boards[0].Widgets.Count == 4,
            "Undo restores automatic hiding while preserving all widget definitions for later recovery.");
        var weatherBoard = DashboardLayoutSettings.CreateBoard("Météo de contrôle", "empty");
        foreach (var (id, warning, critical) in new (string, double?, double?)[] { ("weather-sun", 1, 2), ("weather-watch", 0, 1), ("weather-rain", 0, 0), ("weather-empty", null, null) })
            weatherBoard.Widgets.Add(new() { Id = id, Kind = "metric", SourceKey = "active", Presentation = "weather-volume", WarningValue = warning, CriticalValue = critical, ShowMiniHistory = false, Span = 3 });
        var weatherLayout = new DashboardLayoutSettings { Boards = new() { weatherBoard }, ActiveBoardId = weatherBoard.Id };
        app.LoadFixture(source, Locations(source, settings)); await Call(app, "dashboard.layout.save", new { personal = false, layout = weatherLayout }); await Open("dashboard");
        Check(await page.Locator("[data-widget-id=weather-sun] .analytics-volume-weather[data-state=sun]").CountAsync() == 1
            && await page.Locator("[data-widget-id=weather-watch] .analytics-volume-weather-status").TextContentAsync() == "À surveiller"
            && await page.Locator("[data-widget-id=weather-rain] .analytics-volume-weather-status").TextContentAsync() == "Sous tension"
            && await page.Locator("[data-widget-id=weather-empty] .analytics-volume-weather-status").TextContentAsync() == "Seuils à définir",
            "Volume weather renders the C# strict threshold states while an unconfigured card remains neutral.");
        Check(await page.EvaluateAsync<bool>("[...document.querySelectorAll('.analytics-volume-weather-icon *')].every(node=>getComputedStyle(node).animationName==='none')"),
            "Weather illustrations respect reduced motion for all sun cloud and rain components.");
        await page.Locator("[data-widget-id=weather-sun] .analytics-metric-value").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).Locator("tbody tr").First.WaitForAsync();
        Check(await page.GetByRole(AriaRole.Dialog).Locator("tbody tr").CountAsync() == 1,
            "A volume weather count opens the real active contributors and excludes resolved source rows."); await CloseDialogs();
        await Button("Modifier le dashboard").ClickAsync(); await page.Locator("[data-widget-id=weather-empty]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        Check(await page.GetByLabel("Présentation", new() { Exact = true }).InputValueAsync() == "weather-volume"
            && await page.GetByRole(AriaRole.Dialog).GetByText("strictement au-delà", new() { Exact = false }).CountAsync() == 1,
            "The widget editor exposes the named volume weather presentation and its strict comparison rule.");
        await page.GetByLabel("Seuil de vigilance", new() { Exact = true }).FillAsync("1"); await page.GetByLabel("Seuil critique", new() { Exact = true }).FillAsync("2");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready("dashboard");
        Check(await page.Locator("[data-widget-id=weather-empty] .analytics-volume-weather-status").TextContentAsync() == "Maîtrisé"
            && AppSettings.Load().Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "weather-empty").WarningValue == 1,
            "Editing missing weather thresholds persists the card and recalculates its C# state.");
        host.SavePath = Path.Combine(_root, "weather-layout.json"); await Call(app, "dashboard.layout.export", new { personal = false }); host.OpenPath = host.SavePath;
        var weatherImport = await Call(app, "dashboard.layout.import", new { personal = false });
        var importedCard = weatherImport.GetProperty("layout").GetProperty("boards")[0].GetProperty("widgets")[0];
        Check(Str(importedCard, "presentation") == "weather-volume" && importedCard.GetProperty("warningValue").GetDouble() == 1
            && importedCard.GetProperty("criticalValue").GetDouble() == 2, "The real layout export and import preserve weather presentations and per-card thresholds.");
        if (availabilityCapture >= 0 && availabilityCapture + 1 < args.Length)
        {
            await Button("Quitter l’édition").ClickAsync();
            var output = Path.GetFullPath(args[availabilityCapture + 1]);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-volume-weather-dark.png"), FullPage = true });
            await page.EvaluateAsync("document.documentElement.dataset.theme='light'");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-volume-weather-light.png"), FullPage = true });
            await page.EvaluateAsync("document.documentElement.dataset.theme='dark'");
        }
        var partialWeather = Data(1, Row("2")); partialWeather.Complete = false; partialWeather.ExpectedTotal = 2; app.LoadFixture(partialWeather); await Open("dashboard");
        Check(await page.Locator(".analytics-volume-weather[data-state=partial]").CountAsync() == 4 && await page.Locator(".analytics-volume-weather-status").First.TextContentAsync() == "Relevé à vérifier",
            "An incomplete source retains its measured volume without claiming a healthy weather state.");
        var contentDifferenceWeather = Data(1, source.Rows.Select(row => row.ToArray()).ToArray()); contentDifferenceWeather.Complete = false;
        contentDifferenceWeather.Quality = new() { SourceRows = 3, ExpectedRows = 3, AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = true, ScopeStable = true,
            InitialSample = new() { IdentityVerified = true, RepresentationVerified = false }, FinalSample = new() { IdentityVerified = true, RepresentationVerified = false } };
        app.LoadFixture(contentDifferenceWeather); await Open("dashboard");
        Check(await page.Locator("[data-widget-id=weather-sun] .analytics-volume-weather[data-state=sun]").CountAsync() == 1 && !app.Result!.Complete,
            "Cell representation differences preserve a reliable volume weather when receipt identity and scope are confirmed without certifying source values.");
        contentDifferenceWeather.Quality.ScopeStable = false; app.LoadFixture(contentDifferenceWeather); await Open("dashboard");
        Check(await page.Locator(".analytics-volume-weather[data-state=partial]").CountAsync() == 4,
            "A changed collection scope prevents a weather health claim even when the transferred row count is complete.");
        var weatherBeforeAppend = JsonSerializer.Serialize(app.Settings.Dashboard.Layout.Boards[0], PilotApplication.Json);
        await Open("dashboard", new { editing = true });
        await page.Locator(".analytics-editor-toolbar").GetByText("Disposition", new() { Exact = true }).ClickAsync();
        await Button("Importer une disposition").ClickAsync();
        var importDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Vérifier la disposition importée", Exact = true });
        await importDialog.WaitForAsync();
        Check(await importDialog.Locator(".modal-actions .primary").TextContentAsync() == "Ajouter à mes tableaux"
            && await importDialog.GetByText("Météo de contrôle (importé)", new() { Exact = true }).CountAsync() == 1
            && app.Settings.Dashboard.Layout.Boards.Count == 1,
            "The import dialog recommends addition, previews renamed collisions and preserves the layout until confirmation.");
        await importDialog.GetByRole(AriaRole.Button, new() { Name = "Ajouter à mes tableaux", Exact = true }).ClickAsync();
        await Until("document.querySelector('main').dataset.ready==='true' && [...document.querySelectorAll('select option')].some(option=>option.textContent==='Météo de contrôle (importé)')");
        Check(app.Settings.Dashboard.Layout.Boards.Count == 2
            && JsonSerializer.Serialize(app.Settings.Dashboard.Layout.Boards[0], PilotApplication.Json) == weatherBeforeAppend
            && app.Settings.Dashboard.Layout.Boards[1].Widgets.Count == 4,
            "The real WebView append action keeps the original board and imports the complete additional weather board.");
        await page.GetByText("Autres actions", new() { Exact = true }).ClickAsync(); await Button("Annuler la modification").ClickAsync();
        await Until("document.querySelector('main').dataset.ready==='true' && ![...document.querySelectorAll('select option')].some(option=>option.textContent==='Météo de contrôle (importé)')");
        Check(app.Settings.Dashboard.Layout.Boards.Count == 1
            && JsonSerializer.Serialize(app.Settings.Dashboard.Layout.Boards[0], PilotApplication.Json) == weatherBeforeAppend,
            "WebView undo removes only the appended boards and restores the previous active dashboard.");
        await page.GetByText("Autres actions", new() { Exact = true }).ClickAsync(); await Button("+ Tableau de bord").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByLabel("Modèle", new() { Exact = true }).SelectOptionAsync("service");
        await page.GetByRole(AriaRole.Dialog).GetByLabel("Nom", new() { Exact = true }).FillAsync("Pilotage de contrôle");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Créer", Exact = true }).ClickAsync();
        await Until("document.querySelector('main').dataset.ready==='true' && document.querySelectorAll('.analytics-widget').length===14");
        Check(await page.Locator(".analytics-board-section").CountAsync() == 5
            && await page.Locator(".analytics-volume-weather-status").TextContentAsync() == "Seuils à définir"
            && app.Settings.Dashboard.Layout.Boards.Count == 2
            && app.Settings.Dashboard.Layout.Boards[1].Widgets.Count == 14,
            "The service preset is available from the real creation dialog with fourteen editable widgets and neutral initial weather.");

        var multiGroup = Data(1, Row("31", group: "Alpha;Beta"), Row("32", "En attente", group: "Beta"), Row("33", group: "Gamma;Delta"), Row("34", "Résolu", group: "Alpha"));
        var unionBoard = DashboardLayoutSettings.CreateBoard("Équipes de contrôle", "empty");
        unionBoard.Widgets.Add(new() { Id = "union-stock", Kind = "metric", SourceKey = "active", Title = "Tickets actifs des équipes", ShowMiniHistory = false });
        unionBoard.Widgets.Add(new() { Id = "group-table", Kind = "matrix", SourceKey = "group-status", Presentation = "heatmap", Span = 6 });
        unionBoard.Widgets.Add(new() { Id = "group-stack", Kind = "matrix", SourceKey = "group-status", Presentation = "stacked", TopCount = 3, Span = 6 });
        var unionLayout = new DashboardLayoutSettings { Boards = new() { unionBoard }, ActiveBoardId = unionBoard.Id };
        app.LoadFixture(multiGroup); await Call(app, "dashboard.layout.save", new { layout = unionLayout }); await Open("dashboard", new { editing = true });
        var groupTable = page.Locator("[data-widget-id=group-table] table");
        var groupStatusCells = await groupTable.Locator("tbody tr").First.Locator(".analytics-heat-cell").AllTextContentsAsync();
        Check(await groupTable.Locator("thead th").CountAsync() == 6
            && await groupTable.Locator("thead th").Last.TextContentAsync() == "Total"
            && await groupTable.Locator("tbody tr").First.Locator("td").First.TextContentAsync() == "Beta"
            && groupStatusCells.SequenceEqual(new[] { "1", "0", "0", "1" })
            && await page.Locator("[data-widget-id=group-stack] .analytics-stack-row").CountAsync() == 3
            && await page.Locator("[data-widget-id=group-stack] .analytics-stack-row > span").First.TextContentAsync() == "Beta"
            && await page.Locator("[data-widget-id=group-stack] .analytics-matrix-legend > span").CountAsync() == 4,
            "Active group matrices show four statuses including zeros, sort by row total and respect the stacked top-count limit.");
        await page.Locator("[data-widget-id=union-stock]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        var unionDialog = page.GetByRole(AriaRole.Dialog);
        await unionDialog.GetByText("Filtres propres à ce widget", new() { Exact = true }).ClickAsync();
        await unionDialog.GetByRole(AriaRole.Button, new() { Name = "+ Condition", Exact = true }).ClickAsync();
        var filterRow = unionDialog.Locator(".analytics-filter-row");
        await filterRow.GetByLabel("Type", new() { Exact = true }).SelectOptionAsync("matrix-row");
        await filterRow.GetByLabel("Source", new() { Exact = true }).SelectOptionAsync("group-status");
        await filterRow.GetByLabel("Valeurs acceptées · une par ligne", new() { Exact = true }).FillAsync("Alpha\nBeta");
        await unionDialog.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
        await Until("document.querySelector('main').dataset.ready==='true' && document.querySelector('[data-widget-id=union-stock] .analytics-metric-value')?.textContent==='2'");
        var unionFilter = app.Settings.Dashboard.Layout.Boards[0].Widgets[0].Filters.Single();
        Check(unionFilter.Kind == "matrix-row" && unionFilter.SourceKey == "group-status" && unionFilter.Values.SequenceEqual(new[] { "Alpha", "Beta" })
            && await page.Locator("[data-widget-id=union-stock] .analytics-metric-value").TextContentAsync() == "2",
            "The filter editor persists OR selections for group matrix rows and counts a ticket shared by both teams only once.");
        await page.Locator("[data-widget-id=union-stock] .analytics-metric-value").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).Locator("tbody tr").First.WaitForAsync();
        Check(await page.GetByRole(AriaRole.Dialog).Locator("tbody tr").CountAsync() == 2
            && await page.GetByRole(AriaRole.Dialog).Locator("tbody").GetByText("31", new() { Exact = true }).CountAsync() == 1
            && await page.GetByRole(AriaRole.Dialog).Locator("tbody").GetByText("32", new() { Exact = true }).CountAsync() == 1,
            "A multi-group OR metric drills into unique active source tickets without the resolved or unrelated team rows.");
        await CloseDialogs();
        await page.Locator("[data-widget-id=union-stock]").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByText("Filtres propres à ce widget", new() { Exact = true }).ClickAsync();
        Check(await page.GetByRole(AriaRole.Dialog).GetByLabel("Valeurs acceptées · une par ligne", new() { Exact = true }).InputValueAsync() == "Alpha\nBeta",
            "Reopening the widget editor restores every OR alternative for further editing."); await CloseDialogs();
        await Call(app, "dashboard.layout.save", new { personal = false, layout = layoutBeforeEditing });
        app.LoadFixture(source, Locations(source, settings));
        await Open("dashboard"); await page.SetViewportSizeAsync(900, 700);
        Check(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth+1"), "Analytical widgets fit the compact viewport without horizontal page overflow.");
        var outputIndex = Array.IndexOf(args, "--screenshots");
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[outputIndex + 1]); Directory.CreateDirectory(output); await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-dashboard.png"), FullPage = true });
            await page.EvaluateAsync("document.documentElement.dataset.theme='light'");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-dashboard-light.png"), FullPage = true });
            await Button("Modifier le dashboard").ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-dashboard-editor.png"), FullPage = true });
        }
        Check(external.IsEmpty, "Maps, local assistance and reports initiate no external browser requests.");
        Check(errors.IsEmpty, "Analytics modules produce no browser exceptions or strict-CSP violations." + (errors.IsEmpty ? "" : "\n" + string.Join("\n", errors)));
        await delivery;
    }
}
