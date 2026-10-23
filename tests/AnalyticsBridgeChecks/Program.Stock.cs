using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private sealed class StockTestHost : IPilotHost
    {
        public string Clipboard { get; private set; } = "";
        public int ClipboardWrites { get; private set; }
        public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text) { Clipboard = text; ClipboardWrites++; return Task.CompletedTask; }
        public Task OpenExternalAsync(string url) => throw new InvalidOperationException("No external GLPI page is available in stock checks.");
        public Task CloseAsync() => Task.CompletedTask;
        public Task<byte[]> CapturePngAsync() => throw new NotSupportedException();
        public Task PrintPdfAsync(string path) => throw new NotSupportedException();
        public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
    }

    private static AppSettings StockSettings(int siteCount = 4)
    {
        var city = new CityDefinition { Name = "Ville synthétique", Latitude = 1, Longitude = 2 };
        var settings = new AppSettings
        {
            PortalUrl = "https://glpi.example.invalid/front/ticket.php", Queries = new(),
            ExportFolder = Path.Combine(_root, "stock-exports"),
            Cities = new() { Cities = new() { city }, Sites = Enumerable.Range(0, siteCount).Select(index => new SiteDefinition
                { CityId = city.Id, Name = index switch { 0 => "Site chargé", 1 => "Site secondaire", 2 => "Site tertiaire", _ => "Site vide " + (index - 3).ToString("D3") } }).ToList() }
        };
        var board = DashboardLayoutSettings.CreateBoard("Stock synthétique", "empty"); board.FreshnessWarningHours = 48;
        board.Widgets.Add(new() { Id = "stock-oldest", Kind = "oldest", SourceKey = "groups", TopCount = 3, Title = "Plus anciens par groupe", Span = 12 });
        board.Widgets.Add(new() { Id = "stock-focus", Kind = "oldest", SourceKey = "tickets", TopCount = 3, Title = "Focus du groupe", Span = 12,
            Filters = new() { new() { Kind = "matrix-row", SourceKey = "group-status", Value = "Groupe Alpha" } } });
        board.Widgets.Add(new() { Id = "stock-authors", Kind = "participation", SourceKey = "authors", TopCount = 3, Title = "Rédacteurs observés", Span = 12 });
        board.Widgets.Add(new() { Id = "stock-comparison", Kind = "participation", SourceKey = "comparison", TopCount = 6, Title = "Rédaction et attribution", Span = 12,
            ParticipationRoster = new() { new() { Identity = "1", Name = "Agent A" }, new() { Name = "Agent absent" } } });
        board.Widgets.Add(new() { Id = "stock-sites", Kind = "sites", SourceKey = "sites", TopCount = 4, Title = "Sites du périmètre", WarningValue = 4, Span = 12 });
        board.Widgets.Add(new() { Id = "stock-branch", Kind = "metric", SourceKey = "active", Title = "Branche Applications", ShowMiniHistory = false,
            Filters = new() { new() { Kind = "distribution", SourceKey = "category", Value = "Applications", IncludeDescendants = true } } });
        board.Widgets.Add(new() { Id = "stock-total", Kind = "metric", SourceKey = "active", Title = "Stock actif connu", ShowMiniHistory = false });
        board.Widgets.Add(new() { Id = "stock-weather", Kind = "metric", SourceKey = "active", Title = "Météo du volume", Presentation = "weather-volume",
            WarningValue = 4, CriticalValue = 10, ShowMiniHistory = false });
        settings.Dashboard.Layout = new() { Boards = new() { board }, ActiveBoardId = board.Id };
        settings.Dashboard.PersonalLayout = settings.Dashboard.Layout.Copy();
        return settings;
    }

    private static ExtractionResult StockSource()
    {
        string[] Entry(string id, string status, string group, string date, string assigned, string followups, string tasks, string category)
            => new[] { id, "Ticket synthétique " + id, status, group, date, assigned, followups, tasks, category, "Incident" };
        var rows = new List<string[]>
        {
            Entry("501", "Nouveau", "Groupe Alpha; Groupe Bêta", "01/09/2026 09:00", "Agent A (1)", "Agent A (1); Agent B (2)", "Agent A (1)", "Applications > Portail"),
            Entry("502", "En attente", "Groupe Alpha", "02/09/2026", "Agent B (2)", "Agent A (1)", "Agent B (2)", "Applications"),
            Entry("502", "En cours (attribué)", "Groupe Alpha", "date illisible", "", "", "", "Applications > Portail > Authentification"),
            Entry("504", "Nouveau", "Groupe Bêta", "", "Agent C (3)", "Agent C (3)", "Agent D (4)", "Applications annexes"),
            Entry("505", "Nouveau", "Groupe Gamma", DateTimeOffset.Now.AddDays(2).ToString("yyyy-MM-dd"), "Agent C (3)", "Agent D (4)", "Agent C (3)", "Autre"),
            Entry("506", "Résolu", "Groupe Alpha", "01/09/2026", "Agent A (1)", "Agent A (1)", "Agent A (1)", "Applications"),
            Entry("507", "Statut personnalisé", "Groupe Alpha", "01/09/2026", "Agent A (1)", "Agent A (1)", "Agent A (1)", "Applications > Portail"),
            Entry("508", "Nouveau", "Groupe Alpha", "03/09/2026", "Agent A (1)", "Agent B (2)", "Agent B (2)", "Applications > Portail"),
            Entry("509", "Nouveau", "Groupe Alpha", "04/09/2026", "Agent B (2)", "Agent C (3)", "Agent D (4)", "Applications > Portail"),
            Entry("510", "Nouveau", "Groupe Alpha", "05/09/2026", "", "Agent B (2)", "Agent A (1)", "Autre"),
            Entry("511", "Nouveau", "Groupe Alpha", "06/09/2026", "Agent D (4)", "Agent A (1)", "Agent D (4)", "Autre"),
            Entry("512", "Nouveau", "Groupe Alpha", "07/09/2026", "Agent A (1)", "", "", "Autre"),
            Entry("513", "Nouveau", "Groupe Alpha; Groupe Delta", "08/09/2026", "Agent E (5)", "Agent E (5)", "Agent E (5)", "Autre")
        };
        return new()
        {
            Headers = new() { "ID", "Titre", "Statut", "Attribué à - Groupe de techniciens", "Date d’ouverture", "Attribué à - Technicien", "Suivis - Rédacteur", "Tâches - Rédacteur", "Catégorie", "Type" },
            Rows = rows, ExpectedTotal = rows.Count, Complete = true, SourceUrl = "https://glpi.example.invalid/front/ticket.php",
            Scope = "Relevé synthétique", ExtractedAt = DateTimeOffset.Now.AddHours(-72),
            Provenance = new() { RequestedUrl = "https://glpi.example.invalid/front/ticket.php", SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "stock-synthetic", Method = "native-csv", SourceKind = "glpi" }
        };
    }

    private static CityEnrichmentResult StockLocations(ExtractionResult source, AppSettings settings)
    {
        var city = settings.Cities.Cities[0];
        var matches = Enumerable.Range(0, source.Rows.Count).Select(index =>
        {
            var siteIndex = index switch { 0 or 1 or 2 or 5 or 7 or 8 => 0, 3 => 1, 10 or 11 or 12 => 2, _ => -1 };
            return siteIndex < 0 ? new CityMatch(index, null, null, CityMatchState.Unmatched, Array.Empty<string>())
                : new CityMatch(index, city.Id, city.Name, CityMatchState.Matched, new[] { city.Id })
                { SiteId = settings.Cities.Sites[siteIndex].Id, SiteName = settings.Cities.Sites[siteIndex].Name, SiteState = "matched", Confidence = "high" };
        }).ToArray();
        return new() { Source = source, Data = source, Applied = true, MatchedCount = matches.Count(match => match.SiteId is not null),
            SiteMatchedCount = matches.Count(match => match.SiteId is not null), Matches = matches };
    }

    private static async Task CheckStockViewsRuntime()
    {
        var settings = StockSettings(); var source = StockSource(); var host = new StockTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings,
            snapshots: new SnapshotStore(Path.Combine(_root, "stock-runtime-history")));
        app.LoadFixture(source, StockLocations(source, settings));
        var model = await Call(app, "dashboard.get"); var runId = Str(model, "runId"); var revision = app.Revision;
        JsonElement Content(string id) => Widget(model, id).GetProperty("content");
        object Selection(string widgetId, string kind, string key, string part = "stock") => new { runId, revision, widgetId, kind, key, part };
        static int[] Indexes(JsonElement page) => page.GetProperty("rows").EnumerateArray().Select(row => Int(row, "index")).ToArray();
        var oldest = Content("stock-oldest"); var group = oldest.GetProperty("groups")[0]; var groupKey = Str(group, "key");
        Check(Int(oldest, "count") == 11 && Int(oldest, "total") == 4 && Int(oldest, "topCount") == 5
            && oldest.GetProperty("groups").GetArrayLength() == 3 && Int(group, "count") == 9 && Int(group, "datedCount") == 8
            && group.GetProperty("oldest").EnumerateArray().Select(ticket => Int(ticket, "index")).SequenceEqual(new[] { 0, 1, 7, 8, 9 }),
            "The oldest widget bridge bounds the group preview but retains exact stock, top-five order and source indexes.");
        var focused = Content("stock-focus");
        Check(Int(focused, "count") == 9 && Int(focused, "topCount") == 10
            && focused.GetProperty("groups").GetArrayLength() == 1 && Str(focused.GetProperty("groups")[0], "key") == "scope:"
            && focused.GetProperty("groups")[0].GetProperty("oldest").EnumerateArray().Select(ticket => Int(ticket, "index")).SequenceEqual(new[] { 0, 1, 7, 8, 9, 10, 11, 12 }),
            "The focus widget returns one top-ten ranking of its actual scope without repeating multigroup source occurrences.");
        var stockRows = await Call(app, "dashboard.rows", Selection("stock-oldest", "oldest", groupKey));
        var invalidRows = await Call(app, "dashboard.rows", Selection("stock-oldest", "oldest", groupKey, "invalid"));
        Check(Indexes(stockRows).SequenceEqual(new[] { 0, 1, 2, 7, 8, 9, 10, 11, 12 }) && Indexes(invalidRows).SequenceEqual(new[] { 2 }),
            "Oldest bridge drilldowns include unrankable stock and isolate invalid openings without renumbering or ID deduplication.");
        await Reject<InvalidDataException>(() => Call(app, "dashboard.rows", new { runId, revision, widgetId = "stock-focus", kind = "record", rowIndex = 3 }),
            "A frozen-record drilldown rejects an original index outside the widget's filtered scope.");
        var record = await Call(app, "dashboard.rows", new { runId, revision, widgetId = "stock-focus", kind = "record", rowIndex = 7 });
        Check(Indexes(record).SequenceEqual(new[] { 7 }) && record.GetProperty("rows")[0].GetProperty("values")[0].GetString() == "508",
            "A frozen-record drilldown reads the exact imported source occurrence without a live browser.");
        var allGroups = await Call(app, "dashboard.data", new { runId, revision, widgetId = "stock-oldest", kind = "oldest", offset = 1, limit = 1 });
        Check(Int(allGroups, "total") == 4 && Int(allGroups, "offset") == 1 && allGroups.GetProperty("groups").GetArrayLength() == 1
            && Str(allGroups.GetProperty("groups")[0], "label") == "Groupe Bêta", "Oldest paging returns the next full group rather than repeating the bounded preview.");

        var authors = Content("stock-authors"); var author = authors.GetProperty("participants")[0]; var authorKey = Str(author, "key");
        Check(Int(authors, "population") == 11 && Int(authors, "total") == 6 && authors.GetProperty("participants").GetArrayLength() == 3
            && Int(author, "followups") == 3 && Int(author, "tasks") == 2 && Int(author, "both") == 1 && Int(author, "writers") == 4 && Int(author, "assigned") == 3,
            "Participation bridge measures followups, tasks, intersection, union and assignments independently on active source occurrences.");
        var assignedRows = await Call(app, "dashboard.rows", Selection("stock-authors", "participation", authorKey, "assigned"));
        Check(Indexes(assignedRows).SequenceEqual(new[] { 0, 7, 11 }), "Participation bridge drilldown returns exact assignment rows independently from writer rows.");
        var allAuthors = await Call(app, "dashboard.data", new { runId, revision, widgetId = "stock-authors", kind = "participation", offset = 3, limit = 10 });
        Check(Int(allAuthors, "total") == 6 && allAuthors.GetProperty("participants").GetArrayLength() == 3
            && !allAuthors.GetProperty("participants").EnumerateArray().Any(person => Str(person, "key") == authorKey),
            "Participation paging preserves every remaining actor and the unspecified bucket without overlapping the first page.");
        var compared = Content("stock-comparison").GetProperty("participants");
        var absent = compared.EnumerateArray().Single(person => Str(person, "name") == "Agent absent");
        var other = compared.EnumerateArray().Single(person => Str(person, "name") == "Autres");
        var otherRows = await Call(app, "dashboard.rows", Selection("stock-comparison", "participation", Str(other, "key"), "writers"));
        Check(Int(absent, "writers") == 0 && Int(absent, "assigned") == 0 && Int(other, "writers") == 9
            && Indexes(otherRows).SequenceEqual(new[] { 0, 1, 3, 4, 7, 8, 9, 10, 12 }),
            "Configured participation preserves measured zeros and Others is the union of source occurrences, never the sum of actors.");

        var sites = Content("stock-sites"); var loadedSite = sites.GetProperty("sites")[0]; var siteKey = Str(loadedSite, "key");
        Check(Int(sites, "population") == 11 && Int(sites, "total") == 5 && Int(loadedSite, "count") == 5
            && Str(loadedSite, "advice") == "Intervention à planifier" && loadedSite.GetProperty("tickets").GetArrayLength() == 5,
            "Sites bridge exposes exact active stock, configurable planning advice and bounded ticket previews.");
        var otherSites = await Call(app, "dashboard.data", new { runId, revision, widgetId = "stock-sites", kind = "sites", offset = 3, limit = 10 });
        var siteZero = otherSites.GetProperty("sites")[0]; var siteUnknown = otherSites.GetProperty("sites")[1];
        Check(Int(siteZero, "count") == 0 && Str(siteZero, "advice") == "Aucun ticket"
            && siteUnknown.GetProperty("unknown").GetBoolean() && Int(siteUnknown, "count") == 2 && Str(siteUnknown, "advice") == "À vérifier",
            "Site paging keeps configured zero-stock sites and an explicit unknown-site population.");
        var unknownRows = await Call(app, "dashboard.rows", Selection("stock-sites", "sites", Str(siteUnknown, "key")));
        Check(Indexes(unknownRows).SequenceEqual(new[] { 4, 9 }), "Unknown-site drilldown exposes exactly the active unresolved locations.");
        var copied = await Call(app, "dashboard.copyIds", Selection("stock-sites", "sites", siteKey));
        Check(host.Clipboard == "501;502;502;508;509" && Int(copied, "count") == 5 && Int(copied, "occurrences") == 5 && Int(copied, "missing") == 0,
            "Copying a site's complete ID list preserves source order and repeated identifiers explicitly.");
        await Reject<InvalidDataException>(() => Call(app, "dashboard.copyIds", Selection("stock-sites", "sites", Str(siteZero, "key"))),
            "Copying a zero-stock site is rejected instead of clearing the existing clipboard.");
        Check(host.ClipboardWrites == 1 && host.Clipboard == "501;502;502;508;509", "A rejected empty ID copy leaves the clipboard untouched.");
        var branchRows = await Call(app, "dashboard.rows", new { runId, revision, widgetId = "stock-branch", kind = "metric", key = "active" });
        Check(Content("stock-branch").GetProperty("value").GetDouble() == 5 && Indexes(branchRows).SequenceEqual(new[] { 0, 1, 2, 7, 8 }),
            "Category-branch filtering includes descendants while excluding lexical lookalikes and inactive or unknown statuses.");
        var thin = model.GetRawText();
        Check(!thin.Contains("scopeRows", StringComparison.Ordinal) && !thin.Contains("missingDateRows", StringComparison.Ordinal)
            && !thin.Contains("\"rows\":[", StringComparison.Ordinal) && !thin.Contains("activeRows", StringComparison.Ordinal),
            "The new widget bridge sends bounded display projections, never complete contributor-index arrays.");
        var saved = JsonSerializer.Deserialize<DashboardLayoutSettings>((await Call(app, "dashboard.layout.get")).GetRawText(), PilotApplication.Json)!;
        saved.Boards[0].FreshnessWarningHours = 49;
        await Call(app, "dashboard.layout.save", new { layout = saved });
        Check(AppSettings.Load().Dashboard.Layout.Boards[0].FreshnessWarningHours == 49
            && AppSettings.Load().Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "stock-branch").Filters.Single().IncludeDescendants,
            "Freshness reminders and category descendants round-trip through the actual JSON settings store.");
        await Reject<InvalidOperationException>(() => Call(app, "dashboard.copyIds", Selection("stock-sites", "sites", siteKey)),
            "A stale analytics run cannot overwrite the clipboard after the layout changes.");
        Check(host.ClipboardWrites == 1, "Stale-run rejection occurs before any clipboard write.");
        var partialLocations = new CityEnrichmentResult { Source = source, Data = source, Applied = true,
            Matches = StockLocations(source, settings).Matches.Skip(1).ToArray() };
        app.LoadFixture(source, partialLocations);
        var incomplete = await Call(app, "dashboard.get");
        Check(!Widget(incomplete, "stock-sites").GetProperty("available").GetBoolean()
            && Widget(incomplete, "stock-oldest").GetProperty("available").GetBoolean(),
            "An incomplete site match report cannot fabricate zero sites while unrelated oldest calculations remain usable.");
    }

    private static async Task CheckStockViewsWeb(string[] args)
    {
        await CheckOldestComponentWeb(args);
        await CheckStockDashboardWeb(args);
    }

    private static async Task CheckStockDashboardWeb(string[] args)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        if (!Directory.Exists(Path.Combine(repository, "web"))) repository = Directory.GetCurrentDirectory();
        var web = Path.Combine(repository, "web");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var external = new ConcurrentQueue<string>();
        page.PageError += (_, message) => errors.Enqueue(message); page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        var settings = StockSettings(102); var source = StockSource(); var host = new StockTestHost();
        var archiveStore = new SnapshotStore(Path.Combine(_root, "stock-ui-history"));
        var locations = StockLocations(source, settings); var archive = archiveStore.Save(source, settings, default, locations);
        WebBridge? bridge = null; Task delivery = Task.CompletedTask; var gate = new object();
        void Post(string message) { lock (gate) delivery = delivery.ContinueWith(async _ =>
            { if (!page.IsClosed) await page.EvaluateAsync("json=>window.__pilotDeliver(JSON.parse(json))", message); }, TaskScheduler.Default).Unwrap(); }
        await using var app = new PilotApplication(host, (name, value) => bridge?.Publish(name, value), settings, snapshots: archiveStore);
        app.LoadFixture(source, locations);
        await using var dispatcher = new WebBridge(app.Operations, app.ExecuteAsync, Post); bridge = dispatcher;
        await page.ExposeBindingAsync("__pilotSend", (BindingSource _, string json) => bridge.Receive(json));
        await page.AddInitScriptAsync("""
            (()=>{const handlers=new Set();window.chrome||={};window.chrome.webview={postMessage:message=>window.__pilotSend(JSON.stringify(message)),
              addEventListener:(name,listener)=>{if(name==='message')handlers.add(listener);},removeEventListener:(name,listener)=>handlers.delete(listener)};
              window.__pilotDeliver=message=>{for(const listener of handlers)listener({data:message});};})();
            """);
        var initial = JsonSerializer.Serialize(new { settings = app.Settings,
            session = new { loaded = true, revision = app.Revision, rows = source.Rows.Count, complete = true, historical = false, demo = false } }, PilotApplication.Json);
        var harness = """
            import {api} from '/js/bridge.js';import {pages} from '/js/pages/analytics.js';
            const root=document.querySelector('main'),state=INITIAL;let abort,cleanup;window.stockNotices=[];window.stockNavigation=[];
            api.on('session.changed',session=>state.session=session);api.on('settings.changed',event=>state.settings=event.settings);
            window.openStock=async(params={})=>{abort?.abort();cleanup?.();abort=new AbortController();root.replaceChildren();root.dataset.ready='false';
              cleanup=await pages.find(page=>page.id==='dashboard').render({root,api,state,params,signal:abort.signal,
                navigate:(id,parameters)=>id==='dashboard'?window.openStock(parameters):window.stockNavigation.push({id,parameters}),
                notify:message=>window.stockNotices.push(message)});root.dataset.ready='true';};await window.openStock();
            """.Replace("INITIAL", initial, StringComparison.Ordinal);
        const string html = """
            <!doctype html><html lang="fr" data-theme="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'none'; base-uri 'none'; form-action 'none'; object-src 'none'">
            <link rel="stylesheet" href="/css/app.css"><link rel="stylesheet" href="/css/analytics.css"><title>Stock integration checks</title></head>
            <body><main class="page-content"></main><script type="module" src="/stock-dashboard.js"></script></body></html>
            """;
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { external.Enqueue(uri.Host); await route.AbortAsync(); return; }
            if (uri.AbsolutePath == "/stock-dashboard.html") { await route.FulfillAsync(new() { ContentType = "text/html", Body = html }); return; }
            if (uri.AbsolutePath == "/stock-dashboard.js") { await route.FulfillAsync(new() { ContentType = "text/javascript", Body = harness }); return; }
            var path = Path.GetFullPath(Path.Combine(web, Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/')));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404, Body = "absent" }); return; }
            await route.FulfillAsync(new() { ContentType = Path.GetExtension(path) switch { ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" }, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        async Task Ready() => await page.Locator("main[data-ready=true]").WaitForAsync();
        async Task Until(string expression)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!await page.EvaluateAsync<bool>(expression)) await Task.Delay(50, deadline.Token);
        }
        async Task CloseDialogs() { while (await page.GetByRole(AriaRole.Dialog).CountAsync() > 0) await page.Keyboard.PressAsync("Escape"); }
        ILocator WidgetNode(string id) => page.Locator("[data-widget-id=" + id + "]");
        ILocator Button(string label) => page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });
        await page.GotoAsync("https://glpi-pilot.local/stock-dashboard.html"); await Ready();
        Check(await page.Locator(".analytics-widget").CountAsync() == 8 && await WidgetNode("stock-authors").Locator("tbody tr").CountAsync() == 3
            && await WidgetNode("stock-sites").Locator("tbody tr").CountAsync() == 4,
            "The real dashboard mounts oldest, authors, comparison and site widgets using bounded C# projections.");
        Check(await page.Locator(".analytics-freshness-issue:visible").CountAsync() == 1
            && await WidgetNode("stock-branch").Locator(".analytics-metric-value").TextContentAsync() == "5",
            "A stale live snapshot gets its configured reminder while category descendants remain a real scoped count.");
        Check(await WidgetNode("stock-comparison").Locator("tbody tr").First.EvaluateAsync<bool>("row=>{const writer=parseFloat(row.querySelector('.measure-writers .analytics-participation-track>span').style.width),assigned=parseFloat(row.querySelector('.measure-assigned .analytics-participation-track>span').style.width);return Math.abs(writer-400/9)<.001&&Math.abs(assigned-300/9)<.001;}")
            && await WidgetNode("stock-comparison").Locator("tbody tr").Filter(new() { HasText = "Agent absent" }).GetByRole(AriaRole.Button).First.IsDisabledAsync(),
            "Participation comparison bars share one C# scale, retain distinct roles and render configured zeroes without fabricated activity.");
        Check(await WidgetNode("stock-comparison").Locator("tbody tr").First.EvaluateAsync<bool>("row=>{const writer=row.querySelector('.measure-writers .analytics-participation-track'),assigned=row.querySelector('.measure-assigned .analytics-participation-track');return Math.abs(writer.getBoundingClientRect().width-assigned.getBoundingClientRect().width)<.5&&Math.abs(writer.firstElementChild.getBoundingClientRect().width/assigned.firstElementChild.getBoundingClientRect().width-4/3)<.01;}"),
            "Participation comparison uses equal physical track widths so a four-to-three count retains the same visible bar-length ratio.");
        await WidgetNode("stock-authors").GetByRole(AriaRole.Button, new() { Name = "Agent A (1) · Rédacteur · total distinct · 4", Exact = true }).ClickAsync();
        var modal = page.GetByRole(AriaRole.Dialog).Last;
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check((await modal.Locator("tbody tr td:first-child").AllTextContentsAsync()).SequenceEqual(new[] { "1", "2", "10", "11" }),
            "Selecting a participation value opens original writer rows rather than assignments or renumbered positions.");
        await CloseDialogs();
        Check(await WidgetNode("stock-authors").GetByRole(AriaRole.Button, new() { Name = "Agent A (1) · Rédacteur · total distinct · 4", Exact = true }).EvaluateAsync<bool>("control=>document.activeElement===control"),
            "Closing participation details returns keyboard focus to the exact selected value.");
        await WidgetNode("stock-authors").GetByRole(AriaRole.Button, new() { Name = "Toutes les personnes et regroupements (6)", Exact = true }).ClickAsync();
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 6, "All-participants detail includes actors beyond the widget preview and the unspecified population.");
        await CloseDialogs();
        await WidgetNode("stock-oldest").GetByRole(AriaRole.Button, new() { Name = "Groupe Alpha. Consulter les plus anciens.", Exact = true }).ClickAsync();
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 5, "The integrated oldest-group detail retains the global top-five presentation.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Consulter le ticket 508, ligne source 8.", Exact = true }).ClickAsync();
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 1 && await modal.Locator("tbody tr td:first-child").TextContentAsync() == "8"
            && (await modal.InnerTextAsync()).Contains("508", StringComparison.Ordinal) && await page.EvaluateAsync<bool>("window.stockNavigation.length===0"),
            "An oldest ticket opens its exact frozen local row through the real bridge without live navigation.");
        await CloseDialogs();
        await WidgetNode("stock-focus").Locator("tbody tr td:first-child button").First.ClickAsync();
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 8, "The filtered focus widget displays all eight valid openings within its top-ten allowance.");
        await CloseDialogs();
        await WidgetNode("stock-oldest").GetByRole(AriaRole.Button, new() { Name = "Tous les groupes (4)", Exact = true }).ClickAsync();
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 4 && (await modal.InnerTextAsync()).Contains("Groupe Gamma", StringComparison.Ordinal),
            "The complete oldest-group table retains an unrankable group beyond the limited widget preview.");
        await CloseDialogs();

        await WidgetNode("stock-sites").GetByRole(AriaRole.Button, new() { Name = "Copier tous les ID · Site chargé", Exact = true }).ClickAsync();
        await Until("window.stockNotices.some(text=>text.includes('5 identifiants copiés'))");
        Check(host.Clipboard == "501;502;502;508;509", "The visible site copy action sends the complete occurrence-preserving ID list to the native host.");
        await WidgetNode("stock-sites").GetByRole(AriaRole.Button, new() { Name = "Consulter la ligne 3 · 502 · Site chargé", Exact = true }).ClickAsync();
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check(await modal.Locator("tbody tr td:first-child").TextContentAsync() == "3", "Repeated IDs in site previews still open their own original source occurrence.");
        await CloseDialogs();
        Check(await WidgetNode("stock-sites").GetByRole(AriaRole.Button, new() { Name = "Consulter la ligne 3 · 502 · Site chargé", Exact = true }).EvaluateAsync<bool>("control=>document.activeElement===control"),
            "Closing a site source row returns keyboard focus to its original occurrence control.");
        await WidgetNode("stock-sites").GetByRole(AriaRole.Button, new() { Name = "Tous les sites (103)", Exact = true }).ClickAsync();
        await modal.Locator("tbody tr").First.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 100 && await modal.GetByRole(AriaRole.Button, new() { Name = "Copier tous les ID · Site vide 000", Exact = true }).IsDisabledAsync(),
            "The complete sites view paginates at one hundred rows and keeps configured zero-stock sites with disabled copy actions.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Suivant", Exact = true }).ClickAsync();
        await modal.GetByText("101–103 sur 103", new() { Exact = true }).WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 3 && (await modal.InnerTextAsync()).Contains("Site non connu / à vérifier", StringComparison.Ordinal)
            && (await modal.InnerTextAsync()).Contains("À vérifier", StringComparison.Ordinal),
            "Site pagination reaches the final unknown-location bucket without dropping zero-stock sites or reusing the first page.");
        await CloseDialogs();

        await Button("Modifier le dashboard").ClickAsync();
        await WidgetNode("stock-branch").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await modal.GetByText("Filtres propres à ce widget", new() { Exact = true }).ClickAsync();
        var descendant = modal.GetByLabel("Inclure les sous-catégories", new() { Exact = true });
        Check(await descendant.IsCheckedAsync(), "The widget editor restores the category-descendant option from persisted JSON.");
        await descendant.UncheckAsync(); await modal.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready();
        Check(await WidgetNode("stock-branch").Locator(".analytics-metric-value").TextContentAsync() == "1"
            && !app.Settings.Dashboard.Layout.Boards[0].Widgets.Single(widget => widget.Id == "stock-branch").Filters.Single().IncludeDescendants,
            "Disabling descendants changes the widget to the exact category and persists that choice.");
        await WidgetNode("stock-branch").GetByRole(AriaRole.Button, new() { Name = "Régler", Exact = true }).ClickAsync();
        await modal.GetByText("Filtres propres à ce widget", new() { Exact = true }).ClickAsync(); await descendant.CheckAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready();
        Check(await WidgetNode("stock-branch").Locator(".analytics-metric-value").TextContentAsync() == "5", "Re-enabling descendants restores the branch count without admitting similarly prefixed categories.");
        var weatherBefore = await WidgetNode("stock-weather").Locator(".analytics-volume-weather-status").TextContentAsync();
        await page.Locator("summary").GetByText("Disposition", new() { Exact = true }).ClickAsync(); await Button("Nom et organisation").ClickAsync();
        await modal.GetByLabel("Rappel après un relevé ancien · heures", new() { Exact = true }).FillAsync("");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync(); await Ready();
        Check(await page.Locator(".analytics-freshness-issue:visible").CountAsync() == 0
            && await WidgetNode("stock-weather").Locator(".analytics-volume-weather-status").TextContentAsync() == weatherBefore
            && app.Settings.Dashboard.Layout.Boards[0].FreshnessWarningHours is null,
            "The board's optional freshness reminder can be disabled without changing a single weather state or stock count.");
        await Call(app, "history.open", new { id = archive.Id });
        await page.EvaluateAsync("()=>window.openStock()"); await Ready();
        Check(app.IsHistorical && await page.Locator(".analytics-freshness-issue:visible").CountAsync() == 0
            && await WidgetNode("stock-total").Locator(".analytics-metric-value").TextContentAsync() == "11",
            "An archived snapshot keeps its original stock and does not receive a live freshness reminder despite an archived 48-hour threshold.");
        var capture = Array.IndexOf(args, "--screenshots");
        if (capture >= 0 && capture + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[capture + 1]); Directory.CreateDirectory(output);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-stock-widgets.png"), FullPage = true });
            await WidgetNode("stock-comparison").ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-participation-comparison.png") });
            await page.SetViewportSizeAsync(560, 940);
            await page.EvaluateAsync("window.scrollTo(0,0)");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-stock-narrow.png"), FullPage = true });
        }
        Check(errors.IsEmpty && external.IsEmpty && await page.EvaluateAsync<bool>("window.stockNavigation.length===0"),
            "All stock dashboard interactions remain offline under strict CSP without browser errors or unexpected navigation.");
    }

    private static async Task CheckOldestComponentWeb(string[] args)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        if (!Directory.Exists(Path.Combine(repository, "web"))) repository = Directory.GetCurrentDirectory();
        var web = Path.Combine(repository, "web");
        var reference = new DateTimeOffset(2026, 4, 8, 12, 0, 0, TimeSpan.FromHours(2));
        const string groupLabel = "Groupe <b>Alpha</b>";
        string[] Entry(string id, string date, string group = groupLabel) => new[] { id, "Titre <img src=x onerror=alert(1)>", "Nouveau", date, group, "Agent exemple", "Moyenne" };
        var source = new ExtractionResult
        {
            Headers = new() { "ID", "Titre", "Statut", "Date d’ouverture", "Attribué à - Groupe de techniciens", "Attribué à - Technicien", "Priorité" },
            Rows = new() { Entry("10", "01/04/2026 09:00"), Entry("2", "01/04/2026 09:00"), Entry("900", "01/04/2026 08:00"),
                Entry("3", "02/04/2026"), Entry("4", "03/04/2026"), Entry("5", "04/04/2026"), Entry("6", "05/04/2026"),
                Entry("7", "illisible"), Entry("8", ""), Entry("9", "09/04/2026"), Entry("11", "", "Groupe sans date") },
            Complete = true, ExpectedTotal = 11, ExtractedAt = reference
        };
        var settings = new DashboardSettings();
        var snapshot = DashboardOldestGroups.Calculate(source, settings, DashboardService.Calculate(source, settings, reference), topCount: 10);
        var model = JsonSerializer.SerializeToNode(snapshot, PilotApplication.Json)!.AsObject(); model["topCount"] = 5;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "fr-FR", ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15000);
        var errors = new ConcurrentQueue<string>(); var external = new ConcurrentQueue<string>();
        page.PageError += (_, message) => errors.Enqueue(message); page.Console += (_, message) => { if (message.Type == "error") errors.Enqueue(message.Text); };
        var harness = """
            import {renderOldestGroups} from '/js/pages/analytics-oldest.js';
            window.stockCalls=[];window.stockNavigation=[];window.stockModel=MODEL;
            const root=document.querySelector('main'); const ctx={root,signal:new AbortController().signal,analyticsRevision:42,
              navigate:(id,params)=>window.stockNavigation.push({id,params})};
            window.renderStock=()=>{root.replaceChildren(renderOldestGroups(ctx,window.stockModel,selection=>window.stockCalls.push(selection)));root.dataset.ready='true';};
            window.renderStock();
            """.Replace("MODEL", model.ToJsonString(PilotApplication.Json), StringComparison.Ordinal);
        const string html = """
            <!doctype html><html lang="fr" data-theme="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'none'; base-uri 'none'; form-action 'none'; object-src 'none'">
            <link rel="stylesheet" href="/css/app.css"><link rel="stylesheet" href="/css/analytics.css"><title>Stock component checks</title></head>
            <body><main class="page-content analytics"></main><script type="module" src="/stock-component.js"></script></body></html>
            """;
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url);
            if (uri.Host != "glpi-pilot.local") { external.Enqueue(uri.Host); await route.AbortAsync(); return; }
            if (uri.AbsolutePath == "/stock-component.html") { await route.FulfillAsync(new() { ContentType = "text/html", Body = html }); return; }
            if (uri.AbsolutePath == "/stock-component.js") { await route.FulfillAsync(new() { ContentType = "text/javascript", Body = harness }); return; }
            var path = Path.GetFullPath(Path.Combine(web, Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/')));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404, Body = "absent" }); return; }
            await route.FulfillAsync(new() { ContentType = Path.GetExtension(path) switch { ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" }, BodyBytes = await File.ReadAllBytesAsync(path) });
        });
        await page.GotoAsync("https://glpi-pilot.local/stock-component.html"); await page.Locator("main[data-ready=true]").WaitForAsync();
        var groupControl = page.GetByRole(AriaRole.Button, new() { Name = groupLabel + ". Consulter les plus anciens.", Exact = true });
        Check(await page.Locator("main tbody tr").CountAsync() == 2 && await groupControl.CountAsync() == 1
            && await page.Locator("main b,main img").CountAsync() == 0,
            "Oldest UI renders every projected group and treats group labels as text under strict CSP.");
        await groupControl.FocusAsync(); await page.Keyboard.PressAsync("Enter");
        var modal = page.GetByRole(AriaRole.Dialog);
        await modal.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 5
            && (await modal.Locator("tbody tr td:nth-child(2) button").AllTextContentsAsync()).SequenceEqual(new[] { "900", "2", "10", "3", "4" }),
            "Oldest UI opens the configured top five through the keyboard while preserving timestamp and numeric-ID order.");
        Check(await modal.Locator("img").CountAsync() == 0 && (await modal.InnerTextAsync()).Contains("Titre <img src=x onerror=alert(1)>", StringComparison.Ordinal)
            && (await modal.Locator("tbody tr td:nth-child(2) small").AllTextContentsAsync()).SequenceEqual(new[] { "Ligne 3", "Ligne 2", "Ligne 1", "Ligne 4", "Ligne 5" }),
            "Oldest UI keeps ticket titles literal and exposes original row numbers instead of ranking positions.");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Dates invalides · " + groupLabel + " : 1 lignes. Voir les lignes contributrices.", Exact = true }).ClickAsync();
        Check(await page.EvaluateAsync<bool>("key=>window.stockCalls.length===1&&window.stockCalls[0].kind==='oldest'&&window.stockCalls[0].key===key&&window.stockCalls[0].part==='invalid'", snapshot.Groups[0].Key),
            "Oldest UI requests the exact group's invalid-date population with its opaque backend selector.");
        await page.Keyboard.PressAsync("Escape");
        Check(await modal.CountAsync() == 0 && await groupControl.EvaluateAsync<bool>("node=>node===document.activeElement"),
            "Closing the oldest-group detail restores focus to the original group control.");
        await groupControl.ClickAsync(); await modal.WaitForAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Consulter le ticket 900, ligne source 3.", Exact = true }).ClickAsync();
        Check(await page.EvaluateAsync<bool>("window.stockCalls.length===2&&window.stockCalls[1].kind==='record'&&window.stockCalls[1].rowIndex===2&&window.stockNavigation.length===0")
            && await modal.CountAsync() == 0, "Oldest UI opens the frozen source occurrence by its original index without requiring a live ticket page.");
        await page.EvaluateAsync("()=>{window.stockModel.topCount=10;window.renderStock();}");
        await groupControl.ClickAsync(); await modal.WaitForAsync();
        Check(await modal.Locator("tbody tr").CountAsync() == 7,
            "The same oldest UI supports the ten-ticket focus projection without fabricating absent or unrankable rows.");
        var capture = Array.IndexOf(args, "--screenshots");
        if (capture >= 0 && capture + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[capture + 1]); Directory.CreateDirectory(output);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "analytics-oldest-detail.png"), FullPage = true });
        }
        await page.Keyboard.PressAsync("Escape");
        await page.GetByRole(AriaRole.Button, new() { Name = "Groupe sans date. Consulter les plus anciens.", Exact = true }).ClickAsync();
        Check(await modal.GetByText("Aucune ouverture classable", new() { Exact = true }).CountAsync() == 1
            && await modal.GetByRole(AriaRole.Button, new() { Name = "Stock actif · Groupe sans date : 1 lignes. Voir les lignes contributrices.", Exact = true }).CountAsync() == 1,
            "A group without a valid opening remains inspectable with its full stock and a distinct unrankable message.");
        await page.Keyboard.PressAsync("Escape");
        await page.EvaluateAsync("()=>{window.stockModel.maximumAgeDays=0;window.renderStock();}");
        Check(await page.Locator("main .toolbar .analytics-oldest-age").First.EvaluateAsync<string>("node=>getComputedStyle(node).backgroundColor") == "rgb(235, 245, 251)",
            "An all-zero-age oldest scale remains visually neutral instead of displaying the warm maximum colour.");
        Check(errors.IsEmpty && external.IsEmpty, "Oldest UI uses only local modules, safe text and CSP-compatible CSSOM with no browser error.");
    }
}
