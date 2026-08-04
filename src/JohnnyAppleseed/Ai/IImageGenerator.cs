namespace JohnnyAppleseed.Ai;

/// <summary>
/// Turns a base asset into a condition-specific edition (img2img). Runs only on the
/// AI background worker, never the game loop. Implementations MUST be deterministic -
/// identical inputs produce identical output bytes - so the content-addressed cache
/// (<see cref="AiCacheKey"/>) stays valid and work is never repeated.
///
/// The seam that lets the default dependency-free <see cref="ProceduralImageGenerator"/>
/// be swapped for a neural SD-1.5 LCM ONNX generator without touching the cache,
/// worker, or scene code. <see cref="ModelId"/>/<see cref="ModelRevision"/> feed the
/// cache key, so switching engines naturally produces distinct cache entries.
/// </summary>
interface IImageGenerator
{
    string ModelId { get; }
    string ModelRevision { get; }

    /// <summary>
    /// True if the engine can synthesize from a prompt alone (txt2img / "direct" mode,
    /// no source image). The procedural stylizer cannot; the neural engine can. Used to
    /// decide whether a file-less AI background set can be generated.
    /// </summary>
    bool SupportsTextToImage { get; }

    /// <summary>
    /// Write a PNG for <paramref name="req"/> to <paramref name="outputPath"/>, derived
    /// from the source image <paramref name="sourceBytes"/> (whose format is
    /// <paramref name="sourceExt"/>, e.g. ".jpg"). Return true on success; return false
    /// (don't throw) for an expected inability to produce output.
    /// </summary>
    bool Generate(AiGenRequest req, byte[] sourceBytes, string sourceExt, string outputPath);
}
