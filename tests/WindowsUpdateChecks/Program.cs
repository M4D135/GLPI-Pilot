using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

// This harness never targets an installed application or fetches a release. It runs
// the actual packaged EXE and its maintenance helper against disposable pack copies.
return new WindowsUpdateFixture().Run(args);

internal sealed class WindowsUpdateFixture
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly List<Process> _started = new();
    private readonly List<string> _messages = new();
    private readonly List<(string Name, PreparedUpdate Plan)> _plans = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromMinutes(15));
    private string _sandbox = "", _output = "", _archive = "", _archiveHash = "", _state = "";
    private string _settingsHash = "";
    private bool _createdState, _createdSandbox;
    private int _passed;

    public int Run(string[] arguments)
    {
        var success = false;
        try
        {
            if (arguments.Length != 2) throw new ArgumentException("Usage: WindowsUpdateChecks <PROD-pack.zip> <isolated-log-directory>");
            Require(OperatingSystem.IsWindows(), "NOT RUN: this test requires real Windows process and file-lock behavior.");
            using (var current = Process.GetCurrentProcess()) Require(Environment.UserInteractive && current.SessionId != 0, "An interactive Windows desktop is required.");
            foreach (var existing in Process.GetProcessesByName("GLPI-Pilot"))
                using (existing) Require(existing.HasExited, "An existing GLPI-Pilot process prevents an isolated update test.");
            _archive = Path.GetFullPath(arguments[0]); Require(File.Exists(_archive), "The already-built PROD pack does not exist.");
            _output = Path.GetFullPath(arguments[1]); UpdateService.EnsureNoLinks(_output); Directory.CreateDirectory(_output);
            _sandbox = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Update-" + _owner); UpdateService.EnsureNoLinks(_sandbox);
            Require(!Directory.Exists(_sandbox), "The unique fixture directory already exists.");
            Directory.CreateDirectory(_sandbox); _createdSandbox = true; File.WriteAllText(Path.Combine(_sandbox, "fixture-owner.txt"), _owner);
            _state = Path.Combine(_sandbox, "GLPI-Pilot-Checks-State");
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
            Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", _state);
            Require(AppPaths.TestDataRoot == _state && AppPaths.DataRoot == _state, "The guarded isolated profile was not accepted.");
            UpdateService.EnsureNoLinks(_state); Directory.CreateDirectory(_state); _createdState = true;
            File.WriteAllText(Path.Combine(_state, "fixture-owner.txt"), _owner);
            UpdateService.WriteJson(Path.Combine(_state, "settings.json"), new
            {
                PortalUrl = "https://glpi.example.invalid/front/ticket.php", Theme = "dark", ExportFolder = Path.Combine(_sandbox, "exports"),
                Interface = new { OnboardingCompleted = true }, Automation = new { ScheduledExtraction = false, ResumeAfterReconnect = false }
            });
            _settingsHash = UpdateService.HashFile(Path.Combine(_state, "settings.json"));
            _archiveHash = UpdateService.HashFile(_archive);
            Log("Fixture copies only. The guarded temporary profile isolates all state. No GLPI session or browser extraction is requested; the real embedded WebView2 renders locally.");

            CheckRunningProcess(); CleanupCompletedPlans();
            CheckLockedExecutable(); CleanupCompletedPlans();
            CheckHealthyReplacement(); CleanupCompletedPlans();
            CheckFailedStartupRollback(); CleanupCompletedPlans();
            Require(UpdateService.HashFile(Path.Combine(_state, "settings.json")) == _settingsHash, "Synthetic settings changed during the update tests.");
            Require(UpdateService.HashFile(_archive) == _archiveHash, "The original CI release archive changed.");
            success = true;
        }
        catch (Exception error) { Log("FAIL: " + error); }
        finally
        {
            try { StopOwnedProcesses(); }
            catch (Exception error) { success = false; Log("FAIL: fixture process cleanup: " + error.Message); }
            try { CopyDiagnostics(); }
            catch (Exception error) { success = false; Log("FAIL: diagnostic copy: " + error.Message); }
            try
            {
                if (_createdState) DeleteOwnedDirectory(_state);
                if (_createdSandbox) DeleteOwnedDirectory(_sandbox);
            }
            catch (Exception error) { success = false; Log("FAIL: fixture directory cleanup: " + error.Message); }
            foreach (var process in _started) process.Dispose();
            _deadline.Dispose();
            if (_output.Length > 0) File.WriteAllLines(Path.Combine(_output, "windows-update-fixture.log"), _messages, new UTF8Encoding(false));
        }
        if (!success) return 1;
        Console.WriteLine($"{_passed}/4 Windows update checks passed.");
        return _passed == 4 ? 0 : 1;
    }

    private void CheckRunningProcess()
    {
        var plan = Prepare("running-process");
        var app = Start(Path.Combine(plan.InstallRoot, "GLPI-Pilot.exe"));
        WaitForWindow(app, TimeSpan.FromSeconds(30));
        try
        {
            var refused = false;
            try { UpdateService.ApplyPrepared(plan, _ => throw new InvalidOperationException("Health callback must not run."), _deadline.Token); }
            catch (IOException) { refused = true; }
            Require(refused, "A running application was not rejected before replacement.");
            UpdateService.VerifyInventory(plan.InstallRoot, plan.PreviousFiles, _deadline.Token);
            Require(!Directory.Exists(plan.BackupRoot), "Running-process rejection unexpectedly started a backup or replacement.");
            Pass("An actual running packaged application prevents replacement; every original file remains unchanged.");
        }
        finally { StopOwnedProcesses(); }
    }

    private void CheckLockedExecutable()
    {
        var plan = Prepare("locked-executable"); var healthCalled = false;
        using (var held = new FileStream(Path.Combine(plan.InstallRoot, "GLPI-Pilot.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = UpdateService.ApplyPrepared(plan, _ => { healthCalled = true; return true; }, _deadline.Token);
            Require(result.State == "rolled-back" && !healthCalled, "A locked EXE must abort before launching the new application.");
            UpdateService.VerifyInventory(plan.InstallRoot, plan.PreviousFiles, _deadline.Token);
            UpdateService.VerifyInventory(plan.BackupRoot, plan.PreviousFiles, _deadline.Token);
        }
        CleanupCompletedPlans();
        var blockedRollback = Prepare("locked-rollback"); FileStream? heldNew = null;
        try
        {
            var result = UpdateService.ApplyPrepared(blockedRollback, _ =>
            {
                heldNew = new FileStream(Path.Combine(blockedRollback.InstallRoot, "GLPI-Pilot.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
                return false;
            }, _deadline.Token);
            Require(result.State == "recovery-needed", "A lock which prevents rollback must be reported explicitly, never as a successful restoration.");
            Require(result.Detail.Contains("Échec initial :") && result.Detail.Contains("Restauration :"), "Both the failed startup and blocked restoration must remain diagnosable.");
            UpdateService.VerifyInventory(blockedRollback.BackupRoot, blockedRollback.PreviousFiles, _deadline.Token);
            UpdateService.VerifyInventory(Path.Combine(blockedRollback.InstallRoot, ".glpi-previous-" + blockedRollback.Id), blockedRollback.PreviousFiles, _deadline.Token);
        }
        finally { heldNew?.Dispose(); }
        Pass("Windows file locks abort replacement safely; if restoration is itself blocked, exact old binaries and the independent backup remain available with an explicit recovery-needed result.");
    }

    private void CheckHealthyReplacement()
    {
        var plan = Prepare("healthy-startup");
        try
        {
            var helper = LaunchRealHelper(plan); WaitForExit(helper, TimeSpan.FromSeconds(300));
            Require(helper.ExitCode == 0, "The real maintenance helper did not accept the rendered application window.");
            var applied = UpdateService.ReadPrepared(plan.TransactionPath);
            Require(applied.State == "installed", "The transaction was not marked installed.");
            UpdateService.VerifyInventory(plan.InstallRoot, plan.Files, _deadline.Token);
            UpdateService.VerifyInventory(plan.BackupRoot, plan.PreviousFiles, _deadline.Token);
            Require(File.ReadAllText(Path.Combine(plan.InstallRoot, "personal-export.csv")) == "Unrelated fixture file", "An unrelated root file changed.");
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.WorkRoot, "health.json")));
            Require(receipt.RootElement.GetProperty("Nonce").GetString() == plan.HealthNonce
                && receipt.RootElement.GetProperty("Version").GetString() == plan.Package.Version, "The real application's startup receipt is invalid.");
            using var app = Process.GetProcessById(receipt.RootElement.GetProperty("ProcessId").GetInt32());
            Require(IsOwned(app) && !app.HasExited, "The healthy application was not left running from its fixture installation.");
            Pass("The real EXE helper installs the verified pack, receives the bootstrap-and-first-paint WebView acknowledgement and retains a verified rollback copy.");
        }
        finally { StopOwnedProcesses(); }
    }

    private void CheckFailedStartupRollback()
    {
        var plan = Prepare("missing-health-receipt");
        // A directory at the receipt path denies the app's atomic file write. The
        // real new window can open, but cannot acknowledge: the helper must stop it.
        Directory.CreateDirectory(Path.Combine(plan.WorkRoot, "health.json"));
        try
        {
            var helper = LaunchRealHelper(plan); WaitForExit(helper, TimeSpan.FromSeconds(300));
            Require(helper.ExitCode == 1, "The missing receipt was incorrectly accepted.");
            var applied = UpdateService.ReadPrepared(plan.TransactionPath);
            Require(applied.State == "rolled-back", "The helper did not complete rollback: " + applied.State + ". " + UpdateService.ReadLastOutcome(Path.GetDirectoryName(plan.WorkRoot)!)?.Detail);
            UpdateService.VerifyInventory(plan.InstallRoot, plan.PreviousFiles, _deadline.Token);
            UpdateService.VerifyInventory(plan.BackupRoot, plan.PreviousFiles, _deadline.Token);
            Require(File.Exists(Path.Combine(plan.InstallRoot, "wad", "update-fixture.txt")), "A file unique to the original pack was not restored.");
            var restarted = FindOwnedApplication(plan.InstallRoot, TimeSpan.FromSeconds(20));
            using (restarted) WaitForWindow(restarted, TimeSpan.FromSeconds(30));
            Require(File.ReadAllText(Path.Combine(plan.InstallRoot, "personal-export.csv")) == "Unrelated fixture file", "An unrelated root file changed during rollback.");
            Pass("Missing startup acknowledgement stops the new process, restores every old file and reopens the previous fixture pack.");
        }
        finally { StopOwnedProcesses(); }
    }

    private PreparedUpdate Prepare(string name)
    {
        _deadline.Token.ThrowIfCancellationRequested();
        var root = Path.Combine(_sandbox, name); Directory.CreateDirectory(root);
        // ZIP extraction is confined to the newly-created fixture root. The
        // production preparation below then verifies the entire release manifest.
        using (var stream = File.OpenRead(_archive))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Require(archive.Entries.Count <= UpdateService.MaximumFiles + 5000, "Too many fixture archive entries.");
            long bytes = 0;
            foreach (var entry in archive.Entries)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                var directory = entry.FullName.EndsWith('/'); var relative = UpdateService.ValidRelativePath(directory ? entry.FullName.TrimEnd('/') : entry.FullName);
                Require(relative == "GLPI-Pilot.exe" || relative == "wad" || relative.StartsWith("wad/", StringComparison.Ordinal), "Unexpected fixture archive root.");
                Require((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) == 0 && ((entry.ExternalAttributes >> 16) & 0xf000) != 0xa000, "Fixture links are forbidden.");
                var destination = UpdateService.Combine(root, relative);
                if (directory) { Directory.CreateDirectory(destination); continue; }
                Require(entry.Length <= UpdateService.MaximumFileBytes && (bytes += entry.Length) <= UpdateService.MaximumPayloadBytes, "Fixture archive exceeds its size bound.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[65536]; long written = 0; int count;
                while ((count = input.Read(buffer)) > 0) { _deadline.Token.ThrowIfCancellationRequested(); Require((written += count) <= entry.Length, "Fixture entry exceeds its declared size."); output.Write(buffer, 0, count); }
                Require(written == entry.Length, "Fixture archive entry is truncated.");
            }
        }
        var metadataPath = Path.Combine(root, "wad", "version.json");
        var metadata = JsonSerializer.Deserialize<UpdatePackageMetadata>(File.ReadAllText(metadataPath)) ?? throw new InvalidDataException("No fixture version metadata.");
        Require(metadata.Edition == "PROD", "This harness expects the already-built PROD release pack.");
        var commit = metadata.SourceCommit;
        // Both executions use the actual current EXE. Only fixture metadata and a
        // fixture document differ, proving full-pack rollback without inventing a
        // previous production binary or claiming cross-version data migration.
        metadata.Version = "0.0.1"; UpdateService.WriteJson(metadataPath, metadata);
        File.WriteAllText(Path.Combine(root, "wad", "update-fixture.txt"), "Only the previous fixture pack contains this file.");
        var inventory = UpdateService.Inventory(root, _deadline.Token);
        File.WriteAllLines(Path.Combine(root, "wad", "SHA256SUMS.txt"), inventory.Where(item => item.Key != "wad/SHA256SUMS.txt").Select(item => item.Value + "  " + item.Key), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "personal-export.csv"), "Unrelated fixture file");
        var prepared = UpdateService.PrepareAsync(_archive, _archiveHash, commit, root, Path.Combine(_state, "Updates"), "0.0.1", new UpdatePolicy(), ct: _deadline.Token).GetAwaiter().GetResult();
        Require(prepared.Signature.Status is "Valid" or "NotSigned", "The Windows Authenticode verifier did not return an explicit result.");
        _plans.Add((name, prepared)); Log("PREPARED: " + name + "; signature=" + prepared.Signature.Status + "; files=" + prepared.Files.Count);
        return prepared;
    }

    private Process LaunchRealHelper(PreparedUpdate plan)
    {
        var executable = Path.Combine(plan.WorkRoot, "helper", "GLPI-Pilot.exe"); Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(Path.Combine(plan.InstallRoot, "GLPI-Pilot.exe"), executable, false);
        // A real, already-terminated process models the original app's orderly
        // exit. The helper still checks the saved PID and process creation time.
        using (var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/d", "/c", "exit", "0" } }) ?? throw new IOException("Unable to create the closed-parent fixture."))
        {
            plan.ParentProcessId = parent.Id; plan.ParentStartedUtcTicks = parent.StartTime.ToUniversalTime().Ticks;
            Require(parent.WaitForExit(5000), "The parent fixture did not close.");
        }
        UpdateService.WriteJson(plan.TransactionPath, plan);
        return Start(executable, "--apply-update", plan.TransactionPath);
    }

    private Process Start(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var temporary = Path.Combine(_sandbox, "temporary"); Directory.CreateDirectory(temporary);
        start.Environment["GLPI_PILOT_TEST_MODE"] = "1"; start.Environment["GLPI_PILOT_TEST_ROOT"] = _state;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(temporary, "bundles");
        var process = Process.Start(start) ?? throw new IOException("A fixture process did not start."); _started.Add(process); return process;
    }

    private void WaitForExit(Process process, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!process.WaitForExit(100)) { _deadline.Token.ThrowIfCancellationRequested(); Require(watch.Elapsed < timeout, "A fixture helper exceeded its process deadline."); }
    }
    private void WaitForWindow(Process process, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            _deadline.Token.ThrowIfCancellationRequested(); Require(!process.HasExited, "The packaged fixture process exited before opening its window.");
            process.Refresh(); if (process.MainWindowHandle != IntPtr.Zero) return; Thread.Sleep(100);
        }
        throw new TimeoutException("The packaged fixture did not open a visible window.");
    }
    private Process FindOwnedApplication(string install, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            foreach (var process in Process.GetProcessesByName("GLPI-Pilot"))
            {
                var selected = false;
                try { if (!process.HasExited && string.Equals(process.MainModule?.FileName, Path.Combine(install, "GLPI-Pilot.exe"), StringComparison.OrdinalIgnoreCase)) { selected = true; return process; } }
                catch (InvalidOperationException) { }
                finally { if (!selected) process.Dispose(); }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("The previous fixture application was not reopened after rollback.");
    }
    private bool IsOwned(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return path != null && (_createdSandbox && UpdateService.Within(path, _sandbox) || _createdState && UpdateService.Within(path, Path.Combine(_state, "Updates")));
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private void StopOwnedProcesses()
    {
        var watch = Stopwatch.StartNew();
        var owned = new Dictionary<int, (Process Process, bool Application)>();
        int Remaining(int maximum) => (int)Math.Clamp(40000 - watch.ElapsedMilliseconds, 0, maximum);
        void CaptureOwned()
        {
            foreach (var name in new[] { "GLPI-Pilot", "msedgewebview2", "node", "notification_helper", "platform_experiences_helper" })
                foreach (var process in Process.GetProcessesByName(name))
                {
                    var retained = false;
                    try
                    {
                        if (!IsOwned(process) || process.HasExited || owned.ContainsKey(process.Id)) continue;
                        // Retain the process handle before stopping its host: MainModule
                        // can become inaccessible while a descendant is still exiting.
                        _ = process.Handle;
                        owned.Add(process.Id, (process, name == "GLPI-Pilot")); retained = true;
                    }
                    catch (InvalidOperationException) { }
                    finally { if (!retained) process.Dispose(); }
                }
        }
        void Stop(Process process, bool closeWindow)
        {
            if (process.HasExited) return;
            if (closeWindow)
            {
                var requested = false;
                try { process.Refresh(); requested = process.CloseMainWindow(); }
                catch (InvalidOperationException) when (process.HasExited) { return; }
                if (requested && process.WaitForExit(Remaining(25000))) return;
            }
            else if (process.WaitForExit(Remaining(5000))) return;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            Require(process.WaitForExit(Remaining(5000)), "An owned fixture process did not stop within the cleanup deadline.");
        }
        try
        {
            CaptureOwned();
            // Stop a directly started helper first so it cannot launch another app.
            // A visible app gets its normal close path, which disposes its WebView.
            foreach (var process in _started.AsEnumerable().Reverse()) Stop(process, closeWindow: true);
            while (true)
            {
                CaptureOwned();
                var pending = owned.Values.Where(item => !item.Process.HasExited).OrderByDescending(item => item.Application).ToArray();
                if (pending.Length == 0) return;
                Require(watch.Elapsed < TimeSpan.FromSeconds(40), "Fixture processes remained after the cleanup deadline.");
                foreach (var item in pending) Stop(item.Process, item.Application);
            }
        }
        finally { foreach (var item in owned.Values) item.Process.Dispose(); }
    }

    private void CleanupCompletedPlans()
    {
        StopOwnedProcesses(); CopyDiagnostics();
        // Each fixed runtime is ~700 MB. Release each completed case before preparing
        // another; keep only the small diagnostic evidence in the output directory.
        foreach (var (_, plan) in _plans)
            foreach (var root in new[] { plan.InstallRoot, plan.WorkRoot })
            {
                if (!Directory.Exists(root)) continue;
                Require(UpdateService.Within(root, _sandbox) && root != _sandbox && root != _state,
                    "An update fixture plan escaped its owned temporary root.");
                UpdateService.EnsureNoLinks(root);
                File.WriteAllText(Path.Combine(root, "fixture-owner.txt"), _owner);
                DeleteOwnedDirectory(root);
            }
    }

    private void CopyDiagnostics()
    {
        if (_output.Length == 0) return;
        foreach (var (name, plan) in _plans)
        {
            var folder = Path.Combine(_output, name); Directory.CreateDirectory(folder);
            foreach (var file in new[] { "transaction.json", "outcome.json", "health.json" })
                if (File.Exists(Path.Combine(plan.WorkRoot, file))) File.Copy(Path.Combine(plan.WorkRoot, file), Path.Combine(folder, file), true);
        }
        if (_createdState && Directory.Exists(Path.Combine(_state, "Logs")))
        {
            var logs = Path.Combine(_output, "application-logs"); Directory.CreateDirectory(logs);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(_state, "Logs"), "*.log").Take(30)) File.Copy(file, Path.Combine(logs, Path.GetFileName(file)), true);
        }
    }
    private void DeleteOwnedDirectory(string root)
    {
        Require(_createdSandbox && (root == _sandbox || UpdateService.Within(root, _sandbox)), "Cleanup escaped its owned temporary root.");
        UpdateService.EnsureNoLinks(root);
        var marker = Path.Combine(root, "fixture-owner.txt");
        Require(File.Exists(marker) && new FileInfo(marker).Length == _owner.Length && File.ReadAllText(marker) == _owner, "Cleanup ownership marker was changed; the directory was left intact.");
        // Recursive deletion may remove the marker before encountering a locked
        // runtime DLL. Keep this validated ownership proof for this bounded call.
        var watch = Stopwatch.StartNew(); var retried = false;
        while (true)
        {
            UpdateService.EnsureNoLinks(root);
            if (!Directory.Exists(root)) return;
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
                foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(path); Require((attributes & FileAttributes.ReparsePoint) == 0, "An unexpected link prevents recursive fixture cleanup.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                }
            try
            {
                Directory.Delete(root, true);
                if (retried) Log("CLEANUP: owned runtime files released after " + watch.ElapsedMilliseconds + " ms.");
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && (error is IOException or UnauthorizedAccessException)
                && (error.HResult & 0xffff) is 5 or 32 or 33 && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (!retried) Log("CLEANUP: waiting up to 10 seconds for owned runtime files to be released (Windows " + (error.HResult & 0xffff) + ").");
                retried = true; Thread.Sleep((int)Math.Min(100, Math.Max(1, 10000 - watch.ElapsedMilliseconds)));
            }
        }
    }
    private void Pass(string message) { _passed++; Log("PASS: " + message); }
    private void Log(string message) { _messages.Add(message); Console.WriteLine(message); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
