using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Playwright;

internal sealed class TicketCleanupTrace
{
    public IPage Page { get; }
    public ConcurrentQueue<string> Commands { get; } = new();
    public int CloseCalls { get; private set; }
    public int DetachCalls { get; private set; }
    private readonly bool _stallStopLoading;
    private readonly TaskCompletionSource<JsonElement?> _stalledStop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TicketCleanupTrace(IPage actual, bool stallStopLoading)
    {
        _stallStopLoading = stallStopLoading;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actual.Close += (_, _) => { closed.TrySetResult(); _stalledStop.TrySetResult(null); };
        var context = CleanupForwarder.Create(actual.Context, (method, _) => method.Name == nameof(IBrowserContext.NewCDPSessionAsync)
            ? (true, ObserveSessionAsync(actual.Context.NewCDPSessionAsync(actual))) : (false, null));
        Page = CleanupForwarder.Create(actual, (method, _) =>
        {
            if (method.Name == "get_Context") return (true, context);
            if (method.Name != nameof(IPage.CloseAsync)) return (false, null);
            CloseCalls++;
            return (true, closed.Task);
        });
    }

    private async Task<ICDPSession> ObserveSessionAsync(Task<ICDPSession> pending)
    {
        var actual = await pending;
        return CleanupForwarder.Create(actual, (method, args) =>
        {
            if (method.Name == nameof(ICDPSession.SendAsync))
            {
                var command = (string)args![0]!; Commands.Enqueue(command);
                if (_stallStopLoading && command == "Page.stopLoading") return (true, _stalledStop.Task);
            }
            if (method.Name == nameof(ICDPSession.DetachAsync)) DetachCalls++;
            return (false, null);
        });
    }
}

// Public parameterless construction is required by DispatchProxy. All operations
// except the withheld close are forwarded to the actual Playwright objects.
public class CleanupForwarder : DispatchProxy
{
    private object _actual = null!;
    private Func<MethodInfo, object?[]?, (bool Handled, object? Value)> _replace = null!;
    internal static T Create<T>(T actual, Func<MethodInfo, object?[]?, (bool Handled, object? Value)> replace) where T : class
    {
        var proxy = DispatchProxy.Create<T, CleanupForwarder>();
        ((CleanupForwarder)(object)proxy)._actual = actual;
        ((CleanupForwarder)(object)proxy)._replace = replace;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("Méthode Playwright absente.");
        var replacement = _replace(method, args);
        if (replacement.Handled) return replacement.Value;
        try { return method.Invoke(_actual, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}

namespace GlpiPilot.Core
{
    public sealed partial class BrowserService
    {
        internal Task CloseTicketCleanupFixtureAsync(IPage page, bool own)
        {
            if (own) OwnTicketPage(page);
            return CloseTicketPageAsync(page);
        }
    }
}
