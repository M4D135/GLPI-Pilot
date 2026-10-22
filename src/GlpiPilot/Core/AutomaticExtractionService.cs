using System.Diagnostics;
using System.IO;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed class AutomaticExtractionService(BrowserService browser)
{
    public event Action<string>? Log;
    public event Action<string>? NativeFileSaved;
    public event Action<AutomaticRunPhase>? PhaseChanged;

    public async Task<AutomaticRunResult> RunAsync(AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        var runId = Guid.NewGuid().ToString("N")[..8];
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
        var searchVerified = TargetUrlPolicy.IncludesSearch(page.Url, navigation.PortalUrl);
        Stage("Recherche du tableau mémorisé…", AutomaticRunPhase.Search);
        var table = await browser.WaitForAutoTableAsync(page, settings, ct);
        var scopeEvidence = await browser.CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
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
                Stage("Export natif indisponible : retour à la première page pour lire toute la recherche…", AutomaticRunPhase.Retrieval);
                var pagination = settings.Copy(); pagination.AutoMode = "pagination";
                pagination.PortalUrl = TargetUrlPolicy.FirstPage(settings.PortalUrl);
                page = await browser.OpenTargetAsync(pagination, ct);
                table = await browser.WaitForAutoTableAsync(page, pagination, ct);
                var resumedScope = await browser.CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
                if (scopeEvidence.Fingerprint.Length > 0 && scopeEvidence.Fingerprint != resumedScope.Fingerprint)
                    throw new InvalidOperationException("Le périmètre a changé lors du retour à la première page. Le repli paginé s’arrête avant de lire une autre recherche.");
                scopeEvidence = resumedScope;
                searchVerified = TargetUrlPolicy.IncludesSearch(page.Url, pagination.PortalUrl);
                settings.RememberTable(table); fallbackUsed = true;
            }
            else Stage("Lecture depuis la première page de la recherche…", AutomaticRunPhase.Retrieval);
            method = fallback ? "pagination-fallback" : "pagination";
            var result = await browser.ExtractAsync(table, true, settings, progress, ct);
            result.Scope = "Recherche rechargée avec start=0, puis pages suivantes";
            if (result.Headers.Count == 0) throw new InvalidOperationException("L’extraction n’a produit aucune colonne exploitable.");
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
                Stage("Export non reconnu : GLPI affiche automatiquement toute la liste…", AutomaticRunPhase.Retrieval);
                (data, table) = await browser.ReadAllDisplayedAsync(table, settings, progress, ct);
                settings.RememberTable(table);
                method = "display-all"; prefix = "GLPI-liste-complete";
            }
            else
            {
                Stage("GLPI : sélection de Toutes les pages en CSV…", AutomaticRunPhase.Retrieval);
                NativeDownloadResult? download = null;
                try
                {
                    download = await browser.ExportAllNativeAsync(table, settings, ct);
                    nativePath = download.Path;
                    NativeFileSaved?.Invoke(nativePath);
                    Log?.Invoke("EXPORT · fichier natif conservé dans le dossier des exports, avant l’import.");
                    Stage("Chargement du fichier reçu et contrôle du périmètre…", AutomaticRunPhase.Validation);
                    data = await Task.Run(() => NativeCsvImportService.Read(download.Path, AppPaths.SafeUrl(page.Url), download.ExpectedTotal,
                        settings.MaxRows, true, ct, diagnostics: message => Log?.Invoke(message)), ct);
                    method = "native-csv";
                    var check = ExtractionVerificationService.VerifyNativeSample(visibleBefore!, data, settings.Dashboard, ct);
                    Log?.Invoke("ÉCHANTILLON · " + check.Explanation);
                    if (check.Available && !check.Matches)
                        throw new InvalidDataException(check.Explanation + " Le fichier natif reste conservé ; aucun relevé complet n’a été publié.");
                    if (!check.Available)
                    {
                        data.Complete = false;
                        data.Warnings.Add(check.Explanation + " Le comptage est disponible, mais le lien entre le fichier et les lignes visibles n’a pas pu être vérifié.");
                        data.Status = "Export natif chargé — identité du périmètre à vérifier";
                    }
                }
                catch (NativeExportUnavailableException) when (download == null && settings.Automation.AutomaticPaginationFallback
                    && settings.NativeSubmitSelector.Length == 0 && settings.NativeMenuSelector.Length == 0
                    && settings.NativeFormatSelector.Length == 0 && settings.NativeScopeSelector.Length == 0 && !ct.IsCancellationRequested)
                {
                    // A transfer failure, malformed CSV, wrong sample or partial receipt
                    // never enters this branch. Only absence of a native command does.
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
        if (!stableScope)
        {
            data.Complete = false;
            data.Status = "Extraction à vérifier — périmètre modifié ou non contrôlable";
            data.Warnings.Add("Le contexte de recherche n’est pas resté vérifiable jusqu’à la fin. Aucun relevé complet ni historique comparable n’est certifié pour cette exécution.");
        }
        if (visibleAfter != null)
        {
            if (visibleBefore?.ExpectedTotal is int initialTotal && visibleAfter.ExpectedTotal is int finalTotal && initialTotal != finalTotal)
            {
                data.Complete = false; data.Status = "Extraction à vérifier — total GLPI modifié";
                data.Warnings.Add("Le total GLPI a changé entre le début et la fin de l’extraction. Les données sont conservées avec une couverture non confirmée.");
            }
            if (method == "native-csv")
            {
                var finalSample = ExtractionVerificationService.VerifyNativeSample(visibleAfter, data, settings.Dashboard, ct);
                if (finalSample.Available && !finalSample.Matches)
                {
                    data.Complete = false; data.Status = "Export reçu — liste visible modifiée pendant l’opération";
                    data.Warnings.Add("L’échantillon visible après le transfert ne correspond plus entièrement au fichier reçu. La recherche a pu évoluer pendant le téléchargement ; la couverture reste à vérifier.");
                }
            }
        }
        Log?.Invoke($"PÉRIMÈTRE · contrôle avant/après={(stableScope ? "identique" : "non confirmé")} ; méthode={method} ; repli={(fallbackUsed ? "oui" : "non")}.");
        data.Provenance = new()
        {
            RunId = runId, RequestedUrl = TargetUrlPolicy.PersistedTarget(settings.PortalUrl),
            StartedAt = startedAt, FinishedAt = DateTimeOffset.Now, SearchVerified = searchVerified,
            SourceKind = "browser", Method = method,
            ScopeVerified = stableScope && scopeEvidence.Known && afterScope.Known, ScopeFingerprint = scopeEvidence.Fingerprint,
            OriginalFileName = nativePath == null ? "" : Path.GetFileName(nativePath),
            ObservedSort = observedSort, Resumed = data.Provenance.Resumed, ResumeStartedAt = data.Provenance.ResumeStartedAt,
            StageSeconds = phaseSeconds.Concat(browser.ConnectionStageSeconds.Select(pair => new KeyValuePair<string, double>("SSO/" + pair.Key, pair.Value))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        if (nativePath != null) primaryPath = nativePath;
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
        var cities = await Task.Run(() => CityDetectionService.Enrich(data, settings.Cities, settings.Dashboard, ct), ct);
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
        Log?.Invoke($"CONTRÔLE · {data.Rows.Count} lignes chargées, {data.Headers.Count} colonnes ; total attendu={data.ExpectedTotal?.ToString() ?? "inconnu"}, conforme={data.Complete}.");
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
        PhaseChanged?.Invoke(AutomaticRunPhase.Ready);
        return new(data, table, primaryPath, nativePath, excelPath, cities);
    }

    private static string NewPath(string folder, string prefix, string extension) =>
        Path.Combine(folder, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension}");

}
