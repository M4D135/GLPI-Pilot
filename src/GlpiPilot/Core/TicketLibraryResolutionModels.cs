namespace GlpiPilot.Core;

public sealed class TicketLibraryResolutionResult
{
    public string SourceKey { get; set; } = "";
    public TicketReference SourceTicket { get; set; } = new("", "");
    public string SourceTitle { get; set; } = "";
    public string Installation { get; set; } = "";
    public bool SourceContentConfirmed { get; set; }
    public bool Manual { get; set; }
    public bool Available { get; set; }
    public string Message { get; set; } = "";
    public List<string> Terms { get; set; } = new();
    public long CandidatesFound { get; set; }
    public int CandidatesExamined { get; set; }
    public int DocumentsRechecked { get; set; }
    public int CandidateLimit { get; set; } = TicketLibraryResolutionService.MaximumCandidates;
    public int ResultLimit { get; set; } = TicketLibraryResolutionService.MaximumResults;
    public bool Limited { get; set; }
    public List<TicketLibraryResolutionMatch> Matches { get; set; } = new();
}

public sealed class TicketLibraryResolutionMatch
{
    public string Key { get; set; } = "";
    public TicketReference Ticket { get; set; } = new("", "");
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTimeOffset? ObservedAt { get; set; }
    public List<string> TitleTerms { get; set; } = new();
    public List<string> ContentTerms { get; set; } = new();
    public bool SameCategory { get; set; }
    public List<TicketLibraryCitation> Citations { get; set; } = new();
}

internal sealed record TicketLibraryResolutionEvidence(TicketLibrarySearchRow Ticket, IReadOnlyList<TicketLibraryCitation> Citations);
