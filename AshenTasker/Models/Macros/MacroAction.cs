using System.Text.Json.Serialization;

namespace AshenTasker.Models.Macros;

public sealed class MacroAction
{
    [JsonPropertyName("type")]
    public MacroActionKind Type { get; set; }

    [JsonPropertyName("timeMs")]
    public long TimeMs { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("button")]
    public MacroMouseButton? Button { get; set; }

    [JsonPropertyName("wheelDelta")]
    public int? WheelDelta { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("virtualKey")]
    public int? VirtualKey { get; set; }
}
