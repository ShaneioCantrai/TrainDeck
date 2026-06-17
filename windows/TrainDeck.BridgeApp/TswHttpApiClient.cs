using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrainDeck.BridgeApp;

internal sealed class TswHttpApiClient : IDisposable
{
    private static readonly Uri BaseUri = new("http://127.0.0.1:31270");
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromMilliseconds(900)
    };
    private readonly Action<string> log;
    private readonly TswHttpApiAxisMapper axisMapper;
    private readonly Dictionary<string, DateTimeOffset> interactingControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly object statusSync = new();
    private CancellationTokenSource? cts;
    private string? apiKey;
    private string statusText = "not checked";

    public TswHttpApiClient(Action<string> log)
    {
        this.log = log;
        axisMapper = new TswHttpApiAxisMapper(log);
    }

    public event EventHandler<TswHttpApiStatusEventArgs>? StatusChanged;

    public bool IsReady { get; private set; }
    public bool HasActiveActor { get; private set; }
    public string StatusText
    {
        get
        {
            lock (statusSync)
            {
                return statusText;
            }
        }
    }

    public void Start()
    {
        if (cts is not null)
        {
            return;
        }

        cts = new CancellationTokenSource();
        _ = Task.Run(() => ProbeLoopAsync(cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
        HasActiveActor = false;
        UpdateStatus(false, "stopped");
    }

    public bool TryMapAxis(string control, double value, out TswHttpApiAxisCommand command)
    {
        return axisMapper.TryMap(control, value, out command);
    }

    public bool IsAxisMapped(string control)
    {
        return axisMapper.IsMapped(control);
    }

    public Dictionary<string, List<TrainDeckAxisOption>> GetAxisOptions()
    {
        return axisMapper.GetAxisOptions();
    }

    public bool TryMapButton(string command, out TswHttpApiButtonCommand buttonCommand)
    {
        return axisMapper.TryMapButton(command, out buttonCommand);
    }

    public bool IsButtonMapped(string command)
    {
        return axisMapper.IsButtonMapped(command);
    }

    public async Task SendAxisAsync(TswHttpApiAxisCommand command)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        try
        {
            foreach (var output in command.Outputs)
            {
                await EnsureInteractingAsync(output.ControlName, CancellationToken.None);
                await PatchAsync(
                    $"/set/CurrentDrivableActor/{Uri.EscapeDataString(output.ControlName)}.InputValue?Value={output.Value.ToString("0.000000", CultureInfo.InvariantCulture)}",
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"API send failed: {ex.Message}");
        }
    }

    public async Task SendButtonAsync(TswHttpApiButtonCommand command)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        try
        {
            foreach (var step in command.Steps)
            {
                if (step.DelayBeforeMs > 0)
                {
                    await Task.Delay(step.DelayBeforeMs);
                }

                await EnsureInteractingAsync(step.ControlName, CancellationToken.None);
                if (step.CycleValues.Count > 0)
                {
                    var current = await TryGetDoubleValueAsync(
                        $"/get/CurrentDrivableActor/{Uri.EscapeDataString(step.ControlName)}.Function.GetNormalisedInputValue",
                        "ReturnValue",
                        CancellationToken.None)
                        ?? await TryGetDoubleValueAsync(
                            $"/get/CurrentDrivableActor/{Uri.EscapeDataString(step.ControlName)}.InputValue",
                            "InputValue",
                            CancellationToken.None);
                    var nextValue = NextCycleValue(step.CycleValues, current);
                    await PatchAsync(
                        $"/set/CurrentDrivableActor/{Uri.EscapeDataString(step.ControlName)}.InputValue?Value={nextValue.ToString("0.000000", CultureInfo.InvariantCulture)}",
                        CancellationToken.None);
                    if (step.DelayAfterMs > 0)
                    {
                        await Task.Delay(step.DelayAfterMs);
                    }

                    continue;
                }

                await PatchAsync(
                    $"/set/CurrentDrivableActor/{Uri.EscapeDataString(step.ControlName)}.InputValue?Value={step.Value.ToString("0.000000", CultureInfo.InvariantCulture)}",
                    CancellationToken.None);

                    if (step.HoldMs > 0)
                    {
                        await Task.Delay(step.HoldMs);
                        var releaseValue = step.ReleaseValue ?? 0;
                        await PatchAsync(
                            $"/set/CurrentDrivableActor/{Uri.EscapeDataString(step.ControlName)}.InputValue?Value={releaseValue.ToString("0.000000", CultureInfo.InvariantCulture)}",
                            CancellationToken.None);
                    }

                if (step.DelayAfterMs > 0)
                {
                    await Task.Delay(step.DelayAfterMs);
                }
            }
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"API button failed: {ex.Message}");
        }
    }

    public async Task<double?> TryGetSpeedKmhAsync(CancellationToken token)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var speedMs = await TryGetDoubleValueAsync(
            "/get/CurrentDrivableActor.Function.HUD_GetSpeed",
            "Speed (ms)",
            token);
        return speedMs is null ? null : Math.Abs(speedMs.Value) * 3.6;
    }

    public async Task<TswSpeedLimitTelemetry?> TryGetSpeedLimitTelemetryAsync(CancellationToken token)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            using var data = await GetJsonAsync("/get/DriverAid.Data", token);
            if (!data.RootElement.TryGetProperty("Values", out var values))
            {
                return null;
            }

            if (values.TryGetProperty("nextSpeedLimits", out var limits)
                && limits.ValueKind == JsonValueKind.Array)
            {
                var best = limits
                    .EnumerateArray()
                    .Select(TryReadSpeedLimitTelemetry)
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .OrderBy(item => item.DistanceMeters)
                    .FirstOrDefault();
                if (best is not null)
                {
                    return best;
                }
            }

            var speedMs = TryReadSpeedLimitValue(values, "nextSpeedLimit");
            var distanceCm = TryReadDouble(values, "distanceToNextSpeedLimit");
            return MakeSpeedLimitTelemetry(speedMs, distanceCm);
        }
        catch
        {
            return null;
        }
    }

    private static double NextCycleValue(IReadOnlyList<double> cycleValues, double? current)
    {
        if (cycleValues.Count == 0)
        {
            return 0;
        }

        if (current is null)
        {
            return cycleValues[0];
        }

        var nearestIndex = 0;
        var nearestDistance = double.MaxValue;
        for (var i = 0; i < cycleValues.Count; i++)
        {
            var distance = Math.Abs(cycleValues[i] - current.Value);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        if (nearestDistance <= 0.08)
        {
            return cycleValues[(nearestIndex + 1) % cycleValues.Count];
        }

        foreach (var value in cycleValues)
        {
            if (value > current.Value)
            {
                return value;
            }
        }

        return cycleValues[0];
    }

    public async Task<string> ProbeNowAsync()
    {
        await ProbeAsync(CancellationToken.None);
        return StatusText;
    }

    public async Task<string> SnapshotCabAsync()
    {
        await ProbeAsync(CancellationToken.None);
        if (!IsReady)
        {
            return $"TSW API is not ready: {StatusText}";
        }

        try
        {
            var actor = await GetStringValueAsync("/get/CurrentDrivableActor.ObjectClass", "ObjectClass", CancellationToken.None);
            var cab = await TryGetActiveCabAsync(CancellationToken.None);
            var controls = await ReadInputControlsAsync(CancellationToken.None);
            var lines = new List<string>
            {
                $"Actor: {(string.IsNullOrWhiteSpace(actor) ? "unknown" : actor)}",
                $"Cab: {cab}",
                $"Input controls: {controls.Count}"
            };

            lines.Add("");
            foreach (var control in controls.Take(120))
            {
                var normalized = control.NormalizedValue is null ? "" : $" norm={control.NormalizedValue:0.000}";
                var identifier = string.IsNullOrWhiteSpace(control.Identifier) ? "" : $" id={control.Identifier}";
                lines.Add($"{control.Name} value={control.InputValue:0.000}{normalized}{identifier}");
            }

            var snapshotDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TrainDeck",
                "snapshots");
            Directory.CreateDirectory(snapshotDir);
            var snapshotPath = Path.Combine(snapshotDir, $"tsw-cab-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllLines(snapshotPath, lines);

            var autoMap = TswHttpApiProfileCatalog.MergeSnapshot(actor, controls, log);
            if (autoMap.Changed)
            {
                axisMapper.ReloadProfiles(actor, cab);
            }

            var throttleHints = controls
                .Where(c => c.Name.Contains("throttle", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("master", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("power", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("brake", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("reverser", StringComparison.OrdinalIgnoreCase))
                .Take(16)
                .Select(c => $"{c.Name}={c.InputValue:0.000}");
            var hintText = string.Join("; ", throttleHints);
            var autoMapText = autoMap.MappedCount == 0
                ? autoMap.Message
                : $"{autoMap.Message} ({autoMap.MappedCount} bindings)";
            return string.IsNullOrWhiteSpace(hintText)
                ? $"Cab snapshot saved: {snapshotPath}. {autoMapText}"
                : $"Cab snapshot saved: {snapshotPath}. {autoMapText}. Likely controls: {hintText}";
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"cab snapshot failed: {ex.Message}");
            return $"Cab snapshot failed: {ex.Message}";
        }
    }

    private async Task ProbeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await ProbeAsync(token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IsReady ? 5 : 2), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeAsync(CancellationToken token)
    {
        apiKey = FindApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            HasActiveActor = false;
            UpdateStatus(false, "missing CommAPIKey.txt");
            return;
        }

        try
        {
            var data = await GetJsonAsync("/get/CurrentDrivableActor.ObjectClass", token);
            var actor = "";
            if (data.RootElement.TryGetProperty("Values", out var values)
                && values.TryGetProperty("ObjectClass", out var objectClass))
            {
                actor = objectClass.GetString() ?? "";
            }

            var cab = await TryGetActiveCabAsync(token);
            axisMapper.SetContext(actor, cab);
            var profile = axisMapper.CurrentProfileName;
            var actorText = string.IsNullOrWhiteSpace(actor) ? "connected" : $"connected: {actor}";
            HasActiveActor = !string.IsNullOrWhiteSpace(actor);
            UpdateStatus(true, $"{actorText} | profile: {profile}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            HasActiveActor = false;
            UpdateStatus(false, "bad API key");
        }
        catch (Exception ex)
        {
            HasActiveActor = false;
            if (ex.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatus(false, TswSteamLauncher.IsTrainSimWorldRunning()
                    ? "TSW is running without API mode. Close TSW and launch it from TrainDeck."
                    : "TSW not running with -HTTPAPI");
                return;
            }

            UpdateStatus(false, $"not connected: {ex.Message}");
        }
    }

    private async Task EnsureInteractingAsync(string control, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (interactingControls.TryGetValue(control, out var last) && now - last < TimeSpan.FromSeconds(8))
        {
            return;
        }

        await PatchAsync($"/set/CurrentDrivableActor/{Uri.EscapeDataString(control)}.Interacting?Value=1.000000", token);
        interactingControls[control] = now;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path));
        request.Headers.Add("DTGCommKey", apiKey);
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private async Task<string> GetStringValueAsync(string path, string propertyName, CancellationToken token)
    {
        using var data = await GetJsonAsync(path, token);
        if (data.RootElement.TryGetProperty("Values", out var values)
            && values.TryGetProperty(propertyName, out var property))
        {
            return property.GetString() ?? "";
        }

        return "";
    }

    private async Task<string> TryGetActiveCabAsync(CancellationToken token)
    {
        try
        {
            using var data = await GetJsonAsync("/get/CurrentDrivableActor.Function.IS_GetActiveCab", token);
            if (!data.RootElement.TryGetProperty("Values", out var values))
            {
                return "unknown";
            }

            var front = values.TryGetProperty("bFront", out var bFront) && bFront.ValueKind == JsonValueKind.True;
            var back = values.TryGetProperty("bBack", out var bBack) && bBack.ValueKind == JsonValueKind.True;
            return front && back ? "front+back" : front ? "front" : back ? "back" : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private async Task<List<TswCabControlSnapshot>> ReadInputControlsAsync(CancellationToken token)
    {
        using var list = await GetJsonAsync("/list/CurrentDrivableActor", token);
        if (!list.RootElement.TryGetProperty("Nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var controls = new List<TswCabControlSnapshot>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("Name", out var nameProperty))
            {
                continue;
            }

            var name = nameProperty.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var inputValue = await TryGetDoubleValueAsync(
                $"/get/CurrentDrivableActor/{Uri.EscapeDataString(name)}.InputValue",
                "InputValue",
                token);
            if (inputValue is null)
            {
                continue;
            }

            var normalized = await TryGetDoubleValueAsync(
                $"/get/CurrentDrivableActor/{Uri.EscapeDataString(name)}.Function.GetNormalisedInputValue",
                "ReturnValue",
                token);
            var identifier = await TryGetIdentifierAsync(name, token);
            controls.Add(new TswCabControlSnapshot(name, inputValue.Value, normalized, identifier));
        }

        return controls
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<double?> TryGetDoubleValueAsync(string path, string propertyName, CancellationToken token)
    {
        try
        {
            using var data = await GetJsonAsync(path, token);
            if (data.RootElement.TryGetProperty("Values", out var values)
                && values.TryGetProperty(propertyName, out var property)
                && property.TryGetDouble(out var value))
            {
                return value;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static TswSpeedLimitTelemetry? TryReadSpeedLimitTelemetry(JsonElement item)
    {
        var speedMs = TryReadSpeedLimitValue(item, "value");
        var distanceCm = TryReadDouble(item, "distanceToNextSpeedLimit");
        return MakeSpeedLimitTelemetry(speedMs, distanceCm);
    }

    private static TswSpeedLimitTelemetry? MakeSpeedLimitTelemetry(double? speedMs, double? distanceCm)
    {
        if (speedMs is null || distanceCm is null)
        {
            return null;
        }

        if (!double.IsFinite(speedMs.Value)
            || !double.IsFinite(distanceCm.Value)
            || speedMs.Value < 0
            || speedMs.Value > 250
            || distanceCm.Value < 0)
        {
            return null;
        }

        return new TswSpeedLimitTelemetry(
            speedMs.Value * 3.6,
            distanceCm.Value / 100.0);
    }

    private static double? TryReadSpeedLimitValue(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var direct))
        {
            return direct;
        }

        return TryReadDouble(property, "value");
    }

    private static double? TryReadDouble(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out var value)
            ? value
            : null;
    }

    private async Task<string> TryGetIdentifierAsync(string control, CancellationToken token)
    {
        try
        {
            using var data = await GetJsonAsync(
                $"/get/CurrentDrivableActor/{Uri.EscapeDataString(control)}.Property.InputIdentifier",
                token);
            if (data.RootElement.TryGetProperty("Values", out var values)
                && values.TryGetProperty("identifier", out var property))
            {
                return property.GetString() ?? "";
            }
        }
        catch
        {
            return "";
        }

        return "";
    }

    private async Task PatchAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(BaseUri, path));
        request.Headers.Add("DTGCommKey", apiKey);
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
    }

    private void UpdateStatus(bool ready, string text)
    {
        var changed = ready != IsReady || !string.Equals(text, StatusText, StringComparison.Ordinal);
        IsReady = ready;
        lock (statusSync)
        {
            statusText = text;
        }

        if (changed)
        {
            log(ready ? $"TSW HTTP API ready: {text}" : $"TSW HTTP API unavailable: {text}");
            StatusChanged?.Invoke(this, new TswHttpApiStatusEventArgs(ready, text));
        }
    }

    private static string? FindApiKey()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var primary = Path.Combine(documents, "My Games", "TrainSimWorld6", "Saved", "Config", "CommAPIKey.txt");
        var candidates = new[]
        {
            primary,
            Path.Combine(documents, "My Games", "TrainSimWorld6", "Saved", "Config", "WindowsNoEditor", "CommAPIKey.txt"),
            Path.Combine(documents, "My Games", "TrainSimWorld5", "Saved", "Config", "CommAPIKey.txt"),
            Path.Combine(documents, "My Games", "TrainSimWorld5", "Saved", "Config", "WindowsNoEditor", "CommAPIKey.txt")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToHexString(keyBytes).ToLowerInvariant();
        File.WriteAllText(primary, key);
        return key;
    }

    public void Dispose()
    {
        Stop();
        http.Dispose();
    }
}

internal sealed class TswHttpApiAxisMapper
{
    private readonly Action<string> log;
    private TswHttpApiProfileCatalog catalog;
    private readonly Dictionary<string, double> lastValues = new(StringComparer.OrdinalIgnoreCase);
    private TswHttpApiProfile currentProfile;
    private string activeSide = "F";

    public TswHttpApiAxisMapper(Action<string> log)
    {
        this.log = log;
        catalog = TswHttpApiProfileCatalog.Load(log);
        currentProfile = catalog.DefaultProfile;
    }

    public string CurrentProfileName => currentProfile.Name;

    public void SetContext(string actorClass, string activeCab)
    {
        var nextProfile = catalog.Match(actorClass);
        var nextSide = string.Equals(activeCab, "back", StringComparison.OrdinalIgnoreCase) ? "B" : "F";

        if (!string.Equals(nextProfile.Id, currentProfile.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(nextSide, activeSide, StringComparison.OrdinalIgnoreCase))
        {
            currentProfile = nextProfile;
            activeSide = nextSide;
            lastValues.Clear();
        }
    }

    public void ReloadProfiles(string actorClass, string activeCab)
    {
        catalog = TswHttpApiProfileCatalog.Load(log);
        currentProfile = catalog.DefaultProfile;
        lastValues.Clear();
        SetContext(actorClass, activeCab);
    }

    public bool IsMapped(string control)
    {
        return currentProfile.Axes.ContainsKey(control);
    }

    public bool IsButtonMapped(string command)
    {
        return currentProfile.Buttons.ContainsKey(command);
    }

    public Dictionary<string, List<TrainDeckAxisOption>> GetAxisOptions()
    {
        var options = new Dictionary<string, List<TrainDeckAxisOption>>(StringComparer.OrdinalIgnoreCase);
        if (currentProfile.Axes.TryGetValue("reverser", out var reverserBindings)
            && reverserBindings.FirstOrDefault() is { } reverser)
        {
            var controlName = ResolveControlName(reverser.ControlName);
            if (controlName.Contains("MasterControlSwitch", StringComparison.OrdinalIgnoreCase))
            {
                options["reverser"] =
                [
                    new TrainDeckAxisOption("Reverse", -1),
                    new TrainDeckAxisOption("Recovery", -0.5),
                    new TrainDeckAxisOption("Secure", 0),
                    new TrainDeckAxisOption("Forward", 0.5),
                    new TrainDeckAxisOption("Shutdown", 1, Danger: true)
                ];
            }
            else if (controlName.Contains("Reverser_IrregularLever", StringComparison.OrdinalIgnoreCase)
                || (reverser.TargetMin == 0.25 && reverser.TargetNeutral == 0.5 && reverser.TargetMax == 0.75))
            {
                options["reverser"] =
                [
                    new TrainDeckAxisOption("Reverse", -1),
                    new TrainDeckAxisOption("Neutral", 0),
                    new TrainDeckAxisOption("Forward", 1)
                ];
            }
        }

        return options;
    }

    public bool TryMap(string control, double value, out TswHttpApiAxisCommand command)
    {
        command = default!;
        if (!currentProfile.Axes.TryGetValue(control, out var bindings) || bindings.Count == 0)
        {
            return false;
        }

        var outputs = new List<TswHttpApiAxisOutput>(bindings.Count);
        foreach (var binding in bindings)
        {
            var controlName = ResolveControlName(binding.ControlName);
            var mapped = binding.ConstantValue ?? MapValue(binding, value);
            var outputKey = $"{control}|{controlName}";
            if (lastValues.TryGetValue(outputKey, out var previous) && Math.Abs(previous - mapped) < 0.005)
            {
                continue;
            }

            lastValues[outputKey] = mapped;
            outputs.Add(new TswHttpApiAxisOutput(controlName, mapped));
        }

        if (outputs.Count == 0)
        {
            return false;
        }

        command = new TswHttpApiAxisCommand(control, outputs);
        return true;
    }

    public bool TryMapButton(string command, out TswHttpApiButtonCommand buttonCommand)
    {
        buttonCommand = default!;
        if (!currentProfile.Buttons.TryGetValue(command, out var binding) || binding.Steps.Count == 0)
        {
            return false;
        }

        var steps = binding.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ControlName))
            .Select(step => new TswHttpApiButtonStepCommand(
                ResolveControlName(step.ControlName),
                step.Value,
                step.ReleaseValue,
                step.HoldMs,
                step.DelayBeforeMs,
                step.DelayAfterMs,
                step.CycleValues))
            .ToList();
        if (steps.Count == 0)
        {
            return false;
        }

        buttonCommand = new TswHttpApiButtonCommand(command, steps);
        return true;
    }

    private string ResolveControlName(string controlName)
    {
        return controlName
            .Replace("{SIDE}", activeSide, StringComparison.OrdinalIgnoreCase)
            .Replace("{SIDE:front:back}", activeSide == "B" ? "back" : "front", StringComparison.OrdinalIgnoreCase);
    }

    private static double MapValue(TswHttpApiAxisBinding binding, double value)
    {
        var clamped = Math.Max(binding.SourceMin, Math.Min(binding.SourceMax, value));
        if (binding.SourceNeutral is not null && binding.TargetNeutral is not null)
        {
            var sourceNeutral = binding.SourceNeutral.Value;
            var targetNeutral = binding.TargetNeutral.Value;
            if (clamped >= sourceNeutral)
            {
                var normalized = (clamped - sourceNeutral) / Math.Max(0.0001, binding.SourceMax - sourceNeutral);
                return targetNeutral + (binding.TargetMax - targetNeutral) * normalized;
            }

            var brakeNormalized = (sourceNeutral - clamped) / Math.Max(0.0001, sourceNeutral - binding.SourceMin);
            return targetNeutral - (targetNeutral - binding.TargetMin) * brakeNormalized;
        }

        var normalizedLinear = (clamped - binding.SourceMin) / Math.Max(0.0001, binding.SourceMax - binding.SourceMin);
        return binding.TargetMin + (binding.TargetMax - binding.TargetMin) * normalizedLinear;
    }
}

internal sealed class TswHttpApiProfileCatalog
{
    private static readonly JsonSerializerOptions ProfileReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions ProfileWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IReadOnlyList<TswHttpApiProfile> profiles;

    private TswHttpApiProfileCatalog(IReadOnlyList<TswHttpApiProfile> profiles)
    {
        this.profiles = profiles;
        DefaultProfile = profiles.FirstOrDefault(profile => profile.Default)
            ?? profiles.FirstOrDefault()
            ?? TswHttpApiProfile.CreateFallback();
    }

    public TswHttpApiProfile DefaultProfile { get; }

    public static TswHttpApiProfileCatalog Load(Action<string> log)
    {
        try
        {
            var userPath = EnsureUserProfileFile();
            var bundledPath = BundledProfilePath;

            var profilePath = File.Exists(userPath) ? userPath : bundledPath;
            if (!File.Exists(profilePath))
            {
                log("TSW profile file not found; using built-in generic API mapping.");
                return new TswHttpApiProfileCatalog([TswHttpApiProfile.CreateFallback()]);
            }

            var file = JsonSerializer.Deserialize<TswHttpApiProfileFile>(File.ReadAllText(profilePath), ProfileReadOptions);
            var profiles = file?.Profiles?
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id)
                    && (profile.Default || profile.Axes.Count > 0 || profile.Buttons.Count > 0))
                .ToList();
            if (profiles is null || profiles.Count == 0)
            {
                log("TSW profile file had no usable profiles; using built-in generic API mapping.");
                return new TswHttpApiProfileCatalog([TswHttpApiProfile.CreateFallback()]);
            }

            log($"Loaded {profiles.Count} TSW API profiles from {profilePath}.");
            return new TswHttpApiProfileCatalog(profiles);
        }
        catch (Exception ex)
        {
            log($"TSW profile load failed: {ex.Message}; using built-in generic API mapping.");
            return new TswHttpApiProfileCatalog([TswHttpApiProfile.CreateFallback()]);
        }
    }

    public TswHttpApiProfile Match(string actorClass)
    {
        if (string.IsNullOrWhiteSpace(actorClass))
        {
            return DefaultProfile;
        }

        var candidateProfiles = profiles.Where(profile => !profile.Default).ToList();
        foreach (var profile in candidateProfiles)
        {
            if (profile.MatchActorClasses.Any(match => string.Equals(match, actorClass, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        foreach (var profile in candidateProfiles)
        {
            if (profile.MatchActorContains.Any(match => actorClass.Contains(match, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        return DefaultProfile;
    }

    public static TswProfileAutoMapResult MergeSnapshot(
        string actorClass,
        IReadOnlyList<TswCabControlSnapshot> controls,
        Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(actorClass))
        {
            return new TswProfileAutoMapResult(false, 0, "Cab automap skipped: actor unknown.");
        }

        try
        {
            var userPath = EnsureUserProfileFile();
            var file = File.Exists(userPath)
                ? JsonSerializer.Deserialize<TswHttpApiProfileFile>(File.ReadAllText(userPath), ProfileReadOptions)
                : null;
            file ??= new TswHttpApiProfileFile();

            var profile = FindProfileForActor(file, actorClass);
            if (profile is null)
            {
                profile = new TswHttpApiProfile
                {
                    Id = MakeProfileId(actorClass),
                    Name = MakeProfileName(actorClass),
                    MatchActorClasses = [actorClass]
                };
                file.Profiles.Add(profile);
            }

            if (!profile.MatchActorClasses.Any(match => string.Equals(match, actorClass, StringComparison.OrdinalIgnoreCase)))
            {
                profile.MatchActorClasses.Add(actorClass);
            }

            NormalizeDictionaries(profile);
            var mapped = InferMappings(profile, controls);
            if (mapped == 0)
            {
                return new TswProfileAutoMapResult(false, 0, $"Cab automap found no obvious controls for {profile.Name}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
            File.WriteAllText(userPath, JsonSerializer.Serialize(file, ProfileWriteOptions));
            log($"Cab automap updated {profile.Name} with {mapped} bindings from snapshot.");
            return new TswProfileAutoMapResult(true, mapped, $"Cab automap updated {profile.Name}");
        }
        catch (Exception ex)
        {
            log($"Cab automap failed: {ex.Message}");
            return new TswProfileAutoMapResult(false, 0, $"Cab automap failed: {ex.Message}");
        }
    }

    private static string AppDataProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TrainDeck",
        "profiles");

    private static string UserProfilePath => Path.Combine(AppDataProfileDir, "tsw-api-profiles.json");

    private static string BundledProfilePath => Path.Combine(AppContext.BaseDirectory, "profiles", "tsw-api-profiles.json");

    private static string EnsureUserProfileFile()
    {
        Directory.CreateDirectory(AppDataProfileDir);
        if (File.Exists(BundledProfilePath) && !File.Exists(UserProfilePath))
        {
            File.Copy(BundledProfilePath, UserProfilePath, overwrite: true);
        }

        return UserProfilePath;
    }

    private static TswHttpApiProfile? FindProfileForActor(TswHttpApiProfileFile file, string actorClass)
    {
        foreach (var profile in file.Profiles.Where(profile => !profile.Default))
        {
            if (profile.MatchActorClasses.Any(match => string.Equals(match, actorClass, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        foreach (var profile in file.Profiles.Where(profile => !profile.Default))
        {
            if (profile.MatchActorContains.Any(match => actorClass.Contains(match, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        return null;
    }

    private static int InferMappings(TswHttpApiProfile profile, IReadOnlyList<TswCabControlSnapshot> controls)
    {
        var mapped = 0;
        TswCabControlSnapshot? byName(string name) => controls.FirstOrDefault(
            control => string.Equals(control.Name, name, StringComparison.OrdinalIgnoreCase));
        TswCabControlSnapshot? byId(string identifier) => controls.FirstOrDefault(
            control => string.Equals(control.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
        List<TswCabControlSnapshot> allById(string identifier) => controls
            .Where(control => string.Equals(control.Identifier, identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!profile.Axes.ContainsKey("reverser") && byId("Reverser") is { } reverser)
        {
            var masterControlSwitch = reverser.Name.Contains("MasterControlSwitch", StringComparison.OrdinalIgnoreCase);
            var threePosition = reverser.NormalizedValue is >= 0.32 and <= 0.68
                || reverser.InputValue is >= 0.35 and <= 0.65
                || string.Equals(reverser.Name, "Reverser", StringComparison.OrdinalIgnoreCase);
            mapped += SetAxis(profile, "reverser", new TswHttpApiAxisBinding
            {
                ControlName = reverser.Name,
                SourceMin = -1,
                SourceMax = 1,
                TargetMin = threePosition || masterControlSwitch ? 0 : -1,
                TargetMax = 1,
                SourceNeutral = threePosition || masterControlSwitch ? 0 : null,
                TargetNeutral = threePosition || masterControlSwitch ? 0.5 : null
            });
            if (IsCombinedReverserKeyControl(reverser, masterControlSwitch, threePosition))
            {
                mapped += SetCycle(profile, "reverser_key", reverser.Name, [0, 0.5]);
            }
        }

        var throttle = byId("Throttle")
            ?? controls.FirstOrDefault(control =>
                control.Name.Contains("PowerHandle", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("MasterController", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("TractionBrake", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("Throttle", StringComparison.OrdinalIgnoreCase));
        if (!profile.Axes.ContainsKey("throttle") && throttle is not null)
        {
            mapped += SetAxis(profile, "throttle", new TswHttpApiAxisBinding
            {
                ControlName = throttle.Name,
                SourceMin = 0,
                SourceMax = 1,
                TargetMin = throttle.Name.Contains("Power", StringComparison.OrdinalIgnoreCase)
                    || throttle.Name.Contains("TractionBrake", StringComparison.OrdinalIgnoreCase)
                    || throttle.Name.Contains("MasterController", StringComparison.OrdinalIgnoreCase)
                        ? -1
                        : 0,
                TargetMax = 1,
                SourceNeutral = throttle.Name.Contains("Power", StringComparison.OrdinalIgnoreCase)
                    || throttle.Name.Contains("TractionBrake", StringComparison.OrdinalIgnoreCase)
                    || throttle.Name.Contains("MasterController", StringComparison.OrdinalIgnoreCase)
                        ? 0.5
                        : null,
                TargetNeutral = throttle.Name.Contains("Power", StringComparison.OrdinalIgnoreCase)
                    || throttle.Name.Contains("TractionBrake", StringComparison.OrdinalIgnoreCase)
                    || throttle.Name.Contains("MasterController", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : null
            });
        }

        var horn = byId("Horn");
        mapped += SetMomentary(profile, "horn", horn?.Name, 250, NeutralReleaseValue(horn));
        var masterSwitch = byId("MasterSwitch");
        if (masterSwitch is not null)
        {
            mapped += SetSequence(profile, "master_key_slide", [new TswHttpApiButtonStep
            {
                ControlName = masterSwitch.Name,
                Value = 1,
                HoldMs = 0,
                DelayAfterMs = 40
            }]);
        }
        mapped += SetCycle(profile, "reverser_key", FindReverserKeyControl(controls)?.Name, [0, 1]);

        mapped += SetMomentary(profile, "bell", byId("Bell")?.Name, 140);
        mapped += SetMomentary(profile, "guard_buzzer", byId("Bell")?.Name, 140);
        mapped += SetMomentary(profile, "aws_reset", byId("AWS_Reset")?.Name, 120);
        mapped += SetMomentary(profile, "alerter", byId("AWS_Reset")?.Name ?? byId("Alerter")?.Name, 120);
        mapped += SetMomentary(profile, "sander", byId("Sand")?.Name, 250);
        if (FindWiperControl(controls) is { } wiperControl)
        {
            mapped += IsWiperCycleControl(wiperControl.Name)
                ? SetCycle(profile, "wipers", wiperControl.Name, [0, 0.5, 1])
                : SetMomentary(profile, "wipers", wiperControl.Name, 100);
        }
        mapped += SetMomentary(profile, "dra", byId("DRA_Reset")?.Name, 120);
        var tailLight = byId("HeadlightsBack")
            ?? controls.FirstOrDefault(control =>
                control.Name.Contains("TailLight", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("TailLights", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("MarkerLight", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("MarkerLights", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("HeadMarker", StringComparison.OrdinalIgnoreCase));
        mapped += SetLightControl(profile, "tail_lights", tailLight, 120);
        mapped += SetLightControl(profile, "marker_lights", tailLight, 120);
        mapped += SetMomentary(profile, "ditch_lights", byId("DitchLights")?.Name, 120);
        mapped += SetMomentary(profile, "cab_light", byId("CabLights")?.Name, 120);
        mapped += SetMomentary(profile, "gauge_light", byId("GaugeLights")?.Name, 120);
        mapped += SetMomentary(profile, "couple", byId("CoupleButton")?.Name, 140);
        mapped += SetMomentary(profile, "uncouple", byId("UncoupleButton")?.Name, 140);

        mapped += SetMomentary(profile, "door_left", FindDoorControl(controls, "L", release: true), 140);
        mapped += SetMomentary(profile, "door_right", FindDoorControl(controls, "R", release: true), 140);
        mapped += SetMomentary(profile, "door_close_left", FindDoorControl(controls, "L", release: false), 140);
        mapped += SetMomentary(profile, "door_close_right", FindDoorControl(controls, "R", release: false), 140);

        var allRelease = new[] { byName("DoorAllRelease_L")?.Name, byName("DoorAllRelease_R")?.Name }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        mapped += SetMomentarySequence(profile, "door_release", allRelease, 140);

        var panUpShoesDown = byName("PanUpShoesDown")?.Name;
        mapped += SetPowerChangeover(profile, "power_change_ctrl", byName("CTRL_Button")?.Name, panUpShoesDown);
        mapped += SetPowerChangeover(profile, "power_change_dc", byName("DC_Button")?.Name, panUpShoesDown);

        var engineControls = allById("EngineStartStop");
        mapped += SetMomentary(profile, "engine_start", engineControls.FirstOrDefault(
            control => control.Name.Contains("On", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("Start", StringComparison.OrdinalIgnoreCase))?.Name, 140);
        mapped += SetMomentary(profile, "engine_stop", engineControls.FirstOrDefault(
            control => control.Name.Contains("Off", StringComparison.OrdinalIgnoreCase)
                || control.Name.Contains("Stop", StringComparison.OrdinalIgnoreCase))?.Name, 140);

        var breakerControls = allById("CircuitBreaker");
        mapped += SetMomentary(profile, "mcb_close", breakerControls.FirstOrDefault(
            control => control.Name.Contains("MCB", StringComparison.OrdinalIgnoreCase)
                && control.Name.Contains("Close", StringComparison.OrdinalIgnoreCase))?.Name, 140);
        mapped += SetMomentary(profile, "mcb_open", breakerControls.FirstOrDefault(
            control => control.Name.Contains("MCB", StringComparison.OrdinalIgnoreCase)
                && control.Name.Contains("Open", StringComparison.OrdinalIgnoreCase))?.Name, 140);
        mapped += SetMomentary(profile, "vcb_close", breakerControls.FirstOrDefault(
            control => control.Name.Contains("VCB", StringComparison.OrdinalIgnoreCase)
                && control.Name.Contains("Close", StringComparison.OrdinalIgnoreCase))?.Name, 140);
        mapped += SetMomentary(profile, "vcb_open", breakerControls.FirstOrDefault(
            control => control.Name.Contains("VCB", StringComparison.OrdinalIgnoreCase)
                && control.Name.Contains("Open", StringComparison.OrdinalIgnoreCase))?.Name, 140);

        var emergencyBrake = allById("EmergencyBrake").Select(control => control.Name).ToArray();
        if (emergencyBrake.Length > 0)
        {
            mapped += SetSequence(profile, "emergency_reset", emergencyBrake.Select(name => new TswHttpApiButtonStep
            {
                ControlName = name,
                Value = 0,
                HoldMs = 0,
                DelayAfterMs = 40
            }));
        }

        return mapped;
    }

    private static TswCabControlSnapshot? FindReverserKeyControl(IReadOnlyList<TswCabControlSnapshot> controls)
    {
        return controls.FirstOrDefault(control =>
                control.Name.Contains("Reverser", StringComparison.OrdinalIgnoreCase)
                && control.Name.Contains("Key", StringComparison.OrdinalIgnoreCase))
            ?? controls.FirstOrDefault(control =>
                control.Name.Contains("DirectionSelector", StringComparison.OrdinalIgnoreCase)
                && control.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(control.Identifier, "Reverser", StringComparison.OrdinalIgnoreCase))
            ?? controls.FirstOrDefault(control =>
                control.Name.Contains("Reverser", StringComparison.OrdinalIgnoreCase)
                && (control.Name.Contains("Lock", StringComparison.OrdinalIgnoreCase)
                    || control.Name.Contains("Unlock", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsCombinedReverserKeyControl(
        TswCabControlSnapshot reverser,
        bool masterControlSwitch,
        bool threePosition)
    {
        return !masterControlSwitch
            && threePosition
            && string.Equals(reverser.Name, "Reverser", StringComparison.OrdinalIgnoreCase)
            && (reverser.NormalizedValue is >= 0.32 and <= 0.68
                || reverser.InputValue is >= 0.35 and <= 0.65);
    }

    private static TswCabControlSnapshot? FindWiperControl(IReadOnlyList<TswCabControlSnapshot> controls)
    {
        return controls.FirstOrDefault(control =>
                string.Equals(control.Identifier, "Wipers", StringComparison.OrdinalIgnoreCase)
                && IsWiperCycleControl(control.Name))
            ?? controls.FirstOrDefault(control =>
                string.Equals(control.Identifier, "Wipers", StringComparison.OrdinalIgnoreCase)
                && !control.Name.Contains("Wash", StringComparison.OrdinalIgnoreCase))
            ?? controls.FirstOrDefault(control =>
                string.Equals(control.Identifier, "WipersAlt", StringComparison.OrdinalIgnoreCase)
                && IsWiperCycleControl(control.Name))
            ?? controls.FirstOrDefault(control => IsWiperCycleControl(control.Name));
    }

    private static bool IsWiperCycleControl(string name)
    {
        return name.Contains("WiperControls", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WiperControl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WiperSpeed", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WiperMode", StringComparison.OrdinalIgnoreCase);
    }

    private static double NeutralReleaseValue(TswCabControlSnapshot? control)
    {
        if (control?.InputValue is > 0.05 and < 0.95)
        {
            return control.InputValue;
        }

        return 0;
    }

    private static int SetAxis(TswHttpApiProfile profile, string control, TswHttpApiAxisBinding binding)
    {
        profile.Axes[control] = [binding];
        return 1;
    }

    private static string? FindDoorControl(IReadOnlyList<TswCabControlSnapshot> controls, string side, bool release)
    {
        var actionNames = release
            ? new[] { "DoorRelease", "DoorsRelease", "DoorAllRelease", "DoorsAllRelease", "OpenDoors", "OpenDoor" }
            : ["DoorClose", "DoorsClose", "DoorCloseInterlock", "DoorsCloseInterlock", "CloseDoors", "CloseDoor"];

        foreach (var actionName in actionNames)
        {
            var exact = controls.FirstOrDefault(control =>
                string.Equals(control.Name, $"{actionName}_{side}", StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact.Name;
            }
        }

        var actionText = release ? "Open" : "Close";
        return controls.FirstOrDefault(control =>
            control.Name.Contains("PassengerDoor", StringComparison.OrdinalIgnoreCase)
            && control.Name.Contains(actionText, StringComparison.OrdinalIgnoreCase)
            && control.Name.EndsWith($"_{side}", StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private static int SetMomentary(TswHttpApiProfile profile, string command, string? controlName, int holdMs)
    {
        return SetMomentary(profile, command, controlName, holdMs, 0);
    }

    private static int SetMomentary(TswHttpApiProfile profile, string command, string? controlName, int holdMs, double releaseValue)
    {
        return string.IsNullOrWhiteSpace(controlName)
            ? 0
            : SetMomentarySequence(profile, command, [controlName], holdMs, releaseValue);
    }

    private static int SetLightControl(TswHttpApiProfile profile, string command, TswCabControlSnapshot? control, int holdMs)
    {
        if (control is null)
        {
            return 0;
        }

        return control.Name.Contains("PushButton", StringComparison.OrdinalIgnoreCase)
            || control.Identifier.Contains("Button", StringComparison.OrdinalIgnoreCase)
            ? SetMomentary(profile, command, control.Name, holdMs)
            : SetCycle(profile, command, control.Name, [0, 1]);
    }

    private static int SetMomentarySequence(TswHttpApiProfile profile, string command, IReadOnlyList<string> controlNames, int holdMs)
    {
        return SetMomentarySequence(profile, command, controlNames, holdMs, 0);
    }

    private static int SetMomentarySequence(TswHttpApiProfile profile, string command, IReadOnlyList<string> controlNames, int holdMs, double releaseValue)
    {
        return controlNames.Count == 0
            ? 0
            : SetSequence(profile, command, controlNames.Select(controlName => new TswHttpApiButtonStep
            {
                ControlName = controlName,
                Value = 1,
                ReleaseValue = releaseValue,
                HoldMs = holdMs,
                DelayAfterMs = 40
            }));
    }

    private static int SetCycle(TswHttpApiProfile profile, string command, string? controlName, IReadOnlyList<double> cycleValues)
    {
        return string.IsNullOrWhiteSpace(controlName) || cycleValues.Count == 0
            ? 0
            : SetSequence(profile, command, [new TswHttpApiButtonStep
            {
                ControlName = controlName,
                HoldMs = 0,
                DelayAfterMs = 40,
                CycleValues = cycleValues.ToList()
            }]);
    }

    private static int SetPowerChangeover(
        TswHttpApiProfile profile,
        string command,
        string? powerModeControlName,
        string? panUpShoesDownControlName)
    {
        if (string.IsNullOrWhiteSpace(powerModeControlName)
            || string.IsNullOrWhiteSpace(panUpShoesDownControlName)
            || profile.Buttons.ContainsKey(command))
        {
            return 0;
        }

        return SetSequence(profile, command, [
            new TswHttpApiButtonStep
            {
                ControlName = "Reverser",
                Value = 0.5,
                HoldMs = 0,
                DelayAfterMs = 250
            },
            new TswHttpApiButtonStep
            {
                ControlName = powerModeControlName,
                Value = 1,
                ReleaseValue = 0,
                HoldMs = 5200,
                DelayAfterMs = 700
            },
            new TswHttpApiButtonStep
            {
                ControlName = panUpShoesDownControlName,
                Value = 1,
                ReleaseValue = 0,
                HoldMs = 220,
                DelayAfterMs = 1200
            },
            new TswHttpApiButtonStep
            {
                ControlName = panUpShoesDownControlName,
                Value = 1,
                ReleaseValue = 0,
                HoldMs = 220,
                DelayAfterMs = 80
            }
        ]);
    }

    private static int SetSequence(TswHttpApiProfile profile, string command, IEnumerable<TswHttpApiButtonStep> steps)
    {
        var stepList = steps.Where(step => !string.IsNullOrWhiteSpace(step.ControlName)).ToList();
        if (stepList.Count == 0)
        {
            return 0;
        }

        profile.Buttons[command] = new TswHttpApiButtonBinding { Steps = stepList };
        return 1;
    }

    private static void NormalizeDictionaries(TswHttpApiProfile profile)
    {
        profile.Axes = new Dictionary<string, List<TswHttpApiAxisBinding>>(profile.Axes, StringComparer.OrdinalIgnoreCase);
        profile.Buttons = new Dictionary<string, TswHttpApiButtonBinding>(profile.Buttons, StringComparer.OrdinalIgnoreCase);
    }

    private static string MakeProfileId(string actorClass)
    {
        var id = Regex.Replace(actorClass.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(id) ? "auto_profile" : id;
    }

    private static string MakeProfileName(string actorClass)
    {
        var name = Regex.Replace(actorClass, "^RVM_", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, "_C$", "", RegexOptions.IgnoreCase);
        name = name.Replace('_', ' ').Replace('-', ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(name) ? "Auto-mapped TSW profile" : $"Auto {name}";
    }
}

internal sealed class TswHttpApiProfileFile
{
    public List<TswHttpApiProfile> Profiles { get; set; } = [];
}

internal sealed class TswHttpApiProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Default { get; set; }
    public List<string> MatchActorClasses { get; set; } = [];
    public List<string> MatchActorContains { get; set; } = [];
    public Dictionary<string, List<TswHttpApiAxisBinding>> Axes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TswHttpApiButtonBinding> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static TswHttpApiProfile CreateFallback()
    {
        return new TswHttpApiProfile
        {
            Id = "generic_tsw",
            Name = "Generic TSW combined master",
            Default = true,
            Axes = new Dictionary<string, List<TswHttpApiAxisBinding>>(StringComparer.OrdinalIgnoreCase)
            {
                ["reverser"] = [new TswHttpApiAxisBinding { ControlName = "Reverser", SourceMin = -1, SourceMax = 1, TargetMin = -1, TargetMax = 1 }],
                ["throttle"] = [new TswHttpApiAxisBinding { ControlName = "MasterController", SourceMin = 0, SourceMax = 1, TargetMin = -0.9, TargetMax = 1, SourceNeutral = 0.5, TargetNeutral = 0 }],
                ["dynamic_brake"] = [new TswHttpApiAxisBinding { ControlName = "DynamicBrake", SourceMin = 0, SourceMax = 1, TargetMin = 0, TargetMax = 1 }],
                ["train_brake"] = [new TswHttpApiAxisBinding { ControlName = "TrainBrake (Irregular Lever)", SourceMin = 0, SourceMax = 1, TargetMin = 0, TargetMax = 0.8 }],
                ["independent_brake"] = [new TswHttpApiAxisBinding { ControlName = "IndependentBrake", SourceMin = 0, SourceMax = 1, TargetMin = 0, TargetMax = 1 }]
            }
        };
    }
}

internal sealed class TswHttpApiAxisBinding
{
    public string ControlName { get; set; } = "";
    public double SourceMin { get; set; }
    public double SourceMax { get; set; } = 1;
    public double TargetMin { get; set; }
    public double TargetMax { get; set; } = 1;
    public double? SourceNeutral { get; set; }
    public double? TargetNeutral { get; set; }
    public double? ConstantValue { get; set; }
}

internal sealed class TswHttpApiButtonBinding
{
    public List<TswHttpApiButtonStep> Steps { get; set; } = [];
}

internal sealed class TswHttpApiButtonStep
{
    public string ControlName { get; set; } = "";
    public double Value { get; set; } = 1;
    public double? ReleaseValue { get; set; }
    public int HoldMs { get; set; } = 90;
    public int DelayBeforeMs { get; set; }
    public int DelayAfterMs { get; set; } = 100;
    public List<double> CycleValues { get; set; } = [];
}

internal sealed record TswHttpApiAxisOutput(string ControlName, double Value);
internal sealed record TswHttpApiAxisCommand(string SourceControl, IReadOnlyList<TswHttpApiAxisOutput> Outputs)
{
    public string DisplayControls => string.Join(", ", Outputs.Select(output => output.ControlName));
}
internal sealed record TswHttpApiButtonStepCommand(string ControlName, double Value, double? ReleaseValue, int HoldMs, int DelayBeforeMs, int DelayAfterMs, IReadOnlyList<double> CycleValues);
internal sealed record TswHttpApiButtonCommand(string SourceCommand, IReadOnlyList<TswHttpApiButtonStepCommand> Steps);
internal sealed record TswHttpApiStatusEventArgs(bool Ready, string Status);
internal sealed record TswCabControlSnapshot(string Name, double InputValue, double? NormalizedValue, string Identifier);
internal sealed record TswProfileAutoMapResult(bool Changed, int MappedCount, string Message);
