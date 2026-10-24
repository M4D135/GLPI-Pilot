using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed class AnalyticsRun
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public required long Revision { get; init; }
        public required string Rules { get; init; }
        public required ExtractionResult Source { get; init; }
        public required AppSettings Settings { get; init; }
        public required DashboardSnapshot Baseline { get; init; }
        public required DashboardSnapshot Current { get; init; }
        public DashboardSnapshot? Comparison { get; init; }
        public required DashboardBoardSettings Board { get; init; }
        public required List<DashboardScopeFilter> Common { get; init; }
        public required Dictionary<string, DashboardSnapshot> Widgets { get; init; }
        public required SnapshotScopedHistory History { get; init; }
        public required string TechnicianKey { get; init; }
        public required bool SourceTicketIdentityReliable { get; init; }
        public required bool SourceStatusesKnown { get; init; }
        public bool IsLocalFileTest { get; init; }
        public bool TestFileReadComplete { get; init; }
        public bool HistoryLoaded { get; set; }
    }
    private AnalyticsRun? _analyticsRun;
    private sealed class AnalyticsLayoutJournal
    {
        public string Expected { get; set; } = "";
        public List<DashboardLayoutSettings> Undo { get; } = new();
        public List<DashboardLayoutSettings> Redo { get; } = new();
    }
    private readonly Dictionary<bool, AnalyticsLayoutJournal> _analyticsLayoutJournals = new();

    private void RegisterAnalyticsOperations()
    {
        Register("dashboard.get", AnalyticsDashboardAsync);
        Register("dashboard.history", AnalyticsDashboardHistoryAsync);
        Register("dashboard.catalog", (a, ct) => Task.FromResult<object?>(AnalyticsCatalog(RequireAnalyticsRun(a).Baseline)));
        Register("dashboard.layout.get", (a, ct) => Task.FromResult<object?>(AnalyticsBusinessKey(a) is { Length: > 0 } key ? AnalyticsBusinessLayout(key, ct) : AnalyticsLayout(Flag(a, "personal")).Copy()));
        Register("dashboard.layout.save", AnalyticsSaveLayoutAsync);
        Register("dashboard.layout.undo", (a, ct) => AnalyticsMoveLayoutAsync(a, ct, false));
        Register("dashboard.layout.redo", (a, ct) => AnalyticsMoveLayoutAsync(a, ct, true));
        Register("dashboard.board.create", AnalyticsCreateBoardAsync);
        Register("dashboard.layout.export", AnalyticsExportLayoutAsync);
        Register("dashboard.layout.import", AnalyticsImportLayoutAsync);
        Register("dashboard.layout.append", AnalyticsAppendLayoutAsync);
        Register("dashboard.rows", (a, ct) => Task.FromResult<object?>(AnalyticsRows(a, ct)));
        Register("dashboard.copyIds", AnalyticsCopyIdsAsync);
        Register("dashboard.data", (a, ct) => Task.FromResult<object?>(AnalyticsDataPage(a)));
        Register("reports.export", AnalyticsExportReportAsync);
        RegisterAnalyticsQueries();
        RegisterAnalyticsHistory();
        RegisterAnalyticsAssistant();
        RegisterAnalyticsBusinessOperations();
    }

    private DashboardLayoutSettings AnalyticsLayout(bool personal) => personal ? Settings.Dashboard.PersonalLayout : Settings.Dashboard.Layout;
    private void AnalyticsCheckRevision(JsonElement args)
    {
        if (!args.TryGetProperty("revision", out var revision) || !revision.TryGetInt64(out var value) || value != Revision)
            throw new InvalidOperationException("Le relevé a changé. Rechargez la vue avant de poursuivre.");
    }
    private AnalyticsRun RequireAnalyticsRun(JsonElement args)
    {
        AnalyticsCheckRevision(args);
        var run = _analyticsRun;
        if (run is null || run.Id != Text(args, "runId", 96) || run.Revision != Revision || !ReferenceEquals(run.Source, Result)
            || run.Rules != SnapshotStore.RulesFingerprint(EffectiveSettings))
            throw new InvalidOperationException("Ce calcul n’est plus actif. Recalculez le tableau de bord.");
        return run;
    }
    private static T AnalyticsArgument<T>(JsonElement args, string name) => args.TryGetProperty(name, out var value)
        ? Read<T>(value) : throw new InvalidDataException("Argument requis : " + name);
    private static string AnalyticsClip(string? value, int maximum = 4000)
    {
        value ??= "";
        if (value.Length <= maximum) return value;
        var length = maximum > 0 && char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum;
        return value[..length] + "…";
    }

    private async Task<object?> AnalyticsDashboardAsync(JsonElement args, CancellationToken ct)
    {
        var businessView = AnalyticsBusinessKey(args);
        if (businessView == "sites" && !IsHistorical && Result is not null && Locations is null)
        {
            var context = EffectiveSettings; var report = TerrainLocations(ct); SetResult(report.Data, report, context: context);
        }
        var source = RequireResult(); var revision = Revision; var settings = EffectiveSettings;
        var technician = Text(args, "technicianKey", 300); var personal = technician.Length > 0;
        var layout = businessView.Length > 0 ? AnalyticsBusinessLayout(businessView, ct) : AnalyticsLayout(personal).Copy(); layout.Normalize(); layout.Validate();
        var boardId = Text(args, "boardId", 96, layout.ActiveBoardId);
        var board = layout.Boards.FirstOrDefault(b => b.Id == boardId)?.Copy() ?? throw new InvalidDataException("Tableau de bord introuvable.");
        var cross = args.TryGetProperty("crossFilters", out var crossJson) ? Read<List<DashboardScopeFilter>>(crossJson) : new();
        var common = DashboardScopeFilter.CopyList(board.Filters.Concat(cross));
        if (businessView.Length > 0) AnalyticsBusinessFocus(args, common, ct);
        DashboardScopeFilter.ValidateList(common);
        var historical = HistoricalDocument; var locations = historical?.LocationEvidence?.ToReport(source) ?? Locations;
        var historySource = IsSimulationInstance && historical is null ? SimulationObservation() : source;
        if (locations is not null && !ReferenceEquals(locations.Data, source)) locations = null;
        string? populationWarning = null;
        var populations = historical is not null ? historical.Populations ?? new() : new AnalyticsPopulationStore(DataPath("site-populations.json")).LoadOrEmpty(out populationWarning);
        var result = await Jobs.RunAsync("analytics-dashboard", true, token =>
        {
            var at = historical is null ? DateTimeOffset.Now : source.ExtractedAt;
            var baseline = personal ? TechnicianDashboardService.CalculateForTechnician(source, settings.Dashboard, technician, at, token)
                : DashboardService.Calculate(source, settings.Dashboard, at, token);
            // A filtered card must not hide an uncertain source row and thereby certify
            // the remaining count. Personal layouts also retain the unfiltered evidence.
            var weatherSource = personal && board.Widgets.Any(widget => !widget.Hidden && widget.Kind == "metric" && widget.Presentation == "weather-volume")
                ? DashboardService.Calculate(source, settings.Dashboard, at, token) : baseline;
            void Append(DashboardSnapshot snapshot)
            {
                if (!string.IsNullOrEmpty(populationWarning)) snapshot.Warnings.Add(populationWarning);
                if (locations is not null)
                {
                    CityDetectionService.AppendDashboardProbes(snapshot, locations, token);
                    DashboardGeography.Append(snapshot, locations, settings.Cities, token);
                    if (historical is null || historical.Populations is not null)
                        AnalyticsPopulationStore.Append(snapshot, locations, settings.Cities, AnalyticsPopulationSourceKey(source), populations);
                }
                QueryService.AppendDashboardQueries(snapshot, source, settings.Dashboard, settings.Queries, token);
                DashboardService.RefreshWeatherProfiles(snapshot, settings.Dashboard);
            }
            Append(baseline);
            DashboardSnapshot Calculate(int[] rows, string label)
            {
                var snapshot = DashboardService.CalculateSubset(source, settings.Dashboard, rows, label, at, token); Append(snapshot); return snapshot;
            }
            var scope = DashboardService.ResolveScope(baseline, common, token);
            var current = common.Count == 0 ? baseline : Calculate(scope, AnalyticsDescribe(common));
            var widgets = new Dictionary<string, DashboardSnapshot>(StringComparer.Ordinal);
            var cache = new Dictionary<string, DashboardSnapshot>(StringComparer.Ordinal);
            foreach (var widget in board.Widgets.Where(w => !w.Hidden))
            {
                token.ThrowIfCancellationRequested();
                if (widget.Filters.Count == 0) { widgets[widget.Id] = current; continue; }
                var signature = JsonSerializer.Serialize(widget.Filters, Json);
                if (!cache.TryGetValue(signature, out var filtered))
                {
                    var rows = DashboardService.ResolveScope(baseline, widget.Filters, token).Intersect(scope).Order().ToArray();
                    filtered = Calculate(rows, AnalyticsDescribe(common.Concat(widget.Filters))); cache[signature] = filtered;
                }
                widgets[widget.Id] = filtered;
            }
            var comparison = board.ComparisonEnabled ? board.ComparisonFilters.Count == 0 ? baseline
                : Calculate(DashboardService.ResolveScope(baseline, board.ComparisonFilters, token), "Comparaison · " + AnalyticsDescribe(board.ComparisonFilters)) : null;
            return new AnalyticsRun { Revision = revision, Source = source, Settings = settings, Rules = SnapshotStore.RulesFingerprint(settings),
                Baseline = baseline, Current = current, Comparison = comparison, Board = board, Common = common, Widgets = widgets,
                TechnicianKey = technician, History = new(Snapshots, historySource, settings, baseline, technician, token),
                SourceTicketIdentityReliable = weatherSource.Grain == "tickets", SourceStatusesKnown = weatherSource.Metric("unknown-status")?.Value == 0,
                IsLocalFileTest = IsTestMode, TestFileReadComplete = _localFileTest?.FileReadComplete == true };
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (Revision != revision || !ReferenceEquals(source, Result)) throw new OperationCanceledException("Le relevé a changé.", ct);
        _analyticsRun = result;
        var journal = businessView.Length > 0 ? AnalyticsBusinessJournal(businessView, layout) : AnalyticsJournal(personal);
        return new { runId = result.Id, revision, layout, layoutHistory = new { undo = journal.Undo.Count, redo = journal.Redo.Count }, boardId, businessView, technicianKey = technician, snapshot = AnalyticsSummary(result.Current),
            comparison = result.Comparison is null ? null : AnalyticsSummary(result.Comparison),
            widgets = board.Widgets.Where(w => !w.Hidden).Select(w => AnalyticsWidget(result, w)).ToArray(),
            catalog = AnalyticsCatalog(result.Baseline), commonFilters = common, historical = historical is not null,
            simulation = result.IsLocalFileTest, simulationFileReadComplete = result.TestFileReadComplete,
            sourceIdentityReliable = result.SourceTicketIdentityReliable, sourceStatusesKnown = result.SourceStatusesKnown,
            sourceKey = SnapshotStore.SourceKey(source), roster = TechnicianDashboardService.Detect(source, settings.Dashboard, ct).Technicians.Select(t => new { t.Key, t.Name, count = t.Rows.Length, t.ActiveCount }).Take(2000).ToArray() };
    }

    private async Task<object?> AnalyticsDashboardHistoryAsync(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsRun(args);
        if (run.IsLocalFileTest && !IsSimulationInstance)
            return new { runId = run.Id, revision = run.Revision, widgets = run.Board.Widgets.Where(w => !w.Hidden && (w.Kind == "history" || w.Kind == "metric" && w.ShowMiniHistory))
                .Select(w => new { id = w.Id, history = (object?)null, comparison = (object?)null }).ToArray() };
        if (!run.HistoryLoaded)
        {
            var requests = new List<DashboardHistoryScope> { new("current", run.Common, Array.Empty<DashboardScopeFilter>()) };
            foreach (var widget in run.Board.Widgets.Where(w => !w.Hidden && w.Filters.Count > 0)) requests.Add(new("widget:" + widget.Id, run.Common, widget.Filters));
            if (run.Comparison is not null) requests.Add(new("comparison", run.Board.ComparisonFilters, Array.Empty<DashboardScopeFilter>()));
            var histories = await Jobs.RunAsync("analytics-mini-history", true, token => run.History.Calculate(requests, token), ct);
            RequireAnalyticsRun(args); ct.ThrowIfCancellationRequested();
            void Set(DashboardSnapshot snapshot, string key) { snapshot.History.Clear(); snapshot.History.AddRange(histories[key]); }
            Set(run.Current, "current");
            foreach (var widget in run.Board.Widgets.Where(w => !w.Hidden && w.Filters.Count > 0)) Set(run.Widgets[widget.Id], "widget:" + widget.Id);
            if (run.Comparison is not null && !ReferenceEquals(run.Comparison, run.Current)) Set(run.Comparison, "comparison");
            run.HistoryLoaded = true;
        }
        return new { runId = run.Id, revision = run.Revision,
            widgets = run.Board.Widgets.Where(w => !w.Hidden && (w.Kind == "history" || w.Kind == "metric" && w.ShowMiniHistory))
                .Select(w => AnalyticsHistoryWidget(run, w)).ToArray() };
    }

    private static object AnalyticsHistoryWidget(AnalyticsRun run, DashboardWidgetSettings widget)
    {
        var history = run.Widgets[widget.Id].History.FirstOrDefault(h => h.Key == widget.SourceKey);
        if (history is null || history.Points.Count == 0) return new { id = widget.Id, history, comparison = (object?)null };
        var end = history.Points.Max(p => p.CapturedAt); var middle = end.AddDays(-widget.HistoryPeriodDays); var start = middle.AddDays(-widget.HistoryPeriodDays);
        var points = history.Points.Where(p => p.CapturedAt >= start && p.CapturedAt <= end).ToArray();
        var valid = points.Where(p => p.Comparable && p.Complete && p.Value is double v && double.IsFinite(v)).ToArray();
        var before = valid.Where(p => p.CapturedAt < middle).Select(p => p.Value!.Value).ToArray(); var after = valid.Where(p => p.CapturedAt >= middle).Select(p => p.Value!.Value).ToArray();
        double? previous = before.Length == 0 ? null : before.Average(), current = after.Length == 0 ? null : after.Average();
        return new { id = widget.Id, history = widget.Kind == "metric" ? history with { Points = points } : history,
            comparison = new { periodDays = widget.HistoryPeriodDays, start, middle, end, beforeSamples = before.Length, afterSamples = after.Length,
                breaks = points.Length - valid.Length, before = previous, after = current, delta = previous.HasValue && current.HasValue ? current - previous : null,
                note = "Moyennes des observations disponibles, sans interpolation ni pondération temporelle. Cadence et ruptures influencent la représentativité ; aucune différence d’événements n’est inférée." } };
    }

    private static string AnalyticsDescribe(IEnumerable<DashboardScopeFilter> filters) => string.Join(" · ", filters.Select(f =>
        (f.Exclude ? "sauf " : "") + f.SourceKey + (f.Kind == "metric" ? "" : " = " + string.Join(" OU ", f.Selections()) + (f.Kind == "date" ? " → " + f.Value2 : "")
            + (f.IncludeDescendants ? " (sous-catégories incluses)" : ""))));
    private static object AnalyticsMetric(DashboardMetric m) => AnalyticsMetric(m, null, true);
    private static object AnalyticsMetric(DashboardMetric m, DashboardWidgetSettings? widget, bool complete, bool simulation = false) => new
    {
        m.Key, m.Label, m.Value, m.Unit, explanation = AnalyticsClip(m.Explanation), m.Sample, m.Tone, count = m.Rows.Length,
        volumeWeather = widget?.Presentation == "weather-volume" ? AnalyticsVolumeWeather(widget, m.Value, complete, simulation) : null
    };
    private static object AnalyticsVolumeWeather(DashboardWidgetSettings widget, double? value, bool complete, bool simulation)
    {
        var weather = widget.EvaluateVolumeWeather(value, complete);
        return new { weather.State, title = simulation ? "Simulation · " + weather.Title : weather.Title,
            explanation = simulation ? "Simulation des seuils sur le fichier local ; aucune couverture GLPI n’est confirmée. " + weather.Explanation : weather.Explanation, simulation };
    }
    private static bool AnalyticsVolumeWeatherReliable(AnalyticsRun run, DashboardSnapshot snapshot, DashboardWidgetSettings widget)
    {
        // Every stock weather must retain the unfiltered source's status evidence,
        // including pending, old tickets and incident/request subsets.
        var identities = run.SourceTicketIdentityReliable && snapshot.Grain == "tickets" && run.SourceStatusesKnown;
        if (run.IsLocalFileTest) return run.TestFileReadComplete && identities;
        return identities && snapshot.ReceptionComplete && snapshot.Quality?.ScopeStable != false && snapshot.Quality?.SampleIdentityVerified != false
            && (!snapshot.SourceExpectedTotal.HasValue || snapshot.SourceExpectedTotal == snapshot.SourceRowCount);
    }
    private static object AnalyticsSummary(DashboardSnapshot s) => new { s.CalculatedAt, s.ExtractedAt, s.RowCount, s.ExpectedTotal, s.Complete, s.ReceptionComplete, s.Quality,
        s.IsFiltered, s.ScopeLabel, s.SourceRowCount, s.SourceExpectedTotal, sourceUrl = AppPaths.SafeUrl(s.SourceUrl), s.Scope, s.Grain,
        columns = s.Columns, metrics = s.Metrics.Select(AnalyticsMetric).ToArray(), warnings = s.Warnings.Take(100).Select(w => AnalyticsClip(w)).ToArray(), weather = AnalyticsWeather(s.Weather) };
    private static object AnalyticsWeather(DashboardWeather w) => new { w.State, w.Title, w.Symbol, w.Score, w.MeasuredWeight, description = AnalyticsClip(w.Description),
        signals = w.Signals.Select(s => new { s.Label, s.Count, s.Sample, s.Population, s.Weight, explanation = AnalyticsClip(s.Explanation) }).ToArray() };
    private static object AnalyticsCatalog(DashboardSnapshot s) => new
    {
        metrics = s.Metrics.Select(m => new { m.Key, m.Label, available = m.Value.HasValue, m.Unit }).ToArray(),
        distributions = s.Distributions.Select(d => new { d.Key, label = d.Title, d.Available, count = d.Buckets.Count }).ToArray(),
        matrices = s.Matrices.Select(d => new { d.Key, label = d.Title, d.Available }).ToArray(),
        numeric = s.NumericSeries.Select(d => new { d.Key, label = d.Title, available = d.Values.Count > 0 }).ToArray(),
        boxes = s.BoxSeries.Select(d => new { d.Key, label = d.Title, d.Available }).ToArray(),
        scatters = s.Scatters.Select(d => new { d.Key, label = d.Title, available = d.Points.Count > 0 }).ToArray(),
        weather = s.WeatherProfiles.Select(d => new { key = d.Key, label = d.Value.Title }).ToArray(),
        oldest = new[] {
            new { key = "groups", label = "Plus anciens · 5 par groupe", available = s.OldestGroups?.Available == true },
            new { key = "tickets", label = "Plus anciens · 10 du périmètre", available = s.OldestGroups?.Available == true } },
        participation = new[] {
            new { key = "authors", label = "Participation des rédacteurs · suivis et tâches", available = s.Participation.StatusAvailable && (s.Participation.FollowupsAvailable || s.Participation.TasksAvailable) },
            new { key = "comparison", label = "Rédaction et attribution par personne", available = s.Participation.Available } },
        sites = new[] { new { key = "sites", label = "Sites · stock et planification", available = s.SiteWorkload.Available } },
        dates = DashboardService.Roles.Where(r => r.Key is "created" or "updated" or "resolved" or "closed" or "due").Select(r => new { r.Key, r.Label }).ToArray(),
        kinds = DashboardWidgetSettings.Kinds, presentations = DashboardWidgetSettings.Presentations,
        styles = DashboardWidgetSettings.Styles, accents = DashboardWidgetSettings.Accents
    };
    private static object AnalyticsBucket(DashboardBucket b) => new { b.Label, b.Count, b.Value, b.Unit, b.Sample, b.IsAggregate };
    private static object AnalyticsDistribution(DashboardDistribution d, int top, string presentation = "")
    {
        static double Value(DashboardBucket b) => b.IsAggregate ? b.Value ?? 0 : b.Count;
        var total = d.Buckets.Sum(b => Math.Max(0, Value(b)));
        var ordered = presentation == "pareto" ? d.Buckets.OrderByDescending(Value) : d.Buckets.AsEnumerable();
        return new { d.Key, d.Title, d.Available, d.Denominator, note = AnalyticsClip(d.Note), positiveTotal = double.IsFinite(total) ? (double?)total : null,
            buckets = ordered.Take(top).Select(AnalyticsBucket).ToArray(), totalBuckets = d.Buckets.Count, truncated = d.Buckets.Count > top };
    }
    private static object AnalyticsMatrix(DashboardMatrix matrix)
    {
        var cells = matrix.Cells.Take(400).ToArray(); var labels = cells.Select(c => c.RowLabel).ToHashSet(StringComparer.Ordinal);
        var totals = matrix.Cells.GroupBy(c => c.RowLabel, StringComparer.Ordinal).Select(g => new { label = g.Key, value = g.Sum(c => Math.Max(0, c.Value)) }).ToArray();
        return new { matrix.Key, matrix.Title, matrix.Available, matrix.Unit,
            cells = cells.Select(c => new { c.RowLabel, c.ColumnLabel, c.Value, count = c.Rows.Length }).ToArray(), total = matrix.Cells.Count,
            rowTotals = totals.Where(t => labels.Contains(t.label)).ToArray(), maximumRowTotal = totals.Select(t => t.value).DefaultIfEmpty().Max(), explanation = AnalyticsClip(matrix.Explanation) };
    }
    private static object AnalyticsWidget(AnalyticsRun run, DashboardWidgetSettings widget)
    {
        var s = run.Widgets[widget.Id]; object? content = widget.Kind switch
        {
            "metric" => s.Metric(widget.SourceKey) is { } metric ? AnalyticsMetric(metric, widget, AnalyticsVolumeWeatherReliable(run, s, widget), run.IsLocalFileTest) : null,
            "distribution" => s.Distributions.FirstOrDefault(d => d.Key == widget.SourceKey) is { } distribution ? AnalyticsDistribution(distribution, widget.TopCount, widget.Presentation) : null,
            "weather" => AnalyticsWeather(s.WeatherProfiles.GetValueOrDefault(widget.SourceKey) ?? s.Weather),
            "timeline" => s.Timeline.TakeLast(366).Select(d => new { d.Date, created = d.Created.Length, resolved = d.Resolved.Length, closed = d.Closed.Length }).ToArray(),
            "attention" => new { rows = s.Attention.Take(30).ToArray(), total = s.Attention.Count },
            "oldest" => AnalyticsOldestContent(s, widget),
            "participation" => AnalyticsParticipationContent(s, widget),
            "sites" => AnalyticsSitesContent(run, s, widget),
            "quality" => new { s.Columns, warnings = s.Warnings.Take(100).ToArray() },
            "note" => new { text = widget.Note },
            "matrix" => s.Matrices.FirstOrDefault(m => m.Key == widget.SourceKey) is { } matrix ? AnalyticsMatrix(matrix) : null,
            "histogram" => s.NumericSeries.FirstOrDefault(n => n.Key == widget.SourceKey) is { } numeric ? AnalyticsDistribution(DashboardService.Histogram(numeric, widget.HistogramBreaks), 31) : null,
            "boxplot" => s.BoxSeries.FirstOrDefault(b => b.Key == widget.SourceKey) is { } boxes ? new { boxes.Key, boxes.Title, boxes.Available, boxes.Unit, boxes.Explanation,
                boxes = boxes.Boxes.Take(30).Select(b => new { b.Label, b.Sample, b.Minimum, b.FirstQuartile, b.Median, b.ThirdQuartile, b.Maximum, b.P90 }).ToArray(), total = boxes.Boxes.Count } : null,
            "scatter" => s.Scatters.FirstOrDefault(b => b.Key == widget.SourceKey) is { } scatter ? new { scatter.Key, scatter.Title, scatter.XLabel, scatter.YLabel, scatter.Explanation,
                points = scatter.Points.Take(1000).ToArray(), total = scatter.Points.Count } : null,
            "map" => new { points = s.Geography.Take(2000).Select(g => new { g.Key, g.Label, g.Latitude, g.Longitude, count = g.Rows.Length, active = g.ActiveRows.Length }).ToArray(), total = s.Geography.Count },
            "history" => s.History.FirstOrDefault(h => h.Key == widget.SourceKey),
            _ => null
        };
        var available = widget.Kind switch
        {
            "metric" => s.Metric(widget.SourceKey)?.Value.HasValue == true,
            "distribution" => s.Distributions.FirstOrDefault(d => d.Key == widget.SourceKey)?.Available == true,
            "weather" => (s.WeatherProfiles.GetValueOrDefault(widget.SourceKey) ?? s.Weather).State != "unknown",
            "timeline" => new[] { "created", "resolved", "closed" }.Any(key =>
                s.DateValues.GetValueOrDefault(key)?.Count > 0 || s.RowCount == 0 && s.Column(key)?.Index is not null),
            "attention" => s.Column("status")?.Index is not null,
            "oldest" => s.OldestGroups?.Available == true,
            "participation" => s.Participation.StatusAvailable && (widget.SourceKey == "authors"
                ? s.Participation.FollowupsAvailable || s.Participation.TasksAvailable : s.Participation.Available),
            "sites" => s.SiteWorkload.Available,
            "quality" or "note" or "history" => true,
            "matrix" => s.Matrices.FirstOrDefault(m => m.Key == widget.SourceKey)?.Available == true,
            "histogram" => s.NumericSeries.FirstOrDefault(n => n.Key == widget.SourceKey)?.Values.Count > 0,
            "boxplot" => s.BoxSeries.FirstOrDefault(b => b.Key == widget.SourceKey)?.Available == true,
            "scatter" => s.Scatters.FirstOrDefault(b => b.Key == widget.SourceKey)?.Points.Count > 0,
            "map" => s.Geography.Count > 0,
            _ => content is not null
        };
        var unavailableReason = available ? "" : widget.Kind switch
        {
            "map" => "Aucun lieu du relevé ne dispose de coordonnées utilisables.",
            "timeline" => "Aucune date d’ouverture, de résolution ou de clôture exploitable dans ce périmètre.",
            "weather" => "Les indicateurs disponibles ne permettent pas de calculer cette météo.",
            "attention" => "Une colonne Statut reconnue est nécessaire pour identifier les tickets actifs.",
            "oldest" => s.OldestGroups?.UnavailableReason ?? "Les colonnes Statut, Date d’ouverture et Groupe sont nécessaires.",
            "participation" => "Vérifiez le statut et les colonnes des rédacteurs de suivis, de tâches ou des techniciens attribués.",
            "sites" => s.SiteWorkload.UnavailableReason,
            _ => "Les colonnes ou les valeurs nécessaires à ce widget ne sont pas disponibles dans ce périmètre."
        };
        return new { id = widget.Id, settings = widget, content, rowCount = s.RowCount, s.ScopeLabel, available, unavailableReason };
    }

    private Task<object?> AnalyticsSaveLayoutAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var layout = AnalyticsArgument<DashboardLayoutSettings>(args, "layout"); layout.Validate();
        if (AnalyticsBusinessKey(args) is { Length: > 0 } business) { AnalyticsBusinessPersistLayout(business, layout); return Task.FromResult<object?>(new { saved = true }); }
        if (args.TryGetProperty("baseFingerprint", out _)) AnalyticsCheckLayoutFingerprint(args, Flag(args, "personal"));
        AnalyticsPersistLayout(Flag(args, "personal"), layout); return Task.FromResult<object?>(new { saved = true });
    }
    private string AnalyticsLayoutFingerprint(bool personal) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(AnalyticsLayout(personal), Json))));
    private void AnalyticsCheckLayoutFingerprint(JsonElement args, bool personal)
    {
        if (Text(args, "baseFingerprint", 64) != AnalyticsLayoutFingerprint(personal))
            throw new InvalidOperationException("La disposition a changé depuis l’aperçu. Importez de nouveau le fichier pour vérifier son ajout.");
    }
    private Task<object?> AnalyticsAppendLayoutAsync(JsonElement args, CancellationToken ct)
    {
        if (AnalyticsBusinessKey(args).Length > 0) throw new InvalidDataException("Ajoutez les dispositions dans Mes tableaux ; les rubriques natives restent disponibles séparément.");
        ct.ThrowIfCancellationRequested(); var personal = Flag(args, "personal"); AnalyticsCheckLayoutFingerprint(args, personal);
        var imported = AnalyticsArgument<DashboardLayoutSettings>(args, "layout"); imported.Validate();
        var merged = DashboardLayoutSettings.AppendBoards(AnalyticsLayout(personal), imported);
        AnalyticsPersistLayout(personal, merged.Layout);
        return Task.FromResult<object?>(new { saved = true, boardId = merged.Layout.ActiveBoardId, added = merged.AddedBoards.Count, merged.AddedBoards });
    }
    private AnalyticsLayoutJournal AnalyticsJournal(bool personal)
    {
        var signature = JsonSerializer.Serialize(AnalyticsLayout(personal), Json);
        if (!_analyticsLayoutJournals.TryGetValue(personal, out var journal)) _analyticsLayoutJournals[personal] = journal = new();
        if (signature != journal.Expected) { journal.Undo.Clear(); journal.Redo.Clear(); journal.Expected = signature; }
        return journal;
    }
    private void AnalyticsPersistLayout(bool personal, DashboardLayoutSettings layout, bool remember = true)
    {
        layout.Validate(); layout.Normalize(); layout.Validate();
        var journal = AnalyticsJournal(personal); var previous = AnalyticsLayout(personal).Copy();
        if (JsonSerializer.Serialize(layout, Json) == journal.Expected) return;
        var rollback = Settings.Copy();
        if (personal) Settings.Dashboard.PersonalLayout = layout.Copy(); else Settings.Dashboard.Layout = layout.Copy();
        try { SaveSettings(); } catch { Settings = rollback; throw; }
        if (remember) { journal.Undo.Add(previous); if (journal.Undo.Count > 20) journal.Undo.RemoveAt(0); journal.Redo.Clear(); }
        journal.Expected = JsonSerializer.Serialize(AnalyticsLayout(personal), Json); _analyticsRun = null;
    }
    private Task<object?> AnalyticsMoveLayoutAsync(JsonElement args, CancellationToken ct, bool redo)
    {
        if (AnalyticsBusinessKey(args) is { Length: > 0 } business) return AnalyticsBusinessMoveLayoutAsync(business, redo, ct);
        ct.ThrowIfCancellationRequested(); var personal = Flag(args, "personal"); var journal = AnalyticsJournal(personal);
        var from = redo ? journal.Redo : journal.Undo; var to = redo ? journal.Undo : journal.Redo;
        if (from.Count == 0) throw new InvalidOperationException(redo ? "Aucune modification à rétablir." : "Aucune modification à annuler.");
        var previous = AnalyticsLayout(personal).Copy(); var next = from[^1].Copy();
        AnalyticsPersistLayout(personal, next, false); from.RemoveAt(from.Count - 1); to.Add(previous);
        return Task.FromResult<object?>(new { saved = true, boardId = next.ActiveBoardId, undo = journal.Undo.Count, redo = journal.Redo.Count });
    }
    private Task<object?> AnalyticsCreateBoardAsync(JsonElement args, CancellationToken ct)
    {
        if (AnalyticsBusinessKey(args).Length > 0) throw new InvalidDataException("Créez les tableaux supplémentaires dans Mes tableaux.");
        ct.ThrowIfCancellationRequested(); var personal = Flag(args, "personal"); var layout = AnalyticsLayout(personal).Copy(); layout.Normalize();
        var name = Text(args, "name", 120, "Mon tableau de bord"); var preset = Text(args, "preset", 40, "empty");
        if (!new[] { "empty", "overview", "personal", "team", "deadlines", "quality", "geography", "analysis", "operations", "service" }.Contains(preset)) throw new InvalidDataException("Modèle inconnu.");
        var board = DashboardLayoutSettings.CreateBoard(name, preset); layout.Boards.Add(board); layout.ActiveBoardId = board.Id; layout.Validate();
        AnalyticsPersistLayout(personal, layout); return Task.FromResult<object?>(new { boardId = board.Id });
    }
    private async Task<object?> AnalyticsExportLayoutAsync(JsonElement args, CancellationToken ct)
    {
        var layout = AnalyticsBusinessKey(args) is { Length: > 0 } business ? AnalyticsBusinessLayout(business, ct) : AnalyticsLayout(Flag(args, "personal")).Copy(); layout.Normalize(); layout.Validate();
        var path = await Host.SaveFileAsync("Exporter la disposition", "Disposition JSON (*.json)|*.json", "GLPI-disposition.json");
        if (path is null) return new { cancelled = true };
        ct.ThrowIfCancellationRequested(); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(layout, Json), ct); return new { path };
    }
    private async Task<object?> AnalyticsImportLayoutAsync(JsonElement args, CancellationToken ct)
    {
        if (AnalyticsBusinessKey(args).Length > 0) throw new InvalidDataException("Importez une disposition dans Mes tableaux. Les paramètres du classeur se reprennent depuis Paramètres du pilotage.");
        var path = await Host.OpenFileAsync("Importer une disposition à vérifier", "Disposition JSON (*.json)|*.json");
        if (path is null) return new { cancelled = true };
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Disposition limitée à 2 Mio.");
        var layout = JsonSerializer.Deserialize<DashboardLayoutSettings>(await File.ReadAllTextAsync(path, ct), Json) ?? throw new InvalidDataException("Disposition vide.");
        layout.Validate(); layout.Normalize(); layout.Validate();
        var personal = Flag(args, "personal"); var current = AnalyticsLayout(personal); var canAppend = current.Boards.Count + layout.Boards.Count <= 12;
        var additions = canAppend ? DashboardLayoutSettings.AppendBoards(current, layout).AddedBoards : Array.Empty<DashboardAppendedBoard>();
        return new { layout, preview = true, baseFingerprint = AnalyticsLayoutFingerprint(personal), existingBoards = current.Boards.Count, canAppend,
            addedBoards = additions, appendReason = canAppend ? "" : $"L’ajout dépasserait 12 tableaux ({current.Boards.Count} existants + {layout.Boards.Count} importés)." };
    }

    private static DashboardSnapshot AnalyticsSnapshot(AnalyticsRun run, JsonElement args)
    {
        var id = Text(args, "widgetId", 96);
        return id.Length == 0 ? run.Current : run.Widgets.GetValueOrDefault(id) ?? throw new InvalidDataException("Widget introuvable.");
    }
    private object AnalyticsRows(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsRun(args); var s = AnalyticsSnapshot(run, args); var kind = Text(args, "kind", 40, "scope"); var key = Text(args, "key", 200);
        var label = Text(args, "label", 1000); int[] rows = kind switch
        {
            "scope" => s.ScopeRows,
            "record" => s.ScopeRows.Contains(Number(args, "rowIndex", -1, -1)) ? new[] { Number(args, "rowIndex", -1, -1) }
                : throw new InvalidDataException("Cette ligne n’appartient pas au périmètre du widget."),
            "metric" => s.Metric(key)?.Rows ?? throw new InvalidDataException("Indicateur absent."),
            "distribution" => s.Distributions.SingleOrDefault(d => d.Key == key)?.Buckets.Where(b => b.Label == label).SelectMany(b => b.Rows).Distinct().Order().ToArray() ?? throw new InvalidDataException("Répartition absente."),
            "map" => s.Geography.SingleOrDefault(p => p.Key == key) is { } point ? Flag(args, "active") ? point.ActiveRows : point.Rows : throw new InvalidDataException("Localisation absente."),
            "matrix" => s.Matrices.SingleOrDefault(m => m.Key == key)?.Cells.Where(c => c.RowLabel == label && c.ColumnLabel == Text(args, "columnLabel", 1000)).SelectMany(c => c.Rows).Distinct().Order().ToArray() ?? throw new InvalidDataException("Matrice absente."),
            "boxplot" => s.BoxSeries.SingleOrDefault(b => b.Key == key)?.Boxes.Where(b => b.Label == label).SelectMany(b => b.Rows).Distinct().Order().ToArray() ?? throw new InvalidDataException("Série absente."),
            "histogram" => AnalyticsHistogramRows(run, s, args, label),
            "oldest" or "participation" or "sites" => AnalyticsStockRows(run, s, args, kind, key),
            _ => throw new InvalidDataException("Type de détail inconnu.")
        };
        var sortKey = Text(args, "sortKey", 40);
        if (sortKey == "requester-site")
        {
            var requester = s.Columns.FirstOrDefault(column => column.Role == "requester")?.Index;
            var identifier = s.Columns.FirstOrDefault(column => column.Role == "id")?.Index;
            string Cell(int row, int? column) => column is int index && index < run.Source.Rows[row].Length ? run.Source.Rows[row][index] : "";
            var siteByRow = s.SiteWorkload.Sites.SelectMany(site => site.Rows.Select(row => (Row: row, site.Label))).ToDictionary(item => item.Row, item => item.Label);
            rows = rows.OrderBy(row => Cell(row, requester), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => siteByRow.GetValueOrDefault(row, ""), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => long.TryParse(Cell(row, identifier), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : long.MaxValue)
                .ThenBy(row => row).ToArray();
        }
        else if (sortKey.Length > 0) throw new InvalidDataException("Tri du détail inconnu.");
        return AnalyticsRowPage(run.Source, rows, args, run.Revision, ct);
    }
    private static int[] AnalyticsHistogramRows(AnalyticsRun run, DashboardSnapshot s, JsonElement args, string label)
    {
        var widget = run.Board.Widgets.Single(w => w.Id == Text(args, "widgetId", 96));
        var series = s.NumericSeries.Single(n => n.Key == widget.SourceKey);
        return DashboardService.Histogram(series, widget.HistogramBreaks).Buckets.Where(b => b.Label == label).SelectMany(b => b.Rows).ToArray();
    }
    private static object AnalyticsRowPage(ExtractionResult source, int[] indices, JsonElement args, long revision, CancellationToken ct)
    {
        var offset = Number(args, "offset", 0); var limit = Number(args, "limit", 50, 1, 100);
        var columnOffset = Number(args, "columnOffset", 0, 0, 16384); var columns = source.Headers.Skip(columnOffset).Take(20).ToArray();
        var page = indices.Skip(offset).Take(limit).Select(i =>
        {
            ct.ThrowIfCancellationRequested(); if (i < 0 || i >= source.Rows.Count) throw new InvalidDataException("Ligne devenue indisponible.");
            return new { index = i, values = Enumerable.Range(columnOffset, columns.Length).Select(c => AnalyticsClip(c < source.Rows[i].Length ? source.Rows[i][c] : "", 1000)).ToArray() };
        }).ToArray();
        return new { revision, total = indices.Length, offset, limit, columnOffset, totalColumns = source.Headers.Count, columns, rows = page, cellLimit = 1000 };
    }
    private object AnalyticsDataPage(JsonElement args)
    {
        var run = RequireAnalyticsRun(args); var s = AnalyticsSnapshot(run, args); var kind = Text(args, "kind", 40); var key = Text(args, "key", 200);
        var offset = Number(args, "offset"); var limit = Number(args, "limit", 100, 1, 500);
        if (kind is "oldest" or "participation" or "sites")
        {
            var widget = AnalyticsDetailWidget(run, args);
            return kind switch
            {
                "oldest" => AnalyticsOldestContent(s, widget, offset, limit) ?? throw new InvalidDataException("Classement absent."),
                "participation" => AnalyticsParticipationContent(s, widget, offset, limit),
                _ => AnalyticsSitesContent(run, s, widget, offset, limit)
            };
        }
        IEnumerable<object> values = kind switch
        {
            "distribution" => (s.Distributions.FirstOrDefault(d => d.Key == key)?.Buckets ?? throw new InvalidDataException("Répartition absente.")).Select(AnalyticsBucket),
            "attention" => s.Attention,
            "matrix" => (s.Matrices.FirstOrDefault(d => d.Key == key)?.Cells ?? throw new InvalidDataException("Matrice absente.")).Select(c => (object)new { c.RowLabel, c.ColumnLabel, c.Value, count = c.Rows.Length }),
            "map" => s.Geography.Select(g => (object)new { g.Key, g.Label, g.Latitude, g.Longitude, count = g.Rows.Length, active = g.ActiveRows.Length }),
            "boxplot" => (s.BoxSeries.FirstOrDefault(b => b.Key == key)?.Boxes ?? throw new InvalidDataException("Série absente.")).Select(b => (object)new { b.Label, b.Sample, b.Minimum, b.FirstQuartile, b.Median, b.ThirdQuartile, b.Maximum, b.P90 }),
            "scatter" => s.Scatters.FirstOrDefault(d => d.Key == key)?.Points ?? throw new InvalidDataException("Nuage absent."),
            _ => throw new InvalidDataException("Série détaillée inconnue.")
        };
        return new { total = values.Count(), offset, limit, rows = values.Skip(offset).Take(limit).ToArray() };
    }
    private async Task<object?> AnalyticsExportReportAsync(JsonElement args, CancellationToken ct)
    {
        var run = RequireAnalyticsRun(args); var kind = Text(args, "format", 20, "html");
        if (!new[] { "html", "xlsx", "csv", "png", "pdf" }.Contains(kind)) throw new InvalidDataException("Format d’export inconnu.");
        var path = await Host.SaveFileAsync("Exporter le tableau de bord", kind.ToUpperInvariant() + " (*." + kind + ")|*." + kind, "GLPI-dashboard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "." + kind);
        if (path is null) return new { cancelled = true };
        RequireAnalyticsRun(args); ct.ThrowIfCancellationRequested();
        if (kind is "pdf" or "png")
        {
            var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".glpi-visual-" + Guid.NewGuid().ToString("N") + "." + kind);
            try
            {
                if (kind == "pdf") await Host.PrintPdfAsync(temporary);
                else
                {
                    var image = await Host.CapturePngAsync();
                    if (image.Length < 8 || !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("La capture n’a pas produit d’image PNG valide.");
                    await File.WriteAllBytesAsync(temporary, image, ct);
                }
                RequireAnalyticsRun(args); ct.ThrowIfCancellationRequested();
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("Le fichier visuel n’a pas été créé.");
                File.Move(temporary, path, true); return new { path, format = kind };
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var criteria = AnalyticsDescribe(run.Common) + "\n" + string.Join("\n", run.Board.Widgets.Where(w => !w.Hidden && w.Filters.Count > 0).Select(w => w.Title + " : " + AnalyticsDescribe(w.Filters)));
        var exported = kind == "html" ? ReportService.WriteHtml(run.Current, run.Source, criteria, path, ct)
            : kind == "csv" ? ExportService.WriteCsv(DashboardService.ToExport(run.Current), path, ct)
            : ExportService.WriteXlsx(DashboardService.ToExport(run.Current), path, ct);
        return new { exported.Path, exported.Rows, format = kind };
    }
}
