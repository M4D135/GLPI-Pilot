using System.Globalization;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Terrain-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
var passed = 0;
void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAILED: " + label); passed++; Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> operation, string label)
{
    try { await operation(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException)
    { Check(true, label); return; }
    throw new InvalidOperationException("FAILED (accepted invalid action): " + label);
}
try
{
    if (args.Contains("--serve", StringComparer.Ordinal)) { await TerrainWebFixture.RunAsync(root); return; }
    if (args.Contains("--reference-refresh-only", StringComparer.Ordinal))
    {
        await TerrainDraftRefreshChecks.RunAsync(root, Check, Reject);
        Console.WriteLine($"{passed}/{passed} terrain reference refresh checks passed."); return;
    }
    var alpha = new CityDefinition { Id = "11111111111111111111111111111111", Name = "Alpha", TerritoryPath = "France / Nord / Secteur A", Latitude = 50, Longitude = 2 };
    var beta = new CityDefinition { Id = "22222222222222222222222222222222", Name = "Beta", Latitude = 49, Longitude = 3 };
    var campus = new SiteDefinition { Id = "33333333333333333333333333333333", CityId = alpha.Id, Name = "Campus Nord", Latitude = 50.1, Longitude = 2.1,
        Contact = "Accueil", Equipment = "Adaptateur", Openings = new() { new() { Day = DayOfWeek.Monday, From = "08:00", Until = "12:00" }, new() { Day = DayOfWeek.Monday, From = "13:00", Until = "17:00" } } };
    const string sourceUrl = "https://fixture.invalid/front/ticket.php";
    var settings = new AppSettings { PortalUrl = sourceUrl, Cities = new() { TitleColumn = "Résumé terrain", Cities = new() { alpha, beta }, Sites = new() { campus } } };
    settings.Dashboard.ColumnMappings = new(StringComparer.Ordinal) { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut", ["priority"] = "Priorité", ["assignee"] = "Technicien", ["due"] = "Échéance" };
    var source = new ExtractionResult { Title = "Fixture terrain", SourceUrl = sourceUrl, Complete = true, ExtractedAt = DateTimeOffset.UtcNow,
        Provenance = new() { SourceKind = "browser", RequestedUrl = sourceUrl, SearchVerified = true, ScopeVerified = true },
        Headers = new() { "ID", "Titre", "Résumé terrain", "Statut", "Priorité", "Technicien", "Échéance", "Lieu GLPI" },
        Rows = new() {
            new[] { "1", "Titre natif sans lieu", "Intervention Campus Nord", "En cours (attribué)", "Haute", "Alice", "2030-01-07 17:00", "Beta" },
            new[] { "2", "Titre deuxième", "Intervention Beta", "Nouveau", "Moyenne", "Alice", "2030-01-07 17:00", "" },
            new[] { "3", "Titre ambigu", "Alpha ou Beta", "Nouveau", "Moyenne", "Bob", "", "" },
            new[] { "4", "Titre inconnu", "Intervention introuvable", "Clos", "Basse", "Bob", "", "" }
        } };
    var host = new TerrainTestHost(); var events = new List<string>();
    await using var app = new PilotApplication(host, (name, _) => events.Add(name), settings);
    app.LoadFixture(source);
    async Task<JsonElement> Call(string operation, object? arguments = null, CancellationToken ct = default)
        => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(arguments ?? new { }, PilotApplication.Json), ct), PilotApplication.Json);
    async Task<JsonElement> RefState() => await Call("terrain.reference.state");
    long Rev() => app.Revision;
    static int Draft(JsonElement value) => value.GetProperty("draftVersion").GetInt32();
    static JsonElement Items(JsonElement value) => value.GetProperty("items");

    Check(app.Operations.Contains("terrain.reference.apply") && app.Operations.Contains("terrain.tours.allocate") && app.Operations.Contains("update.install"), "closed terrain/update operation allowlist registered");
    await Reject(() => Call("terrain.executeArbitrary", new { path = root }), "arbitrary operation refused");
    var initial = await RefState();
    Check(initial.GetProperty("cities").GetInt32() == 2 && !initial.GetProperty("dirty").GetBoolean(), "reference starts as an independent saved draft");
    var list = await Call("terrain.reference.list", new { kind = "city", limit = 1, offset = 1 });
    Check(Items(list).GetArrayLength() == 1 && list.GetProperty("total").GetInt32() == 2, "reference pagination occurs in C#");
    await Reject(() => Call("terrain.reference.list", new { kind = "city", limit = 201 }), "oversized page refused");
    var review = await Call("terrain.review.detail", new { revision = Rev(), row = 0 });
    Check(review.GetProperty("title").GetString() == "Intervention Campus Nord" && review.GetProperty("highlights").GetArrayLength() > 0, "review highlights the configured terrain title column");
    var rows = await Call("terrain.review.list", new { revision = Rev(), filter = "all", limit = 2 });
    Check(Items(rows).GetArrayLength() == 2 && rows.GetProperty("total").GetInt32() == 4, "review queue is paginated without dropping source rows");
    await Reject(() => Call("terrain.review.list", new { revision = Rev() - 1 }), "stale source revision refused");
    await Reject(() => Call("terrain.reference.delete", new { draftVersion = Draft(initial), kind = "city", id = alpha.Id }), "deleting a referenced city cannot silently delete sites");

    var building = new BuildingDefinition { Id = "44444444444444444444444444444444", SiteId = campus.Id, Name = "Bâtiment Érable", Aliases = new() { "ERB" }, Floor = "R+1" };
    var draft = await Call("terrain.reference.upsert", new { draftVersion = Draft(initial), kind = "building", entry = building });
    Check(draft.GetProperty("buildings").GetInt32() == 1 && app.Settings.Cities.Buildings.Count == 0, "building edits remain in the draft before global apply");
    await Reject(() => Call("terrain.reference.upsert", new { draftVersion = Draft(initial), kind = "building", entry = building }), "stale reference draft version refused");
    var simulated = await Call("terrain.reference.simulate", new { draftVersion = Draft(draft), title = "Panne Bâtiment Érable", context = new { } });
    Check(simulated.GetProperty("match").GetProperty("buildingName").GetString() == building.Name, "building recognition uses the actual detector");
    draft = await Call("terrain.reference.promote", new { draftVersion = Draft(draft), cityId = beta.Id, term = "BTX", requiredContext = "atelier" });
    simulated = await Call("terrain.reference.simulate", new { draftVersion = Draft(draft), title = "atelier BTX", context = new { } });
    Check(simulated.GetProperty("match").GetProperty("cityId").GetString() == beta.Id, "correction promotion creates a contextual draft rule");
    simulated = await Call("terrain.reference.simulate", new { draftVersion = Draft(draft), title = "BTX", context = new { } });
    Check(simulated.GetProperty("match").GetProperty("state").GetInt32() != (int)CityMatchState.Matched, "promoted context is required, not an unconditional alias");
    var preview = await Call("terrain.reference.preview", new { draftVersion = Draft(draft), revision = Rev() });
    Check(preview.GetProperty("totalRows").GetInt32() == 4 && app.Settings.Cities.Buildings.Count == 0, "full-source preview does not persist the draft");
    var alteredDashboard = app.EffectiveSettings.Dashboard.Copy(); alteredDashboard.ColumnMappings["title"] = "Résumé terrain";
    var mappingRevision = Rev();
    await Call("mapping.save", new { revision = mappingRevision, settings = alteredDashboard });
    Check(ReferenceEquals(app.Result, source) && Rev() > mappingRevision && app.EffectiveSettings.Dashboard.ColumnMappings["title"] == "Résumé terrain",
        "public mapping save changes effective interpretation and revision while preserving the extraction");
    await Reject(() => Call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true }), "preview invalidated by mapping or interpretation change without a new extraction");
    preview = await Call("terrain.reference.preview", new { draftVersion = Draft(draft), revision = Rev() });
    draft = await Call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true });
    Check(app.Settings.Cities.Buildings.Count == 1 && File.Exists(AppPaths.SettingsFile) && app.Result!.Headers.Count > source.Headers.Count, "confirmed preview persists references and publishes enriched result");
    Check(app.Locations!.Source.Headers.Count == source.Headers.Count && app.Locations.Source.Rows[0][1] == source.Rows[0][1], "reapplication preserves original source cells");

    host.SavePath = Path.Combine(root, "reference.csv");
    await Call("terrain.reference.export", new { draftVersion = Draft(draft) });
    Check(File.Exists(host.SavePath) && File.ReadAllText(host.SavePath).Contains("Bâtiment Érable", StringComparison.Ordinal), "CSV reference export uses a host-selected destination");
    host.OpenPath = host.SavePath;
    var imported = await Call("terrain.reference.import", new { draftVersion = Draft(draft) });
    Check(imported.GetProperty("additions").GetInt32() == 0 && imported.GetProperty("modifications").GetInt32() == 0, "CSV round trip produces a real no-op merge preview");
    draft = await Call("terrain.reference.importApply", new { token = imported.GetProperty("token").GetString(), confirmed = true });
    Check(!draft.GetProperty("dirty").GetBoolean(), "identical import leaves saved geography unchanged");
    await Reject(() => Call("terrain.review.correct", new { revision = Rev(), row = 2, cityId = alpha.Id, siteId = "", reason = "" }), "local correction requires an explicit reason");
    await Call("terrain.review.correct", new { revision = Rev(), row = 2, cityId = alpha.Id, siteId = campus.Id, reason = "Lieu confirmé auprès du contact" });
    review = await Call("terrain.review.detail", new { revision = Rev(), row = 2 });
    Check(review.GetProperty("match").GetProperty("isOverride").GetBoolean() && review.GetProperty("correction").GetProperty("reason").GetString() == "Lieu confirmé auprès du contact", "correction persists its reason and overrides ambiguous matching");
    var overrides = CityOverrideStore.Load(Path.Combine(root, "city-overrides.json"));
    Check(overrides.Overrides.Single().TicketId == "3" && overrides.Overrides.Single().SourceKey == "https://fixture.invalid", "correction scoped to installation and unique ticket identity");
    await Call("terrain.review.correct", new { revision = Rev(), row = 2, cityId = "", siteId = "", reason = "" });
    Check(CityOverrideStore.Load(Path.Combine(root, "city-overrides.json")).Overrides.Count == 0, "removing correction restores detector behavior");

    var plan = await Call("terrain.tours.projection", new { revision = Rev(), order = "priority" });
    Check(plan.GetProperty("tickets").GetInt32() == 3 && plan.GetProperty("ambiguous").GetInt32() == 1 && Items(plan).GetArrayLength() == 2, "tour projection preserves active scope and ambiguity accounting");
    await Call("terrain.tours.projectionMove", new { revision = Rev(), cityId = beta.Id, delta = -1 });
    await Call("terrain.tours.projectionSave", new { revision = Rev() });
    Check(File.Exists(Path.Combine(root, "tour-order.json")), "manual city ordering persists per projection scope");
    var basket = await Call("terrain.tours.basket", new { revision = Rev(), limit = 1 });
    Check(Items(basket).GetArrayLength() == 1 && basket.GetProperty("total").GetInt32() == 3, "ticket basket listing is paginated on server");
    await Call("terrain.tours.select", new { revision = Rev(), rows = new[] { 0, 1 }, selected = true });
    await Reject(() => Call("terrain.tours.select", new { revision = Rev(), rows = new[] { 99 }, selected = true }), "invalid basket row refused before mutation");
    var alice = TechnicianDashboardService.IdentityKey("Alice");
    var preference = new TechnicianPreference { Skills = new() { "Réseau" }, WorkPeriods = new() {
        new() { Day = DayOfWeek.Monday, StartMinute = 8 * 60, EndMinute = 12 * 60, CapacityMinutes = 240 },
        new() { Day = DayOfWeek.Monday, StartMinute = 13 * 60, EndMinute = 17 * 60, CapacityMinutes = 240 } },
        CapacityDays = new() { new() { Date = new DateTime(2030, 1, 7), MorningMinutes = 60, AfternoonMinutes = 240 } } };
    new TechnicianPreferences().With(alice, preference).Save(Path.Combine(root, "technician-preferences.json"));
    var tour = await Call("terrain.tours.create", new { revision = Rev(), name = "Lundi terrain", date = "2030-01-07", technician = alice });
    var tourId = tour.GetProperty("draft").GetProperty("id").GetString();
    Check(tour.GetProperty("draft").GetProperty("tickets").GetInt32() == 2 && tour.GetProperty("draft").GetProperty("stops").GetInt32() == 2, "basket creates a real dated tour with unique ticket references");
    var stops = await Call("terrain.tours.stops", new { revision = Rev(), draftVersion = Draft(tour), limit = 20 });
    var stop = Items(stops)[0].GetProperty("stop");
    Check(stop.GetProperty("contact").GetString()!.Contains("Accueil", StringComparison.Ordinal) && stop.GetProperty("equipment").GetString()!.Contains("Adaptateur", StringComparison.Ordinal), "field contact and equipment arrive from the site reference");
    var stopFields = new { date = "", technicianKey = alice, durationMinutes = 30, travelMinutes = 15, earliest = "", latest = "", locked = true, notes = "Intervention contrôlée", requiredSkills = new[] { "Réseau" }, contact = "Accueil", equipment = "Adaptateur" };
    tour = await Call("terrain.tours.stopEdit", new { revision = Rev(), draftVersion = Draft(tour), stopId = stop.GetProperty("id").GetString(), fields = stopFields });
    await Reject(() => Call("terrain.tours.move", new { revision = Rev(), draftVersion = Draft(tour), stopId = stop.GetProperty("id").GetString(), delta = 1 }), "locked stage blocks movement through the bridge");
    var capacity = await Call("terrain.tours.capacity", new { revision = Rev(), draftVersion = Draft(tour), technicians = new[] { alice }, days = 1 });
    Check(Items(capacity)[0].GetProperty("morningCapacityMinutes").GetInt32() == 60 && Items(capacity)[0].GetProperty("afternoonCapacityMinutes").GetInt32() == 240, "half-day capacity exceptions are surfaced by the bridge");
    var allocation = await Call("terrain.tours.allocate", new { revision = Rev(), draftVersion = Draft(tour), technicians = new[] { alice }, days = 1 }); tour = allocation.GetProperty("state");
    Check(allocation.GetProperty("assignedStops").GetInt32() == 2 && allocation.GetProperty("unassigned").GetArrayLength() == 0, "multi-day allocation uses declared presence and capacities");
    var calculation = await Call("terrain.tours.calculate", new { revision = Rev(), draftVersion = Draft(tour) });
    var starts = Items(calculation).EnumerateArray().Select(value => value.GetProperty("start").GetDateTime()).ToArray();
    Check(starts.Length == 2 && starts[0].Hour < 12 && starts[1].Hour >= 13 && calculation.GetProperty("totals").GetProperty("unavailable").GetInt32() == 0, "morning budget exhaustion schedules the next visit inside afternoon presence");
    tour = await Call("terrain.tours.save", new { revision = Rev(), draftVersion = Draft(tour) });
    Check(!tour.GetProperty("dirty").GetBoolean() && events.Contains("tours.changed"), "saved tour publishes the event consumed by My Day");
    var day = await Call("terrain.tours.day", new { date = "2030-01-07", technician = alice });
    Check(day.GetProperty("total").GetInt32() == 1 && Items(day)[0].GetProperty("ticketIds").GetArrayLength() == 2, "day calendar consumes persisted tour visits");
    tour = await Call("terrain.tours.transition", new { revision = Rev(), draftVersion = Draft(tour), state = "ready", reason = "Planning et contact vérifiés" });
    Check(tour.GetProperty("draft").GetProperty("state").GetString() == "ready", "ready transition revalidates live planning constraints");
    await Reject(() => Call("terrain.tours.transition", new { revision = Rev(), draftVersion = Draft(tour), state = "done", reason = "Trop tôt" }), "future visits cannot be declared performed");
    host.SavePath = Path.Combine(root, "tour.ics"); await Call("terrain.tours.export", new { revision = Rev(), draftVersion = Draft(tour), format = "ics" });
    Check(File.ReadAllText(host.SavePath).Contains("BEGIN:VEVENT", StringComparison.Ordinal), "actual ICS calendar exported via host picker");
    host.SavePath = Path.Combine(root, "tour.html"); await Call("terrain.tours.export", new { revision = Rev(), draftVersion = Draft(tour), format = "html" });
    Check(File.ReadAllText(host.SavePath).Contains("Accueil", StringComparison.Ordinal), "actual briefing HTML includes field contact");
    var comparison = await Call("terrain.tours.compare", new { revision = Rev(), draftVersion = Draft(tour), otherId = tourId });
    Check(comparison.GetProperty("sameBasket").GetBoolean(), "scenario comparison identifies matching ticket baskets");
    var services = await Call("terrain.services.get");
    Check(services.GetProperty("routingEndpoint").GetString() == "" && services.GetProperty("weatherEndpoint").GetString() == "", "external providers are disabled by empty defaults");
    await Reject(() => Call("terrain.tours.route", new { revision = Rev(), draftVersion = Draft(tour), consent = false }), "route refuses network without explicit consent");
    await Reject(() => Call("terrain.tours.weather", new { revision = Rev(), draftVersion = Draft(tour), consent = true, consentEndpoint = "https://other.invalid/", settings = new TourServiceSettings { WeatherEndpoint = "https://fixture.invalid/weather" } }), "consent cannot be reused for a changed provider address");
    await Reject(() => Call("terrain.services.save", new { settings = new TourServiceSettings { RoutingEndpoint = "https://user:secret@fixture.invalid/" } }), "provider URLs cannot contain credentials");
    preference.CapacityDays[0].MorningMinutes = 0; preference.CapacityDays[0].AfternoonMinutes = 0;
    new TechnicianPreferences().With(alice, preference).Save(Path.Combine(root, "technician-preferences.json"));
    tour = await Call("terrain.tours.save", new { revision = Rev(), draftVersion = Draft(tour) });
    Check(tour.GetProperty("draft").GetProperty("state").GetString() == "draft", "changed availability returns a validated tour to draft at save");
    await Reject(() => Call("terrain.tours.transition", new { revision = Rev(), draftVersion = Draft(tour), state = "ready", reason = "Impossible" }), "zero declared capacity blocks ready state");
    var persisted = ScheduledTourStore.Load(Path.Combine(root, "scheduled-tours.json")); var changedTour = persisted.Tours[0].Copy(); changedTour.Name = "Modification concurrente"; persisted.With(changedTour).Save(Path.Combine(root, "scheduled-tours.json"));
    await Reject(() => Call("terrain.tours.save", new { revision = Rev(), draftVersion = Draft(tour) }), "concurrent tour changes cannot be overwritten silently");
    await Call("terrain.tours.select", new { revision = Rev(), rows = new[] { 0 }, selected = true });
    app.LoadFixture(source);
    var refreshed = await Call("terrain.tours.state"); Check(refreshed.GetProperty("selected").GetInt32() == 0, "new extraction clears stale basket row indices");

    var update = await Call("update.state");
    Check(!update.GetProperty("automatic").GetBoolean() && !update.GetProperty("policy").GetProperty("requireSignature").GetBoolean(), "update state performs no automatic network or signature claims");
    await Reject(() => Call("update.findOfficial", new { consent = false }), "GitHub release lookup requires an explicit action");
    await Reject(() => Call("update.downloadOfficial", new { consent = true, token = "invented" }), "download cannot use a fabricated release token");
    await Reject(() => Call("update.prepareLocal", new { path = "/arbitrary/file.zip", sha256 = new string('A', 64), commit = new string('B', 40) }), "ZIP path cannot be injected through web arguments");
    await Reject(() => Call("update.install", new { confirmed = true, token = "invented", version = "99.0.0" }), "installation requires a prepared verified pack");
    await Reject(() => Call("update.policy", new { policy = new UpdatePolicy { RequireSignature = true, TrustedPublisherSha256 = "invalid" } }), "signature policy requires a complete certificate digest");
    update = await Call("update.policy", new { policy = new UpdatePolicy { RequireSignature = true, TrustedPublisherSha256 = new string('A', 64) } });
    Check(update.GetProperty("policy").GetProperty("requireSignature").GetBoolean(), "configured publisher policy is persisted");
    await Reject(() => Call("update.prepareRollback", new { confirmed = true }), "rollback without a verified previous installation is refused");
    await Call("update.openReleases");
    Check(host.ExternalUrls.SequenceEqual(new[] { UpdateReleaseClient.ReleasesPage }) && host.Closures == 0, "only fixed official release navigation is offered; no install launched");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Reject(() => Call("terrain.reference.upsert", new { }, cancelled.Token), "cancelled bridge request stops before mutation");
    Check(AppPaths.DataRoot == root && events.Contains("session.changed"), "all fixture persistence stays in explicitly isolated test profile");
    await StandaloneSiteBridgeChecks.RunAsync(root, Check, Reject);
    await TerrainDraftRefreshChecks.RunAsync(root, Check, Reject);
    Console.WriteLine($"{passed}/{passed} terrain bridge checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", null); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", null);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
