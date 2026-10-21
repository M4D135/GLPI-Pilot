namespace GlpiPilot.Core;

public sealed class TicketDetailSettings
{
    public bool AutoLoadOnSelection { get; set; } = true;
    public string IdColumn { get; set; } = "";
    public string UrlTemplate { get; set; } = "";
    public bool IncludeAutoDetected { get; set; } = true;
    public bool AutoReadTreatment { get; set; } = true;
    public int MaxRowsPerSection { get; set; } = 5000;
    public List<TicketSectionDefinition> Sections { get; set; } = new();

    public TicketDetailSettings Copy()
    {
        var copy = (TicketDetailSettings)MemberwiseClone();
        copy.Sections = Sections?.Where(section => section is not null).Select(section => section.Copy()).ToList() ?? new();
        copy.Normalize();
        return copy;
    }

    internal void Normalize()
    {
        IdColumn ??= "";
        UrlTemplate ??= "";
        Sections ??= new();
        Sections.RemoveAll(section => section is null);
        foreach (var section in Sections) section.Normalize();
    }
}

public sealed class TicketSectionDefinition
{
    public string Name { get; set; } = "";
    public string Selector { get; set; } = "";
    public string Kind { get; set; } = "table";
    public string TabSelector { get; set; } = "";

    public TicketSectionDefinition Copy()
    {
        var copy = (TicketSectionDefinition)MemberwiseClone();
        copy.Normalize();
        return copy;
    }

    internal void Normalize()
    {
        Name ??= "";
        Selector ??= "";
        Kind ??= "table";
        TabSelector ??= "";
    }
}

public sealed record TicketReference(string Id, string Url);

public sealed class TicketDetailSection
{
    public string Name { get; set; } = "";
    public string Selector { get; set; } = "";
    public string Kind { get; set; } = "table";
    public string TabSelector { get; set; } = "";
    public string TabName { get; set; } = "";
    public string TabKey { get; set; } = "";
    public ExtractionResult Data { get; set; } = new();
}

public sealed class TicketDetailTab
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Selector { get; set; } = "";
    public string Category { get; set; } = "other";
    public bool IsActive { get; set; }
    public bool Loaded { get; set; }
}

public sealed class TicketDetailResult
{
    public TicketReference Ticket { get; set; } = new("", "");
    public string Title { get; set; } = "";
    public DateTimeOffset ReadAt { get; set; } = DateTimeOffset.Now;
    public List<TicketDetailSection> Sections { get; set; } = new();
    public List<TicketDetailTab> Tabs { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
