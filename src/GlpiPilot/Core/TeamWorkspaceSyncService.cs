using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace GlpiPilot.Core;

public interface ITeamWorkspaceReplica
{
    Task<string> InitializeAsync(CancellationToken ct = default);
    Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default);
    Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default);
}

/// <summary>Shared PostgreSQL protocol v1. Each mutation is idempotent and uses compare-and-swap on the central revision.</summary>
public sealed class PostgresTeamWorkspaceReplica(string connectionString) : ITeamWorkspaceReplica
{
    private const long AdvisoryLock = 0x47504C50544D;
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS gp_shared_metadata(name text PRIMARY KEY,value text NOT NULL);
        CREATE TABLE IF NOT EXISTS gp_shared_items(id text PRIMARY KEY,revision bigint NOT NULL,kind text NOT NULL,
          ticket_key text NOT NULL,deleted boolean NOT NULL,content_hash text NOT NULL,document_json text NOT NULL);
        CREATE TABLE IF NOT EXISTS gp_shared_changes(sequence bigserial PRIMARY KEY,item_id text NOT NULL,document_json text NOT NULL,incoming_json text,reason text NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS gp_shared_conflicts(id text PRIMARY KEY,item_id text NOT NULL,recorded_at timestamptz NOT NULL,current_json text NOT NULL,incoming_json text NOT NULL);
        CREATE TABLE IF NOT EXISTS gp_shared_receipts(mutation_id text PRIMARY KEY,item_id text NOT NULL,request_hash text NOT NULL,response_json text NOT NULL);
        CREATE INDEX IF NOT EXISTS gp_shared_items_kind ON gp_shared_items(kind,deleted);
        CREATE INDEX IF NOT EXISTS gp_shared_items_ticket ON gp_shared_items(ticket_key);
        CREATE OR REPLACE VIEW glpi_pilot_shared_items AS
          SELECT id,revision,kind,ticket_key,deleted,document_json FROM gp_shared_items;
        """;
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        if (AppPaths.SimulationRoot is not null) throw new InvalidOperationException("La simulation utilise sa réplique d’équipe locale.");
        var connection = new NpgsqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; } catch { await connection.DisposeAsync(); throw; }
    }
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, tx); foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command;
    }
    private static async Task Lock(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = Command(connection, tx, "SET LOCAL lock_timeout='8s'; SET LOCAL statement_timeout='30s'; SELECT pg_advisory_xact_lock(@lock)", ("lock", AdvisoryLock)); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<string> InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(ct); await Lock(db, tx, ct);
        await using (var schema = Command(db, tx, SchemaSql)) await schema.ExecuteNonQueryAsync(ct);
        await using (var insert = Command(db, tx, "INSERT INTO gp_shared_metadata(name,value) VALUES('schema','1'),('identity',@id) ON CONFLICT(name) DO NOTHING", ("id", Guid.NewGuid().ToString("N")))) await insert.ExecuteNonQueryAsync(ct);
        await using (var version = Command(db, tx, "SELECT value FROM gp_shared_metadata WHERE name='schema'"))
            if ((string?)await version.ExecuteScalarAsync(ct) != "1") throw new InvalidDataException("Version de la base centrale d’équipe non prise en charge.");
        await using var identity = Command(db, tx, "SELECT value FROM gp_shared_metadata WHERE name='identity'"); var value = (string?)await identity.ExecuteScalarAsync(ct);
        if (value is null || !Regex.IsMatch(value, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Identité de base centrale invalide.");
        await tx.CommitAsync(ct); return value;
    }
    public async Task<TeamWorkspaceReceipt> PushAsync(TeamWorkspaceMutation mutation, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(mutation.MutationId, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) || mutation.BaseRevision < 0) throw new InvalidDataException("Mutation d’équipe invalide.");
        TeamWorkspaceCodec.Validate(mutation.Item); var requestHash = TeamWorkspaceCodec.ContentHash(mutation.Item) + ":" + mutation.BaseRevision.ToString(CultureInfo.InvariantCulture);
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(ct); await Lock(db, tx, ct);
        await using (var repeated = Command(db, tx, "SELECT request_hash,response_json FROM gp_shared_receipts WHERE mutation_id=@id", ("id", mutation.MutationId)))
        await using (var reader = await repeated.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != requestHash) throw new InvalidDataException("Un identifiant de mutation a été réutilisé avec un autre contenu.");
                return JsonSerializer.Deserialize<TeamWorkspaceReceipt>(reader.GetString(1), TeamWorkspaceCodec.Json) ?? throw new InvalidDataException("Accusé de synchronisation illisible.");
            }
        }
        TeamWorkspaceItem? current = null;
        await using (var query = Command(db, tx, "SELECT document_json FROM gp_shared_items WHERE id=@id FOR UPDATE", ("id", mutation.Item.Id)))
        {
            var json = (string?)await query.ExecuteScalarAsync(ct); if (json is not null) current = TeamWorkspaceCodec.Read(json);
        }
        TeamWorkspaceReceipt result;
        if (current is not null && TeamWorkspaceCodec.ContentHash(current) == TeamWorkspaceCodec.ContentHash(mutation.Item)) result = new("unchanged", current);
        else
        {
            var conflict = (current?.CentralRevision ?? 0) != mutation.BaseRevision;
            if (conflict && current is null) throw new InvalidDataException("La version centrale attendue n’existe plus.");
            var next = (conflict ? current! : mutation.Item).Copy(); next.Revision = 0; next.Pending = false; next.Conflicts = 0;
            // Serialize feed allocation with commit order: a client cursor never overtakes an uncommitted change.
            await using (var sequence = Command(db, tx, "SELECT nextval(pg_get_serial_sequence('gp_shared_changes','sequence'))"))
                next.CentralRevision = Convert.ToInt64(await sequence.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            var json = TeamWorkspaceCodec.Write(next);
            await using (var save = Command(db, tx, """
                INSERT INTO gp_shared_items(id,revision,kind,ticket_key,deleted,content_hash,document_json) VALUES(@id,@revision,@kind,@ticket,@deleted,@hash,@json)
                ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,kind=excluded.kind,ticket_key=excluded.ticket_key,
                  deleted=excluded.deleted,content_hash=excluded.content_hash,document_json=excluded.document_json
                """, ("id", next.Id), ("revision", next.CentralRevision), ("kind", next.Kind), ("ticket", next.TicketKey), ("deleted", next.Deleted), ("hash", TeamWorkspaceCodec.ContentHash(next)), ("json", json))) await save.ExecuteNonQueryAsync(ct);
            var candidateJson = conflict ? TeamWorkspaceCodec.Write(mutation.Item) : "";
            await using (var feed = Command(db, tx, "INSERT INTO gp_shared_changes(sequence,item_id,document_json,incoming_json,reason) VALUES(@revision,@id,@json,NULLIF(@incoming,''),@reason)",
                ("revision", next.CentralRevision), ("id", next.Id), ("json", json), ("incoming", candidateJson), ("reason", conflict ? "concurrent-edit" : ""))) await feed.ExecuteNonQueryAsync(ct);
            if (conflict)
            {
                await using var preserve = Command(db, tx, "INSERT INTO gp_shared_conflicts(id,item_id,recorded_at,current_json,incoming_json) VALUES(@key,@id,now(),@current,@incoming) ON CONFLICT(id) DO NOTHING",
                    ("key", TeamWorkspaceCodec.ConflictKey(next, mutation.Item)), ("id", next.Id), ("current", json), ("incoming", candidateJson));
                await preserve.ExecuteNonQueryAsync(ct);
            }
            result = new(conflict ? "conflict" : "accepted", next);
        }
        await using (var receipt = Command(db, tx, "INSERT INTO gp_shared_receipts(mutation_id,item_id,request_hash,response_json) VALUES(@mutation,@id,@hash,@json)",
            ("mutation", mutation.MutationId), ("id", mutation.Item.Id), ("hash", requestHash), ("json", JsonSerializer.Serialize(result, TeamWorkspaceCodec.Json)))) await receipt.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct); return result;
    }
    public async Task<IReadOnlyList<TeamWorkspaceChange>> ChangesAsync(long cursor, int limit, CancellationToken ct = default)
    {
        if (cursor < 0 || limit is < 1 or > 200) throw new InvalidDataException("Curseur de synchronisation invalide.");
        await using var db = await Open(ct); await using var query = Command(db, null, "SELECT sequence,document_json,incoming_json,reason FROM gp_shared_changes WHERE sequence>@cursor ORDER BY sequence LIMIT @limit", ("cursor", cursor), ("limit", limit));
        await using var reader = await query.ExecuteReaderAsync(ct); var result = new List<TeamWorkspaceChange>();
        long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(1); var incomingJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            bytes += System.Text.Encoding.UTF8.GetByteCount(json) + System.Text.Encoding.UTF8.GetByteCount(incomingJson ?? "");
            if (result.Count > 0 && bytes > TeamWorkspaceCodec.MaximumBatchBytes) break;
            var sequence = reader.GetInt64(0); var item = TeamWorkspaceCodec.Read(json); var incoming = incomingJson is null ? null : TeamWorkspaceCodec.Read(incomingJson);
            if (sequence <= cursor || item.CentralRevision != sequence || incoming is not null && incoming.Id != item.Id) throw new InvalidDataException("Ordre du journal central invalide.");
            result.Add(new(sequence, item, incoming, reader.GetString(3))); cursor = sequence;
        }
        return result;
    }
}

public static class TeamWorkspaceSyncService
{
    public static async Task<TeamWorkspaceSyncResult> SynchronizeAsync(TeamWorkspaceStore local, ITeamWorkspaceReplica central,
        IProgress<TeamWorkspaceSyncResult>? progress = null, CancellationToken ct = default)
    {
        await local.InitializeAsync(ct); var identity = await central.InitializeAsync(ct); await local.PrepareRemoteAsync(identity, ct);
        var pushed = 0; var pulled = 0; var conflicts = 0;
        // Bounded per invocation; remaining outbox entries are retained for the next automatic or manual pass.
        for (var batch = 0; batch < 100; batch++)
        {
            var mutations = await local.PendingAsync(50, ct); if (mutations.Count == 0) break;
            foreach (var mutation in mutations)
            {
                ct.ThrowIfCancellationRequested(); var receipt = await central.PushAsync(mutation, ct); await local.AcknowledgeAsync(mutation, receipt, ct);
                pushed++; if (receipt.Outcome == "conflict") conflicts++;
            }
            progress?.Report(new(pushed, pulled, conflicts, (await local.CountsAsync(ct)).Pending));
        }
        var cursor = await local.CursorAsync(ct);
        for (var batch = 0; batch < 1000; batch++)
        {
            var changes = await central.ChangesAsync(cursor, 50, ct); if (changes.Count == 0) break;
            foreach (var change in changes)
            {
                if (change.Sequence <= cursor) throw new InvalidDataException("Le journal central n’avance pas.");
                if (await local.ApplyChangeAsync(change, ct)) conflicts++; cursor = change.Sequence; pulled++;
            }
            progress?.Report(new(pushed, pulled, conflicts, (await local.CountsAsync(ct)).Pending));
        }
        return new(pushed, pulled, conflicts, (await local.CountsAsync(ct)).Pending);
    }
}
