using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using Npgsql;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private readonly object _collectionGate = new();
    private CancellationTokenSource? _collectionCancellation;
    private string _collectionIntent = "";
    private CollectionActivity _collectionActivity = new();
    private sealed record CollectionActivity
    {
        public string SessionId { get; init; } = "";
        public string RunId { get; init; } = "";
        public string State { get; init; } = "idle";
        public string Phase { get; init; } = "";
        public string PhaseCode { get; init; } = "";
        public string Message { get; init; } = "";
        public string ErrorCode { get; init; } = "";
        public int Requests { get; init; }
        public int Processed { get; init; }
        public int Total { get; init; }
        public int Reused { get; init; }
        public double WaitSeconds { get; init; }
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    private void RegisterLibraryCollectionOperations()
    {
        Register("library.collection.status", (_, _) => Task.FromResult<object?>(ReadCollectionActivity()));
        Register("library.collection.events", ReadCollectionEventsAsync);
        Register("library.collection.control", (a, _) => Task.FromResult<object?>(ControlLibraryCollection(a)));
        Register("library.collection.get", async (_, ct) =>
        {
            var context = LibraryCaptureContext(Settings, LibrarySettings);
            var state = await TicketLibrary.CollectionStateAsync(context.Fingerprint, ct).ConfigureAwait(false);
            var report = state.RunId.Length == 0 ? null : await TicketLibrary.GetCaptureAsync(state.RunId, ct).ConfigureAwait(false);
            return new { state, report, installation = context.Installation, activity = ReadCollectionActivity(), applicationMustRemainOpen = true };
        });
        Register("library.collection.settings", async (a, ct) =>
        {
            if (IsTestMode) throw new InvalidOperationException("La programmation réelle ne peut pas être activée depuis la simulation.");
            var options = Read<LibraryCollectionOptions>(a.GetProperty("options")); options.Validate();
            if (options.Automatic && !Flag(a, "confirmedCollector")) throw new InvalidOperationException("Confirmez que ce poste est chargé de la collecte automatique pour ce périmètre.");
            var context = LibraryCaptureContext(Settings, LibrarySettings);
            var state = await TicketLibrary.CollectionStateAsync(context.Fingerprint, ct).ConfigureAwait(false);
            var expected = a.GetProperty("revision").GetInt64();
            if (state.Revision != expected) throw new InvalidOperationException("Les réglages ont changé. Actualisez le menu de collecte.");
            if (state.State == "running") throw new InvalidOperationException("Mettez la collecte en pause avant de modifier son rythme.");
            state.Options = options; state.NextAt = options.Automatic && !state.Held ? DateTimeOffset.UtcNow.AddMinutes(options.IntervalMinutes) : null;
            return await TicketLibrary.SaveCollectionStateAsync(context.Fingerprint, state, expected, ct).ConfigureAwait(false);
        });
        RegisterLibraryBootstrapOperations();
    }

    // Controls and the bounded read-only journal bypass the operation semaphore. They never start a browser read.
    private bool TryCollectionControl(string operation, JsonElement args, CancellationToken ct, out Task<object?> result)
    {
        result = null!;
        if (operation is not ("library.collection.control" or "library.collection.status" or "library.collection.events")) return false;
        if (_simulation is not null || IsSimulationInstance) { result = Task.FromResult<object?>(new { supported = false, state = "simulation" }); return true; }
        ct.ThrowIfCancellationRequested();
        if (operation == "library.collection.events") { result = ReadCollectionEventsAsync(args.Clone(), ct); return true; }
        result = Task.FromResult<object?>(operation == "library.collection.status" ? ReadCollectionActivity() : ControlLibraryCollection(args));
        return true;
    }
    private CollectionActivity ReadCollectionActivity()
    {
        var context = CurrentCollectionContext();
        lock (_collectionGate) return context?.Fingerprint == _collectionContextFingerprint
            || context is null && _collectionSourceIdentity == CollectionSourceIdentity() && _collectionJournal is { RunId.Length: 0 }
            ? _collectionActivity : new();
    }
    private object ControlLibraryCollection(JsonElement args)
    {
        var action = Text(args, "action", 20); var session = Text(args, "sessionId", 32);
        if (action is not ("pause" or "stop")) throw new InvalidDataException("Commande de collecte inconnue.");
        CancellationTokenSource cancellation;
        lock (_collectionGate)
        {
            if (_collectionCancellation is null || session.Length == 0 || session != _collectionActivity.SessionId)
                return new { requested = false, reason = "La collecte ciblée n’est plus active." };
            if (_collectionIntent != "stop") _collectionIntent = action;
            _collectionActivity = _collectionActivity with { State = _collectionIntent == "stop" ? "stopping" : "pausing", UpdatedAt = DateTimeOffset.UtcNow };
            cancellation = _collectionCancellation;
        }
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        Publish("library.collection.progress", ReadCollectionActivity());
        return new { requested = true, message = "Arrêt des nouvelles lectures ; conservation du point de reprise en cours." };
    }
    private void CollectionProgress(Func<CollectionActivity, CollectionActivity> change)
    {
        object snapshot;
        lock (_collectionGate) { _collectionActivity = change(_collectionActivity) with { UpdatedAt = DateTimeOffset.UtcNow }; snapshot = _collectionActivity; }
        Publish("library.collection.progress", snapshot);
    }

    private async Task<object?> CollectLibraryControlledAsync(JsonElement args, CancellationToken requestCancellation)
    {
        var mode = Text(args, "mode", 30, "incremental");
        if (mode is not ("incremental" or "all" or "retry" or "resume")) throw new InvalidDataException("Mode de collecte inconnu.");
        var settings = Settings.Copy(); _ = TargetUrlPolicy.Validate(settings.PortalUrl);
        LibraryPreferences preferences; TicketLibraryStore local; TicketLibraryCaptureContext context; LibraryCollectionState state;
        try
        {
            preferences = LibrarySettings.Copy(); context = LibraryCaptureContext(settings, preferences); local = TicketLibrary;
            state = await local.CollectionStateAsync(context.Fingerprint, requestCancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            throw new CollectionPreparationException(error switch
            {
                SqliteException sql => $"La bibliothèque locale n’a pas pu être ouverte (SQLite {sql.SqliteErrorCode}/{sql.SqliteExtendedErrorCode}). Vérifiez le fichier et l’espace disponible.",
                UnauthorizedAccessException => "L’accès aux réglages ou à la bibliothèque locale a été refusé. Vérifiez les droits du dossier.",
                InvalidDataException or JsonException => "Les réglages, le chemin ou le format de la bibliothèque locale ne sont pas reconnus. Les données existantes sont conservées.",
                _ => "Les réglages ou le dossier de la bibliothèque locale ne sont pas accessibles. Vérifiez le disque et les fichiers verrouillés."
            });
        }
        if (state.State == "running") throw new InvalidOperationException("Une collecte utilise déjà cette bibliothèque locale.");
        if (state.Held && mode == "incremental" && !Flag(args, "confirmedNewCycle"))
            throw new InvalidOperationException("Une collecte est suspendue. Reprenez-la ou confirmez explicitement un nouveau cycle ; les contenus restent conservés.");
        var resumeId = mode == "resume" ? Text(args, "runId", 32, state.RunId) : "";
        if (mode == "resume" && resumeId.Length == 0) throw new InvalidOperationException("Aucune collecte enregistrée à reprendre.");
        var parent = mode == "retry" ? Text(args, "runId", 32) : "";
        var targets = mode == "retry" ? await local.CaptureRetryTargetsAsync(parent, context, requestCancellation).ConfigureAwait(false) : null;
        if (targets is { Count: 0 }) throw new InvalidOperationException("Aucune fiche à reprendre dans cet essai.");
        state.Options.Validate();
        using var controlled = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(state.Options.MaximumMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(controlled.Token, budget.Token);
        var ct = linked.Token; var session = Guid.NewGuid().ToString("N");
        string runId = "", outcome = "attention"; var limits = new List<string>(); var finished = false; var checkpointStarted = false; var ownsJournal = false;
        LibraryCollectionJournalSession journal;
        lock (_collectionGate)
        {
            if (_collectionCancellation is not null) throw new InvalidOperationException("Une collecte est déjà active sur ce poste.");
            _collectionCancellation = controlled; _collectionIntent = "";
            _collectionActivity = new() { SessionId = session, State = "running", Phase = "Préparation du journal", PhaseCode = "prepare" };
            _collectionContextFingerprint = context.Fingerprint;
            _collectionSourceIdentity = CollectionSourceIdentity();
            _collectionJournal = journal = new(local, context, () => Log("BIBLIOTHÈQUE · journal disponible dans cette session ; sauvegarde locale des événements interrompue."));
        }
        try
        {
            CollectionDiagnostic(journal, "lease", "Vérification du poste collecteur", "collection-start", "Démarrage du passage ; vérification du droit de collecte et du point de reprise.");
            await using var lease = await LibraryCollectorLease.AcquireAsync(preferences, context.Installation, preferences.PostgresEnabled ? LibraryConnectionPassword(preferences) : "", ct).ConfigureAwait(false);
            using var pacing = LibraryCollectionPacer.Enter(state.Options,
                (requests, delay) => CollectionProgress(s => s with { Requests = requests, WaitSeconds = Math.Max(0, delay.TotalSeconds) }),
                async token =>
                {
                    if (Flag(args, "automatic") && !state.Options.Allows(DateTimeOffset.UtcNow))
                    { budget.Cancel(); token.ThrowIfCancellationRequested(); }
                    await lease.VerifyAsync(token).ConfigureAwait(false);
                });
            if (mode == "resume") { runId = resumeId; await local.ResumeCaptureAsync(runId, context, ct).ConfigureAwait(false); }
            else runId = await local.BeginCaptureAsync(context, mode, parent, ct).ConfigureAwait(false);
            journal.Bind(runId);
            ownsJournal = true; state.RunId = runId; state.State = "running"; state.NextAt = null;
            state = await local.SaveCollectionStateAsync(context.Fingerprint, state, state.Revision, ct).ConfigureAwait(false); checkpointStarted = true;
            CollectionProgress(s => s with { RunId = runId, Phase = mode == "resume" ? "Revalidation de l’index, contenus conservés" : "Lecture de l’index accessible" });
            CollectionDiagnostic(journal, "index", "Lecture de l’index accessible", mode == "resume" ? "collection-resume" : "index-start",
                mode == "resume" ? "Reprise du même journal ; les contenus déjà enregistrés sont conservés." : "Journal ouvert ; lecture des tickets de tous les statuts dans les entités accessibles.");
            LibraryProgress("Lecture de l’index de tous les statuts dans les entités accessibles…");
            // The index can span many GLPI pages before the first ticket is examined: show its progress instead of a frozen zero.
            var indexPhase = mode == "resume" ? "Revalidation de l’index" : "Lecture de l’index accessible";
            var lastIndexPage = 0;
            var indexProgress = new InlineProgress<ExtractionProgress>(p =>
            {
                var message = p.Pages > 0 ? $"{indexPhase} · page {p.Pages:N0} · {p.Rows:N0}{(p.Expected is int expected ? $" / {expected:N0}" : "")} ticket(s) indexé(s)" : indexPhase;
                CollectionProgress(s => s with { Phase = message, PhaseCode = "index" });
                if (p.Pages > lastIndexPage) { lastIndexPage = p.Pages; CollectionDiagnostic(journal, "index", indexPhase, "index-page", message, changePhase: false); }
            });
            void IndexDiagnostic(LibraryIndexProgress p) => CollectionDiagnostic(journal, p.Phase, p.PhaseLabel, p.Category, p.Message,
                p.Category == "restore-unconfirmed" ? "warning" : "info");
            var index = await Browser.ReadTicketLibraryIndexAsync(settings, indexProgress, ct, preferences.SourceDateOrder, IndexDiagnostic).ConfigureAwait(false);
            if (!index.AllStatuses) throw new GlpiLibraryIndexException("all-status", "index-scope", "Le filtre de tous les statuts n’est pas confirmé. Aucune fiche n’a été lue.");
            foreach (var seed in index.Tickets) NormalizeLibraryDate(seed, preferences);
            IReadOnlyCollection<string>? resumedKeys = null;
            if (mode == "resume") resumedKeys = await local.ReconcileCaptureIndexAsync(runId, index, ct).ConfigureAwait(false);
            else await local.SetCaptureIndexAsync(runId, index, targets, ct).ConfigureAwait(false);
            limits.AddRange(index.Limitations);
            CollectionDiagnostic(journal, "index", "Index enregistré", index.Complete ? "index-saved" : "index-partial",
                $"{index.Tickets.Count:N0} tickets indexés et enregistrés dans le point de reprise. " + (index.Complete ? "Pagination confirmée." : "Index partiel : les limites sont conservées."), index.Complete ? "info" : "warning");
            var selected = resumedKeys?.ToHashSet(StringComparer.Ordinal) ?? targets?.Select(t => t.TicketKey).ToHashSet(StringComparer.Ordinal);
            var tickets = index.Tickets.Where(s => selected is null || selected.Contains(TicketLibraryRules.Key(s.Ticket))).ToList();
            var requiredReads = mode == "resume" ? await local.RequiredCollectionReadKeysAsync(runId, ct).ConfigureAwait(false) : null;
            // A stable portal token is not an indefinite freshness guarantee.
            // Honor the existing library setting (seven days by default).
            var policy = new TicketLibraryCachePolicy { MaximumAge = TimeSpan.FromDays(preferences.MaxAgeDays) };
            var processed = 0; var reused = 0; var failures = 0; TicketReference? lastRead = null;
            CollectionProgress(s => s with { Total = tickets.Count, Phase = "Lecture des contenus nouveaux, incomplets ou modifiés" });
            CollectionDiagnostic(journal, "details", "Lecture des fiches", "details-start", $"{tickets.Count:N0} tickets à examiner ; réutilisation des contenus encore valides et lecture des autres.");
            foreach (var seed in tickets)
            {
                ct.ThrowIfCancellationRequested(); var key = TicketLibraryRules.Key(seed.Ticket);
                try
                {
                    // A saved but unconfirmed body is checked against the refreshed index before any re-download.
                    // The store checks the exact revision/hash/proof and never confirms a competing version.
                    if (mode == "resume" && index.Complete) await local.ConfirmCaptureTicketAsync(runId, seed, true, ct).ConfigureAwait(false);
                    var observed = await local.ObserveCaptureSeedAsync(runId, seed, ct).ConfigureAwait(false); policy.Now = DateTimeOffset.UtcNow;
                    policy.ForceRefresh = mode == "all" || requiredReads?.Contains(key) == true;
                    if (TicketLibraryStore.CanSkip(seed, observed.Document, TicketLibraryAcquisition.ReaderSignature, policy))
                    {
                        var awaiting = !TicketLibraryRules.IsContentConfirmed(observed.Document);
                        if (mode == "resume") await local.RecordCollectionResumeReuseAsync(runId, key, ct, awaiting).ConfigureAwait(false);
                        else await local.RecordCollectionReuseAsync(runId, key, ct, awaiting).ConfigureAwait(false);
                        reused++;
                    }
                    else
                    {
                        await local.RecordCaptureReadingAsync(runId, key, ct).ConfigureAwait(false);
                        var detail = await Browser.ReadTicketLibraryDetailsAsync(seed.Ticket, settings, Progress(), ct).ConfigureAwait(false);
                        await local.SaveCaptureDetailAsync(runId, seed, detail, observed.Document.LocalRevision, ct).ConfigureAwait(false);
                        lastRead = seed.Ticket;
                    }
                    failures = 0;
                }
                catch (Exception error) when (IsLibraryCaptureFailure(error))
                {
                    if (error is SqliteException or IOException or UnauthorizedAccessException or NpgsqlException or LibraryCollectorLeaseException) throw;
                    await local.RecordCaptureFailureAsync(runId, key, LibraryCaptureFailureCategory(error), ct).ConfigureAwait(false);
                    CollectionDiagnostic(journal, "details", "Lecture d’une fiche", LibraryCaptureFailureCategory(error),
                        LibraryCaptureFailureMessage(error), "error", seed.Ticket.Id, changePhase: false);
                    failures++;
                    if (failures >= state.Options.ConsecutiveErrorLimit)
                        throw new CollectionStoppedException("Plusieurs lectures n’ont pas abouti. Les causes par ticket sont conservées dans le journal ; vérifiez la connexion et la session avant une reprise explicite.");
                }
                processed++; CollectionProgress(s => s with { Processed = processed, Reused = reused });
                if (processed == 1 || processed % 25 == 0 || processed == tickets.Count)
                    CollectionDiagnostic(journal, "details", "Contenus enregistrés", "details-progress", $"{processed:N0} / {tickets.Count:N0} tickets examinés ; {reused:N0} contenus réutilisés.", changePhase: false);
                LibraryProgress($"Bibliothèque · {processed}/{tickets.Count} tickets · {reused} contenus conservés sans nouvelle lecture");
            }
            // A convenience only: the collection result does not depend on it.
            if (lastRead is not null)
                try { await Browser.RememberTicketFormTabAsync(lastRead, settings, ct).ConfigureAwait(false); }
                catch (Exception error) when (error is PlaywrightException or TimeoutException or InvalidOperationException) { }
            var finalVerified = false; var finalRead = false;
            try
            {
                CollectionProgress(s => s with { Phase = "Confirmation finale des dates et du périmètre" });
                CollectionDiagnostic(journal, "verification", "Confirmation finale", "verification-start", "Relecture de l’index pour confirmer les dates, les contenus et le périmètre.");
                var final = await Browser.ReadTicketLibraryIndexAsync(settings, Progress(), ct, preferences.SourceDateOrder,
                    p => CollectionDiagnostic(journal, "verification-" + p.Phase, "Confirmation finale · " + p.PhaseLabel, p.Category, p.Message,
                        p.Category == "restore-unconfirmed" ? "warning" : "info")).ConfigureAwait(false);
                finalVerified = final.AllStatuses && final.Complete && index.Complete && final.ScopeFingerprint == index.ScopeFingerprint;
                if (!final.AllStatuses || ObjectIdentity.Installation(final.SourceUrl) != context.Installation)
                    throw new GlpiLibraryIndexException("all-status", "index-scope", "Le périmètre de la confirmation finale diffère. Les contenus reçus restent conservés sans confirmation de fraîcheur.");
                foreach (var seed in final.Tickets) { NormalizeLibraryDate(seed, preferences); await local.ConfirmCaptureTicketAsync(runId, seed, finalVerified, ct).ConfigureAwait(false); }
                finalRead = true; limits.AddRange(final.Limitations);
                if (!finalVerified) limits.Add("Le périmètre reste partiel ou a changé. Aucune absence n’est traitée comme une suppression.");
            }
            catch (Exception error) when (IsLibraryCaptureFailure(error) && error is not (SqliteException or IOException or UnauthorizedAccessException or NpgsqlException or LibraryCollectorLeaseException))
            {
                limits.Add("La confirmation finale n’a pas abouti. Les contenus reçus restent consultables et reprenables.");
                CollectionDiagnostic(journal, "verification", "Confirmation finale", LibraryCaptureFailureCategory(error), LibraryCaptureFailureMessage(error), "warning");
            }
            await local.FinishCaptureVerificationAsync(runId, finalRead, finalVerified, ct).ConfigureAwait(false);
            if (preferences.SourceTimeZone.Length == 0) limits.Add("Configurez le fuseau des dates GLPI pour permettre une réutilisation fiable des contenus.");
            limits.Add("Les fichiers joints ne sont pas téléchargés automatiquement : seules leurs références disponibles sont conservées.");
            await local.FinishCaptureAsync(runId, "completed", limits, ct).ConfigureAwait(false); finished = true; outcome = "completed";
            var completedReport = await local.GetCaptureAsync(runId, ct).ConfigureAwait(false);
            if (finalVerified && completedReport.RetryCount == 0) state.LastSuccess = DateTimeOffset.UtcNow;
            var completionMessage = completedReport.Message + (finalVerified ? " · Périmètre final confirmé." : " · Fraîcheur finale à confirmer.");
            CollectionDiagnostic(journal, "completed", "Passage terminé", "collection-completed", completionMessage,
                finalVerified && completedReport.RetryCount == 0 ? "info" : "warning");
            CollectionProgress(s => s with { Message = completionMessage, ErrorCode = finalVerified && completedReport.RetryCount == 0 ? "" : "collection-incomplete" });
        }
        catch (OperationCanceledException)
        {
            string intent; lock (_collectionGate) intent = _collectionIntent;
            outcome = intent == "stop" ? "stopped" : intent == "pause" ? "paused" : budget.IsCancellationRequested && !requestCancellation.IsCancellationRequested ? "budget" : "interrupted";
            limits.Add(outcome == "budget" ? "Budget de temps atteint ; contenus conservés. La reprise revalide l’index sans réaspirer les contenus inchangés." : "Collecte suspendue ; les contenus enregistrés et le journal sont conservés.");
            CollectionDiagnostic(journal, "interrupted", "Collecte suspendue", "collection-" + outcome, limits[^1], "warning");
            CollectionProgress(s => s with { Message = limits[^1], ErrorCode = "collection-" + outcome });
        }
        catch (Exception error) when (IsLibraryCaptureFailure(error))
        {
            outcome = "attention";
            var code = LibraryCaptureFailureCategory(error); var message = LibraryCaptureFailureMessage(error);
            limits.Add(message);
            var phase = error is GlpiLibraryIndexException indexError ? indexError.PhaseLabel : ReadCollectionActivity().Phase;
            CollectionDiagnostic(journal, error is GlpiLibraryIndexException indexFailure ? indexFailure.Phase : ReadCollectionActivity().PhaseCode,
                phase.Length > 0 ? phase : "Collecte des tickets", code, message, "error");
            CollectionProgress(s => s with { Message = message, ErrorCode = code });
        }
        finally
        {
            // Caller cancellation must not cancel the short transaction that stores the resume point.
            using var saveBudget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                if (ownsJournal && !finished)
                {
                    // Keep the historical journal contract for a cancelled request,
                    // independently of the held scheduler state stored below.
                    var journalStatus = outcome switch
                    {
                        "paused" or "stopped" => outcome,
                        "attention" => "failed",
                        "interrupted" when requestCancellation.IsCancellationRequested => "cancelled",
                        _ => "interrupted"
                    };
                    await local.FinishCaptureAsync(runId, journalStatus, limits, saveBudget.Token).ConfigureAwait(false);
                }
                if (checkpointStarted)
                {
                    state.State = outcome;
                    state.NextAt = state.Options.Automatic && !state.Held ? DateTimeOffset.UtcNow.AddMinutes(state.Options.IntervalMinutes) : null;
                    state = await local.SaveCollectionStateAsync(context.Fingerprint, state, state.Revision, saveBudget.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (IsLibraryCaptureFailure(error) || error is OperationCanceledException)
            {
                outcome = "interrupted";
                const string checkpointError = "Le point de reprise n’a pas pu être finalisé ; le journal sera vérifié à la prochaine ouverture, sans reprise automatique.";
                CollectionDiagnostic(journal, "checkpoint", "Sauvegarde du point de reprise", "local-storage",
                    checkpointError, "error");
                CollectionProgress(s => s with { PhaseCode = "checkpoint", Phase = "Sauvegarde du point de reprise", ErrorCode = "local-storage", Message = checkpointError });
            }
            finally
            {
                if (ownsJournal) TicketLibraryStore.ReleaseCapture(runId);
                lock (_collectionGate) { _collectionCancellation = null; _collectionIntent = ""; }
                await journal.CompleteAsync(saveBudget.Token).ConfigureAwait(false);
                CollectionProgress(s => s with { State = outcome, WaitSeconds = 0 });
                Publish("library.changed", new { captured = true, runId });
            }
        }
        // The caller must observe its cancellation, but only after durable state
        // has been committed. Dedicated pause/stop and budget tokens are separate.
        requestCancellation.ThrowIfCancellationRequested();
        if (!ownsJournal) throw new InvalidOperationException("La collecte n’a pas pu ouvrir son journal. Aucune lecture de ticket n’a été lancée.");
        using var reportBudget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var report = await local.GetCaptureAsync(runId, reportBudget.Token).ConfigureAwait(false);
        return new { report.RunId, report.Message, report.Status, report.StatusLabel, report.StartedAt, report.CompletedAt, report.Mode,
            report.Observed, report.Targeted, report.Processed, report.Read, report.Reused, report.Failed, report.Partial,
            report.Conflicts, report.Unconfirmed, report.RetryCount, report.Scope, report.FinalScopeVerified, report.Limitations, collectionState = outcome };
    }

    private async Task<bool> AutomaticCollectionTickAsync(CancellationToken ct)
    {
        if (IsTestMode || IsFileSource || IsCachedSource || IsHistorical || SessionLocked || EditorOpen || !_networkAvailable()
            || !Uri.TryCreate(Settings.PortalUrl, UriKind.Absolute, out var portal) || portal.Scheme is not ("http" or "https")) return false;
        var context = LibraryCaptureContext(Settings, LibrarySettings);
        var state = await TicketLibrary.CollectionStateAsync(context.Fingerprint, ct).ConfigureAwait(false);
        if (!state.Options.Automatic || state.Held || state.State == "running" || state.NextAt is null || state.NextAt > DateTimeOffset.UtcNow || !state.Options.Allows(DateTimeOffset.UtcNow)) return false;
        EnsureCachedSourceOperationAllowed("library.capture"); EnsureSourceOperationAllowed("library.capture");
        state.NextAt = DateTimeOffset.UtcNow.AddMinutes(state.Options.IntervalMinutes);
        state = await TicketLibrary.SaveCollectionStateAsync(context.Fingerprint, state, state.Revision, ct).ConfigureAwait(false);
        var args = JsonSerializer.SerializeToElement(new { mode = state.State == "budget" && state.RunId.Length > 0 ? "resume" : "incremental", runId = state.RunId, automatic = true }, Json);
        await CaptureLibraryAsync(args, ct).ConfigureAwait(false); return true;
    }
}
