using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record CalendarWindow(DateTimeOffset From, DateTimeOffset To, string FromDate, string ToDate, string Zone);
    private sealed record CalendarView(CalendarWindow Window, IReadOnlyList<TeamWorkspaceItem> Documents, IReadOnlyList<CalendarResource> Resources,
        IReadOnlyList<CalendarOccurrence> All, IReadOnlyList<CalendarOccurrence> Visible, IReadOnlyList<CalendarConflict> Issues);
    private readonly HashSet<string> _calendarReminderNotified = new(StringComparer.Ordinal);
    private DateTimeOffset? _nextCalendarReminderCheck;
    private void RegisterCalendarOperations()
    {
        Register("calendar.get", (a, ct) => CalendarGetAsync(a, false, ct));
        Register("calendar.summary", (a, ct) => CalendarGetAsync(a, true, ct));
        Register("calendar.item", CalendarItemAsync);
        Register("calendar.save", (a, ct) => CalendarSaveAsync(a, false, ct));
        Register("calendar.preview", (a, ct) => CalendarSaveAsync(a, true, ct));
        Register("calendar.remove", CalendarRemoveAsync);
        Register("calendar.undo", CalendarUndoAsync);
        Register("calendar.shift", CalendarShiftAsync);
        Register("calendar.suggest", CalendarSuggestAsync);
        Register("calendar.export", CalendarExportAsync);
        Register("calendar.resource.save", CalendarSaveResourceAsync);
        Register("calendar.identity", CalendarIdentityAsync);
        Register("calendar.tickets", CalendarTicketsAsync);
        Register("calendar.forTicket", CalendarForTicketAsync);
        Register("calendar.reminders.check", CalendarRemindersAsync);
        Register("calendar.conflicts.list", CalendarConflictListAsync);
        Register("calendar.conflicts.get", CalendarConflictGetAsync);
        Register("calendar.conflicts.resolve", CalendarConflictResolveAsync);
        Register("calendar.conflicts.export", CalendarConflictExportAsync);
    }
    private static CalendarWindow CalendarRange(JsonElement a, int maximumDays = 93)
    {
        var zoneId = Text(a, "timeZoneId", 150, TimeZoneInfo.Local.Id);
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zoneId, out var iana)) zoneId = iana;
        var zone = InterventionCalendarService.Zone(zoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var from = CalendarDate(Text(a, "from", 10, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var to = CalendarDate(Text(a, "to", 10, from.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (to <= from || to.DayNumber - from.DayNumber > maximumDays) throw new InvalidDataException($"La vue accepte une période de 1 à {maximumDays} jours, avec une fin exclusive.");
        return new(InterventionCalendarService.ResolveLocal(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00", zoneId),
            InterventionCalendarService.ResolveLocal(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00", zoneId),
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), zoneId);
    }
    private static DateOnly CalendarDate(string value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day.Year is >= 2000 and <= 2100
            ? day : throw new InvalidDataException("Date du calendrier invalide (AAAA-MM-JJ, entre 2000 et 2100).");
    private static long? CalendarExpected(JsonElement a, string name = "expectedRevision")
    {
        if (!a.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt64(out var revision) || revision < 0) throw new InvalidDataException("Version du calendrier invalide."); return revision;
    }
    private static T CalendarArgument<T>(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Données de calendrier absentes ou trop volumineuses.");
        return Read<T>(value);
    }
    private string CalendarAuthor => string.IsNullOrWhiteSpace(LibrarySettings.DisplayName) ? "Utilisateur local" : LibrarySettings.DisplayName;
    private static object CalendarItemReply(TeamWorkspaceItem item)
    {
        var intervention = CalendarWorkspaceDocuments.ReadIntervention(item);
        return new { intervention, item.Revision, item.Pending, item.Conflicts, item.Deleted, item.Author, item.UpdatedAt, history = intervention.History };
    }
    private async Task<object?> CalendarItemAsync(JsonElement a, CancellationToken ct)
    {
        var item = await CalendarRequireItemAsync(Text(a, "id", 32), ct).ConfigureAwait(false); return CalendarItemReply(item);
    }
    private async Task<TeamWorkspaceItem> CalendarRequireItemAsync(string id, CancellationToken ct, bool deleted = false)
    {
        var item = await TeamWorkspace.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null || item.Kind != CalendarWorkspaceDocuments.InterventionKind || item.Deleted && !deleted)
            throw new InvalidDataException("Cette intervention n’existe plus dans le planning. Actualisez la vue.");
        return item;
    }
    private async Task<IReadOnlyList<TeamWorkspaceItem>> CalendarDocumentsAsync(CalendarWindow window, CancellationToken ct)
        // Two-day padding covers every supported timezone and overnight appointment; exact comparison uses UTC below.
        => await TeamWorkspace.CalendarItemsAsync(window.From.UtcDateTime.AddDays(-2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            window.To.UtcDateTime.AddDays(2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
    private async Task<CalendarView> CalendarViewAsync(JsonElement a, CancellationToken ct, int maximumDays = 93)
    {
        var window = CalendarRange(a, maximumDays); var documents = await CalendarDocumentsAsync(window, ct).ConfigureAwait(false);
        var resources = await CalendarResourcesAsync(window, documents, ct).ConfigureAwait(false);
        var expansion = InterventionCalendarService.ExpandDetailed(documents.Select(CalendarWorkspaceDocuments.ReadIntervention), window.From, window.To, ct);
        var all = expansion.Occurrences.Concat(CalendarTours(window, ct)).OrderBy(value => value.StartUtc).ThenBy(value => value.Key, StringComparer.Ordinal).ToArray();
        var knownSites = EffectiveSettings.Cities.Sites.ToDictionary(site => site.Id, StringComparer.Ordinal);
        foreach (var occurrence in all.Where(value => value.CityId.Length == 0 && value.SiteId.Length > 0)) occurrence.CityId = knownSites.GetValueOrDefault(occurrence.SiteId)?.CityId ?? "";
        var catalogKeys = CalendarCatalog(ct).Select(ticket => ticket.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var occurrence in all.Where(value => value.Tickets.Count > 0))
        {
            var absent = occurrence.Tickets.Count(ticket => !catalogKeys.Contains(ticket.Key));
            if (absent > 0) occurrence.Warnings.Add(Result is null ? "Aucun relevé chargé : les références de tickets restent conservées sans vérification actuelle."
                : absent.ToString(CultureInfo.InvariantCulture) + " ticket(s) liés sont absents du relevé courant ; l’intervention reste planifiée.");
        }
        var issues = CalendarIssues(all, resources, ct).Concat(_calendarTourIssues).Concat(expansion.Issues.Select(issue => new CalendarConflict("invalid-occurrence", "danger", issue.Message, "", new[] { issue.InterventionId + ":" + issue.OriginalStartLocal }))).ToArray();
        var visible = CalendarFilter(a, all, resources); var visibleKeys = visible.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        // Invalid recurrence occurrences have no rendered card, so keep their issue visible in the selected resource scope.
        var visibleIds = visible.Select(value => value.InterventionId).ToHashSet(StringComparer.Ordinal);
        var filteredIssues = issues.Where(issue => issue.OccurrenceKeys.Any(visibleKeys.Contains) || issue.Kind == "tour-time-invalid"
            || issue.Kind == "invalid-occurrence" && documents.Any(doc => issue.OccurrenceKeys.Any(key => key.StartsWith(doc.Id + ":", StringComparison.Ordinal))
                && CalendarFilter(a, new[] { CalendarIssueScope(CalendarWorkspaceDocuments.ReadIntervention(doc)) }, resources).Count > 0)).ToArray();
        return new(window, documents, resources, all, visible, filteredIssues);
    }
    private static CalendarOccurrence CalendarIssueScope(CalendarIntervention value) => new()
    { InterventionId = value.Id, ParticipantKeys = value.ParticipantKeys, SiteId = value.SiteId, Tickets = value.Tickets };
    private IReadOnlyList<CalendarOccurrence> CalendarFilter(JsonElement a, IEnumerable<CalendarOccurrence> source, IReadOnlyList<CalendarResource> resources)
    {
        var technicians = MapWorkspaceStrings(a, "technicians", 200, 400).ToHashSet(StringComparer.Ordinal);
        var teams = MapWorkspaceStrings(a, "teams", 100, 1200).ToHashSet(StringComparer.Ordinal);
        var scope = Text(a, "scope", 20, "all");
        if (scope is not ("mine" or "all")) throw new InvalidDataException("Périmètre du calendrier inconnu.");
        if (scope == "mine")
        {
            var personal = Settings.Dashboard.PersonalTechnician;
            if (personal.Length == 0) return Array.Empty<CalendarOccurrence>();
            technicians = new(StringComparer.Ordinal) { personal };
        }
        var teamPeople = resources.Where(resource => resource.TeamKeys.Any(teams.Contains)).Select(resource => resource.Key).ToHashSet(StringComparer.Ordinal);
        var ticket = Text(a, "ticketKey", 300); var site = Text(a, "siteId", 80);
        return source.Where(value => (technicians.Count == 0 || value.ParticipantKeys.Any(technicians.Contains) || technicians.Contains("@unassigned") && value.ParticipantKeys.Count == 0)
            && (teams.Count == 0 || value.ParticipantKeys.Any(teamPeople.Contains))
            && (ticket.Length == 0 || value.Tickets.Any(reference => reference.Key == ticket)) && (site.Length == 0 || value.SiteId == site)).ToArray();
    }
    private async Task<object?> CalendarGetAsync(JsonElement a, bool compact, CancellationToken ct)
    {
        var view = await CalendarViewAsync(a, ct).ConfigureAwait(false); var summary = InterventionCalendarService.Summarize(view.Visible);
        var due = view.Visible.Where(value => value.ReminderMinutes.HasValue && value.State is not ("cancelled" or "done")
            && DateTimeOffset.UtcNow >= value.StartUtc.AddMinutes(-value.ReminderMinutes.Value) && DateTimeOffset.UtcNow < value.EndUtc).Select(value => value.Key).ToArray();
        LoadLibrarySyncRecord();
        var sync = new { enabled = LibrarySettings.SharingEnabled, mode = LibrarySettings.SyncMode, simulated = IsSimulationInstance, state = _librarySyncState,
            pending = await TeamWorkspace.CalendarPendingCountAsync(ct).ConfigureAwait(false), conflicts = await TeamWorkspace.CalendarConflictGroupCountAsync(ct).ConfigureAwait(false),
            _librarySyncRecord.LastSuccess, nextSync = LibrarySettings.SharingEnabled && LibrarySettings.AutoSync ? _nextLibrarySync ?? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            label = LibrarySettings.SharingEnabled ? _librarySyncLabel : "Planning enregistré sur ce poste" };
        if (compact) return new { summary, upcoming = view.Visible.Where(value => value.EndUtc >= DateTimeOffset.UtcNow && value.State != "cancelled").Take(12),
            issues = view.Issues.Take(20), personalTechnician = Settings.Dashboard.PersonalTechnician, timeZoneId = view.Window.Zone,
            identityRequired = Text(a, "scope", 20, "all") == "mine" && Settings.Dashboard.PersonalTechnician.Length == 0, sync };
        var resourceDocuments = await TeamWorkspace.CalendarResourcesAsync(ct).ConfigureAwait(false);
        var stored = resourceDocuments.ToDictionary(item => CalendarWorkspaceDocuments.ReadResource(item).Key, StringComparer.Ordinal);
        var catalog = CalendarCatalog(ct); var teams = CalendarTeams(catalog).ToList();
        foreach (var key in view.Resources.SelectMany(resource => resource.TeamKeys).Distinct(StringComparer.Ordinal))
            if (teams.All(team => team.Key != key)) teams.Add(new(key, key.StartsWith("group:", StringComparison.Ordinal) ? key[6..] : key, Array.Empty<string>()));
        var keys = view.All.Where(value => value.State is not ("cancelled" or "done")).SelectMany(value => value.Tickets).Select(ticket => ticket.Key).ToHashSet(StringComparer.Ordinal);
        var selectedPeople = MapWorkspaceStrings(a, "technicians", 200, 400).ToHashSet(StringComparer.Ordinal);
        if (Text(a, "scope", 20, "all") == "mine") selectedPeople = new(StringComparer.Ordinal) { Settings.Dashboard.PersonalTechnician };
        var selectedTeams = MapWorkspaceStrings(a, "teams", 100, 1200).ToHashSet(StringComparer.Ordinal);
        var backlog = catalog.Where(ticket => ticket.Active && !keys.Contains(ticket.Key)
            && (selectedPeople.Count == 0 || ticket.ParticipantKeys.Any(selectedPeople.Contains) || selectedPeople.Contains("@unassigned") && ticket.ParticipantKeys.Length == 0)
            && (selectedTeams.Count == 0 || ticket.TeamKeys.Any(selectedTeams.Contains))).ToArray();
        var reference = EffectiveSettings.Cities;
        var sites = reference.Sites.Select(site => new { site.Id, label = site.Name, site.Name, site.CityId,
            city = reference.Cities.FirstOrDefault(city => city.Id == site.CityId)?.Name ?? "", site.Address, site.Latitude, site.Longitude, site.Contact, site.Equipment, site.Openings, site.ClosedDates }).ToArray();
        return new { from = view.Window.FromDate, to = view.Window.ToDate, timeZoneId = view.Window.Zone, sourceRevision = Revision, revision = Revision,
            sourceLoaded = Result is not null, personalTechnician = Settings.Dashboard.PersonalTechnician, scope = Text(a, "scope", 20, "all"),
            identityRequired = Text(a, "scope", 20, "all") == "mine" && Settings.Dashboard.PersonalTechnician.Length == 0,
            occurrences = view.Visible, summary, issues = view.Issues, dueReminders = due, sync, sites, teams,
            cities = reference.Cities.Select(city => new { city.Id, label = city.Name, city.Name, city.PostalCode, city.Latitude, city.Longitude }),
            resources = view.Resources.Select(resource => new { resource.Key, resource.Label, resource.TeamKeys, resource.TimeZoneId, resource.Active, resource.WorkPeriods, resource.CapacityDays,
                id = CalendarWorkspaceDocuments.ResourceId(resource.Key), revision = stored.GetValueOrDefault(resource.Key)?.Revision ?? 0,
                saved = stored.ContainsKey(resource.Key), capacityKnown = resource.WorkPeriods.Count > 0 || resource.CapacityDays.Count > 0 }),
            backlog = backlog.Take(100), backlogTotal = backlog.Length,
            coverage = new { from = view.Window.FromDate, to = view.Window.ToDate, resourceScope = "Les conflits sont calculés sur tous les rendez-vous connus, avant le filtrage de la vue.",
                tickets = Result is null ? "Aucun relevé chargé ; les interventions libres et enregistrées restent disponibles." : "Les liens restent enregistrés lorsque les tickets quittent le relevé courant." } };
    }
    private IReadOnlyList<CalendarConflict> CalendarIssues(IReadOnlyList<CalendarOccurrence> values, IReadOnlyList<CalendarResource> resources, CancellationToken ct)
        => InterventionCalendarService.Conflicts(values, resources, ct).Concat(CalendarExtraIssues(values, CalendarExtraSnapshot(resources, ct), ct)).ToArray();
    private sealed record CalendarExtraData(IReadOnlyDictionary<string, SiteDefinition> Sites, TechnicianPreferences Preferences,
        IReadOnlyDictionary<string, CalendarResource> Resources, IReadOnlyDictionary<string, DateTimeOffset> Due);
    private CalendarExtraData CalendarExtraSnapshot(IReadOnlyList<CalendarResource> resources, CancellationToken ct)
    {
        var settings = EffectiveSettings; var dates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (Result is { } source)
            foreach (var ticket in CalendarCatalog(ct))
                if (ticket.Due.Length > 0 && DashboardService.TrySourceDate(ticket.Due, settings.Dashboard, source.ExtractedAt.Offset, out var due))
                    dates[ticket.Key] = new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Unspecified), source.ExtractedAt.Offset).ToUniversalTime();
        return new(settings.Cities.Sites.ToDictionary(site => site.Id, StringComparer.Ordinal), WorkTechnicianPreferences(),
            resources.ToDictionary(resource => resource.Key, StringComparer.Ordinal), dates);
    }
    private static IReadOnlyList<CalendarConflict> CalendarExtraIssues(IReadOnlyList<CalendarOccurrence> values, CalendarExtraData extra, CancellationToken ct)
    {
        var issues = new List<CalendarConflict>(); var sites = extra.Sites; var preferences = extra.Preferences;
        foreach (var value in values.Where(value => value.State != "cancelled" && value.Kind != "unavailability"))
        {
            ct.ThrowIfCancellationRequested();
            var late = value.Tickets.Where(ticket => extra.Due.TryGetValue(ticket.Key, out var due) && value.EndUtc > due).Select(ticket => ticket.Id).ToArray();
            if (late.Length > 0) issues.Add(new("ticket-due", "warning", "La fin prévue dépasse l’échéance lisible du relevé pour le(s) ticket(s) " + string.Join(", ", late.Take(10)) + ".", "", new[] { value.Key }));
            if (value.RequiredSkills.Count > 0 && value.ParticipantKeys.Count > 0)
            {
                var declared = value.ParticipantKeys.Select(key => preferences.Get(key).Skills).ToArray(); var known = declared.SelectMany(skills => skills).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = value.RequiredSkills.Where(skill => !known.Contains(skill)).ToArray();
                if (missing.Length > 0) issues.Add(new("skills", declared.Any(skills => skills.Count == 0) ? "info" : "warning",
                    "Compétences requises non confirmées pour les participants : " + string.Join(", ", missing) + ".", "", new[] { value.Key }));
            }
            foreach (var key in value.ParticipantKeys)
            {
                var preference = preferences.Get(key);
                var zone = InterventionCalendarService.Zone(extra.Resources.GetValueOrDefault(key)?.TimeZoneId ?? value.TimeZoneId);
                var from = TimeZoneInfo.ConvertTime(value.StartUtc, zone).DateTime; var to = TimeZoneInfo.ConvertTime(value.EndUtc, zone).DateTime;
                for (var day = from.Date; day < to; day = day.AddDays(1))
                    if (TechnicianPlanningService.IsUnavailable(preference, day))
                    { issues.Add(new("declared-absence", "warning", "Une indisponibilité est déclarée sur ce poste pour " + (extra.Resources.GetValueOrDefault(key)?.Label ?? key) + " le " + day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ".", key, new[] { value.Key })); break; }
            }
        }
        foreach (var person in values.Where(value => value.State != "cancelled" && value.Kind != "unavailability").SelectMany(value => value.ParticipantKeys.Select(key => (Key: key, Value: value))).GroupBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var preference = preferences.Get(person.Key); if (!preference.DailyCapacityMinutes.HasValue || preference.WorkPeriods.Count > 0) continue;
            var zone = InterventionCalendarService.Zone(extra.Resources.GetValueOrDefault(person.Key)?.TimeZoneId ?? TimeZoneInfo.Local.Id);
            var entries = person.Select(pair => (Value: pair.Value, From: TimeZoneInfo.ConvertTime(pair.Value.StartUtc.AddMinutes(-pair.Value.TravelBeforeMinutes), zone).DateTime,
                To: TimeZoneInfo.ConvertTime(pair.Value.EndUtc.AddMinutes(pair.Value.TravelAfterMinutes), zone).DateTime)).ToArray();
            var first = entries.Min(entry => entry.From).Date; var last = entries.Max(entry => entry.To);
            for (var day = first; day < last; day = day.AddDays(1))
            {
                var affected = entries.Where(entry => entry.To > day && entry.From < day.AddDays(1)).ToArray();
                var reserved = affected.Sum(entry => ((entry.To < day.AddDays(1) ? entry.To : day.AddDays(1)) - (entry.From > day ? entry.From : day)).TotalMinutes);
                if (reserved > preference.DailyCapacityMinutes) issues.Add(new("declared-daily-capacity", "warning", "Le temps réservé dépasse la capacité journalière déclarée sur ce poste pour " + person.Key + " le " + day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ".", person.Key, affected.Select(entry => entry.Value.Key).ToArray()));
            }
        }
        foreach (var value in values.Where(value => !value.Remote && value.SiteId.Length > 0 && value.State != "cancelled"))
        {
            ct.ThrowIfCancellationRequested();
            if (!sites.TryGetValue(value.SiteId, out var site)) { issues.Add(new("site-missing", "warning", "Le site de cette intervention n’existe plus dans le référentiel actuel ; son adresse enregistrée est conservée.", "", new[] { value.Key })); continue; }
            var local = InterventionCalendarService.ParseLocal(value.StartLocal); var finish = InterventionCalendarService.ParseLocal(value.EndLocal);
            if (site.ClosedDates.Contains(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparer.Ordinal)
                || site.Openings.Count > 0 && !site.Openings.Any(opening => opening.Day == local.DayOfWeek && local.Date == finish.AddTicks(-1).Date
                    && TimeOnly.FromDateTime(local) >= TimeOnly.ParseExact(opening.From, "HH:mm", CultureInfo.InvariantCulture)
                    && finish <= local.Date.Add(TimeOnly.ParseExact(opening.Until, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan())))
                issues.Add(new("site-closed", "warning", "Cette intervention ne respecte pas les ouvertures déclarées du site « " + site.Name + " ».", "", new[] { value.Key }) { StartUtc = value.StartUtc, EndUtc = value.EndUtc });
        }
        return issues;
    }
    private async Task CalendarAutomaticRemindersAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (IsSimulationInstance || SessionLocked || EditorOpen || string.IsNullOrWhiteSpace(Settings.Dashboard.PersonalTechnician)
            || AutomationPolicy.IsQuiet(Settings.Automation, DateTimeOffset.Now)
            || _nextCalendarReminderCheck is { } next && now < next) return;
        _nextCalendarReminderCheck = now.AddMinutes(1);
        await CalendarRemindersAsync(JsonSerializer.SerializeToElement(new { scope = "mine", notify = true }, Json), ct).ConfigureAwait(false);
    }
    private async Task<object?> CalendarRemindersAsync(JsonElement a, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; var scope = Text(a, "scope", 20, "mine");
        if (scope is not ("mine" or "all")) throw new InvalidDataException("Périmètre des rappels inconnu.");
        var range = CalendarRange(JsonSerializer.SerializeToElement(new { from = now.AddDays(-1).ToString("yyyy-MM-dd"), to = now.AddDays(32).ToString("yyyy-MM-dd") }, Json));
        // A reminder can precede its booking by thirty days. Expand only documents
        // carrying reminders; the background tick does not need
        // the complete ticket catalogue, tour calculations or conflict analysis.
        var entries = scope == "mine" && string.IsNullOrWhiteSpace(Settings.Dashboard.PersonalTechnician) ? Array.Empty<CalendarIntervention>()
            : (await CalendarDocumentsAsync(range, ct).ConfigureAwait(false)).Select(CalendarWorkspaceDocuments.ReadIntervention)
                .Where(value => value.ReminderMinutes.HasValue || value.Exceptions.Any(exception => exception.Replacement?.ReminderMinutes.HasValue == true)).ToArray();
        var due = InterventionCalendarService.ExpandDetailed(entries, range.From, range.To, ct).Occurrences
            .Where(value => value.ReminderMinutes.HasValue && value.State is not ("done" or "cancelled")
                && (scope == "all" || value.ParticipantKeys.Contains(Settings.Dashboard.PersonalTechnician, StringComparer.Ordinal))
                && now >= value.StartUtc.AddMinutes(-value.ReminderMinutes.Value) && now < value.EndUtc).ToArray();
        foreach (var value in due)
        {
            var key = value.Key + ":" + value.Revision + ":" + value.StartUtc.ToString("O");
            if (Flag(a, "notify") && !IsSimulationInstance && !_calendarReminderNotified.Contains(key)
                && await Host.NotifyAsync("Intervention à préparer", value.Title + " · " + value.StartLocal.Replace('T', ' ')).ConfigureAwait(false))
                _calendarReminderNotified.Add(key);
        }
        if (_calendarReminderNotified.Count > 5000) _calendarReminderNotified.Clear();
        Publish("calendar.reminders.due", new { count = due.Length, keys = due.Select(value => value.Key) });
        return new { items = due, count = due.Length, due = due.Length > 0 };
    }
    private async Task<object?> CalendarExportAsync(JsonElement a, CancellationToken ct)
    {
        var view = await CalendarViewAsync(a, ct).ConfigureAwait(false);
        var format = Text(a, "format", 10, "ics"); if (format is not ("ics" or "html")) throw new InvalidDataException("Format d’export du planning inconnu.");
        var labels = view.Resources.ToDictionary(resource => resource.Key, resource => resource.Label, StringComparer.Ordinal);
        var export = view.Visible.Select(value =>
        {
            var copy = JsonSerializer.Deserialize<CalendarOccurrence>(JsonSerializer.Serialize(value, Json), Json)!;
            // Display-only copies leave canonical participant keys and occurrence UIDs unchanged in the store and UI.
            copy.ParticipantKeys = value.ParticipantKeys.Select(key => labels.GetValueOrDefault(key, key)).ToList(); return copy;
        }).ToArray();
        var text = format == "ics" ? InterventionCalendarService.ExportIcs(export) : InterventionCalendarService.ExportHtml(export);
        var path = await Host.SaveFileAsync("Exporter le planning des interventions", format == "ics" ? "Calendrier iCalendar|*.ics" : "Fiche imprimable HTML|*.html", "GLPI-Pilot-interventions-" + view.Window.FromDate + "." + format).ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        var full = Path.GetFullPath(path); var database = Path.GetFullPath(LibrarySettings.DatabasePath);
        if (new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            throw new InvalidDataException("Le calendrier ne peut pas remplacer la base active.");
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { cancelled = false, path, count = view.Visible.Count };
    }
    private async Task<object?> CalendarForTicketAsync(JsonElement a, CancellationToken ct)
    {
        var key = Text(a, "ticketKey", 300); if (key.Length == 0) throw new InvalidDataException("Référence du ticket absente.");
        var today = DateTime.Today;
        var args = JsonSerializer.SerializeToElement(new { ticketKey = key, from = Text(a, "from", 10, today.AddDays(-365).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            to = Text(a, "to", 10, today.AddDays(366).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), scope = "all" }, Json);
        var view = await CalendarViewAsync(args, ct, 732).ConfigureAwait(false); var now = DateTimeOffset.UtcNow;
        return new { upcoming = view.Visible.Where(value => value.EndUtc >= now && value.State != "cancelled").Take(8),
            past = view.Visible.Where(value => value.EndUtc < now || value.State is "done" or "cancelled").OrderByDescending(value => value.StartUtc).Take(5),
            total = view.Visible.Count, from = view.Window.FromDate, to = view.Window.ToDate, linkedInterventions = view.Visible.Select(value => value.InterventionId).Distinct(StringComparer.Ordinal).Count() };
    }
}
