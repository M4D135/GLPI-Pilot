using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record CalendarTicketRow(string Key, string Installation, string Id, string Url, string Title, int RowIndex,
        string Status, string Type, bool Active, string SiteId, string Location, string Address, string[] ParticipantKeys, string[] TeamKeys, string Due)
    {
        public CalendarTicketReference Reference() => new() { Key = Key, Installation = Installation, Id = Id, Url = Url, Title = Title };
    }
    private sealed record CalendarTeam(string Key, string Label, IReadOnlyList<string> Groups);
    private IReadOnlyList<CalendarTicketRow> _calendarCatalog = Array.Empty<CalendarTicketRow>();
    private IReadOnlyList<CalendarTeam> _calendarTeams = Array.Empty<CalendarTeam>();
    private string _calendarCatalogStamp = "";
    private IReadOnlyList<CalendarTicketRow> CalendarCatalog(CancellationToken ct)
    {
        if (Result is null) { _calendarTeams = Settings.Dashboard.Business.WeatherRules.Where(rule => rule.Kind == "team").Select(rule => new CalendarTeam("team:" + rule.Id, rule.Label, rule.Values)).ToArray(); return Array.Empty<CalendarTicketRow>(); }
        var settings = EffectiveSettings; var stamp = Revision + ":" + TerrainDigest(new { settings.Dashboard, settings.Cities, Settings.Dashboard.Business });
        if (_calendarCatalogStamp == stamp) return _calendarCatalog;
        var data = RequireResult(); var columns = DashboardService.ResolveColumns(data.Headers, settings.Dashboard).ToDictionary(column => column.Role, column => column.Index, StringComparer.Ordinal);
        string Cell(int index, string role) => columns.GetValueOrDefault(role) is int column && column < data.Rows[index].Length
            ? DashboardService.NormalizeValue(data.Rows[index][column], role, data.Headers[column], settings.Dashboard) : "";
        var roster = TechnicianDashboardService.Detect(data, settings.Dashboard, ct); var owners = new Dictionary<int, List<string>>();
        foreach (var person in roster.Technicians) foreach (var index in person.Rows)
        { if (!owners.TryGetValue(index, out var keys)) owners[index] = keys = new(); keys.Add(person.Key); }
        var businessSettings = AnalyticsBusinessSettings(); var observed = BusinessDashboardTemplates.Observe(data, businessSettings, ct); var business = BusinessDashboardTemplates.Profile(businessSettings, observed);
        var teams = new List<CalendarTeam>();
        foreach (var rule in business.WeatherRules.Where(rule => rule.Kind == "team" && rule.Values.Count > 0))
        {
            var groups = rule.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!teams.Any(team => groups.SetEquals(team.Groups))) teams.Add(new("team:" + rule.Id, rule.Label, rule.Values.ToArray()));
        }
        foreach (var group in observed.Groups)
            if (!teams.Any(team => team.Groups.Count == 1 && string.Equals(team.Groups[0], group, StringComparison.OrdinalIgnoreCase))) teams.Add(new("group:" + group, group, new[] { group }));
        var report = TerrainLocations(ct); var matches = report.Matches.ToDictionary(match => match.RowIndex); var sites = settings.Cities.Sites.ToDictionary(site => site.Id, StringComparer.Ordinal);
        var rows = new Dictionary<string, CalendarTicketRow>(StringComparer.Ordinal); var duplicate = new HashSet<string>(StringComparer.Ordinal);
        var localInstallation = IsFileSource && CityOverrideStore.SourceKey(data).Length > 0 ? CityOverrideStore.SourceKey(data) : "local:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.Provenance.RunId))).ToLowerInvariant();
        for (var index = 0; index < data.Rows.Count; index++)
        {
            if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
            var id = Cell(index, "id"); var title = Cell(index, "title"); if (id.Length == 0) continue;
            string key, installation, url;
            try
            {
                var ticket = IsSimulationInstance ? SimulationReference(index) : TicketReferenceResolver.Resolve(data, data.Rows[index], settings);
                id = ticket.Id; url = ticket.Url; installation = ObjectIdentity.Installation(url); key = WorkplaceStore.TicketKey(ticket);
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
            {
                // An unidentified local source does not borrow the configured GLPI installation. Its link remains a snapshot.
                installation = localInstallation; url = ""; key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation + "\n" + id))).ToLowerInvariant();
            }
            if (duplicate.Contains(key)) continue;
            if (rows.ContainsKey(key)) { rows.Remove(key); duplicate.Add(key); continue; }
            var match = matches.GetValueOrDefault(index); var siteId = match?.SiteState == "ambiguous" ? "" : match?.SiteId ?? ""; var site = sites.GetValueOrDefault(siteId);
            var location = site?.Name ?? match?.CityName ?? Cell(index, "location");
            var groups = DashboardService.SplitTechnicianNames(Cell(index, "group"), settings.Dashboard.MultiValueSeparators);
            var teamKeys = teams.Where(team => team.Groups.Any(group => groups.Contains(group, StringComparer.OrdinalIgnoreCase))).Select(team => team.Key).ToArray();
            var status = Cell(index, "status"); var code = DashboardService.Status(status);
            rows[key] = new(key, installation, id, url, title.Length > 2000 ? title[..2000] : title, index, status, Cell(index, "type"), code < 5,
                siteId, location, site?.Address ?? "", owners.GetValueOrDefault(index)?.ToArray() ?? Array.Empty<string>(), teamKeys, Cell(index, "due"));
        }
        ct.ThrowIfCancellationRequested(); _calendarCatalog = rows.Values.ToArray(); _calendarTeams = teams; _calendarCatalogStamp = stamp; return _calendarCatalog;
    }
    private IReadOnlyList<CalendarTeam> CalendarTeams(IReadOnlyList<CalendarTicketRow> catalog) => _calendarTeams;
    private async Task<object?> CalendarTicketsAsync(JsonElement a, CancellationToken ct)
    {
        if (a.TryGetProperty("revision", out _)) CheckRevision(a);
        var rows = CalendarCatalog(ct); var key = Text(a, "ticketKey", 300); var search = Text(a, "search", 300); var indices = new HashSet<int>();
        if (a.TryGetProperty("indices", out var selected))
        {
            if (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() > 2000) throw new InvalidDataException("Choisissez au maximum 2 000 tickets à planifier.");
            foreach (var item in selected.EnumerateArray()) if (item.TryGetInt32(out var index) && index >= 0 && index < (Result?.Rows.Count ?? 0)) indices.Add(index); else throw new InvalidDataException("Une ligne sélectionnée n’existe plus dans le relevé.");
        }
        if (a.TryGetProperty("rowIndex", out _)) indices.Add(Number(a, "rowIndex", -1, 0, Math.Max(0, (Result?.Rows.Count ?? 0) - 1)));
        var people = MapWorkspaceStrings(a, "technicians", 200, 400).ToHashSet(StringComparer.Ordinal);
        var teams = MapWorkspaceStrings(a, "teams", 100, 1200).ToHashSet(StringComparer.Ordinal);
        if (Text(a, "scope", 20, "all") == "mine") people = new(StringComparer.Ordinal) { Settings.Dashboard.PersonalTechnician };
        var booked = new HashSet<string>(StringComparer.Ordinal);
        if (Flag(a, "unplanned"))
        {
            var view = await CalendarViewAsync(a, ct).ConfigureAwait(false);
            booked = view.All.Where(value => value.State is not ("cancelled" or "done")).SelectMany(value => value.Tickets).Select(ticket => ticket.Key).ToHashSet(StringComparer.Ordinal);
        }
        var filtered = rows.Where(row => (key.Length == 0 || row.Key == key) && (indices.Count == 0 || indices.Contains(row.RowIndex))
            && (!Flag(a, "unplanned") || row.Active && !booked.Contains(row.Key))
            && (people.Count == 0 || row.ParticipantKeys.Any(people.Contains) || people.Contains("@unassigned") && row.ParticipantKeys.Length == 0)
            && (teams.Count == 0 || row.TeamKeys.Any(teams.Contains))
            && (search.Length == 0 || TicketReadingService.Contains(row.Id + " " + row.Title + " " + row.Location, search))).ToList();
        if (key.Length > 0 && filtered.Count == 0 && WorkDocument().Tickets.FirstOrDefault(ticket => ticket.Key == key) is { } cached)
            filtered.Add(new(cached.Key, ObjectIdentity.Installation(cached.Url), cached.Id, cached.Url, cached.Title, -1, "Absent du relevé", "", false, "", "", "", Array.Empty<string>(), Array.Empty<string>(), ""));
        var offset = Number(a, "offset", 0, 0, 1000000); var limit = Number(a, "limit", indices.Count > 0 ? Math.Min(indices.Count, 2000) : 50, 1, 2000);
        var selectedRows = filtered.Skip(offset).Take(limit).ToArray();
        return new { rows = selectedRows, tickets = selectedRows, total = filtered.Count, offset, limit, revision = Revision, sourceRevision = Revision };
    }
    private async Task<IReadOnlyList<CalendarResource>> CalendarResourcesAsync(CalendarWindow window, IReadOnlyList<TeamWorkspaceItem> documents, CancellationToken ct)
    {
        var saved = await TeamWorkspace.CalendarResourcesAsync(ct).ConfigureAwait(false);
        var resources = saved.Select(CalendarWorkspaceDocuments.ReadResource).ToDictionary(resource => resource.Key, StringComparer.Ordinal);
        var preferences = WorkTechnicianPreferences(); var catalog = CalendarCatalog(ct);
        var names = Result is null ? new Dictionary<string, string>() : TechnicianDashboardService.Detect(RequireResult(), EffectiveSettings.Dashboard, ct).Technicians.ToDictionary(person => person.Key, person => person.Name, StringComparer.Ordinal);
        var calendarPeople = documents.Select(CalendarWorkspaceDocuments.ReadIntervention).SelectMany(value => value.ParticipantKeys
            .Concat(value.Exceptions.Where(exception => exception.Replacement is not null).SelectMany(exception => exception.Replacement!.ParticipantKeys)));
        var tourPeople = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours.SelectMany(tour => tour.Stops.Select(stop => stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : tour.TechnicianKey)).Where(key => key.Length > 0);
        foreach (var key in preferences.Technicians.Keys.Concat(names.Keys).Concat(calendarPeople).Concat(tourPeople).Distinct(StringComparer.Ordinal))
        {
            if (!resources.TryGetValue(key, out var resource))
            {
                resource = new() { Key = key, Label = names.GetValueOrDefault(key, key), TimeZoneId = window.Zone,
                    TeamKeys = catalog.Where(ticket => ticket.ParticipantKeys.Contains(key, StringComparer.Ordinal)).SelectMany(ticket => ticket.TeamKeys).Distinct(StringComparer.Ordinal).ToList() };
                resources.Add(key, resource);
                var preference = preferences.Get(key);
                resource.WorkPeriods = preference.WorkPeriods.Select(period => new CalendarWorkPeriod { Day = period.Day == DayOfWeek.Sunday ? 7 : (int)period.Day, StartMinute = period.StartMinute, EndMinute = period.EndMinute, CapacityMinutes = period.CapacityMinutes }).ToList();
                resource.CapacityDays = preference.CapacityDays.Select(day => new CalendarCapacityDay { Date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), MorningMinutes = day.MorningMinutes, AfternoonMinutes = day.AfternoonMinutes }).ToList();
            }
        }
        return resources.Values.OrderBy(resource => resource.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private async Task<object?> CalendarSaveResourceAsync(JsonElement a, CancellationToken ct)
    {
        var resource = CalendarArgument<CalendarResource>(a, "resource"); if (resource.Key.Length == 0) resource.Key = "resource:" + Guid.NewGuid().ToString("N");
        InterventionCalendarService.ValidateResource(resource);
        var existing = await TeamWorkspace.CalendarResourcesAsync(ct).ConfigureAwait(false); var id = CalendarWorkspaceDocuments.ResourceId(resource.Key);
        if (existing.All(item => item.Id != id) && existing.Count >= 2000) throw new InvalidDataException("Le calendrier accepte au maximum 2 000 ressources.");
        var item = await TeamWorkspace.SaveAsync(CalendarWorkspaceDocuments.ForResource(resource, CalendarAuthor), CalendarExpected(a), ct).ConfigureAwait(false);
        if (Flag(a, "chooseAsMe")) { Settings.Dashboard.PersonalTechnician = resource.Key; SaveSettings(); Publish("workplace.changed", new { revision = Revision }); }
        Publish("calendar.changed", new { resource = resource.Key });
        return new { saved = true, resource, id = item.Id, item.Revision, item.Pending, personalTechnician = Settings.Dashboard.PersonalTechnician };
    }
    private async Task<object?> CalendarIdentityAsync(JsonElement a, CancellationToken ct)
    {
        var key = Text(a, "key", 400); if (key.Length == 0) throw new InvalidDataException("Choisissez votre nom dans les ressources du calendrier.");
        var window = CalendarRange(JsonSerializer.SerializeToElement(new { }, Json)); var resources = await CalendarResourcesAsync(window, Array.Empty<TeamWorkspaceItem>(), ct).ConfigureAwait(false);
        var resource = resources.FirstOrDefault(value => value.Key == key) ?? throw new InvalidDataException("La ressource choisie n’existe plus. Ajoutez-la au calendrier avant de l’utiliser.");
        var id = CalendarWorkspaceDocuments.ResourceId(key);
        if (await TeamWorkspace.GetAsync(id, ct).ConfigureAwait(false) is null)
            await TeamWorkspace.SaveAsync(CalendarWorkspaceDocuments.ForResource(resource, CalendarAuthor), null, ct).ConfigureAwait(false);
        Settings.Dashboard.PersonalTechnician = key; SaveSettings(); Publish("workplace.changed", new { revision = Revision }); Publish("calendar.changed", new { identity = key });
        return new { personalTechnician = key, label = resource.Label };
    }
    private IReadOnlyList<CalendarOccurrence> CalendarTours(CalendarWindow window, CancellationToken ct, ScheduledTour? proposed = null)
    {
        _calendarTourIssues.Clear(); var store = ScheduledTourStore.Load(DataPath("scheduled-tours.json")); var values = new List<CalendarOccurrence>();
        var tours = proposed is null ? store.Tours : store.Tours.Where(tour => tour.Id != proposed.Id).Append(proposed);
        foreach (var source in tours)
        {
            ct.ThrowIfCancellationRequested();
            if (!source.Stops.Any(stop => string.CompareOrdinal(stop.Date.Length > 0 ? stop.Date : source.Date, window.From.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0
                && string.CompareOrdinal(stop.Date.Length > 0 ? stop.Date : source.Date, window.To.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0)) continue;
            TourPlanningContext context;
            if (Result is not null) context = TerrainContext(ct);
            else
            {
                var empty = new ExtractionResult { SourceUrl = source.SourceKey, Headers = new() { "ID" }, Complete = false };
                context = TourPlanningService.Prepare(CityDetectionService.Enrich(empty, Settings.Cities, Settings.Dashboard, ct), Settings.Cities, Settings.Dashboard, ct: ct);
            }
            var tour = source.Copy(); TerrainRefreshAvailability(tour); var schedule = ScheduledTourService.Calculate(tour, context, ct);
            foreach (var visit in schedule.Visits)
            {
                var stop = visit.Stop; DateTimeOffset start, end;
                try { start = InterventionCalendarService.ResolveLocal(visit.Start, InterventionCalendarService.Zone(tour.TimeZoneId)); end = InterventionCalendarService.ResolveLocal(visit.Finish, InterventionCalendarService.Zone(tour.TimeZoneId)); }
                catch (InvalidDataException error)
                {
                    _calendarTourIssues.Add(new("tour-time-invalid", "danger", "La tournée « " + tour.Name + " » comporte une visite dont l’horaire doit être corrigé : " + error.Message,
                        stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : tour.TechnicianKey, new[] { "tour:" + tour.Id + ":stop:" + stop.Id })
                    { StartUtc = new DateTimeOffset(visit.Start.Date, InterventionCalendarService.Zone(tour.TimeZoneId).BaseUtcOffset).ToUniversalTime(),
                      EndUtc = new DateTimeOffset(visit.Start.Date.AddDays(1), InterventionCalendarService.Zone(tour.TimeZoneId).BaseUtcOffset).ToUniversalTime() });
                    continue;
                }
                if (end <= window.From || start >= window.To) continue;
                var technician = stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : tour.TechnicianKey;
                var sourceInstallation = "tour:" + tour.SourceKey;
                try { sourceInstallation = ObjectIdentity.Installation(tour.SourceKey); } catch (InvalidDataException) { }
                var known = CalendarCatalog(ct).Where(ticket => ticket.Installation == sourceInstallation).GroupBy(ticket => ticket.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                var references = stop.TicketIds.Select(id => known.TryGetValue(id, out var ticket) ? ticket.Reference() : new CalendarTicketReference { Id = id, Installation = sourceInstallation,
                    Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceInstallation + "\n" + id))).ToLowerInvariant(), Title = "Ticket " + id }).ToList();
                var warnings = new List<string>();
                if (visit.SiteClosed || visit.TechnicianUnavailable || visit.DueExceeded || visit.Late) warnings.Add(visit.Warning);
                if (schedule.Reconciliation.Any(row => stop.TicketIds.Contains(row.TicketId, StringComparer.Ordinal) && row.State != "Inchangé")) warnings.Add("Les tickets ou leurs lieux doivent être rapprochés du relevé courant avant le départ.");
                values.Add(new() { Key = "tour:" + tour.Id + ":stop:" + stop.Id, InterventionId = "tour:" + tour.Id, Source = "tour", TourId = tour.Id, StopId = stop.Id, ReadOnly = true,
                    OriginalStartLocal = InterventionCalendarService.FormatLocal(visit.Start), StartLocal = InterventionCalendarService.FormatLocal(visit.Start), EndLocal = InterventionCalendarService.FormatLocal(visit.Finish),
                    StartUtc = start, EndUtc = end, TimeZoneId = tour.TimeZoneId, Title = tour.Name + " · " + visit.Place, State = tour.State == "done" ? "done" : tour.State == "ready" ? "confirmed" : "planned",
                    ParticipantKeys = technician.Length > 0 ? new() { technician } : new(), Tickets = references, SiteId = stop.SiteId, Location = visit.Place, Address = visit.Address,
                    CityId = stop.CityId,
                    Notes = stop.Notes, Contact = stop.Contact, Equipment = stop.Equipment, RequiredSkills = stop.RequiredSkills.ToList(), TravelBeforeMinutes = visit.TravelMinutesUsed, Warnings = warnings });
            }
        }
        return values;
    }
    private readonly List<CalendarConflict> _calendarTourIssues = new();
}
