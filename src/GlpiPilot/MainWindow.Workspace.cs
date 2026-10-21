using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot;

public partial class MainWindow
{
    private readonly Stopwatch _workspaceClock = new();
    private readonly List<(Border Card, TextBlock Label)> _phaseCards = new();
    private DispatcherTimer? _workspaceClockTimer;
    private bool _workspaceInitialized, _automaticOperation, _operationFailed, _operationCancelled;
    private bool? _wideNavigation;
    private string? _lastExportPath;
    private ExtractionResult? _retainedResult;
    private string _retainedSummary = "", _retainedStats = "";
    private CommandPaletteWindow? _commandPalette;

    private void InitializeWorkspace()
    {
        var labels = new[] { "Connexion", "Recherche", "Récupération", "Vérification", "Enregistrement", "Fichier prêt" };
        for (var index = 0; index < labels.Length; index++)
        {
            var label = new TextBlock { Text = $"{index + 1}  {labels[index]}", FontSize = 11.5 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var card = new Border { Child = label, Padding = new Thickness(9, 5, 9, 5), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 7, 3) };
            card.SetResourceReference(Border.BackgroundProperty, "FieldBrush");
            _phaseCards.Add((card, label)); OperationStepsPanel.Children.Add(card);
        }
        _automatic.PhaseChanged += phase => Dispatcher.InvokeAsync(() =>
        {
            if (_automaticOperation && _busy && !_closing) ShowAutomaticPhase(phase);
        });
        _workspaceClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _workspaceClockTimer.Tick += (_, _) => UpdateOperationElapsed();
        PortalUrlBox.TextChanged += (_, _) => { if (_workspaceInitialized) UpdateControls(); };
        InitializeInterfaceZoom();
        ShellRoot.SizeChanged += WorkspaceSizeChanged;
        PreviewHeader.SizeChanged += WorkspaceSizeChanged;
        PreviewKeyDown += Workspace_KeyDown;
        ContentRendered += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_settings.PortalUrl)) { PortalUrlBox.Focus(); Keyboard.Focus(PortalUrlBox); }
        };
        _workspaceInitialized = true;
        UpdateWorkspaceLayout();
        UpdateWorkspaceControls();
    }

    private void UpdateWorkspaceLayout()
    {
        if (!_workspaceInitialized) return;
        UpdateResponsiveChrome();
    }

    private bool HasValidSource()
    {
        try { _ = TargetUrlPolicy.Validate(PortalUrlBox.Text); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private void UpdateWorkspaceControls()
    {
        if (!_workspaceInitialized) return;
        var valid = HasValidSource();
        var hasSource = !string.IsNullOrWhiteSpace(PortalUrlBox.Text);
        var remembered = valid && string.Equals(TargetUrlPolicy.PersistedTarget(PortalUrlBox.Text), _settings.PortalUrl, StringComparison.Ordinal);
        SourceStateText.Text = !hasSource ? "Première utilisation : collez l’adresse de la recherche GLPI. Vous pourrez ensuite relancer en un clic."
            : !valid ? "Utilisez une adresse complète http:// ou https:// de recherche GLPI."
            : remembered ? "Source enregistrée sur ce poste · recherche et filtres conservés."
            : "Source modifiée · Enregistrer la mémorise ; Extraire tout l’enregistre aussi automatiquement.";
        SourceStateText.SetResourceReference(TextBlock.ForegroundProperty, !valid && hasSource ? "WarningBrush" : remembered ? "SuccessBrush" : "MutedBrush");
        SaveSourceButton.IsEnabled = !_busy && valid && !remembered;
        RunAutoButton.IsEnabled = !_busy && valid;
        RunAutoButton.Content = _busy ? "En cours…" : "Extraire tout";
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumnsButton.IsEnabled = !_busy && _preview != null && ResultGrid.Columns.Count > 0;
        SearchBox.IsEnabled = !_busy && !_closing;
        UpdateCityControls();
        UpdateQueryControls();
        PersonalWorkspaceButton.IsEnabled = !_busy && !_closing;
        var hasFile = !string.IsNullOrEmpty(_lastExportPath);
        OpenLastExportButton.Visibility = RevealLastExportButton.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        OpenLastExportButton.IsEnabled = RevealLastExportButton.IsEnabled = !_busy && hasFile;
        var sourceName = _result != null && Uri.TryCreate(_result.SourceUrl, UriKind.Absolute, out var sourceUri) ? sourceUri.Host : "CSV local";
        WorkspaceContextText.Text = _result == null ? "Votre espace de pilotage GLPI"
            : $"{sourceName} · {_result.Rows.Count:N0} lignes · {_result.ExtractedAt.ToLocalTime():dd/MM HH:mm}"
                + (ReferenceEquals(_retainedResult, _result) ? " · actualisation en cours" : _result.Complete ? " · complet" : " · à vérifier");
        WorkspaceContextText.ToolTip = _result == null ? "Extraction, statistiques, techniciens et détail des tickets."
            : "Source du relevé affiché : " + (string.IsNullOrWhiteSpace(_result.SourceUrl) ? "CSV local, provenance GLPI non confirmée" : AppPaths.SafeUrl(_result.SourceUrl));
        UpdateWorkspaceLayout();
    }

    private void SaveSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        try
        {
            var settings = ReadSettings();
            settings.PortalUrl = TargetUrlPolicy.PersistedTarget(settings.PortalUrl);
            _ = TargetUrlPolicy.Validate(settings.PortalUrl);
            settings.Save(); _settings = settings;
            PortalUrlBox.Text = settings.PortalUrl;
            SetStatus("Source enregistrée. La prochaine session : Extraire tout.");
            AddLog("SOURCE · adresse saisie enregistrée avec ses paramètres de recherche : " + AppPaths.SafeUrl(settings.PortalUrl));
            UpdateControls();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void BeginOperationPresentation(string title)
    {
        _operationFailed = _operationCancelled = _automaticOperation = false;
        _workspaceClock.Restart(); _workspaceClockTimer?.Start();
        OperationPanel.Visibility = Visibility.Visible;
        OperationStepsPanel.Visibility = Visibility.Collapsed;
        OperationTitleText.Text = title;
        OperationTitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        UpdateOperationElapsed();
    }

    private void BeginAutomaticPresentation()
    {
        _automaticOperation = true;
        OperationStepsPanel.Visibility = Visibility.Visible;
        ShowAutomaticPhase(AutomaticRunPhase.Connection);
    }

    private void ShowAutomaticPhase(AutomaticRunPhase phase)
    {
        for (var index = 0; index < _phaseCards.Count; index++)
        {
            var active = index == (int)phase;
            _phaseCards[index].Card.SetResourceReference(Border.BackgroundProperty, active ? "SelectedBrush" : "FieldBrush");
            _phaseCards[index].Label.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentBrush" : "MutedBrush");
            _phaseCards[index].Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void UpdateOperationElapsed()
    {
        var elapsed = _workspaceClock.Elapsed;
        OperationElapsedText.Text = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private bool RetainResultDuringRefresh(string message)
    {
        _nativeReceiptPath = null;
        if (_result == null) return false;
        _retainedResult = _result;
        _retainedSummary = ResultSummaryText.Text;
        _retainedStats = StatsText.Text;
        ResultSummaryText.Text = $"Relevé du {_result.ExtractedAt.ToLocalTime():dd/MM à HH:mm} conservé pendant l’actualisation. {message}";
        StatsText.Text = $"{_result.Rows.Count:N0} lignes conservées · actualisation en cours";
        UpdateWorkspaceControls();
        return true;
    }

    private void FinishOperationPresentation()
    {
        _workspaceClock.Stop(); _workspaceClockTimer?.Stop(); UpdateOperationElapsed();
        if (_closing) return;
        if (_retainedResult != null && ReferenceEquals(_retainedResult, _result))
        {
            if (_previewQueryResult != null && ReferenceEquals(_previewSource, _result))
                UpdatePreviewSummary(_result, _previewQueryResult, _previewSearching);
            else { StatsText.Text = _retainedStats; ResultSummaryText.Text = _retainedSummary; }
            ResultSummaryText.Text += (_operationCancelled
                ? " Actualisation annulée : le relevé précédent reste disponible."
                : _operationFailed ? " Actualisation interrompue : le relevé précédent reste disponible."
                : " Le relevé précédent reste disponible.");
            AddLog("RELEVÉ · données précédentes conservées ; aucun nouveau résultat n’a remplacé le relevé affiché.");
        }
        _retainedResult = null; _retainedSummary = _retainedStats = "";
        OperationStepsPanel.Visibility = Visibility.Collapsed;
        if (_operationFailed || _operationCancelled)
        {
            OperationTitleText.Text = _operationCancelled ? "Opération annulée" : "Une action demande votre attention · voir le journal";
            OperationTitleText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
        else if (_automaticOperation && _result != null)
        {
            OperationTitleText.Text = $"Fichier prêt · {_result.Rows.Count:N0} lignes · " + (_result.Complete ? "relevé complet" : "exhaustivité à vérifier");
            OperationTitleText.SetResourceReference(TextBlock.ForegroundProperty, _result.Complete ? "SuccessBrush" : "WarningBrush");
        }
        else OperationPanel.Visibility = Visibility.Collapsed;
        _automaticOperation = false;
        UpdateWorkspaceControls();
    }

    private void RememberExport(string path)
    {
        _lastExportPath = Path.GetFullPath(path);
        OpenLastExportButton.ToolTip = "Ouvrir " + Path.GetFileName(path);
        RevealLastExportButton.ToolTip = "Sélectionner " + Path.GetFileName(path) + " dans l’Explorateur Windows.";
        UpdateWorkspaceControls();
    }

    private void OpenLastExport_Click(object sender, RoutedEventArgs e) => OpenLastExport(reveal: false);
    private void RevealLastExport_Click(object sender, RoutedEventArgs e) => OpenLastExport(reveal: true);
    private void OpenLastExport(bool reveal)
    {
        if (_busy || _closing || string.IsNullOrEmpty(_lastExportPath)) return;
        try
        {
            if (!File.Exists(_lastExportPath)) throw new FileNotFoundException("Le dernier fichier a été déplacé ou supprimé. Ouvrez le dossier des exports pour le retrouver.");
            if (reveal)
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add("/select,"); start.ArgumentList.Add(_lastExportPath); Process.Start(start);
            }
            else Process.Start(new ProcessStartInfo(_lastExportPath) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ClearPreviewSearch_Click(object sender, RoutedEventArgs e) { if (_busy || _closing) return; SearchBox.Clear(); SearchBox.Focus(); }
    private void PersonalWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        MainTabs.SelectedItem = TechniciansTab;
        if (!string.IsNullOrWhiteSpace(_settings.Dashboard.PersonalTechnician)) SelectTechnician(_settings.Dashboard.PersonalTechnician);
    }

    private void CommandPalette_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_commandPalette != null) { _commandPalette.Activate(); return; }
        var commands = new List<WorkspaceCommand>();
        void Go(string title, string description, TabItem tab, string shortcut) => commands.Add(new(title, description, shortcut, () => MainTabs.SelectedItem = tab));
        void Click(string title, string description, Button button, string shortcut = "") => commands.Add(new(title, description, shortcut,
            () => { if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }, button.IsEnabled));
        Go("Extraction", "Retrouver les lignes du relevé, rechercher et ouvrir les tickets.", ExtractionTab, "Ctrl 1");
        Go("Dashboard", "Statistiques de l’équipe, météo et widgets personnalisables.", DashboardTab, "Ctrl 2");
        Go("Techniciens", "Choisir une identité et consulter chaque tableau de bord.", TechniciansTab, "Ctrl 3");
        Click("Mon espace", "Ouvrir le tableau de bord de votre identité mémorisée.", PersonalWorkspaceButton);
        Go("Villes et tournées", "Regrouper les tickets par ville et préparer les visites des techniciens.", ToursTab, "Ctrl 4");
        commands.Add(new("Correspondances des villes", "Définir Marseille = MA, Mars, Marseille et vos propres alias.", "", OpenCitySettings));
        Go("Atelier requêtes", "Bibliothèque de requêtes, filtres personnalisés et widgets enregistrés.", QueriesTab, "Ctrl 5");
        Go("Détail du ticket", "Consulter les rubriques et le traitement du ticket.", TicketDetailTab, "Ctrl 6");
        Go("Réglages", "Source, navigateur, colonnes du dashboard et sections de ticket.", SettingsTab, "Ctrl 7");
        Go("Outils manuels", "Importer un CSV ou agir sur la recherche dans le navigateur.", ManualToolsTab, "Ctrl 8");
        Go("Aide", "Retrouver les explications et les raccourcis.", HelpTab, "F1");
        Click("Extraire tout", "Attendre le SSO, récupérer toutes les pages et enregistrer le résultat.", RunAutoButton, "Ctrl R");
        Click("Afficher les colonnes", "Choisir les colonnes de l’aperçu et mémoriser leur présentation.", PreviewColumnsButton);
        Click("Exporter en Excel", "Enregistrer toutes les lignes récupérées dans un classeur.", ExportXlsxButton, "Ctrl E");
        Click("Exporter en CSV", "Enregistrer toutes les lignes récupérées en CSV.", ExportCsvButton);
        Click("Ouvrir le dernier fichier", "Accéder directement au fichier produit pendant cette session.", OpenLastExportButton);
        Click("Voir le navigateur", "Accéder à GLPI ou terminer une étape du SSO.", ShowBrowserButton);
        Click("Importer un CSV", "Reprendre un relevé local sans ouvrir GLPI.", ImportCsvButton);
        Click("Importer une configuration", "Reprendre les réglages et les dispositions d’un collègue.", ImportConfigButton);
        Click("Exporter la configuration", "Partager les réglages et les widgets sans transférer le SSO.", ExportConfigButton);
        Click("Changer de thème", "Basculer entre le thème clair et le thème sombre.", ThemeToggleButton);
        Click("Agrandir l’interface", "Augmenter le zoom de 5 % et mémoriser cette taille.", ZoomInButton, "Ctrl +");
        Click("Réduire l’interface", "Réduire le zoom de 5 % et mémoriser cette taille.", ZoomOutButton, "Ctrl −");
        Click("Zoom 100 %", "Revenir à la taille normale de l’interface.", ZoomResetButton, "Ctrl 0");
        Click("Copier le diagnostic", "Préparer les étapes techniques à partager en cas d’erreur.", CopyDiagnosticButton);
        var dialog = new CommandPaletteWindow(commands) { Owner = this };
        _commandPalette = dialog;
        try { if (dialog.ShowDialog() == true) dialog.SelectedCommand?.Execute(); }
        finally { _commandPalette = null; }
    }

    private void Workspace_KeyDown(object sender, KeyEventArgs e)
    {
        if (_closing || e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleInterfaceZoomKey(key, Keyboard.Modifiers)) { e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (key >= Key.D1 && key <= Key.D9) { MainTabs.SelectedIndex = key - Key.D1; e.Handled = true; return; }
            switch (key)
            {
                case Key.K: CommandPalette_Click(CommandPaletteButton, new()); break;
                case Key.L: MainTabs.SelectedItem = ExtractionTab; PortalUrlBox.Focus(); PortalUrlBox.SelectAll(); break;
                case Key.F:
                    var search = MainTabs.SelectedItem == TicketDetailTab ? TicketSearchBox : SearchBox;
                    if (search == SearchBox) MainTabs.SelectedItem = ExtractionTab;
                    search.Focus(); search.SelectAll(); break;
                case Key.R: if (RunAutoButton.IsEnabled) RunAutoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                case Key.E: if (ExportXlsxButton.IsEnabled) ExportXlsxButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.F1) { MainTabs.SelectedItem = HelpTab; e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Enter && ResultGrid.IsKeyboardFocusWithin && e.OriginalSource is not TextBoxBase && OpenTicketButton.IsEnabled)
        { OpenTicketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
    }

    private void DisposeWorkspace()
    {
        _workspaceClockTimer?.Stop(); _workspaceClock.Stop();
        DisposeInterfaceZoom();
        ShellRoot.SizeChanged -= WorkspaceSizeChanged;
        PreviewHeader.SizeChanged -= WorkspaceSizeChanged;
        PreviewKeyDown -= Workspace_KeyDown;
        DisposePreviewWorkspace();
    }
}
