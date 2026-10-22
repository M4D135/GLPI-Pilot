using System.Diagnostics;
using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace GlpiPilot.Desktop;

internal sealed class MainForm : Form, IPilotHost
{
    internal const string WebHost = "glpi-pilot.local";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly PilotApplication _application;
    private readonly WebBridge _bridge;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private readonly NotifyIcon _notification = new();
    private readonly bool _backupPrepared;
    private readonly string[] _args;
    private bool _closing, _closed, _tick, _ready, _bootstrapped, _navigated, _placementChanged, _closeRequested, _closeReady;
    private string _closeToken = "";
    public int ExitCode { get; private set; }
    private static readonly string[] HostOperations = { "app.ready", "app.closeReady", "host.screens", "host.screen", "host.editor" };

    public MainForm(string[] args, bool backupPrepared)
    {
        _args = args; _backupPrepared = backupPrepared;
        Text = "GLPI Pilot"; MinimumSize = new Size(800, 560); Size = new Size(1440, 940);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(13, 22, 37);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        _notification.Icon = Icon; _notification.Text = "GLPI Pilot";
        Controls.Add(_web);
        _application = new PilotApplication(this, Publish);
        _bridge = new WebBridge(_application.Operations.Concat(HostOperations), ExecuteAsync, Post);
        _notification.BalloonTipClicked += (_, _) => { ActivateExisting(); _bridge.Publish("app.navigate", new { page = "day" }); };
        RestorePlacement();
        Load += async (_, _) => await InitializeAsync();
        Move += (_, _) => { if (_ready) _placementChanged = true; };
        ResizeEnd += (_, _) => { if (_ready) _placementChanged = true; };
        Resize += (_, _) => { if (_ready) _placementChanged = true; };
        FormClosing += ClosingAsync;
        _timer.Tick += async (_, _) =>
        {
            if (_tick || _closing || !_ready) return;
            _tick = true;
            try { await Task.Run(() => _application.TickAsync(_lifetime.Token)); }
            catch (OperationCanceledException) { }
            catch (Exception error) { AppPaths.AppendLog("Planification : " + AppPaths.SafeError(error)); }
            finally { _tick = false; }
        };
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "wad", "web");
            if (!File.Exists(Path.Combine(root, "index.html"))) throw new FileNotFoundException("Le dossier de l’interface wad\\web manque dans ce pack.");
            var runtime = Path.Combine(AppContext.BaseDirectory, "wad", "runtime", "webview2");
            if (!File.Exists(Path.Combine(runtime, "msedgewebview2.exe"))) throw new FileNotFoundException("Le runtime WebView2 fourni dans wad\\runtime\\webview2 manque. Extrayez à nouveau le pack complet.");
            foreach (var name in new[] { "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER" })
                Environment.SetEnvironmentVariable(name, null);
            var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false };
            var test = AppPaths.TestDataRoot is not null;
            if (test)
            {
                var index = Array.IndexOf(_args, "--test-port");
                if (index >= 0 && index + 1 < _args.Length && int.TryParse(_args[index + 1], out var port) && port is >= 1024 and <= 65535)
                    options.AdditionalBrowserArguments = "--remote-debugging-address=127.0.0.1 --remote-debugging-port=" + port;
            }
            StartupDiagnostics.Step("Initialisation du moteur WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(runtime,
                Path.Combine(AppPaths.DataRoot, "WebView2"), options).WaitAsync(TimeSpan.FromSeconds(60));
            await _web.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(60));
            var core = _web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = test;
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsWebMessageEnabled = true; core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsReputationCheckingRequired = false;
            core.SetVirtualHostNameToFolderMapping(WebHost, root, CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) => { if (!IsLocal(e.Request.Uri)) e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", ""); };
            core.NavigationStarting += (_, e) => { if (!IsLocal(e.Uri)) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
            core.WebMessageReceived += (_, e) => { if (!_closing && IsLocal(e.Source)) _bridge.Receive(e.WebMessageAsJson); };
            core.NavigationCompleted += (_, e) => { _navigated = e.IsSuccess; if (!e.IsSuccess) ReportError(new IOException("Chargement de l’interface interrompu : " + e.WebErrorStatus)); };
            core.ProcessFailed += (_, e) => ReportError(new IOException("Le moteur d’affichage a été interrompu : " + e.ProcessFailedKind));
            _web.ZoomFactor = _application.Settings.InterfaceZoomPercent / 100d;
            StartupDiagnostics.Step("Chargement des modules de l’interface locale");
            core.Navigate("https://" + WebHost + "/index.html");
            _timer.Start();
            _ = MonitorReadyAsync();
        }
        catch (Exception error) { ExitCode = 1; Program.ReportStartupFailure(error); Close(); }
    }
    private async Task MonitorReadyAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), _lifetime.Token); if (!_ready && !_closing) { ExitCode = 1; Program.ReportStartupFailure(new TimeoutException("L’interface n’a pas confirmé son premier affichage.")); Close(); } }
        catch (OperationCanceledException) { }
    }
    internal static bool IsLocal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == WebHost && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    private async Task<object?> ExecuteAsync(string op, JsonElement args, CancellationToken ct)
    {
        switch (op)
        {
            case "app.ready":
                return await UiAsync(() =>
                {
                    if (!_bootstrapped || !_navigated) throw new InvalidOperationException("L’interface n’est pas encore initialisée.");
                    if (!_ready)
                    {
                        _ready = true; StartupDiagnostics.MarkReady();
                        if (_backupPrepared) try { StartupBackupService.MarkReady(AppPaths.DataRoot, AppPaths.ApplicationVersion); } catch (Exception error) { AppPaths.AppendLog("Repère de version : " + AppPaths.SafeError(error)); }
                        UpdateService.AcknowledgeStartup();
                    }
                    return (object?)new { ready = true, engine = _web.CoreWebView2.Environment.BrowserVersionString };
                });
            case "app.closeReady":
                var closingToken = args.GetProperty("token").GetString(); var allow = args.GetProperty("allow").GetBoolean();
                return await UiAsync(() =>
                {
                    if (!_closeRequested || closingToken != _closeToken) throw new InvalidOperationException("Cette demande de fermeture a expiré.");
                    if (allow) { _closeReady = true; BeginInvoke(Close); } else { _closeRequested = false; _closeToken = ""; }
                    return (object?)new { closing = allow };
                });
            case "host.screens": return await UiAsync(() => (object?)Screen.AllScreens.Select(s => new { id = s.DeviceName, primary = s.Primary, width = s.WorkingArea.Width, height = s.WorkingArea.Height }).ToArray());
            case "host.screen":
                var id = args.GetProperty("id").GetString();
                return await UiAsync(() => { var screen = Screen.AllScreens.SingleOrDefault(s => s.DeviceName == id) ?? throw new InvalidDataException("Écran indisponible."); WindowState = FormWindowState.Normal; Bounds = Fit(Bounds, screen.WorkingArea); _placementChanged = true; return (object?)new { moved = true }; });
            case "host.editor": _application.EditorOpen = args.GetProperty("open").GetBoolean(); return new { updated = true };
            default:
                var result = await _application.ExecuteAsync(op, args, ct).ConfigureAwait(false);
                if (op == "app.bootstrap") _bootstrapped = true;
                return result;
        }
    }
    private void Publish(string name, object? data)
    {
        _bridge?.Publish(name, data);
        if (name == "settings.changed") _ = UiAsync(() => { if (_web.CoreWebView2 is not null) _web.ZoomFactor = _application.Settings.InterfaceZoomPercent / 100d; return true; });
    }
    private void Post(string json)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!_closing && _web.CoreWebView2 is not null) _web.CoreWebView2.PostWebMessageAsJson(json); }); }
        catch (InvalidOperationException) when (_closing || IsDisposed) { }
    }
    private Task<T> UiAsync<T>(Func<T> action)
    {
        if (_closed || IsDisposed) return Task.FromException<T>(new ObjectDisposedException(nameof(MainForm)));
        if (!InvokeRequired) { try { return Task.FromResult(action()); } catch (Exception error) { return Task.FromException<T>(error); } }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { BeginInvoke(() => { try { completion.TrySetResult(action()); } catch (Exception error) { completion.TrySetException(error); } }); }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }
    private async Task<T> UiTaskAsync<T>(Func<Task<T>> action) => await await UiAsync(action).ConfigureAwait(false);
    public Task<string?> OpenFileAsync(string title, string filter) => UiAsync(() => { using var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false }; return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null; });
    public Task<string?> SaveFileAsync(string title, string filter, string suggestedName) => UiAsync(() => { using var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = Path.GetFileName(suggestedName), AddExtension = true, OverwritePrompt = true, InitialDirectory = _application.Settings.ExportFolder }; return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null; });
    public Task<string?> PickFolderAsync(string title) => UiAsync(() => { using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true }; return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null; });
    public async Task SetClipboardAsync(string text) => await UiAsync(() => { Clipboard.SetText(text); return true; });
    public Task OpenExternalAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0) throw new InvalidDataException("Lien externe invalide.");
        return UiAsync(() => { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return true; });
    }
    public async Task CloseAsync() => await UiAsync(() => { BeginInvoke(Close); return true; });
    public Task<byte[]> CapturePngAsync() => UiTaskAsync(async () => { using var image = new MemoryStream(); await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image); return image.ToArray(); });
    public async Task PrintPdfAsync(string path) => await UiTaskAsync(async () => { var settings = _web.CoreWebView2.Environment.CreatePrintSettings(); settings.ShouldPrintBackgrounds = true; if (!await _web.CoreWebView2.PrintToPdfAsync(path, settings)) throw new IOException("Le document PDF n’a pas été créé."); return true; });
    public Task<bool> NotifyAsync(string title, string message) => UiTaskAsync(async () =>
    {
        if (!NotificationPolicy.CanNotify()) return false;
        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => shown.TrySetResult(true);
        _notification.BalloonTipShown += handler;
        try { _notification.Visible = true; _notification.ShowBalloonTip(5000, title[..Math.Min(title.Length, 63)], message[..Math.Min(message.Length, 255)], ToolTipIcon.Info); return await shown.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { return false; }
        finally { _notification.BalloonTipShown -= handler; }
    });
    public void ActivateExisting()
    {
        if (IsDisposed || !IsHandleCreated) return;
        _ = UiAsync(() => { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); return true; });
    }
    public void ReportError(Exception error)
    {
        AppPaths.AppendLog("Affichage : " + AppPaths.SafeError(error));
        if (!_ready) { ExitCode = 1; Program.ReportStartupFailure(error); Close(); }
        else _bridge.Publish("app.error", new { message = AppPaths.SafeError(error) });
    }
    private void RestorePlacement()
    {
        var preferences = _application.Settings.Interface; var values = preferences.WindowPixelBounds;
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == preferences.PreferredMonitor) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        if (values.Length == 4)
        {
            var bounds = new Rectangle((int)values[0], (int)values[1], (int)values[2], (int)values[3]);
            screen = Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.IntersectsWith(bounds)) ?? screen;
            StartPosition = FormStartPosition.Manual; Bounds = Fit(bounds, screen.WorkingArea);
        }
        if (values.Length != 4) Size = Fit(Bounds, screen.WorkingArea).Size;
        if (preferences.WindowMaximized) WindowState = FormWindowState.Maximized;
    }
    private static Rectangle Fit(Rectangle bounds, Rectangle area)
    {
        var width = Math.Min(Math.Max(800, bounds.Width), area.Width); var height = Math.Min(Math.Max(560, bounds.Height), area.Height);
        return new Rectangle(Math.Clamp(bounds.X, area.Left, area.Right - width), Math.Clamp(bounds.Y, area.Top, area.Bottom - height), width, height);
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => _application.SessionLocked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect;
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) _application.ResumeAutomaticSchedule(); }
    private void NetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) { if (e.IsAvailable) _application.ResumeAutomaticSchedule(); }
    private async Task WatchCloseHandshakeAsync(string token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _lifetime.Token);
            if (_closeRequested && !_closeReady && _closeToken == token)
            {
                _closeRequested = false; _closeToken = "";
                AppPaths.AppendLog("Fermeture différée : la sauvegarde des brouillons n’a pas confirmé sa fin.");
                _bridge.Publish("app.error", new { message = "La sauvegarde des brouillons n’a pas terminé. La fenêtre reste ouverte ; vérifiez le journal puis réessayez de fermer." });
            }
        }
        catch (OperationCanceledException) { }
    }
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing) return;
        if (!_closeReady && _ready && _web.CoreWebView2 is not null)
        {
            if (_closeRequested) return;
            _closeRequested = true; _closeToken = Guid.NewGuid().ToString("N");
            _bridge.Publish("app.beforeClose", new { token = _closeToken });
            _ = WatchCloseHandshakeAsync(_closeToken); return;
        }
        _closing = true;
        _timer.Stop(); _lifetime.Cancel();
        SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        try
        {
            await _bridge.DisposeAsync();
            await _application.DisposeAsync();
            if (_ready && _placementChanged)
            {
                var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                var preferences = _application.Settings.Interface;
                preferences.WindowPixelBounds = new double[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
                preferences.WindowMaximized = WindowState == FormWindowState.Maximized;
                preferences.PreferredMonitor = Screen.FromRectangle(bounds).DeviceName;
                _application.Settings.Save();
            }
        }
        catch (Exception error) { AppPaths.AppendLog("Fermeture : " + AppPaths.SafeError(error)); }
        finally { _closed = true; _notification.Dispose(); _timer.Dispose(); _web.Dispose(); _lifetime.Dispose(); Close(); }
    }
}
