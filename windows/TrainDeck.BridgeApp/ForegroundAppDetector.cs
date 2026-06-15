using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrainDeck.BridgeApp;

internal static class ForegroundAppDetector
{
    private const int ShowNormal = 1;
    private const int ShowRestore = 9;

    public static bool IsTrainSimWorldForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("TrainSimWorld", StringComparison.OrdinalIgnoreCase)
                || process.MainWindowTitle.Contains("Train Sim World", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool FocusTrainSimWorld()
    {
        try
        {
            var target = Process.GetProcessesByName("TrainSimWorld")
                .Where(process => process.MainWindowHandle != IntPtr.Zero)
                .OrderByDescending(process => process.MainWindowTitle.Contains("Train Sim World", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (target is null)
            {
                return false;
            }

            var targetWindow = target.MainWindowHandle;
            var foregroundWindow = GetForegroundWindow();
            var foregroundThread = foregroundWindow == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foregroundWindow, out _);
            var targetThread = GetWindowThreadProcessId(targetWindow, out _);
            var attached = false;
            if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
            {
                attached = AttachThreadInput(foregroundThread, targetThread, true);
            }

            ShowWindow(targetWindow, ShowRestore);
            ShowWindow(targetWindow, ShowNormal);
            BringWindowToTop(targetWindow);
            SetActiveWindow(targetWindow);
            var focused = SetForegroundWindow(targetWindow);

            if (attached)
            {
                AttachThreadInput(foregroundThread, targetThread, false);
            }

            return focused || IsTrainSimWorldForeground();
        }
        catch
        {
            return false;
        }
    }

    public static string DescribeForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "none";
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return "unknown";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var title = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? ""
                : $" '{process.MainWindowTitle}'";
            return $"{process.ProcessName}/{process.Id}{title}";
        }
        catch
        {
            return $"pid {processId}";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
