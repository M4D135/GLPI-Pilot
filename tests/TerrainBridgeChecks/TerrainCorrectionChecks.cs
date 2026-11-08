using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class TerrainCorrectionChecks
{
    private const string CityA = "a1111111111111111111111111111111";
    private const string CityB = "b2222222222222222222222222222222";
    private const string SiteA = "a3333333333333333333333333333333";
    private const string SiteB = "b4444444444444444444444444444444";
    private const string NewSite = "c5555555555555555555555555555555";

    public static async Task RunAsync(string parent, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var root = Path.Combine(parent, "GLPI-Pilot-Checks-Corrections"); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
        try
        {
            await Run(root, check, reject);
            await CheckFailedRestore(root, check, reject);
        }
        finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", parent); }
    }

    private static async Task CheckFailedRestore(string parent, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var root = Path.Combine(parent, "GLPI-Pilot-Checks-Restore-Race"); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
        try
        {
            var settings = new AppSettings { Cities = new() { Sites = new() { new() { Id = SiteA, Name = "Atelier confirmé" } } } };
            settings.History.RestoreLastOnStartup = false; settings.Save();
            var host = new TerrainTestHost { OpenPath = Path.Combine(root, "first.csv") };
            await File.WriteAllTextAsync(host.OpenPath, "ID;Titre;Statut\n501;Observation originale;Nouveau\n", new UTF8Encoding(true));
            await using (var origin = new PilotApplication(host, (_, _) => { }, settings)) await Call(origin, "records.import");
            await using var app = new PilotApplication(host, (_, _) => { }, AppSettings.Load()); await Call(app, "app.bootstrap");
            var original = app.Result; var originalDocument = app.HistoricalDocument; var originalKey = CityOverrideStore.SourceKey(original!);
            var other = new ExtractionResult { SourceUrl = "https://other-cache.example.invalid/front/ticket.php", Headers = new() { "ID", "Titre", "Statut" },
                Rows = new() { new[] { "501", "Autre observation arrivée", "Nouveau" } }, Provenance = new() { SourceKind = "browser" } };
            var locations = CityDetectionService.Enrich(other, settings.Cities, settings.Dashboard);
            new LastSourceCacheStore().Save(locations.Data, settings, locations);
            var persistedCache = Fingerprints(Path.Combine(root, "LastSource")); var path = Path.Combine(root, "city-overrides.json");
            await File.WriteAllTextAsync(path, "{invalid-json");
            await reject(() => Call(app, "cache.restore"), "site correction: a failed projection cannot partially replace the active cached observation");
            check(ReferenceEquals(original, app.Result) && ReferenceEquals(originalDocument, app.HistoricalDocument) && app.IsCachedSource,
                "site correction: failed cache restoration retains the old active source and document together");
            new CityOverrideStore().Save(path);
            await Apply(app, await Prepare(app, 0), SiteA, "Correction après échec de restauration");
            check(app.IsCachedSource && CityOverrideStore.SourceKey(app.Result!) == originalKey && app.Result!.Rows[0][1] == "Observation originale"
                && app.Locations!.Matches[0].SiteId == SiteA,
                "site correction: subsequent correction uses the original cached baseline after a failed restore");
            check(Fingerprints(Path.Combine(root, "LastSource")) == persistedCache,
                "site correction: correcting the still-open old observation leaves the newer cache receipt untouched");
        }
        finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", parent); }
    }
    private static async Task Run(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        void Check(bool value, string label) => check(value, "site correction: " + label);
        // Fictional sites and coordinates exercise recognition without embedding a private reference.
        var settings = new AppSettings { PortalUrl = "https://correction.example.invalid/front/ticket.php", Cities = new()
        {
            TitleColumn = "Titre", SourcePriority = new() { "combined" },
            Cities = new() { new() { Id = CityA, Name = "Alpha", Latitude = 10, Longitude = 20 }, new() { Id = CityB, Name = "Beta" } },
            Buildings = new() { new() { Id = "d6666666666666666666666666666666", SiteId = SiteA, Name = "Bâtiment Alpha", Aliases = new() { "BAT-A" } } },
            Sites = new() { new() { Id = SiteA, CityId = CityA, Name = "Premier", Aliases = new() { "ATX" }, Address = "Ancienne adresse", Latitude = 10.1, Longitude = 20.1 }, new() { Id = SiteB, CityId = CityB, Name = "Second", Aliases = new() { "ATX" } } }
        } };
        settings.History.RestoreLastOnStartup = false; settings.Save();
        var file = Path.Combine(root, "tickets.csv"); var host = new TerrainTestHost { OpenPath = file };
        const string csv = "ID;Titre;Statut;Type\r\n101;ATX imprimante;Nouveau;Incident\r\n102;ATX réseau;Nouveau;Incident\r\n103;NOUVEAU annexe;Nouveau;Incident\r\n104;BAT-A écran;Nouveau;Incident\r\n105;Alpha écran;Nouveau;Incident\r\n";
        await File.WriteAllTextAsync(file, csv, new UTF8Encoding(true)); string archive; string key;
        await using (var app = new PilotApplication(host, (_, _) => { }, settings))
        {
            await Call(app, "records.import"); key = CityOverrideStore.SourceKey(app.Result!);
            Check(app.Locations!.AmbiguousCount == 2 && app.Locations.UnmatchedCount == 1, "real CSV produces the ambiguous baseline without manual source priority");
            archive = app.Snapshots.Save(app.Result!, app.EffectiveSettings, locations: app.Locations).Id;
        }
        File.Delete(file);
        settings = AppSettings.Load();
        settings.Cities.Cities.Single(city => city.Id == CityA).Latitude = 11; settings.Cities.Cities.Single(city => city.Id == CityA).Longitude = 21;
        var relocated = settings.Cities.Sites.Single(site => site.Id == SiteA); relocated.Address = "Nouvelle adresse"; relocated.Latitude = 11.1; relocated.Longitude = 21.1;
        settings.Cities.Sites.Add(new() { Id = NewSite, CityId = CityA, Name = "Atelier Cobalt", Aliases = new() { "NOUVEAU", "CBX" }, Latitude = 10.2, Longitude = 20.2 }); settings.Save();
        var cacheBytes = Fingerprints(Path.Combine(root, "LastSource")); var archiveBytes = Fingerprints(Path.Combine(root, "History"));
        var overridePath = Path.Combine(root, "city-overrides.json");
        await using (var app = new PilotApplication(host, (_, _) => { }, AppSettings.Load()))
        {
            await Call(app, "app.bootstrap"); var original = Json(app.HistoricalDocument); var originalCells = app.Result!.Rows.Select(row => row.Take(4).ToArray()).ToArray();
            Check(app.IsCachedSource && app.IsHistorical && !File.Exists(file) && app.Locations!.Matches[2].SiteId is null, "real cache restores with its old dictionary after the original CSV is removed");
            var legacy = new CityOverrideStore { Overrides = new() { new() { SourceKey = key, TicketId = "101", CityId = CityA, SiteId = NewSite, Reason = "Ancienne règle placée après le titre" } } };
            Check(CityCorrectionProjection.Create(app.HistoricalDocument!, app.Settings.Cities, legacy) is null,
                "an older non-explicit override never changes a captured decision silently during upgrade");
            var draft = await Prepare(app, 0, 1);
            var choices = await Call(app, "terrain.correction.sites", new { token = Token(draft), search = "CBX", limit = 1 });
            Check(choices.GetProperty("total").GetInt32() == 1 && choices.GetProperty("items")[0].GetProperty("value").GetString() == NewSite,
                "saved sites added after capture are searchable by alias");
            await reject(() => Apply(app, draft, "ffffffffffffffffffffffffffffffff", "Essai invalide"), "site correction: an unknown site rejects the whole batch before persistence");
            await reject(() => Apply(app, draft, NewSite, ""), "site correction: empty reason cannot create a partial batch");
            Check(!File.Exists(overridePath) && Json(app.HistoricalDocument) == original, "rejected batch leaves the original observation and store untouched");
            var applied = await Apply(app, draft, NewSite, "Lieu confirmé sur la carte");
            Check(applied.GetProperty("count").GetInt32() == 2 && app.Locations!.Matches.Take(2).All(match => match.SiteId == NewSite && match.CityId == CityA && match.IsOverride),
                "one atomic correction fixes both selected tickets and derives the site's commune");
            Check(app.IsCachedSource && app.IsHistorical && app.Locations!.Matches[2].SiteId is null && app.Locations.Matches[2].State == CityMatchState.Unmatched,
                "cache stays offline and the unselected new alias is never inferred silently");
            Check(app.Result!.Rows.Select(row => row.Take(4)).Zip(originalCells).All(pair => pair.First.SequenceEqual(pair.Second))
                && app.EffectiveSettings.Cities.Sites.Any(site => site.Id == NewSite), "original ticket cells stay identical and the derived reference supports the selected site");
            var dashboard = await Call(app, "dashboard.get", new { businessView = "sites" });
            var manual = dashboard.GetProperty("snapshot").GetProperty("metrics").EnumerateArray().Single(value => value.GetProperty("key").GetString() == "city-manual");
            var workload = dashboard.GetProperty("widgets").EnumerateArray().Single(value => value.GetProperty("id").GetString() == "business-sites-3")
                .GetProperty("content").GetProperty("sites").EnumerateArray().Single(value => value.GetProperty("label").GetString() == "Atelier Cobalt");
            Check(manual.GetProperty("value").GetDouble() == 2 && workload.GetProperty("count").GetInt32() == 2,
                "the real Sites dashboard uses the corrected historical evidence and derived reference");
            var query = new SavedQuery { Name = "Sites corrigés", BaseProbe = "total", Conditions = new() { new() { Field = "column:" + app.Locations!.SiteColumnName, Operator = "equals", Value = "Atelier Cobalt" } } };
            var queryResult = await Call(app, "queries.preview", new { revision = app.Revision, query });
            Check(queryResult.GetProperty("available").GetBoolean() && queryResult.GetProperty("metric").GetProperty("value").GetDouble() == 2,
                "query evaluation agrees on corrected evidence and the derived site column");
            host.SavePath = Path.Combine(root, "corrected-export.csv"); await Call(app, "records.export", new { revision = app.Revision, format = "csv" });
            var exported = NativeCsvImportService.Read(host.SavePath, ""); var exportedSite = exported.Headers.IndexOf(app.Locations!.SiteColumnName);
            Check(exportedSite >= 0 && exported.Rows.Count(row => row[exportedSite] == "Atelier Cobalt") == 2
                && exported.Rows.Select(row => row.Take(4)).Zip(originalCells).All(pair => pair.First.SequenceEqual(pair.Second)),
                "the real CSV exporter writes corrected derived sites while preserving every original source cell");
            var map = await Call(app, "maps.workspace.get");
            var rows = await Call(app, "maps.workspace.rows", new { token = map.GetProperty("token").GetString() });
            Check(map.GetProperty("correctionAllowed").GetBoolean() && rows.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("ticket").GetProperty("isOverride").GetBoolean()) == 2,
                "real map exposes corrected row markers and remains editable offline");
            var points = map.GetProperty("locations").EnumerateArray().ToArray();
            Check(points.Single(value => value.GetProperty("key").GetString() == "city:" + CityA).GetProperty("place").GetProperty("latitude").GetDouble() == 10,
                "a correction referencing an existing city never moves another cached city-only ticket to today's coordinates");
            await Apply(app, await Prepare(app, 0), SiteA, "Site existant confirmé");
            var remapped = await Call(app, "maps.workspace.get");
            var sitePoint = remapped.GetProperty("locations").EnumerateArray().Single(value => value.GetProperty("key").GetString() == "site:" + SiteA).GetProperty("place");
            Check(sitePoint.GetProperty("latitude").GetDouble() == 10.1 && app.EffectiveSettings.Cities.Sites.Single(site => site.Id == SiteA).Address == "Ancienne adresse"
                && app.Locations!.Matches[3].SiteId == SiteA && !app.Locations.Matches[3].IsOverride,
                "selecting a moved site retains its captured point and address for unselected cached tickets");
            await Apply(app, await Prepare(app, 0), NewSite, "Retour au site confirmé");
            var buildingColumn = app.Result!.Headers.IndexOf("Bâtiment détecté");
            await Apply(app, await Prepare(app, 3), NewSite, "Autre bâtiment confirmé");
            Check(buildingColumn >= 0 && app.Result!.Rows[3][buildingColumn] == "" && app.Locations!.Matches[3].SiteId == NewSite,
                "a corrected site clears the former site's derived building instead of exporting a contradictory address");
            await Call(app, "terrain.correction.apply", new { token = Token(await Prepare(app, 3)), remove = true });
            Check(app.Result!.Rows[3][buildingColumn] == "Bâtiment Alpha" && app.Locations!.Matches[3].SiteId == SiteA,
                "removing that correction restores the captured building and site together");
            await reject(() => Call(app, "terrain.tours.weather"), "site correction: a derived cache cannot enable online weather/actions");
            await reject(() => Apply(app, draft, NewSite, "Réutilisation"), "site correction: a consumed revision token cannot apply twice");
            Check(CityOverrideStore.Load(overridePath).Overrides.Count == 2 && CityOverrideStore.Load(overridePath).Overrides.All(value => value.Explicit),
                "both explicit corrections are persisted together despite a source order without manual");
            await Call(app, "history.open", new { id = archive });
            var archived = await Prepare(app, 0);
            Check(!string.IsNullOrEmpty(archived.GetProperty("readOnlyReason").GetString()) && app.Locations!.Matches[0].SiteId is null,
                "an explicitly opened archive retains its original decision and explains its read-only state");
            await reject(() => Apply(app, archived, NewSite, "Archive"), "site correction: explicit archive correction is refused");
        }
        Check(Fingerprints(Path.Combine(root, "LastSource")) == cacheBytes && Fingerprints(Path.Combine(root, "History")) == archiveBytes,
            "all captured cache and archive bytes remain identical after correction");
        Check(await ChildRestore(root, NewSite, 2), "a fresh operating-system process restores both corrections without the original CSV");
        await using (var app = new PilotApplication(host, (_, _) => { }, AppSettings.Load()))
        {
            await Call(app, "app.bootstrap"); var draft = await Prepare(app, 0);
            Check(draft.GetProperty("canRemove").GetBoolean(), "manual decisions offer an explicit return to their captured baseline");
            await Call(app, "terrain.correction.apply", new { token = Token(draft), remove = true });
            Check(app.Locations!.Matches[0].State == CityMatchState.Ambiguous && !app.Locations.Matches[0].IsOverride && app.Locations.Matches[1].SiteId == NewSite,
                "removal restores only the selected cached row and preserves another correction");
        }
        Check(await ChildRestore(root, NewSite, 1), "a removal and the remaining correction both survive a new process");
        await File.WriteAllTextAsync(file, csv, new UTF8Encoding(true));
        await using (var app = new PilotApplication(host, (_, _) => { }, AppSettings.Load()))
        {
            await Call(app, "records.import");
            Check(!app.IsCachedSource && CityOverrideStore.SourceKey(app.Result!) == key && app.Locations!.Matches[1].SiteId == NewSite && app.Locations.Matches[1].IsOverride,
                "reimporting the same file reapplies the correction even when manual is absent from configured source priorities");
            var token = await Prepare(app, 0, 1);
            var saved = CityOverrideStore.Load(overridePath); saved.With(new() { CityId = CityA, SiteId = NewSite, Reason = "Autre session", Explicit = true }, key, "104").Save(overridePath);
            var afterExternal = File.ReadAllBytes(overridePath);
            await reject(() => Apply(app, token, NewSite, "Lot périmé"), "site correction: a competing persisted correction invalidates the batch token");
            Check(File.ReadAllBytes(overridePath).SequenceEqual(afterExternal) && app.Locations!.Matches[0].SiteId is null, "conflicting batch does not overwrite concurrent changes or partially update the view");
            // Legacy correction bridge uses the same atomic mechanism and saved reference.
            await Call(app, "terrain.review.correct", new { revision = app.Revision, row = 0, cityId = "", siteId = NewSite, reason = "Fiche détaillée" });
            Check(app.Locations!.Matches[0].CityId == CityA && app.Locations.Matches[0].SiteId == NewSite,
                "the existing review dialog derives the commune and shares the new correction semantics");
        }
        // The last-source file above captured a manual decision; removing it must not resurrect it on restart.
        File.Delete(file);
        await using (var app = new PilotApplication(host, (_, _) => { }, AppSettings.Load()))
        {
            await Call(app, "app.bootstrap"); var draft = await Prepare(app, 1);
            await Call(app, "terrain.correction.apply", new { token = Token(draft), remove = true });
            Check(app.Locations!.Matches[1].State == CityMatchState.Ambiguous && !app.Locations.Matches[1].IsOverride,
                "removing a correction already captured in cache reruns only that row with captured automatic rules");
        }
        await using (var app = new PilotApplication(host, (_, _) => { }, AppSettings.Load()))
        {
            await Call(app, "app.bootstrap"); Check(!app.Locations!.Matches[1].IsOverride && app.Locations.Matches[1].State == CityMatchState.Ambiguous,
                "the removal marker prevents a captured manual result from reappearing at restart");
            host.OpenPath = Path.Combine(root, "other-source.csv"); await File.WriteAllTextAsync(host.OpenPath, csv, new UTF8Encoding(true));
            await Call(app, "records.import");
            Check(CityOverrideStore.SourceKey(app.Result!) != key && app.Locations!.Matches.Take(2).All(match => !match.IsOverride && match.State == CityMatchState.Ambiguous),
                "identical IDs in a different file source never inherit these corrections");
            await File.WriteAllTextAsync(host.OpenPath, csv.Replace("104;", "101;"), new UTF8Encoding(true)); await Call(app, "records.import");
            var before = File.ReadAllBytes(overridePath);
            await reject(() => Prepare(app, 0, 1), "site correction: an ID duplicated outside the selection rejects the complete batch");
            await reject(() => Prepare(app, 1, 1), "site correction: repeated row indices cannot create a duplicate correction");
            Check(File.ReadAllBytes(overridePath).SequenceEqual(before), "duplicate identifiers leave every stored correction unchanged");
            var draft = await Prepare(app, 1);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await reject(() => Call(app, "terrain.correction.apply", new { token = Token(draft), siteId = NewSite, reason = "Annulé" }, cancelled.Token),
                "site correction: cancellation happens before persistence");
            Check(File.ReadAllBytes(overridePath).SequenceEqual(before), "cancelled correction does not mutate its batch");
            await File.WriteAllTextAsync(host.OpenPath, csv.Replace("ID;", "Autre;"), new UTF8Encoding(true)); await Call(app, "records.import");
            await reject(() => Prepare(app, 0), "site correction: an unrecognized ID column is refused instead of using the first column");
            Check(File.ReadAllBytes(overridePath).SequenceEqual(before), "missing identifiers never create an accidental persisted identity");
        }
    }

    public static async Task RestoreChild(string root, string site, int expected)
    {
        Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1"); Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
        await using var app = new PilotApplication(new TerrainTestHost(), (_, _) => { }, AppSettings.Load()); await Call(app, "app.bootstrap");
        if (!app.IsCachedSource || !app.IsHistorical || app.Locations!.Matches.Count(match => match.SiteId == site && match.IsOverride) != expected)
            throw new InvalidOperationException("Fresh-process correction restoration failed.");
        Console.WriteLine("CORRECTION_RESTORE_OK");
    }
    private static async Task<bool> ChildRestore(string root, string site, int expected)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--correction-restore-child"); start.ArgumentList.Add(root); start.ArgumentList.Add(site); start.ArgumentList.Add(expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        var stdout = await output; var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr + stdout);
        return stdout.Contains("CORRECTION_RESTORE_OK", StringComparison.Ordinal);
    }
    private static Task<JsonElement> Prepare(PilotApplication app, params int[] rows) => Call(app, "terrain.correction.prepare", new { revision = app.Revision, rows });
    private static Task<JsonElement> Apply(PilotApplication app, JsonElement draft, string site, string reason) => Call(app, "terrain.correction.apply", new { token = Token(draft), siteId = site, reason });
    private static string Token(JsonElement value) => value.GetProperty("token").GetString()!;
    private static async Task<JsonElement> Call(PilotApplication app, string operation, object? args = null, CancellationToken ct = default)
        => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation, JsonSerializer.SerializeToElement(args ?? new { }, PilotApplication.Json), ct), PilotApplication.Json);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, PilotApplication.Json);
    private static string Fingerprints(string root) => Directory.Exists(root) ? string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))) : "";
}
