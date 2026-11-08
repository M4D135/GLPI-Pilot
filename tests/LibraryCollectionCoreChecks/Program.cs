using System.Diagnostics;
using System.IO.Compression;
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
        async Task<(TicketLibraryDocument Kept, TicketLibraryCaptureReport Report)> Run((TicketLibraryStore Store, TicketLibrarySeed Seed) at, bool? scope)
        {
            var run = await Start(at.Store, at.Seed); var observed = await at.Store.ObserveCaptureSeedAsync(run, at.Seed);
            Require(TicketLibraryStore.CanSkip(at.Seed, observed.Document, context.ReaderSignature));
            await at.Store.RecordCollectionReuseAsync(run, observed.Document.Key, awaitingConfirmation: true);
            if (scope is { } verified) await at.Store.ConfirmCaptureTicketAsync(run, at.Seed, verified);
            await at.Store.FinishCaptureAsync(run, scope is null ? "stopped" : "completed", []);
            return ((await at.Store.GetAsync(at.Seed.Ticket))!, await at.Store.GetCaptureAsync(run));
        }
        var demoted = await Run(await Kept("earlier-conflict", true), true);
        Require(!demoted.Kept.DetailsCurrent && demoted.Report.Unconfirmed == 1 && demoted.Report.Conflicts == 0);
        var partialScope = await Run(await Kept("earlier-scope", false), false);
        Require(partialScope.Kept.ConfirmationPending && !partialScope.Kept.DetailsCurrent && partialScope.Report.Unconfirmed == 1);
        var stopped = await Run(await Kept("earlier-stopped", false), null);
        Require(stopped.Kept.ConfirmationPending && stopped.Report.Unconfirmed == 1 && stopped.Report.RetryCount == 0);
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
        Require(kept.ConfirmationPending && !kept.DetailsCurrent && report.Unconfirmed == 1 && report.RetryCount == 0 && report.Conflicts == 0 && (await store.GetStateAsync()).ConflictCount == 0);
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
