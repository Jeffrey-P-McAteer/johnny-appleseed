using Raylib_cs;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// The default, dependency-free image generator: a deterministic CPU "img2img" that
/// restyles the base asset toward a weather / season / daylight mood using Raylib's
/// image color operations (brightness, contrast, multiplicative tint). It needs no
/// model download and no GPU, so the whole generate -> cache -> reuse pipeline is
/// fully operational on every platform out of the box.
///
/// The neural SD-1.5 LCM ONNX generator is a drop-in replacement behind the shared
/// <see cref="IImageGenerator"/> seam - it produces higher-quality variants at the
/// cost of a model download and heavier CPU, and (because <see cref="ModelId"/> feeds
/// the cache key) its outputs cache separately from these.
///
/// CPU image ops only - decode from memory, adjust, encode a PNG to disk - so no GL
/// context is required. It runs on the AI background worker and headlessly in the
/// probe (<c>probe.py ai-gen</c>).
/// </summary>
sealed class ProceduralImageGenerator : IImageGenerator
{
    public string ModelId => "procedural";
    public string ModelRevision => "1";

    public bool Generate(AiGenRequest req, byte[] sourceBytes, string sourceExt, string outputPath)
    {
        Image img = Raylib.LoadImageFromMemory(sourceExt, sourceBytes);
        if (img.Width == 0 || img.Height == 0)
            return false;

        try
        {
            foreach (string tag in req.Tags)
                ApplyTag(ref img, tag);
            return Raylib.ExportImage(img, outputPath);
        }
        finally
        {
            Raylib.UnloadImage(img);
        }
    }

    // Each recognized condition tag nudges brightness/contrast and multiplies the image
    // by a mood tint. Values are hand-tuned to read as the weather/time at a glance;
    // unknown tags are ignored (leaving the base look).
    private static void ApplyTag(ref Image img, string tag)
    {
        switch (tag)
        {
            // weather
            case "rainy": Adjust(ref img, -35, -8f, new Color(150, 165, 200, 255)); break;
            case "snowy": Adjust(ref img, +25, -12f, new Color(205, 215, 235, 255)); break;
            case "sunny": Adjust(ref img, +18, +10f, new Color(255, 236, 190, 255)); break;
            // time of day
            case "morning":   Adjust(ref img, +8,  0f, new Color(255, 224, 196, 255)); break;
            case "day":       break;   // neutral reference
            case "afternoon": Adjust(ref img, +6,  +2f, new Color(255, 240, 210, 255)); break;
            case "evening":   Adjust(ref img, -20, +4f, new Color(230, 150, 110, 255)); break;
            case "night":     Adjust(ref img, -85, +5f, new Color(90, 110, 170, 255)); break;
            // season
            case "spring": Adjust(ref img, +6,  0f, new Color(220, 245, 210, 255)); break;
            case "summer": Adjust(ref img, +12, +6f, new Color(230, 245, 180, 255)); break;
            case "fall":   Adjust(ref img, -4,  +4f, new Color(240, 190, 130, 255)); break;
            case "winter": Adjust(ref img, +8,  -6f, new Color(200, 220, 245, 255)); break;
        }
    }

    private static void Adjust(ref Image img, int brightness, float contrast, Color tint)
    {
        if (brightness != 0) Raylib.ImageColorBrightness(ref img, brightness);
        if (contrast != 0f)  Raylib.ImageColorContrast(ref img, contrast);
        Raylib.ImageColorTint(ref img, tint);
    }
}
