using System.Globalization;
using System.IO;

namespace GlpiPilot.Core;

public sealed class TechnicianWorkPeriod
{
    public DayOfWeek Day { get; set; }
    public int StartMinute { get; set; } = 540;
    public int EndMinute { get; set; } = 720;
    public int CapacityMinutes { get; set; } = 180;
    public TechnicianWorkPeriod Copy() => (TechnicianWorkPeriod)MemberwiseClone();
}
public sealed class TechnicianCapacityDay
{
    public DateTime Date { get; set; }
    public int? MorningMinutes { get; set; }
    public int? AfternoonMinutes { get; set; }
    public TechnicianCapacityDay Copy() => (TechnicianCapacityDay)MemberwiseClone();
}
public sealed class TechnicianCategoryEstimate
{
    public string Category { get; set; } = "";
    public int Minutes { get; set; } = 30;
    public int MinimumMinutes { get; set; } = 15;
    public int MaximumMinutes { get; set; } = 60;
    public TechnicianCategoryEstimate Copy() => (TechnicianCategoryEstimate)MemberwiseClone();
}
public sealed record TechnicianDurationEstimate(int? Minutes, int? MinimumMinutes, int? MaximumMinutes, string Evidence);
public sealed record TechnicianCoverageCell(string Territory, string Skill, DateTime Date, string Period, string Technicians, int? CapacityMinutes, string Evidence);

public static class TechnicianCalendar
{
    public static void Validate(TechnicianPreference p)
    {
        if (p.WorkPeriods == null || p.CapacityDays == null || p.CategoryEstimates == null || p.WorkPeriods.Count > 28 || p.CapacityDays.Count > 732 || p.CategoryEstimates.Count > 200)
            throw new InvalidDataException("Calendrier limité à 28 plages hebdomadaires, 732 exceptions et 200 modèles de durée.");
        foreach (var w in p.WorkPeriods)
            if (w == null || !Enum.IsDefined(w.Day) || w.StartMinute < 0 || w.EndMinute > 1440 || w.StartMinute >= w.EndMinute || w.CapacityMinutes < 0 || w.CapacityMinutes > w.EndMinute - w.StartMinute)
                throw new InvalidDataException("Une plage de présence ou sa capacité est invalide.");
        foreach (var group in p.WorkPeriods.GroupBy(w => w.Day))
        {
            var ordered = group.OrderBy(w => w.StartMinute).ToArray();
            for (var i = 1; i < ordered.Length; i++) if (ordered[i].StartMinute < ordered[i - 1].EndMinute)
                throw new InvalidDataException("Deux plages de présence se chevauchent le même jour.");
        }
        if (p.CapacityDays.Any(d => d == null || d.MorningMinutes is < 0 or > 720 || d.AfternoonMinutes is < 0 or > 720 || !d.MorningMinutes.HasValue && !d.AfternoonMinutes.HasValue)
            || p.CapacityDays.GroupBy(d => d.Date.Date).Any(g => g.Count() > 1)) throw new InvalidDataException("Une exception de capacité est invalide ou dupliquée.");
        if (p.CategoryEstimates.Any(e => e == null || string.IsNullOrWhiteSpace(e.Category) || e.Category.Length > 300 || e.Category.Any(char.IsControl)
                || e.MinimumMinutes < 1 || e.MaximumMinutes > 10080 || e.MinimumMinutes > e.Minutes || e.Minutes > e.MaximumMinutes)
            || p.CategoryEstimates.GroupBy(e => e.Category.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("Une estimation par catégorie doit avoir un minimum ≤ valeur centrale ≤ maximum, de 1 à 10 080 minutes.");
    }

    public static List<TechnicianWorkPeriod> ParsePeriods(string text) => Lines(text).Select(line =>
    {
        var v = line.Split(';', StringSplitOptions.TrimEntries);
        if (v.Length != 4 || !int.TryParse(v[0], out var day) || day is < 1 or > 7 || !TimeOnly.TryParseExact(v[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(v[2], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || !int.TryParse(v[3], out var capacity))
            throw new InvalidDataException("Plages : jour 1=lundi…7=dimanche ; début HH:mm ; fin HH:mm ; capacité en minutes.");
        return new TechnicianWorkPeriod { Day = (DayOfWeek)(day % 7), StartMinute = start.Hour * 60 + start.Minute, EndMinute = end.Hour * 60 + end.Minute, CapacityMinutes = capacity };
    }).ToList();
    public static List<TechnicianCapacityDay> ParseDays(string text) => Lines(text).Select(line =>
    {
        var v = line.Split(';', StringSplitOptions.TrimEntries);
        if (v.Length != 3 || !DateTime.TryParseExact(v[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("Exceptions : date AAAA-MM-JJ ; minutes matin ; minutes après-midi (vide = calendrier habituel).");
        int? Number(string value) => value.Length == 0 ? null : int.TryParse(value, out var n) ? n : throw new InvalidDataException("Une capacité doit être un nombre entier.");
        return new TechnicianCapacityDay { Date = date, MorningMinutes = Number(v[1]), AfternoonMinutes = Number(v[2]) };
    }).ToList();
    public static List<TechnicianCategoryEstimate> ParseEstimates(string text) => Lines(text).Select(line =>
    {
        var v = line.Split(';', StringSplitOptions.TrimEntries);
        if (v.Length != 4 || !int.TryParse(v[1], out var min) || !int.TryParse(v[2], out var mid) || !int.TryParse(v[3], out var max))
            throw new InvalidDataException("Estimations : catégorie ; minimum ; durée centrale ; maximum, en minutes.");
        return new TechnicianCategoryEstimate { Category = v[0], MinimumMinutes = min, Minutes = mid, MaximumMinutes = max };
    }).ToList();
    public static string FormatPeriods(IEnumerable<TechnicianWorkPeriod> periods) => string.Join("\n", periods.Select(p => $"{((int)p.Day == 0 ? 7 : (int)p.Day)};{p.StartMinute / 60:00}:{p.StartMinute % 60:00};{p.EndMinute / 60:00}:{p.EndMinute % 60:00};{p.CapacityMinutes}"));
    public static string FormatDays(IEnumerable<TechnicianCapacityDay> days) => string.Join("\n", days.Select(d => $"{d.Date:yyyy-MM-dd};{d.MorningMinutes};{d.AfternoonMinutes}"));
    public static string FormatEstimates(IEnumerable<TechnicianCategoryEstimate> estimates) => string.Join("\n", estimates.Select(e => $"{e.Category};{e.MinimumMinutes};{e.Minutes};{e.MaximumMinutes}"));
    private static IEnumerable<string> Lines(string text) => (text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
