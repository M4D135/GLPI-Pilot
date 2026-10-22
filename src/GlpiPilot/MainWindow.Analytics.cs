using System.Globalization;
using System.IO;
using GlpiPilot.Core;

namespace GlpiPilot;

public partial class MainWindow
{
    private string _analysisSourceKey = "";
    private void RefreshAnalyticsWorkspace()
    {
        if (_historyView is null || _closing) return;
        var entries = new AnalyticsPopulationStore().LoadOrEmpty(out var warning);
        var sourceKey = _result?.Provenance is { SearchVerified: true, ScopeVerified: true } ? SnapshotStore.SourceKey(_result) : "";
        _historyView.ShowPopulations(sourceKey, IsHistoricalResult ? _historicalDocument!.Cities.Sites : _settings.Cities.Sites, entries);
        if (warning.Length > 0) AddLog(warning);
    }
    private void InitializeAnalyticsWorkspace()
    {
        if (_historyView == null) return;
        _historyView.PopulationRefreshRequested += () => { try { RefreshAnalyticsWorkspace(); } catch (Exception error) { ShowError(error); } };
        _historyView.PopulationSaveRequested += entries =>
        {
            try { new AnalyticsPopulationStore().Save(entries); RefreshAnalyticsWorkspace(); QueueDashboardCalculation(); SetStatus("Inventaire daté enregistré ; indicateurs recalculés avec les références valides à la date du relevé."); }
            catch (Exception error) { ShowError(error); }
        };
        _historyView.VerifyAbsentRequested += async (beforeId, rows) =>
        {
            if (_busy || _closing || _result is null) return;
            try
            {
                var document = await Task.Run(() => _snapshotStore.Load(beforeId));
                if (document.Metadata.SourceKey.Length == 0 || document.Metadata.SourceKey != SnapshotStore.SourceKey(_result))
                    throw new InvalidOperationException("Chargez une recherche du même contexte GLPI confirmé avant de vérifier les absents.");
                var references = rows.Take(1000).Select(row => TicketReferenceResolver.Resolve(document.Data, document.Data.Rows[row], _settings)).ToArray();
                EnrichTicketReferences(references, "Absents de la comparaison" + (rows.Length > 1000 ? " · limité aux 1 000 premiers absents" : "") + " · états lus séparément des archives");
            }
            catch (Exception error) { ShowError(error); }
        };
        _historyView.AnalysisRequested += async (snapshotId, count, cadenceHours) => await RunAsync("Analyse des observations historiques…", async ct =>
        {
            _historyView.ShowAnalysisStatus("Chargement des relevés vérifiés et calcul des intervalles comparables…");
            var report = await _backgroundJobs.RunAsync("history-analysis", true, calculationToken =>
            {
                var selected = _snapshotStore.List().SingleOrDefault(x => x.Id == snapshotId) ?? throw new InvalidDataException("Le relevé choisi n’existe plus.");
                if (selected.SourceKey.Length == 0) throw new InvalidDataException("Cette source n’a pas de recherche et de contexte confirmés.");
                var entries = _snapshotStore.List().Where(x => x.SourceKey == selected.SourceKey && x.CapturedAt <= selected.CapturedAt).Take(count).OrderBy(x => x.CapturedAt).ToArray();
                if (entries.Sum(x => (long)x.Rows) > SnapshotAnalytics.MaximumRows) throw new InvalidDataException("Réduisez le nombre de relevés : plus de 2 millions de lignes cumulées.");
                var documents = entries.Select(entry => { calculationToken.ThrowIfCancellationRequested(); return _snapshotStore.Load(entry.Id, calculationToken); }).ToArray();
                return SnapshotAnalytics.Analyze(documents, TimeSpan.FromHours(cadenceHours), calculationToken);
            }, ct);
            _analysisSourceKey = report.SourceKey; _historyView.ShowAnalytics(report);
        });
        _historyView.TicketHistoryRequested += async (sourceKey, id) => await RunAsync("Recherche indexée dans les archives…", async ct =>
        {
            var entries = await Task.Run(() => _snapshotStore.FindTicketHistory(sourceKey, id, ct: ct), ct);
            _historyView.ShowTicketHistory(entries); SetStatus($"{entries.Count} observations conservées pour le ticket demandé.");
        });
        _historyView.AnalysisTicketsRequested += ids =>
        {
            if (_result == null || _busy || _closing) return;
            if (_analysisSourceKey != SnapshotStore.SourceKey(_result)) { SetStatus("Ouvrez un relevé du même contexte GLPI pour consulter ces contributeurs."); return; }
            var column = DashboardService.ResolveColumns(_result.Headers, EffectiveDashboardSettings(_settings.Dashboard)).FirstOrDefault(x => x.Role == "id")?.Index;
            if (column is not int index) return;
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            var rows = _result.Rows.Select((row, number) => (row, number)).Where(x => index < x.row.Length && wanted.Contains(SnapshotAnalytics.NormalizeTicketId(x.row[index]))).Select(x => x.number).ToArray();
            ShowDashboardRows(rows, "Contributeurs de l’évolution historique présents dans ce relevé");
        };
    }
}
