using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed record LocalDatabaseResetPreview(string Fingerprint, long Tickets, long TeamItems, long PendingTickets, long PendingTeam, bool IncludeTeam);
public sealed record LocalDatabaseResetResult(string BackupPath, string BackupSha256, bool IncludeTeam);

/// <summary>Only the selected SQLite data is cleared; a verified coherent backup always precedes deletion.</summary>
public static class LocalDatabaseMaintenance
{
    private static string Connection(string path) => new SqliteConnectionStringBuilder { DataSource = TicketLibraryStore.ValidateDatabasePath(path), Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 15 }.ToString();
    private static async Task<string> Fingerprint(SqliteConnection db, SqliteTransaction transaction, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value) { var bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
        var tables = new List<string>();
        await using (var query = db.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (name.StartsWith("gp_team_", StringComparison.Ordinal) || name.StartsWith("ticket_library_", StringComparison.Ordinal) && !name.StartsWith("ticket_library_fts", StringComparison.Ordinal)) tables.Add(name);
            }
        }
        foreach (var table in tables)
        {
            Append(table); await using var query = db.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\" ORDER BY rowid";
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column); Append(value.GetType().Name);
                    Append(value is byte[] bytes ? Convert.ToBase64String(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static async Task<LocalDatabaseResetPreview> PreviewAsync(string path, bool includeTeam, CancellationToken ct = default)
    {
        AdministrationCapabilities.Require();
        await new TicketLibraryStore(path).InitializeAsync(ct); await new TeamWorkspaceStore(path).InitializeAsync(ct);
        await using var db = new SqliteConnection(Connection(path)); await db.OpenAsync(ct);
        using var transaction = db.BeginTransaction(deferred: true);
        async Task<long> Count(string table) { await using var cmd = db.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = "SELECT COUNT(*) FROM " + table; return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
        var tickets = await Count("ticket_library_tickets"); var team = await Count("gp_team_items");
        var pending = await Count("ticket_library_outbox"); var pendingTeam = await Count("gp_team_outbox");
        return new(await Fingerprint(db, transaction, ct), tickets, team, pending, pendingTeam, includeTeam);
    }
    public static async Task<LocalDatabaseResetResult> ResetAsync(string path, LocalDatabaseResetPreview expected, CancellationToken ct = default)
    {
        AdministrationCapabilities.Require(); path = TicketLibraryStore.ValidateDatabasePath(path);
        await using var db = new SqliteConnection(Connection(path)); await db.OpenAsync(ct);
        using var transaction = db.BeginTransaction(deferred: false);
        if (await Fingerprint(db, transaction, ct) != expected.Fingerprint) throw new InvalidOperationException("La base locale a changé depuis l’aperçu. Actualisez le bilan avant de confirmer.");
        var folder = Path.Combine(Path.GetDirectoryName(path)!, "Backups"); Directory.CreateDirectory(folder);
        var backup = TicketLibraryStore.ValidateDatabasePath(Path.Combine(folder, "GLPI-Pilot-avant-reset-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".sqlite"));
        // Read through a separate connection while the write reservation prevents
        // another process from changing the snapshot before the clearing commit.
        await using (var source = new SqliteConnection(Connection(path)))
        await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await source.OpenAsync(ct); await target.OpenAsync(ct); ct.ThrowIfCancellationRequested(); source.BackupDatabase(target);
            await using var integrity = target.CreateCommand(); integrity.CommandText = "PRAGMA integrity_check";
            if ((string?)await integrity.ExecuteScalarAsync(ct) != "ok") throw new InvalidDataException("La sauvegarde locale n’a pas passé le contrôle d’intégrité. Aucune donnée n’a été effacée.");
        }
        var backupHash = UpdateService.HashFile(backup);
        var tables = new List<string>();
        await using (var query = db.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (name.StartsWith("ticket_library_", StringComparison.Ordinal) && name != "ticket_library_schema" && !name.StartsWith("ticket_library_fts", StringComparison.Ordinal)
                    || expected.IncludeTeam && name.StartsWith("gp_team_", StringComparison.Ordinal) && name != "gp_team_meta") tables.Add(name);
            }
        }
        // FTS delete triggers run before the parent tickets disappear.
        tables.Remove("ticket_library_chunks"); tables.Insert(0, "ticket_library_chunks");
        foreach (var name in tables)
        {
            ct.ThrowIfCancellationRequested(); await using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM \"" + name.Replace("\"", "\"\"") + "\""; await command.ExecuteNonQueryAsync(ct);
        }
        if (expected.IncludeTeam)
        {
            await using var metadata = db.CreateCommand(); metadata.Transaction = transaction;
            metadata.CommandText = "DELETE FROM gp_team_meta WHERE name<>'schema'"; await metadata.ExecuteNonQueryAsync(ct);
        }
        ct.ThrowIfCancellationRequested(); transaction.Commit(); return new(backup, backupHash, expected.IncludeTeam);
    }
}
