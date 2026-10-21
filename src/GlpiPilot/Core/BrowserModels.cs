using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed class PageChoice
{
    public required IPage Page { get; init; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class NextControl
{
    [JsonPropertyName("selector")] public string Selector { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    [JsonPropertyName("ambiguous")] public bool Ambiguous { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class TableChoice
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("selector")] public string Selector { get; set; } = "";
    [JsonPropertyName("headers")] public List<string> Headers { get; set; } = new();
    [JsonPropertyName("rowCount")] public int RowCount { get; set; }
    [JsonPropertyName("columnCount")] public int ColumnCount { get; set; }
    [JsonPropertyName("headerFingerprint")] public string HeaderFingerprint { get; set; } = "";
    [JsonIgnore] public IFrame? Frame { get; set; }
    [JsonIgnore] public IPage? Page { get; set; }
    [JsonIgnore] public string FrameOrigin { get; set; } = "";
    [JsonIgnore] public string FrameName { get; set; } = "";
    [JsonIgnore] public string TopOrigin { get; set; } = "";
}

public sealed class Inspection
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("hasPasswordField")] public bool HasPasswordField { get; set; }
    [JsonPropertyName("authenticatedHint")] public bool AuthenticatedHint { get; set; }
    [JsonPropertyName("framesHint")] public string FramesHint { get; set; } = "";
    [JsonPropertyName("tables")] public List<TableChoice> Tables { get; set; } = new();
    [JsonPropertyName("next")] public NextControl? Next { get; set; }
    [JsonPropertyName("pageText")] public string PageText { get; set; } = "";
    [JsonPropertyName("expectedTotal")] public int? ExpectedTotal { get; set; }
}

public sealed class TableSnapshot
{
    [JsonPropertyName("headers")] public List<string> Headers { get; set; } = new();
    [JsonPropertyName("rows")] public List<string[]> Rows { get; set; } = new();
    [JsonPropertyName("keys")] public List<string> Keys { get; set; } = new();
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonPropertyName("next")] public NextControl? Next { get; set; }
    [JsonPropertyName("expectedTotal")] public int? ExpectedTotal { get; set; }
    [JsonPropertyName("pageText")] public string PageText { get; set; } = "";
    [JsonPropertyName("headerFingerprint")] public string HeaderFingerprint { get; set; } = "";
    [JsonPropertyName("selector")] public string Selector { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

public sealed class PickResult
{
    [JsonPropertyName("cancelled")] public bool Cancelled { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("selector")] public string Selector { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

public sealed record ExtractionProgress(int Pages, int Rows, int? Expected, string Message);
public enum AutomaticRunPhase { Connection, Search, Retrieval, Validation, Saving, Ready }
public sealed record SessionStatus(string Text, bool TableAvailable, bool ExplicitReady);
