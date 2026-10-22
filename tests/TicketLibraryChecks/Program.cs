using System.Text;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-LibraryChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0; var total = 0;
try
{
    await Check("Real SQLite persists complete original content and citations after reopening", async () =>
    {
        var path = Path.Combine(root, "persistent.db"); var store = new TicketLibraryStore(path); var seed = Seed();
        await store.ObserveSeedAsync(seed); var write = await store.SaveDetailAsync(seed, Detail(seed, "<b>Solution</b>\u0092 inchangée"), Coverage(), "reader-1");
        var reopened = new TicketLibraryStore(path); var read = await reopened.GetAsync(seed.Ticket);
        Require(read is { DetailComplete: true, DetailsCurrent: true, Pending: true } && read.Detail!.Sections[0].Data.Rows[0][3] == "<b>Solution</b>\u0092 inchangée");
        var results = await reopened.SearchAsync(new() { Text = "Solution", ResolvedOnly = true });
        Require(results.Total == 1 && results.Rows.Single().Citations.Any(c => c.IsSolution && c.Url == seed.Ticket.Url && c.Excerpt.Contains('\u0092')));
    });
    await Check("Canonical identities isolate portals and GLPI subdirectories", async () =>
    {
        var store = Store("identity"); var a = Seed(); var b = Seed(); var c = Seed();
        b.Ticket = new("42", "https://other.example/glpi/front/ticket.form.php?id=42");
        c.Ticket = new("42", "https://fixture.example/other/front/ticket.form.php?id=42");
        foreach (var seed in new[] { a, b, c }) await store.ObserveSeedAsync(seed);
        Require((await store.GetStateAsync()).TicketCount == 3);
    });
    await Check("Unchanged reliable minute token reuses only a complete sufficiently late read", async () =>
    {
        var seed = Seed(); var store = Store("skip"); var doc = (await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "reader-1")).Document;
        Require(TicketLibraryStore.CanSkip(seed, doc, "reader-1", new() { Now = At(14, 0) }));
        doc.ReadAt = At(12, 36); doc.Detail!.ReadAt = doc.ReadAt.Value;
        Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-1", new() { Now = At(14, 0) }));
    });
    await Check("Unknown timezone token and missing or ambiguous date never skip", async () =>
    {
        var seed = Seed(); var store = Store("skip-unknown"); var doc = (await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "reader-1")).Document;
        seed.LastModifiedAt = null; Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-1"));
        seed = Seed(); seed.LastModifiedReliable = false; Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-1"));
        seed.LastModifiedRaw = ""; Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-1"));
    });
    await Check("Force refresh, reader changes, seven-day expiry and future reads prevent reuse", async () =>
    {
        var seed = Seed(); var store = Store("skip-policy"); var doc = (await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "reader-1")).Document;
        Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-2", new() { Now = At(14, 0) }));
        Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-1", new() { Now = At(14, 0), ForceRefresh = true }));
        Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-1", new() { Now = At(14, 0).AddDays(8) }));
        Require(!TicketLibraryStore.CanSkip(seed, doc, "reader-1", new() { Now = At(12, 39) }));
    });
    await Check("A newer index invalidates old solution without deleting its original content", async () =>
    {
        var store = Store("newer-index"); var old = Seed(); await store.SaveDetailAsync(old, Detail(old, "Ancienne solution"), Coverage(), "reader-1");
        var next = Seed(); next.LastModifiedAt = At(12, 35); next.LastModifiedRaw = "2026-10-20 12:35"; next.StatusCode = 2; next.Status = "En cours";
        var saved = (await store.ObserveSeedAsync(next)).Document;
        Require(!saved.DetailsCurrent && saved.DetailComplete && saved.Detail!.Sections[0].Data.Rows[0][3] == "Ancienne solution" && saved.DetailModifiedAt == old.LastModifiedAt);
        Require((await store.SearchAsync(new() { Text = "solution", ResolvedOnly = true })).Total == 0);
        var refreshed = (await store.SaveDetailAsync(next, Detail(next, "Nouvelle lecture"), Coverage(), "reader-1")).Document;
        Require(refreshed.DetailsCurrent && refreshed.DetailModifiedAt == next.LastModifiedAt);
    });
    await Check("Equal date with different content conserves both versions and does not select a winner", async () =>
    {
        var store = Store("conflict"); var seed = Seed(); await store.SaveDetailAsync(seed, Detail(seed, "Version A"), Coverage(), "reader-1");
        var result = await store.SaveDetailAsync(seed, Detail(seed, "Version B"), Coverage(), "reader-1");
        var conflict = (await store.GetConflictsAsync(result.Document.Key)).Single();
        Require(result.ConflictCreated && !result.Document.DetailsCurrent && result.Document.Detail!.Sections[0].Data.Rows[0][3] == "Version A");
        Require(conflict.Current.Detail!.Sections[0].Data.Rows[0][3] == "Version A" && conflict.Incoming.Detail!.Sections[0].Data.Rows[0][3] == "Version B");
        Require((await store.SearchAsync(new() { ResolvedOnly = true })).Total == 0);
    });
    await Check("Replaying the same conflicting version does not duplicate conflict history", async () =>
    {
        var store = Store("conflict-idempotent"); var seed = Seed(); await store.SaveDetailAsync(seed, Detail(seed, "A"), Coverage(), "reader-1");
        await store.SaveDetailAsync(seed, Detail(seed, "B"), Coverage(), "reader-1"); await store.SaveDetailAsync(seed, Detail(seed, "B"), Coverage(), "reader-1");
        Require((await store.GetStateAsync()).ConflictCount == 1);
    });
    await Check("An older source cannot replace a newer source even if read later", async () =>
    {
        var store = Store("stale"); var recent = Seed(); recent.LastModifiedAt = At(12, 50); recent.LastModifiedRaw = "2026-10-20 12:50";
        await store.SaveDetailAsync(recent, Detail(recent, "Récente"), Coverage(), "reader-1");
        var old = Seed(); var late = Detail(old, "Ancienne"); late.ReadAt = At(16, 0);
        var result = await store.SaveDetailAsync(old, late, Coverage(), "reader-1");
        Require(result.Outcome == "older-source-version" && result.Document.Detail!.Sections[0].Data.Rows[0][3] == "Récente");
    });
    await Check("Unknown source dates retain conflicting evidence without client-clock overwrite", async () =>
    {
        var store = Store("unknown"); var seed = Seed(); seed.LastModifiedAt = null; seed.LastModifiedReliable = false; seed.LastModifiedPrecision = "unknown";
        await store.SaveDetailAsync(seed, Detail(seed, "Initiale"), Coverage(), "reader-1");
        var next = Detail(seed, "Concurrente"); next.ReadAt = At(18, 0);
        var result = await store.SaveDetailAsync(seed, next, Coverage(), "reader-1");
        Require(result.ConflictCreated && result.Outcome == "incomparable-source-version" && result.Document.Detail!.Sections[0].Data.Rows[0][3] == "Initiale");
    });
    await Check("Overlapping precision and different interpretations of the same raw date are incomparable", async () =>
    {
        var a = Seed(); var b = Seed(); b.LastModifiedPrecision = "seconds"; b.LastModifiedAt = At(12, 34).AddSeconds(30); b.LastModifiedRaw = "2026-10-20 12:34:30";
        Require(TicketLibraryRules.CompareVersion(b, a) is null && TicketLibraryRules.CompareVersion(a, b) is null);
        var reinterpreted = Seed(); reinterpreted.LastModifiedAt = a.LastModifiedAt!.Value.AddHours(1);
        Require(TicketLibraryRules.CompareVersion(reinterpreted, a) is null && TicketLibraryRules.CompareVersion(a, reinterpreted) is null);
        var store = Store("different-date-preferences"); await store.SaveDetailAsync(a, Detail(a, "Originale"), Coverage(), "reader-1");
        var written = await store.SaveDetailAsync(reinterpreted, Detail(reinterpreted, "Interprétation différente"), Coverage(), "reader-1");
        Require(written.ConflictCreated && written.Outcome == "incomparable-source-version" && written.Document.Detail!.Sections[0].Data.Rows[0][3] == "Originale" && !written.Document.DetailsCurrent);
    });
    await Check("A partial newer detail cannot replace a previously complete detail", async () =>
    {
        var store = Store("partial"); var seed = Seed(); await store.SaveDetailAsync(seed, Detail(seed, "Complet conservé"), Coverage(), "reader-1");
        seed.LastModifiedAt = At(12, 35); seed.LastModifiedRaw = "2026-10-20 12:35";
        var coverage = Coverage(); coverage.Complete = false; coverage.Limitations.Add("Onglet non chargé");
        var result = await store.SaveDetailAsync(seed, Detail(seed, "Lecture partielle"), coverage, "reader-1");
        Require(result.Document.Detail!.Sections[0].Data.Rows[0][3] == "Complet conservé" && !result.Document.DetailsCurrent && result.ConflictCreated);
    });
    await Check("Document-reference coverage remains complete without claiming attachment binaries were read", async () =>
    {
        var store = Store("attachment-coverage"); var seed = Seed(); var coverage = Coverage();
        coverage.Limitations.Add("Les pièces jointes sont référencées ; leur contenu binaire n’est pas téléchargé automatiquement.");
        var stored = (await store.SaveDetailAsync(seed, Detail(seed), coverage, "reader-1")).Document;
        Require(stored.DetailComplete && stored.Coverage.Limitations.Count == 1 && TicketLibraryStore.CanSkip(seed, stored, "reader-1", new() { Now = At(14, 0) }));
    });
    await Check("Read clocks and ephemeral selectors do not cause false content conflicts", async () =>
    {
        var store = Store("clock"); var seed = Seed(); var first = Detail(seed); await store.SaveDetailAsync(seed, first, Coverage(), "reader-1");
        var second = Detail(seed); second.ReadAt = At(13, 0); second.Sections[0].Selector = "#random-new-dom"; second.Sections[0].Data.ExtractedAt = At(13, 0);
        var result = await store.SaveDetailAsync(seed, second, Coverage(), "reader-2");
        Require(!result.ConflictCreated && result.Outcome == "identical" && result.Document.ReadAt == second.ReadAt && result.Document.ReaderSignature == "reader-2");
    });
    await Check("Optimistic revision check preserves concurrent local state", async () =>
    {
        var store = Store("revision"); var seed = Seed(); var first = await store.ObserveSeedAsync(seed); await store.ObserveSeedAsync(seed);
        await RejectAsync<InvalidOperationException>(() => store.SaveDetailAsync(seed, Detail(seed), Coverage(), "reader-1", first.Document.LocalRevision));
        Require((await store.GetAsync(seed.Ticket))!.Detail is null);
    });
    await Check("Cancellation does not discard already committed tickets or create the next ticket", async () =>
    {
        var store = Store("cancel"); await store.ObserveSeedAsync(Seed()); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await RejectAsync<OperationCanceledException>(() => store.ObserveSeedAsync(Seed("43"), cancelled.Token));
        Require((await store.GetStateAsync()).TicketCount == 1);
    });
    await Check("Literal search punctuation does not become FTS or SQL syntax", async () =>
    {
        var store = Store("search"); var seed = Seed(); await store.SaveDetailAsync(seed, Detail(seed, "Imprimante réseau"), Coverage(), "reader-1");
        Require((await store.SearchAsync(new() { Text = "\"imprimante\" OR * NEAR() -- '" })).Total == 1);
        Require((await store.SearchAsync(new() { Text = "\"*'()" })).Total == 0);
    });
    await Check("Metadata-only tickets are searchable by exact identifier including one digit", async () =>
    {
        var store = Store("numeric-search"); var one = Seed("7"); one.Title = "Connexion sans chiffre";
        var many = Seed("8765432"); many.Title = "Incident sans chiffre";
        await store.ObserveSeedAsync(one); await store.ObserveSeedAsync(many);
        Require((await store.SearchAsync(new() { Text = "7" })).Rows.Single().Ticket.Id == "7");
        Require((await store.SearchAsync(new() { Text = "8765432" })).Rows.Single().Ticket.Id == "8765432");
        Require((await store.SearchAsync(new() { Text = "876543" })).Total == 0);
    });
    await Check("Search paging and installation filter remain independent", async () =>
    {
        var store = Store("paging"); for (var i = 1; i <= 4; i++) await store.ObserveSeedAsync(Seed(i.ToString()));
        var page = await store.SearchAsync(new() { Offset = 2, Limit = 1 }); Require(page.Total == 4 && page.Rows.Count == 1 && page.Offset == 2);
        Require((await store.SearchAsync(new() { Installation = "https://absent.example" })).Total == 0);
    });
    await Check("Global BM25 ranking selects the strongest match before limiting the first page", async () =>
    {
        var store = Store("ranking");
        for (var i = 1; i <= 30; i++)
        {
            var seed = Seed(i.ToString()); seed.Title = "A " + i; seed.Category = "";
            await store.SaveDetailAsync(seed, Detail(seed, "imprimante " + string.Join(' ', Enumerable.Repeat("texte secondaire sans rapport", 100))), Coverage(), "reader-1");
        }
        var best = Seed("99"); best.Title = "Z meilleure solution"; best.Category = "";
        await store.SaveDetailAsync(best, Detail(best, "imprimante réseau imprimante réseau imprimante réseau"), Coverage(), "reader-1");
        var result = await store.SearchAsync(new() { Text = "imprimante réseau", Limit = 1, ResolvedOnly = true });
        Require(result.Total == 31 && result.Rows.Single().Ticket.Id == "99" && result.Rows.Single().Score > 0);
    });
    await Check("Source enumeration and seed projection do not load ticket detail payloads", async () =>
    {
        var store = Store("sources"); var seed = Seed(); await store.SaveDetailAsync(seed, Detail(seed), Coverage(), "reader-1");
        Require((await store.ReadSourcesAsync()).SequenceEqual(new[] { "https://fixture.example/glpi" }));
        Require((await store.ReadSeedsAsync()).Single().IndexValues.SequenceEqual(seed.IndexValues));
    });
    await Check("NDJSON export preserves native values and cannot overwrite the active database", async () =>
    {
        var store = Store("export"); var seed = Seed(); await store.SaveDetailAsync(seed, Detail(seed, "Texte\u0092 natif"), Coverage(), "reader-1");
        var target = Path.Combine(root, "backup.ndjson"); await store.ExportAsync(target);
        var lines = File.ReadAllLines(target).Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            Require(lines[0].RootElement.GetProperty("recordType").GetString() == "manifest");
            Require(lines.Count(line => line.RootElement.GetProperty("recordType").GetString() == "ticket") == 1);
            var chunk = lines.Single(line => line.RootElement.GetProperty("recordType").GetString() == "chunk" && line.RootElement.GetProperty("citation").GetProperty("isSolution").GetBoolean()).RootElement;
            Require(chunk.GetProperty("text").GetString() == "Texte\u0092 natif" && chunk.GetProperty("chunkKey").GetString()!.Length == 64);
            Require(chunk.GetProperty("citation").GetProperty("current").GetBoolean() && chunk.GetProperty("citation").GetProperty("url").GetString() == seed.Ticket.Url);
        }
        finally { foreach (var line in lines) line.Dispose(); }
        await RejectAsync<InvalidDataException>(() => store.ExportAsync(store.DatabasePath));
        await RejectAsync<InvalidDataException>(() => store.ExportAsync(store.DatabasePath + "-wal"));
    });
    await Check("Live network or redirected SQLite paths are rejected", async () =>
    {
        Reject<InvalidDataException>(() => new TicketLibraryStore("\\\\server\\shared\\library.db"));
        Reject<InvalidDataException>(() => new TicketLibraryStore("//server/shared/library.db"));
        if (!OperatingSystem.IsWindows())
        {
            var linked = Path.Combine(root, "linked"); Directory.CreateSymbolicLink(linked, root);
            Reject<InvalidDataException>(() => new TicketLibraryStore(Path.Combine(linked, "library.db")));
        }
        await Task.CompletedTask;
    });
    await Check("A future schema is rejected without rewriting its version", async () =>
    {
        var store = Store("future"); await store.InitializeAsync();
        using (var c = new SqliteConnection(store.ConnectionString)) { c.Open(); using var command = c.CreateCommand(); command.CommandText = "UPDATE ticket_library_schema SET version=999"; command.ExecuteNonQuery(); }
        await RejectAsync<InvalidDataException>(() => new TicketLibraryStore(store.DatabasePath).InitializeAsync());
    });
    await Check("Other modules can commit to their own tables in the same SQLite file", async () =>
    {
        var store = Store("shared-file"); await store.InitializeAsync();
        using (var c = new SqliteConnection(store.ConnectionString)) { c.Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE team_fixture(id TEXT PRIMARY KEY); INSERT INTO team_fixture VALUES('independent')"; command.ExecuteNonQuery(); }
        await store.ObserveSeedAsync(Seed()); Require((await store.GetStateAsync()).TicketCount == 1);
    });
    await Check("A failed delta page rolls back all tickets and its cursor together", async () =>
    {
        var store = Store("atomic-page"); var identity = Guid.NewGuid().ToString("N");
        await TicketLibrarySyncService.Prepare(store, identity, default);
        var first = TicketLibraryRules.FromDetail(Seed("1"), Detail(Seed("1")), Coverage(), "reader-1"); first.CentralRevision = 1;
        var second = TicketLibraryRules.FromDetail(Seed("2"), Detail(Seed("2")), Coverage(), "reader-1"); second.CentralRevision = 2;
        var invalid = TicketLibraryRules.Copy(second); invalid.ContentHash = new('0', 64);
        await RejectAsync<InvalidDataException>(() => TicketLibrarySyncService.Apply(store, identity, 0, [new(1, first, null, "inserted"), new(2, invalid, null, "inserted")], default));
        Require((await store.GetStateAsync()).TicketCount == 0 && await TicketLibrarySyncService.Prepare(store, identity, default) == 0);
        await TicketLibrarySyncService.Apply(store, identity, 0, [new(1, first, null, "inserted"), new(2, second, null, "inserted")], default);
        Require((await store.GetStateAsync()).TicketCount == 2 && await TicketLibrarySyncService.Prepare(store, identity, default) == 2);
    });
    await Check("Returning to a previous central database queues current snapshots again", async () =>
    {
        var store = Store("central-switch"); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        await store.ObserveSeedAsync(Seed()); await TicketLibrarySyncService.Prepare(store, a, default);
        await store.Run(c => TicketLibraryStore.Execute(c, null, "DELETE FROM ticket_library_outbox"), default);
        await TicketLibrarySyncService.Prepare(store, b, default); Require((await store.GetStateAsync()).PendingCount == 1);
        await store.Run(c => TicketLibraryStore.Execute(c, null, "DELETE FROM ticket_library_outbox"), default);
        await TicketLibrarySyncService.Prepare(store, a, default); Require((await store.GetStateAsync()).PendingCount == 1);
        await RejectAsync<InvalidOperationException>(() => TicketLibrarySyncService.Apply(store, b, 0, [], default));
    });
    await Check("Concurrent SQLite writers preserve the newer source version", async () =>
    {
        var store = Store("concurrent-local"); var a = Seed(); var b = Seed(); b.LastModifiedRaw = "2026-10-20 12:35"; b.LastModifiedAt = At(12, 35);
        await Task.WhenAll(store.SaveDetailAsync(a, Detail(a, "Ancienne"), Coverage(), "reader-1"), new TicketLibraryStore(store.DatabasePath).SaveDetailAsync(b, Detail(b, "Récente"), Coverage(), "reader-1"));
        Require((await store.GetAsync(a.Ticket))!.Detail!.Sections[0].Data.Rows[0][3] == "Récente");
    });
    if (args.Contains("--postgres", StringComparer.Ordinal)) await TicketLibraryPostgresChecks.Run(root, Check);
    Console.WriteLine($"{passed}/{total} ticket library checks passed.");
    return passed == total ? 0 : 1;
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }

TicketLibraryStore Store(string name) => new(Path.Combine(root, name + ".db"));
static DateTimeOffset At(int hour, int minute) => new(2026, 10, 20, hour, minute, 0, TimeSpan.Zero);
static TicketLibrarySeed Seed(string id = "42") => new()
{
    Ticket = new(id, "https://fixture.example/glpi/front/ticket.form.php?id=" + id), Title = "Imprimante réseau " + id,
    Status = "Résolu", StatusCode = 5, Category = "Matériel", LastModifiedRaw = "2026-10-20 12:34", LastModifiedAt = At(12, 34),
    LastModifiedReliable = true, LastModifiedPrecision = "minutes", ObservedAt = At(12, 40),
    Fields = new() { ["id"] = id, ["title"] = "Imprimante réseau " + id }, IndexHeaders = ["ID", "Titre"], IndexValues = [id, "Imprimante réseau " + id]
};
static TicketLibraryCoverage Coverage() => new() { TabsDiscovered = 1, TabsLoaded = 1, Complete = true };
static TicketDetailResult Detail(TicketLibrarySeed seed, string solution = "Redémarrer le service impression") => new()
{
    Ticket = seed.Ticket, Title = seed.Title, ReadAt = At(12, 40), Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
    Sections = [new() { Name = "Chronologie", Kind = "timeline", TabKey = "treatment", Data = new() { Headers = ["Type", "Date", "Auteur", "Contenu"], Rows = [new[] { "Solution", "2026-10-20", "Agent synthétique", solution }], Complete = true } }]
};
async Task Check(string name, Func<Task> body)
{
    total++; try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
static void Reject<T>(Action body) where T : Exception { try { body(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static async Task RejectAsync<T>(Func<Task> body) where T : Exception { try { await body(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
