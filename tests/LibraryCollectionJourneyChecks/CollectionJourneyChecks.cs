using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Web;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static class CollectionJourneyChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        const string portal = "https://collection-journey.example/glpi/front/ticket.php";
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(root, "journey-library"), SourceTimeZone = "UTC", SourceDateOrder = "ymd" };
        new LibraryPreferencesStore().Save(preferences);
        var settings = new AppSettings { PortalUrl = portal, SelectorOrigin = "https://collection-journey.example", TimeoutSeconds = 15, ListTimeoutSeconds = 5, DetailTimeoutSeconds = 5, MaxPages = 5, MaxRows = 100, ResumePagination = false };
        var reads = new ConcurrentDictionary<string, int>(); var requests = 0; var expanded = false;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"), Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        async Task<PilotApplication> Open(string? stopAction = null)
        {
            var context = await browser.NewContextAsync();
            await context.RouteAsync("**/*", async route =>
            {
                Interlocked.Increment(ref requests); var uri = new Uri(route.Request.Url);
                if (uri.Host != "collection-journey.example") { await route.AbortAsync(); throw new InvalidOperationException("Unexpected external fixture request."); }
                var query = HttpUtility.ParseQueryString(uri.Query);
                string body;
                if (uri.AbsolutePath.EndsWith("/ticket.php", StringComparison.Ordinal))
                {
                    var all = query["criteria[0][value]"] == "all";
                    var ids = expanded ? new[] { "41", "42", "43" } : new[] { "41", "42" };
                    var rows = string.Join("", ids.Select(id => $"<tr><td><a href='ticket.form.php?id={id}'>Incident synthétique {id}</a></td><td>{(expanded && id == "42" ? "2025-01-02 12:00" : "2025-01-01 12:00")}</td><td>Résolu</td></tr>"));
                    body = $"<html><title>GLPI</title><body><form name='searchformticket' method='get' action='ticket.php'><select name='criteria[0][field]'><option value='12' selected>Statut</option></select><select name='criteria[0][searchtype]'><option value='equals'>est</option></select><select name='criteria[0][value]'><option value='notold' {(!all ? "selected" : "")}>Non clos</option><option value='all' {(all ? "selected" : "")}>Tous</option><option value='5'>Résolu</option></select><input name='is_deleted' value='0' type='hidden'><input name='as_map' value='0' type='hidden'><input name='browse' value='0' type='hidden'><a class='search-reset' href='ticket.php?reset=reset'>Réinitialiser</a></form><form data-search-itemtype='Ticket' data-count='{ids.Length}' data-start='0' data-limit='100'><table class='search-results'><thead><tr><th data-searchopt-id='1'>Titre</th><th data-searchopt-id='19'>Dernière modification</th><th data-searchopt-id='12'>Statut</th></tr></thead><tbody>{rows}</tbody></table></form></body></html>";
                }
                else if (uri.AbsolutePath.EndsWith("/ticket.form.php", StringComparison.Ordinal))
                {
                    var id = query["id"] ?? throw new InvalidOperationException("Missing fixture ticket identity.");
                    if (id is not ("41" or "42" or "43")) throw new InvalidOperationException("Foreign fixture identity.");
                    reads.AddOrUpdate(id, 1, (_, old) => old + 1);
                    body = $"<html><title>Ticket {id}</title><body><form id='itil-form'><input type='hidden' name='id' value='{id}'><textarea name='name'>Incident synthétique {id}</textarea><textarea name='content'>Description {(expanded && id == "42" ? "modifiée" : "initiale")} pour le test</textarea><select name='status'><option value='5' selected>Résolu</option></select></form></body></html>";
                }
                else { await route.FulfillAsync(new() { Status = 404, Body = "Unknown fixture" }); return; }
                await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = body });
            });
            var page = await context.NewPageAsync(); await page.GotoAsync(portal);
            var service = new BrowserService(); service.AttachForTesting(context, portal); await service.FindBestPageAsync(settings, page, default);
            PilotApplication? app = null; var sent = false;
            app = new PilotApplication(new NullHost(), (name, data) =>
            {
                if (name != "library.collection.progress" || stopAction is null || sent) return;
                var value = JsonSerializer.SerializeToElement(data, PilotApplication.Json);
                if (value.GetProperty("state").GetString() != "running" || value.GetProperty("processed").GetInt32() != 1) return;
                sent = true;
                // ExecuteAsync's narrow control fast lane completes synchronously even
                // while capture owns the normal business semaphore.
                var result = app!.ExecuteAsync("library.collection.control", JsonSerializer.SerializeToElement(new { action = stopAction, sessionId = value.GetProperty("sessionId").GetString() })).GetAwaiter().GetResult();
                Require(JsonSerializer.SerializeToElement(result, PilotApplication.Json).GetProperty("requested").GetBoolean(), "Pause/stop was not admitted during the active capture.");
            }, settings, service);
            return app;
        }
        var runId = "";
        await check("Production controller pauses after one real synthetic browser read and persists its journal", async () =>
        {
            await using var app = await Open("pause");
            var page = await Call(app, "library.collection.get", new { });
            var options = page.GetProperty("state").GetProperty("options").Deserialize<LibraryCollectionOptions>(PilotApplication.Json)!;
            options.DelayMilliseconds = 1000; options.BatchPauseSeconds = 0; options.MaximumMinutes = 5;
            await Call(app, "library.collection.settings", new { revision = page.GetProperty("state").GetProperty("revision").GetInt64(), options });
            var result = await Call(app, "library.capture", new { mode = "incremental" }); runId = result.GetProperty("runId").GetString()!;
            Require(result.GetProperty("collectionState").GetString() == "paused" && reads.GetValueOrDefault("41") == 1 && reads.GetValueOrDefault("42") == 0, "Pause did not preserve the first-only read boundary.");
        });
        await check("Reopening the production controller resumes the same run without rereading the saved body", async () =>
        {
            await using var app = await Open();
            var state = await Call(app, "library.collection.get", new { });
            Require(state.GetProperty("state").GetProperty("state").GetString() == "paused", "Pause was not durable.");
            var result = await Call(app, "library.capture", new { mode = "resume", runId });
            Require(result.GetProperty("runId").GetString() == runId && result.GetProperty("collectionState").GetString() == "completed", "Resume changed identity or failed.");
            Require(result.GetProperty("retryCount").GetInt32() == 0 && reads.GetValueOrDefault("41") == 1 && reads.GetValueOrDefault("42") == 1, "Already saved body was re-read or remains unconfirmed.");
        });
        await check("Incremental production acquisition reads only one modified ticket and one new ticket", async () =>
        {
            await using var app = await Open();
            await Call(app, "library.capture", new { mode = "incremental" });
            Require(reads.GetValueOrDefault("41") == 1 && reads.GetValueOrDefault("42") == 1, "Unchanged content was downloaded again.");
            expanded = true; var result = await Call(app, "library.capture", new { mode = "incremental" });
            Require(result.GetProperty("read").GetInt32() == 2 && result.GetProperty("reused").GetInt32() == 1 && result.GetProperty("retryCount").GetInt32() == 0, "Wrong delta scope or incomplete confirmation.");
            Require(reads.GetValueOrDefault("41") == 1 && reads.GetValueOrDefault("42") == 2 && reads.GetValueOrDefault("43") == 1, "New/changed content boundary was not preserved.");
        });
        await check("Forced refresh retains its unread targets and cumulative counts through two pauses", async () =>
        {
            string refreshRun;
            await using (var app = await Open("pause"))
            {
                var result = await Call(app, "library.capture", new { mode = "all" });
                refreshRun = result.GetProperty("runId").GetString()!;
                Require(result.GetProperty("collectionState").GetString() == "paused" && reads.GetValueOrDefault("41") == 2
                    && reads.GetValueOrDefault("42") == 2 && reads.GetValueOrDefault("43") == 1, "Forced refresh did not stop after its first fresh read.");
            }
            await using (var app = await Open("pause"))
            {
                var result = await Call(app, "library.capture", new { mode = "resume", runId = refreshRun });
                Require(result.GetProperty("collectionState").GetString() == "paused" && result.GetProperty("read").GetInt32() == 1
                    && reads.GetValueOrDefault("41") == 2, "First resume lost the durable read count or reread the saved ticket.");
            }
            await using (var app = await Open())
            {
                var result = await Call(app, "library.capture", new { mode = "resume", runId = refreshRun });
                Require(result.GetProperty("runId").GetString() == refreshRun && result.GetProperty("mode").GetString() == "all"
                    && result.GetProperty("collectionState").GetString() == "completed" && result.GetProperty("retryCount").GetInt32() == 0,
                    "The forced run changed its identity, intent or completion status.");
                Require(reads.GetValueOrDefault("41") == 2 && reads.GetValueOrDefault("42") == 3 && reads.GetValueOrDefault("43") == 2,
                    "Resume skipped still-required forced reads or repeated its already completed read.");
                Require(result.GetProperty("read").GetInt32() == 3 && result.GetProperty("reused").GetInt32() == 0,
                    "The durable journal lost completed reads or counted a ticket twice.");
            }
        });
        await check("A later incremental cycle does not inherit the previous forced refresh", async () =>
        {
            await using var app = await Open();
            var result = await Call(app, "library.capture", new { mode = "incremental" });
            Require(result.GetProperty("read").GetInt32() == 0 && result.GetProperty("reused").GetInt32() == 3
                && reads.GetValueOrDefault("41") == 2 && reads.GetValueOrDefault("42") == 3 && reads.GetValueOrDefault("43") == 2,
                "Forced refresh leaked into the next incremental cycle.");
        });
        await check("Explicit stop remains held after reopening even when the stored schedule is overdue", async () =>
        {
            await using (var app = await Open("stop"))
            {
                var result = await Call(app, "library.capture", new { mode = "incremental" });
                Require(result.GetProperty("collectionState").GetString() == "stopped", "Stop was not persisted.");
            }
            var local = new TicketLibraryStore(preferences.DatabasePath); var fingerprint = TicketLibraryCaptureContext.FromSettings(settings, preferences).Fingerprint;
            var state = await local.CollectionStateAsync(fingerprint); state.Options.Automatic = true; state.NextAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await local.SaveCollectionStateAsync(fingerprint, state, state.Revision);
            await using var reopened = await Open(); var before = requests;
            await reopened.TickAsync();
            Require(requests == before && (await local.CollectionStateAsync(fingerprint)).State == "stopped", "An automatic tick resumed a manual stop.");
        });
        await check("Incremental collection revalidates aged content even when every portal token is unchanged", async () =>
        {
            var original = new TicketLibraryStore(preferences.DatabasePath);
            preferences.LocalFolder = Path.Combine(root, "aged-journey-library");
            new LibraryPreferencesStore().Save(preferences);
            var aged = new TicketLibraryStore(preferences.DatabasePath);
            var before = new Dictionary<string, int>(reads);
            foreach (var id in new[] { "41", "42", "43" })
            {
                var ticket = new TicketReference(id, portal.Replace("ticket.php", "ticket.form.php", StringComparison.Ordinal) + "?id=" + id);
                var saved = (await original.GetAsync(ticket))!;
                saved.Detail!.ReadAt = DateTimeOffset.UtcNow.AddDays(-8);
                var seeded = (await aged.SaveDetailAsync(saved.Seed, saved.Detail, saved.Coverage, saved.ReaderSignature)).Document;
                Require(TicketLibraryRules.IsContentConfirmed(seeded)
                    && TicketLibraryStore.CanSkip(saved.Seed, seeded, saved.ReaderSignature, new() { MaximumAge = TimeSpan.MaxValue })
                    && !TicketLibraryStore.CanSkip(saved.Seed, seeded, saved.ReaderSignature), "Aged fixture must differ only in freshness eligibility.");
            }
            await using var app = await Open();
            var page = await Call(app, "library.collection.get", new { });
            await Call(app, "library.collection.settings", new { revision = page.GetProperty("state").GetProperty("revision").GetInt64(),
                options = new LibraryCollectionOptions { DelayMilliseconds = 1000, BatchPauseSeconds = 0, MaximumMinutes = 5 } });
            var result = await Call(app, "library.capture", new { mode = "incremental" });
            Require(result.GetProperty("read").GetInt32() == 3 && result.GetProperty("reused").GetInt32() == 0
                && result.GetProperty("retryCount").GetInt32() == 0 && before.All(pair => reads[pair.Key] == pair.Value + 1),
                "The production collector reused expired content despite the periodic freshness limit.");
        });
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object args) =>
        JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args, PilotApplication.Json)), PilotApplication.Json);
}
