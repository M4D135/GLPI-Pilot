using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;
using Npgsql;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-SharedWorkspaceChecks-" + Guid.NewGuid().ToString("N"));
    private static int _passed;
    private static int _total;
    private static int _stores;
    private const string LiteralBody = "Première ligne : <script>synthetic()</script>\nRéponse « équipe » ; null ; \u0092 inchangé.";

    private static async Task<int> Main(string[] args)
    {
        var usePostgres = args.Contains("--postgres", StringComparer.Ordinal);
        var connectionString = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_POSTGRES");
        if (usePostgres && string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("FAIL --postgres requires GLPI_PILOT_TEST_POSTGRES; real PostgreSQL checks were not run.");
            return 1;
        }
        Directory.CreateDirectory(Root);
        try
        {
            await CheckLocalAsync();
            await CheckReplicationAsync(new ReplicaFactory(null), "memory protocol");
            if (usePostgres) await CheckReplicationAsync(new ReplicaFactory(connectionString), "PostgreSQL");
            Console.WriteLine($"{_passed}/{_total} shared workspace checks passed.");
            return _passed == _total ? 0 : 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task CheckLocalAsync()
    {
        await Check("Real SQLite preserves notes, comments, knowledge and assignments as literal UTF-8", async () =>
        {
            var store = await StoreAsync("documents");
            var saved = new List<TeamWorkspaceItem>();
            foreach (var kind in new[] { "note", "comment", "knowledge", "assignment" })
            {
                var item = Item(kind, LiteralBody);
                item.Assignee = kind == "assignment" ? "Technicien synthétique" : "";
                item.Status = kind == "knowledge" ? "published" : "open";
                saved.Add(await store.SaveAsync(item, null));
            }
            var reopened = new TeamWorkspaceStore(store.DatabasePath);
            await reopened.InitializeAsync();
            foreach (var original in saved)
            {
                var read = await RequiredAsync(reopened, original.Id);
                Require(read.Body == LiteralBody && read.Kind == original.Kind && read.Status == original.Status
                    && read.Assignee == original.Assignee && read.TicketKey == original.TicketKey && read.Pending,
                    "Reopening must preserve the complete typed contribution and its pending state.");
            }
            var assignments = await reopened.ListAsync(kind: "assignment", ticketKey: saved[^1].TicketKey);
            Require(assignments.Total == 1 && assignments.Items.Single().Assignee == "Technicien synthétique",
                "Assignment filtering must retain the assignee and ticket link.");
        });

        await Check("An obsolete editor or delete cannot replace the latest committed contribution", async () =>
        {
            var store = await StoreAsync("stale");
            var original = await store.SaveAsync(Item(), null);
            var fresh = original.Copy(); fresh.Body = "Modification récente conservée.";
            var current = await store.SaveAsync(fresh, original.Revision);
            var stale = original.Copy(); stale.Body = "Modification ancienne à refuser.";
            await RejectAsync<InvalidOperationException>(() => store.SaveAsync(stale, original.Revision));
            await RejectAsync<InvalidOperationException>(() => store.RemoveAsync(original.Id, original.Revision));
            var read = await RequiredAsync(store, original.Id);
            Require(read.Revision == current.Revision && read.Body == fresh.Body && !read.Deleted
                && (await store.CountsAsync()).Pending == 1,
                "Rejecting a stale operation must leave the current document and outbox intact.");
        });

        await Check("List previews are bounded while full reads and export retain a long article and payload", async () =>
        {
            var store = await StoreAsync("preview");
            var article = Item("knowledge", string.Concat(Enumerable.Repeat("Texte synthétique <b>littéral</b>.\n", 300)));
            article.Status = "published";
            await store.SaveAsync(article, null);
            var configuration = Item("configuration", "Disposition de démonstration.");
            configuration.Payload = JsonSerializer.SerializeToElement(new { sections = new[] { "Synthèse" }, version = 1 });
            await store.SaveAsync(configuration, null);
            var listed = await store.ListAsync();
            var preview = listed.Items.Single(x => x.Id == article.Id);
            Require(preview.Body == article.Body[..1200] + "…" && listed.Items.All(x => x.Payload is null),
                "The listing must expose an explicit preview and omit configuration payloads.");
            Require((await RequiredAsync(store, article.Id)).Body == article.Body
                && (await RequiredAsync(store, configuration.Id)).Payload!.Value.GetProperty("sections")[0].GetString() == "Synthèse",
                "Opening an existing contribution must recover the full document before editing.");
            var exported = await ExportAsync(store);
            Require(exported.Single(x => x.Id == article.Id).Body == article.Body
                && exported.Single(x => x.Id == configuration.Id).Payload is not null,
                "Export must contain complete documents rather than list previews.");
        });

        await Check("Deletion creates a durable tombstone and excludes it from active lists and export", async () =>
        {
            var store = await StoreAsync("tombstone");
            var removed = await store.SaveAsync(Item("comment"), null);
            var retained = await store.SaveAsync(Item("note", "À conserver."), null);
            var tombstone = await store.RemoveAsync(removed.Id, removed.Revision);
            var reopened = new TeamWorkspaceStore(store.DatabasePath);
            var read = await RequiredAsync(reopened, removed.Id);
            Require(read.Deleted && read.Pending && read.Revision == tombstone.Revision && read.Revision > removed.Revision,
                "Deletion must persist a revisioned pending tombstone.");
            Require((await reopened.ListAsync()).Items.Single().Id == retained.Id
                && (await reopened.CountsAsync()).Items == 1
                && (await ExportAsync(reopened)).Single().Id == retained.Id,
                "Deleted contributions must be absent from active results and exports.");
        });
    }

    private static async Task CheckReplicationAsync(ReplicaFactory factory, string label)
    {
        await Check(label + ": two SQLite workstations exchange contributions and settle without repeat writes", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("roundtrip-a"); var b = await StoreAsync("roundtrip-b");
            var note = await a.SaveAsync(Item("note", LiteralBody), null);
            var comment = await a.SaveAsync(Item("comment", "Commentaire initial."), null);
            var pushed = await Sync(a, scope.Replica); var pulled = await Sync(b, scope.Replica);
            Require(pushed.Pushed == 2 && pushed.Pending == 0 && pulled.Pulled == 2
                && (await RequiredAsync(b, note.Id)).Body == LiteralBody,
                "A second workstation must receive both complete original documents.");
            var edit = await RequiredAsync(b, comment.Id); edit.Body = "Commentaire complété sur le deuxième poste.";
            await b.SaveAsync(edit, edit.Revision);
            var assignment = Item("assignment", "Vérifier la solution.");
            assignment.Assignee = "Équipe synthétique"; assignment.Status = "done";
            await b.SaveAsync(assignment, null);
            await Sync(b, scope.Replica); await Sync(a, scope.Replica);
            Require((await RequiredAsync(a, comment.Id)).Body == edit.Body
                && (await RequiredAsync(a, assignment.Id)).Assignee == assignment.Assignee
                && (await RequiredAsync(a, assignment.Id)).Status == "done",
                "Edits and assignments from the second workstation must return to the first.");
            var before = (await AllChangesAsync(scope.Replica)).Count;
            var stableA = await Sync(a, scope.Replica); var stableB = await Sync(b, scope.Replica);
            Require(stableA.Pushed == 0 && stableB.Pushed == 0 && stableA.Pulled == 0 && stableB.Pulled == 0
                && stableA.Pending == 0 && stableB.Pending == 0 && (await AllChangesAsync(scope.Replica)).Count == before,
                "A settled synchronization must neither duplicate writes nor replay cursor entries.");
            await scope.VerifyProjectionAsync(3);
        });

        await Check(label + ": a lost response retries the same mutation without duplicating central history", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("lost-a"); var b = await StoreAsync("lost-b");
            var item = await a.SaveAsync(Item("knowledge", LiteralBody), null);
            var connection = new AfterFirstPushReplica(scope.Replica, () => throw new IOException("Synthetic response lost after commit."));
            await RejectAsync<IOException>(() => Sync(a, connection));
            var queued = (await a.PendingAsync(50, CancellationToken.None)).Single();
            Require(queued.MutationId == connection.FirstMutationId && (await AllChangesAsync(scope.Replica)).Count == 1,
                "A lost receipt must leave the exact committed mutation pending locally.");
            var resumed = await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            Require(resumed.Pending == 0 && (await AllChangesAsync(scope.Replica)).Count == 1
                && (await RequiredAsync(b, item.Id)).Body == LiteralBody,
                "Retrying the pending mutation must converge without a second central change.");
        });

        await Check(label + ": mutation replay is stable and a reused identifier with changed content is refused", async () =>
        {
            await using var scope = await factory.CreateAsync();
            await scope.Replica.InitializeAsync();
            var mutation = new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, Item("note", "Version authentifiée."));
            var first = await scope.Replica.PushAsync(mutation); var repeated = await scope.Replica.PushAsync(mutation);
            Require(first.Outcome == "accepted" && repeated.Outcome == first.Outcome
                && repeated.Current.CentralRevision == first.Current.CentralRevision,
                "An identical mutation must return the original receipt.");
            var changed = mutation.Item.Copy(); changed.Body = "Contenu différent avec le même identifiant.";
            await RejectAsync<InvalidDataException>(() => scope.Replica.PushAsync(mutation with { Item = changed }));
            Require((await AllChangesAsync(scope.Replica)).Single().Item.Body == mutation.Item.Body,
                "Rejecting mutation reuse must leave the central document unchanged.");
        });

        await Check(label + ": simultaneous edits preserve both versions on all three workstations without duplicate conflicts", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("concurrent-a"); var b = await StoreAsync("concurrent-b");
            var item = await a.SaveAsync(Item("note", "Base commune."), null);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var left = await RequiredAsync(a, item.Id); left.Body = "Version du premier poste.";
            var right = await RequiredAsync(b, item.Id); right.Body = "Version du deuxième poste.";
            await a.SaveAsync(left, left.Revision); await b.SaveAsync(right, right.Revision);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var simultaneous = new TwoPushBarrierReplica(scope.Replica);
            var outcomes = await Task.WhenAll(
                TeamWorkspaceSyncService.SynchronizeAsync(a, simultaneous, ct: deadline.Token),
                TeamWorkspaceSyncService.SynchronizeAsync(b, simultaneous, ct: deadline.Token));
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var c = await StoreAsync("concurrent-new-observer");
            await Sync(c, scope.Replica);
            Require(outcomes.Any(x => x.Conflicts > 0), "The competing central revision must be reported as a conflict.");
            string? conflictId = null;
            foreach (var local in new[] { a, b, c })
            {
                var conflict = (await local.ConflictsAsync(item.Id)).Single();
                var preserved = new[] { conflict.Current.Body, conflict.Incoming.Body };
                Require(preserved.Order(StringComparer.Ordinal).SequenceEqual(new[] { left.Body, right.Body }.Order(StringComparer.Ordinal)),
                    "Even a new observer must receive both complete competing bodies.");
                conflictId ??= conflict.Id;
                Require(conflict.Id == conflictId, "All caches must identify the same preserved pair consistently.");
                await Sync(local, scope.Replica);
                Require((await local.CountsAsync()).Conflicts == 1 && (await local.ConflictsAsync(item.Id)).Count == 1,
                    "Acknowledgement followed by feed reception and retry must not duplicate the same conflict.");
            }
            var winnerA = await RequiredAsync(a, item.Id); var winnerB = await RequiredAsync(b, item.Id);
            Require(winnerA.Body == winnerB.Body && winnerA.CentralRevision == winnerB.CentralRevision
                && (await RequiredAsync(c, item.Id)).Body == winnerA.Body
                && !winnerA.Pending && !winnerB.Pending && (await AllChangesAsync(scope.Replica)).Count == 3,
                "All caches must converge while the feed retains the shared conflict event.");
            await scope.VerifyConflictsAsync(left.Body, right.Body);
        });

        await Check(label + ": a deletion reaches existing and new workstations without resurrection", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("delete-a"); var b = await StoreAsync("delete-b"); var c = await StoreAsync("delete-c");
            var item = await a.SaveAsync(Item("comment"), null);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var current = await RequiredAsync(a, item.Id);
            await a.RemoveAsync(item.Id, current.Revision);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica); await Sync(c, scope.Replica);
            foreach (var local in new[] { a, b, c })
                Require((await RequiredAsync(local, item.Id)) is { Deleted: true, Pending: false }
                    && (await local.ListAsync()).Total == 0 && (await ExportAsync(local)).Length == 0,
                    "Every cache must retain the tombstone while omitting it from active content.");
            await Sync(b, scope.Replica);
            Require((await AllChangesAsync(scope.Replica)).Count == 2, "Repeating a delete synchronization must not resurrect or rewrite the document.");
        });

        await Check(label + ": an edit made between push and pull stays queued and its conflict reaches a new workstation", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("pull-race-a"); var b = await StoreAsync("pull-race-b"); var c = await StoreAsync("pull-race-c");
            var item = await a.SaveAsync(Item("note", "Version commune."), null);
            await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            var central = await RequiredAsync(a, item.Id); central.Body = "Modification déjà centrale.";
            await a.SaveAsync(central, central.Revision); await Sync(a, scope.Replica);
            const string lateBody = "Modification locale pendant la synchronisation.";
            var connection = new BeforeFirstChangesReplica(scope.Replica, async () =>
            {
                var draft = await RequiredAsync(b, item.Id); draft.Body = lateBody;
                await b.SaveAsync(draft, draft.Revision);
            });
            await Sync(b, connection);
            var queued = (await b.PendingAsync(50, CancellationToken.None)).Single();
            Require(queued.Item.Body == lateBody && (await b.CountsAsync()).Pending == 1,
                "Receiving a central change must not acknowledge a newer local mutation that was never sent.");
            await Sync(b, scope.Replica); await Sync(c, scope.Replica);
            var conflict = (await c.ConflictsAsync(item.Id)).Single();
            Require((await b.CountsAsync()).Pending == 0 && conflict.Current.Body == central.Body && conflict.Incoming.Body == lateBody
                && (await AllChangesAsync(scope.Replica)).Count == 3,
                "The retained mutation must reach the central conflict archive and a new observer on the next synchronization.");
            await scope.VerifyConflictsAsync(central.Body, lateBody);
        });

        await Check(label + ": cancellation after a central commit resumes all remaining documents", async () =>
        {
            await using var scope = await factory.CreateAsync();
            var a = await StoreAsync("cancel-a"); var b = await StoreAsync("cancel-b");
            var items = new[]
            {
                await a.SaveAsync(Item("note", "Premier document."), null),
                await a.SaveAsync(Item("assignment", "Second document."), null)
            };
            using var cancelled = new CancellationTokenSource();
            var connection = new AfterFirstPushReplica(scope.Replica, cancelled.Cancel);
            await RejectAsync<OperationCanceledException>(() => TeamWorkspaceSyncService.SynchronizeAsync(a, connection, ct: cancelled.Token));
            Require((await a.CountsAsync()).Pending == 2 && (await AllChangesAsync(scope.Replica)).Count == 1,
                "Cancellation before local acknowledgement must retain both pending documents.");
            var resumed = await Sync(a, scope.Replica); await Sync(b, scope.Replica);
            Require(resumed.Pending == 0 && (await AllChangesAsync(scope.Replica)).Count == 2,
                "Resuming must acknowledge the first commit and send the remaining document exactly once.");
            foreach (var item in items)
                Require((await RequiredAsync(b, item.Id)).Body == item.Body, "No document may be lost across cancellation.");
        });

        await Check(label + ": changing central identity resets the cursor and resends active documents and tombstones", async () =>
        {
            await using var oldCentral = await factory.CreateAsync(); await using var newCentral = await factory.CreateAsync();
            var a = await StoreAsync("switch-a"); var b = await StoreAsync("switch-b");
            var active = await a.SaveAsync(Item("knowledge", "Première version."), null);
            var removed = await a.SaveAsync(Item("comment", "Contribution supprimée."), null);
            await Sync(a, oldCentral.Replica);
            for (var i = 0; i < 3; i++)
            {
                var current = await RequiredAsync(a, active.Id); current.Body = "Version courante " + i + ".";
                await a.SaveAsync(current, current.Revision); await Sync(a, oldCentral.Replica);
            }
            var beforeDelete = await RequiredAsync(a, removed.Id); await a.RemoveAsync(removed.Id, beforeDelete.Revision);
            await Sync(a, oldCentral.Replica);
            var oldChanges = (await AllChangesAsync(oldCentral.Replica)).Count;
            Require(oldChanges > 2 && (await a.CountsAsync()).Pending == 0, "The old cursor must be beyond the new central feed.");
            var currentBody = (await RequiredAsync(a, active.Id)).Body;
            var switched = await Sync(a, newCentral.Replica); await Sync(b, newCentral.Replica);
            Require(switched.Pushed == 2 && switched.Pulled == 2 && switched.Pending == 0
                && (await AllChangesAsync(newCentral.Replica)).Count == 2
                && (await AllChangesAsync(oldCentral.Replica)).Count == oldChanges,
                "A new central identity must receive both snapshots without modifying the former central.");
            Require((await RequiredAsync(b, active.Id)).Body == currentBody
                && (await RequiredAsync(b, removed.Id)).Deleted && (await b.ListAsync()).Total == 1,
                "The replacement central must retain the active document and the deletion.");
        });
    }

    private static TeamWorkspaceItem Item(string kind = "note", string body = "Contenu synthétique.") => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = kind, Title = "Contribution " + kind, Body = body,
        TicketKey = "fixture-ticket-7000001", Author = "Technicien de démonstration",
        Status = kind is "knowledge" or "configuration" or "statistics" ? "draft" : "open"
    };
    private static async Task<TeamWorkspaceStore> StoreAsync(string label)
    {
        var store = new TeamWorkspaceStore(Path.Combine(Root, (++_stores).ToString("D3") + "-" + label + ".db"));
        await store.InitializeAsync(); return store;
    }
    private static Task<TeamWorkspaceSyncResult> Sync(TeamWorkspaceStore local, ITeamWorkspaceReplica central) =>
        TeamWorkspaceSyncService.SynchronizeAsync(local, central);
    private static async Task<TeamWorkspaceItem> RequiredAsync(TeamWorkspaceStore store, string id) =>
        await store.GetAsync(id) ?? throw new InvalidOperationException("Expected the synthetic contribution to exist.");
    private static async Task<TeamWorkspaceItem[]> ExportAsync(TeamWorkspaceStore store)
    {
        using var stream = new MemoryStream(); await store.ExportAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => TeamWorkspaceCodec.Read(line.TrimEnd('\r'))).ToArray();
    }
    private static async Task<IReadOnlyList<TeamWorkspaceChange>> AllChangesAsync(ITeamWorkspaceReplica replica)
    {
        var result = new List<TeamWorkspaceChange>(); long cursor = 0;
        while (true)
        {
            var page = await replica.ChangesAsync(cursor, 50);
            if (page.Count == 0) return result;
            Require(page.All(x => x.Sequence > cursor), "The replica must not replay an earlier feed sequence.");
            result.AddRange(page); cursor = page[^1].Sequence;
        }
    }
    private static async Task Check(string name, Func<Task> action)
    {
        _total++;
        try { await action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private sealed class ReplicaFactory(string? connectionString)
    {
        public async Task<ReplicaScope> CreateAsync()
        {
            if (connectionString is null) return new ReplicaScope(new MemoryReplica(), null, null);
            var schema = "gp_team_checks_" + Guid.NewGuid().ToString("N");
            var admin = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
            await using (var db = new NpgsqlConnection(admin.ConnectionString))
            {
                await db.OpenAsync();
                // schema contains a fixed prefix and generated lowercase hex only; no user input is interpolated.
                await using var create = new NpgsqlCommand("CREATE SCHEMA " + schema, db);
                await create.ExecuteNonQueryAsync();
            }
            var isolated = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { SearchPath = schema };
            return new ReplicaScope(new PostgresTeamWorkspaceReplica(isolated.ConnectionString), admin.ConnectionString, schema);
        }
    }
    private sealed class ReplicaScope(ITeamWorkspaceReplica replica, string? adminConnection, string? ownedSchema) : IAsyncDisposable
    {
        public ITeamWorkspaceReplica Replica { get; } = replica;
        public async Task VerifyProjectionAsync(int expectedItems)
        {
            if (adminConnection is null || ownedSchema is null) return;
            await using var db = new NpgsqlConnection(adminConnection); await db.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM " + ownedSchema + ".glpi_pilot_shared_items", db);
            Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == expectedItems,
                "The real PostgreSQL read view must contain the synchronized contributions.");
        }
        public async Task VerifyConflictsAsync(string left, string right)
        {
            if (adminConnection is null || ownedSchema is null) return;
            await using var db = new NpgsqlConnection(adminConnection); await db.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT current_json,incoming_json FROM " + ownedSchema + ".gp_shared_conflicts", db);
            await using var reader = await command.ExecuteReaderAsync();
            Require(await reader.ReadAsync(), "The real central database must archive the conflicting pair.");
            var bodies = new[] { TeamWorkspaceCodec.Read(reader.GetString(0)).Body, TeamWorkspaceCodec.Read(reader.GetString(1)).Body };
            Require(bodies.Order(StringComparer.Ordinal).SequenceEqual(new[] { left, right }.Order(StringComparer.Ordinal))
                && !await reader.ReadAsync(), "The central archive must retain both original bodies exactly once.");
        }
        public async ValueTask DisposeAsync()
        {
            if (adminConnection is null || ownedSchema is null) return;
            await using var db = new NpgsqlConnection(adminConnection); await db.OpenAsync();
            // Only this scope's successfully created, generated schema is eligible for cleanup.
            await using var remove = new NpgsqlCommand("DROP SCHEMA " + ownedSchema + " CASCADE", db);
            await remove.ExecuteNonQueryAsync();
        }
    }
    private sealed class AfterFirstPushReplica(ITeamWorkspaceReplica inner, Action afterCommit) : ITeamWorkspaceReplica
    {
        private int _pushed;
        public string FirstMutationId { get; private set; } = "";
        public Task<string> InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public async Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default)
        {
            var receipt = await inner.PushAsync(mutation, ct);
            if (Interlocked.Increment(ref _pushed) == 1) { FirstMutationId = mutation.MutationId; afterCommit(); }
            return receipt;
        }
        public Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default) => inner.ChangesAsync(cursor, limit, ct);
    }
    private sealed class TwoPushBarrierReplica(ITeamWorkspaceReplica inner) : ITeamWorkspaceReplica
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public Task<string> InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public async Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(ct);
            return await inner.PushAsync(mutation, ct);
        }
        public Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default) => inner.ChangesAsync(cursor, limit, ct);
    }
    private sealed class BeforeFirstChangesReplica(ITeamWorkspaceReplica inner, Func<Task> beforePull) : ITeamWorkspaceReplica
    {
        private int _reads;
        public Task<string> InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default) => inner.PushAsync(mutation, ct);
        public async Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _reads) == 1) await beforePull();
            return await inner.ChangesAsync(cursor, limit, ct);
        }
    }

    /// <summary>In-memory protocol peer for deterministic faults; the --postgres pass replaces it with the real PostgreSQL implementation.</summary>
    private sealed class MemoryReplica : ITeamWorkspaceReplica
    {
        private readonly string _identity = Guid.NewGuid().ToString("N");
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, TeamWorkspaceItem> _items = [];
        private readonly Dictionary<string, (string Hash, TeamWorkspaceReceipt Receipt)> _receipts = [];
        private readonly List<TeamWorkspaceChange> _changes = [];
        public Task<string> InitializeAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_identity); }
        public async Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                TeamWorkspaceCodec.Validate(mutation.Item);
                var hash = TeamWorkspaceCodec.ContentHash(mutation.Item) + ":" + mutation.BaseRevision;
                if (_receipts.TryGetValue(mutation.MutationId, out var previous))
                {
                    if (previous.Hash != hash) throw new InvalidDataException("Synthetic peer refused mutation reuse.");
                    return Copy(previous.Receipt);
                }
                _items.TryGetValue(mutation.Item.Id, out var current);
                TeamWorkspaceReceipt receipt;
                if (current is not null && TeamWorkspaceCodec.ContentHash(current) == TeamWorkspaceCodec.ContentHash(mutation.Item))
                    receipt = new("unchanged", current.Copy());
                else
                {
                    var conflict = (current?.CentralRevision ?? 0) != mutation.BaseRevision;
                    var saved = (conflict ? current ?? throw new InvalidDataException("Synthetic expected revision is absent.") : mutation.Item).Copy();
                    saved.CentralRevision = _changes.Count + 1; saved.Revision = 0; saved.Pending = false; saved.Conflicts = 0;
                    _items[saved.Id] = saved;
                    _changes.Add(new(saved.CentralRevision, saved.Copy(), conflict ? mutation.Item.Copy() : null, conflict ? "concurrent-edit" : ""));
                    receipt = new(conflict ? "conflict" : "accepted", saved.Copy());
                }
                _receipts.Add(mutation.MutationId, (hash, Copy(receipt))); return receipt;
            }
            finally { _gate.Release(); }
        }
        public async Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try { return _changes.Where(x => x.Sequence > cursor).Take(limit).Select(x => new TeamWorkspaceChange(x.Sequence, x.Item.Copy(), x.Incoming?.Copy(), x.Reason)).ToArray(); }
            finally { _gate.Release(); }
        }
        private static TeamWorkspaceReceipt Copy(TeamWorkspaceReceipt receipt) => new(receipt.Outcome, receipt.Current.Copy());
    }
}
