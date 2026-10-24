using System.Text.Json;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GlpiPilot.Desktop;

/// <summary>A separate, opt-in WebView. The application WebView keeps its original offline policy.</summary>
internal sealed class GoogleMapsForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly GoogleMapFrame _map;
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Map-" + Guid.NewGuid().ToString("N"));
    private bool _closing;

    internal GoogleMapsForm(GoogleMapFrame map)
    {
        if (!GoogleMapsService.IsEmbedUrl(map.EmbedUrl) || !GoogleMapsService.IsExternalMapUrl(map.ExternalUrl)) throw new InvalidDataException("Carte Google Maps invalide.");
        _map = map; Text = "Google Maps · GLPI Pilot"; MinimumSize = new(660, 520); Size = new(1100, 780);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = map.Theme == "light" ? Color.FromArgb(242, 246, 251) : Color.FromArgb(13, 22, 37);
        _web.DefaultBackgroundColor = BackColor; Controls.Add(_web);
        Load += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) =>
        {
            _closing = true; _web.Dispose();
            try { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        };
    }
    private static bool Local(string value, bool pageOnly = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != GoogleMapsService.MapHost
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        return pageOnly ? uri.AbsolutePath == "/maps.html" : uri.AbsolutePath is "/maps.html" or "/js/maps-frame.js" or "/css/app.css" or "/css/google-maps.css";
    }
    private async Task InitializeAsync()
    {
        try
        {
            var runtime = Path.Combine(AppContext.BaseDirectory, "wad", "runtime", "webview2");
            var root = Path.Combine(AppContext.BaseDirectory, "wad", "web");
            var environment = await CoreWebView2Environment.CreateAsync(runtime, _profile, new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false })
                .WaitAsync(TimeSpan.FromSeconds(60));
            if (_closing) return;
            await _web.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(60));
            if (_closing) return;
            var core = _web.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsStatusBarEnabled = false; core.Settings.IsZoomControlEnabled = true;
            core.SetVirtualHostNameToFolderMapping(GoogleMapsService.MapHost, root, CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!Local(e.Request.Uri) && !GoogleMapsService.IsMapResource(e.Request.Uri))
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.NavigationStarting += (_, e) => { if (!Local(e.Uri, true)) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) =>
            {
                // Only the generated Google Embed document is admitted. Nested login,
                // advertising and other destination documents cannot gain a window.
                if (e.Uri != "about:blank" && e.Uri != _map.EmbedUrl) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
            core.NavigationCompleted += async (_, e) =>
            {
                if (_closing || !e.IsSuccess || !Local(core.Source, true)) return;
                // Fixed renderer and JSON serialization; no host messages or general command bridge.
                try { await core.ExecuteScriptAsync("window.showGoogleMap(" + JsonSerializer.Serialize(_map, PilotApplication.Json) + ");"); }
                catch (Exception error) { if (!_closing) ShowFailure(error); }
            };
            core.Navigate("https://" + GoogleMapsService.MapHost + "/maps.html");
        }
        catch (Exception error) { if (!_closing) ShowFailure(error); }
    }
    private void ShowFailure(Exception error)
    {
        if (_closing) return;
        // Do not include the remote URL/API key in errors or logs.
        _web.Visible = false;
        Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = false, Padding = new(28), ForeColor = _map.Theme == "light" ? Color.FromArgb(20, 38, 60) : Color.FromArgb(237, 244, 255),
            Font = new Font("Segoe UI", 12), Text = "La fenêtre cartographique n’a pas pu être initialisée.\n\nUtilisez Ouvrir Google Maps dans l’aperçu, puis vérifiez le runtime WebView2 du pack.\n\n" + error.GetType().Name });
    }
}
