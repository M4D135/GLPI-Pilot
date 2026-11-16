using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class TicketSearchChecks
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Create()
    {
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Func<SessionFixture, Task> action) => tests.Add(("Ticket search: " + name, async () => { await using var f = new SessionFixture(); f.App.LoadSessionFixture(Source()); await action(f); }));
        Add("map and planning receive every filtered result beyond the first page, including closed tickets", async f =>
        {
            var search = new { revision = f.App.Revision, search = "Réseau", transform = new { filters = new[] { new { column = 3, @operator = "eq", value = "Centre" } }, sorts = Array.Empty<object>() } };
            var list = await f.Call("records.list", new { search.revision, search.search, search.transform, limit = 50 });
            Check(list.GetProperty("total").GetInt32() == 120 && list.GetProperty("rows").GetArrayLength() == 50, "source fixture spans multiple pages");
            var tickets = await f.Call("calendar.tickets", new { ticketSearch = search, limit = 2000 });
            Check(tickets.GetProperty("total").GetInt32() == 120, "planning uses the complete query");
            var map = await f.Call("maps.workspace.get", new { ticketSearch = search });
            var rows = await f.Call("maps.workspace.rows", new { token = map.GetProperty("token").GetString(), limit = 50 });
            Check(rows.GetProperty("total").GetInt32() == 120, "map must not add an active-status filter");
        });
        Add("an explicit empty selection never broadens to the whole source", async f =>
        {
            var query = new { revision = f.App.Revision, search = "Réseau", selectedRows = Array.Empty<int>() };
            var result = await f.Call("calendar.tickets", new { ticketSearch = query });
            Check(result.GetProperty("total").GetInt32() == 0, "empty shared selection");
            result = await f.Call("calendar.tickets", new { revision = f.App.Revision, indices = Array.Empty<int>() });
            Check(result.GetProperty("total").GetInt32() == 0, "empty explicit plan selection");
            var map = await f.Call("maps.workspace.get", new { ticketSearch = query });
            var rows = await f.Call("maps.workspace.rows", new { token = map.GetProperty("token").GetString() });
            Check(rows.GetProperty("total").GetInt32() == 0, "empty map scope");
        });
        Add("selection is intersected with the search and stale coordinates are refused", async f =>
        {
            var revision = f.App.Revision;
            var result = await f.Call("calendar.tickets", new { ticketSearch = new { revision, search = "Réseau", selectedRows = new[] { 110, 119 } } });
            Check(result.GetProperty("total").GetInt32() == 2 && result.GetProperty("rows")[0].GetProperty("rowIndex").GetInt32() == 110, "selection beyond page one");
            await Refuse(() => f.Call("calendar.tickets", new { ticketSearch = new { revision, search = "Réseau", selectedRows = new[] { 149 } } }));
            f.App.LoadSessionFixture(Source());
            foreach (var operation in new[] { "calendar.tickets", "maps.workspace.get", "field.get", "terrain.tours.list" })
                await Refuse(() => f.Call(operation, new { ticketSearch = new { revision, search = "Réseau" } }));
        });
        Add("basket and targeted source filters intersect consistently", async f =>
        {
            var revision = f.App.Revision;
            await f.Call("records.basket", new { revision, rows = new[] { 0, 1, 2 } });
            var query = new { revision, indices = new[] { 1, 2, 3 }, basketOnly = true };
            var list = await f.Call("records.list", query);
            var calendar = await f.Call("calendar.tickets", new { ticketSearch = query });
            Check(list.GetProperty("total").GetInt32() == 2 && calendar.GetProperty("total").GetInt32() == 2, "same intersection in both tools");
        });
        Add("calendar filters appointments while retaining conflicts with hidden appointments", async f =>
        {
            var tickets = (await f.Call("calendar.tickets", new { limit = 2000 })).GetProperty("rows");
            CalendarIntervention Visit(int index) => new() { Title = "Visite " + index, StartLocal = "2026-11-02T09:00", EndLocal = "2026-11-02T10:00", TimeZoneId = "Europe/Paris", ParticipantKeys = ["agent-a"], Tickets = [tickets[index].Deserialize<CalendarTicketReference>(PilotApplication.Json)!] };
            await f.Call("calendar.save", new { intervention = Visit(0) });
            var other = Visit(149);
            var preview = await f.Call("calendar.preview", new { intervention = other });
            await f.Call("calendar.save", new { intervention = other, allowConflicts = true, conflictFingerprint = preview.GetProperty("conflictFingerprint").GetString() });
            var result = await f.Call("calendar.get", new { from = "2026-11-02", to = "2026-11-03", timeZoneId = "Europe/Paris", ticketSearch = new { revision = f.App.Revision, search = "Réseau" } });
            Check(result.GetProperty("occurrences").GetArrayLength() == 1, "only one appointment belongs to the search");
            Check(result.GetProperty("issues").EnumerateArray().Any(issue => issue.GetProperty("kind").GetString() == "overlap"), "hidden appointment still contributes a conflict");
        });
        Add("material lists retain only preparations linked to the filtered tickets", async f =>
        {
            var tickets = (await f.Call("calendar.tickets", new { limit = 2000 })).GetProperty("rows");
            foreach (var index in new[] { 0, 148 })
            {
                var opened = await f.Call("field.open", new { revision = f.App.Revision, ticketKey = tickets[index].GetProperty("key").GetString() });
                var document = opened.GetProperty("document").Deserialize<FieldPreparationDocument>(PilotApplication.Json)!;
                document.Plan.NoMaterialsRequired = true;
                await f.Call("field.save", new { token = opened.GetProperty("token").GetString(), plan = document.Plan });
            }
            var all = await f.Call("field.get");
            var filtered = await f.Call("field.get", new { ticketSearch = new { revision = f.App.Revision, search = "Réseau" } });
            Check(all.GetProperty("rows").GetArrayLength() == 2 && filtered.GetProperty("rows").GetArrayLength() == 1, "material filter follows ticket links");
        });
        Add("saved tours match both the ticket search and its GLPI installation", async f =>
        {
            ScheduledTour Tour(string name, string source, string id) => new() { Name = name, SourceKey = source, Date = "2026-11-02", TimeZoneId = "Europe/Paris", Tickets = [new() { TicketId = id, BaselineStatus = 1, BaselineFingerprint = new string('a', 64) }] };
            var source = CityOverrideStore.SourceKey(Source());
            new ScheduledTourStore { Tours = [Tour("Liée", source, "1"), Tour("Autre recherche", source, "149"), Tour("Autre installation", "https://other-glpi.test", "1")] }.Save(Path.Combine(AppPaths.DataRoot, "scheduled-tours.json"));
            var result = await f.Call("terrain.tours.list", new { ticketSearch = new { revision = f.App.Revision, search = "Réseau" } });
            Check(result.GetProperty("total").GetInt32() == 1 && result.GetProperty("items")[0].GetProperty("name").GetString() == "Liée", "same ticket ID in another installation must not match");
        });
        return tests;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Refuse(Func<Task> run)
    {
        try { await run(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected stale/out-of-scope search rejection.");
    }
    private static ExtractionResult Source() => new()
    {
        Headers = ["ID", "Titre", "Statut", "Lieu"],
        Rows = Enumerable.Range(0, 160).Select(i => new[] { (i + 1).ToString(), i < 140 ? "Réseau " + i : "Autre " + i, i % 2 == 0 ? "Nouveau" : "Clos", i < 120 ? "Centre" : "Annexe" }).ToList(),
        SourceUrl = SessionFixture.SourceA, ExtractedAt = DateTimeOffset.Now, Complete = true,
        Provenance = new() { SourceKind = "glpi", RequestedUrl = SessionFixture.SourceA, SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "synthetic-search" }
    };
}
