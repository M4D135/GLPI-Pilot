using System.Globalization;
using System.IO;

namespace GlpiPilot.Core;

public static partial class InterventionCalendarService
{
    /// <summary>Resolve a stable series/ordinal identity independently of the former display date.</summary>
    public static CalendarOccurrence? PreparationOccurrence(IEnumerable<CalendarIntervention> entries, string key, CancellationToken ct = default)
    {
        var separator = key.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(key.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal < 0)
            throw new InvalidDataException("Identité de visite invalide.");
        var root = key[..separator]; CalendarOccurrence? found = null;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.SeriesId.Length > 0 ? entry.SeriesId : entry.Id) != root || ordinal < entry.OccurrenceOffset) continue;
            Validate(entry);
            var index = ordinal - entry.OccurrenceOffset;
            var original = Starts(entry, new DateTime(2100, 12, 31, 23, 59, 0), ct).Skip(index).Select(value => (DateTime?)value).FirstOrDefault();
            if (!original.HasValue) continue;
            var exception = entry.Exceptions.SingleOrDefault(value => value.OriginalStartLocal == FormatLocal(original.Value));
            var values = exception?.Replacement ?? entry;
            var start = exception?.Replacement is null ? original.Value : ParseLocal(values.StartLocal);
            var end = exception?.Replacement is null ? start + (ParseLocal(entry.EndLocal) - ParseLocal(entry.StartLocal)) : ParseLocal(values.EndLocal);
            var startUtc = ResolveLocal(start, Zone(values.TimeZoneId), values.StartOffsetMinutes);
            var endUtc = ResolveLocal(end, Zone(values.TimeZoneId), values.EndOffsetMinutes);
            var occurrence = Expand(new[] { entry }, startUtc.AddMinutes(-1), endUtc.AddMinutes(1), ct).SingleOrDefault(value => value.Key == key);
            if (occurrence is null) continue;
            if (found is not null) throw new InvalidDataException("Deux fragments du planning décrivent la même visite. Résolvez leurs versions avant de préparer le départ.");
            found = occurrence;
        }
        return found;
    }
}
