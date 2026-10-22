using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

var root = Path.Combine(Path.GetTempPath(), "GLPI-UpdateChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string Api = "https://api.github.com/repos/M4D135/GLPI-Pilot/";
const string Download = "https://github.com/M4D135/GLPI-Pilot/releases/download/v2.0.0/";
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
    files["wad/SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join('\n', files.Select(pair => Hash(pair.Value) + "  " + pair.Key)) + "\n"); return files;
}
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
HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
string Release(byte[] zip, string archiveUrl = Download + "GLPI-Pilot_2.0.0_PROD_Windows-x64.zip", bool draft = false) => JsonSerializer.Serialize(new
{
    draft, prerelease = false, tag_name = "v2.0.0", published_at = "2026-10-26T10:00:00Z",
    assets = new[] { new { name = "GLPI-Pilot_2.0.0_PROD_Windows-x64.zip", size = (long)zip.Length, browser_download_url = archiveUrl }, new { name = "SHA256SUMS.txt", size = 150L, browser_download_url = Download + "SHA256SUMS.txt" } }
});
FixtureHandler OfficialHandler(byte[] bytes, bool annotated = false) => new((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
{
    Api + "releases/latest" => Json(Release(bytes)),
    Download + "SHA256SUMS.txt" => Json(Hash(bytes) + "  GLPI-Pilot_2.0.0_PROD_Windows-x64.zip\n"),
    Api + "git/ref/tags/v2.0.0" => Json("{\"object\":{\"type\":\"" + (annotated ? "tag" : "commit") + "\",\"sha\":\"" + Commit + "\"}}"),
    Api + "git/tags/" + Commit => Json("{\"object\":{\"type\":\"commit\",\"sha\":\"" + Commit + "\"}}"),
    Download + "GLPI-Pilot_2.0.0_PROD_Windows-x64.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
    _ => Json("{}", HttpStatusCode.NotFound)
}));

Test("Complete package stages separately and preserves running binaries and unrelated root files", () =>
{
    var install = Install(); var old = UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")); var staged = Prepare(installation: install);
    Check(staged.Package.Version == "2.0.0" && staged.Package.SourceCommit == Commit && staged.Files.Count == 6 && Directory.Exists(staged.StageRoot));
    Check(UpdateService.HashFile(Path.Combine(install, "GLPI-Pilot.exe")) == old && File.ReadAllText(Path.Combine(install, "personal-export.csv")) == "User-owned root file");
    Check(UpdateService.ReadPrepared(staged.TransactionPath).Files.Count == staged.Files.Count);
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
Test("Exception during startup triggers the same verified binary rollback", () =>
{
    var value = Prepare(); var result = UpdateService.ApplyPrepared(value, _ => throw new IOException("Synthetic startup failure"));
    Check(result.State == "rolled-back" && result.Detail.Contains("Synthetic startup failure") && UpdateService.HashFile(Path.Combine(value.InstallRoot, "GLPI-Pilot.exe")) == value.PreviousFiles["GLPI-Pilot.exe"]);
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
Test("Official release lookup resolves tag commit and checksum without credentials", () =>
{
    var bytes = File.ReadAllBytes(Zip()); using var handler = OfficialHandler(bytes); using var client = new UpdateReleaseClient(handler); var release = client.FindLatestAsync("PROD").GetAwaiter().GetResult();
    Check(release.Commit == Commit && release.ArchiveSha256 == Hash(bytes) && release.Version == "2.0.0" && handler.Requests.Count == 3 && handler.NoCredentials);
});
Test("Annotated official tags resolve to a concrete commit within a bounded traversal", () =>
{
    var bytes = File.ReadAllBytes(Zip()); using var handler = OfficialHandler(bytes, true); using var client = new UpdateReleaseClient(handler); Check(client.FindLatestAsync("PROD").GetAwaiter().GetResult().Commit == Commit && handler.Requests.Count == 4);
});
Test("Official archive downloads are verified before exposure and removed on mismatch", () =>
{
    var bytes = File.ReadAllBytes(Zip()); using var handler = OfficialHandler(bytes); using var client = new UpdateReleaseClient(handler); var release = client.FindLatestAsync("PROD").GetAwaiter().GetResult();
    var folder = NewDirectory("downloads"); var path = client.DownloadAsync(release, folder).GetAwaiter().GetResult(); Check(File.ReadAllBytes(path).SequenceEqual(bytes));
    var before = Directory.GetFiles(folder).Length; Reject<InvalidDataException>(() => client.DownloadAsync(release with { ArchiveSha256 = new string('0', 64) }, folder).GetAwaiter().GetResult()); Check(Directory.GetFiles(folder).Length == before);
});
Test("Foreign repository assets unpublished releases and cross host redirects are rejected", () =>
{
    var bytes = File.ReadAllBytes(Zip()); foreach (var body in new[] { Release(bytes, "https://example.invalid/fake.zip"), Release(bytes, draft: true) })
    { using var client = new UpdateReleaseClient(new FixtureHandler((_, _) => Task.FromResult(Json(body)))); Reject<InvalidDataException>(() => client.FindLatestAsync("PROD").GetAwaiter().GetResult()); }
    using var handler = new FixtureHandler((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("https://evil.example.invalid/steal"); return Task.FromResult(response); });
    using var redirect = new UpdateReleaseClient(handler); var release = new OfficialUpdate("2.0.0", "v2.0.0", Commit, "PROD", "GLPI-Pilot_2.0.0_PROD_Windows-x64.zip", Download + "GLPI-Pilot_2.0.0_PROD_Windows-x64.zip", Hash(bytes), bytes.Length, DateTimeOffset.UtcNow, UpdateReleaseClient.ReleasesPage);
    Reject<InvalidDataException>(() => redirect.DownloadAsync(release, NewDirectory("redirect")).GetAwaiter().GetResult()); Check(handler.Requests.Count == 1);
});
Test("Explicit network cancellation issues no request and creates no downloaded file", () =>
{
    var bytes = File.ReadAllBytes(Zip()); using var handler = OfficialHandler(bytes); using var client = new UpdateReleaseClient(handler); using var cts = new CancellationTokenSource(); cts.Cancel();
    Reject<OperationCanceledException>(() => client.FindLatestAsync("PROD", cts.Token).GetAwaiter().GetResult()); Check(handler.Requests.Count == 0);
});

var passed = 0;
try
{
    foreach (var (name, run) in tests) try { run(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { Console.Error.WriteLine("FAIL " + name + " : " + error); }
    Console.WriteLine($"{passed}/{tests.Count} update checks passed."); Environment.ExitCode = passed == tests.Count ? 0 : 1;
}
finally { Directory.Delete(root, true); }

sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = new();
    public bool NoCredentials { get; private set; } = true;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!.AbsoluteUri); NoCredentials &= request.Headers.Authorization == null && !request.Headers.Contains("Cookie"); return respond(request, cancellationToken); }
}
