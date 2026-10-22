using System;
using System.Collections.Generic;

namespace GlpiPilot.Core;

public sealed class ExtractionResult
{
    public List<string> Headers { get; set; } = new();
    public List<string[]> Rows { get; set; } = new();
    public string SourceUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset ExtractedAt { get; set; } = DateTimeOffset.Now;
    public int Pages { get; set; }
    public int? ExpectedTotal { get; set; }
    public string Scope { get; set; } = "Page courante";
    public bool Complete { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string Status { get; set; } = "";
    public ExtractionProvenance Provenance { get; set; } = new();
}

public sealed class ExtractionProvenance
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string RequestedUrl { get; set; } = "";
    public string Method { get; set; } = "unknown";
    public string SourceKind { get; set; } = "unknown";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.Now;
    public bool SearchVerified { get; set; }
    public bool ScopeVerified { get; set; }
    public string ScopeFingerprint { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
}

public sealed record ExportResult(string Path, int Rows, int Columns, int TruncatedCells);
