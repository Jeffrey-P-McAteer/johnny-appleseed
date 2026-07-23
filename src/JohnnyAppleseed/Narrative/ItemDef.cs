using System.Text.Json.Serialization;

namespace JohnnyAppleseed.Narrative;

/// <summary>
/// A presentation-agnostic inventory-item definition, authored by writers in
/// <c>story/items/items.jsonc</c> and shared by every view of the item — the
/// face-to-face story scene now, and the future RPG-style map/codex later both
/// look items up by <see cref="Id"/> and reuse the same name/description/flavor.
///
/// The story (Ink) files refer to items by <see cref="Id"/> for give/take/has;
/// keep ids stable and lowercase-with-dashes.
/// </summary>
sealed class ItemDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Plain descriptive text shown in tooltips / the codex.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Optional flavor/quote lines rendered as green "greentext" (the classic
    /// <c>&gt;</c> quote style). Purely cosmetic and shared across every view.
    /// </summary>
    [JsonPropertyName("greentext")]
    public string[] Greentext { get; set; } = Array.Empty<string>();

    /// <summary>Embedded texture key for the icon, e.g. "graphics/ui/items/axe.png".</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    /// <summary>Whether multiple copies stack into a single inventory count.</summary>
    [JsonPropertyName("stackable")]
    public bool Stackable { get; set; } = true;
}
