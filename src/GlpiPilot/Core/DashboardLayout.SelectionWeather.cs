using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>The explicit navigation choices to restore with a portable board.</summary>
public sealed class DashboardSavedSelection
{
    public List<string> FocusKeys { get; set; } = new();
    public string TechnicianKey { get; set; } = "";
    public List<DashboardScopeFilter> CrossFilters { get; set; } = new();
    public DashboardSavedSelection Copy() => new() { FocusKeys = new(FocusKeys ?? new()), TechnicianKey = TechnicianKey,
        CrossFilters = DashboardScopeFilter.CopyList(CrossFilters) };
    public void Validate()
    {
        if (FocusKeys is null || FocusKeys.Count > 64 || FocusKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 1200)
            || TechnicianKey is null || TechnicianKey.Length > 300)
            throw new InvalidDataException("La sélection doit contenir au maximum 64 équipes ou groupes et une identité valide.");
        DashboardScopeFilter.ValidateList(CrossFilters);
    }
    public string Canonical() => JsonSerializer.Serialize(new { FocusKeys = FocusKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        TechnicianKey, CrossFilters = DashboardWeatherSelectionScope.CanonicalFilters(CrossFilters) });
}

/// <summary>A definition of the selection, never its current rows or current total.</summary>
public sealed class DashboardWeatherSelectionScope
{
    public int Version { get; set; } = 1;
    public string SourceIdentity { get; set; } = "";
    public string InterpretationIdentity { get; set; } = "";
    public string Kind { get; set; } = "metric";
    public string SourceKey { get; set; } = "";
    public string TechnicianKey { get; set; } = "";
    public List<DashboardScopeFilter> CommonFilters { get; set; } = new();
    public List<DashboardScopeFilter> WidgetFilters { get; set; } = new();
    public DashboardWeatherSelectionScope Copy() => new() { Version = Version, SourceIdentity = SourceIdentity,
        InterpretationIdentity = InterpretationIdentity, Kind = Kind, SourceKey = SourceKey, TechnicianKey = TechnicianKey,
        CommonFilters = DashboardScopeFilter.CopyList(CommonFilters), WidgetFilters = DashboardScopeFilter.CopyList(WidgetFilters) };
    public void Validate()
    {
        static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        if (Version != 1 || !Digest(SourceIdentity) || !Digest(InterpretationIdentity) || Kind != "metric"
            || string.IsNullOrWhiteSpace(SourceKey) || SourceKey.Length > 120 || TechnicianKey is null || TechnicianKey.Length > 300)
            throw new InvalidDataException("Le périmètre des seuils doit désigner une source, un indicateur et une interprétation valides.");
        DashboardScopeFilter.ValidateList(CommonFilters); DashboardScopeFilter.ValidateList(WidgetFilters);
    }
    public string Canonical() => JsonSerializer.Serialize(new { Version, SourceIdentity, InterpretationIdentity, Kind, SourceKey, TechnicianKey,
        CommonFilters = CanonicalFilters(CommonFilters), WidgetFilters = CanonicalFilters(WidgetFilters) });
    public string Key() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical())));
    internal static string[] CanonicalFilters(IEnumerable<DashboardScopeFilter> filters) => filters.Select(filter =>
    {
        // The engine combines filters by AND and selections within a distribution
        // by OR. Only those commutative sets are sorted; dates and operators remain exact.
        var values = filter.Kind is "distribution" or "matrix-row" ? filter.Selections().Select(value =>
            (filter.IncludeDescendants ? string.Join('>', value.Normalize(NormalizationForm.FormC).Split('>').Select(part => part.Trim())) : value)
            .ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        return JsonSerializer.Serialize(new { filter.Kind, filter.SourceKey, filter.Exclude, filter.IncludeDescendants, Values = values,
            Value = filter.Kind == "date" ? filter.Value : "", Value2 = filter.Kind == "date" ? filter.Value2 : "" });
    }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

public class DashboardWeatherThresholdPolicy
{
    public static readonly string[] Denominators = { "selection-active", "selection-total", "source-active", "source-total", "selection-sample" };
    public string Unit { get; set; } = "count";
    public string DenominatorKey { get; set; } = "selection-active";
    public double WarningValue { get; set; }
    public double CriticalValue { get; set; }
    public bool LowerIsBetter { get; set; } = true;
    public DashboardWeatherThresholdPolicy Copy() => new() { Unit = Unit, DenominatorKey = DenominatorKey,
        WarningValue = WarningValue, CriticalValue = CriticalValue, LowerIsBetter = LowerIsBetter };
    public virtual void Validate()
    {
        if (Unit is not ("count" or "percent") || !Denominators.Contains(DenominatorKey)
            || Unit == "percent" && (WarningValue is < 0 or > 100 || CriticalValue is < 0 or > 100))
            throw new InvalidDataException("Les seuils demandent une unité valide, une base explicite et des pourcentages de 0 à 100.");
        new DashboardWidgetSettings { Kind = "metric", Presentation = "weather-volume", WarningValue = WarningValue,
            CriticalValue = CriticalValue, LowerIsBetter = LowerIsBetter }.ValidateThresholds();
    }
}

public sealed class DashboardSelectionThresholds : DashboardWeatherThresholdPolicy
{
    public const int MaximumPerWidget = 64;
    public DashboardWeatherSelectionScope Scope { get; set; } = new();
    public new DashboardSelectionThresholds Copy() => new() { Scope = Scope.Copy(), Unit = Unit, DenominatorKey = DenominatorKey,
        WarningValue = WarningValue, CriticalValue = CriticalValue, LowerIsBetter = LowerIsBetter };
    public override void Validate()
    {
        if (Scope is null) throw new InvalidDataException("Le périmètre des seuils est absent.");
        Scope.Validate(); base.Validate();
    }
    public static void ValidateList(IReadOnlyList<DashboardSelectionThresholds>? entries)
    {
        if (entries is null || entries.Count > MaximumPerWidget || entries.Any(entry => entry is null))
            throw new InvalidDataException($"Un widget accepte au maximum {MaximumPerWidget} jeux de seuils par sélection.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries) { entry.Validate(); if (!seen.Add(entry.Scope.Key())) throw new InvalidDataException("Deux jeux de seuils désignent la même sélection."); }
    }
}
