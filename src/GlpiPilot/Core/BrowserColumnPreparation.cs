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

    private static bool PersonalColumnTabAvailable(NativeColumnPreferences preferences) => preferences.Supported
        && preferences.Proof == "native-personal-tab"
        && (preferences.PersonalTabSelector.Length > 0 || preferences.PersonalTabSelectSelector.Length > 0);

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

    // This exception is restricted to failures before a native control is
    // invoked. A mutation response or persistence failure must never enter the fallback.
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

    private static async Task<IElementHandle?> ColumnElementAsync(IFrame frame, string origin, string selector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (frame.IsDetached || Origin(frame.Url) != origin)
            throw new InvalidOperationException("Le contexte des colonnes a changé avant la lecture du contrôle natif.");
        if (selector.Length == 0) return null;
        // A locator's ElementHandle/IsEnabled auto-waits for its old selector.
        // Native AJAX can retire that selector after discovery. Take a current,
        // nonwaiting snapshot instead and let the caller reread the native DOM.
        var handles = await frame.Locator(selector).ElementHandlesAsync().WaitAsync(ct);
        if (handles.Count == 1) return handles[0];
        foreach (var handle in handles) await handle.DisposeAsync();
        if (handles.Count > 1)
            throw new InvalidOperationException("Plusieurs éléments correspondent au contrôle natif des colonnes. Aucune commande n’est exécutée.");
        return null;
    }

    private static async Task<bool> ColumnControlReadyAsync(IElementHandle control, CancellationToken ct) =>
        await control.EvaluateAsync<bool>("""
            element => {
              if (!element.isConnected || element.matches(':disabled,[disabled],[aria-disabled="true"],.disabled') ||
                  element.closest('fieldset[disabled],[aria-disabled="true"]')) return false;
              for (let node = element; node instanceof Element; node = node.parentElement) {
                if (node.hidden || node.getAttribute('aria-hidden') === 'true' || node.hasAttribute('inert')) return false;
                const style = getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
              }
              return element.getClientRects().length > 0;
            }
            """).WaitAsync(ct);

    private static async Task ClickColumnControlAsync(IFrame frame, string origin, string selector, CancellationToken ct, bool optionalCommand = false)
    {
        ct.ThrowIfCancellationRequested();
        if (frame.IsDetached || Origin(frame.Url) != origin) throw new InvalidOperationException("Le contexte des colonnes a changé avant l’action native.");
        await using var control = await ColumnElementAsync(frame, origin, selector, ct);
        if (control is null || !await ColumnControlReadyAsync(control, ct))
        {
            if (optionalCommand) throw new ColumnOptionUnavailableException("La commande personnelle a disparu avant le clic ; aucun ajout n’a été envoyé pour cette option.");
            throw new InvalidOperationException("La commande native des colonnes n’est plus disponible sans ambiguïté.");
        }
        // The caller observes the native request independently. If Playwright
        // ends its action wait after dispatch, the command is never repeated.
        await control.ClickAsync(new() { Timeout = 7000 }).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    private async Task ObserveColumnActionAsync(TableChoice table, ColumnPreparationActivity activity,
        Func<Task> command, CancellationToken ct, bool discoverPreferencesFrame = false)
    {
        var action = command();
        _ = action.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        while (!action.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            activity.ThrowIfFailed();
            if (discoverPreferencesFrame && !activity.HasPendingRequest)
            {
                // A click can wait for an iframe navigation internally. Bind
                // the already rendered native frame while that click settles,
                // so slow initial GETs remain visible to the activity observer.
                var frame = await ResolveFrameAsync(table, ct);
                var window = await InspectCurrentColumnWindowAsync(table, frame, ct);
                await using var element = await ColumnElementAsync(frame, table.FrameOrigin, window.IframeSelector, ct);
                if (element is not null && await element.ContentFrameAsync().WaitAsync(ct) is { IsDetached: false } preferencesFrame)
                {
                    if (preferencesFrame.ParentFrame != frame)
                        throw new InvalidOperationException("La fenêtre des colonnes ne provient pas du cadre GLPI sélectionné.");
                    activity.BindPreferencesFrame(preferencesFrame);
                }
            }
            _ = activity.CanWait;
            await Task.WhenAny(action, Task.Delay(120, ct)).WaitAsync(ct);
        }
        await action;
    }

    private async Task<TableChoice?> FindCurrentColumnTableAsync(TableChoice original, int minimumColumns, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(original, ct);
        var sameFrame = (await InspectAsync(original.Page!, ct, quiet: true)).Where(candidate => candidate.Frame == frame
            && candidate.FrameOrigin == original.FrameOrigin && candidate.FrameName == original.FrameName).ToArray();
        var exact = sameFrame.Where(candidate => candidate.Selector == original.Selector).ToArray();
        var inspected = exact.Length > 0 ? exact : sameFrame;
        var candidates = new List<TableChoice>();
        foreach (var candidate in inspected.Where(item => item.Headers.Count >= minimumColumns
            && ColumnPreparationPolicy.PreservesHeaders(original.Headers, item.Headers)))
        {
            await using var element = await ColumnElementAsync(frame, original.FrameOrigin, candidate.Selector, ct);
            if (element is null) continue;
            var nativeTicket = await element.EvaluateAsync<bool>("""
                (table, exactReference) => {
                  if (!table.isConnected) return false;
                  if (exactReference) return true;
                  const owner = table.closest('form[data-search-itemtype],form[id^="massform"],form[name^="massform"]');
                  return owner?.getAttribute('data-search-itemtype') === 'Ticket' ||
                    /^massformTicket\d*$/.test(owner?.id || owner?.getAttribute('name') || '');
                }
                """, exact.Length > 0).WaitAsync(ct);
            if (nativeTicket) candidates.Add(candidate);
        }
        if (candidates.Count > 1)
            throw new InvalidOperationException("Plusieurs tableaux de tickets correspondent pendant la préparation des colonnes. Aucune commande n’est exécutée.");
        if (exact.Length > 0 && candidates.Count == 0 && exact.Any(candidate => !ColumnPreparationPolicy.PreservesHeaders(original.Headers, candidate.Headers)))
            throw new InvalidOperationException("Les colonnes initiales ont été retirées ou réordonnées pendant la préparation. Vérifier la vue GLPI avant de collecter.");
        return candidates.SingleOrDefault();
    }

    private async Task<NativeColumnInspection> InspectCurrentColumnWindowAsync(TableChoice table, IFrame frame, CancellationToken ct)
    {
        var window = await InspectColumnsAsync(frame, table.FrameOrigin, table.Selector, ct);
        if (window.TableSelector.Length > 0) return window;
        var current = await FindCurrentColumnTableAsync(table, table.Headers.Count, ct);
        if (current is null) return window;
        RememberCurrentTableReference(table, current);
        return await InspectColumnsAsync(frame, table.FrameOrigin, table.Selector, ct);
    }

    private async Task<(IFrame? Frame, NativeColumnPreferences Preferences)> OpenColumnPreferencesAsync(
        TableChoice table, ColumnPreparationActivity activity, CancellationToken ct)
    {
        activity.BeginPhase("Ouverture de la fenêtre personnelle des colonnes");
        var frame = await ResolveFrameAsync(table, ct);
        var window = await InspectCurrentColumnWindowAsync(table, frame, ct);
        if (!window.Supported) return (null, new() { Reason = window.Reason });
        if (window.OpenerSelector.Length > 0)
        {
            // Revalidate the native meaning, not the generated locator. GLPI
            // may renew the opener or finish opening its modal between reads.
            var fresh = await InspectCurrentColumnWindowAsync(table, frame, ct);
            if (!fresh.Supported) return (null, new() { Reason = fresh.Reason });
            if (fresh.IframeSelector.Length == 0)
            {
                if (fresh.Proof != window.Proof || fresh.OpenerSelector.Length == 0)
                    return (null, new() { Reason = "La commande personnelle des colonnes n’est plus disponible avant son ouverture." });
                try
                {
                    await ObserveColumnActionAsync(table, activity,
                        () => ClickColumnControlAsync(frame, table.FrameOrigin, fresh.OpenerSelector, ct, optionalCommand: true), ct,
                        discoverPreferencesFrame: true);
                }
                catch (ColumnOptionUnavailableException)
                { return (null, new() { Reason = "La commande personnelle des colonnes a disparu avant son ouverture." }); }
                catch (Exception error) when (error is PlaywrightException or TimeoutException)
                {
                    // Opening the native preferences only reads them. The
                    // modal may already be loading after an action timeout;
                    // inspect it below without clicking the opener again.
                    Log?.Invoke("COLONNES · ouverture en cours ; relecture de la fenêtre native sans nouveau clic.");
                }
            }
        }
        IFrame? preferencesFrame = null;
        NativeColumnPreferences preferences = new() { Reason = "La fenêtre personnelle des colonnes n’a pas fini de se charger." };
        var personalTabRequested = false;
        while (activity.CanWait)
        {
            ct.ThrowIfCancellationRequested();
            window = await InspectCurrentColumnWindowAsync(table, frame, ct);
            var windowObservation = "fenêtre|" + window.Proof + "|" + window.IframeSelector + "|" + window.CloseSelector;
            var preferencesObserved = false;
            if (!window.Supported)
            {
                if (window.TableSelector.Length == 0) { await Task.Delay(120, ct); continue; }
                return (preferencesFrame, new() { Reason = window.Reason });
            }
            if (window.IframeSelector.Length > 0)
            {
                await using var element = await ColumnElementAsync(frame, table.FrameOrigin, window.IframeSelector, ct);
                if (element is null) { await Task.Delay(120, ct); continue; }
                preferencesFrame = await element.ContentFrameAsync().WaitAsync(ct);
                if (preferencesFrame is { IsDetached: false })
                {
                    if (preferencesFrame.ParentFrame != frame)
                        throw new InvalidOperationException("La fenêtre des colonnes ne provient pas du cadre GLPI sélectionné.");
                    activity.BindPreferencesFrame(preferencesFrame);
                }
                if (preferencesFrame is { IsDetached: false } && Origin(preferencesFrame.Url).Length > 0)
                {
                    if (preferencesFrame.ParentFrame != frame || Origin(preferencesFrame.Url) != table.FrameOrigin)
                        throw new InvalidOperationException("La fenêtre des colonnes ne provient pas du cadre GLPI sélectionné.");
                    try { preferences = await InspectColumnPreferencesAsync(preferencesFrame, table.FrameOrigin, ct); }
                    catch (PlaywrightException) { await Task.Delay(120, ct); continue; }
                    preferencesObserved = true;
                    activity.Observe(windowObservation + "|vue|" + preferences.Proof + "|" + preferences.PersonalScope + "|" + preferences.UsersId
                        + "|" + preferences.Selected.Count + "|" + preferences.Options.Count);
                    if (PersonalColumnScope(preferences))
                    {
                        if (!activity.HasPendingRequest) { activity.ThrowIfFailed(); return (preferencesFrame, preferences); }
                        await Task.Delay(120, ct);
                        continue;
                    }
                    if (!personalTabRequested && PersonalColumnTabAvailable(preferences))
                    {
                        NativeColumnPreferences fresh;
                        try { fresh = await InspectColumnPreferencesAsync(preferencesFrame, table.FrameOrigin, ct); }
                        catch (PlaywrightException) { await Task.Delay(120, ct); continue; }
                        // The initial AJAX load can finish between these two
                        // reads. A ready personal view needs no further tab action.
                        if (PersonalColumnScope(fresh))
                        {
                            if (!activity.HasPendingRequest) { activity.ThrowIfFailed(); return (preferencesFrame, fresh); }
                            await Task.Delay(120, ct);
                            continue;
                        }
                        if (!PersonalColumnTabAvailable(fresh))
                        {
                            if (activity.HasPendingRequest) { await Task.Delay(120, ct); continue; }
                            activity.ThrowIfFailed();
                            return (preferencesFrame, fresh);
                        }
                        if (fresh.PersonalTabSelector != preferences.PersonalTabSelector
                            || fresh.PersonalTabSelectSelector != preferences.PersonalTabSelectSelector
                            || fresh.PersonalTabValue != preferences.PersonalTabValue)
                            Log?.Invoke("COLONNES · onglet personnel renouvelé ; sa cible native a été revalidée.");
                        preferences = fresh;
                        try
                        {
                            if (fresh.PersonalTabSelector.Length > 0)
                                await ClickColumnControlAsync(preferencesFrame, table.FrameOrigin, fresh.PersonalTabSelector, ct, optionalCommand: true);
                            else
                            {
                                await using var select = await ColumnElementAsync(preferencesFrame, table.FrameOrigin, fresh.PersonalTabSelectSelector, ct);
                                if (select is null || !await ColumnControlReadyAsync(select, ct))
                                    throw new ColumnOptionUnavailableException("Le menu personnel a changé avant sa sélection.");
                                await select.SelectOptionAsync(new SelectOptionValue { Value = fresh.PersonalTabValue }, new() { Timeout = 5000 }).WaitAsync(ct);
                                // The native change may replace the compact
                                // menu. Read the resulting personal view below,
                                // never wait for the old control's InputValue.
                                ct.ThrowIfCancellationRequested();
                            }
                            personalTabRequested = true;
                        }
                        catch (ColumnOptionUnavailableException)
                        {
                            // No native action was invoked. The next bounded
                            // read may discover a fresh link or compact menu.
                            preferences = new() { Reason = "L’onglet personnel n’est plus disponible avant sa sélection." };
                        }
                        catch (Exception error) when (error is PlaywrightException or TimeoutException)
                        {
                            // A navigation action may have reached GLPI. Do
                            // not repeat it; wait for its visible result only.
                            personalTabRequested = true;
                            Log?.Invoke("COLONNES · chargement de la vue personnelle en cours ; attente sans nouvelle sélection.");
                        }
                    }
                    else if (!preferences.Supported && preferences.Reason.Length > 0 && !personalTabRequested)
                    {
                        // An empty AJAX body is a loading state, not an absent
                        // permission. Explicit global/foreign/invalid controls
                        // still return the adapter's refusal without an action.
                        var loading = await preferencesFrame.EvaluateAsync<bool>("""
                            () => {
                              const body = document.querySelector('#tabsbody');
                              return document.readyState === 'loading' || !body || body.getAttribute('aria-busy') === 'true' ||
                                (!body.querySelector('form,input[name="itemtype"],input[name="users_id"],select,button') && !body.textContent.trim());
                            }
                            """).WaitAsync(ct);
                        if (!loading && !activity.HasPendingRequest) return (preferencesFrame, preferences);
                    }
                }
            }
            if (!preferencesObserved) activity.Observe(windowObservation);
            await Task.Delay(120, ct);
        }
        return (preferencesFrame, new() { Reason = personalTabRequested ? "La vue personnelle n’est pas devenue disponible dans le délai prévu."
            : preferences.Reason.Length > 0 ? preferences.Reason : "La vue personnelle n’est pas devenue disponible dans le délai prévu." });
    }

    private async Task<ColumnCommandResult> SubmitColumnControlAsync(IFrame frame, string origin, NativeColumnPreferences before,
        NativeColumnOption? option, ColumnPreparationActivity activity, Func<Task> validateScope, CancellationToken ct)
    {
        activity.BeginPhase(option is null ? "Activation de la vue personnelle" : "Ajout de la colonne « " + ColumnPreparationPolicy.Header(option) + " »");
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
                        await using var select = await ColumnElementAsync(frame, origin, fresh.SelectSelector, ct);
                        if (select is null)
                        {
                            fresh = await InspectColumnPreferencesAsync(frame, origin, ct);
                            ValidatePersonalColumnView(previous, fresh);
                            if (attempt == 2) throw new ColumnOptionUnavailableException("Le menu personnel a disparu avant le choix de colonne ; aucun ajout n’a été envoyé.");
                            continue;
                        }
                        await select.SelectOptionAsync(new SelectOptionValue { Value = option.Value },
                            new() { Timeout = 3000, Force = fresh.SelectHidden }).WaitAsync(ct);
                        ct.ThrowIfCancellationRequested();
                    }
                    catch (Exception error) when (error is PlaywrightException or TimeoutException)
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
        var response = await activity.RunAndWaitForResponseAsync(
            async () =>
            {
                var ready = await InspectColumnPreferencesAsync(frame, origin, ct);
                ValidatePersonalColumnView(fresh, ready);
                if (activate ? ready.ActivateSelector.Length == 0 : ready.AddSelector.Length == 0 || ready.SelectedValue != option!.Value
                    || !ColumnPreparationPolicy.Available(ready).Any(item => SameColumnOption(option!, item)))
                    throw new ColumnOptionUnavailableException("La commande a changé avant sa validation ; aucun ajout n’a été envoyé pour cette option.");
                await validateScope();
                fresh = ready;
                await ClickColumnControlAsync(frame, origin, activate ? ready.ActivateSelector : ready.AddSelector, ct, optionalCommand: true);
            },
            reply => reply.Request.Method == "POST" && Origin(reply.Url) == origin && reply.Request.Frame == frame
                && new Uri(reply.Url).AbsolutePath.EndsWith(fresh.Layout == "modern" ? "/ajax/displaypreference.php" : "/front/displaypreference.form.php", StringComparison.Ordinal),
            ct);
        if (response.Status is < 200 or >= 400)
            throw new InvalidOperationException("GLPI n’a pas confirmé la commande personnelle des colonnes (réponse HTTP " + response.Status + ").");
        activity.BeginPhase(activate ? "Confirmation de la vue personnelle" : "Confirmation de la colonne ajoutée");
        while (activity.CanWait)
        {
            ct.ThrowIfCancellationRequested();
            NativeColumnPreferences after;
            try { after = await InspectColumnPreferencesAsync(frame, origin, ct); }
            catch (PlaywrightException) { await Task.Delay(120, ct); continue; }
            activity.Observe(after.Proof + "|" + after.UsersId + "|" + after.ActivateSelector + "|"
                + string.Join(',', after.Selected.Select(ColumnIdentity)) + "|" + string.Join(',', after.Options.Select(item => item.Value)));
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

    private async Task CloseColumnPreferencesAsync(TableChoice table, ColumnPreparationActivity activity,
        Func<Task> validateScope, CancellationToken ct, bool waitForRefresh = false)
    {
        activity.BeginPhase("Fermeture de la fenêtre des colonnes et reprise de la liste", includeTableRequests: true);
        while (activity.CanWait)
        {
            ct.ThrowIfCancellationRequested();
            var frame = await ResolveFrameAsync(table, ct);
            var window = await InspectCurrentColumnWindowAsync(table, frame, ct);
            activity.Observe(window.Proof + "|" + window.TableSelector + "|" + window.IframeSelector + "|" + window.CloseSelector);
            if (window.Supported && window.IframeSelector.Length == 0) return;
            if (window.TableSelector.Length == 0) { await Task.Delay(120, ct); continue; }
            if (!window.Supported || window.CloseSelector.Length == 0)
                throw new InvalidOperationException("La fermeture native de la fenêtre des colonnes n’a pas pu être confirmée. Fermer cette fenêtre dans GLPI puis relancer la collecte.");
            await using var previousTable = waitForRefresh ? await ColumnElementAsync(frame, table.FrameOrigin, table.Selector, ct) : null;
            if (waitForRefresh && (previousTable is null || !await previousTable.EvaluateAsync<bool>("element => element.isConnected").WaitAsync(ct)))
            {
                // GLPI can renew the table while its modal is open. Reinspect
                // that native table before dispatching the single close click.
                await Task.Delay(120, ct);
                continue;
            }
            await using var close = await ColumnElementAsync(frame, table.FrameOrigin, window.CloseSelector, ct);
            if (close is null || !await ColumnControlReadyAsync(close, ct)) { await Task.Delay(120, ct); continue; }
            // Re-observation may have taken time. Closing a legacy dialog can
            // reload the search, so recheck immediately before this one click.
            await validateScope();
            try
            {
                await ObserveColumnActionAsync(table, activity,
                    () => close.ClickAsync(new() { Timeout = 7000 }).WaitAsync(ct), ct);
            }
            catch (Exception error) when (error is TimeoutException or PlaywrightException)
            {
                activity.ThrowIfFailed();
                // The close may already have triggered the native list request.
                // Observe its result below; do not send a second close click.
                Log?.Invoke("COLONNES · fermeture envoyée ou contrôle renouvelé ; attente de la liste native sans nouveau clic.");
            }
            if (previousTable is null) return;
            activity.BeginPhase("Rechargement de la liste après fermeture des colonnes", includeTableRequests: true);
            while (activity.CanWait)
            {
                ct.ThrowIfCancellationRequested();
                try { if (!await previousTable.EvaluateAsync<bool>("element => element.isConnected").WaitAsync(ct)) return; }
                catch (PlaywrightException) { return; } // A native document navigation retires the handle too.
                await Task.Delay(120, ct);
            }
            throw new InvalidOperationException("GLPI ne renouvelle plus le tableau après la fermeture des colonnes. Vérifier la liste dans le navigateur ou annuler la collecte.");
        }
        throw new InvalidOperationException("La fenêtre des colonnes ne présente plus de commande de fermeture utilisable. Fermer cette fenêtre dans GLPI puis relancer la collecte.");
    }

    private async Task<TableChoice> RefreshPreparedTableAsync(TableChoice original, int minimumColumns,
        ColumnPreparationActivity activity, CancellationToken ct)
    {
        activity.BeginPhase("Vérification du tableau après préparation des colonnes", includeTableRequests: true);
        while (activity.CanWait)
        {
            ct.ThrowIfCancellationRequested();
            if (original.Page is null || original.Page.IsClosed || Origin(original.Page.Url) != original.TopOrigin)
                throw new InvalidOperationException("L’onglet GLPI a changé pendant la préparation des colonnes.");
            try
            {
                var refreshed = await FindCurrentColumnTableAsync(original, minimumColumns, ct);
                if (refreshed is not null)
                {
                    activity.Observe(refreshed.Selector + "|" + refreshed.HeaderFingerprint + "|" + refreshed.RowCount);
                    var window = await InspectColumnsAsync(refreshed.Frame!, refreshed.FrameOrigin, refreshed.Selector, ct);
                    if (window.TableSelector.Length > 0 && window.IframeSelector.Length == 0) return refreshed;
                }
            }
            catch (Exception error) when (error is PlaywrightException or TimeoutException)
            { /* Native close may be replacing the frame document; reread its current state. */ }
            await Task.Delay(120, ct);
        }
        throw new InvalidOperationException("Le tableau ne progresse plus vers une vue exploitable après la préparation. Le relevé précédent est conservé ; vérifier GLPI et relancer.");
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
        using var activity = new ColumnPreparationActivity(table.Page!, frame, table.FrameOrigin, timeout, Report);
        try
        {
            var opened = await OpenColumnPreferencesAsync(table, activity, ct);
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
                        preferences = (await SubmitColumnControlAsync(opened.Frame, table.FrameOrigin, preferences, null, activity, CheckPreparationScopeAsync, ct)).Preferences;
                    }
                    var initiallyOffered = ColumnPreparationPolicy.Available(preferences).Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
                    while (ColumnPreparationPolicy.Next(mode, original.Headers, preferences, settings.Dashboard, satisfied) is { } option)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!initiallyOffered.Contains(option.Value))
                            throw new ColumnOptionUnavailableException("La liste des options de colonne a changé pendant la préparation ; aucun ajout n’a été envoyé pour cette nouvelle option.");
                        Report($"Ajout de colonne {added.Count + 1:N0} · {ColumnPreparationPolicy.Header(option)}");
                        await CheckPreparationScopeAsync();
                        var command = await SubmitColumnControlAsync(opened.Frame, table.FrameOrigin, preferences, option, activity, CheckPreparationScopeAsync, ct);
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
            // This also covers an unavailable tab before the first personal
            // mutation: closing must not erase unsent changed search criteria.
            await CheckPreparationScopeAsync();
            await CloseColumnPreferencesAsync(table, activity, CheckPreparationScopeAsync, ct, waitForRefresh: true);
            table = await RefreshPreparedTableAsync(original, before.Headers.Count + added.Count, activity, ct);
            if (opened.Frame is not null && preferences.Layout == "modern" && satisfied.Count > 0)
            {
                // GLPI 11 updates its DOM optimistically and can reuse the iframe.
                // Reopen, then read its already observed native GET URL normally;
                // a visible optimistic list alone never proves persistence.
                var persisted = await OpenColumnPreferencesAsync(table, activity, ct);
                if (persisted.Frame is null) throw new InvalidOperationException("La vue personnelle ne peut pas être relue pour confirmer les colonnes ajoutées.");
                await using var iframe = await persisted.Frame.FrameElementAsync().WaitAsync(ct);
                var observedUrl = await iframe.EvaluateAsync<string>("element => element.src").WaitAsync(ct);
                if (!Uri.TryCreate(observedUrl, UriKind.Absolute, out var nativeUrl) || Origin(observedUrl) != table.FrameOrigin
                    || nativeUrl.UserInfo.Length > 0 || !nativeUrl.AbsolutePath.EndsWith("/front/displaypreference.form.php", StringComparison.Ordinal)
                    || nativeUrl.Query.TrimStart('?') != "itemtype=Ticket")
                    throw new InvalidOperationException("L’adresse native de relecture des colonnes n’est pas vérifiable. Aucun relevé complet n’est publié.");
                activity.BeginPhase("Relecture des colonnes enregistrées par GLPI");
                await activity.RunAndWaitForResponseAsync(
                    async () => { await persisted.Frame.GotoAsync(observedUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 0 }).WaitAsync(ct); },
                    response => response.Request.Method == "GET" && response.Request.Frame == persisted.Frame && response.Url == observedUrl, ct);
                var confirmation = await OpenColumnPreferencesAsync(table, activity, ct);
                if (confirmation.Frame != persisted.Frame || !PersonalColumnScope(confirmation.Preferences) || confirmation.Preferences.UsersId != preferences.UsersId
                    || !confirmation.Preferences.SelectedIdsKnown || !SelectedColumnsPreserved(preferences, confirmation.Preferences))
                    throw new InvalidOperationException("Les colonnes ajoutées ne sont pas confirmées après relecture de la vue personnelle GLPI. Aucun relevé complet n’est publié.");
                await CheckPreparationScopeAsync();
                await CloseColumnPreferencesAsync(table, activity, CheckPreparationScopeAsync, ct, waitForRefresh: true);
                table = await RefreshPreparedTableAsync(table, table.Headers.Count, activity, ct);
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
            activity.ThrowIfFailed();
            return new(table, receipt);
        }
        catch (OperationCanceledException)
        {
            await IsolateColumnPreparationAsync(original.Page!);
            throw;
        }
        catch (TimeoutException)
        {
            await IsolateColumnPreparationAsync(original.Page!);
            // SafeError unwraps inner exceptions. Keep the actual column phase
            // in this public error, without leaking a stale locator timeout.
            throw new InvalidOperationException("La commande native n’a pas pu être confirmée pendant « " + activity.Phase
                + " ». Aucun ajout n’est relancé automatiquement ; vérifier la vue personnelle dans GLPI puis relancer la collecte.");
        }
        catch
        {
            if (activity.HasPendingRequest) await IsolateColumnPreparationAsync(original.Page!);
            throw;
        }
    }

    private async Task IsolateColumnPreparationAsync(IPage source)
    {
        // A cancelled Playwright action can still deliver its native request.
        // Retire its page before allowing the next collection to register any
        // observer. Closing is bounded and never conceals the original error.
        RetireNativeExportPageFamily(source);
        Log?.Invoke("COLONNES · tentative interrompue isolée ; la prochaine collecte utilisera un nouvel onglet.");
        var cleanup = CloseNativeExportPagesAsync(source, _context?.Pages.ToArray() ?? Array.Empty<IPage>());
        _ = cleanup.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception error) when (error is TimeoutException or PlaywrightException)
        { Log?.Invoke("COLONNES · fermeture encore en attente ; cette tentative reste exclue des prochaines collectes."); }
    }
}
