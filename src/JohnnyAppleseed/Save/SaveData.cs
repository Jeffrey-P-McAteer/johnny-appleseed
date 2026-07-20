using System.Text.Json;
using System.Text.Json.Serialization;

namespace JohnnyAppleseed.Save;

/// <summary>
/// Root save-game document.
///
/// ── Forward/backward compatibility contract ─────────────────────────────────
/// This type is intentionally designed so the format can grow without breaking
/// existing saves in either direction:
///
///   • Adding a field  — old saves simply lack it and deserialize to the field's
///     default; new saves written by a newer build carry it forward.
///   • Removing a field — System.Text.Json ignores JSON members with no matching
///     property, so stale fields don't throw.
///   • A newer build's save loaded by an older build — unknown members are
///     captured in <see cref="Extra"/> and re-serialized untouched, so a
///     round-trip through an older build never destroys newer data.
///   • Breaking reshapes — bump <see cref="SaveSystem.CurrentFormatVersion"/>
///     and add a step in SaveSystem.Migrate().
///
/// Keep every property nullable-safe with a sensible default and NEVER reuse a
/// retired JSON name for a different meaning.
/// </summary>
sealed class SaveData
{
    /// <summary>Schema version of this document. Drives migration on load.</summary>
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = SaveSystem.CurrentFormatVersion;

    /// <summary>BuildInfo.Version of the build that last wrote this file (diagnostics only).</summary>
    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = "";

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Story / progression flags.</summary>
    [JsonPropertyName("story")]
    public StoryProgress Story { get; set; } = new();

    /// <summary>Player-facing profile. Placeholder for future gameplay data.</summary>
    [JsonPropertyName("player")]
    public PlayerState Player { get; set; } = new();

    /// <summary>
    /// Any JSON members not mapped to a property above — preserved verbatim so a
    /// newer save opened (and re-saved) by an older build keeps its extra data.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Progression through the game's scripted content.</summary>
sealed class StoryProgress
{
    /// <summary>
    /// Zero-based index of the intro page the player is currently on.
    /// Resuming re-enters the intro at exactly this page.
    /// </summary>
    [JsonPropertyName("introStep")]
    public int IntroStep { get; set; }

    /// <summary>True once the player has read past the final intro page.</summary>
    [JsonPropertyName("introComplete")]
    public bool IntroComplete { get; set; }

    /// <summary>
    /// Identifier of the furthest scene/checkpoint reached. Free-form string so
    /// new chapters can be added without a schema change. See <see cref="Checkpoint"/>.
    /// </summary>
    [JsonPropertyName("checkpoint")]
    public string Checkpoint { get; set; } = Save.Checkpoint.Intro;
}

/// <summary>Well-known checkpoint identifiers (kept as strings for extensibility).</summary>
static class Checkpoint
{
    public const string Intro = "intro";
    public const string Overworld = "overworld"; // future: first playable scene
}

/// <summary>Placeholder for future player data (name, position, inventory…).</summary>
sealed class PlayerState
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Johnny";
}
