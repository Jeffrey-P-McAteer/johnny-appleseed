using Raylib_cs;
using JohnnyAppleseed;              // AppData, BuildInfo (internal, exposed to us)
using JohnnyAppleseed.Input;        // InputSystem, InputAction (internal)

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Hardware-probe entry point. Replaces the game's Main() with measurement logic
/// while reusing the game's actual input layer, so what we measure is exactly
/// what the game sees.
///
/// Modes:
///   (default) / probe   Interactive window: live gamepad state + console log of
///                       every button/axis edge with its raw code.
///   list                Enumerate gamepads (raylib) and input devices (Linux),
///                       print, and exit.
///   raw [dev]           Read raw Linux joystick events straight from the kernel
///                       (default /dev/input/js0), bypassing raylib entirely.
/// </summary>
static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";

        Console.WriteLine($"Johnny Appleseed — hardware probe  (game build {BuildInfo.Version})");
        Console.WriteLine($"app-data: {AppData.Path}");
        Console.WriteLine();

        return mode switch
        {
            "list"          => GamepadProbe.List(),
            "raw" or "js"   => RawJoystick.Run(args.Length > 1 ? args[1] : "/dev/input/js0"),
            "probe" or ""   => GamepadProbe.Interactive(),
            _               => Usage(mode),
        };
    }

    private static int Usage(string bad)
    {
        Console.Error.WriteLine($"unknown mode '{bad}'. valid: probe (default) | list | raw [device]");
        return 2;
    }
}
