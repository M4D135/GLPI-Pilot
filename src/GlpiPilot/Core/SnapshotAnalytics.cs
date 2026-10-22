using System.Globalization;

namespace GlpiPilot.Core;

public sealed record ObservedMovement(DateTimeOffset From, DateTimeOffset To, int Appeared, int Absent, int Resolved, int Reopened, int Changed, bool Comparable, string Note)
{
    public int Balance => Appeared - Absent;
}
public sealed record ObservedTransfer(string Dimension, string Before, string After, int Count, string[] Tickets);
public sealed record ObservedCohort(DateTime Date, int Initial, int HorizonDays, int? Active, int? ResolvedOrClosed, int? Absent, int? Unknown, DateTimeOffset? Observation, string Note);
public sealed record ObservedStatusInterval(string Ticket, string Before, string After, DateTimeOffset From, DateTimeOffset To, double MaximumHours, string Note);
public sealed record ObservedStatusDuration(string Ticket, string Status, double MinimumHours, double MaximumHours, int Intervals, string Assumption);
public sealed record ObservedActivity(DateTime Date, int Observations, int ChangesObserved, bool Gap, string Note);
public sealed record MetricVariation(string Metric, string Label, double? Before, double? After, int EnteredContributors, int LeftContributors, int ChangedContributors, double? ExplainedDelta, string Note);
public sealed record StockScenario(int HorizonDays, double? Central, double? Lower, double? Upper, int Sample, string Note);
public sealed record ObservedAnomaly(DateTimeOffset At, string Metric, double Value, double ReferenceMedian, double RobustDeviation, bool Unusual, string Note);
public sealed class SnapshotAnalyticsReport
{
    public string SourceKey { get; init; } = "";
    public int SnapshotCount { get; init; }
    public DateTimeOffset AnalysedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FirstObservation { get; set; }
    public DateTimeOffset? LastObservation { get; set; }
    public double FreshnessHours => LastObservation.HasValue ? Math.Max(0, (AnalysedAt - LastObservation.Value).TotalHours) : double.NaN;
    public int ComparablePairs { get; set; }
    public int BrokenPairs { get; set; }
    public double CoveragePercent { get; set; }
    public List<string> Warnings { get; } = new();
    public List<ObservedMovement> Movements { get; } = new();
    public List<ObservedTransfer> Transfers { get; } = new();
    public List<ObservedCohort> Cohorts { get; } = new();
    public List<ObservedStatusInterval> StatusIntervals { get; } = new();
    public List<ObservedStatusDuration> StatusDurations { get; } = new();
    public List<ObservedActivity> Calendar { get; } = new();
    public List<MetricVariation> Variations { get; } = new();
    public List<StockScenario> Scenarios { get; } = new();
    public List<ObservedAnomaly> Anomalies { get; } = new();
}

/// <summary>Longitudinal calculations use observations only. Missing receipts never become resolutions.</summary>
public static class SnapshotAnalytics
{
    public const int MaximumSnapshots = 120;
    public const int MaximumRows = 2_000_000;
    public static bool Comparable(SnapshotMetadata a, SnapshotMetadata b) => a.CapturedAt < b.CapturedAt
        && a.SourceKey.Length > 0 && a.SourceKey == b.SourceKey && a.Complete && b.Complete
        && a.SearchVerified && b.SearchVerified && a.ScopeVerified && b.ScopeVerified && a.IdentityReliable && b.IdentityReliable
        && a.SchemaFingerprint == b.SchemaFingerprint && a.RulesFingerprint == b.RulesFingerprint;

    public static IReadOnlyList<DashboardHistoryPoint> WithGaps(IEnumerable<DashboardHistoryPoint> points, TimeSpan cadence)
    {
        if (cadence <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cadence));
        var ordered = points.OrderBy(p => p.CapturedAt).ToArray(); var result = new List<DashboardHistoryPoint>();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0 && ordered[i].CapturedAt - ordered[i - 1].CapturedAt > cadence * 1.5)
                result.Add(new(ordered[i - 1].CapturedAt + cadence, null, 0, false, false, "",
                    $"Aucune observation pendant {(ordered[i].CapturedAt - ordered[i - 1].CapturedAt).TotalHours:0.#} h ; cadence attendue {cadence.TotalHours:0.#} h. Aucune interpolation pendant cette rupture."));
            result.Add(ordered[i]);
        }
        return result;
    }

    public static SnapshotAnalyticsReport Analyze(IReadOnlyList<SnapshotDocument> input, TimeSpan cadence, CancellationToken ct = default)
    {
        if (cadence <= TimeSpan.Zero || cadence > TimeSpan.FromDays(30)) throw new ArgumentOutOfRangeException(nameof(cadence));
        if (input.Count > MaximumSnapshots || input.Sum(x => (long)x.Data.Rows.Count) > MaximumRows)
            throw new InvalidOperationException("L’analyse accepte au maximum 120 relevés et 2 millions de lignes cumulées. Réduisez la période.");
        if (input.Select(d => d.Metadata.SourceKey).Distinct(StringComparer.Ordinal).Count() > 1) throw new InvalidOperationException("Une analyse historique doit porter sur une seule recherche et un seul contexte GLPI.");
        var documents = input.OrderBy(x => x.Metadata.CapturedAt).ToArray();
        var report = new SnapshotAnalyticsReport { SnapshotCount = documents.Length, SourceKey = documents.LastOrDefault()?.Metadata.SourceKey ?? "" };
        if (documents.Length == 0) return report;
        report.FirstObservation = documents[0].Metadata.CapturedAt; report.LastObservation = documents[^1].Metadata.CapturedAt;
        if (report.FreshnessHours > cadence.TotalHours * 1.5) report.Warnings.Add($"Dernière observation de cette sélection âgée de {report.FreshnessHours:0.#} h ; cadence attendue {cadence.TotalHours:0.#} h. Aucun état actuel n’est déduit des archives.");
        var transfers = new Dictionary<(string Dimension, string Before, string After), HashSet<string>>();
        var changesByDay = new Dictionary<DateTime, int>();
        for (var i = 1; i < documents.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var before = documents[i - 1]; var after = documents[i];
            var comparable = Comparable(before.Metadata, after.Metadata) && after.Metadata.CapturedAt - before.Metadata.CapturedAt <= cadence * 1.5;
            if (!comparable)
            {
                report.BrokenPairs++;
                report.Movements.Add(new(before.Metadata.CapturedAt, after.Metadata.CapturedAt, 0, 0, 0, 0, 0, false,
                    "Intervalle interrompu : cadence, recherche, règles, colonnes, complétude ou identités incompatibles. Aucun mouvement déduit."));
                continue;
            }
            var comparison = SnapshotStore.Compare(before, after, ct);
            if (!comparison.Compatible) { report.BrokenPairs++; continue; }
            report.ComparablePairs++;
            report.Movements.Add(new(before.Metadata.CapturedAt, after.Metadata.CapturedAt, comparison.Added, comparison.Absent, comparison.ResolutionsObserved,
                comparison.ReopeningsObserved, comparison.Changed, true, "Apparitions et absences du périmètre ; résolutions et réouvertures observées parmi les identifiants présents aux deux dates."));
            var date = after.Metadata.CapturedAt.LocalDateTime.Date;
            changesByDay[date] = changesByDay.GetValueOrDefault(date) + comparison.Added + comparison.Absent + comparison.Changed;
            var oldRows = IdRows(before); var newRows = IdRows(after);
            var groupBefore = DashboardService.ResolveColumns(before.Data.Headers, before.Dashboard).FirstOrDefault(c => c.Role == "group")?.Index ?? -1;
            var groupAfter = DashboardService.ResolveColumns(after.Data.Headers, after.Dashboard).FirstOrDefault(c => c.Role == "group")?.Index ?? -1;
            foreach (var (id, row) in newRows)
            {
                ct.ThrowIfCancellationRequested(); if (!oldRows.TryGetValue(id, out var oldRow)) continue;
                foreach (var (dimension, a, b) in new[] { ("Techniciens", before.Metadata.AssigneeColumn, after.Metadata.AssigneeColumn), ("Groupes", groupBefore, groupAfter) })
                {
                    if (a < 0 || b < 0) continue;
                    var left = Cell(before, oldRow, a); var right = Cell(after, row, b);
                    if (left == right) continue;
                    var key = (dimension, left.Length == 0 ? "Non renseigné" : left, right.Length == 0 ? "Non renseigné" : right);
                    if (!transfers.TryGetValue(key, out var ids)) transfers[key] = ids = new(StringComparer.Ordinal);
                    ids.Add(id);
                }
                var oldStatus = Cell(before, oldRow, before.Metadata.StatusColumn); var newStatus = Cell(after, row, after.Metadata.StatusColumn);
                if (oldStatus.Length > 0 && newStatus.Length > 0 && report.StatusIntervals.Count < 100000)
                    report.StatusIntervals.Add(new(id, oldStatus, newStatus, before.Metadata.CapturedAt, after.Metadata.CapturedAt,
                        (after.Metadata.CapturedAt - before.Metadata.CapturedAt).TotalHours,
                        oldStatus == newStatus ? "Même statut aux deux observations ; des transitions intermédiaires restent possibles. Temps continu non prouvé, borne 0 à la durée de l’intervalle."
                        : "Transition constatée dans cet intervalle, heure exacte inconnue. Temps dans chacun des statuts borné entre 0 et la durée de l’intervalle."));
            }
            if (i == documents.Length - 1) ExplainVariations(before, after, oldRows, newRows, report, ct);
        }
        foreach (var pair in transfers.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key.Before, StringComparer.Ordinal))
            report.Transfers.Add(new(pair.Key.Dimension, pair.Key.Before, pair.Key.After, pair.Value.Count, pair.Value.Order(StringComparer.Ordinal).ToArray()));
        var firstDay = documents[0].Metadata.CapturedAt.LocalDateTime.Date;
        var lastDay = documents[^1].Metadata.CapturedAt.LocalDateTime.Date;
        for (var day = firstDay; day <= lastDay && report.Calendar.Count < 3660; day = day.AddDays(1))
        {
            var count = documents.Count(d => d.Metadata.CapturedAt.LocalDateTime.Date == day);
            report.Calendar.Add(new(day, count, changesByDay.GetValueOrDefault(day), count == 0,
                count == 0 ? "Aucune collecte ce jour : activité inconnue." : "Évolutions constatées à la collecte, pas horodatage des événements GLPI."));
        }
        var duration = (documents[^1].Metadata.CapturedAt - documents[0].Metadata.CapturedAt).TotalHours;
        var covered = report.Movements.Where(m => m.Comparable).Sum(m => (m.To - m.From).TotalHours);
        report.CoveragePercent = duration > 0 ? 100 * covered / duration : 0;
        foreach (var group in report.StatusIntervals.SelectMany(i => new[] { i.Before, i.After }.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Select(status => new { Interval = i, Status = status })).GroupBy(x => (x.Interval.Ticket, x.Status)))
            report.StatusDurations.Add(new(group.Key.Ticket, group.Key.Status,
                group.Where(x => x.Interval.Before.Equals(x.Interval.After, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Interval.MaximumHours),
                group.Sum(x => x.Interval.MaximumHours), group.Count(),
                "Durée conditionnelle sur les seuls intervalles couverts, en supposant aucune transition cachée entre deux observations. Même statut : intervalle entier ; transition : [0 ; durée de l’intervalle] pour chacun des deux statuts. Sans cette hypothèse, la borne minimale reste 0 h."));
        BuildCohorts(documents, cadence, report, ct);
        BuildStatistics(documents, cadence, report);
        if (report.BrokenPairs > 0) report.Warnings.Add($"{report.BrokenPairs} intervalle(s) sans comparabilité ou avec collecte manquante. Les courbes et calculs s’interrompent.");
        if (report.StatusIntervals.Count == 100000) report.Warnings.Add("Détail des intervalles de statut limité aux 100000 premières lignes ; aucun total de durée complète n’est annoncé.");
        report.Warnings.Add("Les transferts comptent les identifiants distincts par couple d’affectations observées. Une affectation multiple reste un libellé entier, sans inventer un transfert individuel.");
        return report;
    }

    private static void BuildCohorts(SnapshotDocument[] docs, TimeSpan cadence, SnapshotAnalyticsReport report, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < docs.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var baseline = docs[i];
            if (baseline.Metadata.State != "Comparable") continue;
            var cohort = IdRows(baseline).Keys.Where(id => !seen.Contains(id)).ToArray(); seen.UnionWith(cohort);
            if (cohort.Length == 0) continue;
            foreach (var horizon in new[] { 1, 7, 30 })
            {
                var target = baseline.Metadata.CapturedAt.AddDays(horizon);
                var index = Array.FindIndex(docs, i, d => d.Metadata.CapturedAt >= target && d.Metadata.CapturedAt <= target + cadence);
                var continuous = index >= 0 && Enumerable.Range(i + 1, index - i).All(j => Comparable(docs[j - 1].Metadata, docs[j].Metadata)
                    && docs[j].Metadata.CapturedAt - docs[j - 1].Metadata.CapturedAt <= cadence * 1.5);
                if (!continuous)
                { report.Cohorts.Add(new(baseline.Metadata.CapturedAt.LocalDateTime.Date, cohort.Length, horizon, null, null, null, null, null, "Horizon non couvert par une chaîne comparable ; aucune valeur imputée.")); continue; }
                var observation = docs[index]; var rows = IdRows(observation); int active = 0, resolved = 0, absent = 0, unknown = 0;
                foreach (var id in cohort)
                    if (!rows.TryGetValue(id, out var row)) absent++;
                    else { var status = DashboardService.Status(DashboardService.NormalizeValue(Cell(observation, row, observation.Metadata.StatusColumn), "status", observation.Metadata.StatusColumn >= 0 ? observation.Data.Headers[observation.Metadata.StatusColumn] : "", observation.Dashboard)); if (status is >= 1 and <= 4) active++; else if (status is 5 or 6) resolved++; else unknown++; }
                report.Cohorts.Add(new(baseline.Metadata.CapturedAt.LocalDateTime.Date, cohort.Length, horizon, active, resolved, absent, unknown, observation.Metadata.CapturedAt,
                    "Cohorte de première observation dans les archives sélectionnées, pas cohorte de création supposée. Les absents gardent un statut inconnu."));
            }
        }
    }

    private static void ExplainVariations(SnapshotDocument before, SnapshotDocument after, Dictionary<string, int> oldRows, Dictionary<string, int> newRows, SnapshotAnalyticsReport report, CancellationToken ct)
    {
        var a = DashboardService.Calculate(before.Data, before.Dashboard, before.Metadata.CapturedAt, ct);
        var b = DashboardService.Calculate(after.Data, after.Dashboard, after.Metadata.CapturedAt, ct);
        QueryService.AppendDashboardQueries(a, before.Data, before.Dashboard, before.Queries, ct);
        QueryService.AppendDashboardQueries(b, after.Data, after.Dashboard, after.Queries, ct);
        foreach (var metric in b.Metrics)
        {
            var previous = a.Metric(metric.Key); if (previous?.Value == null || metric.Value == null) continue;
            var oldContributors = previous.Rows.Select(row => NormalizeTicketId(Cell(before, row, before.Metadata.IdColumn))).ToHashSet(StringComparer.Ordinal);
            var newContributors = metric.Rows.Select(row => NormalizeTicketId(Cell(after, row, after.Metadata.IdColumn))).ToHashSet(StringComparer.Ordinal);
            var entered = newContributors.Except(oldContributors).ToArray(); var left = oldContributors.Except(newContributors).ToArray();
            var changedMembership = entered.Count(oldRows.ContainsKey) + left.Count(newRows.ContainsKey);
            var additive = (metric.Unit is "tickets" or "lignes") && metric.Value == newContributors.Count && previous.Value == oldContributors.Count;
            report.Variations.Add(new(metric.Key, metric.Label, previous.Value, metric.Value, entered.Length, left.Length, changedMembership,
                additive ? entered.Length - left.Length : null,
                additive ? $"Décomposition exacte : +{entered.Length} contributeurs −{left.Length}; {changedMembership} changements de cohorte parmi les tickets communs. Le vieillissement calendaire participe aux seuils."
                : "Agrégat non additif : valeurs recalculées sur chaque échantillon. Les changements de contributeurs sont affichés sans attribuer artificiellement l’écart à chaque ticket."));
        }
    }

    private static void BuildStatistics(SnapshotDocument[] docs, TimeSpan cadence, SnapshotAnalyticsReport report)
    {
        var start = docs.Length - 1;
        while (start > 0 && Comparable(docs[start - 1].Metadata, docs[start].Metadata)
            && docs[start].Metadata.CapturedAt - docs[start - 1].Metadata.CapturedAt <= cadence * 1.5) start--;
        var continuous = docs.Skip(start).Where(d => d.Metadata.State == "Comparable").ToArray();
        foreach (var key in new[] { "total", "active", "overdue", "high" })
        {
            var referenceWindow = new Queue<double>();
            foreach (var sample in continuous)
            {
                if (sample.Metadata.Metrics.GetValueOrDefault(key) is not double value || !double.IsFinite(value)) { referenceWindow.Clear(); continue; }
                if (referenceWindow.Count >= 7)
                {
                    var reference = referenceWindow.Order().ToArray(); var median = Quantile(reference, .5);
                    var mad = Quantile(reference.Select(v => Math.Abs(v - median)).Order().ToArray(), .5); var scale = 1.4826 * mad;
                    var unusual = scale == 0 ? value != median : Math.Abs(value - median) > 3 * scale;
                    report.Anomalies.Add(new(sample.Metadata.CapturedAt, key, value, median, scale, unusual,
                        $"Référence : {reference.Length} observations comparables précédentes, médiane et écart robuste 1,4826 × MAD ; seuil 3 écarts. Signal exploratoire, pas diagnostic causal."));
                }
                referenceWindow.Enqueue(value); if (referenceWindow.Count > 30) referenceWindow.Dequeue();
            }
        }
        var stock = continuous.Reverse().TakeWhile(d => d.Metadata.Metrics.GetValueOrDefault("total") is double v && double.IsFinite(v)).Take(60).Reverse().ToArray();
        foreach (var horizon in new[] { 1, 7, 30 })
        {
            if (stock.Length < 8 || (stock[^1].Metadata.CapturedAt - stock[0].Metadata.CapturedAt).TotalDays < 3)
            { report.Scenarios.Add(new(horizon, null, null, null, stock.Length, "Au moins 8 observations comparables continues couvrant 3 jours sont nécessaires.")); continue; }
            var x = stock.Select(d => (d.Metadata.CapturedAt - stock[0].Metadata.CapturedAt).TotalDays).ToArray();
            var y = stock.Select(d => d.Metadata.Metrics["total"]!.Value).ToArray(); var xm = x.Average(); var ym = y.Average();
            var sxx = x.Sum(v => (v - xm) * (v - xm)); var slope = x.Select((v, i) => (v - xm) * (y[i] - ym)).Sum() / sxx;
            var residual = Math.Sqrt(x.Select((v, i) => Math.Pow(y[i] - (ym + slope * (v - xm)), 2)).Sum() / (stock.Length - 2));
            var target = x[^1] + horizon; var projected = ym + slope * (target - xm); var central = Math.Max(0, projected);
            var band = Student95(stock.Length - 2) * residual * Math.Sqrt(1 + 1d / stock.Length + Math.Pow(target - xm, 2) / sxx);
            report.Scenarios.Add(new(horizon, central, Math.Max(0, projected - band), Math.Max(0, projected + band), stock.Length,
                "Scénario linéaire si la tendance se prolonge ; bande prédictive du modèle linéaire à 95 % (Student) sous hypothèses de résidus indépendants et de variance stable. Aucun changement d’organisation ni saisonnalité modélisé."));
        }
    }
    private static double Student95(int freedom)
    {
        double[] values = { 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042 };
        return freedom <= 30 ? values[Math.Max(1, freedom) - 1] : freedom <= 40 ? 2.042 : freedom <= 60 ? 2.021 : 2.0;
    }
    private static double Quantile(double[] values, double q)
    { var rank = (values.Length - 1) * q; var floor = (int)Math.Floor(rank); return values[floor] + (values[(int)Math.Ceiling(rank)] - values[floor]) * (rank - floor); }
    private static Dictionary<string, int> IdRows(SnapshotDocument d)
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < d.Data.Rows.Count; i++)
        { var id = NormalizeTicketId(Cell(d, i, d.Metadata.IdColumn)); if (id.Length > 0) rows.TryAdd(id, i); }
        return rows;
    }
    public static string NormalizeTicketId(string value)
    { try { return TicketReferenceResolver.NormalizeId(value); } catch (InvalidOperationException) { return ""; } }
    private static string Cell(SnapshotDocument d, int row, int col) => row >= 0 && row < d.Data.Rows.Count && col >= 0 && col < d.Data.Rows[row].Length ? d.Data.Rows[row][col] ?? "" : "";
}
