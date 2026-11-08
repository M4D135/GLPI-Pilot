using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GlpiPilot.Core;

// Repli de présentation pour les navigateurs qui ne parlent pas CDP, c'est-à-dire Firefox.
// Il ne touche que les fenêtres appartenant au build Firefox embarqué dans le pack : le Firefox
// installé par l’utilisateur (chemin différent) n’est jamais énuméré ni réduit.
internal static class BrowserWindowFallback
{
    private const int SW_RESTORE = 9, SW_MINIMIZE = 6;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static bool TrySetWindow(string expectedTitle, bool restore)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var root = BrowserChannels.BundledFirefoxDirectory();
        if (root is null) return false;
        var touched = false;
        foreach (var handle in TopLevelWindows())
        {
            if (!Native.IsWindowVisible(handle)) continue;
            var executable = WindowExecutablePath(handle);
            if (executable is null) continue;
            if (!executable.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            // Sans titre attendu, on ne réduit rien : mieux vaut une fenêtre visible qu'une fenêtre perdue.
            if (string.IsNullOrWhiteSpace(expectedTitle)) return false;
            var caption = Caption(handle);
            if (caption.Length == 0) continue;
            if (!caption.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase)) continue;
            touched |= Native.ShowWindow(handle, restore ? SW_RESTORE : SW_MINIMIZE);
        }
        return touched;
    }

    private static IEnumerable<IntPtr> TopLevelWindows()
    {
        var handles = new List<IntPtr>();
        Native.EnumWindows((handle, _) => { handles.Add(handle); return true; }, IntPtr.Zero);
        return handles;
    }

    private static string? WindowExecutablePath(IntPtr handle)
    {
        Native.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;
        var process = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            return Native.QueryFullProcessImageName(process, 0, buffer, ref length) ? buffer.ToString(0, length) : null;
        }
        catch (Win32Exception) { return null; }
        finally { Native.CloseHandle(process); }
    }

    private static string Caption(IntPtr handle)
    {
        var length = Native.GetWindowTextLength(handle);
        if (length <= 0) return "";
        var buffer = new StringBuilder(length + 1);
        Native.GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int capacity);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int capacity);
    }
}
