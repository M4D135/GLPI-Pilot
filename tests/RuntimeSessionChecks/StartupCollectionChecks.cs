using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class StartupCollectionChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Startup collection: " + name,
            async () => { await using var fixture = new SessionFixture(); await run(fixture); }));

        Add("bootstrap offers a stable token after loading the session without collecting or opening a browser", async f =>
        {
            var stub = new CollectionStub(f.App); var previous = f.App.Result;
            var first = await f.Call("app.bootstrap"); var offer = Offer(first);
            var second = Offer(await f.Call("app.bootstrap"));
            Check(offer.GetProperty("eligible").GetBoolean() && offer.GetProperty("reason").GetString() == "ready"
                && !offer.GetProperty("attempted").GetBoolean() && Token(offer).Length > 0 && Token(second) == Token(offer),
                "A configured startup must expose one stable, unconsumed offer.");
            Check(stub.Calls == 0 && !f.App.Browser.IsOpen && ReferenceEquals(previous, f.App.Result)
                && first.GetProperty("capabilities").EnumerateArray().Any(value => value.GetString() == "collect.startup"),
                "Bootstrap must remain local and return the registered startup operation for the UI to invoke after rendering.");
        });

        Add("one startup invokes the normal collect.run handler once and leaves explicit collection available", async f =>
        {
            f.App.Settings.Automation.ScheduledExtraction = true;
            f.App.Settings.Automation.RunCampaign = true; f.App.Settings.Automation.UseRecipe = true;
            var stub = new CollectionStub(f.App); var entered = Signal(); var release = Signal();
            stub.Run = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new { completed = true }; };
            var token = Token(Offer(await f.Call("app.bootstrap")));
            var first = f.Call("collect.startup", new { token });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                var duplicate = await f.Call("collect.startup", new { token }).WaitAsync(TimeSpan.FromSeconds(1));
                Check(!Started(duplicate) && duplicate.GetProperty("skipped").GetBoolean() && stub.Calls == 1,
                    "Duplicate startup dispatch must return immediately without queueing a second collection.");
            }
            finally { release.TrySetResult(); }
            Check(Started(await first) && stub.Calls == 1 && stub.Operations.Single() == "collect.startup" && stub.AllObservedBusy,
                "The startup operation must execute the ordinary collection handler under the normal runtime execution gate.");
            Check(!Started(await f.Call("collect.startup", new { token })) && stub.Calls == 1
                && Offer(await f.Call("app.bootstrap")).GetProperty("attempted").GetBoolean(),
                "A completed opening must not offer another automatic attempt after UI reload.");
            await f.Call("collect.run");
            Check(stub.Calls == 2 && stub.Operations[^1] == "collect.run" && !f.App.IsBusy && !f.App.Browser.IsOpen,
                "Idempotence must affect startup only; the user's explicit collection command must still use the same handler.");
        });

        Add("missing bootstrap, foreign token and pre-cancelled request do not consume the valid offer", async f =>
        {
            var stub = new CollectionStub(f.App);
            Check(!Started(await f.Call("collect.startup", new { token = "before-bootstrap" })) && stub.Calls == 0,
                "No startup collection may be manufactured before a bootstrap offer exists.");
            var offer = Offer(await f.Call("app.bootstrap")); var token = Token(offer);
            Check(!Started(await f.Call("collect.startup", new { token = token + "-wrong" })) && stub.Calls == 0,
                "A foreign token must not launch collection.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(f.Call("collect.startup", new { token }, cancelled.Token));
            Check(!Offer(await f.Call("app.bootstrap")).GetProperty("attempted").GetBoolean(),
                "Rejected or already cancelled requests must leave the genuine offer usable.");
            Check(Started(await f.Call("collect.startup", new { token })) && stub.Calls == 1,
                "The valid startup token must remain usable exactly once.");
        });

        foreach (var changed in new[] { "source", "configuration", "observation" })
            Add("a changed " + changed + " invalidates the initial offer without collecting later", async f =>
            {
                var stub = new CollectionStub(f.App); var token = Token(Offer(await f.Call("app.bootstrap")));
                if (changed == "observation") f.App.LoadSessionFixture(SessionFixture.Data());
                else
                {
                    var next = f.App.Settings.Copy();
                    if (changed == "source") next.PortalUrl = SessionFixture.SourceB; else next.MaxRows++;
                    await f.Call("settings.save", new { settings = next, revision = f.App.Revision });
                }
                var current = f.App.Result; var revision = f.App.Revision;
                var response = await f.Call("collect.startup", new { token });
                Check(!Started(response) && response.GetProperty("startupCollection").GetProperty("attempted").GetBoolean()
                    && stub.Calls == 0 && f.App.Revision == revision && ReferenceEquals(current, f.App.Result),
                    "An offer for an obsolete source, configuration or observation must be consumed without changing the current work.");
                Check(!Started(await f.Call("collect.startup", new { token })) && stub.Calls == 0,
                    "An obsolete offer must not become a deferred automatic launch.");
            });

        Add("foreground work skips startup immediately and never launches it after the gate becomes idle", async f =>
        {
            var stub = new CollectionStub(f.App); var token = Token(Offer(await f.Call("app.bootstrap")));
            var entered = Signal(); var release = Signal();
            f.App.RegisterSessionFixture("test.startupForeground", async (_, ct) =>
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); return null; });
            var foreground = f.Call("test.startupForeground"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                var response = await f.Call("collect.startup", new { token }).WaitAsync(TimeSpan.FromSeconds(1));
                Check(!Started(response) && response.GetProperty("startupCollection").GetProperty("reason").GetString() == "busy"
                    && response.GetProperty("startupCollection").GetProperty("attempted").GetBoolean() && stub.Calls == 0,
                    "Automatic startup must not wait behind the user's active operation.");
            }
            finally { release.TrySetResult(); await foreground; }
            Check(!Started(await f.Call("collect.startup", new { token })) && stub.Calls == 0 && !f.App.IsBusy,
                "Releasing foreground work must not trigger a late startup collection.");
        });

        Add("background work also prevents a queued automatic collection", async f =>
        {
            var stub = new CollectionStub(f.App); var token = Token(Offer(await f.Call("app.bootstrap")));
            var entered = Signal(); var release = Signal();
            var work = f.App.Jobs.RunAsync("startup-fixture", false, ct =>
            { entered.TrySetResult(); release.Task.Wait(ct); return 1; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                var response = await f.Call("collect.startup", new { token }).WaitAsync(TimeSpan.FromSeconds(1));
                Check(!Started(response) && response.GetProperty("startupCollection").GetProperty("reason").GetString() == "busy"
                    && stub.Calls == 0, "Background computation must count as active work before admitting startup collection.");
            }
            finally { release.TrySetResult(); await work; }
            Check(!Started(await f.Call("collect.startup", new { token })) && stub.Calls == 0,
                "Startup skipped for background work must stay skipped after that work finishes.");
        });

        Add("an incomplete initial configuration remains skipped during the same opening", async f =>
        {
            var stub = new CollectionStub(f.App); f.App.Settings.PortalUrl = "";
            var initial = Offer(await f.Call("app.bootstrap"));
            Check(!initial.GetProperty("eligible").GetBoolean() && initial.GetProperty("reason").GetString() == "configuration-incomplete",
                "A first launch without its source must show an explicit local configuration state.");
            f.App.Settings.PortalUrl = SessionFixture.SourceA;
            var later = Offer(await f.Call("app.bootstrap"));
            Check(!later.GetProperty("eligible").GetBoolean() && !Started(await f.Call("collect.startup", new { token = Token(later) }))
                && stub.Calls == 0 && !f.App.Browser.IsOpen,
                "Editing configuration after startup must not surprise the user with a delayed collection during a later bootstrap.");
        });

        foreach (var sourceKind in new[] { "file", "local", "history" })
            Add("an active " + sourceKind + " observation remains local", async f =>
            {
                var data = SessionFixture.Data();
                if (sourceKind == "history") f.App.LoadSessionFixture(data, new() { Data = data, Dashboard = SessionFixture.MappingA() });
                else { data.Provenance.SourceKind = sourceKind; data.Provenance.Method = sourceKind == "local" ? "ticket-library" : "csv"; f.App.LoadSessionFixture(data); }
                var stub = new CollectionStub(f.App); var previous = f.App.Result;
                var offer = Offer(await f.Call("app.bootstrap"));
                Check(!offer.GetProperty("eligible").GetBoolean() && !Started(await f.Call("collect.startup", new { token = Token(offer) }))
                    && stub.Calls == 0 && ReferenceEquals(previous, f.App.Result) && !f.App.Browser.IsOpen,
                    "A local file, local library or deliberately opened archive must not be replaced by automatic network work.");
            });

        Add("the demonstration never dispatches a real or simulated startup collection", async f =>
        {
            await f.Call("app.demo"); var stub = new CollectionStub(f.App); var previous = f.App.Result;
            var offer = Offer(await f.Call("app.bootstrap"));
            Check(!offer.GetProperty("eligible").GetBoolean() && offer.GetProperty("reason").GetString() == "simulation"
                && !Started(await f.Call("collect.startup", new { token = Token(offer) }))
                && stub.Calls == 0 && f.App.IsTestMode && ReferenceEquals(previous, f.App.Result) && !f.App.Browser.IsOpen,
                "Startup in a demonstration must preserve its disposable data and never reach either collection handler.");
        });

        Add("a restored file cache does not offer browser collection", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import");
            await using var reopened = Reopen(f); var stub = new CollectionStub(reopened);
            var boot = await Call(reopened, "app.bootstrap"); var offer = Offer(boot); var previous = reopened.Result;
            Check(reopened.IsCachedSource && reopened.IsFileSource && previous is not null && !offer.GetProperty("eligible").GetBoolean()
                && !Started(await Call(reopened, "collect.startup", new { token = Token(offer) }))
                && stub.Calls == 0 && ReferenceEquals(previous, reopened.Result),
                "Bootstrap must classify the restored file before deciding whether automatic collection is appropriate.");
        });

        Add("a restored GLPI cache stays visible until the active configured search replaces it", async f =>
        {
            var cached = SessionFixture.Data(); cached.Provenance.SourceKind = "browser";
            await f.App.RememberSourceCacheFixtureAsync(cached);
            var current = f.App.Settings.Copy(); current.Sources.Single(source => source.Id == SessionFixture.ProfileB).ApplyTo(current);
            current.ActiveSourceId = SessionFixture.ProfileB;
            await using var reopened = Reopen(f, current); var stub = new CollectionStub(reopened);
            var offer = Offer(await Call(reopened, "app.bootstrap")); var previous = reopened.Result;
            Check(reopened.IsCachedSource && !reopened.IsFileSource && previous is not null && offer.GetProperty("eligible").GetBoolean(),
                "A prior GLPI observation may be shown locally while a valid startup refresh is offered.");
            stub.Run = _ =>
            {
                Check(ReferenceEquals(previous, reopened.Result) && reopened.Settings.PortalUrl == SessionFixture.SourceB,
                    "The cache must remain visible and must never replace the active configured search parameters.");
                var next = SessionFixture.Data(); next.SourceUrl = SessionFixture.SourceB; next.Provenance.RequestedUrl = SessionFixture.SourceB;
                reopened.LoadSessionFixture(next); return Task.FromResult<object?>(new { completed = true });
            };
            Check(Started(await Call(reopened, "collect.startup", new { token = Token(offer) })) && stub.Calls == 1
                && stub.Sources.Single() == SessionFixture.SourceB && !ReferenceEquals(previous, reopened.Result) && !reopened.IsCachedSource,
                "Only successful ordinary collection may replace the old cached observation with the configured source.");
        });

        Add("cancellation reaches startup immediately and preserves the previous observation without retry", async f =>
        {
            var stub = new CollectionStub(f.App); var previous = f.App.Result; var revision = f.App.Revision;
            var entered = Signal(); var cancelled = false;
            stub.Run = async ct =>
            {
                Check(ReferenceEquals(previous, f.App.Result), "Startup must not clear the last observation before collection succeeds.");
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); return null; }
                finally { cancelled = ct.IsCancellationRequested; }
            };
            var token = Token(Offer(await f.Call("app.bootstrap"))); var run = f.Call("collect.startup", new { token });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var control = await f.Call("operations.cancel").WaitAsync(TimeSpan.FromSeconds(1));
            await Cancelled(run.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(control.GetProperty("cancelled").GetBoolean() && cancelled && !f.App.IsBusy && ReferenceEquals(previous, f.App.Result)
                && f.App.Revision == revision && stub.Calls == 1,
                "The normal immediate cancellation control must reach startup and leave the previous result and its revision intact.");
            Check(!Started(await f.Call("collect.startup", new { token })) && stub.Calls == 1
                && Offer(await f.Call("app.bootstrap")).GetProperty("attempted").GetBoolean(),
                "A cancelled startup attempt must not restart automatically on reload.");
        });

        foreach (var operation in new[] { "records.list", "workplace.ticket.list", "workplace.ticket.open" })
            Add(operation + " interrupts automatic collection and waits for its independent restoration before reading", async f =>
            {
                var stub = new CollectionStub(f.App); var previous = f.App.Result; var revision = f.App.Revision;
                var entered = Signal(); var restoring = Signal(); var restoreRelease = Signal(); var restored = false;
                stub.Run = async ct =>
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); return null; }
                    finally { restoring.TrySetResult(); await restoreRelease.Task; restored = true; }
                };
                var token = Token(Offer(await f.Call("app.bootstrap")));
                var startup = f.Call("collect.startup", new { token }); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var read = f.Call(operation, new { revision, rowIndex = 0, limit = 1 });
                try
                {
                    await restoring.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    Check(!read.IsCompleted && !restored && f.App.IsBusy && f.App.CurrentOperation == "collect.startup",
                        "Ticket consultation must request cancellation immediately but cannot read until independent browser restoration releases the gate.");
                }
                finally { restoreRelease.TrySetResult(); }
                await Cancelled(startup.WaitAsync(TimeSpan.FromSeconds(3)));
                var result = await read.WaitAsync(TimeSpan.FromSeconds(3));
                Check(restored && result.ValueKind == JsonValueKind.Object && !f.App.IsBusy
                    && ReferenceEquals(previous, f.App.Result) && f.App.Revision == revision && stub.Calls == 1 && !f.App.Browser.IsOpen,
                    "The real local ticket/list handler must complete against the preceding observation after startup restoration, without recollecting or opening the browser.");
                if (operation == "workplace.ticket.open") Check(result.GetProperty("personal").GetProperty("id").GetString() == "42", "The requested ticket must remain the original source ticket.");
                else Check(result.GetProperty("rows").GetArrayLength() == 1, "The original observed row must remain available after startup interruption.");
                Check(!Started(await f.Call("collect.startup", new { token })) && stub.Calls == 1,
                    "A startup attempt interrupted for consultation must never relaunch after the ticket read.");
            });

        Add("explicit ticket navigation interrupts only the matching startup and returns before restoration completes", async f =>
        {
            var stub = new CollectionStub(f.App); var entered = Signal(); var restoring = Signal(); var restoreRelease = Signal();
            CancellationToken observed = default;
            stub.Run = async ct =>
            {
                observed = ct; entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); return null; }
                finally { restoring.TrySetResult(); await restoreRelease.Task; }
            };
            var token = Token(Offer(await f.Call("app.bootstrap")));
            var startup = f.Call("collect.startup", new { token }); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                var foreign = await f.Call("collect.startup.interrupt", new { token = token + "-other" }).WaitAsync(TimeSpan.FromSeconds(1));
                Check(!foreign.GetProperty("interrupted").GetBoolean() && !observed.IsCancellationRequested,
                    "An unrelated opening token must not stop the active startup collection.");
                var interrupt = await f.Call("collect.startup.interrupt", new { token }).WaitAsync(TimeSpan.FromSeconds(1));
                await restoring.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Check(interrupt.GetProperty("interrupted").GetBoolean() && observed.IsCancellationRequested && !startup.IsCompleted
                    && interrupt.GetProperty("startupCollection").GetProperty("reason").GetString() == "ticket-consultation",
                    "Navigation control must bypass the business queue without claiming that browser restoration has already finished.");
                var again = await f.Call("collect.startup.interrupt", new { token }).WaitAsync(TimeSpan.FromSeconds(1));
                Check(!again.GetProperty("interrupted").GetBoolean(), "Repeated navigation must not send another cancellation command.");
            }
            finally { restoreRelease.TrySetResult(); }
            await Cancelled(startup.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(!(await f.Call("collect.startup.interrupt", new { token })).GetProperty("interrupted").GetBoolean() && !f.App.IsBusy,
                "The automatic cancellation source must be released after cleanup, so later navigation cannot touch a completed operation.");
        });

        Add("ordinary home and identity reads do not interrupt automatic collection", async f =>
        {
            var stub = new CollectionStub(f.App); var entered = Signal(); var release = Signal(); CancellationToken observed = default;
            stub.Run = async ct => { observed = ct; entered.TrySetResult(); await release.Task.WaitAsync(ct); return new { completed = true }; };
            var token = Token(Offer(await f.Call("app.bootstrap")));
            var startup = f.Call("collect.startup", new { token }); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var identity = f.Call("identity.get"); var workspace = f.Call("workplace.get"); var alerts = f.Call("alerts.get");
            try
            {
                Check(!observed.IsCancellationRequested && !startup.IsCompleted && !identity.IsCompleted && !workspace.IsCompleted && !alerts.IsCompleted,
                    "Background identity, workspace and alert updates must stay queued and must not abort an automatic collection on Home.");
            }
            finally { release.TrySetResult(); }
            Check(Started(await startup.WaitAsync(TimeSpan.FromSeconds(3))), "Ordinary display updates must allow startup to finish normally.");
            await Task.WhenAll(identity, workspace, alerts).WaitAsync(TimeSpan.FromSeconds(3));
        });

        Add("ticket consultation and stale startup control never cancel a manual collection", async f =>
        {
            var stub = new CollectionStub(f.App); var token = Token(Offer(await f.Call("app.bootstrap")));
            Check(Started(await f.Call("collect.startup", new { token })), "The fixture must first finish its one automatic attempt.");
            var entered = Signal(); var release = Signal(); CancellationToken observed = default;
            stub.Run = async ct => { observed = ct; entered.TrySetResult(); await release.Task.WaitAsync(ct); return new { completed = true }; };
            var manual = f.Call("collect.run"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var read = f.Call("records.list", new { revision = f.App.Revision, limit = 1 });
            try
            {
                var interrupt = await f.Call("collect.startup.interrupt", new { token }).WaitAsync(TimeSpan.FromSeconds(1));
                Check(!interrupt.GetProperty("interrupted").GetBoolean() && !observed.IsCancellationRequested && !read.IsCompleted,
                    "The startup-only control and ticket priority must leave an explicitly launched manual collection running under its usual gate.");
            }
            finally { release.TrySetResult(); }
            await manual.WaitAsync(TimeSpan.FromSeconds(3)); await read.WaitAsync(TimeSpan.FromSeconds(3));
            Check(stub.Calls == 2 && !f.App.IsBusy, "Manual collection and its waiting local read must both finish normally.");
        });

        Add("failure preserves the restored GLPI cache, original error and completed startup attempt", async f =>
        {
            var cached = SessionFixture.Data(); cached.Provenance.SourceKind = "browser";
            await f.App.RememberSourceCacheFixtureAsync(cached);
            var cachePath = Path.Combine(f.Root, "LastSource", "current.json"); var saved = await File.ReadAllBytesAsync(cachePath);
            await using var reopened = Reopen(f); var stub = new CollectionStub(reopened);
            var offer = Offer(await Call(reopened, "app.bootstrap")); var previous = reopened.Result; var revision = reopened.Revision;
            var expected = new InvalidOperationException("synthetic collection failure");
            stub.Run = _ => Task.FromException<object?>(expected);
            Exception? actual = null;
            try { await Call(reopened, "collect.startup", new { token = Token(offer) }); }
            catch (InvalidOperationException error) { actual = error; }
            var remainingCache = await File.ReadAllBytesAsync(cachePath);
            Check(ReferenceEquals(expected, actual) && stub.Calls == 1 && !reopened.IsBusy && reopened.IsCachedSource
                && ReferenceEquals(previous, reopened.Result) && reopened.Revision == revision
                && saved.SequenceEqual(remainingCache),
                "A startup failure must propagate unchanged and must not clear, replace or rewrite the preceding successful cached result.");
            Check(!Started(await Call(reopened, "collect.startup", new { token = Token(offer) })) && stub.Calls == 1,
                "A failed opening must remain attempted instead of forming an automatic retry loop.");
        });

        Add("post-bootstrap availability, editor, lock and explicit-collection guards prevent dispatch", async f =>
        {
            foreach (var guard in new[] { "browser", "network", "editor", "locked", "manual" })
            {
                await using var app = Reopen(f); app.LoadSessionFixture(SessionFixture.Data());
                var stub = new CollectionStub(app); var offer = Offer(await Call(app, "app.bootstrap"));
                if (guard == "browser") app.SetStartupAvailabilityFixture(browserAvailable: false, networkAvailable: true);
                else if (guard == "network") app.SetStartupAvailabilityFixture(browserAvailable: true, networkAvailable: false);
                else if (guard == "editor") app.EditorOpen = true;
                else if (guard == "locked") app.SessionLocked = true;
                else await Call(app, "collect.run");
                var expectedCalls = guard == "manual" ? 1 : 0;
                var response = await Call(app, "collect.startup", new { token = Token(offer) });
                Check(!Started(response) && stub.Calls == expectedCalls && !app.Browser.IsOpen
                    && response.GetProperty("startupCollection").GetProperty("attempted").GetBoolean(),
                    "The observed " + guard + " guard must be rechecked after a valid offer and must prevent automatic dispatch.");
                app.EditorOpen = false; app.SessionLocked = false;
                app.SetStartupAvailabilityFixture(browserAvailable: true, networkAvailable: true);
                Check(!Started(await Call(app, "collect.startup", new { token = Token(offer) })) && stub.Calls == expectedCalls,
                    "Clearing a guard must not launch a late collection or duplicate a completed explicit collection.");
            }
        });
        Add("complete deployment export and replacement preserve the receiving workstation's startup choice", async f =>
        {
            var previous = f.App.Result; var path = Path.Combine(f.Root, DeploymentConfigurationService.FileName);
            foreach (var enabled in new[] { false, true })
            {
                var current = f.App.Settings.Copy(); current.Automation.CollectOnStartup = enabled; current.Save();
                var incoming = current.Copy(); incoming.Automation.CollectOnStartup = !enabled;
                DeploymentConfigurationService.Export(path, incoming, f.Root);
                Check(!(await File.ReadAllTextAsync(path)).Contains("CollectOnStartup", StringComparison.OrdinalIgnoreCase),
                    "A complete portable deployment must exclude the local startup choice just like team configuration does.");
                var document = DeploymentConfigurationService.Read(path, f.Root).Configuration;
                var applied = DeploymentConfigurationService.Apply(document, current, f.Root);
                Check(applied.Settings.Automation.CollectOnStartup == enabled && AppSettings.Load().Automation.CollectOnStartup == enabled
                    && current.Automation.CollectOnStartup == enabled && ReferenceEquals(previous, f.App.Result),
                    "Applying a full deployment must preserve and persist the receiving workstation's explicit startup choice without changing the observation.");
                Check(!(await File.ReadAllTextAsync(applied.BackupPath)).Contains("CollectOnStartup", StringComparison.OrdinalIgnoreCase),
                    "Portable rollback copies must not begin sharing the local startup preference either.");
            }
        });
        return tests;
    }

    private static JsonElement Offer(JsonElement bootstrap) => bootstrap.GetProperty("startupCollection");
    private static string Token(JsonElement offer) => offer.GetProperty("token").GetString() ?? "";
    private static bool Started(JsonElement response) => response.GetProperty("started").GetBoolean();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Cancelled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected startup cancellation.");
    }
    private static PilotApplication Reopen(SessionFixture fixture, AppSettings? settings = null) => new(new SessionHost(), (_, _) => { },
        settings ?? fixture.App.Settings.Copy(), snapshots: new SnapshotStore(Path.Combine(fixture.Root, "startup-history")));
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object? args = null) => JsonSerializer.SerializeToElement(
        await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json)), PilotApplication.Json);

    private sealed class CollectionStub
    {
        public int Calls { get; private set; }
        public bool AllObservedBusy { get; private set; } = true;
        public List<string> Operations { get; } = new();
        public List<string> Sources { get; } = new();
        public Func<CancellationToken, Task<object?>> Run { get; set; } = _ => Task.FromResult<object?>(new { completed = true });
        public CollectionStub(PilotApplication app)
        {
            app.ConfigureStartupCollectionFixture((_, ct) =>
            {
                Calls++; AllObservedBusy &= app.IsBusy; Operations.Add(app.CurrentOperation); Sources.Add(app.Settings.PortalUrl);
                Check(!app.Browser.IsOpen, "A startup fixture must dispatch through the injected normal handler without opening a real browser.");
                return Run(ct);
            });
        }
    }
}

namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void ConfigureStartupCollectionFixture(Func<JsonElement, CancellationToken, Task<object?>> collect)
        {
            _handlers["collect.run"] = collect;
            _handlers["collect.campaign"] = (_, _) => Task.FromException<object?>(new InvalidOperationException("Startup must not dispatch a campaign."));
            _handlers["automation.recipe"] = (_, _) => Task.FromException<object?>(new InvalidOperationException("Startup must not dispatch a recipe."));
            SetStartupAvailabilityFixture(browserAvailable: true, networkAvailable: true);
            _simulation?.ConfigureStartupCollectionFixture(collect);
        }
        public void SetStartupAvailabilityFixture(bool browserAvailable, bool networkAvailable)
        {
            _startupBrowserAvailable = _ => browserAvailable;
            _networkAvailable = () => networkAvailable;
            _simulation?.SetStartupAvailabilityFixture(browserAvailable, networkAvailable);
        }
    }
}
