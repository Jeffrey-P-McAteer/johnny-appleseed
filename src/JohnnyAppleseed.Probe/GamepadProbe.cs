using Raylib_cs;
using System.Diagnostics;
using JohnnyAppleseed.Input;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// raylib-level gamepad measurement — reports the controller exactly as the game's
/// stack (Raylib + GLFW) sees it. The button "codes" printed here are Raylib's
/// <see cref="GamepadButton"/> values, i.e. the very numbers the game maps its
/// actions against. For kernel-level codes use the <c>raw</c> mode instead.
/// </summary>
static class GamepadProbe
{
    private const int MaxGamepads = 4;
    private const float AxisLogThreshold = 0.35f;   // report axis moves past this

    // Every button worth polling (skip Unknown=0).
    private static readonly GamepadButton[] Buttons =
        Enum.GetValues<GamepadButton>().Where(b => b != GamepadButton.Unknown).ToArray();

    private static readonly GamepadAxis[] Axes = Enum.GetValues<GamepadAxis>();

    // ── list mode ─────────────────────────────────────────────────────────────

    public static int List()
    {
        LinuxInput.PrintDevices();

        // Raylib only populates gamepad state after the window exists and input has
        // been polled a few times, so spin briefly before reading.
        Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        Raylib.InitWindow(320, 200, "probe: list");
        for (int i = 0; i < 30 && !Raylib.WindowShouldClose(); i++)
        {
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            Raylib.EndDrawing();
        }

        Console.WriteLine("raylib gamepads:");
        bool any = false;
        for (int slot = 0; slot < MaxGamepads; slot++)
        {
            if (!Raylib.IsGamepadAvailable(slot))
                continue;
            any = true;
            Console.WriteLine($"  slot {slot}: \"{Raylib.GetGamepadName_(slot)}\"  " +
                              $"axes={Raylib.GetGamepadAxisCount(slot)}");
        }
        if (!any)
            Console.WriteLine("  (none detected)");

        Raylib.CloseWindow();
        return 0;
    }

    // ── interactive mode ──────────────────────────────────────────────────────

    public static int Interactive()
    {
        PrintButtonLegend();
        Console.WriteLine("Press buttons / move sticks. Edges are logged below. Close window or Esc to quit.\n");

        Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);
        Raylib.InitWindow(960, 640, "Johnny Appleseed — gamepad probe");
        Raylib.SetTargetFPS(60);
        InputSystem.Initialize();

        var clock = Stopwatch.StartNew();
        var down = new HashSet<(int slot, GamepadButton btn)>();
        var axisLast = new Dictionary<(int slot, GamepadAxis axis), float>();

        while (!Raylib.WindowShouldClose())
        {
            InputSystem.Update();
            ScanAndLog(clock, down, axisLast);

            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(10, 12, 20, 255));
            DrawState();
            Raylib.EndDrawing();
        }

        Raylib.CloseWindow();
        return 0;
    }

    // Detect and log button/axis edges across every connected slot.
    private static void ScanAndLog(
        Stopwatch clock,
        HashSet<(int, GamepadButton)> down,
        Dictionary<(int, GamepadAxis), float> axisLast)
    {
        long ms = clock.ElapsedMilliseconds;

        for (int slot = 0; slot < MaxGamepads; slot++)
        {
            if (!Raylib.IsGamepadAvailable(slot))
            {
                // Clear any lingering "down" state for a pad that just vanished.
                down.RemoveWhere(k => k.Item1 == slot);
                continue;
            }

            foreach (GamepadButton b in Buttons)
            {
                bool isDown = Raylib.IsGamepadButtonDown(slot, b);
                var key = (slot, b);
                bool wasDown = down.Contains(key);

                if (isDown && !wasDown)
                {
                    down.Add(key);
                    Log(ms, slot, $"BTN v DOWN  code={(int)b,2}  {b}");
                }
                else if (!isDown && wasDown)
                {
                    down.Remove(key);
                    Log(ms, slot, $"BTN ^ UP    code={(int)b,2}  {b}");
                }
            }

            foreach (GamepadAxis a in Axes)
            {
                float v = Raylib.GetGamepadAxisMovement(slot, a);
                var key = (slot, a);
                axisLast.TryGetValue(key, out float prev);
                // Log only meaningful moves and only when crossing the threshold,
                // to avoid drowning the console in analog jitter.
                if (MathF.Abs(v) >= AxisLogThreshold && MathF.Abs(prev) < AxisLogThreshold)
                    Log(ms, slot, $"AXIS    code={(int)a}  {a,-12} value={v:+0.00;-0.00}");
                axisLast[key] = v;
            }
        }
    }

    private static void Log(long ms, int slot, string msg) =>
        Console.WriteLine($"[{ms,7} ms] gp{slot}  {msg}");

    private static void PrintButtonLegend()
    {
        Console.WriteLine("Raylib GamepadButton codes (what the game maps against):");
        foreach (GamepadButton b in Buttons)
            Console.WriteLine($"  {(int)b,2} = {b}");
        Console.WriteLine();
    }

    // ── on-screen live view ────────────────────────────────────────────────────

    private static void DrawState()
    {
        int y = 16;
        void Line(string s, Color c, int size = 20)
        {
            Raylib.DrawText(s, 16, y, size, c);
            y += size + 6;
        }

        var white = new Color(230, 230, 235, 255);
        var gold  = new Color(255, 210, 80, 255);
        var dim   = new Color(140, 140, 160, 255);
        var green = new Color(120, 230, 140, 255);

        Line("GAMEPAD PROBE", gold, 24);

        // What the game's InputSystem resolved.
        string active = InputSystem.IsGamepadConnected
            ? $"slot {InputSystem.ActiveGamepad}  \"{InputSystem.GamepadName}\""
            : "none (keyboard/mouse)";
        Line($"InputSystem active gamepad: {active}", white);

        // Logical actions the game would receive right now.
        y += 4;
        Line("logical actions (held):", dim, 18);
        foreach (InputAction a in Enum.GetValues<InputAction>())
        {
            bool held = InputSystem.IsDown(a);
            Raylib.DrawText($"  {a}", 16, y, 18, held ? green : dim);
            if (held) Raylib.DrawText("●", 180, y, 18, green);
            y += 22;
        }

        // Raw per-slot readout.
        y += 8;
        Line("connected gamepads:", dim, 18);
        bool any = false;
        for (int slot = 0; slot < MaxGamepads; slot++)
        {
            if (!Raylib.IsGamepadAvailable(slot)) continue;
            any = true;
            Line($"  gp{slot}: {Raylib.GetGamepadName_(slot)}", white, 18);

            int lastPressed = Raylib.GetGamepadButtonPressed();
            if (lastPressed > 0)
                Line($"     last button code: {lastPressed} ({(GamepadButton)lastPressed})", gold, 18);

            for (int i = 0; i < Math.Min(Axes.Length, Raylib.GetGamepadAxisCount(slot)); i++)
            {
                float v = Raylib.GetGamepadAxisMovement(slot, Axes[i]);
                DrawAxisBar(Axes[i].ToString(), v, 16, y);
                y += 22;
            }
        }
        if (!any)
            Line("  (plug one in — it will appear here live)", dim, 18);
    }

    private static void DrawAxisBar(string label, float v, int x, int y)
    {
        const int w = 160, h = 12;
        int barX = x + 130;
        Raylib.DrawText(label, x + 8, y, 16, new Color(160, 160, 180, 255));
        Raylib.DrawRectangleLines(barX, y, w, h, new Color(90, 90, 110, 255));
        int mid = barX + w / 2;
        int len = (int)(v * (w / 2));
        Raylib.DrawRectangle(len >= 0 ? mid : mid + len, y, Math.Abs(len), h,
            new Color(120, 200, 255, 255));
        Raylib.DrawText($"{v:+0.00;-0.00}", barX + w + 8, y - 1, 16, new Color(200, 200, 210, 255));
    }
}
