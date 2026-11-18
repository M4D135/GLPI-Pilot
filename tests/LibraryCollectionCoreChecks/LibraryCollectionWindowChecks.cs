using System.Globalization;
using GlpiPilot.Core;

internal static class LibraryCollectionWindowChecks
{
    private const string Installation = "https://windows-scope.example/glpi";
    private const string NativeScope = "fixture-profile-and-entities";
    private static readonly TicketLibraryCaptureContext Context = new(Installation, new string('f', 64), TicketLibraryAcquisition.ReaderSignature);

    public static async Task<(int Passed, int Total)> RunAsync(string root)
    {
        var passed = 0; var total = 0;
        async Task Check(string name, Func<Task> action)
        {
            total++;
            try { await action(); passed++; Console.WriteLine("PASS Collection windows: " + name); }
            catch (Exception error) { Console.WriteLine("FAIL Collection windows: " + name + "\n" + error); }
        }
        await Check("initialization records the complete denominator exactly once without inventing observed tickets", async () =>
        {
            await using var f = await Start(root, "initialize", 4);
            await f.Store.InitializeCollectionWindowsAsync(f.Run, Context, f.Plan);
            var report = await f.Store.GetCaptureAsync(f.Run);
            var entries = await f.Store.CaptureEntriesAsync(f.Run);
            var coverage = await f.Store.CollectionPlanCoverageAsync(f.Run, Context);
            Require(report.Targeted == 4 && report.Observed == 0 && report.Processed == 0 && report.Read == 0, "Initial targets were presented as remote observations.");
            Require(entries.Items.Count == 4 && entries.Items.All(entry => entry.State == "pending" && entry.Category == "pending"), "Initialization duplicated or prematurely classified targets.");
            Require(coverage.Selected == 4 && coverage.Missing == 4 && (await f.Store.CollectionPlanAsync(f.Run, Context))!.NextOrdinal == 0, "Initial coverage or cursor is incorrect.");
        });
        await Check("a completed first window preserves later pending targets and the fixed selection", async () =>
        {
            await using var f = await Start(root, "isolation", 4);
            var before = await f.Store.CollectionWindowCoverageAsync(f.Run, Context, 0, 2);
            await FinishWindow(f, 0, 2);
            var entries = await f.Store.CaptureEntriesAsync(f.Run);
            var first = await f.Store.CollectionWindowCoverageAsync(f.Run, Context, 0, 2);
            var later = await f.Store.CollectionWindowCoverageAsync(f.Run, Context, 2, 2);
            var all = await f.Store.CollectionPlanCoverageAsync(f.Run, Context);
            var plan = (await f.Store.CollectionPlanAsync(f.Run, Context))!;
            Require(entries.Items.Take(2).All(entry => entry.State == "confirmed") && entries.Items.Skip(2).All(entry => entry.State == "pending"), "Window A changed the state of window B.");
            Require(before.Missing == 2 && first.Complete == 2 && later.Missing == 2 && all.Selected == 4 && all.Complete == 2 && all.Missing == 2 && all.Percent == 50, "Window coverage does not reconcile with its full denominator.");
            Require(plan.NextOrdinal == 2 && plan.Fingerprint == f.Plan.Fingerprint && plan.ObservedAt == f.Plan.ObservedAt
                && plan.Tickets.Select(seed => seed.Ticket.Id).SequenceEqual(f.Plan.Tickets.Select(seed => seed.Ticket.Id)), "Fresh evidence changed the selected worklist or inventory provenance.");
            await Refuse(() => f.Store.ApplyCollectionWindowIndexAsync(f.Run, Context, Index(2, f.Plan.Tickets.Take(2)), 2, 2));
            Require((await f.Store.CollectionPlanAsync(f.Run, Context))!.NextOrdinal == 2, "Foreign-window identities advanced the cursor.");
        });
        await Check("the cursor cannot advance before reads and final evidence, and a completed commit is idempotent", async () =>
        {
            await using var f = await Start(root, "guard", 1);
            var seed = Fresh(f.Plan.Tickets[0]);
            await Refuse(() => f.Store.CompleteCollectionWindowAsync(f.Run, Context, 0, 1));
            await f.Store.ApplyCollectionWindowIndexAsync(f.Run, Context, Index(1, [seed]), 0, 1);
            await f.Store.UpdateCollectionWindowEvidenceAsync(f.Run, Context, 0, 1, [seed]);
            Require((await f.Store.CaptureEntriesAsync(f.Run)).Items[0].Category == "pending", "A newly observed target was labelled changed during collection.");
            await Refuse(() => f.Store.CompleteCollectionWindowAsync(f.Run, Context, 0, 1));
            await SavePending(f, seed);
            await Refuse(() => f.Store.CompleteCollectionWindowAsync(f.Run, Context, 0, 1));
            Require((await f.Store.CollectionPlanAsync(f.Run, Context))!.NextOrdinal == 0, "An unconfirmed read moved the cursor.");
            await f.Store.ConfirmCaptureTicketAsync(f.Run, seed, true);
            await f.Store.UpdateCollectionWindowEvidenceAsync(f.Run, Context, 0, 1, [seed]);
            await f.Store.CompleteCollectionWindowAsync(f.Run, Context, 0, 1);
            await f.Store.CompleteCollectionWindowAsync(f.Run, Context, 0, 1);
            Require((await f.Store.CollectionPlanAsync(f.Run, Context))!.NextOrdinal == 1, "A committed completion moved the cursor twice.");
        });
        await Check("initial and final absences remain retryable examined targets without deleting saved content", async () =>
        {
            await using var f = await Start(root, "absence", 2);
            var seed = Fresh(f.Plan.Tickets[0]);
            await f.Store.ApplyCollectionWindowIndexAsync(f.Run, Context, Index(2, [seed]), 0, 2);
            await f.Store.UpdateCollectionWindowEvidenceAsync(f.Run, Context, 0, 2, [seed]);
            await SavePending(f, seed);
            var saved = (await f.Store.GetAsync(seed.Ticket))!;
            await f.Store.UpdateCollectionWindowEvidenceAsync(f.Run, Context, 0, 2, []);
            await f.Store.CompleteCollectionWindowAsync(f.Run, Context, 0, 2);
            var report = await f.Store.GetCaptureAsync(f.Run);
            var entries = await f.Store.CaptureEntriesAsync(f.Run);
            var kept = (await f.Store.GetAsync(seed.Ticket))!;
            var coverage = await f.Store.CollectionPlanCoverageAsync(f.Run, Context);
            Require(report.Targeted == 2 && report.Processed == 2 && report.RetryCount == 2, "Absent targets were omitted from examined or retry counts.");
            Require(entries.Items.All(entry => entry.State == "unavailable" && entry.Category == "no-longer-visible" && entry.Retryable), "Final absence was presented as a successful confirmation.");
            Require(kept.Detail is not null && kept.ContentHash == saved.ContentHash && kept.LocalRevision == saved.LocalRevision, "Absence removed or rewrote the retained ticket body.");
            Require(coverage.Selected == 2 && coverage.Complete == 0 && coverage.Unconfirmed == 1 && coverage.Missing == 1, "Old evidence inflated completeness after disappearance.");
        });
        await Check("a 27-ticket plan resumes at 25 after reopening and processes only the two remaining identities", async () =>
        {
            await using var f = await Start(root, "restart", 27);
            await FinishWindow(f, 0, 25);
            var firstKeys = f.Plan.Tickets.Take(25).Select(seed => TicketLibraryRules.Key(seed.Ticket)).ToArray();
            var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var key in firstKeys) revisions.Add(key, (await f.Store.GetAsync(key))!.LocalRevision);
            await f.Store.FinishCaptureAsync(f.Run, "paused", []);
            var reopened = new TicketLibraryStore(f.Store.DatabasePath);
            var plan = (await reopened.CollectionPlanAsync(f.Run, Context))!;
            Require(plan.NextOrdinal == 25 && plan.Selected == 27 && plan.Fingerprint == f.Plan.Fingerprint, "Restart lost the window checkpoint.");
            await reopened.ResumeCaptureAsync(f.Run, Context);
            await reopened.InitializeCollectionWindowsAsync(f.Run, Context, plan);
            await Refuse(() => reopened.ApplyCollectionWindowIndexAsync(f.Run, Context, Index(25, plan.Tickets.Take(25)), 0, 25));
            await FinishWindow(f with { Store = reopened }, 25, 2);
            await reopened.FinishCaptureVerificationAsync(f.Run, true, true);
            await reopened.FinishCaptureAsync(f.Run, "completed", []);
            var report = await reopened.GetCaptureAsync(f.Run);
            var coverage = await reopened.CollectionPlanCoverageAsync(f.Run, Context);
            Require(report.Read == 27 && report.Processed == 27 && report.RetryCount == 0 && report.FinalScopeVerified
                && coverage.Selected == 27 && coverage.Complete == 27 && coverage.Percent == 100, "Resume did not finish the original scope exactly once.");
            foreach (var key in firstKeys) Require((await reopened.GetAsync(key))!.LocalRevision == revisions[key], "Resume rewrote an already completed window.");
            var resumed = (await reopened.CollectionPlanAsync(f.Run, Context))!;
            var next = await reopened.BeginCaptureAsync(Context, "incremental");
            try
            {
                await reopened.SaveCollectionPlanAsync(next, Context, resumed);
                await reopened.InitializeCollectionWindowsAsync(next, Context, resumed);
                Require((await reopened.CollectionPlanAsync(next, Context))!.NextOrdinal == 0, "A new cycle inherited the completed cursor of its source plan.");
            }
            finally { await reopened.FinishCaptureAsync(next, "stopped", []); }
        });
        await Check("profile drift between windows is terminal before any later entry or body changes", async () =>
        {
            await using var f = await Start(root, "drift", 4);
            await FinishWindow(f, 0, 2);
            var changed = Index(2, f.Plan.Tickets.Skip(2)); changed.ScopeFingerprint = "other-profile-and-entities";
            try
            {
                await f.Store.ApplyCollectionWindowIndexAsync(f.Run, Context, changed, 2, 2);
                throw new InvalidOperationException("A changed scope was accepted.");
            }
            catch (GlpiLibraryIndexException error) when (error.Category == "index-scope" && error.Phase == "scope") { }
            Require((await f.Store.CollectionPlanAsync(f.Run, Context))!.NextOrdinal == 2, "Drift advanced the durable cursor.");
            var entries = await f.Store.CaptureEntriesAsync(f.Run);
            Require(entries.Items.Skip(2).All(entry => entry.State == "pending"), "Drift changed the remaining entries.");
            foreach (var seed in f.Plan.Tickets.Skip(2)) Require(await f.Store.GetAsync(seed.Ticket) is null, "A body was recorded after scope drift.");
            var all = await f.Store.CollectionPlanCoverageAsync(f.Run, Context);
            Require(all.Selected == 4 && all.Complete == 2 && all.Missing == 2, "Drift corrupted the completed window's coverage.");
        });
        await Check("retry windows retain their parent's profile and reject an unobserved parent scope", async () =>
        {
            await using var f = await Start(root, "retry-parent", 1);
            var seed = Fresh(f.Plan.Tickets[0]);
            await f.Store.ApplyCollectionWindowIndexAsync(f.Run, Context, Index(1, [seed]), 0, 1);
            await f.Store.FinishCaptureAsync(f.Run, "paused", []);
            var retry = await f.Store.BeginCaptureAsync(Context, "retry", f.Run);
            try
            {
                await f.Store.SaveCollectionPlanAsync(retry, Context, f.Plan);
                await f.Store.InitializeCollectionWindowsAsync(retry, Context, f.Plan);
                var other = Index(1, [seed]); other.ScopeFingerprint = "other-profile-and-entities";
                await RefuseScope(() => f.Store.ApplyCollectionWindowIndexAsync(retry, Context, other, 0, 1));
                Require((await f.Store.GetCaptureAsync(retry)).Observed == 0 && (await f.Store.CaptureEntriesAsync(retry)).Items[0].State == "pending", "Rejected retry polluted its initial journal.");
                await f.Store.ApplyCollectionWindowIndexAsync(retry, Context, Index(1, [seed]), 0, 1);
                Require((await f.Store.GetCaptureAsync(retry)).Observed == 1, "Retry rejected the unchanged parent context.");
            }
            finally { await f.Store.FinishCaptureAsync(retry, "stopped", []); }

            await using var unknown = await Start(root, "retry-no-parent-proof", 1);
            await unknown.Store.FinishCaptureAsync(unknown.Run, "paused", []);
            var unverifiedRetry = await unknown.Store.BeginCaptureAsync(Context, "retry", unknown.Run);
            try
            {
                await unknown.Store.SaveCollectionPlanAsync(unverifiedRetry, Context, unknown.Plan);
                await unknown.Store.InitializeCollectionWindowsAsync(unverifiedRetry, Context, unknown.Plan);
                await RefuseScope(() => unknown.Store.ApplyCollectionWindowIndexAsync(unverifiedRetry, Context, Index(1, unknown.Plan.Tickets), 0, 1));
                Require((await unknown.Store.GetCaptureAsync(unverifiedRetry)).Observed == 0, "An unobserved parent was treated as a known scope.");
            }
            finally { await unknown.Store.FinishCaptureAsync(unverifiedRetry, "stopped", []); }
        });
        Console.WriteLine($"{passed}/{total} collection window checks passed.");
        return (passed, total);
    }

    private sealed record Fixture(TicketLibraryStore Store, string Run, LibraryCollectionPlan Plan) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if ((await Store.GetCaptureAsync(Run)).Status == "running") await Store.FinishCaptureAsync(Run, "stopped", []);
            TicketLibraryStore.ReleaseCapture(Run);
        }
    }
    private static async Task<Fixture> Start(string root, string name, int count)
    {
        var source = new ExtractionResult
        {
            SourceUrl = Installation + "/front/ticket.php", Complete = true, ExpectedTotal = count,
            ExtractedAt = new(2025, 2, 1, 12, 0, 0, TimeSpan.Zero),
            Headers = ["ID", "Titre", "Dernière modification", "Statut"],
            Rows = Enumerable.Range(1, count).Select(id => new[] { id.ToString(CultureInfo.InvariantCulture), "Ticket synthétique " + id, "2025-01-01 12:00", "Résolu" }).ToList(),
            Provenance = new() { SourceKind = "glpi", SearchVerified = true, ScopeVerified = true, RequestedUrl = Installation + "/front/ticket.php", ScopeFingerprint = NativeScope }
        };
        var plan = LibraryCollectionScopePlanner.Build(source, new() { PortalUrl = source.SourceUrl }, new() { Enabled = true },
            new() { SourceDateOrder = "ymd", SourceTimeZone = "UTC" });
        var store = new TicketLibraryStore(Path.Combine(root, "window-" + name + ".db"));
        var run = await store.BeginCaptureAsync(Context, "incremental");
        await store.SaveCollectionPlanAsync(run, Context, plan);
        await store.InitializeCollectionWindowsAsync(run, Context, plan);
        return new(store, run, plan);
    }
    private static TicketLibrarySeed Fresh(TicketLibrarySeed seed)
    { var fresh = TicketLibraryRules.Copy(seed); fresh.ObservedAt = DateTimeOffset.UtcNow; return fresh; }
    private static TicketLibraryIndex Index(int requested, IEnumerable<TicketLibrarySeed> seeds)
    {
        var list = seeds.Select(Fresh).ToList();
        return new() { SourceUrl = Installation + "/front/ticket.php", IsTargeted = true, RequestedCount = requested,
            AllStatuses = true, Complete = true, ScopeFingerprint = NativeScope, Tickets = list, ExpectedTotal = list.Count };
    }
    private static async Task FinishWindow(Fixture f, int offset, int count)
    {
        var seeds = f.Plan.Tickets.GetRange(offset, count).Select(Fresh).ToList();
        await f.Store.ApplyCollectionWindowIndexAsync(f.Run, Context, Index(count, seeds), offset, count);
        await f.Store.UpdateCollectionWindowEvidenceAsync(f.Run, Context, offset, count, seeds);
        foreach (var seed in seeds) await SavePending(f, seed);
        foreach (var seed in seeds) await f.Store.ConfirmCaptureTicketAsync(f.Run, seed, true);
        await f.Store.UpdateCollectionWindowEvidenceAsync(f.Run, Context, offset, count, seeds);
        await f.Store.CompleteCollectionWindowAsync(f.Run, Context, offset, count);
    }
    private static async Task SavePending(Fixture f, TicketLibrarySeed seed)
    {
        var observed = await f.Store.ObserveCaptureSeedAsync(f.Run, seed);
        await f.Store.RecordCaptureReadingAsync(f.Run, TicketLibraryRules.Key(seed.Ticket));
        await f.Store.SaveCaptureDetailAsync(f.Run, seed, new()
        {
            ReaderSignature = TicketLibraryAcquisition.ReaderSignature,
            Coverage = new() { Complete = true, TabsDiscovered = 0, TabsLoaded = 0 },
            Detail = new() { Ticket = seed.Ticket, Title = seed.Title, ReadAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Sections = [new() { Name = "Traitement", Kind = "timeline", Data = new() { Headers = ["Texte"], Rows = [["Contenu synthétique"]], Complete = true } }] }
        }, observed.Document.LocalRevision);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException("Expected window operation refusal.");
    }
    private static async Task RefuseScope(Func<Task> action)
    {
        try { await action(); }
        catch (GlpiLibraryIndexException error) when (error.Category == "index-scope" && error.Phase == "scope") { return; }
        throw new InvalidOperationException("Expected typed parent-scope refusal.");
    }
}
