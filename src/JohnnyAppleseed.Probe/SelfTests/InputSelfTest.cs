using JohnnyAppleseed.Input;
using GC = JohnnyAppleseed.Input.InputSystem.GamepadCandidate;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Headless verification of the active-gamepad selection policy
/// (<see cref="InputSystem.SelectActiveGamepad"/>). The policy is pure — it takes
/// a snapshot of every slot (availability, recent event count, realness score) —
/// so the full "track four pads, pick the one being used, fall back to the least
/// virtual" behaviour is checked without any real hardware. Reaches the game's
/// internal input policy through InternalsVisibleTo.
///
/// Run via <c>uv run scripts/probe.py selftest input</c>. Exit code 0 = all passed.
/// </summary>
static class InputSelfTest
{
    public static int Run()
    {
        Console.WriteLine("• active-gamepad selection policy");
        int fails = 0;

        // slot, available, recentEvents, realness

        // Nothing connected → no active pad.
        fails += Check(Select(-1) == -1, "no pad when none available");

        // Single available pad is adopted regardless of slot number (a real pad
        // enumerating on a non-zero slot must still be found).
        fails += Check(Select(-1, C(2, true, 0, 100)) == 2, "adopt lone pad on slot 2");

        // THE touchpad case: a virtual device sits on slot 0 (low realness), the
        // real pad on slot 1 (high realness). With no input yet, least-virtual wins.
        fails += Check(
            Select(-1, C(0, true, 0, -100), C(1, true, 0, 120)) == 1,
            "idle: least-virtual pad (slot 1) beats touchpad (slot 0)");

        // Once the player uses the real pad, event count dominates — even though the
        // touchpad holds the lower slot.
        fails += Check(
            Select(0, C(0, true, 3, -100), C(1, true, 9, 120)) == 1,
            "in use: most-events pad wins over touchpad");

        // Most-events wins even against a higher realness score (events are primary).
        fails += Check(
            Select(-1, C(0, true, 10, 0), C(1, true, 2, 120)) == 0,
            "events outrank realness");

        // Tie on events → higher realness breaks it.
        fails += Check(
            Select(-1, C(0, true, 5, 10), C(1, true, 5, 90)) == 1,
            "event tie broken by realness");

        // Full tie (events + realness) → keep the current pad, no flip-flop.
        fails += Check(
            Select(1, C(0, true, 4, 50), C(1, true, 4, 50)) == 1,
            "full tie keeps current pad");

        // Active pad unplugged → re-scan picks the remaining one.
        fails += Check(
            Select(0, C(0, false, 99, 100), C(1, true, 0, 100)) == 1,
            "unplugged active pad drops out; survivor chosen");

        // All unplugged → fall back to keyboard/mouse.
        fails += Check(Select(2, C(2, false, 0, 100)) == -1, "all unplugged → -1");

        Console.WriteLine(fails == 0
            ? "\nINPUT SELF-TEST: ALL PASSED"
            : $"\nINPUT SELF-TEST: {fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    private static GC C(int slot, bool available, int events, int realness) =>
        new(slot, available, events, realness);

    private static int Select(int current, params GC[] candidates) =>
        InputSystem.SelectActiveGamepad(candidates, current);

    private static int Check(bool ok, string label)
    {
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}");
        return ok ? 0 : 1;
    }
}
