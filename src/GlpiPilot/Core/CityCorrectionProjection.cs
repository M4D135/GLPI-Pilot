using System.IO;

namespace GlpiPilot.Core;

/// <summary>Projects explicit corrections over an immutable cached observation; never reruns its other rows.</summary>
public static class CityCorrectionProjection
{
    public sealed record Projection(SnapshotDocument Document, CityEnrichmentResult Locations);

    public static Projection? Create(SnapshotDocument captured, CitySettings working, CityOverrideStore store, CancellationToken ct = default)
    {
        store.Validate(); working.Validate(); ct.ThrowIfCancellationRequested();
        var source = captured.Data; var key = CityOverrideStore.SourceKey(source);
        if (key.Length == 0) return null;
        var column = DashboardService.ResolveColumns(source.Headers, captured.Dashboard).First(item => item.Role == "id").Index;
        string Id(int row) => column is int index && index >= 0 && index < source.Rows[row].Length ? source.Rows[row][index]?.Trim() ?? "" : "";
        var counts = Enumerable.Range(0, source.Rows.Count).GroupBy(Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        bool Unique(string id) => id.Length > 0 && counts.GetValueOrDefault(id) == 1;
        var corrections = store.Overrides.Where(item => item.Explicit && item.SourceKey == key && Unique(item.TicketId)).ToDictionary(item => item.TicketId, StringComparer.Ordinal);
        var removals = store.RemovedCorrections.Where(item => item.SourceKey == key && Unique(item.TicketId)).Select(item => item.TicketId).ToHashSet(StringComparer.Ordinal);
        if (corrections.Count == 0 && removals.Count == 0) return null;

        var baseline = captured.LocationEvidence?.ToReport(source);
        var capturedMatches = baseline?.Matches.ToDictionary(match => match.RowIndex) ?? new();
        var reference = captured.Cities.Copy();
        // Existing references retain their captured labels and positions for every unaffected row.
        // A new site/city may enter this derived interpretation only through an explicit correction.
        foreach (var correction in corrections.Values)
        {
            if (!reference.Cities.Any(value => value.Id == correction.CityId) && working.Cities.FirstOrDefault(city => city.Id == correction.CityId) is { } city)
                reference.Cities.Add(city.Copy());
            if (!reference.Sites.Any(value => value.Id == correction.SiteId)
                && working.Sites.FirstOrDefault(site => site.Id == correction.SiteId && site.CityId == correction.CityId) is { } site)
                reference.Sites.Add(site.Copy());
        }
        reference.Validate();
        var headers = source.Headers.ToList();
        int Column(string? name) => !string.IsNullOrEmpty(name) && headers.Count(value => value == name) == 1 ? headers.IndexOf(name) : -1;
        int Ensure(string? capturedName, string preferred)
        {
            var index = Column(capturedName); if (index >= 0) return index;
            var candidate = preferred; var suffix = 2;
            while (headers.Contains(candidate, StringComparer.Ordinal)) candidate = preferred + " (" + suffix++ + ")";
            headers.Add(candidate); return headers.Count - 1;
        }
        var cityColumn = Ensure(baseline?.CityColumnName, "Ville détectée");
        // The engine always places its state immediately after its known derived city column.
        var stateColumn = baseline is not null && Column(baseline.CityColumnName) is var originalCity && originalCity >= 0
            && originalCity + 1 < source.Headers.Count && source.Headers[originalCity + 1].StartsWith("Repérage ville", StringComparison.Ordinal)
                ? originalCity + 1 : Ensure(null, "Repérage ville");
        var siteColumn = Ensure(baseline?.SiteColumnName, "Site détecté");
        var regionColumn = Column(baseline?.RegionColumnName); var departmentColumn = Column(baseline?.DepartmentColumnName); var sectorColumn = Column(baseline?.SectorColumnName);
        int Derived(string name) => baseline is not null && Column(baseline.CityColumnName) is var anchor && anchor >= 0
            ? Enumerable.Range(anchor + 1, source.Headers.Count - anchor - 1).Where(index => source.Headers[index] == name
                || source.Headers[index].StartsWith(name + " (", StringComparison.Ordinal)).DefaultIfEmpty(-1).Last() : -1;
        var buildingColumn = Derived("Bâtiment détecté"); var territoryColumn = Derived("Hiérarchie territoriale");
        var rows = new List<string[]>(source.Rows.Count); var matches = new List<CityMatch>(source.Rows.Count);
        for (var index = 0; index < source.Rows.Count; index++)
        {
            if ((index & 255) == 0) ct.ThrowIfCancellationRequested();
            var match = capturedMatches.GetValueOrDefault(index) ?? new CityMatch(index, null, null, CityMatchState.TitleUnavailable, Array.Empty<string>())
                { Explanation = "Aucune décision de localisation n’était conservée avec cette ligne." };
            if (match.CityId is { } cityId) match = match with { CityName = captured.Cities.Cities.FirstOrDefault(city => city.Id == cityId)?.Name ?? match.CityName };
            if (match.SiteId is { } siteId) match = match with { SiteName = captured.Cities.Sites.FirstOrDefault(site => site.Id == siteId)?.Name ?? match.SiteName };
            var id = Id(index); var changed = false;
            if (corrections.TryGetValue(id, out var correction))
            {
                var valid = CityDetectionService.ApplyCorrection(match, correction, working);
                match = valid.Confidence == "manual-invalid" ? valid : CityDetectionService.ApplyCorrection(match, correction, reference);
                changed = true;
            }
            else if (removals.Contains(id) && match.IsOverride)
            {
                // Explicit removal permits inference for this row only, using its captured rules.
                var one = Copy(source, source.Headers.ToList(), new() { source.Rows[index].ToArray() });
                var inferred = CityDetectionService.Enrich(one, captured.Cities, captured.Dashboard, ct);
                match = inferred.Matches.FirstOrDefault() is { } automatic ? automatic with { RowIndex = index } :
                    new(index, null, null, CityMatchState.TitleUnavailable, Array.Empty<string>()) { Explanation = inferred.Message };
                changed = true;
            }
            var cells = new string[headers.Count]; Array.Fill(cells, ""); Array.Copy(source.Rows[index], cells, source.Rows[index].Length);
            if (changed)
            {
                cells[cityColumn] = match.CityName ?? ""; cells[siteColumn] = match.SiteName;
                cells[stateColumn] = match.State switch { CityMatchState.Matched => "Reconnue", CityMatchState.SiteOnly => "Commune non renseignée", CityMatchState.Ambiguous => "Ambiguë", CityMatchState.Unmatched => "Non repérée", _ => "Titre indisponible" };
                var city = match.CityId is null ? null : reference.Cities.FirstOrDefault(value => value.Id == match.CityId);
                if (regionColumn >= 0) cells[regionColumn] = city?.Region ?? "";
                if (departmentColumn >= 0) cells[departmentColumn] = city?.Department ?? "";
                if (sectorColumn >= 0) cells[sectorColumn] = city?.Sector ?? "";
                if (buildingColumn >= 0) cells[buildingColumn] = match.BuildingName;
                if (territoryColumn >= 0) cells[territoryColumn] = city?.TerritoryPath ?? "";
            }
            matches.Add(match); rows.Add(cells);
        }
        var data = Copy(source, headers, rows);
        var title = captured.Cities.TitleColumn.Length > 0 ? captured.Cities.TitleColumn
            : DashboardService.ResolveColumns(source.Headers, captured.Dashboard).First(item => item.Role == "title").Header ?? "";
        var report = new CityEnrichmentResult
        {
            Source = source, Data = data, Applied = true, Matches = matches, TitleColumnName = title,
            CityColumnName = headers[cityColumn], StateColumnName = headers[stateColumn], SiteColumnName = headers[siteColumn],
            RegionColumnName = baseline?.RegionColumnName ?? "", DepartmentColumnName = baseline?.DepartmentColumnName ?? "", SectorColumnName = baseline?.SectorColumnName ?? "",
            BuildingColumnName = buildingColumn >= 0 ? headers[buildingColumn] : "", TerritoryColumnName = territoryColumn >= 0 ? headers[territoryColumn] : "",
            MatchedCount = matches.Count(match => match.State == CityMatchState.Matched), UnmatchedCount = matches.Count(match => match.State == CityMatchState.Unmatched),
            AmbiguousCount = matches.Count(match => match.State == CityMatchState.Ambiguous), TitleUnavailableCount = matches.Count(match => match.State == CityMatchState.TitleUnavailable),
            SiteOnlyCount = matches.Count(match => match.State == CityMatchState.SiteOnly), SiteMatchedCount = matches.Count(match => match.SiteId is not null), ManualCount = matches.Count(match => match.IsOverride),
            Message = "Interprétation locale du relevé conservé avec les corrections explicites. Les autres localisations et les données capturées restent conservées."
        };
        var document = new SnapshotDocument
        {
            Metadata = captured.Metadata, Data = data, Dashboard = captured.Dashboard.Copy(), Cities = reference, Queries = captured.Queries.Copy(),
            LocationEvidence = SnapshotLocationEvidence.Capture(data, report), Populations = captured.Populations
        };
        return new(document, report);
    }

    private static ExtractionResult Copy(ExtractionResult source, List<string> headers, List<string[]> rows) => new()
    {
        Headers = headers, Rows = rows, SourceUrl = source.SourceUrl, Title = source.Title, ExtractedAt = source.ExtractedAt,
        Pages = source.Pages, ExpectedTotal = source.ExpectedTotal, Scope = source.Scope, Complete = source.Complete,
        Warnings = source.Warnings.ToList(), Status = source.Status, Provenance = source.Provenance, Quality = source.Quality
    };
}
