using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>A durable planning object. Ticket snapshots and participants survive changes to the imported GLPI perimeter.</summary>
public sealed class CalendarIntervention
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Revision { get; set; }
    /// <summary>Root identity and ordinal offset survive splitting a series; empty uses Id.</summary>
    public string SeriesId { get; set; } = "";
    public int OccurrenceOffset { get; set; }
    public string Title { get; set; } = "Nouvelle intervention";
    public string Kind { get; set; } = "intervention";
    public string StartLocal { get; set; } = "";
    public string EndLocal { get; set; } = "";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public int? StartOffsetMinutes { get; set; }
    public int? EndOffsetMinutes { get; set; }
    public bool AllDay { get; set; }
    public List<string> ParticipantKeys { get; set; } = [];
    public List<CalendarTicketReference> Tickets { get; set; } = [];
    public string SiteId { get; set; } = "";
    public string Location { get; set; } = "";
    public string Address { get; set; } = "";
    public bool Remote { get; set; }
    public string State { get; set; } = "planned";
    public string Notes { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Equipment { get; set; } = "";
    public List<string> RequiredSkills { get; set; } = [];
    public int TravelBeforeMinutes { get; set; }
    public int TravelAfterMinutes { get; set; }
    public int? ReminderMinutes { get; set; }
    public CalendarRecurrence? Recurrence { get; set; }
    public List<CalendarOccurrenceException> Exceptions { get; set; } = [];
    public List<CalendarChange> History { get; set; } = [];
    public CalendarIntervention Copy() => JsonSerializer.Deserialize<CalendarIntervention>(JsonSerializer.Serialize(this))!;
}

public sealed class CalendarTicketReference
{
    public string Key { get; set; } = "";
    public string Installation { get; set; } = "";
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public CalendarTicketReference Copy() => (CalendarTicketReference)MemberwiseClone();
}

public sealed class CalendarRecurrence
{
    public string Frequency { get; set; } = "weekly";
    public int Interval { get; set; } = 1;
    /// <summary>ISO weekdays: Monday 1 to Sunday 7. Empty means the anchor's weekday.</summary>
    public List<int> WeekDays { get; set; } = [];
    /// <summary>Inclusive local date. Empty means no date limit (queries are always bounded).</summary>
    public string Until { get; set; } = "";
    public int? Count { get; set; }
}

public sealed class CalendarOccurrenceException
{
    /// <summary>The original wall clock start, never the moved date; this is the occurrence identity.</summary>
    public string OriginalStartLocal { get; set; } = "";
    public bool Cancelled { get; set; }
    /// <summary>A complete replacement without its own recurrence or exceptions.</summary>
    public CalendarIntervention? Replacement { get; set; }
}

public sealed class CalendarChange
{
    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class CalendarResource
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> TeamKeys { get; set; } = [];
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public List<CalendarWorkPeriod> WorkPeriods { get; set; } = [];
    public List<CalendarCapacityDay> CapacityDays { get; set; } = [];
    public bool Active { get; set; } = true;
}

public sealed class CalendarWorkPeriod
{
    public int Day { get; set; }
    public int StartMinute { get; set; } = 540;
    public int EndMinute { get; set; } = 720;
    /// <summary>Null = no declared capacity. It is not zero available time.</summary>
    public int? CapacityMinutes { get; set; }
}

public sealed class CalendarCapacityDay
{
    public string Date { get; set; } = "";
    public int? MorningMinutes { get; set; }
    public int? AfternoonMinutes { get; set; }
}

public sealed class CalendarOccurrence
{
    public string Key { get; set; } = "";
    public string InterventionId { get; set; } = "";
    public long Revision { get; set; }
    public string OriginalStartLocal { get; set; } = "";
    public string StartLocal { get; set; } = "";
    public string EndLocal { get; set; } = "";
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string TimeZoneId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "intervention";
    public string State { get; set; } = "planned";
    public bool AllDay { get; set; }
    public List<string> ParticipantKeys { get; set; } = [];
    public List<CalendarTicketReference> Tickets { get; set; } = [];
    public string SiteId { get; set; } = "";
    public string Location { get; set; } = "";
    public string Address { get; set; } = "";
    public bool Remote { get; set; }
    public string Notes { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Equipment { get; set; } = "";
    public List<string> RequiredSkills { get; set; } = [];
    public int TravelBeforeMinutes { get; set; }
    public int TravelAfterMinutes { get; set; }
    public int? ReminderMinutes { get; set; }
    public string Source { get; set; } = "calendar";
    /// <summary>Derived map reference; a commune marker is approximate and never substitutes for a known site address.</summary>
    public string CityId { get; set; } = "";
    public string TourId { get; set; } = "";
    public string StopId { get; set; } = "";
    public bool ReadOnly { get; set; }
    public bool Recurring { get; set; }
    public bool Exception { get; set; }
    public List<string> Warnings { get; set; } = [];
    public double DurationMinutes => (EndUtc - StartUtc).TotalMinutes;
}

public sealed record CalendarConflict(string Kind, string Severity, string Message, string ParticipantKey, IReadOnlyList<string> OccurrenceKeys)
{
    public DateTimeOffset? StartUtc { get; init; }
    public DateTimeOffset? EndUtc { get; init; }
}
public sealed record CalendarSlotSuggestion(string StartLocal, string EndLocal, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Score,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Warnings);
public sealed record CalendarSummary(int Interventions, int Unavailabilities, int Completed, int Cancelled, int Unassigned,
    int LinkedTickets, double InterventionHours, double PersonHours, double UnavailabilityHours);
public sealed record CalendarSeriesSplit(CalendarIntervention Before, CalendarIntervention Following)
{ public bool RemoveBefore { get; init; } }
public sealed record CalendarExpansionIssue(string InterventionId, string OriginalStartLocal, string Message);
public sealed record CalendarExpansion(IReadOnlyList<CalendarOccurrence> Occurrences, IReadOnlyList<CalendarExpansionIssue> Issues);
