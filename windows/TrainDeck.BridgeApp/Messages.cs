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

    [JsonPropertyName("at")]
    public long At { get; set; }
}

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
