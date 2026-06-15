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
    private readonly TswHttpApiAxisMapper axisMapper = new();
    private readonly Dictionary<string, DateTimeOffset> interactingControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly object statusSync = new();
    private CancellationTokenSource? cts;
    private string? apiKey;
    private string statusText = "not checked";

    public TswHttpApiClient(Action<string> log)
    {
        this.log = log;
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

    public async Task SendAxisAsync(TswHttpApiAxisCommand command)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        try
        {
            await EnsureInteractingAsync(command.ControlName, CancellationToken.None);
            await PatchAsync(
                $"/set/CurrentDrivableActor/{Uri.EscapeDataString(command.ControlName)}.InputValue?Value={command.Value.ToString("0.000000", CultureInfo.InvariantCulture)}",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"API send failed: {ex.Message}");
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

            UpdateStatus(true, string.IsNullOrWhiteSpace(actor) ? "connected" : $"connected: {actor}");
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
    private readonly Dictionary<string, TswHttpApiAxisBinding> bindings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reverser"] = new("Reverser", -1, 1, -1, 1),
        ["throttle"] = new("MasterController", 0, 1, -0.9, 1, 0.5, 0),
        ["dynamic_brake"] = new("DynamicBrake", 0, 1, 0, 1),
        ["train_brake"] = new("TrainBrake (Irregular Lever)", 0, 1, 0, 0.8),
        ["independent_brake"] = new("IndependentBrake", 0, 1, 0, 1)
    };

    private readonly Dictionary<string, double> lastValues = new(StringComparer.OrdinalIgnoreCase);

    public bool IsMapped(string control)
    {
        return bindings.ContainsKey(control);
    }

    public bool TryMap(string control, double value, out TswHttpApiAxisCommand command)
    {
        command = default!;
        if (!bindings.TryGetValue(control, out var binding))
        {
            return false;
        }

        var clamped = Math.Max(binding.SourceMin, Math.Min(binding.SourceMax, value));
        var mapped = MapValue(binding, clamped);
        if (lastValues.TryGetValue(control, out var previous) && Math.Abs(previous - mapped) < 0.005)
        {
            return false;
        }

        lastValues[control] = mapped;
        command = new TswHttpApiAxisCommand(control, binding.ControlName, mapped);
        return true;
    }

    private static double MapValue(TswHttpApiAxisBinding binding, double value)
    {
        if (binding.SourceNeutral is not null && binding.TargetNeutral is not null)
        {
            var sourceNeutral = binding.SourceNeutral.Value;
            var targetNeutral = binding.TargetNeutral.Value;
            if (value >= sourceNeutral)
            {
                var normalized = (value - sourceNeutral) / Math.Max(0.0001, binding.SourceMax - sourceNeutral);
                return targetNeutral + (binding.TargetMax - targetNeutral) * normalized;
            }

            var brakeNormalized = (sourceNeutral - value) / Math.Max(0.0001, sourceNeutral - binding.SourceMin);
            return targetNeutral - (targetNeutral - binding.TargetMin) * brakeNormalized;
        }

        var normalizedLinear = (value - binding.SourceMin) / Math.Max(0.0001, binding.SourceMax - binding.SourceMin);
        return binding.TargetMin + (binding.TargetMax - binding.TargetMin) * normalizedLinear;
    }
}

internal sealed record TswHttpApiAxisBinding(
    string ControlName,
    double SourceMin,
    double SourceMax,
    double TargetMin,
    double TargetMax,
    double? SourceNeutral = null,
    double? TargetNeutral = null);
internal sealed record TswHttpApiAxisCommand(string SourceControl, string ControlName, double Value);
internal sealed record TswHttpApiStatusEventArgs(bool Ready, string Status);
internal sealed record TswCabControlSnapshot(string Name, double InputValue, double? NormalizedValue, string Identifier);
