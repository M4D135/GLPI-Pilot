using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;

internal static class TicketLibraryCaptureChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> check)
    {
        TicketLibraryStore Store(string name) => new(Path.Combine(root, "capture-" + name + ".db"));
        await check("Capture: received content remains durable but unconfirmed after final verification failure", async () =>
        {
            var store = Store("failed-final"); var seed = Seed(); var run = await Start(store, seed);
            await Read(store, run, seed);
            await store.FinishCaptureVerificationAsync(run, false, false);
            await store.FinishCaptureAsync(run, "completed", ["Limite synthétique sans contenu de fiche"]);
            var reopened = new TicketLibraryStore(store.DatabasePath); var report = (await reopened.LatestCaptureAsync())!;
            var saved = (await reopened.GetAsync(seed.Ticket))!;
            Require(report.Read == 1 && report.Unconfirmed == 1 && report.RetryCount == 1 && !report.FinalScopeVerified);
            Require(saved is { DetailComplete: true, DetailsCurrent: false, ConfirmationPending: true } && saved.ConfirmationId == run);
            Require(!TicketLibraryStore.CanSkip(seed, saved, TicketLibraryAcquisition.ReaderSignature));
            Require((await reopened.SearchAsync(new() { ResolvedOnly = true })).Total == 0);
            Require((await reopened.CaptureEntriesAsync(run)).Items.Single().Category == "final-check-failed");
            Require((await TicketLibrarySyncService.Pending(store, 100, default)).Any(m => m.Document.ConfirmationPending && !m.Document.DetailsCurrent));
        });
        await check("Capture: counts describe this partial attempt even when an older complete body is retained", async () =>
        {
            var store = Store("partial"); var seed = Seed(); await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "trusted-reader");
            var run = await Start(store, seed); var initial = await store.ObserveCaptureSeedAsync(run, seed);
            var attempt = Detail(seed, "Lecture incomplète"); attempt.Sections[0].Data.Complete = false;
            await store.SaveCaptureDetailAsync(run, seed, new() { Detail = attempt, Coverage = new() { Complete = false, TabsDiscovered = 2, TabsLoaded = 1 } }, initial.Document.LocalRevision);
            await store.FinishCaptureAsync(run, "completed", []);
            var report = await store.GetCaptureAsync(run); var saved = (await store.GetAsync(seed.Ticket))!;
            Require(report.Read == 1 && report.Partial == 1 && report.Processed == 1 && report.RetryCount == 1 && saved.DetailComplete);
            Require(saved.Detail!.Sections[0].Data.Rows[0][3] == "Solution synthétique originale");
            Require((await store.CaptureEntriesAsync(run)).Items.Single().Message.Contains("ancienne copie complète", StringComparison.Ordinal));
        });
        await check("Capture: the exact confirmation creates an outbox mutation and reaches another SQLite client", async () =>
        {
            var a = Store("confirmation-a"); var b = Store("confirmation-b"); var seed = Seed(); var run = await Start(a, seed); await Read(a, run, seed);
            var pending = (await a.GetAsync(seed.Ticket))!; var identity = Guid.NewGuid().ToString("N");
            await TicketLibrarySyncService.Prepare(b, identity, default);
            await TicketLibrarySyncService.Apply(b, identity, 0, [new(1, pending, null, "inserted")], default);
            Require((await b.GetAsync(seed.Ticket)) is { DetailsCurrent: false, ConfirmationPending: true });
            var before = (await TicketLibrarySyncService.Pending(a, 100, default)).Count;
            await a.ConfirmCaptureTicketAsync(run, seed, true); await a.FinishCaptureVerificationAsync(run, true, true); await a.FinishCaptureAsync(run, "completed", []);
            var confirmed = (await a.GetAsync(seed.Ticket))!;
            Require(confirmed.ContentHash == pending.ContentHash && TicketLibraryRules.IsContentConfirmed(confirmed));
            Require((await TicketLibrarySyncService.Pending(a, 100, default)).Count > before);
            await TicketLibrarySyncService.Apply(b, identity, 1, [new(2, confirmed, null, "identical")], default);
            Require(TicketLibraryRules.IsContentConfirmed(await b.GetAsync(seed.Ticket)));
            Require((await b.SearchAsync(new() { ResolvedOnly = true })).Rows.Single().Citations.Any(c => c.IsSolution && c.Current));
            Require((await a.GetCaptureAsync(run)).RetryCount == 0);
        });
        await check("Capture: a later interrupted read of identical content cannot borrow the old attempt confirmation", async () =>
        {
            var store = Store("repeat"); var seed = Seed(); var first = await Start(store, seed); await Read(store, first, seed);
            await store.ConfirmCaptureTicketAsync(first, seed, true); await store.FinishCaptureAsync(first, "completed", []);
            var oldProof = (await store.GetAsync(seed.Ticket))!;
            var run = await Start(store, seed); await Read(store, run, seed); await store.FinishCaptureAsync(run, "cancelled", []);
            var pending = (await store.GetAsync(seed.Ticket))!;
            Require(pending.ConfirmationPending && pending.ConfirmationId == run && pending.ContentHash == oldProof.ContentHash);
            var replay = TicketLibraryRules.Merge(pending, oldProof).Document;
            Require(!replay.DetailsCurrent && replay.ConfirmationPending && replay.ConfirmationId == run);
            var retry = await store.CaptureRetryTargetsAsync(run, Context()); var newRun = await store.BeginCaptureAsync(Context(), "retry", run);
            await store.SetCaptureIndexAsync(newRun, Index(seed), retry); await Read(store, newRun, seed);
            await store.ConfirmCaptureTicketAsync(newRun, seed, true); await store.FinishCaptureAsync(newRun, "completed", []);
            Require(TicketLibraryRules.IsContentConfirmed(await store.GetAsync(seed.Ticket)));
        });
        await check("Capture: a real same-version conflict cannot be promoted by a matching hash proof replay", async () =>
        {
            var store = Store("conflict"); var seed = Seed(); var first = await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "trusted-reader");
            var run = await Start(store, seed); await Read(store, run, seed, "Version réellement différente");
            await store.ConfirmCaptureTicketAsync(run, seed, true); await store.FinishCaptureAsync(run, "completed", []);
            var retained = (await store.GetAsync(seed.Ticket))!;
            Require(!retained.DetailsCurrent && !retained.ConfirmationPending && retained.Conflicts > 0);
            Require(!TicketLibraryRules.Merge(retained, first.Document).Document.DetailsCurrent);
            Require((await store.GetCaptureAsync(run)).Conflicts == 1 && (await store.GetCaptureAsync(run)).RetryCount == 1);
        });
        await check("Capture: final scope and source-version changes cannot confirm a received body", async () =>
        {
            var store = Store("source-change"); var seed = Seed(); var run = await Start(store, seed); await Read(store, run, seed);
            var changed = Seed(); changed.LastModifiedAt = changed.LastModifiedAt!.Value.AddMinutes(1); changed.LastModifiedRaw = "2026-10-20 12:35";
            await store.ConfirmCaptureTicketAsync(run, changed, true); await store.FinishCaptureAsync(run, "completed", []);
            Require(!(await store.GetAsync(seed.Ticket))!.DetailsCurrent);
            Require((await store.CaptureEntriesAsync(run)).Items.Single().Category == "changed-during-capture");
            var second = Store("scope-change"); var otherRun = await Start(second, seed); await Read(second, otherRun, seed);
            await second.ConfirmCaptureTicketAsync(otherRun, seed, false); await second.FinishCaptureAsync(otherRun, "completed", []);
            Require((await second.CaptureEntriesAsync(otherRun)).Items.Single().Category == "scope-not-confirmed");
            Require(!TicketLibraryRules.IsContentConfirmed(await second.GetAsync(seed.Ticket)));
        });
        await check("Capture: a missing modification date remains consultable and explicitly unconfirmed", async () =>
        {
            var store = Store("no-date"); var seed = Seed(); seed.LastModifiedRaw = ""; seed.LastModifiedAt = null; seed.LastModifiedReliable = false; seed.LastModifiedPrecision = "unknown";
            var run = await Start(store, seed); await Read(store, run, seed); await store.ConfirmCaptureTicketAsync(run, seed, true); await store.FinishCaptureAsync(run, "completed", []);
            Require((await store.GetAsync(seed.Ticket))!.DetailComplete && !(await store.GetAsync(seed.Ticket))!.DetailsCurrent);
            Require((await store.CaptureEntriesAsync(run)).Items.Single().Category == "missing-last-modified");
        });
        await check("Capture: retry is bound to its context and authorizes only keys in the newly observed index", async () =>
        {
            var store = Store("retry-scope"); var a = Seed("41"); var b = Seed("42"); var run = await Start(store, a, b);
            await store.RecordCaptureFailureAsync(run, TicketLibraryRules.Key(a.Ticket), "read-timeout"); await store.FinishCaptureAsync(run, "cancelled", []);
            var changedContext = Context() with { Fingerprint = new string('b', 64) };
            await Refuse(() => store.CaptureRetryTargetsAsync(run, changedContext));
            var targets = await store.CaptureRetryTargetsAsync(run, Context()); Require(targets.Count == 2);
            var retry = await store.BeginCaptureAsync(Context(), "retry", run); await store.SetCaptureIndexAsync(retry, Index(b), targets);
            Require((await store.CaptureEntriesAsync(retry)).Items.Single(e => e.TicketId == "41").Category == "no-longer-visible");
            await Refuse(() => store.ObserveCaptureSeedAsync(retry, a));
            var changedSeed = Seed("42"); changedSeed.Title = "Une autre version"; await Refuse(() => store.ObserveCaptureSeedAsync(retry, changedSeed));
            await store.FinishCaptureAsync(retry, "cancelled", []);
            var other = await store.BeginCaptureAsync(Context(), "retry", run); var foreign = Index(a); foreign.ScopeFingerprint = "other-entity";
            await Refuse(() => store.SetCaptureIndexAsync(other, foreign, targets)); await store.FinishCaptureAsync(other, "failed", []);
        });
        await check("Capture: abandoned runs recover after reopening and diagnostics are bounded, paged and sanitized", async () =>
        {
            var store = Store("journal"); var seeds = Enumerable.Range(1, 130).Select(i => Seed(i.ToString())).ToArray(); var run = await Start(store, seeds);
            await store.RecordCaptureFailureAsync(run, TicketLibraryRules.Key(seeds[0].Ticket), "read-timeout");
            await Refuse(() => store.RecordCaptureFailureAsync(run, TicketLibraryRules.Key(seeds[1].Ticket), "private exception with password"));
            TicketLibraryStore.ReleaseCapture(run); var reopened = new TicketLibraryStore(store.DatabasePath); var latest = (await reopened.LatestCaptureAsync())!;
            Require(latest.Status == "interrupted" && latest.Failed == 1 && latest.RetryCount == 130 && latest.Processed == 1);
            var page = await reopened.CaptureEntriesAsync(run, 0, 2000, true); Require(page.Limit == 100 && page.Items.Count == 100 && page.Total == 130);
            var next = await reopened.CaptureEntriesAsync(run, 100, 100, true); Require(next.Items.Count == 30 && !page.Items.Select(e => e.TicketKey).Intersect(next.Items.Select(e => e.TicketKey)).Any());
            Require(!JsonSerializer.Serialize(page).Contains("Solution synthétique", StringComparison.Ordinal));
        });
        await check("Capture: legacy v1 content and citations remain available without claiming a new freshness proof", async () =>
        {
            var store = Store("legacy"); var seed = Seed(); var saved = (await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "glpi-browser-library-v1")).Document;
            var node = JsonNode.Parse(TicketLibraryRules.Serialize(saved))!.AsObject(); node.Remove("confirmationId"); node.Remove("confirmationPending"); node.Remove("confirmedContentHash");
            var oldJson = node.ToJsonString();
            await store.Run(c => TicketLibraryStore.Execute(c, null, "UPDATE ticket_library_tickets SET document_json=$json WHERE ticket_key=$key", ("$json", oldJson), ("$key", saved.Key)), default);
            var legacy = (await store.GetAsync(seed.Ticket))!; Require(legacy.DetailComplete && !legacy.DetailsCurrent && legacy.ConfirmationPending);
            Require((await store.SearchAsync(new() { ResolvedOnly = true })).Total == 0);
            Require((await store.SearchAsync(new() { Text = "Solution" })).Rows.Single().Citations.All(c => !c.Current && !c.IsSolution));
            var export = Path.Combine(root, "legacy-capture.jsonl"); await store.ExportAsync(export);
            Require(!File.ReadAllText(export).Contains("\"current\":true", StringComparison.Ordinal));
            var newlyConfirmed = TicketLibraryRules.FromDetail(seed, Detail(seed), Coverage(), TicketLibraryAcquisition.ReaderSignature);
            var confirmedElsewhere = TicketLibraryRules.Merge(legacy, newlyConfirmed).Document;
            Require(TicketLibraryRules.IsContentConfirmed(confirmedElsewhere) && confirmedElsewhere.ConfirmationId == newlyConfirmed.ConfirmationId
                && confirmedElsewhere.ReaderSignature == TicketLibraryAcquisition.ReaderSignature);
            var later = JsonSerializer.Deserialize<TicketLibraryDocument>(oldJson, TicketLibraryRules.Json)!;
            later.ReadAt = saved.ReadAt!.Value.AddDays(2); later.Detail!.ReadAt = later.ReadAt.Value; later.ReaderSignature = TicketLibraryAcquisition.ReaderSignature;
            var stillConfirmed = TicketLibraryRules.Merge(saved, later).Document;
            Require(TicketLibraryRules.IsContentConfirmed(stillConfirmed) && stillConfirmed.ReadAt == saved.ReadAt && stillConfirmed.ReaderSignature == saved.ReaderSignature);
            var pendingReplay = TicketLibraryRules.Copy(later); pendingReplay.DetailsCurrent = false; pendingReplay.ConfirmationPending = true; pendingReplay.ConfirmationId = saved.ConfirmationId;
            var replayed = TicketLibraryRules.Merge(saved, pendingReplay).Document;
            Require(TicketLibraryRules.IsContentConfirmed(replayed) && replayed.ReadAt == saved.ReadAt && replayed.ReaderSignature == saved.ReaderSignature);
            var pendingRun = await Start(store, seed); await Read(store, pendingRun, seed);
            Require(!TicketLibraryRules.Merge((await store.GetAsync(seed.Ticket))!, later).Document.DetailsCurrent);
            await store.ConfirmCaptureTicketAsync(pendingRun, seed, true); await store.FinishCaptureAsync(pendingRun, "completed", []);
            Require(TicketLibraryRules.IsContentConfirmed(await store.GetAsync(seed.Ticket)));
        });
        await check("Capture: twenty retained runs bound journal and pending-proof rows without erasing ticket bodies", async () =>
        {
            var store = Store("retention"); var seed = Seed(); var first = await Start(store, seed); await Read(store, first, seed); await store.FinishCaptureAsync(first, "cancelled", []);
            for (var i = 0; i < TicketLibraryStore.CaptureHistoryLimit; i++)
            { var run = await store.BeginCaptureAsync(Context(), "incremental"); await store.FinishCaptureAsync(run, "failed", []); }
            var counts = await store.Run(c => (TicketLibraryStore.ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_capture_runs"), TicketLibraryStore.ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_capture_pending")), default);
            Require(counts.Item1 == 20 && counts.Item2 == 0);
            Require((await store.GetAsync(seed.Ticket)) is { DetailComplete: true, ConfirmationPending: true });
            await Refuse(() => store.GetCaptureAsync(first));
        });
    }

    public static TicketLibraryCaptureContext Context() => new("https://capture.example/glpi", new string('a', 64), TicketLibraryAcquisition.ReaderSignature);
    public static TicketLibraryIndex Index(params TicketLibrarySeed[] seeds) => new()
    {
        SourceUrl = "https://capture.example/glpi/front/ticket.php", AllStatuses = true, Complete = true, ScopeFingerprint = "capture-entity-a",
        Tickets = seeds.ToList(), ExpectedTotal = seeds.Length
    };
    public static TicketLibrarySeed Seed(string id = "42") => new()
    {
        Ticket = new(id, "https://capture.example/glpi/front/ticket.form.php?id=" + id), Title = "Ticket synthétique " + id, Status = "Résolu", StatusCode = 5,
        LastModifiedRaw = "2026-10-20 12:34", LastModifiedAt = new(2026, 10, 20, 12, 34, 0, TimeSpan.Zero), LastModifiedReliable = true, LastModifiedPrecision = "minutes"
    };
    private static TicketLibraryCoverage Coverage() => new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1 };
    public static TicketDetailResult Detail(TicketLibrarySeed seed, string text = "Solution synthétique originale") => new()
    {
        Ticket = seed.Ticket, Title = seed.Title, ReadAt = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero), Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
        Sections = [new() { Name = "Chronologie", Kind = "timeline", TabKey = "treatment", Data = new() { Headers = ["Type", "Date", "Auteur", "Contenu"], Rows = [new[] { "Solution", "2026-10-20", "Auteur fictif", text }], Complete = true } }]
    };
    public static async Task<string> Start(TicketLibraryStore store, params TicketLibrarySeed[] seeds)
    { var run = await store.BeginCaptureAsync(Context(), "incremental"); await store.SetCaptureIndexAsync(run, Index(seeds)); return run; }
    public static async Task Read(TicketLibraryStore store, string run, TicketLibrarySeed seed, string text = "Solution synthétique originale")
    {
        var observed = await store.ObserveCaptureSeedAsync(run, seed);
        await store.RecordCaptureReadingAsync(run, TicketLibraryRules.Key(seed.Ticket));
        await store.SaveCaptureDetailAsync(run, seed, new() { Detail = Detail(seed, text), Coverage = Coverage() }, observed.Document.LocalRevision);
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Capture invariant failed."); }
    private static async Task Refuse(Func<Task> action)
    { try { await action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { return; } throw new InvalidOperationException("Expected capture refusal."); }
}
