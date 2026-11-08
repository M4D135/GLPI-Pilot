using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record CalendarConflictReview(string GroupId, string DatabasePath, string GroupHash, DateTimeOffset Expires, IReadOnlyDictionary<string, long> Expected);
    private readonly Dictionary<string, CalendarConflictReview> _calendarConflictReviews = new(StringComparer.Ordinal);
    private async Task<object?> CalendarConflictListAsync(JsonElement a, CancellationToken ct)
    {
        var offset = Number(a, "offset", 0, 0, 100000); var limit = Number(a, "limit", 20, 1, 50);
        var groups = await TeamWorkspace.CalendarConflictGroupsAsync(offset, limit, ct).ConfigureAwait(false);
        return new { groups = groups.Select(group => new { group.Id, title = "Modification de " + group.Items + " élément(s) du planning", group.RecordedAt,
            itemCount = group.Items, reason = "Des modifications simultanées ont été conservées pour examen commun.", group.Resolved }), total = await TeamWorkspace.CalendarConflictGroupCountAsync(ct).ConfigureAwait(false), offset, limit, hasMore = groups.Count == limit };
    }
    private static object CalendarConflictVersion(TeamWorkspaceItem item) => new
    {
        item.Id, item.Kind, item.Title, item.Revision, item.Deleted, item.Author, item.UpdatedAt,
        intervention = item.Kind == CalendarWorkspaceDocuments.InterventionKind ? CalendarWorkspaceDocuments.ReadIntervention(item) : null,
        resource = item.Kind == CalendarWorkspaceDocuments.ResourceKind ? CalendarWorkspaceDocuments.ReadResource(item) : null
    };
    private async Task<object?> CalendarConflictGetAsync(JsonElement a, CancellationToken ct)
    {
        var group = await TeamWorkspace.CalendarConflictGroupAsync(Text(a, "id", 80), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cette modification partagée n’est plus disponible pour examen.");
        var expected = new Dictionary<string, long>(StringComparer.Ordinal); var items = new List<object>();
        foreach (var member in group.Members)
        {
            var current = group.Latest.FirstOrDefault(item => item.Id == member.ItemId) ?? throw new InvalidDataException("Un élément du planning partagé n’est plus disponible.");
            expected[member.ItemId] = current.Revision;
            items.Add(new { id = member.ItemId, current = CalendarConflictVersion(current), incoming = CalendarConflictVersion(member.Incoming), member.Reason });
        }
        foreach (var expired in _calendarConflictReviews.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _calendarConflictReviews.Remove(expired);
        while (_calendarConflictReviews.Count >= 10) _calendarConflictReviews.Remove(_calendarConflictReviews.Keys.First());
        var token = Guid.NewGuid().ToString("N"); _calendarConflictReviews[token] = new(group.Id, LibrarySettings.DatabasePath, TerrainDigest(group.Members), DateTimeOffset.UtcNow.AddMinutes(20), expected);
        return new { group.Id, group.RecordedAt, reason = "Examinez l’ensemble de cette modification. Le choix s’applique à tous ses éléments dans une seule transaction.", items, token };
    }
    private async Task<object?> CalendarConflictResolveAsync(JsonElement a, CancellationToken ct)
    {
        var token = Text(a, "token", 32); var review = CalendarConflictReviewFor(token);
        var choice = Text(a, "choice", 20); if (choice is not ("current" or "incoming")) throw new InvalidDataException("Choisissez la version actuelle ou la proposition conservée.");
        var group = await TeamWorkspace.CalendarConflictGroupAsync(review.GroupId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cette modification n’est plus disponible.");
        if (TerrainDigest(group.Members) != review.GroupHash) throw new InvalidOperationException("Les versions conservées ont changé. Rechargez leur comparaison.");
        var changes = new List<CalendarMutation>();
        foreach (var member in group.Members)
        {
            var current = await TeamWorkspace.GetAsync(member.ItemId, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Un élément du planning n’existe plus.");
            if (!review.Expected.TryGetValue(member.ItemId, out var expected) || expected != current.Revision) throw new InvalidOperationException("Le planning a changé depuis la comparaison. Rechargez les versions avant de choisir.");
            if (choice == "incoming" && member.Incoming.Kind == CalendarWorkspaceDocuments.InterventionKind && !member.Incoming.Deleted)
                changes.Add(new(CalendarWorkspaceDocuments.ReadIntervention(member.Incoming), current, expected));
        }
        if (changes.Count > 0)
        {
            var check = await CalendarCheckAsync(changes, ct, group.Members.Where(member => member.Incoming.Deleted).Select(member => member.ItemId)).ConfigureAwait(false);
            if (check.Issues.Any(issue => issue.Kind is "invalid-occurrence" or "tour-time-invalid")) throw new InvalidDataException("La proposition conservée contient un horaire à corriger avant son application.");
            var fingerprint = CalendarConflictFingerprint(check.Issues);
            if (check.Issues.Any(issue => issue.Severity is "warning" or "danger") && (!Flag(a, "allowConflicts") || Text(a, "conflictFingerprint", 64) != fingerprint))
                return new { resolved = false, requiresConfirmation = true, issues = check.Issues, conflictFingerprint = fingerprint, token };
        }
        await TeamWorkspace.ResolveCalendarConflictAsync(review.GroupId, choice, review.Expected, ct: ct, actor: CalendarAuthor).ConfigureAwait(false);
        _calendarConflictReviews.Remove(token); Publish("calendar.changed", new { conflictsResolved = true }); Publish("library.changed", new { team = true });
        return new { resolved = true, pending = true, message = "Le choix est enregistré pour l’ensemble de la modification ; il sera synchronisé avec l’espace partagé." };
    }
    private CalendarConflictReview CalendarConflictReviewFor(string token)
    {
        if (!_calendarConflictReviews.TryGetValue(token, out var review) || review.Expires <= DateTimeOffset.UtcNow || review.DatabasePath != LibrarySettings.DatabasePath)
            throw new InvalidOperationException("Cette comparaison a expiré ou le stockage a changé. Ouvrez à nouveau les versions à examiner.");
        return review;
    }
    private async Task<object?> CalendarConflictExportAsync(JsonElement a, CancellationToken ct)
    {
        var review = CalendarConflictReviewFor(Text(a, "token", 32));
        var group = await TeamWorkspace.CalendarConflictGroupAsync(review.GroupId, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Versions introuvables.");
        var path = await Host.SaveFileAsync("Exporter les versions du planning", "Versions du planning JSON|*.json", "GLPI-Pilot-versions-planning.json").ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        var full = Path.GetFullPath(path); var database = Path.GetFullPath(LibrarySettings.DatabasePath);
        if (new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            throw new InvalidDataException("L’export des versions ne peut pas remplacer le stockage actif.");
        await File.WriteAllTextAsync(full, JsonSerializer.Serialize(new { schema = "glpi-pilot.calendar-conflicts", format = 1, exportedAt = DateTimeOffset.UtcNow, group }, Json), ct).ConfigureAwait(false);
        return new { cancelled = false, path };
    }
}
