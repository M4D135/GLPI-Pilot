using System.Windows.Controls;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot;

public partial class MainWindow
{
    private readonly AlertService _alerts = new();
    private AlertsView? _alertsView;
    private TabItem? _alertsTab;
    private void InitializeAlerts()
    {
        _alertsView = new(); _alertsView.Load(_alerts.Document); _alertsTab = AddWorkspaceTab("Alertes", "\uE7F4", _alertsView);
        _alertsView.SaveRequested += rules =>
        {
            if (_busy || _closing) return;
            try { _alerts.SaveRules(rules); if (_dashboard != null && _result != null) RefreshAlerts(_result, _dashboard); _alertsView.ShowStatus("Règles enregistrées. Les mesures indisponibles ou les relevés incomplets ne ferment pas une alerte active."); }
            catch (Exception error) { _alertsView.ShowStatus(AppPaths.SafeError(error)); }
        };
        _alertsView.AcknowledgeRequested += id => AlertAction(() => _alerts.Acknowledge(id));
        _alertsView.SnoozeRequested += id => AlertAction(() => _alerts.Snooze(id, TimeSpan.FromHours(1)));
        _alertsView.RowsRequested += incident =>
        {
            if (_result != null && !IsHistoricalResult && incident.RunId == _result.Provenance.RunId && incident.Rows.Length > 0)
                ShowDashboardRows(incident.Rows, "Alerte · " + incident.Name);
            else SetStatus("Les lignes concernent un autre relevé ou la mesure ne comporte pas de lignes. Ouvrez le relevé correspondant dans Historique.");
        };
    }
    private void AlertAction(Action action)
    {
        try { action(); _alertsView?.Show(_alerts.Document); UpdateAlertBadge(); }
        catch (Exception error) { _alertsView?.ShowStatus(AppPaths.SafeError(error)); }
    }
    private void RefreshAlerts(ExtractionResult source, DashboardSnapshot snapshot)
    {
        _alertsView?.SetMetrics(snapshot);
        if (IsHistoricalResult || !ReferenceEquals(source, _result)) return;
        try
        {
            var count = _alerts.Evaluate(source, snapshot);
            _alertsView?.Show(_alerts.Document); UpdateAlertBadge();
            if (count > 0) AddLog($"ALERTES · {count} nouvelle(s) condition(s) déclenchée(s) ; consultez l’onglet Alertes.");
        }
        catch (Exception error) { AddLog("ALERTES · évaluation locale indisponible : " + AppPaths.SafeError(error)); }
    }
    private void UpdateAlertBadge()
    {
        var active = _alerts.Document.Incidents.Count(incident => incident.Active && !incident.Acknowledged && (incident.SnoozedUntil == null || incident.SnoozedUntil <= DateTimeOffset.Now));
        if (_alertsTab != null) _alertsTab.Header = active == 0 ? "Alertes" : $"Alertes · {active}";
    }
}
