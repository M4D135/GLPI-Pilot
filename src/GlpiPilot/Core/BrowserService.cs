using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private BrowserProfileLease? _profileLease;
    private IBrowserContext? _context;
    private readonly string _bridge;
    private readonly PaginationCheckpointStore _checkpoints;
    private DateTimeOffset _openedAt;
    private string _portalOrigin = "";
    private string _channel = "";
    private string _profileInstallation = "";
    private TaskCompletionSource<IDownload>? _downloadWaiter;
    private IPage? _downloadPage;
    private IPage? _activePage;
    private string _targetUrl = "";
    private readonly ConcurrentDictionary<IPage, DateTimeOffset> _navigationTimes = new();
    private bool _authenticationNavigationObserved;
    private long _pagesRevision;
    public event Action<string>? Log;
    public event Action? PagesChanged;
    public bool IsOpen => _context != null;
    public IPage? ActivePage => _activePage is { IsClosed: false } && !IsTicketDetailPage(_activePage) ? _activePage : null;
    public long PagesRevision => Interlocked.Read(ref _pagesRevision);

    public BrowserService(string? bridge = null, PaginationCheckpointStore? checkpoints = null)
    {
        _checkpoints = checkpoints ?? new PaginationCheckpointStore();
        if (bridge != null) { _bridge = bridge; return; }
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("BrowserScripts.bridge.js", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        _bridge = reader.ReadToEnd();
    }

    public static string Origin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.GetLeftPart(UriPartial.Authority) : "";

    public async Task OpenAsync(AppSettings settings, CancellationToken cancellationToken, bool background = true)
    {
        if (AppPaths.SimulationRoot is not null)
            throw new InvalidOperationException("Un adaptateur de simulation doit traiter cette opération avant toute ouverture du navigateur réel.");
        var uri = TargetUrlPolicy.Validate(settings.PortalUrl);
        var installation = ObjectIdentity.InstallationKey(settings.PortalUrl);
        if (_context != null && (_channel != settings.BrowserChannel || _profileInstallation != installation))
        {
            Log?.Invoke("SOURCE · changement d’installation : fermeture du contexte précédent et ouverture du profil isolé correspondant.");
            await DisposeAsync();
        }
        ResetAuthenticationPresentation();
        SetBackgroundMode(background);

        if (_context == null)
        {
            var packagedDriver = Path.Combine(AppContext.BaseDirectory, "wad");
            if (Directory.Exists(Path.Combine(packagedDriver, ".playwright")))
                Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", packagedDriver);
            _profileLease = BrowserProfileLease.Acquire(AppPaths.Profile(settings.BrowserChannel, settings.PortalUrl));
            Log?.Invoke(background ? "Ouverture du navigateur en arrière-plan avec le profil réservé à GLPI Pilot." : "Ouverture du navigateur avec le profil réservé à GLPI Pilot.");
            if (background) BackgroundLaunchStateChanged?.Invoke(true);
            try
            {
                _playwright ??= await Playwright.CreateAsync();
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(AppPaths.Profile(settings.BrowserChannel, settings.PortalUrl), new()
                {
                    Channel = settings.BrowserChannel == "chrome" ? "chrome" : "msedge",
                    Headless = false,
                    ChromiumSandbox = true,
                    AcceptDownloads = true,
                    ViewportSize = ViewportSize.NoViewport,
                    Timeout = 45000,
                    Args = background ? new[] { "--window-size=1280,900" } : new[] { "--start-maximized" }
                });
                _channel = settings.BrowserChannel;
                _profileInstallation = installation;
                _presentationWarningLogged = false;
                ConfigureContext(_context);
                // Minimize the dedicated window while the short launch guard is held.
                // Other windows are handled after it has been released.
                if (BackgroundMode && _context.Pages.FirstOrDefault(p => !p.IsClosed) is { } initialPage)
                    await SetPageWindowAsync(initialPage, false, cancellationToken);
            }
            catch (PlaywrightException error) when (error.Message.Contains("Singleton", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("profile in use", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Le profil GLPI Pilot est déjà utilisé par un navigateur. Fermez cette fenêtre avant de relancer l’extraction."); }
            catch (PlaywrightException error) when (error.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("distribution 'msedge' is not found", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("distribution 'chrome' is not found", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Le navigateur choisi est introuvable. Installez Edge ou Chrome, ou choisissez celui déjà présent dans Réglages."); }
            finally
            {
                if (_context == null) { _profileLease?.Dispose(); _profileLease = null; }
                if (background) BackgroundLaunchStateChanged?.Invoke(false);
            }
            if (BackgroundMode)
                foreach (var initialPage in _context.Pages.ToArray()) _ = KeepPageInBackgroundAsync(initialPage);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _portalOrigin = Origin(uri.AbsoluteUri);
        _openedAt = DateTimeOffset.UtcNow;
        _targetUrl = uri.AbsoluteUri;
        _authenticationNavigationObserved = false;
        var page = ActivePage ?? _context.Pages.FirstOrDefault(p => !p.IsClosed && !IsTicketDetailPage(p) && TargetUrlPolicy.SamePage(p.Url, _targetUrl))
            ?? _context.Pages.FirstOrDefault(p => !p.IsClosed && !IsTicketDetailPage(p)) ?? await _context.NewPageAsync();
        SetActivePage(page);
        if (BackgroundMode) await SetPageWindowAsync(page, false, cancellationToken);
        else await ShowBrowserAsync(page, cancellationToken);
        await NavigateTargetAsync(page, uri.AbsoluteUri, cancellationToken);
    }

    private void ConfigureContext(IBrowserContext context)
    {
        context.SetDefaultTimeout(10000);
        context.SetDefaultNavigationTimeout(30000);
        context.Close += (_, _) => { if (ReferenceEquals(_context, context)) { _context = null; _activePage = null; _profileLease?.Dispose(); _profileLease = null; } _downloadWaiter?.TrySetException(new InvalidOperationException("Le navigateur a été fermé.")); NotifyPagesChanged(); };
        context.Page += (_, page) => HookPage(page);
        foreach (var page in context.Pages) HookPage(page, initial: true);
    }

    private void HookPage(IPage page, bool initial = false)
    {
        _navigationTimes[page] = DateTimeOffset.UtcNow;
        if (!initial) _ = KeepPageInBackgroundAsync(page);
        page.FrameNavigated += (_, frame) =>
        {
            if (frame != page.MainFrame) return;
            _navigationTimes[page] = DateTimeOffset.UtcNow;
            if (!IsTicketDetailPage(page))
            {
                if (IsAuthenticationUrl(frame.Url)) _authenticationNavigationObserved = true;
                Log?.Invoke("NAV · " + AppPaths.SafeUrl(frame.Url));
            }
            _ = KeepPageInBackgroundAsync(page);
            NotifyPagesChanged();
        };
        page.Close += (_, _) => { _navigationTimes.TryRemove(page, out _); if (ReferenceEquals(_activePage, page)) _activePage = null; NotifyPagesChanged(); };
        NotifyPagesChanged();
        page.Download += async (_, download) =>
        {
            var waiter = _downloadWaiter;
            if (waiter == null || _downloadPage == null) return;
            try
            {
                IPage? ancestor = page;
                for (var depth = 0; ancestor != null && depth < 4; depth++)
                {
                    if (ReferenceEquals(ancestor, _downloadPage)) { waiter.TrySetResult(download); break; }
                    ancestor = await ancestor.OpenerAsync();
                }
            }
            catch (PlaywrightException) { }
        };
    }

    internal void AttachForTesting(IBrowserContext context, string portalUrl)
    {
        _context = context;
        _portalOrigin = Origin(portalUrl);
        _targetUrl = portalUrl;
        _channel = "msedge";
        _profileInstallation = ObjectIdentity.InstallationKey(portalUrl);
        _openedAt = DateTimeOffset.UtcNow;
        ConfigureContext(context);
    }

    private void NotifyPagesChanged() { Interlocked.Increment(ref _pagesRevision); PagesChanged?.Invoke(); }
    private void SetActivePage(IPage page)
    {
        if (IsTicketDetailPage(page)) return;
        if (ReferenceEquals(_activePage, page)) return;
        _activePage = page;
        var source = AppPaths.SafeUrl(page.Url);
        Log?.Invoke(source.Length > 0 ? "ONGLET · suivi de " + source : "ONGLET · nouvel onglet, chargement du portail.");
        NotifyPagesChanged();
    }

    public async Task<List<PageChoice>> GetPagesAsync()
    {
        var pages = new List<PageChoice>();
        if (_context == null) return pages;
        foreach (var page in _context.Pages.ToArray())
        {
            if (page.IsClosed || IsTicketDetailPage(page)) continue;
            try
            {
                var title = await page.TitleAsync();
                pages.Add(new() { Page = page, Title = string.IsNullOrWhiteSpace(title) ? AppPaths.SafeUrl(page.Url) : title, Url = page.Url });
            }
            catch (PlaywrightException) { }
        }
        return pages;
    }

    private async Task EnsureBridgeAsync(IFrame frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await frame.EvaluateAsync(_bridge).WaitAsync(ct);
    }

    private static async Task<T> ReadJsonAsync<T>(IFrame frame, string function, object? argument, CancellationToken ct)
    {
        // Explicit JSON keeps the DOM contract independent of Playwright's object converters.
        var json = await frame.EvaluateAsync<string>("arg => JSON.stringify((" + function + ")(arg))", argument).WaitAsync(ct);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    public async Task<SessionStatus> SessionAsync(IPage? selectedPage, AppSettings settings)
    {
        if (_context == null) return new("Navigateur fermé", false, false);
        if (selectedPage != null && IsTicketDetailPage(selectedPage)) selectedPage = null;
        var selectedProbe = selectedPage is { IsClosed: false } ? await ProbeAsync(selectedPage, settings, CancellationToken.None) : null;
        var page = selectedProbe?.Readable == true ? selectedPage : await FindBestPageAsync(settings, selectedPage, CancellationToken.None);
        if (page != null) SetActivePage(page);
        if (page == null) return new("Aucun onglet disponible", false, false);
        var origin = Origin(page.Url);
        if (origin.Length == 0) return new("Ouverture du portail…", false, false);
        if (origin != _portalOrigin && origin != settings.SelectorOrigin)
            return new("Redirection SSO en cours…", false, false);
        var probe = ReferenceEquals(page, selectedPage) ? selectedProbe : await ProbeAsync(page, settings, CancellationToken.None);
        if (probe?.Challenge == true || probe?.Password == true) return new("Le navigateur attend une authentification.", false, false);
        if (probe?.ExplicitReady == true) return new("Repère de connexion détecté", probe.Table || probe.Search, true);
        if (probe?.Readable == true && (probe.Search || probe.Table)) return new("Page disponible · « Extraire tout » lance le parcours complet.", true, false);
        if (!string.IsNullOrWhiteSpace(settings.ReadySelector) && settings.SelectorOrigin == origin)
        {
            var ready = page.Locator(settings.ReadySelector);
            if (await ready.CountAsync() == 1 && await ready.IsVisibleAsync()) return new("Repère de connexion détecté", false, true);
        }
        // This check only reads presence, never input values or authentication tokens.
        var password = page.Locator("input[type=password]:visible");
        if (await password.CountAsync() > 0) return new("Le navigateur attend une authentification.", false, false);
        var table = page.Locator("table:visible, [role=grid]:visible, [role=table]:visible");
        if (await table.CountAsync() > 0) return new("Page disponible · « Extraire tout » lance le parcours complet.", true, false);
        if ((DateTimeOffset.UtcNow - _openedAt).TotalSeconds > settings.TimeoutSeconds)
            return new("Connexion non confirmée. Vérifie la page dans le navigateur.", false, false);
        return new("Attente du SSO ou du tableau…", false, false);
    }

    public async Task<List<TableChoice>> InspectAsync(IPage page, CancellationToken ct, bool quiet = false)
    {
        if (page.IsClosed || Origin(page.Url).Length == 0) throw new InvalidOperationException("Sélectionne un onglet web ouvert sur GLPI.");
        var result = new List<TableChoice>();
        foreach (var frame in page.Frames.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (frame.IsDetached || Origin(frame.Url).Length == 0) continue;
            try
            {
                if (frame != page.MainFrame)
                {
                    await using var element = await frame.FrameElementAsync();
                    if (!await element.IsVisibleAsync()) continue;
                }
                await EnsureBridgeAsync(frame, ct);
                var inspection = await ReadJsonAsync<Inspection>(frame, "() => window.__glpiPilot.inspect()", null, ct);
                if (inspection.HasPasswordField) continue;
                foreach (var table in inspection.Tables)
                {
                    table.Frame = frame; table.Page = page; table.FrameOrigin = Origin(frame.Url); table.FrameName = frame.Name; table.TopOrigin = Origin(page.Url);
                    if (frame != page.MainFrame) table.Label += " · cadre " + new Uri(frame.Url).Host;
                    result.Add(table);
                }
            }
            catch (PlaywrightException ex) { Log?.Invoke("Un cadre de la page est indisponible : " + AppPaths.SafeError(ex)); }
        }
        if (!quiet) Log?.Invoke($"Analyse terminée : {result.Count} tableau(x) visible(s).");
        return result;
    }

    private async Task<IFrame> ResolveFrameAsync(TableChoice table, CancellationToken ct)
    {
        if (table.Page == null || table.Page.IsClosed) throw new InvalidOperationException("L’onglet a été fermé.");
        if (Origin(table.Page.Url) != table.TopOrigin) throw new InvalidOperationException("La page a quitté le portail. La session a peut-être expiré.");
        var frame = table.Frame;
        if (frame == null || frame.IsDetached)
        {
            var matches = table.Page.Frames.Where(f => Origin(f.Url) == table.FrameOrigin && f.Name == table.FrameName).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Le cadre du tableau a changé. Analyse de nouveau la page.");
            frame = matches[0]; table.Frame = frame;
        }
        if (Origin(frame.Url) != table.FrameOrigin) throw new InvalidOperationException("Le tableau a été remplacé par une redirection, possiblement liée au SSO.");
        await EnsureBridgeAsync(frame, ct);
        return frame;
    }

    private async Task<(IFrame Frame, TableSnapshot Snapshot, string Key)> ReadAsync(TableChoice original, string nextSelector, CancellationToken ct, bool withRepresentations = false)
    {
        var frame = await ResolveFrameAsync(original, ct);
        var inspection = await ReadJsonAsync<Inspection>(frame, "() => window.__glpiPilot.inspect()", null, ct);
        if (inspection.HasPasswordField) throw new InvalidOperationException("Authentification demandée : extraction arrêtée. Termine le SSO dans le navigateur.");
        var matching = inspection.Tables.Where(t => t.HeaderFingerprint == original.HeaderFingerprint).ToList();
        var exact = matching.Where(t => t.Selector == original.Selector).ToList();
        var table = exact.Count == 1 ? exact[0] : matching.Count == 1 ? matching[0] : null;
        if (table == null) throw new InvalidOperationException("Le tableau a changé ou plusieurs tableaux correspondent. Analyse et sélectionne à nouveau le tableau.");
        var snapshot = await ReadJsonAsync<TableSnapshot>(frame, "args => window.__glpiPilot.readTable(args.key, args.withRepresentations)", new { key = table.Key, withRepresentations }, ct);
        if (snapshot.Headers.Count == 0) throw new InvalidOperationException("Le tableau n’a pas de colonnes exploitables.");
        if (!string.IsNullOrWhiteSpace(nextSelector))
            snapshot.Next = await ReadJsonAsync<NextControl?>(frame, "args => window.__glpiPilot.getNext(args.key, args.selector)", new { key = table.Key, selector = nextSelector }, ct);
        return (frame, snapshot, table.Key);
    }

    public async Task HighlightAsync(TableChoice table, CancellationToken ct)
    {
        var (frame, _, key) = await ReadAsync(table, "", ct);
        await ShowBrowserAsync(table.Page, ct);
        await frame.EvaluateAsync("key => window.__glpiPilot.highlightTable(key)", key).WaitAsync(ct);
    }

    public async Task<(PickResult Pick, string Origin)> PickAsync(IPage page, string kind, IFrame? preferredFrame, CancellationToken ct)
    {
        if (kind is not ("table" or "next" or "ready")) throw new ArgumentOutOfRangeException(nameof(kind));
        var frame = preferredFrame ?? page.MainFrame;
        if (Origin(frame.Url).Length == 0) throw new InvalidOperationException("Affiche GLPI avant de sélectionner un élément.");
        await EnsureBridgeAsync(frame, ct);
        await ShowBrowserAsync(page, ct);
        await frame.EvaluateAsync("kind => { window.__glpiPilotPick = null; window.__glpiPilot.pick(kind); }", kind);
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(3))
            {
                ct.ThrowIfCancellationRequested();
                var selection = await ReadJsonAsync<PickResult?>(frame, "() => window.__glpiPilotPick || null", null, ct);
                if (selection?.Cancelled == true) throw new OperationCanceledException("Sélection annulée dans le navigateur.", ct);
                if (selection != null && !string.IsNullOrWhiteSpace(selection.Selector)) return (selection, Origin(frame.Url));
                await Task.Delay(200, ct);
            }
            throw new InvalidOperationException("La sélection a expiré. Tu peux relancer le choix de l’élément.");
        }
        finally
        {
            try { await frame.EvaluateAsync("() => window.__glpiPilot?.cancelPick()"); } catch (PlaywrightException) { }
        }
    }

    public async Task<ExtractionResult> ExtractAsync(TableChoice table, bool followingPages, AppSettings settings, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var result = new ExtractionResult
        {
            Title = table.Label,
            SourceUrl = AppPaths.SafeUrl(table.Frame?.Url ?? table.Page?.Url ?? ""),
            Scope = followingPages ? "À partir de la page affichée" : "Page courante",
            ExpectedTotal = settings.ExpectedTotal,
            ExtractedAt = DateTimeOffset.Now
        };
        var signatures = new HashSet<string>();
        var keys = new Dictionary<string, string>();
        var duplicateCount = 0;
        var reachedEnd = false;
        var interrupted = false;
        var customNext = settings.SelectorOrigin == table.FrameOrigin ? settings.NextSelector : "";
        var store = _checkpoints;
        var startedAt = DateTimeOffset.Now;
        string firstSignature = "";
        try
        {
            var originalScope = await CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
            var (_, first, _) = await ReadAsync(table, customNext, ct);
            firstSignature = first.Signature;
            var saved = followingPages && settings.ResumePagination && originalScope.Known ? store.Load(settings) : null;
            if (saved != null && saved.ScopeFingerprint == originalScope.Fingerprint && saved.FirstSignature == first.Signature
                && saved.Data.Headers.SequenceEqual(first.Headers) && saved.Data.ExpectedTotal == first.ExpectedTotal)
            {
                var firstUrl = table.Page!.Url;
                try
                {
                    await NavigateTargetAsync(table.Page, saved.LastUrl, ct);
                    await ReplaceRecoveredTableAsync(table, settings, ct);
                    var (_, last, _) = await ReadAsync(table, customNext, ct);
                    var restoredScope = await CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
                    if (last.Signature != saved.LastSignature || restoredScope.Fingerprint != originalScope.Fingerprint)
                        throw new InvalidOperationException("Le point de reprise ne correspond plus à la dernière page conservée.");
                    result = saved.Data; keys = saved.Keys; signatures = new(saved.Signatures, StringComparer.Ordinal); startedAt = saved.StartedAt;
                    result.Provenance.Resumed = true; result.Provenance.ResumeStartedAt = saved.StartedAt;
                    Log?.Invoke($"REPRISE · {result.Pages} pages et {result.Rows.Count} lignes conservées ; première et dernière pages, colonnes, total et périmètre recontrôlés.");
                    reachedEnd = !await AdvancePaginationAsync(table, last, settings, originalScope, ct);
                }
                catch (Exception error) when (error is PlaywrightException or InvalidOperationException or TimeoutException)
                {
                    Log?.Invoke("REPRISE · point devenu incompatible ; nouvelle collecte depuis la première page.");
                    await NavigateTargetAsync(table.Page!, firstUrl, ct); await ReplaceRecoveredTableAsync(table, settings, ct);
                    result = new() { Title = table.Label, SourceUrl = AppPaths.SafeUrl(firstUrl), Scope = "À partir de la première page", ExpectedTotal = settings.ExpectedTotal, ExtractedAt = DateTimeOffset.Now };
                    keys.Clear(); signatures.Clear(); startedAt = DateTimeOffset.Now; store.Delete(settings.PortalUrl);
                }
            }
            for (var pageNo = result.Pages + 1; !reachedEnd && pageNo <= (followingPages ? settings.MaxPages : 1); pageNo++)
            {
                ct.ThrowIfCancellationRequested();
                var (frame, snapshot, _) = await ReadAsync(table, customNext, ct);
                var currentScope = await CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
                if (originalScope.Fingerprint.Length > 0 && currentScope.Fingerprint != originalScope.Fingerprint)
                    throw new InvalidOperationException("La recherche, l’entité ou le profil GLPI a changé pendant la pagination. Les pages suivantes n’ont pas été mélangées au relevé initial.");
                if (pageNo == 1) { result.Headers = snapshot.Headers; result.ExpectedTotal ??= snapshot.ExpectedTotal; }
                else if (!result.Headers.SequenceEqual(snapshot.Headers)) throw new InvalidOperationException("Les colonnes ont changé pendant l’extraction.");
                if (!signatures.Add(snapshot.Signature)) throw new InvalidOperationException("Une page déjà lue a été retrouvée. Arrêt pour éviter une boucle ou des doublons.");
                if (snapshot.ExpectedTotal.HasValue && result.ExpectedTotal.HasValue && snapshot.ExpectedTotal != result.ExpectedTotal)
                    throw new InvalidOperationException("Le nombre de résultats a changé pendant l’extraction. Relance la recherche avec un tri stable.");
                foreach (var warning in snapshot.Warnings) if (!result.Warnings.Contains(warning)) result.Warnings.Add(warning);
                for (var i = 0; i < snapshot.Rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (result.Rows.Count >= settings.MaxRows) throw new InvalidOperationException($"Limite de {settings.MaxRows:N0} lignes atteinte. Modifie la limite ou réduis la recherche.");
                    var row = snapshot.Rows[i]; var key = i < snapshot.Keys.Count ? snapshot.Keys[i] : "";
                    if (followingPages && !string.IsNullOrEmpty(key))
                    {
                        var rowHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", row))));
                        if (keys.TryGetValue(key, out var oldHash))
                        {
                            if (oldHash != rowHash) throw new InvalidOperationException("Un même identifiant a des valeurs différentes entre deux pages. Résultats instables.");
                            duplicateCount++; continue;
                        }
                        keys[key] = rowHash;
                    }
                    result.Rows.Add(row);
                }
                result.Pages = pageNo;
                if (followingPages && settings.ResumePagination && originalScope.Known)
                {
                    try { store.Save(new() { Target = TargetUrlPolicy.PersistedTarget(settings.PortalUrl), StartedAt = startedAt, ScopeFingerprint = originalScope.Fingerprint,
                        FirstSignature = firstSignature, LastUrl = TargetUrlPolicy.PersistedTarget(table.Page!.Url), LastSignature = snapshot.Signature,
                        Data = result, Keys = keys, Signatures = signatures.ToList() }, ct); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log?.Invoke("REPRISE · point non enregistré : " + AppPaths.SafeError(error)); }
                }
                progress?.Report(new(pageNo, result.Rows.Count, result.ExpectedTotal, $"Page {pageNo} lue · {result.Rows.Count:N0} lignes"));
                if (!followingPages) { reachedEnd = true; break; }
                if (pageNo == settings.MaxPages && snapshot.Next is { Disabled: false, Selector.Length: > 0 }) throw new InvalidOperationException($"Limite de {settings.MaxPages} pages atteinte.");
                reachedEnd = !await AdvancePaginationAsync(table, snapshot, settings, originalScope, ct);
            }
        }
        catch (OperationCanceledException) { interrupted = true; result.Warnings.Add("Extraction arrêtée à la demande de l’utilisateur."); }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or System.TimeoutException)
        { interrupted = true; result.Warnings.Add(AppPaths.SafeError(ex)); }

        if (duplicateCount > 0) result.Warnings.Add($"{duplicateCount} ligne(s) identique(s) avec le même identifiant ignorée(s) entre les pages ; la recherche a pu évoluer pendant la lecture.");
        if (!followingPages)
        {
            result.Complete = !interrupted && reachedEnd && result.Warnings.Count == 0;
            result.Status = result.Complete ? "Page courante extraite" : "Page courante : résultat partiel ou à vérifier";
        }
        else if (!interrupted && reachedEnd && result.ExpectedTotal.HasValue && result.Rows.Count == result.ExpectedTotal.Value && result.Warnings.Count == 0)
        { result.Complete = true; result.Status = "Total récupéré conforme au total annoncé"; }
        else
        {
            result.Complete = false;
            if (result.ExpectedTotal.HasValue && result.Rows.Count != result.ExpectedTotal.Value)
                result.Warnings.Add($"{result.Rows.Count:N0} lignes récupérées pour {result.ExpectedTotal.Value:N0} annoncées. Vérifie la première page et la pagination.");
            if (!result.ExpectedTotal.HasValue) result.Warnings.Add("Total global inconnu : l’exhaustivité ne peut pas être confirmée.");
            result.Status = interrupted ? "Extraction partielle" : "Pagination terminée — exhaustivité à vérifier";
        }
        if (followingPages && reachedEnd && !interrupted)
        { try { store.Delete(settings.PortalUrl); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log?.Invoke("REPRISE · nettoyage différé du point terminé."); } }
        Log?.Invoke(result.Status + $" · {result.Rows.Count:N0} lignes · {result.Pages} page(s).");
        return result;
    }

    private static async Task ClickNextAsync(IFrame frame, NextControl next, string expectedOrigin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var locator = frame.Locator(next.Selector);
        if (await locator.CountAsync() != 1) throw new InvalidOperationException("Le contrôle Suivant ne désigne pas un élément unique.");
        var safe = await locator.EvaluateAsync<bool>("""
            (el, origin) => {
              const labels = [el.textContent, el.getAttribute('aria-label'), el.title, el.rel].filter(Boolean).map(v => v.replace(/\s+/g, ' ').trim());
              if (/supprim|delete|effac|valid|enregistr|save|submit|clotur|clôtur|archiv/i.test(labels.join(' '))) return false;
              const navigation = labels.some(v => /^(?:(?:page )?suivante?|next(?: page)?|[›»→])$/i.test(v.replace(/^[›»→]\s*|\s*[›»→]$/g, '').trim()) || /^[›»→]$/.test(v)) || el.getAttribute('rel') === 'next';
              if (!navigation) return false;
              const pagination = el.closest('.pagination, .pager, .paginator, .tab_cadre_pager, [data-pagination], nav[aria-label*=pagination i], nav[aria-label*=pages i]');
              if (el.tagName !== 'A' && !pagination && el.getAttribute('rel') !== 'next') return false;
              if (el.tagName === 'BUTTON' && el.type === 'submit' && el.closest('form')) return false;
              if (el.tagName === 'A') {
                const href = el.getAttribute('href');
                if (!href || href.trim().toLowerCase().startsWith('javascript:')) return false;
                const u = new URL(href, document.baseURI);
                if (u.origin !== origin || !/^https?:$/.test(u.protocol)) return false;
                if ([...u.searchParams.keys()].some(k => /^(delete|purge|update|save|submit|archive)$/i.test(k))) return false;
              } else if (el.tagName !== 'BUTTON' && el.getAttribute('role') !== 'button') return false;
              if (el.matches(':disabled,[aria-disabled=true]') || el.closest('[aria-disabled=true],.disabled')) return false;
              return true;
            }
            """, expectedOrigin);
        if (!safe) throw new InvalidOperationException("Le contrôle sélectionné n’est pas une pagination sûre et reconnue. Choisis le vrai bouton Suivant.");
        // Once this click starts, finish its short timeout before acknowledging cancellation.
        await locator.ClickAsync(new() { Timeout = 4000 });
        ct.ThrowIfCancellationRequested();
    }

    public Task<string> CaptureDownloadAsync(IPage page, string folder, CancellationToken ct) =>
        CaptureDownloadCoreAsync(page, folder, null, null, ct);

    private async Task<string> CaptureDownloadCoreAsync(IPage page, string folder, Func<CancellationToken, Task>? trigger, int? timeoutSeconds, CancellationToken ct)
    {
        if (_downloadWaiter != null) throw new InvalidOperationException("Une réception de fichier est déjà en cours.");
        if (page.IsClosed) throw new InvalidOperationException("L’onglet a été fermé.");
        Directory.CreateDirectory(folder);
        _downloadWaiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _downloadPage = page;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds.HasValue) deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.Value));
        var transferToken = deadline.Token;
        IDownload? download = null;
        try
        {
            if (trigger == null)
            {
                await ShowBrowserAsync(page, transferToken);
                Log?.Invoke("Réception activée. Dans GLPI, déclenche l’export souhaité, en choisissant Toutes les pages si nécessaire.");
            }
            else { Log?.Invoke("EXPORT · réception armée avant l’action Toutes les pages / CSV."); await trigger(transferToken); }
            download = await _downloadWaiter.Task.WaitAsync(transferToken);
            Log?.Invoke("EXPORT · téléchargement détecté, attente de la fin du transfert.");
            var raw = Path.GetFileName(download.SuggestedFilename);
            var name = string.Join("_", raw.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (name.Length == 0) name = "export-glpi";
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension is not (".csv" or ".xlsx" or ".xls" or ".slk" or ".pdf" or ".txt" or ".json" or ".zip"))
                name += ".download";
            var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-{name}");
            var temporary = path + ".part";
            Task? saveTask = null;
            try
            {
                // SaveAs awaits completion of the actual transfer. Never execute the downloaded file.
                saveTask = download.SaveAsAsync(temporary);
                await saveTask.WaitAsync(transferToken);
                transferToken.ThrowIfCancellationRequested();
                File.Move(temporary, path);
            }
            catch (OperationCanceledException)
            {
                try { await download.CancelAsync(); } catch (PlaywrightException) { }
                if (saveTask != null) { try { await saveTask; } catch (PlaywrightException) { } }
                throw;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Log?.Invoke($"EXPORT · fichier reçu ({new FileInfo(path).Length:N0} octets, extension {extension}).");
            return path;
        }
        catch (OperationCanceledException)
        {
            var pending = download ?? (_downloadWaiter.Task.IsCompletedSuccessfully ? _downloadWaiter.Task.Result : null);
            if (pending != null) { try { await pending.CancelAsync(); } catch (PlaywrightException) { } }
            if (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("Le téléchargement GLPI n’a pas abouti dans le délai d’export. Augmente ce délai dans Réglages ou copie le diagnostic.");
            throw;
        }
        finally { _downloadWaiter = null; _downloadPage = null; }
    }

    public async ValueTask DisposeAsync()
    {
        ResetAuthenticationPresentation();
        var context = _context;
        _context = null;
        if (context != null) { try { await context.CloseAsync(); } catch (PlaywrightException) { } }
        _playwright?.Dispose();
        _playwright = null;
        _profileLease?.Dispose(); _profileLease = null;
    }
}
