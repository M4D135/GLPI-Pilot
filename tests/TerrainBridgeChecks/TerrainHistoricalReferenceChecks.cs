using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class TerrainHistoricalReferenceChecks
{
    private const string CityId = "11111111111111111111111111111111";
    private const string SiteId = "22222222222222222222222222222222";

    public static async Task RunAsync(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        foreach (var kind in new[] { "cache", "archive", "none", "current" })
        {
            var isolated = Path.Combine(root, "GLPI-Pilot-Checks-Reference-" + kind);
            Directory.CreateDirectory(isolated); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", isolated);
            try { await CheckContext(isolated, kind, check, reject); }
            finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root); }
        }
    }

    private static async Task CheckContext(string root, string kind, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var historical = kind is "cache" or "archive";
        void Check(bool value, string message) => check(value, kind + " reference: " + message);
        var settings = new AppSettings { PortalUrl = "https://reference.example.invalid/front/ticket.php", Theme = "dark", InterfaceZoomPercent = 110 };
        settings.History.RestoreLastOnStartup = false;
        settings.Cities = new() { TitleColumn = "Titre", Cities = new() { new() { Id = CityId, Name = "Commune conservée" } },
            Sites = new() { new() { Id = SiteId, CityId = CityId, Name = "Site conservé", Aliases = new() { "ANCIEN-LIEU" }, Contact = "Contact local conservé" } } };
        settings.Dashboard.ColumnMappings = new(StringComparer.Ordinal) { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut" };
        settings.Save();
        var host = new TerrainTestHost { OpenPath = Path.Combine(root, "tickets.csv") };
        await File.WriteAllTextAsync(host.OpenPath, "ID;Titre;Statut\r\n0042;Incident ANCIEN-LIEU;Nouveau\r\n0043;Incident NOUVEAU-LIEU;En attente\r\n", new UTF8Encoding(true));
        string? archiveId = null;
        if (historical)
        {
            await using var origin = new PilotApplication(host, (_, _) => { }, settings);
            await Call(origin, "records.import");
            var receipt = await Call(origin, "cache.state");
            Check(origin.IsFileSource && origin.Locations is { Applied: true } && origin.Result!.Rows.Count == 2 && receipt.GetProperty("available").GetBoolean(),
                "real ticket CSV creates an enriched source with a durable last-source receipt");
            archiveId = origin.Snapshots.Save(origin.Result!, origin.EffectiveSettings, locations: origin.Locations).Id;
            File.Delete(host.OpenPath);
        }
        await using var app = new PilotApplication(host, (_, _) => { }, AppSettings.Load());
        if (kind == "cache") await Call(app, "app.bootstrap");
        else if (kind == "archive") await Call(app, "history.open", new { id = archiveId });
        else if (kind == "current")
        {
            app.LoadFixture(new()
            {
                SourceUrl = settings.PortalUrl, Complete = true, Headers = new() { "ID", "Titre", "Statut" },
                Rows = new() { new[] { "42", "Incident ANCIEN-LIEU", "Nouveau" }, new[] { "43", "Incident NOUVEAU-LIEU", "En attente" } },
                Provenance = new() { SourceKind = "browser", RequestedUrl = settings.PortalUrl, SearchVerified = true, ScopeVerified = true }
            });
        }
        Check(app.IsHistorical == historical && app.IsCachedSource == (kind == "cache") && (app.Result is null) == (kind == "none"),
            "the intended real runtime context is active before importing the reference");
        var originalResult = app.Result; var originalLocations = app.Locations; var originalDocument = app.HistoricalDocument;
        var originalData = Json(app.Result); var originalEvidence = Json(app.Locations); var originalHistory = Json(app.HistoricalDocument);
        var originalInterpretation = Json(new { app.EffectiveSettings.Dashboard, app.EffectiveSettings.Cities, app.EffectiveSettings.Queries });
        var originalSettings = app.Settings.Copy(); var originalDisk = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        var originalCache = Fingerprints(Path.Combine(root, "LastSource")); var originalArchives = Fingerprints(Path.Combine(root, "History"));
        var originalSession = (await Call(app, "app.bootstrap")).GetProperty("session");
        host.OpenPath = Path.Combine(root, "sites.csv");
        await File.WriteAllTextAsync(host.OpenPath,
            "Type;Id;VilleId;Nom;Alias;CodePostal;Adresse;Latitude;Longitude\r\n" +
            "Ville;;;Commune conservée;;;;;\r\nSite;;Commune conservée;Site importé;NOUVEAU-LIEU;;Adresse importée;;\r\n", new UTF8Encoding(true));
        var state = await Call(app, "terrain.reference.state");
        var imported = await Call(app, "terrain.reference.import", new { draftVersion = Version(state) });
        Check(imported.GetProperty("additions").GetInt32() == 1 && app.Settings.Cities.Sites.Count == 1,
            "the actual CSV importer previews the additional site without applying settings");
        state = await Call(app, "terrain.reference.importApply", new { token = imported.GetProperty("token").GetString(), confirmed = true });
        Check(state.GetProperty("dirty").GetBoolean() && state.GetProperty("sites").GetInt32() == 2 && app.Settings.Cities.Sites.Count == 1
            && (await File.ReadAllBytesAsync(AppPaths.SettingsFile)).SequenceEqual(originalDisk),
            "import confirmation updates only the draft before global reference confirmation");
        var preview = await Call(app, "terrain.reference.preview", new { revision = app.Revision, draftVersion = Version(state) });
        var target = kind == "current" ? "working-reference-and-observation" : "working-reference";
        Check(preview.GetProperty("preservedObservation").GetBoolean() == historical && preview.GetProperty("observationKind").GetString() == kind
            && preview.GetProperty("target").GetString() == target && state.GetProperty("target").GetString() == target,
            "preview and state explicitly identify the saved-reference target and preserved observation");
        Check(preview.GetProperty("totalRows").GetInt32() == (kind == "current" ? 2 : 0)
            && preview.GetProperty("changedRows").GetInt32() == (kind == "current" ? 1 : 0)
            && (await File.ReadAllBytesAsync(AppPaths.SettingsFile)).SequenceEqual(originalDisk),
            "preview recalculates only a current observation and never persists the proposal");
        var page = await Call(app, "terrain.reference.previewPage", new { token = preview.GetProperty("token").GetString(), offset = 0, limit = 1 });
        Check(page.GetProperty("observationKind").GetString() == kind && page.GetProperty("target").GetString() == target
            && (!historical || page.GetProperty("items").GetArrayLength() == 0), "paginated preview retains the scope and cannot imply historical row changes");
        await reject(() => Call(app, "terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = false }),
            kind + " reference: applying the saved reference still requires explicit confirmation");
        state = await Call(app, "terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true });
        var persisted = SettingsRecovery.Read(AppPaths.SettingsFile);
        Check(app.Settings.Cities.Sites.Count == 2 && persisted.Cities.Sites.Count == 2 && !state.GetProperty("dirty").GetBoolean()
            && persisted.Cities.Sites.Single(s => s.Id == SiteId).Contact == "Contact local conservé"
            && persisted.Cities.Sites.Single(s => s.Name == "Site importé").Aliases.SequenceEqual(new[] { "NOUVEAU-LIEU" }),
            "confirmed application persists both sites and preserves existing identifiers and local contact");
        var withoutReference = app.Settings.Copy(); withoutReference.Cities = originalSettings.Cities.Copy();
        Check(Json(withoutReference) == Json(originalSettings), "applying the reference does not change unrelated settings");
        if (historical)
        {
            Check(ReferenceEquals(app.Result, originalResult) && ReferenceEquals(app.Locations, originalLocations)
                && ReferenceEquals(app.HistoricalDocument, originalDocument) && Json(app.Result) == originalData
                && Json(app.Locations) == originalEvidence && Json(app.HistoricalDocument) == originalHistory,
                "source, location evidence and historical document retain both object identity and all content");
            Check(Json(new { app.EffectiveSettings.Dashboard, app.EffectiveSettings.Cities, app.EffectiveSettings.Queries }) == originalInterpretation
                && app.IsHistorical && app.IsCachedSource == (kind == "cache"), "the observed rules and cached/archive identity remain frozen");
            var session = (await Call(app, "app.bootstrap")).GetProperty("session");
            Check(session.GetProperty("historical").GetBoolean() && session.GetProperty("capabilities").GetRawText() == originalSession.GetProperty("capabilities").GetRawText()
                && (await Call(app, "terrain.review.detail", new { revision = app.Revision, row = 1 })).GetProperty("match").GetProperty("siteId").ValueKind == JsonValueKind.Null,
                "historical capabilities and the unresolved ticket location remain unchanged after saving the new site");
        }
        else if (kind == "current")
            Check(app.Locations is { MatchedCount: 2 } && app.Locations.Matches[1].SiteId == app.Settings.Cities.Sites.Single(s => s.Name == "Site importé").Id
                && ReferenceEquals(app.Locations.Source, originalResult) && !app.IsHistorical,
                "a current observation still receives the fully recalculated location result from its exact source");
        else Check(app.Result is null && app.Locations is null && !app.IsHistorical, "a profile without observations accepts reference configuration without manufacturing data");
        Check(Fingerprints(Path.Combine(root, "LastSource")) == originalCache && Fingerprints(Path.Combine(root, "History")) == originalArchives,
            "reference changes leave every cache and archive file byte-for-byte intact");
        var pack = Path.Combine(root, "pack"); Directory.CreateDirectory(pack); app.SetTerrainDeploymentFixture(pack);
        await Call(app, "deployment.default.save", new { confirmed = true, expectedSha256 = "" });
        var deployment = DeploymentConfigurationService.Read(DeploymentConfigurationInitializer.DefaultPath(pack), root).Configuration;
        Check(Json(deployment.Settings.Cities) == Json(app.Settings.Cities) && deployment.Settings.Cities.Sites.Count == 2,
            "Define as default captures the updated working reference even while an old observation remains open");
    }

    private static int Version(JsonElement value) => value.GetProperty("draftVersion").GetInt32();
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, PilotApplication.Json);
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object? args = null)
        => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json)), PilotApplication.Json);
    private static string Fingerprints(string path) => Directory.Exists(path)
        ? string.Join('\n', Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))) : "";
}
