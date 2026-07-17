using System.Runtime.Versioning;

namespace JohnnyAppleseed.Platform;

/// <summary>
/// Detects the active Linux display server and guides GLFW's backend
/// selection before <c>Raylib.InitWindow()</c> is called.
///
/// Background
/// ──────────
/// The Raylib-cs NuGet libraylib.so is compiled with GLFW 3.4 but only the
/// X11 backend enabled.  On a Wayland desktop both WAYLAND_DISPLAY (native
/// compositor) and DISPLAY (XWayland compatibility socket) are typically set,
/// so the game runs via XWayland without any extra setup.
///
/// "Native Wayland" — speaking directly to the Wayland compositor — gives
/// better HiDPI scaling, lower latency, and works on pure Wayland sessions
/// that don't run XWayland at all.
///
/// Enabling native Wayland
/// ───────────────────────
/// Build a GLFW 3.4 multi-platform libraylib.so (both X11 and Wayland
/// backends compiled in) with:
///
///     uv run scripts/setup-native-libs.py linux-wayland
///
/// Then package or publish for linux-x64:
///
///     uv run scripts/package.py linux-x64
///
/// The MSBuild target <c>GenerateBuildInfo</c> detects the Wayland lib at
/// compile time and sets <c>BuildInfo.WaylandEnabled = true</c>.  When the
/// binary starts on a Wayland session, this class unsets DISPLAY so GLFW
/// 3.4's backend auto-detect chooses Wayland.
///
/// If <c>BuildInfo.WaylandEnabled</c> is false (default NuGet X11-only lib),
/// DISPLAY is left intact and the game runs via XWayland — safe on all desktops.
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

        bool hasWayland     = !string.IsNullOrEmpty(waylandDisplay);
        bool hasX11         = !string.IsNullOrEmpty(display);
        bool waylandSession = sessionType is "wayland" || (hasWayland && !hasX11);

        if (!hasWayland && !hasX11)
        {
            Detected = Backend.Unknown;
            Console.Error.WriteLine(
                "[Display] Neither DISPLAY nor WAYLAND_DISPLAY is set — " +
                "cannot open a window.");
            return;
        }

        if (hasWayland && hasX11 && waylandSession)
        {
            // Wayland compositor with XWayland running alongside it (typical
            // GNOME / KDE Wayland setup).  Prefer native Wayland when the
            // multi-platform lib is compiled in.
            if (WaylandLibPresent())
            {
                // Unset DISPLAY: GLFW 3.4's auto-detect then picks the
                // Wayland backend (WAYLAND_DISPLAY set, DISPLAY absent).
                Environment.SetEnvironmentVariable("DISPLAY", null);
                Detected = Backend.WaylandNative;
                Console.Error.WriteLine("[Display] Wayland (native)");
            }
            else
            {
                Detected = Backend.XWayland;
                Console.Error.WriteLine(
                    "[Display] XWayland  " +
                    "(build with `uv run scripts/setup-native-libs.py linux-wayland` " +
                    "then repackage to enable native Wayland)");
            }
            return;
        }

        if (hasWayland && !hasX11)
        {
            // Pure Wayland — no XWayland fallback.
            if (!WaylandLibPresent())
            {
                Console.Error.WriteLine(
                    "[Display] FATAL: Pure Wayland session but this binary was " +
                    "compiled without Wayland support.\n" +
                    "          Run `uv run scripts/setup-native-libs.py linux-wayland` " +
                    "then repackage.");
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

    // ── private ───────────────────────────────────────────────────────────────

    // BuildInfo.WaylandEnabled is a compile-time constant stamped by the
    // GenerateBuildInfo MSBuild target.  It is true only when the binary was
    // published for linux-x64 AND the Wayland-capable libraylib.so (built by
    // setup-native-libs.py linux-wayland) was present at compile time.
    //
    // Using a constant instead of a sidecar sentinel file means Wayland
    // detection works correctly regardless of how the binary is packaged or
    // redistributed — no extra file to carry around.
    private static bool WaylandLibPresent() => BuildInfo.WaylandEnabled;
}
