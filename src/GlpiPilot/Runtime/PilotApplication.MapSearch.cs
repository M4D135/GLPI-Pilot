using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    // Transfer exact source coordinates, not a textual city match or a truncated tour basket.
    private object MapWorkspaceSearch(JsonElement args, CancellationToken ct)
    {
        var view = MapWorkspaceRequire(args);
        var keys = MapWorkspaceStrings(args, "placeKeys", CitySettings.MaximumSites + 1000, 80).ToHashSet(StringComparer.Ordinal);
        var reference = EffectiveSettings.Cities;
        string[] Expand(string key)
        {
            // A city represents its own approximate point AND its known sites.
            var city = key.StartsWith("city:", StringComparison.Ordinal) ? reference.Cities.FirstOrDefault(city => "city:" + city.Id == key) : null;
            if (city is null) return view.Locations.Any(location => location.Key == key) ? new[] { key } : Array.Empty<string>();
            var sites = reference.Sites.Where(site => site.CityId == city.Id).Select(site => "site:" + site.Id).ToHashSet(StringComparer.Ordinal);
            return view.Locations.Where(location => location.Key == key || sites.Contains(location.Key)).Select(location => location.Key).ToArray();
        }
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var places = new List<object>();
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            var group = Expand(key);
            if (group.Length == 0) throw new InvalidDataException("Un lieu sélectionné n’appartient plus à cette carte. Actualisez-la.");
            expanded.UnionWith(group);
            var city = reference.Cities.FirstOrDefault(city => "city:" + city.Id == key);
            var location = view.Locations.FirstOrDefault(location => location.Key == key);
            places.Add(new { key, label = city?.Name ?? location!.Place.Label });
        }
        var rows = new List<int>();
        foreach (var ticket in view.Tickets)
        {
            ct.ThrowIfCancellationRequested();
            if (keys.Count == 0 || expanded.Contains(ticket.PlaceKey)) rows.Add(ticket.RowIndex);
        }
        int? focusedRow = null; string? focusedPlaceKey = null;
        if (args.TryGetProperty("rowIndex", out var rowValue))
        {
            if (!rowValue.TryGetInt32(out var rowIndex)) throw new InvalidDataException("La ligne du ticket est invalide.");
            var ticket = view.Tickets.FirstOrDefault(ticket => ticket.RowIndex == rowIndex);
            if (ticket is not null) { focusedRow = rowIndex; focusedPlaceKey = ticket.PlaceKey; }
        }
        ct.ThrowIfCancellationRequested();
        return new { revision = view.Revision, indices = rows.ToArray(), total = rows.Count,
            baseIndices = view.Tickets.Select(ticket => ticket.RowIndex).ToArray(), places, focusedRow, focusedPlaceKey };
    }
}
