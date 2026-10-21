using System.Windows;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot;

public partial class MainWindow
{
    private QueryWorkshopView? _queryWorkshopView;
    private ExtractionResult? _queryWorkspaceSource;

    private void InitializeQueryWorkspace()
    {
        _queryWorkshopView = new QueryWorkshopView();
        QueriesHost.Content = _queryWorkshopView;
        _queryWorkshopView.SaveRequested += async library => await SaveQueryLibraryAsync(library);
        _queryWorkshopView.RowsRequested += (rows, label) =>
        {
            if (!_busy && !_closing && _result != null && ReferenceEquals(_queryWorkspaceSource, _result))
                ShowDashboardRows(rows, label);
        };
        _queryWorkshopView.WidgetRequested += AddSavedQueryWidget;
    }

    private void LoadQuerySettingsFields(AppSettings settings) => _queryWorkshopView?.LoadSettings(settings.Queries);

    private void RefreshQueryWorkspace(DashboardSettings? dashboardSettings = null)
    {
        _queryWorkspaceSource = _result;
        if (_result == null)
        {
            _queryWorkshopView?.ShowEmpty("Préparez vos requêtes maintenant, puis extrayez ou importez un relevé pour les tester.");
            return;
        }
        if (dashboardSettings == null)
        {
            var current = _settings.Copy();
            ApplyDashboardSettingsFields(current);
            dashboardSettings = EffectiveDashboardSettings(current.Dashboard);
        }
        _queryWorkshopView?.Show(_result, dashboardSettings, CitySourceIsCurrent() ? _cityEnrichment : null);
    }

    private async Task SaveQueryLibraryAsync(QueryLibrarySettings library)
    {
        if (_busy || _closing) return;
        await RunAsync("Enregistrement des requêtes et actualisation des widgets…", async ct =>
        {
            library.Validate();
            var updated = _settings.Copy();
            updated.Queries = library.Copy();
            ct.ThrowIfCancellationRequested();
            updated.Save();
            _settings = updated;
            LoadQuerySettingsFields(updated);
            QueueDashboardCalculation();
            SetStatus("Bibliothèque de requêtes enregistrée. Les widgets liés se recalculent sur les dashboards global et personnels.");
            AddLog($"REQUÊTES · {updated.Queries.Queries.Count} définitions et leurs critères enregistrés ; aucun relevé copié dans la bibliothèque.");
            await Task.CompletedTask;
        });
    }

    private void AddSavedQueryWidget(string sourceKey, string title, bool distribution, bool personal)
    {
        if (_busy || _closing) return;
        var query = _settings.Queries.Queries.FirstOrDefault(item =>
            sourceKey == (distribution ? QueryService.DistributionKey(item.Id) : QueryService.MetricKey(item.Id)));
        if (query == null || distribution && string.IsNullOrWhiteSpace(query.GroupBy))
        {
            SetStatus("Enregistrez la requête et son regroupement avant de l’ajouter au dashboard.");
            return;
        }
        var view = personal ? _techniciansView?.Dashboard : _dashboardView;
        if (view == null) return;
        if (!view.AddQueryWidget(sourceKey, query.Name, distribution))
        {
            SetStatus("Cette disposition contient déjà 60 widgets. Créez une autre disposition ou retirez un widget.");
            return;
        }
        if (personal)
        {
            MainTabs.SelectedItem = TechniciansTab;
            _techniciansView?.ShowTechnicianDashboard();
        }
        else MainTabs.SelectedItem = DashboardTab;
        SetStatus("Widget ajouté à la disposition " + (personal ? "personnelle" : "globale") + ". Ses résultats suivront chaque nouveau relevé.");
    }

    private static void AppendLocalDashboardProbes(DashboardSnapshot snapshot, ExtractionResult source,
        DashboardSettings settings, CityEnrichmentResult? cities, QueryLibrarySettings queries, CancellationToken ct)
    {
        if (cities != null && ReferenceEquals(cities.Data, source))
            CityDetectionService.AppendDashboardProbes(snapshot, cities, ct);
        QueryService.AppendDashboardQueries(snapshot, source, settings, queries, ct);
    }

    private void UpdateQueryControls() => _queryWorkshopView?.SetBusy(_busy || _closing);

    private void ResetQueryWorkspace()
    {
        _queryWorkspaceSource = null;
        _queryWorkshopView?.ShowEmpty("Extrayez ou importez un relevé pour exécuter vos requêtes.");
    }

    private void DisposeQueryWorkspace()
    {
        _queryWorkspaceSource = null;
        _queryWorkshopView?.Dispose();
    }
}
