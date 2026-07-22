using Raylib_cs;
using JohnnyAppleseed;              // AppData, BuildInfo, Assets (internal, exposed to us)
using JohnnyAppleseed.Input;        // InputSystem, InputAction (internal)

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Hardware-probe / dev-tooling entry point. Replaces the game's Main() with
/// measurement and verification logic while reusing the game's actual code, so
/// what we measure and test is exactly what the game sees. Keeping this here (not
/// in the game) leaves src/JohnnyAppleseed as pure game logic.
///
/// Modes:
///   (default) / probe   Interactive window: live gamepad state + console log of
///                       every button/axis edge with its raw code.
///   list                Enumerate gamepads (raylib) and input devices (Linux).
///   raw [dev]           Read raw Linux joystick events straight from the kernel
///                       (default /dev/input/js0), bypassing raylib entirely.
///   assets              List the assets embedded in the game binary.
///   capture [menu|intro] [seconds] [out.png]
///                       Render a scene headlessly and write a PNG screenshot.
///   selftest [save|input]
///                       Run the headless save / input self-tests (default: both).
///                       Exit code 0 = all passed.
/// </summary>
static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";

        Console.WriteLine($"Johnny Appleseed — probe/dev tools  (game build {BuildInfo.Version})");
        Console.WriteLine($"app-data: {AppData.Path}");
        Console.WriteLine();

        return mode switch
        {
            "list"          => GamepadProbe.List(),
            "raw" or "js"   => RawJoystick.Run(args.Length > 1 ? args[1] : "/dev/input/js0"),
            "probe" or ""   => GamepadProbe.Interactive(),
            "assets"        => AssetProbe.List(),
            "capture"       => RunCapture(args),
            "selftest"      => RunSelfTest(args),
            _               => Usage(mode),
        };
    }

    // capture [menu|intro] [seconds] [out.png]
    private static int RunCapture(string[] args)
    {
        string scene   = args.Length > 1 ? args[1].ToLowerInvariant() : "menu";
        if (scene is not ("menu" or "intro"))
        {
            Console.Error.WriteLine($"unknown scene '{scene}'. valid: menu | intro");
            return 2;
        }
        float  seconds = args.Length > 2 && float.TryParse(args[2], out float s) ? s : 1.2f;
        string outPath = args.Length > 3 ? args[3] : $"capture-{scene}.png";
        return Capture.Run(scene, seconds, outPath);
    }

    // selftest [save|input]  → default runs both, ORs their exit codes.
    private static int RunSelfTest(string[] args)
    {
        string? which = args.Length > 1 ? args[1].ToLowerInvariant() : null;
        bool save  = which is null or "save";
        bool input = which is null or "input";
        if (!save && !input)
        {
            Console.Error.WriteLine($"unknown suite '{which}'. valid: save | input (default: both)");
            return 2;
        }

        int rc = 0;
        if (save)  rc |= SaveSelfTest.Run();
        if (input) rc |= InputSelfTest.Run();
        return rc;
    }

    private static int Usage(string bad)
    {
        Console.Error.WriteLine(
            $"unknown mode '{bad}'. valid: probe (default) | list | raw [device] | " +
            "assets | capture [menu|intro] [secs] [out.png] | selftest [save|input]");
        return 2;
    }
}
