using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TrainDeck.BridgeApp;

internal static class TswSteamLauncher
{
    private const string Tsw6AppId = "3656800";
    private const string HttpApiLaunchOption = "-HTTPAPI";

    public static string LaunchWithHttpApi()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "steam://run/3656800//-HTTPAPI/",
            UseShellExecute = true
        });

        return "Launch requested through Steam with -HTTPAPI. If TSW was already running, close it fully and press this again.";
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
        if (Regex.IsMatch(block, "\"LaunchOptions\"\\s+\"[^\"]*-HTTPAPI[^\"]*\"", RegexOptions.IgnoreCase))
        {
            return $"TSW6 Steam launch option already includes {HttpApiLaunchOption}. Restart TSW if it is already running.";
        }

        var updatedBlock = Regex.IsMatch(block, "\"LaunchOptions\"\\s+\"([^\"]*)\"")
            ? Regex.Replace(block, "\"LaunchOptions\"\\s+\"([^\"]*)\"", match =>
            {
                var existing = match.Groups[1].Value.Trim();
                var value = string.IsNullOrWhiteSpace(existing) ? HttpApiLaunchOption : $"{existing} {HttpApiLaunchOption}";
                return $"\"LaunchOptions\"\t\t\"{value}\"";
            }, RegexOptions.None, TimeSpan.FromSeconds(1))
            : InsertLaunchOption(block);

        var backup = $"{localConfig}.traindeck-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(localConfig, backup, overwrite: false);
        File.WriteAllText(localConfig, string.Concat(text.AsSpan(0, start), updatedBlock, text.AsSpan(end)));

        var steamRunning = Process.GetProcessesByName("steam").Length > 0;
        return steamRunning
            ? $"Wrote {HttpApiLaunchOption} to TSW6 Steam launch options and backed up {Path.GetFileName(backup)}. Because Steam is running, restart TSW; if Steam overwrites it, exit Steam once and press this again."
            : $"Wrote {HttpApiLaunchOption} to TSW6 Steam launch options and backed up {Path.GetFileName(backup)}.";
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
                || block.Contains($"\"{Tsw6AppId}_eula_", StringComparison.Ordinal))
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
        var inserted = $"\t\t\t\t\t\t\"LaunchOptions\"\t\t\"{HttpApiLaunchOption}\"{lineEnding}";
        return block.Insert(closeIndex, inserted);
    }
}
