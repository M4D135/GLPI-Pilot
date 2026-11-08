using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class IdentityBridgeChecks
{
    public static void Register(Action<string, Func<CalendarFixture, Task>> add)
    {
        add("Identity can be created without an extract and reopened with its display name and no declared availability", async f =>
        {
            var empty = await f.Call("identity.get", new { currentOnly = true });
            Check(empty.GetProperty("current").ValueKind == JsonValueKind.Null && !empty.GetProperty("hasSource").GetBoolean());
            var created = await f.Call("identity.create", new { label = "  René\u00a0 Dupont  ", revision = f.App.Revision });
            Check(Text(created, "key") == "RENÉ DUPONT" && Text(created, "label") == "René Dupont");
            var current = await f.Call("identity.get", new { currentOnly = true });
            Check(Text(current.GetProperty("current"), "label") == "René Dupont" && current.GetProperty("people").GetArrayLength() == 0 && current.GetProperty("total").GetInt32() == 0);
            var stored = await f.App.CalendarStoreFixture.GetAsync(CalendarWorkspaceDocuments.ResourceId("RENÉ DUPONT"));
            var resource = CalendarWorkspaceDocuments.ReadResource(stored!);
            Check(resource.Active && resource.WorkPeriods.Count == 0 && resource.CapacityDays.Count == 0 && resource.TeamKeys.Count == 0);
            Check(AppSettings.Load().Dashboard.PersonalTechnician == "RENÉ DUPONT");
            await using var reopened = new PilotApplication(new CalendarHost(), (_, _) => { }, snapshots: new SnapshotStore(Path.Combine(f.Root, "reopened-history")));
            var after = JsonSerializer.SerializeToElement(await reopened.ExecuteAsync("identity.get", JsonSerializer.SerializeToElement(new { currentOnly = true }, PilotApplication.Json)), PilotApplication.Json);
            Check(Text(after.GetProperty("current"), "key") == "RENÉ DUPONT" && Text(after.GetProperty("current"), "label") == "René Dupont");
            Check(new[] { "settings.changed", "workplace.changed", "calendar.changed" }.All(name => f.Events.Any(item => item.Name == name)));
        });
        add("Identity search and pagination merge only exact source and calendar keys and expose observed ticket counts", async f =>
        {
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("1", "Alice"), Row("2", "Alice"), Row("3", "Rene"), Row("4", "René"), Row("5", "domain\\pat")));
            await Resource(f, "ALICE", "Alice"); await Resource(f, "manual:calendar", "Agent calendrier");
            var all = await f.Call("identity.get"); var people = all.GetProperty("people").EnumerateArray().ToArray();
            Check(all.GetProperty("hasSource").GetBoolean() && all.GetProperty("total").GetInt32() == 5 && people.Length == 5);
            var alice = people.Single(person => Text(person, "key") == "ALICE");
            Check(alice.GetProperty("fromSource").GetBoolean() && alice.GetProperty("fromCalendar").GetBoolean() && alice.GetProperty("ticketCount").GetInt32() == 2);
            var page = await f.Call("identity.get", new { offset = 1, limit = 2 });
            Check(page.GetProperty("total").GetInt32() == 5 && page.GetProperty("people").EnumerateArray().Select(person => Text(person, "key")).SequenceEqual(people.Skip(1).Take(2).Select(person => Text(person, "key"))));
            var login = await f.Call("identity.get", new { search = "DOMAIN\\pat" });
            Check(login.GetProperty("total").GetInt32() == 1 && Text(login.GetProperty("people")[0], "key") == "DOMAIN\\PAT");
            Check((await f.Call("identity.get", new { search = "rene" })).GetProperty("total").GetInt32() == 2);
            Check((await f.Call("identity.get", new { offset = 100 })).GetProperty("people").GetArrayLength() == 0);
            await Refuse(() => f.Call("identity.get", new { limit = 101 }), "hors limites");
        });
        add("Choosing a source identity persists it and drives both ticket and intervention scopes after the source changes", async f =>
        {
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("1", "Alice"), Row("2", "Alice"), Row("3", "Bob")));
            var picked = await f.Call("identity.set", new { key = "ALICE", revision = f.App.Revision });
            Check(Text(picked, "key") == "ALICE" && Text(picked, "label") == "Alice");
            Check((await f.Call("records.list", new { mine = true, revision = f.App.Revision })).GetProperty("total").GetInt32() == 2);
            Check((await f.Call("calendar.tickets", new { scope = "mine" })).GetProperty("total").GetInt32() == 2);
            await f.Save(f.Event(participants: new[] { "ALICE" }));
            Check((await f.Call("calendar.get", f.Range("mine"))).GetProperty("occurrences").GetArrayLength() == 1);
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("3", "Bob")));
            Check(Text((await f.Call("identity.get", new { currentOnly = true })).GetProperty("current"), "label") == "Alice");
            Check((await f.Call("records.list", new { mine = true, revision = f.App.Revision })).GetProperty("total").GetInt32() == 0);
            Check((await f.Call("calendar.get", f.Range("mine"))).GetProperty("occurrences").GetArrayLength() == 1);
        });
        add("A name chosen before the first extract matches its later assignment without a duplicate calendar identity", async f =>
        {
            await f.Call("identity.create", new { label = "Agent A", revision = f.App.Revision });
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("1", "agent a"), Row("2", "Agent B")));
            var people = (await f.Call("identity.get")).GetProperty("people").EnumerateArray().ToArray();
            Check(people.Count(person => Text(person, "key") == "AGENT A") == 1 && people.Single(person => Text(person, "key") == "AGENT A").GetProperty("ticketCount").GetInt32() == 1);
            Check((await f.Call("records.list", new { mine = true, revision = f.App.Revision })).GetProperty("total").GetInt32() == 1);
            Check((await f.App.CalendarStoreFixture.CalendarResourcesAsync()).Count == 1);
        });
        add("A uniquely named existing manual identity is reused without changing its key or declared availability", async f =>
        {
            var resource = new CalendarResource { Key = "manual:existing", Label = "Anne Martin", TimeZoneId = "Europe/Paris", TeamKeys = new() { "group:Support" },
                WorkPeriods = new() { new() { Day = 1, StartMinute = 540, EndMinute = 720, CapacityMinutes = 120 } } };
            await f.Call("calendar.resource.save", new { resource });
            var before = (await f.App.CalendarStoreFixture.GetAsync(CalendarWorkspaceDocuments.ResourceId(resource.Key)))!;
            var chosen = await f.Call("identity.create", new { label = "anne   martin", revision = f.App.Revision });
            var after = (await f.App.CalendarStoreFixture.GetAsync(before.Id))!;
            Check(Text(chosen, "key") == resource.Key && Text(chosen, "label") == resource.Label && after.Revision == before.Revision
                && after.Payload!.Value.GetRawText() == before.Payload!.Value.GetRawText() && (await f.App.CalendarStoreFixture.CalendarResourcesAsync()).Count == 1);
        });
        add("Ambiguous names require an explicit exact key and never merge existing people or their appointments", async f =>
        {
            await Resource(f, "manual:first", "Alex Martin"); await Resource(f, "manual:second", "alex  martin");
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("1", "Alex Martin")));
            await f.Save(f.Event(participants: new[] { "manual:first" }));
            await Refuse(() => f.Call("identity.create", new { label = "Alex Martin", revision = f.App.Revision }), "Plusieurs identités");
            Check((await f.Call("identity.get", new { currentOnly = true })).GetProperty("current").ValueKind == JsonValueKind.Null);
            await f.Call("identity.set", new { key = "manual:first", revision = f.App.Revision });
            await Refuse(() => f.Call("identity.set", new { key = "MANUAL:FIRST", revision = f.App.Revision }), "plus disponible");
            Check((await f.Call("calendar.get", f.Range("mine"))).GetProperty("occurrences").GetArrayLength() == 1
                && (await f.App.CalendarStoreFixture.CalendarResourcesAsync()).Count == 2 && f.App.Settings.Dashboard.PersonalTechnician == "manual:first");
        });
        add("Identity creation preserves accents and punctuation and refuses stale or malformed requests without changing the choice", async f =>
        {
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("1", "René"), Row("2", "Rene"), Row("3", "Jean-Pierre"), Row("4", "Jean Pierre")));
            var stale = f.App.Revision;
            Check(Text(await f.Call("identity.create", new { label = "René", revision = stale }), "key") == "RENÉ");
            await Refuse(() => f.Call("identity.create", new { label = "Rene", revision = stale }), "changé");
            await Refuse(() => f.Call("identity.set", new { key = "RENE" }), "changé");
            Check(Text(await f.Call("identity.create", new { label = "Rene", revision = f.App.Revision }), "key") == "RENE");
            Check(Text(await f.Call("identity.create", new { label = "Jean-Pierre", revision = f.App.Revision }), "key") == "JEAN-PIERRE");
            foreach (var label in new[] { " ", "Alice; Bob", "Alice\nBob" })
                await Refuse(() => f.Call("identity.create", new { label, revision = f.App.Revision }), "Saisissez");
            Check(f.App.Settings.Dashboard.PersonalTechnician == "JEAN-PIERRE" && (await f.App.CalendarStoreFixture.CalendarResourcesAsync()).Count == 3);
        });
        add("Clearing the personal identity preserves calendar data and leaves the mine scope explicitly empty", async f =>
        {
            await f.Call("identity.create", new { label = "Alice", revision = f.App.Revision });
            await f.Save(f.Event(participants: new[] { "ALICE" }));
            var cleared = await f.Call("identity.clear");
            Check(Text(cleared, "key") == "" && Text(cleared, "label") == "" && AppSettings.Load().Dashboard.PersonalTechnician == "");
            var mine = await f.Call("calendar.get", f.Range("mine"));
            Check(mine.GetProperty("identityRequired").GetBoolean() && mine.GetProperty("occurrences").GetArrayLength() == 0);
            Check((await f.Call("calendar.get", f.Range())).GetProperty("occurrences").GetArrayLength() == 1
                && (await f.App.CalendarStoreFixture.CalendarResourcesAsync()).Count == 1
                && (await f.Call("identity.get", new { currentOnly = true })).GetProperty("current").ValueKind == JsonValueKind.Null);
        });
        add("The legacy technician choice persists the same resource while still rejecting people absent from the extract", async f =>
        {
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("1", "Alice")));
            await f.Call("workplace.technicians.me", new { technicianKey = "alice" });
            Check((await f.App.CalendarStoreFixture.GetAsync(CalendarWorkspaceDocuments.ResourceId("ALICE"))) is not null);
            f.App.CalendarLoadFixture(CalendarFixture.Data(Row("2", "Bob")));
            await Refuse(() => f.Call("workplace.technicians.me", new { technicianKey = "Alice" }), "présente dans ce relevé");
            Check(Text((await f.Call("identity.get", new { currentOnly = true })).GetProperty("current"), "label") == "Alice");
        });
        add("Identity set create and clear remain isolated inside simulation and restore the real local choice on exit", async f =>
        {
            await f.Call("identity.create", new { label = "Personne réelle", revision = f.App.Revision });
            var settingsPath = AppPaths.SettingsFile; var before = await File.ReadAllBytesAsync(settingsPath);
            var store = f.App.CalendarStoreFixture;
            f.Host.OpenPath = Path.Combine(f.Root, "identity-simulation.csv");
            await File.WriteAllTextAsync(f.Host.OpenPath, "ID;Titre;Statut;Technicien;Groupe\n42;Test local;Nouveau;Agent A;Support\n");
            await f.Call("records.test.import");
            var listed = await f.Call("identity.get");
            var agent = listed.GetProperty("people").EnumerateArray().Single(person => person.GetProperty("fromSource").GetBoolean());
            await f.Call("identity.set", new { key = Text(agent, "key"), revision = listed.GetProperty("revision").GetInt64() });
            var selected = await f.Call("identity.get", new { currentOnly = true });
            var created = await f.Call("identity.create", new { label = "Personne simulée", revision = selected.GetProperty("revision").GetInt64() });
            Check(Text(created, "key") == "PERSONNE SIMULÉE"); await f.Call("identity.clear");
            Check((await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(before) && (await store.CalendarResourcesAsync()).Count == 1);
            await f.Call("records.test.exit");
            Check(Text((await f.Call("identity.get", new { currentOnly = true })).GetProperty("current"), "label") == "Personne réelle"
                && (await store.CalendarResourcesAsync()).Count == 1);
        });
    }
    private static string[] Row(string id, string person) => new[] { id, "Ticket " + id, "Nouveau", person, "Support" };
    private static Task<JsonElement> Resource(CalendarFixture f, string key, string label)
        => f.Call("calendar.resource.save", new { resource = new CalendarResource { Key = key, Label = label, TimeZoneId = "Europe/Paris" } });
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Identity bridge assertion failed."); }
    private static async Task Refuse(Func<Task> action, string text)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        { Check(error.Message.Contains(text, StringComparison.OrdinalIgnoreCase)); return; }
        throw new InvalidOperationException("Expected refusal: " + text);
    }
}
