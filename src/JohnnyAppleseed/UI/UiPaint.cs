using Raylib_cs;

namespace JohnnyAppleseed.UI;

/// <summary>Small shared drawing helpers for the menu / preferences UI.</summary>
static class UiPaint
{
    public static readonly Color Accent      = new(255, 210, 80, 255);
    public static readonly Color TextNormal   = new(220, 220, 220, 255);
    public static readonly Color TextSelected = new(255, 240, 120, 255);
    public static readonly Color TextMuted    = new(150, 150, 170, 255);

    /// <summary>Linear-interpolate between two colors (alpha forced opaque).</summary>
    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)255);
    }

    /// <summary>A color with a replaced alpha.</summary>
    public static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, (byte)Math.Clamp(a, 0f, 255f));
}
