using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class TerrainTourSearchChecks
{
    private const string SourceUrl = "https://search-tour.example.invalid/front/ticket.php";

    public static async Task RunAsync(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var isolated = Path.Combine(root, "GLPI-Pilot-Checks-SearchTour");
        Directory.CreateDirectory(isolated); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", isolated);
        try
        {
            await CheckSearch(isolated, check, reject);
            await CheckLimit(check, reject);
        }
        finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root); }
    }

    private static async Task CheckSearch(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var host = new TerrainTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, Settings());
        app.LoadFixture(Source());
        Task<JsonElement> Call(string operation, object? args = null, CancellationToken ct = default) => Execute(app, operation, args, ct);
        check(app.Operations.Contains("terrain.tours.createFromSearch"), "search tour creation has a registered production operation");
        var revision = app.Revision;
        var search = new { revision, search = "Réseau", transform = new { filters = new[] { new { column = 3, @operator = "eq", value = "Centre" } }, sorts = Array.Empty<object>() } };
        var list = await Call("records.list", new { revision, search.search, search.transform, offset = 50, limit = 50 });
        check(list.GetProperty("total").GetInt32() == 120 && list.GetProperty("rows").GetArrayLength() == 50,
            "search tour fixture has 120 matching tickets while the visible page contains only 50");
        await Call("terrain.tours.select", new { revision, rows = new[] { 149, 151 }, selected = true });
        var alice = TechnicianDashboardService.IdentityKey("Alice");
        var tour = await Call("terrain.tours.createFromSearch", new { revision, ticketSearch = search, name = "Recherche complète", date = "2030-01-07", technician = alice });
        var expected = Enumerable.Range(1, 120).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
        var reconciliation = await Reconciliation(app, tour);
        check(Ids(reconciliation).SequenceEqual(expected) && tour.GetProperty("draft").GetProperty("tickets").GetInt32() == 120,
            "search creates every exact matching ticket across pages and excludes the unrelated terrain basket");
        check(reconciliation.GetProperty("completed").GetInt32() == 60 && Ids(reconciliation).Contains("120"),
            "search creation preserves matching closed tickets and rows beyond the first page");
        check(tour.GetProperty("draft").GetProperty("name").GetString() == "Recherche complète"
            && tour.GetProperty("draft").GetProperty("date").GetString() == "2030-01-07"
            && tour.GetProperty("draft").GetProperty("technicianKey").GetString() == alice,
            "search tour uses the supplied name, date and technician without a map operation");
        check((await BasketRows(app)).SequenceEqual(new[] { 149, 151 }), "search tour creation leaves the existing terrain basket intact");

        tour = await Call("terrain.tours.save", new { revision, draftVersion = Version(tour) });
        var path = Path.Combine(root, "scheduled-tours.json");
        check(ScheduledTourStore.Load(path).Tours.Single().Tickets.Select(ticket => ticket.TicketId).SequenceEqual(expected),
            "search tour saves the actual exact ticket references through the production store");
        var savedState = tour.GetRawText(); var savedBytes = await File.ReadAllBytesAsync(path);
        await reject(() => Call("terrain.tours.createFromSearch", new { revision, ticketSearch = search, name = "Date invalide", date = "2030-02-31", discardDraft = true }),
            "invalid search tour date is refused before replacing the saved draft");
        check((await Call("terrain.tours.state")).GetRawText() == savedState && (await File.ReadAllBytesAsync(path)).SequenceEqual(savedBytes),
            "failed search creation preserves the draft version, saved baseline and persisted tour");
        tour = await Call("terrain.tours.save", new { revision, draftVersion = Version(tour) });
        check(!tour.GetProperty("dirty").GetBoolean(), "the original saved draft remains saveable after a failed replacement");

        var selectedSearch = new { revision, search = "Réseau", search.transform, selectedRows = new[] { 119, 110, 119 } };
        tour = await Call("terrain.tours.createFromSearch", new { revision, ticketSearch = selectedSearch, name = "Sélection précise", date = "2030-01-08" });
        check(Ids(await Reconciliation(app, tour)).SequenceEqual(new[] { "111", "120" }),
            "explicit search selection creates only those unique ticket IDs, including a closed ticket beyond page one");
        var draftBefore = (await Call("terrain.tours.state")).GetRawText();
        async Task RefuseAndPreserve(object args, string label, CancellationToken ct = default)
        {
            await reject(() => Call("terrain.tours.createFromSearch", args, ct), label);
            check((await Call("terrain.tours.state")).GetRawText() == draftBefore
                && Ids(await Reconciliation(app, tour)).SequenceEqual(new[] { "111", "120" })
                && (await BasketRows(app)).SequenceEqual(new[] { 149, 151 }),
                label + ": original draft and terrain basket remain intact");
        }
        await RefuseAndPreserve(new { revision, ticketSearch = search, name = "Sans confirmation", date = "2030-01-09" },
            "a dirty tour requires explicit discard confirmation for search creation");
        await RefuseAndPreserve(new { revision, name = "Sans recherche", date = "2030-01-09", discardDraft = true },
            "missing search never falls back to the whole source or terrain basket");
        await RefuseAndPreserve(new { revision, ticketSearch = (object?)null, name = "Recherche nulle", date = "2030-01-09", discardDraft = true },
            "a null search is rejected");
        await RefuseAndPreserve(new { revision, ticketSearch = new { }, name = "Recherche vide", date = "2030-01-09", discardDraft = true },
            "an empty search object without its revision is rejected");
        await RefuseAndPreserve(new { revision, ticketSearch = new { revision, search = "Absent de tous les titres" }, name = "Aucun résultat", date = "2030-01-09", discardDraft = true },
            "zero search matches cannot create an empty tour");
        await RefuseAndPreserve(new { revision, ticketSearch = new { revision, search = "Réseau", selectedRows = Array.Empty<int>() }, name = "Sélection vide", date = "2030-01-09", discardDraft = true },
            "explicit empty search selection cannot broaden to matching rows");
        await RefuseAndPreserve(new { revision, ticketSearch = new { revision, search = "Réseau", indices = Array.Empty<int>() }, name = "Périmètre vide", date = "2030-01-09", discardDraft = true },
            "explicit empty search scope cannot broaden to the source");
        await RefuseAndPreserve(new { revision, ticketSearch = new { revision, search = "Réseau", selectedRows = new[] { 110, 149 } }, name = "Hors recherche", date = "2030-01-09", discardDraft = true },
            "one selected row outside the search rejects the whole creation");
        await RefuseAndPreserve(new { revision = revision - 1, ticketSearch = search, name = "Écran périmé", date = "2030-01-09", discardDraft = true },
            "stale tour-screen revision is rejected");
        await RefuseAndPreserve(new { revision, ticketSearch = new { revision = revision - 1, search = "Réseau" }, name = "Recherche périmée", date = "2030-01-09", discardDraft = true },
            "stale nested search revision is rejected independently");
        await RefuseAndPreserve(new { revision, ticketSearch = search, name = "", date = "2030-01-09", discardDraft = true },
            "an invalid tour name is rejected without replacing the current draft");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await RefuseAndPreserve(new { revision, ticketSearch = search, name = "Annulée", date = "2030-01-09", discardDraft = true },
            "cancelled search creation stops before draft mutation", cancelled.Token);

        await Call("records.basket", new { revision, rows = new[] { 110, 111, 112, 119, 139, 149 } });
        var filteredSearch = new { revision, search = "Réseau", search.transform, indices = new[] { 108, 110, 111, 112, 119, 139, 149 }, basketOnly = true, technicianKeys = new[] { alice } };
        tour = await Call("terrain.tours.createFromSearch", new { revision, ticketSearch = filteredSearch, name = "Intersection complète", date = "2030-01-09", discardDraft = true });
        check(Ids(await Reconciliation(app, tour)).SequenceEqual(new[] { "111", "113" }),
            "search tour intersects text, column filters, source indices, search basket and technician scope");
        check((await Call("records.basket")).GetProperty("rows").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(new[] { 110, 111, 112, 119, 139, 149 })
            && (await BasketRows(app)).SequenceEqual(new[] { 149, 151 }),
            "both independent baskets retain their exact rows after confirmed search-tour replacement");
        check(host.ExternalUrls.Count == 0 && host.Closures == 0, "direct search-tour creation opens no external page or application");
    }

    private static async Task CheckLimit(Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        await using var app = new PilotApplication(new TerrainTestHost(), (_, _) => { }, Settings());
        app.LoadFixture(Source(ScheduledTour.MaximumTickets + 1));
        var revision = app.Revision;
        await Execute(app, "terrain.tours.select", new { revision, rows = new[] { ScheduledTour.MaximumTickets }, selected = true });
        var tour = await Execute(app, "terrain.tours.createFromSearch", new { revision, ticketSearch = new { revision, selectedRows = new[] { 0, 1 } }, name = "Avant limite", date = "2030-01-07" });
        var before = tour.GetRawText();
        await reject(() => Execute(app, "terrain.tours.createFromSearch", new { revision, ticketSearch = new { revision, search = "Réseau" }, name = "Trop de tickets", date = "2030-01-07", discardDraft = true }),
            "a search containing more than 2000 matching tickets is refused before mutation");
        check((await Execute(app, "terrain.tours.state")).GetRawText() == before
            && Ids(await Reconciliation(app, tour)).SequenceEqual(new[] { "1", "2" })
            && (await BasketRows(app)).SequenceEqual(new[] { ScheduledTour.MaximumTickets }),
            "oversized search preserves the previous draft and terrain basket");
        tour = await Execute(app, "terrain.tours.createFromSearch", new { revision, ticketSearch = new { revision, search = "Réseau", selectedRows = Enumerable.Range(0, ScheduledTour.MaximumTickets).ToArray() }, name = "Limite exacte", date = "2030-01-07", discardDraft = true });
        var last = await Execute(app, "terrain.tours.reconciliation", new { revision, draftVersion = Version(tour), offset = 1900, limit = 200 });
        check(tour.GetProperty("draft").GetProperty("tickets").GetInt32() == ScheduledTour.MaximumTickets
            && last.GetProperty("total").GetInt32() == ScheduledTour.MaximumTickets
            && Ids(last).SequenceEqual(Enumerable.Range(1901, 100).Select(value => value.ToString(CultureInfo.InvariantCulture)))
            && (await BasketRows(app)).SequenceEqual(new[] { ScheduledTour.MaximumTickets }),
            "an explicit 2000-ticket selection from a larger search is accepted completely without changing the basket");
    }

    private static AppSettings Settings()
    {
        var settings = new AppSettings { PortalUrl = SourceUrl };
        settings.Cities.TitleColumn = "Titre";
        settings.Cities.Cities = [new() { Id = "11111111111111111111111111111111", Name = "Centre" }, new() { Id = "22222222222222222222222222222222", Name = "Annexe" }];
        settings.Dashboard.ColumnMappings = new(StringComparer.Ordinal) { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut", ["assignee"] = "Technicien" };
        return settings;
    }
    private static ExtractionResult Source(int count = 160) => new()
    {
        Headers = ["ID", "Titre", "Statut", "Lieu", "Technicien"],
        Rows = Enumerable.Range(0, count).Select(i => new[] {
            (i + 1).ToString(CultureInfo.InvariantCulture),
            (count > 160 || i < 140 ? "Réseau " : "Autre ") + (count > 160 || i < 120 ? "Centre " : "Annexe ") + i.ToString(CultureInfo.InvariantCulture),
            i % 2 == 0 ? "Nouveau" : "Clos", count > 160 || i < 120 ? "Centre" : "Annexe", i % 2 == 0 ? "Alice" : "Bob"
        }).ToList(),
        SourceUrl = SourceUrl, ExtractedAt = DateTimeOffset.Now, Complete = true,
        Provenance = new() { SourceKind = "browser", RequestedUrl = SourceUrl, SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "synthetic-search-tour" }
    };
    private static int Version(JsonElement value) => value.GetProperty("draftVersion").GetInt32();
    private static string[] Ids(JsonElement value) => value.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("ticketId").GetString()!).ToArray();
    private static Task<JsonElement> Reconciliation(PilotApplication app, JsonElement tour) => Execute(app, "terrain.tours.reconciliation", new { revision = app.Revision, draftVersion = Version(tour), limit = 200 });
    private static async Task<int[]> BasketRows(PilotApplication app)
    {
        var page = await Execute(app, "terrain.tours.basket", new { revision = app.Revision, allStatuses = true, selectedOnly = true, limit = 200 });
        return page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("row").GetInt32()).ToArray();
    }
    private static async Task<JsonElement> Execute(PilotApplication app, string operation, object? args = null, CancellationToken ct = default)
        => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json), ct), PilotApplication.Json);
}
