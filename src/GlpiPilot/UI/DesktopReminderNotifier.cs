using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlpiPilot.UI;

/// <summary>A generic local notification while the application is running. No ticket content enters the Windows notification.</summary>
public sealed class DesktopReminderNotifier : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 73;
    private readonly Window _window;
    private readonly Action _open;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _added, _disposed;
    private uint _taskbarCreated;

    public DesktopReminderNotifier(Window window, Action open) { _window = window; _open = open; }

    public bool Show(int count)
    {
        if (_disposed || count <= 0 || !OperatingSystem.IsWindows()) return false;
        if (_handle == IntPtr.Zero)
        {
            _handle = new WindowInteropHelper(_window).Handle;
            if (_handle == IntPtr.Zero) return false;
            _source = HwndSource.FromHwnd(_handle); if (_source == null) return false;
            _source.AddHook(OnMessage); _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        }
        var data = Data();
        if (!_added)
        {
            data.Flags = 0x1 | 0x2 | 0x4 | 0x80; // NIF_MESSAGE | ICON | TIP | SHOWTIP
            data.Callback = CallbackMessage; data.Icon = LoadIcon(IntPtr.Zero, new IntPtr(32516)); // Shared IDI_INFORMATION
            data.Tip = "GLPI Pilot · mes rappels";
            if (!ShellNotifyIcon(0, ref data)) return false;
            _added = true; data.Version = 4; _ = ShellNotifyIcon(4, ref data);
        }
        data.Flags = 0x10; data.Title = "GLPI Pilot · Ma journée";
        data.Info = $"{count:N0} rappel(s) personnel(s) à consulter. Cliquez pour ouvrir Ma journée.";
        data.InfoFlags = 0x1 | 0x10 | 0x80; // Information, no sound, respect Windows quiet time.
        var success = ShellNotifyIcon(1, ref data);
        if (!success) _added = false;
        return success;
    }

    private IntPtr OnMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreated != 0 && message == _taskbarCreated) _added = false;
        if (message == CallbackMessage)
        {
            var code = unchecked((uint)lParam.ToInt64()) & 0xffff;
            if (code is 0x405 or 0x400 or 0x401) // Balloon user click / icon select / keyboard select.
            {
                handled = true;
                _window.Dispatcher.BeginInvoke((Action)(() => { if (!_disposed) _open(); }));
            }
        }
        return IntPtr.Zero;
    }
    private NotificationData Data() => new() { Size = (uint)Marshal.SizeOf<NotificationData>(), Window = _handle, Id = 1, Tip = "", Info = "", Title = "" };
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_added) { var data = Data(); _ = ShellNotifyIcon(2, ref data); }
        _source?.RemoveHook(OnMessage); _source = null; _added = false;
    }

    // Win32 NOTIFYICONDATAW (full Windows Vista+ structure); the app targets Windows x64.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint action, ref NotificationData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
