using System.Text.Json.Serialization;

namespace AshenTasker.Models.Macros;

public sealed class MacroDocument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Macro";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedUtc")]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<MacroAction> Actions { get; set; } = [];
}
