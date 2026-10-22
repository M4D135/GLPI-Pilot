using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

/// <summary>The browser receives review data, never transaction paths, health nonces or arbitrary updater commands.</summary>
public sealed partial class PilotApplication
{
    private string? _terrainUpdateArchive;
    private OfficialUpdate? _terrainOfficialUpdate;
    private string _terrainOfficialToken = "";
    private PreparedUpdate? _terrainPreparedUpdate;
    private string _terrainPreparedPolicy = "";
    private static string TerrainUpdatesRoot => Path.Combine(AppPaths.DataRoot, "Updates");
    private static string TerrainInstallRoot => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);

    private void RegisterTerrainUpdateOperations()
    {
        TerrainRegister("update.state", (a, ct) => TerrainUpdateState());
        TerrainRegister("update.policy", (a, ct) =>
        {
            var policy = TerrainRead<UpdatePolicy>(a, "policy", 2000); policy.Validate(); ct.ThrowIfCancellationRequested();
            policy.Save(DataPath("update-policy.json")); _terrainPreparedUpdate = null; _terrainPreparedPolicy = "";
            return TerrainUpdateState();
        });
        Register("update.selectArchive", TerrainSelectUpdateAsync);
        Register("update.prepareLocal", TerrainPrepareLocalUpdateAsync);
        Register("update.findOfficial", TerrainFindUpdateAsync);
        Register("update.downloadOfficial", TerrainDownloadUpdateAsync);
        Register("update.prepareRollback", TerrainPrepareRollbackAsync);
        Register("update.install", TerrainInstallUpdateAsync);
        Register("update.openReleases", async (a, ct) => { ct.ThrowIfCancellationRequested(); await Host.OpenExternalAsync(UpdateReleaseClient.ReleasesPage); return new { opened = true }; });
    }
    private object TerrainUpdateState()
    {
        var policy = UpdatePolicy.Load(DataPath("update-policy.json"));
        return new { version = AppPaths.ApplicationVersion, installRoot = TerrainInstallRoot, policy, windows = OperatingSystem.IsWindows(),
            archive = _terrainUpdateArchive is null ? null : new { name = Path.GetFileName(_terrainUpdateArchive) },
            official = _terrainOfficialUpdate is null ? null : TerrainOfficialReview(_terrainOfficialUpdate, _terrainOfficialToken),
            prepared = _terrainPreparedUpdate is null ? null : TerrainPreparedReview(_terrainPreparedUpdate),
            outcome = UpdateService.ReadLastOutcome(TerrainUpdatesRoot), automatic = false };
    }
    private static object TerrainPreparedReview(PreparedUpdate value) => new
    {
        token = value.Id, version = value.Package.Version, edition = value.Package.Edition, commit = value.Package.SourceCommit,
        archiveSha256 = value.ArchiveSha256, files = value.Files.Count, value.IsRollback,
        signature = new { value.Signature.Status, value.Signature.Publisher, value.Signature.CertificateSha256, value.Signature.Description },
        backup = value.BackupRoot
    };
    private static object TerrainOfficialReview(OfficialUpdate value, string token) => new
    {
        token, value.Version, value.Tag, value.Commit, value.Edition, value.ArchiveName, value.ArchiveSha256, value.Bytes, value.PublishedAt,
        newer = Version.TryParse(value.Version, out var next) && Version.TryParse(AppPaths.ApplicationVersion, out var current) && next > current
    };
    private async Task<object?> TerrainSelectUpdateAsync(JsonElement args, CancellationToken ct)
    {
        var path = await Host.OpenFileAsync("Choisir un pack Windows GLPI Pilot", "Pack ZIP (*.zip)|*.zip"); if (path is null) return new { cancelled = true };
        ct.ThrowIfCancellationRequested(); var info = new FileInfo(path); if (!info.Exists || info.Length is <= 0 or > 768L * 1024 * 1024) throw new InvalidDataException("Choisissez une archive existante de moins de 768 Mio.");
        _terrainUpdateArchive = path; _terrainPreparedUpdate = null; return new { name = info.Name, bytes = info.Length };
    }
    private async Task<object?> TerrainPrepareLocalUpdateAsync(JsonElement args, CancellationToken ct)
    {
        var path = _terrainUpdateArchive ?? throw new InvalidOperationException("Choisissez l’archive depuis le sélecteur de fichiers.");
        var hash = Text(args, "sha256", 64).Trim(); var commit = Text(args, "commit", 40).Trim();
        var policy = UpdatePolicy.Load(DataPath("update-policy.json")); _terrainPreparedUpdate = null;
        Publish("update.progress", new { message = "Vérification du pack local, de sa provenance et de tous ses fichiers…" });
        var prepared = await UpdateService.PrepareAsync(path, hash, commit, TerrainInstallRoot, TerrainUpdatesRoot, AppPaths.ApplicationVersion, policy, ct: ct);
        ct.ThrowIfCancellationRequested(); _terrainPreparedUpdate = prepared; _terrainPreparedPolicy = TerrainDigest(policy);
        return TerrainPreparedReview(prepared);
    }
    private async Task<object?> TerrainFindUpdateAsync(JsonElement args, CancellationToken ct)
    {
        if (!Flag(args, "consent")) throw new InvalidDataException("Lancez explicitement la recherche de publication sur GitHub.");
        var edition = Text(args, "edition", 10, "PROD"); if (edition is not ("PROD" or "DEV")) throw new InvalidDataException("Édition attendue : PROD ou DEV.");
        Publish("update.progress", new { message = "Lecture de la dernière publication du dépôt officiel, de son commit et de ses empreintes…" });
        using var client = new UpdateReleaseClient(); var value = await client.FindLatestAsync(edition, ct); ct.ThrowIfCancellationRequested();
        _terrainOfficialUpdate = value; _terrainOfficialToken = Guid.NewGuid().ToString("N"); return TerrainOfficialReview(value, _terrainOfficialToken);
    }
    private async Task<object?> TerrainDownloadUpdateAsync(JsonElement args, CancellationToken ct)
    {
        if (!Flag(args, "consent")) throw new InvalidDataException("Confirmez le téléchargement du pack officiel affiché.");
        var release = _terrainOfficialUpdate ?? throw new InvalidOperationException("Vérifiez d’abord la dernière publication officielle.");
        if (Text(args, "token", 40) != _terrainOfficialToken) throw new InvalidOperationException("La publication affichée a changé. Rechargez ses informations avant le téléchargement.");
        if (!Version.TryParse(release.Version, out var next) || !Version.TryParse(AppPaths.ApplicationVersion, out var current) || next <= current)
            throw new InvalidOperationException("La publication officielle ne dépasse pas la version installée. Utilisez la sauvegarde vérifiée pour un retour arrière.");
        var policy = UpdatePolicy.Load(DataPath("update-policy.json")); _terrainPreparedUpdate = null;
        using var client = new UpdateReleaseClient(); var progress = new Progress<long>(bytes => Publish("update.progress", new { message = "Téléchargement du pack officiel…", bytes, total = release.Bytes }));
        var archive = await client.DownloadAsync(release, Path.Combine(TerrainUpdatesRoot, "downloads"), progress, ct);
        try
        {
            Publish("update.progress", new { message = "Vérification de l’archive, du manifeste et de la signature de l’exécutable…" });
            var prepared = await UpdateService.PrepareAsync(archive, release.ArchiveSha256, release.Commit, TerrainInstallRoot, TerrainUpdatesRoot, AppPaths.ApplicationVersion, policy, ct: ct);
            ct.ThrowIfCancellationRequested(); _terrainPreparedUpdate = prepared; _terrainPreparedPolicy = TerrainDigest(policy); return TerrainPreparedReview(prepared);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }
    private Task<object?> TerrainPrepareRollbackAsync(JsonElement args, CancellationToken ct)
    {
        if (!Flag(args, "confirmed")) throw new InvalidDataException("Confirmez la préparation de la version antérieure conservée.");
        _terrainPreparedUpdate = null; Publish("update.progress", new { message = "Vérification de la sauvegarde des binaires de cette installation…" });
        var installed = UpdateService.FindRollback(TerrainUpdatesRoot, TerrainInstallRoot) ?? throw new InvalidOperationException("Aucune version antérieure vérifiée n’est conservée pour cette installation.");
        var prepared = UpdateService.PrepareRollback(installed, TerrainUpdatesRoot, ct); ct.ThrowIfCancellationRequested();
        _terrainPreparedUpdate = prepared; _terrainPreparedPolicy = TerrainDigest(UpdatePolicy.Load(DataPath("update-policy.json")));
        return Task.FromResult<object?>(TerrainPreparedReview(prepared));
    }
    private async Task<object?> TerrainInstallUpdateAsync(JsonElement args, CancellationToken ct)
    {
        var prepared = _terrainPreparedUpdate ?? throw new InvalidOperationException("Vérifiez un pack avant de lancer son installation.");
        if (!Flag(args, "confirmed") || Text(args, "token", 40) != prepared.Id || Text(args, "version", 32) != prepared.Package.Version)
            throw new InvalidDataException("Confirmez la fermeture et l’installation de la version vérifiée affichée.");
        if (_terrainPreparedPolicy != TerrainDigest(UpdatePolicy.Load(DataPath("update-policy.json"))))
            throw new InvalidOperationException("La politique de confiance a changé. Vérifiez de nouveau le pack avant installation.");
        if (_terrainTourDirty || _terrainCityDraft is not null && TerrainDigest(_terrainCityDraft) != _terrainCityBaseline)
            throw new InvalidOperationException("Enregistrez ou abandonnez vos brouillons de référentiel et de tournée avant de fermer l’application pour sa mise à jour.");
        ct.ThrowIfCancellationRequested(); SaveSettings();
        UpdateService.LaunchUpdater(prepared); _terrainPreparedUpdate = null;
        Log("MISE À JOUR · pack " + prepared.Package.Version + " vérifié ; fermeture pour installation avec sauvegarde.");
        await Host.CloseAsync(); return new { closing = true, version = prepared.Package.Version };
    }
}
