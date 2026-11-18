using System.Globalization;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-KnowledgeChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0; var total = 0;
const string prefix = TicketLibraryKnowledge.SqlitePrefix;
try
{
    await Check("SQL corpus exposes exact identities, dates, revisions and confirmation without decoding JSON", async () =>
    {
        var store = Store("metadata"); var seed = KnowledgeFixture.Seed();
        var saved = await store.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed), KnowledgeFixture.Coverage(), "knowledge-reader");
        var row = (await Rows(store, "SELECT ticket_id,ticket_url,content_hash,content_confirmed,detail_complete,local_revision,last_modified_raw,last_modified_precision,access_basis,visibility FROM " + prefix + "tickets")).Single();
        Require(row[0] == seed.Ticket.Id && row[1] == seed.Ticket.Url && row[2] == saved.Document.ContentHash && row[3] == "1" && row[4] == "1"
            && row[5] == saved.Document.LocalRevision.ToString(CultureInfo.InvariantCulture) && row[6] == seed.LastModifiedRaw && row[7] == "seconds"
            && row[8] == "collecting-session" && row[9] == "unknown");
        var raw = await Rows(store, "SELECT document_json FROM ticket_library_tickets");
        await Rows(store, "SELECT body FROM " + prefix + "texts ORDER BY citation_key");
        Require((await Rows(store, "SELECT document_json FROM ticket_library_tickets")).Single()[0] == raw.Single()[0]);
    });
    await Check("All original fields, duplicate index labels, empty cells and missing cells stay distinguishable", async () =>
    {
        var store = Store("cells"); var seed = KnowledgeFixture.Seed();
        await store.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed), KnowledgeFixture.Coverage(), "knowledge-reader");
        var fields = await Rows(store, "SELECT origin,source_path,column_index,name,value FROM " + prefix + "fields WHERE version_kind='selected' ORDER BY source_path");
        Require(fields.Count == seed.Fields.Count + seed.IndexValues.Count);
        Require(fields.Any(r => r[1] == "/seed/fields/clé~1avec~0séparateur" && r[4] == "AiguilleChamp"));
        Require(fields.Count(r => r[0] == "index-column" && r[3] == "Libellé identique") == 2 && fields.Any(r => r[0] == "index-column" && r[4] == ""));
        var cells = await Rows(store, "SELECT row_index,column_index,name,value FROM " + prefix + "cells WHERE section_index=1 AND version_kind='selected' ORDER BY row_index,column_index");
        Require(cells.Count == 4 && cells[0][3] == "" && cells[1][3] == "ValeurCourte" && cells[2][3] == "" && cells[3][2] is null && cells[3][3] == "SansEntête"
            && !cells.Any(row => row[0] == "0" && row[1] == "1"));
        Require((await Rows(store, "SELECT row_count,column_count FROM " + prefix + "sections WHERE section_index=2 AND version_kind='selected'")).Single().SequenceEqual(["0", "1"]));
    });
    await Check("Observed items retain their structured dates, ownership, URL and searchable text", async () =>
    {
        var store = Store("items"); var seed = KnowledgeFixture.Seed();
        await store.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed), KnowledgeFixture.Coverage(), "knowledge-reader");
        var item = (await Rows(store, "SELECT identity,author,owner,planned_start,waiting_reason,url,source_path,source_tab_key FROM " + prefix + "items WHERE version_kind='selected'")).Single();
        Require(item.SequenceEqual(["followup-88", "AuteurObservation", "ÉquipeMatériel", "2026-11-01 10:00", "AiguilleObservation", "https://fixture.example/glpi/front/ticket.form.php?id=9007199254740993", "/detail/observedItems/0", "treatment"]));
        foreach (var needle in new[] { "AiguilleChamp", "AiguilleColonne", "AiguilleObservation", "AuteurObservation" })
            Require((await store.SearchAsync(new() { Text = needle })).Rows.Single().Ticket.Id == seed.Ticket.Id);
    });
    await Check("Private timeline evidence and unloaded or empty sections never imply public or missing content", async () =>
    {
        var store = Store("privacy"); var seed = KnowledgeFixture.Seed();
        var detail = KnowledgeFixture.Detail(seed); detail.Tabs.Add(new() { Key = "unloaded", Name = "Historique", Loaded = false });
        detail.Warnings.Add("Avertissement global"); detail.Sections[1].Data.Warnings.Add("Avertissement section");
        await store.SaveDetailAsync(seed, detail, new() { Complete = false, TabsDiscovered = 2, TabsLoaded = 1, Limitations = ["Onglet non lu"] }, "knowledge-reader");
        var privacy = await Rows(store, "SELECT text_visibility FROM " + prefix + "texts WHERE source_kind='section-row' AND section_key='treatment' AND version_kind='selected'");
        Require(privacy.Single()[0] == "private");
        Require((await Rows(store, "SELECT visibility FROM " + prefix + "items WHERE version_kind='selected'")).Single()[0] == "unknown");
        Require((await Rows(store, "SELECT loaded FROM " + prefix + "tabs WHERE tab_key='unloaded' AND version_kind='selected'")).Single()[0] == "0");
        Require((await Rows(store, "SELECT origin FROM " + prefix + "limitations WHERE version_kind='selected'")).Count == 3);
        Require((await Rows(store, "SELECT detail_complete FROM " + prefix + "tickets")).Single()[0] == "0");
    });
    await Check("Corpus text supplies stable versioned citations and complete source rows including solution metadata", async () =>
    {
        var store = Store("citations"); var seed = KnowledgeFixture.Seed(); var detail = KnowledgeFixture.Detail(seed);
        await store.SaveDetailAsync(seed, detail, KnowledgeFixture.Coverage(), "knowledge-reader");
        var texts = await Rows(store, "SELECT citation_key,source_path,body,ticket_url,source_kind FROM " + prefix + "texts WHERE version_kind='selected' ORDER BY citation_key");
        Require(texts.Select(r => r[0]).Distinct().Count() == texts.Count);
        var solution = texts.Single(r => r[1] == "/detail/sections/0/data/rows/0");
        Require(solution[2]!.Contains("AuteurLigne", StringComparison.Ordinal) && solution[2]!.Contains("Privé", StringComparison.Ordinal)
            && solution[2]!.Contains("Solution intacte\u0092 <b>native</b>", StringComparison.Ordinal) && solution[3] == seed.Ticket.Url);
        var citations = (await store.SearchAsync(new() { Text = "Solution intacte", ResolvedOnly = true })).Rows.Single().Citations;
        Require(citations.Any(c => c.IsSolution && c.Excerpt == detail.Sections[0].Data.Rows[0][3]));
        Require((await new TicketLibraryStore(store.DatabasePath).SearchAsync(new() { Text = "AuteurLigne" })).Total == 1);
        var reopened = await Rows(new(store.DatabasePath), "SELECT citation_key,source_path,body,ticket_url,source_kind FROM " + prefix + "texts WHERE version_kind='selected' ORDER BY citation_key");
        Require(texts.Zip(reopened).All(pair => pair.First.SequenceEqual(pair.Second)));
    });
    await Check("Received unconfirmed content remains queryable and excluded from the confirmed corpus", async () =>
    {
        var store = Store("unconfirmed"); var seed = KnowledgeFixture.Seed();
        var context = new TicketLibraryCaptureContext(ObjectIdentity.Installation(seed.Ticket.Url), new string('d', 64), TicketLibraryAcquisition.ReaderSignature);
        var run = await store.BeginCaptureAsync(context, "all");
        await store.SetCaptureIndexAsync(run, new() { SourceUrl = "https://fixture.example/glpi/front/ticket.php", ScopeFingerprint = "knowledge-scope", AllStatuses = true, Complete = true, Tickets = [seed] });
        var initial = await store.ObserveCaptureSeedAsync(run, seed);
        await store.SaveCaptureDetailAsync(run, seed, new() { Detail = KnowledgeFixture.Detail(seed), Coverage = KnowledgeFixture.Coverage() }, initial.Document.LocalRevision);
        await store.FinishCaptureAsync(run, "cancelled", []);
        Require((await Rows(store, "SELECT content_confirmed,confirmation_pending,detail_complete FROM " + prefix + "tickets")).Single().SequenceEqual(["0", "1", "1"]));
        Require((await Rows(store, "SELECT body FROM " + prefix + "texts WHERE version_kind='selected' AND source_kind='section-row'")).Count > 0);
        Require((await Rows(store, "SELECT body FROM " + prefix + "texts WHERE version_kind='selected' AND content_confirmed=1 AND detail_complete=1")).Count == 0);
    });
    await Check("Conflicting and historical evidence stays normalized with its own version and cannot masquerade as current", async () =>
    {
        var store = Store("conflicts"); var seed = KnowledgeFixture.Seed();
        await store.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed), KnowledgeFixture.Coverage(), "knowledge-reader");
        await store.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed, "Contenu divergent"), KnowledgeFixture.Coverage(), "knowledge-reader");
        var versions = await Rows(store, "SELECT version_kind,content_confirmed,conflict_count FROM " + prefix + "versions");
        Require(versions.Any(r => r[0] == "history") && versions.Any(r => r[0] == "conflict-current") && versions.Any(r => r[0] == "conflict-incoming") && versions.All(r => r[1] == "0"));
        Require((await Rows(store, "SELECT body FROM " + prefix + "texts WHERE version_kind='conflict-incoming' AND source_path='/detail/sections/0/data/rows/0'")).Single()[0]!.Contains("Contenu divergent", StringComparison.Ordinal));
    });
    await Check("Legacy source documents acquire complete FTS in bounded resumable batches without rewriting original state", async () =>
    {
        var store = Store("migration");
        for (var i = 1; i <= 19; i++) { var seed = KnowledgeFixture.Seed(i.ToString(CultureInfo.InvariantCulture)); await store.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed), KnowledgeFixture.Coverage(), "knowledge-reader"); }
        var before = await Rows(store, "SELECT document_json,local_revision,central_revision FROM ticket_library_tickets ORDER BY ticket_key");
        var pending = await Rows(store, "SELECT mutation_id,document_json FROM ticket_library_outbox ORDER BY mutation_id");
        using (var c = new SqliteConnection(store.ConnectionString))
        {
            c.Open(); Execute(c, "DELETE FROM ticket_library_chunks; DELETE FROM ticket_library_knowledge_index");
            Require(TicketLibraryStore.BackfillKnowledgeBatch(c, default, 2) == 2);
            Require((long)Scalar(c, "SELECT COUNT(*) FROM ticket_library_knowledge_index")! == 2);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { TicketLibraryStore.BackfillKnowledgeBatch(c, cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); } catch (OperationCanceledException) { }
            Require((long)Scalar(c, "SELECT COUNT(*) FROM ticket_library_knowledge_index")! == 2);
        }
        var reopened = new TicketLibraryStore(store.DatabasePath); await reopened.InitializeAsync();
        Require((await reopened.SearchAsync(new() { Text = "AiguilleObservation", Limit = 100 })).Total == 19);
        var after = await Rows(reopened, "SELECT document_json,local_revision,central_revision FROM ticket_library_tickets ORDER BY ticket_key");
        Require(before.Zip(after).All(pair => pair.First.SequenceEqual(pair.Second)));
        Require(pending.Zip(await Rows(reopened, "SELECT mutation_id,document_json FROM ticket_library_outbox ORDER BY mutation_id")).All(pair => pair.First.SequenceEqual(pair.Second)));
        Require((await Rows(reopened, "SELECT COUNT(*) FROM ticket_library_knowledge_index")).Single()[0] == "19");
    });
    await Check("Read-only SQL connections can consume every view and separate independently collected installations", async () =>
    {
        var store = Store("readonly"); var seed = KnowledgeFixture.Seed(); await store.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed), KnowledgeFixture.Coverage(), "knowledge-reader");
        var other = KnowledgeFixture.Seed(); other.Ticket = new(other.Ticket.Id, other.Ticket.Url.Replace("fixture.example", "other.example", StringComparison.Ordinal));
        await store.SaveDetailAsync(other, KnowledgeFixture.Detail(other), KnowledgeFixture.Coverage(), "knowledge-reader");
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open();
        foreach (var view in TicketLibraryKnowledge.PostgresViewNames)
            using (var command = c.CreateCommand()) { command.CommandText = "SELECT * FROM " + view.Replace(TicketLibraryKnowledge.PostgresPrefix, prefix, StringComparison.Ordinal) + " LIMIT 1"; using var reader = command.ExecuteReader(); Require(reader.Read()); }
        using var filtered = c.CreateCommand(); filtered.CommandText = "SELECT DISTINCT ticket_url FROM " + prefix + "texts WHERE installation=$installation AND version_kind='selected'";
        filtered.Parameters.AddWithValue("$installation", "https://fixture.example/glpi"); using var found = filtered.ExecuteReader(); Require(found.Read() && found.GetString(0) == seed.Ticket.Url && !found.Read());
    });
    if (args.Contains("--postgres", StringComparer.Ordinal)) await KnowledgePostgresChecks.Run(root, Check);
    Console.WriteLine($"{passed}/{total} ticket library knowledge checks passed.");
    return passed == total ? 0 : 1;
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }

TicketLibraryStore Store(string name) => new(Path.Combine(root, name + ".db"));
static async Task<List<string?[]>> Rows(TicketLibraryStore store, string sql)
{
    await store.InitializeAsync();
    using var connection = new SqliteConnection(store.ConnectionString); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
    using var reader = command.ExecuteReader(); var rows = new List<string?[]>();
    while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray());
    return rows;
}
static object? Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
async Task Check(string name, Func<Task> body)
{
    total++; try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); }
}

internal static class KnowledgeFixture
{
    internal static TicketLibrarySeed Seed(string id = "9007199254740993") => new()
    {
        Ticket = new(id, "https://fixture.example/glpi/front/ticket.form.php?id=" + id), Title = "Ticket de connaissance", Status = "Résolu", StatusCode = 5,
        Category = "Matériel", LastModifiedRaw = "2026-11-01 08:00:00", LastModifiedAt = At(), LastModifiedReliable = true, LastModifiedPrecision = "seconds", ObservedAt = At().AddMinutes(5),
        Fields = new() { ["clé/avec~séparateur"] = "AiguilleChamp", ["vide"] = "" }, IndexHeaders = ["ID", "Libellé identique", "Libellé identique", ""], IndexValues = [id, "AiguilleColonne", "", "EntêteVide"]
    };
    internal static DateTimeOffset At() => new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    internal static TicketLibraryCoverage Coverage() => new() { Complete = true, TabsDiscovered = 1, TabsLoaded = 1, Limitations = ["Pièces jointes référencées sans contenu binaire."] };
    internal static TicketDetailResult Detail(TicketLibrarySeed seed, string text = "Solution intacte\u0092 <b>native</b>") => new()
    {
        Ticket = seed.Ticket, Title = "TitreFicheExclusif", ReadAt = At().AddMinutes(5), Tabs = [new() { Key = "treatment", Name = "Traitement", Loaded = true }],
        Sections = [
            new() { Name = "Chronologie", Kind = "timeline", TabKey = "treatment", Data = new() { Headers = ["Type", "Date", "Auteur", "Contenu", "Informations"], Rows = [["Solution", "2026-11-01", "AuteurLigne", text, "Acceptée\nPrivé"]], Complete = true, ExtractedAt = At().AddMinutes(5) } },
            new() { Name = "Autres champs", TabKey = "fields", Data = new() { Headers = ["Champ", "Valeur"], Rows = [[""], ["ValeurCourte", "", "SansEntête"]], Complete = true, ExtractedAt = At().AddMinutes(5) } },
            new() { Name = "Section vide observée", TabKey = "empty", Data = new() { Headers = ["Nom"], Complete = true, ExtractedAt = At().AddMinutes(5) } }
        ],
        ObservedItems = [new() { Kind = "followup", Identity = "followup-88", SourceTabKey = "treatment", SourceSection = "Chronologie", Title = "Intervention prévue", Date = "2026-11-01", Author = "AuteurObservation", Owner = "ÉquipeMatériel", PlannedStart = "2026-11-01 10:00", WaitingReason = "AiguilleObservation", Url = seed.Ticket.Url, ObservedAt = At().AddMinutes(5) }]
    };
}
