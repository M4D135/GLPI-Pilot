using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public enum TourOrderKind { Alphabetical, Priorities, DueDates, Manual, Saved, Proximity }

public sealed record TourTechnicianChoice(string Key, string Name, int RowCount);
public sealed record TourTicket(int SourceIndex, string Id, string Title, string Status, string Priority, string Due);

public sealed class TourStop
{
    internal TourStop(CityDefinition city, IEnumerable<int> rows, int? active, int? high, int? overdue,
        int unknownStatus, int prioritySample, int dueSample, int? maximumPriority, DateTime? firstDue)
    {
        CityId = city.Id; CityName = city.Name; PostalCode = city.PostalCode;
        Latitude = city.Latitude; Longitude = city.Longitude; Rows = Array.AsReadOnly(rows.ToArray());
        ActiveCount = active; HighPriorityCount = high; OverdueCount = overdue;
        UnknownStatusCount = unknownStatus; PrioritySample = prioritySample; DueSample = dueSample;
        MaximumActivePriority = maximumPriority; FirstActiveDue = firstDue;
    }
    public string CityId { get; }
    public string CityName { get; }
    public string PostalCode { get; }
    public double? Latitude { get; }
    public double? Longitude { get; }
    public IReadOnlyList<int> Rows { get; }
    public int Count => Rows.Count;
    public int? ActiveCount { get; }
    public int? HighPriorityCount { get; }
    public int? OverdueCount { get; }
    public int UnknownStatusCount { get; }
    public int PrioritySample { get; }
    public int DueSample { get; }
    public int? MaximumActivePriority { get; }
    public DateTime? FirstActiveDue { get; }
    public bool HasCoordinates => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180
        && double.IsFinite(Latitude.Value) && double.IsFinite(Longitude.Value);
}

/// <summary>An immutable, independent plan. Row numbers always refer to its source extraction.</summary>
public sealed class TourPlan
{
    internal TourPlan(TourPlanningContext context, string technicianKey, string scopeLabel, bool allStatuses,
        IEnumerable<TourStop> stops, IEnumerable<int> selected, IEnumerable<int> unlocated, IEnumerable<int> ambiguous,
        IEnumerable<int> missingTitle, IEnumerable<int> unknownStatus, int excludedCompleted, bool available,
        string message, TourOrderKind orderKind)
    {
        Context = context; TechnicianKey = technicianKey; ScopeLabel = scopeLabel; AllStatuses = allStatuses;
        Stops = Array.AsReadOnly(stops.ToArray()); SelectedRows = Array.AsReadOnly(selected.ToArray());
        UnlocatedRows = Array.AsReadOnly(unlocated.ToArray()); AmbiguousRows = Array.AsReadOnly(ambiguous.ToArray());
        TitleUnavailableRows = Array.AsReadOnly(missingTitle.ToArray()); UnknownStatusRows = Array.AsReadOnly(unknownStatus.ToArray());
        ExcludedCompletedCount = excludedCompleted; Available = available; Message = message; OrderKind = orderKind;
    }
    internal TourPlanningContext Context { get; }
    public string TechnicianKey { get; }
    public string ScopeLabel { get; }
    public bool AllStatuses { get; }
    public string StatusLabel => AllStatuses ? "Tous les statuts" : "Actifs reconnus uniquement";
    public IReadOnlyList<TourStop> Stops { get; }
    public IReadOnlyList<int> SelectedRows { get; }
    public IReadOnlyList<int> UnlocatedRows { get; }
    public IReadOnlyList<int> AmbiguousRows { get; }
    public IReadOnlyList<int> TitleUnavailableRows { get; }
    public IReadOnlyList<int> UnknownStatusRows { get; }
    public int ExcludedCompletedCount { get; }
    public bool Available { get; }
    public string Message { get; }
    public TourOrderKind OrderKind { get; }
    public string OrderLabel => OrderKind switch
    {
        TourOrderKind.Priorities => "Priorités actives décroissantes", TourOrderKind.DueDates => "Échéances actives les plus proches",
        TourOrderKind.Manual => "Ordre manuel", TourOrderKind.Saved => "Ordre mémorisé sur ce poste",
        TourOrderKind.Proximity => "Proximité indicative à vol d’oiseau", _ => "Ordre alphabétique"
    };
    public bool SourceComplete => Context.Source.Complete;
    public int SourceRowCount => Context.Source.Rows.Length;
    public string Grain => Context.Grain;
    public DateTimeOffset CalculatedAt => Context.CalculatedAt;
    public bool CanUseProximity => Stops.Count > 1 && Stops.All(stop => stop.HasCoordinates);
    public double? StraightLineDistanceKm
    {
        get
        {
            if (!CanUseProximity) return null;
            double distance = 0;
            for (var i = 1; i < Stops.Count; i++) distance += TourPlanningService.DistanceKm(Stops[i - 1], Stops[i]);
            return distance;
        }
    }
}

public sealed class TourPlanningContext
{
    internal TourPlanningContext(TourSource source, TourRow[] rows, Dictionary<string, CityDefinition> cities, Dictionary<string, SiteDefinition> sites,
        TechnicianRoster roster, DashboardSnapshot dashboard, bool applied, string message)
    {
        Source = source; Rows = rows; Cities = cities; Sites = sites; Roster = roster; Grain = dashboard.Grain;
        CalculatedAt = dashboard.CalculatedAt; HasStatus = dashboard.Column("status")!.Index.HasValue;
        HasPriority = dashboard.Column("priority")!.Index.HasValue; HasDue = dashboard.Column("due")!.Index.HasValue;
        Applied = applied; Message = message;
        Columns = dashboard.Columns.ToDictionary(column => column.Role, column => column.Index, StringComparer.Ordinal);
        Technicians = Array.AsReadOnly(roster.Technicians.Select(entry => new TourTechnicianChoice(entry.Key, entry.Name, entry.Rows.Length)).ToArray());
    }
    internal TourSource Source { get; }
    internal TourRow[] Rows { get; }
    internal Dictionary<string, CityDefinition> Cities { get; }
    internal Dictionary<string, SiteDefinition> Sites { get; }
    internal TechnicianRoster Roster { get; }
    internal Dictionary<string, int?> Columns { get; }
    internal bool HasStatus { get; }
    internal bool HasPriority { get; }
    internal bool HasDue { get; }
    public IReadOnlyList<TourTechnicianChoice> Technicians { get; }
    public bool TechnicianColumnAvailable => Roster.Available;
    public bool Applied { get; }
    public string Message { get; }
    public string Grain { get; }
    public DateTimeOffset CalculatedAt { get; }
}

internal sealed record TourRow(int Index, int Status, int? Priority, DateTime? Due, bool Active, bool High, bool Overdue,
    string? CityId, CityMatchState CityState, string? SiteId = null);

internal sealed record TourSource(string[] Headers, string[][] Rows, string SourceUrl, string Title,
    DateTimeOffset ExtractedAt, int Pages, int? ExpectedTotal, bool Complete, string Scope, string[] Warnings);

/// <summary>Local planning over a frozen extraction. No geocoding, routing service or GLPI write.</summary>
public static class TourPlanningService
{
    public static TourPlanningContext Prepare(CityEnrichmentResult report, CitySettings citySettings,
        DashboardSettings dashboardSettings, DateTimeOffset? referenceTime = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(citySettings);
        ArgumentNullException.ThrowIfNull(dashboardSettings);
        ct.ThrowIfCancellationRequested();
        citySettings.Validate(); var cities = citySettings.Copy();
        var settings = dashboardSettings.Copy(); DashboardService.ValidateSettings(settings);
        var data = report.Data;
        if (data == null || report.Source == null || data.Rows.Count != report.Source.Rows.Count)
            throw new InvalidDataException("Le relevé enrichi ne conserve pas les lignes source attendues.");
        var cityById = cities.Cities.ToDictionary(city => city.Id, city => city.Copy(), StringComparer.Ordinal);
        var matches = new Dictionary<int, CityMatch>();
        if (report.Applied)
        {
            foreach (var match in report.Matches)
            {
                if ((matches.Count & 255) == 0) ct.ThrowIfCancellationRequested();
                if (match.RowIndex < 0 || match.RowIndex >= data.Rows.Count || !matches.TryAdd(match.RowIndex, match))
                    throw new InvalidDataException("Les indices de localisation ne correspondent pas au relevé.");
                if (match.State == CityMatchState.Matched && (match.CityId == null || !cityById.ContainsKey(match.CityId)))
                    throw new InvalidDataException("Une ville détectée n’appartient pas au référentiel associé à ce relevé.");
                if (match.SiteId != null && (match.State != CityMatchState.Matched || !cities.Sites.Any(site => site.Id == match.SiteId && site.CityId == match.CityId)))
                    throw new InvalidDataException("Le site détecté n’appartient pas à la ville du référentiel associé au relevé.");
            }
            if (matches.Count != data.Rows.Count)
                throw new InvalidDataException("La localisation ne couvre pas chaque ligne du relevé.");
        }
        var dashboard = DashboardService.Calculate(data, settings, referenceTime, ct);
        var roster = TechnicianDashboardService.Detect(data, settings, ct);
        var active = dashboard.Metric("active")!.Rows.ToHashSet();
        var high = dashboard.Metric("high")!.Rows.ToHashSet();
        var overdue = dashboard.Metric("overdue")!.Rows.ToHashSet();
        var columns = dashboard.Columns.ToDictionary(column => column.Role, column => column.Index, StringComparer.Ordinal);
        var sourceRows = new string[data.Rows.Count][];
        var rows = new TourRow[data.Rows.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            sourceRows[i] = data.Rows[i].ToArray();
            string Cell(string role) => columns[role] is int column && column < sourceRows[i].Length ? sourceRows[i][column] ?? "" : "";
            var due = DashboardService.TryDate(Cell("due"), settings.DateCulture, dashboard.CalculatedAt.Offset, out var date) ? (DateTime?)date : null;
            matches.TryGetValue(i, out var match);
            rows[i] = new(i, DashboardService.Status(Cell("status")), DashboardService.Priority(Cell("priority")), due,
                active.Contains(i), high.Contains(i), overdue.Contains(i), match?.State == CityMatchState.Matched ? match.CityId : null,
                match?.State ?? CityMatchState.Unmatched, match?.SiteId);
        }
        var source = new TourSource(data.Headers.ToArray(), sourceRows, data.SourceUrl, data.Title, data.ExtractedAt, data.Pages,
            data.ExpectedTotal, dashboard.Complete, data.Scope, dashboard.Warnings.ToArray());
        ct.ThrowIfCancellationRequested();
        return new(source, rows, cityById, cities.Sites.ToDictionary(site => site.Id, site => site.Copy(), StringComparer.Ordinal), roster, dashboard, report.Applied, report.Message);
    }

    public static TourPlan Create(TourPlanningContext context, string? technicianKey = null, bool allStatuses = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context); ct.ThrowIfCancellationRequested();
        var key = TechnicianDashboardService.IdentityKey(technicianKey);
        var technician = context.Roster.Technicians.FirstOrDefault(entry => entry.Key == key);
        var scope = key.Length == 0 ? "Toute l’équipe" : "Technicien · " + (technician?.Name ?? key);
        var available = key.Length == 0 || context.Roster.Available;
        IEnumerable<int> scopeRows = key.Length == 0 ? Enumerable.Range(0, context.Rows.Length) : technician?.Rows ?? Array.Empty<int>();
        var selected = new List<int>(); var unlocated = new List<int>(); var ambiguous = new List<int>();
        var titleUnavailable = new List<int>(); var unknownStatus = new List<int>(); var excludedCompleted = 0;
        var groups = new Dictionary<string, List<TourRow>>(StringComparer.Ordinal);
        var iteration = 0;
        foreach (var index in scopeRows)
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = context.Rows[index];
            if (row.Status == 0) unknownStatus.Add(index);
            if (!allStatuses && !row.Active)
            { if (row.Status is 5 or 6) excludedCompleted++; continue; }
            selected.Add(index);
            if (row.CityState == CityMatchState.Ambiguous) { ambiguous.Add(index); continue; }
            if (row.CityState != CityMatchState.Matched || row.CityId == null)
            {
                unlocated.Add(index);
                if (row.CityState == CityMatchState.TitleUnavailable) titleUnavailable.Add(index);
                continue;
            }
            if (!groups.TryGetValue(row.CityId, out var group)) groups[row.CityId] = group = new();
            group.Add(row);
        }
        var stops = new List<TourStop>(groups.Count);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var rows = group.Value; var active = rows.Where(row => row.Active).ToArray();
            var knownStatus = rows.Count(row => row.Status != 0);
            var statusAvailable = context.HasStatus && knownStatus > 0;
            var priorities = active.Where(row => row.Priority.HasValue).ToArray();
            var due = active.Where(row => row.Due.HasValue).ToArray();
            stops.Add(new(context.Cities[group.Key], rows.Select(row => row.Index), statusAvailable ? active.Length : null,
                statusAvailable && context.HasPriority && (active.Length == 0 || priorities.Length > 0) ? active.Count(row => row.High) : null,
                statusAvailable && context.HasDue && (active.Length == 0 || due.Length > 0) ? active.Count(row => row.Overdue) : null,
                rows.Count - knownStatus, priorities.Length, due.Length,
                priorities.Length == 0 ? null : priorities.Max(row => row.Priority), due.Length == 0 ? null : due.Min(row => row.Due)));
        }
        var message = !available ? context.Roster.Message
            : key.Length > 0 && technician == null ? "Ce technicien n’apparaît pas dans les affectations du relevé. Aucun ticket ne lui est attribué dans ce périmètre."
            : !context.Applied ? context.Message
            : !allStatuses && !context.HasStatus ? "La colonne Statut n’est pas reconnue. Choisissez « Tous les statuts » pour préparer un plan sans filtrage d’état."
            : !allStatuses && selected.Count == 0 && unknownStatus.Count > 0 ? "Aucun actif reconnu dans ce périmètre. Des statuts non classés restent accessibles séparément."
            : "Les étapes regroupent les villes confirmées du relevé. Les affectations multiples restent visibles dans chaque tournée concernée ; les totaux par technicien ne s’additionnent pas.";
        return new(context, key, scope, allStatuses,
            stops.OrderBy(stop => stop.CityName, StringComparer.CurrentCultureIgnoreCase).ThenBy(stop => stop.CityId, StringComparer.Ordinal),
            selected, unlocated, ambiguous, titleUnavailable, unknownStatus, excludedCompleted, available, message, TourOrderKind.Alphabetical);
    }

    public static TourPlan Sort(TourPlan plan, TourOrderKind order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ct.ThrowIfCancellationRequested();
        IEnumerable<TourStop> stops = order switch
        {
            TourOrderKind.Alphabetical => plan.Stops.OrderBy(stop => stop.CityName, StringComparer.CurrentCultureIgnoreCase).ThenBy(stop => stop.CityId, StringComparer.Ordinal),
            TourOrderKind.Priorities => plan.Stops.OrderByDescending(stop => stop.MaximumActivePriority ?? -1)
                .ThenByDescending(stop => stop.HighPriorityCount ?? -1).ThenBy(stop => stop.FirstActiveDue ?? DateTime.MaxValue)
                .ThenBy(stop => stop.CityName, StringComparer.CurrentCultureIgnoreCase),
            TourOrderKind.DueDates => plan.Stops.OrderBy(stop => stop.FirstActiveDue ?? DateTime.MaxValue)
                .ThenByDescending(stop => stop.MaximumActivePriority ?? -1).ThenBy(stop => stop.CityName, StringComparer.CurrentCultureIgnoreCase),
            _ => throw new ArgumentException("Choisissez un tri alphabétique, par priorité ou par échéance.", nameof(order))
        };
        return WithStops(plan, stops, order);
    }

    public static TourPlan Move(TourPlan plan, string cityId, int delta)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta));
        var stops = plan.Stops.ToArray(); var index = Array.FindIndex(stops, stop => stop.CityId == cityId);
        if (index < 0) throw new ArgumentException("L’étape choisie n’appartient pas au plan.", nameof(cityId));
        var destination = index + delta;
        if (destination >= 0 && destination < stops.Length)
            (stops[index], stops[destination]) = (stops[destination], stops[index]);
        return WithStops(plan, stops, TourOrderKind.Manual);
    }

    public static TourPlan ApplySavedOrder(TourPlan plan, IEnumerable<string> cityIds)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(cityIds);
        var byId = plan.Stops.ToDictionary(stop => stop.CityId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); var stops = new List<TourStop>(byId.Count);
        foreach (var id in cityIds)
            if (byId.TryGetValue(id, out var stop) && seen.Add(id)) stops.Add(stop);
        foreach (var stop in plan.Stops) if (seen.Add(stop.CityId)) stops.Add(stop);
        return WithStops(plan, stops, TourOrderKind.Saved);
    }

    public static TourPlan SortByProximity(TourPlan plan, string firstCityId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ct.ThrowIfCancellationRequested();
        if (!plan.CanUseProximity) throw new InvalidOperationException("Chaque étape doit disposer de coordonnées valides pour proposer un ordre à vol d’oiseau.");
        var remaining = plan.Stops.ToList(); var first = remaining.FirstOrDefault(stop => stop.CityId == firstCityId)
            ?? throw new ArgumentException("Choisissez une étape de départ du plan.", nameof(firstCityId));
        var ordered = new List<TourStop>(remaining.Count) { first }; remaining.Remove(first);
        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            TourStop? nearest = null; var minimum = double.PositiveInfinity; var index = 0;
            foreach (var stop in remaining)
            {
                if ((index++ & 255) == 0) ct.ThrowIfCancellationRequested();
                var distance = DistanceKm(ordered[^1], stop);
                if (distance < minimum || distance == minimum && string.CompareOrdinal(stop.CityId, nearest?.CityId) < 0)
                { nearest = stop; minimum = distance; }
            }
            ordered.Add(nearest!); remaining.Remove(nearest!);
        }
        return WithStops(plan, ordered, TourOrderKind.Proximity);
    }

    public static ExtractionResult ToExport(TourPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ct.ThrowIfCancellationRequested();
        var source = plan.Context.Source;
        var headers = new List<string> { "Tournée · étape", "Tournée · ville", "Tournée · code postal", "Tournée · localisation",
            "Tournée · périmètre", "Tournée · statuts", "Tournée · ligne du relevé" };
        var used = headers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var header in source.Headers)
        {
            var title = header; var suffix = 2;
            while (!used.Add(title)) title = header + " (source " + (suffix++).ToString(CultureInfo.InvariantCulture) + ")";
            headers.Add(title);
        }
        var export = new ExtractionResult
        {
            Headers = headers, SourceUrl = source.SourceUrl, Title = source.Title + " · Tournée", ExtractedAt = source.ExtractedAt,
            Pages = source.Pages, Complete = source.Complete, ExpectedTotal = source.Complete ? plan.SelectedRows.Count : null,
            Scope = $"Tournée · {plan.ScopeLabel} · {plan.StatusLabel} · {plan.OrderLabel} · {plan.Stops.Count:N0} étapes numérotées dans l’ordre de visite · "
                + $"{plan.UnlocatedRows.Count:N0} non localisés et {plan.AmbiguousRows.Count:N0} ambigus hors étapes · source : {source.Scope}",
            Status = "Plan local ; aucune écriture dans GLPI", Warnings = source.Warnings.ToList()
        };
        export.Warnings.Add("L’ordre de visite est un repère local. Aucune route, durée de trajet, présence ni intervention sur site n’est déduite du titre d’un ticket.");
        if (!plan.AllStatuses && plan.UnknownStatusRows.Count > 0)
            export.Warnings.Add($"{plan.UnknownStatusRows.Count:N0} statuts non reconnus exclus du plan d’actifs ; accessibles dans la vue Tournées.");
        if (plan.UnlocatedRows.Count + plan.AmbiguousRows.Count > 0)
            export.Warnings.Add("Les lignes non localisées ou ambiguës sont exportées après les étapes, sans numéro ni ville attribuée arbitrairement.");
        var iteration = 0;
        void Add(int index, string order, string city, string postal, string state)
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = new string[headers.Count];
            row[0] = order; row[1] = city; row[2] = postal; row[3] = state; row[4] = plan.ScopeLabel; row[5] = plan.StatusLabel;
            row[6] = (index + 1).ToString(CultureInfo.InvariantCulture);
            for (var column = 0; column < source.Headers.Length; column++)
                row[7 + column] = column < source.Rows[index].Length ? source.Rows[index][column] ?? "" : "";
            export.Rows.Add(row);
        }
        for (var order = 0; order < plan.Stops.Count; order++)
        {
            var stop = plan.Stops[order];
            foreach (var row in stop.Rows) Add(row, (order + 1).ToString(CultureInfo.InvariantCulture), stop.CityName, stop.PostalCode, "Ville confirmée");
        }
        foreach (var row in plan.UnlocatedRows) Add(row, "", "", "", plan.Context.Rows[row].CityState == CityMatchState.TitleUnavailable ? "Titre indisponible" : "Non localisé");
        foreach (var row in plan.AmbiguousRows) Add(row, "", "", "", "Localisation ambiguë");
        if (export.Rows.Count != plan.SelectedRows.Count)
            throw new InvalidDataException("Le plan ne conserve pas exactement le périmètre sélectionné.");
        return export;
    }

    public static int[] RowsForStop(TourPlan plan, string cityId, string metric = "total")
    {
        ArgumentNullException.ThrowIfNull(plan);
        var stop = plan.Stops.FirstOrDefault(item => item.CityId == cityId)
            ?? throw new ArgumentException("L’étape choisie n’appartient pas au plan.", nameof(cityId));
        return metric switch
        {
            "total" => stop.Rows.ToArray(),
            "active" => stop.Rows.Where(index => plan.Context.Rows[index].Active).ToArray(),
            "high" => stop.Rows.Where(index => plan.Context.Rows[index].High).ToArray(),
            "overdue" => stop.Rows.Where(index => plan.Context.Rows[index].Overdue).ToArray(),
            _ => throw new ArgumentException("Indicateur de tournée inconnu.", nameof(metric))
        };
    }

    public static IReadOnlyList<TourTicket> TicketsForStop(TourPlan plan, string cityId, CancellationToken ct = default)
    {
        var rows = RowsForStop(plan, cityId);
        var tickets = new TourTicket[rows.Length];
        string Cell(int index, string role) => plan.Context.Columns[role] is int column && column < plan.Context.Source.Rows[index].Length
            ? plan.Context.Source.Rows[index][column] ?? "" : "";
        for (var i = 0; i < rows.Length; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            var index = rows[i];
            tickets[i] = new(index, Cell(index, "id"), Cell(index, "title"), Cell(index, "status"), Cell(index, "priority"), Cell(index, "due"));
        }
        ct.ThrowIfCancellationRequested();
        return Array.AsReadOnly(tickets);
    }

    private static TourPlan WithStops(TourPlan plan, IEnumerable<TourStop> stops, TourOrderKind order) => new(plan.Context,
        plan.TechnicianKey, plan.ScopeLabel, plan.AllStatuses, stops, plan.SelectedRows, plan.UnlocatedRows, plan.AmbiguousRows,
        plan.TitleUnavailableRows, plan.UnknownStatusRows, plan.ExcludedCompletedCount, plan.Available, plan.Message, order);

    internal static double DistanceKm(TourStop first, TourStop second)
    {
        const double radians = Math.PI / 180d;
        var latitude = (second.Latitude!.Value - first.Latitude!.Value) * radians;
        var longitude = (second.Longitude!.Value - first.Longitude!.Value) * radians;
        var a = Math.Pow(Math.Sin(latitude / 2d), 2d) + Math.Cos(first.Latitude.Value * radians)
            * Math.Cos(second.Latitude.Value * radians) * Math.Pow(Math.Sin(longitude / 2d), 2d);
        return 6371d * 2d * Math.Atan2(Math.Sqrt(Math.Clamp(a, 0, 1)), Math.Sqrt(Math.Clamp(1d - a, 0, 1)));
    }
}

/// <summary>Only technician scope keys and ordered city IDs are persisted; no ticket cells.</summary>
public sealed class TourOrderPreferences
{
    public const int MaximumProfiles = 128;
    public const int MaximumCitiesPerProfile = 5000;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    public int Version { get; set; } = 1;
    public Dictionary<string, TourOrderProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
    public static string ScopeKey(string? technicianKey, bool allStatuses)
    {
        var key = TechnicianDashboardService.IdentityKey(technicianKey);
        return (key.Length == 0 ? "team:" : "technician:" + key) + (allStatuses ? "|all" : "|active");
    }
    public string[] Get(string key) => Profiles.TryGetValue(key, out var profile) ? profile.CityIds.ToArray() : Array.Empty<string>();
    public TourOrderPreferences With(string key, IEnumerable<string> cityIds)
    {
        var copy = Validate(this);
        copy.Profiles[key] = new() { CityIds = cityIds.Distinct(StringComparer.Ordinal).ToArray(), UpdatedUtc = DateTimeOffset.UtcNow };
        copy.Profiles = copy.Profiles.OrderByDescending(pair => pair.Key == key).ThenByDescending(pair => pair.Value.UpdatedUtc)
            .Take(MaximumProfiles).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return Validate(copy);
    }
    public static TourOrderPreferences Load(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        using var owned = stream;
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Le fichier des ordres de tournée est trop volumineux.");
        return Validate(JsonSerializer.Deserialize<TourOrderPreferences>(stream, JsonOptions)
            ?? throw new InvalidDataException("Le fichier des ordres de tournée est vide."));
    }
    public void Save(string path)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Validate(this), JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Les ordres de tournée dépassent la taille autorisée.");
        var fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, fullPath, overwrite: true); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static TourOrderPreferences Validate(TourOrderPreferences source)
    {
        if (source.Version != 1 || source.Profiles == null || source.Profiles.Count > MaximumProfiles)
            throw new InvalidDataException("Les préférences d’ordre de tournée sont invalides.");
        var result = new TourOrderPreferences();
        foreach (var pair in source.Profiles)
        {
            if (pair.Key.Length is 0 or > 450 || pair.Key.Any(char.IsControl) || pair.Value?.CityIds == null
                || pair.Value.CityIds.Length > MaximumCitiesPerProfile || pair.Value.CityIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)))
                throw new InvalidDataException("Une préférence d’ordre de tournée dépasse les limites autorisées.");
            result.Profiles[pair.Key] = new()
            {
                CityIds = pair.Value.CityIds.Distinct(StringComparer.Ordinal).ToArray(), UpdatedUtc = pair.Value.UpdatedUtc
            };
        }
        return result;
    }
}

public sealed class TourOrderProfile
{
    public string[] CityIds { get; set; } = Array.Empty<string>();
    public DateTimeOffset UpdatedUtc { get; set; }
}
