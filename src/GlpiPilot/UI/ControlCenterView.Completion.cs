using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class ControlCenterView
{
    private readonly TextBox _fixedTimes = PilotControls.Input(""), _weekDays = PilotControls.Input("1,2,3,4,5"), _failureLimit = PilotControls.Input("3");
    private readonly TextBox _listTimeout = PilotControls.Input("45"), _detailTimeout = PilotControls.Input("30"), _reconnectAttempts = PilotControls.Input("2"), _freeSpace = PilotControls.Input("256"), _checkpointAge = PilotControls.Input("120");
    private readonly CheckBox _resume = new() { Content = "Réutiliser un point de pagination après contrôle du périmètre" }, _reconnect = new() { Content = "Reprendre la programmation au retour du réseau ou de veille" }, _useRecipe = new() { Content = "Utiliser cette recette pour la programmation hors campagne" };
    private readonly CheckBox _windowsMotion = new() { Content = "Respecter aussi la réduction des animations de Windows" };
    private readonly ComboBox _colorVision = Choice(("standard", "Palette standard"), ("deuteranopia", "Deutéranopie · bleu / orange"), ("protanopia", "Protanopie · bleu / jaune"), ("tritanopia", "Tritanopie · rose / vert"));
    private readonly ComboBox _monitorChoice = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id", MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _health = PilotControls.Text("", 12, true);
    private readonly DataGrid _recipeGrid = PilotControls.Table(editable: true), _campaignResults = PilotControls.Table();
    private ObservableCollection<AutomationRecipeStep> _steps = new();
    public event Action<string>? CaptureSourceRequested;
    public event Action? RecipeRequested, NavigationRequested, MappingRequested, OnboardingRequested, DiagnosticRequested, UpdateRequested, AssistanceRequested;

    private void AddCompletionTabs(TabControl tabs)
    {
        var acquisition = new StackPanel();
        Add(acquisition, "Attente de chargement d’une liste (secondes, 5 à 600)", _listTimeout);
        Add(acquisition, "Attente d’une fiche détaillée (secondes, 5 à 600)", _detailTimeout);
        Add(acquisition, "Tentatives de reconnexion pendant la pagination (0 à 5)", _reconnectAttempts);
        Add(acquisition, "Espace libre minimal avant une collecte (Mio)", _freeSpace);
        acquisition.Children.Add(_resume); Add(acquisition, "Âge maximal d’un point de reprise (minutes)", _checkpointAge);
        acquisition.Children.Add(PilotControls.Text("Les étapes SSO et téléchargement gardent leurs délais dans Réglages. Les repères et formats sont conservés par profil avec « Actualiser le profil sélectionné ».", 12, true));
        acquisition.Children.Add(PilotControls.Button("Assistant de correspondance et de formats", () => MappingRequested?.Invoke()));
        tabs.Items.Add(new TabItem { Header = "Collecte et formats", Content = Scroll(acquisition) });

        var schedule = new StackPanel(); Add(schedule, "Heures fixes locales séparées par une virgule (ex. 08:30,13:30 ; vide = intervalle)", _fixedTimes);
        Add(schedule, "Jours actifs pour les heures fixes (1 = lundi … 7 = dimanche)", _weekDays);
        Add(schedule, "Alerter après ce nombre d’échecs consécutifs", _failureLimit); schedule.Children.Add(_reconnect); schedule.Children.Add(_useRecipe); schedule.Children.Add(_health);
        _recipeGrid.AutoGenerateColumns = false; _recipeGrid.Height = 270; _recipeGrid.ItemsSource = _steps;
        _recipeGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Active", Binding = new Binding("Enabled"), Width = 65 });
        _recipeGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Étape", SelectedItemBinding = new Binding("Kind"), ItemsSource = new[] { "extract", "quality", "enrich", "archive", "csv", "xlsx", "alerts" }, Width = 130 });
        _recipeGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Condition", SelectedItemBinding = new Binding("Condition"), ItemsSource = new[] { "always", "complete", "incomplete" }, Width = 130 });
        PilotControls.Column(_recipeGrid, "Minimum de lignes", "MinimumRows", 150);
        schedule.Children.Add(PilotControls.Text("Étapes : extraire, contrôler, enrichir les lieux, archiver, exporter CSV/XLSX, évaluer les alertes. La première étape active doit être extract, sans condition. Un reçu brut reste conservé à l’extraction.", 12, true));
        schedule.Children.Add(_recipeGrid);
        var recipeActions = new WrapPanel();
        recipeActions.Children.Add(PilotControls.Button("Ajouter une étape", () => { if (_steps.Count < 20) _steps.Add(new()); }));
        recipeActions.Children.Add(PilotControls.Button("Monter", () => MoveStep(-1))); recipeActions.Children.Add(PilotControls.Button("Descendre", () => MoveStep(1)));
        recipeActions.Children.Add(PilotControls.Button("Retirer", () => { if (_recipeGrid.SelectedItem is AutomationRecipeStep item) _steps.Remove(item); }));
        recipeActions.Children.Add(PilotControls.Button("Exécuter la recette", () => RecipeRequested?.Invoke())); schedule.Children.Add(recipeActions);
        tabs.Items.Add(new TabItem { Header = "Horaires et recettes", Content = Scroll(schedule) });

        foreach (var c in new[] { ("Source", "Name", 180d), ("État", "State", 120d), ("Lignes", "Rows", 90d), ("Détail", "Detail", -1d), ("Fichier", "Path", -1d) }) PilotControls.Column(_campaignResults, c.Item1, c.Item2, c.Item3);
        tabs.Items.Add(new TabItem { Header = "Bilan de campagne", Content = _campaignResults });
        var comfort = new StackPanel(); Add(comfort, "Vision des couleurs (les textes restent la référence)", _colorVision); comfort.Children.Add(_windowsMotion); Add(comfort, "Écran à utiliser à la réouverture", _monitorChoice);
        comfort.Children.Add(PilotControls.Button("Organiser les rubriques de navigation", () => NavigationRequested?.Invoke()));
        comfort.Children.Add(PilotControls.Text("F11 active ou quitte la concentration ; Ctrl+K permet toujours de changer de rubrique.", 12, true));
        comfort.Children.Add(PilotControls.Button("Assistant de démarrage", () => OnboardingRequested?.Invoke()));
        comfort.Children.Add(PilotControls.Button("Examiner un diagnostic avant partage", () => DiagnosticRequested?.Invoke()));
        comfort.Children.Add(PilotControls.Button("Vérifier une mise à jour", () => UpdateRequested?.Invoke()));
        comfort.Children.Add(PilotControls.Button("Assistance en langage naturel (facultative)", () => AssistanceRequested?.Invoke()));
        tabs.Items.Add(new TabItem { Header = "Confort et maintenance", Content = Scroll(comfort) });
    }
    private void MoveStep(int direction)
    { var index = _recipeGrid.SelectedIndex; if (index >= 0 && index + direction >= 0 && index + direction < _steps.Count) _steps.Move(index, index + direction); }
    private void LoadCompletion(AppSettings settings)
    {
        var a = settings.Automation; _fixedTimes.Text = a.FixedTimes; _weekDays.Text = a.WeekDays; _failureLimit.Text = a.FailureAlertThreshold.ToString(); _reconnect.IsChecked = a.ResumeAfterReconnect; _useRecipe.IsChecked = a.UseRecipe;
        _steps = new(a.Steps.Select(s => s.Copy())); _recipeGrid.ItemsSource = _steps;
        _listTimeout.Text = settings.ListTimeoutSeconds.ToString(); _detailTimeout.Text = settings.DetailTimeoutSeconds.ToString(); _reconnectAttempts.Text = settings.ReconnectAttempts.ToString(); _freeSpace.Text = settings.MinimumFreeSpaceMb.ToString(); _resume.IsChecked = settings.ResumePagination; _checkpointAge.Text = settings.CheckpointMaximumAgeMinutes.ToString();
        _windowsMotion.IsChecked = settings.Interface.FollowWindowsMotion; _colorVision.SelectedValue = settings.Interface.ColorVision;
        _monitorChoice.ItemsSource = MonitorPlacement.Available(); _monitorChoice.SelectedValue = settings.Interface.PreferredMonitor;
    }
    private void ApplyCompletion(AppSettings settings)
    {
        _recipeGrid.CommitEdit(DataGridEditingUnit.Cell, true); _recipeGrid.CommitEdit(DataGridEditingUnit.Row, true);
        settings.Automation.FixedTimes = _fixedTimes.Text.Trim(); settings.Automation.WeekDays = _weekDays.Text.Trim();
        _ = AutomationPolicy.Times(settings.Automation.FixedTimes); _ = AutomationPolicy.Days(settings.Automation.WeekDays);
        settings.Automation.FailureAlertThreshold = Number(_failureLimit, 1, 20); settings.Automation.ResumeAfterReconnect = _reconnect.IsChecked == true; settings.Automation.UseRecipe = _useRecipe.IsChecked == true;
        settings.Automation.Steps = _steps.Select(s => s.Copy()).ToList(); AutomationPolicy.ValidateRecipe(settings.Automation.Steps);
        settings.ListTimeoutSeconds = Number(_listTimeout, 5, 600); settings.DetailTimeoutSeconds = Number(_detailTimeout, 5, 600); settings.ReconnectAttempts = Number(_reconnectAttempts, 0, 5); settings.MinimumFreeSpaceMb = Number(_freeSpace, 32, 32768); settings.ResumePagination = _resume.IsChecked == true; settings.CheckpointMaximumAgeMinutes = Number(_checkpointAge, 5, 1440);
        settings.Interface.FollowWindowsMotion = _windowsMotion.IsChecked == true; settings.Interface.ColorVision = _colorVision.SelectedValue?.ToString() ?? "standard"; settings.Interface.PreferredMonitor = _monitorChoice.SelectedValue?.ToString() ?? "";
    }
    public void CaptureSource(string id, AppSettings settings)
    { var source = _sources.FirstOrDefault(s => s.Id == id); if (source == null) return; source.Capture(settings); _sourcesGrid.Items.Refresh(); }
    public void ShowHealth(string message) => _health.Text = message;
    public void ShowCampaign(IReadOnlyList<CampaignSourceResult> results) => _campaignResults.ItemsSource = results;
}
