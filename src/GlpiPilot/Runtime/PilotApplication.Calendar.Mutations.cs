using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record CalendarMutation(CalendarIntervention Value, TeamWorkspaceItem? Previous, long? Expected);
    private sealed record CalendarCheck(CalendarWindow Window, IReadOnlyList<CalendarConflict> Issues, IReadOnlyList<CalendarOccurrence> Occurrences, IReadOnlyList<CalendarResource> Resources);
    private sealed record CalendarUndoEntry(TeamWorkspaceItem? Previous, string Id, long Revision);
    private sealed record CalendarUndoDraft(DateTimeOffset Expires, IReadOnlyList<CalendarUndoEntry> Entries);
    private sealed record CalendarShiftDraft(DateTimeOffset Expires, IReadOnlyList<CalendarMutation> Changes);
    private readonly Dictionary<string, CalendarUndoDraft> _calendarUndos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CalendarShiftDraft> _calendarShifts = new(StringComparer.Ordinal);
    private static string CalendarConflictFingerprint(IEnumerable<CalendarConflict> issues) => TerrainDigest(issues.Where(issue => issue.Severity is "warning" or "danger")
        .OrderBy(issue => issue.Kind, StringComparer.Ordinal).ThenBy(issue => issue.ParticipantKey, StringComparer.Ordinal).ThenBy(issue => string.Join("|", issue.OccurrenceKeys), StringComparer.Ordinal)
        .Select(issue => new { issue.Kind, issue.Severity, issue.Message, issue.ParticipantKey, keys = issue.OccurrenceKeys.Order(StringComparer.Ordinal).ToArray(), issue.StartUtc, issue.EndUtc }).ToArray());

    private static string CalendarOriginal(JsonElement a, CalendarIntervention value)
    {
        var start = Text(a, "originalStartLocal", 16); if (start.Length > 0) return start;
        throw new InvalidDataException("L’occurrence à modifier doit être précisée ; rechargez le rendez-vous.");
    }
    private static void CalendarCheckRevision(TeamWorkspaceItem? previous, long? expected)
    {
        if (previous is not null && previous.Revision != expected || previous is null && expected is not (null or 0))
            throw new InvalidOperationException("L’intervention a changé. Rechargez sa version actuelle avant d’enregistrer ; votre saisie est conservée.");
        if (previous?.Deleted == true) throw new InvalidOperationException("Cette intervention a été retirée du planning. Rechargez la vue.");
    }
    private async Task<IReadOnlyList<CalendarMutation>> CalendarPrepareAsync(JsonElement a, CancellationToken ct)
    {
        var incoming = CalendarArgument<CalendarIntervention>(a, "intervention");
        if (incoming.Id.Length == 0) incoming.Id = Guid.NewGuid().ToString("N");
        if (Guid.TryParse(incoming.Id, out var parsed)) incoming.Id = parsed.ToString("N");
        var previous = await TeamWorkspace.GetAsync(incoming.Id, ct).ConfigureAwait(false);
        if (previous is not null && previous.Kind != CalendarWorkspaceDocuments.InterventionKind) throw new InvalidDataException("Cette identité n’appartient pas à une intervention.");
        var expected = CalendarExpected(a); CalendarCheckRevision(previous, expected);
        var old = previous is null ? null : CalendarWorkspaceDocuments.ReadIntervention(previous);
        var scope = Text(a, "scope", 20, "series");
        if (scope is not ("series" or "occurrence" or "following")) throw new InvalidDataException("Choisissez cette occurrence, les suivantes ou la série entière.");
        incoming.Revision = previous?.Revision ?? 0;
        incoming.History = old?.History.ToList() ?? new();
        incoming.SeriesId = old?.SeriesId ?? ""; incoming.OccurrenceOffset = old?.OccurrenceOffset ?? 0;
        if (scope != "series" && old is null) throw new InvalidDataException("Une nouvelle intervention ne possède pas encore d’occurrence à modifier.");
        if (scope == "following" && CalendarOriginal(a, old!) == old!.StartLocal) scope = "series";
        if (scope == "occurrence" && old!.Recurrence is not null)
        {
            incoming.Recurrence = null; incoming.Exceptions.Clear(); incoming.History.Clear();
            var changed = InterventionCalendarService.WithOccurrence(old, CalendarOriginal(a, old), incoming);
            changed = InterventionCalendarService.AddHistory(changed, CalendarAuthor, "occurrence-updated", "Occurrence modifiée : " + CalendarOriginal(a, old));
            return new[] { new CalendarMutation(changed, previous, expected) };
        }
        if (scope == "following")
        {
            var original = CalendarOriginal(a, old!); var split = InterventionCalendarService.SplitFollowing(old!, original, incoming);
            var before = InterventionCalendarService.AddHistory(split.Before, CalendarAuthor, "series-split", "La suite de la série est planifiée séparément à partir du " + original + ".");
            var following = InterventionCalendarService.AddHistory(split.Following, CalendarAuthor, "series-created", "Suite de la série " + old!.Id + " à partir du " + original + ".");
            return new[] { new CalendarMutation(before, previous, expected), new CalendarMutation(following, null, null) };
        }
        if (old is not null) incoming = InterventionCalendarService.WithSeries(old, incoming);
        incoming = InterventionCalendarService.AddHistory(incoming, CalendarAuthor, old is null ? "created" : "updated", old is null ? "Intervention ajoutée au planning." : "Planification mise à jour.");
        InterventionCalendarService.Validate(incoming); return new[] { new CalendarMutation(incoming, previous, expected) };
    }
    private async Task<CalendarCheck> CalendarCheckAsync(IReadOnlyList<CalendarMutation> changes, CancellationToken ct, IEnumerable<string>? removedIds = null)
    {
        foreach (var change in changes) InterventionCalendarService.Validate(change.Value);
        var today = DateTimeOffset.UtcNow.Date;
        var candidates = changes.SelectMany(change => new[] { change.Value }.Concat(change.Value.Exceptions.Where(exception => exception.Replacement is not null).Select(exception => exception.Replacement!))).ToArray();
        var starts = candidates.Select(value => InterventionCalendarService.ParseLocal(value.StartLocal)).ToArray();
        var from = starts.Min(); if (changes.Any(change => change.Value.Recurrence is not null) && from < today.AddDays(-31)) from = today.AddDays(-31);
        var last = starts.Where(start => start >= from).DefaultIfEmpty(from).Max();
        var to = changes.Any(change => change.Value.Recurrence is not null) ? from.AddDays(366) : last.AddDays(32);
        if (to < last.AddDays(32)) to = last.AddDays(32);
        if ((to - from).TotalDays > 732) throw new InvalidDataException("Ce lot couvre plus de deux ans. Déplacez les interventions par période plus courte.");
        var window = new CalendarWindow(new(DateTime.SpecifyKind(from.Date.AddDays(-1), DateTimeKind.Utc)), new(DateTime.SpecifyKind(to.Date.AddDays(1), DateTimeKind.Utc)),
            from.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TimeZoneInfo.Local.Id);
        var existing = await CalendarDocumentsAsync(window, ct).ConfigureAwait(false); var ids = changes.Select(change => change.Value.Id).Concat(removedIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var combined = existing.Where(item => !ids.Contains(item.Id)).Select(CalendarWorkspaceDocuments.ReadIntervention).Concat(changes.Select(change => change.Value)).ToArray();
        var resources = await CalendarResourcesAsync(window, existing, ct).ConfigureAwait(false);
        var expansion = InterventionCalendarService.ExpandDetailed(combined, window.From, window.To, ct);
        var occurrences = expansion.Occurrences.Concat(CalendarTours(window, ct)).ToArray();
        var changedKeys = occurrences.Where(value => ids.Contains(value.InterventionId)).Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        var issues = CalendarIssues(occurrences, resources, ct).Where(issue => issue.OccurrenceKeys.Any(changedKeys.Contains))
            .Concat(_calendarTourIssues.Where(issue => occurrences.Any(value => ids.Contains(value.InterventionId) && value.ParticipantKeys.Contains(issue.ParticipantKey, StringComparer.Ordinal)
                && value.StartUtc < issue.EndUtc && value.EndUtc > issue.StartUtc)))
            .Concat(expansion.Issues.Where(issue => ids.Contains(issue.InterventionId)).Select(issue => new CalendarConflict("invalid-occurrence", "danger", issue.Message, "", new[] { issue.InterventionId + ":" + issue.OriginalStartLocal }))).ToArray();
        return new(window, issues, occurrences.Where(value => ids.Contains(value.InterventionId)).ToArray(), resources);
    }
    private async Task<object?> CalendarSaveAsync(JsonElement a, bool preview, CancellationToken ct)
    {
        var changes = await CalendarPrepareAsync(a, ct).ConfigureAwait(false); var check = await CalendarCheckAsync(changes, ct).ConfigureAwait(false);
        var confirmation = check.Issues.Any(issue => issue.Severity is "warning" or "danger"); var requiresCorrection = check.Issues.Any(issue => issue.Kind is "invalid-occurrence" or "tour-time-invalid");
        var conflictFingerprint = CalendarConflictFingerprint(check.Issues);
        if (requiresCorrection && !preview) throw new InvalidDataException("Un horaire de la planification est inexistant ou ambigu. Corrigez-le avant d’enregistrer : " + check.Issues.First(issue => issue.Kind is "invalid-occurrence" or "tour-time-invalid").Message);
        if (preview || confirmation && (!Flag(a, "allowConflicts") || Text(a, "conflictFingerprint", 64) != conflictFingerprint)) return new { saved = false, preview, requiresCorrection, requiresConfirmation = confirmation, conflictFingerprint, issues = check.Issues,
            occurrences = check.Occurrences, checkedFrom = check.Window.FromDate, checkedTo = check.Window.ToDate };
        var result = await CalendarCommitAsync(changes, check.Resources, ct).ConfigureAwait(false);
        return new { saved = true, item = CalendarItemReply(result.Saved.Last()), items = result.Saved.Select(CalendarItemReply), undoToken = result.UndoToken,
            issues = check.Issues, checkedFrom = check.Window.FromDate, checkedTo = check.Window.ToDate };
    }
    private async Task<(IReadOnlyList<TeamWorkspaceItem> Saved, string UndoToken)> CalendarCommitAsync(IReadOnlyList<CalendarMutation> changes, IReadOnlyList<CalendarResource> resources, CancellationToken ct)
    {
        var batch = changes.Select(change => (CalendarWorkspaceDocuments.ForIntervention(change.Value, CalendarAuthor), change.Expected)).ToList();
        var known = await TeamWorkspace.CalendarResourcesAsync(ct).ConfigureAwait(false); var keys = known.Select(CalendarWorkspaceDocuments.ReadResource).Select(resource => resource.Key).ToHashSet(StringComparer.Ordinal);
        var participants = changes.SelectMany(change => change.Value.ParticipantKeys.Concat(change.Value.Exceptions.Where(exception => exception.Replacement is not null).SelectMany(exception => exception.Replacement!.ParticipantKeys))).Distinct(StringComparer.Ordinal);
        foreach (var key in participants.Where(key => !keys.Contains(key)))
        {
            var resource = resources.FirstOrDefault(resource => resource.Key == key) ?? new CalendarResource { Key = key, Label = key };
            batch.Add((CalendarWorkspaceDocuments.ForResource(resource, CalendarAuthor), null)); keys.Add(key);
        }
        if (keys.Count > 2000) throw new InvalidDataException("Le calendrier accepte au maximum 2 000 ressources.");
        var saved = await TeamWorkspace.SaveCalendarBatchAsync(batch, ct).ConfigureAwait(false);
        var events = saved.Where(item => item.Kind == CalendarWorkspaceDocuments.InterventionKind).ToArray();
        var undo = CalendarRememberUndo(events.Select(item => new CalendarUndoEntry(changes.Single(change => change.Value.Id == item.Id).Previous, item.Id, item.Revision)).ToArray());
        Publish("calendar.changed", new { ids = events.Select(item => item.Id), saved = true });
        return (events, undo);
    }
    private string CalendarRememberUndo(IReadOnlyList<CalendarUndoEntry> entries)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _calendarUndos.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _calendarUndos.Remove(key);
        while (_calendarUndos.Count >= 20) _calendarUndos.Remove(_calendarUndos.Keys.First());
        var token = Guid.NewGuid().ToString("N"); _calendarUndos[token] = new(now.AddHours(1), entries); return token;
    }
    private async Task<object?> CalendarRemoveAsync(JsonElement a, CancellationToken ct)
    {
        var previous = await CalendarRequireItemAsync(Text(a, "id", 32), ct).ConfigureAwait(false); var expected = CalendarExpected(a); CalendarCheckRevision(previous, expected);
        var value = CalendarWorkspaceDocuments.ReadIntervention(previous); var scope = Text(a, "scope", 20, "series"); TeamWorkspaceItem next;
        if (scope == "following" && CalendarOriginal(a, value) == value.StartLocal) scope = "series";
        if (scope == "occurrence" && value.Recurrence is not null)
        {
            value = InterventionCalendarService.WithOccurrence(value, CalendarOriginal(a, value), null, true);
            value = InterventionCalendarService.AddHistory(value, CalendarAuthor, "occurrence-cancelled", "Occurrence retirée du planning.");
            next = CalendarWorkspaceDocuments.ForIntervention(value, CalendarAuthor);
        }
        else if (scope == "following" && value.Recurrence is not null)
        {
            var original = CalendarOriginal(a, value); var replacement = value.Copy(); replacement.StartLocal = original;
            replacement.EndLocal = InterventionCalendarService.FormatLocal(InterventionCalendarService.ParseLocal(original).Add(InterventionCalendarService.ParseLocal(value.EndLocal) - InterventionCalendarService.ParseLocal(value.StartLocal)));
            value = InterventionCalendarService.SplitFollowing(value, original, replacement).Before;
            value = InterventionCalendarService.AddHistory(value, CalendarAuthor, "following-cancelled", "Suite de la série retirée à partir du " + original + ".");
            next = CalendarWorkspaceDocuments.ForIntervention(value, CalendarAuthor);
        }
        else if (scope is "series" or "occurrence" or "following")
        {
            value = InterventionCalendarService.AddHistory(value, CalendarAuthor, "removed", "Intervention retirée du planning.");
            next = CalendarWorkspaceDocuments.ForIntervention(value, CalendarAuthor); next.Deleted = true;
        }
        else throw new InvalidDataException("Portée de suppression inconnue.");
        var saved = await TeamWorkspace.SaveCalendarBatchAsync(new[] { (next, expected) }, ct).ConfigureAwait(false);
        var token = CalendarRememberUndo(new[] { new CalendarUndoEntry(previous, next.Id, saved[0].Revision) });
        Publish("calendar.changed", new { id = next.Id, removed = true }); return new { removed = true, saved = true, undoToken = token };
    }
    private async Task<object?> CalendarUndoAsync(JsonElement a, CancellationToken ct)
    {
        var token = Text(a, "token", 32);
        if (!_calendarUndos.TryGetValue(token, out var undo) || undo.Expires <= DateTimeOffset.UtcNow) throw new InvalidOperationException("L’annulation n’est plus disponible pour cette modification.");
        var changes = new List<(TeamWorkspaceItem, long?)>();
        foreach (var entry in undo.Entries)
        {
            var current = await CalendarRequireItemAsync(entry.Id, ct, true).ConfigureAwait(false);
            if (current.Revision != entry.Revision) throw new InvalidOperationException("Cette intervention a été modifiée depuis. L’annulation n’écrasera pas une version plus récente.");
            var value = entry.Previous?.Copy() ?? current.Copy();
            if (entry.Previous is null) value.Deleted = true;
            var intervention = CalendarWorkspaceDocuments.ReadIntervention(value);
            intervention = InterventionCalendarService.AddHistory(intervention, CalendarAuthor, "undo", "Dernière modification locale annulée.");
            value.Payload = JsonSerializer.SerializeToElement(intervention, Json); value.Author = CalendarAuthor;
            changes.Add((value, entry.Revision));
        }
        var saved = await TeamWorkspace.SaveCalendarBatchAsync(changes, ct).ConfigureAwait(false); _calendarUndos.Remove(token);
        Publish("calendar.changed", new { undone = true, ids = saved.Select(item => item.Id) }); return new { saved = true, undone = true };
    }
    private async Task<object?> CalendarSuggestAsync(JsonElement a, CancellationToken ct)
    {
        var value = CalendarArgument<CalendarIntervention>(a, "intervention"); InterventionCalendarService.Validate(value);
        var view = await CalendarViewAsync(a, ct, 31).ConfigureAwait(false);
        var original = Text(a, "originalStartLocal", 16); var scope = Text(a, "scope", 20, "series");
        var existing = view.All.Where(occurrence => occurrence.InterventionId != value.Id || scope == "occurrence" && occurrence.OriginalStartLocal != original).ToArray();
        // Suggestions are tentative intervals, not an existing series occurrence. The target was excluded explicitly above.
        value = value.Copy(); value.Id = Guid.NewGuid().ToString("N"); value.SeriesId = ""; value.OccurrenceOffset = 0;
        var extra = CalendarExtraSnapshot(view.Resources, ct); var unresolvedTours = _calendarTourIssues.ToArray();
        IReadOnlyList<CalendarConflict> Additional(CalendarOccurrence candidate)
        {
            var nearby = existing.Where(occurrence => occurrence.EndUtc > candidate.StartUtc.AddDays(-2) && occurrence.StartUtc < candidate.EndUtc.AddDays(2)
                && occurrence.ParticipantKeys.Intersect(candidate.ParticipantKeys, StringComparer.Ordinal).Any()).Append(candidate).ToArray();
            return CalendarExtraIssues(nearby, extra, ct).Where(issue => issue.OccurrenceKeys.Contains(candidate.Key, StringComparer.Ordinal))
                .Concat(unresolvedTours.Where(issue => candidate.ParticipantKeys.Contains(issue.ParticipantKey, StringComparer.Ordinal) && candidate.StartUtc < issue.EndUtc && candidate.EndUtc > issue.StartUtc)).ToArray();
        }
        var suggestions = InterventionCalendarService.SuggestSlots(value, existing, view.Resources, view.Window.From, view.Window.To, Number(a, "limit", 8, 1, 30), ct, Additional);
        return new { suggestions, from = view.Window.FromDate, to = view.Window.ToDate, scope = "occurrence", note = "Chaque proposition porte sur ce créneau. Une série complète est contrôlée lors de son enregistrement." };
    }
    private static CalendarIntervention CalendarShiftOccurrence(CalendarIntervention current, string original, int minutes)
    {
        if (current.Recurrence is null)
        {
            var single = current.Copy(); single.StartLocal = InterventionCalendarService.FormatLocal(InterventionCalendarService.ParseLocal(single.StartLocal).AddMinutes(minutes));
            single.EndLocal = InterventionCalendarService.FormatLocal(InterventionCalendarService.ParseLocal(single.EndLocal).AddMinutes(minutes)); return single;
        }
        var replacement = current.Exceptions.FirstOrDefault(exception => exception.OriginalStartLocal == original)?.Replacement?.Copy();
        if (replacement is null)
        {
            replacement = current.Copy(); replacement.StartLocal = original;
            replacement.EndLocal = InterventionCalendarService.FormatLocal(InterventionCalendarService.ParseLocal(original).Add(InterventionCalendarService.ParseLocal(current.EndLocal) - InterventionCalendarService.ParseLocal(current.StartLocal)));
        }
        replacement.Recurrence = null; replacement.Exceptions.Clear(); replacement.History.Clear();
        replacement.StartLocal = InterventionCalendarService.FormatLocal(InterventionCalendarService.ParseLocal(replacement.StartLocal).AddMinutes(minutes));
        replacement.EndLocal = InterventionCalendarService.FormatLocal(InterventionCalendarService.ParseLocal(replacement.EndLocal).AddMinutes(minutes));
        return InterventionCalendarService.WithOccurrence(current, original, replacement);
    }
    private async Task<object?> CalendarShiftAsync(JsonElement a, CancellationToken ct)
    {
        var token = Text(a, "token", 32); IReadOnlyList<CalendarMutation> changes;
        if (token.Length > 0)
        {
            if (!_calendarShifts.TryGetValue(token, out var prepared) || prepared.Expires <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Cet aperçu de déplacement a expiré. Préparez-le à nouveau.");
            changes = prepared.Changes;
            foreach (var change in changes) CalendarCheckRevision(await TeamWorkspace.GetAsync(change.Value.Id, ct).ConfigureAwait(false), change.Expected);
        }
        else
        {
            if (!a.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 50) throw new InvalidDataException("Sélectionnez entre 1 et 50 interventions.");
            var minutes = Number(a, "minutes", 0, -525600, 525600); if (minutes == 0) throw new InvalidDataException("Indiquez un décalage non nul en minutes.");
            var accumulated = new Dictionary<string, CalendarMutation>(StringComparer.Ordinal); var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                var id = Text(item, "id", 32); var expected = CalendarExpected(item); var previous = await CalendarRequireItemAsync(id, ct).ConfigureAwait(false); CalendarCheckRevision(previous, expected);
                var current = accumulated.TryGetValue(id, out var existing) ? existing.Value : CalendarWorkspaceDocuments.ReadIntervention(previous);
                var original = current.Recurrence is null ? current.StartLocal : CalendarOriginal(item, current);
                if (!selected.Add(id + ":" + original)) throw new InvalidDataException("Une intervention a été sélectionnée deux fois dans le même déplacement.");
                var shifted = CalendarShiftOccurrence(current, original, minutes);
                shifted = InterventionCalendarService.AddHistory(shifted, CalendarAuthor, "shifted", "Déplacement de " + minutes.ToString(CultureInfo.InvariantCulture) + " minutes : " + original + ".");
                accumulated[id] = new(shifted, previous, expected);
            }
            changes = accumulated.Values.ToArray();
        }
        var check = await CalendarCheckAsync(changes, ct).ConfigureAwait(false); var confirmation = check.Issues.Any(issue => issue.Severity is "warning" or "danger");
        if (token.Length > 0 && check.Issues.Any(issue => issue.Kind is "invalid-occurrence" or "tour-time-invalid")) throw new InvalidDataException("Le déplacement concerne un horaire inexistant ou ambigu. Corrigez les occurrences concernées avant d’appliquer.");
        var conflictFingerprint = CalendarConflictFingerprint(check.Issues);
        if (token.Length == 0 || confirmation && (!Flag(a, "allowConflicts") || Text(a, "conflictFingerprint", 64) != conflictFingerprint))
        {
            if (token.Length == 0)
            {
                foreach (var key in _calendarShifts.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _calendarShifts.Remove(key);
                while (_calendarShifts.Count >= 5) _calendarShifts.Remove(_calendarShifts.Keys.First()); token = Guid.NewGuid().ToString("N");
                _calendarShifts[token] = new(DateTimeOffset.UtcNow.AddMinutes(20), changes);
            }
            return new { preview = true, saved = false, token, requiresConfirmation = confirmation, conflictFingerprint, requiresCorrection = check.Issues.Any(issue => issue.Kind is "invalid-occurrence" or "tour-time-invalid"), changes = check.Occurrences, issues = check.Issues, checkedFrom = check.Window.FromDate, checkedTo = check.Window.ToDate };
        }
        var result = await CalendarCommitAsync(changes, check.Resources, ct).ConfigureAwait(false); _calendarShifts.Remove(token);
        return new { saved = true, count = changes.Count, undoToken = result.UndoToken, issues = check.Issues };
    }
}
