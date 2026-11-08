using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private SnapshotDocument? _cachedDocument;
    private SnapshotDocument? _cachedSourceDocument;
    private LastSourceCacheEntry? _activeCachedEntry;
    private bool _cachedRecovered;
    private bool _cacheStartupAttempted;
    private string _cacheError = "";
    public bool IsCachedSource => !IsTestMode && _cachedDocument is not null && ReferenceEquals(HistoricalDocument, _cachedDocument);
    private static LastSourceCacheStore SourceCache => new();
    private void RegisterSourceCacheOperations()
    {
        Register("cache.state", (_, _) => Task.FromResult<object?>(SourceCacheState()));
        Register("cache.restore", async (_, ct) =>
        {
            if (IsTestMode) throw new InvalidOperationException("Fermez la démonstration avant de consulter le dernier relevé réel conservé.");
            if (!await RestoreSourceCacheAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException(_cacheError.Length > 0 ? _cacheError : "Aucun relevé conservé. Importez une liste ou réalisez une collecte GLPI.");
            return SessionState();
        });
        Register("cache.clear", (a, ct) =>
        {
            if (IsTestMode) throw new InvalidOperationException("La démonstration ne peut pas effacer le cache réel.");
            if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez l’effacement du dernier relevé conservé. La base, le calendrier et les paramètres restent conservés.");
            SourceCache.Clear(ct); _cacheError = ""; _cacheStartupAttempted = true;
            Publish("cache.changed", new { cleared = true });
            return Task.FromResult<object?>(SourceCacheState());
        });
    }
    private object SourceCacheState()
    {
        LastSourceCacheEntry? entry = null;
        if (!IsTestMode)
            try { entry = SourceCache.Describe(); if (entry is not null && IsCachedSource && _cachedRecovered) entry = _activeCachedEntry; }
            catch (Exception error) when (LastSourceCacheStore.IsStorageError(error)) { _cacheError = AppPaths.SafeError(error); }
        return new
        {
            available = entry is not null, simulation = IsTestMode, cachedSource = IsCachedSource,
            sourceKind = entry?.SourceKind ?? "", fileName = entry?.FileName ?? "", title = entry?.Title ?? "", source = entry?.SourceUrl ?? "",
            extractedAt = entry?.ExtractedAt, savedAt = entry?.SavedAt, rows = entry?.Rows ?? 0, columns = entry?.Columns ?? 0, complete = entry?.Complete ?? false,
            recovered = IsCachedSource && _cachedRecovered, error = IsTestMode ? "" : _cacheError,
            detail = IsTestMode ? "La démonstration reste séparée du cache réel."
                : entry is null ? "Aucun relevé n’est conservé sur ce poste. Une importation ou collecte réussie prépare automatiquement la prochaine consultation."
                : "Copie locale du dernier relevé réussi, avec sa date et son interprétation. Aucun fichier original ni réseau nécessaire. Les données ne sont pas actualisées pendant cette consultation."
        };
    }
    private async Task RememberSourceAsync(ExtractionResult source, AppSettings settings, CityEnrichmentResult? locations, CancellationToken ct, bool realFileImport = false)
    {
        if (IsSimulationInstance || IsTestMode && !realFileImport || source.Provenance.SourceKind is not ("file" or "glpi" or "browser" or "unknown")) return;
        try
        {
            var populations = new AnalyticsPopulationStore().LoadOrEmpty(out _);
            await Task.Run(() => SourceCache.Save(source, settings, locations, populations, ct), ct).ConfigureAwait(false);
            _cacheError = ""; Publish("cache.changed", new { saved = true });
        }
        catch (Exception error) when (LastSourceCacheStore.IsStorageError(error))
        {
            _cacheError = "Le relevé est chargé, mais sa copie hors connexion n’a pas pu être enregistrée. Le cache précédent reste conservé. " + AppPaths.SafeError(error);
            Log(_cacheError); Publish("cache.changed", new { saved = false, message = _cacheError });
        }
    }
    private async Task<bool> RestoreSourceCacheAsync(CancellationToken ct)
    {
        if (IsTestMode) return false;
        try
        {
            var loaded = await Task.Run(() => SourceCache.Load(ct), ct).ConfigureAwait(false);
            if (loaded is null) return false;
            ct.ThrowIfCancellationRequested();
            var projection = CityCorrectionProjection.Create(loaded.Document, Settings.Cities, CityOverrideStore.Load(DataPath("city-overrides.json")), ct);
            ct.ThrowIfCancellationRequested();
            _cachedSourceDocument = loaded.Document;
            _cachedDocument = projection?.Document ?? loaded.Document; _activeCachedEntry = loaded.Entry; _cachedRecovered = loaded.Recovered;
            _fileSource = null; _fileRecordBasket.Clear();
            SetResult(_cachedDocument.Data, projection?.Locations ?? _cachedDocument.LocationEvidence?.ToReport(_cachedDocument.Data), _cachedDocument);
            _analyticsRun = null; _analyticsQueryRun = null; _cacheError = "";
            if (loaded.Recovered) Log("CACHE · le dernier relevé était illisible ; la copie précédente vérifiée a été restaurée.");
            return true;
        }
        catch (Exception error) when (LastSourceCacheStore.IsStorageError(error))
        { _cacheError = AppPaths.SafeError(error); Log("CACHE · " + _cacheError); return false; }
    }
    private async Task RestoreStartupSourceAsync(CancellationToken ct)
    {
        if (IsTestMode || _cacheStartupAttempted || Result is not null) return;
        _cacheStartupAttempted = true;
        if (!await RestoreSourceCacheAsync(ct).ConfigureAwait(false)) await RestoreLastHistoryOnStartupAsync(ct).ConfigureAwait(false);
    }
    private void EnsureCachedSourceOperationAllowed(string operation)
    {
        if (!IsCachedSource) return;
        if (operation.StartsWith("workplace.ticket.", StringComparison.Ordinal) && operation is not ("workplace.ticket.list" or "workplace.ticket.close")
            || operation.StartsWith("workplace.actions.", StringComparison.Ordinal) && operation != "workplace.actions.invalidate"
            || operation.StartsWith("workplace.enrichment.", StringComparison.Ordinal)
            || operation is "library.capture" or "library.open" or "workplace.document.download" or "workplace.queue.add"
                or "terrain.tours.route" or "terrain.tours.weather")
            throw new InvalidOperationException("Ce relevé conservé est consulté hors connexion. Lancez explicitement une nouvelle collecte GLPI pour retrouver les lectures et actions en ligne.");
    }
}
