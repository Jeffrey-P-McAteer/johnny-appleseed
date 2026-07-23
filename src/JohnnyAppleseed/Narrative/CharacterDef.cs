using System.Text.Json.Serialization;

namespace JohnnyAppleseed.Narrative;

/// <summary>
/// A presentation-agnostic character definition, authored in
/// <c>story/characters/characters.jsonc</c>. Shared by the story scene (portrait
/// + name colour on dialogue) and the future map/codex (the same
/// <see cref="Description"/>), looked up by <see cref="Id"/>.
/// </summary>
sealed class CharacterDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Display name shown on dialogue and in the codex.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Shared descriptive/biographical text.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Embedded texture key for the portrait, e.g. "graphics/portraits/johnny.png".</summary>
    [JsonPropertyName("portrait")]
    public string Portrait { get; set; } = "";

    /// <summary>Optional dialogue name/text colour as "#RRGGBB"; the presenter parses it.</summary>
    [JsonPropertyName("textColor")]
    public string? TextColor { get; set; }
}
