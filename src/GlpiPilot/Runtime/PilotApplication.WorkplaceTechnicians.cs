using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private void RegisterWorkplaceTechnicians()
    {
        Register("workplace.technicians.get", (a, ct) => Task.FromResult<object?>(WorkTechnicians(a, ct)));
        WorkRegister("workplace.technicians.preference", a => new { key = WorkTechnicianKey(a), preference = WorkTechnicianPreferences().Get(WorkTechnicianKey(a)) });
        WorkRegister("workplace.technicians.save", WorkSaveTechnician);
        Register("workplace.technicians.me", (a, ct) =>
        {
            var key = WorkTechnicianKey(a); if (WorkWorkload(ct).Technicians.All(t => t.Key != key)) throw new InvalidOperationException("Choisissez une identité présente dans ce relevé.");
            Settings.Dashboard.PersonalTechnician = key; SaveSettings(); Publish("workplace.changed", new { revision = Revision }); return Task.FromResult<object?>(new { personalTechnician = key });
        });
        Register("workplace.technicians.rows", (a, ct) => Task.FromResult<object?>(WorkTechnicianRows(a, ct)));
        Register("workplace.technicians.coverage", (a, ct) => Task.FromResult<object?>(WorkTechnicianCoverage(a, ct)));
        Register("workplace.planning.get", (a, ct) => Task.FromResult<object?>(WorkPlanning(a, ct)));
        WorkRegister("workplace.planning.save", WorkSaveScenario);
        Register("workplace.planning.recommend", (a, ct) => Task.FromResult<object?>(WorkRecommend(a, ct)));
        Register("workplace.planning.assign", (a, ct) => Task.FromResult<object?>(WorkAssign(a, ct)));
        WorkRegister("workplace.planning.remove", a => { var scenario = WorkScenario(); scenario.Assignments.RemoveAll(p => p.TicketKey == Text(a, "ticketKey", 64)); TechnicianPlanningService.Save(DataPath("dispatch-scenario.json"), scenario); return new { saved = true }; });
        Register("workplace.planning.handover", WorkPlanningHandoverAsync);
        Register("workplace.planning.export", WorkExportScenarioAsync);
        Register("workplace.technicians.export", WorkExportTechniciansAsync);
    }
    private TechnicianPreferences WorkTechnicianPreferences() => TechnicianPreferences.Load(DataPath("technician-preferences.json"));
    private string WorkTechnicianKey(JsonElement a)
    {
        var key = TechnicianDashboardService.IdentityKey(Text(a, "technicianKey", 400));
        if (key.Length == 0 || key.Any(char.IsControl)) throw new InvalidDataException("Identité de technicien invalide."); return key;
    }
    private object WorkSaveTechnician(JsonElement a)
    {
        var key = WorkTechnicianKey(a); var preference = WorkArgument<TechnicianPreference>(a, "preference");
        var changed = WorkTechnicianPreferences().With(key, preference); changed.Save(DataPath("technician-preferences.json"));
        Publish("technicians.changed", new { key }); return new { key, preference = changed.Get(key) };
    }
    private object WorkTechnicians(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var workload = WorkWorkload(ct); var prefs = WorkTechnicianPreferences(); var search = Text(a, "search", 300);
        var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100); var date = WorkDate(a, "date", DateTime.Today);
        var filtered = workload.Technicians.Where(t => (search.Length == 0 || TicketReadingService.Contains(t.Name, search)) && (!Flag(a, "favorites") || prefs.Get(t.Key).Favorite))
            .OrderByDescending(t => prefs.Get(t.Key).Favorite).ThenByDescending(t => t.ActiveCount).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new
        {
            revision = Revision, workload.Roster.Available, workload.Roster.Message, total = filtered.Length, page, pageSize = size,
            sourceDate = RequireResult().ExtractedAt, workload.Global.Complete, personalTechnician = Settings.Dashboard.PersonalTechnician,
            assigned = workload.AssignedRows.Length, unassigned = workload.UnassignedRows.Length, multiple = workload.MultipleAssignmentRows.Length,
            rows = filtered.Skip(page * size).Take(size).Select(t =>
            {
                var pref = prefs.Get(t.Key); return new
                {
                    t.Key, t.Name, t.ActiveCount, t.PendingCount, t.HighCount, t.OverdueCount, t.OldCount, t.StaleCount, t.UnknownStatusCount, t.MultipleAssignmentCount, t.MeanAgeDays, t.MeanInactivityDays,
                    total = t.Rows.Length, pref.Favorite, pref.ActiveTarget, loadPercent = TechnicianWorkloadService.LoadPercentage(t, pref.ActiveTarget),
                    capacityMinutes = TechnicianPlanningService.CapacityForDate(pref, date), unavailable = TechnicianPlanningService.IsUnavailable(pref, date),
                    pref.Skills, pref.PrimaryTerritories, pref.BackupTerritories,
                    measures = t.Measures.Values.Select(m => new { m.Key, m.Label, m.Count, m.Sample, m.Population, m.Coverage, m.Explanation }).ToArray()
                };
            }).ToArray()
        };
    }
    private object WorkTechnicianRows(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var workload = WorkWorkload(ct); var key = Text(a, "technicianKey", 400); var measure = Text(a, "measure", 100, "total");
        int[] rows;
        if (key.Length == 0) rows = measure switch { "unassigned" => workload.UnassignedRows, "multiple" => workload.MultipleAssignmentRows, "assigned" => workload.AssignedRows, _ => throw new InvalidDataException("Périmètre collectif inconnu.") };
        else
        {
            var t = workload.Technicians.SingleOrDefault(t => t.Key == TechnicianDashboardService.IdentityKey(key)) ?? throw new InvalidOperationException("Technicien absent de ce relevé.");
            rows = t.Measures.TryGetValue(measure, out var metric) ? metric.Rows : throw new InvalidDataException("Indicateur inconnu.");
        }
        var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100); var data = RequireResult();
        return new { revision = Revision, total = rows.Length, page, pageSize = size, headers = data.Headers, rows = rows.Skip(page * size).Take(size).Select(i => new { rowIndex = i, values = data.Rows[i].Select(c => WorkClip(c, 4000)).ToArray() }).ToArray() };
    }
    private object WorkTechnicianCoverage(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var projection = WorkBuildProjection(ct); var prefs = WorkTechnicianPreferences(); var date = WorkDate(a, "date", DateTime.Today);
        var territories = prefs.Technicians.Values.SelectMany(p => p.PrimaryTerritories.Concat(p.BackupTerritories)).Concat(projection.Dispatch.Select(t => t.Territory)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
        var skill = Text(a, "skill", 160); var chosenTerritory = Text(a, "territory", 160);
        var skills = skill.Length > 0 ? new[] { skill } : prefs.Technicians.Values.SelectMany(p => p.Skills).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
        var matrix = TechnicianPlanningService.CoverageMatrix(projection.Workload!, prefs, chosenTerritory.Length > 0 ? new[] { chosenTerritory } : territories, skills, date, Number(a, "days", 7, 1, 31));
        var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100);
        return new { revision = Revision, territories, skills, page, pageSize = size, total = matrix.Count, rows = matrix.Skip(page * size).Take(size).ToArray(), coverage = TechnicianPlanningService.Coverage(projection.Workload!, prefs, territories, date) };
    }
    private DispatchScenario WorkScenario() => TechnicianPlanningService.Load(DataPath("dispatch-scenario.json"));
    private object WorkSaveScenario(JsonElement a)
    {
        var scenario = WorkScenario(); scenario.Name = Text(a, "name", 200, scenario.Name); scenario.Date = WorkDate(a, "date", scenario.Date);
        if (Flag(a, "clear")) { if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez le retrait de toutes les propositions."); scenario.Assignments.Clear(); }
        TechnicianPlanningService.Save(DataPath("dispatch-scenario.json"), scenario); return new { saved = true, scenario.Name, scenario.Date, count = scenario.Assignments.Count };
    }
    private object WorkPlanning(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var projection = WorkBuildProjection(ct); var scenario = WorkScenario(); var prefs = WorkTechnicianPreferences();
        var page = Number(a, "page", 0, 0, 100000); var size = Number(a, "pageSize", 50, 1, 100); var search = Text(a, "search", 300);
        var found = projection.Dispatch.Where(t => search.Length == 0 || TicketReadingService.Contains(t.Id + " " + t.Title + " " + t.Category + " " + t.Territory, search)).ToArray();
        var proposalPage = Number(a, "proposalPage", 0, 0, 100000); var catalog = projection.Dispatch.ToDictionary(t => t.Key);
        return new
        {
            revision = Revision, scenario.Name, scenario.Date, total = found.Length, page, pageSize = size,
            tickets = found.Skip(page * size).Take(size).ToArray(),
            technicians = projection.Workload!.Technicians.Take(2000).Select(t => new { t.Key, t.Name }).ToArray(),
            skills = prefs.Technicians.Values.SelectMany(p => p.Skills).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray(),
            proposalTotal = scenario.Assignments.Count, proposalPage,
            proposals = scenario.Assignments.Skip(proposalPage * size).Take(size).Select(p => new { p.TicketKey, p.TicketId, p.TechnicianKey, p.EstimatedMinutes, p.Note, title = catalog.GetValueOrDefault(p.TicketKey)?.Title ?? "Absent du relevé ; exclu de la simulation", present = catalog.ContainsKey(p.TicketKey) }).ToArray(),
            loads = TechnicianPlanningService.EstimateLoads(projection.Workload!, prefs, projection.Dispatch, scenario).Take(2000).ToArray(),
            coverage = TechnicianPlanningService.Coverage(projection.Workload!, prefs, projection.Dispatch.Select(t => t.Territory).Concat(prefs.Technicians.Values.SelectMany(p => p.PrimaryTerritories.Concat(p.BackupTerritories))).Take(2000), scenario.Date).Take(200).ToArray(),
            historical = IsHistorical, complete = RequireResult().Complete
        };
    }
    private static string[] WorkSkills(JsonElement a)
    {
        var list = a.TryGetProperty("requiredSkills", out var value) ? Read<List<string>>(value) : new();
        if (list.Count > 50 || list.Any(s => s is null || s.Length > 160 || s.Any(char.IsControl))) throw new InvalidDataException("Compétences requises invalides.");
        return list.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private object WorkRecommend(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var projection = WorkBuildProjection(ct); var ticket = projection.Dispatch.SingleOrDefault(t => t.Key == Text(a, "ticketKey", 64)) ?? throw new InvalidOperationException("Ticket absent ou identité ambiguë dans ce scénario.");
        var scenario = WorkScenario(); var prefs = WorkTechnicianPreferences();
        var recommendations = TechnicianPlanningService.Recommend(ticket, projection.Workload!, prefs, scenario.Date, WorkSkills(a), scenario, projection.Dispatch).Take(2000).ToArray();
        return new { ticket, date = scenario.Date, recommendations, estimates = recommendations.Select(r => new { r.TechnicianKey, estimate = TechnicianPlanningService.Estimate(prefs.Get(r.TechnicianKey), ticket.Category) }).ToArray() };
    }
    private object WorkAssign(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var projection = WorkBuildProjection(ct); var key = Text(a, "ticketKey", 64); var tech = WorkTechnicianKey(a);
        var ticket = projection.Dispatch.SingleOrDefault(t => t.Key == key) ?? throw new InvalidOperationException("Le ticket n’est plus disponible dans ce scénario.");
        if (projection.Workload!.Technicians.All(t => t.Key != tech)) throw new InvalidOperationException("Le technicien n’est plus présent dans le relevé.");
        var scenario = WorkScenario(); var prefs = WorkTechnicianPreferences(); var selected = TechnicianPlanningService.Recommend(ticket, projection.Workload, prefs, scenario.Date, WorkSkills(a), scenario, projection.Dispatch).Single(r => r.TechnicianKey == tech);
        if (!selected.AvailableForScenario && !Flag(a, "overrideConfirmed")) throw new InvalidOperationException("Cette proposition ne satisfait pas les disponibilités ou compétences déclarées. Confirmez explicitement la dérogation locale.");
        var minutes = Number(a, "minutes", 30, 1, 10080); var note = Text(a, "note", 1200);
        scenario.Assignments.RemoveAll(p => p.TicketKey == key); scenario.Assignments.Add(new() { TicketKey = key, TicketId = ticket.Id, TechnicianKey = tech, EstimatedMinutes = minutes, Note = note });
        TechnicianPlanningService.Save(DataPath("dispatch-scenario.json"), scenario); return new { saved = true, localOnly = true };
    }
    private async Task<object?> WorkPlanningHandoverAsync(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var projection = WorkBuildProjection(ct); var prefs = WorkTechnicianPreferences(); var scenario = WorkScenario(); var departing = WorkTechnicianKey(a);
        var from = projection.Workload!.Technicians.SingleOrDefault(t => t.Key == departing) ?? throw new InvalidOperationException("Technicien absent de ce relevé.");
        var text = new StringBuilder("PASSATION PRÉPARÉE · " + from.Name + "\nDate : " + scenario.Date.ToString("dd/MM/yyyy") + "\nPropositions locales à examiner ; aucun changement GLPI.\n\n");
        var proposals = new List<DispatchAssignment>();
        foreach (var ticket in projection.Dispatch.Where(t => t.CurrentTechnicians.Contains(departing, StringComparer.Ordinal)).Take(5000))
        {
            ct.ThrowIfCancellationRequested(); var candidate = TechnicianPlanningService.Recommend(ticket, projection.Workload, prefs, scenario.Date, WorkSkills(a), scenario, projection.Dispatch).FirstOrDefault(r => r.AvailableForScenario && r.TechnicianKey != departing);
            if (candidate is null) { text.AppendLine(ticket.Id + " · aucun relais admissible dans les déclarations."); continue; }
            var minutes = TechnicianPlanningService.Estimate(prefs.Get(candidate.TechnicianKey), ticket.Category).Minutes;
            if (!minutes.HasValue) { text.AppendLine(ticket.Id + " · " + candidate.Name + " possible, durée non déclarée ; proposition à compléter."); continue; }
            var proposal = new DispatchAssignment { TicketKey = ticket.Key, TicketId = ticket.Id, TechnicianKey = candidate.TechnicianKey, EstimatedMinutes = minutes.Value, Note = "Relais préparé pour " + from.Name };
            proposals.Add(proposal); scenario.Assignments.RemoveAll(p => p.TicketKey == ticket.Key); scenario.Assignments.Add(proposal);
            text.AppendLine(ticket.Id + " · " + ticket.Title + "\nRelais proposé : " + candidate.Name + " · " + minutes + " min estimées\n" + candidate.Evidence + "\n");
        }
        if (Flag(a, "save")) TechnicianPlanningService.Save(DataPath("dispatch-scenario.json"), scenario);
        if (Flag(a, "copy")) await Host.SetClipboardAsync(text.ToString());
        return new { text = text.ToString(), proposed = proposals.Count, saved = Flag(a, "save") };
    }
    private async Task<object?> WorkExportScenarioAsync(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var scenario = WorkScenario(); var projection = WorkBuildProjection(ct); var catalog = projection.Dispatch.ToDictionary(t => t.Key);
        var path = await Host.SaveFileAsync("Exporter les propositions locales", "CSV (*.csv)|*.csv", "GLPI-Repartition-" + scenario.Date.ToString("yyyyMMdd") + ".csv");
        if (path is null) return new { saved = false };
        var data = new ExtractionResult { Headers = new() { "Ticket", "Titre / couverture", "Technicien proposé", "Minutes prévues", "Date du scénario", "Nature", "Note" }, Complete = true, Scope = "Scénario local" };
        data.Rows = scenario.Assignments.Select(p => new[] { p.TicketId, catalog.GetValueOrDefault(p.TicketKey)?.Title ?? "Absent du relevé", p.TechnicianKey, p.EstimatedMinutes.ToString(CultureInfo.InvariantCulture), scenario.Date.ToString("yyyy-MM-dd"), "Proposition locale, non appliquée dans GLPI", p.Note }).ToList();
        ExportService.WriteCsv(data, path, ct); return new { saved = true, name = Path.GetFileName(path), rows = data.Rows.Count };
    }
    private async Task<object?> WorkExportTechniciansAsync(JsonElement a, CancellationToken ct)
    {
        CheckRevision(a); var workload = WorkWorkload(ct); var prefs = WorkTechnicianPreferences(); var date = WorkDate(a, "date", DateTime.Today);
        var path = await Host.SaveFileAsync("Exporter la charge des techniciens", "CSV (*.csv)|*.csv", "GLPI-Equipe-" + date.ToString("yyyyMMdd") + ".csv"); if (path is null) return new { saved = false };
        string N(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Indéterminé";
        var data = new ExtractionResult { Headers = new() { "Technicien", "Tickets observés", "Actifs", "Priorité élevée", "Échéance dépassée", "Statuts inconnus", "Objectif local", "Capacité déclarée (min)", "Couverture" }, Rows = workload.Technicians.Select(t => new[] { t.Name, N(t.Rows.Length), N(t.ActiveCount), N(t.HighCount), N(t.OverdueCount), N(t.UnknownStatusCount), N(prefs.Get(t.Key).ActiveTarget), N(TechnicianPlanningService.CapacityForDate(prefs.Get(t.Key), date)), workload.Global.Complete ? "Relevé complet" : "Relevé partiel ou non confirmé" }).ToList(), Complete = true };
        ExportService.WriteCsv(data, path, ct); return new { saved = true, name = Path.GetFileName(path), rows = data.Rows.Count };
    }
}
