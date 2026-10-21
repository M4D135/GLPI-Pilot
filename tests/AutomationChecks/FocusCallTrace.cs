using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

// Forward real Playwright operations while observing application focus requests.
// This measures BringToFront calls, not the Windows window manager's behaviour.
sealed class FocusCallTrace(bool stallCdpCreation = false)
{
    private readonly Dictionary<IPage, IPage> _pages = new();
    private IBrowserContext? _context;
    private int _bringToFrontCalls;
    private int _newCdpSessionCalls;
    public int BringToFrontCalls => Volatile.Read(ref _bringToFrontCalls);
    public int NewCdpSessionCalls => Volatile.Read(ref _newCdpSessionCalls);
    public ConcurrentQueue<string> RequestedWindowStates { get; } = new();
    public string? ReportedWindowState { get; set; }
    public string? CdpCreationFailure { get; set; }

    public IBrowserContext WrapContext(IBrowserContext actual)
    {
        _context = ForwardingSpy.Create<IBrowserContext>(actual, (method, args) =>
        {
            if (method.Name == "get_Pages") return (true, actual.Pages.Select(WrapPage).ToArray());
            if (method.Name == nameof(IBrowserContext.NewCDPSessionAsync))
            {
                Interlocked.Increment(ref _newCdpSessionCalls);
                if (stallCdpCreation) return (true, new TaskCompletionSource<ICDPSession>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
                if (CdpCreationFailure is { } error) return (true, Task.FromException<ICDPSession>(new PlaywrightException(error)));
                var page = (IPage)ForwardingSpy.Unwrap(args![0])!;
                return (true, ObserveSessionAsync(actual.NewCDPSessionAsync(page)));
            }
            return (false, null);
        });
        return _context;
    }

    public IPage WrapPage(IPage actual)
    {
        if (_pages.TryGetValue(actual, out var found)) return found;
        var wrapped = ForwardingSpy.Create<IPage>(actual, (method, _) =>
            method.Name == "get_Context" && _context != null ? (true, _context) : (false, null), method =>
            {
                if (method.Name == nameof(IPage.BringToFrontAsync)) Interlocked.Increment(ref _bringToFrontCalls);
            });
        _pages.Add(actual, wrapped);
        return wrapped;
    }

    private async Task<ICDPSession> ObserveSessionAsync(Task<ICDPSession> pending)
    {
        var actual = await pending;
        return ForwardingSpy.Create<ICDPSession>(actual, (method, args) =>
        {
            if (method.Name != nameof(ICDPSession.SendAsync) || args?[0] is not string command) return (false, null);
            if (command == "Browser.getWindowForTarget" && ReportedWindowState != null)
                return (true, ReportWindowStateAsync(actual));
            if (command == "Browser.setWindowBounds" && args.Length > 1)
            {
                var parameters = JsonSerializer.SerializeToElement(args[1]);
                if (parameters.TryGetProperty("bounds", out var bounds) && bounds.TryGetProperty("windowState", out var state))
                {
                    RequestedWindowStates.Enqueue(state.GetString() ?? "");
                    if (ReportedWindowState != null) ReportedWindowState = state.GetString();
                }
            }
            return (false, null);
        });
    }

    private async Task<JsonElement?> ReportWindowStateAsync(ICDPSession actual)
    {
        var response = await actual.SendAsync("Browser.getWindowForTarget");
        if (response is not { } window || !window.TryGetProperty("windowId", out var id)) return response;
        // Only the reported window state is simulated. Commands still reach the
        // real CDP session, and every requested state is observed independently.
        return JsonSerializer.SerializeToElement(new { windowId = id.GetInt32(), bounds = new { windowState = ReportedWindowState } });
    }
}

// DispatchProxy requires a non-sealed class with a public parameterless constructor.
class ForwardingSpy : DispatchProxy
{
    private object _actual = null!;
    private Func<MethodInfo, object?[]?, (bool Handled, object? Value)>? _override;
    private Action<MethodInfo>? _observe;

    public static T Create<T>(T actual, Func<MethodInfo, object?[]?, (bool Handled, object? Value)>? replace = null, Action<MethodInfo>? observe = null) where T : class
    {
        T proxy = DispatchProxy.Create<T, ForwardingSpy>();
        var handler = (ForwardingSpy)(object)proxy;
        handler._actual = actual;
        handler._override = replace;
        handler._observe = observe;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("Méthode Playwright absente.");
        _observe?.Invoke(method);
        var replacement = _override?.Invoke(method, args);
        if (replacement.HasValue && replacement.Value.Handled) return replacement.Value.Value;
        // CDP session creation requires the original Playwright page implementation.
        var forwarded = args?.Select(Unwrap).ToArray();
        try { return method.Invoke(_actual, forwarded); }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public static object? Unwrap(object? value) => value is ForwardingSpy proxy ? proxy._actual : value;
}
