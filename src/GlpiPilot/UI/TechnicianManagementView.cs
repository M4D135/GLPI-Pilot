using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Read-only assignment comparison with explicit, locally saved planning preferences.</summary>
public sealed class TechnicianManagementView : UserControl
{
    private readonly TextBlock _summary = Label("Extrayez un relevé pour comparer les charges visibles.", 18, "TextBrush", FontWeights.SemiBold);
    private readonly TextBlock _scope = Label("", 12, "MutedBrush");
    private readonly TextBlock _status = Label("", 12, "MutedBrush");
    private readonly TextBlock _count = Label("", 12, "MutedBrush");
    private readonly TextBlock _selectionTitle = Label("Sélectionnez un technicien", 18, "TextBrush", FontWeights.SemiBold);
    private readonly TextBlock _selectionCoverage = Label("", 12, "MutedBrush");
    private readonly TextBlock _loadNote = Label("Aucun objectif local défini.", 12, "MutedBrush");
    private readonly TextBox _search = new() { Width = 140, Margin = new Thickness(0, 0, 12, 0), MaxLength = 400 };
    private readonly ComboBox _filter = new() { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, MinHeight = 0,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserAddRows = false,
        CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
        RowHeight = 38, FrozenColumnCount = 2, Margin = new Thickness(0, 8, 0, 0)
    };
    private readonly CheckBox _favorite = new() { Margin = new Thickness(0, 0, 20, 0) };
    private readonly TextBox _target = new() { Width = 105, MaxLength = 6, Margin = new Thickness(8, 0, 12, 0) };
    private readonly TextBox _notes = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxLength = TechnicianPreferences.MaximumNotesLength,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 6, 0, 10)
    };
    private readonly TextBox _skills = new() { MaxLength = 8050, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _primaryTerritories = new() { MaxLength = 8050, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _backupTerritories = new() { MaxLength = 8050, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _capacityMinutes = new() { Width = 110, MaxLength = 4 };
    private readonly TextBox _estimatedMinutes = new() { Width = 110, MaxLength = 5 };
    private readonly DatePicker _unavailableFrom = new() { Width = 150 };
    private readonly DatePicker _unavailableUntil = new() { Width = 150 };
    private readonly TextBox _availabilityNote = new() { MaxLength = 1200, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _metricActions = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly List<Button> _metricButtons = new();
    private readonly Button _refresh, _dashboard, _save, _unassigned, _multiple;
    // The workspace adds its dashboard and identity pages to this navigation instead
    // of nesting a second row of tabs above a height-constrained comparison table.
    internal TabControl Navigation { get; } = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    internal TabItem ComparisonSection { get; }
    private ICollectionView? _view;
    private TechnicianWorkloadSnapshot? _snapshot;
    private TechnicianPreferences _preferences = new();
    private bool _busy, _loading, _rebuildingRows;
    private string _preferenceEditingKey = "";

    public event Action<string>? DashboardRequested;
    public event Action<int[], string>? RowsRequested;
    public event Action<string, TechnicianPreference>? PreferenceSaveRequested;
    public event Action? RefreshRequested;

    public TechnicianManagementView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var sections = Navigation;
        AutomationProperties.SetAutomationId(sections, "TechnicianManagementTabs");
        var comparison = new Grid();
        comparison.RowDefinitions.Add(new() { Height = GridLength.Auto });
        comparison.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        ComparisonSection = new TabItem { Header = "Comparaison", Content = comparison };
        sections.Items.Add(ComparisonSection);
        var selectedTab = new TabItem { Header = "Fiche", ToolTip = "Fiche du technicien sélectionné et repères enregistrés sur ce poste" }; sections.Items.Add(selectedTab);
        root.Children.Add(sections);
        var intro = new StackPanel();
        intro.Children.Add(_summary);
        _scope.Margin = new Thickness(0, 8, 0, 0); intro.Children.Add(_scope);
        intro.Children.Add(Label("Charge visible dans ce relevé. Un ticket partagé figure chez chaque technicien concerné ; les colonnes des techniciens ne s’additionnent pas. Les objectifs sont des repères locaux modifiables.", 12, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        intro.Children.Add(Label("Dans le tableau, cliquez sur un en-tête pour trier puis sélectionnez une ligne. L’onglet Fiche permet de consulter ses tickets et ses repères locaux ; un double-clic sur la ligne ouvre son dashboard.", 12, "MutedBrush", margin: new Thickness(0, 8, 0, 0)));
        sections.Items.Add(new TabItem { Header = "Périmètre", Content = Viewport(Card(intro)) });

        var comparisonHeader = new DockPanel();
        _count.Margin = new Thickness(12, 0, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(_count, Dock.Right); comparisonHeader.Children.Add(_count);
        var toolbar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(Label("Nom", 12, "MutedBrush", margin: new Thickness(0, 0, 6, 0)));
        AutomationProperties.SetName(_search, "Rechercher un technicien dans la gestion de l’équipe");
        toolbar.Children.Add(_search);
        _filter.ItemsSource = new[] { "Tous les techniciens", "Avec actifs reconnus", "Favoris locaux" };
        _filter.SelectedIndex = 0;
        AutomationProperties.SetName(_filter, "Filtre du tableau des techniciens");
        toolbar.Children.Add(_filter);
        _refresh = Command("Actualiser les calculs", () => RefreshRequested?.Invoke());
        _refresh.Content = "↻"; _refresh.ToolTip = "Actualiser les calculs";
        _refresh.Margin = new Thickness(0); toolbar.Children.Add(_refresh);
        comparisonHeader.Children.Add(toolbar); comparison.Children.Add(comparisonHeader);

        AddColumn("★", nameof(ComparisonRow.FavoriteMark), 44, nameof(ComparisonRow.Favorite), "Favori enregistré sur ce poste.");
        AddColumn("Technicien", nameof(ComparisonRow.Name), 210);
        AddColumn("Relevé", nameof(ComparisonRow.TotalCount), 90, format: "N0");
        AddColumn("Actifs", nameof(ComparisonRow.ActiveCount), 86, format: "N0", tooltip: "Statuts reconnus : nouveau, en cours ou en attente. Les statuts inconnus sont comptés séparément.");
        AddColumn("En attente", nameof(ComparisonRow.PendingCount), 105, format: "N0");
        AddColumn("Priorité haute", nameof(ComparisonRow.HighCount), 120, format: "N0");
        AddColumn("Échéance dépassée", nameof(ComparisonRow.OverdueCount), 160, format: "N0");
        AddColumn("Anciens", nameof(ComparisonRow.OldCount), 90, format: "N0", tooltip: "Actifs dont l’âge atteint le seuil du dashboard. Détail et couverture en sélectionnant la ligne.");
        AddColumn("Sans modif. récente", nameof(ComparisonRow.StaleCount), 165, format: "N0", tooltip: "Date de dernière modification selon le seuil configuré ; aucune conclusion sur le travail de la personne.");
        AddColumn("Âge moyen · j", nameof(ComparisonRow.MeanAgeDays), 130, format: "N1");
        AddColumn("Dernière modif. · j", nameof(ComparisonRow.MeanInactivityDays), 160, format: "N1", tooltip: "Moyenne calendaire sur les actifs dont la date est lisible, depuis leur dernière modification.");
        AddColumn("Statuts inconnus", nameof(ComparisonRow.UnknownStatusCount), 140, format: "N0");
        AddColumn("Multi-affectés", nameof(ComparisonRow.MultipleAssignmentCount), 125, format: "N0");
        AddColumn("Objectif local", nameof(ComparisonRow.ActiveTarget), 120, format: "N0");
        AddColumn("Actifs / objectif", nameof(ComparisonRow.LoadLabel), 145, nameof(ComparisonRow.LoadPercent),
            "Actifs reconnus / objectif choisi sur ce poste. Un astérisque signale une source partielle ou des statuts inconnus. Ce ratio n’est pas une mesure de performance.");
        AddColumn("Compétences déclarées", nameof(ComparisonRow.Skills), 200);
        AddColumn("Secteurs déclarés", nameof(ComparisonRow.Territories), 210);
        AddColumn("Capacité · min/j", nameof(ComparisonRow.CapacityMinutes), 130, format: "N0");
        AddColumn("Charge estimée · min", nameof(ComparisonRow.EstimatedLoadMinutes), 160, format: "N0", tooltip: "Estimation locale uniforme : actifs reconnus × estimation par ticket. Aucun temps de travail réel n’est déduit.");
        VirtualizingPanel.SetIsVirtualizing(_grid, true);
        VirtualizingPanel.SetVirtualizationMode(_grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_grid, true);
        AutomationProperties.SetName(_grid, "Comparaison des charges des techniciens, triable par en-tête");
        Grid.SetRow(_grid, 1); comparison.Children.Add(_grid);
        var overviewActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _unassigned = Command("Voir les lignes sans technicien", () =>
        { if (_snapshot != null) RowsRequested?.Invoke(_snapshot.UnassignedRows, "Sans technicien renseigné · tous statuts"); });
        _multiple = Command("Voir les affectations multiples", () =>
        { if (_snapshot != null) RowsRequested?.Invoke(_snapshot.MultipleAssignmentRows, "Affectations multiples · une ligne source affichée une fois"); });
        _unassigned.Content = "Sans technicien"; _unassigned.ToolTip = "Voir les lignes sans technicien";
        _multiple.Content = "Multi-affectés"; _multiple.ToolTip = "Voir les affectations multiples";
        overviewActions.Children.Add(_unassigned); overviewActions.Children.Add(_multiple);
        intro.Children.Add(overviewActions);
        _status.Margin = new Thickness(0, 8, 0, 0); _status.Visibility = Visibility.Collapsed;
        Grid.SetRow(_status, 1); root.Children.Add(_status);

        var selected = new StackPanel();
        var selectedHeading = new DockPanel();
        _dashboard = Command("Ouvrir le dashboard", OpenSelectedDashboard);
        DockPanel.SetDock(_dashboard, Dock.Right); selectedHeading.Children.Add(_dashboard);
        selectedHeading.Children.Add(_selectionTitle); selected.Children.Add(selectedHeading);
        _selectionCoverage.Margin = new Thickness(0, 8, 0, 0); selected.Children.Add(_selectionCoverage);
        selected.Children.Add(_metricActions);
        selected.Children.Add(Label("REPÈRES SUR CE POSTE", 11, "MutedBrush", FontWeights.SemiBold));
        var preferenceRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        _favorite.Content = Label("Favori", 13, "TextBrush");
        preferenceRow.Children.Add(_favorite);
        preferenceRow.Children.Add(Label("Objectif d’actifs", 12, "MutedBrush"));
        _target.ToolTip = $"Repère local de 1 à {TechnicianPreferences.MaximumActiveTarget:N0} actifs. Laissez vide pour ne pas afficher de taux de charge.";
        AutomationProperties.SetName(_target, "Objectif local de tickets actifs pour le technicien sélectionné");
        preferenceRow.Children.Add(_target); preferenceRow.Children.Add(Label("Vide = non défini", 12, "MutedBrush"));
        selected.Children.Add(preferenceRow); selected.Children.Add(_loadNote);
        selected.Children.Add(Label($"Note locale facultative · {TechnicianPreferences.MaximumNotesLength:N0} caractères maximum", 12, "MutedBrush", margin: new Thickness(0, 12, 0, 0)));
        AutomationProperties.SetName(_notes, "Note locale du technicien sélectionné"); selected.Children.Add(_notes);
        selected.Children.Add(Label("COMPÉTENCES ET COUVERTURE DÉCLARÉES", 11, "MutedBrush", FontWeights.SemiBold, new Thickness(0, 16, 0, 4)));
        selected.Children.Add(Label("Compétences / habilitations · séparer par un point-virgule", 12, "MutedBrush")); selected.Children.Add(_skills);
        selected.Children.Add(Label("Secteurs principaux · noms de villes ou de sites", 12, "MutedBrush", margin: new Thickness(0, 8, 0, 4))); selected.Children.Add(_primaryTerritories);
        selected.Children.Add(Label("Secteurs de remplacement", 12, "MutedBrush", margin: new Thickness(0, 8, 0, 4))); selected.Children.Add(_backupTerritories);
        var capacity = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        capacity.Children.Add(Label("Capacité déclarée · min/jour", 12, "MutedBrush")); capacity.Children.Add(_capacityMinutes);
        capacity.Children.Add(Label("Estimation par ticket actif · min", 12, "MutedBrush", margin: new Thickness(12, 0, 6, 0))); capacity.Children.Add(_estimatedMinutes); selected.Children.Add(capacity);
        var absence = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        absence.Children.Add(Label("Indisponibilité du", 12, "MutedBrush")); absence.Children.Add(_unavailableFrom);
        absence.Children.Add(Label("au", 12, "MutedBrush", margin: new Thickness(8, 0, 8, 0))); absence.Children.Add(_unavailableUntil);
        absence.Children.Add(Command("Effacer l’indisponibilité", () => _unavailableFrom.SelectedDate = _unavailableUntil.SelectedDate = null)); selected.Children.Add(absence);
        selected.Children.Add(Label("Précisions de disponibilité / horaires", 12, "MutedBrush")); selected.Children.Add(_availabilityNote);
        selected.Children.Add(Label("Ces déclarations servent uniquement à la simulation. Une absence non renseignée ne confirme pas la présence ; la charge en minutes est une estimation choisie.", 12, "MutedBrush", margin: new Thickness(0, 8, 0, 12)));
        _save = Command("Enregistrer les repères", SaveSelectedPreference);
        selected.Children.Add(_save);
        selected.Children.Add(Label("Ces réglages restent dans GLPI Pilot sur ce poste. Le ratio décrit le stock présent dans le relevé ; il ne décrit ni présence, ni capacité disponible, ni productivité.", 12, "MutedBrush", margin: new Thickness(0, 10, 0, 0)));
        selectedTab.Content = Viewport(Card(selected));
        Content = root;

        // All controls exist before callbacks can run, including initial selection changes.
        _search.TextChanged += (_, _) => ApplyFilter();
        _filter.SelectionChanged += (_, _) => ApplyFilter();
        _grid.SelectionChanged += (_, _) => { if (!_rebuildingRows) ShowSelection(); };
        _grid.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(_grid, element) is DataGridRow)
                OpenSelectedDashboard();
        };
        _grid.KeyDown += (_, args) => { if (args.Key == Key.Enter) { OpenSelectedDashboard(); args.Handled = true; } };
        UpdateEnabled();
    }

    public void Show(TechnicianWorkloadSnapshot snapshot, TechnicianPreferences preferences)
    {
        var selectedKey = (_grid.SelectedItem as ComparisonRow)?.Key;
        _snapshot = snapshot; _preferences = preferences.Copy(); _loading = false;
        _summary.Text = $"{snapshot.Technicians.Count:N0} techniciens · {snapshot.Global.RowCount:N0} {snapshot.Global.Grain} dans le relevé";
        _scope.Text = snapshot.Roster.Available
            ? $"{snapshot.AssignedRows.Length:N0} {snapshot.Global.Grain} avec au moins un technicien · {snapshot.AssignedActiveRows.Length:N0} actifs reconnus affectés · {snapshot.MultipleAssignmentRows.Length:N0} multi-affectés · calcul {snapshot.Global.CalculatedAt:HH:mm}"
            : snapshot.Roster.Message;
        SetMessage(!snapshot.Global.Complete
            ? "Relevé incomplet ou non confirmé : les charges portent uniquement sur les lignes récupérées."
            : "", !snapshot.Global.Complete);
        RebuildRows(selectedKey); UpdateEnabled();
    }

    public void ShowLoading()
    {
        _loading = true;
        SetMessage(_snapshot == null ? "Calcul des charges et de la couverture du relevé…"
            : "Actualisation en cours. La comparaison précédente reste affichée jusqu’à la fin du calcul.");
        UpdateEnabled();
    }

    public void ShowRefreshError(string message)
    {
        _loading = true; SetMessage(message, true); UpdateEnabled();
    }

    public void ShowEmpty(string message)
    {
        _snapshot = null; _view = null; _grid.ItemsSource = null; _loading = false;
        _summary.Text = "Gestion de l’équipe"; _scope.Text = message; _count.Text = "";
        SetMessage(message); ShowSelection(); UpdateEnabled();
    }

    public void SetPreferences(TechnicianPreferences preferences, string message, bool warning = false)
    {
        _preferences = preferences.Copy();
        RebuildRows((_grid.SelectedItem as ComparisonRow)?.Key, reloadPreference: true);
        SetMessage(message, warning);
    }

    public void ShowPreferenceSaveError(string message) => SetMessage(message, warning: true);

    public void SetSelectedKey(string key)
    {
        if (_view == null) return;
        var row = _view.Cast<ComparisonRow>().FirstOrDefault(item => item.Key == key);
        if (row != null) _grid.SelectedItem = row;
    }

    public void SetBusy(bool busy) { _busy = busy; UpdateEnabled(); }

    private void RebuildRows(string? selectedKey, bool reloadPreference = false)
    {
        if (_snapshot == null) return;
        var sorts = _view?.SortDescriptions.ToArray() ?? new[]
        {
            new SortDescription(nameof(ComparisonRow.Favorite), ListSortDirection.Descending),
            new SortDescription(nameof(ComparisonRow.Name), ListSortDirection.Ascending)
        };
        _view = new ListCollectionView(_snapshot.Technicians.Select(entry => new ComparisonRow(entry,
            _preferences.Get(entry.Key), !_snapshot.Global.Complete)).ToArray());
        using (_view.DeferRefresh())
        {
            _view.Filter = MatchesFilter;
            foreach (var sort in sorts) _view.SortDescriptions.Add(sort);
        }
        _rebuildingRows = true;
        try
        {
            _grid.ItemsSource = _view;
            _grid.SelectedItem = _view.Cast<ComparisonRow>().FirstOrDefault(row => row.Key == selectedKey)
                ?? _view.Cast<ComparisonRow>().FirstOrDefault();
        }
        finally { _rebuildingRows = false; }
        UpdateCount(); ShowSelection(reloadPreference);
    }

    private bool MatchesFilter(object value)
    {
        if (value is not ComparisonRow row) return false;
        var query = _search.Text.Trim();
        return (_filter.SelectedIndex != 1 || row.ActiveCount > 0)
            && (_filter.SelectedIndex != 2 || row.Favorite)
            && (query.Length == 0 || CultureInfo.CurrentCulture.CompareInfo.IndexOf(row.Name + " " + row.Skills + " " + row.Territories, query,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    private void ApplyFilter()
    {
        var selectedKey = (_grid.SelectedItem as ComparisonRow)?.Key;
        _rebuildingRows = true;
        try
        {
            _view?.Refresh();
            _grid.SelectedItem = _view?.Cast<ComparisonRow>().FirstOrDefault(row => row.Key == selectedKey);
        }
        finally { _rebuildingRows = false; }
        UpdateCount();
        ShowSelection();
    }

    private void UpdateCount() => _count.Text = _snapshot == null ? ""
        : $"{_view?.Cast<object>().Count() ?? 0:N0} / {_snapshot.Technicians.Count:N0} techniciens";

    private void ShowSelection(bool reloadPreference = false)
    {
        _metricActions.Children.Clear(); _metricButtons.Clear();
        if (_grid.SelectedItem is not ComparisonRow row)
        {
            _selectionTitle.Text = "Sélectionnez un technicien"; _selectionCoverage.Text = "";
            _preferenceEditingKey = "";
            _favorite.IsChecked = false; _target.Text = ""; _notes.Text = ""; _loadNote.Text = "Aucun technicien sélectionné.";
            UpdateEnabled(); return;
        }
        _selectionTitle.Text = row.Name;
        _selectionCoverage.Text = $"{row.TotalCount:N0} {_snapshot?.Global.Grain ?? "lignes"} dans ce périmètre · {row.UnknownStatusCount:N0} statuts inconnus · {row.MultipleAssignmentCount:N0} lignes partagées avec d’autres techniciens."
            + " Les valeurs — signalent un champ absent ou aucun actif évaluable pour ce champ.";
        foreach (var key in new[] { "total", "active", "pending", "high", "overdue", "soon", "old", "stale" })
        {
            var metric = row.Entry.Measure(key);
            var label = key == "total" ? "Tout le périmètre" : metric.Label;
            var button = Command(label + " · " + (metric.Count?.ToString("N0") ?? "—"), () =>
                RowsRequested?.Invoke(metric.Rows, row.Name + " · " + label));
            button.Tag = metric.Count is > 0;
            button.ToolTip = metric.Explanation + "\n" + metric.Coverage + ".";
            button.Margin = new Thickness(0, 0, 8, 8);
            _metricButtons.Add(button); _metricActions.Children.Add(button);
        }
        if (reloadPreference || _preferenceEditingKey != row.Key)
        {
            _preferenceEditingKey = row.Key;
            _favorite.IsChecked = row.Favorite;
            _target.Text = row.ActiveTarget?.ToString(CultureInfo.InvariantCulture) ?? "";
            _notes.Text = row.Preference.Notes;
            _skills.Text = string.Join(" ; ", row.Preference.Skills);
            _primaryTerritories.Text = string.Join(" ; ", row.Preference.PrimaryTerritories);
            _backupTerritories.Text = string.Join(" ; ", row.Preference.BackupTerritories);
            _capacityMinutes.Text = row.Preference.DailyCapacityMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
            _estimatedMinutes.Text = row.Preference.EstimatedTicketMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
            _unavailableFrom.SelectedDate = row.Preference.UnavailableFrom; _unavailableUntil.SelectedDate = row.Preference.UnavailableUntil;
            _availabilityNote.Text = row.Preference.AvailabilityNote;
        }
        _loadNote.Text = row.LoadExplanation;
        UpdateEnabled();
    }

    private void SaveSelectedPreference()
    {
        if (_busy || _loading || _grid.SelectedItem is not ComparisonRow row) return;
        int? target = null;
        if (_target.Text.Trim().Length > 0)
        {
            if (!int.TryParse(_target.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < 1 || value > TechnicianPreferences.MaximumActiveTarget)
            { SetMessage($"Indiquez un objectif de 1 à {TechnicianPreferences.MaximumActiveTarget:N0} actifs, ou laissez le champ vide.", true); return; }
            target = value;
        }
        try
        {
            int? Minutes(TextBox field, int maximum, bool allowZero)
            {
                if (string.IsNullOrWhiteSpace(field.Text)) return null;
                if (!int.TryParse(field.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < (allowZero ? 0 : 1) || value > maximum)
                    throw new InvalidOperationException($"Indiquez un nombre de minutes compris entre {(allowZero ? 0 : 1)} et {maximum:N0}, ou laissez vide.");
                return value;
            }
            var preference = new TechnicianPreference
            {
                Favorite = _favorite.IsChecked == true, ActiveTarget = target, Notes = _notes.Text,
                Skills = _skills.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                PrimaryTerritories = _primaryTerritories.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                BackupTerritories = _backupTerritories.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                DailyCapacityMinutes = Minutes(_capacityMinutes, 1440, true), EstimatedTicketMinutes = Minutes(_estimatedMinutes, 10080, false),
                UnavailableFrom = _unavailableFrom.SelectedDate, UnavailableUntil = _unavailableUntil.SelectedDate, AvailabilityNote = _availabilityNote.Text
            };
            _ = _preferences.With(row.Key, preference);
            PreferenceSaveRequested?.Invoke(row.Key, preference);
        }
        catch (Exception error) { SetMessage(error.Message, true); }
    }

    private void OpenSelectedDashboard()
    {
        if (!_busy && !_loading && _grid.SelectedItem is ComparisonRow row) DashboardRequested?.Invoke(row.Key);
    }

    private void SetMessage(string message, bool warning = false)
    {
        _status.Text = message;
        _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "MutedBrush");
    }

    private void UpdateEnabled()
    {
        var available = !_busy && !_loading && _snapshot?.Roster.Available == true;
        _grid.IsEnabled = !_busy && !_loading;
        _search.IsEnabled = _filter.IsEnabled = !_busy && _snapshot != null;
        _refresh.IsEnabled = !_busy;
        var selected = available && _grid.SelectedItem is ComparisonRow;
        _dashboard.IsEnabled = _save.IsEnabled = _favorite.IsEnabled = _target.IsEnabled = _notes.IsEnabled = selected;
        _skills.IsEnabled = _primaryTerritories.IsEnabled = _backupTerritories.IsEnabled = _capacityMinutes.IsEnabled = _estimatedMinutes.IsEnabled
            = _unavailableFrom.IsEnabled = _unavailableUntil.IsEnabled = _availabilityNote.IsEnabled = selected;
        _unassigned.IsEnabled = available && _snapshot!.UnassignedRows.Length > 0;
        _multiple.IsEnabled = available && _snapshot!.MultipleAssignmentRows.Length > 0;
        foreach (var button in _metricButtons) button.IsEnabled = selected && button.Tag is true;
    }

    private void AddColumn(string title, string path, double width, string? sort = null, string? tooltip = null, string? format = null)
    {
        var header = Label(title, 12, "HeaderTextBrush", FontWeights.SemiBold);
        header.ToolTip = tooltip ?? title;
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header, Binding = new Binding(path) { Mode = BindingMode.OneWay, TargetNullValue = "—", StringFormat = format },
            SortMemberPath = sort ?? path, Width = width, MinWidth = 44, IsReadOnly = true
        });
    }

    private static Button Command(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7), FontSize = 12, MinHeight = 36 };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => action(); return button;
    }
    private static TextBlock Label(string text, double size, string brush, FontWeight? weight = null, Thickness? margin = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush); return label;
    }
    private static Border Card(UIElement content)
    {
        var border = new Border { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    private static ScrollViewer Viewport(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        Padding = new Thickness(0, 4, 8, 8)
    };

    public sealed class ComparisonRow
    {
        public ComparisonRow(TechnicianWorkloadEntry entry, TechnicianPreference preference, bool partialSource)
        { Entry = entry; Preference = preference; PartialSource = partialSource; }
        public TechnicianWorkloadEntry Entry { get; }
        public TechnicianPreference Preference { get; }
        public bool PartialSource { get; }
        public string Key => Entry.Key;
        public string Name => Entry.Name;
        public bool Favorite => Preference.Favorite;
        public string FavoriteMark => Favorite ? "★" : "";
        public int TotalCount => Entry.Rows.Length;
        public int? ActiveCount => Entry.ActiveCount;
        public int? PendingCount => Entry.PendingCount;
        public int? HighCount => Entry.HighCount;
        public int? OverdueCount => Entry.OverdueCount;
        public int? OldCount => Entry.OldCount;
        public int? StaleCount => Entry.StaleCount;
        public double? MeanAgeDays => Entry.MeanAgeDays;
        public double? MeanInactivityDays => Entry.MeanInactivityDays;
        public int UnknownStatusCount => Entry.UnknownStatusCount;
        public int MultipleAssignmentCount => Entry.MultipleAssignmentCount;
        public int? ActiveTarget => Preference.ActiveTarget;
        public string Skills => string.Join(" ; ", Preference.Skills);
        public string Territories => string.Join(" ; ", Preference.PrimaryTerritories) + (Preference.BackupTerritories.Count > 0 ? " · relais : " + string.Join(" ; ", Preference.BackupTerritories) : "");
        public int? CapacityMinutes => Preference.DailyCapacityMinutes;
        public double? EstimatedLoadMinutes => ActiveCount.HasValue && Preference.EstimatedTicketMinutes.HasValue ? ActiveCount.Value * (double)Preference.EstimatedTicketMinutes.Value : null;
        public double? LoadPercent => TechnicianWorkloadService.LoadPercentage(Entry, ActiveTarget);
        public string LoadLabel => LoadPercent.HasValue ? LoadPercent.Value.ToString("N0") + " %" + (PartialSource || UnknownStatusCount > 0 ? " *" : "") : "—";
        public string LoadExplanation => !ActiveTarget.HasValue ? "Aucun objectif local défini : aucun taux de charge n’est calculé."
            : !ActiveCount.HasValue ? $"Objectif local : {ActiveTarget.Value:N0} actifs. Statuts indisponibles, ratio non calculable."
            : $"{ActiveCount.Value:N0} actifs reconnus / objectif local de {ActiveTarget.Value:N0} = {LoadPercent:N1} %."
                + (UnknownStatusCount > 0 ? $" {UnknownStatusCount:N0} statuts inconnus exclus du numérateur." : "")
                + (PartialSource ? " Source partielle ou non confirmée : le stock réel peut être plus élevé." : "");
    }
}
