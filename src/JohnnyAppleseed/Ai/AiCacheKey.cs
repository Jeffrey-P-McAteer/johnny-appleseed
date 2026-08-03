using System.Security.Cryptography;
using System.Text;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// The deterministic identity + filename scheme for generated assets - the core of
/// the "skip work if it's already been done" guarantee.
///
/// Everything that can change the output pixels is folded into one short content
/// hash (<see cref="Compute"/>): the schema version, the model + its revision, the
/// SOURCE asset key and its bytes' SHA (so re-arting the base invalidates), the
/// normalized condition tags, the prompt text, and the pipeline parameters. The
/// cached file is named with that hash, so "already generated?" reduces to "does a
/// file bearing this hash exist?" - no regeneration, and a stale input can never be
/// silently reused (it simply hashes to a different, absent name).
///
/// Two names are derived per generation:
///   * <see cref="StoreFileName"/> - the on-disk name, carrying the hash + a
///     human-readable prefix, e.g. graphics__main-menu__backdrop.night.rainy.a1b2c3d4e5f60718.png
///   * <see cref="LogicalKey"/> - a key shaped exactly like an embedded asset key
///     (stem.tag.tag.ext), so <see cref="Ambient.ArtVariant"/> parses and ranks a
///     cached variant with no changes to its selection policy.
///
/// Pure and side-effect-free; unit-tested headlessly in the probe.
/// </summary>
static class AiCacheKey
{
    /// <summary>Bump to invalidate every previously cached asset at once.</summary>
    public const int SchemaVersion = 1;

    // ASCII unit separator - can't appear in any of the joined fields, so the payload
    // is unambiguous (no accidental collisions from field concatenation).
    private const char Sep = '\u001f';

    /// <summary>
    /// The 16-hex content hash identifying one generation. Independent of tag order.
    /// </summary>
    public static string Compute(
        string modelId, string modelRevision,
        string sourceKey, string sourceSha,
        IEnumerable<string> conditionTags,
        string promptText, string paramsJson)
    {
        string payload = string.Join(Sep,
            SchemaVersion.ToString(),
            modelId, modelRevision,
            sourceKey, sourceSha,
            NormalizeTags(conditionTags),
            promptText, paramsJson);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();   // first 8 bytes -> 16 hex
    }

    /// <summary>SHA-256 (hex) of arbitrary source bytes, for the sourceSha field.</summary>
    public static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Condition tags -> lower-cased, de-duplicated, ordinally sorted, dot-joined.
    /// e.g. ["Rainy","night"] -> "night.rainy". Order-independent so the same variant
    /// always hashes and names identically.
    /// </summary>
    public static string NormalizeTags(IEnumerable<string> tags) =>
        string.Join('.', tags
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal));

    /// <summary>Set key -> filename-safe slug: "graphics/main-menu/backdrop" -> "graphics__main-menu__backdrop".</summary>
    public static string SetSlug(string setKey) => setKey.Replace("/", "__");

    /// <summary>
    /// The on-disk store filename. <paramref name="ext"/> includes the dot (".png").
    /// The hash suffix makes it unique and the presence-check trivial.
    /// </summary>
    public static string StoreFileName(string setKey, string tagsSlug, string cacheKey, string ext) =>
        $"{SetSlug(setKey)}.{tagsSlug}.{cacheKey}{ext}";

    /// <summary>
    /// The embedded-style logical key a cached variant advertises to
    /// <see cref="Ambient.ArtVariant"/>: setKey + "." + tags + ext, e.g.
    /// "graphics/main-menu/backdrop.night.rainy.png". <paramref name="ext"/> includes the dot.
    /// </summary>
    public static string LogicalKey(string setKey, string tagsSlug, string ext) =>
        $"{setKey}.{tagsSlug}{ext}";
}
