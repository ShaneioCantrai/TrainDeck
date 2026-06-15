using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

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
                await PatchAsync(
                    $"/set/CurrentDrivableActor/{Uri.EscapeDataString(step.ControlName)}.InputValue?Value={step.Value.ToString("0.000000", CultureInfo.InvariantCulture)}",
                    CancellationToken.None);

                if (step.HoldMs > 0)
                {
                    await Task.Delay(step.HoldMs);
                    await PatchAsync(
                        $"/set/CurrentDrivableActor/{Uri.EscapeDataString(step.ControlName)}.InputValue?Value=0.000000",
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

            var throttleHints = controls
                .Where(c => c.Name.Contains("throttle", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("master", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("power", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("brake", StringComparison.OrdinalIgnoreCase)
                    || c.Name.Contains("reverser", StringComparison.OrdinalIgnoreCase))
                .Take(16)
                .Select(c => $"{c.Name}={c.InputValue:0.000}");
            var hintText = string.Join("; ", throttleHints);
            return string.IsNullOrWhiteSpace(hintText)
                ? $"Cab snapshot saved: {snapshotPath}"
                : $"Cab snapshot saved: {snapshotPath}. Likely controls: {hintText}";
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
            UpdateStatus(true, $"{actorText} | profile: {profile}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            UpdateStatus(false, "bad API key");
        }
        catch (Exception ex)
        {
            UpdateStatus(false, ex.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                ? "TSW not running with -HTTPAPI"
                : $"not connected: {ex.Message}");
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
    private readonly TswHttpApiProfileCatalog catalog;
    private readonly Dictionary<string, double> lastValues = new(StringComparer.OrdinalIgnoreCase);
    private TswHttpApiProfile currentProfile;
    private string activeSide = "F";

    public TswHttpApiAxisMapper(Action<string> log)
    {
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

    public bool IsMapped(string control)
    {
        return currentProfile.Axes.ContainsKey(control);
    }

    public bool IsButtonMapped(string command)
    {
        return currentProfile.Buttons.ContainsKey(command);
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
                step.HoldMs,
                step.DelayBeforeMs,
                step.DelayAfterMs))
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
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TrainDeck",
                "profiles");
            var userPath = Path.Combine(appDataDir, "tsw-api-profiles.json");
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "profiles", "tsw-api-profiles.json");

            Directory.CreateDirectory(appDataDir);
            if (File.Exists(bundledPath)
                && (!File.Exists(userPath)
                    || File.GetLastWriteTimeUtc(bundledPath) > File.GetLastWriteTimeUtc(userPath)))
            {
                File.Copy(bundledPath, userPath, overwrite: true);
            }

            var profilePath = File.Exists(userPath) ? userPath : bundledPath;
            if (!File.Exists(profilePath))
            {
                log("TSW profile file not found; using built-in generic API mapping.");
                return new TswHttpApiProfileCatalog([TswHttpApiProfile.CreateFallback()]);
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var file = JsonSerializer.Deserialize<TswHttpApiProfileFile>(File.ReadAllText(profilePath), options);
            var profiles = file?.Profiles?
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id) && profile.Axes.Count > 0)
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
    public int HoldMs { get; set; } = 90;
    public int DelayBeforeMs { get; set; }
    public int DelayAfterMs { get; set; } = 100;
}

internal sealed record TswHttpApiAxisOutput(string ControlName, double Value);
internal sealed record TswHttpApiAxisCommand(string SourceControl, IReadOnlyList<TswHttpApiAxisOutput> Outputs)
{
    public string DisplayControls => string.Join(", ", Outputs.Select(output => output.ControlName));
}
internal sealed record TswHttpApiButtonStepCommand(string ControlName, double Value, int HoldMs, int DelayBeforeMs, int DelayAfterMs);
internal sealed record TswHttpApiButtonCommand(string SourceCommand, IReadOnlyList<TswHttpApiButtonStepCommand> Steps);
internal sealed record TswHttpApiStatusEventArgs(bool Ready, string Status);
internal sealed record TswCabControlSnapshot(string Name, double InputValue, double? NormalizedValue, string Identifier);
