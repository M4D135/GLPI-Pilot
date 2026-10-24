using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record WorkDayRow(PersonalTicket Personal, int? RowIndex, string Status, string City, string Due, bool? Active, bool InSource, bool Mine, int? Priority);
    private sealed record WorkProjection(TechnicianWorkloadSnapshot? Workload, IReadOnlyList<WorkDayRow> Day, IReadOnlyList<DispatchTicket> Dispatch);
    private TechnicianWorkloadSnapshot? _workplaceWorkload;
    private string _workplaceWorkloadStamp = "";
    private void RegisterWorkplaceDay()
    {
        Register("workplace.day.get", (a, ct) => Task.FromResult<object?>(WorkDay(a, ct)));
        Register("workplace.reminders.check", WorkCheckRemindersAsync);
    }
    private TechnicianWorkloadSnapshot WorkWorkload(CancellationToken ct)
    {
        var settings = EffectiveSettings; var stamp = Revision + "\n" + SnapshotStore.RulesFingerprint(settings) + (IsHistorical ? "" : "\n" + DateTimeOffset.Now.ToUnixTimeSeconds() / 60);
        if (_workplaceWorkload is not null && _workplaceWorkloadStamp == stamp) return _workplaceWorkload;
        var data = RequireResult(); var calculated = TechnicianWorkloadService.Calculate(data, settings.Dashboard, referenceTime: IsHistorical ? data.ExtractedAt : DateTimeOffset.Now, ct: ct);
        _workplaceWorkload = calculated; _workplaceWorkloadStamp = stamp; return calculated;
    }
    private WorkProjection WorkBuildProjection(CancellationToken ct)
    {
        var document = WorkDocument(); var saved = document.Tickets.ToDictionary(p => p.Key, StringComparer.Ordinal);
        if (Result is null) return new(null, saved.Values.Select(p => new WorkDayRow(p, null, "État non vérifié", "", "", null, false, false, null)).ToArray(), Array.Empty<DispatchTicket>());
        var source = RequireResult(); var settings = EffectiveSettings; var workload = WorkWorkload(ct);
        var personalKey = TechnicianDashboardService.IdentityKey(Settings.Dashboard.PersonalTechnician);
        var mine = workload.Roster.Technicians.FirstOrDefault(t => t.Key == personalKey)?.Rows.ToHashSet() ?? new();
        var owners = new Dictionary<int, List<string>>();
        foreach (var t in workload.Technicians) foreach (var index in t.Rows) { if (!owners.TryGetValue(index, out var list)) owners[index] = list = new(); list.Add(t.Key); }
        var locations = HistoricalDocument?.LocationEvidence?.ToReport(source) ?? Locations;
        var cities = locations is not null && ReferenceEquals(locations.Data, source) ? locations.Matches.Where(m => m.CityName is not null).GroupBy(m => m.RowIndex).ToDictionary(g => g.Key, g => g.First().CityName!) : new Dictionary<int, string>();
        string Cell(int index, string role)
        {
            if (workload.Global.Column(role)?.Index is not int col || col >= source.Rows[index].Length) return "";
            var value = DashboardService.NormalizeValue(source.Rows[index][col], role, source.Headers[col], settings.Dashboard);
            return role == "due" && DashboardService.TrySourceDate(value, settings.Dashboard, source.ExtractedAt.Offset, out var date)
                ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), source.ExtractedAt.Offset).ToString("O", CultureInfo.InvariantCulture) : value;
        }
        var day = new Dictionary<string, WorkDayRow>(StringComparer.Ordinal); var dispatch = new Dictionary<string, DispatchTicket>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal); var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < source.Rows.Count; i++)
        {
            if ((i & 127) == 0) ct.ThrowIfCancellationRequested(); TicketReference reference;
            try { reference = IsSimulationInstance ? SimulationReference(i) : TicketReferenceResolver.Resolve(source, source.Rows[i], settings); } catch (InvalidOperationException) { continue; }
            var key = WorkplaceStore.TicketKey(reference);
            if (!seen.Add(key)) { ambiguous.Add(key); day.Remove(key); dispatch.Remove(key); continue; }
            var status = Cell(i, "status"); var code = DashboardService.Status(status); var title = WorkplaceStore.DisplayTitle(Cell(i, "title"));
            var personal = saved.GetValueOrDefault(key) ?? new PersonalTicket { Key = key, Id = reference.Id, Url = reference.Url, Title = title };
            var city = cities.GetValueOrDefault(i, Cell(i, "location"));
            if (mine.Contains(i) || saved.ContainsKey(key)) day[key] = new(personal, i, status, city, Cell(i, "due"), code == 0 ? null : code is >= 1 and <= 4, true, mine.Contains(i), DashboardService.Priority(Cell(i, "priority")));
            if (code < 5) dispatch[key] = new(key, reference.Id, title, city, Cell(i, "category"), owners.GetValueOrDefault(i)?.ToArray() ?? Array.Empty<string>(), i, code is >= 1 and <= 4);
        }
        foreach (var p in saved.Values) day.TryAdd(p.Key, new(p, null, ambiguous.Contains(p.Key) ? "Identité ambiguë dans le relevé" : "État non vérifié", "", "", null, false, false, null));
        return new(workload, day.Values.ToArray(), dispatch.Values.ToArray());
    }
    private object WorkDay(JsonElement a, CancellationToken ct)
    {
        var projection = WorkBuildProjection(ct); var doc = WorkDocument(); var now = DateTimeOffset.Now;
        var scope = Number(a, "scope", doc.DayScope, 0, 6); var search = Text(a, "search", 300, doc.DaySearch);
        var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100);
        bool Due(PersonalTicket p) => p.ReminderAt <= now && (!p.ReminderAcknowledgedAt.HasValue || p.ReminderAcknowledgedAt < p.ReminderAt) && !(p.SnoozedUntil > now);
        var filtered = projection.Day.Where(row => scope switch
        {
            1 => row.Mine, 2 => row.Personal.QueueOrder.HasValue, 3 => row.Personal.Pinned, 4 => Due(row.Personal),
            5 => row.Personal.SnoozedUntil > now, 6 => doc.Tickets.Any(p => p.Key == row.Personal.Key),
            _ => !(row.Personal.SnoozedUntil > now) && (row.Personal.QueueOrder.HasValue || Due(row.Personal) || row.Mine && row.Active != false || row.Personal.Pinned)
        }).Where(row => search.Length == 0 || TicketReadingService.Contains(row.Personal.Id + " " + row.Personal.Title + " " + row.City + " " + row.Personal.Notes, search))
            .OrderBy(row => row.Personal.QueueOrder ?? int.MaxValue).ThenByDescending(row => Due(row.Personal)).ThenByDescending(row => row.Personal.Pinned).ThenBy(row => row.Personal.Id).ToArray();
        var personalKey = TechnicianDashboardService.IdentityKey(Settings.Dashboard.PersonalTechnician); var source = Result;
        var sourceKey = source?.Provenance is { SearchVerified: true, ScopeVerified: true } ? SnapshotStore.SourceKey(source) : "";
        var baselineScope = sourceKey.Length == 0 || source is null ? "" : TicketActionService.Hash(sourceKey + "\n" + personalKey + "\n" + SnapshotStore.SchemaFingerprint(source.Headers) + "\n" + SnapshotStore.RulesFingerprint(EffectiveSettings));
        var observations = projection.Day.Where(row => row.InSource).Take(5000).Select(row => new PersonalDayObservation(row.Personal.Key, row.Personal.Id, row.Personal.Title, row.Status, row.Due, row.Mine, row.Priority)).ToArray();
        var briefing = TicketDailyBriefing.Build(_workplacePrevious.DayBaseline, _workplacePrevious.DayBaselineScope, _workplacePrevious.DayBaselineAt, observations, baselineScope, _workplacePrevious.LastSessionStartedAt, source?.ExtractedAt, now, source?.Complete ?? false);
        var stamp = baselineScope + "|" + source?.ExtractedAt.ToUnixTimeMilliseconds();
        if (!IsHistorical && baselineScope.Length > 0 && stamp != _workplaceBriefingStamp)
        { var changed = doc.Copy(); changed.DayBaseline = observations.ToList(); changed.DayBaselineScope = baselineScope; changed.DayBaselineAt = source?.ExtractedAt; WorkSave(changed); _workplaceBriefingStamp = stamp; }
        var tours = ScheduledTourStore.Load(DataPath("scheduled-tours.json")).ForDate(DateOnly.FromDateTime(WorkDate(a, "date", DateTime.Today))).Where(t => personalKey.Length == 0 || t.TechnicianKey.Length == 0 || t.TechnicianKey == personalKey).ToArray();
        return new
        {
            revision = Revision, scope, search, page, pageSize = size, total = filtered.Length, sourceDate = source?.ExtractedAt, complete = source?.Complete ?? false,
            personalTechnician = personalKey, personalName = projection.Workload?.Technicians.FirstOrDefault(t => t.Key == personalKey)?.Name ?? "",
            rows = filtered.Skip(page * size).Take(size).Select(row => new { ticketKey = row.Personal.Key, row.RowIndex, id = row.Personal.Id, title = row.Personal.Title, row.Status, row.City, row.Due, row.Active, row.InSource, row.Mine, row.Priority, row.Personal.QueueOrder, row.Personal.Pinned, row.Personal.ReminderAt, row.Personal.SnoozedUntil, reminderDue = Due(row.Personal), saved = doc.Tickets.Any(p => p.Key == row.Personal.Key) }).ToArray(),
            briefing, tours, templates = doc.ChecklistTemplates, doc.DesktopReminders
        };
    }
    private async Task<object?> WorkCheckRemindersAsync(JsonElement a, CancellationToken ct)
    {
        var doc = WorkDocument(); var now = DateTimeOffset.Now;
        var due = doc.Tickets.Where(p => p.ReminderAt <= now && (!p.ReminderAcknowledgedAt.HasValue || p.ReminderAcknowledgedAt < p.ReminderAt) && !(p.SnoozedUntil > now)).ToArray();
        Publish("workplace.reminders.due", new { count = due.Length });
        if (!doc.DesktopReminders || !NotificationPolicy.CanNotify()) return new { count = due.Length, notified = false, deferred = true };
        var pending = due.Where(p => !_workplaceNotified.TryGetValue(p.Key, out var at) || at < p.ReminderAt).ToArray();
        if (pending.Length == 0) return new { count = due.Length, notified = false, deferred = false };
        ct.ThrowIfCancellationRequested();
        var shown = await Host.NotifyAsync("GLPI Pilot · rappels personnels", $"{pending.Length} rappel(s) à traiter dans Mon espace.");
        if (shown) foreach (var p in pending) _workplaceNotified[p.Key] = now;
        return new { count = due.Length, notified = shown, deferred = !shown };
    }
}
