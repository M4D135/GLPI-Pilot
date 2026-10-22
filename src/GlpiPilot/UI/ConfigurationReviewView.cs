using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class ConfigurationReviewView : UserControl
{
    private readonly Dictionary<ConfigurationParts, CheckBox> _parts = new();
    private readonly CheckBox _merge = new() { Content = "Fusionner les listes en appliquant mes choix de conflit", IsChecked = false, Margin = new Thickness(0, 14, 0, 12) };
    private readonly TextBlock _state = PilotControls.Text("", 12, true);
    private readonly DataGrid _conflictGrid = PilotControls.Table(editable: true);
    private readonly List<ConfigurationConflict> _conflicts;
    public IReadOnlyDictionary<string, string> Decisions { get { _conflictGrid.CommitEdit(DataGridEditingUnit.Cell, true); _conflictGrid.CommitEdit(DataGridEditingUnit.Row, true); return _conflicts.ToDictionary(c => c.Key, c => c.Choice, StringComparer.Ordinal); } }
    public event Action<ConfigurationParts, bool>? ApplyRequested;
    public ConfigurationReviewView(AppSettings current, AppSettings incoming, string title = "Relire la configuration importée")
    {
        _conflicts = ConfigurationReview.Conflicts(current, incoming).ToList();
        var panel = new StackPanel();
        panel.Children.Add(PilotControls.Text("Choisissez les sections à reprendre. L’identité personnelle, les notes, les sessions SSO, la programmation et les chemins du poste sont conservés. Les anciens réglages sont archivés avant l’application.", 13, true));
        foreach (var difference in ConfigurationReview.Describe(current, incoming))
        {
            var card = new StackPanel(); var toggle = new CheckBox { Content = difference.Section, IsChecked = true, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            _parts[difference.Part] = toggle; card.Children.Add(toggle);
            card.Children.Add(PilotControls.Text("Actuel : " + difference.Current, 12, true));
            card.Children.Add(PilotControls.Text("À importer : " + difference.Incoming, 12));
            var border = PilotControls.Card(card); border.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(border);
        }
        panel.Children.Add(_merge); panel.Children.Add(_state);
        var toolbar = new WrapPanel();
        toolbar.Children.Add(PilotControls.Button("Appliquer les sections cochées", () =>
        {
            var parts = _parts.Where(pair => pair.Value.IsChecked == true).Aggregate(ConfigurationParts.None, (value, pair) => value | pair.Key);
            if (parts == ConfigurationParts.None) { _state.Text = "Cochez au moins une section à importer."; return; }
            ApplyRequested?.Invoke(parts, _merge.IsChecked == true);
        }));
        _conflictGrid.ItemsSource = _conflicts; PilotControls.Column(_conflictGrid, "Section", "Section", 150); PilotControls.Column(_conflictGrid, "Élément", "Name", -1);
        _conflictGrid.Columns[0].IsReadOnly = _conflictGrid.Columns[1].IsReadOnly = true;
        _conflictGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Arbitrage lors de la fusion", SelectedValueBinding = new Binding("Choice"), SelectedValuePath = "Key", DisplayMemberPath = "Label", Width = 240, ItemsSource = new[] { new QueryChoice("current", "Conserver l’actuel"), new QueryChoice("incoming", "Prendre l’importé"), new QueryChoice("both", "Conserver les deux variantes") } });
        var differences = PilotControls.Table(); PilotControls.Column(differences, "Champ", "Path", 280); PilotControls.Column(differences, "Changement", "Change", 115); PilotControls.Column(differences, "Actuel", "Current", -1); PilotControls.Column(differences, "À appliquer", "Incoming", -1);
        var detailText = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        differences.SelectionChanged += (_, _) => { if (differences.SelectedItem is ConfigurationFieldDifference diff) detailText.Text = diff.Path + "\nACTUEL\n" + diff.Current + "\nÀ APPLIQUER\n" + diff.Incoming; };
        var detailPanel = new DockPanel(); DockPanel.SetDock(detailText, Dock.Bottom); detailPanel.Children.Add(detailText); detailPanel.Children.Add(differences);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Sections à reprendre", Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 18, 0) } });
        tabs.Items.Add(new TabItem { Header = "Conflits et variantes", Content = _conflictGrid }); tabs.Items.Add(new TabItem { Header = "Ajouts, modifications et suppressions", Content = detailPanel });
        void Refresh()
        {
            try { var parts = _parts.Where(pair => pair.Value.IsChecked == true).Aggregate(ConfigurationParts.None, (value, pair) => value | pair.Key); var proposed = ConfigurationReview.Apply(current, incoming, parts, _merge.IsChecked == true, Decisions); var changes = ConfigurationReview.DescribeFields(current, proposed); differences.ItemsSource = changes; _state.Text = changes.Count + " champ(s) modifié(s). Le détail complet est disponible dans le troisième onglet."; }
            catch (Exception error) { _state.Text = AppPaths.SafeError(error); differences.ItemsSource = null; }
        }
        toolbar.Children.Insert(0, PilotControls.Button("Actualiser l’aperçu détaillé", () => { Refresh(); tabs.SelectedIndex = 2; }));
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs && tabs.SelectedIndex == 2) Refresh(); };
        Refresh();
        Content = PilotControls.Page(title, "Choisissez les sections, arbitrez les conflits puis relisez les changements champ par champ.", tabs, toolbar);
    }
    public void ShowError(string message) => _state.Text = message;
}
