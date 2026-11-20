using System.Diagnostics;
using System.IO;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed class AutomaticExtractionService(BrowserService browser)
{
    public event Action<string>? Log;
    public event Action<string>? NativeFileSaved;
    public event Action<AutomaticRunPhase>? PhaseChanged;

    public async Task<AutomaticRunResult> RunAsync(AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct, IReadOnlyList<CityTicketOverride>? overrides = null)
    {
        AutomaticRunResult? run = null;
        TableChoice? tableToRemember = null;
        Task<ColumnRestorationResult>? cleanup = null;
        Task<ColumnRestorationResult> RestoreOnce() => cleanup ??= browser.CompleteTemporaryColumnCollectionAsync(settings, progress);
        try
        {
            run = await RunCoreAsync(settings, progress, ct, overrides, RestoreOnce, table => tableToRemember = table);
            return run;
        }
        finally
        {
            ColumnRestorationResult restoration;
            try { restoration = await RestoreOnce(); }
            finally { if (tableToRemember is not null) settings.RememberTable(tableToRemember); }
            if (run is not null)
            {
                ColumnRestorationReceipt.Apply(run.Data, restoration);
                ct.ThrowIfCancellationRequested();
                if (!restoration.Pending) PhaseChanged?.Invoke(AutomaticRunPhase.Ready);
            }
        }
    }

    private async Task<AutomaticRunResult> RunCoreAsync(AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct,
        IReadOnlyList<CityTicketOverride>? overrides, Func<Task<ColumnRestorationResult>> restoreColumns, Action<TableChoice> rememberBaseline)
    {
        var clock = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        var runId = Guid.NewGuid().ToString("N")[..8];
        var divergences = new NativeCellDivergenceReport(runId, startedAt);
        var phaseSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        string activePhase = ""; var phaseStarted = TimeSpan.Zero;
        void Stage(string message, AutomaticRunPhase? phase = null)
        {
            ct.ThrowIfCancellationRequested();
            if (phase.HasValue)
            {
                var key = phase.Value.ToString();
                if (key != activePhase) { if (activePhase.Length > 0) phaseSeconds[activePhase] = phaseSeconds.GetValueOrDefault(activePhase) + (clock.Elapsed - phaseStarted).TotalSeconds; activePhase = key; phaseStarted = clock.Elapsed; }
                PhaseChanged?.Invoke(phase.Value);
            }
            Log?.Invoke($"AUTO {runId} +{clock.Elapsed.TotalSeconds:F1}s · {message}");
            progress?.Report(new(0, 0, null, message));
            ct.ThrowIfCancellationRequested();
        }
        async Task<ExportResult> WriteExportAsync(Func<ExportResult> write)
        {
            ct.ThrowIfCancellationRequested();
            // A write already started is atomic and finishes before observing
            // cancellation. Keep that valid file, but never publish Ready for it.
            var exported = await Task.Run(write, ct);
            ct.ThrowIfCancellationRequested();
            return exported;
        }
        Stage("Vérification de la cible, des limites et du dossier d’export…", AutomaticRunPhase.Connection);
        ExtractionVerificationService.Preflight(settings, ct);
        Log?.Invoke("PRÉVOL · adresse, limites et écriture du dossier d’export vérifiées.");
        Stage("Ouverture de la recherche et attente du SSO…", AutomaticRunPhase.Connection);
        var navigation = settings.Copy();
        navigation.PortalUrl = settings.AutoMode == "pagination" ? TargetUrlPolicy.FirstPage(settings.PortalUrl) : settings.PortalUrl;
        var page = await browser.OpenTargetAsync(navigation, ct);
        if (browser.ResolvedTargetUrl.Length > 0) navigation.PortalUrl = browser.ResolvedTargetUrl;
        var searchVerified = TargetUrlPolicy.IncludesSearch(page.Url, navigation.PortalUrl);
        Stage("Recherche du tableau mémorisé…", AutomaticRunPhase.Search);
        var table = await browser.WaitForAutoTableAsync(page, navigation, ct);
        var tableToRemember = SnapshotTableSelection(table);
        rememberBaseline(tableToRemember);
        var preparation = await browser.PrepareColumnsAsync(table, settings, progress, ct);
        table = preparation.Table;
        // Native export can navigate its page to a report/download controller.
        // Keep the source of the verified table, not that transient destination.
        var observedTableSource = AppPaths.SafeUrl(table.Frame?.Url ?? table.Page?.Url ?? page.Url);
        var scopeEvidence = await browser.CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
        var initialScopeVerified = searchVerified && scopeEvidence.Known && scopeEvidence.Fingerprint.Length > 0;
        var observedSort = await browser.ReadObservedSortAsync(table, ct);
        Log?.Invoke($"PÉRIMÈTRE · critères observables={scopeEvidence.Known} ; empreinte conservée, valeurs absentes du journal.");
        settings.RememberTable(table);
        ExtractionResult data;
        string? nativePath = null;
        string? excelPath = null;
        string primaryPath;
        string method = "empty", prefix = "GLPI";
        var fallbackUsed = false;
        var empty = await browser.ReadEmptySearchAsync(table, settings, ct);
        var visibleBefore = empty == null ? await browser.ReadVerificationSampleAsync(table, ct) : null;
        if (settings.ExpectedTotal.HasValue && visibleBefore?.ExpectedTotal is int observedTotal && observedTotal != settings.ExpectedTotal)
            throw new InvalidOperationException("Le total configuré ne correspond pas à celui de GLPI. Corrigez le total attendu avant de lancer l’extraction.");
        if (visibleBefore?.ExpectedTotal > settings.MaxRows)
            throw new InvalidOperationException("Le nombre de résultats annoncé par GLPI dépasse la limite de lignes configurée. Augmentez cette limite ou réduisez la recherche avant l’extraction.");

        async Task<ExtractionResult> ReadPaginationAsync(bool fallback)
        {
            if (fallback)
            {
                Stage("L’export classique n’est pas disponible pour cette collecte. La lecture continue en mode parse du tableau…", AutomaticRunPhase.Retrieval);
                var pagination = settings.Copy(); pagination.AutoMode = "pagination";
                pagination.PortalUrl = TargetUrlPolicy.FirstPage(navigation.PortalUrl);
                page = await browser.OpenTargetAsync(pagination, ct);
                table = await browser.WaitForAutoTableAsync(page, pagination, ct);
                var resumedScope = await browser.CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
                if (!scopeEvidence.Known || !resumedScope.Known || scopeEvidence.Fingerprint.Length == 0
                    || scopeEvidence.Fingerprint != resumedScope.Fingerprint
                    || !TargetUrlPolicy.IncludesSearch(page.Url, pagination.PortalUrl))
                    throw new InvalidOperationException("Le périmètre a changé lors du retour à la première page. Le repli paginé s’arrête avant de lire une autre recherche.");
                var resumedSample = await browser.ReadVerificationSampleAsync(table, ct);
                if (visibleBefore is not null && !ColumnPreparationPolicy.PreservesHeaders(visibleBefore.Headers, resumedSample.Headers))
                    throw new InvalidOperationException("Les colonnes de la recherche initiale ne sont plus toutes présentes. La lecture du tableau s’arrête avant de publier un relevé incomplet.");
                scopeEvidence = resumedScope;
                searchVerified = TargetUrlPolicy.IncludesSearch(page.Url, pagination.PortalUrl);
                settings.RememberTable(table); fallbackUsed = true;
            }
            else Stage("Lecture depuis la première page de la recherche…", AutomaticRunPhase.Retrieval);
            var paginationPreparation = await browser.PrepareNativeTicketPageLimitAsync(table, settings, progress, ct);
            table = paginationPreparation.Table;
            var preparedScope = await browser.CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
            if (scopeEvidence.Fingerprint.Length > 0 && scopeEvidence.Fingerprint != preparedScope.Fingerprint)
                throw new InvalidOperationException("Le périmètre a changé pendant la préparation de la pagination. Le repli s’arrête avant de lire une autre recherche.");
            method = fallback ? "pagination-fallback" : "pagination";
            var result = await browser.ExtractAsync(table, true, settings, progress, ct);
            ct.ThrowIfCancellationRequested();
            result.Scope = "Recherche rechargée avec start=0, puis pages suivantes";
            if (result.Headers.Count == 0) throw new InvalidOperationException("L’extraction n’a produit aucune colonne exploitable.");
            if (fallback && visibleBefore is not null)
            {
                if (!ColumnPreparationPolicy.PreservesHeaders(visibleBefore.Headers, result.Headers))
                    throw new InvalidDataException("Les colonnes de la recherche initiale ne sont plus toutes présentes. Aucun relevé complet n’a été publié.");
                result.Quality = new()
                {
                    SourceRows = result.Rows.Count, ExpectedRows = result.ExpectedTotal, AllPagesRequested = true,
                    RowCountVerified = result.ExpectedTotal.HasValue ? result.Rows.Count == result.ExpectedTotal.Value : null
                };
                var sample = ExtractionVerificationService.VerifyNativeSample(visibleBefore, result, settings.Dashboard, ct);
                result.Quality.InitialSample = sample.Observation();
                if (sample.IdentityVerified != true)
                    throw new InvalidDataException("Les identifiants de la recherche initiale ne sont pas confirmés dans la lecture du tableau. Aucun relevé complet n’a été publié.");
                if (!sample.Matches || !sample.Available)
                {
                    result.Complete = false;
                    result.Warnings.Add("Les valeurs de la recherche initiale et du tableau lu ne concordent pas entièrement. Le relevé reste à vérifier.");
                }
            }
            if (fallback) result.Status += " · mode parse du tableau";
            return result;
        }
        if (empty != null)
        {
            data = empty;
            prefix = "GLPI-0-resultat";
        }
        else if (settings.AutoMode == "pagination")
        {
            data = await ReadPaginationAsync(false);
        }
        else
        {
            var native = await browser.InspectNativeExportAsync(table, ct);
            if (settings.NativeSubmitSelector.Length == 0 && native.Unavailable && native.DisplayPlan != null)
            {
                if (!settings.Automation.AutomaticPaginationFallback)
                    throw new InvalidOperationException("L’export natif est indisponible. Active le repli par pages pour poursuivre en mode parse du tableau, ou réduis la recherche.");
                Log?.Invoke("AFFICHAGE · préparation de la pagination native avant la lecture de secours ; la limite réellement appliquée sera vérifiée.");
                data = await ReadPaginationAsync(true);
            }
            else
            {
                Stage("GLPI : sélection de Toutes les pages en CSV…", AutomaticRunPhase.Retrieval);
                NativeDownloadResult? download = null;
                try
                {
                    download = await browser.ExportAllNativeAsync(table, settings, ct, progress);
                    nativePath = download.Path;
                    NativeFileSaved?.Invoke(nativePath);
                    Log?.Invoke("EXPORT · fichier natif conservé dans le dossier des exports, avant l’import.");
                    Stage("Chargement du fichier reçu et contrôle du périmètre…", AutomaticRunPhase.Validation);
                    try
                    {
                        data = await Task.Run(() => NativeCsvImportService.Read(download.Path, observedTableSource, download.ExpectedTotal,
                            settings.MaxRows, true, ct, diagnostics: message => Log?.Invoke(message)), ct);
                        method = "native-csv";
                    }
                    catch (InvalidDataException) when (settings.Automation.AutomaticPaginationFallback && !ct.IsCancellationRequested)
                    {
                        // Keep the received bytes as evidence. The replacement
                        // comes from a fresh, independently verified search and
                        // is written to another file; no CSV rows are repaired.
                        Log?.Invoke("EXPORT · reçu non exploitable conservé intact ; poursuite par lecture du tableau.");
                        data = await ReadPaginationAsync(true);
                    }
                    if (!fallbackUsed)
                    {
                        var check = ExtractionVerificationService.VerifyNativeSample(visibleBefore!, data, settings.Dashboard, ct,
                            cell => divergences.Add("initial", cell));
                        data.Quality!.InitialSample = check.Observation();
                        Log?.Invoke("ÉCHANTILLON · " + check.Explanation);
                        foreach (var diagnostic in check.Diagnostics) Log?.Invoke("ÉCHANTILLON DÉTAIL · " + diagnostic);
                        // An unusable native receipt is never repaired or certified.
                        // A separate read can replace it only after the original
                        // search and its sampled identities have been revalidated.
                        if (settings.Automation.AutomaticPaginationFallback
                            && (check.Missing > 0 || data.Quality.RowCountVerified == false))
                        {
                            Log?.Invoke("EXPORT · le reçu classique ne confirme pas la recherche attendue ; poursuite par lecture du tableau, fichier reçu conservé.");
                            data = await ReadPaginationAsync(true);
                        }
                        else if (check.Missing > 0)
                            throw new InvalidDataException(check.Explanation + " Le fichier natif reste conservé ; aucun relevé complet n’a été publié.");
                        else if (!check.Available || !check.Matches)
                        {
                            data.Complete = false;
                            data.Warnings.Add(check.Explanation + " Le CSV est chargé ; sa concordance avec l’affichage reste à vérifier.");
                            data.Status = $"Import terminé · {data.Rows.Count:N0} lignes chargées";
                        }
                    }
                }
                catch (NativeExportRecoverableException error) when (download == null && settings.Automation.AutomaticPaginationFallback
                    && !ct.IsCancellationRequested)
                {
                    // Only a classified export failure reaches this branch after
                    // its transfer has been retired. Session, origin, identity,
                    // storage and cancellation failures retain their own outcome.
                    Log?.Invoke("EXPORT · basculement automatique vers la lecture du tableau ; motif=" + error.Kind + ".");
                    data = await ReadPaginationAsync(true);
                }
            }
        }
        Stage("Vérification finale de la recherche et du contexte GLPI…", AutomaticRunPhase.Validation);
        BrowserScopeEvidence afterScope;
        TableSnapshot? visibleAfter = null;
        try
        {
            afterScope = await browser.CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
            if (empty == null) visibleAfter = await browser.ReadVerificationSampleAsync(table, ct);
        }
        catch (Exception error) when (error is PlaywrightException or InvalidOperationException or TimeoutException)
        {
            // Pagination may already have preserved useful pages when the tab
            // disappears or the session expires. Keep those rows explicitly
            // partial instead of losing them during the final recheck.
            afterScope = new("", false);
            data.Warnings.Add("Le contrôle final du tableau est indisponible. Les lignes déjà lues sont conservées avec une couverture non confirmée. " + AppPaths.SafeError(error));
        }
        var stableScope = scopeEvidence.Fingerprint.Length > 0 && scopeEvidence.Fingerprint == afterScope.Fingerprint;
        var referenceTotal = visibleBefore?.ExpectedTotal ?? data.ExpectedTotal;
        var totalChanged = referenceTotal is int initialTotal && visibleAfter?.ExpectedTotal is int finalTotal && initialTotal != finalTotal;
        if (data.Quality is { } quality)
        {
            quality.FinalTotalVerified = referenceTotal.HasValue && visibleAfter?.ExpectedTotal is int observedFinal
                ? referenceTotal.Value == observedFinal : null;
            quality.ScopeStable = stableScope;
        }
        if (!stableScope)
        {
            data.Complete = false;
            data.Status = "Extraction à vérifier — périmètre modifié ou non contrôlable";
            data.Warnings.Add("Le contexte de recherche n’est pas resté vérifiable jusqu’à la fin. Aucun relevé complet ni historique comparable n’est certifié pour cette exécution.");
        }
        if (visibleAfter != null)
        {
            if (totalChanged)
            {
                data.Complete = false;
                var observedInitially = visibleBefore?.ExpectedTotal.HasValue == true;
                data.Status = observedInitially ? "Extraction à vérifier — total GLPI modifié" : "Extraction à vérifier — total GLPI différent du total attendu";
                data.Warnings.Add((observedInitially ? "Le total GLPI a changé entre le début et la fin de l’extraction."
                    : "Le total GLPI final diffère du total attendu utilisé pour cette extraction.") + " Les données sont conservées avec une couverture non confirmée.");
            }
            if (method is "native-csv" or "pagination-fallback")
            {
                var finalSample = ExtractionVerificationService.VerifyNativeSample(visibleAfter, data, settings.Dashboard, ct,
                    cell => divergences.Add("final", cell));
                data.Quality!.FinalSample = finalSample.Observation();
                Log?.Invoke("ÉCHANTILLON FINAL · " + finalSample.Explanation);
                foreach (var diagnostic in finalSample.Diagnostics) Log?.Invoke("ÉCHANTILLON FINAL DÉTAIL · " + diagnostic);
                if (!finalSample.Available)
                {
                    data.Complete = false;
                    if (stableScope && !totalChanged) data.Status = $"Lecture terminée · {data.Rows.Count:N0} lignes chargées";
                    data.Warnings.Add(finalSample.Explanation + " Le contrôle de l’échantillon final n’est plus disponible ; la couverture reste à vérifier.");
                }
                else if (!finalSample.Matches)
                {
                    data.Complete = false;
                    if (stableScope && !totalChanged) data.Status = finalSample.Missing > 0
                        ? "Relevé chargé — identifiants de l’échantillon final à vérifier"
                        : $"Lecture terminée · {data.Rows.Count:N0} lignes chargées";
                    var warning = finalSample.Explanation + " Le relevé est chargé ; sa concordance avec l’affichage reste à vérifier.";
                    if (!data.Warnings.Contains(warning, StringComparer.Ordinal)) data.Warnings.Add(warning);
                }
            }
        }
        Log?.Invoke($"PÉRIMÈTRE · contrôle avant/après={(stableScope ? "identique" : "non confirmé")} ; méthode={method} ; repli={(fallbackUsed ? "oui" : "non")}.");
        data.Provenance = new()
        {
            RunId = runId, RequestedUrl = TargetUrlPolicy.PersistedTarget(settings.PortalUrl),
            StartedAt = startedAt, FinishedAt = DateTimeOffset.Now, SearchVerified = searchVerified,
            SourceKind = "browser", Method = method,
            InitialScopeVerified = initialScopeVerified,
            ScopeVerified = stableScope && scopeEvidence.Known && afterScope.Known, ScopeFingerprint = scopeEvidence.Fingerprint,
            OriginalFileName = nativePath == null || method != "native-csv" ? "" : Path.GetFileName(nativePath),
            ObservedSort = observedSort, Resumed = data.Provenance.Resumed, ResumeStartedAt = data.Provenance.ResumeStartedAt,
            StageSeconds = phaseSeconds.Concat(browser.ConnectionStageSeconds.Select(pair => new KeyValuePair<string, double>("SSO/" + pair.Key, pair.Value))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        ColumnPreparationPolicy.ApplyReceipt(data, preparation.Receipt);
        ColumnRestorationReceipt.Apply(data, await restoreColumns());
        ct.ThrowIfCancellationRequested();
        if (divergences.Count > 0 && data.Quality is { } observedQuality)
        {
            try
            {
                observedQuality.DivergenceReportName = divergences.Save(AppPaths.CellDivergenceFolder);
                observedQuality.DivergenceReportCount = divergences.Count;
                observedQuality.DivergenceTotalCount = divergences.TotalCount;
                Log?.Invoke($"ÉCARTS · diagnostic local enregistré : {observedQuality.DivergenceReportName} ; {divergences.Count}/{divergences.TotalCount} comparaisons détaillées. Accessible dans Qualité du relevé ; les valeurs restent dans ce fichier dédié.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // A local diagnostic write must not discard an otherwise usable import.
                data.Warnings.Add("Le diagnostic détaillé des écarts n’a pas pu être enregistré. Les données importées restent disponibles.");
                Log?.Invoke("ÉCARTS · écriture du diagnostic local indisponible ; contenu de cellule absent du journal.");
            }
        }
        data.Warnings = data.Warnings.Distinct(StringComparer.Ordinal).ToList();
        if (nativePath != null && method == "native-csv") primaryPath = nativePath;
        else if (data.Headers.Count > 0)
        {
            Stage("Enregistrement du relevé contrôlé…", AutomaticRunPhase.Saving);
            primaryPath = NewPath(settings.ExportFolder, data.Complete ? prefix : "GLPI-a-verifier", ".csv");
            await WriteExportAsync(() => ExportService.WriteCsv(data, primaryPath, ct));
        }
        else
        {
            Stage("Enregistrement de la recherche vide confirmée…", AutomaticRunPhase.Saving);
            primaryPath = NewPath(settings.ExportFolder, prefix, ".txt");
            await File.WriteAllTextAsync(primaryPath, $"GLPI Pilot {ConfigurationTransferService.ApplicationVersion}\n{data.Status}\nDate : {DateTimeOffset.Now:O}\nSource : {data.SourceUrl}\nGLPI ne fournit pas d’en-têtes pour cette recherche vide.\n", ct);
            ct.ThrowIfCancellationRequested();
        }
        PhaseChanged?.Invoke(AutomaticRunPhase.Validation);
        foreach (var diagnostic in CityDetectionService.DescribeSourceAvailability(data, settings.Cities, settings.Dashboard, ct))
            Log?.Invoke("VILLES SOURCES · " + diagnostic);
        var cities = await Task.Run(() => CityDetectionService.Enrich(data, settings.Cities, settings.Dashboard, ct, overrides), ct);
        data = cities.Data;
        if (cities.Applied)
        {
            Stage("Repérage des villes et enregistrement du CSV enrichi…", AutomaticRunPhase.Saving);
            var enrichedPath = NewPath(settings.ExportFolder, "GLPI-villes", ".csv");
            ct.ThrowIfCancellationRequested();
            await WriteExportAsync(() => ExportService.WriteCsv(data, enrichedPath, ct));
            primaryPath = enrichedPath;
            Log?.Invoke($"VILLES · reconnues={cities.MatchedCount}, sans correspondance={cities.UnmatchedCount}, ambiguës={cities.AmbiguousCount}, titre indisponible={cities.TitleUnavailableCount} ; fichier natif conservé intact.");
        }
        foreach (var warning in data.Warnings) Log?.Invoke("CONTRÔLE · " + warning);
        static string Observed(bool? value) => value switch { true => "oui", false => "non", _ => "non confirmé" };
        var columnReceipt = data.Provenance.ColumnPreparation;
        var viewRestored = columnReceipt is null || !columnReceipt.TemporaryView
            ? (bool?)null : !columnReceipt.RecoveryPending && columnReceipt.RestorationState is "restored" or "restored-preserving-changes";
        Log?.Invoke($"CONTRÔLE · {data.Rows.Count} lignes chargées, {data.Headers.Count} colonnes ; total attendu={data.ExpectedTotal?.ToString() ?? "inconnu"} ; réception complète={Observed(data.Quality?.ReceptionComplete)} ; périmètre stable={Observed(data.Quality?.ScopeStable)} ; couverture vérifiée={Observed(data.Complete)} ; vue restaurée={Observed(viewRestored)}.");
        ct.ThrowIfCancellationRequested();
        if (settings.AutoSaveXlsx && data.Headers.Count > 0)
        {
            Stage("Enregistrement automatique du classeur Excel…", AutomaticRunPhase.Saving);
            excelPath = NewPath(settings.ExportFolder, data.Complete ? "GLPI" : "GLPI-a-verifier", ".xlsx");
            var exported = await WriteExportAsync(() => ExportService.WriteXlsx(data, excelPath, ct));
            if (exported.TruncatedCells > 0) Log?.Invoke($"EXCEL · {exported.TruncatedCells} cellules trop longues pour Excel ; détail dans la feuille Informations, CSV conservé.");
            primaryPath = excelPath;
        }
        ct.ThrowIfCancellationRequested();
        Log?.Invoke($"AUTO {runId} +{clock.Elapsed.TotalSeconds:F1}s · terminé, {data.Rows.Count:N0} lignes, fichiers enregistrés.");
        if (activePhase.Length > 0) phaseSeconds[activePhase] = phaseSeconds.GetValueOrDefault(activePhase) + (clock.Elapsed - phaseStarted).TotalSeconds;
        data.Provenance.StageSeconds = phaseSeconds.Concat(browser.ConnectionStageSeconds.Select(pair => new KeyValuePair<string, double>("SSO/" + pair.Key, pair.Value))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        data.Provenance.FinishedAt = DateTimeOffset.Now;
        return new(data, table, primaryPath, nativePath, excelPath, cities) { TableToRemember = tableToRemember };
    }

    internal static TableChoice SnapshotTableSelection(TableChoice table) => new()
    {
        Key = table.Key, Label = table.Label, Selector = table.Selector,
        Headers = table.Headers.ToList(), RowCount = table.RowCount, ColumnCount = table.ColumnCount,
        HeaderFingerprint = table.HeaderFingerprint, Frame = table.Frame, Page = table.Page,
        FrameOrigin = table.FrameOrigin, FrameName = table.FrameName, TopOrigin = table.TopOrigin
    };

    private static string NewPath(string folder, string prefix, string extension) =>
        Path.Combine(folder, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension}");

}
