using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static partial class Program
{
    private sealed class MapWorkspaceHost : IPilotHost
    {
        public string? OpenPath { get; set; }
        public string Clipboard { get; private set; } = "";
        public int ClipboardWrites { get; private set; }
        public int ExternalCalls { get; private set; }
        public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult(OpenPath);
        public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text) { Clipboard = text; ClipboardWrites++; return Task.CompletedTask; }
        public Task OpenExternalAsync(string url) { ExternalCalls++; throw new InvalidOperationException("No external page is permitted in the map workspace checks."); }
        public Task CloseAsync() => Task.CompletedTask;
        public Task<bool> NotifyAsync(string title, string message) => Task.FromResult(false);
        public Task<byte[]> CapturePngAsync() => throw new NotSupportedException();
        public Task PrintPdfAsync(string path) => throw new NotSupportedException();
    }

    private static async Task CheckMapWorkspaceRuntime()
    {
        var previousRoot = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_ROOT");
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-MapWorkspace-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", folder);
        try
        {
            var city = new CityDefinition { Name = "Commune de contrôle", Latitude = 40, Longitude = 3 };
            var alpha = new SiteDefinition { Name = "Site Alpha", Aliases = new() { "ALPHA" }, Latitude = 40.1, Longitude = 3.1, Address = "1 rue de contrôle" };
            var beta = new SiteDefinition { Name = "Site Bêta", Aliases = new() { "BETA" }, CityId = city.Id, Latitude = 40.2, Longitude = 3.2, Address = "2 rue de contrôle" };
            var gamma = new SiteDefinition { Name = "Site Gamma", Aliases = new() { "GAMMA" }, CityId = city.Id, Address = "3 rue de contrôle" };
            var zero = new SiteDefinition { Name = "Site sans ticket", CityId = city.Id, Latitude = 40.3, Longitude = 3.3 };
            var settings = new AppSettings { PortalUrl = "https://maps.example.invalid/front/ticket.php", ExportFolder = Path.Combine(folder, "Exports"),
                Cities = new() { Cities = new() { city }, Sites = new() { alpha, beta, gamma, zero } } };
            settings.Dashboard.PersonalTechnician = "Alex";
            settings.Dashboard.Business.WeatherRules = new()
            {
                new() { Id = "map-team-a", Label = "Équipe de contrôle", Kind = "team", SourceKey = "active", Values = new() { "Groupe A" } },
                new() { Id = "map-team-ab", Label = "Équipe transversale", Kind = "team", SourceKey = "active", Values = new() { "Groupe A", "Groupe B" } }
            };
            string[] Entry(string id, string title, string status, string groups, string technicians, string type)
                => new[] { id, title, status, groups, technicians, type, "01/09/2026", "Haute" };
            var source = new ExtractionResult
            {
                Headers = new() { "ID", "Titre", "Statut", "Attribué à - Groupe de techniciens", "Attribué à - Technicien", "Type", "Date d’ouverture", "Priorité" },
                Rows = new()
                {
                    Entry("201", "ALPHA équipement", "Nouveau", "Groupe A; Groupe B", "Alex; Bob", "Incident"),
                    Entry("202", "BETA demande", "En attente", "Groupe A", "Alex", "Demande"),
                    Entry("203", "Commune de contrôle accès", "Nouveau", "Groupe B", "Bob", "Incident"),
                    Entry("204", "Titre sans lieu", "En cours (attribué)", "Groupe A", "", "Demande"),
                    Entry("205", "GAMMA intervention", "En cours (planifié)", "Groupe B", "Alex", "Incident"),
                    Entry("206", "ALPHA BETA à vérifier", "Nouveau", "Groupe A", "Bob", "Incident"),
                    Entry("207", "BETA terminé", "Résolu", "Groupe A", "Alex", "Demande"),
                    Entry("208", "ALPHA statut personnalisé", "Statut personnalisé", "Groupe A", "Alex", "Incident"),
                    Entry("209", "", "En attente", "Groupe C", "", "Type personnalisé"),
                    Entry("210", "BETA équipement", "Nouveau", "Groupe A; Groupe B", "Bob", "Incident")
                },
                Complete = true, ExpectedTotal = 10, ExtractedAt = Clock, SourceUrl = settings.PortalUrl,
                Provenance = new() { RequestedUrl = settings.PortalUrl, SourceKind = "glpi", SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "map-fixture" }
            };
            SettingsRecovery.Save(AppPaths.SettingsFile, settings); var settingsBytes = File.ReadAllBytes(AppPaths.SettingsFile);
            var host = new MapWorkspaceHost();
            await using var app = new PilotApplication(host, (_, _) => { }, settings, snapshots: new SnapshotStore(Path.Combine(folder, "History")));
            app.LoadFixture(source, CityDetectionService.Enrich(source, settings.Cities, settings.Dashboard));
            var day = DateTime.Today.ToString("yyyy-MM-dd");
            var view = await Call(app, "maps.workspace.get", new { visitDate = day }); var token = Str(view, "token"); var totals = view.GetProperty("totals");
            Check(Int(totals, "tickets") == 8 && Int(totals, "placed") == 4 && Int(totals, "sites") == 4 && Int(totals, "approximate") == 1
                && Int(totals, "review") == 3 && Int(totals, "withoutCoordinates") == 4 && Int(totals, "unassigned") == 2 && Int(view, "sourceUnknownStatus") == 1,
                "The central map partitions the real active scope into geocoded tickets, exact sites, approximate municipalities and explicit location gaps; resolved and unknown statuses are excluded.");
            var locations = view.GetProperty("locations").EnumerateArray().ToArray();
            var alphaLocation = locations.Single(item => Str(item, "key") == "site:" + alpha.Id);
            var gammaLocation = locations.Single(item => Str(item, "key") == "site:" + gamma.Id);
            Check(alphaLocation.GetProperty("place").GetProperty("latitude").GetDouble() == 40.1 && gammaLocation.GetProperty("place").GetProperty("latitude").ValueKind == JsonValueKind.Null
                && locations.Any(item => Str(item, "kind") == "city" && item.GetProperty("place").GetProperty("latitude").GetDouble() == 40),
                "Standalone site coordinates remain exact, a site without coordinates never borrows the municipality centre, and city-only evidence remains explicitly approximate.");
            var first = await Call(app, "maps.workspace.rows", new { token, offset = 0, limit = 3 });
            var second = await Call(app, "maps.workspace.rows", new { token, offset = 3, limit = 3 });
            var third = await Call(app, "maps.workspace.rows", new { token, offset = 6, limit = 3 });
            static int[] Rows(JsonElement data) => data.GetProperty("items").EnumerateArray().Select(item => Int(item.GetProperty("ticket"), "rowIndex")).ToArray();
            Check(Int(first, "total") == 8 && Rows(first).Concat(Rows(second)).Concat(Rows(third)).SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 8, 9 }),
                "Paginated map details retain every original scoped occurrence including ambiguous places, absent titles and sites without coordinates.");
            var union = await Call(app, "maps.workspace.get", new { focusKeys = new[] { "team:map-team-a", "group:Groupe A" } });
            Check(Int(union.GetProperty("totals"), "tickets") == 5,
                "Overlapping team and group filters form one union and never double-count a multi-group ticket.");
            var people = await Call(app, "maps.workspace.get", new { technicianKeys = new[] { "ALEX", "BOB" } });
            var mine = await Call(app, "maps.workspace.get", new { mine = true });
            var unassigned = await Call(app, "maps.workspace.get", new { technicianKeys = new[] { "@unassigned" } });
            Check(Int(people.GetProperty("totals"), "tickets") == 6 && Int(mine.GetProperty("totals"), "tickets") == 3 && Int(unassigned.GetProperty("totals"), "tickets") == 2,
                "Management filters combine technicians once per row, Mes tickets uses the already selected identity, and unassigned tickets have their own explicit scope.");
            var combined = await Call(app, "maps.workspace.get", new { focusKeys = new[] { "team:map-team-a" }, technicianKeys = new[] { "ALEX" }, type = "request", statusCodes = new[] { 4 } });
            var combinedRows = await Call(app, "maps.workspace.rows", new { token = Str(combined, "token") });
            Check(Int(combined.GetProperty("totals"), "tickets") == 1 && Rows(combinedRows).SequenceEqual(new[] { 1 }),
                "Team, technician, type and active-status selections share exactly the same marker counts and detail rows.");
            var unknownType = await Call(app, "maps.workspace.get", new { type = "unknown" });
            Check(Int(unknownType.GetProperty("totals"), "tickets") == 1,
                "An unrecognized incident/request type can be inspected without relabelling it as an incident.");
            await Reject<InvalidDataException>(async () => { await Call(app, "maps.workspace.get", new { technicianKeys = new[] { "REMOVED" } }); }, "A removed technician choice fails explicitly rather than broadening the map scope.");
            await Reject<InvalidDataException>(async () => { await Call(app, "maps.workspace.get", new { statusCodes = new[] { 0 } }); }, "The active-status map cannot silently include an unknown status code.");
            view = await Call(app, "maps.workspace.get", new { includeEmptySites = true, visitDate = day }); token = Str(view, "token");
            Check(view.GetProperty("locations").EnumerateArray().Any(item => Str(item, "key") == "site:" + zero.Id && Int(item, "count") == 0)
                && Int(view.GetProperty("totals"), "tickets") == 8,
                "Site exploration retains configured zero-stock sites without adding tickets to the scoped total.");
            var copied = await Call(app, "maps.workspace.copyIds", new { token, placeKey = "site:" + beta.Id });
            Check(Int(copied, "copied") == 2 && host.Clipboard == "202" + Environment.NewLine + "210",
                "The site ID action copies the full scoped occurrence list through the native host.");
            var copyCount = host.ClipboardWrites;
            await Reject<InvalidDataException>(async () => { await Call(app, "maps.workspace.copyIds", new { token, placeKey = "site:" + zero.Id }); },
                "A zero-stock site cannot clear the clipboard through a hidden or stale copy action.");
            Check(host.ClipboardWrites == copyCount, "Rejecting an empty site ID list preserves the existing clipboard.");
            var selection = await Call(app, "maps.workspace.select", new { token, rows = new[] { 0 }, placeKeys = new[] { "site:" + alpha.Id, "site:" + beta.Id } });
            Check(Int(selection, "selected") == 3, "Selecting a ticket and its whole site keeps one selected source occurrence.");
            await Reject<InvalidDataException>(async () => { await Call(app, "maps.workspace.select", new { token, rows = new[] { 6 } }); }, "A selection outside the frozen map scope is rejected before any local planning mutation.");
            await Call(app, "maps.workspace.select", new { token, rows = new[] { 3 } });
            var preview = await Call(app, "maps.workspace.tour.preview", new { token, name = "Visites de contrôle", date = day, technicianKey = "ALEX" });
            var state = await Call(app, "terrain.tours.state");
            Check(Int(preview, "tickets") == 4 && Int(preview, "unlocated") == 1 && preview.GetProperty("stops").GetArrayLength() == 2
                && state.GetProperty("draft").ValueKind == JsonValueKind.Null && !File.Exists(Path.Combine(folder, "scheduled-tours.json")),
                "Tour preview derives real stops and preserves unlocated tickets without changing a draft, saving a tour or writing to GLPI.");
            var previewToken = Str(preview, "token");
            await Call(app, "maps.workspace.select", new { token, rows = new[] { 3 }, selected = false });
            await Reject<InvalidOperationException>(async () => { await Call(app, "maps.workspace.tour.apply", new { token, previewToken }); }, "Changing selection invalidates the previously reviewed tour preview.");
            preview = await Call(app, "maps.workspace.tour.preview", new { token, name = "Visites de contrôle", date = day, technicianKey = "ALEX" });
            var applied = await Call(app, "maps.workspace.tour.apply", new { token, previewToken = Str(preview, "token") });
            state = await Call(app, "terrain.tours.state");
            Check(state.GetProperty("dirty").GetBoolean() && Int(state.GetProperty("draft"), "stops") == 2
                && !applied.GetProperty("saved").GetBoolean() && !File.Exists(Path.Combine(folder, "scheduled-tours.json")),
                "Applying a reviewed map selection creates the existing local planning draft and leaves persistence to the planning save action.");
            preview = await Call(app, "maps.workspace.tour.preview", new { token, name = "Autre brouillon", date = day, technicianKey = "BOB" });
            await Reject<InvalidOperationException>(async () => { await Call(app, "maps.workspace.tour.apply", new { token, previewToken = Str(preview, "token") }); },
                "A different unsaved planning draft is protected until its replacement is explicitly chosen.");
            await Call(app, "terrain.tours.save", new { revision = app.Revision, draftVersion = Int(state, "draftVersion") });
            var planned = await Call(app, "maps.workspace.get", new { visitDate = day });
            var anotherDay = await Call(app, "maps.workspace.get", new { visitDate = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd") });
            Check(planned.GetProperty("visits").GetArrayLength() == 2 && planned.GetProperty("visits").EnumerateArray().All(visit => Str(visit, "label").StartsWith("PRÉVU", StringComparison.Ordinal)
                    && Str(visit, "technician") == "Alex" && Str(visit, "date") == day)
                && anotherDay.GetProperty("visits").GetArrayLength() == 0,
                "The planned layer contains saved dated visits and declared technicians only; changing day does not invent current staff positions.");
            var normalTours = File.ReadAllBytes(Path.Combine(folder, "scheduled-tours.json"));
            var csv = Path.Combine(folder, "incidents.csv"); ExportService.WriteCsv(source, csv); host.OpenPath = csv;
            await Call(app, "records.test.import");
            var simulated = await Call(app, "maps.workspace.get", new { visitDate = day }); var simulationToken = Str(simulated, "token");
            Check(app.IsTestMode && simulated.GetProperty("simulation").GetBoolean() && Int(simulated.GetProperty("totals"), "tickets") == 8,
                "The same central map pipeline works on a real CSV import while preserving simulation provenance.");
            await Call(app, "maps.workspace.select", new { token = simulationToken, rows = new[] { 0, 1 } });
            var simulationPreview = await Call(app, "maps.workspace.tour.preview", new { token = simulationToken, name = "Tournée simulée", date = day, technicianKey = "ALEX" });
            var simulatedDraft = await Call(app, "maps.workspace.tour.apply", new { token = simulationToken, previewToken = Str(simulationPreview, "token"), discardDraft = true });
            await Call(app, "terrain.tours.save", new { revision = app.Revision, draftVersion = Int(simulatedDraft, "draftVersion") });
            await Call(app, "records.test.exit");
            Check(!app.IsTestMode && settingsBytes.SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile))
                && normalTours.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "scheduled-tours.json"))) && host.ExternalCalls == 0,
                "Creating and saving a map tour in CSV simulation leaves the ordinary profile and tour register byte-for-byte unchanged and never calls an external GLPI page.");
            await Reject<InvalidOperationException>(async () => { await Call(app, "maps.workspace.rows", new { token = simulationToken }); }, "A simulation map token is unusable in the restored ordinary workspace.");
            var oldToken = Str(await Call(app, "maps.workspace.get"), "token");
            var many = new ExtractionResult { Headers = source.Headers.ToList(), Rows = Enumerable.Range(0, 2001).Select(index => Entry((1000 + index).ToString(), "ALPHA équipement", "Nouveau", "Groupe A", "Alex", "Incident")).ToList(),
                SourceUrl = source.SourceUrl, ExtractedAt = source.ExtractedAt, Complete = true, ExpectedTotal = 2001, Provenance = source.Provenance };
            app.LoadFixture(many, CityDetectionService.Enrich(many, settings.Cities, settings.Dashboard));
            var writesBefore = host.ClipboardWrites;
            await Reject<InvalidOperationException>(async () => { await Call(app, "maps.workspace.copyIds", new { token = oldToken }); }, "Revision changes reject a stale map operation before touching the clipboard.");
            Check(host.ClipboardWrites == writesBefore, "Rejected stale map actions preserve the previous clipboard contents.");
            var large = await Call(app, "maps.workspace.get"); var largeToken = Str(large, "token");
            await Reject<InvalidDataException>(async () => { await Call(app, "maps.workspace.select", new { token = largeToken, all = true }); }, "The 2,000-ticket planning limit rejects an oversized whole-scope selection without silent truncation.");
            var largeRows = await Call(app, "maps.workspace.rows", new { token = largeToken, offset = 2000, limit = 40 });
            Check(Int(large.GetProperty("totals"), "tickets") == 2001 && Int(largeRows, "total") == 2001 && Int(largeRows, "selected") == 0 && largeRows.GetProperty("items").GetArrayLength() == 1,
                "Marker totals and paginated details cover all 2,001 source rows even when the separate planning selection is too large.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", previousRoot);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
