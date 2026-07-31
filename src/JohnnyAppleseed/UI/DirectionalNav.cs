using System.Numerics;
using Raylib_cs;

namespace JohnnyAppleseed.UI;

/// <summary>A cardinal navigation intent from Up/Down/Left/Right input.</summary>
enum NavDirection { Up, Down, Left, Right }

/// <summary>
/// Spatial "move focus to the nearest button in that direction" navigation for
/// arbitrarily-placed menu buttons, so keyboard and controller D-pad/stick feel
/// natural over an artist's freeform layout (not just a vertical list).
///
/// From the focused rect's center, each other rect is projected onto the direction
/// vector; candidates that are behind (or exactly sideways) are discarded, and the
/// remaining one with the smallest weighted distance (distance along the axis + a
/// penalty for lateral offset) wins. To avoid dead ends in scattered layouts, the
/// search is TIERED:
///   1. prefer a candidate within a ~45-degree cone of the pressed direction
///      (feels precisely aligned), else
///   2. accept any candidate that lies at all in that direction (positive
///      projection) - so e.g. pressing Up from a bottom-left button still reaches a
///      button up and to the right rather than doing nothing.
/// Only when there is nothing whatsoever in the pressed half-plane does focus stay
/// put. Pure and deterministic - unit-tested in the probe (MenuNavSelfTest).
/// </summary>
static class DirectionalNav
{
    // How strongly to penalize lateral (off-axis) offset relative to on-axis
    // distance. >1 keeps movement feeling aligned with the pressed direction.
    private const float LateralBias = 2.0f;

    /// <summary>
    /// Index of the button to focus after pressing <paramref name="dir"/>, or
    /// <paramref name="current"/> unchanged if there is no button in that direction.
    /// </summary>
    public static int Next(IReadOnlyList<Rectangle> rects, int current, NavDirection dir)
    {
        if (rects.Count == 0 || (uint)current >= (uint)rects.Count)
            return current;

        Vector2 from = Center(rects[current]);
        Vector2 axis = Unit(dir);

        // Tier 1: nearest within the aligned cone.
        int inCone = Best(rects, current, from, axis, requireCone: true);
        if (inCone != current) return inCone;

        // Tier 2: nearest anywhere in the pressed direction (no cone constraint).
        return Best(rects, current, from, axis, requireCone: false);
    }

    // Lowest-cost candidate strictly in the pressed direction; `current` if none.
    private static int Best(IReadOnlyList<Rectangle> rects, int current,
                            Vector2 from, Vector2 axis, bool requireCone)
    {
        int   best     = current;
        float bestCost = float.MaxValue;

        for (int i = 0; i < rects.Count; i++)
        {
            if (i == current) continue;

            Vector2 v = Center(rects[i]) - from;
            float along = Vector2.Dot(v, axis);          // component in the pressed direction
            if (along <= 0.0001f) continue;              // behind or exactly sideways

            float lateral = MathF.Abs(Cross(v, axis));   // perpendicular distance (axis is unit)
            if (requireCone && lateral > along) continue; // outside the ~45-degree cone

            float cost = along + lateral * LateralBias;
            if (cost < bestCost) { bestCost = cost; best = i; }
        }

        return best;
    }

    private static Vector2 Center(Rectangle r) => new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

    private static Vector2 Unit(NavDirection d) => d switch
    {
        NavDirection.Up    => new Vector2(0, -1),
        NavDirection.Down  => new Vector2(0,  1),
        NavDirection.Left  => new Vector2(-1, 0),
        _                  => new Vector2( 1, 0),   // Right
    };

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
