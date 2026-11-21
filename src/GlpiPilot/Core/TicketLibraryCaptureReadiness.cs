using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

/// <summary>Current selected-ticket quality, using the same v1 flags as knowledge consumers.</summary>
public sealed class TicketLibraryCaptureReadiness
{
    public bool Available { get; init; } = true;
    public int Tickets { get; init; }
    public int EligibleTickets { get; init; }
    public int Incomplete { get; init; }
    public int MissingContent { get; init; }
    public int Unconfirmed { get; init; }
    public int Conflicts { get; init; }
    public bool IsReady => Available && Tickets > 0 && EligibleTickets == Tickets;
    public string Message => $"{EligibleTickets:N0} / {Tickets:N0} fiches complètes, confirmées et sans conflit dans la bibliothèque actuelle."
        + (IsReady ? "" : " Les autres contenus restent conservés ; leurs motifs doivent être examinés avant un import de connaissances.");
}

public sealed partial class TicketLibraryStore
{
    // This is a read-only audit of the public SQL contract. Journal counters alone
    // cannot attest to a selected document after another local read or synchronization.
    public Task<TicketLibraryCaptureReadiness> CaptureKnowledgeReadinessAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); ValidateDatabasePath(DatabasePath);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
            c.Open(); using var tx = c.BeginTransaction(deferred: true);
            _ = ReadCapture(c, tx, runId);
            using var command = Command(c, tx, """
                SELECT COUNT(*),
                    COALESCE(SUM(t.detail_complete=1 AND t.has_content=1 AND t.content_confirmed=1 AND t.confirmation_pending=0 AND t.conflict_count=0),0),
                    COALESCE(SUM(COALESCE(t.detail_complete,0)<>1),0),
                    COALESCE(SUM(COALESCE(t.has_content,0)<>1),0),
                    COALESCE(SUM(COALESCE(t.content_confirmed,0)<>1 OR COALESCE(t.confirmation_pending,1)<>0),0),
                    COALESCE(SUM(COALESCE(t.conflict_count,0)>0),0)
                FROM ticket_library_capture_entries e
                JOIN ticket_library_capture_runs r ON r.run_id=e.run_id
                LEFT JOIN ticket_library_knowledge_v1_tickets t
                    ON t.ticket_key=e.ticket_key AND t.installation=r.installation
                    AND t.version_kind='selected' AND t.version_id='selected'
                WHERE e.run_id=$run
                """, ("$run", runId));
            using var reader = command.ExecuteReader(); reader.Read();
            var result = new TicketLibraryCaptureReadiness { Tickets = reader.GetInt32(0), EligibleTickets = reader.GetInt32(1),
                Incomplete = reader.GetInt32(2), MissingContent = reader.GetInt32(3), Unconfirmed = reader.GetInt32(4), Conflicts = reader.GetInt32(5) };
            ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
        }, ct);
    }
}
