using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] == "--worker")
{
    var peer = new FolderTeamWorkspaceReplica(args[1], args[3]); await peer.InitializeAsync(); var value = Note("Session distincte"); value.Id = args[2];
    await peer.PushAsync(new(Guid.NewGuid().ToString("N"), 0, value)); Console.WriteLine("COMMITTED"); return;
}
if (args.Length > 0 && args[0] == "--lock-wait")
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    try { await new FolderTeamWorkspaceReplica(args[1], args[2]).InitializeAsync(cancel.Token); throw new Exception("Le verrou interprocessus n’a pas été respecté."); }
    catch (OperationCanceledException) { Console.WriteLine("LOCKED"); return; }
}
var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-SharedFolderChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var checks = 0;
try
{
    await Check("Two independent replicas share identity and literal team documents without SQLite in the share", async () =>
    {
        var folder = Share("identity"); var a = Team(folder); var b = Team(folder);
        var idA = await a.InitializeAsync(); var idB = await b.InitializeAsync(); Require(idA == idB, "Workspace identity must be stable.");
        var item = Note("Texte « équipe »\n<script>reste littéral</script>"); var receipt = await a.PushAsync(new(Guid.NewGuid().ToString("N"), 0, item)); var received = await b.ChangesAsync(0, 50);
        Require(received.Single().Item.Body == item.Body && receipt.Current.Id == item.Id, "Original strings and identity must roundtrip.");
        Require(!Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(path => path.EndsWith(".db", StringComparison.Ordinal) || path.EndsWith("-wal", StringComparison.Ordinal)), "The share must not contain an open SQLite database.");
    });
    await Check("Concurrent instances serialize commits into a monotonic complete journal", async () =>
    {
        var folder = Share("concurrency"); var a = Team(folder); var b = Team(folder);
        var writes = Enumerable.Range(0, 16).Select(index => (index % 2 == 0 ? a : b).PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("Écriture " + index)))); await Task.WhenAll(writes);
        var received = await Team(folder).ChangesAsync(0, 100);
        Require(received.Count == 16 && received.Select(value => value.Sequence).SequenceEqual(Enumerable.Range(1, 16).Select(value => (long)value)) && received.Select(value => value.Item.Id).Distinct().Count() == 16, "No concurrent transaction may be overwritten or skipped.");
    });
    await Check("An independent process commits through the same share lock and protocol", async () =>
    {
        var folder = Share("process"); var peer = Team(folder); await peer.InitializeAsync(); var id = Guid.NewGuid().ToString("N");
        var output = await Child("--worker", folder, id, Path.Combine(root, "LocalIndexes")); Require(output.Contains("COMMITTED"), "The independent process did not commit.");
        Require((await peer.ChangesAsync(0, 50)).Single().Item.Id == id, "The first process must observe the second process's transaction.");
    });
    await Check("Cross-process lock contention is cancellable without deleting or taking over the lock", async () =>
    {
        var folder = Share("lock"); await Team(folder).InitializeAsync(); var lockPath = Path.Combine(folder, "GLPI-Pilot-Shared", "workspace.lock");
        using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require((await Child("--lock-wait", folder, Path.Combine(root, "LocalIndexes"))).Contains("LOCKED"), "A second process must wait for the held lock and respect cancellation.");
        Require(File.Exists(lockPath), "A cancelled contender must leave the lock file alone.");
        await Team(folder).InitializeAsync();
    });
    await Check("Restart after a lost response returns the same receipt without another journal transaction", async () =>
    {
        var folder = Share("retry"); var peer = Team(folder); var mutation = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, Note("Unique"));
        var first = await peer.PushAsync(mutation); var replay = await Team(folder).PushAsync(mutation);
        Require(first.Current.CentralRevision == replay.Current.CentralRevision && (await peer.ChangesAsync(0, 100)).Count == 1, "Retry must be idempotent after a process restart.");
        var changed = mutation.Item.Copy(); changed.Body = "Autre requête"; await Reject<InvalidDataException>(() => Team(folder).PushAsync(mutation with { Item = changed }));
    });
    await Check("Calendar batches arrive whole even when the requested page contains one row", async () =>
    {
        var folder = Share("calendar"); var a = new TeamWorkspaceStore(Path.Combine(root, "calendar-a.db")); var b = new TeamWorkspaceStore(Path.Combine(root, "calendar-b.db"));
        var first = Calendar("Diagnostic"); var second = Calendar("Retour");
        await a.SaveCalendarBatchAsync(new[] { (first, (long?)null), (second, (long?)null) }); await TeamWorkspaceSyncService.SynchronizeAsync(a, Team(folder));
        var changes = await Team(folder).ChangesAsync(0, 1); Require(changes.Count == 2 && changes.All(value => value.BatchSize == 2), "An atomic calendar transaction cannot be paginated in half.");
        await TeamWorkspaceSyncService.SynchronizeAsync(b, Team(folder)); Require((await b.CountsAsync()).Items == 2, "Both appointments must enter the second local cache together.");
    });
    await Check("Shared-folder stale calendar comparisons reject the whole transaction and preserve every proposal", async () =>
    {
        var folder = Share("calendar-conflict"); var a = Team(folder); var b = Team(folder); var initial = Calendar("Original");
        var initialBatch = Batch((initial, 0)); var receipt = await a.PushBatchAsync(initialBatch); var current = receipt.Receipts.Single().Current;
        var winner = CalendarTitle(current, "Déjà partagé"); await b.PushBatchAsync(Batch((winner, current.CentralRevision)));
        var future = Calendar("Suite rejetée"); var stale = CalendarTitle(current, "Ancienne proposition");
        var rejected = await a.PushBatchAsync(Batch((stale, current.CentralRevision), (future, 0)));
        Require(rejected.Outcome == "conflict" && rejected.Receipts.Single(value => value.Current.Id == initial.Id).Current.Title == "Déjà partagé" && rejected.Receipts.Single(value => value.Current.Id == future.Id).Current.Deleted, "No partial future may survive the failed comparison.");
        var restart = await Team(folder).ChangesAsync(0, 100);
        Require(restart.TakeLast(2).All(value => value.Incoming is not null), "All incoming versions must remain in the durable group feed.");
    });
    await Check("An unfinished temporary file is ignored while committed corruption stops every read and write", async () =>
    {
        var folder = Share("corruption"); var peer = Team(folder); await peer.PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("Validée")));
        var domain = Path.Combine(folder, "GLPI-Pilot-Shared", "team"); await File.WriteAllTextAsync(Path.Combine(domain, "unfinished.tmp"), "{");
        Require((await Team(folder).ChangesAsync(0, 20)).Count == 1, "An abandoned staging file is not a transaction.");
        var committed = Path.Combine(domain, "00000000000000000001.json"); await File.WriteAllTextAsync(committed, "{");
        await Reject<InvalidDataException>(() => Team(folder).ChangesAsync(0, 20));
        await Reject<InvalidDataException>(() => peer.PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("Refusée"))));
        Require(!File.Exists(Path.Combine(domain, "00000000000000000002.json")), "Corruption must not allow appending beyond an unreadable transaction.");
    });
    await Check("A lost committed tail is detected after restart by the durable checkpoint", async () =>
    {
        var folder = Share("missing-tail"); var peer = Team(folder); await peer.PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("A"))); await peer.PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("B")));
        File.Delete(Path.Combine(folder, "GLPI-Pilot-Shared", "team", "00000000000000000002.json"));
        await Reject<InvalidDataException>(() => Team(folder).InitializeAsync());
    });
    await Check("A committed journal transaction with a lagging checkpoint is recovered without reapplying it", async () =>
    {
        var folder = Share("checkpoint"); var peer = Team(folder); await peer.InitializeAsync(); var path = Path.Combine(folder, "GLPI-Pilot-Shared", "team", "head.json"); var old = await File.ReadAllTextAsync(path);
        var request = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, Note("Validée avant le point de contrôle")); var receipt = await peer.PushAsync(request);
        await File.WriteAllTextAsync(path, old); var resumed = Team(folder); Require((await resumed.PushAsync(request)).Current.CentralRevision == receipt.Current.CentralRevision && (await resumed.ChangesAsync(0, 50)).Count == 1, "Recovery must keep the existing transaction and receipt.");
    });
    await Check("Unavailable share leaves the complete local calendar outbox resumable", async () =>
    {
        var folder = Share("offline"); var store = new TeamWorkspaceStore(Path.Combine(root, "offline.db")); await store.SaveAsync(Calendar("À synchroniser"), null);
        Directory.Move(folder, folder + "-offline");
        await Reject<IOException>(() => TeamWorkspaceSyncService.SynchronizeAsync(store, Team(folder)));
        Require((await store.CountsAsync()).Pending == 1 && !Directory.Exists(folder), "An unavailable share must not be silently recreated as another workspace.");
        Directory.Move(folder + "-offline", folder); await TeamWorkspaceSyncService.SynchronizeAsync(store, Team(folder)); Require((await store.CountsAsync()).Pending == 0, "Reconnect must drain the original outbox.");
    });
    await Check("Cancellation before commit leaves no transaction and simulation never opens a real share", async () =>
    {
        var folder = Share("cancel"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Reject<OperationCanceledException>(() => Team(folder).PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("Annulée")), cancel.Token));
        Require(!Directory.Exists(Path.Combine(folder, "GLPI-Pilot-Shared")), "Cancelled write must not create the workspace.");
        using (AppPaths.EnterSimulation(Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Simulation-" + Guid.NewGuid().ToString("N")))) await Reject<InvalidOperationException>(() => Team(folder).InitializeAsync());
        Require(!Directory.Exists(Path.Combine(folder, "GLPI-Pilot-Shared")), "Simulation must not probe the real shared folder.");
    });
    await Check("Ticket corpus synchronizes full source fields across separate local caches", async () =>
    {
        var folder = Share("tickets"); var a = new TicketLibraryStore(Path.Combine(root, "tickets-a.db")); var b = new TicketLibraryStore(Path.Combine(root, "tickets-b.db")); var seed = Seed("42", "Demande d’intervention");
        seed.IndexHeaders = ["ID", "Titre", "Description", "Attribué à - Groupe de techniciens"]; seed.IndexValues = ["42", seed.Title, "Description intégrale « é »\n<b>conservée</b>", "Équipe A"];
        await a.ObserveSeedAsync(seed); await TicketLibrarySyncService.SynchronizeAsync(a, (ITicketLibraryReplica)Tickets(folder));
        await TicketLibrarySyncService.SynchronizeAsync(b, (ITicketLibraryReplica)Tickets(folder)); var received = (await b.GetAsync(seed.Ticket))!;
        Require(received.Seed.IndexValues.SequenceEqual(seed.IndexValues), "The shared corpus must retain source columns and multiline content.");
        Require(!Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(path => path.EndsWith(".db", StringComparison.Ordinal)), "Ticket caches must remain local.");
    });
    await Check("Ticket receipts and source-version conflicts survive a fresh transport instance", async () =>
    {
        var folder = Share("ticket-versions"); ITicketLibraryReplica peer = Tickets(folder);
        var initial = TicketLibraryRules.FromSeed(Seed("43", "Version source")); var request = new TicketLibraryMutation(Guid.NewGuid().ToString("N"), initial);
        var first = await peer.PushAsync(request, default); var replay = await ((ITicketLibraryReplica)Tickets(folder)).PushAsync(request, default);
        Require(first.Revision == replay.Revision && (await peer.PullAsync(0, 100, default)).Count == 1, "Ticket replay must be idempotent.");
        var uncertain = TicketLibraryRules.FromSeed(Seed("43", "Même version, autre contenu")); var conflict = await peer.PushAsync(new(Guid.NewGuid().ToString("N"), uncertain), default);
        Require(conflict.Conflict && conflict.Document.Seed.Title == initial.Seed.Title, "Equal source versions must preserve conflicting evidence without choosing a workstation clock.");
        var restored = await ((ITicketLibraryReplica)Tickets(folder)).PullAsync(0, 100, default);
        Require(restored.Count == 2 && restored[^1].Incoming?.Seed.Title == uncertain.Seed.Title, "Conflict evidence must remain after a process restart.");
    });
    await Check("Structural path validation does not create or access a configured missing share", () =>
    {
        var absent = Path.Combine(root, "not-mounted"); Require(SharedFolderWorkspace.ValidatePath(absent) == absent && !Directory.Exists(absent), "Saving a path should not touch the server.");
        Require(SharedFolderWorkspace.ValidatePath(Path.GetPathRoot(root)!).Length > 0, "Filesystem roots must not turn into relative paths."); return Task.CompletedTask;
    });
    await Check("Both disposable indexes live only in the explicit local cache and reject a shared cache path", async () =>
    {
        var folder = Share("index-location"); var team = Team(folder); var tickets = Tickets(folder);
        await team.InitializeAsync(); await tickets.InitializeAsync();
        Require(File.Exists(team.LocalIndexPath) && File.Exists(tickets.LocalIndexPath)
            && team.LocalIndexPath.StartsWith(Path.Combine(root, "LocalIndexes"), StringComparison.Ordinal), "Indexes must live in the test's explicit LOCAL root.");
        await Reject<InvalidDataException>(() => Task.FromResult(new FolderTeamWorkspaceReplica(folder, folder)));
        await Reject<InvalidDataException>(() => Task.FromResult(new FolderTicketLibraryReplica(folder, Path.Combine(folder, "cache"))));
        await Reject<InvalidDataException>(() => Task.FromResult(new FolderTeamWorkspaceReplica(folder, "//server/share/cache")));
        Require(!Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(path => path.EndsWith(".db", StringComparison.Ordinal)), "No SQLite index may enter the exchange directory.");
        await Reject<InvalidDataException>(() => Task.FromResult(SharedFolderWorkspace.SerializeBounded(new { body = new string('x', 500) }, 128)));
    });
    await Check("Many document and ticket versions retain history on disk while managed memory stays bounded", async () =>
    {
        var folder = Share("index-history"); var team = Team(folder); var tickets = Tickets(folder);
        await team.InitializeAsync(); await tickets.InitializeAsync();
        var note = Note("Initial"); TeamWorkspaceMutation? firstTeam = null; TicketLibraryMutation? firstTicket = null;
        long revision = 0; const int versions = 80, characters = 140000;
        var before = GC.GetTotalMemory(forceFullCollection: true);
        for (var i = 0; i < versions; i++)
        {
            note.Body = i.ToString(CultureInfo.InvariantCulture) + ":" + new string((char)('a' + i % 26), characters);
            var request = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), revision, note.Copy()); firstTeam ??= request;
            revision = (await team.PushAsync(request)).Current.CentralRevision;
            var seed = Seed("90", "Version " + i); seed.LastModifiedAt = seed.LastModifiedAt!.Value.AddMinutes(i);
            seed.LastModifiedRaw = seed.LastModifiedAt.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            seed.IndexHeaders = ["ID", "Description"]; seed.IndexValues = ["90", i.ToString(CultureInfo.InvariantCulture) + ":" + new string((char)('A' + i % 26), characters)];
            var ticketRequest = new TicketLibraryMutation(Guid.NewGuid().ToString("N"), TicketLibraryRules.FromSeed(seed)); firstTicket ??= ticketRequest;
            await tickets.PushAsync(ticketRequest, default);
        }
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        Require(retained < 24L * 1024 * 1024, "Historical payloads remained cached in managed memory: " + retained.ToString(CultureInfo.InvariantCulture) + " bytes.");
        Require(await IndexScalar(team.LocalIndexPath, "SELECT COUNT(*) FROM gp_folder_receipts") == versions
            && await IndexScalar(tickets.LocalIndexPath, "SELECT COUNT(*) FROM gp_folder_changes") == versions
            && await IndexScalar(team.LocalIndexPath, "SELECT SUM(length(json)) FROM gp_folder_changes") > versions * characters,
            "The disk index did not retain the complete historical receipts and contents.");
        Require((await team.ChangesAsync(0, 7)).Select(value => value.Sequence).SequenceEqual(Enumerable.Range(1, 7).Select(value => (long)value))
            && (await tickets.PullAsync(0, 7, default)).Count == 7, "Historical reads must be paginated.");
        Require((await Team(folder).PushAsync(firstTeam!)).Current.CentralRevision == 1
            && (await Tickets(folder).PushAsync(firstTicket!, default)).Revision == 1, "Old receipts must stay exact after many newer versions and a new transport instance.");
        Require((await team.ChangesAsync(versions - 1, 1)).Single().Item.Body.StartsWith("79:", StringComparison.Ordinal), "The final version is missing.");
        GC.KeepAlive(team); GC.KeepAlive(tickets);
    });
    await Check("Deleted or unreadable local indexes rebuild from the journal without duplicate transactions", async () =>
    {
        var folder = Share("index-rebuild"); var team = Team(folder); var tickets = Tickets(folder);
        var request = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, Note("Historique à reconstruire")); var original = await team.PushAsync(request);
        var ticketRequest = new TicketLibraryMutation(Guid.NewGuid().ToString("N"), TicketLibraryRules.FromSeed(Seed("91", "À reconstruire")));
        var ticketReceipt = await tickets.PushAsync(ticketRequest, default);
        File.Delete(team.LocalIndexPath); await File.WriteAllTextAsync(tickets.LocalIndexPath, "local cache damaged");
        Require((await Team(folder).PushAsync(request)).Current.CentralRevision == original.Current.CentralRevision
            && (await Tickets(folder).PushAsync(ticketRequest, default)).Revision == ticketReceipt.Revision, "Rebuilding must preserve original request receipts.");
        Require((await Team(folder).ChangesAsync(0, 100)).Count == 1 && (await Tickets(folder).PullAsync(0, 100, default)).Count == 1, "A rebuild duplicated central history.");
    });
    await Check("Missing index rows and altered cached payloads are repaired from authoritative transactions", async () =>
    {
        var folder = Share("index-checksum"); var peer = Team(folder); var request = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, Note("Valeur commune exacte"));
        await peer.PushAsync(request);
        await IndexExecute(peer.LocalIndexPath, "UPDATE gp_folder_changes SET json=replace(json,'Valeur commune exacte','Valeur locale altérée')");
        Require((await peer.ChangesAsync(0, 10)).Single().Item.Body == "Valeur commune exacte", "An altered cached page bypassed checksum verification.");
        await IndexExecute(peer.LocalIndexPath, "DELETE FROM gp_folder_receipts");
        Require((await Team(folder).PushAsync(request)).Current.CentralRevision == 1 && (await peer.ChangesAsync(0, 10)).Count == 1,
            "Missing cached receipts must rebuild, not reapply a committed mutation.");
    });
    await Check("A failure after journal commit but before local index commit is replayed atomically", async () =>
    {
        var folder = Share("index-crash"); var peer = Team(folder); await peer.InitializeAsync();
        await IndexExecute(peer.LocalIndexPath, "CREATE TRIGGER synthetic_index_failure BEFORE INSERT ON gp_folder_journal BEGIN SELECT RAISE(ABORT,'synthetic local commit failure'); END");
        var request = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, Note("Journal validé, index interrompu"));
        await Reject<SqliteException>(() => peer.PushAsync(request));
        Require(File.Exists(Path.Combine(folder, "GLPI-Pilot-Shared", "team", "00000000000000000001.json"))
            && await IndexScalar(peer.LocalIndexPath, "SELECT COUNT(*) FROM gp_folder_changes") == 0
            && await IndexScalar(peer.LocalIndexPath, "SELECT COUNT(*) FROM gp_folder_receipts") == 0, "The injected failure must leave only the authoritative transaction committed.");
        await IndexExecute(peer.LocalIndexPath, "DROP TRIGGER synthetic_index_failure");
        Require((await Team(folder).PushAsync(request)).Current.CentralRevision == 1 && (await peer.ChangesAsync(0, 10)).Count == 1,
            "Recovery must apply the already committed request exactly once.");
    });
    await Check("A replacement workspace identity cannot reuse the former local index state", async () =>
    {
        var folder = Share("index-new-identity"); var peer = Team(folder); var firstIdentity = await peer.InitializeAsync();
        await peer.PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("Ancien espace")));
        Directory.Move(folder, folder + "-previous"); Directory.CreateDirectory(folder);
        var secondIdentity = await peer.InitializeAsync();
        Require(secondIdentity != firstIdentity && (await peer.ChangesAsync(0, 10)).Count == 0, "The former workspace leaked through its reusable local cache path.");
        var next = await peer.PushAsync(new(Guid.NewGuid().ToString("N"), 0, Note("Nouvel espace")));
        Require(next.Current.CentralRevision == 1 && (await Team(folder).ChangesAsync(0, 10)).Single().Item.Body == "Nouvel espace", "The new workspace must begin its own history.");
    });
    await Check("Legacy JSON journals replay with identical memory-protocol receipts including atomic calendar conflicts", async () =>
    {
        var folder = Share("legacy-protocol"); await Team(folder).InitializeAsync(); await Tickets(folder).InitializeAsync();
        var legacy = new InMemoryTeamWorkspaceReplica(); var noteRequest = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, Note("Avant l’index local"));
        var noteReceipt = await legacy.PushAsync(noteRequest); var hash = await LegacyEnvelope(folder, "team", 1, "", "single", noteRequest, noteReceipt);
        var calendar = Calendar("Série d’origine"); var calendarRequest = Batch((calendar, 0)); var calendarReceipt = await legacy.PushBatchAsync(calendarRequest);
        hash = await LegacyEnvelope(folder, "team", 2, hash, "batch", calendarRequest, calendarReceipt);
        var rejected = Batch((CalendarTitle(calendarReceipt.Receipts.Single().Current, "Ancien choix"), 0), (Calendar("Suite refusée"), 0));
        var rejectedReceipt = await legacy.PushBatchAsync(rejected); await LegacyEnvelope(folder, "team", 3, hash, "batch", rejected, rejectedReceipt);
        var source = TicketLibraryRules.FromSeed(Seed("92", "Ancienne fiche")); var ticketRequest = new TicketLibraryMutation(Guid.NewGuid().ToString("N"), source);
        var accepted = TicketLibraryRules.Copy(source); accepted.LocalRevision = 0; accepted.CentralRevision = 1; accepted.Pending = false;
        var ticketReceipt = new TicketLibraryPushReceipt(1, "inserted", false, accepted);
        await LegacyEnvelope(folder, "tickets", 1, "", "ticket", ticketRequest, ticketReceipt);
        var peer = Team(folder); await peer.InitializeAsync();
        Require(LegacyDigest(await peer.PushBatchAsync(rejected)) == LegacyDigest(rejectedReceipt)
            && LegacyDigest(await peer.PushAsync(noteRequest)) == LegacyDigest(noteReceipt), "Old team and calendar receipts changed during indexed replay.");
        Require(LegacyDigest(await Tickets(folder).PushAsync(ticketRequest, default)) == LegacyDigest(ticketReceipt), "Old ticket receipts changed during indexed replay.");
        Require((await peer.ChangesAsync(0, 100)).Count == 4, "Legacy feed order or rejected future evidence changed.");
    });
    Console.WriteLine($"{checks}/{checks} shared folder checks passed.");
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

FolderTeamWorkspaceReplica Team(string folder) => new(folder, Path.Combine(root, "LocalIndexes"));
FolderTicketLibraryReplica Tickets(string folder) => new(folder, Path.Combine(root, "LocalIndexes"));
string Share(string name) { var folder = Path.Combine(root, name); Directory.CreateDirectory(folder); return folder; }
async Task Check(string label, Func<Task> body) { await body(); checks++; Console.WriteLine("PASS " + label); }
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Reject<T>(Func<Task> body) where T : Exception { try { await body(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static TeamWorkspaceItem Note(string body) => new() { Id = Guid.NewGuid().ToString("N"), Kind = "note", Title = "Note d’équipe", Body = body, Author = "Test" };
static TeamWorkspaceItem Calendar(string title) => CalendarWorkspaceDocuments.ForIntervention(new() { Title = title, StartLocal = "2026-10-26T09:00", EndLocal = "2026-10-26T10:00", TimeZoneId = "Europe/Paris", ParticipantKeys = ["tech"] }, "Test");
static TeamWorkspaceItem CalendarTitle(TeamWorkspaceItem item, string title) { var value = CalendarWorkspaceDocuments.ReadIntervention(item); value.Title = title; return CalendarWorkspaceDocuments.ForIntervention(value, "Test"); }
static TeamWorkspaceBatchMutation Batch(params (TeamWorkspaceItem Item, long Revision)[] items) => new(Guid.NewGuid().ToString("N"), items.Select(value => new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), value.Revision, value.Item)).ToArray());
static TicketLibrarySeed Seed(string id, string title) => new() { Ticket = new(id, "https://fixture.example/glpi/front/ticket.form.php?id=" + id), Title = title, Status = "En cours", StatusCode = 2, LastModifiedRaw = "2026-10-26 08:30:00", LastModifiedAt = DateTimeOffset.Parse("2026-10-26T08:30:00Z", CultureInfo.InvariantCulture), LastModifiedReliable = true, LastModifiedPrecision = "seconds" };
static async Task<long> IndexScalar(string path, string sql)
{
    await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString); await db.OpenAsync();
    await using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}
static async Task IndexExecute(string path, string sql)
{
    await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString); await db.OpenAsync();
    await using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
}
static string LegacyDigest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, SharedFolderWorkspace.Json))).ToLowerInvariant();
static async Task<string> LegacyEnvelope<T, TReceipt>(string folder, string domain, long sequence, string previous, string kind, T request, TReceipt receipt)
{
    var envelope = new SharedFolderJournal.Envelope { Domain = domain, Sequence = sequence, PreviousHash = previous, Kind = kind,
        Request = JsonSerializer.SerializeToElement(request, SharedFolderWorkspace.Json), ReceiptHash = LegacyDigest(receipt) };
    envelope.Hash = LegacyDigest(new { envelope.Format, envelope.Domain, envelope.Sequence, envelope.PreviousHash, envelope.Kind, envelope.Request, envelope.ReceiptHash });
    await File.WriteAllBytesAsync(Path.Combine(folder, "GLPI-Pilot-Shared", domain, sequence.ToString("D20", CultureInfo.InvariantCulture) + ".json"), JsonSerializer.SerializeToUtf8Bytes(envelope, SharedFolderWorkspace.Json));
    return envelope.Hash;
}
static async Task<string> Child(params string[] values)
{
    var host = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable."); var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var value in values) start.ArgumentList.Add(value);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start independent test process."); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
    var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync(); Require(process.ExitCode == 0, "Child process failed: " + error); return output;
}
