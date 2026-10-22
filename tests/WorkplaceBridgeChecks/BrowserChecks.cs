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
            ("WebView notes checklist and reminder save through real handler", PersonalPage),
            ("WebView draft composition persists and clipboard uses actual stored text", DraftPage),
            ("WebView immediate navigation waits for its last draft save", DraftNavigation),
            ("WebView long collected text opens intact as inert text", LongTextPage),
            ("WebView calendar typed controls save real technician capacity", CalendarPage),
            ("WebView local dispatch persists an explicit proposal", PlanningPage),
            ("WebView enrichment restoration does not start network reads", EnrichmentPage),
            ("WebView Ma journée uses personal identity and live stored templates", DayPage),
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
        public async Task Start(string page, object? args = null)
        {
            Fixture.App.ReadFixture(WorkFixture.Detail()); _context = await _browser!.NewContextAsync(new() { ViewportSize = new() { Width = 1480, Height = 1050 } }); Page = await _context.NewPageAsync(); Page.PageError += (_, error) => Errors.Add(error);
            var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
            await Page.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url); if (uri.Host != "workplace-ui.test") { await route.AbortAsync(); return; }
                if (uri.AbsolutePath == "/") { await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><html lang='fr'><meta charset='utf-8'><link rel='stylesheet' href='/css/app.css'><link rel='stylesheet' href='/css/workplace.css'><main id='root'></main><div id='notices'></div></html>" }); return; }
                var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); var path = Path.GetFullPath(Path.Combine(web, relative));
                if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) { await route.FulfillAsync(new() { Status = 404, Body = "Not found" }); return; }
                await route.FulfillAsync(new() { ContentType = path.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/css", Body = await File.ReadAllTextAsync(path) });
            });
            await Page.ExposeFunctionAsync("workplaceRuntime", async (string operation, string json) => (await Fixture.App.ExecuteAsync(operation, JsonDocument.Parse(json).RootElement)).Serialize());
            await Page.GotoAsync("http://workplace-ui.test/");
            var state = JsonSerializer.Serialize(new { session = new { loaded = true, revision = Fixture.App.Revision }, settings = Fixture.App.Settings, version = "test" }, PilotApplication.Json);
            await Page.EvaluateAsync("""
                async state => {
                  const {pages}=await import('/js/pages/workplace.js');
                  const listeners=new Map(); let cleanup, controller;
                  window.workState=JSON.parse(state);window.workNotices=[];window.workReady=false;
                  window.workApi={call:async(op,args={})=>JSON.parse(await workplaceRuntime(op,JSON.stringify(args))),on:(name,handler)=>{listeners.set(name,handler);return()=>listeners.delete(name);}};
                  window.workNavigate=async(id,params={})=>{await cleanup?.();cleanup=null;controller?.abort();controller=new AbortController(); document.querySelectorAll('dialog').forEach(d=>d.remove());const root=document.querySelector('#root');root.replaceChildren();window.workReady=false;
                    const page=pages.find(p=>p.id===id);if(!page){window.workDestination={id,params};return;}
                    cleanup=await page.render({root,api:workApi,state:workState,signal:controller.signal,navigate:workNavigate,params,refresh:()=>workNavigate(id,params),notify:(text,tone)=>{workNotices.push({text,tone});document.querySelector('#notices').textContent=text;}});window.workReady=true;};
                }
                """, state);
            await Navigate(page, args);
        }
        public async Task Navigate(string page, object? args = null)
        { await Page.EvaluateAsync("async ({page,args})=>await workNavigate(page,args)", new { page, args = args ?? new { } }); await Page.WaitForFunctionAsync("()=>workReady"); }
        public async Task NoErrors() { Check(Errors.Count == 0, string.Join("\n", Errors)); Check(!await Page.EvaluateAsync<bool>("()=>workNotices.some(n=>n.tone==='error'||n.tone==='danger')"), await Page.EvaluateAsync<string>("()=>JSON.stringify(workNotices)")); }
        public async ValueTask DisposeAsync() { if (_context is not null) await _context.DisposeAsync(); await Fixture.DisposeAsync(); }
    }
    private static async Task TicketPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision });
        Check(await ui.Page.GetByText("Réponse visible", new() { Exact = true }).CountAsync() > 0);
        await ui.Page.GetByText("Filtres de chronologie", new() { Exact = true }).ClickAsync(); await ui.Page.GetByLabel("Type d’événement", new() { Exact = true }).FillAsync("Suivi"); await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Appliquer les filtres", Exact = true }).ClickAsync();
        await ui.Page.WaitForFunctionAsync("()=>document.querySelector('.work-section-content')?.textContent.includes('Observation non datée')");
        Check(!await ui.Page.Locator(".work-section-content").InnerTextAsync().ContinueWith(t => t.Result.Contains("Préparer")));
        await ui.Navigate("tickets", new { ticketKey = await ui.Fixture.Key() }); Check(await ui.Page.GetByLabel("Type d’événement", new() { Exact = true }).InputValueAsync() == "Suivi"); await ui.NoErrors();
    }
    private static async Task PersonalPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision, section = "personal" });
        await ui.Page.GetByLabel("Mes notes", new() { Exact = true }).FillAsync("Note depuis la vraie interface WebView"); await ui.Page.GetByLabel("Épingler ce ticket", new() { Exact = true }).CheckAsync();
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
        Check(WorkplaceStore.Load(Path.Combine(ui.Fixture.Folder, "workplace.json")).Tickets.Single().FollowupDraft == "Dernière frappe avant navigation immédiate"); await ui.NoErrors();
    }
    private static async Task LongTextPage()
    {
        await using var ui = new UiSession(); await ui.Start("tickets", new { rowIndex = 0, revision = ui.Fixture.App.Revision });
        var detail = WorkFixture.Detail(); var full = new string('x', 15000) + "<script>window.badWorkplace=true</script> · fin observée"; detail.Sections[1].Data.Rows[0][3] = full; ui.Fixture.App.ReadFixture(detail);
        await ui.Navigate("tickets", new { ticketKey = await ui.Fixture.Key(), section = "reader" }); await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Lire le texte complet", Exact = true }).ClickAsync();
        Check(await ui.Page.GetByRole(AriaRole.Dialog).Locator("pre").InnerTextAsync() == full && await ui.Page.EvaluateAsync<bool>("()=>!window.badWorkplace")); await ui.NoErrors();
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
