namespace JohnnyAppleseed.Input;

/// <summary>
/// Headless verification of the hot-plug gamepad-selection policy
/// (<see cref="InputSystem.ResolveActiveGamepad"/>). Because the policy is pure —
/// availability is injected as a predicate — the full connect / disconnect /
/// slot-shift lifecycle can be checked without any real hardware.
///
/// Run via <c>JohnnyAppleseed --selftest-input</c>. Exit code 0 = all passed.
/// </summary>
static class InputSelfTest
{
    private const int Max = 4;

    public static int Run()
    {
        Console.WriteLine("• gamepad hot-plug resolution");
        int fails = 0;

        // Nothing connected → no active pad.
        fails += Check(Resolve(-1, none) == -1, "no pad when none available");

        // Plugged in after start (slot 0) → adopted.
        fails += Check(Resolve(-1, only(0)) == 0, "adopt pad hot-plugged at slot 0");

        // Controller enumerates on a non-zero slot (0 & 1 empty) → still found.
        // This is the case a hardcoded slot-0 lookup would have missed entirely.
        fails += Check(Resolve(-1, only(2)) == 2, "adopt pad enumerating on slot 2");

        // Active pad stays put while it remains available (no jitter between pads).
        fails += Check(Resolve(1, both(1, 3)) == 1, "keep active pad while available");

        // Active pad unplugged, another present → re-scan picks the survivor.
        fails += Check(Resolve(0, only(1)) == 1, "re-scan after active pad unplugged");

        // Everything unplugged → fall back to keyboard/mouse (-1).
        fails += Check(Resolve(2, none) == -1, "fall back to -1 when all unplugged");

        // Stale index beyond range is ignored, not trusted.
        fails += Check(Resolve(99, only(0)) == 0, "ignore out-of-range stale index");

        Console.WriteLine(fails == 0
            ? "\nINPUT SELF-TEST: ALL PASSED"
            : $"\nINPUT SELF-TEST: {fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    // ── availability predicates ───────────────────────────────────────────────
    private static readonly Func<int, bool> none = _ => false;
    private static Func<int, bool> only(int slot) => i => i == slot;
    private static Func<int, bool> both(int a, int b) => i => i == a || i == b;

    private static int Resolve(int current, Func<int, bool> avail) =>
        InputSystem.ResolveActiveGamepad(current, avail, Max);

    private static int Check(bool ok, string label)
    {
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}");
        return ok ? 0 : 1;
    }
}
