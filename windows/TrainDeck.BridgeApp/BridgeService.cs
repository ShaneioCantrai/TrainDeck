using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace TrainDeck.BridgeApp;

internal sealed class BridgeService : IDisposable
{
    private static readonly TimeSpan DeckNeutralizeDelay = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(500);
    private const double SpeedHoldNeutral = 0.5;
    private const double SpeedHoldDeadbandKmh = 1.0;
    private const double SpeedHoldMinTargetKmh = 0.0;
    private const double SpeedHoldMaxTargetKmh = 250.0;
    private const double SpeedHoldPowerMax = 1.0;
    private const double SpeedHoldFeatherHoldKmh = 0.2;
    private const double SpeedHoldFeatherWindowKmh = 4.0;
    private const double SpeedHoldFullPowerErrorKmh = 8.0;
    private const double AutoPilotLimitBufferKmh = 1.0;
    private const double AutoPilotBrakeDecelMps2 = 0.62;
    private const double AutoPilotReactionSeconds = 2.0;
    private const double AutoPilotBrakeMarginMeters = 25.0;
    private const double AutoPilotStopSignalBufferMeters = 15.0;
    private const double AutoPilotUpcomingLimitHoldWindowKmh = 3.0;
    private const double ScenarioResetStoppedSpeedKmh = 2.0;
    private const double ScenarioResetSuddenSpeedDropKmh = 8.0;
    private const double ScenarioResetLimitDistanceJumpMeters = 400.0;
    private static readonly TimeSpan ScenarioResetCooldown = TimeSpan.FromSeconds(10);
    private static readonly PairedCommand[] PairedCommands =
    [
        new("door_left", "door_left", "door_close_left"),
        new("door_right", "door_right", "door_close_right"),
        new("afb", "afb_on", "afb_off")
    ];

    private readonly KeyboardProfile profile;
    private readonly Dictionary<string, AxisLog> axisLogState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> pairedCommandNextAlternate = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IPEndPoint> deckRemotes = [];
    private readonly object deckRemotesSync = new();
    private readonly TswAxisMapper axisMapper;
    private readonly TswHttpApiClient tswApi;
    private bool lastAutoTargetActive;
    private bool deckNeutralizedSinceApiLost;
    private bool autoAxisSuppressedUntilApiReadyLogged;
    private bool speedHoldArmed;
    private bool speedHoldAutoPilot;
    private double speedHoldTargetKmh = 80;
    private double speedHoldCruiseTargetKmh = 80;
    private double speedHoldOutput = SpeedHoldNeutral;
    private string speedHoldMode = "off";
    private string speedHoldLastLog = "";
    private double? lastTelemetrySpeedKmh;
    private TswSpeedLimitTelemetry? lastTelemetrySpeedLimit;
    private TswSpeedLimitTelemetry? lastTelemetryNextSpeedLimit;
    private string? lastTelemetrySignalAspect;
    private double? lastTelemetrySignalDistanceMeters;
    private double? previousTelemetrySpeedKmh;
    private TswSpeedLimitTelemetry? previousTelemetrySpeedLimit;
    private DateTimeOffset lastScenarioResetDetectedAt = DateTimeOffset.MinValue;
    private StreamWriter? runRecordingWriter;
    private string? runRecordingPath;
    private DateTimeOffset? runRecordingStartedAt;
    private DateTimeOffset? runRecordingPreviousAt;
    private double? runRecordingPreviousSpeedKmh;
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
    public event EventHandler<RunRecordingStatusEventArgs>? RunRecordingStatusChanged;

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
    public bool IsRunRecording => runRecordingWriter is not null;
    public string? CurrentRunRecordingPath => runRecordingPath;

    public Task<string> ProbeApiAsync() => tswApi.ProbeNowAsync();

    public async Task<string> SnapshotCabAsync()
    {
        var result = await tswApi.SnapshotCabAsync();
        await SendDeckCapabilitiesAsync();
        return result;
    }

    public string ToggleRunRecording(string reason)
    {
        if (runRecordingWriter is null)
        {
            StartRunRecording();
            return $"Run recording started: {runRecordingPath}";
        }

        var path = runRecordingPath;
        StopRunRecording(reason);
        return $"Run recording stopped ({reason}): {path}";
    }

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
        _ = Task.Run(() => TelemetryLoopAsync(cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        udp?.Dispose();
        udp = null;
        cts?.Dispose();
        cts = null;
        CancelDeckNeutralize();
        StopRunRecording("bridge stopped");
        tswApi.Stop();
        lock (deckRemotesSync)
        {
            deckRemotes.Clear();
        }
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
            catch (SocketException ex)
            {
                LogWarn($"Receive warning: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                LogError($"Receive error: {ex.Message}");
                continue;
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

        var isHello = string.Equals(message.Type, "hello", StringComparison.OrdinalIgnoreCase);
        var knownRemote = false;
        var remoteAdded = false;
        lock (deckRemotesSync)
        {
            knownRemote = deckRemotes.Contains(remote);
            if (isHello)
            {
                remoteAdded = deckRemotes.Add(remote);
                knownRemote = true;
            }
        }

        var endpointChanged = knownRemote && (LastRemote is null || !LastRemote.Equals(remote));
        if (knownRemote)
        {
            LastRemote = remote;
        }

        if (knownRemote && (endpointChanged || remoteAdded))
        {
            StatusChanged?.Invoke(this, new BridgeStatusEventArgs(IsRunning, Port, LastRemote));
        }

        switch (message.Type)
        {
            case "hello":
                if (endpointChanged || remoteAdded)
                {
                    LogInfo($"Tablet connected: {message.Device ?? remote.Address.ToString()} at {remote.Address}.");
                    _ = SendDeckCapabilitiesAsync();
                }
                if ((endpointChanged || remoteAdded) && !tswApi.IsReady)
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

        if (HandleRunRecorderButton(command, state))
        {
            return;
        }

        if (HandleSpeedHoldButton(command, state))
        {
            return;
        }

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

    private bool HandleSpeedHoldButton(string command, string state)
    {
        if (!command.StartsWith("td_speed_hold_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(state, "down", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        switch (command.ToLowerInvariant())
        {
            case "td_speed_hold_toggle":
                if (speedHoldArmed && !speedHoldAutoPilot)
                {
                    DisarmSpeedHold("tablet");
                }
                else
                {
                    ArmSpeedHold(lastTelemetrySpeedKmh ?? speedHoldCruiseTargetKmh, "tablet");
                }
                break;
            case "td_speed_hold_auto_pilot":
                if (speedHoldArmed && speedHoldAutoPilot)
                {
                    DisarmSpeedHold("auto pilot");
                }
                else
                {
                    ArmSpeedHold(lastTelemetrySpeedKmh ?? speedHoldCruiseTargetKmh, "auto pilot", autoPilot: true);
                }
                break;
            case "td_speed_hold_set_current":
                if (lastTelemetrySpeedKmh is { } current)
                {
                    SetSpeedHoldTarget(current, "current speed");
                }
                break;
            case "td_speed_hold_set_limit":
                if (lastTelemetrySpeedLimit is { } currentLimit)
                {
                    SetSpeedHoldTarget(currentLimit.NextSpeedLimitKmh, "speed limit");
                }
                break;
            case "td_speed_hold_set_next":
                if (lastTelemetryNextSpeedLimit is { } nextLimit)
                {
                    SetSpeedHoldTarget(nextLimit.NextSpeedLimitKmh, "next speed limit");
                }
                break;
            case "td_speed_hold_minus_5":
                NudgeSpeedHoldTarget(-5);
                break;
            case "td_speed_hold_minus_1":
                NudgeSpeedHoldTarget(-1);
                break;
            case "td_speed_hold_plus_1":
                NudgeSpeedHoldTarget(1);
                break;
            case "td_speed_hold_plus_5":
                NudgeSpeedHoldTarget(5);
                break;
        }

        return true;
    }

    private bool HandleRunRecorderButton(string command, string state)
    {
        if (!string.Equals(command, "td_run_record_toggle", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(state, "down", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ToggleRunRecording("tablet");

        return true;
    }

    private void StartRunRecording()
    {
        var runDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrainDeck",
            "runs");
        Directory.CreateDirectory(runDir);

        runRecordingPath = Path.Combine(runDir, $"traindeck-run-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        runRecordingWriter = new StreamWriter(runRecordingPath, append: false, Encoding.UTF8);
        runRecordingStartedAt = DateTimeOffset.UtcNow;
        runRecordingPreviousAt = null;
        runRecordingPreviousSpeedKmh = null;
        runRecordingWriter.WriteLine(string.Join(",",
        [
            "timestampUtc",
            "elapsedSeconds",
            "speedKmh",
            "accelerationMps2",
            "speedLimitKmh",
            "nextSpeedLimitKmh",
            "nextSpeedLimitDistanceM",
            "gradient",
            "signalAspect",
            "signalDistanceM",
            "powerHandleInput",
            "powerHandleNormalized",
            "reverser",
            "emergencyBrake",
            "dra",
            "brakeHold",
            "doorReleaseLeft",
            "doorReleaseRight",
            "tpwsBrakeDemand",
            "tdHoldArmed",
            "tdAutoPilot",
            "tdTargetKmh",
            "tdOutput",
            "tdMode"
        ]));
        runRecordingWriter.Flush();
        RunRecordingStatusChanged?.Invoke(this, new RunRecordingStatusEventArgs(true, runRecordingPath, TimeSpan.Zero));
        LogInfo($"Run recording started: {runRecordingPath}");
    }

    private void StopRunRecording(string reason)
    {
        if (runRecordingWriter is null)
        {
            return;
        }

        var path = runRecordingPath;
        runRecordingWriter.Dispose();
        runRecordingWriter = null;
        runRecordingPath = null;
        runRecordingStartedAt = null;
        runRecordingPreviousAt = null;
        runRecordingPreviousSpeedKmh = null;
        RunRecordingStatusChanged?.Invoke(this, new RunRecordingStatusEventArgs(false, path, null));
        LogInfo($"Run recording stopped ({reason}): {path}");
    }

    private void ArmSpeedHold(double targetKmh, string reason)
        => ArmSpeedHold(targetKmh, reason, autoPilot: false);

    private void ArmSpeedHold(double targetKmh, string reason, bool autoPilot)
    {
        if (!tswApi.IsReady || !tswApi.IsAxisMapped("throttle"))
        {
            LogWarn("TD Speed Hold skipped; throttle API axis is not available for the current cab.");
            return;
        }

        var requestedTarget = ClampSpeedHoldTarget(targetKmh);
        speedHoldCruiseTargetKmh = autoPilot ? SpeedHoldMaxTargetKmh : requestedTarget;
        speedHoldTargetKmh = autoPilot ? CalculateAutoPilotTargetKmh(requestedTarget) : speedHoldCruiseTargetKmh;
        speedHoldAutoPilot = autoPilot;
        speedHoldArmed = true;
        speedHoldMode = autoPilot ? "auto" : "armed";
        speedHoldOutput = SpeedHoldNeutral;
        LogSpeedHold($"{(autoPilot ? "auto pilot armed" : "armed")} at {speedHoldTargetKmh:0} km/h ({reason})");
    }

    private void DisarmSpeedHold(string reason, bool neutralize = true)
    {
        if (!speedHoldArmed && string.Equals(speedHoldMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        speedHoldArmed = false;
        speedHoldAutoPilot = false;
        speedHoldMode = "off";
        speedHoldOutput = SpeedHoldNeutral;
        LogSpeedHold($"disarmed ({reason})");
        if (neutralize && tswApi.IsReady && tswApi.IsAxisMapped("throttle"))
        {
            _ = SendSpeedHoldAxisAsync(SpeedHoldNeutral);
        }
    }

    private void SetSpeedHoldTarget(double targetKmh, string reason)
    {
        speedHoldCruiseTargetKmh = ClampSpeedHoldTarget(targetKmh);
        speedHoldTargetKmh = speedHoldCruiseTargetKmh;
        speedHoldAutoPilot = false;
        if (!speedHoldArmed)
        {
            ArmSpeedHold(speedHoldTargetKmh, reason);
            return;
        }

        LogSpeedHold($"target {speedHoldTargetKmh:0} km/h ({reason})");
    }

    private void NudgeSpeedHoldTarget(double deltaKmh)
    {
        var keepAutoPilot = speedHoldAutoPilot;
        speedHoldCruiseTargetKmh = ClampSpeedHoldTarget(speedHoldCruiseTargetKmh + deltaKmh);
        speedHoldTargetKmh = speedHoldCruiseTargetKmh;
        if (!speedHoldArmed)
        {
            ArmSpeedHold(speedHoldCruiseTargetKmh, deltaKmh > 0 ? $"+{deltaKmh:0}" : $"{deltaKmh:0}", autoPilot: keepAutoPilot);
            return;
        }

        speedHoldAutoPilot = keepAutoPilot;
        LogSpeedHold($"target {speedHoldCruiseTargetKmh:0} km/h ({(deltaKmh > 0 ? $"+{deltaKmh:0}" : $"{deltaKmh:0}")})");
    }

    private async Task UpdateSpeedHoldAsync(double currentSpeedKmh)
    {
        if (!speedHoldArmed)
        {
            speedHoldMode = "off";
            speedHoldOutput = SpeedHoldNeutral;
            return;
        }

        if (!tswApi.IsReady || !tswApi.IsAxisMapped("throttle"))
        {
            DisarmSpeedHold("API unavailable");
            return;
        }

        if (speedHoldAutoPilot)
        {
            speedHoldTargetKmh = CalculateAutoPilotTargetKmh(currentSpeedKmh);
        }

        var error = speedHoldTargetKmh - currentSpeedKmh;
        var (output, mode) = CalculateSpeedHoldOutput(error);

        output = Math.Clamp(output, 0.08, SpeedHoldPowerMax);
        speedHoldMode = speedHoldAutoPilot ? $"auto-{mode}" : mode;
        speedHoldOutput = output;
        await SendSpeedHoldAxisAsync(output);

        var summary = $"{speedHoldMode}:{speedHoldTargetKmh:0}:{currentSpeedKmh:0}:{output:0.000}";
        if (!string.Equals(summary, speedHoldLastLog, StringComparison.OrdinalIgnoreCase))
        {
            speedHoldLastLog = summary;
            LogInfo($"TD Hold {speedHoldMode,-10} target={speedHoldTargetKmh:0} km/h speed={currentSpeedKmh:0.0} km/h throttle={output:0.000}");
        }
    }

    private double CalculateAutoPilotTargetKmh(double currentSpeedKmh)
    {
        var target = lastTelemetrySpeedLimit is null
            ? Math.Min(speedHoldCruiseTargetKmh, ClampSpeedHoldTarget(currentSpeedKmh))
            : speedHoldCruiseTargetKmh;

        if (lastTelemetrySpeedLimit is { } currentLimit)
        {
            target = Math.Min(target, ClampSpeedHoldTarget(currentLimit.NextSpeedLimitKmh - AutoPilotLimitBufferKmh));
        }

        if (lastTelemetryNextSpeedLimit is { } nextLimit)
        {
            var nextTargetKmh = ClampSpeedHoldTarget(nextLimit.NextSpeedLimitKmh - AutoPilotLimitBufferKmh);
            if (nextTargetKmh < target)
            {
                target = currentSpeedKmh >= nextTargetKmh - AutoPilotUpcomingLimitHoldWindowKmh
                    ? Math.Min(target, nextTargetKmh)
                    : ApplyBrakeCurveTarget(target, currentSpeedKmh, nextTargetKmh, nextLimit.DistanceMeters);
            }
        }

        if (IsStopSignal(lastTelemetrySignalAspect)
            && lastTelemetrySignalDistanceMeters is { } signalDistanceMeters
            && signalDistanceMeters > 0)
        {
            target = signalDistanceMeters <= AutoPilotStopSignalBufferMeters
                ? 0
                : ApplyStopSignalCurveTarget(target, currentSpeedKmh, signalDistanceMeters - AutoPilotStopSignalBufferMeters);
        }

        return ClampSpeedHoldTarget(target);
    }

    private static (double Output, string Mode) CalculateSpeedHoldOutput(double errorKmh)
    {
        if (errorKmh < -SpeedHoldDeadbandKmh)
        {
            var overspeed = Math.Min(25, Math.Abs(errorKmh) - SpeedHoldDeadbandKmh);
            return (SpeedHoldNeutral - Math.Min(0.42, 0.06 + overspeed * 0.024), "brake");
        }

        if (errorKmh <= SpeedHoldFeatherHoldKmh)
        {
            return (SpeedHoldNeutral, "hold");
        }

        if (errorKmh < SpeedHoldDeadbandKmh)
        {
            return (Lerp(SpeedHoldNeutral, 0.60, (errorKmh - SpeedHoldFeatherHoldKmh) / (SpeedHoldDeadbandKmh - SpeedHoldFeatherHoldKmh)), "feather");
        }

        if (errorKmh <= 2.0)
        {
            return (Lerp(0.60, 0.75, (errorKmh - SpeedHoldDeadbandKmh) / 1.0), "feather");
        }

        if (errorKmh <= SpeedHoldFeatherWindowKmh)
        {
            return (Lerp(0.75, 0.875, (errorKmh - 2.0) / (SpeedHoldFeatherWindowKmh - 2.0)), "feather");
        }

        if (errorKmh <= SpeedHoldFullPowerErrorKmh)
        {
            return (Lerp(0.875, SpeedHoldPowerMax, (errorKmh - SpeedHoldFeatherWindowKmh) / (SpeedHoldFullPowerErrorKmh - SpeedHoldFeatherWindowKmh)), "power");
        }

        return (SpeedHoldPowerMax, "power");
    }

    private static double ApplyBrakeCurveTarget(double currentTargetKmh, double currentSpeedKmh, double requiredTargetKmh, double distanceMeters)
    {
        var curveTargetKmh = BrakeCurveTargetKmh(currentSpeedKmh, requiredTargetKmh, distanceMeters);
        return Math.Min(currentTargetKmh, Math.Max(requiredTargetKmh, curveTargetKmh));
    }

    private static double ApplyStopSignalCurveTarget(double currentTargetKmh, double currentSpeedKmh, double distanceMeters)
    {
        var curveTargetKmh = BrakeCurveTargetKmh(currentSpeedKmh, 0, distanceMeters);
        return Math.Min(currentTargetKmh, Math.Min(currentSpeedKmh, Math.Max(0, curveTargetKmh)));
    }

    private static double BrakeCurveTargetKmh(double currentSpeedKmh, double targetSpeedKmh, double distanceMeters)
    {
        var currentMps = Math.Max(0, currentSpeedKmh / 3.6);
        var targetMps = Math.Max(0, targetSpeedKmh / 3.6);
        if (currentMps <= targetMps || distanceMeters <= AutoPilotBrakeMarginMeters)
        {
            return targetSpeedKmh;
        }

        var usableMeters = distanceMeters - (currentMps * AutoPilotReactionSeconds) - AutoPilotBrakeMarginMeters;
        if (usableMeters <= 0)
        {
            return targetSpeedKmh;
        }

        var curveMps = Math.Sqrt((targetMps * targetMps) + (2 * AutoPilotBrakeDecelMps2 * usableMeters));
        return Math.Max(targetSpeedKmh, curveMps * 3.6);
    }

    private static double Lerp(double start, double end, double amount)
    {
        return start + (end - start) * Math.Clamp(amount, 0, 1);
    }

    private static bool IsStopSignal(string? signalAspect)
    {
        return string.Equals(signalAspect, "Stop", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendSpeedHoldAxisAsync(double value)
    {
        if (tswApi.TryMapAxis("throttle", value, out var command, force: true))
        {
            await tswApi.SendAxisAsync(command);
        }
    }

    private static double ClampSpeedHoldTarget(double targetKmh)
    {
        if (!double.IsFinite(targetKmh))
        {
            return 80;
        }

        return Math.Round(Math.Clamp(targetKmh, SpeedHoldMinTargetKmh, SpeedHoldMaxTargetKmh));
    }

    private void LogSpeedHold(string message)
    {
        speedHoldLastLog = "";
        LogInfo($"TD Hold {message}");
    }

    private static bool IsSpeedHoldManualOverride(string? control)
    {
        return string.Equals(control, "throttle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(control, "dynamic_brake", StringComparison.OrdinalIgnoreCase)
            || string.Equals(control, "train_brake", StringComparison.OrdinalIgnoreCase)
            || string.Equals(control, "independent_brake", StringComparison.OrdinalIgnoreCase)
            || string.Equals(control, "afb", StringComparison.OrdinalIgnoreCase);
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
        if (speedHoldArmed && IsSpeedHoldManualOverride(message.Control))
        {
            DisarmSpeedHold($"manual {message.Control}", neutralize: false);
        }

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
        if (string.Equals(message.Action, "scroll", StringComparison.OrdinalIgnoreCase))
        {
            HandlePointerScroll(message);
            return;
        }

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

    private void HandlePointerScroll(TrainDeckMessage message)
    {
        var dy = message.DeltaY ?? 0;
        if (Math.Abs(dy) < 0.5)
        {
            return;
        }

        var focused = EnsureKeyboardTargetForButton();
        if (!focused)
        {
            LogWarn($"pointer scroll skipped; Train Sim World could not be focused. foreground={ForegroundAppDetector.DescribeForeground()}");
            return;
        }

        var wheel = (int)Math.Round(Math.Max(-8, Math.Min(8, -dy)) * 120);
        KeyboardOutput.MouseWheel(wheel);
        LogInfo($"pointer scroll {wheel,5} focusTsw={focused} foreground={ForegroundAppDetector.DescribeForeground()} {KeyboardOutput.LastSummary}");
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
            DisarmSpeedHold(e.Status);
            return;
        }

        DisarmSpeedHold(e.Status);
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

    private async Task TelemetryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TelemetryInterval, token);
                await SendDeckTelemetryAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private async Task SendDeckTelemetryAsync(CancellationToken token)
    {
        if (udp is null || !tswApi.IsReady)
        {
            return;
        }

        var speedKmh = await tswApi.TryGetSpeedKmhAsync(token);
        if (speedKmh is null)
        {
            return;
        }

        var driverAid = await tswApi.TryGetDriverAidTelemetryAsync(token);
        var speedLimit = driverAid?.Current;
        var nextSpeedLimit = driverAid?.Next;
        await DetectScenarioRestartAsync(speedKmh.Value, nextSpeedLimit ?? speedLimit);
        lastTelemetrySpeedKmh = speedKmh.Value;
        lastTelemetrySpeedLimit = speedLimit;
        lastTelemetryNextSpeedLimit = nextSpeedLimit;
        lastTelemetrySignalAspect = driverAid?.SignalAspect;
        lastTelemetrySignalDistanceMeters = driverAid?.SignalDistanceMeters;
        previousTelemetrySpeedKmh = speedKmh.Value;
        previousTelemetrySpeedLimit = nextSpeedLimit ?? speedLimit;
        await UpdateSpeedHoldAsync(speedKmh.Value);
        await WriteRunRecordingSampleAsync(DateTimeOffset.UtcNow, speedKmh.Value, driverAid, token);
        var recordingElapsed = runRecordingStartedAt is { } startedAt
            ? DateTimeOffset.UtcNow - startedAt
            : (TimeSpan?)null;
        var payload = new TrainDeckBridgeMessage
        {
            Type = "telemetry",
            SpeedKmh = speedKmh.Value,
            SpeedMph = speedKmh.Value * 0.621371,
            SpeedLimitKmh = speedLimit?.NextSpeedLimitKmh,
            SpeedLimitDistanceM = speedLimit?.DistanceMeters,
            NextSpeedLimitKmh = nextSpeedLimit?.NextSpeedLimitKmh,
            NextSpeedLimitDistanceM = nextSpeedLimit?.DistanceMeters,
            SpeedHoldArmed = speedHoldArmed,
            SpeedHoldAutoPilot = speedHoldAutoPilot,
            SpeedHoldTargetKmh = speedHoldArmed ? speedHoldTargetKmh : null,
            SpeedHoldOutput = speedHoldArmed ? speedHoldOutput : null,
            SpeedHoldMode = speedHoldMode,
            RunRecording = runRecordingWriter is not null,
            RunRecordingElapsedSeconds = recordingElapsed?.TotalSeconds,
            At = Environment.TickCount64
        };
        if (runRecordingWriter is not null)
        {
            RunRecordingStatusChanged?.Invoke(this, new RunRecordingStatusEventArgs(true, runRecordingPath, recordingElapsed));
        }

        try
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions.Default));
            foreach (var remote in DeckRemoteSnapshot())
            {
                await udp.SendAsync(body, body.Length, remote);
            }
        }
        catch
        {
        }
    }

    private async Task WriteRunRecordingSampleAsync(
        DateTimeOffset at,
        double speedKmh,
        TswDriverAidTelemetry? driverAid,
        CancellationToken token)
    {
        if (runRecordingWriter is null || runRecordingStartedAt is not { } startedAt)
        {
            return;
        }

        var cab = await tswApi.TryGetCabTraceTelemetryAsync(token);
        var elapsed = (at - startedAt).TotalSeconds;
        double? accelerationMps2 = null;
        if (runRecordingPreviousAt is { } previousAt
            && runRecordingPreviousSpeedKmh is { } previousSpeed
            && (at - previousAt).TotalSeconds > 0)
        {
            accelerationMps2 = ((speedKmh - previousSpeed) / 3.6) / (at - previousAt).TotalSeconds;
        }

        runRecordingPreviousAt = at;
        runRecordingPreviousSpeedKmh = speedKmh;

        runRecordingWriter.WriteLine(CsvRow(
            at.ToString("O", CultureInfo.InvariantCulture),
            elapsed,
            speedKmh,
            accelerationMps2,
            driverAid?.Current?.NextSpeedLimitKmh,
            driverAid?.Next?.NextSpeedLimitKmh,
            driverAid?.Next?.DistanceMeters,
            driverAid?.Gradient,
            driverAid?.SignalAspect,
            driverAid?.SignalDistanceMeters,
            cab?.PowerHandleInput,
            cab?.PowerHandleNormalized,
            cab?.Reverser,
            cab?.EmergencyBrake,
            cab?.Dra,
            cab?.BrakeHold,
            cab?.DoorReleaseLeft,
            cab?.DoorReleaseRight,
            cab?.TpwsBrakeDemand,
            speedHoldArmed,
            speedHoldAutoPilot,
            speedHoldArmed ? speedHoldTargetKmh : null,
            speedHoldArmed ? speedHoldOutput : null,
            speedHoldMode));
        await runRecordingWriter.FlushAsync(token);
    }

    private static string CsvRow(params object?[] values)
        => string.Join(",", values.Select(CsvValue));

    private static string CsvValue(object? value)
    {
        return value switch
        {
            null => "",
            double d when double.IsFinite(d) => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f when float.IsFinite(f) => f.ToString("0.###", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => QuoteCsv(value.ToString() ?? "")
        };
    }

    private static string QuoteCsv(string value)
    {
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private async Task DetectScenarioRestartAsync(double speedKmh, TswSpeedLimitTelemetry? speedLimit)
    {
        if (LastRemote is null || DateTimeOffset.UtcNow - lastScenarioResetDetectedAt < ScenarioResetCooldown)
        {
            return;
        }

        var stoppedAfterSuddenDrop = previousTelemetrySpeedKmh is { } previousSpeed
            && previousSpeed - speedKmh >= ScenarioResetSuddenSpeedDropKmh
            && speedKmh <= ScenarioResetStoppedSpeedKmh;

        var nextLimitJumpedBack = speedLimit is { } currentLimit
            && previousTelemetrySpeedLimit is { } previousLimit
            && currentLimit.DistanceMeters - previousLimit.DistanceMeters >= ScenarioResetLimitDistanceJumpMeters
            && speedKmh <= ScenarioResetStoppedSpeedKmh;

        if (!stoppedAfterSuddenDrop && !nextLimitJumpedBack)
        {
            return;
        }

        lastScenarioResetDetectedAt = DateTimeOffset.UtcNow;
        axisMapper.Reset();
        pairedCommandNextAlternate.Clear();
        DisarmSpeedHold("scenario restarted");
        await SendDeckCommandAsync("reset_axes", "scenario restarted");
        LogInfo("Scenario restart detected; TrainDeck tablet state reset.");
    }

    private List<IPEndPoint> DeckRemoteSnapshot()
    {
        lock (deckRemotesSync)
        {
            return deckRemotes.ToList();
        }
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

        if (!tswApi.HasActiveActor)
        {
            LogInfo("tablet capabilities held; active TSW cab actor is unknown.");
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
            "marker_lights",
            "reverser_key"
        }
            .Where(IsButtonHandledByApi)
            .Concat([
                "td_speed_hold_toggle",
                "td_speed_hold_auto_pilot",
                "td_speed_hold_set_current",
                "td_speed_hold_set_limit",
                "td_speed_hold_set_next",
                "td_speed_hold_minus_5",
                "td_speed_hold_minus_1",
                "td_speed_hold_plus_1",
                "td_speed_hold_plus_5",
                "td_run_record_toggle"
            ])
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
internal sealed record RunRecordingStatusEventArgs(bool Recording, string? Path, TimeSpan? Elapsed);
internal sealed record AxisLog(double Value, DateTimeOffset At);
internal sealed record PairedCommand(string ToggleCommand, string PrimaryCommand, string AlternateCommand);
internal sealed record TswSpeedLimitsTelemetry(TswSpeedLimitTelemetry? Current, TswSpeedLimitTelemetry? Next);
internal sealed record TswDriverAidTelemetry(
    TswSpeedLimitTelemetry? Current,
    TswSpeedLimitTelemetry? Next,
    double? Gradient,
    string? SignalAspect,
    double? SignalDistanceMeters);
internal sealed record TswSpeedLimitTelemetry(double NextSpeedLimitKmh, double DistanceMeters);
internal sealed record TswCabTraceTelemetry(
    double? PowerHandleInput,
    double? PowerHandleNormalized,
    double? Reverser,
    double? EmergencyBrake,
    double? Dra,
    double? BrakeHold,
    double? DoorReleaseLeft,
    double? DoorReleaseRight,
    double? TpwsBrakeDemand);
