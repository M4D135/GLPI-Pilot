using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    partial void InvalidatePreparedActions()
    {
        _workplacePreviews.Clear(); _workplaceCandidates.Clear(); _workplaceCandidateTicket = "";
    }
    private WorkplaceDocument? _workplaceDocument;
    private WorkplaceDocument _workplacePrevious = new();
    private readonly Dictionary<string, TicketDetailResult> _workplaceDetails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TicketReadChanges> _workplaceChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _workplaceNotified = new(StringComparer.Ordinal);
    private string _workplaceBriefingStamp = "";

    private void RegisterWorkplaceOperations()
    {
        WorkRegister("workplace.get", a => WorkState());
        WorkRegister("workplace.preferences.save", WorkSavePreferences);
        WorkRegister("workplace.personal.save", WorkSavePersonal);
        WorkRegister("workplace.personal.remove", WorkRemovePersonal);
        WorkRegister("workplace.queue.move", a => { var doc = WorkDocument().Copy(); WorkplaceStore.MoveQueue(doc, WorkPersonal(a).Key, Number(a, "direction", 1, -1, 1)); WorkSave(doc); return WorkState(); });
        WorkRegister("workplace.queue.add", WorkQueueAdd);
        WorkRegister("workplace.template.save", WorkSaveTemplate);
        WorkRegister("workplace.template.remove", a => { var doc = WorkDocument().Copy(); doc.ChecklistTemplates.Remove(Text(a, "name", 100)); WorkSave(doc); return doc.ChecklistTemplates; });
        RegisterWorkplaceTickets(); RegisterWorkplaceActions(); RegisterWorkplaceDay(); RegisterWorkplaceTechnicians(); RegisterWorkplaceEnrichment();
    }

    private void WorkRegister(string operation, Func<JsonElement, object?> handler) => Register(operation, (a, _) => Task.FromResult(handler(a)));
    private static T WorkArgument<T>(JsonElement args, string key) => args.TryGetProperty(key, out var value) ? Read<T>(value) : throw new InvalidDataException("Champ requis : " + key);
    private WorkplaceDocument WorkDocument()
    {
        if (_workplaceDocument is not null) return _workplaceDocument;
        // A corrupt existing document is never replaced by an empty one.
        var loaded = WorkplaceStore.Load(DataPath("workplace.json"));
        _workplacePrevious = loaded.Copy(); loaded.LastSessionStartedAt = DateTimeOffset.Now;
        WorkplaceStore.Save(DataPath("workplace.json"), loaded);
        return _workplaceDocument = loaded;
    }
    private void WorkSave(WorkplaceDocument document)
    {
        var validated = WorkplaceStore.Validate(document);
        WorkplaceStore.Save(DataPath("workplace.json"), validated); _workplaceDocument = validated;
        Publish("workplace.changed", new { revision = Revision });
    }
    private PersonalTicket WorkPersonal(JsonElement args)
    {
        var key = Text(args, "ticketKey", 64);
        return WorkDocument().Tickets.SingleOrDefault(t => t.Key == key) ?? throw new InvalidOperationException("Ce dossier n’est plus conservé. Ouvrez-le à nouveau depuis le relevé.");
    }
    private static object WorkPersonalView(PersonalTicket p) => new
    {
        p.Key, p.Id, p.Url, p.Title, p.Pinned, p.Offline, p.QueueOrder, p.Notes, p.Checklist, p.ReminderAt,
        p.ReminderAcknowledgedAt, p.SnoozedUntil, p.SnoozeReason, p.LastOpenedAt, p.LastReadAt, p.FollowupDraft,
        p.LastActionReceipt, p.PreferredSection, p.ReadingPosition
    };
    private object WorkState()
    {
        var doc = WorkDocument();
        return new
        {
            revision = Revision, activeTicketKey = doc.ActiveTicketKey,
            openTickets = doc.OpenTicketKeys.Select(key => doc.Tickets.First(t => t.Key == key)).Select(WorkPersonalView).ToArray(),
            tickets = doc.Tickets.OrderByDescending(t => t.Pinned).ThenByDescending(t => t.LastOpenedAt).Select(p => new { p.Key, p.Id, p.Title, p.Pinned, p.QueueOrder, p.LastReadAt, p.LastOpenedAt, p.ReminderAt, p.SnoozedUntil, p.Offline }).ToArray(),
            doc.FavoriteSections, doc.DefaultSection, doc.SectionOrder, doc.GroupTimelineByDay, doc.ChecklistTemplates,
            doc.DesktopReminders, doc.DaySearch, doc.DayScope, doc.DaySelectedKey, doc.ActiveWorkspace,
            autoRead = Settings.TicketDetails.AutoLoadOnSelection && Result?.Provenance is not { SourceKind: "local", Method: "ticket-library" }, personalTechnician = Settings.Dashboard.PersonalTechnician
        };
    }
    private object WorkSavePreferences(JsonElement a)
    {
        var doc = WorkDocument().Copy();
        if (a.TryGetProperty("favoriteSections", out var favorites)) doc.FavoriteSections = Read<List<string>>(favorites);
        if (a.TryGetProperty("sectionOrder", out var order)) doc.SectionOrder = Read<List<string>>(order);
        doc.DefaultSection = Text(a, "defaultSection", 300, doc.DefaultSection);
        doc.GroupTimelineByDay = Flag(a, "groupTimelineByDay", doc.GroupTimelineByDay);
        doc.DesktopReminders = Flag(a, "desktopReminders", doc.DesktopReminders);
        doc.DaySearch = Text(a, "daySearch", 300, doc.DaySearch); doc.DayScope = Number(a, "dayScope", doc.DayScope, 0, 6);
        doc.DaySelectedKey = Text(a, "daySelectedKey", 64, doc.DaySelectedKey); doc.ActiveWorkspace = Text(a, "activeWorkspace", 40, doc.ActiveWorkspace);
        WorkSave(doc); return WorkState();
    }
    private object WorkSavePersonal(JsonElement a)
    {
        var old = WorkPersonal(a); var doc = WorkDocument().Copy(); var p = doc.Tickets.Single(t => t.Key == old.Key);
        p.Pinned = Flag(a, "pinned", p.Pinned); p.Notes = Text(a, "notes", 8000, p.Notes);
        p.SnoozeReason = Text(a, "snoozeReason", 1200, p.SnoozeReason);
        if (a.TryGetProperty("checklist", out var checklist)) p.Checklist = Read<List<PersonalChecklistItem>>(checklist);
        if (a.TryGetProperty("reminderAt", out _)) { p.ReminderAt = WorkOffset(a, "reminderAt"); if (p.ReminderAt != old.ReminderAt) p.ReminderAcknowledgedAt = null; }
        if (a.TryGetProperty("snoozedUntil", out _)) p.SnoozedUntil = WorkOffset(a, "snoozedUntil");
        if (Flag(a, "acknowledge")) p.ReminderAcknowledgedAt = DateTimeOffset.Now;
        if (a.TryGetProperty("queued", out _)) p.QueueOrder = Flag(a, "queued") ? p.QueueOrder ?? Math.Min(499, doc.Tickets.Where(t => t.QueueOrder.HasValue).Select(t => t.QueueOrder!.Value).DefaultIfEmpty(-1).Max() + 1) : null;
        if (a.TryGetProperty("offline", out _)) p.Offline = Flag(a, "offline");
        var validated = WorkplaceStore.Validate(doc);
        if (p.Offline && !old.Offline) WorkplaceStore.SaveOffline(DataPath("TicketCache"), WorkDetail(old.Key));
        WorkSave(validated);
        if (!p.Offline && old.Offline) WorkplaceStore.RemoveOffline(DataPath("TicketCache"), new(old.Id, old.Url));
        return WorkPersonalView(_workplaceDocument!.Tickets.Single(t => t.Key == old.Key));
    }
    private object WorkRemovePersonal(JsonElement a)
    {
        var p = WorkPersonal(a);
        if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez le retrait de ce dossier et de ses notes locales.");
        var doc = WorkDocument().Copy(); doc.Tickets.RemoveAll(t => t.Key == p.Key); doc.OpenTicketKeys.Remove(p.Key);
        if (doc.ActiveTicketKey == p.Key) doc.ActiveTicketKey = doc.OpenTicketKeys.LastOrDefault() ?? "";
        WorkSave(doc); WorkplaceStore.RemoveOffline(DataPath("TicketCache"), new(p.Id, p.Url)); _workplaceDetails.Remove(p.Key); _workplaceChanges.Remove(p.Key);
        return WorkState();
    }
    private object WorkSaveTemplate(JsonElement a)
    {
        var name = Text(a, "name", 100).Trim(); if (name.Length == 0) throw new InvalidDataException("Donnez un nom au modèle.");
        var doc = WorkDocument().Copy(); doc.ChecklistTemplates[name] = WorkArgument<List<string>>(a, "items"); WorkSave(doc); return doc.ChecklistTemplates;
    }
    private object WorkQueueAdd(JsonElement a)
    {
        CheckRevision(a); var source = RequireResult(); var settings = EffectiveSettings;
        var indices = WorkArgument<List<int>>(a, "rows");
        if (indices.Count is < 1 or > 500 || indices.Any(i => i < 0 || i >= source.Rows.Count)) throw new InvalidDataException("Sélectionnez de 1 à 500 lignes existantes.");
        var columns = DashboardService.ResolveColumns(source.Headers, settings.Dashboard); var titleColumn = columns.FirstOrDefault(c => c.Role == "title")?.Index;
        var doc = WorkDocument().Copy(); var added = 0; var failures = new List<string>();
        foreach (var index in indices.Distinct())
        {
            TicketReference reference;
            try { reference = TicketReferenceResolver.Resolve(source, source.Rows[index], settings); }
            catch (InvalidOperationException error) { failures.Add(AppPaths.SafeError(error)); continue; }
            var p = WorkplaceStore.Remember(doc, reference, titleColumn is int col ? source.Rows[index][col] : "Ticket " + reference.Id, false);
            if (!p.QueueOrder.HasValue) { p.QueueOrder = Math.Min(499, doc.Tickets.Count(t => t.QueueOrder.HasValue)); added++; }
        }
        WorkSave(doc); return new { added, excluded = failures.Count, warnings = failures.Distinct().Take(5), revision = Revision };
    }
    private static DateTimeOffset? WorkOffset(JsonElement a, string key)
    {
        var raw = Text(a, key, 80); if (raw.Length == 0) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : throw new InvalidDataException("Date invalide : " + key);
    }
    private static DateTime WorkDate(JsonElement a, string key, DateTime fallback)
    {
        var raw = Text(a, key, 40); if (raw.Length == 0) return fallback.Date;
        return DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new InvalidDataException("Date attendue au format AAAA-MM-JJ.");
    }
    private TicketReference WorkValidateReference(TicketReference ticket)
    {
        var id = TicketReferenceResolver.NormalizeId(ticket.Id);
        var uri = TicketReferenceResolver.ReadHttpUrl(ticket.Url, "L’adresse du ticket"); var query = TicketReferenceResolver.ParseQuery(uri);
        if (uri.Fragment.Length > 0 || query.Count(p => p.Key == "id") != 1 || TicketReferenceResolver.NormalizeId(query.Single(p => p.Key == "id").Value) != id)
            throw new InvalidOperationException("L’adresse du ticket ne correspond pas à son identité.");
        var template = uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", query.Select(p => p.Key + "=" + (p.Key == "id" ? "{id}" : Uri.EscapeDataString(p.Value))));
        var valid = TicketReferenceResolver.ValidateTemplate(template, Settings.PortalUrl);
        return new(id, valid.Replace("{id}", id, StringComparison.Ordinal));
    }
    private sealed class WorkProgress<T>(Action<T> onProgress) : IProgress<T>
    { public void Report(T value) => onProgress(value); }
    private static string WorkClip(string? text, int max = 12000) => text is null ? "" : text.Length <= max ? text : text[..max] + "… [texte tronqué dans cet aperçu]";
}
