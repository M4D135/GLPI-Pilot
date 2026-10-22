using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GlpiPilot.Core;
using GlpiPilot.UI;
using Microsoft.Playwright;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow : Window
{
    private readonly BrowserService _browser;
    private readonly AutomaticExtractionService _automatic;
    private AppSettings _settings = new();
    private CancellationTokenSource? _operation;
    private readonly DispatcherTimer _monitor;
    private readonly DispatcherTimer _filterTimer;
    private bool _busy, _monitorBusy, _closing, _closedAllowed, _capturing;
    private bool _refreshingPages;
    private bool _presentingBrowser;
    private bool _backgroundLaunch;
    private ForegroundLaunchGuard? _foregroundLaunchGuard;
    private string? _nativeReceiptPath;
    private ExtractionResult? _result;
    private DataTable? _preview;
    private const int PreviewLimit = 10000;

    public MainWindow()
    {
        StartupDiagnostics.Step("Fenêtre · chargement XAML");
        InitializeComponent();
        ApplicationVersionText.Text = $"Version {AppPaths.ApplicationVersion} · Windows x64";
        StartupDiagnostics.Step("Fenêtre · dimensions et services locaux");
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, work.Width - 24);
        MinHeight = Math.Min(MinHeight, work.Height - 24);
        Width = Math.Min(Width, work.Width - 24);
        Height = Math.Min(Height, work.Height - 24);
        _browser = new();
        _browser.Log += message => Dispatcher.InvokeAsync(() => AddLog(message));
        _browser.PagesChanged += () => Dispatcher.InvokeAsync(() => { if (!_closing) UpdateBrowserButton(); });
        _browser.BackgroundLaunchStateChanged += starting => Dispatcher.Invoke(() =>
        {
            _foregroundLaunchGuard?.Dispose();
            _foregroundLaunchGuard = starting && !_closing ? ForegroundLaunchGuard.Acquire(this) : null;
            _backgroundLaunch = starting;
            UpdateWindowPriority();
        });
        _browser.ConnectionProgress += message => Dispatcher.InvokeAsync(() =>
        {
            if (!_busy || _closing) return;
            ConnectionText.Text = message;
            SetStatus(message);
            if (_result == null) EmptyStateHintText.Text = message;
        });
        _automatic = new(_browser);
        _automatic.Log += message => Dispatcher.InvokeAsync(() => AddLog(message));
        _automatic.NativeFileSaved += path => Dispatcher.Invoke(() =>
        {
            _nativeReceiptPath = path;
            ResultSummaryText.Text = "Fichier GLPI reçu et conservé. Vérification des données en cours…";
        });
        StartupDiagnostics.Step("Préférences · lecture de la configuration locale");
        try { _settings = AppSettings.Load(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { AddLog("Réglages illisibles : valeurs par défaut. " + AppPaths.SafeError(ex)); }
        StartupDiagnostics.Step("Interface · application du thème");
        ThemeService.Apply(_settings.Theme);
        ThemeService.Attach(this);
        UpdateThemeButton();
        StartupDiagnostics.Step("Interface · construction du détail ticket");
        InitializeTicketWorkspace();
        StartupDiagnostics.Step("Interface · construction du dashboard et des techniciens");
        InitializeDashboardWorkspace();
        InitializeCityWorkspace();
        InitializeQueryWorkspace();
        StartupDiagnostics.Step("Préférences · restauration des champs de l’interface");
        LoadSettingsFields();
        StartupDiagnostics.Step("Interface · branchement des événements et minuteurs");
        PageBox.SelectionChanged += (_, _) => { if (!_busy && !_refreshingPages) { TableBox.ItemsSource = null; UpdateControls(); } };
        TableBox.SelectionChanged += (_, _) => UpdateControls();
        ResultGrid.AutoGeneratingColumn += Grid_AutoGeneratingColumn;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyPreviewFilter(); };
        SearchBox.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        InitializePreviewWorkspace();
        InitializeWorkspace();
        InitializeControlCenter();
        InitializeAnalyticsWorkspace();
        InitializeCompletionWorkspace();
        _monitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitor.Tick += Monitor_Tick;
        _monitor.Start();
        Closing += Window_Closing;
        StateChanged += (_, _) => UpdateWindowPriority();
        UpdateControls();
        StartupDiagnostics.Step("Fenêtre construite · attente du premier rendu");
        AddLog($"GLPI Pilot {AppPaths.ApplicationVersion} prêt. Historique, qualité, sources, dashboards et espace technicien disponibles.");
    }

    public void ShowError(Exception error)
    {
        _operationFailed = true;
        var message = AppPaths.SafeError(error);
        if (_currentOperationEntry != null) { _currentOperationEntry.Code = OperationJournal.ErrorCode(error); _currentOperationEntry.Detail = message; }
        if (error is PlaywrightException && message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
            message = "Le navigateur choisi est introuvable. Choisis Edge ou Chrome déjà installé sur ce poste.";
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        LogExpander.IsExpanded = true;
        if (_result == null)
        {
            StatsText.Text = "Extraction interrompue";
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateTitleText.Text = "L’extraction demande votre attention";
            EmptyStateHintText.Text = "Le détail figure ci-dessous. Vous pouvez accéder au navigateur ou relancer l’extraction.";
            if (_nativeReceiptPath != null)
                ResultSummaryText.Text = "Le fichier reçu est conservé : " + _nativeReceiptPath + ". Outils manuels → Importer un CSV permet de le rouvrir.";
        }
        AddLog("ERREUR · " + error.GetType().Name + " · " + message);
    }

    private void AddLog(string message)
    {
        message = AppPaths.SafeLog(message);
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        if (LogBox.Text.Length > 45000) LogBox.Text = LogBox.Text[^30000..];
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        AppPaths.AppendLog(message);
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        if (_busy && _workspaceInitialized) OperationTitleText.Text = text;
    }

    private void UpdateThemeButton()
    {
        ThemeToggleButton.Content = ThemeService.IsDark ? "☀" : "☾";
        ThemeToggleButton.ToolTip = ThemeService.IsDark ? "Passer au thème clair" : "Passer au thème sombre";
        System.Windows.Automation.AutomationProperties.SetName(ThemeToggleButton, ThemeToggleButton.ToolTip.ToString());
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Interface.FollowWindowsTheme = false;
        ThemeService.Apply(ThemeService.IsDark ? "light" : "dark");
        UpdateThemeButton();
        _settings.Theme = ThemeService.CurrentTheme;
        ApplyInterfacePreferences();
        try { _settings.Save(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateBrowserButton()
    {
        ShowBrowserButton.IsEnabled = _browser.IsOpen && !_presentingBrowser && !_closing;
        ShowBrowserButton.Content = _browser.BackgroundMode ? "Voir le navigateur" : "Revenir à GLPI Pilot";
        ShowBrowserButton.ToolTip = _browser.BackgroundMode
            ? "Accéder au navigateur si le SSO demande une action. L’extraction reprendra automatiquement après connexion."
            : "Replacer GLPI Pilot au-dessus pendant l’extraction, en laissant le navigateur ouvert.";
        UpdateWindowPriority();
    }

    private void UpdateWindowPriority()
    {
        // Keep the application above the browser while automation runs. Explicit
        // browser access suspends this immediately; no timer steals keyboard focus.
        // Releasing Topmost when idle also lets Explorer and other applications open normally.
        Topmost = !_closing && !_presentingBrowser && WindowState != WindowState.Minimized
            && (_backgroundLaunch || (_busy && _browser.IsOpen && _browser.BackgroundMode));
    }

    private async void ShowBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_presentingBrowser || _closing) return;
        _presentingBrowser = true;
        UpdateBrowserButton();
        try
        {
            _foregroundLaunchGuard?.Dispose(); _foregroundLaunchGuard = null;
            if (_browser.BackgroundMode)
            {
                Topmost = false;
                var page = IsTicketDetailBusy || MainTabs.SelectedItem == TicketDetailTab ? _browser.TicketPage : null;
                await _browser.ShowBrowserAsync(page ?? _browser.ActivePage ?? SelectedPage, CancellationToken.None);
            }
            else
            {
                await _browser.HideBrowserAsync(CancellationToken.None);
                Activate(); // Explicit user request to return to this application.
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _presentingBrowser = false; if (!_closing) UpdateBrowserButton(); }
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var diagnosticSettings = _settings.Copy();
            diagnosticSettings.Dashboard = EffectiveDashboardSettings(diagnosticSettings.Dashboard);
            Clipboard.SetText(DiagnosticReport.Create(diagnosticSettings, _result, LogBox.Text));
            SetStatus("Diagnostic copié. Tu peux le coller dans la conversation.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ClearResult(string text, bool retainPrevious = true)
    {
        if (retainPrevious && RetainResultDuringRefresh(text)) return;
        ResetPreviewWorkspace();
        OnExtractionChanged();
        _result = null; _preview = null; _nativeReceiptPath = null;
        ResetCityWorkspace();
        ResetQueryWorkspace();
        ResetDashboard();
        ResultGrid.ItemsSource = null;
        EmptyStatePanel.Visibility = Visibility.Visible;
        EmptyStateTitleText.Text = "Préparation de l’extraction";
        EmptyStateHintText.Text = text;
        StatsText.Text = "Extraction en cours";
        ResultSummaryText.Text = text;
    }

    private async void RunAuto_Click(object sender, RoutedEventArgs e) => await RunAutomaticAsync();

    private async Task RunAutomaticAsync(AppSettings? rememberedSettings = null)
    {
        await RunAsync("Démarrage de l’extraction automatique…", async ct =>
        {
            var settings = rememberedSettings?.Copy() ?? ReadSettings();
            _ = TargetUrlPolicy.Validate(settings.PortalUrl);
            _settings = settings; _settings.Save();
            BeginAutomaticPresentation();
            MainTabs.SelectedIndex = 0;
            ClearResult("Ouverture de la source mémorisée, attente du SSO puis extraction de toutes les pages.");
            ConnectionText.Text = "Connexion et navigation automatiques…";
            var progress = new Progress<ExtractionProgress>(p =>
            {
                SetStatus(p.Message);
                if (p.Rows > 0 || p.Pages > 0) StatsText.Text = $"{p.Rows:N0} lignes" + (p.Pages > 0 ? $" · {p.Pages} page(s)" : "");
            });
            AutomaticRunResult run;
            try { run = await _automatic.RunAsync(settings, progress, ct); RecordCollectionHealth(settings, run.Data.Complete, run.Data.Status); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { RecordCollectionHealth(settings, false, AppPaths.SafeError(error)); throw; }
            CaptureActiveSource(settings);
            PublishCityResult(run.Cities ?? await EnrichCitiesAsync(run.Data, settings, ct), settings);
            await ArchiveResultAsync(settings, ct);
            BuildPreview();
            RememberExport(run.PrimaryPath);
            RequestPersonalWorkspaceAfterExtraction();
            await RefreshPagesAsync(run.Table.Page);
            TableBox.ItemsSource = new[] { run.Table }; TableBox.SelectedItem = run.Table;
            _settings = settings; _settings.Theme = ThemeService.CurrentTheme; _settings.Save();
            ConnectionText.Text = "Source et réglages mémorisés";
            SetStatus(run.Data.Status + " · Fichier : " + run.PrimaryPath);
            AddLog("Fichiers enregistrés dans : " + settings.ExportFolder);
        });
    }

    private async void RememberPage_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Mémorisation de la source…", async ct =>
        {
            var page = SelectedPage ?? _browser.ActivePage ?? throw new InvalidOperationException("Ouvre d’abord la recherche dans le navigateur piloté.");
            var url = TargetUrlPolicy.PersistedTarget(page.Url);
            if (await page.Locator("input[type=password]:visible").CountAsync() > 0)
                throw new InvalidOperationException("Termine la connexion et affiche la recherche GLPI avant de la mémoriser.");
            PortalUrlBox.Text = url;
            var settings = ReadSettings();
            var table = SelectedTable;
            if (table != null && ReferenceEquals(table.Page, page)) settings.RememberTable(table);
            else
            {
                var tables = await _browser.InspectAsync(page, ct);
                if (tables.Count == 1) settings.RememberTable(tables[0]);
            }
            _settings = settings; _settings.Save();
            SetStatus("Adresse source complète et réglages enregistrés. La prochaine session : Extraire tout.");
            AddLog("SOURCE · adresse mémorisée avec ses paramètres de recherche : " + AppPaths.SafeUrl(url));
        });
    }

    private void LoadSettingsFields()
    {
        PortalUrlBox.Text = _settings.PortalUrl;
        BrowserBox.SelectedIndex = _settings.BrowserChannel == "chrome" ? 1 : 0;
        AutoModeBox.SelectedIndex = _settings.AutoMode == "pagination" ? 1 : 0;
        AutoSaveXlsxCheckBox.IsChecked = _settings.AutoSaveXlsx;
        ReadySelectorBox.Text = _settings.ReadySelector;
        NextSelectorBox.Text = _settings.NextSelector;
        TimeoutBox.Text = _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        DownloadTimeoutBox.Text = _settings.DownloadTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        MaxPagesBox.Text = _settings.MaxPages.ToString(CultureInfo.InvariantCulture);
        MaxRowsBox.Text = _settings.MaxRows.ToString(CultureInfo.InvariantCulture);
        ExportFolderBox.Text = _settings.ExportFolder;
        ExpectedTotalBox.Text = _settings.ExpectedTotal?.ToString(CultureInfo.InvariantCulture) ?? "";
        NativeMenuSelectorBox.Text = _settings.NativeMenuSelector;
        NativeFormatSelectorBox.Text = _settings.NativeFormatSelector;
        NativeFormatValueBox.Text = _settings.NativeFormatValue;
        NativeScopeSelectorBox.Text = _settings.NativeScopeSelector;
        NativeScopeValueBox.Text = _settings.NativeScopeValue;
        NativeSubmitSelectorBox.Text = _settings.NativeSubmitSelector;
        LoadTicketSettingsFields(_settings);
        LoadDashboardSettingsFields(_settings);
        LoadCitySettingsFields(_settings);
        LoadQuerySettingsFields(_settings);
    }

    private static int Number(TextBox box, string label, int minimum, int maximum)
    {
        var raw = box.Text.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Trim();
        if (!int.TryParse(raw, out var number) || number < minimum || number > maximum)
            throw new InvalidOperationException($"{label} : indique un entier compris entre {minimum:N0} et {maximum:N0}.");
        return number;
    }

    private AppSettings ReadSettings()
    {
        var origin = _settings.SelectorOrigin;
        if (ReadySelectorBox.Text.Trim() != _settings.ReadySelector || NextSelectorBox.Text.Trim() != _settings.NextSelector)
            origin = SelectedTable?.FrameOrigin ?? BrowserService.Origin(SelectedPage?.Url ?? PortalUrlBox.Text);
        var folder = Environment.ExpandEnvironmentVariables(ExportFolderBox.Text.Trim());
        if (folder.Length == 0) folder = AppPaths.DefaultExports;
        if (!Path.IsPathFullyQualified(folder)) throw new InvalidOperationException("Choisis un chemin complet pour le dossier d’export.");
        var nativeOrigin = _settings.NativeSelectorOrigin;
        if (NativeMenuSelectorBox.Text.Trim() != _settings.NativeMenuSelector || NativeFormatSelectorBox.Text.Trim() != _settings.NativeFormatSelector ||
            NativeFormatValueBox.Text.Trim() != _settings.NativeFormatValue || NativeScopeSelectorBox.Text.Trim() != _settings.NativeScopeSelector ||
            NativeScopeValueBox.Text.Trim() != _settings.NativeScopeValue || NativeSubmitSelectorBox.Text.Trim() != _settings.NativeSubmitSelector)
            nativeOrigin = SelectedTable?.FrameOrigin ?? BrowserService.Origin(SelectedPage?.Url ?? PortalUrlBox.Text);
        var settings = new AppSettings
        {
            PortalUrl = PortalUrlBox.Text.Trim(),
            BrowserChannel = (BrowserBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "chrome" ? "chrome" : "msedge",
            Theme = ThemeService.CurrentTheme,
            InterfaceZoomPercent = _settings.InterfaceZoomPercent,
            AutoMode = (AutoModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "pagination" ? "pagination" : "native",
            AutoSaveXlsx = AutoSaveXlsxCheckBox.IsChecked == true,
            ReadySelector = ReadySelectorBox.Text.Trim(), NextSelector = NextSelectorBox.Text.Trim(), SelectorOrigin = origin,
            TimeoutSeconds = Number(TimeoutBox, "Attente", 1, 1800),
            DownloadTimeoutSeconds = Number(DownloadTimeoutBox, "Délai export", 1, 7200),
            MaxPages = Number(MaxPagesBox, "Pages maximum", 1, 10000),
            MaxRows = Number(MaxRowsBox, "Lignes maximum", 1, 1000000),
            ExpectedTotal = string.IsNullOrWhiteSpace(ExpectedTotalBox.Text) ? null : Number(ExpectedTotalBox, "Total attendu", 0, 10000000),
            ExportFolder = folder,
            TableSelector = _settings.TableSelector, TableHeaderFingerprint = _settings.TableHeaderFingerprint,
            TableFrameOrigin = _settings.TableFrameOrigin, TableFrameName = _settings.TableFrameName, TableTargetUrl = _settings.TableTargetUrl,
            NativeSelectorOrigin = nativeOrigin,
            NativeMenuSelector = NativeMenuSelectorBox.Text.Trim(), NativeFormatSelector = NativeFormatSelectorBox.Text.Trim(),
            NativeFormatValue = NativeFormatValueBox.Text.Trim(), NativeScopeSelector = NativeScopeSelectorBox.Text.Trim(),
            NativeScopeValue = NativeScopeValueBox.Text.Trim(), NativeSubmitSelector = NativeSubmitSelectorBox.Text.Trim(),
            Cities = _citySettingsView?.ReadSettings() ?? _settings.Cities.Copy(),
            Queries = _settings.Queries.Copy(),
            Sources = _settings.Sources.Select(source => source.Copy()).ToList(), ActiveSourceId = _settings.ActiveSourceId,
            History = _settings.History.Copy(), Automation = _settings.Automation.Copy(), Interface = _settings.Interface.Copy()
        };
        ApplyTicketSettingsFields(settings);
        ApplyDashboardSettingsFields(settings);
        return settings;
    }

    private IPage? SelectedPage => (PageBox.SelectedItem as PageChoice)?.Page;
    private TableChoice? SelectedTable => TableBox.SelectedItem as TableChoice;

    private void UpdateControls()
    {
        var page = SelectedPage is { IsClosed: false } && _browser.IsOpen;
        var table = page && SelectedTable != null;
        OpenBrowserButton.IsEnabled = !_busy;
        RunAutoButton.IsEnabled = !_busy;
        RememberPageButton.IsEnabled = !_busy && page;
        BrowserBox.IsEnabled = !_busy;
        AutoModeBox.IsEnabled = !_busy;
        AutoSaveXlsxCheckBox.IsEnabled = !_busy;
        PortalUrlBox.IsEnabled = !_busy;
        RefreshTabsButton.IsEnabled = !_busy && _browser.IsOpen;
        PageBox.IsEnabled = !_busy;
        TableBox.IsEnabled = !_busy;
        InspectButton.IsEnabled = !_busy && page;
        PickTableButton.IsEnabled = !_busy && page;
        HighlightButton.IsEnabled = !_busy && table;
        CurrentPageButton.IsEnabled = !_busy && table;
        AllPagesButton.IsEnabled = !_busy && table;
        NativeExportButton.IsEnabled = !_busy && page;
        ImportCsvButton.IsEnabled = !_busy;
        StopCaptureButton.IsEnabled = _busy && _capturing;
        CancelButton.IsEnabled = _busy;
        ExportCsvButton.IsEnabled = !_busy && _result != null && _result.Headers.Count > 0;
        ExportXlsxButton.IsEnabled = ExportCsvButton.IsEnabled;
        PickReadyButton.IsEnabled = !_busy && page;
        PickNextButton.IsEnabled = !_busy && table;
        SaveSettingsButton.IsEnabled = !_busy;
        ImportConfigButton.IsEnabled = !_busy;
        ExportConfigButton.IsEnabled = !_busy;
        ClearSelectorsButton.IsEnabled = !_busy;
        WorkProgress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateBrowserButton();
        UpdateTicketControls();
        UpdateDashboardControls();
        UpdateWorkspaceControls();
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action)
    {
        if (_busy || _closing) return;
        _busy = true;
        _operation = new();
        _currentOperationEntry = _operationJournal.Start(status, _settings.PortalUrl);
        _controlCenter?.ShowOperations(_operationJournal.Entries);
        BeginOperationPresentation(status);
        SetStatus(status);
        UpdateControls();
        try { await action(_operation.Token); }
        catch (OperationCanceledException)
        {
            _operationCancelled = true;
            SetStatus("Opération annulée."); AddLog("Opération annulée.");
            if (_result == null)
            {
                StatsText.Text = "Opération annulée";
                EmptyStateTitleText.Text = "Opération annulée";
                EmptyStateHintText.Text = "Vous pouvez relancer l’extraction avec le bouton Extraire tout.";
                if (_nativeReceiptPath != null) ResultSummaryText.Text = "Le fichier déjà reçu est conservé : " + _nativeReceiptPath;
            }
        }
        catch (Exception ex)
        {
            if (_currentOperationEntry != null) { _currentOperationEntry.Code = OperationJournal.ErrorCode(ex); _currentOperationEntry.Detail = AppPaths.SafeError(ex); }
            ShowError(ex);
        }
        finally
        {
            _operation.Dispose(); _operation = null;
            _busy = false; _capturing = false;
            FinishOperationPresentation();
            if (_currentOperationEntry != null)
            {
                _operationJournal.Finish(_currentOperationEntry, _operationCancelled ? "Annulé" : _operationFailed ? "Échec" : "Terminé",
                    _operationCancelled ? "CANCELLED" : _operationFailed ? _currentOperationEntry.Code : "OK",
                    _operationFailed ? _currentOperationEntry.Detail : StatusText.Text);
                _currentOperationEntry = null; _controlCenter?.ShowOperations(_operationJournal.Entries);
            }
            if (!_closing) UpdateControls();
        }
    }

    private async void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Ouverture du navigateur…", async ct =>
        {
            var settings = ReadSettings();
            _settings = settings; _settings.Save();
            ConnectionText.Text = "Attente automatique du SSO…";
            Topmost = false;
            await _browser.OpenAsync(settings, ct, background: false);
            await RefreshPagesAsync(_browser.ActivePage);
            SetStatus("Navigateur ouvert. Laisse le SSO se terminer, affiche la recherche puis mémorise la page. Les onglets sont suivis automatiquement.");
        });
    }

    private async Task RefreshPagesAsync(IPage? preferred = null)
    {
        var old = preferred ?? SelectedPage;
        var pages = await _browser.GetPagesAsync();
        _refreshingPages = true;
        try
        {
            PageBox.ItemsSource = pages;
            PageBox.SelectedItem = pages.FirstOrDefault(p => ReferenceEquals(p.Page, old)) ?? pages.FirstOrDefault(p => ReferenceEquals(p.Page, _browser.ActivePage)) ?? pages.LastOrDefault();
            if (pages.Count == 0 || (SelectedTable?.Page != null && !ReferenceEquals(SelectedTable.Page, SelectedPage))) TableBox.ItemsSource = null;
        }
        finally { _refreshingPages = false; }
    }

    private async void TabsRefresh_Click(object sender, RoutedEventArgs e) => await RunAsync("Actualisation des onglets…", async _ => { TableBox.ItemsSource = null; await RefreshPagesAsync(); SetStatus("Onglets actualisés. Choisis la page à analyser."); });

    private async Task InspectPageAsync(CancellationToken ct, string? pickedSelector = null)
    {
        var page = SelectedPage ?? throw new InvalidOperationException("Choisis d’abord un onglet.");
        var tables = await _browser.InspectAsync(page, ct);
        TableBox.ItemsSource = tables;
        TableBox.SelectedItem = (pickedSelector == null ? null : tables.FirstOrDefault(t => t.Selector == pickedSelector)) ?? tables.FirstOrDefault();
        if (tables.Count == 0)
        {
            ConnectionText.Text = "Aucun tableau exploitable détecté.";
            SetStatus("Aucun tableau visible. Affiche une liste GLPI après le SSO, ou utilise l’export natif du portail.");
        }
        else
        {
            ConnectionText.Text = $"{tables.Count} tableau(x) détecté(s).";
            SetStatus("Choisis le tableau, utilise Repérer pour le vérifier, puis lance l’extraction.");
        }
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e) => await RunAsync("Détection des tableaux…", ct => InspectPageAsync(ct));

    private async void PickTable_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Clique sur le tableau dans le navigateur. Échap annule.", async ct =>
        {
            var page = SelectedPage ?? throw new InvalidOperationException("Choisis un onglet.");
            Topmost = false;
            var selection = await _browser.PickAsync(page, "table", SelectedTable?.Frame, ct);
            await InspectPageAsync(ct, selection.Pick.Selector);
            Activate();
        });
    }

    private async void Highlight_Click(object sender, RoutedEventArgs e) => await RunAsync("Repérage du tableau…", async ct =>
    {
        Topmost = false;
        await _browser.HighlightAsync(SelectedTable ?? throw new InvalidOperationException("Choisis un tableau."), ct);
        SetStatus("Le tableau choisi est encadré dans le navigateur.");
    });

    private async Task ExtractAsync(bool following)
    {
        await RunAsync("Lecture du tableau…", async ct =>
        {
            var settings = ReadSettings();
            var table = SelectedTable ?? throw new InvalidOperationException("Choisis un tableau.");
            var progress = new Progress<ExtractionProgress>(p =>
            {
                StatsText.Text = $"{p.Rows:N0} lignes · {p.Pages} page(s)";
                SetStatus(p.Message);
            });
            var extracted = await _browser.ExtractAsync(table, following, settings, progress, ct);
            _settings = settings;
            PublishCityResult(await EnrichCitiesAsync(extracted, settings, ct), settings);
            await ArchiveResultAsync(settings, ct);
            BuildPreview();
            SetStatus(extracted.Status + $" · {extracted.Rows.Count:N0} lignes.");
            foreach (var warning in extracted.Warnings) AddLog(warning);
            settings.RememberTable(table);
            _settings = settings; _settings.Save();
        });
    }

    private async void ExtractCurrent_Click(object sender, RoutedEventArgs e) => await ExtractAsync(false);
    private async void ExtractAll_Click(object sender, RoutedEventArgs e) => await ExtractAsync(true);

    private async Task ExportAsync(bool xlsx)
    {
        if (_result == null) return;
        await RunAsync("Préparation du fichier…", async _ =>
        {
            var settings = ReadSettings();
            Directory.CreateDirectory(settings.ExportFolder);
            var dialog = new SaveFileDialog
            {
                Title = xlsx ? "Exporter le tableau en Excel" : "Exporter le tableau en CSV",
                Filter = xlsx ? "Classeur Excel (*.xlsx)|*.xlsx" : "CSV UTF-8 (*.csv)|*.csv",
                DefaultExt = xlsx ? ".xlsx" : ".csv", AddExtension = true,
                InitialDirectory = settings.ExportFolder,
                FileName = $"GLPI-{DateTime.Now:yyyyMMdd-HHmmss}" + (xlsx ? ".xlsx" : ".csv")
            };
            if (dialog.ShowDialog(this) != true) { SetStatus("Enregistrement annulé."); return; }
            var data = _result;
            CancelButton.IsEnabled = false;
            var exported = await Task.Run(() => xlsx ? ExportService.WriteXlsx(data, dialog.FileName) : ExportService.WriteCsv(data, dialog.FileName));
            SetStatus($"Fichier enregistré : {exported.Path} · {exported.Rows:N0} lignes.");
            RememberExport(exported.Path);
            AddLog($"Export {(xlsx ? "Excel" : "CSV")} enregistré : {exported.Path}");
            if (exported.TruncatedCells > 0) AddLog($"{exported.TruncatedCells} cellule(s) dépasse(nt) la longueur maximale Excel. Les détails sont dans la feuille Informations.");
            _settings = settings; _settings.Save();
        });
    }

    private async void ExportXlsx_Click(object sender, RoutedEventArgs e) => await ExportAsync(true);
    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await ExportAsync(false);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        SetStatus("Annulation demandée…");
    }

    private async void NativeExport_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        await RunAsync("Dans GLPI, déclenche l’export souhaité. Le logiciel attend son téléchargement.", async ct =>
        {
            var settings = ReadSettings();
            var page = SelectedPage ?? throw new InvalidOperationException("Choisis un onglet.");
            ClearResult("Réception d’un nouvel export GLPI…");
            Topmost = false;
            var path = await _browser.CaptureDownloadAsync(page, settings.ExportFolder, ct);
            _nativeReceiptPath = path;
            AddLog("EXPORT · fichier natif conservé dans le dossier des exports, avant l’import.");
            if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                var imported = await Task.Run(() => NativeCsvImportService.Read(path, AppPaths.SafeUrl(page.Url), settings.ExpectedTotal, settings.MaxRows, false, ct,
                    diagnostics: message => Dispatcher.InvokeAsync(() => AddLog(message))), ct);
                _settings = settings;
                var enrichment = await EnrichCitiesAsync(imported, settings, ct);
                PublishCityResult(enrichment, settings);
                await ArchiveResultAsync(settings, ct);
                BuildPreview();
                foreach (var warning in enrichment.Data.Warnings) AddLog(warning);
            }
            else ResultSummaryText.Text = "Fichier GLPI reçu. L’aperçu automatique prend en charge le CSV ; ce fichier est disponible dans le dossier des exports.";
            SetStatus("Export GLPI reçu : " + path);
            AddLog("Le fichier natif est conservé tel que fourni par GLPI ; sa portée dépend de l’option choisie dans le portail.");
        });
    }

    private void StopCapture_Click(object sender, RoutedEventArgs e) => Cancel_Click(sender, e);

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Ouverture d’un fichier déjà reçu…", async ct =>
        {
            var settings = ReadSettings();
            var dialog = new OpenFileDialog
            {
                Title = "Importer un relevé CSV ou Excel",
                Filter = "Relevés CSV et Excel (*.csv;*.txt;*.xlsx)|*.csv;*.txt;*.xlsx|CSV ou texte|*.csv;*.txt|Classeur Excel|*.xlsx",
                Multiselect = false, CheckFileExists = true,
                InitialDirectory = Directory.Exists(settings.ExportFolder) ? settings.ExportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dialog.ShowDialog(this) != true) { SetStatus("Import annulé."); return; }
            ClearResult("Lecture du fichier local, sans nouvelle connexion au portail…");
            _nativeReceiptPath = dialog.FileName;
            // A local file alone cannot prove which GLPI search/export option produced it.
            var imported = await Task.Run(() => Path.GetExtension(dialog.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxImportService.Read(dialog.FileName, settings.MaxRows, ct)
                : NativeCsvImportService.Read(dialog.FileName, "", null, settings.MaxRows, false, ct,
                    diagnostics: message => Dispatcher.InvokeAsync(() => AddLog(message))), ct);
            imported.Provenance.SourceKind = "file";
            imported.Provenance.OriginalFileName = Path.GetFileName(dialog.FileName);
            _settings = settings;
            var enrichment = await EnrichCitiesAsync(imported, settings, ct);
            PublishCityResult(enrichment, settings);
            await ArchiveResultAsync(settings, ct);
            BuildPreview();
            foreach (var warning in enrichment.Data.Warnings) AddLog("CONTRÔLE · " + warning);
            var savedPath = dialog.FileName;
            if (settings.AutoSaveXlsx)
            {
                Directory.CreateDirectory(settings.ExportFolder);
                SetStatus("Enregistrement du classeur Excel…");
                savedPath = Path.Combine(settings.ExportFolder, $"GLPI-CSV-importe-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.xlsx");
                var exported = await Task.Run(() => ExportService.WriteXlsx(enrichment.Data, savedPath));
                if (exported.TruncatedCells > 0) AddLog($"EXCEL · {exported.TruncatedCells} cellules trop longues pour Excel ; CSV original conservé.");
            }
            SetStatus($"Fichier importé : {enrichment.Data.Rows.Count:N0} lignes. Périmètre GLPI non confirmé · Fichier : {savedPath}");
            RememberExport(savedPath);
            _settings = settings; _settings.Save();
        });
    }

    private async void PickReady_Click(object sender, RoutedEventArgs e) => await PickSettingAsync("ready");
    private async void PickNext_Click(object sender, RoutedEventArgs e) => await PickSettingAsync("next");

    private async Task PickSettingAsync(string kind)
    {
        await RunAsync("Sélectionne l’élément dans le navigateur. Le clic de sélection n’active pas son action.", async ct =>
        {
            var page = SelectedPage ?? throw new InvalidOperationException("Choisis un onglet.");
            Topmost = false;
            var selected = await _browser.PickAsync(page, kind, kind == "next" ? SelectedTable?.Frame : page.MainFrame, ct);
            if (kind == "ready") ReadySelectorBox.Text = selected.Pick.Selector;
            else NextSelectorBox.Text = selected.Pick.Selector;
            _settings = ReadSettings(); _settings.SelectorOrigin = selected.Origin; _settings.Save();
            SetStatus("Repère enregistré pour les prochaines sessions.");
            Activate();
        });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Enregistrement des réglages et recalcul du relevé…", async ct =>
        {
            var settings = ReadSettings();
            await SaveAndReapplyCitiesAsync(settings, ct);
            OnExtractionChanged();
            SetStatus("Réglages enregistrés et calculs actualisés.");
            AddLog("Réglages enregistrés ; villes et dashboards recalculés sur le relevé disponible.");
        });
    }

    private void ClearSelectors_Click(object sender, RoutedEventArgs e)
    {
        ReadySelectorBox.Clear(); NextSelectorBox.Clear();
        _settings.ReadySelector = ""; _settings.NextSelector = ""; _settings.SelectorOrigin = "";
        NativeMenuSelectorBox.Clear(); NativeFormatSelectorBox.Clear(); NativeFormatValueBox.Clear();
        NativeScopeSelectorBox.Clear(); NativeScopeValueBox.Clear(); NativeSubmitSelectorBox.Clear();
        _settings.NativeMenuSelector = ""; _settings.NativeFormatSelector = ""; _settings.NativeFormatValue = "";
        _settings.NativeScopeSelector = ""; _settings.NativeScopeValue = ""; _settings.NativeSubmitSelector = ""; _settings.NativeSelectorOrigin = "";
        _settings.TableSelector = ""; _settings.TableHeaderFingerprint = ""; _settings.TableFrameOrigin = ""; _settings.TableFrameName = ""; _settings.TableTargetUrl = "";
        try { _settings.Save(); SetStatus("Détection automatique rétablie."); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ChooseExportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choisir le dossier des exports", Multiselect = false };
        if (dialog.ShowDialog(this) == true) ExportFolderBox.Text = dialog.FolderName;
    }

    private void OpenExports_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = ReadSettings().ExportFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Monitor_Tick(object? sender, EventArgs e)
    {
        if (_monitorBusy || _closing) return;
        _monitorBusy = true;
        try
        {
            if (!_busy)
            {
                var status = await _browser.SessionAsync(SelectedPage, _settings);
                ConnectionText.Text = status.Text;
            }
            await RefreshPagesAsync(_browser.ActivePage);
            UpdateControls();
        }
        catch (PlaywrightException) { ConnectionText.Text = "Navigation en cours…"; }
        catch (Exception ex) { ConnectionText.Text = "État indisponible : " + AppPaths.SafeError(ex); }
        finally { _monitorBusy = false; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closedAllowed) return;
        _foregroundLaunchGuard?.Dispose(); _foregroundLaunchGuard = null;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _monitor.Stop(); _filterTimer.Stop(); _operation?.Cancel();
        DisposeControlCenter();
        DisposeWorkspace();
        DisposeTicketWorkspace();
        DisposeDashboardWorkspace();
        DisposeCityWorkspace();
        DisposeQueryWorkspace();
        Topmost = false;
        SetStatus("Fermeture du navigateur piloté…");
        try { await _browser.DisposeAsync(); }
        catch (Exception ex) { AppPaths.AppendLog("Fermeture : " + AppPaths.SafeError(ex)); }
        // DisposeAsync can complete synchronously when no browser was opened.
        // Let the cancelled Closing event return before requesting the final close.
        _ = Dispatcher.InvokeAsync(() =>
        {
            _closedAllowed = true;
            Close();
        });
    }
}
