using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlpiPilot.Core;

/// <summary>Replaces every application palette brush; controls reference them dynamically.</summary>
public static class ThemeService
{
    private static readonly ConditionalWeakTable<Window, object> HookedWindows = new();

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["WindowBrush"] = "#18181B",
        ["PanelBrush"] = "#232326",
        ["FieldBrush"] = "#29292D",
        ["ElevatedBrush"] = "#303034",
        ["LineBrush"] = "#3C3C42",
        ["TextBrush"] = "#F5F5F7",
        ["MutedBrush"] = "#AEADB6",
        ["AccentBrush"] = "#9ABFFF",
        ["ActionBrush"] = "#2768D6",
        ["ActionHoverBrush"] = "#2B6EDB",
        ["ActionBorderBrush"] = "#447DE0",
        ["OnActionBrush"] = "#FFFFFF",
        ["SelectedBrush"] = "#2C4165",
        ["FieldSelectionBrush"] = "#38547F",
        ["HeaderBrush"] = "#29292D",
        ["HeaderTextBrush"] = "#C9C8D0",
        ["HeaderHoverBrush"] = "#333338",
        ["RowBrush"] = "#232326",
        ["AlternateRowBrush"] = "#262629",
        ["RowHoverBrush"] = "#303036",
        ["GridLineBrush"] = "#333338",
        ["ScrollThumbBrush"] = "#66656F",
        ["ScrollThumbHoverBrush"] = "#8A8993",
        ["ScrollTrackBrush"] = "#252528",
        ["TooltipBrush"] = "#343439",
        ["TooltipTextBrush"] = "#F5F5F7",
        ["TooltipBorderBrush"] = "#51515A",
        ["BadgeBrush"] = "#303034",
        ["BadgeTextBrush"] = "#C9C8D0",
        ["BrandBrush"] = "#2768D6",
        ["BrandTextBrush"] = "#FFFFFF",
        ["NoticeBrush"] = "#2A2A2F",
        ["StatusBrush"] = "#18181B",
        ["ProgressTrackBrush"] = "#3A3A41",
        ["ErrorBrush"] = "#FFB7AD",
        ["SuccessBrush"] = "#9DD7B7",
        ["WarningBrush"] = "#E9CA92",
        ["DisabledTextBrush"] = "#83828C",
        ["DisabledBackgroundBrush"] = "#29292D",
        ["DisabledBorderBrush"] = "#36363C"
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["WindowBrush"] = "#F5F5F7",
        ["PanelBrush"] = "#FFFFFF",
        ["FieldBrush"] = "#FFFFFF",
        ["ElevatedBrush"] = "#EBEBEF",
        ["LineBrush"] = "#D7D7DE",
        ["TextBrush"] = "#202024",
        ["MutedBrush"] = "#666570",
        ["AccentBrush"] = "#145FC7",
        ["ActionBrush"] = "#1765D1",
        ["ActionHoverBrush"] = "#1054B7",
        ["ActionBorderBrush"] = "#1765D1",
        ["OnActionBrush"] = "#FFFFFF",
        ["SelectedBrush"] = "#E2ECFC",
        ["FieldSelectionBrush"] = "#C9DEFC",
        ["HeaderBrush"] = "#F5F5F7",
        ["HeaderTextBrush"] = "#4E4D57",
        ["HeaderHoverBrush"] = "#EBEBF0",
        ["RowBrush"] = "#FFFFFF",
        ["AlternateRowBrush"] = "#FAFAFB",
        ["RowHoverBrush"] = "#F0F4FB",
        ["GridLineBrush"] = "#EAEAEF",
        ["ScrollThumbBrush"] = "#A3A2AD",
        ["ScrollThumbHoverBrush"] = "#7E7D89",
        ["ScrollTrackBrush"] = "#F4F4F6",
        ["TooltipBrush"] = "#FFFFFF",
        ["TooltipTextBrush"] = "#202024",
        ["TooltipBorderBrush"] = "#CBCBD3",
        ["BadgeBrush"] = "#EEEEF2",
        ["BadgeTextBrush"] = "#5B5A65",
        ["BrandBrush"] = "#1765D1",
        ["BrandTextBrush"] = "#FFFFFF",
        ["NoticeBrush"] = "#F3F4F7",
        ["StatusBrush"] = "#F5F5F7",
        ["ProgressTrackBrush"] = "#DFDFE6",
        ["ErrorBrush"] = "#AB342C",
        ["SuccessBrush"] = "#276849",
        ["WarningBrush"] = "#855600",
        ["DisabledTextBrush"] = "#8A8992",
        ["DisabledBackgroundBrush"] = "#F0F0F3",
        ["DisabledBorderBrush"] = "#E1E1E6"
    };

    public static string CurrentTheme { get; private set; } = "dark";
    public static bool IsDark => CurrentTheme == "dark";

    public static void Apply(string? theme)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(theme));
            return;
        }

        CurrentTheme = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        var palette = IsDark ? DarkPalette : LightPalette;
        foreach (var pair in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            brush.Freeze();
            app.Resources[pair.Key] = brush;
        }

        // Keep fallback WPF surfaces (context menus, selection, scroll corners) consistent.
        SetSystemBrush(app, SystemColors.WindowBrushKey, "FieldBrush");
        SetSystemBrush(app, SystemColors.WindowTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.ControlBrushKey, "PanelBrush");
        SetSystemBrush(app, SystemColors.ControlTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.ControlDarkBrushKey, "LineBrush");
        SetSystemBrush(app, SystemColors.ControlDarkDarkBrushKey, "LineBrush");
        SetSystemBrush(app, SystemColors.ControlLightBrushKey, "ElevatedBrush");
        SetSystemBrush(app, SystemColors.ControlLightLightBrushKey, "PanelBrush");
        SetSystemBrush(app, SystemColors.HighlightBrushKey, "SelectedBrush");
        SetSystemBrush(app, SystemColors.HighlightTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.InactiveSelectionHighlightBrushKey, "SelectedBrush");
        SetSystemBrush(app, SystemColors.InactiveSelectionHighlightTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.GrayTextBrushKey, "DisabledTextBrush");
        SetSystemBrush(app, SystemColors.MenuBrushKey, "PanelBrush");
        SetSystemBrush(app, SystemColors.MenuTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.MenuHighlightBrushKey, "SelectedBrush");
        SetSystemBrush(app, SystemColors.ScrollBarBrushKey, "ScrollTrackBrush");
        SetSystemBrush(app, SystemColors.InfoBrushKey, "TooltipBrush");
        SetSystemBrush(app, SystemColors.InfoTextBrushKey, "TooltipTextBrush");

        foreach (Window window in app.Windows) Attach(window);
    }

    public static void Attach(Window window)
    {
        if (!HookedWindows.TryGetValue(window, out _))
        {
            HookedWindows.Add(window, new object());
            window.SourceInitialized += (_, _) => UpdateNativeCaption(window);
        }
        UpdateNativeCaption(window);
    }

    private static void SetSystemBrush(Application app, ResourceKey key, string paletteKey) => app.Resources[key] = app.Resources[paletteKey];

    private static void UpdateNativeCaption(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var palette = IsDark ? DarkPalette : LightPalette;
            int background = ColorRef(palette["WindowBrush"]);
            int foreground = ColorRef(palette["TextBrush"]);
            DwmSetWindowAttribute(handle, 35, ref background, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref foreground, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static int ColorRef(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        return color.R | color.G << 8 | color.B << 16;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
