using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-RelaisChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
var passed = 0; var total = 0;
try
{
    if (args.Length == 4 && args[0] == "--export-source")
    {
        var source = new TicketLibraryStore(args[1]);
        var exported = await source.ExportRelaisAsync(args[3], args[2], new RelaisInlineProgress(value => Console.Error.WriteLine(JsonSerializer.Serialize(value))));
        Console.WriteLine(JsonSerializer.Serialize(exported)); return 0;
    }
    await Check("Native contract exports only complete confirmed conflict-free tickets from one installation", async () =>
    {
        var store = await RelaisFixture.Mixed(Path.Combine(root, "quality.db"));
        var preview = await store.PreviewRelaisExportAsync(RelaisFixture.Installation);
        Require(preview.SourceTicketCount == 7 && preview.EligibleTicketCount == 2 && preview.ExcludedTicketCount == 5
            && preview.MissingContentCount == 1 && preview.IncompleteCount == 2 && preview.UnconfirmedCount == 4 && preview.PendingCount == 1 && preview.ConflictCount == 1, "Native quality counts");
        var result = await store.ExportRelaisAsync(Path.Combine(root, "quality.relais"), RelaisFixture.Installation);
        Require(result.TicketCount == 2 && result.TextCount > 2 && result.SourceTicketCount == 7 && result.ExcludedTicketCount == 5, "Export counts");
        var manifest = ReadRows(result.Path, "SELECT * FROM relais_manifest").Single();
        Require((string)manifest["protocol"]! == "glpi-pilot-relais" && (string)manifest["scope"]! == "installation-admissible"
            && (long)manifest["complete"]! == 1 && (long)manifest["source_ticket_count"]! == 7 && (long)manifest["conflict_count"]! == 1, "Manifest scope/counts");
        var ids = ReadRows(result.Path, "SELECT DISTINCT ticket_id FROM relais_texts ORDER BY ticket_id").Select(row => (string)row["ticket_id"]!).ToArray();
        Require(ids.SequenceEqual(["2", RelaisFixture.LargeId]), "Ticket selection");
        Require(ReadRows(result.Path, "SELECT DISTINCT installation FROM relais_texts").Single()["installation"] as string == RelaisFixture.Installation, "Installation isolation");
    });

    await Check("Texts citations privacy NULLs Unicode and 64-bit identities preserve the actual SQL projections", async () =>
    {
        var store = await RelaisFixture.Mixed(Path.Combine(root, "exact.db"));
        var result = await store.ExportRelaisAsync(Path.Combine(root, "exact.relais"), RelaisFixture.Installation);
        var columns = string.Join(',', RelaisExportFormat.TextColumns);
        var expected = ReadRows(store.DatabasePath, "SELECT " + columns + " FROM ticket_library_knowledge_v1_texts WHERE installation=$installation AND version_id='selected' AND version_kind='selected' AND detail_complete=1 AND content_confirmed=1 AND confirmation_pending=0 AND has_content=1 AND conflict_count=0 AND body<>'' ORDER BY citation_key",
            ("$installation", RelaisFixture.Installation));
        foreach (var row in expected)
            foreach (var name in new[] { "local_revision", "central_revision" }) if (row[name] is long value) row[name] = value.ToString(CultureInfo.InvariantCulture);
        var actual = ReadRows(result.Path, "SELECT " + columns + " FROM ticket_library_knowledge_v1_texts ORDER BY citation_key");
        Require(JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(actual), "Source projection fidelity");
        var large = actual.First(row => row["ticket_id"] as string == RelaisFixture.LargeId);
        Require(large["local_revision"] as string == "9007199254740993" && large["central_revision"] as string == "9223372036854775807", "Exact revisions");
        Require(actual.Any(row => row["text_visibility"] as string == "private") && actual.Any(row => row["text_visibility"] as string == "unknown")
            && actual.All(row => row["text_visibility"] as string != "public"), "Observed privacy");
        Require(actual.Any(row => row["source_url"] is null) && actual.Any(row => (row["source_path"] as string)?.Contains("clé~1avec~0séparateur", StringComparison.Ordinal) == true)
            && actual.Any(row => (row["body"] as string)?.Contains("😀", StringComparison.Ordinal) == true), "Nullable/Unicode metadata");
    });

    await Check("Read-only export leaves source bytes schema proofs outbox and unfinished knowledge migration untouched", async () =>
    {
        var store = await RelaisFixture.Ready(Path.Combine(root, "readonly.db"));
        using (var connection = Writable(store.DatabasePath))
        {
            Execute(connection, "DELETE FROM ticket_library_knowledge_index; CREATE TABLE private_profile(secret TEXT); INSERT INTO private_profile VALUES('SYNTHETIC_SESSION_SECRET_4629'); CREATE TABLE team_workspace_private(body TEXT); INSERT INTO team_workspace_private VALUES('SYNTHETIC_TEAM_CONTENT_9981');");
        }
        var before = HashFile(store.DatabasePath);
        var fresh = new TicketLibraryStore(store.DatabasePath); var preview = await fresh.PreviewRelaisExportAsync(RelaisFixture.Installation);
        var result = await fresh.ExportRelaisAsync(Path.Combine(root, "readonly.relais"), RelaisFixture.Installation);
        Require(preview.CanExport && HashFile(store.DatabasePath) == before, "Source bytes unchanged");
        Require((long)ReadRows(store.DatabasePath, "SELECT COUNT(*) AS n FROM ticket_library_knowledge_index").Single()["n"]! == 0, "No migration");
        var objects = ReadRows(result.Path, "SELECT type,name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY name");
        Require(objects.Where(row => row["type"] as string == "table").Select(row => (string)row["name"]!).Order().SequenceEqual(new[] { "relais_manifest", "relais_texts", "relais_tickets", "ticket_library_schema" }.Order()), "Allowlisted tables");
        Require(objects.Count(row => row["type"] as string == "view") == 2 && !objects.Any(row => row["type"] as string == "trigger"), "Only public views and no trigger");
        var bytes = File.ReadAllBytes(result.Path); var decoded = Encoding.UTF8.GetString(bytes);
        Require(!decoded.Contains("SYNTHETIC_SESSION_SECRET_4629", StringComparison.Ordinal) && !decoded.Contains("SYNTHETIC_TEAM_CONTENT_9981", StringComparison.Ordinal)
            && !decoded.Contains("document_json", StringComparison.Ordinal) && !decoded.Contains("ticket_library_outbox", StringComparison.Ordinal), "No operational data");
    });

    await Check("A WAL snapshot includes committed frames and excludes changes committed after its counts", async () =>
    {
        var store = await RelaisFixture.Ready(Path.Combine(root, "wal.db"), "1");
        using var keeper = Writable(store.DatabasePath); Execute(keeper, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        using var baseline = keeper.BeginTransaction(deferred: true);
        using (var pin = keeper.CreateCommand()) { pin.Transaction = baseline; pin.CommandText = "SELECT COUNT(*) FROM ticket_library_tickets"; _ = pin.ExecuteScalar(); }
        var beforeSeed = RelaisFixture.Seed("2");
        await store.SaveDetailAsync(beforeSeed, RelaisFixture.Detail(beforeSeed, "BEFORE_SNAPSHOT_COMMITTED_WAL"), RelaisFixture.Coverage(), "relais-synthetic-reader");
        Require(File.Exists(store.DatabasePath + "-wal") && new FileInfo(store.DatabasePath + "-wal").Length > 32, "Real WAL frames");
        var isolated = Path.Combine(root, "only-main-file.db"); File.Copy(store.DatabasePath, isolated);
        Require((long)ReadRows(isolated, "SELECT COUNT(*) AS n FROM ticket_library_tickets").Single()["n"]! == 1, "Committed ticket is not in a raw main-file copy");
        var changed = false;
        var progress = new RelaisInlineProgress(value =>
        {
            if (value.Phase != "snapshot" || changed) return; changed = true;
            var added = RelaisFixture.Seed("3"); store.SaveDetailAsync(added, RelaisFixture.Detail(added, "AFTER_SNAPSHOT_NEW_TICKET"), RelaisFixture.Coverage(), "relais-synthetic-reader").GetAwaiter().GetResult();
            var updated = RelaisFixture.Seed("2"); updated.LastModifiedRaw = "2026-11-20T11:00:00Z"; updated.LastModifiedAt = RelaisFixture.At.AddHours(2);
            store.SaveDetailAsync(updated, RelaisFixture.Detail(updated, "AFTER_SNAPSHOT_CHANGED_BODY"), RelaisFixture.Coverage(), "relais-synthetic-reader").GetAwaiter().GetResult();
        });
        var result = await store.ExportRelaisAsync(Path.Combine(root, "wal.relais"), RelaisFixture.Installation, progress);
        Require(changed && result.SourceTicketCount == 2 && result.TicketCount == 2, "Single snapshot counts");
        var texts = string.Join('\n', ReadRows(result.Path, "SELECT body FROM relais_texts").Select(row => (string)row["body"]!));
        Require(texts.Contains("BEFORE_SNAPSHOT_COMMITTED_WAL", StringComparison.Ordinal) && !texts.Contains("AFTER_SNAPSHOT", StringComparison.Ordinal), "Consistent snapshot bodies");
        Require((await store.PreviewRelaisExportAsync(RelaisFixture.Installation)).SourceTicketCount == 3, "Concurrent writer committed");
    });

    await Check("The delivered file is autonomous and its digest and explicit column types remain verifiable", async () =>
    {
        var store = await RelaisFixture.Ready(Path.Combine(root, "portable.db"));
        var result = await store.ExportRelaisAsync(Path.Combine(root, "portable.relais"), RelaisFixture.Installation);
        Require(result.FileBytes == new FileInfo(result.Path).Length && result.Sha256 == HashFile(result.Path), "File digest");
        var elsewhere = Path.Combine(root, "elsewhere"); Directory.CreateDirectory(elsewhere); var moved = Path.Combine(elsewhere, "only-file.relais"); File.Copy(result.Path, moved);
        File.Delete(result.Path); File.Delete(store.DatabasePath);
        Require((long)ReadRows(moved, "SELECT COUNT(*) AS n FROM ticket_library_knowledge_v1_texts").Single()["n"]! == result.TextCount, "Standalone file");
        Require(ReadRows(moved, "PRAGMA journal_mode").Single().Values.Single() as string == "delete", "No WAL mode");
        var types = ReadRows(moved, "PRAGMA table_info(relais_texts)").ToDictionary(row => (string)row["name"]!, row => (string)row["type"]!);
        Require(RelaisExportFormat.TextColumns.All(name => types[name] == (RelaisExportFormat.IntegerColumns.Contains(name) ? "INTEGER" : "TEXT")), "Explicit column types");
        Require(Directory.GetFiles(elsewhere).Length == 1, "No sidecar needed");
    });

    await Check("Empty nonexistent and inadmissible installations produce no file and no new source database", async () =>
    {
        var missing = new TicketLibraryStore(Path.Combine(root, "does-not-exist.db"));
        Require(!(await missing.PreviewRelaisExportAsync(RelaisFixture.Installation)).CanExport && !File.Exists(missing.DatabasePath), "Absent source preview");
        var path = Path.Combine(root, "none.relais"); await Refuse<InvalidOperationException>(() => missing.ExportRelaisAsync(path, RelaisFixture.Installation));
        Require(!File.Exists(path) && !File.Exists(missing.DatabasePath), "Absent source export");
        var store = new TicketLibraryStore(Path.Combine(root, "index-only.db")); await store.ObserveSeedAsync(RelaisFixture.Seed());
        await Refuse<InvalidOperationException>(() => store.ExportRelaisAsync(path, RelaisFixture.Installation)); Require(!File.Exists(path), "No eligible content");
        await Refuse<InvalidOperationException>(() => store.ExportRelaisAsync(path, "https://not-collected.example/glpi")); Require(!File.Exists(path), "No installation content");
    });

    await Check("Existing files source journals and destination sidecars are refused without modifying them", async () =>
    {
        var store = await RelaisFixture.Ready(Path.Combine(root, "destinations.db"));
        var sourceHash = HashFile(store.DatabasePath);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            await Refuse<InvalidDataException>(() => store.ExportRelaisAsync(store.DatabasePath + suffix, RelaisFixture.Installation));
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var destination = Path.Combine(root, "existing-" + (suffix.Length == 0 ? "main" : suffix[1..]) + ".relais"); var existing = destination + suffix;
            File.WriteAllText(existing, "PREEXISTING_FILE_MUST_SURVIVE", Encoding.UTF8); var before = HashFile(existing);
            await Refuse<InvalidDataException>(() => store.ExportRelaisAsync(destination, RelaisFixture.Installation));
            Require(HashFile(existing) == before && (suffix.Length == 0 || !File.Exists(destination)), "Preexisting destination file");
        }
        if (!OperatingSystem.IsWindows())
        {
            var destination = Path.Combine(root, "dangling-sidecar.relais"); var sidecar = destination + "-wal";
            File.CreateSymbolicLink(sidecar, Path.Combine(root, "absent-target"));
            await Refuse<InvalidDataException>(() => store.ExportRelaisAsync(destination, RelaisFixture.Installation));
            Require(!File.Exists(destination) && new FileInfo(sidecar).LinkTarget is not null, "Dangling sidecar preserved");
        }
        Require(HashFile(store.DatabasePath) == sourceHash, "Active source preserved");
    });

    await Check("A destination or sidecar appearing during transfer wins without overwrite or cleanup of its bytes", async () =>
    {
        var store = await RelaisFixture.Ready(Path.Combine(root, "race.db"));
        foreach (var suffix in new[] { "", "-wal" })
        {
            var destination = Path.Combine(root, "race" + (suffix.Length == 0 ? "main" : "wal") + ".relais"); var raced = destination + suffix;
            var progress = new RelaisInlineProgress(value => { if (value.Phase == "transfer") File.WriteAllText(raced, "RACING_OWNER_BYTES"); });
            await Refuse<InvalidDataException>(() => store.ExportRelaisAsync(destination, RelaisFixture.Installation, progress));
            Require(File.ReadAllText(raced) == "RACING_OWNER_BYTES" && (suffix.Length == 0 || !File.Exists(destination)), "Concurrent destination preserved");
            Require(!Directory.GetFiles(root, ".glpi-relais-*.tmp").Any(), "Transfer temporary removed");
        }
    });

    await Check("Cancellation before copying during row writes and after validation leaves no published or staged artifact", async () =>
    {
        var store = await RelaisFixture.Ready(Path.Combine(root, "cancel.db")); var before = HashFile(store.DatabasePath);
        foreach (var phase in new[] { "snapshot", "export", "validate", "transfer" })
        {
            using var cancellation = new CancellationTokenSource(); var destination = Path.Combine(root, "cancel-" + phase + ".relais");
            var temporaries = Directory.GetFiles(Path.GetTempPath(), "GLPI-Pilot-Relais-*.sqlite*").ToHashSet(StringComparer.Ordinal);
            await Refuse<OperationCanceledException>(() => store.ExportRelaisAsync(destination, RelaisFixture.Installation,
                new RelaisInlineProgress(value => { if (value.Phase == phase) cancellation.Cancel(); }), cancellation.Token));
            Require(!File.Exists(destination) && !Directory.GetFiles(root, ".glpi-relais-*.tmp").Any(), "No cancelled publication");
            Require(!Directory.GetFiles(Path.GetTempPath(), "GLPI-Pilot-Relais-*.sqlite*").Except(temporaries, StringComparer.Ordinal).Any(), "Owned local SQLite removed");
        }
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        await Refuse<OperationCanceledException>(() => store.ExportRelaisAsync(Path.Combine(root, "pre-cancel.relais"), RelaisFixture.Installation, ct: alreadyCancelled.Token));
        Require(HashFile(store.DatabasePath) == before, "Cancellation is read-only");
    });

    await Check("Fragment JSON body and file ceilings fail the entire export without truncation", async () =>
    {
        var store = await RelaisFixture.Ready(Path.Combine(root, "limits.db")); var defaults = RelaisExportLimits.Default;
        var limits = new[] { defaults with { Texts = 1 }, defaults with { JsonBytes = 100 }, defaults with { BodyBytes = 16 }, defaults with { FileBytes = 4096 } };
        for (var i = 0; i < limits.Length; i++)
        {
            var destination = Path.Combine(root, "limit-" + i.ToString(CultureInfo.InvariantCulture) + ".relais");
            await Refuse<InvalidDataException>(() => store.ExportRelaisWithLimitsAsync(destination, RelaisFixture.Installation, limits[i]));
            Require(!File.Exists(destination) && !Directory.GetFiles(root, ".glpi-relais-*.tmp").Any(), "No truncated publication");
        }
        await Refuse<ArgumentOutOfRangeException>(() => store.ExportRelaisWithLimitsAsync(Path.Combine(root, "raised.relais"), RelaisFixture.Installation, defaults with { Texts = 200_001 }));
        Require(RelaisExportFormat.MaximumTexts == 200_000 && RelaisExportFormat.MaximumJsonBytes == 256L * 1024 * 1024
            && RelaisExportFormat.MaximumBodyBytes == 2L * 1024 * 1024 && RelaisExportFormat.MaximumFileBytes == 512L * 1024 * 1024, "Public protocol ceilings");
    });

    await Check("The real two-MiB UTF-8 body limit refuses a native row assembled from individually legal cells", async () =>
    {
        var store = new TicketLibraryStore(Path.Combine(root, "large-body.db")); var seed = RelaisFixture.Seed(); var detail = RelaisFixture.Detail(seed);
        detail.Sections = [new() { Name = "Grande ligne synthétique", TabKey = "treatment", Data = new()
            { Headers = ["Un", "Deux", "Trois", "Quatre", "Cinq"], Rows = [[new('x', 450_000), new('x', 450_000), new('x', 450_000), new('x', 450_000), new('x', 450_000)]], Complete = true } }];
        await store.SaveDetailAsync(seed, detail, RelaisFixture.Coverage(), "relais-synthetic-reader");
        var destination = Path.Combine(root, "large-body.relais");
        await Refuse<InvalidDataException>(() => store.ExportRelaisAsync(destination, RelaisFixture.Installation)); Require(!File.Exists(destination), "No oversized body published");
    });

    await Check("Sensitive URL metadata is refused without exposing its value or rewriting citations", async () =>
    {
        const string marker = "SYNTHETIC_URL_SECRET_DO_NOT_PRINT_771";
        foreach (var mode in new[] { "ticket-token", "source-userinfo", "source-encoded-token", "source-fragment", "source-space", "source-control", "source-too-long" })
        {
            var store = new TicketLibraryStore(Path.Combine(root, mode + ".db")); var seed = RelaisFixture.Seed();
            if (mode == "ticket-token") seed.Ticket = new(seed.Ticket.Id, seed.Ticket.Url + "&access_token=" + marker);
            var detail = RelaisFixture.Detail(seed);
            if (mode == "source-userinfo") detail.Sections[0].Data.SourceUrl = "https://fixture:" + marker + "@relais-fixture.example/glpi/front/ticket.form.php?id=1";
            if (mode == "source-encoded-token") detail.Sections[0].Data.SourceUrl = seed.Ticket.Url + "&%61ccess%5ftoken=" + marker;
            if (mode == "source-fragment") detail.Sections[0].Data.SourceUrl = seed.Ticket.Url + "#access_token=" + marker;
            if (mode == "source-space") detail.Sections[0].Data.SourceUrl = seed.Ticket.Url + "&label=raw space";
            if (mode == "source-control") detail.Sections[0].Data.SourceUrl = seed.Ticket.Url + "&label=raw\tcontrol";
            if (mode == "source-too-long") detail.Sections[0].Data.SourceUrl = seed.Ticket.Url + "&label=" + new string('a', 32768);
            await store.SaveDetailAsync(seed, detail, RelaisFixture.Coverage(), "relais-synthetic-reader"); var before = HashFile(store.DatabasePath);
            var destination = Path.Combine(root, mode + ".relais"); var error = await Refuse<InvalidDataException>(() => store.ExportRelaisAsync(destination, RelaisFixture.Installation));
            Require(!error.Message.Contains(marker, StringComparison.Ordinal) && !File.Exists(destination) && HashFile(store.DatabasePath) == before, "Safe URL rejection");
        }
    });

    if (args.Length != 0 && !(args.Length == 2 && args[0] == "--fixture"))
        throw new ArgumentException("Use --fixture <directory> or --export-source <sqlite> <installation> <destination>.");
    if (passed == total && args.Length == 2)
    {
        var store = await RelaisFixture.Mixed(Path.Combine(root, "fixture.db"));
        var result = await store.ExportRelaisAsync(Path.Combine(root, "fixture.relais"), RelaisFixture.Installation);
        RelaisFixtureWriter.Write(result, args[1]); Console.WriteLine("Synthetic C# export fixture written.");
    }
    Console.WriteLine($"{passed}/{total} Relais export checks passed."); return passed == total ? 0 : 1;
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }

async Task Check(string name, Func<Task> run)
{
    total++; try { await run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + " · " + error); }
}
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static async Task<T> Refuse<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new InvalidOperationException("Expected refusal: " + typeof(T).Name);
}
static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
static SqliteConnection Writable(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); connection.Open(); return connection;
}
static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
static List<Dictionary<string, object?>> ReadRows(string path, string sql, params (string Name, object Value)[] parameters)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); connection.Open();
    using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
    using var reader = command.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
    while (reader.Read())
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)); rows.Add(row);
    }
    return rows;
}
