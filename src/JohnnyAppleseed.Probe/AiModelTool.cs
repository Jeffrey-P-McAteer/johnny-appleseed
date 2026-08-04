using JohnnyAppleseed.Ai;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Fetch + verify the neural image model into the app-data ai-models/ folder, with
/// progress. Lets you pre-provision (and confirm the download/checksum path works)
/// without launching the game or building the ONNX engine - <see cref="AiModels"/> is
/// pure download/verify and has no ONNX dependency.
///
/// Usage: <c>uv run scripts/probe.py ai-model</c>          (download if missing)
///        <c>uv run scripts/probe.py ai-model check</c>    (report status only)
/// </summary>
static class AiModelTool
{
    public static int Run(string[] args)
    {
        AiPaths.Initialize();
        Console.WriteLine($"model:     {AiModels.ModelId}");
        Console.WriteLine($"model dir: {AiModels.ModelDir}");
        Console.WriteLine($"installed: {AiModels.IsInstalled}");

        if (args.Length > 1 && args[1] == "check")
            return 0;

        if (AiModels.IsInstalled)
        {
            Console.WriteLine("already installed (.complete present) - nothing to do.");
            return 0;
        }

        Console.WriteLine("downloading (~4 GB, one time) ...");
        bool ok = AiModels.EnsureAsync(m => Console.WriteLine("  " + m)).GetAwaiter().GetResult();
        Console.WriteLine(ok ? "OK - model ready" : "FAILED - see log above");
        return ok ? 0 : 1;
    }
}
