using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Ticket-local navigation stays within the main window and does not invoke the browser.</summary>
public sealed class TicketWorkplaceView : UserControl
{
    private readonly StackPanel _ticketTabs = new() { Orientation = Orientation.Horizontal };
    private readonly TabControl _pages = new();
    private readonly TabItem _readerPage, _personalPage, _searchPage, _handoverPage;
    private readonly TextBox _search = new() { MinWidth = 200, MaxLength = 500 };
    private readonly TextBlock _searchCount = WorkplaceControls.Label("Rechercher dans toutes les sections déjà lues.");
    private readonly DataGrid _searchResults = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, EnableRowVirtualization = true, MinHeight = 120 };
    private readonly TextBox _handover = WorkplaceControls.Editor(double.NaN, 1000000, true);
    private readonly TextBox _summary = WorkplaceControls.Editor(double.NaN, 1000000, true);
    private readonly ComboBox _type = new() { MinWidth = 130 };
    private readonly TextBox _author = new() { Width = 150, MaxLength = 200 };
    private readonly DatePicker _from = new() { Width = 135 };
    private readonly DatePicker _to = new() { Width = 135 };
    private readonly CheckBox _groupDay = new() { Content = "Regrouper par journée", IsChecked = true, Margin = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _changes = WorkplaceControls.Label("");
    private readonly Button _close, _favoriteSection, _defaultSection;
    private TicketDetailResult? _detail;
    private PersonalTicket? _personal;
    private bool _updating;
    private long _searchGeneration;
    public PersonalTicketEditorView Editor { get; } = new();
    public TicketIntelligenceView Intelligence { get; } = new();
    public TicketInsightsView Insights { get; } = new();
    public TicketActionsView Actions { get; } = new();
    public TicketDraftView Drafts { get; } = new();
    public TicketEnrichmentView Enrichment { get; } = new();
    public TicketTimelineFilter TimelineFilter => new(_type.SelectedIndex > 0 ? _type.SelectedItem as string ?? "" : "", _author.Text.Trim(), _from.SelectedDate, _to.SelectedDate);
    public bool GroupByDay => _groupDay.IsChecked == true;
    public string SearchText => _search.Text.Trim();
    public Task WhenIdle { get; private set; } = Task.CompletedTask;
    public event Action<PersonalTicket>? TicketRequested;
    public event Action<string>? TicketClosed;
    public event Action<TicketSearchHit>? SearchHitRequested;
    public event Action? FiltersChanged;
    public event Action? FavoriteSectionRequested;
    public event Action? DefaultSectionRequested;
    public event Action<int>? MoveSectionRequested;
    public event Action? ReadingPositionChanged;
    public event Action<bool>? GroupByDayChanged;

    public TicketWorkplaceView(UIElement reader)
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _close = WorkplaceControls.Command("Fermer cet onglet", () => { if (_personal != null) TicketClosed?.Invoke(_personal.Key); });
        DockPanel.SetDock(_close, Dock.Right); bar.Children.Add(_close);
        bar.Children.Add(new ScrollViewer { Content = _ticketTabs, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }); root.Children.Add(bar);
        _readerPage = new TabItem { Header = "Fiche GLPI", Content = reader };
        _personalPage = new TabItem { Header = "Mon travail", Content = Editor };
        var summaryContent = new Grid(); summaryContent.RowDefinitions.Add(new() { Height = GridLength.Auto }); summaryContent.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        summaryContent.Children.Add(_changes); Grid.SetRow(_summary, 1); summaryContent.Children.Add(_summary);
        var summaryPage = new TabItem { Header = "Synthèse du traitement", Content = summaryContent };
        _searchPage = new TabItem { Header = "Chercher partout", Content = BuildSearch() };
        var handoverRoot = new DockPanel();
        var copy = WorkplaceControls.Command("Copier la passation", () => { try { Clipboard.SetText(_handover.Text); } catch (Exception error) { _changes.Text = error.Message; } });
        DockPanel.SetDock(copy, Dock.Top); handoverRoot.Children.Add(copy); handoverRoot.Children.Add(_handover);
        _handoverPage = new TabItem { Header = "Passation", Content = handoverRoot };
        _pages.Items.Add(_readerPage); _pages.Items.Add(summaryPage); _pages.Items.Add(_personalPage); _pages.Items.Add(_searchPage); _pages.Items.Add(_handoverPage);
        _pages.Items.Add(new TabItem { Header = "Rapprochements", Content = Intelligence });
        _pages.Items.Add(new TabItem { Header = "Planification et relations", Content = Insights });
        _pages.Items.Add(new TabItem { Header = "Brouillon de suivi", Content = Drafts });
        _pages.Items.Add(new TabItem { Header = "Actions assistées", Content = Actions });
        _pages.Items.Add(new TabItem { Header = "Enrichissement", Content = Enrichment });
        Drafts.PrepareFollowupRequested += text => { Actions.UseDraft(text); _pages.SelectedItem = _pages.Items.OfType<TabItem>().First(page => ReferenceEquals(page.Content, Actions)); };
        var tools = new StackPanel { Margin = new Thickness(12) };
        tools.Children.Add(WorkplaceControls.Label("Filtres des rubriques de traitement", 16));
        var filter = new WrapPanel(); _type.ItemsSource = new[] { "Tous les types" }; _type.SelectedIndex = 0;
        filter.Children.Add(_type); filter.Children.Add(WorkplaceControls.Label("Auteur")); filter.Children.Add(_author);
        filter.Children.Add(WorkplaceControls.Label("Du")); filter.Children.Add(_from); filter.Children.Add(WorkplaceControls.Label("au")); filter.Children.Add(_to);
        tools.Children.Add(filter); tools.Children.Add(_groupDay);
        tools.Children.Add(WorkplaceControls.Command("Réinitialiser les filtres", () => { _type.SelectedIndex = 0; _author.Clear(); _from.SelectedDate = _to.SelectedDate = null; FiltersChanged?.Invoke(); }));
        tools.Children.Add(WorkplaceControls.Label("Les dates filtrent les événements lisibles de la chronologie. Une date illisible ne passe pas un filtre de période."));
        var sectionActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _favoriteSection = WorkplaceControls.Command("Favori de rubrique : basculer", () => FavoriteSectionRequested?.Invoke());
        _defaultSection = WorkplaceControls.Command("Ouvrir cette rubrique par défaut", () => DefaultSectionRequested?.Invoke());
        sectionActions.Children.Add(_favoriteSection); sectionActions.Children.Add(_defaultSection);
        sectionActions.Children.Add(WorkplaceControls.Command("Monter la rubrique", () => MoveSectionRequested?.Invoke(-1)));
        sectionActions.Children.Add(WorkplaceControls.Command("Descendre la rubrique", () => MoveSectionRequested?.Invoke(1))); tools.Children.Add(sectionActions);
        tools.Children.Add(WorkplaceControls.Label("Sélectionnez d’abord la rubrique dans Fiche GLPI. L’ordre personnalisé est prioritaire ; les favoris sont regroupés parmi les rubriques sans ordre personnalisé."));
        _pages.Items.Add(new TabItem { Header = "Lecture", Content = WorkplaceControls.Viewport(tools) });
        Grid.SetRow(_pages, 1); root.Children.Add(_pages); Content = root;
        _type.SelectionChanged += (_, _) => { if (!_updating) FiltersChanged?.Invoke(); };
        _author.TextChanged += (_, _) => { if (!_updating) FiltersChanged?.Invoke(); };
        _from.SelectedDateChanged += (_, _) => { if (!_updating) FiltersChanged?.Invoke(); };
        _to.SelectedDateChanged += (_, _) => { if (!_updating) FiltersChanged?.Invoke(); };
        _groupDay.Checked += (_, _) => { if (!_updating) { GroupByDayChanged?.Invoke(true); FiltersChanged?.Invoke(); } };
        _groupDay.Unchecked += (_, _) => { if (!_updating) { GroupByDayChanged?.Invoke(false); FiltersChanged?.Invoke(); } };
        _pages.SelectionChanged += (_, e) => { if (!_updating && e.Source == _pages) ReadingPositionChanged?.Invoke(); };
        Editor.HandoverRequested += ShowHandover;
        Intelligence.ReadingFilterChanged += () => { if (!_updating) ReadingPositionChanged?.Invoke(); };
        _search.TextChanged += (_, _) => { if (!_updating) ReadingPositionChanged?.Invoke(); };
        AutomationProperties.SetAutomationId(this, "TicketWorkplace");
    }

    private UIElement BuildSearch()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new DockPanel { Margin = new Thickness(12) };
        var run = WorkplaceControls.Command("Rechercher", Search); DockPanel.SetDock(run, Dock.Right); bar.Children.Add(run); bar.Children.Add(_search); grid.Children.Add(bar);
        _search.KeyDown += (_, args) => { if (args.Key == Key.Enter) { Search(); args.Handled = true; } };
        _searchResults.Columns.Add(new DataGridTextColumn { Header = "Rubrique", Binding = new Binding("Section"), Width = 180 });
        _searchResults.Columns.Add(new DataGridTextColumn { Header = "Texte trouvé", Binding = new Binding("Text"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _searchResults.MouseDoubleClick += (_, args) => { if (args.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(_searchResults, element) is DataGridRow) OpenSearchHit(); };
        _searchResults.KeyDown += (_, args) => { if (args.Key == Key.Enter) { OpenSearchHit(); args.Handled = true; } };
        Grid.SetRow(_searchCount, 1); grid.Children.Add(_searchCount); Grid.SetRow(_searchResults, 2); grid.Children.Add(_searchResults); return grid;
    }

    private void Search() => WhenIdle = SearchAsync();

    private async Task SearchAsync()
    {
        var detail = _detail; var text = _search.Text.Trim(); var generation = ++_searchGeneration;
        if (detail == null) { _searchCount.Text = "Lisez un ticket ou ouvrez une fiche conservée hors connexion."; return; }
        _searchCount.Text = "Recherche dans les sections lues…";
        try
        {
            var found = await Task.Run(() => TicketReadingService.Search(detail, text));
            if (generation != _searchGeneration || !ReferenceEquals(detail, _detail)) return;
            _searchResults.ItemsSource = found;
            _searchCount.Text = $"{found.Count:N0} résultat(s) dans {detail.Sections.Count} sections lues · limite de présentation : 2 000 · double-cliquez pour ouvrir la rubrique.";
        }
        catch (Exception error) { if (generation == _searchGeneration) _searchCount.Text = error.Message; }
    }

    private void OpenSearchHit()
    {
        if (_searchResults.SelectedItem is TicketSearchHit hit) { SearchHitRequested?.Invoke(hit); _pages.SelectedItem = _readerPage; }
    }

    public void Show(WorkplaceDocument document, PersonalTicket? personal, TicketDetailResult? detail, TicketReadChanges? changes = null)
    {
        if (!ReferenceEquals(_detail, detail)) { ++_searchGeneration; _searchResults.ItemsSource = null; _searchCount.Text = "Rechercher dans toutes les sections déjà lues."; }
        var changedTicket = _personal?.Key != personal?.Key || _detail == null && detail != null;
        Insights.Show(detail);
        Actions.ShowTicket(detail?.Ticket);
        Drafts.Show(personal, detail);
        _detail = detail; _personal = personal; _ticketTabs.Children.Clear();
        foreach (var key in document.OpenTicketKeys)
        {
            var ticket = document.Tickets.FirstOrDefault(item => item.Key == key); if (ticket == null) continue;
            var button = WorkplaceControls.Command((ticket.Pinned ? "★ " : "") + ticket.Id, () => TicketRequested?.Invoke(ticket));
            button.ToolTip = ticket.Title + (ticket.Offline ? " · fiche hors connexion disponible" : "");
            button.SetResourceReference(BackgroundProperty, personal?.Key == key ? "SelectedBrush" : "FieldBrush"); _ticketTabs.Children.Add(button);
        }
        _close.IsEnabled = personal != null; _favoriteSection.IsEnabled = _defaultSection.IsEnabled = detail != null;
        Editor.Show(personal, document, detail != null);
        _changes.Text = changes?.Summary ?? "Les différences portent sur les rubriques déjà lues, selon leur empreinte de contenu.";
        if (changes?.HasPrevious == true)
            _changes.Text += (changes.AddedSections.Count > 0 ? "\nNouvelles rubriques lues : " + string.Join(", ", changes.AddedSections) : "")
                + (changes.ChangedSections.Count > 0 ? "\nContenu modifié : " + string.Join(", ", changes.ChangedSections) : "")
                + (changes.MissingSections.Count > 0 ? "\nRubriques non relues : " + string.Join(", ", changes.MissingSections) : "");
        _summary.Text = detail == null ? "La synthèse apparaît après lecture du ticket." : string.Join("\n\n", TicketReadingService.TreatmentSummary(detail).Select(row => row[0] + "\n" + row[1]));
        _handover.Text = personal == null ? "Choisissez un ticket." : TicketReadingService.Handover(personal, detail);
        _updating = true;
        try
        {
            var selectedType = _type.SelectedItem as string;
            var types = new[] { "Tous les types" }.Concat(detail?.Sections.Where(section => section.Kind == "timeline").SelectMany(section => section.Data.Rows)
                .Where(row => row.Length > 0).Select(row => row[0]).Distinct() ?? Array.Empty<string>()).ToArray();
            _type.ItemsSource = types; _type.SelectedItem = types.Contains(selectedType) ? selectedType : types[0];
            _groupDay.IsChecked = document.GroupTimelineByDay;
            if (changedTicket && personal != null)
            {
                var position = personal.ReadingPosition;
                _search.Text = position.WholeTicketSearch; Intelligence.FilterText = position.IntelligenceSearch;
                _type.SelectedItem = types.Contains(position.Type) ? position.Type : types[0];
                _author.Text = position.Author; _from.SelectedDate = position.From; _to.SelectedDate = position.To;
                _pages.SelectedIndex = Math.Clamp(position.PageIndex, 0, _pages.Items.Count - 1);
            }
        }
        finally { _updating = false; }
    }

    public TicketReadingPosition CapturePosition(TicketReadingPosition? previous = null)
    {
        var position = previous?.Copy() ?? new();
        position.Type = _type.SelectedIndex > 0 ? _type.SelectedItem as string ?? "" : "";
        position.Author = _author.Text; position.From = _from.SelectedDate; position.To = _to.SelectedDate;
        position.WholeTicketSearch = _search.Text; position.IntelligenceSearch = Intelligence.FilterText;
        position.PageIndex = Math.Max(0, _pages.SelectedIndex); return position;
    }

    public void ShowPersonal() => _pages.SelectedItem = _personalPage;
    public void ShowHandover() => _pages.SelectedItem = _handoverPage;
    public void ShowEnrichment() => _pages.SelectedItem = _pages.Items.OfType<TabItem>().First(page => ReferenceEquals(page.Content, Enrichment));
    public void ShowReader() => _pages.SelectedItem = _readerPage;
}
