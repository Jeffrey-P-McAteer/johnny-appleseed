using System.Text.Json;
using System.Text.Json.Serialization;

namespace JohnnyAppleseed.Narrative;

/// <summary>
/// System.Text.Json source-generation context for the authored content files.
///
/// <see cref="JsonCommentHandling.Skip"/> plus <c>AllowTrailingCommas</c> is what
/// gives writers a "primitive comment" facility in the otherwise strict JSON
/// files: <c>//</c> line and <c>/* … */</c> block comments (and trailing commas)
/// are accepted and ignored, so authors can annotate items/characters as they
/// write. Source-gen keeps this trim/AOT- and single-file-safe like the save context.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ItemDef[]))]
[JsonSerializable(typeof(CharacterDef[]))]
internal partial class ContentJsonContext : JsonSerializerContext
{
}
