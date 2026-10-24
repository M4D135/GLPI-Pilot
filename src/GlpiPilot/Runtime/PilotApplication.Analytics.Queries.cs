using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record AnalyticsQueryRun(string Id, long Revision, ExtractionResult Source, AppSettings Settings,
        DashboardSnapshot Snapshot, SavedQuery Definition, QueryResult Result, Dictionary<string, string> Parameters);
    private AnalyticsQueryRun? _analyticsQueryRun;
    private (string Id, string Before, QueryPackMerge Merge)? _analyticsPackPreview;

    private void RegisterAnalyticsQueries()
    {
        Register("queries.list", (a, ct) => Task.FromResult<object?>(new
        {
            queries = Settings.Queries.Queries.Select(q => new { q.Id, q.Name, q.Category, q.Favorite, q.Description, q.BaseProbe, q.Aggregate, groups = new[] { q.GroupBy, q.GroupBySecondary, q.GroupByTertiary }.Count(g => g.Length > 0) }).ToArray(),
            bases = QueryCatalog.BaseProbes, aggregates = QueryCatalog.Aggregates.Order().ToArray(), operators = QueryCatalog.Operators.Order().ToArray(),
            functions = QueryCatalog.Functions.Order().ToArray(), dateBuckets = QueryCatalog.DateBuckets.Order().ToArray(),
            packs = QueryPackService.BuiltInPacks().Select(p => new { p.Name, p.Description, count = p.Queries.Count }).ToArray(), maximum = QueryLibrarySettings.MaximumQueries
        }));
        Register("queries.get", (a, ct) => Task.FromResult<object?>(AnalyticsSavedQuery(Text(a, "id", 96)).Copy()));
        Register("queries.fields", (a, ct) => Task.FromResult<object?>(AnalyticsQueryFields(a)));
        Register("queries.preview", AnalyticsPreviewQueryAsync);
        Register("queries.results", (a, ct) => Task.FromResult<object?>(AnalyticsQueryResults(a)));
        Register("queries.results.export", AnalyticsExportQueryResultsAsync);
        Register("queries.rows", (a, ct) => Task.FromResult<object?>(AnalyticsQueryRows(a, ct)));
        Register("queries.explain", (a, ct) => Task.FromResult<object?>(AnalyticsQueryExplain(a, ct)));
        Register("queries.save", AnalyticsSaveQueryAsync);
        Register("queries.delete", AnalyticsDeleteQueryAsync);
        Register("queries.export", AnalyticsExportQueriesAsync);
        Register("queries.import.preview", AnalyticsPreviewPackAsync);
        Register("queries.import.commit", AnalyticsCommitPackAsync);
        Register("queries.widget.add", AnalyticsAddQueryWidgetAsync);
    }
    private SavedQuery AnalyticsSavedQuery(string id) => Settings.Queries.Queries.FirstOrDefault(q => q.Id == id) ?? throw new InvalidDataException("Requête introuvable.");
    private object AnalyticsQueryFields(JsonElement args)
    {
        var source = RequireResult(); var term = Text(args, "search", 200); var offset = Number(args, "offset");
        var fields = QueryService.FieldChoices(source.Headers).Where(f => term.Length == 0 || f.Label.Contains(term, StringComparison.CurrentCultureIgnoreCase) || f.Key.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new { revision = Revision, total = fields.Length, offset, fields = fields.Skip(offset).Take(512).ToArray() };
    }
    private DashboardSnapshot AnalyticsQueryBaseline(ExtractionResult source, AppSettings settings, CancellationToken ct)
    {
        var snapshot = DashboardService.Calculate(source, settings.Dashboard, HistoricalDocument is null ? DateTimeOffset.Now : source.ExtractedAt, ct);
        var places = HistoricalDocument?.LocationEvidence?.ToReport(source) ?? Locations;
        if (places is not null && ReferenceEquals(places.Data, source))
        {
            CityDetectionService.AppendDashboardProbes(snapshot, places, ct); DashboardGeography.Append(snapshot, places, settings.Cities);
            var population = HistoricalDocument is null ? new AnalyticsPopulationStore(DataPath("site-populations.json")).LoadOrEmpty(out _) : HistoricalDocument.Populations;
            if (population is not null) AnalyticsPopulationStore.Append(snapshot, places, settings.Cities, AnalyticsPopulationSourceKey(source), population);
        }
        return snapshot;
    }
    private async Task<object?> AnalyticsPreviewQueryAsync(JsonElement args, CancellationToken ct)
    {
        AnalyticsCheckRevision(args); var source = RequireResult(); var revision = Revision; var settings = EffectiveSettings;
        var query = AnalyticsArgument<SavedQuery>(args, "query"); query.Validate();
        var parameters = args.TryGetProperty("parameters", out var p) ? Read<Dictionary<string, string>>(p) : new();
        if (parameters.Count > 12 || parameters.Any(v => v.Key.Length > 40 || v.Value is null || v.Value.Length > 500)) throw new InvalidDataException("Valeurs de paramètres invalides.");
        var result = await Jobs.RunAsync("analytics-query", true, token =>
        {
            var snapshot = AnalyticsQueryBaseline(source, settings, token);
            var evaluated = QueryService.Evaluate(source, settings.Dashboard, snapshot, query, token, parameters);
            return new AnalyticsQueryRun(Guid.NewGuid().ToString("N"), revision, source, settings, snapshot, query.Copy(), evaluated, parameters);
        }, ct);
        AnalyticsCheckRevision(args); ct.ThrowIfCancellationRequested(); _analyticsQueryRun = result;
        return new { runId = result.Id, revision, result.Result.Available, result.Result.Message, metric = AnalyticsMetric(result.Result.Metric), result.Result.UnknownRows,
            rows = result.Result.Rows.Length, groups = result.Result.Groups.Count, hierarchy = result.Result.Hierarchy.Count, result.Snapshot.CalculatedAt, source.ExtractedAt,
            source.Complete, definition = result.Definition };
    }
    private AnalyticsQueryRun RequireAnalyticsQuery(JsonElement args)
    {
        AnalyticsCheckRevision(args); var run = _analyticsQueryRun;
        if (run is null || run.Id != Text(args, "runId", 96) || run.Revision != Revision || !ReferenceEquals(run.Source, Result)
            || SnapshotStore.RulesFingerprint(run.Settings) != SnapshotStore.RulesFingerprint(EffectiveSettings))
            throw new InvalidOperationException("Cette prévisualisation est périmée. Relancez la requête.");
        return run;
    }
    private object AnalyticsQueryResults(JsonElement args)
    {
        var run = RequireAnalyticsQuery(args); var hierarchy = Flag(args, "hierarchy"); var rows = hierarchy ? run.Result.Hierarchy : run.Result.Groups;
        var offset = Number(args, "offset"); var limit = Number(args, "limit", 100, 1, 500);
        return new { total = rows.Count, offset, limit, rows = rows.Skip(offset).Take(limit).Select((g, i) => new
        { index = offset + i, g.Label, g.Path, g.Count, g.Value, g.Unit, g.Sample, g.Denominator, contributions = g.ContributionRows.Length }).ToArray() };
    }
    private object AnalyticsQueryRows(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsQuery(args); var rows = run.Result.Rows;
        if (args.TryGetProperty("groupIndex", out var groupJson))
        {
            var groups = Flag(args, "hierarchy") ? run.Result.Hierarchy : run.Result.Groups;
            if (!groupJson.TryGetInt32(out var groupIndex) || groupIndex < 0 || groupIndex >= groups.Count) throw new InvalidDataException("Groupe absent.");
            rows = Flag(args, "contributors") ? groups[groupIndex].ContributionRows : groups[groupIndex].Rows;
        }
        return AnalyticsRowPage(run.Source, rows, args, run.Revision, ct);
    }
    private object AnalyticsQueryExplain(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsQuery(args); var row = Number(args, "rowIndex", -1, -1);
        if (row < 0 || row >= run.Source.Rows.Count) throw new InvalidDataException("Ligne absente.");
        return QueryService.Explain(run.Source, run.Settings.Dashboard, run.Snapshot, run.Definition, row, ct, run.Parameters);
    }
    private async Task<object?> AnalyticsExportQueryResultsAsync(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsQuery(args);
        if (!run.Result.Available) throw new InvalidOperationException("Une requête indisponible ne peut pas être exportée comme un résultat vide.");
        var hierarchy = Flag(args, "hierarchy"); var format = Text(args, "format", 10, "xlsx");
        if (format is not ("csv" or "xlsx")) throw new InvalidDataException("Format d’export inconnu.");
        var output = new ExtractionResult { Title = run.Definition.Name, SourceUrl = run.Source.SourceUrl, ExtractedAt = run.Source.ExtractedAt, Complete = run.Source.Complete,
            Scope = "Résultat de requête · " + run.Definition.Name,
            Headers = hierarchy ? new() { "Niveau", "Groupe", "Lignes uniques", "Valeur", "Unité", "Échantillon", "Dénominateur", "Contributeurs" } : new(run.Source.Headers) };
        if (hierarchy) foreach (var group in run.Result.Hierarchy)
        {
            ct.ThrowIfCancellationRequested();
            output.Rows.Add(new[] { group.Path.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Join(" › ", group.Path),
                group.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), group.Value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                group.Unit, group.Sample.ToString(System.Globalization.CultureInfo.InvariantCulture), group.Denominator.ToString(System.Globalization.CultureInfo.InvariantCulture),
                group.ContributionRows.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        else foreach (var row in run.Result.Rows) { ct.ThrowIfCancellationRequested(); output.Rows.Add(run.Source.Rows[row].ToArray()); }
        var path = await Host.SaveFileAsync("Exporter le résultat de la requête", format.ToUpperInvariant() + " (*." + format + ")|*." + format, "GLPI-requete." + format);
        if (path is null) return new { cancelled = true }; RequireAnalyticsQuery(args); ct.ThrowIfCancellationRequested();
        var exported = format == "csv" ? ExportService.WriteCsv(output, path, ct) : ExportService.WriteXlsx(output, path, ct);
        return new { exported.Path, exported.Rows, hierarchy };
    }
    private void AnalyticsPersistQueries(QueryLibrarySettings library)
    {
        library.Validate(); var rollback = Settings.Copy(); Settings.Queries = library;
        try { SaveSettings(); } catch { Settings = rollback; throw; }
        _analyticsRun = null; _analyticsQueryRun = null;
    }
    private Task<object?> AnalyticsSaveQueryAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var query = AnalyticsArgument<SavedQuery>(args, "query"); query.Validate();
        var library = Settings.Queries.Copy(); var existing = library.Queries.FindIndex(q => q.Id == query.Id);
        if (existing < 0) library.Queries.Add(query.Copy()); else library.Queries[existing] = query.Copy(); library.Validate();
        AnalyticsPersistQueries(library);
        return Task.FromResult<object?>(new { saved = true, query.Id });
    }
    private Task<object?> AnalyticsDeleteQueryAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var query = AnalyticsSavedQuery(Text(args, "id", 96));
        var library = Settings.Queries.Copy(); library.Queries.RemoveAll(q => q.Id == query.Id); library.Validate();
        AnalyticsPersistQueries(library);
        return Task.FromResult<object?>(new { deleted = true, warning = "Les widgets qui utilisaient cette requête affichent désormais une source indisponible ; aucune requête de remplacement n’est choisie." });
    }
    private async Task<object?> AnalyticsExportQueriesAsync(JsonElement args, CancellationToken ct)
    {
        var category = Text(args, "category", 80); var pack = new QueryPack { Name = category.Length == 0 ? "Bibliothèque GLPI Pilot" : category,
            Queries = Settings.Queries.Queries.Where(q => category.Length == 0 || q.Category == category).Select(q => q.Copy()).ToList() }; QueryPackService.Validate(pack);
        var path = await Host.SaveFileAsync("Exporter les requêtes", "Pack JSON (*.json)|*.json", "GLPI-requetes.json");
        if (path is null) return new { cancelled = true }; ct.ThrowIfCancellationRequested(); QueryPackService.Write(pack, path); return new { path, count = pack.Queries.Count };
    }
    private async Task<object?> AnalyticsPreviewPackAsync(JsonElement args, CancellationToken ct)
    {
        QueryPack pack; var builtIn = Text(args, "builtIn", 120);
        if (builtIn.Length > 0) pack = QueryPackService.BuiltInPacks().FirstOrDefault(p => p.Name == builtIn) ?? throw new InvalidDataException("Pack métier inconnu.");
        else
        {
            var path = await Host.OpenFileAsync("Prévisualiser un pack de requêtes", "Pack JSON (*.json)|*.json");
            if (path is null) return new { cancelled = true }; ct.ThrowIfCancellationRequested(); pack = QueryPackService.Read(path);
        }
        var merge = QueryPackService.PreviewMerge(Settings.Queries, pack); var id = Guid.NewGuid().ToString("N");
        _analyticsPackPreview = (id, JsonSerializer.Serialize(Settings.Queries, Json), merge);
        return new { previewId = id, pack.Name, pack.Description, merge.Added, merge.Identical, merge.Conflicts, merge.Names };
    }
    private Task<object?> AnalyticsCommitPackAsync(JsonElement args, CancellationToken ct)
    {
        var preview = _analyticsPackPreview;
        if (preview is null || preview.Value.Id != Text(args, "previewId", 96) || preview.Value.Before != JsonSerializer.Serialize(Settings.Queries, Json))
            throw new InvalidOperationException("L’aperçu d’import est périmé. Prévisualisez à nouveau le pack.");
        ct.ThrowIfCancellationRequested(); AnalyticsPersistQueries(preview.Value.Merge.Library.Copy()); _analyticsPackPreview = null;
        return Task.FromResult<object?>(new { added = preview.Value.Merge.Added });
    }
    private Task<object?> AnalyticsAddQueryWidgetAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var query = AnalyticsSavedQuery(Text(args, "queryId", 96)); var personal = Flag(args, "personal");
        var layout = AnalyticsLayout(personal).Copy(); var board = layout.Boards.FirstOrDefault(b => b.Id == Text(args, "boardId", 96, layout.ActiveBoardId)) ?? throw new InvalidDataException("Dashboard absent.");
        var grouped = Flag(args, "grouped") && query.GroupBy.Length > 0;
        board.Widgets.Add(new() { Kind = grouped ? "distribution" : "metric", SourceKey = grouped ? QueryService.DistributionKey(query.Id) : QueryService.MetricKey(query.Id), Title = query.Name });
        AnalyticsPersistLayout(personal, layout); return Task.FromResult<object?>(new { added = true, boardId = board.Id });
    }
}
