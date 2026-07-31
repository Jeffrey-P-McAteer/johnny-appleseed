namespace JohnnyAppleseed.Ambient;

/// <summary>Current sky/precipitation. <see cref="Normal"/> is the untagged default.</summary>
enum Weather { Normal, Sunny, Rainy, Snowy }

/// <summary>Meteorological season (hemisphere-aware).</summary>
enum Season { Spring, Summer, Fall, Winter }

/// <summary>
/// The ambient conditions the game selects artwork against: the current
/// <see cref="Weather"/> and <see cref="Season"/>. Immutable snapshot produced by
/// <see cref="ConditionsProvider"/> and consumed by <see cref="ArtVariant"/>.
/// </summary>
readonly record struct Conditions(Weather Weather, Season Season);

/// <summary>
/// The controlled vocabulary of art-variant filename tags, shared by the runtime
/// selector (<see cref="ArtVariant"/>) and the coverage reporter
/// (scripts/media-variants-report.py). KEEP THE TWO IN SYNC: a tag added here must
/// be added there too, or the report will mislabel it as unknown.
///
/// A file's variant is encoded as dot-separated tags between its stem and
/// extension, e.g. <c>backdrop.winter.snowy.gif</c>. Tags are order-independent.
/// Untagged weather == <see cref="Weather.Normal"/> (the fallback).
/// </summary>
static class ConditionVocab
{
    // weather tag <-> enum. Weather.Normal has no tag (the untagged base file).
    private static readonly Dictionary<string, Weather> WeatherByTag =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sunny"] = Weather.Sunny,
            ["rainy"] = Weather.Rainy,
            ["snowy"] = Weather.Snowy,
        };

    private static readonly Dictionary<string, Season> SeasonByTag =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["spring"] = Season.Spring,
            ["summer"] = Season.Summer,
            ["fall"]   = Season.Fall,
            ["winter"] = Season.Winter,
        };

    /// <summary>All recognized weather tags (for the reporter / docs).</summary>
    public static IEnumerable<string> WeatherTags => WeatherByTag.Keys;

    /// <summary>All recognized season tags (for the reporter / docs).</summary>
    public static IEnumerable<string> SeasonTags => SeasonByTag.Keys;

    public static bool TryWeather(string tag, out Weather w) => WeatherByTag.TryGetValue(tag, out w);
    public static bool TrySeason(string tag, out Season s)   => SeasonByTag.TryGetValue(tag, out s);

    /// <summary>True if <paramref name="tag"/> is any recognized weather/season tag.</summary>
    public static bool IsKnown(string tag) =>
        WeatherByTag.ContainsKey(tag) || SeasonByTag.ContainsKey(tag);
}

/// <summary>
/// Pure mappers from real-world signals (date, forecast text, WMO codes) to the
/// game's <see cref="Season"/> / <see cref="Weather"/> enums. No I/O, so every
/// branch is unit-tested headlessly in the probe (ArtVariantSelfTest).
/// </summary>
static class ConditionMap
{
    /// <summary>
    /// Meteorological season from a local date. Northern hemisphere by default;
    /// pass <paramref name="southernHemisphere"/> true to shift by six months.
    /// (Dec-Feb winter, Mar-May spring, Jun-Aug summer, Sep-Nov fall.)
    /// </summary>
    public static Season SeasonFromDate(DateTime local, bool southernHemisphere = false)
    {
        Season north = local.Month switch
        {
            12 or 1 or 2 => Season.Winter,
            3 or 4 or 5  => Season.Spring,
            6 or 7 or 8  => Season.Summer,
            _            => Season.Fall,       // 9, 10, 11
        };
        if (!southernHemisphere) return north;
        return north switch
        {
            Season.Winter => Season.Summer,
            Season.Spring => Season.Fall,
            Season.Summer => Season.Winter,
            _             => Season.Spring,
        };
    }

    /// <summary>
    /// Classify a free-text forecast/observation description (e.g. NWS
    /// <c>textDescription</c> "Light Rain", "Mostly Cloudy", "Sunny") into a
    /// <see cref="Weather"/>. Order matters: snow/rain keywords win over "cloudy".
    /// Unrecognized or empty text -> <see cref="Weather.Normal"/>.
    /// </summary>
    public static Weather WeatherFromText(string? description)
    {
        string d = (description ?? "").ToLowerInvariant();
        if (d.Length == 0) return Weather.Normal;

        if (Contains(d, "snow", "sleet", "blizzard", "flurr", "ice pellet", "wintry"))
            return Weather.Snowy;
        if (Contains(d, "rain", "drizzle", "shower", "thunder", "storm", "squall"))
            return Weather.Rainy;
        if (Contains(d, "sunny", "clear", "fair"))
            return Weather.Sunny;
        return Weather.Normal;   // cloudy, overcast, fog, haze, ...
    }

    /// <summary>
    /// Map an Open-Meteo WMO weather-interpretation code to a <see cref="Weather"/>.
    /// See https://open-meteo.com/en/docs (codes 0-99).
    /// </summary>
    public static Weather WeatherFromWmoCode(int code) => code switch
    {
        0 or 1                          => Weather.Sunny,   // clear / mainly clear
        71 or 73 or 75 or 77 or 85 or 86 => Weather.Snowy,  // snow (fall + showers)
        >= 51 and <= 67                 => Weather.Rainy,   // drizzle + rain (incl. freezing)
        >= 80 and <= 82                 => Weather.Rainy,   // rain showers
        95 or 96 or 99                  => Weather.Rainy,   // thunderstorm
        _                               => Weather.Normal,  // 2,3 cloud; 45,48 fog; etc.
    };

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (string n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }
}
