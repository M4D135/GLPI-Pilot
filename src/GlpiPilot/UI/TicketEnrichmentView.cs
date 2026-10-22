using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class TicketEnrichmentView : UserControl
{
    private readonly TextBlock _summary = WorkplaceControls.Label("Préparez une sélection ou une requête depuis le relevé.");
    private readonly TextBox _limit = new() { Text = "20", Width = 65, MaxLength = 3 };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly Button _start, _cancel;
    private TicketEnrichmentQueue _queue = new();
    public event Action<int>? StartRequested;
    public event Action? CancelRequested;
    public event Action? ResetRequested;
    public event Action<TicketReference>? TicketRequested;
    public event Action<string>? PrioritizeRequested;
    public TicketEnrichmentView()
    {
        var root = new DockPanel(); var top = new StackPanel { Margin = new Thickness(10) }; top.Children.Add(_summary);
        top.Children.Add(WorkplaceControls.Label("Lecture séquentielle des fiches dans l’onglet dédié, recherche conservée. La limite est globale pour ce lancement. Chaque lancement reprend les candidats encore non lus avec succès. Les résultats datés ci-dessous restent séparés du relevé et de ses comparaisons historiques."));
        var commands = new WrapPanel(); commands.Children.Add(WorkplaceControls.Label("Maximum de fiches (1–100)")); commands.Children.Add(_limit);
        _start = WorkplaceControls.Command("Lancer la lecture de cette file", () => { if (int.TryParse(_limit.Text, out var limit) && limit is >= 1 and <= 100) StartRequested?.Invoke(limit); else _summary.Text = "Choisissez une limite de 1 à 100 fiches."; });
        _cancel = WorkplaceControls.Command("Annuler la file", () => CancelRequested?.Invoke()); _cancel.IsEnabled = false;
        commands.Children.Add(_start); commands.Children.Add(_cancel);
        commands.Children.Add(WorkplaceControls.Command("Réinitialiser pour relire la file", () => ResetRequested?.Invoke()));
        commands.Children.Add(WorkplaceControls.Command("Lire ce ticket", () => { if (_grid.SelectedItem is QueueRow row) TicketRequested?.Invoke(row.Reference); }));
        commands.Children.Add(WorkplaceControls.Command("Priorité au ticket choisi", () => { if (_grid.SelectedItem is QueueRow row) PrioritizeRequested?.Invoke(WorkplaceStore.TicketKey(row.Reference)); }));
        top.Children.Add(commands); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); root.Children.Add(_grid); Content = root;
        foreach (var (label, property, width) in new[] { ("Ordre", "Order", 65d), ("Ticket", "Id", 90d), ("Lecture", "State", 90d), ("Date de lecture", "ReadAt", 155d), ("Statut observé", "Status", 190d), ("Rubriques", "Sections", 80d), ("Précision", "Message", 430d) })
            _grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), Width = width });
    }
    public void Show(TicketEnrichmentQueue queue, string suffix = "")
    {
        _queue = queue; _limit.Text = queue.Limit.ToString();
        var results = queue.Results.GroupBy(result => WorkplaceStore.TicketKey(result.Ticket)).ToDictionary(group => group.Key, group => group.Last());
        _grid.ItemsSource = queue.Tickets.Select((ticket, index) => new QueueRow(ticket, index + 1, results.GetValueOrDefault(WorkplaceStore.TicketKey(ticket)))).ToArray();
        _summary.Text = queue.Label + $" · {queue.Tickets.Count} candidats · {queue.Results.Count(result => result.State == "Lu")} lectures réussies. " + suffix;
    }
    public void SetBusy(bool busy) { _start.IsEnabled = _limit.IsEnabled = !busy; _cancel.IsEnabled = busy; }
    private sealed record QueueRow(TicketReference Reference, int Order, TicketEnrichmentOutcome? Outcome)
    {
        public string Id => Reference.Id; public string State => Outcome?.State ?? "En attente";
        public string ReadAt => Outcome?.ReadAt?.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss") ?? "";
        public string Status => Outcome?.ObservedStatus ?? "Non lu"; public int? Sections => Outcome?.Sections; public string Message => Outcome?.Message ?? "";
    }
}
