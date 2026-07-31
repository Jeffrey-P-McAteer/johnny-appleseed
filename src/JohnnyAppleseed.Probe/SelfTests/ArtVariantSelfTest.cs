using JohnnyAppleseed.Ambient;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Headless verification of the weather/season art-variant selection and the pure
/// condition mappers (<see cref="ArtVariant"/>, <see cref="ConditionMap"/>). No
/// Raylib, no network, no files - reaches the game's internal selection policy via
/// InternalsVisibleTo, so the full "pick the most specific eligible edition, fall
/// back gracefully" behaviour is checked deterministically.
///
/// Run via <c>uv run scripts/probe.py selftest art</c>. Exit code 0 = all passed.
/// </summary>
static class ArtVariantSelfTest
{
    private const string Set   = "graphics/main-menu/backdrop";
    private const string Base  = "graphics/main-menu/backdrop.jpg";
    private const string Rainy = "graphics/main-menu/backdrop.rainy.png";
    private const string Sunny = "graphics/main-menu/backdrop.sunny.png";
    private const string Fall  = "graphics/main-menu/backdrop.fall.png";
    private const string WinterSnowy = "graphics/main-menu/backdrop.winter.snowy.gif";
    // A sibling set that shares the prefix but not the stem - must be ignored.
    private const string Sibling = "graphics/main-menu/backdrop2.png";

    public static int Run()
    {
        Console.WriteLine("ART-VARIANT SELF-TEST");
        int fails = 0;

        // -- season from date (meteorological, hemisphere-aware) ---------------
        fails += Eq(ConditionMap.SeasonFromDate(new DateTime(2026, 1, 15)), Season.Winter, "Jan -> winter (N)");
        fails += Eq(ConditionMap.SeasonFromDate(new DateTime(2026, 4, 15)), Season.Spring, "Apr -> spring (N)");
        fails += Eq(ConditionMap.SeasonFromDate(new DateTime(2026, 7, 15)), Season.Summer, "Jul -> summer (N)");
        fails += Eq(ConditionMap.SeasonFromDate(new DateTime(2026, 10, 15)), Season.Fall,  "Oct -> fall (N)");
        fails += Eq(ConditionMap.SeasonFromDate(new DateTime(2026, 1, 15), southernHemisphere: true), Season.Summer, "Jan -> summer (S)");
        fails += Eq(ConditionMap.SeasonFromDate(new DateTime(2026, 7, 15), southernHemisphere: true), Season.Winter, "Jul -> winter (S)");

        // -- forecast text -> weather ------------------------------------------
        fails += Eq(ConditionMap.WeatherFromText("Light Rain"),          Weather.Rainy,  "'Light Rain' -> rainy");
        fails += Eq(ConditionMap.WeatherFromText("Chance Rain Showers"), Weather.Rainy,  "'Chance Rain Showers' -> rainy");
        fails += Eq(ConditionMap.WeatherFromText("Sunny"),               Weather.Sunny,  "'Sunny' -> sunny");
        fails += Eq(ConditionMap.WeatherFromText("Clear"),               Weather.Sunny,  "'Clear' -> sunny");
        fails += Eq(ConditionMap.WeatherFromText("Mostly Cloudy"),       Weather.Normal, "'Mostly Cloudy' -> normal");
        fails += Eq(ConditionMap.WeatherFromText("Snow Showers"),        Weather.Snowy,  "'Snow Showers' -> snowy");
        fails += Eq(ConditionMap.WeatherFromText(""),                    Weather.Normal, "empty -> normal");

        // -- WMO code -> weather (Open-Meteo) ----------------------------------
        fails += Eq(ConditionMap.WeatherFromWmoCode(0),  Weather.Sunny,  "WMO 0 clear -> sunny");
        fails += Eq(ConditionMap.WeatherFromWmoCode(3),  Weather.Normal, "WMO 3 overcast -> normal");
        fails += Eq(ConditionMap.WeatherFromWmoCode(45), Weather.Normal, "WMO 45 fog -> normal");
        fails += Eq(ConditionMap.WeatherFromWmoCode(61), Weather.Rainy,  "WMO 61 rain -> rainy");
        fails += Eq(ConditionMap.WeatherFromWmoCode(71), Weather.Snowy,  "WMO 71 snow -> snowy");
        fails += Eq(ConditionMap.WeatherFromWmoCode(85), Weather.Snowy,  "WMO 85 snow showers -> snowy");
        fails += Eq(ConditionMap.WeatherFromWmoCode(95), Weather.Rainy,  "WMO 95 thunder -> rainy");

        // -- variant resolution ------------------------------------------------
        string[] all = { Base, Rainy, Sunny, Fall, WinterSnowy, Sibling };

        fails += Eq(Resolve(all, Weather.Rainy, Season.Summer), Rainy,
            "rainy day -> rainy edition (weather beats no season)");
        fails += Eq(Resolve(all, Weather.Sunny, Season.Fall), Sunny,
            "sunny+fall, both count 1 -> weather wins the tie");
        fails += Eq(Resolve(all, Weather.Snowy, Season.Winter), WinterSnowy,
            "snowy+winter -> most specific combined edition");
        fails += Eq(Resolve(all, Weather.Normal, Season.Fall), Fall,
            "normal+fall -> season edition beats base");
        fails += Eq(Resolve(all, Weather.Normal, Season.Summer), Base,
            "normal+summer, nothing matches -> untagged base");
        fails += Eq(Resolve(all, Weather.Snowy, Season.Summer), Base,
            "snowy+summer, combined ineligible (season) -> base");

        // -- degenerate cases --------------------------------------------------
        fails += Eq(Resolve(new[] { Sunny }, Weather.Rainy, Season.Summer), Sunny,
            "no base + nothing eligible -> least-specific fallback still shows art");
        fails += Eq(ArtVariant.Resolve(Set, new Conditions(Weather.Normal, Season.Spring), Array.Empty<string>()), null,
            "empty set -> null");

        Console.WriteLine(fails == 0
            ? "\nART-VARIANT SELF-TEST: ALL PASSED"
            : $"\nART-VARIANT SELF-TEST: {fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static string? Resolve(string[] keys, Weather w, Season s) =>
        ArtVariant.Resolve(Set, new Conditions(w, s), keys);

    private static int Eq<T>(T actual, T expected, string label)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}"
            + (ok ? "" : $"   (got {actual?.ToString() ?? "null"}, want {expected?.ToString() ?? "null"})"));
        return ok ? 0 : 1;
    }
}
