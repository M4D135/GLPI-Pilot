using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record SiteFieldChange(string Name, string Before, string After);
    private sealed record SitePatchChange(string Kind, string Type, string Id, string Name, string Detail, IReadOnlyList<SiteFieldChange> Fields);
    private sealed record SitePatchDraft(string Token, SharedSiteOffer Offer, string Baseline, DateTimeOffset Expires,
        CitySettings Settings, IReadOnlyList<SitePatchChange> Changes);
    private SitePatchDraft? _sitePatchDraft;

    private void RequireSitePatchContext()
    {
        RequireSharedUpdateAccess();
        if (IsHistorical) throw new InvalidOperationException("Fermez l’archive avant d’appliquer un patch au référentiel de travail.");
        if (_terrainCityDraft is not null && TerrainDigest(_terrainCityDraft) != _terrainCityBaseline)
            throw new InvalidOperationException("Enregistrez ou abandonnez le brouillon du référentiel avant de préparer le patch des sites.");
    }
    private void RegisterSitePatchOperations()
    {
        Register("update.sites.preview", async (_, ct) =>
        {
            RequireSitePatchContext(); _sitePatchDraft = null;
            var offer = await SharedUpdateSource.FindSitesAsync(SharedUpdateFolder(), ct);
            var path = await SharedUpdateSource.CopyVerifiedAsync(offer.Folder, offer.Name, offer.Sha256, offer.Bytes, Path.Combine(TerrainUpdatesRoot, "site-patches"), ct);
            try
            {
                var csv = SitePatchFormat.Read(path, ct);
                var before = SitePatchReviewData(Settings.Cities);
                if (!csv.Headers.SequenceEqual(before.Headers) || csv.Rows.Any(row => !Guid.TryParse(row[1], out var id) || id == Guid.Empty))
                    throw new InvalidDataException("Utilisez le patch GLPI Pilot v1 exporté par le lanceur Admin : colonnes complètes et identifiants stables sont obligatoires.");
                var preview = SitePatchFormat.Preview(csv, Settings.Cities, ct);
                var after = SitePatchReviewData(preview.Settings);
                string Key(string[] row) => row[0] + ":" + row[1];
                var original = before.Rows.ToDictionary(Key, StringComparer.Ordinal);
                var merged = after.Rows.ToDictionary(Key, StringComparer.Ordinal);
                var changes = preview.Changes.Select(change =>
                {
                    var key = change.Type + ":" + change.Id;
                    original.TryGetValue(key, out var previous); var next = merged[key];
                    var fields = before.Headers.Select((name, index) => new SiteFieldChange(name, previous?[index] ?? "", next[index]))
                        .Where(field => field.Before != field.After).ToArray();
                    return new SitePatchChange(change.Kind, change.Type, change.Id, change.Name, change.Detail, fields);
                }).ToArray();
                ct.ThrowIfCancellationRequested();
                _sitePatchDraft = new(Guid.NewGuid().ToString("N"), offer, TerrainDigest(Settings.Cities), DateTimeOffset.UtcNow.AddMinutes(30), preview.Settings, changes);
                return SitePatchPage(JsonSerializer.SerializeToElement(new { token = _sitePatchDraft.Token }, Json));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        TerrainRegister("update.sites.page", (a, ct) => { ct.ThrowIfCancellationRequested(); return SitePatchPage(a); });
        Register("update.sites.apply", async (a, ct) =>
        {
            var draft = RequireSitePatch(a);
            if (!Flag(a, "confirmed")) throw new InvalidDataException("Confirmez les valeurs du comparatif avant d’appliquer le patch.");
            if (await SharedUpdateSource.FindSitesAsync(draft.Offer.Folder, ct) != draft.Offer)
                throw new InvalidOperationException("Le patch publié a changé. Rechargez son aperçu avant de l’appliquer.");
            // Verify the current bytes too: a publisher may replace the CSV before its checksum file.
            var verified = await SharedUpdateSource.CopyVerifiedAsync(draft.Offer.Folder, draft.Offer.Name, draft.Offer.Sha256, draft.Offer.Bytes, Path.Combine(TerrainUpdatesRoot, "site-patches"), ct);
            try
            {
                draft = RequireSitePatch(a);
                var changed = draft.Changes.Any(item => item.Kind != "Identique");
                var context = EffectiveSettings; var source = TerrainNativeSource(); CityEnrichmentResult? report = null;
                if (changed && source is not null)
                    report = CityDetectionService.Enrich(source, draft.Settings, context.Dashboard, ct, CityOverrideStore.Load(DataPath("city-overrides.json")).Overrides);
                var backup = "";
                if (changed)
                {
                    backup = Path.Combine(AppPaths.DataRoot, "Backups", "sites-avant-patch-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
                    UpdateService.EnsureNoLinks(Path.GetDirectoryName(backup)!);
                    UpdateService.WriteJson(backup, new { Schema = ConfigurationTransferService.Schema, SchemaVersion = ConfigurationTransferService.SchemaVersion, AppVersion = AppPaths.ApplicationVersion, Settings = new { Cities = Settings.Cities } });
                }
                ct.ThrowIfCancellationRequested(); var previous = Settings; var next = Settings.Copy();
                next.Cities = draft.Settings.Copy(); next.SitePatchAppliedHash = draft.Offer.Sha256;
                Settings = next; try { SaveSettings(); } catch { Settings = previous; throw; }
                _sitePatchDraft = null; _terrainCityDraft = null; _terrainCityPreview = null; _terrainImportPreview = null;
                if (report is not null) { context.Cities = Settings.Cities.Copy(); SetResult(report.Data, report, context: context); }
                Publish("terrain.reference.changed", new { revision = Revision });
                return new { backupPath = backup, sha256 = draft.Offer.Sha256, added = draft.Changes.Count(item => item.Kind == "Ajout"),
                    modified = draft.Changes.Count(item => item.Kind == "Modification"), unchanged = draft.Changes.Count(item => item.Kind == "Identique") };
            }
            finally { if (File.Exists(verified)) File.Delete(verified); }
        });
        Register("admin.sites.export", async (_, ct) =>
        {
            AdministrationCapabilities.Require(); RequireSitePatchContext();
            var folder = await Host.PickFolderAsync("Choisir où préparer le patch des sites pour diffusion");
            if (folder is null) return new { cancelled = true };
            folder = SharedUpdateSource.ValidateFolder(folder); ct.ThrowIfCancellationRequested();
            // Every export has its own folder. Publishing the two reviewed files is an explicit distributor action.
            var target = Path.Combine(folder, "GLPI-Pilot-Sites-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(target);
            var path = Path.Combine(target, SharedUpdateSource.SiteFile);
            var exported = SitePatchFormat.Write(Settings.Cities, path, ct);
            if (new FileInfo(path).Length > 8 * 1024 * 1024) { Directory.Delete(target, true); throw new InvalidDataException("Le patch dépasse 8 Mio. Réduisez le référentiel diffusé."); }
            var hash = UpdateService.HashFile(path);
            await File.WriteAllTextAsync(Path.Combine(target, SharedUpdateSource.SiteHashes), hash + "  " + SharedUpdateSource.SiteFile + "\n", ct);
            return new { cancelled = false, folder = target, rows = exported.Rows, sha256 = hash };
        });
    }
    private static ExtractionResult SitePatchReviewData(CitySettings settings)
    {
        var data = CityReferenceService.Export(settings);
        var aliases = settings.Cities.ToDictionary(item => "Ville:" + item.Id, item => item.Aliases);
        foreach (var item in settings.Sites) aliases.Add("Site:" + item.Id, item.Aliases);
        foreach (var item in settings.Buildings) aliases.Add("Bâtiment:" + item.Id, item.Aliases);
        foreach (var row in data.Rows)
        {
            // JSON makes list boundaries explicit: ["A | B"] must differ from ["A", "B"].
            row[4] = JsonSerializer.Serialize(aliases[row[0] + ":" + row[1]], Json);
            if (row[0] == "Ville") row[12] = JsonSerializer.Serialize(settings.Cities.Single(item => item.Id == row[1]).ExcludedTerms, Json);
            if (row[0] == "Site") row[18] = JsonSerializer.Serialize(settings.Sites.Single(item => item.Id == row[1]).ClosedDates, Json);
        }
        return data;
    }
    private SitePatchDraft RequireSitePatch(JsonElement a)
    {
        RequireSitePatchContext(); var draft = _sitePatchDraft;
        if (draft is null || Text(a, "token", 40) != draft.Token || draft.Expires < DateTimeOffset.UtcNow
            || draft.Baseline != TerrainDigest(Settings.Cities) || draft.Offer.Folder != SharedUpdateFolder())
            throw new InvalidOperationException("Le contexte du patch a changé ou son aperçu a expiré. Rechargez le comparatif.");
        return draft;
    }
    private object SitePatchPage(JsonElement a)
    {
        var draft = RequireSitePatch(a); var (offset, limit) = TerrainPage(a);
        return new { token = draft.Token, sha256 = draft.Offer.Sha256, total = draft.Changes.Count, offset, limit,
            added = draft.Changes.Count(item => item.Kind == "Ajout"), modified = draft.Changes.Count(item => item.Kind == "Modification"),
            unchanged = draft.Changes.Count(item => item.Kind == "Identique"), items = draft.Changes.Skip(offset).Take(limit).ToArray() };
    }
}
