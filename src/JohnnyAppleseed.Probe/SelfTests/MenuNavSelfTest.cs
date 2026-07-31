using Raylib_cs;
using JohnnyAppleseed.UI;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Headless verification of the freeform-menu spatial navigation
/// (<see cref="DirectionalNav.Next"/>): from a focused button, Up/Down/Left/Right
/// must move to the nearest button in that direction, ignore off-cone candidates,
/// stay put when there is no neighbor, and break ties deterministically. Pure
/// geometry (Raylib.Rectangle is a plain struct), so no window is needed.
///
/// Run via <c>uv run scripts/probe.py selftest nav</c>. Exit code 0 = all passed.
/// </summary>
static class MenuNavSelfTest
{
    public static int Run()
    {
        Console.WriteLine("MENU-NAV SELF-TEST");
        int fails = 0;

        // A plus-shaped layout around a center button, plus a diagonal decoy.
        //   1 up            5 up-right (decoy)
        //   3 left  0 center  4 right
        //   2 down
        var rects = new List<Rectangle>
        {
            Btn(100, 100),   // 0 center
            Btn(100,  30),   // 1 up
            Btn(100, 170),   // 2 down
            Btn( 30, 100),   // 3 left
            Btn(170, 100),   // 4 right
            Btn(170,  30),   // 5 up-right diagonal (should lose to aligned neighbors)
        };

        fails += Eq(DirectionalNav.Next(rects, 0, NavDirection.Up),    1, "center -> up");
        fails += Eq(DirectionalNav.Next(rects, 0, NavDirection.Down),  2, "center -> down");
        fails += Eq(DirectionalNav.Next(rects, 0, NavDirection.Left),  3, "center -> left");
        fails += Eq(DirectionalNav.Next(rects, 0, NavDirection.Right), 4, "center -> right (aligned beats diagonal)");
        fails += Eq(DirectionalNav.Next(rects, 1, NavDirection.Down),  0, "top -> down lands on nearest (center, not bottom)");
        fails += Eq(DirectionalNav.Next(rects, 1, NavDirection.Up),    1, "top -> up has no neighbor, stays put");
        fails += Eq(DirectionalNav.Next(rects, 3, NavDirection.Right), 0, "left -> right lands on nearest (center)");
        fails += Eq(DirectionalNav.Next(rects, 4, NavDirection.Up),    5, "right -> up reaches the diagonal (only one above)");

        // Sideways-only candidate must be ignored (dead ahead required).
        var sideways = new List<Rectangle> { Btn(100, 100), Btn(200, 100) };
        fails += Eq(DirectionalNav.Next(sideways, 0, NavDirection.Up), 0, "purely sideways candidate ignored");

        // Deterministic tie: two equidistant candidates above -> lower index wins.
        var tie = new List<Rectangle> { Btn(100, 100), Btn(70, 30), Btn(130, 30) };
        fails += Eq(DirectionalNav.Next(tie, 0, NavDirection.Up), 1, "equidistant tie -> lower index");

        // Guards.
        fails += Eq(DirectionalNav.Next(new List<Rectangle>(), 0, NavDirection.Up), 0, "empty list -> current");
        fails += Eq(DirectionalNav.Next(rects, 99, NavDirection.Up), 99, "out-of-range current -> unchanged");

        Console.WriteLine(fails == 0
            ? "\nMENU-NAV SELF-TEST: ALL PASSED"
            : $"\nMENU-NAV SELF-TEST: {fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    // A 20x20 button centered on (cx, cy).
    private static Rectangle Btn(float cx, float cy) => new(cx - 10, cy - 10, 20, 20);

    private static int Eq(int actual, int expected, string label)
    {
        bool ok = actual == expected;
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}"
            + (ok ? "" : $"   (got {actual}, want {expected})"));
        return ok ? 0 : 1;
    }
}
