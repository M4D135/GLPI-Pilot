using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlpiPilot.Core;
using GlpiPilot.UI;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private readonly SnapshotStore _snapshotStore = new();
    private readonly OperationJournal _operationJournal = new();
    private HistoryWorkspaceView? _historyView;
    private DataQualityView? _qualityView;
    private ControlCenterView? _controlCenter;
    private TabItem? _historyTab, _qualityTab, _controlCenterTab;
    private SnapshotDocument? _historicalDocument;
    private bool IsHistoricalResult => _historicalDocument != null && ReferenceEquals(_historicalDocument.Data, _result);
    private DispatcherTimer? _automationTimer;
    private DateTimeOffset _nextAutomaticRun;
    private bool _scheduledJobStarting;
    private volatile bool _windowsSessionLocked;
    private long _qualityGeneration;
    private CancellationTokenSource? _qualityCancellation;
    private bool _qualityNeedsRetry;
    private OperationEntry? _currentOperationEntry;
    private Border? _inlineOverlay;
    private IInputElement? _overlayPreviousFocus;
    private string? _comparedAfterId;
    private Rect? _initialWindowBounds;
    private bool _initialWindowMaximized;

    private void InitializeControlCenter()
    {
        _historyView = new(); _qualityView = new(); _controlCenter = new();
        _historyTab = AddWorkspaceTab("Historique", "\uE81C", _historyView);
        _qualityTab = AddWorkspaceTab("Qualité", "\uE73E", _qualityView);
        _controlCenterTab = AddWorkspaceTab("Sources et organisation", "\uE713", _controlCenter);
        InitializeAlerts();
        _controlCenter.Load(_settings); _controlCenter.ShowOperations(_operationJournal.Entries);
        _controlCenter.SaveRequested += SaveControlCenter;
        _controlCenter.SourceRequested += ApplySourceProfile;
        _controlCenter.CurrentSourceRequested += () =>
        {
            try { var source = new SourceProfile { Name = "Recherche " + (_settings.Sources.Count + 1) }; source.Capture(ReadSettings()); _controlCenter.AddSource(source); }
            catch (Exception error) { ShowError(error); }
        };
        _controlCenter.CampaignRequested += async () => await RunCampaignAsync();
        _controlCenter.DemoRequested += LoadDemo;
        _controlCenter.RefreshBackupsRequested += RefreshSettingsBackups;
        _controlCenter.RestoreSettingsRequested += path =>
        {
            if (_busy || _closing) return;
            try { ReviewConfiguration(SettingsRecovery.Read(path), "Restaurer des réglages sauvegardés"); }
            catch (Exception error) { ShowError(error); }
        };
        RefreshSettingsBackups();
        if (SettingsRecovery.LastRecoveryMessage.Length > 0) AddLog("RÉGLAGES · " + SettingsRecovery.LastRecoveryMessage);
        _controlCenter.AcknowledgeRequested += id => { _operationJournal.Acknowledge(id); _controlCenter.ShowOperations(_operationJournal.Entries); };
        _controlCenter.SnoozeRequested += id => { _operationJournal.Snooze(id, TimeSpan.FromHours(1)); _controlCenter.ShowOperations(_operationJournal.Entries); };
        _historyView.RefreshRequested += async () => await RefreshHistoryAsync();
        _historyView.OpenRequested += async id => await OpenHistoricalResultAsync(id);
        _historyView.ExportRequested += async id => await ExportHistoricalResultAsync(id);
        _historyView.ProtectRequested += (id, protect) =>
        {
            try { _snapshotStore.Protect(id, protect); _ = RefreshHistoryAsync(); }
            catch (Exception error) { ShowError(error); }
        };
        _historyView.CompareRequested += async (before, after) => await RunAsync("Comparaison des relevés…", async ct =>
        {
            var comparison = await Task.Run(() => SnapshotStore.Compare(_snapshotStore.Load(before, ct), _snapshotStore.Load(after, ct), ct), ct);
            _comparedAfterId = after; _historyView.ShowComparison(comparison); SetStatus(comparison.Summary);
        });
        _historyView.ChangeRequested += async change =>
        {
            if (change.CurrentRow is int row && _comparedAfterId != null)
            { await OpenHistoricalResultAsync(_comparedAfterId); if (!_busy && _result != null) ShowDashboardRows(new[] { row }, "Évolution du ticket " + change.TicketId); }
        };
        _qualityView.MappingRequested += OpenMappingAssistant;
        _qualityView.RowRequested += row => { if (!_busy && !_closing) ShowDashboardRows(new[] { row }, "Qualité du relevé"); };
        MainTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != MainTabs) return;
            Dispatcher.BeginInvoke(ApplyInterfaceDensity, DispatcherPriority.Loaded);
            if (MainTabs.SelectedItem == _historyTab) _ = RefreshHistoryAsync();
            if (MainTabs.SelectedItem == _qualityTab && _qualityCancellation == null) RefreshQualityWorkspace();
            ResumeVisibleCalculation();
            RememberGridFocus();
        };
        _automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionSwitchChanged;
        _automationTimer.Tick += Automation_Tick; _automationTimer.Start(); ResetAutomaticSchedule();
        RestoreWindowPlacement(); ApplyInterfacePreferences();
        ContentRendered += async (_, _) =>
        {
            _initialWindowBounds ??= WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            _initialWindowMaximized = WindowState == WindowState.Maximized;
            await RefreshHistoryAsync();
            if (_settings.History.RestoreLastOnStartup && !_busy && !_closing && _result == null)
            {
                var previous = (await Task.Run(() => _snapshotStore.List(1))).FirstOrDefault();
                if (previous != null) await OpenHistoricalResultAsync(previous.Id);
            }
        };
        PreviewKeyDown += InlineOverlay_KeyDown;
    }

    private TabItem AddWorkspaceTab(string title, string icon, UIElement content)
    {
        var tab = new TabItem { Header = title, Uid = title, Tag = icon, Content = content };
        MainTabs.Items.Add(tab); return tab;
    }

    private async Task ArchiveResultAsync(AppSettings settings, CancellationToken ct)
    {
        if (_result == null || !settings.History.Enabled) return;
        try
        {
            var data = _result;
            var archiveSettings = settings.Copy(); archiveSettings.Dashboard = EffectiveDashboardSettings(settings.Dashboard);
            var locations = CitySourceIsCurrent() ? _cityEnrichment : null;
            var populations = new AnalyticsPopulationStore().LoadOrEmpty(out var warning);
            if (warning.Length > 0) AddLog("HISTORIQUE · " + warning);
            var metadata = await Task.Run(() => _snapshotStore.Save(data, archiveSettings, ct, locations, populations), ct);
            AddLog($"HISTORIQUE · relevé {metadata.Id[..8]} conservé ; {metadata.Rows:N0} lignes ; {metadata.State.ToLowerInvariant()}.");
            await RefreshHistoryAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { AddLog("HISTORIQUE · l’extraction reste disponible, archivage impossible : " + AppPaths.SafeError(error)); }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_closing || _historyView == null) return;
        try { var entries = await Task.Run(() => _snapshotStore.List()); if (!_closing) _historyView.Show(entries); }
        catch (Exception error) { if (!_closing) _historyView.ShowStatus("Historique indisponible : " + AppPaths.SafeError(error)); }
    }

    private async Task OpenHistoricalResultAsync(string id)
    {
        await RunAsync("Ouverture du relevé historique…", async ct =>
        {
            var document = await Task.Run(() => _snapshotStore.Load(id, ct), ct);
            _historicalDocument = document;
            // Stored derived columns are already part of this immutable receipt.
            _result = document.Data; _cityEnrichment = null; _citySettingsForResult = null;
            BuildPreview(); MainTabs.SelectedItem = ExtractionTab;
            SetStatus($"Consultation locale du {document.Metadata.CapturedAt.ToLocalTime():dd/MM/yyyy HH:mm} · règles et date du relevé conservées.");
            ResultSummaryText.Text += "\nRelevé historique ; les données sont datées et peuvent avoir changé dans GLPI.";
        });
    }

    private async Task ExportHistoricalResultAsync(string id)
    {
        if (_busy || _closing) return;
        var dialog = new SaveFileDialog { Title = "Exporter le relevé historique", Filter = "Classeur Excel|*.xlsx|CSV|*.csv", FileName = "GLPI-Historique.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync("Export du relevé historique…", async ct =>
        {
            var exported = await Task.Run(() =>
            {
                var data = _snapshotStore.Load(id, ct).Data;
                return Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                    ? ExportService.WriteCsv(data, dialog.FileName, ct) : ExportService.WriteXlsx(data, dialog.FileName, ct);
            }, ct);
            RememberExport(exported.Path); SetStatus("Relevé historique exporté.");
        });
    }

    private async void RefreshQualityWorkspace()
    {
        if (_qualityView == null || _closing) return;
        var generation = ++_qualityGeneration; _qualityCancellation?.Cancel();
        var operation = new CancellationTokenSource(); _qualityCancellation = operation; _qualityNeedsRetry = false;
        var source = _result;
        try
        {
            if (source == null) { _qualityView.ShowStatus("Chargez un relevé pour examiner sa qualité."); return; }
            var settings = EffectiveDashboardSettings(_settings.Dashboard);
            _qualityView.ShowStatus("Analyse de la couverture des champs et de la cohérence des valeurs…");
            var report = await _backgroundJobs.RunAsync("quality", MainTabs.SelectedItem == _qualityTab, token => DataQualityService.Analyze(source, settings, token), operation.Token);
            if (!_closing && generation == _qualityGeneration && ReferenceEquals(source, _result)) _qualityView.Show(report);
        }
        catch (OperationCanceledException)
        {
            if (!_closing && generation == _qualityGeneration && !operation.IsCancellationRequested && ReferenceEquals(source, _result)) _qualityNeedsRetry = true;
        }
        catch (Exception error) { if (!_closing && generation == _qualityGeneration) _qualityView.ShowStatus("Analyse indisponible : " + AppPaths.SafeError(error)); }
        finally
        {
            if (ReferenceEquals(_qualityCancellation, operation)) _qualityCancellation = null;
            operation.Dispose(); ResumeVisibleCalculation();
        }
    }

    private void SaveControlCenter()
    {
        if (_busy || _closing || _controlCenter == null) return;
        try
        {
            var settings = ReadSettings(); _controlCenter.ApplyTo(settings); settings.Normalize(); settings.Save(); _settings = settings;
            ApplyInterfacePreferences(); ResetAutomaticSchedule(); _snapshotStore.Prune(_settings.History);
            SetStatus("Sources, automatisations, historique et présentation enregistrés.");
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ApplySourceProfile(SourceProfile profile)
    {
        if (_busy || _closing) return;
        try
        {
            _ = TargetUrlPolicy.Validate(profile.Url);
            var settings = ReadSettings(); profile.ApplyTo(settings);
            settings.Save(); _settings = settings; LoadSettingsFields(); MainTabs.SelectedItem = ExtractionTab;
            SetStatus("Source sélectionnée : " + profile.Name + " · Extraire tout pour actualiser."); UpdateControls();
        }
        catch (Exception error) { ShowError(error); }
    }

    private async Task RunCampaignAsync(bool scheduled = false)
    {
        if (_busy || _closing || _controlCenter == null) return;
        try
        {
            var draft = scheduled ? _settings.Copy() : ReadSettings();
            if (!scheduled) _controlCenter.ApplyTo(draft);
            draft.Save(); _settings = draft;
            var sources = draft.Sources.Where(source => source.IncludedInCampaign).Select(source => source.Copy()).ToArray();
            if (sources.Length == 0) { SetStatus("Cochez au moins une source dans la campagne."); return; }
            await RunAsync("Extraction de la campagne…", async ct =>
            {
                BeginAutomaticPresentation(); var started = DateTimeOffset.Now;
                var results = sources.Select(source => new CampaignSourceResult { Id = source.Id, Name = source.Name }).ToList();
                try
                {
                    for (int index = 0; index < sources.Length; index++)
                    {
                        ct.ThrowIfCancellationRequested(); var source = sources[index]; var item = results[index]; item.StartedAt = DateTimeOffset.Now; item.State = "En cours";
                        _controlCenter.ShowCampaign(results.ToArray());
                        try
                        {
                            var settings = draft.Copy(); source.ApplyTo(settings);
                            AddLog($"CAMPAGNE · source {index + 1}/{sources.Length} : {source.Name}");
                            var run = await _automatic.RunAsync(settings, new Progress<ExtractionProgress>(progress => SetStatus(source.Name + " · " + progress.Message)), ct);
                            source.Capture(settings); var saved = draft.Sources.FindIndex(entry => entry.Id == source.Id); if (saved >= 0) draft.Sources[saved] = source.Copy();
                            settings.Sources = draft.Sources.Select(entry => entry.Copy()).ToList();
                            _settings = settings; PublishCityResult(run.Cities ?? await EnrichCitiesAsync(run.Data, settings, ct), settings);
                            await ArchiveResultAsync(settings, ct); BuildPreview(); RememberExport(run.PrimaryPath);
                            await RefreshPagesAsync(run.Table.Page); TableBox.ItemsSource = new[] { run.Table }; TableBox.SelectedItem = run.Table;
                            item.Rows = run.Data.Rows.Count; item.State = run.Data.Complete ? "Complet" : "Partiel"; item.Detail = run.Data.Status; item.Path = run.PrimaryPath;
                            RecordCollectionHealth(settings, run.Data.Complete, run.Data.Status); settings.Save(); LoadSettingsFields();
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { item.State = "Annulé"; item.Detail = "Annulation demandée"; throw; }
                        catch (Exception error)
                        {
                            item.State = "Échec"; item.Detail = AppPaths.SafeError(error); var failed = draft.Copy(); source.ApplyTo(failed); RecordCollectionHealth(failed, false, item.Detail);
                            AddLog("CAMPAGNE · " + source.Name + " · " + item.Detail + " ; passage à la source suivante.");
                        }
                        finally { item.FinishedAt = DateTimeOffset.Now; _controlCenter.ShowCampaign(results.ToArray()); }
                    }
                }
                finally
                {
                    foreach (var item in results.Where(item => item.State == "En attente")) { item.State = "Non lancée"; item.Detail = "Campagne interrompue"; }
                    _settings.Sources = draft.Sources.Select(entry => entry.Copy()).ToList(); _settings.Save(); _controlCenter.Load(_settings); _controlCenter.ShowCampaign(results.ToArray());
                    try { var report = await Task.Run(() => CampaignReport.Save(draft.ExportFolder, results, started)); AddLog("CAMPAGNE · bilan JSON et CSV : " + report); }
                    catch (Exception error) { AddLog("CAMPAGNE · bilan conservé à l’écran ; écriture impossible : " + AppPaths.SafeError(error)); }
                }
                SetStatus($"Campagne terminée : {results.Count(item => item.State == "Complet")} complètes, {results.Count(item => item.State == "Partiel")} partielles, {results.Count(item => item.State == "Échec")} en échec. Bilan par source dans Sources et organisation.");
            });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ResetAutomaticSchedule()
    {
        _nextAutomaticRun = AutomationPolicy.Next(_settings.Automation, DateTimeOffset.Now);
        _controlCenter?.ShowSchedule(_settings.Automation.ScheduledExtraction ? $"Prochain lancement au plus tôt à {_nextAutomaticRun.LocalDateTime:HH:mm}." : "Programmation désactivée.");
    }

    private async void Automation_Tick(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (_settings.Interface.FollowWindowsTheme && ApplySystemThemeIfChanged()) ApplyInterfacePreferences();
        if (_result != null && DateTimeOffset.Now - _result.ExtractedAt > TimeSpan.FromHours(_settings.Automation.FreshnessHours))
            WorkspaceContextText.ToolTip = $"Relevé ancien : {_result.ExtractedAt.ToLocalTime():dd/MM/yyyy HH:mm}. Actualisez avant de décider d’une intervention.";
        UpdateAlertBadge(); RefreshCollectionHealth();
        if (!_settings.Automation.ScheduledExtraction || _windowsSessionLocked || _inlineOverlay != null || _busy || _scheduledJobStarting || DateTimeOffset.Now < _nextAutomaticRun) return;
        var config = _settings.Automation;
        if (AutomationPolicy.IsQuiet(config, DateTimeOffset.Now)) return;
        _scheduledJobStarting = true; ResetAutomaticSchedule();
        try
        {
            if (config.RunCampaign) await RunCampaignAsync(scheduled: true);
            else if (config.UseRecipe) await RunRecipeAsync(true);
            else if (RunAutoButton.IsEnabled) await RunAutomaticAsync(_settings.Copy());
        }
        finally { _scheduledJobStarting = false; }
    }

    private void ApplyInterfacePreferences()
    {
        if (_settings.Interface.FollowWindowsTheme) ApplySystemThemeIfChanged();
        // Always restore the complete base palette before applying optional overrides.
        // Otherwise turning a mode off leaves its old colors when the OS theme is unchanged.
        ThemeService.Apply(_settings.Theme);
        UpdateThemeButton();
        var accent = _settings.Interface.Accent switch { "violet" => "#7557C7", "green" => "#16785F", "orange" => "#A95118", "rose" => "#B34475", _ => "#2768D6" };
        Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
        foreach (var key in new[] { "ActionBrush", "ActionHoverBrush", "ActionBorderBrush", "BrandBrush" }) Application.Current.Resources[key] = Brush(accent);
        Application.Current.Resources["AccentBrush"] = Brush(ThemeService.IsDark ? _settings.Interface.Accent switch { "violet" => "#C8B4FF", "green" => "#8BDABD", "orange" => "#F4BD8D", "rose" => "#F3AECA", _ => "#9ABFFF" } : accent);
        if (_settings.Interface.HighContrast)
        {
            Application.Current.Resources["MutedBrush"] = Brush(ThemeService.IsDark ? "#DDDDDF" : "#3C3C40");
            Application.Current.Resources["LineBrush"] = Brush(ThemeService.IsDark ? "#8B8B95" : "#72727C");
        }
        var reduceMotion = _settings.Interface.ReduceMotion || _settings.Interface.FollowWindowsMotion && !SystemParameters.ClientAreaAnimation;
        Application.Current.Resources["AnimationsEnabled"] = !reduceMotion;
        Application.Current.Resources["PopupMotion"] = reduceMotion ? System.Windows.Controls.Primitives.PopupAnimation.None : System.Windows.Controls.Primitives.PopupAnimation.Fade;
        LogExpander.Visibility = _settings.Interface.FocusMode ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceContextText.Visibility = _settings.Interface.FocusMode ? Visibility.Collapsed : Visibility.Visible;
        ApplyAccessiblePalette(); ApplyNavigationPreferences(); ApplyInterfaceDensity();
    }
    private bool ApplySystemThemeIfChanged()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
            var theme = value is int number && number == 1 ? "light" : "dark";
            if (ThemeService.CurrentTheme != theme) { ThemeService.Apply(theme); _settings.Theme = theme; UpdateThemeButton(); return true; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return false;
    }
    private void ApplyInterfaceDensity()
    {
        double row = _settings.Interface.Density switch { "compact" => 30, "comfortable" => 46, _ => 38 };
        void Visit(DependencyObject parent)
        {
            if (parent is DataGrid grid) { grid.RowHeight = row; grid.MinRowHeight = row; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Visit(VisualTreeHelper.GetChild(parent, i));
        }
        Visit(MainTabs); ResultGrid.FrozenColumnCount = Math.Min(_settings.Interface.FrozenColumns, ResultGrid.Columns.Count);
    }
    private void RestoreWindowPlacement()
    {
        var prefs = _settings.Interface; var area = SystemParameters.WorkArea;
        if (prefs.WindowWidth > 0) Width = Math.Clamp(prefs.WindowWidth, MinWidth, Math.Max(MinWidth, area.Width));
        if (prefs.WindowHeight > 0) Height = Math.Clamp(prefs.WindowHeight, MinHeight, Math.Max(MinHeight, area.Height));
        if (prefs.WindowLeft.HasValue && prefs.WindowTop.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(prefs.WindowLeft.Value, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(prefs.WindowTop.Value, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + Math.Max(0, SystemParameters.VirtualScreenHeight - Height));
        }
        if (prefs.WindowMaximized) WindowState = WindowState.Maximized;
    }
    private void RememberWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (_initialWindowBounds is not Rect initial || bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return;
        if (Math.Abs(bounds.Left - initial.Left) < 1 && Math.Abs(bounds.Top - initial.Top) < 1 &&
            Math.Abs(bounds.Width - initial.Width) < 1 && Math.Abs(bounds.Height - initial.Height) < 1 &&
            (WindowState == WindowState.Maximized) == _initialWindowMaximized) return;
        _settings.Interface.WindowWidth = bounds.Width; _settings.Interface.WindowHeight = bounds.Height;
        _settings.Interface.WindowLeft = bounds.Left; _settings.Interface.WindowTop = bounds.Top;
        _settings.Interface.WindowMaximized = WindowState == WindowState.Maximized;
        MonitorPlacement.Remember(this, _settings.Interface);
        try { _settings.Save(); } catch (Exception error) { AppPaths.AppendLog("FENÊTRE · mémorisation indisponible : " + AppPaths.SafeError(error)); }
    }

    private void ShowInlinePanel(FrameworkElement content)
    {
        CloseInlinePanel(); _overlayPreviousFocus = Keyboard.FocusedElement;
        var container = new Grid { Margin = new Thickness(24), MaxWidth = 980 };
        KeyboardNavigation.SetTabNavigation(container, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(container, KeyboardNavigationMode.Cycle);
        container.RowDefinitions.Add(new() { Height = GridLength.Auto }); container.RowDefinitions.Add(new());
        var close = PilotControls.Button("Fermer · Échap", CloseInlinePanel); close.HorizontalAlignment = HorizontalAlignment.Right; container.Children.Add(close);
        Grid.SetRow(content, 1); container.Children.Add(content);
        var card = PilotControls.Card(container); card.Margin = new Thickness(20); card.MaxWidth = 1020; card.HorizontalAlignment = HorizontalAlignment.Stretch;
        _inlineOverlay = new Border { Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), Child = card };
        Grid.SetRowSpan(_inlineOverlay, 4); Panel.SetZIndex(_inlineOverlay, 100); ShellRoot.Children.Add(_inlineOverlay);
        Dispatcher.BeginInvoke(() => content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }
    private void CloseInlinePanel()
    {
        if (_inlineOverlay != null) { ShellRoot.Children.Remove(_inlineOverlay); _inlineOverlay.Child = null; _inlineOverlay = null; }
        _commandPalette = null;
        if (_overlayPreviousFocus != null) Keyboard.Focus(_overlayPreviousFocus);
        _overlayPreviousFocus = null;
    }
    private void InlineOverlay_KeyDown(object sender, KeyEventArgs e)
    { if (_inlineOverlay != null && e.Key == Key.Escape) { CloseInlinePanel(); e.Handled = true; } }

    private void LoadDemo()
    {
        if (_busy || _closing) return;
        var data = new ExtractionResult { Headers = new() { "ID", "Titre", "Statut", "Priorité", "Date d’ouverture", "Dernière modification", "Attribué à - Technicien", "Catégorie", "Lieu", "Date de résolution", "Échéance" },
            Title = "Démonstration fictive", Scope = "Données fictives de démonstration", Complete = false, SourceUrl = "", Provenance = new() { SourceKind = "demo", Method = "synthetic" } };
        var cities = new[] { "Marseille", "Aix-en-Provence", "Aubagne", "Arles" }; var technicians = new[] { "Alex Martin", "Camille Durand", "Sam Bernard", "" };
        for (int index = 1; index <= 180; index++)
        {
            var status = new[] { "Nouveau", "En cours (attribué)", "En attente", "Résolu", "Clos" }[index % 5];
            data.Rows.Add(new[] { index.ToString(), $"{cities[index % 4]} · Incident fictif {index}", status, new[] { "Basse", "Moyenne", "Haute", "Très haute" }[index % 4],
                DateTime.Today.AddDays(-index % 60).ToString("dd/MM/yyyy HH:mm"), DateTime.Today.AddDays(-index % 20).ToString("dd/MM/yyyy HH:mm"), technicians[index % 4],
                new[] { "Poste de travail", "Impression", "Réseau" }[index % 3], cities[index % 4], status is "Résolu" or "Clos" ? DateTime.Today.AddDays(-index % 7).ToString("dd/MM/yyyy HH:mm") : "", DateTime.Today.AddDays(index % 9 - 4).ToString("dd/MM/yyyy HH:mm") });
        }
        data.Status = "180 tickets fictifs · démonstration"; data.Warnings.Add("Données entièrement fictives. Elles ne seront pas archivées comme un relevé métier.");
        _cityEnrichment = null; _citySettingsForResult = null; _result = data; BuildPreview(); MainTabs.SelectedItem = DashboardTab; SetStatus(data.Status);
    }

    private void RefreshSettingsBackups()
    {
        try { _controlCenter?.ShowBackups(SettingsRecovery.List(AppPaths.SettingsFile)); }
        catch (Exception error) { AddLog("RÉGLAGES · versions locales indisponibles : " + AppPaths.SafeError(error)); }
    }

    private void SessionSwitchChanged(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) _windowsSessionLocked = true;
        else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) _windowsSessionLocked = false;
    }

    private void DisposeControlCenter()
    { Microsoft.Win32.SystemEvents.SessionSwitch -= SessionSwitchChanged; DisposeCompletionWorkspace(); _automationTimer?.Stop(); _qualityCancellation?.Cancel(); RememberWindowPlacement(); PreviewKeyDown -= InlineOverlay_KeyDown; CloseInlinePanel(); }
}
