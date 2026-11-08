using System.Collections.Generic;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    /// <summary>
    /// A resumed forced refresh must still read entries not read by this run.
    /// Older confirmed library contents are not proof that the requested refresh occurred.
    /// Reconciliation resets read_count when the observed source version changes.
    /// </summary>
    public Task<IReadOnlySet<string>> RequiredCollectionReadKeysAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        return Run<IReadOnlySet<string>>(c =>
        {
            using var tx = c.BeginTransaction();
            RequireRunningCapture(c, tx, runId);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            using (var query = Command(c, tx, """
                SELECT e.ticket_key FROM ticket_library_capture_entries e
                JOIN ticket_library_capture_runs r ON r.run_id=e.run_id
                WHERE e.run_id=$run AND r.mode='all' AND e.read_count=0
                """, ("$run", runId)))
            using (var reader = query.ExecuteReader())
                while (reader.Read()) { ct.ThrowIfCancellationRequested(); keys.Add(reader.GetString(0)); }
            ct.ThrowIfCancellationRequested(); tx.Commit(); return keys;
        }, ct);
    }

    // Resuming the same journal must retain its already completed reads and counts.
    // Otherwise a second pause loses that evidence and a forced refresh repeats work.
    public Task RecordCollectionResumeReuseAsync(string runId, string key, CancellationToken ct = default, bool awaitingConfirmation = false) => UpdateCaptureEntry(runId, key,
        awaitingConfirmation ? "state='reused',category='awaiting-final-check',retryable=0,processed=1,reused=CASE WHEN read_count>0 THEN 0 ELSE 1 END,failed=0,partial=0,confirmed=0"
            : "state='reused',category='cache-reused',retryable=0,processed=1,reused=CASE WHEN read_count>0 THEN 0 ELSE 1 END,failed=0,partial=0,confirmed=1", ct);
}
