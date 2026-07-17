using System.Runtime.InteropServices;

namespace JohnnyAppleseed;

static class AppData
{
    public static readonly string Path = ResolveAppDataPath();

    public static void Initialize()
    {
        Directory.CreateDirectory(Path);
    }

    private static string ResolveAppDataPath()
    {
        string folder;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // %LOCALAPPDATA%\JohnnyAppleseed
            folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JohnnyAppleseed");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // ~/Library/Application Support/JohnnyAppleseed
            folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "JohnnyAppleseed");
        }
        else
        {
            // XDG_DATA_HOME/JohnnyAppleseed  (defaults to ~/.local/share)
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(xdgDataHome))
                xdgDataHome = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");
            folder = System.IO.Path.Combine(xdgDataHome, "JohnnyAppleseed");
        }

        return folder;
    }
}
