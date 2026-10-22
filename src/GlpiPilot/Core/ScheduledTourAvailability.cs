using System.Globalization;
using System.IO;

namespace GlpiPilot.Core;

public static partial class ScheduledTourService
{
    internal static void ValidateAvailability(IReadOnlyList<TourTechnicianAvailability> availability)
    {
        if (availability == null || availability.Count > 448 || availability.Any(item => item == null || string.IsNullOrWhiteSpace(item.TechnicianKey) || !ScheduledTour.Text(item.TechnicianKey, 400)
            || !DateOnly.TryParseExact(item.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year is < 2000 or > 2100
            || item.CapacityMinutes is < 0 or > 1440 || item.MorningCapacityMinutes is < 0 or > 720 || item.AfternoonCapacityMinutes is < 0 or > 720
            || item.Skills == null || item.Territories == null || item.Skills.Count > 100 || item.Territories.Count > 100
            || item.Skills.Concat(item.Territories).Any(value => !CityText.ValidText(value, false))
            || item.WorkWindows != null && (item.WorkWindows.Count > 56 || item.WorkWindows.Any(window => window == null || window.FromMinute < 0 || window.UntilMinute > 1440
                || window.FromMinute >= window.UntilMinute || window.CapacityMinutes is < 0 || window.CapacityMinutes > window.UntilMinute - window.FromMinute)))
            || availability.Select(item => (item.TechnicianKey, item.Day)).Distinct().Count() != availability.Count
            || availability.Select(item => item.TechnicianKey).Distinct(StringComparer.Ordinal).Count() > 32 || availability.Select(item => item.Day).Distinct(StringComparer.Ordinal).Count() > 14)
            throw new InvalidDataException("Disponibilités : au maximum 32 techniciens sur 14 jours, sans doublon, avec plages et capacités valides.");
        foreach (var item in availability.Where(item => item.WorkWindows != null))
        {
            var windows = item.WorkWindows!.OrderBy(window => window.FromMinute).ToArray();
            for (var index = 1; index < windows.Length; index++)
                if (windows[index].FromMinute < windows[index - 1].UntilMinute) throw new InvalidDataException("Deux plages de présence d’un technicien se chevauchent.");
        }
    }
}

/// <summary>Fits indivisible travel plus visits inside declared presence and budget windows.
/// Gaps outside presence are not billed as site waiting. Missing windows remain unknown.</summary>
internal sealed class TourAvailabilityPlanner
{
    private readonly TourTechnicianAvailability _availability;
    private readonly DateTime _midnight;
    private readonly int _start, _end;
    private readonly IReadOnlyList<TourWorkWindow> _windows;
    private readonly Dictionary<int, int> _windowUse = new();
    private int _used, _morning, _afternoon;

    internal TourAvailabilityPlanner(TourTechnicianAvailability availability, DateOnly day, TimeOnly start, TimeOnly end)
    {
        _availability = availability; _midnight = day.ToDateTime(TimeOnly.MinValue); _start = start.Hour * 60 + start.Minute; _end = end.Hour * 60 + end.Minute;
        _windows = availability.WorkWindows?.OrderBy(window => window.FromMinute).ToArray() ?? new[] { new TourWorkWindow(_start, _end) };
    }

    internal bool TryFit(DateTime cursor, int travel, int duration, string earliest, SiteDefinition? site, IReadOnlyList<string>? skills,
        out DateTime arrival, out DateTime start, out DateTime finish, out int waiting)
    {
        arrival = cursor.AddMinutes(travel); start = arrival; finish = start.AddMinutes(duration); waiting = 0;
        if (_availability.Unavailable || skills?.Any(skill => !_availability.Skills.Any(known => string.Join(' ', CityText.Tokens(known)) == string.Join(' ', CityText.Tokens(skill)))) == true) return false;
        var first = Math.Max(_start, (int)Math.Ceiling((cursor - _midnight).TotalMinutes));
        var earliestMinute = earliest.Length > 0 ? (int)TimeOnly.ParseExact(earliest, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan().TotalMinutes : 0;
        for (var index = 0; index < _windows.Count; index++)
        {
            var window = _windows[index]; var from = Math.Max(first, window.FromMinute); var until = Math.Min(_end, window.UntilMinute);
            // Noon is another candidate when a declared morning budget is exhausted.
            foreach (var departure in new[] { from, Math.Max(from, 720) }.Distinct().Order())
            {
                if (departure >= until && travel + duration > 0) continue;
                var proposedArrival = _midnight.AddMinutes(departure + travel);
                var requested = _midnight.AddMinutes(Math.Max(departure + travel, earliestMinute));
                var opening = ScheduledTourService.FitSiteOpening(site, DateOnly.FromDateTime(_midnight), requested, duration);
                if (opening.Closed || !opening.Start.HasValue) continue;
                var proposedStart = opening.Start.Value; var end = (int)Math.Ceiling((proposedStart.AddMinutes(duration) - _midnight).TotalMinutes);
                if (end > until || end < departure) continue;
                var minutes = end - departure; var morning = Math.Max(0, Math.Min(end, 720) - Math.Min(departure, 720)); var afternoon = minutes - morning;
                if (_availability.CapacityMinutes.HasValue && _used + minutes > _availability.CapacityMinutes.Value
                    || _availability.MorningCapacityMinutes.HasValue && _morning + morning > _availability.MorningCapacityMinutes.Value
                    || _availability.AfternoonCapacityMinutes.HasValue && _afternoon + afternoon > _availability.AfternoonCapacityMinutes.Value
                    || window.CapacityMinutes.HasValue && _windowUse.GetValueOrDefault(index) + minutes > window.CapacityMinutes.Value) continue;
                _used += minutes; _morning += morning; _afternoon += afternoon; _windowUse[index] = _windowUse.GetValueOrDefault(index) + minutes;
                arrival = proposedArrival; start = proposedStart; finish = start.AddMinutes(duration); waiting = (int)Math.Round((start - arrival).TotalMinutes); return true;
            }
        }
        return false;
    }
}
