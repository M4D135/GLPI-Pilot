using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

// One observer belongs to one armed export. Request observation only prevents a
// second trigger; success still requires an owned Download and its saved file.
internal sealed class NativeExportAttempt : IDisposable
{
    private sealed class Chain(IRequest root, IPage? page, bool verified)
    {
        public IRequest Root { get; } = root;
        public IPage? Page { get; set; } = page;
        public bool Verified { get; set; } = verified;
        public bool Rejected { get; set; }
        public string? Failure { get; set; }
        public HashSet<string> Urls { get; } = new(StringComparer.Ordinal);
    }

    private readonly IBrowserContext _context;
    private readonly IPage _sourcePage;
    private readonly IFrame _sourceFrame;
    private readonly string _origin;
    private readonly string _csvMode;
    private readonly string _exportPath;
    private readonly Action<Exception> _reportFailure;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<IRequest, Chain> _requests = new(ReferenceEqualityComparer.Instance);
    private readonly List<Chain> _chains = [];
    private readonly TaskCompletionSource<bool> _requestSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _requestLogged;
    private bool _failed;
    private InvalidOperationException? _failure;
    private bool _disposed;

    public NativeExportAttempt(IBrowserContext context, IPage sourcePage, IFrame sourceFrame, string origin,
        string csvMode, string exportPath, Action<Exception> reportFailure, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sourcePage);
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(log);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http")
            || address.UserInfo.Length != 0)
            throw new ArgumentException("L’origine de l’export GLPI n’est pas valide.", nameof(origin));
        if (!Regex.IsMatch(csvMode, @"^-[1-9][0-9]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Le mode CSV de toutes les pages n’est pas confirmé.", nameof(csvMode));
        if (!exportPath.StartsWith('/') || exportPath.Contains('?') || exportPath.Contains('#')
            || !Regex.IsMatch(exportPath, @"/front/report\.dynamic\.php/?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("La destination native de l’export n’est pas valide.", nameof(exportPath));
        _context = context; _sourcePage = sourcePage; _sourceFrame = sourceFrame;
        _origin = address.GetLeftPart(UriPartial.Authority); _csvMode = csvMode; _exportPath = exportPath;
        _reportFailure = reportFailure; _log = log;
        _context.Request += OnRequest;
        _context.Response += OnResponse;
        _context.RequestFailed += OnRequestFailed;
        _context.Page += OnPage;
    }

    public bool RequestObserved
    {
        get { lock (_gate) return !_disposed && _chains.Any(chain => !chain.Rejected && (chain.Verified || chain.Page is null)); }
    }

    // DOM actions and the context's Request event can complete in either order.
    // This bounded wait bridges that ordering without sending another request.
    public async Task<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ct.ThrowIfCancellationRequested();
        try { return await _requestSignal.Task.WaitAsync(timeout, ct) && RequestObserved; }
        catch (TimeoutException) { ct.ThrowIfCancellationRequested(); return false; }
    }

    // A failure can follow the Download event while SaveAs is still running;
    // completing the receiver with an exception is no longer sufficient then.
    public void ThrowIfFailed()
    {
        lock (_gate) { if (_failure is not null) throw _failure; }
    }

    private Uri? LocalAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var address)
        && address.Scheme is "https" or "http" && address.UserInfo.Length == 0
        && string.Equals(address.GetLeftPart(UriPartial.Authority), _origin, StringComparison.OrdinalIgnoreCase) ? address : null;

    private static string RequestUrl(Uri address) => address.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

    private bool IsRoot(IRequest request, Uri address)
    {
        if (request.Method != "GET" || !request.IsNavigationRequest || address.AbsolutePath != _exportPath) return false;
        var modes = new List<string>();
        foreach (var part in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (key.StartsWith("display_type[", StringComparison.Ordinal)) return false;
            if (key == "display_type") modes.Add(pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "");
        }
        return modes.Count == 1 && modes[0] == _csvMode;
    }

    private void OnRequest(object? sender, IRequest request)
    {
        IPage? pageToVerify = null;
        lock (_gate)
        {
            if (_disposed || _failed) return;
            var address = LocalAddress(request.Url);
            if (request.RedirectedFrom is { } previous)
            {
                if (!_requests.TryGetValue(previous, out var chain)) return;
                _requests[request] = chain;
                if (address is null || request.Method != "GET" || !request.IsNavigationRequest)
                    chain.Failure = "La redirection de l’export a quitté la destination GLPI autorisée. Aucun fichier n’est accepté.";
                else chain.Urls.Add(RequestUrl(address));
                CheckState();
                return;
            }
            if (address is null || !IsRoot(request, address)) return;
            IFrame? frame = null;
            try { frame = request.Frame; }
            catch (PlaywrightException) { /* Initial popup navigation can precede its frame. */ }
            var page = frame?.Page;
            if (page == _sourcePage && frame != _sourceFrame) return;
            var candidate = new Chain(request, page, frame == _sourceFrame);
            candidate.Urls.Add(RequestUrl(address));
            _chains.Add(candidate); _requests.Add(request, candidate);
            if (page is not null && !candidate.Verified) pageToVerify = page;
            CheckState();
        }
        if (pageToVerify is not null) _ = VerifyPageAsync(pageToVerify);
    }

    private void OnResponse(object? sender, IResponse response)
    {
        lock (_gate)
        {
            if (_disposed || _failed || response.Status < 400 || !_requests.TryGetValue(response.Request, out var chain)) return;
            chain.Failure = response.Status is 401 or 403
                ? $"GLPI a refusé l’export (HTTP {response.Status}). Vérifier la connexion et les droits d’export dans le navigateur."
                : $"GLPI n’a pas pu produire l’export (HTTP {response.Status}). Aucun fichier n’a été validé.";
            CheckState();
        }
    }

    private void OnRequestFailed(object? sender, IRequest request)
    {
        lock (_gate)
        {
            if (_disposed || _failed || !_requests.TryGetValue(request, out var chain)) return;
            var failure = request.Failure ?? "";
            // A navigation handed over to the download manager can be aborted
            // from the renderer's perspective while the attachment succeeds.
            if (failure.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("NS_BINDING_ABORTED", StringComparison.OrdinalIgnoreCase)
                || failure.Contains("NS_ERROR_ABORT", StringComparison.OrdinalIgnoreCase)) return;
            chain.Failure = "La requête d’export GLPI a échoué avant le téléchargement. Vérifier la connexion puis relancer la collecte.";
            CheckState();
        }
    }

    private void OnPage(object? sender, IPage page) => _ = VerifyPageAsync(page);

    private async Task VerifyPageAsync(IPage page)
    {
        var owned = false;
        try
        {
            IPage? ancestor = page;
            for (var depth = 0; ancestor is not null && depth < 4; depth++)
            {
                if (ancestor == _sourcePage) { owned = true; break; }
                ancestor = await ancestor.OpenerAsync();
            }
        }
        catch (PlaywrightException) { return; }
        lock (_gate)
        {
            if (_disposed || _failed) return;
            var address = LocalAddress(page.Url);
            // A foreign page with the same URL cannot identify a frame-less
            // request. Only proved descendants can resolve that provisional
            // association; direct Frame associations may still be rejected.
            foreach (var chain in _chains.Where(chain => chain.Page == page
                || owned && chain.Page is null && address is not null && chain.Urls.Contains(RequestUrl(address))))
            {
                chain.Page = page; chain.Verified = owned; chain.Rejected = !owned;
            }
            CheckState();
        }
    }

    // Called only after HookPage proves that download.Page is the source page
    // or one of its descendant popups. That proof resolves a frame-less initial
    // request; a matching URL alone never authorizes an unrelated page.
    public bool AcceptsDownload(IDownload download)
    {
        lock (_gate)
        {
            if (_disposed || _failed || LocalAddress(download.Url) is not { } address) return false;
            var matches = _chains.Where(chain => !chain.Rejected && (chain.Page is null || chain.Page == download.Page)
                && chain.Urls.Contains(RequestUrl(address))).ToArray();
            if (matches.Length != 1) return false;
            var candidate = matches[0];
            candidate.Page = download.Page; candidate.Verified = true;
            CheckState();
            return !_failed && candidate.Failure is null;
        }
    }

    // The caller holds _gate. A frame-less request is provisional evidence
    // sufficient only to avoid another click. HTTP failures require ownership.
    private void CheckState()
    {
        if (_disposed || _failed) return;
        var candidates = _chains.Where(chain => !chain.Rejected && (chain.Verified || chain.Page is null)).ToArray();
        if (candidates.Length > 0) _requestSignal.TrySetResult(true);
        if (candidates.Length > 1)
        {
            Fail("Plusieurs requêtes natives d’export distinctes ont été observées. La réception est arrêtée pour ne pas confondre leurs fichiers.");
            return;
        }
        if (candidates.Length == 0) return;
        if (!_requestLogged)
        {
            _requestLogged = true;
            _log("EXPORT · requête native observée ; attente du téléchargement sans nouvel envoi.");
        }
        if (candidates[0].Verified && candidates[0].Failure is { } failure) Fail(failure);
    }

    private void Fail(string message)
    {
        if (_failed || _disposed) return;
        _failed = true;
        _failure = new InvalidOperationException(message);
        _reportFailure(_failure);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _requestSignal.TrySetResult(false);
            _context.Request -= OnRequest;
            _context.Response -= OnResponse;
            _context.RequestFailed -= OnRequestFailed;
            _context.Page -= OnPage;
            _requests.Clear(); _chains.Clear();
        }
    }
}
