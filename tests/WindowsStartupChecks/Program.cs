using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            var siteCsvPath = Path.Combine(folder, "wad", "sites.csv");
            if (File.Exists(siteCsvPath)) throw new InvalidOperationException("This fixture requires the generic pack without wad/sites.csv; it creates its own synthetic CSV only in the temporary installation.");
            var retainedSiteId = Guid.NewGuid().ToString("N");
            var initial = JsonSerializer.Serialize(new { PortalUrl = "https://glpi.example.invalid/front/ticket.php", Theme = "dark", InterfaceZoomPercent = 100,
                ExportFolder = Path.Combine(_sandbox, "exports"), Interface = new { OnboardingCompleted = true },
                Cities = new { Enabled = true, Sites = new[] { new { Id = retainedSiteId, Name = "Site fictif Nord", Aliases = new[] { "Repère local Nord" }, Address = "Adresse fictive conservée" } } },
                Automation = new { ScheduledExtraction = false, ResumeAfterReconnect = false }, History = new { RestoreLastOnStartup = false } });
            var settingsPath = Path.Combine(_state, "settings.json"); File.WriteAllText(settingsPath, initial, new UTF8Encoding(false));
            var initialHash = Digest(settingsPath); var archiveHash = Digest(_pack);
            using var playwright = await Playwright.CreateAsync();
            // First launch: inspect the complete navigation without saving preferences.
            var first = Start(Port()); var firstPort = int.Parse(first.StartInfo.ArgumentList[1]);
            await using (var connected = await ConnectAsync(playwright, first, firstPort))
            {
                var page = await ReadyPageAsync(connected, first);
                Check(Visible(first), "The actual packaged EXE owns a visible, nonzero Windows window after the WebView handshake.");
                var registeredRoutes = await NavigationRouteIdsAsync(page);
                Check(await page.Locator(".hero").CountAsync() == 1 && registeredRoutes.SequenceEqual(new[] { "home", "pilot", "records", "terrain-map", "data", "settings" }) && await page.Locator(".workspace-view,.nav-group-toggle").CountAsync() == 0, "The six redesigned workspaces load in the embedded fixed WebView2 runtime without the old screen inventory or local-view tabs.");
                Check(File.ReadAllText(Path.Combine(_state, "Logs", "demarrage-dernier.log")).Contains("[PRÊT]", StringComparison.Ordinal), "Startup readiness is recorded after bootstrap and first web paint.");
                await StopNormallyAsync(first);
            }
            Check(Digest(settingsPath) == initialHash, "Without the optional sites CSV, startup, read-only navigation inspection and shutdown preserve the exact synthetic settings bytes.");
            // Add only synthetic reference data to the extracted fixture copy.
            // This is the first launch on which the optional file is present.
            File.WriteAllText(siteCsvPath, """
                "Site";"Termes de détection"
                "SITE FICTIF NORD";"Annexe fictive Nord|Entrée fictive Nord"
                "Site fictif Sud";"Annexe fictive Sud"
                """, new UTF8Encoding(true));
            var siteImportHash = Digest(siteCsvPath);
            // Second launch: initialize the reference, use the real controls and persist an explicit zoom.
            var second = Start(Port()); var secondPort = int.Parse(second.StartInfo.ArgumentList[1]);
            await using (var connected = await ConnectAsync(playwright, second, secondPort))
            {
                var page = await ReadyPageAsync(connected, second); var errors = new List<string>(); page.PageError += (_, message) => errors.Add(message);
                await NavigateAsync(page, "terrain-reference");
                await page.GetByLabel("Type", new() { Exact = true }).SelectOptionAsync("site");
                await page.GetByRole(AriaRole.Cell, new() { Name = "Site fictif Sud", Exact = true }).WaitForAsync();
                using (var seeded = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                {
                    var sites = seeded.RootElement.GetProperty("Cities").GetProperty("Sites").EnumerateArray().ToArray();
                    var retained = sites.Single(site => site.GetProperty("Id").GetString() == retainedSiteId);
                    Check(seeded.RootElement.GetProperty("SiteReferenceImportHash").GetString() == siteImportHash && sites.Length == 2
                        && retained.GetProperty("Name").GetString() == "Site fictif Nord" && retained.GetProperty("Address").GetString() == "Adresse fictive conservée"
                        && retained.GetProperty("Aliases").EnumerateArray().Select(alias => alias.GetString()).SequenceEqual(new[] { "Repère local Nord", "Annexe fictive Nord", "Entrée fictive Nord" })
                        && sites.Single(site => site.GetProperty("Name").GetString() == "Site fictif Sud").GetProperty("Aliases")[0].GetString() == "Annexe fictive Sud"
                        && await page.GetByRole(AriaRole.Cell, new() { Name = "Site fictif Nord", Exact = true }).IsVisibleAsync(),
                        "The real startup merges the optional synthetic CSV, persists its hash and aliases, preserves the existing site, and exposes both sites in the interface.");
                }
                var width = await page.EvaluateAsync<double>("innerWidth");
                await NavigateAsync(page, "settings");
                await page.GetByLabel("Zoom (%)", new() { Exact = true }).FillAsync("125");
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Thème", Exact = true }).SelectOptionAsync("light");
                await page.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync();
                await WaitForPageAsync(page, "document.documentElement.dataset.theme === 'light' && document.querySelector('#zoom-label').innerText.includes('125')");
                Check(await page.EvaluateAsync<double>("innerWidth") < width * .9, "The interface zoom changes native WebView2 scaling rather than just a displayed number.");
                await page.Keyboard.PressAsync("Control+0"); await WaitForPageAsync(page, "document.querySelector('#zoom-label').innerText.includes('100')");
                Check(Math.Abs(await page.EvaluateAsync<double>("innerWidth") - width) <= 2, "Ctrl 0 restores the actual web viewport to 100 percent.");
                await NavigateAsync(page, "home");
                await page.GetByRole(AriaRole.Button, new() { Name = "Simulation avec données fictives", Exact = true }).ClickAsync();
                await page.Locator(".page-content[data-page=pilot]").WaitForAsync();
                await WaitForPageAsync(page, "!document.querySelector('#content').hasAttribute('aria-busy')");
                Check(await page.Locator(".page-content > .notice.danger[role=alert]").CountAsync() == 0, "The native pack computes and renders the real C# dashboard from synthetic records.");
                await page.ScreenshotAsync(new() { Path = Path.Combine(_output, "dashboard-light.png"), FullPage = true });
                await NavigateAsync(page, "settings");
                foreach (var label in new[] { "Assistant de langage", "Lectures ciblées", "Effectifs des sites" })
                    await page.GetByRole(AriaRole.Checkbox, new() { Name = label, Exact = true }).CheckAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Appliquer les fonctions visibles", Exact = true }).ClickAsync();
                await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
                var routes = (await NavigationRouteIdsAsync(page)).Concat(new[] { "queries", "history", "technicians", "planning", "assistant", "sources", "collect", "library", "quality", "mapping", "enrichment", "terrain-reference", "terrain-review", "pilot-sites", "populations", "tours", "automation", "updates", "help", "operations", "pilot-settings" }).Distinct().ToArray();
                foreach (var route in routes)
                {
                    await NavigateAsync(page, route);
                    if (await page.Locator(".page-content > .notice.danger[role=alert],dialog[data-workspace-panel] .notice.danger[role=alert]").CountAsync() > 0) throw new InvalidOperationException("Screen failure: " + route + " · " + await page.Locator(".page-content").InnerTextAsync());
                }
                Check(true, "All six workspaces and every contextual analysis, source, library, ticket-data, terrain and configuration tool open inside the real Windows WebView2 host.");
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
            // Emulate an already-saved reference edit while the app is closed.
            // Keep the same temporary profile and import receipt for the next process.
            var edited = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            var editedSites = edited["Cities"]!["Sites"]!.AsArray();
            var editedNorth = editedSites.Single(site => site!["Id"]!.GetValue<string>() == retainedSiteId)!;
            editedNorth["Name"] = "Site fictif Nord modifié"; editedNorth["Aliases"] = new JsonArray("Repère local modifié");
            editedSites.Remove(editedSites.Single(site => site!["Name"]!.GetValue<string>() == "Site fictif Sud"));
            File.WriteAllText(settingsPath, edited.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            var editedHash = Digest(settingsPath);
            // Third launch proves zoom restoration and that initialization does not undo local edits or deletions.
            var third = Start(Port()); var thirdPort = int.Parse(third.StartInfo.ArgumentList[1]);
            await using (var connected = await ConnectAsync(playwright, third, thirdPort))
            {
                var page = await ReadyPageAsync(connected, third);
                Check((await page.Locator("#zoom-label").InnerTextAsync()).Contains("110", StringComparison.Ordinal), "The stored 110 percent zoom is restored in a new Windows process.");
                await NavigateAsync(page, "terrain-reference");
                await page.GetByLabel("Type", new() { Exact = true }).SelectOptionAsync("site");
                await page.GetByRole(AriaRole.Cell, new() { Name = "Site fictif Nord modifié", Exact = true }).WaitForAsync();
                using (var restarted = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                    Check(Digest(settingsPath) == editedHash && restarted.RootElement.GetProperty("SiteReferenceImportHash").GetString() == siteImportHash
                        && restarted.RootElement.GetProperty("Cities").GetProperty("Sites").GetArrayLength() == 1
                        && await page.GetByRole(AriaRole.Cell, new() { Name = "Site fictif Sud", Exact = true }).CountAsync() == 0,
                        "A new native process reuses the same profile and preserves the renamed site, changed aliases and deleted site without reimporting the still-present CSV.");
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
            try
            {
                if (_state.Length > 0 && Directory.Exists(Path.Combine(_state, "Logs")))
                    foreach (var file in Directory.GetFiles(Path.Combine(_state, "Logs"), "*.log")) File.Copy(file, Path.Combine(_output, Path.GetFileName(file)), true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Log("Cleanup diagnostics: " + error.Message); success = false; }
            try
            {
                if (_sandbox.Length > 0 && File.Exists(Path.Combine(_sandbox, "fixture-owner.txt")) && File.ReadAllText(Path.Combine(_sandbox, "fixture-owner.txt")) == _owner)
                    await DeleteOwnedSandboxAsync();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Log("Cleanup: " + error.Message); success = false; }
            try { if (_output.Length > 0) File.WriteAllLines(Path.Combine(_output, "native-webview.log"), _log, new UTF8Encoding(false)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Log("Cleanup evidence: " + error.Message); success = false; }
            _deadline.Dispose();
        }
        if (success) Console.WriteLine($"{_passed}/{_passed} native Windows WebView checks passed; isolated profile, no GLPI session accessed.");
        return success ? 0 : 1;
    }
    private void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _passed++; Log("PASS: " + message); }
    private void Log(string message) { _log.Add(message); Console.WriteLine(message); }
    private async Task DeleteOwnedSandboxAsync()
    {
        // The caller has verified this fixture's owner marker. Recursive deletion
        // may remove it before a runtime DLL is released; retain that proof only
        // for this bounded call and never target another installation or profile.
        var watch = Stopwatch.StartNew(); var retried = false;
        while (Directory.Exists(_sandbox))
        {
            try
            {
                Directory.Delete(_sandbox, true);
                if (retried) Log("Cleanup: owned runtime files released after " + watch.ElapsedMilliseconds + " ms.");
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && (error is IOException or UnauthorizedAccessException)
                && (error.HResult & 0xffff) is 5 or 32 or 33 && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (!retried) Log("Cleanup: waiting up to 10 seconds for owned runtime files to be released (Windows " + (error.HResult & 0xffff) + ").");
                retried = true; await Task.Delay((int)Math.Min(100, Math.Max(1, 10000 - watch.ElapsedMilliseconds)));
            }
        }
    }
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
    private static async Task<string[]> NavigationRouteIdsAsync(IPage page)
    {
        if (!await page.Locator(".sidebar").IsVisibleAsync()) await page.GetByRole(AriaRole.Button, new() { Name = "Navigation", Exact = true }).ClickAsync();
        await page.Locator(".sidebar .nav-customize").ClickAsync();
        var editor = page.Locator("dialog.navigation-modal"); await editor.WaitForAsync();
        var routes = await editor.Locator("[data-nav-page]").EvaluateAllAsync<string[]>("nodes=>nodes.map(node=>node.dataset.navPage)");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        return routes;
    }

    private static async Task NavigateAsync(IPage page, string route)
    {
        var panels = page.Locator("dialog[open][data-workspace-panel]");
        while (await panels.CountAsync() > 0)
        {
            var panel = panels.Last;
            await panel.GetByRole(AriaRole.Button, new() { Name = "Fermer", Exact = true }).First.ClickAsync();
            await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
        var target = await page.EvaluateAsync<JsonElement>("async route=>(await import('/js/navigation.js')).navigationTarget(route)", route);
        var id = target.GetProperty("id").GetString()!;
        if (!await page.Locator(".sidebar").IsVisibleAsync()) await page.GetByRole(AriaRole.Button, new() { Name = "Navigation", Exact = true }).ClickAsync();
        await page.Locator($".nav-item[data-page='{id}']").ClickAsync();
        await WaitForPageAsync(page, "id=>document.querySelector('.page-content')?.dataset.page===id&&!document.querySelector('#content').hasAttribute('aria-busy')", id);
        if (target.GetProperty("params").TryGetProperty("panel", out var panelValue) && panelValue.GetString() is { } panelName && panelName != "compositions")
        {
            var control = page.Locator($".page-content [data-open-panel='{panelName}']").First;
            if (await control.CountAsync() == 0) throw new InvalidOperationException("Missing contextual action: " + route + " in " + id);
            var details = control.Locator("xpath=ancestor::details");
            for (var index = 0; index < await details.CountAsync(); index++)
                if (await details.Nth(index).GetAttributeAsync("open") is null) await details.Nth(index).Locator(":scope > summary").ClickAsync();
            await control.ClickAsync();
            if (id != "home") await page.Locator($"dialog[open][data-workspace-panel='{panelName}']").WaitForAsync();
        }
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
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
