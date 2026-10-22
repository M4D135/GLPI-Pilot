using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record DispatchTicket(string Key, string Id, string Title, string Territory, string Category,
    string[] CurrentTechnicians, int SourceIndex, bool ActiveKnown = true);
public sealed record DispatchRecommendation(string TechnicianKey, string Name, bool AvailableForScenario,
    int Score, double? EstimatedLoadMinutes, int? CapacityMinutes, string Evidence);
public sealed record TerritoryCoverage(string Territory, string Primary, string Backup, string AvailableDeclared, string Gaps);
public sealed record DispatchLoad(string Technician, double? BeforeMinutes, double? AfterMinutes, int? CapacityMinutes, int ProposedTickets, string Coverage);
public sealed class DispatchAssignment
{
    public string TicketKey { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string TechnicianKey { get; set; } = "";
    public int EstimatedMinutes { get; set; } = 30;
    public string Note { get; set; } = "";
}
public sealed class DispatchScenario
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Répartition préparée";
    public DateTime Date { get; set; } = DateTime.Today;
    public List<DispatchAssignment> Assignments { get; set; } = new();
}

/// <summary>Explicit local scenarios. Neither recommendations nor persistence send writes to GLPI.</summary>
public static class TechnicianPlanningService
{
    public static bool IsUnavailable(TechnicianPreference preference, DateTime date) =>
        preference.UnavailableFrom.HasValue && preference.UnavailableUntil.HasValue
        && date.Date >= preference.UnavailableFrom.Value.Date && date.Date <= preference.UnavailableUntil.Value.Date;

    public static IReadOnlyList<DispatchRecommendation> Recommend(DispatchTicket ticket, TechnicianWorkloadSnapshot workload,
        TechnicianPreferences preferences, DateTime date, IEnumerable<string>? requiredSkills = null, DispatchScenario? scenario = null,
        IReadOnlyList<DispatchTicket>? availableTickets = null)
    {
        var required = (requiredSkills ?? Array.Empty<string>()).Select(Key).Where(text => text.Length > 0).Distinct().ToArray();
        return workload.Technicians.Select(technician =>
        {
            var preference = preferences.Get(technician.Key); var evidence = new List<string>(); var score = 0;
            var unavailable = IsUnavailable(preference, date);
            evidence.Add(unavailable ? "Indisponibilité déclarée à cette date" : "Aucune indisponibilité déclarée à cette date ; présence non vérifiée");
            var skills = preference.Skills.Select(Key).ToHashSet();
            var missing = required.Where(skill => !skills.Contains(skill)).ToArray();
            if (required.Length > 0)
            {
                evidence.Add(missing.Length == 0 ? "Compétences demandées déclarées" : "Compétences non déclarées : " + string.Join(", ", missing));
                score += 20 * (required.Length - missing.Length);
            }
            else evidence.Add("Aucune compétence exigée dans ce scénario");
            if (ticket.Territory.Length > 0)
            {
                if (preference.PrimaryTerritories.Any(value => Key(value) == Key(ticket.Territory))) { score += 40; evidence.Add("Secteur principal déclaré"); }
                else if (preference.BackupTerritories.Any(value => Key(value) == Key(ticket.Territory))) { score += 20; evidence.Add("Secteur de remplacement déclaré"); }
                else evidence.Add("Secteur non déclaré");
            }
            else evidence.Add("Localisation du ticket indisponible");
            double? load = technician.ActiveCount.HasValue && preference.EstimatedTicketMinutes.HasValue
                ? technician.ActiveCount.Value * (double)preference.EstimatedTicketMinutes.Value : null;
            if (scenario != null && load.HasValue && availableTickets != null)
            {
                var catalog = availableTickets.ToDictionary(item => item.Key, StringComparer.Ordinal);
                foreach (var assignment in scenario.Assignments.Where(item => item.TicketKey != ticket.Key))
                {
                    if (!catalog.TryGetValue(assignment.TicketKey, out var planned)) continue;
                    if (planned.ActiveKnown && planned.CurrentTechnicians.Contains(technician.Key, StringComparer.Ordinal)) load -= preference.EstimatedTicketMinutes;
                    if (assignment.TechnicianKey == technician.Key) load += assignment.EstimatedMinutes;
                }
                load = Math.Max(0, load.GetValueOrDefault());
            }
            if (load.HasValue && preference.DailyCapacityMinutes > 0)
            {
                var ratio = load.Value / preference.DailyCapacityMinutes.Value;
                score += (int)Math.Round(20 * Math.Max(0, 1 - ratio));
                evidence.Add($"Charge indicative {load:N0} min / capacité déclarée {preference.DailyCapacityMinutes:N0} min ; estimation uniforme par ticket actif");
            }
            else evidence.Add("Charge ou capacité en minutes non renseignée");
            if (!workload.Global.Complete) evidence.Add("Relevé partiel ou non confirmé");
            if (technician.UnknownStatusCount > 0) evidence.Add("Statuts inconnus exclus de l’estimation");
            if (preference.DailyCapacityMinutes == 0) evidence.Add("Capacité déclarée nulle");
            return new DispatchRecommendation(technician.Key, technician.Name, !unavailable && missing.Length == 0 && preference.DailyCapacityMinutes != 0,
                score, load, preference.DailyCapacityMinutes, string.Join(" · ", evidence));
        }).OrderByDescending(item => item.AvailableForScenario).ThenByDescending(item => item.Score)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<DispatchLoad> EstimateLoads(TechnicianWorkloadSnapshot workload, TechnicianPreferences preferences,
        IReadOnlyList<DispatchTicket> tickets, DispatchScenario scenario)
    {
        var catalog = tickets.ToDictionary(ticket => ticket.Key, StringComparer.Ordinal);
        return workload.Technicians.Select(technician =>
        {
            var preference = preferences.Get(technician.Key);
            double? before = technician.ActiveCount.HasValue && preference.EstimatedTicketMinutes.HasValue
                ? technician.ActiveCount.Value * (double)preference.EstimatedTicketMinutes.Value : null;
            var after = before; var proposals = 0;
            foreach (var assignment in scenario.Assignments)
            {
                if (!catalog.TryGetValue(assignment.TicketKey, out var ticket)) continue;
                if (ticket.ActiveKnown && ticket.CurrentTechnicians.Contains(technician.Key, StringComparer.Ordinal)) after -= preference.EstimatedTicketMinutes;
                if (assignment.TechnicianKey == technician.Key) { proposals++; after += assignment.EstimatedMinutes; }
            }
            if (after.HasValue) after = Math.Max(0, after.Value);
            return new DispatchLoad(technician.Name, before, after, preference.DailyCapacityMinutes, proposals,
                !before.HasValue ? "Actifs ou estimation par ticket indisponibles"
                    : "Estimation uniforme des actifs ; la proposition remplace les affectations du ticket dans ce scénario"
                        + (!workload.Global.Complete || technician.UnknownStatusCount > 0 ? " · couverture partielle" : ""));
        }).ToArray();
    }

    public static IReadOnlyList<TerritoryCoverage> Coverage(TechnicianWorkloadSnapshot workload, TechnicianPreferences preferences,
        IEnumerable<string> territories, DateTime date)
    {
        return territories.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).Select(territory =>
            {
                var primary = workload.Technicians.Where(entry => preferences.Get(entry.Key).PrimaryTerritories.Any(value => Key(value) == Key(territory))).ToArray();
                var backup = workload.Technicians.Where(entry => preferences.Get(entry.Key).BackupTerritories.Any(value => Key(value) == Key(territory))).ToArray();
                var available = primary.Concat(backup).DistinctBy(entry => entry.Key).Where(entry => !IsUnavailable(preferences.Get(entry.Key), date)).ToArray();
                return new TerritoryCoverage(territory, string.Join(", ", primary.Select(entry => entry.Name)), string.Join(", ", backup.Select(entry => entry.Name)),
                    string.Join(", ", available.Select(entry => entry.Name)), primary.Length == 0 && backup.Length == 0 ? "Aucun secteur déclaré"
                        : available.Length == 0 ? "Tous les intervenants déclarés indisponibles" : backup.Length == 0 ? "Aucun remplaçant déclaré" : "Couverture déclarée ; présence non vérifiée");
            }).ToArray();
    }

    public static DispatchScenario Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path);
        if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("Le scénario dépasse 2 Mo.");
        return Validate(JsonSerializer.Deserialize<DispatchScenario>(stream) ?? throw new InvalidDataException("Le scénario est vide."));
    }

    public static void Save(string path, DispatchScenario scenario)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Validate(scenario), new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Le scénario dépasse 2 Mo.");
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(full)) File.Copy(full, full + ".bak", overwrite: true);
            File.Move(temporary, full, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public static DispatchScenario Validate(DispatchScenario scenario)
    {
        if (scenario.Version != 1 || string.IsNullOrWhiteSpace(scenario.Name) || scenario.Name.Length > 200 || scenario.Name.Any(char.IsControl)
            || scenario.Assignments == null || scenario.Assignments.Count > 5000)
            throw new InvalidDataException("Le scénario de répartition est invalide ou dépasse 5 000 propositions.");
        var keys = new HashSet<string>();
        foreach (var item in scenario.Assignments)
            if (item == null || item.TicketKey?.Length != 64 || !item.TicketKey.All(Uri.IsHexDigit) || !keys.Add(item.TicketKey)
                || string.IsNullOrWhiteSpace(item.TicketId) || item.TicketId.Length > 160 || item.TicketId.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(item.TechnicianKey) || item.TechnicianKey.Length > 400 || item.TechnicianKey.Any(char.IsControl)
                || item.EstimatedMinutes is < 1 or > 10080 || item.Note == null || item.Note.Length > 1200)
                throw new InvalidDataException("Une proposition du scénario est invalide.");
        return scenario;
    }

    private static string Key(string text) => string.Join(" ", (text ?? "").Trim().ToUpper(CultureInfo.GetCultureInfo("fr-FR")).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
