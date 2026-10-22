using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class TicketDraftView : UserControl
{
    private readonly ComboBox _template = new() { Width = 300, ItemsSource = new[] { "Compte rendu d’intervention", "Diagnostic et prochaine action", "Demande de complément", "Passation technique" }, SelectedIndex = 0 };
    private readonly TextBox _findings = WorkplaceControls.Editor(75, 4000), _action = WorkplaceControls.Editor(75, 4000), _next = WorkplaceControls.Editor(75, 4000);
    private readonly ListBox _context = new() { SelectionMode = SelectionMode.Multiple, MaxHeight = 180, DisplayMemberPath = "Label" };
    private readonly TextBox _draft = WorkplaceControls.Editor(240, 20000);
    private readonly TextBlock _state = WorkplaceControls.Label("Le brouillon est personnel. Sa publication exige une action assistée et une confirmation distinctes.");
    private PersonalTicket? _personal; private TicketDetailResult? _detail; private bool _updating;
    private readonly Dictionary<string, Composition> _compositions = new(StringComparer.Ordinal);
    private sealed record Composition(string Draft, string Findings, string Action, string Next, int Template);
    public event Action<string, string>? DraftChanged;
    public event Action<string>? PrepareFollowupRequested;
    public TicketDraftView()
    {
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(WorkplaceControls.Label("Brouillon de suivi structuré", 18)); root.Children.Add(_state); root.Children.Add(_template);
        root.Children.Add(WorkplaceControls.Label("Constat observé")); root.Children.Add(_findings);
        root.Children.Add(WorkplaceControls.Label("Action réalisée ou prévue")); root.Children.Add(_action);
        root.Children.Add(WorkplaceControls.Label("Suite attendue / prochaine vérification")); root.Children.Add(_next);
        root.Children.Add(WorkplaceControls.Label("Contexte à inclure · sélection multiple facultative")); root.Children.Add(_context);
        root.Children.Add(WorkplaceControls.Command("Composer le brouillon", Compose)); root.Children.Add(_draft);
        var actions = new WrapPanel();
        actions.Children.Add(WorkplaceControls.Command("Copier le brouillon", () => { try { Clipboard.SetText(_draft.Text); _state.Text = "Brouillon copié ; vous pouvez le relire et le coller dans GLPI."; } catch (Exception e) { _state.Text = e.Message; } }));
        actions.Children.Add(WorkplaceControls.Command("Préparer l’ajout assisté d’un suivi", () => PrepareFollowupRequested?.Invoke(_draft.Text)));
        root.Children.Add(actions); Content = WorkplaceControls.Viewport(root);
        _draft.TextChanged += (_, _) => { if (!_updating && _personal != null) { DraftChanged?.Invoke(_personal.Key, _draft.Text); _state.Text = "Brouillon personnel en cours d’enregistrement sur ce poste."; } };
    }
    private void Compose()
    {
        if (_personal == null) return;
        if (_draft.Text.Length > 0 && MessageBox.Show("Remplacer le texte du brouillon par la composition des champs affichés ?", "Composer le suivi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var context = _context.SelectedItems.OfType<ContextLine>().Select(line => line.Label);
        _draft.Text = TicketInsightService.DraftFollowup(_personal, _detail, _template.SelectedItem as string ?? "Suivi", _findings.Text, _action.Text, _next.Text)
            + (context.Any() ? "\n\nContexte sélectionné\n" + string.Join("\n", context) : "");
    }
    public void Show(PersonalTicket? personal, TicketDetailResult? detail)
    {
        var changed = _personal?.Key != personal?.Key;
        if (changed && _personal != null) _compositions[_personal.Key] = new(_draft.Text, _findings.Text, _action.Text, _next.Text, _template.SelectedIndex);
        _personal = personal; _detail = detail;
        _context.ItemsSource = detail == null ? Array.Empty<ContextLine>() : TicketReadingService.TreatmentSummary(detail).Select(row => new ContextLine(row[0] + " : " + row[1])).ToArray();
        if (!changed) return;
        _updating = true;
        try
        {
            var previous = personal != null ? _compositions.GetValueOrDefault(personal.Key) : null;
            _draft.Text = previous?.Draft ?? personal?.FollowupDraft ?? ""; _findings.Text = previous?.Findings ?? ""; _action.Text = previous?.Action ?? ""; _next.Text = previous?.Next ?? "";
            _template.SelectedIndex = previous?.Template ?? 0;
        }
        finally { _updating = false; }
        _state.Text = "Brouillon personnel restauré. Le texte est mémorisé après modification ; aucune publication automatique.";
    }
    public void Saved(bool success) => _state.Text = success ? "Brouillon personnel enregistré sur ce poste." : "Enregistrement impossible. Le texte reste affiché ; copiez-le avant de changer de fiche.";
    private sealed record ContextLine(string Label);
}
