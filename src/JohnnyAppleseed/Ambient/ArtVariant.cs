namespace JohnnyAppleseed.Ambient;

/// <summary>
/// Resolves a logical "art set" key to the single best-fitting concrete asset key
/// for the current <see cref="Conditions"/>, plus a brightness multiplier to apply
/// when only a near (not exact) time-of-day edition is available.
///
/// An art set is every embedded file that shares a directory + stem and differs
/// only by dot-separated condition tags and extension, e.g. the set
/// <c>graphics/main-menu/backdrop</c> is satisfied by any of:
/// <c>backdrop.jpg</c> (untagged default), <c>backdrop.rainy.png</c>,
/// <c>backdrop.fall.png</c>, <c>backdrop.night.png</c>,
/// <c>backdrop.winter.snowy.gif</c>, ...
///
/// Selection rule (graceful degradation):
///   * Weather / season first: a candidate is WEATHER-SEASON-ELIGIBLE only if every
///     weather and season tag it declares matches the current condition, and all its
///     tags are recognized (see <see cref="ConditionVocab"/>). If none qualify, fall
///     back to the least-specific member so art still shows (brightness 1.0).
///   * Among the weather/season-eligible, prefer an edition whose time-of-day matches
///     (or that declares no time of day - it is time-agnostic). Most specific wins
///     (most matching tags), then weather-specific over the rest, then key order.
///     Brightness 1.0.
///   * If EVERY weather/season-eligible edition is tagged for a DIFFERENT time of day
///     (i.e. there is no artwork for the current lumen level), pick the one whose
///     representative lumens is nearest the real expected lumens and nudge its
///     brightness +/-15% toward the real value (darker if it is darker outside than
///     that edition depicts, brighter if lighter). This is the "no exact edition ->
///     nearest + 15%" rule.
///   * Returns null only when the set has no files at all.
///
/// Pure (no Raylib, no I/O) so the whole policy is unit-tested in the probe.
/// </summary>
static class ArtVariant
{
    /// <summary>A chosen edition: its embedded key and a brightness multiplier (1.0 = as authored).</summary>
    public readonly record struct Selection(string Key, float Brightness);

    /// <param name="setKey">Logical set key, e.g. "graphics/main-menu/backdrop".</param>
    /// <param name="conditions">Current ambient conditions to match against.</param>
    /// <param name="availableKeys">
    /// Candidate embedded keys (typically <c>Assets.Keys(setKey)</c>); keys not
    /// belonging to this set are ignored.
    /// </param>
    /// <returns>The chosen edition, or null if the set is empty.</returns>
    public static Selection? Resolve(string setKey, Conditions conditions, IEnumerable<string> availableKeys)
    {
        var members = new List<Candidate>();
        Candidate? overallFallback = null;   // least-specific member, last resort

        foreach (string key in availableKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!TryParse(setKey, key, out Candidate cand))
                continue;
            members.Add(cand);
            if (overallFallback is null || cand.TagCount < overallFallback.Value.TagCount)
                overallFallback = cand;
        }

        if (members.Count == 0)
            return null;

        // Weather + season must match (time of day handled separately below).
        var eligible = members.Where(c => WeatherSeasonMatches(c, conditions)).ToList();
        if (eligible.Count == 0)
            return new Selection(overallFallback!.Value.Key, 1f);

        // Editions whose time of day fits: no daylight tag (time-agnostic) or a
        // matching one. Prefer the most specific; brightness stays as authored.
        var daylightFits = eligible.Where(c => !c.DeclaresDaylight || c.Daylight == conditions.Daylight).ToList();
        if (daylightFits.Count > 0)
        {
            Candidate best = daylightFits[0];
            foreach (Candidate c in daylightFits)
                if (Prefer(c, best)) best = c;
            return new Selection(best.Key, 1f);
        }

        // No edition for this time of day: pick the nearest by representative lumens
        // and nudge brightness +/-15% toward the real expected lumens.
        Candidate nearest = eligible[0];
        foreach (Candidate c in eligible)
            if (PreferNearest(c, nearest, conditions.Lumens)) nearest = c;

        float brightness = BrightnessNudge(conditions.Lumens, ConditionVocab.RepresentativeLumens(nearest.Daylight));
        return new Selection(nearest.Key, brightness);
    }

    // A parsed set member: its key and the weather/season/daylight tags it declares.
    private readonly record struct Candidate(
        string Key, bool HasWeatherTag, int TagCount,
        Weather Weather, Season Season, Daylight Daylight,
        bool DeclaresWeather, bool DeclaresSeason, bool DeclaresDaylight);

    // Parse "graphics/.../backdrop.rainy.night.png" relative to set "graphics/.../backdrop".
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

        bool declaresWeather = false, declaresSeason = false, declaresDaylight = false;
        Weather w = Weather.Normal;
        Season s = Season.Spring;
        Daylight d = Daylight.Day;

        for (int i = 1; i <= tagCount; i++)
        {
            string tag = parts[i];
            if (ConditionVocab.TryWeather(tag, out Weather pw))       { declaresWeather = true;  w = pw; }
            else if (ConditionVocab.TrySeason(tag, out Season ps))    { declaresSeason = true;   s = ps; }
            else if (ConditionVocab.TryDaylight(tag, out Daylight pd)) { declaresDaylight = true; d = pd; }
            else return false;   // unknown tag segment -> not a recognized variant
        }

        cand = new Candidate(key, declaresWeather, tagCount, w, s, d,
                             declaresWeather, declaresSeason, declaresDaylight);
        return true;
    }

    private static bool WeatherSeasonMatches(Candidate c, Conditions cond)
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

    // Prefer the edition whose representative lumens sits closest to the real value;
    // then a weather-specific match; then earlier key.
    private static bool PreferNearest(Candidate a, Candidate b, double lumens)
    {
        double da = Math.Abs(ConditionVocab.RepresentativeLumens(a.Daylight) - lumens);
        double db = Math.Abs(ConditionVocab.RepresentativeLumens(b.Daylight) - lumens);
        if (da != db) return da < db;
        if (a.HasWeatherTag != b.HasWeatherTag) return a.HasWeatherTag;
        return string.CompareOrdinal(a.Key, b.Key) < 0;
    }

    // A single 15% step darker/brighter toward the real expected lumens (or none if
    // the chosen edition already sits at that level).
    private static float BrightnessNudge(double actualLumens, double editionLumens)
    {
        if (actualLumens < editionLumens) return 0.85f;   // darker outside than the art depicts
        if (actualLumens > editionLumens) return 1.15f;   // brighter outside
        return 1f;
    }
}
