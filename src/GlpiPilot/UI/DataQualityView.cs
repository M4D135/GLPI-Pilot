using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class DataQualityView : UserControl
{
    private readonly TextBlock _summary = PilotControls.Text("Chargez un relevé pour examiner sa qualité.", 13, true);
    private readonly DataGrid _columns = PilotControls.Table();
    private readonly DataGrid _issues = PilotControls.Table();
    private readonly ListBox _missing = new();
    public event Action<int>? RowRequested;
    public event Action? MappingRequested;
    public DataQualityView()
    {
        PilotControls.Column(_columns, "Colonne", "Header", -1); PilotControls.Column(_columns, "Rôle", "Role", 120);
        PilotControls.Column(_columns, "Remplissage", "Coverage", 100); PilotControls.Column(_columns, "Vides", "Empty", 80);
        PilotControls.Column(_columns, "Invalides", "Invalid", 85); PilotControls.Column(_columns, "Valeurs distinctes", "Diversity", 130);
        PilotControls.Column(_columns, "Exemple conservé", "Example", -1);
        PilotControls.Column(_issues, "Point à examiner", "Kind", 175); PilotControls.Column(_issues, "Ticket", "TicketId", 90);
        PilotControls.Column(_issues, "Champ", "Field", 145); PilotControls.Column(_issues, "Explication", "Detail", -1);
        _issues.MouseDoubleClick += (_, _) => { if (_issues.SelectedItem is QualityIssue issue) RowRequested?.Invoke(issue.Row); };
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Colonnes", Content = _columns });
        tabs.Items.Add(new TabItem { Header = "Anomalies", Content = _issues }); tabs.Items.Add(new TabItem { Header = "Champs à associer", Content = _missing });
        var toolbar = new StackPanel(); toolbar.Children.Add(_summary);
        var buttons = new WrapPanel(); buttons.Children.Add(PilotControls.Button("Associer les colonnes", () => MappingRequested?.Invoke())); toolbar.Children.Add(buttons);
        Content = PilotControls.Page("Qualité du relevé", "Complétude de la collecte, couverture des champs et interprétation des valeurs sont évaluées séparément.", tabs, toolbar);
    }
    public void Show(DataQualityReport report)
    { _summary.Text = report.Summary; _columns.ItemsSource = report.Columns; _issues.ItemsSource = report.Issues; _missing.ItemsSource = report.UnavailableRoles; }
    public void ShowStatus(string text) => _summary.Text = text;
}
