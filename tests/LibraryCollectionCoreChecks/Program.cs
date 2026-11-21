using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
#if !COLLECTION_CORE_ONLY
using GlpiPilot.Runtime;
#endif

var checkpointWorker = args.Length == 2 && args[0] == "--checkpoint-worker";
var root = checkpointWorker ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Collection-" + Guid.NewGuid().ToString("N"));
if (checkpointWorker && (!Directory.Exists(root) || !Path.GetFileName(root).StartsWith("GLPI-Pilot-Checks-Collection-", StringComparison.Ordinal)
    || !string.Equals(Path.GetDirectoryName(root), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Checkpoint worker requires its parent's isolated temporary profile.");
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
const string installation = "https://collection.example/glpi";
var context = new TicketLibraryCaptureContext(installation, new string('a', 64), TicketLibraryAcquisition.ReaderSignature);
var passed = 0; var total = 0;
try
{
    if (checkpointWorker)
    {
        var store = Store("process-checkpoint"); var seed = Seed(); var run = await Start(store, seed);
        await SaveBody(store, run, seed);
        var state = await store.CollectionStateAsync(context.Fingerprint); state.RunId = run; state.State = "running";
        await store.SaveCollectionStateAsync(context.Fingerprint, state, state.Revision);
        Console.WriteLine(run); Console.Out.Flush();
        // The parent kills this owned process after the committed checkpoint.
        // No FinishCapture or ReleaseCapture may make the restart test pass.
        await Task.Delay(Timeout.Infinite);
        return 1;
    }
    var scopeChecks = await LibraryCollectionScopeChecks.RunAsync(root); passed += scopeChecks.Passed; total += scopeChecks.Total;
    var windowChecks = await LibraryCollectionWindowChecks.RunAsync(root); passed += windowChecks.Passed; total += windowChecks.Total;
    var availabilityChecks = await LibraryCaptureAvailabilityChecks.RunAsync(root); passed += availabilityChecks.Passed; total += availabilityChecks.Total;
#if !COLLECTION_CORE_ONLY
    await CollectionScopeRuntimeChecks.RunAsync(root, Check);
#endif
    await Check("A finished pass succeeds only when every target and the final scope were verified", () =>
    {
        TicketLibraryCaptureReport Complete() => new()
        { Status = "completed", Observed = 2, Targeted = 2, Processed = 2, Read = 1, Reused = 1, FinalScopeVerified = true };
        var complete = Complete();
        Require(complete.IsSuccessful && !complete.NeedsAttention && complete.StatusLabel == "Collecte validée");
        Action<TicketLibraryCaptureReport>[] incomplete =
        [
            report => report.FinalScopeVerified = false, report => report.Processed = 1, report => report.Processed = 3,
            report => report.Failed = 1, report => report.Partial = 1, report => report.Conflicts = 1,
            report => report.Unconfirmed = 1, report => report.RetryCount = 1
        ];
        foreach (var change in incomplete)
        {
            var report = Complete(); change(report);
            Require(!report.IsSuccessful && report.NeedsAttention && report.StatusLabel == "Collecte terminée, à compléter");
        }
        var retained = Complete(); retained.Unconfirmed = 2; // historical retryable=0 must not turn this green
        Require(retained.RetryCount == 0 && !retained.IsSuccessful && retained.Message.Contains("2 contenus non confirmés", StringComparison.Ordinal));
        foreach (var status in new[] { "running", "paused", "stopped", "failed", "cancelled", "interrupted" })
        { var report = Complete(); report.Status = status; Require(!report.IsSuccessful && !report.NeedsAttention); }
        return Task.CompletedTask;
    });
    await Check("An empty finished journal explicitly says that no content was validated", () =>
    {
        var report = new TicketLibraryCaptureReport { Status = "completed", FinalScopeVerified = true };
        Require(!report.IsSuccessful && report.NeedsAttention && report.Message.Contains("Aucun ticket ciblé : aucun contenu validé", StringComparison.Ordinal));
        return Task.CompletedTask;
    });
    await Check("Date preflight blocks local dates without a source timezone and ambiguous date order without changing seeds", () =>
    {
        var local = Seed("date-local"); local.LastModifiedRaw = "2026-11-01 08:00:00"; local.LastModifiedPrecision = "seconds";
        var ambiguous = Seed("date-ambiguous"); ambiguous.LastModifiedRaw = "03/04/2026 12:00";
        var before = TicketLibraryRules.Serialize(new[] { local, ambiguous });
        var withoutZone = LibraryCollectionPreflight.Inspect([local], new() { SourceDateOrder = "ymd" });
        var withoutOrder = LibraryCollectionPreflight.Inspect([ambiguous], new() { SourceTimeZone = "UTC" });
        Require(withoutZone.State == "blocked" && withoutZone.ReliableDates == 0 && withoutZone.UnreliableDates == 1);
        Require(withoutOrder.State == "blocked" && withoutOrder.ReliableDates == 0 && withoutOrder.UnreliableDates == 1);
        var preview = LibraryCollectionPreflight.Preview([local], new() { SourceDateOrder = "ymd" });
        Require(preview.State == "warning" && preview.Checked == 1 && preview.ReliableDates == 0 && preview.UnreliableDates == 1);
        Require(LibraryCollectionPreflight.Inspect([ambiguous], new() { SourceTimeZone = "UTC", SourceDateOrder = "dmy" }).State == "ready");
        Require(TicketLibraryRules.Serialize(new[] { local, ambiguous }) == before);
        return Task.CompletedTask;
    });
    await Check("Date preflight accepts an explicit ISO offset without inventing a source timezone", () =>
    {
        var seed = Seed("date-offset"); seed.LastModifiedRaw = "2026-11-01T08:00:00+02:00";
        seed.LastModifiedAt = null; seed.LastModifiedPrecision = "seconds";
        var preferences = new LibraryPreferences(); var before = TicketLibraryRules.Serialize(seed);
        var result = LibraryCollectionPreflight.Inspect([seed], preferences);
        Require(result.State == "ready" && result.Checked == 1 && result.ReliableDates == 1 && result.UnreliableDates == 0
            && result.SourceTimeZone == "" && preferences.SourceTimeZone == "" && seed.LastModifiedAt is null);
        Require(TicketLibraryRules.Serialize(seed) == before);
        seed.LastModifiedReliable = false;
        Require(LibraryCollectionPreflight.Inspect([seed], preferences).State == "blocked");
        return Task.CompletedTask;
    });
    await Check("Mixed date preflight warns with exact counts and an empty inventory remains unknown", () =>
    {
        var valid = Seed("date-valid"); valid.LastModifiedRaw = "2026-11-01T08:00:00Z"; valid.LastModifiedPrecision = "seconds";
        var missing = Seed("date-missing"); missing.LastModifiedRaw = ""; missing.LastModifiedAt = null; missing.LastModifiedReliable = false;
        var before = TicketLibraryRules.Serialize(new[] { valid, missing });
        var result = LibraryCollectionPreflight.Inspect([valid, missing], new());
        Require(result.State == "warning" && result.Checked == 2 && result.ReliableDates == 1 && result.UnreliableDates == 1
            && result.Messages.Single().Contains("1 date(s) de modification sur 2", StringComparison.Ordinal));
        Require(TicketLibraryRules.Serialize(new[] { valid, missing }) == before);
        Require(LibraryCollectionPreflight.Inspect(null, new()).State == "unknown" && LibraryCollectionPreflight.Inspect([], new()).Checked == 0);
        return Task.CompletedTask;
    });
    await Check("Current knowledge readiness matches native complete, pending, partial, conflicting and missing content without writing", async () =>
    {
        var store = Store("knowledge-readiness");
        var ready = Seed("61"); var pending = Seed("62"); var partial = Seed("63"); var conflict = Seed("64"); var metadata = Seed("65"); var absent = Seed("66");
        var run = await Start(store, ready, pending, partial, conflict, metadata, absent);
        await SaveBody(store, run, ready); await store.ConfirmCaptureTicketAsync(run, ready, true);
        await SaveBody(store, run, pending);
        var observed = await store.ObserveCaptureSeedAsync(run, partial);
        await store.SaveCaptureDetailAsync(run, partial, new()
        {
            Coverage = new() { Complete = false, TabsDiscovered = 2, TabsLoaded = 1 },
            Detail = new()
            {
                Ticket = partial.Ticket, Title = partial.Title, ReadAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
                Sections = [new() { Name = "Chronologie partielle", Kind = "timeline", TabKey = "treatment", Data = new()
                    { Headers = ["Type", "Contenu"], Rows = [["Solution", "Corps partiel conservé"]], Complete = false } }]
            }
        }, observed.Document.LocalRevision);
        await store.ConfirmCaptureTicketAsync(run, partial, true);
        await SaveBody(store, run, conflict, solution: "Version A conservée");
        await SaveBody(store, run, conflict, solution: "Version B conservée");
        await store.ConfirmCaptureTicketAsync(run, conflict, true);
        await store.ObserveCaptureSeedAsync(run, metadata);
        await store.FinishCaptureAsync(run, "completed", []);
        var before = FileHash(store.DatabasePath);
        var result = await store.CaptureKnowledgeReadinessAsync(run);
        Require(result.Available && !result.IsReady && result.Tickets == 6 && result.EligibleTickets == 1
            && result.Incomplete == 3 && result.MissingContent == 2 && result.Unconfirmed == 4 && result.Conflicts == 1);
        Require(FileHash(store.DatabasePath) == before);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(store.ConnectionString)
            { Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ticket_library_knowledge_v1_tickets WHERE version_kind='selected' AND detail_complete=1 AND has_content=1 AND content_confirmed=1 AND confirmation_pending=0 AND conflict_count=0";
        Require(Convert.ToInt32(command.ExecuteScalar()) == result.EligibleTickets);
        Require(FileHash(store.DatabasePath) == before);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await RejectCancelled(() => store.CaptureKnowledgeReadinessAsync(run, cancelled.Token));
        await Reject(() => store.CaptureKnowledgeReadinessAsync(new string('f', 32)));
        Require(FileHash(store.DatabasePath) == before);
    });
    await Check("Native capture readiness changes only after its exact final confirmation while the body remains identical", async () =>
    {
        var store = Store("knowledge-ready-after-confirmation"); var seed = Seed("67"); var run = await Start(store, seed);
        await SaveBody(store, run, seed);
        var pending = (await store.GetAsync(seed.Ticket))!; var body = TicketLibraryRules.Serialize(pending.Detail); var contentHash = pending.ContentHash;
        var before = FileHash(store.DatabasePath); var first = await store.CaptureKnowledgeReadinessAsync(run);
        Require(!first.IsReady && first.Tickets == 1 && first.EligibleTickets == 0 && first.Unconfirmed == 1 && FileHash(store.DatabasePath) == before);
        await store.ConfirmCaptureTicketAsync(run, seed, true); await store.FinishCaptureVerificationAsync(run, true, true);
        await store.FinishCaptureAsync(run, "completed", []);
        var confirmed = (await store.GetAsync(seed.Ticket))!;
        Require(TicketLibraryRules.IsContentConfirmed(confirmed) && confirmed.ContentHash == contentHash && TicketLibraryRules.Serialize(confirmed.Detail) == body);
        before = FileHash(store.DatabasePath); var final = await store.CaptureKnowledgeReadinessAsync(run);
        Require(final.IsReady && final.Tickets == 1 && final.EligibleTickets == 1 && final.Incomplete == 0 && final.MissingContent == 0
            && final.Unconfirmed == 0 && final.Conflicts == 0 && FileHash(store.DatabasePath) == before);
    });
    await Check("Options default to explicit manual collection, distinct from shared synchronization", () =>
    { var options = new LibraryCollectionOptions(); options.Validate(); Require(!options.Automatic && options.DelayMilliseconds >= 1000); return Task.CompletedTask; });
    await Check("Unbounded pace and malformed schedules are refused", async () =>
    { await Reject(() => { new LibraryCollectionOptions { DelayMilliseconds = 0 }.Validate(); return Task.CompletedTask; }); await Reject(() => { new LibraryCollectionOptions { StartMinute = 10, EndMinute = 10 }.Validate(); return Task.CompletedTask; }); });
    await Check("Overnight collection windows and their boundaries are explicit", () =>
    {
        var options = new LibraryCollectionOptions { StartMinute = 22 * 60, EndMinute = 6 * 60, TimeZone = "UTC" };
        Require(options.Allows(new(2026, 1, 1, 23, 0, 0, TimeSpan.Zero)) && options.Allows(new(2026, 1, 1, 5, 59, 0, TimeSpan.Zero)) && !options.Allows(new(2026, 1, 1, 6, 0, 0, TimeSpan.Zero)));
        return Task.CompletedTask;
    });
    await Check("Navigation pacing spaces requests and cancellation never starts another request", async () =>
    {
        var starts = 0;
        using (LibraryCollectionPacer.Enter(new() { DelayMilliseconds = 1000, BatchSize = 20, BatchPauseSeconds = 0 }, (n, delay) => { if (delay == TimeSpan.Zero) starts = n; }))
        {
            await LibraryCollectionPacer.BeforeNavigationAsync(default); var timer = Stopwatch.StartNew(); await LibraryCollectionPacer.BeforeNavigationAsync(default);
            Require(timer.ElapsedMilliseconds >= 900 && starts == 2);
            using var stop = new CancellationTokenSource(30); await RejectCancelled(() => LibraryCollectionPacer.BeforeNavigationAsync(stop.Token)); Require(starts == 2);
        }
        var outside = Stopwatch.StartNew(); await LibraryCollectionPacer.BeforeNavigationAsync(default); Require(outside.ElapsedMilliseconds < 500);
    });
    await Check("A lost shared lease prevents the next navigation", async () =>
    {
        var requests = 0;
        using var scope = LibraryCollectionPacer.Enter(new() { DelayMilliseconds = 1000 }, (n, delay) => requests = n,
            _ => throw new InvalidOperationException("Synthetic unavailable lease"));
        await Reject(() => LibraryCollectionPacer.BeforeNavigationAsync(default)); Require(requests == 0);
    });
    await Check("A folder collector lease excludes a second collector but releases on disposal", async () =>
    {
        var folder = Path.Combine(root, "shared"); Directory.CreateDirectory(folder); var preferences = new LibraryPreferences { SyncMode = "folder", SharedFolder = folder };
        await using (var held = await LibraryCollectorLease.AcquireAsync(preferences, installation, "", default))
        { await Reject(async () => { await using var refused = await LibraryCollectorLease.AcquireAsync(preferences, installation, "", default); }); await held.VerifyAsync(default); }
        await using var next = await LibraryCollectorLease.AcquireAsync(preferences, installation, "", default); await next.VerifyAsync(default);
    });
    await Check("Collection policy persists with optimistic revisions and no shared outbox", async () =>
    {
        var store = Store("policy"); var a = await store.CollectionStateAsync(context.Fingerprint); a.Options.Automatic = true; a.State = "paused";
        var saved = await store.SaveCollectionStateAsync(context.Fingerprint, a, 0); var read = await new TicketLibraryStore(store.DatabasePath).CollectionStateAsync(context.Fingerprint);
        Require(saved.Revision == 1 && read.Held && read.Options.Automatic && (await store.GetStateAsync()).PendingCount == 0);
        await Reject(() => store.SaveCollectionStateAsync(context.Fingerprint, a, 0));
    });
    await Check("Pause retains the same run and confirms a saved body on resume without reading it again", async () =>
    {
        var store = Store("resume"); var a = Seed("41"); var b = Seed("42"); var run = await Start(store, a, b);
        await SaveBody(store, run, a); var hash = (await store.GetAsync(a.Ticket))!.ContentHash;
        await store.FinishCaptureAsync(run, "paused", []);
        var reopened = new TicketLibraryStore(store.DatabasePath); await reopened.ResumeCaptureAsync(run, context);
        await reopened.ReconcileCaptureIndexAsync(run, Index(a, b)); await reopened.ConfirmCaptureTicketAsync(run, a, true);
        var saved = (await reopened.GetAsync(a.Ticket))!;
        Require(TicketLibraryRules.IsContentConfirmed(saved) && saved.ContentHash == hash && TicketLibraryStore.CanSkip(a, saved, context.ReaderSignature));
        await reopened.RecordCollectionReuseAsync(run, saved.Key); await SaveBody(reopened, run, b); await reopened.ConfirmCaptureTicketAsync(run, b, true);
        await reopened.FinishCaptureAsync(run, "completed", []); var report = await reopened.GetCaptureAsync(run);
        Require(report.Reused == 1 && report.Read == 1 && report.RetryCount == 0 && report.RunId == run);
    });
    await Check("Resume refuses an installation, reader, context or entity change", async () =>
    {
        var store = Store("context"); var seed = Seed(); var run = await Start(store, seed); await SaveBody(store, run, seed); await store.FinishCaptureAsync(run, "stopped", []);
        await Reject(() => store.ResumeCaptureAsync(run, context with { Fingerprint = new string('b', 64) }));
        await Reject(() => store.ResumeCaptureAsync(run, context with { ReaderSignature = "another-reader" }));
        await store.ResumeCaptureAsync(run, context); var index = Index(seed); index.ScopeFingerprint = "another-entity";
        await Reject(() => store.ReconcileCaptureIndexAsync(run, index)); Require((await store.GetAsync(seed.Ticket))!.DetailComplete);
        await store.FinishCaptureAsync(run, "stopped", []);
    });
    await Check("A run paused by the previous reader resumes with the current reader only", async () =>
    {
        var store = Store("earlier-reader"); var seed = Seed(); var earlier = context with { ReaderSignature = "glpi-browser-library-v2-confirmed" };
        var run = await store.BeginCaptureAsync(earlier, "incremental"); await store.SetCaptureIndexAsync(run, Index(seed)); await store.FinishCaptureAsync(run, "paused", []);
        await Reject(() => store.ResumeCaptureAsync(run, context with { ReaderSignature = "glpi-browser-library-v1" }));
        await store.ResumeCaptureAsync(run, context); await SaveBody(store, run, seed); await store.ConfirmCaptureTicketAsync(run, seed, true);
        await store.FinishCaptureAsync(run, "completed", []); Require(TicketLibraryRules.IsContentConfirmed(await store.GetAsync(seed.Ticket)));
    });
    await Check("A pending read by the previous reader is kept, then confirmed by the next run's final check without a conflict", async () =>
    {
        var store = Store("earlier-pending"); var seed = Seed(); var earlier = context with { ReaderSignature = "glpi-browser-library-v2-confirmed" };
        var old = await store.BeginCaptureAsync(earlier, "incremental"); await store.SetCaptureIndexAsync(old, Index(seed));
        await SaveBody(store, old, seed, earlier.ReaderSignature); await store.FinishCaptureAsync(old, "stopped", []);
        var pending = (await store.GetAsync(seed.Ticket))!;
        Require(pending.ConfirmationPending && !pending.DetailsCurrent && TicketLibraryStore.CanSkip(seed, pending, context.ReaderSignature, new() { ForceRefresh = true }));
        var run = await Start(store, seed); var observed = await store.ObserveCaptureSeedAsync(run, seed);
        Require(TicketLibraryStore.CanSkip(seed, observed.Document, context.ReaderSignature));
        await store.RecordCollectionReuseAsync(run, observed.Document.Key, awaitingConfirmation: true); await store.ConfirmCaptureTicketAsync(run, seed, true);
        await store.FinishCaptureAsync(run, "completed", []);
        var kept = (await store.GetAsync(seed.Ticket))!; var report = await store.GetCaptureAsync(run);
        Require(TicketLibraryRules.IsContentConfirmed(kept) && kept.ReaderSignature == earlier.ReaderSignature && report.Conflicts == 0 && report.Unconfirmed == 0
            && (await store.GetStateAsync()).ConflictCount == 0);
    });
    await Check("A paused 3.13.2 run whose saved body a sync rewrote is resumed by 3.13.3 and confirms that body", async () =>
    {
        var store = Store("earlier-resume-sync"); var seed = Seed(); var earlier = context with { ReaderSignature = "glpi-browser-library-v2-confirmed" };
        var run = await store.BeginCaptureAsync(earlier, "incremental"); await store.SetCaptureIndexAsync(run, Index(seed));
        await SaveBody(store, run, seed, earlier.ReaderSignature); await store.FinishCaptureAsync(run, "paused", []);
        await store.ResumeCaptureAsync(run, context); await store.ObserveCaptureSeedAsync(run, seed); // rewrites the copy, as a sync does
        await store.ConfirmCaptureTicketAsync(run, seed, true); // the resume pre-check, before any read
        Require(TicketLibraryRules.IsContentConfirmed(await store.GetAsync(seed.Ticket)));
        var observed = await store.ObserveCaptureSeedAsync(run, seed); Require(TicketLibraryStore.CanSkip(seed, observed.Document, context.ReaderSignature));
        await store.RecordCollectionResumeReuseAsync(run, observed.Document.Key, awaitingConfirmation: !TicketLibraryRules.IsContentConfirmed(observed.Document));
        await store.ConfirmCaptureTicketAsync(run, seed, true);
        await store.FinishCaptureAsync(run, "completed", []); var report = await store.GetCaptureAsync(run);
        Require(TicketLibraryRules.IsContentConfirmed(await store.GetAsync(seed.Ticket)) && report.Unconfirmed == 0 && report.RetryCount == 0 && report.Conflicts == 0);
    });
    await Check("A kept earlier read is not confirmed after a same-version conflict, with an incomplete scope or without a final check", async () =>
    {
        var earlier = context with { ReaderSignature = "glpi-browser-library-v2-confirmed" };
        async Task<(TicketLibraryStore Store, TicketLibrarySeed Seed)> Kept(string name, bool conflict)
        {
            var store = Store(name); var seed = Seed();
            var old = await store.BeginCaptureAsync(earlier, "incremental"); await store.SetCaptureIndexAsync(old, Index(seed));
            await SaveBody(store, old, seed, earlier.ReaderSignature); await store.FinishCaptureAsync(old, "stopped", []);
            if (conflict)
            {
                var other = await store.BeginCaptureAsync(earlier, "incremental"); await store.SetCaptureIndexAsync(other, Index(seed));
                await SaveBody(store, other, seed, earlier.ReaderSignature, "Autre solution du même jeton"); await store.FinishCaptureAsync(other, "stopped", []);
            }
            return (store, seed);
        }
        async Task<(TicketLibraryDocument Kept, TicketLibraryCaptureReport Report)> Run((TicketLibraryStore Store, TicketLibrarySeed Seed) at, bool? scope, bool reusable = true)
        {
            var run = await Start(at.Store, at.Seed); var observed = await at.Store.ObserveCaptureSeedAsync(run, at.Seed);
            Require(TicketLibraryStore.CanSkip(at.Seed, observed.Document, context.ReaderSignature) == reusable);
            // Also submit the stale planner decision: final verification must not
            // promote a real conflict even if a caller had already chosen reuse.
            await at.Store.RecordCollectionReuseAsync(run, observed.Document.Key, awaitingConfirmation: true);
            if (scope is { } verified) await at.Store.ConfirmCaptureTicketAsync(run, at.Seed, verified);
            await at.Store.FinishCaptureAsync(run, scope is null ? "stopped" : "completed", []);
            return ((await at.Store.GetAsync(at.Seed.Ticket))!, await at.Store.GetCaptureAsync(run));
        }
        var demoted = await Run(await Kept("earlier-conflict", true), true, reusable: false);
        Require(!demoted.Kept.DetailsCurrent && demoted.Report.Unconfirmed == 1 && demoted.Report.Conflicts == 1 && demoted.Report.RetryCount == 1 && !demoted.Report.IsSuccessful);
        var partialScope = await Run(await Kept("earlier-scope", false), false);
        Require(partialScope.Kept.ConfirmationPending && !partialScope.Kept.DetailsCurrent && partialScope.Report.Unconfirmed == 1);
        var stopped = await Run(await Kept("earlier-stopped", false), null);
        Require(stopped.Kept.ConfirmationPending && stopped.Report.Unconfirmed == 1 && stopped.Report.RetryCount == 1);
    });
    await Check("A kept earlier read that the final check cannot confirm stays pending and is reported as unconfirmed", async () =>
    {
        var store = Store("earlier-undated"); var seed = Seed(); seed.LastModifiedReliable = false; seed.LastModifiedAt = null; seed.LastModifiedPrecision = "unknown";
        var earlier = context with { ReaderSignature = "glpi-browser-library-v2-confirmed" };
        var old = await store.BeginCaptureAsync(earlier, "incremental"); await store.SetCaptureIndexAsync(old, Index(seed));
        await SaveBody(store, old, seed, earlier.ReaderSignature); await store.FinishCaptureAsync(old, "stopped", []);
        var run = await Start(store, seed); var observed = await store.ObserveCaptureSeedAsync(run, seed);
        Require(TicketLibraryStore.CanSkip(seed, observed.Document, context.ReaderSignature));
        await store.RecordCollectionReuseAsync(run, observed.Document.Key, awaitingConfirmation: true); await store.ConfirmCaptureTicketAsync(run, seed, true);
        await store.FinishCaptureAsync(run, "completed", []);
        var kept = (await store.GetAsync(seed.Ticket))!; var report = await store.GetCaptureAsync(run);
        Require(kept.ConfirmationPending && !kept.DetailsCurrent && report.Unconfirmed == 1 && report.RetryCount == 1 && report.Conflicts == 0 && (await store.GetStateAsync()).ConflictCount == 0);
        Require(!report.IsSuccessful && report.NeedsAttention && (await store.CaptureRetryTargetsAsync(run, context)).Count == 1);
    });
    await Check("Finalizing an older pending reuse restores its retry without rewriting its retained body or proof", async () =>
    {
        var store = Store("legacy-pending-retry"); var seed = Seed(); var run = await Start(store, seed);
        await SaveBody(store, run, seed);
        var stored = (await store.GetAsync(seed.Ticket))!;
        var body = TicketLibraryRules.Serialize(stored.Detail); var hash = stored.ContentHash; var proof = stored.ConfirmationId;
        await store.RecordCollectionResumeReuseAsync(run, stored.Key, awaitingConfirmation: true);
        Require((await store.GetCaptureAsync(run)).RetryCount == 1);
        // Reproduce the old journal flag only; no content or confirmation is fabricated.
        await store.Run(connection => TicketLibraryStore.Execute(connection, null,
            "UPDATE ticket_library_capture_entries SET retryable=0 WHERE run_id=$run", ("$run", run)), default);
        await store.FinishCaptureAsync(run, "completed", []);
        var kept = (await store.GetAsync(seed.Ticket))!; var report = await store.GetCaptureAsync(run);
        Require(kept.ContentHash == hash && kept.ConfirmationId == proof && kept.ConfirmationPending && !kept.DetailsCurrent
            && TicketLibraryRules.Serialize(kept.Detail) == body && report.Unconfirmed == 1 && report.RetryCount == 1
            && report.Read == 1 && report.Reused == 0 && !report.IsSuccessful && report.NeedsAttention);
    });
    await Check("An already completed legacy journal exposes pending, partial, failed and conflicting retries without a repair write", async () =>
    {
        var store = Store("legacy-completed-retry");
        var pending = Seed("71"); var ready = Seed("72"); var partial = Seed("73"); var conflict = Seed("74"); var missing = Seed("75");
        var seeds = new[] { pending, ready, partial, conflict, missing }; var parent = await Start(store, seeds);
        await SaveBody(store, parent, pending);
        await store.RecordCollectionReuseAsync(parent, TicketLibraryRules.Key(pending.Ticket), awaitingConfirmation: true);
        await SaveBody(store, parent, ready); await store.ConfirmCaptureTicketAsync(parent, ready, true);
        var observed = await store.ObserveCaptureSeedAsync(parent, partial);
        await store.SaveCaptureDetailAsync(parent, partial, new()
        {
            Coverage = new() { Complete = false, TabsDiscovered = 2, TabsLoaded = 1 },
            Detail = new()
            {
                Ticket = partial.Ticket, Title = partial.Title, ReadAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
                Sections = [new() { Name = "Chronologie partielle", Kind = "timeline", TabKey = "treatment", Data = new()
                    { Headers = ["Type", "Contenu"], Rows = [["Solution", "Corps partiel conservé"]], Complete = false } }]
            }
        }, observed.Document.LocalRevision);
        await store.ConfirmCaptureTicketAsync(parent, partial, true);
        await SaveBody(store, parent, conflict, solution: "Version A conservée");
        await SaveBody(store, parent, conflict, solution: "Version B conservée");
        await store.ConfirmCaptureTicketAsync(parent, conflict, true);
        await store.RecordCaptureFailureAsync(parent, TicketLibraryRules.Key(missing.Ticket), "read-timeout");
        await store.FinishCaptureAsync(parent, "completed", []);
        // Emulate the persisted legacy flag after the run has already closed.
        // Never call FinishCapture again on this parent: consultation must work
        // without a repair pass or a rewrite of bodies, proofs or journal flags.
        await store.Run(connection => TicketLibraryStore.Execute(connection, null,
            "UPDATE ticket_library_capture_entries SET retryable=0 WHERE run_id=$run", ("$run", parent)), default);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seed in seeds) documents[seed.Ticket.Id] = TicketLibraryRules.Serialize(await store.GetAsync(seed.Ticket));
        var before = FileHash(store.DatabasePath);
        var report = await store.GetCaptureAsync(parent);
        Require(report.Status == "completed" && report.Targeted == 5 && report.RetryCount == 4
            && report.Partial == 1 && report.Failed == 1 && report.Conflicts == 1 && report.Unconfirmed > 0
            && !report.IsSuccessful && report.NeedsAttention);
        var all = await store.CaptureEntriesAsync(parent);
        var retry = await store.CaptureEntriesAsync(parent, retryOnly: true);
        var paged = await store.CaptureEntriesAsync(parent, offset: 1, limit: 2, retryOnly: true);
        var expectedIds = new[] { "71", "73", "74", "75" };
        Require(all.Total == 5 && all.Items.Single(item => item.TicketId == "72").Retryable == false
            && all.Items.Count(item => item.Retryable) == 4 && retry.Total == 4
            && retry.Items.Select(item => item.TicketId).SequenceEqual(expectedIds)
            && retry.Items.All(item => item.Retryable) && paged.Total == 4
            && paged.Items.Select(item => item.TicketId).SequenceEqual(expectedIds.Skip(1).Take(2)));
        var offline = await store.ReadSavedCollectionAsync(installation, parent);
        var offlinePage = await store.ReadSavedCaptureEntriesAsync(installation, parent, retryOnly: true);
        Require(offline is not null && offline.Report.RetryCount == 4 && offline.Report.NeedsAttention
            && offlinePage.Total == 4 && offlinePage.Items.All(item => item.Retryable)
            && offlinePage.Items.Select(item => item.TicketId).SequenceEqual(expectedIds));
        var targets = await store.CaptureRetryTargetsAsync(parent, context);
        var missingTargets = await store.CaptureRetryTargetsAsync(parent, context, missingOnly: true);
        Require(targets.Select(target => target.TicketId).SequenceEqual(expectedIds)
            && missingTargets.Count == 1 && missingTargets[0].TicketId == "75");
        await Reject(() => store.BeginCaptureAsync(context, "retry", parent,
            retryTargets: [new(TicketLibraryRules.Key(ready.Ticket), ready.Ticket.Id)]));
        Require(FileHash(store.DatabasePath) == before);
        var run = await store.BeginCaptureAsync(context, "retry", parent, retryTargets: targets);
        Require((await store.CaptureEntriesAsync(run)).Items.Select(item => item.TicketId).SequenceEqual(expectedIds));
        await store.FinishCaptureAsync(run, "stopped", []);
        // Legacy unrestricted retry journals reconstruct only these effective
        // parent targets, never a healthy entry or an unrelated live ticket.
        var legacyRetry = await store.BeginCaptureAsync(context, "retry", parent);
        var keys = await store.ReconcileCaptureIndexAsync(legacyRetry, Index([.. seeds, Seed("76")]));
        Require(keys.Count == 4 && keys.All(key => targets.Any(target => target.TicketKey == key)));
        await store.FinishCaptureAsync(legacyRetry, "stopped", []);
        foreach (var seed in seeds) Require(TicketLibraryRules.Serialize(await store.GetAsync(seed.Ticket)) == documents[seed.Ticket.Id]);
        Require(await store.Run(connection => TicketLibraryStore.ScalarLong(connection, null,
            "SELECT COUNT(*) FROM ticket_library_capture_entries WHERE run_id=$run AND retryable=0", ("$run", parent)), default) == 5);
    });
    await Check("A changed source token invalidates the old pending proof without losing its saved content", async () =>
    {
        var store = Store("modified"); var seed = Seed(); var run = await Start(store, seed); await SaveBody(store, run, seed); await store.FinishCaptureAsync(run, "paused", []);
        var next = Seed(); next.LastModifiedAt = next.LastModifiedAt!.Value.AddHours(1); next.LastModifiedRaw = "2025-01-01 13:00";
        await store.ResumeCaptureAsync(run, context); await store.ReconcileCaptureIndexAsync(run, Index(next)); await store.ConfirmCaptureTicketAsync(run, next, true);
        var saved = (await store.GetAsync(seed.Ticket))!; Require(saved.Detail is not null && !TicketLibraryStore.CanSkip(next, saved, context.ReaderSignature));
        await store.FinishCaptureAsync(run, "stopped", []);
    });
    await Check("New tickets join the resumed queue; missing tickets are not deleted", async () =>
    {
        var store = Store("new"); var a = Seed("41"); var b = Seed("42"); var run = await Start(store, a); await SaveBody(store, run, a); await store.FinishCaptureAsync(run, "paused", []);
        await store.ResumeCaptureAsync(run, context); await store.ReconcileCaptureIndexAsync(run, Index(b));
        var entries = await store.CaptureEntriesAsync(run); Require(entries.Total == 2 && entries.Items.Any(x => x.TicketId == "41" && x.State == "unavailable") && await store.GetAsync(a.Ticket) is not null);
        await store.FinishCaptureAsync(run, "stopped", []);
    });
    await Check("A resumed targeted retry never widens to unrelated tickets", async () =>
    {
        var store = Store("retry-scope"); var a = Seed("41"); var b = Seed("42"); var parent = await Start(store, a, b);
        await SaveBody(store, parent, a); await store.ConfirmCaptureTicketAsync(parent, a, true); await store.FinishCaptureAsync(parent, "completed", []);
        var targets = await store.CaptureRetryTargetsAsync(parent, context); Require(targets.Count == 1);
        var run = await store.BeginCaptureAsync(context, "retry", parent); await store.SetCaptureIndexAsync(run, Index(a, b), targets); await store.FinishCaptureAsync(run, "paused", []);
        await store.ResumeCaptureAsync(run, context); var keys = await store.ReconcileCaptureIndexAsync(run, Index(a, b, Seed("43")));
        Require(keys.Count == 1 && keys.Contains(TicketLibraryRules.Key(b.Ticket)) && (await store.CaptureEntriesAsync(run)).Total == 1);
        await store.FinishCaptureAsync(run, "stopped", []);
    });
    await Check("An abandoned process becomes a held interrupted checkpoint", async () =>
    {
        var store = Store("crash"); var run = await Start(store, Seed()); var state = await store.CollectionStateAsync(context.Fingerprint); state.State = "running"; state.RunId = run;
        await store.SaveCollectionStateAsync(context.Fingerprint, state, state.Revision); TicketLibraryStore.ReleaseCapture(run);
        var recovered = await new TicketLibraryStore(store.DatabasePath).CollectionStateAsync(context.Fingerprint);
        Require(recovered.State == "interrupted" && recovered.Held && (await store.GetCaptureAsync(run)).Status == "interrupted");
    });
    await Check("A killed process retains its committed body and resumes the same journal with its exact proof", async () =>
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--checkpoint-worker"); start.ArgumentList.Add(root);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Checkpoint worker did not start.");
        try
        {
            var run = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(run is { Length: 32 } && Guid.TryParseExact(run, "N", out _) && child.Id != Environment.ProcessId);
            child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var store = Store("process-checkpoint"); var seed = Seed(); var saved = (await store.GetAsync(seed.Ticket))!;
            var hash = saved.ContentHash; Require(saved.DetailComplete && !TicketLibraryRules.IsContentConfirmed(saved));
            var state = await store.CollectionStateAsync(context.Fingerprint);
            Require(state.RunId == run && state.State == "interrupted" && state.Held && state.NextAt is null);
            await store.ResumeCaptureAsync(run!, context); await store.ReconcileCaptureIndexAsync(run!, Index(seed));
            await store.ConfirmCaptureTicketAsync(run!, seed, true);
            saved = (await store.GetAsync(seed.Ticket))!;
            Require(saved.ContentHash == hash && TicketLibraryStore.CanSkip(seed, saved, context.ReaderSignature));
            await store.RecordCollectionResumeReuseAsync(run!, saved.Key); await store.FinishCaptureAsync(run!, "completed", []);
            var report = await store.GetCaptureAsync(run!);
            Require(report.RunId == run && report.Read == 1 && report.Reused == 0 && report.RetryCount == 0);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    });
    await Check("A second resume cannot own an already running journal", async () =>
    { var store = Store("double"); var run = await Start(store, Seed()); await Reject(() => store.ResumeCaptureAsync(run, context)); await store.FinishCaptureAsync(run, "stopped", []); });

    var source = Store("export-source"); var firstSeed = Seed("41"); var secondSeed = Seed("42"); var sourceRun = await Start(source, firstSeed, secondSeed);
    await SaveBody(source, sourceRun, firstSeed); await SaveBody(source, sourceRun, secondSeed); await source.ConfirmCaptureTicketAsync(sourceRun, firstSeed, true); await source.ConfirmCaptureTicketAsync(sourceRun, secondSeed, true); await source.FinishCaptureAsync(sourceRun, "completed", []);
    var package = Path.Combine(root, "initial.glpilibrary.zip");
    await Check("Bootstrap exports only ticket documents and a checked manifest, not database or settings", async () =>
    {
        await source.ExportBootstrapAsync(package, installation); var preview = LibraryBootstrapFormat.Inspect(package, installation);
        Require(preview.Manifest.Records == 2 && preview.Complete == 2 && preview.Confirmed == 2);
        using var archive = ZipFile.OpenRead(package); Require(archive.Entries.Count == 2 && archive.GetEntry("manifest.json") is not null && archive.GetEntry("tickets.jsonl") is not null);
    });
    var destination = Store("import-destination");
    await Check("Bootstrap import rebuilds search and retains original proven content without sharing it automatically", async () =>
    {
        var preview = LibraryBootstrapFormat.Inspect(package, installation); var result = await destination.ImportBootstrapAsync(package, preview.ArchiveSha256, installation);
        Require(result.Added == 2 && result.Conflicts == 0 && (await destination.GetStateAsync()).PendingCount == 0);
        Require((await destination.SearchAsync(new() { Text = "Solution", ResolvedOnly = true })).Total == 2);
        Require((await destination.CollectionStateAsync(context.Fingerprint)).Options.Automatic == false);
        Require(TicketLibraryStore.CanSkip(firstSeed, await destination.GetAsync(firstSeed.Ticket), context.ReaderSignature));
    });
    await Check("Repeated import is idempotent and does not reset local revision or identity", async () =>
    {
        var before = (await destination.GetAsync(firstSeed.Ticket))!.LocalRevision; var preview = LibraryBootstrapFormat.Inspect(package, installation);
        var result = await destination.ImportBootstrapAsync(package, preview.ArchiveSha256, installation);
        Require(result.Identical == 2 && result.Added == 0 && (await destination.GetAsync(firstSeed.Ticket))!.LocalRevision == before);
    });
    await Check("Import refuses other installations and an archive changed after preview", async () =>
    {
        await Reject(() => { LibraryBootstrapFormat.Inspect(package, "https://other.example/glpi"); return Task.CompletedTask; });
        await Reject(() => destination.ImportBootstrapAsync(package, new string('0', 64), installation));
    });
    await Check("Late payload corruption rolls back every row, including a valid first document", async () =>
    {
        var corrupt = CopyPackage("late", package, (zip, entry) => { if (entry.FullName == "tickets.jsonl") { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd().Replace("Solution originale", "Solution altérée"); } return null; });
        var empty = Store("late"); using var stream = File.OpenRead(corrupt); var digest = LibraryBootstrapFormat.Digest(stream);
        await Reject(() => empty.ImportBootstrapAsync(corrupt, digest, installation)); Require((await empty.GetStateAsync()).TicketCount == 0);
    });
    await Check("Unexpected ZIP paths and duplicated manifest properties are rejected", async () =>
    {
        var extra = Path.Combine(root, "extra.glpilibrary.zip"); File.Copy(package, extra);
        using (var zip = ZipFile.Open(extra, ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("../private.json").Open()); writer.Write("{}"); }
        await Reject(() => { LibraryBootstrapFormat.Inspect(extra, installation); return Task.CompletedTask; });
        var duplicate = CopyPackage("duplicate", package, (zip, entry) => { if (entry.FullName != "manifest.json") return null; using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd().Replace("\"version\":1", "\"version\":1,\"version\":1"); });
        await Reject(() => { LibraryBootstrapFormat.Inspect(duplicate, installation); return Task.CompletedTask; });
    });
    await Check("Import during another local capture is refused without altering either journal", async () =>
    {
        var store = Store("busy-import"); var run = await Start(store, Seed()); var preview = LibraryBootstrapFormat.Inspect(package, installation);
        await Reject(() => store.ImportBootstrapAsync(package, preview.ArchiveSha256, installation)); Require((await store.GetStateAsync()).TicketCount == 0);
        await store.FinishCaptureAsync(run, "stopped", []);
    });
    await Check("Unconfirmed imported bodies stay unconfirmed and carry no foreign pending lease", async () =>
    {
        var store = Store("unconfirmed"); var seed = Seed(); var run = await Start(store, seed); await SaveBody(store, run, seed); await store.FinishCaptureAsync(run, "paused", []);
        var path = Path.Combine(root, "unconfirmed.glpilibrary.zip"); await store.ExportBootstrapAsync(path, installation); var preview = LibraryBootstrapFormat.Inspect(path, installation);
        var other = Store("unconfirmed-other"); await other.ImportBootstrapAsync(path, preview.ArchiveSha256, installation); var read = (await other.GetAsync(seed.Ticket))!;
        Require(read.DetailComplete && !read.DetailsCurrent && !read.ConfirmationPending && read.ConfirmationId == "");
    });
#if !COLLECTION_CORE_ONLY
    async Task<JsonElement> RuntimeCall(PilotApplication app, string operation, object args) => JsonSerializer.SerializeToElement(
        await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args, PilotApplication.Json)), PilotApplication.Json);
    async Task WithRuntimePreferences(LibraryPreferences preferences, Func<Task> action)
    {
        var path = Path.Combine(AppPaths.DataRoot, "library.local.json"); var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try { new LibraryPreferencesStore().Save(preferences); await action(); }
        finally { if (previous is null) File.Delete(path); else File.WriteAllBytes(path, previous); }
    }
    await Check("A library folder that becomes a file keeps a visible memory journal and final operation failure without starting the browser", async () =>
    {
        var folder = Path.Combine(root, "blocked-runtime-library");
        await WithRuntimePreferences(new() { LocalFolder = folder }, async () =>
        {
            File.WriteAllText(folder, "Synthetic storage obstruction.");
            await using var app = new PilotApplication(new NullHost(), (_, _) => { }, new() { PortalUrl = installation + "/front/ticket.php" });
            await Reject(async () => { await RuntimeCall(app, "library.capture", new { mode = "incremental", confirmedGlobal = true }); });
            var activity = await RuntimeCall(app, "library.collection.status", new { });
            var events = await RuntimeCall(app, "library.collection.events", new { });
            Require(activity.GetProperty("state").GetString() == "attention" && activity.GetProperty("errorCode").GetString() == "local-storage"
                && events.GetProperty("runId").GetString() == "" && !events.GetProperty("persisted").GetBoolean()
                && events.GetProperty("items").EnumerateArray().Any(e => e.GetProperty("code").GetString() == "local-storage"));
            var history = await RuntimeCall(app, "operations.list", new { });
            Require(history.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("title").GetString() == "Collecte globale des tickets"
                && e.GetProperty("code").GetString() == "local-storage" && e.GetProperty("state").GetString() == "Échec"));
        });
    });
    await Check("An unreadable preferences file cannot leave a collection operation in progress or hide its memory diagnostics", async () =>
    {
        await WithRuntimePreferences(new() { LocalFolder = Path.Combine(root, "locked-preferences-library") }, async () =>
        {
            using var held = new FileStream(Path.Combine(AppPaths.DataRoot, "library.local.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await using var app = new PilotApplication(new NullHost(), (_, _) => { }, new() { PortalUrl = installation + "/front/ticket.php" });
            await Reject(async () => { await RuntimeCall(app, "library.capture", new { mode = "incremental", confirmedGlobal = true }); });
            var activity = await RuntimeCall(app, "library.collection.status", new { });
            var events = await RuntimeCall(app, "library.collection.events", new { });
            var history = await RuntimeCall(app, "operations.list", new { });
            Require(activity.GetProperty("state").GetString() == "attention" && activity.GetProperty("errorCode").GetString() == "local-storage"
                && events.GetProperty("total").GetInt32() > 0 && !events.GetProperty("persisted").GetBoolean()
                && history.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("title").GetString() == "Collecte globale des tickets"
                    && e.GetProperty("code").GetString() == "local-storage" && e.GetProperty("state").GetString() == "Échec"));
        });
    });
    await Check("A held shared collector lease retains its coordination cause in both journals before any browser read", async () =>
    {
        var share = Path.Combine(root, "held-runtime-share"); Directory.CreateDirectory(share);
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(root, "held-runtime-library"), SyncMode = "folder", SharedFolder = share };
        await WithRuntimePreferences(preferences, async () =>
        {
            await using var held = await LibraryCollectorLease.AcquireAsync(preferences, installation, "", default);
            await using var app = new PilotApplication(new NullHost(), (_, _) => { }, new() { PortalUrl = installation + "/front/ticket.php" });
            await Reject(async () => { await RuntimeCall(app, "library.capture", new { mode = "incremental", confirmedGlobal = true }); });
            var activity = await RuntimeCall(app, "library.collection.status", new { });
            var events = await RuntimeCall(app, "library.collection.events", new { });
            var history = await RuntimeCall(app, "operations.list", new { });
            Require(activity.GetProperty("errorCode").GetString() == "collection-coordination" && events.GetProperty("runId").GetString() == ""
                && events.GetProperty("items").EnumerateArray().Any(e => e.GetProperty("code").GetString() == "collection-coordination")
                && history.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("title").GetString() == "Collecte globale des tickets"
                    && e.GetProperty("code").GetString() == "collection-coordination"));
        });
    });
    await Check("Runtime exposes narrow status/stop controls but an obsolete session cannot cancel other operations", async () =>
    {
        await using var app = new PilotApplication(new NullHost(), (_, _) => { }, new() { PortalUrl = installation + "/front/ticket.php" });
        var args = JsonSerializer.SerializeToElement(new { action = "pause", sessionId = new string('a', 32) });
        var result = JsonSerializer.SerializeToElement(await app.ExecuteAsync("library.collection.control", args), PilotApplication.Json);
        Require(!result.GetProperty("requested").GetBoolean());
        Require(app.Operations.Contains("library.bootstrap.apply") && app.Operations.Contains("library.collection.settings"));
    });
    await Check("Runtime requires explicit designation before enabling scheduled collection", async () =>
    {
        await using var app = new PilotApplication(new NullHost(), (_, _) => { }, new() { PortalUrl = installation + "/front/ticket.php" });
        await Reject(async () => { await app.ExecuteAsync("library.collection.settings", JsonSerializer.SerializeToElement(new { revision = 0, options = new LibraryCollectionOptions { Automatic = true } }, PilotApplication.Json)); });
    });
#endif
}
finally
{
    Console.WriteLine($"{passed}/{total} collection and bootstrap checks passed.");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (!checkpointWorker) Directory.Delete(root, true);
    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", null); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", null);
}
return passed == total ? 0 : 1;

async Task Check(string label, Func<Task> test) { total++; try { await test(); passed++; Console.WriteLine("PASS " + label); } catch (Exception error) { Console.WriteLine("FAIL " + label + "\n" + error); } }
static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Collection invariant failed."); }
static string FileHash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException or JsonException) { return; } throw new Exception("Expected refusal."); }
static async Task RejectCancelled(Func<Task> action) { try { await action(); } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation."); }
TicketLibraryStore Store(string name) => new(Path.Combine(root, name + ".db"));
TicketLibrarySeed Seed(string id = "42") => new() { Ticket = new(id, installation + "/front/ticket.form.php?id=" + id), Title = "Ticket fictif " + id, Status = "Résolu", StatusCode = 5, LastModifiedRaw = "2025-01-01 12:00", LastModifiedAt = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), LastModifiedReliable = true, LastModifiedPrecision = "minutes" };
TicketLibraryIndex Index(params TicketLibrarySeed[] seeds) => new() { SourceUrl = installation + "/front/ticket.php", AllStatuses = true, Complete = true, ScopeFingerprint = "fixture-entity", Tickets = seeds.ToList(), ExpectedTotal = seeds.Length };
async Task<string> Start(TicketLibraryStore store, params TicketLibrarySeed[] seeds) { var id = await store.BeginCaptureAsync(context, "incremental"); await store.SetCaptureIndexAsync(id, Index(seeds)); return id; }
async Task SaveBody(TicketLibraryStore store, string run, TicketLibrarySeed seed, string? reader = null, string solution = "Solution originale")
{
    var before = await store.ObserveCaptureSeedAsync(run, seed); await store.RecordCaptureReadingAsync(run, TicketLibraryRules.Key(seed.Ticket));
    await store.SaveCaptureDetailAsync(run, seed, new() { ReaderSignature = reader ?? context.ReaderSignature, Coverage = new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 }, Detail = new() {
        Ticket = seed.Ticket, Title = seed.Title, ReadAt = DateTimeOffset.UtcNow.AddMinutes(-5), Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
        Sections = [new() { Name = "Chronologie", Kind = "timeline", TabKey = "treatment", Data = new() { Headers = ["Type", "Date", "Auteur", "Contenu"], Rows = [new[] { "Solution", "2025-01-01", "Auteur fictif", solution }], Complete = true } }] } }, before.Document.LocalRevision);
}
string CopyPackage(string name, string input, Func<ZipArchive, ZipArchiveEntry, string?> replace)
{
    var output = Path.Combine(root, name + ".glpilibrary.zip");
    using var source = ZipFile.OpenRead(input); using var target = ZipFile.Open(output, ZipArchiveMode.Create);
    foreach (var entry in source.Entries) { using var stream = target.CreateEntry(entry.FullName).Open(); var text = replace(source, entry); if (text is not null) { var data = Encoding.UTF8.GetBytes(text); stream.Write(data); } else { using var original = entry.Open(); original.CopyTo(stream); } }
    return output;
}
#if !COLLECTION_CORE_ONLY
sealed class NullHost : IPilotHost
{
    public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task SetClipboardAsync(string text) => Task.CompletedTask;
    public Task OpenExternalAsync(string url) => Task.CompletedTask;
    public Task CloseAsync() => Task.CompletedTask;
    public Task<byte[]> CapturePngAsync() => Task.FromResult(Array.Empty<byte>());
    public Task PrintPdfAsync(string path) => Task.CompletedTask;
    public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
}
#endif
