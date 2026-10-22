using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

/// <summary>Local team documents, transactional outbox and conflict copies in the same SQLite file as the ticket library.</summary>
public sealed class TeamWorkspaceStore
{
    private readonly string _connection;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;
    public string DatabasePath { get; }
    public TeamWorkspaceStore(string path)
    {
        DatabasePath = TicketLibraryStore.ValidateDatabasePath(path);
        LibraryPreferencesStore.ValidateLocalFolder(Path.GetDirectoryName(DatabasePath)!);
        _connection = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 15 }.ConnectionString;
    }
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); var connection = new SqliteConnection(_connection);
        try
        {
            TicketLibraryStore.ValidateDatabasePath(DatabasePath); await connection.OpenAsync(ct);
            if (!_initialized)
            {
                await _initialization.WaitAsync(ct);
                try { if (!_initialized) { await InitializeConnectionAsync(connection, ct); _initialized = true; } }
                finally { _initialization.Release(); }
            }
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command;
    }
    public async Task InitializeAsync(CancellationToken ct = default) { await using var db = await Open(ct); }
    private static async Task InitializeConnectionAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var exists = Command(db, null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='gp_team_meta'");
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
        {
            await using var known = Command(db, null, "SELECT value FROM gp_team_meta WHERE name='schema'");
            if ((string?)await known.ExecuteScalarAsync(ct) != "1") throw new InvalidDataException("Version de l’espace d’équipe non prise en charge.");
        }
        await using var command = Command(db, null, """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=15000;
            CREATE TABLE IF NOT EXISTS gp_team_meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR IGNORE INTO gp_team_meta(name,value) VALUES ('schema','1');
            CREATE TABLE IF NOT EXISTS gp_team_items (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, central_revision INTEGER NOT NULL,
              kind TEXT NOT NULL, ticket_key TEXT NOT NULL, deleted INTEGER NOT NULL, updated_at TEXT NOT NULL, search_text TEXT NOT NULL, document_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS gp_team_items_kind ON gp_team_items(kind,deleted,updated_at);
            CREATE INDEX IF NOT EXISTS gp_team_items_ticket ON gp_team_items(ticket_key,deleted);
            CREATE TABLE IF NOT EXISTS gp_team_outbox (item_id TEXT PRIMARY KEY, mutation_id TEXT UNIQUE NOT NULL, base_revision INTEGER NOT NULL, document_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS gp_team_conflicts (id TEXT PRIMARY KEY, item_id TEXT NOT NULL, reason TEXT NOT NULL, recorded_at TEXT NOT NULL, current_json TEXT NOT NULL, incoming_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS gp_team_conflicts_item ON gp_team_conflicts(item_id);
            """);
        await command.ExecuteNonQueryAsync(ct);
        await using var version = Command(db, null, "SELECT value FROM gp_team_meta WHERE name='schema'");
        if ((string?)await version.ExecuteScalarAsync(ct) != "1") throw new InvalidDataException("Version de l’espace d’équipe non prise en charge.");
    }
    private static async Task<TeamWorkspaceItem?> ReadItem(SqliteConnection db, SqliteTransaction? tx, string id, CancellationToken ct)
    {
        await using var cmd = Command(db, tx, "SELECT document_json, EXISTS(SELECT 1 FROM gp_team_outbox WHERE item_id=@id), (SELECT COUNT(*) FROM gp_team_conflicts WHERE item_id=@id) FROM gp_team_items WHERE id=@id", ("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null;
        var item = TeamWorkspaceCodec.Read(reader.GetString(0)); item.Pending = reader.GetInt64(1) != 0; item.Conflicts = checked((int)reader.GetInt64(2)); return item;
    }
    public async Task<TeamWorkspaceItem?> GetAsync(string id, CancellationToken ct = default)
    { await using var db = await Open(ct); return await ReadItem(db, null, id, ct); }
    private static async Task Put(SqliteConnection db, SqliteTransaction tx, TeamWorkspaceItem item, CancellationToken ct)
    {
        var json = TeamWorkspaceCodec.Write(item);
        await using var cmd = Command(db, tx, """
            INSERT INTO gp_team_items(id,revision,central_revision,kind,ticket_key,deleted,updated_at,search_text,document_json)
            VALUES(@id,@rev,@central,@kind,@ticket,@deleted,@updated,@search,@json)
            ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,central_revision=excluded.central_revision,kind=excluded.kind,
            ticket_key=excluded.ticket_key,deleted=excluded.deleted,updated_at=excluded.updated_at,search_text=excluded.search_text,document_json=excluded.document_json
            """, ("@id", item.Id), ("@rev", item.Revision), ("@central", item.CentralRevision), ("@kind", item.Kind), ("@ticket", item.TicketKey),
            ("@deleted", item.Deleted ? 1 : 0), ("@updated", item.UpdatedAt.ToUniversalTime().ToString("O")),
            ("@search", TicketIntelligenceService.Normalize(item.Title + " " + item.Body + " " + item.Assignee + " " + item.TicketKey)), ("@json", json));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task Queue(SqliteConnection db, SqliteTransaction tx, TeamWorkspaceItem item, CancellationToken ct)
    {
        await using var cmd = Command(db, tx, "INSERT INTO gp_team_outbox(item_id,mutation_id,base_revision,document_json) VALUES(@id,@mutation,@base,@json) ON CONFLICT(item_id) DO UPDATE SET mutation_id=excluded.mutation_id,base_revision=excluded.base_revision,document_json=excluded.document_json",
            ("@id", item.Id), ("@mutation", Guid.NewGuid().ToString("N")), ("@base", item.CentralRevision), ("@json", TeamWorkspaceCodec.Write(item)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<TeamWorkspaceItem> SaveAsync(TeamWorkspaceItem proposed, long? expectedRevision, CancellationToken ct = default)
    {
        var next = proposed.Copy();
        await using var db = await Open(ct); using var tx = db.BeginTransaction(); var old = await ReadItem(db, tx, next.Id, ct);
        if (old is not null && expectedRevision != old.Revision || old is null && expectedRevision is not (null or 0))
            throw new InvalidOperationException("Cette contribution a changé. Actualisez-la avant d’enregistrer votre modification.");
        if (old?.Kind != null && old.Kind != next.Kind) throw new InvalidOperationException("Le type d’une contribution existante ne peut pas être modifié.");
        next.CreatedAt = old?.CreatedAt ?? DateTimeOffset.UtcNow; next.UpdatedAt = DateTimeOffset.UtcNow;
        next.CentralRevision = old?.CentralRevision ?? 0; next.Revision = (old?.Revision ?? 0) + 1; next.Pending = true; next.Conflicts = old?.Conflicts ?? 0;
        if (old is not null && TeamWorkspaceCodec.ContentHash(old) == TeamWorkspaceCodec.ContentHash(next)) return old;
        await Put(db, tx, next, ct); await Queue(db, tx, next, ct); tx.Commit(); return next;
    }
    public async Task<TeamWorkspaceItem> RemoveAsync(string id, long expectedRevision, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct) ?? throw new InvalidDataException("Contribution introuvable."); item.Deleted = true;
        return await SaveAsync(item, expectedRevision, ct);
    }
    public async Task<TeamWorkspaceList> ListAsync(string kind = "", string ticketKey = "", string text = "", int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (offset is < 0 or > 1000000 || limit is < 1 or > 200 || text.Length > 1000) throw new InvalidDataException("Recherche d’équipe hors limites.");
        await using var db = await Open(ct); var where = "deleted=0"; var values = new List<(string, object?)>();
        if (kind.Length > 0) { where += " AND kind=@kind"; values.Add(("@kind", kind)); }
        if (ticketKey.Length > 0) { where += " AND ticket_key=@ticket"; values.Add(("@ticket", ticketKey)); }
        var words = TicketIntelligenceService.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Take(20).ToArray();
        for (var i = 0; i < words.Length; i++) { where += $" AND instr(search_text,@word{i})>0"; values.Add(($"@word{i}", words[i])); }
        await using var count = Command(db, null, "SELECT COUNT(*) FROM gp_team_items WHERE " + where, values.ToArray());
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        values.Add(("@offset", offset)); values.Add(("@limit", limit));
        await using var query = Command(db, null, "SELECT document_json, EXISTS(SELECT 1 FROM gp_team_outbox o WHERE o.item_id=i.id), (SELECT COUNT(*) FROM gp_team_conflicts c WHERE c.item_id=i.id) FROM gp_team_items i WHERE " + where + " ORDER BY updated_at DESC,id LIMIT @limit OFFSET @offset", values.ToArray());
        await using var reader = await query.ExecuteReaderAsync(ct); var items = new List<TeamWorkspaceItem>();
        while (await reader.ReadAsync(ct))
        {
            var item = TeamWorkspaceCodec.Read(reader.GetString(0)); item.Pending = reader.GetInt64(1) != 0; item.Conflicts = checked((int)reader.GetInt64(2));
            // Listing does not ship entire configurations or long articles over the WebView bridge.
            item.Payload = null; if (item.Body.Length > 1200) item.Body = item.Body[..1200] + "…"; items.Add(item);
        }
        return new(items, total);
    }
    public async Task<TeamWorkspaceCounts> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct); await using var query = Command(db, null, "SELECT (SELECT COUNT(*) FROM gp_team_items WHERE deleted=0),(SELECT COUNT(*) FROM gp_team_outbox),(SELECT COUNT(*) FROM gp_team_conflicts)");
        await using var reader = await query.ExecuteReaderAsync(ct); await reader.ReadAsync(ct); return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
    public async Task<IReadOnlyList<TeamWorkspaceConflict>> ConflictsAsync(string itemId, CancellationToken ct = default)
    {
        await using var db = await Open(ct); await using var query = Command(db, null, "SELECT id,item_id,reason,recorded_at,current_json,incoming_json FROM gp_team_conflicts WHERE item_id=@id ORDER BY recorded_at DESC LIMIT 20", ("@id", itemId));
        await using var reader = await query.ExecuteReaderAsync(ct); var result = new List<TeamWorkspaceConflict>();
        long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var current = reader.GetString(4); var incoming = reader.GetString(5);
            bytes += System.Text.Encoding.UTF8.GetByteCount(current) + System.Text.Encoding.UTF8.GetByteCount(incoming);
            if (result.Count > 0 && bytes > TeamWorkspaceCodec.MaximumBatchBytes) break;
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), TeamWorkspaceCodec.Read(current), TeamWorkspaceCodec.Read(incoming)));
        }
        return result;
    }
    internal async Task PrepareRemoteAsync(string identity, CancellationToken ct)
    {
        await using var db = await Open(ct); using var tx = db.BeginTransaction();
        await using var current = Command(db, tx, "SELECT value FROM gp_team_meta WHERE name='remote'");
        if ((string?)await current.ExecuteScalarAsync(ct) == identity) return;
        var ids = new List<string>();
        await using (var query = Command(db, tx, "SELECT id FROM gp_team_items"))
        await using (var reader = await query.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        foreach (var id in ids) { ct.ThrowIfCancellationRequested(); var item = (await ReadItem(db, tx, id, ct))!; item.CentralRevision = 0; item.Pending = true; await Put(db, tx, item, ct); await Queue(db, tx, item, ct); }
        await using var save = Command(db, tx, "INSERT INTO gp_team_meta(name,value) VALUES('remote',@remote),('cursor','0') ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("@remote", identity));
        await save.ExecuteNonQueryAsync(ct); tx.Commit();
    }
    internal async Task<IReadOnlyList<TeamWorkspaceMutation>> PendingAsync(int maximum, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var query = Command(db, null, "SELECT mutation_id,base_revision,document_json FROM gp_team_outbox ORDER BY item_id LIMIT @limit", ("@limit", maximum));
        await using var reader = await query.ExecuteReaderAsync(ct); var result = new List<TeamWorkspaceMutation>();
        long bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(2); bytes += System.Text.Encoding.UTF8.GetByteCount(json);
            if (result.Count > 0 && bytes > TeamWorkspaceCodec.MaximumBatchBytes) break;
            result.Add(new(reader.GetString(0), reader.GetInt64(1), TeamWorkspaceCodec.Read(json)));
        }
        return result;
    }
    private static async Task<bool> Conflict(SqliteConnection db, SqliteTransaction tx, TeamWorkspaceItem current, TeamWorkspaceItem incoming, string reason, CancellationToken ct)
    {
        await using var command = Command(db, tx, "INSERT OR IGNORE INTO gp_team_conflicts(id,item_id,reason,recorded_at,current_json,incoming_json) VALUES(@id,@item,@reason,@at,@current,@incoming)",
            ("@id", TeamWorkspaceCodec.ConflictKey(current, incoming)), ("@item", incoming.Id), ("@reason", reason), ("@at", DateTimeOffset.UtcNow.ToString("O")),
            ("@current", TeamWorkspaceCodec.Write(current)), ("@incoming", TeamWorkspaceCodec.Write(incoming))); return await command.ExecuteNonQueryAsync(ct) > 0;
    }
    internal async Task AcknowledgeAsync(TeamWorkspaceMutation mutation, TeamWorkspaceReceipt receipt, CancellationToken ct)
    {
        await using var db = await Open(ct); using var tx = db.BeginTransaction();
        await using var pending = Command(db, tx, "SELECT mutation_id FROM gp_team_outbox WHERE item_id=@id", ("@id", mutation.Item.Id));
        if ((string?)await pending.ExecuteScalarAsync(ct) != mutation.MutationId) return; // A newer local edit keeps its own outbox entry.
        var old = await ReadItem(db, tx, mutation.Item.Id, ct);
        var incoming = receipt.Current.Copy(); incoming.Revision = (old?.Revision ?? 0) + 1; incoming.Pending = false;
        if (receipt.Outcome == "conflict") await Conflict(db, tx, incoming, mutation.Item, "Modification concurrente : les deux versions ont été conservées.", ct);
        await Put(db, tx, incoming, ct);
        await using var clear = Command(db, tx, "DELETE FROM gp_team_outbox WHERE item_id=@id AND mutation_id=@mutation", ("@id", mutation.Item.Id), ("@mutation", mutation.MutationId)); await clear.ExecuteNonQueryAsync(ct); tx.Commit();
    }
    internal async Task<long> CursorAsync(CancellationToken ct)
    {
        await using var db = await Open(ct); await using var query = Command(db, null, "SELECT value FROM gp_team_meta WHERE name='cursor'");
        return long.TryParse((string?)await query.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture, out var cursor) ? cursor : 0;
    }
    internal async Task<bool> ApplyChangeAsync(TeamWorkspaceChange change, CancellationToken ct)
    {
        TeamWorkspaceCodec.Validate(change.Item);
        if (change.Sequence <= 0 || change.Sequence != change.Item.CentralRevision || change.Incoming is { } version && version.Id != change.Item.Id)
            throw new InvalidDataException("Entrée de synchronisation d’équipe incohérente.");
        await using var db = await Open(ct); using var tx = db.BeginTransaction(); var old = await ReadItem(db, tx, change.Item.Id, ct); var conflict = false;
        if (change.Incoming is { } candidate)
        {
            TeamWorkspaceCodec.Validate(candidate);
            conflict = await Conflict(db, tx, change.Item, candidate, "Modification concurrente partagée : les deux versions ont été conservées.", ct);
        }
        if (old is null || old.CentralRevision < change.Item.CentralRevision)
        {
            if (old is { Pending: true } && TeamWorkspaceCodec.ContentHash(old) != TeamWorkspaceCodec.ContentHash(change.Item))
            { conflict = await Conflict(db, tx, change.Item, old, "Une contribution reçue et une modification locale ont divergé ; les deux versions sont conservées.", ct) || conflict; }
            var next = change.Item.Copy(); next.Revision = (old?.Revision ?? 0) + 1; next.Pending = old?.Pending ?? false;
            await Put(db, tx, next, ct);
            // Receiving a feed entry is not an acknowledgement of a local mutation.
            // Keep the exact outbox request until PushAsync has returned its own receipt,
            // including if another writer edited it during synchronization.
        }
        await using var cursor = Command(db, tx, "INSERT INTO gp_team_meta(name,value) VALUES('cursor',@cursor) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("@cursor", change.Sequence.ToString(CultureInfo.InvariantCulture)));
        await cursor.ExecuteNonQueryAsync(ct); tx.Commit(); return conflict;
    }
    public async Task ExportAsync(Stream destination, CancellationToken ct = default)
    {
        await using var db = await Open(ct); await using var command = Command(db, null, "SELECT document_json FROM gp_team_items WHERE deleted=0 ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(ct); await using var writer = new StreamWriter(destination, new System.Text.UTF8Encoding(false), 65536, leaveOpen: true);
        while (await reader.ReadAsync(ct)) { ct.ThrowIfCancellationRequested(); await writer.WriteLineAsync(reader.GetString(0).AsMemory(), ct); } await writer.FlushAsync(ct);
    }
}
