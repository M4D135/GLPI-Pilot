using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

public static class FileSourceChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> run) => tests.Add(("File source: " + name, async () => { await using var f = new SessionFixture(); await run(f); }));
        Add("CSV is real list data, preserves every source column and leaves the configured portal intact", async f =>
        {
            var settings = JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json);
            await OfflineImportChecks.WriteCsv(f); var source = await f.Call("records.import");
            Check(f.App.IsFileSource && !f.App.IsTestMode && source.GetProperty("fileReadComplete").GetBoolean(), source.GetRawText());
            Check(!source.GetProperty("capabilities").GetProperty("ticketDetails").GetBoolean());
            Check(f.App.Result!.Headers.Take(19).SequenceEqual(OfflineImportChecks.IncidentHeaders));
            Check(f.App.Result.SourceUrl == "" && !f.App.Result.Provenance.SearchVerified && !f.App.Result.Provenance.ScopeVerified);
            Check(settings == JsonSerializer.Serialize(f.App.Settings, PilotApplication.Json));
            Check((await f.Call("library.get")).GetProperty("stats").GetProperty("ticketCount").GetInt64() == 0, "File import must never manufacture detailed library tickets.");
        });
        Add("the engine rejects all detail and GLPI-action entry points while retaining cells and calendar tickets", async f =>
        {
            await Import(f);
            foreach (var op in new[] { "workplace.ticket.open", "workplace.ticket.read", "workplace.ticket.tab", "workplace.ticket.section", "workplace.ticket.browser",
                "workplace.ticket.handover", "workplace.actions.inspect", "workplace.actions.preview", "workplace.actions.confirm", "workplace.enrichment.run",
                "workplace.document.download", "library.capture", "library.ticket", "library.open", "library.resolution" })
                await Refuse(() => f.Call(op), "CSV / Excel");
            Check((await f.Call("records.cell", new { revision = f.App.Revision, row = 0, column = 0 })).GetProperty("raw").GetString() == "0042");
            var tickets = (await f.Call("calendar.tickets")).GetProperty("rows");
            Check(tickets.GetArrayLength() == 1 && tickets[0].GetProperty("url").GetString() == "" && tickets[0].GetProperty("installation").GetString()!.StartsWith("file:"));
        });
        Add("file basket and repeated imports restore the exact prior browser observation", async f =>
        {
            var original = f.App.Result;
            await f.Call("records.basket", new { revision = f.App.Revision, rows = new[] { 0 } });
            await Import(f); await f.Call("records.basket", new { revision = f.App.Revision, rows = new[] { 0 } });
            Check((await f.Call("records.basket")).GetProperty("count").GetInt32() == 1);
            await OfflineImportChecks.WriteCsv(f, "second.csv", 2); await f.Call("records.import");
            Check((await f.Call("records.basket")).GetProperty("count").GetInt32() == 0);
            await f.Call("records.file.close");
            Check(ReferenceEquals(original, f.App.Result) && !f.App.IsFileSource && !f.App.IsTestMode);
            Check((await f.Call("records.basket")).GetProperty("count").GetInt32() == 1);
        });
        Add("XLSX uses the chosen sheet and header while cancelled or changed selections preserve the source", async f =>
        {
            await Import(f); var original = f.App.Result;
            f.Host.OpenPath = OfflineImportChecks.WriteWorkbook(f.Root);
            var choice = await f.Call("records.import");
            await f.Call("records.import", new { token = choice.GetProperty("token").GetString(), cancelled = true });
            Check(ReferenceEquals(original, f.App.Result));
            choice = await f.Call("records.import");
            var selected = await f.Call("records.import", new { token = choice.GetProperty("token").GetString(), sheetName = "Incidents", headerRow = 4 });
            Check(selected.GetProperty("fileReadComplete").GetBoolean() && !f.App.IsTestMode && f.App.Result!.Provenance.Method == "xlsx");
            Check(f.App.Result!.Headers.Take(19).SequenceEqual(OfflineImportChecks.IncidentHeaders));
            original = f.App.Result; choice = await f.Call("records.import");
            await File.AppendAllTextAsync(f.Host.OpenPath!, "changed");
            await Refuse(() => f.Call("records.import", new { token = choice.GetProperty("token").GetString(), sheetName = "Incidents", headerRow = 4 }), "changé");
            Check(ReferenceEquals(original, f.App.Result));
        });
        Add("scheduled browser collection cannot overwrite an active file", async f =>
        {
            var invoked = false; f.App.ForceScheduledFixture((_, _) => { invoked = true; return Task.FromResult<object?>(null); });
            await Import(f); var original = f.App.Result;
            await f.Call("automation.tick"); await f.App.TickAsync();
            Check(!invoked && ReferenceEquals(original, f.App.Result));
        });
        Add("calendar edits made from a file are normal durable work, not demo data", async f =>
        {
            await Import(f); var original = f.App.Result;
            var item = new CalendarIntervention { Title = "Visite issue du fichier", StartLocal = "2026-11-02T09:00", EndLocal = "2026-11-02T10:00", TimeZoneId = "Europe/Paris" };
            var result = await f.Call("calendar.save", new { intervention = item }); Check(result.GetProperty("saved").GetBoolean());
            await f.Call("app.demo"); await f.Call("records.test.exit");
            Check(ReferenceEquals(original, f.App.Result) && f.App.IsFileSource);
            Check((await f.Call("calendar.item", new { id = item.Id })).GetProperty("intervention").GetProperty("title").GetString() == item.Title);
            await f.Call("records.file.close");
            Check((await f.Call("calendar.item", new { id = item.Id })).GetProperty("intervention").GetProperty("title").GetString() == item.Title);
        });
        Add("a file chosen from a demo returns to real settings and does not retain fictitious details", async f =>
        {
            var original = f.App.Result; var portal = f.App.Settings.PortalUrl;
            await f.Call("app.demo"); await Import(f);
            Check(f.App.IsFileSource && !f.App.IsTestMode && f.App.Settings.PortalUrl == portal);
            Check((await f.Call("library.get")).GetProperty("stats").GetProperty("ticketCount").GetInt64() == 0);
            await f.Call("records.file.close"); Check(ReferenceEquals(original, f.App.Result));
        });
        Add("file ticket identities preserve tours and calendar links on reimport without conflating another file", async f =>
        {
            var city = new CityDefinition { Name = "Ville fictive", Latitude = 44, Longitude = 5 };
            f.App.Settings.Cities.Cities.Add(city);
            f.App.Settings.Cities.Sites.Add(new SiteDefinition { Name = "Atelier Nord", Aliases = ["Incident à tester"], CityId = city.Id, Latitude = 44.1, Longitude = 5.1 });
            await f.Call("settings.save", new { revision = f.App.Revision, settings = f.App.Settings });
            await Import(f); var first = f.App.Result!; var key = CityOverrideStore.SourceKey(first);
            var ticketKey = (await f.Call("calendar.tickets")).GetProperty("rows")[0].GetProperty("key").GetString();
            await f.Call("terrain.tours.select", new { revision = f.App.Revision, rows = new[] { 0 }, selected = true });
            var state = await f.Call("terrain.tours.create", new { revision = f.App.Revision, name = "Visite depuis CSV", date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            await f.Call("terrain.tours.save", new { revision = f.App.Revision, draftVersion = state.GetProperty("draftVersion").GetInt32() });
            var saved = ScheduledTourStore.Load(Path.Combine(f.Root, "scheduled-tours.json")).Tours.Single();
            Check(saved.SourceKey == key && saved.Stops.Single().TicketIds.Single() == "0042");
            await Import(f);
            Check(CityOverrideStore.SourceKey(f.App.Result!) == key && (await f.Call("calendar.tickets")).GetProperty("rows")[0].GetProperty("key").GetString() == ticketKey);
            await OfflineImportChecks.WriteCsv(f, "another.csv"); await f.Call("records.import");
            Check(CityOverrideStore.SourceKey(f.App.Result!) != key);
        });
        Add("the demo database already contains 60 complete tickets, six resources, past and future visits and tours", async f =>
        {
            await f.Call("app.demo"); Check(f.App.IsTestMode && !f.App.IsFileSource && f.App.Result!.Rows.Count == 60);
            var stats = (await f.Call("library.get")).GetProperty("stats");
            Check(stats.GetProperty("ticketCount").GetInt64() == 60 && stats.GetProperty("contentCount").GetInt64() == 60);
            var opened = await f.Call("workplace.ticket.open", new { rowIndex = 0, revision = f.App.Revision });
            Check(opened.GetProperty("loaded").GetBoolean() && opened.GetProperty("sections").GetArrayLength() >= 2);
            var root = Directory.GetParent(f.App.Settings.ExportFolder)!.FullName;
            var tours = ScheduledTourStore.Load(Path.Combine(root, "scheduled-tours.json")).Tours;
            Check(tours.Count == 4 && tours.Count(t => t.State == "done") == 2 && tours.Any(t => t.State == "ready") && tours.Any(t => t.State == "draft"));
            var calendar = await f.Call("calendar.get", new { from = DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = DateTime.Today.AddDays(25).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZoneId = "Europe/Paris" });
            var json = calendar.GetRawText(); Check(json.Contains("Formation", StringComparison.OrdinalIgnoreCase) && json.Contains("done") && json.Contains("planned"));
            Check(TechnicianDashboardService.Detect(f.App.Result!, f.App.EffectiveSettings.Dashboard).Technicians.Count == 6);
            await f.Call("records.test.exit"); Check((await f.Call("library.get")).GetProperty("stats").GetProperty("ticketCount").GetInt64() == 0);
        });
        return tests;
    }
    private static async Task Import(SessionFixture f) { await OfflineImportChecks.WriteCsv(f); await f.Call("records.import"); }
    private static void Check(bool value, string message = "File source assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> action, string text)
    {
        try { await action(); } catch (InvalidOperationException e) when (e.Message.Contains(text, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected source capability refusal: " + text);
    }
}
