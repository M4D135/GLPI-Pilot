using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Runtime;
using static WorkAssert;

public static class WorkBridgeChecks
{
    public static List<(string Name, Func<Task> Run)> Create() => new()
    {
        ("Bridge simultaneous completion and cancellation never use a disposed token source", CancellationCompletion),
        ("Bridge shutdown waits for admitted operations after identity reuse and rejects late admission", ShutdownCleanup)
    };

    private static async Task CancellationCompletion()
    {
        await using var fixture = new WorkFixture();
        var replies = new ConcurrentQueue<JsonElement>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = new WebBridge(new[] { "test.read" }, async (_, _, ct) =>
        { await release.Task.WaitAsync(ct); return new { read = true }; }, json => replies.Enqueue(JsonSerializer.Deserialize<JsonElement>(json)));
        for (var round = 0; round < 100; round++)
        {
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = replies.Count(r => r.TryGetProperty("id", out _));
            var ids = Enumerable.Range(0, 16).Select(i => "r" + round + "_" + i).ToArray();
            foreach (var id in ids) bridge.Receive(JsonSerializer.Serialize(new { id, op = "test.read", args = new { } }));
            var cancellations = Task.Run(() => Parallel.ForEach(ids, id =>
            { for (var repeat = 0; repeat < 5; repeat++) bridge.Receive(JsonSerializer.Serialize(new { type = "cancel", id })); }));
            release.TrySetResult();
            await cancellations;
            await Until(() => replies.Count(r => r.TryGetProperty("id", out _)) >= before + ids.Length);
            Check(!replies.Any(r => r.TryGetProperty("error", out var error) && error.GetProperty("code").GetString() != "CANCELLED"), "A valid cancellation was misreported as an invalid request or operation failure.");
        }
    }

    private static async Task ShutdownCleanup()
    {
        await using var fixture = new WorkFixture();
        var replies = new ConcurrentQueue<JsonElement>(); var entered = 0; var cleaned = 0; var active = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new WebBridge(new[] { "test.read" }, async (_, args, ct) =>
        {
            Interlocked.Increment(ref entered);
            if (!args.GetProperty("wait").GetBoolean()) return new { read = true };
            Interlocked.Increment(ref active); started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return new { read = true }; }
            finally { await Task.Delay(25, CancellationToken.None); Interlocked.Decrement(ref active); Interlocked.Increment(ref cleaned); }
        }, json => replies.Enqueue(JsonSerializer.Deserialize<JsonElement>(json)));
        // Repeated use of a completed identity must leave the next worker registered for shutdown.
        for (var i = 0; i < 200; i++)
        {
            var expected = entered + 1;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do
            {
                bridge.Receive("{\"id\":\"reused\",\"op\":\"test.read\",\"args\":{\"wait\":false}}");
                await Task.Delay(1, deadline.Token);
            } while (Volatile.Read(ref entered) < expected);
        }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            do
            {
                bridge.Receive("{\"id\":\"reused\",\"op\":\"test.read\",\"args\":{\"wait\":true}}");
                await Task.Delay(1, deadline.Token);
            } while (!started.Task.IsCompleted);
        }
        await bridge.DisposeAsync();
        Check(active == 0 && cleaned == 1, "Bridge disposal returned before the last admitted operation finished cancellation cleanup.");
        var calls = entered;
        bridge.Receive("{\"id\":\"after-close\",\"op\":\"test.read\",\"args\":{\"wait\":false}}");
        bridge.Receive("{\"type\":\"cancel\",\"id\":\"reused\"}");
        Check(entered == calls, "A disposed bridge accepted a new request.");
        await bridge.DisposeAsync();
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(1, deadline.Token);
    }
}
