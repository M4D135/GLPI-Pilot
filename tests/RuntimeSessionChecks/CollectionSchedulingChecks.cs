using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class CollectionSchedulingChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Collection scheduling: " + name,
            async () => { await using var f = new SessionFixture(); await run(f); }));

        Add("duplicate requests are rejected through cancellation cleanup, then explicit retry is available", async f =>
        {
            var entered = Signal(); var cleanup = Signal(); var release = Signal(); var calls = 0;
            f.App.ConfigureStartupCollectionFixture(async (_, ct) =>
            {
                calls++; entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); return null; }
                finally { cleanup.TrySetResult(); await release.Task; }
            });
            var first = f.Call("collect.run"); await entered.Task;
            try
            {
                await Refused(() => f.Call("collect.run"));
                await f.Call("operations.cancel"); await cleanup.Task;
                await Refused(() => f.Call("collect.campaign"));
                var read = f.Call("records.cell", new { revision = f.App.Revision, row = 0, column = 0 });
                Check(!read.IsCompleted && calls == 1, "A local read waits for cleanup without becoming a second collection.");
                release.TrySetResult(); await Cancelled(first);
                Check((await read).GetProperty("raw").GetString() == "42", "Cleanup must leave the received data readable.");
                f.App.ConfigureStartupCollectionFixture((_, _) => { calls++; return Task.FromResult<object?>(null); });
                await f.Call("collect.run");
                Check(calls == 2, "A later explicit retry must remain available once cleanup really finishes.");
            }
            finally { release.TrySetResult(); }
        });

        Add("a queued collection reserves admission before the execution gate is available", async f =>
        {
            var entered = Signal(); var release = Signal(); var calls = 0;
            f.App.RegisterSessionFixture("test.collectionGate", async (_, _) => { entered.TrySetResult(); await release.Task; return null; });
            f.App.ConfigureStartupCollectionFixture((_, _) => { calls++; return Task.FromResult<object?>(null); });
            var hold = f.Call("test.collectionGate"); await entered.Task;
            try
            {
                var first = f.Call("collect.run");
                await Refused(() => f.Call("collect.manual", new { revision = 0 }));
                Check(calls == 0, "No collection starts while another operation owns the execution gate.");
                release.TrySetResult(); await hold; await first;
                Check(calls == 1, "Only the originally admitted collection may run.");
            }
            finally { release.TrySetResult(); }
        });

        foreach (var outcome in new[] { "completed", "failed", "cancelled" })
            Add("the next deadline follows a long " + outcome + " run and does not immediately retry", async f =>
            {
                var now = DateTimeOffset.Now; var calls = 0;
                f.App.Settings.Automation.IntervalMinutes = 5;
                f.App.Settings.Automation.FixedTimes = "";
                f.App.ForceScheduledFixture((_, _) =>
                {
                    calls++; now = now.AddHours(1);
                    return outcome == "failed" ? Task.FromException<object?>(new InvalidOperationException("Synthetic failure"))
                        : outcome == "cancelled" ? Task.FromException<object?>(new OperationCanceledException())
                        : Task.FromResult<object?>(new { received = true });
                });
                f.App.SetAutomaticClockFixture(() => now);
                if (outcome == "cancelled") await Cancelled(f.App.TickAsync()); else await f.App.TickAsync();
                var schedule = await f.Call("automation.get");
                Check(schedule.GetProperty("next").GetDateTimeOffset() == now.AddMinutes(5),
                    "The interval must start at completion, even after failure/cancellation and restoration.");
                await f.App.TickAsync();
                Check(calls == 1, "An overdue start-time deadline must not dispatch another automatic collection.");
            });

        Add("an explicit collection arriving during a local scheduler check takes priority without an extra run", async f =>
        {
            var entered = Signal(); var release = Signal(); var operations = new List<string>();
            f.App.ForceScheduledFixture((_, _) => { operations.Add(f.App.CurrentOperation); return Task.FromResult<object?>(null); });
            f.App.SetAutomaticPreparationFixture(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return null; });
            var tick = f.App.TickAsync(); await entered.Task;
            try
            {
                var requested = f.Call("collect.run");
                release.TrySetResult(); await tick; await requested;
                Check(operations.SequenceEqual(new[] { "collect.run" }),
                    "A foreground collection queued during local schedule inspection must suppress the automatic dispatch.");
            }
            finally { release.TrySetResult(); }
        });

        Add("pending restoration pauses startup and scheduled work without blocking local reads or explicit recovery", async f =>
        {
            var calls = 0;
            f.App.Settings.Automation.CollectOnStartup = true;
            f.App.SetStartupAvailabilityFixture(true, true);
            f.App.ForceScheduledFixture((_, _) => { calls++; return Task.FromResult<object?>(null); });
            var path = new TemporaryColumnJournalStore().PathFor(f.App.Settings);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // An unreadable existing journal must be treated as pending too;
            // no scheduler is allowed to repair it by overwriting the record.
            await File.WriteAllTextAsync(path, "{unreadable-recovery-journal");
            await f.App.TickAsync(); await f.App.TickAsync();
            var offer = (await f.Call("app.bootstrap")).GetProperty("startupCollection");
            Check(!offer.GetProperty("eligible").GetBoolean() && offer.GetProperty("reason").GetString() == "restoration-pending",
                "Startup must not automatically retry a pending or unreadable restoration.");
            var schedule = await f.Call("automation.get");
            Check(calls == 0 && schedule.GetProperty("suspended").GetBoolean() && schedule.GetProperty("next").ValueKind == JsonValueKind.Null,
                "Scheduled extraction must be paused, with a visible reason and no misleading next deadline.");
            Check((await f.Call("records.cell", new { revision = f.App.Revision, row = 0, column = 0 })).GetProperty("raw").GetString() == "42",
                "Pending browser preferences do not invalidate received ticket values.");
            await f.Call("collect.run");
            Check(calls == 1 && File.Exists(path), "An explicit recovery request is admitted; only the browser recovery path may remove its journal.");
            Check(f.Events.Count(e => e.Name == "operation.log" && JsonSerializer.Serialize(e.Data, PilotApplication.Json).Contains("PROGRAMMATION", StringComparison.Ordinal)) == 1,
                "A paused scheduler must not repeat the same warning at each tick.");
        });
        return tests;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Refused(Func<Task> run)
    {
        try { await run().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (InvalidOperationException error) when (error.Message.Contains("n’est pas mise en attente", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A second collection was queued instead of being refused immediately.");
    }
    private static async Task Cancelled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void SetAutomaticClockFixture(Func<DateTimeOffset> now) => _automaticNow = now;
        public void SetAutomaticPreparationFixture(Func<JsonElement, CancellationToken, Task<object?>> preparation) => _handlers["library.tick"] = preparation;
    }
}
