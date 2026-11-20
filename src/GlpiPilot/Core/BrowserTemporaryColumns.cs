using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private readonly TemporaryColumnJournalStore _temporaryColumnJournal = new();
    private TemporaryColumnJournalRecord? _temporaryColumns;
    private AppSettings? _temporaryColumnSettings;
    private bool _temporaryRestorationDispatched;
    private sealed class ColumnBackupUnavailableException(string message) : InvalidOperationException(message);
    private sealed class ColumnRecoveryRefusal(string state, string message) : InvalidOperationException(message)
    { public string State { get; } = state; }
    private sealed record ColumnContextEvidence(string Fingerprint, bool Observed);

    private static bool NoTemporaryColumnCommand(TemporaryColumnJournalRecord record) => record.State == "prepared"
        && record.PendingCommand is null && record.ActivationBaseline is null && record.Added.Count == 0
        && TemporaryColumnPolicy.Same(record.Original, record.Expected);

    public bool HasPendingTemporaryColumns(AppSettings settings)
    {
        try { return _temporaryColumnJournal.Load(settings) is { State: not "restored" }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return _temporaryColumnJournal.Exists(settings); }
    }

    // Pagination can reconnect inside a live collection. It must not restore
    // that collection's columns halfway through its export.
    public bool NeedsTemporaryColumnRecovery(AppSettings settings) =>
        !(_temporaryColumns is { } active && active.InstallationKey == ObjectIdentity.InstallationKey(settings.PortalUrl)
            && active.BrowserChannel == settings.BrowserChannel) && HasPendingTemporaryColumns(settings);

    private static async Task<ColumnContextEvidence> ReadColumnContextAsync(IFrame frame, CancellationToken ct)
    {
        var json = await frame.EvaluateAsync<string>("""
            () => {
              const controls = [...document.querySelectorAll('input[name],select[name]')]
                .filter(e => /^(active_entity|entities_id|newprofile|profiles_id)$/.test(e.name));
              if (controls.length > 500) return '';
              return JSON.stringify(controls.filter(e => !['checkbox','radio'].includes(e.type) || e.checked)
                .map(e => [e.name, e.tagName === 'SELECT' ? [...e.selectedOptions].map(o => o.value).sort().join('|') : e.value])
                .sort((a,b) => JSON.stringify(a).localeCompare(JSON.stringify(b))));
            }
            """).WaitAsync(ct);
        if (json.Length is 0 or > 128000)
            throw new ColumnRecoveryRefusal("context-unverifiable", "Le contexte observable des préférences ne peut pas être contrôlé. Aucune commande de colonnes n’est envoyée.");
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), json != "[]");
    }

    private async Task ValidateTemporaryColumnContextAsync(IFrame frame, TemporaryColumnJournalRecord record, CancellationToken ct)
    {
        CheckColumnFrame(frame, record.Origin, ct);
        var current = await ReadColumnContextAsync(frame, ct);
        if (current.Observed != record.ContextObserved || current.Fingerprint != record.ContextFingerprint)
            throw new ColumnRecoveryRefusal("context-changed", "Le profil ou l’entité observable des préférences a changé. Le journal est conservé ; aucune autre commande n’est envoyée.");
    }

    private async Task BeginTemporaryColumnsAsync(TableChoice table, IFrame frame, NativeColumnPreferences preferences,
        BrowserScopeEvidence sourceScope, AppSettings settings, CancellationToken ct)
    {
        if (_temporaryColumns is not null || HasPendingTemporaryColumns(settings))
            throw new InvalidOperationException("Une préparation temporaire de colonnes est déjà ouverte. La restaurer avant toute nouvelle préparation.");
        if (!PersonalColumnScope(preferences) || !preferences.RestorationSupported)
            throw new InvalidOperationException("Les préférences personnelles ne permettent pas une sauvegarde et un retour natifs vérifiables.");
        await using var iframe = await frame.FrameElementAsync().WaitAsync(ct);
        var observedUrl = await iframe.EvaluateAsync<string>("element => element.src").WaitAsync(ct);
        if (!TemporaryColumnJournalStore.IsObservedPreferencesUrl(observedUrl, table.FrameOrigin))
            throw new InvalidOperationException("L’adresse native des préférences ne peut pas être sauvegardée pour la restauration. Aucun ajout n’est envoyé.");
        var context = await ReadColumnContextAsync(frame, ct);
        var original = TemporaryColumnPolicy.Capture(preferences);
        var record = new TemporaryColumnJournalRecord
        {
            InstallationKey = ObjectIdentity.InstallationKey(settings.PortalUrl), BrowserChannel = settings.BrowserChannel,
            Origin = table.FrameOrigin, PreferencesUrl = observedUrl, UsersId = preferences.UsersId, Layout = preferences.Layout,
            SourceScopeFingerprint = sourceScope.Fingerprint, ContextFingerprint = context.Fingerprint, ContextObserved = context.Observed,
            Original = original, Expected = TemporaryColumnPolicy.Capture(preferences)
        };
        ct.ThrowIfCancellationRequested();
        _temporaryColumnJournal.Create(settings, record);
        _temporaryColumnSettings = settings.Copy();
        _temporaryColumns = record;
        Log?.Invoke("COLONNES · sauvegarde initiale durable confirmée (état personnel, identifiants et ordre) ; "
            + (context.Observed ? "contexte profil/entité observable mémorisé." : "profil/entité non exposés par ce formulaire ; aucune preuve de contexte supplémentaire n’est revendiquée."));
    }

    private void RecordTemporaryColumnIntent(NativeColumnPreferences before, string kind, NativeColumnOption? option = null,
        string identity = "")
    {
        var record = _temporaryColumns ?? throw new InvalidOperationException("La sauvegarde initiale des colonnes est absente. Aucune commande n’est envoyée.");
        var settings = _temporaryColumnSettings ?? throw new InvalidOperationException("Le contexte du journal des colonnes est absent.");
        if (!PersonalColumnScope(before) || before.UsersId != record.UsersId || before.Layout != record.Layout
            || record.PendingCommand is not null)
            throw new InvalidOperationException("Une commande personnelle n’est pas confirmée ou la vue a changé. Aucun nouvel envoi n’est autorisé.");
        var current = TemporaryColumnPolicy.Capture(before);
        if (!record.Original.PersonalActive && !TemporaryColumnPolicy.Same(record.Expected, current))
            throw new ColumnRecoveryRefusal("concurrent-change", "La vue temporaire créée par Pilot a été modifiée ailleurs. Aucun ajout ni effacement global n’est envoyé.");
        if (NoTemporaryColumnCommand(record))
        {
            // A concurrent append before our first command belongs to the
            // initial personal view. Save its latest exact state in the same
            // durable transaction as the first intent, before any click.
            record.Original = TemporaryColumnPolicy.Capture(before);
            record.Expected = TemporaryColumnPolicy.Capture(before);
        }
        record.PendingCommand = new()
        {
            Kind = kind, OptionValue = option?.Value ?? "", OptionLabel = option?.Label ?? "",
            OptionHeader = option is null ? "" : ColumnPreparationPolicy.Header(option), TargetIdentity = identity, Before = current
        };
        record.State = kind is "remove" or "disable" ? "restoring" : "active";
        _temporaryColumnJournal.Save(settings, record);
    }

    private void RecordTemporaryColumnResponse()
    {
        if (_temporaryColumns?.PendingCommand is not { } command || _temporaryColumnSettings is null)
            throw new InvalidOperationException("La réponse des colonnes ne correspond plus à son journal.");
        command.ResponseObserved = true;
        _temporaryColumnJournal.Save(_temporaryColumnSettings, _temporaryColumns);
    }

    private void ConfirmTemporaryColumnCommand(NativeColumnPreferences after)
    {
        var record = _temporaryColumns ?? throw new InvalidOperationException("Le journal des colonnes est absent.");
        var command = record.PendingCommand ?? throw new InvalidOperationException("La commande des colonnes n’est pas journalisée.");
        if (!PersonalColumnScope(after) || after.UsersId != record.UsersId || after.Layout != record.Layout)
            throw new InvalidOperationException("La confirmation ne correspond pas à la vue personnelle sauvegardée.");
        var current = TemporaryColumnPolicy.Capture(after);
        if (command.Kind == "activate")
        {
            if (command.Before.PersonalActive || !after.PersonalActive)
                throw new InvalidOperationException("L’activation temporaire ne peut pas être confirmée.");
            record.ActivationBaseline = current;
        }
        else if (command.Kind == "add")
        {
            var added = TemporaryColumnPolicy.ConfirmAdded(command, after)
                ?? throw new InvalidOperationException("L’ajout ne peut pas être rattaché à un unique identifiant de préférence. Aucun ajout suivant n’est envoyé ; le journal reste disponible.");
            if (!record.Added.Any(column => column.Identity == added.Identity)) record.Added.Add(added);
        }
        else if (command.Kind == "remove")
        {
            if (!TemporaryColumnPolicy.RemovedOnly(command.Before, current, command.TargetIdentity, !record.Original.PersonalActive))
                throw new ColumnRecoveryRefusal("concurrent-change", "Le retrait ne correspond pas uniquement à la colonne temporaire attendue. La restauration s’arrête pour préserver la vue courante.");
        }
        else if (command.Kind == "disable" && after.PersonalActive)
            throw new InvalidOperationException("Le retour à la vue globale initiale n’est pas confirmé.");
        record.Expected = current;
        record.PendingCommand = null;
        _temporaryColumnJournal.Save(_temporaryColumnSettings!, record);
    }

    public async Task<ColumnRestorationResult> RestoreTemporaryColumnsAsync(AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        // The caller supplies an independent cleanup token, including after a
        // cancelled collection. The durable journal survives this method too.
        IPage? recoveryPage = null;
        try
        {
            var effective = _temporaryColumnSettings ?? settings;
            var record = _temporaryColumnJournal.Load(effective);
            if (record is null || record.State == "restored") return new("not-needed", "Aucune restauration journalisée de colonnes en attente.");
            if (NoTemporaryColumnCommand(record))
            {
                _temporaryColumnJournal.Complete(effective, record);
                return new("not-needed", "Aucune commande de colonnes n’a été envoyée ; la sauvegarde sans modification est clôturée.");
            }
            if (_context is null) return ColumnRecoveryPending("browser-unavailable", "La session GLPI est fermée. La restauration des colonnes sera vérifiée à la prochaine connexion.", progress);
            recoveryPage = await NewCollectionPageAsync(_context);
            return await RestoreTemporaryColumnsOnPageAsync(recoveryPage, record, effective, progress, ct, allowAuthentication: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlaywrightException or TimeoutException or OperationCanceledException)
        { return ColumnRecoveryFailure(error, progress); }
        finally
        {
            var uncertainMutation = _temporaryRestorationDispatched && _temporaryColumns?.PendingCommand is not null;
            _temporaryColumns = null; _temporaryColumnSettings = null; _temporaryRestorationDispatched = false;
            if (recoveryPage is { IsClosed: false })
            {
                if (uncertainMutation) RetireNativeExportPageFamily(recoveryPage);
                try { await recoveryPage.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (error is PlaywrightException or TimeoutException) { }
            }
        }
    }

    public async Task<ColumnRestorationResult> RecoverTemporaryColumnsAsync(IPage authenticatedPage, AppSettings settings,
        IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        if (!NeedsTemporaryColumnRecovery(settings)) return new("not-needed", "Aucune récupération de colonnes en attente.");
        try
        {
            var record = _temporaryColumnJournal.Load(settings);
            if (record is null || record.State == "restored") return new("not-needed", "Aucune récupération de colonnes en attente.");
            if (NoTemporaryColumnCommand(record))
            {
                _temporaryColumnJournal.Complete(settings, record);
                return new("not-needed", "La sauvegarde ne contient aucune commande envoyée ; aucune préférence GLPI n’est modifiée.");
            }
            return await RestoreTemporaryColumnsOnPageAsync(authenticatedPage, record, settings, progress, ct, allowAuthentication: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlaywrightException or TimeoutException or OperationCanceledException)
        {
            if (_temporaryRestorationDispatched && _temporaryColumns?.PendingCommand is not null)
                await IsolateColumnPreparationAsync(authenticatedPage);
            return ColumnRecoveryFailure(error, progress);
        }
        finally { _temporaryColumns = null; _temporaryColumnSettings = null; _temporaryRestorationDispatched = false; }
    }

    private ColumnRestorationResult ColumnRecoveryPending(string state, string message, IProgress<ExtractionProgress>? progress, int restored = 0)
    {
        Log?.Invoke("RESTAURATION COLONNES · " + state + " · " + AppPaths.SafeLog(message));
        progress?.Report(new(0, 0, null, message));
        return new(state, message, true, restored);
    }

    private ColumnRestorationResult ColumnRecoveryFailure(Exception error, IProgress<ExtractionProgress>? progress)
    {
        var state = error is ColumnRecoveryRefusal refusal ? refusal.State
            : error is OperationCanceledException or TimeoutException ? "recovery-pending"
            : error is IOException or UnauthorizedAccessException ? "journal-unavailable" : "recovery-pending";
        var message = error is ColumnRecoveryRefusal ? error.Message
            : error is OperationCanceledException or TimeoutException ? "La restauration des colonnes n’est pas confirmée. Son journal est conservé pour la prochaine connexion ; aucune commande n’est rejouée."
            : "La restauration des colonnes reste en attente. " + AppPaths.SafeError(error);
        return ColumnRecoveryPending(state, message, progress);
    }

    private async Task<NativeColumnPreferences> OpenRecoveryPreferencesAsync(IPage page, TemporaryColumnJournalRecord record,
        AppSettings settings, Action<string> report, bool allowAuthentication, CancellationToken ct)
    {
        if (page.IsClosed || page.Context != _context)
            throw new ColumnRecoveryRefusal("browser-unavailable", "Le navigateur de récupération n’appartient pas à la session GLPI courante.");
        report("Restauration des colonnes avant la liste des tickets · ouverture des préférences sauvegardées…");
        var navigation = page.GotoAsync(record.PreferencesUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 15000 });
        _ = navigation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await navigation.WaitAsync(ct); }
        catch (TimeoutException) { /* A slow native GET is observed below; no repeat is dispatched. */ }
        var wait = Stopwatch.StartNew();
        var tabRequested = false;
        var returnedToPreferences = false;
        var authenticationSeen = false;
        var loginRefreshes = 0;
        var lastNotice = TimeSpan.FromSeconds(-5);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(allowAuthentication ? settings.TimeoutSeconds : settings.ListTimeoutSeconds, 5, 600));
        while (wait.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed) throw new ColumnRecoveryRefusal("browser-unavailable", "La fenêtre GLPI a été fermée pendant la récupération.");
            var nativeDocument = Origin(page.Url) == record.Origin && Uri.TryCreate(page.Url, UriKind.Absolute, out var address)
                && address.AbsolutePath == new Uri(record.PreferencesUrl).AbsolutePath;
            if (nativeDocument)
            {
                NativeColumnPreferences preferences;
                try { preferences = await InspectColumnPreferencesAsync(page.MainFrame, record.Origin, ct); }
                catch (PlaywrightException) { await Task.Delay(120, ct); continue; }
                if (PersonalColumnScope(preferences))
                {
                    if (authenticationSeen) await ResumeAfterAuthenticationAsync(ct);
                    return preferences;
                }
                if (!tabRequested && PersonalColumnTabAvailable(preferences))
                {
                    await SelectRecoveryPersonalTabAsync(page.MainFrame, record.Origin, preferences, ct);
                    tabRequested = true;
                    continue;
                }
                var loading = await page.MainFrame.EvaluateAsync<bool>("""
                    () => { const body=document.querySelector('#tabsbody'); return document.readyState==='loading' || !body ||
                      body.getAttribute('aria-busy')==='true' || !body.querySelector('input[name="itemtype"],input[name="users_id"],form,button,select') && !body.textContent.trim(); }
                    """).WaitAsync(ct);
                if (!loading && !tabRequested)
                    throw new ColumnRecoveryRefusal("restoration-unsupported", "Les préférences personnelles de tickets ne sont pas disponibles dans ce contexte. Le journal est conservé.");
            }
            else
            {
                var probe = await ProbeFrameAsync(page.MainFrame, settings, ct);
                if (probe?.Login == true || probe?.Challenge == true || IsAuthenticationUrl(page.Url) || Origin(page.Url) != record.Origin)
                {
                    authenticationSeen = true;
                    if (!allowAuthentication)
                        throw new ColumnRecoveryRefusal("authentication-required", "La session doit être reconnectée pour restaurer les colonnes. La récupération reprendra avant la prochaine liste de tickets.");
                    if (probe?.Challenge == true)
                    {
                        if (wait.Elapsed - lastNotice >= TimeSpan.FromSeconds(5))
                        {
                            report("La restauration attend la connexion GLPI dans le navigateur.");
                            lastNotice = wait.Elapsed;
                        }
                        await RevealAuthenticationAsync(page, ct);
                    }
                    if (await AuthenticationPopupAsync(page, settings, ct) is { } popup)
                        await RevealAuthenticationAsync(popup, ct);
                    if (Origin(page.Url) == record.Origin && probe is { Login: true, Challenge: false }
                        && loginRefreshes < 2 && wait.Elapsed.TotalSeconds >= (loginRefreshes == 0 ? 8 : 20))
                    {
                        loginRefreshes++;
                        await page.GotoAsync(record.PreferencesUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 15000 }).WaitAsync(ct);
                    }
                }
                else if (!returnedToPreferences && probe is { Login: false, Challenge: false, Authenticated: true })
                {
                    // The login can land on a home page. Reenter only the
                    // already observed preferences GET, never the heavy list.
                    returnedToPreferences = true;
                    await page.GotoAsync(record.PreferencesUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 15000 }).WaitAsync(ct);
                }
                else if (probe is { Login: false, Challenge: false } && !authenticationSeen)
                    throw new ColumnRecoveryRefusal("origin-changed", "L’adresse des préférences a été redirigée hors du document attendu. Aucune colonne n’est modifiée.");
            }
            await Task.Delay(200, ct);
        }
        throw new ColumnRecoveryRefusal(authenticationSeen ? "authentication-required" : "recovery-pending",
            authenticationSeen ? "La connexion nécessaire à la récupération des colonnes n’est pas encore confirmée. Le journal est conservé."
                : "Le formulaire de préférences ne peut pas encore être relu. Le journal est conservé, sans effacement fondé sur la lenteur de GLPI.");
    }

    private async Task SelectRecoveryPersonalTabAsync(IFrame frame, string origin, NativeColumnPreferences before, CancellationToken ct)
    {
        var role = before.PersonalTabSelector.Length > 0 ? "personal-tab" : "personal-tab-select";
        await using var capture = await CaptureColumnPreferencesAsync(frame, origin, role, ct);
        await using var control = await capture.ElementAsync(ct);
        var ready = control is not null && await ColumnControlReadyAsync(control, ct);
        var current = await capture.ValidateAsync(ct);
        if (PersonalColumnScope(current.State)) return;
        if (!current.SameElement || control is null || !PersonalColumnTabAvailable(current.State)
            || !ready)
            throw new ColumnRecoveryRefusal("restoration-unsupported", "L’onglet personnel n’est pas vérifiable avant la récupération. Aucune commande de colonnes n’est envoyée.");
        try
        {
            if (role == "personal-tab") await control.ClickAsync(new() { Timeout = 7000 }).WaitAsync(ct);
            else await control.SelectOptionAsync(new SelectOptionValue { Value = current.State.PersonalTabValue }, new() { Timeout = 5000 }).WaitAsync(ct);
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        { Log?.Invoke("RESTAURATION COLONNES · onglet personnel demandé ; relecture sans nouveau clic."); }
    }

    private async Task<NativeColumnPreferences> ReadRecoveryPreferencesAsync(IFrame frame, TemporaryColumnJournalRecord record,
        ColumnPreparationActivity activity, CancellationToken ct, bool reload)
    {
        activity.BeginPhase(reload ? "Relecture persistée des préférences personnelles" : "Lecture des préférences personnelles");
        if (reload)
            await activity.RunAndWaitForResponseAsync(async () =>
                { await frame.GotoAsync(record.PreferencesUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 0 }).WaitAsync(ct); },
                response => response.Request.Method == "GET" && response.Request.Frame == frame && response.Url == record.PreferencesUrl, ct);
        var tabRequested = false;
        while (activity.CanWait)
        {
            ct.ThrowIfCancellationRequested();
            NativeColumnPreferences preferences;
            try { preferences = await InspectColumnPreferencesAsync(frame, record.Origin, ct); }
            catch (PlaywrightException) { await Task.Delay(120, ct); continue; }
            activity.Observe(preferences.Proof + "|" + preferences.UsersId + "|" + preferences.PersonalActive + "|"
                + string.Join(',', preferences.Selected.Select(ColumnIdentity)));
            if (PersonalColumnScope(preferences) && !activity.HasPendingRequest)
            {
                await ValidateRecoveryPreferencesAsync(frame, record, preferences, ct);
                return preferences;
            }
            if (!tabRequested && PersonalColumnTabAvailable(preferences))
            {
                await SelectRecoveryPersonalTabAsync(frame, record.Origin, preferences, ct);
                tabRequested = true;
            }
            await Task.Delay(120, ct);
        }
        throw new ColumnRecoveryRefusal("recovery-pending", "La vue personnelle n’est pas disponible pour confirmer la restauration. Le journal est conservé.");
    }

    private async Task ValidateRecoveryPreferencesAsync(IFrame frame, TemporaryColumnJournalRecord record,
        NativeColumnPreferences preferences, CancellationToken ct)
    {
        ValidateRecoveryIdentity(frame, record, preferences);
        await ValidateTemporaryColumnContextAsync(frame, record, ct);
    }

    private static void ValidateRecoveryIdentity(IFrame frame, TemporaryColumnJournalRecord record, NativeColumnPreferences preferences)
    {
        if (!PersonalColumnScope(preferences) || preferences.UsersId != record.UsersId || preferences.Layout != record.Layout)
            throw new ColumnRecoveryRefusal("identity-changed", "Le compte ou la vue personnelle ne correspond pas à la sauvegarde. Aucune préférence de ce compte n’est modifiée.");
        if (!Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri) || uri.AbsolutePath != new Uri(record.PreferencesUrl).AbsolutePath)
            throw new ColumnRecoveryRefusal("origin-changed", "Le document des préférences a changé. La restauration est arrêtée.");
    }

    private bool ReconcileTemporaryColumnCommand(TemporaryColumnJournalRecord record, NativeColumnPreferences preferences)
    {
        if (record.PendingCommand is not { } command) return true;
        var current = TemporaryColumnPolicy.Capture(preferences);
        if (command.Kind == "add" && TemporaryColumnPolicy.ConfirmAdded(command, preferences) is not null
            || command.Kind == "remove" && TemporaryColumnPolicy.RemovedOnly(command.Before, current, command.TargetIdentity, !record.Original.PersonalActive)
            || command.Kind == "disable" && !current.PersonalActive)
        {
            ConfirmTemporaryColumnCommand(preferences);
            Log?.Invoke("RESTAURATION COLONNES · effet de la commande interrompue confirmé par une nouvelle lecture native ; aucun envoi rejoué.");
            return true;
        }
        if (command.Kind is "add" or "activate" && TemporaryColumnPolicy.Same(command.Before, current) && command.ResponseObserved)
        {
            record.Expected = current; record.PendingCommand = null;
            _temporaryColumnJournal.Save(_temporaryColumnSettings!, record);
            Log?.Invoke("RESTAURATION COLONNES · réponse terminée mais aucun effet persisté ; la commande n’est pas rejouée.");
            return true;
        }
        // An interrupted activation has no post-activation baseline. An
        // unrelated personal view cannot be attributed to Pilot merely because
        // it is now active. Likewise a still-unanswered POST is not retried.
        return false;
    }

    private async Task<ColumnRestorationResult> RestoreTemporaryColumnsOnPageAsync(IPage page, TemporaryColumnJournalRecord record,
        AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct, bool allowAuthentication)
    {
        void Report(string message)
        {
            Log?.Invoke("RESTAURATION COLONNES · " + AppPaths.SafeLog(message));
            progress?.Report(new(0, 0, null, message));
            if (allowAuthentication) ConnectionProgress?.Invoke(message);
        }
        _temporaryColumns = record; _temporaryColumnSettings = settings.Copy();
        var preferences = await OpenRecoveryPreferencesAsync(page, record, settings, Report, allowAuthentication, ct);
        await ValidateRecoveryPreferencesAsync(page.MainFrame, record, preferences, ct);
        using var activity = new ColumnPreparationActivity(page, page.MainFrame, record.Origin, ColumnCleanupBudgetSeconds(settings.ListTimeoutSeconds), Report, directPreferences: true);
        // A second native GET also excludes a reused optimistic document and
        // gives all recovery decisions a server-rendered starting point.
        preferences = await ReadRecoveryPreferencesAsync(page.MainFrame, record, activity, ct, reload: true);
        if (!ReconcileTemporaryColumnCommand(record, preferences))
            return ColumnRecoveryPending("outcome-unknown", "Une commande précédente n’a pas d’effet attribuable avec certitude. Aucun ajout ni retrait n’est rejoué ; la sauvegarde reste disponible.", progress);
        var restored = 0;
        var current = TemporaryColumnPolicy.Capture(preferences);
        if (TemporaryColumnPolicy.OnlyOwnEntriesMissing(record, current))
        {
            record.Expected = current;
            _temporaryColumnJournal.Save(settings, record);
        }
        if (!current.PersonalActive)
        {
            // A user may already have removed the personal view. Do not
            // recreate it just to impose the old layout.
            _temporaryColumnJournal.Complete(settings, record);
            var state = record.Original.PersonalActive ? "restored-preserving-changes" : "restored";
            var message = record.Original.PersonalActive ? "La vue personnelle a déjà été supprimée dans GLPI ; ce changement est conservé et aucun ajout temporaire ne subsiste."
                : "La vue globale initiale est confirmée ; aucune colonne temporaire ne subsiste.";
            Report(message); return new(state, message);
        }
        if (!record.Original.PersonalActive && !TemporaryColumnPolicy.Same(record.Expected, current))
            return ColumnRecoveryPending("concurrent-change", "La vue créée temporairement a été modifiée ailleurs. Elle n’est pas désactivée ; le journal reste disponible.", progress);
        foreach (var added in record.Added.AsEnumerable().Reverse().ToArray())
        {
            current = TemporaryColumnPolicy.Capture(preferences);
            var present = current.Columns.SingleOrDefault(column => column.Identity == added.Identity);
            if (present is null) continue; // A concurrent removal needs no replay.
            if (!TemporaryColumnPolicy.OwnEntryUnchanged(record, present))
                return ColumnRecoveryPending("concurrent-change", "Une colonne temporaire a changé d’identité ou de libellé. Elle est conservée pour éviter d’effacer une modification concurrente.", progress, restored);
            // Legacy purge targets one exact preference ID. Modern removal
            // submits the entire native order: do not overwrite an observable
            // concurrent reorder/insert with that broader command.
            if (record.Layout == "modern" && !TemporaryColumnPolicy.Same(record.Expected, current))
                return ColumnRecoveryPending("concurrent-change", "L’ordre ou les colonnes ont changé depuis la collecte. La commande native qui réécrit cette liste est suspendue pour conserver ces changements.", progress, restored);
            preferences = await SubmitTemporaryColumnRestoreAsync(page.MainFrame, record, preferences, "remove", added.Identity, activity, ct);
            restored++;
        }
        current = TemporaryColumnPolicy.Capture(preferences);
        if (!record.Original.PersonalActive)
        {
            if (TemporaryColumnPolicy.ActivationCopyForDisable(record, preferences) is { } activationCopy)
            {
                // Persist ownership of this exact activation copy before its
                // single native removal. No unknown row or global preference
                // is cleared to reveal the native disable command.
                if (!record.Added.Any(column => column.Identity == activationCopy.Identity)) record.Added.Add(activationCopy);
                _temporaryColumnJournal.Save(settings, record);
                Report("Réaffichage du retour natif : retrait d’une copie personnelle créée par l’activation temporaire confirmée.");
                preferences = await SubmitTemporaryColumnRestoreAsync(page.MainFrame, record, preferences, "remove", activationCopy.Identity, activity, ct);
                restored++;
                current = TemporaryColumnPolicy.Capture(preferences);
            }
            if (current.PersonalActive)
            {
                if (!TemporaryColumnPolicy.CanDisable(record, current))
                    return ColumnRecoveryPending("concurrent-change", "Le retour à la vue globale initiale ne peut pas être prouvé sans effacer une modification concurrente. Le journal reste disponible.", progress, restored);
                preferences = await SubmitTemporaryColumnRestoreAsync(page.MainFrame, record, preferences, "disable", "", activity, ct);
            }
            current = TemporaryColumnPolicy.Capture(preferences);
        }
        var preservedChanges = !TemporaryColumnPolicy.Same(record.Original, current);
        _temporaryColumnJournal.Complete(settings, record);
        var finalMessage = preservedChanges
            ? $"{restored:N0} colonnes temporaires retirées ; les autres modifications observées dans GLPI ont été conservées."
            : $"Vue GLPI initiale restaurée et relue · {restored:N0} colonnes temporaires retirées.";
        Report(finalMessage);
        return new(preservedChanges ? "restored-preserving-changes" : "restored", finalMessage, false, restored);
    }

    private async Task<NativeColumnPreferences> SubmitTemporaryColumnRestoreAsync(IFrame frame, TemporaryColumnJournalRecord record,
        NativeColumnPreferences before, string kind, string identity, ColumnPreparationActivity activity, CancellationToken ct)
    {
        var role = kind == "disable" ? "disable" : "remove:" + identity;
        activity.BeginPhase(kind == "disable" ? "Retour à la vue globale initiale" : "Retrait d’une colonne temporaire identifiée");
        var fresh = before;
        var response = await activity.RunAndWaitForResponseAsync(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await using var capture = await CaptureColumnPreferencesAsync(frame, record.Origin, role, ct);
                await using var control = await capture.ElementAsync(ct);
                await ValidateRecoveryPreferencesAsync(frame, record, capture.State, ct);
                var ready = control is not null && await ColumnControlReadyAsync(control, ct);
                await ValidateTemporaryColumnContextAsync(frame, record, ct);
                var validated = await capture.ValidateAsync(ct);
                ValidateRecoveryIdentity(frame, record, validated.State);
                fresh = validated.State;
                var observed = TemporaryColumnPolicy.Capture(fresh);
                if (!TemporaryColumnPolicy.Same(TemporaryColumnPolicy.Capture(before), observed))
                    throw new ColumnRecoveryRefusal("concurrent-change", "Les préférences ont changé avant le retrait natif. Aucune nouvelle commande n’est envoyée.");
                if (kind == "disable" ? !TemporaryColumnPolicy.CanDisable(record, observed) || fresh.DisableSelector.Length == 0
                    : !observed.Columns.Any(column => column.Identity == identity && TemporaryColumnPolicy.OwnEntryUnchanged(record, column)))
                    throw new ColumnRecoveryRefusal("restoration-unsupported", "Le retour natif demandé n’est pas vérifiable dans cette vue personnelle. Le journal est conservé.");
                if (!validated.SameElement || !ready)
                {
                    if (attempt == 2) throw new ColumnRecoveryRefusal("restoration-unsupported", "Le contrôle natif de restauration n’est plus disponible. Aucune commande n’est envoyée.");
                    await Task.Delay(120, ct); continue;
                }
                RecordTemporaryColumnIntent(fresh, kind, identity: identity);
                _temporaryRestorationDispatched = true;
                await control!.ClickAsync(new() { Timeout = 7000 }).WaitAsync(ct);
                return;
            }
        }, reply => reply.Request.Method == "POST" && reply.Request.Frame == frame && Origin(reply.Url) == record.Origin
            && new Uri(reply.Url).AbsolutePath.EndsWith(record.Layout == "modern" ? "/ajax/displaypreference.php" : "/front/displaypreference.form.php", StringComparison.Ordinal), ct);
        if (response.Status is < 200 or >= 400)
            throw new ColumnRecoveryRefusal("recovery-pending", "GLPI n’a pas confirmé la commande de restauration. Elle ne sera pas rejouée automatiquement.");
        RecordTemporaryColumnResponse();
        // Read the post-action native view before GET. This lets legacy POST
        // navigation finish and prevents a GET from aborting an outstanding
        // native redirect. The activity observer also waits for its chain.
        _ = await ReadRecoveryPreferencesAsync(frame, record, activity, ct, reload: false);
        var persisted = await ReadRecoveryPreferencesAsync(frame, record, activity, ct, reload: true);
        ConfirmTemporaryColumnCommand(persisted);
        return persisted;
    }
}
