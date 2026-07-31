using System.Text.Json;
using System.Text.Json.Serialization;

namespace JohnnyAppleseed.UI;

/// <summary>
/// System.Text.Json source-generation context for the authored main-menu layout.
/// Same recipe as <see cref="Narrative.ContentJsonContext"/>: comment-skipping plus
/// trailing commas so artists can annotate <c>content/menu/main-menu.jsonc</c>, and
/// source-gen so it stays trim/AOT- and single-file-safe.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(MenuLayoutDef))]
internal partial class MenuJsonContext : JsonSerializerContext
{
}
