namespace GlpiPilot.Core;

public sealed record DashboardRole(string Key, string Label, string[] Aliases);
public sealed record DashboardColumn(string Role, string Label, int? Index, string Header, string State)
{
    // State remains the stable machine-readable value used by mappings and checks.
    public string StateLabel => State switch
    {
        "auto" => "Reconnue automatiquement",
        "configured" => "Définie manuellement",
        "ambiguous" => "Plusieurs colonnes possibles",
        "missing-override" => "Colonne configurée absente",
        "missing" => "Aucune colonne reconnue",
        _ => "Correspondance non déterminée"
    };
}
public sealed record DashboardMetric(string Key, string Label, double? Value, string Unit,
    string Explanation, int[] Rows, int? Sample = null, string Tone = "normal");
public sealed record DashboardBucket(string Label, int Count, int[] Rows,
    double? Value = null, string Unit = "", int? Sample = null, bool IsAggregate = false);
public sealed record DashboardDistribution(string Key, string Title, bool Available, int Denominator,
    string Note, IReadOnlyList<DashboardBucket> Buckets);
public sealed record DashboardDay(DateTime Date, int[] Created, int[] Resolved, int[] Closed);
public sealed record DashboardSignal(string Label, int Count, int Sample, int Population, int Weight,
    int[] Rows, string Explanation);
public sealed record DashboardWeather(string State, string Title, string Symbol, double? Score,
    int MeasuredWeight, string Description, IReadOnlyList<DashboardSignal> Signals);
public sealed record DashboardFocusRow(int Index, string Id, string Title, string Status,
    string Priority, string Group, double? AgeDays, string Reasons);
public sealed record DashboardDateValue(int Row, DateTime Date);
public sealed record DashboardNumericValue(int Row, double Value, string Label = "");
public sealed record DashboardNumericSeries(string Key, string Title, string Unit,
    IReadOnlyList<DashboardNumericValue> Values, string Explanation);
public sealed record DashboardMatrixCell(string RowLabel, string ColumnLabel, double Value, int[] Rows);
public sealed record DashboardMatrix(string Key, string Title, bool Available, string Unit,
    IReadOnlyList<DashboardMatrixCell> Cells, string Explanation);
public sealed record DashboardScatterPoint(int Row, string Label, double X, double Y);
public sealed record DashboardScatter(string Key, string Title, string XLabel, string YLabel,
    IReadOnlyList<DashboardScatterPoint> Points, string Explanation);
public sealed record DashboardBox(string Label, int Sample, double Minimum, double FirstQuartile,
    double Median, double ThirdQuartile, double Maximum, double P90, int[] Rows);
public sealed record DashboardBoxSeries(string Key, string Title, bool Available, string Unit,
    IReadOnlyList<DashboardBox> Boxes, string Explanation);
public sealed record DashboardGeoPoint(string Key, string Label, double Latitude, double Longitude, int[] Rows, int[] ActiveRows);
public sealed record DashboardHistoryPoint(DateTimeOffset CapturedAt, double? Value, int Rows,
    bool Complete, bool Comparable, string SnapshotId, string Note = "");
public sealed record DashboardHistorySeries(string Key, string Title, string Unit,
    IReadOnlyList<DashboardHistoryPoint> Points, string Explanation);
public sealed record DashboardHistoryScope(string Key, IReadOnlyList<DashboardScopeFilter> CommonFilters,
    IReadOnlyList<DashboardScopeFilter> WidgetFilters);

public sealed class DashboardSnapshot
{
    public DateTimeOffset CalculatedAt { get; init; }
    public DateTimeOffset ExtractedAt { get; init; }
    public int RowCount { get; init; }
    public int? ExpectedTotal { get; init; }
    public bool Complete { get; init; }
    public bool ReceptionComplete { get; init; }
    public ExtractionQuality? Quality { get; init; }
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
    public int[] ScopeRows { get; init; } = Array.Empty<int>();
    public Dictionary<string, IReadOnlyList<DashboardDateValue>> DateValues { get; init; } = new(StringComparer.Ordinal);
    public List<DashboardNumericSeries> NumericSeries { get; init; } = new();
    public List<DashboardMatrix> Matrices { get; init; } = new();
    public List<DashboardScatter> Scatters { get; init; } = new();
    public List<DashboardBoxSeries> BoxSeries { get; init; } = new();
    public List<DashboardHistorySeries> History { get; init; } = new();
    public List<DashboardGeoPoint> Geography { get; init; } = new();
    public Dictionary<string, DashboardWeather> WeatherProfiles { get; init; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<int[], string, CancellationToken, DashboardSnapshot>? RecalculateScope { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<IReadOnlyList<DashboardHistoryScope>, CancellationToken, IReadOnlyDictionary<string, IReadOnlyList<DashboardHistorySeries>>>? RecalculateHistories { get; set; }
    public DashboardWeather Weather { get; set; } = new("unknown", "Météo indéterminée", "◌", null, 0,
        "Importez ou extrayez un relevé pour calculer sa météo.", Array.Empty<DashboardSignal>());
    public DashboardMetric? Metric(string key) => Metrics.FirstOrDefault(x => x.Key == key);
    public DashboardColumn? Column(string key) => Columns.FirstOrDefault(x => x.Role == key);
}
