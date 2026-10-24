using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

/// <summary>Local reference drafts, reviewable detection and the terrain operation allowlist.</summary>
public sealed partial class PilotApplication
{
    private CitySettings? _terrainCityDraft;
    private int _terrainCityDraftVersion;
    private string _terrainCityBaseline = "";
    private TerrainCityPreview? _terrainCityPreview;
    private TerrainImportPreview? _terrainImportPreview;
    private CityEnrichmentResult? _terrainLocationCache;
    private long _terrainLocationRevision = -1;
    private string _terrainLocationSettings = "";

    private void RegisterTerrainOperations()
    {
        TerrainRegister("terrain.reference.state", (a, ct) => TerrainReferenceState());
        TerrainRegister("terrain.reference.list", TerrainReferenceList);
        TerrainRegister("terrain.reference.detail", (a, ct) => TerrainReferenceDetail(a));
        TerrainRegister("terrain.reference.lookup", TerrainLookup);
        TerrainRegister("terrain.reference.upsert", TerrainReferenceUpsert);
        TerrainRegister("terrain.reference.delete", TerrainReferenceDelete);
        TerrainRegister("terrain.reference.configure", TerrainReferenceConfigure);
        TerrainRegister("terrain.reference.reset", (a, ct) => { TerrainCheckDraft(a); _terrainCityDraft = null; _terrainCityPreview = null; _terrainImportPreview = null; return TerrainReferenceState(); });
        TerrainRegister("terrain.reference.simulate", TerrainSimulate);
        TerrainRegister("terrain.reference.promote", TerrainPromote);
        TerrainRegister("terrain.reference.preview", TerrainPreviewCities);
        TerrainRegister("terrain.reference.previewPage", TerrainPreviewCitiesPage);
        TerrainRegister("terrain.reference.apply", TerrainApplyCities);
        Register("terrain.reference.import", TerrainImportCitiesAsync);
        TerrainRegister("terrain.reference.importPage", TerrainImportCitiesPage);
        TerrainRegister("terrain.reference.importApply", TerrainApplyImport);
        Register("terrain.reference.export", TerrainExportCitiesAsync);
        TerrainRegister("terrain.review.list", TerrainReviewList);
        TerrainRegister("terrain.review.detail", TerrainReviewDetail);
        TerrainRegister("terrain.review.correct", TerrainCorrect);
        RegisterTerrainTourOperations();
        RegisterTerrainUpdateOperations();
    }

    private void TerrainRegister(string name, Func<JsonElement, CancellationToken, object?> handler)
        => Register(name, (args, ct) => Task.FromResult(handler(args, ct)));

    private static T TerrainRead<T>(JsonElement args, string name, int maximum = 128 * 1024)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)
            || value.GetRawText().Length > maximum) throw new InvalidDataException("Données absentes ou trop volumineuses : " + name);
        return Read<T>(value);
    }
    private static string TerrainDigest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static bool TerrainContains(string? value, string term) => term.Length == 0 || value?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true;
    private static (int Offset, int Limit) TerrainPage(JsonElement args) => (Number(args, "offset", 0, 0, 1_000_000), Number(args, "limit", 50, 1, 200));
    private static string TerrainShort(string? value, int maximum = 2000) => value is null ? "" : value.Length > maximum ? value[..maximum] + "…" : value;
    private static DateOnly TerrainDate(string value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year is >= 2000 and <= 2100
            ? date : throw new InvalidDataException("Date attendue au format AAAA-MM-JJ, entre 2000 et 2100.");

    private CitySettings TerrainDraft()
    {
        var saved = TerrainDigest(Settings.Cities);
        // An import from another page can update the saved reference while this
        // page retains a draft. Refresh an untouched copy; keep local edits and
        // let the existing conflict check protect them from an implicit merge.
        if (_terrainCityDraft is null || _terrainCityBaseline != saved && TerrainDigest(_terrainCityDraft) == _terrainCityBaseline)
        {
            _terrainCityDraft = Settings.Cities.Copy();
            _terrainCityBaseline = saved;
            _terrainCityDraftVersion++;
            _terrainCityPreview = null; _terrainImportPreview = null;
        }
        return _terrainCityDraft;
    }
    private void TerrainCheckDraft(JsonElement args)
    {
        _ = TerrainDraft();
        if (Number(args, "draftVersion", -1, -1, int.MaxValue) != _terrainCityDraftVersion)
            throw new InvalidOperationException("Le brouillon du référentiel a changé. Rechargez sa liste avant de poursuivre.");
    }
    private object TerrainReferenceState()
    {
        var draft = TerrainDraft();
        return new
        {
            revision = Revision, draftVersion = _terrainCityDraftVersion,
            dirty = TerrainDigest(draft) != _terrainCityBaseline, conflicted = _terrainCityBaseline != TerrainDigest(Settings.Cities),
            cities = draft.Cities.Count, sites = draft.Sites.Count, buildings = draft.Buildings.Count,
            sitesWithoutCity = draft.Sites.Count(site => site.CityId.Length == 0),
            configuration = new { draft.Enabled, draft.TitleColumn, draft.LocationColumn, draft.SourcePriority },
            warnings = CityDetectionService.InspectDictionary(draft).Take(100).ToArray(),
            sourceLoaded = Result is not null, historical = IsHistorical
        };
    }
    private object TerrainReplaceDraft(CitySettings value)
    {
        value.Validate(); _terrainCityDraft = value.Copy(); _terrainCityDraftVersion++;
        _terrainCityPreview = null; _terrainImportPreview = null;
        return TerrainReferenceState();
    }
    private object TerrainReferenceList(JsonElement args, CancellationToken ct)
    {
        var draft = TerrainDraft(); var kind = Text(args, "kind", 20, "city"); var term = Text(args, "search", 120).Trim(); var (offset, limit) = TerrainPage(args);
        IEnumerable<TerrainReferenceRow> rows = kind switch
        {
            "city" => draft.Cities.Select(city => new TerrainReferenceRow(city.Id, city.Name, city.TerritoryPath.Length > 0 ? city.TerritoryPath : string.Join(" / ", new[] { city.Region, city.Department, city.Sector }.Where(value => value.Length > 0)), string.Join(" ; ", city.Aliases), city.PostalCode, city.Rules.Count, city.Latitude, city.Longitude)),
            "site" => draft.Sites.Select(site => new TerrainReferenceRow(site.Id, site.Name, draft.Cities.FirstOrDefault(city => city.Id == site.CityId)?.Name ?? "Commune non renseignée", string.Join(" ; ", site.Aliases), site.Address, site.Openings.Count, site.Latitude, site.Longitude)),
            "building" => draft.Buildings.Select(building => new TerrainReferenceRow(building.Id, building.Name, draft.Sites.FirstOrDefault(site => site.Id == building.SiteId)?.Name ?? "", string.Join(" ; ", building.Aliases), building.Floor, 0, null, null)),
            _ => throw new InvalidDataException("Type de référence inconnu.")
        };
        var all = rows.Where(row => TerrainContains(row.Name, term) || TerrainContains(row.Parent, term) || TerrainContains(row.Aliases, term) || TerrainContains(row.Detail, term))
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(); ct.ThrowIfCancellationRequested();
        return new { revision = Revision, draftVersion = _terrainCityDraftVersion, total = all.Length, offset, limit, items = all.Skip(offset).Take(limit).ToArray() };
    }
    private object TerrainReferenceDetail(JsonElement args)
    {
        var draft = TerrainDraft(); var id = Text(args, "id", 40); object entry = Text(args, "kind", 20) switch
        {
            "city" => draft.Cities.FirstOrDefault(value => value.Id == id) ?? throw new InvalidDataException("Ville absente du brouillon."),
            "site" => draft.Sites.FirstOrDefault(value => value.Id == id) ?? throw new InvalidDataException("Site absent du brouillon."),
            "building" => draft.Buildings.FirstOrDefault(value => value.Id == id) ?? throw new InvalidDataException("Bâtiment absent du brouillon."),
            _ => throw new InvalidDataException("Type de référence inconnu.")
        };
        return new { revision = Revision, draftVersion = _terrainCityDraftVersion, entry };
    }
    private object TerrainLookup(JsonElement args, CancellationToken ct)
    {
        var useSaved = Flag(args, "saved"); var settings = useSaved ? EffectiveSettings.Cities : TerrainDraft();
        var kind = Text(args, "kind", 20, "city"); var term = Text(args, "search", 120).Trim(); var parent = Text(args, "parent", 400); var (offset, limit) = TerrainPage(args);
        IEnumerable<TerrainChoice> choices = kind switch
        {
            "city" => settings.Cities.Select(value => new TerrainChoice(value.Id, value.Name, value.TerritoryPath)),
            "site" => settings.Sites.Where(value => parent.Length == 0 || value.CityId == parent).Select(value => new TerrainChoice(value.Id, value.Name, settings.Cities.FirstOrDefault(city => city.Id == value.CityId)?.Name ?? "Commune non renseignée")),
            "place" => settings.Cities.Select(value => new TerrainChoice("city:" + value.Id, value.Name, "Ville"))
                .Concat(settings.Sites.Select(value => new TerrainChoice("site:" + value.Id, value.Name, settings.Cities.FirstOrDefault(city => city.Id == value.CityId)?.Name ?? "Commune non renseignée"))),
            "header" => (Locations?.Source ?? Result)?.Headers.Select(value => new TerrainChoice(value, value, "Colonne du relevé")) ?? Array.Empty<TerrainChoice>(),
            "technician" => TerrainContext(ct).Technicians.Select(value => new TerrainChoice(value.Key, value.Name, value.RowCount.ToString(CultureInfo.InvariantCulture) + " lignes")),
            _ => throw new InvalidDataException("Liste de choix inconnue.")
        };
        var items = choices.Where(value => TerrainContains(value.Label, term) || TerrainContains(value.Detail, term)).OrderBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ct.ThrowIfCancellationRequested(); return new { total = items.Length, offset, limit, items = items.Skip(offset).Take(limit).ToArray() };
    }
    private object TerrainReferenceUpsert(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args); var draft = TerrainDraft().Copy(); var kind = Text(args, "kind", 20);
        switch (kind)
        {
            case "city":
                var city = TerrainRead<CityDefinition>(args, "entry"); if (city.Id.Length == 0) city.Id = Guid.NewGuid().ToString("N");
                draft.Cities.RemoveAll(value => value.Id == city.Id); draft.Cities.Add(city); break;
            case "site":
                var site = TerrainRead<SiteDefinition>(args, "entry"); if (site.Id.Length == 0) site.Id = Guid.NewGuid().ToString("N");
                draft.Sites.RemoveAll(value => value.Id == site.Id); draft.Sites.Add(site); break;
            case "building":
                var building = TerrainRead<BuildingDefinition>(args, "entry"); if (building.Id.Length == 0) building.Id = Guid.NewGuid().ToString("N");
                draft.Buildings.RemoveAll(value => value.Id == building.Id); draft.Buildings.Add(building); break;
            default: throw new InvalidDataException("Type de référence inconnu.");
        }
        ct.ThrowIfCancellationRequested(); return TerrainReplaceDraft(draft);
    }
    private object TerrainReferenceDelete(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args); var draft = TerrainDraft().Copy(); var id = Text(args, "id", 40); var kind = Text(args, "kind", 20);
        // Linked references are never silently cascaded out of the draft.
        if (kind == "city" && draft.Sites.Any(site => site.CityId == id)) throw new InvalidDataException("Déplacez ou retirez les sites rattachés avant de supprimer cette ville.");
        if (kind == "site" && draft.Buildings.Any(building => building.SiteId == id)) throw new InvalidDataException("Déplacez ou retirez les bâtiments rattachés avant de supprimer ce site.");
        var removed = kind switch { "city" => draft.Cities.RemoveAll(value => value.Id == id), "site" => draft.Sites.RemoveAll(value => value.Id == id), "building" => draft.Buildings.RemoveAll(value => value.Id == id), _ => 0 };
        if (removed != 1) throw new InvalidDataException("La référence à retirer n’existe plus.");
        ct.ThrowIfCancellationRequested(); return TerrainReplaceDraft(draft);
    }
    private object TerrainReferenceConfigure(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args); var draft = TerrainDraft().Copy();
        draft.Enabled = Flag(args, "enabled", true); draft.TitleColumn = Text(args, "titleColumn", 120); draft.LocationColumn = Text(args, "locationColumn", 120);
        draft.SourcePriority = TerrainRead<List<string>>(args, "sourcePriority", 200);
        ct.ThrowIfCancellationRequested(); return TerrainReplaceDraft(draft);
    }
    private object TerrainSimulate(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args); var title = Text(args, "title", 20_000); var context = args.TryGetProperty("context", out _) ? TerrainRead<Dictionary<string, string>>(args, "context", 64_000) : new();
        if (context.Count > 32 || context.Any(value => !CityText.ValidText(value.Key, false) || value.Value is null || value.Value.Length > 2000)) throw new InvalidDataException("Le test accepte 32 colonnes de contexte de 2 000 caractères au maximum.");
        var match = CityDetectionService.ExplainTitle(title, TerrainDraft(), context, ct);
        return new { title, match, highlights = CityDetectionReview.Highlights(title, match.Evidence), draftVersion = _terrainCityDraftVersion };
    }
    private object TerrainPromote(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args); ct.ThrowIfCancellationRequested();
        return TerrainReplaceDraft(CityDetectionReview.Promote(TerrainDraft(), Text(args, "cityId", 40), Text(args, "term", 120), Text(args, "requiredContext", 120), Text(args, "contextColumn", 120)));
    }

    private ExtractionResult? TerrainNativeSource() => Locations is not null && ReferenceEquals(Locations.Data, Result) ? Locations.Source : Result;
    private string TerrainDetectionContext() => TerrainDigest(new { dashboard = EffectiveSettings.Dashboard, corrections = CityOverrideStore.Load(DataPath("city-overrides.json")).Overrides });
    private static int? TerrainTitleColumn(CityEnrichmentResult report, IReadOnlyDictionary<string, int?> columns)
    {
        var exact = report.Source.Headers.Select((header, index) => (header, index)).Where(value => value.header == report.TitleColumnName).ToArray();
        return exact.Length == 1 ? exact[0].index : columns.GetValueOrDefault("title");
    }
    private CityEnrichmentResult TerrainLocations(CancellationToken ct)
    {
        var source = TerrainNativeSource() ?? throw new InvalidOperationException("Chargez un relevé pour consulter les localisations.");
        if (IsHistorical && Locations is not null && ReferenceEquals(Locations.Data, Result)) return Locations;
        var effective = EffectiveSettings; var fingerprint = TerrainDigest(new { effective.Cities, effective.Dashboard });
        if (_terrainLocationRevision == Revision && _terrainLocationSettings == fingerprint && _terrainLocationCache is not null) return _terrainLocationCache;
        var overrides = CityOverrideStore.Load(DataPath("city-overrides.json"));
        var report = CityDetectionService.Enrich(source, effective.Cities, effective.Dashboard, ct, overrides.Overrides);
        ct.ThrowIfCancellationRequested(); _terrainLocationCache = report; _terrainLocationSettings = fingerprint; _terrainLocationRevision = Revision; return report;
    }
    private object TerrainPreviewCities(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args); CheckRevision(args);
        if (IsHistorical) throw new InvalidOperationException("Fermez l’archive pour appliquer le référentiel de travail. Les références conservées avec une archive restent celles de son relevé.");
        if (_terrainCityBaseline != TerrainDigest(Settings.Cities)) throw new InvalidOperationException("Le référentiel enregistré a changé depuis l’ouverture du brouillon. Rechargez-le avant de préparer l’application.");
        var source = TerrainNativeSource(); var draft = TerrainDraft(); draft.Validate(); CityEnrichmentResult? after = null;
        var comparison = new CityDetectionPreview(0, 0, Array.Empty<CityDetectionChange>());
        if (source is not null)
        {
            var corrections = CityOverrideStore.Load(DataPath("city-overrides.json")); var dashboard = EffectiveSettings.Dashboard;
            var before = CityDetectionService.Enrich(source, Settings.Cities, dashboard, ct, corrections.Overrides);
            after = CityDetectionService.Enrich(source, draft, dashboard, ct, corrections.Overrides);
            comparison = CityDetectionReview.Compare(before, after, ct);
        }
        ct.ThrowIfCancellationRequested(); _terrainCityPreview = new(Guid.NewGuid().ToString("N"), Revision, _terrainCityDraftVersion, _terrainCityBaseline, TerrainDetectionContext(), comparison, after);
        return TerrainPreviewCitiesPage(JsonSerializer.SerializeToElement(new { token = _terrainCityPreview.Token }, Json), ct);
    }
    private TerrainCityPreview TerrainRequireCityPreview(JsonElement args)
    {
        var value = _terrainCityPreview ?? throw new InvalidOperationException("Simulez le référentiel avant son application.");
        if (Text(args, "token", 40) != value.Token || value.Revision != Revision || value.DraftVersion != _terrainCityDraftVersion || value.Baseline != TerrainDigest(Settings.Cities) || value.DetectionContext != TerrainDetectionContext())
            throw new InvalidOperationException("Cet aperçu est périmé. Relancez la simulation sur le relevé actuel.");
        return value;
    }
    private object TerrainPreviewCitiesPage(JsonElement args, CancellationToken ct)
    {
        var preview = TerrainRequireCityPreview(args); var (offset, limit) = TerrainPage(args); ct.ThrowIfCancellationRequested();
        return new { token = preview.Token, revision = preview.Revision, draftVersion = preview.DraftVersion, totalRows = preview.Comparison.TotalRows,
            changedRows = preview.Comparison.ChangedRows, total = preview.Comparison.Changes.Count, offset, limit,
            items = preview.Comparison.Changes.Skip(offset).Take(limit).ToArray(), limited = preview.Comparison.ChangedRows > preview.Comparison.Changes.Count };
    }
    private object TerrainApplyCities(JsonElement args, CancellationToken ct)
    {
        var preview = TerrainRequireCityPreview(args); if (!Flag(args, "confirmed")) throw new InvalidDataException("Confirmez l’application du résultat simulé.");
        var context = EffectiveSettings; var previous = Settings; var next = Settings.Copy(); next.Cities = TerrainDraft().Copy(); next.Cities.Validate(); ct.ThrowIfCancellationRequested();
        Settings = next; try { SaveSettings(); } catch { Settings = previous; throw; }
        _terrainCityDraft = null; _terrainCityPreview = null; _terrainImportPreview = null;
        if (preview.Report is not null) { context.Cities = Settings.Cities.Copy(); SetResult(preview.Report.Data, preview.Report, context: context); }
        else Publish("terrain.reference.changed", new { revision = Revision });
        Log("VILLES · référentiel enregistré après simulation complète."); return TerrainReferenceState();
    }
    private async Task<object?> TerrainImportCitiesAsync(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args);
        var path = await Host.OpenFileAsync("Comparer un référentiel de villes, sites et bâtiments", "CSV (*.csv)|*.csv");
        if (path is null) return new { cancelled = true };
        ct.ThrowIfCancellationRequested(); if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Le référentiel CSV dépasse 8 Mio.");
        var csv = NativeCsvImportService.Read(path, "", maxRows: 8001, cancellationToken: ct);
        var result = CityReferenceService.Preview(csv, TerrainDraft(), ct); ct.ThrowIfCancellationRequested();
        _terrainImportPreview = new(Guid.NewGuid().ToString("N"), _terrainCityDraftVersion, result, Path.GetFileName(path));
        return TerrainImportCitiesPage(JsonSerializer.SerializeToElement(new { token = _terrainImportPreview.Token }, Json), ct);
    }
    private TerrainImportPreview TerrainRequireImport(JsonElement args)
    {
        var value = _terrainImportPreview ?? throw new InvalidOperationException("Choisissez le fichier CSV à comparer.");
        if (Text(args, "token", 40) != value.Token || value.DraftVersion != _terrainCityDraftVersion) throw new InvalidOperationException("Le brouillon a changé depuis ce comparatif d’import. Reprenez le fichier.");
        return value;
    }
    private object TerrainImportCitiesPage(JsonElement args, CancellationToken ct)
    {
        var value = TerrainRequireImport(args); var (offset, limit) = TerrainPage(args); ct.ThrowIfCancellationRequested();
        return new { token = value.Token, value.FileName, draftVersion = value.DraftVersion, total = value.Preview.Changes.Count, offset, limit,
            additions = value.Preview.Changes.Count(row => row.Kind == "Ajout"), modifications = value.Preview.Changes.Count(row => row.Kind == "Modification"),
            identical = value.Preview.Changes.Count(row => row.Kind == "Identique"), items = value.Preview.Changes.Skip(offset).Take(limit).ToArray() };
    }
    private object TerrainApplyImport(JsonElement args, CancellationToken ct)
    {
        var value = TerrainRequireImport(args); if (!Flag(args, "confirmed")) throw new InvalidDataException("Confirmez la fusion proposée dans le brouillon.");
        ct.ThrowIfCancellationRequested(); return TerrainReplaceDraft(value.Preview.Settings);
    }
    private async Task<object?> TerrainExportCitiesAsync(JsonElement args, CancellationToken ct)
    {
        TerrainCheckDraft(args); var data = CityReferenceService.Export(TerrainDraft());
        var path = await Host.SaveFileAsync("Exporter le référentiel", "CSV (*.csv)|*.csv", "GLPI-Referentiel-villes-sites.csv"); if (path is null) return new { cancelled = true };
        ct.ThrowIfCancellationRequested(); var result = ExportService.WriteCsv(data, path); return new { path = result.Path, rows = result.Rows };
    }

    private object TerrainReviewList(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var report = TerrainLocations(ct); var (offset, limit) = TerrainPage(args); var filter = Text(args, "filter", 20, "review"); var term = Text(args, "search", 120).Trim();
        if (filter is not ("review" or "all" or "matched" or "unmatched" or "ambiguous" or "manual")) throw new InvalidDataException("Filtre de localisation inconnu.");
        var dashboard = EffectiveSettings.Dashboard; var columns = DashboardService.ResolveColumns(report.Source.Headers, dashboard).ToDictionary(column => column.Role, column => column.Index);
        columns["title"] = TerrainTitleColumn(report, columns);
        string Cell(int index, string role) => columns.GetValueOrDefault(role) is int column && column < report.Source.Rows[index].Length ? report.Source.Rows[index][column] ?? "" : "";
        var matches = report.Matches.Where(match => filter switch { "review" => match.State is not (CityMatchState.Matched or CityMatchState.SiteOnly), "matched" => match.State is CityMatchState.Matched or CityMatchState.SiteOnly, "unmatched" => match.State is CityMatchState.Unmatched or CityMatchState.TitleUnavailable, "ambiguous" => match.State == CityMatchState.Ambiguous, "manual" => match.IsOverride, _ => true })
            .Where(match => TerrainContains(Cell(match.RowIndex, "id"), term) || TerrainContains(Cell(match.RowIndex, "title"), term) || TerrainContains(match.CityName, term) || TerrainContains(match.SiteName, term)).ToArray();
        ct.ThrowIfCancellationRequested(); return new { revision = Revision, total = matches.Length, offset, limit,
            summary = new { report.Applied, report.MatchedCount, report.SiteOnlyCount, report.UnmatchedCount, report.AmbiguousCount, report.TitleUnavailableCount, report.ManualCount, report.Message },
            items = matches.Skip(offset).Take(limit).Select(match => new { match.RowIndex, id = TerrainShort(Cell(match.RowIndex, "id"), 120), title = TerrainShort(Cell(match.RowIndex, "title")), match.CityName, match.SiteName, match.BuildingName, match.State, match.IsOverride, match.Explanation }).ToArray() };
    }
    private object TerrainReviewDetail(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); var report = TerrainLocations(ct); var row = Number(args, "row", -1, -1); var match = report.Matches.FirstOrDefault(value => value.RowIndex == row) ?? throw new InvalidDataException("Ligne absente du relevé.");
        var columns = DashboardService.ResolveColumns(report.Source.Headers, EffectiveSettings.Dashboard).ToDictionary(column => column.Role, column => column.Index);
        columns["title"] = TerrainTitleColumn(report, columns);
        string Cell(string role) => columns.GetValueOrDefault(role) is int column && column < report.Source.Rows[row].Length ? report.Source.Rows[row][column] ?? "" : "";
        var id = Cell("id").Trim(); var title = TerrainShort(Cell("title"), 20_000);
        var correction = CityOverrideStore.Load(DataPath("city-overrides.json")).Overrides.FirstOrDefault(value => value.SourceKey == CityOverrideStore.SourceKey(report.Source.SourceUrl) && value.TicketId == id);
        ct.ThrowIfCancellationRequested(); return new { revision = Revision, row, id, title, titleTruncated = Cell("title").Length > 20_000, match,
            highlights = CityDetectionReview.Highlights(title, match.Evidence), correction,
            fields = report.Source.Headers.Take(100).Select((name, index) => new { name, value = index < report.Source.Rows[row].Length ? TerrainShort(report.Source.Rows[row][index]) : "" }).ToArray() };
    }
    private object TerrainCorrect(JsonElement args, CancellationToken ct)
    {
        CheckRevision(args); if (IsHistorical) throw new InvalidOperationException("Les corrections s’appliquent au relevé courant ; fermez l’archive avant de corriger un ticket.");
        var report = TerrainLocations(ct); var source = report.Source; var row = Number(args, "row", -1, -1); if (row < 0 || row >= source.Rows.Count) throw new InvalidDataException("Ligne absente du relevé.");
        var city = Text(args, "cityId", 40); var site = Text(args, "siteId", 40); var reason = Text(args, "reason", 1000).Trim();
        var remove = city.Length == 0 && site.Length == 0;
        if (!remove && reason.Length == 0) throw new InvalidDataException("Indiquez le motif de la correction locale.");
        if (city.Length > 0 && (!Settings.Cities.Cities.Any(value => value.Id == city) || reason.Length == 0)) throw new InvalidDataException("Choisissez une ville enregistrée et indiquez le motif de la correction locale.");
        if (site.Length > 0 && !Settings.Cities.Sites.Any(value => value.Id == site && value.CityId == city)) throw new InvalidDataException("Le site choisi n’appartient pas à cette ville enregistrée.");
        var dashboard = EffectiveSettings.Dashboard; var column = DashboardService.ResolveColumns(source.Headers, dashboard).First(value => value.Role == "id").Index;
        string Id(int index) => column is int position && position < source.Rows[index].Length ? source.Rows[index][position]?.Trim() ?? "" : "";
        var ticket = Id(row); var key = CityOverrideStore.SourceKey(source.SourceUrl);
        if (key.Length == 0 || ticket.Length == 0 || Enumerable.Range(0, source.Rows.Count).Count(index => Id(index) == ticket) != 1) throw new InvalidDataException("La correction nécessite une installation GLPI et un identifiant unique dans le relevé.");
        var store = CityOverrideStore.Load(DataPath("city-overrides.json")).With(remove ? null : new CityTicketOverride { CityId = city, SiteId = site, Reason = reason }, key, ticket);
        var context = EffectiveSettings; context.Cities = Settings.Cities.Copy();
        var after = CityDetectionService.Enrich(source, context.Cities, dashboard, ct, store.Overrides); ct.ThrowIfCancellationRequested();
        store.Save(DataPath("city-overrides.json")); SetResult(after.Data, after, context: context); _terrainCityPreview = null;
        Log("VILLES · correction locale " + (remove ? "retirée" : "enregistrée") + ".");
        return new { revision = Revision, removed = remove };
    }

    private sealed record TerrainReferenceRow(string Id, string Name, string Parent, string Aliases, string Detail, int Rules, double? Latitude, double? Longitude);
    private sealed record TerrainChoice(string Value, string Label, string Detail);
    private sealed record TerrainCityPreview(string Token, long Revision, int DraftVersion, string Baseline, string DetectionContext, CityDetectionPreview Comparison, CityEnrichmentResult? Report);
    private sealed record TerrainImportPreview(string Token, int DraftVersion, CityReferencePreview Preview, string FileName);
}
