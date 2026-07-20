using JohnnyAppleseed.Dev;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Save;

namespace JohnnyAppleseed;

static class Program
{
    [System.STAThread]
    public static int Main(string[] args)
    {
        // Headless verification — no window is created. `--selftest` runs all.
        bool all = Array.Exists(args, a => a is "--selftest");
        bool save = all || Array.Exists(args, a => a is "--selftest-save");
        bool input = all || Array.Exists(args, a => a is "--selftest-input");
        if (save || input)
        {
            int rc = 0;
            if (save)  rc |= SaveSelfTest.Run();
            if (input) rc |= InputSelfTest.Run();
            return rc;
        }

        // Dev screenshot: --capture-intro|--capture-menu [seconds] [out.png]
        int cap = Array.FindIndex(args, a => a.StartsWith("--capture"));
        if (cap >= 0)
        {
            string scene = args[cap].Contains("menu") ? "menu" : "intro";
            float secs = cap + 1 < args.Length && float.TryParse(args[cap + 1], out var s) ? s : 1.2f;
            string outPath = cap + 2 < args.Length ? args[cap + 2] : $"capture-{scene}.png";
            return Capture.Run(scene, secs, outPath);
        }

        Game.Run();
        return 0;
    }
}
