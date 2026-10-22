using GlpiPilot.Core;
using Npgsql;

internal static class TicketLibraryPostgresChecks
{
    public static async Task Run(string directory, Func<string, Func<Task>, Task> check)
    {
        var supplied = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            await check("Explicit PostgreSQL mode requires a real isolated server", () => throw new InvalidOperationException("GLPI_PILOT_TEST_POSTGRES is required with --postgres."));
            return;
        }
        var schema = "gp_library_checks_" + Guid.NewGuid().ToString("N");
        var administration = new NpgsqlConnectionStringBuilder(supplied) { IncludeErrorDetail = false, LogParameters = false, Timeout = 10, CommandTimeout = 20 };
        await using var admin = new NpgsqlConnection(administration.ToString()); await admin.OpenAsync();
        await Sql(admin, "CREATE SCHEMA " + schema);
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(administration.ToString()) { SearchPath = schema };
            var central = new TicketLibraryPostgresStore(scoped.ToString());
            var identity = await central.InitializeAsync();
            var a = new TicketLibraryStore(Path.Combine(directory, "postgres-a.db"));
            var b = new TicketLibraryStore(Path.Combine(directory, "postgres-b.db"));
            var first = Seed("1001", 1);
            await check("PostgreSQL: two real local databases exchange complete source-cited content", async () =>
            {
                await a.SaveDetailAsync(first, Detail(first, "Solution originale\u0092"), Coverage(), "pg-reader");
                var sent = await TicketLibrarySyncService.SynchronizeAsync(a, central);
                var received = await TicketLibrarySyncService.SynchronizeAsync(b, new(scoped.ToString()));
                var copy = await b.GetAsync(first.Ticket);
                Require(sent.Pushed > 0 && received.Pulled > 0 && copy!.Detail!.Sections[0].Data.Rows[0][3] == "Solution originale\u0092");
                Require((await a.GetStateAsync()).PendingCount == 0 && (await b.GetStateAsync()).PendingCount == 0);
            });
            await check("PostgreSQL: older offline edits cannot overwrite a newer GLPI version", async () =>
            {
                var newer = Seed("1001", 3); await a.SaveDetailAsync(newer, Detail(newer, "Nouvelle version source"), Coverage(), "pg-reader");
                await TicketLibrarySyncService.SynchronizeAsync(a, central);
                var lateOld = Detail(first, "Ancienne copie lue après"); lateOld.ReadAt = DateTimeOffset.UtcNow.AddDays(1);
                await b.SaveDetailAsync(first, lateOld, Coverage(), "pg-reader"); await TicketLibrarySyncService.SynchronizeAsync(b, central);
                var stored = await b.GetAsync(first.Ticket);
                Require(stored!.Seed.LastModifiedAt == newer.LastModifiedAt && stored.Detail!.Sections[0].Data.Rows[0][3] == "Nouvelle version source");
            });
            await check("PostgreSQL: equal source dates with different bodies preserve both versions on both PCs", async () =>
            {
                var same = Seed("1001", 3); await a.SaveDetailAsync(same, Detail(same, "Copie concurrente"), Coverage(), "pg-reader");
                await TicketLibrarySyncService.SynchronizeAsync(a, central); await TicketLibrarySyncService.SynchronizeAsync(b, central);
                Require((await a.GetConflictsAsync()).Any(c => c.Incoming.Detail?.Sections[0].Data.Rows[0][3] == "Copie concurrente"));
                Require((await b.GetConflictsAsync()).Any(c => c.Incoming.Detail?.Sections[0].Data.Rows[0][3] == "Copie concurrente"));
                Require(!(await b.GetAsync(same.Ticket))!.DetailsCurrent);
            });
            await check("PostgreSQL: unknown source dates do not become client-clock last-writer-wins", async () =>
            {
                var unknown = Seed("1002", 1); unknown.LastModifiedAt = null; unknown.LastModifiedReliable = false; unknown.LastModifiedPrecision = "unknown";
                await a.SaveDetailAsync(unknown, Detail(unknown, "Première observation"), Coverage(), "pg-reader");
                await TicketLibrarySyncService.SynchronizeAsync(a, central); await TicketLibrarySyncService.SynchronizeAsync(b, central);
                var later = Detail(unknown, "Date source inconnue différente"); later.ReadAt = DateTimeOffset.UtcNow.AddHours(1);
                await b.SaveDetailAsync(unknown, later, Coverage(), "pg-reader"); await TicketLibrarySyncService.SynchronizeAsync(b, central); await TicketLibrarySyncService.SynchronizeAsync(a, central);
                Require((await a.GetAsync(unknown.Ticket))!.Detail!.Sections[0].Data.Rows[0][3] == "Première observation");
                Require((await a.GetConflictsAsync()).Any(c => c.Reason == "incomparable-source-version" && c.Incoming.Seed.Ticket.Id == "1002"));
            });
            await check("PostgreSQL: lost acknowledgement survives process restart and UUID replay is idempotent", async () =>
            {
                var local = new TicketLibraryStore(Path.Combine(directory, "postgres-retry.db")); var seed = Seed("1003", 1);
                await local.SaveDetailAsync(seed, Detail(seed, "Reprise exacte"), Coverage(), "pg-reader");
                await TicketLibrarySyncService.Prepare(local, identity, default);
                var mutation = (await TicketLibrarySyncService.Pending(local, 50, default)).Single();
                var firstReceipt = await central.PushAsync(mutation, default); var secondReceipt = await new TicketLibraryPostgresStore(scoped.ToString()).PushAsync(mutation, default);
                Require(firstReceipt.Revision == secondReceipt.Revision && (await local.GetStateAsync()).PendingCount == 1);
                var altered = TicketLibraryRules.Copy(mutation.Document); altered.ReadAt = altered.ReadAt!.Value.AddHours(1); altered.Detail!.ReadAt = altered.ReadAt.Value;
                try { await central.PushAsync(new(mutation.Id, altered), default); throw new InvalidOperationException("Immutable mutation identifier was reused."); }
                catch (InvalidDataException) { }
                var reopened = new TicketLibraryStore(local.DatabasePath); await TicketLibrarySyncService.SynchronizeAsync(reopened, central);
                Require((await reopened.GetStateAsync()).PendingCount == 0);
                Require((await central.PullAsync(firstReceipt.Revision - 1, 100, default)).Count(c => c.Document.Seed.Ticket.Id == "1003") == 1);
            });
            await check("PostgreSQL: acknowledgement removes only its immutable outbox entry", async () =>
            {
                var local = new TicketLibraryStore(Path.Combine(directory, "postgres-ack.db")); var seed = Seed("1004", 1);
                await local.SaveDetailAsync(seed, Detail(seed, "Première"), Coverage(), "pg-reader"); await TicketLibrarySyncService.Prepare(local, identity, default);
                var firstMutation = (await TicketLibrarySyncService.Pending(local, 50, default)).Single(); await central.PushAsync(firstMutation, default);
                var newer = Seed("1004", 2); await local.SaveDetailAsync(newer, Detail(newer, "Deuxième"), Coverage(), "pg-reader");
                await local.Run(c => TicketLibraryStore.Execute(c, null, "DELETE FROM ticket_library_outbox WHERE mutation_id=$id", ("$id", firstMutation.Id)), default);
                Require((await TicketLibrarySyncService.Pending(local, 50, default)).Single().Document.Seed.LastModifiedAt == newer.LastModifiedAt);
                await TicketLibrarySyncService.SynchronizeAsync(local, central); Require((await local.GetStateAsync()).PendingCount == 0);
                Require((await local.GetAsync(newer.Ticket))!.Detail!.Sections[0].Data.Rows[0][3] == "Deuxième");
            });
            await check("PostgreSQL: publication is serialized through commit and concurrent pulls lose no revision", async () =>
            {
                var before = (await central.PullAsync(0, 100, default)).Select(c => c.Revision).DefaultIfEmpty(0).Max();
                var seed = Seed("2001", 1); var document = TicketLibraryRules.FromDetail(seed, Detail(seed, "Publication bloquée"), Coverage(), "pg-reader");
                await using var gate = new NpgsqlConnection(scoped.ToString()); await gate.OpenAsync(); await using var tx = await gate.BeginTransactionAsync();
                await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(7273459411307858)", gate, tx)) await lockCommand.ExecuteNonQueryAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var blocked = central.PushAsync(new(Guid.NewGuid().ToString("N"), document), timeout.Token);
                Require(await Task.WhenAny(blocked, Task.Delay(150, timeout.Token)) != blocked);
                Require((await central.PullAsync(before, 100, timeout.Token)).Count == 0);
                await tx.CommitAsync(timeout.Token); await blocked;
                var tasks = Enumerable.Range(2, 8).Select(i =>
                {
                    var s = Seed((2000 + i).ToString(), i);
                    return central.PushAsync(new(Guid.NewGuid().ToString("N"), TicketLibraryRules.FromDetail(s, Detail(s, "Publication " + i), Coverage(), "pg-reader")), timeout.Token);
                }).ToArray();
                var cursor = before; var seen = new HashSet<long>();
                while (tasks.Any(t => !t.IsCompleted))
                {
                    foreach (var change in await central.PullAsync(cursor, 2, timeout.Token)) { Require(seen.Add(change.Revision)); cursor = change.Revision; }
                    await Task.Delay(10, timeout.Token);
                }
                await Task.WhenAll(tasks);
                foreach (var change in await central.PullAsync(cursor, 100, timeout.Token)) { Require(seen.Add(change.Revision)); cursor = change.Revision; }
                Require(seen.Count == 9 && seen.Order().SequenceEqual(Enumerable.Range(1, 9).Select(i => before + i)));
            });
            await check("PostgreSQL: a committed partial pull resumes from its cursor after reopening SQLite", async () =>
            {
                var local = new TicketLibraryStore(Path.Combine(directory, "postgres-pull-resume.db")); await TicketLibrarySyncService.Prepare(local, identity, default);
                var page = await central.PullAsync(0, 2, default); var applied = await TicketLibrarySyncService.Apply(local, identity, 0, page, default);
                Require(applied.Cursor == page.Last().Revision);
                var reopened = new TicketLibraryStore(local.DatabasePath); var resumed = await TicketLibrarySyncService.SynchronizeAsync(reopened, central);
                Require(resumed.Cursor > applied.Cursor && (await reopened.GetStateAsync()).TicketCount >= 13);
                Require((await TicketLibrarySyncService.SynchronizeAsync(reopened, central)).Pulled == 0);
            });
            await check("PostgreSQL: cancellation retains local outbox for a later successful synchronization", async () =>
            {
                var local = new TicketLibraryStore(Path.Combine(directory, "postgres-cancel.db")); var seed = Seed("3001", 1);
                await local.SaveDetailAsync(seed, Detail(seed), Coverage(), "pg-reader"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await TicketLibrarySyncService.SynchronizeAsync(local, central, ct: cancelled.Token); throw new InvalidOperationException("Expected cancellation."); }
                catch (OperationCanceledException) { }
                Require((await local.GetStateAsync()).PendingCount == 1 && (await local.GetAsync(seed.Ticket))!.Detail is not null);
                await TicketLibrarySyncService.SynchronizeAsync(local, central); Require((await local.GetStateAsync()).PendingCount == 0);
            });
        }
        finally
        {
            // The only destructive command targets the exact random schema created above in this explicit test mode.
            await Sql(admin, "DROP SCHEMA " + schema + " CASCADE");
        }
    }

    private static async Task Sql(NpgsqlConnection connection, string sql) { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("PostgreSQL integration assertion failed."); }
    private static TicketLibraryCoverage Coverage() => new() { Complete = true, TabsLoaded = 1, TabsDiscovered = 1 };
    private static TicketLibrarySeed Seed(string id, int minute) => new()
    {
        Ticket = new(id, "https://shared-fixture.example/front/ticket.form.php?id=" + id), Title = "Ticket synthétique " + id,
        Status = "Résolu", StatusCode = 5, LastModifiedAt = new(2026, 10, 20, 12, minute, 0, TimeSpan.Zero),
        LastModifiedRaw = "2026-10-20 12:" + minute.ToString("00"), LastModifiedPrecision = "minutes", LastModifiedReliable = true
    };
    private static TicketDetailResult Detail(TicketLibrarySeed seed, string text = "Solution de test") => new()
    {
        Ticket = seed.Ticket, Title = seed.Title, ReadAt = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero),
        Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
        Sections = [new() { Name = "Chronologie", Kind = "timeline", TabKey = "treatment", Data = new() { Complete = true, Headers = ["Type", "Date", "Auteur", "Texte"], Rows = [new[] { "Solution", "2026-10-20", "Fixture", text }] } }]
    };
}
