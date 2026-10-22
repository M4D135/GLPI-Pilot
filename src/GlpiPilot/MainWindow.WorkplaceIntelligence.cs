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
        _ticketWorkplaceView.Intelligence.SetRecurrenceColumns(source?.Headers ?? new());
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
    private async void ReadTicketRecurrences(string equipmentColumn)
    {
        if (_busy || _closing || _ticketDetail == null || _result == null) return;
        var target = _ticketDetail; var source = _result; var settings = EffectiveDashboardSettings(_settings.Dashboard);
        await RunAsync("Recherche de récurrences dans les relevés locaux…", async ct =>
        {
            _ticketWorkplaceView!.Intelligence.ShowRecurrenceStatus("Lecture des relevés datés du même périmètre…");
            var report = await Task.Run(() =>
            {
                var sourceKey = SnapshotStore.SourceKey(source);
                IEnumerable<TicketRecurrenceSource> Sources()
                {
                    yield return new(source, settings, "Relevé courant · " + source.ExtractedAt.ToString("dd/MM/yyyy HH:mm"));
                    if (sourceKey.Length == 0) yield break;
                    var entries = _snapshotStore.List().Where(entry => entry.SourceKey == sourceKey && entry.CapturedAt != source.ExtractedAt).Take(TicketRecurrenceService.MaximumSources - 1);
                    var total = source.Rows.Count;
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((long)total + entry.Rows > TicketRecurrenceService.MaximumRows) continue;
                        var document = _snapshotStore.Load(entry.Id, ct); total += document.Data.Rows.Count;
                        yield return new(document.Data, document.Dashboard, document.Metadata.Label);
                    }
                }
                return TicketRecurrenceService.Analyze(target.Ticket, Sources(), equipmentColumn, ct);
            }, ct);
            if (!_closing && ReferenceEquals(target, _ticketDetail) && ReferenceEquals(source, _result)) _ticketWorkplaceView.Intelligence.ShowRecurrences(report);
        });
    }
}
