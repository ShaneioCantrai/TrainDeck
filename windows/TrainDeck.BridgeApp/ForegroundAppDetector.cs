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

            ShowWindow(target.MainWindowHandle, ShowRestore);
            ShowWindow(target.MainWindowHandle, ShowNormal);
            return SetForegroundWindow(target.MainWindowHandle);
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
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
