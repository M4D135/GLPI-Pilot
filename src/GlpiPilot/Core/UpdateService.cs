using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed class UpdatePolicy
{
    public bool RequireSignature { get; set; }
    public string TrustedPublisherSha256 { get; set; } = "";
    public void Validate()
    {
        if (TrustedPublisherSha256 == null || TrustedPublisherSha256.Length > 0 && !UpdateService.IsHash(TrustedPublisherSha256)
            || RequireSignature && TrustedPublisherSha256.Length == 0)
            throw new InvalidDataException("Une signature exigée doit désigner l’empreinte SHA-256 du certificat d’éditeur approuvé.");
    }
    public static UpdatePolicy Load(string path)
    {
        if (!File.Exists(path)) return new(); using var input = File.OpenRead(path); if (input.Length > 8192) throw new InvalidDataException("Politique de mise à jour trop volumineuse.");
        var value = JsonSerializer.Deserialize<UpdatePolicy>(input) ?? throw new InvalidDataException("Politique de mise à jour vide."); value.Validate(); return value;
    }
    public void Save(string path) { Validate(); UpdateService.WriteJson(path, this); }
}

public sealed class UpdatePackageMetadata
{
    [JsonPropertyName("application")] public string Application { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("distribution")] public string Edition { get; set; } = "";
    [JsonPropertyName("source_commit")] public string SourceCommit { get; set; } = "";
    [JsonPropertyName("executable_sha256")] public string ExecutableSha256 { get; set; } = "";
    [JsonPropertyName("admin_executable_sha256")] public string AdminExecutableSha256 { get; set; } = "";
    [JsonPropertyName("self_contained")] public bool SelfContained { get; set; }
    [JsonPropertyName("ui")] public string Ui { get; set; } = "";
    [JsonPropertyName("webview2_sdk")] public string WebView2Sdk { get; set; } = "";
    [JsonPropertyName("webview2_runtime")] public string WebView2Runtime { get; set; } = "";
    [JsonPropertyName("webview2_runtime_sha256")] public string WebView2RuntimeSha256 { get; set; } = "";
    [JsonPropertyName("webview2_loader")] public string WebView2Loader { get; set; } = "";
}
public sealed record UpdateSignature(string Status, string Publisher, string CertificateSha256)
{
    public string Description => Status == "Valid" ? "Signature Authenticode valide : " + Publisher : Status == "NotSigned" ? "Exécutable non signé ; provenance vérifiée par l’empreinte attendue et le commit." : "Signature non vérifiable sur ce système : " + Status;
}
public sealed class PreparedUpdate
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InstallRoot { get; set; } = "";
    public string WorkRoot { get; set; } = "";
    public string ArchiveSha256 { get; set; } = "";
    public UpdatePackageMetadata Package { get; set; } = new();
    public UpdateSignature Signature { get; set; } = new("NotChecked", "", "");
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PreviousFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string HealthNonce { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public int ParentProcessId { get; set; }
    public long ParentStartedUtcTicks { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string State { get; set; } = "prepared";
    public bool IsRollback { get; set; }
    public string CurrentExecutable { get; set; } = "GLPI-Pilot.exe";
    public string LaunchExecutable { get; set; } = "GLPI-Pilot.exe";
    [JsonIgnore] public string StageRoot => Path.Combine(WorkRoot, "payload");
    [JsonIgnore] public string BackupRoot => Path.Combine(WorkRoot, "backup");
    [JsonIgnore] public string TransactionPath => Path.Combine(WorkRoot, "transaction.json");
}
public sealed record UpdateOutcome(string State, string Detail, string Version, string BackupPath, DateTimeOffset At);

/// <summary>Verifies a complete pack before exposing any installation action. No extraction into the running installation.</summary>
public static partial class UpdateService
{
    public const long MaximumArchiveBytes = 768L * 1024 * 1024, MaximumPayloadBytes = 2L * 1024 * 1024 * 1024, MaximumFileBytes = 768L * 1024 * 1024;
    public const int MaximumFiles = 20000;
    public const string AdminExecutable = "GLPI-Pilot-Admin.exe";
    public const string DeploymentDefaultFileName = "GLPI-Pilot.deployment.json";
    public const string DeploymentDefaultRelativePath = "wad/" + DeploymentDefaultFileName;
    private static bool IsLauncher(string name) => name is "GLPI-Pilot.exe" or AdminExecutable;
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 12 };
    public static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsCommit(string? value) => value?.Length == 40 && value.All(Uri.IsHexDigit);
    public static string HashFile(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    public static async Task<PreparedUpdate> PrepareAsync(string zipPath, string expectedSha256, string expectedCommit, string installRoot, string updatesRoot,
        string currentVersion, UpdatePolicy policy, bool allowOlder = false, CancellationToken ct = default)
    {
        policy.Validate(); if (!IsHash(expectedSha256) || !IsCommit(expectedCommit)) throw new InvalidDataException("Indiquez l’empreinte SHA-256 de l’archive et le commit source de 40 caractères, obtenus depuis la publication de confiance.");
        var prepared = await Task.Run(() => PrepareArchive(zipPath, expectedSha256, expectedCommit, installRoot, updatesRoot, currentVersion, allowOlder, ct), ct);
        try
        {
            prepared.Signature = await CheckSignatureAsync(Path.Combine(prepared.StageRoot, "GLPI-Pilot.exe"), ct);
            ValidateTrust(prepared.Signature, policy);
            if (prepared.Files.ContainsKey(AdminExecutable))
                ValidateTrust(await CheckSignatureAsync(Path.Combine(prepared.StageRoot, AdminExecutable), ct), policy);
            // A distributor can remove the Admin launcher before delivery. An
            // ordinary update must retain that absence, including in its stage.
            if (!prepared.PreviousFiles.ContainsKey(AdminExecutable) && prepared.Files.Remove(AdminExecutable))
                File.Delete(Path.Combine(prepared.StageRoot, AdminExecutable));
            prepared.CurrentExecutable = AdministrationCapabilities.Enabled ? AdminExecutable : "GLPI-Pilot.exe";
            prepared.LaunchExecutable = AdministrationCapabilities.Enabled && prepared.Files.ContainsKey(AdminExecutable) ? AdminExecutable : "GLPI-Pilot.exe";
            ct.ThrowIfCancellationRequested(); WriteJson(prepared.TransactionPath, prepared); return prepared;
        }
        catch { DeleteOwnDirectory(prepared.WorkRoot); throw; }
    }

    internal static void ValidateTrust(UpdateSignature signature, UpdatePolicy policy)
    {
        policy.Validate();
        if (signature.Status is not ("Valid" or "NotSigned" or "Unavailable") || signature.Status == "Valid" && !IsHash(signature.CertificateSha256) || policy.RequireSignature && signature.Status != "Valid"
            || policy.TrustedPublisherSha256.Length > 0 && !string.Equals(signature.CertificateSha256, policy.TrustedPublisherSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La signature du pack ne correspond pas à la politique de confiance configurée : " + signature.Status + ".");
    }
    internal static PreparedUpdate PrepareArchive(string zipPath, string expectedSha256, string expectedCommit, string installRoot, string updatesRoot,
        string currentVersion, bool allowOlder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); installRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar); updatesRoot = Path.GetFullPath(updatesRoot);
        ValidateInstallRoot(installRoot); EnsureNoLinks(updatesRoot); Directory.CreateDirectory(updatesRoot);
        using var zipFile = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (zipFile.Length is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("L’archive doit être un ZIP non vide de moins de 768 Mio.");
        var archiveHash = HashStream(zipFile, MaximumArchiveBytes, ct); if (!archiveHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("L’empreinte de l’archive ne correspond pas à la publication attendue. Aucun fichier n’a été installé.");
        zipFile.Position = 0; using var archive = new ZipArchive(zipFile, ZipArchiveMode.Read, false);
        if (archive.Entries.Count is < 4 or > MaximumFiles + 5000) throw new InvalidDataException("Le nombre d’entrées de l’archive est invalide.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested(); var isDirectory = entry.FullName.EndsWith('/'); var name = ValidRelativePath(isDirectory ? entry.FullName.TrimEnd('/') : entry.FullName);
            if (!names.Add(name)) throw new InvalidDataException("L’archive contient un chemin dupliqué ou ambigu pour Windows.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || unixType is not (0 or 0x8000 or 0x4000)
                || !isDirectory && unixType == 0x4000 || isDirectory && unixType == 0x8000) throw new InvalidDataException("Les liens et entrées spéciales ou incohérentes ne sont pas acceptés dans une mise à jour.");
            if (isDirectory) { if (name != "wad" && !name.StartsWith("wad/", StringComparison.Ordinal)) throw new InvalidDataException("Le pack contient un dossier racine inattendu."); continue; }
            if (!IsLauncher(name) && !name.StartsWith("wad/", StringComparison.Ordinal)) throw new InvalidDataException("Le pack doit contenir les lanceurs GLPI-Pilot.exe, éventuellement GLPI-Pilot-Admin.exe, et wad à sa racine.");
            if (entry.Length < 0 || entry.Length > MaximumFileBytes || (total += entry.Length) > MaximumPayloadBytes || entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 250)
                throw new InvalidDataException("La taille décompressée ou le ratio de compression dépasse la limite autorisée.");
            files.Add(name, entry);
        }
        if (files.Count > MaximumFiles || !files.ContainsKey("GLPI-Pilot.exe") || !files.ContainsKey("wad/version.json") || !files.ContainsKey("wad/SHA256SUMS.txt")
            || !files.ContainsKey("wad/.playwright/node/win32_x64/node.exe") || !files.ContainsKey("wad/.playwright/package/cli.js")) throw new InvalidDataException("Le pack est incomplet : exécutable, inventaire, provenance ou pilote Windows manquant.");
        var hashes = ParseHashes(ReadEntry(files["wad/SHA256SUMS.txt"], 3 * 1024 * 1024, ct));
        if (hashes.Count != files.Count - 1 || hashes.Keys.Any(name => !files.ContainsKey(name) || name == "wad/SHA256SUMS.txt")) throw new InvalidDataException("L’inventaire SHA-256 ne couvre pas exactement tous les fichiers du pack.");
        var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(ReadEntry(files["wad/version.json"], 32768, ct), JsonOptions) ?? throw new InvalidDataException("Provenance de version vide.");
        ValidateMetadata(metadata, expectedCommit, hashes["GLPI-Pilot.exe"]);
        if (files.ContainsKey(AdminExecutable) != (metadata.AdminExecutableSha256.Length > 0)
            || files.ContainsKey(AdminExecutable) && (!IsHash(metadata.AdminExecutableSha256) || metadata.AdminExecutableSha256 != hashes[AdminExecutable]))
            throw new InvalidDataException("Le lanceur Admin ne correspond pas à l’inventaire et aux métadonnées du pack.");
        ValidateWebViewPackage(metadata, files.Keys);
        EnsureSameLoader(installRoot, metadata);
        if (!Version.TryParse(currentVersion, out var current) || !Version.TryParse(metadata.Version, out var next) || !allowOlder && next <= current) throw new InvalidDataException("Le pack doit être plus récent que la version installée. Le retour à une version conservée dispose de sa propre action.");
        var prepared = new PreparedUpdate { InstallRoot = installRoot, WorkRoot = Path.Combine(updatesRoot, Guid.NewGuid().ToString("N")), ArchiveSha256 = archiveHash, Package = metadata, Files = hashes };
        if (Within(prepared.WorkRoot, installRoot) || Within(installRoot, prepared.WorkRoot)) throw new InvalidDataException("Le dossier de préparation doit être séparé du dossier d’installation.");
        try
        {
            Directory.CreateDirectory(prepared.StageRoot);
            foreach (var (name, entry) in files)
            {
                ct.ThrowIfCancellationRequested(); var destination = Combine(prepared.StageRoot, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long written = 0; int count;
                while ((count = input.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); if ((written += count) > entry.Length) throw new InvalidDataException("Un fichier décompressé dépasse sa taille déclarée."); hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count); }
                if (written != entry.Length) throw new InvalidDataException("Un fichier décompressé est tronqué.");
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (name == "wad/SHA256SUMS.txt") prepared.Files.Add(name, actual);
                else if (!actual.Equals(hashes[name], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("L’empreinte d’un fichier du pack est incorrecte : " + name);
            }
            prepared.PreviousFiles = Inventory(installRoot, ct);
            PreserveDeploymentDefault(prepared, restoringBackup: false, ct);
            VerifyInventory(prepared.StageRoot, prepared.Files, ct);
            WriteJson(prepared.TransactionPath, prepared); return prepared;
        }
        catch { DeleteOwnDirectory(prepared.WorkRoot); throw; }
    }

    public static Dictionary<string, string> ParseHashes(string document)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in document.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimEnd('\r'); if (value.Length < 67 || value[64..66] != "  " || !IsHash(value[..64])) throw new InvalidDataException("Une ligne de l’inventaire SHA-256 est invalide.");
            var name = ValidRelativePath(value[66..]); if (!result.TryAdd(name, value[..64].ToLowerInvariant()) || result.Count > MaximumFiles) throw new InvalidDataException("L’inventaire SHA-256 contient des doublons ou trop de fichiers.");
        }
        return result;
    }
    private static void ValidateMetadata(UpdatePackageMetadata metadata, string commit, string executableHash)
    {
        if (metadata.Application != "GLPI Pilot" || metadata.Target != "win-x64" || metadata.Edition is not ("PROD" or "PROD-LIGHT" or "DEV") || !metadata.SelfContained
            || !Version.TryParse(metadata.Version, out var version) || version.Build < 0 || metadata.Version.Length > 40 || !IsCommit(metadata.SourceCommit)
            || !metadata.SourceCommit.Equals(commit, StringComparison.OrdinalIgnoreCase) || !IsHash(metadata.ExecutableSha256) || !metadata.ExecutableSha256.Equals(executableHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La provenance du pack ne correspond pas au produit, à la plateforme, au commit ou à l’exécutable attendu.");
    }

    // Complete packs (PROD, DEV) carry the fixed Microsoft runtime; the light pack (PROD-LIGHT) relies on the
    // WebView2 Evergreen runtime installed on Windows. The two kinds never replace one another through an update.
    public const string BundledLoader = "bundled-native-x64";
    public const string EvergreenLoader = "evergreen-installed";
    internal static readonly string[] RequiredLightWebViewFiles =
    [
        "wad/web/index.html", "wad/web/css/app.css", "wad/web/js/app.js", "wad/web/js/bridge.js", "wad/web/js/ui.js",
        "wad/licenses/WebView2-SDK-LICENSE.txt"
    ];
    internal static readonly string[] RequiredWebViewFiles =
    [
        .. RequiredLightWebViewFiles,
        "wad/runtime/webview2/msedgewebview2.exe", "wad/runtime/webview2/msedge.dll", "wad/runtime/webview2/msedge_elf.dll",
        "wad/runtime/webview2/icudtl.dat", "wad/runtime/webview2/resources.pak", "wad/runtime/webview2/v8_context_snapshot.bin",
        "wad/runtime/webview2/Locales/en-US.pak", "wad/runtime/webview2/Locales/fr.pak",
        "wad/licenses/WebView2-FixedRuntime-LICENSE.html"
    ];
    private static void ValidateWebViewPackage(UpdatePackageMetadata metadata, IEnumerable<string> paths)
    {
        // Retain the verified v2 rollback format. Every v3+ pack must carry its actual local WebView application and,
        // unless it is the light edition, its fixed runtime; never an installer.
        if (!Version.TryParse(metadata.Version, out var applicationVersion) || applicationVersion.Major < 3) return;
        var light = metadata.Edition == "PROD-LIGHT";
        if (metadata.Ui != "webview2" || metadata.WebView2Loader != (light ? EvergreenLoader : BundledLoader)
            || !Version.TryParse(metadata.WebView2Sdk, out var sdk) || sdk.Revision < 0 || sdk.Major <= 0)
            throw new InvalidDataException("Le pack WebView doit déclarer son interface, le SDK et son chargeur : runtime fixe intégré ou runtime Windows installé pour le pack allégé.");
        if (light ? metadata.WebView2Runtime.Length > 0 || metadata.WebView2RuntimeSha256.Length > 0
            : !IsHash(metadata.WebView2RuntimeSha256) || !Version.TryParse(metadata.WebView2Runtime, out var runtime) || runtime.Revision < 0 || runtime.Major <= 0)
            throw new InvalidDataException(light ? "Le pack allégé ne déclare aucun runtime fixe." : "Le pack complet doit déclarer son runtime fixe et l’empreinte du CAB Microsoft.");
        var files = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if ((light ? RequiredLightWebViewFiles : RequiredWebViewFiles).Any(path => !files.Contains(path))) throw new InvalidDataException("Le pack WebView est incomplet : interface locale, runtime fixe ou licences absents.");
        if (light && files.Any(path => path.StartsWith("wad/runtime/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Le pack allégé ne doit contenir aucun runtime intégré.");
    }
    // The edition whose packs may replace this installation: PROD-LIGHT when it runs on the installed Windows runtime.
    public static string InstalledEdition(string installRoot)
    {
        var path = Path.Combine(installRoot, "wad", "version.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32768) return "PROD";
            return JsonSerializer.Deserialize<UpdatePackageMetadata>(File.ReadAllBytes(path), JsonOptions)?.WebView2Loader == EvergreenLoader ? "PROD-LIGHT" : "PROD";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return "PROD"; }
    }
    // Only light packs are published since 3.14.0. A complete installation may move to one once this workstation has
    // an engine outside the pack: the Socle runtime installed as a component, or (set by the WebView host) Evergreen.
    public static Func<bool> RuntimeOutsidePack { get; set; } = () => PackComponents.IsInstalled(PackComponents.WebView2Fixed);

    // The installed pack's own provenance decides which kind of pack may replace it; v2-era installations carry no loader.
    internal static void EnsureSameLoader(string installRoot, UpdatePackageMetadata next)
    {
        var path = Path.Combine(installRoot, "wad", "version.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 32768) return;
        UpdatePackageMetadata? installed;
        try { installed = JsonSerializer.Deserialize<UpdatePackageMetadata>(File.ReadAllBytes(path), JsonOptions); }
        catch (JsonException) { return; }
        // A v2-era rollback pack declares no loader and stays accepted by the dedicated rollback path.
        if (string.IsNullOrEmpty(installed?.WebView2Loader) || string.IsNullOrEmpty(next.WebView2Loader) || installed.WebView2Loader == next.WebView2Loader) return;
        if (next.WebView2Loader == EvergreenLoader && RuntimeOutsidePack()) return;
        throw new InvalidDataException(next.WebView2Loader == EvergreenLoader
            ? "Ce poste utilise le pack complet avec son runtime intégré, que le pack allégé n’emporte pas. "
                + PackComponents.SocleSourceInstructions + ", puis installez-le dans Réglages · Composants. Vous pouvez aussi installer WebView2 Evergreen. Relancez ensuite la mise à jour."
            : "Ce poste utilise le pack allégé ; le pack complet ne le remplace pas. Installez le pack allégé de la nouvelle version.");
    }
    public static string ValidRelativePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name.Contains('\\') || name.StartsWith('/') || name.Any(ch => char.IsControl(ch) || "<>:\"|?*".Contains(ch))) throw new InvalidDataException("Chemin d’archive non portable ou dangereux.");
        foreach (var part in name.Split('/'))
        {
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³')) throw new InvalidDataException("Le chemin d’archive contient une traversée ou un nom Windows réservé.");
        }
        return name;
    }
    internal static string Combine(string root, string relative)
    {
        var result = Path.GetFullPath(Path.Combine(root, ValidRelativePath(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(result, root)) throw new InvalidDataException("Un chemin sort du dossier de préparation."); return result;
    }
    internal static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Un dossier de mise à jour ou d’installation traverse un lien ou une jonction.");
    }
    internal static void ValidateInstallRoot(string root)
    {
        EnsureNoLinks(root);
        if (!File.Exists(Path.Combine(root, "GLPI-Pilot.exe")) || !Directory.Exists(Path.Combine(root, "wad")) || Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar) == root.TrimEnd(Path.DirectorySeparatorChar)) throw new InvalidDataException("L’assistant nécessite le dossier d’un pack GLPI Pilot installé, avec GLPI-Pilot.exe et wad.");
    }
    internal static Dictionary<string, string> Inventory(string root, CancellationToken ct = default)
    {
        EnsureNoLinks(root); var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        void FileEntry(string path)
        {
            ct.ThrowIfCancellationRequested(); EnsureNoLinks(path); var file = new FileInfo(path); total += file.Length;
            if (file.Length > MaximumFileBytes || total > MaximumPayloadBytes || result.Count >= MaximumFiles) throw new InvalidDataException("L’inventaire installé dépasse les limites de sauvegarde.");
            result.Add(ValidRelativePath(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')), HashFile(path));
        }
        FileEntry(Path.Combine(root, "GLPI-Pilot.exe"));
        if (File.Exists(Path.Combine(root, AdminExecutable))) FileEntry(Path.Combine(root, AdminExecutable));
        var pending = new Stack<string>(); pending.Push(Path.Combine(root, "wad")); var directories = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var directory = pending.Pop(); EnsureNoLinks(directory);
            if (++directories > 5000) throw new InvalidDataException("Trop de dossiers dans le pack installé.");
            foreach (var path in Directory.EnumerateFiles(directory)) FileEntry(path);
            foreach (var child in Directory.EnumerateDirectories(directory)) { EnsureNoLinks(child); pending.Push(child); }
        }
        return result;
    }
    internal static void VerifyInventory(string root, Dictionary<string, string> expected, CancellationToken ct = default)
    {
        var actual = Inventory(root, ct); if (actual.Count != expected.Count || actual.Any(item => !expected.TryGetValue(item.Key, out var hash) || !hash.Equals(item.Value, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Le contenu du pack a changé après sa vérification. Recommencez la préparation.");
    }
    private static string ReadEntry(ZipArchiveEntry entry, int maximum, CancellationToken ct)
    {
        if (entry.Length > maximum) throw new InvalidDataException("Un document de provenance est trop volumineux.");
        using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); if (output.Length + count > maximum) throw new InvalidDataException("Document de provenance trop volumineux."); output.Write(buffer, 0, count); }
        return new UTF8Encoding(false, true).GetString(output.ToArray()).TrimStart('\uFEFF');
    }
    private static string HashStream(Stream input, long maximum, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0; int count;
        while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); if ((total += count) > maximum) throw new InvalidDataException("Le fichier dépasse la taille autorisée."); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    internal static void WriteJson<T>(string path, T value)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false)); File.Move(temp, full, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void DeleteOwnDirectory(string path)
    { try { if (Directory.Exists(path)) { EnsureNoLinks(path); Directory.Delete(path, true); } } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
