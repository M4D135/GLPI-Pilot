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
    public List<TicketObservedItem> ObservedItems { get; set; } = new();
}

/// <summary>Only visible, explicitly labelled values; missing values remain empty.</summary>
public sealed class TicketObservedItem
{
    [System.Text.Json.Serialization.JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("identity")]
    public string Identity { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("sourceTabKey")]
    public string SourceTabKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("sourceTabSelector")]
    public string SourceTabSelector { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("sourceSelector")]
    public string SourceSelector { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("sourceSection")]
    public string SourceSection { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("date")]
    public string Date { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("author")]
    public string Author { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("authorRole")]
    public string AuthorRole { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("owner")]
    public string Owner { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("due")]
    public string Due { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("plannedStart")]
    public string PlannedStart { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("plannedEnd")]
    public string PlannedEnd { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("completedAt")]
    public string CompletedAt { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("requestedAt")]
    public string RequestedAt { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("waitingStart")]
    public string WaitingStart { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("waitingEnd")]
    public string WaitingEnd { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("waitingReason")]
    public string WaitingReason { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("relatedType")]
    public string RelatedType { get; set; } = "";
}
