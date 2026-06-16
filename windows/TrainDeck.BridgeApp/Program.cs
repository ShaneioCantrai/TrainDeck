namespace TrainDeck.BridgeApp;

static class Program
{
    [STAThread]
    static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--snapshot-cab", StringComparison.OrdinalIgnoreCase)))
        {
            return await SnapshotCabAsync();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }    

    private static async Task<int> SnapshotCabAsync()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrainDeck",
            "bridge.log");

        void Log(string message)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] {message}{Environment.NewLine}");
        }

        using var client = new TswHttpApiClient(Log);
        var result = await client.SnapshotCabAsync();
        Log(result);
        return result.StartsWith("Cab snapshot saved:", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
