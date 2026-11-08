using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;
using static WorkAssert;

public static class WorkBrowserChecks
{
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    public static async Task<List<(string Name, Func<Task> Run)>> Create(string executable)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { ExecutablePath = executable, Headless = true, Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
        return new()
        {
            ("Runtime action preview never mutates and durable receipt precedes one verified POST", () => ActionCircuit(false)),
            ("Runtime ambiguous receipt blocks another preview until manual review", () => ActionCircuit(true)),
            ("Runtime enrichment reads only its limit and preserves untouched candidate status", Enrichment),
            ("WebView ticket reader renders real engine observations and restores filters", TicketPage),
            ("WebView optional reader sections omit null placeholders while preserving literal user text", OptionalReaderContent),
            ("WebView seven-digit ticket IDs stay horizontal and open the original source row", TicketIdentifierLayout),
            ("WebView unified Tickets retains its queried list and unfinished personal work", UnifiedTicketList),
            ("WebView saved dossiers and original-row links remain local without automatic reads", SavedTicketJourney),
            ("WebView simulated ticket changes refresh the list and preserve the real workspace", SimulatedTicketJourney),
            ("WebView workspace panels await render and cleanup exactly once before close or navigation", WorkspacePanelLifecycle),
            ("WebView notes checklist and reminder save through real handler", PersonalPage),
            ("WebView draft composition persists and clipboard uses actual stored text", DraftPage),
            ("WebView immediate navigation waits for its last draft save", DraftNavigation),
            ("WebView long collected text opens intact as inert text", LongTextPage),
            ("WebView calendar typed controls save real technician capacity", CalendarPage),
            ("WebView local dispatch persists an explicit proposal", PlanningPage),
            ("WebView enrichment restoration does not start network reads", EnrichmentPage),
            ("WebView Mon espace uses personal identity and live stored templates", DayPage),
            ("WebView relations expose unknown facts and block unsafe links", InsightsPage)
        };
    }
    public static async Task DisposeAsync() { if (_browser is not null) await _browser.DisposeAsync(); _playwright?.Dispose(); }

    private static string FormHtml(string content = "") => "<!doctype html><meta charset='utf-8'><title>Ticket synthétique</title><a href='/glpi/front/logout.php'>Déconnexion</a>"
        + "<form id='itil-form'><input type='hidden' name='id' value='42'><label for='name'>Titre</label><input id='name' name='name' value='Ticket synthétique'></form>"
        + "<form id='action-form' method='post' action='/glpi/front/itilfollowup.form.php'><input type='hidden' name='items_id' value='42'><input type='hidden' name='itemtype' value='Ticket'><label for='content'>Contenu</label><textarea id='content' name='content'></textarea><input type='hidden' name='_glpi_csrf_token' value='NEVER_TOKEN'><button name='add' value='1' type='submit'>Ajouter</button></form>"
        + "<div class='timeline_history'>" + (content.Length > 0 ? "<div class='h_item' id='entry-501'><div class='h_content ITILFollowup'><div class='rich_text_container'>" + WebUtility.HtmlEncode(content) + "</div></div></div>" : "") + "</div>";

    private static async Task ActionCircuit(bool ambiguous)
    {
        var context = await _browser!.NewContextAsync(); var posts = 0; var content = ""; var persistedBeforeSend = false; string folder = "";
        await context.RouteAsync("**/*", async route =>
        {
            if (new Uri(route.Request.Url).Host != "workplace-glpi.test") { await route.AbortAsync(); return; }
            if (route.Request.Method == "POST")
            {
                posts++; var path = Path.Combine(folder, "workplace-action-receipts.json"); persistedBeforeSend = File.Exists(path) && File.ReadAllText(path).Contains("pending", StringComparison.Ordinal);
                var fields = (route.Request.PostData ?? "").Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "");
                Check(fields.GetValueOrDefault("_glpi_csrf_token") == "NEVER_TOKEN");
                if (!ambiguous) content = fields.GetValueOrDefault("content", "");
                await route.FulfillAsync(new() { Status = ambiguous ? 503 : 303, Headers = ambiguous ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Location"] = WorkFixture.TicketUrl }, Body = ambiguous ? "<h1>Réponse indisponible</h1>" : "" }); return;
            }
            var html = route.Request.Url.Contains("ticket.form.php", StringComparison.Ordinal) ? FormHtml(content) : "<a href='/glpi/front/logout.php'>Déconnexion</a><table id='search-results'><tr><th>ID</th></tr><tr><td>42</td></tr></table>";
            await route.FulfillAsync(new() { ContentType = "text/html", Body = html });
        });
        var search = await context.NewPageAsync(); await search.GotoAsync(WorkFixture.ListUrl); var service = new BrowserService(); service.AttachForTesting(context, WorkFixture.ListUrl);
        await using var f = new WorkFixture(service); folder = f.Folder;
        await service.FindBestPageAsync(f.App.Settings, search, CancellationToken.None);
        var key = await f.Key(); await f.Call("workplace.ticket.read", new { ticketKey = key });
        var state = await f.Call("workplace.actions.inspect", new { ticketKey = key }); var adapter = S(state.GetProperty("candidates")[0], "key");
        await f.Call("workplace.actions.adapter.save", new { ticketKey = key, adapterKey = adapter });
        var preview = await f.Call("workplace.actions.preview", new { ticketKey = key, adapterKey = adapter, value = "Compte rendu synthétique" }); var token = S(preview, "token");
        Check(posts == 0 && await service.TicketPage!.Locator("#content").InputValueAsync() == "" && !preview.GetRawText().Contains("NEVER_TOKEN"));
        await Refuse(() => f.Call("workplace.actions.confirm", new { ticketKey = key, token, confirmed = false }), "explicite");
        var receipt = await f.Call("workplace.actions.confirm", new { ticketKey = key, token, confirmed = true }); Check(posts == 1 && persistedBeforeSend && S(receipt, "state") == (ambiguous ? "unknown" : "verified"));
        Check(search.Url == WorkFixture.ListUrl && ReferenceEquals(service.ActivePage, search));
        await Refuse(() => f.Call("workplace.actions.confirm", new { ticketKey = key, token, confirmed = true }), "Aperçu"); Check(posts == 1);
        if (ambiguous)
        {
            await Refuse(() => f.Call("workplace.actions.preview", new { ticketKey = key, adapterKey = adapter, value = "Ne pas rejouer" }), "indéterminé");
            await f.Call("workplace.actions.review", new { ticketKey = key, receiptId = token, confirmed = true });
            var after = await f.Call("workplace.actions.get", new { ticketKey = key }); Check(after.GetProperty("receipts")[0].GetProperty("reviewedAt").ValueKind == JsonValueKind.String);
        }
        Check(!File.ReadAllText(Path.Combine(folder, "workplace-action-receipts.json")).Contains("NEVER_TOKEN"));
    }
    private static async Task Enrichment()
    {
        var context = await _browser!.NewContextAsync(); var reads = new List<string>();
        await context.RouteAsync("**/*", async route =>
        {
            var uri = new Uri(route.Request.Url); if (uri.Host != "workplace-glpi.test") { await route.AbortAsync(); return; }
            if (uri.AbsolutePath.EndsWith("ticket.form.php", StringComparison.Ordinal)) { reads.Add(uri.Query); var id = uri.Query.Split('=').Last(); await route.FulfillAsync(new() { ContentType = "text/html", Body = FormHtml().Replace("42", id) }); }
            else await route.FulfillAsync(new() { ContentType = "text/html", Body = "<a href='/glpi/front/logout.php'>Déconnexion</a><table><tr><th>ID</th></tr><tr><td>42</td></tr></table>" });
        });
        var search = await context.NewPageAsync(); await search.GotoAsync(WorkFixture.ListUrl); var service = new BrowserService(); service.AttachForTesting(context, WorkFixture.ListUrl); await using var f = new WorkFixture(service);
        await f.Call("workplace.enrichment.prepare", new { revision = f.App.Revision, rows = new[] { 0, 1 }, limit = 1 }); Check(reads.Count == 0);
        var result = await f.Call("workplace.enrichment.run", new { }); Check(I(result, "attempted") == 1 && result.GetProperty("queue").GetProperty("rows")[1].GetProperty("outcome").ValueKind == JsonValueKind.Null);
        var firstReads = reads.Count; await f.Call("workplace.enrichment.run", new { }); Check(reads.Count > firstReads && reads.Last() == "?id=43");
        Check(f.Source.Rows[0][2] == "Nouveau" && f.Events.Any(e => e.Name == "workplace.enrichment.progress"));
    }
    private sealed class UiSession : IAsyncDisposable
    {
        public WorkFixture Fixture { get; } = new();
        public IPage Page { get; private set; } = null!;
        private IBrowserContext _context = null!;
        public List<string> Errors { get; } = new();
        public List<string> Operations { get; } = new();
        public List<string> ExternalRequests { get; } = new();
        public async Task Start(string page, object? args = null, TicketDetailResult? detail = null, bool cacheDetail = true)
        {
            if (cacheDetail) Fixture.App.ReadFixture(detail ?? WorkFixture.Detail());
            _context = await _browser!.NewContextAsync(new() { ViewportSize = new() { Width = 1480, Height = 1050 }, Locale = "fr-FR" }); Page = await _context.NewPageAsync(); Page.PageError += (_, error) => Errors.Add(error);
            var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
            await Page.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url); if (uri.Host != "workplace-ui.test") { ExternalRequests.Add(route.Request.Url); await route.AbortAsync(); return; }
                if (uri.AbsolutePath == "/") { await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><html lang='fr' data-theme='light' data-motion='reduce'><meta charset='utf-8'><link rel='stylesheet' href='/css/app.css'><link rel='stylesheet' href='/css/workplace.css'><link rel='stylesheet' href='/css/tickets.css'><link rel='stylesheet' href='/css/domenia.css'><main id='content' style='height:100vh;overflow:auto'><div id='root' class='page-content'></div></main><div id='notices'></div></html>" }); return; }
                var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); var path = Path.GetFullPath(Path.Combine(web, relative));
                if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
                await route.FulfillAsync(new() { ContentType = path.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/css", Body = await File.ReadAllTextAsync(path) });
            });
            var eventOffset = Fixture.Events.Count;
            await Page.ExposeFunctionAsync("workplaceRuntime", async (string operation, string json) =>
            {
                Operations.Add(operation);
                var result = await Fixture.App.ExecuteAsync(operation, JsonDocument.Parse(json).RootElement);
                var events = Fixture.Events.Skip(eventOffset).Select(entry => new { name = entry.Name, data = entry.Data }).ToArray();
                eventOffset = Fixture.Events.Count;
                return JsonSerializer.Serialize(new { result, events }, PilotApplication.Json);
            });
            await Page.GotoAsync("http://workplace-ui.test/");
            var state = (await Fixture.Call("app.bootstrap", new { })).GetRawText();
            await Page.EvaluateAsync("""
                async state => {
                  const {pages:workplacePages}=await import('/js/pages/workplace.js');
                  const {pages:recordPages}=await import('/js/pages/records.js');
                  const pages=[...workplacePages,...recordPages];
                  const listeners=new Map(); let cleanup, controller;
                  window.workState=JSON.parse(state);window.workNotices=[];window.workReady=false;
                  window.addEventListener('pilot:error',event=>workNotices.push({text:event.detail?.message||String(event.detail),tone:'error'}));
                  window.workApi={call:async(op,args={})=>{const envelope=JSON.parse(await workplaceRuntime(op,JSON.stringify(args)));for(const event of envelope.events){if(event.name==='session.changed')workState.session=event.data;if(event.name==='settings.changed')workState.settings=event.data.settings;listeners.get(event.name)?.(event.data);}if(op==='records.list')window.workLastRecords={args,result:envelope.result};return envelope.result;},on:(name,handler)=>{listeners.set(name,handler);return()=>listeners.delete(name);}};
                  window.workNavigate=async(id,params={})=>{await cleanup?.();cleanup=null;controller?.abort();controller=new AbortController(); document.querySelectorAll('dialog').forEach(d=>d.remove());const root=document.querySelector('#root');root.replaceChildren();window.workReady=false;
                    const page=pages.find(p=>p.id===id);if(!page){window.workDestination={id,params};return;}window.workPage=id;
                    cleanup=await page.render({root,api:workApi,state:workState,signal:controller.signal,navigate:workNavigate,params,refresh:()=>workNavigate(id,params),notify:(text,tone)=>{workNotices.push({text,tone});document.querySelector('#notices').textContent=text;}});window.workReady=true;};
                }
                """, state);
            await Navigate(page, args);
        }
        public async Task Navigate(string page, object? args = null)
        { await Page.EvaluateAsync("async ({page,args})=>await workNavigate(page,args)", new { page, args = args ?? new { } }); await Page.WaitForFunctionAsync("()=>workReady"); }
        public async Task Capture(string name)
        {
            var folder = Environment.GetEnvironmentVariable("GLPI_TEST_WORKPLACE_SCREENSHOTS"); if (string.IsNullOrWhiteSpace(folder)) return;
            Directory.CreateDirectory(folder); await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, name), FullPage = true });
        }
        public async Task NoErrors() { Check(Errors.Count == 0, string.Join("\n", Errors)); Check(!await Page.EvaluateAsync<bool>("()=>workNotices.some(n=>n.tone==='error'||n.tone==='danger')"), await Page.EvaluateAsync<string>("()=>JSON.stringify(workNotices)")); }
        public async ValueTask DisposeAsync() { if (_context is not null) await _context.DisposeAsync(); await Fixture.DisposeAsync(); }
    }
    private static async Task WorkspacePanelLifecycle()
    {
        await using var ui = new UiSession(); await ui.Start("records");
        var result = await ui.Page.EvaluateAsync<JsonElement>("""
            async () => {
              const { createWorkspacePanels } = await import('/js/workspace-panels.js');
              const gate = () => { let resolve; const promise=new Promise(done=>resolve=done); return {promise,resolve}; };
              const root=document.createElement('div'); document.body.append(root);
              const notices=[]; const base={root,params:{},signal:new AbortController().signal,notify:message=>notices.push(message)};
              let cleaned=0,closed=0; const started=gate(),renderRelease=gate(),cleanupStarted=gate(),cleanupRelease=gate();
              const first=createWorkspacePanels({...base,navigate:()=>{throw new Error('Unexpected navigation');}});
              const opening=first.open('Fermeture contrôlée',async()=>{started.resolve();await renderRelease.promise;
                return async()=>{cleaned++;cleanupStarted.resolve();await cleanupRelease.promise;};},{panel:'lifecycle'});
              await started.promise;
              const node=document.querySelector('dialog[data-workspace-panel="lifecycle"]');
              node.addEventListener('pilot:closed',()=>closed++);
              node.querySelector('.modal-heading button').click(); const firstDisposal=first.dispose();
              const waitsForRender=node.isConnected&&cleaned===0&&closed===0;
              renderRelease.resolve(); await cleanupStarted.promise;
              const waitsForCleanup=node.isConnected&&cleaned===1&&closed===0;
              cleanupRelease.resolve(); const firstModal=await opening; await Promise.all([firstDisposal,firstModal.close(),first.dispose()]);
              const closesOnce=cleaned===1&&closed===1&&!node.isConnected;

              let renderCount=0,refreshCleanups=0,local; const secondRender=gate(),secondRelease=gate();
              const second=createWorkspacePanels({...base,navigate:()=>{throw new Error('Unexpected navigation');}});
              const secondModal=await second.open('Rafraîchissement contrôlé',async ctx=>{local=ctx;renderCount++;
                if(renderCount===2){secondRender.resolve();await secondRelease.promise;}
                return async()=>{refreshCleanups++;};},{panel:'refresh'});
              const refresh=local.refresh(); await secondRender.promise;
              const duringRefresh=refreshCleanups===1&&secondModal.element.isConnected;
              const secondClose=secondModal.close(),secondDisposal=second.dispose(); secondRelease.resolve();
              await Promise.all([refresh,secondClose,secondDisposal]);
              const refreshOnce=duringRefresh&&renderCount===2&&refreshCleanups===2&&!secondModal.element.isConnected;

              const navigation=gate(),order=[];
              const third=createWorkspacePanels({...base,navigate:async(page,args)=>{order.push(`navigate:${page}:${args.rowIndex}`);navigation.resolve();}});
              const thirdModal=await third.open('Navigation du rendu',async ctx=>{await Promise.resolve();
                await ctx.navigate('records',{rowIndex:1});order.push('render-return');return async()=>{order.push('cleanup');};},{panel:'redirect'});
              await Promise.race([navigation.promise,new Promise((_,reject)=>setTimeout(()=>reject(new Error('Render awaited its own navigation')),2000))]);
              await third.dispose();
              const redirectsAfterCleanup=order.join('|')==='render-return|cleanup|navigate:records:1'&&!thirdModal.element.isConnected;

              let failedCleanup=0,failedClosed=0,saveAvailable=false,failureContext;
              const fourth=createWorkspacePanels({...base,navigate:()=>{throw new Error('Unexpected navigation');}});
              const fourthModal=await fourth.open('Échec de sauvegarde',async ctx=>{failureContext=ctx;
                ctx.root.append(Object.assign(document.createElement('textarea'),{value:'Brouillon conservé'}));
                return async()=>{failedCleanup++;if(!saveAvailable)throw new Error('Sauvegarde refusée');};},{panel:'failure'});
              fourthModal.element.addEventListener('pilot:closed',()=>failedClosed++);
              const failure=await fourth.dispose().then(()=>false,reason=>reason.message==='Sauvegarde refusée');
              const failureRetained=failure&&failedCleanup===1&&failedClosed===0&&fourthModal.element.isConnected
                &&!failureContext.signal.aborted&&!failureContext.root.inert&&failureContext.root.querySelector('textarea').value==='Brouillon conservé';
              saveAvailable=true;await Promise.all([fourthModal.close(),fourth.dispose()]);await fourthModal.close();await fourth.dispose();
              const failureRecoverable=failureRetained&&failedCleanup===2&&failedClosed===1&&!fourthModal.element.isConnected&&failureContext.signal.aborted;
              return {waitsForRender,waitsForCleanup,closesOnce,refreshOnce,redirectsAfterCleanup,failureRecoverable,notices};
            }
            """);
        foreach (var key in new[] { "waitsForRender", "waitsForCleanup", "closesOnce", "refreshOnce", "redirectsAfterCleanup", "failureRecoverable" })
            Check(result.GetProperty(key).GetBoolean(), key + ": " + result.GetRawText());
        Check(result.GetProperty("notices").GetArrayLength() == 0, result.GetRawText()); await ui.NoErrors();
    }
    private static async Task TicketPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision });
        Check(await ui.Page.GetByText("Réponse visible", new() { Exact = true }).CountAsync() > 0);
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Consulter les observations", Exact = true }).ClickAsync();
        await ui.Page.GetByText("Filtres de chronologie", new() { Exact = true }).ClickAsync(); await ui.Page.GetByLabel("Type d’événement", new() { Exact = true }).FillAsync("Suivi"); await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Appliquer les filtres", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>document.querySelector('.work-section-content')?.textContent.includes('Observation non datée')");
        Check(!await ui.Page.Locator(".work-section-content").InnerTextAsync().ContinueWith(t => t.Result.Contains("Préparer")));
        await ui.Navigate("tickets", new { ticketKey = await ui.Fixture.Key() });
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Consulter les observations", Exact = true }).ClickAsync();
        Check(await ui.Page.GetByLabel("Type d’événement", new() { Exact = true }).InputValueAsync() == "Suivi"); await ui.NoErrors();
    }
    private static Task<int> NullTextNodes(IPage page) => page.EvaluateAsync<int>("""
        () => { const walker = document.createTreeWalker(document.querySelector('.tickets-tool-modal .modal-body')||document.querySelector('.work-ticket-reader'), NodeFilter.SHOW_TEXT); let count = 0;
          while (walker.nextNode()) if (walker.currentNode.nodeValue.trim() === 'null') count++; return count; }
        """);
    private static async Task OptionalReaderContent()
    {
        await using var ui = new UiSession(); var detail = WorkFixture.Detail();
        detail.Sections[0].Data.Rows.Add(new[] { "Texte saisi littéralement", "null" });
        await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision }, detail);
        Check(await NullTextNodes(ui.Page) == 0, "Absent reader notices and optional timeline blocks must not render null text nodes.");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Consulter les observations", Exact = true }).ClickAsync();
        await ui.Page.Locator(".work-reader-body select").First.SelectOptionAsync(new SelectOptionValue { Label = "Fiche (3)" });
        await ui.Page.GetByRole(AriaRole.Cell, new() { Name = "Texte saisi littéralement", Exact = true }).WaitForAsync();
        Check(await NullTextNodes(ui.Page) == 1 && await ui.Page.GetByRole(AriaRole.Cell, new() { Name = "null", Exact = true }).CountAsync() == 1,
            "The literal user value null must remain present in its cell, without unrelated placeholders.");
        await ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Observations du ticket", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await ui.Page.SetViewportSizeAsync(900, 900);
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Actions assistées", Exact = true }).ClickAsync();
        await ui.Page.GetByLabel("Adaptateur de formulaire", new() { Exact = true }).WaitForAsync();
        Check(await NullTextNodes(ui.Page) == 0, "An absent assisted-action warning must not appear as null.");
        await ui.Page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        Check(await ui.Page.EvaluateAsync<bool>("""
            () => { const bar = document.querySelector('.tickets-actionbar'), bounds = bar.getBoundingClientRect();
              return [...bar.querySelectorAll('button')].every(button=>{const box=button.getBoundingClientRect();return box.left>=bounds.left-1&&box.right<=bounds.right+1;}); }
            """), "The working-panel actions wrap within their available width at compact size.");
        Check(await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Actions assistées", Exact = true }).GetAttributeAsync("aria-haspopup") == "dialog"
            && await ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Actions assistées", Exact = true }).IsVisibleAsync(),
            "The contextual action identifies its accessible dialog instead of exposing an old reader page."); await ui.NoErrors();
    }
    private static async Task TicketIdentifierLayout()
    {
        await using var ui = new UiSession(); const string originalId = "8\u202f765\u202f432";
        ui.Fixture.Source.Rows[0][0] = originalId; ui.Fixture.App.LoadFixture(ui.Fixture.Source);
        var detail = WorkFixture.Detail(); detail.Ticket = new("8765432", WorkFixture.TicketUrl.Replace("42", "8765432", StringComparison.Ordinal));
        await ui.Start("records", detail: detail);
        var ticket = ui.Page.Locator(".tickets-record-grid tbody tr").First;
        Check(await ticket.Locator(".tickets-row-id").InnerTextAsync() == "#8765432", "A ticket identifier is a digit string, without numeric grouping separators.");
        foreach (var width in new[] { 1480, 900 })
        {
            await ui.Page.SetViewportSizeAsync(width, 900);
            Check(await ticket.Locator(".tickets-row-id").EvaluateAsync<bool>("""
                label => { const range = document.createRange(); range.selectNodeContents(label);
                  const rects = [...range.getClientRects()]; const cell = label.closest('td').getBoundingClientRect();
                  return rects.length === 1 && rects[0].left >= cell.left && rects[0].right <= cell.right + 1; }
                """), "A seven-digit ticket button must fit on one line within its column at desktop and compact widths.");
        }
        await ticket.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync(); await ui.Page.Locator(".work-ticket-heading h2").WaitForAsync();
        Check((await ui.Page.Locator(".work-ticket-heading h2").InnerTextAsync()).StartsWith("#8765432 ·", StringComparison.Ordinal)
            && ui.Fixture.Source.Rows[0][0] == originalId, "Opening a display-normalized ID keeps the original source value and row identity.");
        await ui.Page.SetViewportSizeAsync(1480, 900); await ui.Capture("tickets-desktop.png");
        await ui.Page.SetViewportSizeAsync(900, 900); await ui.Capture("tickets-compact.png");
        await ui.NoErrors();
    }
    private static async Task UnifiedTicketList()
    {
        await using var ui = new UiSession();
        ui.Fixture.Source.Rows = Enumerable.Range(0, 140).Select(i => new[] { (1000000 + i).ToString(), "Conservation réseau · ticket " + i, "Nouveau", "Normale", "Réseau", "Alice", "2026-10-26T12:00:00", "Site synthétique" }).ToList();
        ui.Fixture.Source.ExpectedTotal = 140; ui.Fixture.App.Settings.TicketDetails.AutoLoadOnSelection = true; ui.Fixture.App.LoadFixture(ui.Fixture.Source);
        await ui.Start("records", cacheDetail: false);
        var list = ui.Page.Locator(".tickets-list-pane");
        await list.GetByLabel("Recherche du relevé", new() { Exact = true }).FillAsync("Conservation");
        await ui.Page.WaitForFunctionAsync("()=>workLastRecords?.args.search==='Conservation'");
        await list.GetByLabel("Filtrer Statut", new() { Exact = true }).FillAsync("Nouveau");
        await ui.Page.WaitForFunctionAsync("()=>workLastRecords?.args.transform.filters.some(f=>f.column===2&&f.value==='Nouveau')");
        await list.GetByRole(AriaRole.Button, new() { Name = "ID", Exact = true }).ClickAsync();
        await list.GetByRole(AriaRole.Button, new() { Name = "ID ↑ 1", Exact = true }).ClickAsync();
        await list.GetByRole(AriaRole.Button, new() { Name = "ID ↓ 1", Exact = true }).WaitForAsync();
        await list.Locator("select").SelectOptionAsync("50");
        await ui.Page.WaitForFunctionAsync("()=>workLastRecords?.result.limit===50");
        await list.GetByRole(AriaRole.Button, new() { Name = "Suivant", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>workLastRecords?.result.offset===50");
        var row = list.Locator("tbody tr").Nth(8);
        var originalIndex = int.Parse((await row.GetAttributeAsync("data-focus-key"))![4..]);
        var expectedId = ui.Fixture.Source.Rows[originalIndex][0];
        await row.GetByRole(AriaRole.Checkbox).CheckAsync();
        await list.GetByRole(AriaRole.Button, new() { Name = "Ajouter à ma file", Exact = true }).ClickAsync();
        Check(WorkplaceStore.Load(Path.Combine(ui.Fixture.Folder, "workplace.json")).Tickets.Single(t => t.QueueOrder.HasValue).Id == expectedId,
            "A selected source occurrence adds its actual ticket to the personal queue without changing the source.");
        await ui.Page.EvaluateAsync("""
            () => { const list=document.querySelector('.tickets-list-pane'), grid=list.querySelector('.table-scroll');
              grid.scrollTop=220;grid.scrollLeft=250;
              list.addEventListener('click',event=>{const opener=event.target.closest('button[data-focus-key^="open-"]');if(!opener)return;
                window.workListBefore={grid,search:list.querySelector('[aria-label="Recherche du relevé"]'),filter:list.querySelector('[aria-label="Filtrer Statut"]'),
                  top:grid.scrollTop,left:grid.scrollLeft,opener,pagination:list.querySelector('.pagination').textContent,
                  selected:[...list.querySelectorAll('tbody input:checked')].map(node=>node.getAttribute('aria-label'))};},true); }
            """);
        await row.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        await ui.Page.Locator(".work-ticket-heading h2").WaitForAsync();
        Check((await ui.Page.Locator(".work-ticket-heading h2").InnerTextAsync()).StartsWith("#" + expectedId + " ·", StringComparison.Ordinal)
            && await ui.Page.EvaluateAsync<bool>("()=>workPage==='records'&&!document.querySelector('.tickets-list-pane').hidden&&!document.querySelector('.tickets-detail-pane').hidden&&document.querySelector('.tickets-workspace').classList.contains('has-detail')"),
            "Opening a sorted and paged row keeps its original index and exposes an adjacent working panel beside the same list.");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Ticket suivant", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("id=>document.querySelector('.work-ticket-heading h2')?.textContent.startsWith('#'+id+' ·')", ui.Fixture.Source.Rows[originalIndex - 1][0]);
        Check((await ui.Page.Locator(".work-ticket-heading h2").InnerTextAsync()).StartsWith("#" + ui.Fixture.Source.Rows[originalIndex - 1][0] + " ·", StringComparison.Ordinal));
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Ticket précédent", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("id=>document.querySelector('.work-ticket-heading h2')?.textContent.startsWith('#'+id+' ·')", expectedId);
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Notes et rappels", Exact = true }).ClickAsync();
        await ui.Page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync("Texte personnel encore non enregistré");
        await ui.Page.EvaluateAsync("()=>window.workNoteBefore=document.querySelector('[aria-label=\"Mes notes\"]')||[...document.querySelectorAll('textarea')].find(node=>node.value==='Texte personnel encore non enregistré')");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Retour à la liste", Exact = true }).ClickAsync();
        await ui.Page.EvaluateAsync("()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
        Check(await ui.Page.EvaluateAsync<bool>("""
            () => {const list=document.querySelector('.tickets-list-pane'),before=workListBefore,grid=list.querySelector('.table-scroll');
              return !list.hidden&&grid===before.grid&&before.search===list.querySelector('[aria-label="Recherche du relevé"]')&&before.search.value==='Conservation'
                &&before.filter===list.querySelector('[aria-label="Filtrer Statut"]')&&before.filter.value==='Nouveau'
                &&list.querySelector('.pagination').textContent===before.pagination&&Math.abs(grid.scrollTop-before.top)<=1&&Math.abs(grid.scrollLeft-before.left)<=1
                &&JSON.stringify([...list.querySelectorAll('tbody input:checked')].map(node=>node.getAttribute('aria-label')))===JSON.stringify(before.selected)
                &&document.activeElement===before.opener;}
            """), "Returning preserves the same list DOM, query, column filter, sort, page, selection, both scroll axes and opener focus.");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Reprendre la fiche", Exact = true }).ClickAsync();
        Check(await ui.Page.GetByLabel("Mes notes", new() { Exact = true }).InputValueAsync() == "Texte personnel encore non enregistré"
            && await ui.Page.EvaluateAsync<bool>("()=>workNoteBefore.isConnected&&workNoteBefore.value==='Texte personnel encore non enregistré'"),
            "A list round-trip keeps the same personal editor and its unsaved text.");
        await ui.Capture("tickets-workspace-reader-desktop.png");
        await ui.Page.SetViewportSizeAsync(1000, 760);
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Retour à la liste", Exact = true }).ClickAsync();
        await ui.Capture("tickets-workspace-list-compact.png");
        Check(ui.Operations.All(op => op is not ("workplace.ticket.read" or "workplace.ticket.browser" or "workplace.ticket.tab")) && ui.ExternalRequests.Count == 0,
            "Opening, moving and returning never start a GLPI read, including with the legacy automatic-read setting enabled.");
        await ui.NoErrors();
    }
    private static async Task SavedTicketJourney()
    {
        await using var ui = new UiSession(); ui.Fixture.App.ReadFixture(WorkFixture.Detail());
        var key = await ui.Fixture.Key();
        await ui.Fixture.Call("workplace.personal.save", new { ticketKey = key, notes = "Repère personnel conservé", pinned = true });
        ui.Fixture.Source.Rows.RemoveAt(0); ui.Fixture.Source.ExpectedTotal = 3;
        ui.Fixture.App.Settings.TicketDetails.AutoLoadOnSelection = true; ui.Fixture.App.LoadFixture(ui.Fixture.Source);
        await ui.Start("tickets", cacheDetail: false);
        Check(await ui.Page.Locator(".tickets-record-grid tbody tr").CountAsync() == 3
            && (await ui.Page.Locator(".work-ticket-heading h2").InnerTextAsync()).StartsWith("#42 ·", StringComparison.Ordinal),
            "The legacy ticket entry resumes its dossier inside the same workspace and leaves the current source list intact.");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Mes dossiers", Exact = true }).ClickAsync();
        await ui.Page.GetByLabel("Rechercher dans les dossiers conservés", new() { Exact = true }).FillAsync("Repère personnel");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Rechercher", Exact = true }).ClickAsync();
        Check(await ui.Page.Locator(".tickets-saved-item").CountAsync() == 1);
        await ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Mes dossiers", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Retour à la liste", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>!document.querySelector('.tickets-list-pane').hidden&&document.querySelectorAll('.tickets-record-grid tbody tr').length===3");
        Check(await ui.Page.Locator(".tickets-record-grid tbody tr").CountAsync() == 3);
        await ui.Page.Locator(".tickets-record-grid tbody tr").First.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>document.querySelector('.work-ticket-heading h2')?.textContent.startsWith('#43 ·')");
        Check((await ui.Page.Locator(".work-ticket-heading h2").InnerTextAsync()).StartsWith("#43 ·", StringComparison.Ordinal)
            && await ui.Page.GetByText("Fiche non lue", new() { Exact = true }).CountAsync() == 1,
            "An unread source row opens its exact local dossier and exposes an explicit refresh action.");
        await ui.Navigate("tickets", new { ticketKey = key, section = "personal" });
        Check(await ui.Page.GetByLabel("Mes notes", new() { Exact = true }).InputValueAsync() == "Repère personnel conservé",
            "A ticketKey deep link still restores personal work after source rows change.");
        Check(ui.Operations.All(op => op is not ("workplace.ticket.read" or "workplace.ticket.browser" or "workplace.ticket.tab")) && ui.ExternalRequests.Count == 0);
        await ui.NoErrors();
    }
    private static async Task SimulatedTicketJourney()
    {
        await using var ui = new UiSession(); ui.Fixture.App.ReadFixture(WorkFixture.Detail());
        var realKey = await ui.Fixture.Key();
        await ui.Fixture.Call("workplace.personal.save", new { ticketKey = realKey, notes = "Travail réel à conserver", queued = true });
        var realWorkplace = await File.ReadAllBytesAsync(Path.Combine(ui.Fixture.Folder, "workplace.json"));
        var realRows = JsonSerializer.Serialize(ui.Fixture.Source.Rows);
        var csv = Path.Combine(ui.Fixture.Folder, "tickets-simulation.csv");
        await File.WriteAllTextAsync(csv, "ID;Titre;Statut;Priorité;Catégorie;Technicien;Échéance;Lieu\r\n7000001;Premier ticket local;Nouveau;Normale;Réseau;Agent fictif;;Site fictif\r\n7000002;Second ticket local;En attente;Normale;Réseau;Agent fictif;;Site fictif\r\n", new System.Text.UTF8Encoding(true));
        var originalFile = await File.ReadAllBytesAsync(csv); ui.Fixture.Host.OpenPath = csv;
        await ui.Fixture.Call("records.test.import", new { });
        await ui.Start("records", cacheDetail: false);
        var list = ui.Page.Locator(".tickets-list-pane");
        Check(await list.Locator("tbody tr").CountAsync() == 2);
        await list.GetByLabel("Sélectionner la ligne 2", new() { Exact = true }).CheckAsync();
        await list.Locator("tbody tr").Nth(1).GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        Check((await ui.Page.Locator(".work-ticket-heading h2").InnerTextAsync()).StartsWith("#7000002 ·", StringComparison.Ordinal));
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Notes et rappels", Exact = true }).ClickAsync();
        await ui.Page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync("Note isolée de simulation");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer mon travail", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>workNotices.some(n=>n.text==='Travail personnel enregistré dans la simulation.')");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Actions assistées", Exact = true }).ClickAsync();
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Inspecter les formulaires simulés", Exact = true }).ClickAsync();
        await ui.Page.GetByLabel("Adaptateur de formulaire", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Changer le statut (simulation)" });
        await ui.Page.GetByLabel("Statut simulé", new() { Exact = true }).SelectOptionAsync("5");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Prévisualiser l’action…", Exact = true }).ClickAsync();
        var preview = ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Relire la modification simulée", Exact = true }); await preview.WaitForAsync();
        var before = await ui.Fixture.Call("records.list", new { revision = ui.Fixture.App.Revision });
        Check(before.GetProperty("rows")[1].GetProperty("cells")[2].GetString() == "En attente",
            "The status preview alone never changes its imported occurrence.");
        await preview.GetByRole(AriaRole.Button, new() { Name = "Appliquer dans la simulation…", Exact = true }).ClickAsync();
        var confirmation = ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Modifier le ticket simulé ?", Exact = true }); await confirmation.WaitForAsync();
        await confirmation.GetByRole(AriaRole.Button, new() { Name = "Appliquer la simulation", Exact = true }).ClickAsync();
        await ui.Page.GetByText("Modification simulée appliquée", new() { Exact = true }).WaitForAsync();
        await ui.Page.WaitForFunctionAsync("()=>document.querySelector('.tickets-record-grid')?.textContent.includes('Résolu')");
        await ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Actions assistées", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Retour à la liste", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>workLastRecords?.result.rows.some(row=>row.cells[0]==='7000002'&&row.cells[2]==='Résolu')");
        await ui.Page.WaitForFunctionAsync("()=>document.querySelector('.tickets-record-grid')?.textContent.includes('Résolu')");
        Check(await list.Locator(".tickets-record-grid tbody tr").CountAsync() == 2 && await list.Locator(".tickets-record-grid tbody input:checked").CountAsync() == 0
            && await ui.Page.EvaluateAsync<bool>("()=>workNotices.some(n=>n.text.includes('anciennes sélections'))"),
            "A simulated action rebuilds the current source list, clears obsolete row selections and never duplicates the imported tickets.");
        Check((await File.ReadAllBytesAsync(csv)).SequenceEqual(originalFile)
            && (await File.ReadAllBytesAsync(Path.Combine(ui.Fixture.Folder, "workplace.json"))).SequenceEqual(realWorkplace)
            && JsonSerializer.Serialize(ui.Fixture.Source.Rows) == realRows,
            "Simulated status and personal edits leave the input CSV, real source and real personal store byte-for-byte intact.");
        await ui.Capture("tickets-workspace-simulation-return.png");
        // Finish the local reader before leaving its owning session, as the real
        // application does when closing the simulation.
        await ui.Navigate("day");
        await ui.Page.EvaluateAsync("async()=>await workApi.call('records.test.exit')");
        await ui.Navigate("records");
        Check(await ui.Page.Locator(".tickets-record-grid tbody tr").CountAsync() == 4
            && await ui.Page.EvaluateAsync<bool>("()=>!workState.session.testMode")
            && (await File.ReadAllBytesAsync(Path.Combine(ui.Fixture.Folder, "workplace.json"))).SequenceEqual(realWorkplace),
            "After exit, the exact original list and personal workspace are restored.");
        Check(ui.ExternalRequests.Count == 0); await ui.NoErrors();
    }
    private static async Task PersonalPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision, section = "personal" });
        await ui.Page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync("Note depuis la vraie interface WebView"); await ui.Page.GetByLabel("Épingler ce ticket", new() { Exact = true }).CheckAsync();
        await ui.Page.GetByText("Checklist, rappels et conservation", new() { Exact = true }).ClickAsync();
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Ajouter une action", Exact = true }).ClickAsync(); await ui.Page.GetByLabel("Action personnelle", new() { Exact = true }).FillAsync("Vérification terrain");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer mon travail", Exact = true }).ClickAsync(); await ui.Page.WaitForFunctionAsync("()=>workNotices.some(n=>n.text==='Travail personnel enregistré.')");
        var p = WorkplaceStore.Load(Path.Combine(ui.Fixture.Folder, "workplace.json")).Tickets.Single(); Check(p.Pinned && p.Notes.Contains("WebView") && p.Checklist.Single().Text == "Vérification terrain"); await ui.NoErrors();
    }
    private static async Task DraftPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision, section = "draft" });
        await ui.Page.GetByLabel("Constat", new() { Exact = true }).FillAsync("Constat de test réel"); await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Composer le brouillon", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>document.querySelector('textarea[maxlength=\"20000\"]')?.value.includes('Constat de test réel')");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Copier le brouillon", Exact = true }).ClickAsync(); Check(ui.Fixture.Host.Clipboard.Contains("Constat de test réel")); await ui.NoErrors();
    }
    private static async Task DraftNavigation()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision, section = "draft" });
        await ui.Page.GetByLabel("Texte modifiable", new() { Exact = true }).FillAsync("Dernière frappe avant navigation immédiate");
        await ui.Navigate("day");
        Check(WorkplaceStore.Load(Path.Combine(ui.Fixture.Folder, "workplace.json")).Tickets.Single().FollowupDraft == "Dernière frappe avant navigation immédiate");
        await ui.Navigate("tickets", new { ticketKey = await ui.Fixture.Key(), section = "draft" });
        await ui.Page.EvaluateAsync("""
            () => {
              const actual=workApi.call;window.draftSaveAttempts=0;
              workApi.call=async(op,args)=>{if(op==='workplace.draft.save'&&++draftSaveAttempts===1)throw new Error('Sauvegarde refusée pour le contrôle');return actual(op,args);};
              const modal=document.querySelector('.tickets-tool-modal'),draft=modal.querySelector('textarea[maxlength="20000"]');
              draft.value='Brouillon conservé après un refus temporaire';draft.dispatchEvent(new Event('input',{bubbles:true}));
              modal.querySelector('.modal-heading button').click();
            }
            """);
        await ui.Page.WaitForFunctionAsync("()=>workNotices.some(n=>n.text.includes('Sauvegarde refusée pour le contrôle'))");
        var draftDialog = ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Brouillon de suivi", Exact = true });
        Check(await draftDialog.IsVisibleAsync() && await draftDialog.GetByLabel("Texte modifiable", new() { Exact = true }).InputValueAsync() == "Brouillon conservé après un refus temporaire"
            && await draftDialog.Locator(".tickets-tool-content").EvaluateAsync<bool>("node=>!node.inert")
            && WorkplaceStore.Load(Path.Combine(ui.Fixture.Folder, "workplace.json")).Tickets.Single().FollowupDraft == "Dernière frappe avant navigation immédiate",
            "A failed draft save keeps the same editable dialog and does not replace the previously stored text.");
        await draftDialog.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).ClickAsync();
        await draftDialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await ui.Navigate("day");
        Check(WorkplaceStore.Load(Path.Combine(ui.Fixture.Folder, "workplace.json")).Tickets.Single().FollowupDraft == "Brouillon conservé après un refus temporaire"
            && await ui.Page.EvaluateAsync<int>("()=>draftSaveAttempts") == 2,
            "A new explicit close retries the refused save once, then navigation does not save it again.");
        await ui.Page.EvaluateAsync("()=>{window.workNotices=workNotices.filter(n=>!n.text.includes('Sauvegarde refusée pour le contrôle'));}");
        await ui.NoErrors();
    }
    private static async Task LongTextPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision });
        var detail = WorkFixture.Detail(); var full = new string('x', 15000) + "<script>window.badWorkplace=true</script> · fin observée"; detail.Sections[1].Data.Rows[0][3] = full; ui.Fixture.App.ReadFixture(detail);
        await ui.Navigate("tickets", new { ticketKey = await ui.Fixture.Key(), section = "reader" });
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Consulter les observations", Exact = true }).ClickAsync();
        await ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Observations du ticket", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Lire le texte complet", Exact = true }).ClickAsync();
        Check(await ui.Page.GetByRole(AriaRole.Dialog, new() { Name = "Traitement · Contenu", Exact = true }).Locator("pre").InnerTextAsync() == full && await ui.Page.EvaluateAsync<bool>("()=>!window.badWorkplace")); await ui.NoErrors();
    }
    private static async Task CalendarPage()
    {
        await using var ui = new UiSession(); await ui.Start("technicians", new { technicianKey = "Alice" });
        var dialog = ui.Page.GetByRole(AriaRole.Dialog); await dialog.GetByLabel("Capacité quotidienne (minutes)", new() { Exact = true }).FillAsync("300");
        await dialog.GetByLabel("Compétences (une par ligne)", new() { Exact = true }).FillAsync("Réseau\nPoste de travail");
        var periods = dialog.Locator("section").Filter(new() { HasText = "Plages hebdomadaires de présence" }); await periods.GetByRole(AriaRole.Button, new() { Name = "Ajouter une ligne", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Enregistrer les déclarations", Exact = true }).ClickAsync(); await ui.Page.WaitForFunctionAsync("()=>workNotices.some(n=>n.text==='Déclarations enregistrées.')");
        var p = TechnicianPreferences.Load(Path.Combine(ui.Fixture.Folder, "technician-preferences.json")).Get("Alice"); Check(p.DailyCapacityMinutes == 300 && p.WorkPeriods.Count == 1 && p.Skills.Count == 2); await ui.NoErrors();
    }
    private static async Task PlanningPage()
    {
        await using var ui = new UiSession(); await ui.Start("planning"); await ui.Page.GetByRole(AriaRole.Button, new() { Name = "42", Exact = true }).ClickAsync();
        await ui.Page.GetByLabel("Minutes prévues", new() { Exact = true }).FillAsync("75"); await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer cette proposition locale", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>workNotices.some(n=>n.text==='Proposition locale enregistrée.')"); var p = TechnicianPlanningService.Load(Path.Combine(ui.Fixture.Folder, "dispatch-scenario.json")).Assignments.Single(); Check(p.TicketId == "42" && p.EstimatedMinutes == 75); await ui.NoErrors();
    }
    private static async Task EnrichmentPage()
    {
        await using var ui = new UiSession(); await ui.Fixture.Call("workplace.enrichment.prepare", new { revision = ui.Fixture.App.Revision, rows = new[] { 0, 1 } }); await ui.Start("enrichment");
        Check(await ui.Page.GetByText("Non lu", new() { Exact = true }).CountAsync() == 2); await ui.Page.GetByLabel("Lectures par lancement (1 à 100)", new() { Exact = true }).FillAsync("1");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer les réglages", Exact = true }).ClickAsync();
        var q = TicketEnrichmentService.Load(Path.Combine(ui.Fixture.Folder, "ticket-enrichment.json")); Check(q.Limit == 1 && q.Results.Count == 0); await ui.NoErrors();
    }
    private static async Task DayPage()
    {
        await using var ui = new UiSession(); await ui.Fixture.Call("workplace.technicians.me", new { technicianKey = "Alice" }); await ui.Start("day");
        Check(await ui.Page.GetByText("Mon identité : Alice", new() { Exact = false }).CountAsync() == 1);
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Modèles de listes", Exact = true }).ClickAsync(); await ui.Page.GetByLabel("Nom du modèle", new() { Exact = true }).FillAsync("Vérification UI"); await ui.Page.GetByLabel("Une action par ligne (40 maximum)", new() { Exact = true }).FillAsync("Une action\nUn contrôle"); await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer le modèle", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>workNotices.some(n=>n.text==='Modèle enregistré.')"); Check(WorkplaceStore.Load(Path.Combine(ui.Fixture.Folder, "workplace.json")).ChecklistTemplates["Vérification UI"].Count == 2); await ui.NoErrors();
    }
    private static async Task InsightsPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision, section = "insights" });
        Check(await ui.Page.GetByText("Indéterminé", new() { Exact = true }).CountAsync() > 0); var foreign = ui.Page.Locator("tr").Filter(new() { HasText = "Lien étranger" }); Check(await foreign.GetByRole(AriaRole.Button, new() { Name = "Consulter", Exact = true }).IsDisabledAsync());
        Check(await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Télécharger…", Exact = true }).CountAsync() == 1); await ui.NoErrors();
    }
}

internal static class WorkJsonExtensions
{ public static string Serialize(this object? value) => JsonSerializer.Serialize(value, PilotApplication.Json); }
