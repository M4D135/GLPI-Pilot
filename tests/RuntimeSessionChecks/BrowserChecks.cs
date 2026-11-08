using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

public static class SessionBrowserChecks
{
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    public static async Task<IReadOnlyList<(string Name, Func<Task> Run)>> Create(string executable)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { ExecutablePath = executable, Headless = true, Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
        return new (string, Func<Task>)[]
        {
            ("Inspection uses the chosen page object when an earlier browser tab closes", StablePageChoice),
            ("A refreshed page list and a source change both invalidate earlier browser choices", StaleChoices),
            ("Native navigation invalidates inspected and selected tables before extraction", ChangedSearch),
            ("Manual extraction reads the selected real table without inventing verified provenance", ManualExtraction),
            ("Application shutdown drains handlers before closing the authenticated browser context", BrowserDrain),
            ("The actual draft page retries its final text after a queued save is cancelled for closing", () => DraftCloseFlush(false)),
            ("A failed final draft save leaves the editor usable and permits an acknowledged retry", () => DraftCloseFlush(true))
        };
    }
    public static async Task DisposeAsync()
    { if (_browser is not null) await _browser.DisposeAsync(); _playwright?.Dispose(); }
    private static void Check(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Refuse(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        { Check(error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), error.Message); return; }
        throw new InvalidOperationException("Expected refusal: " + expected);
    }
    private static async Task Cancelled(Task task)
    { try { await task; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Expected cancellation."); }
    private static async Task<(SessionFixture Fixture, IBrowserContext Context, IPage Page)> Open(bool secondPage = false)
    {
        var context = await _browser!.NewContextAsync();
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (url.Host != "session-glpi.test" || route.Request.Method != "GET") { await route.AbortAsync(); return; }
            var suffix = url.Query.Contains("query=B", StringComparison.Ordinal) ? "B" : "A";
            await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><meta charset='utf-8'><title>Recherche " + suffix + "</title><a href='/glpi/front/logout.php'>Déconnexion</a><table id='results'><tr><th>ID</th><th>Titre " + suffix + "</th><th>Statut</th></tr><tr><td>42</td><td>Observation " + suffix + "</td><td>Nouveau</td></tr></table>" });
        });
        var page = await context.NewPageAsync(); await page.GotoAsync(SessionFixture.SourceA);
        if (secondPage) { var second = await context.NewPageAsync(); await second.GotoAsync(SessionFixture.SourceB); }
        var service = new BrowserService(); service.AttachForTesting(context, SessionFixture.SourceA);
        return (new SessionFixture(service), context, page);
    }
    private static long Generation(JsonElement page) => page.GetProperty("revision").GetInt64();
    private static async Task<JsonElement> Inspect(SessionFixture fixture, int pageIndex = 0)
    {
        var pages = (await fixture.Call("collect.pages")).GetProperty("pages");
        return await fixture.Call("collect.inspect", new { page = pageIndex, revision = Generation(pages[pageIndex]) });
    }
    private static async Task StablePageChoice()
    {
        var opened = await Open(true); await using var f = opened.Fixture;
        var pages = (await f.Call("collect.pages")).GetProperty("pages"); Check(pages.GetArrayLength() == 2);
        var chosen = pages[1]; await opened.Page.CloseAsync();
        var tables = await f.Call("collect.inspect", new { page = chosen.GetProperty("index").GetInt32(), revision = Generation(chosen) });
        Check(tables.GetArrayLength() == 1 && tables[0].GetProperty("headers")[1].GetString() == "Titre B");
        Check(opened.Context.Pages.Count == 1 && opened.Context.Pages[0].Url == SessionFixture.SourceB);
    }
    private static async Task StaleChoices()
    {
        var opened = await Open(); await using var f = opened.Fixture;
        var old = (await f.Call("collect.pages")).GetProperty("pages")[0]; await f.Call("collect.pages");
        await Refuse(() => f.Call("collect.inspect", new { page = 0, revision = Generation(old) }), "sélection");
        var tables = await Inspect(f); var revision = Generation(tables[0]); await f.SelectB();
        await Refuse(() => f.Call("collect.table", new { index = 0, revision, highlight = false }), "sélection");
        await Refuse(() => f.Call("collect.manual", new { revision, followingPages = false }), "sélection");
    }
    private static async Task ChangedSearch()
    {
        var opened = await Open(); await using var f = opened.Fixture;
        var pages = (await f.Call("collect.pages")).GetProperty("pages"); await opened.Page.GotoAsync(SessionFixture.SourceB);
        await Refuse(() => f.Call("collect.inspect", new { page = 0, revision = Generation(pages[0]) }), "recherche");
        var tables = await Inspect(f); await opened.Page.GotoAsync(SessionFixture.SourceA);
        await Refuse(() => f.Call("collect.table", new { index = 0, revision = Generation(tables[0]), highlight = false }), "recherche");
        tables = await Inspect(f); var selected = await f.Call("collect.table", new { index = 0, revision = Generation(tables[0]), highlight = false });
        var previous = f.App.Result; await opened.Page.GotoAsync(SessionFixture.SourceB);
        await Refuse(() => f.Call("collect.manual", new { revision = Generation(selected), followingPages = false }), "recherche");
        Check(ReferenceEquals(previous, f.App.Result));
    }
    private static async Task ManualExtraction()
    {
        var opened = await Open(); await using var f = opened.Fixture;
        var tables = await Inspect(f); var selected = await f.Call("collect.table", new { index = 0, revision = Generation(tables[0]), highlight = false });
        await f.Call("collect.manual", new { revision = Generation(selected), followingPages = false });
        Check(f.App.Result is { } result && result.Rows.Count == 1 && result.Rows[0][1] == "Observation A" && result.SourceUrl == AppPaths.SafeUrl(SessionFixture.SourceA), "The exact selected cells and the redacted source must be retained.");
        Check(!f.App.Result!.Provenance.SearchVerified && !f.App.Result.Provenance.ScopeVerified);
        var ticket = await f.Call("workplace.ticket.open", new { rowIndex = 0, revision = f.App.Revision });
        await Refuse(() => f.Call("workplace.actions.inspect", new { ticketKey = ticket.GetProperty("personal").GetProperty("key").GetString() }), "actuel");
    }
    private static async Task BrowserDrain()
    {
        var opened = await Open(); await using var f = opened.Fixture; var began = Signal(); var cleanup = Signal(); var release = Signal();
        f.App.RegisterSessionFixture("test.drain", async (_, ct) =>
        { began.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); return null; } finally { cleanup.TrySetResult(); await release.Task; Check(!opened.Page.IsClosed, "Browser closed before handler cleanup."); } });
        var operation = f.Call("test.drain"); await began.Task; var close = f.App.DisposeAsync().AsTask(); await cleanup.Task;
        Check(!close.IsCompleted && !opened.Page.IsClosed); release.TrySetResult(); await Cancelled(operation); await close;
        Check(opened.Page.IsClosed);
    }
    private static async Task DraftCloseFlush(bool storageFailure)
    {
        await using var f = new SessionFixture(); await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync(); var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../web"));
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url); if (url.Host != "session-ui.test") { await route.AbortAsync(); return; }
            if (url.AbsolutePath == "/") { await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><html lang='fr'><meta charset='utf-8'><main id='root'></main></html>" }); return; }
            var path = Path.GetFullPath(Path.Combine(web, Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/')));
            if (!path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !path.EndsWith(".js", StringComparison.Ordinal) || !File.Exists(path)) { await route.AbortAsync(); return; }
            await route.FulfillAsync(new() { ContentType = "text/javascript", Body = await File.ReadAllTextAsync(path) });
        });
        var draftSubmitted = Signal(); var saves = 0;
        await page.ExposeFunctionAsync("sessionRuntime", async (string operation, string json) =>
        {
            try
            {
                if (operation == "workplace.draft.save") { Interlocked.Increment(ref saves); draftSubmitted.TrySetResult(); }
                using var args = JsonDocument.Parse(json); var result = await f.App.ExecuteAsync(operation, args.RootElement);
                return JsonSerializer.Serialize(new { ok = true, value = result }, PilotApplication.Json);
            }
            catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, message = error.Message, name = error is OperationCanceledException ? "AbortError" : "Error" }); }
        });
        await page.GotoAsync("http://session-ui.test/");
        var ticket = await f.Call("workplace.ticket.open", new { revision = f.App.Revision, rowIndex = 0 });
        await page.EvaluateAsync("""
            async ticket => {
              const {renderDraft}=await import('/js/pages/workplace-actions.js');
              window.sessionApi={call:async(op,args={})=>{const reply=JSON.parse(await sessionRuntime(op,JSON.stringify(args)));if(!reply.ok){const error=new Error(reply.message);error.name=reply.name;throw error;}return reply.value;}};
              window.sessionCleanup=await renderDraft({api:sessionApi,state:{session:{testMode:false}},notify:()=>{},navigate:()=>{}},document.querySelector('#root'),JSON.parse(ticket));
            }
            """, ticket.GetRawText());
        var draft = page.GetByLabel("Texte modifiable", new() { Exact = true });
        if (storageFailure)
        {
            var store = Path.Combine(f.Root, "workplace.json"); var previous = await File.ReadAllBytesAsync(store);
            File.Delete(store); Directory.CreateDirectory(store);
            try
            {
                await draft.FillAsync("Texte conservé malgré le dossier devenu non inscriptible.");
                var refused = await page.EvaluateAsync<bool>("async()=>{try{await sessionCleanup();return false;}catch{return true;}}");
                Check(refused && await draft.InputValueAsync() == "Texte conservé malgré le dossier devenu non inscriptible.");
            }
            finally { Directory.Delete(store); await File.WriteAllBytesAsync(store, previous); }
            await draft.FillAsync("Dernier texte enregistré après correction de l’emplacement.");
            await page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer maintenant", Exact = true }).ClickAsync();
            await page.GetByText("Brouillon enregistré sur ce poste.", new() { Exact = true }).WaitForAsync();
            var retained = await f.Call("workplace.ticket.open", new { ticketKey = ticket.GetProperty("personal").GetProperty("key").GetString() });
            Check(retained.GetProperty("personal").GetProperty("followupDraft").GetString() == "Dernier texte enregistré après correction de l’emplacement.");
            await page.EvaluateAsync("async()=>await sessionCleanup()"); Check(errors.Count == 0); return;
        }
        var began = Signal(); f.App.RegisterSessionFixture("test.collect", async (_, ct) => { began.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; });
        var collecting = f.Call("test.collect"); await began.Task;
        await draft.FillAsync("Première version en attente");
        await page.GetByRole(AriaRole.Button, new() { Name = "Enregistrer maintenant", Exact = true }).ClickAsync();
        await draftSubmitted.Task; await draft.FillAsync("Dernière version avant fermeture, conservée intégralement.");
        await page.EvaluateAsync("async()=>{await sessionApi.call('operations.cancel');await sessionCleanup();}"); await Cancelled(collecting);
        var saved = await f.Call("workplace.ticket.open", new { ticketKey = ticket.GetProperty("personal").GetProperty("key").GetString() });
        Check(saved.GetProperty("personal").GetProperty("followupDraft").GetString() == "Dernière version avant fermeture, conservée intégralement.");
        Check(saves == 2 && errors.Count == 0, "The cancelled save must be followed by one successful final flush.");
    }
}
