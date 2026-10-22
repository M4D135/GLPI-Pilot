using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class TicketInsightsView : UserControl
{
    private readonly TextBlock _coverage = WorkplaceControls.Label("Lisez les rubriques GLPI pour observer les tâches, validations, documents et relations.");
    private readonly DataGrid _tasks = Grid(("Tâche", "Title", 300), ("Responsable", "Owner", 180), ("Échéance", "Due", 160), ("Début prévu", "Start", 160), ("Fin prévue", "End", 160), ("Achevée", "Completed", 90), ("En retard", "Overdue", 90), ("Lu le", "ObservedAt", 165), ("Preuve", "Evidence", 300));
    private readonly DataGrid _validations = Grid(("Validation", "Title", 300), ("Responsable", "Responsible", 180), ("Statut lu", "Status", 140), ("Demandée le", "RequestedAt", 160), ("Ancienneté · j", "AgeDays", 140), ("En attente", "Pending", 100), ("Lu le", "ObservedAt", 165), ("Preuve", "Evidence", 300));
    private readonly DataGrid _waits = Grid(("Motif", "Reason", 250), ("Début", "Start", 160), ("Fin", "End", 160), ("Heures observées", "Hours", 150), ("En cours", "Ongoing", 100), ("Lu le", "ObservedAt", 165), ("Preuve", "Evidence", 400));
    private readonly DataGrid _relations = Grid(("Type", "RelatedType", 150), ("Identifiant", "Identity", 100), ("Libellé", "Title", 350), ("Rubrique source", "SourceSection", 200));
    private readonly DataGrid _documents = Grid(("Document", "Title", 350), ("Identifiant", "Identity", 100), ("Rubrique source", "SourceSection", 200));
    private readonly TextBlock _exchange = WorkplaceControls.Label("");
    public event Action<TicketObservedItem>? RelationRequested;
    public event Action<TicketObservedItem>? DocumentRequested;

    public TicketInsightsView()
    {
        var root = new DockPanel(); DockPanel.SetDock(_coverage, Dock.Top); root.Children.Add(_coverage);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Tâches planifiées", Content = _tasks });
        tabs.Items.Add(new TabItem { Header = "Validations", Content = _validations });
        tabs.Items.Add(new TabItem { Header = "Mises en attente", Content = _waits });
        tabs.Items.Add(new TabItem { Header = "Dernier échange", Content = WorkplaceControls.Viewport(_exchange) });
        tabs.Items.Add(new TabItem { Header = "Relations", Content = WithCommand(_relations, "Ouvrir la relation sélectionnée", () => { if (_relations.SelectedItem is TicketObservedItem item) RelationRequested?.Invoke(item); }) });
        tabs.Items.Add(new TabItem { Header = "Documents", Content = WithCommand(_documents, "Télécharger le document sélectionné", () => { if (_documents.SelectedItem is TicketObservedItem item) DocumentRequested?.Invoke(item); }) });
        root.Children.Add(tabs); Content = root;
    }

    public void Show(TicketDetailResult? detail)
    {
        if (detail == null) { _tasks.ItemsSource = _validations.ItemsSource = _waits.ItemsSource = _relations.ItemsSource = _documents.ItemsSource = null; _exchange.Text = "Aucune lecture disponible."; return; }
        var report = TicketInsightService.Analyze(detail, DateTimeOffset.Now);
        _coverage.Text = report.Coverage + $"\n{report.OverdueTasks} tâches en retard confirmées · {report.PendingValidations} validations en attente confirmées. « — » signifie non déterminé.";
        _tasks.ItemsSource = report.Tasks; _validations.ItemsSource = report.Validations; _waits.ItemsSource = report.Waits;
        _relations.ItemsSource = detail.ObservedItems.Where(i => i.Kind == "relation").DistinctBy(i => i.Url).ToArray();
        _documents.ItemsSource = detail.ObservedItems.Where(i => i.Kind == "document").DistinctBy(i => i.Url).ToArray();
        _exchange.Text = report.LastExchange is { } exchange ? $"{exchange.Date:dd/MM/yyyy HH:mm} · {exchange.Author} · {exchange.Role}\n\n{exchange.Content}\n\n{exchange.WaitingFor}\nLe message ne prouve pas qu’une réponse est contractuellement attendue." : "Aucun suivi daté collecté. Lisez la rubrique Traitement pour compléter cette vue.";
    }
    private static UIElement WithCommand(DataGrid grid, string title, Action action)
    {
        var root = new DockPanel(); var button = WorkplaceControls.Command(title, action); DockPanel.SetDock(button, Dock.Top); root.Children.Add(button); root.Children.Add(grid); return root;
    }
    private static DataGrid Grid(params (string Title, string Property, double Width)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
        foreach (var c in columns) grid.Columns.Add(new DataGridTextColumn { Header = c.Title, Binding = new Binding(c.Property) { TargetNullValue = "—" }, Width = c.Width });
        return grid;
    }
}
