using Raylib_cs;

namespace JohnnyAppleseed.Input;

/// <summary>
/// Unified input layer: keyboard, gamepad, and (for spatial queries) mouse.
///
/// Call <see cref="Initialize"/> once after the window is created, then
/// <see cref="Update"/> once at the top of each game-loop iteration before any
/// scene logic runs.  Scenes query logical actions via <see cref="IsPressed"/> /
/// <see cref="IsDown"/> rather than polling Raylib directly.
///
/// ── Gamepad hot-plugging ────────────────────────────────────────────────────
/// Controllers may be connected or disconnected at any time.  Rather than assume
/// a fixed slot, <see cref="Update"/> resolves an <em>active gamepad</em> every
/// frame by scanning all slots (<see cref="MaxGamepads"/>):
///
///   • It keeps the current active pad while that slot stays available (stable,
///     so input never jumps between two connected pads).
///   • If the active pad vanishes (unplugged), it re-scans for another.
///   • If none was active, the first available slot is adopted (plug-after-start).
///
/// This is why every button/axis query uses the resolved <see cref="_active"/>
/// index and never a hardcoded slot 0 — a controller enumerating on slot 1+ (or
/// plugged in mid-game) works exactly the same as one present at launch.
///
/// Mouse clicks are intentionally not mapped here — they carry spatial context
/// (position) that the scene needs to resolve anyway.
/// </summary>
static class InputSystem
{
    // ── constants ─────────────────────────────────────────────────────────────
    private const int   MaxGamepads   = 4;      // raylib's default MAX_GAMEPADS
    private const float AxisThreshold = 0.5f;   // dead-zone threshold
    private const int   NoGamepad     = -1;

    // ── state ─────────────────────────────────────────────────────────────────
    private static int   _active = NoGamepad;   // resolved active gamepad slot, or -1
    private static float _axisLX, _axisLY;      // current frame
    private static float _pLX,    _pLY;         // previous frame (for edge detection)

    // ── lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One-time setup after <c>InitWindow</c>.  Loads an up-to-date SDL gamepad
    /// mapping database if one is shipped alongside the game, so controllers newer
    /// than GLFW's built-in table still map their face buttons correctly (the
    /// classic "controller detected but A/B do nothing" case on Linux).
    /// </summary>
    public static void Initialize()
    {
        TryLoadGamepadMappings();
    }

    /// <summary>Advance the input state; must be called once per frame.</summary>
    public static void Update()
    {
        int previous = _active;
        _active = ResolveActiveGamepad(_active, IsSlotAvailable, MaxGamepads);

        if (_active != previous)
            OnActiveGamepadChanged(previous, _active);

        _pLX = _axisLX;
        _pLY = _axisLY;

        if (_active >= 0)
        {
            _axisLX = Raylib.GetGamepadAxisMovement(_active, GamepadAxis.LeftX);
            _axisLY = Raylib.GetGamepadAxisMovement(_active, GamepadAxis.LeftY);
        }
        else
        {
            _axisLX = _axisLY = 0f;
        }
    }

    /// <summary>
    /// Pure gamepad-selection policy, separated from Raylib so it can be unit
    /// tested.  Keeps <paramref name="current"/> while it stays available;
    /// otherwise returns the lowest available slot, or -1 when none are.
    /// </summary>
    internal static int ResolveActiveGamepad(int current, Func<int, bool> isAvailable, int max)
    {
        if (current >= 0 && current < max && isAvailable(current))
            return current;

        for (int i = 0; i < max; i++)
            if (isAvailable(i))
                return i;

        return NoGamepad;
    }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// True on the frame the action transitions from inactive to active.
    /// Analog-stick directions fire once per threshold crossing.
    /// </summary>
    public static bool IsPressed(InputAction action)
    {
        int  gp    = _active;
        bool hasGp = gp >= 0;

        return action switch
        {
            InputAction.Up =>
                Raylib.IsKeyPressed(KeyboardKey.Up)   ||
                Raylib.IsKeyPressed(KeyboardKey.W)    ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.LeftFaceUp))   ||
                AxisEdge(_axisLY, _pLY, negative: true),

            InputAction.Down =>
                Raylib.IsKeyPressed(KeyboardKey.Down) ||
                Raylib.IsKeyPressed(KeyboardKey.S)    ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.LeftFaceDown)) ||
                AxisEdge(_axisLY, _pLY, negative: false),

            InputAction.Left =>
                Raylib.IsKeyPressed(KeyboardKey.Left) ||
                Raylib.IsKeyPressed(KeyboardKey.A)    ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.LeftFaceLeft)) ||
                AxisEdge(_axisLX, _pLX, negative: true),

            InputAction.Right =>
                Raylib.IsKeyPressed(KeyboardKey.Right) ||
                Raylib.IsKeyPressed(KeyboardKey.D)     ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.LeftFaceRight)) ||
                AxisEdge(_axisLX, _pLX, negative: false),

            InputAction.Confirm =>
                Raylib.IsKeyPressed(KeyboardKey.Enter) ||
                Raylib.IsKeyPressed(KeyboardKey.Space) ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.RightFaceDown)),

            InputAction.Cancel =>
                Raylib.IsKeyPressed(KeyboardKey.Escape) ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.RightFaceRight)),

            // LB / L1  →  Q or PageUp on keyboard
            InputAction.ShortcutLeft =>
                Raylib.IsKeyPressed(KeyboardKey.Q)       ||
                Raylib.IsKeyPressed(KeyboardKey.PageUp)  ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.LeftTrigger1)),

            // RB / R1  →  E or PageDown on keyboard
            InputAction.ShortcutRight =>
                Raylib.IsKeyPressed(KeyboardKey.E)        ||
                Raylib.IsKeyPressed(KeyboardKey.PageDown) ||
                (hasGp && Raylib.IsGamepadButtonPressed(gp, GamepadButton.RightTrigger1)),

            _ => false
        };
    }

    /// <summary>
    /// True while the action is continuously held.
    /// Useful for movement in gameplay; prefer <see cref="IsPressed"/> for menus.
    /// </summary>
    public static bool IsDown(InputAction action)
    {
        int  gp    = _active;
        bool hasGp = gp >= 0;

        return action switch
        {
            InputAction.Up =>
                Raylib.IsKeyDown(KeyboardKey.Up)   || Raylib.IsKeyDown(KeyboardKey.W) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.LeftFaceUp))   ||
                _axisLY < -AxisThreshold,

            InputAction.Down =>
                Raylib.IsKeyDown(KeyboardKey.Down) || Raylib.IsKeyDown(KeyboardKey.S) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.LeftFaceDown)) ||
                _axisLY > AxisThreshold,

            InputAction.Left =>
                Raylib.IsKeyDown(KeyboardKey.Left) || Raylib.IsKeyDown(KeyboardKey.A) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.LeftFaceLeft)) ||
                _axisLX < -AxisThreshold,

            InputAction.Right =>
                Raylib.IsKeyDown(KeyboardKey.Right) || Raylib.IsKeyDown(KeyboardKey.D) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.LeftFaceRight)) ||
                _axisLX > AxisThreshold,

            InputAction.Confirm =>
                Raylib.IsKeyDown(KeyboardKey.Enter) || Raylib.IsKeyDown(KeyboardKey.Space) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.RightFaceDown)),

            InputAction.Cancel =>
                Raylib.IsKeyDown(KeyboardKey.Escape) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.RightFaceRight)),

            InputAction.ShortcutLeft =>
                Raylib.IsKeyDown(KeyboardKey.Q) || Raylib.IsKeyDown(KeyboardKey.PageUp) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.LeftTrigger1)),

            InputAction.ShortcutRight =>
                Raylib.IsKeyDown(KeyboardKey.E) || Raylib.IsKeyDown(KeyboardKey.PageDown) ||
                (hasGp && Raylib.IsGamepadButtonDown(gp, GamepadButton.RightTrigger1)),

            _ => false
        };
    }

    /// <summary>Whether a gamepad is currently connected and active.</summary>
    public static bool IsGamepadConnected => _active >= 0;

    /// <summary>Slot of the resolved active gamepad, or -1 if none. Diagnostics/tools.</summary>
    public static int ActiveGamepad => _active;

    /// <summary>Human-readable name of the active gamepad, or "" if none.</summary>
    public static string GamepadName =>
        _active >= 0 ? (Raylib.GetGamepadName_(_active) ?? "") : "";

    // ── helpers ───────────────────────────────────────────────────────────────

    private static bool IsSlotAvailable(int slot) => Raylib.IsGamepadAvailable(slot);

    private static void OnActiveGamepadChanged(int previous, int active)
    {
        if (active >= 0)
            Console.Error.WriteLine(
                $"[input] gamepad connected: slot {active} \"{Raylib.GetGamepadName_(active)}\"");
        else if (previous >= 0)
            Console.Error.WriteLine("[input] gamepad disconnected; using keyboard/mouse");

        // Discard any stale axis reading from the previous device so switching
        // controllers can't emit a phantom navigation edge on the first frame.
        _axisLX = _axisLY = _pLX = _pLY = 0f;
    }

    // Detects a single-frame edge: axis just crossed the threshold this frame.
    private static bool AxisEdge(float curr, float prev, bool negative) =>
        negative
            ? curr < -AxisThreshold && prev >= -AxisThreshold
            : curr >  AxisThreshold && prev <=  AxisThreshold;

    // Load a shipped gamecontrollerdb.txt (SDL mapping database) if present next
    // to the executable or in the app-data folder.  Missing file is not an error:
    // GLFW's built-in mappings still cover most mainstream controllers.
    private static void TryLoadGamepadMappings()
    {
        foreach (string dir in new[] { AppContext.BaseDirectory, AppData.Path })
        {
            string path = System.IO.Path.Combine(dir, "gamecontrollerdb.txt");
            if (!File.Exists(path))
                continue;

            try
            {
                string db = File.ReadAllText(path);
                Raylib.SetGamepadMappings(db);
                Console.Error.WriteLine($"[input] loaded gamepad mappings from {path}");
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[input] failed to load gamepad mappings ({path}): {ex.Message}");
            }
        }
    }
}
