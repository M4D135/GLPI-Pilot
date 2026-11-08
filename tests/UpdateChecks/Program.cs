using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Win32.SafeHandles;

var root = Path.Combine(Path.GetTempPath(), "GLPI-UpdateChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
var tests = new List<(string Name, Action Run)>();
void Test(string name, Action action) => tests.Add((name, action));
void Check(bool value, string reason = "Assertion failed") { if (!value) throw new InvalidOperationException(reason); }
void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
Dictionary<string, byte[]> Payload(string version = "2.0.0")
{
    var executable = Encoding.UTF8.GetBytes("MZ fixture executable " + version);
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["GLPI-Pilot.exe"] = executable,
        ["wad/.playwright/node/win32_x64/node.exe"] = Encoding.UTF8.GetBytes("driver fixture"),
        ["wad/.playwright/package/cli.js"] = Encoding.UTF8.GetBytes("driver protocol fixture"),
        ["wad/docs/help.txt"] = Encoding.UTF8.GetBytes("Help " + version),
        ["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(new UpdatePackageMetadata { Application = "GLPI Pilot", Version = version, Edition = "PROD", Target = "win-x64", SelfContained = true, SourceCommit = Commit, ExecutableSha256 = Hash(executable) })
    };
    if (Version.Parse(version).Major >= 3)
    {
        var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(files["wad/version.json"])!;
        metadata.Ui = "webview2"; metadata.WebView2Sdk = "1.0.4258.31"; metadata.WebView2Runtime = "154.0.4258.62";
        metadata.WebView2RuntimeSha256 = new string('e', 64); metadata.WebView2Loader = "bundled-native-x64";
        files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata);
        foreach (var name in UpdateService.RequiredWebViewFiles) files[name] = Encoding.UTF8.GetBytes("WebView fixture " + name);
    }
    Seal(files); return files;
}
// The light edition: the complete payload without its bundled runtime, declaring the installed Windows runtime.
Dictionary<string, byte[]> LightPayload(string version = "3.0.0")
{
    var files = Payload(version); foreach (var name in files.Keys.Where(name => name.StartsWith("wad/runtime/", StringComparison.Ordinal)).ToArray()) files.Remove(name);
    var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(files["wad/version.json"])!;
    metadata.Edition = "PROD-LIGHT"; metadata.WebView2Runtime = ""; metadata.WebView2RuntimeSha256 = ""; metadata.WebView2Loader = UpdateService.EvergreenLoader;
    files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata); Seal(files); return files;
}
string InstallWith(Dictionary<string, byte[]> files)
{
    var path = Install(); foreach (var (name, content) in files) { var file = Path.Combine(path, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, content); }
    return path;
}
void Seal(Dictionary<string, byte[]> files) { files.Remove("wad/SHA256SUMS.txt"); files["wad/SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join('\n', files.Select(pair => Hash(pair.Value) + "  " + pair.Key)) + "\n"); }
string NewDirectory(string name) { var path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
string Install()
{
    var path = NewDirectory("install"); foreach (var (name, content) in Payload("1.0.0")) { var file = Path.Combine(path, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, content); }
    File.WriteAllText(Path.Combine(path, "personal-export.csv"), "User-owned root file"); return path;
}
string Zip(Dictionary<string, byte[]>? files = null, Action<ZipArchive>? append = null)
{
    var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip"); using var stream = File.Create(path); using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    foreach (var (name, content) in files ?? Payload()) { var entry = archive.CreateEntry(name, CompressionLevel.Optimal); using var output = entry.Open(); output.Write(content); }
    append?.Invoke(archive); return path;
}
PreparedUpdate Prepare(string? zip = null, string? installation = null, string? expectedHash = null, string commit = Commit, CancellationToken ct = default)
{
    zip ??= Zip(); installation ??= Install(); return UpdateService.PrepareArchive(zip, expectedHash ?? UpdateService.HashFile(zip), commit, installation, NewDirectory("updates"), "1.0.0", false, ct);
}

Test("Complete package stages separately and preserves running binaries and unrelated root files", () =>
{
    var install = Install(); var old = UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")); var staged = Prepare(installation: install);
    Check(staged.Package.Version == "2.0.0" && staged.Package.SourceCommit == Commit && staged.Files.Count == 6 && Directory.Exists(staged.StageRoot));
    Check(UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")) == old && File.ReadAllText(Path.Combine(install, "personal-export.csv")) == "User-owned root file");
    Check(UpdateService.ReadPrepared(staged.TransactionPath).Files.Count == staged.Files.Count);
});
Dictionary<string, byte[]> WithAdmin(string version)
{
    var files = Payload(version);
    // These fixtures exercise the real public verifier, including WinTrust.
    // Use our unsigned managed PE with a harmless overlay, never text masquerading
    // as an EXE. No fixture executable is launched by the transaction callbacks.
    var pe = File.ReadAllBytes(typeof(WindowsDirectoryLock).Assembly.Location);
    files["GLPI-Pilot.exe"] = pe.Concat(Encoding.UTF8.GetBytes("Standard fixture " + version)).ToArray();
    files[UpdateService.AdminExecutable] = pe.Concat(Encoding.UTF8.GetBytes("Admin fixture " + version)).ToArray();
    var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(files["wad/version.json"])!;
    metadata.ExecutableSha256 = Hash(files["GLPI-Pilot.exe"]); metadata.AdminExecutableSha256 = Hash(files[UpdateService.AdminExecutable]); files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata); Seal(files); return files;
}
Test("A standard installation never receives a removed Admin launcher during update or rollback", () =>
{
    var installed = Install(); var zip = Zip(WithAdmin("2.0.0"));
    var prepared = UpdateService.PrepareAsync(zip, UpdateService.HashFile(zip), Commit, installed, NewDirectory("updates"), "1.0.0", new()).GetAwaiter().GetResult();
    Check(!prepared.Files.ContainsKey(UpdateService.AdminExecutable) && !File.Exists(Path.Combine(prepared.StageRoot, UpdateService.AdminExecutable)));
    Check(UpdateService.ApplyPrepared(prepared, _ => true).State == "installed" && !File.Exists(Path.Combine(installed, UpdateService.AdminExecutable)));
    var rollback = UpdateService.PrepareRollback(prepared, NewDirectory("rollback"));
    Check(UpdateService.ApplyPrepared(rollback, _ => true).State == "installed" && !File.Exists(Path.Combine(installed, UpdateService.AdminExecutable)));
});
Test("An installed Admin launcher is replaced and restored with the same verified transaction", () =>
{
    var installed = Install(); foreach (var (name, bytes) in WithAdmin("1.0.0")) File.WriteAllBytes(Path.Combine(installed, name.Replace('/', Path.DirectorySeparatorChar)), bytes);
    var old = UpdateService.HashFile(Path.Combine(installed, UpdateService.AdminExecutable)); var zip = Zip(WithAdmin("2.0.0"));
    var prepared = UpdateService.PrepareAsync(zip, UpdateService.HashFile(zip), Commit, installed, NewDirectory("updates"), "1.0.0", new()).GetAwaiter().GetResult();
    Check(prepared.Files.ContainsKey(UpdateService.AdminExecutable));
    Check(UpdateService.ApplyPrepared(prepared, _ => false).State == "rolled-back" && UpdateService.HashFile(Path.Combine(installed, UpdateService.AdminExecutable)) == old);
    prepared = UpdateService.PrepareAsync(zip, UpdateService.HashFile(zip), Commit, installed, NewDirectory("retry"), "1.0.0", new()).GetAwaiter().GetResult();
    Check(UpdateService.ApplyPrepared(prepared, _ => true).State == "installed" && UpdateService.HashFile(Path.Combine(installed, UpdateService.AdminExecutable)) != old);
    var rollback = UpdateService.PrepareRollback(prepared, NewDirectory("rollback"));
    Check(UpdateService.ApplyPrepared(rollback, _ => true).State == "installed" && UpdateService.HashFile(Path.Combine(installed, UpdateService.AdminExecutable)) == old);
});
Test("An Admin launcher requires its exact metadata hash even when every archive hash was recomputed", () =>
{
    var files = WithAdmin("2.0.0"); files[UpdateService.AdminExecutable] = Encoding.UTF8.GetBytes("different launcher"); Seal(files);
    Reject<InvalidDataException>(() => Prepare(Zip(files)));
    files = WithAdmin("2.0.0"); files.Remove(UpdateService.AdminExecutable); Seal(files); Reject<InvalidDataException>(() => Prepare(Zip(files)));
});
Test("Outer archive digest rejects altered downloads before staging", () => Reject<InvalidDataException>(() => Prepare(expectedHash: new string('0', 64))));
Test("Every embedded file digest is verified even when outer digest matches", () =>
{
    var files = Payload(); files["wad/docs/help.txt"] = Encoding.UTF8.GetBytes("Modified"); Reject<InvalidDataException>(() => Prepare(Zip(files)));
});
Test("Provenance rejects wrong source commit platform application and executable digest", () =>
{
    Reject<InvalidDataException>(() => Prepare(commit: new string('b', 40)));
    foreach (var change in new[] { "target", "application", "executable_sha256" })
    {
        var files = Payload(); var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(files["wad/version.json"])!; metadata[change] = JsonSerializer.SerializeToElement("wrong"); files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata);
        files.Remove("wad/SHA256SUMS.txt"); files["wad/SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join('\n', files.Select(pair => Hash(pair.Value) + "  " + pair.Key)) + "\n");
        Reject<InvalidDataException>(() => Prepare(Zip(files)));
    }
});
Test("Equal and older versions require the dedicated rollback path", () => Reject<InvalidDataException>(() => Prepare(Zip(Payload("1.0.0")))));
Test("Archive paths reject traversal backslashes alternate streams device names and trailing dots", () =>
{
    foreach (var name in new[] { "../outside", "/absolute", "wad/../../escape", "wad\\evil", "wad/help.txt:stream", "wad/CON.txt", "wad/CON .txt", "wad/LPT1", "wad/COM¹.txt", "wad/LPT²", "wad/CONOUT$", "wad/trailing. ", "wad/end.", "wad/./same" })
        Reject<InvalidDataException>(() => Prepare(Zip(append: archive => { using var output = archive.CreateEntry(name).Open(); output.WriteByte(1); })));
    Check(!File.Exists(Path.Combine(root, "outside")));
});
Test("Archive case duplicates symbolic links and unknown roots are refused", () =>
{
    Reject<InvalidDataException>(() => Prepare(Zip(append: archive => archive.CreateEntry("wad/DOCS/help.txt"))));
    Reject<InvalidDataException>(() => Prepare(Zip(append: archive => { var entry = archive.CreateEntry("wad/link"); entry.ExternalAttributes = unchecked((int)0xa1ff0000); })));
    Reject<InvalidDataException>(() => Prepare(Zip(append: archive => { var entry = archive.CreateEntry("wad/misdeclared-file"); entry.ExternalAttributes = unchecked((int)0x41ed0000); })));
    Reject<InvalidDataException>(() => Prepare(Zip(append: archive => archive.CreateEntry("extra.exe"))));
});
Test("Inventory must cover exactly the archive and include the Windows browser driver", () =>
{
    var files = Payload(); files["wad/extra.txt"] = Encoding.UTF8.GetBytes("unlisted"); Reject<InvalidDataException>(() => Prepare(Zip(files)));
    files = Payload(); files.Remove("wad/.playwright/package/cli.js"); Reject<InvalidDataException>(() => Prepare(Zip(files)));
    files = Payload(); files.Remove("wad/SHA256SUMS.txt"); Reject<InvalidDataException>(() => Prepare(Zip(files)));
});
Test("A complete v3 WebView pack preserves runtime provenance through preparation and the transaction journal", () =>
{
    var staged = Prepare(Zip(Payload("3.0.0"))); var read = UpdateService.ReadPrepared(staged.TransactionPath);
    Check(read.Package.Ui == "webview2" && read.Package.WebView2Runtime == "154.0.4258.62" && read.Package.WebView2Loader == "bundled-native-x64");
    Check(UpdateService.RequiredWebViewFiles.All(read.Files.ContainsKey));
});
Test("Each mandatory v3 local asset runtime file and license is required even with a matching archive inventory", () =>
{
    foreach (var name in UpdateService.RequiredWebViewFiles)
    {
        var files = Payload("3.0.0"); files.Remove(name); Seal(files);
        Reject<InvalidDataException>(() => Prepare(Zip(files)));
    }
});
Test("WebView provenance rejects absent UI loader runtime SDK and CAB hash declarations", () =>
{
    foreach (var property in new[] { "ui", "webview2_sdk", "webview2_runtime", "webview2_runtime_sha256", "webview2_loader" })
    {
        var files = Payload("3.0.0"); var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(files["wad/version.json"])!;
        metadata[property] = JsonSerializer.SerializeToElement("invalid"); files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata); Seal(files);
        Reject<InvalidDataException>(() => Prepare(Zip(files)));
    }
    var plan = Prepare(Zip(Payload("3.0.0"))); plan.Files.Remove("wad/runtime/webview2/msedgewebview2.exe"); UpdateService.WriteJson(plan.TransactionPath, plan);
    Reject<InvalidDataException>(() => UpdateService.ReadPrepared(plan.TransactionPath));
});
Test("WebView full-pack rollback restores legacy metadata without requiring a runtime in a v2-era pack", () =>
{
    var plan = Prepare(Zip(Payload("3.0.0"))); var result = UpdateService.ApplyPrepared(plan, _ => false);
    Check(result.State == "rolled-back"); UpdateService.VerifyInventory(plan.InstallRoot, plan.PreviousFiles);
    Check(!Directory.Exists(Path.Combine(plan.InstallRoot, "wad", "runtime")));
});
Test("A light v3 pack without bundled runtime is accepted on the installed Windows runtime", () =>
{
    var staged = Prepare(Zip(LightPayload())); Check(staged.Package.Edition == "PROD-LIGHT" && staged.Package.WebView2Loader == UpdateService.EvergreenLoader);
    Check(!staged.Files.Keys.Any(name => name.StartsWith("wad/runtime/", StringComparison.Ordinal)) && UpdateService.RequiredLightWebViewFiles.All(staged.Files.ContainsKey));
});
Test("A light pack neither carries a bundled runtime nor declares a fixed one, and a complete pack keeps both", () =>
{
    var files = LightPayload(); files["wad/runtime/webview2/msedgewebview2.exe"] = Encoding.UTF8.GetBytes("stray runtime"); Seal(files); Reject<InvalidDataException>(() => Prepare(Zip(files)));
    files = LightPayload(); var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(files["wad/version.json"])!; metadata.WebView2Runtime = "154.0.4258.62";
    files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata); Seal(files); Reject<InvalidDataException>(() => Prepare(Zip(files)));
    files = Payload("3.0.0"); metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(files["wad/version.json"])!; metadata.WebView2Loader = UpdateService.EvergreenLoader;
    files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata); Seal(files); Reject<InvalidDataException>(() => Prepare(Zip(files)));
    files = LightPayload(); metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(files["wad/version.json"])!; metadata.Edition = "PROD";
    files["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata); Seal(files); Reject<InvalidDataException>(() => Prepare(Zip(files)));
});
Test("Complete and light packs never replace one another through an update", () =>
{
    foreach (var (installedFiles, candidate) in new[] { (Payload("3.0.0"), LightPayload("3.1.0")), (LightPayload("3.0.0"), Payload("3.1.0")) })
        Reject<InvalidDataException>(() => Prepare(Zip(candidate), InstallWith(installedFiles)));
    Check(Prepare(Zip(LightPayload("3.1.0")), InstallWith(LightPayload("3.0.0"))).Package.Edition == "PROD-LIGHT");
    Check(Prepare(Zip(Payload("3.1.0")), InstallWith(Payload("3.0.0"))).Package.Edition == "PROD");
});
Test("A complete installation moves to the light pack only once an engine exists outside the pack", () =>
{
    var previous = UpdateService.RuntimeOutsidePack;
    try
    {
        UpdateService.RuntimeOutsidePack = () => false;
        Reject<InvalidDataException>(() => Prepare(Zip(LightPayload("3.1.0")), InstallWith(Payload("3.0.0"))));
        UpdateService.RuntimeOutsidePack = () => true;
        Check(Prepare(Zip(LightPayload("3.1.0")), InstallWith(Payload("3.0.0"))).Package.Edition == "PROD-LIGHT");
        Reject<InvalidDataException>(() => Prepare(Zip(Payload("3.1.0")), InstallWith(LightPayload("3.0.0"))));
    }
    finally { UpdateService.RuntimeOutsidePack = previous; }
});
Test("Process ownership distinguishes this pack's WebView from sibling installations and unrelated runtimes", () =>
{
    var install = Install();
    Check(UpdateService.IsInstallProcessPath(Path.Combine(install, "wad", "runtime", "webview2", "msedgewebview2.exe"), install));
    Check(!UpdateService.IsInstallProcessPath(null, install));
    Check(!UpdateService.IsInstallProcessPath(Path.Combine(install + "-other", "msedgewebview2.exe"), install));
    Check(!UpdateService.IsInstallProcessPath(Path.Combine(root, "other-runtime", "msedgewebview2.exe"), install));
});
Test("Compression bomb ratio and duplicate hash lines fail within configured bounds", () =>
{
    var files = Payload(); files["wad/bomb.bin"] = new byte[2 * 1024 * 1024]; Reject<InvalidDataException>(() => Prepare(Zip(files)));
    var line = new string('a', 64) + "  GLPI-Pilot.exe\n"; Reject<InvalidDataException>(() => UpdateService.ParseHashes(line + line));
});
Test("Cancellation leaves the original installation untouched", () =>
{
    var install = Install(); var before = UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")); using var cts = new CancellationTokenSource(); cts.Cancel();
    Reject<OperationCanceledException>(() => Prepare(installation: install, ct: cts.Token)); Check(UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")) == before);
});
Test("Signature policy distinguishes integrity unsigned valid publisher and rejected signatures", () =>
{
    var pin = new string('c', 64); UpdateService.ValidateTrust(new("NotSigned", "", ""), new()); UpdateService.ValidateTrust(new("Valid", "Publisher", pin), new() { RequireSignature = true, TrustedPublisherSha256 = pin });
    Reject<InvalidDataException>(() => UpdateService.ValidateTrust(new("HashMismatch", "", ""), new()));
    Reject<InvalidDataException>(() => UpdateService.ValidateTrust(new("Valid", "Publisher", ""), new()));
    Reject<InvalidDataException>(() => UpdateService.ValidateTrust(new("NotSigned", "", ""), new() { RequireSignature = true, TrustedPublisherSha256 = pin }));
    Reject<InvalidDataException>(() => UpdateService.ValidateTrust(new("Valid", "Other", new string('d', 64)), new() { TrustedPublisherSha256 = pin }));
    Reject<InvalidDataException>(() => new UpdatePolicy { RequireSignature = true }.Validate());
});
Test("Signature responses distinguish an explicit unsigned result from a failed command", () =>
{
    var unsigned = JsonSerializer.Serialize(new UpdateSignature("NotSigned", "", ""));
    Check(UpdateService.ParseSignatureResponse(0, unsigned, "").Status == "NotSigned");
    try { UpdateService.ParseSignatureResponse(1, unsigned, "Synthetic module load failure\nHidden continuation"); throw new InvalidOperationException("A failed verifier was accepted."); }
    catch (InvalidDataException error) { Check(error.Message.Contains("code de sortie 1") && error.Message.Contains("Synthetic module load failure") && !error.Message.Contains("Hidden continuation")); }
    try { UpdateService.ParseSignatureResponse(1, "", new string('x', 20_000)); throw new InvalidOperationException("An oversized error was accepted."); }
    catch (InvalidDataException error) { Check(error.Message.Length < 1200); }
});
Test("Signature response rejects invalid JSON incomplete certificates and oversized output", () =>
{
    Reject<InvalidDataException>(() => UpdateService.ParseSignatureResponse(0, "not JSON", ""));
    Reject<InvalidDataException>(() => UpdateService.ParseSignatureResponse(0, "{}", ""));
    Reject<InvalidDataException>(() => UpdateService.ParseSignatureResponse(0, JsonSerializer.Serialize(new UpdateSignature("Valid", "Publisher", "")), ""));
    Reject<InvalidDataException>(() => UpdateService.ParseSignatureResponse(0, new string(' ', 16385), ""));
    var pin = new string('c', 64);
    var valid = UpdateService.ParseSignatureResponse(0, JsonSerializer.Serialize(new UpdateSignature("Valid", "Éditeur vérifié", pin)), "");
    UpdateService.ValidateTrust(valid, new() { RequireSignature = true, TrustedPublisherSha256 = pin });
});
if (OperatingSystem.IsWindows())
    Test("Native Windows signature check ignores incompatible inherited modules and refuses a missing file", () =>
    {
        var modules = NewDirectory("incompatible-powershell-modules");
        foreach (var module in new[] { "Microsoft.PowerShell.Security", "Microsoft.PowerShell.Utility" })
        {
            var folder = Path.Combine(modules, module); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, module + ".psd1"), "@{ ModuleVersion = '99.0'; PowerShellVersion = '99.0'; RootModule = 'incompatible.psm1' }", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "incompatible.psm1"), "throw 'The incompatible inherited module must never load.'", new UTF8Encoding(false));
        }
        var previous = Environment.GetEnvironmentVariable("PSModulePath");
        try
        {
            Environment.SetEnvironmentVariable("PSModulePath", modules);
            var unsignedPath = Path.Combine(root, "unsigned-signature-fixture.ps1"); File.WriteAllText(unsignedPath, "# Unsigned data fixture. Never executed.\r\n", new UTF8Encoding(false));
            var unsigned = UpdateService.CheckSignatureAsync(unsignedPath).GetAwaiter().GetResult();
            Check(unsigned.Status == "NotSigned" && unsigned.CertificateSha256.Length == 0 && unsigned.Publisher.Length == 0);
            Reject<InvalidDataException>(() => UpdateService.CheckSignatureAsync(Path.Combine(root, "missing-signature-fixture.exe")).GetAwaiter().GetResult());
            Check(Environment.GetEnvironmentVariable("PSModulePath") == modules, "The verifier must isolate only its child environment.");
        }
        finally { Environment.SetEnvironmentVariable("PSModulePath", previous); }
    });
Test("Successful replacement verifies startup and keeps a complete independent backup", () =>
{
    var value = Prepare(); var before = UpdateService.HashFile(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe")); var checks = 0;
    var result = UpdateService.ApplyPrepared(value, plan => { checks++; return UpdateService.HashFile(Path.Combine(plan.InstallRoot, "GLPI-Pilot.exe")) == plan.Files["GLPI-Pilot.exe"] && Directory.Exists(plan.BackupRoot); });
    Check(result.State == "installed" && checks == 1 && UpdateService.HashFile(Path.Combine(value.BackupRoot, "GLPI-Pilot.exe")) == before);
    Check(File.Exists(Path.Combine(value.InstallRoot, "personal-export.csv")) && UpdateService.ReadLastOutcome(Path.GetDirectoryName(value.WorkRoot)!)!.State == "installed");
    Reject<InvalidDataException>(() => UpdateService.ApplyPrepared(value, _ => true));
});
Test("Failed startup restores original EXE driver documents and preserves user files", () =>
{
    var value = Prepare(); var result = UpdateService.ApplyPrepared(value, _ => false);
    Check(result.State == "rolled-back" && value.PreviousFiles.All(pair => UpdateService.HashFile(Path.Combine(value.InstallRoot, pair.Key)) == pair.Value));
    Check(File.Exists(Path.Combine(value.InstallRoot, "personal-export.csv")) && UpdateService.ReadPrepared(value.TransactionPath).State == "rolled-back");
});
Test("A local wad deployment default is copied into the verified stage and survives update with its legacy root file", () =>
{
    var install = Install(); var path = Path.Combine(install, UpdateService.DeploymentDefaultRelativePath);
    var content = Encoding.UTF8.GetBytes("{\"fixture\":\"local default and accents é\"}\r\n"); File.WriteAllBytes(path, content);
    var legacy = Path.Combine(install, UpdateService.DeploymentDefaultFileName); File.WriteAllText(legacy, "legacy root configuration retained");
    var zip = Zip(); var archiveHash = UpdateService.HashFile(zip); var value = Prepare(zip, install);
    Check(value.Files[UpdateService.DeploymentDefaultRelativePath] == Hash(content)
        && File.ReadAllBytes(Path.Combine(value.StageRoot, UpdateService.DeploymentDefaultRelativePath)).SequenceEqual(content)
        && !UpdateService.ParseHashes(File.ReadAllText(Path.Combine(value.StageRoot, "wad/SHA256SUMS.txt"))).ContainsKey(UpdateService.DeploymentDefaultRelativePath));
    value = UpdateService.ReadPrepared(value.TransactionPath);
    var result = UpdateService.ApplyPrepared(value, plan => File.ReadAllBytes(path).SequenceEqual(content));
    Check(result.State == "installed" && File.ReadAllBytes(path).SequenceEqual(content)
        && File.ReadAllBytes(Path.Combine(value.BackupRoot, UpdateService.DeploymentDefaultRelativePath)).SequenceEqual(content)
        && File.ReadAllText(legacy) == "legacy root configuration retained" && UpdateService.HashFile(zip) == archiveHash);
});
Test("An incoming deployment default cannot silently replace a different local default", () =>
{
    var install = Install(); var path = Path.Combine(install, UpdateService.DeploymentDefaultRelativePath); File.WriteAllText(path, "local configuration");
    var oldExe = UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe"));
    var files = Payload(); files[UpdateService.DeploymentDefaultRelativePath] = Encoding.UTF8.GetBytes("different supplied configuration"); Seal(files);
    Reject<InvalidDataException>(() => Prepare(Zip(files), install));
    Check(File.ReadAllText(path) == "local configuration" && UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")) == oldExe);
});
Test("Identical supplied deployment defaults remain unambiguous and a new supplied default is accepted when absent locally", () =>
{
    var contents = Encoding.UTF8.GetBytes("same supplied configuration"); var files = Payload(); files[UpdateService.DeploymentDefaultRelativePath] = contents; Seal(files);
    foreach (var present in new[] { false, true })
    {
        var install = Install(); var path = Path.Combine(install, UpdateService.DeploymentDefaultRelativePath); if (present) File.WriteAllBytes(path, contents);
        var value = Prepare(Zip(files), install); Check(value.Files.Keys.Count(name => name.Equals(UpdateService.DeploymentDefaultRelativePath, StringComparison.OrdinalIgnoreCase)) == 1);
        Check(UpdateService.ApplyPrepared(value, _ => File.ReadAllBytes(path).SequenceEqual(contents)).State == "installed");
    }
});
Test("A deployment default changed after preparation is rejected before touching the installed pack", () =>
{
    var install = Install(); var path = Path.Combine(install, UpdateService.DeploymentDefaultRelativePath); File.WriteAllText(path, "observed default");
    var value = Prepare(installation: install); File.WriteAllText(path, "concurrently changed default");
    Reject<InvalidDataException>(() => UpdateService.ApplyPrepared(value, _ => true));
    Check(!Directory.Exists(value.BackupRoot) && File.ReadAllText(path) == "concurrently changed default"
        && UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")) == value.PreviousFiles["GLPI-Pilot.exe"]);
});
Test("Failed startup restores the exact local deployment default with the former wad", () =>
{
    var install = Install(); var path = Path.Combine(install, UpdateService.DeploymentDefaultRelativePath); var content = Encoding.UTF8.GetBytes("default kept through failed startup\r\n"); File.WriteAllBytes(path, content);
    var value = Prepare(installation: install); var seen = false;
    var result = UpdateService.ApplyPrepared(value, _ => { seen = File.ReadAllBytes(path).SequenceEqual(content); return false; });
    Check(seen && result.State == "rolled-back" && File.ReadAllBytes(path).SequenceEqual(content)
        && File.ReadAllBytes(Path.Combine(value.BackupRoot, UpdateService.DeploymentDefaultRelativePath)).SequenceEqual(content));
});
Test("Manual rollback preserves the latest local deployment default or its deliberate removal", () =>
{
    foreach (var remove in new[] { false, true })
    {
        var install = Install(); var path = Path.Combine(install, UpdateService.DeploymentDefaultRelativePath); File.WriteAllText(path, "default before update");
        var value = Prepare(installation: install); Check(UpdateService.ApplyPrepared(value, _ => true).State == "installed");
        if (remove) File.Delete(path); else File.WriteAllText(path, "default edited after update");
        var rollback = UpdateService.PrepareRollback(value, NewDirectory("deployment-rollback"));
        Check(rollback.Files.ContainsKey(UpdateService.DeploymentDefaultRelativePath) == !remove);
        var result = UpdateService.ApplyPrepared(rollback, _ => remove ? !File.Exists(path) : File.ReadAllText(path) == "default edited after update");
        Check(result.State == "installed" && (remove ? !File.Exists(path) : File.ReadAllText(path) == "default edited after update")
            && UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")) == value.PreviousFiles["GLPI-Pilot.exe"]);
    }
});
Test("Exception during startup triggers the same verified binary rollback", () =>
{
    var value = Prepare(); var result = UpdateService.ApplyPrepared(value, _ => throw new IOException("Synthetic startup failure"));
    Check(result.State == "rolled-back" && result.Detail.Contains("Synthetic startup failure") && UpdateService.HashFile(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe")) == value.PreviousFiles["GLPI-Pilot.exe"]);
});
if (OperatingSystem.IsWindows())
    Test("A transient native Windows directory lock is released before verified rollback completes", () =>
    {
        var value = Prepare(); SafeFileHandle? held = null; Task? release = null;
        try
        {
            var result = UpdateService.ApplyPrepared(value, plan =>
            {
                var wad = Path.Combine(plan.InstallRoot, "wad");
                var directoryLock = WindowsDirectoryLock.Open(wad); held = directoryLock;
                Check(!directoryLock.IsInvalid, "Unable to hold the Windows directory: " + Marshal.GetLastPInvokeError());
                var refused = false;
                try { Directory.Move(wad, Path.Combine(plan.InstallRoot, "unexpectedly-moved-wad")); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) is 5 or 32 or 33) { refused = true; }
                Check(refused, "The fixture directory handle did not actually prevent its rename.");
                release = Task.Run(() =>
                {
                    try
                    {
                        // The failed EXE is moved immediately before wad. Keep
                        // the directory locked across that first rollback attempt.
                        var movedExe = Path.Combine(plan.InstallRoot, ".glpi-failed-" + plan.Id, "GLPI-Pilot.exe");
                        var deadline = System.Diagnostics.Stopwatch.StartNew();
                        while (!File.Exists(movedExe) && deadline.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
                        Thread.Sleep(500);
                    }
                    finally { directoryLock.Dispose(); }
                });
                return false;
            });
            release?.GetAwaiter().GetResult();
            Check(result.State == "rolled-back", result.Detail);
            UpdateService.VerifyInventory(value.InstallRoot, value.PreviousFiles);
            UpdateService.VerifyInventory(value.BackupRoot, value.PreviousFiles);
            Check(File.ReadAllText(Path.Combine(value.InstallRoot, "personal-export.csv")) == "User-owned root file");
        }
        finally { held?.Dispose(); release?.GetAwaiter().GetResult(); }
    });
Test("Changed staged or installed files are rejected before replacement", () =>
{
    var stage = Prepare(); File.WriteAllText(Path.Combine(stage.StageRoot, "GLPI-Pilot.exe"), "tampered"); Reject<InvalidDataException>(() => UpdateService.ApplyPrepared(stage, _ => true));
    Check(!Directory.Exists(stage.BackupRoot)); var current = Prepare(); File.AppendAllText(Path.Combine(current.InstallRoot, "wad/docs/help.txt"), "changed"); Reject<InvalidDataException>(() => UpdateService.ApplyPrepared(current, _ => true)); Check(!Directory.Exists(current.BackupRoot));
});
Test("Malformed transaction inventory and overlapping roots cannot start replacement", () =>
{
    var incomplete = Prepare(); incomplete.Files.Remove("wad/.playwright/package/cli.js"); Reject<InvalidDataException>(() => UpdateService.ApplyPrepared(incomplete, _ => true)); Check(!Directory.Exists(incomplete.BackupRoot));
    var duplicate = Prepare(); duplicate.Files = new Dictionary<string, string>(duplicate.Files, StringComparer.Ordinal); duplicate.Files.Add("wad/DOCS/help.txt", duplicate.Files["wad/docs/help.txt"]);
    Reject<InvalidDataException>(() => UpdateService.ApplyPrepared(duplicate, _ => true)); Check(!Directory.Exists(duplicate.BackupRoot));
    var overlap = Prepare(); overlap.WorkRoot = Path.Combine(overlap.InstallRoot, "nested-update"); Reject<InvalidDataException>(() => UpdateService.ApplyPrepared(overlap, _ => true));
});
Test("A held update lock prevents concurrent replacement and an abandoned unlocked marker is reusable", () =>
{
    var plan = Prepare(); var lockPath = Path.Combine(plan.InstallRoot, ".glpi-update.lock");
    using (var held = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    { Reject<IOException>(() => UpdateService.ApplyPrepared(plan, _ => true)); Check(!Directory.Exists(plan.BackupRoot)); }
    Check(File.Exists(lockPath)); Check(UpdateService.ApplyPrepared(plan, _ => true).State == "installed"); Check(!File.Exists(lockPath));
});
Test("Manual rollback is staged from saved hashes and itself retains a reverse backup", () =>
{
    var installed = Prepare(); Check(UpdateService.ApplyPrepared(installed, _ => true).State == "installed"); var updates = Path.GetDirectoryName(installed.WorkRoot)!;
    Check(UpdateService.FindRollback(updates, installed.InstallRoot)?.Id == installed.Id); var rollback = UpdateService.PrepareRollback(installed, updates);
    Check(rollback.IsRollback && rollback.Package.Version == "1.0.0" && UpdateService.ApplyPrepared(rollback, _ => true).State == "installed");
    Check(UpdateService.HashFile(Path.Combine(installed.InstallRoot, "GLPI-Pilot.exe")) == installed.PreviousFiles["GLPI-Pilot.exe"] && Directory.Exists(rollback.BackupRoot));
});
Test("A corrupted backup cannot become a rollback package", () =>
{
    var installed = Prepare(); UpdateService.ApplyPrepared(installed, _ => true); File.AppendAllText(Path.Combine(installed.BackupRoot, "GLPI-Pilot.exe"), "changed");
    Reject<InvalidDataException>(() => UpdateService.PrepareRollback(installed, Path.GetDirectoryName(installed.WorkRoot)!));
});
Test("Shared diffusion selects numeric latest PROD and ignores DEV or temporary files", () =>
{
    var folder = NewDirectory("shared"); var hashes = new List<string>();
    foreach (var version in new[] { "3.9.0", "3.10.0" })
    {
        var name = "GLPI-Pilot_" + version + "_PROD_Windows-x64.zip";
        File.Copy(Zip(Payload(version)), Path.Combine(folder, name)); hashes.Add(UpdateService.HashFile(Path.Combine(folder, name)) + "  " + name);
    }
    hashes.Add(new string('f', 64) + "  GLPI-Pilot_9.0.0_DEV_Windows-x64.zip");
    hashes.Add(new string('f', 64) + "  GLPI-Pilot_8.0.0_PROD_Windows-x64.zip.tmp");
    File.WriteAllLines(Path.Combine(folder, "SHA256SUMS.txt"), hashes);
    var offer = SharedUpdateSource.FindAsync(folder).GetAwaiter().GetResult(); Check(offer.Version == "3.10.0");
    var local = NewDirectory("local-copy"); var copied = SharedUpdateSource.CopyVerifiedAsync(folder, offer.ArchiveName, offer.Sha256, offer.Bytes, local).GetAwaiter().GetResult();
    Check(Path.GetDirectoryName(copied) == local && UpdateService.HashFile(copied) == offer.Sha256);
    Check(SharedUpdateSource.ReadCopiedMetadata(copied, offer).SourceCommit == Commit);
    var prepared = Prepare(copied); Check(prepared.Package.Version == "3.10.0");
});
Test("A shared file changed after discovery is refused and every partial local copy removed", () =>
{
    var share = NewDirectory("shared-changed"); var file = Path.Combine(share, "pack.zip"); File.Copy(Zip(), file);
    var hash = UpdateService.HashFile(file); var size = new FileInfo(file).Length; var local = NewDirectory("local-changed");
    var changed = File.ReadAllBytes(file); changed[0] ^= 1; File.WriteAllBytes(file, changed);
    Reject<InvalidDataException>(() => SharedUpdateSource.CopyVerifiedAsync(share, "pack.zip", hash, size, local).GetAwaiter().GetResult());
    Check(!Directory.EnumerateFiles(local).Any());
    File.AppendAllText(file, "incomplete publication");
    Reject<InvalidDataException>(() => SharedUpdateSource.CopyVerifiedAsync(share, "pack.zip", hash, size, local).GetAwaiter().GetResult());
    Check(!Directory.EnumerateFiles(local).Any());
});
Test("Shared metadata cannot substitute another edition or version", () =>
{
    var zip = Zip(Payload("3.6.0")); var offer = new SharedUpdateOffer(root, "GLPI-Pilot_3.7.0_PROD_Windows-x64.zip", "3.7.0", UpdateService.HashFile(zip), new FileInfo(zip).Length);
    Reject<InvalidDataException>(() => SharedUpdateSource.ReadCopiedMetadata(zip, offer));
    var payload = Payload("3.6.0"); var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(payload["wad/version.json"])!;
    metadata.Edition = "DEV"; payload["wad/version.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata);
    Reject<InvalidDataException>(() => SharedUpdateSource.ReadCopiedMetadata(Zip(payload), offer with { Version = "3.6.0" }));
});
Test("Unavailable malformed or oversized diffusion manifests cannot offer an update", () =>
{
    Reject<InvalidDataException>(() => SharedUpdateSource.ValidateFolder("https://example.invalid/distribution"));
    Reject<InvalidDataException>(() => SharedUpdateSource.ValidateFolder("relative-folder"));
    var folder = NewDirectory("bad-shared");
    Reject<FileNotFoundException>(() => SharedUpdateSource.FindAsync(folder).GetAwaiter().GetResult());
    File.WriteAllText(Path.Combine(folder, "SHA256SUMS.txt"), new string('x', 1024 * 1024 + 1));
    Reject<InvalidDataException>(() => SharedUpdateSource.FindAsync(folder).GetAwaiter().GetResult());
    File.WriteAllText(Path.Combine(folder, "SHA256SUMS.txt"), new string('0',64) + "  ../escape.zip\n");
    Reject<InvalidDataException>(() => SharedUpdateSource.FindAsync(folder).GetAwaiter().GetResult());
});
Test("Cancelling the shared copy preserves the installation and removes the temporary file", () =>
{
    var share = NewDirectory("shared-cancelled"); var file = Path.Combine(share, "pack.zip"); File.Copy(Zip(), file);
    var local = NewDirectory("cancelled-copy"); using var ct = new CancellationTokenSource(); ct.Cancel();
    Reject<OperationCanceledException>(() => SharedUpdateSource.CopyVerifiedAsync(share, "pack.zip", UpdateService.HashFile(file), new FileInfo(file).Length, local, ct.Token).GetAwaiter().GetResult());
    Check(!Directory.EnumerateFiles(local).Any() && File.Exists(file));
});
Test("Site diffusion accepts only the versioned CSV and its independent checksum", () =>
{
    var folder = NewDirectory("sites-diffusion"); var file = Path.Combine(folder, SharedUpdateSource.SiteFile); File.WriteAllText(file, "synthetic-reference");
    var hash = UpdateService.HashFile(file); File.WriteAllText(Path.Combine(folder, SharedUpdateSource.SiteHashes), hash + "  " + SharedUpdateSource.SiteFile + "\n");
    var offer = SharedUpdateSource.FindSitesAsync(folder).GetAwaiter().GetResult(); Check(offer.Name == SharedUpdateSource.SiteFile && offer.Sha256 == hash);
    File.AppendAllText(Path.Combine(folder, SharedUpdateSource.SiteHashes), hash + "  other.csv\n");
    Reject<InvalidDataException>(() => SharedUpdateSource.FindSitesAsync(folder).GetAwaiter().GetResult());
});

var passed = 0;
try
{
    if (args.Length > 0)
    {
        if (args.Length != 4 || args[0] != "--verify-pack") throw new ArgumentException("Usage: UpdateChecks --verify-pack <ZIP> <expected-SHA256> <expected-source-commit>");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var verified = UpdateService.PrepareArchive(args[1], args[2], args[3], Install(), NewDirectory("pack-validation"), "0.0.0", false, timeout.Token);
        Check(UpdateService.ReadPrepared(verified.TransactionPath).Files.Count == verified.Files.Count);
        Console.WriteLine($"PASS package accepted by the production update verifier: {verified.Package.Edition} {verified.Package.Version}; {verified.Files.Count} hashed files; staged only, no application launched.");
        return;
    }
    foreach (var (name, run) in tests) try { run(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { Console.Error.WriteLine("FAIL " + name + " : " + error); }
    Console.WriteLine($"{passed}/{tests.Count} update checks passed."); Environment.ExitCode = passed == tests.Count ? 0 : 1;
}
finally { Directory.Delete(root, true); }

static class WindowsDirectoryLock
{
    // A directory handle without FILE_SHARE_DELETE reproduces the exact Windows
    // rename refusal while allowing the fixture's ordinary reads and writes.
    public static SafeFileHandle Open(string path) => CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
