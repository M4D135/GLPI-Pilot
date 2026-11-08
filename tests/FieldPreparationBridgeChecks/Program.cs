using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

var checks = new List<(string Name, Func<Task> Run)>();
void Add(string name, Func<CalendarFixture, Task> test) => checks.Add((name, async () => { await using var f = new CalendarFixture(); await test(f); }));
void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
async Task Refuse(Func<Task> call)
{
    try { await call(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return; }
    throw new Exception("Expected refusal");
}
FieldPreparationDocument Document(JsonElement opened) => opened.GetProperty("document").Deserialize<FieldPreparationDocument>(PilotApplication.Json)!;
async Task<JsonElement> OpenVisit(CalendarFixture f, string? key = null)
{
    key ??= (await f.Call("calendar.get", f.Range())).GetProperty("occurrences")[0].GetProperty("key").GetString();
    return await f.Call("field.open", new { occurrenceKey = key, from = "2026-11-02", to = "2026-11-09", timeZoneId = "Europe/Paris", revision = f.App.Revision });
}
async Task<JsonElement> OpenTicket(CalendarFixture f, int index = 0)
{
    var rows = await f.Call("calendar.tickets");
    return await f.Call("field.open", new { ticketKey = rows.GetProperty("rows")[index].GetProperty("key").GetString(), revision = f.App.Revision });
}
async Task<JsonElement> SaveField(CalendarFixture f, JsonElement opened, Action<FieldPreparationPlan>? change = null)
{
    var plan = Document(opened).Plan; change?.Invoke(plan);
    return await f.Call("field.save", new { token = opened.GetProperty("token").GetString(), plan });
}
object Selection(JsonElement saved) => new { id = saved.GetProperty("id").GetString(), expectedRevision = saved.GetProperty("revision").GetInt64() };
void AddMaterial(JsonElement opened, FieldPreparationPlan plan)
{
    var catalogue = opened.GetProperty("catalogue").Deserialize<FieldMaterialCatalogue>(PilotApplication.Json)!;
    var added = FieldPreparationService.AddReference(plan, catalogue, catalogue.References[0].Id, 2);
    plan.Lines = added.Lines; plan.NoMaterialsRequired = false;
}
Add("Preparation operations and the generic catalogue are available without a GLPI extract", async f =>
{
    Check(f.App.Operations.Contains("field.save"));
    var catalogue = await f.Call("field.catalogue.get"); Check(catalogue.GetProperty("catalogue").GetProperty("categories").GetArrayLength() == 16);
    Check((await f.Call("field.get")).GetProperty("total").GetInt32() == 0);
});
Add("A manual intervention can explicitly need no material and be reopened", async f =>
{
    await f.Save(f.Event()); var opened = await OpenVisit(f); var saved = await SaveField(f, opened, plan => plan.NoMaterialsRequired = true);
    var read = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() });
    Check(Document(read).Plan.NoMaterialsRequired && !read.GetProperty("snapshotOnly").GetBoolean());
});
Add("Quantities prepared and remaining are computed by the real registered engine", async f =>
{
    await f.Save(f.Event()); var opened = await OpenVisit(f);
    await SaveField(f, opened, plan => { AddMaterial(opened, plan); plan.Lines[0].PreparedQuantity = 1; });
    var summary = (await f.Call("field.get")).GetProperty("summary");
    Check(summary.GetProperty("partlyPreparedCount").GetInt32() == 1 && summary.GetProperty("materials")[0].GetProperty("remainingQuantity").GetDecimal() == 1);
});
Add("Two open editors cannot overwrite each other's saved preparation", async f =>
{
    await f.Save(f.Event()); var first = await OpenVisit(f); var second = await OpenVisit(f);
    var saved = await SaveField(f, first, plan => plan.PreparationNote = "Première version");
    await Refuse(() => SaveField(f, second, plan => plan.PreparationNote = "Écrasement interdit"));
    var read = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() }); Check(Document(read).Plan.PreparationNote == "Première version");
});
Add("Changing the loaded extract invalidates an open preparation draft", async f =>
{
    await f.Save(f.Event()); var opened = await OpenVisit(f); f.App.CalendarLoadFixture(CalendarFixture.Data());
    await Refuse(() => SaveField(f, opened, plan => plan.NoMaterialsRequired = true));
    Check((await f.Call("field.get")).GetProperty("total").GetInt32() == 0);
});
Add("A concurrent calendar edit is detected before saving material", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); var opened = await OpenVisit(f);
    entry.Title = "Intervention modifiée"; await f.Save(entry, entry.Revision);
    await Refuse(() => SaveField(f, opened, plan => plan.NoMaterialsRequired = true));
});
Add("Moving a visit preserves its preparation identity and material without duplicating it", async f =>
{
    var entry = CalendarFixture.Entry(await f.Save(f.Event())); var opened = await OpenVisit(f);
    var saved = await SaveField(f, opened, plan => AddMaterial(opened, plan));
    entry.StartLocal = "2026-11-03T14:00"; entry.EndLocal = "2026-11-03T15:00"; await f.Save(entry, entry.Revision);
    var moved = await OpenVisit(f); Check(Document(moved).Plan.Id == saved.GetProperty("id").GetString() && Document(moved).Plan.Lines.Count == 1);
    await SaveField(f, moved);
    Check((await f.Call("field.get")).GetProperty("total").GetInt32() == 1);
});
Add("Repeated occurrences receive distinct preparations", async f =>
{
    var value = f.Event(); value.Recurrence = new() { Frequency = "daily", Count = 2 }; await f.Save(value);
    var rows = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences");
    var a = await OpenVisit(f, rows[0].GetProperty("key").GetString()); var b = await OpenVisit(f, rows[1].GetProperty("key").GetString());
    Check(Document(a).Plan.Id != Document(b).Plan.Id);
    await SaveField(f, a, plan => plan.NoMaterialsRequired = true); await SaveField(f, b, plan => plan.NoMaterialsRequired = true);
    Check((await f.Call("field.get")).GetProperty("summary").GetProperty("planCount").GetInt32() == 2);
});
Add("An intervention shared by two technicians still counts as one preparation", async f =>
{
    await f.Save(f.Event(participants: new[] { "agent-a", "agent-b" })); var opened = await OpenVisit(f);
    await SaveField(f, opened, plan => plan.NoMaterialsRequired = true);
    var result = await f.Call("field.get"); Check(result.GetProperty("total").GetInt32() == 1 && result.GetProperty("rows")[0].GetProperty("participantKeys").GetArrayLength() == 2);
});
Add("The page cannot replace trusted contact or title metadata in a material save", async f =>
{
    await f.Save(f.Event("Titre officiel")); var opened = await OpenVisit(f);
    var saved = await SaveField(f, opened, plan => { plan.Title = "Titre injecté"; plan.Contact = "Faux contact"; plan.NoMaterialsRequired = true; });
    var read = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() });
    Check(Document(read).Plan.Title == "Titre officiel" && Document(read).Plan.Contact == "");
});
Add("A report only contains selected persisted preparations and safely encodes text", async f =>
{
    await f.Save(f.Event("Fiche sélectionnée")); var a = await OpenVisit(f);
    var first = await SaveField(f, a, plan => { plan.NoMaterialsRequired = true; plan.PreparationNote = "<script>alert('test')</script>"; });
    await f.Save(f.Event("NE PAS IMPRIMER", "2026-11-04T09:00", "2026-11-04T10:00"));
    var occurrences = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences");
    var b = await OpenVisit(f, occurrences.EnumerateArray().Single(row => row.GetProperty("title").GetString() == "NE PAS IMPRIMER").GetProperty("key").GetString());
    await SaveField(f, b, plan => plan.NoMaterialsRequired = true);
    var html = (await f.Call("field.report", new { selections = new[] { Selection(first) }, timeZoneId = "Europe/Paris" })).GetProperty("html").GetString()!;
    Check(!html.Contains("NE PAS IMPRIMER", StringComparison.Ordinal) && !html.Contains("<script>", StringComparison.Ordinal) && html.Contains("&lt;script&gt;", StringComparison.Ordinal));
});
Add("Stale selected revisions cannot generate an apparently current printout", async f =>
{
    await f.Save(f.Event()); var first = await SaveField(f, await OpenVisit(f), plan => plan.NoMaterialsRequired = true);
    var changed = await f.Call("field.open", new { id = first.GetProperty("id").GetString() }); await SaveField(f, changed, plan => plan.PreparationNote = "Nouvelle consigne");
    await Refuse(() => f.Call("field.report", new { selections = new[] { Selection(first) } }));
});
Add("Cancelling the native HTML file picker creates no exported file", async f =>
{
    await f.Save(f.Event()); var saved = await SaveField(f, await OpenVisit(f), plan => plan.NoMaterialsRequired = true);
    f.Host.SavePath = null; var result = await f.Call("field.export", new { selections = new[] { Selection(saved) } });
    Check(result.GetProperty("cancelled").GetBoolean());
});
Add("HTML export writes an actual selected document through the host-approved path", async f =>
{
    await f.Save(f.Event()); var saved = await SaveField(f, await OpenVisit(f), plan => plan.NoMaterialsRequired = true);
    f.Host.SavePath = Path.Combine(f.Root, "fiches.html"); await f.Call("field.export", new { selections = new[] { Selection(saved) } });
    Check(File.ReadAllText(f.Host.SavePath).Contains("Liste de départ", StringComparison.Ordinal));
    Check(!Directory.EnumerateFiles(f.Root, "*.tmp").Any());
});
Add("Mine without a selected identity is empty rather than widened to everyone", async f =>
{
    await f.Save(f.Event()); await SaveField(f, await OpenVisit(f), plan => plan.NoMaterialsRequired = true);
    var result = await f.Call("field.get", new { scope = "mine" }); Check(result.GetProperty("identityRequired").GetBoolean() && result.GetProperty("total").GetInt32() == 0);
});
Add("A ticket preparation survives a new extract and a changed row order", async f =>
{
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Incident A", "Nouveau", "Agent A", "Support" }));
    var first = await SaveField(f, await OpenTicket(f), plan => plan.NoMaterialsRequired = true);
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "99", "Autre", "Nouveau", "Agent B", "Support" }, new[] { "42", "Incident A", "Nouveau", "Agent A", "Support" }));
    var rows = (await f.Call("calendar.tickets")).GetProperty("rows"); var key = rows.EnumerateArray().Single(row => row.GetProperty("id").GetString() == "42").GetProperty("key").GetString();
    var current = await f.Call("field.open", new { ticketKey = key, revision = f.App.Revision });
    Check(Document(current).Plan.Id == first.GetProperty("id").GetString() && Document(current).Plan.NoMaterialsRequired);
});
Add("A missing ticket retains its recorded detail and explicit snapshot warning", async f =>
{
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "42", "Incident conservé", "Nouveau", "Agent A", "Support" }));
    var saved = await SaveField(f, await OpenTicket(f), plan => plan.NoMaterialsRequired = true);
    f.App.CalendarLoadFixture(CalendarFixture.Data());
    var read = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() });
    Check(read.GetProperty("snapshotOnly").GetBoolean() && Document(read).Tickets[0].Title == "Incident conservé");
});
Add("Unknown tokens cannot manufacture a preparation or bypass source validation", async f =>
{
    var forged = FieldPreparationService.NewPlan(new("ticket", "forged"), "Forged");
    await Refuse(() => f.Call("field.save", new { token = Guid.NewGuid().ToString("N"), plan = forged }));
    Check((await f.Call("field.get")).GetProperty("total").GetInt32() == 0);
});
var failed = 0;
foreach (var check in checks)
{
    try { await check.Run(); Console.WriteLine("PASS: " + check.Name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL: " + check.Name + "\n" + error); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} field bridge checks passed.");
return failed == 0 ? 0 : 1;
