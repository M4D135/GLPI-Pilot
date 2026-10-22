using System.IO;
using System.Windows;
using GlpiPilot.Core;
using GlpiPilot.UI;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private TechnicianWorkspaceView? _techniciansView;
    private TechnicianRoster? _technicianRoster;
    private DashboardSettings? _technicianRosterSettings;
    private TechnicianPreferences _technicianPreferences = new();
    private DashboardSnapshot? _technicianDashboard;
    private ExtractionResult? _technicianRosterSource, _technicianDashboardSource, _personalWorkspaceRequestedSource;
    private CancellationTokenSource? _technicianRosterCancellation, _technicianCalculationCancellation;
    private string _selectedTechnicianKey = "";
    private long _technicianGeneration, _technicianRosterGeneration, _technicianPublishedGeneration, _personalWorkspaceRequestedGeneration;
    private string _technicianDashboardKey = "";
    private static string TechnicianPreferencesPath => Path.Combine(AppPaths.DataRoot, "technician-preferences.json");

    private void InitializeTechnicianWorkspace()
    {
        _techniciansView = new TechnicianWorkspaceView();
        TechniciansHost.Content = _techniciansView;
        _techniciansView.TechnicianRequested += key => SelectTechnician(key);
        _techniciansView.PersonalTechnicianRequested += RememberPersonalTechnician;
        _techniciansView.OpenPersonalAfterExtractionRequested += RememberOpenPersonalAfterExtraction;
        _techniciansView.PersonalRequested += () => SelectTechnician(_settings.Dashboard.PersonalTechnician);
        _techniciansView.TeamRequested += () => MainTabs.SelectedItem = DashboardTab;
        _techniciansView.Management.DashboardRequested += key => SelectTechnician(key);
        _techniciansView.Management.RefreshRequested += RefreshTechnicianWorkspace;
        _techniciansView.Management.PreferenceSaveRequested += SaveTechnicianPreference;
        _techniciansView.Management.RowsRequested += (rows, label) =>
        {
            if (TechnicianSourceIsCurrent() && !_busy && !_closing)
                ShowDashboardRows(rows, "Gestion de l’équipe · " + label);
        };
        try { _technicianPreferences = TechnicianPreferences.Load(TechnicianPreferencesPath); }
        catch (Exception ex)
        {
            _technicianPreferences = new();
            AddLog("TECHNICIENS · préférences locales non chargées ; valeurs par défaut utilisées. " + AppPaths.SafeError(ex));
        }
        var dashboard = _techniciansView.Dashboard;
        dashboard.RowsRequested += (rows, label) =>
        {
            if (TechnicianDashboardIsCurrent() && !_busy && !_closing)
                ShowDashboardRows(rows, _technicianDashboard!.ScopeLabel + " · " + label);
        };
        dashboard.RefreshRequested += () => RefreshTechnicianWorkspace();
        dashboard.SettingsRequested += () => { MainTabs.SelectedItem = SettingsTab; SettingsDashboardHost.BringIntoView(); };
        dashboard.ExportRequested += async () => await ExportTechnicianDashboardAsync();
        dashboard.LayoutChanged += layout => SaveDashboardLayout(layout, dashboard);
        dashboard.SnapshotChanged += snapshot => { if (!_closing && TechnicianSourceIsCurrent()) _technicianDashboard = snapshot; };
        dashboard.SetLayout(_settings.Dashboard.PersonalLayout);
        _techniciansView.SetIdentity(_settings.Dashboard.PersonalTechnician);
        _techniciansView.SetOpenPersonalAfterExtraction(_settings.Dashboard.OpenPersonalAfterExtraction);
        InitializePersonalDayWorkspace();
    }

    private void LoadTechnicianSettingsFields(AppSettings settings)
    {
        _personalWorkspaceRequestedSource = null;
        if (_techniciansView == null) return;
        _techniciansView.Dashboard.SetLayout(settings.Dashboard.PersonalLayout);
        _techniciansView.SetIdentity(settings.Dashboard.PersonalTechnician);
        _techniciansView.SetOpenPersonalAfterExtraction(settings.Dashboard.OpenPersonalAfterExtraction);
    }

    // Called only after a successful automatic extraction. It never starts an extraction.
    private void RequestPersonalWorkspaceAfterExtraction()
    {
        _personalWorkspaceRequestedSource = null;
        if (_closing || _result == null || !_settings.Dashboard.OpenPersonalAfterExtraction ||
            string.IsNullOrWhiteSpace(_settings.Dashboard.PersonalTechnician)) return;
        _personalWorkspaceRequestedSource = _result;
        _personalWorkspaceRequestedGeneration = _technicianRosterGeneration;
        TryOpenRequestedPersonalWorkspace();
    }

    private bool TryOpenRequestedPersonalWorkspace()
    {
        var source = _personalWorkspaceRequestedSource;
        if (source == null) return false;
        if (_closing || !ReferenceEquals(source, _result) || !_settings.Dashboard.OpenPersonalAfterExtraction ||
            string.IsNullOrWhiteSpace(_settings.Dashboard.PersonalTechnician))
        { _personalWorkspaceRequestedSource = null; return false; }
        if (_techniciansView == null || _technicianRoster == null || !ReferenceEquals(source, _technicianRosterSource) ||
            _personalWorkspaceRequestedGeneration != _technicianRosterGeneration || !TechnicianSourceIsCurrent()) return false;

        _personalWorkspaceRequestedSource = null;
        MainTabs.SelectedItem = TechniciansTab;
        if (!_technicianRoster.Available)
        {
            _techniciansView.Dashboard.ShowEmpty(_technicianRoster.Message);
            _techniciansView.SetCalculationBusy(false);
        }
        else SelectTechnician(_settings.Dashboard.PersonalTechnician, allowWhileBusy: true);
        return true;
    }

    private void ApplyTechnicianDashboardLayout(DashboardLayoutSettings layout, DashboardView origin)
    {
        if (_techniciansView != null && !ReferenceEquals(origin, _techniciansView.Dashboard))
            _techniciansView.Dashboard.SetLayout(layout);
    }

    private bool TechnicianSourceIsCurrent() => _result != null && _technicianRosterSettings != null && ReferenceEquals(_technicianRosterSource, _result)
        && _technicianPublishedGeneration == _technicianRosterGeneration;

    private bool TechnicianDashboardIsCurrent() => TechnicianSourceIsCurrent() && _technicianRoster?.Available == true && _technicianDashboard != null
        && ReferenceEquals(_technicianDashboardSource, _result) && _technicianDashboardKey == _selectedTechnicianKey
        && _technicianCalculationCancellation == null;

    private async void RefreshTechnicianWorkspace()
    {
        if (_closing || _techniciansView == null) return;
        _technicianRosterCancellation?.Cancel();
        _technicianCalculationCancellation?.Cancel();
        var generation = ++_technicianGeneration;
        _technicianRosterGeneration = generation;
        var source = _result;
        if (ReferenceEquals(source, _personalWorkspaceRequestedSource)) _personalWorkspaceRequestedGeneration = generation;
        else _personalWorkspaceRequestedSource = null;
        if (source == null) { ResetTechnicianWorkspace(); return; }
        var operation = new CancellationTokenSource(); _technicianRosterCancellation = operation;
        _techniciansView.ShowLoading();
        try
        {
            var allSettings = _settings.Copy(); ApplyDashboardSettingsFields(allSettings);
            allSettings.Dashboard = EffectiveDashboardSettings(allSettings.Dashboard);
            var referenceTime = IsHistoricalResult ? source.ExtractedAt : DateTimeOffset.Now;
            var workload = await Task.Run(() => TechnicianWorkloadService.Calculate(source, allSettings.Dashboard, referenceTime: referenceTime, ct: operation.Token), operation.Token);
            if (operation.IsCancellationRequested || _closing || generation != _technicianGeneration || !ReferenceEquals(source, _result)) return;
            var roster = workload.Roster;
            _technicianRoster = roster;
            _technicianRosterSettings = allSettings.Dashboard.Copy();
            _technicianRosterSource = source;
            _technicianPublishedGeneration = generation;
            // Keep an explicit currently selected colleague, then prefer the remembered
            // personal identity. Never guess the user's identity from the first entry.
            if (_selectedTechnicianKey.Length == 0) _selectedTechnicianKey = _settings.Dashboard.PersonalTechnician;
            _techniciansView.ShowRoster(roster, _settings.Dashboard.PersonalTechnician, _selectedTechnicianKey);
            _techniciansView.Management.Show(workload, _technicianPreferences);
            _personalWorkload = workload;
            RefreshPersonalDay();
            AddLog($"TECHNICIENS · {roster.Technicians.Count:N0} identité(s) détectée(s) ; colonne d’affectation={(roster.Available ? "reconnue" : "indisponible")}.");
            if (roster.Available)
                AddLog($"TECHNICIENS · gestion de charge : {workload.AssignedRows.Length:N0} lignes affectées au moins une fois, {workload.MultipleAssignmentRows.Length:N0} multi-affectées ; synthèse par union d’indices source.");
            if (TryOpenRequestedPersonalWorkspace()) return;
            if (!roster.Available)
            {
                _techniciansView.Dashboard.ShowEmpty(roster.Message);
                _techniciansView.SetCalculationBusy(false);
            }
            else if (_selectedTechnicianKey.Length > 0) SelectTechnician(_selectedTechnicianKey, allowWhileBusy: true, openDashboard: false);
            else
            {
                _techniciansView.Dashboard.ShowEmpty("Choisissez votre nom puis « C’est moi », ou ouvrez l’onglet d’un technicien. Les indicateurs seront recalculés sur ses tickets du relevé.");
                _techniciansView.SetCalculationBusy(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && !operation.IsCancellationRequested && generation == _technicianGeneration)
            {
                _techniciansView.ShowRefreshError("Le nouveau calcul des techniciens n’a pas abouti. Vérifiez les correspondances du dashboard puis actualisez les calculs. La vue précédente est conservée sans action sur ses lignes.");
                AddLog("TECHNICIENS · " + AppPaths.SafeError(ex));
            }
        }
        finally
        {
            if (ReferenceEquals(_technicianRosterCancellation, operation)) _technicianRosterCancellation = null;
            operation.Dispose();
            UpdateTechnicianControls();
        }
    }

    private async void SelectTechnician(string key, bool allowWhileBusy = false, bool openDashboard = true)
    {
        if (_closing || _busy && !allowWhileBusy || _result == null || _techniciansView == null || _technicianRoster?.Available != true ||
            !TechnicianSourceIsCurrent() || string.IsNullOrWhiteSpace(key)) return;
        _technicianCalculationCancellation?.Cancel();
        var operation = new CancellationTokenSource(); _technicianCalculationCancellation = operation;
        var generation = ++_technicianGeneration;
        var source = _result;
        var roster = _technicianRoster;
        // The roster and its semantic mappings/thresholds are one published calculation.
        // Unapplied UI edits must never reinterpret another technician's stored row indexes.
        var settings = _technicianRosterSettings!.Copy();
        var cities = CitySourceIsCurrent() ? _cityEnrichment : null;
        var queries = _settings.Queries.Copy();
        var geography = IsHistoricalResult ? _historicalDocument!.Cities.Copy() : _settings.Cities.Copy();
        var referenceTime = IsHistoricalResult ? source.ExtractedAt : DateTimeOffset.Now;
        _selectedTechnicianKey = TechnicianDashboardService.IdentityKey(key);
        var selectedKey = _selectedTechnicianKey;
        _techniciansView.SetSelectedKey(selectedKey);
        if (openDashboard) _techniciansView.ShowTechnicianDashboard();
        _techniciansView.SetCalculationBusy(true);
        if (_technicianDashboard == null)
            _techniciansView.Dashboard.ShowEmpty("Calcul de la météo, des indicateurs et des répartitions du technicien sélectionné…");
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var populations = new AnalyticsPopulationStore().LoadOrEmpty(out var populationWarning);
                var calculated = TechnicianDashboardService.CalculateForKnownTechnician(source, settings, roster, selectedKey, referenceTime, operation.Token);
                if (populationWarning.Length > 0) calculated.Warnings.Add(populationWarning);
                AppendLocalDashboardProbes(calculated, source, settings, cities, queries, operation.Token, geography, populations);
                AttachDashboardScope(calculated, source, settings, cities, queries, geography, populations);
                return calculated;
            }, operation.Token);
            if (operation.IsCancellationRequested || _closing || generation != _technicianGeneration || !ReferenceEquals(source, _result) || _selectedTechnicianKey != selectedKey) return;
            _technicianDashboard = snapshot;
            _technicianDashboardSource = source; _technicianDashboardKey = selectedKey;
            _techniciansView.Dashboard.Show(snapshot);
            AddLog($"TECHNICIENS · vue calculée sur {snapshot.RowCount:N0} lignes du relevé ; source={(snapshot.Complete ? "complète" : "partielle ou non confirmée")} ; météo={snapshot.Weather.State}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && !operation.IsCancellationRequested && generation == _technicianGeneration)
            {
                _techniciansView.Dashboard.ShowEmpty("Cette vue ne peut pas être calculée. Vérifiez les colonnes et rechargez la liste des techniciens.");
                _technicianDashboard = null; _technicianDashboardSource = null; _technicianDashboardKey = "";
                AddLog("TECHNICIENS · " + AppPaths.SafeError(ex));
            }
        }
        finally
        {
            if (ReferenceEquals(_technicianCalculationCancellation, operation))
            {
                _technicianCalculationCancellation = null;
                _techniciansView.SetCalculationBusy(false);
            }
            operation.Dispose();
            UpdateTechnicianControls();
        }
    }

    private void RememberPersonalTechnician(string key)
    {
        if (_busy || _closing || _technicianRoster?.Available != true || !TechnicianSourceIsCurrent()) return;
        var entry = _technicianRoster.Technicians.FirstOrDefault(technician => technician.Key == key);
        if (entry == null) return;
        var previous = _settings.Dashboard.PersonalTechnician;
        try
        {
            _settings.Dashboard.PersonalTechnician = entry.Key;
            _settings.Save();
            _techniciansView?.SetIdentity(entry.Key);
            RefreshPersonalDay();
            SetStatus("Identité mémorisée. Votre tableau de bord personnel sera repris avec les prochains relevés.");
            AddLog("TECHNICIENS · identité personnelle mémorisée sur ce poste.");
            SelectTechnician(entry.Key);
        }
        catch (Exception ex)
        {
            _settings.Dashboard.PersonalTechnician = previous;
            _techniciansView?.SetIdentity(previous);
            ShowError(ex);
        }
    }

    private void SaveTechnicianPreference(string key, TechnicianPreference preference)
    {
        if (_busy || _closing || !TechnicianSourceIsCurrent() || _technicianRoster?.Technicians.Any(entry => entry.Key == key) != true) return;
        try
        {
            var updated = _technicianPreferences.With(key, preference);
            updated.Save(TechnicianPreferencesPath);
            _technicianPreferences = updated;
            RefreshPersonalDay();
            _techniciansView?.Management.SetPreferences(updated, "Favori, objectif et note enregistrés sur ce poste.");
            SetStatus("Repères du technicien enregistrés localement.");
            AddLog("TECHNICIENS · repères locaux enregistrés ; aucune modification de GLPI.");
        }
        catch (Exception ex)
        {
            _techniciansView?.Management.ShowPreferenceSaveError(
                "Les repères n’ont pas pu être enregistrés. Votre saisie est conservée pour réessayer ; les valeurs précédentes restent appliquées. Consultez le journal.");
            AddLog("TECHNICIENS · sauvegarde des repères locaux impossible. " + AppPaths.SafeError(ex));
        }
    }

    private void RememberOpenPersonalAfterExtraction(bool enabled)
    {
        var previous = _settings.Dashboard.OpenPersonalAfterExtraction;
        if (_busy || _closing || previous == enabled)
        { _techniciansView?.SetOpenPersonalAfterExtraction(previous); return; }
        try
        {
            _settings.Dashboard.OpenPersonalAfterExtraction = enabled;
            _settings.Save();
            if (!enabled) _personalWorkspaceRequestedSource = null;
            _techniciansView?.SetOpenPersonalAfterExtraction(enabled);
            SetStatus(!enabled ? "Ouverture automatique de l’espace personnel désactivée sur ce poste."
                : string.IsNullOrWhiteSpace(_settings.Dashboard.PersonalTechnician)
                    ? "Option enregistrée. Choisissez votre nom puis « C’est moi » pour définir l’espace à ouvrir."
                    : "Après Extraire tout, votre espace personnel s’ouvrira avec le nouveau relevé.");
        }
        catch (Exception ex)
        {
            _settings.Dashboard.OpenPersonalAfterExtraction = previous;
            _techniciansView?.SetOpenPersonalAfterExtraction(previous);
            ShowError(ex);
        }
    }

    private void ResetTechnicianWorkspace()
    {
        ++_technicianGeneration;
        _technicianRosterGeneration = _technicianGeneration;
        _technicianPublishedGeneration = 0;
        _technicianRosterSource = _technicianDashboardSource = _personalWorkspaceRequestedSource = null;
        _technicianRosterCancellation?.Cancel(); _technicianCalculationCancellation?.Cancel();
        _technicianRoster = null; _technicianRosterSettings = null; _technicianDashboard = null; _technicianDashboardKey = "";
        _personalWorkload = null; RefreshPersonalDay(); _techniciansView?.Planning.Clear();
        _selectedTechnicianKey = _settings.Dashboard.PersonalTechnician;
        _techniciansView?.SetIdentity(_settings.Dashboard.PersonalTechnician);
        _techniciansView?.SetOpenPersonalAfterExtraction(_settings.Dashboard.OpenPersonalAfterExtraction);
        _techniciansView?.ShowEmpty("Extrayez un relevé pour détecter les techniciens et calculer leurs tableaux de bord personnels.");
    }

    private void UpdateTechnicianControls() => _techniciansView?.SetBusy(_busy || _closing);

    private async Task ExportTechnicianDashboardAsync()
    {
        var snapshot = _technicianDashboard;
        if (snapshot == null || _busy || _closing || !TechnicianDashboardIsCurrent()) return;
        await RunAsync("Préparation du bilan du technicien…", async _ =>
        {
            var settings = ReadSettings(); Directory.CreateDirectory(settings.ExportFolder);
            var csv = DashboardService.EstimatedExportRows(snapshot) > 1_048_575;
            var dialog = new SaveFileDialog
            {
                Title = "Exporter le bilan du technicien sélectionné",
                Filter = csv ? "Bilan complet CSV (*.csv)|*.csv" : "Classeur Excel (*.xlsx)|*.xlsx",
                DefaultExt = csv ? ".csv" : ".xlsx", AddExtension = true, InitialDirectory = settings.ExportFolder,
                FileName = $"GLPI-Bilan-Technicien-{DateTime.Now:yyyyMMdd-HHmmss}" + (csv ? ".csv" : ".xlsx")
            };
            if (dialog.ShowDialog(this) != true) { SetStatus("Enregistrement annulé."); return; }
            CancelButton.IsEnabled = false;
            var exported = await Task.Run(() =>
            {
                var data = DashboardService.ToExport(snapshot);
                return csv ? ExportService.WriteCsv(data, dialog.FileName) : ExportService.WriteXlsx(data, dialog.FileName);
            });
            RememberExport(exported.Path);
            SetStatus("Bilan du technicien enregistré : " + exported.Path);
            AddLog($"TECHNICIENS · bilan {(csv ? "CSV" : "Excel")} enregistré ; {snapshot.RowCount:N0} lignes dans le périmètre, {exported.Rows:N0} lignes de synthèse.");
        });
    }

    private void DisposeTechnicianWorkspace()
    {
        ++_technicianGeneration;
        _technicianRosterSettings = null;
        _technicianRosterSource = _technicianDashboardSource = _personalWorkspaceRequestedSource = null;
        _technicianRosterCancellation?.Cancel(); _technicianCalculationCancellation?.Cancel();
    }
}
