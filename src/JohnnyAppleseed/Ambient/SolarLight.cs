namespace JohnnyAppleseed.Ambient;

/// <summary>
/// Pure, offline estimation of how much daylight is falling outdoors right now for a
/// given location and instant: the sun's elevation angle and, from it, an
/// approximate outdoor illuminance ("expected lumens", in lux).
///
/// The elevation uses the NOAA general solar-position equations (equation of time +
/// declination from the fractional year, then the standard hour-angle/zenith
/// formula). The illuminance is a clear-sky approximation - it deliberately ignores
/// clouds and terrain (weather is handled separately by <see cref="Weather"/>); it
/// is meant to drive time-of-day art/lighting, not to be photometrically exact.
///
/// No Raylib, no I/O, no clock reads inside the math (the caller passes the instant),
/// so every branch is unit-tested headlessly in the probe (ArtVariantSelfTest).
/// </summary>
static class SolarLight
{
    /// <summary>
    /// Sun elevation above the horizon (degrees; negative when below) and whether it
    /// is past solar noon at the given location/instant.
    /// </summary>
    /// <param name="latDeg">Latitude, degrees north (negative south).</param>
    /// <param name="lonDeg">Longitude, degrees east (negative west).</param>
    /// <param name="utc">The instant, in UTC.</param>
    public static (double elevationDeg, bool afternoon) SunPosition(double latDeg, double lonDeg, DateTime utc)
    {
        utc = utc.ToUniversalTime();
        double latRad = Deg2Rad(latDeg);

        // Fractional year (radians).
        int daysInYear = DateTime.IsLeapYear(utc.Year) ? 366 : 365;
        double hourFrac = utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0;
        double gamma = 2.0 * Math.PI / daysInYear * (utc.DayOfYear - 1 + (hourFrac - 12.0) / 24.0);

        // Equation of time (minutes) and solar declination (radians).
        double eqTime = 229.18 * (0.000075
            + 0.001868 * Math.Cos(gamma)      - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma)  - 0.040849 * Math.Sin(2 * gamma));
        double decl = 0.006918
            - 0.399912 * Math.Cos(gamma)      + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma)  + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma)  + 0.001480 * Math.Sin(3 * gamma);

        // True solar time (minutes). Input is UTC, so the timezone term is zero and
        // longitude alone carries the offset (4 minutes per degree east).
        double tst = hourFrac * 60.0 + eqTime + 4.0 * lonDeg;

        // Hour angle: 0 at solar noon, negative in the morning, positive afternoon.
        double haDeg = tst / 4.0 - 180.0;
        double haRad = Deg2Rad(haDeg);

        double cosZenith = Math.Sin(latRad) * Math.Sin(decl)
                         + Math.Cos(latRad) * Math.Cos(decl) * Math.Cos(haRad);
        cosZenith = Math.Clamp(cosZenith, -1.0, 1.0);
        double elevationDeg = 90.0 - Rad2Deg(Math.Acos(cosZenith));

        return (elevationDeg, haDeg > 0);
    }

    /// <summary>
    /// Approximate clear-sky outdoor illuminance (lux) for a sun elevation. Blends a
    /// daylight curve (~400 lux at the horizon up to ~100k lux overhead) with an
    /// exponential twilight tail down to astronomical night (~-18 deg).
    /// </summary>
    public static double IlluminanceLux(double elevationDeg)
    {
        const double nightLux = 0.001;    // starlight-ish floor
        const double horizonLux = 400.0;  // rough illuminance with the sun on the horizon

        if (elevationDeg <= -18.0) return nightLux;
        if (elevationDeg < 0.0)
        {
            // Twilight: exponential interpolation from horizonLux at 0 deg down to
            // nightLux at -18 deg.
            double t = (elevationDeg + 18.0) / 18.0;   // 0 at -18 deg, 1 at 0 deg
            return nightLux * Math.Pow(horizonLux / nightLux, t);
        }

        double s = Math.Sin(Deg2Rad(elevationDeg));
        return Math.Max(horizonLux, 100000.0 * Math.Pow(s, 1.15));
    }

    /// <summary>
    /// Expected outdoor lumens (illuminance, lux) at the location/instant - the
    /// single number gameplay reads. Combines <see cref="SunPosition"/> and
    /// <see cref="IlluminanceLux"/>.
    /// </summary>
    public static double EstimateLumens(double latDeg, double lonDeg, DateTime utc)
    {
        (double elevation, _) = SunPosition(latDeg, lonDeg, utc);
        return IlluminanceLux(elevation);
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
