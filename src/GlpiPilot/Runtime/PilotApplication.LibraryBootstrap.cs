using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private string _libraryBootstrapToken = "", _libraryBootstrapPath = "", _libraryBootstrapDigest = "", _libraryBootstrapInstallation = "";
    private DateTimeOffset _libraryBootstrapExpires;
    private void RegisterLibraryBootstrapOperations()
    {
        Register("library.bootstrap.export", async (a, ct) =>
        {
            if (IsTestMode || !Flag(a, "confirmedInternalUse")) throw new InvalidOperationException("Confirmez un transfert interne vers des destinataires habilités. Le paquet contient les contenus privés accessibles et n’est pas chiffré.");
            var installation = LibraryCaptureContext(Settings, LibrarySettings).Installation;
            var path = await Host.SaveFileAsync("Bibliothèque interne : destinataires habilités uniquement", "Bibliothèque GLPI Pilot|*.glpilibrary.zip", "GLPI-Pilot-bibliotheque-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".glpilibrary.zip").ConfigureAwait(false);
            if (path is null) return new { cancelled = true };
            await TicketLibrary.ExportBootstrapAsync(path, installation, ct).ConfigureAwait(false);
            return new { cancelled = false, path, message = "Bibliothèque exportée sans identifiants, paramètres personnels ni programmation." };
        });
        Register("library.bootstrap.preview", async (_, ct) =>
        {
            if (IsTestMode) throw new InvalidOperationException("L’import d’une bibliothèque réelle est désactivé dans la simulation.");
            ClearLibraryBootstrapPreview();
            var path = await Host.OpenFileAsync("Importer une bibliothèque interne autorisée", "Bibliothèque GLPI Pilot|*.glpilibrary.zip").ConfigureAwait(false);
            if (path is null) return new { cancelled = true };
            var installation = LibraryCaptureContext(Settings, LibrarySettings).Installation;
            var folder = DataPath("BootstrapStaging"); Directory.CreateDirectory(folder);
            SharedFolderWorkspace.RejectLink(folder);
            var staged = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".glpilibrary.zip");
            try
            {
                await using (var input = LibraryBootstrapFormat.Open(path))
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                var preview = await Task.Run(() => LibraryBootstrapFormat.Inspect(staged, installation, ct), ct).ConfigureAwait(false);
                _libraryBootstrapToken = Guid.NewGuid().ToString("N"); _libraryBootstrapPath = staged;
                _libraryBootstrapDigest = preview.ArchiveSha256; _libraryBootstrapInstallation = installation; _libraryBootstrapExpires = DateTimeOffset.UtcNow.AddMinutes(15);
                return new { cancelled = false, token = _libraryBootstrapToken, preview, expiresAt = _libraryBootstrapExpires,
                    warning = "Ce paquet interne non chiffré peut contenir des informations privées. L’import conserve les versions concurrentes et ne partage pas automatiquement les données importées." };
            }
            catch { if (File.Exists(staged)) File.Delete(staged); throw; }
        });
        Register("library.bootstrap.cancel", (_, _) => { ClearLibraryBootstrapPreview(); return Task.FromResult<object?>(new { cancelled = true }); });
        Register("library.bootstrap.apply", async (a, ct) =>
        {
            if (IsTestMode || !Flag(a, "confirmedAuthorized") || !Flag(a, "confirmedMerge") || Text(a, "token", 32) != _libraryBootstrapToken
                || _libraryBootstrapToken.Length == 0 || DateTimeOffset.UtcNow >= _libraryBootstrapExpires
                || LibraryCaptureContext(Settings, LibrarySettings).Installation != _libraryBootstrapInstallation)
                throw new InvalidOperationException("Aperçu expiré ou confirmation de transfert manquante. Aucun import effectué.");
            var path = _libraryBootstrapPath; var digest = _libraryBootstrapDigest; var installation = _libraryBootstrapInstallation;
            _libraryBootstrapToken = "";
            try
            {
                var result = await TicketLibrary.ImportBootstrapAsync(path, digest, installation, ct).ConfigureAwait(false);
                Publish("library.changed", new { imported = true }); return result;
            }
            finally { ClearLibraryBootstrapPreview(); }
        });
    }
    private void ClearLibraryBootstrapPreview()
    {
        var path = _libraryBootstrapPath; _libraryBootstrapToken = ""; _libraryBootstrapPath = ""; _libraryBootstrapDigest = ""; _libraryBootstrapInstallation = "";
        if (path.Length > 0 && File.Exists(path)) File.Delete(path);
    }
}
