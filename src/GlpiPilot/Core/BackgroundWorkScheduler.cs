namespace GlpiPilot.Core;

public sealed class BackgroundWorkScheduler : IDisposable
{
    private sealed record Job(string Category, bool Visible, CancellationTokenSource Cancellation, Action Execute, long Order);
    private readonly object _gate = new();
    private readonly PriorityQueue<Job, (int Priority, long Order)> _queue = new();
    private readonly Dictionary<string, Job> _latest = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ready = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private Job? _running;
    private long _sequence;
    public BackgroundWorkScheduler() => _ = Task.Run(WorkerAsync);
    public Task<T> RunAsync<T>(string category, bool visible, Func<CancellationToken, T> work, CancellationToken ct = default)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) { result.TrySetCanceled(); return result.Task; }
            if (_latest.TryGetValue(category, out var old)) old.Cancellation.Cancel();
            if (visible && _running is { Visible: false }) _running.Cancellation.Cancel();
            if (_queue.Count >= 32) { result.TrySetException(new InvalidOperationException("La file de travaux est pleine ; terminez ou annulez une opération.")); return result.Task; }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var job = new Job(category, visible, cancellation, () =>
            {
                try { cancellation.Token.ThrowIfCancellationRequested(); var value = work(cancellation.Token); cancellation.Token.ThrowIfCancellationRequested(); result.TrySetResult(value); }
                catch (OperationCanceledException) { result.TrySetCanceled(cancellation.Token); }
                catch (Exception error) { result.TrySetException(error); }
            }, ++_sequence);
            _latest[category] = job; _queue.Enqueue(job, (visible ? 0 : 1, job.Order)); _ready.Release();
        }
        return result.Task;
    }
    private async Task WorkerAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _ready.WaitAsync(_lifetime.Token); Job job;
                lock (_gate) { if (!_queue.TryDequeue(out job!, out _)) continue; _running = job; }
                job.Execute();
                lock (_gate) { if (_latest.GetValueOrDefault(job.Category) == job) _latest.Remove(job.Category); _running = null; }
                job.Cancellation.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) { while (_queue.TryDequeue(out var job, out _)) { job.Execute(); job.Cancellation.Dispose(); } _latest.Clear(); }
        }
    }
    public void Dispose() { lock (_gate) { if (!_lifetime.IsCancellationRequested) _lifetime.Cancel(); } }
}
