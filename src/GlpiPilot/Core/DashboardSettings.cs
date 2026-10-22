namespace GlpiPilot.Core;

public sealed class DashboardSettings
{
    public Dictionary<string, string> ColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int StaleDays { get; set; } = 7;
    public int OldDays { get; set; } = 30;
    public int DueSoonHours { get; set; } = 24;
    public string DateCulture { get; set; } = "fr-FR";
    public string NumberCulture { get; set; } = "fr-FR";
    public string SourceTimeZone { get; set; } = "";
    public string MultiValueSeparators { get; set; } = ";\n|";
    public Dictionary<string, Dictionary<string, string>> ValueDictionaries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<double> DueHorizonHours { get; set; } = new() { 0, 24, 48, 168, 720 };
    public double SatisfactionScale { get; set; } = 5;
    public int HistoryCadenceHours { get; set; } = 24;
    public DashboardLayoutSettings Layout { get; set; } = new();
    private DashboardLayoutSettings? _personalLayout;
    // An older configuration starts with a copy of its existing board. From then
    // on, edits in either workspace cannot replace the other workspace's layout.
    public DashboardLayoutSettings PersonalLayout
    {
        get => _personalLayout ??= Layout?.Copy() ?? new();
        set => _personalLayout = value;
    }
    public string PersonalTechnician { get; set; } = "";
    // Local navigation preference, deliberately excluded from portable configuration.
    public bool OpenPersonalAfterExtraction { get; set; }
    public DashboardWeatherSettings Weather { get; set; } = new();
    public string AssignmentCountingMode { get; set; } = "participation";
    public Dictionary<string, int> PriorityRules { get; set; } = new(StringComparer.Ordinal);

    public DashboardSettings Copy()
    {
        var copy = (DashboardSettings)MemberwiseClone();
        copy.ColumnMappings = new(StringComparer.OrdinalIgnoreCase);
        copy.Layout = Layout?.Copy() ?? new();
        copy._personalLayout = _personalLayout?.Copy() ?? copy.Layout.Copy();
        copy.Weather = Weather?.Copy() ?? new();
        copy.DueHorizonHours = new(DueHorizonHours ?? new());
        copy.ValueDictionaries = CleanDictionaries(ValueDictionaries);
        copy.PriorityRules = new(PriorityRules ?? new(), StringComparer.Ordinal);
        if (ColumnMappings is not null)
            foreach (var pair in ColumnMappings) copy.ColumnMappings[pair.Key] = pair.Value ?? "";
        copy.Normalize();
        return copy;
    }

    private static Dictionary<string, Dictionary<string, string>> CleanDictionaries(Dictionary<string, Dictionary<string, string>>? dictionaries) =>
        (dictionaries ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Key.Length <= 200).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Take(60)
        .ToDictionary(group => group.Key, group => (group.Last().Value ?? new()).Where(v => !string.IsNullOrWhiteSpace(v.Key) && v.Key.Length <= 1000 && v.Value != null && v.Value.Length <= 1000)
            .GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Take(2000).ToDictionary(v => v.Key, v => v.Last().Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    internal void Normalize()
    {
        DateCulture ??= "fr-FR";
        NumberCulture ??= "fr-FR"; SourceTimeZone ??= ""; MultiValueSeparators ??= ";\n|";
        if (MultiValueSeparators.Length > 32) MultiValueSeparators = MultiValueSeparators[..32];
        ValueDictionaries = CleanDictionaries(ValueDictionaries);
        DueHorizonHours = (DueHorizonHours ?? new()).Where(double.IsFinite).Where(v => v >= 0 && v <= 87600).Append(0).Distinct().Order().Take(20).ToList();
        if (!double.IsFinite(SatisfactionScale) || SatisfactionScale <= 0 || SatisfactionScale > 100) SatisfactionScale = 5;
        HistoryCadenceHours = Math.Clamp(HistoryCadenceHours, 1, 720);
        PersonalTechnician ??= "";
        Weather ??= new();
        Weather.Normalize();
        if (AssignmentCountingMode is not ("participation" or "fractional" or "cell")) AssignmentCountingMode = "participation";
        PriorityRules ??= new(StringComparer.Ordinal);
        Layout ??= new();
        Layout.Normalize();
        _personalLayout ??= Layout.Copy();
        if (ReferenceEquals(_personalLayout, Layout)) _personalLayout = Layout.Copy();
        _personalLayout.Normalize();
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ColumnMappings is not null)
            foreach (var pair in ColumnMappings) mappings[pair.Key] = pair.Value ?? "";
        ColumnMappings = mappings;
    }
}

public sealed class DashboardWeatherSettings
{
    public int Version { get; set; } = 1;
    public int OverdueWeight { get; set; } = 35;
    public int PriorityWeight { get; set; } = 25;
    public int AssignmentWeight { get; set; } = 15;
    public int StaleWeight { get; set; } = 15;
    public int OldWeight { get; set; } = 10;
    public double WatchThreshold { get; set; } = 10;
    public double RainThreshold { get; set; } = 25;
    public double StormThreshold { get; set; } = 45;
    public int MinimumCoveragePercent { get; set; } = 80;
    public int MinimumWeightPercent { get; set; } = 60;
    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalWeight => OverdueWeight + PriorityWeight + AssignmentWeight + StaleWeight + OldWeight;
    public DashboardWeatherSettings Copy() => (DashboardWeatherSettings)MemberwiseClone();
    public void Normalize()
    {
        if (Version != 1) Version = 1;
        OverdueWeight = Math.Clamp(OverdueWeight, 0, 100); PriorityWeight = Math.Clamp(PriorityWeight, 0, 100);
        AssignmentWeight = Math.Clamp(AssignmentWeight, 0, 100); StaleWeight = Math.Clamp(StaleWeight, 0, 100); OldWeight = Math.Clamp(OldWeight, 0, 100);
        if (!double.IsFinite(WatchThreshold) || !double.IsFinite(RainThreshold) || !double.IsFinite(StormThreshold)
            || WatchThreshold < 0 || WatchThreshold > RainThreshold || RainThreshold > StormThreshold || StormThreshold > 100)
        { WatchThreshold = 10; RainThreshold = 25; StormThreshold = 45; }
        MinimumCoveragePercent = Math.Clamp(MinimumCoveragePercent, 1, 100);
        MinimumWeightPercent = Math.Clamp(MinimumWeightPercent, 1, 100);
    }
    public void Validate()
    {
        if (Version != 1 || new[] { OverdueWeight, PriorityWeight, AssignmentWeight, StaleWeight, OldWeight }.Any(x => x is < 0 or > 100)
            || !double.IsFinite(WatchThreshold) || !double.IsFinite(RainThreshold) || !double.IsFinite(StormThreshold)
            || WatchThreshold < 0 || WatchThreshold > RainThreshold || RainThreshold > StormThreshold || StormThreshold > 100
            || MinimumCoveragePercent is < 1 or > 100 || MinimumWeightPercent is < 1 or > 100)
            throw new System.IO.InvalidDataException("La météo demande des poids de 0 à 100, des seuils ordonnés de 0 à 100 et une couverture de 1 à 100 %.");
    }
}
