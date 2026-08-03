using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// The authored prompt placeholder for one art set (a background or a portrait),
/// read from the embedded <c>content/ai-prompts.jsonc</c>. Adding an entry here is
/// the single authoring action that "unlocks" auto-generation for a slot: when a
/// real weather/season/daylight asset is missing, the system may generate one from
/// <see cref="Base"/> (img2img) using the matching <see cref="Conditions"/> fragment.
///
/// This lets writers/artists add real assets at their own pace: until a hand-made
/// <c>backdrop.rainy.png</c> exists, a generated placeholder fills in so testing
/// never stalls; drop in the real file later and it wins on the next resolve.
/// </summary>
sealed class AiPromptSet
{
    /// <summary>
    /// img2img source asset key, e.g. "graphics/main-menu/backdrop.jpg". Optional: when
    /// omitted, the scene supplies the base it was about to show (e.g. an ink <c># bg:</c>
    /// image), so a background can get weather editions without repeating its path here.
    /// </summary>
    [JsonPropertyName("base")]
    public string Base { get; set; } = "";

    /// <summary>
    /// Generation mode: <c>"img2img"</c> (default) restyles the base/scene image so the
    /// composition is preserved - the right choice for weather editions of existing art;
    /// <c>"direct"</c> generates purely from the prompt (txt2img), for slots with no base.
    /// Direct mode needs the neural engine; the default procedural engine is img2img-only.
    /// </summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>Optional img2img parameters; sensible defaults when omitted.</summary>
    [JsonPropertyName("img2img")]
    public AiImg2ImgParams? Img2Img { get; set; }

    /// <summary>
    /// Condition tag (a <see cref="Ambient.ConditionVocab"/> tag such as "rainy",
    /// "winter", "night") -> the prompt fragment describing that variant.
    /// </summary>
    [JsonPropertyName("conditions")]
    public Dictionary<string, string> Conditions { get; set; } = new();
}

/// <summary>img2img knobs. Low strength keeps the base composition/identity; few steps for CPU speed.</summary>
sealed class AiImg2ImgParams
{
    /// <summary>Denoise strength 0..1; ~0.35-0.5 transforms sky/light while keeping the scene.</summary>
    [JsonPropertyName("strength")]
    public float Strength { get; set; } = 0.45f;

    /// <summary>Denoising steps (LCM/few-step schedule; 4 is a good CPU default).</summary>
    [JsonPropertyName("steps")]
    public int Steps { get; set; } = 4;
}

/// <summary>
/// System.Text.Json source-generation context for the AI subsystem - trim/AOT/
/// single-file safe, exactly like <see cref="Save.SaveJsonContext"/>. Comments and
/// trailing commas are allowed so the authored <c>.jsonc</c> can be hand-edited.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, AiPromptSet>))]
[JsonSerializable(typeof(AiPromptSet))]
[JsonSerializable(typeof(AiIndex))]
[JsonSerializable(typeof(AiIndexEntry))]
internal partial class AiJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads the authored prompt placeholders. Keyed by art-set key (the same key
/// <see cref="Ambient.ArtVariant"/> resolves), e.g. "graphics/main-menu/backdrop".
/// Best-effort: a missing or malformed file yields an empty map (auto-generation is
/// simply inert until prompts are authored), never an exception into the game.
/// </summary>
static class AiPrompts
{
    public const string EmbeddedKey = "content/ai-prompts.jsonc";

    public static Dictionary<string, AiPromptSet> Parse(string json) =>
        JsonSerializer.Deserialize(json, AiJsonContext.Default.DictionaryStringAiPromptSet)
        ?? new Dictionary<string, AiPromptSet>();

    public static Dictionary<string, AiPromptSet> LoadEmbedded()
    {
        if (!Assets.Exists(EmbeddedKey))
            return new Dictionary<string, AiPromptSet>();
        try
        {
            return Parse(Encoding.UTF8.GetString(Assets.Bytes(EmbeddedKey)));
        }
        catch
        {
            return new Dictionary<string, AiPromptSet>();
        }
    }
}
