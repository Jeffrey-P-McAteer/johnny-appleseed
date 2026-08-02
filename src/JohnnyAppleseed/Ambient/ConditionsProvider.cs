using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace JohnnyAppleseed.Ambient;

/// <summary>
/// Supplies the current ambient <see cref="Conditions"/> (weather + season) that
/// drive art-variant selection.
///
/// Season is always available instantly and offline, computed from the local date
/// (hemisphere inferred from the last known latitude). Weather is fetched
/// best-effort over the network: approximate location via IP geolocation, then the
/// US National Weather Service (api.weather.gov) inside the US or Open-Meteo
/// elsewhere. The result is cached in app-data for <see cref="CacheTtlMinutes"/>
/// minutes, so subsequent launches (and offline sessions) reuse the last reading.
///
/// The fetch is fire-and-forget and never blocks or throws into the game loop:
/// callers read <see cref="Current"/> immediately (seeded from cache or defaulting
/// to <see cref="Weather.Normal"/>) and, if they want to react to a late-arriving
/// reading, watch <see cref="Revision"/> for a bump and re-read.
///
/// Privacy: fetching discloses an approximate location to ip-api.com and the weather
/// service. It can be turned off by setting <c>"enabled": false</c> in
/// <c>conditions-cache.json</c> under the app-data folder, which pins weather to
/// <see cref="Weather.Normal"/> (season still works).
/// </summary>
static class ConditionsProvider
{
    private const int CacheTtlMinutes = 30;

    private static readonly object _lock = new();
    private static readonly string CachePath =
        System.IO.Path.Combine(AppData.Path, "conditions-cache.json");
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(CacheTtlMinutes);

    private static readonly HttpClient Http = CreateHttpClient();

    // Guarded by _lock.
    private static Weather   _weather          = Weather.Normal;
    private static double    _lat              = 0;
    private static double    _lon              = 0;
    private static bool      _southern         = false;
    private static DateTime  _fetchedUtc       = DateTime.MinValue;
    private static bool      _enabled          = true;
    private static Weather?  _override         = null;   // manual weather override; null = automatic
    private static Season?   _seasonOverride   = null;   // manual season override; null = automatic
    private static Daylight? _daylightOverride = null;   // manual time-of-day override; null = automatic
    private static int       _revision         = 0;
    private static bool      _seeded           = false;

    // Last effective (weather, season, daylight) tuple seen by Tick, so a natural
    // time-of-day transition can bump the revision without spamming it every frame.
    private static (Weather, Season, Daylight)? _lastEffective = null;

    private static int _refreshing = 0;   // Interlocked 0/1 guard against overlapping fetches

    /// <summary>Best-known conditions right now (never blocks; safe every frame).</summary>
    public static Conditions Current
    {
        get
        {
            EnsureSeeded();
            lock (_lock)
            {
                Weather w = _override ?? (_enabled ? _weather : Weather.Normal);
                Season  s = _seasonOverride ?? ConditionMap.SeasonFromDate(DateTime.Now, _southern);

                // Expected outdoor lumens for the last known location, right now.
                // Offline / pre-geolocation this uses lat/lon 0,0 and self-corrects
                // once a reading lands. Season override does not affect the sun.
                (double elevation, bool afternoon) = SolarLight.SunPosition(_lat, _lon, DateTime.UtcNow);
                double lumens = SolarLight.IlluminanceLux(elevation);
                Daylight d = _daylightOverride ?? ConditionMap.DaylightFromLumens(lumens, afternoon);

                return new Conditions(w, s, d, lumens);
            }
        }
    }

    /// <summary>Expected outdoor lumens (illuminance, lux) at the last known location, right now.</summary>
    public static double Lumens => Current.Lumens;

    /// <summary>
    /// Poll once per frame: if the effective (weather, season, daylight) has changed
    /// since last time - typically a natural time-of-day transition - bump the
    /// revision so scenes re-pick their editions. Non-blocking; cheap trig only.
    /// </summary>
    public static void Tick()
    {
        Conditions now = Current;   // seeds + computes the effective tuple
        var eff = (now.Weather, now.Season, now.Daylight);
        lock (_lock)
        {
            if (_lastEffective is { } prev && prev.Equals(eff)) return;
            bool first = _lastEffective is null;
            _lastEffective = eff;
            if (!first) _revision++;   // don't bump on the very first observation
        }
    }

    /// <summary>Bumps whenever the effective conditions change; watch it to swap art.</summary>
    public static int Revision
    {
        get { lock (_lock) return _revision; }
    }

    /// <summary>
    /// Whether live weather fetching is on. Turning it off pins weather to
    /// <see cref="Weather.Normal"/> (unless a manual <see cref="WeatherOverride"/> is
    /// set); turning it on kicks off a refresh. Persisted to the cache file.
    /// </summary>
    public static bool Enabled
    {
        get { EnsureSeeded(); lock (_lock) return _enabled; }
        set
        {
            EnsureSeeded();
            lock (_lock)
            {
                if (_enabled == value) return;
                _enabled = value;
                _revision++;
            }
            SaveCache();
            if (value) BeginRefresh();
        }
    }

    /// <summary>
    /// Manual weather override, or null for automatic. When set, it wins over any
    /// fetched/normal weather regardless of <see cref="Enabled"/>. Persisted.
    /// </summary>
    public static Weather? WeatherOverride
    {
        get { EnsureSeeded(); lock (_lock) return _override; }
        set
        {
            EnsureSeeded();
            lock (_lock)
            {
                if (_override == value) return;
                _override = value;
                _revision++;
            }
            SaveCache();
        }
    }

    /// <summary>
    /// Manual season override, or null for automatic (computed from the local date
    /// and hemisphere). Persisted.
    /// </summary>
    public static Season? SeasonOverride
    {
        get { EnsureSeeded(); lock (_lock) return _seasonOverride; }
        set
        {
            EnsureSeeded();
            lock (_lock)
            {
                if (_seasonOverride == value) return;
                _seasonOverride = value;
                _revision++;
            }
            SaveCache();
        }
    }

    /// <summary>
    /// Manual time-of-day (daylight) override, or null for automatic (derived from
    /// the expected lumens at the last known location). Persisted.
    /// </summary>
    public static Daylight? DaylightOverride
    {
        get { EnsureSeeded(); lock (_lock) return _daylightOverride; }
        set
        {
            EnsureSeeded();
            lock (_lock)
            {
                if (_daylightOverride == value) return;
                _daylightOverride = value;
                _revision++;
            }
            SaveCache();
        }
    }

    /// <summary>
    /// Kick off a best-effort weather refresh if enabled and the cache is stale.
    /// Idempotent and non-blocking; results land in <see cref="Current"/> later.
    /// </summary>
    public static void BeginRefresh()
    {
        EnsureSeeded();
        lock (_lock)
        {
            if (!_enabled) return;
            if (DateTime.UtcNow - _fetchedUtc < CacheTtl) return;   // still fresh
        }
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { Log($"refresh failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    // -- seeding from the on-disk cache ----------------------------------------

    private static void EnsureSeeded()
    {
        lock (_lock)
        {
            if (_seeded) return;
            _seeded = true;
            TryLoadCache();
        }
    }

    private static void TryLoadCache()
    {
        if (!File.Exists(CachePath)) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(CachePath));
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("enabled", out JsonElement en) && en.ValueKind == JsonValueKind.False)
                _enabled = false;
            if (root.TryGetProperty("weather", out JsonElement wv) &&
                Enum.TryParse(wv.GetString(), ignoreCase: true, out Weather w))
                _weather = w;
            // "auto" (or absent/invalid) leaves the overrides null.
            if (root.TryGetProperty("override", out JsonElement ov) &&
                Enum.TryParse(ov.GetString(), ignoreCase: true, out Weather ow))
                _override = ow;
            if (root.TryGetProperty("seasonOverride", out JsonElement so) &&
                Enum.TryParse(so.GetString(), ignoreCase: true, out Season sov))
                _seasonOverride = sov;
            if (root.TryGetProperty("daylightOverride", out JsonElement do_) &&
                Enum.TryParse(do_.GetString(), ignoreCase: true, out Daylight dov))
                _daylightOverride = dov;
            if (root.TryGetProperty("latitude", out JsonElement lv) && lv.TryGetDouble(out double lat))
            {
                _lat = lat;
                _southern = lat < 0;
            }
            if (root.TryGetProperty("longitude", out JsonElement lo) && lo.TryGetDouble(out double lon))
                _lon = lon;
            if (root.TryGetProperty("fetchedUtc", out JsonElement fv) &&
                DateTime.TryParse(fv.GetString(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind, out DateTime dt))
                _fetchedUtc = dt.ToUniversalTime();
        }
        catch (Exception ex)
        {
            Log($"could not read cache: {ex.Message}");
        }
    }

    // Persist the whole current state (weather + settings) from the fields.
    private static void SaveCache()
    {
        Weather weather; double lat, lon; DateTime fetched; bool enabled;
        Weather? ovr; Season? sovr; Daylight? dovr;
        lock (_lock)
        {
            weather = _weather; lat = _lat; lon = _lon;
            fetched = _fetchedUtc; enabled = _enabled;
            ovr = _override; sovr = _seasonOverride; dovr = _daylightOverride;
        }
        try
        {
            AppData.Initialize();
            using FileStream stream = File.Create(CachePath);
            using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteString("weather", weather.ToString());
            w.WriteString("override", ovr?.ToString() ?? "auto");
            w.WriteString("seasonOverride", sovr?.ToString() ?? "auto");
            w.WriteString("daylightOverride", dovr?.ToString() ?? "auto");
            w.WriteNumber("latitude", lat);
            w.WriteNumber("longitude", lon);
            w.WriteString("fetchedUtc", fetched == DateTime.MinValue
                ? "" : fetched.ToString("o", CultureInfo.InvariantCulture));
            w.WriteBoolean("enabled", enabled);
            w.WriteEndObject();
        }
        catch (Exception ex)
        {
            Log($"could not write cache: {ex.Message}");
        }
    }

    // -- the fetch pipeline ----------------------------------------------------

    private static async Task RefreshAsync()
    {
        (bool ok, double lat, double lon, string country) = await GeolocateAsync();
        if (!ok)
        {
            Log("geolocation unavailable; weather stays normal");
            return;
        }

        Weather weather = string.Equals(country, "US", StringComparison.OrdinalIgnoreCase)
            ? await FetchNwsAsync(lat, lon)
            : await FetchOpenMeteoAsync(lat, lon);

        lock (_lock)
        {
            _weather    = weather;
            _lat        = lat;
            _lon        = lon;
            _southern   = lat < 0;
            _fetchedUtc = DateTime.UtcNow;
            _revision++;
        }
        SaveCache();
        Log($"weather -> {weather}  (lat {lat.ToString("F2", CultureInfo.InvariantCulture)}, {country})");
    }

    private static async Task<(bool ok, double lat, double lon, string country)> GeolocateAsync()
    {
        using JsonDocument? doc = await GetJsonAsync("http://ip-api.com/json/?fields=status,countryCode,lat,lon");
        if (doc is null) return (false, 0, 0, "");

        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("status", out JsonElement st) || st.GetString() != "success")
            return (false, 0, 0, "");
        if (!root.TryGetProperty("lat", out JsonElement la) || !la.TryGetDouble(out double lat) ||
            !root.TryGetProperty("lon", out JsonElement lo) || !lo.TryGetDouble(out double lon))
            return (false, 0, 0, "");

        string country = root.TryGetProperty("countryCode", out JsonElement cc) ? (cc.GetString() ?? "") : "";
        return (true, lat, lon, country);
    }

    // US National Weather Service: /points -> forecast URL -> first period's text.
    private static async Task<Weather> FetchNwsAsync(double lat, double lon)
    {
        string coords = $"{lat.ToString("F4", CultureInfo.InvariantCulture)},{lon.ToString("F4", CultureInfo.InvariantCulture)}";
        using JsonDocument? points = await GetJsonAsync($"https://api.weather.gov/points/{coords}");
        string? forecastUrl = points?.RootElement.TryGetProperty("properties", out JsonElement props) == true &&
                              props.TryGetProperty("forecast", out JsonElement fc)
            ? fc.GetString()
            : null;
        if (string.IsNullOrEmpty(forecastUrl)) return Weather.Normal;

        using JsonDocument? forecast = await GetJsonAsync(forecastUrl);
        if (forecast is not null &&
            forecast.RootElement.TryGetProperty("properties", out JsonElement fp) &&
            fp.TryGetProperty("periods", out JsonElement periods) &&
            periods.ValueKind == JsonValueKind.Array && periods.GetArrayLength() > 0 &&
            periods[0].TryGetProperty("shortForecast", out JsonElement sf))
        {
            return ConditionMap.WeatherFromText(sf.GetString());
        }
        return Weather.Normal;
    }

    // Open-Meteo (global, no key): current.weather_code -> WMO mapping.
    private static async Task<Weather> FetchOpenMeteoAsync(double lat, double lon)
    {
        string url = "https://api.open-meteo.com/v1/forecast" +
                     $"?latitude={lat.ToString("F4", CultureInfo.InvariantCulture)}" +
                     $"&longitude={lon.ToString("F4", CultureInfo.InvariantCulture)}" +
                     "&current=weather_code";
        using JsonDocument? doc = await GetJsonAsync(url);
        if (doc is not null &&
            doc.RootElement.TryGetProperty("current", out JsonElement cur) &&
            cur.TryGetProperty("weather_code", out JsonElement wc) && wc.TryGetInt32(out int code))
        {
            return ConditionMap.WeatherFromWmoCode(code);
        }
        return Weather.Normal;
    }

    // -- infrastructure --------------------------------------------------------

    private static async Task<JsonDocument?> GetJsonAsync(string url)
    {
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"GET {url} -> {(int)resp.StatusCode}");
                return null;
            }
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            return JsonDocument.Parse(bytes);
        }
        catch (Exception ex)
        {
            Log($"GET {url} failed: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // NWS requires a descriptive User-Agent identifying the application.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"JohnnyAppleseed/{BuildInfo.Version} (game)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    private static void Log(string msg) => Console.Error.WriteLine($"[conditions] {msg}");
}
