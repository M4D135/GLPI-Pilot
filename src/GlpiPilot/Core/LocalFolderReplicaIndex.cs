using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

/// <summary>Disposable LOCAL projection of the immutable shared-folder journal.
/// Historical payloads and receipts stay on disk; no corpus is cached in managed memory.</summary>
internal sealed partial class LocalFolderReplicaIndex
{
    internal string DatabasePath { get; }
    public LocalFolderReplicaIndex(string sharedFolder, string domain, string? cacheFolder)
    {
        if (domain is not ("team" or "tickets")) throw new InvalidDataException("Collection de cache inconnue.");
        var root = LibraryPreferencesStore.ValidateLocalFolder(cacheFolder ?? Path.Combine(AppPaths.DataRoot, "SharedFolderIndexes"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var shared = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sharedFolder));
        var binding = OperatingSystem.IsWindows() ? shared.ToUpperInvariant() : shared;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))).ToLowerInvariant();
        DatabasePath = Path.Combine(root, "v1-" + key, domain + ".db");
        if (DatabasePath.Equals(shared, comparison) || DatabasePath.StartsWith(shared.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("L’index SQLite doit rester dans un dossier local distinct du dossier partagé.");
    }
    private async Task<Session> OpenAsync(CancellationToken ct)
    {
        LibraryPreferencesStore.ValidateLocalFolder(Path.GetDirectoryName(DatabasePath)!);
        TicketLibraryStore.ValidateDatabasePath(DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        for (var attempt = 0; ; attempt++)
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, DefaultTimeout = 15 }.ConnectionString);
            try
            {
                await db.OpenAsync(ct).ConfigureAwait(false);
                var session = new Session(db, ct);
                await session.InitializeAsync().ConfigureAwait(false);
                return session;
            }
            catch (SqliteException error) when (attempt == 0 && error.SqliteErrorCode is 11 or 26)
            {
                await db.DisposeAsync().ConfigureAwait(false);
                PreserveInvalidDatabase();
            }
            catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
        }
    }
    public async Task<T> RunAsync<T>(SharedFolderWorkspace.Lease lease, Func<Session, Task<T>> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var session = await OpenAsync(lease.Cancellation).ConfigureAwait(false);
            try { await session.ValidateManifestAsync().ConfigureAwait(false); return await operation(session).ConfigureAwait(false); }
            catch (InvalidIndexException) when (attempt == 0)
            { await session.ResetAsync("").ConfigureAwait(false); }
            catch (SqliteException error) when (attempt == 0 && error.SqliteErrorCode is 11 or 26)
            { await session.DisposeAsync().ConfigureAwait(false); PreserveInvalidDatabase(); }
        }
    }
    private void PreserveInvalidDatabase()
    {
        // An unreadable projection is never authoritative. Preserve it for
        // diagnostics, then rebuild only from validated shared transactions.
        var suffix = ".invalid-" + Guid.NewGuid().ToString("N");
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal" })
            if (File.Exists(path)) File.Move(path, path + suffix);
    }
    internal sealed class InvalidIndexException(string message) : IOException(message);
    internal sealed record JournalEntry(long Sequence, long Length, long ModifiedTicks, string Hash);
    internal sealed record StoredReceipt(string RequestHash, string Json);

    internal sealed class Session(SqliteConnection db, CancellationToken cancellation) : IAsyncDisposable
    {
        private SqliteTransaction? _transaction;
        public CancellationToken Cancellation { get; } = cancellation;
        internal SqliteCommand Command(string sql, params (string Name, object? Value)[] values)
        {
            var command = db.CreateCommand(); command.Transaction = _transaction; command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
        internal async Task ExecuteAsync(string sql, params (string Name, object? Value)[] values)
        { await using var command = Command(sql, values); await command.ExecuteNonQueryAsync(Cancellation).ConfigureAwait(false); }
        internal async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] values)
        { await using var command = Command(sql, values); return await command.ExecuteScalarAsync(Cancellation).ConfigureAwait(false); }
        internal async Task InitializeAsync()
        {
            await ExecuteAsync("""
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=15000;
                PRAGMA cache_size=-8192;
                PRAGMA mmap_size=0;
                PRAGMA temp_store=FILE;
                CREATE TABLE IF NOT EXISTS gp_folder_meta(name TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT OR IGNORE INTO gp_folder_meta(name,value) VALUES('schema','1');
                CREATE TABLE IF NOT EXISTS gp_folder_documents(id TEXT PRIMARY KEY,json TEXT NOT NULL,payload_hash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS gp_folder_receipts(id TEXT PRIMARY KEY,request_hash TEXT NOT NULL,json TEXT NOT NULL,payload_hash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS gp_folder_changes(sequence INTEGER PRIMARY KEY,batch_id TEXT NOT NULL,batch_size INTEGER NOT NULL,json TEXT NOT NULL,payload_hash TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS gp_folder_changes_batch ON gp_folder_changes(batch_id,sequence);
                CREATE TABLE IF NOT EXISTS gp_folder_conflicts(id TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS gp_folder_journal(sequence INTEGER PRIMARY KEY,length INTEGER NOT NULL,modified_ticks INTEGER NOT NULL,hash TEXT NOT NULL);
                """).ConfigureAwait(false);
            if ((string?)await ScalarAsync("SELECT value FROM gp_folder_meta WHERE name='schema'").ConfigureAwait(false) != "1")
                throw new InvalidDataException("La version de l’index local partagé n’est pas prise en charge.");
        }
        public IDisposable Begin()
        {
            if (_transaction is not null) throw new InvalidOperationException("Transaction locale imbriquée.");
            _transaction = db.BeginTransaction(); return new Transaction(this);
        }
        public void Commit() { Cancellation.ThrowIfCancellationRequested(); _transaction!.Commit(); }
        private sealed class Transaction(Session session) : IDisposable
        { public void Dispose() { session._transaction!.Dispose(); session._transaction = null; } }
        public async Task ResetAsync(string identity)
        {
            using var transaction = Begin();
            await ExecuteAsync("DELETE FROM gp_folder_documents; DELETE FROM gp_folder_receipts; DELETE FROM gp_folder_changes; DELETE FROM gp_folder_conflicts; DELETE FROM gp_folder_journal;").ConfigureAwait(false);
            await ExecuteAsync("DELETE FROM gp_folder_meta WHERE name='manifest'").ConfigureAwait(false);
            await ExecuteAsync("INSERT INTO gp_folder_meta(name,value) VALUES('identity',@identity) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("@identity", identity)).ConfigureAwait(false);
            Commit();
        }
        public async Task EnsureIdentityAsync(string identity)
        {
            if ((string?)await ScalarAsync("SELECT value FROM gp_folder_meta WHERE name='identity'").ConfigureAwait(false) != identity)
                await ResetAsync(identity).ConfigureAwait(false);
        }
        public async Task<IReadOnlyList<JournalEntry>> JournalAsync()
        {
            var entries = new List<JournalEntry>();
            await using var command = Command("SELECT sequence,length,modified_ticks,hash FROM gp_folder_journal ORDER BY sequence");
            await using var reader = await command.ExecuteReaderAsync(Cancellation).ConfigureAwait(false);
            while (await reader.ReadAsync(Cancellation).ConfigureAwait(false))
            {
                var entry = new JournalEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
                if (entries.Count >= 100000 || entry.Sequence != entries.Count + 1 || entry.Length is < 1 or > SharedFolderWorkspace.MaximumEnvelopeBytes || entry.Hash.Length != 64)
                    throw new InvalidIndexException("L’index local du journal doit être reconstruit.");
                entries.Add(entry);
            }
            return entries;
        }
        public async Task<JournalEntry?> LastJournalAsync()
        {
            await using var command = Command("SELECT sequence,length,modified_ticks,hash FROM gp_folder_journal ORDER BY sequence DESC LIMIT 1");
            await using var reader = await command.ExecuteReaderAsync(Cancellation).ConfigureAwait(false);
            return await reader.ReadAsync(Cancellation).ConfigureAwait(false) ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)) : null;
        }
        public async Task RecordJournalAsync(JournalEntry entry)
        {
            await ExecuteAsync("INSERT INTO gp_folder_journal(sequence,length,modified_ticks,hash) VALUES(@sequence,@length,@modified,@hash)",
                ("@sequence", entry.Sequence), ("@length", entry.Length), ("@modified", entry.ModifiedTicks), ("@hash", entry.Hash)).ConfigureAwait(false);
            await ExecuteAsync("INSERT INTO gp_folder_meta(name,value) VALUES('manifest',@value) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
                ("@value", await ManifestAsync().ConfigureAwait(false))).ConfigureAwait(false);
        }
        private async Task<string> ManifestAsync() => (string)(await ScalarAsync("""
            SELECT (SELECT COUNT(*) FROM gp_folder_documents)||':'||(SELECT COUNT(*) FROM gp_folder_receipts)||':'||
              (SELECT COUNT(*) FROM gp_folder_changes)||':'||(SELECT COUNT(*) FROM gp_folder_conflicts)||':'||
              (SELECT COUNT(*) FROM gp_folder_journal)||':'||(SELECT COALESCE(MAX(sequence),0) FROM gp_folder_changes)
            """).ConfigureAwait(false))!;
        internal async Task ValidateManifestAsync()
        {
            var saved = (string?)await ScalarAsync("SELECT value FROM gp_folder_meta WHERE name='manifest'").ConfigureAwait(false);
            if ((saved ?? "0:0:0:0:0:0") != await ManifestAsync().ConfigureAwait(false))
                throw new InvalidIndexException("Des lignes de l’index local sont absentes ; le journal partagé sera relu.");
        }
        internal static string VerifyPayload(string json, string hash)
        {
            if (Encoding.UTF8.GetByteCount(json) > SharedFolderWorkspace.MaximumEnvelopeBytes || TextHash(json) != hash)
                throw new InvalidIndexException("Un contenu de l’index local doit être reconstruit depuis le journal partagé.");
            return json;
        }
        internal static string TextHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        internal async Task<string?> ReadDocumentAsync(string id, long maximumBytes = SharedFolderWorkspace.MaximumEnvelopeBytes)
        {
            await using var command = Command("SELECT json,payload_hash,length(CAST(json AS BLOB)) FROM gp_folder_documents WHERE id=@id", ("@id", id));
            await using var reader = await command.ExecuteReaderAsync(Cancellation).ConfigureAwait(false);
            if (!await reader.ReadAsync(Cancellation).ConfigureAwait(false)) return null;
            if (reader.GetInt64(2) > maximumBytes) throw new InvalidDataException("Le lot courant dépasse la limite de mémoire autorisée. Réduisez le nombre de documents modifiés ensemble.");
            return VerifyPayload(reader.GetString(0), reader.GetString(1));
        }
        internal Task PutDocumentAsync(string id, string json) => ExecuteAsync("INSERT INTO gp_folder_documents(id,json,payload_hash) VALUES(@id,@json,@hash) ON CONFLICT(id) DO UPDATE SET json=excluded.json,payload_hash=excluded.payload_hash",
            ("@id", id), ("@json", json), ("@hash", TextHash(json)));
        internal async Task<StoredReceipt?> ReceiptAsync(string id)
        {
            await using var command = Command("SELECT request_hash,json,payload_hash,length(CAST(json AS BLOB)) FROM gp_folder_receipts WHERE id=@id", ("@id", id));
            await using var reader = await command.ExecuteReaderAsync(Cancellation).ConfigureAwait(false);
            if (!await reader.ReadAsync(Cancellation).ConfigureAwait(false)) return null;
            if (reader.GetInt64(3) > SharedFolderWorkspace.MaximumEnvelopeBytes) throw new InvalidIndexException("Reçu local trop volumineux.");
            return new(reader.GetString(0), VerifyPayload(reader.GetString(1), reader.GetString(2)));
        }
        internal Task SaveReceiptAsync<T>(string id, string requestHash, T value)
        {
            var json = Encoding.UTF8.GetString(SharedFolderWorkspace.SerializeBounded(value));
            return ExecuteAsync("INSERT INTO gp_folder_receipts(id,request_hash,json,payload_hash) VALUES(@id,@request,@json,@hash)",
                ("@id", id), ("@request", requestHash), ("@json", json), ("@hash", TextHash(json)));
        }
        internal async Task<long> NextRevisionAsync() => Convert.ToInt64(await ScalarAsync("SELECT COALESCE(MAX(sequence),0)+1 FROM gp_folder_changes").ConfigureAwait(false), CultureInfo.InvariantCulture);
        internal Task SaveChangeAsync<T>(long sequence, T value, string batchId = "", int batchSize = 0)
        {
            var json = Encoding.UTF8.GetString(SharedFolderWorkspace.SerializeBounded(value));
            return ExecuteAsync("INSERT INTO gp_folder_changes(sequence,batch_id,batch_size,json,payload_hash) VALUES(@sequence,@batch,@size,@json,@hash)",
                ("@sequence", sequence), ("@batch", batchId), ("@size", batchSize), ("@json", json), ("@hash", TextHash(json)));
        }
        internal async Task<bool> RememberConflictAsync(string id)
        {
            await using var command = Command("INSERT OR IGNORE INTO gp_folder_conflicts(id) VALUES(@id)", ("@id", id));
            return await command.ExecuteNonQueryAsync(Cancellation).ConfigureAwait(false) > 0;
        }
        internal static T Deserialize<T>(string json)
        {
            try { return JsonSerializer.Deserialize<T>(json, SharedFolderWorkspace.Json) ?? throw new InvalidIndexException("Document local absent."); }
            catch (JsonException) { throw new InvalidIndexException("Document local illisible ; reconstruction nécessaire."); }
        }
        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
