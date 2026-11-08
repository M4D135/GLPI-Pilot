using System.Globalization;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    /// <summary>A tour keeps ownership of its visits; its candidate projection joins the same reservations before validation.</summary>
    private async Task<IReadOnlyList<CalendarConflict>> CalendarTourIssuesAsync(ScheduledTour proposed, CancellationToken ct)
    {
        if (proposed.Stops.Count == 0) return Array.Empty<CalendarConflict>();
        var dates = proposed.Stops.Select(stop => CalendarDate(stop.Date.Length > 0 ? stop.Date : proposed.Date)).ToArray();
        var from = dates.Min(); var to = dates.Max().AddDays(1);
        if (to.DayNumber - from.DayNumber > 732) throw new System.IO.InvalidDataException("Le contrôle commun d’une tournée est limité à 732 jours ; répartissez-la en tournées plus courtes.");
        var window = new CalendarWindow(InterventionCalendarService.ResolveLocal(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00", proposed.TimeZoneId),
            InterventionCalendarService.ResolveLocal(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00", proposed.TimeZoneId),
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), proposed.TimeZoneId);
        var documents = await CalendarDocumentsAsync(window, ct).ConfigureAwait(false); var resources = await CalendarResourcesAsync(window, documents, ct).ConfigureAwait(false);
        var expansion = InterventionCalendarService.ExpandDetailed(documents.Select(CalendarWorkspaceDocuments.ReadIntervention), window.From, window.To, ct);
        var values = expansion.Occurrences.Concat(CalendarTours(window, ct, proposed)).ToArray();
        var prefix = "tour:" + proposed.Id + ":stop:"; var keys = values.Where(value => value.TourId == proposed.Id).Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        return CalendarIssues(values, resources, ct).Concat(_calendarTourIssues)
            .Where(issue => issue.OccurrenceKeys.Any(key => keys.Contains(key) || key.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
    }
}
