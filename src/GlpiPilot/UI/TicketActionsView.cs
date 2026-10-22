using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class TicketActionsView : UserControl
{
    private readonly ComboBox _adapter = new() { MinWidth = 320, DisplayMemberPath = "Name" };
    private readonly ComboBox _choice = new() { MinWidth = 320, DisplayMemberPath = "Label", Visibility = Visibility.Collapsed };
    private readonly TextBox _value = WorkplaceControls.Editor(180, 20000);
    private readonly TextBox _preview = WorkplaceControls.Editor(240, 40000, true);
    private readonly TextBlock _state = WorkplaceControls.Label("Ouvrez le formulaire souhaité dans l’onglet dédié GLPI, puis inspectez-le.");
    private readonly Button _confirm;
    private IReadOnlyList<TicketActionCandidate> _candidates = Array.Empty<TicketActionCandidate>();
    private TicketActionPreview? _pending;
    private string _ticketKey = "";
    private bool _busy;
    public event Action? InspectRequested;
    public event Action? BrowserRequested;
    public event Action<TicketActionAdapter>? AdapterSaveRequested;
    public event Action<TicketActionRequest>? PreviewRequested;
    public event Action<TicketActionPreview>? ConfirmRequested;

    public TicketActionsView()
    {
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(WorkplaceControls.Label("Actions GLPI assistées", 18));
        root.Children.Add(WorkplaceControls.Label("Chaque action utilise un formulaire natif ouvert dans la fiche dédiée. L’inspection contrôle l’installation, l’identifiant du ticket, la destination et les champs autorisés. Prévisualisez la valeur et les autres champs, puis confirmez une seule soumission. Les droits restent ceux de votre session GLPI."));
        var commands = new WrapPanel();
        commands.Children.Add(WorkplaceControls.Command("Afficher la fiche GLPI", () => BrowserRequested?.Invoke()));
        commands.Children.Add(WorkplaceControls.Command("Inspecter le formulaire ouvert", () => { Invalidate(); InspectRequested?.Invoke(); }));
        commands.Children.Add(WorkplaceControls.Command("Mémoriser cet adaptateur", () => { if (_adapter.SelectedItem is TicketActionAdapter selected) AdapterSaveRequested?.Invoke(selected); }));
        root.Children.Add(commands); root.Children.Add(_state); root.Children.Add(_adapter);
        root.Children.Add(WorkplaceControls.Label("Valeur à préparer")); root.Children.Add(_choice); root.Children.Add(_value);
        root.Children.Add(WorkplaceControls.Command("Prévisualiser l’action", Preview)); root.Children.Add(_preview);
        _confirm = WorkplaceControls.Command("Confirmer l’aperçu et envoyer une fois…", () => { if (!_busy && _pending != null) ConfirmRequested?.Invoke(_pending); }); _confirm.IsEnabled = false;
        root.Children.Add(_confirm);
        root.Children.Add(WorkplaceControls.Label("Le formulaire doit être en POST, limité à l’affectation ou à l’ajout demandé et rattaché à ce ticket. Les formulaires non reconnus restent disponibles directement dans GLPI. Un résultat non confirmé impose une vérification manuelle avant toute nouvelle action. Une référence unique est incluse dans les nouveaux suivis et tâches pour retrouver l’envoi."));
        Content = WorkplaceControls.Viewport(root);
        _adapter.SelectionChanged += (_, _) => SelectAdapter();
        _choice.SelectionChanged += (_, _) => Invalidate(); _value.TextChanged += (_, _) => Invalidate();
    }
    private void SelectAdapter()
    {
        Invalidate(); var selected = _adapter.SelectedItem as TicketActionAdapter;
        var assignment = selected?.Kind == "assignment";
        _choice.Visibility = assignment ? Visibility.Visible : Visibility.Collapsed; _value.Visibility = assignment ? Visibility.Collapsed : Visibility.Visible;
        _choice.ItemsSource = _candidates.FirstOrDefault(item => item.Adapter.Key == selected?.Key)?.Choices ?? Array.Empty<TicketActionChoice>();
        _choice.SelectedIndex = -1;
    }
    private void Preview()
    {
        if (_busy || _adapter.SelectedItem is not TicketActionAdapter adapter) { _state.Text = "Inspectez puis choisissez un formulaire reconnu."; return; }
        var choice = _choice.SelectedItem as TicketActionChoice;
        var value = adapter.Kind == "assignment" ? choice?.Value ?? "" : _value.Text;
        PreviewRequested?.Invoke(new(adapter.Copy(), value, choice?.Label ?? value));
    }
    private void Invalidate() { _pending = null; _confirm.IsEnabled = false; _preview.Clear(); }
    public void ShowTicket(TicketReference? ticket)
    {
        var key = ticket == null ? "" : WorkplaceStore.TicketKey(ticket);
        if (_ticketKey == key) return;
        _ticketKey = key; _candidates = Array.Empty<TicketActionCandidate>(); _adapter.ItemsSource = null; _value.Clear(); Invalidate();
        _state.Text = "Ouvrez le formulaire souhaité dans l’onglet dédié GLPI, puis inspectez-le.";
    }
    public void ShowAdapters(IEnumerable<TicketActionAdapter> adapters) => _adapter.ItemsSource = adapters.Select(item => item.Copy()).ToArray();
    public void ShowCandidates(IReadOnlyList<TicketActionCandidate> candidates)
    {
        _candidates = candidates; _adapter.ItemsSource = candidates.Select(item => item.Adapter).ToArray(); _adapter.SelectedIndex = candidates.Count > 0 ? 0 : -1;
        _state.Text = candidates.Count == 0 ? "Aucun formulaire natif sûr reconnu. Dans GLPI, ouvrez l’ajout d’un suivi, d’une tâche ou d’un acteur attribué, puis inspectez à nouveau. Les champs inattendus, cibles ambiguës et modifications d’entrées existantes sont refusés."
            : $"{candidates.Count} formulaire(s) reconnus. Les valeurs seront revalidées avant la confirmation.";
    }
    public void ShowPreview(TicketActionPreview preview) { _pending = preview; _preview.Text = preview.Text; _confirm.IsEnabled = !_busy; _state.Text = "Aperçu prêt. Relisez le ticket, la valeur et les autres champs avant confirmation."; }
    public void ShowOutcome(TicketActionOutcome outcome) { Invalidate(); _state.Text = outcome.Message; }
    public void SetMessage(string message) { Invalidate(); _state.Text = message; }
    public void UseDraft(string text) { _value.Text = text; var followup = (_adapter.ItemsSource as IEnumerable<TicketActionAdapter>)?.FirstOrDefault(a => a.Kind == "followup"); if (followup != null) _adapter.SelectedItem = followup; _state.Text = "Brouillon repris. Inspectez le formulaire de suivi et prévisualisez avant confirmation."; }
    public void SetBusy(bool busy) { _busy = busy; _confirm.IsEnabled = !busy && _pending != null; }
}
