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
    public event Action<TicketReference>? TicketRequested;

    public TicketIntelligenceView()
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.Children.Add(_summary);
        var actions = new WrapPanel(); actions.Children.Add(WorkplaceControls.Label("Rechercher dans les résultats")); actions.Children.Add(_search);
        Grid.SetRow(actions, 1); root.Children.Add(actions);
        var tabs = new TabControl(); Grid.SetRow(tabs, 2); root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "Tickets rapprochés", Content = Page(_similar, _evidence) });
        tabs.Items.Add(new TabItem { Header = "Solutions collectées", Content = Page(_solutions, _solution) });
        Content = root;
        _search.TextChanged += (_, _) => Filter();
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
    public void Show(TicketIntelligenceResult result) { _result = result; _summary.Text = result.Message; Filter(); }
    public void ShowStatus(string status) { _result = null; _summary.Text = status; _similar.ItemsSource = _solutions.ItemsSource = null; _evidence.Clear(); _solution.Clear(); }
    private void Filter()
    {
        var query = _search.Text.Trim(); var rows = _result?.Related.Where(item => query.Length == 0 || TicketReadingService.Contains(item.Title + " " + item.Id + " " + item.City + " " + item.Category + " " + item.Solution, query)).ToArray() ?? Array.Empty<RelatedTicket>();
        _similar.ItemsSource = rows; _similar.SelectedIndex = rows.Length > 0 ? 0 : -1;
        _solutions.ItemsSource = rows.Where(item => item.HasSolution).ToArray(); _solutions.SelectedIndex = _solutions.Items.Count > 0 ? 0 : -1;
    }
}
