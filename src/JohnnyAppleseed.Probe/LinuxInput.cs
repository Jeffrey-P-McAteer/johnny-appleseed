namespace JohnnyAppleseed.Probe;

/// <summary>
/// Reads the kernel's own view of input devices on Linux from
/// <c>/proc/bus/input/devices</c> — unprivileged and independent of Raylib, so it
/// helps distinguish "the OS doesn't see the pad" from "Raylib maps it wrong".
/// </summary>
static class LinuxInput
{
    private const string DevicesFile = "/proc/bus/input/devices";

    public static void PrintDevices()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("(input-device listing is Linux-only)\n");
            return;
        }

        if (!File.Exists(DevicesFile))
        {
            Console.WriteLine($"({DevicesFile} not present)\n");
            return;
        }

        Console.WriteLine($"kernel input devices ({DevicesFile}):");

        string? name = null;
        string? handlers = null;
        foreach (string raw in File.ReadLines(DevicesFile))
        {
            string line = raw.TrimEnd();

            if (line.StartsWith("N: Name="))
                name = line["N: Name=".Length..].Trim('"');
            else if (line.StartsWith("H: Handlers="))
                handlers = line["H: Handlers=".Length..].Trim();
            else if (line.Length == 0)   // blank line terminates a device block
            {
                PrintIfJoystick(name, handlers);
                name = handlers = null;
            }
        }
        PrintIfJoystick(name, handlers);   // last block (no trailing blank line)
        Console.WriteLine();
    }

    private static void PrintIfJoystick(string? name, string? handlers)
    {
        if (name == null || handlers == null)
            return;

        // Only surface things that expose a joystick node — that's what raylib/GLFW
        // consumes. (Full evdev "eventN" nodes are noted for the raw reader.)
        if (handlers.Contains("js"))
            Console.WriteLine($"  \"{name}\"  →  {handlers}");
    }
}
