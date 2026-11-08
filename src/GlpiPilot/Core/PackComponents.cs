using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

// Un composant est une dépendance lourd que le pack allégé ne transporte pas : runtime WebView2 fixe,
// pilote Playwright, build Firefox. Il arrive sur le poste par un ZIP déposé hors ligne, vérifié par
// empreinte, et son absence doit être annoncée comme telle plutôt que de faire échouer l’application.
public sealed record PackComponent(string Id, string Version, string Sha256, long Bytes, DateTimeOffset InstalledAt);

public static class PackComponents
{
    public const string WebView2Fixed = "webview2-fixed";
    public const string PlaywrightDriver = "playwright-driver";
    public const string Firefox = "firefox";

    public static readonly string[] Known = { WebView2Fixed, PlaywrightDriver, Firefox };

    public static string DisplayName(string id) => id switch
    {
        WebView2Fixed => "Runtime WebView2 à version fixe",
        PlaywrightDriver => "Pilote Playwright et son nœud",
        Firefox => "Build Firefox pilotable",
        _ => id
    };

    public static string MissingMessage(string id) => DisplayName(id) + " n’est pas installé sur ce poste. "
        + (id == WebView2Fixed ? "Téléchargez le ZIP WebView2 du Socle (" + SocleReleases + "), puis installez-le dans Réglages · Composants."
            : "Déposez le ZIP de dépendance correspondant, puis installez-le dans Réglages · Composants.");

    private static string ComponentsRoot => Path.Combine(AppPaths.DataRoot, "Components");
    public static string Root(string id) => Path.Combine(ComponentsRoot, SafeId(id));
    private static string RegistryFile => Path.Combine(AppPaths.DataRoot, "components.json");

    private static string SafeId(string id)
    {
        if (!Known.Contains(id, StringComparer.Ordinal))
            throw new InvalidDataException("Composant inconnu : " + id);
        return id;
    }

    // Renvoie le dossier du composant seulement s'il contient vraiment ce qui est attendu.
    public static string? InstalledRootContaining(string id, string relativeMarker)
    {
        if (!IsInstalled(id)) return null;
        try { return Directory.Exists(Path.Combine(Root(id), relativeMarker)) ? Root(id) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static bool IsInstalled(string id) => ManifestPath(id) is { } manifest && File.Exists(manifest);

    public static string? ManifestPath(string id)
    {
        try { return Path.Combine(Root(id), "component.json"); }
        catch (ArgumentException) { return null; }
    }

    public static PackComponent? Describe(string id)
    {
        var path = ManifestPath(id);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new PackComponent(
                root.GetProperty("id").GetString() ?? id,
                root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                root.TryGetProperty("archive_sha256", out var s) ? s.GetString() ?? "" : "",
                root.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bytes) ? bytes : 0,
                root.TryGetProperty("installed_at", out var d) && DateTimeOffset.TryParse(d.GetString(), out var at) ? at : DateTimeOffset.MinValue);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static IReadOnlyList<PackComponent> Installed() =>
        Known.Select(Describe).OfType<PackComponent>().ToList();

    // Le runtime WebView2 fixe est publié une seule fois pour tous les outils, dans le dépôt Socle Windows. Son ZIP
    // n’a ni component.json ni inventaire : il est reconnu à sa forme (runtime/webview2/ seul) et n’est accepté que si
    // son empreinte est l’une de celles épinglées ici, publiées dans le SHA256SUMS.txt de la release du Socle.
    public const string SocleReleases = "https://github.com/M4D135/socle-windows/releases";
    private static readonly Dictionary<string, string> SoclePins = new(StringComparer.Ordinal)
    {
        ["156.0.4314.42"] = "42dc4b13d8a79e7296692d684908c27e32863db432a103e2042b587fb64571b2"
    };
    public static IReadOnlyDictionary<string, string> SocleWebView2 => SoclePins;

    // Test runs only: the real Socle ZIP (310 Mo) is not available to the checks.
    public static void PinSocleWebView2ForTests(string version, string sha256)
    {
        if (AppPaths.TestDataRoot is null) throw new InvalidOperationException("Épinglage de test refusé hors mode test.");
        SoclePins[version] = sha256;
    }

    // Le ZIP doit contenir component.json et SHA256SUMS.txt à la racine ; chaque fichier listé est vérifié.
    // Seule exception : le ZIP WebView2 du Socle, vérifié par son empreinte épinglée.
    public static PackComponent Install(string archivePath, string expectedArchiveSha256)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Le ZIP de dépendance est introuvable.", archivePath);
        var archiveHash = Sha256File(archivePath);
        if (!string.IsNullOrWhiteSpace(expectedArchiveSha256) &&
            !string.Equals(archiveHash, expectedArchiveSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("L’empreinte du ZIP de dépendance ne correspond pas à celle qui a été publiée. Rien n’a été installé.");

        string staging = Path.Combine(ComponentsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            PackManifest manifest;
            if (archive.GetEntry("component.json") is { } manifestEntry)
            {
                manifest = ReadManifest(manifestEntry);
                VerifySums(archive);
            }
            else manifest = ReadSocleWebView2(archive, archiveHash);

            Directory.CreateDirectory(staging);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var destination = SafeDestination(staging, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            var stamped = new PackComponent(manifest.Id, manifest.Version, archiveHash, manifest.Bytes, DateTimeOffset.Now);
            File.WriteAllText(Path.Combine(staging, "component.json"), Serialize(stamped), new UTF8Encoding(false));

            var target = Root(manifest.Id);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
            WriteRegistry();
            return stamped;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private record PackManifest(string Id, string Version, string Sha256, long Bytes, DateTimeOffset InstalledAt);

    private static PackManifest ReadManifest(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var identity) ? identity.GetString() ?? "" : "";
        if (!Known.Contains(id, StringComparer.Ordinal)) throw new InvalidDataException("Composant inconnu : " + id);
        var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        var bytes = root.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var value) ? value : 0;
        return new PackManifest(id, version, "", bytes, DateTimeOffset.MinValue);
    }

    private static PackManifest ReadSocleWebView2(ZipArchive archive, string archiveHash)
    {
        const string prefix = "runtime/webview2/";
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).Select(entry => entry.FullName.Replace('\\', '/')).ToList();
        if (files.Count == 0 || files.Any(name => !name.StartsWith(prefix, StringComparison.Ordinal)) || !files.Contains(prefix + "msedgewebview2.exe"))
            throw new InvalidDataException("Le ZIP ne contient pas component.json et n’est pas un ZIP WebView2 du Socle (runtime/webview2/ uniquement).");
        var versions = files.Select(name => name[prefix.Length..]).Where(name => !name.Contains('/') && name.EndsWith(".manifest", StringComparison.Ordinal))
            .Select(name => name[..^".manifest".Length]).Where(name => Version.TryParse(name, out var parsed) && parsed.Revision >= 0).ToList();
        if (versions.Count != 1) throw new InvalidDataException("Le ZIP WebView2 du Socle ne déclare pas une version unique.");
        if (!SocleWebView2.TryGetValue(versions[0], out var pinned) || !string.Equals(pinned, archiveHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ce ZIP WebView2 n’est pas une release reconnue du Socle (version " + versions[0] + "). Rien n’a été installé. Releases : " + SocleReleases);
        return new PackManifest(WebView2Fixed, versions[0], "", archive.Entries.Sum(entry => entry.Length), DateTimeOffset.MinValue);
    }

    private static void VerifySums(ZipArchive archive)
    {
        var entry = archive.GetEntry("SHA256SUMS.txt") ?? throw new InvalidDataException("Le ZIP ne contient pas SHA256SUMS.txt.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        int verified = 0;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(new[] { "  " }, 2, StringSplitOptions.None);
            if (parts.Length != 2) throw new InvalidDataException("Ligne d’inventaire illisible dans SHA256SUMS.txt.");
            var expected = parts[0].Trim();
            var relative = parts[1].Trim().Replace('\\', '/');
            if (relative.Equals("SHA256SUMS.txt", StringComparison.Ordinal)) continue;
            var candidate = archive.GetEntry(relative) ?? throw new InvalidDataException("Fichier annoncé absent du ZIP : " + relative);
            using var stream = candidate.Open();
            using var hash = SHA256.Create();
            var actual = Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fichier corrompu dans le ZIP de dépendance : " + relative);
            verified++;
        }
        if (verified == 0) throw new InvalidDataException("L’inventaire du ZIP de dépendance est vide.");
    }

    private static string SafeDestination(string staging, string entryName)
    {
        var relative = entryName.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains("../", StringComparison.Ordinal) || relative.StartsWith("/"))
            throw new InvalidDataException("Chemin interdit dans le ZIP de dépendance : " + entryName);
        var destination = Path.GetFullPath(Path.Combine(staging, relative));
        if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Chemin hors du dossier du composant : " + entryName);
        return destination;
    }

    private static string Serialize(PackComponent component)
    {
        var payload = new Dictionary<string, object>
        {
            ["id"] = component.Id, ["version"] = component.Version, ["archive_sha256"] = component.Sha256,
            ["bytes"] = component.Bytes, ["installed_at"] = component.InstalledAt.ToString("O")
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static void WriteRegistry()
    {
        var payload = Installed().Select(c => new Dictionary<string, object>
        {
            ["id"] = c.Id, ["version"] = c.Version, ["archive_sha256"] = c.Sha256,
            ["bytes"] = c.Bytes, ["installed_at"] = c.InstalledAt.ToString("O")
        }).ToList();
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(RegistryFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
    }
}
