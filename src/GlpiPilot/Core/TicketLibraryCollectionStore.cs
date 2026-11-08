using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    private const string CollectionSchema = """
        CREATE TABLE IF NOT EXISTS ticket_library_collection (
          context_hash TEXT PRIMARY KEY, revision INTEGER NOT NULL, state_json TEXT NOT NULL);
        """;
    public Task<LibraryCollectionState> CollectionStateAsync(string contextHash, CancellationToken ct = default)
    {
        ValidateCollectionContext(contextHash);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionSchema); RecoverAbandonedCaptures(c, tx);
            using var query = Command(c, tx, "SELECT state_json FROM ticket_library_collection WHERE context_hash=$context", ("$context", contextHash));
            var state = query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<LibraryCollectionState>(json, TicketLibraryRules.Json) ?? throw new InvalidDataException("État de collecte illisible.") : new LibraryCollectionState();
            state.Options.Validate();
            if (state.RunId.Length > 0 && ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", state.RunId)) == 0)
            {
                state.RunId = ""; state.State = "interrupted"; state.NextAt = null; state.Revision++;
                WriteCollectionState(c, tx, contextHash, state);
            }
            if (state.State == "running")
            {
                var running = state.RunId.Length > 0 && ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE run_id=$run AND status='running'", ("$run", state.RunId)) == 1;
                if (!running) { state.State = "interrupted"; state.Revision++; WriteCollectionState(c, tx, contextHash, state); }
            }
            tx.Commit(); return state;
        }, ct);
    }
    public Task<LibraryCollectionState> SaveCollectionStateAsync(string contextHash, LibraryCollectionState state, long expectedRevision, CancellationToken ct = default)
    {
        ValidateCollectionContext(contextHash); state = TicketLibraryRules.Copy(state); state.Options.Validate();
        if (expectedRevision < 0 || state.State is not ("idle" or "running" or "paused" or "stopped" or "interrupted" or "attention" or "completed" or "budget")
            || state.RunId.Length != 0 && !Guid.TryParseExact(state.RunId, "N", out _)) throw new InvalidDataException("Point de reprise de collecte invalide.");
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionSchema);
            using var query = Command(c, tx, "SELECT revision FROM ticket_library_collection WHERE context_hash=$context", ("$context", contextHash));
            var revision = query.ExecuteScalar(); var current = revision is null ? 0 : Convert.ToInt64(revision, CultureInfo.InvariantCulture);
            if (current != expectedRevision) throw new InvalidOperationException("Les réglages de collecte ont changé. Relisez-les avant d’enregistrer.");
            state.Revision = current + 1; WriteCollectionState(c, tx, contextHash, state); ct.ThrowIfCancellationRequested(); tx.Commit(); return state;
        }, ct);
    }
    private static void ValidateCollectionContext(string value)
    { if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Contexte de collecte invalide."); }
    private static void WriteCollectionState(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string context, LibraryCollectionState state)
        => Execute(c, tx, "INSERT INTO ticket_library_collection VALUES($context,$revision,$json) ON CONFLICT(context_hash) DO UPDATE SET revision=excluded.revision,state_json=excluded.state_json",
            ("$context", context), ("$revision", state.Revision), ("$json", TicketLibraryRules.Serialize(state)));

    /// <summary>Reopens the same durable journal. Never copies a device identity or another capture's proof.</summary>
    public Task ResumeCaptureAsync(string runId, TicketLibraryCaptureContext context, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); RecoverAbandonedCaptures(c, tx);
            if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_capture_runs WHERE status='running'") != 0)
                throw new InvalidOperationException("Une collecte utilise déjà cette bibliothèque.");
            using (var query = Command(c, tx, "SELECT installation,context_hash,reader_signature FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId)))
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(0) != context.Installation || reader.GetString(1) != context.Fingerprint || !TicketLibraryAcquisition.Continues(reader.GetString(2), context.ReaderSignature))
                    throw new InvalidOperationException("Cette reprise ne correspond pas à la source, au lecteur ou aux réglages de date actuels.");
            }
            Execute(c, tx, "UPDATE ticket_library_capture_runs SET status='running',completed_at=NULL,owner_process=$process,owner_start=$start,final_scope_verified=0 WHERE run_id=$run",
                ("$run", runId), ("$process", CaptureProcessId), ("$start", CaptureProcessStart));
            ct.ThrowIfCancellationRequested(); ActiveCaptures[runId] = 0;
            try { tx.Commit(); } catch { ReleaseCapture(runId); throw; }
            return true;
        }, ct);
    }

    /// <summary>Refreshes the lightweight index; unchanged saved bodies and their exact confirmation proofs survive.</summary>
    public Task<IReadOnlyCollection<string>> ReconcileCaptureIndexAsync(string runId, TicketLibraryIndex index, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (!index.AllStatuses || index.ScopeFingerprint.Length is 0 or > 128 || index.Tickets.Count > CaptureTicketLimit)
            throw new InvalidDataException("L’index de reprise ne confirme pas le périmètre de collecte.");
        var seeds = index.Tickets.ToDictionary(s => TicketLibraryRules.Key(TicketLibraryRules.ValidateSeed(s).Ticket), StringComparer.Ordinal);
        return Run<IReadOnlyCollection<string>>(c =>
        {
            using var tx = c.BeginTransaction(); RequireRunningCapture(c, tx, runId);
            var mode = ""; var parent = "";
            using (var query = Command(c, tx, "SELECT installation,scope_hash,mode,parent_run_id FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId)))
            using (var reader = query.ExecuteReader())
            {
                reader.Read(); var installation = reader.GetString(0); var scope = reader.GetString(1);
                mode = reader.GetString(2); parent = reader.GetString(3);
                if (installation != ObjectIdentity.Installation(index.SourceUrl) || seeds.Values.Any(s => ObjectIdentity.Installation(s.Ticket.Url) != installation)
                    || scope.Length > 0 && scope != index.ScopeFingerprint)
                    throw new InvalidOperationException("Le périmètre GLPI a changé. La reprise conserve les contenus mais ne mélange pas les profils ou entités.");
            }
            var old = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var query = Command(c, tx, "SELECT ticket_key,seed_hash FROM ticket_library_capture_entries WHERE run_id=$run", ("$run", runId)))
            using (var reader = query.ExecuteReader()) while (reader.Read()) old.Add(reader.GetString(0), reader.GetString(1));
            // A paused targeted retry must not widen to unrelated tickets. If interrupted
            // before its first index, recover the original failed targets from its parent.
            var allowed = new HashSet<string>(old.Keys, StringComparer.Ordinal);
            if (mode == "retry" && allowed.Count == 0)
            {
                using var query = Command(c, tx, "SELECT ticket_key FROM ticket_library_capture_entries WHERE run_id=$parent AND retryable=1", ("$parent", parent));
                using var reader = query.ExecuteReader(); while (reader.Read()) { ct.ThrowIfCancellationRequested(); allowed.Add(reader.GetString(0)); }
                if (allowed.Count == 0) throw new InvalidOperationException("Les cibles de cet essai ne sont plus présentes. Démarrez explicitement une mise à jour différentielle.");
            }
            var selected = seeds.Where(pair => mode != "retry" || allowed.Contains(pair.Key)).ToArray();
            if (old.Count + selected.Count(pair => !old.ContainsKey(pair.Key)) > CaptureTicketLimit)
                throw new InvalidDataException("Le journal de reprise atteint sa limite. Les contenus sont conservés ; utilisez un nouveau cycle différentiel.");
            var ordinal = 0;
            foreach (var pair in selected)
            {
                ct.ThrowIfCancellationRequested(); var hash = TicketLibraryRules.FromSeed(pair.Value).ContentHash;
                if (!old.TryGetValue(pair.Key, out var previous))
                    Execute(c, tx, "INSERT INTO ticket_library_capture_entries(run_id,ticket_key,ticket_id,ordinal,seed_hash) VALUES($run,$key,$id,$ordinal,$hash)",
                        ("$run", runId), ("$key", pair.Key), ("$id", pair.Value.Ticket.Id), ("$ordinal", ordinal), ("$hash", hash));
                else if (previous != hash)
                {
                    Execute(c, tx, "UPDATE ticket_library_capture_entries SET ordinal=$ordinal,seed_hash=$hash,state='pending',category='changed-during-capture',processed=0,read_count=0,reused=0,failed=0,partial=0,confirmed=0,final_seen=0,retryable=1 WHERE run_id=$run AND ticket_key=$key",
                        ("$run", runId), ("$key", pair.Key), ("$hash", hash), ("$ordinal", ordinal));
                    Execute(c, tx, "DELETE FROM ticket_library_capture_pending WHERE ticket_key=$key AND run_id=$run", ("$key", pair.Key), ("$run", runId));
                }
                else Execute(c, tx, "UPDATE ticket_library_capture_entries SET final_seen=0,ordinal=$ordinal WHERE run_id=$run AND ticket_key=$key", ("$run", runId), ("$key", pair.Key), ("$ordinal", ordinal));
                ordinal++;
            }
            foreach (var missing in old.Keys.Where(key => !seeds.ContainsKey(key)))
                Execute(c, tx, "UPDATE ticket_library_capture_entries SET state='unavailable',category='no-longer-visible',retryable=1,final_seen=0 WHERE run_id=$run AND ticket_key=$key", ("$run", runId), ("$key", missing));
            Execute(c, tx, "UPDATE ticket_library_capture_runs SET observed=$count,scope=$scope,scope_hash=$hash WHERE run_id=$run",
                ("$count", seeds.Count), ("$scope", BoundedScope(index.ScopeDescription)), ("$hash", index.ScopeFingerprint), ("$run", runId));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return selected.Select(pair => pair.Key).ToArray();
        }, ct);
    }
    // A kept read by the previous reader may still await its confirmation (TicketLibraryRules.CanSkip): the final check decides.
    public Task RecordCollectionReuseAsync(string runId, string key, CancellationToken ct = default, bool awaitingConfirmation = false) => UpdateCaptureEntry(runId, key,
        awaitingConfirmation ? "state='reused',category='awaiting-final-check',retryable=0,processed=1,reused=1,read_count=0,failed=0,partial=0,confirmed=0"
            : "state='reused',category='cache-reused',retryable=0,processed=1,reused=1,read_count=0,failed=0,partial=0,confirmed=1", ct);
}
