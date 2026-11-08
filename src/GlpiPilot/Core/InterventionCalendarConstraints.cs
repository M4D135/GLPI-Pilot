using System.Globalization;
using System.IO;

namespace GlpiPilot.Core;

public static partial class InterventionCalendarService
{
    public static IReadOnlyList<CalendarConflict> Conflicts(IReadOnlyList<CalendarOccurrence> occurrences, IEnumerable<CalendarResource>? resources = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (occurrences.Count > MaximumOccurrences) throw new InvalidDataException("Trop d’occurrences à contrôler ; réduisez la période.");
        var resourceMap = (resources ?? []).GroupBy(value => value.Key, Keys).ToDictionary(group => group.Key, group => group.First(), Keys);
        foreach (var resource in resourceMap.Values) ValidateResource(resource);
        var active = occurrences.Where(value => value.State != "cancelled").GroupBy(value => value.Key, Keys).Select(group => group.First()).ToArray();
        if (active.Any(value => value.EndUtc <= value.StartUtc || value.ParticipantKeys is null || value.TravelBeforeMinutes is < 0 or > 1440 || value.TravelAfterMinutes is < 0 or > 1440))
            throw new InvalidDataException("Une réservation contient un intervalle invalide.");
        var result = new List<CalendarConflict>();
        foreach (var assignment in active.SelectMany(value => value.ParticipantKeys.Distinct(Keys).Select(key => (Key: key, Occurrence: value))).GroupBy(value => value.Key, Keys))
        {
            ct.ThrowIfCancellationRequested(); var assigned = assignment.Select(value => value.Occurrence).OrderBy(value => value.StartUtc).ThenBy(value => value.Key, Keys).ToArray();
            var resource = resourceMap.GetValueOrDefault(assignment.Key); var name = resource?.Label ?? assignment.Key;
            for (var i = 0; i < assigned.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); var first = assigned[i];
                for (var j = i + 1; j < assigned.Length; j++)
                {
                    if (result.Count >= 50000) throw new InvalidDataException("Trop de conflits à détailler ; réduisez le périmètre du calendrier.");
                    var second = assigned[j];
                    // A later event can reserve up to one day before its own start.
                    if (second.StartUtc.AddMinutes(-1440) >= first.EndUtc.AddMinutes(first.TravelAfterMinutes)) break;
                    if (first.StartUtc < second.EndUtc && second.StartUtc < first.EndUtc)
                    {
                        var unavailable = first.Kind == "unavailability" || second.Kind == "unavailability";
                        result.Add(new(unavailable ? "unavailable" : "overlap", "danger", name + (unavailable ? " est indisponible pendant cette intervention." : " a deux interventions qui se chevauchent."), assignment.Key, [first.Key, second.Key])
                        { StartUtc = first.StartUtc > second.StartUtc ? first.StartUtc : second.StartUtc, EndUtc = first.EndUtc < second.EndUtc ? first.EndUtc : second.EndUtc });
                    }
                    else if (first.EndUtc.AddMinutes(first.TravelAfterMinutes) > second.StartUtc.AddMinutes(-second.TravelBeforeMinutes))
                        result.Add(new("travel", "warning", "Le temps de déplacement déclaré entre ces interventions est insuffisant pour " + name + ".", assignment.Key, [first.Key, second.Key])
                        { StartUtc = first.EndUtc, EndUtc = second.StartUtc });
                }
            }
            if (resource is null)
            {
                foreach (var item in assigned.Where(item => item.Kind != "unavailability")) result.Add(new("unknownAvailability", "info", "Disponibilité non renseignée pour " + name + ".", assignment.Key, [item.Key]));
                continue;
            }
            if (!resource.Active)
                foreach (var item in assigned.Where(item => item.Kind != "unavailability")) result.Add(new("inactiveResource", "warning", name + " est une ressource désactivée.", assignment.Key, [item.Key]));
            var zone = Zone(resource.TimeZoneId);
            foreach (var item in assigned.Where(item => item.Kind != "unavailability"))
            {
                ct.ThrowIfCancellationRequested();
                var start = TimeZoneInfo.ConvertTime(item.StartUtc.AddMinutes(-item.TravelBeforeMinutes), zone).DateTime; var end = TimeZoneInfo.ConvertTime(item.EndUtc.AddMinutes(item.TravelAfterMinutes), zone).DateTime;
                var unknown = false; var outside = false;
                for (var day = start.Date; day < end; day = day.AddDays(1))
                {
                    var low = day == start.Date ? start.TimeOfDay.TotalMinutes : 0; var high = day == end.Date ? end.TimeOfDay.TotalMinutes : 1440;
                    if (high <= low) continue;
                    var periods = resource.WorkPeriods.Where(value => value.Day == IsoDay(day.DayOfWeek)).ToArray(); var exception = resource.CapacityDays.FirstOrDefault(value => value.Date == day.ToString(DateFormat, CultureInfo.InvariantCulture));
                    if (resource.WorkPeriods.Count > 0)
                    {
                        var covered = periods.Sum(period => Math.Max(0, Math.Min(high, period.EndMinute) - Math.Max(low, period.StartMinute)));
                        outside |= covered + 0.001 < high - low;
                    }
                    else
                    {
                        if (low < 720 && exception?.MorningMinutes is null || high > 720 && exception?.AfternoonMinutes is null) unknown = true;
                    }
                    if (low < 720 && exception?.MorningMinutes == 0 || high > 720 && exception?.AfternoonMinutes == 0) outside = true;
                }
                if (outside) result.Add(new("outsideHours", "warning", "L’intervention dépasse les plages de présence ou empiète sur une indisponibilité déclarée de " + name + ".", assignment.Key, [item.Key]));
                else if (unknown) result.Add(new("unknownAvailability", "info", "Les plages de présence de " + name + " ne sont pas entièrement renseignées.", assignment.Key, [item.Key]));
            }
            // Capacity is counted once per participant and occurrence, not once per linked ticket.
            var work = assigned.Where(item => item.Kind != "unavailability").ToArray();
            if (work.Length == 0) continue;
            var firstDay = work.Min(value => TimeZoneInfo.ConvertTime(value.StartUtc.AddMinutes(-value.TravelBeforeMinutes), zone).Date); var lastDay = work.Max(value => TimeZoneInfo.ConvertTime(value.EndUtc.AddMinutes(value.TravelAfterMinutes).AddTicks(-1), zone).Date);
            if ((lastDay - firstDay).TotalDays > 800) throw new InvalidDataException("Période de capacité trop étendue.");
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                var dayName = day.ToString(DateFormat, CultureInfo.InvariantCulture); var exception = resource.CapacityDays.FirstOrDefault(value => value.Date == dayName);
                var periods = resource.WorkPeriods.Where(value => value.Day == IsoDay(day.DayOfWeek)).ToArray();
                var capacityWindows = new List<(int Low, int High, int Capacity, string Label)>();
                if (exception?.MorningMinutes is int morning) capacityWindows.Add((0, 720, morning, "matin"));
                if (exception?.AfternoonMinutes is int afternoon) capacityWindows.Add((720, 1440, afternoon, "après-midi"));
                foreach (var period in periods.Where(value => value.CapacityMinutes.HasValue))
                {
                    // A declaration such as “180 minutes in 08:00–16:00” does not
                    // declare 90 minutes in each half-day. Never invent that split.
                    if (period.StartMinute < 720 && exception?.MorningMinutes is not null || period.EndMinute > 720 && exception?.AfternoonMinutes is not null) continue;
                    capacityWindows.Add((period.StartMinute, period.EndMinute, period.CapacityMinutes!.Value,
                        $"{period.StartMinute / 60:00}:{period.StartMinute % 60:00}–{period.EndMinute / 60:00}:{period.EndMinute % 60:00}"));
                }
                foreach (var window in capacityWindows)
                {
                    var (low, high, cap, label) = window;
                    var affected = new List<CalendarOccurrence>(); double reserved = 0;
                    foreach (var item in work)
                    {
                        var start = TimeZoneInfo.ConvertTime(item.StartUtc.AddMinutes(-item.TravelBeforeMinutes), zone).DateTime; var end = TimeZoneInfo.ConvertTime(item.EndUtc.AddMinutes(item.TravelAfterMinutes), zone).DateTime;
                        var segmentStart = start > day.AddMinutes(low) ? start : day.AddMinutes(low); var segmentEnd = end < day.AddMinutes(high) ? end : day.AddMinutes(high);
                        if (segmentEnd <= segmentStart) continue; reserved += (segmentEnd - segmentStart).TotalMinutes; affected.Add(item);
                    }
                    if (reserved > cap + 0.001)
                        result.Add(new("capacity", "warning", $"{name} : {reserved:0} min réservées pour {cap} min de capacité déclarée le {day:dd/MM/yyyy} ({label}).", assignment.Key, affected.Select(value => value.Key).ToArray()));
                }
            }
        }
        return result;
    }

    public static CalendarSummary Summarize(IEnumerable<CalendarOccurrence> occurrences)
    {
        var unique = occurrences.GroupBy(value => value.Key, Keys).Select(group => group.First()).ToArray(); var active = unique.Where(value => value.State != "cancelled").ToArray();
        var work = active.Where(value => value.Kind != "unavailability").ToArray(); var unavailable = active.Where(value => value.Kind == "unavailability").ToArray();
        return new(work.Length, unavailable.Length, work.Count(value => value.State == "done"), unique.Count(value => value.State == "cancelled"), work.Count(value => value.ParticipantKeys.Count == 0),
            work.SelectMany(value => value.Tickets).Select(value => value.Key).Distinct(Keys).Count(), work.Sum(value => value.DurationMinutes) / 60,
            work.Sum(value => value.DurationMinutes * value.ParticipantKeys.Distinct(Keys).Count()) / 60, unavailable.Sum(value => value.DurationMinutes) / 60);
    }

    /// <summary>Proposes times only; unknown presence is visible in Warnings and never represented as verified availability.</summary>
    public static IReadOnlyList<CalendarSlotSuggestion> SuggestSlots(CalendarIntervention proposed, IReadOnlyList<CalendarOccurrence> existing,
        IEnumerable<CalendarResource>? resources, DateTimeOffset fromUtc, DateTimeOffset toUtc, int maximum = 8, CancellationToken ct = default,
        Func<CalendarOccurrence, IReadOnlyList<CalendarConflict>>? additionalConstraints = null)
    {
        Validate(proposed); Range(fromUtc, toUtc);
        if ((toUtc - fromUtc).TotalDays > 32 || maximum is < 1 or > 50) throw new InvalidDataException("Les propositions sont limitées à 31 jours et 50 créneaux.");
        var participantKeys = proposed.ParticipantKeys.ToHashSet(Keys);
        var resourceList = (resources ?? []).Where(value => participantKeys.Contains(value.Key)).ToArray(); var zone = Zone(proposed.TimeZoneId); var duration = ParseLocal(proposed.EndLocal) - ParseLocal(proposed.StartLocal);
        var related = existing.Where(value => value.State != "cancelled" && value.ParticipantKeys.Any(participantKeys.Contains)).ToArray();
        if (duration <= TimeSpan.Zero || duration.TotalDays > 1) throw new InvalidDataException("Les propositions automatiques concernent les interventions d’une journée maximum avec un intervalle local positif.");
        var localFrom = TimeZoneInfo.ConvertTime(fromUtc, zone).DateTime; var localTo = TimeZoneInfo.ConvertTime(toUtc, zone).DateTime;
        var first = new DateTime(localFrom.Year, localFrom.Month, localFrom.Day, localFrom.Hour, localFrom.Minute / 15 * 15, 0);
        if (first < localFrom) first = first.AddMinutes(15);
        var result = new List<CalendarSlotSuggestion>(); var checks = 0;
        for (var start = first; start + duration <= localTo; start = start.AddMinutes(15))
        {
            ct.ThrowIfCancellationRequested(); if (++checks > 3100) break;
            if (proposed.AllDay ? start.TimeOfDay != TimeSpan.Zero : start.Hour < 7 || (start + duration).Date != start.Date || (start + duration).TimeOfDay.TotalMinutes > 20 * 60) continue;
            var candidate = proposed.Copy(); candidate.StartLocal = FormatLocal(start); candidate.EndLocal = FormatLocal(start + duration); candidate.Recurrence = null; candidate.Exceptions.Clear();
            CalendarOccurrence occurrence;
            try { occurrence = Expand([candidate], fromUtc, toUtc, ct).Single(); }
            catch (InvalidDataException) { continue; }
            // Limit each proposal calculation to relevant participants and nearby
            // dates; capacity checks still receive every booking on those dates.
            var scope = related.Where(value => value.Key != occurrence.Key && value.EndUtc > occurrence.StartUtc.AddDays(-2) && value.StartUtc < occurrence.EndUtc.AddDays(2)).Append(occurrence).ToArray();
            var conflicts = Conflicts(scope, resourceList, ct).Where(value => value.OccurrenceKeys.Contains(occurrence.Key, Keys))
                .Concat(additionalConstraints?.Invoke(occurrence) ?? Array.Empty<CalendarConflict>()).ToArray();
            if (conflicts.Any(value => value.Severity is "warning" or "danger")) continue;
            var warnings = conflicts.Where(value => value.Severity == "info").Select(value => value.Message).Distinct().ToList();
            if (candidate.ParticipantKeys.Count == 0) warnings.Add("Aucun technicien affecté : les disponibilités restent à vérifier.");
            var reasons = new List<string> { "Aucun chevauchement avec les réservations connues." }; var score = warnings.Count == 0 ? 100 : 60;
            if (warnings.Count == 0 && candidate.ParticipantKeys.Count > 0) reasons.Add("Créneau inclus dans les plages de présence déclarées.");
            if (candidate.SiteId.Length > 0 && existing.Any(value => value.State != "cancelled" && value.SiteId == candidate.SiteId
                    && value.ParticipantKeys.Intersect(candidate.ParticipantKeys, Keys).Any() && Math.Min(Math.Abs((value.EndUtc - occurrence.StartUtc).TotalMinutes), Math.Abs((value.StartUtc - occurrence.EndUtc).TotalMinutes)) <= 90))
            { score += 20; reasons.Add("Une intervention proche est déjà prévue sur ce site pour un participant."); }
            result.Add(new(candidate.StartLocal, candidate.EndLocal, occurrence.StartUtc, occurrence.EndUtc, score, reasons, warnings));
        }
        // Avoid returning eight nearly identical adjacent slots; retain choices at least 30 minutes apart.
        var selected = new List<CalendarSlotSuggestion>();
        foreach (var slot in result.OrderByDescending(value => value.Score).ThenBy(value => value.StartUtc))
        {
            if (selected.Any(value => Math.Abs((value.StartUtc - slot.StartUtc).TotalMinutes) < 30)) continue;
            selected.Add(slot); if (selected.Count == maximum) break;
        }
        return selected;
    }
}
