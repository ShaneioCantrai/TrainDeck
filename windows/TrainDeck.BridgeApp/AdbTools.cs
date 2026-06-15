using System.Diagnostics;

namespace TrainDeck.BridgeApp;

internal static class AdbTools
{
    public static string AdbPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        }
    }

    public static async Task<string> LaunchTrainDeckAsync(CancellationToken token = default)
    {
        if (!File.Exists(AdbPath))
        {
            return $"adb.exe not found at {AdbPath}";
        }

        var devices = await RunAdbAsync("devices", token);
        var serial = devices.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[1].Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0].Trim())
            .OrderByDescending(value => value.Contains(':'))
            .FirstOrDefault();

        if (serial is null)
        {
            return "No authorized Android device found. Open TrainDeck manually on the tablet, or connect USB/wireless ADB.";
        }

        var result = await RunAdbAsync(
            $"-s {serial} shell monkey -p ca.maplevibe.traindeck -c android.intent.category.LAUNCHER 1",
            token);
        return $"Launch sent to {serial}. {result.Trim()}";
    }

    private static async Task<string> RunAdbAsync(string arguments, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AdbPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return "Unable to start adb.";
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        return string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}";
    }
}

