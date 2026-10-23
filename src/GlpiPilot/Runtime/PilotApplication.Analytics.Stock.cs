using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private static string AnalyticsParticipantKey(DashboardParticipationParticipant person)
        => "participant:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(person.Key)));

    private static DashboardParticipationResult AnalyticsParticipation(DashboardSnapshot snapshot, DashboardWidgetSettings widget)
        => DashboardParticipationService.Project(snapshot.Participation, new() { Roster = widget.ParticipationRoster });

    private static IEnumerable<DashboardParticipationParticipant> AnalyticsParticipants(DashboardParticipationResult result, DashboardWidgetSettings widget)
        => widget.SourceKey == "authors" && widget.ParticipationRoster.Count == 0
            ? result.Participants.Where(person => person.IsAggregate || person.Followups.Count > 0 || person.Tasks.Count > 0)
            : result.Participants;

    private static object AnalyticsParticipant(DashboardParticipationParticipant person) => new
    {
        key = AnalyticsParticipantKey(person), identity = AnalyticsClip(person.Key, 200), name = AnalyticsClip(person.Name, 300), person.IsAggregate,
        followups = person.Followups.Count, tasks = person.Tasks.Count, both = person.Both.Count, writers = person.Writers.Count, assigned = person.Assigned.Count,
        observedNames = person.ObservedNames.Take(16).Select(name => AnalyticsClip(name, 300)).ToArray()
    };

    private static object AnalyticsParticipationContent(DashboardSnapshot snapshot, DashboardWidgetSettings widget, int offset = 0, int? limit = null)
    {
        var result = AnalyticsParticipation(snapshot, widget);
        var people = AnalyticsParticipants(result, widget).ToArray();
        var maximum = people.SelectMany(person => widget.SourceKey == "comparison"
            ? new[] { person.Writers.Count ?? 0, person.Assigned.Count ?? 0 }
            : new[] { person.Followups.Count ?? 0, person.Tasks.Count ?? 0, person.Both.Count ?? 0, person.Writers.Count ?? 0 }).DefaultIfEmpty().Max();
        var size = limit ?? widget.TopCount;
        return new
        {
            result.Available, result.StatusAvailable, result.FollowupsAvailable, result.TasksAvailable, result.AssignmentAvailable,
            result.Grain, result.Population, total = people.Length, offset, limit = size, maximumCount = maximum,
            participants = people.Skip(offset).Take(size).Select(AnalyticsParticipant).ToArray(),
            warnings = result.Warnings.Take(100).Select(warning => AnalyticsClip(warning)).ToArray(), explanation = AnalyticsClip(result.Explanation)
        };
    }

    private static IReadOnlyList<DashboardOldestGroup> AnalyticsOldestGroups(DashboardOldestGroupsSnapshot data, DashboardWidgetSettings widget)
        => widget.SourceKey == "tickets" && data.Available
            ? new[] { new DashboardOldestGroup("scope:", string.IsNullOrWhiteSpace(widget.Title) ? "Actifs du périmètre" : widget.Title,
                data.Rows, data.DatedRows, data.MissingDateRows, data.InvalidDateRows, data.FutureDateRows,
                data.Oldest.FirstOrDefault()?.OpenedAt, data.MaximumAgeDays, data.Oldest.FirstOrDefault()?.Color ?? "#ebf5fb", data.Oldest) }
            : data.Groups;

    private static object? AnalyticsOldestContent(DashboardSnapshot snapshot, DashboardWidgetSettings widget, int offset = 0, int? limit = null)
    {
        var data = snapshot.OldestGroups;
        if (data is null) return null;
        var top = widget.SourceKey == "tickets" ? 10 : 5;
        var size = limit ?? widget.TopCount;
        var groups = AnalyticsOldestGroups(data, widget);
        return new
        {
            data.Available, data.UnavailableReason, data.ReferenceDate, data.Grain, topCount = top,
            data.Count, data.DatedCount, data.UnrankedCount, data.MissingDateCount, data.InvalidDateCount, data.FutureDateCount,
            data.UnknownStatusCount, data.MaximumAgeDays, explanation = AnalyticsClip(data.Explanation), total = groups.Count, offset, limit = size,
            groups = groups.Skip(offset).Take(size).Select(group => new
            {
                group.Key, label = AnalyticsClip(group.Label, 1000), group.Count, group.DatedCount, group.UnrankedCount,
                group.MissingDateCount, group.InvalidDateCount, group.FutureDateCount, group.OldestOpenedAt, group.MaximumAgeDays, group.Color,
                oldest = group.Oldest.Take(top).Select(ticket => new
                {
                    ticket.Index, id = AnalyticsClip(ticket.Id, 200), title = AnalyticsClip(ticket.Title, 1000), ticket.Status, ticket.Priority,
                    assignee = AnalyticsClip(ticket.Assignee, 1000), ticket.OpenedAt, ticket.AgeDays, ticket.RelativeAge, ticket.Color
                }).ToArray()
            }).ToArray()
        };
    }

    private static object AnalyticsSitesContent(AnalyticsRun run, DashboardSnapshot snapshot, DashboardWidgetSettings widget, int offset = 0, int? limit = null)
    {
        var data = snapshot.SiteWorkload;
        var size = limit ?? widget.TopCount;
        var threshold = widget.WarningValue is >= 1 ? widget.WarningValue.Value : 4;
        var idColumn = snapshot.Column("id")?.Index;
        var titleColumn = snapshot.Column("title")?.Index;
        string Cell(int row, int? column, int maximum) => column is int index && index < run.Source.Rows[row].Length
            ? AnalyticsClip(run.Source.Rows[row][index], maximum) : "";
        return new
        {
            data.Available, data.UnavailableReason, data.Grain, data.Population, total = data.Sites.Count, offset, limit = size,
            threshold, explanation = AnalyticsClip(data.Explanation),
            sites = data.Sites.Skip(offset).Take(size).Select(site => new
            {
                site.Key, label = AnalyticsClip(site.Label, 1000), site.Unknown, count = site.Rows.Length,
                advice = site.Rows.Length == 0 ? "Aucun ticket" : site.Unknown ? "À vérifier"
                    : site.Rows.Length >= threshold ? "Intervention à planifier" : "À suivre",
                tickets = site.Rows.Take(5).Select(row => new { index = row, id = Cell(row, idColumn, 200), title = Cell(row, titleColumn, 500) }).ToArray()
            }).ToArray()
        };
    }

    private static DashboardWidgetSettings AnalyticsDetailWidget(AnalyticsRun run, JsonElement args)
        => run.Board.Widgets.SingleOrDefault(widget => widget.Id == Text(args, "widgetId", 96))
            ?? throw new InvalidDataException("Widget introuvable.");

    private static int[] AnalyticsStockRows(AnalyticsRun run, DashboardSnapshot snapshot, JsonElement args, string kind, string key)
    {
        var part = Text(args, "part", 40, "stock");
        if (kind == "sites")
            return snapshot.SiteWorkload.Available
                ? snapshot.SiteWorkload.Sites.SingleOrDefault(site => site.Key == key)?.Rows ?? throw new InvalidDataException("Site absent du périmètre.")
                : throw new InvalidDataException(snapshot.SiteWorkload.UnavailableReason);
        if (kind == "oldest")
        {
            var widget = AnalyticsDetailWidget(run, args);
            var group = snapshot.OldestGroups is { Available: true } data
                ? AnalyticsOldestGroups(data, widget).SingleOrDefault(group => group.Key == key)
                ?? throw new InvalidDataException("Groupe absent du classement.")
                : throw new InvalidDataException("Le classement des plus anciens est indisponible.");
            return part switch
            {
                "stock" => group.Rows, "dated" => group.DatedRows, "missing" => group.MissingDateRows,
                "invalid" => group.InvalidDateRows, "future" => group.FutureDateRows,
                _ => throw new InvalidDataException("Population de dates inconnue.")
            };
        }
        if (kind == "participation")
        {
            var widget = AnalyticsDetailWidget(run, args);
            var result = AnalyticsParticipation(snapshot, widget);
            var person = AnalyticsParticipants(result, widget).SingleOrDefault(person => AnalyticsParticipantKey(person) == key)
                ?? throw new InvalidDataException("Participant absent du périmètre.");
            var measure = part switch
            {
                "followups" => person.Followups, "tasks" => person.Tasks, "both" => person.Both,
                "writers" => person.Writers, "assigned" => person.Assigned,
                _ => throw new InvalidDataException("Mesure de participation inconnue.")
            };
            return measure.Count.HasValue ? measure.Rows : throw new InvalidDataException("Cette mesure est indisponible ; vérifiez les colonnes source.");
        }
        throw new InvalidDataException("Type de détail inconnu.");
    }

    private async Task<object?> AnalyticsCopyIdsAsync(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsRun(args); var snapshot = AnalyticsSnapshot(run, args);
        var kind = Text(args, "kind", 40); var key = Text(args, "key", 200);
        if (snapshot.Column("id")?.Index is not int column) throw new InvalidDataException("La colonne des identifiants est absente ou ambiguë.");
        var rows = AnalyticsStockRows(run, snapshot, args, kind, key);
        var text = new StringBuilder(); var count = 0; var missing = 0;
        foreach (var row in rows)
        {
            if ((count & 255) == 0) ct.ThrowIfCancellationRequested();
            var id = column < run.Source.Rows[row].Length ? run.Source.Rows[row][column].Trim() : "";
            if (id.Length == 0) { missing++; continue; }
            if (text.Length + id.Length + 1 > 2_000_000) throw new InvalidDataException("Cette liste est trop longue pour la copie. Exportez le périmètre pour conserver tous les identifiants.");
            if (text.Length > 0) text.Append(';'); text.Append(id); count++;
        }
        if (count == 0) throw new InvalidDataException("Aucun identifiant renseigné à copier dans cette sélection.");
        RequireAnalyticsRun(args); ct.ThrowIfCancellationRequested();
        await Host.SetClipboardAsync(text.ToString());
        return new { copied = true, count, missing, occurrences = rows.Length };
    }
}
