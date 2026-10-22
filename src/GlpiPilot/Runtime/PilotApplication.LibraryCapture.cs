using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using Npgsql;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private async Task<object?> CaptureLibraryAsync(JsonElement args, CancellationToken ct)
    {
        var mode = Text(args, "mode", 30, "incremental");
        if (mode is not ("incremental" or "all" or "retry")) throw new InvalidDataException("Mode de collecte de bibliothèque inconnu.");
        var settings = Settings.Copy(); _ = TargetUrlPolicy.Validate(settings.PortalUrl);
        var preferences = LibrarySettings.Copy(); var local = TicketLibrary;
        await local.InitializeAsync(ct).ConfigureAwait(false);
        var context = LibraryCaptureContext(settings, preferences); var parentRunId = mode == "retry" ? Text(args, "runId", 32) : "";
        var targets = mode == "retry" ? await local.CaptureRetryTargetsAsync(parentRunId, context, ct).ConfigureAwait(false) : null;
        if (targets is { Count: 0 }) throw new InvalidOperationException("Cet essai ne contient aucune fiche à reprendre.");
        var runId = await local.BeginCaptureAsync(context, mode, parentRunId, ct).ConfigureAwait(false);
        var limits = new List<string>(); var finished = false; var status = "failed";
        try
        {
            LibraryProgress("Lecture de l’index de tous les statuts dans les entités GLPI accessibles…");
            var index = await Browser.ReadTicketLibraryIndexAsync(settings, Progress(), ct, preferences.SourceDateOrder).ConfigureAwait(false);
            if (!index.AllStatuses) throw new InvalidDataException("La recherche GLPI n’a pas confirmé tous les statuts.");
            foreach (var seed in index.Tickets) NormalizeLibraryDate(seed, preferences);
            await local.SetCaptureIndexAsync(runId, index, targets, ct).ConfigureAwait(false); limits.AddRange(index.Limitations);
            var selected = targets?.Select(t => t.TicketKey).ToHashSet(StringComparer.Ordinal);
            var tickets = index.Tickets.Where(s => selected is null || selected.Contains(TicketLibraryRules.Key(s.Ticket))).ToList();
            var policy = new TicketLibraryCachePolicy { ForceRefresh = mode is "all" or "retry", MaximumAge = TimeSpan.FromDays(preferences.MaxAgeDays) };
            var processed = 0; var reused = 0;
            foreach (var seed in tickets)
            {
                ct.ThrowIfCancellationRequested(); var key = TicketLibraryRules.Key(seed.Ticket);
                try
                {
                    var observed = await local.ObserveCaptureSeedAsync(runId, seed, ct).ConfigureAwait(false); policy.Now = DateTimeOffset.UtcNow;
                    if (TicketLibraryStore.CanSkip(seed, observed.Document, TicketLibraryAcquisition.ReaderSignature, policy))
                    {
                        await local.RecordCaptureReuseAsync(runId, key, ct).ConfigureAwait(false); reused++; processed++;
                        LibraryProgress($"Bibliothèque · {processed}/{tickets.Count} tickets · {reused} contenus réutilisés"); continue;
                    }
                    await local.RecordCaptureReadingAsync(runId, key, ct).ConfigureAwait(false);
                    LibraryProgress($"Lecture des contenus · {processed + 1}/{tickets.Count}");
                    var detail = await Browser.ReadTicketLibraryDetailsAsync(seed.Ticket, settings, Progress(), ct).ConfigureAwait(false);
                    await local.SaveCaptureDetailAsync(runId, seed, detail, observed.Document.LocalRevision, ct).ConfigureAwait(false);
                }
                catch (Exception error) when (IsLibraryCaptureFailure(error))
                {
                    // Only a fixed category enters the per-ticket journal; provider messages never reach it or the general log.
                    await local.RecordCaptureFailureAsync(runId, key, LibraryCaptureFailureCategory(error), ct).ConfigureAwait(false);
                    Log("BIBLIOTHÈQUE · une fiche n’a pas pu être conservée complètement ; le journal local permet de la reprendre.");
                }
                processed++;
            }
            var finalScopeVerified = false; var finalIndexRead = false;
            // A body is durable but explicitly unconfirmed until its own GLPI version is reobserved.
            try
            {
                LibraryProgress("Vérification finale des dates de modification et du périmètre…");
                var finalIndex = await Browser.ReadTicketLibraryIndexAsync(settings, Progress(), ct, preferences.SourceDateOrder).ConfigureAwait(false);
                finalScopeVerified = finalIndex.AllStatuses && finalIndex.Complete && index.Complete && finalIndex.ScopeFingerprint == index.ScopeFingerprint;
                if (!finalIndex.AllStatuses || ObjectIdentity.Installation(finalIndex.SourceUrl) != context.Installation)
                    throw new InvalidDataException("Le périmètre final ne correspond pas à cette collecte.");
                foreach (var seed in finalIndex.Tickets)
                {
                    ct.ThrowIfCancellationRequested(); NormalizeLibraryDate(seed, preferences);
                    await local.ConfirmCaptureTicketAsync(runId, seed, finalScopeVerified, ct).ConfigureAwait(false);
                }
                finalIndexRead = true; limits.AddRange(finalIndex.Limitations);
                if (!finalScopeVerified) limits.Add("Le périmètre final diffère ou reste partiel. Les observations sont conservées ; aucune absence n’est traitée comme une suppression.");
            }
            catch (Exception error) when (IsLibraryCaptureFailure(error))
            {
                finalScopeVerified = false;
                limits.Add("La vérification finale n’a pas abouti. Les contenus reçus restent consultables, avec leur état de confirmation et une reprise ciblée.");
            }
            await local.FinishCaptureVerificationAsync(runId, finalIndexRead, finalScopeVerified, ct).ConfigureAwait(false);
            if (preferences.SourceTimeZone.Length == 0)
                limits.Add("Le fuseau des dates GLPI n’est pas configuré : les dates sans décalage explicite ne permettent pas de confirmer la fraîcheur ni de réutiliser automatiquement les contenus.");
            limits.Add("Les pièces jointes sont conservées sous forme de références et métadonnées ; leurs fichiers binaires ne sont pas téléchargés.");
            await local.FinishCaptureAsync(runId, "completed", limits, ct).ConfigureAwait(false); finished = true;
            var report = await local.GetCaptureAsync(runId, ct).ConfigureAwait(false);
            Log($"BIBLIOTHÈQUE · index={report.Observed}; lus={report.Read}; cache={report.Reused}; partiels={report.Partial}; à reprendre={report.RetryCount}; versions concurrentes={report.Conflicts}.");
            string sync = "";
            if (preferences.PostgresEnabled)
            {
                try { await SynchronizeLibraryAsync(ct).ConfigureAwait(false); sync = _librarySyncLabel; }
                catch (InvalidOperationException) { sync = _librarySyncLabel; }
            }
            return new
            {
                report.RunId, report.Message, report.Status, report.StatusLabel, report.StartedAt, report.CompletedAt, report.Mode,
                report.Observed, report.Targeted, report.Processed, report.Read, report.Reused, report.Failed, report.Partial,
                report.Conflicts, report.Unconfirmed, report.RetryCount, report.Scope, report.FinalScopeVerified, report.Limitations, sync
            };
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) when (IsLibraryCaptureFailure(error))
        {
            limits.Add(TicketLibraryCaptureMessages.For(LibraryCaptureFailureCategory(error)));
            throw new InvalidOperationException("La collecte n’a pas abouti. Son bilan local conserve les observations et les fiches à reprendre.");
        }
        finally
        {
            if (!finished)
            {
                // Cancellation of the browser must not cancel the short journal commit that makes resumption possible.
                using var saveBudget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await local.FinishCaptureAsync(runId, status, limits, saveBudget.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsLibraryCaptureFailure(error) || error is OperationCanceledException)
                { Log("BIBLIOTHÈQUE · le bilan sera marqué interrompu à la prochaine ouverture ; les observations déjà enregistrées restent conservées."); }
            }
            TicketLibraryStore.ReleaseCapture(runId);
            Publish("library.changed", new { captured = true, runId });
        }
    }
    private static bool IsLibraryCaptureFailure(Exception error) => error is PlaywrightException or TimeoutException or InvalidDataException or JsonException
        or InvalidOperationException or IOException or SqliteException or UnauthorizedAccessException;
    private static string LibraryCaptureFailureCategory(Exception error) => error switch
    {
        TimeoutException => "read-timeout", PlaywrightException => "browser-unavailable", InvalidDataException or JsonException => "invalid-content",
        SqliteException or IOException or UnauthorizedAccessException => "local-storage", _ => "local-changed"
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
        if (!settings.PostgresEnabled) throw new InvalidOperationException("Configurez et activez la connexion centrale pour synchroniser les postes.");
        var connection = settings.ConnectionString(LibraryConnectionPassword(settings));
        _librarySyncState = "running"; _librarySyncLabel = "Synchronisation en cours"; _librarySyncError = "";
        _nextLibrarySync = DateTimeOffset.UtcNow.AddMinutes(settings.IntervalMinutes);
        try
        {
            var tickets = await TicketLibrarySyncService.SynchronizeAsync(TicketLibrary, new TicketLibraryPostgresStore(connection),
                new InlineProgress<TicketLibrarySyncProgress>(progress => LibraryProgress($"Tickets · {progress.Pushed} envoyés, {progress.Pulled} reçus, {progress.Pending} en attente")), ct).ConfigureAwait(false);
            var team = await TeamWorkspaceSyncService.SynchronizeAsync(TeamWorkspace, new PostgresTeamWorkspaceReplica(connection),
                new InlineProgress<TeamWorkspaceSyncResult>(progress => LibraryProgress($"Espace d’équipe · {progress.Pushed} envoyés, {progress.Pulled} reçus, {progress.Pending} en attente")), ct).ConfigureAwait(false);
            _librarySyncState = "ready";
            _librarySyncLabel = $"Synchronisé · {tickets.Pushed + team.Pushed} envois, {tickets.Pulled + team.Pulled} réceptions";
            var pending = tickets.Pending + team.Pending;
            if (pending > 0) _librarySyncLabel += $" · {pending} en attente";
            Log($"SYNCHRONISATION · tickets envoyés={tickets.Pushed}; reçus={tickets.Pulled}; contributions envoyées={team.Pushed}; reçues={team.Pulled}; en attente={pending}.");
            Publish("library.changed", new { synchronized = true });
            return new { message = _librarySyncLabel, tickets, team, pending };
        }
        catch (OperationCanceledException)
        { _librarySyncState = "paused"; _librarySyncLabel = "Synchronisation interrompue · reprise possible"; throw; }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            _librarySyncState = "offline";
            _librarySyncLabel = "Central indisponible · modifications conservées localement";
            _librarySyncError = "Vérifiez la connexion, le certificat TLS et les droits PostgreSQL. Une prochaine synchronisation reprendra les échanges en attente.";
            // Provider exceptions may include server text or connection details. Never expose them to the page or journal.
            Log("SYNCHRONISATION · échange central interrompu ; file locale conservée pour reprise.");
            throw new InvalidOperationException(_librarySyncLabel + ". " + _librarySyncError);
        }
        finally { _nextLibrarySync = DateTimeOffset.UtcNow.AddMinutes(settings.IntervalMinutes); }
    }
    private async Task<object?> AutomaticLibrarySyncAsync(JsonElement _, CancellationToken ct)
    {
        if (SessionLocked || EditorOpen || !_networkAvailable() || _nextLibrarySync is { } next && next > DateTimeOffset.UtcNow)
            return new { skipped = true };
        try
        {
            var settings = LibrarySettings;
            if (!settings.PostgresEnabled || !settings.AutoSync) return new { skipped = true };
            return await SynchronizeLibraryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or FormatException or System.Security.Cryptography.CryptographicException)
        {
            _nextLibrarySync = DateTimeOffset.UtcNow.AddMinutes(5);
            if (_librarySyncState != "offline")
            { _librarySyncState = "paused"; _librarySyncLabel = "Vérifiez les réglages de bibliothèque"; _librarySyncError = "La connexion automatique reprendra après correction des réglages locaux."; }
            return new { deferred = true };
        }
    }
}
