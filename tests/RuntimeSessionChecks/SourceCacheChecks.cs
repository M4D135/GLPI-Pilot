using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class SourceCacheChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("Source cache: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("a successful CSV survives deletion of its original and a fresh application bootstrap with its interpretation and quality", async f =>
        {
            var before = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json);
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); var original = f.App.Result!;
            var state = await f.Call("cache.state"); Check(state.GetProperty("available").GetBoolean() && state.GetProperty("sourceKind").GetString() == "file");
            File.Delete(f.Host.OpenPath!);
            await using var reopened = Reopen(f);
            var bootstrap = await Call(reopened, "app.bootstrap"); var session = bootstrap.GetProperty("session");
            Check(reopened.IsCachedSource && reopened.IsFileSource && !reopened.IsTestMode && reopened.IsHistorical);
            Check(session.GetProperty("cachedSource").GetBoolean() && session.GetProperty("fileReadComplete").GetBoolean()
                && !session.GetProperty("capabilities").GetProperty("ticketDetails").GetBoolean());
            Check(JsonSerializer.Serialize(reopened.Result!.Rows) == JsonSerializer.Serialize(original.Rows) && reopened.Result.ExtractedAt == original.ExtractedAt
                && JsonSerializer.Serialize(reopened.Settings, PilotApplication.Json) == before);
            Check((await Call(reopened, "records.cell", new { revision = reopened.Revision, row = 0, column = 0 })).GetProperty("raw").GetString() == "0042");
        });
        Add("the selected XLSX sheet is durable and a later mapping correction remains available offline", async f =>
        {
            f.Host.OpenPath = OfflineImportChecks.WriteWorkbook(f.Root); var selected = await f.Call("records.import");
            await f.Call("records.import", new { token = selected.GetProperty("token").GetString(), sheetName = "Incidents", headerRow = 4 });
            var mapping = f.App.EffectiveSettings.Dashboard.Copy(); mapping.ColumnMappings["title"] = "ID";
            await f.Call("mapping.save", new { revision = f.App.Revision, settings = mapping }); File.Delete(f.Host.OpenPath);
            await using var reopened = Reopen(f); await Call(reopened, "app.bootstrap");
            Check(reopened.IsCachedSource && reopened.Result!.Provenance.Method == "xlsx" && reopened.EffectiveSettings.Dashboard.ColumnMappings["title"] == "ID"
                && reopened.Settings.Dashboard.ColumnMappings["title"] == "Titre");
        });
        Add("failed or cancelled imports keep the preceding cache and do not replace its receipt", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); var before = CacheBytes(f);
            f.Host.OpenPath = Path.Combine(f.Root, "invalid.csv"); await File.WriteAllTextAsync(f.Host.OpenPath, "<html>not a list</html>");
            await Refuse(() => f.Call("records.import")); Check(before.SequenceEqual(CacheBytes(f)));
            f.Host.OpenPath = null; await f.Call("records.import"); Check(before.SequenceEqual(CacheBytes(f)));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Refuse(() => f.Call("records.import", ct: cancelled.Token)); Check(before.SequenceEqual(CacheBytes(f)));
        });
        Add("demo exploration and its simulated collection never overwrite or clear the real cache", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); var before = CacheBytes(f);
            await f.Call("app.demo"); Check(!(await f.Call("cache.state")).GetProperty("available").GetBoolean());
            await Refuse(() => f.Call("cache.restore")); await Refuse(() => f.Call("cache.clear", new { confirmed = true }));
            await f.Call("collect.run"); await f.Call("records.test.exit"); Check(before.SequenceEqual(CacheBytes(f)));
            await f.Call("cache.restore"); Check(f.App.IsCachedSource && f.App.IsFileSource && !f.App.IsTestMode);
        });
        Add("a damaged latest payload falls back to the verified previous receipt and reports recovery", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import");
            await OfflineImportChecks.WriteCsv(f, "new.csv", 2); await f.Call("records.import");
            var entry = new LastSourceCacheStore().Describe()!;
            await File.AppendAllTextAsync(Path.Combine(f.Root, "LastSource", entry.Generation, entry.SnapshotId + ".json.gz"), "corrupt");
            await using var reopened = Reopen(f); var reply = await Call(reopened, "app.bootstrap");
            Check(reopened.IsCachedSource && reopened.Result!.Rows.Count == 1 && reply.GetProperty("session").GetProperty("cachedRecovered").GetBoolean());
            Check((await Call(reopened, "cache.state")).GetProperty("recovered").GetBoolean());
        });
        Add("a damaged atomic index recovers without silently presenting corrupt bytes", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); await OfflineImportChecks.WriteCsv(f, "new.csv", 2); await f.Call("records.import");
            await File.WriteAllTextAsync(Path.Combine(f.Root, "LastSource", "current.json"), "damaged index");
            await using var reopened = Reopen(f); await Call(reopened, "app.bootstrap");
            Check(reopened.IsCachedSource && reopened.Result!.Rows.Count == 1 && (await Call(reopened, "cache.state")).GetProperty("recovered").GetBoolean());
        });
        Add("a new save after corruption keeps the last verified fallback instead of the corrupt receipt", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import");
            await OfflineImportChecks.WriteCsv(f, "second.csv", 2); await f.Call("records.import");
            var entry = new LastSourceCacheStore().Describe()!;
            await File.WriteAllTextAsync(Path.Combine(f.Root, "LastSource", entry.Generation, entry.SnapshotId + ".json.gz"), "corrupt second");
            await f.Call("cache.restore"); Check(f.App.Result!.Rows.Count == 1);
            await OfflineImportChecks.WriteCsv(f, "third.csv", 3); await f.Call("records.import");
            entry = new LastSourceCacheStore().Describe()!;
            await File.WriteAllTextAsync(Path.Combine(f.Root, "LastSource", entry.Generation, entry.SnapshotId + ".json.gz"), "corrupt third");
            await using var reopened = Reopen(f); await Call(reopened, "app.bootstrap");
            Check(reopened.IsCachedSource && reopened.Result!.Rows.Count == 1 && Directory.EnumerateDirectories(Path.Combine(f.Root, "LastSource")).Count() == 2);
        });
        Add("explicit cache clearing preserves the live database and settings and cannot restore the cleared fallback", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); await OfflineImportChecks.WriteCsv(f, "new.csv", 2); await f.Call("records.import");
            var preferences = new LibraryPreferencesStore().Load(); var library = new TicketLibraryStore(preferences.DatabasePath);
            await library.ObserveSeedAsync(new TicketLibrarySeed { Ticket = new("77", "https://synthetic.invalid/front/ticket.form.php?id=77"), Title = "À conserver" });
            var before = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json);
            await Refuse(() => f.Call("cache.clear")); Check((await f.Call("cache.state")).GetProperty("available").GetBoolean());
            await f.Call("cache.restore"); Check(f.App.IsCachedSource);
            await f.Call("cache.clear", new { confirmed = true }); Check(!(await f.Call("cache.state")).GetProperty("available").GetBoolean());
            Check(f.App.IsCachedSource && f.App.Result!.Rows.Count == 2);
            await using var reopened = Reopen(f); await Call(reopened, "app.bootstrap");
            Check(reopened.Result is null && (await library.GetStateAsync()).TicketCount == 1 && JsonSerializer.Serialize(reopened.Settings, PilotApplication.Json) == before);
            Check(!Directory.EnumerateDirectories(Path.Combine(f.Root, "LastSource")).Any());
        });
        Add("a browser receipt restores without credentials, current source replacement or automatic network work", async f =>
        {
            var data = SessionFixture.Data(); data.SourceUrl += "&access_token=secret-token"; data.Provenance.RequestedUrl = data.SourceUrl;
            data.Provenance.SourceKind = "browser"; // AutomaticExtractionService's actual provenance.
            f.App.Settings.PortalUrl = data.SourceUrl;
            new AnalyticsPopulationStore().Save(new[] { new SitePopulationEntry { SourceKey = SnapshotStore.SourceKey(data), SiteId = Guid.NewGuid().ToString(),
                SiteLabel = "Site fictif", FromDate = "2026-01-01", UntilDate = "2026-12-31", Count = 25 } });
            await f.App.RememberSourceCacheFixtureAsync(data);
            var loaded = new LastSourceCacheStore().Load()!;
            var json = JsonSerializer.Serialize(loaded.Document);
            Check(!json.Contains("secret-token", StringComparison.Ordinal) && loaded.Document.Data.SourceUrl == AppPaths.SafeUrl(data.SourceUrl));
            Check(loaded.Document.Populations is { Count: 1 } && loaded.Document.Populations[0].Count == 25
                && loaded.Document.Populations[0].SourceKey == loaded.Document.Metadata.SourceKey
                && loaded.Document.Metadata.SourceKey == SnapshotStore.SourceKey(data));
            var current = f.App.Settings.Copy(); current.PortalUrl = SessionFixture.SourceB; current.History.RestoreLastOnStartup = false;
            await using var reopened = new PilotApplication(new SessionHost(), (_, _) => { }, current, snapshots: new SnapshotStore(Path.Combine(f.Root, "reopened-history")));
            await Call(reopened, "app.bootstrap"); Check(reopened.IsCachedSource && !reopened.IsTestMode && reopened.Settings.PortalUrl == SessionFixture.SourceB);
            var invoked = false; reopened.ForceScheduledFixture((_, _) => { invoked = true; return Task.FromResult<object?>(null); });
            reopened.ReplaceSourceCacheFixture("library.tick", (_, _) => { invoked = true; return Task.FromResult<object?>(null); });
            await reopened.TickAsync(); Check(!invoked);
            await Refuse(() => Call(reopened, "workplace.ticket.open", new { rowIndex = 0, revision = reopened.Revision }));
            await Refuse(() => Call(reopened, "library.capture"));
        });
        Add("a sanitized browser cache retains its opaque source identity and historical indicator observations", async f =>
        {
            var data = SessionFixture.Data(); var sourceKey = SnapshotStore.SourceKey(data);
            var history = new SnapshotStore(Path.Combine(f.Root, "cache-history")); history.Save(data, f.App.Settings);
            await f.App.RememberSourceCacheFixtureAsync(data);
            await using var reopened = new PilotApplication(new SessionHost(), (_, _) => { }, f.App.Settings.Copy(), snapshots: history);
            await Call(reopened, "app.bootstrap");
            var dashboard = await Call(reopened, "dashboard.get");
            Check(dashboard.GetProperty("sourceKey").GetString() == sourceKey
                && (await Call(reopened, "history.list")).GetProperty("currentSourceKey").GetString() == sourceKey);
            var observations = await Call(reopened, "dashboard.history", new { runId = dashboard.GetProperty("runId").GetString(), revision = reopened.Revision });
            Check(observations.GetProperty("widgets").EnumerateArray().Any(widget => widget.TryGetProperty("history", out var series)
                && series.ValueKind == JsonValueKind.Object && series.GetProperty("points").GetArrayLength() > 0));
        });
        Add("bounded serialization failure preserves the previous atomic pointer and a corrupt cache never blocks startup", async f =>
        {
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); var before = CacheBytes(f);
            var invalid = SessionFixture.Data(); invalid.Headers.AddRange(Enumerable.Repeat("column", 16384));
            await Refuse(() => Task.Run(() => new LastSourceCacheStore().Save(invalid, f.App.Settings))); Check(before.SequenceEqual(CacheBytes(f)));
            var entry = new LastSourceCacheStore().Describe()!;
            await File.WriteAllTextAsync(Path.Combine(f.Root, "LastSource", entry.Generation, entry.SnapshotId + ".json.gz"), "broken");
            await using var reopened = Reopen(f); await Call(reopened, "app.bootstrap");
            Check(reopened.Result is null && (await Call(reopened, "cache.state")).GetProperty("error").GetString()!.Length > 0);
        });
        return tests;
    }
    private static byte[] CacheBytes(SessionFixture f) => File.ReadAllBytes(Path.Combine(f.Root, "LastSource", "current.json"));
    private static PilotApplication Reopen(SessionFixture f) => new(new SessionHost(), (_, _) => { }, f.App.Settings.Copy(), snapshots: new SnapshotStore(Path.Combine(f.Root, "reopened-history")));
    private static async Task<JsonElement> Call(PilotApplication app, string op, object? args = null) => JsonSerializer.SerializeToElement(await app.ExecuteAsync(op, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json)), PilotApplication.Json);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Source cache assertion failed."); }
    private static async Task Refuse(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cache refusal.");
    }
}
namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void ReplaceSourceCacheFixture(string name, Func<JsonElement, CancellationToken, Task<object?>> handler) => _handlers[name] = handler;
        public async Task RememberSourceCacheFixtureAsync(ExtractionResult data)
        {
            await RememberSourceAsync(data, Settings.Copy(), null, CancellationToken.None);
            SetResult(data);
        }
    }
}
