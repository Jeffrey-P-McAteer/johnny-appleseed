using Raylib_cs;

namespace JohnnyAppleseed.Input;

/// <summary>
/// Unified, hot-plug-aware input layer: keyboard, gamepad, and (for spatial
/// queries) mouse.
///
/// Call <see cref="Initialize"/> once after the window is created, then
/// <see cref="Update"/> once at the top of each game-loop iteration before any
/// scene logic runs. Scenes query logical actions via <see cref="IsPressed"/> /
/// <see cref="IsDown"/> rather than polling Raylib directly.
///
/// ── Dynamic multi-gamepad tracking ──────────────────────────────────────────
/// All <see cref="MaxGamepads"/> slots are tracked simultaneously, because the OS
/// happily reports non-controllers (laptop touchpads, lid sensors, virtual
/// devices) as "gamepads" and they can occupy low slots. Rather than trust slot
/// order, each frame we count real input <em>events</em> (button-press edges and
/// stick/trigger dead-zone crossings) per slot over a rolling
/// <see cref="WindowSeconds"/>-second window.
///
/// For the (many) APIs that need a single controller, the <em>active</em> pad is:
///   1. the slot with the most events in the last 30 s (the pad you're using), else
///   2. on a tie or when nobody has produced any events, the "least-virtual" slot —
///      the one most likely to be a real gamepad, judged by name and axis-count
///      heuristics (see <see cref="EstimateRealness"/>; these are guesses, not
///      authoritative identification), else
///   3. the current pad (stability), else the lowest slot.
/// The upshot: an idle touchpad never steals focus, and the instant you press a
/// button on a real controller it becomes active — on any slot, at any time.
///
/// Mouse clicks are intentionally not mapped here — they carry spatial context
/// (position) that the scene needs to resolve anyway.
/// </summary>
static class InputSystem
{
    // ── constants ─────────────────────────────────────────────────────────────
    private const int   NoGamepad     = -1;
    private const int   MaxGamepads   = 4;      // raylib's default MAX_GAMEPADS
    private const float WindowSeconds = 30f;    // rolling event-count window
    private const float AxisThreshold = 0.5f;   // dead-zone: nav + event "engage"
    private const float AxisRelease   = 0.35f;  // event "disengage" (hysteresis)

    // Buttons/axes scanned every frame for event counting.
    private static readonly GamepadButton[] AllButtons =
        Enum.GetValues<GamepadButton>().Where(b => b != GamepadButton.Unknown).ToArray();
    private static readonly GamepadAxis[] AllAxes = Enum.GetValues<GamepadAxis>();

    // Heuristic name hints for the least-virtual tie-break. NOT authoritative —
    // device names vary by OS, driver, and locale; treat as a best-effort guess.
    private static readonly string[] VirtualNameHints =
    {
        "touchpad", "trackpad", "mouse", "keyboard", "consumer control",
        "system control", "accelerometer", "gyro", "power button", "sleep button",
        "video bus", "virtual", "uinput", "pc speaker", "headset", "webcam",
    };
    private static readonly string[] RealNameHints =
    {
        "xbox", "x-box", "360", "gamepad", "game pad", "controller", "joystick",
        "joypad", "dualshock", "dualsense", "playstation", "nintendo", "switch",
        "joy-con", "pro controller", "8bitdo", "logitech", "razer", "steam",
        "wheel", "gravis", "saitek", "thrustmaster",
    };

    // ── state ─────────────────────────────────────────────────────────────────
    private static int _active = NoGamepad;                 // resolved active slot, or -1
    private static readonly bool[] _available = new bool[MaxGamepads];

    private static float _axisLX, _axisLY;                  // active-pad nav axes, this frame
    private static float _pLX, _pLY;                        // previous frame (edge detection)

    // Per-slot event tracking (all four tracked at once).
    private static readonly Queue<double>[] _eventTimes =
        MakeArray(() => new Queue<double>());
    private static readonly HashSet<GamepadButton>[] _btnDown =
        MakeArray(() => new HashSet<GamepadButton>());
    private static readonly HashSet<GamepadAxis>[] _axisEngaged =
        MakeArray(() => new HashSet<GamepadAxis>());

    private static T[] MakeArray<T>(Func<T> factory)
    {
        var a = new T[MaxGamepads];
        for (int i = 0; i < MaxGamepads; i++) a[i] = factory();
        return a;
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One-time setup after <c>InitWindow</c>. Loads an up-to-date SDL gamepad
    /// mapping database if one is shipped alongside the game, so controllers newer
    /// than GLFW's built-in table still map their face buttons correctly.
    /// </summary>
    public static void Initialize() => TryLoadGamepadMappings();

    /// <summary>Advance the input state; must be called once per frame.</summary>
    public static void Update()
    {
        double now = Raylib.GetTime();
        int previous = _active;

        var candidates = new GamepadCandidate[MaxGamepads];
        for (int slot = 0; slot < MaxGamepads; slot++)
        {
            bool avail = Raylib.IsGamepadAvailable(slot);
            _available[slot] = avail;

            if (avail)
                DetectEvents(slot, now);
            else
                ResetEdgeState(slot);   // so a reconnect can't emit a phantom edge

            PruneWindow(slot, now);

            candidates[slot] = new GamepadCandidate(
                slot, avail, _eventTimes[slot].Count, avail ? EstimateRealness(slot) : 0);
        }

        _active = SelectActiveGamepad(candidates, _active);
        if (_active != previous)
            OnActiveGamepadChanged(previous, _active);

        // Nav axes for the active pad (edge detection for menu movement).
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

    // ── active-pad selection (pure, unit-tested) ────────────────────────────────

    /// <summary>A per-slot snapshot fed to <see cref="SelectActiveGamepad"/>.</summary>
    internal readonly record struct GamepadCandidate(
        int Slot, bool Available, int RecentEventCount, int Realness);

    /// <summary>
    /// Choose the single active gamepad from a snapshot of all slots. Pure — no
    /// Raylib — so the full policy is testable without hardware. Preference order:
    /// most recent events → higher realness → current pad (stability) → lowest slot.
    /// </summary>
    internal static int SelectActiveGamepad(IReadOnlyList<GamepadCandidate> candidates, int current)
    {
        GamepadCandidate? best = null;
        foreach (var c in candidates)
        {
            if (!c.Available) continue;
            if (best is null || Prefer(c, best.Value, current))
                best = c;
        }
        return best?.Slot ?? NoGamepad;
    }

    private static bool Prefer(GamepadCandidate a, GamepadCandidate b, int current)
    {
        if (a.RecentEventCount != b.RecentEventCount) return a.RecentEventCount > b.RecentEventCount;
        if (a.Realness != b.Realness)                 return a.Realness > b.Realness;
        if (a.Slot == current) return true;   // keep current on a full tie (no flip-flop)
        if (b.Slot == current) return false;
        return a.Slot < b.Slot;
    }

    // ── per-slot event tracking ─────────────────────────────────────────────────

    // Count a button-press edge or a dead-zone crossing as one "event".
    private static void DetectEvents(int slot, double now)
    {
        var down = _btnDown[slot];
        foreach (GamepadButton b in AllButtons)
        {
            bool isDown = Raylib.IsGamepadButtonDown(slot, b);
            if (isDown && down.Add(b))       // Add == true → newly pressed this frame
                Record(slot, now);
            else if (!isDown)
                down.Remove(b);
        }

        var engaged = _axisEngaged[slot];
        foreach (GamepadAxis ax in AllAxes)
        {
            float v = MathF.Abs(Raylib.GetGamepadAxisMovement(slot, ax));
            bool was = engaged.Contains(ax);
            // Hysteresis: engage at the threshold, release lower, so jitter near
            // the edge doesn't spam events.
            bool nowEngaged = was ? v >= AxisRelease : v >= AxisThreshold;

            if (nowEngaged && !was) { engaged.Add(ax); Record(slot, now); }
            else if (!nowEngaged && was) engaged.Remove(ax);
        }
    }

    private static void Record(int slot, double now) => _eventTimes[slot].Enqueue(now);

    private static void PruneWindow(int slot, double now)
    {
        var q = _eventTimes[slot];
        double cutoff = now - WindowSeconds;
        while (q.Count > 0 && q.Peek() < cutoff)
            q.Dequeue();
    }

    private static void ResetEdgeState(int slot)
    {
        _btnDown[slot].Clear();
        _axisEngaged[slot].Clear();
    }

    // Heuristic "least-virtual" score — higher means more likely a real, physical
    // gamepad. Based only on the device NAME and axis count (the only identifying
    // signals Raylib exposes); this is a guess, not authoritative identification.
    private static int EstimateRealness(int slot)
    {
        string name = (Raylib.GetGamepadName_(slot) ?? "").ToLowerInvariant();

        int score = 0;
        if (VirtualNameHints.Any(name.Contains)) score -= 100;
        if (RealNameHints.Any(name.Contains))    score += 100;

        // Real controllers have two sticks + triggers (≥4 axes); a lid sensor or
        // touchpad masquerading as a pad usually has 0–1.
        int axes = Raylib.GetGamepadAxisCount(slot);
        if (axes >= 4) score += 20;
        else if (axes <= 1) score -= 20;

        return score;
    }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>Whether a gamepad is currently connected and active.</summary>
    public static bool IsGamepadConnected => _active >= 0;

    /// <summary>Slot of the resolved active gamepad, or -1 if none.</summary>
    public static int ActiveGamepad => _active;

    /// <summary>Human-readable name of the active gamepad, or "" if none.</summary>
    public static string GamepadName =>
        _active >= 0 ? (Raylib.GetGamepadName_(_active) ?? "") : "";

    /// <summary>Slots currently reporting as available (up to four).</summary>
    public static IReadOnlyList<int> ConnectedGamepads
    {
        get
        {
            var list = new List<int>(MaxGamepads);
            for (int i = 0; i < MaxGamepads; i++)
                if (_available[i]) list.Add(i);
            return list;
        }
    }

    /// <summary>Events counted for a slot within the rolling window. Diagnostics/tools.</summary>
    public static int RecentEventCount(int slot) =>
        (uint)slot < MaxGamepads ? _eventTimes[slot].Count : 0;

    /// <summary>Name of a specific slot's device, or "" if that slot is empty.</summary>
    public static string GamepadNameFor(int slot) =>
        (uint)slot < MaxGamepads && _available[slot] ? (Raylib.GetGamepadName_(slot) ?? "") : "";

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

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void OnActiveGamepadChanged(int previous, int active)
    {
        if (active >= 0)
            Console.Error.WriteLine(
                $"[input] active gamepad → slot {active} \"{Raylib.GetGamepadName_(active)}\"");
        else if (previous >= 0)
            Console.Error.WriteLine("[input] no active gamepad; using keyboard/mouse");

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
    // to the executable or in the app-data folder. Missing file is not an error:
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
