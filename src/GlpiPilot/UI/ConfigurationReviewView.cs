using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class ConfigurationReviewView : UserControl
{
    private readonly Dictionary<ConfigurationParts, CheckBox> _parts = new();
    private readonly CheckBox _merge = new() { Content = "Compléter mes listes sans remplacer les éléments ayant le même identifiant", IsChecked = false, Margin = new Thickness(0, 14, 0, 12) };
    private readonly TextBlock _state = PilotControls.Text("", 12, true);
    public event Action<ConfigurationParts, bool>? ApplyRequested;
    public ConfigurationReviewView(AppSettings current, AppSettings incoming, string title = "Relire la configuration importée")
    {
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
        Content = PilotControls.Page(title, "Aperçu des changements", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 18, 0) }, toolbar);
    }
    public void ShowError(string message) => _state.Text = message;
}
