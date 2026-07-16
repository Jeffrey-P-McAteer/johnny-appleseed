using System.Runtime.Versioning;

namespace JohnnyAppleseed.Platform;

/// <summary>
/// Detects the active Linux display server and guides GLFW's backend
/// selection before <c>Raylib.InitWindow()</c> is called.
///
/// Background
/// ──────────
/// The Raylib-cs NuGet libraylib.so is compiled with GLFW 3.4 but only the
/// X11 backend enabled.  On a typical Wayland desktop both WAYLAND_DISPLAY
/// (native compositor) and DISPLAY (XWayland compatibility socket) are set,
/// so the game runs today via XWayland without any special handling.
///
/// "Native Wayland" — talking directly to the Wayland compositor rather than
/// through XWayland — gives better HiDPI scaling, lower latency, and works on
/// pure Wayland sessions that don't run XWayland at all.
///
/// Enabling native Wayland
/// ───────────────────────
/// Build a GLFW-3.4 multi-platform libraylib.so (both X11 and Wayland backends
/// compiled in) with:
///
///     uv run scripts/setup-native-libs.py linux-wayland
///
/// That script places the library at
///   src/JohnnyAppleseed/runtimes/linux-x64/native/libraylib.so
/// and writes a sentinel file
///   src/JohnnyAppleseed/runtimes/linux-x64/native/.wayland-enabled
///
/// The .csproj includes these files in the publish output.  When the sentinel
/// is present at runtime this class unsets DISPLAY before InitWindow so that
/// GLFW 3.4's auto-detection picks the Wayland backend instead of X11.
///
/// If the sentinel is absent (NuGet X11-only lib), DISPLAY is left untouched
/// and the game falls back to XWayland transparently — safe on all desktops.
/// </summary>
[SupportedOSPlatform("linux")]
static class LinuxDisplay
{
    public enum Backend { X11, XWayland, WaylandNative, Unknown }

    public static Backend Detected { get; private set; } = Backend.Unknown;

    /// <summary>
    /// Must be called before <c>Raylib.InitWindow()</c>.
    /// Detects the display environment and, when safe, guides GLFW towards
    /// the native Wayland backend.
    /// </summary>
    public static void Configure()
    {
        string? waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        string? display        = Environment.GetEnvironmentVariable("DISPLAY");
        string? sessionType    = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";

        bool hasWayland    = !string.IsNullOrEmpty(waylandDisplay);
        bool hasX11        = !string.IsNullOrEmpty(display);
        bool waylandSession = sessionType is "wayland" || (hasWayland && !hasX11);

        if (!hasWayland && !hasX11)
        {
            // No display at all — Raylib will fail to open a window; surface
            // the problem early with a readable message.
            Detected = Backend.Unknown;
            Console.Error.WriteLine("[Display] Neither DISPLAY nor WAYLAND_DISPLAY is set. The game cannot open a window.");
            return;
        }

        if (hasWayland && hasX11 && waylandSession)
        {
            // Wayland compositor running XWayland alongside itself (the usual
            // GNOME/KDE Wayland setup).
            if (WaylandLibPresent())
            {
                // Multi-platform lib is installed: unset DISPLAY so GLFW 3.4's
                // platform auto-detect picks Wayland over X11/XWayland.
                Environment.SetEnvironmentVariable("DISPLAY", null);
                Detected = Backend.WaylandNative;
                Console.Error.WriteLine("[Display] Native Wayland (multi-platform libraylib)");
            }
            else
            {
                Detected = Backend.XWayland;
                Console.Error.WriteLine(
                    "[Display] XWayland (Wayland session detected; run " +
                    "`uv run scripts/setup-native-libs.py linux-wayland` " +
                    "to enable native Wayland)");
            }
            return;
        }

        if (hasWayland && !hasX11)
        {
            // Pure Wayland — no XWayland fallback available.
            if (!WaylandLibPresent())
            {
                // X11-only lib + no DISPLAY = InitWindow will fail.
                Console.Error.WriteLine(
                    "[Display] FATAL: Pure Wayland session but libraylib.so " +
                    "has no Wayland backend compiled in.\n" +
                    "          Run `uv run scripts/setup-native-libs.py linux-wayland` " +
                    "to build a Wayland-capable library, then re-package.");
            }
            else
            {
                Detected = Backend.WaylandNative;
                Console.Error.WriteLine("[Display] Wayland (native, no XWayland)");
            }
            return;
        }

        // DISPLAY set, no WAYLAND_DISPLAY — plain X11 desktop.
        Detected = Backend.X11;
        Console.Error.WriteLine("[Display] X11");
    }

    // ── private helpers ───────────────────────────────────────────────────────

    // The sentinel file is written by setup-native-libs.py alongside the
    // multi-platform libraylib.so.  Its presence is the only reliable signal
    // that Wayland backend code is compiled into the running library.
    private static bool WaylandLibPresent() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, ".wayland-enabled"));
}
