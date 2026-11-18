using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using Npgsql;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed class CollectionStoppedException(string message) : InvalidOperationException(message);
    private sealed class CollectionPreparationException(string message) : InvalidOperationException(message);
    private async Task<object?> CaptureLibraryAsync(JsonElement args, CancellationToken ct)
    {
        var targeted = args.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object && Flag(scope, "enabled");
        var title = targeted ? "Collecte ciblée des tickets"
            : Text(args, "mode", 30) is "resume" or "retry" || Flag(args, "automatic") ? "Collecte des tickets" : "Collecte globale des tickets";
        var entry = _journal.Start(title, Settings.PortalUrl);
        string previousSessionId; lock (_collectionGate) previousSessionId = _collectionActivity.SessionId;
        try
        {
            var result = await CollectLibraryControlledAsync(args, ct).ConfigureAwait(false);
            var activity = ReadCollectionActivity();
            var state = activity.State switch { "completed" when activity.ErrorCode.Length == 0 => "Terminé", "paused" => "En pause", "stopped" => "Arrêté", "budget" => "À reprendre", _ => "À vérifier" };
            _journal.Finish(entry, state, activity.ErrorCode.Length > 0 ? activity.ErrorCode : "COLLECTION_" + activity.State.ToUpperInvariant(),
                activity.Message.Length > 0 ? activity.Message : activity.Phase);
            return result;
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException;
            await RecordCollectionPreparationFailureAsync(error, cancelled, previousSessionId).ConfigureAwait(false);
            var activity = ReadCollectionActivity();
            var diagnosed = activity.SessionId.Length > 0 && activity.SessionId != previousSessionId;
            var message = diagnosed && activity.Message.Length > 0 ? activity.Message
                : cancelled ? "Collecte interrompue ; les contenus déjà enregistrés sont conservés." : LibraryCaptureFailureMessage(error);
            var code = diagnosed && activity.ErrorCode.Length > 0 ? activity.ErrorCode : cancelled ? "CANCELLED" : LibraryCaptureFailureCategory(error);
            _journal.Finish(entry, cancelled ? "Annulé" : "Échec", code, message);
            Log("BIBLIOTHÈQUE · préparation ou collecte non aboutie · " + code + " · " + message);
            throw;
        }
    }
    private static bool IsLibraryCaptureFailure(Exception error) => error is PlaywrightException or TimeoutException or InvalidDataException or JsonException
        or InvalidOperationException or IOException or SqliteException or UnauthorizedAccessException or NpgsqlException or GlpiLibraryIndexException;
    private static string LibraryCaptureFailureCategory(Exception error) => error switch
    {
        GlpiLibraryIndexException index => index.Category,
        LibraryCollectorLeaseException => "collection-coordination",
        CollectionStoppedException => "consecutive-errors",
        CollectionPreparationException => "local-storage",
        NpgsqlException => "collection-coordination",
        TimeoutException => "read-timeout", PlaywrightException => "browser-unavailable", InvalidDataException or JsonException => "invalid-content",
        SqliteException or IOException or UnauthorizedAccessException => "local-storage", _ => "local-changed"
    };
    private static string LibraryCaptureFailureMessage(Exception error) => error switch
    {
        GlpiLibraryIndexException index => index.Message,
        LibraryCollectorLeaseException lease => lease.Message,
        CollectionStoppedException stopped => stopped.Message,
        CollectionPreparationException preparation => preparation.Message,
        GlpiLibraryPageException page => "Liste GLPI non reconnue : " + page.Message,
        NpgsqlException => "Le verrou de collecte sur la base partagée n’a pas pu être confirmé. Vérifiez la connexion PostgreSQL avant de reprendre.",
        _ => TicketLibraryCaptureMessages.For(LibraryCaptureFailureCategory(error))
    };
    private static void NormalizeLibraryDate(TicketLibrarySeed seed, LibraryPreferences preferences)
    {
        if (!seed.LastModifiedReliable) { seed.LastModifiedAt = null; return; }
        seed.LastModifiedAt = LibraryPreferencesStore.ParseModification(seed.LastModifiedRaw, preferences.SourceTimeZone, preferences.SourceDateOrder);
        if (seed.LastModifiedAt is null) seed.LastModifiedReliable = false;
    }
    private void LibraryProgress(string message) => Publish("operation.progress", new { message });

    private async Task<object?> SynchronizeLibraryAsync(CancellationToken ct)
    {
        var settings = LibrarySettings.Copy();
        if (!settings.SharingEnabled) throw new InvalidOperationException("Choisissez un dossier partagé ou PostgreSQL pour synchroniser les postes.");
        LibrarySyncStarted(settings.SyncMode == "folder" ? "Synchronisation avec le dossier commun" : "Synchronisation avec PostgreSQL");
        _nextLibrarySync = DateTimeOffset.UtcNow.AddMinutes(settings.IntervalMinutes);
        try
        {
            // Stage first: an unavailable destination must still leave business changes
            // in the durable local outbox for the next successful exchange.
            await StageSharedBusinessAsync(ct).ConfigureAwait(false);
            var replicas = LibraryReplicas(settings);
            var tickets = await TicketLibrarySyncService.SynchronizeAsync(TicketLibrary, replicas.Tickets,
                new InlineProgress<TicketLibrarySyncProgress>(progress => LibraryProgress($"Tickets · {progress.Pushed} envoyés, {progress.Pulled} reçus, {progress.Pending} en attente")), ct).ConfigureAwait(false);
            var team = await TeamWorkspaceSyncService.SynchronizeAsync(TeamWorkspace, replicas.Team,
                new InlineProgress<TeamWorkspaceSyncResult>(progress => LibraryProgress($"Espace d’équipe · {progress.Pushed} envoyés, {progress.Pulled} reçus, {progress.Pending} en attente")), ct).ConfigureAwait(false);
            await ApplySharedBusinessAsync(ct).ConfigureAwait(false);
            _librarySyncState = "ready";
            _librarySyncLabel = $"Synchronisé · {tickets.Pushed + team.Pushed} envois, {tickets.Pulled + team.Pulled} réceptions";
            var pending = tickets.Pending + team.Pending;
            if (pending > 0) _librarySyncLabel += $" · {pending} en attente";
            Log($"SYNCHRONISATION · tickets envoyés={tickets.Pushed}; reçus={tickets.Pulled}; contributions envoyées={team.Pushed}; reçues={team.Pulled}; en attente={pending}.");
            LibrarySyncCompleted(tickets.Pushed + team.Pushed, tickets.Pulled + team.Pulled, tickets.Conflicts + team.Conflicts);
            return new { message = _librarySyncLabel, tickets, team, pending };
        }
        catch (OperationCanceledException)
        { _librarySyncState = "paused"; _librarySyncLabel = "Synchronisation interrompue · reprise possible"; throw; }
        catch (InvalidDataException error)
        {
            _librarySyncState = "attention"; _librarySyncLabel = "Partage à vérifier · données locales conservées";
            _librarySyncError = AppPaths.SafeError(error); throw new InvalidOperationException(_librarySyncLabel + ". " + _librarySyncError);
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.InvalidCatalogName or PostgresErrorCodes.InsufficientPrivilege)
        {
            _librarySyncState = "attention"; _librarySyncLabel = "Base centrale à préparer · modifications conservées localement";
            _librarySyncError = "Demandez au responsable de préparer la base et les droits du compte technique avec GLPI-Pilot-Admin.exe. Les échanges en attente restent disponibles sur ce poste.";
            throw new InvalidOperationException(_librarySyncLabel + ". " + _librarySyncError);
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _librarySyncState = "offline";
            _librarySyncLabel = settings.SyncMode == "folder" ? "Dossier partagé indisponible · modifications conservées localement" : "Central indisponible · modifications conservées localement";
            _librarySyncError = settings.SyncMode == "folder"
                ? "Vérifiez le dossier commun, la connexion réseau et les droits de la session Windows. La prochaine synchronisation reprendra les échanges en attente."
                : "Vérifiez la connexion, le certificat TLS et les droits PostgreSQL. Une prochaine synchronisation reprendra les échanges en attente.";
            // Provider exceptions may include server text or connection details. Never expose them to the page or journal.
            Log("SYNCHRONISATION · échange central interrompu ; file locale conservée pour reprise.");
            throw new InvalidOperationException(_librarySyncLabel + ". " + _librarySyncError);
        }
        finally
        {
            _nextLibrarySync = settings.AutoSync ? DateTimeOffset.UtcNow.AddMinutes(settings.IntervalMinutes) : null;
            Publish("library.changed", new { state = _librarySyncState, simulated = IsSimulationInstance });
        }
    }
    private async Task<object?> AutomaticLibrarySyncAsync(JsonElement _, CancellationToken ct)
    {
        if (IsCachedSource || SessionLocked || EditorOpen || _nextLibrarySync is { } next && next > DateTimeOffset.UtcNow)
            return new { skipped = true };
        try
        {
            var settings = LibrarySettings;
            if (!settings.SharingEnabled || !settings.AutoSync) return new { skipped = true };
            // Mounted shares may remain reachable even when the operating system's
            // Internet availability indicator is false. Let the folder transport decide.
            if (settings.SyncMode == "postgres" && !_networkAvailable())
            {
                await StageSharedBusinessAsync(ct).ConfigureAwait(false);
                _librarySyncState = "offline"; _librarySyncLabel = "Réseau indisponible · modifications conservées localement";
                _librarySyncError = "La synchronisation reprendra automatiquement lorsque la connexion sera disponible.";
                _nextLibrarySync = DateTimeOffset.UtcNow.AddMinutes(settings.IntervalMinutes);
                Publish("library.changed", new { state = _librarySyncState }); return new { deferred = true };
            }
            return await SynchronizeLibraryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or FormatException or System.Security.Cryptography.CryptographicException)
        {
            _nextLibrarySync = DateTimeOffset.UtcNow.AddMinutes(LibrarySettings.IntervalMinutes);
            if (_librarySyncState is not ("offline" or "attention"))
            { _librarySyncState = "paused"; _librarySyncLabel = "Vérifiez les réglages de bibliothèque"; _librarySyncError = "La connexion automatique reprendra après correction des réglages locaux."; }
            return new { deferred = true };
        }
    }
}
