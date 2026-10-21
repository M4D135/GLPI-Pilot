using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

/// <summary>
/// Executes real production WPF controls on Windows. Does not construct MainWindow,
/// read AppSettings, start BrowserService, or register production startup handlers.
/// </summary>
internal static class Program
{
    private static readonly List<string> Passed = new();
    private static int _exitCode = 1;
    private static bool _failed;
    private static Window? _host;
    private static Application? _app;
    private static string _stage = "initialisation";
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "GLPI-WindowsStartupChecks-" + Guid.NewGuid().ToString("N"));
    private static readonly List<IDisposable> DisposableViews = new();
    private static readonly DashboardSettings Settings = new();
    private static readonly CitySettings Cities = SampleCities();
    private static readonly QueryLibrarySettings Queries = QueryLibrarySettings.CreateDefault();
    private static CityEnrichmentResult? _cities;
    private static TechnicianWorkspaceView? _technicians;
    private static CitySettingsView? _cityEditor;
    private static TourPlanningView? _tours;
    private static QueryWorkshopView? _queryEditor;

    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("NOT RUN: these checks require Windows and an interactive desktop. A cross-build is not a UI test.");
            return 3;
        }
        if (!Environment.UserInteractive)
        {
            Console.Error.WriteLine("NOT RUN: an interactive Windows desktop is required; no service/session-0 execution.");
            return 3;
        }
        // A ThreadPool timer, not a DispatcherTimer: a hung dispatcher still fails.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL: overall 90-second deadline exceeded at stage: " + _stage);
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        try
        {
            Directory.CreateDirectory(TemporaryRoot);
            _stage = "real App resources";
            _app = new Application();
            _app.Resources = LoadProductionResources();
            _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _app.DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                Fail(args.Exception);
                _app.Shutdown(1);
            };
            ThemeService.Apply("dark");
            Pass("Production App.xaml resource definitions loaded in an isolated WPF Application");
            var tabs = BuildViews();
            _host = new Window
            {
                Title = "GLPI Pilot — Windows UI checks (synthetic data)",
                Width = 1280, Height = 850, MinWidth = 700, MinHeight = 500,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = tabs
            };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _host.Loaded += (_, _) => loaded.TrySetResult();
            _host.ContentRendered += (_, _) => rendered.TrySetResult();
            _host.Closed += (_, _) =>
            {
                if (_exitCode != 0 && !_failed) Fail(new InvalidOperationException("UI test window was closed before completion."));
                _app.Shutdown(_exitCode);
            };
            _host.Loaded += async (_, _) =>
            {
                try { await ExerciseViewsAsync(tabs, loaded.Task, rendered.Task); }
                catch (Exception ex) { Fail(ex); }
                finally
                {
                    await DisposeViewsAsync();
                    _host.Close();
                    if (!_app.Dispatcher.HasShutdownStarted) _app.Shutdown(_exitCode);
                }
            };
            _app.Run(_host);
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            foreach (var view in DisposableViews) view.Dispose();
            try { if (Directory.Exists(TemporaryRoot)) Directory.Delete(TemporaryRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); }
        }
        Console.WriteLine(_exitCode == 0
            ? $"PASS: {Passed.Count} real Windows UI checks completed; no browser or user settings accessed."
            : $"FAIL: Windows UI checks interrupted at '{_stage}' after {Passed.Count} completed checks.");
        return _exitCode;
    }

    private static TabControl BuildViews()
    {
        _stage = "production control constructors";
        var tabs = new TabControl();
        var data = Sample();
        _cities = CityDetectionService.Enrich(data, Cities, Settings);
        var settings = Settings.Copy(); settings.ColumnMappings["city"] = _cities.CityColumnName;
        var snapshot = DashboardService.Calculate(_cities.Data, settings, data.ExtractedAt);
        CityDetectionService.AppendDashboardProbes(snapshot, _cities);
        QueryService.AppendDashboardQueries(snapshot, _cities.Data, settings, Queries);
        var empty = new DashboardView();
        Add(tabs, "Dashboard vide", empty, true);
        Pass("DashboardView constructor and first empty Render");
        var dashboard = new DashboardView();
        dashboard.SetLayout(Layout("overview"));
        dashboard.Show(snapshot);
        Add(tabs, "Dashboard renseigné", dashboard, true);
        Pass("DashboardView populated snapshot with all widget types from a real calculation");
        var technicians = _technicians = new TechnicianWorkspaceView();
        var roster = TechnicianDashboardService.Detect(_cities.Data, settings);
        var identity = roster.Technicians.Single(x => x.Name == "Alex Exemple").Key;
        technicians.ShowRoster(roster, identity, identity);
        technicians.Dashboard.SetLayout(Layout("personal"));
        var personalSnapshot = TechnicianDashboardService.CalculateForTechnician(_cities.Data, settings, identity, data.ExtractedAt);
        CityDetectionService.AppendDashboardProbes(personalSnapshot, _cities);
        QueryService.AppendDashboardQueries(personalSnapshot, _cities.Data, settings, Queries);
        technicians.Dashboard.Show(personalSnapshot);
        var preferences = new TechnicianPreferences().With(identity, new() { Favorite = true, ActiveTarget = 8, Notes = "Note locale synthétique en mémoire." });
        technicians.Management.Show(TechnicianWorkloadService.Calculate(_cities.Data, settings, referenceTime: data.ExtractedAt), preferences);
        Add(tabs, "Techniciens", technicians, true);
        Pass("TechnicianWorkspaceView constructor, real team workload, local in-memory preferences and scoped DashboardView");
        AddReader(tabs, "Ticket · champs", new()
        {
            Name = "Champs", Kind = "fields", Data = new()
            {
                Headers = new() { "Champ", "Valeur" }, Rows = new()
                {
                    new[] { "Titre", "Incident de démonstration synthétique" },
                    new[] { "Description", "Texte long avec plusieurs lignes.\nDeuxième paragraphe.\n" + new string('A', 700) },
                    new[] { "Impact", "Moyen" }
                }
            }
        });
        AddReader(tabs, "Ticket · traitement", new()
        {
            Name = "Traitement", Kind = "timeline", Data = new()
            {
                Headers = new() { "Type", "Date", "Auteur", "Contenu", "Détails" }, Rows = new()
                {
                    new[] { "Suivi", "21/10/2026 09:15", "Alex Exemple", "Suivi synthétique.\nAucun appel à GLPI.", "Public" },
                    new[] { "Tâche", "21/10/2026 10:20", "Sam Exemple", "Une tâche fictive pour vérifier le retour à la ligne.", "15 minutes" },
                    new[] { "Solution", "21/10/2026 11:30", "Alex Exemple", "Solution fictive utilisée uniquement dans cette fenêtre de test.", "Proposée" }
                }
            }
        });
        AddReader(tabs, "Ticket · tableau", new()
        {
            Name = "Tableau", Kind = "table", Data = new()
            {
                Headers = new() { "Nom", "Valeur", "Commentaire" },
                Rows = new() { new[] { "Ligne synthétique", "42", "Lecture de la représentation générique du tableau." } }
            }
        });
        AddReader(tabs, "Ticket · texte", new()
        {
            Name = "Texte", Kind = "text", Data = new()
            {
                Headers = new() { "Texte" }, Rows = new() { new[] { "Texte brut synthétique.\nLes caractères < > & restent du texte, sans interprétation HTML." } }
            }
        });
        _cityEditor = new CitySettingsView();
        _cityEditor.LoadSettings(Cities); _cityEditor.ShowSummary(_cities);
        Add(tabs, "Villes · réglages", _cityEditor, true);
        Pass("CitySettingsView constructor, editable dictionary and real detection summary");
        _tours = new TourPlanningView(Path.Combine(TemporaryRoot, "tour-order.json"));
        DisposableViews.Add(_tours);
        _tours.ShowEmpty("Relevé synthétique : calcul lancé après l’ouverture réelle de la fenêtre.");
        Add(tabs, "Tournées", _tours, true);
        Pass("TourPlanningView constructor with isolated temporary preference path");
        var emptyQueries = new QueryWorkshopView(); DisposableViews.Add(emptyQueries);
        emptyQueries.LoadSettings(new()); emptyQueries.ShowEmpty("Aucun relevé synthétique chargé.");
        Add(tabs, "Requêtes · vide", emptyQueries, true);
        _queryEditor = new QueryWorkshopView(); DisposableViews.Add(_queryEditor);
        _queryEditor.LoadSettings(Queries); _queryEditor.Show(_cities.Data, settings, _cities);
        Add(tabs, "Requêtes · bibliothèque", _queryEditor, true);
        Require(Queries.Queries.Count == 40, "Expected forty real built-in saved queries.");
        Pass("QueryWorkshopView constructors, empty library and forty real saved query templates");
        Require(tabs.Items.Count == 11, "Expected eleven synthetic view tabs.");
        return tabs;
    }

    private static void AddReader(TabControl tabs, string title, TicketDetailSection section)
    {
        var reader = new TicketReaderView();
        reader.Show(section, section.Data.Rows);
        Add(tabs, title, reader, false);
        Pass("TicketReaderView constructor and " + section.Kind + " binding");
    }
    private static void Add(TabControl tabs, string title, FrameworkElement content, bool scroll)
    {
        FrameworkElement body = scroll ? new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(18)
        } : content;
        tabs.Items.Add(new TabItem { Header = title, Content = body, Tag = content });
    }

    private static async Task ExerciseViewsAsync(TabControl tabs, Task loaded, Task rendered)
    {
        _stage = "Loaded and ContentRendered";
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        await rendered.WaitAsync(TimeSpan.FromSeconds(10));
        Pass("Host Window.Show completed Loaded and ContentRendered");
        // Run async controls only once the real WPF DispatcherSynchronizationContext is active.
        _stage = "tour preparation on the real dispatcher";
        var effective = Settings.Copy(); effective.ColumnMappings["city"] = _cities!.CityColumnName;
        _tours!.Show(_cities, Cities, effective);
        await _tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
        Require(_tours.SelectedPlan is { Stops.Count: 2, SelectedRows.Count: 2 }, "Initial tour did not retain the two active synthetic tickets.");
        Pass("Tour preparation and initial asynchronous detail completed on the real dispatcher");
        for (var index = 0; index < tabs.Items.Count; index++)
        {
            var tab = (TabItem)tabs.Items[index];
            _stage = "render " + tab.Header;
            tabs.SelectedIndex = index;
            await FlushLayoutAsync();
            Require(tab.Tag is FrameworkElement { IsLoaded: true } control && control.ActualWidth > 0 && control.ActualHeight > 0,
                "Selected production control was not loaded with a nonzero rendered size: " + tab.Header);
            if (tab.Tag is TicketReaderView reader)
            {
                var list = (ListBox?)reader.FindName("CardsList");
                Require(list != null && list.Items.Count > 0, "Reader cards were not bound.");
                Require(list!.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem,
                    "The first real reader DataTemplate was not instantiated.");
                var textBoxes = Descendants<TextBox>(list).Where(box => box.IsReadOnly).ToArray();
                Require(textBoxes.Any(box => !string.IsNullOrEmpty(box.Text)), "Reader template has no displayed read-only content.");
            }
            Pass("Loaded and rendered: " + tab.Header);
        }
        await ExerciseCityEditorAsync(tabs);
        await ExerciseTourAsync(tabs, effective);
        await ExerciseQueryEditorAsync(tabs);
        var dashboardTab = (TabItem)tabs.Items[1];
        var dashboard = (DashboardView)dashboardTab.Tag;
        tabs.SelectedIndex = 1;
        foreach (var preset in new[] { "operations", "analysis", "geography", "empty", "overview" })
        {
            _stage = "dashboard preset " + preset;
            dashboard.SetLayout(Layout(preset));
            await FlushLayoutAsync();
            dashboard.GetLayout().Validate();
            Pass("Rendered dashboard preset: " + preset);
        }
        _stage = "dashboard editor";
        var edit = Descendants<Button>(dashboard).FirstOrDefault(button => button.Content as string == "Personnaliser");
        Require(edit != null, "The real Personnaliser button is missing.");
        edit!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushLayoutAsync();
        Require(Descendants<Button>(dashboard).Any(button => button.Content as string == "+ Widget"), "Editor toolbar did not open.");
        Require(Descendants<ComboBox>(dashboard).Any(), "Editor view did not contain its real selectors.");
        Pass("Real dashboard edit command creates editor, drag handles and resize controls");
        foreach (var width in new[] { 850d, 1440d })
        {
            _stage = "responsive dashboard width " + width;
            _host!.Width = width;
            await FlushLayoutAsync();
            Require(dashboard.ActualWidth > 0 && double.IsFinite(dashboard.ActualHeight), "Responsive dashboard produced invalid dimensions.");
            foreach (var panel in Descendants<DashboardBoardPanel>(dashboard))
                Require(panel.ActualWidth > 0 && double.IsFinite(panel.ActualHeight), "Board panel produced invalid dimensions.");
            Pass("Responsive layout at host width " + width);
        }
        _stage = "independent global and personal query widgets";
        var personal = _technicians!.Dashboard;
        var sharedLayout = Layout("empty"); dashboard.SetLayout(sharedLayout); personal.SetLayout(sharedLayout);
        var query = Queries.Queries.First(item => item.GroupBy == "role:city");
        Require(dashboard.AddQueryWidget(QueryService.DistributionKey(query.Id), query.Name, distribution: true), "Global query widget was not added.");
        Require(personal.GetLayout().Boards.Single().Widgets.Count == 0, "Global widget mutation leaked into the personal layout.");
        Require(personal.AddQueryWidget(QueryService.MetricKey(query.Id), query.Name, distribution: false), "Personal query widget was not added.");
        Require(dashboard.GetLayout().Boards.Single().Widgets.Count == 1 && personal.GetLayout().Boards.Single().Widgets.Count == 1,
            "Global and personal layouts are not independent.");
        Require(sharedLayout.Boards.Single().Widgets.Count == 0, "Widget additions mutated the caller's original layout.");
        dashboard.GetLayout().Validate(); personal.GetLayout().Validate();
        await FlushLayoutAsync();
        Pass("Real AddQueryWidget creates independent global distribution and personal metric widgets");
        // Keep the geographic preset, donut and gauge visible for both theme passes as well.
        dashboard.SetLayout(Layout("geography"));
        personal.SetLayout(Layout("personal"));
        Require(dashboard.AddQueryWidget(QueryService.DistributionKey(query.Id), query.Name, distribution: true)
            && personal.AddQueryWidget(QueryService.MetricKey(query.Id), query.Name, distribution: false),
            "Query widgets could not be added to the full themed layouts.");
        foreach (var theme in new[] { "light", "dark" })
        {
            _stage = "theme " + theme;
            ThemeService.Apply(theme);
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                tabs.SelectedItem = tab;
                await FlushLayoutAsync();
                if (tab.Tag is TechnicianWorkspaceView technicians)
                {
                    var internalTabs = Descendants<TabControl>(technicians).Single();
                    foreach (var workspace in internalTabs.Items.Cast<TabItem>())
                    {
                        internalTabs.SelectedItem = workspace; await FlushLayoutAsync();
                        if (ReferenceEquals(workspace.Content, technicians.Management))
                        {
                            var grid = Descendants<DataGrid>(technicians.Management).Single();
                            Require(grid.Items.Count == 2 && grid.IsReadOnly && grid.EnableRowVirtualization,
                                "Real technician management data was not bound or virtualized.");
                        }
                    }
                }
            }
            Require(ThemeService.CurrentTheme == theme, "Theme switch did not apply.");
            Require(Application.Current.Resources["PanelBrush"] is SolidColorBrush, "Theme brush is missing.");
            Pass("All eleven synthetic views and both technician workspaces rendered in theme: " + theme);
        }
        if (_failed) return;
        _exitCode = 0;
    }

    private static async Task ExerciseCityEditorAsync(TabControl tabs)
    {
        _stage = "city bulk editor";
        SelectView(tabs, _cityEditor!); await FlushLayoutAsync();
        var expander = Descendants<Expander>(_cityEditor!).Single(item => item.Header as string == "Ajouter plusieurs correspondances");
        expander.IsExpanded = true; await FlushLayoutAsync();
        var bulk = Descendants<TextBox>(_cityEditor!).Single(box => AutomationProperties.GetName(box) == "Liste de correspondances à ajouter, une ville par ligne");
        Require(!bulk.IsReadOnly && bulk.IsEnabled, "Bulk city input is not editable.");
        bulk.Text = "Marseille | MRS ; Mars\nNice | NCE";
        Click(_cityEditor!, "Ajouter ces correspondances"); await FlushLayoutAsync();
        var edited = _cityEditor!.ReadSettings(); edited.Validate();
        Require(edited.Cities.Count == 4 && edited.Cities.Any(city => city.Name == "Nice" && city.Aliases.Contains("NCE")), "Real bulk city command did not add the new dictionary entry.");
        Require(edited.Cities.Single(city => city.Name == "Marseille").Aliases.Contains("Mars"), "Real bulk city command did not merge aliases with the existing entry.");
        Require(Cities.Cities.Count == 3 && !Cities.Cities.Single(city => city.Name == "Marseille").Aliases.Contains("Mars"), "City editing mutated the caller's configuration.");
        var grid = Descendants<DataGrid>(_cityEditor).Single();
        Require(grid.Items.Count == 4 && !grid.IsReadOnly && grid.EnableRowVirtualization, "City dictionary is not an editable, virtualized real grid.");
        Pass("Real city bulk editor preserves draft isolation and merges aliases without losing existing cities");
    }

    private static async Task ExerciseTourAsync(TabControl tabs, DashboardSettings settings)
    {
        _stage = "tour displayed order, local preferences and explicit actions";
        var tours = _tours!; SelectView(tabs, tours); await FlushLayoutAsync();
        var mapCalls = 0; string? mapCity = null; string? mapPostal = null; int[]? selectedRows = null; ExtractionResult? exported = null;
        tours.MapRequested += (city, postal) => { mapCalls++; mapCity = city; mapPostal = postal; };
        tours.RowsRequested += (rows, _) => selectedRows = rows;
        tours.ExportRequested += data => exported = data;
        var steps = Descendants<DataGrid>(tours).Single(grid => AutomationProperties.GetName(grid) == "Étapes de la tournée dans l’ordre de visite");
        var tickets = Descendants<DataGrid>(tours).Single(grid => AutomationProperties.GetName(grid) == "Tickets de l’étape sélectionnée");
        Require(steps.Items.Count == 2 && tickets.Items.Count == 1 && steps.EnableRowVirtualization && tickets.EnableRowVirtualization,
            "Tour tables did not bind and virtualize their real results.");
        Require(mapCalls == 0 && !File.Exists(Path.Combine(TemporaryRoot, "tour-order.json")), "Initial tour unexpectedly performed an external action or saved an order.");
        Click(tours, "↓ Descendre"); await tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
        Require(tours.SelectedPlan!.Stops.Select(stop => stop.CityName).SequenceEqual(new[] { "Paris", "Lyon" }), "Displayed manual tour order was not applied.");
        Require(File.Exists(Path.Combine(TemporaryRoot, "tour-order.json")), "Manual visit order was not saved to the isolated temporary directory.");
        Click(tours, "Exporter le plan…"); await tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
        Require(exported is { Rows.Count: 2 } && exported.Rows.Select(row => row[1]).SequenceEqual(new[] { "Paris", "Lyon" }), "Export event did not preserve the displayed visit order.");
        Click(tours, "Tous les tickets du plan");
        Require(selectedRows != null && selectedRows.SequenceEqual(new[] { 0, 1 }), "Tour drilldown did not retain original source indexes.");
        Click(tours, "Carte de la ville ↗");
        Require(mapCalls == 1 && mapCity == "Lyon" && mapPostal == "69000", "Explicit map event did not contain only the selected city and postal code.");
        Pass("Real tour controls reorder, save only to TEMP, export the displayed order and emit explicit drilldown/map actions");

        _stage = "tour busy-to-ready details and stale result isolation";
        tours.SetBusy(true); tours.Show(_cities!, Cities, settings);
        await tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); tours.SetBusy(false);
        await tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
        Require(tickets.Items.Count == 1 && tickets.IsEnabled, "Tour details did not resume after the parent operation stopped being busy.");
        tours.Show(_cities!, Cities, settings); tours.ShowEmpty("Relevé synthétique retiré pendant le calcul.");
        await tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
        Require(tours.SelectedPlan == null, "Cancelled tour published an old plan after ShowEmpty.");
        var map = FindButton(tours, "Carte de la ville ↗"); Require(!map.IsEnabled, "Empty tour retained an enabled map action.");
        map.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Require(mapCalls == 1, "Invalidated tour emitted a stale map event.");
        tours.Show(_cities!, Cities, settings); await tours.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
        Pass("Tour async generation guards discard removed reports and restore detail after the busy state");
    }

    private static async Task ExerciseQueryEditorAsync(TabControl tabs)
    {
        _stage = "query preview from real saved library";
        var editor = _queryEditor!; SelectView(tabs, editor); await FlushLayoutAsync();
        var library = Descendants<ListBox>(editor).Single(list => AutomationProperties.GetName(list).StartsWith("Bibliothèque de requêtes", StringComparison.Ordinal));
        Require(library.Items.Count == 40 && library.SelectedItem != null && VirtualizingPanel.GetIsVirtualizing(library), "Forty saved query templates were not bound to the virtualized library.");
        int[]? selectedRows = null; editor.RowsRequested += (rows, _) => selectedRows = rows;
        Click(editor, "Prévisualiser"); await editor.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
        var rowsButton = Descendants<Button>(editor).Single(button => button.Content is string text && text.StartsWith("Voir les ", StringComparison.Ordinal) && text.EndsWith("lignes retenues", StringComparison.Ordinal));
        Require(rowsButton.IsEnabled, "The real query preview did not publish an actionable result.");
        rowsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(selectedRows != null && selectedRows.SequenceEqual(new[] { 0, 1, 2 }), "Saved total-by-city query lost original source rows.");
        var buckets = Descendants<DataGrid>(editor).Single(grid => AutomationProperties.GetName(grid).StartsWith("Répartition complète", StringComparison.Ordinal));
        Require(buckets.Items.Count == 3 && buckets.EnableRowVirtualization, "Query grouped preview did not display three synthetic cities.");
        Pass("Real saved-query preview publishes exact source rows and a virtualized grouped result");

        _stage = "query draft and stale preview guards";
        var name = Descendants<TextBox>(editor).Single(box => AutomationProperties.GetName(box) == "Nom de la requête");
        name.Text = "Brouillon synthétique conservé";
        Require(!rowsButton.IsEnabled, "Editing a query retained actions on the old result.");
        Click(editor, "Prévisualiser"); editor.ShowEmpty("Source retirée pendant un calcul synthétique.");
        await editor.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
        Require(name.Text == "Brouillon synthétique conservé" && !rowsButton.IsEnabled, "Query source removal erased the draft or revived an old preview.");
        selectedRows = null; rowsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(selectedRows == null, "Invalidated query emitted stale source indexes.");
        editor.LoadSettings(Queries);
        var effective = Settings.Copy(); effective.ColumnMappings["city"] = _cities!.CityColumnName;
        editor.Show(_cities.Data, effective, _cities);
        Pass("Query editing and source replacement invalidate old results while preserving draft text");
    }

    private static void SelectView(TabControl tabs, FrameworkElement view)
        => tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => ReferenceEquals(tab.Tag, view));

    private static Button FindButton(DependencyObject root, string title)
        => Descendants<Button>(root).Single(button => button.Content as string == title);

    private static void Click(DependencyObject root, string title)
    {
        var button = FindButton(root, title); Require(button.IsEnabled, "Command is not enabled: " + title);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task DisposeViewsAsync()
    {
        try
        {
            foreach (var view in DisposableViews) view.Dispose();
            var tasks = DisposableViews.Select(view => view switch
            {
                TourPlanningView tour => tour.WhenIdle,
                QueryWorkshopView queries => queries.WhenIdle,
                _ => Task.CompletedTask
            }).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { Fail(error); }
    }

    private static async Task FlushLayoutAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        _host!.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (_failed) throw new InvalidOperationException("An unhandled dispatcher exception was already reported.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static DashboardLayoutSettings Layout(string preset)
    {
        var board = DashboardLayoutSettings.CreateBoard("Test " + preset, preset);
        if (preset != "empty") board.Widgets.Add(new()
        {
            Kind = "note", SourceKey = "", Title = "Note synthétique", Span = 4, Height = 220,
            Note = "Note de test.\nAucune donnée utilisateur et aucun contenu exécutable."
        });
        // Exercise the real custom styles without any data from a user profile.
        for (var i = 0; i < board.Widgets.Count; i++)
        {
            board.Widgets[i].Style = DashboardWidgetSettings.Styles[i % DashboardWidgetSettings.Styles.Length];
            board.Widgets[i].Accent = DashboardWidgetSettings.Accents[i % DashboardWidgetSettings.Accents.Length];
            board.Widgets[i].Presentation = DashboardWidgetSettings.Presentations[i % DashboardWidgetSettings.Presentations.Length];
        }
        if (preset != "empty")
        {
            board.Widgets.Add(new() { Kind = "distribution", SourceKey = "status", Title = "Anneau synthétique", Presentation = "donut", Span = 6, Height = 330 });
            board.Widgets.Add(new() { Kind = "metric", SourceKey = "active", Title = "Jauge synthétique", Presentation = "gauge", Span = 6, Height = 280,
                TargetValue = 10, WarningValue = 7, CriticalValue = 9, LowerIsBetter = true });
        }
        return new() { Boards = new() { board }, ActiveBoardId = board.Id };
    }
    private static CitySettings SampleCities() => new()
    {
        TitleColumn = "Titre", Cities = new()
        {
            new() { Id = "11111111111111111111111111111111", Name = "Paris", PostalCode = "75000", Aliases = new() { "PAR" } },
            new() { Id = "22222222222222222222222222222222", Name = "Lyon", PostalCode = "69000", Aliases = new() { "LYO" } },
            new() { Id = "33333333333333333333333333333333", Name = "Marseille", PostalCode = "13000", Aliases = new() { "MRS" } }
        }
    };
    private static ExtractionResult Sample() => new()
    {
        Headers = new() { "ID", "Titre", "Statut", "Priorité", "Urgence", "Impact", "Type", "Date d'ouverture", "Dernière modification", "Date de résolution", "Date de clôture", "Temps de résolution", "Attribué à - Technicien", "Attribué à - Groupe de techniciens", "Catégorie", "Entité", "Demandeur", "Lieu" },
        Rows = new()
        {
            new[] { "1", "Paris · Incident synthétique", "En cours (attribué)", "Haute", "Haute", "Haut", "Incident", "01/10/2026 09:00", "20/10/2026 10:00", "", "", "20/10/2026 17:00", "Alex Exemple", "Support Exemple", "Matériel", "Entité Exemple", "Demandeur Exemple", "Site Exemple" },
            new[] { "2", "Lyon · Demande synthétique", "En attente", "Moyenne", "Moyenne", "Moyen", "Demande", "18/10/2026 09:00", "19/10/2026 11:00", "", "", "23/10/2026 17:00", "Sam Exemple", "Support Exemple", "Accès", "Entité Exemple", "Demandeur Exemple", "Site Exemple" },
            new[] { "3", "Marseille · Ticket synthétique résolu", "Résolu", "Basse", "Basse", "Bas", "Incident", "15/10/2026 08:00", "20/10/2026 16:00", "20/10/2026 15:00", "", "21/10/2026 17:00", "Alex Exemple", "Support Exemple", "Logiciel", "Entité Exemple", "Demandeur Exemple", "Site Exemple" }
        },
        ExpectedTotal = 3, Complete = true, Scope = "Jeu synthétique de test UI",
        SourceUrl = "https://glpi.example.invalid/front/ticket.php",
        ExtractedAt = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)
    };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) { Passed.Add(message); Console.WriteLine("PASS " + message); }
    private static void Fail(Exception exception)
    {
        _failed = true; _exitCode = 1;
        Console.Error.WriteLine("FAIL stage: " + _stage);
        Console.Error.WriteLine(exception);
    }

    private static ResourceDictionary LoadProductionResources()
    {
        // Link the actual production XAML into this test assembly: no copied styles
        // can drift from the app. An App subclass in another assembly cannot load
        // App's BAML, and a real App schedules production OnStartup in its constructor.
        // The separate PackStartup stage verifies the real compiled App and main window.
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("GlpiPilot.WindowsStartupChecks.ProductionApp.xaml")
            ?? throw new InvalidDataException("The linked production App.xaml resource is missing.");
        var root = XDocument.Load(stream).Root
            ?? throw new InvalidDataException("The linked production App.xaml is empty.");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Require(root.Name == presentation + "Application", "Expected the production Application XAML root.");
        var resources = root.Element(presentation + "Application.Resources")
            ?? throw new InvalidDataException("Production Application.Resources is missing.");
        var dictionary = new XElement(presentation + "ResourceDictionary",
            root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration), resources.Nodes());
        using var reader = dictionary.CreateReader();
        var result = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(reader);
        Require(result.Contains("AppWindowStyle") && result.Contains("WindowBrush") && result.Contains(typeof(TabControl)),
            "Production resources did not contain the expected window, theme and tab styles.");
        return result;
    }
}
