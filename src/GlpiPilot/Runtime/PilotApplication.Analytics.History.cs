using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private (string Id, SnapshotDocument Before, SnapshotDocument After, SnapshotComparison Comparison)? _analyticsComparison;
    private (string Id, SnapshotAnalyticsReport Report)? _analyticsHistoricalReport;
    private (string Id, string Before, List<SitePopulationEntry> Entries)? _analyticsPopulationPreview;

    private void RegisterAnalyticsHistory()
    {
        Register("history.list", (a, ct) => Task.FromResult<object?>(AnalyticsHistoryList(a)));
        Register("history.open", AnalyticsOpenHistoryAsync);
        Register("history.export", AnalyticsExportHistoryAsync);
        Register("history.archive", AnalyticsArchiveAsync);
        Register("history.protect", (a, ct) => { ct.ThrowIfCancellationRequested(); Snapshots.Protect(Text(a, "id", 96), Flag(a, "protect")); return Task.FromResult<object?>(new { saved = true }); });
        Register("history.compare", AnalyticsCompareAsync);
        Register("history.changes", (a, ct) => Task.FromResult<object?>(AnalyticsChanges(a)));
        Register("history.absent.references", (a, ct) => Task.FromResult<object?>(AnalyticsAbsentReferences(a, ct)));
        Register("history.analyze", AnalyticsAnalyzeAsync);
        Register("history.analysis.page", (a, ct) => Task.FromResult<object?>(AnalyticsAnalysisPage(a)));
        Register("history.analysis.rows", (a, ct) => Task.FromResult<object?>(AnalyticsAnalysisRows(a, ct)));
        Register("history.ticket", (a, ct) => Task.FromResult<object?>(Snapshots.FindTicketHistory(Text(a, "sourceKey", 200), Text(a, "ticketId", 64), Number(a, "limit", 500, 1, 500), ct)));
        Register("populations.list", (a, ct) => Task.FromResult<object?>(AnalyticsPopulations(a)));
        Register("populations.upsert", AnalyticsUpsertPopulationAsync);
        Register("populations.delete", AnalyticsDeletePopulationAsync);
        Register("populations.import.preview", AnalyticsPreviewPopulationsAsync);
        Register("populations.import.commit", AnalyticsCommitPopulationsAsync);
        Register("populations.export", AnalyticsExportPopulationsAsync);
    }
    private static object AnalyticsMetadata(SnapshotMetadata m) => new { m.Id, m.RunId, m.CapturedAt, m.SourceLabel, m.SourceKey, m.Rows, m.Columns,
        m.ExpectedTotal, m.Complete, m.SearchVerified, m.ScopeVerified, m.IdentityReliable, m.Protected, m.State, m.Method, m.ApplicationVersion, m.CompressedBytes, m.SchemaFingerprint, m.RulesFingerprint };
    private object AnalyticsHistoryList(JsonElement args)
    {
        var all = Snapshots.List(); var key = Text(args, "sourceKey", 200); var offset = Number(args, "offset"); var limit = Number(args, "limit", 100, 1, 200);
        var selected = all.Where(m => key.Length == 0 || m.SourceKey == key).ToArray();
        return new { total = selected.Length, offset, limit, rows = selected.Skip(offset).Take(limit).Select(AnalyticsMetadata).ToArray(),
            sources = all.GroupBy(m => m.SourceKey).Select(g => new { key = g.Key, label = g.First().SourceLabel + " · " + g.Count() + " relevés" }).Take(500).ToArray(),
            currentSourceKey = Result is null ? "" : SnapshotStore.SourceKey(IsSimulationInstance ? SimulationObservation() : Result), retention = Settings.History };
    }
    private async Task<object?> AnalyticsOpenHistoryAsync(JsonElement args, CancellationToken ct)
    {
        var document = await Jobs.RunAsync("analytics-history-open", true, token => Snapshots.Load(Text(args, "id", 96), token), ct);
        ct.ThrowIfCancellationRequested(); SetResult(document.Data, document.LocationEvidence?.ToReport(document.Data), document);
        _analyticsRun = null; _analyticsQueryRun = null; return new { revision = Revision, metadata = AnalyticsMetadata(document.Metadata) };
    }
    internal async Task<bool> RestoreLastHistoryOnStartupAsync(CancellationToken ct = default)
    {
        if (Result is not null || !Settings.History.RestoreLastOnStartup) return false;
        try
        {
            var latest = Snapshots.List().FirstOrDefault();
            if (latest is null) return false;
            var document = await Jobs.RunAsync("analytics-history-restore", true, token => Snapshots.Load(latest.Id, token), ct);
            ct.ThrowIfCancellationRequested();
            if (Result is not null) return false;
            SetResult(document.Data, document.LocationEvidence?.ToReport(document.Data), document);
            _analyticsRun = null; _analyticsQueryRun = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log("Le dernier relevé n’a pas pu être restauré : " + AppPaths.SafeError(error));
            return false;
        }
    }
    private Task<object?> AnalyticsArchiveAsync(JsonElement args, CancellationToken ct)
    {
        AnalyticsCheckRevision(args); RequireCurrentResult(); var source = RequireResult();
        var populations = new AnalyticsPopulationStore(DataPath("site-populations.json")).Load();
        var metadata = Snapshots.Save(source, EffectiveSettings, ct, Locations is not null && ReferenceEquals(Locations.Data, source) ? Locations : null, populations);
        Publish("history.changed", new { metadata.Id }); return Task.FromResult<object?>(AnalyticsMetadata(metadata));
    }
    private async Task<object?> AnalyticsExportHistoryAsync(JsonElement args, CancellationToken ct)
    {
        var document = Snapshots.Load(Text(args, "id", 96), ct); var csv = Text(args, "format", 10, "xlsx") == "csv";
        var path = await Host.SaveFileAsync("Exporter les données de l’archive", csv ? "CSV (*.csv)|*.csv" : "Excel (*.xlsx)|*.xlsx", "GLPI-archive-" + document.Metadata.CapturedAt.ToString("yyyyMMdd-HHmmss") + (csv ? ".csv" : ".xlsx"));
        if (path is null) return new { cancelled = true }; ct.ThrowIfCancellationRequested();
        var exported = csv ? ExportService.WriteCsv(document.Data, path, ct) : ExportService.WriteXlsx(document.Data, path, ct);
        return new { exported.Path, exported.Rows };
    }
    private async Task<object?> AnalyticsCompareAsync(JsonElement args, CancellationToken ct)
    {
        var beforeId = Text(args, "beforeId", 96); var afterId = Text(args, "afterId", 96);
        if (beforeId == afterId) throw new InvalidDataException("Choisissez deux relevés distincts dans l’ordre chronologique.");
        var result = await Jobs.RunAsync("analytics-history-compare", true, token =>
        {
            var before = Snapshots.Load(beforeId, token); var after = Snapshots.Load(afterId, token);
            if (before.Metadata.CapturedAt >= after.Metadata.CapturedAt) throw new InvalidDataException("Le relevé précédent doit être antérieur au relevé suivant.");
            return (Id: Guid.NewGuid().ToString("N"), Before: before, After: after, Comparison: SnapshotStore.Compare(before, after, token));
        }, ct);
        ct.ThrowIfCancellationRequested(); _analyticsComparison = result;
        var c = result.Comparison;
        return new { comparisonId = result.Id, c.Compatible, c.Warnings, c.AddedColumns, c.RemovedColumns, c.Added, c.Absent, c.Changed, c.Unchanged,
            c.ResolutionsObserved, c.ReopeningsObserved, c.ReassignmentsObserved, c.Truncated, c.Summary, count = c.Changes.Count,
            before = AnalyticsMetadata(result.Before.Metadata), after = AnalyticsMetadata(result.After.Metadata) };
    }
    private (string Id, SnapshotDocument Before, SnapshotDocument After, SnapshotComparison Comparison) RequireAnalyticsComparison(JsonElement args)
        => _analyticsComparison is { } value && value.Id == Text(args, "comparisonId", 96) ? value : throw new InvalidOperationException("Comparaison expirée. Relancez-la.");
    private object AnalyticsChanges(JsonElement args)
    {
        var comparison = RequireAnalyticsComparison(args).Comparison; var kind = Text(args, "kind", 50); var term = Text(args, "search", 300);
        var offset = Number(args, "offset"); var limit = Number(args, "limit", 100, 1, 200);
        var rows = comparison.Changes.Where(c => (kind.Length == 0 || c.Kind == kind) && (term.Length == 0 || new[] { c.TicketId, c.Field, c.Before, c.After, c.Kind }.Any(value => value.Contains(term, StringComparison.CurrentCultureIgnoreCase)))).ToArray();
        return new { total = rows.Length, offset, limit, kinds = comparison.Changes.Select(c => c.Kind).Distinct().ToArray(),
            rows = rows.Skip(offset).Take(limit).Select(c => new { c.Kind, c.TicketId, c.Field, before = AnalyticsClip(c.Before), after = AnalyticsClip(c.After), c.PreviousRow, c.CurrentRow }).ToArray() };
    }
    private object AnalyticsAbsentReferences(JsonElement args, CancellationToken ct)
    {
        AnalyticsCheckRevision(args); RequireCurrentResult(); var comparison = RequireAnalyticsComparison(args);
        var sourceKey = SnapshotStore.SourceKey(IsSimulationInstance ? SimulationObservation() : RequireResult());
        if (!comparison.Comparison.Compatible || sourceKey.Length == 0 || comparison.Before.Metadata.SourceKey != sourceKey)
            throw new InvalidOperationException("Chargez une recherche actuelle du même contexte confirmé pour préparer la vérification des absents.");
        var rows = comparison.Comparison.Changes.Where(c => c.Kind == "Absent du relevé").Where(c => c.PreviousRow.HasValue).Select(c => c.PreviousRow!.Value).Distinct().ToArray();
        var references = rows.Take(1000).Select(row => { ct.ThrowIfCancellationRequested(); return IsSimulationInstance ? SimulationReference(row) : TicketReferenceResolver.Resolve(comparison.Before.Data, comparison.Before.Data.Rows[row], Settings); }).ToArray();
        return new { revision = Revision, sourceKey, references, total = rows.Length, truncated = rows.Length > 1000,
            label = "Absents du relevé · " + references.Length + " références à vérifier séparément des archives" };
    }
    private async Task<object?> AnalyticsAnalyzeAsync(JsonElement args, CancellationToken ct)
    {
        var selectedId = Text(args, "snapshotId", 96); var count = Number(args, "count", 30, 1, SnapshotAnalytics.MaximumSnapshots);
        var cadence = Number(args, "cadenceHours", 24, 1, 720);
        var report = await Jobs.RunAsync("analytics-longitudinal", true, token =>
        {
            var all = Snapshots.List(); var selected = all.SingleOrDefault(m => m.Id == selectedId) ?? throw new InvalidDataException("Archive introuvable.");
            if (selected.SourceKey.Length == 0) throw new InvalidDataException("Le contexte de cette archive n’est pas confirmé.");
            var metadata = all.Where(m => m.SourceKey == selected.SourceKey && m.CapturedAt <= selected.CapturedAt).Take(count).OrderBy(m => m.CapturedAt).ToArray();
            if (metadata.Sum(m => (long)m.Rows) > SnapshotAnalytics.MaximumRows) throw new InvalidDataException("La sélection dépasse 2 millions de lignes cumulées. Réduisez le nombre de relevés.");
            var documents = new List<SnapshotDocument>();
            foreach (var item in metadata)
            {
                token.ThrowIfCancellationRequested(); documents.Add(Snapshots.Load(item.Id, token));
                Publish("analytics.progress", new { operation = "history", done = documents.Count, total = metadata.Length });
            }
            return SnapshotAnalytics.Analyze(documents, TimeSpan.FromHours(cadence), token);
        }, ct);
        ct.ThrowIfCancellationRequested(); var id = Guid.NewGuid().ToString("N"); _analyticsHistoricalReport = (id, report);
        return new { reportId = id, report.SourceKey, report.SnapshotCount, report.AnalysedAt, report.FirstObservation, report.LastObservation,
            freshnessHours = double.IsFinite(report.FreshnessHours) ? (double?)report.FreshnessHours : null,
            report.ComparablePairs, report.BrokenPairs, report.CoveragePercent, report.Warnings, report.Scenarios,
            sections = new[] { "movements", "calendar", "transfers", "cohorts", "durations", "intervals", "variations", "anomalies", "scenarios" } };
    }
    private object AnalyticsAnalysisPage(JsonElement args)
    {
        var cache = _analyticsHistoricalReport;
        if (cache is null || cache.Value.Id != Text(args, "reportId", 96)) throw new InvalidOperationException("Analyse expirée. Relancez-la.");
        var r = cache.Value.Report; var kind = Text(args, "section", 40, "movements");
        var offset = Number(args, "offset"); var limit = Number(args, "limit", 100, 1, 200);
        IEnumerable<object> values = kind switch
        {
            "movements" => r.Movements, "calendar" => r.Calendar, "transfers" => r.Transfers.Select((t, index) => (object)new { index, t.Dimension, t.Before, t.After, t.Count, tickets = t.Tickets.Take(100).ToArray(), totalTickets = t.Tickets.Length }),
            "cohorts" => r.Cohorts, "durations" => r.StatusDurations, "intervals" => r.StatusIntervals,
            "variations" => r.Variations, "anomalies" => r.Anomalies, "scenarios" => r.Scenarios,
            _ => throw new InvalidDataException("Analyse inconnue.")
        };
        return new { total = values.Count(), offset, limit, rows = values.Skip(offset).Take(limit).ToArray() };
    }
    private object AnalyticsAnalysisRows(JsonElement args, CancellationToken ct)
    {
        AnalyticsCheckRevision(args); var report = _analyticsHistoricalReport;
        if (report is null || report.Value.Id != Text(args, "reportId", 96)) throw new InvalidOperationException("Analyse expirée.");
        var source = RequireResult();
        if (report.Value.Report.SourceKey != SnapshotStore.SourceKey(IsSimulationInstance ? SimulationObservation() : source)) throw new InvalidOperationException("Chargez un relevé du même contexte pour retrouver les contributeurs présents.");
        var index = Number(args, "transferIndex", -1, -1);
        if (index < 0 || index >= report.Value.Report.Transfers.Count) throw new InvalidDataException("Flux absent.");
        var wanted = report.Value.Report.Transfers[index].Tickets.ToHashSet(StringComparer.Ordinal);
        var column = DashboardService.ResolveColumns(source.Headers, EffectiveSettings.Dashboard).Single(c => c.Role == "id").Index ?? throw new InvalidDataException("Colonne d’identifiant absente.");
        var rows = Enumerable.Range(0, source.Rows.Count).Where(row =>
        {
            if (row % 256 == 0) ct.ThrowIfCancellationRequested();
            return wanted.Contains(SnapshotAnalytics.NormalizeTicketId(column < source.Rows[row].Length ? source.Rows[row][column] : ""));
        }).ToArray();
        return AnalyticsRowPage(source, rows, args, Revision, ct);
    }

    private AnalyticsPopulationStore AnalyticsPopulationStorage() => new(DataPath("site-populations.json"));
    private object AnalyticsPopulations(JsonElement args)
    {
        var all = AnalyticsPopulationStorage().Load(); var sourceKey = Result is null ? "" : SnapshotStore.SourceKey(Result);
        var key = Text(args, "sourceKey", 200, sourceKey); var offset = Number(args, "offset"); var limit = Number(args, "limit", 100, 1, 200);
        var entries = all.Where(e => key.Length == 0 || e.SourceKey == key).ToArray();
        return new { revision = Revision, sourceKey, total = entries.Length, offset, limit, rows = entries.Skip(offset).Take(limit).ToArray(),
            sites = Settings.Cities.Sites.Select(s => new { s.Id, s.Name, s.CityId }).Take(10000).ToArray(),
            historical = HistoricalDocument is not null, note = "Références locales datées. Les archives déjà enregistrées conservent leurs propres dénominateurs." };
    }
    private static string AnalyticsPopulationKey(SitePopulationEntry e) => e.SourceKey + "|" + e.SiteId + "|" + e.Unit + "|" + e.FromDate;
    private Task<object?> AnalyticsUpsertPopulationAsync(JsonElement args, CancellationToken ct)
    {
        AnalyticsCheckRevision(args); var data = RequireResult();
        if (data.Provenance is not { SearchVerified: true, ScopeVerified: true }) throw new InvalidDataException("Un contexte de source confirmé est nécessaire pour lier un effectif.");
        var entry = AnalyticsArgument<SitePopulationEntry>(args, "entry"); var sourceKey = SnapshotStore.SourceKey(data);
        if (entry.SourceKey != sourceKey || !Settings.Cities.Sites.Any(s => s.Id == entry.SiteId)) throw new InvalidDataException("L’effectif doit utiliser ce contexte et un site du référentiel actuel.");
        var previous = args.TryGetProperty("previous", out var oldJson) && oldJson.ValueKind != JsonValueKind.Null ? Read<SitePopulationEntry>(oldJson) : null;
        var entries = AnalyticsPopulationStorage().Load().Select(e => e.Copy()).ToList();
        if (previous is not null)
        {
            var existing = entries.SingleOrDefault(e => AnalyticsPopulationKey(e) == AnalyticsPopulationKey(previous));
            if (existing is null || JsonSerializer.Serialize(existing, Json) != JsonSerializer.Serialize(previous, Json)) throw new InvalidOperationException("Cette référence a changé. Rechargez l’inventaire.");
            entries.Remove(existing);
        }
        entries.Add(entry); AnalyticsPopulationStore.Validate(entries); ct.ThrowIfCancellationRequested(); AnalyticsPopulationStorage().Save(entries);
        _analyticsRun = null; Publish("populations.changed", new { sourceKey }); return Task.FromResult<object?>(new { saved = true });
    }
    private Task<object?> AnalyticsDeletePopulationAsync(JsonElement args, CancellationToken ct)
    {
        var entry = AnalyticsArgument<SitePopulationEntry>(args, "entry"); var entries = AnalyticsPopulationStorage().Load().Select(e => e.Copy()).ToList();
        var existing = entries.SingleOrDefault(e => AnalyticsPopulationKey(e) == AnalyticsPopulationKey(entry));
        if (existing is null || JsonSerializer.Serialize(existing, Json) != JsonSerializer.Serialize(entry, Json)) throw new InvalidOperationException("Cette référence a changé. Rechargez l’inventaire.");
        entries.Remove(existing); ct.ThrowIfCancellationRequested(); AnalyticsPopulationStorage().Save(entries); _analyticsRun = null;
        Publish("populations.changed", new { entry.SourceKey }); return Task.FromResult<object?>(new { deleted = true });
    }
    private async Task<object?> AnalyticsPreviewPopulationsAsync(JsonElement args, CancellationToken ct)
    {
        var path = await Host.OpenFileAsync("Prévisualiser les effectifs datés", "Inventaire JSON (*.json)|*.json");
        if (path is null) return new { cancelled = true }; ct.ThrowIfCancellationRequested();
        var imported = AnalyticsPopulationStore.Read(path); var current = AnalyticsPopulationStorage().Load(); var merged = current.Select(e => e.Copy()).ToList(); var identical = 0; var replaced = 0;
        foreach (var e in imported)
        {
            if (merged.Any(old => JsonSerializer.Serialize(old, Json) == JsonSerializer.Serialize(e, Json))) { identical++; continue; }
            replaced += merged.RemoveAll(old => AnalyticsPopulationKey(old) == AnalyticsPopulationKey(e));
            merged.Add(e.Copy());
        }
        AnalyticsPopulationStore.Validate(merged); var id = Guid.NewGuid().ToString("N");
        _analyticsPopulationPreview = (id, JsonSerializer.Serialize(current, Json), merged);
        return new { previewId = id, added = merged.Count - current.Count, replaced, identical, total = merged.Count, rows = imported.Take(200).ToArray(), previewRows = imported.Count };
    }
    private Task<object?> AnalyticsCommitPopulationsAsync(JsonElement args, CancellationToken ct)
    {
        var preview = _analyticsPopulationPreview;
        if (preview is null || preview.Value.Id != Text(args, "previewId", 96) || preview.Value.Before != JsonSerializer.Serialize(AnalyticsPopulationStorage().Load(), Json))
            throw new InvalidOperationException("L’inventaire a changé. Prévisualisez à nouveau l’import.");
        ct.ThrowIfCancellationRequested(); AnalyticsPopulationStorage().Save(preview.Value.Entries); _analyticsPopulationPreview = null; _analyticsRun = null;
        Publish("populations.changed", null); return Task.FromResult<object?>(new { saved = true, count = preview.Value.Entries.Count });
    }
    private async Task<object?> AnalyticsExportPopulationsAsync(JsonElement args, CancellationToken ct)
    {
        var entries = AnalyticsPopulationStorage().Load(); var path = await Host.SaveFileAsync("Exporter les effectifs datés", "Inventaire JSON (*.json)|*.json", "GLPI-effectifs.json");
        if (path is null) return new { cancelled = true }; ct.ThrowIfCancellationRequested(); AnalyticsPopulationStore.Write(path, entries); return new { path, count = entries.Count };
    }
}
