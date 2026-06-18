using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrainDeck.BridgeApp;

internal sealed class TrainDeckMessage
{
    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("device")]
    public string? Device { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("control")]
    public string? Control { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("dx")]
    public double? DeltaX { get; set; }

    [JsonPropertyName("dy")]
    public double? DeltaY { get; set; }
}

internal sealed class TrainDeckBridgeMessage
{
    [JsonPropertyName("app")]
    public string App { get; set; } = "TrainDeck";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("axes")]
    public List<string> Axes { get; set; } = [];

    [JsonPropertyName("axisOptions")]
    public Dictionary<string, List<TrainDeckAxisOption>> AxisOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("buttons")]
    public List<string> Buttons { get; set; } = [];

    [JsonPropertyName("speedKmh")]
    public double? SpeedKmh { get; set; }

    [JsonPropertyName("speedMph")]
    public double? SpeedMph { get; set; }

    [JsonPropertyName("speedLimitKmh")]
    public double? SpeedLimitKmh { get; set; }

    [JsonPropertyName("speedLimitDistanceM")]
    public double? SpeedLimitDistanceM { get; set; }

    [JsonPropertyName("nextSpeedLimitKmh")]
    public double? NextSpeedLimitKmh { get; set; }

    [JsonPropertyName("nextSpeedLimitDistanceM")]
    public double? NextSpeedLimitDistanceM { get; set; }

    [JsonPropertyName("speedHoldArmed")]
    public bool SpeedHoldArmed { get; set; }

    [JsonPropertyName("speedHoldAutoPilot")]
    public bool SpeedHoldAutoPilot { get; set; }

    [JsonPropertyName("speedHoldTargetKmh")]
    public double? SpeedHoldTargetKmh { get; set; }

    [JsonPropertyName("speedHoldOutput")]
    public double? SpeedHoldOutput { get; set; }

    [JsonPropertyName("speedHoldMode")]
    public string SpeedHoldMode { get; set; } = "";

    [JsonPropertyName("runRecording")]
    public bool RunRecording { get; set; }

    [JsonPropertyName("runRecordingElapsedSeconds")]
    public double? RunRecordingElapsedSeconds { get; set; }

    [JsonPropertyName("at")]
    public long At { get; set; }
}

internal sealed record TrainDeckAxisOption(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("danger")] bool Danger = false);

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
