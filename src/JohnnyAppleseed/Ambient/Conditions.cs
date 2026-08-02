namespace JohnnyAppleseed.Ambient;

/// <summary>Current sky/precipitation. <see cref="Normal"/> is the untagged default.</summary>
enum Weather { Normal, Sunny, Rainy, Snowy }

/// <summary>Meteorological season (hemisphere-aware).</summary>
enum Season { Spring, Summer, Fall, Winter }

/// <summary>
/// Named phase of the outdoor-light day, derived from the expected outdoor
/// illuminance (see <see cref="SolarLight"/>) plus whether the sun is before or
/// after solar noon. Drives day/night lights and artwork. Listed brightest-to-
/// darkest is not the intent; use <see cref="ConditionVocab.RepresentativeLumens"/>
/// for a light-level ordering.
/// </summary>
enum Daylight { Morning, Day, Afternoon, Evening, Night }

/// <summary>
/// The ambient conditions the game selects artwork against: the current
/// <see cref="Weather"/>, <see cref="Season"/> and <see cref="Daylight"/> phase,
/// plus the raw expected outdoor <see cref="Lumens"/> (illuminance in lux) used to
/// nudge brightness when no exact time-of-day edition exists. Immutable snapshot
/// produced by <see cref="ConditionsProvider"/> and consumed by <see cref="ArtVariant"/>.
///
/// <see cref="Daylight"/> / <see cref="Lumens"/> default to a plain daytime value so
/// callers that only care about weather+season can still write
/// <c>new Conditions(w, s)</c>.
/// </summary>
readonly record struct Conditions(
    Weather Weather, Season Season, Daylight Daylight = Daylight.Day, double Lumens = 0);

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

    // daylight tag <-> enum. Every phase has a tag (there is no "untagged" default
    // time of day - an untagged file is time-agnostic and matches any phase).
    private static readonly Dictionary<string, Daylight> DaylightByTag =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["morning"]   = Daylight.Morning,
            ["day"]       = Daylight.Day,
            ["afternoon"] = Daylight.Afternoon,
            ["evening"]   = Daylight.Evening,
            ["night"]     = Daylight.Night,
        };

    /// <summary>All recognized weather tags (for the reporter / docs).</summary>
    public static IEnumerable<string> WeatherTags => WeatherByTag.Keys;

    /// <summary>All recognized season tags (for the reporter / docs).</summary>
    public static IEnumerable<string> SeasonTags => SeasonByTag.Keys;

    /// <summary>All recognized time-of-day tags (for the reporter / docs).</summary>
    public static IEnumerable<string> DaylightTags => DaylightByTag.Keys;

    public static bool TryWeather(string tag, out Weather w) => WeatherByTag.TryGetValue(tag, out w);
    public static bool TrySeason(string tag, out Season s)   => SeasonByTag.TryGetValue(tag, out s);
    public static bool TryDaylight(string tag, out Daylight d) => DaylightByTag.TryGetValue(tag, out d);

    /// <summary>True if <paramref name="tag"/> is any recognized weather/season/daylight tag.</summary>
    public static bool IsKnown(string tag) =>
        WeatherByTag.ContainsKey(tag) || SeasonByTag.ContainsKey(tag) || DaylightByTag.ContainsKey(tag);

    /// <summary>
    /// A representative clear-sky outdoor illuminance (lux) for each time-of-day
    /// phase. Used only when no artwork exists for the current phase: the selector
    /// picks the edition whose representative level is nearest the real expected
    /// lumens and nudges its brightness toward the real value (see <see cref="ArtVariant"/>).
    /// Morning and afternoon share a level (comparably bright, differ only by
    /// before/after noon).
    /// </summary>
    public static double RepresentativeLumens(Daylight d) => d switch
    {
        Daylight.Night     => 5,
        Daylight.Evening   => 800,
        Daylight.Morning   => 20000,
        Daylight.Afternoon => 20000,
        Daylight.Day       => 60000,
        _                  => 20000,
    };
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

    // Illuminance thresholds (lux) that separate the time-of-day phases. Chosen to
    // line up with SolarLight.IlluminanceLux: ~50 lux is deep twilight (sun a few
    // degrees down), ~2000 lux is a low sun / heavy dusk, ~40000 lux is a high sun.
    private const double NightMaxLux = 50;
    private const double DuskMaxLux  = 2000;
    private const double DayMinLux   = 40000;

    /// <summary>
    /// Classify the expected outdoor <paramref name="lumens"/> (illuminance in lux)
    /// into a <see cref="Daylight"/> phase. <paramref name="afternoon"/> (true past
    /// solar noon) splits the otherwise-ambiguous mid-brightness band into
    /// morning vs. afternoon/evening. Very dark -> <see cref="Daylight.Night"/>;
    /// very bright -> <see cref="Daylight.Day"/>.
    /// </summary>
    public static Daylight DaylightFromLumens(double lumens, bool afternoon)
    {
        if (lumens < NightMaxLux) return Daylight.Night;
        if (lumens >= DayMinLux)  return Daylight.Day;
        if (!afternoon)           return Daylight.Morning;
        return lumens < DuskMaxLux ? Daylight.Evening : Daylight.Afternoon;
    }

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (string n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }
}
