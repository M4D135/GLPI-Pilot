using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;

internal static class TicketCacheRuntimeChecks
{
    private static readonly DateTimeOffset Modified = DateTimeOffset.ParseExact(DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static readonly DateTimeOffset OldRead = DateTimeOffset.UtcNow.AddDays(-10);
    private static string Stamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static TicketReference Reference(string source = SessionFixture.SourceA) => new("42", new Uri(source).GetLeftPart(UriPartial.Path).Replace("/front/ticket.php", "/front/ticket.form.php", StringComparison.Ordinal) + "?id=42");

    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string label, Func<SessionFixture, Task> run) => tests.Add(("Ticket cache: " + label, async () =>
        {
            await using var f = new SessionFixture();
            f.App.Settings.Dashboard.ColumnMappings["updated"] = "Dernière modification";
            f.App.LoadSessionFixture(Source(Stamp(Modified), OldRead.AddMinutes(-1)));
            await run(f);
        }));
        Add("missing content is explicit and consistent between the list and reader", async f =>
        {
            var opened = await Open(f); var cache = Cache(opened);
            State(cache, "missing", false, true);
            Require(!opened.GetProperty("loaded").GetBoolean() && cache.GetProperty("readAt").ValueKind == JsonValueKind.Null, "Missing content acquired a fabricated read date.");
            Require(cache.GetProperty("sourceModifiedRaw").GetString() == Stamp(Modified), "The restored short header and old configured mapping did not resolve the source stamp.");
            await Consistent(f, cache);
        });
        Add("opening the calendar before a ticket leaves an unused library absent without creating its schema", async f =>
        {
            var first = await Open(f);
            await f.Call("calendar.forTicket", new { ticketKey = Key(first) });
            var path = new LibraryPreferencesStore().Load().DatabasePath;
            Require(File.Exists(path), "The calendar did not create its actual shared SQLite file.");
            var before = await File.ReadAllBytesAsync(path);
            var opened = await Open(f); State(Cache(opened), "missing", false, true);
            Require(!opened.GetProperty("loaded").GetBoolean() && opened.GetProperty("canRead").GetBoolean(),
                "An unused library prevented an unread source ticket from opening normally.");
            await Consistent(f, Cache(opened));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE lower(name) GLOB 'ticket_library_*'";
            Require((long)command.ExecuteScalar()! == 0 && (await File.ReadAllBytesAsync(path)).SequenceEqual(before),
                "Opening a ticket or listing badges initialized or changed the shared database.");
        });
        Add("partial or unsupported library schemas remain unknown and untouched in both list and reader", async f =>
        {
            foreach (var (name, sql) in new[]
            {
                ("missing-table", "DROP TABLE ticket_library_outbox"),
                ("missing-column", "ALTER TABLE ticket_library_conflicts RENAME COLUMN ticket_key TO unavailable_key"),
                ("future-version", "UPDATE ticket_library_schema SET version=999"),
                ("invalid-version", "UPDATE ticket_library_schema SET version='unrecognized'"),
                ("missing-version", "DELETE FROM ticket_library_schema")
            })
            {
                var preferences = new LibraryPreferences { LocalFolder = Path.Combine(f.Root, "cache-" + name) };
                new LibraryPreferencesStore().Save(preferences);
                var store = new TicketLibraryStore(preferences.DatabasePath); await store.InitializeAsync();
                using (var connection = new SqliteConnection(store.ConnectionString))
                {
                    connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
                }
                var before = await File.ReadAllBytesAsync(preferences.DatabasePath);
                var opened = await Open(f); State(Cache(opened), "unknown", false, true);
                Require(opened.GetProperty("canRead").GetBoolean(), "An unavailable cache blocked the source ticket: " + name);
                await Consistent(f, Cache(opened));
                Require((await File.ReadAllBytesAsync(preferences.DatabasePath)).SequenceEqual(before), "Read-only cache lookup repaired or rewrote an unsupported schema: " + name);
            }
        });
        Add("an old read of the same reliable source version skips automatic reading without a browser", async f =>
        {
            f.App.AcceptTicketCacheFixture(Detail(OldRead));
            var opened = await Open(f); var cache = Cache(opened); State(cache, "current", true, false);
            Require(cache.GetProperty("readAt").GetDateTimeOffset() == OldRead && opened.GetProperty("readAt").GetDateTimeOffset() == OldRead,
                "A current old cache was displayed with a manufactured recent date.");
            Require(cache.GetProperty("sourceModifiedRaw").GetString() == Stamp(Modified)
                && cache.GetProperty("cachedModifiedRaw").GetString() == Stamp(Modified), "The matching source and saved modification stamps are missing.");
            var same = await f.Call("workplace.ticket.read", new { ticketKey = Key(opened), automatic = true });
            State(Cache(same), "current", true, false);
            Require(same.GetProperty("readAt").GetDateTimeOffset() == OldRead, "An automatic cache hit changed the observation time.");
            await Consistent(f, Cache(same));
        });
        Add("a refreshed source with the same stamp retains the cache and a changed stamp invalidates it immediately", async f =>
        {
            f.App.AcceptTicketCacheFixture(Detail(OldRead));
            var initial = await Open(f); var oldToken = Cache(initial).GetProperty("sourceToken").GetString();
            var sameTicketSearch = Source(Stamp(Modified), DateTimeOffset.UtcNow, SessionFixture.SourceB);
            sameTicketSearch.Provenance.ScopeFingerprint = "another-filtered-search";
            f.App.LoadSessionFixture(sameTicketSearch);
            var equal = await Open(f); State(Cache(equal), "current", true, false);
            Require(Cache(equal).GetProperty("sourceToken").GetString() == oldToken, "A different search or source revision invalidated an unchanged ticket version.");
            var changedAt = Modified.AddHours(1);
            f.App.LoadSessionFixture(Source(Stamp(changedAt), DateTimeOffset.UtcNow, SessionFixture.SourceB));
            var changed = await Open(f); var cache = Cache(changed); State(cache, "stale", true, true);
            Require(cache.GetProperty("sourceModifiedRaw").GetString() == Stamp(changedAt)
                && cache.GetProperty("cachedModifiedRaw").GetString() == Stamp(Modified)
                && cache.GetProperty("readAt").GetDateTimeOffset() == OldRead
                && cache.GetProperty("sourceToken").GetString() != oldToken,
                "A changed source must preserve the actual cached date and produce a distinct scheduling token.");
            Require(changed.GetProperty("loaded").GetBoolean() && changed.GetProperty("sections")[0].GetProperty("readAt").GetDateTimeOffset() == OldRead,
                "A stale cache disappeared or its section date was overwritten.");
            await Consistent(f, cache);
        });
        Add("reading one tab cannot certify the old main body against a changed source version", async f =>
        {
            f.App.AcceptTicketCacheFixture(Detail(OldRead));
            f.App.LoadSessionFixture(Source(Stamp(Modified.AddHours(1)), DateTimeOffset.UtcNow));
            var merged = Detail(DateTimeOffset.UtcNow);
            merged.Sections[0].Data.ExtractedAt = OldRead;
            merged.Sections.Add(new() { Name = "Historique", Kind = "table", Data = new() { Headers = ["Événement"], Rows = [["Onglet relu séparément"]], Complete = true, ExtractedAt = merged.ReadAt } });
            f.App.AcceptTicketCacheFixture(merged, observeSource: false);
            var opened = await Open(f); var cache = Cache(opened); State(cache, "stale", true, true);
            Require(cache.GetProperty("cachedModifiedRaw").GetString() == Stamp(Modified)
                && opened.GetProperty("sections")[0].GetProperty("readAt").GetDateTimeOffset() == OldRead,
                "A tab-only observation replaced the main ticket's source-version proof.");
            await Consistent(f, cache);
        });
        Add("a successful read close to a known modification explains its timing without weakening version guards", f =>
        {
            var modified = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
            var observed = modified.AddMinutes(1).AddSeconds(9); var readAt = modified.AddMinutes(1).AddSeconds(25);
            foreach (var raw in new[] { Stamp(modified), modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) })
            {
                var source = TicketReadCache.Source(Reference(), Source(raw, observed), 0, f.App.Settings);
                var version = TicketReadCache.Observe(source, Detail(readAt))!;
                Require(source.Reliable && source.ModifiedAt == modified && !version.Reliable && version.MainReadComplete,
                    "The synthetic 12:00 / 12:01:09 / 12:01:25 read did not exercise the comparison margin.");
                var entry = new TicketReadCacheEntry { HasContent = true, ReadAt = readAt, Version = version };
                var recent = TicketReadCache.Describe(source, entry, true, now: readAt);
                Require(recent.State == "recent" && !recent.AutoRead && recent.Reason.Contains("trop proche", StringComparison.Ordinal),
                    "A dated modification close to a successful read was reported as missing evidence: " + recent.Reason);
                Require(recent.SourceModifiedAt == modified && recent.CachedModifiedAt == modified && recent.ReadAt == readAt,
                    "Explaining the margin changed the actual observation dates.");
                var boundary = TicketReadCache.Describe(source, entry, true, now: readAt.AddMinutes(5));
                var expired = TicketReadCache.Describe(source, entry, true, now: readAt.AddMinutes(5).AddTicks(1));
                Require(boundary.State == "recent" && !boundary.AutoRead && expired.State == "unknown" && expired.AutoRead
                    && boundary.SourceToken == recent.SourceToken && expired.SourceToken == recent.SourceToken && !version.Reliable,
                    "The explanation changed the five-minute fallback, source token or version proof as the clock advanced.");
                var changedSource = TicketReadCache.Source(Reference(), Source(Stamp(modified.AddMinutes(1)), readAt.AddSeconds(1)), 0, f.App.Settings);
                var changed = TicketReadCache.Describe(changedSource, entry, true, now: readAt.AddSeconds(1));
                Require(changed.State == "stale" && changed.AutoRead && changed.SourceToken != recent.SourceToken,
                    "A close successful read hid a later known change.");
                var partial = Detail(readAt); partial.Sections[0].Data.Complete = false;
                entry.Version = TicketReadCache.Observe(source, partial);
                var incomplete = TicketReadCache.Describe(source, entry, true, now: readAt);
                Require(incomplete.State == "unknown" && incomplete.AutoRead, "An incomplete main read acquired recent success.");
            }
            var laterObservation = TicketReadCache.Source(Reference(), Source(Stamp(modified.AddMinutes(-10)), readAt.AddSeconds(1)), 0, f.App.Settings);
            var laterEntry = new TicketReadCacheEntry { HasContent = true, ReadAt = readAt, Version = TicketReadCache.Observe(laterObservation, Detail(readAt)) };
            var dated = TicketReadCache.Describe(laterObservation, laterEntry, true, now: readAt);
            Require(dated.State == "recent" && !dated.AutoRead && dated.Reason.Contains("date de dernière modification est disponible", StringComparison.Ordinal)
                && !dated.Reason.Contains("trop proche", StringComparison.Ordinal),
                "A source observed after the read was incorrectly described as a close modification: " + dated.Reason);
            return Task.CompletedTask;
        });
        Add("a changed recent modification stamp bypasses the short fallback even before the earlier version was confirmed", async f =>
        {
            var now = DateTimeOffset.UtcNow;
            f.App.LoadSessionFixture(Source(Stamp(now.AddSeconds(-30)), now.AddSeconds(-10)));
            f.App.AcceptTicketCacheFixture(Detail(now));
            var opened = await Open(f); var recent = Cache(opened); State(recent, "recent", true, false);
            Require(recent.GetProperty("reason").GetString()!.Contains("trop proche", StringComparison.Ordinal),
                "The real reader did not explain why its known modification stamp is not yet comparable.");
            var skipped = await f.Call("workplace.ticket.read", new { ticketKey = Key(opened), automatic = true });
            State(Cache(skipped), "recent", true, false);
            Require(skipped.GetProperty("readAt").GetDateTimeOffset() == now, "A close successful read caused an immediate second automatic read.");
            f.App.LoadSessionFixture(Source(Stamp(now.AddSeconds(-5)), DateTimeOffset.UtcNow));
            var changed = Cache(await Open(f));
            Require(changed.GetProperty("autoRead").GetBoolean() && changed.GetProperty("state").GetString() is not ("current" or "recent")
                && changed.GetProperty("sourceToken").GetString() != recent.GetProperty("sourceToken").GetString(),
                "The recent-read fallback hid a newly changed modification marker.");
            await Consistent(f, changed);
        });
        Add("missing or ambiguous modification evidence stays unknown for an old readable cache", async f =>
        {
            f.App.LoadSessionFixture(Source("", OldRead.AddMinutes(-1))); f.App.AcceptTicketCacheFixture(Detail(OldRead));
            var missing = await Open(f); State(Cache(missing), "unknown", true, true);
            Require(Cache(missing).GetProperty("sourceModifiedAt").ValueKind == JsonValueKind.Null, "An absent stamp acquired an invented date.");
            var ambiguous = Source(Stamp(Modified), DateTimeOffset.UtcNow);
            ambiguous.Headers.Add("Updated at"); ambiguous.Rows[0] = [..ambiguous.Rows[0], Stamp(Modified.AddHours(1))];
            f.App.LoadSessionFixture(ambiguous);
            var opened = await Open(f); State(Cache(opened), "unknown", true, true);
            Require(Cache(opened).GetProperty("sourceModifiedAt").ValueKind == JsonValueKind.Null, "Ambiguous date columns must not pick an arbitrary version.");
            await Consistent(f, Cache(opened));
        });
        Add("a recent read without a modification marker uses a bounded fallback without claiming a known version", async f =>
        {
            var recent = DateTimeOffset.UtcNow.AddMinutes(-2);
            f.App.LoadSessionFixture(Source("", recent.AddMinutes(-1))); f.App.AcceptTicketCacheFixture(Detail(recent));
            var opened = await Open(f); var cache = Cache(opened); State(cache, "recent", true, false);
            Require(cache.GetProperty("sourceModifiedAt").ValueKind == JsonValueKind.Null
                && cache.GetProperty("cachedModifiedAt").ValueKind == JsonValueKind.Null, "Recent fallback must not manufacture source-version proof.");
            Require(cache.GetProperty("reason").GetString()!.Contains("absente", StringComparison.Ordinal),
                "An absent modification marker acquired the diagnostic for a known close date.");
            var skipped = await f.Call("workplace.ticket.read", new { ticketKey = Key(opened), automatic = true });
            Require(skipped.GetProperty("readAt").GetDateTimeOffset() == recent, "Recent fallback caused an unnecessary read.");
            f.App.AcceptTicketCacheFixture(Detail(DateTimeOffset.UtcNow.AddMinutes(-6)));
            State(Cache(await Open(f)), "unknown", true, true);
        });
        Add("the same ticket ID in another host or installation never borrows cached content", async f =>
        {
            f.App.AcceptTicketCacheFixture(Detail(OldRead));
            var original = await Open(f); var originalKey = Key(original);
            foreach (var source in new[] { "http://other-session-glpi.test/glpi/front/ticket.php", "http://session-glpi.test/another-glpi/front/ticket.php" })
            {
                var profile = new SourceProfile { Id = Guid.NewGuid().ToString("N"), Name = "Autre installation synthétique", Url = source, Interpretation = f.App.Settings.Dashboard.Copy() };
                await f.Call("sources.save", new { revision = f.App.Revision, source = profile });
                await f.Call("sources.select", new { revision = f.App.Revision, id = profile.Id });
                f.App.LoadSessionFixture(Source(Stamp(Modified), DateTimeOffset.UtcNow, source));
                var foreign = await Open(f); var cache = Cache(foreign);
                Require(Key(foreign) != originalKey && !foreign.GetProperty("loaded").GetBoolean()
                    && !cache.GetProperty("hasContent").GetBoolean() && cache.GetProperty("state").GetString() != "current",
                    "A same-number ticket in another GLPI installation borrowed cached content.");
                await Consistent(f, cache);
            }
        });
        Add("persisted offline content retains its source version and actual observation dates across application restart", async f =>
        {
            f.App.AcceptTicketCacheFixture(Detail(OldRead)); var opened = await Open(f);
            await f.Call("workplace.personal.save", new { ticketKey = Key(opened), offline = true });
            await using var reopened = new PilotApplication(f.Host, (_, _) => { }, f.App.Settings.Copy());
            reopened.LoadSessionFixture(Source(Stamp(Modified), DateTimeOffset.UtcNow));
            var loaded = await Call(reopened, "workplace.ticket.open", new { revision = reopened.Revision, rowIndex = 0 });
            State(Cache(loaded), "current", true, false);
            Require(loaded.GetProperty("readAt").GetDateTimeOffset() == OldRead
                && loaded.GetProperty("sections")[0].GetProperty("readAt").GetDateTimeOffset() == OldRead,
                "Restoring the offline cache changed the body's observation dates.");
            var skipped = await Call(reopened, "workplace.ticket.read", new { ticketKey = Key(loaded), automatic = true });
            Require(skipped.GetProperty("readAt").GetDateTimeOffset() == OldRead, "Restart lost the persisted equal-version cache decision.");
        });
        Add("normal source opening reuses library content without rewriting its saved version or confirmation", async f =>
        {
            var seed = new TicketLibrarySeed
            {
                Ticket = Reference(), Title = "Incident cache", Status = "Nouveau", StatusCode = 1,
                LastModifiedAt = Modified, LastModifiedRaw = Stamp(Modified), LastModifiedReliable = true, LastModifiedPrecision = "seconds"
            };
            var store = new TicketLibraryStore(new LibraryPreferences().DatabasePath);
            await store.SaveDetailAsync(seed, Detail(OldRead), new() { Complete = true, TabsDiscovered = 0, TabsLoaded = 0 }, "cache-fixture-reader");
            var before = (await store.GetAsync(seed.Ticket))!;
            var opened = await Open(f); State(Cache(opened), "current", true, false);
            Require(opened.GetProperty("loaded").GetBoolean() && opened.GetProperty("readAt").GetDateTimeOffset() == OldRead,
                "A normal GLPI source failed to reuse its already stored library body.");
            await Consistent(f, Cache(opened));
            var after = (await store.GetAsync(seed.Ticket))!;
            Require(JsonSerializer.Serialize(before, PilotApplication.Json) == JsonSerializer.Serialize(after, PilotApplication.Json),
                "Consulting cached library content changed its version, read time or confirmation proof.");
        });
        Add("a tab-only read preserves a confirmed library main body across reopening without confirming an incomplete section", async f =>
        {
            var seed = new TicketLibrarySeed
            {
                Ticket = Reference(), Title = "Incident cache", Status = "Nouveau", StatusCode = 1,
                LastModifiedAt = Modified, LastModifiedRaw = Stamp(Modified), LastModifiedReliable = true, LastModifiedPrecision = "seconds"
            };
            var store = new TicketLibraryStore(new LibraryPreferences().DatabasePath);
            await store.SaveDetailAsync(seed, Detail(OldRead), new() { Complete = true }, "cache-fixture-reader");
            var databaseBefore = await File.ReadAllBytesAsync(store.DatabasePath);
            var opened = await Open(f); State(Cache(opened), "current", true, false);
            await f.Call("workplace.personal.save", new { ticketKey = Key(opened), offline = true });
            var tabReadAt = DateTimeOffset.UtcNow;
            var merged = Detail(tabReadAt); merged.Sections[0].Data.ExtractedAt = OldRead;
            merged.Sections.Add(new() { Name = "Historique", Kind = "table", Data = new() { Headers = ["Événement"], Rows = [["Onglet lu explicitement"]], Complete = true, ExtractedAt = tabReadAt } });
            f.App.AcceptTicketCacheFixture(merged, observeSource: false);
            var afterTab = await Open(f); State(Cache(afterTab), "current", true, false);
            var skipped = await f.Call("workplace.ticket.read", new { ticketKey = Key(afterTab), automatic = true });
            Require(skipped.GetProperty("sections")[0].GetProperty("readAt").GetDateTimeOffset() == OldRead
                && skipped.GetProperty("sections")[1].GetProperty("readAt").GetDateTimeOffset() == tabReadAt,
                "A tab-only read changed the main body's actual observation date.");
            await Consistent(f, Cache(skipped));
            await using var reopened = new PilotApplication(f.Host, (_, _) => { }, f.App.Settings.Copy());
            reopened.LoadSessionFixture(Source(Stamp(Modified), DateTimeOffset.UtcNow));
            var restored = await Call(reopened, "workplace.ticket.open", new { revision = reopened.Revision, rowIndex = 0 });
            State(Cache(restored), "current", true, false);
            await Call(reopened, "workplace.ticket.read", new { ticketKey = Key(restored), automatic = true });
            Require(restored.GetProperty("sections")[0].GetProperty("readAt").GetDateTimeOffset() == OldRead
                && restored.GetProperty("sections")[1].GetProperty("readAt").GetDateTimeOffset() == tabReadAt,
                "Restoring a merged offline copy changed the independent body and tab observations.");
            reopened.LoadSessionFixture(Source(Stamp(Modified.AddHours(1)), DateTimeOffset.UtcNow));
            State(Cache(await Call(reopened, "workplace.ticket.open", new { revision = reopened.Revision, rowIndex = 0 })), "stale", true, true);
            merged.Sections[1].Data.Complete = false;
            f.App.AcceptTicketCacheFixture(merged, observeSource: false);
            State(Cache(await Open(f)), "unknown", true, true);
            Require((await File.ReadAllBytesAsync(store.DatabasePath)).SequenceEqual(databaseBefore),
                "Reading a local body or its separate tab rewrote the collected library database.");
        });
        return tests;
    }

    public static IReadOnlyList<(string Name, Func<Task> Run)> CreateBrowserChecks(IBrowser browser) =>
        [("Ticket cache: real automatic reads skip equal versions, reread changed stamps and preserve manual force", () => RealReads(browser))];

    private static async Task RealReads(IBrowser browser)
    {
        await using var glpi = await TicketLibraryCaptureBrowserFixture.OpenAsync(browser, SessionFixture.SourceA);
        await using var f = new SessionFixture(glpi.Service);
        f.App.Settings.Dashboard.ColumnMappings["updated"] = "Dernière modification";
        f.App.Settings.DetailTimeoutSeconds = 5;
        f.App.LoadSessionFixture(Source(Stamp(Modified), DateTimeOffset.UtcNow.AddMinutes(-1)));
        var opened = await Open(f); var key = Key(opened);
        var first = await f.Call("workplace.ticket.read", new { ticketKey = key, automatic = true });
        var firstRequests = glpi.DetailRequests.Count; Require(firstRequests > 0, "The fixture did not perform a real initial ticket read.");
        var firstRead = first.GetProperty("readAt").GetDateTimeOffset(); State(Cache(first), "current", true, false);
        var second = await f.Call("workplace.ticket.read", new { ticketKey = key, automatic = true });
        Require(glpi.DetailRequests.Count == firstRequests && second.GetProperty("readAt").GetDateTimeOffset() == firstRead,
            "An equal source version caused an automatic browser read.");
        f.App.LoadSessionFixture(Source(Stamp(Modified.AddMinutes(1)), DateTimeOffset.UtcNow));
        opened = await Open(f); State(Cache(opened), "stale", true, true);
        var refreshed = await f.Call("workplace.ticket.read", new { ticketKey = key, automatic = true });
        var refreshedRequests = glpi.DetailRequests.Count;
        Require(refreshedRequests > firstRequests && glpi.DetailRequests.All(id => id == "42"), "A changed stamp did not reread the exact ticket.");
        State(Cache(refreshed), "current", true, false);
        await f.Call("workplace.ticket.read", new { ticketKey = key });
        Require(glpi.DetailRequests.Count > refreshedRequests, "Explicit manual refresh was incorrectly suppressed by the cache.");
    }

    private static ExtractionResult Source(string modified, DateTimeOffset observedAt, string source = SessionFixture.SourceA) => new()
    {
        Headers = ["ID", "Titre", "Statut", "Dernière modif"], Rows = [["42", "Incident cache", "Nouveau", modified]],
        SourceUrl = source, ExtractedAt = observedAt, Complete = true,
        Provenance = new() { SourceKind = "glpi", RequestedUrl = source, SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "ticket-cache-fixture" }
    };
    private static TicketDetailResult Detail(DateTimeOffset readAt) => new()
    {
        Ticket = Reference(), Title = "Incident cache", ReadAt = readAt,
        Sections = [new() { Name = "Description", Kind = "text", Data = new() { Headers = ["Contenu"], Rows = [["Description synthétique conservée"]], Complete = true, ExtractedAt = readAt } }]
    };
    private static Task<JsonElement> Open(SessionFixture f) => f.Call("workplace.ticket.open", new { revision = f.App.Revision, rowIndex = 0 });
    private static string Key(JsonElement opened) => opened.GetProperty("personal").GetProperty("key").GetString()!;
    private static JsonElement Cache(JsonElement value) => value.GetProperty("cache");
    private static void State(JsonElement cache, string state, bool hasContent, bool autoRead) => Require(cache.GetProperty("state").GetString() == state
        && cache.GetProperty("hasContent").GetBoolean() == hasContent && cache.GetProperty("autoRead").GetBoolean() == autoRead
        && !string.IsNullOrWhiteSpace(cache.GetProperty("reason").GetString()), "Expected cache " + state + ": " + cache.GetRawText());
    private static async Task Consistent(SessionFixture f, JsonElement expected)
    {
        var list = await f.Call("records.list", new { revision = f.App.Revision }); var actual = Cache(list.GetProperty("rows")[0]);
        foreach (var name in new[] { "state", "hasContent", "autoRead", "readAt", "sourceModifiedAt", "sourceModifiedRaw", "cachedModifiedAt", "cachedModifiedRaw", "sourceObservedAt", "sourceToken" })
            Require(actual.GetProperty(name).GetRawText() == expected.GetProperty(name).GetRawText(), "Search list and detail disagree about " + name + ".");
    }
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object args)
        => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args, PilotApplication.Json)), PilotApplication.Json);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
