using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GlpiPilot.Core;

var checks = 0;
void Check(string name, bool condition) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Reject(string name, Action action)
{
    try { action(); } catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException) { Check(name, true); return; }
    throw new Exception(name + " should be rejected");
}
var root = Path.Combine(Path.GetTempPath(), "glpi-startup-resilience-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var fresh = StartupBackupService.Prepare(root, "2.0.0");
    Check("Clean first startup has no archive", fresh.VersionChanged && fresh.Files == 0 && fresh.Path == "");
    Check("Preparing startup never marks the version ready", !File.Exists(Path.Combine(root, "last-ready-version.txt")));
    StartupBackupService.MarkReady(root, "1.9.0");
    var settings = Encoding.UTF8.GetBytes("{\"Theme\":\"dark\",\"PortalUrl\":\"https://example.invalid/front/ticket.php\"}");
    var notes = Encoding.UTF8.GetBytes("{\"Notes\":\"Préserver mon travail personnel\"}");
    File.WriteAllBytes(Path.Combine(root, "settings.json"), settings);
    File.WriteAllBytes(Path.Combine(root, "workplace.json"), notes);
    Directory.CreateDirectory(Path.Combine(root, "Browser")); File.WriteAllText(Path.Combine(root, "Browser", "session.json"), "DO-NOT-COPY");
    File.WriteAllText(Path.Combine(root, "unknown.json"), "DO-NOT-COPY");
    var backup = StartupBackupService.Prepare(root, "2.0.0");
    Check("New version backs up recognized preferences", backup.PreviousVersion == "1.9.0" && backup.Files == 2 && File.Exists(backup.Path));
    Check("Preparing new version preserves exact source bytes", File.ReadAllBytes(Path.Combine(root, "settings.json")).SequenceEqual(settings) && File.ReadAllBytes(Path.Combine(root, "workplace.json")).SequenceEqual(notes));
    Check("Ready marker not advanced before first render", File.ReadAllText(Path.Combine(root, "last-ready-version.txt")) == "1.9.0");
    using (var archive = ZipFile.OpenRead(backup.Path))
    {
        Check("Archive allowlist excludes browser sessions and unknown files", archive.Entries.Select(entry => entry.FullName).Order().SequenceEqual(new[] { "manifest.json", "settings.json", "workplace.json" }));
        using var saved = archive.GetEntry("workplace.json")!.Open(); using var memory = new MemoryStream(); saved.CopyTo(memory);
        Check("Archive preserves personal notes exactly", memory.ToArray().SequenceEqual(notes));
        using var manifest = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        Check("Manifest records per-file integrity digest", manifest.ReadToEnd().Contains(Convert.ToHexString(SHA256.HashData(notes))));
    }
    var repeat = StartupBackupService.Prepare(root, "2.0.0");
    Check("Repeated failed launch reuses verified same-state backup", repeat.Path == backup.Path && Directory.GetFiles(Path.Combine(root, "Backups"), "*.zip").Length == 1);
    File.WriteAllBytes(Path.Combine(root, "settings.json"), Encoding.UTF8.GetBytes("changed"));
    var changed = StartupBackupService.Prepare(root, "2.0.0");
    Check("A changed preference gets a separate snapshot", changed.Path != backup.Path && File.Exists(backup.Path));
    File.WriteAllText(changed.Path, "broken archive");
    Reject("Corrupted existing backup is not trusted or overwritten", () => StartupBackupService.Prepare(root, "2.0.0"));
    Check("Backup failure preserves source preference", File.ReadAllText(Path.Combine(root, "settings.json")) == "changed");
    StartupBackupService.MarkReady(root, "2.0.0");
    Check("Same successful version skips backup work", !StartupBackupService.Prepare(root, "2.0.0").VersionChanged);
    Reject("Reject path characters in version marker", () => StartupBackupService.Prepare(root, "../next"));
    Check("Invalid version did not change ready marker", File.ReadAllText(Path.Combine(root, "last-ready-version.txt")) == "2.0.0");
    using (var huge = File.Create(Path.Combine(root, "settings.json"))) huge.SetLength(StartupBackupService.MaximumFileBytes + 1L);
    Reject("Oversized local preference cannot trigger unbounded backup", () => StartupBackupService.Prepare(root, "2.1.0"));
    File.WriteAllText(Path.Combine(root, "settings.json"), "small");
    for (var index = 0; index < StartupBackupService.MaximumBackups; index++) File.WriteAllText(Path.Combine(root, "Backups", "before-synthetic-" + index + ".zip"), "synthetic");
    Reject("Backup budget stops without deleting earlier snapshots", () => StartupBackupService.Prepare(root, "2.1.0"));
    Check("Budget overflow preserved earlier backup", File.Exists(backup.Path));
    Check("Permission failure advice identifies local write locations", StartupDiagnostics.RecoveryAdvice(new UnauthorizedAccessException()).Contains("%LOCALAPPDATA%"));
    Check("Missing dependency advice requests complete autonomous pack", StartupDiagnostics.RecoveryAdvice(new FileNotFoundException()).Contains("runtime"));
    Check("Advice follows nested loader failure", StartupDiagnostics.RecoveryAdvice(new Exception("outer", new BadImageFormatException())).Contains("x64"));
    Check("Memory failure advice is distinct", StartupDiagnostics.RecoveryAdvice(new OutOfMemoryException()).Contains("mémoire"));
    Check("IO failure advice does not recommend deleting settings", StartupDiagnostics.RecoveryAdvice(new IOException()).Contains("Conservez vos préférences"));
    Console.WriteLine($"{checks} startup resilience checks passed.");
}
finally { Directory.Delete(root, true); }
