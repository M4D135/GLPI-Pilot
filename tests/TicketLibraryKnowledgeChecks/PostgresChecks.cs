using System.Globalization;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;
using Npgsql;

internal static class KnowledgePostgresChecks
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> check)
    {
        var connectionString = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await check("PostgreSQL: explicit corpus tests require an isolated server", () => throw new InvalidOperationException("GLPI_PILOT_TEST_POSTGRES is required with --postgres."));
            return;
        }
        var schema = "knowledge_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false };
        await using var maintenance = new NpgsqlConnection(connectionString); await maintenance.OpenAsync();
        await Execute(maintenance, "CREATE SCHEMA " + schema);
        try
        {
            var central = new TicketLibraryPostgresStore(builder.ToString());
            var local = new TicketLibraryStore(Path.Combine(root, "postgres-knowledge.db"));
            var seed = KnowledgeFixture.Seed();
            await local.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed), KnowledgeFixture.Coverage(), "knowledge-reader");
            await TicketLibrarySyncService.SynchronizeAsync(local, central);
            await using var postgres = new NpgsqlConnection(builder.ToString()); await postgres.OpenAsync();
            await using var sqlite = new SqliteConnection(local.ConnectionString); await sqlite.OpenAsync();

            await check("PostgreSQL: normalized scalar fields, rows, items and citations match SQLite after real synchronization", async () =>
            {
                (string View, string Columns, string Order)[] projections = [
                    ("tickets", "ticket_key,ticket_id,ticket_url,title,content_hash,content_confirmed,detail_complete,access_basis,visibility", "ticket_key"),
                    ("fields", "source_path,column_index,name,value", "source_path"),
                    ("sections", "section_index,name,kind,tab_key,complete,row_count,column_count", "section_index"),
                    ("headers", "section_index,column_index,name", "section_index,column_index"),
                    ("rows", "section_index,row_index,source_path,visibility,body", "section_index,row_index"),
                    ("cells", "section_index,row_index,column_index,name,value", "section_index,row_index,column_index"),
                    ("items", "item_index,identity,title,author,owner,planned_start,waiting_reason,url,body", "item_index"),
                    ("tabs", "tab_index,tab_key,name,category,loaded", "tab_index"),
                    ("limitations", "origin,section_index,ordinal,body", "origin,section_index,ordinal"),
                    ("texts", "citation_key,source_path,source_kind,section,section_key,row_index,text_visibility,body", "citation_key")
                ];
                foreach (var projection in projections)
                {
                    var suffix = " WHERE version_kind='selected' ORDER BY " + projection.Order;
                    var expected = await Read(sqlite, "SELECT " + projection.Columns + " FROM " + TicketLibraryKnowledge.SqlitePrefix + projection.View + suffix);
                    var actual = await Read(postgres, "SELECT " + projection.Columns + " FROM " + TicketLibraryKnowledge.PostgresPrefix + projection.View + suffix);
                    Require(expected.Count == actual.Count && expected.Zip(actual).All(pair => pair.First.SequenceEqual(pair.Second)), projection.View);
                }
            });
            await check("PostgreSQL: clients can use the provisioned corpus with schema provisioning disabled", async () =>
            {
                var before = await Read(postgres, "SELECT ticket_key,document_json,revision FROM glpi_pilot_library_tickets ORDER BY ticket_key");
                var normal = new TicketLibraryPostgresStore(builder.ToString(), provisionSchema: false);
                Require(await normal.InitializeAsync() == await central.InitializeAsync(), "central identity");
                var recipient = new TicketLibraryStore(Path.Combine(root, "postgres-knowledge-recipient.db"));
                await TicketLibrarySyncService.SynchronizeAsync(recipient, normal);
                Require((await recipient.SearchAsync(new() { Text = "AiguilleObservation" })).Total == 1, "received FTS");
                var found = await Read(postgres, "SELECT ticket_id,source_path FROM " + TicketLibraryKnowledge.PostgresPrefix + "texts WHERE version_kind='selected' AND content_confirmed=1 AND detail_complete=1 AND body LIKE '%AiguilleObservation%'");
                Require(found.Count == 1 && found[0][0] == seed.Ticket.Id && found[0][1] == "/detail/observedItems/0", "queryable item citation");
                var after = await Read(postgres, "SELECT ticket_key,document_json,revision FROM glpi_pilot_library_tickets ORDER BY ticket_key");
                Require(before.Count == after.Count && before.Zip(after).All(pair => pair.First.SequenceEqual(pair.Second)), "read-only source snapshots");
            });
            await check("PostgreSQL: archived conflicting versions remain separate and never pass the current confirmed filter", async () =>
            {
                await local.SaveDetailAsync(seed, KnowledgeFixture.Detail(seed, "ConflitCentralConservé"), KnowledgeFixture.Coverage(), "knowledge-reader");
                await TicketLibrarySyncService.SynchronizeAsync(local, central);
                var result = await Read(postgres, "SELECT version_kind,body,content_confirmed FROM " + TicketLibraryKnowledge.PostgresPrefix + "texts WHERE source_path='/detail/sections/0/data/rows/0' ORDER BY version_id");
                Require(result.Any(row => row[0] == "conflict-incoming" && row[1]!.Contains("ConflitCentralConservé", StringComparison.Ordinal)) && result.All(row => row[2] == "0"), "archived conflicts");
                Require((await Read(postgres, "SELECT body FROM " + TicketLibraryKnowledge.PostgresPrefix + "texts WHERE version_kind='selected' AND content_confirmed=1 AND detail_complete=1")).Count == 0, "confirmed filter");
            });
        }
        finally { await Execute(maintenance, "DROP SCHEMA " + schema + " CASCADE"); }
    }

    private static async Task<List<string?[]>> Read(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql; await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray());
        return rows;
    }
    private static async Task Execute(NpgsqlConnection connection, string sql) { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
    private static void Require(bool result, string context) { if (!result) throw new InvalidOperationException("Assertion failed: " + context); }
}
