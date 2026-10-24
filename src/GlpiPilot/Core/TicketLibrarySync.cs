using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace GlpiPilot.Core;

internal sealed record TicketLibraryMutation(string Id, TicketLibraryDocument Document, string? RequestJson = null);
internal sealed record TicketLibraryPushReceipt(long Revision, string Outcome, bool Conflict, TicketLibraryDocument Document);
internal sealed record TicketLibraryChange(long Revision, TicketLibraryDocument Document, TicketLibraryDocument? Incoming, string Reason);
internal interface ITicketLibraryReplica
{
    Task<string> InitializeAsync(CancellationToken ct = default);
    Task<TicketLibraryPushReceipt> PushAsync(TicketLibraryMutation mutation, CancellationToken ct);
    Task<IReadOnlyList<TicketLibraryChange>> PullAsync(long afterRevision, int limit, CancellationToken ct);
}

/// <summary>Version 1 of the shared PostgreSQL protocol. Source timestamps, not workstation clocks, choose current content.</summary>
public sealed class TicketLibraryPostgresStore : ITicketLibraryReplica
{
    public const int SchemaVersion = 1;
    public const long PublishLock = 7273459411307858;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private string? _identity;

    public TicketLibraryPostgresStore(string connectionString)
    {
        if (AppPaths.SimulationRoot is not null) throw new InvalidOperationException("La simulation utilise sa réplique locale, sans connexion PostgreSQL.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            IncludeErrorDetail = false, LogParameters = false, Timeout = 15, CommandTimeout = 30,
            CancellationTimeout = 2000, ApplicationName = "GLPI-Pilot Library", MaxPoolSize = 10
        };
        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database)) throw new InvalidDataException("Le serveur et la base PostgreSQL sont requis.");
        _connectionString = builder.ToString();
    }

    Task<TicketLibraryPushReceipt> ITicketLibraryReplica.PushAsync(TicketLibraryMutation mutation, CancellationToken ct) => PushAsync(mutation, ct);
    Task<IReadOnlyList<TicketLibraryChange>> ITicketLibraryReplica.PullAsync(long afterRevision, int limit, CancellationToken ct) => PullAsync(afterRevision, limit, ct);

    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS glpi_pilot_library_schema (
          singleton integer PRIMARY KEY CHECK(singleton=1), version integer NOT NULL, identity uuid NOT NULL, last_revision bigint NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS glpi_pilot_library_tickets (
          ticket_key text PRIMARY KEY, document_json text NOT NULL CHECK(octet_length(document_json)<=8388608),
          content_hash text NOT NULL, revision bigint NOT NULL);
        CREATE TABLE IF NOT EXISTS glpi_pilot_library_changes (
          revision bigint PRIMARY KEY, ticket_key text NOT NULL, document_json text NOT NULL CHECK(octet_length(document_json)<=8388608),
          incoming_json text CHECK(octet_length(incoming_json)<=8388608), reason text NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS glpi_pilot_library_receipts (
          mutation_id uuid PRIMARY KEY, ticket_key text NOT NULL, request_hash text NOT NULL, receipt_json text NOT NULL CHECK(octet_length(receipt_json)<=16777216));
        CREATE TABLE IF NOT EXISTS glpi_pilot_library_conflicts (
          conflict_id text PRIMARY KEY, ticket_key text NOT NULL, reason text NOT NULL,
          current_json text NOT NULL CHECK(octet_length(current_json)<=8388608), incoming_json text NOT NULL CHECK(octet_length(incoming_json)<=8388608), recorded_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS glpi_pilot_library_history (
          ticket_key text NOT NULL, revision bigint NOT NULL, document_json text NOT NULL CHECK(octet_length(document_json)<=8388608), reason text NOT NULL,
          PRIMARY KEY(ticket_key,revision));
        """;

    public async Task<string> InitializeAsync(CancellationToken ct = default)
    {
        if (_identity is not null) return _identity;
        await _initialization.WaitAsync(ct);
        try
        {
            if (_identity is not null) return _identity;
            await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await Lock(c, tx, ct);
            await Execute(c, tx, "CREATE TABLE IF NOT EXISTS glpi_pilot_library_schema (singleton integer PRIMARY KEY CHECK(singleton=1),version integer NOT NULL,identity uuid NOT NULL,last_revision bigint NOT NULL DEFAULT 0)", ct);
            await Execute(c, tx, "INSERT INTO glpi_pilot_library_schema(singleton,version,identity) VALUES(1,1,@identity) ON CONFLICT(singleton) DO NOTHING", ct, ("identity", Guid.NewGuid()));
            string identity;
            await using (var command = Command(c, tx, "SELECT version,identity FROM glpi_pilot_library_schema WHERE singleton=1"))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct) || reader.GetInt32(0) != SchemaVersion) throw new InvalidDataException("Version du schéma central non prise en charge. Aucune donnée n’a été remplacée.");
                identity = reader.GetGuid(1).ToString("N");
            }
            await Execute(c, tx, SchemaSql, ct); await tx.CommitAsync(ct); _identity = identity; return identity;
        }
        finally { _initialization.Release(); }
    }

    internal async Task<TicketLibraryPushReceipt> PushAsync(TicketLibraryMutation mutation, CancellationToken ct)
    {
        await InitializeAsync(ct); TicketLibraryRules.Validate(mutation.Document);
        if (!Guid.TryParseExact(mutation.Id, "N", out var mutationId)) throw new InvalidDataException("Identifiant de mutation invalide.");
        var requestHash = MutationRequestHash(mutation);
        await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct); await Lock(c, tx, ct);
        await using (var receiptCommand = Command(c, tx, "SELECT ticket_key,request_hash,receipt_json FROM glpi_pilot_library_receipts WHERE mutation_id=@id", ("id", mutationId)))
        await using (var reader = await receiptCommand.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != mutation.Document.Key || reader.GetString(1) != requestHash)
                    throw new InvalidDataException("Une mutation existante ne peut pas être réutilisée pour un autre contenu.");
                var previous = JsonSerializer.Deserialize<TicketLibraryPushReceipt>(reader.GetString(2), TicketLibraryRules.Json)
                    ?? throw new InvalidDataException("Le reçu de synchronisation est invalide.");
                return previous with { Document = TicketLibraryRules.Deserialize(TicketLibraryRules.Serialize(previous.Document)) };
            }
        }
        TicketLibraryDocument? current = null;
        await using (var command = Command(c, tx, "SELECT document_json FROM glpi_pilot_library_tickets WHERE ticket_key=@key FOR UPDATE", ("key", mutation.Document.Key)))
            if (await command.ExecuteScalarAsync(ct) is string json) current = TicketLibraryRules.Deserialize(json);
        var merge = TicketLibraryRules.Merge(current, mutation.Document);
        var conflict = false;
        if (merge.Conflict && current is not null)
            conflict = await Execute(c, tx, "INSERT INTO glpi_pilot_library_conflicts(conflict_id,ticket_key,reason,current_json,incoming_json) VALUES(@id,@key,@reason,@current,@incoming) ON CONFLICT(conflict_id) DO NOTHING", ct,
                ("id", TicketLibraryStore.ConflictId(current, mutation.Document, merge.Outcome)), ("key", current.Key), ("reason", merge.Outcome), ("current", TicketLibraryRules.Serialize(current)), ("incoming", TicketLibraryRules.Serialize(mutation.Document))) > 0;
        var changed = current is null || current.ContentHash != merge.Document.ContentHash || current.DetailsCurrent != merge.Document.DetailsCurrent
            || current.ConfirmationPending != merge.Document.ConfirmationPending || current.ConfirmedContentHash != merge.Document.ConfirmedContentHash || current.ConfirmationId != merge.Document.ConfirmationId
            || current.ReadAt != merge.Document.ReadAt || current.ReaderSignature != merge.Document.ReaderSignature;
        var revision = current?.CentralRevision ?? 0;
        if (changed || conflict)
        {
            // The transaction lock is held through COMMIT. A pull cannot observe revision N+1 before revision N has committed.
            await using (var sequence = Command(c, tx, "UPDATE glpi_pilot_library_schema SET last_revision=last_revision+1 WHERE singleton=1 RETURNING last_revision"))
                revision = Convert.ToInt64(await sequence.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (current is not null && changed)
                await Execute(c, tx, "INSERT INTO glpi_pilot_library_history(ticket_key,revision,document_json,reason) VALUES(@key,@revision,@json,@reason) ON CONFLICT DO NOTHING", ct,
                    ("key", current.Key), ("revision", current.CentralRevision), ("json", TicketLibraryRules.Serialize(current)), ("reason", merge.Outcome));
            merge.Document.LocalRevision = 0; merge.Document.CentralRevision = revision; merge.Document.Pending = false;
            var json = TicketLibraryRules.Serialize(merge.Document);
            await Execute(c, tx, "INSERT INTO glpi_pilot_library_tickets VALUES(@key,@json,@hash,@revision) ON CONFLICT(ticket_key) DO UPDATE SET document_json=excluded.document_json,content_hash=excluded.content_hash,revision=excluded.revision", ct,
                ("key", merge.Document.Key), ("json", json), ("hash", merge.Document.ContentHash), ("revision", revision));
            await Execute(c, tx, "INSERT INTO glpi_pilot_library_changes(revision,ticket_key,document_json,incoming_json,reason) VALUES(@revision,@key,@json,@incoming,@reason)", ct,
                ("revision", revision), ("key", merge.Document.Key), ("json", json), ("incoming", conflict ? TicketLibraryRules.Serialize(mutation.Document) : null), ("reason", merge.Outcome));
        }
        var receipt = new TicketLibraryPushReceipt(revision, merge.Outcome, conflict, merge.Document);
        await Execute(c, tx, "INSERT INTO glpi_pilot_library_receipts VALUES(@id,@key,@hash,@json)", ct,
            ("id", mutationId), ("key", mutation.Document.Key), ("hash", requestHash), ("json", TicketLibraryRules.Serialize(receipt)));
        await tx.CommitAsync(ct); return receipt;
    }

    private static string MutationRequestHash(TicketLibraryMutation mutation)
    {
        // The outbox is immutable. Re-serializing a 3.2.0 request after upgrading
        // adds default proof fields and qualifies its old freshness flag, which
        // must not change the identity of an already committed, unacknowledged UUID.
        var request = mutation.RequestJson ?? TicketLibraryRules.Serialize(mutation.Document);
        if (mutation.RequestJson is not null)
        {
            try
            {
                var original = TicketLibraryRules.Deserialize(request);
                var current = TicketLibraryRules.Deserialize(TicketLibraryRules.Serialize(mutation.Document));
                if (!string.Equals(TicketLibraryRules.Serialize(original), TicketLibraryRules.Serialize(current), StringComparison.Ordinal))
                    throw new InvalidDataException("La requête immuable ne correspond pas au document à synchroniser.");
            }
            catch (JsonException)
            { throw new InvalidDataException("La requête immuable de synchronisation est invalide."); }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request)));
    }

    internal async Task<IReadOnlyList<TicketLibraryChange>> PullAsync(long afterRevision, int limit, CancellationToken ct)
    {
        await InitializeAsync(ct);
        if (afterRevision < 0) throw new InvalidDataException("Le curseur central est invalide.");
        var changes = new List<TicketLibraryChange>();
        await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(ct);
        await using var command = Command(c, null, "SELECT revision,document_json,incoming_json,reason FROM glpi_pilot_library_changes WHERE revision>@cursor ORDER BY revision LIMIT @limit", ("cursor", afterRevision), ("limit", Math.Clamp(limit, 1, 100)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var currentJson = reader.GetString(1); var incomingJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            bytes += currentJson.Length + (incomingJson?.Length ?? 0);
            // Bounded pages even when individual tickets are large; the next call resumes at the last included revision.
            if (changes.Count > 0 && bytes > 32 * 1024 * 1024) break;
            var document = TicketLibraryRules.Deserialize(currentJson); var incoming = incomingJson is null ? null : TicketLibraryRules.Deserialize(incomingJson);
            if (document.CentralRevision != reader.GetInt64(0) || incoming is not null && incoming.Key != document.Key)
                throw new InvalidDataException("Une entrée du flux central est incohérente.");
            changes.Add(new(reader.GetInt64(0), document, incoming, reader.GetString(3)));
        }
        return changes;
    }

    private static Task<int> Lock(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
        => Execute(c, tx, "SET LOCAL lock_timeout='8s'; SET LOCAL statement_timeout='30s'; SELECT pg_advisory_xact_lock(7273459411307858)", ct);

    private static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, c, tx) { CommandTimeout = 30 };
        foreach (var p in parameters)
            if (p.Value is null) command.Parameters.Add(p.Name, NpgsqlDbType.Text).Value = DBNull.Value;
            else command.Parameters.AddWithValue(p.Name, p.Value);
        return command;
    }
    private static async Task<int> Execute(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    { await using var command = Command(c, tx, sql, parameters); return await command.ExecuteNonQueryAsync(ct); }
}

/// <summary>Durable UUID outbox + transactional delta cursor. A failed operation leaves the next run resumable.</summary>
public static class TicketLibrarySyncService
{
    public static Task<TicketLibrarySyncResult> SynchronizeAsync(TicketLibraryStore local, TicketLibraryPostgresStore central,
        IProgress<TicketLibrarySyncProgress>? progress = null, CancellationToken ct = default)
        => SynchronizeAsync(local, (ITicketLibraryReplica)central, progress, ct);

    internal static async Task<TicketLibrarySyncResult> SynchronizeAsync(TicketLibraryStore local, ITicketLibraryReplica central,
        IProgress<TicketLibrarySyncProgress>? progress = null, CancellationToken ct = default)
    {
        var identity = await central.InitializeAsync(ct);
        var cursor = await Prepare(local, identity, ct); var result = new TicketLibrarySyncResult { Cursor = cursor };
        // A finite batch prevents an automatic sync from monopolizing the application. Remaining mutations stay durable.
        for (var batch = 0; batch < 100; batch++)
        {
            var mutations = await Pending(local, 50, ct); if (mutations.Count == 0) break;
            foreach (var mutation in mutations)
            {
                ct.ThrowIfCancellationRequested(); var receipt = await central.PushAsync(mutation, ct);
                // An acknowledgement only removes this exact immutable mutation, never a newer ticket version.
                await local.Run(c =>
                {
                    using var tx = c.BeginTransaction(); AssertCentral(c, tx, identity);
                    var count = TicketLibraryStore.Execute(c, tx, "DELETE FROM ticket_library_outbox WHERE mutation_id=$id", ("$id", mutation.Id)); tx.Commit(); return count;
                }, ct);
                result.Pushed++; if (receipt.Conflict) result.Conflicts++;
                progress?.Report(new("push", result.Pushed, result.Pulled, result.Conflicts, await PendingCount(local, ct)));
            }
        }
        for (var batch = 0; batch < 1000; batch++)
        {
            ct.ThrowIfCancellationRequested(); var changes = await central.PullAsync(result.Cursor, 100, ct); if (changes.Count == 0) break;
            var applied = await Apply(local, identity, result.Cursor, changes, ct); result.Cursor = applied.Cursor;
            result.Pulled += changes.Count; result.Conflicts += applied.Conflicts;
            progress?.Report(new("pull", result.Pushed, result.Pulled, result.Conflicts, await PendingCount(local, ct)));
        }
        await local.Run(c => TicketLibraryStore.Execute(c, null, "UPDATE ticket_library_sync SET last_sync_at=$date WHERE central_id=$id", ("$date", DateTimeOffset.UtcNow.ToString("O")), ("$id", identity)), ct);
        result.Pending = await PendingCount(local, ct); progress?.Report(new("complete", result.Pushed, result.Pulled, result.Conflicts, result.Pending));
        return result;
    }

    internal static Task<long> Prepare(TicketLibraryStore local, string identity, CancellationToken ct) => local.Run(c =>
    {
        using var tx = c.BeginTransaction();
        var existing = TicketLibraryStore.ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_sync_active WHERE singleton=1 AND central_id=$id", ("$id", identity));
        TicketLibraryStore.Execute(c, tx, "INSERT OR IGNORE INTO ticket_library_sync(central_id,cursor) VALUES($id,0)", ("$id", identity));
        if (existing == 0)
        {
            TicketLibraryStore.Execute(c, tx, "INSERT INTO ticket_library_sync_active VALUES(1,$id) ON CONFLICT(singleton) DO UPDATE SET central_id=excluded.central_id", ("$id", identity));
            var keys = new List<string>();
            using (var command = TicketLibraryStore.Command(c, tx, "SELECT ticket_key FROM ticket_library_tickets t WHERE NOT EXISTS(SELECT 1 FROM ticket_library_outbox o WHERE o.ticket_key=t.ticket_key)"))
            using (var reader = command.ExecuteReader()) while (reader.Read()) { ct.ThrowIfCancellationRequested(); keys.Add(reader.GetString(0)); }
            foreach (var key in keys) { ct.ThrowIfCancellationRequested(); TicketLibraryStore.Queue(c, tx, TicketLibraryStore.Read(c, tx, key)!); }
        }
        var cursor = TicketLibraryStore.ScalarLong(c, tx, "SELECT cursor FROM ticket_library_sync WHERE central_id=$id", ("$id", identity)); tx.Commit(); return cursor;
    }, ct);

    internal static Task<IReadOnlyList<TicketLibraryMutation>> Pending(TicketLibraryStore local, int limit, CancellationToken ct) => local.Run<IReadOnlyList<TicketLibraryMutation>>(c =>
    {
        var result = new List<TicketLibraryMutation>();
        using var command = TicketLibraryStore.Command(c, null, "SELECT mutation_id,document_json FROM ticket_library_outbox ORDER BY rowid LIMIT $limit", ("$limit", Math.Clamp(limit, 1, 100)));
        using var reader = command.ExecuteReader(); long bytes = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested(); var json = reader.GetString(1); bytes += json.Length;
            if (result.Count > 0 && bytes > 32 * 1024 * 1024) break;
            result.Add(new(reader.GetString(0), TicketLibraryRules.Deserialize(json), json));
        }
        return result;
    }, ct);

    internal static Task<(long Cursor, int Conflicts)> Apply(TicketLibraryStore local, string identity, long cursor, IReadOnlyList<TicketLibraryChange> changes, CancellationToken ct) => local.Run(c =>
    {
        using var tx = c.BeginTransaction();
        AssertCentral(c, tx, identity);
        var committed = TicketLibraryStore.ScalarLong(c, tx, "SELECT cursor FROM ticket_library_sync WHERE central_id=$id", ("$id", identity));
        var conflicts = 0; var next = committed;
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested(); if (change.Revision <= committed) continue;
            if (change.Revision <= next || change.Revision <= cursor) throw new InvalidDataException("Ordre du flux central invalide.");
            var current = TicketLibraryStore.Read(c, tx, change.Document.Key);
            if (change.Incoming is not null && TicketLibraryStore.AddConflict(c, tx, change.Document, change.Incoming, change.Reason)) conflicts++;
            var merge = TicketLibraryRules.Merge(current, change.Document);
            if (merge.Conflict && merge.Outcome != "older-source-version" && current is not null
                && TicketLibraryStore.AddConflict(c, tx, current, change.Document, merge.Outcome)) conflicts++;
            if (current is not null && (current.ContentHash != merge.Document.ContentHash || current.DetailsCurrent != merge.Document.DetailsCurrent))
                TicketLibraryStore.AddHistory(c, tx, current, "central-" + merge.Outcome);
            merge.Document.LocalRevision = (current?.LocalRevision ?? 0) + 1;
            merge.Document.CentralRevision = Math.Max(current?.CentralRevision ?? 0, change.Revision);
            TicketLibraryStore.Write(c, tx, merge.Document, ct); next = change.Revision;
        }
        TicketLibraryStore.Execute(c, tx, "UPDATE ticket_library_sync SET cursor=$cursor WHERE central_id=$id AND cursor=$previous", ("$cursor", next), ("$id", identity), ("$previous", committed));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return (next, conflicts);
    }, ct);

    private static void AssertCentral(SqliteConnection c, SqliteTransaction tx, string identity)
    {
        if (TicketLibraryStore.ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_sync_active WHERE singleton=1 AND central_id=$id", ("$id", identity)) != 1)
            throw new InvalidOperationException("La destination centrale a changé. La file locale est conservée ; relancez la synchronisation.");
    }

    private static Task<long> PendingCount(TicketLibraryStore local, CancellationToken ct)
        => local.Run(c => TicketLibraryStore.ScalarLong(c, null, "SELECT COUNT(DISTINCT ticket_key) FROM ticket_library_outbox"), ct);
}
