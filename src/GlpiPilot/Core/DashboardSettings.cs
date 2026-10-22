namespace GlpiPilot.Core;

public sealed class DashboardSettings
{
    public Dictionary<string, string> ColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int StaleDays { get; set; } = 7;
    public int OldDays { get; set; } = 30;
    public int DueSoonHours { get; set; } = 24;
    public string DateCulture { get; set; } = "fr-FR";
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
        copy.PriorityRules = new(PriorityRules ?? new(), StringComparer.Ordinal);
        if (ColumnMappings is not null)
            foreach (var pair in ColumnMappings) copy.ColumnMappings[pair.Key] = pair.Value ?? "";
        copy.Normalize();
        return copy;
    }

    internal void Normalize()
    {
        DateCulture ??= "fr-FR";
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
