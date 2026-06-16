using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TrainDeck.BridgeApp;

internal sealed class BridgeService : IDisposable
{
    private static readonly TimeSpan DeckNeutralizeDelay = TimeSpan.FromSeconds(12);
    private static readonly PairedCommand[] PairedCommands =
    [
        new("door_left", "door_left", "door_close_left"),
        new("door_right", "door_right", "door_close_right"),
        new("afb", "afb_on", "afb_off")
    ];

    private readonly KeyboardProfile profile;
    private readonly Dictionary<string, AxisLog> axisLogState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> pairedCommandNextAlternate = new(StringComparer.OrdinalIgnoreCase);
    private readonly TswAxisMapper axisMapper;
    private readonly TswHttpApiClient tswApi;
    private bool lastAutoTargetActive;
    private bool deckNeutralizedSinceApiLost;
    private bool autoAxisSuppressedUntilApiReadyLogged;
    private CancellationTokenSource? pendingDeckNeutralizeCts;
    private CancellationTokenSource? cts;
    private UdpClient? udp;

    public BridgeService(KeyboardProfile profile)
    {
        this.profile = profile;
        axisMapper = new TswAxisMapper(message => LogInfo(message));
        tswApi = new TswHttpApiClient(message => LogInfo(message));
        tswApi.StatusChanged += OnApiStatusChanged;
    }

    public event EventHandler<BridgeLogEventArgs>? Log;
    public event EventHandler<BridgeStatusEventArgs>? StatusChanged;
    public event EventHandler<TswHttpApiStatusEventArgs>? ApiStatusChanged;

    private bool keyboardEnabled;

    public bool KeyboardEnabled
    {
        get => keyboardEnabled;
        set
        {
            if (keyboardEnabled == value)
            {
                return;
            }

            keyboardEnabled = value;
            axisMapper.Reset();
            LogInfo(value
                ? "TSW axis mapper armed; next lever packet will sync without sending keys."
                : "TSW axis mapper disarmed and reset.");
        }
    }
    public bool IsRunning => udp is not null;
    public int Port { get; private set; }
    public IPEndPoint? LastRemote { get; private set; }
    public bool AutoArmTrainSimWorld { get; set; } = true;

    public Task<string> ProbeApiAsync() => tswApi.ProbeNowAsync();
    public Task<string> SnapshotCabAsync() => tswApi.SnapshotCabAsync();

    public async Task StartAsync(int port)
    {
        if (IsRunning)
        {
            return;
        }

        Port = port;
        cts = new CancellationTokenSource();
        udp = new UdpClient(port);
        LogInfo($"Listening on UDP {port}.");
        StatusChanged?.Invoke(this, new BridgeStatusEventArgs(true, port, LastRemote));
        tswApi.Start();
        _ = Task.Run(() => ReceiveLoopAsync(cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        udp?.Dispose();
        udp = null;
        cts?.Dispose();
        cts = null;
        CancelDeckNeutralize();
        tswApi.Stop();
        LogInfo("Bridge stopped.");
        StatusChanged?.Invoke(this, new BridgeStatusEventArgs(false, Port, LastRemote));
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && udp is not null)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogError($"Receive error: {ex.Message}");
                break;
            }

            var text = Encoding.UTF8.GetString(result.Buffer);
            HandleDatagram(text, result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(string text, IPEndPoint remote)
    {
        TrainDeckMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TrainDeckMessage>(text, JsonOptions.Default);
        }
        catch (JsonException)
        {
            LogWarn($"Ignored malformed packet from {remote}.");
            return;
        }

        if (message is null || !string.Equals(message.App, "TrainDeck", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LastRemote = remote;
        StatusChanged?.Invoke(this, new BridgeStatusEventArgs(IsRunning, Port, LastRemote));

        switch (message.Type)
        {
            case "hello":
                LogInfo($"Tablet connected: {message.Device ?? remote.Address.ToString()} at {remote.Address}.");
                _ = SendDeckCapabilitiesAsync();
                if (!tswApi.IsReady)
                {
                    ScheduleDeckNeutralize(tswApi.StatusText);
                }
                break;
            case "button":
                HandleButton(message);
                break;
            case "axis":
                HandleAxis(message);
                break;
            case "pointer":
                HandlePointer(message);
                break;
        }
    }

    private void HandleButton(TrainDeckMessage message)
    {
        var state = message.State ?? "";
        var command = message.Command ?? "";
        var label = message.Label ?? command;

        if (TryHandleMouseButton(command, state, label))
        {
            return;
        }

        var mapped = profile.Buttons.TryGetValue(command, out var binding);
        var handledByApi = tswApi.IsReady && IsButtonHandledByApi(command);
        var target = handledByApi ? "api" : mapped ? binding!.Key : "unmapped";

        LogInfo($"button {state,-4} {command,-18} ({label}) -> {target}");

        if (handledByApi)
        {
            if (string.Equals(state, "down", StringComparison.OrdinalIgnoreCase)
                && tswApi.TryMapButton(ResolveApiButtonCommand(command), out var apiCommand))
            {
                _ = tswApi.SendButtonAsync(apiCommand);
                var controls = string.Join(", ", apiCommand.Steps.Select(step => step.ControlName));
                LogInfo($"api    button {command,-12} -> {apiCommand.SourceCommand}: {controls}");
            }

            return;
        }

        if (!mapped)
        {
            return;
        }

        if (IsUnassignedKey(binding!.Key))
        {
            LogInfo($"send   {binding.Key,-18} skipped; command is not assigned in the keyboard profile.");
            return;
        }

        var focused = EnsureKeyboardTargetForButton();
        if (!focused)
        {
            LogWarn($"send   {binding!.Key,-18} skipped; Train Sim World could not be focused. foreground={ForegroundAppDetector.DescribeForeground()}");
            return;
        }

        if (string.Equals(state, "down", StringComparison.OrdinalIgnoreCase))
        {
            KeyboardOutput.KeyDown(binding!.Key);
        }
        else if (string.Equals(state, "up", StringComparison.OrdinalIgnoreCase))
        {
            KeyboardOutput.KeyUp(binding!.Key);
        }

        LogInfo($"send   {binding!.Key,-18} focusTsw={focused} foreground={ForegroundAppDetector.DescribeForeground()} {KeyboardOutput.LastSummary}");
    }

    private bool IsButtonHandledByApi(string command)
    {
        var pair = FindPairedCommand(command);
        if (pair is not null && string.Equals(command, pair.ToggleCommand, StringComparison.OrdinalIgnoreCase))
        {
            return tswApi.IsButtonMapped(pair.PrimaryCommand) || tswApi.IsButtonMapped(pair.AlternateCommand);
        }

        if (tswApi.IsButtonMapped(command))
        {
            return true;
        }

        return false;
    }

    private string ResolveApiButtonCommand(string command)
    {
        var pair = FindPairedCommand(command);
        if (pair is null)
        {
            return command;
        }

        if (string.Equals(command, pair.ToggleCommand, StringComparison.OrdinalIgnoreCase))
        {
            return TogglePairedCommand(pair);
        }

        UpdatePairAfterExplicitCommand(pair, command);
        return command;
    }

    private string TogglePairedCommand(PairedCommand pair)
    {
        var nextAlternate = pairedCommandNextAlternate.TryGetValue(pair.ToggleCommand, out var stored) && stored;
        var preferred = nextAlternate ? pair.AlternateCommand : pair.PrimaryCommand;
        var fallback = nextAlternate ? pair.PrimaryCommand : pair.AlternateCommand;
        var resolved = tswApi.IsButtonMapped(preferred) || !tswApi.IsButtonMapped(fallback)
            ? preferred
            : fallback;
        UpdatePairAfterExplicitCommand(pair, resolved);
        return resolved;
    }

    private void UpdatePairAfterExplicitCommand(PairedCommand pair, string command)
    {
        pairedCommandNextAlternate[pair.ToggleCommand] =
            string.Equals(command, pair.PrimaryCommand, StringComparison.OrdinalIgnoreCase);
    }

    private static PairedCommand? FindPairedCommand(string command)
    {
        return PairedCommands.FirstOrDefault(pair =>
            string.Equals(command, pair.ToggleCommand, StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, pair.PrimaryCommand, StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, pair.AlternateCommand, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryHandleMouseButton(string command, string state, string label)
    {
        var button = command.ToLowerInvariant() switch
        {
            "mouse_left" => "left",
            "mouse_right" => "right",
            "mouse_middle" => "middle",
            _ => ""
        };

        if (button.Length == 0)
        {
            return false;
        }

        var focused = EnsureKeyboardTargetForButton();
        if (!focused)
        {
            LogWarn($"mouse  {state,-4} {command,-18} ({label}) skipped; Train Sim World could not be focused. foreground={ForegroundAppDetector.DescribeForeground()}");
            return true;
        }

        if (string.Equals(state, "down", StringComparison.OrdinalIgnoreCase))
        {
            KeyboardOutput.MouseButtonDown(button);
        }
        else if (string.Equals(state, "up", StringComparison.OrdinalIgnoreCase))
        {
            KeyboardOutput.MouseButtonUp(button);
        }

        LogInfo($"mouse  {state,-4} {command,-18} ({label}) focusTsw={focused} foreground={ForegroundAppDetector.DescribeForeground()} {KeyboardOutput.LastSummary}");
        return true;
    }

    private bool EnsureKeyboardTargetForButton()
    {
        if (KeyboardEnabled)
        {
            return ForegroundAppDetector.IsTrainSimWorldForeground()
                || ForegroundAppDetector.FocusTrainSimWorld();
        }

        if (!AutoArmTrainSimWorld)
        {
            return false;
        }

        var active = ForegroundAppDetector.IsTrainSimWorldForeground();
        if (!active)
        {
            active = ForegroundAppDetector.FocusTrainSimWorld();
        }

        if (active != lastAutoTargetActive)
        {
            lastAutoTargetActive = active;
            axisMapper.Reset();
            LogInfo(active
                ? "Auto-armed: Train Sim World is foreground. Next lever packet syncs without sending keys."
                : "Auto-disarmed: Train Sim World is not foreground.");
        }

        return active;
    }

    private void HandleAxis(TrainDeckMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Control) || message.Value is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var value = message.Value.Value;

        var handledByApi = !KeyboardEnabled && tswApi.IsReady && tswApi.IsAxisMapped(message.Control);
        if (handledByApi && tswApi.TryMapAxis(message.Control, value, out var apiCommand))
        {
            _ = tswApi.SendAxisAsync(apiCommand);
            var values = string.Join(", ", apiCommand.Outputs.Select(output => $"{output.ControlName}={output.Value:0.000}"));
            LogInfo($"api    axis {message.Control,-12} -> {values}");
        }

        if (!handledByApi && ShouldSendKeyboardAxis())
        {
            var focused = ForegroundAppDetector.FocusTrainSimWorld();
            KeyboardOutput.ClearLastSummary();
            axisMapper.HandleAxis(message.Control, value);
            if (KeyboardOutput.LastSummary != "No key sent yet.")
            {
                LogInfo($"send   axis {message.Control,-12} focusTsw={focused} foreground={ForegroundAppDetector.DescribeForeground()} {KeyboardOutput.LastSummary}");
            }
        }

        if (!axisLogState.TryGetValue(message.Control, out var previous)
            || Math.Abs(previous.Value - value) >= 0.025
            || now - previous.At > TimeSpan.FromSeconds(1))
        {
            axisLogState[message.Control] = new AxisLog(value, now);
            LogInfo($"axis   {message.Control,-18} {value,7:0.000}");
        }
    }

    private void HandlePointer(TrainDeckMessage message)
    {
        var dx = message.DeltaX ?? 0;
        var dy = message.DeltaY ?? 0;
        if (Math.Abs(dx) < 0.1 && Math.Abs(dy) < 0.1)
        {
            return;
        }

        var focused = EnsureKeyboardTargetForButton();
        if (!focused)
        {
            LogWarn($"pointer move skipped; Train Sim World could not be focused. foreground={ForegroundAppDetector.DescribeForeground()}");
            return;
        }

        var moveX = (int)Math.Round(Math.Max(-80, Math.Min(80, dx)));
        var moveY = (int)Math.Round(Math.Max(-80, Math.Min(80, dy)));
        KeyboardOutput.MouseMove(moveX, moveY);
        LogInfo($"pointer move {moveX,4},{moveY,4} focusTsw={focused} foreground={ForegroundAppDetector.DescribeForeground()} {KeyboardOutput.LastSummary}");
    }

    private void LogInfo(string message) => Log?.Invoke(this, new BridgeLogEventArgs("INFO", message));
    private void LogWarn(string message) => Log?.Invoke(this, new BridgeLogEventArgs("WARN", message));
    private void LogError(string message) => Log?.Invoke(this, new BridgeLogEventArgs("ERROR", message));

    private static bool IsUnassignedKey(string key)
    {
        return string.IsNullOrWhiteSpace(key)
            || string.Equals(key, "Unassigned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "None", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldSendKeyboardAxis()
    {
        if (KeyboardEnabled)
        {
            return true;
        }

        if (!AutoArmTrainSimWorld)
        {
            return false;
        }

        if (!tswApi.IsReady)
        {
            if (!autoAxisSuppressedUntilApiReadyLogged)
            {
                autoAxisSuppressedUntilApiReadyLogged = true;
                axisMapper.Reset();
                LogInfo("Auto-arm axis output waiting for TSW HTTP API. Use explicit keyboard output to force key-based lever fallback.");
            }

            return false;
        }

        var active = ForegroundAppDetector.IsTrainSimWorldForeground();
        if (active != lastAutoTargetActive)
        {
            lastAutoTargetActive = active;
            axisMapper.Reset();
            LogInfo(active
                ? "Auto-armed: Train Sim World is foreground. Next lever packet syncs without sending keys."
                : "Auto-disarmed: Train Sim World is not foreground.");
        }

        return active;
    }

    public void Dispose()
    {
        Stop();
        tswApi.Dispose();
    }

    private void OnApiStatusChanged(object? sender, TswHttpApiStatusEventArgs e)
    {
        ApiStatusChanged?.Invoke(this, e);

        if (e.Ready)
        {
            autoAxisSuppressedUntilApiReadyLogged = false;
            deckNeutralizedSinceApiLost = false;
            pairedCommandNextAlternate.Clear();
            CancelDeckNeutralize();
            _ = SendDeckCapabilitiesAsync();
            return;
        }

        if (LastRemote is null)
        {
            axisMapper.Reset();
            return;
        }

        if (deckNeutralizedSinceApiLost)
        {
            return;
        }

        ScheduleDeckNeutralize(e.Status);
    }

    private void ScheduleDeckNeutralize(string reason)
    {
        if (LastRemote is null || deckNeutralizedSinceApiLost)
        {
            return;
        }

        CancelDeckNeutralize();
        pendingDeckNeutralizeCts = new CancellationTokenSource();
        var token = pendingDeckNeutralizeCts.Token;
        _ = Task.Run(() => NeutralizeDeckAfterDelayAsync(reason, token), token);
    }

    private async Task NeutralizeDeckAfterDelayAsync(string reason, CancellationToken token)
    {
        try
        {
            await Task.Delay(DeckNeutralizeDelay, token);
            while (!token.IsCancellationRequested && !tswApi.IsReady)
            {
                var tswRunning = ForegroundAppDetector.IsTrainSimWorldRunning();
                if (!tswRunning || ForegroundAppDetector.IsTrainSimWorldForeground())
                {
                    deckNeutralizedSinceApiLost = true;
                    axisMapper.Reset();
                    await SendDeckCommandAsync("reset_axes", reason);
                    return;
                }

                LogInfo($"tablet reset held; TSW is backgrounded. foreground={ForegroundAppDetector.DescribeForeground()}");
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelDeckNeutralize()
    {
        pendingDeckNeutralizeCts?.Cancel();
        pendingDeckNeutralizeCts?.Dispose();
        pendingDeckNeutralizeCts = null;
    }

    private async Task SendDeckCommandAsync(string type, string reason)
    {
        if (udp is null || LastRemote is null)
        {
            return;
        }

        var payload = new TrainDeckBridgeMessage
        {
            Type = type,
            Reason = reason,
            At = Environment.TickCount64
        };

        try
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions.Default));
            await udp.SendAsync(body, body.Length, LastRemote);
            LogInfo($"tablet command {type} -> {LastRemote.Address} ({reason})");
        }
        catch (Exception ex)
        {
            LogWarn($"tablet command {type} failed: {ex.Message}");
        }
    }

    private async Task SendDeckCapabilitiesAsync()
    {
        if (udp is null || LastRemote is null)
        {
            return;
        }

        var axes = new[] { "reverser", "throttle", "dynamic_brake", "train_brake", "independent_brake", "afb" }
            .Where(tswApi.IsAxisMapped)
            .ToList();
        var buttons = new[]
        {
            "door_left",
            "door_right",
            "door_close_left",
            "door_close_right",
            "afb",
            "afb_on",
            "afb_off",
            "power_change_ctrl",
            "power_change_dc",
            "wipers",
            "tail_lights",
            "marker_lights"
        }
            .Where(IsButtonHandledByApi)
            .ToList();

        var payload = new TrainDeckBridgeMessage
        {
            Type = "capabilities",
            Axes = axes,
            AxisOptions = tswApi.GetAxisOptions(),
            Buttons = buttons,
            At = Environment.TickCount64
        };

        try
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions.Default));
            await udp.SendAsync(body, body.Length, LastRemote);
            LogInfo($"tablet capabilities -> {LastRemote.Address} axes=[{string.Join(",", axes)}] buttons=[{string.Join(",", buttons)}]");
        }
        catch (Exception ex)
        {
            LogWarn($"tablet capabilities failed: {ex.Message}");
        }
    }
}

internal sealed record BridgeLogEventArgs(string Level, string Message);
internal sealed record BridgeStatusEventArgs(bool Running, int Port, IPEndPoint? LastRemote);
internal sealed record AxisLog(double Value, DateTimeOffset At);
internal sealed record PairedCommand(string ToggleCommand, string PrimaryCommand, string AlternateCommand);
