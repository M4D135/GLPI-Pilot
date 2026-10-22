namespace GlpiPilot.Core;

/// <summary>The last-modified token is evidence from GLPI, never the workstation clock.</summary>
public sealed class TicketLibrarySeed
{
    public TicketReference Ticket { get; set; } = new("", "");
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int? StatusCode { get; set; }
    public string Category { get; set; } = "";
    public string LastModifiedRaw { get; set; } = "";
    public DateTimeOffset? LastModifiedAt { get; set; }
    /// <summary>Reliable identifies an unambiguous date_mod field/token; precision still limits cache reuse.</summary>
    public bool LastModifiedReliable { get; set; }
    public string LastModifiedPrecision { get; set; } = "unknown";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
    public List<string> IndexHeaders { get; set; } = new();
    public List<string> IndexValues { get; set; } = new();
}

public sealed class TicketLibraryCoverage
{
    public int TabsDiscovered { get; set; }
    public int TabsLoaded { get; set; }
    public bool Complete { get; set; }
    public List<string> Limitations { get; set; } = new();
}

/// <summary>Format 1 public JSON document. Original GLPI strings are kept unchanged.</summary>
public sealed class TicketLibraryDocument
{
    public int Format { get; set; } = 1;
    public string Key { get; set; } = "";
    public string Installation { get; set; } = "";
    public TicketLibrarySeed Seed { get; set; } = new();
    public TicketDetailResult? Detail { get; set; }
    public TicketLibraryCoverage Coverage { get; set; } = new();
    public string ReaderSignature { get; set; } = "";
    public bool DetailComplete { get; set; }
    public bool DetailsCurrent { get; set; }
    /// <summary>A complete/partial body received by a capture whose final index has not confirmed it yet.</summary>
    public bool ConfirmationPending { get; set; }
    /// <summary>The acquisition attempt; an old proof cannot confirm a later interrupted read of identical bytes.</summary>
    public string ConfirmationId { get; set; } = "";
    /// <summary>Confirmation applies to this exact content hash, never to a different retained conflict.</summary>
    public string ConfirmedContentHash { get; set; } = "";
    public string DetailModifiedRaw { get; set; } = "";
    public DateTimeOffset? DetailModifiedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public string ContentHash { get; set; } = "";
    public long LocalRevision { get; set; }
    public long CentralRevision { get; set; }
    public bool Pending { get; set; }
    public int Conflicts { get; set; }
}

public sealed class TicketLibraryCachePolicy
{
    public bool ForceRefresh { get; set; }
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumAge { get; set; } = TimeSpan.FromDays(7);
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TicketLibraryState
{
    public long TicketCount { get; set; }
    public long ResolvedCount { get; set; }
    public long ContentCount { get; set; }
    public long PendingCount { get; set; }
    public long ConflictCount { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

public sealed class TicketLibrarySearchQuery
{
    public string Text { get; set; } = "";
    public string Installation { get; set; } = "";
    public bool ResolvedOnly { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 30;
}

public sealed class TicketLibraryCitation
{
    public string ChunkKey { get; set; } = "";
    public string Key { get; set; } = "";
    public TicketReference Ticket { get; set; } = new("", "");
    public string Section { get; set; } = "";
    public string SectionKey { get; set; } = "";
    public int Row { get; set; }
    public string Excerpt { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset? ObservedAt { get; set; }
    public bool IsSolution { get; set; }
    public bool Current { get; set; }
    public string ContentHash { get; set; } = "";
}

public sealed class TicketLibrarySearchRow
{
    public string Key { get; set; } = "";
    public TicketReference Ticket { get; set; } = new("", "");
    public string Installation { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int? StatusCode { get; set; }
    public string Category { get; set; } = "";
    public string LastModifiedRaw { get; set; } = "";
    public DateTimeOffset? LastModifiedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public bool DetailComplete { get; set; }
    public bool DetailsCurrent { get; set; }
    public bool Pending { get; set; }
    public int Conflicts { get; set; }
    public double Score { get; set; }
    public List<TicketLibraryCitation> Citations { get; set; } = new();
}

public sealed class TicketLibrarySearchResult
{
    public List<TicketLibrarySearchRow> Rows { get; set; } = new();
    public long Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
}

public sealed class TicketLibraryConflict
{
    public string Id { get; set; } = "";
    public string TicketKey { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    public TicketLibraryDocument Current { get; set; } = new();
    public TicketLibraryDocument Incoming { get; set; } = new();
}

public sealed record TicketLibraryWriteResult(TicketLibraryDocument Document, string Outcome, bool ConflictCreated = false);
public sealed record TicketLibrarySyncProgress(string Phase, int Pushed, int Pulled, int Conflicts, long Pending);
public sealed class TicketLibrarySyncResult
{
    public int Pushed { get; set; }
    public int Pulled { get; set; }
    public int Conflicts { get; set; }
    public long Pending { get; set; }
    public long Cursor { get; set; }
}
