namespace JohnnyAppleseed.Ambient;

/// <summary>
/// Resolves a logical "art set" key to the single best-fitting concrete asset key
/// for the current <see cref="Conditions"/>.
///
/// An art set is every embedded file that shares a directory + stem and differs
/// only by dot-separated condition tags and extension, e.g. the set
/// <c>graphics/main-menu/backdrop</c> is satisfied by any of:
/// <c>backdrop.jpg</c> (untagged default), <c>backdrop.rainy.png</c>,
/// <c>backdrop.fall.png</c>, <c>backdrop.winter.snowy.gif</c>, ...
///
/// Selection rule (graceful degradation):
///   * A candidate is ELIGIBLE only if every tag it declares matches the current
///     condition (a <c>sunny</c> file is ineligible while it is raining), and all
///     its tags are recognized (see <see cref="ConditionVocab"/>).
///   * Among eligible candidates the MOST SPECIFIC wins (most matching tags); the
///     untagged base has zero tags and is the ultimate fallback.
///   * Ties: a weather-tagged match outranks a season-only match, then the
///     lexicographically-first key (fully deterministic).
///   * If nothing is eligible (e.g. a set with only a <c>.sunny</c> file on a rainy
///     day and no base), fall back to the least-specific available file so art
///     still shows. Returns null only when the set has no files at all.
///
/// Pure (no Raylib, no I/O) so the whole policy is unit-tested in the probe.
/// </summary>
static class ArtVariant
{
    /// <param name="setKey">Logical set key, e.g. "graphics/main-menu/backdrop".</param>
    /// <param name="conditions">Current ambient conditions to match against.</param>
    /// <param name="availableKeys">
    /// Candidate embedded keys (typically <c>Assets.Keys(setKey)</c>); keys not
    /// belonging to this set are ignored.
    /// </param>
    /// <returns>The chosen concrete key, or null if the set is empty.</returns>
    public static string? Resolve(string setKey, Conditions conditions, IEnumerable<string> availableKeys)
    {
        Candidate? best = null;
        Candidate? fallback = null;   // least-specific member, used when nothing is eligible

        foreach (string key in availableKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!TryParse(setKey, key, out Candidate cand))
                continue;

            // Track the least-specific member (fewest tags) as a last resort.
            if (fallback is null || cand.TagCount < fallback.Value.TagCount)
                fallback = cand;

            if (!IsEligible(cand, conditions))
                continue;

            if (best is null || Prefer(cand, best.Value))
                best = cand;
        }

        return (best ?? fallback)?.Key;
    }

    // A parsed set member: its key and the weather/season tags it declares.
    private readonly record struct Candidate(string Key, bool HasWeatherTag, int TagCount, Weather Weather, Season Season, bool DeclaresWeather, bool DeclaresSeason);

    // Parse "graphics/.../backdrop.rainy.png" relative to set "graphics/.../backdrop".
    private static bool TryParse(string setKey, string key, out Candidate cand)
    {
        cand = default;
        if (!key.StartsWith(setKey, StringComparison.Ordinal))
            return false;

        string rest = key.Substring(setKey.Length);   // ".rainy.png" or ".jpg"
        // Must be exactly this stem followed by a dot (not a sibling like "backdrop2").
        if (rest.Length == 0 || rest[0] != '.')
            return false;

        // rest = ".<tag>.<tag>...<ext>"  -> segments without the leading empty and
        // the trailing extension are the tags.
        string[] parts = rest.Split('.');            // ["", "rainy", "png"]
        if (parts.Length < 2) return false;          // needs at least ".ext"
        int tagCount = parts.Length - 2;             // drop leading "" and trailing ext

        bool declaresWeather = false, declaresSeason = false;
        Weather w = Weather.Normal;
        Season s = Season.Spring;

        for (int i = 1; i <= tagCount; i++)
        {
            string tag = parts[i];
            if (ConditionVocab.TryWeather(tag, out Weather pw)) { declaresWeather = true; w = pw; }
            else if (ConditionVocab.TrySeason(tag, out Season ps)) { declaresSeason = true; s = ps; }
            else return false;   // unknown tag segment -> not a recognized variant
        }

        cand = new Candidate(key, declaresWeather, tagCount, w, s, declaresWeather, declaresSeason);
        return true;
    }

    private static bool IsEligible(Candidate c, Conditions cond)
    {
        if (c.DeclaresWeather && c.Weather != cond.Weather) return false;
        if (c.DeclaresSeason && c.Season != cond.Season) return false;
        return true;
    }

    // Prefer more matching tags; then a weather-specific match; then earlier key.
    private static bool Prefer(Candidate a, Candidate b)
    {
        if (a.TagCount != b.TagCount) return a.TagCount > b.TagCount;
        if (a.HasWeatherTag != b.HasWeatherTag) return a.HasWeatherTag;
        return string.CompareOrdinal(a.Key, b.Key) < 0;
    }
}
