using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class TicketIntelligenceView : UserControl
{
    private readonly TextBlock _summary = WorkplaceControls.Label("Ouvrez une fiche et son relevé pour rechercher les rapprochements locaux.");
    private readonly DataGrid _similar = CreateGrid(), _solutions = CreateGrid();
    private readonly TextBox _evidence = WorkplaceControls.Editor(double.NaN, int.MaxValue, true);
    private readonly TextBox _solution = WorkplaceControls.Editor(double.NaN, int.MaxValue, true);
    private readonly TextBox _search = new() { MinWidth = 220, MaxLength = 300 };
    private TicketIntelligenceResult? _result;
    private readonly ComboBox _equipmentColumn = new() { Width = 240 };
    private readonly TextBlock _recurrenceSummary = WorkplaceControls.Label("Chargez jusqu’à 20 relevés locaux du même périmètre pour compter les dossiers distincts par titre ou équipement.");
    private readonly DataGrid _recurrences = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true };
    public event Action<TicketReference>? TicketRequested;
    public event Action<string>? RecurrencesRequested;
    public event Action? ReadingFilterChanged;
    public string FilterText { get => _search.Text; set => _search.Text = value; }

    public TicketIntelligenceView()
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.Children.Add(_summary);
        var actions = new WrapPanel(); actions.Children.Add(WorkplaceControls.Label("Rechercher dans les résultats")); actions.Children.Add(_search);
        Grid.SetRow(actions, 1); root.Children.Add(actions);
        var tabs = new TabControl(); Grid.SetRow(tabs, 2); root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "Tickets rapprochés", Content = Page(_similar, _evidence) });
        tabs.Items.Add(new TabItem { Header = "Solutions collectées", Content = Page(_solutions, _solution) });
        var recurrenceRoot = new DockPanel(); var recurrenceTop = new StackPanel(); recurrenceTop.Children.Add(_recurrenceSummary);
        var recurrenceActions = new WrapPanel(); recurrenceActions.Children.Add(WorkplaceControls.Label("Colonne équipement")); recurrenceActions.Children.Add(_equipmentColumn);
        recurrenceActions.Children.Add(WorkplaceControls.Command("Analyser les relevés conservés", () => RecurrencesRequested?.Invoke(_equipmentColumn.SelectedIndex > 0 ? _equipmentColumn.SelectedItem as string ?? "" : "")));
        recurrenceActions.Children.Add(WorkplaceControls.Command("Lire le dossier", () => { if (_recurrences.SelectedItem is TicketRecurrenceIncident row) TicketRequested?.Invoke(row.Ticket); })); recurrenceTop.Children.Add(recurrenceActions);
        DockPanel.SetDock(recurrenceTop, Dock.Top); recurrenceRoot.Children.Add(recurrenceTop); recurrenceRoot.Children.Add(_recurrences);
        foreach (var (label, field, width) in new[] { ("Ticket", "Id", 80d), ("Titre", "Title", 280d), ("Équipement", "Equipment", 160d), ("Catégorie", "Category", 140d), ("Première observation", "FirstSeen", 170d), ("Dernière observation", "LastSeen", 170d), ("Création lue", "CreatedAt", 170d), ("Relevés", "Observations", 70d), ("Preuves du rapprochement", "Evidence", 450d) })
            _recurrences.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(field), Width = width });
        tabs.Items.Add(new TabItem { Header = "Récurrences multi-relevés", Content = recurrenceRoot });
        Content = root;
        _search.TextChanged += (_, _) => { Filter(); ReadingFilterChanged?.Invoke(); };
        _similar.SelectionChanged += (_, _) => _evidence.Text = _similar.SelectedItem is RelatedTicket row ? row.Evidence + "\n\nSource : " + row.Ticket.Url + "\nRelevé : " + _result?.ExtractedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm") : "Sélectionnez un ticket pour lire les critères du rapprochement.";
        _solutions.SelectionChanged += (_, _) => _solution.Text = _solutions.SelectedItem is RelatedTicket row ? row.Solution
            + "\n\nSource : " + row.Ticket.Url + "\n" + row.SolutionSource + "\nLecture : " + row.ObservedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm")
            + "\nStatut dans le relevé : " + row.Status + "\nCette solution est proposée à la lecture ; sa pertinence pour le ticket courant reste à vérifier." : "Aucun texte de solution collecté pour cette sélection.";
    }
    private UIElement Page(DataGrid grid, TextBox text)
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = new GridLength(2, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.Children.Add(grid);
        var open = WorkplaceControls.Command("Lire ce ticket dans GLPI", () => { if (grid.SelectedItem is RelatedTicket ticket) TicketRequested?.Invoke(ticket.Ticket); }); Grid.SetRow(open, 1); root.Children.Add(open); Grid.SetRow(text, 2); root.Children.Add(text); return root;
    }
    private static DataGrid CreateGrid()
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (header, field, width) in new[] { ("Ticket", "Id", 80d), ("Titre", "Title", 300d), ("Rapprochement", "Relationship", 155d), ("Ville", "City", 120d), ("Catégorie", "Category", 140d), ("Statut", "Status", 140d), ("Score local", "Score", 85d) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(field), Width = width });
        return grid;
    }
    public void SetRecurrenceColumns(IEnumerable<string> columns)
    {
        var selected = _equipmentColumn.SelectedItem as string; var names = new[] { "Détection par en-tête explicite" }.Concat(columns).ToArray();
        if ((_equipmentColumn.ItemsSource as IEnumerable<string>)?.SequenceEqual(names) == true) return;
        _equipmentColumn.ItemsSource = names; _equipmentColumn.SelectedItem = selected != null && names.Contains(selected) ? selected : names[0];
    }
    public void ShowRecurrences(TicketRecurrenceReport report) { _recurrenceSummary.Text = report.Summary; _recurrences.ItemsSource = report.Incidents; }
    public void ShowRecurrenceStatus(string text) { _recurrenceSummary.Text = text; _recurrences.ItemsSource = null; }
    public void Show(TicketIntelligenceResult result) { _result = result; _summary.Text = result.Message; Filter(); }
    public void ShowStatus(string status) { _result = null; _summary.Text = status; _similar.ItemsSource = _solutions.ItemsSource = null; _evidence.Clear(); _solution.Clear(); }
    private void Filter()
    {
        var query = _search.Text.Trim(); var rows = _result?.Related.Where(item => query.Length == 0 || TicketReadingService.Contains(item.Title + " " + item.Id + " " + item.City + " " + item.Category + " " + item.Solution, query)).ToArray() ?? Array.Empty<RelatedTicket>();
        _similar.ItemsSource = rows; _similar.SelectedIndex = rows.Length > 0 ? 0 : -1;
        _solutions.ItemsSource = rows.Where(item => item.HasSolution).ToArray(); _solutions.SelectedIndex = _solutions.Items.Count > 0 ? 0 : -1;
    }
}
