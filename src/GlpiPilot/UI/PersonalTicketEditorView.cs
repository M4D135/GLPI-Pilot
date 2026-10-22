using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class PersonalTicketEditorView : UserControl
{
    private readonly TextBlock _heading = WorkplaceControls.Label("Choisissez un ticket", 18);
    private readonly TextBlock _message = WorkplaceControls.Label("Les informations de cet espace sont personnelles et restent sur ce poste.");
    private readonly CheckBox _pinned = new() { Content = "Épingler", Margin = new Thickness(0, 0, 16, 8) };
    private readonly CheckBox _queue = new() { Content = "Dans ma file de travail", Margin = new Thickness(0, 0, 16, 8) };
    private readonly CheckBox _offline = new() { Content = "Conserver les sections lues hors connexion", Margin = new Thickness(0, 0, 16, 8) };
    private readonly TextBox _notes = WorkplaceControls.Editor(140, WorkplaceStore.MaximumNotes);
    private readonly TextBox _checklist = WorkplaceControls.Editor(120, 20000);
    private readonly DatePicker _reminderDate = new() { Width = 150 };
    private readonly TextBox _reminderTime = new() { Width = 64, Text = "09:00", MaxLength = 5 };
    private readonly DatePicker _snoozeDate = new() { Width = 150 };
    private readonly TextBox _snoozeReason = new() { MinWidth = 220, MaxWidth = 550, MaxLength = 1200, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _templates = new() { Width = 200 };
    private readonly Button _save;
    private PersonalTicket? _ticket;
    private WorkplaceDocument _document = new();
    private bool _canKeepOffline;
    private readonly Dictionary<string, EditorDraft> _drafts = new(StringComparer.Ordinal);
    private PersonalTicket? _submitted;
    public event Action<PersonalTicket>? SaveRequested;
    public event Action? HandoverRequested;

    public PersonalTicketEditorView()
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(_heading); panel.Children.Add(_message);
        var options = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        options.Children.Add(_pinned); options.Children.Add(_queue); options.Children.Add(_offline); panel.Children.Add(options);
        panel.Children.Add(WorkplaceControls.Label("Notes personnelles")); panel.Children.Add(_notes);
        var checklistHeading = new WrapPanel { Margin = new Thickness(0, 14, 0, 6) };
        checklistHeading.Children.Add(WorkplaceControls.Label("Liste de vérification · une ligne par action · [x] = terminée"));
        checklistHeading.Children.Add(_templates);
        checklistHeading.Children.Add(WorkplaceControls.Command("Ajouter le modèle", () =>
        {
            if (_templates.SelectedItem is string name && _document.ChecklistTemplates.TryGetValue(name, out var entries))
                _checklist.Text = string.Join("\n", new[] { _checklist.Text.Trim() }.Concat(entries.Select(text => "[ ] " + text)).Where(text => text.Length > 0));
        }));
        panel.Children.Add(checklistHeading); panel.Children.Add(_checklist);
        var reminders = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        reminders.Children.Add(WorkplaceControls.Label("Rappel personnel")); reminders.Children.Add(_reminderDate); reminders.Children.Add(_reminderTime);
        reminders.Children.Add(WorkplaceControls.Command("Effacer le rappel", () => _reminderDate.SelectedDate = null)); panel.Children.Add(reminders);
        var snooze = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        snooze.Children.Add(WorkplaceControls.Label("Reporter dans ma journée jusqu’au")); snooze.Children.Add(_snoozeDate);
        snooze.Children.Add(WorkplaceControls.Command("Reprendre maintenant", () => { _snoozeDate.SelectedDate = null; _snoozeReason.Clear(); })); panel.Children.Add(snooze);
        panel.Children.Add(WorkplaceControls.Label("Motif personnel du report")); panel.Children.Add(_snoozeReason);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 4) };
        _save = WorkplaceControls.Command("Enregistrer mon travail", Save); actions.Children.Add(_save);
        actions.Children.Add(WorkplaceControls.Command("Préparer la passation", () => HandoverRequested?.Invoke())); panel.Children.Add(actions);
        panel.Children.Add(WorkplaceControls.Label("Le rappel et le report sont des repères personnels. Ils ne remplacent pas l’échéance ni le statut GLPI."));
        Content = WorkplaceControls.Viewport(panel);
        AutomationProperties.SetAutomationId(this, "PersonalTicketEditor");
        Show(null, new(), false);
    }

    public void Show(PersonalTicket? ticket, WorkplaceDocument document, bool canKeepOffline)
    {
        RememberDraft();
        _ticket = ticket?.Copy(); _document = document; _canKeepOffline = canKeepOffline;
        var baseline = EditorDraft.From(ticket);
        var draft = ticket != null && _drafts.TryGetValue(ticket.Key, out var retained) ? retained : baseline;
        if (ticket != null && draft == baseline) _drafts.Remove(ticket.Key);
        _heading.Text = ticket == null ? "Choisissez un ticket" : "Mon travail · " + ticket.Id + " · " + ticket.Title;
        _pinned.IsChecked = draft.Pinned; _queue.IsChecked = draft.Queued; _offline.IsChecked = draft.Offline;
        _notes.Text = draft.Notes; _checklist.Text = draft.Checklist;
        _reminderDate.SelectedDate = draft.ReminderDate; _reminderTime.Text = draft.ReminderTime;
        _snoozeDate.SelectedDate = draft.SnoozeDate; _snoozeReason.Text = draft.SnoozeReason;
        var selectedTemplate = _templates.SelectedItem as string;
        _templates.ItemsSource = document.ChecklistTemplates.Keys.ToArray();
        _templates.SelectedItem = selectedTemplate != null && document.ChecklistTemplates.ContainsKey(selectedTemplate)
            ? selectedTemplate : document.ChecklistTemplates.Keys.FirstOrDefault();
        _save.IsEnabled = ticket != null; _offline.IsEnabled = ticket != null && (canKeepOffline || ticket.Offline);
        _message.Text = ticket?.LastReadAt is DateTimeOffset date ? $"Dernière lecture mémorisée : {date.LocalDateTime:dd/MM/yyyy HH:mm}. Les notes restent sur ce poste."
            : "Les informations de cet espace restent sur ce poste. Sélectionnez un ticket du relevé ou un ticket épinglé.";
        if (draft != baseline) _message.Text += " Brouillon conservé dans cette session ; cliquez sur Enregistrer mon travail pour le conserver après fermeture.";
        _message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
    }

    // A save event is only a request. Clear its draft after the caller confirms the disk write.
    public void AcceptSaved(PersonalTicket saved)
    {
        if (!ReferenceEquals(_submitted, saved)) return;
        _submitted = null; _drafts.Remove(saved.Key);
        if (_ticket?.Key != saved.Key) return;
        _ticket = null;
        Show(saved, _document, _canKeepOffline);
    }

    public void RejectSave(PersonalTicket submitted, string message)
    {
        if (ReferenceEquals(_submitted, submitted)) SetMessage(message, true);
    }

    private void RememberDraft()
    {
        if (_ticket == null) return;
        var draft = new EditorDraft(_pinned.IsChecked == true, _queue.IsChecked == true, _offline.IsChecked == true,
            EditorDraft.Lines(_notes.Text), EditorDraft.Lines(_checklist.Text), _reminderDate.SelectedDate, _reminderTime.Text,
            _snoozeDate.SelectedDate, EditorDraft.Lines(_snoozeReason.Text));
        if (draft == EditorDraft.From(_ticket)) _drafts.Remove(_ticket.Key);
        else _drafts[_ticket.Key] = draft;
    }

    private sealed record EditorDraft(bool Pinned, bool Queued, bool Offline, string Notes, string Checklist,
        DateTime? ReminderDate, string ReminderTime, DateTime? SnoozeDate, string SnoozeReason)
    {
        internal static EditorDraft From(PersonalTicket? ticket) => new(ticket?.Pinned == true, ticket?.QueueOrder.HasValue == true,
            ticket?.Offline == true, Lines(ticket?.Notes ?? ""), string.Join("\n", (ticket?.Checklist ?? new()).Select(item => (item.Done ? "[x] " : "[ ] ") + item.Text)),
            ticket?.ReminderAt?.LocalDateTime.Date, ticket?.ReminderAt?.LocalDateTime.ToString("HH:mm") ?? "09:00",
            ticket?.SnoozedUntil?.LocalDateTime.Date, Lines(ticket?.SnoozeReason ?? ""));
        internal static string Lines(string text) => text.Replace("\r\n", "\n");
    }

    public void SetMessage(string text, bool error = false)
    { _message.Text = text; _message.SetResourceReference(TextBlock.ForegroundProperty, error ? "WarningBrush" : "MutedBrush"); }

    private void Save()
    {
        if (_ticket == null) return;
        try
        {
            var copy = _ticket.Copy(); copy.Pinned = _pinned.IsChecked == true; copy.Offline = _offline.IsChecked == true;
            if (copy.Offline && !_canKeepOffline && !_ticket.Offline) throw new InvalidOperationException("Lisez la fiche avant de conserver ses sections hors connexion.");
            copy.QueueOrder = _queue.IsChecked == true ? copy.QueueOrder ?? Math.Min(WorkplaceStore.MaximumTickets, _document.Tickets.Count(ticket => ticket.QueueOrder.HasValue)) : null;
            copy.Notes = _notes.Text.Trim();
            copy.Checklist = _checklist.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var text = line.Trim(); var done = text.StartsWith("[x]", StringComparison.OrdinalIgnoreCase);
                if (done || text.StartsWith("[ ]", StringComparison.Ordinal)) text = text[3..].Trim();
                return new PersonalChecklistItem { Text = text, Done = done };
            }).Where(item => item.Text.Length > 0).ToList();
            if (copy.Checklist.Count > WorkplaceStore.MaximumChecklistItems || copy.Checklist.Any(item => item.Text.Length > 500))
                throw new InvalidOperationException("La liste accepte 40 actions de 500 caractères maximum.");
            if (_reminderDate.SelectedDate is DateTime day)
            {
                if (!TimeSpan.TryParseExact(_reminderTime.Text.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out var time) || time.TotalHours >= 24)
                    throw new InvalidOperationException("L’heure du rappel doit être au format HH:mm.");
                copy.ReminderAt = new DateTimeOffset(DateTime.SpecifyKind(day.Date.Add(time), DateTimeKind.Local));
            }
            else copy.ReminderAt = null;
            if (copy.ReminderAt != _ticket.ReminderAt) copy.ReminderAcknowledgedAt = null;
            copy.SnoozedUntil = _snoozeDate.SelectedDate is DateTime resume ? new DateTimeOffset(DateTime.SpecifyKind(resume.Date.AddHours(8), DateTimeKind.Local)) : null;
            copy.SnoozeReason = _snoozeReason.Text.Trim();
            _submitted = copy;
            SaveRequested?.Invoke(copy);
        }
        catch (Exception error) { SetMessage(error.Message, true); }
    }
}

internal static class WorkplaceControls
{
    internal static TextBlock Label(string text, double size = 12)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, size >= 16 ? "TextBrush" : "MutedBrush"); return label;
    }
    internal static Button Command(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(4, 3, 4, 3), Padding = new Thickness(10, 6, 10, 6), MinHeight = 34 };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    internal static TextBox Editor(double height, int maximum, bool readOnly = false) => new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, MaxLength = maximum, IsReadOnly = readOnly,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    internal static ScrollViewer Viewport(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false
    };
}
