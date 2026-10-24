using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private static DashboardVolumeWeather AnalyticsEvaluateStockWeather(DashboardWidgetSettings widget, double? value,
        bool complete, bool simulation, DashboardVolumeWeather? weatherOverride = null)
    {
        var weather = weatherOverride ?? widget.EvaluateVolumeWeather(value, complete);
        if (!complete && value.HasValue && weatherOverride is not null && weatherOverride.Title is not ("Hors périmètre" or "Météo désactivée"))
            weather = new("partial", "Relevé à vérifier", weather.Explanation
                + " La réception, le périmètre, les identifiants ou les statuts du relevé restent également à vérifier ; aucun état de santé n’est attribué.");
        return simulation ? weather with { Title = "Simulation · " + weather.Title,
            Explanation = "Simulation des seuils sur le fichier local ; aucune couverture GLPI n’est confirmée. " + weather.Explanation } : weather;
    }

    private static IReadOnlyList<DashboardStockWeatherEntry>? AnalyticsStockWeatherEntries(AnalyticsRun run)
    {
        if (run.StockWeather is not { } projection) return null;
        var entries = new List<DashboardStockWeatherEntry>();
        foreach (var widget in run.Board.Widgets.Where(widget => !widget.Hidden && widget.Kind == "metric" && widget.Presentation == "weather-volume"
            && !run.Board.Sections.Any(section => section.Key == widget.Section && section.Hidden)))
        {
            var context = projection.Contexts.GetValueOrDefault(widget.Id);
            if (context is { Applicable: false } or { RepresentedByHero: true }) continue;
            var snapshot = run.Widgets[widget.Id]; var metric = snapshot.Metric(widget.SourceKey);
            if (metric is null) continue;
            var weather = AnalyticsEvaluateStockWeather(widget, metric.Value, AnalyticsVolumeWeatherReliable(run, snapshot, widget), run.IsLocalFileTest,
                projection.Overrides.GetValueOrDefault(widget.Id));
            entries.Add(new(widget.Title, metric.Value, metric.Unit, weather.State, weather.Title, widget.WarningValue, widget.CriticalValue,
                widget.LowerIsBetter, context?.Reason.Length > 0 ? context.Reason : snapshot.ScopeLabel, weather.Explanation));
        }
        return entries;
    }
}
