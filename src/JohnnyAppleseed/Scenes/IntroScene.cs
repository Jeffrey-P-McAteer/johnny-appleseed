using Raylib_cs;
using System.Numerics;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Rendering;
using JohnnyAppleseed.Save;
using JohnnyAppleseed.Story;
using JohnnyAppleseed.UI;

namespace JohnnyAppleseed.Scenes;

/// <summary>
/// The clickable introduction to the early 1800s.
///
/// Narration is revealed with a typewriter effect inside a dialogue box. Every
/// input device advances the story identically:
///
///   • Confirm (Enter/Space, gamepad A), Right, ShortcutRight, or a left-click
///     anywhere → if still typing, finish the line instantly; otherwise turn to
///     the next page.
///   • Left / ShortcutLeft → step back a page (re-reads the previous beat).
///   • Cancel (Esc / gamepad B) → leave to the main menu; progress is kept.
///
/// Progress is written to the save file every time the page changes, so quitting
/// mid-intro and returning resumes on the exact page the player left off.
/// </summary>
sealed class IntroScene : IScene
{
    private readonly StoryPage[] _pages = IntroScript.Pages;
    private readonly Typewriter _typewriter = new();

    private SaveData _save = null!;
    private ParallaxBackground _bg = null!;

    private int _page;
    private float _blink;        // cursor / prompt blink accumulator
    private float _pageFade;     // 0→1 fade-in when a page opens

    // ── layout / palette ──────────────────────────────────────────────────────
    private const int HeadingFontSize = 30;
    private const int BodyFontSize     = 24;
    private const int HintFontSize     = 16;
    private const int BoxMargin        = 48;   // gap from screen edges
    private const int BoxPadding       = 28;   // interior padding
    private const int BoxHeight        = 240;
    private const float LineSpacing    = 1.35f;

    private static readonly Color ColBox      = new(8,   10,  22, 225);
    private static readonly Color ColBoxEdge  = new(255, 210, 80,  70);
    private static readonly Color ColHeading  = new(255, 210, 80, 255);
    private static readonly Color ColBody     = new(232, 228, 214, 255);
    private static readonly Color ColShadow   = new(0,   0,   0,  150);
    private static readonly Color ColHint     = new(150, 150, 180, 170);
    private static readonly Color ColVignette = new(0,   0,   0,  120);

    /// <param name="startPage">Page index to resume on (clamped to the script length).</param>
    public IntroScene(int startPage = 0)
    {
        _page = startPage;
    }

    public void Load()
    {
        _bg = new ParallaxBackground();
        _bg.Load();

        // Load-or-create the auto-save, then reconcile the starting page against
        // whatever the file actually holds (defends against a stale ctor value or
        // a script that shrank since the save was written).
        _save = SaveSystem.Load() ?? NewSave();
        _page = Math.Clamp(Math.Max(_page, _save.Story.IntroStep), 0, _pages.Length - 1);

        BeginPage(_page, persist: false);
    }

    public IScene? Update(float dt)
    {
        _bg.Update(dt);
        _typewriter.Update(dt);
        _blink += dt;
        if (_pageFade < 1f)
            _pageFade = MathF.Min(1f, _pageFade + dt * 3f);

        // Leaving the intro — progress is already saved on every page turn.
        if (InputSystem.IsPressed(InputAction.Cancel))
            return new MainMenuScene();

        // A left-click anywhere in the window counts as "continue", matching the
        // keyboard/gamepad confirm. (Menus need spatial hit-testing; a full-screen
        // story prompt does not.)
        bool advance =
            InputSystem.IsPressed(InputAction.Confirm)       ||
            InputSystem.IsPressed(InputAction.Right)         ||
            InputSystem.IsPressed(InputAction.ShortcutRight) ||
            Raylib.IsMouseButtonPressed(MouseButton.Left);

        bool back =
            InputSystem.IsPressed(InputAction.Left) ||
            InputSystem.IsPressed(InputAction.ShortcutLeft);

        if (advance)
        {
            if (!_typewriter.IsComplete)
                _typewriter.CompleteNow();      // first press finishes the line
            else
                return NextPage();              // second press turns the page
        }
        else if (back && _page > 0)
        {
            BeginPage(_page - 1, persist: true);
        }

        return null;
    }

    public void Draw()
    {
        Raylib.ClearBackground(new Color(3, 3, 12, 255));
        _bg.Draw();
        DrawVignette();
        DrawDialogueBox();
    }

    public void Unload()
    {
        _bg.Dispose();
    }

    // ── page flow ───────────────────────────────────────────────────────────

    private IScene? NextPage()
    {
        if (_page + 1 < _pages.Length)
        {
            BeginPage(_page + 1, persist: true);
            return null;
        }

        // Past the final page: mark the intro finished and hand back to the menu.
        // (A future GameplayScene would be returned here instead.)
        _save.Story.IntroComplete = true;
        _save.Story.Checkpoint = Checkpoint.Overworld;
        SaveSystem.Save(_save);
        return new MainMenuScene();
    }

    // Switch to a page: reset the typewriter/fade and (optionally) persist the step.
    private void BeginPage(int page, bool persist)
    {
        _page = page;
        _typewriter.SetText(_pages[page].Body);
        _pageFade = 0f;

        if (persist)
        {
            _save.Story.IntroStep = page;
            _save.Story.IntroComplete = false;
            SaveSystem.Save(_save);
        }
    }

    private static SaveData NewSave()
    {
        var data = new SaveData();
        SaveSystem.Save(data);   // establish the file immediately
        return data;
    }

    // ── drawing ───────────────────────────────────────────────────────────────

    private void DrawVignette()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        // Simple top+bottom darkening to seat the text over the busy parallax.
        Raylib.DrawRectangleGradientV(0, 0, sw, sh / 3, ColVignette, new Color(0, 0, 0, 0));
        Raylib.DrawRectangleGradientV(0, sh - sh / 3, sw, sh / 3, new Color(0, 0, 0, 0), ColVignette);
    }

    private void DrawDialogueBox()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        var box = new Rectangle(
            BoxMargin,
            sh - BoxHeight - BoxMargin,
            sw - BoxMargin * 2,
            BoxHeight);

        byte a = (byte)(255 * _pageFade);
        Color Fade(Color c) => new(c.R, c.G, c.B, (byte)(c.A * _pageFade));

        // panel
        Raylib.DrawRectangleRounded(box, 0.06f, 8, Fade(ColBox));
        Raylib.DrawRectangleRoundedLinesEx(box, 0.06f, 8, 1.5f, Fade(ColBoxEdge));

        float x = box.X + BoxPadding;
        float y = box.Y + BoxPadding;
        float textWidth = box.Width - BoxPadding * 2;

        StoryPage page = _pages[_page];

        // heading (optional)
        if (!string.IsNullOrEmpty(page.Heading))
        {
            Raylib.DrawText(page.Heading, (int)x + 1, (int)y + 1, HeadingFontSize, Fade(ColShadow));
            Raylib.DrawText(page.Heading, (int)x,     (int)y,     HeadingFontSize, Fade(ColHeading));
            y += HeadingFontSize + 14;
            Raylib.DrawLine((int)x, (int)y, (int)(x + textWidth), (int)y, Fade(ColBoxEdge));
            y += 16;
        }

        // body — wrap the revealed substring so the reveal survives window resizes
        string wrapped = TextWrap.Wrap(_typewriter.Visible, BodyFontSize, textWidth);
        DrawWrappedBody(wrapped, x, y, Fade(ColBody), Fade(ColShadow));

        // blinking continue prompt, once the line is fully typed
        if (_typewriter.IsComplete)
            DrawContinuePrompt(box);

        // page counter + controls hint
        DrawFooter(box, a);
    }

    private void DrawWrappedBody(string wrapped, float x, float y, Color body, Color shadow)
    {
        int lineHeight = (int)(BodyFontSize * LineSpacing);
        foreach (string line in wrapped.Split('\n'))
        {
            Raylib.DrawText(line, (int)x + 1, (int)y + 1, BodyFontSize, shadow);
            Raylib.DrawText(line, (int)x,     (int)y,     BodyFontSize, body);
            y += lineHeight;
        }
    }

    private void DrawContinuePrompt(Rectangle box)
    {
        // Blink at ~1.4 Hz.
        if (MathF.Sin(_blink * 9f) <= 0f)
            return;

        bool last = _page + 1 >= _pages.Length;
        string glyph = last ? "■" : "▼"; // ■ to end, ▼ to continue
        int size = 22;
        int gw = Raylib.MeasureText(glyph, size);
        Raylib.DrawText(glyph,
            (int)(box.X + box.Width - BoxPadding - gw),
            (int)(box.Y + box.Height - BoxPadding - size),
            size, ColHeading);
    }

    private void DrawFooter(Rectangle box, byte alpha)
    {
        // page counter, bottom-left of the box
        string counter = $"{_page + 1} / {_pages.Length}";
        Raylib.DrawText(counter,
            (int)(box.X + BoxPadding),
            (int)(box.Y + box.Height - BoxPadding - HintFontSize),
            HintFontSize, new Color(ColHint.R, ColHint.G, ColHint.B, alpha));

        // controls hint, centered below the box
        string hint = InputSystem.IsGamepadConnected
            ? "[ A ]  continue     [ B ]  menu     [ LB/RB ]  back / skip"
            : "[ Click / Enter / Space ]  continue     [ Esc ]  menu     [ ← → ]  back / skip";
        int hw = Raylib.MeasureText(hint, HintFontSize);
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        Raylib.DrawText(hint, sw / 2 - hw / 2, sh - 30, HintFontSize,
            new Color(ColHint.R, ColHint.G, ColHint.B, (byte)(alpha * 0.9f)));
    }
}
