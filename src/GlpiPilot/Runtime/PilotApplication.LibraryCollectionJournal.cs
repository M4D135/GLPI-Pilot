using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private LibraryCollectionJournalSession? _collectionJournal;
    private string _collectionContextFingerprint = "";
    private string _collectionSourceIdentity = "";
    private string CollectionSourceIdentity() => Settings.ActiveSourceId + "\n" + Settings.PortalUrl + "\n" + Settings.BrowserChannel;

    private TicketLibraryCaptureContext? CurrentCollectionContext()
    {
        try { return LibraryCaptureContext(Settings, LibrarySettings); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task<object?> ReadCollectionEventsAsync(JsonElement args, CancellationToken ct)
    {
        var runId = Text(args, "runId", 32); var offset = Number(args, "offset", 0, 0, TicketLibraryStore.CaptureTicketLimit);
        var limit = Number(args, "limit", 50, 1, 100);
        if (IsCachedSource)
        {
            var source = Result; var installation = CachedCollectionInstallation();
            var store = installation.Length == 0 ? null : WorkExistingLibrary();
            var savedPage = store is null ? new TicketLibraryCollectionEventPage { Offset = offset, Limit = limit }
                : await store.ReadSavedCollectionEventsAsync(installation, runId, offset, limit, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!IsCachedSource || !ReferenceEquals(source, Result))
                throw new InvalidOperationException("Le relevé consulté a changé pendant la lecture du journal. Actualisez cet écran.");
            return savedPage;
        }
        var context = CurrentCollectionContext();
        if (context is null)
        {
            // Reading preferences may itself be the failed operation. The unbound session tail must remain available.
            lock (_collectionGate)
                if (runId.Length == 0 && _collectionSourceIdentity == CollectionSourceIdentity() && _collectionJournal is { RunId.Length: 0 } fallback)
                    return fallback.MemoryPage(offset, limit);
            throw new InvalidOperationException("Configurez une source GLPI avant de consulter son journal de collecte.");
        }
        LibraryCollectionJournalSession? live;
        lock (_collectionGate) live = _collectionContextFingerprint == context.Fingerprint ? _collectionJournal : null;
        TicketLibraryCollectionEventPage page;
        if (live is not null && (runId.Length == 0 || runId == live.RunId) && !live.Persisted)
            page = live.MemoryPage(offset, limit);
        else
        {
            if (runId.Length == 0 && live?.RunId.Length > 0) runId = live.RunId;
            try { page = await TicketLibrary.CollectionEventsAsync(runId, context, offset, limit, ct).ConfigureAwait(false); }
            catch (Exception error) when (live is not null && runId == live.RunId && error is (SqliteException or IOException or UnauthorizedAccessException))
            { page = live.MemoryPage(offset, limit); }
        }
        ct.ThrowIfCancellationRequested();
        if (CurrentCollectionContext()?.Fingerprint != context.Fingerprint)
            throw new InvalidOperationException("La source GLPI a changé pendant la lecture du journal. Actualisez cet écran.");
        return page;
    }

    private void CollectionDiagnostic(LibraryCollectionJournalSession journal, string phaseCode, string stage,
        string code, string message, string level = "info", string ticketId = "", bool changePhase = true)
    {
        static string Clean(string value, int length) => new string(AppPaths.SafeLog(value).Where(c => !char.IsControl(c)).Take(length).ToArray());
        var item = new TicketLibraryCollectionEvent { Stage = Clean(stage, 160), Code = code,
            Message = Clean(message, 1600), Level = level, TicketId = ticketId };
        lock (_collectionGate)
        {
            if (!ReferenceEquals(_collectionJournal, journal) || !journal.Append(item)) return;
            if (_collectionActivity.State == "running")
                _collectionActivity = _collectionActivity with { Phase = changePhase ? item.Stage : _collectionActivity.Phase,
                    PhaseCode = changePhase ? phaseCode : _collectionActivity.PhaseCode, Message = item.Message,
                    ErrorCode = level == "error" ? code : _collectionActivity.ErrorCode, UpdatedAt = item.At };
        }
        var identity = journal.RunId.Length > 0 ? journal.RunId[..8] : "préparation";
        Log($"BIBLIOTHÈQUE · {identity} · {item.Stage} · {item.Level} · {item.Code} · {item.Message}");
        Publish("library.collection.progress", ReadCollectionActivity());
    }

    private async Task RecordCollectionPreparationFailureAsync(Exception error, bool cancelled, string previousSessionId)
    {
        var context = CurrentCollectionContext();
        if (context is null)
        {
            try { context = LibraryCaptureContext(Settings, new LibraryPreferences()); }
            catch (Exception failure) when (failure is InvalidDataException or InvalidOperationException or ArgumentException) { return; }
        }
        lock (_collectionGate)
        {
            if (_collectionCancellation is not null) return;
            // An engine failure already has its own phase, durable event and held state.
            if (_collectionActivity.SessionId.Length > 0 && _collectionActivity.SessionId != previousSessionId && _collectionContextFingerprint == context.Fingerprint
                && _collectionActivity.State is "attention" or "interrupted" or "paused" or "stopped" or "budget") return;
            _collectionContextFingerprint = context.Fingerprint;
            _collectionSourceIdentity = CollectionSourceIdentity();
            _collectionJournal = new(null, context, () => Log("BIBLIOTHÈQUE · le journal de collecte reste disponible dans cette session ; sa sauvegarde locale n’a pas abouti."));
            _collectionActivity = new() { SessionId = Guid.NewGuid().ToString("N"), State = "running", Phase = "Préparation de la collecte", PhaseCode = "prepare" };
        }
        var journal = _collectionJournal;
        if (journal is null) return;
        var message = cancelled ? "La préparation de la collecte a été annulée." : LibraryCaptureFailureMessage(error);
        CollectionDiagnostic(journal, "prepare", "Préparation de la collecte", cancelled ? "interrupted" : LibraryCaptureFailureCategory(error), message, cancelled ? "warning" : "error");
        CollectionProgress(s => s with { State = cancelled ? "interrupted" : "attention", Message = message, WaitSeconds = 0 });
        // No database run was opened. Only the empty writer is closed; the live tail remains readable.
        await journal.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
