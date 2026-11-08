using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record WorkOverviewSelection(string Token, long Revision, ExtractionResult Source, Dictionary<string, int[]> Rows);
    private WorkOverviewSelection? _workOverviewSelection;

    private object WorkScopeChoices(CancellationToken ct) => new
    {
        profile = AnalyticsBusinessBootstrap(ct), personalTechnician = TechnicianDashboardService.IdentityKey(Settings.Dashboard.PersonalTechnician),
        technicians = Result is null ? Array.Empty<object>() : WorkWorkload(ct).Roster.Technicians.Select(person => (object)new { person.Key, person.Name }).ToArray()
    };

    private int[]? RecordScopedRows(JsonElement args, int[]? indices, CancellationToken ct)
    {
        var hasTeams = args.TryGetProperty("focusKeys", out var teams) && !(teams.ValueKind == JsonValueKind.Array && teams.GetArrayLength() == 0);
        var hasTechnicians = args.TryGetProperty("technicianKeys", out var technicians) && !(technicians.ValueKind == JsonValueKind.Array && technicians.GetArrayLength() == 0);
        if (!hasTeams && !hasTechnicians && !Flag(args, "mine")) return indices;
        var scope = WorkScopeRows(args, ct);
        if (indices is null) return scope;
        var allowed = scope.ToHashSet(); return indices.Where(allowed.Contains).ToArray();
    }

    // Filtering is a reading aid, never an authorization boundary. All work
    // functions stay accessible independently from the selected GLPI identity.
    private int[] WorkScopeRows(JsonElement args, CancellationToken ct)
    {
        var workload = WorkWorkload(ct);
        var filters = new List<DashboardScopeFilter>(); AnalyticsBusinessFocus(args, filters, ct);
        var rows = DashboardService.ResolveScope(workload.Global, filters, ct).ToHashSet();
        var technicians = MapWorkspaceStrings(args, "technicianKeys");
        if (Flag(args, "mine"))
        {
            var personal = TechnicianDashboardService.IdentityKey(Settings.Dashboard.PersonalTechnician);
            if (personal.Length == 0) throw new InvalidOperationException("Choisissez votre identité avant d’afficher uniquement vos tickets.");
            technicians = [personal];
        }
        if (technicians.Length > 0)
        {
            if (!workload.Roster.Available) throw new InvalidOperationException("La colonne des techniciens n’est pas disponible dans ce relevé.");
            var matching = workload.Roster.Technicians.Where(person => technicians.Contains(person.Key, StringComparer.Ordinal)).SelectMany(person => person.Rows).ToHashSet();
            if (technicians.Contains("@unassigned", StringComparer.Ordinal)) matching.UnionWith(workload.UnassignedRows);
            rows.IntersectWith(matching);
        }
        return rows.Order().ToArray();
    }

    private object WorkOverview(JsonElement args, CancellationToken ct)
    {
        if (Result is null) return new { hasSource = false, revision = Revision, personalTechnician = Settings.Dashboard.PersonalTechnician };
        var data = RequireResult(); var work = WorkWorkload(ct); var scoped = WorkScopeRows(args, ct).ToHashSet();
        var rows = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var metrics = new List<object>();
        foreach (var key in new[] { "active", "high", "overdue", "stale", "old", "pending" })
        {
            var metric = work.Global.Metric(key)!;
            rows[key] = metric.Rows.Where(scoped.Contains).ToArray();
            metrics.Add(new { key, metric.Label, value = metric.Value.HasValue ? (int?)rows[key].Length : null, metric.Unit, metric.Explanation });
        }
        var active = rows["active"].ToHashSet();
        rows["unassigned"] = work.UnassignedRows.Where(row => scoped.Contains(row) && active.Contains(row)).ToArray();
        metrics.Add(new { key = "unassigned", label = "Sans technicien", value = work.Roster.Available ? (int?)rows["unassigned"].Length : null,
            unit = work.Global.Grain, explanation = "Tickets actifs sans technicien renseigné dans la colonne d’affectation du relevé." });
        var token = Guid.NewGuid().ToString("N"); _workOverviewSelection = new(token, Revision, data, rows);
        var overdue = rows["overdue"].ToHashSet(); var high = rows["high"].ToHashSet(); var stale = rows["stale"].ToHashSet();
        var old = rows["old"].ToHashSet(); var unassigned = rows["unassigned"].ToHashSet();
        string Cell(int index, string role) => work.Global.Column(role)?.Index is int column && column < data.Rows[index].Length
            ? DashboardService.NormalizeValue(data.Rows[index][column], role, data.Headers[column], EffectiveSettings.Dashboard) : "";
        var priority = active.OrderByDescending(overdue.Contains).ThenByDescending(high.Contains).ThenByDescending(unassigned.Contains)
            .ThenByDescending(stale.Contains).ThenByDescending(old.Contains).ThenBy(index => index).Take(12).Select(index => new
            {
                rowIndex = index, id = Cell(index, "id"), title = Cell(index, "title"), status = Cell(index, "status"), technician = Cell(index, "assignee"),
                reasons = new[] { overdue.Contains(index) ? "Échéance dépassée" : null, high.Contains(index) ? "Priorité haute" : null,
                    unassigned.Contains(index) ? "Sans technicien" : null, stale.Contains(index) ? "Sans mise à jour récente" : null,
                    old.Contains(index) ? "Ancienneté à revoir" : null }.Where(value => value is not null).ToArray()
            }).ToArray();
        var people = work.Technicians.Select(person => new
        {
            person.Key, person.Name, assigned = person.Rows.Count(scoped.Contains), active = person.ActiveCount.HasValue ? (int?)person.Measure("active").Rows.Count(scoped.Contains) : null,
            overdue = person.OverdueCount.HasValue ? (int?)person.Measure("overdue").Rows.Count(scoped.Contains) : null,
            high = person.HighCount.HasValue ? (int?)person.Measure("high").Rows.Count(scoped.Contains) : null
        }).Where(person => person.assigned > 0).OrderByDescending(person => person.active).ThenBy(person => person.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new { hasSource = true, revision = Revision, token, metrics, priority, people, total = scoped.Count, sourceTotal = data.Rows.Count,
            work.Global.Grain, work.Global.ExtractedAt, personalTechnician = Settings.Dashboard.PersonalTechnician,
            assignmentAvailable = work.Roster.Available, rosterNote = work.Roster.Message,
            warnings = work.Global.Warnings.Take(5).ToArray() };
    }

    private object WorkOverviewRows(JsonElement args)
    {
        var selected = _workOverviewSelection;
        if (selected is null || selected.Token != Text(args, "token", 64) || selected.Revision != Revision || !ReferenceEquals(selected.Source, Result))
            throw new InvalidOperationException("Le périmètre a changé. Actualisez les priorités avant d’ouvrir leurs tickets.");
        if (!selected.Rows.TryGetValue(Text(args, "key", 30), out var rows)) throw new InvalidDataException("Indicateur inconnu.");
        return new { revision = Revision, rows };
    }
}
