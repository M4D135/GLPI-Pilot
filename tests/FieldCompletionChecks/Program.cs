using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Data.Sqlite;
using Npgsql;

var cases = new List<(string Name, Func<Task> Run)>();
void Add(string name, Func<CalendarFixture, Task> test) => cases.Add((name, async () => { await using var f = new CalendarFixture(); await test(f); }));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
async Task Refuse(Func<Task> action)
{
    try { await action(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return; }
    throw new InvalidOperationException("A refusal was required.");
}
FieldPreparationDocument Doc(JsonElement reply) => reply.GetProperty("document").Deserialize<FieldPreparationDocument>(PilotApplication.Json)!;
async Task<JsonElement> Open(CalendarFixture f, string? key = null)
{
    key ??= (await f.Call("calendar.get", f.Range())).GetProperty("occurrences")[0].GetProperty("key").GetString();
    return await f.Call("field.open", new { occurrenceKey = key, revision = f.App.Revision, from = "2026-11-02", to = "2026-11-09", timeZoneId = "Europe/Paris" });
}
async Task<JsonElement> Save(CalendarFixture f, JsonElement opened, bool material = true)
{
    var plan = Doc(opened).Plan;
    if (material) { var cat = opened.GetProperty("catalogue").Deserialize<FieldMaterialCatalogue>(PilotApplication.Json)!; plan = FieldPreparationService.AddReference(plan, cat, cat.References[0].Id, 2); }
    else plan.NoMaterialsRequired = true;
    return await f.Call("field.save", new { token = opened.GetProperty("token").GetString(), plan });
}
object Selection(JsonElement saved) => new { id = saved.GetProperty("id").GetString(), expectedRevision = saved.GetProperty("revision").GetInt64() };
Add("Cancellation removes departure quantities immediately without rewriting saved material", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); var saved = await Save(f, await Open(f));
    entry.State = "cancelled"; await f.Save(entry, entry.Revision);
    var view = await f.Call("field.get", new { includeClosed = true });
    Check(view.GetProperty("summary").GetProperty("planCount").GetInt32() == 0);
    Check(view.GetProperty("rows")[0].GetProperty("readiness").GetString() == "cancelled");
    var persisted = await f.App.CalendarStoreFixture.GetAsync(saved.GetProperty("id").GetString()!);
    Check(persisted!.Revision == saved.GetProperty("revision").GetInt64() && FieldPreparationDocuments.ReadPlan(persisted).Plan.Lines[0].Quantity == 2);
});
Add("Completing a ticket removes its material from departure totals", async f =>
{
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Ticket fictif", "Nouveau", "Agent A", "Support" }));
    var ticket = (await f.Call("calendar.tickets")).GetProperty("rows")[0].GetProperty("key").GetString();
    await Save(f, await f.Call("field.open", new { ticketKey = ticket, revision = f.App.Revision }));
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Ticket fictif", "Clos", "Agent A", "Support" }));
    Check((await f.Call("field.get")).GetProperty("total").GetInt32() == 0);
});
Add("A deleted appointment stays readable but is never included in current departure totals", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); await Save(f, await Open(f));
    await f.App.CalendarStoreFixture.RemoveAsync(entry.Id, entry.Revision);
    var view = await f.Call("field.get"); Check(view.GetProperty("total").GetInt32() == 1);
    Check(view.GetProperty("rows")[0].GetProperty("sourceStatus").GetString() == "missing" && view.GetProperty("rows")[0].GetProperty("excludedFromTotals").GetBoolean());
});
Add("A visit moved months away is resolved by its ordinal identity rather than its former date", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); var saved = await Save(f, await Open(f));
    entry.StartLocal = "2027-05-03T14:00"; entry.EndLocal = "2027-05-03T15:00"; await f.Save(entry, entry.Revision);
    var opened = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() });
    Check(Doc(opened).Plan.PlannedStart!.Value.Year == 2027 && Doc(opened).Plan.Lines.Count == 1 && !opened.GetProperty("snapshotOnly").GetBoolean());
});
Add("A following-series split retains preparation identity and reads the new fragment", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 5 }; var entry = CalendarFixture.Entry(await f.Save(value));
    var rows = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences");
    var saved = await Save(f, await Open(f, rows[3].GetProperty("key").GetString()));
    var following = entry.Copy(); following.StartLocal = "2026-11-04T14:00"; following.EndLocal = "2026-11-04T15:00";
    await f.Call("calendar.save", new { intervention = following, expectedRevision = entry.Revision, scope = "following", originalStartLocal = "2026-11-04T09:00" });
    var opened = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() });
    Check(Doc(opened).Plan.Id == saved.GetProperty("id").GetString() && Doc(opened).Plan.PlannedStart!.Value.Hour == 13);
});
Add("Cancelling one recurring occurrence does not cancel material for the next visit", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 2 }; var entry = CalendarFixture.Entry(await f.Save(value));
    var rows = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences");
    await Save(f, await Open(f, rows[0].GetProperty("key").GetString())); await Save(f, await Open(f, rows[1].GetProperty("key").GetString()));
    var changed = InterventionCalendarService.WithOccurrence(entry, entry.StartLocal, null, true);
    await f.App.CalendarStoreFixture.SaveAsync(CalendarWorkspaceDocuments.ForIntervention(changed, "Test"), entry.Revision);
    var view = await f.Call("field.get"); Check(view.GetProperty("total").GetInt32() == 1 && view.GetProperty("summary").GetProperty("materials")[0].GetProperty("quantity").GetDecimal() == 2);
});
Add("Reopening by ID protects the draft against a later calendar edit", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); var saved = await Save(f, await Open(f));
    var opened = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() });
    entry.Contact = "Nouveau contact fictif"; await f.Save(entry, entry.Revision);
    await Refuse(() => f.Call("field.save", new { token = opened.GetProperty("token").GetString(), plan = Doc(opened).Plan }));
});
Add("A missing ticket is unverified, not presumed deleted or closed", async f =>
{
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Ticket fictif", "Nouveau", "Agent A", "Support" }));
    var key = (await f.Call("calendar.tickets")).GetProperty("rows")[0].GetProperty("key").GetString();
    await Save(f, await f.Call("field.open", new { ticketKey = key, revision = f.App.Revision }));
    f.App.CalendarLoadFixture(CalendarFixture.Data()); var view = await f.Call("field.get");
    Check(view.GetProperty("rows")[0].GetProperty("sourceStatus").GetString() == "unverified" && view.GetProperty("summary").GetProperty("planCount").GetInt32() == 0);
});
Add("Current report generation reflects cancellation and excludes its quantities", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); var saved = await Save(f, await Open(f));
    entry.State = "cancelled"; await f.Save(entry, entry.Revision);
    var html = (await f.Call("field.report", new { selections = new[] { Selection(saved) } })).GetProperty("html").GetString()!;
    Check(html.Contains("annul", StringComparison.OrdinalIgnoreCase) && html.Contains("0 intervention", StringComparison.Ordinal));
});
Add("Printing an unknown source requires an explicit archive choice and never invents its current state", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); var saved = await Save(f, await Open(f));
    await f.App.CalendarStoreFixture.RemoveAsync(entry.Id, entry.Revision);
    await Refuse(() => f.Call("field.report", new { selections = new[] { Selection(saved) } }));
    var html = (await f.Call("field.report", new { selections = new[] { Selection(saved) }, archive = true })).GetProperty("html").GetString()!;
    Check(html.Contains("ARCHIVE", StringComparison.Ordinal) && html.Contains("0 intervention", StringComparison.Ordinal));
});
Add("Readiness filters and complete selection return explicit revisions", async f =>
{
    await f.Save(f.Event()); await Save(f, await Open(f), false);
    var view = await f.Call("field.get", new { readiness = "none", selectAll = true });
    Check(view.GetProperty("selections").GetArrayLength() == 1 && view.GetProperty("selections")[0].GetProperty("expectedRevision").GetInt64() > 0);
    Check((await f.Call("field.get", new { readiness = "ready" })).GetProperty("total").GetInt32() == 0);
});
Add("Filter options expose declared identities without requiring an extract", async f =>
{
    await f.Call("calendar.resource.save", new { resource = new CalendarResource { Key = "declared-agent", Label = "Agent fictif", TimeZoneId = "Europe/Paris" } });
    var options = await f.Call("field.options");
    Check(options.GetProperty("technicians").EnumerateArray().Any(row => row.GetProperty("id").GetString() == "declared-agent"));
});
Add("Material suggestions never save or mark a reused kit prepared", async f =>
{
    await f.Save(f.Event()); await Save(f, await Open(f));
    await f.Save(f.Event("Deuxième visite", "2026-11-04T09:00", "2026-11-04T10:00"));
    var rows = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences");
    var opened = await Open(f, rows[1].GetProperty("key").GetString());
    var kits = await f.Call("field.kits", new { token = opened.GetProperty("token").GetString() });
    Check(kits.GetProperty("suggestions").GetArrayLength() == 1);
    Check(kits.GetProperty("suggestions")[0].GetProperty("lines")[0].GetProperty("preparedQuantity").GetDecimal() == 0);
    Check((await f.Call("field.get")).GetProperty("total").GetInt32() == 1);
});

var transport = args.Contains("--postgres") ? "postgres" : "folder";
Add("Real " + transport + " transport preserves material and resolves a two-client conflict without resurrection", async f =>
{
    ITeamWorkspaceReplica replica; string? schema = null; string? connection = null;
    if (transport == "postgres")
    {
        connection = Environment.GetEnvironmentVariable("GLPI_FIELD_POSTGRES") ?? throw new InvalidOperationException("The PostgreSQL job requires a real service connection.");
        schema = "field_" + Guid.NewGuid().ToString("N"); await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand("CREATE SCHEMA " + schema, db); await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }; replica = new PostgresTeamWorkspaceReplica(builder.ConnectionString);
    }
    else { var shared = Path.Combine(f.Root, "shared"); Directory.CreateDirectory(shared); replica = new FolderTeamWorkspaceReplica(shared); }
    try
    {
        var a = new TeamWorkspaceStore(Path.Combine(f.Root, "peer-a", "library.db")); var b = new TeamWorkspaceStore(Path.Combine(f.Root, "peer-b", "library.db"));
        var catalogue = FieldMaterialDefaults.Create(); await a.SaveFieldCatalogueAsync(catalogue, 0, "Agent A");
        var plan = FieldPreparationService.AddReference(FieldPreparationService.NewPlan(new("ticket", "fictitious-ticket"), "Préparation partagée fictive"), catalogue, catalogue.References[0].Id, 2);
        var document = new FieldPreparationDocument { Plan = plan, ObservedAtUtc = DateTimeOffset.UtcNow, Tickets = new() { new("fictitious-ticket", "42", "Ticket fictif") } };
        await a.SaveFieldPreparationAsync(document, 0, "Agent A");
        await TeamWorkspaceSyncService.SynchronizeAsync(a, replica); await TeamWorkspaceSyncService.SynchronizeAsync(b, replica);
        Check(FieldPreparationDocuments.ReadPlan((await b.GetAsync(plan.Id))!).Plan.Lines[0].Quantity == 2);
        var first = (await a.GetAsync(plan.Id))!; var second = (await b.GetAsync(plan.Id))!;
        var ad = FieldPreparationDocuments.ReadPlan(first); ad.Plan.PreparationNote = "Version A"; await a.SaveFieldPreparationAsync(ad, first.Revision, "Agent A");
        var bd = FieldPreparationDocuments.ReadPlan(second); bd.Plan.PreparationNote = "Version B choisie"; await b.SaveFieldPreparationAsync(bd, second.Revision, "Agent B");
        await TeamWorkspaceSyncService.SynchronizeAsync(a, replica); await TeamWorkspaceSyncService.SynchronizeAsync(b, replica); await TeamWorkspaceSyncService.SynchronizeAsync(a, replica);
        var conflicted = (await b.GetAsync(plan.Id))!; Check(conflicted.Conflicts > 0);
        var conflict = (await b.ConflictsAsync(plan.Id)).First();
        await Refuse(() => b.ResolveFieldConflictAsync(plan.Id, conflicted.Revision - 1, conflict.Id, "incoming", "Agent B"));
        await b.ResolveFieldConflictAsync(plan.Id, conflicted.Revision, conflict.Id, "incoming", "Agent B");
        await TeamWorkspaceSyncService.SynchronizeAsync(b, replica); await TeamWorkspaceSyncService.SynchronizeAsync(a, replica);
        Check((await a.GetAsync(plan.Id))!.Conflicts == 0 && (await b.GetAsync(plan.Id))!.Conflicts == 0);
        Check(FieldPreparationDocuments.ReadPlan((await a.GetAsync(plan.Id))!).Plan.PreparationNote == "Version B choisie");
        foreach (var change in await replica.ChangesAsync(0, 200)) await a.ApplyChangeAsync(change, CancellationToken.None);
        Check((await a.GetAsync(plan.Id))!.Conflicts == 0, "Replaying an archived conflict must not reopen it.");
        await using var local = new SqliteConnection("Data Source=" + b.DatabasePath); await local.OpenAsync();
        await using var audit = local.CreateCommand(); audit.CommandText = "SELECT COUNT(*) FROM gp_team_field_resolution_audit WHERE item_id=@id"; audit.Parameters.AddWithValue("@id", plan.Id);
        Check(Convert.ToInt64(await audit.ExecuteScalarAsync()) > 0, "Both conflict versions must be retained in the audit table.");
    }
    finally
    {
        if (schema is not null) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", db); await drop.ExecuteNonQueryAsync(); }
    }
});
var failures = 0;
foreach (var check in cases)
{
    try { await check.Run(); Console.WriteLine("PASS: " + check.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL: " + check.Name + "\n" + error); }
}
Console.WriteLine($"{cases.Count - failures}/{cases.Count} field completion checks passed ({transport}).");
return failures == 0 ? 0 : 1;
