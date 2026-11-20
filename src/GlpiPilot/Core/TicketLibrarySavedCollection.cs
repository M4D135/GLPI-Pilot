using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

/// <summary>A stored run selected by its installation, not by today's browser or date preferences.</summary>
public sealed record TicketLibrarySavedCollection(TicketLibraryCaptureContext Context,
    LibraryCollectionState State, TicketLibraryCaptureReport Report);

public sealed partial class TicketLibraryStore
{
    /// <summary>Consultation only: never initializes a schema, recovers a run or changes its proof.</summary>
    public Task<TicketLibrarySavedCollection?> ReadSavedCollectionAsync(string installation, string runId = "", CancellationToken ct = default)
    {
        if (installation.Length is 0 or > 3000) throw new InvalidDataException("Installation de collecte inconnue.");
        if (runId.Length > 0) ValidateRunId(runId);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); ValidateDatabasePath(DatabasePath);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            c.Open();
            if (!HasReadableCacheSchema(c)) return null;
            using var tx = c.BeginTransaction(deferred: true);
            if (!HasSavedCollectionTable(c, tx, "ticket_library_capture_runs")) return null;
            var selected = ReadSavedCollectionIdentity(c, tx, installation, runId);
            if (selected is null) return null;
            var (id, context) = selected.Value;
            var report = ReadCapture(c, tx, id);
            LibraryCollectionState? state = null;
            if (HasSavedCollectionTable(c, tx, "ticket_library_collection"))
            {
                using var query = Command(c, tx, "SELECT state_json FROM ticket_library_collection WHERE context_hash=$context", ("$context", context.Fingerprint));
                if (query.ExecuteScalar() is string json)
                {
                    var stored = JsonSerializer.Deserialize<LibraryCollectionState>(json, TicketLibraryRules.Json)
                        ?? throw new InvalidDataException("État de collecte conservé illisible.");
                    stored.Options.Validate(); stored.Scope.Validate();
                    // A later/older state for this context cannot replace the selected run.
                    if (stored.RunId == id) state = stored;
                }
            }
            state ??= new() { RunId = id, State = report.Status switch
                { "completed" => "completed", "paused" => "paused", "stopped" or "cancelled" => "stopped", _ => "interrupted" } };
            ct.ThrowIfCancellationRequested(); tx.Commit();
            return new TicketLibrarySavedCollection(context, state, report);
        }, ct);
    }

    public Task<TicketLibraryCollectionEventPage> ReadSavedCollectionEventsAsync(string installation, string runId = "",
        int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (installation.Length is 0 or > 3000) throw new InvalidDataException("Installation de collecte inconnue.");
        if (runId.Length > 0) ValidateRunId(runId);
        offset = Math.Clamp(offset, 0, CaptureTicketLimit); limit = Math.Clamp(limit, 1, 100);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); ValidateDatabasePath(DatabasePath);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            c.Open(); var page = new TicketLibraryCollectionEventPage { Offset = offset, Limit = limit };
            if (!HasReadableCacheSchema(c)) return page;
            using var tx = c.BeginTransaction(deferred: true);
            if (!HasSavedCollectionTable(c, tx, "ticket_library_capture_runs")) return page;
            var selected = ReadSavedCollectionIdentity(c, tx, installation, runId);
            if (selected is null) return page;
            page.RunId = selected.Value.RunId;
            if (HasSavedCollectionTable(c, tx, "ticket_library_collection_events"))
            {
                page.Total = ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_collection_events WHERE run_id=$run", ("$run", page.RunId));
                using var query = Command(c, tx, "SELECT sequence,recorded_at,level,stage,code,message,ticket_id FROM ticket_library_collection_events WHERE run_id=$run ORDER BY sequence DESC LIMIT $limit OFFSET $offset",
                    ("$run", page.RunId), ("$limit", limit), ("$offset", offset));
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    page.Items.Add(new() { Sequence = reader.GetInt64(0), At = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                        Level = reader.GetString(2), Stage = reader.GetString(3), Code = reader.GetString(4), Message = reader.GetString(5), TicketId = reader.GetString(6) });
                }
            }
            ct.ThrowIfCancellationRequested(); tx.Commit(); return page;
        }, ct);
    }

    public Task<TicketLibraryCaptureEntryPage> ReadSavedCaptureEntriesAsync(string installation, string runId,
        int offset = 0, int limit = 50, bool retryOnly = false, CancellationToken ct = default)
    {
        if (installation.Length is 0 or > 3000) throw new InvalidDataException("Installation de collecte inconnue.");
        ValidateRunId(runId); offset = Math.Clamp(offset, 0, CaptureTicketLimit); limit = Math.Clamp(limit, 1, 100);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); ValidateDatabasePath(DatabasePath);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            c.Open();
            if (!HasReadableCacheSchema(c)) throw new InvalidDataException("Le journal de collecte n’est pas disponible dans cette bibliothèque.");
            using var tx = c.BeginTransaction(deferred: true);
            _ = ReadSavedCollectionIdentity(c, tx, installation, runId);
            var page = ReadCaptureEntries(c, tx, runId, offset, limit, retryOnly, ct);
            ct.ThrowIfCancellationRequested(); tx.Commit(); return page;
        }, ct);
    }

    private static bool HasSavedCollectionTable(SqliteConnection c, SqliteTransaction tx, string name) =>
        ScalarLong(c, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", name)) == 1;

    private static (string RunId, TicketLibraryCaptureContext Context)? ReadSavedCollectionIdentity(
        SqliteConnection c, SqliteTransaction tx, string installation, string runId)
    {
        using var query = Command(c, tx, "SELECT run_id,context_hash,reader_signature FROM ticket_library_capture_runs WHERE installation=$installation AND ($run='' OR run_id=$run) ORDER BY started_at DESC,run_id DESC LIMIT 1",
            ("$installation", installation), ("$run", runId));
        using var reader = query.ExecuteReader();
        if (!reader.Read())
        {
            if (runId.Length > 0) throw new InvalidOperationException("Ce journal n’appartient pas à l’installation du relevé conservé ou n’est plus présent sur ce poste.");
            return null;
        }
        var id = reader.GetString(0); var context = new TicketLibraryCaptureContext(installation, reader.GetString(1), reader.GetString(2));
        ValidateRunId(id); ValidateCaptureContext(context); return (id, context);
    }
}
