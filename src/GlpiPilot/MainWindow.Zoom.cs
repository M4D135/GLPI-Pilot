using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlpiPilot.Core;

namespace GlpiPilot;

public partial class MainWindow
{
    private readonly ScaleTransform _interfaceScale = new();
    private DispatcherTimer? _interfaceZoomSaveTimer;
    private bool _interfaceZoomDirty;
    private bool _updatingResponsiveChrome;

    private void InitializeInterfaceZoom()
    {
        ShellRoot.LayoutTransform = _interfaceScale;
        _interfaceZoomSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _interfaceZoomSaveTimer.Tick += (_, _) => PersistInterfaceZoom();
        PreviewMouseWheel += InterfaceZoom_MouseWheel;
        ApplyInterfaceZoom();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetInterfaceZoom(_settings.InterfaceZoomPercent - AppSettings.InterfaceZoomStep);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetInterfaceZoom(_settings.InterfaceZoomPercent + AppSettings.InterfaceZoomStep);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetInterfaceZoom(100);

    private void SetInterfaceZoom(int percent)
    {
        if (_closing) return;
        percent = AppSettings.NormalizeInterfaceZoom(percent);
        if (percent == _settings.InterfaceZoomPercent) return;
        _settings.InterfaceZoomPercent = percent;
        ApplyInterfaceZoom();
        _interfaceZoomDirty = true;
        _interfaceZoomSaveTimer?.Stop();
        _interfaceZoomSaveTimer?.Start();
    }

    private void ApplyInterfaceZoom()
    {
        var percent = AppSettings.NormalizeInterfaceZoom(_settings.InterfaceZoomPercent);
        _interfaceScale.ScaleX = _interfaceScale.ScaleY = percent / 100d;
        ZoomResetButton.Content = $"{percent} %";
        ZoomOutButton.IsEnabled = percent > AppSettings.MinimumInterfaceZoomPercent;
        ZoomInButton.IsEnabled = percent < AppSettings.MaximumInterfaceZoomPercent;
        // LayoutTransform causes real reflow at the new logical viewport size.
        // The SizeChanged callback updates navigation after that layout pass.
        UpdateWorkspaceLayout();
    }

    private bool HandleInterfaceZoomKey(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        switch (key)
        {
            case Key.Add: case Key.OemPlus: SetInterfaceZoom(_settings.InterfaceZoomPercent + AppSettings.InterfaceZoomStep); return true;
            case Key.Subtract: case Key.OemMinus: SetInterfaceZoom(_settings.InterfaceZoomPercent - AppSettings.InterfaceZoomStep); return true;
            case Key.D0: case Key.NumPad0: SetInterfaceZoom(100); return true;
            default: return false;
        }
    }

    private void InterfaceZoom_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_closing || Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0) return;
        SetInterfaceZoom(_settings.InterfaceZoomPercent + Math.Sign(e.Delta) * AppSettings.InterfaceZoomStep);
        e.Handled = true;
    }

    private void PersistInterfaceZoom()
    {
        _interfaceZoomSaveTimer?.Stop();
        if (!_interfaceZoomDirty) return;
        _interfaceZoomDirty = false;
        try
        {
            // Save only the current persisted model. Do not submit unfinished form fields.
            _settings.Save();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddLog("INTERFACE · zoom appliqué, mémorisation indisponible : " + AppPaths.SafeError(error));
            if (!_closing) SetStatus("Zoom appliqué pour cette session ; sa mémorisation a échoué. Voir le journal.");
        }
    }

    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkspaceLayout();

    private void UpdateResponsiveChrome()
    {
        if (_updatingResponsiveChrome) return;
        _updatingResponsiveChrome = true;
        try
        {
            var scale = Math.Max(.75, _interfaceScale.ScaleX);
            var width = ShellRoot.ActualWidth > 0 ? ShellRoot.ActualWidth : Math.Max(300, Width / scale - 48);
            var height = ShellRoot.ActualHeight > 0 ? ShellRoot.ActualHeight : Height / scale;
            var compact = width < 850;
            var stackedHeader = !compact && width < 1200;
            ShellHeaderTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ShellHeaderTitle.Margin = new Thickness(0, 0, 0, stackedHeader ? 7 : 0);
            Grid.SetRow(ShellHeaderActions, stackedHeader ? 1 : 0);
            Grid.SetColumn(ShellHeaderActions, compact || stackedHeader ? 0 : 1);
            Grid.SetColumnSpan(ShellHeaderActions, compact || stackedHeader ? 2 : 1);
            ShellHeaderActions.HorizontalAlignment = compact || stackedHeader ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            ShellHeaderActions.MaxWidth = Math.Max(260, width - (compact || stackedHeader ? 0 : 240));
            CommandPaletteButton.Content = compact ? "Actions" : "Actions · Ctrl K";
            PersonalWorkspaceButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ShellHeader.Margin = new Thickness(0, 0, 0, compact ? 10 : 14);

            var wide = width >= 1240 && height >= 620;
            if (_wideNavigation != wide)
            {
                _wideNavigation = wide;
                MainTabs.Style = (Style)FindResource(_settings.Interface.FocusMode ? "FocusWorkspaceTabs" : wide ? "WorkspaceTabs" : "CompactWorkspaceTabs");
            }

            var previewWidth = PreviewHeader.ActualWidth > 0 ? PreviewHeader.ActualWidth : width - (wide ? 234 : 32);
            var compactPreview = previewWidth < 820;
            PreviewHeading.Visibility = compactPreview ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(PreviewActions, compactPreview ? 0 : 1);
            Grid.SetColumnSpan(PreviewActions, compactPreview ? 2 : 1);
            PreviewActions.HorizontalAlignment = compactPreview ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            PreviewActions.MaxWidth = Math.Max(240, compactPreview ? previewWidth : Math.Min(610, previewWidth - 190));
            PreviewFilterLabel.Visibility = compactPreview ? Visibility.Collapsed : Visibility.Visible;
            SearchBox.Width = compactPreview ? 140 : 180;
            OpenTicketButton.Content = compactPreview ? "Ticket" : "Détail du ticket";
            RevealLastExportButton.Visibility = !compactPreview && !string.IsNullOrEmpty(_lastExportPath) ? Visibility.Visible : Visibility.Collapsed;
            var stackFooter = previewWidth < 610 && !string.IsNullOrEmpty(_lastExportPath);
            Grid.SetRow(PreviewExportActions, stackFooter ? 1 : 0);
            Grid.SetColumn(PreviewExportActions, stackFooter ? 0 : 1);
            Grid.SetColumnSpan(PreviewExportActions, stackFooter ? 2 : 1);
            PreviewExportActions.Margin = new Thickness(0, stackFooter ? 5 : 0, 0, 0);
            PreviewExportActions.MaxWidth = Math.Max(230, previewWidth);
            SourceStateText.TextWrapping = compact && height < 520 ? TextWrapping.NoWrap : TextWrapping.Wrap;
            SourceStateText.TextTrimming = TextTrimming.CharacterEllipsis;
            SourceStateText.ToolTip = SourceStateText.Text;
            ConnectionText.Visibility = compact && height < 520 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _updatingResponsiveChrome = false; }
    }

    private void DisposeInterfaceZoom()
    {
        PersistInterfaceZoom();
        PreviewMouseWheel -= InterfaceZoom_MouseWheel;
    }
}
