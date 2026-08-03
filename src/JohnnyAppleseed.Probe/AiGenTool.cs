using JohnnyAppleseed;
using JohnnyAppleseed.Ai;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Headless exercise of the real <see cref="ProceduralImageGenerator"/>: decode an
/// embedded base image, restyle it toward one or more condition tags, and write the
/// PNG to the working directory - no game window, no model download. Lets you eyeball
/// generated editions and confirms the CPU img2img path works on this platform.
///
/// Usage: <c>uv run scripts/probe.py ai-gen [setKey] [tags] [baseKey]</c>
///   e.g. <c>ai-gen graphics/main-menu/backdrop snowy</c>
///        <c>ai-gen graphics/main-menu/backdrop night.rainy</c>   (dot-joined tags)
/// </summary>
static class AiGenTool
{
    public static int Run(string[] args)
    {
        string setKey  = args.Length > 1 ? args[1] : "graphics/main-menu/backdrop";
        string tagArg  = args.Length > 2 ? args[2] : "summer";
        string baseKey = args.Length > 3 ? args[3] : setKey + ".jpg";

        string[] tags = tagArg.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string slug   = AiCacheKey.NormalizeTags(tags);

        if (!Assets.Exists(baseKey))
        {
            Console.Error.WriteLine($"base asset not embedded: {baseKey}");
            return 1;
        }

        byte[] src = Assets.Bytes(baseKey);
        string ext = System.IO.Path.GetExtension(baseKey);

        var gen = new ProceduralImageGenerator();
        var req = new AiGenRequest(setKey, baseKey, tags, slug, "", null, "img2img");
        string outPath = $"ai-gen-{slug}.png";

        bool ok = gen.Generate(req, src, ext, outPath);
        if (!ok)
        {
            Console.Error.WriteLine("generation failed");
            return 1;
        }

        long bytes = new FileInfo(outPath).Length;
        Console.WriteLine($"wrote {outPath}  ({bytes} bytes)  from {baseKey}  tags=[{slug}]");
        return 0;
    }
}
