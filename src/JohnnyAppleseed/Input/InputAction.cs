namespace JohnnyAppleseed.Input;

/// <summary>
/// Logical input actions, independent of physical device.
/// Scenes consume these instead of querying keys or buttons directly.
/// </summary>
enum InputAction
{
    // ── navigation ───────────────────────────────────────────────────────────
    Up,
    Down,
    Left,
    Right,

    // ── selection ────────────────────────────────────────────────────────────
    Confirm,    // Enter / Space / Gamepad A (South)
    Cancel,     // Escape / Gamepad B (East)

    // ── bumper shortcuts ─────────────────────────────────────────────────────
    // Mapped to LB / RB on a controller, Q / E on keyboard.
    // Used as single-key shortcuts to menus that would otherwise require
    // several arrow-key + Enter presses in keyboard-only mode.
    ShortcutLeft,
    ShortcutRight,
}
