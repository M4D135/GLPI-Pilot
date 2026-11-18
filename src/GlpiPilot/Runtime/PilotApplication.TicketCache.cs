using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private readonly Dictionary<string, TicketReadCacheEntry> _workplaceLibraryReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _workplaceCacheErrors = new(StringComparer.Ordinal);
    private ExtractionResult? _ticketCacheIndexData;
    private long _ticketCacheIndexRevision = -1;
    private readonly Dictionary<string, int> _ticketCacheIdRows = new(StringComparer.Ordinal);

    private void WorkPrepareCacheSourceIndex()
    {
        if (ReferenceEquals(_ticketCacheIndexData, Result) && _ticketCacheIndexRevision == Revision) return;
        _ticketCacheIndexData = Result; _ticketCacheIndexRevision = Revision; _ticketCacheIdRows.Clear();
        if (Result is not { } data || IsSimulationInstance || IsFileSource || string.IsNullOrWhiteSpace(data.SourceUrl)) return;
        var configured = EffectiveSettings.TicketDetails.IdColumn;
        var columns = data.Headers.Select((header, index) => (header, index)).Where(value => configured.Length > 0
            ? value.header == configured : DashboardService.Normalize(value.header) is "id" or "identifiant" or "ticket id").ToArray();
        if (columns.Length != 1) return;
        var column = columns[0].index;
        for (var index = 0; index < data.Rows.Count; index++)
        {
            if (column >= data.Rows[index].Length) continue;
            try
            {
                var id = TicketReferenceResolver.NormalizeId(data.Rows[index][column]);
                if (!_ticketCacheIdRows.TryAdd(id, index)) _ticketCacheIdRows[id] = -1;
            }
            catch (InvalidOperationException) { }
        }
    }

    private TicketReference? WorkCacheRowReference(int rowIndex)
    {
        if (Result is not { } data || rowIndex < 0 || rowIndex >= data.Rows.Count || IsFileSource) return null;
        if (IsSimulationInstance) return _simulationReferences.GetValueOrDefault(rowIndex);
        try { return TicketReferenceResolver.Resolve(data, data.Rows[rowIndex], EffectiveSettings); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return null; }
    }

    private TicketReadSourceVersion? WorkCacheSource(TicketReference ticket)
    {
        WorkPrepareCacheSourceIndex();
        if (Result is not { } data || !_ticketCacheIdRows.TryGetValue(ticket.Id, out var index) || index < 0) return null;
        var current = WorkCacheRowReference(index);
        if (current is null || WorkplaceStore.TicketKey(current) != WorkplaceStore.TicketKey(ticket)) return null;
        return TicketReadCache.Source(current, data, index, EffectiveSettings);
    }

    private bool WorkAutomaticReadAllowed => !IsSimulationInstance && !IsHistorical && !IsFileSource && !IsCachedSource;

    private TicketReadCacheEntry? WorkMemoryCache(PersonalTicket? personal, TicketReference ticket, bool includeOfflinePresence = false)
    {
        var key = WorkplaceStore.TicketKey(ticket);
        if (_workplaceDetails.TryGetValue(key, out var detail))
        {
            if (_workplaceLibraryReads.TryGetValue(key, out var library) && library.ReadAt == detail.ReadAt) return library;
            return new() { HasContent = true, ReadAt = detail.ReadAt,
                Version = personal?.LastReadAt == detail.ReadAt ? personal.LastReadSource : null };
        }
        if (includeOfflinePresence && personal?.Offline == true)
        {
            var path = Path.Combine(DataPath("TicketCache"), key + ".json");
            try
            {
                _ = File.GetAttributes(path);
                return new() { HasContent = true, ReadAt = personal.LastReadAt, Version = personal.LastReadSource };
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return null;
    }

    private static bool WorkCacheUnavailable(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidDataException or JsonException or SqliteException or ArgumentException or InvalidOperationException;

    private TicketLibraryStore? WorkExistingLibrary()
    {
        var path = new LibraryPreferencesStore().Load().DatabasePath;
        try { _ = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        return new TicketLibraryStore(path);
    }

    private async Task WorkLoadCachedTicketAsync(PersonalTicket personal, CancellationToken ct)
    {
        if (IsSimulationInstance || IsFileSource) return;
        var key = personal.Key; var ticket = new TicketReference(personal.Id, personal.Url);
        _workplaceCacheErrors.Remove(key);
        try
        {
            _ = WorkOptionalDetail(personal);
            var store = WorkExistingLibrary();
            if (store is null) return;
            var cached = await store.ReadCachedTicketAsync(ticket, ct).ConfigureAwait(false);
            if (cached?.Detail is not { } detail || _workplaceDetails.TryGetValue(key, out var current) && current.ReadAt >= detail.ReadAt) return;
            detail.Warnings.Add(TicketLibraryRules.IsContentConfirmed(cached)
                ? "Copie chargée depuis la bibliothèque locale ; sa version est comparée au relevé courant."
                : "Copie chargée depuis la bibliothèque locale ; sa version ou sa couverture reste à vérifier.");
            WorkCache(detail);
            var confirmed = TicketLibraryRules.IsContentConfirmed(cached);
            _workplaceLibraryReads[key] = new()
            {
                HasContent = true, ReadAt = detail.ReadAt, FromLibrary = true,
                Version = new()
                {
                    TicketKey = key, ModifiedRaw = cached.DetailModifiedRaw, ModifiedAt = cached.DetailModifiedAt,
                    Precision = cached.Seed.LastModifiedPrecision, Reliable = confirmed && cached.Seed.LastModifiedReliable,
                    ObservedAt = cached.Seed.ObservedAt, MainReadComplete = confirmed && TicketReadCache.MainReadComplete(detail)
                }
            };
        }
        catch (Exception error) when (WorkCacheUnavailable(error))
        { _workplaceCacheErrors[key] = "Une copie locale n’a pas pu être vérifiée. Le dernier contenu disponible est conservé."; }
    }

    private TicketReadCacheStatus WorkTicketCacheStatus(PersonalTicket personal)
    {
        var ticket = new TicketReference(personal.Id, personal.Url);
        var source = WorkCacheSource(ticket); var cached = WorkMemoryCache(personal, ticket);
        var reason = _workplaceCacheErrors.GetValueOrDefault(personal.Key, "");
        return TicketReadCache.Describe(source, cached, WorkAutomaticReadAllowed, reason.Length == 0, reason);
    }

    private async Task<IReadOnlyDictionary<int, TicketReadCacheStatus>> WorkRecordCacheStatusAsync(int[] rows, CancellationToken ct)
    {
        var result = new Dictionary<int, TicketReadCacheStatus>();
        var references = rows.Distinct().ToDictionary(index => index, WorkCacheRowReference);
        var valid = references.Values.Where(reference => reference is not null).Cast<TicketReference>().ToArray();
        IReadOnlyDictionary<string, TicketReadCacheEntry> library = new Dictionary<string, TicketReadCacheEntry>();
        Dictionary<string, PersonalTicket> personal = new(StringComparer.Ordinal);
        var known = true; var reason = "";
        try
        {
            // Listing a cache does not create a personal workspace or a library database.
            var document = _workplaceDocument ?? (File.Exists(DataPath("workplace.json")) ? WorkplaceStore.Load(DataPath("workplace.json")) : new());
            personal = document.Tickets.ToDictionary(ticket => ticket.Key, StringComparer.Ordinal);
            if (!IsSimulationInstance && !IsFileSource && valid.Length > 0 && WorkExistingLibrary() is { } store)
                library = await store.ReadCacheMetadataAsync(valid, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (WorkCacheUnavailable(error))
        { known = false; reason = "Le cache local n’a pas pu être vérifié ; la liste des tickets reste consultable."; }
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (references[row] is not { } ticket)
            {
                result[row] = TicketReadCache.Describe(null, null, false, false,
                    "L’identité et l’installation GLPI de cette ligne ne permettent pas de vérifier une copie locale.");
                continue;
            }
            var key = WorkplaceStore.TicketKey(ticket); var lookupKnown = known; var rowReason = reason;
            TicketReadCacheEntry? cached = null;
            try { cached = WorkMemoryCache(personal.GetValueOrDefault(key), ticket, true); }
            catch (Exception error) when (WorkCacheUnavailable(error))
            { lookupKnown = false; rowReason = "La copie hors connexion n’a pas pu être vérifiée."; }
            if (library.GetValueOrDefault(key) is { HasContent: true } stored && (cached?.ReadAt is null || stored.ReadAt > cached.ReadAt)) cached = stored;
            var source = WorkCacheSource(ticket);
            if (source is null && !IsSimulationInstance)
            { lookupKnown = false; rowReason = "Le ticket est absent ou son identité est ambiguë dans le relevé courant."; }
            result[row] = TicketReadCache.Describe(source, cached, WorkAutomaticReadAllowed, lookupKnown, rowReason);
        }
        return result;
    }
}
