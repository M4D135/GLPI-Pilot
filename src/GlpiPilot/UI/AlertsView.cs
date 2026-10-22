using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class AlertsView : UserControl
{
    private readonly DataGrid _incidents = PilotControls.Table(), _rules = PilotControls.Table(editable: true);
    private readonly ObservableCollection<AlertRule> _drafts = new();
    private readonly DataGridComboBoxColumn _metric;
    private readonly TextBlock _state = PilotControls.Text("Les alertes sont évaluées sur le relevé global à chaque nouvelle acquisition. Elles restent locales à ce poste.", 12, true);
    private readonly TextBlock _collectionHealth = PilotControls.Text("L’état des collectes sera affiché après lecture des sources suivies.", 14, true);
    public event Action<IReadOnlyList<AlertRule>>? SaveRequested;
    public event Action<string>? AcknowledgeRequested;
    public event Action<string, string>? ResolveRequested;
    public event Action<string>? SnoozeRequested;
    public event Action<AlertIncident>? RowsRequested;
    public AlertsView()
    {
        PilotControls.Column(_incidents, "État", "State", 130); PilotControls.Column(_incidents, "Alerte", "Name", 180); PilotControls.Column(_incidents, "Valeur", "DisplayValue", 115);
        PilotControls.Column(_incidents, "Premier constat", "FirstObserved", 150, "{0:dd/MM/yyyy HH:mm}"); PilotControls.Column(_incidents, "Dernier constat", "LastObserved", 150, "{0:dd/MM/yyyy HH:mm}");
        PilotControls.Column(_incidents, "Nouveaux contributeurs", "ContributorsLabel", 160);
        PilotControls.Column(_incidents, "Constats", "Occurrences", 75); PilotControls.Column(_incidents, "Détail", "Detail", -1);
        _incidents.MouseDoubleClick += (_, _) => { if (_incidents.SelectedItem is AlertIncident incident) RowsRequested?.Invoke(incident); };
        var actions = new WrapPanel(); actions.Children.Add(PilotControls.Button("Voir les lignes concernées", () => { if (_incidents.SelectedItem is AlertIncident incident) RowsRequested?.Invoke(incident); }));
        actions.Children.Add(PilotControls.Button("Pris en compte", () => { if (_incidents.SelectedItem is AlertIncident incident) AcknowledgeRequested?.Invoke(incident.Id); }));
        actions.Children.Add(PilotControls.Button("Rappeler dans une heure", () => { if (_incidents.SelectedItem is AlertIncident incident) SnoozeRequested?.Invoke(incident.Id); }));
        var resolution = PilotControls.Input(""); resolution.Width = 210; resolution.ToolTip = "Motif de la résolution locale"; actions.Children.Add(resolution);
        actions.Children.Add(PilotControls.Button("Résoudre avec motif", () => { if (_incidents.SelectedItem is AlertIncident incident) ResolveRequested?.Invoke(incident.Id, resolution.Text); }));
        var history = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _incidents.SelectionChanged += (_, _) => { if (_incidents.SelectedItem is AlertIncident incident) history.Text = "Nouveaux identifiants : " + string.Join(", ", incident.NewContributorIds) + (incident.NewContributorIds.Count == 500 ? " · affichage limité à 500" : "") + "\n" + string.Join("\n", incident.History.Select(h => $"{h.At.ToLocalTime():dd/MM HH:mm} · {h.Action} · {h.Reason}")); };
        var incidentsPanel = new DockPanel(); DockPanel.SetDock(history, Dock.Bottom); incidentsPanel.Children.Add(history); DockPanel.SetDock(actions, Dock.Top); incidentsPanel.Children.Add(actions); incidentsPanel.Children.Add(_incidents);
        _rules.ItemsSource = _drafts; _rules.Columns.Add(new DataGridCheckBoxColumn { Header = "Active", Binding = new Binding("Enabled"), Width = 65 });
        PilotControls.Column(_rules, "Nom", "Name", 180);
        _metric = new DataGridComboBoxColumn { Header = "Indicateur ou requête", SelectedValueBinding = new Binding("MetricKey"), SelectedValuePath = "Key", DisplayMemberPath = "Label", Width = new DataGridLength(1, DataGridLengthUnitType.Star) }; _rules.Columns.Add(_metric);
        _rules.Columns.Add(new DataGridComboBoxColumn { Header = "Condition", SelectedValueBinding = new Binding("Operator"), SelectedValuePath = "Key", DisplayMemberPath = "Label", ItemsSource = new[] { new QueryChoice("gt", ">"), new QueryChoice("gte", "≥"), new QueryChoice("lt", "<"), new QueryChoice("lte", "≤") }, Width = 90 });
        _rules.Columns.Add(new DataGridComboBoxColumn { Header = "Évaluation", SelectedItemBinding = new Binding("EvaluationMode"), ItemsSource = new[] { "value", "delta", "percent" }, Width = 105 });
        PilotControls.Column(_rules, "Seuil", "Threshold", 80); PilotControls.Column(_rules, "Échantillon min.", "MinimumSample", 110);
        _rules.Columns.Add(new DataGridCheckBoxColumn { Header = "Exiger un relevé complet et confirmé", Binding = new Binding("RequireComplete"), Width = 150 });
        var ruleActions = new WrapPanel(); ruleActions.Children.Add(PilotControls.Button("Ajouter une alerte", () => { if (_drafts.Count < 50) _drafts.Add(new()); }));
        ruleActions.Children.Add(PilotControls.Button("Retirer", () => { if (_rules.SelectedItem is AlertRule rule) _drafts.Remove(rule); }));
        ruleActions.Children.Add(PilotControls.Button("Enregistrer les règles", () => { _rules.CommitEdit(DataGridEditingUnit.Cell, true); _rules.CommitEdit(DataGridEditingUnit.Row, true); SaveRequested?.Invoke(_drafts.Select(rule => rule.Copy()).ToArray()); }));
        var rulePanel = new DockPanel(); DockPanel.SetDock(ruleActions, Dock.Top); rulePanel.Children.Add(ruleActions); rulePanel.Children.Add(_rules);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "À suivre et historique", Content = incidentsPanel }); tabs.Items.Add(new TabItem { Header = "Mes règles", Content = rulePanel });
        tabs.Items.Add(new TabItem { Header = "Santé des collectes", Content = new ScrollViewer { Content = _collectionHealth, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) } });
        var body = new DockPanel(); DockPanel.SetDock(_state, Dock.Top); _state.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(_state); body.Children.Add(tabs);
        Content = PilotControls.Page("Alertes du relevé", "Des conditions explicites sur vos indicateurs, y compris ceux de l’atelier de requêtes.", body);
    }
    public void Load(AlertDocument document) { _drafts.Clear(); foreach (var rule in document.Rules) _drafts.Add(rule.Copy()); Show(document); }
    public void Show(AlertDocument document) => _incidents.ItemsSource = document.Incidents.OrderByDescending(incident => incident.Active).ThenByDescending(incident => incident.LastObserved).ToArray();
    public void SetMetrics(DashboardSnapshot snapshot) => _metric.ItemsSource = snapshot.Metrics.Select(metric => new QueryChoice(metric.Key, metric.Label + " · " + metric.Unit)).OrderBy(choice => choice.Label).ToArray();
    public void ShowStatus(string text) => _state.Text = text;
    public void ShowCollectionHealth(string text) => _collectionHealth.Text = text;
}
