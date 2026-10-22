namespace GlpiPilot.Core;

public static class DashboardGeography
{
    public static void Append(DashboardSnapshot snapshot, CityEnrichmentResult report, CitySettings geography)
    {
        var scope = snapshot.ScopeRows.ToHashSet(); var active = snapshot.Metric("active")?.Rows.ToHashSet() ?? new HashSet<int>();
        var groups = new Dictionary<string, (string Label, double Latitude, double Longitude, List<int> Rows)>();
        foreach (var match in report.Matches.Where(m => scope.Contains(m.RowIndex) && m.State == CityMatchState.Matched))
        {
            var site = geography.Sites.FirstOrDefault(s => s.Id == match.SiteId);
            var city = geography.Cities.FirstOrDefault(c => c.Id == match.CityId);
            var siteLocated = site?.Latitude is double slat && double.IsFinite(slat) && slat is >= -90 and <= 90 && site.Longitude is double slon && double.IsFinite(slon) && slon is >= -180 and <= 180;
            var latitude = siteLocated ? site!.Latitude : city?.Latitude; var longitude = siteLocated ? site!.Longitude : city?.Longitude;
            if (latitude is not double lat || longitude is not double lon || !double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180) continue;
            var key = (siteLocated ? "site:" + site!.Id : "city:" + city!.Id);
            var label = (siteLocated ? site!.Name + " · site" : city!.Name + " · ville") + " · " + key.Split(':')[1][..8];
            if (!groups.TryGetValue(key, out var group)) group = (label, lat, lon, new List<int>());
            group.Rows.Add(match.RowIndex); groups[key] = group;
        }
        foreach (var pair in groups.OrderBy(x => x.Value.Label, StringComparer.CurrentCultureIgnoreCase))
            snapshot.Geography.Add(new(pair.Key, pair.Value.Label, pair.Value.Latitude, pair.Value.Longitude, pair.Value.Rows.Distinct().Order().ToArray(), pair.Value.Rows.Where(active.Contains).Distinct().Order().ToArray()));
        foreach (var mode in new[] { "all", "active" })
        {
            var buckets = snapshot.Geography.Select(p => { var rows = mode == "active" ? p.ActiveRows : p.Rows; return new DashboardBucket(p.Label, rows.Length, rows); }).ToArray();
            snapshot.Distributions.Add(new("geography-points-" + mode, "Localisations cartographiées · " + (mode == "active" ? "actifs" : "tous"), mode == "all" || snapshot.Metric("active")?.Value.HasValue == true,
                mode == "active" ? active.Count : snapshot.RowCount, "Coordonnées fournies dans le référentiel local. Point du site si disponible, sinon de la ville ; aucune position précise n’est déduite. Chaque ligne est rattachée à un seul point.", buckets));
        }
    }
}
