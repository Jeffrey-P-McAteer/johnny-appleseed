using JohnnyAppleseed.Ambient;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// The AI-aware wrapper around the pure <see cref="ArtVariant"/> selector. It does
/// two things and stays pure (no Raylib, no I/O), so the whole policy is unit-tested
/// headlessly in the probe:
///
///   1. SELECT - merge the embedded candidate keys with any cached (already
///      generated) variant keys and hand the union to <see cref="ArtVariant.Resolve"/>.
///      Because a cached variant advertises an embedded-style key
///      (<see cref="AiCacheKey.LogicalKey"/>), the existing selection policy ranks it
///      with no changes and the caller loads it like any other asset.
///
///   2. PLAN GENERATION - if an authored prompt exists for this set and the most
///      desirable variant for the current conditions is still missing from BOTH the
///      embedded and cached candidates, emit a single <see cref="AiGenRequest"/> for a
///      background worker to fulfil. If an exact variant already exists (embedded or
///      cached), no request is emitted - that is the "skip work if already done" rule.
/// </summary>
static class AiVariant
{
    /// <summary>The chosen edition to show now, plus an optional variant to generate in the background.</summary>
    public readonly record struct Plan(ArtVariant.Selection? Chosen, AiGenRequest? Generate);

    /// <param name="sourceFallback">
    /// The base image the caller was about to show (e.g. an ink <c># bg:</c> key). Used
    /// as the img2img source when the prompt itself declares no <see cref="AiPromptSet.Base"/>.
    /// </param>
    public static Plan Resolve(
        string setKey,
        Conditions conditions,
        IEnumerable<string> embeddedKeys,
        IEnumerable<string> cachedKeys,
        AiPromptSet? prompt,
        string? sourceFallback = null)
    {
        var all = embeddedKeys.Concat(cachedKeys).Distinct(StringComparer.Ordinal).ToList();

        ArtVariant.Selection? chosen = ArtVariant.Resolve(setKey, conditions, all);
        AiGenRequest? generate = prompt is null
            ? null
            : PlanGeneration(setKey, conditions, all, prompt, sourceFallback);

        return new Plan(chosen, generate);
    }

    // Walk the desired condition tags in priority order (weather first - the feature's
    // headline use) and return the first that (a) has an authored prompt fragment and
    // (b) is not already satisfied by an exact edition. One request per call; the
    // serialized worker fills further gaps on subsequent resolves (revision bumps).
    private static AiGenRequest? PlanGeneration(
        string setKey, Conditions cond, List<string> candidates, AiPromptSet prompt, string? sourceFallback)
    {
        // img2img source: the prompt's own base, else the image the scene was about to show.
        string source = !string.IsNullOrEmpty(prompt.Base) ? prompt.Base : (sourceFallback ?? "");
        bool hasScene = !string.IsNullOrWhiteSpace(prompt.Scene);
        // Explicit mode wins; otherwise infer from whether we have a source to transform.
        string mode = !string.IsNullOrWhiteSpace(prompt.Mode)
            ? prompt.Mode!.Trim().ToLowerInvariant()
            : (source.Length == 0 ? "direct" : "img2img");

        // A scene-based set (no source art) generates its own untagged base first, from the
        // scene prompt alone, so there's a clear-weather image to show before the variants.
        if (hasScene && !HasExactVariant(candidates, setKey, Array.Empty<string>()))
            return new AiGenRequest(setKey, source, Array.Empty<string>(), "", prompt.Scene!.Trim(), prompt.Img2Img, mode);

        foreach (string tag in DesiredTags(cond))
        {
            if (!prompt.Conditions.TryGetValue(tag, out string? fragment) || string.IsNullOrWhiteSpace(fragment))
                continue;

            var target = new[] { tag };
            if (HasExactVariant(candidates, setKey, target))
                continue;   // already generated or hand-authored -> skip

            string slug = AiCacheKey.NormalizeTags(target);
            // With a scene, each edition is scene + weather fragment; otherwise just the fragment.
            string text = hasScene ? Combine(prompt.Scene, fragment) : fragment.Trim();
            return new AiGenRequest(setKey, source, target, slug, text, prompt.Img2Img, mode);
        }
        return null;
    }

    private static string Combine(string? scene, string fragment)
    {
        if (string.IsNullOrWhiteSpace(scene)) return fragment.Trim();
        if (string.IsNullOrWhiteSpace(fragment)) return scene.Trim();
        return $"{scene.Trim()}, {fragment.Trim()}";
    }

    // The condition tags we'd like a bespoke edition for, most valuable first.
    // Weather.Normal has no tag (it is the untagged base), so it is skipped.
    private static IEnumerable<string> DesiredTags(Conditions c)
    {
        if (c.Weather != Weather.Normal) yield return c.Weather.ToString().ToLowerInvariant();
        yield return c.Season.ToString().ToLowerInvariant();
        yield return c.Daylight.ToString().ToLowerInvariant();
    }

    // True if some candidate key is a member of setKey whose tag set equals `target`.
    private static bool HasExactVariant(IEnumerable<string> candidates, string setKey, string[] target)
    {
        string want = AiCacheKey.NormalizeTags(target);
        foreach (string key in candidates)
        {
            string[]? tags = MemberTags(setKey, key);
            if (tags is not null && AiCacheKey.NormalizeTags(tags) == want)
                return true;
        }
        return false;
    }

    // The condition tags a key declares within setKey, or null if it is not a member.
    // "graphics/x/backdrop.rainy.png" within "graphics/x/backdrop" -> ["rainy"];
    // the untagged base "graphics/x/backdrop.jpg" -> [] (zero tags).
    private static string[]? MemberTags(string setKey, string key)
    {
        string prefix = setKey + ".";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        string rest = key.Substring(prefix.Length);   // "rainy.png" | "night.rainy.png" | "jpg"
        string[] segs = rest.Split('.');
        if (segs.Length < 1) return null;
        return segs.Take(segs.Length - 1).ToArray();   // drop the trailing extension
    }
}

/// <summary>
/// A request to generate one missing variant: transform <see cref="SourceKey"/> into
/// the edition described by <see cref="Prompt"/> for the given condition
/// <see cref="Tags"/>. The background worker turns this into pixels, then caches them
/// under a deterministic <see cref="AiCacheKey"/> name and re-indexes so the next
/// resolve finds it instead of re-requesting.
/// </summary>
readonly record struct AiGenRequest(
    string SetKey,
    string SourceKey,
    IReadOnlyList<string> Tags,
    string TagsSlug,
    string Prompt,
    AiImg2ImgParams? Img2Img,
    string Mode);
