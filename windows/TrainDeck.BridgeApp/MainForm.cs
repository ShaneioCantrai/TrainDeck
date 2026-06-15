using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TrainDeck.BridgeApp;

internal sealed class MainForm : Form
{
    private readonly KeyboardProfile profile;
    private readonly BridgeService bridge;
    private readonly Label statusLabel = new();
    private readonly Label addressLabel = new();
    private readonly Label tabletLabel = new();
    private readonly Label apiLabel = new();
    private readonly NumericUpDown portInput = new();
    private readonly Button startStopButton = new();
    private readonly CheckBox keyboardCheck = new();
    private readonly CheckBox autoArmCheck = new();
    private readonly Button launchTabletButton = new();
    private readonly Button launchTswApiButton = new();
    private readonly Button setTswApiOptionButton = new();
    private readonly Button probeApiButton = new();
    private readonly Button snapshotCabButton = new();
    private readonly Button openProfileButton = new();
    private readonly Button toggleLogButton = new();
    private readonly TextBox logBox = new();
    private bool logVisible;
    private readonly string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TrainDeck",
        "bridge.log");

    public MainForm()
    {
        Text = "TrainDeck Bridge";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 520);
        Size = new Size(1100, 680);
        BackColor = Color.FromArgb(16, 20, 24);
        ForeColor = Color.FromArgb(232, 236, 239);
        Font = new Font("Segoe UI", 10F);

        profile = KeyboardProfile.LoadOrCreate(KeyboardProfile.DefaultPath);
        bridge = new BridgeService(profile);
        bridge.Log += OnBridgeLog;
        bridge.StatusChanged += OnBridgeStatusChanged;
        bridge.ApiStatusChanged += OnApiStatusChanged;

        BuildUi();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await StartBridgeAsync();
        EnsureTswApiLaunchOption();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        bridge.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        Controls.Add(root);

        var title = new Label
        {
            Text = "TrainDeck Bridge",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 22F),
            ForeColor = Color.FromArgb(232, 236, 239),
            Margin = new Padding(0, 0, 0, 4)
        };
        root.Controls.Add(title);
        root.SetRow(title, 0);

        var topPanel = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.None,
            SplitterWidth = 10,
            Margin = new Padding(0, 8, 0, 12),
            BackColor = BackColor
        };
        root.Controls.Add(topPanel);
        root.SetRow(topPanel, 1);

        var statusPanel = MakePanel();
        statusPanel.Resize += (_, _) =>
        {
            apiLabel.MaximumSize = new Size(Math.Max(260, statusPanel.ClientSize.Width - statusPanel.Padding.Horizontal - 8), 0);
        };
        topPanel.Panel1.Controls.Add(statusPanel);

        statusLabel.Text = "Starting";
        statusLabel.Font = new Font("Segoe UI Semibold", 15F);
        statusLabel.ForeColor = Color.FromArgb(73, 160, 120);
        statusLabel.AutoSize = true;
        statusPanel.Controls.Add(statusLabel);

        addressLabel.Text = $"PC bridge address: {GetLanAddress()}:47331";
        addressLabel.AutoSize = true;
        addressLabel.Margin = new Padding(0, 8, 0, 0);
        statusPanel.Controls.Add(addressLabel);

        tabletLabel.Text = "Tablet: waiting";
        tabletLabel.AutoSize = true;
        tabletLabel.Margin = new Padding(0, 4, 0, 0);
        statusPanel.Controls.Add(tabletLabel);

        apiLabel.Text = "TSW API: checking";
        apiLabel.AutoSize = true;
        apiLabel.MaximumSize = new Size(560, 0);
        apiLabel.Margin = new Padding(0, 4, 0, 0);
        apiLabel.ForeColor = Color.FromArgb(218, 198, 103);
        statusPanel.Controls.Add(apiLabel);

        var controlPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.FromArgb(27, 32, 37)
        };
        controlPanel.AutoScroll = true;
        topPanel.Panel2.Controls.Add(controlPanel);

        void ApplyResponsiveLayout()
        {
            var narrow = ClientSize.Width < 980;
            topPanel.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
            topPanel.SplitterDistance = narrow
                ? Math.Min(Math.Max(136, statusPanel.PreferredSize.Height + 12), Math.Max(136, topPanel.Height - 214))
                : Math.Max(320, topPanel.Width / 2);

            root.RowStyles[2].Height = logVisible ? (narrow ? 96 : 120) : 0;
            controlPanel.Padding = new Padding(narrow ? 12 : 16);
            statusPanel.Padding = new Padding(narrow ? 12 : 16);
            root.PerformLayout();
        }

        topPanel.Resize += (_, _) => ApplyResponsiveLayout();
        Resize += (_, _) => ApplyResponsiveLayout();
        Shown += (_, _) => ApplyResponsiveLayout();

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = false,
            WrapContents = true,
            Dock = DockStyle.Top,
            Height = 78
        };
        row.Resize += (_, _) => row.Height = Math.Max(76, row.PreferredSize.Height + 6);

        row.Controls.Add(new Label
        {
            Text = "UDP port",
            AutoSize = true,
            Padding = new Padding(0, 8, 6, 0),
            ForeColor = ForeColor
        });

        portInput.Minimum = 1024;
        portInput.Maximum = 65535;
        portInput.Value = 47331;
        portInput.Width = 92;
        row.Controls.Add(portInput);

        startStopButton.Text = "Stop Bridge";
        startStopButton.Width = 120;
        startStopButton.Height = 34;
        startStopButton.Click += async (_, _) =>
        {
            if (bridge.IsRunning)
            {
                bridge.Stop();
            }
            else
            {
                await StartBridgeAsync();
            }
        };
        row.Controls.Add(startStopButton);

        keyboardCheck.Text = "Arm TSW6 keyboard output";
        keyboardCheck.AutoSize = true;
        keyboardCheck.Padding = new Padding(8, 7, 0, 0);
        keyboardCheck.ForeColor = Color.FromArgb(232, 236, 239);
        keyboardCheck.CheckedChanged += (_, _) =>
        {
            bridge.KeyboardEnabled = keyboardCheck.Checked;
            AppendLog(keyboardCheck.Checked
                ? "TSW6 keyboard output armed. Focus Train Sim World before moving controls."
                : "Keyboard output disarmed.");
        };
        row.Controls.Add(keyboardCheck);

        autoArmCheck.Text = "Auto-arm for TSW6";
        autoArmCheck.AutoSize = true;
        autoArmCheck.Checked = true;
        autoArmCheck.Padding = new Padding(8, 7, 0, 0);
        autoArmCheck.ForeColor = Color.FromArgb(232, 236, 239);
        autoArmCheck.CheckedChanged += (_, _) =>
        {
            bridge.AutoArmTrainSimWorld = autoArmCheck.Checked;
            AppendLog(autoArmCheck.Checked
                ? "Auto-arm enabled for TrainSimWorld foreground window."
                : "Auto-arm disabled.");
        };
        row.Controls.Add(autoArmCheck);

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = false,
            WrapContents = true,
            Dock = DockStyle.Top,
            Height = 90,
            Margin = new Padding(0, 12, 0, 0)
        };
        buttonRow.Resize += (_, _) => buttonRow.Height = Math.Max(84, buttonRow.PreferredSize.Height + 6);

        launchTabletButton.Text = "Launch Tablet App";
        launchTabletButton.Width = 150;
        launchTabletButton.Height = 34;
        launchTabletButton.Click += async (_, _) =>
        {
            launchTabletButton.Enabled = false;
            try
            {
                AppendLog(await AdbTools.LaunchTrainDeckAsync());
            }
            finally
            {
                launchTabletButton.Enabled = true;
            }
        };
        buttonRow.Controls.Add(launchTabletButton);

        launchTswApiButton.Text = "Launch TSW";
        launchTswApiButton.Width = 125;
        launchTswApiButton.Height = 34;
        launchTswApiButton.Click += (_, _) =>
        {
            try
            {
                AppendLog(TswSteamLauncher.LaunchWithHttpApi());
            }
            catch (Exception ex)
            {
                AppendLog($"Could not launch TSW API mode: {ex.Message}");
            }
        };
        buttonRow.Controls.Add(launchTswApiButton);

        setTswApiOptionButton.Text = "Enable TSW API";
        setTswApiOptionButton.Width = 145;
        setTswApiOptionButton.Height = 34;
        setTswApiOptionButton.Click += (_, _) =>
        {
            try
            {
                AppendLog(TswSteamLauncher.EnsureHttpApiLaunchOption());
            }
            catch (Exception ex)
            {
                AppendLog($"Could not set TSW API launch option: {ex.Message}");
            }
        };
        buttonRow.Controls.Add(setTswApiOptionButton);

        probeApiButton.Text = "Probe TSW API";
        probeApiButton.Width = 130;
        probeApiButton.Height = 34;
        probeApiButton.Click += async (_, _) =>
        {
            probeApiButton.Enabled = false;
            try
            {
                AppendLog($"TSW API probe: {await bridge.ProbeApiAsync()}");
            }
            finally
            {
                probeApiButton.Enabled = true;
            }
        };
        buttonRow.Controls.Add(probeApiButton);

        snapshotCabButton.Text = "Cab Snapshot";
        snapshotCabButton.Width = 120;
        snapshotCabButton.Height = 34;
        snapshotCabButton.Click += async (_, _) =>
        {
            snapshotCabButton.Enabled = false;
            try
            {
                AppendLog(await bridge.SnapshotCabAsync());
            }
            finally
            {
                snapshotCabButton.Enabled = true;
            }
        };
        buttonRow.Controls.Add(snapshotCabButton);

        openProfileButton.Text = "Open Key Profile";
        openProfileButton.Width = 150;
        openProfileButton.Height = 34;
        openProfileButton.Click += (_, _) => OpenProfile();
        buttonRow.Controls.Add(openProfileButton);

        toggleLogButton.Text = "Show Log";
        toggleLogButton.Width = 110;
        toggleLogButton.Height = 34;
        toggleLogButton.Click += (_, _) =>
        {
            logVisible = !logVisible;
            logBox.Visible = logVisible;
            toggleLogButton.Text = logVisible ? "Hide Log" : "Show Log";
            ApplyResponsiveLayout();
        };
        buttonRow.Controls.Add(toggleLogButton);

        controlPanel.Controls.Add(buttonRow);
        controlPanel.Controls.Add(row);

        logBox.Dock = DockStyle.Fill;
        logBox.MinimumSize = new Size(0, 44);
        logBox.Multiline = true;
        logBox.Visible = false;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.ReadOnly = true;
        logBox.BackColor = Color.FromArgb(10, 13, 16);
        logBox.ForeColor = Color.FromArgb(218, 226, 233);
        logBox.BorderStyle = BorderStyle.FixedSingle;
        logBox.Font = new Font("Cascadia Mono", 10F);
        root.Controls.Add(logBox);
        root.SetRow(logBox, 2);

        AppendLog("Ready. Open TrainDeck on the tablet, or use Launch Tablet App if ADB is connected.");
        AppendLog($"Keyboard profile: {profile.Path}");
        AppendLog($"Log file: {logPath}");
    }

    private async Task StartBridgeAsync()
    {
        try
        {
            await bridge.StartAsync((int)portInput.Value);
            startStopButton.Text = "Stop Bridge";
            portInput.Enabled = false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            AppendLog($"Port {(int)portInput.Value} is already in use. Stop the other bridge, or choose a different port.");
            statusLabel.Text = "Port busy";
            statusLabel.ForeColor = Color.FromArgb(206, 84, 65);
            startStopButton.Text = "Start Bridge";
            portInput.Enabled = true;
        }
        catch (Exception ex)
        {
            AppendLog($"Unable to start bridge: {ex.Message}");
            statusLabel.Text = "Stopped";
            statusLabel.ForeColor = Color.FromArgb(206, 84, 65);
            startStopButton.Text = "Start Bridge";
            portInput.Enabled = true;
        }
    }

    private void OnBridgeLog(object? sender, BridgeLogEventArgs e)
    {
        AppendLog($"[{e.Level}] {e.Message}");
    }

    private void OnBridgeStatusChanged(object? sender, BridgeStatusEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnBridgeStatusChanged(sender, e));
            return;
        }

        statusLabel.Text = e.Running ? "Listening" : "Stopped";
        statusLabel.ForeColor = e.Running ? Color.FromArgb(73, 160, 120) : Color.FromArgb(206, 84, 65);
        addressLabel.Text = $"PC bridge address: {GetLanAddress()}:{e.Port}";
        tabletLabel.Text = e.LastRemote is null ? "Tablet: waiting" : $"Tablet: {e.LastRemote.Address}";
        startStopButton.Text = e.Running ? "Stop Bridge" : "Start Bridge";
        portInput.Enabled = !e.Running;
    }

    private void OnApiStatusChanged(object? sender, TswHttpApiStatusEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnApiStatusChanged(sender, e));
            return;
        }

        apiLabel.Text = e.Ready ? $"TSW API: {e.Status}" : $"TSW API: {e.Status}";
        apiLabel.ForeColor = e.Ready
            ? Color.FromArgb(73, 160, 120)
            : Color.FromArgb(218, 198, 103);
        launchTswApiButton.Text = e.Ready ? "Launch TSW" : "Launch TSW API";
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // UI logging should never be blocked by a file-system issue.
        }
    }

    private void OpenProfile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(profile.Path) ?? ".");
        if (!File.Exists(profile.Path))
        {
            File.WriteAllText(profile.Path, "{}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = profile.Path,
            UseShellExecute = true
        });
    }

    private void EnsureTswApiLaunchOption()
    {
        try
        {
            var status = TswSteamLauncher.GetLaunchOptionStatus();
            if (status.Enabled)
            {
                AppendLog(status.Message);
                return;
            }

            AppendLog(TswSteamLauncher.EnsureHttpApiLaunchOption());
        }
        catch (Exception ex)
        {
            AppendLog($"Could not verify TSW API launch option: {ex.Message}");
        }
    }

    private static FlowLayoutPanel MakePanel()
    {
        return new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.FromArgb(27, 32, 37),
            WrapContents = false
        };
    }

    private static string GetLanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(ip.Address)
                    && IsPrivateIpv4(ip.Address))
                {
                    return ip.Address.ToString();
                }
            }
        }

        return Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            ?.ToString() ?? "127.0.0.1";
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31
            || bytes[0] == 192 && bytes[1] == 168;
    }
}
