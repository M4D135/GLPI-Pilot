namespace GlpiPilot.Core;

/// <summary>Observed tickets in the active GLPI entity scope, with no ticket search restriction.</summary>
public sealed class TicketLibraryIndex
{
    public string SourceUrl { get; set; } = "";
    public string ScopeDescription { get; set; } = "Tous les statuts hors corbeille, dans le périmètre d’entités actif de la session GLPI";
    public string ScopeFingerprint { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TicketLibrarySeed> Tickets { get; set; } = new();
    public int? ExpectedTotal { get; set; }
    public int PagesRead { get; set; }
    public bool AllStatuses { get; set; }
    public bool Complete { get; set; }
    public bool SearchRestored { get; set; }
    public List<string> Limitations { get; set; } = new();
}

/// <summary>Completeness covers readable ticket sections and document references, never attachment binaries.</summary>
public sealed class TicketLibraryDetailRead
{
    public TicketDetailResult Detail { get; set; } = new();
    public TicketLibraryCoverage Coverage { get; set; } = new();
    public string ReaderSignature { get; set; } = TicketLibraryAcquisition.ReaderSignature;
}

public static class TicketLibraryAcquisition
{
    // v4 (3.13.4) opens the ticket on its form tab and reads that tab first. v3 (3.13.3) stopped reading the GLPI tabs
    // without ticket knowledge: statistics, costs, links and the All view.
    public const string ReaderSignature = "glpi-browser-library-v4-confirmed";

    // A complete read by an earlier reader is kept until GLPI modifies the ticket (TicketLibraryRules.CanSkip),
    // and a collection it paused can be resumed by the current reader.
    public static bool IsEarlierReader(string signature) => signature is "glpi-browser-library-v2-confirmed" or "glpi-browser-library-v3-confirmed";

    public static bool Continues(string runReader, string reader) => runReader == reader || reader == ReaderSignature && IsEarlierReader(runReader);
}
