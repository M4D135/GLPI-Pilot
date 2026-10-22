using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public static class MonitorPlacement
{
    public sealed record MonitorChoice(string Id, string Label, int Left, int Top, int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    public static IReadOnlyList<MonitorChoice> Available()
    {
        var items = new List<MonitorChoice> { new("", "Automatique · écran disponible le plus proche", 0, 0, 0, 0) };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info)) items.Add(new(info.Device, $"{info.Device} · {info.Work.Right - info.Work.Left} × {info.Work.Bottom - info.Work.Top}" + (info.Flags == 1 ? " · principal" : ""), info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        }, IntPtr.Zero); return items;
    }
    public static void Restore(Window window, InterfacePreferences preferences)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero || !GetWindowRect(handle, out var current)) return;
        var monitors = Available().Where(m => m.Id.Length > 0).ToArray(); if (monitors.Length == 0) return;
        var saved = preferences.WindowPixelBounds; var x = saved.Length == 4 ? saved[0] : current.Left; var y = saved.Length == 4 ? saved[1] : current.Top;
        var width = saved.Length == 4 ? saved[2] : current.Right - current.Left; var height = saved.Length == 4 ? saved[3] : current.Bottom - current.Top;
        var monitor = monitors.FirstOrDefault(m => m.Id == preferences.PreferredMonitor) ?? monitors.OrderBy(m => Math.Pow(Math.Clamp(x + width / 2, m.Left, m.Left + m.Width) - x - width / 2, 2) + Math.Pow(Math.Clamp(y + height / 2, m.Top, m.Top + m.Height) - y - height / 2, 2)).First();
        width = Math.Clamp(width, Math.Min(640, monitor.Width), monitor.Width); height = Math.Clamp(height, Math.Min(420, monitor.Height), monitor.Height);
        x = Math.Clamp(x, monitor.Left, monitor.Left + monitor.Width - width); y = Math.Clamp(y, monitor.Top, monitor.Top + monitor.Height - height);
        if (window.WindowState == WindowState.Maximized) window.WindowState = WindowState.Normal;
        SetWindowPos(handle, IntPtr.Zero, (int)x, (int)y, (int)width, (int)height, 0x0014);
        if (preferences.WindowMaximized) window.WindowState = WindowState.Maximized;
    }
    public static void Remember(Window window, InterfacePreferences preferences)
    {
        if (window.WindowState != WindowState.Normal) return;
        if (GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) preferences.WindowPixelBounds = new double[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
    }
}
