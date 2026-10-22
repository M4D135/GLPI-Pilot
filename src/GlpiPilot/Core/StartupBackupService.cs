using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record StartupBackupResult(bool VersionChanged, string PreviousVersion, string Path, int Files, string Message);

/// <summary>Copies a bounded allowlist of local preferences before the next version reads them. Never copies browser sessions.</summary>
public static class StartupBackupService
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const long MaximumBackupBudgetBytes = 256L * 1024 * 1024;
    public const int MaximumBackups = 12;
    private const string MarkerName = "last-ready-version.txt";
    private static readonly string[] Names = { "settings.json", "technician-preferences.json", "workplace.json", "dispatch-scenario.json", "tour-order.json",
        "preview-view.json", "table-views.json", "city-overrides.json", "alerts.json" };

    public static StartupBackupResult Prepare(string root, string version)
    {
        RequireVersion(version);
        root = Path.GetFullPath(root);
        var marker = Path.Combine(root, MarkerName);
        var previous = File.Exists(marker) && new FileInfo(marker).Length <= 120 ? File.ReadAllText(marker).Trim() : "";
        if (previous == version) return new(false, previous, "", 0, "Version déjà ouverte avec succès.");
        var inputs = new List<(string Name, byte[] Content, string Digest)>();
        foreach (var name in Names)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Une préférence liée à un autre emplacement ne peut pas être sauvegardée automatiquement : " + name);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumFileBytes) throw new InvalidDataException("La préférence " + name + " dépasse 16 Mio ; la sauvegarde automatique n’a pas remplacé le fichier source.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            inputs.Add((name, bytes, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        if (inputs.Count == 0) return new(true, previous, "", 0, "Premier lancement, aucune préférence existante à sauvegarder.");
        var manifest = string.Join("\n", inputs.Select(item => item.Name + "|" + item.Content.Length + "|" + item.Digest));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)))[..20];
        var folder = Path.Combine(root, "Backups");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "before-" + version + "-" + fingerprint + ".zip");
        if (File.Exists(destination))
        {
            Verify(destination, inputs);
            return new(true, previous, destination, inputs.Count, "Sauvegarde identique déjà vérifiée avant cette version.");
        }
        var backups = Directory.EnumerateFiles(folder, "before-*.zip").Select(path => new FileInfo(path)).ToArray();
        if (backups.Length >= MaximumBackups || backups.Sum(file => file.Length) + inputs.Sum(item => (long)item.Content.Length) > MaximumBackupBudgetBytes)
            throw new IOException("Le dossier Backups contient déjà 12 sauvegardes ou approche 256 Mio. Archivez les anciennes sauvegardes pour permettre la suivante.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var input in inputs)
                {
                    using var output = zip.CreateEntry(input.Name, CompressionLevel.Optimal).Open(); output.Write(input.Content);
                }
                var metadata = new { version = 1, beforeApplicationVersion = version, previousApplicationVersion = previous, createdAt = DateTimeOffset.Now,
                    files = inputs.Select(item => new { name = item.Name, bytes = item.Content.Length, sha256 = item.Digest }).ToArray() };
                using var manifestStream = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(manifestStream, metadata);
            }
            Verify(temporary, inputs);
            File.Move(temporary, destination);
            return new(true, previous, destination, inputs.Count, "Préférences sauvegardées et empreintes vérifiées avant le changement de version.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void MarkReady(string root, string version)
    {
        RequireVersion(version); Directory.CreateDirectory(root);
        var path = Path.Combine(root, MarkerName); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, version, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Verify(string path, IReadOnlyList<(string Name, byte[] Content, string Digest)> inputs)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumBackupBudgetBytes) throw new InvalidDataException("La sauvegarde locale dépasse sa limite.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count != inputs.Count + 1 || archive.GetEntry("manifest.json") == null)
            throw new InvalidDataException("Le catalogue de sauvegarde est incomplet.");
        foreach (var input in inputs)
        {
            var entry = archive.GetEntry(input.Name);
            if (entry == null || entry.Length != input.Content.Length) throw new InvalidDataException("Une préférence manque dans la sauvegarde : " + input.Name);
            using var entryStream = entry.Open();
            if (Convert.ToHexString(SHA256.HashData(entryStream)) != input.Digest)
                throw new InvalidDataException("La vérification de sauvegarde a échoué : " + input.Name);
        }
    }

    private static void RequireVersion(string version)
    {
        if (version.Length is < 1 or > 80 || version.Any(character => !char.IsAsciiDigit(character) && character != '.'))
            throw new ArgumentException("La version de l’application n’est pas reconnue.", nameof(version));
    }
}
