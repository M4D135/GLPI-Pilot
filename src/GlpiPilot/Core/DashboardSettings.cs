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
    public BusinessDashboardSettings Business { get; set; } = new();
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
        copy.Business = Business?.Copy() ?? new();
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
        Business ??= new();
        Business.Normalize();
        _personalLayout ??= Layout.Copy();
        if (ReferenceEquals(_personalLayout, Layout)) _personalLayout = Layout.Copy();
        _personalLayout.Normalize();
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ColumnMappings is not null)
            foreach (var pair in ColumnMappings) mappings[pair.Key] = pair.Value ?? "";
        ColumnMappings = mappings;
    }
}

/// <summary>Native pilot views, independent of the twelve user-created dashboards.</summary>
public sealed class BusinessDashboardSettings
{
    public static readonly string[] ViewKeys = { "weather", "pilotage", "groups", "categories", "oldest", "followup", "participants", "sites" };
    public List<BusinessWeatherRule> WeatherRules { get; set; } = new();
    public List<DashboardBoardSettings> BoardOverrides { get; set; } = new();
    public string Notes { get; set; } = "";
    public int? FreshnessWarningHours { get; set; } = 48;
    public int SiteInterventionThreshold { get; set; } = 4;
    public BusinessDashboardSettings Copy() => new()
    {
        WeatherRules = (WeatherRules ?? new()).Where(rule => rule is not null).Select(rule => rule.Copy()).ToList(),
        BoardOverrides = (BoardOverrides ?? new()).Where(board => board is not null).Select(board => board.Copy()).ToList(),
        Notes = Notes ?? "", FreshnessWarningHours = FreshnessWarningHours, SiteInterventionThreshold = SiteInterventionThreshold
    };
    public void Normalize()
    {
        Notes ??= "";
        WeatherRules ??= new(); BoardOverrides ??= new();
        foreach (var rule in WeatherRules.Where(rule => rule is not null)) rule.Normalize();
        foreach (var board in BoardOverrides.Where(board => board is not null)) board.Normalize();
    }
    public void Validate()
    {
        if (WeatherRules is null || WeatherRules.Count > 48 || WeatherRules.Any(rule => rule is null)
            || BoardOverrides is null || BoardOverrides.Count > ViewKeys.Length || BoardOverrides.Any(board => board is null)
            || Notes is null || Notes.Length > 4000 || FreshnessWarningHours is < 1 or > 8760 || SiteInterventionThreshold is < 1 or > 1000000)
            throw new System.IO.InvalidDataException("Les paramètres de pilotage dépassent les limites autorisées.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in WeatherRules) { rule.Validate(); if (!ids.Add(rule.Id)) throw new System.IO.InvalidDataException("Deux règles météo portent le même identifiant."); }
        if (WeatherRules.Where(rule => rule.Kind == "team").SelectMany(rule => rule.Values).Distinct(StringComparer.Ordinal).Count() > 352)
            throw new System.IO.InvalidDataException("La priorité météo accepte au maximum 352 groupes répartis dans les équipes.");
        ids.Clear();
        foreach (var board in BoardOverrides)
        {
            if (!ViewKeys.Any(key => board.Id == "business-" + key) || !ids.Add(board.Id))
                throw new System.IO.InvalidDataException("Une personnalisation de rubrique est absente, inconnue ou dupliquée.");
            new DashboardLayoutSettings { ActiveBoardId = board.Id, Boards = new() { board } }.Validate();
        }
    }
}

public sealed class BusinessWeatherRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "metric";
    public string SourceKey { get; set; } = "active";
    public List<string> Values { get; set; } = new();
    public double? OrangeAbove { get; set; }
    public double? RedAbove { get; set; }
    public bool Visible { get; set; } = true;
    public bool WeatherEnabled { get; set; } = true;
    public bool IncludeDescendants { get; set; }
    public string Note { get; set; } = "";
    public BusinessWeatherRule Copy() => new()
    {
        Id = Id, Label = Label, Kind = Kind, SourceKey = SourceKey, Values = new(Values ?? new()), OrangeAbove = OrangeAbove,
        RedAbove = RedAbove, Visible = Visible, WeatherEnabled = WeatherEnabled, IncludeDescendants = IncludeDescendants, Note = Note
    };
    public void Normalize()
    {
        Id ??= ""; Label ??= ""; Kind ??= "metric"; SourceKey ??= "active"; Note ??= "";
        Values = (Values ?? new()).Where(value => value is not null).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 96 || string.IsNullOrWhiteSpace(Label) || Label.Length > 180
            || Kind is not ("metric" or "type" or "category" or "team") || string.IsNullOrWhiteSpace(SourceKey) || SourceKey.Length > 120
            || Values is null || Values.Count > 32 || Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1000)
            || Kind != "metric" && Values.Count == 0 || Note is null || Note.Length > 4000
            || OrangeAbove.HasValue && (!double.IsFinite(OrangeAbove.Value) || OrangeAbove < 0)
            || RedAbove.HasValue && (!double.IsFinite(RedAbove.Value) || RedAbove < 0)
            || OrangeAbove.HasValue && RedAbove.HasValue && OrangeAbove > RedAbove)
            throw new System.IO.InvalidDataException("Une règle météo demande un nom, un périmètre et des seuils positifs ordonnés.");
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
