using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class LibraryCaptureRuntimeChecks
{
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create() => new (string, Func<Task>)[]
    {
        ("Library capture journal survives interruption and exposes only paged fixed diagnostic categories", Journal),
        ("Library retry refuses a changed active source before any browser navigation", ChangedContext)
    };
    public static async Task<IReadOnlyList<(string Name, Func<Task> Run)>> CreateBrowser(string executable)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { ExecutablePath = executable, Headless = true, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        return new (string, Func<Task>)[]
        {
            ("Real library capture counts failed and partial attempts and retries only currently accessible failures", TargetedRetry),
            ("Real final GLPI index failure leaves received bodies unconfirmed until a successful explicit retry", FailedFinal),
            ("Cancelling after the last real ticket read commits a durable report without confirming its bodies", CancelFinal)
        };
    }
    public static async Task DisposeAsync() { if (_browser is not null) await _browser.DisposeAsync(); _playwright?.Dispose(); }
    private static TicketLibraryStore Store() => new(new LibraryPreferencesStore().Load().DatabasePath);
    private static TicketLibrarySeed Seed(string id) => new()
    {
        Ticket = new(id, "http://session-glpi.test/glpi/front/ticket.form.php?id=" + id), Title = "Ticket de test " + id,
        Status = "Résolu", StatusCode = 5, LastModifiedRaw = "2026-10-20T12:34:00Z", LastModifiedAt = new(2026, 10, 20, 12, 34, 0, TimeSpan.Zero), LastModifiedReliable = true, LastModifiedPrecision = "seconds"
    };
    private static async Task<string> SeedJournal(SessionFixture f, int count)
    {
        var store = Store(); var context = TicketLibraryCaptureContext.FromSettings(f.App.Settings, new LibraryPreferencesStore().Load());
        var run = await store.BeginCaptureAsync(context, "incremental");
        var index = new TicketLibraryIndex { SourceUrl = SessionFixture.SourceA, AllStatuses = true, Complete = true, ScopeFingerprint = "runtime-scope", Tickets = Enumerable.Range(1, count).Select(i => Seed(i.ToString())).ToList() };
        await store.SetCaptureIndexAsync(run, index); return run;
    }
    private static async Task Journal()
    {
        await using var f = new SessionFixture(); Require((await f.Call("library.capture.latest")).ValueKind == JsonValueKind.Null);
        var store = Store(); var run = await SeedJournal(f, 130);
        await store.RecordCaptureFailureAsync(run, TicketLibraryRules.Key(Seed("1").Ticket), "read-timeout"); TicketLibraryStore.ReleaseCapture(run);
        var report = await f.Call("library.capture.latest");
        Require(report.GetProperty("status").GetString() == "interrupted" && report.GetProperty("retryCount").GetInt32() == 130);
        var first = await f.Call("library.capture.entries", new { runId = run, offset = 0, limit = 100, retryOnly = true });
        var next = await f.Call("library.capture.entries", new { runId = run, offset = 100, limit = 100, retryOnly = true });
        Require(first.GetProperty("items").GetArrayLength() == 100 && next.GetProperty("items").GetArrayLength() == 30);
        Require(first.GetProperty("items")[0].GetProperty("category").GetString() == "read-timeout");
        Require(!first.GetRawText().Contains("Ticket de test", StringComparison.Ordinal) && !report.GetRawText().Contains("http://", StringComparison.Ordinal));
        await Refuse(() => f.Call("library.capture.entries", new { runId = "../unsafe", limit = 25 }));
    }
    private static async Task ChangedContext()
    {
        await using var f = new SessionFixture(); var store = Store(); var run = await SeedJournal(f, 1); await store.FinishCaptureAsync(run, "cancelled", []);
        await f.SelectB(); await Refuse(() => f.Call("library.capture", new { mode = "retry", runId = run }));
        Require((await f.Call("library.capture.latest")).GetProperty("runId").GetString() == run);
    }
    private static async Task TargetedRetry()
    {
        await using var glpi = await TicketLibraryCaptureBrowserFixture.OpenAsync(_browser!, SessionFixture.SourceA);
        var initialActivePage = glpi.Service.ActivePage;
        glpi.FailDetails.Add("42"); glpi.PartialDetails.Add("43");
        await using var f = new SessionFixture(glpi.Service); f.App.Settings.ListTimeoutSeconds = 5; f.App.Settings.DetailTimeoutSeconds = 5;
        var first = await f.Call("library.capture", new { mode = "all" }); var run = first.GetProperty("runId").GetString();
        Require(first.GetProperty("observed").GetInt32() == 3 && first.GetProperty("read").GetInt32() == 2 && first.GetProperty("failed").GetInt32() == 1 && first.GetProperty("partial").GetInt32() == 1);
        Require(first.GetProperty("retryCount").GetInt32() == 2 && first.GetProperty("finalScopeVerified").GetBoolean());
        var before = glpi.DetailRequests.Count; glpi.FailDetails.Clear(); glpi.PartialDetails.Clear(); glpi.TicketIds.Remove("42");
        var retry = await f.Call("library.capture", new { mode = "retry", runId = run });
        Require(retry.GetProperty("observed").GetInt32() == 2 && retry.GetProperty("targeted").GetInt32() == 2 && retry.GetProperty("read").GetInt32() == 1);
        Require(glpi.DetailRequests.Skip(before).SequenceEqual(["43"]));
        var entries = await f.Call("library.capture.entries", new { runId = retry.GetProperty("runId").GetString(), retryOnly = true });
        Require(entries.GetProperty("total").GetInt32() == 1 && entries.GetProperty("items")[0].GetProperty("ticketId").GetString() == "42"
            && entries.GetProperty("items")[0].GetProperty("category").GetString() == "no-longer-visible");
        Require(glpi.Context.Pages.All(p => ReferenceEquals(p, glpi.Page) || ReferenceEquals(p, glpi.Service.TicketPage))
            && glpi.Page.Url == SessionFixture.SourceA && ReferenceEquals(glpi.Service.ActivePage, initialActivePage));
    }
    private static async Task FailedFinal()
    {
        await using var glpi = await TicketLibraryCaptureBrowserFixture.OpenAsync(_browser!, SessionFixture.SourceA); glpi.FailIndexNumber = 2;
        await using var f = new SessionFixture(glpi.Service); f.App.Settings.ListTimeoutSeconds = 5; f.App.Settings.DetailTimeoutSeconds = 5;
        var result = await f.Call("library.capture", new { mode = "all" }); var run = result.GetProperty("runId").GetString();
        Require(result.GetProperty("read").GetInt32() == 3 && result.GetProperty("unconfirmed").GetInt32() == 3 && !result.GetProperty("finalScopeVerified").GetBoolean());
        var store = Store(); Require((await store.SearchAsync(new() { ResolvedOnly = true })).Total == 0);
        Require((await new TicketLibraryStore(store.DatabasePath).LatestCaptureAsync())!.RunId == run);
        glpi.FailIndexNumber = 0; var retry = await f.Call("library.capture", new { mode = "retry", runId = run });
        Require(retry.GetProperty("read").GetInt32() == 3 && retry.GetProperty("unconfirmed").GetInt32() == 0 && retry.GetProperty("retryCount").GetInt32() == 0);
        Require((await store.SearchAsync(new() { ResolvedOnly = true })).Total == 3);
    }
    private static async Task CancelFinal()
    {
        await using var glpi = await TicketLibraryCaptureBrowserFixture.OpenAsync(_browser!, SessionFixture.SourceA); glpi.PauseIndexNumber = 2;
        var closing = new System.Collections.Concurrent.ConcurrentQueue<string>(); glpi.Service.Log += message => { if (message.StartsWith("FICHE · fermeture", StringComparison.Ordinal)) closing.Enqueue(message); };
        await using var f = new SessionFixture(glpi.Service); f.App.Settings.ListTimeoutSeconds = 5; f.App.Settings.DetailTimeoutSeconds = 5;
        var capture = f.Call("library.capture", new { mode = "all" }); await glpi.WaitForIndexAsync(2);
        await f.Call("operations.cancel");
        try { await capture; throw new InvalidOperationException("Expected capture cancellation."); } catch (OperationCanceledException) { }
        var report = await f.Call("library.capture.latest");
        Require(report.GetProperty("status").GetString() == "cancelled" && report.GetProperty("read").GetInt32() == 3 && report.GetProperty("unconfirmed").GetInt32() == 3);
        Require((await Store().SearchAsync(new() { ResolvedOnly = true })).Total == 0, "Cancelled content became eligible for resolution.");
        // Closing a Chromium target is acknowledged separately from the context's
        // page-closed event. Observe the actual target disappearing, under a bound.
        var cleanup = System.Diagnostics.Stopwatch.StartNew();
        while (glpi.Context.Pages.Any(p => !ReferenceEquals(p, glpi.Page) && !ReferenceEquals(p, glpi.Service.TicketPage)) && cleanup.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(25);
        Require(glpi.Context.Pages.All(p => ReferenceEquals(p, glpi.Page) || ReferenceEquals(p, glpi.Service.TicketPage)),
            "Unexpected fixture page after cancellation: " + string.Join(" | ", glpi.Context.Pages.Select(p => $"{p.Url} closed={p.IsClosed} source={ReferenceEquals(p, glpi.Page)} detail={ReferenceEquals(p, glpi.Service.TicketPage)}")) + " / " + string.Join(" / ", closing));
        glpi.PauseIndexNumber = 0;
        var retry = await f.Call("library.capture", new { mode = "retry", runId = report.GetProperty("runId").GetString() });
        Require(retry.GetProperty("retryCount").GetInt32() == 0 && retry.GetProperty("finalScopeVerified").GetBoolean());
    }
    private static async Task Refuse(Func<Task> action)
    { try { await action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { return; } throw new InvalidOperationException("Expected refusal."); }
    private static void Require(bool condition, string message = "Library runtime capture assertion failed.") { if (!condition) throw new InvalidOperationException(message); }
}
