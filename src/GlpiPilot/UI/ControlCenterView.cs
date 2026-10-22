using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed class ControlCenterView : UserControl
{
    private readonly DataGrid _sourcesGrid = PilotControls.Table(editable: true);
    private ObservableCollection<SourceProfile> _sources = new();
    private readonly DataGrid _operations = PilotControls.Table();
    private readonly ListBox _backups = new() { DisplayMemberPath = "Label" };
    private readonly CheckBox _scheduled = new() { Content = "Actualiser automatiquement pendant que GLPI Pilot est ouvert", Margin = new Thickness(0, 8, 0, 12) };
    private readonly CheckBox _campaign = new() { Content = "Parcourir les sources cochées dans la campagne", Margin = new Thickness(0, 4, 0, 12) };
    private readonly CheckBox _quiet = new() { Content = "Respecter les heures de tranquillité", Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _interval = PilotControls.Input("60"), _freshness = PilotControls.Input("24"), _quietStart = PilotControls.Input("20"), _quietEnd = PilotControls.Input("8");
    private readonly CheckBox _historyEnabled = new() { Content = "Conserver les relevés après acquisition", Margin = new Thickness(0, 8, 0, 12) };
    private readonly CheckBox _restoreLast = new() { Content = "Rouvrir le dernier relevé au démarrage (consultation locale)", Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _retention = PilotControls.Input("180"), _maximum = PilotControls.Input("500"), _budget = PilotControls.Input("1024");
    private readonly ComboBox _density = Choice(("compact", "Compact"), ("balanced", "Équilibré"), ("comfortable", "Confortable"));
    private readonly ComboBox _accent = Choice(("blue", "Bleu"), ("violet", "Violet"), ("green", "Vert"), ("orange", "Orange"), ("rose", "Rose"));
    private readonly CheckBox _systemTheme = new() { Content = "Suivre le thème Windows", Margin = new Thickness(0, 8, 0, 12) };
    private readonly CheckBox _contrast = new() { Content = "Renforcer le contraste", Margin = new Thickness(0, 4, 0, 12) };
    private readonly CheckBox _motion = new() { Content = "Réduire les animations", Margin = new Thickness(0, 4, 0, 12) };
    private readonly CheckBox _focus = new() { Content = "Mode concentration", Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _frozen = PilotControls.Input("2");
    private readonly TextBlock _scheduleState = PilotControls.Text("Programmation désactivée.", 12, true);
    public event Action? SaveRequested;
    public event Action<string>? RestoreSettingsRequested;
    public event Action? RefreshBackupsRequested;
    public event Action<SourceProfile>? SourceRequested;
    public event Action? CampaignRequested;
    public event Action? CurrentSourceRequested;
    public event Action? DemoRequested;
    public event Action<string>? AcknowledgeRequested;
    public event Action<string>? SnoozeRequested;

    public ControlCenterView()
    {
        var tabs = new TabControl();
        PilotControls.Column(_sourcesGrid, "Nom", "Name", 180); PilotControls.Column(_sourcesGrid, "Adresse complète de recherche", "Url", -1);
        _sourcesGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Navigateur", SelectedItemBinding = new Binding("Browser"), ItemsSource = new[] { "msedge", "chrome" }, Width = 100 });
        _sourcesGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Méthode", SelectedItemBinding = new Binding("Mode"), ItemsSource = new[] { "native", "pagination" }, Width = 105 });
        _sourcesGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Campagne", Binding = new Binding("IncludedInCampaign"), Width = 90 });
        _sourcesGrid.ItemsSource = _sources;
        var sourceActions = new WrapPanel();
        sourceActions.Children.Add(PilotControls.Button("Ajouter", () => { var item = new SourceProfile(); _sources.Add(item); _sourcesGrid.SelectedItem = item; }));
        sourceActions.Children.Add(PilotControls.Button("Mémoriser la source actuelle", () => CurrentSourceRequested?.Invoke()));
        sourceActions.Children.Add(PilotControls.Button("Utiliser cette source", UseSelected));
        sourceActions.Children.Add(PilotControls.Button("Retirer", () => { if (_sourcesGrid.SelectedItem is SourceProfile item) _sources.Remove(item); }));
        sourceActions.Children.Add(PilotControls.Button("Extraire la campagne", () => CampaignRequested?.Invoke()));
        _sourcesGrid.MouseDoubleClick += (_, _) => { if (!_sourcesGrid.IsReadOnly) return; UseSelected(); };
        var sourcePanel = new DockPanel(); DockPanel.SetDock(sourceActions, Dock.Top); sourcePanel.Children.Add(sourceActions); sourcePanel.Children.Add(_sourcesGrid);
        tabs.Items.Add(new TabItem { Header = "Sources", Content = sourcePanel });

        var automation = new StackPanel(); automation.Children.Add(_scheduled); automation.Children.Add(_campaign);
        Add(automation, "Intervalle entre les lancements (minutes, 5 à 10 080)", _interval);
        Add(automation, "Signaler un relevé ancien après (heures)", _freshness);
        automation.Children.Add(_quiet); Add(automation, "Début de tranquillité (heure locale)", _quietStart); Add(automation, "Fin de tranquillité (heure locale)", _quietEnd);
        automation.Children.Add(_scheduleState); automation.Children.Add(PilotControls.Text("Les opérations s’exécutent dans la session Windows ouverte. Si le SSO demande une action, utilisez Voir le navigateur. Une opération déjà en cours retarde le prochain lancement.", 12, true));
        tabs.Items.Add(new TabItem { Header = "Automatisations", Content = Scroll(automation) });

        var history = new StackPanel(); history.Children.Add(_historyEnabled); history.Children.Add(_restoreLast);
        Add(history, "Conservation (jours)", _retention); Add(history, "Nombre maximal de relevés", _maximum); Add(history, "Budget de stockage (Mio)", _budget);
        history.Children.Add(PilotControls.Text("Les relevés protégés et le dernier relevé de chaque source sont conservés même si le budget est atteint. Les données et sessions de navigateur restent sur ce poste.", 12, true));
        tabs.Items.Add(new TabItem { Header = "Historique", Content = Scroll(history) });

        var appearance = new StackPanel(); Add(appearance, "Densité de lecture (indépendante du zoom)", _density); Add(appearance, "Couleur d’accent", _accent);
        appearance.Children.Add(_systemTheme); appearance.Children.Add(_contrast); appearance.Children.Add(_motion); appearance.Children.Add(_focus);
        Add(appearance, "Colonnes figées à gauche du relevé (0 à 8)", _frozen);
        appearance.Children.Add(PilotControls.Button("Ouvrir une démonstration fictive", () => DemoRequested?.Invoke()));
        tabs.Items.Add(new TabItem { Header = "Présentation", Content = Scroll(appearance) });

        PilotControls.Column(_operations, "Début", "StartedAt", 165, "{0:dd/MM/yyyy HH:mm:ss}"); PilotControls.Column(_operations, "Opération", "Title", -1);
        PilotControls.Column(_operations, "État", "State", 110); PilotControls.Column(_operations, "Durée", "Duration", 90);
        PilotControls.Column(_operations, "Code", "Code", 135); PilotControls.Column(_operations, "Détail", "Detail", -1);
        var operationActions = new WrapPanel();
        operationActions.Children.Add(PilotControls.Button("Pris en compte", () => { if (_operations.SelectedItem is OperationEntry entry) AcknowledgeRequested?.Invoke(entry.Id); }));
        operationActions.Children.Add(PilotControls.Button("Rappeler dans une heure", () => { if (_operations.SelectedItem is OperationEntry entry) SnoozeRequested?.Invoke(entry.Id); }));
        var operationPanel = new DockPanel(); DockPanel.SetDock(operationActions, Dock.Top); operationPanel.Children.Add(operationActions); operationPanel.Children.Add(_operations);
        tabs.Items.Add(new TabItem { Header = "Opérations", Content = operationPanel });
        var backupActions = new WrapPanel();
        backupActions.Children.Add(PilotControls.Button("Actualiser la liste", () => RefreshBackupsRequested?.Invoke()));
        backupActions.Children.Add(PilotControls.Button("Examiner et restaurer", () => { if (_backups.SelectedItem is SettingsBackup backup) RestoreSettingsRequested?.Invoke(backup.Path); }));
        var backupPanel = new DockPanel(); DockPanel.SetDock(backupActions, Dock.Top); backupPanel.Children.Add(backupActions); backupPanel.Children.Add(_backups);
        tabs.Items.Add(new TabItem { Header = "Réglages sauvegardés", Content = backupPanel });
        var toolbar = new WrapPanel(); toolbar.Children.Add(PilotControls.Button("Enregistrer les réglages", () => SaveRequested?.Invoke()));
        Content = PilotControls.Page("Sources et organisation", "Préparez vos recherches, programmez les collectes et retrouvez le résultat des opérations.", tabs, toolbar);
    }

    public void Load(AppSettings settings)
    {
        _sources = new(settings.Sources.Select(source => source.Copy())); _sourcesGrid.ItemsSource = _sources;
        var a = settings.Automation; _scheduled.IsChecked = a.ScheduledExtraction; _campaign.IsChecked = a.RunCampaign; _quiet.IsChecked = a.QuietHours;
        _interval.Text = a.IntervalMinutes.ToString(); _freshness.Text = a.FreshnessHours.ToString(); _quietStart.Text = a.QuietStartHour.ToString(); _quietEnd.Text = a.QuietEndHour.ToString();
        var h = settings.History; _historyEnabled.IsChecked = h.Enabled; _restoreLast.IsChecked = h.RestoreLastOnStartup;
        _retention.Text = h.RetentionDays.ToString(); _maximum.Text = h.MaximumSnapshots.ToString(); _budget.Text = h.StorageBudgetMb.ToString();
        var i = settings.Interface; _density.SelectedValue = i.Density; _accent.SelectedValue = i.Accent; _systemTheme.IsChecked = i.FollowWindowsTheme;
        _contrast.IsChecked = i.HighContrast; _motion.IsChecked = i.ReduceMotion; _focus.IsChecked = i.FocusMode; _frozen.Text = i.FrozenColumns.ToString();
    }
    public void ApplyTo(AppSettings settings)
    {
        _sourcesGrid.CommitEdit(DataGridEditingUnit.Cell, true); _sourcesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_sources.Count > 100) throw new InvalidOperationException("Conservez au maximum 100 sources.");
        foreach (var source in _sources)
        { if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 120) throw new InvalidOperationException("Chaque source doit avoir un nom de 1 à 120 caractères."); _ = TargetUrlPolicy.Validate(source.Url); }
        settings.Sources = _sources.Select(source => source.Copy()).ToList();
        settings.Automation.ScheduledExtraction = _scheduled.IsChecked == true; settings.Automation.RunCampaign = _campaign.IsChecked == true; settings.Automation.QuietHours = _quiet.IsChecked == true;
        settings.Automation.IntervalMinutes = Number(_interval, 5, 10080); settings.Automation.FreshnessHours = Number(_freshness, 1, 720);
        settings.Automation.QuietStartHour = Number(_quietStart, 0, 23); settings.Automation.QuietEndHour = Number(_quietEnd, 0, 23);
        settings.History.Enabled = _historyEnabled.IsChecked == true; settings.History.RestoreLastOnStartup = _restoreLast.IsChecked == true;
        settings.History.RetentionDays = Number(_retention, 7, 3650); settings.History.MaximumSnapshots = Number(_maximum, 2, 5000); settings.History.StorageBudgetMb = Number(_budget, 32, 32768);
        settings.Interface.Density = _density.SelectedValue?.ToString() ?? "balanced"; settings.Interface.Accent = _accent.SelectedValue?.ToString() ?? "blue";
        settings.Interface.FollowWindowsTheme = _systemTheme.IsChecked == true; settings.Interface.HighContrast = _contrast.IsChecked == true;
        settings.Interface.ReduceMotion = _motion.IsChecked == true; settings.Interface.FocusMode = _focus.IsChecked == true; settings.Interface.FrozenColumns = Number(_frozen, 0, 8);
    }
    public void AddSource(SourceProfile source) { _sources.Add(source); _sourcesGrid.SelectedItem = source; }
    public void ShowOperations(IReadOnlyList<OperationEntry> entries) => _operations.ItemsSource = entries;
    public void ShowBackups(IReadOnlyList<SettingsBackup> backups) => _backups.ItemsSource = backups;
    public void ShowSchedule(string text) => _scheduleState.Text = text;
    private void UseSelected()
    { _sourcesGrid.CommitEdit(); if (_sourcesGrid.SelectedItem is SourceProfile source) SourceRequested?.Invoke(source.Copy()); }
    private static void Add(Panel panel, string label, UIElement control) { panel.Children.Add(PilotControls.Text(label, 12, true)); panel.Children.Add(control); }
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(4, 8, 18, 8) };
    private static int Number(TextBox box, int minimum, int maximum)
    { if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value < minimum || value > maximum) throw new InvalidOperationException($"Indiquez une valeur comprise entre {minimum} et {maximum}."); return value; }
    private static ComboBox Choice(params (string Key, string Label)[] values) => new() { ItemsSource = values.Select(value => new SettingChoice(value.Key, value.Label)).ToArray(), SelectedValuePath = "Key", DisplayMemberPath = "Label", SelectedIndex = 0, Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
    private sealed record SettingChoice(string Key, string Label);
}
