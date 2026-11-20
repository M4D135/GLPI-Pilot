using System.IO;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using Npgsql;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    // Each finite window is indexed, read and confirmed before moving on. A time
    // budget therefore never has to restart a whole large inventory before it
    // can make progress on the next ticket body.
    private async Task CollectLibraryWindowsAsync(TicketLibraryStore local, TicketLibraryCaptureContext context,
        AppSettings settings, LibraryPreferences preferences, LibraryCollectionState state, string runId,
        string mode, LibraryCollectionPlan plan, LibraryCollectionJournalSession journal, List<string> limits, CancellationToken ct)
    {
        await local.InitializeCollectionWindowsAsync(runId, context, plan, ct).ConfigureAwait(false);
        var durable = await local.CollectionPlanAsync(runId, context, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Le périmètre ciblé du passage n’est plus disponible.");
        var offset = durable.NextOrdinal;
        var coverage = await local.CollectionPlanCoverageAsync(runId, context, ct).ConfigureAwait(false);
        var existing = await local.GetCaptureAsync(runId, ct).ConfigureAwait(false);
        var reused = existing.Reused; var failures = 0;
        var windowSize = Math.Min(TicketLibraryStore.CollectionWindowSize, Math.Clamp(settings.MaxRows, 1, TicketLibraryStore.CaptureTicketLimit));
        var requiredReads = mode == "resume" ? await local.RequiredCollectionReadKeysAsync(runId, ct).ConfigureAwait(false) : null;
        var policy = new TicketLibraryCachePolicy();
        limits.AddRange(plan.Warnings);
        CollectionProgress(s => s with { RunId = runId, Total = plan.Selected, Processed = offset, Reused = reused,
            Coverage = coverage, CoverageVerifiedAt = durable.VerifiedAt, Phase = "Préparation des lots ciblés", PhaseCode = "targeted" });
        CollectionDiagnostic(journal, "targeted", "Périmètre ciblé enregistré", mode == "resume" ? "collection-resume" : "targeted-start",
            $"{plan.Selected:N0} tickets sélectionnés dans un relevé de {plan.InventoryCount:N0} lignes ; {offset:N0} références de lots déjà terminés. Vérification et confirmation par lots de {windowSize} au maximum.");

        while (offset < plan.Selected)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(windowSize, plan.Selected - offset);
            var window = plan.Tickets.GetRange(offset, count);
            var references = window.Select(seed => seed.Ticket).ToArray();
            var label = $"Tickets {offset + 1:N0} à {offset + count:N0} sur {plan.Selected:N0}";
            void IndexDiagnostic(LibraryIndexProgress item) => CollectionDiagnostic(journal, "index", label + " · " + item.PhaseLabel,
                item.Category, item.Message, item.Category == "restore-unconfirmed" ? "warning" : "info");
            var indexProgress = new InlineProgress<ExtractionProgress>(item => CollectionProgress(s => s with
            { Phase = label + $" · {item.Rows:N0} référence(s) vérifiée(s)", PhaseCode = "index" }));
            CollectionDiagnostic(journal, "index", label, "window-index-start", "Vérification des seules références du lot avant lecture des contenus.");
            var index = await Browser.ReadTicketLibrarySubsetIndexAsync(settings, references, indexProgress, ct,
                preferences.SourceDateOrder, IndexDiagnostic).ConfigureAwait(false);
            ValidateCollectionWindowIndex(index, window, context.Installation);
            if (!index.Complete)
                throw new GlpiLibraryIndexException("targeted", "index-incomplete", "Le lot ciblé n’a pas été vérifié entièrement. Vérifier les limites de pagination avant de reprendre ; les lots déjà terminés restent conservés.");
            foreach (var seed in index.Tickets) NormalizeLibraryDate(seed, preferences);
            var beforeWindow = await local.CollectionWindowCoverageAsync(runId, context, offset, count, ct).ConfigureAwait(false);
            await local.ApplyCollectionWindowIndexAsync(runId, context, index, offset, count, ct).ConfigureAwait(false);
            await local.UpdateCollectionWindowEvidenceAsync(runId, context, offset, count, index.Tickets, ct).ConfigureAwait(false);
            var indexedCoverage = await local.CollectionWindowCoverageAsync(runId, context, offset, count, ct).ConfigureAwait(false);
            coverage = AdjustCollectionCoverage(coverage, beforeWindow, indexedCoverage)!;
            CollectionProgress(s => s with { Coverage = coverage, CoverageVerifiedAt = index.ObservedAt });
            limits.AddRange(index.Limitations);
            var processed = offset + count - index.Tickets.Count;
            TicketReference? lastRead = null;
            // Keep the plan's order even if the native list has a different sort.
            var byKey = index.Tickets.ToDictionary(seed => TicketLibraryRules.Key(seed.Ticket), StringComparer.Ordinal);
            foreach (var planned in window)
            {
                ct.ThrowIfCancellationRequested();
                var key = TicketLibraryRules.Key(planned.Ticket);
                if (!byKey.TryGetValue(key, out var seed)) continue;
                var beforeTicket = await local.CollectionCoverageAsync([seed], TicketLibraryAcquisition.ReaderSignature, ct).ConfigureAwait(false);
                try
                {
                    if (!existing.MissingOnly || !await local.PreserveCaptureContentAsync(runId, key, ct).ConfigureAwait(false))
                    {
                        // A body saved before interruption can be confirmed by this
                        // fresh same-window observation before considering a reread.
                        if (mode == "resume") await local.ConfirmCaptureTicketAsync(runId, seed, true, ct).ConfigureAwait(false);
                        var observed = await local.ObserveCaptureSeedAsync(runId, seed, ct).ConfigureAwait(false);
                        policy.Now = DateTimeOffset.UtcNow; policy.ForceRefresh = mode == "all" || requiredReads?.Contains(key) == true;
                        var compatibleEarlierRead = TicketLibraryAcquisition.IsEarlierReader(observed.Document.ReaderSignature)
                            && TicketLibraryStore.CanSkip(seed, observed.Document, TicketLibraryAcquisition.ReaderSignature, policy);
                        var canSkip = policy.ForceRefresh
                            ? TicketLibraryStore.CanSkip(seed, observed.Document, TicketLibraryAcquisition.ReaderSignature, policy)
                            : LibraryCollectionScopePlanner.CanReuse(seed, observed.Document, TicketLibraryAcquisition.ReaderSignature) || compatibleEarlierRead;
                        if (canSkip)
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
                        throw new CollectionStoppedException("Plusieurs lectures n’ont pas abouti. Les lots terminés et les contenus reçus sont conservés ; vérifier la connexion avant de reprendre le lot en cours.");
                }
                var afterTicket = await local.CollectionCoverageAsync([seed], TicketLibraryAcquisition.ReaderSignature, ct).ConfigureAwait(false);
                coverage = AdjustCollectionCoverage(coverage, beforeTicket, afterTicket)!;
                processed++;
                CollectionProgress(s => s with { Processed = processed, Reused = reused, Coverage = coverage,
                    Phase = label + " · lecture des contenus nécessaires", PhaseCode = "details" });
                LibraryProgress($"Bibliothèque · {processed:N0}/{plan.Selected:N0} tickets examinés · {reused:N0} contenus réutilisés");
            }
            if (lastRead is not null)
                try { await Browser.RememberTicketFormTabAsync(lastRead, settings, ct).ConfigureAwait(false); }
                catch (Exception error) when (error is PlaywrightException or TimeoutException or InvalidOperationException) { }

            CollectionDiagnostic(journal, "verification", label + " · confirmation", "window-verification-start",
                "Confirmation des versions et contenus de ce lot avant d’enregistrer le point de reprise suivant.");
            var final = await Browser.ReadTicketLibrarySubsetIndexAsync(settings, references, Progress(), ct, preferences.SourceDateOrder,
                item => CollectionDiagnostic(journal, "verification", label + " · " + item.PhaseLabel, item.Category, item.Message,
                    item.Category == "restore-unconfirmed" ? "warning" : "info")).ConfigureAwait(false);
            ValidateCollectionWindowIndex(final, window, context.Installation);
            if (!final.Complete || final.ScopeFingerprint != index.ScopeFingerprint)
                throw new GlpiLibraryIndexException("scope", "index-scope", "La confirmation du lot est partielle ou son périmètre GLPI a changé. Les contenus restent conservés ; le lot sera revalidé à la reprise.");
            beforeWindow = await local.CollectionWindowCoverageAsync(runId, context, offset, count, ct).ConfigureAwait(false);
            foreach (var seed in final.Tickets)
            {
                NormalizeLibraryDate(seed, preferences);
                await local.ConfirmCaptureTicketAsync(runId, seed, true, ct).ConfigureAwait(false);
            }
            await local.UpdateCollectionWindowEvidenceAsync(runId, context, offset, count, final.Tickets, ct).ConfigureAwait(false);
            // The cursor is committed before the completion event. A pause from
            // that event resumes the next window, even after process restart.
            await local.CompleteCollectionWindowAsync(runId, context, offset, count, ct).ConfigureAwait(false);
            var finalCoverage = await local.CollectionWindowCoverageAsync(runId, context, offset, count, ct).ConfigureAwait(false);
            coverage = AdjustCollectionCoverage(coverage, beforeWindow, finalCoverage)!;
            limits.AddRange(final.Limitations); offset += count;
            CollectionProgress(s => s with { Processed = offset, Reused = reused, Coverage = coverage, CoverageVerifiedAt = final.ObservedAt });
            CollectionDiagnostic(journal, "window-completed", "Lot enregistré", "window-completed",
                $"{offset:N0}/{plan.Selected:N0} références traitées ; point de reprise enregistré après confirmation du lot.");
        }
        ct.ThrowIfCancellationRequested();
        await local.FinishCaptureVerificationAsync(runId, true, true, ct).ConfigureAwait(false);
        var completedCoverage = await local.CollectionPlanCoverageAsync(runId, context, ct).ConfigureAwait(false);
        CollectionProgress(s => s with { Coverage = completedCoverage });
        if (preferences.SourceTimeZone.Length == 0) limits.Add("Configurer le fuseau des dates GLPI pour permettre une réutilisation fiable des contenus.");
        limits.Add("La sélection est vérifiée par lots ; les dates de vérification des tickets peuvent différer pendant le passage.");
        limits.Add("Les fichiers joints ne sont pas téléchargés automatiquement : seules leurs références disponibles sont conservées.");
    }

    private static void ValidateCollectionWindowIndex(TicketLibraryIndex index, IReadOnlyList<TicketLibrarySeed> window, string installation)
    {
        if (!index.IsTargeted || !index.AllStatuses || index.RequestedCount != window.Count
            || index.ScopeFingerprint.Length == 0 || ObjectIdentity.Installation(index.SourceUrl) != installation)
            throw new GlpiLibraryIndexException("targeted", "index-scope", "Le contrôle GLPI ne correspond pas au lot de tickets demandé. Aucune fiche de ce lot n’est collectée.");
        var allowed = window.Select(seed => TicketLibraryRules.Key(seed.Ticket)).ToHashSet(StringComparer.Ordinal);
        if (index.Tickets.Any(seed => !allowed.Contains(TicketLibraryRules.Key(seed.Ticket))))
            throw new GlpiLibraryIndexException("targeted", "index-scope", "GLPI a renvoyé un ticket extérieur au lot sélectionné. Le périmètre de collecte est conservé.");
    }
}
