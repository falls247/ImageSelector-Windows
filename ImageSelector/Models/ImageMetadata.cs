using System.Text.Json.Serialization;

namespace ImageSelector.Models;

/// <summary>Portable sidecar format. Positions/styles are intentionally defined by the consuming app.</summary>
public sealed class ImageMetadata
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("imageFile")]
    public string ImageFile { get; set; } = "";

    [JsonPropertyName("annotations")]
    public List<ImageAnnotation> Annotations { get; set; } = new();

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; set; }
}

public sealed class ImageAnnotation
{
    // "dialogue" | "caption" | "sound_effect" | "speech_bubble"
    [JsonPropertyName("type")]
    public string Type { get; set; } = "dialogue";

    // Present only for dialogue: "male" | "female"
    [JsonPropertyName("speaker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Speaker { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    public ImageAnnotation Clone() => new() { Type = Type, Speaker = Speaker, Text = Text };
}
