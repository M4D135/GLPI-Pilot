using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed class NativeExportPlan
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("menuSelector")] public string MenuSelector { get; set; } = "";
    [JsonPropertyName("formatSelector")] public string FormatSelector { get; set; } = "";
    [JsonPropertyName("formatValue")] public string FormatValue { get; set; } = "";
    [JsonPropertyName("formatHidden")] public bool FormatHidden { get; set; }
    [JsonPropertyName("scopeSelector")] public string ScopeSelector { get; set; } = "";
    [JsonPropertyName("scopeValue")] public string ScopeValue { get; set; } = "";
    [JsonPropertyName("submitSelector")] public string SubmitSelector { get; set; } = "";
    [JsonPropertyName("directSelector")] public string DirectSelector { get; set; } = "";
    [JsonPropertyName("allPages")] public bool AllPages { get; set; }
    [JsonPropertyName("needsOpen")] public bool NeedsOpen { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

public sealed class NativeExportInspection
{
    [JsonPropertyName("supported")] public bool Supported { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("expectedTotal")] public int? ExpectedTotal { get; set; }
    [JsonPropertyName("rangeStart")] public int? RangeStart { get; set; }
    [JsonPropertyName("rangeEnd")] public int? RangeEnd { get; set; }
    [JsonPropertyName("plans")] public List<NativeExportPlan> Plans { get; set; } = new();
    [JsonPropertyName("diagnostics")] public List<string> Diagnostics { get; set; } = new();
    [JsonPropertyName("displayPlan")] public NativeDisplayPlan? DisplayPlan { get; set; }
    [JsonIgnore] public bool Unavailable => !Supported && Plans.Count == 0
        && Reason.StartsWith("Aucune commande native CSV", StringComparison.Ordinal);
}

public sealed class NativeDisplayPlan
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("selector")] public string Selector { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("expectedTotal")] public int ExpectedTotal { get; set; }
    [JsonPropertyName("formSelector")] public string FormSelector { get; set; } = "";
    [JsonPropertyName("formMethod")] public string FormMethod { get; set; } = "";
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    [JsonPropertyName("startsAtZero")] public bool StartsAtZero { get; set; }
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
}

public sealed record NativeDownloadResult(string Path, int? ExpectedTotal, string PlanKind);
public sealed class NativeExportUnavailableException(string message) : InvalidOperationException(message) { }
public sealed record AutomaticRunResult(ExtractionResult Data, TableChoice Table, string PrimaryPath, string? NativePath, string? ExcelPath, CityEnrichmentResult? Cities = null);
