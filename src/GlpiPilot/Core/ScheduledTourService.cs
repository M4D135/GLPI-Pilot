using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed class TourPoint
{
    public string Label { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public TourPoint Copy() => new() { Label = Label, Latitude = Latitude, Longitude = Longitude };
    internal bool Valid => CityText.ValidText(Label, true) && CityDefinition.ValidCoordinates(Latitude, Longitude);
}
public sealed class ScheduledTourTicket
{
    public string TicketId { get; set; } = "";
    public string PlaceKey { get; set; } = "";
    public int BaselineStatus { get; set; }
    public string BaselineFingerprint { get; set; } = "";
    public ScheduledTourTicket Copy() => new() { TicketId = TicketId, PlaceKey = PlaceKey, BaselineStatus = BaselineStatus, BaselineFingerprint = BaselineFingerprint };
}
public sealed class ScheduledTourStop
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CityId { get; set; } = "";
    public string SiteId { get; set; } = "";
    public List<string> TicketIds { get; set; } = new();
    public string TechnicianKey { get; set; } = "";
    public int DurationMinutes { get; set; } = 30;
    public int TravelMinutes { get; set; } = 15;
    public string Earliest { get; set; } = "";
    public string Latest { get; set; } = "";
    public bool Locked { get; set; }
    public string Notes { get; set; } = "";
    public string Date { get; set; } = "";
    public List<string> RequiredSkills { get; set; } = new();
    public string Contact { get; set; } = "";
    public string Equipment { get; set; } = "";
    [JsonIgnore] public string SkillsText { get => string.Join(" ; ", RequiredSkills); set => RequiredSkills = (value ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(); }
    [JsonIgnore] public string PlaceKey => SiteId.Length > 0 ? "site:" + SiteId : "city:" + CityId;
    public ScheduledTourStop Copy() => new() { Id = Id, CityId = CityId, SiteId = SiteId, TicketIds = TicketIds.ToList(), TechnicianKey = TechnicianKey, DurationMinutes = DurationMinutes,
        TravelMinutes = TravelMinutes, Earliest = Earliest, Latest = Latest, Locked = Locked, Notes = Notes, Date = Date, RequiredSkills = RequiredSkills.ToList(), Contact = Contact, Equipment = Equipment };
}
public sealed partial class ScheduledTour
{
    public const int MaximumTickets = 2000, MaximumStops = 500;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nouvelle tournée";
    public string SourceKey { get; set; } = "";
    public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string StartTime { get; set; } = "08:30";
    public string EndTime { get; set; } = "17:30";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public string TechnicianKey { get; set; } = "";
    public TourPoint Start { get; set; } = new();
    public TourPoint End { get; set; } = new();
    public int ReturnMinutes { get; set; }
    public List<ScheduledTourTicket> Tickets { get; set; } = new();
    public List<ScheduledTourStop> Stops { get; set; } = new();
    public DateTimeOffset SourceExtractedAt { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Notes { get; set; } = "";
    public ScheduledTour Copy() => new()
    {
        Id = Id, Name = Name, SourceKey = SourceKey, Date = Date, StartTime = StartTime, EndTime = EndTime, TimeZoneId = TimeZoneId, TechnicianKey = TechnicianKey,
        Start = Start.Copy(), End = End.Copy(), ReturnMinutes = ReturnMinutes, Tickets = Tickets.Select(ticket => ticket.Copy()).ToList(), Stops = Stops.Select(stop => stop.Copy()).ToList(),
        SourceExtractedAt = SourceExtractedAt, UpdatedUtc = UpdatedUtc, Notes = Notes, State = State, StateHistory = StateHistory.Select(value => value with { }).ToList(), RoadLegs = RoadLegs.Select(value => value with { }).ToList(), Weather = Weather.Select(value => value with { }).ToList(), Availability = Availability.Select(value => value.Copy()).ToList()
    };
    public void Validate()
    {
        ValidateExtended();
        if (!Guid.TryParse(Id, out var id) || id == Guid.Empty || !CityText.ValidText(Name, false) || SourceKey is null || SourceKey.Length is 0 or > 1000 || CityOverrideStore.SourceKey(SourceKey) != SourceKey
            || !DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 2000 or > 2100
            || !Time(StartTime, false, out var start) || !Time(EndTime, false, out var end) || end <= start
            || !Text(TimeZoneId, 150) || !Text(TechnicianKey, 400) || Start == null || End == null || !Start.Valid || !End.Valid || ReturnMinutes is < 0 or > 1440
            || !Text(Notes, 4000, true) || Tickets == null || Tickets.Count > MaximumTickets || Stops == null || Stops.Count > MaximumStops)
            throw new InvalidDataException("Tournée invalide : nom, source, date (2000–2100), horaires de journée et points de départ/retour sont à vérifier.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); } catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new InvalidDataException("Le fuseau horaire de cette tournée n’est pas disponible sur ce poste.", error); }
        var ids = new HashSet<string>(StringComparer.Ordinal); var stopIds = new HashSet<Guid>(); var assigned = new HashSet<string>(StringComparer.Ordinal); var places = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ticket in Tickets)
            if (ticket == null || !CityText.ValidText(ticket.TicketId, false) || !ids.Add(ticket.TicketId) || ticket.BaselineStatus is < 0 or > 6
                || !ValidPlace(ticket.PlaceKey) || ticket.BaselineFingerprint is null || ticket.BaselineFingerprint.Length != 64 || ticket.BaselineFingerprint.Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException("La sélection de tickets contient un identifiant dupliqué ou une référence invalide.");
        var ticketPlaces = Tickets.ToDictionary(ticket => ticket.TicketId, ticket => ticket.PlaceKey, StringComparer.Ordinal);
        foreach (var stop in Stops)
        {
            if (stop == null || !Guid.TryParse(stop.Id, out var stopId) || stopId == Guid.Empty || !stopIds.Add(stopId)
                || !Guid.TryParse(stop.CityId, out var city) || city == Guid.Empty || stop.SiteId is null || stop.SiteId.Length > 0 && (!Guid.TryParse(stop.SiteId, out var site) || site == Guid.Empty)
                || stop.TicketIds == null || stop.TicketIds.Count == 0 || stop.TicketIds.Count > MaximumTickets || stop.TicketIds.Any(ticket => !ids.Contains(ticket) || !assigned.Add(ticket))
                || !Text(stop.TechnicianKey, 400) || stop.DurationMinutes is < 1 or > 1440 || stop.TravelMinutes is < 0 or > 1440
                || !Time(stop.Earliest, true, out var earliest) || !Time(stop.Latest, true, out var latest) || stop.Earliest.Length > 0 && stop.Latest.Length > 0 && earliest > latest
                || !Text(stop.Notes, 2000, true) || !Text(stop.Contact, 2000, true) || !Text(stop.Equipment, 2000, true)
                || stop.RequiredSkills == null || stop.RequiredSkills.Count > 30 || stop.RequiredSkills.Any(value => !CityText.ValidText(value, false))
                || stop.Date == null || stop.Date.Length > 0 && (!DateOnly.TryParseExact(stop.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stopDate) || stopDate.Year is < 2000 or > 2100)
                || !places.Add(stop.PlaceKey)) throw new InvalidDataException("Une étape contient un lieu ou des tickets dupliqués, des durées ou une fenêtre horaire invalides.");
            if (stop.TicketIds.Any(ticket => ticketPlaces[ticket] != stop.PlaceKey))
                throw new InvalidDataException("Le lieu d’une étape ne correspond pas à la référence de ses tickets. Actualisez les étapes depuis le relevé.");
        }
    }
    internal static bool ValidPlace(string? value) => value != null && (value.Length == 0 || (value.StartsWith("site:", StringComparison.Ordinal) || value.StartsWith("city:", StringComparison.Ordinal)) && Guid.TryParse(value[5..], out var id) && id != Guid.Empty);
    internal static bool Time(string? value, bool empty, out TimeOnly time)
    { time = default; return empty && value == "" || TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time); }
    internal static bool Text(string? value, int limit, bool multiline = false) => value != null && value.Length <= limit && !value.Any(ch => char.IsControl(ch) && !(multiline && ch is '\r' or '\n' or '\t'));
}
public sealed record TourReconciliationRow(string TicketId, int? SourceIndex, string State, string Detail, string CurrentPlaceKey, bool Completed, bool Changed);
public sealed record TourScheduledVisit(int Number, ScheduledTourStop Stop, string Place, string Address, string Technician, DateTime Arrival,
    DateTime Start, DateTime Finish, int WaitMinutes, bool Late, double? StraightLineKm, string Warning)
{ public int TravelMinutesUsed { get; init; } public double? RoadKm { get; init; } public bool SiteClosed { get; init; } public bool TechnicianUnavailable { get; init; } public bool DueExceeded { get; init; } }
public sealed record TourTechnicianSchedule(string Technician, int Stops, int Tickets, int InterventionMinutes, int TravelMinutes, int WaitMinutes, DateTime Finish, bool OutsideDay, double? StraightLineKm = null, string Date = "", double? RoadKm = null);
public sealed class TourSchedule
{
    public required ScheduledTour Tour { get; init; }
    public required IReadOnlyList<TourScheduledVisit> Visits { get; init; }
    public required IReadOnlyList<TourTechnicianSchedule> Technicians { get; init; }
    public required IReadOnlyList<TourReconciliationRow> Reconciliation { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public bool CanExportCalendar => Visits.Count > 0 && Reconciliation.All(row => row.SourceIndex.HasValue && row.CurrentPlaceKey.Length > 0 && row.State is not ("Source différente" or "Lieu modifié"))
        && Visits.All(visit => !visit.TechnicianUnavailable) && Warnings.All(warning => !warning.StartsWith("Référence absente", StringComparison.Ordinal));
}

/// <summary>Explicit local appointments. Travel minutes are user estimates, never a claimed road route.</summary>
public static partial class ScheduledTourService
{
    public static ScheduledTour Create(TourPlanningContext context, IEnumerable<int> selectedRows, string name, DateOnly date, string? technicianKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(selectedRows); ct.ThrowIfCancellationRequested();
        var rows = selectedRows.Distinct().ToArray(); if (rows.Length == 0 || rows.Length > ScheduledTour.MaximumTickets || rows.Any(row => row < 0 || row >= context.Rows.Length))
            throw new InvalidDataException("Sélectionnez entre 1 et 2 000 lignes du relevé courant.");
        var idMap = IdMap(context); var tour = new ScheduledTour { Name = name, Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), SourceKey = CityOverrideStore.SourceKey(context.Source.SourceUrl),
            SourceExtractedAt = context.Source.ExtractedAt, TechnicianKey = TechnicianDashboardService.IdentityKey(technicianKey) };
        var groups = new Dictionary<string, ScheduledTourStop>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested(); var id = Cell(context, row, "id").Trim();
            if (id.Length == 0 || !idMap.TryGetValue(id, out var indices) || indices.Length != 1)
                throw new InvalidDataException("Une tournée mémorisée nécessite un ID unique pour chaque ticket sélectionné. Corrigez les doublons et identifiants absents.");
            var source = context.Rows[row]; var place = PlaceKey(source); tour.Tickets.Add(new() { TicketId = id, PlaceKey = place, BaselineStatus = source.Status, BaselineFingerprint = Fingerprint(context, row) });
            if (place.Length == 0) continue;
            if (!groups.TryGetValue(place, out var stop))
            { stop = new() { CityId = source.CityId!, SiteId = source.SiteId ?? "", TechnicianKey = tour.TechnicianKey }; groups.Add(place, stop); tour.Stops.Add(stop); }
            stop.TicketIds.Add(id);
        }
        foreach (var stop in tour.Stops)
        {
            var site = context.Sites.GetValueOrDefault(stop.SiteId);
            stop.Contact = GatherFields(context, stop.TicketIds, new[] { "contact", "telephone", "telephonedemandeur", "contactsurplace" }, site?.Contact ?? "");
            stop.Equipment = GatherFields(context, stop.TicketIds, new[] { "materiel", "equipement", "ordinateur", "elementassocie", "elementsassocies" }, site?.Equipment ?? "");
        }
        tour.Validate(); return tour;
    }
    public static IReadOnlyList<TourReconciliationRow> Reconcile(ScheduledTour tour, TourPlanningContext context, CancellationToken ct = default)
    {
        tour.Validate(); ct.ThrowIfCancellationRequested(); var sameSource = tour.SourceKey == CityOverrideStore.SourceKey(context.Source.SourceUrl); var ids = IdMap(context);
        var results = new List<TourReconciliationRow>();
        foreach (var ticket in tour.Tickets)
        {
            ct.ThrowIfCancellationRequested();
            if (!sameSource) { results.Add(new(ticket.TicketId, null, "Source différente", "Le relevé appartient à une autre installation GLPI.", "", false, false)); continue; }
            if (!ids.TryGetValue(ticket.TicketId, out var rows))
            { results.Add(new(ticket.TicketId, null, "Absent du relevé", context.Source.Complete ? "Absent du périmètre exporté actuel ; cela ne prouve pas sa suppression dans GLPI." : "Source partielle : présence inconnue.", "", false, false)); continue; }
            if (rows.Length != 1) { results.Add(new(ticket.TicketId, null, "ID ambigu", "Plusieurs lignes ont cet identifiant ; aucun rapprochement arbitraire.", "", false, false)); continue; }
            var row = rows[0]; var current = context.Rows[row]; var place = PlaceKey(current); var completed = current.Status is 5 or 6;
            var changed = Fingerprint(context, row) != ticket.BaselineFingerprint;
            var state = place != ticket.PlaceKey ? "Lieu modifié" : completed && ticket.BaselineStatus is not (5 or 6) ? "Terminé depuis" : changed ? "Ticket modifié" : "Inchangé";
            results.Add(new(ticket.TicketId, row, state, place != ticket.PlaceKey ? "La localisation actuelle diffère de l’étape mémorisée. Actualisez les étapes après vérification."
                : completed ? "Statut résolu ou clos dans le relevé courant." : changed ? "Le titre, statut, priorité, échéance, affectation ou lieu a changé depuis la préparation." : "Référence identique au relevé de préparation.", place, completed, changed));
        }
        return results;
    }
    public static TourSchedule Calculate(ScheduledTour tour, TourPlanningContext context, CancellationToken ct = default)
    {
        tour.Validate(); ct.ThrowIfCancellationRequested(); var copy = tour.Copy(); var changes = Reconcile(copy, context, ct);
        var visits = new List<TourScheduledVisit>(); var technicians = new List<TourTechnicianSchedule>(); var warnings = new List<string>();
        if (!context.Source.Complete) warnings.Add("Le relevé actuel est partiel ou son périmètre complet n’est pas confirmé.");
        var unplaced = copy.Tickets.Count - copy.Stops.Sum(stop => stop.TicketIds.Count); if (unplaced > 0) warnings.Add($"{unplaced} ticket(s) du panier n’ont pas d’étape localisée.");
        if (changes.Any(change => change.State != "Inchangé")) warnings.Add("Le rapprochement avec le relevé courant comporte des changements ou absences ; consultez-le avant le départ.");
        var idMap = IdMap(context); var usedRoads = 0; var staleRoads = false;
        foreach (var group in copy.Stops.Select((stop, index) => (Stop: stop, Index: index)).GroupBy(item => (Technician: item.Stop.TechnicianKey.Length == 0 ? copy.TechnicianKey : item.Stop.TechnicianKey, Day: item.Stop.Date.Length == 0 ? copy.Date : item.Stop.Date)))
        {
            ct.ThrowIfCancellationRequested(); var date = DateOnly.ParseExact(group.Key.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var cursor = date.ToDateTime(TimeOnly.ParseExact(copy.StartTime, "HH:mm", CultureInfo.InvariantCulture)); var dayEnd = date.ToDateTime(TimeOnly.ParseExact(copy.EndTime, "HH:mm", CultureInfo.InvariantCulture));
            var availability = copy.Availability.SingleOrDefault(item => item.TechnicianKey == group.Key.Technician && item.Day == group.Key.Day);
            var planner = availability == null ? null : new TourAvailabilityPlanner(availability, date, TimeOnly.FromDateTime(cursor), TimeOnly.FromDateTime(dayEnd)); var incompatible = false;
            var point = copy.Start; var previous = "start"; var intervention = 0; var travel = 0; var waiting = 0; double distanceTotal = 0, roadTotal = 0; var distanceKnown = true; var roadsKnown = true;
            foreach (var (stop, index) in group)
            {
                ct.ThrowIfCancellationRequested(); var (place, address, nextPoint, missing) = Location(context, stop); if (missing) warnings.Add("Référence absente : " + place);
                var road = FindRoad(copy, group.Key.Day, group.Key.Technician, previous, stop.Id, point, nextPoint);
                var minutes = road == null ? stop.TravelMinutes : (int)Math.Ceiling(road.DurationMinutes);
                if (road != null) { roadTotal += road.DistanceKm; usedRoads++; } else { roadsKnown = false; staleRoads |= copy.RoadLegs.Count > 0; }
                var arrival = cursor.AddMinutes(minutes); var actual = arrival;
                if (stop.Earliest.Length > 0) actual = actual > date.ToDateTime(TimeOnly.ParseExact(stop.Earliest, "HH:mm", CultureInfo.InvariantCulture)) ? actual : date.ToDateTime(TimeOnly.ParseExact(stop.Earliest, "HH:mm", CultureInfo.InvariantCulture));
                var opening = FitSiteOpening(context.Sites.GetValueOrDefault(stop.SiteId), date, actual, stop.DurationMinutes);
                if (opening.Start.HasValue) actual = opening.Start.Value;
                var finish = actual.AddMinutes(stop.DurationMinutes); var technicianUnavailable = false; var wait = (int)Math.Round((actual - arrival).TotalMinutes);
                if (planner != null)
                {
                    if (planner.TryFit(cursor, minutes, stop.DurationMinutes, stop.Earliest, context.Sites.GetValueOrDefault(stop.SiteId), stop.RequiredSkills, out var fitArrival, out var fitStart, out var fitFinish, out var fitWaiting))
                    { arrival = fitArrival; actual = fitStart; finish = fitFinish; wait = fitWaiting; opening = (actual, false); }
                    else { technicianUnavailable = true; incompatible = true; }
                }
                var late = stop.Latest.Length > 0 && finish > date.ToDateTime(TimeOnly.ParseExact(stop.Latest, "HH:mm", CultureInfo.InvariantCulture));
                var due = stop.TicketIds.Where(id => idMap.TryGetValue(id, out var positions) && positions.Length == 1).Select(id => context.Rows[idMap[id][0]].Due).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
                var overdue = due.Length > 0 && finish > due.Min(); var distance = Distance(point, nextPoint);
                if (distance.HasValue) distanceTotal += distance.Value; else distanceKnown = false;
                var signal = missing ? "Lieu retiré du référentiel" : opening.Closed ? "Aucune plage d’ouverture ne permet cette visite" : technicianUnavailable ? "Présence, capacité ou compétence déclarée incompatible" : late ? "Fin après la fenêtre prévue" : overdue ? "Intervention après une échéance de ticket" : road != null ? "Trajet routier calculé · " + road.Provider : "Trajet saisi · distance à vol d’oiseau";
                visits.Add(new(index + 1, stop.Copy(), place, address, TechnicianName(context, group.Key.Technician), arrival, actual, finish, wait, late, distance, signal) { TravelMinutesUsed = minutes, RoadKm = road?.DistanceKm, SiteClosed = opening.Closed, TechnicianUnavailable = technicianUnavailable, DueExceeded = overdue });
                cursor = finish; point = nextPoint; previous = stop.Id; intervention += stop.DurationMinutes; travel += minutes; waiting += wait;
            }
            var returnRoad = FindRoad(copy, group.Key.Day, group.Key.Technician, previous, "end", point, copy.End); var returnMinutes = returnRoad == null ? copy.ReturnMinutes : (int)Math.Ceiling(returnRoad.DurationMinutes);
            if (planner != null && returnMinutes > 0)
            {
                if (planner.TryFit(cursor, returnMinutes, 0, "", null, null, out _, out _, out var returnFinish, out _)) cursor = returnFinish;
                else { cursor = cursor.AddMinutes(returnMinutes); incompatible = true; warnings.Add("Retour incompatible avec la présence ou la capacité déclarée de " + TechnicianName(context, group.Key.Technician) + " le " + group.Key.Day + "."); }
            }
            else cursor = cursor.AddMinutes(returnMinutes);
            travel += returnMinutes;
            if (copy.End.Latitude.HasValue)
            {
                var back = Distance(point, copy.End); if (back.HasValue) distanceTotal += back.Value; else distanceKnown = false;
                if (returnRoad != null) { roadTotal += returnRoad.DistanceKm; usedRoads++; } else { roadsKnown = false; staleRoads |= copy.RoadLegs.Count > 0; }
            }
            else if (copy.End.Label.Length > 0 || copy.ReturnMinutes > 0) { distanceKnown = false; roadsKnown = false; }
            technicians.Add(new(TechnicianName(context, group.Key.Technician), group.Count(), group.Sum(item => item.Stop.TicketIds.Count), intervention, travel, waiting, cursor, cursor > dayEnd || incompatible, distanceKnown ? distanceTotal : null, group.Key.Day, roadsKnown ? roadTotal : null));
        }
        if (visits.Any(visit => visit.Late)) warnings.Add("Une ou plusieurs fenêtres horaires sont dépassées.");
        if (visits.Any(visit => visit.SiteClosed)) warnings.Add("Des visites ne respectent pas les ouvertures ou fermetures déclarées des sites.");
        if (visits.Any(visit => visit.TechnicianUnavailable)) warnings.Add("Des visites ne respectent pas la présence, la capacité ou les compétences déclarées du technicien. Recalculez la répartition ou adaptez explicitement les contraintes.");
        if (visits.Any(visit => visit.DueExceeded)) warnings.Add("Des interventions finissent après une échéance de ticket connue ; arbitrez le scénario avant de le valider.");
        if (technicians.Any(technician => technician.OutsideDay)) warnings.Add("Une ou plusieurs tournées dépassent la fin de journée configurée.");
        if (staleRoads) warnings.Add("Des trajets routiers ne correspondent plus au parcours courant : durées saisies utilisées, recalcul routier nécessaire.");
        warnings.Add(usedRoads == 0 ? "Trajets : durées saisies par l’utilisateur ; kilomètres indicatifs à vol d’oiseau. Aucun trafic temps réel." : $"{usedRoads} trajet(s) routiers issus du fournisseur choisi ; les autres durées restent saisies. Aucun trafic temps réel garanti.");
        return new() { Tour = copy, Visits = visits.OrderBy(visit => visit.Start).ThenBy(visit => visit.Number).ToArray(), Technicians = technicians, Reconciliation = changes, Warnings = warnings };
    }
    public static ScheduledTour Duplicate(ScheduledTour source, string name)
    {
        source.Validate(); var copy = source.Copy(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = name; copy.State = "draft"; copy.StateHistory.Clear(); copy.RoadLegs.Clear(); copy.Weather.Clear(); copy.UpdatedUtc = DateTimeOffset.UtcNow;
        foreach (var stop in copy.Stops) stop.Id = Guid.NewGuid().ToString("N"); copy.Validate(); return copy;
    }
    public static ScheduledTour Move(ScheduledTour source, string stopId, int delta)
    {
        source.Validate(); if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta)); var copy = source.Copy(); var index = copy.Stops.FindIndex(stop => stop.Id == stopId);
        if (index < 0) throw new InvalidDataException("L’étape n’appartient pas à cette tournée."); var next = index + delta;
        if (next < 0 || next >= copy.Stops.Count) return copy;
        if (copy.Stops[index].Locked || copy.Stops[next].Locked) throw new InvalidDataException("Déverrouillez les étapes concernées pour modifier leur position.");
        (copy.Stops[index], copy.Stops[next]) = (copy.Stops[next], copy.Stops[index]); return PrepareEdit(copy, "Ordre des étapes modifié");
    }
    public static ScheduledTour Distribute(ScheduledTour source, IEnumerable<string> technicianKeys)
    {
        source.Validate(); var keys = technicianKeys.Select(TechnicianDashboardService.IdentityKey).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length is < 1 or > 32) throw new InvalidDataException("Choisissez entre 1 et 32 techniciens.");
        var copy = source.Copy(); var load = keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        foreach (var stop in copy.Stops.Where(stop => stop.Locked))
        { var key = stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : copy.TechnicianKey; if (!load.ContainsKey(key)) throw new InvalidDataException("Un technicien d’étape verrouillée ne fait pas partie de la sélection."); load[key] += stop.DurationMinutes + stop.TravelMinutes; }
        foreach (var stop in copy.Stops.Where(stop => !stop.Locked).OrderByDescending(stop => stop.DurationMinutes + stop.TravelMinutes))
        { var key = keys.OrderBy(key => load[key]).ThenBy(key => key, StringComparer.Ordinal).First(); stop.TechnicianKey = key; load[key] += stop.DurationMinutes + stop.TravelMinutes; }
        return PrepareEdit(copy, "Répartition entre techniciens modifiée");
    }
    public static ScheduledTour RebuildUnlocked(ScheduledTour source, TourPlanningContext context, bool removeCompleted, CancellationToken ct = default)
    {
        source.Validate(); var changes = Reconcile(source, context, ct);
        if (changes.Any(change => !change.SourceIndex.HasValue)) throw new InvalidDataException("Des identifiants sont absents, ambigus ou issus d’une autre source. Rechargez le bon relevé avant de reconstruire les étapes.");
        var kept = changes.Where(change => !removeCompleted || !change.Completed).ToArray(); if (kept.Length == 0) throw new InvalidDataException("Aucun ticket ne resterait dans cette tournée.");
        var changedPlaces = kept.Where(change => change.State == "Lieu modifié").Select(change => change.TicketId).ToHashSet(StringComparer.Ordinal);
        if (source.Stops.Any(stop => stop.Locked && stop.TicketIds.Any(id => changedPlaces.Contains(id) || kept.All(change => change.TicketId != id))))
            throw new InvalidDataException("Une étape verrouillée serait déplacée ou vidée. Déverrouillez-la après vérification.");
        var generated = Create(context, kept.Select(change => change.SourceIndex!.Value), source.Name, DateOnly.ParseExact(source.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), source.TechnicianKey, ct);
        var copy = source.Copy(); copy.Tickets = generated.Tickets; copy.SourceExtractedAt = context.Source.ExtractedAt;
        var remaining = generated.Stops.ToDictionary(stop => stop.PlaceKey, StringComparer.Ordinal); var replacement = new List<ScheduledTourStop>();
        foreach (var locked in source.Stops.Where(stop => stop.Locked))
            if (!remaining.TryGetValue(locked.PlaceKey, out var updated) || !locked.TicketIds.ToHashSet(StringComparer.Ordinal).SetEquals(updated.TicketIds))
                throw new InvalidDataException("Le panier d’une étape verrouillée changerait. Déverrouillez cette étape après vérification.");
        foreach (var old in copy.Stops)
        {
            if (!remaining.Remove(old.PlaceKey, out var updated)) continue;
            var stop = old.Copy(); stop.TicketIds = updated.TicketIds; replacement.Add(stop);
        }
        replacement.AddRange(remaining.Values);
        for (var index = 0; index < source.Stops.Count; index++)
            if (source.Stops[index].Locked && (index >= replacement.Count || replacement[index].Id != source.Stops[index].Id))
                throw new InvalidDataException("L’actualisation déplacerait une étape verrouillée. Déverrouillez-la après vérification.");
        copy.Stops = replacement; copy.Validate(); return PrepareEdit(copy, "Panier actualisé depuis le relevé");
    }
    public static ScheduledTour OrderByProximity(ScheduledTour source, TourPlanningContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); source.Validate(); var copy = source.Copy();
        if (copy.Stops.Any(stop => !Location(context, stop).Point.Latitude.HasValue)) throw new InvalidDataException("Chaque étape doit avoir ses coordonnées pour proposer un ordre indicatif.");
        foreach (var group in copy.Stops.Select((stop, index) => (Stop: stop, Index: index)).GroupBy(item => (item.Stop.TechnicianKey.Length == 0 ? copy.TechnicianKey : item.Stop.TechnicianKey, item.Stop.Date.Length == 0 ? copy.Date : item.Stop.Date)))
        {
            var positions = group.Select(item => item.Index).ToArray(); var point = copy.Start; var cursor = 0;
            while (cursor < positions.Length)
            {
                ct.ThrowIfCancellationRequested(); if (copy.Stops[positions[cursor]].Locked) { point = Location(context, copy.Stops[positions[cursor]]).Point; cursor++; continue; }
                var end = cursor; while (end < positions.Length && !copy.Stops[positions[end]].Locked) end++;
                var remaining = positions[cursor..end].Select(position => copy.Stops[position]).ToList();
                for (var i = cursor; i < end; i++)
                {
                    ct.ThrowIfCancellationRequested(); var nearest = point.Latitude.HasValue ? remaining.OrderBy(stop => Distance(point, Location(context, stop).Point)).ThenBy(stop => stop.Id, StringComparer.Ordinal).First() : remaining[0];
                    copy.Stops[positions[i]] = nearest; remaining.Remove(nearest); point = Location(context, nearest).Point;
                }
                cursor = end;
            }
        }
        return PrepareEdit(copy, "Ordre indicatif recalculé");
    }
    public static string ToCalendar(TourSchedule schedule, DateTimeOffset? createdAt = null)
    {
        // RFC 5545 §§3.1, 3.3.11, 3.8.2: CRLF, 75-octet UTF-8 folds, escaped TEXT, UTC DTSTART/DTEND.
        // https://www.rfc-editor.org/rfc/rfc5545.html
        if (!schedule.CanExportCalendar) throw new InvalidDataException("Le calendrier nécessite des tickets uniques et présents, des lieux à jour et au moins une étape. Consultez le rapprochement et actualisez les étapes.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Tour.TimeZoneId); var stamp = (createdAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//GLPI Pilot//Tournees locales//FR", "CALSCALE:GREGORIAN", "METHOD:PUBLISH" };
        foreach (var visit in schedule.Visits)
        {
            string Utc(DateTime date)
            {
                date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(date) || zone.IsAmbiguousTime(date)) throw new InvalidDataException("Un horaire tombe dans un changement d’heure ambigu ou inexistant. Ajustez-le avant l’export calendrier.");
                return TimeZoneInfo.ConvertTimeToUtc(date, zone).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            }
            lines.AddRange(new[] { "BEGIN:VEVENT", "UID:" + schedule.Tour.Id + "-" + visit.Stop.Id + "@glpi-pilot.local", "DTSTAMP:" + stamp,
                "DTSTART:" + Utc(visit.Start), "DTEND:" + Utc(visit.Finish), "SUMMARY:" + Ics(schedule.Tour.Name + " · " + visit.Place),
                "LOCATION:" + Ics((visit.Place + " · " + visit.Address).Trim(' ', '·')),
                "DESCRIPTION:" + Ics("Technicien : " + visit.Technician + "\nTickets : " + string.Join(", ", visit.Stop.TicketIds) + "\n" + visit.Stop.Notes + "\n" + string.Join("\n", schedule.Warnings)), "STATUS:TENTATIVE", "END:VEVENT" });
        }
        lines.Add("END:VCALENDAR"); return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }
    public static string ToBriefingHtml(TourSchedule schedule, TourPlanningContext context)
    {
        string H(string? text) => WebUtility.HtmlEncode(text ?? ""); var html = new StringBuilder("<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><title>Feuille de tournée</title><style>body{font:15px system-ui,sans-serif;color:#172338;max-width:1100px;margin:36px auto;padding:0 20px}h1{font-size:28px}h2{margin-top:30px}p{line-height:1.5}table{border-collapse:collapse;width:100%;margin:12px 0}td,th{padding:10px;text-align:left;border-bottom:1px solid #ccd3df}small{color:#536175}.notice{background:#f4f6fa;border-radius:12px;padding:14px}article{break-inside:avoid}button{padding:10px 16px}@media print{button{display:none}body{margin:0;font-size:11px}}</style><body>");
        html.Append("<button onclick=\"window.print()\">Imprimer / PDF</button><h1>").Append(H(schedule.Tour.Name)).Append("</h1><p>").Append(H(schedule.Tour.Date)).Append(" · ").Append(H(schedule.Tour.StartTime)).Append("–").Append(H(schedule.Tour.EndTime)).Append(" · ").Append(H(schedule.Tour.TimeZoneId)).Append("</p>");
        html.Append("<p>Départ : ").Append(H(schedule.Tour.Start.Label)).Append(" · Retour : ").Append(H(schedule.Tour.End.Label)).Append("</p><div class=\"notice\">").Append(string.Join("<br>", schedule.Warnings.Select(H))).Append("</div>");
        var currentRows = schedule.Reconciliation.ToDictionary(item => item.TicketId, item => item.SourceIndex, StringComparer.Ordinal);
        foreach (var visit in schedule.Visits)
        {
            html.Append("<article><h2>").Append(visit.Number).Append(" · ").Append(H(visit.Place)).Append("</h2><p>").Append(H(visit.Technician)).Append(" · ").Append(visit.Start.ToString("dd/MM HH:mm")).Append("–").Append(visit.Finish.ToString("HH:mm"))
                .Append(" · intervention ").Append(visit.Stop.DurationMinutes).Append(" min · trajet prévu ").Append(visit.TravelMinutesUsed).Append(" min</p><p>").Append(H(visit.Address)).Append("</p><table><tr><th>Ticket</th><th>Titre actuel</th><th>Statut</th><th>Échéance</th></tr>");
            foreach (var id in visit.Stop.TicketIds)
            {
                var row = currentRows.GetValueOrDefault(id);
                html.Append("<tr><td>").Append(H(id)).Append("</td><td>").Append(H(row is int r ? Cell(context, r, "title") : "Indisponible dans le relevé actuel")).Append("</td><td>").Append(H(row is int s ? Cell(context, s, "status") : "—")).Append("</td><td>").Append(H(row is int d ? Cell(context, d, "due") : "—")).Append("</td></tr>");
            }
            html.Append("</table><p><strong>Contact :</strong> ").Append(H(visit.Stop.Contact.Length == 0 ? "Non renseigné" : visit.Stop.Contact)).Append("</p><p><strong>Matériel / accès :</strong> ").Append(H(visit.Stop.Equipment.Length == 0 ? "Non renseigné" : visit.Stop.Equipment)).Append("</p>");
            var weather = WeatherForStop(schedule.Tour, context, visit.Stop);
            if (weather != null)
                html.Append("<p><strong>Prévision météo du ").Append(H(weather.Day)).Append(" :</strong> code WMO ").Append(weather.WeatherCode?.ToString(CultureInfo.InvariantCulture) ?? "non disponible")
                    .Append(" · minimum ").Append(weather.MinimumCelsius?.ToString("N1", CultureInfo.CurrentCulture) ?? "—").Append(" °C · maximum ").Append(weather.MaximumCelsius?.ToString("N1", CultureInfo.CurrentCulture) ?? "—")
                    .Append(" °C · risque de précipitation ").Append(weather.RainProbabilityPercent?.ToString("N0", CultureInfo.CurrentCulture) ?? "—").Append(" %. Source : ").Append(H(weather.Provider)).Append(" ; reçu le ").Append(H(weather.RetrievedAt.ToString("g"))).Append(".</p>");
            html.Append("<p>").Append(H(visit.Stop.Notes).Replace("\n", "<br>")).Append("</p><p>Intervention réalisée : ☐ &nbsp; Observations : __________________________________________</p></article>");
        }
        html.Append("<h2>Rapprochement du relevé</h2><table><tr><th>Ticket</th><th>État</th><th>Détail</th></tr>");
        foreach (var row in schedule.Reconciliation.Where(row => row.State != "Inchangé")) html.Append("<tr><td>").Append(H(row.TicketId)).Append("</td><td>").Append(H(row.State)).Append("</td><td>").Append(H(row.Detail)).Append("</td></tr>");
        html.Append("</table><small>Source de préparation : ").Append(H(schedule.Tour.SourceExtractedAt.ToString("O"))).Append(". Relevé actuel : ").Append(H(context.Source.ExtractedAt.ToString("O"))).Append(". Plan local ; aucune affectation ni mise à jour dans GLPI.</small></body></html>"); return html.ToString();
    }
    public static string Cell(TourPlanningContext context, int row, string role) => context.Columns.TryGetValue(role, out var position) && position is int column && column < context.Source.Rows[row].Length ? context.Source.Rows[row][column] ?? "" : "";
    public static string PlaceName(TourPlanningContext context, ScheduledTourStop stop) => Location(context, stop).Place;
    public static TourPoint PointFor(TourPlanningContext context, string placeKey)
    {
        if (placeKey.StartsWith("site:", StringComparison.Ordinal) && context.Sites.TryGetValue(placeKey[5..], out var site)) return new() { Label = site.Name, Latitude = site.Latitude, Longitude = site.Longitude };
        if (placeKey.StartsWith("city:", StringComparison.Ordinal) && context.Cities.TryGetValue(placeKey[5..], out var city)) return new() { Label = city.Name, Latitude = city.Latitude, Longitude = city.Longitude };
        return new();
    }
    public static IReadOnlyList<(string Key, string Label)> PlaceChoices(TourPlanningContext context) => context.Cities.Values.Select(city => ("city:" + city.Id, city.Name + " · ville"))
        .Concat(context.Sites.Values.Select(site => ("site:" + site.Id, site.Name + " · " + context.Cities[site.CityId].Name))).OrderBy(item => item.Item2, StringComparer.CurrentCultureIgnoreCase).ToArray();
    private static Dictionary<string, int[]> IdMap(TourPlanningContext context) => Enumerable.Range(0, context.Rows.Length).GroupBy(row => Cell(context, row, "id").Trim(), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    private static string PlaceKey(TourRow row) => row.CityState == CityMatchState.Matched && row.CityId != null ? row.SiteId != null ? "site:" + row.SiteId : "city:" + row.CityId : "";
    private static string Fingerprint(TourPlanningContext context, int row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Cell(context, row, "title"), Cell(context, row, "status"), Cell(context, row, "priority"), Cell(context, row, "due"), Cell(context, row, "assignee"), PlaceKey(context.Rows[row]) }))));
    private static string TechnicianName(TourPlanningContext context, string key) => key.Length == 0 ? "À attribuer" : context.Technicians.FirstOrDefault(technician => technician.Key == key)?.Name ?? key;
    private static (string Place, string Address, TourPoint Point, bool Missing) Location(TourPlanningContext context, ScheduledTourStop stop)
    {
        if (!context.Cities.TryGetValue(stop.CityId, out var city)) return ("Ville retirée", "", new(), true);
        if (stop.SiteId.Length == 0) return (city.Name, city.PostalCode, new() { Label = city.Name, Latitude = city.Latitude, Longitude = city.Longitude }, false);
        if (!context.Sites.TryGetValue(stop.SiteId, out var site) || site.CityId != city.Id) return ("Site retiré · " + city.Name, "", new(), true);
        return (site.Name + " · " + city.Name, site.Address, new() { Label = site.Name, Latitude = site.Latitude, Longitude = site.Longitude }, false);
    }
    private static double? Distance(TourPoint first, TourPoint second)
    {
        if (!first.Latitude.HasValue || !second.Latitude.HasValue || !first.Longitude.HasValue || !second.Longitude.HasValue) return null;
        const double r = Math.PI / 180; var a = Math.Pow(Math.Sin((second.Latitude.Value - first.Latitude.Value) * r / 2), 2) + Math.Cos(first.Latitude.Value * r) * Math.Cos(second.Latitude.Value * r) * Math.Pow(Math.Sin((second.Longitude.Value - first.Longitude.Value) * r / 2), 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(Math.Clamp(a, 0, 1)), Math.Sqrt(Math.Clamp(1 - a, 0, 1)));
    }
    private static string Ics(string text) => text.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string Fold(string text)
    {
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength; if (bytes + size > 75) { result.Append("\r\n "); bytes = 1; } result.Append(rune.ToString()); bytes += size;
        }
        return result.ToString();
    }
}

/// <summary>Local saved tours contain explicit ticket IDs and snapshots of their references; never portable team settings.</summary>
public sealed partial class ScheduledTourStore
{
    public const int MaximumTours = 100;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public int Version { get; set; } = 1;
    public List<ScheduledTour> Tours { get; set; } = new();
    public void Validate()
    {
        if (Version != 1 || Tours == null || Tours.Count > MaximumTours || Tours.Any(tour => tour == null)) throw new InvalidDataException("Le registre des tournées est invalide.");
        var ids = new HashSet<Guid>(); foreach (var tour in Tours) { tour.Validate(); if (!ids.Add(Guid.Parse(tour.Id))) throw new InvalidDataException("Deux tournées portent le même identifiant."); }
    }
    public ScheduledTourStore With(ScheduledTour tour)
    { Validate(); tour.Validate(); var copy = new ScheduledTourStore { Tours = Tours.Where(item => item.Id != tour.Id).Select(item => item.Copy()).ToList() }; var saved = tour.Copy(); saved.UpdatedUtc = DateTimeOffset.UtcNow; copy.Tours.Add(saved); copy.Validate(); return copy; }
    public ScheduledTourStore Without(string id) { Validate(); return new() { Tours = Tours.Where(tour => tour.Id != id).Select(tour => tour.Copy()).ToList() }; }
    public static ScheduledTourStore Load(string path)
    {
        if (!File.Exists(path)) return new(); using var stream = File.OpenRead(path); if (stream.Length > MaximumBytes) throw new InvalidDataException("Le registre des tournées dépasse 16 Mio.");
        var store = JsonSerializer.Deserialize<ScheduledTourStore>(stream, Options) ?? throw new InvalidDataException("Registre des tournées vide."); store.Validate(); return store;
    }
    public void Save(string path)
    {
        Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options); if (bytes.Length > MaximumBytes) throw new InvalidDataException("Les tournées dépassent la limite de 16 Mio.");
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, full, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
