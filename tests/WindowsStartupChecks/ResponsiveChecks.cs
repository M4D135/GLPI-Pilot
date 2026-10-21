using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static async Task ExerciseResponsiveViewsAsync(TabControl tabs)
    {
        var dashboard = (DashboardView)((TabItem)tabs.Items[1]).Tag;
        var cases = new[]
        {
            (Width: 1440d, Height: 900d, Zoom: 1d, Theme: "light", Name: "large-100"),
            (Width: 960d, Height: 720d, Zoom: 1.5d, Theme: "light", Name: "small-150"),
            (Width: 1280d, Height: 850d, Zoom: 1.25d, Theme: "dark", Name: "medium-125")
        };
        foreach (var scenario in cases)
        {
            _stage = $"responsive views {scenario.Name} / {scenario.Theme}";
            _host!.Width = scenario.Width; _host.Height = scenario.Height;
            tabs.LayoutTransform = new ScaleTransform(scenario.Zoom, scenario.Zoom);
            ThemeService.Apply(scenario.Theme);
            SelectView(tabs, dashboard);
            dashboard.SetLayout(Layout("overview"));
            await FlushLayoutAsync();
            ScrollViewToTop(dashboard);
            await FlushLayoutAsync();
            Console.WriteLine($"VIEWPORT: requested={scenario.Width:0}x{scenario.Height:0}; actual window={_host.ActualWidth:0.#}x{_host.ActualHeight:0.#}; "
                + $"client={((FrameworkElement)_host.Content).ActualWidth:0.#}x{((FrameworkElement)_host.Content).ActualHeight:0.#}; "
                + $"logical test surface={tabs.ActualWidth:0.#}x{tabs.ActualHeight:0.#}; zoom={scenario.Zoom:P0}; theme={scenario.Theme}");
            AssertDashboardGeometry(dashboard);
            AssertVisibleInHost(FindButton(dashboard, "Personnaliser"), "dashboard customization");
            AssertVisibleInHost(FindButton(dashboard, "+ Ajouter un widget"), "dashboard widget catalog");
            CaptureView("dashboard-" + scenario.Name + "-" + scenario.Theme);
            Pass($"Dashboard cards fit their board without nested vertical bars at {scenario.Zoom:P0}, actual window {_host.ActualWidth:0.#}×{_host.ActualHeight:0.#}, {scenario.Theme}");

            _stage = $"personal dashboard {scenario.Name} / {scenario.Theme}";
            _technicians!.ShowTechnicianDashboard();
            SelectView(tabs, _technicians); await FlushLayoutAsync();
            ScrollViewToTop(_technicians.Dashboard); await FlushLayoutAsync();
            AssertDashboardGeometry(_technicians.Dashboard);
            Require(VerticalScrollAncestors(_technicians.Dashboard).Count() == 1,
                "The personal dashboard must have one vertical page viewport, without an outer workspace viewport.");
            CaptureView("personal-dashboard-" + scenario.Name + "-" + scenario.Theme);
            Pass("Personal dashboard has one page viewport at " + scenario.Name);

            if (scenario.Name == "small-150")
            {
                await ExerciseExpandedWeatherAsync(tabs, dashboard);
                await ExerciseCompleteDistributionAsync(tabs, dashboard);
                _stage = "technician comparison / small-150 / light";
                SelectView(tabs, _technicians); await FlushLayoutAsync();
                await SelectSectionAsync(_technicians, "Équipe");
                var technicianGrid = Descendants<DataGrid>(_technicians.Management).Single();
                AssertBoundedVirtualList(technicianGrid, "technician comparison");
                CaptureView("technicians-small-150-light");
                Pass("Technician comparison remains bounded and virtualized without a surrounding page scroller at 150 %");

                _stage = "tour steps / small-150 / light";
                SelectView(tabs, _tours!); await FlushLayoutAsync();
                await SelectSectionAsync(_tours!, "Plan de tournée");
                var tourGrid = Descendants<DataGrid>(_tours!).Single(grid => System.Windows.Automation.AutomationProperties.GetName(grid) == "Étapes de la tournée dans l’ordre de visite");
                AssertBoundedVirtualList(tourGrid, "tour steps");
                CaptureView("tour-small-150-light");
                Pass("Tour steps remain bounded and virtualized without a surrounding page scroller at 150 %");

                _stage = "query library / small-150 / light";
                SelectView(tabs, _queryEditor!); await FlushLayoutAsync();
                await SelectSectionAsync(_queryEditor!, "Bibliothèque");
                var library = Descendants<ListBox>(_queryEditor!).Single(list => System.Windows.Automation.AutomationProperties.GetName(list).StartsWith("Bibliothèque de requêtes", StringComparison.Ordinal));
                AssertBoundedVirtualList(library, "saved-query library");
                CaptureView("query-library-small-150-light");
                Pass("Query library remains bounded and virtualized without a surrounding page scroller at 150 %");
            }
        }
        tabs.LayoutTransform = Transform.Identity;
        _host!.Width = 1280; _host.Height = 850;
        ThemeService.Apply("dark");
        await FlushLayoutAsync();
    }

    private static async Task ExerciseExpandedWeatherAsync(TabControl tabs, DashboardView dashboard)
    {
        _stage = "expanded weather uses the main page scroll";
        SelectView(tabs, dashboard); await FlushLayoutAsync();
        var layout = dashboard.GetLayout();
        var weather = layout.Boards.Single(board => board.Id == layout.ActiveBoardId).Widgets.First(widget => widget.Kind == "weather");
        var board = Descendants<DashboardBoardPanel>(dashboard).Single();
        var card = board.Children.OfType<FrameworkElement>().Single(element => element.Tag as string == weather.Id);
        var details = Descendants<Expander>(card).SingleOrDefault(expander => AutomationProperties.GetAutomationId(expander) == "Dashboard.Weather.Details");
        Require(details != null && !details.IsExpanded, "Weather explanations should start collapsed.");
        var before = card.ActualHeight;
        details!.IsExpanded = true; await FlushLayoutAsync();
        Require(card.ActualHeight > before + 1, "Opening weather explanations did not enlarge the card to reveal its content.");
        AssertDashboardGeometry(dashboard);
        details.BringIntoView(); await FlushLayoutAsync();
        CaptureView("weather-expanded-small-150-light");
        details.IsExpanded = false; await FlushLayoutAsync();
        Require(card.ActualHeight <= before + 2, "Closing weather explanations did not restore compact card height.");
        Pass("Weather explanation expands the card and reflows the board without nested scrolling or overlaps");
    }

    private static async Task ExerciseCompleteDistributionAsync(TabControl tabs, DashboardView dashboard)
    {
        _stage = "complete dashboard list, paging, search and return";
        var originalLayout = dashboard.GetLayout();
        var sample = Sample();
        var categoryColumn = sample.Headers.IndexOf("Catégorie");
        var source = new ExtractionResult
        {
            Headers = sample.Headers.ToList(), Complete = true, ExpectedTotal = 43,
            SourceUrl = sample.SourceUrl, Scope = "Catégories synthétiques pour vérifier une liste de plusieurs pages",
            ExtractedAt = sample.ExtractedAt,
            Rows = Enumerable.Range(0, 43).Select(index =>
            {
                var row = sample.Rows[index % sample.Rows.Count].ToArray();
                row[0] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                row[categoryColumn] = $"Catégorie synthétique {index + 1:00}";
                return row;
            }).ToList()
        };
        var snapshot = DashboardService.Calculate(source, Settings, source.ExtractedAt);
        var distribution = snapshot.Distributions.Single(item => item.Key == "category");
        Require(distribution.Available && distribution.Buckets.Count == source.Rows.Count, "The synthetic category calculation did not retain each source category.");
        var layout = Layout("empty");
        layout.Boards.Single().Widgets.Add(new()
        {
            Kind = "distribution", SourceKey = distribution.Key, Title = "Catégories synthétiques", Span = 12, TopCount = 6
        });
        int[]? selectedRows = null;
        void OnRows(int[] rows, string _) => selectedRows = rows;
        dashboard.RowsRequested += OnRows;
        try
        {
            SelectView(tabs, dashboard);
            dashboard.SetLayout(layout); dashboard.Show(snapshot); await FlushLayoutAsync();
            ClickById(dashboard, "Dashboard.Distribution.Open"); await FlushLayoutAsync();
            Require(VerticalScrollAncestors(dashboard).Count() == 1, "The complete dashboard list lost its single page viewport.");
            Require(!Descendants<ScrollViewer>(dashboard).Any(scroll => scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableHeight > 1),
                "The complete dashboard list introduced another vertical scrollbar.");
            var target = distribution.Buckets.Last();
            Require(!HasBucket(dashboard, target), "The synthetic last category is already in the first page; the paging fixture is insufficient.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pages = 0;
            while (true)
            {
                foreach (var bucket in distribution.Buckets.Where(bucket => HasBucket(dashboard, bucket)))
                    Require(seen.Add(bucket.Label), "A category was repeated on two complete-list pages.");
                var next = FindById<Button>(dashboard, "Dashboard.Detail.Next.Top");
                pages++;
                if (!next.IsEnabled) break;
                Require(pages < distribution.Buckets.Count, "Complete-list pagination did not terminate.");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FlushLayoutAsync();
            }
            Require(seen.SetEquals(distribution.Buckets.Select(bucket => bucket.Label)), "Paging the complete list did not expose every category.");
            CaptureView("dashboard-complete-list-small-150-light");
            ClickById(dashboard, "Dashboard.Detail.Back"); await FlushLayoutAsync();
            Require(dashboard.GetLayout().Boards.Single().Widgets.Count == 1 && Descendants<DashboardBoardPanel>(dashboard).Single().Children.Count == 1,
                "Returning from a complete list changed or lost its source widget.");
            ClickById(dashboard, "Dashboard.Distribution.Open"); await FlushLayoutAsync();
            var search = FindById<TextBox>(dashboard, "Dashboard.Detail.Search");
            search.Text = target.Label; await FlushLayoutAsync();
            Require(distribution.Buckets.Count(bucket => HasBucket(dashboard, bucket)) == 1 && HasBucket(dashboard, target),
                "Complete-list search did not reach a category outside the initial page.");
            FindButton(dashboard, target.Label + $" : {target.Count:N0}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(selectedRows != null && selectedRows.SequenceEqual(target.Rows), "The complete-list category action lost its source row indexes.");
            ClickById(dashboard, "Dashboard.Detail.Back"); await FlushLayoutAsync();
            AssertDashboardGeometry(dashboard);
            Pass($"The complete dashboard list exposes all {seen.Count} synthetic categories across {pages} pages; cross-page search, source rows and return work");
        }
        finally
        {
            dashboard.RowsRequested -= OnRows;
            var settings = Settings.Copy(); settings.ColumnMappings["city"] = _cities!.CityColumnName;
            var original = DashboardService.Calculate(_cities.Data, settings, sample.ExtractedAt);
            CityDetectionService.AppendDashboardProbes(original, _cities);
            QueryService.AppendDashboardQueries(original, _cities.Data, settings, Queries);
            dashboard.SetLayout(originalLayout); dashboard.Show(original); await FlushLayoutAsync();
        }
    }

    private static bool HasBucket(DependencyObject root, DashboardBucket bucket)
        => Descendants<Button>(root).Any(button => AutomationProperties.GetName(button) == bucket.Label + $" : {bucket.Count:N0}");

    private static T FindById<T>(DependencyObject root, string automationId) where T : DependencyObject
        => Descendants<T>(root).Single(element => AutomationProperties.GetAutomationId(element) == automationId);

    private static void ClickById(DependencyObject root, string automationId)
    {
        var button = FindById<Button>(root, automationId);
        Require(button.IsEnabled, "Command is disabled: " + automationId);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void AssertDashboardGeometry(DashboardView dashboard)
    {
        var board = Descendants<DashboardBoardPanel>(dashboard).Single();
        Require(board.ActualWidth > 0 && double.IsFinite(board.ActualHeight), "Dashboard board has invalid rendered dimensions.");
        var cards = board.Children.OfType<FrameworkElement>().Where(card => card.Visibility == Visibility.Visible).ToArray();
        Require(cards.Length > 0, "Expected actual rendered dashboard cards.");
        var rectangles = cards.Select(card => card.TransformToAncestor(board).TransformBounds(new Rect(card.RenderSize))).ToArray();
        for (var index = 0; index < cards.Length; index++)
        {
            var bounds = rectangles[index];
            Require(bounds.Width > 0 && bounds.Height > 0 && double.IsFinite(bounds.Bottom)
                && bounds.Left >= -1 && bounds.Right <= board.ActualWidth + 1 && bounds.Bottom <= board.ActualHeight + 1,
                "A dashboard card is clipped by, or extends outside, its board.");
            Require(!Descendants<ScrollViewer>(cards[index]).Any(scroll => scroll.IsVisible
                && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableHeight > 1),
                "A dashboard card still exposes an inner vertical scrollbar.");
            for (var previous = 0; previous < index; previous++)
                Require(!bounds.IntersectsWith(rectangles[previous]), "Dashboard cards overlap after layout or expansion.");
        }
    }

    private static void AssertBoundedVirtualList(ItemsControl list, string label)
    {
        var usable = list.IsLoaded && list.ActualWidth > 0 && list.ActualHeight > 40 && double.IsFinite(list.ActualHeight);
        if (!usable) LogLayoutAncestors(list);
        Require(usable,
            $"The {label} has no usable bounded viewport: loaded={list.IsLoaded}, actual={list.ActualWidth:0.#}x{list.ActualHeight:0.#}, required height > 40.");
        Require(!VerticalScrollAncestors(list).Any(), "The " + label + " is nested inside a vertical page scroller.");
        Require(list is DataGrid grid ? grid.EnableRowVirtualization : VirtualizingPanel.GetIsVirtualizing(list),
            "The " + label + " lost virtualization.");
        AssertVisibleInHost(list, label);
        if (list is DataGrid populated && populated.Items.Count > 0)
        {
            var row = populated.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
            Require(row is { IsLoaded: true, ActualHeight: > 0 }, "The " + label + " did not realize its first data row.");
            DependencyObject? parent = row;
            while (parent != null && parent is not ScrollContentPresenter) parent = VisualTreeHelper.GetParent(parent);
            Require(parent is ScrollContentPresenter, "The " + label + " has no data-row viewport.");
            var viewport = (ScrollContentPresenter)parent!;
            var rowBounds = row!.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            if (rowBounds.Top < -1 || rowBounds.Bottom > viewport.ActualHeight + 1) LogLayoutAncestors(populated);
            Require(rowBounds.Top >= -1 && rowBounds.Bottom <= viewport.ActualHeight + 1,
                $"The {label} clips its first data row: top={rowBounds.Top:0.#}, bottom={rowBounds.Bottom:0.#}, data viewport height={viewport.ActualHeight:0.#}.");
        }
    }

    private static void LogLayoutAncestors(FrameworkElement element)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement part) continue;
            var rows = part is Grid grid ? "; rows=" + string.Join(",", grid.RowDefinitions.Select(row => $"{row.Height}:{row.ActualHeight:0.#}")) : "";
            var id = AutomationProperties.GetAutomationId(part);
            Console.Error.WriteLine($"LAYOUT: {part.GetType().Name}{(id.Length == 0 ? "" : " #" + id)}; "
                + $"actual={part.ActualWidth:0.#}x{part.ActualHeight:0.#}; desired={part.DesiredSize.Width:0.#}x{part.DesiredSize.Height:0.#}; "
                + $"margin={part.Margin}; visibility={part.Visibility}{rows}");
        }
    }

    private static void AssertVisibleInHost(FrameworkElement element, string label)
    {
        var surface = (FrameworkElement)_host!.Content;
        var bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
        var viewport = new Rect(surface.RenderSize); viewport.Inflate(2, 2);
        Require(element.IsVisible && bounds.Width > 0 && bounds.Height > 0 && viewport.Contains(bounds),
            "The " + label + " is clipped or outside the current host viewport.");
    }

    private static IEnumerable<ScrollViewer> VerticalScrollAncestors(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll && scroll.VerticalScrollBarVisibility is not (ScrollBarVisibility.Disabled or ScrollBarVisibility.Hidden))
                yield return scroll;
    }

    private static void ScrollViewToTop(DependencyObject view)
    {
        foreach (var scroll in VerticalScrollAncestors(view)) scroll.ScrollToTop();
    }

    private static async Task SelectSectionAsync(DependencyObject root, string header)
    {
        var tabs = Descendants<TabControl>(root).Cast<DependencyObject>().Prepend(root).OfType<TabControl>();
        var match = tabs.SelectMany(control => control.Items.OfType<TabItem>().Select(tab => (Control: control, Tab: tab)))
            .Single(item => item.Tab.Header as string == header);
        match.Control.SelectedItem = match.Tab;
        await FlushLayoutAsync();
    }
}
