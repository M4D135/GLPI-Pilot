using System.Diagnostics;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private async Task ReplaceRecoveredTableAsync(TableChoice original, AppSettings settings, CancellationToken ct)
    {
        var recoverySettings = settings.Copy(); recoverySettings.AutoMode = "pagination";
        var recovered = await WaitForAutoTableAsync(original.Page!, recoverySettings, ct);
        if (recovered.HeaderFingerprint != original.HeaderFingerprint) throw new InvalidOperationException("Les colonnes ont changé pendant la reprise.");
        original.Frame = recovered.Frame; original.Page = recovered.Page; original.Key = recovered.Key; original.Selector = recovered.Selector;
        original.FrameOrigin = recovered.FrameOrigin; original.FrameName = recovered.FrameName; original.TopOrigin = recovered.TopOrigin;
    }
    private async Task<bool> AdvancePaginationAsync(TableChoice table, TableSnapshot snapshot, AppSettings settings, BrowserScopeEvidence scope, CancellationToken ct)
    {
        var next = snapshot.Next;
        if (next?.Ambiguous == true) throw new InvalidOperationException("Plusieurs contrôles de pagination sont possibles. Choisissez le repère Suivant.");
        if (next == null || next.Disabled || string.IsNullOrWhiteSpace(next.Selector)) return false;
        var frame = table.Frame!; var previousUrl = table.Page!.Url;
        var href = await frame.Locator(next.Selector).GetAttributeAsync("href").WaitAsync(ct);
        var nextUrl = href != null && Uri.TryCreate(new Uri(frame.Url), href, out var candidate) && candidate.Scheme is "http" or "https"
            && TargetUrlPolicy.SamePage(candidate.AbsoluteUri, settings.PortalUrl) && ScopeLocation(candidate.AbsoluteUri) == ScopeLocation(previousUrl)
            ? TargetUrlPolicy.PersistedTarget(candidate.AbsoluteUri) : "";
        var attempts = 0;
        var clicked = false;
        string? networkFailure = null;
        var observedPage = table.Page;
        void FailedRequest(object? sender, IRequest request)
        {
            if (request.IsNavigationRequest && TargetUrlPolicy.SamePage(request.Url, previousUrl) && IsRecoverableNetworkError(request.Failure ?? "")) networkFailure = request.Failure;
        }
        observedPage.RequestFailed += FailedRequest;
        try
        {
        while (true)
        {
            try
            {
                if (networkFailure != null) throw new PlaywrightException(networkFailure);
                if (!clicked) { await ClickNextAsync(frame, next, table.FrameOrigin, ct); clicked = true; }
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(settings.ListTimeoutSeconds))
                {
                    ct.ThrowIfCancellationRequested(); await Task.Delay(250, ct);
                    if (networkFailure != null) throw new PlaywrightException(networkFailure);
                    try
                    {
                        var (_, changed, _) = await ReadAsync(table, settings.SelectorOrigin == table.FrameOrigin ? settings.NextSelector : "", ct);
                        if (changed.Signature != snapshot.Signature && changed.Rows.Count > 0)
                        {
                            await Task.Delay(350, ct);
                            var (_, stable, _) = await ReadAsync(table, settings.SelectorOrigin == table.FrameOrigin ? settings.NextSelector : "", ct);
                            if (stable.Signature == changed.Signature) return true;
                        }
                    }
                    catch (Exception error) when (error is PlaywrightException or InvalidOperationException)
                    {
                        if (error is PlaywrightException && IsRecoverableNetworkError(error.Message)) throw;
                        var probe = table.Page.IsClosed ? null : await ProbeAsync(table.Page, settings, ct);
                        if (probe?.Challenge == true || probe?.Login == true || IsAuthenticationUrl(table.Page.Url) || Origin(table.Page.Url) != Origin(settings.PortalUrl)) throw;
                    }
                }
                throw new InvalidOperationException("La page suivante n’a pas pu être confirmée. Les lignes déjà extraites sont conservées.");
            }
            catch (Exception error) when (error is PlaywrightException or InvalidOperationException or TimeoutException)
            {
                if (_context == null || table.Page.IsClosed || attempts >= settings.ReconnectAttempts || !scope.Known) throw;
                var probe = await ProbeAsync(table.Page, settings, ct);
                var authentication = probe?.Challenge == true || probe?.Login == true || IsAuthenticationUrl(table.Page.Url) || Origin(table.Page.Url) != Origin(settings.PortalUrl);
                var network = networkFailure != null || error is PlaywrightException && IsRecoverableNetworkError(error.Message);
                if (!authentication && !network) throw;
                attempts++; Log?.Invoke($"RECONNEXION · tentative {attempts}/{settings.ReconnectAttempts}, conservation des pages déjà contrôlées.");
                var recovery = settings.Copy(); recovery.PortalUrl = nextUrl.Length > 0 ? nextUrl : previousUrl;
                recovery.AutoMode = "pagination";
                if (network)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempts, 8)), ct);
                    networkFailure = null;
                    try { await NavigateTargetAsync(table.Page, recovery.PortalUrl, ct); }
                    catch (PlaywrightException retryError) when (IsRecoverableNetworkError(retryError.Message)) { networkFailure = retryError.Message; continue; }
                }
                var restored = await WaitForTargetAsync(recovery, ct); table.Page = restored;
                if (!TargetUrlPolicy.IncludesSearch(restored.Url, recovery.PortalUrl)) await NavigateTargetAsync(restored, recovery.PortalUrl, ct);
                await ReplaceRecoveredTableAsync(table, recovery, ct); frame = table.Frame!;
                var evidence = await CaptureScopeEvidenceAsync(table, settings.PortalUrl, ct);
                if (evidence.Fingerprint != scope.Fingerprint) throw new InvalidOperationException("Le périmètre GLPI a changé pendant la reconnexion. La reprise est refusée.");
                clicked = nextUrl.Length > 0;
                if (!clicked)
                {
                    var (_, prior, _) = await ReadAsync(table, "", ct);
                    if (prior.Signature != snapshot.Signature) throw new InvalidOperationException("La dernière page a changé pendant la reconnexion. Relancez la collecte.");
                    next = prior.Next ?? throw new InvalidOperationException("La pagination a disparu pendant la reconnexion.");
                }
            }
        }
        }
        finally { observedPage.RequestFailed -= FailedRequest; }
    }
    private static bool IsRecoverableNetworkError(string message) => new[] { "ERR_INTERNET_DISCONNECTED", "ERR_NETWORK_CHANGED", "ERR_CONNECTION_RESET", "ERR_CONNECTION_CLOSED", "ERR_CONNECTION_REFUSED", "ERR_CONNECTION_TIMED_OUT", "ERR_TIMED_OUT", "ERR_NAME_NOT_RESOLVED", "ERR_ADDRESS_UNREACHABLE", "ERR_FAILED" }.Any(code => message.Contains(code, StringComparison.Ordinal));
    public async Task<string> ReadObservedSortAsync(TableChoice table, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(table, ct);
        return await frame.EvaluateAsync<string>("""
            () => JSON.stringify({parameters:[...new URL(location.href).searchParams].filter(([key]) => /^(sort|order)(\[|$)/i.test(key)),
              columns:[...document.querySelectorAll('th[aria-sort]')].filter(e => e.getAttribute('aria-sort') !== 'none').slice(0,20)
              .map(e => ({column:(e.textContent||'').trim().slice(0,120),direction:e.getAttribute('aria-sort')}))})
            """).WaitAsync(ct);
    }
}
