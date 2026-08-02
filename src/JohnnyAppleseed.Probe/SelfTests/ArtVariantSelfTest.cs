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
        fails += Eq(ArtVariant.Resolve(Set, new Conditions(Weather.Normal, Season.Spring), Array.Empty<string>())?.Key, null,
            "empty set -> null");

        fails += SolarAndLumens();
        fails += DaylightVariants();

        Console.WriteLine(fails == 0
            ? "\nART-VARIANT SELF-TEST: ALL PASSED"
            : $"\nART-VARIANT SELF-TEST: {fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static string? Resolve(string[] keys, Weather w, Season s) =>
        ArtVariant.Resolve(Set, new Conditions(w, s), keys)?.Key;

    // -- sun position, illuminance, and lumens -> daylight phase ----------------
    private static int SolarAndLumens()
    {
        int fails = 0;

        // Sun position: high at local solar noon, below the horizon at local midnight.
        // Use the equinox at lon 0 so UTC noon ~ solar noon.
        (double noonElev, bool noonPm) = SolarLight.SunPosition(0, 0, new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc));
        fails += True(noonElev > 45, $"equator equinox noon sun high (elev {noonElev:F1} > 45)");
        (double midElev, _) = SolarLight.SunPosition(0, 0, new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        fails += True(midElev < -45, $"equator equinox midnight sun below horizon (elev {midElev:F1} < -45)");

        // afternoon flag: before vs after solar noon at lon 0.
        (_, bool amPm) = SolarLight.SunPosition(40, 0, new DateTime(2026, 6, 21, 9, 0, 0, DateTimeKind.Utc));
        (_, bool pmPm) = SolarLight.SunPosition(40, 0, new DateTime(2026, 6, 21, 15, 0, 0, DateTimeKind.Utc));
        fails += True(!amPm, "09:00 UTC at lon 0 -> morning (not afternoon)");
        fails += True(pmPm,  "15:00 UTC at lon 0 -> afternoon");

        // Illuminance monotonic-ish and bracketed.
        fails += True(SolarLight.IlluminanceLux(90) > 90000, "overhead sun ~100k lux");
        fails += True(SolarLight.IlluminanceLux(-20) < 0.01, "deep night ~ starlight floor");
        fails += True(SolarLight.IlluminanceLux(30) > SolarLight.IlluminanceLux(5), "higher sun -> brighter");

        // Lumens -> daylight phase.
        fails += Eq(ConditionMap.DaylightFromLumens(1, afternoon: false),      Daylight.Night,     "1 lx -> night");
        fails += Eq(ConditionMap.DaylightFromLumens(70000, afternoon: true),   Daylight.Day,       "70k lx -> day");
        fails += Eq(ConditionMap.DaylightFromLumens(8000, afternoon: false),   Daylight.Morning,   "8k lx AM -> morning");
        fails += Eq(ConditionMap.DaylightFromLumens(8000, afternoon: true),    Daylight.Afternoon, "8k lx PM -> afternoon");
        fails += Eq(ConditionMap.DaylightFromLumens(500, afternoon: true),     Daylight.Evening,   "500 lx PM -> evening");
        fails += Eq(ConditionMap.DaylightFromLumens(500, afternoon: false),    Daylight.Morning,   "500 lx AM -> morning");

        return fails;
    }

    // -- time-of-day art editions (nearest + brightness nudge) ------------------
    private static int DaylightVariants()
    {
        int fails = 0;
        const string set = "graphics/x/img";
        string baseImg = set + ".png";
        string night   = set + ".night.png";
        string morning = set + ".morning.png";
        string day     = set + ".day.png";

        // Exact phase edition beats the time-agnostic base, brightness untouched.
        var s1 = ArtVariant.Resolve(set, Cond(Daylight.Night, 5), new[] { baseImg, night, morning, day });
        fails += Eq(s1?.Key, night, "night phase -> night edition");
        fails += True(s1?.Brightness == 1f, "exact edition -> brightness 1.0");

        // Time-agnostic base is used (brightness 1.0) when no phase edition matches
        // but a base exists.
        var s2 = ArtVariant.Resolve(set, Cond(Daylight.Evening, 600), new[] { baseImg, night, day });
        fails += Eq(s2?.Key, baseImg, "no evening edition but base exists -> base");
        fails += True(s2?.Brightness == 1f, "base fallback -> brightness 1.0");

        // No base, no matching phase: pick nearest by lumens and brighten (real
        // lumens above the nearest edition's level).
        var s3 = ArtVariant.Resolve(set, Cond(Daylight.Day, 55000), new[] { night, morning });
        fails += Eq(s3?.Key, morning, "no base, 55k lx -> nearest is morning (20k) not night (5)");
        fails += True(s3?.Brightness == 1.15f, "brighter than edition depicts -> +15%");

        // No base, no matching phase: nearest and darken (real lumens below level).
        var s4 = ArtVariant.Resolve(set, Cond(Daylight.Evening, 500), new[] { morning, day });
        fails += Eq(s4?.Key, morning, "no base, 500 lx -> nearest is morning (20k) not day (60k)");
        fails += True(s4?.Brightness == 0.85f, "darker than edition depicts -> -15%");

        return fails;
    }

    private static Conditions Cond(Daylight d, double lumens) =>
        new Conditions(Weather.Normal, Season.Spring, d, lumens);

    private static int True(bool ok, string label)
    {
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}");
        return ok ? 0 : 1;
    }

    private static int Eq<T>(T actual, T expected, string label)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}"
            + (ok ? "" : $"   (got {actual?.ToString() ?? "null"}, want {expected?.ToString() ?? "null"})"));
        return ok ? 0 : 1;
    }
}
