using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class DashboardView
{
    private FrameworkElement MapContent(DashboardWidgetSettings widget)
    {
        var activeOnly = widget.SourceKey == "active";
        if (activeOnly && _data!.Metric("active")?.Value.HasValue != true) return Missing("La carte des actifs demande des statuts exploitables.");
        var points = _data!.Geography.Where(p => (activeOnly ? p.ActiveRows : p.Rows).Length > 0).ToArray();
        if (points.Length == 0) return Missing("Aucune coordonnée exploitable dans ce périmètre. Renseignez les coordonnées des villes ou sites, puis leur correspondance avec les tickets.");
        var panel = new StackPanel(); var controls = new WrapPanel(); var canvas = new Canvas { Width = 720, Height = 380, ClipToBounds = true };
        canvas.SetResourceReference(Panel.BackgroundProperty, "FieldBrush");
        var zoom = new ScaleTransform(1, 1); canvas.LayoutTransform = zoom;
        var viewport = new ScrollViewer { Content = canvas, Height = 390, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Scale(double factor) { zoom.ScaleX = zoom.ScaleY = Math.Clamp(zoom.ScaleX * factor, .6, 8); }
        controls.Children.Add(Command("−", () => Scale(1 / 1.4))); controls.Children.Add(Command("+", () => Scale(1.4)));
        controls.Children.Add(Command("Recentrer", () => { zoom.ScaleX = zoom.ScaleY = 1; viewport.ScrollToHorizontalOffset(0); viewport.ScrollToVerticalOffset(0); }));
        var linked = new CheckBox { Content = "Filtrer les autres widgets au clic", IsChecked = Board.CrossFilterOnClick, Margin = new Thickness(8, 8, 0, 0) };
        linked.Checked += (_, _) => Change(() => Board.CrossFilterOnClick = true); linked.Unchecked += (_, _) => Change(() => Board.CrossFilterOnClick = false); controls.Children.Add(linked);
        panel.Children.Add(controls);
        var latMin = points.Min(p => p.Latitude); var latMax = points.Max(p => p.Latitude); var lonMin = points.Min(p => p.Longitude); var lonMax = points.Max(p => p.Longitude);
        var latSpan = Math.Max(.02, latMax - latMin); var lonSpan = Math.Max(.02, lonMax - lonMin);
        latMin -= latSpan * .12; latMax += latSpan * .12; lonMin -= lonSpan * .12; lonMax += lonSpan * .12;
        if (latMax - latMin < .02) { latMin -= .01; latMax += .01; } if (lonMax - lonMin < .02) { lonMin -= .01; lonMax += .01; }
        double X(double lon) => 60 + 610 * (lon - lonMin) / (lonMax - lonMin);
        double Y(double lat) => 325 - 285 * (lat - latMin) / (latMax - latMin);
        for (var i = 0; i <= 5; i++)
        {
            var lat = latMin + (latMax - latMin) * i / 5; var lon = lonMin + (lonMax - lonMin) * i / 5;
            var horizontal = new Line { X1 = 60, X2 = 670, Y1 = Y(lat), Y2 = Y(lat), StrokeThickness = .5 }; horizontal.SetResourceReference(Shape.StrokeProperty, "LineBrush"); canvas.Children.Add(horizontal);
            var vertical = new Line { X1 = X(lon), X2 = X(lon), Y1 = 40, Y2 = 325, StrokeThickness = .5 }; vertical.SetResourceReference(Shape.StrokeProperty, "LineBrush"); canvas.Children.Add(vertical);
            var latitude = Text(lat.ToString("0.###", CultureInfo.InvariantCulture) + "°", 10, "MutedBrush"); Canvas.SetLeft(latitude, 3); Canvas.SetTop(latitude, Y(lat) - 6); canvas.Children.Add(latitude);
            var longitude = Text(lon.ToString("0.###", CultureInfo.InvariantCulture) + "°", 10, "MutedBrush"); Canvas.SetLeft(longitude, X(lon) - 20); Canvas.SetTop(longitude, 335); canvas.Children.Add(longitude);
        }
        var maximum = points.Max(p => (activeOnly ? p.ActiveRows : p.Rows).Length);
        // Draw larger markers first, so a smaller co-located site remains reachable in the list below.
        foreach (var point in points.OrderByDescending(p => (activeOnly ? p.ActiveRows : p.Rows).Length))
        {
            var rows = activeOnly ? point.ActiveRows : point.Rows; var diameter = 12 + 30 * Math.Sqrt((double)rows.Length / maximum);
            var marker = new Ellipse { Width = diameter, Height = diameter, Fill = Accent(widget.Accent), Opacity = .75, Stroke = Brushes.White, StrokeThickness = 1.5 };
            var button = Command(marker, () => ActivateRows(rows, point.Label, "geography-points-" + (activeOnly ? "active" : "all"), point.Label));
            button.Padding = new Thickness(0); button.Margin = new Thickness(0); button.MinHeight = button.MinWidth = 0; button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
            Help(button, point.Label, $"{rows.Length:N0} lignes · {point.Latitude:0.#####}°, {point.Longitude:0.#####}° · coordonnées du référentiel local");
            Canvas.SetLeft(button, X(point.Longitude) - diameter / 2); Canvas.SetTop(button, Y(point.Latitude) - diameter / 2); canvas.Children.Add(button);
        }
        panel.Children.Add(viewport);
        var covered = points.SelectMany(p => activeOnly ? p.ActiveRows : p.Rows).Distinct().Count(); var total = activeOnly ? _data.Metric("active")!.Rows.Length : _data.RowCount;
        panel.Children.Add(Text($"Plan géographique hors ligne · {covered:N0}/{total:N0} lignes localisées · {points.Length:N0} points. Projection longitude/latitude ; pas de fond routier. Les coordonnées de ville représentent la ville, jamais une adresse de ticket. Aucune donnée transmise.", 10, "MutedBrush"));
        panel.Children.Add(Command("Toutes les localisations", () => OpenDetails(widget, "Localisations et coordonnées", body => PagedDetails(body, points, p => p.Label,
            p => Command(p.Label + " · " + (activeOnly ? p.ActiveRows.Length : p.Rows.Length) + " lignes", () => ActivateRows(activeOnly ? p.ActiveRows : p.Rows, p.Label, "geography-points-" + (activeOnly ? "active" : "all"), p.Label)), "points"))));
        return panel;
    }
}
