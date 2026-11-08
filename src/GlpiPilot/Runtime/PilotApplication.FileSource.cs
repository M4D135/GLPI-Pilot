using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private const string FileSourceDetailReason = "Cette source CSV / Excel contient la liste des tickets, sans leurs fiches détaillées. Utilisez la collecte GLPI par navigateur pour lire les suivis, tâches, solutions et documents.";
    private sealed record FileSourceContext(ExtractionResult? Previous, CityEnrichmentResult? Locations,
        SnapshotDocument? Historical, AppSettings? Interpretation, string FileName);
    private FileSourceContext? _fileSource;
    private readonly HashSet<int> _fileRecordBasket = new();
    public bool IsFileSource => !IsTestMode && Result?.Provenance.SourceKind == "file";
    private bool FileSourceReadComplete => Result is { } data && data.Quality is { } quality && quality.SourceRows == data.Rows.Count
        && (quality.UncachedFormulaCount ?? 0) == 0;

    private async Task<object?> ActivateFileSourceAsync(ExtractionResult data, AppSettings interpretation, string fileName,
        long? sourceRows, bool fileReadComplete, int missingFormulaValues, CancellationToken ct)
    {
        // Prepare all derived rows before replacing the active source or leaving
        // a demo. A failed/cancelled import keeps the previous work intact.
        var locations = CityDetectionService.Enrich(data, interpretation.Cities, interpretation.Dashboard, ct, CityOverrideStore.Load(DataPath("city-overrides.json")).Overrides);
        ct.ThrowIfCancellationRequested();
        var previous = _fileSource is not null && _result?.Provenance.SourceKind == "file" ? _fileSource
            : new FileSourceContext(_result, _locations, _historicalDocument, _resultContext?.Copy(), fileName);
        await RememberSourceAsync(locations.Data, interpretation, locations, ct, realFileImport: true).ConfigureAwait(false);
        // The cache replacement has committed; do not turn that completed import
        // into a cancellation while disposing the former disposable demo.
        if (_simulation is not null) await ExitTestRecordsAsync(default, CancellationToken.None).ConfigureAwait(false);
        _fileSource = previous with { FileName = fileName }; _fileRecordBasket.Clear();
        SetResult(locations.Data, locations, context: interpretation);
        return SessionState();
    }

    private Task<object?> CloseFileSourceAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsFileSource) return Task.FromResult<object?>(SessionState());
        var source = _fileSource;
        _fileSource = null; _fileRecordBasket.Clear();
        if (source?.Previous is not null) SetResult(source.Previous, source.Locations, source.Historical, source.Interpretation);
        else
        {
            Result = null; Locations = null; HistoricalDocument = null; _resultContext = null;
            Revision++; InvalidatePreparedActions(); Publish("session.changed", SessionState());
        }
        return Task.FromResult<object?>(SessionState());
    }

    private object FileRecordBasket(JsonElement args)
    {
        var data = RequireResult();
        if (args.TryGetProperty("rows", out var chosen))
        {
            CheckRevision(args); var rows = Read<int[]>(chosen);
            if (rows.Length > 10000 || rows.Any(row => row < 0 || row >= data.Rows.Count) || _fileRecordBasket.Union(rows).Count() > 10000)
                throw new InvalidDataException("Le panier accepte 10 000 lignes présentes dans le fichier.");
            _fileRecordBasket.UnionWith(rows);
        }
        if (Flag(args, "clear")) _fileRecordBasket.Clear();
        return new { count = _fileRecordBasket.Count, rows = _fileRecordBasket.Order().ToArray() };
    }

    private void EnsureSourceOperationAllowed(string operation)
    {
        if (!IsFileSource) return;
        if (operation.StartsWith("workplace.ticket.", StringComparison.Ordinal) && operation is not ("workplace.ticket.list" or "workplace.ticket.close")
            || operation.StartsWith("workplace.actions.", StringComparison.Ordinal) && operation != "workplace.actions.invalidate"
            || operation.StartsWith("workplace.enrichment.", StringComparison.Ordinal)
            || operation is "library.ticket" or "library.open" or "library.reference" or "library.resolution" or "library.capture" or "workplace.queue.add" or "workplace.document.download")
            throw new InvalidOperationException(FileSourceDetailReason);
    }
}
