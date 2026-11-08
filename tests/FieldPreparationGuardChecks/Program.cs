using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

static async Task Refuse(Func<Task> action)
{
    try { await action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { return; }
    throw new InvalidOperationException("The generic workspace operation must refuse this field document.");
}
var failures = 0;
foreach (var catalogue in new[] { false, true })
{
    await using var f = new CalendarFixture();
    try
    {
        string id; long revision;
        if (catalogue)
        {
            var current = await f.Call("field.catalogue.get");
            var saved = await f.Call("field.catalogue.save", new { catalogue = current.GetProperty("catalogue"), expectedRevision = 0 });
            id = FieldPreparationDocuments.CatalogueId; revision = saved.GetProperty("revision").GetInt64();
        }
        else
        {
            await f.Save(f.Event());
            var occurrence = (await f.Call("calendar.get", f.Range())).GetProperty("occurrences")[0].GetProperty("key").GetString();
            var opened = await f.Call("field.open", new { occurrenceKey = occurrence, from = "2026-11-02", to = "2026-11-09", timeZoneId = "Europe/Paris", revision = f.App.Revision });
            var document = opened.GetProperty("document").Deserialize<FieldPreparationDocument>(PilotApplication.Json)!;
            document.Plan.NoMaterialsRequired = true;
            var saved = await f.Call("field.save", new { token = opened.GetProperty("token").GetString(), plan = document.Plan });
            id = saved.GetProperty("id").GetString()!; revision = saved.GetProperty("revision").GetInt64();
        }
        var before = await f.Call("workspace.get", new { id });
        await Refuse(() => f.Call("workspace.remove", new { id, expectedRevision = revision }));
        await Refuse(() => f.Call("workspace.save", new { item = new { id, kind = "note", title = "Not an allowed replacement" }, expectedRevision = revision }));
        var after = await f.Call("workspace.get", new { id });
        if (before.GetRawText() != after.GetRawText()) throw new InvalidOperationException("A rejected generic operation altered the field document.");
        Console.WriteLine("PASS: " + (catalogue ? "Catalogue" : "Preparation") + " cannot be removed or replaced through generic workspace actions");
    }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL: field document protection\n" + error); }
}
Console.WriteLine($"{2 - failures}/2 field guard checks passed.");
return failures == 0 ? 0 : 1;
