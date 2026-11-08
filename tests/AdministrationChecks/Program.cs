using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Text.Json;

var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }
async Task Refuse(Func<Task> action, string text)
{
    try { await action(); } catch (Exception e) when ((e is InvalidOperationException or InvalidDataException) && e.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidOperationException("Expected refusal: " + text);
}
await using (var f = new SessionFixture())
{
    Check(AdministrationCapabilities.Enabled && (await f.Call("library.get")).GetProperty("administration").GetBoolean(), "The Admin build exposes maintenance capabilities from its compiled identity.");
    var path = new LibraryPreferencesStore().Load().DatabasePath; var store = new TicketLibraryStore(path); var team = new TeamWorkspaceStore(path);
    var seed = new TicketLibrarySeed { Ticket = new("42", "https://synthetic.invalid/front/ticket.form.php?id=42"), Title = "Ticket fictif" };
    await store.ObserveSeedAsync(seed);
    await team.SaveAsync(new TeamWorkspaceItem { Id = Guid.NewGuid().ToString("N"), Title = "Note commune fictive", Body = "À conserver", Kind = "note" }, null);
    var personal = Path.Combine(f.Root, "workplace.json"); await File.WriteAllTextAsync(personal, "personal-sentinel");
    var preview = await f.Call("admin.local.preview", new { includeTeam = false });
    Check(preview.GetProperty("tickets").GetInt64() == 1 && preview.GetProperty("teamItems").GetInt64() == 1, "The reset preview reports actual tickets, shared items and pending work before deletion.");
    seed.Title = "Modification concurrente"; await store.ObserveSeedAsync(seed);
    await Refuse(() => f.Call("admin.local.reset", new { token = preview.GetProperty("token").GetString(), confirmed = true }), "changé");
    Check((await store.GetStateAsync()).TicketCount == 1, "A stale confirmation cannot erase a database changed after its preview.");
    preview = await f.Call("admin.local.preview", new { includeTeam = false });
    var result = await f.Call("admin.local.reset", new { token = preview.GetProperty("token").GetString(), confirmed = true });
    var backup = result.GetProperty("backupPath").GetString()!;
    Check((await new TicketLibraryStore(path).GetStateAsync()).TicketCount == 0 && (await new TicketLibraryStore(backup).GetStateAsync()).TicketCount == 1
        && UpdateService.HashFile(backup) == result.GetProperty("backupSha256").GetString(), "Reset clears tickets only after preserving a readable SQLite backup and its exact hash.");
    Check((await f.Call("library.get")).GetProperty("team").GetProperty("items").GetInt64() == 1 && await File.ReadAllTextAsync(personal) == "personal-sentinel"
        && !new LibraryPreferencesStore().Load().AutoSync, "Ticket-only reset preserves team data and personal files and pauses automatic synchronization.");
    preview = await f.Call("admin.local.preview", new { includeTeam = true });
    await f.Call("admin.local.reset", new { token = preview.GetProperty("token").GetString(), confirmed = true });
    Check((await f.Call("library.get")).GetProperty("team").GetProperty("items").GetInt64() == 0 && await File.ReadAllTextAsync(personal) == "personal-sentinel", "Whole-database reset clears the team tables without deleting unrelated personal files.");
    await Refuse(() => f.Call("admin.local.reset", new { token = preview.GetProperty("token").GetString(), confirmed = true }), "bilan");
    Check(true, "A reset token cannot be replayed after a completed operation.");
    await f.Call("app.demo"); await Refuse(() => f.Call("admin.postgres.prepare"), "démonstration");
    Check(true, "Even the Admin demonstration refuses provisioning a real PostgreSQL server.");
}
if (args.Contains("--postgres"))
{
    var admin = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_POSTGRES") ?? throw new InvalidOperationException("GLPI_PILOT_TEST_POSTGRES is required.");
    var name = "glpi_pilot_admin_checks_" + Guid.NewGuid().ToString("N")[..16]; var account = name + "_agent"; var ownedDatabase = name + "_owned";
    var secret = Guid.NewGuid().ToString("N") + "'synthetic"; var q = new NpgsqlCommandBuilder();
    try
    {
        var created = await PostgresDatabaseProvisioning.PrepareAsync(admin, name, account, secret);
        var config = new NpgsqlConnectionStringBuilder(admin) { Database = name, Username = account, Password = secret, Pooling = false, SearchPath = "public" };
        ITicketLibraryReplica replica = new TicketLibraryPostgresStore(config.ToString(), provisionSchema: false);
        var identity = await replica.InitializeAsync(); var teamReplica = new PostgresTeamWorkspaceReplica(config.ToString(), provisionSchema: false); var teamIdentity = await teamReplica.InitializeAsync();
        Check(created.DatabaseCreated && created.AccountCreated && identity.Length == 32 && teamIdentity.Length == 32, "The Admin provisions a missing database, a dedicated technical account and both schemas on real PostgreSQL.");
        var repeated = await PostgresDatabaseProvisioning.PrepareAsync(admin, name, account, "unused-new-secret");
        Check(!repeated.DatabaseCreated && !repeated.AccountCreated && await new TicketLibraryPostgresStore(config.ToString(), provisionSchema: false).InitializeAsync() == identity,
            "Repeating provisioning preserves the database identity, existing account and its password.");
        await using var db = new NpgsqlConnection(config.ToString()); await db.OpenAsync();
        foreach (var sql in new[] { "CREATE DATABASE " + q.QuoteIdentifier(name + "_forbidden"), "DROP TABLE public.gp_shared_items", "ALTER TABLE public.gp_shared_items ADD COLUMN forbidden text" })
        {
            try { await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(); throw new InvalidOperationException("Unexpected administrative permission"); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }
        }
        Check(true, "The technical account cannot create databases, drop tables or alter their schema.");
        var note = new TeamWorkspaceItem { Id = Guid.NewGuid().ToString("N"), Kind = "note", Title = "CRUD technique", Body = "Fictif" };
        var teamReceipt = await teamReplica.PushAsync(new TeamWorkspaceMutation(Guid.NewGuid().ToString("N"), 0, note));
        var ticket = TicketLibraryRules.FromSeed(new TicketLibrarySeed { Ticket = new("43", "https://synthetic.invalid/front/ticket.form.php?id=43"), Title = "CRUD technique fictif" });
        var ticketReceipt = await replica.PushAsync(new TicketLibraryMutation(Guid.NewGuid().ToString("N"), ticket), CancellationToken.None);
        Check(teamReceipt.Outcome == "accepted" && (await teamReplica.ChangesAsync(0, 100)).Count == 1 && ticketReceipt.Revision > 0
            && (await replica.PullAsync(0, 100, CancellationToken.None)).Count == 1, "The restricted technical account actually exchanges tickets and team notes, including the central sequence.");
        await using var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false }.ToString()); await owner.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, owner); await command.ExecuteNonQueryAsync(); }
        await Sql("ALTER ROLE " + q.QuoteIdentifier(account) + " CREATEDB");
        await Refuse(() => PostgresDatabaseProvisioning.PrepareAsync(admin, name, account, secret), "privilèges");
        await Sql("ALTER ROLE " + q.QuoteIdentifier(account) + " NOCREATEDB");
        Check(true, "A pre-existing role with database administration rights is refused without silently downgrading it.");
        foreach (var (grant, revoke) in new[] {
            ("GRANT CREATE ON SCHEMA public TO " + q.QuoteIdentifier(account), "REVOKE CREATE ON SCHEMA public FROM " + q.QuoteIdentifier(account)),
            ("GRANT CREATE ON DATABASE " + q.QuoteIdentifier(name) + " TO " + q.QuoteIdentifier(account), "REVOKE CREATE ON DATABASE " + q.QuoteIdentifier(name) + " FROM " + q.QuoteIdentifier(account)),
            ("GRANT CREATE ON SCHEMA public TO PUBLIC", "REVOKE CREATE ON SCHEMA public FROM PUBLIC") })
        {
            await Sql(grant); await Refuse(() => PostgresDatabaseProvisioning.PrepareAsync(admin, name, account, secret), "CREATE"); await Sql(revoke);
        }
        Check(true, "Database, schema and inherited PUBLIC creation privileges are refused before schema preparation; no existing grant is revoked by the application.");
        await Sql("GRANT TRUNCATE ON public.gp_shared_items TO " + q.QuoteIdentifier(account));
        await Refuse(() => PostgresDatabaseProvisioning.PrepareAsync(admin, name, account, secret), "gestion");
        await Sql("REVOKE TRUNCATE ON public.gp_shared_items FROM " + q.QuoteIdentifier(account));
        Check(true, "Existing destructive or delegation privileges on business tables are refused rather than disguised as an ordinary agent account.");
        await Sql("CREATE TABLE public.admin_preflight_sentinel(value text)");
        await Sql("ALTER TABLE public.admin_preflight_sentinel OWNER TO " + q.QuoteIdentifier(account));
        await Refuse(() => PostgresDatabaseProvisioning.PrepareAsync(admin, name, account, secret), "possède");
        await Sql("DROP TABLE public.admin_preflight_sentinel");
        Check(true, "An agent owning existing public objects is refused while the existing central identities and business rows remain intact.");
        await Sql("CREATE DATABASE " + q.QuoteIdentifier(ownedDatabase) + " OWNER " + q.QuoteIdentifier(account) + " TEMPLATE template0");
        await Refuse(() => PostgresDatabaseProvisioning.PrepareAsync(admin, ownedDatabase, account, secret), "CREATE");
        await using (var unprepared = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Database = ownedDatabase, Pooling = false }.ToString()))
        {
            await unprepared.OpenAsync();
            await using var absent = new NpgsqlCommand("SELECT to_regclass('public.glpi_pilot_library_schema') IS NULL AND to_regclass('public.gp_shared_metadata') IS NULL", unprepared);
            Check((bool)(await absent.ExecuteScalarAsync())!, "Selecting an agent-owned blank database is refused before either application schema is created.");
        }
        Check(await new TicketLibraryPostgresStore(config.ToString(), provisionSchema: false).InitializeAsync() == identity
            && (await teamReplica.ChangesAsync(0, 100)).Count == 1 && (await replica.PullAsync(0, 100, CancellationToken.None)).Count == 1,
            "Every refused preparation preserves the tickets, team notes and central identity.");
    }
    finally
    {
        await using var cleanup = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Pooling = false }.ToString()); await cleanup.OpenAsync();
        await using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + q.QuoteIdentifier(ownedDatabase) + " WITH (FORCE)", cleanup)) await drop.ExecuteNonQueryAsync();
        await using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + q.QuoteIdentifier(name) + " WITH (FORCE)", cleanup)) await drop.ExecuteNonQueryAsync();
        await using (var drop = new NpgsqlCommand("DROP ROLE IF EXISTS " + q.QuoteIdentifier(account), cleanup)) await drop.ExecuteNonQueryAsync();
    }
}
Console.WriteLine($"{passed}/{passed} administration checks passed.");
