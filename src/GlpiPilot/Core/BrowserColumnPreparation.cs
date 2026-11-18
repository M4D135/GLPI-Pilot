using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private string? _columnScript;

    private async Task EnsureColumnReaderAsync(IFrame frame, string origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (frame.IsDetached || Origin(frame.Url) != origin)
            throw new InvalidOperationException("L’origine de la fenêtre des colonnes a changé. La préparation s’arrête avant toute autre action.");
        if (_columnScript is null)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("BrowserScripts.native-columns.js", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            _columnScript = reader.ReadToEnd();
        }
        await frame.EvaluateAsync(_columnScript).WaitAsync(ct);
    }

    private async Task<NativeColumnInspection> InspectColumnsAsync(IFrame frame, string origin, string selector, CancellationToken ct)
    {
        await EnsureColumnReaderAsync(frame, origin, ct);
        return await ReadJsonAsync<NativeColumnInspection>(frame, "selector => window.__glpiPilotColumns.inspect(selector)", selector, ct);
    }

    private async Task<NativeColumnPreferences> InspectColumnPreferencesAsync(IFrame frame, string origin, CancellationToken ct)
    {
        await EnsureColumnReaderAsync(frame, origin, ct);
        return await ReadJsonAsync<NativeColumnPreferences>(frame, "() => window.__glpiPilotColumns.inspectPreferences()", null, ct);
    }

    private static bool PersonalColumnScope(NativeColumnPreferences preferences) => preferences.Supported && preferences.PersonalScope
        && preferences.ItemType == "Ticket" && long.TryParse(preferences.UsersId, out var id) && id > 0;

    private static string ColumnIdentity(NativeSelectedColumn column) => column.Value.Length > 0 ? column.Value
        : column.PreferenceId + "|" + DashboardService.Normalize(column.Label);

    private static bool SelectedColumnsPreserved(NativeColumnPreferences before, NativeColumnPreferences after) =>
        after.Selected.Count >= before.Selected.Count && before.Selected.Select(ColumnIdentity)
            .SequenceEqual(after.Selected.Take(before.Selected.Count).Select(ColumnIdentity), StringComparer.Ordinal);

    private static bool SamePersonalColumnView(NativeColumnPreferences before, NativeColumnPreferences after) =>
        PersonalColumnScope(after) && after.UsersId == before.UsersId && after.Layout == before.Layout
        && after.PersonalActive == before.PersonalActive && SelectedColumnsPreserved(before, after);

    private static bool SameColumnOption(NativeColumnOption expected, NativeColumnOption observed) =>
        expected.Value == observed.Value && DashboardService.Normalize(ColumnPreparationPolicy.Header(expected))
            == DashboardService.Normalize(ColumnPreparationPolicy.Header(observed));

    // This exception is restricted to failures before the native add/activate
    // click. A response or persistence failure must never enter the fallback.
    private sealed class ColumnOptionUnavailableException(string message) : InvalidOperationException(message);
    private sealed record ColumnCommandResult(NativeColumnPreferences Preferences, bool Added);

    private void ValidatePersonalColumnView(NativeColumnPreferences before, NativeColumnPreferences fresh)
    {
        if (!SamePersonalColumnView(before, fresh))
            throw new InvalidOperationException("La vue personnelle des colonnes a changé avant l’ajout. Aucune autre commande n’est exécutée.");
        if (fresh.FormSelector != before.FormSelector || fresh.SelectSelector != before.SelectSelector
            || fresh.AddSelector != before.AddSelector || fresh.ActivateSelector != before.ActivateSelector)
            Log?.Invoke("COLONNES · repères natifs renouvelés ; la même vue personnelle a été revalidée.");
    }

    private static async Task ClickColumnControlAsync(IFrame frame, string origin, string selector, CancellationToken ct, bool optionalCommand = false)
    {
        ct.ThrowIfCancellationRequested();
        if (frame.IsDetached || Origin(frame.Url) != origin) throw new InvalidOperationException("Le contexte des colonnes a changé avant l’action native.");
        var control = frame.Locator(selector);
        if (selector.Length == 0 || await control.CountAsync() != 1 || !await control.IsVisibleAsync() || !await control.IsEnabledAsync())
        {
            if (optionalCommand) throw new ColumnOptionUnavailableException("La commande personnelle a disparu avant le clic ; aucun ajout n’a été envoyé pour cette option.");
            throw new InvalidOperationException("La commande native des colonnes n’est plus disponible sans ambiguïté.");
        }
        await control.ClickAsync(new() { Timeout = 7000 }).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    private async Task<(IFrame? Frame, NativeColumnPreferences Preferences)> OpenColumnPreferencesAsync(
        TableChoice table, int timeoutSeconds, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(table, ct);
        var window = await InspectColumnsAsync(frame, table.FrameOrigin, table.Selector, ct);
        if (!window.Supported) return (null, new() { Reason = window.Reason });
        if (window.OpenerSelector.Length > 0)
        {
            // The selector is re-observed immediately before clicking. Only the
            // adapter's reviewed native opener can reach this operation.
            var fresh = await InspectColumnsAsync(frame, table.FrameOrigin, table.Selector, ct);
            if (!fresh.Supported || fresh.Proof != window.Proof || fresh.OpenerSelector != window.OpenerSelector)
                throw new InvalidOperationException("La commande des colonnes a changé avant son ouverture.");
            await ClickColumnControlAsync(frame, table.FrameOrigin, fresh.OpenerSelector, ct);
        }
        var timer = Stopwatch.StartNew();
        IFrame? preferencesFrame = null;
        NativeColumnPreferences preferences = new() { Reason = "La fenêtre personnelle des colonnes n’a pas fini de se charger." };
        var personalTabRequested = false;
        while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            ct.ThrowIfCancellationRequested();
            window = await InspectColumnsAsync(frame, table.FrameOrigin, table.Selector, ct);
            if (!window.Supported) return (preferencesFrame, new() { Reason = window.Reason });
            if (window.IframeSelector.Length > 0)
            {
                var locator = frame.Locator(window.IframeSelector);
                if (await locator.CountAsync() != 1) throw new InvalidOperationException("La fenêtre native des colonnes est ambiguë.");
                var element = await locator.ElementHandleAsync();
                preferencesFrame = element is null ? null : await element.ContentFrameAsync();
                if (preferencesFrame is { IsDetached: false } && Origin(preferencesFrame.Url).Length > 0)
                {
                    if (preferencesFrame.ParentFrame != frame || Origin(preferencesFrame.Url) != table.FrameOrigin)
                        throw new InvalidOperationException("La fenêtre des colonnes ne provient pas du cadre GLPI sélectionné.");
                    try { preferences = await InspectColumnPreferencesAsync(preferencesFrame, table.FrameOrigin, ct); }
                    catch (PlaywrightException) { await Task.Delay(120, ct); continue; }
                    if (PersonalColumnScope(preferences)) return (preferencesFrame, preferences);
                    if (!personalTabRequested && preferences.Supported && (preferences.PersonalTabSelector.Length > 0 || preferences.PersonalTabSelectSelector.Length > 0))
                    {
                        var fresh = await InspectColumnPreferencesAsync(preferencesFrame, table.FrameOrigin, ct);
                        if (fresh.PersonalTabSelector.Length > 0 && fresh.PersonalTabSelector == preferences.PersonalTabSelector)
                            await ClickColumnControlAsync(preferencesFrame, table.FrameOrigin, fresh.PersonalTabSelector, ct);
                        else if (fresh.PersonalTabSelectSelector.Length > 0 && fresh.PersonalTabSelectSelector == preferences.PersonalTabSelectSelector
                            && fresh.PersonalTabValue == preferences.PersonalTabValue)
                            await SelectNativeOptionAsync(preferencesFrame, fresh.PersonalTabSelectSelector, fresh.PersonalTabValue, false, ct);
                        else throw new InvalidOperationException("L’onglet personnel des colonnes a changé avant sa sélection.");
                        personalTabRequested = true;
                    }
                    else if (!preferences.Supported && preferences.Reason.Length > 0 && !personalTabRequested) return (preferencesFrame, preferences);
                }
            }
            await Task.Delay(120, ct);
        }
        return (preferencesFrame, new() { Reason = preferences.Reason.Length > 0 ? preferences.Reason : "La vue personnelle n’est pas devenue disponible dans le délai prévu." });
    }

    private async Task<ColumnCommandResult> SubmitColumnControlAsync(IFrame frame, string origin, NativeColumnPreferences before,
        NativeColumnOption? option, int timeoutSeconds, CancellationToken ct)
    {
        var fresh = await InspectColumnPreferencesAsync(frame, origin, ct);
        ValidatePersonalColumnView(before, fresh);
        var activate = option is null;
        if (activate && fresh.ActivateSelector.Length == 0)
            throw new ColumnOptionUnavailableException("La commande personnelle de création n’est plus disponible après relecture.");
        if (!activate)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (fresh.SelectedIdsKnown && fresh.Selected.Any(item => item.Value == option!.Value))
                {
                    Log?.Invoke("COLONNES · option déjà présente dans la vue relue ; aucun ajout en double.");
                    return new(fresh, false);
                }
                var offered = ColumnPreparationPolicy.Available(fresh).FirstOrDefault(item => SameColumnOption(option!, item));
                if (offered is null)
                    throw new ColumnOptionUnavailableException(fresh.Options.Any(item => item.Value == option!.Value)
                        ? "Le libellé de l’option a changé après relecture ; aucun ajout n’a été envoyé pour cette option."
                        : "L’option choisie n’est plus disponible dans la vue relue ; aucun ajout n’a été envoyé pour cette option.");
                if (fresh.SelectSelector.Length == 0 || fresh.AddSelector.Length == 0)
                    throw new ColumnOptionUnavailableException("La commande personnelle d’ajout n’est plus disponible après relecture.");
                if (fresh.SelectedValue != option!.Value)
                {
                    var previous = fresh;
                    try
                    {
                        // A native rerender may replace the ID during change.
                        // Re-read the verified form afterwards instead of asking
                        // the old locator for its now-detached input value.
                        await frame.Locator(fresh.SelectSelector).SelectOptionAsync(new SelectOptionValue { Value = option.Value },
                            new() { Timeout = 3000, Force = fresh.SelectHidden });
                        ct.ThrowIfCancellationRequested();
                    }
                    catch (PlaywrightException)
                    {
                        fresh = await InspectColumnPreferencesAsync(frame, origin, ct);
                        ValidatePersonalColumnView(previous, fresh);
                        if (attempt == 2) throw new ColumnOptionUnavailableException("Le menu des colonnes n’a pas conservé un repère utilisable ; aucun ajout n’a été envoyé pour cette option.");
                        continue;
                    }
                    fresh = await InspectColumnPreferencesAsync(frame, origin, ct);
                    ValidatePersonalColumnView(previous, fresh);
                }
                if (fresh.SelectedValue == option.Value && ColumnPreparationPolicy.Available(fresh).Any(item => SameColumnOption(option, item))) break;
                if (attempt == 2) throw new ColumnOptionUnavailableException("Le menu n’a pas conservé la colonne choisie ; aucun ajout n’a été envoyé pour cette option.");
            }
        }
        // Observe GLPI's own request. No endpoint, payload, CSRF token or form
        // is synthesized. The modern route returns no application JSON status;
        // its persisted list is checked again after reopening below.
        var response = await frame.Page.RunAndWaitForResponseAsync(
            async () =>
            {
                var ready = await InspectColumnPreferencesAsync(frame, origin, ct);
                ValidatePersonalColumnView(fresh, ready);
                if (activate ? ready.ActivateSelector.Length == 0 : ready.AddSelector.Length == 0 || ready.SelectedValue != option!.Value
                    || !ColumnPreparationPolicy.Available(ready).Any(item => SameColumnOption(option!, item)))
                    throw new ColumnOptionUnavailableException("La commande a changé avant sa validation ; aucun ajout n’a été envoyé pour cette option.");
                fresh = ready;
                await ClickColumnControlAsync(frame, origin, activate ? ready.ActivateSelector : ready.AddSelector, ct, optionalCommand: true);
            },
            reply => reply.Request.Method == "POST" && Origin(reply.Url) == origin && reply.Request.Frame == frame
                && new Uri(reply.Url).AbsolutePath.EndsWith(fresh.Layout == "modern" ? "/ajax/displaypreference.php" : "/front/displaypreference.form.php", StringComparison.Ordinal),
            new() { Timeout = timeoutSeconds * 1000 }).WaitAsync(ct);
        if (response.Status is < 200 or >= 400)
            throw new InvalidOperationException("GLPI n’a pas confirmé la commande personnelle des colonnes (réponse HTTP " + response.Status + ").");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            ct.ThrowIfCancellationRequested();
            NativeColumnPreferences after;
            try { after = await InspectColumnPreferencesAsync(frame, origin, ct); }
            catch (PlaywrightException) { await Task.Delay(120, ct); continue; }
            if (PersonalColumnScope(after) && after.UsersId == fresh.UsersId && after.ActivateSelector.Length == 0)
            {
                if (activate) return new(after, false);
                if (after.Selected.Count >= fresh.Selected.Count + 1 && SelectedColumnsPreserved(fresh, after)
                    && (after.SelectedIdsKnown ? after.Selected.Any(item => item.Value == option!.Value)
                        : !after.Options.Any(item => item.Value == option!.Value))) return new(after, true);
            }
            await Task.Delay(120, ct);
        }
        throw new InvalidOperationException(activate ? "GLPI n’a pas confirmé l’activation de la vue personnelle. Relancer après vérification dans le navigateur."
            : "GLPI n’a pas confirmé l’ajout de la colonne. Les ajouts déjà effectués restent dans la vue personnelle ; aucun relevé complet n’est publié.");
    }

    private async Task CloseColumnPreferencesAsync(TableChoice table, CancellationToken ct, bool waitForRefresh = false, int timeoutSeconds = 5)
    {
        var frame = await ResolveFrameAsync(table, ct);
        var window = await InspectColumnsAsync(frame, table.FrameOrigin, table.Selector, ct);
        if (window.Supported && window.IframeSelector.Length == 0) return;
        if (!window.Supported || window.CloseSelector.Length == 0)
            throw new InvalidOperationException("La fermeture native de la fenêtre des colonnes n’a pas pu être confirmée. Fermer cette fenêtre dans GLPI puis relancer la collecte.");
        var previousTable = waitForRefresh ? await frame.Locator(table.Selector).ElementHandleAsync() : null;
        await ClickColumnControlAsync(frame, table.FrameOrigin, window.CloseSelector, ct);
        if (previousTable is not null)
        {
            // GLPI 11 refreshes the results asynchronously on modal close. An
            // unchanged column count cannot distinguish that response from the
            // old table, particularly after the final persistence recheck.
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                ct.ThrowIfCancellationRequested();
                try { if (!await previousTable.EvaluateAsync<bool>("element => element.isConnected").WaitAsync(ct)) return; }
                catch (PlaywrightException) { return; } // Full document navigation invalidates the old handle too.
                await Task.Delay(120, ct);
            }
            throw new InvalidOperationException("La fermeture native n’a pas confirmé le renouvellement du tableau. Vérifier GLPI avant de relancer la collecte.");
        }
    }

    private async Task<TableChoice> RefreshPreparedTableAsync(TableChoice original, int minimumColumns, int timeoutSeconds, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            ct.ThrowIfCancellationRequested();
            if (original.Page is null || original.Page.IsClosed || Origin(original.Page.Url) != original.TopOrigin)
                throw new InvalidOperationException("L’onglet GLPI a changé pendant la préparation des colonnes.");
            try
            {
                var expectedFrame = await ResolveFrameAsync(original, ct);
                var sameFrame = (await InspectAsync(original.Page, ct, quiet: true)).Where(table => table.Frame == expectedFrame
                    && table.FrameOrigin == original.FrameOrigin && table.FrameName == original.FrameName).ToArray();
                var tables = sameFrame.Where(table => table.Selector == original.Selector).ToArray();
                if (tables.Length == 0)
                {
                    // Native refreshes can replace the randomized table ID. In
                    // that case require one Ticket table in the original frame,
                    // preserving every initial column in its original order.
                    var candidates = new List<TableChoice>();
                    foreach (var candidate in sameFrame.Where(item => item.Headers.Count >= minimumColumns
                        && ColumnPreparationPolicy.PreservesHeaders(original.Headers, item.Headers)))
                    {
                        var nativeTicket = await candidate.Frame!.Locator(candidate.Selector).EvaluateAsync<bool>("""
                            table => {
                              const owner = table.closest('form[data-search-itemtype],form[id^="massform"],form[name^="massform"]');
                              return owner?.getAttribute('data-search-itemtype') === 'Ticket' ||
                                /^massformTicket\d*$/.test(owner?.id || owner?.getAttribute('name') || '');
                            }
                            """).WaitAsync(ct);
                        if (nativeTicket) candidates.Add(candidate);
                    }
                    tables = candidates.ToArray();
                }
                if (tables.Length > 1) throw new InvalidOperationException("Plusieurs tableaux de tickets correspondent après la préparation. Inspecter de nouveau la liste avant de collecter.");
                if (tables.Length == 1 && tables[0].Headers.Count >= minimumColumns)
                {
                    var refreshed = tables[0];
                    if (!ColumnPreparationPolicy.PreservesHeaders(original.Headers, refreshed.Headers))
                        throw new InvalidOperationException("Les colonnes initiales ont été retirées ou réordonnées pendant la préparation. Vérifier la vue GLPI avant de collecter.");
                    var window = await InspectColumnsAsync(refreshed.Frame!, refreshed.FrameOrigin, refreshed.Selector, ct);
                    if (window.IframeSelector.Length == 0) return refreshed;
                }
            }
            catch (PlaywrightException) { /* Native close may be reloading the table's document. */ }
            await Task.Delay(120, ct);
        }
        throw new InvalidOperationException("Le tableau n’a pas été retrouvé avec ses colonnes après la préparation. Le relevé précédent est conservé ; vérifier la liste GLPI et relancer.");
    }

    public async Task<ColumnPreparationResult> PrepareColumnsAsync(TableChoice table, AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mode = settings.ColumnPreparationMode;
        var receipt = new ColumnPreparationReceipt { Mode = mode, ColumnCount = table.Headers.Count };
        void Report(string message) { Log?.Invoke("COLONNES · " + AppPaths.SafeLog(message)); progress?.Report(new(0, 0, null, message)); }
        void Unavailable(string reason)
        {
            receipt.State = "unavailable";
            receipt.Message = "Préparation des colonnes indisponible : " + reason + " La collecte conserve les colonnes affichées.";
            receipt.Warnings.Add(receipt.Message);
        }
        if (mode == "current")
        {
            receipt.Message = $"Vue actuelle conservée · {table.Headers.Count:N0} colonnes.";
            return new(table, receipt);
        }
        if (mode is not ("supervision" or "all")) throw new InvalidOperationException("Le mode de préparation des colonnes est inconnu.");
        Report(mode == "all" ? "Préparation de toutes les colonnes proposées par GLPI…" : "Préparation des colonnes de supervision…");
        var frame = await ResolveFrameAsync(table, ct);
        var window = await InspectColumnsAsync(frame, table.FrameOrigin, table.Selector, ct);
        if (!window.Supported)
        {
            Unavailable(window.Reason); receipt.MissingRoles = ColumnPreparationPolicy.MissingRoles(table.Headers, settings.Dashboard);
            Report(receipt.Message); ct.ThrowIfCancellationRequested(); return new(table, receipt);
        }
        var initialScope = await CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
        if (initialScope.Fingerprint.Length == 0)
        {
            Unavailable("Le contexte de recherche ne peut pas être contrôlé avant les ajouts.");
            Report(receipt.Message); ct.ThrowIfCancellationRequested(); return new(table, receipt);
        }
        var before = await ReadVerificationSampleAsync(table, ct);
        var original = table;
        async Task CheckPreparationScopeAsync()
        {
            var observed = await CaptureScopeEvidenceAsync(original, settings.PortalUrl, ct);
            if (observed.Fingerprint.Length == 0 || observed.Fingerprint != initialScope.Fingerprint)
                throw new InvalidOperationException("Les critères ou le contexte de recherche ont changé pendant la préparation des colonnes. Aucun autre ajout n’est exécuté.");
        }
        var timeout = Math.Clamp(settings.ListTimeoutSeconds, 5, 600);
        var opened = await OpenColumnPreferencesAsync(table, timeout, ct);
        var preferences = opened.Preferences;
        var added = new List<NativeColumnOption>();
        var satisfied = new List<NativeColumnOption>();
        if (opened.Frame is null || !PersonalColumnScope(preferences)) Unavailable(preferences.Reason);
        else
        {
            try
            {
                if (preferences.ActivateSelector.Length > 0)
                {
                    await CheckPreparationScopeAsync();
                    Report("Activation de la vue personnelle GLPI ; les colonnes et leur ordre sont conservés.");
                    preferences = (await SubmitColumnControlAsync(opened.Frame, table.FrameOrigin, preferences, null, timeout, ct)).Preferences;
                }
                var initiallyOffered = ColumnPreparationPolicy.Available(preferences).Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
                while (ColumnPreparationPolicy.Next(mode, original.Headers, preferences, settings.Dashboard, satisfied) is { } option)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!initiallyOffered.Contains(option.Value))
                        throw new ColumnOptionUnavailableException("La liste des options de colonne a changé pendant la préparation ; aucun ajout n’a été envoyé pour cette nouvelle option.");
                    Report($"Ajout de colonne {added.Count + 1:N0} · {ColumnPreparationPolicy.Header(option)}");
                    await CheckPreparationScopeAsync();
                    var command = await SubmitColumnControlAsync(opened.Frame, table.FrameOrigin, preferences, option, timeout, ct);
                    preferences = command.Preferences;
                    if (command.Added) added.Add(option);
                    satisfied.Add(option);
                }
                receipt.State = "ready";
            }
            catch (ColumnOptionUnavailableException error)
            {
                ct.ThrowIfCancellationRequested();
                // Closing the legacy dialog reloads the page and could erase
                // a changed, not-yet-submitted search form. Check before it.
                await CheckPreparationScopeAsync();
                // No unconfirmed add is retried or hidden here. Earlier native
                // commands remain subject to the persistence check below.
                Unavailable(error.Message);
                Log?.Invoke("COLONNES · préparation interrompue avant la commande ; reprise de la collecte après fermeture et relecture du même périmètre.");
            }
        }
        // Legacy closes reload the document too. Even with zero additions, do
        // not return the still-visible old table before that reload completes.
        await CloseColumnPreferencesAsync(table, ct, waitForRefresh: true, timeout);
        table = await RefreshPreparedTableAsync(original, before.Headers.Count + added.Count, timeout, ct);
        if (opened.Frame is not null && preferences.Layout == "modern" && satisfied.Count > 0)
        {
            // GLPI 11 updates its DOM optimistically and can reuse the iframe.
            // Reopen, then read its already observed native GET URL normally;
            // a visible optimistic list alone never proves persistence.
            var persisted = await OpenColumnPreferencesAsync(table, timeout, ct);
            if (persisted.Frame is null) throw new InvalidOperationException("La vue personnelle ne peut pas être relue pour confirmer les colonnes ajoutées.");
            var iframe = await persisted.Frame.FrameElementAsync();
            var observedUrl = await iframe.EvaluateAsync<string>("element => element.src").WaitAsync(ct);
            if (!Uri.TryCreate(observedUrl, UriKind.Absolute, out var nativeUrl) || Origin(observedUrl) != table.FrameOrigin
                || nativeUrl.UserInfo.Length > 0 || !nativeUrl.AbsolutePath.EndsWith("/front/displaypreference.form.php", StringComparison.Ordinal)
                || nativeUrl.Query.TrimStart('?') != "itemtype=Ticket")
                throw new InvalidOperationException("L’adresse native de relecture des colonnes n’est pas vérifiable. Aucun relevé complet n’est publié.");
            await persisted.Frame.GotoAsync(observedUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout * 1000 }).WaitAsync(ct);
            var confirmation = await OpenColumnPreferencesAsync(table, timeout, ct);
            if (confirmation.Frame != persisted.Frame || !PersonalColumnScope(confirmation.Preferences) || confirmation.Preferences.UsersId != preferences.UsersId
                || !confirmation.Preferences.SelectedIdsKnown || !SelectedColumnsPreserved(preferences, confirmation.Preferences))
                throw new InvalidOperationException("Les colonnes ajoutées ne sont pas confirmées après relecture de la vue personnelle GLPI. Aucun relevé complet n’est publié.");
            await CloseColumnPreferencesAsync(table, ct, waitForRefresh: true, timeout);
            table = await RefreshPreparedTableAsync(table, table.Headers.Count, timeout, ct);
        }
        var afterScope = await CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
        if (afterScope.Fingerprint.Length == 0 || afterScope.Fingerprint != initialScope.Fingerprint)
            throw new InvalidOperationException("Les critères ou le contexte de recherche ont changé pendant la préparation des colonnes. La collecte s’arrête pour ne pas lire un autre périmètre.");
        var after = await ReadVerificationSampleAsync(table, ct);
        receipt.AddedCount = added.Count; receipt.ColumnCount = after.Headers.Count;
        receipt.MissingRoles = ColumnPreparationPolicy.MissingRoles(after.Headers, settings.Dashboard);
        receipt.SourceChanged = ColumnPreparationPolicy.SourceChanged(before, after, settings.Dashboard);
        if (receipt.SourceChanged)
            receipt.Warnings.Add("La liste des tickets a évolué pendant la préparation des colonnes, avec les mêmes critères observables. L’export utilise un nouvel échantillon ; la couverture de cette exécution reste à vérifier.");
        if (receipt.State == "ready") receipt.Message = $"{added.Count:N0} colonnes ajoutées à la vue personnelle · {after.Headers.Count:N0} colonnes relues dans GLPI."
            + (mode == "all" ? " Toutes les options disponibles ont été traitées." : " Préparation de supervision terminée.");
        else if (added.Count > 0)
        {
            var previousMessage = receipt.Message;
            receipt.Message += $" Ajouts confirmés : {added.Count:N0} · colonnes relues : {after.Headers.Count:N0}.";
            var warning = receipt.Warnings.IndexOf(previousMessage);
            if (warning >= 0) receipt.Warnings[warning] = receipt.Message;
        }
        Report(receipt.Message);
        ct.ThrowIfCancellationRequested();
        return new(table, receipt);
    }
}
