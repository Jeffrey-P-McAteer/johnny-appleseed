namespace JohnnyAppleseed.Probe;

/// <summary>
/// Reads raw events straight from a Linux joystick device (the legacy but simple
/// <c>/dev/input/jsN</c> API), bypassing Raylib and GLFW entirely. This is the
/// ground truth: the button/axis <em>numbers</em> printed here come directly from
/// the kernel, so they prove what the hardware emits regardless of any mapping
/// layer above it.
///
/// The joystick event record is 8 bytes, little-endian:
///   uint32 time (ms) · int16 value · uint8 type · uint8 number
/// type: 0x01 = button, 0x02 = axis, 0x80 flag = synthetic "initial state" event.
/// </summary>
static class RawJoystick
{
    private const byte JsEventButton = 0x01;
    private const byte JsEventAxis   = 0x02;
    private const byte JsEventInit   = 0x80;

    public static int Run(string device)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("raw joystick reading is Linux-only.");
            return 2;
        }

        Console.WriteLine($"reading raw kernel events from {device}");
        Console.WriteLine("(type=BTN/AXIS, number=kernel index, value=state)  —  Ctrl-C to stop\n");

        FileStream fs;
        try
        {
            fs = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"no such device: {device}. Try `list` to see joystick nodes.");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"permission denied on {device}. Add yourself to the 'input' group " +
                "(sudo usermod -aG input $USER, then re-login) or run with sufficient privileges.");
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"could not open {device}: {ex.Message}");
            return 1;
        }

        var buf = new byte[8];
        using (fs)
        {
            while (true)
            {
                if (!ReadExactly(fs, buf))
                {
                    Console.WriteLine("device closed.");
                    return 0;
                }

                uint time  = BitConverter.ToUInt32(buf, 0);
                short value = BitConverter.ToInt16(buf, 4);
                byte type   = buf[6];
                byte number = buf[7];

                bool init = (type & JsEventInit) != 0;
                byte kind = (byte)(type & ~JsEventInit);
                string k = kind switch
                {
                    JsEventButton => "BTN ",
                    JsEventAxis   => "AXIS",
                    _             => $"0x{kind:x2}",
                };

                Console.WriteLine(
                    $"[{time,8} ms] {k}  number={number,2}  value={value,6}{(init ? "   (init)" : "")}");
            }
        }
    }

    // FileStream.Read may return short reads; loop until the 8-byte record is full.
    private static bool ReadExactly(FileStream fs, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = fs.Read(buf, off, buf.Length - off);
            if (n == 0)
                return false;
            off += n;
        }
        return true;
    }
}
