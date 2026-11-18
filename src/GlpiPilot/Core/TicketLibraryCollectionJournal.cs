using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

/// <summary>Application diagnostics only. Never pass page text, headers, URLs or provider exceptions here.</summary>
public sealed record TicketLibraryCollectionEvent
{
    public long Sequence { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Level { get; init; } = "info";
    public string Stage { get; init; } = "";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string TicketId { get; init; } = "";
}

public sealed class TicketLibraryCollectionEventPage
{
    public string RunId { get; set; } = "";
    public long Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public bool Persisted { get; set; } = true;
    public List<TicketLibraryCollectionEvent> Items { get; set; } = new();
}

public sealed partial class TicketLibraryStore
{
    public Task AppendCollectionEventAsync(string runId, TicketLibraryCaptureContext context,
        TicketLibraryCollectionEvent item, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context); ValidateCollectionEvent(item);
        return Run(c =>
        {
            using var tx = c.BeginTransaction();
            RequireCollectionEventContext(c, tx, runId, context);
            Execute(c, tx, """
                INSERT INTO ticket_library_collection_events(run_id,recorded_at,level,stage,code,message,ticket_id)
                VALUES($run,$at,$level,$stage,$code,$message,$ticket)
                """, ("$run", runId), ("$at", item.At.ToUniversalTime().ToString("O")), ("$level", item.Level),
                ("$stage", item.Stage), ("$code", item.Code), ("$message", item.Message), ("$ticket", item.TicketId));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    // Read-only, independently of the collection semaphore. In particular this works before any ticket is indexed.
    public Task<TicketLibraryCollectionEventPage> CollectionEventsAsync(string runId, TicketLibraryCaptureContext context,
        int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (runId.Length > 0) ValidateRunId(runId);
        ValidateCaptureContext(context); offset = Math.Clamp(offset, 0, CaptureTicketLimit); limit = Math.Clamp(limit, 1, 100);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(deferred: true);
            var selected = runId;
            if (selected.Length == 0)
            {
                using var latest = Command(c, tx, "SELECT run_id FROM ticket_library_capture_runs WHERE installation=$installation AND context_hash=$context ORDER BY started_at DESC,run_id DESC LIMIT 1",
                    ("$installation", context.Installation), ("$context", context.Fingerprint));
                selected = latest.ExecuteScalar() as string ?? "";
            }
            var page = new TicketLibraryCollectionEventPage { RunId = selected, Offset = offset, Limit = limit };
            if (selected.Length == 0) { tx.Commit(); return page; }
            RequireCollectionEventContext(c, tx, selected, context);
            page.Total = ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_collection_events WHERE run_id=$run", ("$run", selected));
            using (var command = Command(c, tx, "SELECT sequence,recorded_at,level,stage,code,message,ticket_id FROM ticket_library_collection_events WHERE run_id=$run ORDER BY sequence DESC LIMIT $limit OFFSET $offset",
                ("$run", selected), ("$limit", limit), ("$offset", offset)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    page.Items.Add(new() { Sequence = reader.GetInt64(0), At = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                        Level = reader.GetString(2), Stage = reader.GetString(3), Code = reader.GetString(4), Message = reader.GetString(5), TicketId = reader.GetString(6) });
                }
            tx.Commit(); return page;
        }, ct);
    }

    private static void RequireCollectionEventContext(SqliteConnection c, SqliteTransaction tx, string runId, TicketLibraryCaptureContext context)
    {
        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE run_id=$run AND installation=$installation AND context_hash=$context",
            ("$run", runId), ("$installation", context.Installation), ("$context", context.Fingerprint)) != 1)
            throw new InvalidOperationException("Ce journal n’appartient pas à la source GLPI active ou n’est plus conservé sur ce poste.");
    }

    private static void ValidateCollectionEvent(TicketLibraryCollectionEvent item)
    {
        if (item.Level is not ("info" or "warning" or "error") || item.Stage.Length is 0 or > 160 || item.Message.Length is 0 or > 1600
            || item.Code.Length is 0 or > 80 || item.Code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            || item.TicketId.Length > 32 || item.TicketId.Any(c => !char.IsAsciiDigit(c))
            || item.Stage.Any(char.IsControl) || item.Message.Any(char.IsControl))
            throw new InvalidDataException("Événement de collecte invalide.");
    }
}
