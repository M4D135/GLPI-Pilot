using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class CollectionScopeRuntimeChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        const string portal = "https://scope-runtime.example/glpi/front/ticket.php";
        var settings = new AppSettings { PortalUrl = portal };
        var preferencesStore = new LibraryPreferencesStore(); var previous = preferencesStore.Load();
        var preferences = new LibraryPreferences { LocalFolder = Path.Combine(root, "scope-runtime"), SourceDateOrder = "ymd", SourceTimeZone = "UTC" };
        preferencesStore.Save(preferences);
        var scope = new LibraryCollectionScope { Enabled = true, FromDate = "2025-03-01", Fields = [new() { Column = "Attribué à - Groupe", Values = ["N2"] }] };
        try
        {
            await check("Targeted collection starts with an unavailable inventory, never an implicit global preview", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings);
                var model = await Call(app, "library.collection.get", new { });
                Require(!model.GetProperty("available").GetBoolean() && model.GetProperty("scope").GetProperty("enabled").GetBoolean(), "A missing inventory silently selected global collection.");
                Require(app.Browser.ActivePage is null, "Reading the scope opened a browser.");
            });
            await check("A local collection preview combines date and group and computes the exact selected denominator without GLPI", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings); app.LoadCollectionScopeFixture(Inventory(portal));
                var preview = await Call(app, "library.collection.preview", new { scope });
                var coverage = preview.GetProperty("coverage");
                Require(coverage.GetProperty("selected").GetInt32() == 2 && coverage.GetProperty("missing").GetInt32() == 2
                    && coverage.GetProperty("percent").GetDouble() == 0 && preview.GetProperty("inventory").GetProperty("count").GetInt32() == 4,
                    "The preview used the inventory or installation total instead of the selected scope.");
                var state = await Call(app, "library.collection.get", new { });
                Require(state.GetProperty("state").GetProperty("runId").GetString() == "" && app.Browser.ActivePage is null,
                    "Preview created a run or accessed GLPI.");
            });
            await check("A new collection without a scope cannot implicitly crawl the entire installation", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings);
                await Refuse(app, new { mode = "incremental" }, "choix explicite");
                Require(app.Browser.ActivePage is null, "A missing scope started a global browser operation.");
            });
            await check("Changing inventory after preview refuses collection before any browser access", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings); app.LoadCollectionScopeFixture(Inventory(portal));
                var preview = await Call(app, "library.collection.preview", new { scope });
                app.LoadCollectionScopeFixture(Inventory(portal));
                await Refuse(app, new { mode = "incremental", scope, previewToken = preview.GetProperty("previewToken").GetString() }, "aperçu");
                Require(app.Browser.ActivePage is null, "An expired preview opened GLPI.");
            });
            await check("Changing filters after preview cannot reuse its token or widen the selection", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings); app.LoadCollectionScopeFixture(Inventory(portal));
                var preview = await Call(app, "library.collection.preview", new { scope });
                var widened = scope.Copy(); widened.Fields.Clear();
                await Refuse(app, new { mode = "incremental", scope = widened, previewToken = preview.GetProperty("previewToken").GetString() }, "aperçu");
                Require(app.Browser.ActivePage is null, "A changed filter opened GLPI.");
            });
            await check("An empty targeted selection remains empty and never falls back to global acquisition", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings); app.LoadCollectionScopeFixture(Inventory(portal));
                var empty = scope.Copy(); empty.TicketIds = ["999"];
                var preview = await Call(app, "library.collection.preview", new { scope = empty });
                Require(preview.GetProperty("coverage").GetProperty("selected").GetInt32() == 0
                    && preview.GetProperty("coverage").GetProperty("percent").ValueKind == JsonValueKind.Null, "Empty scope reported a misleading success percentage.");
                await Refuse(app, new { mode = "incremental", scope = empty, previewToken = preview.GetProperty("previewToken").GetString() }, "Aucun ticket");
                Require(app.Browser.ActivePage is null, "Empty selection opened GLPI.");
            });
            await check("An explicit global draft still requires its own collection choice", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings);
                var global = new LibraryCollectionScope();
                var preview = await Call(app, "library.collection.preview", new { scope = global });
                Require(preview.GetProperty("coverage").ValueKind == JsonValueKind.Null, "Global preview fabricated coverage.");
                await Refuse(app, new { mode = "incremental", scope = global, previewToken = preview.GetProperty("previewToken").GetString() }, "explicitement");
                Require(app.Browser.ActivePage is null, "Unconfirmed global draft opened GLPI.");
            });
            await check("Scheduling cannot apply a local draft before a targeted plan has been recorded", async () =>
            {
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings); app.LoadCollectionScopeFixture(Inventory(portal));
                await Call(app, "library.collection.preview", new { scope });
                var model = await Call(app, "library.collection.get", new { });
                try
                {
                    await Call(app, "library.collection.settings", new { revision = model.GetProperty("state").GetProperty("revision").GetInt64(), confirmedCollector = true,
                        options = new LibraryCollectionOptions { Automatic = true } });
                    throw new InvalidOperationException("An unrecorded draft enabled automatic collection.");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("première collecte ciblée", StringComparison.Ordinal)) { }
            });
            await check("A reopened paused collection exposes its frozen scope without the former inventory or a browser", async () =>
            {
                var context = TicketLibraryCaptureContext.FromSettings(settings, preferences);
                var store = new TicketLibraryStore(preferences.DatabasePath);
                var plan = LibraryCollectionScopePlanner.Build(Inventory(portal), settings, scope, preferences);
                var run = await store.BeginCaptureAsync(context, "incremental");
                await store.SaveCollectionPlanAsync(run, context, plan);
                await store.FinishCaptureAsync(run, "paused", Array.Empty<string>()); TicketLibraryStore.ReleaseCapture(run);
                var state = await store.CollectionStateAsync(context.Fingerprint); state.Scope = scope.Copy(); state.RunId = run; state.State = "paused";
                await store.SaveCollectionStateAsync(context.Fingerprint, state, state.Revision);
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, settings);
                var model = await Call(app, "library.collection.get", new { });
                Require(!model.GetProperty("available").GetBoolean() && model.GetProperty("state").GetProperty("runId").GetString() == run
                    && model.GetProperty("scope").GetProperty("fromDate").GetString() == scope.FromDate
                    && model.GetProperty("coverage").GetProperty("selected").GetInt32() == 2,
                    "Restart lost the saved selection or replaced its denominator.");
                Require(app.Browser.ActivePage is null, "Reading a persisted scope opened GLPI.");
            });
            await check("Another configured source cannot see the previous collection scope", async () =>
            {
                var other = settings.Copy(); other.PortalUrl = "https://another-scope.example/glpi/front/ticket.php";
                await using var app = new PilotApplication(new NullHost(), (_, _) => { }, other);
                var model = await Call(app, "library.collection.get", new { });
                Require(model.GetProperty("state").GetProperty("runId").GetString() == ""
                    && model.GetProperty("coverage").ValueKind == JsonValueKind.Null, "Scope crossed source context.");
            });
        }
        finally { preferencesStore.Save(previous); }
    }

    private static ExtractionResult Inventory(string portal) => new()
    {
        SourceUrl = portal, ExtractedAt = new(2025, 5, 1, 12, 0, 0, TimeSpan.Zero), Complete = true, ExpectedTotal = 4,
        Headers = ["ID", "Titre", "Date d'ouverture", "Dernière modification", "Attribué à - Groupe", "Demandeur"],
        Rows = [ ["41", "Incident visio", "2025-01-01 12:00", "2025-02-01 12:00", "N2", "Alice"],
            ["42", "Incident réseau", "2025-04-01 12:00", "2025-04-02 12:00", "N2", "Alice"],
            ["43", "Incident messagerie", "2025-04-02 12:00", "2025-04-03 12:00", "N2", "Bob"],
            ["44", "Incident accès", "2025-04-02 12:00", "2025-04-03 12:00", "N3", "Chloé"] ],
        Provenance = new() { RequestedUrl = portal, SourceKind = "browser", SearchVerified = true, ScopeVerified = true, ScopeFingerprint = "scope-fixture" }
    };
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object args)
        => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args, PilotApplication.Json)), PilotApplication.Json);
    private static async Task Refuse(PilotApplication app, object args, string message)
    {
        try { await Call(app, "library.capture", args); }
        catch (InvalidOperationException error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException("Expected scope refusal: " + message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
