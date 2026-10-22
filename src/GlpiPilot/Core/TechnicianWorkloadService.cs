namespace GlpiPilot.Core;

public sealed record TechnicianWorkloadMeasure(string Key, string Label, int? Count, int Sample, int Population,
    int[] Rows, string Explanation)
{
    public string Coverage => Count.HasValue
        ? $"{Sample:N0} / {Population:N0} lignes évaluables" : "Donnée indisponible dans ce périmètre";
}

public sealed class TechnicianWorkloadEntry
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int[] Rows { get; init; } = Array.Empty<int>();
    public int UnknownStatusCount { get; init; }
    public int MultipleAssignmentCount { get; init; }
    public double? MeanAgeDays { get; init; }
    public double? MeanInactivityDays { get; init; }
    public IReadOnlyDictionary<string, TechnicianWorkloadMeasure> Measures { get; init; }
        = new Dictionary<string, TechnicianWorkloadMeasure>();
    public TechnicianWorkloadMeasure Measure(string key) => Measures[key];
    public int? ActiveCount => Measure("active").Count;
    public int? PendingCount => Measure("pending").Count;
    public int? HighCount => Measure("high").Count;
    public int? OverdueCount => Measure("overdue").Count;
    public int? OldCount => Measure("old").Count;
    public int? StaleCount => Measure("stale").Count;
}

public sealed class TechnicianWorkloadSnapshot
{
    public TechnicianRoster Roster { get; init; } = new(false, "", "", Array.Empty<TechnicianEntry>());
    public DashboardSnapshot Global { get; init; } = new();
    public IReadOnlyList<TechnicianWorkloadEntry> Technicians { get; init; } = Array.Empty<TechnicianWorkloadEntry>();
    public int[] AssignedRows { get; init; } = Array.Empty<int>();
    public int[] AssignedActiveRows { get; init; } = Array.Empty<int>();
    public int[] UnassignedRows { get; init; } = Array.Empty<int>();
    public int[] MultipleAssignmentRows { get; init; } = Array.Empty<int>();
}

/// <summary>One canonical dashboard calculation, then intersections on source row indexes for every assignment.</summary>
public static class TechnicianWorkloadService
{
    private static readonly string[] MeasureKeys = { "total", "active", "pending", "high", "overdue", "old", "stale", "soon" };

    public static double? LoadPercentage(TechnicianWorkloadEntry technician, int? activeTarget)
    {
        ArgumentNullException.ThrowIfNull(technician);
        if (!activeTarget.HasValue) return null;
        if (activeTarget is < 1 or > TechnicianPreferences.MaximumActiveTarget)
            throw new ArgumentOutOfRangeException(nameof(activeTarget), "L’objectif local doit être un nombre positif dans la limite autorisée.");
        return technician.ActiveCount.HasValue ? 100d * technician.ActiveCount.Value / activeTarget.Value : null;
    }

    public static TechnicianWorkloadSnapshot Calculate(ExtractionResult source, DashboardSettings settings,
        IReadOnlyList<int>? sourceRows = null, DateTimeOffset? referenceTime = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();
        // The shared calculator validates and canonicalizes a supplied subset, preserving original indexes.
        var global = sourceRows is null ? DashboardService.Calculate(source, settings, referenceTime, ct)
            : DashboardService.CalculateSubset(source, settings, sourceRows, "Gestion de l’équipe · périmètre choisi", referenceTime, ct);
        var rows = global.Metric("total")!.Rows;
        var roster = sourceRows is null ? TechnicianDashboardService.Detect(source, settings, ct)
            : TechnicianDashboardService.DetectSubset(source, settings, rows, ct);
        if (!roster.Available) return new() { Roster = roster, Global = global };
        var metrics = MeasureKeys.ToDictionary(key => key, key => global.Metric(key)!, StringComparer.Ordinal);
        var sets = metrics.ToDictionary(pair => pair.Key, pair => pair.Value.Rows.ToHashSet(), StringComparer.Ordinal);
        var active = sets["active"];
        var unknown = global.Metric("unknown-status")!.Rows.ToHashSet();
        var hasStatus = global.Column("status")!.Index.HasValue;
        var samples = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal)
        {
            ["total"] = rows.ToHashSet(),
            ["active"] = hasStatus ? rows.Where(index => !unknown.Contains(index)).ToHashSet() : new(),
            ["pending"] = hasStatus ? rows.Where(index => !unknown.Contains(index)).ToHashSet() : new(),
            ["high"] = new(), ["overdue"] = new(), ["old"] = new(), ["stale"] = new(), ["soon"] = new()
        };
        var age = new Dictionary<int, double>();
        var inactivity = new Dictionary<int, double>();
        var now = global.CalculatedAt.DateTime;
        string Cell(int index, string role) => global.Column(role)!.Index is int column && column < source.Rows[index].Length
            ? DashboardService.NormalizeValue(source.Rows[index][column], role, source.Headers[column], settings) : "";
        var iteration = 0;
        foreach (var index in active)
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            if (DashboardService.Priority(Cell(index, "priority")).HasValue) samples["high"].Add(index);
            if (DashboardService.TrySourceDate(Cell(index, "due"), settings, global.CalculatedAt.Offset, out _))
            { samples["overdue"].Add(index); samples["soon"].Add(index); }
            var createdValid = DashboardService.TrySourceDate(Cell(index, "created"), settings, global.CalculatedAt.Offset, out var created) && created <= now;
            if (createdValid)
            { samples["old"].Add(index); age[index] = (now - created).TotalDays; }
            if (DashboardService.TrySourceDate(Cell(index, "updated"), settings, global.CalculatedAt.Offset, out var updated)
                && updated <= now && (!createdValid || updated >= created))
            { samples["stale"].Add(index); inactivity[index] = (now - updated).TotalDays; }
        }
        var assignmentCounts = new Dictionary<int, int>();
        foreach (var technician in roster.Technicians)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var index in technician.Rows)
            {
                if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
                assignmentCounts[index] = assignmentCounts.GetValueOrDefault(index) + 1;
            }
        }
        var multiple = assignmentCounts.Where(pair => pair.Value > 1).Select(pair => pair.Key).Order().ToArray();
        var multipleSet = multiple.ToHashSet();
        var entries = new List<TechnicianWorkloadEntry>(roster.Technicians.Count);
        foreach (var technician in roster.Technicians)
        {
            ct.ThrowIfCancellationRequested();
            var found = MeasureKeys.ToDictionary(key => key, _ => new List<int>(), StringComparer.Ordinal);
            var sampleCounts = MeasureKeys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
            var unknownCount = 0; var multipleCount = 0;
            double totalAge = 0, totalInactivity = 0; var ageCount = 0; var inactivityCount = 0;
            foreach (var index in technician.Rows)
            {
                if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
                foreach (var key in MeasureKeys)
                {
                    if (sets[key].Contains(index)) found[key].Add(index);
                    if (samples[key].Contains(index)) sampleCounts[key]++;
                }
                if (!hasStatus || unknown.Contains(index)) unknownCount++;
                if (multipleSet.Contains(index)) multipleCount++;
                if (age.TryGetValue(index, out var days)) { totalAge += days; ageCount++; }
                if (inactivity.TryGetValue(index, out var unchanged)) { totalInactivity += unchanged; inactivityCount++; }
            }
            var measures = new Dictionary<string, TechnicianWorkloadMeasure>(StringComparer.Ordinal);
            foreach (var key in MeasureKeys)
            {
                var metric = metrics[key];
                var population = key is "total" or "active" or "pending" ? technician.Rows.Length : found["active"].Count;
                var role = key switch { "high" => "priority", "overdue" or "soon" => "due", "old" => "created", "stale" => "updated", _ => "status" };
                var statusEvaluable = hasStatus && (technician.Rows.Length == 0 || sampleCounts["active"] > 0);
                var available = key == "total" || statusEvaluable && (key is "active" or "pending" || global.Column(role)!.Index.HasValue
                    && (population == 0 || sampleCounts[key] > 0));
                measures[key] = new(key, metric.Label, available ? found[key].Count : null, sampleCounts[key], population,
                    available ? found[key].ToArray() : Array.Empty<int>(), metric.Explanation);
            }
            entries.Add(new()
            {
                Key = technician.Key, Name = technician.Name, Rows = technician.Rows,
                UnknownStatusCount = unknownCount, MultipleAssignmentCount = multipleCount,
                MeanAgeDays = ageCount > 0 ? totalAge / ageCount : null,
                MeanInactivityDays = inactivityCount > 0 ? totalInactivity / inactivityCount : null,
                Measures = measures
            });
        }
        ct.ThrowIfCancellationRequested();
        var assigned = assignmentCounts.Keys.Order().ToArray();
        return new()
        {
            Roster = roster, Global = global, Technicians = entries,
            AssignedRows = assigned, AssignedActiveRows = assigned.Where(active.Contains).ToArray(),
            UnassignedRows = rows.Where(index => !assignmentCounts.ContainsKey(index)).ToArray(), MultipleAssignmentRows = multiple
        };
    }
}
