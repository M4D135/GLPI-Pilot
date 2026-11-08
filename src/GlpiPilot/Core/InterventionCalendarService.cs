using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Bounded, deterministic calendar calculations; no dependency on a loaded extract or a network connection.</summary>
public static partial class InterventionCalendarService
{
    public const int MaximumOccurrences = 25000;
    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm";
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly StringComparer Keys = StringComparer.Ordinal;

    public static DateTime ParseLocal(string value)
    {
        if (!DateTime.TryParseExact(value, LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) || result.Year is < 2000 or > 2100)
            throw new InvalidDataException("L’horaire doit être une date locale entre 2000 et 2100 au format AAAA-MM-JJTHH:mm.");
        return DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
    }
    public static string FormatLocal(DateTime value) => value.ToString(LocalFormat, CultureInfo.InvariantCulture);
    public static TimeZoneInfo Zone(string id)
    {
        if (!ValidText(id, 150, false) || id.Length == 0) throw new InvalidDataException("Choisissez un fuseau horaire.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidDataException("Le fuseau horaire « " + id + " » n’est pas disponible sur ce poste.", error); }
    }
    /// <summary>The optional UTC offset disambiguates only a repeated wall time; ordinary dates still follow the zone's DST rules.</summary>
    public static DateTimeOffset ResolveLocal(string local, string timeZoneId, int? ambiguousOffsetMinutes = null)
        => ResolveLocal(ParseLocal(local), Zone(timeZoneId), ambiguousOffsetMinutes);
    public static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo zone, int? ambiguousOffsetMinutes = null)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw new InvalidDataException($"L’horaire {FormatLocal(local)} n’existe pas dans le fuseau {zone.Id} (passage à l’heure d’été). Choisissez un autre horaire.");
        if (zone.IsAmbiguousTime(local))
        {
            var choices = zone.GetAmbiguousTimeOffsets(local);
            if (!ambiguousOffsetMinutes.HasValue || !choices.Any(value => value.TotalMinutes == ambiguousOffsetMinutes))
                throw new InvalidDataException($"L’horaire {FormatLocal(local)} apparaît deux fois dans le fuseau {zone.Id}. Précisez son décalage UTC en minutes ({string.Join(" ou ", choices.Select(value => value.TotalMinutes.ToString(CultureInfo.InvariantCulture)))}).");
            return new DateTimeOffset(local, TimeSpan.FromMinutes(ambiguousOffsetMinutes.Value)).ToUniversalTime();
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static void Validate(CalendarIntervention entry) => ValidateEntry(entry, false);
    private static void ValidateEntry(CalendarIntervention entry, bool replacement)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Guid.TryParse(entry.Id, out var id) || id == Guid.Empty || entry.Revision < 0 || entry.SeriesId is null || entry.SeriesId.Length > 0 && (!Guid.TryParse(entry.SeriesId, out var seriesId) || seriesId == Guid.Empty)
            || entry.OccurrenceOffset is < 0 or > 1000000 || !ValidText(entry.Title, 300, false) || string.IsNullOrWhiteSpace(entry.Title)
            || entry.Kind is not ("intervention" or "unavailability" or "meeting" or "maintenance")
            || entry.State is not ("planned" or "confirmed" or "inProgress" or "done" or "cancelled")
            || !ValidText(entry.SiteId, 120, false) || !ValidText(entry.Location, 500, false) || !ValidText(entry.Address, 2000, true)
            || !ValidText(entry.Notes, 8000, true) || !ValidText(entry.Contact, 2000, true) || !ValidText(entry.Equipment, 2000, true)
            || entry.TravelBeforeMinutes is < 0 or > 1440 || entry.TravelAfterMinutes is < 0 or > 1440
            || entry.ReminderMinutes is < 0 or > 43200 || entry.StartOffsetMinutes is < -840 or > 840 || entry.EndOffsetMinutes is < -840 or > 840
            || entry.ParticipantKeys is null || entry.ParticipantKeys.Count > 100 || entry.ParticipantKeys.Any(key => !ValidText(key, 500, false) || string.IsNullOrWhiteSpace(key))
            || entry.ParticipantKeys.Distinct(Keys).Count() != entry.ParticipantKeys.Count
            || entry.Tickets is null || entry.Tickets.Count > 2000 || entry.RequiredSkills is null || entry.RequiredSkills.Count > 50
            || entry.RequiredSkills.Any(value => !ValidText(value, 300, false) || string.IsNullOrWhiteSpace(value))
            || entry.Exceptions is null || entry.Exceptions.Count > 1000 || entry.History is null || entry.History.Count > 500)
            throw new InvalidDataException("Intervention invalide : vérifiez son identité, son titre, ses participants, ses champs et ses limites.");
        var zone = Zone(entry.TimeZoneId); var start = ParseLocal(entry.StartLocal); var end = ParseLocal(entry.EndLocal);
        var realStart = ResolveLocal(start, zone, entry.StartOffsetMinutes); var realEnd = ResolveLocal(end, zone, entry.EndOffsetMinutes);
        if (realEnd <= realStart || (realEnd - realStart).TotalDays > 31) throw new InvalidDataException("La fin doit suivre le début ; une intervention est limitée à 31 jours.");
        if (entry.AllDay && (start.TimeOfDay != TimeSpan.Zero || end.TimeOfDay != TimeSpan.Zero))
            throw new InvalidDataException("Une intervention sur la journée entière commence à minuit et se termine au minuit exclusif du jour suivant.");
        var tickets = new HashSet<string>(Keys);
        foreach (var ticket in entry.Tickets)
        {
            if (ticket is null || !ValidText(ticket.Key, 2000, false) || string.IsNullOrWhiteSpace(ticket.Key) || !tickets.Add(ticket.Key)
                || !ValidText(ticket.Installation, 1000, false) || string.IsNullOrWhiteSpace(ticket.Installation)
                || !ValidText(ticket.Id, 400, false) || string.IsNullOrWhiteSpace(ticket.Id) || !ValidText(ticket.Title, 2000, true)
                || !ValidText(ticket.Url, 3000, false) || ticket.Url.Length > 0 && (!Uri.TryCreate(ticket.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                throw new InvalidDataException("Chaque ticket lié doit avoir une référence unique, son installation et son identifiant ; une URL éventuelle doit être HTTP ou HTTPS.");
        }
        foreach (var history in entry.History)
            if (history is null || !ValidText(history.Actor, 500, false) || !ValidText(history.Action, 100, false) || !ValidText(history.Detail, 2000, true))
                throw new InvalidDataException("Une entrée de l’historique est invalide.");
        if (replacement && (entry.Recurrence is not null || entry.Exceptions.Count != 0)) throw new InvalidDataException("Une exception ne peut pas contenir une autre série.");
        if (entry.Recurrence is { } recurrence)
        {
            if (recurrence.Frequency is not ("daily" or "weekly" or "monthly") || recurrence.Interval is < 1 or > 366 || recurrence.Count is < 1 or > 10000
                || recurrence.WeekDays is null || recurrence.WeekDays.Any(day => day is < 1 or > 7) || recurrence.WeekDays.Distinct().Count() != recurrence.WeekDays.Count
                || recurrence.Until is null || recurrence.Until.Length > 0 && (!DateOnly.TryParseExact(recurrence.Until, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until) || until < DateOnly.FromDateTime(start) || until.Year > 2100))
                throw new InvalidDataException("Récurrence invalide : fréquence, intervalle, jours, nombre ou date de fin à vérifier.");
            if (recurrence.Frequency != "weekly" && recurrence.WeekDays.Count > 0)
                throw new InvalidDataException("Les jours de semaine s’appliquent uniquement à une récurrence hebdomadaire.");
            if (recurrence.Frequency == "weekly" && recurrence.WeekDays.Count > 0 && !recurrence.WeekDays.Contains(IsoDay(start.DayOfWeek)))
                throw new InvalidDataException("Le jour de la première intervention doit faire partie des jours de la récurrence hebdomadaire.");
        }
        else if (entry.Exceptions.Count > 0) throw new InvalidDataException("Une intervention sans récurrence ne peut pas porter d’exceptions de série.");
        var exceptionKeys = new HashSet<string>(Keys);
        foreach (var exception in entry.Exceptions)
        {
            if (exception is null || !exceptionKeys.Add(exception.OriginalStartLocal) || exception.Cancelled && exception.Replacement is not null
                || !exception.Cancelled && exception.Replacement is null)
                throw new InvalidDataException("Une exception doit annuler ou remplacer exactement une occurrence, sans doublon.");
            var original = ParseLocal(exception.OriginalStartLocal);
            if (OccurrenceIndex(entry, original) < 0) throw new InvalidDataException("L’exception ne correspond pas à une occurrence de cette série.");
            if (exception.Replacement is { } changed) ValidateEntry(changed, true);
        }
    }

    public static void ValidateResource(CalendarResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!ValidText(resource.Key, 500, false) || string.IsNullOrWhiteSpace(resource.Key) || !ValidText(resource.Label, 300, false) || string.IsNullOrWhiteSpace(resource.Label)
            || resource.TeamKeys is null || resource.TeamKeys.Count > 100 || resource.TeamKeys.Any(key => !ValidText(key, 500, false))
            || resource.WorkPeriods is null || resource.WorkPeriods.Count > 28 || resource.CapacityDays is null || resource.CapacityDays.Count > 732)
            throw new InvalidDataException("Ressource de planning invalide.");
        _ = Zone(resource.TimeZoneId);
        foreach (var period in resource.WorkPeriods)
            if (period is null || period.Day is < 1 or > 7 || period.StartMinute < 0 || period.EndMinute > 1440 || period.EndMinute <= period.StartMinute
                || period.CapacityMinutes < 0 || period.CapacityMinutes > period.EndMinute - period.StartMinute)
                throw new InvalidDataException("Une plage de présence ou sa capacité est invalide.");
        foreach (var day in resource.WorkPeriods.GroupBy(period => period.Day))
        {
            var ordered = day.OrderBy(period => period.StartMinute).ToArray();
            if (ordered.Skip(1).Where((period, index) => period.StartMinute < ordered[index].EndMinute).Any())
                throw new InvalidDataException("Deux plages de présence de cette ressource se chevauchent.");
        }
        var dates = new HashSet<string>(Keys);
        foreach (var day in resource.CapacityDays)
            if (day is null || !DateOnly.TryParseExact(day.Date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 2000 or > 2100 || !dates.Add(day.Date)
                || day.MorningMinutes is < 0 or > 720 || day.AfternoonMinutes is < 0 or > 720 || day.MorningMinutes is null && day.AfternoonMinutes is null)
                throw new InvalidDataException("Une exception de capacité est invalide ou dupliquée.");
    }

    public static IReadOnlyList<CalendarOccurrence> Expand(IEnumerable<CalendarIntervention> entries, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var expanded = ExpandDetailed(entries, fromUtc, toUtc, ct);
        if (expanded.Issues.Count > 0) throw new InvalidDataException(expanded.Issues[0].Message);
        return expanded.Occurrences;
    }
    /// <summary>Read projection keeps valid appointments visible and reports individual invalid DST occurrences for repair.</summary>
    public static CalendarExpansion ExpandDetailed(IEnumerable<CalendarIntervention> entries, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        Range(fromUtc, toUtc); ArgumentNullException.ThrowIfNull(entries);
        var occurrences = new List<CalendarOccurrence>(); var issues = new List<CalendarExpansionIssue>(); var ids = new HashSet<string>(Keys); var occurrenceKeys = new HashSet<string>(Keys); var count = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 10000) throw new InvalidDataException("Le calendrier est limité à 10 000 séries par calcul.");
            try
            {
                Validate(entry);
                if (!ids.Add(entry.Id)) throw new InvalidDataException("Deux séries portent la même identité.");
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentNullException)
            { issues.Add(new(entry?.Id ?? "", entry?.StartLocal ?? "", error.Message)); continue; }
            var zone = Zone(entry.TimeZoneId); var anchor = ParseLocal(entry.StartLocal); var duration = ParseLocal(entry.EndLocal) - anchor;
            var exceptions = entry.Exceptions.ToDictionary(value => value.OriginalStartLocal, Keys);
            var upperLocal = TimeZoneInfo.ConvertTime(toUtc, zone).DateTime.AddDays(1);
            var ordinal = -1;
            foreach (var original in Starts(entry, upperLocal, ct))
            {
                ordinal++; ct.ThrowIfCancellationRequested(); var originalText = FormatLocal(original);
                if (exceptions.ContainsKey(originalText)) continue;
                Add(entry, ordinal, originalText, original, original + duration, false);
            }
            // Moved exceptions are considered even if their original position lies outside the requested interval.
            foreach (var exception in entry.Exceptions)
            {
                ct.ThrowIfCancellationRequested(); var original = ParseLocal(exception.OriginalStartLocal);
                if (exception.Cancelled)
                { var cancelled = entry.Copy(); cancelled.State = "cancelled"; Add(cancelled, OccurrenceIndex(entry, original), exception.OriginalStartLocal, original, original + duration, true); }
                else if (exception.Replacement is { } replacement)
                    Add(replacement, OccurrenceIndex(entry, original), exception.OriginalStartLocal, ParseLocal(replacement.StartLocal), ParseLocal(replacement.EndLocal), true);
            }
            void Add(CalendarIntervention values, int index, string originalText, DateTime start, DateTime end, bool exception)
            {
                // Calendar reads often contain an old open-ended series. Do not
                // resolve every historic wall time just to display this week.
                // Two days safely cover all supported UTC offsets and DST folds.
                var approximate = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc));
                if (approximate >= toUtc.AddDays(2) || approximate.Add(end - start) <= fromUtc.AddDays(-2)) return;
                try
                {
                    var localZone = Zone(values.TimeZoneId); var startUtc = ResolveLocal(start, localZone, values.StartOffsetMinutes); var endUtc = ResolveLocal(end, localZone, values.EndOffsetMinutes);
                    if (endUtc <= startUtc) throw new InvalidDataException("L’heure réelle de fin doit suivre le début : " + FormatLocal(start));
                    if (startUtc >= toUtc || endUtc <= fromUtc) return;
                    if (occurrences.Count >= MaximumOccurrences) throw new CalendarLimitException();
                    var key = (entry.SeriesId.Length > 0 ? entry.SeriesId : entry.Id) + ":" + (entry.OccurrenceOffset + index).ToString(CultureInfo.InvariantCulture);
                    if (!occurrenceKeys.Add(key)) throw new InvalidDataException("Deux fragments de série réservent la même occurrence ; réconciliez leurs versions avant de planifier.");
                    occurrences.Add(new()
                    {
                        Key = key, InterventionId = entry.Id, Revision = entry.Revision, OriginalStartLocal = originalText,
                        StartLocal = FormatLocal(start), EndLocal = FormatLocal(end), StartUtc = startUtc, EndUtc = endUtc, TimeZoneId = values.TimeZoneId,
                        Title = values.Title, Kind = values.Kind, State = values.State, AllDay = values.AllDay, ParticipantKeys = values.ParticipantKeys.ToList(),
                        Tickets = values.Tickets.Select(ticket => ticket.Copy()).ToList(), SiteId = values.SiteId, Location = values.Location, Address = values.Address, Remote = values.Remote,
                        Notes = values.Notes, Contact = values.Contact, Equipment = values.Equipment, RequiredSkills = values.RequiredSkills.ToList(), TravelBeforeMinutes = values.TravelBeforeMinutes,
                        TravelAfterMinutes = values.TravelAfterMinutes, ReminderMinutes = values.ReminderMinutes, Recurring = entry.Recurrence is not null, Exception = exception
                    });
                }
                catch (InvalidDataException error)
                {
                    // Old invalid occurrences must not obscure an unrelated month. A +/- 2 day window also covers UTC offsets and overnight visits.
                    if (approximate < toUtc.AddDays(2) && approximate.Add(end - start) > fromUtc.AddDays(-2)) issues.Add(new(entry.Id, originalText, error.Message));
                }
            }
        }
        return new(occurrences.OrderBy(value => value.StartUtc).ThenBy(value => value.Key, Keys).ToArray(), issues);
    }

    private sealed class CalendarLimitException : Exception
    { public CalendarLimitException() : base("Plus de 25 000 occurrences : réduisez la période du calendrier.") { } }
    private static IEnumerable<DateTime> Starts(CalendarIntervention entry, DateTime throughLocal, CancellationToken ct = default)
    {
        var start = ParseLocal(entry.StartLocal); if (start > throughLocal) yield break;
        if (entry.Recurrence is not { } recurrence) { yield return start; yield break; }
        var until = recurrence.Until.Length == 0 ? new DateTime(2100, 12, 31, 23, 59, 0) : DateOnly.ParseExact(recurrence.Until, DateFormat, CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MaxValue);
        var through = throughLocal < until ? throughLocal : until; var emitted = 0;
        if (recurrence.Frequency == "monthly")
        {
            for (var month = 0; month <= 1212; month += recurrence.Interval)
            {
                ct.ThrowIfCancellationRequested(); var first = new DateTime(start.Year, start.Month, 1).AddMonths(month);
                if (first > through || first.Year > 2100) yield break;
                if (start.Day > DateTime.DaysInMonth(first.Year, first.Month)) continue;
                var candidate = first.AddDays(start.Day - 1).Add(start.TimeOfDay); if (candidate > through) yield break;
                if (recurrence.Count.HasValue && emitted >= recurrence.Count) yield break; emitted++; yield return candidate;
            }
            yield break;
        }
        var anchorMonday = start.Date.AddDays(1 - IsoDay(start.DayOfWeek));
        var days = recurrence.WeekDays.Count == 0 ? new HashSet<int> { IsoDay(start.DayOfWeek) } : recurrence.WeekDays.ToHashSet();
        for (var day = start; day <= through; day = day.AddDays(recurrence.Frequency == "daily" ? recurrence.Interval : 1))
        {
            ct.ThrowIfCancellationRequested();
            if (recurrence.Frequency == "weekly" && (!days.Contains(IsoDay(day.DayOfWeek)) || (int)((day.Date - anchorMonday).TotalDays / 7) % recurrence.Interval != 0)) continue;
            if (recurrence.Count.HasValue && emitted >= recurrence.Count) yield break; emitted++; yield return day;
        }
    }
    private static int OccurrenceIndex(CalendarIntervention entry, DateTime original)
    {
        var index = 0;
        foreach (var start in Starts(entry, original)) { if (start == original) return index; index++; }
        return -1;
    }
    private static DateTime? OccurrenceAt(CalendarIntervention entry, int index)
    {
        var position = 0;
        foreach (var start in Starts(entry, new DateTime(2100, 12, 31, 23, 59, 0))) { if (position++ == index) return start; }
        return null;
    }
    private static int IsoDay(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;
    private static bool ValidText(string? value, int maximum, bool multiline) => value is not null && value.Length <= maximum && !value.Any(ch => char.IsControl(ch) && !(multiline && ch is '\n' or '\r' or '\t'));
    private static void Range(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || (to - from).TotalDays > 733) throw new InvalidDataException("Choisissez une période positive ne dépassant pas 732 jours.");
    }

    public static CalendarIntervention WithOccurrence(CalendarIntervention original, string originalStartLocal, CalendarIntervention? replacement, bool cancelled = false)
    {
        Validate(original); var start = ParseLocal(originalStartLocal);
        if (original.Recurrence is null || OccurrenceIndex(original, start) < 0) throw new InvalidDataException("L’occurrence à modifier n’appartient pas à cette série.");
        if (!cancelled && replacement is null) throw new InvalidDataException("Le remplacement de l’occurrence est absent.");
        var next = original.Copy(); next.Exceptions.RemoveAll(value => value.OriginalStartLocal == originalStartLocal);
        CalendarIntervention? changed = null;
        if (!cancelled)
        {
            changed = replacement!.Copy(); changed.Id = original.Id; changed.Revision = original.Revision; changed.Recurrence = null; changed.Exceptions.Clear(); changed.History.Clear(); Validate(changed);
        }
        next.Exceptions.Add(new() { OriginalStartLocal = originalStartLocal, Cancelled = cancelled, Replacement = changed }); Validate(next); return next;
    }

    public static CalendarSeriesSplit SplitFollowing(CalendarIntervention original, string originalStartLocal, CalendarIntervention replacement)
    {
        Validate(original); var split = ParseLocal(originalStartLocal); var index = OccurrenceIndex(original, split);
        if (original.Recurrence is null || index < 0) throw new InvalidDataException("Le point de séparation n’appartient pas à cette série.");
        var before = original.Copy(); var following = replacement.Copy(); following.Id = Guid.NewGuid().ToString("N"); following.Revision = 0; following.Exceptions.Clear();
        following.SeriesId = original.SeriesId.Length > 0 ? original.SeriesId : original.Id; following.OccurrenceOffset = original.OccurrenceOffset + index;
        if (following.Recurrence is not null && original.Recurrence.Count.HasValue && following.Recurrence.Count == original.Recurrence.Count)
            following.Recurrence.Count = Math.Max(1, original.Recurrence.Count.Value - index);
        before.Exceptions.RemoveAll(value => ParseLocal(value.OriginalStartLocal) >= split);
        if (index == 0) { before.State = "cancelled"; before.Recurrence = null; before.Exceptions.Clear(); }
        else before.Recurrence!.Count = index;
        foreach (var exception in original.Exceptions.Where(value => ParseLocal(value.OriginalStartLocal) >= split))
        {
            var relative = OccurrenceIndex(original, ParseLocal(exception.OriginalStartLocal)) - index; var target = OccurrenceAt(following, relative);
            if (!target.HasValue || following.Recurrence is null) continue;
            var moved = JsonSerializer.Deserialize<CalendarOccurrenceException>(JsonSerializer.Serialize(exception))!; moved.OriginalStartLocal = FormatLocal(target.Value);
            if (moved.Replacement is { } individual) { individual.Id = following.Id; individual.Revision = 0; }
            following.Exceptions.Add(moved);
        }
        Validate(before); Validate(following); return new(before, following) { RemoveBefore = index == 0 };
    }

    /// <summary>Edits a complete series while preserving root identity and individually changed occurrences by their ordinal position.</summary>
    public static CalendarIntervention WithSeries(CalendarIntervention original, CalendarIntervention replacement)
    {
        Validate(original); var next = replacement.Copy(); next.Id = original.Id; next.Revision = original.Revision; next.SeriesId = original.SeriesId; next.OccurrenceOffset = original.OccurrenceOffset;
        next.Exceptions.Clear(); Validate(next);
        if (next.Recurrence is not null)
            foreach (var exception in original.Exceptions)
            {
                var index = OccurrenceIndex(original, ParseLocal(exception.OriginalStartLocal)); var target = OccurrenceAt(next, index); if (!target.HasValue) continue;
                var mapped = JsonSerializer.Deserialize<CalendarOccurrenceException>(JsonSerializer.Serialize(exception))!; mapped.OriginalStartLocal = FormatLocal(target.Value); next.Exceptions.Add(mapped);
            }
        Validate(next); return next;
    }

    public static CalendarIntervention AddHistory(CalendarIntervention original, string actor, string action, string detail, DateTimeOffset? at = null)
    {
        var copy = original.Copy(); copy.History.Add(new() { AtUtc = at ?? DateTimeOffset.UtcNow, Actor = actor, Action = action, Detail = detail });
        copy.History = copy.History.TakeLast(200).ToList(); Validate(copy); return copy;
    }
}
