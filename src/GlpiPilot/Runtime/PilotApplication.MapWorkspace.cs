using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

/// <summary>A local, revision-bound view of observed tickets and explicitly planned visits.</summary>
public sealed partial class PilotApplication
{
    private sealed record MapWorkspacePerson(string Key, string Name, int Count);
    private sealed record MapWorkspaceTicket(int RowIndex, string Id, string Title, string Status, string Type,
        string Priority, string Due, string PlaceKey, string PlaceLabel, string LocationKind, bool Located,
        bool LocationReview, bool AssignmentKnown, MapWorkspacePerson[] Technicians);
    private sealed record MapWorkspaceLocation(string Key, EmbeddedMapPlace Place, string Kind, int Count,
        int ReviewCount, int? UnassignedCount, MapWorkspacePerson[] Technicians, int[] Rows);
    private sealed class MapWorkspaceView
    {
        public required string Token { get; init; }
        public required long Revision { get; init; }
        public required string SettingsHash { get; init; }
        public required TourPlanningContext Context { get; init; }
        public required MapWorkspaceTicket[] Tickets { get; init; }
        public required MapWorkspaceLocation[] Locations { get; init; }
        public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
        public HashSet<int> Selected { get; } = new();
        public int SelectionVersion { get; set; }
        public MapWorkspaceTourPreview? TourPreview { get; set; }
    }
    private sealed record MapWorkspaceTourPreview(string Token, int SelectionVersion, int PlanningVersion, ScheduledTour Tour);
    private readonly Dictionary<string, MapWorkspaceView> _mapWorkspaceViews = new(StringComparer.Ordinal);

    private void RegisterMapWorkspaceOperations()
    {
        TerrainRegister("maps.workspace.get", MapWorkspaceGet);
        TerrainRegister("maps.workspace.rows", MapWorkspaceRows);
        TerrainRegister("maps.workspace.select", MapWorkspaceSelect);
        Register("maps.workspace.copyIds", MapWorkspaceCopyIdsAsync);
        TerrainRegister("maps.workspace.tour.preview", MapWorkspacePrepareTour);
        TerrainRegister("maps.workspace.tour.apply", MapWorkspaceApplyTour);
    }

    private string MapWorkspaceSettingsHash() => TerrainDigest(new { EffectiveSettings.Cities, EffectiveSettings.Dashboard,
        Settings.Dashboard.Business, Settings.Dashboard.PersonalTechnician });
    private static string[] MapWorkspaceStrings(JsonElement args, string name, int maximum = 64, int width = 1200)
    {
        if (!args.TryGetProperty(name, out var value)) return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
            throw new InvalidDataException($"La sélection {name} accepte au maximum {maximum} éléments.");
        var items = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : "").ToArray();
        if (items.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > width))
            throw new InvalidDataException("Une sélection de la carte est invalide. Rechargez les choix.");
        return items.Distinct(StringComparer.Ordinal).ToArray();
    }

    private object MapWorkspaceGet(JsonElement args, CancellationToken ct)
    {
        if (args.TryGetProperty("revision", out _)) CheckRevision(args);
        var report = TerrainLocations(ct); var source = report.Data; var settings = EffectiveSettings;
        var baseline = DashboardService.Calculate(source, settings.Dashboard, IsHistorical ? source.ExtractedAt : DateTimeOffset.Now, ct);
        var roster = TechnicianDashboardService.Detect(source, settings.Dashboard, ct);
        var context = TerrainContext(ct); var active = baseline.Metric("active")?.Rows.ToHashSet() ?? new();
        var filters = new List<DashboardScopeFilter>(); AnalyticsBusinessFocus(args, filters, ct);
        var focusKeys = MapWorkspaceStrings(args, "focusKeys");
        var scope = DashboardService.ResolveScope(baseline, filters, ct).Where(active.Contains).ToHashSet();
        var type = Text(args, "type", 20, "all");
        if (type is not ("all" or "incident" or "request" or "unknown")) throw new InvalidDataException("Type de ticket inconnu.");
        var types = baseline.Distributions.FirstOrDefault(distribution => distribution.Key == "type");
        var incidents = types?.Buckets.Where(bucket => bucket.Label == "Incident").SelectMany(bucket => bucket.Rows).ToHashSet() ?? new();
        var requests = types?.Buckets.Where(bucket => bucket.Label == "Demande").SelectMany(bucket => bucket.Rows).ToHashSet() ?? new();
        if (type != "all" && types?.Available != true) throw new InvalidDataException("La colonne Type est absente ou ambiguë. Vérifiez ses correspondances.");
        if (type == "incident") scope.IntersectWith(incidents);
        else if (type == "request") scope.IntersectWith(requests);
        else if (type == "unknown") { scope.ExceptWith(incidents); scope.ExceptWith(requests); }
        var statuses = new HashSet<int> { 1, 2, 3, 4 };
        if (args.TryGetProperty("statusCodes", out var statusInput))
        {
            if (statusInput.ValueKind != JsonValueKind.Array || statusInput.GetArrayLength() is 0 or > 4
                || statusInput.EnumerateArray().Any(item => !item.TryGetInt32(out var code) || code is < 1 or > 4))
                throw new InvalidDataException("Choisissez au moins un des quatre statuts actifs.");
            statuses = statusInput.EnumerateArray().Select(item => item.GetInt32()).ToHashSet();
        }
        string Cell(int row, string role) => baseline.Column(role)?.Index is int column && column < source.Rows[row].Length
            ? DashboardService.NormalizeValue(source.Rows[row][column], role, source.Headers[column], settings.Dashboard) : "";
        scope.RemoveWhere(row => !statuses.Contains(DashboardService.Status(Cell(row, "status"))));
        var owners = new Dictionary<int, List<TechnicianEntry>>();
        foreach (var person in roster.Technicians) foreach (var row in person.Rows)
        { if (!owners.TryGetValue(row, out var values)) owners[row] = values = new(); values.Add(person); }
        var personalKey = TechnicianDashboardService.IdentityKey(Settings.Dashboard.PersonalTechnician);
        var personal = roster.Technicians.FirstOrDefault(person => person.Key == personalKey);
        var mine = Flag(args, "mine"); var technicians = MapWorkspaceStrings(args, "technicianKeys", 64, 400);
        if (mine)
        {
            if (personal is null) throw new InvalidOperationException("Choisissez d’abord votre identité parmi les techniciens de ce relevé pour afficher Mes tickets.");
            technicians = new[] { personal.Key };
        }
        if (technicians.Length > 0)
        {
            if (!roster.Available) throw new InvalidDataException(roster.Message);
            var known = roster.Technicians.Select(person => person.Key).Append("@unassigned").ToHashSet(StringComparer.Ordinal);
            if (technicians.Any(key => !known.Contains(key))) throw new InvalidDataException("Un technicien choisi n’existe plus dans ce relevé. Actualisez les filtres.");
            var selected = technicians.ToHashSet(StringComparer.Ordinal);
            scope.RemoveWhere(row => owners.TryGetValue(row, out var assigned)
                ? !assigned.Any(person => selected.Contains(person.Key)) : !selected.Contains("@unassigned"));
        }

        var reference = settings.Cities; var cities = reference.Cities.ToDictionary(city => city.Id, StringComparer.Ordinal);
        var sites = reference.Sites.ToDictionary(site => site.Id, StringComparer.Ordinal);
        var matches = report.Matches.ToDictionary(match => match.RowIndex);
        var places = new Dictionary<string, (EmbeddedMapPlace Place, string Kind)>(StringComparer.Ordinal);
        (EmbeddedMapPlace Place, string Kind, bool Review) Locate(int row)
        {
            matches.TryGetValue(row, out var match);
            if (match is not null && match.State is CityMatchState.Matched or CityMatchState.SiteOnly)
            {
                if (match.SiteState != "ambiguous" && match.SiteId is { } siteId && sites.TryGetValue(siteId, out var site))
                    return (EmbeddedMapsService.ForSite(site, cities.GetValueOrDefault(site.CityId)), "site", false);
                if (match.State == CityMatchState.Matched && match.CityId is { } cityId && cities.TryGetValue(cityId, out var city))
                {
                    var place = EmbeddedMapsService.ForCity(city);
                    if (match.SiteState == "ambiguous") place = place with { Precision = "Sites possibles multiples · repère de commune uniquement" };
                    return (place, "city", match.SiteState == "ambiguous");
                }
            }
            var kind = match?.State == CityMatchState.Ambiguous || match?.SiteState == "ambiguous" ? "ambiguous"
                : match?.State == CityMatchState.TitleUnavailable ? "title-unavailable" : "unmatched";
            var label = kind switch { "ambiguous" => "Localisation ambiguë", "title-unavailable" => "Localisation non renseignée", _ => "Lieu non reconnu" };
            return (new("unknown:" + kind, label, "", match?.Explanation ?? report.Message, null, null), kind, true);
        }
        var tickets = scope.Order().Select(row =>
        {
            ct.ThrowIfCancellationRequested(); var (place, kind, review) = Locate(row); places.TryAdd(place.Id, (place, kind));
            var assigned = owners.GetValueOrDefault(row)?.Select(person => new MapWorkspacePerson(person.Key, person.Name, 1)).ToArray() ?? Array.Empty<MapWorkspacePerson>();
            return new MapWorkspaceTicket(row, Cell(row, "id"), Cell(row, "title"), Cell(row, "status"), Cell(row, "type"), Cell(row, "priority"), Cell(row, "due"),
                place.Id, place.Label, kind, EmbeddedMapsService.ValidCoordinates(place.Latitude, place.Longitude), review, roster.Available, assigned);
        }).ToArray();
        var locations = tickets.GroupBy(ticket => ticket.PlaceKey, StringComparer.Ordinal).Select(group =>
        {
            var location = places[group.Key]; var entries = group.ToArray();
            var assigned = entries.SelectMany(ticket => ticket.Technicians).GroupBy(person => person.Key, StringComparer.Ordinal)
                .Select(people => new MapWorkspacePerson(people.Key, people.First().Name, people.Count())).OrderByDescending(person => person.Count).ThenBy(person => person.Name).ToArray();
            return new MapWorkspaceLocation(group.Key, location.Place, location.Kind, entries.Length, entries.Count(ticket => ticket.LocationReview),
                roster.Available ? entries.Count(ticket => ticket.Technicians.Length == 0) : null, assigned, entries.Select(ticket => ticket.RowIndex).ToArray());
        }).ToList();
        var includeEmptySites = Flag(args, "includeEmptySites");
        if (includeEmptySites) foreach (var site in reference.Sites)
        {
            var key = "site:" + site.Id; if (locations.Any(location => location.Key == key)) continue;
            locations.Add(new(key, EmbeddedMapsService.ForSite(site, cities.GetValueOrDefault(site.CityId)), "site", 0, 0,
                roster.Available ? 0 : null, Array.Empty<MapWorkspacePerson>(), Array.Empty<int>()));
        }
        var orderedLocations = locations.OrderByDescending(location => location.Count).ThenBy(location => location.Place.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var observed = BusinessDashboardTemplates.Observe(Result, AnalyticsBusinessSettings(), ct);
        var teams = BusinessDashboardTemplates.WeatherRules(AnalyticsBusinessSettings(), observed).Where(rule => rule.Kind == "team")
            .Select(rule => new { key = "team:" + rule.Id, label = rule.Label, groups = rule.Values }).ToArray();
        var date = args.TryGetProperty("visitDate", out _) ? TerrainDate(Text(args, "visitDate", 10)) : DateOnly.FromDateTime(DateTime.Today);
        object[] visits; var visitsWarning = "";
        try { visits = MapWorkspaceVisits(context, reference, tickets, date, technicians, focusKeys.Length > 0 || type != "all" || statuses.Count != 4, ct); }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException)
        { visits = Array.Empty<object>(); visitsWarning = "Le registre des visites prévues n’a pas pu être lu. Les tickets et leurs localisations restent disponibles ; vérifiez le registre des tournées."; }
        var token = MapCreatePreview(reference);
        foreach (var view in _mapWorkspaceViews.Values.Where(view => view.Revision != Revision || view.Created < DateTimeOffset.UtcNow.AddMinutes(-30)).ToArray()) _mapWorkspaceViews.Remove(view.Token);
        if (_mapWorkspaceViews.Count >= 8) _mapWorkspaceViews.Remove(_mapWorkspaceViews.Values.MinBy(view => view.Created)!.Token);
        _mapWorkspaceViews[token] = new() { Token = token, Revision = Revision, SettingsHash = MapWorkspaceSettingsHash(), Context = context, Tickets = tickets, Locations = orderedLocations };
        ct.ThrowIfCancellationRequested();
        return new
        {
            token, revision = Revision, title = "Carte des tickets", simulation = IsSimulationInstance, grain = baseline.Grain,
            sourceDate = source.ExtractedAt, sourceRows = source.Rows.Count, activeAvailable = baseline.Metric("active")?.Value is not null,
            sourceUnknownStatus = baseline.Metric("unknown-status")?.Rows.Length ?? 0,
            totals = new { tickets = tickets.Length, placed = tickets.Count(ticket => ticket.Located), sites = tickets.Count(ticket => ticket.LocationKind == "site"),
                approximate = tickets.Count(ticket => ticket.LocationKind == "city"), review = tickets.Count(ticket => ticket.LocationReview),
                withoutCoordinates = tickets.Count(ticket => !ticket.Located), places = locations.Count(location => location.Kind is "site" or "city"),
                unassigned = roster.Available ? (int?)tickets.Count(ticket => ticket.Technicians.Length == 0) : null },
            filters = new { focusKeys, technicianKeys = mine ? Array.Empty<string>() : technicians, mine, type, includeEmptySites, statusCodes = statuses.Order().ToArray(), visitDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            choices = new { teams, groups = observed.Groups, technicians = roster.Technicians.Select(person => new { person.Key, person.Name }).ToArray(),
                technicianAvailable = roster.Available, typeAvailable = types?.Available == true, personalKey = personal?.Key ?? "", personalName = personal?.Name ?? "" },
            locations = orderedLocations.Select(location => new { location.Key, place = location.Place, location.Kind, location.Count, location.ReviewCount, location.UnassignedCount, location.Technicians }).ToArray(),
            routes = new[] { new EmbeddedMapRoute("tickets", "Localisations du périmètre", false, orderedLocations.Select(location => location.Place).ToArray()) },
            visits, visitsWarning, siteInterventionThreshold = Settings.Dashboard.Business.SiteInterventionThreshold,
            selected = 0, selectionLimit = ScheduledTour.MaximumTickets,
            notes = new[] { "Les marqueurs décrivent les lieux des tickets observés. Les noms affichés sont des affectations, jamais la position actuelle d’une personne.",
                "Une commune seule est un repère approximatif. Aucun géocodage automatique n’est effectué." },
            notice = "Rues OpenStreetMap à l’ouverture ; le fond local prend le relais si Internet est indisponible."
        };
    }

    private object[] MapWorkspaceVisits(TourPlanningContext context, CitySettings reference, MapWorkspaceTicket[] tickets, DateOnly date,
        string[] technicians, bool filterTickets, CancellationToken ct)
    {
        var sourceKey = CityOverrideStore.SourceKey(context.Source.SourceUrl);
        var visibleIds = tickets.Select(ticket => ticket.Id).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var selectedTechnicians = technicians.ToHashSet(StringComparer.Ordinal); var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var visits = new List<object>();
        var tours = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours.Where(tour => tour.SourceKey == sourceKey && tour.State != "done");
        foreach (var tour in tours) foreach (var stop in tour.Stops)
        {
            ct.ThrowIfCancellationRequested(); var plannedDate = stop.Date.Length > 0 ? stop.Date : tour.Date;
            var technician = stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : tour.TechnicianKey;
            if (plannedDate != day || selectedTechnicians.Count > 0 && !selectedTechnicians.Contains(technician.Length > 0 ? technician : "@unassigned")
                || filterTickets && !stop.TicketIds.Any(visibleIds.Contains)) continue;
            EmbeddedMapPlace? place = null;
            if (stop.SiteId.Length > 0 && reference.Sites.FirstOrDefault(site => site.Id == stop.SiteId) is { } site)
                place = EmbeddedMapsService.ForSite(site, reference.Cities.FirstOrDefault(city => city.Id == site.CityId));
            else if (stop.SiteId.Length == 0 && reference.Cities.FirstOrDefault(city => city.Id == stop.CityId) is { } city)
                place = EmbeddedMapsService.ForCity(city);
            place ??= new(stop.PlaceKey, "Lieu absent du référentiel courant", "", "Position non vérifiable", null, null);
            visits.Add(new { id = tour.Id + ":" + stop.Id, tourId = tour.Id, tourName = tour.Name, date = plannedDate, state = tour.State,
                technicianKey = technician, technician = context.Technicians.FirstOrDefault(person => person.Key == technician)?.Name ?? (technician.Length == 0 ? "À attribuer" : technician),
                count = stop.TicketIds.Count, inScope = stop.TicketIds.Count(visibleIds.Contains), place,
                label = "PRÉVU · " + plannedDate });
        }
        return visits.ToArray();
    }

    private MapWorkspaceView MapWorkspaceRequire(JsonElement args)
    {
        var token = Text(args, "token", 40);
        if (!_mapWorkspaceViews.TryGetValue(token, out var view)) throw new InvalidOperationException("La carte a expiré. Actualisez son périmètre.");
        if (view.Revision != Revision || view.SettingsHash != MapWorkspaceSettingsHash())
            throw new InvalidOperationException("Le relevé ou ses règles ont changé. Actualisez la carte avant de poursuivre.");
        return view;
    }
    private object MapWorkspaceRows(JsonElement args, CancellationToken ct)
    {
        var view = MapWorkspaceRequire(args); var placeKey = Text(args, "placeKey", 80); var term = Text(args, "search", 300).Trim(); var (offset, limit) = TerrainPage(args);
        if (placeKey.Length > 0 && view.Locations.All(location => location.Key != placeKey)) throw new InvalidDataException("Ce lieu n’appartient pas au périmètre de la carte.");
        var rows = view.Tickets.Where(ticket => (placeKey.Length == 0 || ticket.PlaceKey == placeKey)
            && (TerrainContains(ticket.Id, term) || TerrainContains(ticket.Title, term))).ToArray();
        ct.ThrowIfCancellationRequested();
        return new { token = view.Token, revision = view.Revision, total = rows.Length, offset, limit, selected = view.Selected.Count,
            items = rows.Skip(offset).Take(limit).Select(ticket => new { ticket, selected = view.Selected.Contains(ticket.RowIndex) }).ToArray() };
    }
    private object MapWorkspaceSelect(JsonElement args, CancellationToken ct)
    {
        var view = MapWorkspaceRequire(args); var next = view.Selected.ToHashSet();
        if (Flag(args, "clear")) next.Clear();
        else
        {
            var requested = args.TryGetProperty("rows", out _) ? TerrainRead<int[]>(args, "rows", 100_000) : Array.Empty<int>();
            var keys = MapWorkspaceStrings(args, "placeKeys", CitySettings.MaximumSites + 1000, 80).ToHashSet(StringComparer.Ordinal);
            if (keys.Any(key => view.Locations.All(location => location.Key != key))) throw new InvalidDataException("Un lieu sélectionné n’appartient pas à cette carte.");
            var allowed = view.Tickets.Select(ticket => ticket.RowIndex).ToHashSet();
            if (requested.Any(row => !allowed.Contains(row))) throw new InvalidDataException("Une ligne sélectionnée est hors du périmètre de cette carte.");
            var rows = Flag(args, "all") ? allowed : requested.Concat(view.Locations.Where(location => keys.Contains(location.Key)).SelectMany(location => location.Rows)).ToHashSet();
            if (Flag(args, "selected", true)) next.UnionWith(rows); else next.ExceptWith(rows);
            if (next.Count > ScheduledTour.MaximumTickets) throw new InvalidDataException("Une tournée accepte au maximum 2 000 tickets. Réduisez le périmètre ou la sélection ; aucun ticket n’a été ajouté.");
        }
        ct.ThrowIfCancellationRequested(); view.Selected.Clear(); view.Selected.UnionWith(next); view.SelectionVersion++; view.TourPreview = null;
        return new { selected = next.Count, rows = next.Order().ToArray() };
    }
    private async Task<object?> MapWorkspaceCopyIdsAsync(JsonElement args, CancellationToken ct)
    {
        var view = MapWorkspaceRequire(args); var key = Text(args, "placeKey", 80);
        if (key.Length > 0 && view.Locations.All(location => location.Key != key)) throw new InvalidDataException("Ce lieu n’appartient pas au périmètre de la carte.");
        var tickets = view.Tickets.Where(ticket => key.Length == 0 || ticket.PlaceKey == key).ToArray();
        var ids = tickets.Select(ticket => ticket.Id).Where(id => id.Length > 0).ToArray();
        if (ids.Length == 0) throw new InvalidDataException("Aucun identifiant exploitable à copier dans ce périmètre.");
        ct.ThrowIfCancellationRequested(); await Host.SetClipboardAsync(string.Join(Environment.NewLine, ids));
        return new { copied = ids.Length, missing = tickets.Length - ids.Length, total = tickets.Length };
    }
    private object MapWorkspacePrepareTour(JsonElement args, CancellationToken ct)
    {
        var view = MapWorkspaceRequire(args); var technician = Text(args, "technicianKey", 400);
        if (technician.Length > 0 && view.Context.Technicians.All(person => person.Key != technician)) throw new InvalidDataException("Le technicien de la tournée n’est pas présent dans ce relevé.");
        var tour = ScheduledTourService.Create(view.Context, view.Selected.Order(), Text(args, "name", 120), TerrainDate(Text(args, "date", 10)), technician, ct);
        var token = Guid.NewGuid().ToString("N"); view.TourPreview = new(token, view.SelectionVersion, _terrainTourDraftVersion, tour);
        return new { token, revision = Revision, name = tour.Name, date = tour.Date, technician = view.Context.Technicians.FirstOrDefault(person => person.Key == technician)?.Name ?? "À attribuer",
            tickets = tour.Tickets.Count, unlocated = tour.Tickets.Count - tour.Stops.Sum(stop => stop.TicketIds.Count),
            stops = tour.Stops.Select(stop => new { place = ScheduledTourService.PlaceName(view.Context, stop), count = stop.TicketIds.Count, date = tour.Date,
                point = ScheduledTourService.PointFor(view.Context, stop.PlaceKey) }).ToArray(),
            dirtyDraft = _terrainTourDirty, currentDraftName = _terrainTourDirty ? _terrainTourDraft?.Name ?? "" : "", simulation = IsSimulationInstance };
    }
    private object MapWorkspaceApplyTour(JsonElement args, CancellationToken ct)
    {
        var view = MapWorkspaceRequire(args); var preview = view.TourPreview;
        if (preview is null || preview.Token != Text(args, "previewToken", 40) || preview.SelectionVersion != view.SelectionVersion || preview.PlanningVersion != _terrainTourDraftVersion)
            throw new InvalidOperationException("La sélection ou le planning a changé. Préparez à nouveau l’aperçu de tournée.");
        TerrainMaySwitch(args); ct.ThrowIfCancellationRequested();
        _terrainTourBaseline = null; _ = TerrainSetTour(preview.Tour); view.TourPreview = null;
        return new { tourId = _terrainTourDraft!.Id, draftVersion = _terrainTourDraftVersion, revision = Revision, saved = false, simulation = IsSimulationInstance };
    }
}
