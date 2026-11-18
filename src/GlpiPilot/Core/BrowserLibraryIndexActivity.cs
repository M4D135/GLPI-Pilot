using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private sealed class LibraryIndexActivity : IDisposable
    {
        private readonly IPage _page;
        private readonly IFrame _frame;
        private readonly Uri _source;
        private readonly Action<LibraryIndexProgress> _report;
        private readonly LibraryIndexActivityState _state;
        private readonly HashSet<IRequest> _requests = new(ReferenceEqualityComparer.Instance);
        private readonly object _gate = new();
        private GlpiLibraryIndexException? _failure;
        private string _phase = "open-list";
        private string _nativeField = "12";
        private bool _searchMayHaveChanged;
        private bool _disposed;

        public LibraryIndexActivity(IPage page, Uri source, int inactivitySeconds, Action<LibraryIndexProgress> report)
        {
            _page = page; _frame = page.MainFrame; _source = source; _report = report;
            _state = new(inactivitySeconds);
            page.Request += OnRequest; page.Response += OnResponse;
            page.RequestFinished += OnRequestFinished; page.RequestFailed += OnRequestFailed;
        }

        public string Phase { get { lock (_gate) return _phase; } }
        public bool HasPendingRequest { get { lock (_gate) return _state.HasPendingRequest; } }
        public bool SearchMayHaveChanged { get { lock (_gate) return _searchMayHaveChanged; } }

        // The targeted reader obtains this value from the native ID option.
        // Only that observed criterion's asynchronous rendering may extend the wait.
        public void ObserveNativeField(string value)
        {
            if (value.Length is 0 or > 20 || value.Any(c => c is < '0' or > '9'))
                throw new GlpiLibraryPageException("Le critère d’identifiant natif n’a pas une valeur exploitable.");
            lock (_gate) _nativeField = value;
        }

        public void BeginPhase(string phase)
        {
            ThrowIfFailed();
            lock (_gate) { _phase = phase; _state.BeginPhase(); }
            _report(new(phase, "phase-start", LibraryIndexProgress.LabelFor(phase) + "…"));
        }

        public void CompletePhase() => _report(new(Phase, "request-complete", LibraryIndexProgress.LabelFor(Phase) + " : réponse native reçue et structure vérifiée."));

        public void Observe(string signature) { lock (_gate) _state.Observe(signature); }

        public void ThrowIfFailed()
        {
            lock (_gate)
            {
                if (_failure is not null) throw _failure;
                if (_page.IsClosed || _frame.IsDetached)
                    throw new GlpiLibraryIndexException(_phase, "index-browser", "La fenêtre dédiée à l’index a été fermée ou remplacée. La lecture de la liste n’est pas confirmée.");
            }
        }

        public bool CanWait
        {
            get
            {
                ThrowIfFailed();
                bool result; bool notice; bool documentLoading; string phase;
                lock (_gate)
                {
                    result = _state.CanWait; notice = _state.TakePendingNotice(); phase = _phase;
                    documentLoading = _state.DocumentLoading && !_state.HasPendingRequest;
                }
                if (notice) _report(new(phase, "request-pending", LibraryIndexProgress.LabelFor(phase)
                    + (documentLoading
                        ? " : initialisation de la page GLPI encore en cours. Attendre ou annuler."
                        : " : une requête GLPI est encore en cours. Attendre ou annuler.")));
                return result;
            }
        }

        private bool NativeRequest(IRequest request)
        {
            try
            {
                return request.Frame == _frame && request.Frame.Page == _page
                    && LibraryIndexRequestPolicy.IsNative(_source, request.Url, request.Method, request.ResourceType, request.IsNavigationRequest, request.PostData, _nativeField);
            }
            catch (PlaywrightException) { return false; }
        }

        private void OnRequest(object? sender, IRequest request)
        {
            lock (_gate)
            {
                if (_disposed || _failure is not null) return;
                if (request.RedirectedFrom is { } previous && _requests.Contains(previous))
                {
                    _state.RequestEnded(previous);
                    if (!NativeRequest(request))
                    {
                        Fail("index-session", "GLPI a redirigé la lecture de l’index hors de la recherche native vérifiée. Vérifier la connexion et le profil dans le navigateur.");
                        return;
                    }
                }
                if (!NativeRequest(request)) return;
                _searchMayHaveChanged |= request.IsNavigationRequest;
                _requests.Add(request); _state.RequestStarted(request);
            }
        }

        private void OnResponse(object? sender, IResponse response)
        {
            lock (_gate)
            {
                if (_disposed || !_requests.Contains(response.Request)) return;
                _state.ResponseReceived(response.Request);
                if (response.Status >= 400)
                    Fail(response.Status is 401 or 403 ? "index-session" : "index-http-error",
                        response.Status is 401 or 403
                            ? $"GLPI a refusé la lecture de l’index (HTTP {response.Status}). Vérifier la connexion et les droits du profil actif."
                            : $"GLPI a renvoyé une erreur pendant la lecture de l’index (HTTP {response.Status}). La recherche n’a pas été renvoyée automatiquement.");
            }
        }

        private void OnRequestFinished(object? sender, IRequest request)
        {
            lock (_gate) { if (!_disposed && _requests.Contains(request)) _state.RequestEnded(request); }
        }

        private void OnRequestFailed(object? sender, IRequest request)
        {
            lock (_gate)
            {
                if (_disposed || !_requests.Contains(request)) return;
                _state.RequestEnded(request);
                Fail("index-network-error", "La requête native de l’index a été interrompue ou le réseau a échoué. La recherche n’a pas été renvoyée automatiquement.");
            }
        }

        private void Fail(string category, string message) => _failure ??= new(_phase, category, message);

        public async Task<T> AwaitDocumentAsync<T>(Func<Task<T>> navigateUntilDomContentLoaded, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfFailed();
            lock (_gate) _state.BeginDocument();
            try { return await AwaitAsync(navigateUntilDomContentLoaded(), ct); }
            finally { lock (_gate) _state.DocumentLoaded(); }
        }

        // A task is started once. In particular a select/change or GET already
        // sent is never replayed when an old locator/navigation budget expires.
        public async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken ct, Func<GlpiLibraryIndexException>? stalled = null)
        {
            _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (true)
            {
                ct.ThrowIfCancellationRequested(); ThrowIfFailed();
                if (task.IsCompleted) return await task;
                if (!CanWait)
                    throw stalled?.Invoke() ?? new GlpiLibraryIndexException(Phase, "index-stalled", LibraryIndexProgress.LabelFor(Phase) + " : aucune progression ni requête native en cours. L’index n’a pas pu être confirmé.");
                await Task.Delay(120, ct);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _page.Request -= OnRequest; _page.Response -= OnResponse;
                _page.RequestFinished -= OnRequestFinished; _page.RequestFailed -= OnRequestFailed;
                _requests.Clear();
            }
        }
    }
}
