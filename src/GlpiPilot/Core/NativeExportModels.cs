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

public enum NativeExportFailureKind { Unavailable, Transport, Http, Deadline }

// This exception is exposed only before a command is sent, or after the failed
// native transfer has been cleaned up and its page family has been retired.
// The caller must still revalidate the search before reading the table instead.
public class NativeExportRecoverableException : InvalidOperationException
{
    public NativeExportFailureKind Kind { get; }
    public int? HttpStatus { get; }

    public NativeExportRecoverableException(NativeExportFailureKind kind, string message, int? httpStatus = null, Exception? innerException = null)
        : base(message, innerException)
    { Kind = kind; HttpStatus = httpStatus; }
}

public sealed class NativeExportUnavailableException(string message)
    : NativeExportRecoverableException(NativeExportFailureKind.Unavailable, message) { }

// An observed failure is not yet a safe handoff: the browser can still deliver
// a late attachment. Only ExportAllNativeAsync converts this internal signal
// after CaptureDownloadCoreAsync has completed its failure cleanup.
internal sealed class NativeExportInterruptionException(NativeExportFailureKind kind, string message, int? httpStatus = null, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public NativeExportFailureKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
}

public sealed record AutomaticRunResult(ExtractionResult Data, TableChoice Table, string PrimaryPath, string? NativePath, string? ExcelPath, CityEnrichmentResult? Cities = null)
{
    // The live collection table can carry temporary columns. Persist this
    // observed baseline for the next navigation after those columns are restored.
    public TableChoice? TableToRemember { get; init; }
}
