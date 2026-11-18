using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Core;

public sealed partial class TicketLibraryStore
{
    // A workstation-local immutable selection. Fresh evidence can change; membership and inventory provenance cannot.
    private const string CollectionPlanSchema = """
        CREATE TABLE IF NOT EXISTS ticket_library_collection_plans (
          run_id TEXT PRIMARY KEY REFERENCES ticket_library_capture_runs(run_id) ON DELETE CASCADE,
          context_hash TEXT NOT NULL, installation TEXT NOT NULL, fingerprint TEXT NOT NULL,
          plan_json TEXT NOT NULL, verified_at TEXT);
        CREATE TABLE IF NOT EXISTS ticket_library_collection_plan_entries (
          run_id TEXT NOT NULL REFERENCES ticket_library_collection_plans(run_id) ON DELETE CASCADE,
          ticket_key TEXT NOT NULL, ordinal INTEGER NOT NULL, seed_json TEXT NOT NULL,
          evidence_seed_json TEXT, evidence_present INTEGER,
          PRIMARY KEY(run_id,ticket_key));
        CREATE INDEX IF NOT EXISTS ticket_library_collection_plan_order ON ticket_library_collection_plan_entries(run_id,ordinal);
        CREATE TABLE IF NOT EXISTS ticket_library_collection_window_progress (
          run_id TEXT PRIMARY KEY REFERENCES ticket_library_collection_plans(run_id) ON DELETE CASCADE,
          next_ordinal INTEGER NOT NULL DEFAULT 0, initialized INTEGER NOT NULL DEFAULT 0);
        """;

    public Task SaveCollectionPlanAsync(string runId, TicketLibraryCaptureContext context, LibraryCollectionPlan plan, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context); ValidateCollectionPlan(plan);
        if (plan.Installation != context.Installation) throw new InvalidDataException("Le périmètre appartient à une autre installation GLPI.");
        var header = plan.Header(); header.VerifiedAt = null; header.EvidenceCount = 0; header.NextOrdinal = 0;
        var tickets = plan.Tickets.ToArray();
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionPlanContext(c, tx, runId, context);
            using (var existing = Command(c, tx, "SELECT fingerprint FROM ticket_library_collection_plans WHERE run_id=$run", ("$run", runId)))
                if (existing.ExecuteScalar() is string fingerprint)
                {
                    if (fingerprint != header.Fingerprint) throw new InvalidOperationException("Le périmètre de cet essai est déjà figé. Une nouvelle sélection nécessite un nouvel essai.");
                    tx.Commit(); return true;
                }
            Execute(c, tx, "INSERT INTO ticket_library_collection_plans(run_id,context_hash,installation,fingerprint,plan_json) VALUES($run,$context,$installation,$fingerprint,$json)",
                ("$run", runId), ("$context", context.Fingerprint), ("$installation", context.Installation),
                ("$fingerprint", header.Fingerprint), ("$json", JsonSerializer.Serialize(header, TicketLibraryRules.Json)));
            using var insert = Command(c, tx, "INSERT INTO ticket_library_collection_plan_entries(run_id,ticket_key,ordinal,seed_json) VALUES($run,$key,$ordinal,$seed)",
                ("$run", runId), ("$key", ""), ("$ordinal", 0), ("$seed", ""));
            for (var ordinal = 0; ordinal < tickets.Length; ordinal++)
            {
                ct.ThrowIfCancellationRequested(); var seed = tickets[ordinal];
                insert.Parameters["$key"].Value = TicketLibraryRules.Key(seed.Ticket);
                insert.Parameters["$ordinal"].Value = ordinal;
                insert.Parameters["$seed"].Value = JsonSerializer.Serialize(seed, TicketLibraryRules.Json);
                insert.ExecuteNonQuery();
            }
            Execute(c, tx, "INSERT INTO ticket_library_collection_window_progress(run_id) VALUES($run)", ("$run", runId));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    public Task<LibraryCollectionPlan?> CollectionPlanAsync(string runId, TicketLibraryCaptureContext context, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionPlanContext(c, tx, runId, context);
            LibraryCollectionPlan? plan;
            using (var command = Command(c, tx, "SELECT plan_json,verified_at,COALESCE((SELECT next_ordinal FROM ticket_library_collection_window_progress w WHERE w.run_id=ticket_library_collection_plans.run_id),0) FROM ticket_library_collection_plans WHERE run_id=$run AND context_hash=$context AND installation=$installation",
                ("$run", runId), ("$context", context.Fingerprint), ("$installation", context.Installation)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) { tx.Commit(); return null; }
                plan = JsonSerializer.Deserialize<LibraryCollectionPlan>(reader.GetString(0), TicketLibraryRules.Json)
                    ?? throw new InvalidDataException("Le périmètre de reprise est illisible.");
                plan.VerifiedAt = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                plan.NextOrdinal = reader.GetInt32(2);
            }
            plan.Tickets.Clear(); plan.EvidenceCount = 0;
            using (var command = Command(c, tx, "SELECT seed_json,evidence_present FROM ticket_library_collection_plan_entries WHERE run_id=$run ORDER BY ordinal", ("$run", runId)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    plan.Tickets.Add(TicketLibraryRules.ValidateSeed(JsonSerializer.Deserialize<TicketLibrarySeed>(reader.GetString(0), TicketLibraryRules.Json)!));
                    if (!reader.IsDBNull(1) && reader.GetBoolean(1)) plan.EvidenceCount++;
                }
            ValidateCollectionPlan(plan); tx.Commit(); return plan;
        }, ct);
    }

    /// <summary>A replacement observation of the same fixed worklist. Missing targets never acquire freshness from an older observation.</summary>
    public Task UpdateCollectionPlanEvidenceAsync(string runId, TicketLibraryCaptureContext context,
        IReadOnlyList<TicketLibrarySeed> seeds, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context);
        var evidence = CollectionSeeds(seeds); var verifiedAt = evidence.Count == 0 ? DateTimeOffset.UtcNow : evidence.Values.Max(seed => seed.ObservedAt);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionPlanContext(c, tx, runId, context);
            if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_collection_plans WHERE run_id=$run AND context_hash=$context", ("$run", runId), ("$context", context.Fingerprint)) != 1)
                throw new InvalidOperationException("Le périmètre ciblé de cet essai est absent.");
            var members = new HashSet<string>(StringComparer.Ordinal);
            using (var command = Command(c, tx, "SELECT ticket_key FROM ticket_library_collection_plan_entries WHERE run_id=$run", ("$run", runId)))
            using (var reader = command.ExecuteReader()) while (reader.Read()) { ct.ThrowIfCancellationRequested(); members.Add(reader.GetString(0)); }
            if (evidence.Keys.Any(key => !members.Contains(key))
                || evidence.Values.Any(seed => ObjectIdentity.Installation(seed.Ticket.Url) != context.Installation))
                throw new InvalidDataException("Le contrôle ciblé contient un ticket absent du périmètre figé.");
            Execute(c, tx, "UPDATE ticket_library_collection_plan_entries SET evidence_seed_json=NULL,evidence_present=0 WHERE run_id=$run", ("$run", runId));
            using var update = Command(c, tx, "UPDATE ticket_library_collection_plan_entries SET evidence_seed_json=$seed,evidence_present=1 WHERE run_id=$run AND ticket_key=$key",
                ("$run", runId), ("$seed", ""), ("$key", ""));
            foreach (var pair in evidence)
            {
                ct.ThrowIfCancellationRequested(); update.Parameters["$key"].Value = pair.Key;
                update.Parameters["$seed"].Value = JsonSerializer.Serialize(pair.Value, TicketLibraryRules.Json); update.ExecuteNonQuery();
            }
            Execute(c, tx, "UPDATE ticket_library_collection_plans SET verified_at=$date WHERE run_id=$run", ("$run", runId), ("$date", verifiedAt.ToString("O")));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return true;
        }, ct);
    }

    /// <summary>Bounded parameter batches, with no ticket body deserialization or 100,000-ticket ceiling.</summary>
    public Task<LibraryCollectionCoverage> CollectionCoverageAsync(IReadOnlyList<TicketLibrarySeed> seeds,
        string readerSignature, CancellationToken ct = default)
    {
        var candidates = CollectionSeeds(seeds);
        if (string.IsNullOrWhiteSpace(readerSignature) || readerSignature.Length > 200) throw new InvalidDataException("Lecteur de collecte invalide.");
        return Run(c =>
        {
            var coverage = new LibraryCollectionCoverage();
            foreach (var batch in candidates.Chunk(500))
            {
                ct.ThrowIfCancellationRequested();
                var missing = batch.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var parameters = batch.Select((pair, index) => (Name: "$key" + index, Value: (object?)pair.Key)).ToArray();
                using var command = Command(c, null, "SELECT " + CollectionMetadataSql + " FROM ticket_library_tickets t WHERE t.ticket_key IN ("
                    + string.Join(",", parameters.Select(parameter => parameter.Name)) + ")", parameters);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested(); var stored = ReadCollectionMetadata(reader);
                    coverage.Add(LibraryCollectionScopePlanner.Classify(missing[stored.Key], stored, readerSignature)); missing.Remove(stored.Key);
                }
                foreach (var _ in missing) coverage.Add("missing");
            }
            return coverage;
        }, ct);
    }

    public Task<LibraryCollectionCoverage> CollectionPlanCoverageAsync(string runId,
        TicketLibraryCaptureContext context, CancellationToken ct = default)
    {
        ValidateRunId(runId); ValidateCaptureContext(context);
        return Run(c =>
        {
            using var tx = c.BeginTransaction(); Execute(c, tx, CollectionPlanSchema);
            RequireCollectionPlanContext(c, tx, runId, context);
            if (ScalarLong(c, tx, "SELECT COUNT(*) FROM ticket_library_collection_plans WHERE run_id=$run AND context_hash=$context", ("$run", runId), ("$context", context.Fingerprint)) != 1)
                throw new InvalidOperationException("Le périmètre ciblé de cet essai est absent.");
            var coverage = new LibraryCollectionCoverage();
            using (var command = Command(c, tx, "SELECT " + CollectionMetadataSql + ",COALESCE(p.evidence_seed_json,p.seed_json),p.evidence_present"
                + " FROM ticket_library_collection_plan_entries p LEFT JOIN ticket_library_tickets t ON t.ticket_key=p.ticket_key WHERE p.run_id=$run ORDER BY p.ordinal", ("$run", runId)))
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

    // SQLite extracts only small metadata values. Neither caller materializes the large detail body.
    private const string CollectionMetadataSql = """
        t.ticket_key,t.seed_json,t.detail_complete,t.details_current,t.has_content,t.content_hash,
        json_extract(t.document_json,'$.readerSignature'),json_extract(t.document_json,'$.readAt'),
        json_extract(t.document_json,'$.detailModifiedRaw'),json_extract(t.document_json,'$.detailModifiedAt'),
        json_extract(t.document_json,'$.confirmationPending'),json_extract(t.document_json,'$.confirmationId'),
        json_extract(t.document_json,'$.confirmedContentHash'),t.local_revision,
        EXISTS(SELECT 1 FROM ticket_library_conflicts cf WHERE cf.ticket_key=t.ticket_key)
        """;

    private static TicketLibraryDocument ReadCollectionMetadata(SqliteDataReader reader)
    {
        string Text(int column) => reader.IsDBNull(column) ? "" : reader.GetString(column);
        bool Flag(int column) => !reader.IsDBNull(column) && reader.GetBoolean(column);
        DateTimeOffset? Date(int column) => DateTimeOffset.TryParse(Text(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
        return new TicketLibraryDocument
        {
            Key = reader.GetString(0), Seed = TicketLibraryRules.ValidateSeed(JsonSerializer.Deserialize<TicketLibrarySeed>(reader.GetString(1), TicketLibraryRules.Json)!),
            DetailComplete = Flag(2), DetailsCurrent = Flag(3), Detail = Flag(4) ? new TicketDetailResult() : null,
            ContentHash = Text(5), ReaderSignature = Text(6), ReadAt = Date(7), DetailModifiedRaw = Text(8), DetailModifiedAt = Date(9),
            ConfirmationPending = Flag(10), ConfirmationId = Text(11), ConfirmedContentHash = Text(12), LocalRevision = reader.GetInt64(13), Conflicts = Flag(14) ? 1 : 0
        };
    }

    private static Dictionary<string, TicketLibrarySeed> CollectionSeeds(IReadOnlyList<TicketLibrarySeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        if (seeds.Count > CaptureTicketLimit) throw new InvalidDataException("Le périmètre ciblé dépasse sa limite de tickets.");
        var result = new Dictionary<string, TicketLibrarySeed>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            TicketLibraryRules.ValidateSeed(seed); var key = TicketLibraryRules.Key(seed.Ticket);
            if (!result.TryAdd(key, seed)) throw new InvalidDataException("Le périmètre ciblé contient plusieurs fois le même ticket.");
        }
        return result;
    }

    private static void ValidateCollectionPlan(LibraryCollectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); plan.Scope.Validate();
        if (plan.Version != 1 || !plan.Scope.Enabled || plan.SourceFingerprint.Length != 64 || !plan.SourceFingerprint.All(char.IsAsciiHexDigit)
            || plan.Fingerprint.Length != 64 || !plan.Fingerprint.All(char.IsAsciiHexDigit) || plan.Tickets is null
            || plan.Selected != plan.Tickets.Count || plan.InventoryCount < plan.Selected || plan.InventoryCount > CaptureTicketLimit
            || plan.Selected < 0 || plan.NextOrdinal < 0 || plan.NextOrdinal > plan.Selected
            || plan.InvalidRows < 0 || plan.DuplicateRows < 0 || plan.AmbiguousRows < 0 || plan.UnknownDates < 0
            || plan.SourceUrl.Length > 8192 || ObjectIdentity.Installation(plan.SourceUrl) != plan.Installation
            || plan.Warnings is null || plan.Warnings.Count > 100 || plan.Warnings.Any(value => value is null || value.Length > 2000))
            throw new InvalidDataException("Le périmètre ciblé enregistré est invalide.");
        var seeds = CollectionSeeds(plan.Tickets);
        if (seeds.Values.Any(seed => ObjectIdentity.Installation(seed.Ticket.Url) != plan.Installation)
            || plan.Fingerprint != LibraryCollectionScopePlanner.Fingerprint(plan))
            throw new InvalidDataException("La sélection ou la provenance du périmètre ciblé a changé.");
    }

    private static void RequireCollectionPlanContext(SqliteConnection c, SqliteTransaction tx, string runId, TicketLibraryCaptureContext context)
    {
        using var command = Command(c, tx, "SELECT installation,context_hash,reader_signature FROM ticket_library_capture_runs WHERE run_id=$run", ("$run", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != context.Installation || reader.GetString(1) != context.Fingerprint
            || !TicketLibraryAcquisition.Continues(reader.GetString(2), context.ReaderSignature))
            throw new InvalidOperationException("Le contexte GLPI ne correspond pas au périmètre de cet essai.");
    }
}
