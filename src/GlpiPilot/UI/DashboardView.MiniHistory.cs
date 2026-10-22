using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private FrameworkElement MiniHistory(DashboardWidgetSettings widget)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        var series = _data!.History.FirstOrDefault(x => x.Key == widget.SourceKey);
        if (series is null || series.Points.Count == 0)
        {
            panel.Children.Add(Text("Historique : aucun point comparable conservé pour ce périmètre.", 10, "MutedBrush"));
            return panel;
        }
        var end = series.Points.Max(x => x.CapturedAt); var middle = end.AddDays(-widget.HistoryPeriodDays); var start = middle.AddDays(-widget.HistoryPeriodDays);
        var points = series.Points.Where(x => x.CapturedAt >= start && x.CapturedAt <= end).OrderBy(x => x.CapturedAt).ToArray();
        var valid = points.Where(x => x.Comparable && x.Complete && x.Value is double value && double.IsFinite(value)).ToArray();
        if (valid.Length == 0) { panel.Children.Add(Text("Historique : données absentes ou incompatibles sur ces périodes.", 10, "MutedBrush")); return panel; }
        var canvas = new Canvas { Width = 240, Height = 65, ClipToBounds = true };
        var min = valid.Min(x => x.Value!.Value); var max = valid.Max(x => x.Value!.Value); var range = Math.Max(1e-9, max - min);
        Point? previous = null;
        foreach (var point in points)
        {
            var x = 4 + 232 * (point.CapturedAt - start).TotalSeconds / Math.Max(1, (end - start).TotalSeconds);
            if (!point.Comparable || !point.Complete || point.Value is not double value || !double.IsFinite(value))
            {
                previous = null; canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 5, Y2 = 56, Stroke = Accent("orange"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, ToolTip = point.Note }); continue;
            }
            var position = new Point(x, max == min ? 30 : 56 - 48 * (value - min) / range);
            if (previous is Point before) canvas.Children.Add(new Line { X1 = before.X, Y1 = before.Y, X2 = position.X, Y2 = position.Y, Stroke = Accent(widget.Accent), StrokeThickness = 1.5 });
            var dot = new Ellipse { Width = 4, Height = 4, Fill = Accent(widget.Accent), ToolTip = point.CapturedAt.ToLocalTime().ToString("g") + " · " + value.ToString("G", CultureInfo.CurrentCulture) + " " + series.Unit };
            Canvas.SetLeft(dot, position.X - 2); Canvas.SetTop(dot, position.Y - 2); canvas.Children.Add(dot); previous = position;
        }
        panel.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 65, HorizontalAlignment = HorizontalAlignment.Left });
        var beforeValues = valid.Where(x => x.CapturedAt < middle).Select(x => x.Value!.Value).ToArray();
        var afterValues = valid.Where(x => x.CapturedAt >= middle).Select(x => x.Value!.Value).ToArray();
        var breaks = points.Count(x => !x.Comparable || !x.Complete || !x.Value.HasValue);
        var note = $"Deux fenêtres de {widget.HistoryPeriodDays} j · {beforeValues.Length}/{afterValues.Length} observations · {breaks} rupture(s)";
        if (beforeValues.Length > 0 && afterValues.Length > 0)
            note += $"\nMoyennes observées : {beforeValues.Average():0.##} → {afterValues.Average():0.##} {series.Unit} · Δ {afterValues.Average() - beforeValues.Average():+0.##;-0.##;0}";
        else note += "\nComparaison indisponible : une période ne contient aucun point exploitable.";
        var label = Text(note, 10, "MutedBrush"); label.ToolTip = "Moyennes des observations disponibles, sans interpolation ni pondération temporelle. La cadence et les ruptures peuvent changer la représentativité ; aucune différence d’événements n’est inférée."; panel.Children.Add(label);
        panel.Children.Add(Command("Historique et méthode", () => OpenDetails(widget, series.Title, body => body.Children.Add(HistoryContent(new DashboardWidgetSettings { Kind = "history", SourceKey = widget.SourceKey, Accent = widget.Accent })))));
        return panel;
    }
}
