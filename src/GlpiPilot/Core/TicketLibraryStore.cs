using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

/// <summary>Short local transactions only. Other modules may use the same file with their own table prefix.</summary>
public sealed partial class TicketLibraryStore
{
    public const int SchemaVersion = 1;
    public string DatabasePath { get; }
    public string ConnectionString { get; }
    private readonly object _initialization = new();
    private bool _initialized;

    public TicketLibraryStore(string path)
    {
        DatabasePath = ValidateDatabasePath(path);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10 }.ToString();
    }

    public static string ValidateDatabasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal) || path.IndexOf('\0') >= 0)
            throw new InvalidDataException("La base SQLite doit être un fichier local. Utilisez PostgreSQL pour le partage entre postes.");
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Une base SQLite active ne peut pas être placée sur un lecteur réseau.");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Une base SQLite liée n’est pas autorisée.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(full)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Le chemin de la base SQLite doit rester dans des dossiers locaux sans redirection.");
        return full;
    }

    // The public SQL protocol is separate from PRAGMA user_version so independent team tables can coexist.
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS ticket_library_schema (singleton INTEGER PRIMARY KEY CHECK(singleton=1), version INTEGER NOT NULL);
        INSERT OR IGNORE INTO ticket_library_schema VALUES (1,1);
        CREATE TABLE IF NOT EXISTS ticket_library_tickets (
          ticket_key TEXT PRIMARY KEY, installation TEXT NOT NULL, title TEXT NOT NULL, status_code INTEGER,
          document_json TEXT NOT NULL, seed_json TEXT NOT NULL, content_hash TEXT NOT NULL, local_revision INTEGER NOT NULL,
          central_revision INTEGER NOT NULL DEFAULT 0, detail_complete INTEGER NOT NULL, details_current INTEGER NOT NULL, has_content INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ticket_library_installation ON ticket_library_tickets(installation,status_code,ticket_key);
        CREATE INDEX IF NOT EXISTS ticket_library_content ON ticket_library_tickets(has_content);
        CREATE TABLE IF NOT EXISTS ticket_library_history (
          history_id INTEGER PRIMARY KEY, ticket_key TEXT NOT NULL, document_json TEXT NOT NULL, reason TEXT NOT NULL, recorded_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ticket_library_conflicts (
          conflict_id TEXT PRIMARY KEY, ticket_key TEXT NOT NULL, reason TEXT NOT NULL, recorded_at TEXT NOT NULL,
          current_json TEXT NOT NULL, incoming_json TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ticket_library_conflict_key ON ticket_library_conflicts(ticket_key);
        CREATE TABLE IF NOT EXISTS ticket_library_outbox (
          mutation_id TEXT PRIMARY KEY, ticket_key TEXT NOT NULL, document_json TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ticket_library_outbox_key ON ticket_library_outbox(ticket_key);
        CREATE TABLE IF NOT EXISTS ticket_library_sync (
          central_id TEXT PRIMARY KEY, cursor INTEGER NOT NULL DEFAULT 0, last_sync_at TEXT);
        CREATE TABLE IF NOT EXISTS ticket_library_sync_active (singleton INTEGER PRIMARY KEY CHECK(singleton=1),central_id TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ticket_library_chunks (
          chunk_id INTEGER PRIMARY KEY, ticket_key TEXT NOT NULL REFERENCES ticket_library_tickets(ticket_key) ON DELETE CASCADE,
          citation_json TEXT NOT NULL, body TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ticket_library_chunk_key ON ticket_library_chunks(ticket_key);
        CREATE VIRTUAL TABLE IF NOT EXISTS ticket_library_fts USING fts5(body, content='ticket_library_chunks', content_rowid='chunk_id', tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER IF NOT EXISTS ticket_library_chunks_ai AFTER INSERT ON ticket_library_chunks BEGIN
          INSERT INTO ticket_library_fts(rowid,body) VALUES(new.chunk_id,new.body); END;
        CREATE TRIGGER IF NOT EXISTS ticket_library_chunks_ad AFTER DELETE ON ticket_library_chunks BEGIN
          INSERT INTO ticket_library_fts(ticket_library_fts,rowid,body) VALUES('delete',old.chunk_id,old.body); END;
        """;

    public Task InitializeAsync(CancellationToken ct = default) => Run(connection => true, ct);

    public Task<TicketLibraryDocument?> GetAsync(string key, CancellationToken ct = default) => Run(c => Read(c, null, key), ct);
    public Task<TicketLibraryDocument?> GetAsync(TicketReference ticket, CancellationToken ct = default) => GetAsync(TicketLibraryRules.Key(ticket), ct);

    public Task<TicketLibraryWriteResult> ObserveSeedAsync(TicketLibrarySeed seed, CancellationToken ct = default)
    {
        var candidate = TicketLibraryRules.FromSeed(seed);
        return Run(c => Save(c, candidate, null, ct), ct);
    }

    public Task<TicketLibraryWriteResult> SaveDetailAsync(TicketLibrarySeed seed, TicketDetailResult detail, TicketLibraryCoverage coverage,
        string readerSignature, long? expectedLocalRevision = null, CancellationToken ct = default)
    {
        var candidate = TicketLibraryRules.FromDetail(seed, detail, coverage, readerSignature);
        return Run(c => Save(c, candidate, expectedLocalRevision, ct), ct);
    }

    public static bool CanSkip(TicketLibrarySeed seed, TicketLibraryDocument? stored, string readerSignature, TicketLibraryCachePolicy? policy = null)
        => TicketLibraryRules.CanSkip(seed, stored, readerSignature, policy);

    public Task<TicketLibraryState> GetStateAsync(CancellationToken ct = default) => Run(c =>
    {
        var state = new TicketLibraryState
        {
            TicketCount = ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_tickets"),
            ResolvedCount = ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_tickets WHERE status_code IN (5,6)"),
            ContentCount = ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_tickets WHERE has_content=1"),
            PendingCount = ScalarLong(c, null, "SELECT COUNT(DISTINCT ticket_key) FROM ticket_library_outbox"),
            ConflictCount = ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_conflicts")
        };
        using var command = Command(c, null, "SELECT MAX(last_sync_at) FROM ticket_library_sync");
        if (command.ExecuteScalar() is string date && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) state.LastSyncAt = parsed;
        return state;
    }, ct);

    public Task<IReadOnlyList<TicketLibrarySeed>> ReadSeedsAsync(string installation = "", CancellationToken ct = default) => Run<IReadOnlyList<TicketLibrarySeed>>(c =>
    {
        var list = new List<TicketLibrarySeed>();
        using var command = Command(c, null, "SELECT seed_json FROM ticket_library_tickets WHERE ($installation='' OR installation=$installation) ORDER BY ticket_key LIMIT 100001", ("$installation", installation));
        using var reader = command.ExecuteReader();
        while (reader.Read()) { ct.ThrowIfCancellationRequested(); list.Add(TicketLibraryRules.ValidateSeed(System.Text.Json.JsonSerializer.Deserialize<TicketLibrarySeed>(reader.GetString(0), TicketLibraryRules.Json)!)); }
        if (list.Count > 100000) throw new InvalidDataException("Le relevé de bibliothèque dépasse 100 000 tickets. Choisissez une installation.");
        return list;
    }, ct);

    public Task<IReadOnlyList<string>> ReadSourcesAsync(CancellationToken ct = default) => Run<IReadOnlyList<string>>(c =>
    {
        var sources = new List<string>();
        using var command = Command(c, null, "SELECT DISTINCT installation FROM ticket_library_tickets ORDER BY installation");
        using var reader = command.ExecuteReader();
        while (reader.Read()) { ct.ThrowIfCancellationRequested(); sources.Add(reader.GetString(0)); }
        return sources;
    }, ct);

    public Task<IReadOnlyList<TicketLibraryConflict>> GetConflictsAsync(string key = "", int limit = 50, CancellationToken ct = default) => Run<IReadOnlyList<TicketLibraryConflict>>(c =>
    {
        var list = new List<TicketLibraryConflict>();
        using var command = Command(c, null, "SELECT conflict_id,ticket_key,reason,recorded_at,current_json,incoming_json FROM ticket_library_conflicts WHERE ($key='' OR ticket_key=$key) ORDER BY recorded_at DESC,conflict_id LIMIT $limit", ("$key", key), ("$limit", Math.Clamp(limit, 1, 100)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new() { Id = reader.GetString(0), TicketKey = reader.GetString(1), Reason = reader.GetString(2), RecordedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), Current = TicketLibraryRules.Deserialize(reader.GetString(4)), Incoming = TicketLibraryRules.Deserialize(reader.GetString(5)) });
        }
        return list;
    }, ct);

    public Task<TicketLibrarySearchResult> SearchAsync(TicketLibrarySearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = TicketLibraryRules.Copy(query); q.Limit = Math.Clamp(q.Limit, 1, TicketLibrarySearch.MaximumPageSize); q.Offset = Math.Clamp(q.Offset, 0, 1000000);
        var exactId = TicketLibrarySearch.NumericIdentifier(q.Text ?? "");
        var match = TicketLibrarySearch.MatchQuery(exactId.Length > 0 ? exactId : q.Text ?? "");
        return Run(c =>
        {
            var result = new TicketLibrarySearchResult { Offset = q.Offset, Limit = q.Limit };
            if (!string.IsNullOrWhiteSpace(q.Text) && match.Length == 0) return result;
            var where = " WHERE ($installation='' OR t.installation=$installation) AND ($resolved=0 OR (t.status_code IN (5,6) AND t.details_current=1 AND t.detail_complete=1 AND json_extract(t.document_json,'$.confirmedContentHash')=t.content_hash AND length(json_extract(t.document_json,'$.confirmationId'))=32))";
            // Keys contain the validated canonical installation and decimal ticket ID.
            // Keep the source/resolution filters outside this alternative and rank before LIMIT.
            const string exact = "($exactId<>'' AND t.ticket_key LIKE '%:ticket:' || $exactId)";
            if (match.Length > 0) where += " AND (t.ticket_key IN (SELECT c.ticket_key FROM ticket_library_fts JOIN ticket_library_chunks c ON c.chunk_id=ticket_library_fts.rowid WHERE ticket_library_fts MATCH $match) OR " + exact + ")";
            (string, object?)[] parameters = [("$installation", q.Installation ?? ""), ("$resolved", q.ResolvedOnly ? 1 : 0), ("$match", match), ("$exactId", exactId), ("$offset", q.Offset), ("$limit", q.Limit)];
            result.Total = ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_tickets t" + where, parameters);
            var keys = new List<string>();
            var ranked = match.Length == 0 ? "" : "WITH hits AS MATERIALIZED (SELECT c.ticket_key,bm25(ticket_library_fts) AS score FROM ticket_library_fts JOIN ticket_library_chunks c ON c.chunk_id=ticket_library_fts.rowid WHERE ticket_library_fts MATCH $match) ";
            var order = match.Length == 0 ? "t.title COLLATE NOCASE,t.ticket_key" : "CASE WHEN " + exact + " THEN 0 ELSE 1 END,(SELECT MIN(h.score) FROM hits h WHERE h.ticket_key=t.ticket_key),t.title COLLATE NOCASE,t.ticket_key";
            using (var command = Command(c, null, ranked + "SELECT t.ticket_key FROM ticket_library_tickets t" + where + " ORDER BY " + order + " LIMIT $limit OFFSET $offset", parameters))
            using (var reader = command.ExecuteReader()) while (reader.Read()) keys.Add(reader.GetString(0));
            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested(); var d = Read(c, null, key)!;
                var row = new TicketLibrarySearchRow { Key = d.Key, Ticket = d.Seed.Ticket, Installation = d.Installation, Title = d.Seed.Title, Status = d.Seed.Status, StatusCode = d.Seed.StatusCode, Category = d.Seed.Category, LastModifiedRaw = d.Seed.LastModifiedRaw, LastModifiedAt = d.Seed.LastModifiedAt, ReadAt = d.ReadAt, DetailComplete = d.DetailComplete, DetailsCurrent = d.DetailsCurrent, Pending = d.Pending, Conflicts = d.Conflicts };
                var sql = match.Length == 0
                    ? "SELECT citation_json,0.0 FROM ticket_library_chunks WHERE ticket_key=$key ORDER BY json_extract(citation_json,'$.isSolution') DESC,chunk_id LIMIT 4"
                    : "SELECT c.citation_json,bm25(ticket_library_fts) FROM ticket_library_fts JOIN ticket_library_chunks c ON c.chunk_id=ticket_library_fts.rowid WHERE ticket_library_fts MATCH $match AND c.ticket_key=$key ORDER BY bm25(ticket_library_fts),c.chunk_id LIMIT 4";
                using var citations = Command(c, null, sql, ("$key", key), ("$match", match)); using var reader = citations.ExecuteReader();
                while (reader.Read())
                {
                    var citation = System.Text.Json.JsonSerializer.Deserialize<TicketLibraryCitation>(reader.GetString(0), TicketLibraryRules.Json)!;
                    citation.Current = d.DetailsCurrent; citation.IsSolution &= d.DetailsCurrent && d.DetailComplete;
                    row.Citations.Add(citation); row.Score = Math.Max(row.Score, Math.Clamp(-reader.GetDouble(1), 0, 1000000));
                }
                result.Rows.Add(row);
            }
            return result;
        }, ct);
    }

    public Task ExportAsync(string destination, CancellationToken ct = default) => Run(c =>
    {
        var full = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal" }.Any(active => string.Equals(full, active, comparison)))
            throw new InvalidDataException("L’export ne peut pas remplacer la base active ni ses journaux.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var snapshot = c.BeginTransaction(deferred: true);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(TicketLibraryRules.Serialize(new { recordType = "manifest", protocol = "glpi-pilot-ticket-library", format = SchemaVersion, exportedAt = DateTimeOffset.UtcNow, records = new[] { "ticket", "chunk" } }));
                using (var command = Command(c, snapshot, "SELECT document_json FROM ticket_library_tickets ORDER BY ticket_key"))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested(); var document = TicketLibraryRules.Deserialize(reader.GetString(0));
                        writer.WriteLine(TicketLibraryRules.Serialize(new { recordType = "ticket", format = SchemaVersion, document }));
                    }
                using (var command = Command(c, snapshot, "SELECT c.citation_json,t.document_json FROM ticket_library_chunks c JOIN ticket_library_tickets t ON t.ticket_key=c.ticket_key ORDER BY c.ticket_key,c.chunk_id"))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested(); var citation = System.Text.Json.JsonSerializer.Deserialize<TicketLibraryCitation>(reader.GetString(0), TicketLibraryRules.Json)!;
                        var owner = TicketLibraryRules.Deserialize(reader.GetString(1));
                        citation.Current = owner.DetailsCurrent; citation.IsSolution &= owner.DetailsCurrent && owner.DetailComplete;
                        writer.WriteLine(TicketLibraryRules.Serialize(new
                        {
                            recordType = "chunk", format = SchemaVersion, citation.ChunkKey, ticketKey = citation.Key, text = citation.Excerpt,
                            citation = new { citation.Ticket, citation.Section, citation.SectionKey, citation.Row, citation.Url, citation.ObservedAt, citation.IsSolution, citation.Current, citation.ContentHash }
                        }));
                    }
            }
            ct.ThrowIfCancellationRequested(); snapshot.Commit(); File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }, ct);

    private TicketLibraryWriteResult Save(SqliteConnection c, TicketLibraryDocument candidate, long? expectedRevision, CancellationToken ct)
    {
        using var tx = c.BeginTransaction();
        var result = SaveCore(c, tx, candidate, expectedRevision, ct);
        ct.ThrowIfCancellationRequested(); tx.Commit();
        return result;
    }

    // Capture adds its local confirmation proof and diagnostic in this same short transaction.
    private TicketLibraryWriteResult SaveCore(SqliteConnection c, SqliteTransaction tx, TicketLibraryDocument candidate,
        long? expectedRevision, CancellationToken ct, bool queueChanges = true)
    {
        var current = Read(c, tx, candidate.Key);
        if (expectedRevision.HasValue && (current?.LocalRevision ?? 0) != expectedRevision.Value)
            throw new InvalidOperationException("La fiche locale a changé pendant sa lecture. Relisez son état avant de l’enregistrer.");
        var merge = TicketLibraryRules.Merge(current, candidate);
        var conflictCreated = merge.Conflict && current is not null && AddConflict(c, tx, current, candidate, merge.Outcome);
        var changed = current is null || current.ContentHash != merge.Document.ContentHash || current.DetailsCurrent != merge.Document.DetailsCurrent
            || current.ConfirmationPending != merge.Document.ConfirmationPending || current.ConfirmedContentHash != merge.Document.ConfirmedContentHash || current.ConfirmationId != merge.Document.ConfirmationId
            || current.ReadAt != merge.Document.ReadAt || current.ReaderSignature != merge.Document.ReaderSignature;
        if (current is not null && changed) AddHistory(c, tx, current, merge.Outcome);
        merge.Document.LocalRevision = (current?.LocalRevision ?? 0) + 1;
        merge.Document.CentralRevision = current?.CentralRevision ?? 0;
        Write(c, tx, merge.Document, ct, rebuildIndex: changed);
        // Conflicting candidate content is itself pushed so that both versions reach the central history.
        if (queueChanges && (changed || conflictCreated)) Queue(c, tx, merge.Conflict ? candidate : merge.Document);
        ct.ThrowIfCancellationRequested();
        return new(Read(c, tx, candidate.Key)!, merge.Outcome, conflictCreated);
    }

    internal Task<T> Run<T>(Func<SqliteConnection, T> operation, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); EnsureInitialized(); ct.ThrowIfCancellationRequested();
        ValidateDatabasePath(DatabasePath);
        using var c = new SqliteConnection(ConnectionString); c.Open();
        Execute(c, null, "PRAGMA foreign_keys=ON;");
        return operation(c);
    }, ct);

    private void EnsureInitialized()
    {
        lock (_initialization)
        {
            if (_initialized) return;
            ValidateDatabasePath(DatabasePath); Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            using var c = new SqliteConnection(ConnectionString); c.Open();
            using (var check = Command(c, null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ticket_library_schema'"))
                if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0 && ScalarLong(c, null, "SELECT version FROM ticket_library_schema WHERE singleton=1") != SchemaVersion)
                    throw new InvalidDataException("Version du schéma de bibliothèque non prise en charge. La base est conservée.");
            using var tx = c.BeginTransaction(); Execute(c, tx, SchemaSql); Execute(c, tx, CaptureSchemaSql); tx.Commit();
            _initialized = true;
        }
    }

    internal static TicketLibraryDocument? Read(SqliteConnection c, SqliteTransaction? tx, string key)
    {
        using var command = Command(c, tx, "SELECT document_json,local_revision,central_revision,EXISTS(SELECT 1 FROM ticket_library_outbox o WHERE o.ticket_key=t.ticket_key),(SELECT COUNT(*) FROM ticket_library_conflicts x WHERE x.ticket_key=t.ticket_key) FROM ticket_library_tickets t WHERE ticket_key=$key", ("$key", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var document = TicketLibraryRules.Deserialize(reader.GetString(0));
        document.LocalRevision = reader.GetInt64(1); document.CentralRevision = reader.GetInt64(2); document.Pending = reader.GetBoolean(3); document.Conflicts = reader.GetInt32(4);
        return document;
    }

    internal static void Write(SqliteConnection c, SqliteTransaction tx, TicketLibraryDocument document, CancellationToken ct, bool rebuildIndex = true)
    {
        TicketLibraryRules.Validate(document);
        Execute(c, tx, """
            INSERT INTO ticket_library_tickets(ticket_key,installation,title,status_code,document_json,seed_json,content_hash,local_revision,central_revision,detail_complete,details_current,has_content)
            VALUES($key,$installation,$title,$status,$json,$seed,$hash,$local,$central,$complete,$current,$hasContent)
            ON CONFLICT(ticket_key) DO UPDATE SET installation=excluded.installation,title=excluded.title,status_code=excluded.status_code,
            document_json=excluded.document_json,seed_json=excluded.seed_json,content_hash=excluded.content_hash,local_revision=excluded.local_revision,
            central_revision=excluded.central_revision,detail_complete=excluded.detail_complete,details_current=excluded.details_current,has_content=excluded.has_content;
            """, ("$key", document.Key), ("$installation", document.Installation), ("$title", document.Seed.Title), ("$status", document.Seed.StatusCode),
            ("$json", TicketLibraryRules.Serialize(document)), ("$seed", TicketLibraryRules.Serialize(document.Seed)), ("$hash", document.ContentHash), ("$local", document.LocalRevision), ("$central", document.CentralRevision),
            ("$complete", document.DetailComplete ? 1 : 0), ("$current", document.DetailsCurrent ? 1 : 0), ("$hasContent", document.Detail is not null ? 1 : 0));
        if (!rebuildIndex) return;
        Execute(c, tx, "DELETE FROM ticket_library_chunks WHERE ticket_key=$key", ("$key", document.Key));
        using var chunk = Command(c, tx, "INSERT INTO ticket_library_chunks(ticket_key,citation_json,body) VALUES($key,$json,$body)", ("$key", document.Key), ("$json", ""), ("$body", ""));
        foreach (var citation in TicketLibrarySearch.Chunks(document))
        {
            ct.ThrowIfCancellationRequested(); chunk.Parameters["$json"].Value = TicketLibraryRules.Serialize(citation); chunk.Parameters["$body"].Value = citation.Excerpt; chunk.ExecuteNonQuery();
        }
    }

    internal static string ConflictId(TicketLibraryDocument current, TicketLibraryDocument incoming, string reason)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current.Key + "\n" + reason + "\n" + current.ContentHash + "\n" + incoming.ContentHash))).ToLowerInvariant();
    internal static bool AddConflict(SqliteConnection c, SqliteTransaction tx, TicketLibraryDocument current, TicketLibraryDocument incoming, string reason)
        => Execute(c, tx, "INSERT OR IGNORE INTO ticket_library_conflicts VALUES($id,$key,$reason,$date,$current,$incoming)", ("$id", ConflictId(current, incoming, reason)), ("$key", current.Key), ("$reason", reason), ("$date", DateTimeOffset.UtcNow.ToString("O")), ("$current", TicketLibraryRules.Serialize(current)), ("$incoming", TicketLibraryRules.Serialize(incoming))) > 0;
    internal static void AddHistory(SqliteConnection c, SqliteTransaction tx, TicketLibraryDocument document, string reason)
        => Execute(c, tx, "INSERT INTO ticket_library_history(ticket_key,document_json,reason,recorded_at) VALUES($key,$json,$reason,$date)", ("$key", document.Key), ("$json", TicketLibraryRules.Serialize(document)), ("$reason", reason), ("$date", DateTimeOffset.UtcNow.ToString("O")));
    internal static void Queue(SqliteConnection c, SqliteTransaction tx, TicketLibraryDocument document)
        => Execute(c, tx, "INSERT INTO ticket_library_outbox VALUES($id,$key,$json,$date)", ("$id", Guid.NewGuid().ToString("N")), ("$key", document.Key), ("$json", TicketLibraryRules.Serialize(document)), ("$date", DateTimeOffset.UtcNow.ToString("O")));

    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.CommandTimeout = 10;
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        return command;
    }
    internal static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(c, tx, sql, parameters); return command.ExecuteNonQuery(); }
    internal static long ScalarLong(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(c, tx, sql, parameters); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
}
