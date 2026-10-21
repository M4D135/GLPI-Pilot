using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private FrameworkElement WidgetContent(DashboardWidgetSettings widget)
    {
        if (widget.Kind == "note")
            return Scroll(Text(string.IsNullOrWhiteSpace(widget.Note) ? "Ajoutez votre consigne, votre contexte ou vos repères dans les options de ce widget." : widget.Note, 13, string.IsNullOrWhiteSpace(widget.Note) ? "MutedBrush" : "TextBrush"));
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
            return Scroll(empty);
        }
        return widget.Kind switch
        {
            "metric" => MetricContent(widget), "distribution" => DistributionContent(widget),
            "weather" => WeatherContent(widget), "timeline" => TimelineContent(widget),
            "attention" => AttentionContent(widget), "quality" => QualityContent(widget),
            _ => Scroll(Text("Type de widget non disponible.", 13, "MutedBrush"))
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
        var value = Text(number + suffix, widget.Presentation == "compact" ? 30 : 38, weight: FontWeights.SemiBold);
        if (metric.Value.HasValue && metric.Value > 0 && metric.Tone is "warning" or "error") value.SetResourceReference(TextBlock.ForegroundProperty, metric.Tone == "error" ? "ErrorBrush" : "WarningBrush");
        else value.Foreground = Accent(widget.Accent);
        panel.Children.Add(value);
        panel.Children.Add(Text(metric.Value.HasValue ? metric.Sample.HasValue ? $"Échantillon : {metric.Sample:N0}" : metric.Unit : "Données indisponibles", 11, "MutedBrush", margin: new Thickness(0, 5, 0, 0)));
        if (widget.ShowDescription && widget.Presentation != "compact") panel.Children.Add(Text(metric.Explanation, 11, "MutedBrush", margin: new Thickness(0, 9, 0, 0)));
        var action = Command(panel, () => RowsRequested?.Invoke(metric.Rows, WidgetTitle(widget)), metric.Value.HasValue && (metric.Rows.Length > 0 || metric.Value == 0));
        action.Background = Brushes.Transparent; action.BorderThickness = new Thickness(0); action.Padding = new Thickness(0); action.Margin = new Thickness(0); action.HorizontalContentAlignment = HorizontalAlignment.Stretch; action.VerticalContentAlignment = VerticalAlignment.Top; action.ToolTip = metric.Explanation;
        return Scroll(action);
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
            if (widget.ShowDescription) panel.Children.Add(Text(metric.Explanation, 11, "MutedBrush"));
            return Scroll(panel);
        }
        if (widget.TargetValue is not double target || !double.IsFinite(target) || target <= 0)
        {
            panel.Children.Add(Text("Renseigner un objectif", 14, "MutedBrush", FontWeights.SemiBold, new Thickness(0, 16, 0, 6)));
            panel.Children.Add(Text("Choisissez un nombre strictement positif, dans l’unité de cet indicateur, pour activer la jauge.", 11, "MutedBrush"));
            panel.Children.Add(Command("Régler l’objectif", () => OpenEditor("widget", widget.Id)));
            if (widget.ShowDescription) panel.Children.Add(Text(metric.Explanation, 11, "MutedBrush"));
            return Scroll(panel);
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
            panel.Children.Add(Text(metric.Explanation, 11, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
            panel.Children.Add(Text("Objectif et seuils de présentation choisis pour ce widget ; ils ne définissent pas un engagement SLA.", 10, "MutedBrush", margin: new Thickness(0, 6, 0, 0)));
        }
        return Scroll(panel);
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
        if (dist.Buckets.Count == 0) return Scroll(Text("Aucune ligne dans ce périmètre.", 13, "MutedBrush"));
        if (widget.Presentation == "donut") return DonutContent(widget, dist);
        if (widget.Presentation == "list")
        {
            var list = new Grid(); list.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); list.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var table = ReadOnlyGrid();
            table.Columns.Add(new DataGridTextColumn { Header = "Valeur", Binding = new Binding(nameof(DashboardBucket.Label)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120 });
            table.Columns.Add(new DataGridTextColumn { Header = "Nombre", Binding = new Binding(nameof(DashboardBucket.Count)) { StringFormat = "N0" }, Width = 75 }); table.ItemsSource = dist.Buckets;
            table.MouseDoubleClick += (_, e) => { if (!_busy && FindRow(e.OriginalSource as DependencyObject) != null && table.SelectedItem is DashboardBucket b) RowsRequested?.Invoke(b.Rows, dist.Title + " · " + b.Label); };
            list.Children.Add(table); var footer = new StackPanel();
            footer.Children.Add(Command("Voir les lignes sélectionnées", () => { if (table.SelectedItem is DashboardBucket b) RowsRequested?.Invoke(b.Rows, dist.Title + " · " + b.Label); }));
            if (widget.ShowDescription) footer.Children.Add(Text(dist.Note, 11, "MutedBrush")); Grid.SetRow(footer, 1); list.Children.Add(footer); return list;
        }
        var panel = new StackPanel(); var maximum = Math.Max(1, dist.Buckets.Max(x => x.Count));
        foreach (var bucket in dist.Buckets.Take(widget.TopCount))
        {
            var row = new StackPanel();
            var heading = new DockPanel();
            var count = Text($"{bucket.Count:N0} · {100d * bucket.Count / Math.Max(1, dist.Denominator):0.#} %", 11, "MutedBrush", margin: new Thickness(8, 0, 0, 0)); DockPanel.SetDock(count, Dock.Right); heading.Children.Add(count);
            var label = Text(bucket.Label, 12); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; heading.Children.Add(label); row.Children.Add(heading);
            if (widget.Presentation != "compact")
            {
                var progress = new ProgressBar { Maximum = maximum, Value = bucket.Count, Height = 5, Margin = new Thickness(0, 7, 0, 0), Foreground = Accent(widget.Accent) }; row.Children.Add(progress);
            }
            var button = Command(row, () => RowsRequested?.Invoke(bucket.Rows, dist.Title + " · " + bucket.Label));
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(0, 5, 0, 7); button.Margin = new Thickness(0, 0, 0, 3); button.ToolTip = bucket.Label + $" · {bucket.Count:N0} lignes"; panel.Children.Add(button);
        }
        if (dist.Buckets.Count > widget.TopCount)
        {
            var rest = new StackPanel();
            var table = ReadOnlyGrid(); table.Height = 260;
            table.Columns.Add(new DataGridTextColumn { Header = "Valeur", Binding = new Binding(nameof(DashboardBucket.Label)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120 });
            table.Columns.Add(new DataGridTextColumn { Header = "Nombre", Binding = new Binding(nameof(DashboardBucket.Count)), Width = 75 });
            table.ItemsSource = dist.Buckets;
            table.MouseDoubleClick += (_, e) => { if (!_busy && FindRow(e.OriginalSource as DependencyObject) != null && table.SelectedItem is DashboardBucket b) RowsRequested?.Invoke(b.Rows, dist.Title + " · " + b.Label); };
            rest.Children.Add(table);
            rest.Children.Add(Command("Voir les lignes sélectionnées", () => { if (table.SelectedItem is DashboardBucket b) RowsRequested?.Invoke(b.Rows, dist.Title + " · " + b.Label); }));
            panel.Children.Add(new Expander { Header = $"Consulter les {dist.Buckets.Count:N0} valeurs", Content = rest, Margin = new Thickness(0, 7, 0, 0) });
        }
        if (widget.ShowDescription) panel.Children.Add(Text(dist.Note + $" Base : {dist.Denominator:N0} lignes.", 11, "MutedBrush", margin: new Thickness(0, 12, 0, 0)));
        return Scroll(panel);
    }

    private sealed record DonutPart(string Label, long Count, Func<int[]> Rows, bool Others = false);

    private FrameworkElement DonutContent(DashboardWidgetSettings widget, DashboardDistribution distribution)
    {
        var panel = new StackPanel(); var total = distribution.Buckets.Sum(bucket => (long)Math.Max(0, bucket.Count));
        if (total == 0) return Scroll(Text("Aucune occurrence positive à représenter. Les nombres restent consultables en présentation Tableau.", 12, "MutedBrush"));
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
        AutomationProperties.SetName(graphic, distribution.Title + $". {total:N0} occurrences, {distribution.Buckets.Count:N0} catégories. La légende donne tous les nombres et l’accès aux lignes."); panel.Children.Add(graphic);
        panel.Children.Add(Text($"Base : {distribution.Denominator:N0} lignes · {distribution.Buckets.Count:N0} catégories", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 9)));
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index]; var content = new Grid();
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Accent(part.Others ? "slate" : palette[index % palette.Length]), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 8, 0) }; content.Children.Add(swatch);
            var label = Text(part.Label, 12); Grid.SetColumn(label, 1); content.Children.Add(label);
            var numbers = Text($"{part.Count:N0} · {100d * part.Count / total:0.#} %", 11, "MutedBrush", margin: new Thickness(8, 0, 0, 0)); Grid.SetColumn(numbers, 2); content.Children.Add(numbers);
            var button = Command(content, () => RowsRequested?.Invoke(part.Rows(), distribution.Title + " · " + part.Label));
            button.Padding = new Thickness(5, 7, 5, 7); button.Margin = new Thickness(0, 0, 0, 2); button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
            Help(button, part.Label + $" : {part.Count:N0} occurrences", part.Others
                ? $"{part.Count:N0} occurrences regroupées. Afficher l’union des lignes des catégories Autres ; une ligne commune n’est ouverte qu’une fois. Le tableau complet détaille les catégories."
                : part.Label + $" · {part.Count:N0} occurrences · {100d * part.Count / total:0.#} % des occurrences. Afficher les lignes correspondantes.");
            panel.Children.Add(button);
        }
        panel.Children.Add(Text("Les parts comparent les occurrences des catégories, pas un pourcentage de tickets distincts.", 10, "MutedBrush", margin: new Thickness(0, 9, 0, 0)));
        if (distribution.Buckets.Count > widget.TopCount)
        {
            var complete = new StackPanel(); var table = ReadOnlyGrid(); table.Height = 250;
            table.Columns.Add(new DataGridTextColumn { Header = "Catégorie", Binding = new Binding(nameof(DashboardBucket.Label)) { Mode = BindingMode.OneWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 130 });
            table.Columns.Add(new DataGridTextColumn { Header = "Nombre", Binding = new Binding(nameof(DashboardBucket.Count)) { StringFormat = "N0", Mode = BindingMode.OneWay }, Width = 85 }); table.ItemsSource = distribution.Buckets;
            table.MouseDoubleClick += (_, e) => { if (!_busy && FindRow(e.OriginalSource as DependencyObject) != null && table.SelectedItem is DashboardBucket bucket) RowsRequested?.Invoke(bucket.Rows, distribution.Title + " · " + bucket.Label); };
            complete.Children.Add(table); complete.Children.Add(Command("Voir les lignes sélectionnées", () => { if (table.SelectedItem is DashboardBucket bucket) RowsRequested?.Invoke(bucket.Rows, distribution.Title + " · " + bucket.Label); }));
            panel.Children.Add(new Expander { Header = $"Détailler les {distribution.Buckets.Count:N0} catégories", Content = complete, Margin = new Thickness(0, 10, 0, 0) });
        }
        if (widget.ShowDescription) panel.Children.Add(Text(distribution.Note, 11, "MutedBrush", margin: new Thickness(0, 10, 0, 0)));
        return Scroll(panel);
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
        var weather = _data!.Weather; var tone = weather.State switch { "sun" => "SuccessBrush", "storm" => "ErrorBrush", "unknown" or "partial" => "MutedBrush", _ => "WarningBrush" };
        var panel = new StackPanel(); var heading = new DockPanel();
        var icon = Text(weather.Symbol, 45, tone, margin: new Thickness(0, 0, 18, 0)); icon.FontFamily = new FontFamily("Segoe UI Symbol"); DockPanel.SetDock(icon, Dock.Left); heading.Children.Add(icon);
        var title = new StackPanel(); title.Children.Add(Text(weather.Title, widget.Presentation == "compact" ? 20 : 23, tone, FontWeights.SemiBold));
        if (widget.ShowDescription) title.Children.Add(Text(weather.Description, 12, "MutedBrush", margin: new Thickness(0, 6, 0, 0)));
        heading.Children.Add(title); panel.Children.Add(heading);
        if (weather.Score.HasValue)
        {
            var score = Text($"Indice {weather.Score:0.#}/100 · poids disponibles {weather.MeasuredWeight}/100", 11, "MutedBrush", margin: new Thickness(0, 10, 0, 0)); panel.Children.Add(score);
            var progress = new ProgressBar { Maximum = 100, Value = weather.Score.Value, Height = 5, Margin = new Thickness(0, 8, 0, 10) }; progress.SetResourceReference(Control.ForegroundProperty, tone); panel.Children.Add(progress);
        }
        if (widget.Presentation != "compact")
        {
            var signals = new WrapPanel();
            foreach (var signal in weather.Signals)
            {
                var signalPanel = new StackPanel { Width = 194 };
                signalPanel.Children.Add(Text(signal.Label, 11, "MutedBrush"));
                signalPanel.Children.Add(Text($"{signal.Count:N0} / {signal.Sample:N0} · {100d * signal.Count / Math.Max(1, signal.Sample):0.#} %", 18, weight: FontWeights.SemiBold, margin: new Thickness(0, 5, 0, 4)));
                signalPanel.Children.Add(Text($"Champ exploitable : {signal.Sample:N0} / {signal.Population:N0} actifs", 10, "MutedBrush"));
                var button = Command(signalPanel, () => RowsRequested?.Invoke(signal.Rows, signal.Label)); button.ToolTip = signal.Explanation; signals.Children.Add(button);
            }
            panel.Children.Add(signals);
        }
        if (widget.ShowDescription)
            panel.Children.Add(new Expander { Header = "Comment est calculée cette météo ?", Content = Text("Météo indicative du relevé : échéances 35 %, priorités hautes 25 %, affectation 15 %, dernière modification 15 %, âge 10 %. Les poids disponibles sont renormalisés, avec un dénominateur propre à chaque signal. Soleil < 10 ; vigilance de 10 à < 25 ; pluie de 25 à < 45 ; orage ≥ 45. Les données insuffisantes restent indiquées comme partielles.", 11, "MutedBrush"), Margin = new Thickness(0, 6, 0, 0) });
        return Scroll(panel);
    }

    private FrameworkElement TimelineContent(DashboardWidgetSettings widget)
    {
        var data = _data!; if (data.Timeline.Count == 0) return Scroll(Text("Aucune date disponible.", 13, "MutedBrush"));
        var panel = new StackPanel();
        var keys = new[] { ("created", "Créations", "ActionBrush"), ("resolved", "Résolutions", "SuccessBrush"), ("closed", "Clôtures", "MutedBrush") };
        var legend = new WrapPanel(); foreach (var (key, label, brush) in keys) legend.Children.Add(Text(label + (data.Column(key)?.Index.HasValue == true ? "" : " · indisponible"), 11, brush, margin: new Thickness(0, 0, 18, 10))); panel.Children.Add(legend);
        var maximum = Math.Max(1, data.Timeline.SelectMany(x => new[] { x.Created.Length, x.Resolved.Length, x.Closed.Length }).Max());
        var chart = new UniformGrid { Columns = data.Timeline.Count, Rows = 1, MinWidth = 640, Height = 165 };
        foreach (var day in data.Timeline)
        {
            var column = new StackPanel(); var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 130, HorizontalAlignment = HorizontalAlignment.Center };
            var series = new[] { day.Created, day.Resolved, day.Closed };
            for (var i = 0; i < 3; i++)
            {
                var (key, label, brush) = keys[i]; var rows = series[i]; var available = data.Column(key)?.Index.HasValue == true;
                var bar = new Border { Height = Math.Max(rows.Length > 0 ? 3 : 1, 125d * rows.Length / maximum), Width = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom }; bar.SetResourceReference(Border.BackgroundProperty, brush); if (!available) bar.Opacity = .2;
                var button = Command(bar, () => RowsRequested?.Invoke(rows, label + " · " + day.Date.ToString("dd/MM/yyyy")), available); button.Padding = new Thickness(0); button.Margin = new Thickness(1, 0, 1, 0); button.MinHeight = 0; button.BorderThickness = new Thickness(0); button.Background = Brushes.Transparent; button.VerticalAlignment = VerticalAlignment.Bottom; button.ToolTip = $"{day.Date:dd/MM/yyyy} · {label} : " + (available ? rows.Length.ToString("N0") : "indisponible"); bars.Children.Add(button);
            }
            column.Children.Add(bars); column.Children.Add(Text(day.Date.Day.ToString("00"), 10, "MutedBrush", margin: new Thickness(0, 8, 0, 0))); chart.Children.Add(column);
        }
        panel.Children.Add(new ScrollViewer { Content = chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, FocusVisualStyle = null });
        if (widget.ShowDescription) panel.Children.Add(Text($"Du {data.Timeline[0].Date:dd/MM/yyyy} au {data.Timeline[^1].Date:dd/MM/yyyy}. Événements des tickets présents dans ce relevé. Une recherche limitée aux tickets ouverts ne décrit pas le flux global des clôtures.", 11, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        return Scroll(panel);
    }

    private FrameworkElement AttentionContent(DashboardWidgetSettings widget)
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        if (widget.ShowDescription) panel.Children.Add(Text("Échéance dépassée, priorité puis ancienneté. Jusqu’à 50 lignes du périmètre ; double-cliquez pour ouvrir la ligne dans Extraction.", 11, "MutedBrush", margin: new Thickness(0, 0, 0, 8)));
        var table = ReadOnlyGrid();
        table.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(DashboardFocusRow.Id)), Width = 85 });
        foreach (var (property, label, weight, minimum) in new[] { ("Title", "Titre", 3d, 190d), ("Status", "Statut", 1.3, 110d), ("Priority", "Priorité", 1d, 90d), ("Group", "Groupe", 1.4, 120d), ("Reasons", "Points d’attention", 2.5, 170d) })
            table.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = minimum });
        table.ItemsSource = _data!.Attention; table.MouseDoubleClick += (_, e) => { if (!_busy && table.SelectedItem is DashboardFocusRow row && FindRow(e.OriginalSource as DependencyObject) != null) RowsRequested?.Invoke(new[] { row.Index }, "Point d’attention"); }; Grid.SetRow(table, 1); panel.Children.Add(table);
        var footer = Command("Voir le ticket sélectionné", () => { if (table.SelectedItem is DashboardFocusRow row) RowsRequested?.Invoke(new[] { row.Index }, "Point d’attention"); }, _data.Attention.Count > 0); Grid.SetRow(footer, 2); footer.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(footer); return panel;
    }
    private static DataGridRow? FindRow(DependencyObject? current)
    {
        while (current is not null && current is not DataGridRow) current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        return current as DataGridRow;
    }

    private FrameworkElement QualityContent(DashboardWidgetSettings widget)
    {
        var data = _data!; var panel = new StackPanel(); var metrics = new WrapPanel();
        foreach (var key in new[] { "unique", "duplicate", "missing-id", "unknown-status" })
        {
            var metric = data.Metric(key); if (metric is null) continue;
            var content = new StackPanel { Width = 170 }; content.Children.Add(Text(metric.Label, 11, "MutedBrush")); content.Children.Add(Text(metric.Value?.ToString("N0") ?? "—", 23, weight: FontWeights.SemiBold));
            var button = Command(content, () => RowsRequested?.Invoke(metric.Rows, metric.Label), metric.Value.HasValue && (metric.Rows.Length > 0 || metric.Value == 0)); button.ToolTip = metric.Explanation; metrics.Children.Add(button);
        }
        panel.Children.Add(metrics);
        panel.Children.Add(Text($"{data.Columns.Count(x => x.Index.HasValue)} / {data.Columns.Count} rôles de colonnes reconnus · {data.Warnings.Count} avertissement(s)", 12, data.Warnings.Count > 0 ? "WarningBrush" : "MutedBrush", margin: new Thickness(0, 8, 0, 8)));
        if (data.Warnings.Count == 0) panel.Children.Add(Text("Aucune anomalie détectée par ces contrôles. Cela ne garantit pas l’exactitude métier du relevé.", 11, "MutedBrush"));
        else
        {
            foreach (var warning in data.Warnings.Take(3))
            {
                var summary = warning.Length > 220 ? warning[..220].TrimEnd() + "…" : warning;
                panel.Children.Add(Text("• " + summary, 11, "WarningBrush", margin: new Thickness(0, 0, 0, 7)));
            }
            var full = new TextBox { Text = string.Join("\n\n", data.Warnings), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new Expander { Header = $"Consulter les {data.Warnings.Count} avertissement(s) complets", Content = full, Margin = new Thickness(0, 7, 0, 7) });
        }
        var table = ReadOnlyGrid(); table.Height = 270;
        table.Columns.Add(new DataGridTextColumn { Header = "Rôle", Binding = new Binding(nameof(DashboardColumn.Label)), Width = 170 });
        table.Columns.Add(new DataGridTextColumn { Header = "Colonne du relevé", Binding = new Binding(nameof(DashboardColumn.Header)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 140 });
        table.Columns.Add(new DataGridTextColumn { Header = "Détection", Binding = new Binding(nameof(DashboardColumn.State)), Width = 120 }); table.ItemsSource = data.Columns;
        panel.Children.Add(new Expander { Header = "Colonnes utilisées pour les calculs", Content = table, Margin = new Thickness(0, 7, 0, 7) });
        var probes = data.Metrics.Select(metric => new ProbeAvailability(metric.Label, "Indicateur",
                metric.Value is double value && double.IsFinite(value) ? "Disponible" : "Indisponible",
                metric.Sample?.ToString("N0", CultureInfo.CurrentCulture) ?? "—", metric.Explanation))
            .Concat(data.Distributions.Select(distribution => new ProbeAvailability(distribution.Title, "Répartition",
                distribution.Available ? "Disponible" : "Indisponible", distribution.Denominator.ToString("N0", CultureInfo.CurrentCulture), distribution.Note))).ToArray();
        var probesTable = ReadOnlyGrid(); probesTable.Height = 290;
        probesTable.Columns.Add(new DataGridTextColumn { Header = "Sonde", Binding = new Binding(nameof(ProbeAvailability.Label)) { Mode = BindingMode.OneWay }, Width = 240 });
        probesTable.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(ProbeAvailability.Kind)) { Mode = BindingMode.OneWay }, Width = 95 });
        probesTable.Columns.Add(new DataGridTextColumn { Header = "Disponibilité", Binding = new Binding(nameof(ProbeAvailability.State)) { Mode = BindingMode.OneWay }, Width = 110 });
        probesTable.Columns.Add(new DataGridTextColumn { Header = "Échantillon / base", Binding = new Binding(nameof(ProbeAvailability.Sample)) { Mode = BindingMode.OneWay }, Width = 120 });
        probesTable.Columns.Add(new DataGridTextColumn { Header = "Calcul et conditions", Binding = new Binding(nameof(ProbeAvailability.Explanation)) { Mode = BindingMode.OneWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 360 });
        probesTable.ItemsSource = probes;
        panel.Children.Add(new Expander { Header = $"Sondes du relevé · {probes.Count(probe => probe.State == "Disponible")} / {probes.Length} disponibles", Content = probesTable, Margin = new Thickness(0, 7, 0, 7) });
        panel.Children.Add(Command("Régler les correspondances", () => SettingsRequested?.Invoke()));
        if (widget.ShowDescription) panel.Children.Add(Text("Les occurrences d’un même identifiant ne sont pas fusionnées. Une colonne absente reste indisponible ; aucun zéro ne la remplace.", 11, "MutedBrush"));
        return Scroll(panel);
    }

    private sealed record ProbeAvailability(string Label, string Kind, string State, string Sample, string Explanation);

    private FrameworkElement Missing(string text)
    {
        var panel = new StackPanel(); panel.Children.Add(Text(text, 12, "MutedBrush")); panel.Children.Add(Command("Vérifier les colonnes", () => SettingsRequested?.Invoke())); return Scroll(panel);
    }
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, FocusVisualStyle = null, Padding = new Thickness(0, 0, 4, 0) };
}
