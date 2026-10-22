using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlpiPilot.Core;
using GlpiPilot.UI;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private readonly BackgroundWorkScheduler _backgroundJobs = new();
    private readonly CollectionHealthStore _collectionHealth = new();
    private readonly GridWorkspaceMemory _gridMemory = new();
    private bool _applyingNavigation;
    private string _lastHealthSummary = "";
    private void InitializeCompletionWorkspace()
    {
        if (_controlCenter == null) return;
        _controlCenter.CaptureSourceRequested += id => { try { _controlCenter.CaptureSource(id, ReadSettings()); SetStatus("Profil actualisé dans le brouillon. Enregistrez les réglages pour le conserver."); } catch (Exception error) { ShowError(error); } };
        _controlCenter.RecipeRequested += async () => await RunRecipeAsync();
        _controlCenter.NavigationRequested += OpenNavigationEditor;
        _controlCenter.MappingRequested += OpenMappingAssistant;
        _controlCenter.OnboardingRequested += OpenOnboarding;
        _controlCenter.DiagnosticRequested += OpenDiagnosticPreview;
        _controlCenter.UpdateRequested += OpenUpdateAssistant;
        _controlCenter.AssistanceRequested += OpenLanguageAssistant;
        NotificationPolicy.CanNotify = () => !_closing && !_windowsSessionLocked && !AutomationPolicy.IsQuiet(_settings.Automation, DateTimeOffset.Now);
        SystemEvents.PowerModeChanged += PowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        PreviewKeyDown += Completion_KeyDown;
        Loaded += (_, _) => { MonitorPlacement.Restore(this, _settings.Interface); ApplyNavigationPreferences(); _gridMemory.Attach(MainTabs); };
        ContentRendered += (_, _) =>
        {
            UpdateService.AcknowledgeStartup();
            if (!_settings.Interface.OnboardingCompleted && string.IsNullOrWhiteSpace(_settings.PortalUrl)) OpenOnboarding();
        };
        InitializeDataWorkspace();
    }
    private void CaptureActiveSource(AppSettings settings)
    { var source = settings.Sources.FirstOrDefault(s => s.Id == settings.ActiveSourceId); if (source != null && TargetUrlPolicy.SamePage(source.Url, settings.PortalUrl)) source.Capture(settings); }
    private void RecordCollectionHealth(AppSettings settings, bool success, string detail)
    {
        try
        {
            var id = settings.ActiveSourceId.Length > 0 ? settings.ActiveSourceId : SnapshotStore.SourceKey(settings.PortalUrl);
            var name = settings.Sources.FirstOrDefault(s => s.Id == settings.ActiveSourceId)?.Name ?? "Source courante";
            _collectionHealth.Record(id, name, success, detail, DateTimeOffset.Now); RefreshCollectionHealth();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AddLog("COLLECTE · bilan de santé indisponible : " + AppPaths.SafeError(error)); }
    }
    private void RefreshCollectionHealth()
    {
        var sources = _settings.Sources.ToList();
        if (sources.Count == 0 && !string.IsNullOrWhiteSpace(_settings.PortalUrl)) sources.Add(new() { Id = SnapshotStore.SourceKey(_settings.PortalUrl), Name = "Source courante" });
        var summary = string.Join("\n", _collectionHealth.Alerts(sources, _settings.Automation, DateTimeOffset.Now));
        _controlCenter?.ShowHealth(summary.Length > 0 ? summary : "Toutes les sources suivies disposent d’une collecte récente sans échecs répétés.");
        _alertsView?.ShowCollectionHealth(summary.Length > 0 ? summary : "Toutes les sources suivies disposent d’une collecte récente sans échecs répétés.");
        if (summary.Length > 0 && summary != _lastHealthSummary && _settings.Automation.ScheduledExtraction) AddLog("SANTÉ DES COLLECTES · " + summary.Replace('\n', ' '));
        _lastHealthSummary = summary;
    }
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) QueueAutomaticResume("Retour de veille"); }
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    { if (e.IsAvailable) QueueAutomaticResume("Réseau disponible"); }
    private void QueueAutomaticResume(string reason)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !_settings.Automation.ScheduledExtraction || !_settings.Automation.ResumeAfterReconnect) return;
            AddLog("PROGRAMMATION · " + reason + " ; les lancements dus reprendront lorsque la session sera disponible.");
            Automation_Tick(this, EventArgs.Empty);
        });
    }
    private async Task RunRecipeAsync(bool scheduled = false)
    {
        if (_busy || _closing) return;
        try
        {
            var settings = scheduled ? _settings.Copy() : ReadSettings(); if (!scheduled) _controlCenter?.ApplyTo(settings);
            AutomationPolicy.ValidateRecipe(settings.Automation.Steps); settings.Save(); _settings = settings;
            await RunAsync("Exécution de la recette…", async ct =>
            {
                ExtractionResult? data = null; AutomaticRunResult? acquisition = null; bool citiesApplied = false;
                foreach (var step in settings.Automation.Steps)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!AutomationPolicy.ShouldRun(step, data)) { AddLog("RECETTE · " + step.Kind + " ignorée : condition non remplie ou étape désactivée."); continue; }
                    AddLog("RECETTE · " + step.Kind + " démarrée.");
                    switch (step.Kind)
                    {
                        case "extract":
                            var raw = settings.Copy(); raw.AutoSaveXlsx = false; raw.Cities.Enabled = false; BeginAutomaticPresentation();
                            try { acquisition = await _automatic.RunAsync(raw, new Progress<ExtractionProgress>(p => SetStatus(p.Message)), ct); RecordCollectionHealth(settings, acquisition.Data.Complete, acquisition.Data.Status); }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception error) { RecordCollectionHealth(settings, false, AppPaths.SafeError(error)); throw; }
                            settings.RememberTable(acquisition.Table); data = acquisition.Data; _result = data; _historicalDocument = null; _cityEnrichment = null; RememberExport(acquisition.PrimaryPath); break;
                        case "quality":
                            var report = await Task.Run(() => DataQualityService.Analyze(data!, settings.Dashboard, ct), ct); _qualityView?.Show(report); break;
                        case "enrich":
                            var enriched = await EnrichCitiesAsync(data!, settings, ct); PublishCityResult(enriched, settings); data = _result; citiesApplied = true; break;
                        case "archive": _result = data; var archiveSettings = settings.Copy(); archiveSettings.Cities.Enabled = settings.Cities.Enabled && citiesApplied; await ArchiveResultAsync(archiveSettings, ct); break;
                        case "csv": case "xlsx":
                            var path = Path.Combine(settings.ExportFolder, "GLPI_Recette_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6] + "." + step.Kind);
                            var file = await Task.Run(() => step.Kind == "csv" ? ExportService.WriteCsv(data!, path, ct) : ExportService.WriteXlsx(data!, path, ct), ct); RememberExport(file.Path); break;
                        case "alerts":
                            var snapshot = await Task.Run(() => { var summary = DashboardService.Calculate(data!, settings.Dashboard, ct: ct); QueryService.AppendDashboardQueries(summary, data!, settings.Dashboard, settings.Queries, ct); return summary; }, ct); RefreshAlerts(data!, snapshot); break;
                    }
                    AddLog("RECETTE · " + step.Kind + " terminée.");
                }
                if (data != null) { _result = data; BuildPreview(); }
                if (acquisition != null) { await RefreshPagesAsync(acquisition.Table.Page); TableBox.ItemsSource = new[] { acquisition.Table }; TableBox.SelectedItem = acquisition.Table; }
                CaptureActiveSource(settings); _settings = settings; settings.Save(); LoadSettingsFields(); SetStatus("Recette terminée. Le journal indique chaque étape exécutée ou ignorée.");
            });
        }
        catch (Exception error) { ShowError(error); }
    }
    private void ApplyAccessiblePalette()
    {
        var mode = _settings.Interface.ColorVision;
        var changed = (Application.Current.Resources["ColorVisionMode"] as string ?? "standard") != mode;
        Application.Current.Resources["ColorVisionMode"] = mode;
        if (changed && _result != null) QueueDashboardCalculation();
        if (mode == "standard") return;
        var colors = mode switch { "protanopia" => new[] { "#0072B2", "#D4A000", "#56B4E9" }, "tritanopia" => new[] { "#C94F7C", "#168566", "#9E5A19" }, _ => new[] { "#0072B2", "#D55E00", "#009E73" } };
        var keys = new[] { "AccentBrush", "WarningBrush", "SuccessBrush" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    private void ApplyNavigationPreferences()
    {
        if (_applyingNavigation || MainTabs == null) return; _applyingNavigation = true;
        try
        {
            var tabs = MainTabs.Items.OfType<TabItem>().ToArray(); foreach (var tab in tabs) if (tab.Uid.Length == 0) tab.Uid = tab.Name.Length > 0 ? tab.Name : tab.Header?.ToString() ?? "";
            var selected = MainTabs.SelectedItem; var order = _settings.Interface.NavigationOrder;
            var desired = tabs.OrderBy(tab => order.Contains(tab.Uid) ? order.IndexOf(tab.Uid) : int.MaxValue).ToArray();
            for (int index = 0; index < desired.Length; index++) if (!ReferenceEquals(MainTabs.Items[index], desired[index])) { MainTabs.Items.Remove(desired[index]); MainTabs.Items.Insert(index, desired[index]); }
            foreach (var tab in tabs) tab.Visibility = _settings.Interface.HiddenNavigation.Contains(tab.Uid) && tab != _controlCenterTab ? Visibility.Collapsed : Visibility.Visible;
            if (selected != null) MainTabs.SelectedItem = selected;
            MainTabs.Style = (Style)FindResource(_settings.Interface.FocusMode ? "FocusWorkspaceTabs" : _wideNavigation == false ? "CompactWorkspaceTabs" : "WorkspaceTabs");
        }
        finally { _applyingNavigation = false; }
    }
    private sealed class NavigationEntry { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public bool Visible { get; set; } = true; }
    private void OpenNavigationEditor()
    {
        var rows = new ObservableCollection<NavigationEntry>(MainTabs.Items.OfType<TabItem>().Select(tab => new NavigationEntry { Id = tab.Uid, Name = tab.Header?.ToString() ?? "", Visible = tab.Visibility == Visibility.Visible }));
        var grid = PilotControls.Table(editable: true); grid.ItemsSource = rows;
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Visible", Binding = new Binding("Visible"), Width = 90 }); PilotControls.Column(grid, "Rubrique", "Name", -1); grid.Columns[1].IsReadOnly = true;
        void Move(int delta) { var index = grid.SelectedIndex; if (index >= 0 && index + delta >= 0 && index + delta < rows.Count) rows.Move(index, index + delta); }
        var toolbar = new WrapPanel(); toolbar.Children.Add(PilotControls.Button("Épingler en tête", () => { if (grid.SelectedIndex > 0) rows.Move(grid.SelectedIndex, 0); })); toolbar.Children.Add(PilotControls.Button("Monter", () => Move(-1))); toolbar.Children.Add(PilotControls.Button("Descendre", () => Move(1)));
        toolbar.Children.Add(PilotControls.Button("Appliquer", () => { grid.CommitEdit(); _settings.Interface.NavigationOrder = rows.Select(r => r.Id).ToList(); _settings.Interface.HiddenNavigation = rows.Where(r => !r.Visible).Select(r => r.Id).ToList(); _settings.Save(); ApplyNavigationPreferences(); CloseInlinePanel(); }));
        ShowInlinePanel(PilotControls.Page("Votre navigation", "Sources et organisation reste accessible ; Ctrl+K ouvre aussi les rubriques masquées.", grid, toolbar));
    }
    private void Completion_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _closing || _inlineOverlay != null) return;
        if (e.Key == Key.F11) { _settings.Interface.FocusMode = !_settings.Interface.FocusMode; _settings.Save(); ApplyInterfacePreferences(); _controlCenter?.Load(_settings); e.Handled = true; }
    }
    private void OpenDiagnosticPreview()
    {
        var text = DiagnosticReport.Create(_settings, _result, LogBox.Text);
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var toolbar = new WrapPanel(); toolbar.Children.Add(PilotControls.Button("Copier ce rapport", () => Clipboard.SetText(box.Text)));
        toolbar.Children.Add(PilotControls.Button("Enregistrer…", () => { var dialog = new SaveFileDialog { Filter = "Diagnostic texte (*.txt)|*.txt", FileName = "GLPI-Pilot-Diagnostic.txt" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, box.Text); }));
        ShowInlinePanel(PilotControls.Page("Diagnostic à relire", "Inspectez le rapport avant de le copier ou de le partager. Aucun envoi automatique.", box, toolbar));
    }
    private void RememberGridFocus() => Dispatcher.BeginInvoke(() => { if (!_closing) _gridMemory.Attach(MainTabs); }, DispatcherPriority.Loaded);
    private void ResumeVisibleCalculation()
    {
        if (_closing || _result == null) return;
        if (MainTabs.SelectedItem == DashboardTab && _dashboardNeedsRetry && _dashboardCancellation == null) QueueDashboardCalculation();
        if (MainTabs.SelectedItem == _qualityTab && _qualityNeedsRetry && _qualityCancellation == null) RefreshQualityWorkspace();
    }
    private void DisposeCompletionWorkspace()
    {
        SystemEvents.PowerModeChanged -= PowerModeChanged; NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        PreviewKeyDown -= Completion_KeyDown; NotificationPolicy.CanNotify = () => false; _backgroundJobs.Dispose(); _gridMemory.Dispose();
    }
}
