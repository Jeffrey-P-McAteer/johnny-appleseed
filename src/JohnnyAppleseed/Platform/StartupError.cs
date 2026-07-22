using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JohnnyAppleseed.Platform;

/// <summary>
/// Cross-platform, best-effort native error dialog for fatal startup failures —
/// shown when the game can't get far enough to draw its own UI (most often the
/// graphics device / OpenGL context failing to initialize). It uses whatever
/// low-level facility each OS always provides, so it works even when raylib is
/// dead: a Win32 message box on Windows, <c>osascript</c> on macOS, and
/// <c>zenity</c>/<c>kdialog</c> on Linux. It never throws, and always echoes the
/// same text to stderr as a universal fallback.
/// </summary>
static class StartupError
{
    public static void Show(string title, string message)
    {
        try
        {
            if (OperatingSystem.IsWindows())     ShowWindows(title, message);
            else if (OperatingSystem.IsMacOS())  ShowMac(title, message);
            else if (OperatingSystem.IsLinux())  ShowLinux(title, message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[johnny-appleseed] could not show error dialog: {ex.Message}");
        }

        // Universal fallback — also useful when launched from a terminal.
        Console.Error.WriteLine($"[johnny-appleseed] STARTUP ERROR — {title}\n{message}");
    }

    // Windows: MessageBoxW is always available and its text is Ctrl+C copyable.
    static void ShowWindows(string title, string message)
    {
        const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        MessageBoxW(IntPtr.Zero, message, title, MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    // macOS: message/title are passed as argv (via `on run argv`) so there is no
    // AppleScript string escaping to get wrong, and newlines survive intact.
    static void ShowMac(string title, string message)
    {
        Run("/usr/bin/osascript", new[]
        {
            "-e", "on run argv",
            "-e", "display dialog (item 1 of argv) with title (item 2 of argv) " +
                  "buttons {\"Close\"} default button \"Close\" with icon stop",
            "-e", "end run",
            "--", message, title,
        });
    }

    // Linux: prefer a real GUI dialog if the desktop provides one; otherwise the
    // stderr fallback in Show() is all we can offer.
    static void ShowLinux(string title, string message)
    {
        if (TryRun("zenity", new[] { "--error", "--no-wrap", "--title", title, "--text", message })) return;
        TryRun("kdialog", new[] { "--error", message, "--title", title });
    }

    static void Run(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.WaitForExit();
    }

    static bool TryRun(string file, string[] args)
    {
        try { Run(file, args); return true; }
        catch { return false; }   // command not installed → try the next one
    }
}
