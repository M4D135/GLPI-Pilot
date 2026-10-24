using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

// Drives the real self-contained WinForms/WebView2 executable through its guarded,
// loopback-only test port. No UI mock, GLPI access or existing user state is used.
return await new WindowsWebViewFixture().RunAsync(args);

internal sealed class WindowsWebViewFixture
{
    private string _sandbox = "", _state = "", _pack = "", _output = "", _executable = "";
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly List<Process> _owned = new();
    private readonly List<string> _log = new();
    private static readonly string[] RuntimeProcessNames = { "msedgewebview2", "node", "notification_helper", "platform_experiences_helper" };
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromMinutes(5));
    private int _passed;

    public async Task<int> RunAsync(string[] args)
    {
        var success = false;
        try
        {
            if (args.Length != 4 || args[0] != "--pack" || args[2] != "--out") throw new ArgumentException("WindowsStartupChecks --pack <PROD.zip> --out <evidence-directory>");
            if (!OperatingSystem.IsWindows() || !Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0) throw new InvalidOperationException("NOT RUN: an interactive Windows desktop is required.");
            if (Process.GetProcessesByName("GLPI-Pilot").Any(p => { using (p) return !p.HasExited; })) throw new InvalidOperationException("An existing GLPI Pilot session prevents this isolated test.");
            _pack = Path.GetFullPath(args[1]); if (!File.Exists(_pack)) throw new FileNotFoundException("The production ZIP is missing.");
            _output = Path.GetFullPath(args[3]); Directory.CreateDirectory(_output);
            _sandbox = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Native-" + _owner); Directory.CreateDirectory(_sandbox);
            File.WriteAllText(Path.Combine(_sandbox, "fixture-owner.txt"), _owner);
            _state = Path.Combine(_sandbox, "GLPI-Pilot-Checks-State"); Directory.CreateDirectory(_state);
            var folder = Path.Combine(_sandbox, "pack"); ZipFile.ExtractToDirectory(_pack, folder); _executable = Path.Combine(folder, "GLPI-Pilot.exe");
            if (!File.Exists(Path.Combine(folder, "wad", "runtime", "webview2", "msedgewebview2.exe"))) throw new FileNotFoundException("The fixed WebView2 runtime is not in the pack.");
            var initial = JsonSerializer.Serialize(new { PortalUrl = "https://glpi.example.invalid/front/ticket.php", Theme = "dark", InterfaceZoomPercent = 100,
                ExportFolder = Path.Combine(_sandbox, "exports"), Interface = new { OnboardingCompleted = true },
                Automation = new { ScheduledExtraction = false, ResumeAfterReconnect = false }, History = new { RestoreLastOnStartup = false } });
            var settingsPath = Path.Combine(_state, "settings.json"); File.WriteAllText(settingsPath, initial, new UTF8Encoding(false));
            var initialHash = Digest(settingsPath); var archiveHash = Digest(_pack);
            using var playwright = await Playwright.CreateAsync();
            // First launch: no interaction, exact preference preservation.
            var first = Start(Port()); var firstPort = int.Parse(first.StartInfo.ArgumentList[1]);
            await using (var connected = await ConnectAsync(playwright, first, firstPort))
            {
                var page = await ReadyPageAsync(connected, first);
                Check(Visible(first), "The actual packaged EXE owns a visible, nonzero Windows window after the WebView handshake.");
                Check(await page.Locator(".hero").CountAsync() == 1 && await page.Locator(".nav-item").CountAsync() >= 20, "All local modules load in the embedded fixed WebView2 runtime.");
                Check(File.ReadAllText(Path.Combine(_state, "Logs", "demarrage-dernier.log")).Contains("[PRÊT]", StringComparison.Ordinal), "Startup readiness is recorded after bootstrap and first web paint.");
                await StopNormallyAsync(first);
            }
            Check(Digest(settingsPath) == initialHash, "An untouched startup and shutdown preserve the exact synthetic settings bytes.");
            // Second launch: use the real controls and persist an explicit zoom.
            var second = Start(Port()); var secondPort = int.Parse(second.StartInfo.ArgumentList[1]);
            await using (var connected = await ConnectAsync(playwright, second, secondPort))
            {
                var page = await ReadyPageAsync(connected, second); var errors = new List<string>(); page.PageError += (_, message) => errors.Add(message);
                var width = await page.EvaluateAsync<double>("innerWidth");
                await NavigateAsync(page, "settings");
                await page.GetByLabel("Zoom de l’interface (%)", new() { Exact = true }).FillAsync("125");
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Thème", Exact = true }).SelectOptionAsync("light");
                await page.GetByRole(AriaRole.Button, new() { Name = "Appliquer l’apparence", Exact = true }).ClickAsync();
                await WaitForPageAsync(page, "document.documentElement.dataset.theme === 'light' && document.querySelector('#zoom-label').innerText.includes('125')");
                Check(await page.EvaluateAsync<double>("innerWidth") < width * .9, "The interface zoom changes native WebView2 scaling rather than just a displayed number.");
                await page.Keyboard.PressAsync("Control+0"); await WaitForPageAsync(page, "document.querySelector('#zoom-label').innerText.includes('100')");
                Check(Math.Abs(await page.EvaluateAsync<double>("innerWidth") - width) <= 2, "Ctrl 0 restores the actual web viewport to 100 percent.");
                await NavigateAsync(page, "home");
                await page.GetByRole(AriaRole.Button, new() { Name = "Simulation avec données fictives", Exact = true }).ClickAsync();
                await page.Locator(".page-content[data-page=pilot-weather]").WaitForAsync();
                await WaitForPageAsync(page, "!document.querySelector('#content').hasAttribute('aria-busy')");
                Check(await page.Locator(".page-content > .notice.danger[role=alert]").CountAsync() == 0, "The native pack computes and renders the real C# dashboard from synthetic records.");
                await page.ScreenshotAsync(new() { Path = Path.Combine(_output, "dashboard-light.png"), FullPage = true });
                var routes = await page.Locator(".nav-item").EvaluateAllAsync<string[]>("nodes => nodes.map(node => node.dataset.page)");
                foreach (var route in routes)
                {
                    await NavigateAsync(page, route);
                    if (await page.Locator(".page-content > .notice.danger[role=alert]").CountAsync() > 0) throw new InvalidOperationException("Screen failure: " + route + " · " + await page.Locator(".page-content").InnerTextAsync());
                }
                Check(true, "Every feature screen opens inside the real Windows WebView2 host.");
                await NavigateAsync(page, "home");
                await page.GetByRole(AriaRole.Button, new() { Name = "Changer de thème", Exact = true }).ClickAsync();
                await WaitForPageAsync(page, "document.documentElement.dataset.theme === 'dark'");
                await page.ScreenshotAsync(new() { Path = Path.Combine(_output, "home-dark.png"), FullPage = true });
                await page.GetByRole(AriaRole.Button, new() { Name = "Quitter la simulation", Exact = true }).ClickAsync();
                await WaitForPageAsync(page, "document.querySelector('.local-test-banner').hidden && document.documentElement.dataset.theme === 'light'");
                Check((await page.Locator("#zoom-label").InnerTextAsync()).Contains("100", StringComparison.Ordinal),
                    "Leaving the native simulation restores the original appearance before further preferences are persisted.");
                Native.ShowWindow(second.MainWindowHandle, 6);
                var secondary = Start(secondPort);
                await secondary.WaitForExitAsync(_deadline.Token).WaitAsync(TimeSpan.FromSeconds(12));
                Check(secondary.ExitCode == 0, "A second launch signals the existing instance and exits successfully.");
                await Until(() => !Native.IsIconic(second.MainWindowHandle));
                Check(Visible(second), "The first window is restored after the same-user activation signal.");
                Native.MoveWindow(second.MainWindowHandle, 20, 20, 1000, 740, true);
                await Task.Delay(250, _deadline.Token);
                Check(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "The native window remains usable at compact dimensions.");
                await page.Keyboard.PressAsync("Control+="); await WaitForPageAsync(page, "document.querySelector('#zoom-label').innerText.includes('105')"); await page.Keyboard.PressAsync("Control+=");
                await WaitForPageAsync(page, "document.querySelector('#zoom-label').innerText.includes('110')");
                Check(errors.Count == 0, "Native screen navigation and controls produce no unhandled JavaScript errors.");
                await StopNormallyAsync(second);
            }
            using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                Check(saved.RootElement.GetProperty("InterfaceZoomPercent").GetInt32() == 110 && saved.RootElement.GetProperty("PortalUrl").GetString() == "https://glpi.example.invalid/front/ticket.php", "User zoom persists while the configured source is preserved.");
            // Third launch proves persisted zoom is applied by the real host.
            var third = Start(Port()); var thirdPort = int.Parse(third.StartInfo.ArgumentList[1]);
            await using (var connected = await ConnectAsync(playwright, third, thirdPort))
            {
                var page = await ReadyPageAsync(connected, third);
                Check((await page.Locator("#zoom-label").InnerTextAsync()).Contains("110", StringComparison.Ordinal), "The stored 110 percent zoom is restored in a new Windows process.");
                await StopNormallyAsync(third);
            }
            await CheckRuntimeCrashAsync(playwright, settingsPath, browser: false);
            await CheckRuntimeCrashAsync(playwright, settingsPath, browser: true);
            Check(Digest(_pack) == archiveHash, "The original validated production ZIP remains unchanged.");
            success = true;
        }
        catch (Exception error) { Log("FAIL: " + error); }
        finally
        {
            foreach (var process in _owned)
                try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } } catch (Exception error) { Log("Cleanup: " + error.Message); success = false; }
            // The WebView browser can outlive an already-exited host briefly. Stop only
            // children whose executable belongs to this marked fixture installation.
            foreach (var name in RuntimeProcessNames)
            foreach (var child in Process.GetProcessesByName(name))
                using (child)
                    try
                    {
                        if (IsOwnedRuntime(child))
                        { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (Exception error) { Log("Cleanup: " + error.Message); success = false; }
            foreach (var process in _owned) process.Dispose();
            if (_state.Length > 0 && Directory.Exists(Path.Combine(_state, "Logs")))
                foreach (var file in Directory.GetFiles(Path.Combine(_state, "Logs"), "*.log")) File.Copy(file, Path.Combine(_output, Path.GetFileName(file)), true);
            if (_sandbox.Length > 0 && File.Exists(Path.Combine(_sandbox, "fixture-owner.txt")) && File.ReadAllText(Path.Combine(_sandbox, "fixture-owner.txt")) == _owner)
            { try { Directory.Delete(_sandbox, true); } catch (IOException error) { Log("Cleanup: " + error.Message); success = false; } }
            if (_output.Length > 0) File.WriteAllLines(Path.Combine(_output, "native-webview.log"), _log, new UTF8Encoding(false));
            _deadline.Dispose();
        }
        if (success) Console.WriteLine($"{_passed}/{_passed} native Windows WebView checks passed; isolated profile, no GLPI session accessed.");
        return success ? 0 : 1;
    }
    private void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _passed++; Log("PASS: " + message); }
    private void Log(string message) { _log.Add(message); Console.WriteLine(message); }
    private Process Start(int port)
    {
        var start = new ProcessStartInfo(_executable) { WorkingDirectory = Path.GetDirectoryName(_executable)!, UseShellExecute = false };
        start.ArgumentList.Add("--test-port"); start.ArgumentList.Add(port.ToString());
        start.Environment["GLPI_PILOT_TEST_MODE"] = "1"; start.Environment["GLPI_PILOT_TEST_ROOT"] = _state;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(_sandbox, "bundle");
        var process = Process.Start(start) ?? throw new IOException("The packed application could not start."); _owned.Add(process); return process;
    }
    private async Task<IBrowser> ConnectAsync(IPlaywright playwright, Process process, int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }; var endpoint = "http://127.0.0.1:" + port;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested(); process.Refresh(); if (process.HasExited) throw new InvalidOperationException("The app exited before its debugging endpoint became available: " + process.ExitCode);
            try { using var response = await client.GetAsync(endpoint + "/json/version", timeout.Token); if (response.IsSuccessStatusCode) break; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!timeout.IsCancellationRequested) { }
            await Task.Delay(200, timeout.Token);
        }
        return await playwright.Chromium.ConnectOverCDPAsync(endpoint, new() { Timeout = 15000 });
    }
    private async Task<IPage> ReadyPageAsync(IBrowser browser, Process process)
    {
        IPage? page = null;
        await Until(() => { page = browser.Contexts.SelectMany(context => context.Pages).FirstOrDefault(p => p.Url.StartsWith("https://glpi-pilot.local/", StringComparison.Ordinal)); return page is not null; });
        page!.SetDefaultTimeout(20000); await page.Locator("body[data-ready=true]").WaitForAsync(new() { Timeout = 60000 }); process.Refresh(); return page;
    }
    private async Task CheckRuntimeCrashAsync(IPlaywright playwright, string settingsPath, bool browser)
    {
        var settingsHash = Digest(settingsPath); var port = Port(); var process = Start(port);
        await using var connected = await ConnectAsync(playwright, process, port);
        var page = await ReadyPageAsync(connected, process);
        var session = browser ? await connected.NewBrowserCDPSessionAsync() : await page.Context.NewCDPSessionAsync(page);
        var kind = browser ? "BrowserProcessExited" : "RenderProcessExited";
        // The documented CDP commands crash only this fixture's browser or
        // renderer. A closed channel is expected; the native event is the proof.
        var crash = session.SendAsync(browser ? "Browser.crash" : "Page.crash");
        try { await crash.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (PlaywrightException) { }
        catch (TimeoutException)
        { _ = crash.ContinueWith(static failed => { _ = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        await Until(() =>
        {
            process.Refresh();
            return !process.HasExited && process.MainWindowTitle == "GLPI Pilot — affichage interrompu"
                && Directory.EnumerateFiles(Path.Combine(_state, "Logs"), "*.log")
                    .Any(path => File.ReadAllText(path).Contains("Incident du moteur d’affichage : " + kind, StringComparison.Ordinal));
        });
        Check(Visible(process), kind + " leaves a visible native diagnostic after the real embedded process crashes.");
        await StopNormallyAsync(process, kind + " permits a standard bounded native close without awaiting the dead page.");
        Check(Digest(settingsPath) == settingsHash, kind + " preserves the exact saved synthetic settings.");
    }
    private async Task StopNormallyAsync(Process process, string message = "The native close handshake flushes the page and exits with code zero.")
    {
        process.Refresh(); if (!process.CloseMainWindow()) throw new InvalidOperationException("The window refused a standard close request.");
        await process.WaitForExitAsync(_deadline.Token).WaitAsync(TimeSpan.FromSeconds(25));
        // A disposed WebView control can return before its browser finishes
        // releasing the profile. The next launch must get its own CDP port.
        await WaitForRuntimeExitAsync();
        Check(process.ExitCode == 0, message);
    }
    private bool IsOwnedRuntime(Process process)
    {
        try
        {
            if (_sandbox.Length == 0 || process.HasExited) return false;
            var path = process.MainModule?.FileName;
            return path is not null && path.StartsWith(_sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private async Task WaitForRuntimeExitAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var active = false;
            foreach (var name in RuntimeProcessNames)
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) active |= IsOwnedRuntime(process);
            if (!active) return;
            await Task.Delay(100, timeout.Token);
        }
    }
    private static async Task NavigateAsync(IPage page, string route)
    {
        // A small VM desktop or higher Windows DPI can collapse the sidebar.
        // Use its real opening control before selecting the requested screen.
        if (!await page.Locator(".sidebar").IsVisibleAsync())
            await page.GetByRole(AriaRole.Button, new() { Name = "Navigation", Exact = true }).ClickAsync();
        await page.Locator($".nav-item[data-page='{route}']").ClickAsync();
        await WaitForPageAsync(page, "id => !!document.querySelector('.page-content[data-page=\"' + id + '\"]') && !document.querySelector('#content').hasAttribute('aria-busy')", route);
    }
    // Poll from the test process: WaitForFunction injects eval, forbidden by the real app CSP.
    private static async Task WaitForPageAsync(IPage page, string expression, object? argument = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await page.EvaluateAsync<bool>(expression, argument)) await Task.Delay(50, timeout.Token);
    }
    private async Task Until(Func<bool> predicate) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token); timeout.CancelAfter(TimeSpan.FromSeconds(12)); while (!predicate()) await Task.Delay(100, timeout.Token); }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static string Digest(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool Visible(Process process) { process.Refresh(); return process.MainWindowHandle != IntPtr.Zero && Native.IsWindowVisible(process.MainWindowHandle) && Native.GetWindowRect(process.MainWindowHandle, out var rectangle) && rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top; }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
    }
}
