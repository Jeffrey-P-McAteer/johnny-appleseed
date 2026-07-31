using Raylib_cs;
using System.Numerics;
using JohnnyAppleseed.Ambient;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Platform;
using JohnnyAppleseed.UI;

namespace JohnnyAppleseed.Scenes;

/// <summary>
/// The preferences screen: a tabbed table of animated controls over a dark panel.
/// Adding a preference is a one-liner - construct a <see cref="PrefControl"/> and add
/// it to a tab's row list in <see cref="BuildTabs"/>; adding a whole category is one
/// more <see cref="Tab"/>. Navigation reuses the menu's spatial <see cref="DirectionalNav"/>
/// (Up/Down between rows), Left/Right adjusts the focused control, the bumpers switch
/// tabs, and Cancel returns to the menu.
/// </summary>
sealed class PreferencesScene : IScene
{
    private const string FocusSoundKey = "audio/stone_rock_or_wood_moved_no_tick.mp3";
    private const string WebsiteUrl    = "https://jeffrey-p-mcateer.github.io/johnny-appleseed/";

    private sealed record Tab(string Name, List<PrefControl> Rows);

    private readonly List<Tab> _tabs = new();
    private int _tab;
    private bool _onTabs;           // true when focus is on the tab bar (not a row)
    private int _focus;             // index into the current tab's focusable rows
    private float[] _glow = Array.Empty<float>();   // per-row, sized to the current tab
    private float _tabGlow;         // focus glow for the active tab header
    private bool _exit;             // set by the Return button; leaves next frame

    // -- layout -----------------------------------------------------------------
    private const int TitleFontSize = 48;
    private const int TabFontSize   = 24;
    private const int LabelFontSize = 22;
    private const int RowH          = 48;
    private const int Margin        = 60;
    private const float GlowSpeed   = 12f;

    private static readonly Color ColPanel    = new(6, 7, 16, 235);
    private static readonly Color ColPanelEdge = new(255, 210, 80, 60);
    private static readonly Color ColRowPlate  = new(40, 40, 58, 255);

    // Tab-header hit rects, refreshed each Draw for mouse clicks.
    private readonly List<Rectangle> _tabRects = new();

    public void Load()
    {
        BuildTabs();
        _tab = 0;
        RebuildGlow();
        _focus = 0;
        Assets.Sound(FocusSoundKey);
    }

    private void BuildTabs()
    {
        // -- General --
        var general = new List<PrefControl>
        {
            new ToggleControl("Fullscreen",
                get: () => Raylib.IsWindowFullscreen(),
                set: v => { if (v != Raylib.IsWindowFullscreen()) Raylib.ToggleFullscreen(); }),

            new ActionControl("Save data folder",
                onActivate: () => OpenExternal.OpenFolder(AppData.Path),
                value: () => AppData.Path,
                hint: "Open"),

            new ActionControl("Website",
                onActivate: () => OpenExternal.OpenUrl(WebsiteUrl),
                value: () => WebsiteUrl,
                hint: "Open"),

            new LabelControl("Version", () => $"{BuildInfo.Version}  ({BuildInfo.GitHash})"),
            new LabelControl("Built",   () => BuildInfo.BuildDate),
            new LabelControl("Host",    () => BuildInfo.BuildHost),
        };

        // -- Weather (demonstrates a second tab) --
        var weather = new List<PrefControl>
        {
            new ToggleControl("Live weather",
                get: () => ConditionsProvider.Enabled,
                set: v => ConditionsProvider.Enabled = v),

            new SelectorControl("Manual override", OverrideOptions,
                get: () => OverrideToIndex(ConditionsProvider.WeatherOverride),
                set: i => ConditionsProvider.WeatherOverride = IndexToOverride(i)),

            new LabelControl("Detected now", () =>
            {
                Conditions c = ConditionsProvider.Current;
                return $"{c.Weather}, {c.Season}";
            }),
        };

        _tabs.Clear();
        _tabs.Add(new Tab("General", general));
        _tabs.Add(new Tab("Weather", weather));

        // Every tab ends with an explicit Return button, so mouse users (and anyone
        // who doesn't know Esc / gamepad B) always have a visible way back.
        foreach (Tab t in _tabs)
            t.Rows.Add(new ActionControl("Return to menu", onActivate: () => _exit = true, hint: "Return"));
    }

    // Manual-override selector: index 0 = automatic, then one per Weather value.
    private static readonly string[] OverrideOptions = { "Auto", "Normal", "Sunny", "Rainy", "Snowy" };
    private static int OverrideToIndex(Weather? w) => w switch
    {
        null => 0, Weather.Normal => 1, Weather.Sunny => 2, Weather.Rainy => 3, Weather.Snowy => 4, _ => 0,
    };
    private static Weather? IndexToOverride(int i) => i switch
    {
        1 => Weather.Normal, 2 => Weather.Sunny, 3 => Weather.Rainy, 4 => Weather.Snowy, _ => (Weather?)null,
    };

    public IScene? Update(float dt)
    {
        // Cancel / B / Esc -> back to the menu.
        if (InputSystem.IsPressed(InputAction.Cancel))
            return new MainMenuScene();

        ComputeTabRects();   // keep hit rects current for this frame's mouse handling
        (bool prevOnTabs, int prevTab, int prevFocus) = (_onTabs, _tab, _focus);

        // Bumpers (LB/RB, Q/E) always switch tabs and park focus on the tab bar.
        if (InputSystem.IsPressed(InputAction.ShortcutLeft))  { SetActiveTab(_tab - 1); _onTabs = true; }
        if (InputSystem.IsPressed(InputAction.ShortcutRight)) { SetActiveTab(_tab + 1); _onTabs = true; }

        List<PrefControl> rows = _tabs[_tab].Rows;
        var (focusRects, focusRow) = FocusableRects();

        if (_onTabs) UpdateTabFocus(focusRects.Count);
        else         UpdateRowFocus(rows, focusRects, focusRow);

        // -- mouse ------------------------------------------------------------
        Vector2 mouse = Raylib.GetMousePosition();
        bool click = Raylib.IsMouseButtonPressed(MouseButton.Left);

        for (int i = 0; i < focusRects.Count; i++)                 // hover a row
            if (RectContains(focusRects[i], mouse)) { _onTabs = false; _focus = i; }
        if (click)                                                 // click a tab header
            for (int i = 0; i < _tabRects.Count; i++)
                if (RectContains(_tabRects[i], mouse)) { SetActiveTab(i); _onTabs = true; break; }
        if (click && !_onTabs && focusRects.Count > 0 &&
            RectContains(focusRects[Math.Clamp(_focus, 0, focusRects.Count - 1)], mouse))
            rows[focusRow[_focus]].Activate();

        // The Return button (or Esc / gamepad B) leaves to the main menu.
        if (_exit)
            return new MainMenuScene();

        if (_onTabs != prevOnTabs || _tab != prevTab || _focus != prevFocus)
            Raylib.PlaySound(Assets.Sound(FocusSoundKey));

        // Tab header glows when the tab bar holds focus; rows glow individually.
        _tabGlow += ((_onTabs ? 1f : 0f) - _tabGlow) * MathF.Min(1f, dt * GlowSpeed);
        AdvanceGlow(dt, _onTabs || focusRects.Count == 0 ? -1 : focusRow[_focus]);
        return null;
    }

    // Focus is on the tab bar: Left/Right switch tabs (highlight == selected, content
    // updates live); Down or Confirm drop into the tab's first option.
    private void UpdateTabFocus(int focusableRowCount)
    {
        if (InputSystem.IsPressed(InputAction.Left))  SetActiveTab(_tab - 1);
        if (InputSystem.IsPressed(InputAction.Right)) SetActiveTab(_tab + 1);

        if ((InputSystem.IsPressed(InputAction.Down) || InputSystem.IsPressed(InputAction.Confirm))
            && focusableRowCount > 0)
        {
            _onTabs = false;
            _focus = 0;
        }
    }

    // Focus is on an option row: Up/Down move between rows (Up off the top row jumps
    // to the tab bar); Left/Right adjust the control; Confirm activates it.
    private void UpdateRowFocus(List<PrefControl> rows, List<Rectangle> focusRects, List<int> focusRow)
    {
        if (focusRects.Count == 0) { _onTabs = true; return; }
        _focus = Math.Clamp(_focus, 0, focusRects.Count - 1);

        if (InputSystem.IsPressed(InputAction.Up))
        {
            int up = DirectionalNav.Next(focusRects, _focus, NavDirection.Up);
            if (up == _focus) _onTabs = true;   // nothing above in the table -> tabs
            else _focus = up;
        }
        if (InputSystem.IsPressed(InputAction.Down))
            _focus = DirectionalNav.Next(focusRects, _focus, NavDirection.Down);

        if (_onTabs) return;   // moved up onto the tab bar this frame

        PrefControl focused = rows[focusRow[_focus]];
        if (InputSystem.IsPressed(InputAction.Left))    focused.Adjust(-1);
        if (InputSystem.IsPressed(InputAction.Right))   focused.Adjust(+1);
        if (InputSystem.IsPressed(InputAction.Confirm)) focused.Activate();
    }

    private void SetActiveTab(int index)
    {
        int next = Math.Clamp(index, 0, _tabs.Count - 1);
        if (next == _tab) return;
        _tab = next;
        _focus = 0;
        RebuildGlow();
    }

    private void RebuildGlow() => _glow = new float[_tabs[_tab].Rows.Count];

    private void AdvanceGlow(float dt, int focusedRow)
    {
        float k = MathF.Min(1f, dt * GlowSpeed);
        for (int i = 0; i < _glow.Length; i++)
            _glow[i] += ((i == focusedRow ? 1f : 0f) - _glow[i]) * k;
    }

    public void Draw()
    {
        Raylib.ClearBackground(new Color(3, 3, 12, 255));
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        var panel = new Rectangle(Margin, Margin, sw - 2 * Margin, sh - 2 * Margin);
        Raylib.DrawRectangleRounded(panel, 0.03f, 8, ColPanel);
        Raylib.DrawRectangleRoundedLinesEx(panel, 0.03f, 8, 1.5f, ColPanelEdge);

        // Title.
        const string title = "PREFERENCES";
        int tw = Raylib.MeasureText(title, TitleFontSize);
        int titleY = (int)panel.Y + 24;
        Raylib.DrawText(title, sw / 2 - tw / 2, titleY, TitleFontSize, UiPaint.Accent);

        DrawTabs();

        // Rows.
        var rowRects = RowRects();
        List<PrefControl> rows = _tabs[_tab].Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            Rectangle r = rowRects[i];
            float glow = _glow.Length > i ? _glow[i] : 0f;

            if (rows[i].Focusable && glow > 0.01f)
            {
                Raylib.DrawRectangleRounded(r, 0.3f, 6, UiPaint.WithAlpha(ColRowPlate, 40 + 120 * glow));
                var edge = UiPaint.WithAlpha(UiPaint.Accent, 200 * glow);
                Raylib.DrawRectangleRoundedLinesEx(r, 0.3f, 6, 1f + glow, edge);
            }

            // Label (left column) + value widget (right column).
            Color labelCol = rows[i].Focusable ? UiPaint.Lerp(UiPaint.TextNormal, UiPaint.TextSelected, glow)
                                               : UiPaint.TextMuted;
            Raylib.DrawText(rows[i].Label, (int)r.X + 14, (int)(r.Y + r.Height / 2f - LabelFontSize / 2f),
                            LabelFontSize, labelCol);

            float labelColW = r.Width * 0.42f;
            var valueRect = new Rectangle(r.X + labelColW, r.Y, r.Width - labelColW - 14, r.Height);
            rows[i].DrawValue(valueRect, glow, Raylib.GetFrameTime());
        }

        // Hint (changes with focus zone).
        string hint = _onTabs
            ? (InputSystem.IsGamepadConnected
                ? "[ Left/Right ] switch tab    [ Down / A ] open    [ B ] back"
                : "[ Left/Right ] switch tab    [ Down / Enter ] open    [ Esc ] back")
            : (InputSystem.IsGamepadConnected
                ? "[ D-pad ] move    [ Left/Right / A ] change    [ Up ] tabs    [ LB/RB ] tabs    [ B ] back"
                : "[ Up/Down ] move    [ Left/Right / Enter ] change    [ Up ] tabs    [ Q/E ] tabs    [ Esc ] back");
        int hw = Raylib.MeasureText(hint, 16);
        Raylib.DrawText(hint, sw / 2 - hw / 2, (int)(panel.Y + panel.Height - 34), 16, UiPaint.TextMuted);
    }

    public void Unload() { }

    // -- geometry ---------------------------------------------------------------

    private void DrawTabs()
    {
        ComputeTabRects();
        for (int i = 0; i < _tabs.Count; i++)
        {
            Rectangle rect = _tabRects[i];
            bool active  = i == _tab;
            bool focused = _onTabs && active;

            if (focused)
            {
                Raylib.DrawRectangleRounded(rect, 0.4f, 6, UiPaint.WithAlpha(ColRowPlate, 60 + 140 * _tabGlow));
                Raylib.DrawRectangleRoundedLinesEx(rect, 0.4f, 6, 1f + _tabGlow, UiPaint.WithAlpha(UiPaint.Accent, 220 * _tabGlow));
            }
            else if (active)
            {
                Raylib.DrawRectangleRounded(rect, 0.4f, 6, UiPaint.WithAlpha(ColRowPlate, 160));
            }

            Color col = active ? UiPaint.TextSelected : UiPaint.TextMuted;
            Raylib.DrawText(_tabs[i].Name, (int)rect.X + 14, (int)rect.Y + 7, TabFontSize, col);
            if (active)
                Raylib.DrawRectangle((int)rect.X + 10, (int)(rect.Y + rect.Height - 3), (int)rect.Width - 20, 2, UiPaint.Accent);
        }
    }

    // Screen-space panel and tab-bar geometry, shared by input + drawing.
    private static Rectangle Panel()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        return new Rectangle(Margin, Margin, sw - 2 * Margin, sh - 2 * Margin);
    }

    private static int TabBarY() => (int)Panel().Y + 24 + TitleFontSize + 18;
    private static int TabBarHeight => TabFontSize + 14;

    // (Re)compute the tab-header hit rects into _tabRects.
    private void ComputeTabRects()
    {
        _tabRects.Clear();
        Rectangle panel = Panel();
        int y = TabBarY();
        int x = (int)panel.X + 40;
        for (int i = 0; i < _tabs.Count; i++)
        {
            int w = Raylib.MeasureText(_tabs[i].Name, TabFontSize) + 28;
            _tabRects.Add(new Rectangle(x, y, w, TabBarHeight));
            x += w + 10;
        }
    }

    // Rect for every row in the current tab (aligned to the row list).
    private List<Rectangle> RowRects()
    {
        Rectangle panel = Panel();
        int top = TabBarY() + TabBarHeight + 24;
        int tableX = (int)panel.X + 40;
        int tableW = (int)panel.Width - 80;

        var list = new List<Rectangle>();
        List<PrefControl> rows = _tabs[_tab].Rows;
        for (int i = 0; i < rows.Count; i++)
            list.Add(new Rectangle(tableX, top + i * RowH, tableW, RowH - 8));
        return list;
    }

    // Rects of just the focusable rows, plus their index back into the row list.
    private (List<Rectangle> rects, List<int> rowIndex) FocusableRects()
    {
        var all = RowRects();
        var rects = new List<Rectangle>();
        var rowIndex = new List<int>();
        List<PrefControl> rows = _tabs[_tab].Rows;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Focusable) { rects.Add(all[i]); rowIndex.Add(i); }
        return (rects, rowIndex);
    }

    private static bool RectContains(Rectangle r, Vector2 p) =>
        p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height;
}
