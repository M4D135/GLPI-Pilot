using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using GlpiPilot.UI;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private DashboardView? _dashboardView;
    private DashboardSnapshot? _dashboard;
    private CancellationTokenSource? _dashboardCancellation;
    private bool _dashboardNeedsRetry;
    private readonly Dictionary<string, ComboBox> _dashboardColumns = new();
    private TextBox? _dashboardStaleBox, _dashboardOldBox, _dashboardDueBox;
    private ComboBox? _dashboardDateCulture;
    private StackPanel? _dashboardSettingsPanel;
    private readonly Dictionary<string, TextBox> _weatherFields = new();
    private ComboBox? _assignmentMode;
    private TextBox? _priorityRulesBox, _dueHorizonBox, _satisfactionScaleBox, _historyCadenceBox;
    private int[]? _dashboardPreviewRows;
    private string _dashboardPreviewLabel = "";

    private void InitializeDashboardWorkspace()
    {
        StartupDiagnostics.Step("Dashboard · construction de la vue principale");
        _dashboardView = new DashboardView();
        DashboardHost.Content = _dashboardView;
        _dashboardView.RowsRequested += ShowDashboardRows;
        _dashboardView.RefreshRequested += () => RefreshDashboardFromFields();
        _dashboardView.SettingsRequested += () => { MainTabs.SelectedItem = SettingsTab; SettingsDashboardHost.BringIntoView(); };
        _dashboardView.ExportRequested += async () => await ExportDashboardAsync();
        _dashboardView.LayoutChanged += layout => SaveDashboardLayout(layout, _dashboardView);
        _dashboardView.SnapshotChanged += snapshot => _dashboard = snapshot;
        _dashboardSettingsPanel = new StackPanel();
        _dashboardSettingsPanel.Children.Add(DashboardLabel("Laissez une correspondance vide pour la détection automatique. Si plusieurs colonnes conviennent, choisissez le titre exact. Les dates, seuils et correspondances suivent la configuration partageable."));
        var fields = new WrapPanel();
        foreach (var role in DashboardService.Roles)
        {
            var panel = new StackPanel { Width = 280, Margin = new Thickness(0, 0, 18, 8) };
            panel.Children.Add(DashboardLabel(role.Label));
            var box = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, MinWidth = 250, ToolTip = "Vide : détection automatique. Sinon : intitulé exact de la colonne." };
            panel.Children.Add(box); fields.Children.Add(panel); _dashboardColumns[role.Key] = box;
        }
        _dashboardSettingsPanel.Children.Add(fields);
        var thresholds = new WrapPanel();
        _dashboardStaleBox = DashboardNumberField(thresholds, "Sans modification depuis (jours)");
        _dashboardOldBox = DashboardNumberField(thresholds, "Ticket ancien à partir de (jours)");
        _dashboardDueBox = DashboardNumberField(thresholds, "Échéance proche sous (heures)");
        _dueHorizonBox = DashboardNumberField(thresholds, "Tranches exclusives d’échéance (h, séparées par ;)");
        _satisfactionScaleBox = DashboardNumberField(thresholds, "Note maximale de satisfaction");
        _historyCadenceBox = DashboardNumberField(thresholds, "Cadence historique attendue (heures)");
        var dates = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 18, 8) };
        dates.Children.Add(DashboardLabel("Format des dates du relevé"));
        _dashboardDateCulture = new ComboBox();
        foreach (var (label, tag) in new[] { ("Français · jour/mois/année", "fr-FR"), ("Anglais UK · jour/mois/année", "en-GB"), ("Anglais US · mois/jour/année", "en-US"), ("ISO uniquement · année-mois-jour", "invariant") })
            _dashboardDateCulture.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        dates.Children.Add(_dashboardDateCulture); thresholds.Children.Add(dates);
        _dashboardSettingsPanel.Children.Add(thresholds);
        _dashboardSettingsPanel.Children.Add(DashboardLabel("Les heures sont comparées à l’heure locale du poste. Les horodatages ISO avec fuseau sont convertis. Les durées affichées sont calendaires ; les pauses et calendriers SLA ne peuvent pas être reconstruits à partir d’un simple relevé."));
        var weather = new StackPanel();
        weather.Children.Add(DashboardLabel("Les poids sont relatifs. Un poids de zéro désactive un signal. Les champs manquants restent exclus ; la couverture demeure affichée."));
        var weatherFields = new WrapPanel();
        foreach (var (key, title) in new[] { ("OverdueWeight", "Poids · échéances dépassées"), ("PriorityWeight", "Poids · priorités hautes"), ("AssignmentWeight", "Poids · sans affectation"), ("StaleWeight", "Poids · dernière modification"), ("OldWeight", "Poids · ancienneté"), ("WatchThreshold", "Seuil · vigilance / 100"), ("RainThreshold", "Seuil · pluie / 100"), ("StormThreshold", "Seuil · orage / 100"), ("MinimumCoveragePercent", "Couverture minimale / signal (%)"), ("MinimumWeightPercent", "Part minimale des poids (%)") })
            _weatherFields[key] = DashboardNumberField(weatherFields, title);
        weather.Children.Add(weatherFields);
        _assignmentMode = new ComboBox { ItemsSource = new[] { "Participations par technicien", "Parts égales par ticket", "Cellule d’affectation complète" }, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 8) };
        weather.Children.Add(DashboardLabel("Comptage des matrices de techniciens")); weather.Children.Add(_assignmentMode);
        weather.Children.Add(DashboardLabel("Matrice de priorité locale facultative · une règle par ligne : impact:urgence=priorité. Exemple : 5:5=6. Aucun défaut GLPI n’est présumé."));
        _priorityRulesBox = new TextBox { AcceptsReturn = true, MinHeight = 80, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        weather.Children.Add(_priorityRulesBox);
        _dashboardSettingsPanel.Children.Add(new Expander { Header = "Météo, couverture et règles locales", Content = weather, Margin = new Thickness(0, 16, 0, 8) });
        SettingsDashboardHost.Content = _dashboardSettingsPanel;
        StartupDiagnostics.Step("Techniciens · construction de l’espace personnel");
        InitializeTechnicianWorkspace();
        StartupDiagnostics.Step("Dashboard et techniciens construits");
    }

    private static TextBlock DashboardLabel(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return label;
    }
    private static TextBox DashboardNumberField(Panel parent, string title)
    {
        var panel = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 18, 8) };
        panel.Children.Add(DashboardLabel(title)); var box = new TextBox(); panel.Children.Add(box); parent.Children.Add(panel); return box;
    }

    private void LoadDashboardSettingsFields(AppSettings settings)
    {
        var dashboard = settings.Dashboard;
        foreach (var (key, box) in _dashboardColumns)
            box.Text = dashboard.ColumnMappings.GetValueOrDefault(key) ?? "";
        _dashboardStaleBox!.Text = dashboard.StaleDays.ToString(CultureInfo.InvariantCulture);
        _dashboardOldBox!.Text = dashboard.OldDays.ToString(CultureInfo.InvariantCulture);
        _dashboardDueBox!.Text = dashboard.DueSoonHours.ToString(CultureInfo.InvariantCulture);
        _dueHorizonBox!.Text = string.Join(";", dashboard.DueHorizonHours.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        _satisfactionScaleBox!.Text = dashboard.SatisfactionScale.ToString(CultureInfo.InvariantCulture);
        _historyCadenceBox!.Text = dashboard.HistoryCadenceHours.ToString(CultureInfo.InvariantCulture);
        _dashboardDateCulture!.SelectedItem = _dashboardDateCulture.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == dashboard.DateCulture) ?? _dashboardDateCulture.Items[0];
        foreach (var pair in _weatherFields) pair.Value.Text = Convert.ToString(typeof(DashboardWeatherSettings).GetProperty(pair.Key)!.GetValue(dashboard.Weather), CultureInfo.InvariantCulture) ?? "0";
        _assignmentMode!.SelectedIndex = dashboard.AssignmentCountingMode switch { "fractional" => 1, "cell" => 2, _ => 0 };
        _priorityRulesBox!.Text = string.Join(Environment.NewLine, dashboard.PriorityRules.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value));
        RefreshDashboardColumnChoices();
        _dashboardView?.SetLayout(dashboard.Layout);
        LoadTechnicianSettingsFields(settings);
    }

    private void ApplyDashboardSettingsFields(AppSettings settings)
    {
        var previousSettings = settings.Dashboard.Copy();
        settings.Dashboard = new DashboardSettings
        {
            NumberCulture = previousSettings.NumberCulture, SourceTimeZone = previousSettings.SourceTimeZone,
            MultiValueSeparators = previousSettings.MultiValueSeparators, ValueDictionaries = previousSettings.ValueDictionaries,
            DueHorizonHours = _dueHorizonBox!.Text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x.Trim().Replace(',', '.'), CultureInfo.InvariantCulture)).ToList(),
            SatisfactionScale = double.Parse(_satisfactionScaleBox!.Text.Replace(',', '.'), CultureInfo.InvariantCulture),
            HistoryCadenceHours = Number(_historyCadenceBox!, "Cadence historique", 1, 720),
            ColumnMappings = _dashboardColumns.Where(p => !string.IsNullOrWhiteSpace(p.Value.Text)).ToDictionary(p => p.Key, p => p.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase),
            StaleDays = Number(_dashboardStaleBox!, "Inactivité", 1, 3650),
            OldDays = Number(_dashboardOldBox!, "Ticket ancien", 1, 3650),
            DueSoonHours = Number(_dashboardDueBox!, "Échéance proche", 1, 720),
            DateCulture = (_dashboardDateCulture!.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fr-FR",
            Layout = _dashboardView?.GetLayout() ?? _settings.Dashboard.Layout.Copy(),
            PersonalLayout = _techniciansView?.Dashboard.GetLayout() ?? _settings.Dashboard.PersonalLayout.Copy(),
            PersonalTechnician = _settings.Dashboard.PersonalTechnician,
            OpenPersonalAfterExtraction = _settings.Dashboard.OpenPersonalAfterExtraction
        };
        foreach (var pair in _weatherFields)
        {
            var property = typeof(DashboardWeatherSettings).GetProperty(pair.Key)!;
            if (!double.TryParse(pair.Value.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException("Météo : renseignez des nombres valides pour les poids et seuils.");
            if (property.PropertyType == typeof(int))
            {
                if (value != Math.Truncate(value) || value < 0 || value > 100) throw new InvalidOperationException("Météo : les poids et couvertures sont des entiers de 0 à 100.");
                property.SetValue(settings.Dashboard.Weather, (int)value);
            }
            else property.SetValue(settings.Dashboard.Weather, value);
        }
        settings.Dashboard.AssignmentCountingMode = _assignmentMode!.SelectedIndex switch { 1 => "fractional", 2 => "cell", _ => "participation" };
        foreach (var line in _priorityRulesBox!.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=');
            if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out var priority) || !settings.Dashboard.PriorityRules.TryAdd(parts[0].Trim(), priority))
                throw new InvalidOperationException("Matrice locale : utilisez une règle unique impact:urgence=priorité par ligne.");
        }
        DashboardService.ValidateSettings(settings.Dashboard);
    }

    private void RefreshDashboardColumnChoices()
    {
        foreach (var box in _dashboardColumns.Values)
        {
            var current = box.Text;
            box.ItemsSource = new[] { "" }.Concat(_result?.Headers ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            box.Text = current;
        }
    }

    private void ResetDashboard()
    {
        _dashboardCancellation?.Cancel();
        _dashboardNeedsRetry = false;
        _dashboard = null; _dashboardPreviewRows = null; _dashboardPreviewLabel = "";
        ClearDashboardFilterButton.Visibility = Visibility.Collapsed;
        _dashboardView?.ShowEmpty();
        ResetTechnicianWorkspace();
        RefreshDashboardColumnChoices();
    }

    private void RefreshDashboardFromFields()
    {
        if (_closing) return;
        try
        {
            var settings = _settings.Copy(); ApplyDashboardSettingsFields(settings);
            _settings.Dashboard = settings.Dashboard; _settings.Save();
            QueueDashboardCalculation();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void QueueDashboardCalculation()
    {
        _dashboardCancellation?.Cancel();
        var operation = new CancellationTokenSource(); _dashboardCancellation = operation;
        _dashboardNeedsRetry = false;
        var source = _result;
        _dashboard = null;
        if (source == null) { _dashboardView?.ShowEmpty(); operation.Dispose(); if (_dashboardCancellation == operation) _dashboardCancellation = null; return; }
        _dashboardView?.ShowEmpty("Calcul des indicateurs, des répartitions et de la météo du relevé…");
        RefreshDashboardColumnChoices();
        try
        {
            var allSettings = _settings.Copy(); ApplyDashboardSettingsFields(allSettings);
            var settings = EffectiveDashboardSettings(allSettings.Dashboard);
            allSettings.Dashboard = settings.Copy();
            if (IsHistoricalResult) { allSettings.Cities = _historicalDocument!.Cities.Copy(); allSettings.Queries = _historicalDocument.Queries.Copy(); }
            var calculationTime = IsHistoricalResult ? source.ExtractedAt : DateTimeOffset.Now;
            var cities = CitySourceIsCurrent() ? _cityEnrichment : null;
            var queries = allSettings.Queries.Copy();
            RefreshTechnicianWorkspace();
            RefreshCityWorkspace(settings);
            RefreshQueryWorkspace(settings);
            var snapshot = await _backgroundJobs.RunAsync("dashboard", MainTabs.SelectedItem == DashboardTab, calculationToken =>
            {
                var populations = new AnalyticsPopulationStore().LoadOrEmpty(out var populationWarning);
                var calculated = DashboardService.Calculate(source, settings, calculationTime, calculationToken);
                if (populationWarning.Length > 0) calculated.Warnings.Add(populationWarning);
                AppendLocalDashboardProbes(calculated, source, settings, cities, queries, calculationToken, allSettings.Cities, populations);
                AttachDashboardScope(calculated, source, settings, cities, queries, allSettings.Cities, populations);
                AttachDashboardHistory(calculated, source, allSettings, calculationToken);
                return calculated;
            }, operation.Token);
            if (operation.IsCancellationRequested || _closing || !ReferenceEquals(source, _result)) return;
            _dashboard = snapshot; RefreshAlerts(source, snapshot); _dashboardView?.Show(snapshot); _dashboardView?.SetBusy(_busy); RefreshAnalyticsWorkspace();
            AddLog($"DASHBOARD · {snapshot.RowCount:N0} lignes ; {snapshot.Columns.Count(c => c.Index.HasValue)}/{snapshot.Columns.Count} colonnes reconnues ; {snapshot.Warnings.Count} avertissement(s) ; météo={snapshot.Weather.State}.");
        }
        catch (OperationCanceledException)
        {
            if (!_closing && ReferenceEquals(_dashboardCancellation, operation) && !operation.IsCancellationRequested && ReferenceEquals(source, _result)) _dashboardNeedsRetry = true;
        }
        catch (Exception ex)
        {
            if (!_closing && !operation.IsCancellationRequested)
            { _dashboardView?.ShowEmpty("Calcul indisponible. Vérifiez les correspondances de colonnes dans Réglages."); AddLog("DASHBOARD · " + AppPaths.SafeError(ex)); }
        }
        finally { if (_dashboardCancellation == operation) _dashboardCancellation = null; operation.Dispose(); ResumeVisibleCalculation(); }
    }

    private static void AttachDashboardScope(DashboardSnapshot snapshot, ExtractionResult source, DashboardSettings settings,
        CityEnrichmentResult? cities, QueryLibrarySettings queries, CitySettings? geography = null, IReadOnlyList<SitePopulationEntry>? populations = null)
    {
        DashboardService.RefreshWeatherProfiles(snapshot, settings);
        var rootRows = snapshot.ScopeRows.ToHashSet();
        snapshot.RecalculateScope = (rows, label, cancellation) =>
        {
            var bounded = rows.Where(rootRows.Contains).Distinct().Order().ToArray();
            var calculated = DashboardService.CalculateSubset(source, settings, bounded, label, snapshot.CalculatedAt, cancellation);
            AppendLocalDashboardProbes(calculated, source, settings, cities, queries, cancellation, geography, populations);
            DashboardService.RefreshWeatherProfiles(calculated, settings);
            return calculated;
        };
    }

    private static void AttachDashboardHistory(DashboardSnapshot snapshot, ExtractionResult source, AppSettings settings, CancellationToken ct)
    {
        // Global metadata is never reused by the scoped calculation callback above.
        // A personal or locally filtered history needs its own historical row recalculation.
        var sourceKey = SnapshotStore.SourceKey(source);
        if (sourceKey.Length == 0 || snapshot.IsFiltered) return;
        try
        {
            var schema = SnapshotStore.SchemaFingerprint(source.Headers);
            var rules = SnapshotStore.RulesFingerprint(settings);
            var entries = new SnapshotStore().List().Where(x => x.SourceKey == sourceKey).OrderBy(x => x.CapturedAt).TakeLast(500).ToArray();
            ct.ThrowIfCancellationRequested();
            var currentVerified = snapshot.Complete && source.Provenance is { SearchVerified: true, ScopeVerified: true };
            var idColumn = snapshot.Column("id")?.Index;
            var ids = new HashSet<ulong>();
            if (idColumn is not int column) currentVerified = false;
            else foreach (var row in source.Rows)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var id = TicketReferenceResolver.NormalizeId(column < row.Length ? row[column] : "");
                    if (!ids.Add(ulong.Parse(id, CultureInfo.InvariantCulture))) { currentVerified = false; break; }
                }
                catch (InvalidOperationException) { currentVerified = false; break; }
            }
            foreach (var metric in snapshot.Metrics)
            {
                var key = metric.Key; var title = metric.Label + " · observations";
                var points = entries.Select(entry =>
                {
                    var limits = new List<string>();
                    if (!currentVerified) limits.Add("Relevé courant incomplet, périmètre non confirmé ou identifiants non fiables");
                    if (!entry.Complete) limits.Add("Relevé incomplet");
                    if (!entry.SearchVerified || !entry.ScopeVerified) limits.Add("Recherche ou contexte GLPI non confirmé");
                    if (!entry.IdentityReliable) limits.Add("Identifiants non fiables");
                    if (entry.SchemaFingerprint != schema) limits.Add("Colonnes différentes");
                    if (entry.RulesFingerprint != rules) limits.Add("Règles de normalisation différentes");
                    var value = entry.Metrics.GetValueOrDefault(key);
                    if (value is null || !double.IsFinite(value.Value)) limits.Add("Indicateur indisponible dans ce relevé");
                    return new DashboardHistoryPoint(entry.CapturedAt, value, entry.Rows, entry.Complete, limits.Count == 0, entry.Id,
                        limits.Count == 0 ? "Même recherche, mêmes colonnes, mêmes règles ; relevé complet et contexte confirmé." : string.Join(" · ", limits));
                }).ToArray();
                snapshot.History.Add(new(key, title, metric.Unit, SnapshotAnalytics.WithGaps(points, TimeSpan.FromHours(settings.Dashboard.HistoryCadenceHours)),
                    "Archives locales de cette recherche et de son contexte GLPI confirmé. Les colonnes, les règles et les identifiants doivent rester compatibles. Les relevés incomplets ou incompatibles interrompent la courbe. Une disparition du relevé ne signifie jamais une résolution. Les 500 archives les plus récentes de ce contexte sont retenues."));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            snapshot.Warnings.Add("Historique local indisponible : " + AppPaths.SafeError(error));
        }
    }

    private void UpdateDashboardControls()
    {
        _dashboardView?.SetBusy(_busy || _closing);
        if (_dashboardSettingsPanel != null) _dashboardSettingsPanel.IsEnabled = !_busy && !_closing;
        ClearDashboardFilterButton.IsEnabled = !_busy;
        UpdateTechnicianControls();
    }

    private void ShowDashboardRows(int[] indices, string label)
    {
        if (_busy || _closing || _result == null) return;
        _dashboardPreviewRows = indices.Where(i => i >= 0 && i < _result.Rows.Count).Distinct().ToArray();
        _dashboardPreviewLabel = label;
        SearchBox.Clear();
        BuildPreview(resetDashboard: false);
        ClearDashboardFilterButton.Visibility = Visibility.Visible;
        MainTabs.SelectedItem = ExtractionTab;
        SetStatus($"{_dashboardPreviewRows.Length:N0} lignes · {label}. Les exports du relevé restent complets.");
    }

    private void ClearDashboardFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _dashboardPreviewRows = null; _dashboardPreviewLabel = "";
        ClearDashboardFilterButton.Visibility = Visibility.Collapsed;
        BuildPreview(resetDashboard: false);
        SetStatus("Toutes les lignes du relevé sont de nouveau accessibles dans l’aperçu.");
    }

    private async Task ExportDashboardAsync()
    {
        if (_dashboard == null || _result == null || _busy) return;
        var snapshot = _dashboard; var source = _result; var criteria = _dashboardView?.ExportCriteria() ?? snapshot.ScopeLabel;
        await RunAsync("Préparation du bilan et du rapport du relevé…", async _ =>
        {
            var settings = ReadSettings(); Directory.CreateDirectory(settings.ExportFolder);
            var csv = DashboardService.EstimatedExportRows(snapshot) > 1_048_575;
            var dialog = new SaveFileDialog { Title = "Exporter un rapport complet ou le bilan chiffré",
                Filter = "Rapport complet autonome (*.html)|*.html|" + (csv ? "Bilan CSV (*.csv)|*.csv" : "Bilan Excel (*.xlsx)|*.xlsx|Bilan CSV (*.csv)|*.csv"),
                DefaultExt = ".html", AddExtension = true, InitialDirectory = settings.ExportFolder, FileName = $"GLPI-Rapport-{DateTime.Now:yyyyMMdd-HHmmss}" };
            if (dialog.ShowDialog(this) != true) { SetStatus("Enregistrement annulé."); return; }
            CancelButton.IsEnabled = false;
            var html = dialog.FilterIndex == 1; var useCsv = !html && (csv || dialog.FilterIndex == 3);
            var exported = await Task.Run(() =>
            {
                if (html) return ReportService.WriteHtml(snapshot, source, criteria, dialog.FileName);
                var data = DashboardService.ToExport(snapshot); return useCsv ? ExportService.WriteCsv(data, dialog.FileName) : ExportService.WriteXlsx(data, dialog.FileName);
            });
            RememberExport(exported.Path);
            SetStatus((html ? "Rapport complet (graphiques, critères, qualité et tickets)" : useCsv ? "Bilan CSV" : "Bilan Excel") + " enregistré : " + exported.Path);
            AddLog($"DASHBOARD · {(html ? "rapport HTML complet" : "bilan chiffré")} enregistré ; {exported.Rows:N0} lignes.");
        });
    }

    private void SaveDashboardLayout(DashboardLayoutSettings layout, DashboardView origin)
    {
        if (_closing) return;
        try
        {
            layout.Validate();
            if (ReferenceEquals(origin, _techniciansView?.Dashboard))
                _settings.Dashboard.PersonalLayout = layout.Copy();
            else
                _settings.Dashboard.Layout = layout.Copy();
            // Each workspace keeps its edited layout in memory even when disk
            // persistence fails. A subsequent extraction cannot restore a stale copy.
            _settings.Save();
            origin.SetSaveState(true);
        }
        catch (Exception ex)
        {
            origin.SetSaveState(false);
            AddLog("DASHBOARD · sauvegarde de la disposition impossible : " + AppPaths.SafeError(ex));
        }
    }

    private void DisposeDashboardWorkspace()
    {
        _dashboardCancellation?.Cancel();
        DisposeTechnicianWorkspace();
    }
}
