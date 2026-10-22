using System.Diagnostics;
using System.IO;
using System.Windows;
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
        _citySettingsView.LocalCorrectionRequested += async (row, city, site) => await SaveCityCorrectionAsync(row, city, site);
        _tourPlanningView = new TourPlanningView();
        ToursHost.Content = _tourPlanningView;
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
        var effective = IsHistoricalResult ? _historicalDocument!.Dashboard.Copy() : settings.Copy();
        if (IsHistoricalResult)
        {
            // Keep today’s presentation, but replay the recorded interpretation and date.
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
        var dashboard = settings.Dashboard.Copy();
        return await Task.Run(() =>
        {
            var corrections = CityOverrideStore.Load(Path.Combine(AppPaths.DataRoot, "city-overrides.json"));
            return CityDetectionService.Enrich(source, cities, dashboard, ct, corrections.Overrides);
        }, ct);
    }

    private async Task SaveCityCorrectionAsync(int row, string cityId, string siteId)
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
                var store = CityOverrideStore.Load(path).With(cityId.Length == 0 ? null : new CityTicketOverride { CityId = cityId, SiteId = siteId }, sourceKey, ticketId);
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
        await RunAsync("Enregistrement des correspondances et recalcul des villes…", async ct =>
        {
            var settings = _settings.Copy();
            settings.Cities = _citySettingsView.ReadSettings();
            ApplyDashboardSettingsFields(settings);
            await SaveAndReapplyCitiesAsync(settings, ct);
            SetStatus("Correspondances enregistrées. La colonne des villes, les dashboards et les tournées utilisent le relevé recalculé.");
        });
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
