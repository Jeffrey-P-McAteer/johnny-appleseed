using Raylib_cs;

namespace JohnnyAppleseed.Input;

/// <summary>
/// Unified input layer: keyboard, gamepad, and (for spatial queries) mouse.
///
/// Call <see cref="Update"/> once at the top of each game-loop iteration before
/// any scene logic runs.  Scenes then query logical actions via
/// <see cref="IsPressed"/> / <see cref="IsDown"/> rather than polling Raylib
/// directly.
///
/// Mouse clicks are intentionally not mapped here — they carry spatial context
/// (position) that the scene needs to resolve anyway.  Scenes call
/// <c>Raylib.GetMousePosition()</c> and <c>IsMouseButtonPressed</c> directly
/// and use <see cref="IsPressed"/> for the Confirm action (keyboard/gamepad).
/// </summary>
static class InputSystem
{
    // ── constants ─────────────────────────────────────────────────────────────
    private const int   Gp             = 0;        // first gamepad slot
    private const float AxisThreshold  = 0.5f;     // dead-zone threshold

    // ── state ─────────────────────────────────────────────────────────────────
    private static float _axisLX, _axisLY;      // current frame
    private static float _pLX,    _pLY;         // previous frame (for edge detection)

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>Advance the input state; must be called once per frame.</summary>
    public static void Update()
    {
        _pLX = _axisLX;
        _pLY = _axisLY;

        if (Raylib.IsGamepadAvailable(Gp))
        {
            _axisLX = Raylib.GetGamepadAxisMovement(Gp, GamepadAxis.LeftX);
            _axisLY = Raylib.GetGamepadAxisMovement(Gp, GamepadAxis.LeftY);
        }
        else
        {
            _axisLX = _axisLY = 0f;
        }
    }

    /// <summary>
    /// True on the frame the action transitions from inactive to active.
    /// Analog-stick directions fire once per threshold crossing.
    /// </summary>
    public static bool IsPressed(InputAction action)
    {
        bool gp = Raylib.IsGamepadAvailable(Gp);

        return action switch
        {
            InputAction.Up =>
                Raylib.IsKeyPressed(KeyboardKey.Up)   ||
                Raylib.IsKeyPressed(KeyboardKey.W)    ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.LeftFaceUp))   ||
                AxisEdge(_axisLY, _pLY, negative: true),

            InputAction.Down =>
                Raylib.IsKeyPressed(KeyboardKey.Down) ||
                Raylib.IsKeyPressed(KeyboardKey.S)    ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.LeftFaceDown)) ||
                AxisEdge(_axisLY, _pLY, negative: false),

            InputAction.Left =>
                Raylib.IsKeyPressed(KeyboardKey.Left) ||
                Raylib.IsKeyPressed(KeyboardKey.A)    ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.LeftFaceLeft)) ||
                AxisEdge(_axisLX, _pLX, negative: true),

            InputAction.Right =>
                Raylib.IsKeyPressed(KeyboardKey.Right) ||
                Raylib.IsKeyPressed(KeyboardKey.D)     ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.LeftFaceRight)) ||
                AxisEdge(_axisLX, _pLX, negative: false),

            InputAction.Confirm =>
                Raylib.IsKeyPressed(KeyboardKey.Enter) ||
                Raylib.IsKeyPressed(KeyboardKey.Space) ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.RightFaceDown)),

            InputAction.Cancel =>
                Raylib.IsKeyPressed(KeyboardKey.Escape) ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.RightFaceRight)),

            // LB / L1  →  Q or PageUp on keyboard
            InputAction.ShortcutLeft =>
                Raylib.IsKeyPressed(KeyboardKey.Q)       ||
                Raylib.IsKeyPressed(KeyboardKey.PageUp)  ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.LeftTrigger1)),

            // RB / R1  →  E or PageDown on keyboard
            InputAction.ShortcutRight =>
                Raylib.IsKeyPressed(KeyboardKey.E)        ||
                Raylib.IsKeyPressed(KeyboardKey.PageDown) ||
                (gp && Raylib.IsGamepadButtonPressed(Gp, GamepadButton.RightTrigger1)),

            _ => false
        };
    }

    /// <summary>
    /// True while the action is continuously held.
    /// Useful for movement in gameplay; prefer <see cref="IsPressed"/> for menus.
    /// </summary>
    public static bool IsDown(InputAction action)
    {
        bool gp = Raylib.IsGamepadAvailable(Gp);

        return action switch
        {
            InputAction.Up =>
                Raylib.IsKeyDown(KeyboardKey.Up)   || Raylib.IsKeyDown(KeyboardKey.W) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.LeftFaceUp))   ||
                _axisLY < -AxisThreshold,

            InputAction.Down =>
                Raylib.IsKeyDown(KeyboardKey.Down) || Raylib.IsKeyDown(KeyboardKey.S) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.LeftFaceDown)) ||
                _axisLY > AxisThreshold,

            InputAction.Left =>
                Raylib.IsKeyDown(KeyboardKey.Left) || Raylib.IsKeyDown(KeyboardKey.A) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.LeftFaceLeft)) ||
                _axisLX < -AxisThreshold,

            InputAction.Right =>
                Raylib.IsKeyDown(KeyboardKey.Right) || Raylib.IsKeyDown(KeyboardKey.D) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.LeftFaceRight)) ||
                _axisLX > AxisThreshold,

            InputAction.Confirm =>
                Raylib.IsKeyDown(KeyboardKey.Enter) || Raylib.IsKeyDown(KeyboardKey.Space) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.RightFaceDown)),

            InputAction.Cancel =>
                Raylib.IsKeyDown(KeyboardKey.Escape) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.RightFaceRight)),

            InputAction.ShortcutLeft =>
                Raylib.IsKeyDown(KeyboardKey.Q) || Raylib.IsKeyDown(KeyboardKey.PageUp) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.LeftTrigger1)),

            InputAction.ShortcutRight =>
                Raylib.IsKeyDown(KeyboardKey.E) || Raylib.IsKeyDown(KeyboardKey.PageDown) ||
                (gp && Raylib.IsGamepadButtonDown(Gp, GamepadButton.RightTrigger1)),

            _ => false
        };
    }

    /// <summary>Whether at least one gamepad is connected.</summary>
    public static bool IsGamepadConnected => Raylib.IsGamepadAvailable(Gp);

    // ── helpers ───────────────────────────────────────────────────────────────

    // Detects a single-frame edge: axis just crossed the threshold this frame.
    // negative=true → crossing into < -threshold
    // negative=false → crossing into > +threshold
    private static bool AxisEdge(float curr, float prev, bool negative) =>
        negative
            ? curr < -AxisThreshold && prev >= -AxisThreshold
            : curr >  AxisThreshold && prev <=  AxisThreshold;
}
