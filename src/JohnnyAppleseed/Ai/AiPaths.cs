namespace JohnnyAppleseed.Ai;

/// <summary>
/// On-disk locations for the AI subsystem, under the cross-platform app-data folder
/// (<see cref="AppData.Path"/>):
///
///   ai-models/   downloaded model weights (SD-1.5 LCM img2img, a small text GGUF)
///   ai-assets/   generated + cached art, plus the lookup index
///     store/     the cached PNGs and their provenance sidecars
///     tmp/       *.part files being written; atomically renamed into store/
///
/// Names are fixed here (the two top-level subfolders the design calls for) and the
/// tree is created lazily via <see cref="Initialize"/>, mirroring <see cref="AppData"/>.
/// </summary>
static class AiPaths
{
    public static string ModelsDir     => System.IO.Path.Combine(AppData.Path, "ai-models");
    public static string ModelsIndex   => System.IO.Path.Combine(ModelsDir, "manifest.json");

    public static string AssetsDir     => System.IO.Path.Combine(AppData.Path, "ai-assets");
    public static string AssetStoreDir => System.IO.Path.Combine(AssetsDir, "store");
    public static string AssetTmpDir   => System.IO.Path.Combine(AssetsDir, "tmp");
    public static string AssetIndex    => System.IO.Path.Combine(AssetsDir, "index.json");

    /// <summary>Create the whole ai-models/ + ai-assets/ tree if missing. Idempotent.</summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(AssetStoreDir);
        Directory.CreateDirectory(AssetTmpDir);
    }
}
