using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    // This expression deliberately checks the stored body, rather than the historical
    // read counter. A failed later attempt must not hide an older usable local body.
    private const string CaptureCurrentContentSql = """
        CASE WHEN t.document_json IS NULL THEN 'missing'
          WHEN NOT json_valid(t.document_json) THEN 'invalid'
          WHEN json_type(t.document_json,'$.detail') IS NULL OR json_type(t.document_json,'$.detail')='null' THEN 'missing'
          WHEN json_type(t.document_json,'$.detail')<>'object'
            OR json_extract(t.document_json,'$.key') IS NOT t.ticket_key
            OR json_extract(t.document_json,'$.contentHash') IS NOT t.content_hash THEN 'invalid'
          ELSE 'stored' END
        """;

    // An archived body is evidence to inspect, never permission to choose a winning
    // version. Exclude it from a missing-only collection rather than rereading it.
    private const string CaptureArchivedContentSql = """
        EXISTS(SELECT 1 FROM ticket_library_history h WHERE h.ticket_key=e.ticket_key AND
            CASE WHEN NOT json_valid(h.document_json) THEN 1 ELSE json_type(h.document_json,'$.detail')='object' END)
        OR EXISTS(SELECT 1 FROM ticket_library_conflicts cf WHERE cf.ticket_key=e.ticket_key AND
            (CASE WHEN NOT json_valid(cf.current_json) THEN 1 ELSE json_type(cf.current_json,'$.detail')='object' END
             OR CASE WHEN NOT json_valid(cf.incoming_json) THEN 1 ELSE json_type(cf.incoming_json,'$.detail')='object' END))
        """;

    private static readonly string CaptureStoredContentSql = $"""
        CASE WHEN ({CaptureCurrentContentSql})='missing' AND ({CaptureArchivedContentSql}) THEN 'archived'
            ELSE ({CaptureCurrentContentSql}) END
        """;

    private const string CaptureConfirmedContentSql = """
        COALESCE(json_extract(t.document_json,'$.detailsCurrent'),0)=1
        AND COALESCE(json_extract(t.document_json,'$.confirmationPending'),0)=0
        AND length(t.content_hash)=64
        AND json_extract(t.document_json,'$.confirmedContentHash')=t.content_hash
        AND length(json_extract(t.document_json,'$.confirmationId'))=32
        AND json_extract(t.document_json,'$.confirmationId') NOT GLOB '*[^0-9A-Fa-f]*'
        """;

    /// <summary>
    /// Counts available local bodies without a browser, a source-date reinterpretation,
    /// a journal repair, a schema initialization or any database write. This is an
    /// availability inventory; it does not claim a new GLPI freshness proof.
    /// </summary>
    public Task<TicketLibraryCaptureAvailability> CaptureAvailabilityAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); ValidateDatabasePath(DatabasePath);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            c.Open();
            using var tx = c.BeginTransaction(deferred: true);
            _ = ReadCapture(c, tx, runId);
            var result = new TicketLibraryCaptureAvailability { RunId = runId };
            using var command = Command(c, tx, $"""
                WITH bodies AS (
                    SELECT {CaptureStoredContentSql} AS availability,
                        CASE WHEN json_valid(t.document_json) THEN COALESCE(json_extract(t.document_json,'$.detailComplete'),0) ELSE 0 END AS complete,
                        CASE WHEN json_valid(t.document_json) THEN CASE WHEN {CaptureConfirmedContentSql} THEN 1 ELSE 0 END ELSE 0 END AS confirmed
                    FROM ticket_library_capture_entries e
                    LEFT JOIN ticket_library_tickets t ON t.ticket_key=e.ticket_key
                    WHERE e.run_id=$run)
                SELECT COUNT(*),
                    COALESCE(SUM(availability='stored'),0),
                    COALESCE(SUM(availability='stored' AND complete=1),0),
                    COALESCE(SUM(availability='stored' AND complete<>1),0),
                    COALESCE(SUM(availability='missing'),0),
                    COALESCE(SUM(availability='stored' AND confirmed=1),0),
                    COALESCE(SUM(availability='stored' AND confirmed<>1),0),
                    COALESCE(SUM(availability='invalid'),0),
                    COALESCE(SUM(availability='archived'),0)
                FROM bodies
                """, ("$run", runId));
            using var reader = command.ExecuteReader(); reader.Read();
            result.Observed = reader.GetInt32(0); result.Stored = reader.GetInt32(1);
            result.Complete = reader.GetInt32(2); result.Partial = reader.GetInt32(3); result.Missing = reader.GetInt32(4);
            result.Confirmed = reader.GetInt32(5); result.Unconfirmed = reader.GetInt32(6); result.Invalid = reader.GetInt32(7); result.Archived = reader.GetInt32(8);
            ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
        }, ct);
    }

    /// <summary>
    /// A missing-only run preserves any body that has arrived since selection or was
    /// saved before a pause, even if partial or undated. Only its local journal changes;
    /// no stored document, confirmation proof, history, conflict or outbox is rewritten.
    /// </summary>
    public Task<bool> PreserveCaptureContentAsync(string runId, string ticketKey, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireCaptureEntry(c, tx, runId, ticketKey);
            string availability; bool complete;
            using (var command = Command(c, tx, $"""
                SELECT {CaptureStoredContentSql},
                    CASE WHEN json_valid(t.document_json) THEN COALESCE(json_extract(t.document_json,'$.detailComplete'),0) ELSE 0 END
                FROM ticket_library_capture_entries e JOIN ticket_library_capture_runs r ON r.run_id=e.run_id
                LEFT JOIN ticket_library_tickets t ON t.ticket_key=e.ticket_key
                WHERE e.run_id=$run AND e.ticket_key=$key AND r.mode='retry-missing'
                """, ("$run", runId), ("$key", ticketKey)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(0) == "missing") { tx.Commit(); return false; }
                availability = reader.GetString(0); complete = reader.GetBoolean(1);
            }
            var partial = availability == "stored" && !complete;
            Execute(c, tx, """
                UPDATE ticket_library_capture_entries SET state=$state,category='stored-preserved',processed=1,
                    partial=$partial,confirmed=0,retryable=1,failed=0 WHERE run_id=$run AND ticket_key=$key
                """, ("$state", availability != "stored" ? "conflict" : partial ? "partial" : "unconfirmed"),
                ("$partial", partial ? 1 : 0), ("$run", runId), ("$key", ticketKey));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }
}
