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
            var opened = await Call(reopened, "workplace.ticket.open", new { rowIndex = 0, revision = reopened.Revision });
            Check(!opened.GetProperty("loaded").GetBoolean() && !opened.GetProperty("canRead").GetBoolean()
                && opened.GetProperty("personal").GetProperty("id").GetString() == "42"
                && !opened.GetProperty("cache").GetProperty("autoRead").GetBoolean());
            await Refuse(() => Call(reopened, "workplace.ticket.read", new { ticketKey = opened.GetProperty("personal").GetProperty("key").GetString() }));
            await Refuse(() => Call(reopened, "library.capture"));
        });
        Add("restored GLPI rows reuse partial library content without changing any stored observation or calling the browser", async f =>
        {
            var data = SessionFixture.Data(); data.Headers[0] = "Numéro conservé";
            f.App.Settings.Dashboard.ColumnMappings["id"] = "Numéro conservé";
            await f.App.RememberSourceCacheFixtureAsync(data);
            var reference = new TicketReference("42", "http://session-glpi.test/glpi/front/ticket.form.php?id=42");
            var readAt = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
            var store = new TicketLibraryStore(new LibraryPreferencesStore().Load().DatabasePath);
            var seed = new TicketLibrarySeed { Ticket = reference, Title = "Fiche déjà recueillie", LastModifiedRaw = "Date non confirmée", LastModifiedReliable = false };
            var detail = new TicketDetailResult
            {
                Ticket = reference, Title = seed.Title, ReadAt = readAt,
                Sections = [new() { Name = "Description", Kind = "text", Data = new() { Headers = ["Contenu"], Rows = [["Texte intégral conservé <b>littéral</b>"]], Complete = true, ExtractedAt = readAt } },
                    new() { Name = "Suivis", Kind = "timeline", Data = new() { Headers = ["Type", "Date", "Auteur", "Texte"], Rows = [["Suivi", "18/11/2026 12:00", "Technicien fictif", "Diagnostic et résolution conservés"]], Complete = false, ExtractedAt = readAt } }],
                Tabs = [new() { Key = "unread", Name = "Documents", Loaded = false }]
            };
            await store.SaveDetailAsync(seed, detail, new() { Complete = false, TabsDiscovered = 1, TabsLoaded = 0, Limitations = ["Date de modification à confirmer"] }, "existing-reader");
            var before = JsonSerializer.Serialize(await store.GetAsync(reference), PilotApplication.Json);
            var bytesBefore = await File.ReadAllBytesAsync(store.DatabasePath);
            var settings = f.App.Settings.Copy(); settings.PortalUrl = "http://another-portal.test/other/front/ticket.php";
            settings.TicketDetails.IdColumn = "Autre ID"; settings.TicketDetails.UrlTemplate = "http://another-portal.test/other/front/ticket.form.php?id={id}";
            await using var reopened = new PilotApplication(f.Host, (_, _) => { }, settings);
            var bootstrap = await Call(reopened, "app.bootstrap");
            Check(bootstrap.GetProperty("session").GetProperty("capabilities").GetProperty("ticketDetails").GetBoolean());
            var opened = await Call(reopened, "workplace.ticket.open", new { rowIndex = 0, revision = reopened.Revision });
            var key = opened.GetProperty("personal").GetProperty("key").GetString();
            Check(opened.GetProperty("loaded").GetBoolean() && !opened.GetProperty("canRead").GetBoolean()
                && opened.GetProperty("readAt").GetDateTimeOffset() == readAt && opened.GetProperty("sections").GetArrayLength() == 2
                && !opened.GetProperty("cache").GetProperty("autoRead").GetBoolean()
                && opened.GetProperty("cache").GetProperty("state").GetString() != "current"
                && opened.GetProperty("personal").GetProperty("id").GetString() == "42");
            var section = await Call(reopened, "workplace.ticket.section", new { ticketKey = key, sectionIndex = 1 });
            Check(section.GetProperty("rows")[0].GetProperty("values")[3].GetString() == "Diagnostic et résolution conservés"
                && !section.GetProperty("complete").GetBoolean());
            var cell = await Call(reopened, "workplace.ticket.cell", new { ticketKey = key, sectionIndex = 0, rowIndex = 0, columnIndex = 0, readAt });
            Check(cell.GetProperty("text").GetString() == "Texte intégral conservé <b>littéral</b>");
            Check((await Call(reopened, "workplace.ticket.search", new { ticketKey = key, search = "résolution" })).GetArrayLength() == 1);
            await Call(reopened, "workplace.ticket.position", new { ticketKey = key, position = new { sectionKey = "Description", rowIndex = 0 } });
            Check((await Call(reopened, "workplace.ticket.handover", new { ticketKey = key })).GetProperty("text").GetString()!.Contains("Diagnostic et résolution conservés", StringComparison.Ordinal));
            var invoked = false;
            foreach (var operation in new[] { "workplace.ticket.read", "workplace.ticket.tab", "workplace.ticket.browser", "workplace.actions.inspect", "workplace.relation.open", "workplace.document.download", "library.open", "library.capture" })
            {
                reopened.ReplaceSourceCacheFixture(operation, (_, _) => { invoked = true; return Task.FromResult<object?>(null); });
                await Refuse(() => Call(reopened, operation, new { ticketKey = key, automatic = true }));
            }
            var after = JsonSerializer.Serialize(await store.GetAsync(reference), PilotApplication.Json);
            var bytesAfter = await File.ReadAllBytesAsync(store.DatabasePath);
            Check(!invoked && settings.PortalUrl == reopened.Settings.PortalUrl && before == after && bytesBefore.SequenceEqual(bytesAfter));
            var list = await Call(reopened, "records.list", new { revision = reopened.Revision });
            Check(list.GetProperty("rows")[0].GetProperty("cache").GetProperty("hasContent").GetBoolean()
                && !list.GetProperty("rows")[0].GetProperty("cache").GetProperty("autoRead").GetBoolean());
            await Refuse(() => Call(reopened, "workplace.ticket.open", new { rowIndex = 0, revision = reopened.Revision - 1 }));
        });
        Add("same-number tickets from another host or GLPI path are never borrowed by a restored list", async f =>
        {
            await f.App.RememberSourceCacheFixtureAsync(SessionFixture.Data());
            var store = new TicketLibraryStore(new LibraryPreferencesStore().Load().DatabasePath);
            foreach (var url in new[] { "http://other-session-glpi.test/glpi/front/ticket.form.php?id=42", "http://session-glpi.test/another-glpi/front/ticket.form.php?id=42" })
            {
                var reference = new TicketReference("42", url);
                await store.SaveDetailAsync(new() { Ticket = reference, Title = "Autre installation" }, new() { Ticket = reference, Title = "Contenu étranger", ReadAt = DateTimeOffset.UtcNow,
                    Sections = [new() { Name = "Description", Kind = "text", Data = new() { Headers = ["Contenu"], Rows = [["Ne pas mélanger"]], Complete = true } }] }, new() { Complete = true }, "existing-reader");
            }
            await using var reopened = Reopen(f); await Call(reopened, "app.bootstrap");
            var opened = await Call(reopened, "workplace.ticket.open", new { rowIndex = 0, revision = reopened.Revision });
            Check(!opened.GetProperty("loaded").GetBoolean() && !opened.GetProperty("canRead").GetBoolean()
                && !opened.GetProperty("cache").GetProperty("hasContent").GetBoolean()
                && opened.GetProperty("readReason").GetString()!.Contains("Aucun contenu détaillé", StringComparison.Ordinal));
            Check((await store.GetStateAsync()).TicketCount == 2);
        });
        Add("offline collection audit selects the cached installation and its recorded context after portal and date preferences change", async f =>
        {
            var preferences = new LibraryPreferences { SourceTimeZone = "UTC", SourceDateOrder = "dmy" };
            new LibraryPreferencesStore().Save(preferences);
            var store = new TicketLibraryStore(preferences.DatabasePath);
            var contextA = TicketLibraryCaptureContext.FromSettings(f.App.Settings, preferences);
            var runA = await SaveCollectionForCache(store, contextA, "Passage conservé A");
            var anotherPath = f.App.Settings.Copy(); anotherPath.PortalUrl = "http://session-glpi.test/another-glpi/front/ticket.php";
            var contextPath = TicketLibraryCaptureContext.FromSettings(anotherPath, preferences);
            var runPath = await SaveCollectionForCache(store, contextPath, "Passage autre installation");
            var anotherHost = f.App.Settings.Copy(); anotherHost.PortalUrl = "http://another-session-glpi.test/glpi/front/ticket.php";
            var runHost = await SaveCollectionForCache(store, TicketLibraryCaptureContext.FromSettings(anotherHost, preferences), "Passage autre serveur");
            await f.App.RememberSourceCacheFixtureAsync(SessionFixture.Data());
            preferences.SourceTimeZone = "Europe/Paris"; preferences.SourceDateOrder = "mdy";
            new LibraryPreferencesStore().Save(preferences);
            var bytesBefore = await File.ReadAllBytesAsync(store.DatabasePath); var cacheBefore = CacheBytes(f);
            var settingsBefore = JsonSerializer.Serialize(anotherHost, PilotApplication.Json);
            await using var reopened = new PilotApplication(f.Host, (_, _) => { }, anotherHost);
            await Call(reopened, "app.bootstrap");
            var model = await Call(reopened, "library.collection.get");
            Check(model.GetProperty("offlineSavedRun").GetBoolean() && model.GetProperty("installation").GetString() == contextA.Installation
                && model.GetProperty("state").GetProperty("runId").GetString() == runA
                && model.GetProperty("report").GetProperty("runId").GetString() == runA
                && model.GetProperty("availability").GetProperty("stored").GetInt32() == 1
                && model.GetProperty("coverage").ValueKind == JsonValueKind.Null
                && model.GetProperty("message").GetString()!.Contains("Dernier passage conservé", StringComparison.Ordinal));
            Check(model.GetProperty("message").GetString()!.Contains(contextA.Installation, StringComparison.Ordinal));
            var scope = await Call(reopened, "library.collection.scope", new { column = "Statut" });
            Check(scope.GetProperty("installation").GetString() == contextA.Installation
                && scope.GetProperty("report").GetProperty("runId").GetString() == runA
                && scope.GetProperty("catalog").ValueKind == JsonValueKind.Null);
            await Refuse(() => Call(reopened, "library.collection.preview", new { scope = new { enabled = true } }));
            await Refuse(() => Call(reopened, "library.collection.settings", new { revision = 1, options = new LibraryCollectionOptions { Automatic = true }, confirmedCollector = true }));
            var saved = await store.ReadSavedCollectionAsync(contextA.Installation);
            Check(saved?.Context == contextA && saved.Report.RunId == runA);
            var events = await Call(reopened, "library.collection.events");
            Check(events.GetProperty("runId").GetString() == runA && events.GetProperty("items").GetArrayLength() == 1
                && events.GetProperty("items")[0].GetProperty("message").GetString() == "Passage conservé A");
            Check((await Call(reopened, "library.collection.events", new { runId = runA })).GetProperty("runId").GetString() == runA);
            Check((await Call(reopened, "library.capture.latest")).GetProperty("runId").GetString() == runA);
            var entries = await Call(reopened, "library.capture.entries", new { runId = runA, retryOnly = true });
            Check(entries.GetProperty("total").GetInt64() == 1 && entries.GetProperty("items")[0].GetProperty("hasContent").GetBoolean()
                && !entries.GetProperty("items")[0].GetProperty("detailsCurrent").GetBoolean());
            foreach (var foreignRun in new[] { runPath, runHost })
            {
                await Refuse(() => Call(reopened, "library.collection.events", new { runId = foreignRun }));
                await Refuse(() => Call(reopened, "library.capture.entries", new { runId = foreignRun }));
            }
            Check((await Call(reopened, "library.collection.status")).GetProperty("state").GetString() == "idle");
            var bytesAfter = await File.ReadAllBytesAsync(store.DatabasePath);
            Check(bytesBefore.SequenceEqual(bytesAfter) && cacheBefore.SequenceEqual(CacheBytes(f))
                && settingsBefore == JsonSerializer.Serialize(reopened.Settings, PilotApplication.Json));
        });
        Add("offline collection audit never substitutes another installation or a CSV source and does not create a missing library", async f =>
        {
            await f.App.RememberSourceCacheFixtureAsync(SessionFixture.Data());
            var path = new LibraryPreferencesStore().Load().DatabasePath;
            await using var reopened = Reopen(f); await Call(reopened, "app.bootstrap");
            var absent = await Call(reopened, "library.collection.get");
            Check(absent.GetProperty("report").ValueKind == JsonValueKind.Null && absent.GetProperty("availability").ValueKind == JsonValueKind.Null
                && absent.GetProperty("state").GetProperty("runId").GetString() == "" && !File.Exists(path));
            Check((await Call(reopened, "library.capture.latest")).ValueKind == JsonValueKind.Null);
            await Refuse(() => Call(reopened, "library.capture.entries", new { runId = Guid.NewGuid().ToString("N") }));
            Check((await Call(reopened, "library.collection.scope", new { column = "Statut" })).GetProperty("report").ValueKind == JsonValueKind.Null);
            await Refuse(() => Call(reopened, "library.collection.preview", new { scope = new { enabled = true } }));
            await Refuse(() => Call(reopened, "library.collection.settings", new { revision = 0, options = new LibraryCollectionOptions() }));
            Check(!File.Exists(path));
            var other = f.App.Settings.Copy(); other.PortalUrl = "http://another-session-glpi.test/glpi/front/ticket.php";
            var store = new TicketLibraryStore(path); var foreignRun = await SaveCollectionForCache(store,
                TicketLibraryCaptureContext.FromSettings(other, new LibraryPreferences()), "Autre bilan conservé");
            var bytesBefore = await File.ReadAllBytesAsync(path);
            var model = await Call(reopened, "library.collection.get");
            Check(model.GetProperty("report").ValueKind == JsonValueKind.Null && model.GetProperty("availability").ValueKind == JsonValueKind.Null
                && model.GetProperty("message").GetString()!.Contains("Aucun passage conservé", StringComparison.Ordinal));
            Check((await Call(reopened, "library.collection.events")).GetProperty("items").GetArrayLength() == 0);
            await Refuse(() => Call(reopened, "library.collection.events", new { runId = foreignRun }));
            var bytesAfter = await File.ReadAllBytesAsync(path); Check(bytesBefore.SequenceEqual(bytesAfter));
            await OfflineImportChecks.WriteCsv(f); await f.Call("records.import");
            await Call(reopened, "cache.restore");
            model = await Call(reopened, "library.collection.get");
            Check(reopened.IsFileSource && model.GetProperty("installation").GetString() == ""
                && model.GetProperty("report").ValueKind == JsonValueKind.Null
                && (await Call(reopened, "library.collection.events")).GetProperty("items").GetArrayLength() == 0);
            Check((await Call(reopened, "library.capture.latest")).ValueKind == JsonValueKind.Null);
            await Refuse(() => Call(reopened, "library.capture.entries", new { runId = foreignRun }));
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
    private static async Task<string> SaveCollectionForCache(TicketLibraryStore store, TicketLibraryCaptureContext context, string message)
    {
        var runId = await store.BeginCaptureAsync(context, "incremental");
        var reference = new TicketReference("42", context.Installation + "/front/ticket.form.php?id=42");
        var seed = new TicketLibrarySeed { Ticket = reference, Title = message, LastModifiedRaw = "Date conservée à confirmer" };
        await store.SetCaptureIndexAsync(runId, new() { SourceUrl = context.Installation + "/front/ticket.php", AllStatuses = true,
            Complete = true, ScopeFingerprint = "saved-cache-fixture", Tickets = [seed] });
        await store.SaveCaptureDetailAsync(runId, seed, new() { ReaderSignature = context.ReaderSignature, Coverage = new() { Complete = false },
            Detail = new() { Ticket = reference, Title = message, ReadAt = DateTimeOffset.UtcNow, Sections = [new() { Name = "Description", Kind = "text",
                Data = new() { Headers = ["Contenu"], Rows = [[message]], Complete = true } }] } }, 0);
        await store.AppendCollectionEventAsync(runId, context, new() { Stage = "Passage conservé", Code = "saved-cache", Message = message });
        await store.FinishCaptureAsync(runId, "completed", ["Version non confirmée"]);
        await store.SaveCollectionStateAsync(context.Fingerprint, new() { RunId = runId, State = "attention" }, 0);
        return runId;
    }
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
