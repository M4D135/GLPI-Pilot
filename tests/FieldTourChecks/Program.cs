using System.Globalization;
using System.Net;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

var checks = new List<(string Name, Func<Task> Run)>();
void Add(string name, Func<CalendarFixture, Task> test) => checks.Add((name, async () => { await using var f = new CalendarFixture(); await test(f); }));
void Check(bool value, string label = "Assertion failed") { if (!value) throw new InvalidOperationException(label); }
async Task Refuse(Func<Task> call)
{
    try { await call(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected refusal");
}
ScheduledTour Tour(CalendarFixture f, int stops = 2)
{
    f.App.Settings.Cities.Enabled = true;
    var city = new CityDefinition { Name = "Ville fictive", Aliases = new() { "Ville fictive" } }; f.App.Settings.Cities.Cities.Add(city);
    var tour = new ScheduledTour { Name = "Tour fictif", Date = "2026-11-02", StartTime = "08:30", EndTime = "17:30", TimeZoneId = "Europe/Paris", TechnicianKey = "agent-a", SourceKey = "https://calendar-glpi.test" };
    for (var index = 1; index <= stops; index++)
    {
        var site = new SiteDefinition { Name = "Bureau " + index, CityId = city.Id, Address = "Rue fictive " + index, Aliases = new() { "Bureau " + index } }; f.App.Settings.Cities.Sites.Add(site);
        tour.Tickets.Add(new() { TicketId = index.ToString(CultureInfo.InvariantCulture), PlaceKey = "site:" + site.Id, BaselineStatus = 1, BaselineFingerprint = new string('a', 64) });
        tour.Stops.Add(new() { CityId = city.Id, SiteId = site.Id, TicketIds = new() { index.ToString(CultureInfo.InvariantCulture) }, DurationMinutes = 30, TravelMinutes = index == 1 ? 15 : 20 });
    }
    new ScheduledTourStore().With(tour).Save(Path.Combine(f.Root, "scheduled-tours.json")); return tour;
}
async Task<JsonElement> Save(CalendarFixture f, object origin, decimal quantity = 2)
{
    var opened = await f.Call("field.open", origin); var document = opened.GetProperty("document").Deserialize<FieldPreparationDocument>(PilotApplication.Json)!;
    var cat = opened.GetProperty("catalogue").Deserialize<FieldMaterialCatalogue>(PilotApplication.Json)!;
    document.Plan = FieldPreparationService.AddReference(document.Plan, cat, cat.References[0].Id, quantity);
    return await f.Call("field.save", new { token = opened.GetProperty("token").GetString(), plan = document.Plan });
}
Add("Whole-tour printing includes every unprepared visit without manufacturing stored preparations", async f =>
{
    var tour = Tour(f);
    var reply = await f.Call("field.report", new { tourId = tour.Id, timeZoneId = "Europe/Paris" }); var html = WebUtility.HtmlDecode(reply.GetProperty("html").GetString()!);
    Check(html.Split("<article class=\"sheet\">", StringSplitOptions.None).Length - 1 == 2);
    Check(html.Contains("Bureau 1", StringComparison.Ordinal) && html.Contains("Bureau 2", StringComparison.Ordinal));
    Check(html.Contains("À renseigner", StringComparison.Ordinal) || html.Contains("à préciser", StringComparison.Ordinal));
    Check((await f.App.CalendarStoreFixture.FieldPreparationsAsync()).Count == 0);
});
Add("Tour export retains the saved material of its selected stop and all other visits", async f =>
{
    var tour = Tour(f); var key = "tour:" + tour.Id + ":stop:" + tour.Stops[0].Id;
    await Save(f, new { occurrenceKey = key, revision = f.App.Revision });
    f.Host.SavePath = Path.Combine(f.Root, "tour.html");
    var exported = await f.Call("field.export", new { tourId = tour.Id }); Check(!exported.GetProperty("cancelled").GetBoolean());
    var html = WebUtility.HtmlDecode(await File.ReadAllTextAsync(f.Host.SavePath));
    Check(html.Contains("Bureau 1", StringComparison.Ordinal) && html.Contains("Bureau 2", StringComparison.Ordinal));
    Check(html.Contains(FieldMaterialDefaults.Create().References[0].Label, StringComparison.Ordinal));
});
Add("Replanning a tour changes preparation dates without losing its stable stop material", async f =>
{
    var tour = Tour(f); var saved = await Save(f, new { occurrenceKey = "tour:" + tour.Id + ":stop:" + tour.Stops[0].Id, revision = f.App.Revision });
    tour.Date = "2027-05-03"; new ScheduledTourStore().With(tour).Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var read = await f.Call("field.open", new { id = saved.GetProperty("id").GetString() });
    var plan = read.GetProperty("document").GetProperty("plan"); Check(plan.GetProperty("plannedStart").GetDateTimeOffset().Year == 2027 && plan.GetProperty("lines").GetArrayLength() == 1);
});
Add("Removing a stop excludes its recorded needs without deleting the record", async f =>
{
    var tour = Tour(f); await Save(f, new { occurrenceKey = "tour:" + tour.Id + ":stop:" + tour.Stops[0].Id, revision = f.App.Revision });
    tour.Stops.RemoveAt(0); new ScheduledTourStore().With(tour).Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var view = await f.Call("field.get"); Check(view.GetProperty("rows")[0].GetProperty("sourceStatus").GetString() == "missing");
    Check(view.GetProperty("summary").GetProperty("materials").GetArrayLength() == 0 && (await f.App.CalendarStoreFixture.FieldPreparationsAsync()).Count == 1);
});
Add("Tour scope includes standalone linked-ticket needs once and preserves additional visit needs", async f =>
{
    var tour = Tour(f);
    f.App.CalendarLoadFixture(CalendarFixture.Data(new[] { "1", "Bureau 1 Incident fictif", "Nouveau", "Agent A", "Support" }, new[] { "2", "Bureau 2 Incident fictif", "Nouveau", "Agent A", "Support" }));
    var ticket = (await f.Call("calendar.tickets")).GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "1");
    await Save(f, new { ticketKey = ticket.GetProperty("key").GetString(), revision = f.App.Revision }, 4);
    await Save(f, new { occurrenceKey = "tour:" + tour.Id + ":stop:" + tour.Stops[0].Id, revision = f.App.Revision }, 2);
    var view = await f.Call("field.get", new { tourId = tour.Id, selectAll = true });
    Check(view.GetProperty("total").GetInt32() == 2 && view.GetProperty("summary").GetProperty("materials")[0].GetProperty("quantity").GetDecimal() == 6);
    var html = WebUtility.HtmlDecode((await f.Call("field.report", new { tourId = tour.Id })).GetProperty("html").GetString()!);
    Check(html.Split("<article class=\"sheet\">", StringSplitOptions.None).Length - 1 == 3, "Two visit sheets plus one standalone ticket sheet are required.");
    Check(html.Contains("complémentaires", StringComparison.Ordinal), "Independent visit needs must be explained rather than silently omitted.");
});
Add("A removed tour cannot silently generate a partial report", async f =>
{
    var tour = Tour(f); new ScheduledTourStore().Save(Path.Combine(f.Root, "scheduled-tours.json"));
    await Refuse(() => f.Call("field.report", new { tourId = tour.Id }));
});
Add("A stale tour-linked preparation is retained as an explicit archive and never implies current availability", async f =>
{
    var tour = Tour(f); var saved = await Save(f, new { occurrenceKey = "tour:" + tour.Id + ":stop:" + tour.Stops[0].Id, revision = f.App.Revision });
    new ScheduledTourStore().Save(Path.Combine(f.Root, "scheduled-tours.json"));
    var selection = new[] { new { id = saved.GetProperty("id").GetString(), expectedRevision = saved.GetProperty("revision").GetInt64() } };
    await Refuse(() => f.Call("field.report", new { selections = selection }));
    var archive = WebUtility.HtmlDecode((await f.Call("field.report", new { selections = selection, archive = true })).GetProperty("html").GetString()!);
    Check(archive.Contains("état actuel inconnu", StringComparison.Ordinal) && archive.Contains("0 intervention", StringComparison.Ordinal));
});
var failed = 0;
foreach (var test in checks) { try { await test.Run(); Console.WriteLine("PASS: " + test.Name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL: " + test.Name + "\n" + error); } }
Console.WriteLine($"{checks.Count - failed}/{checks.Count} field tour checks passed."); return failed == 0 ? 0 : 1;
