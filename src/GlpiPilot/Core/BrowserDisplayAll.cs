using System.Diagnostics;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    public async Task<(ExtractionResult Data, TableChoice Table)> ReadAllDisplayedAsync(
        TableChoice table, AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var inspection = await InspectNativeExportAsync(table, ct);
        var plan = inspection.DisplayPlan ?? throw new InvalidOperationException("Le choix du nombre d’éléments affichés n’a pas été reconnu pour cette recherche.");
        var expected = plan.ExpectedTotal;
        if (expected <= 0 || expected != inspection.ExpectedTotal)
            throw new InvalidOperationException("Le total de la recherche doit être connu avant de demander son affichage complet.");
        if (expected > settings.MaxRows)
            throw new InvalidOperationException($"La recherche contient {expected:N0} lignes, au-delà de la limite de {settings.MaxRows:N0}. Augmente la limite ou réduis la recherche avant l’affichage complet.");
        if (settings.ExpectedTotal.HasValue && settings.ExpectedTotal.Value != expected)
            throw new InvalidOperationException($"Le total configuré ({settings.ExpectedTotal:N0}) ne correspond pas au total annoncé par GLPI ({expected:N0}).");

        if (!plan.StartsAtZero)
        {
            var frame = await ResolveFrameAsync(table, ct);
            Log?.Invoke("AFFICHAGE · retour au début de la recherche avant de modifier le nombre d’éléments.");
            var firstUrl = TargetUrlPolicy.FirstPage(frame.Url);
            if (frame == table.Page!.MainFrame) await NavigateTargetAsync(table.Page, firstUrl, ct);
            else
            {
                ct.ThrowIfCancellationRequested();
                try { await frame.GotoAsync(firstUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 }); }
                catch (TimeoutException) { /* Recheck the resulting table below. */ }
                ct.ThrowIfCancellationRequested();
            }
            table = await WaitForAutoTableAsync(table.Page!, settings, ct);
            inspection = await InspectNativeExportAsync(table, ct);
            plan = inspection.DisplayPlan ?? throw new InvalidOperationException("Le contrôle d’affichage a changé après le retour à la première page.");
            if (!plan.StartsAtZero || inspection.ExpectedTotal != expected || plan.ExpectedTotal != expected)
                throw new InvalidOperationException("Le début ou le total de la recherche a changé ; l’affichage complet ne peut pas être confirmé.");
        }

        var alreadyAll = inspection.RangeStart == 1 && inspection.RangeEnd == expected && table.RowCount == expected;
        if (!alreadyAll)
        {
            if (plan.Kind != "select-change" || plan.Proof != "native-pager-list-limit-submit" ||
                !long.TryParse(plan.Value, out var requested) || requested < expected)
                throw new InvalidOperationException("Le choix Afficher tout n’est pas une option GLPI reconnue et suffisante.");
            var frame = await ResolveFrameAsync(table, ct);
            // Re-inspection proves the current element/form/option association.
            // SelectOption triggers GLPI's own list-limit submission; no endpoint
            // or option is synthesized and no business action is submitted.
            var current = await InspectNativeExportAsync(table, ct);
            if (current.DisplayPlan is not { } fresh || fresh.Selector != plan.Selector ||
                fresh.Value != plan.Value || fresh.FormSelector != plan.FormSelector || fresh.Proof != plan.Proof)
                throw new InvalidOperationException("Le contrôle d’affichage a changé avant l’action. Relance l’extraction.");
            Log?.Invoke($"AFFICHAGE · option existante retenue pour charger les {expected:N0} résultats ; attente du rechargement.");
            ct.ThrowIfCancellationRequested();
            var list = frame.Locator(plan.Selector);
            if (await list.CountAsync() != 1) throw new InvalidOperationException("Le contrôle d’affichage est ambigu.");
            await list.SelectOptionAsync(new SelectOptionValue { Value = plan.Value }, new() { Timeout = 7000, Force = plan.Hidden });
            ct.ThrowIfCancellationRequested();
        }
        else Log?.Invoke($"AFFICHAGE · les {expected:N0} résultats sont déjà affichés sur une seule page.");

        var clock = Stopwatch.StartNew();
        string stableSignature = "", lastState = "";
        var stableSince = TimeSpan.Zero;
        var visibleRows = table.RowCount;
        string problem = "Le rechargement de la liste n’a pas été confirmé.";
        while (clock.Elapsed < TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 2, 60)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                table = await ChooseAutoTableAsync(table.Page!, settings, ct, quiet: true);
                inspection = await InspectNativeExportAsync(table, ct);
                visibleRows = table.RowCount;
                if (inspection.ExpectedTotal.HasValue && inspection.ExpectedTotal != expected)
                    throw new InvalidOperationException("Le total de la recherche a changé pendant le rechargement. Relance l’extraction.");
                var completeRange = inspection.ExpectedTotal == expected && inspection.RangeStart == 1 && inspection.RangeEnd == expected;
                var state = $"{visibleRows}|{inspection.RangeStart}|{inspection.RangeEnd}";
                if (state != lastState)
                {
                    lastState = state;
                    Log?.Invoke($"AFFICHAGE · {visibleRows:N0} lignes visibles sur {expected:N0} attendues ; plage {inspection.RangeStart?.ToString() ?? "?"}–{inspection.RangeEnd?.ToString() ?? "?"}.");
                    progress?.Report(new(0, visibleRows, expected, $"Chargement de toute la liste : {visibleRows:N0}/{expected:N0} lignes"));
                }
                if (completeRange && visibleRows == expected)
                {
                    var (_, snapshot, _) = await ReadAsync(table, "", ct);
                    var next = snapshot.Next;
                    if (snapshot.Rows.Count != expected || snapshot.Headers.Count == 0 || snapshot.Warnings.Count > 0 ||
                        next?.Ambiguous == true || (next is { Disabled: false } && next.Selector.Length > 0))
                    {
                        problem = "La structure du tableau ou la pagination reste incohérente avec l’affichage complet.";
                        stableSignature = "";
                    }
                    else if (stableSignature != snapshot.Signature)
                    { stableSignature = snapshot.Signature; stableSince = clock.Elapsed; }
                    else if (clock.Elapsed - stableSince >= TimeSpan.FromMilliseconds(350))
                    {
                        // Values are retained exactly as displayed, including
                        // duplicates. Completeness comes from range AND count.
                        var result = new ExtractionResult
                        {
                            Headers = snapshot.Headers, Rows = snapshot.Rows, Title = table.Label,
                            SourceUrl = AppPaths.SafeUrl(table.Frame!.Url), Pages = 1,
                            ExpectedTotal = expected, ExtractedAt = DateTimeOffset.Now,
                            Scope = "Toute la recherche affichée dans GLPI, de 1 au total annoncé",
                            Complete = true, Status = "Toute la liste récupérée, total vérifié"
                        };
                        Log?.Invoke($"AFFICHAGE · liste complète vérifiée : {expected:N0}/{expected:N0} lignes, première ligne 1, aucune page suivante.");
                        return (result, table);
                    }
                }
                else
                {
                    stableSignature = "";
                    problem = "GLPI n’a pas affiché toute la recherche après le changement du nombre d’éléments.";
                }
            }
            catch (PlaywrightException) { stableSignature = ""; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ne contient pas de tableau", StringComparison.Ordinal) || ex.Message.Contains("tableau a changé", StringComparison.Ordinal))
            { stableSignature = ""; }
            await Task.Delay(400, ct);
        }
        LogNativeInspection(inspection);
        throw new InvalidOperationException($"{problem} {visibleRows:N0}/{expected:N0} lignes observées. Aucun export complet depuis la liste affichée n’a été créé ; copie le diagnostic.");
    }
}
