using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GlpiPilot.Core;

// Un seul endroit décide quels navigateurs le pilotage accepte et lesquels le pack peut réellement lancer.
// Playwright ne sait piloter Edge et Chrome que par leur canal installé, mais Firefox uniquement par son
// propre build embarqué : il n'existe aucun canal « Firefox du système ».
public static class BrowserChannels
{
    public const string Edge = "msedge", Chrome = "chrome", Firefox = "firefox";

    public static readonly string[] Supported = { Edge, Chrome, Firefox };

    public static bool IsSupported(string? channel) => channel is Edge or Chrome or Firefox;

    public static string UnavailableMessage(string? channel) => channel == Firefox
        ? "Firefox n’est pas fourni dans cette édition du pack. Choisissez Edge ou Chrome, ou installez le pack complet."
        : "Ce navigateur n’est pas pris en charge par le pilotage. Choisissez Edge, Chrome ou Firefox.";

    // Le build Playwright de Firefox est lié à la redistribuable Visual C++ : sans ces DLL système, le
    // processus meurt à peine lancé et Playwright ne rend qu'un échec de lancement sans aucune cause utile.
    public static string FirefoxMissingRuntimeMessage() =>
        FirefoxMissingRuntimeMessage(FirefoxRequiredSystemDlls.Where(dll => !SystemDllPresent(dll)).ToArray());

    public static string FirefoxMissingRuntimeMessage(IReadOnlyList<string> missingDlls) => missingDlls.Count == 0
        ? "Le build Firefox du pack est exécutable sur ce poste."
        : "Le build Firefox fourni par le pack nécessite la redistribuable Microsoft Visual C++ 2015-2022 : "
          + string.Join(", ", missingDlls) + " absente" + (missingDlls.Count > 1 ? "s" : "") + " de ce poste. "
          + "Installez cette redistribuable, ou choisissez Edge ou Chrome dans Réglages.";

    private static readonly string[] FirefoxRequiredSystemDlls = { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll" };

    // Le build embarqué est un win64 : ses DLL de liaison sont celles du System32 du poste, pas du pack.
    public static bool HasFirefoxSystemDependencies() => FirefoxRequiredSystemDlls.All(SystemDllPresent);

    private static bool SystemDllPresent(string dll)
    {
        try { return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dll)); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return false; }
    }

    public static bool IsAvailable(string? channel) => channel switch
    {
        Firefox => BundledFirefoxDirectory() is not null,
        _ => IsSupported(channel)
    };

    // Le build Playwright de Firefox vit à côté du pilote, jamais dans le Firefox installé par l’utilisateur.
    public static string? BundledFirefoxDirectory()
    {
        foreach (var root in SearchRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> candidates;
            try { candidates = Directory.EnumerateDirectories(root, "firefox-*"); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException) { continue; }
            // Le plus haut numéro de version gagne ; Ordinal suffit car Playwright nomme firefox-<digits>.
            var best = candidates.Where(CarriesFirefoxExecutable).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            if (best is not null) return best;
        }
        return null;
    }

    private static bool CarriesFirefoxExecutable(string directory)
    {
        foreach (var candidate in new[] { Path.Combine(directory, "firefox", "firefox.exe"), Path.Combine(directory, "firefox.exe") })
            if (File.Exists(candidate)) return true;
        return false;
    }

    private static IEnumerable<string> SearchRoots()
    {
        var component = PackComponents.InstalledRootContaining(PackComponents.Firefox, ".local-browsers");
        if (component is not null) yield return Path.Combine(component, ".local-browsers");
        var driverSearch = Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH");
        if (!string.IsNullOrWhiteSpace(driverSearch)) yield return Path.Combine(driverSearch, ".local-browsers");
        yield return Path.Combine(AppContext.BaseDirectory, "wad", ".local-browsers");
    }
}
