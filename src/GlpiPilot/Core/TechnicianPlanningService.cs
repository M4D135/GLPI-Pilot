using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record DispatchTicket(string Key, string Id, string Title, string Territory, string Category,
    string[] CurrentTechnicians, int SourceIndex, bool ActiveKnown = true);
public sealed record DispatchRecommendation(string TechnicianKey, string Name, bool AvailableForScenario,
    int Score, double? EstimatedLoadMinutes, int? CapacityMinutes, string Evidence);
public sealed record TerritoryCoverage(string Territory, string Primary, string Backup, string AvailableDeclared, string Gaps);
public sealed record DispatchLoad(string Technician, double? BeforeMinutes, double? AfterMinutes, int? CapacityMinutes, int ProposedTickets, string Coverage)
{
    public double? BeforeMinimumMinutes { get; init; }
    public double? BeforeMaximumMinutes { get; init; }
    public double? AfterMinimumMinutes { get; init; }
    public double? AfterMaximumMinutes { get; init; }
}
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
            && date.Date >= preference.UnavailableFrom.Value.Date && date.Date <= preference.UnavailableUntil.Value.Date
        || CapacityForDate(preference, date) == 0;

    public static int? CapacityForDate(TechnicianPreference preference, DateTime date, string period = "day")
    {
        if (period is not ("day" or "morning" or "afternoon")) throw new ArgumentException("Unknown day period.", nameof(period));
        if (preference.UnavailableFrom.HasValue && preference.UnavailableUntil.HasValue && date.Date >= preference.UnavailableFrom.Value.Date && date.Date <= preference.UnavailableUntil.Value.Date) return 0;
        var change = preference.CapacityDays.FirstOrDefault(d => d.Date.Date == date.Date);
        var periods = preference.WorkPeriods.Where(p => p.Day == date.DayOfWeek).ToArray();
        int? Part(bool morning)
        {
            var value = morning ? change?.MorningMinutes : change?.AfternoonMinutes;
            if (value.HasValue) return value;
            if (preference.WorkPeriods.Count == 0) return null;
            var start = morning ? 0 : 720; var end = morning ? 720 : 1440;
            return periods.Sum(p => (int)Math.Floor(p.CapacityMinutes * (double)Math.Max(0, Math.Min(end, p.EndMinute) - Math.Max(start, p.StartMinute)) / (p.EndMinute - p.StartMinute)));
        }
        if (period == "morning") return Part(true);
        if (period == "afternoon") return Part(false);
        var am = Part(true); var pm = Part(false);
        if (am.HasValue && pm.HasValue) return am.Value + pm.Value;
        if (change != null) return null;
        return preference.DailyCapacityMinutes;
    }

    public static TechnicianDurationEstimate Estimate(TechnicianPreference preference, string category)
    {
        var match = preference.CategoryEstimates.SingleOrDefault(e => string.Equals(e.Category.Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase));
        return match == null ? new(preference.EstimatedTicketMinutes, null, null, "Estimation générale déclarée ; fourchette non renseignée")
            : new(match.Minutes, match.MinimumMinutes, match.MaximumMinutes, "Modèle déclaré pour la catégorie « " + match.Category + " » ; estimation, pas une durée mesurée");
    }

    public static IReadOnlyList<TechnicianCoverageCell> CoverageMatrix(TechnicianWorkloadSnapshot workload, TechnicianPreferences preferences,
        IEnumerable<string> territories, IEnumerable<string> skills, DateTime firstDate, int days = 7)
    {
        var result = new List<TechnicianCoverageCell>();
        var knownTerritories = territories.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
        var knownSkills = skills.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).DefaultIfEmpty("Toute spécialité").ToArray();
        foreach (var territory in knownTerritories)
        foreach (var skill in knownSkills)
        for (var day = 0; day < Math.Clamp(days, 1, 31); day++)
        foreach (var period in new[] { "morning", "afternoon" })
        {
            var date = firstDate.Date.AddDays(day);
            var candidates = workload.Technicians.Where(t =>
            {
                var p = preferences.Get(t.Key);
                return !IsUnavailable(p, date) && CapacityForDate(p, date, period) != 0
                    && p.PrimaryTerritories.Concat(p.BackupTerritories).Any(v => Key(v) == Key(territory))
                    && (skill == "Toute spécialité" || p.Skills.Any(v => Key(v) == Key(skill)));
            }).ToArray();
            var capacities = candidates.Select(t => CapacityForDate(preferences.Get(t.Key), date, period)).ToArray();
            result.Add(new(territory, skill, date, period == "morning" ? "Matin" : "Après-midi", string.Join(", ", candidates.Select(t => t.Name)),
                candidates.Length == 0 ? 0 : capacities.All(c => c.HasValue) ? capacities.Sum(c => c!.Value) : null,
                candidates.Length == 0 ? "Aucune couverture déclarée" : "Capacité partagée entre secteurs et compétences ; ne pas additionner les cellules"));
        }
        return result;
    }

    public static IReadOnlyList<DispatchRecommendation> Recommend(DispatchTicket ticket, TechnicianWorkloadSnapshot workload,
        TechnicianPreferences preferences, DateTime date, IEnumerable<string>? requiredSkills = null, DispatchScenario? scenario = null,
        IReadOnlyList<DispatchTicket>? availableTickets = null)
    {
        var required = (requiredSkills ?? Array.Empty<string>()).Select(Key).Where(text => text.Length > 0).Distinct().ToArray();
        return workload.Technicians.Select(technician =>
        {
            var preference = preferences.Get(technician.Key); var capacity = CapacityForDate(preference, date); var estimate = Estimate(preference, ticket.Category); var evidence = new List<string>(); var score = 0;
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
            evidence.Add(estimate.Evidence + (estimate.Minutes.HasValue ? $" · intervention {estimate.Minutes} min ({estimate.MinimumMinutes?.ToString() ?? "?"}–{estimate.MaximumMinutes?.ToString() ?? "?"})" : " · durée indisponible"));
            if (ticket.Territory.Length > 0)
            {
                if (preference.PrimaryTerritories.Any(value => Key(value) == Key(ticket.Territory))) { score += 40; evidence.Add("Secteur principal déclaré"); }
                else if (preference.BackupTerritories.Any(value => Key(value) == Key(ticket.Territory))) { score += 20; evidence.Add("Secteur de remplacement déclaré"); }
                else evidence.Add("Secteur non déclaré");
            }
            else evidence.Add("Localisation du ticket indisponible");
            double? load = BaselineLoad(technician, preference, availableTickets ?? Array.Empty<DispatchTicket>()).Middle;
            if (scenario != null && load.HasValue && availableTickets != null)
            {
                var catalog = availableTickets.ToDictionary(item => item.Key, StringComparer.Ordinal);
                foreach (var assignment in scenario.Assignments.Where(item => item.TicketKey != ticket.Key))
                {
                    if (!catalog.TryGetValue(assignment.TicketKey, out var planned)) continue;
                    if (planned.ActiveKnown && planned.CurrentTechnicians.Contains(technician.Key, StringComparer.Ordinal)) load -= Estimate(preference, planned.Category).Minutes;
                    if (assignment.TechnicianKey == technician.Key) load += assignment.EstimatedMinutes;
                }
                if (load.HasValue) load = Math.Max(0, load.Value);
            }
            if (load.HasValue && capacity > 0)
            {
                var ratio = load.Value / capacity.Value;
                score += (int)Math.Round(20 * Math.Max(0, 1 - ratio));
                evidence.Add($"Charge indicative {load:N0} min / capacité déclarée {capacity:N0} min ; modèles par catégorie quand le ticket est identifié, sinon estimation générale");
            }
            else evidence.Add("Charge ou capacité en minutes non renseignée");
            if (!workload.Global.Complete) evidence.Add("Relevé partiel ou non confirmé");
            if (technician.UnknownStatusCount > 0) evidence.Add("Statuts inconnus exclus de l’estimation");
            if (capacity == 0) evidence.Add("Capacité déclarée nulle");
            return new DispatchRecommendation(technician.Key, technician.Name, !unavailable && missing.Length == 0 && capacity != 0,
                score, load, capacity, string.Join(" · ", evidence));
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
            var baseline = BaselineLoad(technician, preference, tickets);
            var before = baseline.Middle; var after = before; var low = baseline.Low; var high = baseline.High; var proposals = 0;
            foreach (var assignment in scenario.Assignments)
            {
                if (!catalog.TryGetValue(assignment.TicketKey, out var ticket)) continue;
                if (ticket.ActiveKnown && ticket.CurrentTechnicians.Contains(technician.Key, StringComparer.Ordinal))
                {
                    var old = Estimate(preference, ticket.Category); after -= old.Minutes; low -= old.MinimumMinutes; high -= old.MaximumMinutes;
                }
                if (assignment.TechnicianKey == technician.Key) { proposals++; after += assignment.EstimatedMinutes; low = high = null; }
            }
            if (after.HasValue) after = Math.Max(0, after.Value);
            return new DispatchLoad(technician.Name, before, after, CapacityForDate(preference, scenario.Date), proposals,
                !before.HasValue ? "Actifs ou durée déclarée indisponibles"
                    : "Somme des modèles déclarés par catégorie ; durée générale pour les autres actifs. Fourchette inconnue si un actif ou une proposition n’a pas de bornes déclarées. La proposition remplace les affectations du ticket."
                        + (!workload.Global.Complete || technician.UnknownStatusCount > 0 ? " · couverture partielle" : ""))
            { BeforeMinimumMinutes = baseline.Low, BeforeMaximumMinutes = baseline.High, AfterMinimumMinutes = low.HasValue ? Math.Max(0, low.Value) : null, AfterMaximumMinutes = high.HasValue ? Math.Max(0, high.Value) : null };
        }).ToArray();
    }

    private static (double? Middle, double? Low, double? High) BaselineLoad(TechnicianWorkloadEntry technician, TechnicianPreference preference, IReadOnlyList<DispatchTicket> tickets)
    {
        if (!technician.ActiveCount.HasValue) return (null, null, null);
        var catalog = tickets.Where(ticket => ticket.ActiveKnown && ticket.CurrentTechnicians.Contains(technician.Key, StringComparer.Ordinal)).GroupBy(ticket => ticket.SourceIndex).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First());
        double? middle = 0, low = 0, high = 0;
        foreach (var index in technician.Measure("active").Rows)
        {
            var estimate = Estimate(preference, catalog.TryGetValue(index, out var ticket) ? ticket.Category : "");
            middle += estimate.Minutes; low += estimate.MinimumMinutes; high += estimate.MaximumMinutes;
        }
        return (middle, low, high);
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
