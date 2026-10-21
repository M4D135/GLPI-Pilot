namespace GlpiPilot.Core;

public sealed record DashboardRole(string Key, string Label, string[] Aliases);
public sealed record DashboardColumn(string Role, string Label, int? Index, string Header, string State);
public sealed record DashboardMetric(string Key, string Label, double? Value, string Unit,
    string Explanation, int[] Rows, int? Sample = null, string Tone = "normal");
public sealed record DashboardBucket(string Label, int Count, int[] Rows);
public sealed record DashboardDistribution(string Key, string Title, bool Available, int Denominator,
    string Note, IReadOnlyList<DashboardBucket> Buckets);
public sealed record DashboardDay(DateTime Date, int[] Created, int[] Resolved, int[] Closed);
public sealed record DashboardSignal(string Label, int Count, int Sample, int Population, int Weight,
    int[] Rows, string Explanation);
public sealed record DashboardWeather(string State, string Title, string Symbol, double? Score,
    int MeasuredWeight, string Description, IReadOnlyList<DashboardSignal> Signals);
public sealed record DashboardFocusRow(int Index, string Id, string Title, string Status,
    string Priority, string Group, double? AgeDays, string Reasons);

public sealed class DashboardSnapshot
{
    public DateTimeOffset CalculatedAt { get; init; }
    public DateTimeOffset ExtractedAt { get; init; }
    public int RowCount { get; init; }
    public int? ExpectedTotal { get; init; }
    public bool Complete { get; init; }
    public bool IsFiltered { get; init; }
    public string ScopeLabel { get; init; } = "Équipe entière";
    public int SourceRowCount { get; init; }
    public int? SourceExpectedTotal { get; init; }
    public string SourceUrl { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Grain { get; set; } = "lignes";
    public List<DashboardColumn> Columns { get; init; } = new();
    public List<DashboardMetric> Metrics { get; init; } = new();
    public List<DashboardDistribution> Distributions { get; init; } = new();
    public List<DashboardDay> Timeline { get; init; } = new();
    public List<DashboardFocusRow> Attention { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public DashboardWeather Weather { get; set; } = new("unknown", "Météo indéterminée", "◌", null, 0,
        "Importez ou extrayez un relevé pour calculer sa météo.", Array.Empty<DashboardSignal>());
    public DashboardMetric? Metric(string key) => Metrics.FirstOrDefault(x => x.Key == key);
    public DashboardColumn? Column(string key) => Columns.FirstOrDefault(x => x.Role == key);
}
