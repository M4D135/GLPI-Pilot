using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    public const int CollectionWindowSize = 25;

    /// <summary>Durably records the entire denominator before the first navigation, without inventing a source observation.</summary>
    public Task InitializeCollectionWindowsAsync(string runId, TicketLibraryCaptureContext context,
        LibraryCollectionPlan plan, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context); ValidateCollectionPlan(plan);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireRunningCapture(c, tx, runId); RequireCollectionPlanContext(c, tx, runId, context);
            using (var saved = Command(c, tx, "SELECT fingerprint FROM ticket_library_collection_plans WHERE run_id=$run AND context_hash=$context", ("$run", runId), ("$context", context.Fingerprint)))
                if (saved.ExecuteScalar() is not string fingerprint || fingerprint != plan.Fingerprint)
                    throw new InvalidDataException("La liste de fenêtres ne correspond pas au périmètre enregistré.");
            if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_collection_plan_entries WHERE run_id=$run", ("$run", runId)) != plan.Selected
                || ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_entries e WHERE e.run_id=$run AND NOT EXISTS(SELECT 1 FROM ticket_library_collection_plan_entries p WHERE p.run_id=e.run_id AND p.ticket_key=e.ticket_key)", ("$run", runId)) != 0)
                throw new InvalidDataException("Le journal contient des identités extérieures au périmètre figé.");
            if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_collection_window_progress WHERE run_id=$run AND initialized=1", ("$run", runId)) == 1)
            {
                if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_entries WHERE run_id=$run", ("$run", runId)) != plan.Selected)
                    throw new InvalidDataException("La liste de fenêtres enregistrée est incomplète.");
                tx.Commit(); return true;
            }
            Execute(c, tx, """
                INSERT OR IGNORE INTO ticket_library_capture_entries(run_id,ticket_key,ticket_id,ordinal,seed_hash)
                SELECT run_id,ticket_key,json_extract(seed_json,'$.ticket.id'),ordinal,''
                FROM ticket_library_collection_plan_entries WHERE run_id=$run
                """, ("$run", runId));
            // An existing journal may have used the GLPI sort order. The durable plan owns the window order.
            Execute(c, tx, """
                UPDATE ticket_library_capture_entries SET ordinal=(SELECT p.ordinal FROM ticket_library_collection_plan_entries p
                  WHERE p.run_id=ticket_library_capture_entries.run_id AND p.ticket_key=ticket_library_capture_entries.ticket_key)
                WHERE run_id=$run
                """, ("$run", runId));
            Execute(c, tx, "INSERT INTO ticket_library_collection_window_progress(run_id,initialized) VALUES($run,1) ON CONFLICT(run_id) DO UPDATE SET initialized=1", ("$run", runId));
            Execute(c, tx, "UPDATE ticket_library_capture_runs SET observed=(SELECT COUNT(*) FROM ticket_library_capture_entries WHERE run_id=$run AND seed_hash<>'') WHERE run_id=$run", ("$run", runId));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    /// <summary>Reconciles only the current finite window. Later and already completed windows retain all their evidence.</summary>
    public Task ApplyCollectionWindowIndexAsync(string runId, TicketLibraryCaptureContext context, TicketLibraryIndex index,
        int offset, int count, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context); ValidateCollectionWindowRange(offset, count);
        ArgumentNullException.ThrowIfNull(index);
        if (!index.AllStatuses || !index.IsTargeted || index.RequestedCount != count || index.ScopeFingerprint.Length is 0 or > 128
            || ObjectIdentity.Installation(index.SourceUrl) != context.Installation)
            throw new InvalidDataException("L’index ciblé ne confirme pas le périmètre de cette fenêtre.");
        var seeds = CollectionSeeds(index.Tickets);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionWindow(c, tx, runId, context, offset, count, requireCursor: true);
            var members = CollectionWindowMembers(c, tx, runId, offset, count);
            if (seeds.Keys.Any(key => !members.ContainsKey(key)) || seeds.Values.Any(seed => ObjectIdentity.Installation(seed.Ticket.Url) != context.Installation))
                throw new InvalidDataException("GLPI a renvoyé une identité extérieure à la fenêtre de collecte.");
            using (var scope = Command(c, tx, "SELECT scope_hash,parent_run_id FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId)))
            using (var reader = scope.ExecuteReader())
            {
                reader.Read(); var previous = reader.GetString(0); var parent = reader.GetString(1);
                if (previous.Length > 0 && previous != index.ScopeFingerprint)
                    throw new GlpiLibraryIndexException("scope", "index-scope", "Le profil ou le périmètre d’entités GLPI a changé entre les fenêtres. Reprenez dans le même contexte.");
                if (parent.Length > 0)
                {
                    using var parentScope = Command(c, tx, "SELECT scope_hash FROM ticket_library_capture_runs WHERE run_id=$parent", ("$parent", parent));
                    if (parentScope.ExecuteScalar() is not string expected || expected.Length == 0 || expected != index.ScopeFingerprint)
                        throw new GlpiLibraryIndexException("scope", "index-scope", "Le périmètre GLPI du passage d’origine n’est pas confirmé ou a changé. Reprendre dans le même contexte avant de relancer ses erreurs.");
                }
            }
            var newlyObserved = seeds.Keys.Count(key => members[key].Length == 0);
            foreach (var pair in seeds)
            {
                ct.ThrowIfCancellationRequested(); var hash = TicketLibraryRules.FromSeed(pair.Value).ContentHash;
                if (members[pair.Key] != hash)
                {
                    Execute(c, tx, """
                        UPDATE ticket_library_capture_entries SET seed_hash=$hash,state='pending',category=$category,processed=0,
                          read_count=0,reused=0,failed=0,partial=0,confirmed=0,final_seen=0,retryable=1
                        WHERE run_id=$run AND ticket_key=$key
                        """, ("$hash", hash), ("$category", members[pair.Key].Length == 0 ? "pending" : "changed-during-capture"), ("$run", runId), ("$key", pair.Key));
                    Execute(c, tx, "DELETE FROM ticket_library_capture_pending WHERE run_id=$run AND ticket_key=$key", ("$run", runId), ("$key", pair.Key));
                }
                else Execute(c, tx, "UPDATE ticket_library_capture_entries SET final_seen=0 WHERE run_id=$run AND ticket_key=$key", ("$run", runId), ("$key", pair.Key));
            }
            foreach (var missing in members.Keys.Where(key => !seeds.ContainsKey(key)))
                Execute(c, tx, "UPDATE ticket_library_capture_entries SET state='unavailable',category='no-longer-visible',retryable=1,processed=1,confirmed=0,final_seen=0 WHERE run_id=$run AND ticket_key=$key",
                    ("$run", runId), ("$key", missing));
            Execute(c, tx, """
                UPDATE ticket_library_capture_runs SET scope=$scope,scope_hash=$hash,observed=observed+$new WHERE run_id=$run
                """, ("$scope", BoundedScope(index.ScopeDescription)), ("$hash", index.ScopeFingerprint), ("$new", newlyObserved), ("$run", runId));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    public Task UpdateCollectionWindowEvidenceAsync(string runId, TicketLibraryCaptureContext context,
        int offset, int count, IReadOnlyList<TicketLibrarySeed> seeds, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context); ValidateCollectionWindowRange(offset, count);
        var evidence = CollectionSeeds(seeds); var observedAt = evidence.Count == 0 ? DateTimeOffset.UtcNow : evidence.Values.Max(seed => seed.ObservedAt);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionWindow(c, tx, runId, context, offset, count, requireCursor: true);
            var members = CollectionWindowMembers(c, tx, runId, offset, count);
            if (evidence.Keys.Any(key => !members.ContainsKey(key)) || evidence.Values.Any(seed => ObjectIdentity.Installation(seed.Ticket.Url) != context.Installation))
                throw new InvalidDataException("L’observation contient un ticket extérieur à cette fenêtre.");
            Execute(c, tx, "UPDATE ticket_library_collection_plan_entries SET evidence_seed_json=NULL,evidence_present=0 WHERE run_id=$run AND ordinal>=$offset AND ordinal<$end",
                ("$run", runId), ("$offset", offset), ("$end", offset + count));
            using var update = Command(c, tx, "UPDATE ticket_library_collection_plan_entries SET evidence_seed_json=$seed,evidence_present=1 WHERE run_id=$run AND ticket_key=$key",
                ("$run", runId), ("$seed", ""), ("$key", ""));
            foreach (var pair in evidence)
            {
                ct.ThrowIfCancellationRequested(); update.Parameters["$key"].Value = pair.Key;
                update.Parameters["$seed"].Value = JsonSerializer.Serialize(pair.Value, TicketLibraryRules.Json); update.ExecuteNonQuery();
            }
            Execute(c, tx, "UPDATE ticket_library_collection_plans SET verified_at=$date WHERE run_id=$run", ("$run", runId), ("$date", observedAt.ToString("O")));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    public Task<LibraryCollectionCoverage> CollectionWindowCoverageAsync(string runId, TicketLibraryCaptureContext context,
        int offset, int count, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context); ValidateCollectionWindowRange(offset, count);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionWindow(c, tx, runId, context, offset, count, requireCursor: false);
            var coverage = new LibraryCollectionCoverage();
            using (var command = Command(c, tx, "SELECT " + CollectionMetadataSql + ",COALESCE(p.evidence_seed_json,p.seed_json),p.evidence_present"
                + " FROM ticket_library_collection_plan_entries p LEFT JOIN ticket_library_tickets t ON t.ticket_key=p.ticket_key WHERE p.run_id=$run AND p.ordinal>=$offset AND p.ordinal<$end ORDER BY p.ordinal",
                ("$run", runId), ("$offset", offset), ("$end", offset + count)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var seed = TicketLibraryRules.ValidateSeed(JsonSerializer.Deserialize<TicketLibrarySeed>(reader.GetString(15), TicketLibraryRules.Json)!);
                    var stored = reader.IsDBNull(0) ? null : ReadCollectionMetadata(reader);
                    var state = LibraryCollectionScopePlanner.Classify(seed, stored, context.ReaderSignature);
                    if (!reader.IsDBNull(16) && !reader.GetBoolean(16) && state == "complete") state = "unconfirmed";
                    coverage.Add(state);
                }
            tx.Commit(); return coverage;
        }, ct);
    }

    /// <summary>Called only after the runtime's successful final index comparison for this same window.</summary>
    public Task CompleteCollectionWindowAsync(string runId, TicketLibraryCaptureContext context,
        int offset, int count, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context); ValidateCollectionWindowRange(offset, count);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionWindow(c, tx, runId, context, offset, count, requireCursor: false);
            var cursor = ScalarLong(c, tx, "SELECT next_ordinal FROM ticket_library_collection_window_progress WHERE run_id=$run", ("$run", runId));
            if (cursor != offset && cursor != offset + count)
                throw new InvalidOperationException("Le point de reprise des fenêtres a changé. Aucun ticket n’a été sauté.");
            if (cursor == offset)
            {
                if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE run_id=$run AND scope_hash<>''", ("$run", runId)) != 1
                    || ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_entries e JOIN ticket_library_collection_plan_entries p ON p.run_id=e.run_id AND p.ticket_key=e.ticket_key WHERE p.run_id=$run AND p.ordinal>=$offset AND p.ordinal<$end AND (e.state IN ('pending','reading') OR p.evidence_present IS NULL)",
                        ("$run", runId), ("$offset", offset), ("$end", offset + count)) != 0)
                    throw new InvalidOperationException("La fenêtre n’a pas terminé ses lectures et son contrôle final. Le point de reprise est conservé.");
                // Explicit absence from the final finite index is a retryable result, never deletion of the cached body.
                Execute(c, tx, """
                    UPDATE ticket_library_capture_entries SET state='unavailable',category='no-longer-visible',retryable=1,processed=1,confirmed=0,final_seen=0
                    WHERE run_id=$run AND ticket_key IN (SELECT ticket_key FROM ticket_library_collection_plan_entries
                      WHERE run_id=$run AND ordinal>=$offset AND ordinal<$end AND evidence_present=0)
                    """, ("$run", runId), ("$offset", offset), ("$end", offset + count));
                if (ScalarLong(c, tx, """
                    SELECT COUNT(*) FROM ticket_library_capture_entries e JOIN ticket_library_collection_plan_entries p
                      ON p.run_id=e.run_id AND p.ticket_key=e.ticket_key
                    WHERE p.run_id=$run AND p.ordinal>=$offset AND p.ordinal<$end
                      AND NOT (p.evidence_present=0 AND e.state='unavailable') AND (e.seed_hash='' OR e.final_seen<>1)
                    """, ("$run", runId), ("$offset", offset), ("$end", offset + count)) != 0)
                    throw new InvalidOperationException("La preuve finale de cette fenêtre est incomplète. Aucun ticket n’a été sauté.");
                Execute(c, tx, "UPDATE ticket_library_collection_window_progress SET next_ordinal=$end WHERE run_id=$run AND next_ordinal=$offset",
                    ("$run", runId), ("$offset", offset), ("$end", offset + count));
            }
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    private static void ValidateCollectionWindowRange(int offset, int count)
    {
        if (offset < 0 || count is < 1 or > CollectionWindowSize || offset > CaptureTicketLimit - count)
            throw new InvalidDataException("La fenêtre de collecte doit contenir entre 1 et 25 tickets dans le périmètre.");
    }

    private static void RequireCollectionWindow(SqliteConnection c, SqliteTransaction tx, string runId,
        TicketLibraryCaptureContext context, int offset, int count, bool requireCursor)
    {
        RequireRunningCapture(c, tx, runId); RequireCollectionPlanContext(c, tx, runId, context);
        using var command = Command(c, tx, "SELECT next_ordinal,initialized,(SELECT json_extract(plan_json,'$.selected') FROM ticket_library_collection_plans p WHERE p.run_id=w.run_id) FROM ticket_library_collection_window_progress w WHERE run_id=$run", ("$run", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !reader.GetBoolean(1) || offset + count > reader.GetInt32(2) || requireCursor && reader.GetInt32(0) != offset)
            throw new InvalidOperationException("Cette fenêtre ne correspond pas au point de reprise durable de la collecte.");
    }

    private static Dictionary<string, string> CollectionWindowMembers(SqliteConnection c, SqliteTransaction tx, string runId, int offset, int count)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = Command(c, tx, "SELECT p.ticket_key,e.seed_hash FROM ticket_library_collection_plan_entries p JOIN ticket_library_capture_entries e ON e.run_id=p.run_id AND e.ticket_key=p.ticket_key WHERE p.run_id=$run AND p.ordinal>=$offset AND p.ordinal<$end ORDER BY p.ordinal",
            ("$run", runId), ("$offset", offset), ("$end", offset + count));
        using var reader = command.ExecuteReader(); while (reader.Read()) members.Add(reader.GetString(0), reader.GetString(1));
        if (members.Count != count) throw new InvalidDataException("Les identités de la fenêtre ne correspondent plus au périmètre enregistré.");
        return members;
    }
}
