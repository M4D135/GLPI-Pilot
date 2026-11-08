using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public static class NotificationPolicy
{
    public static Func<bool> CanNotify { get; set; } = () => true;
}

public static class AutomationPolicy
{
    public static IReadOnlyList<TimeOnly> Times(string text)
    {
        var values = (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length > 24) throw new InvalidDataException("Choisissez au maximum 24 horaires par jour.");
        return values.Select(value => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time : throw new InvalidDataException("Les horaires fixes utilisent HH:mm, séparés par des points-virgules.")).Distinct().Order().ToArray();
    }
    public static IReadOnlySet<int> Days(string text)
    {
        var days = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => int.TryParse(value, out var day) && day is >= 1 and <= 7 ? day : throw new InvalidDataException("Jours : 1 = lundi à 7 = dimanche, séparés par des virgules.")).ToHashSet();
        if (days.Count == 0) throw new InvalidDataException("Choisissez au moins un jour de lancement."); return days;
    }
    public static DateTimeOffset Next(AutomationSettings settings, DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local; var times = Times(settings.FixedTimes);
        if (times.Count == 0) return after.AddMinutes(settings.IntervalMinutes);
        var days = Days(settings.WeekDays); var start = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (var offset = 0; offset <= 8; offset++)
        {
            var date = start.AddDays(offset); if (!days.Contains(((int)date.DayOfWeek + 6) % 7 + 1)) continue;
            foreach (var time in times)
            {
                var local = DateTime.SpecifyKind(date + time.ToTimeSpan(), DateTimeKind.Unspecified); if (zone.IsInvalidTime(local)) continue;
                var result = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
                if (result > after) return result;
            }
        }
        throw new InvalidOperationException("Aucun horaire valide dans les huit prochains jours.");
    }
    public static bool IsQuiet(AutomationSettings settings, DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        if (!settings.QuietHours) return false;
        var hour = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).Hour;
        return settings.QuietStartHour < settings.QuietEndHour ? hour >= settings.QuietStartHour && hour < settings.QuietEndHour : hour >= settings.QuietStartHour || hour < settings.QuietEndHour;
    }
    public static void ValidateRecipe(IEnumerable<AutomationRecipeStep> recipe)
    {
        var steps = recipe.ToArray(); var active = steps.Where(step => step.Enabled).ToArray();
        if (steps.Length > 20 || active.Length == 0 || active[0].Kind != "extract" || active.Count(step => step.Kind == "extract") != 1 || active[0].Condition != "always" || active[0].MinimumRows != 0)
            throw new InvalidDataException("La recette commence par une seule extraction sans condition ; elle comporte au maximum 20 étapes.");
        foreach (var step in steps)
            if (step.Kind is not ("extract" or "quality" or "enrich" or "archive" or "csv" or "xlsx" or "alerts") || step.Condition is not ("always" or "complete" or "incomplete") || step.MinimumRows is < 0 or > 1000000)
                throw new InvalidDataException("Une étape ou condition de recette n’est pas reconnue.");
    }
    public static bool ShouldRun(AutomationRecipeStep step, ExtractionResult? result) => step.Enabled && (step.Kind == "extract" || result != null && result.Rows.Count >= step.MinimumRows && (step.Condition == "always" || step.Condition == "complete" && result.Complete || step.Condition == "incomplete" && !result.Complete));
}

public sealed class CollectionHealth
{
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class CollectionHealthStore
{
    private readonly string _path;
    public List<CollectionHealth> Entries { get; private set; } = new();
    public CollectionHealthStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataRoot, "collection-health.json");
        try { if (File.Exists(_path) && new FileInfo(_path).Length < 1024 * 1024) Entries = JsonSerializer.Deserialize<List<CollectionHealth>>(File.ReadAllText(_path))?.Where(entry => entry != null).Take(100).ToList() ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public void Record(string id, string name, bool success, string error, DateTimeOffset at)
    {
        var item = Entries.FirstOrDefault(entry => entry.SourceId == id);
        if (item == null) { item = new() { SourceId = id, Name = name }; Entries.Add(item); }
        item.LastAttempt = at; item.LastError = success ? "" : AppPaths.SafeLog(error); item.ConsecutiveFailures = success ? 0 : Math.Min(999, item.ConsecutiveFailures + 1);
        if (success) item.LastSuccess = at;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!); var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Entries.TakeLast(100))); File.Move(temp, _path, true);
    }
    public IReadOnlyList<string> Alerts(IEnumerable<SourceProfile> sources, AutomationSettings settings, DateTimeOffset now) => sources.Where(source => source.IncludedInCampaign).Select(source =>
    {
        var entry = Entries.FirstOrDefault(item => item.SourceId == source.Id);
        if (entry?.ConsecutiveFailures >= settings.FailureAlertThreshold) return source.Name + $" : {entry.ConsecutiveFailures} échecs consécutifs ; dernier essai {entry.LastAttempt:dd/MM HH:mm}.";
        if (entry?.LastSuccess == null || now - entry.LastSuccess > TimeSpan.FromHours(settings.FreshnessHours)) return source.Name + " : collecte attendue absente ou trop ancienne.";
        return "";
    }).Where(message => message.Length > 0).ToArray();
}
