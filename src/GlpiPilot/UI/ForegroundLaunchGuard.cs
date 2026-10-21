using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlpiPilot.UI;

/// <summary>
/// Briefly protects the foreground app while its browser is being launched.
/// Dispose as soon as the browser has been reduced, including on launch failure.
/// </summary>
public sealed class ForegroundLaunchGuard : IDisposable
{
    private const uint LockForeground = 1;
    private const uint UnlockForeground = 2;
    private int _held;

    private ForegroundLaunchGuard(bool held = false) => _held = held ? 1 : 0;

    /// <summary>
    /// True when this guard acquired the lock and has not been disposed.
    /// Windows may release the lock earlier when the user changes windows.
    /// </summary>
    public bool IsHeld => Volatile.Read(ref _held) != 0;

    public static ForegroundLaunchGuard Acquire(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows()) return new();

        if (!window.Dispatcher.CheckAccess())
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return new();
            return window.Dispatcher.Invoke(() => Acquire(window));
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || GetForegroundWindow() != handle) return new();

        return new(LockSetForegroundWindow(LockForeground));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _held, 0) != 0)
            LockSetForegroundWindow(UnlockForeground);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockSetForegroundWindow(uint lockCode);
}
