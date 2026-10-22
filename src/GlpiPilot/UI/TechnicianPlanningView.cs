using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

public sealed class TechnicianPlanningView : UserControl
{
    private readonly TextBox _name = new() { Width = 220, MaxLength = 200 };
    private readonly DatePicker _date = new() { Width = 150, SelectedDate = DateTime.Today };
    private readonly TextBox _search = new() { Width = 240, MaxLength = 300 };
    private readonly TextBox _requiredSkills = new() { Width = 260, MaxLength = 4000 };
    private readonly TextBox _minutes = new() { Width = 75, Text = "30", MaxLength = 5 };
    private readonly ComboBox _departing = new() { MinWidth = 200, DisplayMemberPath = "Name", SelectedValuePath = "Key" };
    private readonly DataGrid _tickets = GridWithColumns(("Ticket", "Id", 85), ("Titre", "Title", 300), ("Secteur", "Territory", 140), ("Catégorie", "Category", 170));
    private readonly DataGrid _recommendations = GridWithColumns(("Technicien", "Name", 220), ("Admissible", "AvailableForScenario", 100), ("Score du scénario", "Score", 125), ("Estimation · min", "EstimatedLoadMinutes", 140), ("Capacité · min", "CapacityMinutes", 120));
    private readonly DataGrid _proposals = GridWithColumns(("Ticket", "Id", 90), ("Technicien proposé", "Technician", 200), ("Minutes prévues", "Minutes", 140), ("Titre / couverture", "Title", 350));
    private readonly DataGrid _coverage = GridWithColumns(("Secteur", "Territory", 160), ("Principal", "Primary", 250), ("Remplacement", "Backup", 250), ("Sans indisponibilité déclarée", "AvailableDeclared", 250), ("Limites", "Gaps", 300));
    private readonly DataGrid _loads = GridWithColumns(("Technicien", "Technician", 220), ("Avant · min", "BeforeMinutes", 130), ("Après · min", "AfterMinutes", 130), ("Capacité · min", "CapacityMinutes", 130), ("Tickets proposés", "ProposedTickets", 140), ("Méthode / couverture", "Coverage", 400));
    private readonly TextBlock _message = WorkplaceControls.Label("Simulation locale : aucune affectation GLPI n’est modifiée.");
    private readonly TextBlock _evidence = WorkplaceControls.Label("Sélectionnez un ticket puis un technicien proposé pour lire les critères.");
    private readonly TextBox _handover = WorkplaceControls.Editor(double.NaN, 1000000, true);
    private TechnicianWorkloadSnapshot? _snapshot;
    private TechnicianPreferences _preferences = new();
    private IReadOnlyList<DispatchTicket> _data = Array.Empty<DispatchTicket>();
    private DispatchScenario _scenario = new();
    private bool _updating;
    private static string ScenarioPath => Path.Combine(AppPaths.DataRoot, "dispatch-scenario.json");
    public event Action<int[], string>? RowsRequested;

    public TechnicianPlanningView()
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(WorkplaceControls.Label("Scénario")); bar.Children.Add(_name); bar.Children.Add(_date);
        bar.Children.Add(WorkplaceControls.Command("Enregistrer", Save)); bar.Children.Add(WorkplaceControls.Command("Exporter CSV", Export));
        bar.Children.Add(WorkplaceControls.Command("Nouveau scénario", () => { _scenario = new(); _name.Text = _scenario.Name; _date.SelectedDate = _scenario.Date; Refresh(); })); root.Children.Add(bar);
        var tabs = new TabControl(); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var prepare = new Grid(); prepare.RowDefinitions.Add(new() { Height = GridLength.Auto }); prepare.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); prepare.RowDefinitions.Add(new() { Height = GridLength.Auto }); prepare.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); prepare.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var search = new WrapPanel(); search.Children.Add(WorkplaceControls.Label("Ticket / lieu")); search.Children.Add(_search); search.Children.Add(WorkplaceControls.Label("Compétences requises ;")); search.Children.Add(_requiredSkills); prepare.Children.Add(search);
        Grid.SetRow(_tickets, 1); prepare.Children.Add(_tickets);
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) }; actions.Children.Add(WorkplaceControls.Label("Minutes prévues")); actions.Children.Add(_minutes);
        actions.Children.Add(WorkplaceControls.Command("Proposer le technicien sélectionné", Assign));
        actions.Children.Add(WorkplaceControls.Command("Voir le ticket du relevé", () => { if (_tickets.SelectedItem is DispatchTicket ticket) RowsRequested?.Invoke(new[] { ticket.SourceIndex }, "Ticket du scénario local"); }));
        Grid.SetRow(actions, 2); prepare.Children.Add(actions); Grid.SetRow(_recommendations, 3); prepare.Children.Add(_recommendations);
        _evidence.MaxHeight = 110; Grid.SetRow(_evidence, 4); prepare.Children.Add(WorkplaceControls.Viewport(_evidence));
        // Place the evidence viewport explicitly to preserve bounded data grids.
        Grid.SetRow(prepare.Children[^1], 4);
        tabs.Items.Add(new TabItem { Header = "Préparer", Content = prepare });
        var proposals = new DockPanel(); var remove = WorkplaceControls.Command("Retirer la proposition sélectionnée", () => { if (_proposals.SelectedItem is ProposalRow row) { _scenario.Assignments.RemoveAll(item => item.TicketKey == row.Key); Refresh(); } }); DockPanel.SetDock(remove, Dock.Top); proposals.Children.Add(remove); proposals.Children.Add(_proposals);
        tabs.Items.Add(new TabItem { Header = "Propositions", Content = proposals }); tabs.Items.Add(new TabItem { Header = "Couverture", Content = _coverage });
        tabs.Items.Add(new TabItem { Header = "Charge simulée", Content = _loads });
        var absence = new DockPanel(); var absenceActions = new WrapPanel(); absenceActions.Children.Add(WorkplaceControls.Label("Préparer le relais de")); absenceActions.Children.Add(_departing);
        absenceActions.Children.Add(WorkplaceControls.Command("Proposer les relais", ProposeHandover));
        absenceActions.Children.Add(WorkplaceControls.Command("Copier le plan", () => { try { Clipboard.SetText(_handover.Text); } catch (Exception error) { _message.Text = error.Message; } }));
        DockPanel.SetDock(absenceActions, Dock.Top); absence.Children.Add(absenceActions); absence.Children.Add(_handover);
        tabs.Items.Add(new TabItem { Header = "Absences / passation", Content = absence });
        Grid.SetRow(_message, 2); root.Children.Add(_message); Content = root;
        _name.Text = _scenario.Name; _date.SelectedDate = _scenario.Date;
        _search.TextChanged += (_, _) => RefreshTickets(); _requiredSkills.TextChanged += (_, _) => RefreshRecommendations();
        _date.SelectedDateChanged += (_, _) => { if (!_updating) Refresh(); };
        _tickets.SelectionChanged += (_, _) => { if (!_updating) RefreshRecommendations(); };
        _recommendations.SelectionChanged += (_, _) => _evidence.Text = (_recommendations.SelectedItem as DispatchRecommendation)?.Evidence ?? "Aucune recommandation sélectionnée.";
    }

    public void SetScenario(DispatchScenario scenario)
    {
        _scenario = TechnicianPlanningService.Validate(scenario);
        _name.Text = _scenario.Name; _date.SelectedDate = _scenario.Date; Refresh();
    }

    public void Show(TechnicianWorkloadSnapshot snapshot, TechnicianPreferences preferences, IReadOnlyList<DispatchTicket> tickets)
    {
        _snapshot = snapshot; _preferences = preferences.Copy(); _data = tickets;
        var selected = _departing.SelectedValue as string; _departing.ItemsSource = snapshot.Technicians; _departing.SelectedValue = selected;
        Refresh();
    }
    public void SetBusy(bool busy) => IsEnabled = !busy;
    public void Clear() { _snapshot = null; _data = Array.Empty<DispatchTicket>(); _tickets.ItemsSource = _recommendations.ItemsSource = _coverage.ItemsSource = null; Refresh(); }

    private void Refresh()
    {
        RefreshTickets(); RefreshProposals();
        if (_snapshot != null)
            _coverage.ItemsSource = TechnicianPlanningService.Coverage(_snapshot, _preferences,
                _data.Select(ticket => ticket.Territory).Concat(_preferences.Technicians.Values.SelectMany(value => value.PrimaryTerritories.Concat(value.BackupTerritories))), _date.SelectedDate ?? DateTime.Today);
        RefreshRecommendations();
    }
    private void RefreshTickets()
    {
        var selected = (_tickets.SelectedItem as DispatchTicket)?.Key; var search = _search.Text.Trim();
        var shown = _data.Where(ticket => search.Length == 0 || TicketReadingService.Contains(ticket.Id + " " + ticket.Title + " " + ticket.Territory + " " + ticket.Category, search)).ToArray();
        _updating = true; try { _tickets.ItemsSource = shown; _tickets.SelectedItem = shown.FirstOrDefault(ticket => ticket.Key == selected) ?? shown.FirstOrDefault(); } finally { _updating = false; }
        RefreshRecommendations();
    }
    private void RefreshRecommendations()
    {
        if (_snapshot == null || _tickets.SelectedItem is not DispatchTicket ticket) { _recommendations.ItemsSource = null; return; }
        var selected = (_recommendations.SelectedItem as DispatchRecommendation)?.TechnicianKey;
        var recommendations = TechnicianPlanningService.Recommend(ticket, _snapshot, _preferences, _date.SelectedDate ?? DateTime.Today, RequiredSkills(), _scenario, _data);
        _recommendations.ItemsSource = recommendations; _recommendations.SelectedItem = recommendations.FirstOrDefault(item => item.TechnicianKey == selected) ?? recommendations.FirstOrDefault();
    }
    private string[] RequiredSkills() => _requiredSkills.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private int Minutes()
    {
        if (!int.TryParse(_minutes.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 1 or > 10080)
            throw new InvalidOperationException("Indiquez une estimation de 1 à 10 080 minutes.");
        return minutes;
    }
    private void Assign()
    {
        try
        {
            if (_tickets.SelectedItem is not DispatchTicket ticket || _recommendations.SelectedItem is not DispatchRecommendation technician) return;
            if (!technician.AvailableForScenario) throw new InvalidOperationException("Ce scénario indique une indisponibilité ou une compétence requise non déclarée. Ajustez les déclarations ou le scénario.");
            _scenario.Assignments.RemoveAll(item => item.TicketKey == ticket.Key);
            _scenario.Assignments.Add(new() { TicketKey = ticket.Key, TicketId = ticket.Id, TechnicianKey = technician.TechnicianKey, EstimatedMinutes = Minutes() });
            RefreshProposals(); RefreshRecommendations(); _message.Text = "Proposition ajoutée au brouillon. Enregistrer conserve ce scénario local ; GLPI n’est pas modifié.";
        }
        catch (Exception error) { _message.Text = error.Message; }
    }
    private void RefreshProposals()
    {
        var tickets = _data.ToDictionary(ticket => ticket.Key, StringComparer.Ordinal);
        _proposals.ItemsSource = _scenario.Assignments.Select(item => new ProposalRow(item.TicketKey, item.TicketId,
            _snapshot?.Technicians.FirstOrDefault(technician => technician.Key == item.TechnicianKey)?.Name ?? item.TechnicianKey, item.EstimatedMinutes,
            tickets.TryGetValue(item.TicketKey, out var ticket) ? ticket.Title : "Absent du relevé courant · proposition à vérifier")).ToArray();
        var missing = _scenario.Assignments.Count(item => !tickets.ContainsKey(item.TicketKey));
        _loads.ItemsSource = _snapshot == null ? null : TechnicianPlanningService.EstimateLoads(_snapshot, _preferences, _data, _scenario);
        _message.Text = $"{_scenario.Assignments.Count:N0} propositions locales · {missing:N0} absentes du relevé courant · aucun changement envoyé à GLPI.";
    }
    private void Save()
    {
        try
        {
            _scenario.Name = _name.Text.Trim(); _scenario.Date = _date.SelectedDate ?? DateTime.Today;
            TechnicianPlanningService.Save(ScenarioPath, _scenario); _message.Text = "Scénario de répartition enregistré sur ce poste. Les affectations GLPI restent celles du relevé.";
        }
        catch (Exception error) { _message.Text = "Scénario non enregistré : " + error.Message; }
    }
    private void Export()
    {
        try
        {
            var dialog = new SaveFileDialog { Title = "Exporter les propositions locales", Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv", FileName = "GLPI-Repartition-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var rows = (_proposals.ItemsSource as IEnumerable<ProposalRow> ?? Array.Empty<ProposalRow>()).Select(row => new[] { row.Id, row.Title, row.Technician, row.Minutes.ToString(CultureInfo.InvariantCulture), (_date.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd"), "Proposition locale, non appliquée dans GLPI" }).ToList();
            ExportService.WriteCsv(new() { Headers = new() { "Ticket", "Titre / couverture", "Technicien proposé", "Minutes prévues", "Date du scénario", "Nature" }, Rows = rows, Scope = "Scénario local", Complete = true }, dialog.FileName);
            _message.Text = "Propositions exportées : " + dialog.FileName;
        }
        catch (Exception error) { _message.Text = error.Message; }
    }
    private void ProposeHandover()
    {
        try
        {
            if (_snapshot == null || _departing.SelectedValue is not string key) return;
            var minutes = Minutes(); var notes = new List<string>(); var source = _snapshot.Technicians.FirstOrDefault(item => item.Key == key);
            foreach (var ticket in _data.Where(item => item.CurrentTechnicians.Contains(key, StringComparer.Ordinal)))
            {
                var candidate = TechnicianPlanningService.Recommend(ticket, _snapshot, _preferences, _date.SelectedDate ?? DateTime.Today, RequiredSkills(), _scenario, _data)
                    .FirstOrDefault(item => item.AvailableForScenario && item.TechnicianKey != key);
                if (candidate == null) { notes.Add(ticket.Id + " · aucun relais admissible dans les déclarations"); continue; }
                _scenario.Assignments.RemoveAll(item => item.TicketKey == ticket.Key);
                _scenario.Assignments.Add(new() { TicketKey = ticket.Key, TicketId = ticket.Id, TechnicianKey = candidate.TechnicianKey, EstimatedMinutes = minutes, Note = "Relais préparé pour " + key });
                notes.Add(ticket.Id + " · " + ticket.Title + "\nRelais proposé : " + candidate.Name + "\n" + candidate.Evidence);
            }
            _handover.Text = "PASSATION PRÉPARÉE · " + (source?.Name ?? key) + "\nDate du scénario : " + (_date.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy")
                + "\nPropositions locales à examiner ; aucun changement GLPI.\n\n" + string.Join("\n\n", notes);
            RefreshProposals(); RefreshRecommendations();
        }
        catch (Exception error) { _message.Text = error.Message; }
    }
    private sealed record ProposalRow(string Key, string Id, string Technician, int Minutes, string Title);
    private static DataGrid GridWithColumns(params (string Title, string Field, double Width)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            SelectionUnit = DataGridSelectionUnit.FullRow, SelectionMode = DataGridSelectionMode.Single, FrozenColumnCount = 1 };
        foreach (var column in columns) grid.Columns.Add(new DataGridTextColumn { Header = column.Title, Binding = new Binding(column.Field) { TargetNullValue = "—" }, Width = column.Width });
        return grid;
    }
}
