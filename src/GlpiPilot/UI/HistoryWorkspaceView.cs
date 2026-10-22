using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class HistoryWorkspaceView : UserControl
{
    private readonly DataGrid _catalogue = PilotControls.Table();
    private readonly DataGrid _changes = PilotControls.Table();
    private readonly ComboBox _before = new() { DisplayMemberPath = "Label", MinWidth = 240, MaxWidth = 450, Margin = new Thickness(0, 0, 8, 8) };
    private readonly ComboBox _after = new() { DisplayMemberPath = "Label", MinWidth = 240, MaxWidth = 450, Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBlock _summary = PilotControls.Text("Sélectionnez deux relevés pour examiner leur évolution.", 12, true);
    private readonly TextBox _search = PilotControls.Input("", 230);
    private readonly TabControl _tabs = new();
    private IReadOnlyList<SnapshotChange> _allChanges = Array.Empty<SnapshotChange>();
    private string _comparisonBeforeId = "";
    public event Action<string, int[]>? VerifyAbsentRequested;
    public event Action? RefreshRequested;
    public event Action<string>? OpenRequested;
    public event Action<string>? ExportRequested;
    public event Action<string, bool>? ProtectRequested;
    public event Action<string, string>? CompareRequested;
    public event Action<SnapshotChange>? ChangeRequested;

    public HistoryWorkspaceView()
    {
        PilotControls.Column(_catalogue, "Relevé", "CapturedAt", 170, "{0:dd/MM/yyyy HH:mm:ss}");
        PilotControls.Column(_catalogue, "Source", "SourceLabel", -1);
        PilotControls.Column(_catalogue, "Lignes", "Rows", 85, "{0:N0}");
        PilotControls.Column(_catalogue, "État", "State", 125);
        PilotControls.Column(_catalogue, "Méthode", "Method", 110);
        _catalogue.Columns.Add(new DataGridCheckBoxColumn { Header = "Conserver", Binding = new Binding("Protected"), Width = 90 });
        var actions = new WrapPanel();
        actions.Children.Add(PilotControls.Button("Actualiser", () => RefreshRequested?.Invoke()));
        actions.Children.Add(PilotControls.Button("Ouvrir ce relevé", () => WithSelected(item => OpenRequested?.Invoke(item.Id))));
        actions.Children.Add(PilotControls.Button("Exporter", () => WithSelected(item => ExportRequested?.Invoke(item.Id))));
        actions.Children.Add(PilotControls.Button("Conserver / libérer", () => WithSelected(item => ProtectRequested?.Invoke(item.Id, !item.Protected)), "Protéger ce relevé contre la politique de rétention, ou retirer sa protection."));
        _catalogue.MouseDoubleClick += (_, _) => WithSelected(item => OpenRequested?.Invoke(item.Id));
        var cataloguePanel = new DockPanel(); DockPanel.SetDock(actions, Dock.Top); cataloguePanel.Children.Add(actions); cataloguePanel.Children.Add(_catalogue);
        _tabs.Items.Add(new TabItem { Header = "Relevés conservés", Content = cataloguePanel });

        var compareActions = new WrapPanel(); compareActions.Children.Add(_before); compareActions.Children.Add(_after);
        compareActions.Children.Add(PilotControls.Button("Comparer", () =>
        {
            if (_before.SelectedItem is SnapshotMetadata before && _after.SelectedItem is SnapshotMetadata after) { _comparisonBeforeId = before.Id; CompareRequested?.Invoke(before.Id, after.Id); }
        }));
        compareActions.Children.Add(PilotControls.Button("Vérifier les absents dans GLPI", () =>
        {
            var rows = _allChanges.Where(c => c.Kind == "Absent du relevé" && c.PreviousRow.HasValue).Select(c => c.PreviousRow!.Value).Distinct().ToArray();
            if (_comparisonBeforeId.Length > 0 && rows.Length > 0) VerifyAbsentRequested?.Invoke(_comparisonBeforeId, rows);
            else _summary.Text = "Comparez deux relevés et vérifiez qu’ils contiennent des absents. Les absences gardent leur statut historique inconnu.";
        }));
        _search.ToolTip = "Filtrer toutes les différences par identifiant, champ ou valeur.";
        _search.TextChanged += (_, _) => Filter(); compareActions.Children.Add(_search);
        PilotControls.Column(_changes, "Évolution", "Kind", 140); PilotControls.Column(_changes, "Ticket", "TicketId", 85);
        PilotControls.Column(_changes, "Champ", "Field", 160); PilotControls.Column(_changes, "Avant", "Before", -1); PilotControls.Column(_changes, "Après", "After", -1);
        _changes.MouseDoubleClick += (_, _) => { if (_changes.SelectedItem is SnapshotChange change) ChangeRequested?.Invoke(change); };
        var comparePanel = new DockPanel(); DockPanel.SetDock(compareActions, Dock.Top); DockPanel.SetDock(_summary, Dock.Top);
        comparePanel.Children.Add(compareActions); comparePanel.Children.Add(_summary); comparePanel.Children.Add(_changes);
        _tabs.Items.Add(new TabItem { Header = "Comparer", Content = comparePanel });
        _tabs.Items.Add(new TabItem { Header = "Analyses historiques", Content = BuildAnalyticsWorkspace() });
        _tabs.Items.Add(new TabItem { Header = "Historique d’un ticket", Content = BuildIndexedTicketHistory() });
        _tabs.Items.Add(new TabItem { Header = "Effectifs des sites", Content = BuildPopulations() });
        Content = PilotControls.Page("Historique des relevés", "Retrouvez les données telles qu’elles ont été collectées. Une absence du relevé ne signifie pas une clôture du ticket.", _tabs);
    }
    public void Show(IReadOnlyList<SnapshotMetadata> entries)
    {
        var beforeId = (_before.SelectedItem as SnapshotMetadata)?.Id; var afterId = (_after.SelectedItem as SnapshotMetadata)?.Id;
        var analysisId = (_analysisSource.SelectedItem as SnapshotMetadata)?.Id;
        _analysisSource.ItemsSource = entries;
        _analysisSource.SelectedItem = entries.FirstOrDefault(e => e.Id == analysisId) ?? entries.FirstOrDefault();
        _catalogue.ItemsSource = entries; _before.ItemsSource = entries; _after.ItemsSource = entries;
        _before.SelectedItem = entries.FirstOrDefault(item => item.Id == beforeId) ?? entries.Skip(1).FirstOrDefault();
        _after.SelectedItem = entries.FirstOrDefault(item => item.Id == afterId) ?? entries.FirstOrDefault();
    }
    public void ShowComparison(SnapshotComparison comparison)
    {
        _summary.Text = comparison.Summary + (comparison.Compatible ? $"\n{comparison.ResolutionsObserved:N0} résolutions observées · {comparison.ReopeningsObserved:N0} réouvertures observées · {comparison.ReassignmentsObserved:N0} réaffectations observées" : "")
            + "\n" + string.Join("\n", comparison.Warnings)
            + (comparison.AddedColumns.Count == 0 ? "" : "\nColonnes ajoutées : " + string.Join(", ", comparison.AddedColumns))
            + (comparison.RemovedColumns.Count == 0 ? "" : "\nColonnes retirées : " + string.Join(", ", comparison.RemovedColumns));
        _allChanges = comparison.Changes; Filter(); _tabs.SelectedIndex = 1;
    }
    public void ShowStatus(string text) => _summary.Text = text;
    private void WithSelected(Action<SnapshotMetadata> action) { if (_catalogue.SelectedItem is SnapshotMetadata item) action(item); }
    private void Filter()
    {
        var text = _search.Text.Trim();
        _changes.ItemsSource = text.Length == 0 ? _allChanges : _allChanges.Where(change => (change.TicketId + " " + change.Field + " " + change.Before + " " + change.After + " " + change.Kind).Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
