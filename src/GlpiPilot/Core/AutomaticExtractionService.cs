using System.Diagnostics;
using System.IO;

namespace GlpiPilot.Core;

public sealed class AutomaticExtractionService(BrowserService browser)
{
    public event Action<string>? Log;
    public event Action<string>? NativeFileSaved;
    public event Action<AutomaticRunPhase>? PhaseChanged;

    public async Task<AutomaticRunResult> RunAsync(AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString("N")[..8];
        void Stage(string message, AutomaticRunPhase? phase = null)
        {
            ct.ThrowIfCancellationRequested();
            if (phase.HasValue) PhaseChanged?.Invoke(phase.Value);
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
        Stage("Ouverture de la recherche et attente du SSO…", AutomaticRunPhase.Connection);
        var navigation = settings.Copy();
        navigation.PortalUrl = settings.AutoMode == "pagination" ? TargetUrlPolicy.FirstPage(settings.PortalUrl) : settings.PortalUrl;
        var page = await browser.OpenTargetAsync(navigation, ct);
        Stage("Recherche du tableau mémorisé…", AutomaticRunPhase.Search);
        var table = await browser.WaitForAutoTableAsync(page, settings, ct);
        settings.RememberTable(table);
        Directory.CreateDirectory(settings.ExportFolder);
        ExtractionResult data;
        string? nativePath = null;
        string? excelPath = null;
        string primaryPath;
        var empty = await browser.ReadEmptySearchAsync(table, settings, ct);
        if (empty != null)
        {
            data = empty;
            Stage("Recherche vide confirmée : enregistrement du résultat.", AutomaticRunPhase.Saving);
            if (data.Headers.Count > 0)
            {
                primaryPath = NewPath(settings.ExportFolder, "GLPI-0-resultat", ".csv");
                await WriteExportAsync(() => ExportService.WriteCsv(data, primaryPath));
            }
            else
            {
                primaryPath = NewPath(settings.ExportFolder, "GLPI-0-resultat", ".txt");
                await File.WriteAllTextAsync(primaryPath, $"GLPI Pilot {ConfigurationTransferService.ApplicationVersion}\n{data.Status}\nDate : {DateTimeOffset.Now:O}\nSource : {data.SourceUrl}\nGLPI ne fournit pas d’en-têtes pour cette recherche vide.\n", ct);
                ct.ThrowIfCancellationRequested();
            }
        }
        else if (settings.AutoMode == "pagination")
        {
            Stage("Lecture depuis la première page de la recherche…", AutomaticRunPhase.Retrieval);
            data = await browser.ExtractAsync(table, true, settings, progress, ct);
            data.Scope = "Recherche rechargée avec start=0, puis pages suivantes";
            if (data.Headers.Count == 0) throw new InvalidOperationException("L’extraction n’a produit aucune colonne exploitable.");
            var csvPath = NewPath(settings.ExportFolder, data.Complete ? "GLPI" : "GLPI-a-verifier", ".csv");
            await WriteExportAsync(() => ExportService.WriteCsv(data, csvPath));
            primaryPath = csvPath;
        }
        else
        {
            var native = await browser.InspectNativeExportAsync(table, ct);
            if (settings.NativeSubmitSelector.Length == 0 && native.Plans.Count == 0 && native.DisplayPlan != null)
            {
                Stage("Export non reconnu : GLPI affiche automatiquement toute la liste…", AutomaticRunPhase.Retrieval);
                (data, table) = await browser.ReadAllDisplayedAsync(table, settings, progress, ct);
                settings.RememberTable(table);
                primaryPath = NewPath(settings.ExportFolder, "GLPI-liste-complete", ".csv");
                await WriteExportAsync(() => ExportService.WriteCsv(data, primaryPath));
            }
            else
            {
                Stage("GLPI : sélection de Toutes les pages en CSV…", AutomaticRunPhase.Retrieval);
                var download = await browser.ExportAllNativeAsync(table, settings, ct);
                nativePath = download.Path;
                NativeFileSaved?.Invoke(nativePath);
                Log?.Invoke("EXPORT · fichier natif conservé dans le dossier des exports, avant l’import.");
                Stage("Chargement du fichier complet reçu de GLPI…", AutomaticRunPhase.Validation);
                try
                {
                    data = await Task.Run(() => NativeCsvImportService.Read(download.Path, AppPaths.SafeUrl(page.Url), download.ExpectedTotal,
                        settings.MaxRows, true, ct, diagnostics: message => Log?.Invoke(message)), ct);
                    primaryPath = nativePath;
                }
                catch (InvalidDataException ex) when (native.DisplayPlan != null && !ct.IsCancellationRequested)
                {
                    // The native receipt remains intact. Only a verified whole-list
                    // display can recover from an unreadable CSV; do not mask I/O,
                    // cancellation, permissions or a failed/missing download.
                    Log?.Invoke("IMPORT · CSV non exploitable : " + AppPaths.SafeError(ex));
                    Stage("Lecture du CSV impossible : GLPI affiche automatiquement toute la liste…", AutomaticRunPhase.Retrieval);
                    (data, table) = await browser.ReadAllDisplayedAsync(table, settings, progress, ct);
                    settings.RememberTable(table);
                    data.Warnings.Add("Le fichier natif reçu n’a pas pu être lu comme CSV et a été conservé intact. Ce résultat provient de la liste complète affichée dans GLPI, avec vérification du total ; les valeurs correspondent au texte visible.");
                    primaryPath = NewPath(settings.ExportFolder, "GLPI-liste-complete", ".csv");
                    await WriteExportAsync(() => ExportService.WriteCsv(data, primaryPath));
                }
            }
        }
        PhaseChanged?.Invoke(AutomaticRunPhase.Validation);
        var cities = await Task.Run(() => CityDetectionService.Enrich(data, settings.Cities, settings.Dashboard, ct), ct);
        data = cities.Data;
        if (cities.Applied)
        {
            Stage("Repérage des villes et enregistrement du CSV enrichi…", AutomaticRunPhase.Saving);
            var enrichedPath = NewPath(settings.ExportFolder, "GLPI-villes", ".csv");
            ct.ThrowIfCancellationRequested();
            await WriteExportAsync(() => ExportService.WriteCsv(data, enrichedPath));
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
            var exported = await WriteExportAsync(() => ExportService.WriteXlsx(data, excelPath));
            if (exported.TruncatedCells > 0) Log?.Invoke($"EXCEL · {exported.TruncatedCells} cellules trop longues pour Excel ; détail dans la feuille Informations, CSV conservé.");
            primaryPath = excelPath;
        }
        ct.ThrowIfCancellationRequested();
        Log?.Invoke($"AUTO {runId} +{clock.Elapsed.TotalSeconds:F1}s · terminé, {data.Rows.Count:N0} lignes, fichiers enregistrés.");
        PhaseChanged?.Invoke(AutomaticRunPhase.Ready);
        return new(data, table, primaryPath, nativePath, excelPath, cities);
    }

    private static string NewPath(string folder, string prefix, string extension) =>
        Path.Combine(folder, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension}");
}
