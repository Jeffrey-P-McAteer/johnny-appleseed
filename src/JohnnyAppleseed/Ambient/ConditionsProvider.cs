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
    private static Weather  _weather     = Weather.Normal;
    private static bool     _southern    = false;
    private static DateTime _fetchedUtc  = DateTime.MinValue;
    private static bool     _enabled     = true;
    private static int      _revision    = 0;
    private static bool     _seeded      = false;

    private static int _refreshing = 0;   // Interlocked 0/1 guard against overlapping fetches

    /// <summary>Best-known conditions right now (never blocks; safe every frame).</summary>
    public static Conditions Current
    {
        get
        {
            EnsureSeeded();
            lock (_lock)
                return new Conditions(_weather, ConditionMap.SeasonFromDate(DateTime.Now, _southern));
        }
    }

    /// <summary>Bumps whenever the weather reading changes; watch it to swap art.</summary>
    public static int Revision
    {
        get { lock (_lock) return _revision; }
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
            if (root.TryGetProperty("latitude", out JsonElement lv) && lv.TryGetDouble(out double lat))
                _southern = lat < 0;
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

    private static void SaveCache(Weather weather, double lat, double lon)
    {
        try
        {
            AppData.Initialize();
            using FileStream stream = File.Create(CachePath);
            using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteString("weather", weather.ToString());
            w.WriteNumber("latitude", lat);
            w.WriteNumber("longitude", lon);
            w.WriteString("fetchedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.WriteBoolean("enabled", true);
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
            _southern   = lat < 0;
            _fetchedUtc = DateTime.UtcNow;
            _revision++;
        }
        SaveCache(weather, lat, lon);
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
