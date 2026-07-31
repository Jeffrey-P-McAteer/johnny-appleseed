using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JohnnyAppleseed.Platform;

/// <summary>
/// Hands a path or URL to the OS to open with the user's default handler: the file
/// manager for a folder (Explorer / Finder / the XDG file manager) and the default
/// browser for a URL. Best-effort - failures are logged, never thrown, so a missing
/// helper can't crash the game.
/// </summary>
static class OpenExternal
{
    /// <summary>Open a folder path in the OS file manager.</summary>
    public static void OpenFolder(string path) => Open(path);

    /// <summary>Open a URL in the default browser.</summary>
    public static void OpenUrl(string url) => Open(url);

    private static void Open(string target)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // ShellExecute resolves both folders and URLs via their default handler.
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else
            {
                // macOS: `open`; Linux/other: `xdg-open`. ArgumentList avoids quoting issues.
                string launcher = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
                var psi = new ProcessStartInfo { FileName = launcher, UseShellExecute = false };
                psi.ArgumentList.Add(target);
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[open] could not open '{target}': {ex.Message}");
        }
    }
}
