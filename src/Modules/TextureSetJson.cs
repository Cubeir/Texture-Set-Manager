using System.Text.Json;
using System.Text.Json.Serialization;

namespace Texture_Set_Manager.Modules;

// ══════════════════════════════════════════════════════════════════════════════
//  Texture set JSON  ──  shared options and the source-generated write model
// ══════════════════════════════════════════════════════════════════════════════
//
// Release builds publish trimmed (see PublishTrimmed in the csproj), and reflection-based
// System.Text.Json is exactly the kind of thing that survives a Debug run and then throws
// "no metadata for type" in the user's hands. So: everything this app *writes* goes through
// the source-generated context below (no reflection, nothing for the trimmer to remove), and
// everything it *reads* goes through the JsonNode/JsonDocument DOM, which never needs type
// metadata at all.

public static class TextureSetJson
{
    /// <summary>
    /// Parsing tolerance for texture sets we didn't write. Resource packs are hand-edited far
    /// more often than they're generated, and the previous JSON stack accepted comments and
    /// trailing commas – System.Text.Json rejects both by default, so a pack that opened fine
    /// yesterday would suddenly be "invalid". These options keep that tolerance.
    /// </summary>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>Formatting for JSON we write back out. Indented, to stay hand-editable.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };
}

/// <summary>
/// The shape this app generates. Deliberately a plain model rather than a hand-built DOM: it
/// makes the emitted key order explicit and lets the source generator produce the writer.
/// </summary>
public sealed class TextureSetDocument
{
    [JsonPropertyName("format_version")]
    public string FormatVersion { get; set; } = "1.21.30";

    [JsonPropertyName("minecraft:texture_set")]
    public TextureSetLayers TextureSet { get; set; } = new();
}

/// <summary>
/// MER and MERS are mutually exclusive, as are normal and heightmap – the unused ones stay null
/// and are omitted entirely rather than written as null, which the game would reject.
/// </summary>
public sealed class TextureSetLayers
{
    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("metalness_emissive_roughness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetalnessEmissiveRoughness { get; set; }

    [JsonPropertyName("metalness_emissive_roughness_subsurface")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetalnessEmissiveRoughnessSubsurface { get; set; }

    [JsonPropertyName("normal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Normal { get; set; }

    [JsonPropertyName("heightmap")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Heightmap { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TextureSetDocument))]
internal sealed partial class TextureSetJsonContext : JsonSerializerContext
{
}
