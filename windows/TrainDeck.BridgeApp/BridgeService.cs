using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TrainDeck.BridgeApp;

internal sealed class BridgeService : IDisposable
{
    private readonly KeyboardProfile profile;
    private readonly Dictionary<string, AxisLog> axisLogState = new(StringComparer.OrdinalIgnoreCase);
    private readonly TswAxisMapper axisMapper;
    private readonly TswHttpApiClient tswApi;
    private bool lastAutoTargetActive;
    private CancellationTokenSource? cts;
    private UdpClient? udp;

    public BridgeService(KeyboardProfile profile)
    {
        this.profile = profile;
        axisMapper = new TswAxisMapper(message => LogInfo(message));
        tswApi = new TswHttpApiClient(message => LogInfo(message));
        tswApi.StatusChanged += (_, e) => ApiStatusChanged?.Invoke(this, e);
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
                break;
            case "button":
                HandleButton(message);
                break;
            case "axis":
                HandleAxis(message);
                break;
        }
    }

    private void HandleButton(TrainDeckMessage message)
    {
        var state = message.State ?? "";
        var command = message.Command ?? "";
        var label = message.Label ?? command;
        var mapped = profile.Buttons.TryGetValue(command, out var binding);
        var target = mapped ? binding!.Key : "unmapped";

        LogInfo($"button {state,-4} {command,-18} ({label}) -> {target}");

        if (!ShouldSendKeyboard() || !mapped)
        {
            return;
        }

        var focused = ForegroundAppDetector.FocusTrainSimWorld();
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

    private void HandleAxis(TrainDeckMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Control) || message.Value is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var value = message.Value.Value;

        var handledByApi = tswApi.IsReady && tswApi.IsAxisMapped(message.Control);
        if (handledByApi && tswApi.TryMapAxis(message.Control, value, out var apiCommand))
        {
            _ = tswApi.SendAxisAsync(apiCommand);
            LogInfo($"api    axis {message.Control,-12} -> {apiCommand.ControlName} {apiCommand.Value:0.000}");
        }

        if (!handledByApi && ShouldSendKeyboard())
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

    private void LogInfo(string message) => Log?.Invoke(this, new BridgeLogEventArgs("INFO", message));
    private void LogWarn(string message) => Log?.Invoke(this, new BridgeLogEventArgs("WARN", message));
    private void LogError(string message) => Log?.Invoke(this, new BridgeLogEventArgs("ERROR", message));

    private bool ShouldSendKeyboard()
    {
        if (KeyboardEnabled)
        {
            return true;
        }

        if (!AutoArmTrainSimWorld)
        {
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
}

internal sealed record BridgeLogEventArgs(string Level, string Message);
internal sealed record BridgeStatusEventArgs(bool Running, int Port, IPEndPoint? LastRemote);
internal sealed record AxisLog(double Value, DateTimeOffset At);
