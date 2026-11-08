using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

public sealed record SharedUpdateOffer(string Folder, string ArchiveName, string Version, string Sha256, long Bytes);
public sealed record SharedSiteOffer(string Folder, string Name, string Sha256, long Bytes);

/// <summary>A user-selected Windows share is read as a distribution source; all execution stays local.</summary>
public static class SharedUpdateSource
{
    public const string SiteFile = "GLPI-Pilot-Sites-v1.csv", SiteHashes = "GLPI-Pilot-Sites.sha256";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string ValidateFolder(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 3000 || value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("Choisissez un dossier absolu ou un partage réseau Windows accessible, par exemple \\\\serveur\\diffusion\\GLPI-Pilot.");
        var full = Path.GetFullPath(value);
        return full.Length == Path.GetPathRoot(full)?.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static async Task<string> TextFile(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("Le manifeste de diffusion dépasse 1 Mio.");
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: true);
        var content = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (content.Length + read > 1024 * 1024) throw new InvalidDataException("Manifeste trop volumineux.");
            content.Append(buffer, 0, read);
        }
        return content.ToString();
    }
    public static async Task<SharedUpdateOffer> FindAsync(string folder, CancellationToken ct = default) => await FindAsync(folder, "PROD", ct);
    // The edition is the installed one (UpdateService.InstalledEdition): a light installation only takes PROD-LIGHT packs.
    public static async Task<SharedUpdateOffer> FindAsync(string folder, string edition, CancellationToken ct = default)
    {
        if (edition is not ("PROD" or "PROD-LIGHT")) throw new InvalidDataException("Édition attendue sur le partage : PROD ou PROD-LIGHT.");
        folder = ValidateFolder(folder); var hashes = UpdateService.ParseHashes(await TextFile(Path.Combine(folder, "SHA256SUMS.txt"), ct));
        var candidates = hashes.Select(pair => (Pair: pair, Match: Regex.Match(pair.Key, @"^GLPI-Pilot_(\d+\.\d+\.\d+)_" + Regex.Escape(edition) + @"_Windows-x64\.zip$", RegexOptions.CultureInvariant)))
            .Where(item => item.Match.Success).Select(item => (item.Pair, Version: Version.Parse(item.Match.Groups[1].Value))).OrderByDescending(item => item.Version).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("Le partage ne contient aucun pack " + edition + " Windows x64 déclaré dans SHA256SUMS.txt.");
        if (candidates.Count(item => item.Version == candidates[0].Version) != 1) throw new InvalidDataException("Plusieurs archives correspondent à la même version " + edition + ". Corrigez le manifeste du partage.");
        var chosen = candidates[0]; var info = new FileInfo(Path.Combine(folder, chosen.Pair.Key));
        if (!info.Exists || info.Length is <= 0 or > UpdateService.MaximumArchiveBytes) throw new InvalidDataException("Le pack annoncé est absent, incomplet ou dépasse la taille autorisée.");
        return new(folder, chosen.Pair.Key, chosen.Version.ToString(3), chosen.Pair.Value, info.Length);
    }
    public static async Task<SharedSiteOffer> FindSitesAsync(string folder, CancellationToken ct = default)
    {
        folder = ValidateFolder(folder); var hashes = UpdateService.ParseHashes(await TextFile(Path.Combine(folder, SiteHashes), ct));
        if (hashes.Count != 1 || !hashes.TryGetValue(SiteFile, out var hash)) throw new InvalidDataException("Le manifeste du patch doit désigner uniquement " + SiteFile + ".");
        var info = new FileInfo(Path.Combine(folder, SiteFile));
        if (!info.Exists || info.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Le patch de sites est absent, vide ou dépasse 8 Mio.");
        return new(folder, SiteFile, hash, info.Length);
    }
    public static async Task<string> CopyVerifiedAsync(string folder, string name, string hash, long bytes, string localRoot, CancellationToken ct = default)
    {
        folder = ValidateFolder(folder);
        if (Path.GetFileName(name) != name || name.Contains('\\') || !UpdateService.IsHash(hash) || bytes is <= 0 or > UpdateService.MaximumArchiveBytes)
            throw new InvalidDataException("Référence de diffusion invalide.");
        UpdateService.EnsureNoLinks(localRoot); Directory.CreateDirectory(localRoot);
        var path = Path.Combine(localRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(name));
        try
        {
            await using (var input = new FileStream(Path.Combine(folder, name), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                if (input.Length != bytes) throw new InvalidDataException("Le fichier partagé a changé depuis sa vérification. Recherchez de nouveau la publication.");
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if ((total += read) > bytes) throw new InvalidDataException("Le fichier partagé a changé pendant la copie.");
                    digest.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total != bytes || !Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La copie ne correspond pas à l’empreinte publiée sur le partage. Le fichier peut être incomplet ou modifié.");
                await output.FlushAsync(ct);
            }
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public static UpdatePackageMetadata ReadCopiedMetadata(string zipPath, SharedUpdateOffer offer)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Where(entry => entry.FullName == "wad/version.json").ToArray();
        if (entries.Length != 1 || entries[0].Length > 32768) throw new InvalidDataException("Métadonnées de version absentes, ambiguës ou trop volumineuses.");
        using var input = entries[0].Open(); using var reader = new StreamReader(input, Utf8);
        var content = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            if (content.Length + read > 32768) throw new InvalidDataException("Métadonnées trop volumineuses.");
            content.Append(buffer, 0, read);
        }
        var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(content.ToString()) ?? throw new InvalidDataException("Métadonnées vides.");
        if (metadata.Application != "GLPI Pilot" || metadata.Target != "win-x64" || metadata.Edition != (offer.ArchiveName.Contains("_PROD-LIGHT_", StringComparison.Ordinal) ? "PROD-LIGHT" : "PROD") || metadata.Version != offer.Version
            || metadata.SourceCommit.Length != 40 || !metadata.SourceCommit.All(Uri.IsHexDigit)) throw new InvalidDataException("Le pack copié ne correspond pas à la version et à l’édition annoncées sur le partage.");
        return metadata;
    }
}
