using System.Diagnostics;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace TrainDeck.BridgeApp;

internal static class TswSteamLauncher
{
    private const string Tsw6AppId = "3656800";
    private const string HttpApiLaunchOption = "-HTTPAPI";
    private const string LaunchOptionsPattern = "(?m)^(?<indent>\\s*)\"LaunchOptions\"\\s+\"(?<value>[^\"]*)\"\\s*$";

    public static async Task<string> LaunchWithHttpApiAsync()
    {
        var optionStatus = GetLaunchOptionStatus();
        var prefix = optionStatus.Enabled
            ? "TSW6 Steam launch option already includes -HTTPAPI."
            : "Passing -HTTPAPI directly for this launch.";
        var running = IsTrainSimWorldRunning();
        if (running)
        {
            return $"{prefix} TSW is already running, so Steam may not apply API mode to this session. Close TSW fully, then press Launch TSW again.";
        }

        var steamExe = FindSteamExe();
        if (steamExe is not null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                Arguments = $"-applaunch {Tsw6AppId} {HttpApiLaunchOption}",
                UseShellExecute = false
            });

            if (await WaitForTrainSimWorldAsync(TimeSpan.FromSeconds(12)))
            {
                return $"{prefix} Launch requested through steam.exe.";
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = $"steam://rungameid/{Tsw6AppId}",
            UseShellExecute = true
        });

        if (await WaitForTrainSimWorldAsync(TimeSpan.FromSeconds(12)))
        {
            return steamExe is null
                ? $"{prefix} Launch requested through the Steam URL handler."
                : $"{prefix} steam.exe did not start TSW, but the Steam URL fallback did.";
        }

        return $"{prefix} Launch was requested, but no TrainSimWorld process appeared within 24 seconds. Try launching TSW from Steam once; TrainDeck will attach when the API is ready.";
    }

    public static string EnsureHttpApiLaunchOption()
    {
        var localConfig = FindSteamLocalConfig();
        if (localConfig is null)
        {
            return "Could not find Steam localconfig.vdf.";
        }

        var text = File.ReadAllText(localConfig);
        var appBlock = FindAppBlock(text);
        if (appBlock is null)
        {
            return $"Could not find TSW6 app {Tsw6AppId} in {localConfig}.";
        }

        var (start, end) = appBlock.Value;
        var block = text[start..end];
        var launchOptionsMatch = Regex.Match(block, LaunchOptionsPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var updatedBlock = launchOptionsMatch.Success
            ? Regex.Replace(block, LaunchOptionsPattern, match =>
            {
                var existing = match.Groups["value"].Value.Trim();
                var value = ContainsHttpApiOption(existing)
                    ? existing
                    : string.IsNullOrWhiteSpace(existing) ? HttpApiLaunchOption : $"{existing} {HttpApiLaunchOption}";
                return $"{GetPropertyIndent(block)}\"LaunchOptions\"\t\t\"{value}\"";
            }, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            : InsertLaunchOption(block);
        updatedBlock = NormalizeClosingBraceIndent(updatedBlock);

        if (updatedBlock == block)
        {
            return $"TSW6 Steam launch option already includes {HttpApiLaunchOption}.";
        }

        var backup = $"{localConfig}.traindeck-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(localConfig, backup, overwrite: false);
        File.WriteAllText(localConfig, string.Concat(text.AsSpan(0, start), updatedBlock, text.AsSpan(end)));

        var steamRunning = Process.GetProcessesByName("steam").Length > 0;
        return steamRunning
            ? $"Wrote {HttpApiLaunchOption} to TSW6 Steam launch options and backed up {Path.GetFileName(backup)}. Steam is running and may overwrite this; fully exit Steam first if the setting does not stick."
            : $"Wrote {HttpApiLaunchOption} to TSW6 Steam launch options and backed up {Path.GetFileName(backup)}.";
    }

    public static TswLaunchOptionStatus GetLaunchOptionStatus()
    {
        var localConfig = FindSteamLocalConfig();
        if (localConfig is null)
        {
            return new TswLaunchOptionStatus(false, false, "Could not find Steam localconfig.vdf.");
        }

        var text = File.ReadAllText(localConfig);
        var appBlock = FindAppBlock(text);
        if (appBlock is null)
        {
            return new TswLaunchOptionStatus(true, false, $"Could not find TSW6 app {Tsw6AppId} in Steam local config.");
        }

        var (start, end) = appBlock.Value;
        var block = text[start..end];
        var enabled = Regex.IsMatch(block, "\"LaunchOptions\"\\s+\"[^\"]*-HTTPAPI[^\"]*\"", RegexOptions.IgnoreCase);
        return new TswLaunchOptionStatus(true, enabled, enabled
            ? "TSW API launch option is enabled."
            : "TSW API launch option is not enabled.");
    }

    public static bool IsTrainSimWorldRunning()
    {
        return Process.GetProcessesByName("TrainSimWorld").Length > 0
            || Process.GetProcessesByName("TrainSimWorld-Win64-Shipping").Length > 0;
    }

    private static async Task<bool> WaitForTrainSimWorldAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (IsTrainSimWorldRunning())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static string? FindSteamLocalConfig()
    {
        var steamRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam",
            "userdata");
        if (!Directory.Exists(steamRoot))
        {
            return null;
        }

        return Directory.EnumerateFiles(steamRoot, "localconfig.vdf", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(path => File.ReadAllText(path).Contains($"\"{Tsw6AppId}\"", StringComparison.Ordinal));
    }

    private static string? FindSteamExe()
    {
        var standardPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam",
            "steam.exe");
        if (File.Exists(standardPath))
        {
            return standardPath;
        }

        foreach (var registryPath in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Valve\Steam"
        })
        {
            var steamExe = Registry.GetValue(registryPath, "SteamExe", null) as string;
            if (!string.IsNullOrWhiteSpace(steamExe) && File.Exists(steamExe))
            {
                return steamExe;
            }

            var installPath = Registry.GetValue(registryPath, "InstallPath", null) as string;
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                var candidate = Path.Combine(installPath, "steam.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static (int Start, int End)? FindAppBlock(string text)
    {
        var appMatch = Regex.Match(text, $"(?m)^\\s+\"{Tsw6AppId}\"\\s*\\r?\\n\\s+\\{{");
        while (appMatch.Success)
        {
            var openBrace = text.IndexOf('{', appMatch.Index + appMatch.Length - 1);
            if (openBrace < 0)
            {
                return null;
            }

            var closeBrace = FindMatchingBrace(text, openBrace);
            if (closeBrace < 0)
            {
                return null;
            }

            var block = text[appMatch.Index..(closeBrace + 1)];
            if (block.Contains("\"LastPlayed\"", StringComparison.Ordinal)
                && (block.Contains($"\"{Tsw6AppId}_eula_", StringComparison.Ordinal)
                    || block.Contains("\"Playtime\"", StringComparison.Ordinal)
                    || block.Contains("\"BadgeData\"", StringComparison.Ordinal)))
            {
                return (appMatch.Index, closeBrace + 1);
            }

            appMatch = appMatch.NextMatch();
        }

        return null;
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string InsertLaunchOption(string block)
    {
        var closeIndex = block.LastIndexOf('}');
        if (closeIndex < 0)
        {
            return block;
        }

        var lineEnding = block.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var closeLineStart = block.LastIndexOf('\n', closeIndex);
        closeLineStart = closeLineStart < 0 ? closeIndex : closeLineStart + 1;
        if (closeLineStart < closeIndex && block[closeLineStart] == '\r')
        {
            closeLineStart++;
        }

        var inserted = $"{GetPropertyIndent(block)}\"LaunchOptions\"\t\t\"{HttpApiLaunchOption}\"{lineEnding}";
        return block.Insert(closeLineStart, inserted);
    }

    private static string GetPropertyIndent(string block)
    {
        foreach (var propertyName in new[] { "LastPlayed", "Playtime", "BadgeData" })
        {
            var match = Regex.Match(block, $"(?m)^(\\s*)\"{propertyName}\"\\s+\"", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        var appKeyIndent = GetAppKeyIndent(block);
        return $"{appKeyIndent}\t";
    }

    private static string GetAppKeyIndent(string block)
    {
        var match = Regex.Match(block, $"(?m)^(\\s*)\"{Tsw6AppId}\"\\s*$", RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string NormalizeClosingBraceIndent(string block)
    {
        var closeIndex = block.LastIndexOf('}');
        if (closeIndex < 0)
        {
            return block;
        }

        var lineStart = block.LastIndexOf('\n', closeIndex);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        if (lineStart < closeIndex && block[lineStart] == '\r')
        {
            lineStart++;
        }

        var expected = GetAppKeyIndent(block);
        var actual = block[lineStart..closeIndex];
        return actual == expected ? block : string.Concat(block.AsSpan(0, lineStart), expected, block.AsSpan(closeIndex));
    }

    private static bool ContainsHttpApiOption(string launchOptions)
    {
        return launchOptions
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(option => string.Equals(option, HttpApiLaunchOption, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record TswLaunchOptionStatus(bool SteamConfigFound, bool Enabled, string Message);
