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
    // Absent in older archives. Never infer these observations from a warning's wording.
    public ExtractionQuality? Quality { get; set; }
}

/// <summary>Independent receipt and comparison observations; no cell contents or identifiers.</summary>
public sealed class ExtractionQuality
{
    public long? SourceRows { get; set; }
    public int? ExpectedRows { get; set; }
    public bool? AllPagesRequested { get; set; }
    public bool? RowCountVerified { get; set; }
    public bool? FinalTotalVerified { get; set; }
    public long UnusualControlCount { get; set; }
    public long C1ControlCount { get; set; }
    public ExtractionSampleObservation? InitialSample { get; set; }
    public ExtractionSampleObservation? FinalSample { get; set; }

    // This describes receipt only. It does not certify scope, source values, history or alerts.
    public bool ReceptionComplete => AllPagesRequested == true && RowCountVerified == true && FinalTotalVerified != false;
    public bool? SampleIdentityVerified => Together(InitialSample?.IdentityVerified, FinalSample?.IdentityVerified);
    public bool? RepresentationVerified => Together(InitialSample?.RepresentationVerified, FinalSample?.RepresentationVerified);

    private static bool? Together(bool? initial, bool? final) => initial == false || final == false
        ? false : initial == true && final == true ? true : null;
}

public sealed class ExtractionSampleObservation
{
    public bool? IdentityVerified { get; set; }
    public bool? RepresentationVerified { get; set; }
    public int SampleSize { get; set; }
    public int MissingSampleIds { get; set; }
    public int ComparedCells { get; set; }
    public int DifferentCells { get; set; }
    public int EquivalentFormatCells { get; set; }
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
    public string ObservedSort { get; set; } = "";
    public bool Resumed { get; set; }
    public DateTimeOffset? ResumeStartedAt { get; set; }
    public Dictionary<string, double> StageSeconds { get; set; } = new(StringComparer.Ordinal);
}

public sealed record ExportResult(string Path, int Rows, int Columns, int TruncatedCells);
