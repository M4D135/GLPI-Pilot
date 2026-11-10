using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record AnalyticsWeatherDenominator(string Key, string Label, bool Available = true);
    private static readonly AnalyticsWeatherDenominator[] SelectionWeatherDenominators =
    {
        new("selection-active", "Tickets actifs de la sélection (avant filtres du widget)"),
        new("selection-total", "Tous les tickets de la sélection (avant filtres du widget)"),
        new("source-active", "Tickets actifs de la source entière"),
        new("source-total", "Tous les tickets de la source entière")
    };
    private sealed record AnalyticsWeatherEvaluation(string Unit, double? EvaluatedValue, double? Numerator, double? Denominator,
        string DenominatorLabel, string Mode, string Reason);
    private sealed record AnalyticsSelectionWeather(string Key, bool Configured, double? WarningValue, double? CriticalValue,
        bool LowerIsBetter, string Unit, string DenominatorKey, string ScopeDescription, bool CanEdit,
        DashboardWeatherThresholdPolicy? Policy, DashboardWeatherThresholdPolicy? EffectivePolicy,
        AnalyticsWeatherEvaluation? Evaluation, IReadOnlyList<AnalyticsWeatherDenominator> Denominators,
        string RecommendedDenominatorKey, bool PercentageAvailable, string PercentageReason);

    private static string AnalyticsWeatherSampleLabel(string key) => key switch
    {
        "high" or "major" => "Tickets actifs de la sélection dont la priorité est reconnue",
        "high-urgency" => "Tickets actifs de la sélection dont l’urgence est reconnue",
        "high-impact" => "Tickets actifs de la sélection dont l’impact est reconnu",
        "old" => "Tickets actifs de la sélection avec une date d’ouverture exploitable",
        "stale" => "Tickets actifs de la sélection avec une date de modification exploitable et cohérente",
        "overdue" or "soon" => "Tickets actifs de la sélection avec une date d’échéance exploitable",
        _ => ""
    };

    private void RegisterSelectionWeatherOperations()
    {
        Register("dashboard.selection.get", (args, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var (_, _, layout, board) = AnalyticsSelectionLayout(args);
            return Task.FromResult<object?>(new { selection = board.SavedSelection?.Copy(), boardId = board.Id });
        });
        Register("dashboard.selection.remember", AnalyticsRememberSelectionAsync);
        Register("dashboard.weatherSelection.save", (args, ct) => AnalyticsSaveSelectionWeatherAsync(args, ct, false));
        Register("dashboard.weatherSelection.remove", (args, ct) => AnalyticsSaveSelectionWeatherAsync(args, ct, true));
    }
    private (string Business, bool Personal, DashboardLayoutSettings Layout, DashboardBoardSettings Board) AnalyticsSelectionLayout(JsonElement args)
    {
        var business = AnalyticsBusinessKey(args); var personal = Flag(args, "personal");
        var layout = business.Length > 0 ? AnalyticsBusinessLayout(business) : AnalyticsLayout(personal).Copy();
        var id = Text(args, "boardId", 96, layout.ActiveBoardId);
        // An empty boardId is a normal first visit, before the board has been loaded.
        if (id.Length == 0) id = layout.ActiveBoardId;
        var board = layout.Boards.FirstOrDefault(item => item.Id == id) ?? throw new InvalidDataException("Tableau de bord introuvable.");
        return (business, personal, layout, board);
    }
    private void AnalyticsStoreSelectionBoard(string business, bool personal, DashboardBoardSettings board)
    {
        var next = Settings.Copy();
        if (business.Length > 0)
        {
            next.Dashboard.Business.BoardOverrides.RemoveAll(item => item.Id == board.Id);
            next.Dashboard.Business.BoardOverrides.Add(board.Copy()); next.Dashboard.Business.Validate();
            _businessLayoutJournals.Remove(business);
        }
        else
        {
            var layout = personal ? next.Dashboard.PersonalLayout : next.Dashboard.Layout;
            var index = layout.Boards.FindIndex(item => item.Id == board.Id);
            if (index < 0) throw new InvalidOperationException("Ce tableau de bord n’existe plus.");
            layout.Boards[index] = board.Copy(); layout.Validate(); _analyticsLayoutJournals.Remove(personal);
        }
        CommitSettings(next); _analyticsRun = null;
    }
    private Task<object?> AnalyticsRememberSelectionAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var (business, personal, _, board) = AnalyticsSelectionLayout(args);
        var selection = AnalyticsArgument<DashboardSavedSelection>(args, "selection"); selection.Validate();
        selection.FocusKeys = selection.FocusKeys.Distinct(StringComparer.Ordinal).ToList();
        if (board.SavedSelection?.Canonical() == selection.Canonical())
            return Task.FromResult<object?>(new { saved = false, settings = Settings, revision = Revision });
        board.SavedSelection = selection.Copy(); AnalyticsStoreSelectionBoard(business, personal, board);
        return Task.FromResult<object?>(new { saved = true, settings = Settings, revision = Revision });
    }
    private Task<object?> AnalyticsSaveSelectionWeatherAsync(JsonElement args, CancellationToken ct, bool remove)
    {
        ct.ThrowIfCancellationRequested(); var run = RequireAnalyticsRun(args); var id = Text(args, "widgetId", 96);
        var raw = run.StoredBoard.Widgets.SingleOrDefault(widget => widget.Id == id)
            ?? throw new InvalidDataException("Widget introuvable dans ce calcul.");
        if (raw.Kind != "metric" || raw.Presentation != "weather-volume" || !run.SelectionWeather.TryGetValue(id, out var metadata) || !metadata.CanEdit)
            throw new InvalidOperationException("Choisissez un widget météo applicable au périmètre courant.");
        var business = run.BusinessView; var personal = run.TechnicianKey.Length > 0;
        var layout = business.Length > 0 ? AnalyticsBusinessLayout(business) : AnalyticsLayout(personal).Copy();
        var board = layout.Boards.SingleOrDefault(item => item.Id == run.StoredBoard.Id)
            ?? throw new InvalidOperationException("Ce tableau de bord n’existe plus.");
        if (JsonSerializer.Serialize(board, Json) != JsonSerializer.Serialize(run.StoredBoard, Json))
            throw new InvalidOperationException("La configuration du widget a changé. Recalculez le tableau avant d’enregistrer ses seuils.");
        var widget = board.Widgets.Single(item => item.Id == id); var mode = Text(args, "mode", 20, "selection");
        if (mode is not ("selection" or "widget")) throw new InvalidDataException("Choisissez cette sélection ou ce widget pour la portée des seuils.");
        var scope = AnalyticsSelectionScope(run, raw); var key = scope.Key();
        var policy = remove ? null : new DashboardWeatherThresholdPolicy
        {
            Unit = Text(args, "unit", 20, "count"), DenominatorKey = Text(args, "denominatorKey", 40, "selection-active"),
            WarningValue = AnalyticsArgument<double>(args, "warningValue"), CriticalValue = AnalyticsArgument<double>(args, "criticalValue"),
            LowerIsBetter = !args.TryGetProperty("lowerIsBetter", out var direction) || direction.GetBoolean()
        };
        policy?.Validate();
        if (mode == "widget")
        {
            if (policy is not null && policy.Unit != "percent") throw new InvalidDataException("Une règle réutilisable entre sélections doit utiliser des pourcentages et une base explicite.");
            widget.WeatherThresholdPolicy = policy?.Copy();
        }
        else
        {
            widget.SelectionThresholds.RemoveAll(entry => entry.Scope.Key() == key);
            if (policy is not null) widget.SelectionThresholds.Add(new() { Scope = scope, Unit = policy.Unit, DenominatorKey = policy.DenominatorKey,
                WarningValue = policy.WarningValue, CriticalValue = policy.CriticalValue, LowerIsBetter = policy.LowerIsBetter });
            DashboardSelectionThresholds.ValidateList(widget.SelectionThresholds);
        }
        board.SavedSelection = run.Selection.Copy();
        AnalyticsStoreSelectionBoard(business, personal, board);
        return Task.FromResult<object?>(new { saved = true, settings = Settings, revision = Revision });
    }
    private static string WeatherDigest(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
    private static string AnalyticsWeatherSourceIdentity(ExtractionResult source)
    {
        var p = source.Provenance;
        var proven = SnapshotStore.SourceKey(source);
        // The verified browser search and real file identity survive cache restoration.
        // An unverified observation without such an identity remains private to its run.
        return WeatherDigest(new { Version = 1, p.SourceKind, Source = proven,
            Scope = p.ScopeFingerprint, p.SearchVerified, p.ScopeVerified,
            Observation = proven.Length > 0 || p.SourceKind is "file" or "demo" && p.ScopeFingerprint.Length > 0 ? "" : p.RunId });
    }
    private static DashboardWeatherSelectionScope AnalyticsSelectionScope(AnalyticsRun run, DashboardWidgetSettings widget)
    {
        var d = run.Settings.Dashboard;
        var scopeKeys = run.Common.Concat(widget.Filters).Select(filter => filter.SourceKey).Append(widget.SourceKey).ToArray();
        var queryIds = scopeKeys
            .Where(key => key.StartsWith("query:", StringComparison.Ordinal) || key.StartsWith("query-group:", StringComparison.Ordinal))
            .Select(key => key[(key.StartsWith("query-group:", StringComparison.Ordinal) ? 12 : 6)..]).ToHashSet(StringComparer.Ordinal);
        // Native group-only scopes do not depend on the site catalogue. A saved
        // query may read a generated location column by its exact, arbitrary
        // header (including through a computed field), so conservatively bind
        // every referenced query to the geographical interpretation as well.
        var geography = queryIds.Count > 0 || scopeKeys.Any(key => key is "city" or "active-city" or "technician-city"
            or "region-reference" or "department-reference" or "sector-reference"
            || key.StartsWith("city-", StringComparison.Ordinal) || key.StartsWith("site-", StringComparison.Ordinal)
            || key.StartsWith("location-", StringComparison.Ordinal) || key.StartsWith("geography-", StringComparison.Ordinal));
        var queries = run.Settings.Queries.Queries.Where(query => queryIds.Contains(query.Id)).OrderBy(query => query.Id, StringComparer.Ordinal).Select(query => new
        {
            query.Id, query.BaseProbe, query.Match, query.Aggregate, query.AggregateField, query.GroupBy, query.GroupBySecondary, query.GroupByTertiary,
            query.GroupDateBucket, query.SecondaryDateBucket, query.TertiaryDateBucket, query.Percentile, query.ConditionExpression,
            Conditions = query.ConditionExpression.Length > 0 ? query.Conditions.Select(condition => JsonSerializer.Serialize(condition, Json)).ToArray()
                : query.Conditions.Select(condition => JsonSerializer.Serialize(condition, Json)).Order(StringComparer.Ordinal).ToArray(),
            Parameters = query.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal).ToArray(), query.ComputedFields
        }).ToArray();
        return new()
        {
            SourceIdentity = AnalyticsWeatherSourceIdentity(run.Source), Kind = widget.Kind, SourceKey = widget.SourceKey,
            TechnicianKey = run.TechnicianKey, CommonFilters = DashboardScopeFilter.CopyList(run.Common), WidgetFilters = DashboardScopeFilter.CopyList(widget.Filters),
            InterpretationIdentity = WeatherDigest(new { Version = 1, d.DateCulture, d.NumberCulture, d.SourceTimeZone, d.MultiValueSeparators,
                d.StaleDays, d.OldDays, d.DueSoonHours, d.DueHorizonHours, d.AssignmentCountingMode, d.SatisfactionScale,
                Mappings = d.ColumnMappings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                Columns = run.Unfiltered.Columns.Select(column => new { column.Role, column.Header, column.State }).ToArray(),
                Dictionaries = d.ValueDictionaries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key,
                    Values = pair.Value.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray() }).ToArray(),
                Priority = d.PriorityRules.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(), Queries = queries,
                Geography = geography ? run.Settings.Cities : null })
        };
    }
    private static DashboardVolumeWeather? AnalyticsWeatherOverride(AnalyticsRun run, string widgetId) =>
        run.WeatherOverrides.GetValueOrDefault(widgetId) ?? run.StockWeather?.Overrides.GetValueOrDefault(widgetId);
    private static string AnalyticsSelectionDescription(AnalyticsRun run, DashboardWidgetSettings widget)
    {
        var details = AnalyticsDescribe(run.Common.Concat(widget.Filters).Select(filter =>
        {
            var display = filter.Copy();
            display.SourceKey = filter.Kind switch
            {
                "metric" => run.Baseline.Metric(filter.SourceKey)?.Label ?? filter.SourceKey,
                "distribution" => run.Baseline.Distributions.FirstOrDefault(item => item.Key == filter.SourceKey)?.Title ?? filter.SourceKey,
                "matrix-row" => run.Baseline.Matrices.FirstOrDefault(item => item.Key == filter.SourceKey)?.Title ?? filter.SourceKey,
                "date" => run.Baseline.Columns.FirstOrDefault(item => item.Role == filter.SourceKey)?.Label ?? filter.SourceKey,
                _ => filter.SourceKey
            };
            return display;
        }));
        return "Source et recherche courantes" + (run.TechnicianKey.Length > 0 ? " · Technicien : " + run.TechnicianKey : "")
            + (details.Length == 0 ? " · Tous les tickets du périmètre" : " · " + details);
    }
    private static void AnalyticsApplySelectionWeather(AnalyticsRun run, CancellationToken ct)
    {
        foreach (var raw in run.StoredBoard.Widgets.Where(widget => !widget.Hidden && widget.Kind == "metric" && widget.Presentation == "weather-volume" && run.Widgets.ContainsKey(widget.Id)))
        {
            ct.ThrowIfCancellationRequested(); var widget = run.Board.Widgets.Single(item => item.Id == raw.Id);
            var scope = AnalyticsSelectionScope(run, raw); var key = scope.Key(); var canonical = scope.Canonical();
            var selected = raw.SelectionThresholds.SingleOrDefault(entry => entry.Scope.Key() == key && entry.Scope.Canonical() == canonical);
            var policy = (DashboardWeatherThresholdPolicy?)selected ?? raw.WeatherThresholdPolicy;
            var context = run.StockWeather?.Contexts.GetValueOrDefault(widget.Id); var applicable = context?.Applicable != false;
            var description = AnalyticsSelectionDescription(run, raw); AnalyticsWeatherEvaluation? evaluation = null;
            var currentMetric = run.Widgets[widget.Id].Metric(widget.SourceKey); var sampleMetric = run.Current.Metric(widget.SourceKey);
            var sampleLabel = AnalyticsWeatherSampleLabel(widget.SourceKey);
            var denominators = SelectionWeatherDenominators.Select(item => item with { Available =
                (item.Key.StartsWith("source-", StringComparison.Ordinal) ? run.Unfiltered : run.Current)
                    .Metric(item.Key.EndsWith("-active", StringComparison.Ordinal) ? "active" : "total")?.Value is > 0 }).ToList();
            if (sampleLabel.Length > 0) denominators.Add(new("selection-sample", sampleLabel, sampleMetric is { Value: not null, Sample: > 0 }));
            var percentageAvailable = currentMetric is { Unit: "tickets", Value: not null } && currentMetric.Value == currentMetric.Rows.Distinct().Count();
            var percentageReason = percentageAvailable ? "Le pourcentage représente la part de ces tickets dans la base choisie."
                : currentMetric?.Unit == "%" ? "Cet indicateur est déjà un pourcentage ; il n’est pas divisé une seconde fois."
                : "Le pourcentage nécessite un décompte de tickets disponible ; les durées et autres mesures gardent leur unité.";
            var recommended = sampleLabel.Length > 0 ? "selection-sample" : widget.SourceKey == "active" && widget.Filters.Count == 0 ? "source-active"
                : widget.SourceKey is "total" or "resolved" or "closed" || widget.SourceKey.StartsWith("created-", StringComparison.Ordinal)
                    || widget.SourceKey.StartsWith("resolved-", StringComparison.Ordinal) || widget.SourceKey.StartsWith("closed-", StringComparison.Ordinal) ? "selection-total" : "selection-active";
            if (policy is not null && applicable)
            {
                var snapshot = run.Widgets[widget.Id]; var metric = snapshot.Metric(widget.SourceKey);
                var complete = AnalyticsVolumeWeatherReliable(run, snapshot, widget); var mode = selected is null ? "widget" : "selection";
                widget.WarningValue = policy.WarningValue; widget.CriticalValue = policy.CriticalValue; widget.LowerIsBetter = policy.LowerIsBetter; widget.TargetValue = null;
                DashboardVolumeWeather weather;
                if (policy.Unit == "count")
                {
                    weather = widget.EvaluateVolumeWeather(metric?.Value, complete);
                    evaluation = new("count", metric?.Value, metric?.Value, null, "", mode, "Seuils enregistrés pour cette sélection exacte.");
                }
                else
                {
                    var denominatorSnapshot = policy.DenominatorKey.StartsWith("source-", StringComparison.Ordinal) ? run.Unfiltered : run.Current;
                    var usesSample = policy.DenominatorKey == "selection-sample";
                    var denominator = usesSample ? sampleMetric : denominatorSnapshot.Metric(policy.DenominatorKey.EndsWith("-active", StringComparison.Ordinal) ? "active" : "total");
                    var denominatorValue = usesSample ? sampleMetric?.Sample : denominator?.Value;
                    var label = usesSample ? sampleLabel.Length > 0 ? sampleLabel : "Population évaluable indisponible pour cet indicateur"
                        : SelectionWeatherDenominators.Single(item => item.Key == policy.DenominatorKey).Label;
                    double? percentage = null; var reason = "";
                    if (usesSample && sampleLabel.Length == 0) reason = "Cet indicateur ne définit pas de population évaluable pour cette base.";
                    else if (metric?.Value is not double numerator || denominator?.Value is null || denominatorValue is not double population)
                        reason = "Le numérateur ou la base est indisponible ; aucun pourcentage n’est attribué.";
                    else if (population <= 0) reason = "La base est nulle ; aucun pourcentage n’est attribué.";
                    else if (metric.Unit != "tickets" || denominator.Unit != "tickets" || numerator != metric.Rows.Distinct().Count()
                        || !usesSample && population != denominator.Rows.Distinct().Count()
                        || usesSample && (denominator.Value > population || denominator.Value != denominator.Rows.Distinct().Count())
                        || !metric.Rows.ToHashSet().IsSubsetOf(denominator.Rows))
                        reason = "Cette base ne contient pas tous les tickets de l’indicateur, ou cet indicateur n’est pas un décompte de tickets ; le pourcentage est indisponible.";
                    else percentage = 100d * numerator / population;
                    if (percentage is > 100 or < 0 || percentage.HasValue && !double.IsFinite(percentage.Value))
                    { percentage = null; reason = "Les valeurs ne définissent pas une part comprise entre 0 et 100 % sur cette base."; }
                    var method = "Base : " + label + ". " + (percentage.HasValue
                        ? $"{metric!.Value:G} / {denominatorValue:G} × 100 = {percentage.Value:0.###} %. " : reason + " ")
                        + $"Vigilance {(policy.LowerIsBetter ? ">" : "<")} {policy.WarningValue:G} % ; tension {(policy.LowerIsBetter ? ">" : "<")} {policy.CriticalValue:G} %. L’égalité reste dans l’état précédent.";
                    weather = percentage.HasValue ? widget.EvaluateVolumeWeather(percentage, complete && AnalyticsVolumeWeatherReliable(run, denominatorSnapshot, widget))
                        : new("unknown", "Pourcentage indisponible", reason);
                    weather = weather with { Explanation = method + (weather.State == "partial" ? " Le relevé reste à vérifier ; aucune météo de santé n’est attribuée." : "") };
                    evaluation = new("percent", percentage, metric?.Value, denominatorValue, label, mode, reason);
                }
                run.WeatherOverrides[widget.Id] = weather;
                if (context is not null) run.StockWeather!.Contexts[widget.Id] = context with { RepresentedByHero = false,
                    Reason = selected is null ? "Seuils en pourcentage réutilisables pour ce widget, avec une base explicite." : "Seuils enregistrés pour cette sélection exacte. " + description };
                if (run.StockWeather is { } projection && projection.Scope.HeroWidgetId == widget.Id)
                {
                    projection.Scope = projection.Scope with { Mode = selected is null ? "proportional" : "selection",
                        Reason = (selected is null ? "Seuils en pourcentage réutilisables pour ce widget. " : "Seuils enregistrés pour cette sélection exacte. ") + weather.Explanation };
                    foreach (var duplicate in projection.Contexts.Where(pair => pair.Value.RepresentedByHero).Select(pair => pair.Key).ToArray())
                        projection.Contexts[duplicate] = projection.Contexts[duplicate] with { RepresentedByHero = false,
                            Reason = "La météo principale utilise ses propres seuils ; les seuils de cette équipe restent présentés séparément." };
                }
            }
            run.SelectionWeather[widget.Id] = new(key, selected is not null, selected?.WarningValue, selected?.CriticalValue,
                selected?.LowerIsBetter ?? raw.LowerIsBetter, selected?.Unit ?? "count", selected?.DenominatorKey ?? "selection-active", description, applicable,
                raw.WeatherThresholdPolicy?.Copy(), policy?.Copy(), evaluation, denominators, recommended, percentageAvailable, percentageReason);
        }
    }
}
