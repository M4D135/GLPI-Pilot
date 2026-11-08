using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private SharedUpdateOffer? _sharedUpdateOffer;
    private string _sharedUpdateToken = "";
    private string SharedUpdateFolder()
    {
        var path = DataPath("update-share.json"); if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Le réglage du dossier de diffusion est trop volumineux.");
        var folder = JsonSerializer.Deserialize<string>(File.ReadAllText(path), Json) ?? "";
        return folder.Length == 0 ? "" : SharedUpdateSource.ValidateFolder(folder);
    }
    private void RequireSharedUpdateAccess()
    {
        if (IsTestMode) throw new InvalidOperationException("La démonstration utilise les mises à jour fictives. Aucun partage réseau réel n’est consulté depuis cet espace.");
    }
    private void RegisterSharedUpdateOperations()
    {
        Register("update.shared.pick", async (_, ct) =>
        {
            RequireSharedUpdateAccess(); ct.ThrowIfCancellationRequested();
            var folder = await Host.PickFolderAsync("Choisir le dossier de diffusion des packs PROD");
            return new { cancelled = folder is null, folder = folder is null ? "" : SharedUpdateSource.ValidateFolder(folder) };
        });
        Register("update.shared.save", (a, ct) =>
        {
            RequireSharedUpdateAccess(); var folder = Text(a, "folder", 3000).Trim();
            if (folder.Length > 0) folder = SharedUpdateSource.ValidateFolder(folder);
            ct.ThrowIfCancellationRequested(); UpdateService.WriteJson(DataPath("update-share.json"), folder);
            _sharedUpdateOffer = null; _sharedUpdateToken = ""; _terrainPreparedUpdate = null; _sitePatchDraft = null;
            return Task.FromResult<object?>(new { folder });
        });
        Register("update.shared.find", async (a, ct) =>
        {
            RequireSharedUpdateAccess(); if (!Flag(a, "consent")) throw new InvalidDataException("Lancez explicitement la recherche sur le partage.");
            var offer = await FindSharedOfferAsync(SharedUpdateFolder(), ct); ct.ThrowIfCancellationRequested();
            _sharedUpdateOffer = offer; _sharedUpdateToken = Guid.NewGuid().ToString("N");
            return new { token = _sharedUpdateToken, offer.Version, offer.ArchiveName, archiveSha256 = offer.Sha256, offer.Bytes,
                newer = Version.Parse(offer.Version) > Version.Parse(AppPaths.ApplicationVersion) };
        });
        Register("update.shared.prepare", async (a, ct) =>
        {
            RequireSharedUpdateAccess(); var offer = _sharedUpdateOffer;
            if (offer is null || Text(a, "token", 40) != _sharedUpdateToken || !Flag(a, "consent") || offer.Folder != SharedUpdateFolder())
                throw new InvalidOperationException("Recherchez le pack sur le partage puis confirmez sa copie locale.");
            _terrainPreparedUpdate = null; _terrainPreparedPolicy = "";
            if (await FindSharedOfferAsync(offer.Folder, ct) != offer) throw new InvalidOperationException("La publication du partage a changé. Actualisez son bilan avant de préparer le pack.");
            Publish("update.progress", new { message = "Copie du pack partagé dans le dossier local et vérification de son empreinte…" });
            var path = await SharedUpdateSource.CopyVerifiedAsync(offer.Folder, offer.ArchiveName, offer.Sha256, offer.Bytes, Path.Combine(TerrainUpdatesRoot, "downloads"), ct);
            try
            {
                var metadata = SharedUpdateSource.ReadCopiedMetadata(path, offer); var policy = UpdatePolicy.Load(DataPath("update-policy.json"));
                _terrainPreparedUpdate = null;
                var prepared = await UpdateService.PrepareAsync(path, offer.Sha256, metadata.SourceCommit, TerrainInstallRoot, TerrainUpdatesRoot, AppPaths.ApplicationVersion, policy, ct: ct);
                ct.ThrowIfCancellationRequested(); _terrainPreparedUpdate = prepared; _terrainPreparedPolicy = TerrainDigest(policy);
                return TerrainPreparedReview(prepared);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        RegisterSitePatchOperations();
    }

    // Only light packs are published since 3.14.0: a complete installation also considers them and takes the newest;
    // UpdateService.EnsureSameLoader still requires an engine outside the pack before such a move.
    private async Task<SharedUpdateOffer> FindSharedOfferAsync(string folder, CancellationToken ct)
    {
        var installed = UpdateService.InstalledEdition(TerrainInstallRoot);
        if (installed != "PROD") return await SharedUpdateSource.FindAsync(folder, installed, ct);
        SharedUpdateOffer complete;
        try { complete = await SharedUpdateSource.FindAsync(folder, "PROD", ct); }
        catch (InvalidDataException) { return await SharedUpdateSource.FindAsync(folder, "PROD-LIGHT", ct); }
        try
        {
            var light = await SharedUpdateSource.FindAsync(folder, "PROD-LIGHT", ct);
            return Version.Parse(light.Version) > Version.Parse(complete.Version) ? light : complete;
        }
        catch (InvalidDataException) { return complete; }
    }
}
