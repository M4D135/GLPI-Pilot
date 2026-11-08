using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record DeploymentSeedOffer(bool Available, string Sha256 = "", string Version = "", string Reason = "");
public sealed record DeploymentSeedState(string FileName, string SeedPath, string SeedStatus, string SeedMessage,
    DateTimeOffset? SeedAppliedAt, bool Simulation, DeploymentSeedOffer Offer, string DefaultPath = "", string DefaultSha256 = "");
public sealed record DeploymentDefaultSaved(string Path, string Sha256, string BackupPath, DeploymentSeedState State);

public static class DeploymentConfigurationInitializer
{
    public static string DefaultPath(string applicationDirectory) => Path.Combine(Path.GetFullPath(applicationDirectory), "wad", DeploymentConfigurationService.FileName);
    // An invalid or inaccessible preferred file is an error, never a reason to
    // silently apply an older root-level configuration.
    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    public static string ResolveSeedPath(string applicationDirectory)
    {
        var preferred = DefaultPath(applicationDirectory); var wad = Path.GetDirectoryName(preferred)!;
        UpdateService.EnsureNoLinks(preferred);
        if (EntryExists(wad) && (File.GetAttributes(wad) & FileAttributes.Directory) == 0)
            throw new InvalidDataException("L’emplacement wad doit être un dossier pour recevoir la configuration par défaut.");
        if (EntryExists(preferred)) return preferred;
        var legacy = Path.Combine(Path.GetFullPath(applicationDirectory), DeploymentConfigurationService.FileName);
        UpdateService.EnsureNoLinks(legacy);
        return EntryExists(legacy) ? legacy : preferred;
    }
    private sealed record Choice(string Sha256, string Version, string Action, DateTimeOffset At);
    private sealed class Receipt
    {
        public int Version { get; set; } = 1;
        public string Status { get; set; } = "absent";
        public string Message { get; set; } = "";
        public DateTimeOffset? AppliedAt { get; set; }
        public bool CompleteOptionsApplied { get; set; }
        public List<Choice> Choices { get; set; } = new();
    }
    private static string ReceiptPath(string root) => Path.Combine(root, "deployment-state.json");
    private static Receipt ReadReceipt(string root) => File.Exists(ReceiptPath(root))
        ? JsonSerializer.Deserialize<Receipt>(DeploymentConfigurationService.ReadBytes(ReceiptPath(root)), DeploymentConfigurationService.Json)
            ?? throw new InvalidDataException("Reçu de déploiement absent.") : new();
    private static void SaveReceipt(string root, Receipt receipt) => DeploymentConfigurationService.AtomicWrite(ReceiptPath(root), JsonSerializer.SerializeToUtf8Bytes(receipt, DeploymentConfigurationService.Json));
    internal static byte[] AppliedOptionsReceipt(string root)
    {
        var receipt = ReadReceipt(root); receipt.CompleteOptionsApplied = true;
        return JsonSerializer.SerializeToUtf8Bytes(receipt, DeploymentConfigurationService.Json);
    }
    public static bool PreservesSites(string root)
    {
        try { return ReadReceipt(root).CompleteOptionsApplied; }
        // Do not silently enrich a possibly deployed site catalogue if its local receipt is damaged.
        catch (Exception error) when (DeploymentConfigurationService.StorageError(error)) { return true; }
    }
    public static DeploymentDefaultSaved SaveDefault(string applicationDirectory, string root, string version, AppSettings settings, string? expectedSha256 = null)
    {
        DeploymentConfigurationService.RealOnly();
        var path = DefaultPath(applicationDirectory);
        var staging = Path.Combine(root, "DeploymentDefaults", Guid.NewGuid().ToString("N"));
        var candidate = Path.Combine(staging, "candidate.json"); var backup = ""; byte[]? previous = null; byte[]? written = null; var installed = false;
        try
        {
            var bytes = DeploymentConfigurationService.Serialize(DeploymentConfigurationService.Capture(settings, root));
            DeploymentConfigurationService.AtomicWrite(candidate, bytes);
            var imported = DeploymentConfigurationService.Read(candidate, root);
            UpdateService.EnsureNoLinks(path);
            if (EntryExists(path))
            {
                previous = DeploymentConfigurationService.ReadBytes(path);
                backup = Path.Combine(staging, DeploymentConfigurationService.FileName);
                DeploymentConfigurationService.AtomicWrite(backup, previous);
            }
            if (expectedSha256 is not null && expectedSha256 != (previous is null ? "" : DeploymentConfigurationService.Digest(previous)))
                throw new IOException("La configuration par défaut a changé depuis l’ouverture de cet écran. Rouvrez les réglages avant de confirmer son remplacement.");
            if (previous is null ? File.Exists(path) : !File.Exists(path) || !previous.AsSpan().SequenceEqual(DeploymentConfigurationService.ReadBytes(path)))
                throw new IOException("Le fichier par défaut a changé pendant sa préparation. Relancez l’enregistrement.");
            DeploymentConfigurationService.AtomicWrite(path, bytes); written = bytes; installed = true;
            RememberChoice(root, imported.Sha256, version, false, defaultSaved: true);
            return new(path, imported.Sha256, backup, State(applicationDirectory, root, version));
        }
        catch (Exception error) when (DeploymentConfigurationService.StorageError(error))
        {
            if (installed)
            {
                if (!File.Exists(path) || !DeploymentConfigurationService.ReadBytes(path).AsSpan().SequenceEqual(written))
                    throw new InvalidOperationException("Le fichier par défaut a de nouveau changé après son enregistrement. Cette modification a été conservée ; la sauvegarde précédente reste dans " + staging + ". " + AppPaths.SafeError(error), error);
                if (previous is null) File.Delete(path); else DeploymentConfigurationService.AtomicWrite(path, previous);
            }
            throw new InvalidOperationException("Impossible d’enregistrer la configuration par défaut dans le dossier wad du pack. Vérifiez que ce dossier est accessible en écriture. " + AppPaths.SafeError(error), error);
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }
    public static DeploymentSeedState TryInitialize(string applicationDirectory, string root, string version)
    {
        if (AppPaths.SimulationRoot is not null) return State(applicationDirectory, root, version);
        try
        {
            DeploymentConfigurationService.Recover(root);
            if (File.Exists(ReceiptPath(root))) return State(applicationDirectory, root, version);
            // Startup diagnostics may already have opened Logs; every other
            // existing artifact proves a profile, even if settings.json is absent.
            var existing = Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root)
                .Any(path => !string.Equals(Path.GetFileName(path), "Logs", StringComparison.OrdinalIgnoreCase));
            var seed = ResolveSeedPath(applicationDirectory);
            var receipt = new Receipt { Status = existing ? "existing" : "absent", Message = existing
                ? "Le profil existant est conservé. Une configuration fournie peut être examinée avant tout remplacement."
                : "Aucun fichier de déploiement fourni au premier lancement." };
            if (!existing && EntryExists(seed))
            {
                var imported = DeploymentConfigurationService.Read(seed, root);
                DeploymentConfigurationService.Apply(imported.Configuration, new AppSettings(), root);
                receipt.Status = "applied"; receipt.Message = "La configuration fournie a été appliquée au nouveau profil local."; receipt.AppliedAt = DateTimeOffset.UtcNow; receipt.CompleteOptionsApplied = true;
                receipt.Choices.Add(new(imported.Sha256, version, "applied", receipt.AppliedAt.Value));
            }
            SaveReceipt(root, receipt);
        }
        catch (Exception error) when (DeploymentConfigurationService.StorageError(error))
        {
            var message = "Configuration fournie non appliquée : " + AppPaths.SafeError(error);
            try { SaveReceipt(root, new Receipt { Status = "error", Message = message }); } catch (Exception e) when (DeploymentConfigurationService.StorageError(e)) { }
            AppPaths.AppendLog(message);
        }
        return State(applicationDirectory, root, version);
    }
    public static DeploymentSeedState State(string applicationDirectory, string root, string version)
    {
        var defaultPath = DefaultPath(applicationDirectory); var seed = defaultPath; var defaultHash = "";
        if (AppPaths.SimulationRoot is not null) return new(DeploymentConfigurationService.FileName, "", "simulation", "La démonstration ne diffuse pas sa configuration fictive.", null, true, new(false));
        try
        {
            seed = ResolveSeedPath(applicationDirectory);
            defaultHash = EntryExists(defaultPath) ? DeploymentConfigurationService.Digest(DeploymentConfigurationService.ReadBytes(defaultPath)) : "";
            var receipt = ReadReceipt(root);
            if (receipt.Version != 1 || receipt.Choices is null || receipt.Choices.Count > 100) throw new InvalidDataException("Reçu de déploiement invalide.");
            if (!EntryExists(seed)) return new(DeploymentConfigurationService.FileName, seed, receipt.Status, receipt.Message, receipt.AppliedAt, false, new(false), defaultPath, defaultHash);
            var imported = DeploymentConfigurationService.Read(seed, root);
            var decided = receipt.Choices.Any(choice => choice.Sha256 == imported.Sha256 && choice.Version == version);
            return new(DeploymentConfigurationService.FileName, seed, receipt.Status, receipt.Message, receipt.AppliedAt, false,
                new(!decided, imported.Sha256, version, "Une configuration accompagne cette version. Examinez les différences puis remplacez vos options ou gardez le profil actuel."), defaultPath, defaultHash);
        }
        catch (Exception error) when (DeploymentConfigurationService.StorageError(error))
        { return new(DeploymentConfigurationService.FileName, seed, "error", AppPaths.SafeError(error), null, false, new(false), defaultPath, defaultHash); }
    }
    public static void RememberChoice(string root, string hash, string version, bool applied, bool defaultSaved = false)
    {
        DeploymentConfigurationService.RealOnly();
        if (!UpdateService.IsHash(hash) || !Version.TryParse(version, out _)) throw new InvalidDataException("Identification du déploiement invalide.");
        var receipt = ReadReceipt(root); var at = DateTimeOffset.UtcNow;
        receipt.Choices.RemoveAll(choice => choice.Sha256 == hash && choice.Version == version); receipt.Choices.Add(new(hash, version, applied ? "applied" : "kept", at));
        receipt.Choices = receipt.Choices.TakeLast(100).ToList();
        if (applied) { receipt.Status = "applied"; receipt.AppliedAt = at; receipt.Message = "Configuration fournie appliquée après examen et sauvegarde."; }
        else if (defaultSaved) { receipt.Status = "default-saved"; receipt.Message = "Configuration par défaut enregistrée dans le dossier wad du pack. Vos options locales restent inchangées."; }
        SaveReceipt(root, receipt);
    }
}
