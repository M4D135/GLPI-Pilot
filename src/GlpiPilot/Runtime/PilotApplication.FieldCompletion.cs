using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed class FieldSourceUnavailable(string reason, string message) : InvalidOperationException(message)
    { public string Reason { get; } = reason; }
    private sealed record FieldSources(Dictionary<string, CalendarTicketRow> Tickets,
        Dictionary<string, CalendarIntervention[]> Series, HashSet<string> ConflictedSeries,
        Dictionary<string, ScheduledTour> Tours, Dictionary<string, string> People)
    { public Dictionary<string, CalendarOccurrence> TourVisits { get; } = new(StringComparer.Ordinal); }
    private sealed record FieldObservation(FieldPreparationDocument Document, string Status, string Message, bool Verified);

    private void RegisterFieldCompletionOperations()
    {
        Register("field.options", FieldOptionsAsync);
        Register("field.kits", FieldKitsAsync);
        Register("field.conflict.resolve", async (a, ct) =>
        {
            if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez le choix après avoir comparé les deux versions.");
            var saved = await TeamWorkspace.ResolveFieldConflictAsync(Text(a, "id", 32), WorkspaceExpectedRevision(a, true)!.Value,
                Text(a, "conflictId", 64), Text(a, "choice", 20), CalendarAuthor, ct).ConfigureAwait(false);
            Publish("field.changed", new { id = saved.Id }); Publish("library.changed", new { team = true });
            return new { saved = true, saved.Id, saved.Revision, saved.Conflicts, saved.Pending };
        });
    }

    private async Task<FieldSources> FieldSourcesAsync(CancellationToken ct)
    {
        var tickets = CalendarCatalog(ct).ToDictionary(value => value.Key, StringComparer.Ordinal);
        var documents = await TeamWorkspace.FieldCalendarDocumentsAsync(ct).ConfigureAwait(false);
        var values = documents.Select(item => (Item: item, Value: CalendarWorkspaceDocuments.ReadIntervention(item))).ToArray();
        static string Root(CalendarIntervention entry) => entry.SeriesId.Length > 0 ? entry.SeriesId : entry.Id;
        var people = await IdentityPeopleAsync(ct).ConfigureAwait(false);
        return new(tickets, values.GroupBy(row => Root(row.Value), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(row => row.Value).ToArray(), StringComparer.Ordinal),
            values.Where(row => row.Item.Conflicts > 0).Select(row => Root(row.Value)).ToHashSet(StringComparer.Ordinal),
            ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours.ToDictionary(tour => tour.Id, StringComparer.Ordinal),
            people.People.ToDictionary(person => person.Key, person => person.Label, StringComparer.Ordinal));
    }
    private CalendarOccurrence FieldOccurrence(string key, FieldSources sources, CancellationToken ct)
    {
        if (key.StartsWith("tour:", StringComparison.Ordinal))
        {
            if (sources.TourVisits.TryGetValue(key, out var cached)) return cached;
            var parts = key.Split(':');
            if (parts.Length != 4 || parts[2] != "stop" || !sources.Tours.TryGetValue(parts[1], out var tour))
                throw new FieldSourceUnavailable("missing", "La tournée ou sa visite a été retirée. La fiche reste archivée, mais son matériel n’est plus totalisé pour le départ.");
            var stop = tour.Stops.SingleOrDefault(value => value.Id == parts[3]);
            if (stop is null) throw new FieldSourceUnavailable("missing", "Cette visite a été retirée de la tournée.");
            var date = CalendarDate(stop.Date.Length > 0 ? stop.Date : tour.Date);
            var args = JsonSerializer.SerializeToElement(new { from = date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to = date.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZoneId = tour.TimeZoneId }, Json);
            foreach (var occurrence in CalendarTours(CalendarRange(args), ct)) sources.TourVisits[occurrence.Key] = occurrence;
            return sources.TourVisits.GetValueOrDefault(key)
                ?? throw new FieldSourceUnavailable("unverified", "L’horaire de la visite ne peut pas être confirmé. Vérifiez la tournée avant de préparer le départ.");
        }
        var separator = key.LastIndexOf(':'); var root = separator > 0 ? key[..separator] : "";
        if (sources.ConflictedSeries.Contains(root)) throw new FieldSourceUnavailable("conflict", "Le planning possède des versions concurrentes pour cette visite. Résolvez-les avant de préparer le départ.");
        if (!sources.Series.TryGetValue(root, out var series)) throw new FieldSourceUnavailable("missing", "L’intervention a été supprimée du planning. Sa fiche est conservée, mais exclue des besoins à emporter.");
        try
        {
            return InterventionCalendarService.PreparationOccurrence(series, key, ct)
                ?? throw new FieldSourceUnavailable("missing", "Cette occurrence a été retirée de la série. Sa préparation reste conservée sans être comptée pour le départ.");
        }
        catch (InvalidDataException error) { throw new FieldSourceUnavailable("unverified", error.Message); }
    }
    private static JsonElement FieldSourceRequest(FieldPreparationPlan plan)
        => JsonSerializer.SerializeToElement(plan.Owner.Kind == "ticket" ? new { ticketKey = plan.Owner.Key, occurrenceKey = "" }
            : new { ticketKey = "", occurrenceKey = plan.Owner.Key }, Json);

    private async Task<FieldPreparationDocument> FieldSourceAsync(JsonElement a, CancellationToken ct, FieldSources? sources = null)
    {
        var ticketKey = Text(a, "ticketKey", 300); var occurrenceKey = Text(a, "occurrenceKey", 300);
        if ((ticketKey.Length > 0) == (occurrenceKey.Length > 0)) throw new InvalidDataException("Choisissez exactement un ticket ou une visite.");
        sources ??= await FieldSourcesAsync(ct).ConfigureAwait(false);
        FieldPreparationPlan plan; List<FieldTicketDetail> tickets; long sourceRevision = 0;
        if (ticketKey.Length > 0)
        {
            if (IsHistorical || !sources.Tickets.TryGetValue(ticketKey, out var ticket))
                throw new FieldSourceUnavailable("unverified", "Le ticket est absent du relevé courant ou le relevé est archivé. Cela ne prouve pas sa suppression. Son contexte enregistré reste consultable, hors des totaux de départ vérifiés.");
            plan = FieldPreparationService.NewPlan(new("ticket", ticket.Key), FieldShort(ticket.Title.Length > 0 ? ticket.Title : "Ticket " + ticket.Id, 300));
            plan.SiteId = ticket.SiteId; plan.SiteName = FieldShort(ticket.Location, 200); plan.Address = FieldShort(ticket.Address, 1000);
            plan.ParticipantKeys = ticket.ParticipantKeys.Distinct(StringComparer.Ordinal).ToList();
            plan.Contact = FieldShort(EffectiveSettings.Cities.Sites.FirstOrDefault(site => site.Id == ticket.SiteId)?.Contact, 1000);
            plan.State = ticket.Active ? "planned" : "done";
            tickets = new() { FieldTicketSnapshot(ticket) };
        }
        else
        {
            var occurrence = FieldOccurrence(occurrenceKey, sources, ct);
            if (occurrence.Kind == "unavailability") throw new FieldSourceUnavailable("missing", "Cette entrée est devenue une indisponibilité, pas une intervention à préparer.");
            plan = FieldPreparationService.NewPlan(new("intervention", occurrence.Key), FieldShort(occurrence.Title, 300));
            plan.SiteId = occurrence.SiteId; plan.SiteName = FieldShort(occurrence.Location, 200); plan.Address = FieldShort(occurrence.Address, 1000);
            plan.Contact = FieldShort(occurrence.Contact, 1000); plan.PlannedStart = occurrence.StartUtc; plan.PlannedEnd = occurrence.EndUtc;
            plan.ParticipantKeys = occurrence.ParticipantKeys.Distinct(StringComparer.Ordinal).ToList();
            if (occurrence.TourId.Length > 0) plan.TourIds.Add(occurrence.TourId);
            plan.State = occurrence.State is "done" or "cancelled" ? occurrence.State : "planned"; sourceRevision = occurrence.Revision;
            tickets = occurrence.Tickets.Select(ticket => sources.Tickets.TryGetValue(ticket.Key, out var current) && !IsHistorical ? FieldTicketSnapshot(current)
                : new FieldTicketDetail(ticket.Key, ticket.Id, ticket.Title)).ToList();
        }
        var document = new FieldPreparationDocument { Plan = plan, Tickets = tickets, ObservedAtUtc = DateTimeOffset.UtcNow,
            ParticipantLabels = plan.ParticipantKeys.Select(key => FieldShort(sources.People.GetValueOrDefault(key, key), 400)).ToList() };
        document.SourceStamp = TerrainDigest(new { plan, tickets, document.ParticipantLabels, sourceRevision });
        FieldPreparationDocuments.Validate(document);
        if (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document, Json)) > FieldPreparationDocuments.MaximumPayloadBytes)
            throw new InvalidDataException("Les détails de la fiche dépassent 2 Mio. Réduisez les tickets liés ou les descriptions avant d’ouvrir cette préparation.");
        return document;
    }
    private async Task<FieldObservation> FieldObserveAsync(FieldPreparationDocument recorded, CancellationToken ct, FieldSources? sources = null)
    {
        try
        {
            var current = await FieldSourceAsync(FieldSourceRequest(recorded.Plan), ct, sources).ConfigureAwait(false);
            current.Plan.Lines = FieldPreparationService.Copy(recorded.Plan.Lines); current.Plan.NoMaterialsRequired = recorded.Plan.NoMaterialsRequired;
            current.Plan.PreparationNote = recorded.Plan.PreparationNote; current.ResolvedConflictKeys = recorded.ResolvedConflictKeys.ToList();
            // Missing detailed descriptions never erase a previously collected description on a read projection.
            current.Tickets = current.Tickets.Select(ticket => ticket.Description.Length == 0 && recorded.Tickets.FirstOrDefault(old => old.Key == ticket.Key) is { Description.Length: > 0 } old
                ? ticket with { Description = old.Description, ObservedAt = old.ObservedAt } : ticket).ToList();
            var changed = current.SourceStamp != recorded.SourceStamp;
            return new(current, changed ? "updated" : "current", changed
                ? "Dates, participants et état rapprochés du planning ou du relevé connu de ce poste. Le matériel enregistré est conservé ; aucune écriture GLPI."
                : "Contexte rapproché des données actuellement connues sur ce poste ; ce contrôle n’est pas une collecte GLPI.", true);
        }
        catch (FieldSourceUnavailable error) { return new(FieldPreparationService.Copy(recorded), error.Reason, error.Message, false); }
    }

    private async Task<object?> FieldOptionsAsync(JsonElement a, CancellationToken ct)
    {
        var sources = await FieldSourcesAsync(ct).ConfigureAwait(false);
        var stored = (await TeamWorkspace.FieldPreparationsAsync(ct).ConfigureAwait(false)).Select(FieldPreparationDocuments.ReadPlan).ToArray();
        var sites = EffectiveSettings.Cities.Sites.Select(site => new { id = site.Id, label = site.Name })
            .Concat(stored.Where(doc => doc.Plan.SiteId.Length > 0).Select(doc => new { id = doc.Plan.SiteId, label = doc.Plan.SiteName }))
            .GroupBy(site => site.id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(site => site.label).ToArray();
        return new { sites, technicians = sources.People.Select(person => new { id = person.Key, label = person.Value }).OrderBy(person => person.label),
            teams = CalendarTeams(sources.Tickets.Values.ToArray()).Select(team => new { id = team.Key, label = team.Label }),
            tours = sources.Tours.Values.Select(tour => new { id = tour.Id, label = tour.Name }), revision = Revision };
    }
    private async Task<object?> FieldGetAsync(JsonElement a, CancellationToken ct)
    {
        var scope = Text(a, "scope", 20, "all"); if (scope is not ("all" or "mine")) throw new InvalidDataException("Périmètre inconnu.");
        var search = Text(a, "search", 300); var site = Text(a, "siteId", 80); var tour = Text(a, "tourId", 100); var team = Text(a, "teamKey", 1200);
        var person = scope == "mine" ? Settings.Dashboard.PersonalTechnician : Text(a, "technicianKey", 400);
        var readiness = Text(a, "readiness", 30); var includeClosed = Flag(a, "includeClosed"); var includeUnscheduled = Flag(a, "includeUnscheduled", true);
        var sources = await FieldSourcesAsync(ct).ConfigureAwait(false);
        var teamPeople = (await TeamWorkspace.CalendarResourcesAsync(ct).ConfigureAwait(false)).Select(CalendarWorkspaceDocuments.ReadResource)
            .Where(resource => resource.TeamKeys.Contains(team, StringComparer.Ordinal)).Select(resource => resource.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var ticket in sources.Tickets.Values.Where(ticket => ticket.TeamKeys.Contains(team, StringComparer.Ordinal))) foreach (var key in ticket.ParticipantKeys) teamPeople.Add(key);
        CalendarWindow? range = a.TryGetProperty("from", out _) || a.TryGetProperty("to", out _) ? CalendarRange(a) : null;
        var tourTickets = new HashSet<string>(StringComparer.Ordinal);
        if (tour.Length > 0 && sources.Tours.TryGetValue(tour, out var selectedTour))
            foreach (var stop in selectedTour.Stops)
                foreach (var ticket in FieldOccurrence("tour:" + tour + ":stop:" + stop.Id, sources, ct).Tickets) tourTickets.Add(ticket.Key);
        var all = new List<(TeamWorkspaceItem Item, FieldPreparationDocument Document, FieldObservation Observation)>();
        foreach (var item in await TeamWorkspace.FieldPreparationsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested(); var recorded = FieldPreparationDocuments.ReadPlan(item);
            var observation = await FieldObserveAsync(recorded, ct, sources).ConfigureAwait(false); all.Add((item, observation.Document, observation));
        }
        var selected = all.Where(row => !(scope == "mine" && person.Length == 0)
            && (person.Length == 0 || row.Document.Plan.ParticipantKeys.Contains(person, StringComparer.Ordinal))
            && (team.Length == 0 || row.Document.Plan.ParticipantKeys.Any(teamPeople.Contains))
            && (site.Length == 0 || row.Document.Plan.SiteId == site) && (tour.Length == 0 || row.Document.Plan.TourIds.Contains(tour, StringComparer.Ordinal)
                || row.Document.Plan.Owner.Kind == "ticket" && tourTickets.Contains(row.Document.Plan.Owner.Key))
            && (includeClosed || row.Document.Plan.State == "planned")
            && (readiness.Length == 0 || FieldPreparationService.Readiness(row.Document.Plan) == readiness)
            && (range is null || row.Document.Plan.PlannedStart is null && includeUnscheduled || row.Document.Plan.PlannedStart < range.To && (row.Document.Plan.PlannedEnd ?? row.Document.Plan.PlannedStart) > range.From)
            && (search.Length == 0 || TicketReadingService.Contains(row.Document.Plan.Title + " " + row.Document.Plan.SiteName + " " + string.Join(" ", row.Document.Tickets.Select(ticket => ticket.Id)) + " " + string.Join(" ", row.Document.Plan.Lines.Select(line => line.Label + " " + line.Code)), search)))
            .OrderBy(row => row.Document.Plan.PlannedStart).ThenBy(row => row.Item.Id, StringComparer.Ordinal).ToArray();
        var visited = selected.Where(row => row.Document.Plan.Owner.Kind == "intervention" && row.Document.Plan.State == "planned" && row.Observation.Verified)
            .SelectMany(row => row.Document.Tickets).Select(ticket => ticket.Key).ToHashSet(StringComparer.Ordinal);
        bool Overlap(FieldPreparationDocument doc) => doc.Plan.Owner.Kind == "ticket" && visited.Contains(doc.Plan.Owner.Key);
        bool Excluded((TeamWorkspaceItem Item, FieldPreparationDocument Document, FieldObservation Observation) row) => row.Item.Conflicts > 0 || !row.Observation.Verified;
        var summary = FieldPreparationService.Summarize(selected.Where(row => !Excluded(row)).Select(row => row.Document.Plan));
        var warnings = summary.Warnings.ToList();
        if (selected.Any(row => row.Item.Conflicts > 0)) warnings.Add("Les préparations en conflit sont exclues des totaux. Comparez leurs versions pour les réintégrer.");
        if (selected.Any(row => !row.Observation.Verified)) warnings.Add("Les fiches dont la source a disparu ou reste non vérifiée sont conservées mais exclues des besoins de départ vérifiés.");
        if (selected.Any(row => Overlap(row.Document))) warnings.Add("Des préparations de tickets et des besoins complémentaires de visite coexistent. Chaque fiche est comptée une seule fois ; vérifiez les éventuelles lignes saisies deux fois avant le départ.");
        summary = summary with { Warnings = warnings };
        var offset = Number(a, "offset", 0, 0, 1000000); var limit = Number(a, "limit", 50, 1, 200);
        if (Flag(a, "selectAll") && selected.Length > 200) throw new InvalidDataException("Plus de 200 préparations correspondent. Affinez les filtres avant de sélectionner toutes les fiches.");
        LoadLibrarySyncRecord();
        return new { revision = Revision, total = selected.Length, offset, limit, summary, identityRequired = scope == "mine" && person.Length == 0,
            selections = Flag(a, "selectAll") ? selected.Where(row => row.Item.Conflicts == 0).Select(row => new { id = row.Item.Id, expectedRevision = row.Item.Revision }).ToArray() : null,
            rows = selected.Skip(offset).Take(limit).Select(row => new { row.Item.Id, row.Item.Revision, row.Item.Pending, row.Item.Conflicts, row.Item.UpdatedAt,
                row.Document.Plan.Title, row.Document.Plan.Owner, row.Document.Plan.SiteName, row.Document.Plan.PlannedStart, row.Document.Plan.PlannedEnd,
                row.Document.Plan.ParticipantKeys, row.Document.ParticipantLabels, row.Document.Plan.TourIds, row.Document.ObservedAtUtc,
                sourceStatus = row.Observation.Status, sourceMessage = row.Observation.Message, row.Document.SourceStamp,
                readiness = FieldPreparationService.Readiness(row.Document.Plan), lines = row.Document.Plan.Lines.Count, excludedFromTotals = Excluded(row) }),
            coverage = "Besoins des préparations enregistrées, rapprochés du planning et du relevé connus sur ce poste. Les sources absentes ou en conflit sont exclues. Les visites sans préparation ne sont pas assimilées à aucun matériel.",
            sync = new { enabled = LibrarySettings.SharingEnabled, simulated = IsSimulationInstance, mode = LibrarySettings.SyncMode,
                state = _librarySyncState, lastSuccess = _librarySyncRecord.LastSuccess, pending = all.Count(row => row.Item.Pending), conflicts = all.Count(row => row.Item.Conflicts > 0),
                label = LibrarySettings.SharingEnabled ? _librarySyncLabel : "Préparations enregistrées sur ce poste" } };
    }

    private async Task<List<FieldPreparationDocument>> FieldTourDocumentsAsync(string tourId, FieldSources sources, bool archive, HashSet<string> excluded, CancellationToken ct)
    {
        if (!sources.Tours.TryGetValue(tourId, out var tour)) throw new InvalidDataException("La tournée sélectionnée n’existe plus.");
        if (tour.Stops.Count is < 1 or > FieldPreparationReport.MaximumSheets) throw new InvalidDataException("Une impression accepte de 1 à 200 visites ; aucune tournée n’est tronquée silencieusement.");
        var documents = new List<FieldPreparationDocument>(); var tickets = new HashSet<string>(StringComparer.Ordinal); long bytes = 0;
        void Add(FieldPreparationDocument document)
        {
            bytes += System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document, Json));
            if (bytes > 12 * 1024 * 1024 || documents.Count >= FieldPreparationReport.MaximumSheets)
                throw new InvalidDataException("La tournée et ses fiches de tickets dépassent 200 fiches ou 12 Mio. Utilisez une sélection plus petite ; aucune fiche n’est omise silencieusement.");
            documents.Add(document);
        }
        foreach (var stop in tour.Stops)
        {
            var current = await FieldSourceAsync(JsonSerializer.SerializeToElement(new { occurrenceKey = "tour:" + tour.Id + ":stop:" + stop.Id }, Json), ct, sources).ConfigureAwait(false);
            foreach (var ticket in current.Tickets) tickets.Add(ticket.Key);
            var saved = await TeamWorkspace.GetAsync(current.Plan.Id, ct).ConfigureAwait(false);
            if (saved is { Conflicts: > 0 } || saved is { Deleted: true }) throw new InvalidOperationException("Une visite possède des versions à examiner ; aucune impression partielle de la tournée n’est produite.");
            if (saved is not null)
            {
                var observation = await FieldObserveAsync(FieldPreparationDocuments.ReadPlan(saved), ct, sources).ConfigureAwait(false);
                if (!observation.Verified) throw new InvalidOperationException(observation.Message);
                current = observation.Document;
            }
            else current.Plan.PreparationNote = "Matériel commun à cette visite non renseigné. À renseigner avant le départ ; les préparations de tickets liées figurent séparément lorsqu’elles existent.";
            Add(current);
        }
        foreach (var key in tickets.Order(StringComparer.Ordinal))
        {
            var id = FieldPreparationService.IdFor(new("ticket", key));
            var item = await TeamWorkspace.GetAsync(id, ct).ConfigureAwait(false); if (item is null || item.Deleted) continue;
            if (item.Conflicts > 0) throw new InvalidOperationException("La préparation d’un ticket de la tournée est en conflit. Comparez ses versions avant l’impression.");
            var observation = await FieldObserveAsync(FieldPreparationDocuments.ReadPlan(item), ct, sources).ConfigureAwait(false);
            if (!observation.Verified)
            {
                if (!archive) throw new InvalidOperationException("La tournée contient une préparation de ticket dont la source n’est plus vérifiable. Actualisez le relevé ou choisissez explicitement l’impression des archives non vérifiées.");
                excluded.Add(id);
                observation.Document.Plan.PreparationNote = FieldShort("ARCHIVE NON VÉRIFIÉE : état actuel inconnu. " + observation.Message + "\n" + observation.Document.Plan.PreparationNote, 4000);
            }
            Add(observation.Document);
        }
        return documents;
    }
    private async Task<string> FieldReportAsync(JsonElement a, CancellationToken ct)
    {
        var sources = await FieldSourcesAsync(ct).ConfigureAwait(false); var documents = new List<FieldPreparationDocument>();
        var tour = Text(a, "tourId", 100); var notices = new List<string>(); var excludedDepartureIds = new HashSet<string>(StringComparer.Ordinal);
        if (tour.Length > 0) documents = await FieldTourDocumentsAsync(tour, sources, Flag(a, "archive"), excludedDepartureIds, ct).ConfigureAwait(false);
        else
        {
            if (!a.TryGetProperty("selections", out var selection) || selection.ValueKind != JsonValueKind.Array || selection.GetArrayLength() is < 1 or > FieldPreparationReport.MaximumSheets)
                throw new InvalidDataException("Sélectionnez de 1 à 200 préparations à imprimer.");
            var seen = new HashSet<string>(StringComparer.Ordinal); long bytes = 0;
            foreach (var entry in selection.EnumerateArray())
            {
                var id = Text(entry, "id", 32); if (!seen.Add(id)) throw new InvalidDataException("Une fiche est sélectionnée plusieurs fois.");
                var item = await FieldRequireItemAsync(id, ct).ConfigureAwait(false);
                if (item.Revision != WorkspaceExpectedRevision(entry, true)!.Value || item.Conflicts > 0) throw new InvalidOperationException("Une fiche a changé ou possède des versions en conflit. Actualisez la sélection avant d’imprimer.");
                bytes += System.Text.Encoding.UTF8.GetByteCount(item.Payload!.Value.GetRawText());
                if (bytes > 12 * 1024 * 1024) throw new InvalidDataException("Les fiches sont trop volumineuses. Imprimez-les en plusieurs lots.");
                var observation = await FieldObserveAsync(FieldPreparationDocuments.ReadPlan(item), ct, sources).ConfigureAwait(false);
                if (!observation.Verified)
                {
                    if (!Flag(a, "archive")) throw new InvalidOperationException("Une source n’est plus vérifiable. Choisissez explicitement l’impression d’archives datées, ou actualisez les données avant le départ.");
                    notices.Add(observation.Message);
                    // An archived, unverified sheet must never inflate a departure checklist.
                    excludedDepartureIds.Add(observation.Document.Plan.Id);
                    observation.Document.Plan.PreparationNote = FieldShort("ARCHIVE NON VÉRIFIÉE : état actuel inconnu. " + observation.Message + "\n" + observation.Document.Plan.PreparationNote, 4000);
                }
                documents.Add(observation.Document);
            }
        }
        var visited = documents.Where(doc => doc.Plan.Owner.Kind == "intervention").SelectMany(doc => doc.Tickets).Select(ticket => ticket.Key).ToHashSet(StringComparer.Ordinal);
        var complementary = documents.Any(doc => doc.Plan.Owner.Kind == "ticket" && visited.Contains(doc.Plan.Owner.Key));
        var title = Text(a, "title", 300, tour.Length > 0 ? "Fiches de tournée" : "Fiches de préparation terrain");
        var html = FieldPreparationReport.Render(documents.Select(doc => doc.Plan), documents.Select(doc => new FieldSheetContext(doc.Plan.Id, doc.Tickets, doc.ParticipantLabels)), title,
            DateTimeOffset.UtcNow, InterventionCalendarService.Zone(Text(a, "timeZoneId", 150, TimeZoneInfo.Local.Id)), excludedDepartureIds);
        var banner = "<p class=\"note\">Contexte rapproché des données connues du poste au moment de l’impression. Ce contrôle ne constitue pas une collecte GLPI ni une synchronisation réseau forcée.</p>";
        if (excludedDepartureIds.Count > 0) banner += "<p class=\"note\">ARCHIVES : des sources sont non vérifiées. Leurs quantités sont exclues de la liste de départ. L’état actuel de ces interventions reste inconnu.</p>";
        if (complementary) banner += "<p class=\"note\">Les préparations de tickets et les besoins complémentaires de visite sont cumulés, chaque fiche une seule fois. Vérifiez les éventuels doublons saisis sur deux fiches distinctes.</p>";
        if (System.Text.Encoding.UTF8.GetByteCount(html) > 16 * 1024 * 1024) throw new InvalidDataException("Le rapport dépasse 16 Mio. Réduisez la sélection avant d’imprimer.");
        var position = html.IndexOf("</header>", StringComparison.Ordinal);
        return position < 0 ? html : html.Insert(position + "</header>".Length, banner);
    }
    private async Task<object?> FieldKitsAsync(JsonElement a, CancellationToken ct)
    {
        var token = Text(a, "token", 64);
        if (!_fieldDrafts.TryGetValue(token, out var draft) || draft.Expires < DateTimeOffset.UtcNow) throw new InvalidOperationException("Rouvrez la préparation pour obtenir des suggestions.");
        var catalogue = await FieldCatalogueAsync(ct).ConfigureAwait(false);
        var activeCategories = catalogue.Catalogue.Categories.Where(category => !category.Archived).Select(category => category.Id).ToHashSet(StringComparer.Ordinal);
        var references = catalogue.Catalogue.References.Where(reference => !reference.Archived && activeCategories.Contains(reference.CategoryId)).ToDictionary(reference => reference.Id, StringComparer.Ordinal);
        var samples = (await TeamWorkspace.FieldPreparationsAsync(ct).ConfigureAwait(false)).Where(item => item.Conflicts == 0 && item.Id != draft.Document.Plan.Id)
            .Select(item => (Item: item, Plan: FieldPreparationDocuments.ReadPlan(item).Plan)).Where(row => row.Plan.Lines.Count > 0)
            .OrderByDescending(row => row.Item.UpdatedAt).Take(100).ToArray();
        var suggestions = samples.Where(row => row.Plan.Lines.All(line => references.TryGetValue(line.ReferenceId, out var reference) && reference.Unit == line.Unit))
            .GroupBy(row => string.Join("|", row.Plan.Lines.OrderBy(line => line.ReferenceId).Select(line => line.ReferenceId + ":" + line.Quantity.ToString(CultureInfo.InvariantCulture))), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).Take(6).Select(group => new
            {
                id = group.First().Plan.Id, label = group.First().Plan.Title,
                reason = "Matériel déjà prévu ensemble dans " + group.Count().ToString(CultureInfo.InvariantCulture) + " préparation(s) récente(s). Vérifiez quantités et compatibilité avant l’ajout.",
                lines = group.First().Plan.Lines.Select(line => { var reference = references[line.ReferenceId]; return new FieldMaterialLine
                { ReferenceId = reference.Id, Code = reference.Code, Label = reference.Label, Unit = reference.Unit, Quantity = line.Quantity,
                  Category = catalogue.Catalogue.Categories.Single(category => category.Id == reference.CategoryId).Name, Note = reference.Note }; }).ToArray()
            }).ToArray();
        return new { suggestions, catalogueRevision = catalogue.Item?.Revision ?? 0, message = "Suggestions locales issues de vos préparations, sans IA distante, sans ajout automatique ni garantie de compatibilité." };
    }
}
