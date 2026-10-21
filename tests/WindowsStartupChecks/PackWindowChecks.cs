using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;

namespace GlpiPilot.WindowsStartupChecks;

/// <summary>
/// Exercises only the window of the explicitly supplied pack process. The CI caller
/// owns its isolated application state and its lifetime; this class opens no windows,
/// searches no desktop tree, and never invokes extraction or browser commands.
/// </summary>
internal static class PackWindowChecks
{
    public static int Run(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 0 || args[2] is not ("exercise" or "verify-restore"))
        {
            Console.Error.WriteLine("Usage: WindowsStartupChecks --inspect-pack-window <pid> <exercise|verify-restore>");
            return 3;
        }
        var result = 1;
        var worker = new Thread(() =>
        {
            try
            {
                Inspect(pid, args[2]);
                result = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: packaged window zoom inspection for PID " + pid);
                Console.Error.WriteLine(error);
            }
        }) { IsBackground = true, Name = "GLPI pack UI Automation check" };
        // UI Automation uses its own Windows messages; a windowless MTA worker
        // avoids blocking a WPF dispatcher or creating an STA apartment dependency.
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(45)))
        {
            Console.Error.WriteLine("FAIL: packaged window zoom inspection exceeded 45 seconds.");
            return 2;
        }
        return result;
    }

    private static void Inspect(int pid, string mode)
    {
        using var process = Process.GetProcessById(pid);
        process.Refresh();
        Require(!process.HasExited && process.ProcessName == "GLPI-Pilot", "The supplied PID is not a running GLPI-Pilot pack.");
        Require(process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle == "GLPI Pilot", "The supplied pack has no ready main window.");
        var window = AutomationElement.FromHandle(process.MainWindowHandle);
        Require(window.Current.ProcessId == pid && window.Current.ControlType == ControlType.Window,
            "UI Automation did not bind the exact supplied process window.");

        if (mode == "verify-restore")
        {
            WaitForZoom(window, pid, 110);
            AssertCommandBounds(window, pid);
            Invoke(window, pid, "ZoomResetButton", 100);
            Console.WriteLine("PASS: packaged window zoom persisted at 110 % after restart and reset to 100 % through the real button.");
            return;
        }

        WaitForZoom(window, pid, 100);
        AssertCommandBounds(window, pid);
        Invoke(window, pid, "ZoomInButton", 105);
        Invoke(window, pid, "ZoomInButton", 110);
        Invoke(window, pid, "ZoomOutButton", 105);
        Invoke(window, pid, "ZoomResetButton", 100);
        Console.WriteLine("PASS: packaged window zoom buttons changed 100 → 105 → 110 → 105 → 100 %.");

        for (var expected = 95; expected >= 75; expected -= 5) Invoke(window, pid, "ZoomOutButton", expected);
        Require(!Find(window, pid, "ZoomOutButton").Current.IsEnabled, "Zoom out remained enabled below the supported 75 % minimum.");
        for (var expected = 80; expected <= 150; expected += 5) Invoke(window, pid, "ZoomInButton", expected);
        Require(!Find(window, pid, "ZoomInButton").Current.IsEnabled, "Zoom in remained enabled above the supported 150 % maximum.");
        Console.WriteLine("PASS: packaged window zoom enforces 75–150 % limits and updates both real button states.");

        var original = window.Current.BoundingRectangle;
        Require(window.TryGetCurrentPattern(TransformPattern.Pattern, out var transformValue)
            && transformValue is TransformPattern { Current.CanResize: true }, "The main window cannot be resized through its accessible window pattern.");
        var transform = (TransformPattern)transformValue;
        transform.Resize(960, 720);
        WaitUntil(() =>
        {
            var resized = window.Current.BoundingRectangle;
            if (Math.Abs(resized.Width - 960) > 3 || Math.Abs(resized.Height - 720) > 3) return false;
            try { AssertCommandBounds(window, pid); return true; }
            catch (InvalidOperationException) { return false; }
        }, "At 150 % zoom the primary controls do not fit the resized pack window.");
        var narrow = window.Current.BoundingRectangle;
        Console.WriteLine($"PASS: packaged window primary controls remain inside its {narrow.Width:0}×{narrow.Height:0} window at 150 %.");
        transform.Resize(original.Width, original.Height);
        Invoke(window, pid, "ZoomResetButton", 100);
        Invoke(window, pid, "ZoomInButton", 105);
        Invoke(window, pid, "ZoomInButton", 110);
        Console.WriteLine("PASS: packaged window zoom exercise completed; real UI is left at 110 % for the caller's close/reopen persistence check.");
    }

    private static AutomationElement Find(AutomationElement window, int pid, string id)
    {
        var element = window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, id),
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
            ?? throw new InvalidOperationException("Missing real pack control: " + id);
        Require(element.Current.ProcessId == pid, "Control belongs to a different process: " + id);
        return element;
    }

    private static void Invoke(AutomationElement window, int pid, string id, int expected)
    {
        var button = Find(window, pid, id);
        Require(button.Current.IsEnabled && !button.Current.IsOffscreen, "Zoom control is not actionable: " + id);
        Require(button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern), "Zoom control has no accessible Invoke pattern: " + id);
        ((InvokePattern)pattern).Invoke();
        WaitForZoom(window, pid, expected);
    }

    private static void WaitForZoom(AutomationElement window, int pid, int expected)
    {
        WaitUntil(() =>
        {
            var value = Find(window, pid, "ZoomResetButton").Current.Name;
            var percent = Regex.Match(value, @"(?<!\d)(\d{2,3})\s*%");
            return percent.Success && int.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture) == expected;
        }, "The real pack UI did not display the expected zoom: " + expected + " %.");
    }

    private static void AssertCommandBounds(AutomationElement window, int pid)
    {
        var bounds = window.Current.BoundingRectangle;
        bounds.Inflate(2, 2); // Tolerate rounding between logical layout and screen pixels.
        foreach (var id in new[] { "ZoomOutButton", "ZoomResetButton", "ZoomInButton", "RunAutoButton" })
        {
            var control = Find(window, pid, id);
            var rectangle = control.Current.BoundingRectangle;
            Require(!control.Current.IsOffscreen && rectangle.Width > 0 && rectangle.Height > 0 && bounds.Contains(rectangle),
                "Primary command is clipped or outside the pack window: " + id);
        }
    }

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(4))
        {
            if (condition()) return;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException(failure);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
