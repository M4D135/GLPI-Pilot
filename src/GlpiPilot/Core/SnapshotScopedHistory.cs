using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Replays a person's identity and actual filter definitions on comparable archives; current row numbers are never reused.</summary>
public sealed class SnapshotScopedHistory
{
    public const int MaximumSnapshots = 120, MaximumScopes = 64, MaximumEvaluatedRows = 2_000_000, MaximumMetricPoints = 250_000;
    private readonly SnapshotStore _store;
    private readonly string _sourceKey, _schema, _rules, _technicianKey;
    private readonly DateTimeOffset _asOf;
    private readonly bool _currentVerified;
    private readonly TimeSpan _cadence;
    private readonly (string Key, string Label, string Unit)[] _metrics;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<DashboardHistorySeries>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private sealed record Observation(SnapshotMetadata Metadata, int Rows, Dictionary<string, (double? Value, string Unit)>? Values, string Note, bool BudgetLimited = false);

    public SnapshotScopedHistory(SnapshotStore store, ExtractionResult current, AppSettings settings,
        DashboardSnapshot baseline, string technicianKey = "", CancellationToken ct = default)
    {
        _store = store; _sourceKey = SnapshotStore.SourceKey(current); _schema = SnapshotStore.SchemaFingerprint(current.Headers);
        _rules = SnapshotStore.RulesFingerprint(settings); _asOf = current.ExtractedAt;
        _technicianKey = string.IsNullOrWhiteSpace(technicianKey) ? "" : TechnicianDashboardService.IdentityKey(technicianKey);
        _cadence = TimeSpan.FromHours(settings.Dashboard.HistoryCadenceHours);
        _metrics = baseline.Metrics.Select(m => (m.Key, m.Label, m.Unit)).DistinctBy(m => m.Key).Take(512).ToArray();
        _currentVerified = _sourceKey.Length > 0 && current.Complete && (!current.ExpectedTotal.HasValue || current.ExpectedTotal == current.Rows.Count)
            && current.Provenance is { SearchVerified: true, ScopeVerified: true } && ReliableIds(current, settings.Dashboard, ct);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<DashboardHistorySeries>> Calculate(IReadOnlyList<DashboardHistoryScope> requests, CancellationToken ct = default)
    {
        if (requests.Count > MaximumScopes || requests.Any(r => r is null || string.IsNullOrWhiteSpace(r.Key) || r.Key.Length > 200)
            || requests.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            throw new InvalidDataException("L’historique accepte au maximum 64 périmètres distincts par recalcul.");
        foreach (var request in requests) { DashboardScopeFilter.ValidateList(request.CommonFilters); DashboardScopeFilter.ValidateList(request.WidgetFilters); }
        _serial.Wait(ct);
        try { return CalculateCore(requests, ct); }
        finally { _serial.Release(); }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<DashboardHistorySeries>> CalculateCore(IReadOnlyList<DashboardHistoryScope> requests, CancellationToken ct)
    {
        var signatures = requests.ToDictionary(r => r.Key, Signature, StringComparer.Ordinal);
        // Keep this request's cached results alive even when later scopes evict them from the bounded cache.
        var cached = signatures.Values.Distinct(StringComparer.Ordinal).Where(_cache.ContainsKey)
            .ToDictionary(key => key, key => _cache[key], StringComparer.Ordinal);
        var pending = requests.DistinctBy(r => signatures[r.Key]).Where(r => !cached.ContainsKey(signatures[r.Key])).ToArray();
        var generated = new Dictionary<string, IReadOnlyList<DashboardHistorySeries>>(StringComparer.Ordinal);
        if (pending.Length > 0)
        {
            var entries = _sourceKey.Length == 0 ? Array.Empty<SnapshotMetadata>()
                : _store.List().Where(m => m.SourceKey == _sourceKey && m.CapturedAt <= _asOf).Take(MaximumSnapshots).ToArray();
            var observations = pending.ToDictionary(r => signatures[r.Key], _ => new List<Observation>(), StringComparer.Ordinal);
            long evaluatedRows = 0; var metricPoints = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested(); var incompatibility = Incompatibility(entry);
                if (incompatibility.Length > 0) { AddGap(incompatibility); continue; }
                var cost = (long)Math.Max(1, entry.Rows) * (1 + pending.Length);
                if (evaluatedRows + cost > MaximumEvaluatedRows || metricPoints + _metrics.Length * pending.Length > MaximumMetricPoints)
                { AddGap("Budget historique atteint : réduire les périmètres distincts ou le volume des relevés. Ce point reste inconnu.", true); continue; }
                evaluatedRows += cost; metricPoints += _metrics.Length * pending.Length;
                SnapshotDocument document; DashboardSnapshot historical;
                try
                {
                    document = _store.Load(entry.Id, ct);
                    historical = _technicianKey.Length == 0 ? DashboardService.Calculate(document.Data, document.Dashboard, entry.CapturedAt, ct)
                        : TechnicianDashboardService.CalculateForTechnician(document.Data, document.Dashboard, _technicianKey, entry.CapturedAt, ct);
                    Append(historical, document, ct);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
                { AddGap("Archive non exploitable pour ce périmètre : " + error.Message); continue; }
                foreach (var request in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var common = DashboardService.ResolveScope(historical, request.CommonFilters, ct);
                        var rows = DashboardService.ResolveScope(historical, request.WidgetFilters, ct).Intersect(common).Order().ToArray();
                        var scoped = request.CommonFilters.Count == 0 && request.WidgetFilters.Count == 0 ? historical
                            : DashboardService.CalculateSubset(document.Data, document.Dashboard, rows, "Historique du même périmètre", entry.CapturedAt, ct);
                        if (!ReferenceEquals(scoped, historical)) Append(scoped, document, ct);
                        observations[signatures[request.Key]].Add(new(entry, scoped.RowCount,
                            scoped.Metrics.ToDictionary(m => m.Key, m => (m.Value, m.Unit), StringComparer.Ordinal),
                            "Même identité personnelle et mêmes définitions de filtres rejouées sur cette archive ; valeurs calculées à sa date d’observation."));
                    }
                    catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
                    { observations[signatures[request.Key]].Add(new(entry, 0, null, "Définition de périmètre indisponible dans l’archive : " + error.Message)); }
                }
                void AddGap(string note, bool limited = false)
                { foreach (var list in observations.Values) list.Add(new(entry, 0, null, note, limited)); }
            }
            foreach (var pair in observations)
            {
                ct.ThrowIfCancellationRequested(); var ordered = pair.Value.OrderBy(o => o.Metadata.CapturedAt).ToArray();
                // Share unavailable points across metrics to keep bounded replays small even when many archives are incompatible.
                var gaps = ordered.Where(o => o.Values is null).ToDictionary(o => o.Metadata.Id,
                    o => new DashboardHistoryPoint(o.Metadata.CapturedAt, null, 0, o.Metadata.Complete, false, o.Metadata.Id, o.Note));
                var series = new List<DashboardHistorySeries>();
                foreach (var metric in _metrics)
                {
                    var points = ordered.Select(o =>
                    {
                        if (gaps.TryGetValue(o.Metadata.Id, out var gap)) return gap;
                        var found = o.Values!.TryGetValue(metric.Key, out var value) && value.Value is double number && double.IsFinite(number) && value.Unit == metric.Unit;
                        return new DashboardHistoryPoint(o.Metadata.CapturedAt, found ? value.Value : null, o.Rows, o.Metadata.Complete, found, o.Metadata.Id,
                            found ? o.Note : "Cet indicateur ou son unité est indisponible dans cette archive. Les preuves de localisation et les dénominateurs doivent avoir été conservés lorsqu’ils sont nécessaires.");
                    }).ToArray();
                    series.Add(new(metric.Key, metric.Label + " · historique du périmètre", metric.Unit, SnapshotAnalytics.WithGaps(points, _cadence),
                        $"Périmètre recalculé dans chaque archive, sans réutiliser les indices ou la liste des tickets courants. Identité : {(_technicianKey.Length == 0 ? "équipe entière" : _technicianKey)}. "
                        + $"Au plus {MaximumSnapshots} archives à la date du relevé courant, {MaximumEvaluatedRows:N0} lignes de calcul et {MaximumMetricPoints:N0} valeurs par recalcul. Les limites, filtres indisponibles, données absentes et archives incompatibles créent des ruptures. Les localisations et effectifs proviennent des preuves conservées dans chaque archive."));
                }
                generated[pair.Key] = series;
                if (!ordered.Any(o => o.BudgetLimited))
                {
                    while (_cache.Count >= 8 && _cacheOrder.TryDequeue(out var oldest)) _cache.Remove(oldest);
                    _cache[pair.Key] = series; _cacheOrder.Enqueue(pair.Key);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        return requests.ToDictionary(r => r.Key, r => generated.TryGetValue(signatures[r.Key], out var fresh) ? fresh : cached[signatures[r.Key]], StringComparer.Ordinal);
    }

    private string Incompatibility(SnapshotMetadata metadata)
    {
        var issues = new List<string>();
        if (!_currentVerified) issues.Add("Relevé courant incomplet, contexte non confirmé ou identifiants non fiables");
        if (!metadata.Complete || !metadata.SearchVerified || !metadata.ScopeVerified || !metadata.IdentityReliable) issues.Add("Archive incomplète ou identité/périmètre non confirmé");
        if (metadata.SchemaFingerprint != _schema) issues.Add("Colonnes différentes");
        if (metadata.RulesFingerprint != _rules) issues.Add("Règles de calcul, de requêtes ou de localisation différentes");
        return string.Join(" · ", issues);
    }
    private static string Signature(DashboardHistoryScope request) => JsonSerializer.Serialize(new
    {
        common = request.CommonFilters.Select(f => f.Copy()).OrderBy(f => f.Kind).ThenBy(f => f.SourceKey).ThenBy(f => f.Value).ThenBy(f => f.Value2).ThenBy(f => f.Exclude),
        widget = request.WidgetFilters.Select(f => f.Copy()).OrderBy(f => f.Kind).ThenBy(f => f.SourceKey).ThenBy(f => f.Value).ThenBy(f => f.Value2).ThenBy(f => f.Exclude)
    });
    private static void Append(DashboardSnapshot snapshot, SnapshotDocument document, CancellationToken ct)
    {
        SnapshotLocationEvidence.Append(snapshot, document, ct);
        QueryService.AppendDashboardQueries(snapshot, document.Data, document.Dashboard, document.Queries, ct);
        DashboardService.RefreshWeatherProfiles(snapshot, document.Dashboard);
    }
    private static bool ReliableIds(ExtractionResult data, DashboardSettings settings, CancellationToken ct)
    {
        if (DashboardService.ResolveColumns(data.Headers, settings).Single(c => c.Role == "id").Index is not int column) return false;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 0; row < data.Rows.Count; row++)
        {
            if (row % 256 == 0) ct.ThrowIfCancellationRequested();
            var id = SnapshotAnalytics.NormalizeTicketId(column < data.Rows[row].Length ? data.Rows[row][column] : "");
            if (id.Length == 0 || !unique.Add(id)) return false;
        }
        return true;
    }
}
