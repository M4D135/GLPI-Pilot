using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Calendar = System.Windows.Controls.Calendar;
using System.Globalization;
using System.IO;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static HistoryWorkspaceView? _historyView;
    private static DataQualityView? _qualityView;
    private static ControlCenterView? _controlCenter;
    private static PreviewColumnsDialog? _columnsView;
    private static CommandPaletteWindow? _paletteView;
    private static AlertsView? _alertsView;
    private static PreviewViewsView? _namedViews;
    private static NamedPreviewStore? _namedViewsStore;

    private static void AddFoundationViews(TabControl tabs)
    {
        _historyView = new();
        _historyView.Show(new[] { new SnapshotMetadata { Id = "11111111111111111111111111111111", CapturedAt = Sample().ExtractedAt, Rows = 3, Columns = 18, SourceLabel = "Relevé synthétique", Complete = true } });
        Add(tabs, "Historique", _historyView, false);
        _qualityView = new(); _qualityView.Show(DataQualityService.Analyze(Sample(), Settings)); Add(tabs, "Qualité", _qualityView, false);
        _controlCenter = new(); _controlCenter.Load(new() { Sources = new() { new() { Name = "Source synthétique", Url = Sample().SourceUrl } } }); Add(tabs, "Sources", _controlCenter, false);
        _columnsView = new(Sample().Headers, Array.Empty<PreviewColumnPreference>()); Add(tabs, "Colonnes intégrées", _columnsView, false);
        _paletteView = new(new[] { new WorkspaceCommand("Historique", "Ouvrir les relevés conservés", "", () => { }), new WorkspaceCommand("Action indisponible", "Exemple désactivé", "", () => { }, false) });
        Add(tabs, "Palette intégrée", _paletteView, false);
        _alertsView = new();
        _alertsView.Load(new() { Rules = new() { new() { Name = "Échéances synthétiques", MetricKey = "overdue", Threshold = 1 } }, Incidents = new()
        { new() { Id = "22222222222222222222222222222222", Name = "Alerte synthétique", Value = 2, Unit = "tickets", FirstObserved = Sample().ExtractedAt, LastObserved = Sample().ExtractedAt, Rows = new[] { 0, 1 } } } });
        _alertsView.SetMetrics(DashboardService.Calculate(Sample(), Settings, Sample().ExtractedAt)); Add(tabs, "Alertes", _alertsView, false);
        _namedViewsStore = new(Path.Combine(TemporaryRoot, "table-views.json"));
        _namedViews = new(Sample().Headers, new() { Filters = new() { new() { Column = 2, Operator = "eq", Value = "En attente" } }, Sorts = new() { new() { Column = 0, Type = "number", Descending = true } } },
            "Paris", Array.Empty<PreviewColumnPreference>(), CultureInfo.GetCultureInfo("fr-FR"), _namedViewsStore);
        Add(tabs, "Vues du relevé", _namedViews, false);
        Pass("History, quality, source management, integrated columns and command palette construct from isolated synthetic state");
    }

    private static async Task ExerciseFoundationViewsAsync(TabControl tabs)
    {
        _stage = "history quality and sources workspace";
        SelectView(tabs, _historyView!); await FlushLayoutAsync();
        string? requestedId = null; _historyView!.OpenRequested += id => requestedId = id;
        var catalogue = Descendants<DataGrid>(_historyView).Single(); catalogue.SelectedIndex = 0;
        Click(_historyView, "Ouvrir ce relevé"); Require(requestedId == "11111111111111111111111111111111", "History did not emit the selected immutable snapshot identity.");
        var comparison = new SnapshotComparison { Compatible = true, Absent = 1, Changed = 1 };
        comparison.Changes.Add(new("Modifié", "1", "Statut", "En attente", "Résolu", 0, 0)); _historyView.ShowComparison(comparison); await FlushLayoutAsync();
        Require(Descendants<DataGrid>(_historyView).Single().Items.Count == 1, "Historical comparison did not bind observed source changes.");
        SelectView(tabs, _qualityView!); await FlushLayoutAsync();
        Require(Descendants<DataGrid>(_qualityView!).Single().Items.Count == Sample().Headers.Count, "Quality columns do not describe the source schema.");
        foreach (var view in new FrameworkElement[] { _qualityView!, _controlCenter! })
        {
            SelectView(tabs, view); await FlushLayoutAsync(); var pages = Descendants<TabControl>(view).First();
            foreach (var page in pages.Items.Cast<TabItem>()) { pages.SelectedItem = page; await FlushLayoutAsync(); }
        }
        var applied = new AppSettings(); _controlCenter!.ApplyTo(applied);
        Require(applied.Sources.Count == 1 && applied.Sources[0].Url == Sample().SourceUrl, "Source editor did not preserve the complete search address.");
        Pass("Historical selection/comparison, column quality and each source/automation/preferences page render with real bindings");
        SelectView(tabs, _columnsView!); await FlushLayoutAsync(); bool? complete = null; _columnsView!.Completed += value => complete = value;
        Click(_columnsView, "Appliquer"); Require(complete == true && _columnsView.Preferences?.Length == Sample().Headers.Count, "Embedded columns editor no longer emits a complete preference draft.");
        SelectView(tabs, _paletteView!); await FlushLayoutAsync();
        var query = (TextBox)_paletteView!.FindName("QueryBox"); query.Text = "historique";
        Require(((ListBox)_paletteView.FindName("ActionsList")).Items.Count == 1, "Embedded command palette search lost filtering.");
        Require(Application.Current.Windows.Count == 1, "An integrated editor created an unsolicited secondary window.");
        Pass("Columns and command palette render in the existing host, filter correctly and create no secondary windows");

        SelectView(tabs, _alertsView!); await FlushLayoutAsync();
        string? acknowledged = null; _alertsView!.AcknowledgeRequested += id => acknowledged = id;
        var incidents = Descendants<DataGrid>(_alertsView).Single(); incidents.SelectedIndex = 0; Click(_alertsView, "Pris en compte");
        Require(acknowledged == "22222222222222222222222222222222", "Alert command did not preserve the selected incident identity.");
        var alertTabs = Descendants<TabControl>(_alertsView).Single(); alertTabs.SelectedIndex = 1; await FlushLayoutAsync();
        IReadOnlyList<AlertRule>? rules = null; _alertsView.SaveRequested += value => rules = value;
        Click(_alertsView, "Ajouter une alerte"); Click(_alertsView, "Enregistrer les règles");
        Require(rules?.Count == 2 && rules[0].Name == "Échéances synthétiques", "Alert rule editor did not emit its complete isolated draft.");
        Pass("Alerts bind metric choices, retain incident identity and emit edited rules without constructing a persistent alert service");

        SelectView(tabs, _namedViews!); await FlushLayoutAsync();
        var viewTabs = Descendants<TabControl>(_namedViews!).Single();
        foreach (var page in viewTabs.Items.Cast<TabItem>()) { viewTabs.SelectedItem = page; await FlushLayoutAsync(); }
        NamedPreview? selectedView = null; _namedViews!.Applied += value => selectedView = value;
        Click(_namedViews, "Mémoriser et appliquer");
        Require(selectedView?.Search == "Paris" && selectedView.Transform.Filters.Single().Value == "En attente" && selectedView.Transform.Sorts.Single().Descending,
            "Named-view editor lost its complete filters, search or sort draft.");
        Require(_namedViewsStore!.List().Single().Id == selectedView!.Id, "Named-view action did not persist through the injected temporary store.");
        Pass("Named table views render both editors, preserve search/filter/sort state and save only into the isolated temporary store");
        await ExerciseDatePickerAsync(tabs);
        await ExerciseCompletionViewsAsync(tabs);
    }

    private static async Task ExerciseDatePickerAsync(TabControl tabs)
    {
        _stage = "themed date entry and calendar navigation";
        var picker = new DatePicker { SelectedDate = new DateTime(2026, 10, 22), Width = 180, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Top };
        var page = new TabItem { Header = "Calendrier synthétique", Content = picker }; tabs.Items.Add(page); tabs.SelectedItem = page;
        try
        {
            foreach (var theme in new[] { "dark", "light" })
            {
                ThemeService.Apply(theme); await FlushLayoutAsync(); picker.IsDropDownOpen = true; await FlushLayoutAsync();
                var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                var calendar = popup.Child as Calendar ?? Descendants<Calendar>(popup.Child).Single();
                Require(calendar.IsVisible && Descendants<CalendarDayButton>(calendar).Count() == 42, "The date popup did not render six weeks of selectable dates.");
                Require(((SolidColorBrush)picker.Background).Color == ((SolidColorBrush)Application.Current.FindResource("FieldBrush")).Color &&
                    ((SolidColorBrush)calendar.Background).Color == ((SolidColorBrush)Application.Current.FindResource("PanelBrush")).Color,
                    "Date field and calendar did not follow the active theme.");
                var item = Descendants<CalendarItem>(calendar).Single();
                var next = (Button)item.Template.FindName("PART_NextButton", item); var previousMonth = calendar.DisplayDate;
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FlushLayoutAsync();
                Require(calendar.DisplayDate.Month != previousMonth.Month, "Calendar next-month command lost its production handler.");
                var header = (Button)item.Template.FindName("PART_HeaderButton", item); header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FlushLayoutAsync();
                var monthView = (Grid)item.Template.FindName("PART_MonthView", item);
                var yearView = (Grid)item.Template.FindName("PART_YearView", item);
                Require(calendar.DisplayMode == CalendarMode.Year && yearView.IsVisible && !monthView.IsVisible
                    && Descendants<CalendarButton>(calendar).Count(button => button.IsVisible) == 12,
                    "Calendar header did not open the real month selection; mode=" + calendar.DisplayMode + ", month=" + monthView.Visibility + ", year=" + yearView.Visibility + ".");
                var selectedYear = calendar.DisplayDate.Year;
                header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FlushLayoutAsync();
                Require(calendar.DisplayMode == CalendarMode.Decade && yearView.IsVisible && !monthView.IsVisible
                    && Descendants<CalendarButton>(calendar).Count(button => button.IsVisible) == 12,
                    "Calendar header did not display the real decade selection.");
                var yearButton = Descendants<CalendarButton>(calendar).Single(button => button.IsVisible
                    && button.DataContext is DateTime date && date.Year == selectedYear);
                yearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FlushLayoutAsync();
                Require(calendar.DisplayMode == CalendarMode.Year && yearView.IsVisible && !monthView.IsVisible,
                    "Choosing a year did not return to the visible month selection.");
                var selectedMonth = calendar.DisplayDate.Month;
                var monthButton = Descendants<CalendarButton>(calendar).Single(button => button.IsVisible
                    && button.DataContext is DateTime date && date.Year == selectedYear && date.Month == selectedMonth);
                monthButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FlushLayoutAsync();
                Require(calendar.DisplayMode == CalendarMode.Month && monthView.IsVisible && !yearView.IsVisible
                    && Descendants<CalendarDayButton>(calendar).Count(button => button.IsVisible) == 42,
                    "Choosing a month did not restore the visible day grid.");
                var selectedDate = new DateTime(selectedYear, selectedMonth, 15);
                var dayButton = Descendants<CalendarDayButton>(calendar).Single(button => button.IsVisible
                    && button.DataContext is DateTime date && date.Date == selectedDate);
                dayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FlushLayoutAsync();
                Require(picker.SelectedDate?.Date == selectedDate, "Choosing a day did not update the DatePicker value.");
                picker.IsDropDownOpen = false; await FlushLayoutAsync();
            }
            Pass("Date fields and calendar popup follow both themes; native month, year and decade navigation returns to selectable days");
        }
        finally { picker.IsDropDownOpen = false; tabs.Items.Remove(page); ThemeService.Apply("dark"); }
    }
}
