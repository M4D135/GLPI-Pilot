using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private FrameworkElement ColumnsContent(DashboardWidgetSettings widget, DashboardDistribution distribution, bool pareto)
    {
        var panel = new StackPanel();
        IEnumerable<DashboardBucket> ordered = pareto ? distribution.Buckets.OrderByDescending(x => x.Count) : distribution.Buckets;
        var buckets = ordered.Take(widget.TopCount).ToArray();
        if (buckets.Length == 0) return Text("Aucune valeur disponible.", 12, "MutedBrush");
        var total = distribution.Buckets.Sum(x => (long)x.Count); var maximum = Math.Max(1, buckets.Max(x => x.Count));
        panel.Children.Add(Text($"{buckets.Length} / {distribution.Buckets.Count} catégories · {distribution.Denominator:N0} lignes", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 8)));
        var canvas = new Canvas { Width = 600, Height = 240 };
        var width = 540d / buckets.Length;
        double cumulative = 0; Point? previous = null;
        for (var i = 0; i < buckets.Length; i++)
        {
            var bucket = buckets[i]; var height = 165d * bucket.Count / maximum;
            var bar = Command(new Border { Background = Accent(widget.Accent), Height = Math.Max(2, height), Width = Math.Max(3, width - 6), CornerRadius = new CornerRadius(3, 3, 0, 0) },
                () => ActivateRows(bucket.Rows, distribution.Title + " · " + bucket.Label, _baseline?.Distributions.Any(x => x.Key == distribution.Key) == true ? distribution.Key : null, bucket.Label));
            bar.Padding = new Thickness(0); bar.Margin = new Thickness(0); bar.BorderThickness = new Thickness(0); bar.MinHeight = 0; bar.Background = Brushes.Transparent;
            Help(bar, bucket.Label, bucket.Label + " · " + bucket.Count.ToString("N0")); Canvas.SetLeft(bar, 40 + i * width); Canvas.SetTop(bar, 185 - Math.Max(2, height)); canvas.Children.Add(bar);
            var value = Text(bucket.Count.ToString("N0"), 10, "MutedBrush"); Canvas.SetLeft(value, 40 + i * width); Canvas.SetTop(value, 167 - height); canvas.Children.Add(value);
            var label = Text((i + 1).ToString(), 10, "MutedBrush"); Canvas.SetLeft(label, 44 + i * width); Canvas.SetTop(label, 194); canvas.Children.Add(label);
            cumulative += bucket.Count;
            if (pareto && total > 0)
            {
                var point = new Point(40 + (i + .4) * width, 185 - 165 * cumulative / total);
                if (previous is Point before) canvas.Children.Add(new Line { X1 = before.X, Y1 = before.Y, X2 = point.X, Y2 = point.Y, Stroke = Accent("orange"), StrokeThickness = 2, IsHitTestVisible = false });
                previous = point;
            }
        }
        if (pareto)
        {
            var line = new Line { X1 = 35, X2 = 580, Y1 = 185 - 1.65 * widget.ParetoThresholdPercent, Y2 = 185 - 1.65 * widget.ParetoThresholdPercent, Stroke = Accent("slate"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false }; canvas.Children.Add(line);
            var threshold = Text(widget.ParetoThresholdPercent.ToString("0.#") + " %", 10, "MutedBrush"); Canvas.SetLeft(threshold, 0); Canvas.SetTop(threshold, 175 - 1.65 * widget.ParetoThresholdPercent); canvas.Children.Add(threshold);
            panel.Children.Add(Text($"Courbe orange : cumul des occurrences sur toutes les catégories. Le repère à {widget.ParetoThresholdPercent:0.#} % est configurable dans les options.", 10, "MutedBrush", margin: new Thickness(0, 0, 0, 5)));
        }
        panel.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 250 });
        var legend = new StackPanel();
        for (var i = 0; i < buckets.Length; i++) legend.Children.Add(Text($"{i + 1}. {buckets[i].Label} · {buckets[i].Count:N0}", 11, "MutedBrush", margin: new Thickness(0, 2, 0, 2)));
        panel.Children.Add(Details("Légende chiffrée", legend));
        panel.Children.Add(Command("Toutes les catégories", () => OpenDistribution(widget, distribution)));
        if (widget.ShowDescription) panel.Children.Add(Details("Méthode", Text(distribution.Note, 11, "MutedBrush")));
        return panel;
    }

    private FrameworkElement HistogramContent(DashboardWidgetSettings widget)
    {
        var series = _data!.NumericSeries.FirstOrDefault(x => x.Key == widget.SourceKey);
        if (series == null || series.Values.Count == 0) return Missing("Aucune paire de dates exploitable pour cet histogramme. Vérifiez le champ et le périmètre.");
        var distribution = DashboardService.Histogram(series, widget.HistogramBreaks);
        if (widget.Presentation is "bars" or "list")
        {
            var panel = new StackPanel(); var max = Math.Max(1, distribution.Buckets.Max(x => x.Count));
            panel.Children.Add(Text(series.Values.Count.ToString("N0") + " valeurs exploitables · " + series.Unit, 11, "MutedBrush"));
            foreach (var bucket in distribution.Buckets) panel.Children.Add(DistributionRow(distribution, bucket, widget, widget.Presentation == "bars", max));
            if (widget.ShowDescription) panel.Children.Add(Details("Tranches et méthode", Text(distribution.Note, 11, "MutedBrush")));
            return panel;
        }
        var display = widget.Copy(); display.TopCount = 31;
        return ColumnsContent(display, distribution, false);
    }

    private FrameworkElement MatrixContent(DashboardWidgetSettings widget)
    {
        var matrix = _data!.Matrices.FirstOrDefault(x => x.Key == widget.SourceKey);
        if (matrix?.Available != true) return Missing("Les deux dimensions nécessaires à cette matrice ne sont pas disponibles. Vérifiez leurs correspondances.");
        if (matrix.Cells.Count == 0) return Text("Aucune occurrence dans le périmètre.", 12, "MutedBrush");
        var panel = new StackPanel();
        var rowNames = matrix.Cells.GroupBy(x => x.RowLabel).OrderByDescending(x => x.Sum(v => v.Value)).ThenBy(x => x.Key).Take(widget.TopCount).Select(x => x.Key).ToArray();
        var colNames = matrix.Cells.GroupBy(x => x.ColumnLabel).OrderByDescending(x => x.Sum(v => v.Value)).ThenBy(x => x.Key).Take(8).Select(x => x.Key).ToArray();
        panel.Children.Add(Text($"{matrix.Cells.Select(x => x.RowLabel).Distinct().Count():N0} lignes × {matrix.Cells.Select(x => x.ColumnLabel).Distinct().Count():N0} colonnes · {matrix.Unit}", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 9)));
        if (widget.Presentation == "list")
        {
            foreach (var cell in matrix.Cells.Take(widget.TopCount)) panel.Children.Add(MatrixCellRow(cell));
        }
        else if (widget.Presentation == "stacked")
        {
            var palette = new[] { "blue", "teal", "purple", "orange", "green", "red", "slate" };
            var allColumns = matrix.Cells.Select(x => x.ColumnLabel).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            var maximumTotal = matrix.Cells.GroupBy(x => x.RowLabel).Max(x => x.Sum(c => c.Value));
            foreach (var row in rowNames)
            {
                var cells = matrix.Cells.Where(x => x.RowLabel == row).ToArray(); var total = cells.Sum(x => x.Value);
                panel.Children.Add(Text(row + " · " + total.ToString("0.##", CultureInfo.CurrentCulture) + " " + matrix.Unit, 12, margin: new Thickness(0, 7, 0, 4)));
                var strip = new Grid { Height = 28 };
                for (var i = 0; i < cells.Length; i++)
                {
                    var cell = cells[i]; if (cell.Value <= 0) continue;
                    var col = strip.ColumnDefinitions.Count; strip.ColumnDefinitions.Add(new() { Width = new GridLength(cell.Value, GridUnitType.Star) });
                    var button = Command("", () => RowsRequested?.Invoke(cell.Rows, row + " · " + cell.ColumnLabel));
                    button.Background = Accent(palette[Array.IndexOf(allColumns, cell.ColumnLabel) % palette.Length]); button.Padding = new Thickness(0); button.Margin = new Thickness(0); button.MinWidth = 0; button.MinHeight = 0; button.BorderThickness = new Thickness(0);
                    Help(button, row + " · " + cell.ColumnLabel, $"{cell.Value:0.##} {matrix.Unit} · {100 * cell.Value / total:0.#} % de cette ligne");
                    Grid.SetColumn(button, col); strip.Children.Add(button);
                }
                if (widget.StackedMode == "volume" && maximumTotal > total)
                    strip.ColumnDefinitions.Add(new() { Width = new GridLength(maximumTotal - total, GridUnitType.Star) });
                panel.Children.Add(strip);
            }
            var legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            for (var i = 0; i < Math.Min(20, allColumns.Length); i++) { var label = Text("● " + allColumns[i], 10, margin: new Thickness(0, 0, 12, 5)); label.Foreground = Accent(palette[i % palette.Length]); legend.Children.Add(label); }
            panel.Children.Add(legend); panel.Children.Add(Text((widget.StackedMode == "volume" ? "La longueur représente le volume, sur une échelle commune à toutes les lignes. " : "Chaque barre représente 100 % des occurrences de sa ligne. ") + (allColumns.Length > 20 ? "Légende limitée à 20 catégories ; chaque segment garde son intitulé en infobulle et dans la liste complète." : ""), 10, "MutedBrush"));
        }
        else
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1.8, GridUnitType.Star) });
            for (var i = 0; i < colNames.Length; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var col = 0; col < colNames.Length; col++) { var label = Text(PreviewText(colNames[col], 34), 10, "MutedBrush", margin: new Thickness(3, 0, 3, 6)); label.ToolTip = colNames[col]; Grid.SetColumn(label, col + 1); grid.Children.Add(label); }
            var maximum = Math.Max(1, matrix.Cells.Max(x => x.Value));
            for (var row = 0; row < rowNames.Length; row++)
            {
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var title = Text(PreviewText(rowNames[row], 45), 11, margin: new Thickness(0, 6, 6, 6)); title.ToolTip = rowNames[row]; Grid.SetRow(title, row + 1); grid.Children.Add(title);
                for (var col = 0; col < colNames.Length; col++)
                {
                    var cell = matrix.Cells.FirstOrDefault(x => x.RowLabel == rowNames[row] && x.ColumnLabel == colNames[col]);
                    var button = Command((cell?.Value ?? 0).ToString("0.##", CultureInfo.CurrentCulture), () => { if (cell != null) RowsRequested?.Invoke(cell.Rows, matrix.Title + " · " + cell.RowLabel + " · " + cell.ColumnLabel); }, cell != null);
                    button.Padding = new Thickness(3, 7, 3, 7); button.Margin = new Thickness(2); button.MinWidth = 0; button.FontSize = 11;
                    var fill = Accent(widget.Accent).Clone(); fill.Opacity = cell == null ? .04 : .1 + .6 * cell.Value / maximum; button.Background = fill;
                    button.ToolTip = rowNames[row] + " × " + colNames[col] + " · " + (cell?.Value ?? 0).ToString("G", CultureInfo.CurrentCulture) + " " + matrix.Unit;
                    Grid.SetRow(button, row + 1); Grid.SetColumn(button, col + 1); grid.Children.Add(button);
                }
            }
            panel.Children.Add(grid);
            panel.Children.Add(Text($"Aperçu : {rowNames.Length} lignes et {colNames.Length} colonnes. La liste complète conserve chaque cellule renseignée.", 10, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        }
        panel.Children.Add(Command("Toutes les cellules", () => OpenDetails(widget, matrix.Title, body =>
        { body.Children.Add(Text(matrix.Explanation, 12, "MutedBrush")); PagedDetails(body, matrix.Cells, x => x.RowLabel + " " + x.ColumnLabel, MatrixCellRow, "cellules"); })));
        if (widget.ShowDescription) panel.Children.Add(Details("Méthode et comptage", Text(matrix.Explanation, 11, "MutedBrush")));
        return panel;
    }

    private FrameworkElement MatrixCellRow(DashboardMatrixCell cell) => Command(cell.RowLabel + " × " + cell.ColumnLabel + " · " + cell.Value.ToString("0.##", CultureInfo.CurrentCulture),
        () => RowsRequested?.Invoke(cell.Rows, cell.RowLabel + " × " + cell.ColumnLabel));

    private FrameworkElement BoxContent(DashboardWidgetSettings widget)
    {
        var series = _data!.BoxSeries.FirstOrDefault(x => x.Key == widget.SourceKey);
        if (series?.Available != true || series.Boxes.Count == 0) return Missing("La dimension ou les dates nécessaires à cette dispersion ne sont pas exploitables.");
        var panel = new StackPanel(); var maximum = Math.Max(1e-9, series.Boxes.Max(x => x.Maximum));
        FrameworkElement BoxRow(DashboardBox box)
        {
            var row = new StackPanel(); row.Children.Add(Text(box.Label + " · n=" + box.Sample.ToString("N0"), 12, weight: FontWeights.SemiBold));
            var canvas = new Canvas { Width = 500, Height = 36 };
            double X(double value) => 6 + 480 * value / maximum;
            var whisker = new Line { X1 = X(box.Minimum), X2 = X(box.Maximum), Y1 = 18, Y2 = 18, Stroke = Accent("slate"), StrokeThickness = 1.5 }; canvas.Children.Add(whisker);
            foreach (var value in new[] { box.Minimum, box.Maximum }) canvas.Children.Add(new Line { X1 = X(value), X2 = X(value), Y1 = 10, Y2 = 26, Stroke = Accent("slate"), StrokeThickness = 1.5 });
            var rectangle = new Rectangle { Width = Math.Max(2, X(box.ThirdQuartile) - X(box.FirstQuartile)), Height = 20, Fill = Accent(widget.Accent), Opacity = .65, RadiusX = 3, RadiusY = 3 };
            Canvas.SetLeft(rectangle, X(box.FirstQuartile)); Canvas.SetTop(rectangle, 8); canvas.Children.Add(rectangle);
            var median = new Line { X1 = X(box.Median), X2 = X(box.Median), Y1 = 5, Y2 = 31, StrokeThickness = 2 }; median.SetResourceReference(Shape.StrokeProperty, "TextBrush"); canvas.Children.Add(median);
            var button = Command(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Height = 36 }, () => RowsRequested?.Invoke(box.Rows, series.Title + " · " + box.Label));
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(0); button.Margin = new Thickness(0); row.Children.Add(button);
            row.Children.Add(Text($"Min {box.Minimum:0.##} · Q1 {box.FirstQuartile:0.##} · Médiane {box.Median:0.##} · Q3 {box.ThirdQuartile:0.##} · P90 {box.P90:0.##} · Max {box.Maximum:0.##} {series.Unit}", 10, "MutedBrush", margin: new Thickness(0, 0, 0, 12)));
            return row;
        }
        foreach (var box in series.Boxes.Take(widget.TopCount)) panel.Children.Add(BoxRow(box));
        panel.Children.Add(Command("Tous les segments", () => OpenDetails(widget, series.Title, body => PagedDetails(body, series.Boxes, x => x.Label, BoxRow, "segments"))));
        panel.Children.Add(Details("Lire la dispersion", Text(series.Explanation + " Le rectangle va de Q1 à Q3 ; la ligne centrale est la médiane ; les moustaches représentent le minimum et le maximum observés. Un faible effectif limite l’interprétation.", 11, "MutedBrush")));
        return panel;
    }

    private FrameworkElement ScatterContent(DashboardWidgetSettings widget)
    {
        var series = _data!.Scatters.FirstOrDefault(x => x.Key == widget.SourceKey);
        if (series == null || series.Points.Count == 0) return Missing("Aucune ligne active avec une ouverture et une échéance exploitables.");
        var panel = new StackPanel(); var canvas = new Canvas { Width = 600, Height = 310 };
        var minX = Math.Min(0, series.Points.Min(x => x.X)); var maxX = Math.Max(minX + 1, series.Points.Max(x => x.X));
        var minY = Math.Min(0, series.Points.Min(x => x.Y)); var maxY = Math.Max(minY + 1, series.Points.Max(x => x.Y));
        double X(double value) => 50 + 515 * (value - minX) / (maxX - minX);
        double Y(double value) => 258 - 220 * (value - minY) / (maxY - minY);
        canvas.Children.Add(new Line { X1 = 50, X2 = 565, Y1 = Y(0), Y2 = Y(0), Stroke = Accent("slate"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
        var shown = series.Points.OrderBy(x => x.Y).ThenByDescending(x => x.X).Take(400).ToArray();
        foreach (var point in shown)
        {
            var marker = Command(new Ellipse { Width = 7, Height = 7, Fill = Accent(point.Y < 0 ? "red" : widget.Accent), Opacity = .8 }, () => RowsRequested?.Invoke(new[] { point.Row }, point.Label));
            marker.Padding = new Thickness(1); marker.Margin = new Thickness(0); marker.MinWidth = marker.MinHeight = 0; marker.BorderThickness = new Thickness(0); marker.Background = Brushes.Transparent;
            Help(marker, point.Label, point.Label + $"\nÂge : {point.X:0.##} j · avant échéance : {point.Y:0.##} h");
            Canvas.SetLeft(marker, X(point.X) - 4); Canvas.SetTop(marker, Y(point.Y) - 4); canvas.Children.Add(marker);
        }
        var xLabel = Text(series.XLabel + $" · {minX:0.#} → {maxX:0.#}", 11, "MutedBrush"); Canvas.SetLeft(xLabel, 50); Canvas.SetTop(xLabel, 280); canvas.Children.Add(xLabel);
        var yLabel = Text(series.YLabel + $" · {minY:0.#} → {maxY:0.#}", 11, "MutedBrush"); Canvas.SetLeft(yLabel, 50); Canvas.SetTop(yLabel, 5); canvas.Children.Add(yLabel);
        panel.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 310 });
        panel.Children.Add(Text($"{shown.Length:N0} / {series.Points.Count:N0} points affichés, classés par échéance la plus ancienne. Les axes couvrent toutes les valeurs.", 10, "MutedBrush"));
        panel.Children.Add(Command("Toutes les valeurs", () => OpenDetails(widget, series.Title, body =>
            PagedDetails(body, series.Points, x => x.Label, point => Command(point.Label + $" · {point.X:0.##} j · {point.Y:0.##} h", () => RowsRequested?.Invoke(new[] { point.Row }, point.Label)), "tickets"))));
        if (widget.ShowDescription) panel.Children.Add(Details("Méthode", Text(series.Explanation, 11, "MutedBrush")));
        return panel;
    }

    private FrameworkElement HistoryContent(DashboardWidgetSettings widget)
    {
        var series = _data!.History.FirstOrDefault(x => x.Key == widget.SourceKey);
        if (series == null || series.Points.Count == 0) return Missing("Aucun historique comparable pour ce périmètre. Conservez plusieurs relevés complets avec la même recherche, les mêmes colonnes et les mêmes règles.");
        var panel = new StackPanel(); var all = series.Points.OrderBy(x => x.CapturedAt).ToArray();
        var trusted = all.Where(x => x.Comparable && x.Complete && x.Value.HasValue).ToArray();
        panel.Children.Add(Text($"{trusted.Length:N0} points comparables / {all.Length:N0} relevés · {series.Unit}", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 7)));
        if (trusted.Length == 0) { panel.Children.Add(Text("Les relevés conservés ne permettent pas encore de tracer cette série.", 12, "MutedBrush")); return panel; }
        var canvas = new Canvas { Width = 600, Height = 220 };
        var start = all[0].CapturedAt; var span = Math.Max(1, (all[^1].CapturedAt - start).TotalSeconds); var maximum = Math.Max(1, trusted.Max(x => x.Value!.Value));
        Point? previous = null;
        foreach (var point in all.TakeLast(500))
        {
            var x = 30 + 540 * (point.CapturedAt - start).TotalSeconds / span;
            if (!point.Comparable || !point.Complete || point.Value is not double value)
            {
                previous = null;
                var missing = new Line { X1 = x, X2 = x, Y1 = 30, Y2 = 180, Stroke = Accent("orange"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 }, ToolTip = point.Note }; canvas.Children.Add(missing); continue;
            }
            var position = new Point(x, 180 - 150 * value / maximum);
            if (previous is Point before) canvas.Children.Add(new Line { X1 = before.X, X2 = position.X, Y1 = before.Y, Y2 = position.Y, Stroke = Accent(widget.Accent), StrokeThickness = 2 });
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Accent(widget.Accent), ToolTip = point.CapturedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm") + " · " + value.ToString("G", CultureInfo.CurrentCulture) + " " + series.Unit };
            Canvas.SetLeft(dot, position.X - 3); Canvas.SetTop(dot, position.Y - 3); canvas.Children.Add(dot); previous = position;
        }
        var dates = Text(all[0].CapturedAt.ToLocalTime().ToString("dd/MM HH:mm") + " → " + all[^1].CapturedAt.ToLocalTime().ToString("dd/MM HH:mm"), 11, "MutedBrush"); Canvas.SetLeft(dates, 30); Canvas.SetTop(dates, 197); canvas.Children.Add(dates);
        panel.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 220 });
        if (all.Length >= 2 && all[^1] is { Comparable: true, Complete: true, Value: double lastValue }
            && all[^2] is { Comparable: true, Complete: true, Value: double previousValue })
        {
            var delta = lastValue - previousValue;
            panel.Children.Add(Text($"Dernière variation observée : {delta:+0.##;-0.##;0} {series.Unit}", 14, weight: FontWeights.SemiBold));
        }
        panel.Children.Add(Command("Relevés et ruptures", () => OpenDetails(widget, series.Title, body =>
            PagedDetails(body, all.Reverse().ToArray(), x => x.CapturedAt.ToString("O") + " " + x.Note,
                point => Text(point.CapturedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") + " · " + (point.Comparable && point.Value.HasValue ? point.Value.Value.ToString("G", CultureInfo.CurrentCulture) + " " + series.Unit : "Non comparable") + "\n" + point.Note, 12), "relevés"))));
        if (widget.ShowDescription) panel.Children.Add(Details("Périmètre et continuité", Text(series.Explanation + " Les traits orange interrompent la courbe aux relevés incompatibles. Une ligne relie deux observations et ne décrit pas les événements entre elles.", 11, "MutedBrush")));
        return panel;
    }
}
