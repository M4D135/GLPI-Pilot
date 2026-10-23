using Microsoft.Playwright;
using System.Collections.Concurrent;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly ConcurrentDictionary<IPage, byte> _pendingPresentation = new();
    private long _presentationRevision;
    private long _authenticationPresentationRevision = -1;
    private IPage? _authenticationPresentedPage;
    private IPage? _authenticationSuppressedPage;
    private string _authenticationSuppressedUrl = "";
    private bool _presentationWarningLogged;
    public bool BackgroundMode { get; private set; } = true;
    public event Action<bool>? BackgroundLaunchStateChanged;

    private void SetBackgroundMode(bool value)
    {
        BackgroundMode = value;
        Interlocked.Increment(ref _presentationRevision);
        NotifyPagesChanged();
    }

    // This session targets only a window owned by the dedicated Playwright
    // context. Automatic work minimizes that dedicated window without activating
    // it. Only an explicit request or a visible SSO challenge restores it.
    // Other Edge/Chrome profiles and processes are never enumerated.
    private async Task SetPageWindowAsync(IPage page, bool restore, CancellationToken ct)
    {
        var revision = Interlocked.Read(ref _presentationRevision);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var token = deadline.Token;
        var entered = false;
        ICDPSession? session = null;
        Task<ICDPSession>? pendingSession = null;
        try
        {
            await _presentationGate.WaitAsync(token);
            entered = true;
            if (_context == null || page.IsClosed || revision != Interlocked.Read(ref _presentationRevision)) return;
            if (!restore && !BackgroundMode) return;
            pendingSession = _context.NewCDPSessionAsync(page);
            session = await pendingSession.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var result = await session.SendAsync("Browser.getWindowForTarget").WaitAsync(token);
            if (result is not { } window || !window.TryGetProperty("windowId", out var id)) return;
            if (revision != Interlocked.Read(ref _presentationRevision)) return;
            token.ThrowIfCancellationRequested();
            var state = window.TryGetProperty("bounds", out var bounds) && bounds.TryGetProperty("windowState", out var current)
                ? current.GetString() : null;
            if (restore ? state is "normal" or "maximized" : state == "minimized") return;
            if (revision != Interlocked.Read(ref _presentationRevision)) return;
            token.ThrowIfCancellationRequested();
            await session.SendAsync("Browser.setWindowBounds", new()
            {
                ["windowId"] = id.GetInt32(),
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = restore ? "normal" : "minimized" }
            }).WaitAsync(token);
        }
        catch (Exception ex) when (ex is PlaywrightException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            // A download popup can lose its CDP target before IsClosed updates.
            // It does not prove that window management is unavailable for the
            // live search page, and must not consume the one-time warning.
            bool obsolete = _context == null || page.IsClosed || revision != Interlocked.Read(ref _presentationRevision);
            bool transientTarget = ex is PlaywrightException && !ReferenceEquals(page, ActivePage) && IsGoneWindowTarget(ex.Message);
            if (!_presentationWarningLogged && !obsolete && !transientTarget)
            {
                _presentationWarningLogged = true;
                Log?.Invoke("FENÊTRE · mode discret indisponible pour cette fenêtre ; le navigateur reste ouvert. Son affichage peut être repris avec Voir le navigateur.");
            }
        }
        finally
        {
            // A stalled window-management response must not retain the launch
            // foreground guard or block cancellation of the actual extraction.
            if (session != null)
            {
                try { await session.DetachAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
            }
            else if (pendingSession != null) _ = DetachLateWindowSessionAsync(pendingSession);
            if (entered) _presentationGate.Release();
        }
    }

    private static bool IsGoneWindowTarget(string message) =>
        new[] { "No target with given id", "No web contents", "Session closed", "Target closed", "Target page, context or browser has been closed", "Not attached to an active page" }
            .Any(fragment => message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static async Task DetachLateWindowSessionAsync(Task<ICDPSession> pending)
    {
        try
        {
            var session = await pending;
            await session.DetachAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or OperationCanceledException) { }
    }

    private async Task KeepPageInBackgroundAsync(IPage page)
    {
        if (!BackgroundMode || !_pendingPresentation.TryAdd(page, 0)) return;
        try { await SetPageWindowAsync(page, false, CancellationToken.None); }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException or InvalidOperationException)
        { /* A popup can close immediately after starting its download. */ }
        finally { _pendingPresentation.TryRemove(page, out _); }
    }

    public Task HideBrowserAsync(CancellationToken ct) => HideBrowserAsync(ct, userRequested: true);

    private async Task HideBrowserAsync(CancellationToken ct, bool userRequested)
    {
        if (userRequested && _authenticationPresentedPage is { IsClosed: false } authentication)
        {
            _authenticationSuppressedPage = authentication;
            _authenticationSuppressedUrl = authentication.Url;
        }
        Interlocked.Exchange(ref _authenticationPresentationRevision, -1);
        _authenticationPresentedPage = null;
        SetBackgroundMode(true);
        if (_context == null) return;
        foreach (var page in _context.Pages.ToArray())
            if (!page.IsClosed) await SetPageWindowAsync(page, false, ct);
        Log?.Invoke("FENÊTRE · mode discret : fenêtre du navigateur réservée à GLPI Pilot réduite ; le suivi continue dans le logiciel.");
    }

    public async Task ShowBrowserAsync(IPage? page, CancellationToken ct)
    {
        page ??= ActivePage ?? _context?.Pages.FirstOrDefault(p => !p.IsClosed);
        if (page == null || page.IsClosed || _context == null)
            throw new InvalidOperationException("Le navigateur piloté n’est pas encore ouvert.");
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _authenticationPresentationRevision, -1);
        _authenticationPresentedPage = null;
        _authenticationSuppressedPage = null;
        _authenticationSuppressedUrl = "";
        SetBackgroundMode(false);
        var revision = Interlocked.Read(ref _presentationRevision);
        await SetPageWindowAsync(page, true, ct);
        if (!BackgroundMode && !page.IsClosed && revision == Interlocked.Read(ref _presentationRevision)) await page.BringToFrontAsync();
        Log?.Invoke("FENÊTRE · navigateur affiché à ta demande, sans recharger la recherche.");
    }

    private async Task RevealAuthenticationAsync(IPage page, CancellationToken ct)
    {
        if (page.IsClosed || _context is null) return;
        if (ReferenceEquals(_authenticationSuppressedPage, page) && _authenticationSuppressedUrl == page.Url) return;
        // Once a person explicitly shows the browser, automatic work no longer
        // owns its visibility. An automatically exposed SSO may move to a popup.
        var owned = Interlocked.Read(ref _authenticationPresentationRevision);
        if (!BackgroundMode && (owned < 0 || owned != Interlocked.Read(ref _presentationRevision)
            || ReferenceEquals(_authenticationPresentedPage, page))) return;
        ct.ThrowIfCancellationRequested();
        if (BackgroundMode) SetBackgroundMode(false);
        var revision = Interlocked.Read(ref _presentationRevision);
        Interlocked.Exchange(ref _authenticationPresentationRevision, revision);
        _authenticationPresentedPage = page;
        await SetPageWindowAsync(page, true, ct);
        if (page.IsClosed || BackgroundMode || revision != Interlocked.Read(ref _presentationRevision)) return;
        await page.BringToFrontAsync();
        Log?.Invoke("SSO · validation visible détectée : navigateur affiché temporairement. Aucune case ni aucun bouton de connexion n’est actionné automatiquement.");
    }

    private async Task ResumeAfterAuthenticationAsync(CancellationToken ct)
    {
        _authenticationSuppressedPage = null;
        _authenticationSuppressedUrl = "";
        var owned = Interlocked.Exchange(ref _authenticationPresentationRevision, -1);
        if (owned < 0 || owned != Interlocked.Read(ref _presentationRevision)) return;
        await HideBrowserAsync(ct, userRequested: false);
        Log?.Invoke("SSO · validation terminée ; reprise de la collecte en mode discret.");
    }

    private async Task CancelAuthenticationPresentationAsync()
    {
        var owned = Interlocked.Exchange(ref _authenticationPresentationRevision, -1);
        if (owned < 0 || owned != Interlocked.Read(ref _presentationRevision)) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await HideBrowserAsync(cleanup.Token, userRequested: false); }
        catch (Exception error) when (error is OperationCanceledException or PlaywrightException or ObjectDisposedException) { }
    }

    private void ResetAuthenticationPresentation()
    {
        Interlocked.Exchange(ref _authenticationPresentationRevision, -1);
        _authenticationPresentedPage = null;
        _authenticationSuppressedPage = null;
        _authenticationSuppressedUrl = "";
    }
}
