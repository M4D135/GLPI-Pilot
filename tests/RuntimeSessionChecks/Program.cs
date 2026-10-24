using System.Collections.Concurrent;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

var tests = new List<(string Name, Func<Task> Run)>();
void Add(string name, Func<SessionFixture, Task> run) => tests.Add((name, async () => { await using var f = new SessionFixture(); await run(f); }));

Add("Changing the configured search detaches a live result without rewriting its saved profile", async f =>
{
    var revision = f.App.Revision; var next = f.App.Settings.Copy(); next.PortalUrl = SessionFixture.SourceB;
    var response = await f.Call("settings.save", new { settings = next, revision });
    Check(f.App.Result is null && f.App.Revision > revision && f.App.Settings.ActiveSourceId == "");
    Check(f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileA).Url == SessionFixture.SourceA);
    Check(response.GetProperty("revision").GetInt64() == f.App.Revision && !response.GetProperty("session").GetProperty("loaded").GetBoolean());
    Check(f.Events.Any(e => e.Name == "session.changed"));
});
Add("Selecting another profile invalidates old forms and installs that profile's interpretation", async f =>
{
    var revision = f.App.Revision; var stale = f.App.Settings.Copy(); await f.SelectB();
    Check(f.App.Result is null && f.App.Settings.PortalUrl == SessionFixture.SourceB && f.App.Settings.Dashboard.ColumnMappings["id"] == "Autre ID");
    await Refuse(() => f.Call("settings.save", new { settings = stale, revision }), "changé");
    Check(f.App.Settings.PortalUrl == SessionFixture.SourceB);
});
Add("Editing the active source applies its changed URL and clears its previous result", async f =>
{
    var source = f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileA).Copy(); source.Url = SessionFixture.SourceB;
    var revision = f.App.Revision; await f.Call("sources.save", new { source, revision });
    Check(f.App.Settings.PortalUrl == SessionFixture.SourceB && f.App.Result is null && f.App.Revision > revision);
    Check(AppSettings.Load().Sources.Single(s => s.Id == SessionFixture.ProfileA).Url == SessionFixture.SourceB);
});
Add("Editing an inactive recipe preserves the active observation but invalidates stale configuration forms", async f =>
{
    var source = f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileB).Copy(); source.Name = "Source B modifiée";
    var data = f.App.Result; var revision = f.App.Revision; await f.Call("sources.save", new { source, revision });
    Check(ReferenceEquals(f.App.Result, data) && f.App.Settings.PortalUrl == SessionFixture.SourceA && f.App.Revision > revision);
    await Refuse(() => f.Call("sources.save", new { source, revision }), "changé");
});
Add("Capturing current settings explicitly updates only the named profile and preserves its choices", async f =>
{
    var source = f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileA).Copy(); source.IncludedInCampaign = false; source.RestoreWorkspace = true;
    await f.Call("sources.save", new { source, revision = f.App.Revision });
    var other = JsonSerializer.Serialize(f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileB), PilotApplication.Json);
    var previousLimit = f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileA).Acquisition["MaxRows"];
    var revision = f.App.Revision; var next = f.App.Settings.Copy(); next.MaxRows = 5432;
    await f.Call("settings.save", new { settings = next, revision });
    Check(f.App.Revision > revision && f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileA).Acquisition["MaxRows"] == previousLimit);
    await Refuse(() => f.Call("sources.capture", new { id = SessionFixture.ProfileA, revision }), "changé");
    await f.Call("sources.capture", new { id = SessionFixture.ProfileA, revision = f.App.Revision });
    var updated = f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileA);
    Check(updated.Id == source.Id && updated.Name == source.Name && !updated.IncludedInCampaign && updated.RestoreWorkspace && updated.Acquisition["MaxRows"] == "5432");
    Check(f.App.Settings.Sources.Count == 2 && JsonSerializer.Serialize(f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileB), PilotApplication.Json) == other);
    await f.SelectB(); await f.Call("sources.select", new { id = source.Id, revision = f.App.Revision }); Check(f.App.Settings.MaxRows == 5432);
});
Add("An unknown capture target never creates or overwrites a different profile", async f =>
{
    var before = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json); var revision = f.App.Revision;
    await Refuse(() => f.Call("sources.capture", new { id = "33333333333333333333333333333333", name = "Inconnu", revision }), "introuvable");
    Check(f.App.Revision == revision && JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json) == before);
    var captured = await f.Call("sources.capture", new { name = "Copie explicite", revision });
    Check(f.App.Settings.Sources.Count == 3 && f.App.Settings.ActiveSourceId == captured.GetProperty("id").GetString());
});
Add("Invalid settings never replace valid in-memory or persisted configuration", async f =>
{
    await f.Call("app.appearance", new { theme = "light" }); var revision = f.App.Revision; var previous = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json); var next = f.App.Settings.Copy(); next.MaxRows = 0;
    await Refuse(() => f.Call("settings.save", new { settings = next, revision }), "limites");
    Check(JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json) == previous && f.App.Revision == revision && AppSettings.Load().MaxRows > 0);
});
Add("Appearance changes do not obsolete unchanged data coordinates", async f =>
{
    var revision = f.App.Revision; await f.Call("app.appearance", new { theme = "light", zoom = 125 });
    Check(f.App.Revision == revision); var cell = await f.Call("records.cell", new { revision, row = 0, column = 0 }); Check(cell.GetProperty("raw").GetString() == "42");
});
Add("An explicit same-source mapping changes the revision and captures the active profile", async f =>
{
    var revision = f.App.Revision; var data = f.App.Result;
    await f.Call("mapping.save", new { settings = SessionFixture.MappingB(), revision });
    Check(ReferenceEquals(data, f.App.Result) && f.App.Revision > revision && f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "Autre ID");
    Check(f.App.Settings.Sources.Single(s => s.Id == SessionFixture.ProfileA).Interpretation!.ColumnMappings["id"] == "Autre ID");
    await Refuse(() => f.Call("records.cell", new { revision, row = 0, column = 0 }), "changé");
});
Add("An old mapping preview or save cannot reach a newly selected source", async f =>
{
    var revision = f.App.Revision; await f.SelectB();
    await Refuse(() => f.Call("mapping.preview", new { revision, settings = SessionFixture.MappingA() }), "changé");
    await Refuse(() => f.Call("mapping.save", new { revision, settings = SessionFixture.MappingA() }), "changé");
    Check(f.App.Settings.Dashboard.ColumnMappings["id"] == "Autre ID");
});
Add("Imported observations retain their own mapping while GLPI profiles change", async f =>
{
    var data = SessionFixture.Data(); data.Provenance.SourceKind = "file"; data.Provenance.SearchVerified = false; data.Provenance.ScopeVerified = false; data.SourceUrl = ""; f.App.LoadSessionFixture(data);
    await f.SelectB(); Check(ReferenceEquals(data, f.App.Result) && f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "ID" && f.App.Settings.Dashboard.ColumnMappings["id"] == "Autre ID");
    var local = SessionFixture.MappingA(); local.ColumnMappings["title"] = "Autre titre"; await f.Call("mapping.save", new { revision = f.App.Revision, settings = local });
    Check(f.App.EffectiveSettings.Dashboard.ColumnMappings["title"] == "Autre titre" && f.App.Settings.Dashboard.ColumnMappings["id"] == "Autre ID");
    await f.Call("sources.select", new { id = SessionFixture.ProfileA, revision = f.App.Revision }); Check(f.App.EffectiveSettings.Dashboard.ColumnMappings["title"] == "Autre titre" && f.App.Settings.Dashboard.ColumnMappings["title"] == "Titre");
});
Add("Actual CSV import receives local provenance and remains isolated from later source selection", async f =>
{
    f.Host.OpenPath = Path.Combine(f.Root, "local.csv"); await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut\n42;Import observé;Nouveau\n");
    await f.Call("records.import"); var result = f.App.Result; Check(result?.Provenance.SourceKind == "file"); await f.SelectB();
    Check(ReferenceEquals(f.App.Result, result) && f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "ID");
});
Add("A local mapping edit removes derived locations and retains the original imported cells", async f =>
{
    var raw = SessionFixture.Data(); raw.Provenance.SourceKind = "file"; raw.Provenance.SearchVerified = false; raw.SourceUrl = "";
    var context = f.App.Settings.Copy(); context.Cities.Cities.Add(new CityDefinition { Name = "Observation" });
    var report = CityDetectionService.Enrich(raw, context.Cities, context.Dashboard); Check(report.Applied && report.Data.Headers.Count > raw.Headers.Count);
    f.App.LoadEnrichedSessionFixture(report, context); await f.SelectB(); var revision = f.App.Revision;
    await f.Call("mapping.save", new { settings = SessionFixture.MappingB(), revision });
    Check(f.App.Locations is null && ReferenceEquals(f.App.Result, raw) && f.App.Revision > revision);
    Check(f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "Autre ID" && f.App.EffectiveSettings.Cities.Cities.Single().Name == "Observation");
    Check(raw.Rows[0][0] == "42" && raw.Headers.Count == 5);
});
Add("Deriving locations from an import preserves its captured interpretation after profile selection", async f =>
{
    var raw = SessionFixture.Data(); raw.Provenance.SourceKind = "file"; raw.Provenance.SearchVerified = false; raw.SourceUrl = "";
    var context = f.App.Settings.Copy(); context.Cities.Cities.Add(new CityDefinition { Name = "Observation" });
    f.App.LoadEnrichedSessionFixture(CityDetectionService.Enrich(raw, context.Cities, context.Dashboard), context); await f.SelectB();
    var report = CityDetectionService.Enrich(raw, f.App.EffectiveSettings.Cities, f.App.EffectiveSettings.Dashboard);
    Check(report.Applied); f.App.LoadEnrichedSessionFixture(report);
    Check(f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "ID" && f.App.EffectiveSettings.Cities.Cities.Single().Name == "Observation");
    Check(JsonSerializer.Serialize(f.App.EffectiveSettings.Queries, PilotApplication.Json) == JsonSerializer.Serialize(QueryLibrarySettings.CreateDefault(), PilotApplication.Json));
});
Add("Explicit terrain application and correction preserve the import mapping and install only the chosen geography", async f =>
{
    var raw = SessionFixture.Data(); raw.Provenance.SourceKind = "file"; raw.Provenance.SearchVerified = false; f.App.LoadSessionFixture(raw); await f.SelectB();
    var draft = await f.Call("terrain.reference.state"); var city = new CityDefinition { Name = "Observation" };
    draft = await f.Call("terrain.reference.upsert", new { kind = "city", entry = city, draftVersion = draft.GetProperty("draftVersion").GetInt32() });
    var preview = await f.Call("terrain.reference.preview", new { revision = f.App.Revision, draftVersion = draft.GetProperty("draftVersion").GetInt32() });
    await f.Call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true });
    Check(f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "ID" && f.App.EffectiveSettings.Cities.Cities.Single().Name == "Observation");
    Check(ReferenceEquals(f.App.Locations!.Source, raw) && f.App.Settings.Dashboard.ColumnMappings["id"] == "Autre ID");
    await f.Call("terrain.review.correct", new { revision = f.App.Revision, row = 0, cityId = city.Id, reason = "Lieu confirmé dans la fixture" });
    var correction = CityOverrideStore.Load(Path.Combine(f.Root, "city-overrides.json")).Overrides.Single();
    Check(correction.TicketId == "42" && f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "ID" && f.App.Locations!.ManualCount == 1);
});
Add("An archive retains its captured interpretation and cannot save over a live profile", async f =>
{
    var data = SessionFixture.Data(); var archive = new SnapshotDocument { Data = data, Dashboard = SessionFixture.MappingA(), Cities = new(), Queries = QueryLibrarySettings.CreateDefault() };
    f.App.LoadSessionFixture(data, archive); await f.SelectB();
    Check(f.App.IsHistorical && ReferenceEquals(f.App.Result, data) && f.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "ID");
    await Refuse(() => f.Call("mapping.save", new { revision = f.App.Revision, settings = SessionFixture.MappingB() }), "ne peuvent pas");
});
Add("Old record selection, view and column edits are refused after a result replacement", async f =>
{
    var revision = f.App.Revision; var columns = (await f.Call("records.columns")).GetProperty("columns"); await f.Call("app.demo");
    await Refuse(() => f.Call("records.columns", new { revision, columns }), "changé");
    await Refuse(() => f.Call("records.saveView", new { revision, name = "Vue ancienne" }), "changé");
    await Refuse(() => f.Call("records.list", new { revision, indices = new[] { 0 } }), "changé");
    await Refuse(() => f.Call("records.list", new { revision, transform = new { } }), "changé");
});
Add("A confirmed result from another search cannot authorize current GLPI writes", async f =>
{
    await f.SelectB(); f.App.LoadSessionFixture(SessionFixture.Data());
    var opened = await f.Call("workplace.ticket.open", new { revision = f.App.Revision, rowIndex = 0 }); var key = opened.GetProperty("personal").GetProperty("key").GetString();
    await Refuse(() => f.Call("workplace.actions.inspect", new { ticketKey = key }), "actuel");
});
Add("Collection choices are invalidated when their settings context changes", async f =>
{
    await f.SelectB();
    await Refuse(() => f.Call("collect.inspect", new { revision = 0, page = 0 }), "sélection");
    await Refuse(() => f.Call("collect.table", new { revision = 0, index = 0 }), "sélection");
    await Refuse(() => f.Call("collect.manual", new { revision = 0, followingPages = false }), "sélection");
});
Add("Immediate cancel reaches an active task and a queued mutation without waiting for the execution gate", async f =>
{
    var started = Signal(); var cleanup = Signal(); var release = Signal(); var mutations = 0;
    f.App.RegisterSessionFixture("test.wait", async (_, ct) => { started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); return null; } finally { cleanup.TrySetResult(); await release.Task; } });
    f.App.RegisterSessionFixture("test.mutate", (_, _) => { mutations++; return Task.FromResult<object?>(new { saved = true }); });
    var first = f.Call("test.wait"); await started.Task; var queued = f.Call("test.mutate");
    var cancelled = await f.Call("operations.cancel").WaitAsync(TimeSpan.FromSeconds(2)); Check(cancelled.GetProperty("count").GetInt32() == 2); await cleanup.Task;
    var finalSave = f.Call("test.mutate"); Check(!finalSave.IsCompleted); release.TrySetResult(); await Cancelled(first); await Cancelled(queued); await finalSave;
    Check(mutations == 1);
});
Add("A full bridge reserves cancellation and cancels accepted workers before queued mutations can start", async f =>
{
    var started = Signal(); var mutations = 0; var replies = new ConcurrentQueue<JsonElement>();
    f.App.RegisterSessionFixture("test.wait", async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; });
    f.App.RegisterSessionFixture("test.mutate", (_, _) => { mutations++; return Task.FromResult<object?>(new { saved = true }); });
    await using var bridge = new WebBridge(f.App.Operations, f.App.ExecuteAsync, json => { using var document = JsonDocument.Parse(json); replies.Enqueue(document.RootElement.Clone()); });
    void Send(string id, string operation) => bridge.Receive(JsonSerializer.Serialize(new { id, op = operation, args = new { } }));
    Send("active", "test.wait"); await started.Task;
    for (var index = 1; index < WebBridge.MaximumPending; index++) Send("queued_" + index, "test.mutate");
    Send("overflow", "test.mutate"); Send("control", "operations.cancel");
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (replies.Count < WebBridge.MaximumPending + 2) await Task.Delay(10, limit.Token);
    var response = replies.ToArray();
    Check(mutations == 0 && response.Single(r => r.GetProperty("id").GetString() == "control").GetProperty("ok").GetBoolean());
    Check(response.Count(r => r.TryGetProperty("error", out var error) && error.GetProperty("code").GetString() == "CANCELLED") == WebBridge.MaximumPending);
    Check(response.Single(r => r.GetProperty("id").GetString() == "overflow").GetProperty("error").GetProperty("code").GetString() == "INVALID_REQUEST");
});
Add("Shutdown waits for cleanup and shares one drain across simultaneous disposal calls", async f =>
{
    var started = Signal(); var cleanup = Signal(); var release = Signal(); var written = Path.Combine(f.Root, "cleanup.txt");
    f.App.RegisterSessionFixture("test.wait", async (_, ct) => { started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); return null; } finally { cleanup.TrySetResult(); await release.Task; await File.WriteAllTextAsync(written, "Observation conservée"); } });
    var operation = f.Call("test.wait"); await started.Task; var first = f.App.DisposeAsync().AsTask(); var second = f.App.DisposeAsync().AsTask(); await cleanup.Task;
    Check(!first.IsCompleted && !second.IsCompleted);
    try { await f.Call("settings.get"); throw new Exception("Disposed application admitted new work."); } catch (ObjectDisposedException) { }
    release.TrySetResult(); await Cancelled(operation); await Task.WhenAll(first, second);
    Check(await File.ReadAllTextAsync(written) == "Observation conservée" && f.App.Settings.PortalUrl == SessionFixture.SourceA);
});
Add("Queued operations keep their argument document alive until execution", async f =>
{
    var started = Signal(); var release = Signal();
    f.App.RegisterSessionFixture("test.wait", async (_, _) => { started.TrySetResult(); await release.Task; return null; });
    f.App.RegisterSessionFixture("test.echo", (a, _) => Task.FromResult<object?>(a.GetProperty("value").GetString()));
    var first = f.Call("test.wait"); await started.Task; Task<object?> second;
    using (var document = JsonDocument.Parse("{\"value\":\"retained\"}")) second = f.App.ExecuteAsync("test.echo", document.RootElement);
    release.TrySetResult(); await first; Check((string?)await second == "retained");
});
Add("A periodic tick never queues behind foreground work", async f =>
{
    var started = Signal(); var release = Signal();
    f.App.RegisterSessionFixture("test.wait", async (_, _) => { started.TrySetResult(); await release.Task; return null; });
    var first = f.Call("test.wait"); await started.Task; await f.App.TickAsync().WaitAsync(TimeSpan.FromSeconds(1)); release.TrySetResult(); await first;
    Check(!f.Events.Any(e => e.Name == "workplace.reminders.due"));
});
Add("A due scheduled collection is cancellable by the same immediate control operation", async f =>
{
    var started = Signal(); var finished = false;
    f.App.ForceScheduledFixture(async (_, ct) => { started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); return null; } finally { finished = true; } });
    var tick = f.App.TickAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); await f.Call("operations.cancel"); await Cancelled(tick);
    Check(finished && !f.App.IsBusy);
});
Add("Busy events finish one operation before announcing the next", async f =>
{
    var release = Signal(); var started = Signal();
    f.App.RegisterSessionFixture("test.first", async (_, _) => { started.TrySetResult(); await release.Task; return null; });
    f.App.RegisterSessionFixture("test.second", (_, _) => Task.FromResult<object?>(null));
    var first = f.Call("test.first"); await started.Task; var second = f.Call("test.second"); release.TrySetResult(); await Task.WhenAll(first, second);
    var events = f.Events.Where(e => e.Name == "operation.state").Select(e => JsonSerializer.SerializeToElement(e.Data, PilotApplication.Json)).Where(e => e.GetProperty("operation").GetString()!.StartsWith("test.", StringComparison.Ordinal)).ToArray();
    Check(events.Length == 4 && !events[1].GetProperty("busy").GetBoolean() && events[1].GetProperty("operation").GetString() == "test.first" && events[2].GetProperty("busy").GetBoolean());
});

tests.AddRange(OfflineImportChecks.Create());
tests.AddRange(SimulationChecks.Create());
tests.AddRange(LibraryRuntimeChecks.Create());
tests.AddRange(LibraryCaptureRuntimeChecks.Create());
tests.AddRange(SharedOperationsRuntimeChecks.Create());
tests.AddRange(SharedConflictRuntimeChecks.Create());
var chromium = Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM");
if (!string.IsNullOrWhiteSpace(chromium)) tests.AddRange(await SessionBrowserChecks.Create(chromium));
if (!string.IsNullOrWhiteSpace(chromium)) tests.AddRange(await LibraryCaptureRuntimeChecks.CreateBrowser(chromium));
else Console.WriteLine("SKIP Chromium checks: set GLPI_TEST_CHROMIUM to a local test browser.");
if (args.Contains("--simulation-only", StringComparer.Ordinal)) tests = tests.Where(test => test.Name.StartsWith("Simulation:", StringComparison.Ordinal)).ToList();
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(45)); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + error); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} Runtime session isolation and shutdown checks passed.");
await SessionBrowserChecks.DisposeAsync();
await LibraryCaptureRuntimeChecks.DisposeAsync();
return failures == 0 ? 0 : 1;

static void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static async Task Refuse(Func<Task> call, string text)
{
    try { await call(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { Check(error.Message.Contains(text, StringComparison.OrdinalIgnoreCase), "Unexpected refusal: " + error.Message); return; }
    throw new Exception("Expected refusal: " + text);
}
static async Task Cancelled(Task task)
{
    try { await task; } catch (OperationCanceledException) { return; }
    throw new Exception("Expected cancellation.");
}
