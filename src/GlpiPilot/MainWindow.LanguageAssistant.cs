using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot;

public partial class MainWindow
{
    private void OpenLanguageAssistant()
    {
        if (_busy || _closing) return;
        if (_result is not { } source) { SetStatus("Chargez un relevé pour fournir un schéma de requête ou un périmètre de synthèse à l’assistant."); return; }
        var configuration = _settings.Copy(); ApplyDashboardSettingsFields(configuration);
        var settings = EffectiveDashboardSettings(configuration.Dashboard).Copy();
        var cities = CitySourceIsCurrent() ? _cityEnrichment : null;
        var scope = MainTabs.SelectedItem == TechniciansTab && ReferenceEquals(_technicianDashboardSource, source) ? _technicianDashboard : _dashboard;
        bool Current() => !_closing && !_busy && ReferenceEquals(source, _result);
        var view = new LanguageAssistantView(source, settings, scope, Current, (query, ct) =>
            _backgroundJobs.RunAsync("language-query-preview", true, calculationToken =>
            {
                var snapshot = DashboardService.Calculate(source, settings, ct: calculationToken);
                if (cities is not null && ReferenceEquals(cities.Data, source)) CityDetectionService.AppendDashboardProbes(snapshot, cities, calculationToken);
                return QueryService.Evaluate(source, settings, snapshot, query, calculationToken);
            }, ct));
        view.DraftAccepted += query =>
        {
            if (!Current() || _queryWorkshopView is null) return;
            _queryWorkshopView.AddAssistedDraft(query); CloseInlinePanel(); MainTabs.SelectedItem = QueriesTab;
            SetStatus("Proposition prévisualisée ajoutée au brouillon de l’atelier. Relisez-la puis enregistrez la bibliothèque si elle convient.");
        };
        view.RowsRequested += (rows, label) => { if (!Current()) return; CloseInlinePanel(); ShowDashboardRows(rows, label); };
        ShowInlinePanel(view);
    }
}
