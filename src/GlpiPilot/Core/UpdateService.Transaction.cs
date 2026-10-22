using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public static partial class UpdateService
{
    public static PreparedUpdate ReadPrepared(string path)
    {
        EnsureNoLinks(path); using var stream = File.OpenRead(path); if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Le journal de mise à jour est trop volumineux.");
        var value = JsonSerializer.Deserialize<PreparedUpdate>(stream, JsonOptions) ?? throw new InvalidDataException("Journal de mise à jour vide."); ValidatePrepared(value);
        if (!Path.GetFullPath(path).Equals(Path.GetFullPath(value.TransactionPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Le journal ne se trouve pas dans son dossier de transaction.");
        return value;
    }
    private static void ValidatePrepared(PreparedUpdate value)
    {
        if (value.Format != 1 || !Guid.TryParseExact(value.Id, "N", out _) || !IsHash(value.HealthNonce) || !IsHash(value.ArchiveSha256) || value.Files == null || value.PreviousFiles == null
            || value.Files.Count is < 2 or > MaximumFiles || value.PreviousFiles.Count is < 2 or > MaximumFiles || !value.Files.ContainsKey("GLPI-Pilot.exe") || !value.PreviousFiles.ContainsKey("GLPI-Pilot.exe")
            || value.State is not ("prepared" or "installing" or "installed" or "rolled-back" or "recovery-needed") || value.Package == null || value.Signature == null)
            throw new InvalidDataException("Transaction de mise à jour invalide.");
        EnsureNoLinks(value.WorkRoot); EnsureNoLinks(value.InstallRoot);
        if (!Path.IsPathFullyQualified(value.WorkRoot) || !Path.IsPathFullyQualified(value.InstallRoot) || Within(value.WorkRoot, value.InstallRoot) || Within(value.InstallRoot, value.WorkRoot) || value.WorkRoot.Equals(value.InstallRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Les dossiers de mise à jour doivent être absolus et séparés.");
        foreach (var (name, hash) in value.Files.Concat(value.PreviousFiles))
            if (!IsHash(hash) || ValidRelativePath(name) != name || name != "GLPI-Pilot.exe" && !name.StartsWith("wad/", StringComparison.Ordinal)) throw new InvalidDataException("Inventaire de transaction invalide.");
        if (value.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Files.Count || value.PreviousFiles.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.PreviousFiles.Count
            || new[] { "wad/version.json", "wad/SHA256SUMS.txt", "wad/.playwright/node/win32_x64/node.exe", "wad/.playwright/package/cli.js" }.Any(name => !value.Files.ContainsKey(name)))
            throw new InvalidDataException("L’inventaire de transaction est incomplet ou contient des chemins ambigus pour Windows.");
        ValidateMetadata(value.Package, value.Package.SourceCommit, value.Files["GLPI-Pilot.exe"]);
    }

    /// <summary>Runs only when the application is stopped. Health verification is mandatory; every failure restores the captured pack.</summary>
    public static UpdateOutcome ApplyPrepared(PreparedUpdate value, Func<PreparedUpdate, bool> verifyStartup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verifyStartup); ValidatePrepared(value);
        if (value.State != "prepared") throw new InvalidDataException("Cette transaction a déjà été appliquée ou interrompue.");
        ValidateInstallRoot(value.InstallRoot); EnsureInstallNotRunning(value.InstallRoot); VerifyInventory(value.InstallRoot, value.PreviousFiles, ct); VerifyInventory(value.StageRoot, value.Files, ct);
        if (Directory.Exists(value.BackupRoot)) throw new InvalidDataException("Le dossier de sauvegarde de cette transaction existe déjà.");
        var lockPath = Path.Combine(value.InstallRoot, ".glpi-update.lock"); using var updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var oldExeMoved = false; var oldWadMoved = false; var newExeMoved = false; var newWadMoved = false; var healthy = false;
        // These two directories share the installation volume; the durable backup may be on another drive.
        var incoming = Path.Combine(value.InstallRoot, ".glpi-incoming-" + value.Id); var previous = Path.Combine(value.InstallRoot, ".glpi-previous-" + value.Id);
        if (Directory.Exists(incoming) || Directory.Exists(previous)) throw new InvalidDataException("Une préparation de remplacement existe déjà dans le dossier d’installation.");
        try
        {
            ct.ThrowIfCancellationRequested(); value.State = "installing"; WriteJson(value.TransactionPath, value);
            CopyInventory(value.InstallRoot, value.BackupRoot, value.PreviousFiles, ct); VerifyInventory(value.BackupRoot, value.PreviousFiles, ct);
            CopyInventory(value.StageRoot, incoming, value.Files, ct); VerifyInventory(incoming, value.Files, ct); EnsureInstallNotRunning(value.InstallRoot); Directory.CreateDirectory(previous);
            File.Move(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe"), Path.Combine(previous, "GLPI-Pilot.exe")); oldExeMoved = true;
            Directory.Move(Path.Combine(value.InstallRoot, "wad"), Path.Combine(previous, "wad")); oldWadMoved = true;
            File.Move(Path.Combine(incoming, "GLPI-Pilot.exe"), Path.Combine(value.InstallRoot, "GLPI-Pilot.exe")); newExeMoved = true;
            Directory.Move(Path.Combine(incoming, "wad"), Path.Combine(value.InstallRoot, "wad")); newWadMoved = true;
            VerifyInventory(value.InstallRoot, value.Files, ct); ct.ThrowIfCancellationRequested();
            if (!verifyStartup(value)) throw new InvalidDataException("La nouvelle version n’a pas confirmé l’ouverture de sa fenêtre dans le délai prévu.");
            healthy = true; value.State = "installed"; WriteJson(value.TransactionPath, value); DeleteOwnDirectory(previous);
            return RecordOutcome(value, "installed", "Version installée et ouverture confirmée. La version précédente reste disponible pour un retour arrière.");
        }
        catch (Exception error)
        {
            if (healthy) return new UpdateOutcome("installed", "La nouvelle version a démarré ; l’écriture du bilan a échoué. La sauvegarde est conservée dans " + value.BackupRoot + ". " + SafeUpdateError(error), value.Package.Version, value.BackupRoot, DateTimeOffset.UtcNow);
            try
            {
                // The health probe normally stops its own child before returning.
                // If that stop failed, retain the verified backup instead of moving
                // binaries underneath an application which is still running.
                if (oldExeMoved || oldWadMoved || newExeMoved || newWadMoved) EnsureInstallNotRunning(value.InstallRoot);
                var failed = Path.Combine(value.InstallRoot, ".glpi-failed-" + value.Id); Directory.CreateDirectory(failed);
                if (newExeMoved && File.Exists(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe"))) File.Move(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe"), Path.Combine(failed, "GLPI-Pilot.exe"));
                if (newWadMoved && Directory.Exists(Path.Combine(value.InstallRoot, "wad"))) Directory.Move(Path.Combine(value.InstallRoot, "wad"), Path.Combine(failed, "wad"));
                if (oldExeMoved) File.Move(Path.Combine(previous, "GLPI-Pilot.exe"), Path.Combine(value.InstallRoot, "GLPI-Pilot.exe"));
                if (oldWadMoved) Directory.Move(Path.Combine(previous, "wad"), Path.Combine(value.InstallRoot, "wad"));
                VerifyInventory(value.InstallRoot, value.PreviousFiles); value.State = "rolled-back"; WriteJson(value.TransactionPath, value);
                DeleteOwnDirectory(failed); DeleteOwnDirectory(previous);
                return RecordOutcome(value, "rolled-back", "Installation interrompue ; le pack précédent a été restauré et vérifié. Motif : " + SafeUpdateError(error));
            }
            catch (Exception restoreError)
            {
                value.State = "recovery-needed"; WriteJson(value.TransactionPath, value);
                return RecordOutcome(value, "recovery-needed", "La restauration n’a pas pu se terminer. Les fichiers précédents sont conservés dans " + value.BackupRoot + ". Fermez GLPI Pilot avant de les restaurer. " + SafeUpdateError(restoreError));
            }
        }
        finally { DeleteOwnDirectory(incoming); }
    }
    private static void CopyInventory(string source, string target, Dictionary<string, string> files, CancellationToken ct)
    {
        if (Directory.Exists(target)) throw new InvalidDataException("Le dossier de copie doit être nouveau.");
        EnsureNoLinks(source); EnsureNoLinks(target); Directory.CreateDirectory(target);
        foreach (var name in files.Keys) { ct.ThrowIfCancellationRequested(); var destination = Combine(target, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(Combine(source, name), destination, false); }
    }
    private static void EnsureInstallNotRunning(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var name in new[] { "GLPI-Pilot", "node" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue; var path = process.MainModule?.FileName;
                        if (path != null && Within(path, root)) throw new IOException("Un processus utilise encore ce pack ; fermez cette instance et son navigateur piloté avant la mise à jour.");
                    }
                    catch (System.ComponentModel.Win32Exception) when (name == "node") { }
                    catch (System.ComponentModel.Win32Exception error) { throw new IOException("L’arrêt des instances GLPI Pilot ne peut pas être vérifié.", error); }
                    catch (InvalidOperationException) { /* Process exited during inspection. */ }
                }
    }
    private static string SafeUpdateError(Exception error) => (error.GetBaseException().Message ?? error.GetType().Name).Split('\n')[0][..Math.Min((error.GetBaseException().Message ?? error.GetType().Name).Split('\n')[0].Length, 1000)];
    private static UpdateOutcome RecordOutcome(PreparedUpdate value, string state, string detail)
    {
        var outcome = new UpdateOutcome(state, detail, value.Package.Version, value.BackupRoot, DateTimeOffset.UtcNow);
        WriteJson(Path.Combine(value.WorkRoot, "outcome.json"), outcome); WriteJson(Path.Combine(Path.GetDirectoryName(value.WorkRoot)!, "last-result.json"), outcome); return outcome;
    }
    public static UpdateOutcome? ReadLastOutcome(string updatesRoot)
    {
        var path = Path.Combine(updatesRoot, "last-result.json"); if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path); if (input.Length > 16384) throw new InvalidDataException("Bilan de mise à jour trop volumineux."); return JsonSerializer.Deserialize<UpdateOutcome>(input, JsonOptions);
    }
    public static PreparedUpdate? FindRollback(string updatesRoot, string installRoot)
    {
        if (!Directory.Exists(updatesRoot)) return null;
        foreach (var path in Directory.EnumerateDirectories(updatesRoot).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(100))
        {
            var file = Path.Combine(path, "transaction.json"); if (!File.Exists(file)) continue;
            try { var value = ReadPrepared(file); if (value.State == "installed" && value.InstallRoot.Equals(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && Directory.Exists(value.BackupRoot)) return value; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return null;
    }
    public static PreparedUpdate PrepareRollback(PreparedUpdate installed, string updatesRoot, CancellationToken ct = default)
    {
        ValidatePrepared(installed); if (installed.State != "installed") throw new InvalidDataException("Cette transaction ne possède pas de version antérieure restaurable.");
        VerifyInventory(installed.BackupRoot, installed.PreviousFiles, ct); ValidateInstallRoot(installed.InstallRoot);
        var metadataPath = Path.Combine(installed.BackupRoot, "wad", "version.json");
        var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(File.ReadAllText(metadataPath), JsonOptions) ?? throw new InvalidDataException("Provenance de la version précédente absente.");
        ValidateMetadata(metadata, metadata.SourceCommit, installed.PreviousFiles["GLPI-Pilot.exe"]);
        var value = new PreparedUpdate { InstallRoot = installed.InstallRoot, WorkRoot = Path.Combine(Path.GetFullPath(updatesRoot), Guid.NewGuid().ToString("N")), ArchiveSha256 = installed.PreviousFiles["GLPI-Pilot.exe"],
            Package = metadata, Files = new(installed.PreviousFiles, StringComparer.OrdinalIgnoreCase), PreviousFiles = Inventory(installed.InstallRoot, ct), Signature = new("SavedBackup", "Sauvegarde locale vérifiée", ""), IsRollback = true };
        ValidatePrepared(value);
        try
        {
            foreach (var name in value.Files.Keys) { ct.ThrowIfCancellationRequested(); var target = Combine(value.StageRoot, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Combine(installed.BackupRoot, name), target, false); }
            VerifyInventory(value.StageRoot, value.Files, ct); WriteJson(value.TransactionPath, value); return value;
        }
        catch { DeleteOwnDirectory(value.WorkRoot); throw; }
    }

    public static void LaunchUpdater(PreparedUpdate prepared)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Le remplacement des binaires est disponible sous Windows.");
        var value = ReadPrepared(prepared.TransactionPath); ValidateInstallRoot(value.InstallRoot); VerifyInventory(value.StageRoot, value.Files); VerifyInventory(value.InstallRoot, value.PreviousFiles);
        var root = Path.Combine(AppPaths.DataRoot, "Updates"); if (!Within(value.WorkRoot, root)) throw new InvalidDataException("La transaction ne se trouve pas dans le dossier local des mises à jour.");
        var ownExe = Environment.ProcessPath ?? throw new InvalidDataException("Chemin de l’exécutable courant inconnu.");
        if (!HashFile(ownExe).Equals(value.PreviousFiles["GLPI-Pilot.exe"], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("La version en cours n’est pas celle du dossier à remplacer.");
        var helper = Path.Combine(value.WorkRoot, "helper", "GLPI-Pilot.exe"); Directory.CreateDirectory(Path.GetDirectoryName(helper)!); File.Copy(ownExe, helper, false);
        using var current = Process.GetCurrentProcess(); value.ParentProcessId = current.Id; value.ParentStartedUtcTicks = current.StartTime.ToUniversalTime().Ticks; WriteJson(value.TransactionPath, value);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(value.TransactionPath);
        using var launched = Process.Start(start) ?? throw new IOException("Le processus de mise à jour n’a pas démarré.");
    }
    public static bool TryRunMaintenance(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length == 0 || arguments[0] != "--apply-update")
        {
            // A normal second launch must not race the replacement; the nonce-bearing health launch is expected.
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(AppContext.BaseDirectory, ".glpi-update.lock");
                if (File.Exists(path)) try { using var stale = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
                    catch (IOException) { if (!IsAuthorizedHealthLaunch(arguments)) { exitCode = 1; return true; } }
            }
            return false;
        }
        PreparedUpdate? value = null; var parentExited = false;
        try
        {
            if (!OperatingSystem.IsWindows() || arguments.Length != 2) throw new InvalidDataException("Commande de maintenance invalide.");
            var updatesRoot = Path.Combine(AppPaths.DataRoot, "Updates"); if (!Within(arguments[1], updatesRoot)) throw new InvalidDataException("Journal de maintenance extérieur au dossier local.");
            value = ReadPrepared(arguments[1]); var ownExe = Environment.ProcessPath ?? "";
            if (!Path.GetFullPath(ownExe).Equals(Path.Combine(value.WorkRoot, "helper", "GLPI-Pilot.exe"), StringComparison.OrdinalIgnoreCase) || !HashFile(ownExe).Equals(value.PreviousFiles["GLPI-Pilot.exe"], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("L’auxiliaire de mise à jour ne correspond pas à la version précédente.");
            try
            {
                using var parent = Process.GetProcessById(value.ParentProcessId);
                if (parent.StartTime.ToUniversalTime().Ticks != value.ParentStartedUtcTicks || !parent.WaitForExit(60000)) throw new InvalidDataException("L’application précédente est encore ouverte ; la mise à jour est abandonnée.");
            }
            catch (ArgumentException) { /* Parent already exited. */ }
            parentExited = true;
            var outcome = ApplyPrepared(value, VerifyNewWindow);
            if (outcome.State == "rolled-back") { using var restarted = Process.Start(new ProcessStartInfo(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe")) { UseShellExecute = false, WorkingDirectory = value.InstallRoot }); }
            exitCode = outcome.State == "installed" ? 0 : 1;
        }
        catch (Exception error)
        {
            exitCode = 1;
            if (value != null)
                try
                {
                    RecordOutcome(value, "not-installed", "Aucun remplacement validé : " + SafeUpdateError(error));
                    if (parentExited) { VerifyInventory(value.InstallRoot, value.PreviousFiles); using var restored = Process.Start(new ProcessStartInfo(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe")) { UseShellExecute = false, WorkingDirectory = value.InstallRoot }); }
                }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
        return true;
    }
    private static bool IsAuthorizedHealthLaunch(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != "--update-health") return false;
        try
        {
            if (!Within(arguments[1], Path.Combine(AppPaths.DataRoot, "Updates"))) return false;
            var value = ReadPrepared(arguments[1]);
            return value.State == "installing" && arguments[2] == value.HealthNonce && value.Package.Version == AppPaths.ApplicationVersion
                && string.Equals(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe"), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }
    private static bool VerifyNewWindow(PreparedUpdate value)
    {
        var start = new ProcessStartInfo(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe")) { UseShellExecute = false, WorkingDirectory = value.InstallRoot };
        start.ArgumentList.Add("--update-health"); start.ArgumentList.Add(value.TransactionPath); start.ArgumentList.Add(value.HealthNonce);
        using var process = Process.Start(start) ?? throw new IOException("La nouvelle version ne démarre pas."); var deadline = Stopwatch.StartNew(); var success = false;
        try
        {
            while (deadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                if (process.HasExited) return false;
                var health = Path.Combine(value.WorkRoot, "health.json");
                if (File.Exists(health))
                {
                    using var input = File.OpenRead(health); if (input.Length > 8192) throw new InvalidDataException("Confirmation de démarrage invalide.");
                    var receipt = JsonSerializer.Deserialize<UpdateHealth>(input, JsonOptions);
                    if (receipt?.Nonce == value.HealthNonce && receipt.ProcessId == process.Id && receipt.Version == value.Package.Version) { success = true; return true; }
                }
                Thread.Sleep(200);
            }
            return false;
        }
        finally
        {
            if (!success && !process.HasExited) { process.Kill(entireProcessTree: true); if (!process.WaitForExit(5000)) throw new IOException("La nouvelle version ne s’est pas fermée ; sauvegarde conservée pour restauration."); }
        }
    }
    private sealed record UpdateHealth(string Nonce, int ProcessId, string Version);
    public static void AcknowledgeStartup()
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray(); if (arguments.Length != 3 || arguments[0] != "--update-health") return;
        try
        {
            if (!Within(arguments[1], Path.Combine(AppPaths.DataRoot, "Updates"))) return;
            var value = ReadPrepared(arguments[1]); if (value.State != "installing" || arguments[2] != value.HealthNonce || value.Package.Version != AppPaths.ApplicationVersion || !string.Equals(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe"), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            WriteJson(Path.Combine(value.WorkRoot, "health.json"), new UpdateHealth(value.HealthNonce, Environment.ProcessId, AppPaths.ApplicationVersion));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }
}
