using System.Text.Json;
using GlpiPilot.Core;

var root = Path.Combine(Path.GetTempPath(), "GlpiPilot-Completion-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var total = 0; var failed = 0;
string FileName() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
void Check(string name, Action test) { total++; try { test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + e); } }
async Task CheckAsync(string name, Func<Task> test) { total++; try { await test().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + e); } }
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
const string target = "https://glpi.test/instance-a/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=Paris";
AppSettings Settings() => new() { PortalUrl = target, ExportFolder = root, MaxRows = 100, MaxPages = 20, Dashboard = new() { PersonalTechnician = "Personnel local" } };
ExtractionResult Data(int day = 0, params string[] ids) => new()
{
    SourceUrl = target, Headers = new() { "ID", "Titre", "Statut", "Date de création", "Attribué à - Technicien" },
    Rows = ids.Select(id => new[] { id, "Objet " + id, "Nouveau", "01/10/2026", "Alex" }).ToList(), Complete = true, ExpectedTotal = ids.Length,
    ExtractedAt = new DateTimeOffset(2026, 10, 10 + day, 10, 0, 0, TimeSpan.Zero),
    Provenance = new() { RequestedUrl = target, SourceKind = "browser", Method = "pagination", ScopeVerified = true, SearchVerified = true, ScopeFingerprint = "search-a" }
};

Check("Source profile: acquisition, folder and interpretation isolated", () =>
{
    var a = Settings(); a.TableSelector = "#a"; a.NextSelector = "a.next"; a.Dashboard.NumberCulture = "en-US"; a.DetailTimeoutSeconds = 63;
    var profile = new SourceProfile(); profile.Capture(a); a.TableSelector = "#changed"; a.Dashboard.ColumnMappings["title"] = "Other";
    var b = Settings(); b.ExportFolder = "elsewhere"; profile.ApplyTo(b); Equal("#a", b.TableSelector); Equal("en-US", b.Dashboard.NumberCulture); Equal(63, b.DetailTimeoutSeconds); Equal(root, b.ExportFolder); Assert(!b.Dashboard.ColumnMappings.ContainsKey("title"));
});
Check("Source profile: workspace opt-in preserves personal identity", () =>
{
    var a = Settings(); a.Dashboard.PersonalTechnician = "Imported identity"; a.Dashboard.Layout.Boards.Add(DashboardLayoutSettings.CreateBoard("Source board", "empty")); a.Queries.Queries.Clear();
    var p = new SourceProfile { RestoreWorkspace = true }; p.Capture(a); var b = Settings(); b.Normalize(); p.ApplyTo(b); Equal("Personnel local", b.Dashboard.PersonalTechnician); Assert(b.Dashboard.Layout.Boards.Any(board => board.Name == "Source board")); Equal(0, b.Queries.Queries.Count);
});
Check("Source profile: plain switching preserves existing dashboard layout", () =>
{
    var a = Settings(); var p = new SourceProfile(); p.Capture(a); var b = Settings(); b.Normalize(); b.Dashboard.Layout.Boards[0].Name = "My board"; p.ApplyTo(b); Equal("My board", b.Dashboard.Layout.Boards[0].Name);
});
Check("Identity: installation and object type cannot collide", () =>
{
    Equal(ObjectIdentity.Key(target, "Ticket", "00042"), ObjectIdentity.Key(target, "ticket", "42"));
    Assert(ObjectIdentity.Key(target, "Ticket", "42") != ObjectIdentity.Key(target, "Computer", "42"));
    Assert(ObjectIdentity.Key(target, "Ticket", "42") != ObjectIdentity.Key(target.Replace("instance-a", "instance-b"), "Ticket", "42"));
    Equal(ObjectIdentity.InstallationKey(target), ObjectIdentity.InstallationKey("https://glpi.test/instance-a/ajax/common.tabs.php"));
});
Check("Identity: invalid object types, zero and credential URLs rejected", () =>
{ Reject<InvalidDataException>(() => ObjectIdentity.Key(target, "../ticket", "1")); Reject<InvalidDataException>(() => ObjectIdentity.Key(target, "Ticket", "0")); Reject<InvalidDataException>(() => ObjectIdentity.Key("https://user:pass@glpi.test/front/ticket.php", "Ticket", "1")); });
Check("Browser profiles: separate portal paths and browsers", () =>
{ Assert(AppPaths.Profile("msedge", target) != AppPaths.Profile("msedge", target.Replace("instance-a", "instance-b"))); Assert(AppPaths.Profile("msedge", target) != AppPaths.Profile("chrome", target)); });
Check("Schema: explicit rename proposals never apply themselves", () =>
{ var diff = SourceSchemaStore.Compare(new[] { "ID", "Date création", "Catégorie" }, new[] { "ID", "Date création ticket", "Categorie" }); Equal(2, diff.Added.Length); Equal(2, diff.Removed.Length); Assert(diff.ProposedRenames.Any(r => r.Previous == "Catégorie" && r.Current == "Categorie")); });
Check("Schema: acceptance establishes a new per-source baseline", () =>
{ var store = new SourceSchemaStore(Path.Combine(root, "schemas")); Assert(!store.Inspect("a", new[] { "ID" }).Changed); Assert(store.Inspect("a", new[] { "ID", "Titre" }).Changed); Assert(!store.Inspect("b", new[] { "ID", "Titre" }).Changed); store.Accept("a", new[] { "ID", "Titre" }); Assert(!store.Inspect("a", new[] { "ID", "Titre" }).Changed); });
Check("Basket: survives restart and filter order with source identities", () =>
{ var path = FileName(); var basket = new SelectionBasket(path); var data = Data(0, "1", "2", "3"); Equal(2, basket.Add(data, new[] { 0, 2 }, Settings().Dashboard)); var reloaded = new SelectionBasket(path); data.Rows.Reverse(); Assert(reloaded.Find(data, Settings().Dashboard).SequenceEqual(new[] { 0, 2 })); });
Check("Basket: duplicate identifiers, other portals and object types excluded", () =>
{ var basket = new SelectionBasket(FileName()); var data = Data(0, "1"); basket.Add(data, new[] { 0 }, Settings().Dashboard); Equal(0, basket.Find(Data(0, "1", "01"), Settings().Dashboard).Length); data.Provenance.RequestedUrl = target.Replace("ticket.php", "computer.php"); Equal(0, basket.Find(data, Settings().Dashboard).Length); data.Provenance.RequestedUrl = target.Replace("instance-a", "instance-b"); Equal(0, basket.Find(data, Settings().Dashboard).Length); });
Check("Basket: malformed and local identifiers cannot become remote targets", () =>
{ var basket = new SelectionBasket(FileName()); var data = Data(0, "bad", "0", "1"); data.SourceUrl = ""; data.Provenance.RequestedUrl = ""; Equal(0, basket.Add(data, new[] { 0, 1, 2 }, Settings().Dashboard)); });
Check("Native sample: matching IDs with changed cells fails verification", () =>
{ var visible = new TableSnapshot { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "0001", "Original" } } }; var data = new ExtractionResult { Headers = new() { "ID", "Titre" }, Rows = new() { new[] { "1", "Changed" } } }; var check = ExtractionVerificationService.VerifyNativeSample(visible, data, new()); Assert(check.Available && !check.Matches && check.Missing == 0); });
Check("Native sample: whitespace equivalent and truncated cells respected", () =>
{ var visible = new TableSnapshot { Headers = new() { "ID", "Titre", "Description" }, Rows = new() { new[] { "1", "A\nB", "Long…" } } }; var data = new ExtractionResult { Headers = new() { "ID", "Titre", "Description" }, Rows = new() { new[] { "1", "A B", "Long description" } } }; Assert(ExtractionVerificationService.VerifyNativeSample(visible, data, new()).Matches); });

PaginationCheckpoint Checkpoint()
{
    var data = Data(0, "1"); data.Pages = 1; data.ExpectedTotal = 2;
    return new() { Target = target, LastUrl = target + "&start=0", ScopeFingerprint = "scope", FirstSignature = "first", LastSignature = "first", Signatures = new() { "first" }, Keys = new() { ["ticket:1"] = "hash" }, Data = data };
}
Check("Checkpoint: bounded exact state round-trips and is deleted after completion", () =>
{ var store = new PaginationCheckpointStore(Path.Combine(root, "checkpoints1")); store.Save(Checkpoint(), CancellationToken.None); var loaded = store.Load(Settings()); Assert(loaded != null); Equal("1", loaded!.Data.Rows[0][0]); store.Delete(target); Assert(store.Load(Settings()) == null); });
Check("Checkpoint: changed target, cross-origin page and excess limits rejected", () =>
{ var store = new PaginationCheckpointStore(Path.Combine(root, "checkpoints2")); var checkpoint = Checkpoint(); checkpoint.LastUrl = "https://other.test/front/ticket.php"; store.Save(checkpoint, CancellationToken.None); Assert(store.Load(Settings()) == null); checkpoint = Checkpoint(); store.Save(checkpoint, CancellationToken.None); var settings = Settings(); settings.PortalUrl += "&other=1"; Assert(store.Load(settings) == null); settings = Settings(); settings.MaxPages = 1; Assert(store.Load(settings) == null); });
Check("Checkpoint: corrupted shape and duplicate page signatures rejected", () =>
{ var store = new PaginationCheckpointStore(Path.Combine(root, "checkpoints3")); var checkpoint = Checkpoint(); checkpoint.Data.Rows[0] = new[] { "bad width" }; store.Save(checkpoint, CancellationToken.None); Assert(store.Load(Settings()) == null); checkpoint = Checkpoint(); checkpoint.Data.Pages = 2; checkpoint.Signatures.Add("first"); store.Save(checkpoint, CancellationToken.None); Assert(store.Load(Settings()) == null); });
Check("Checkpoint: cancellation never replaces a valid saved point", () =>
{ var store = new PaginationCheckpointStore(Path.Combine(root, "checkpoints4")); store.Save(Checkpoint(), CancellationToken.None); using var cts = new CancellationTokenSource(); cts.Cancel(); Reject<OperationCanceledException>(() => store.Save(Checkpoint(), cts.Token)); Assert(store.Load(Settings()) != null); });
Check("Fixed schedule: multiple times and weekdays use the chosen local zone", () =>
{ var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"); var settings = new AutomationSettings { FixedTimes = "08:30,13:30", WeekDays = "1,2,3,4,5" }; var next = AutomationPolicy.Next(settings, new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero), zone); Equal(new DateTimeOffset(2026, 10, 26, 7, 30, 0, TimeSpan.Zero), next); });
Check("Fixed schedule: nonexistent spring DST time is skipped", () =>
{ var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"); var settings = new AutomationSettings { FixedTimes = "02:30,04:00", WeekDays = "7" }; Equal(new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero), AutomationPolicy.Next(settings, new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero), zone)); });
Check("Fixed schedule: malformed times and empty weekdays refused", () =>
{ Reject<InvalidDataException>(() => AutomationPolicy.Times("8h30")); Reject<InvalidDataException>(() => AutomationPolicy.Days("")); Reject<InvalidDataException>(() => AutomationPolicy.Days("8")); Equal(2, AutomationPolicy.Times("08:00;08:00;12:00").Count); });
Check("Quiet hours: midnight crossing and full-day quiet are explicit", () =>
{ var settings = new AutomationSettings { QuietHours = true, QuietStartHour = 20, QuietEndHour = 8 }; Assert(AutomationPolicy.IsQuiet(settings, new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc)); Assert(!AutomationPolicy.IsQuiet(settings, new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc)); settings.QuietStartHour = settings.QuietEndHour; Assert(AutomationPolicy.IsQuiet(settings, DateTimeOffset.Now)); });
Check("Recipe: one unconditional extraction must precede conditional steps", () =>
{ AutomationPolicy.ValidateRecipe(new[] { new AutomationRecipeStep { Kind = "extract" }, new() { Kind = "archive", Condition = "complete" } }); Reject<InvalidDataException>(() => AutomationPolicy.ValidateRecipe(new[] { new AutomationRecipeStep { Kind = "archive" } })); Reject<InvalidDataException>(() => AutomationPolicy.ValidateRecipe(new[] { new AutomationRecipeStep { Kind = "extract", Condition = "complete" } })); });
Check("Recipe: row, completeness and disabled conditions are evaluated", () =>
{ var data = Data(0, "1"); var step = new AutomationRecipeStep { Kind = "archive", Condition = "complete", MinimumRows = 2 }; Assert(!AutomationPolicy.ShouldRun(step, data)); step.MinimumRows = 1; Assert(AutomationPolicy.ShouldRun(step, data)); data.Complete = false; Assert(!AutomationPolicy.ShouldRun(step, data)); step.Condition = "incomplete"; Assert(AutomationPolicy.ShouldRun(step, data)); step.Enabled = false; Assert(!AutomationPolicy.ShouldRun(step, data)); });
Check("Collection health: missing, repeated failures and recovery survive restart", () =>
{ var path = FileName(); var store = new CollectionHealthStore(path); var source = new SourceProfile { Name = "Source" }; var settings = new AutomationSettings { FailureAlertThreshold = 2 }; Equal(1, store.Alerts(new[] { source }, settings, DateTimeOffset.Now).Count); store.Record(source.Id, source.Name, false, "failure", DateTimeOffset.Now); store.Record(source.Id, source.Name, false, "failure", DateTimeOffset.Now); Assert(store.Alerts(new[] { source }, settings, DateTimeOffset.Now)[0].Contains("2 échecs")); new CollectionHealthStore(path).Record(source.Id, source.Name, true, "", DateTimeOffset.Now); Equal(0, new CollectionHealthStore(path).Alerts(new[] { source }, settings, DateTimeOffset.Now).Count); });
Check("Alerts: deltas require a comparable observed baseline", () =>
{ var service = new AlertService(FileName()); service.SaveRules(new[] { new AlertRule { MetricKey = "active", EvaluationMode = "delta", Threshold = 1 } }); var a = Data(0, "1"); Equal(0, service.Evaluate(a, DashboardService.Calculate(a, Settings().Dashboard), settings: Settings())); var b = Data(1, "1", "2", "3"); Equal(1, service.Evaluate(b, DashboardService.Calculate(b, Settings().Dashboard), settings: Settings())); Equal(2d, service.Document.Incidents.Single().Value); });
Check("Alerts: changed scope and zero percent denominator remain unknown", () =>
{ var service = new AlertService(FileName()); service.SaveRules(new[] { new AlertRule { MetricKey = "active", EvaluationMode = "percent", Threshold = 1, MinimumSample = 0 } }); var a = Data(); service.Evaluate(a, DashboardService.Calculate(a, Settings().Dashboard), settings: Settings()); var b = Data(1, "1"); Equal(0, service.Evaluate(b, DashboardService.Calculate(b, Settings().Dashboard), settings: Settings())); var c = Data(2, "1", "2"); c.Provenance.ScopeFingerprint = "changed"; Equal(0, service.Evaluate(c, DashboardService.Calculate(c, Settings().Dashboard), settings: Settings())); });
Check("Alerts: newly contributing identifiers highlighted between occurrences", () =>
{ var service = new AlertService(FileName()); service.SaveRules(new[] { new AlertRule { MetricKey = "active" } }); var a = Data(0, "1", "2"); service.Evaluate(a, DashboardService.Calculate(a, Settings().Dashboard)); var b = Data(1, "2", "3"); service.Evaluate(b, DashboardService.Calculate(b, Settings().Dashboard)); var incident = service.Document.Incidents.Single(); Equal(1, incident.NewContributorCount); Assert(incident.NewContributorIds.SequenceEqual(new[] { "3" })); Equal(2, incident.Occurrences); });
Check("Alerts: manual resolution has a reason and never reopens the same run", () =>
{ var path = FileName(); var service = new AlertService(path); service.SaveRules(new[] { new AlertRule { MetricKey = "active" } }); var a = Data(0, "1"); service.Evaluate(a, DashboardService.Calculate(a, Settings().Dashboard)); var id = service.Document.Incidents.Single().Id; Reject<InvalidDataException>(() => service.Resolve(id, "")); service.Resolve(id, "Intervention terminée"); service.Evaluate(a, DashboardService.Calculate(a, Settings().Dashboard)); Equal(1, service.Document.Incidents.Count); Assert(!service.Document.Incidents.Single().Active); Assert(new AlertService(path).Document.Incidents.Single().History.Any(h => h.Reason == "Intervention terminée")); var b = Data(1, "1"); Equal(1, service.Evaluate(b, DashboardService.Calculate(b, Settings().Dashboard))); });
Check("Configuration: detailed differences distinguish additions and deletions", () =>
{ var a = Settings(); var b = a.Copy(); b.Dashboard.ValueDictionaries["status"] = new() { ["Done"] = "Clos" }; b.Dashboard.StaleDays = 11; var diff = ConfigurationReview.DescribeFields(a, b); Assert(diff.Any(d => d.Path.EndsWith("StaleDays") && d.Change == "Modification")); Assert(diff.Any(d => d.Path.Contains("ValueDictionaries") && d.Change == "Ajout")); Assert(ConfigurationReview.DescribeFields(b, a).Any(d => d.Change == "Suppression")); });
Check("Configuration: conflict choices current/incoming/both are deterministic", () =>
{ var a = Settings(); a.Normalize(); var b = a.Copy(); b.Queries.Queries[0].Name = "Changed"; var key = "queries/" + b.Queries.Queries[0].Id; var both = new Dictionary<string, string> { [key] = "both" }; var merged = ConfigurationReview.Apply(a, b, ConfigurationParts.Queries, true, both); Equal(a.Queries.Queries.Count + 1, merged.Queries.Queries.Count); Assert(merged.Queries.Queries.Last().Id != b.Queries.Queries[0].Id); Equal(merged.Queries.Queries.Last().Id, ConfigurationReview.Apply(a, b, ConfigurationParts.Queries, true, both).Queries.Queries.Last().Id); Equal("Changed", ConfigurationReview.Apply(a, b, ConfigurationParts.Queries, true, new Dictionary<string, string> { [key] = "incoming" }).Queries.Queries[0].Name); Equal(a.Queries.Queries[0].Name, ConfigurationReview.Apply(a, b, ConfigurationParts.Queries, true, new Dictionary<string, string>()).Queries.Queries[0].Name); });
Check("Configuration: keep-both query references follow imported dashboard variant", () =>
{ var a = Settings(); a.Normalize(); var b = a.Copy(); b.Queries.Queries[0].Name = "Changed"; var board = b.Dashboard.Layout.Boards[0]; board.Name = "Changed board"; board.Widgets.Clear(); board.Widgets.Add(new() { SourceKey = QueryService.MetricKey(b.Queries.Queries[0].Id) }); var choices = new Dictionary<string, string> { ["queries/" + b.Queries.Queries[0].Id] = "both", ["boards/" + board.Id] = "both" }; var merged = ConfigurationReview.Apply(a, b, ConfigurationParts.Queries | ConfigurationParts.Dashboards, true, choices); Equal(QueryService.MetricKey(merged.Queries.Queries.Last().Id), merged.Dashboard.Layout.Boards.Last().Widgets.Single().SourceKey); });
Check("Configuration: profile export strips nested personal identity and folders", () =>
{ var settings = Settings(); settings.Normalize(); settings.Dashboard.NumberCulture = "en-US"; settings.Dashboard.ValueDictionaries["category"] = new() { ["PC"] = "Poste" }; var profile = new SourceProfile { RestoreWorkspace = true }; profile.Capture(settings); profile.ExportFolder = "PRIVATE-FOLDER"; profile.Interpretation!.PersonalTechnician = "PRIVATE-IDENTITY"; settings.Sources.Add(profile); var path = FileName(); ConfigurationTransferService.Export(path, settings); var text = File.ReadAllText(path); Assert(!text.Contains("PRIVATE-FOLDER") && !text.Contains("PRIVATE-IDENTITY")); var imported = ConfigurationTransferService.Import(path, Settings()); Equal("en-US", imported.Sources.Single().Interpretation!.NumberCulture); Equal("Poste", imported.Sources.Single().Interpretation!.ValueDictionaries["category"]["PC"]); Assert(imported.Sources.Single().WorkspaceQueries!.Queries.Count > 0); });
Check("Campaign report: complete, failed and unstarted sources remain distinct", () =>
{ var rows = new[] { new CampaignSourceResult { Name = "A", State = "Complet", Rows = 4 }, new() { Name = "B", State = "Échec", Detail = "unavailable" }, new() { Name = "C", State = "Non lancée" } }; var path = CampaignReport.Save(root, rows, DateTimeOffset.Now); using var json = JsonDocument.Parse(File.ReadAllText(path)); Equal(3, json.RootElement.GetProperty("Sources").GetArrayLength()); Assert(File.Exists(Path.ChangeExtension(path, ".csv"))); });
await CheckAsync("Scheduler: visible work cancels background and runs before queued hidden work", async () =>
{
    using var scheduler = new BackgroundWorkScheduler(); var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var order = new List<string>();
    var first = scheduler.RunAsync("slow", false, ct => { started.SetResult(true); ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); return 0; }); await started.Task;
    var hidden = scheduler.RunAsync("hidden", false, ct => { order.Add("hidden"); return 1; }); var visible = scheduler.RunAsync("visible", true, ct => { order.Add("visible"); return 2; });
    try { await first; throw new Exception("Background work was not cancelled"); } catch (OperationCanceledException) { }
    Equal(2, await visible); Equal(1, await hidden); Assert(order.SequenceEqual(new[] { "visible", "hidden" }));
});
await CheckAsync("Scheduler: replacement of a category suppresses stale computation", async () =>
{
    using var scheduler = new BackgroundWorkScheduler(); var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = scheduler.RunAsync("same", false, ct => { started.SetResult(true); ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); return 1; }); await started.Task;
    var latest = scheduler.RunAsync("same", false, ct => 2); try { await first; throw new Exception("Stale computation completed"); } catch (OperationCanceledException) { } Equal(2, await latest);
});
Check("Configuration: reordered identities are visible in the detailed preview", () =>
{
    var a = Settings(); a.Normalize(); a.Dashboard.Layout.Boards.Add(DashboardLayoutSettings.CreateBoard("Second", "empty")); var b = a.Copy(); b.Dashboard.Layout.Boards.Reverse();
    Assert(ConfigurationReview.DescribeFields(a, b).Any(d => d.Path == "Dashboard.Layout.Boards.$ordre" && d.Change == "Réorganisation"));
});
Check("Configuration: invalid source booleans and numeric bounds rejected before normalization", () =>
{
    var a = Settings(); var source = new SourceProfile { Name = "Source" }; source.Capture(a); a.Sources.Add(source); var path = FileName(); ConfigurationTransferService.Export(path, a);
    var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
    foreach (var invalid in new[] { ("ResumePagination", "yes"), ("ListTimeoutSeconds", "0"), ("MaxRows", "1000001"), ("ExpectedTotal", "letters") })
    {
        var altered = json.DeepClone(); altered["Settings"]!["Sources"]![0]!["Acquisition"]![invalid.Item1] = invalid.Item2; File.WriteAllText(path, altered.ToJsonString());
        Reject<InvalidDataException>(() => ConfigurationTransferService.Import(path, a));
    }
});
Check("Source profile: personal landing preference stays on the local workstation", () =>
{
    var a = Settings(); a.Dashboard.OpenPersonalAfterExtraction = false; var profile = new SourceProfile(); profile.Capture(a);
    var b = Settings(); b.Dashboard.OpenPersonalAfterExtraction = true; profile.ApplyTo(b); Assert(b.Dashboard.OpenPersonalAfterExtraction);
});
Check("Mapping: only the matching verified search may update its current profile", () =>
{
    var data = Data(0, "42"); var settings = Settings(); var profile = new SourceProfile(); profile.Capture(settings); settings.Sources.Add(profile); settings.ActiveSourceId = profile.Id;
    Equal(SourceMappingTarget.CurrentConfiguration, SourceMappingPolicy.Resolve(data, settings, false));
    settings.PortalUrl = target + "&start=50"; Equal(SourceMappingTarget.CurrentConfiguration, SourceMappingPolicy.Resolve(data, settings, false));
    settings.PortalUrl = target.Replace("Paris", "Lyon"); Equal(SourceMappingTarget.Unavailable, SourceMappingPolicy.Resolve(data, settings, false));
    settings.PortalUrl = target; profile.Url = target.Replace("instance-a", "instance-b"); Equal(SourceMappingTarget.Unavailable, SourceMappingPolicy.Resolve(data, settings, false));
});
Check("Mapping: archives are read-only and an unverified browser scope cannot update a GLPI profile", () =>
{
    var data = Data(0, "42"); var settings = Settings(); Equal(SourceMappingTarget.ArchiveReadOnly, SourceMappingPolicy.Resolve(data, settings, true));
    data.Provenance.ScopeVerified = false; Equal(SourceMappingTarget.Unavailable, SourceMappingPolicy.Resolve(data, settings, false));
    data.Provenance.ScopeVerified = true; data.Provenance.SearchVerified = false; Equal(SourceMappingTarget.Unavailable, SourceMappingPolicy.Resolve(data, settings, false));
    Equal(SourceMappingTarget.CurrentConfiguration, SourceMappingPolicy.Resolve(null, settings, false));
});
Check("Mapping: local imports and demos explicitly target only their current result", () =>
{
    var data = Data(0, "42"); var settings = Settings(); data.Provenance.SourceKind = "file"; Equal(SourceMappingTarget.LocalResult, SourceMappingPolicy.Resolve(data, settings, false));
    data.Provenance.SourceKind = "demo"; Equal(SourceMappingTarget.LocalResult, SourceMappingPolicy.Resolve(data, settings, false));
    Equal(SourceMappingTarget.ArchiveReadOnly, SourceMappingPolicy.Resolve(data, settings, true));
});
Check("Mapping: local rules are isolated by result and cannot mutate active settings or source cells", () =>
{
    var first = Data(0, "42"); first.Provenance.SourceKind = "file"; var second = Data(0, "42"); second.Provenance.SourceKind = "file";
    var current = Settings(); var draft = current.Dashboard.Copy(); draft.ColumnMappings["title"] = "Titre"; draft.NumberCulture = "en-US";
    var store = new LocalResultMappingStore(); store.Set(first, draft); draft.NumberCulture = "fr-FR";
    Equal("en-US", store.Get(first)!.NumberCulture); Assert(store.Get(second) == null); Assert(!current.Dashboard.ColumnMappings.ContainsKey("title")); Equal("Objet 42", first.Rows[0][1]);
    var returned = store.Get(first)!; returned.ColumnMappings.Clear(); Equal("Titre", store.Get(first)!.ColumnMappings["title"]);
    Reject<InvalidDataException>(() => store.Set(Data(0, "42"), current.Dashboard));
});
Check("Schema: repeated columns and reordered columns remain observable", () =>
{
    var repeated = SourceSchemaStore.Compare(new[] { "ID", "Nom" }, new[] { "ID", "Nom", "Nom" }); Assert(repeated.Changed && repeated.Added.SequenceEqual(new[] { "Nom" }));
    var reordered = SourceSchemaStore.Compare(new[] { "ID", "Nom" }, new[] { "Nom", "ID" }); Assert(reordered.Changed && reordered.Reordered && reordered.Added.Length == 0 && reordered.Removed.Length == 0);
});
Check("Identity: mixed asset searches and demonstrations cannot create ambiguous basket targets", () =>
{
    var data = Data(0, "1"); data.SourceUrl = data.Provenance.RequestedUrl = "https://glpi.test/front/allassets.php"; Equal("", DataObjectIdentity.RowKey(data, 0, Settings().Dashboard));
    data.SourceUrl = data.Provenance.RequestedUrl = "https://glpi.test/front/search.php?item_type=Computer"; Assert(DataObjectIdentity.RowKey(data, 0, Settings().Dashboard).Length > 0);
    data.Provenance.SourceKind = "demo"; Equal("", DataObjectIdentity.RowKey(data, 0, Settings().Dashboard));
});
Console.WriteLine($"{total - failed}/{total} Completion checks passed.");
Directory.Delete(root, true); return failed == 0 ? 0 : 1;
