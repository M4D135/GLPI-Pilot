using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private TourPlanningContext? _terrainTourContext;
    private CityEnrichmentResult? _terrainTourContextReport;
    private string _terrainTourContextSettings = "";
    private TourPlan? _terrainProjection;
    private long _terrainProjectionRevision = -1;
    private ScheduledTour? _terrainTourDraft;
    private int _terrainTourDraftVersion;
    private bool _terrainTourDirty;
    private string? _terrainTourBaseline;
    private readonly HashSet<int> _terrainBasket = new();
    private long _terrainBasketRevision = -1;

    private void RegisterTerrainTourOperations()
    {
        RegisterMapsOperations();
        TerrainRegister("terrain.tours.state", (a, ct) => TerrainTourState());
        TerrainRegister("terrain.tours.list", TerrainTourList);
        TerrainRegister("terrain.tours.projection", TerrainProjection);
        TerrainRegister("terrain.tours.projectionMove", TerrainProjectionMove);
        TerrainRegister("terrain.tours.projectionSave", TerrainProjectionSave);
        Register("terrain.tours.projectionExport", TerrainProjectionExportAsync);
        TerrainRegister("terrain.tours.basket", TerrainBasketPage);
        TerrainRegister("terrain.tours.select", TerrainBasketSelect);
        TerrainRegister("terrain.tours.create", TerrainCreateTour);
        TerrainRegister("terrain.tours.open", TerrainOpenTour);
        TerrainRegister("terrain.tours.edit", TerrainEditTour);
        TerrainRegister("terrain.tours.stops", TerrainStops);
        TerrainRegister("terrain.tours.stopEdit", TerrainEditStop);
        TerrainRegister("terrain.tours.move", (a, ct) => { var draft = TerrainCheckedTour(a); return TerrainSetTour(ScheduledTourService.Move(draft, Text(a, "stopId", 40), Number(a, "delta", 0, -1, 1))); });
        TerrainRegister("terrain.tours.proximity", (a, ct) => TerrainSetTour(ScheduledTourService.OrderByProximity(TerrainCheckedTour(a), TerrainContext(ct), ct)));
        TerrainRegister("terrain.tours.rebuild", (a, ct) => TerrainSetTour(ScheduledTourService.RebuildUnlocked(TerrainCheckedTour(a), TerrainContext(ct), Flag(a, "removeCompleted"), ct)));
        TerrainRegister("terrain.tours.calculate", TerrainCalculateTour);
        TerrainRegister("terrain.tours.reconciliation", TerrainTourReconciliation);
        TerrainRegister("terrain.tours.capacity", TerrainTourCapacity);
        TerrainRegister("terrain.tours.allocate", TerrainAllocateTour);
        TerrainRegister("terrain.tours.compare", TerrainCompareTours);
        TerrainRegister("terrain.tours.save", TerrainSaveTour);
        TerrainRegister("terrain.tours.duplicate", TerrainDuplicateTour);
        TerrainRegister("terrain.tours.delete", TerrainDeleteTour);
        TerrainRegister("terrain.tours.transition", TerrainTransitionTour);
        TerrainRegister("terrain.tours.day", TerrainToursDay);
        TerrainRegister("terrain.tours.point", (a, ct) => ScheduledTourService.PointFor(TerrainContext(ct), Text(a, "place", 50)));
        Register("terrain.tours.export", TerrainExportTourAsync);
        TerrainRegister("terrain.services.get", (a, ct) => TourServiceSettings.Load(DataPath("terrain-services.json")));
        TerrainRegister("terrain.services.save", (a, ct) => { var settings = TerrainRead<TourServiceSettings>(a, "settings", 5000); settings.Validate(); ct.ThrowIfCancellationRequested(); settings.Save(DataPath("terrain-services.json")); return settings; });
        Register("terrain.tours.route", (a, ct) => TerrainProviderAsync(a, true, ct));
        Register("terrain.tours.weather", (a, ct) => TerrainProviderAsync(a, false, ct));
    }
    private TourPlanningContext TerrainContext(CancellationToken ct)
    {
        var report = TerrainLocations(ct); var settings = EffectiveSettings; var fingerprint = TerrainDigest(new { settings.Cities, settings.Dashboard });
        if (_terrainTourContext is not null && ReferenceEquals(_terrainTourContextReport, report) && _terrainTourContextSettings == fingerprint) return _terrainTourContext;
        var context = TourPlanningService.Prepare(report, settings.Cities, settings.Dashboard, ct: ct);
        ct.ThrowIfCancellationRequested(); _terrainTourContext = context; _terrainTourContextReport = report; _terrainTourContextSettings = fingerprint; return context;
    }
    private void TerrainRefreshBasket()
    {
        if (_terrainBasketRevision == Revision) return;
        _terrainBasket.Clear(); _terrainBasketRevision = Revision;
    }
    private object TerrainTourState()
    {
        TerrainRefreshBasket();
        return new { revision = Revision, draftVersion = _terrainTourDraftVersion, dirty = _terrainTourDirty, selected = _terrainBasket.Count,
            draft = _terrainTourDraft is null ? null : TerrainTourHeader(_terrainTourDraft), loaded = Result is not null, historical = IsHistorical };
    }
    private static object TerrainTourHeader(ScheduledTour value) => new
    {
        value.Id, value.Name, value.Date, value.StartTime, value.EndTime, value.TimeZoneId, value.TechnicianKey, value.Start, value.End,
        value.ReturnMinutes, value.Notes, value.State, stateLabel = ScheduledTourService.StateLabel(value.State), tickets = value.Tickets.Count,
        stops = value.Stops.Count, value.UpdatedUtc, value.SourceExtractedAt, value.StateHistory,
        roadSegments = value.RoadLegs.Count, weatherObservations = value.Weather.Count
    };
    private ScheduledTour TerrainCheckedTour(JsonElement args)
    {
        CheckRevision(args); var draft = _terrainTourDraft ?? throw new InvalidOperationException("Créez une tournée depuis le panier ou ouvrez une tournée enregistrée.");
        if (Number(args, "draftVersion", -1, -1, int.MaxValue) != _terrainTourDraftVersion) throw new InvalidOperationException("La tournée a changé. Rechargez son planning avant de poursuivre.");
        return draft.Copy();
    }
    private object TerrainSetTour(ScheduledTour value, bool edit = true, bool dirty = true)
    {
        value.Validate(); if (edit) value = ScheduledTourService.PrepareEdit(value, "Modification du planning après validation");
        _terrainTourDraft = value.Copy(); _terrainTourDraftVersion++; _terrainTourDirty = dirty; return TerrainTourState();
    }
    private object TerrainTourList(JsonElement args, CancellationToken ct)
    {
        var (offset, limit) = TerrainPage(args); var term = Text(args, "search", 120); var state = Text(args, "state", 20); var date = Text(args, "date", 10);
        if (state.Length > 0 && state is not ("draft" or "ready" or "done")) throw new InvalidDataException("État de tournée inconnu.");
        if (date.Length > 0) _ = TerrainDate(date);
        var rows = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours
            .Where(value => (state.Length == 0 || value.State == state) && TerrainContains(value.Name, term)
                && (date.Length == 0 || value.Date == date || value.Stops.Any(stop => stop.Date == date)))
            .OrderByDescending(value => value.Date, StringComparer.Ordinal).ThenBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ct.ThrowIfCancellationRequested(); return new { total = rows.Length, offset, limit, items = rows.Skip(offset).Take(limit).Select(TerrainTourHeader).ToArray() };
    }

    private TourPlan TerrainBuildProjection(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var plan = TourPlanningService.Create(TerrainContext(ct), Text(args, "technician", 400), Flag(args, "allStatuses"), ct);
        var order = Text(args, "order", 20, "saved");
        plan = order switch
        {
            "saved" => TourPlanningService.ApplySavedOrder(plan, TourOrderPreferences.Load(DataPath("tour-order.json")).Get(TourOrderPreferences.ScopeKey(plan.TechnicianKey, plan.AllStatuses))),
            "priority" => TourPlanningService.Sort(plan, TourOrderKind.Priorities, ct),
            "due" => TourPlanningService.Sort(plan, TourOrderKind.DueDates, ct),
            "alphabetical" => plan,
            "proximity" => TourPlanningService.SortByProximity(plan, Text(args, "firstCity", 40), ct),
            "current" when _terrainProjectionRevision == Revision && _terrainProjection?.TechnicianKey == plan.TechnicianKey && _terrainProjection.AllStatuses == plan.AllStatuses => _terrainProjection,
            _ => throw new InvalidDataException("Ordre de villes inconnu ou devenu périmé.")
        };
        _terrainProjection = plan; _terrainProjectionRevision = Revision; return plan;
    }
    private object TerrainProjection(JsonElement args, CancellationToken ct)
    {
        var plan = TerrainBuildProjection(args, ct); return TerrainProjectionPage(plan, args);
    }
    private object TerrainProjectionPage(TourPlan plan, JsonElement args)
    {
        var (offset, limit) = TerrainPage(args);
        return new { revision = Revision, total = plan.Stops.Count, offset, limit, plan.ScopeLabel, plan.StatusLabel, plan.OrderLabel, plan.Available, plan.Message,
            tickets = plan.SelectedRows.Count, unlocated = plan.UnlocatedRows.Count, ambiguous = plan.AmbiguousRows.Count, unknownStatus = plan.UnknownStatusRows.Count,
            completedExcluded = plan.ExcludedCompletedCount, plan.SourceComplete, plan.Grain, plan.StraightLineDistanceKm, plan.CanUseProximity,
            items = plan.Stops.Skip(offset).Take(limit).Select((stop, index) => new { number = offset + index + 1, stop.CityId, stop.CityName, stop.PostalCode, stop.Count, stop.ActiveCount,
                stop.HighPriorityCount, stop.OverdueCount, stop.UnknownStatusCount, stop.PrioritySample, stop.DueSample, stop.FirstActiveDue, stop.Latitude, stop.Longitude }).ToArray() };
    }
    private TourPlan TerrainRequireProjection(JsonElement args)
    {
        CheckRevision(args); if (_terrainProjection is null || _terrainProjectionRevision != Revision) throw new InvalidOperationException("Calculez d’abord le regroupement des villes sur le relevé courant."); return _terrainProjection;
    }
    private object TerrainProjectionMove(JsonElement args, CancellationToken ct)
    {
        var plan = TourPlanningService.Move(TerrainRequireProjection(args), Text(args, "cityId", 40), Number(args, "delta", 0, -1, 1));
        ct.ThrowIfCancellationRequested(); _terrainProjection = plan; return TerrainProjectionPage(plan, args);
    }
    private object TerrainProjectionSave(JsonElement args, CancellationToken ct)
    {
        var plan = TerrainRequireProjection(args); var preferences = TourOrderPreferences.Load(DataPath("tour-order.json")).With(TourOrderPreferences.ScopeKey(plan.TechnicianKey, plan.AllStatuses), plan.Stops.Select(stop => stop.CityId));
        ct.ThrowIfCancellationRequested(); preferences.Save(DataPath("tour-order.json")); return new { saved = true, cities = plan.Stops.Count };
    }
    private async Task<object?> TerrainProjectionExportAsync(JsonElement args, CancellationToken ct)
    {
        var data = TourPlanningService.ToExport(TerrainRequireProjection(args), ct);
        var path = await Host.SaveFileAsync("Exporter le regroupement de tournée", "Classeur Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", "GLPI-Tournee-villes.xlsx"); if (path is null) return new { cancelled = true };
        ct.ThrowIfCancellationRequested(); var extension = Path.GetExtension(path).ToLowerInvariant(); if (extension is not (".csv" or ".xlsx")) throw new InvalidDataException("Choisissez une extension CSV ou XLSX.");
        var result = extension == ".csv" ? ExportService.WriteCsv(data, path) : ExportService.WriteXlsx(data, path); return new { path = result.Path, rows = result.Rows };
    }
    private object TerrainBasketPage(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); TerrainRefreshBasket(); var context = TerrainContext(ct);
        var plan = TourPlanningService.Create(context, Text(args, "technician", 400), Flag(args, "allStatuses"), ct);
        var city = Text(args, "cityId", 40); var search = Text(args, "search", 120).Trim(); var selectedOnly = Flag(args, "selectedOnly"); var (offset, limit) = TerrainPage(args);
        var rows = plan.SelectedRows.Where(index => (city.Length == 0 || context.Rows[index].CityId == city) && (!selectedOnly || _terrainBasket.Contains(index))
            && (TerrainContains(ScheduledTourService.Cell(context, index, "id"), search) || TerrainContains(ScheduledTourService.Cell(context, index, "title"), search))).ToArray();
        ct.ThrowIfCancellationRequested(); return new { revision = Revision, total = rows.Length, offset, limit, selected = _terrainBasket.Count, plan.Message,
            items = rows.Skip(offset).Take(limit).Select(index => new { row = index, id = TerrainShort(ScheduledTourService.Cell(context, index, "id"), 120), title = TerrainShort(ScheduledTourService.Cell(context, index, "title")),
                status = TerrainShort(ScheduledTourService.Cell(context, index, "status"), 120), priority = TerrainShort(ScheduledTourService.Cell(context, index, "priority"), 120), due = TerrainShort(ScheduledTourService.Cell(context, index, "due"), 120),
                city = context.Cities.GetValueOrDefault(context.Rows[index].CityId ?? "")?.Name ?? (context.Rows[index].CityState == CityMatchState.SiteOnly ? "Commune non renseignée" : "À localiser"), site = context.Sites.GetValueOrDefault(context.Rows[index].SiteId ?? "")?.Name ?? "", selected = _terrainBasket.Contains(index) }).ToArray() };
    }
    private object TerrainBasketSelect(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); TerrainRefreshBasket(); if (Flag(args, "clear")) { _terrainBasket.Clear(); return new { revision = Revision, selected = 0 }; }
        var rows = TerrainRead<List<int>>(args, "rows", 50_000); if (rows.Count > 2000) throw new InvalidDataException("Le panier accepte au maximum 2 000 tickets.");
        var context = TerrainContext(ct); if (rows.Any(row => row < 0 || row >= context.Rows.Length)) throw new InvalidDataException("Une ligne sélectionnée n’appartient pas à ce relevé.");
        var selected = Flag(args, "selected", true); var next = _terrainBasket.ToHashSet(); foreach (var row in rows) { if (selected) next.Add(row); else next.Remove(row); }
        if (next.Count > 2000) throw new InvalidDataException("Le panier accepte au maximum 2 000 tickets. Retirez des tickets avant d’en ajouter.");
        ct.ThrowIfCancellationRequested(); _terrainBasket.Clear(); _terrainBasket.UnionWith(next); return new { revision = Revision, selected = _terrainBasket.Count };
    }
    private void TerrainMaySwitch(JsonElement args)
    {
        if (_terrainTourDirty && !Flag(args, "discardDraft")) throw new InvalidOperationException("Enregistrez la tournée en cours ou confirmez l’abandon de son brouillon avant de changer de tournée.");
    }
    private object TerrainCreateTour(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); TerrainRefreshBasket(); TerrainMaySwitch(args);
        var tour = ScheduledTourService.Create(TerrainContext(ct), _terrainBasket.Order(), Text(args, "name", 120), TerrainDate(Text(args, "date", 10)), Text(args, "technician", 400), ct);
        _terrainTourBaseline = null; return TerrainSetTour(tour);
    }
    private object TerrainOpenTour(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); TerrainMaySwitch(args); var id = Text(args, "id", 40);
        var tour = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours.FirstOrDefault(value => value.Id == id) ?? throw new InvalidDataException("Cette tournée n’existe plus dans le registre local.");
        ct.ThrowIfCancellationRequested(); _terrainTourBaseline = TerrainDigest(tour); return TerrainSetTour(tour, false, false);
    }
    private object TerrainEditTour(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var values = args.GetProperty("fields"); if (values.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Champs de tournée invalides.");
        draft.Name = Text(values, "name", 120); draft.Date = Text(values, "date", 10); draft.StartTime = Text(values, "startTime", 5); draft.EndTime = Text(values, "endTime", 5);
        draft.TimeZoneId = Text(values, "timeZoneId", 150); draft.TechnicianKey = Text(values, "technicianKey", 400); draft.ReturnMinutes = Number(values, "returnMinutes", 0, 0, 1440); draft.Notes = Text(values, "notes", 4000);
        draft.Start = TerrainRead<TourPoint>(values, "start", 1000); draft.End = TerrainRead<TourPoint>(values, "end", 1000); draft.Validate(); ct.ThrowIfCancellationRequested(); return TerrainSetTour(draft);
    }
    private object TerrainStops(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var context = TerrainContext(ct); var (offset, limit) = TerrainPage(args); var schedule = TerrainCalculate(draft, context, ct);
        return new { revision = Revision, draftVersion = _terrainTourDraftVersion, total = draft.Stops.Count, offset, limit,
            items = draft.Stops.Skip(offset).Take(limit).Select((stop, index) => new { number = offset + index + 1, stop, place = ScheduledTourService.PlaceName(context, stop),
                technician = context.Technicians.FirstOrDefault(value => value.Key == (stop.TechnicianKey.Length == 0 ? draft.TechnicianKey : stop.TechnicianKey))?.Name ?? "À attribuer",
                visit = schedule.Visits.FirstOrDefault(visit => visit.Stop.Id == stop.Id) is { } visit ? TerrainVisit(visit) : null,
                weather = ScheduledTourService.WeatherForStop(draft, context, stop) }).ToArray() };
    }
    private object TerrainEditStop(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var id = Text(args, "stopId", 40); var stop = draft.Stops.FirstOrDefault(value => value.Id == id) ?? throw new InvalidDataException("Étape absente de cette tournée.");
        var values = args.GetProperty("fields"); if (values.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Champs d’étape invalides.");
        stop.Date = Text(values, "date", 10); stop.TechnicianKey = Text(values, "technicianKey", 400); stop.DurationMinutes = Number(values, "durationMinutes", 30, 1, 1440); stop.TravelMinutes = Number(values, "travelMinutes", 15, 0, 1440);
        stop.Earliest = Text(values, "earliest", 5); stop.Latest = Text(values, "latest", 5); stop.Locked = Flag(values, "locked"); stop.Notes = Text(values, "notes", 2000);
        stop.RequiredSkills = TerrainRead<List<string>>(values, "requiredSkills", 5000); stop.Contact = Text(values, "contact", 2000); stop.Equipment = Text(values, "equipment", 2000);
        draft.Validate(); ct.ThrowIfCancellationRequested(); return TerrainSetTour(draft);
    }
    private static object TerrainVisit(TourScheduledVisit visit) => new { visit.Number, stopId = visit.Stop.Id, visit.Place, visit.Address, visit.Technician, visit.Arrival, visit.Start, visit.Finish, visit.WaitMinutes, visit.Late,
        visit.StraightLineKm, visit.RoadKm, visit.TravelMinutesUsed, visit.SiteClosed, visit.TechnicianUnavailable, visit.DueExceeded, visit.Warning };
    private TourSchedule TerrainCalculate(ScheduledTour draft, TourPlanningContext context, CancellationToken ct)
    {
        TerrainRefreshAvailability(draft); return ScheduledTourService.Calculate(draft, context, ct);
    }
    private object TerrainCalculateTour(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var context = TerrainContext(ct); var schedule = TerrainCalculate(draft, context, ct); var (offset, limit) = TerrainPage(args);
        return new { revision = Revision, draftVersion = _terrainTourDraftVersion, total = schedule.Visits.Count, offset, limit,
            items = schedule.Visits.Skip(offset).Take(limit).Select(TerrainVisit).ToArray(), schedule.CanExportCalendar,
            warnings = schedule.Warnings.Take(100).ToArray(), warningCount = schedule.Warnings.Count,
            totals = new { interventions = schedule.Technicians.Sum(value => value.InterventionMinutes), travel = schedule.Technicians.Sum(value => value.TravelMinutes), waiting = schedule.Technicians.Sum(value => value.WaitMinutes), late = schedule.Visits.Count(value => value.Late), closed = schedule.Visits.Count(value => value.SiteClosed), unavailable = schedule.Visits.Count(value => value.TechnicianUnavailable) },
            technicians = schedule.Technicians.Take(448).ToArray(), availability = draft.Availability.Take(448).ToArray(), facts = ScheduledTourService.CompareFacts(draft, context) };
    }
    private object TerrainTourReconciliation(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var (offset, limit) = TerrainPage(args); var rows = ScheduledTourService.Reconcile(draft, TerrainContext(ct), ct);
        var changedOnly = Flag(args, "changedOnly"); var filtered = rows.Where(value => !changedOnly || value.State != "Inchangé").ToArray();
        return new { revision = Revision, draftVersion = _terrainTourDraftVersion, total = filtered.Length, offset, limit, items = filtered.Skip(offset).Take(limit).ToArray(),
            missing = rows.Count(value => !value.SourceIndex.HasValue), changed = rows.Count(value => value.Changed), completed = rows.Count(value => value.Completed) };
    }
    private static TourTechnicianAvailability TerrainDeclaredAvailability(TechnicianPreference preference, string key, DateTime day)
    {
        var exception = preference.CapacityDays.FirstOrDefault(value => value.Date.Date == day.Date);
        return new(key, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TechnicianPlanningService.CapacityForDate(preference, day), TechnicianPlanningService.IsUnavailable(preference, day),
            preference.Skills.ToArray(), preference.PrimaryTerritories.Concat(preference.BackupTerritories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            MorningCapacityMinutes = TechnicianPlanningService.CapacityForDate(preference, day, "morning"),
            AfternoonCapacityMinutes = TechnicianPlanningService.CapacityForDate(preference, day, "afternoon"),
            WorkWindows = preference.WorkPeriods.Count == 0 ? null : preference.WorkPeriods.Where(value => value.Day == day.DayOfWeek)
                .Select(value => new TourWorkWindow(value.StartMinute, value.EndMinute,
                    value.StartMinute < 720 && exception?.MorningMinutes.HasValue == true || value.EndMinute > 720 && exception?.AfternoonMinutes.HasValue == true ? null : value.CapacityMinutes)).ToArray()
        };
    }
    private void TerrainRefreshAvailability(ScheduledTour draft)
    {
        var preferences = TechnicianPreferences.Load(DataPath("technician-preferences.json"));
        draft.Availability = draft.Stops.Select(stop => (Key: stop.TechnicianKey.Length == 0 ? draft.TechnicianKey : stop.TechnicianKey, Day: stop.Date.Length == 0 ? draft.Date : stop.Date))
            .Where(value => value.Key.Length > 0).Distinct().Select(value => TerrainDeclaredAvailability(preferences.Get(value.Key), value.Key, TerrainDate(value.Day).ToDateTime(TimeOnly.MinValue))).ToList();
        ScheduledTourService.ValidateAvailability(draft.Availability);
    }
    private List<TourTechnicianAvailability> TerrainAvailability(JsonElement args, ScheduledTour draft, CancellationToken ct)
    {
        var keys = TerrainRead<List<string>>(args, "technicians", 16_000).Select(TechnicianDashboardService.IdentityKey).Distinct(StringComparer.Ordinal).ToArray();
        var days = Number(args, "days", 5, 1, 14); if (keys.Length is < 1 or > 32 || keys.Any(key => key.Length is < 1 or > 400)) throw new InvalidDataException("Choisissez entre 1 et 32 techniciens.");
        var preferences = TechnicianPreferences.Load(DataPath("technician-preferences.json")); var available = new List<TourTechnicianAvailability>(); var first = TerrainDate(draft.Date);
        var known = TerrainContext(ct).Technicians.Select(value => value.Key).Concat(preferences.Technicians.Keys).ToHashSet(StringComparer.Ordinal);
        if (keys.Any(key => !known.Contains(key))) throw new InvalidDataException("Un technicien sélectionné n’existe ni dans le relevé ni dans les préférences locales.");
        foreach (var key in keys) for (var offset = 0; offset < days; offset++) { ct.ThrowIfCancellationRequested(); available.Add(TerrainDeclaredAvailability(preferences.Get(key), key, first.AddDays(offset).ToDateTime(TimeOnly.MinValue))); }
        ScheduledTourService.ValidateAvailability(available); return available;
    }
    private object TerrainTourCapacity(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var available = TerrainAvailability(args, draft, ct); var (offset, limit) = TerrainPage(args);
        return new { revision = Revision, total = available.Count, offset, limit, unknown = available.Count(value => !value.CapacityMinutes.HasValue), items = available.Skip(offset).Take(limit).ToArray() };
    }
    private object TerrainAllocateTour(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var context = TerrainContext(ct); var availability = TerrainAvailability(args, draft, ct);
        var result = ScheduledTourService.DistributeAcrossDays(draft, context, availability, ct); ct.ThrowIfCancellationRequested();
        var state = TerrainSetTour(result.Tour); return new { state, result.AssignedStops, unassigned = result.Unassigned, unknownCapacity = availability.Count(value => !value.CapacityMinutes.HasValue),
            method = "Proposition heuristique : échéances, charge et territoires déclarés. Les absences, compétences, présences, budgets par demi-journée et étapes verrouillées restent des contraintes." };
    }
    private object TerrainCompareTours(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var id = Text(args, "otherId", 40); var other = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours.FirstOrDefault(value => value.Id == id) ?? throw new InvalidDataException("La tournée à comparer n’existe plus.");
        var context = TerrainContext(ct); TerrainRefreshAvailability(draft); TerrainRefreshAvailability(other);
        ct.ThrowIfCancellationRequested(); return new { current = ScheduledTourService.CompareFacts(draft, context), other = ScheduledTourService.CompareFacts(other, context),
            sameBasket = draft.SourceKey == other.SourceKey && draft.Tickets.Select(value => value.TicketId).ToHashSet(StringComparer.Ordinal).SetEquals(other.Tickets.Select(value => value.TicketId)) };
    }
    private void TerrainPersistTour(ScheduledTour draft, CancellationToken ct)
    {
        var path = DataPath("scheduled-tours.json"); var store = ScheduledTourStore.Load(path); var existing = store.Tours.FirstOrDefault(value => value.Id == draft.Id);
        if (existing is not null && (_terrainTourBaseline is null || TerrainDigest(existing) != _terrainTourBaseline) || existing is null && _terrainTourBaseline is not null)
            throw new InvalidOperationException("La tournée enregistrée a changé depuis son ouverture. Rechargez-la pour préserver les modifications existantes.");
        var previousAvailability = TerrainDigest(draft.Availability); TerrainRefreshAvailability(draft);
        if (previousAvailability != TerrainDigest(draft.Availability) && draft.State != "draft") draft = ScheduledTourService.PrepareEdit(draft, "Les disponibilités déclarées ont changé depuis la validation");
        var next = store.With(draft); ct.ThrowIfCancellationRequested(); next.Save(path);
        _terrainTourDraft = next.Tours.Single(value => value.Id == draft.Id).Copy(); _terrainTourBaseline = TerrainDigest(_terrainTourDraft); _terrainTourDirty = false; _terrainTourDraftVersion++;
        Publish("tours.changed", new { id = draft.Id, state = draft.State });
    }
    private object TerrainSaveTour(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); TerrainPersistTour(draft, ct); Log("TOURNÉE · planning local enregistré."); return TerrainTourState();
    }
    private object TerrainDuplicateTour(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var duplicate = ScheduledTourService.Duplicate(draft, Text(args, "name", 120)); ct.ThrowIfCancellationRequested(); _terrainTourBaseline = null; return TerrainSetTour(duplicate);
    }
    private object TerrainDeleteTour(JsonElement args, CancellationToken ct)
    {
        if (!Flag(args, "confirmed")) throw new InvalidDataException("Confirmez la suppression de cette tournée locale.");
        var draft = TerrainCheckedTour(args); var store = ScheduledTourStore.Load(DataPath("scheduled-tours.json")); var existing = store.Tours.FirstOrDefault(value => value.Id == draft.Id);
        if (existing is null || _terrainTourBaseline is null || TerrainDigest(existing) != _terrainTourBaseline) throw new InvalidOperationException("Rechargez la tournée enregistrée avant de la supprimer.");
        ct.ThrowIfCancellationRequested(); store.Without(draft.Id).Save(DataPath("scheduled-tours.json")); _terrainTourDraft = null; _terrainTourBaseline = null; _terrainTourDirty = false; _terrainTourDraftVersion++;
        Publish("tours.changed", new { id = draft.Id, deleted = true }); return TerrainTourState();
    }
    private object TerrainTransitionTour(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); TerrainRefreshAvailability(draft);
        var updated = ScheduledTourService.Transition(draft, TerrainContext(ct), Text(args, "state", 20), Text(args, "reason", 1000));
        TerrainPersistTour(updated, ct); return TerrainTourState();
    }
    private object TerrainToursDay(JsonElement args, CancellationToken ct)
    {
        var date = TerrainDate(Text(args, "date", 10)); var key = Text(args, "technician", 400); var rows = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).ForDate(date, key); var (offset, limit) = TerrainPage(args);
        ct.ThrowIfCancellationRequested(); return new { total = rows.Count, offset, limit, items = rows.Skip(offset).Take(limit).ToArray() };
    }
    private async Task<object?> TerrainExportTourAsync(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); var context = TerrainContext(ct); var schedule = TerrainCalculate(draft, context, ct); var format = Text(args, "format", 10);
        if (format is not ("ics" or "html")) throw new InvalidDataException("Format d’export attendu : ICS ou HTML.");
        var content = format == "ics" ? ScheduledTourService.ToCalendar(schedule) : ScheduledTourService.ToBriefingHtml(schedule, context);
        var path = await Host.SaveFileAsync(format == "ics" ? "Exporter les rendez-vous" : "Exporter le briefing imprimable", format == "ics" ? "Calendrier ICS (*.ics)|*.ics" : "Briefing HTML (*.html)|*.html", "GLPI-Tournee-" + draft.Date + "." + format);
        if (path is null) return new { cancelled = true }; ct.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct); return new { path, visits = schedule.Visits.Count, format };
    }
    private async Task<object?> TerrainProviderAsync(JsonElement args, bool routing, CancellationToken ct)
    {
        var draft = TerrainCheckedTour(args); if (!Flag(args, "consent")) throw new InvalidOperationException("Autorisez explicitement l’envoi des coordonnées et des dates au fournisseur choisi pour cette demande.");
        var settings = TerrainRead<TourServiceSettings>(args, "settings", 5000); settings.Validate(); var endpoint = TourExternalServices.Endpoint(routing ? settings.RoutingEndpoint : settings.WeatherEndpoint);
        if (Text(args, "consentEndpoint", 1000) != endpoint.AbsoluteUri) throw new InvalidOperationException("L’adresse a changé depuis votre autorisation. Confirmez l’adresse affichée avant de relancer la demande.");
        var context = TerrainContext(ct); using var service = new TourExternalServices();
        Publish("terrain.progress", new { message = routing ? "Calcul des segments routiers…" : "Lecture des prévisions quotidiennes…" });
        var result = routing ? await service.RouteAsync(draft, context, settings, true, ct) : await service.WeatherAsync(draft, context, settings, true, ct);
        ct.ThrowIfCancellationRequested(); var state = TerrainSetTour(result, edit: routing); return new { state, segments = result.RoadLegs.Count, forecasts = result.Weather.Count, provider = endpoint.GetLeftPart(UriPartial.Path) };
    }
}
