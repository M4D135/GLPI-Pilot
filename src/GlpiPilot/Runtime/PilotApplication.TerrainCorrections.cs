using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record TerrainCorrectionDraft(string Token, long Revision, string SourceKey, int[] Rows, string[] TicketIds,
        string ReferenceFingerprint, string InterpretationFingerprint, string StoreFingerprint, DateTimeOffset Expires);
    private TerrainCorrectionDraft? _terrainCorrectionDraft;
    private string TerrainCorrectionReadOnlyReason => IsHistorical && !IsCachedSource
        ? "Cette archive conserve les localisations observées. Ouvrez le dernier relevé en cache ou importez une source pour corriger ses tickets." : "";

    private void RegisterTerrainCorrectionOperations()
    {
        TerrainRegister("terrain.correction.prepare", TerrainPrepareCorrection);
        TerrainRegister("terrain.correction.sites", TerrainCorrectionSites);
        TerrainRegister("terrain.correction.apply", TerrainApplyCorrection);
    }

    private string TerrainCorrectionInterpretation() => TerrainDigest(new { EffectiveSettings.Dashboard, EffectiveSettings.Cities, cached = IsCachedSource });
    private object TerrainPrepareCorrection(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var report = TerrainLocations(ct); var rows = TerrainRead<int[]>(args, "rows", 100_000);
        if (rows.Length is 0 or > 2000 || rows.Distinct().Count() != rows.Length || rows.Any(row => row < 0 || row >= report.Source.Rows.Count))
            throw new InvalidDataException("Choisissez de 1 à 2 000 tickets distincts du relevé.");
        var key = CityOverrideStore.SourceKey(report.Source); var dashboard = EffectiveSettings.Dashboard;
        var columns = DashboardService.ResolveColumns(report.Source.Headers, dashboard).ToDictionary(value => value.Role, value => value.Index);
        columns["title"] = TerrainTitleColumn(report, columns);
        string Cell(int row, string role) => columns.TryGetValue(role, out var resolved) && resolved is int column && column >= 0 && column < report.Source.Rows[row].Length ? report.Source.Rows[row][column]?.Trim() ?? "" : "";
        var counts = Enumerable.Range(0, report.Source.Rows.Count).GroupBy(row => Cell(row, "id"), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ids = rows.Select(row => Cell(row, "id")).ToArray();
        if (key.Length == 0 || ids.Any(id => id.Length == 0 || counts[id] != 1))
            throw new InvalidDataException("Chaque correction exige une source identifiable et un identifiant de ticket unique dans tout le relevé. Aucun ticket n’a été modifié.");
        var store = CityOverrideStore.Load(DataPath("city-overrides.json")); var matches = report.Matches.ToDictionary(value => value.RowIndex);
        var canRemove = ids.Any(id => store.Overrides.Any(value => value.SourceKey == key && value.TicketId == id))
            || rows.Any(row => matches.GetValueOrDefault(row)?.IsOverride == true);
        _terrainCorrectionDraft = new(Guid.NewGuid().ToString("N"), Revision, key, rows.ToArray(), ids, TerrainDigest(Settings.Cities),
            TerrainCorrectionInterpretation(), store.Fingerprint(), DateTimeOffset.UtcNow.AddMinutes(20));
        return new
        {
            token = _terrainCorrectionDraft.Token, revision = Revision, count = rows.Length, canRemove, readOnlyReason = TerrainCorrectionReadOnlyReason,
            items = rows.Select((row, index) => new { rowIndex = row, id = ids[index], title = TerrainShort(Cell(row, "title")),
                cityName = matches.GetValueOrDefault(row)?.CityName ?? "", siteName = matches.GetValueOrDefault(row)?.SiteName ?? "", isOverride = matches.GetValueOrDefault(row)?.IsOverride == true }).ToArray()
        };
    }
    private TerrainCorrectionDraft TerrainRequireCorrection(JsonElement args)
    {
        var value = _terrainCorrectionDraft;
        if (value is null || Text(args, "token", 40) != value.Token || value.Revision != Revision || value.Expires < DateTimeOffset.UtcNow
            || value.ReferenceFingerprint != TerrainDigest(Settings.Cities) || value.InterpretationFingerprint != TerrainCorrectionInterpretation()
            || value.StoreFingerprint != CityOverrideStore.Load(DataPath("city-overrides.json")).Fingerprint())
            throw new InvalidOperationException("Le relevé, le référentiel ou les corrections ont changé. Rouvrez la sélection avant de poursuivre.");
        return value;
    }
    private object TerrainCorrectionSites(JsonElement args, CancellationToken ct)
    {
        _ = TerrainRequireCorrection(args); var term = Text(args, "search", 120).Trim(); var (offset, limit) = TerrainPage(args);
        var cities = Settings.Cities.Cities.ToDictionary(city => city.Id, StringComparer.Ordinal);
        var items = Settings.Cities.Sites.Where(site => TerrainContains(site.Name, term) || site.Aliases.Any(alias => TerrainContains(alias, term))
            || TerrainContains(cities.GetValueOrDefault(site.CityId)?.Name, term))
            .OrderBy(site => site.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(site => new { value = site.Id, label = site.Name, detail = cities.GetValueOrDefault(site.CityId)?.Name ?? "Commune non renseignée" }).ToArray();
        ct.ThrowIfCancellationRequested(); return new { total = items.Length, offset, limit, items = items.Skip(offset).Take(limit).ToArray() };
    }
    private object TerrainApplyCorrection(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainRequireCorrection(args);
        if (TerrainCorrectionReadOnlyReason.Length > 0) throw new InvalidOperationException(TerrainCorrectionReadOnlyReason);
        var remove = Flag(args, "remove"); var siteId = Text(args, "siteId", 40); var reason = Text(args, "reason", 1000).Trim();
        var site = remove ? null : Settings.Cities.Sites.SingleOrDefault(value => value.Id == siteId)
            ?? throw new InvalidDataException("Choisissez un site enregistré dans le référentiel de travail.");
        return TerrainCommitCorrection(draft, remove ? null : new CityTicketOverride
            { CityId = site!.CityId, SiteId = site.Id, Reason = reason, Explicit = true }, ct);
    }
    private object TerrainCommitCorrection(TerrainCorrectionDraft draft, CityTicketOverride? correction, CancellationToken ct)
    {
        if (correction is not null && (string.IsNullOrWhiteSpace(correction.Reason) || correction.Reason.Length > 1000 || correction.Reason.Any(char.IsControl)))
            throw new InvalidDataException("Indiquez un motif lisible pour la correction (1 000 caractères au maximum).");
        var path = DataPath("city-overrides.json"); var before = CityOverrideStore.Load(path);
        if (before.Fingerprint() != draft.StoreFingerprint) throw new InvalidOperationException("Les corrections ont changé. Rouvrez la sélection.");
        var store = before.WithBatch(correction, draft.SourceKey, draft.TicketIds);
        CityEnrichmentResult report; SnapshotDocument? document = null; AppSettings? context = null;
        if (IsCachedSource)
        {
            var captured = _cachedSourceDocument ?? throw new InvalidOperationException("Le relevé source du cache est indisponible. Rechargez le dernier relevé.");
            if (correction is not null && captured.Cities.Sites.FirstOrDefault(site => site.Id == correction.SiteId) is { } capturedSite
                && capturedSite.CityId != correction.CityId)
                throw new InvalidDataException("Ce site a changé de commune depuis le relevé conservé. Réimportez la liste ou réalisez une nouvelle collecte avant de lui rattacher ces tickets.");
            var projection = CityCorrectionProjection.Create(captured, Settings.Cities, store, ct);
            document = projection?.Document ?? captured;
            report = projection?.Locations ?? captured.LocationEvidence?.ToReport(captured.Data)
                ?? new CityEnrichmentResult { Source = captured.Data, Data = captured.Data };
        }
        else
        {
            var source = TerrainLocations(ct).Source; context = EffectiveSettings; context.Cities = Settings.Cities.Copy();
            report = CityDetectionService.Enrich(source, context.Cities, context.Dashboard, ct, store.Overrides);
        }
        ct.ThrowIfCancellationRequested();
        // All tickets, IDs, references and derived results are checked before the single atomic store replacement.
        store.Save(path, draft.StoreFingerprint);
        if (document is not null) _cachedDocument = document;
        SetResult(report.Data, report, document, context);
        _terrainCorrectionDraft = null; _terrainCityPreview = null;
        Log($"LIEUX · {draft.Rows.Length:N0} correction(s) locale(s) {(correction is null ? "retirée(s)" : "enregistrée(s)")} ; cellules source conservées.");
        return new { revision = Revision, count = draft.Rows.Length, removed = correction is null };
    }
}
