using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Playwright;
using Npgsql;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private async Task<object?> CaptureLibraryAsync(JsonElement args, CancellationToken ct)
    {
        var mode = Text(args, "mode", 30, "incremental");
        if (mode is not ("incremental" or "all")) throw new InvalidDataException("Mode de collecte de bibliothèque inconnu.");
        var settings = Settings.Copy(); _ = TargetUrlPolicy.Validate(settings.PortalUrl);
        var preferences = LibrarySettings.Copy(); var local = TicketLibrary;
        await local.InitializeAsync(ct).ConfigureAwait(false);
        LibraryProgress("Lecture de l’index de tous les statuts dans les entités GLPI accessibles…");
        var index = await Browser.ReadTicketLibraryIndexAsync(settings, Progress(), ct, preferences.SourceDateOrder).ConfigureAwait(false);
        if (!index.AllStatuses) throw new InvalidDataException("La recherche GLPI n’a pas confirmé tous les statuts. La collecte de bibliothèque est interrompue.");
        var read = 0; var reused = 0; var failed = 0; var partial = 0; var conflicts = 0;
        var policy = new TicketLibraryCachePolicy { ForceRefresh = mode == "all", MaximumAge = TimeSpan.FromDays(preferences.MaxAgeDays) };
        foreach (var seed in index.Tickets)
        {
            ct.ThrowIfCancellationRequested(); NormalizeLibraryDate(seed, preferences);
            var observed = await local.ObserveSeedAsync(seed, ct).ConfigureAwait(false);
            if (observed.ConflictCreated) conflicts++;
            policy.Now = DateTimeOffset.UtcNow;
            if (TicketLibraryStore.CanSkip(seed, observed.Document, TicketLibraryAcquisition.ReaderSignature, policy))
            { reused++; LibraryProgress($"Bibliothèque · {read + reused + failed}/{index.Tickets.Count} tickets · {reused} contenus réutilisés"); continue; }
            try
            {
                LibraryProgress($"Lecture des contenus · {read + reused + failed + 1}/{index.Tickets.Count}");
                var detail = await Browser.ReadTicketLibraryDetailsAsync(seed.Ticket, settings, Progress(), ct).ConfigureAwait(false);
                var saved = await local.SaveDetailAsync(seed, detail.Detail, detail.Coverage, detail.ReaderSignature, observed.Document.LocalRevision, ct).ConfigureAwait(false);
                read++; if (!saved.Document.DetailComplete) partial++; if (saved.ConflictCreated) conflicts++;
            }
            catch (Exception error) when (error is PlaywrightException or TimeoutException or InvalidDataException or InvalidOperationException)
            {
                // Partial successes are already committed. No ticket identifier, title or DOM value enters the general journal.
                failed++; Log("BIBLIOTHÈQUE · une fiche n’a pas pu être conservée complètement ; elle sera proposée à la prochaine collecte.");
            }
        }
        var limits = index.Limitations.ToList(); var finalScopeVerified = false;
        // A long collection is not a transaction in GLPI. Reobserve date_mod after reading the bodies,
        // so a ticket changed during this run cannot remain a current solution in the local search.
        try
        {
            LibraryProgress("Vérification finale des dates de modification et du périmètre…");
            var finalIndex = await Browser.ReadTicketLibraryIndexAsync(settings, Progress(), ct, preferences.SourceDateOrder).ConfigureAwait(false);
            finalScopeVerified = finalIndex.AllStatuses && finalIndex.Complete && index.Complete && finalIndex.ScopeFingerprint == index.ScopeFingerprint;
            foreach (var seed in finalIndex.Tickets)
            { ct.ThrowIfCancellationRequested(); NormalizeLibraryDate(seed, preferences); await local.ObserveSeedAsync(seed, ct).ConfigureAwait(false); }
            limits.AddRange(finalIndex.Limitations);
            if (!finalScopeVerified) limits.Add("Le périmètre final diffère ou reste partiel. Les observations déjà conservées restent consultables ; aucune absence n’est traitée comme une suppression.");
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException or InvalidDataException or InvalidOperationException)
        { limits.Add("La vérification finale des dates n’a pas abouti. Une nouvelle mise à jour du cache est nécessaire pour confirmer la fraîcheur après cette collecte."); }
        if (preferences.SourceTimeZone.Length == 0)
            limits.Add("Le fuseau des dates GLPI n’est pas configuré : les dates sans décalage explicite ne permettent pas de réutiliser automatiquement les contenus.");
        limits.Add("Les pièces jointes sont conservées sous forme de références et métadonnées ; leurs fichiers binaires ne sont pas téléchargés.");
        var message = $"Bibliothèque mise à jour · {index.Tickets.Count:N0} tickets observés, {read:N0} contenus lus, {reused:N0} réutilisés";
        if (failed > 0 || partial > 0) message += $" · {failed:N0} lectures à reprendre, {partial:N0} contenus partiels";
        Log($"BIBLIOTHÈQUE · index={index.Tickets.Count}; lus={read}; cache={reused}; partiels={partial}; à reprendre={failed}; versions concurrentes={conflicts}.");
        string sync = "";
        if (preferences.PostgresEnabled)
        {
            try { await SynchronizeLibraryAsync(ct).ConfigureAwait(false); sync = _librarySyncLabel; }
            catch (InvalidOperationException) { sync = _librarySyncLabel; }
        }
        Publish("library.changed", new { captured = true });
        return new { message, observed = index.Tickets.Count, read, reused, failed, partial, conflicts, scope = index.ScopeDescription, finalScopeVerified, limitations = limits.Distinct().ToArray(), sync };
    }
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
