namespace GlpiPilot.Core;

public static class DashboardGeography
{
    public static void Append(DashboardSnapshot snapshot, CityEnrichmentResult report, CitySettings geography, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AppendSiteWorkload(snapshot, report, geography, ct);
        var scope = snapshot.ScopeRows.ToHashSet(); var active = snapshot.Metric("active")?.Rows.ToHashSet() ?? new HashSet<int>();
        var groups = new Dictionary<string, (string Label, double Latitude, double Longitude, List<int> Rows)>();
        foreach (var match in report.Matches.Where(m => scope.Contains(m.RowIndex) && m.State is CityMatchState.Matched or CityMatchState.SiteOnly))
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

    private static void AppendSiteWorkload(DashboardSnapshot snapshot, CityEnrichmentResult report, CitySettings geography, CancellationToken ct)
    {
        if (!report.Applied || !geography.Enabled || snapshot.Metric("active")?.Value is null)
        {
            snapshot.SiteWorkload = new() { UnavailableReason = "La reconnaissance des sites et une colonne Statut exploitable sont nécessaires." };
            return;
        }
        var scope = snapshot.ScopeRows.ToHashSet();
        var active = snapshot.Metric("active")!.Rows.ToHashSet();
        var matches = new Dictionary<int, CityMatch>();
        var iteration = 0;
        foreach (var match in report.Matches)
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            if (!active.Contains(match.RowIndex)) continue;
            if (!scope.Contains(match.RowIndex) || !matches.TryAdd(match.RowIndex, match))
            {
                snapshot.SiteWorkload = new() { UnavailableReason = "Le repérage des sites ne couvre pas exactement ce périmètre." };
                return;
            }
        }
        if (matches.Count != active.Count || report.Data.Rows.Count != snapshot.SourceRowCount)
        {
            snapshot.SiteWorkload = new() { UnavailableReason = "Le repérage des sites ne correspond pas au relevé courant." };
            return;
        }
        var rowsBySite = geography.Sites.ToDictionary(site => site.Id, _ => new List<int>(), StringComparer.Ordinal);
        var unknown = new List<int>();
        foreach (var (index, match) in matches.OrderBy(pair => pair.Key))
        {
            if ((iteration++ & 255) == 0) ct.ThrowIfCancellationRequested();
            if (match.State is CityMatchState.Matched or CityMatchState.SiteOnly
                && match.SiteId is { } siteId && rowsBySite.TryGetValue(siteId, out var rows)) rows.Add(index);
            else unknown.Add(index);
        }
        var duplicateNames = geography.Sites.GroupBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sites = geography.Sites.Select(site => new DashboardSiteWorkloadEntry("site:" + site.Id,
                duplicateNames.Contains(site.Name) ? site.Name + " · " + site.Id : site.Name, false, rowsBySite[site.Id].ToArray()))
            .OrderByDescending(site => site.Rows.Length).ThenBy(site => site.Label, StringComparer.OrdinalIgnoreCase).ToList();
        sites.Add(new("unmatched:", "Site non connu / à vérifier", true, unknown.ToArray()));
        snapshot.SiteWorkload = new()
        {
            Available = true, UnavailableReason = "", Grain = snapshot.Grain, Population = active.Count, Sites = sites,
            Explanation = "Tickets actifs reconnus dans le périmètre courant. Tous les sites configurés restent visibles, y compris à zéro. "
                + "Chaque ligne appartient à un seul site ; les sites absents ou ambigus restent dans Site non connu / à vérifier. "
                + "Les correspondances viennent du référentiel et des corrections locales, sans adresse ni coordonnée supposée. "
                + "La préconisation selon le volume est un repère de planification local, sans réservation ni calcul de capacité."
        };
    }
}
