using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    // Le pack allégé n’emporte ni le runtime WebView2 fixe, ni le pilote Playwright, ni le build Firefox.
    // L’opérateur dépose le ZIP de dépendance sur le poste et l’installe ici, sans réseau.
    private void RegisterComponentOperations()
    {
        Register("components.list", async (_, ct) =>
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested();
            return new
            {
                known = PackComponents.Known.Select(id => new
                {
                    id,
                    label = PackComponents.DisplayName(id),
                    installed = PackComponents.IsInstalled(id),
                    component = PackComponents.Describe(id),
                    missing = PackComponents.IsInstalled(id) ? "" : PackComponents.MissingMessage(id)
                }).ToArray(),
                folder = PackComponents.Root("firefox") is { } sample ? Path.GetDirectoryName(sample) ?? "" : ""
            };
        });

        Register("components.install", async (args, ct) =>
        {
            var archive = Text(args, "archive", 4000);
            var expected = Text(args, "sha256", 80);
            if (string.IsNullOrWhiteSpace(archive)) throw new InvalidDataException("Indiquez le ZIP de dépendance à installer.");
            if (!File.Exists(archive)) throw new FileNotFoundException("Le ZIP de dépendance est introuvable sur ce poste.", archive);
            Log("COMPOSANTS · vérification puis installation de " + Path.GetFileName(archive) + ".");
            var installed = await Task.Run(() => PackComponents.Install(archive, expected), ct);
            Log("COMPOSANTS · " + PackComponents.DisplayName(installed.Id) + " installé en version " + installed.Version + ".");
            return new { installed, message = PackComponents.DisplayName(installed.Id) + " est installé. Redémarrez GLPI Pilot pour qu’il soit utilisé partout." };
        });
    }
}
