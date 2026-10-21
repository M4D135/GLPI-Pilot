using Microsoft.Playwright;
using System.Collections.Concurrent;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly ConcurrentDictionary<IPage, byte> _pendingPresentation = new();
    private long _presentationRevision;
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
    // context. Automatic work keeps the browser open behind GLPI Pilot; it
    // never minimizes or activates it. The UI owns GLPI Pilot's stacking order.
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
            if (state is "normal" or "maximized") return;
            if (revision != Interlocked.Read(ref _presentationRevision)) return;
            token.ThrowIfCancellationRequested();
            await session.SendAsync("Browser.setWindowBounds", new()
            {
                ["windowId"] = id.GetInt32(),
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" }
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
                Log?.Invoke("FENÊTRE · ajustement de fenêtre indisponible ; le navigateur reste ouvert et aucun passage au premier plan n’est demandé automatiquement.");
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

    public async Task HideBrowserAsync(CancellationToken ct)
    {
        SetBackgroundMode(true);
        if (_context == null) return;
        foreach (var page in _context.Pages.ToArray())
            if (!page.IsClosed) await SetPageWindowAsync(page, false, ct);
        Log?.Invoke("FENÊTRE · navigateur ouvert derrière GLPI Pilot ; le suivi continue dans le logiciel.");
    }

    public async Task ShowBrowserAsync(IPage? page, CancellationToken ct)
    {
        page ??= ActivePage ?? _context?.Pages.FirstOrDefault(p => !p.IsClosed);
        if (page == null || page.IsClosed || _context == null)
            throw new InvalidOperationException("Le navigateur piloté n’est pas encore ouvert.");
        ct.ThrowIfCancellationRequested();
        SetBackgroundMode(false);
        await SetPageWindowAsync(page, true, ct);
        if (!BackgroundMode && !page.IsClosed) await page.BringToFrontAsync();
        Log?.Invoke("FENÊTRE · navigateur affiché à ta demande, sans recharger la recherche.");
    }
}
