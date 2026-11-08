using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record IdentityPerson(string Key, string Label, bool FromSource, bool FromCalendar, int? TicketCount);
    private sealed record PersonalIdentity(string Key, string Label);

    private void RegisterIdentityOperations()
    {
        Register("identity.get", IdentityGetAsync);
        Register("identity.set", IdentitySetAsync);
        Register("identity.create", IdentityCreateAsync);
        Register("identity.clear", (a, ct) =>
        {
            if (a.TryGetProperty("revision", out _)) CheckRevision(a);
            ct.ThrowIfCancellationRequested();
            Settings.Dashboard.PersonalTechnician = ""; SaveSettings();
            Publish("workplace.changed", new { revision = Revision }); Publish("calendar.changed", new { identity = "" });
            return Task.FromResult<object?>(new { key = "", label = "", revision = Revision });
        });
    }

    private async Task<PersonalIdentity?> IdentityCurrentAsync(CancellationToken ct)
    {
        var key = Settings.Dashboard.PersonalTechnician;
        if (string.IsNullOrWhiteSpace(key)) return null;
        var stored = await TeamWorkspace.GetAsync(CalendarWorkspaceDocuments.ResourceId(key), ct).ConfigureAwait(false);
        var label = stored is { Deleted: false, Kind: CalendarWorkspaceDocuments.ResourceKind }
            ? CalendarWorkspaceDocuments.ReadResource(stored).Label : key;
        return new(key, label);
    }

    private async Task<(IReadOnlyList<IdentityPerson> People, string Message)> IdentityPeopleAsync(CancellationToken ct)
    {
        var saved = await TeamWorkspace.CalendarResourcesAsync(ct).ConfigureAwait(false);
        var people = saved.Select(CalendarWorkspaceDocuments.ReadResource).ToDictionary(resource => resource.Key,
            resource => new IdentityPerson(resource.Key, resource.Label, false, true, null), StringComparer.Ordinal);
        var roster = Result is null ? null : TechnicianDashboardService.Detect(RequireResult(), EffectiveSettings.Dashboard, ct);
        if (roster is not null)
        {
            foreach (var person in roster.Technicians)
            {
                ct.ThrowIfCancellationRequested();
                people[person.Key] = people.TryGetValue(person.Key, out var known)
                    ? known with { FromSource = true, TicketCount = person.Rows.Length }
                    : new(person.Key, person.Name, true, false, person.Rows.Length);
            }
        }
        // A technician can already be known to planning before a resource document
        // or a ticket exists. Keep those exact keys available without expanding a calendar.
        var preferences = WorkTechnicianPreferences();
        var tourPeople = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours.SelectMany(tour =>
            tour.Stops.Select(stop => stop.TechnicianKey.Length > 0 ? stop.TechnicianKey : tour.TechnicianKey));
        foreach (var key in preferences.Technicians.Keys.Concat(tourPeople).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (people.TryGetValue(key, out var person)) people[key] = person with { FromCalendar = true };
            else people.Add(key, new(key, key, false, true, null));
        }
        var message = Result is null ? "Choisissez votre nom ou ajoutez-le pour préparer vos interventions, même sans relevé."
            : roster is { Available: false } ? roster.Message : "";
        return (people.Values.OrderBy(person => person.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(person => person.Key, StringComparer.Ordinal).ToArray(), message);
    }

    private async Task<object?> IdentityGetAsync(JsonElement a, CancellationToken ct)
    {
        var search = Text(a, "search", 300).Trim(); var offset = Number(a, "offset", 0, 0, 1000000); var limit = Number(a, "limit", 50, 1, 100);
        var current = await IdentityCurrentAsync(ct).ConfigureAwait(false);
        // The always-visible identity button must not analyse the current extract.
        if (Flag(a, "currentOnly")) return new { revision = Revision, current, people = Array.Empty<IdentityPerson>(), total = 0, hasSource = Result is not null, message = "", offset, limit };
        var catalog = await IdentityPeopleAsync(ct).ConfigureAwait(false);
        var filtered = catalog.People.Where(person => search.Length == 0 || TicketReadingService.Contains(person.Label, search) || TicketReadingService.Contains(person.Key, search)).ToArray();
        if (current is not null && catalog.People.FirstOrDefault(person => person.Key == current.Key) is { } selected) current = new(selected.Key, selected.Label);
        return new { revision = Revision, current, people = filtered.Skip(offset).Take(limit), total = filtered.Length, hasSource = Result is not null, catalog.Message, offset, limit };
    }

    private async Task<object?> IdentitySetAsync(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var key = Text(a, "key", 400);
        var catalog = await IdentityPeopleAsync(ct).ConfigureAwait(false);
        var person = catalog.People.FirstOrDefault(person => person.Key == key)
            ?? throw new InvalidDataException("Cette identité n’est plus disponible. Actualisez la liste avant de la choisir.");
        return await IdentityChooseAsync(person, ct).ConfigureAwait(false);
    }

    private async Task<object?> IdentityChooseAsync(IdentityPerson person, CancellationToken ct)
    {
        var stored = await TeamWorkspace.GetAsync(CalendarWorkspaceDocuments.ResourceId(person.Key), ct).ConfigureAwait(false);
        if (stored is { Deleted: true }) throw new InvalidDataException("Cette identité a été retirée du calendrier. Actualisez la liste avant de choisir une personne.");
        if (stored is null && (await TeamWorkspace.CalendarResourcesAsync(ct).ConfigureAwait(false)).Count >= 2000)
            throw new InvalidDataException("Le calendrier accepte au maximum 2 000 ressources.");
        // The existing calendar operation persists an observed technician together
        // with their declared availability, then publishes the normal identity events.
        await CalendarIdentityAsync(JsonSerializer.SerializeToElement(new { key = person.Key }, Json), ct).ConfigureAwait(false);
        return new { person.Key, person.Label, revision = Revision };
    }

    private async Task<object?> IdentityCreateAsync(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var input = Text(a, "label", 300);
        if (input.Any(char.IsControl)) throw new InvalidDataException("Saisissez un nom ou un identifiant sur une seule ligne.");
        var label = Regex.Replace(input.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
        if (label.Length == 0 || DashboardService.SplitTechnicianNames(label, EffectiveSettings.Dashboard.MultiValueSeparators).Count != 1)
            throw new InvalidDataException("Saisissez le nom ou l’identifiant d’une seule personne, tel qu’il apparaît dans vos données.");
        var key = TechnicianDashboardService.IdentityKey(label);
        var catalog = await IdentityPeopleAsync(ct).ConfigureAwait(false);
        var matching = catalog.People.Where(person => TechnicianDashboardService.IdentityKey(person.Key) == key
            || TechnicianDashboardService.IdentityKey(person.Label) == key).ToArray();
        if (matching.Length > 1)
            throw new InvalidDataException("Plusieurs identités correspondent à ce nom. Choisissez explicitement la bonne personne dans la liste.");
        if (matching.Length == 1) return await IdentityChooseAsync(matching[0], ct).ConfigureAwait(false);
        var resource = new CalendarResource { Key = key, Label = label, TimeZoneId = CalendarRange(JsonSerializer.SerializeToElement(new { }, Json)).Zone };
        await CalendarSaveResourceAsync(JsonSerializer.SerializeToElement(new { resource, chooseAsMe = true, expectedRevision = 0 }, Json), ct).ConfigureAwait(false);
        return new { key, label, revision = Revision };
    }
}
