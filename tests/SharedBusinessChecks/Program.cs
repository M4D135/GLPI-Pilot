using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

var checks = 0; var failures = new List<string>();
var share = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Common-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(share);
var cityId = Guid.NewGuid().ToString("N"); var siteId = Guid.NewGuid().ToString("N");
var baseSettings = new AppSettings { PortalUrl = "https://shared-glpi.test/front/ticket.php?criteria=active" };
baseSettings.Normalize();
baseSettings.Cities.Enabled = true;
baseSettings.Cities.Cities.Add(new() { Id = cityId, Name = "Ville témoin", Aliases = new() { "VLT" }, Latitude = 43.3, Longitude = 5.4 });
baseSettings.Cities.Sites.Add(new() { Id = siteId, CityId = cityId, Name = "Agence témoin", Aliases = new() { "AGT" }, Address = "12 rue témoin", Latitude = 43.3, Longitude = 5.4 });
baseSettings.Dashboard.ColumnMappings = new() { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut", ["assignee"] = "Attribué à - Technicien", ["group"] = "Attribué à - Groupe de techniciens", ["description"] = "Description" };
var alice = TechnicianDashboardService.IdentityKey("Alice");
await using var a = new Fixture(baseSettings, share, "Poste A");
await using var b = new Fixture(baseSettings, share, "Poste B");
await using var c = new Fixture(baseSettings, share, "Poste C");
try
{
    await Check("Local-only mode keeps all data local and never prepares a shared exchange", async () =>
    {
        await using var local = new Fixture(baseSettings, null, "Local");
        await Reject(() => local.Call("library.sync"));
        Require(!File.Exists(Path.Combine(local.Root, "shared-business.local.json")), "A disabled sharing mode staged common documents.");
        var status = await local.Call("sharing.business.status"); Require(!status.GetProperty("enabled").GetBoolean() && status.GetProperty("documents").GetInt32() == 0, "Local mode must be explicit.");
    });
    await Check("The same site registry and rules arrive automatically while chosen identity remains personal", async () =>
    {
        a.InProfile(() => { a.App.Settings.Dashboard.StaleDays = 13; a.App.Settings.Dashboard.PersonalTechnician = "personal-A-not-shared"; });
        b.InProfile(() => b.App.Settings.Dashboard.PersonalTechnician = "personal-B-not-shared");
        await a.Call("library.sync"); await b.Call("library.sync");
        Require(b.App.Settings.Dashboard.StaleDays == 13 && b.App.Settings.Cities.Sites.Single().Id == siteId && b.App.Settings.Cities.Sites.Single().Aliases.Contains("AGT"), "Shared configuration was not applied.");
        Require(b.App.Settings.Dashboard.PersonalTechnician == "personal-B-not-shared", "A remote identity replaced the personal filter.");
    });
    var tour = new ScheduledTour { Id = Guid.NewGuid().ToString("N"), Name = "Tournée commune", SourceKey = "https://shared-glpi.test", Date = "2026-11-02", TimeZoneId = "Europe/Paris", TechnicianKey = alice,
        Tickets = new() { new() { TicketId = "1", PlaceKey = "site:" + siteId, BaselineFingerprint = new string('a', 64), BaselineStatus = 2 } },
        Stops = new() { new() { SiteId = siteId, CityId = cityId, TicketIds = new() { "1" }, TechnicianKey = alice, DurationMinutes = 45, TravelMinutes = 15 } } };
    await Check("Tours retain one canonical identity across profiles and edits never create a local copy", async () =>
    {
        a.InProfile(() => new ScheduledTourStore { Tours = new() { tour.Copy() } }.Save(a.Path("scheduled-tours.json")));
        await a.Call("library.sync"); await b.Call("library.sync");
        var received = ScheduledTourStore.Load(b.Path("scheduled-tours.json")).Tours.Single(); Require(received.Id == tour.Id, "A shared tour was duplicated.");
        received.Name = "Tournée ajustée sur B"; b.InProfile(() => new ScheduledTourStore { Tours = new() { received } }.Save(b.Path("scheduled-tours.json")));
        await b.Call("library.sync"); await a.Call("library.sync");
        Require(ScheduledTourStore.Load(a.Path("scheduled-tours.json")).Tours.Single().Name == received.Name, "A changed shared tour did not return to A.");
    });
    await Check("Business capacities and skills synchronize without private technician notes or favourites", async () =>
    {
        a.InProfile(() => new TechnicianPreferences().With(alice, new() { DailyCapacityMinutes = 420, Skills = new() { "Réseau" }, Notes = "PRIVATE-A-NOTE", Favorite = true }).Save(a.Path("technician-preferences.json")));
        b.InProfile(() => new TechnicianPreferences().With(alice, new() { Notes = "PRIVATE-B-NOTE", Favorite = true, ActiveTarget = 8 }).Save(b.Path("technician-preferences.json")));
        await a.Call("library.sync"); await b.Call("library.sync");
        var received = TechnicianPreferences.Load(b.Path("technician-preferences.json")).Get(alice);
        Require(received.DailyCapacityMinutes == 420 && received.Skills.SequenceEqual(new[] { "Réseau" }), "Business capacity or skills missing.");
        Require(received.Notes == "PRIVATE-B-NOTE" && received.Favorite && received.ActiveTarget == 8, "Private fields were overwritten.");
    });
    await Check("Ticket location corrections and saved tour order become common data", async () =>
    {
        a.InProfile(() =>
        {
            new CityOverrideStore { Overrides = new() { new() { SourceKey = "https://shared-glpi.test", TicketId = "1", CityId = cityId, SiteId = siteId, Reason = "Précision sur place" } } }.Save(a.Path("city-overrides.json"));
            new TourOrderPreferences().With(TourOrderPreferences.ScopeKey(alice, false), new[] { cityId }).Save(a.Path("tour-order.json"));
        });
        await a.Call("library.sync"); await b.Call("library.sync");
        Require(CityOverrideStore.Load(b.Path("city-overrides.json")).Overrides.Single().SiteId == siteId, "Location correction not received.");
        Require(TourOrderPreferences.Load(b.Path("tour-order.json")).Get(TourOrderPreferences.ScopeKey(alice, false)).SequenceEqual(new[] { cityId }), "Saved tour order not received.");
    });
    await Check("A shared location removal and reintroduction replace their tombstones atomically", async () =>
    {
        const string key = "https://shared-glpi.test"; const string ticket = "correction-roundtrip";
        a.InProfile(() => CityOverrideStore.Load(a.Path("city-overrides.json")).With(new()
            { CityId = cityId, SiteId = siteId, Reason = "Correction explicite partagée", Explicit = true }, key, ticket).Save(a.Path("city-overrides.json")));
        await a.Call("library.sync"); await b.Call("library.sync");
        Require(CityOverrideStore.Load(b.Path("city-overrides.json")).Overrides.Single(value => value.TicketId == ticket).Explicit, "Explicit correction flag did not arrive.");
        b.InProfile(() => CityOverrideStore.Load(b.Path("city-overrides.json")).With(null, key, ticket).Save(b.Path("city-overrides.json")));
        await b.Call("library.sync"); await a.Call("library.sync");
        var removed = CityOverrideStore.Load(a.Path("city-overrides.json"));
        Require(removed.Overrides.All(value => value.TicketId != ticket) && removed.RemovedCorrections.Any(value => value.TicketId == ticket), "Remote removal did not retain a cache-safe tombstone.");
        a.InProfile(() => CityOverrideStore.Load(a.Path("city-overrides.json")).With(new()
            { CityId = cityId, SiteId = siteId, Reason = "Site confirmé à nouveau", Explicit = true }, key, ticket).Save(a.Path("city-overrides.json")));
        await a.Call("library.sync"); await b.Call("library.sync");
        var restored = CityOverrideStore.Load(b.Path("city-overrides.json"));
        Require(restored.Overrides.Single(value => value.TicketId == ticket).Reason == "Site confirmé à nouveau"
            && restored.RemovedCorrections.All(value => value.TicketId != ticket), "Remote reintroduction retained a conflicting tombstone.");
    });
    var table = Fixture.Table();
    await Check("A complete nineteen-column observation loads on another machine with literal rows and provenance", async () =>
    {
        a.InProfile(() => a.App.SharedBusinessFixtureLoad(table)); await a.Call("library.sync"); await b.Call("library.sync");
        var received = b.App.Result ?? throw new Exception("A matching shared source was not loaded automatically.");
        Require(received.Headers.SequenceEqual(table.Headers) && received.Rows.Count == table.Rows.Count && received.Rows.Zip(table.Rows).All(pair => pair.First.SequenceEqual(pair.Second)), "Source fields were lost or reinterpreted.");
        Require(received.Provenance.SourceKind == "local" && received.Provenance.Method == "shared-records", "Shared data masquerades as a new live acquisition.");
        Require(!received.SourceUrl.Contains("access_token", StringComparison.Ordinal) && received.SourceUrl.Contains("criteria=active", StringComparison.Ordinal), "URL credentials leaked or the search disappeared.");
        var list = await b.Call("sharing.datasets"); Require(list.GetProperty("items")[0].GetProperty("columns").GetInt32() == 19, "Common observation metadata is wrong.");
    });
    await Check("A followed common extract refreshes while an explicitly selected other source stays in place", async () =>
    {
        var changed = Fixture.Table(); changed.Rows[0][1] = "Titre actualisé"; changed.ExtractedAt = table.ExtractedAt.AddMinutes(1);
        a.InProfile(() => a.App.SharedBusinessFixtureLoad(changed)); await a.Call("library.sync"); await b.Call("library.sync");
        Require(b.App.Result!.Rows[0][1] == "Titre actualisé", "Followed common extract did not refresh.");
        var other = Fixture.Table(); other.SourceUrl = "https://another-glpi.test/front/ticket.php"; other.Rows[0][1] = "Autre source choisie";
        b.InProfile(() => b.App.SharedBusinessFixtureLoad(other));
        changed = Fixture.Table(); changed.Rows[0][1] = "Version commune suivante"; changed.ExtractedAt = table.ExtractedAt.AddMinutes(2);
        a.InProfile(() => a.App.SharedBusinessFixtureLoad(changed)); await a.Call("library.sync"); await b.Call("library.sync");
        Require(ReferenceEquals(b.App.Result, other) && b.App.Result.Rows[0][1] == "Autre source choisie", "A background pull replaced a deliberately selected source.");
    });
    await Check("Disconnected edits enter a durable local queue and publish after the share returns", async () =>
    {
        a.InProfile(() => { var prefs = TechnicianPreferences.Load(a.Path("technician-preferences.json")); var value = prefs.Get(alice); value.DailyCapacityMinutes = 390; prefs.With(alice, value).Save(a.Path("technician-preferences.json")); });
        var moved = share + "-disconnected"; Directory.Move(share, moved); File.WriteAllText(share, "network endpoint unavailable");
        try
        {
            await Reject(() => a.Call("library.sync")); var status = await a.Call("library.get");
            Require(status.GetProperty("sync").GetProperty("state").GetString() == "offline" && status.GetProperty("sync").GetProperty("pending").GetInt64() > 0, "Offline state or durable queue missing.");
            Require(TechnicianPreferences.Load(a.Path("technician-preferences.json")).Get(alice).DailyCapacityMinutes == 390, "Offline edit was lost.");
        }
        finally { File.Delete(share); Directory.Move(moved, share); }
        await a.Call("library.sync"); await b.Call("library.sync");
        Require(TechnicianPreferences.Load(b.Path("technician-preferences.json")).Get(alice).DailyCapacityMinutes == 390, "Queued offline edit was not delivered.");
    });
    await Check("Competing offline edits preserve both proposals and one explicit decision converges on three profiles", async () =>
    {
        SetCapacity(a, 300); SetCapacity(b, 360);
        await a.Call("library.sync"); await b.Call("library.sync"); await a.Call("library.sync"); await c.Call("library.sync");
        var conflicts = (await b.Call("sharing.business.status")).GetProperty("conflicts").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "team-technician").ToArray();
        Require(conflicts.Length == 1, "Both versions must remain reviewable."); var row = conflicts.Single();
        var review = await b.Call("sharing.business.conflict", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString() });
        Require(review.GetProperty("current").GetProperty("data").GetProperty("dailyCapacityMinutes").GetInt32() == 300 && review.GetProperty("incoming").GetProperty("data").GetProperty("dailyCapacityMinutes").GetInt32() == 360, "Conflict comparison lost one proposal.");
        await b.Call("sharing.business.resolve", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString(), expectedRevision = review.GetProperty("expectedRevision").GetInt64(), choice = "incoming" });
        await b.Call("library.sync"); await a.Call("library.sync"); await c.Call("library.sync");
        foreach (var fixture in new[] { a, b, c })
        {
            Require(TechnicianPreferences.Load(fixture.Path("technician-preferences.json")).Get(alice).DailyCapacityMinutes == 360, "Resolution did not converge.");
            Require(!(await fixture.Call("sharing.business.status")).GetProperty("conflicts").EnumerateArray().Any(x => x.GetProperty("kind").GetString() == "team-technician"), "Resolved versions remain falsely outstanding on a peer.");
        }
    });
    await Check("A stale conflict review cannot overwrite a more recent business edit", async () =>
    {
        SetCapacity(a, 330); SetCapacity(b, 370); await a.Call("library.sync"); await b.Call("library.sync");
        var row = (await b.Call("sharing.business.status")).GetProperty("conflicts").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "team-technician");
        var review = await b.Call("sharing.business.conflict", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString() });
        SetCapacity(b, 380); await b.Call("library.sync");
        await Reject(() => b.Call("sharing.business.resolve", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString(), expectedRevision = review.GetProperty("expectedRevision").GetInt64(), choice = "incoming" }));
        Require(TechnicianPreferences.Load(b.Path("technician-preferences.json")).Get(alice).DailyCapacityMinutes == 380, "Stale comparison overwrote the fresh edit.");
    });
    await Check("Deleting a common tour propagates its tombstone without recreating it on another profile", async () =>
    {
        a.InProfile(() => new ScheduledTourStore().Save(a.Path("scheduled-tours.json"))); await a.Call("library.sync"); await b.Call("library.sync"); await b.Call("library.sync"); await a.Call("library.sync");
        Require(ScheduledTourStore.Load(a.Path("scheduled-tours.json")).Tours.Count == 0 && ScheduledTourStore.Load(b.Path("scheduled-tours.json")).Tours.Count == 0, "Removed tour was resurrected.");
    });
    await Check("Common envelopes exclude personal identities, private notes, connection secrets and browser profiles", () =>
    {
        var texts = string.Join("\n", Directory.EnumerateFiles(share, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var secret in new[] { "PRIVATE-A-NOTE", "PRIVATE-B-NOTE", "personal-A-not-shared", "personal-B-not-shared", "TOKEN-TO-STRIP" })
            Require(!texts.Contains(secret, StringComparison.Ordinal), "Private data reached a common document: " + secret);
        Require(!Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories).Any(x => x.EndsWith(".db", StringComparison.Ordinal)), "SQLite was placed in the shared folder."); return Task.CompletedTask;
    });
    await Check("Malformed or oversized compressed observations are refused before they replace any local source", async () =>
    {
        var dataset = SharedBusinessDocuments.Dataset(Fixture.Table(), baseSettings.Dashboard); dataset.ExpandedBytes = 1;
        var malformed = SharedBusinessDocuments.Create("team-dataset", dataset.SourceKey, "Relevé altéré", dataset);
        await Reject(() => Task.Run(() => SharedBusinessDocuments.OpenDataset(malformed)));
        dataset = SharedBusinessDocuments.Dataset(Fixture.Table(), baseSettings.Dashboard); dataset.Sha256 = new string('0', 64);
        malformed = SharedBusinessDocuments.Create("team-dataset", dataset.SourceKey, "Empreinte altérée", dataset);
        await Reject(() => Task.Run(() => SharedBusinessDocuments.OpenDataset(malformed)));
    });
    await Check("Simulation exercises common business sync without writing to the configured real share", async () =>
    {
        var before = Fingerprint(share); await a.Call("app.demo"); await a.Call("library.sync");
        Require((await a.Call("sharing.datasets")).GetProperty("items").GetArrayLength() > 0, "Simulation did not exercise shared records.");
        await a.Call("records.test.exit"); Require(before == Fingerprint(share), "Simulation changed the real common workspace.");
    });
    await Check("An unsynchronized local edit invalidates a conflict decision before any overwrite", async () =>
    {
        var row = (await b.Call("sharing.business.status")).GetProperty("conflicts").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "team-technician");
        var review = await b.Call("sharing.business.conflict", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString() });
        SetCapacity(b, 371);
        await Reject(() => b.Call("sharing.business.resolve", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString(), expectedRevision = review.GetProperty("expectedRevision").GetInt64(), choice = "incoming" }));
        Require(TechnicianPreferences.Load(b.Path("technician-preferences.json")).Get(alice).DailyCapacityMinutes == 371, "An edit not yet in SQLite was overwritten by a stale comparison.");
        await b.Call("library.sync");
    });
    await Check("New common thresholds immediately recalculate an already loaded shared extract", async () =>
    {
        var data = Fixture.Table(); data.ExtractedAt = DateTimeOffset.UtcNow; data.Rows[0][3] = DateTime.UtcNow.AddDays(-10).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        // The fixture opening date (30/10/2026 09:00) is in the past from that day on: keep the opening before the modification.
        data.Rows[0][4] = DateTime.UtcNow.AddDays(-20).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        a.InProfile(() => { a.App.Settings.Dashboard.StaleDays = 5; a.App.SharedBusinessFixtureLoad(data); }); await a.Call("library.sync"); await b.Call("library.sync");
        var dataset = (await b.Call("sharing.datasets")).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("urlKey").GetString() == SnapshotStore.SourceKey(baseSettings.PortalUrl));
        await b.Call("sharing.dataset.load", new { id = dataset.GetProperty("id").GetString(), expectedRevision = dataset.GetProperty("revision").GetInt64() });
        var before = await b.Call("workplace.overview"); Require(before.GetProperty("metrics").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "stale").GetProperty("value").GetInt32() == 1, "The initial threshold should include this unchanged ticket.");
        a.InProfile(() => a.App.Settings.Dashboard.StaleDays = 20); await a.Call("library.sync"); await b.Call("library.sync");
        var after = await b.Call("workplace.overview");
        Require(b.App.EffectiveSettings.Dashboard.StaleDays == 20 && after.GetProperty("metrics").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "stale").GetProperty("value").GetInt32() == 0, "The existing shared table retained an obsolete dashboard mapping.");
        Require(b.App.EffectiveSettings.Dashboard.ColumnMappings["id"] == "ID", "Refreshing rules must preserve the dataset column mapping.");
    });
    await Check("Following after map enrichment and receiving a correction preserve exactly the original source columns", async () =>
    {
        b.InProfile(() => b.App.SharedBusinessFixtureEnrich()); Require(b.App.Locations is not null, "Map enrichment fixture did not apply.");
        var data = Fixture.Table(); data.Rows[0][1] = "AGT — nouvelle observation après carte"; a.InProfile(() => a.App.SharedBusinessFixtureLoad(data));
        await a.Call("library.sync"); await b.Call("library.sync"); Require(b.App.Result!.Rows[0][1] == data.Rows[0][1], "Looking at the map broke automatic following.");
        b.InProfile(() => b.App.SharedBusinessFixtureEnrich());
        a.InProfile(() => { var corrections = CityOverrideStore.Load(a.Path("city-overrides.json")); corrections.Overrides[0].Reason = "Localisation révisée"; corrections.Save(a.Path("city-overrides.json")); });
        await a.Call("library.sync"); await b.Call("library.sync");
        Require(b.App.Result!.Headers.Count == 19 && b.App.Locations is null, "A received correction retained a derived table as the raw source.");
        await b.Call("library.sync"); Require((await b.Call("sharing.datasets")).GetProperty("items").EnumerateArray().All(x => x.GetProperty("columns").GetInt32() == 19), "Derived geography columns leaked into a shared extract.");
    });
    await Check("Native capture URLs and different verified entity scopes stay distinct and require a deliberate choice", async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Scopes-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            await using var first = new Fixture(baseSettings, folder, "Entité A"); await using var second = new Fixture(baseSettings, folder, "Entité B"); await using var receiver = new Fixture(baseSettings, folder, "Réception");
            var firstData = Fixture.Table(); firstData.SourceUrl = "https://shared-glpi.test/front/ticket.php"; firstData.Provenance.RequestedUrl = baseSettings.PortalUrl;
            var secondData = Fixture.Table(); secondData.SourceUrl = firstData.SourceUrl; secondData.Provenance.RequestedUrl = baseSettings.PortalUrl; secondData.Provenance.ScopeFingerprint = "entity-two-profile-support"; secondData.Rows[0][0] = "999";
            first.InProfile(() => first.App.SharedBusinessFixtureLoad(firstData)); second.InProfile(() => second.App.SharedBusinessFixtureLoad(secondData));
            await first.Call("library.sync"); await second.Call("library.sync"); await receiver.Call("library.sync");
            var list = (await receiver.Call("sharing.datasets")).GetProperty("items").EnumerateArray().ToArray();
            Require(list.Length == 2 && list.Select(x => x.GetProperty("sourceKey").GetString()).Distinct().Count() == 2, "Different GLPI entitlements were conflated by their shared URL.");
            Require(list.All(x => x.GetProperty("urlKey").GetString() == SnapshotStore.SourceKey(baseSettings.PortalUrl)) && receiver.App.Result is null, "A native filtered search was lost or an ambiguous scope was silently selected.");
        }
        finally { Directory.Delete(folder, true); }
    });
    await Check("A first empty profile adopts the common configuration without publishing random starter boards", async () =>
    {
        var settings = new AppSettings { PortalUrl = baseSettings.PortalUrl }; settings.Normalize();
        Require(SharedBusinessDocuments.IsDefaultConfiguration(settings), "Default layout identity must not count as a deliberate business configuration.");
        var replica = new FolderTeamWorkspaceReplica(share); var before = (await replica.ChangesAsync(0, 200)).Count(x => x.Incoming is not null && x.Item.Kind == "team-configuration");
        await using var empty = new Fixture(settings, share, "Nouveau poste"); await empty.Call("library.sync"); await empty.Call("library.sync");
        var after = (await replica.ChangesAsync(0, 200)).Count(x => x.Incoming is not null && x.Item.Kind == "team-configuration");
        Require(empty.App.Settings.Cities.Sites.Single().Id == siteId && before == after, "A fresh profile manufactured a starter-configuration conflict.");
    });
    await Check("Changing to an existing empty local database and another share reseeds unchanged business files", async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-NewShare-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            await using var mover = new Fixture(baseSettings, folder, "Poste déplacé"); mover.InProfile(() => mover.App.SharedBusinessFixtureLoad(Fixture.Table())); await mover.Call("library.sync");
            var target = mover.Path("OtherLibrary"); await new TicketLibraryStore(Path.Combine(target, "glpi-pilot-library.db")).InitializeAsync(); await new TeamWorkspaceStore(Path.Combine(target, "glpi-pilot-library.db")).InitializeAsync();
            var nextShare = Path.Combine(folder, "destination"); Directory.CreateDirectory(nextShare);
            var preferences = (await mover.Call("library.get")).GetProperty("settings").Deserialize<LibraryPreferences>(PilotApplication.Json)!; preferences.LocalFolder = target; preferences.SharedFolder = nextShare;
            await mover.Call("library.settings.save", new { settings = preferences }); await mover.Call("library.sync");
            var changes = await new FolderTeamWorkspaceReplica(nextShare).ChangesAsync(0, 200);
            Require(changes.Any(x => x.Item.Kind == "team-configuration") && changes.Any(x => x.Item.Kind == "team-dataset"), "Old local markers skipped unchanged data in a new empty database.");
        }
        finally { Directory.Delete(folder, true); }
    });
    await Check("A refused local observation remains reviewable and its selected version becomes the followed dataset", async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-DatasetConflict-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            await using var first = new Fixture(baseSettings, folder, "Collecte A"); await using var second = new Fixture(baseSettings, folder, "Collecte B");
            var left = Fixture.Table(); left.Rows[0][1] = "Observation A"; var right = Fixture.Table(); right.Rows[0][1] = "Observation B";
            first.InProfile(() => first.App.SharedBusinessFixtureLoad(left)); second.InProfile(() => second.App.SharedBusinessFixtureLoad(right));
            await first.Call("library.sync"); await second.Call("library.sync");
            Require(ReferenceEquals(second.App.Result, right), "A refused live observation should remain open until an explicit choice.");
            var row = (await second.Call("sharing.business.status")).GetProperty("conflicts").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "team-dataset");
            var archived = await new TeamWorkspaceStore(second.Path("Library/glpi-pilot-library.db")).GetConflictAsync(row.GetProperty("id").GetString()!, row.GetProperty("conflictId").GetString()!);
            var visible = JsonSerializer.SerializeToElement(SharedBusinessDocuments.Dataset(second.App.Result!, second.App.EffectiveSettings.Dashboard), SharedBusinessDocuments.Json);
            var proposal = SharedBusinessDocuments.Read(archived!.Incoming).Data;
            var differing = visible.EnumerateObject().Where(x => !JsonElement.DeepEquals(x.Value, proposal.GetProperty(x.Name))).Select(x => x.Name).ToArray();
            Require(differing.Length == 0, "The visible refused dataset no longer matches its archived proposal: " + string.Join(", ", differing));
            Require(SharedBusinessDocuments.Hash(visible) == SharedBusinessDocuments.Hash(proposal), "Persisting a common observation changed its field fingerprint without changing any value.");
            var review = await second.Call("sharing.business.conflict", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString() });
            await second.Call("sharing.business.resolve", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString(), expectedRevision = review.GetProperty("expectedRevision").GetInt64(), choice = "current" });
            Require(second.App.Result!.Rows[0][1] == "Observation A", "The deliberate dataset choice was not applied to its own open perimeter.");
            await second.Call("library.sync"); await first.Call("library.sync");
            Require(!(await second.Call("sharing.business.status")).GetProperty("conflicts").EnumerateArray().Any(x => x.GetProperty("kind").GetString() == "team-dataset"), "The rejected local observation was silently republished.");
        }
        finally { Directory.Delete(folder, true); }
    });
    await Check("An interrupted local application does not publish deletions and a later edit preserves both versions", async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-ApplyFailure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            await using var first = new Fixture(baseSettings, folder, "Planning A"); await using var second = new Fixture(baseSettings, folder, "Planning B");
            first.InProfile(() => new ScheduledTourStore { Tours = new() { tour.Copy() } }.Save(first.Path("scheduled-tours.json")));
            await first.Call("library.sync"); await second.Call("library.sync");
            var remote = tour.Copy(); remote.Name = "Version récente sur A"; first.InProfile(() => new ScheduledTourStore { Tours = new() { remote } }.Save(first.Path("scheduled-tours.json"))); await first.Call("library.sync");
            var destination = second.Path("scheduled-tours.json"); var saved = destination + ".previous"; File.Move(destination, saved); Directory.CreateDirectory(destination);
            try { await Reject(() => second.Call("library.sync")); }
            finally { Directory.Delete(destination); File.Move(saved, destination); }
            var edited = tour.Copy(); edited.Name = "Édition locale après interruption"; second.InProfile(() => new ScheduledTourStore { Tours = new() { edited } }.Save(destination));
            await second.Call("library.sync"); await first.Call("library.sync");
            Require(ScheduledTourStore.Load(first.Path("scheduled-tours.json")).Tours.Single().Name == remote.Name, "A baseline never applied locally overwrote a more recent shared tour.");
            var row = (await second.Call("sharing.business.status")).GetProperty("conflicts").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "team-tour");
            var review = await second.Call("sharing.business.conflict", new { id = row.GetProperty("id").GetString(), conflictId = row.GetProperty("conflictId").GetString() });
            Require(review.GetProperty("current").GetProperty("data").GetProperty("name").GetString() == remote.Name && review.GetProperty("incoming").GetProperty("data").GetProperty("name").GetString() == edited.Name, "The local proposal was not retained for comparison.");
        }
        finally { Directory.Delete(folder, true); }
    });
}
finally { try { Directory.Delete(share, true); } catch (IOException) { } }
Console.WriteLine($"{checks - failures.Count}/{checks} shared business checks passed.");
if (failures.Count > 0) Environment.ExitCode = 1;

void SetCapacity(Fixture fixture, int capacity) => fixture.InProfile(() => { var prefs = TechnicianPreferences.Load(fixture.Path("technician-preferences.json")); var value = prefs.Get(alice); value.DailyCapacityMinutes = capacity; prefs.With(alice, value).Save(fixture.Path("technician-preferences.json")); });
async Task Check(string name, Func<Task> run) { checks++; try { await run(); Console.WriteLine("PASS: " + name); } catch (Exception error) { failures.Add(name); Console.WriteLine("FAIL: " + name + " — " + error); } }
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Reject(Func<Task> run) { try { await run(); } catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or JsonException) { return; } throw new Exception("Expected operation to refuse invalid or stale state."); }
static string Fingerprint(string folder) => string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => System.IO.Path.GetRelativePath(folder, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));

sealed class Fixture : IAsyncDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GLPI-Pilot-Checks-Business-" + Guid.NewGuid().ToString("N"));
    public PilotApplication App { get; }
    public Fixture(AppSettings settings, string? share, string author)
    {
        Directory.CreateDirectory(Root); SetEnvironment();
        new LibraryPreferencesStore().Save(new() { LocalFolder = Path("Library"), SyncMode = share is null ? "local" : "folder", SharedFolder = share ?? "", DisplayName = author });
        var copy = settings.Copy(); copy.ExportFolder = Path("exports");
        App = new(new Host(), (_, _) => { }, copy, snapshots: new SnapshotStore(Path("history")));
    }
    public string Path(string name) => System.IO.Path.Combine(Root, name);
    private void SetEnvironment() { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", Root); }
    public void InProfile(Action action) { SetEnvironment(); action(); }
    public async Task<JsonElement> Call(string operation, object? arguments = null)
    { SetEnvironment(); return JsonSerializer.SerializeToElement(await App.ExecuteAsync(operation, JsonSerializer.SerializeToElement(arguments ?? new { }, PilotApplication.Json)), PilotApplication.Json); }
    public async ValueTask DisposeAsync() { SetEnvironment(); await App.DisposeAsync(); try { Directory.Delete(Root, true); } catch (IOException) { } }
    public static ExtractionResult Table()
    {
        var columns = new[] { "ID", "Titre", "Statut", "Dernière modification", "Date d'ouverture", "Priorité", "Demandeur - Demandeur", "Attribué à - Technicien", "Catégorie", "Suivis - Rédacteur", "Tâches - Rédacteur", "TTR + Progression", "Statistiques - Délai de prise en compte", "Type", "Attribué à - Groupe de techniciens", "Âge (jours)", "Nombre de groupes", "Nombre de rédacteurs", "Description" };
        return new() { Headers = columns.ToList(), Rows = new() { new[] { "1", "AGT — poste réseau", "En cours (attribué)", "2026-11-01 10:00", "2026-10-30 09:00", "Haute", "Demandeur témoin", "Alice", "Réseau", "Alice; Bob", "Bob", "35 %", "00:15", "Incident", "Support", "2", "1", "2", "Texte exact\n<script>reste du texte</script> ; guillemets \" et accents éè" } },
            SourceUrl = "https://shared-glpi.test/front/ticket.php?criteria=active&access_token=TOKEN-TO-STRIP&start=20", Title = "Liste commune", Complete = true, ExtractedAt = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
            Provenance = new() { SourceKind = "glpi", Method = "test-fixture", SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "entity-one-profile-support" } };
    }
    private sealed class Host : IPilotHost
    {
        public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
        public Task OpenExternalAsync(string url) => throw new InvalidOperationException("No external navigation in common data checks.");
        public Task CloseAsync() => Task.CompletedTask;
        public Task<byte[]> CapturePngAsync() => throw new NotSupportedException();
        public Task PrintPdfAsync(string path) => throw new NotSupportedException();
        public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
    }
}
namespace GlpiPilot.Runtime
{
    public sealed partial class PilotApplication
    {
        public void SharedBusinessFixtureLoad(ExtractionResult data) => SetResult(data);
        public void SharedBusinessFixtureEnrich()
        {
            var report = CityDetectionService.Enrich(TerrainNativeSource()!, EffectiveSettings.Cities, EffectiveSettings.Dashboard);
            SetResult(report.Data, report);
        }
    }
}
