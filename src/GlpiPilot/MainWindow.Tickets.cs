using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
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
    private readonly ObservableCollection<TicketSectionDefinition> _ticketSections = new();
    private readonly LinkedList<TicketCacheEntry> _ticketCache = new();
    private DispatcherTimer? _ticketDebounceTimer;
    private CancellationTokenSource? _ticketLoadCancellation;
    private TicketLoadRequest? _pendingTicket;
    private TicketReference? _selectedTicket;
    private TicketDetailResult? _ticketDetail;
    private DataTable? _ticketPreview;
    private string? _ticketActiveCacheKey, _ticketDisplayedCacheKey;
    private bool _ticketWorkspaceInitialized, _ticketLoadingFields, _ticketSuppressSelection;
    private bool _ticketLoading, _ticketPumpActive;
    private bool _ticketNavigationUpdating;
    private TicketReaderView? _ticketReader;
    private long _ticketGeneration, _ticketActiveGeneration;
    private const int TicketCacheEntries = 6;
    private const long TicketCacheCharacters = 4_000_000;
    private const long TicketCacheCells = 120_000;

    private bool IsTicketDetailBusy => _ticketLoading;

    private void InitializeTicketWorkspace()
    {
        if (_ticketWorkspaceInitialized) return;
        _ticketWorkspaceInitialized = true;
        TicketSectionsGrid.ItemsSource = _ticketSections;
        _ticketDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _ticketDebounceTimer.Tick += TicketDebounce_Tick;
        ResultGrid.SelectionChanged += ResultGrid_TicketSelectionChanged;
        ResultGrid.MouseDoubleClick += ResultGrid_TicketDoubleClick;
        TicketSectionBox.SelectionChanged += (_, _) => BuildTicketSectionPreview();
        TicketSectionsGrid.SelectionChanged += (_, _) => UpdateTicketControls();
        TicketGrid.AutoGeneratingColumn += TicketGrid_AutoGeneratingColumn;
        TicketGrid.CurrentCellChanged += (_, _) => ShowTicketCellText();
        TicketNavigation.SelectionChanged += TicketNavigation_SelectionChanged;
        TicketSearchBox.TextChanged += (_, _) => { if (!_ticketNavigationUpdating) BuildTicketSectionPreview(); };
        TicketViewModeBox.SelectionChanged += (_, _) => { if (!_ticketNavigationUpdating) BuildTicketSectionPreview(); };
        _ticketReader = new TicketReaderView(); TicketReaderHost.Content = _ticketReader;
        InitializePersonalTicketWorkspace();
        TicketAutoLoadCheckBox.Checked += TicketAutoLoad_Changed;
        TicketAutoLoadCheckBox.Unchecked += TicketAutoLoad_Changed;
        UpdateTicketControls();
    }

    private void LoadTicketSettingsFields(AppSettings settings)
    {
        _ticketLoadingFields = true;
        try
        {
            var detail = settings.TicketDetails ?? new TicketDetailSettings();
            TicketAutoLoadCheckBox.IsChecked = detail.AutoLoadOnSelection;
            TicketIdColumnBox.Text = detail.IdColumn;
            TicketUrlTemplateBox.Text = detail.UrlTemplate;
            TicketIncludeAutoCheckBox.IsChecked = detail.IncludeAutoDetected;
            TicketAutoTreatmentCheckBox.IsChecked = detail.AutoReadTreatment;
            TicketMaxRowsBox.Text = detail.MaxRowsPerSection.ToString(CultureInfo.InvariantCulture);
            _ticketSections.Clear();
            foreach (var section in detail.Sections) _ticketSections.Add(section.Copy());
            TicketSectionsGrid.ItemsSource = _ticketSections;
            InvalidateTicketRequest();
            _ticketCache.Clear();
            _selectedTicket = null;
            ClearTicketPresentation("Détail du ticket", "Sélectionne un ticket dans Extraction.");
        }
        finally { _ticketLoadingFields = false; }
        UpdateTicketControls();
    }

    private void ApplyTicketSettingsFields(AppSettings settings)
    {
        if (!TicketSectionsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !TicketSectionsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Termine la modification de la section du ticket avant d’enregistrer.");
        if (_ticketSections.Count > 20)
            throw new InvalidOperationException("Configure au maximum 20 sections par ticket.");
        var idColumn = TicketIdColumnBox.Text.Trim();
        var urlTemplate = TicketUrlTemplateBox.Text.Trim();
        if (idColumn.Length > 512) throw new InvalidOperationException("Le nom de la colonne d’identifiant dépasse 512 caractères.");
        if (urlTemplate.Length > 8192) throw new InvalidOperationException("L’adresse modèle du ticket dépasse 8 192 caractères.");
        var sections = new List<TicketSectionDefinition>();
        foreach (var editor in _ticketSections)
        {
            var item = new TicketSectionDefinition
            {
                Name = (editor.Name ?? "").Trim(), Selector = (editor.Selector ?? "").Trim(),
                Kind = (editor.Kind ?? "table").Trim().ToLowerInvariant(), TabSelector = (editor.TabSelector ?? "").Trim()
            };
            if (item.Name.Length == 0 || item.Name.Length > 120)
                throw new InvalidOperationException("Chaque section doit avoir un nom de 1 à 120 caractères.");
            if (item.Selector.Length == 0 || item.Selector.Length > 2048)
                throw new InvalidOperationException("Chaque section doit avoir un sélecteur CSS de 1 à 2 048 caractères.");
            if (item.TabSelector.Length > 2048)
                throw new InvalidOperationException("Le sélecteur d’onglet dépasse 2 048 caractères.");
            if (item.Kind is not ("table" or "fields" or "text" or "timeline"))
                throw new InvalidOperationException("Le mode d’une section doit être table, fields, text ou timeline.");
            sections.Add(item);
        }
        settings.TicketDetails = new TicketDetailSettings
        {
            AutoLoadOnSelection = TicketAutoLoadCheckBox.IsChecked == true,
            IdColumn = idColumn, UrlTemplate = urlTemplate,
            IncludeAutoDetected = TicketIncludeAutoCheckBox.IsChecked == true,
            AutoReadTreatment = TicketAutoTreatmentCheckBox.IsChecked == true,
            MaxRowsPerSection = Number(TicketMaxRowsBox, "Lignes par section", 1, 100000),
            Sections = sections
        };
    }

    private void OnExtractionChanged()
    {
        InvalidateTicketRequest();
        _ticketCache.Clear();
        _selectedTicket = null;
        _ticketSuppressSelection = true;
        try { ResultGrid.UnselectAll(); }
        finally { _ticketSuppressSelection = false; }
        ClearTicketPresentation("Détail du ticket", "Sélectionne un ticket dans Extraction.");
        UpdateTicketControls();
    }

    private void DisposeTicketWorkspace()
    {
        _personalReminderTimer?.Stop();
        _desktopReminderNotifier?.Dispose();
        _personalDayCancellation?.Cancel();
        _ticketIntelligenceCancellation?.Cancel();
        InvalidateTicketRequest();
        _ticketDebounceTimer?.Stop();
        _ticketCache.Clear();
    }

    private void InvalidateTicketRequest()
    {
        _ticketGeneration++;
        _pendingTicket = null;
        _ticketDebounceTimer?.Stop();
        _ticketLoadCancellation?.Cancel();
    }

    private void UpdateTicketControls()
    {
        if (!_ticketWorkspaceInitialized) return;
        var free = !_busy && !_closing;
        var canSelect = !_closing && (!_busy || _ticketLoading);
        var selectedRow = ResultGrid.SelectedItem is DataRowView && _result != null;
        var section = TicketSectionBox.SelectedItem as TicketDetailSection;
        ResultGrid.IsEnabled = canSelect;
        OpenTicketButton.IsEnabled = canSelect && selectedRow;
        RefreshTicketButton.IsEnabled = canSelect && _selectedTicket != null;
        TicketBrowserButton.IsEnabled = free && _ticketDetail != null && _selectedTicket != null;
        ExportTicketXlsxButton.IsEnabled = free && section?.Data.Headers.Count > 0;
        RememberTicketSectionButton.IsEnabled = free && !string.IsNullOrWhiteSpace(section?.Selector);
        TicketSectionBox.IsEnabled = !_ticketLoading && _ticketDetail?.Sections.Count > 0;
        TicketNavigation.IsEnabled = free;
        TicketSearchBox.IsEnabled = free && section != null;
        TicketViewModeBox.IsEnabled = free && section != null;
        ExportTicketAllButton.IsEnabled = free && _ticketDetail?.Sections.Count > 0;
        TicketAutoLoadCheckBox.IsEnabled = free;
        TicketIncludeAutoCheckBox.IsEnabled = free;
        TicketAutoTreatmentCheckBox.IsEnabled = free;
        TicketIdColumnBox.IsEnabled = free;
        TicketUrlTemplateBox.IsEnabled = free;
        TicketMaxRowsBox.IsEnabled = free;
        TicketSectionsGrid.IsEnabled = free;
        AddTicketSectionButton.IsEnabled = free && _ticketSections.Count < 20;
        RemoveTicketSectionButton.IsEnabled = free && TicketSectionsGrid.SelectedItem is TicketSectionDefinition;
        if (free && !_ticketPumpActive && _pendingTicket != null && _ticketDebounceTimer?.IsEnabled == false)
            ArmTicketTimer();
    }

    private void ResultGrid_TicketSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ticketSuppressSelection || _ticketLoadingFields || _closing) return;
        if (_busy && !_ticketLoading) return;
        if (ResultGrid.SelectedItems.Count > 1)
        {
            InvalidateTicketRequest();
            TicketStateText.Text = "Sélection multiple : utilisez Dans ma journée pour ajouter ces tickets à votre file personnelle.";
            UpdateTicketControls(); return;
        }
        if (ResultGrid.SelectedItem is not DataRowView || _result == null)
        {
            InvalidateTicketRequest();
            _selectedTicket = null;
            ClearTicketPresentation("Détail du ticket", "Sélectionne un ticket dans Extraction.");
        }
        else if (TicketAutoLoadCheckBox.IsChecked == true)
            QueueSelectedTicket(showTab: false, force: false);
        else
        {
            InvalidateTicketRequest();
            _selectedTicket = null;
            ClearTicketPresentation("Ticket sélectionné", "Clique sur Détail du ticket ou double-clique sur sa ligne pour le lire.");
        }
        UpdateTicketControls();
    }

    private void TicketAutoLoad_Changed(object sender, RoutedEventArgs e)
    {
        if (_ticketLoadingFields || !_ticketWorkspaceInitialized || _closing) return;
        if (TicketAutoLoadCheckBox.IsChecked == true && ResultGrid.SelectedItem is DataRowView && !_busy)
            QueueSelectedTicket(showTab: false, force: false);
        else if (TicketAutoLoadCheckBox.IsChecked != true)
        {
            InvalidateTicketRequest();
            if (_ticketLoading) TicketStateText.Text = "Arrêt de la lecture automatique…";
        }
    }

    private void ResultGrid_TicketDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_closing || (_busy && !_ticketLoading)) return;
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow && element != ResultGrid)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        if (element is not DataGridRow row || row.Item is not DataRowView) return;
        ResultGrid.SelectedItem = row.Item;
        QueueSelectedTicket(showTab: true, force: false);
        e.Handled = true;
    }

    private void OpenTicket_Click(object sender, RoutedEventArgs e) => QueueSelectedTicket(showTab: true, force: false);

    private void RefreshTicket_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTicket != null) QueuePersonalTicketReference(_selectedTicket, true);
        else QueueSelectedTicket(showTab: true, force: true);
    }

    private void QueueSelectedTicket(bool showTab, bool force)
    {
        if (_closing || (_busy && !_ticketLoading)) return;
        if (showTab) MainTabs.SelectedItem = TicketDetailTab;
        try
        {
            if (_result == null || ResultGrid.SelectedItem is not DataRowView selected)
                throw new InvalidOperationException("Sélectionne d’abord une ligne dans l’onglet Extraction.");
            var sourceIndex = GetPreviewSourceIndex(selected.Row);
            if (!sourceIndex.HasValue || sourceIndex.Value < 0 || sourceIndex.Value >= _result.Rows.Count)
                throw new InvalidOperationException("L’aperçu a changé. Sélectionne de nouveau la ligne du ticket.");
            var values = _result.Rows[sourceIndex.Value];
            var settings = ReadSettings();
            var ticket = TicketReferenceResolver.Resolve(_result, values, settings);
            var key = ticket.Url + "\n" + JsonSerializer.Serialize(settings);
            if (!force && ((_pendingTicket?.CacheKey == key && _pendingTicket.Generation == _ticketGeneration) ||
                (_ticketLoading && _ticketActiveCacheKey == key && _ticketActiveGeneration == _ticketGeneration && _ticketLoadCancellation?.IsCancellationRequested == false) ||
                (_ticketDetail != null && _ticketDisplayedCacheKey == key))) return;
            InvalidateTicketRequest();
            _selectedTicket = ticket;
            ClearTicketPresentation("Ticket " + ticket.Id, "Lecture du détail en préparation…");
            _pendingTicket = new(ticket, settings, key, _ticketGeneration, force,
                DateTimeOffset.UtcNow.AddMilliseconds(300));
            ArmTicketTimer();
        }
        catch (Exception ex)
        {
            InvalidateTicketRequest();
            _selectedTicket = null;
            ClearTicketPresentation("Détail du ticket", "La ligne sélectionnée demande une vérification.");
            ReportTicketError(ex);
        }
        UpdateTicketControls();
    }

    private void ArmTicketTimer()
    {
        if (_ticketDebounceTimer == null || _pendingTicket == null || _closing) return;
        _ticketDebounceTimer.Stop();
        var remaining = _pendingTicket.NotBefore - DateTimeOffset.UtcNow;
        _ticketDebounceTimer.Interval = remaining > TimeSpan.FromMilliseconds(30) ? remaining : TimeSpan.FromMilliseconds(30);
        _ticketDebounceTimer.Start();
    }

    private async void TicketDebounce_Tick(object? sender, EventArgs e)
    {
        _ticketDebounceTimer?.Stop();
        if (_closing || _ticketPumpActive || _busy || _pendingTicket == null) return;
        var request = _pendingTicket;
        _pendingTicket = null;
        if (request.Generation != _ticketGeneration) return;
        _ticketPumpActive = true;
        try
        {
            if (!request.Force && GetCachedTicket(request.CacheKey) is { } cached)
            {
                PresentTicketResult(cached, fromCache: true, request.CacheKey);
                SetStatus("Détail disponible en mémoire. Actualiser relit le ticket dans GLPI.");
                return;
            }
            if (request.Force) RemoveCachedTicket(request.CacheKey);
            _ticketActiveCacheKey = request.CacheKey;
            _ticketActiveGeneration = request.Generation;
            _ticketLoading = true;
            await RunAsync("Lecture du détail du ticket…", async ct =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ticketLoadCancellation = linked;
                var progress = new Progress<ExtractionProgress>(p =>
                {
                    if (_closing || request.Generation != _ticketGeneration || !ReferenceEquals(_ticketLoadCancellation, linked)) return;
                    TicketStateText.Text = p.Message;
                    SetStatus(p.Message);
                });
                try
                {
                    var result = await _browser.ReadTicketDetailsAsync(request.Ticket, request.Settings, progress, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if (_closing || request.Generation != _ticketGeneration) return;
                    if (result.Ticket.Id != request.Ticket.Id || result.Ticket.Url != request.Ticket.Url)
                        throw new InvalidOperationException("Le résultat reçu ne correspond pas au ticket sélectionné. Relance la lecture.");
                    CacheTicket(request.CacheKey, result);
                    PresentTicketResult(result, fromCache: false, request.CacheKey);
                    SetStatus($"Détail lu : {result.Sections.Count} section(s). La liste de recherche reste conservée.");
                    AddLog($"DÉTAIL · lecture terminée ; {result.Sections.Count} sections, {result.Warnings.Count} avertissements.");
                }
                catch (OperationCanceledException)
                {
                    if (_closing || request.Generation != _ticketGeneration) return;
                    TicketStateText.Text = "Lecture du ticket annulée.";
                    TicketEmptyTitleText.Text = "Lecture annulée";
                    TicketEmptyHintText.Text = "Actualiser permet de reprendre la lecture de ce ticket.";
                    throw;
                }
                catch (Exception ex)
                {
                    if (!_closing && request.Generation == _ticketGeneration) ReportTicketError(ex);
                }
                finally { if (ReferenceEquals(_ticketLoadCancellation, linked)) _ticketLoadCancellation = null; }
            });
        }
        catch (Exception ex)
        {
            if (!_closing && request.Generation == _ticketGeneration) ReportTicketError(ex);
        }
        finally
        {
            _ticketLoading = false;
            _ticketActiveCacheKey = null;
            _ticketPumpActive = false;
            if (!_closing)
            {
                UpdateControls();
                if (_pendingTicket != null) ArmTicketTimer();
            }
        }
    }

    private void ClearTicketPresentation(string title, string message)
    {
        CaptureTicketReadingPosition(); FlushTicketDrafts();
        _readingTicketKey = _readingSectionKey = "";
        TicketCellTextBox.Clear(); TicketCellExpander.IsExpanded = false;
        _ticketDetail = null;
        _ticketDisplayedCacheKey = null;
        _ticketPreview = null;
        _ticketNavigationUpdating = true;
        TicketNavigation.ItemsSource = null; TicketSearchBox.Clear();
        _ticketNavigationUpdating = false;
        TicketSectionBox.ItemsSource = null;
        TicketGrid.ItemsSource = null;
        TicketTitleText.Text = title;
        TicketMetaText.Text = message;
        TicketStateText.Text = "La liste de résultats reste conservée.";
        TicketRowsText.Text = "Aucune section";
        TicketSummaryText.Text = "Le détail est lu dans un onglet dédié du navigateur, sans modifier le ticket.";
        TicketMenuSummaryText.Text = "Les rubriques apparaissent après lecture.";
        TicketSectionTitleText.Text = "Vue d’ensemble";
        TicketReaderHost.Visibility = Visibility.Collapsed;
        TicketEmptyPanel.Visibility = Visibility.Visible;
        TicketEmptyTitleText.Text = "Détail du ticket";
        TicketEmptyHintText.Text = message;
        RefreshPersonalTicketPresentation();
    }

    private void PresentTicketResult(TicketDetailResult result, bool fromCache, string cacheKey)
    {
        CaptureTicketReadingPosition(); FlushTicketDrafts();
        _readingTicketKey = _readingSectionKey = "";
        _ticketDetail = result;
        _ticketDisplayedCacheKey = cacheKey;
        _selectedTicket = result.Ticket;
        TicketTitleText.Text = "Ticket " + result.Ticket.Id + (string.IsNullOrWhiteSpace(result.Title) ? "" : " · " + result.Title);
        TicketMetaText.Text = $"{result.Sections.Count} section(s) · Lu le {result.ReadAt.LocalDateTime:dd/MM/yyyy à HH:mm:ss}";
        TicketStateText.Text = fromCache ? "Données conservées en mémoire. Actualiser relit le portail." : "Lecture terminée. Aucune modification apportée au ticket.";
        RememberPersonalTicketRead(result, !fromCache);
        RefreshPersonalTicketPresentation();
        BuildTicketNavigation();
        UpdateTicketControls();
    }

    private void BuildTicketNavigation(string? selectedKey = null)
    {
        if (_ticketDetail == null) return;
        var result = _ticketDetail;
        var items = new List<TicketNavigationEntry>();
        var fields = result.Sections.Where(section => section.Kind == "fields").ToList();
        if (fields.Count > 0)
        {
            var rows = fields.SelectMany(section => section.Data.Rows).Where(row => row.Length >= 2)
                .DistinctBy(row => (row[0], row[1])).ToList();
            var overview = DerivedTicketSection("Vue d’ensemble", "fields", fields[0], rows);
            overview.Data.Complete = fields.All(section => section.Data.Complete);
            overview.Data.Warnings = fields.SelectMany(section => section.Data.Warnings).Distinct().ToList();
            if (fields.Count == 1) { overview.Selector = fields[0].Selector; overview.TabSelector = fields[0].TabSelector; }
            items.Add(new("overview", "Vue d’ensemble", $"{rows.Count:N0} champs · lecture", overview));
            var descriptions = rows.Where(row => row[0].Contains("description", StringComparison.OrdinalIgnoreCase)).Select(row => new[] { row[1] }).Where(row => row[0].Length > 0).ToList();
            if (descriptions.Count > 0)
            {
                var description = DerivedTicketSection("Description", "text", overview, descriptions);
                description.Data.Headers = new() { "Texte" };
                items.Add(new("description", "Description", "Texte complet et sélectionnable", description));
            }
            foreach (var (key, label, terms) in new[]
            {
                ("actors", "Intervenants", new[] { "demandeur", "attribué", "technicien", "observateur", "fournisseur", "groupe", "modification par" }),
                ("qualification", "Qualification", new[] { "statut", "type", "urgence", "impact", "priorité", "catégorie", "entité", "lieu", "ville", "code postal" }),
                ("dates", "Dates et délais", new[] { "date", "délai", "durée", "sla", "ola", "échéance", "temps de" })
            })
            {
                var matchingRows = rows.Where(row => terms.Any(term => CultureInfo.CurrentCulture.CompareInfo.IndexOf(row[0], term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)).ToList();
                if (matchingRows.Count > 0) items.Add(new(key, label, $"{matchingRows.Count:N0} champs de la fiche", DerivedTicketSection(label, "fields", overview, matchingRows)));
            }
        }
        var timelines = result.Sections.Where(section => section.Kind == "timeline").ToList();
        if (timelines.Count > 0)
        {
            var timelineRows = timelines.SelectMany(section => section.Data.Rows).ToList();
            var timeline = DerivedTicketSection("Traitement du ticket", "timeline", timelines[0], timelineRows);
            timeline.Data.Complete = timelines.All(section => section.Data.Complete);
            timeline.Data.Warnings = timelines.SelectMany(section => section.Data.Warnings).Distinct().ToList();
            if (timelines.Count == 1) { timeline.Selector = timelines[0].Selector; timeline.TabSelector = timelines[0].TabSelector; }
            items.Add(new("treatment", "Traitement du ticket", $"{timelineRows.Count:N0} entrées lues", timeline));
            foreach (var (type, label) in new[] { ("Suivi", "Suivis"), ("Tâche", "Tâches"), ("Solution", "Solutions"), ("Document", "Documents"), ("Validation", "Validations"), ("Description initiale", "Description initiale") })
            {
                var rows = timelineRows.Where(row => row.Length > 0 && row[0] == type).ToList();
                if (rows.Count > 0) items.Add(new("treatment:" + type, "  " + label, $"{rows.Count:N0} éléments · traitement", DerivedTicketSection(label, "timeline", timeline, rows)));
            }
        }
        foreach (var (section, index) in result.Sections.Select((section, index) => (section, index)))
        {
            if (section.Kind is "fields" or "timeline" || section.TabSelector.Length > 0 && result.Tabs.Any(tab => tab.Key == section.TabKey || tab.Selector == section.TabSelector)) continue;
            items.Add(new("section:" + index, section.Name, $"{section.Data.Rows.Count:N0} lignes · section lue", section));
        }
        foreach (var tab in result.Tabs)
        {
            if (tab.Category == "overview" && tab.Loaded && fields.Count > 0 || tab.Category == "treatment" && timelines.Count > 0) continue;
            var tabSections = result.Sections.Where(section => section.TabKey == tab.Key || section.TabSelector == tab.Selector).ToList();
            var first = tabSections.FirstOrDefault();
            items.Add(new("tab:" + tab.Key, tab.Name,
                tab.Loaded ? (first == null ? "Aucun contenu lisible · actualiser pour réessayer" : $"{tabSections.Count} sections lues") : "Lire cet onglet à la demande", first, tab));
            for (var index = 1; index < tabSections.Count; index++)
            {
                var section = tabSections[index];
                items.Add(new("tab:" + tab.Key + ":" + index, "  " + section.Name, $"{section.Data.Rows.Count:N0} lignes", section));
            }
        }
        if (items.Count == 0)
            foreach (var section in result.Sections) items.Add(new("section:" + items.Count, section.Name, $"{section.Data.Rows.Count:N0} lignes", section));
        items = items.OrderBy(item => _workplace.SectionOrder.IndexOf(item.Key) is int order && order >= 0 ? order : int.MaxValue)
            .ThenBy(item => _workplace.FavoriteSections.Contains(item.Key) ? 0 : 1)
            .Select(item => _workplace.FavoriteSections.Contains(item.Key) ? item with { Label = "★ " + item.Label } : item).ToList();
        selectedKey ??= _workplace.Tickets.FirstOrDefault(item => item.Key == WorkplaceStore.TicketKey(result.Ticket))?.PreferredSection;
        if (string.IsNullOrEmpty(selectedKey)) selectedKey = _workplace.DefaultSection;
        _ticketNavigationUpdating = true;
        try
        {
            TicketNavigation.ItemsSource = items;
            TicketNavigation.SelectedItem = items.FirstOrDefault(item => item.Key == selectedKey) ?? items.FirstOrDefault();
            TicketMenuSummaryText.Text = $"{result.Sections.Count} sections lues · {result.Tabs.Count(tab => !tab.Loaded)} onglets à découvrir";
        }
        finally { _ticketNavigationUpdating = false; }
        if (TicketNavigation.SelectedItem is TicketNavigationEntry selected) PresentTicketNavigationEntry(selected);
        else { TicketSectionBox.ItemsSource = null; BuildTicketSectionPreview(); }
    }

    private static TicketDetailSection DerivedTicketSection(string name, string kind, TicketDetailSection source, List<string[]> rows) => new()
    {
        Name = name, Kind = kind,
        Data = new()
        {
            Title = name, Headers = source.Data.Headers.ToList(), Rows = rows,
            SourceUrl = source.Data.SourceUrl, ExtractedAt = source.Data.ExtractedAt,
            ExpectedTotal = rows.Count, Complete = source.Data.Complete, Pages = source.Data.Pages,
            Scope = "Vue des sections déjà lues du ticket", Status = "Vue des données lues",
            Warnings = source.Data.Warnings.ToList()
        }
    };

    private async void TicketNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ticketNavigationUpdating || TicketNavigation.SelectedItem is not TicketNavigationEntry entry || _closing) return;
        if (entry.Section != null || entry.Tab == null || entry.Tab.Loaded) { PresentTicketNavigationEntry(entry); return; }
        if (_busy || _selectedTicket == null || _ticketDetail == null) return;
        var ticket = _selectedTicket;
        var displayed = _ticketDetail;
        var generation = _ticketGeneration;
        _ticketLoading = true;
        try
        {
            var settings = ReadSettings();
            await RunAsync("Lecture d’une rubrique du ticket…", async ct =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ticketLoadCancellation = linked;
                TicketStateText.Text = "Lecture de « " + entry.Tab.Name + " »…";
                try
                {
                    var additional = await _browser.ReadTicketTabAsync(ticket, settings, entry.Tab,
                        new Progress<ExtractionProgress>(p => { if (generation == _ticketGeneration && !_closing) TicketStateText.Text = p.Message; }), linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if (_closing || generation != _ticketGeneration || !ReferenceEquals(displayed, _ticketDetail)) return;
                    if (additional.Tabs.Any(tab => tab.Key == entry.Tab.Key && tab.Loaded))
                    {
                        displayed.ObservedItems.RemoveAll(item => item.SourceTabKey == entry.Tab.Key || item.SourceTabSelector == entry.Tab.Selector);
                        displayed.Sections.RemoveAll(section => section.TabKey == entry.Tab.Key || section.TabSelector == entry.Tab.Selector);
                        displayed.Sections.AddRange(additional.Sections);
                        displayed.ObservedItems = displayed.ObservedItems.Concat(additional.ObservedItems).OrderByDescending(item => item.ObservedAt ?? DateTimeOffset.MinValue)
                            .DistinctBy(i => (i.Kind, i.Identity, i.Title, i.Url, i.Date)).Take(6000).ToList();
                    }
                    foreach (var updated in additional.Tabs)
                    {
                        var original = displayed.Tabs.FirstOrDefault(tab => tab.Key == updated.Key);
                        if (original == null) displayed.Tabs.Add(updated);
                        else { original.Selector = updated.Selector; if (updated.Loaded) original.Loaded = true; }
                    }
                    displayed.Warnings = displayed.Warnings.Concat(additional.Warnings).Distinct().ToList();
                    displayed.ReadAt = additional.ReadAt;
                    if (_ticketDisplayedCacheKey != null) CacheTicket(_ticketDisplayedCacheKey, displayed);
                    RememberPersonalTicketRead(displayed, true);
                    BuildTicketNavigation(entry.Key);
                    RefreshPersonalTicketPresentation();
                    TicketMetaText.Text = $"{displayed.Sections.Count} sections lues · {displayed.ReadAt.LocalDateTime:dd/MM/yyyy à HH:mm:ss}";
                    var loaded = additional.Tabs.Any(tab => tab.Key == entry.Tab.Key && tab.Loaded);
                    TicketStateText.Text = loaded
                        ? additional.Sections.Count > 0 ? "Rubrique lue. Les autres onglets restent accessibles dans le menu." : "Lecture terminée : aucun contenu exploitable dans cet onglet."
                        : "La rubrique n’est pas devenue lisible. Consulte les indications de lecture avant de réessayer.";
                    SetStatus(TicketStateText.Text);
                }
                catch (OperationCanceledException)
                {
                    if (!_closing && generation == _ticketGeneration) TicketStateText.Text = "Lecture de la rubrique annulée. Les sections déjà lues sont conservées.";
                    throw;
                }
                catch (Exception ex)
                {
                    if (!_closing && generation == _ticketGeneration) ReportTicketError(ex);
                }
                finally { if (ReferenceEquals(_ticketLoadCancellation, linked)) _ticketLoadCancellation = null; }
            });
        }
        catch (Exception ex) { if (!_closing && generation == _ticketGeneration) ReportTicketError(ex); }
        finally { _ticketLoading = false; if (!_closing) UpdateControls(); }
    }

    private void PresentTicketNavigationEntry(TicketNavigationEntry entry)
    {
        CaptureTicketReadingPosition();
        var position = CurrentReadingPosition();
        _readingTicketKey = _ticketDetail == null ? "" : WorkplaceStore.TicketKey(_ticketDetail.Ticket);
        _readingSectionKey = entry.Key;
        _restoringReadingPosition = true;
        _ticketNavigationUpdating = true;
        try
        {
            TicketSearchBox.Text = position?.SectionKey == entry.Key ? position.Search : "";
            TicketViewModeBox.SelectedIndex = position?.ViewMode ?? 0;
            TicketSectionBox.ItemsSource = entry.Section == null ? null : new[] { entry.Section };
            TicketSectionBox.SelectedIndex = entry.Section == null ? -1 : 0;
        }
        finally { _ticketNavigationUpdating = false; }
        BuildTicketSectionPreview();
        _restoringReadingPosition = false; QueueReadingPositionSave();
        if (entry.Section == null)
        {
            TicketSectionTitleText.Text = entry.Label;
            TicketEmptyTitleText.Text = "Aucun contenu lisible dans cet onglet";
            TicketEmptyHintText.Text = "Consulte les indications de lecture, ouvre cet onglet dans GLPI, ou configure un repère de section dans les réglages.";
        }
    }

    private void TicketBack_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = ExtractionTab;

    private sealed record TicketNavigationEntry(string Key, string Label, string Hint, TicketDetailSection? Section, TicketDetailTab? Tab = null);

    private void BuildTicketSectionPreview()
    {
        var section = TicketSectionBox.SelectedItem as TicketDetailSection;
        _ticketPreview = null;
        TicketGrid.ItemsSource = null;
        TicketCellTextBox.Clear(); TicketCellExpander.IsExpanded = false;
        TicketReaderHost.Visibility = Visibility.Collapsed;
        TicketEmptyPanel.Visibility = Visibility.Visible;
        if (section == null)
        {
            TicketRowsText.Text = "Aucune section";
            TicketEmptyTitleText.Text = _ticketDetail == null ? "Détail du ticket" : "Aucune section lisible";
            TicketEmptyHintText.Text = _ticketDetail == null
                ? "Sélectionne un ticket dans Extraction pour charger ses informations."
                : "Consulte le bilan ou adapte les sections dans Réglages → Détail des tickets.";
            TicketSummaryText.Text = _ticketDetail?.Warnings.Count > 0
                ? string.Join(" · ", _ticketDetail.Warnings)
                : "Les sections et tableaux détectés apparaîtront ici.";
            UpdateTicketControls();
            return;
        }
        var data = section.Data;
        TicketSectionTitleText.Text = section.Name;
        var search = TicketSearchBox.Text.Trim();
        var timelineSelection = section.Kind == "timeline" && _ticketWorkplaceView != null
            ? TicketReadingService.Filter(data.Rows, _ticketWorkplaceView.TimelineFilter) : null;
        var matching = (timelineSelection?.Rows ?? data.Rows).Where(row => search.Length == 0 || row.Any(value =>
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(value ?? "", search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)).ToList();
        var displayed = matching.Take(PreviewLimit).ToList();
        _ticketPreview = new DataTable { Locale = CultureInfo.CurrentCulture };
        for (var i = 0; i < data.Headers.Count; i++)
            _ticketPreview.Columns.Add(new DataColumn("T" + i, typeof(string)) { Caption = data.Headers[i] });
        foreach (var values in displayed)
        {
            var row = _ticketPreview.NewRow();
            for (var i = 0; i < data.Headers.Count; i++) row[i] = i < values.Length ? values[i] : "";
            _ticketPreview.Rows.Add(row);
        }
        var changes = CurrentReadingChanges();
        _changedTicketRows = changes?.NewOrChangedRows.Values.SelectMany(set => set).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        TicketGrid.ItemsSource = _ticketPreview.DefaultView;
        var position = CurrentReadingPosition();
        if (position?.SectionKey == _readingSectionKey && position.RowIndex >= 0 && position.RowIndex < TicketGrid.Items.Count)
        { TicketGrid.SelectedIndex = position.RowIndex; TicketGrid.ScrollIntoView(TicketGrid.SelectedItem); }
        TicketRowsText.Text = search.Length > 0 ? $"{matching.Count:N0} / {data.Rows.Count:N0} lignes" : $"{data.Rows.Count:N0} lignes · {data.Headers.Count} colonnes";
        var reader = TicketViewModeBox.SelectedIndex == 0 && section.Kind is "fields" or "text" or "timeline";
        TicketReaderHost.Visibility = reader ? Visibility.Visible : Visibility.Collapsed;
        TicketGrid.Visibility = reader ? Visibility.Collapsed : Visibility.Visible;
        if (reader) _ticketReader?.Show(section, displayed, _ticketWorkplaceView?.GroupByDay == true, changes?.PreviousReadAt, _changedTicketRows, position?.SectionKey == _readingSectionKey ? position.ScrollOffset : 0);
        TicketCellExpander.Visibility = reader ? Visibility.Collapsed : Visibility.Visible;
        TicketEmptyPanel.Visibility = data.Headers.Count > 0 && matching.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        TicketEmptyTitleText.Text = search.Length > 0 ? "Aucun résultat dans cette rubrique" : "Section sans données";
        TicketEmptyHintText.Text = search.Length > 0 ? "Modifie la recherche pour retrouver les lignes lues." : "Consulte les indications de lecture ou choisis une autre rubrique.";
        var warnings = (_ticketDetail?.Warnings ?? new List<string>()).Concat(data.Warnings).Distinct().ToList();
        TicketSummaryText.Text = data.Status + (warnings.Count > 0 ? " · " + string.Join(" · ", warnings) : "")
            + (data.Rows.Count > PreviewLimit ? $" · Aperçu limité à {PreviewLimit:N0} lignes ; l’Excel contient toutes les lignes lues." : "")
            + " · La lecture des sections ne confirme pas l’exhaustivité globale du ticket.";
        if (timelineSelection?.UnreadableDates > 0) TicketSummaryText.Text += $" · {timelineSelection.UnreadableDates:N0} date(s) illisible(s) exclue(s) du filtre de période.";
        if (changes?.RowComparisonComplete == false) TicketSummaryText.Text += " · Comparaison détaillée limitée à 10 000 lignes mémorisées ; les autres lignes ne sont pas qualifiées comme nouvelles.";
        QueueReadingPositionSave();
        UpdateTicketControls();
    }

    private void TicketGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (_ticketPreview?.Columns.Contains(e.PropertyName) == true)
            e.Column.Header = _ticketPreview.Columns[e.PropertyName]!.Caption;
        var index = _ticketPreview?.Columns.IndexOf(e.PropertyName) ?? 0;
        var section = TicketSectionBox.SelectedItem as TicketDetailSection;
        e.Column.Width = section?.Kind == "fields" ? (index == 0 ? new DataGridLength(210) : new DataGridLength(1, DataGridLengthUnitType.Star))
            : section?.Kind == "timeline" ? (index == 3 ? new DataGridLength(3, DataGridLengthUnitType.Star) : new DataGridLength(index == 4 ? 1.5 : 1, DataGridLengthUnitType.Star))
            : new DataGridLength(1, DataGridLengthUnitType.Star);
        e.Column.MinWidth = 90;
        if (e.Column is DataGridTextColumn textColumn)
            textColumn.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap),
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Top),
                    new Setter(TextBlock.PaddingProperty, new Thickness(2, 7, 2, 7)),
                    new Setter(TextBlock.MaxHeightProperty, 140.0)
                }
            };
    }

    private void ShowTicketCellText()
    {
        var cell = TicketGrid.CurrentCell;
        var binding = (cell.Column as DataGridBoundColumn)?.Binding as Binding;
        var property = binding?.Path?.Path;
        if (cell.Item is not DataRowView row || string.IsNullOrEmpty(property) || !row.Row.Table.Columns.Contains(property))
        {
            TicketCellTextBox.Clear();
            TicketCellExpander.Header = "Texte de la cellule sélectionnée";
            return;
        }
        var text = Convert.ToString(row.Row[property], CultureInfo.CurrentCulture) ?? "";
        TicketCellTextBox.Text = text;
        TicketCellTextBox.ScrollToHome();
        TicketCellExpander.Header = "Texte complet · " + cell.Column!.Header;
        if (text.Length > 140 || text.Contains('\n')) TicketCellExpander.IsExpanded = true;
    }

    private void ReportTicketError(Exception error)
    {
        var message = AppPaths.SafeError(error);
        TicketStateText.Text = message;
        TicketSummaryText.Text = message;
        TicketEmptyTitleText.Text = "Le détail demande votre attention";
        TicketEmptyHintText.Text = message;
        if (_ticketDetail == null) TicketEmptyPanel.Visibility = Visibility.Visible;
        SetStatus(message);
        AddLog("DÉTAIL · " + error.GetType().Name + " · " + message);
        LogExpander.IsExpanded = true;
    }

    private TicketDetailResult? GetCachedTicket(string key)
    {
        for (var node = _ticketCache.First; node != null; node = node.Next)
        {
            if (node.Value.Key != key) continue;
            var entry = node.Value;
            _ticketCache.Remove(node);
            _ticketCache.AddFirst(entry);
            return entry.Data;
        }
        return null;
    }

    private void CacheTicket(string key, TicketDetailResult result)
    {
        RemoveCachedTicket(key);
        long characters = 0, cells = 0;
        foreach (var section in result.Sections)
        {
            characters += section.Name.Length;
            foreach (var header in section.Data.Headers) { characters += header.Length; cells++; }
            foreach (var row in section.Data.Rows)
            {
                cells += row.Length;
                foreach (var cell in row) characters += cell?.Length ?? 0;
                if (characters > TicketCacheCharacters || cells > TicketCacheCells) return;
            }
        }
        _ticketCache.AddFirst(new TicketCacheEntry(key, result, characters, cells));
        while (_ticketCache.Count > TicketCacheEntries || _ticketCache.Sum(entry => entry.Characters) > TicketCacheCharacters ||
               _ticketCache.Sum(entry => entry.Cells) > TicketCacheCells)
            _ticketCache.RemoveLast();
    }

    private void RemoveCachedTicket(string key)
    {
        var existing = _ticketCache.First;
        while (existing != null)
        {
            var next = existing.Next;
            if (existing.Value.Key == key) _ticketCache.Remove(existing);
            existing = next;
        }
    }

    private void AddTicketSection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _ticketSections.Count >= 20) return;
        var item = new TicketSectionDefinition { Name = "Nouvelle section", Kind = "table" };
        _ticketSections.Add(item);
        TicketSectionsGrid.SelectedItem = item;
        TicketSectionsGrid.ScrollIntoView(item);
        TicketSectionsGrid.CurrentCell = new DataGridCellInfo(item, TicketSectionsGrid.Columns[0]);
        TicketSectionsGrid.Focus();
        TicketSectionsGrid.BeginEdit();
        UpdateTicketControls();
    }

    private void RemoveTicketSection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TicketSectionsGrid.SelectedItem is not TicketSectionDefinition item) return;
        TicketSectionsGrid.CancelEdit(DataGridEditingUnit.Cell);
        TicketSectionsGrid.CancelEdit(DataGridEditingUnit.Row);
        _ticketSections.Remove(item);
        UpdateTicketControls();
    }

    private void RememberTicketSection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TicketSectionBox.SelectedItem is not TicketDetailSection section || string.IsNullOrWhiteSpace(section.Selector)) return;
        try
        {
            if (_ticketSections.Any(item => item.Selector == section.Selector && item.Kind == section.Kind && item.TabSelector == section.TabSelector))
            { SetStatus("Cette section figure déjà dans les réglages."); return; }
            if (_ticketSections.Count >= 20) throw new InvalidOperationException("La configuration contient déjà 20 sections. Retire une section avant d’en mémoriser une autre.");
            var name = string.IsNullOrWhiteSpace(section.Name) ? "Section du ticket" : section.Name;
            _ticketSections.Add(new TicketSectionDefinition
            {
                Name = name[..Math.Min(name.Length, 120)], Selector = section.Selector,
                Kind = section.Kind, TabSelector = section.TabSelector
            });
            var settings = ReadSettings();
            settings.Save();
            _settings = settings;
            SetStatus("Section mémorisée. Son nom et ses repères restent modifiables dans Réglages.");
            AddLog("DÉTAIL · une section a été mémorisée dans les réglages.");
        }
        catch (Exception ex) { ReportTicketError(ex); }
        UpdateTicketControls();
    }

    private async void TicketBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || _selectedTicket == null || _ticketDetail == null) return;
        var ticket = _selectedTicket;
        _presentingBrowser = true;
        UpdateBrowserButton();
        try
        {
            await RunAsync("Affichage du ticket dans le navigateur…", async ct =>
            {
                var page = _browser.TicketPage;
                if (page == null || page.IsClosed || !SameTicketAddress(page.Url, ticket.Url))
                {
                    var settings = ReadSettings();
                    var result = await _browser.ReadTicketDetailsAsync(ticket, settings, null, ct);
                    ct.ThrowIfCancellationRequested();
                    if (_closing) return;
                    var key = ticket.Url + "\n" + JsonSerializer.Serialize(settings);
                    CacheTicket(key, result);
                    PresentTicketResult(result, fromCache: false, key);
                }
                Topmost = false;
                var ticketPage = _browser.TicketPage;
                if (ticketPage == null || ticketPage.IsClosed)
                    throw new InvalidOperationException("L’onglet du ticket a été fermé. Actualise son détail pour le rouvrir.");
                await _browser.ShowBrowserAsync(ticketPage, ct);
                SetStatus("Ticket affiché dans GLPI. La recherche reste disponible dans son onglet.");
            });
        }
        finally
        {
            _presentingBrowser = false;
            if (!_closing) UpdateBrowserButton();
        }
    }

    private static bool SameTicketAddress(string actual, string expected)
    {
        if (!Uri.TryCreate(actual, UriKind.Absolute, out var first) || !Uri.TryCreate(expected, UriKind.Absolute, out var second)) return false;
        return Uri.Compare(first, second, UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped, StringComparison.Ordinal) == 0;
    }

    private async void ExportTicketXlsx_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TicketSectionBox.SelectedItem is not TicketDetailSection section || _ticketDetail == null) return;
        var detail = _ticketDetail;
        var source = section.Data;
        await RunAsync("Préparation du classeur de la section…", async _ =>
        {
            var settings = ReadSettings();
            Directory.CreateDirectory(settings.ExportFolder);
            var dialog = new SaveFileDialog
            {
                Title = "Exporter la section du ticket en Excel", Filter = "Classeur Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx", AddExtension = true, InitialDirectory = settings.ExportFolder,
                FileName = $"GLPI-Detail-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx"
            };
            if (dialog.ShowDialog(this) != true) { SetStatus("Enregistrement annulé."); return; }
            var data = new ExtractionResult
            {
                Headers = source.Headers, Rows = source.Rows, SourceUrl = source.SourceUrl,
                Title = source.Title, ExtractedAt = source.ExtractedAt, Pages = source.Pages,
                ExpectedTotal = source.ExpectedTotal, Scope = source.Scope, Complete = source.Complete,
                Status = source.Status, Warnings = source.Warnings.Concat(detail.Warnings).Distinct().ToList()
            };
            CancelButton.IsEnabled = false;
            var exported = await Task.Run(() => ExportService.WriteXlsx(data, dialog.FileName));
            SetStatus($"Section enregistrée en Excel : {exported.Rows:N0} lignes · {exported.Path}");
            AddLog("DÉTAIL · export Excel enregistré : " + exported.Path);
            if (exported.TruncatedCells > 0) AddLog($"DÉTAIL · {exported.TruncatedCells} cellules raccourcies selon la limite Excel ; voir la feuille Informations.");
        });
    }

    private async void ExportTicketAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _ticketDetail == null || _ticketDetail.Sections.Count == 0) return;
        var detail = _ticketDetail;
        await RunAsync("Préparation de la fiche lue…", async _ =>
        {
            var settings = ReadSettings();
            Directory.CreateDirectory(settings.ExportFolder);
            var count = detail.Sections.Sum(section => (long)section.Data.Rows.Count * section.Data.Headers.Count);
            var csv = count > 1_048_575;
            var dialog = new SaveFileDialog
            {
                Title = "Exporter toutes les sections lues du ticket", Filter = csv ? "Fiche CSV (*.csv)|*.csv" : "Fiche Excel (*.xlsx)|*.xlsx",
                DefaultExt = csv ? ".csv" : ".xlsx", AddExtension = true, InitialDirectory = settings.ExportFolder,
                FileName = $"GLPI-Fiche-{DateTime.Now:yyyyMMdd-HHmmss}" + (csv ? ".csv" : ".xlsx")
            };
            if (dialog.ShowDialog(this) != true) { SetStatus("Enregistrement annulé."); return; }
            CancelButton.IsEnabled = false;
            var exported = await Task.Run(() =>
            {
                var data = new ExtractionResult
                {
                    Headers = new() { "Rubrique", "Ligne", "Champ", "Valeur" },
                    SourceUrl = AppPaths.SafeUrl(detail.Ticket.Url), Title = "Fiche des sections lues",
                    ExtractedAt = detail.ReadAt, Pages = detail.Sections.Count, ExpectedTotal = count <= int.MaxValue ? (int)count : null,
                    Scope = "Sections déjà lues du ticket ; autres onglets et contenus masqués non couverts",
                    Complete = false, Status = "Export de la fiche lue ; exhaustivité globale non confirmée",
                    Warnings = detail.Warnings.Concat(detail.Sections.SelectMany(section => section.Data.Warnings)).Distinct().ToList()
                };
                foreach (var section in detail.Sections)
                for (var row = 0; row < section.Data.Rows.Count; row++)
                for (var column = 0; column < section.Data.Headers.Count; column++)
                    data.Rows.Add(new[] { section.Name, (row + 1).ToString(CultureInfo.InvariantCulture), section.Data.Headers[column], column < section.Data.Rows[row].Length ? section.Data.Rows[row][column] : "" });
                return csv ? ExportService.WriteCsv(data, dialog.FileName) : ExportService.WriteXlsx(data, dialog.FileName);
            });
            SetStatus("Fiche lue enregistrée : " + exported.Path);
            AddLog($"DÉTAIL · export de {detail.Sections.Count} sections déjà lues ; {exported.Rows:N0} valeurs.");
        });
    }

    private sealed record TicketLoadRequest(TicketReference Ticket, AppSettings Settings, string CacheKey,
        long Generation, bool Force, DateTimeOffset NotBefore);
    private sealed record TicketCacheEntry(string Key, TicketDetailResult Data, long Characters, long Cells);
}
