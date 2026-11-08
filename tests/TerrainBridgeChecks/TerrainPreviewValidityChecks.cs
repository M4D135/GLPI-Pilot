using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;

internal static class TerrainPreviewValidityChecks
{
    private const string CityId = "11111111111111111111111111111111";
    private const string SiteId = "22222222222222222222222222222222";
    private const string Url = "https://reference-preview.example.invalid/front/ticket.php";

    public static async Task RunAsync(string root, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        foreach (var kind in new[] { "browser", "file", "cache", "none", "legacy-layout", "legacy-correction" })
        {
            var isolated = Path.Combine(root, "GLPI-Pilot-Checks-Preview-" + kind);
            Directory.CreateDirectory(isolated);
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", isolated);
            try { await CheckImport(isolated, kind, check, reject); }
            finally { Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root); }
        }
    }

    private static async Task CheckImport(string root, string kind, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        void Check(bool value, string message) => check(value, kind + " preview: " + message);
        var settings = new AppSettings { PortalUrl = Url };
        settings.History.RestoreLastOnStartup = false;
        settings.Cities = new() { TitleColumn = "Titre", Cities = new() { new() { Id = CityId, Name = "Commune fictive" } },
            Sites = new() { new() { Id = SiteId, CityId = CityId, Name = "Site initial fictif", Aliases = new() { "ANCIEN-LIEU" } } } };
        settings.Dashboard.ColumnMappings = new(StringComparer.Ordinal) { ["id"] = "ID", ["title"] = "Titre", ["status"] = "Statut" };
        settings.Save();
        var source = new ExtractionResult
        {
            SourceUrl = Url, Complete = true, Headers = new() { "ID", "Titre", "Statut" },
            Rows = new() { new[] { "101", "Incident ANCIEN-LIEU", "Nouveau" }, new[] { "102", "Incident NOUVEAU-LIEU", "En attente" } },
            Provenance = new() { SourceKind = "browser", RequestedUrl = Url, SearchVerified = true, ScopeVerified = true }
        };
        var host = new TerrainTestHost();
        await using var app = new PilotApplication(host, (_, _) => { }, settings);
        async Task<JsonElement> Call(string operation, object? arguments = null)
            => JsonSerializer.SerializeToElement(await app.ExecuteAsync(operation,
                JsonSerializer.SerializeToElement(arguments ?? new { }, PilotApplication.Json)), PilotApplication.Json);

        if (kind is "file" or "cache")
        {
            host.OpenPath = Path.Combine(root, "tickets-fictifs.csv");
            await File.WriteAllTextAsync(host.OpenPath, "ID;Titre;Statut\r\n101;Incident ANCIEN-LIEU;Nouveau\r\n102;Incident NOUVEAU-LIEU;En attente\r\n", new UTF8Encoding(true));
            await Call("records.import");
            if (kind == "cache")
            {
                File.Delete(host.OpenPath);
                await Call("cache.restore");
                Check(app.IsCachedSource && app.IsHistorical, "the actual last-source cache is restored without its original ticket CSV");
            }
        }
        else if (kind == "legacy-layout")
        {
            // Older observations can omit all dashboard layouts. Re-reading an
            // interpretation must not turn generated display IDs into a change
            // of the column mapping used by the location detector.
            app.LoadFixture(source, history: new SnapshotDocument
            {
                Data = source, Cities = settings.Cities.Copy(), Dashboard = new()
                { ColumnMappings = new(settings.Dashboard.ColumnMappings, StringComparer.Ordinal) }
            });
        }
        else if (kind != "none") app.LoadFixture(source);

        var correctionsPath = Path.Combine(root, "city-overrides.json");
        if (kind == "legacy-correction")
        {
            // A legacy correction without its optional audit timestamp is valid
            // input; reading it must not fabricate a new time on every load.
            await File.WriteAllTextAsync(correctionsPath, JsonSerializer.Serialize(new
            {
                Version = 1,
                Overrides = new[] { new { SourceKey = CityOverrideStore.SourceKey(Url), TicketId = "101", CityId, SiteId,
                    Reason = "Ancienne correction fictive", Explicit = true } }
            }));
            var first = CityOverrideStore.Load(correctionsPath);
            var second = CityOverrideStore.Load(correctionsPath);
            Check(first.Overrides.Single().UpdatedUtc == default && first.Fingerprint() == second.Fingerprint(),
                "an omitted legacy timestamp remains unknown and produces a stable correction-store fingerprint");
        }
        var correctionBytes = File.Exists(correctionsPath) ? await File.ReadAllBytesAsync(correctionsPath) : null;
        host.OpenPath = Path.Combine(root, "reference-fictive.csv");
        await File.WriteAllTextAsync(host.OpenPath,
            "Type;Id;VilleId;Nom;Alias;CodePostal;Adresse;Latitude;Longitude;Fusion\r\n" +
            "Ville;;;Commune fictive;;;;;;Completer\r\nSite;;Commune fictive;Site importé fictif;NOUVEAU-LIEU;;;;;Completer\r\n", new UTF8Encoding(true));
        var original = app.Result;
        var saved = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        var state = await Call("terrain.reference.state");
        var imported = await Call("terrain.reference.import", new { draftVersion = state.GetProperty("draftVersion").GetInt32() });
        state = await Call("terrain.reference.importApply", new { token = imported.GetProperty("token").GetString(), confirmed = true });
        Check(state.GetProperty("dirty").GetBoolean() && state.GetProperty("sites").GetInt32() == 2 && app.Settings.Cities.Sites.Count == 1,
            "real CSV confirmation updates the draft without saving its references");
        var preview = await Call("terrain.reference.preview", new { revision = app.Revision, draftVersion = state.GetProperty("draftVersion").GetInt32() });
        Check(preview.GetProperty("token").GetString() is { Length: 32 }
            && (await File.ReadAllBytesAsync(AppPaths.SettingsFile)).SequenceEqual(saved),
            "a newly calculated preview is immediately valid and leaves saved settings unchanged");
        var page = await Call("terrain.reference.previewPage", new { token = preview.GetProperty("token").GetString() });
        Check(page.GetProperty("token").GetString() == preview.GetProperty("token").GetString(),
            "the same unmodified preview remains pageable across independent context reads");
        state = await Call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true });
        Check(app.Settings.Cities.Sites.Count == 2 && !state.GetProperty("dirty").GetBoolean()
            && SettingsRecovery.Read(AppPaths.SettingsFile).Cities.Sites.Count == 2,
            "confirmed application saves the imported fictitious site through the real runtime");
        Check(kind is "none" or "cache" or "legacy-layout" ? ReferenceEquals(app.Result, original)
                : app.Locations is { MatchedCount: 2 } && app.Locations.Matches[1].SiteName == "Site importé fictif",
            "only a current observation is recalculated while an absent or archived observation is preserved");
        if (correctionBytes is not null)
        {
            Check((await File.ReadAllBytesAsync(correctionsPath)).SequenceEqual(correctionBytes),
                "reading and applying the reference do not rewrite the pre-existing correction file");
            Check(app.Locations!.Matches[0].Explanation.Contains("date non renseignée", StringComparison.Ordinal)
                && !app.Locations.Matches[0].Explanation.Contains("0001", StringComparison.Ordinal),
                "an unknown correction date is displayed honestly instead of an invented reading date");
            var beforeCommit = DateTimeOffset.UtcNow;
            await Call("terrain.review.correct", new { revision = app.Revision, row = 1, siteId = SiteId, cityId = CityId, reason = "Nouvelle correction fictive" });
            var persisted = CityOverrideStore.Load(correctionsPath);
            var newCorrection = persisted.Overrides.Single(value => value.TicketId == "102");
            Check(newCorrection.UpdatedUtc >= beforeCommit && newCorrection.UpdatedUtc <= DateTimeOffset.UtcNow
                && persisted.Overrides.Single(value => value.TicketId == "101").UpdatedUtc == default,
                "a new real correction receives its commit time without inventing a date for the older correction");
        }
        await reject(() => Call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true }),
            kind + " preview: a successfully consumed preview cannot be applied twice");
        if (kind == "browser") await CheckGuards(app, Call, root, check, reject);
    }

    private static async Task CheckGuards(PilotApplication app, Func<string, object?, Task<JsonElement>> call, string root,
        Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        async Task<JsonElement> Preview()
        {
            var state = await call("terrain.reference.state", null);
            return await call("terrain.reference.preview", new { revision = app.Revision, draftVersion = state.GetProperty("draftVersion").GetInt32() });
        }
        async Task RejectWithoutMutation(JsonElement preview, string label)
        {
            var saved = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
            var result = app.Result; var revision = app.Revision;
            await reject(() => call("terrain.reference.apply", new { token = preview.GetProperty("token").GetString(), confirmed = true }), label);
            check((await File.ReadAllBytesAsync(AppPaths.SettingsFile)).SequenceEqual(saved) && ReferenceEquals(app.Result, result) && app.Revision == revision,
                label + " without changing the saved configuration or observed rows");
        }
        var preview = await Preview();
        var path = Path.Combine(root, "city-overrides.json"); var sourceKey = CityOverrideStore.SourceKey(app.Result!);
        var before = app.Revision;
        CityOverrideStore.Load(path).With(new() { CityId = CityId, SiteId = SiteId, Explicit = true, Reason = "Correction concurrente fictive" }, sourceKey, "102").Save(path);
        check(app.Revision == before, "preview guard fixture changes only the correction file, not the session revision");
        await RejectWithoutMutation(preview, "preview rejects an actual correction-file change after calculation");
        preview = await Preview();
        var corrected = CityOverrideStore.Load(path); corrected.Overrides.Single().Reason = "Motif corrigé ailleurs"; corrected.Save(path);
        await RejectWithoutMutation(preview, "preview rejects a changed correction explanation even when its selected location is unchanged");
        preview = await Preview();
        var state = await call("terrain.reference.state", null);
        var site = app.Settings.Cities.Sites.Single(value => value.Id == SiteId).Copy(); site.Contact = "Contact fictif ajouté au brouillon";
        await call("terrain.reference.upsert", new { draftVersion = state.GetProperty("draftVersion").GetInt32(), kind = "site", entry = site });
        await RejectWithoutMutation(preview, "preview rejects a later draft edit");
        preview = await Preview();
        var mapping = app.EffectiveSettings.Dashboard.Copy(); mapping.ColumnMappings["id"] = "Titre";
        await call("mapping.save", new { revision = app.Revision, settings = mapping });
        await RejectWithoutMutation(preview, "preview rejects a changed real source interpretation");
        preview = await Preview();
        app.LoadFixture(new() { Headers = new() { "ID", "Titre", "Statut" }, Rows = new() { new[] { "201", "Autre relevé fictif", "Nouveau" } },
            SourceUrl = Url, Complete = true, Provenance = new() { SourceKind = "browser", RequestedUrl = Url, SearchVerified = true, ScopeVerified = true } });
        await RejectWithoutMutation(preview, "preview rejects a replaced observation revision");
        preview = await Preview();
        app.Settings.Cities.Sites.Single(value => value.Id == SiteId).Contact = "Référentiel modifié ailleurs";
        await RejectWithoutMutation(preview, "preview rejects a changed saved-reference baseline");
    }
}
