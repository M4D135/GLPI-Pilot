using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static class TargetedCollectionJourneyChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        const string portal = "https://targeted-journey.example/glpi/front/ticket.php";
        var preferencesStore = new LibraryPreferencesStore(); var previous = preferencesStore.Load();
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(root, "targeted-library"), SourceTimeZone = "UTC", SourceDateOrder = "ymd" };
        preferencesStore.Save(preferences);
        var settings = new AppSettings { PortalUrl = portal, SelectorOrigin = "https://targeted-journey.example", ListTimeoutSeconds = 5,
            DetailTimeoutSeconds = 5, TimeoutSeconds = 15, MaxPages = 10, MaxRows = 100, ResumePagination = false };
        var scope = new LibraryCollectionScope { Enabled = true, FromDate = "2025-03-01",
            Fields = [new() { Column = "Attribué à - Groupe", Values = ["N2"] }] };
        var reads = new ConcurrentDictionary<string, int>(); var queries = new ConcurrentQueue<string[]>();
        var globalQueries = 0; var modified = false; var extraTicket = false;
        var bulkSelection = 0; var crossBatchDrift = false; var driftQueryBase = 0;
        var scopeChanged = false; var readsAfterDrift = 0;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });

        async Task<PilotApplication> Open(bool withInventory = true, bool pause = false, bool pauseAfterWindow = false)
        {
            var context = await browser.NewContextAsync();
            await context.RouteAsync("**/*", async route =>
            {
                var uri = new Uri(route.Request.Url);
                if (uri.Host != "targeted-journey.example") { await route.AbortAsync(); return; }
                var query = TicketReferenceResolver.ParseQuery(uri);
                string Value(string key) => query.FirstOrDefault(p => p.Key == key).Value ?? "";
                string body;
                if (uri.AbsolutePath.EndsWith("/ticket.php", StringComparison.Ordinal))
                {
                    if (Value("criteria[0][value]") == "all" || Value("start").Length > 0)
                    { Interlocked.Increment(ref globalQueries); await route.FulfillAsync(new() { Status = 400, Body = "Global scan forbidden in targeted fixture." }); return; }
                    var targeted = Value("criteria[0][field]") == "92";
                    var ids = targeted ? query.Where(p => p.Key.EndsWith("[value]", StringComparison.Ordinal)).Select(p => p.Value).ToArray()
                        : extraTicket ? ["41", "42", "43", "44", "45"] : new[] { "41", "42", "43", "44" };
                    if (targeted) queries.Enqueue(ids);
                    // With no visible source page, each batch can independently
                    // observe a valid scope while the aggregate crosses scopes.
                    var entity = crossBatchDrift && queries.Count > driftQueryBase + (targeted ? 2 : 1) ? "B" : "A";
                    if (entity == "B") scopeChanged = true;
                    var fields = targeted ? string.Join("", ids.Select((id, i) =>
                        $"<select name='criteria[{i}][field]'><option value='12'>Statut</option><option value='92' selected>ID</option></select>"
                        + $"<select name='criteria[{i}][searchtype]'><option value='equals'>est</option></select><input name='criteria[{i}][value]' value='{WebUtility.HtmlEncode(id)}'>"
                        + (i > 0 ? $"<select name='criteria[{i}][link]'><option value='OR'>OU</option></select>" : "")))
                        : "<select id='criterion-field' name='criteria[0][field]'><option value='12' selected>Statut</option><option value='92'>ID</option></select>"
                        + "<span id='criterion-value'><select name='criteria[0][searchtype]'><option value='equals'>est</option></select><select name='criteria[0][value]'><option value='notold' selected>Non clos</option><option value='all'>Tous</option><option value='5'>Résolu</option><option value='6'>Clos</option></select></span>";
                    var rows = string.Join("", ids.Select(id => $"<tr><td><a href='ticket.form.php?id={WebUtility.HtmlEncode(id)}'>Incident synthétique {WebUtility.HtmlEncode(id)}</a></td><td>{Modification(id, modified)}</td><td>Résolu</td></tr>"));
                    var handler = targeted ? "" : "<script>document.getElementById('criterion-field').addEventListener('change',async e=>{document.getElementById('criterion-value').innerHTML=await(await fetch('/glpi/ajax/search.php',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'action=display_searchoption&field='+encodeURIComponent(e.target.value)+'&itemtype=Ticket'})).text()})</script>";
                    body = $"<html><title>GLPI</title><body><div class='page'><header class='navbar'>Entité {entity} · Profil support</header></div><a href='/glpi/front/logout.php'>Déconnexion</a>"
                        + $"<form name='searchformticket' method='get' action='ticket.php'>{fields}<input name='is_deleted' value='0' type='hidden'><input name='as_map' value='0' type='hidden'><input name='browse' value='0' type='hidden'><a class='search-reset' href='ticket.php?reset=reset'>Réinitialiser</a></form>"
                        + $"<form data-search-itemtype='Ticket' data-count='{(targeted ? ids.Length : 180000)}' data-start='0' data-limit='100'><table class='search-results'><thead><tr><th data-searchopt-id='1'>Titre</th><th data-searchopt-id='19'>Dernière modification</th><th data-searchopt-id='12'>Statut</th></tr></thead><tbody>{rows}</tbody></table>"
                        + (targeted ? "" : "<div class='search-pager'><a class='page-link-next' data-start='100' href='ticket.php?start=100'>Suivant</a></div>") + "</form>" + handler + "</body></html>";
                }
                else if (uri.AbsolutePath.EndsWith("/ajax/search.php", StringComparison.Ordinal))
                    body = "<select name='criteria[0][searchtype]'><option value='equals'>est</option></select><input name='criteria[0][value]' value=''>";
                else if (uri.AbsolutePath.EndsWith("/ticket.form.php", StringComparison.Ordinal))
                {
                    if (crossBatchDrift && scopeChanged) Interlocked.Increment(ref readsAfterDrift);
                    var id = Value("id"); reads.AddOrUpdate(id, 1, (_, old) => old + 1);
                    body = $"<html><title>Ticket {WebUtility.HtmlEncode(id)}</title><body><form id='itil-form'><input type='hidden' name='id' value='{WebUtility.HtmlEncode(id)}'><textarea name='name'>Incident synthétique {WebUtility.HtmlEncode(id)}</textarea><textarea name='content'>Description {(modified && id == "42" ? "modifiée" : "initiale")} synthétique</textarea><select name='status'><option value='5' selected>Résolu</option></select></form></body></html>";
                }
                else { await route.FulfillAsync(new() { Status = 404, Body = "Unknown targeted fixture" }); return; }
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = body });
            });
            var page = await context.NewPageAsync(); await page.GotoAsync(portal);
            var service = new BrowserService(); service.AttachForTesting(context, portal); await service.FindBestPageAsync(settings, page, default);
            PilotApplication? app = null; var paused = false;
            app = new(new NullHost(), (name, data) =>
            {
                if ((!pause && !pauseAfterWindow) || paused || name != "library.collection.progress") return;
                var value = JsonSerializer.SerializeToElement(data, PilotApplication.Json);
                if (value.GetProperty("state").GetString() != "running") return;
                if (pause && value.GetProperty("processed").GetInt32() != 1) return;
                if (pauseAfterWindow)
                {
                    var runId = value.GetProperty("runId").GetString();
                    if (string.IsNullOrEmpty(runId)) return;
                    var journal = app!.ExecuteAsync("library.collection.events", JsonSerializer.SerializeToElement(new { runId })).GetAwaiter().GetResult();
                    var events = JsonSerializer.SerializeToElement(journal, PilotApplication.Json);
                    if (!events.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "window-completed")) return;
                }
                paused = true;
                var accepted = app!.ExecuteAsync("library.collection.control", JsonSerializer.SerializeToElement(new
                    { action = "pause", sessionId = value.GetProperty("sessionId").GetString() })).GetAwaiter().GetResult();
                Require(JsonSerializer.SerializeToElement(accepted, PilotApplication.Json).GetProperty("requested").GetBoolean(), "Targeted pause was not admitted.");
            }, settings, service);
            if (withInventory) app.LoadTargetedCollectionFixture(Inventory(portal, modified, bulkSelection));
            var current = await Call(app, "library.collection.get", new { });
            await Call(app, "library.collection.settings", new { revision = current.GetProperty("state").GetProperty("revision").GetInt64(),
                options = new LibraryCollectionOptions { DelayMilliseconds = 1000, BatchPauseSeconds = 0, MaximumMinutes = 5 } });
            return app;
        }

        async Task<JsonElement> Start(PilotApplication app)
        {
            var preview = await Call(app, "library.collection.preview", new { scope });
            Require(preview.GetProperty("coverage").GetProperty("selected").GetInt32() == 2, "Preview denominator differs from date/group selection.");
            return await Call(app, "library.capture", new { mode = "incremental", scope, previewToken = preview.GetProperty("previewToken").GetString() });
        }
        void OnlySelection()
        {
            Require(globalQueries == 0 && queries.All(ids => ids.Length == 2 && ids.Order(StringComparer.Ordinal).SequenceEqual(new[] { "42", "43" })),
                "A global query or a ticket outside the frozen selection was requested.");
            Require(reads.Keys.All(id => id is "42" or "43"), "An excluded ticket body was collected.");
        }

        try
        {
            await check("Targeted controller filters date and group, reads two bodies and confirms 2/2 while GLPI announces 180000 tickets", async () =>
            {
                await using var app = await Open();
                var result = await Start(app);
                Require(result.GetProperty("collectionState").GetString() == "completed" && result.GetProperty("targeted").GetInt32() == 2
                    && result.GetProperty("read").GetInt32() == 2 && result.GetProperty("retryCount").GetInt32() == 0
                    && result.GetProperty("finalScopeVerified").GetBoolean(), "Initial targeted collection did not confirm its two selected tickets.");
                var state = await Call(app, "library.collection.get", new { });
                Require(state.GetProperty("coverage").GetProperty("selected").GetInt32() == 2
                    && state.GetProperty("coverage").GetProperty("percent").GetDouble() == 100, "Coverage uses the global 180000 denominator.");
                Require(reads.GetValueOrDefault("42") == 1 && reads.GetValueOrDefault("43") == 1, "A selected body was read twice.");
                OnlySelection();
            });
            await check("Targeted controller revalidates metadata and reuses both unchanged bodies even after eight days", async () =>
            {
                var original = new TicketLibraryStore(preferences.DatabasePath);
                preferences.LocalFolder = Path.Combine(root, "targeted-aged-library"); preferencesStore.Save(preferences);
                var aged = new TicketLibraryStore(preferences.DatabasePath);
                foreach (var id in new[] { "42", "43" })
                {
                    var saved = (await original.GetAsync(Ticket(portal, id)))!;
                    saved.Detail!.ReadAt = DateTimeOffset.UtcNow.AddDays(-8);
                    var seeded = (await aged.SaveDetailAsync(saved.Seed, saved.Detail, saved.Coverage, saved.ReaderSignature)).Document;
                    Require(TicketLibraryRules.IsContentConfirmed(seeded) && !TicketLibraryStore.CanSkip(saved.Seed, seeded, saved.ReaderSignature),
                        "Aged fixture does not exceed the former seven-day re-read policy.");
                }
                var before = reads.Values.Sum(); var beforeQueries = queries.Count;
                await using var app = await Open();
                var result = await Start(app);
                Require(result.GetProperty("read").GetInt32() == 0 && result.GetProperty("reused").GetInt32() == 2
                    && result.GetProperty("retryCount").GetInt32() == 0 && reads.Values.Sum() == before && queries.Count > beforeQueries,
                    "Unchanged aged bodies were downloaded again or reused without a live targeted metadata check.");
                OnlySelection();
            });
            await check("Targeted controller reads only the ticket whose live GLPI modification token changed", async () =>
            {
                var before42 = reads.GetValueOrDefault("42"); var before43 = reads.GetValueOrDefault("43"); modified = true;
                await using var app = await Open(); var result = await Start(app);
                Require(result.GetProperty("read").GetInt32() == 1 && result.GetProperty("reused").GetInt32() == 1
                    && result.GetProperty("retryCount").GetInt32() == 0 && reads.GetValueOrDefault("42") == before42 + 1
                    && reads.GetValueOrDefault("43") == before43, "The changed token did not produce exactly one detail read.");
                OnlySelection();
            });
            await check("Targeted pause survives restart without an inventory and resumes the frozen plan without rereading its saved body", async () =>
            {
                preferences.LocalFolder = Path.Combine(root, "targeted-paused-library"); preferencesStore.Save(preferences);
                var before = new Dictionary<string, int>(reads); string run;
                await using (var app = await Open(pause: true))
                {
                    var result = await Start(app); run = result.GetProperty("runId").GetString()!;
                    Require(result.GetProperty("collectionState").GetString() == "paused" && reads.Values.Sum() == before.Values.Sum() + 1,
                        "Pause did not preserve one saved body of the selected pair.");
                }
                extraTicket = true;
                await using (var app = await Open(withInventory: false))
                {
                    var saved = await Call(app, "library.collection.get", new { });
                    Require(!saved.GetProperty("available").GetBoolean() && saved.GetProperty("coverage").GetProperty("selected").GetInt32() == 2,
                        "Reopening required a new inventory or lost its frozen denominator.");
                    var result = await Call(app, "library.capture", new { mode = "resume", runId = run });
                    Require(result.GetProperty("runId").GetString() == run && result.GetProperty("collectionState").GetString() == "completed"
                        && result.GetProperty("read").GetInt32() == 2 && result.GetProperty("retryCount").GetInt32() == 0,
                        "Resuming the targeted journal changed its identity, reread a saved body or left a stale confirmation.");
                }
                Require(new[] { "42", "43" }.All(id => reads.GetValueOrDefault(id) == before.GetValueOrDefault(id) + 1),
                    "Resume re-downloaded its already saved content.");
                OnlySelection();
            });
            await check("Targeted controller stops before reading another window after the active entity scope changes", async () =>
            {
                preferences.LocalFolder = Path.Combine(root, "targeted-scope-drift-library"); preferencesStore.Save(preferences);
                bulkSelection = 30; crossBatchDrift = true; driftQueryBase = queries.Count;
                var before = reads.Values.Sum();
                await using var app = await Open();
                await app.Browser.ActivePage!.CloseAsync();
                var preview = await Call(app, "library.collection.preview", new { scope });
                Require(preview.GetProperty("coverage").GetProperty("selected").GetInt32() == 30, "Drift fixture must span more than one native ID batch.");
                var result = await Call(app, "library.capture", new { mode = "incremental", scope, previewToken = preview.GetProperty("previewToken").GetString() });
                Require(result.GetProperty("collectionState").GetString() == "attention" && reads.Values.Sum() == before + 25
                    && readsAfterDrift == 0 && queries.Count == driftQueryBase + 3 && globalQueries == 0,
                    "Metadata observed under scope A was used to read bodies after GLPI switched to scope B.");
                var events = await Call(app, "library.collection.events", new { runId = result.GetProperty("runId").GetString() });
                Require(events.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "index-scope"
                    && item.GetProperty("level").GetString() == "error"), "Scope drift was not recorded as an explicit collection error.");
            });
            await check("A completed 25-ticket window survives restart and only the remaining two tickets are indexed and read", async () =>
            {
                preferences.LocalFolder = Path.Combine(root, "targeted-window-checkpoint-library"); preferencesStore.Save(preferences);
                bulkSelection = 27; crossBatchDrift = false;
                var before = new Dictionary<string, int>(reads); var beforeQueries = queries.Count;
                string run; HashSet<string> completed;
                await using (var app = await Open(pauseAfterWindow: true))
                {
                    var preview = await Call(app, "library.collection.preview", new { scope });
                    Require(preview.GetProperty("coverage").GetProperty("selected").GetInt32() == 27, "Window fixture selection is not 27 tickets.");
                    var result = await Call(app, "library.capture", new { mode = "incremental", scope, previewToken = preview.GetProperty("previewToken").GetString() });
                    run = result.GetProperty("runId").GetString()!;
                    Require(result.GetProperty("collectionState").GetString() == "paused" && reads.Values.Sum() == before.Values.Sum() + 25,
                        "Pause after window-completed did not stop after exactly 25 saved and confirmed bodies.");
                    var state = await Call(app, "library.collection.get", new { });
                    Require(state.GetProperty("coverage").GetProperty("selected").GetInt32() == 27
                        && state.GetProperty("coverage").GetProperty("complete").GetInt32() == 25,
                        "Completed window was not reflected in the full-plan coverage.");
                    var firstQueries = queries.Skip(beforeQueries).ToArray();
                    Require(firstQueries.Length == 2 && firstQueries.All(ids => ids.Length == 25), "The first window requested another portion of the worklist.");
                    completed = firstQueries.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);
                    Require(completed.Count == 25, "The initial and final metadata queries covered different identities.");
                }
                var resumeQueries = queries.Count; var readCheckpoint = new Dictionary<string, int>(reads);
                await using (var app = await Open(withInventory: false))
                {
                    var result = await Call(app, "library.capture", new { mode = "resume", runId = run });
                    Require(result.GetProperty("runId").GetString() == run && result.GetProperty("collectionState").GetString() == "completed"
                        && result.GetProperty("read").GetInt32() == 27 && result.GetProperty("retryCount").GetInt32() == 0,
                        "Window resume lost its durable identity, counters or confirmation.");
                    var state = await Call(app, "library.collection.get", new { });
                    Require(state.GetProperty("coverage").GetProperty("selected").GetInt32() == 27
                        && state.GetProperty("coverage").GetProperty("complete").GetInt32() == 27
                        && state.GetProperty("coverage").GetProperty("percent").GetDouble() == 100,
                        "The final rate does not cover all 27 selected tickets.");
                }
                var finalQueries = queries.Skip(resumeQueries).ToArray();
                Require(finalQueries.Length == 2 && finalQueries.All(ids => ids.Length == 2 && ids.All(id => !completed.Contains(id)))
                    && finalQueries.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).Count() == 2,
                    "Resume re-indexed a completed window instead of only the remaining two identities.");
                Require(completed.All(id => reads.GetValueOrDefault(id) == readCheckpoint.GetValueOrDefault(id))
                    && reads.Values.Sum() == before.Values.Sum() + 27 && globalQueries == 0,
                    "Resume re-downloaded a confirmed window or collected outside its plan.");
            });
        }
        finally { preferencesStore.Save(previous); }
    }

    private static string Modification(string id, bool modified) => id switch
    { "42" => modified ? "2025-04-05 12:00" : "2025-04-02 12:00", "43" => "2025-04-03 12:00", _ => "2025-02-01 12:00" };
    private static TicketReference Ticket(string portal, string id) => new(id, portal.Replace("ticket.php", "ticket.form.php", StringComparison.Ordinal) + "?id=" + id);
    private static ExtractionResult Inventory(string portal, bool modified, int bulk = 0)
    {
        var result = new ExtractionResult
        {
        SourceUrl = portal, ExtractedAt = DateTimeOffset.UtcNow, Complete = false, ExpectedTotal = 180000,
        Headers = ["ID", "Titre", "Date d'ouverture", "Dernière modification", "Attribué à - Groupe", "Statut"],
        Rows = [ ["41", "Incident synthétique 41", "2025-01-01 12:00", Modification("41", modified), "N2", "Résolu"],
            ["42", "Incident synthétique 42", "2025-04-01 12:00", Modification("42", modified), "N2", "Résolu"],
            ["43", "Incident synthétique 43", "2025-04-02 12:00", Modification("43", modified), "N2", "Résolu"],
            ["44", "Incident synthétique 44", "2025-04-02 12:00", Modification("44", modified), "N3", "Résolu"] ],
            Provenance = new() { RequestedUrl = portal, SourceKind = "browser", SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "targeted-journey-fixture" }
        };
        if (bulk > 0) result.Rows = Enumerable.Range(100, bulk).Select(id => new[] { id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Incident synthétique " + id, "2025-04-01 12:00", "2025-04-02 12:00", "N2", "Résolu" }).ToList();
        return result;
    }
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object args) =>
        JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args, PilotApplication.Json)), PilotApplication.Json);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void LoadTargetedCollectionFixture(ExtractionResult data) => SetResult(data);
    }
}
