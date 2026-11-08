using System.Diagnostics;
using Microsoft.Playwright;

internal sealed partial class WindowsWebViewFixture
{
    // CDP dispatch does not prove that a process has stopped. Time only the injected
    // fault here; the original 12-second host response check begins at the observed
    // crash. Both injections together can use only the remainder of the CI wall budget.
    private async Task InjectCrashAsync(IBrowser connected, IPage page, ICDPSession session, bool browser)
    {
        var command = browser ? "Browser.crash" : "Page.crash";
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void RendererCrashed(object? sender, IPage target) => observed.TrySetResult();
        void BrowserDisconnected(object? sender, IBrowser target) => observed.TrySetResult();
        var start = _elapsed.Elapsed;
        _deadline.Token.ThrowIfCancellationRequested();
        var allowance = RunBudget - InteractionBudget - _faultInjection;
        if (start - _faultInjection >= InteractionBudget || allowance <= TimeSpan.Zero)
            throw new TimeoutException($"No fixture budget remains for {command}.");
        if (browser) connected.Disconnected += BrowserDisconnected; else page.Crash += RendererCrashed;
        _deadline.CancelAfter(allowance);
        try
        {
            var sending = session.SendAsync(command);
            // A crashed target may close its channel before acknowledging the command.
            _ = sending.ContinueWith(static failed => { _ = failed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            try
            {
                var first = await Task.WhenAny(sending, observed.Task).WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
                if (first == sending) await sending;
            }
            catch (PlaywrightException e) { Log(command + " channel: " + e.Message); }
            catch (TimeoutException) { Log(command + " is awaiting the actual crash signal."); }
            await observed.Task.WaitAsync(_deadline.Token);
            Log($"{command}: isolated target signalled failure after {(_elapsed.Elapsed - start).TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException e) when (_deadline.IsCancellationRequested)
        {
            throw new TimeoutException(command + " did not produce the expected target event within the fixture budget.", e);
        }
        finally
        {
            if (browser) connected.Disconnected -= BrowserDisconnected; else page.Crash -= RendererCrashed;
            var elapsed = _elapsed.Elapsed;
            _faultInjection += elapsed - start;
            var active = InteractionBudget - (elapsed - _faultInjection);
            var wall = RunBudget - elapsed;
            var remaining = active < wall ? active : wall;
            if (remaining <= TimeSpan.Zero) _deadline.Cancel(); else _deadline.CancelAfter(remaining);
        }
    }
}
