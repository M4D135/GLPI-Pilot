using GlpiPilot.Core;

namespace GlpiPilot;

public partial class MainWindow
{
    private CancellationTokenSource? _ticketIntelligenceCancellation;
    private long _ticketIntelligenceGeneration;

    private async void RefreshTicketIntelligence()
    {
        _ticketIntelligenceCancellation?.Cancel();
        var generation = ++_ticketIntelligenceGeneration;
        if (_ticketWorkplaceView == null) return;
        var source = _result; var detail = _ticketDetail;
        if (source == null || detail == null)
        { _ticketWorkplaceView.Intelligence.ShowStatus("Ouvrez une fiche avec son relevé pour comparer localement les titres, catégories et villes."); return; }
        var operation = new CancellationTokenSource(); _ticketIntelligenceCancellation = operation;
        var settings = EffectiveDashboardSettings(_settings.Dashboard);
        var known = _ticketCache.Select(entry => entry.Data).ToArray();
        _ticketWorkplaceView.Intelligence.ShowStatus("Rapprochement local dans le relevé en cours…");
        try
        {
            var result = await Task.Run(() => TicketIntelligenceService.Analyze(detail, source, settings, known, operation.Token), operation.Token);
            if (!_closing && generation == _ticketIntelligenceGeneration && ReferenceEquals(source, _result) && ReferenceEquals(detail, _ticketDetail)) _ticketWorkplaceView.Intelligence.Show(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_closing && generation == _ticketIntelligenceGeneration) _ticketWorkplaceView.Intelligence.ShowStatus("Rapprochement indisponible : " + AppPaths.SafeError(error));
        }
        finally { if (ReferenceEquals(_ticketIntelligenceCancellation, operation)) _ticketIntelligenceCancellation = null; operation.Dispose(); }
    }
}
