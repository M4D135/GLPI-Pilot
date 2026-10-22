using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed record PersonalDayTicket(PersonalTicket Personal, string Status, string City, string Due, bool? Active, bool InSource, bool Mine, int? Priority = null);

public sealed class PersonalDayView : UserControl
{
    private readonly TextBlock _summary = WorkplaceControls.Label("Ma journée", 18);
    private readonly TextBlock _brief = WorkplaceControls.Label("Choisissez votre identité puis chargez un relevé.");
    private readonly ComboBox _scope = new() { Width = 190 };
    private readonly TextBox _search = new() { Width = 220, MaxLength = 300 };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow, FrozenColumnCount = 2 };
    private readonly TextBlock _count = WorkplaceControls.Label("");
    private readonly TextBox _handover = WorkplaceControls.Editor(double.NaN, 1000000, true);
    private readonly TabControl _pages = new();
    private readonly TabItem _listTab, _editTab, _handoverTab;
    private readonly PersonalTicketEditorView _editor = new();
    private readonly ComboBox _templateChoice = new() { Width = 220 };
    private readonly TextBox _templateName = new() { Width = 220, MaxLength = 100 };
    private readonly TextBox _templateLines = WorkplaceControls.Editor(220, 20000);
    private readonly CheckBox _desktopReminders = new() { Content = "Afficher une notification Windows pour mes rappels", Margin = new Thickness(0, 12, 0, 12) };
    private List<DayRow> _rows = new();
    private WorkplaceDocument _document = new();
    private bool _busy, _refreshing;
    private bool _updatingPreferences;
    private bool _stateRestored;
    private readonly TextBlock _briefingSummary = WorkplaceControls.Label("");
    private readonly DataGrid _briefingGrid = new() { AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true };
    private readonly TextBlock _tourSummary = WorkplaceControls.Label("");
    private readonly DataGrid _tourGrid = new() { AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true };
    private bool _refreshingTemplates;
    public event Action<PersonalTicket>? TicketRequested;
    public event Action<PersonalTicket>? SaveRequested;
    public event Action<string, int>? MoveRequested;
    public event Action<string, List<string>>? TemplateSaveRequested;
    public event Action<string>? TemplateDeleteRequested;
    public event Action<bool>? DesktopRemindersChanged;
    public event Action<string, int, string>? ViewStateChanged;
    public event Action? ToursRequested;

    public PersonalDayView()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var intro = new StackPanel(); intro.Children.Add(_summary); intro.Children.Add(_brief); grid.Children.Add(intro);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        _scope.ItemsSource = new[] { "Aujourd’hui", "Mes tickets du relevé", "File personnelle", "Épinglés", "Rappels à traiter", "Reportés", "Tous les conservés" }; _scope.SelectedIndex = 0;
        toolbar.Children.Add(_scope); toolbar.Children.Add(_search);
        toolbar.Children.Add(WorkplaceControls.Command("Ouvrir le ticket", OpenSelected));
        toolbar.Children.Add(WorkplaceControls.Command("Mon travail", () => { ShowEditor(); _pages.SelectedItem = _editTab; }));
        toolbar.Children.Add(WorkplaceControls.Command("↑", () => Move(-1))); toolbar.Children.Add(WorkplaceControls.Command("↓", () => Move(1)));
        toolbar.Children.Add(WorkplaceControls.Command("Ajouter / retirer de la file", ToggleQueue));
        toolbar.Children.Add(WorkplaceControls.Command("Rappel traité", Acknowledge));
        Grid.SetRow(toolbar, 1); grid.Children.Add(toolbar);
        Column("Ticket", nameof(DayRow.Id), 90); Column("Titre", nameof(DayRow.Title), 300); Column("État", nameof(DayRow.Status), 150);
        Column("Ville", nameof(DayRow.City), 130); Column("Échéance GLPI", nameof(DayRow.Due), 145); Column("Rappel personnel", nameof(DayRow.Reminder), 155);
        Column("Reprise personnelle", nameof(DayRow.Resume), 155); Column("File", nameof(DayRow.Queue), 65); Column("Couverture", nameof(DayRow.Coverage), 200);
        Grid.SetRow(_grid, 2); grid.Children.Add(_grid); Grid.SetRow(_count, 3); grid.Children.Add(_count);
        _listTab = new TabItem { Header = "À faire", Content = grid };
        _editTab = new TabItem { Header = "Travail sélectionné", Content = _editor };
        var handoverRoot = new DockPanel(); var copy = WorkplaceControls.Command("Copier la passation sélectionnée", () => { try { Clipboard.SetText(_handover.Text); } catch (Exception error) { _count.Text = error.Message; } });
        DockPanel.SetDock(copy, Dock.Top); handoverRoot.Children.Add(copy); handoverRoot.Children.Add(_handover);
        _handoverTab = new TabItem { Header = "Passation", Content = handoverRoot };
        _pages.Items.Add(_listTab); _pages.Items.Add(_editTab); _pages.Items.Add(_handoverTab); _pages.Items.Add(new TabItem { Header = "Modèles de listes", Content = BuildTemplates() });
        var preferences = new StackPanel { Margin = new Thickness(18) }; preferences.Children.Add(WorkplaceControls.Label("Mes rappels", 18)); preferences.Children.Add(_desktopReminders);
        preferences.Children.Add(WorkplaceControls.Label("Les rappels sont vérifiés toutes les minutes pendant que GLPI Pilot est ouvert. La notification indique seulement un nombre ; son contenu ne révèle aucun titre de ticket. Cliquer sur la notification ouvre Ma journée. Les réglages de notification de Windows peuvent masquer cet affichage ; les rappels restent présents dans la liste."));
        _pages.Items.Add(new TabItem { Header = "Préférences personnelles", Content = WorkplaceControls.Viewport(preferences) });
        _desktopReminders.Checked += (_, _) => { if (!_updatingPreferences) DesktopRemindersChanged?.Invoke(true); };
        _desktopReminders.Unchecked += (_, _) => { if (!_updatingPreferences) DesktopRemindersChanged?.Invoke(false); };
        var briefingRoot = new DockPanel(); DockPanel.SetDock(_briefingSummary, Dock.Top); briefingRoot.Children.Add(_briefingSummary); briefingRoot.Children.Add(_briefingGrid);
        _pages.Items.Add(new TabItem { Header = "Briefing de session", Content = briefingRoot });
        var toursRoot = new DockPanel(); var toursTop = new StackPanel(); toursTop.Children.Add(_tourSummary); toursTop.Children.Add(WorkplaceControls.Command("Ouvrir les tournées", () => ToursRequested?.Invoke())); DockPanel.SetDock(toursTop, Dock.Top); toursRoot.Children.Add(toursTop); toursRoot.Children.Add(_tourGrid);
        _pages.Items.Add(new TabItem { Header = "Tournées du jour", Content = toursRoot });
        _briefingGrid.MouseDoubleClick += (_, _) => { if (_briefingGrid.SelectedItem is PersonalBriefingLine row && _rows.FirstOrDefault(item => item.Ticket.Personal.Key == row.TicketKey) is { } ticket) TicketRequested?.Invoke(ticket.Ticket.Personal); };
        Content = _pages;
        _scope.SelectionChanged += (_, _) => ApplyFilter(); _search.TextChanged += (_, _) => ApplyFilter();
        _grid.SelectionChanged += (_, _) => { if (!_refreshing) { ShowEditor(); NotifyState(); } };
        _grid.MouseDoubleClick += (_, args) => { if (args.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(_grid, element) is DataGridRow) OpenSelected(); };
        _grid.KeyDown += (_, args) => { if (args.Key == Key.Enter) { OpenSelected(); args.Handled = true; } };
        _editor.SaveRequested += ticket => SaveRequested?.Invoke(ticket);
        _editor.HandoverRequested += () => { if (_grid.SelectedItem is DayRow row) _handover.Text = TicketReadingService.Handover(row.Ticket.Personal); _pages.SelectedItem = _handoverTab; };
        AutomationProperties.SetAutomationId(this, "PersonalDay");
    }

    private UIElement BuildTemplates()
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(WorkplaceControls.Label("Mes modèles de vérification", 18)); panel.Children.Add(_templateChoice);
        panel.Children.Add(WorkplaceControls.Label("Nom du modèle")); panel.Children.Add(_templateName);
        panel.Children.Add(WorkplaceControls.Label("Une action par ligne · 40 actions maximum")); panel.Children.Add(_templateLines);
        var actions = new WrapPanel();
        actions.Children.Add(WorkplaceControls.Command("Enregistrer le modèle", () => TemplateSaveRequested?.Invoke(_templateName.Text.Trim(), _templateLines.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(text => text.Trim()).Where(text => text.Length > 0).ToList())));
        actions.Children.Add(WorkplaceControls.Command("Retirer le modèle", () => { if (_templateChoice.SelectedItem is string name) TemplateDeleteRequested?.Invoke(name); })); panel.Children.Add(actions);
        _templateChoice.SelectionChanged += (_, _) => { if (!_refreshingTemplates && _templateChoice.SelectedItem is string name && _document.ChecklistTemplates.TryGetValue(name, out var lines)) { _templateName.Text = name; _templateLines.Text = string.Join("\n", lines); } };
        return WorkplaceControls.Viewport(panel);
    }

    public void Show(IEnumerable<PersonalDayTicket> tickets, WorkplaceDocument document, string personalName, DateTimeOffset? extractedAt)
    {
        _document = document;
        if (!_stateRestored) { _refreshing = true; try { _scope.SelectedIndex = document.DayScope; _search.Text = document.DaySearch; } finally { _refreshing = false; } _stateRestored = true; }
        _updatingPreferences = true; try { _desktopReminders.IsChecked = document.DesktopReminders; } finally { _updatingPreferences = false; }
        _rows = tickets.Select(ticket => new DayRow(ticket)).OrderBy(row => row.Ticket.Personal.QueueOrder ?? int.MaxValue)
            .ThenBy(row => row.Ticket.Personal.ReminderAt ?? DateTimeOffset.MaxValue).ThenBy(row => row.Id).ToList();
        var due = _rows.Count(row => row.Ticket.Personal.ReminderAt <= DateTimeOffset.Now && (!row.Ticket.Personal.ReminderAcknowledgedAt.HasValue || row.Ticket.Personal.ReminderAcknowledgedAt < row.Ticket.Personal.ReminderAt));
        _summary.Text = "Ma journée" + (personalName.Length > 0 ? " · " + personalName : " · identité à choisir");
        _brief.Text = $"{_rows.Count(row => row.Ticket.Mine):N0} tickets du relevé attribués à mon identité · {document.Tickets.Count(ticket => ticket.QueueOrder.HasValue):N0} dans ma file · {due:N0} rappels à traiter. "
            + (extractedAt.HasValue ? $"Relevé du {extractedAt.Value.LocalDateTime:dd/MM/yyyy HH:mm}." : "Aucun relevé chargé ; les dossiers conservés restent accessibles.")
            + " Une absence du relevé ne confirme aucune clôture.";
        var selectedTemplate = _templateChoice.SelectedItem as string;
        _refreshingTemplates = true;
        try { _templateChoice.ItemsSource = document.ChecklistTemplates.Keys.ToArray(); _templateChoice.SelectedItem = selectedTemplate; }
        finally { _refreshingTemplates = false; }
        ApplyFilter();
    }

    public void SetBusy(bool busy) { _busy = busy; _grid.IsEnabled = _editor.IsEnabled = !busy; }

    public void AcceptSaved(PersonalTicket saved) => _editor.AcceptSaved(saved);
    public void RejectSave(PersonalTicket submitted, string message) => _editor.RejectSave(submitted, message);

    private void ApplyFilter()
    {
        if (_refreshing) return;
        var selected = (_grid.SelectedItem as DayRow)?.Ticket.Personal.Key ?? _document.DaySelectedKey;
        var now = DateTimeOffset.Now; var query = _search.Text.Trim();
        var shown = _rows.Where(row =>
        {
            var record = row.Ticket.Personal;
            var due = record.ReminderAt <= now && (!record.ReminderAcknowledgedAt.HasValue || record.ReminderAcknowledgedAt < record.ReminderAt);
            var snoozed = record.SnoozedUntil > now;
            var matches = _scope.SelectedIndex switch
            {
                0 => !snoozed && (record.QueueOrder.HasValue || due || row.Ticket.Mine && row.Ticket.Active != false),
                1 => row.Ticket.Mine && row.Ticket.InSource,
                2 => record.QueueOrder.HasValue, 3 => record.Pinned, 4 => due, 5 => snoozed,
                _ => _document.Tickets.Any(ticket => ticket.Key == record.Key)
            };
            return matches && (query.Length == 0 || TicketReadingService.Contains(row.Title + " " + row.Id + " " + row.City + " " + record.Notes, query));
        }).ToArray();
        _refreshing = true;
        try { _grid.ItemsSource = shown; _grid.SelectedItem = shown.FirstOrDefault(row => row.Ticket.Personal.Key == selected) ?? shown.FirstOrDefault(); }
        finally { _refreshing = false; }
        _count.Text = $"{shown.Length:N0} tickets affichés · ordre personnel indépendant de GLPI"; ShowEditor(); NotifyState();
    }

    private void NotifyState() { if (!_refreshing && _stateRestored) ViewStateChanged?.Invoke(_search.Text, _scope.SelectedIndex, (_grid.SelectedItem as DayRow)?.Ticket.Personal.Key ?? ""); }

    public void ShowBriefing(PersonalBriefingReport report) { _briefingSummary.Text = report.Summary; _briefingGrid.ItemsSource = report.Lines; }
    public void ShowTours(IReadOnlyList<ScheduledTourDayItem> tours, bool personalIdentity, string warning = "")
    {
        _tourSummary.Text = $"{tours.Count} tournée(s) pour le {DateTime.Today:dd/MM/yyyy}" + (personalIdentity ? " · mon identité et tournées sans affectation." : " · toutes les identités, identité personnelle non choisie.") + " " + warning;
        _tourGrid.ItemsSource = tours.Select(tour => new { Tournée = tour.Name, État = ScheduledTourService.StateLabel(tour.State), Début = tour.StartTime, Fin = tour.EndTime, Technicien = tour.TechnicianKey, Tickets = string.Join(", ", tour.TicketIds) }).ToArray();
    }

    private void ShowEditor()
    {
        var ticket = (_grid.SelectedItem as DayRow)?.Ticket.Personal;
        _editor.Show(ticket, _document, false);
        _handover.Text = ticket == null ? "Choisissez un ticket." : TicketReadingService.Handover(ticket);
    }
    private void OpenSelected() { if (!_busy && _grid.SelectedItem is DayRow row) TicketRequested?.Invoke(row.Ticket.Personal); }
    private void Move(int direction) { if (!_busy && _grid.SelectedItem is DayRow row) MoveRequested?.Invoke(row.Ticket.Personal.Key, direction); }
    private void ToggleQueue()
    {
        if (_busy || _grid.SelectedItem is not DayRow row) return;
        var ticket = row.Ticket.Personal.Copy(); ticket.QueueOrder = ticket.QueueOrder.HasValue ? null : _document.Tickets.Count(item => item.QueueOrder.HasValue);
        SaveRequested?.Invoke(ticket);
    }
    private void Acknowledge()
    {
        if (_busy || _grid.SelectedItem is not DayRow row) return;
        var ticket = row.Ticket.Personal.Copy(); ticket.ReminderAcknowledgedAt = DateTimeOffset.Now; SaveRequested?.Invoke(ticket);
    }
    private void Column(string title, string property, double width) => _grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width });
    private sealed class DayRow(PersonalDayTicket ticket)
    {
        public PersonalDayTicket Ticket { get; } = ticket;
        public string Id => Ticket.Personal.Id;
        public string Title => (Ticket.Personal.Pinned ? "★ " : "") + Ticket.Personal.Title;
        public string Status => Ticket.Status;
        public string City => Ticket.City;
        public string Due => Ticket.Due;
        public string Reminder => Ticket.Personal.ReminderAt?.LocalDateTime.ToString("dd/MM HH:mm") ?? "";
        public string Resume => Ticket.Personal.SnoozedUntil?.LocalDateTime.ToString("dd/MM HH:mm") ?? "";
        public int? Queue => Ticket.Personal.QueueOrder + 1;
        public string Coverage => Ticket.InSource ? "Présent dans le relevé" : "Absent / aucun relevé · statut inconnu";
    }
}
