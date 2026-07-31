using Raylib_cs;

namespace JohnnyAppleseed.UI;

/// <summary>
/// One row in the preferences table. Subclasses supply the value widget and how it
/// responds to input, so adding a new preference is just constructing one of these
/// (or a new subclass) and dropping it into a tab's row list.
///
///   * <see cref="ToggleControl"/>   - animated on/off check slider
///   * <see cref="SelectorControl"/> - cycle through a fixed set of options
///   * <see cref="ActionControl"/>   - run an action (open folder / URL, ...)
///   * <see cref="LabelControl"/>    - read-only value (build info, paths, ...)
///
/// The scene owns focus/glow; a control just draws its value in the supplied rect
/// and reacts to Confirm (<see cref="Activate"/>) / Left-Right (<see cref="Adjust"/>).
/// </summary>
abstract class PrefControl
{
    protected const int ValueFontSize = 22;

    public string Label { get; }
    protected PrefControl(string label) => Label = label;

    /// <summary>Read-only rows (labels) are skipped by navigation.</summary>
    public virtual bool Focusable => true;

    /// <summary>Confirm / left-click.</summary>
    public virtual void Activate() { }

    /// <summary>Left (-1) / Right (+1) adjustment.</summary>
    public virtual void Adjust(int dir) { }

    /// <summary>Draw the value widget within <paramref name="rect"/> (the row's right column).</summary>
    public abstract void DrawValue(Rectangle rect, float glow, float dt);

    protected static Color FocusTint(float glow) => UiPaint.Lerp(UiPaint.TextNormal, UiPaint.TextSelected, glow);
}

/// <summary>An animated on/off check slider bound to a bool get/set.</summary>
sealed class ToggleControl : PrefControl
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private float _knob;

    public ToggleControl(string label, Func<bool> get, Action<bool> set) : base(label)
    {
        _get = get; _set = set; _knob = get() ? 1f : 0f;
    }

    public override void Activate() => _set(!_get());
    public override void Adjust(int dir) { if (dir != 0) _set(dir > 0); }

    public override void DrawValue(Rectangle rect, float glow, float dt)
    {
        bool on = _get();
        _knob += ((on ? 1f : 0f) - _knob) * MathF.Min(1f, dt * 12f);

        const float pw = 54f, ph = 24f;
        float cy = rect.Y + rect.Height / 2f;
        float px = rect.X;
        var track = new Rectangle(px, cy - ph / 2f, pw, ph);

        Color off = new(70, 70, 84, 255);
        Color trackCol = UiPaint.Lerp(off, UiPaint.Accent, _knob);
        Raylib.DrawRectangleRounded(track, 1f, 8, UiPaint.WithAlpha(trackCol, 220));

        float r = ph / 2f - 3f;
        float knobX = px + ph / 2f + _knob * (pw - ph);
        Raylib.DrawCircle((int)knobX, (int)cy, r, new Color(245, 245, 245, 255));

        string text = on ? "ON" : "OFF";
        Raylib.DrawText(text, (int)(px + pw + 12), (int)(cy - 9), 18, FocusTint(glow));
    }
}

/// <summary>Cycle through a fixed list of options bound to an index get/set.</summary>
sealed class SelectorControl : PrefControl
{
    private readonly string[] _options;
    private readonly Func<int> _get;
    private readonly Action<int> _set;

    public SelectorControl(string label, string[] options, Func<int> get, Action<int> set) : base(label)
    {
        _options = options; _get = get; _set = set;
    }

    public override void Activate() => Cycle(+1);
    public override void Adjust(int dir) => Cycle(dir);

    private void Cycle(int d)
    {
        int n = _options.Length;
        if (n == 0 || d == 0) return;
        _set(((_get() + d) % n + n) % n);
    }

    public override void DrawValue(Rectangle rect, float glow, float dt)
    {
        int n = _options.Length;
        string value = n == 0 ? "" : _options[Math.Clamp(_get(), 0, n - 1)];
        int cy = (int)(rect.Y + rect.Height / 2f - ValueFontSize / 2f);

        Raylib.DrawText("<", (int)rect.X, cy, ValueFontSize, UiPaint.WithAlpha(UiPaint.Accent, 120 + 135 * glow));
        Raylib.DrawText(value, (int)rect.X + 24, cy, ValueFontSize, FocusTint(glow));
        int vw = Raylib.MeasureText(value, ValueFontSize);
        Raylib.DrawText(">", (int)rect.X + 24 + vw + 12, cy, ValueFontSize, UiPaint.WithAlpha(UiPaint.Accent, 120 + 135 * glow));
    }
}

/// <summary>A row that runs an action; optionally shows a current value (e.g. a path).</summary>
sealed class ActionControl : PrefControl
{
    private readonly Action _onActivate;
    private readonly Func<string>? _value;
    private readonly string _hint;

    public ActionControl(string label, Action onActivate, Func<string>? value = null, string hint = "Open")
        : base(label)
    {
        _onActivate = onActivate; _value = value; _hint = hint;
    }

    public override void Activate() => _onActivate();

    public override void DrawValue(Rectangle rect, float glow, float dt)
    {
        int cy = (int)(rect.Y + rect.Height / 2f - ValueFontSize / 2f);

        string button = $"[ {_hint} ]";
        int bw = Raylib.MeasureText(button, ValueFontSize);
        int bx = (int)(rect.X + rect.Width - bw);

        if (_value is not null)
        {
            string v = Ellipsize(_value(), (int)(rect.Width - bw - 16), 18);
            Raylib.DrawText(v, (int)rect.X, (int)(rect.Y + rect.Height / 2f - 9), 18, UiPaint.TextMuted);
        }

        Raylib.DrawText(button, bx, cy, ValueFontSize, FocusTint(glow));
    }

    // Trim from the front with a leading ellipsis so the tail (filename) stays visible.
    private static string Ellipsize(string s, int maxWidth, int fontSize)
    {
        if (maxWidth <= 0 || Raylib.MeasureText(s, fontSize) <= maxWidth) return s;
        while (s.Length > 1 && Raylib.MeasureText("..." + s, fontSize) > maxWidth)
            s = s.Substring(1);
        return "..." + s;
    }
}

/// <summary>A read-only value row (skipped by navigation).</summary>
sealed class LabelControl : PrefControl
{
    private readonly Func<string> _value;
    public LabelControl(string label, Func<string> value) : base(label) => _value = value;

    public override bool Focusable => false;

    public override void DrawValue(Rectangle rect, float glow, float dt)
    {
        Raylib.DrawText(_value(), (int)rect.X, (int)(rect.Y + rect.Height / 2f - 9), 18, UiPaint.TextMuted);
    }
}
