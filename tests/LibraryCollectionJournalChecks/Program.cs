using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Journal-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var context = new TicketLibraryCaptureContext("https://journal.example/glpi", new string('a', 64), TicketLibraryAcquisition.ReaderSignature);
var passed = 0; var total = 0;
try
{
    await Check("Initial index failure is durable with zero tickets, including after reopening the database", async () =>
    {
        var store = Store("initial"); var run = await store.BeginCaptureAsync(context, "incremental");
        await store.AppendCollectionEventAsync(run, context, Event("index-start", "Ouverture de l’index."));
        await store.AppendCollectionEventAsync(run, context, Event("index-http-error", "Accès à l’index refusé (HTTP 403).", "error"));
        await store.FinishCaptureAsync(run, "failed", ["Accès à l’index refusé."]);
        var reopened = Store("initial"); var report = await reopened.GetCaptureAsync(run);
        var page = await reopened.CollectionEventsAsync(run, context);
        Require(report.Observed == 0 && report.RetryCount == 0 && page.Persisted && page.Total == 2 && page.Items[0].Code == "index-http-error"
            && page.Items[0].Stage == "Lecture de l’index" && page.Items[0].Level == "error");
    });
    await Check("Journal reads and writes cannot cross source fingerprints or GLPI installations", async () =>
    {
        var store = Store("scope"); var run = await store.BeginCaptureAsync(context, "incremental");
        await store.AppendCollectionEventAsync(run, context, Event("start", "Lecture en cours."));
        foreach (var other in new[] { context with { Fingerprint = new string('b', 64) }, context with { Installation = "https://other.example/glpi" } })
        {
            await Reject(() => store.CollectionEventsAsync(run, other));
            await Reject(() => store.AppendCollectionEventAsync(run, other, Event("start", "Autre contexte.")));
            Require((await store.CollectionEventsAsync("", other)).Total == 0);
        }
        await store.FinishCaptureAsync(run, "completed", []);
    });
    await Check("Newest-first pages retain distinct ordered events while the run is active", async () =>
    {
        var store = Store("pages"); var run = await store.BeginCaptureAsync(context, "incremental");
        for (var i = 0; i < 7; i++) await store.AppendCollectionEventAsync(run, context, Event("page-" + i, "Page " + i));
        var first = await store.CollectionEventsAsync(run, context, 0, 3); var second = await store.CollectionEventsAsync(run, context, 3, 3);
        Require(first.Total == 7 && first.Items.Select(x => x.Code).SequenceEqual(["page-6", "page-5", "page-4"])
            && second.Items.Select(x => x.Code).SequenceEqual(["page-3", "page-2", "page-1"])
            && first.Items.Last().Sequence > second.Items.First().Sequence);
        await store.FinishCaptureAsync(run, "completed", []);
    });
    await Check("Resuming a capture appends diagnostics without deleting its earlier failure", async () =>
    {
        var store = Store("resume"); var run = await store.BeginCaptureAsync(context, "incremental");
        await store.AppendCollectionEventAsync(run, context, Event("index-network-error", "La connexion a été interrompue.", "error"));
        await store.FinishCaptureAsync(run, "failed", ["Connexion interrompue."]);
        await store.ResumeCaptureAsync(run, context);
        await store.AppendCollectionEventAsync(run, context, Event("collection-resume", "Reprise de l’index."));
        await store.FinishCaptureAsync(run, "completed", []);
        await store.AppendCollectionEventAsync(run, context, Event("collection-completed", "Passage terminé."));
        Require((await store.CollectionEventsAsync(run, context)).Items.Select(x => x.Code)
            .SequenceEqual(["collection-completed", "collection-resume", "index-network-error"]));
    });
    await Check("A cancelled write cannot leave a diagnostic that claims it was committed", async () =>
    {
        var store = Store("cancel"); var run = await store.BeginCaptureAsync(context, "incremental");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await store.AppendCollectionEventAsync(run, context, Event("cancelled", "Événement annulé."), cancellation.Token); throw new Exception("Cancelled write succeeded."); }
        catch (OperationCanceledException) { }
        Require((await store.CollectionEventsAsync(run, context)).Total == 0);
        await store.FinishCaptureAsync(run, "cancelled", []);
    });
    await Check("Buffered preparation events are persisted in order when the capture identity is known", async () =>
    {
        var store = Store("writer"); var failed = 0;
        var journal = new LibraryCollectionJournalSession(store, context, () => failed++);
        journal.Append(Event("prepare", "Préparation du journal."));
        var run = await store.BeginCaptureAsync(context, "incremental"); journal.Bind(run);
        journal.Append(Event("index-start", "Lecture de l’index."));
        await journal.CompleteAsync(default);
        Require(failed == 0 && journal.Persisted && !journal.Append(Event("late", "Réponse tardive ignorée.")));
        Require((await store.CollectionEventsAsync(run, context)).Items.Select(x => x.Code).SequenceEqual(["index-start", "prepare"]));
        await store.FinishCaptureAsync(run, "completed", []);
    });
    await Check("Failure before database capture remains readable and is explicitly not persisted", async () =>
    {
        var journal = new LibraryCollectionJournalSession(null, context, () => throw new Exception("No database write was expected."));
        journal.Append(Event("collection-coordination", "Un autre poste détient la collecte.", "error"));
        await journal.CompleteAsync(default); var page = journal.MemoryPage(0, 50);
        Require(page.RunId == "" && !page.Persisted && page.Total == 1 && page.Items[0].Code == "collection-coordination");
    });
    await Check("A journal storage failure is reported once and keeps its live diagnostic tail", async () =>
    {
        var failed = 0; var journal = new LibraryCollectionJournalSession(Store("missing"), context, () => Interlocked.Increment(ref failed));
        journal.Bind(Guid.NewGuid().ToString("N"));
        journal.Append(Event("one", "Premier événement.")); journal.Append(Event("two", "Deuxième événement."));
        await journal.CompleteAsync(default);
        Require(failed == 1 && !journal.Persisted && journal.MemoryPage(0, 50).Total == 2);
    });
    await Check("Capture retention also removes the associated events, without orphans", async () =>
    {
        var store = Store("retention"); var first = "";
        for (var i = 0; i < TicketLibraryStore.CaptureHistoryLimit + 2; i++)
        {
            var run = await store.BeginCaptureAsync(context, "incremental"); if (i == 0) first = run;
            await store.AppendCollectionEventAsync(run, context, Event("finished", "Passage conservé."));
            await store.FinishCaptureAsync(run, "completed", []);
        }
        await Reject(() => store.CollectionEventsAsync(first, context));
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()); await c.OpenAsync();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM ticket_library_collection_events e LEFT JOIN ticket_library_capture_runs r ON e.run_id=r.run_id WHERE r.run_id IS NULL";
        Require(Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 0);
    });
    await Check("The unsaved session tail stays bounded and only accepts diagnostic fields", async () =>
    {
        var store = Store("bounded"); var journal = new LibraryCollectionJournalSession(store, context, () => { });
        for (var i = 0; i < 1005; i++) journal.Append(Event("waiting", "Attente " + i));
        await journal.CompleteAsync(default); var page = journal.MemoryPage(0, 500);
        Require(page.Total == 1000 && page.Items.Count == 100 && page.Items[0].Message == "Attente 1004");
        var run = await store.BeginCaptureAsync(context, "incremental");
        await Reject(() => store.AppendCollectionEventAsync(run, context, Event("bad\ncode", "Message.")));
        await Reject(() => store.AppendCollectionEventAsync(run, context, Event("bad", "Message\r\nforgé.")));
        await store.FinishCaptureAsync(run, "completed", []);
    });
    Console.WriteLine($"{passed}/{total} vérifications du journal de collecte réussies.");
    return passed == total ? 0 : 1;
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

TicketLibraryStore Store(string name) => new(Path.Combine(root, name, "library.db"));
TicketLibraryCollectionEvent Event(string code, string message, string level = "info") => new() { Stage = "Lecture de l’index", Code = code, Message = message, Level = level };
static void Require(bool valid) { if (!valid) throw new InvalidOperationException("Journal assertion failed."); }
static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException) { return; } throw new Exception("Unsafe operation was accepted."); }
async Task Check(string name, Func<Task> action) { total++; try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); } }
