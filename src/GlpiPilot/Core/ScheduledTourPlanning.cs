using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record TourStateChange(string From, string To, DateTimeOffset At, string Reason);
public sealed record TourRoadLeg(string Day, string TechnicianKey, string FromStop, string ToStop, string CoordinateFingerprint,
    double DistanceKm, double DurationMinutes, string Provider, DateTimeOffset RetrievedAt);
public sealed record TourWeatherObservation(string StopId, string Day, string Place, int? WeatherCode, double? MinimumCelsius,
    double? MaximumCelsius, double? RainProbabilityPercent, string Provider, DateTimeOffset RetrievedAt, string CoordinateFingerprint = "");
public sealed record ScheduledTourDayItem(string TourId, string Name, string Date, string State, string StartTime, string EndTime, string TechnicianKey, IReadOnlyList<string> TicketIds);
public sealed record TourWorkWindow(int FromMinute, int UntilMinute, int? CapacityMinutes = null);
public sealed record TourTechnicianAvailability(string TechnicianKey, string Day, int? CapacityMinutes, bool Unavailable,
    IReadOnlyList<string> Skills, IReadOnlyList<string> Territories)
{
    public IReadOnlyList<TourWorkWindow>? WorkWindows { get; init; }
    public int? MorningCapacityMinutes { get; init; }
    public int? AfternoonCapacityMinutes { get; init; }
    public TourTechnicianAvailability Copy() => this with { Skills = Skills.ToArray(), Territories = Territories.ToArray(), WorkWindows = WorkWindows?.Select(window => window with { }).ToArray() };
}
public sealed record TourAllocationIssue(string StopId, string Place, string Reason);
public sealed record TourAllocationResult(ScheduledTour Tour, int AssignedStops, IReadOnlyList<TourAllocationIssue> Unassigned);
public sealed record TourScenarioComparison(string Name, int Tickets, int Stops, int InterventionMinutes, int TravelMinutes, int WaitingMinutes,
    int MissedWindows, int ClosedSites, int MissedDueDates, int UnknownDueDates, double? RoadKm, int RoutedLegs, string Explanation);

public sealed partial class ScheduledTour
{
    public string State { get; set; } = "draft";
    public List<TourStateChange> StateHistory { get; set; } = new();
    public List<TourRoadLeg> RoadLegs { get; set; } = new();
    public List<TourWeatherObservation> Weather { get; set; } = new();
    public List<TourTechnicianAvailability> Availability { get; set; } = new();
    private void ValidateExtended()
    {
        if (State is not ("draft" or "ready" or "done") || StateHistory == null || StateHistory.Count > 100
            || StateHistory.Any(value => value == null || value.From is not ("draft" or "ready" or "done") || value.To is not ("draft" or "ready" or "done") || !Text(value.Reason, 1000, true))
            || RoadLegs == null || RoadLegs.Count > 1000 || Weather == null || Weather.Count > MaximumStops || Availability == null)
            throw new InvalidDataException("Cycle de vie ou données externes de tournée invalides.");
        ScheduledTourService.ValidateAvailability(Availability);
        foreach (var leg in RoadLegs)
            if (leg == null || !DateOnly.TryParseExact(leg.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || !Text(leg.TechnicianKey, 400) || !Text(leg.FromStop, 40) || !Text(leg.ToStop, 40)
                || leg.CoordinateFingerprint?.Length != 64 || !leg.CoordinateFingerprint.All(Uri.IsHexDigit)
                || !double.IsFinite(leg.DistanceKm) || leg.DistanceKm is < 0 or > 50000 || !double.IsFinite(leg.DurationMinutes) || leg.DurationMinutes is < 0 or > 43200 || !Text(leg.Provider, 1000))
                throw new InvalidDataException("Une mesure routière est invalide.");
        if (RoadLegs.Select(leg => (leg.Day, leg.TechnicianKey, leg.FromStop, leg.ToStop)).Distinct().Count() != RoadLegs.Count) throw new InvalidDataException("Deux mesures routières portent sur le même segment.");
        foreach (var weather in Weather)
            if (weather == null || !Text(weather.StopId, 40) || !Text(weather.Place, 300) || !Text(weather.Provider, 1000)
                || !DateOnly.TryParseExact(weather.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || weather.WeatherCode is < 0 or > 99 || !Finite(weather.MinimumCelsius, -100, 70) || !Finite(weather.MaximumCelsius, -100, 70) || !Finite(weather.RainProbabilityPercent, 0, 100)
                || weather.MinimumCelsius.HasValue && weather.MaximumCelsius.HasValue && weather.MinimumCelsius > weather.MaximumCelsius
                || weather.CoordinateFingerprint == null || weather.CoordinateFingerprint.Length > 0 && (weather.CoordinateFingerprint.Length != 64 || !weather.CoordinateFingerprint.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Une prévision météo est invalide.");
    }
    private static bool Finite(double? value, double min, double max) => !value.HasValue || double.IsFinite(value.Value) && value.Value >= min && value.Value <= max;
}

public sealed partial class ScheduledTourStore
{
    public IReadOnlyList<ScheduledTourDayItem> ForDate(DateOnly date, string technicianKey = "")
    {
        Validate(); var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); var key = TechnicianDashboardService.IdentityKey(technicianKey);
        return Tours.SelectMany(tour => tour.Stops.Where(stop => (stop.Date.Length == 0 ? tour.Date : stop.Date) == day)
            .GroupBy(stop => stop.TechnicianKey.Length == 0 ? tour.TechnicianKey : stop.TechnicianKey)
            .Where(group => key.Length == 0 || group.Key == key)
            .Select(group => new ScheduledTourDayItem(tour.Id, tour.Name, day, tour.State, tour.StartTime, tour.EndTime, group.Key, group.SelectMany(stop => stop.TicketIds).Distinct(StringComparer.Ordinal).ToArray())))
            .OrderBy(item => item.StartTime, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.CurrentCulture).ToArray();
    }
}

public static partial class ScheduledTourService
{
    public static string StateLabel(string state) => state switch { "ready" => "Prête", "done" => "Effectuée", _ => "Brouillon" };
    public static ScheduledTour PrepareEdit(ScheduledTour source, string reason)
    {
        source.Validate(); var copy = source.Copy();
        if (copy.State != "draft") { copy.StateHistory.Add(new(copy.State, "draft", DateTimeOffset.Now, reason)); copy.StateHistory = copy.StateHistory.TakeLast(100).ToList(); copy.State = "draft"; }
        copy.Validate(); return copy;
    }
    public static ScheduledTour Transition(ScheduledTour source, TourPlanningContext context, string target, string reason, DateTimeOffset? now = null)
    {
        source.Validate(); var instant = now ?? DateTimeOffset.Now;
        if (target is not ("draft" or "ready" or "done") || target == source.State) throw new InvalidDataException("Choisissez un autre état de préparation.");
        if (source.State == "done" && target != "draft" || target == "done" && source.State != "ready") throw new InvalidDataException("Une tournée doit être prête avant d’être effectuée ; revenez au brouillon pour la modifier.");
        if (string.IsNullOrWhiteSpace(reason) || !ScheduledTour.Text(reason, 1000, true)) throw new InvalidDataException("Indiquez le motif du changement d’état.");
        if (target == "ready")
        {
            var schedule = Calculate(source, context);
            if (!schedule.CanExportCalendar || source.Stops.Sum(stop => stop.TicketIds.Count) != source.Tickets.Count || source.Stops.Any(stop => (stop.TechnicianKey.Length == 0 ? source.TechnicianKey : stop.TechnicianKey).Length == 0)
                || schedule.Visits.Any(visit => visit.Late || visit.SiteClosed || visit.TechnicianUnavailable) || schedule.Technicians.Any(technician => technician.OutsideDay))
                throw new InvalidDataException("La tournée prête demande des tickets rapprochés et localisés, des techniciens affectés et un planning respectant les ouvertures et fenêtres horaires.");
        }
        if (target == "done" && source.Stops.Any(stop => DateOnly.ParseExact(stop.Date.Length == 0 ? source.Date : stop.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) > DateOnly.FromDateTime(instant.LocalDateTime)))
            throw new InvalidDataException("Une visite future ne peut pas être déclarée effectuée.");
        var copy = source.Copy(); copy.StateHistory.Add(new(copy.State, target, instant, reason.Trim())); copy.StateHistory = copy.StateHistory.TakeLast(100).ToList(); copy.State = target; copy.Validate(); return copy;
    }
    internal static (DateTime? Start, bool Closed) FitSiteOpening(SiteDefinition? site, DateOnly date, DateTime requested, int minutes)
    {
        if (site == null) return (requested, false);
        if (site.ClosedDates.Contains(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparer.Ordinal)) return (null, true);
        if (site.Openings.Count == 0) return (requested, false);
        foreach (var opening in site.Openings.Where(value => value.Day == date.DayOfWeek).OrderBy(value => value.From, StringComparer.Ordinal))
        {
            var from = date.ToDateTime(TimeOnly.ParseExact(opening.From, "HH:mm", CultureInfo.InvariantCulture)); var until = date.ToDateTime(TimeOnly.ParseExact(opening.Until, "HH:mm", CultureInfo.InvariantCulture));
            var candidate = requested > from ? requested : from; if (candidate.AddMinutes(minutes) <= until) return (candidate, false);
        }
        return (null, true);
    }
    public static string CoordinatesFingerprint(TourPoint from, TourPoint to) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { from.Latitude, from.Longitude, to.Latitude, to.Longitude }))));
    public static TourWeatherObservation? WeatherForStop(ScheduledTour tour, TourPlanningContext context, ScheduledTourStop stop)
    {
        var point = PointFor(context, stop.PlaceKey); if (!point.Latitude.HasValue) return null;
        var day = stop.Date.Length == 0 ? tour.Date : stop.Date; var fingerprint = CoordinatesFingerprint(point, point);
        return tour.Weather.FirstOrDefault(value => value.StopId == stop.Id && value.Day == day && value.CoordinateFingerprint == fingerprint);
    }
    internal static TourRoadLeg? FindRoad(ScheduledTour tour, string day, string technician, string fromId, string toId, TourPoint from, TourPoint to)
        => !from.Latitude.HasValue || !to.Latitude.HasValue ? null : tour.RoadLegs.FirstOrDefault(leg => leg.Day == day && leg.TechnicianKey == technician && leg.FromStop == fromId && leg.ToStop == toId && leg.CoordinateFingerprint == CoordinatesFingerprint(from, to));
    private static string GatherFields(TourPlanningContext context, IEnumerable<string> ids, IReadOnlyList<string> accepted, string reference)
    {
        var values = new List<string>(); if (reference.Length > 0) values.Add("Référentiel : " + reference); var idMap = IdMap(context);
        var columns = context.Source.Headers.Select((header, index) => (Header: header, Index: index, Key: string.Concat(CityText.Tokens(header)).ToLowerInvariant())).Where(item => accepted.Contains(item.Key, StringComparer.Ordinal))
            .GroupBy(item => item.Key).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        foreach (var id in ids)
            if (idMap.TryGetValue(id, out var rows) && rows.Length == 1)
                foreach (var column in columns)
                {
                    var row = context.Source.Rows[rows[0]]; var value = column.Index < row.Length ? row[column.Index].Trim() : "";
                    if (value.Length > 0) values.Add(id + " · " + column.Header + " : " + value);
                }
        var text = string.Join("\n", values.Distinct(StringComparer.Ordinal)); return text.Length > 1900 ? text[..1900] + "\n[Liste abrégée ; consulter les tickets]" : text;
    }
    public static TourScenarioComparison CompareFacts(ScheduledTour source, TourPlanningContext context)
    {
        var schedule = Calculate(source, context); var ids = IdMap(context); var dueUnknown = source.Tickets.Count(ticket => !ids.TryGetValue(ticket.TicketId, out var rows) || rows.Length != 1 || !context.Rows[rows[0]].Due.HasValue);
        return new(source.Name, source.Tickets.Count, source.Stops.Count, schedule.Technicians.Sum(item => item.InterventionMinutes), schedule.Technicians.Sum(item => item.TravelMinutes), schedule.Technicians.Sum(item => item.WaitMinutes),
            schedule.Visits.Count(visit => visit.Late), schedule.Visits.Count(visit => visit.SiteClosed), schedule.Visits.Count(visit => visit.DueExceeded), dueUnknown,
            schedule.Technicians.Count > 0 && schedule.Technicians.All(item => item.RoadKm.HasValue) ? schedule.Technicians.Sum(item => item.RoadKm!.Value) : null,
            schedule.Visits.Count(visit => visit.RoadKm.HasValue), "Échéances issues du relevé ; kilomètres routiers disponibles seulement si tous les segments ont été calculés pour ce parcours. Les paniers doivent être identiques pour comparer leur efficacité.");
    }
    public static TourAllocationResult DistributeAcrossDays(ScheduledTour source, TourPlanningContext context, IReadOnlyList<TourTechnicianAvailability> availability, CancellationToken ct = default)
    {
        source.Validate(); if (source.State == "done") throw new InvalidDataException("Revenez au brouillon pour répartir une tournée effectuée.");
        ValidateAvailability(availability); if (availability.Count == 0) throw new InvalidDataException("Déclarez au moins une disponibilité pour répartir les étapes.");
        var copy = PrepareEdit(source, "Répartition sur plusieurs journées ou techniciens"); foreach (var stop in copy.Stops) if (stop.TechnicianKey.Length == 0) stop.TechnicianKey = copy.TechnicianKey; copy.TechnicianKey = "";
        copy.Availability = availability.Select(item => item.Copy()).ToList();
        var allocated = new List<ScheduledTourStop>(); var issues = new List<TourAllocationIssue>(); var loads = new Dictionary<(string, string), int>();
        string K(string value) => string.Join(" ", CityText.Tokens(value));
        bool Skills(ScheduledTourStop stop, TourTechnicianAvailability item) => stop.RequiredSkills.All(skill => item.Skills.Any(known => K(known) == K(skill)));
        foreach (var stop in copy.Stops.Where(stop => stop.Locked))
        {
            var day = stop.Date.Length == 0 ? copy.Date : stop.Date; var constraint = availability.SingleOrDefault(item => item.Day == day && item.TechnicianKey == stop.TechnicianKey);
            if (constraint == null || constraint.Unavailable || !constraint.CapacityMinutes.HasValue || !Skills(stop, constraint)) throw new InvalidDataException("Une étape verrouillée ne respecte pas la disponibilité, la capacité déclarée ou les compétences. Vérifiez-la avant de répartir.");
            allocated.Add(stop.Copy());
        }
        bool Fits(ScheduledTour candidate, TourTechnicianAvailability item, out int minutes)
        {
            var schedule = Calculate(candidate, context, ct); var groups = schedule.Technicians.Where(group => group.Date == item.Day && group.Technician == TechnicianName(context, item.TechnicianKey)).ToArray();
            minutes = groups.Sum(group => group.InterventionMinutes + group.TravelMinutes + group.WaitMinutes);
            return minutes <= item.CapacityMinutes && !groups.Any(group => group.OutsideDay) && !schedule.Visits.Any(visit => visit.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == item.Day && visit.Technician == TechnicianName(context, item.TechnicianKey) && (visit.Late || visit.SiteClosed || visit.TechnicianUnavailable));
        }
        var initial = copy.Copy(); initial.Stops = allocated.Select(stop => stop.Copy()).ToList();
        foreach (var constraint in availability.Where(item => allocated.Any(stop => (stop.Date.Length == 0 ? copy.Date : stop.Date) == item.Day && stop.TechnicianKey == item.TechnicianKey)))
        {
            if (!Fits(initial, constraint, out var minutes)) throw new InvalidDataException("Les étapes verrouillées dépassent une capacité ou une plage d’ouverture."); loads[(constraint.TechnicianKey, constraint.Day)] = minutes;
        }
        var ids = IdMap(context);
        DateTime Due(ScheduledTourStop stop) => stop.TicketIds.Where(id => ids.TryGetValue(id, out var rows) && rows.Length == 1).Select(id => context.Rows[ids[id][0]].Due ?? DateTime.MaxValue).DefaultIfEmpty(DateTime.MaxValue).Min();
        foreach (var stop in copy.Stops.Where(stop => !stop.Locked).OrderBy(Due).ThenByDescending(stop => stop.DurationMinutes))
        {
            ct.ThrowIfCancellationRequested(); var city = context.Cities.GetValueOrDefault(stop.CityId);
            var candidates = availability.Where(item => !item.Unavailable && item.CapacityMinutes > 0 && Skills(stop, item))
                .OrderBy(item => item.Day, StringComparer.Ordinal).ThenBy(item => loads.GetValueOrDefault((item.TechnicianKey, item.Day)) / (double)item.CapacityMinutes!.Value)
                .ThenByDescending(item => city != null && item.Territories.Any(territory => new[] { city.Name, city.Sector, city.Department, city.Region }.Any(name => K(name) == K(territory))))
                .ThenBy(item => item.TechnicianKey, StringComparer.Ordinal); var assigned = false;
            foreach (var candidate in candidates)
            {
                if (loads.GetValueOrDefault((candidate.TechnicianKey, candidate.Day)) + stop.DurationMinutes + stop.TravelMinutes > candidate.CapacityMinutes) continue;
                var trial = copy.Copy(); var item = stop.Copy(); item.Date = candidate.Day; item.TechnicianKey = candidate.TechnicianKey;
                var proposed = allocated.Append(item).ToDictionary(value => value.Id, StringComparer.Ordinal);
                trial.Stops = copy.Stops.Where(value => proposed.ContainsKey(value.Id)).Select(value => proposed[value.Id].Copy()).ToList();
                if (!Fits(trial, candidate, out var minutes)) continue;
                stop.Date = item.Date; stop.TechnicianKey = item.TechnicianKey; allocated.Add(item); loads[(candidate.TechnicianKey, candidate.Day)] = minutes; assigned = true; break;
            }
            if (!assigned) { stop.Date = ""; stop.TechnicianKey = ""; issues.Add(new(stop.Id, PlaceName(context, stop), "Aucun créneau compatible avec les compétences, disponibilités, capacités déclarées et ouvertures ; étape laissée sans technicien.")); }
        }
        copy.Validate(); return new(copy, allocated.Count, issues);
    }
}
