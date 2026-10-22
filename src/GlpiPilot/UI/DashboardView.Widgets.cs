using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private FrameworkElement WidgetContent(DashboardWidgetSettings widget)
    {
        if (widget.Kind == "note")
        {
            var note = string.IsNullOrWhiteSpace(widget.Note) ? "Ajoutez votre consigne, votre contexte ou vos repères dans les options de ce widget." : widget.Note;
            var panel = new StackPanel(); panel.Children.Add(Text(PreviewText(note, 300), 13, string.IsNullOrWhiteSpace(widget.Note) ? "MutedBrush" : "TextBrush"));
            if (note.Length > 300) panel.Children.Add(Details("Lire la note complète", Text(note, 13)));
            return panel;
        }
        if (_data is null)
        {
            var empty = new StackPanel();
            empty.Children.Add(Text(widget.Kind == "metric" ? "—" : "En attente du relevé", widget.Kind == "metric" ? 38 : 15, "MutedBrush", FontWeights.SemiBold));
            empty.Children.Add(Text(widget.Kind switch
            {
                "metric" => "Cet indicateur se calculera après le chargement des données.",
                "distribution" => "La répartition utilisera les valeurs réellement présentes dans la colonne associée.",
                "weather" => "La météo résumera les signaux disponibles dans le relevé.",
                "timeline" => "L’activité affichera les dates présentes dans les tickets récupérés.",
                "attention" => "Les tickets à regarder apparaîtront ici selon leurs échéances, priorités et ancienneté.",
                _ => "Les informations du relevé apparaîtront ici après son chargement."
            }, 12, "MutedBrush", margin: new Thickness(0, 10, 0, 0)));
            return empty;
        }
        return widget.Kind switch
        {
            "metric" => MetricContent(widget), "distribution" => DistributionContent(widget),
            "weather" => WeatherContent(widget), "timeline" => TimelineContent(widget),
            "attention" => AttentionContent(widget), "quality" => QualityContent(widget),
            "matrix" => MatrixContent(widget), "histogram" => HistogramContent(widget), "boxplot" => BoxContent(widget),
            "scatter" => ScatterContent(widget), "history" => HistoryContent(widget),
            _ => Text("Type de widget non disponible.", 13, "MutedBrush")
        };
    }

    private FrameworkElement MetricContent(DashboardWidgetSettings widget)
    {
        var metric = _data!.Metric(widget.SourceKey);
        if (metric is null) return Missing("Cet indicateur n’existe pas dans le relevé. Choisissez une autre source du widget.");
        if (widget.Presentation == "gauge") return GaugeContent(widget, metric);
        var panel = new StackPanel();
        var number = metric.Value.HasValue ? metric.Value.Value.ToString(metric.Unit is "h" or "j" or "%" ? "N1" : "N0", CultureInfo.CurrentCulture) : "—";
        var suffix = metric.Value.HasValue && metric.Unit is "h" or "j" or "%" ? " " + metric.Unit : "";
        var value = Text(number + suffix, widget.Presentation == "compact" ? 30 : 36, weight: FontWeights.SemiBold);
        if (metric.Value.HasValue && metric.Value > 0 && metric.Tone is "warning" or "error") value.SetResourceReference(TextBlock.ForegroundProperty, metric.Tone == "error" ? "ErrorBrush" : "WarningBrush");
        else value.Foreground = Accent(widget.Accent);
        panel.Children.Add(value);
        panel.Children.Add(Text(metric.Value.HasValue ? metric.Sample.HasValue ? $"Échantillon : {metric.Sample:N0}" : metric.Unit : "Données indisponibles", 11, "MutedBrush", margin: new Thickness(0, 5, 0, 0)));
        var action = Command(panel, () => ActivateRows(metric.Rows, WidgetTitle(widget), metricKey: metric.Key), metric.Value.HasValue && (metric.Rows.Length > 0 || metric.Value == 0));
        action.Background = Brushes.Transparent; action.BorderThickness = new Thickness(0); action.Padding = new Thickness(0); action.Margin = new Thickness(0); action.HorizontalContentAlignment = HorizontalAlignment.Stretch; action.VerticalContentAlignment = VerticalAlignment.Top; action.ToolTip = metric.Explanation;
        var content = new StackPanel(); content.Children.Add(action);
        if (widget.ShowDescription && widget.Presentation != "compact") content.Children.Add(Details("Comprendre cet indicateur", Text(metric.Explanation, 11, "MutedBrush")));
        return content;
    }

    private FrameworkElement GaugeContent(DashboardWidgetSettings widget, DashboardMetric metric)
    {
        var panel = new StackPanel();
        var available = metric.Value is double number && double.IsFinite(number);
        var valueText = available ? MetricNumber(metric.Value!.Value, metric.Unit) : "—";
        panel.Children.Add(Text(valueText, 31, weight: FontWeights.SemiBold));
        panel.Children.Add(Text(metric.Sample.HasValue ? $"Échantillon : {metric.Sample:N0}" : available ? metric.Unit : "Données indisponibles", 11, "MutedBrush", margin: new Thickness(0, 5, 0, 0)));
        if (!available)
        {
            panel.Children.Add(Text("Données indisponibles : aucun niveau ni pourcentage n’est affiché.", 12, "MutedBrush", margin: new Thickness(0, 13, 0, 8)));
            panel.Children.Add(Command("Vérifier les colonnes", () => SettingsRequested?.Invoke()));
            if (widget.TargetValue is not double configuredTarget || !double.IsFinite(configuredTarget) || configuredTarget <= 0)
                panel.Children.Add(Command("Renseigner un objectif", () => OpenEditor("widget", widget.Id)));
            if (widget.ShowDescription) panel.Children.Add(Details("Comprendre cet indicateur", Text(metric.Explanation, 11, "MutedBrush")));
            return panel;
        }
        if (widget.TargetValue is not double target || !double.IsFinite(target) || target <= 0)
        {
            panel.Children.Add(Text("Renseigner un objectif", 14, "MutedBrush", FontWeights.SemiBold, new Thickness(0, 16, 0, 6)));
            panel.Children.Add(Text("Choisissez un nombre strictement positif, dans l’unité de cet indicateur, pour activer la jauge.", 11, "MutedBrush"));
            panel.Children.Add(Command("Régler l’objectif", () => OpenEditor("widget", widget.Id)));
            if (widget.ShowDescription) panel.Children.Add(Details("Comprendre cet indicateur", Text(metric.Explanation, 11, "MutedBrush")));
            return panel;
        }

        var value = metric.Value!.Value;
        bool Reached(double? threshold) => threshold is double limit && double.IsFinite(limit)
            && (widget.LowerIsBetter ? value >= limit : value <= limit);
        var critical = Reached(widget.CriticalValue); var warning = !critical && Reached(widget.WarningValue);
        var achieved = widget.LowerIsBetter ? value <= target : value >= target;
        var tone = critical ? "ErrorBrush" : warning ? "WarningBrush" : achieved ? "SuccessBrush" : "AccentBrush";
        var fraction = value <= 0 ? 0 : value >= target ? 1 : value / target;
        var percentage = value / target * 100d;
        var ratio = double.IsFinite(percentage) ? percentage.ToString("G", CultureInfo.CurrentCulture) + " % de l’objectif"
            : "Rapport trop grand pour un pourcentage numérique ; valeurs exactes affichées";
        var gauge = new Canvas { Width = 240, Height = 132, IsHitTestVisible = false };
        var track = ArcStroke(120, 114, 92, 180, 180, 14); track.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FieldBrush"); gauge.Children.Add(track);
        if (fraction > 0)
        {
            var fill = ArcStroke(120, 114, 92, 180, 180 * fraction, 14); fill.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, tone); gauge.Children.Add(fill);
        }
        foreach (var (threshold, brush) in new[] { (widget.WarningValue, "WarningBrush"), (widget.CriticalValue, "ErrorBrush") })
        {
            if (threshold is not double mark || !double.IsFinite(mark) || mark < 0 || mark > target) continue;
            var angle = 180 + 180 * (mark / target); var start = Polar(120, 114, 81, angle); var end = Polar(120, 114, 103, angle);
            var marker = new System.Windows.Shapes.Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, StrokeThickness = 2 };
            marker.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush); gauge.Children.Add(marker);
        }
        var center = new StackPanel { Width = 152 };
        var direction = Text(widget.LowerIsBetter ? "Plus bas est préférable" : "Plus haut est préférable", 11, "MutedBrush"); direction.TextAlignment = TextAlignment.Center; center.Children.Add(direction);
        var targetText = Text("Objectif\n" + MetricNumber(target, metric.Unit), 12, weight: FontWeights.SemiBold, margin: new Thickness(0, 7, 0, 0)); targetText.TextAlignment = TextAlignment.Center; center.Children.Add(targetText);
        Canvas.SetLeft(center, 44); Canvas.SetTop(center, 65); gauge.Children.Add(center);
        var graphic = new Viewbox { Child = gauge, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 270, Height = 132, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(graphic, "Jauge. " + valueText + ". Objectif " + MetricNumber(target, metric.Unit) + ". " + ratio);
        panel.Children.Add(graphic);
        panel.Children.Add(Text(ratio, 12, tone, FontWeights.SemiBold, new Thickness(0, 5, 0, 0)));
        panel.Children.Add(Text(critical ? "Seuil critique atteint" : warning ? "Seuil de vigilance atteint" : achieved ? "Objectif atteint" : widget.LowerIsBetter ? "Valeur au-dessus de l’objectif" : "Objectif à atteindre", 11, tone, margin: new Thickness(0, 5, 0, 0)));
        var thresholds = new List<string>(); var comparison = widget.LowerIsBetter ? "≥" : "≤";
        if (widget.WarningValue is double vigilance) thresholds.Add("Vigilance " + comparison + " " + MetricNumber(vigilance, metric.Unit));
        if (widget.CriticalValue is double alert) thresholds.Add("Critique " + comparison + " " + MetricNumber(alert, metric.Unit));
        if (thresholds.Count > 0) panel.Children.Add(Text(string.Join(" · ", thresholds), 10, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        if (percentage > 100 || percentage < 0 || !double.IsFinite(percentage)) panel.Children.Add(Text("Le dessin est borné de 0 à 100 % ; la valeur et l’objectif restent affichés sans plafonnement.", 10, "MutedBrush", margin: new Thickness(0, 6, 0, 0)));
        if (metric.Rows.Length > 0 || value == 0)
        {
            var rows = Command("Voir les lignes de l’indicateur", () => RowsRequested?.Invoke(metric.Rows, WidgetTitle(widget))); rows.Margin = new Thickness(0, 10, 0, 5); panel.Children.Add(rows);
        }
        if (widget.ShowDescription)
        {
            var explanation = new StackPanel(); explanation.Children.Add(Text(metric.Explanation, 11, "MutedBrush"));
            explanation.Children.Add(Text("Objectif et seuils de présentation choisis pour ce widget ; ils ne définissent pas un engagement SLA.", 10, "MutedBrush", margin: new Thickness(0, 6, 0, 0)));
            panel.Children.Add(Details("Méthode et objectif de la jauge", explanation));
        }
        return panel;
    }

    private static string MetricNumber(double value, string unit) => value.ToString("G", CultureInfo.CurrentCulture) + (string.IsNullOrWhiteSpace(unit) ? "" : " " + unit);
    private static Point Polar(double x, double y, double radius, double angle)
    {
        var radians = angle * Math.PI / 180d; return new Point(x + radius * Math.Cos(radians), y + radius * Math.Sin(radians));
    }
    private static System.Windows.Shapes.Path ArcStroke(double x, double y, double radius, double startAngle, double sweep, double thickness)
    {
        var figure = new PathFigure { StartPoint = Polar(x, y, radius, startAngle), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(Polar(x, y, radius, startAngle + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure }); geometry.Freeze();
        return new System.Windows.Shapes.Path { Data = geometry, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    }

    private FrameworkElement DistributionContent(DashboardWidgetSettings widget)
    {
        var dist = _data!.Distributions.FirstOrDefault(x => x.Key == widget.SourceKey);
        if (dist is null) return Missing("Cette répartition n’existe pas dans le relevé. Choisissez une autre source du widget.");
        if (!dist.Available) return Missing("Colonne absente ou ambiguë. Réglez sa correspondance pour activer ce graphique.");
        if (dist.Buckets.Count == 0) return Text("Aucune ligne dans ce périmètre.", 13, "MutedBrush");
        if (widget.Presentation == "donut" && !dist.Buckets.Any(x => x.IsAggregate)) return DonutContent(widget, dist);
        if (widget.Presentation is "pareto" or "columns" && !dist.Buckets.Any(x => x.IsAggregate)) return ColumnsContent(widget, dist, widget.Presentation == "pareto");
        var panel = new StackPanel(); var maximum = Math.Max(1, dist.Buckets.Max(BucketMagnitude));
        if (dist.Buckets.Any(x => x.IsAggregate)) panel.Children.Add(Text("Valeurs agrégées par groupe · échantillons distincts. Les moyennes et percentiles ne sont pas additionnés ni transformés en parts de cercle.", 10, "MutedBrush", margin: new Thickness(0, 0, 0, 8)));
        panel.Children.Add(Text($"Base : {dist.Denominator:N0} lignes · {Math.Min(widget.TopCount, dist.Buckets.Count):N0} / {dist.Buckets.Count:N0} catégories affichées", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 7)));
        foreach (var bucket in dist.Buckets.Take(widget.TopCount))
            panel.Children.Add(DistributionRow(dist, bucket, widget, widget.Presentation is not ("compact" or "list"), maximum));
        var complete = DescribedCommand($"Voir les {dist.Buckets.Count:N0} catégories", "Ouvrir la liste complète, avec recherche et pagination, dans le dashboard.", () => OpenDistribution(widget, dist));
        AutomationProperties.SetAutomationId(complete, "Dashboard.Distribution.Open");
        complete.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(complete);
        if (widget.ShowDescription) panel.Children.Add(Details("Lire la méthode de cette répartition", Text(dist.Note, 11, "MutedBrush")));
        return panel;
    }

    private FrameworkElement DistributionRow(DashboardDistribution distribution, DashboardBucket bucket,
        DashboardWidgetSettings widget, bool showBar, double maximum, bool fullLabel = false)
    {
        var row = new StackPanel(); var heading = new Grid();
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var label = Text(fullLabel ? bucket.Label : PreviewText(bucket.Label, 100), 12);
        label.ToolTip = bucket.Label; heading.Children.Add(label);
        var percentage = !bucket.IsAggregate && distribution.Denominator > 0 ? $" · {100d * bucket.Count / distribution.Denominator:0.#} %" : "";
        var count = Text(bucket.IsAggregate ? (bucket.Value is double value ? MetricNumber(value, bucket.Unit) : "Indisponible") + " · n=" + (bucket.Sample ?? bucket.Count).ToString("N0") : $"{bucket.Count:N0}" + percentage, 11, "MutedBrush", margin: new Thickness(12, 0, 0, 0));
        Grid.SetColumn(count, 1); heading.Children.Add(count); row.Children.Add(heading);
        if (showBar && (!bucket.IsAggregate || bucket.Value.HasValue)) row.Children.Add(new ProgressBar { Maximum = Math.Max(1, maximum), Value = BucketMagnitude(bucket), Height = 5, Margin = new Thickness(0, 7, 0, 0), Foreground = Accent(bucket.Value < 0 ? "orange" : widget.Accent), ToolTip = bucket.IsAggregate ? "Longueur proportionnelle à la valeur absolue ; valeur signée affichée." : null });
        var button = Command(row, () => ActivateRows(bucket.Rows, distribution.Title + " · " + bucket.Label,
            _baseline?.Distributions.Any(x => x.Key == distribution.Key) == true ? distribution.Key : null, bucket.Label));
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0, 5, 0, 7); button.Margin = new Thickness(0, 0, 0, 3);
        Help(button, bucket.Label + $" : {bucket.Count:N0}", bucket.Label + $" · {bucket.Count:N0} lignes. Afficher les lignes correspondantes.");
        return button;
    }

    private static double BucketMagnitude(DashboardBucket bucket) => bucket.IsAggregate ? Math.Abs(bucket.Value ?? 0) : bucket.Count;
    private sealed record DonutPart(string Label, long Count, Func<int[]> Rows, bool Others = false);

    private FrameworkElement DonutContent(DashboardWidgetSettings widget, DashboardDistribution distribution)
    {
        var panel = new StackPanel(); var total = distribution.Buckets.Sum(bucket => (long)Math.Max(0, bucket.Count));
        if (total == 0)
        {
            panel.Children.Add(Text("Aucune occurrence positive à représenter.", 12, "MutedBrush"));
            panel.Children.Add(Command("Consulter les catégories", () => OpenDistribution(widget, distribution)));
            return panel;
        }
        var parts = distribution.Buckets.Take(widget.TopCount).Select(bucket => new DonutPart(bucket.Label, bucket.Count, () => bucket.Rows)).ToList();
        if (distribution.Buckets.Count > widget.TopCount)
        {
            var remainder = distribution.Buckets.Skip(widget.TopCount).ToArray();
            parts.Add(new DonutPart("Autres", remainder.Sum(bucket => (long)Math.Max(0, bucket.Count)),
                () => remainder.SelectMany(bucket => bucket.Rows).Distinct().ToArray(), true));
        }
        var chart = new Grid { Width = 216, Height = 216, IsHitTestVisible = false };
        var palette = new[] { widget.Accent }.Concat(new[] { "teal", "purple", "orange", "green", "blue", "red", "slate" }.Where(accent => accent != widget.Accent)).ToArray();
        var angle = -90d;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index]; if (part.Count <= 0) continue;
            var sweep = 360d * part.Count / total;
            var shape = new System.Windows.Shapes.Path { Data = RingSlice(108, 108, 91, 65, angle, sweep), Fill = Accent(part.Others ? "slate" : palette[index % palette.Length]), StrokeThickness = 1, Width = 216, Height = 216, Stretch = Stretch.None };
            shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PanelBrush"); chart.Children.Add(shape); angle += sweep;
        }
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 119 };
        var sum = Text(total.ToString("N0", CultureInfo.CurrentCulture), 25, weight: FontWeights.SemiBold); sum.TextAlignment = TextAlignment.Center; center.Children.Add(sum);
        var occurrences = Text("occurrences", 11, "MutedBrush", margin: new Thickness(0, 4, 0, 0)); occurrences.TextAlignment = TextAlignment.Center; center.Children.Add(occurrences); chart.Children.Add(center);
        var graphic = new Viewbox { Child = chart, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Height = 190, MaxWidth = 216, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(graphic, distribution.Title + $". {total:N0} occurrences, {distribution.Buckets.Count:N0} catégories. La légende donne tous les nombres et l’accès aux lignes.");
        var legend = new StackPanel();
        legend.Children.Add(Text($"Base : {distribution.Denominator:N0} lignes · {distribution.Buckets.Count:N0} catégories", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 9)));
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index]; var content = new Grid();
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Accent(part.Others ? "slate" : palette[index % palette.Length]), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 8, 0) }; content.Children.Add(swatch);
            var label = Text(part.Label, 12); Grid.SetColumn(label, 1); content.Children.Add(label);
            var numbers = Text($"{part.Count:N0} · {100d * part.Count / total:0.#} %", 11, "MutedBrush", margin: new Thickness(8, 0, 0, 0)); Grid.SetColumn(numbers, 2); content.Children.Add(numbers);
            var button = Command(content, () => ActivateRows(part.Rows(), distribution.Title + " · " + part.Label,
                !part.Others && _baseline?.Distributions.Any(x => x.Key == distribution.Key) == true ? distribution.Key : null, part.Label));
            button.Padding = new Thickness(5, 7, 5, 7); button.Margin = new Thickness(0, 0, 0, 2); button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
            Help(button, part.Label + $" : {part.Count:N0} occurrences", part.Others
                ? $"{part.Count:N0} occurrences regroupées. Afficher l’union des lignes des catégories Autres ; une ligne commune n’est ouverte qu’une fois. Le tableau complet détaille les catégories."
                : part.Label + $" · {part.Count:N0} occurrences · {100d * part.Count / total:0.#} % des occurrences. Afficher les lignes correspondantes.");
            legend.Children.Add(button);
        }
        panel.Children.Add(ResponsivePair(graphic, legend, 520, 208));
        panel.Children.Add(Text("Les parts comparent les occurrences des catégories, pas un pourcentage de tickets distincts.", 10, "MutedBrush", margin: new Thickness(0, 9, 0, 0)));
        var complete = DescribedCommand($"Voir les {distribution.Buckets.Count:N0} catégories", "Ouvrir la liste complète, y compris les catégories regroupées dans Autres.", () => OpenDistribution(widget, distribution));
        AutomationProperties.SetAutomationId(complete, "Dashboard.Distribution.Open");
        complete.Margin = new Thickness(0, 10, 0, 0); panel.Children.Add(complete);
        if (widget.ShowDescription) panel.Children.Add(Details("Lire la méthode de cette répartition", Text(distribution.Note, 11, "MutedBrush")));
        return panel;
    }

    private static Geometry RingSlice(double x, double y, double outer, double inner, double start, double sweep)
    {
        if (sweep >= 360d)
        {
            var circle = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(x, y), outer, outer), new EllipseGeometry(new Point(x, y), inner, inner)); circle.Freeze(); return circle;
        }
        var figure = new PathFigure { StartPoint = Polar(x, y, outer, start), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(Polar(x, y, outer, start + sweep), new Size(outer, outer), 0, sweep > 180, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(Polar(x, y, inner, start + sweep), true));
        figure.Segments.Add(new ArcSegment(Polar(x, y, inner, start), new Size(inner, inner), 0, sweep > 180, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry(new[] { figure }); geometry.Freeze(); return geometry;
    }

    private FrameworkElement WeatherContent(DashboardWidgetSettings widget)
    {
        var weather = _data!.WeatherProfiles.GetValueOrDefault(string.IsNullOrEmpty(widget.SourceKey) ? "overall" : widget.SourceKey) ?? _data.Weather; var tone = weather.State switch { "sun" => "SuccessBrush", "storm" => "ErrorBrush", "unknown" or "partial" => "MutedBrush", _ => "WarningBrush" };
        var panel = new StackPanel(); var heading = new DockPanel();
        var icon = Text(weather.Symbol, 36, tone, margin: new Thickness(0, 0, 13, 0)); icon.FontFamily = new FontFamily("Segoe UI Symbol"); DockPanel.SetDock(icon, Dock.Left); heading.Children.Add(icon);
        var title = new StackPanel(); title.Children.Add(Text(weather.Title, 20, tone, FontWeights.SemiBold));
        title.Children.Add(Text((weather.Score.HasValue ? $"Indice {weather.Score:0.#}/100" : "Indice indisponible")
            + $" · poids disponibles {weather.MeasuredWeight}", 11, "MutedBrush", margin: new Thickness(0, 5, 0, 0)));
        heading.Children.Add(title); panel.Children.Add(heading);
        if (weather.Score.HasValue)
        {
            var progress = new ProgressBar { Maximum = 100, Value = weather.Score.Value, Height = 4, Margin = new Thickness(0, 10, 0, 10) }; progress.SetResourceReference(Control.ForegroundProperty, tone); panel.Children.Add(progress);
        }
        if (widget.Presentation != "compact" && weather.Signals.Count > 0)
        {
            var signals = AdaptiveTiles(weather.Signals.Count, 180);
            foreach (var signal in weather.Signals)
            {
                var signalPanel = new StackPanel();
                signalPanel.Children.Add(Text(signal.Label, 11, "MutedBrush"));
                signalPanel.Children.Add(Text(signal.Sample > 0 ? $"{signal.Count:N0} / {signal.Sample:N0} · {100d * signal.Count / signal.Sample:0.#} %" : "— · aucun échantillon", 17, weight: FontWeights.SemiBold, margin: new Thickness(0, 5, 0, 4)));
                signalPanel.Children.Add(Text($"Échantillon du signal : {signal.Sample:N0} · population : {signal.Population:N0}", 10, "MutedBrush"));
                var button = Command(signalPanel, () => RowsRequested?.Invoke(signal.Rows, signal.Label), signal.Sample > 0);
                button.Padding = new Thickness(9); button.Margin = new Thickness(0, 3, 7, 4);
                Help(button, signal.Label, signal.Explanation); signals.Children.Add(button);
            }
            panel.Children.Add(signals);
        }
        if (widget.ShowDescription)
        {
            var explanation = new StackPanel(); explanation.Children.Add(Text(weather.Description, 12, "MutedBrush"));
            explanation.Children.Add(Text("La formule locale, ses seuils et la couverture nécessaire sont configurables dans Colonnes et seuils. Chaque signal garde son propre échantillon. Les données absentes ne deviennent pas des zéros favorables.", 11, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
            var details = Details("Comprendre la météo et ses signaux", explanation);
            AutomationProperties.SetAutomationId(details, "Dashboard.Weather.Details"); panel.Children.Add(details);
        }
        return panel;
    }

    private FrameworkElement TimelineContent(DashboardWidgetSettings widget)
    {
        var data = _data!; if (data.Timeline.Count == 0) return Text("Aucune date disponible.", 13, "MutedBrush");
        var panel = new StackPanel();
        var keys = new[] { ("created", "Créations", "ActionBrush"), ("resolved", "Résolutions", "SuccessBrush"), ("closed", "Clôtures", "MutedBrush") };
        var legend = new WrapPanel(); foreach (var (key, label, brush) in keys) legend.Children.Add(Text(label + (data.Column(key)?.Index.HasValue == true ? "" : " · indisponible"), 11, brush, margin: new Thickness(0, 0, 18, 10))); panel.Children.Add(legend);
        var grouping = Text("", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 7)); panel.Children.Add(grouping);
        var chartHost = new Grid(); panel.Children.Add(chartHost);
        var chartButtons = new HashSet<Button>(); bool? weekly = null;
        void BuildChart(bool groupByWeek)
        {
            if (weekly == groupByWeek) return; weekly = groupByWeek;
            _commands.RemoveAll(pair => chartButtons.Contains(pair.Button)); chartButtons.Clear(); chartHost.Children.Clear();
            var periods = groupByWeek
                ? data.Timeline.GroupBy(day => day.Date.Date.AddDays(-((7 + (int)day.Date.DayOfWeek - 1) % 7)))
                    .Select(days => new TimelinePeriod(days.Min(day => day.Date), days.Max(day => day.Date),
                        days.SelectMany(day => day.Created).Distinct().ToArray(), days.SelectMany(day => day.Resolved).Distinct().ToArray(), days.SelectMany(day => day.Closed).Distinct().ToArray())).ToArray()
                : data.Timeline.Select(day => new TimelinePeriod(day.Date, day.Date, day.Created, day.Resolved, day.Closed)).ToArray();
            grouping.Text = groupByWeek ? "Vue par semaine · détail quotidien disponible ci-dessous" : "Vue par jour";
            var maximum = Math.Max(1, periods.SelectMany(period => new[] { period.Created.Length, period.Resolved.Length, period.Closed.Length }).Max());
            var chart = new UniformGrid { Columns = periods.Length, Rows = 1, Height = 164 };
            foreach (var period in periods)
            {
                var column = new StackPanel(); var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 130, HorizontalAlignment = HorizontalAlignment.Center };
                var series = new[] { period.Created, period.Resolved, period.Closed };
                var range = period.Start == period.End ? period.Start.ToString("dd/MM/yyyy") : $"{period.Start:dd/MM/yyyy}–{period.End:dd/MM/yyyy}";
                for (var index = 0; index < keys.Length; index++)
                {
                    var (key, label, brush) = keys[index]; var rows = series[index]; var available = data.Column(key)?.Index.HasValue == true;
                    var bar = new Border { Height = Math.Max(rows.Length > 0 ? 3 : 1, 125d * rows.Length / maximum), Width = groupByWeek ? 8 : 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom };
                    bar.SetResourceReference(Border.BackgroundProperty, brush); if (!available) bar.Opacity = .2;
                    var button = Command(bar, () => RowsRequested?.Invoke(rows, label + " · " + range), available);
                    button.Padding = new Thickness(0); button.Margin = new Thickness(1, 0, 1, 0); button.MinHeight = 0; button.BorderThickness = new Thickness(0); button.Background = Brushes.Transparent; button.VerticalAlignment = VerticalAlignment.Bottom;
                    Help(button, range + " · " + label, range + " · " + label + " : " + (available ? rows.Length.ToString("N0") : "indisponible")); bars.Children.Add(button); chartButtons.Add(button);
                }
                column.Children.Add(bars);
                var date = Text(period.Start.ToString(groupByWeek ? "dd/MM" : "dd"), 10, "MutedBrush", margin: new Thickness(0, 8, 0, 0)); date.TextAlignment = TextAlignment.Center; date.ToolTip = range;
                column.Children.Add(date); chart.Children.Add(column);
            }
            chartHost.Children.Add(chart);
        }
        BuildChart(true); chartHost.SizeChanged += (_, e) => BuildChart(e.NewSize.Width < 650);
        panel.Children.Add(Text($"Du {data.Timeline[0].Date:dd/MM/yyyy} au {data.Timeline[^1].Date:dd/MM/yyyy}", 11, "MutedBrush", margin: new Thickness(0, 4, 0, 7)));
        var complete = DescribedCommand("Voir le détail quotidien", "Consulter toutes les journées et ouvrir les lignes de chaque type d’événement.", () => OpenTimeline(widget, data));
        AutomationProperties.SetAutomationId(complete, "Dashboard.Timeline.Open"); panel.Children.Add(complete);
        if (widget.ShowDescription) panel.Children.Add(Details("Comprendre cette activité", Text("Événements des tickets présents dans ce relevé. Une recherche limitée aux tickets ouverts ne décrit pas le flux global des clôtures. Sur une carte étroite, les jours visibles sont regroupés par semaine civile ; le détail quotidien reste intégralement accessible.", 11, "MutedBrush")));
        return panel;
    }

    private sealed record TimelinePeriod(DateTime Start, DateTime End, int[] Created, int[] Resolved, int[] Closed);

    private FrameworkElement AttentionContent(DashboardWidgetSettings widget)
    {
        var panel = new StackPanel(); var attention = _data!.Attention; var previewCount = widget.Presentation == "compact" ? 3 : 5;
        if (attention.Count == 0) { panel.Children.Add(Text("Aucune ligne à signaler selon les critères disponibles.", 12, "MutedBrush")); return panel; }
        panel.Children.Add(Text($"{Math.Min(previewCount, attention.Count):N0} / {attention.Count:N0} lignes prioritaires affichées", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 7)));
        foreach (var row in attention.Take(previewCount)) panel.Children.Add(AttentionRow(row, full: false));
        var complete = DescribedCommand($"Voir les {attention.Count:N0} lignes prioritaires", "Ouvrir la liste complète des points d’attention avec recherche et détails.", () => OpenAttention(widget));
        AutomationProperties.SetAutomationId(complete, "Dashboard.Attention.Open"); complete.Margin = new Thickness(0, 9, 0, 0); panel.Children.Add(complete);
        if (widget.ShowDescription) panel.Children.Add(Details("Comprendre ce classement", Text("Échéance dépassée, priorité puis ancienneté. Cette sélection présente jusqu’à 50 lignes du périmètre. Cliquez sur une ligne pour la retrouver dans Extraction.", 11, "MutedBrush")));
        return panel;
    }

    private FrameworkElement AttentionRow(DashboardFocusRow row, bool full)
    {
        var content = new StackPanel(); var title = string.IsNullOrWhiteSpace(row.Title) ? "Titre non renseigné" : row.Title;
        content.Children.Add(Text((string.IsNullOrWhiteSpace(row.Id) ? "" : "#" + row.Id + " · ") + (full ? title : PreviewText(title, 150)), 12, weight: FontWeights.SemiBold));
        content.Children.Add(Text(string.Join(" · ", new[] { row.Status, row.Priority, full ? row.Group : "" }.Where(value => !string.IsNullOrWhiteSpace(value))), 11, "MutedBrush", margin: new Thickness(0, 4, 0, 0)));
        if (!string.IsNullOrWhiteSpace(row.Reasons)) content.Children.Add(Text(full ? row.Reasons : PreviewText(row.Reasons, 180), 11, "WarningBrush", margin: new Thickness(0, 4, 0, 0)));
        if (full && row.AgeDays is double age) content.Children.Add(Text($"Ancienneté : {age:N1} j", 11, "MutedBrush", margin: new Thickness(0, 4, 0, 0)));
        var button = Command(content, () => RowsRequested?.Invoke(new[] { row.Index }, "Point d’attention"));
        button.Padding = new Thickness(9); button.Margin = new Thickness(0, 0, 0, 6);
        Help(button, "Ouvrir la ligne " + row.Id, title + ". " + row.Reasons); return button;
    }

    private FrameworkElement QualityContent(DashboardWidgetSettings widget)
    {
        var data = _data!; var panel = new StackPanel(); var metrics = AdaptiveTiles(4, 170);
        foreach (var key in new[] { "unique", "duplicate", "missing-id", "unknown-status" })
        {
            var metric = data.Metric(key); if (metric is null) continue;
            var content = new StackPanel(); content.Children.Add(Text(metric.Label, 11, "MutedBrush")); content.Children.Add(Text(metric.Value?.ToString("N0") ?? "—", 23, weight: FontWeights.SemiBold));
            var button = Command(content, () => RowsRequested?.Invoke(metric.Rows, metric.Label), metric.Value.HasValue && (metric.Rows.Length > 0 || metric.Value == 0)); button.ToolTip = metric.Explanation; metrics.Children.Add(button);
        }
        panel.Children.Add(metrics);
        panel.Children.Add(Text($"{data.Columns.Count(x => x.Index.HasValue)} / {data.Columns.Count} rôles de colonnes reconnus · {data.Warnings.Count} avertissement(s)", 12, data.Warnings.Count > 0 ? "WarningBrush" : "MutedBrush", margin: new Thickness(0, 8, 0, 8)));
        if (data.Warnings.Count == 0) panel.Children.Add(Text("Aucune anomalie détectée par ces contrôles. Cela ne garantit pas l’exactitude métier du relevé.", 11, "MutedBrush"));
        else
        {
            foreach (var warning in data.Warnings.Take(2))
            {
                var summary = PreviewText(warning, 160);
                panel.Children.Add(Text("• " + summary, 11, "WarningBrush", margin: new Thickness(0, 0, 0, 7)));
            }
            var full = Command($"Lire les {data.Warnings.Count:N0} avertissement(s)", () => OpenWarnings(widget));
            AutomationProperties.SetAutomationId(full, "Dashboard.Quality.Warnings"); panel.Children.Add(full);
        }
        var probes = data.Metrics.Select(metric => new ProbeAvailability(metric.Label, "Indicateur",
                metric.Value is double value && double.IsFinite(value) ? "Disponible" : "Indisponible",
                metric.Sample?.ToString("N0", CultureInfo.CurrentCulture) ?? "—", metric.Explanation))
            .Concat(data.Distributions.Select(distribution => new ProbeAvailability(distribution.Title, "Répartition",
                distribution.Available ? "Disponible" : "Indisponible", distribution.Available ? distribution.Denominator.ToString("N0", CultureInfo.CurrentCulture) : "—", distribution.Note))).ToArray();
        var links = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        var columns = Command($"Colonnes utilisées · {data.Columns.Count:N0}", () => OpenColumns(widget)); AutomationProperties.SetAutomationId(columns, "Dashboard.Quality.Columns"); links.Children.Add(columns);
        var sources = Command($"Sondes disponibles · {probes.Count(probe => probe.State == "Disponible"):N0} / {probes.Length:N0}", () => OpenProbes(widget, probes)); AutomationProperties.SetAutomationId(sources, "Dashboard.Quality.Probes"); links.Children.Add(sources);
        links.Children.Add(Command("Régler les correspondances", () => SettingsRequested?.Invoke())); panel.Children.Add(links);
        if (widget.ShowDescription) panel.Children.Add(Details("Comprendre les contrôles de qualité", Text("Les occurrences d’un même identifiant ne sont pas fusionnées. Une colonne absente reste indisponible ; aucun zéro ne la remplace.", 11, "MutedBrush")));
        return panel;
    }

    private sealed record ProbeAvailability(string Label, string Kind, string State, string Sample, string Explanation);

    private FrameworkElement Missing(string text)
    {
        var panel = new StackPanel(); panel.Children.Add(Text(text, 12, "MutedBrush")); panel.Children.Add(Command("Vérifier les colonnes", () => SettingsRequested?.Invoke())); return panel;
    }

    private static UniformGrid AdaptiveTiles(int count, double minimumWidth)
    {
        var panel = new UniformGrid { Columns = 1 };
        panel.SizeChanged += (_, e) =>
        {
            var columns = Math.Clamp((int)Math.Floor(e.NewSize.Width / minimumWidth), 1, Math.Max(1, count));
            if (panel.Columns != columns) panel.Columns = columns;
        };
        return panel;
    }

    private static Grid ResponsivePair(FrameworkElement first, FrameworkElement second, double breakpoint, double firstWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(0) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(first); Grid.SetRow(second, 1); grid.Children.Add(second); bool wide = false;
        grid.SizeChanged += (_, e) =>
        {
            var desired = e.NewSize.Width >= breakpoint; if (wide == desired) return; wide = desired;
            grid.ColumnDefinitions[0].Width = wide ? new GridLength(firstWidth) : new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(second, wide ? 1 : 0); Grid.SetRow(second, wide ? 0 : 1);
            second.Margin = wide ? new Thickness(14, 0, 0, 0) : new Thickness(0);
        };
        return grid;
    }
}
