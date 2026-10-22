using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    public const int CaptureHistoryLimit = 20;
    public const int CaptureTicketLimit = 1_000_000;
    private static readonly int CaptureProcessId = Environment.ProcessId;
    private static readonly long CaptureProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private static readonly ConcurrentDictionary<string, byte> ActiveCaptures = new(StringComparer.Ordinal);

    // These tables belong to this workstation. They are neither exported as RAG content nor synchronized.
    private const string CaptureSchemaSql = """
        CREATE TABLE IF NOT EXISTS ticket_library_capture_runs (
          run_id TEXT PRIMARY KEY, started_at TEXT NOT NULL, completed_at TEXT, status TEXT NOT NULL, mode TEXT NOT NULL,
          installation TEXT NOT NULL, context_hash TEXT NOT NULL, reader_signature TEXT NOT NULL, parent_run_id TEXT NOT NULL,
          scope TEXT NOT NULL DEFAULT '', scope_hash TEXT NOT NULL DEFAULT '', observed INTEGER NOT NULL DEFAULT 0,
          final_scope_verified INTEGER NOT NULL DEFAULT 0, limitations_json TEXT NOT NULL DEFAULT '[]',
          owner_process INTEGER NOT NULL, owner_start INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ticket_library_capture_recent ON ticket_library_capture_runs(started_at DESC,run_id);
        CREATE TABLE IF NOT EXISTS ticket_library_capture_entries (
          run_id TEXT NOT NULL REFERENCES ticket_library_capture_runs(run_id) ON DELETE CASCADE,
          ticket_key TEXT NOT NULL, ticket_id TEXT NOT NULL, ordinal INTEGER NOT NULL, seed_hash TEXT NOT NULL,
          state TEXT NOT NULL DEFAULT 'pending', category TEXT NOT NULL DEFAULT 'pending', retryable INTEGER NOT NULL DEFAULT 1,
          processed INTEGER NOT NULL DEFAULT 0, read_count INTEGER NOT NULL DEFAULT 0, reused INTEGER NOT NULL DEFAULT 0,
          failed INTEGER NOT NULL DEFAULT 0, partial INTEGER NOT NULL DEFAULT 0, conflicts INTEGER NOT NULL DEFAULT 0,
          confirmed INTEGER NOT NULL DEFAULT 0, final_seen INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(run_id,ticket_key));
        CREATE INDEX IF NOT EXISTS ticket_library_capture_page ON ticket_library_capture_entries(run_id,retryable,ordinal);
        CREATE TABLE IF NOT EXISTS ticket_library_capture_pending (
          ticket_key TEXT PRIMARY KEY, run_id TEXT NOT NULL, content_hash TEXT NOT NULL, local_revision INTEGER NOT NULL,
          conflict_count INTEGER NOT NULL);
        """;

    public Task<string> BeginCaptureAsync(TicketLibraryCaptureContext context, string mode, string parentRunId = "", CancellationToken ct = default)
    {
        ValidateCaptureContext(context);
        if (mode is not ("incremental" or "all" or "retry") || mode == "retry" && !Guid.TryParseExact(parentRunId, "N", out _))
            throw new InvalidDataException("Mode ou essai de reprise invalide.");
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RecoverAbandonedCaptures(c, tx);
            if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE status='running'") != 0)
                throw new InvalidOperationException("Une collecte utilise déjà cette bibliothèque locale.");
            var id = Guid.NewGuid().ToString("N");
            Execute(c, tx, """
                INSERT INTO ticket_library_capture_runs(run_id,started_at,status,mode,installation,context_hash,reader_signature,parent_run_id,owner_process,owner_start)
                VALUES($id,$date,'running',$mode,$installation,$context,$reader,$parent,$process,$start)
                """, ("$id", id), ("$date", DateTimeOffset.UtcNow.ToString("O")), ("$mode", mode), ("$installation", context.Installation),
                ("$context", context.Fingerprint), ("$reader", context.ReaderSignature), ("$parent", parentRunId), ("$process", CaptureProcessId), ("$start", CaptureProcessStart));
            Execute(c, tx, "DELETE FROM ticket_library_capture_runs WHERE run_id IN (SELECT run_id FROM ticket_library_capture_runs ORDER BY CASE WHEN run_id IN ($current,$parent) THEN 0 ELSE 1 END,started_at DESC,run_id DESC LIMIT -1 OFFSET $limit)",
                ("$limit", CaptureHistoryLimit), ("$current", id), ("$parent", parentRunId));
            Execute(c, tx, "DELETE FROM ticket_library_capture_pending WHERE NOT EXISTS(SELECT 1 FROM ticket_library_capture_runs r WHERE r.run_id=ticket_library_capture_pending.run_id)");
            ct.ThrowIfCancellationRequested(); ActiveCaptures[id] = 0;
            try { tx.Commit(); } catch { ReleaseCapture(id); throw; }
            return id;
        }, ct);
    }

    public Task<TicketLibraryCaptureReport?> LatestCaptureAsync(CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction(); RecoverAbandonedCaptures(c, tx);
        using var command = Command(c, tx, "SELECT run_id FROM ticket_library_capture_runs ORDER BY started_at DESC,run_id DESC LIMIT 1");
        var id = command.ExecuteScalar() as string; var result = id is null ? null : ReadCapture(c, tx, id);
        tx.Commit(); return result;
    }, ct);

    public Task<TicketLibraryCaptureReport> GetCaptureAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        return Run(c => ReadCapture(c, null, runId), ct);
    }

    /// <summary>Releases the in-process owner even if a final journal write was interrupted by an I/O error.</summary>
    public static void ReleaseCapture(string runId) => ActiveCaptures.TryRemove(runId, out _);

    public Task<TicketLibraryCaptureEntryPage> CaptureEntriesAsync(string runId, int offset = 0, int limit = 50, bool retryOnly = false, CancellationToken ct = default)
    {
        ValidateRunId(runId); offset = Math.Clamp(offset, 0, CaptureTicketLimit); limit = Math.Clamp(limit, 1, 100);
        return Run(c =>
        {
            _ = ReadCapture(c, null, runId);
            var page = new TicketLibraryCaptureEntryPage { Offset = offset, Limit = limit };
            const string where = " WHERE run_id=$run AND ($retry=0 OR retryable=1)";
            page.Total = ScalarLong(c, null, "SELECT COUNT(*) FROM ticket_library_capture_entries" + where, ("$run", runId), ("$retry", retryOnly ? 1 : 0));
            using var command = Command(c, null, "SELECT ticket_key,ticket_id,state,category,retryable FROM ticket_library_capture_entries" + where + " ORDER BY ordinal LIMIT $limit OFFSET $offset",
                ("$run", runId), ("$retry", retryOnly ? 1 : 0), ("$limit", limit), ("$offset", offset));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                page.Items.Add(new() { TicketKey = reader.GetString(0), TicketId = reader.GetString(1), State = reader.GetString(2), Category = reader.GetString(3), Retryable = reader.GetBoolean(4) });
            }
            return page;
        }, ct);
    }

    public Task<IReadOnlyList<TicketLibraryCaptureTarget>> CaptureRetryTargetsAsync(string runId, TicketLibraryCaptureContext context, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context);
        return Run<IReadOnlyList<TicketLibraryCaptureTarget>>(c =>
        {
            using var tx = c.BeginTransaction(); RecoverAbandonedCaptures(c, tx);
            using (var command = Command(c, tx, "SELECT installation,context_hash,reader_signature,status FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("Cet essai n’est plus présent dans le journal local.");
                if (reader.GetString(0) != context.Installation || reader.GetString(1) != context.Fingerprint || reader.GetString(2) != context.ReaderSignature)
                    throw new InvalidOperationException("Le contexte GLPI a changé. Revenez à la source de cet essai ou démarrez une nouvelle collecte.");
                if (reader.GetString(3) == "running") throw new InvalidOperationException("Cet essai est encore en cours.");
            }
            var targets = new List<TicketLibraryCaptureTarget>();
            using (var command = Command(c, tx, "SELECT ticket_key,ticket_id FROM ticket_library_capture_entries WHERE run_id=$run AND retryable=1 ORDER BY ordinal LIMIT $limit", ("$run", runId), ("$limit", CaptureTicketLimit + 1)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { ct.ThrowIfCancellationRequested(); targets.Add(new(reader.GetString(0), reader.GetString(1))); }
            if (targets.Count > CaptureTicketLimit) throw new InvalidDataException("Le journal de reprise dépasse sa limite de tickets.");
            tx.Commit(); return targets;
        }, ct);
    }

    public Task SetCaptureIndexAsync(string runId, TicketLibraryIndex index, IReadOnlyList<TicketLibraryCaptureTarget>? retryTargets = null, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (!index.AllStatuses || index.Tickets.Count > CaptureTicketLimit || index.ScopeFingerprint.Length is 0 or > 128)
            throw new InvalidDataException("L’index de collecte ne confirme pas tous les statuts dans un périmètre identifiable.");
        var seeds = index.Tickets.ToDictionary(s => TicketLibraryRules.Key(TicketLibraryRules.ValidateSeed(s).Ticket), StringComparer.Ordinal);
        var targets = retryTargets ?? seeds.Values.Select(s => new TicketLibraryCaptureTarget(TicketLibraryRules.Key(s.Ticket), s.Ticket.Id)).ToArray();
        if (targets.Count > CaptureTicketLimit) throw new InvalidDataException("Le journal de collecte dépasse sa limite de tickets.");
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireRunningCapture(c, tx, runId);
            using (var command = Command(c, tx, "SELECT installation,parent_run_id FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId)))
            using (var reader = command.ExecuteReader())
            {
                reader.Read(); var installation = reader.GetString(0); var parent = reader.GetString(1);
                if (ObjectIdentity.Installation(index.SourceUrl) != installation || seeds.Values.Any(s => ObjectIdentity.Installation(s.Ticket.Url) != installation))
                    throw new InvalidDataException("L’index ne correspond pas à l’installation de cet essai.");
                if (parent.Length > 0)
                {
                    using var parentCommand = Command(c, tx, "SELECT scope_hash FROM ticket_library_capture_runs WHERE run_id=$parent", ("$parent", parent));
                    if (parentCommand.ExecuteScalar() is not string scope || scope.Length == 0 || scope != index.ScopeFingerprint)
                        throw new InvalidOperationException("Le périmètre GLPI de la reprise a changé. Démarrez une nouvelle collecte pour ce périmètre.");
                }
            }
            Execute(c, tx, "UPDATE ticket_library_capture_runs SET observed=$count,scope=$scope,scope_hash=$hash,limitations_json=$limits WHERE run_id=$run",
                ("$count", index.Tickets.Count), ("$scope", BoundedScope(index.ScopeDescription)), ("$hash", index.ScopeFingerprint), ("$limits", CaptureLimits(index.Limitations)), ("$run", runId));
            using var insert = Command(c, tx, "INSERT INTO ticket_library_capture_entries(run_id,ticket_key,ticket_id,ordinal,seed_hash,state,category,processed) VALUES($run,$key,$id,$ordinal,$hash,$state,$category,$processed)",
                ("$run", runId), ("$key", ""), ("$id", ""), ("$ordinal", 0), ("$hash", ""), ("$state", "pending"), ("$category", "pending"), ("$processed", 0));
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested(); var target = targets[i]; seeds.TryGetValue(target.TicketKey, out var seed);
                if (target.TicketKey.Length is 0 or > 128 || target.TicketId.Length is 0 or > 20 || !target.TicketId.All(char.IsAsciiDigit))
                    throw new InvalidDataException("Identité du journal de reprise invalide.");
                insert.Parameters["$key"].Value = target.TicketKey; insert.Parameters["$id"].Value = seed?.Ticket.Id ?? target.TicketId;
                insert.Parameters["$ordinal"].Value = i; insert.Parameters["$hash"].Value = seed is null ? "" : TicketLibraryRules.FromSeed(seed).ContentHash;
                insert.Parameters["$state"].Value = seed is null ? "unavailable" : "pending";
                insert.Parameters["$category"].Value = seed is null ? "no-longer-visible" : "pending";
                insert.Parameters["$processed"].Value = seed is null ? 1 : 0; insert.ExecuteNonQuery();
            }
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    public Task RecordCaptureReadingAsync(string runId, string ticketKey, CancellationToken ct = default)
        => UpdateCaptureEntry(runId, ticketKey, "state='reading',category='reading'", ct);
    public Task RecordCaptureReuseAsync(string runId, string ticketKey, CancellationToken ct = default)
        => UpdateCaptureEntry(runId, ticketKey, "state='reused',category='cache-reused',retryable=0,processed=1,reused=1,confirmed=1", ct);

    public Task RecordCaptureFailureAsync(string runId, string ticketKey, string category, CancellationToken ct = default)
    {
        if (category is not ("read-timeout" or "browser-unavailable" or "invalid-content" or "local-changed" or "local-storage"))
            throw new InvalidDataException("Catégorie de diagnostic inconnue.");
        return UpdateCaptureEntry(runId, ticketKey, "state='failed',category=$category,retryable=1,processed=1,failed=1", ct, ("$category", category));
    }

    public Task<TicketLibraryWriteResult> SaveCaptureDetailAsync(string runId, TicketLibrarySeed seed, TicketLibraryDetailRead detail,
        long expectedLocalRevision, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var candidate = TicketLibraryRules.FromDetail(seed, detail.Detail, detail.Coverage, detail.ReaderSignature);
        candidate.DetailsCurrent = false; candidate.ConfirmationPending = true; candidate.ConfirmationId = runId; candidate.ConfirmedContentHash = "";
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireCaptureEntry(c, tx, runId, candidate.Key);
            RequireCaptureSeed(c, tx, runId, seed);
            var result = SaveCore(c, tx, candidate, expectedLocalRevision, ct);
            var conflict = result.Outcome is not ("identical" or "inserted" or "content-added" or "newer-source-version");
            var partial = !candidate.DetailComplete;
            Execute(c, tx, """
                INSERT INTO ticket_library_capture_pending VALUES($key,$run,$hash,$revision,$conflicts)
                ON CONFLICT(ticket_key) DO UPDATE SET run_id=excluded.run_id,content_hash=excluded.content_hash,local_revision=excluded.local_revision,conflict_count=excluded.conflict_count
                """, ("$key", candidate.Key), ("$run", runId), ("$hash", candidate.ContentHash), ("$revision", result.Document.LocalRevision), ("$conflicts", result.Document.Conflicts));
            Execute(c, tx, """
                UPDATE ticket_library_capture_entries SET state=$state,category=$category,retryable=1,processed=1,read_count=1,
                partial=$partial,conflicts=MAX(conflicts,$conflicts),confirmed=0 WHERE run_id=$run AND ticket_key=$key
                """, ("$state", partial ? "partial" : conflict ? "conflict" : "unconfirmed"),
                ("$category", partial ? "incomplete-content" : conflict ? "conflicting-version" : "awaiting-final-check"),
                ("$partial", partial ? 1 : 0), ("$conflicts", conflict ? 1 : 0), ("$run", runId), ("$key", candidate.Key));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
        }, ct);
    }

    public Task<TicketLibraryWriteResult> ObserveCaptureSeedAsync(string runId, TicketLibrarySeed seed, CancellationToken ct = default)
    {
        var candidate = TicketLibraryRules.FromSeed(seed); ValidateRunId(runId);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireCaptureEntry(c, tx, runId, candidate.Key);
            RequireCaptureSeed(c, tx, runId, seed);
            var result = SaveCore(c, tx, candidate, null, ct);
            if (result.ConflictCreated) Execute(c, tx, "UPDATE ticket_library_capture_entries SET conflicts=1 WHERE run_id=$run AND ticket_key=$key", ("$run", runId), ("$key", candidate.Key));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
        }, ct);
    }

    public Task ConfirmCaptureTicketAsync(string runId, TicketLibrarySeed finalSeed, bool scopeVerified, CancellationToken ct = default)
    {
        ValidateRunId(runId); var metadata = TicketLibraryRules.FromSeed(finalSeed);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireRunningCapture(c, tx, runId);
            using (var installation = Command(c, tx, "SELECT installation FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId)))
                if (installation.ExecuteScalar() is not string expectedInstallation || expectedInstallation != metadata.Installation)
                    throw new InvalidDataException("Le contrôle final ne correspond pas à l’installation de cet essai.");
            var before = Read(c, tx, metadata.Key);
            var observed = SaveCore(c, tx, metadata, null, ct);
            string initialHash; bool read; bool reused; bool partial;
            using (var command = Command(c, tx, "SELECT seed_hash,read_count,reused,partial FROM ticket_library_capture_entries WHERE run_id=$run AND ticket_key=$key", ("$run", runId), ("$key", metadata.Key)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) { tx.Commit(); return true; }
                initialHash = reader.GetString(0); read = reader.GetBoolean(1); reused = reader.GetBoolean(2); partial = reader.GetBoolean(3);
            }
            Execute(c, tx, "UPDATE ticket_library_capture_entries SET final_seen=1,conflicts=MAX(conflicts,$conflict) WHERE run_id=$run AND ticket_key=$key",
                ("$conflict", observed.ConflictCreated ? 1 : 0), ("$run", runId), ("$key", metadata.Key));
            if (!read && !reused) { tx.Commit(); return true; }
            string? category = null;
            if (initialHash != metadata.ContentHash) category = "changed-during-capture";
            else if (!scopeVerified && read) category = "scope-not-confirmed";
            else if (read && (!finalSeed.LastModifiedReliable || finalSeed.LastModifiedAt is null || finalSeed.LastModifiedPrecision == "unknown")) category = "missing-last-modified";
            if (category is not null)
            {
                Execute(c, tx, "UPDATE ticket_library_capture_entries SET state=CASE WHEN partial=1 THEN 'partial' ELSE 'unconfirmed' END,category=$category,retryable=1,confirmed=0 WHERE run_id=$run AND ticket_key=$key",
                    ("$category", category), ("$run", runId), ("$key", metadata.Key));
                tx.Commit(); return true;
            }
            if (reused) { tx.Commit(); return true; }
            string hash; long revision; int conflicts;
            using (var command = Command(c, tx, "SELECT content_hash,local_revision,conflict_count FROM ticket_library_capture_pending WHERE ticket_key=$key AND run_id=$run", ("$run", runId), ("$key", metadata.Key)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) { tx.Commit(); return true; }
                hash = reader.GetString(0); revision = reader.GetInt64(1); conflicts = reader.GetInt32(2);
            }
            var document = observed.Document;
            var canConfirm = before is not null && before.LocalRevision == revision && before.Conflicts == conflicts
                && document.ContentHash == hash && document.ConfirmationPending && document.ConfirmationId == runId;
            var alreadyConfirmed = document.ContentHash == hash && document.DetailsCurrent && document.ConfirmedContentHash == hash && document.ConfirmationId == runId;
            if (canConfirm || alreadyConfirmed)
            {
                if (!alreadyConfirmed)
                {
                    AddHistory(c, tx, document, "capture-confirmed");
                    document.DetailsCurrent = true; document.ConfirmationPending = false; document.ConfirmedContentHash = document.ContentHash; document.LocalRevision++;
                    Write(c, tx, document, ct); Queue(c, tx, document);
                }
                Execute(c, tx, "DELETE FROM ticket_library_capture_pending WHERE ticket_key=$key AND run_id=$run", ("$key", metadata.Key), ("$run", runId));
                Execute(c, tx, "UPDATE ticket_library_capture_entries SET state=$state,category=$category,confirmed=1,retryable=$retry WHERE run_id=$run AND ticket_key=$key",
                    ("$state", partial ? "partial" : "confirmed"), ("$category", partial ? "incomplete-content" : "confirmed"), ("$retry", partial ? 1 : 0), ("$run", runId), ("$key", metadata.Key));
            }
            else
            {
                var conflicting = document.ContentHash != hash || !document.ConfirmationPending;
                Execute(c, tx, "UPDATE ticket_library_capture_entries SET state=$state,category=$category,retryable=1,confirmed=0 WHERE run_id=$run AND ticket_key=$key",
                    ("$state", partial ? "partial" : conflicting ? "conflict" : "unconfirmed"), ("$category", partial ? "incomplete-content" : conflicting ? "conflicting-version" : "local-changed"), ("$run", runId), ("$key", metadata.Key));
            }
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    public Task FinishCaptureVerificationAsync(string runId, bool finalIndexRead, bool scopeVerified, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireRunningCapture(c, tx, runId);
            Execute(c, tx, "UPDATE ticket_library_capture_runs SET final_scope_verified=$scope WHERE run_id=$run", ("$scope", scopeVerified ? 1 : 0), ("$run", runId));
            Execute(c, tx, """
                UPDATE ticket_library_capture_entries SET state=CASE WHEN partial=1 THEN 'partial' ELSE 'unconfirmed' END,
                category=$category,retryable=1,confirmed=0 WHERE run_id=$run AND final_seen=0 AND (read_count=1 OR ($final=1 AND reused=1))
                """, ("$category", finalIndexRead ? "no-longer-visible" : "final-check-failed"), ("$run", runId), ("$final", finalIndexRead ? 1 : 0));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    public Task FinishCaptureAsync(string runId, string status, IEnumerable<string> limitations, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (status is not ("completed" or "cancelled" or "interrupted" or "failed")) throw new InvalidDataException("État final de collecte inconnu.");
        var limits = CaptureLimits(limitations);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireRunningCapture(c, tx, runId);
            Execute(c, tx, "UPDATE ticket_library_capture_entries SET state='interrupted',category='interrupted',retryable=1 WHERE run_id=$run AND state IN ('pending','reading')", ("$run", runId));
            if (status != "completed")
                Execute(c, tx, "UPDATE ticket_library_capture_entries SET category='interrupted' WHERE run_id=$run AND category='awaiting-final-check'", ("$run", runId));
            Execute(c, tx, "UPDATE ticket_library_capture_runs SET status=$status,completed_at=$date,limitations_json=$limits WHERE run_id=$run",
                ("$status", status), ("$date", DateTimeOffset.UtcNow.ToString("O")), ("$limits", limits), ("$run", runId));
            ct.ThrowIfCancellationRequested(); tx.Commit(); ReleaseCapture(runId); return true;
        }, ct);
    }

    private Task UpdateCaptureEntry(string runId, string key, string updates, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        ValidateRunId(runId);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RequireCaptureEntry(c, tx, runId, key);
            Execute(c, tx, "UPDATE ticket_library_capture_entries SET " + updates + " WHERE run_id=$run AND ticket_key=$key", parameters.Concat(new (string, object?)[] { ("$run", runId), ("$key", key) }).ToArray());
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    private static TicketLibraryCaptureReport ReadCapture(SqliteConnection c, SqliteTransaction? tx, string runId)
    {
        var report = new TicketLibraryCaptureReport { RunId = runId };
        using (var command = Command(c, tx, "SELECT status,started_at,completed_at,mode,scope,observed,final_scope_verified,limitations_json FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Cet essai n’est plus présent dans le journal local.");
            report.Status = reader.GetString(0); report.StartedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            report.CompletedAt = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            report.Mode = reader.GetString(3); report.Scope = reader.GetString(4); report.Observed = reader.GetInt32(5); report.FinalScopeVerified = reader.GetBoolean(6);
            report.Limitations = JsonSerializer.Deserialize<List<string>>(reader.GetString(7), TicketLibraryRules.Json) ?? new();
        }
        using var count = Command(c, tx, """
            SELECT COUNT(*),COALESCE(SUM(processed),0),COALESCE(SUM(read_count),0),COALESCE(SUM(reused),0),COALESCE(SUM(failed),0),
            COALESCE(SUM(partial),0),COALESCE(SUM(conflicts),0),COALESCE(SUM(CASE WHEN read_count=1 AND partial=0 AND confirmed=0 THEN 1 ELSE 0 END),0),COALESCE(SUM(retryable),0)
            FROM ticket_library_capture_entries WHERE run_id=$run
            """, ("$run", runId));
        using var counts = count.ExecuteReader(); counts.Read();
        report.Targeted = counts.GetInt32(0); report.Processed = counts.GetInt32(1); report.Read = counts.GetInt32(2); report.Reused = counts.GetInt32(3);
        report.Failed = counts.GetInt32(4); report.Partial = counts.GetInt32(5); report.Conflicts = counts.GetInt32(6); report.Unconfirmed = counts.GetInt32(7); report.RetryCount = counts.GetInt32(8);
        return report;
    }

    private static void RecoverAbandonedCaptures(SqliteConnection c, SqliteTransaction tx)
    {
        var abandoned = new List<string>();
        using (var command = Command(c, tx, "SELECT run_id,owner_process,owner_start FROM ticket_library_capture_runs WHERE status='running'"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var alive = false;
                try
                {
                    using var process = Process.GetProcessById(reader.GetInt32(1));
                    alive = !process.HasExited && process.StartTime.ToUniversalTime().Ticks == reader.GetInt64(2);
                    if (alive && process.Id == CaptureProcessId && reader.GetInt64(2) == CaptureProcessStart)
                        alive = ActiveCaptures.ContainsKey(reader.GetString(0));
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { alive = true; } // Lack of OS permission is not proof of abandonment.
                if (!alive) abandoned.Add(reader.GetString(0));
            }
        foreach (var id in abandoned)
        {
            Execute(c, tx, "UPDATE ticket_library_capture_runs SET status='interrupted',completed_at=$date WHERE run_id=$run", ("$date", DateTimeOffset.UtcNow.ToString("O")), ("$run", id));
            Execute(c, tx, "UPDATE ticket_library_capture_entries SET state='interrupted',category='interrupted',retryable=1 WHERE run_id=$run AND state IN ('pending','reading')", ("$run", id));
            Execute(c, tx, "UPDATE ticket_library_capture_entries SET category='interrupted' WHERE run_id=$run AND category='awaiting-final-check'", ("$run", id));
        }
    }

    private static void RequireRunningCapture(SqliteConnection c, SqliteTransaction tx, string runId)
    {
        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE run_id=$run AND status='running'", ("$run", runId)) != 1)
            throw new InvalidOperationException("L’essai de collecte n’est plus actif.");
    }
    private static void RequireCaptureEntry(SqliteConnection c, SqliteTransaction tx, string runId, string key)
    {
        RequireRunningCapture(c, tx, runId);
        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_entries WHERE run_id=$run AND ticket_key=$key AND seed_hash<>''", ("$run", runId), ("$key", key)) != 1)
            throw new InvalidOperationException("La fiche n’est pas autorisée par l’index de cet essai.");
    }
    private static void RequireCaptureSeed(SqliteConnection c, SqliteTransaction tx, string runId, TicketLibrarySeed seed)
    {
        using var command = Command(c, tx, "SELECT seed_hash FROM ticket_library_capture_entries WHERE run_id=$run AND ticket_key=$key", ("$run", runId), ("$key", TicketLibraryRules.Key(seed.Ticket)));
        if (command.ExecuteScalar() is not string expected || expected != TicketLibraryRules.FromSeed(seed).ContentHash)
            throw new InvalidOperationException("La version de la fiche ne correspond pas à l’index de cet essai.");
    }
    private static void ValidateRunId(string runId)
    { if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidDataException("Identifiant d’essai invalide."); }
    private static void ValidateCaptureContext(TicketLibraryCaptureContext context)
    {
        if (context.Installation.Length is 0 or > 3000 || context.Fingerprint.Length != 64 || !context.Fingerprint.All(char.IsAsciiHexDigit)
            || context.ReaderSignature.Length is 0 or > 200)
            throw new InvalidDataException("Le contexte de collecte est invalide.");
    }
    private static string BoundedScope(string scope) => scope.Length <= 2000 ? scope : scope[..2000];
    private static string CaptureLimits(IEnumerable<string> limitations)
        => TicketLibraryRules.Serialize(limitations.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Length <= 2000 ? s : s[..2000]).Distinct(StringComparer.Ordinal).Take(50).ToArray());
}
