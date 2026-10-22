using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlpiPilot.Core;
using GlpiPilot.UI;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private CitySettingsView? _citySettingsView;
    private TourPlanningView? _tourPlanningView;
    private CityEnrichmentResult? _cityEnrichment;
    private CitySettings? _citySettingsForResult;

    private void InitializeCityWorkspace()
    {
        _citySettingsView = new CitySettingsView();
        SettingsCitiesHost.Content = _citySettingsView;
        _citySettingsView.SaveRequested += async () => await SaveCitySettingsAsync();
        _citySettingsView.LocalCorrectionRequested += async (row, city, site, reason) => await SaveCityCorrectionAsync(row, city, site, reason);
        _tourPlanningView = new TourPlanningView();
        ToursHost.Content = _tourPlanningView;
        _tourPlanningView.SavedToursChanged += RefreshPersonalDay;
        _tourPlanningView.SettingsRequested += OpenCitySettings;
        _tourPlanningView.Log += message => AddLog(message);
        _tourPlanningView.RowsRequested += (rows, label) =>
        {
            if (CitySourceIsCurrent() && !_busy && !_closing) ShowDashboardRows(rows, label);
        };
        _tourPlanningView.ExportRequested += async data =>
        {
            if (CitySourceIsCurrent() && !_busy && !_closing) await ExportTourAsync(data);
        };
        _tourPlanningView.MapRequested += (city, postal) =>
        {
            if (_busy || _closing || !CitySourceIsCurrent()) return;
            try
            {
                // This is an explicit user click. Only the configured city/postal code
                // leaves the application; no title, ticket, technician or address is sent.
                var query = Uri.EscapeDataString((city + " " + postal).Trim());
                Process.Start(new ProcessStartInfo("https://www.openstreetmap.org/search?query=" + query) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowError(ex); }
        };
    }

    private bool CitySourceIsCurrent() => _cityEnrichment != null && ReferenceEquals(_cityEnrichment.Data, _result);

    private void LoadCitySettingsFields(AppSettings settings) => _citySettingsView?.LoadSettings(settings.Cities);

    private void OpenCitySettings()
    {
        MainTabs.SelectedItem = SettingsTab;
        CitiesSettingsExpander.IsExpanded = true;
        SettingsCitiesHost.BringIntoView();
    }

    private DashboardSettings EffectiveDashboardSettings(DashboardSettings settings)
    {
        var localMapping = LocalMappingForCurrentResult();
        var effective = IsHistoricalResult ? _historicalDocument!.Dashboard.Copy() : localMapping ?? settings.Copy();
        if (IsHistoricalResult || localMapping != null)
        {
            // Keep today's presentation and personal identity while using the result's interpretation.
            effective.Layout = settings.Layout.Copy(); effective.PersonalLayout = settings.PersonalLayout.Copy();
            effective.PersonalTechnician = settings.PersonalTechnician;
        }
        // Inference is tied to this exact extraction. Never persist this generated
        // header or overwrite an explicitly selected GLPI city column.
        if (CitySourceIsCurrent() && _cityEnrichment!.Applied &&
            string.IsNullOrWhiteSpace(effective.ColumnMappings.GetValueOrDefault("city")))
            effective.ColumnMappings["city"] = _cityEnrichment.CityColumnName;
        return effective;
    }

    private async Task<CityEnrichmentResult> EnrichCitiesAsync(ExtractionResult source, AppSettings settings, CancellationToken ct)
    {
        var cities = settings.Cities.Copy();
        var dashboard = _localMappings.Get(source) ?? settings.Dashboard.Copy();
        return await Task.Run(() =>
        {
            var corrections = CityOverrideStore.Load(Path.Combine(AppPaths.DataRoot, "city-overrides.json"));
            return CityDetectionService.Enrich(source, cities, dashboard, ct, corrections.Overrides);
        }, ct);
    }

    private async Task SaveCityCorrectionAsync(int row, string cityId, string siteId, string reason)
    {
        if (_busy || _closing || !CitySourceIsCurrent()) return;
        await RunAsync("Application de la correction locale de lieu…", async ct =>
        {
            var source = _cityEnrichment!.Source;
            if (row < 0 || row >= source.Rows.Count) throw new InvalidDataException("La ligne n’appartient plus au relevé actuel.");
            if (cityId.Length > 0 && !_settings.Cities.Cities.Any(city => city.Id == cityId))
                throw new InvalidDataException("Enregistrez d’abord cette ville dans le référentiel avant de corriger un ticket.");
            if (siteId.Length > 0 && !_settings.Cities.Sites.Any(site => site.Id == siteId && site.CityId == cityId))
                throw new InvalidDataException("Enregistrez d’abord le site dans la ville sélectionnée.");
            var column = DashboardService.ResolveColumns(source.Headers, _settings.Dashboard).First(item => item.Role == "id").Index;
            string Id(int index) => column is int position && source.Rows[index] is { } cells && position < cells.Length ? cells[position]?.Trim() ?? "" : "";
            var ticketId = Id(row); var sourceKey = CityOverrideStore.SourceKey(source.SourceUrl);
            if (sourceKey.Length == 0 || ticketId.Length == 0 || Enumerable.Range(0, source.Rows.Count).Count(index => Id(index) == ticketId) != 1)
                throw new InvalidDataException("Une correction mémorisée nécessite une source GLPI et un identifiant de ticket unique dans ce relevé.");
            var path = Path.Combine(AppPaths.DataRoot, "city-overrides.json");
            var result = await Task.Run(() =>
            {
                var store = CityOverrideStore.Load(path).With(cityId.Length == 0 ? null : new CityTicketOverride { CityId = cityId, SiteId = siteId, Reason = reason }, sourceKey, ticketId);
                var report = CityDetectionService.Enrich(source, _settings.Cities, _settings.Dashboard, ct, store.Overrides);
                ct.ThrowIfCancellationRequested(); store.Save(path); return report;
            }, ct);
            var selectedTab = MainTabs.SelectedItem; PublishCityResult(result, _settings); BuildPreview(); MainTabs.SelectedItem = selectedTab;
            SetStatus(cityId.Length == 0 ? "Correction locale retirée ; les règles de détection sont réappliquées." : "Lieu corrigé localement. Les tableaux de bord et tournées utilisent la correction.");
            AddLog("VILLES · correction locale " + (cityId.Length == 0 ? "retirée" : "enregistrée") + " ; aucune écriture dans GLPI.");
        });
    }

    private void PublishCityResult(CityEnrichmentResult report, AppSettings settings)
    {
        _cityEnrichment = report;
        _citySettingsForResult = settings.Cities.Copy();
        _result = report.Data;
        _citySettingsView?.ShowSummary(report);
        RefreshCityWorkspace(settings.Dashboard);
        AddLog($"VILLES · enrichissement={(report.Applied ? "appliqué" : "inactif")} ; reconnues={report.MatchedCount}, sans correspondance={report.UnmatchedCount}, ambiguës={report.AmbiguousCount}, titre indisponible={report.TitleUnavailableCount}.");
    }

    private void RefreshCityWorkspace(DashboardSettings? dashboardSettings = null)
    {
        if (!CitySourceIsCurrent() || _citySettingsForResult == null)
        {
            _tourPlanningView?.ShowEmpty("Extrayez un relevé et définissez vos correspondances de villes pour préparer les tournées.");
            return;
        }
        _tourPlanningView?.Show(_cityEnrichment!, _citySettingsForResult, EffectiveDashboardSettings(dashboardSettings ?? _settings.Dashboard));
    }

    private async Task SaveCitySettingsAsync()
    {
        if (_busy || _closing || _citySettingsView == null) return;
        CitySettings draft;
        try { draft = _citySettingsView.ReadSettings(); } catch (Exception error) { ShowError(error); return; }
        var source = CitySourceIsCurrent() ? _cityEnrichment!.Source : _result;
        if (source == null)
        {
            await RunAsync("Enregistrement du référentiel…", async ct => { var settings = _settings.Copy(); settings.Cities = draft; await SaveAndReapplyCitiesAsync(settings, ct); }); return;
        }
        CityDetectionPreview? preview = null;
        await RunAsync("Simulation de toutes les localisations avant application…", async ct =>
        {
            var settings = _settings.Copy(); settings.Cities = draft; ApplyDashboardSettingsFields(settings);
            var before = await EnrichCitiesAsync(source, _settings, ct); var after = await EnrichCitiesAsync(source, settings, ct);
            preview = await Task.Run(() => CityDetectionReview.Compare(before, after, ct), ct);
        });
        if (preview == null || _closing) return;
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(PilotControls.Text("Impact des règles sur le relevé complet", 22));
        header.Children.Add(PilotControls.Text($"{preview.TotalRows:N0} lignes examinées · {preview.ChangedRows:N0} localisations modifiées. {preview.Changes.Count:N0} changements affichés (limite 1 000). Aucun changement enregistré.")); panel.Children.Add(header);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true, ItemsSource = preview.Changes, Margin = new Thickness(0, 12, 0, 12) };
        foreach (var (label, path, width) in new[] { ("Ligne", "RowIndex", 65d), ("Avant", "Before", 180d), ("Après", "After", 180d), ("Décision", "Explanation", 420d) }) grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(path), Width = width });
        Grid.SetRow(grid, 1); panel.Children.Add(grid);
        var apply = PilotControls.Button("Enregistrer et appliquer ce résultat", async () =>
        {
            if (_busy || _closing) return;
            if (!ReferenceEquals(source, CitySourceIsCurrent() ? _cityEnrichment!.Source : _result)) { ShowError(new InvalidDataException("Le relevé a changé ; relancez la simulation avant application.")); return; }
            await RunAsync("Application des localisations vérifiées…", async ct => { var settings = _settings.Copy(); settings.Cities = draft; ApplyDashboardSettingsFields(settings); await SaveAndReapplyCitiesAsync(settings, ct); CloseInlinePanel(); SetStatus("Référentiel enregistré après simulation du relevé complet."); });
        });
        Grid.SetRow(apply, 2); panel.Children.Add(apply); ShowInlinePanel(panel);
    }

    private async Task SaveAndReapplyCitiesAsync(AppSettings settings, CancellationToken ct)
    {
        // Always start from native cells, including when a city is renamed or disabled.
        // Previously derived columns therefore cannot accumulate across configuration edits.
        var source = CitySourceIsCurrent() ? _cityEnrichment!.Source : _result;
        var report = source == null ? null : await EnrichCitiesAsync(source, settings, ct);
        ct.ThrowIfCancellationRequested();
        settings.Save();
        _settings = settings;
        LoadCitySettingsFields(settings);
        if (report != null)
        {
            var selectedTab = MainTabs.SelectedItem;
            PublishCityResult(report, settings);
            BuildPreview();
            MainTabs.SelectedItem = selectedTab;
        }
        else QueueDashboardCalculation();
    }

    private async Task ExportTourAsync(ExtractionResult data)
    {
        await RunAsync("Préparation de la tournée…", async _ =>
        {
            var folder = _settings.ExportFolder;
            Directory.CreateDirectory(folder);
            var csvOnly = data.Rows.Count > 1_048_575 || data.Headers.Count > 16384;
            var dialog = new SaveFileDialog
            {
                Title = "Exporter la tournée préparée",
                Filter = csvOnly ? "CSV complet (*.csv)|*.csv" : "Classeur Excel (*.xlsx)|*.xlsx|CSV complet (*.csv)|*.csv",
                DefaultExt = csvOnly ? ".csv" : ".xlsx", AddExtension = true, InitialDirectory = folder,
                FileName = $"GLPI-Tournee-{DateTime.Now:yyyyMMdd-HHmmss}" + (csvOnly ? ".csv" : ".xlsx")
            };
            if (dialog.ShowDialog(this) != true) { SetStatus("Enregistrement annulé."); return; }
            CancelButton.IsEnabled = false;
            var csv = Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase);
            var exported = await Task.Run(() => csv ? ExportService.WriteCsv(data, dialog.FileName) : ExportService.WriteXlsx(data, dialog.FileName));
            RememberExport(exported.Path);
            SetStatus($"Tournée enregistrée : {exported.Rows:N0} lignes · {exported.Path}");
            AddLog($"TOURNÉE · export local enregistré ; {exported.Rows:N0} lignes.");
        });
    }

    private void UpdateCityControls()
    {
        _citySettingsView?.SetBusy(_busy || _closing);
        _tourPlanningView?.SetBusy(_busy || _closing);
    }

    private void ResetCityWorkspace()
    {
        _cityEnrichment = null; _citySettingsForResult = null;
        _citySettingsView?.ShowSummary(null);
        _tourPlanningView?.ShowEmpty("Extrayez un relevé pour préparer les tournées.");
    }

    private void DisposeCityWorkspace() => _tourPlanningView?.Dispose();
}
