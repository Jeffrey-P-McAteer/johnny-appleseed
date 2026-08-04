using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// Runtime coordinator for AI-generated art. OFF by default - a plain launch behaves
/// byte-for-byte like the shipped game; enable it with the environment variable
/// <c>JOHNNY_APPLESEED_AI=1</c>. When enabled it:
///
///   * advertises already-cached variants to <see cref="AiVariant"/> (<see cref="CachedKeys"/>),
///   * serves their bytes to <see cref="Assets"/> through the disk-resolver hook, so a
///     cached PNG loads via the identical Texture/Animated path as an embedded asset,
///   * fulfils <see cref="AiGenRequest"/>s on a single background worker (never the game
///     loop), writing deterministically-named PNGs into <c>ai-assets/store/</c> and
///     bumping <see cref="Revision"/> so scenes swap the new art in.
///
/// Mirrors <see cref="Ambient.ConditionsProvider"/>'s threading and best-effort,
/// never-throw-into-the-loop style: one serialized worker, an interlocked guard, and
/// all failures logged to stderr rather than propagated.
/// </summary>
static class AiAssets
{
    private static readonly object _lock = new();
    private static bool _initialized;
    private static bool _enabled;
    private static bool _active;   // resources (prompts + index + disk resolver) loaded

    private static string SettingsPath => System.IO.Path.Combine(AppData.Path, "ai-settings.json");

    private static IImageGenerator _generator = new ProceduralImageGenerator();

    // logicalKey -> absolute store path (only entries whose file exists). Guarded by _lock.
    private static readonly Dictionary<string, string> _pathByKey = new(StringComparer.Ordinal);
    // setKey -> its cached logical keys. Guarded by _lock.
    private static readonly Dictionary<string, List<string>> _keysBySet = new(StringComparer.Ordinal);

    private static Dictionary<string, AiPromptSet> _prompts = new();
    private static AiIndex _index = new();

    private static readonly ConcurrentQueue<AiGenRequest> _queue = new();
    private static readonly HashSet<string> _inflight = new(StringComparer.Ordinal);  // "setKey|tagsSlug"
    private static int _draining;   // Interlocked 0/1
    private static int _revision;

    // Direct (txt2img) requests the current engine can't fulfil (procedural, before the
    // neural model is ready). Tracked so we log the deferral once instead of every frame;
    // cleared when the neural engine comes up so those editions get retried.
    private static readonly HashSet<string> _declinedDirect = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether AI generation is on. Default off; persisted to <c>ai-settings.json</c> so a
    /// user's choice survives restarts (toggle it in Preferences, like Fullscreen / Live
    /// weather). Setting it activates the subsystem on demand and bumps <see cref="Revision"/>
    /// so scenes re-resolve to pick up (or drop) generated art. The environment variable
    /// JOHNNY_APPLESEED_AI forces the initial value regardless of the saved setting.
    /// </summary>
    public static bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set => SetEnabled(value);
    }

    /// <summary>Bumps whenever a new variant is cached; scenes watch it to swap art in.</summary>
    public static int Revision { get { lock (_lock) return _revision; } }

    /// <summary>Swap the engine (tests inject a fake; a neural build installs the ONNX one).</summary>
    public static void UseGenerator(IImageGenerator generator) { lock (_lock) _generator = generator; }

    /// <summary>
    /// One-time setup. Reads the enable flag; if on, prepares the ai-models/ + ai-assets/
    /// tree, loads the authored prompts and the cache index, and installs the disk
    /// resolver so cached PNGs load like embedded assets. Cheap and safe to call early.
    /// </summary>
    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            _enabled = ResolveInitialEnabled();
            if (_enabled) EnsureActiveLocked();
        }
    }

    // env var override, else the persisted setting, else off.
    private static bool ResolveInitialEnabled()
    {
        string? env = Environment.GetEnvironmentVariable("JOHNNY_APPLESEED_AI");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim() is "1" or "true" or "on" or "yes";
        return LoadSettingEnabled();
    }

    private static void SetEnabled(bool value)
    {
        lock (_lock)
        {
            if (!_initialized) { _initialized = true; }   // toggled before Initialize ran
            _enabled = value;
            if (value) EnsureActiveLocked();
            _revision++;   // scenes re-resolve: pick up generated art, or fall back to embedded
        }
        SaveSettingEnabled(value);
    }

    // Load prompts + cache index and install the disk resolver. Idempotent; only the
    // first activation does the work, so toggling off then on again is cheap.
    private static void EnsureActiveLocked()
    {
        if (_active) return;
        _active = true;

        AiPaths.Initialize();
        _prompts = AiPrompts.LoadEmbedded();
        LoadIndexLocked();
        Assets.DiskResolver = TryDiskPath;
        Log($"enabled; {_pathByKey.Count} cached variant(s), {_prompts.Count} prompt set(s)");

        MaybeUseNeural();
    }

    // Bring up the neural engine (always compiled in) entirely on a background thread:
    // download the ~4 GB model if missing, then load it (loading the 3.4 GB UNet is slow -
    // doing it here keeps it OFF the startup/UI thread). The procedural stylizer serves
    // meanwhile; when the engine is ready we swap it in, clear any "declined" markers, and
    // bump the revision so scenes re-request the editions procedural couldn't make.
    private static void MaybeUseNeural()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!AiModels.IsInstalled)
                {
                    Log("downloading image model in the background (~4 GB, one time); procedural stylizer until it lands...");
                    if (!await AiModels.EnsureAsync(m => Log(m))) return;
                }

                Log("loading neural model (SD-1.5 LCM); this can take a while on CPU...");
                var generator = new OnnxImageGenerator(AiModels.ModelDir);   // heavy: builds ONNX sessions
                lock (_lock)
                {
                    _generator = generator;
                    _declinedDirect.Clear();
                    _revision++;
                }
                Log("neural engine ready; new generations will use it");
            }
            catch (Exception ex) { Log($"neural init failed, staying on procedural: {ex.Message}"); }
        });
    }

    private static bool LoadSettingEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
            return doc.RootElement.TryGetProperty("enabled", out JsonElement e)
                && e.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static void SaveSettingEnabled(bool value)
    {
        try
        {
            AppData.Initialize();
            using FileStream fs = File.Create(SettingsPath);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteBoolean("enabled", value);
            w.WriteEndObject();
        }
        catch (Exception ex) { Log($"settings write failed: {ex.Message}"); }
    }

    /// <summary>Cached logical keys for a set (empty when disabled). Fed to <see cref="AiVariant"/>.</summary>
    public static IReadOnlyList<string> CachedKeys(string setKey)
    {
        lock (_lock)
        {
            if (!_enabled) return Array.Empty<string>();
            return _keysBySet.TryGetValue(setKey, out List<string>? list)
                ? list.ToArray() : Array.Empty<string>();
        }
    }

    /// <summary>Authored prompt for a set, or null (also null when disabled - keeps generation inert).</summary>
    public static AiPromptSet? PromptFor(string setKey)
    {
        lock (_lock)
        {
            if (!_enabled) return null;
            return _prompts.GetValueOrDefault(setKey);
        }
    }

    /// <summary>Disk path a cached logical key maps to, or null. Installed as <see cref="Assets.DiskResolver"/>.</summary>
    public static string? TryDiskPath(string logicalKey)
    {
        lock (_lock) return _pathByKey.TryGetValue(logicalKey, out string? p) ? p : null;
    }

    /// <summary>
    /// Queue a variant to generate in the background. No-op when disabled, when it is
    /// already cached, or when an identical request is already queued/running (so the
    /// per-frame re-resolve loop can call this freely - the "skip work" guarantee).
    /// </summary>
    public static void RequestGeneration(AiGenRequest req)
    {
        lock (_lock)
        {
            if (!_enabled) return;

            string logicalKey = AiCacheKey.LogicalKey(req.SetKey, req.TagsSlug, ".png");
            if (_pathByKey.ContainsKey(logicalKey)) return;              // already cached
            if (!_inflight.Add(req.SetKey + "|" + req.TagsSlug)) return; // already queued/running
        }
        _queue.Enqueue(req);
        StartDrain();
    }

    private static void StartDrain()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;   // a worker is already draining
        _ = Task.Run(() =>
        {
            try
            {
                while (_queue.TryDequeue(out AiGenRequest req))
                {
                    try { Fulfil(req); }
                    catch (Exception ex) { Log($"generation failed for {req.SetKey}.{req.TagsSlug}: {ex.Message}"); }
                    finally { lock (_lock) _inflight.Remove(req.SetKey + "|" + req.TagsSlug); }
                }
            }
            finally { Interlocked.Exchange(ref _draining, 0); }
        });
    }

    private static void Fulfil(AiGenRequest req)
    {
        IImageGenerator gen;
        lock (_lock) gen = _generator;

        string tagLabel = req.TagsSlug.Length == 0 ? "(base)" : req.TagsSlug;

        // Direct (txt2img) generation has no source image - only the neural engine can do
        // it. The procedural stylizer declines cleanly (so a file-less AI set just shows
        // the parallax backdrop until a neural build fills it in).
        bool direct = req.Mode == "direct" || string.IsNullOrEmpty(req.SourceKey);
        if (direct && !gen.SupportsTextToImage)
        {
            // The neural engine isn't up yet (downloading or loading). Defer quietly - log
            // once per edition, not every frame; MaybeUseNeural clears this and bumps the
            // revision when the engine is ready, so these get retried automatically.
            bool firstTime;
            lock (_lock) firstTime = _declinedDirect.Add(req.SetKey + "|" + req.TagsSlug);
            if (firstTime)
                Log($"deferring {req.SetKey}.{tagLabel}: text-to-image needs the neural engine (coming up); procedural can't do it");
            return;
        }

        byte[] src;
        string ext, srcSha;
        if (direct)
        {
            src = Array.Empty<byte>();
            ext = ".png";
            srcSha = "txt2img";   // no source bytes; a constant so the cache key stays stable
        }
        else
        {
            if (!Assets.Exists(req.SourceKey)) { Log($"source missing: {req.SourceKey}"); return; }
            src = Assets.Bytes(req.SourceKey);
            ext = System.IO.Path.GetExtension(req.SourceKey);
            srcSha = AiCacheKey.Sha(src);
        }

        string paramsJson = ParamsJson(req.Img2Img, req.Mode);
        string cacheKey   = AiCacheKey.Compute(gen.ModelId, gen.ModelRevision,
                                req.SourceKey, srcSha, req.Tags, req.Prompt, paramsJson);

        string file       = AiCacheKey.StoreFileName(req.SetKey, req.TagsSlug, cacheKey, ".png");
        string logicalKey = AiCacheKey.LogicalKey(req.SetKey, req.TagsSlug, ".png");
        string storePath  = System.IO.Path.Combine(AiPaths.AssetStoreDir, file);

        // Already produced by an earlier run (same inputs -> same name): reuse, skip work.
        if (File.Exists(storePath))
        {
            RegisterCached(req, logicalKey, storePath, cacheKey, srcSha, gen, file);
            Log($"reuse {logicalKey} (already cached)");
            return;
        }

        // Generate into the tmp/ dir, then atomically move into store/ so a crash never
        // leaves a half-written PNG that would look "done". NOTE: the temp name must end
        // in ".png" - Raylib.ExportImage picks the encoder from the file extension, so a
        // ".part" suffix would make it silently refuse to write. Atomicity comes from the
        // separate tmp/ directory + the rename, not from the suffix.
        AiPaths.Initialize();
        string tmp = System.IO.Path.Combine(AiPaths.AssetTmpDir, cacheKey + ".tmp.png");
        var sw = Stopwatch.StartNew();
        bool ok = gen.Generate(req, src, ext, tmp);
        sw.Stop();

        if (!ok || !File.Exists(tmp))
        {
            Log($"generator produced nothing for {logicalKey}");
            TryDelete(tmp);
            return;
        }

        File.Move(tmp, storePath, overwrite: true);
        RegisterCached(req, logicalKey, storePath, cacheKey, srcSha, gen, file);
        Log($"generated {logicalKey} <- {req.SourceKey}  ({gen.ModelId}, {sw.ElapsedMilliseconds} ms)  {file}");
    }

    private static void RegisterCached(AiGenRequest req, string logicalKey, string storePath,
        string cacheKey, string srcSha, IImageGenerator gen, string file)
    {
        lock (_lock)
        {
            _pathByKey[logicalKey] = storePath;
            if (!_keysBySet.TryGetValue(req.SetKey, out List<string>? list))
                _keysBySet[req.SetKey] = list = new List<string>();
            if (!list.Contains(logicalKey, StringComparer.Ordinal))
                list.Add(logicalKey);

            _index.Entries.RemoveAll(e => e.LogicalKey == logicalKey);
            _index.Entries.Add(new AiIndexEntry
            {
                SetKey = req.SetKey, LogicalKey = logicalKey, TagsSlug = req.TagsSlug,
                CacheKey = cacheKey, File = file, SourceKey = req.SourceKey,
                SourceSha = srcSha, Model = $"{gen.ModelId}@{gen.ModelRevision}",
            });
            SaveIndexLocked();
            _revision++;
        }
    }

    // -- index persistence (source-generated JSON; trim/single-file safe) -----------

    private static void LoadIndexLocked()
    {
        _index = new AiIndex();
        _pathByKey.Clear();
        _keysBySet.Clear();
        try
        {
            if (File.Exists(AiPaths.AssetIndex))
            {
                AiIndex? loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(AiPaths.AssetIndex), AiJsonContext.Default.AiIndex);
                if (loaded is not null) _index = loaded;
            }
        }
        catch (Exception ex) { Log($"index read failed: {ex.Message}"); _index = new AiIndex(); }

        foreach (AiIndexEntry e in _index.Entries)
        {
            string path = System.IO.Path.Combine(AiPaths.AssetStoreDir, e.File);
            if (!File.Exists(path)) continue;   // file cleaned/removed -> ignore this entry
            _pathByKey[e.LogicalKey] = path;
            if (!_keysBySet.TryGetValue(e.SetKey, out List<string>? list))
                _keysBySet[e.SetKey] = list = new List<string>();
            if (!list.Contains(e.LogicalKey, StringComparer.Ordinal))
                list.Add(e.LogicalKey);
        }
    }

    private static void SaveIndexLocked()
    {
        try
        {
            AiPaths.Initialize();
            File.WriteAllText(AiPaths.AssetIndex,
                JsonSerializer.Serialize(_index, AiJsonContext.Default.AiIndex));
        }
        catch (Exception ex) { Log($"index write failed: {ex.Message}"); }
    }

    private static string ParamsJson(AiImg2ImgParams? p, string mode)
    {
        string s = p is null ? "" : p.Strength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int steps = p?.Steps ?? 0;
        return $"{{\"mode\":\"{mode}\",\"strength\":\"{s}\",\"steps\":{steps}}}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void Log(string msg) => Console.Error.WriteLine($"[ai] {msg}");
}
