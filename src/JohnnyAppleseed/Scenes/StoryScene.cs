using Raylib_cs;
using System.Numerics;
using JohnnyAppleseed.Audio;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Narrative;
using JohnnyAppleseed.Rendering;
using JohnnyAppleseed.Save;
using JohnnyAppleseed.UI;

namespace JohnnyAppleseed.Scenes;

/// <summary>
/// The generic, data-driven narrative scene. It runs an embedded ink story
/// (compiled at load), reveals each line with the typewriter in a dialogue box,
/// presents choices as a menu, and honours per-line presentation tags:
///
///   • <c># heading: …</c>  the dialogue-box title for that beat
///   • <c># bg: key</c>     a background image (else the parallax backdrop shows)
///   • <c># music: key</c>  a looping track (cross-faded via MusicManager)
///
/// The ink runtime state is saved on every beat, so quitting mid-story resumes on
/// the exact beat. Where the beat leads next (more story, a choice, or the end) is
/// decided by ink; the end hands off through <see cref="Director"/>.
/// </summary>
sealed class StoryScene : IScene
{
    public const string IntroStory = "story/chapters/00-intro/intro.ink";

    private readonly string _storyKey;

    private StoryRunner _runner = null!;
    private SaveData _save = null!;
    private readonly Typewriter _typewriter = new();
    private ParallaxBackground _bg = null!;

    // Presentation state, carried across beats until a tag changes it.
    private string _heading = "";
    private string? _bgImageKey;
    private string? _musicKey;

    // Choice mode.
    private bool _choosing;
    private int _choiceIndex;

    private float _blink;
    private float _pageFade;

    // ── layout / palette (matches IntroScene) ──────────────────────────────────
    private const int HeadingFontSize = 30;
    private const int BodyFontSize     = 24;
    private const int HintFontSize     = 16;
    private const int BoxMargin        = 48;
    private const int BoxPadding       = 28;
    private const int BoxHeight        = 240;
    private const float LineSpacing    = 1.35f;

    private static readonly Color ColBox      = new(8,   10,  22, 225);
    private static readonly Color ColBoxEdge  = new(255, 210, 80,  70);
    private static readonly Color ColHeading  = new(255, 210, 80, 255);
    private static readonly Color ColBody     = new(232, 228, 214, 255);
    private static readonly Color ColChoice   = new(220, 220, 220, 255);
    private static readonly Color ColChoiceSel = new(255, 240, 120, 255);
    private static readonly Color ColShadow   = new(0,   0,   0,  150);
    private static readonly Color ColHint     = new(150, 150, 180, 170);
    private static readonly Color ColVignette = new(0,   0,   0,  120);

    public StoryScene(string storyKey = IntroStory) => _storyKey = storyKey;

    public void Load()
    {
        _bg = new ParallaxBackground();
        _bg.Load();

        _save = SaveSystem.Load() ?? NewSave();

        string source = System.Text.Encoding.UTF8.GetString(Assets.Bytes(_storyKey));
        _runner = new StoryRunner(source);

        bool resumed = false;
        if (!string.IsNullOrEmpty(_save.World.InkState) && !_save.Story.IntroComplete)
        {
            try { _runner.LoadState(_save.World.InkState!); resumed = true; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[story] resume failed, restarting: {ex.Message}");
            }
        }

        if (resumed) ShowCurrent();
        else Advance();
    }

    public IScene? Update(float dt)
    {
        _bg.Update(dt);
        _typewriter.Update(dt);
        _blink += dt;
        if (_pageFade < 1f)
            _pageFade = MathF.Min(1f, _pageFade + dt * 3f);

        // Leave to the menu; progress is already saved on every beat.
        if (InputSystem.IsPressed(InputAction.Cancel))
            return new MainMenuScene();

        return _choosing ? UpdateChoosing() : UpdateNarrating();
    }

    public void Draw()
    {
        Raylib.ClearBackground(new Color(3, 3, 12, 255));
        DrawBackground();
        DrawVignette();
        DrawBox();
    }

    public void Unload() => _bg.Dispose();

    // ── flow ────────────────────────────────────────────────────────────────────

    private IScene? UpdateNarrating()
    {
        bool advance =
            InputSystem.IsPressed(InputAction.Confirm)       ||
            InputSystem.IsPressed(InputAction.Right)         ||
            InputSystem.IsPressed(InputAction.ShortcutRight) ||
            Raylib.IsMouseButtonPressed(MouseButton.Left);

        if (advance)
        {
            if (!_typewriter.IsComplete)
                _typewriter.CompleteNow();     // first press finishes the line
            else
                return Advance();               // second press → next beat
        }
        return null;
    }

    private IScene? UpdateChoosing()
    {
        int count = _runner.ChoiceCount;
        if (count == 0)
            return Advance();

        if (InputSystem.IsPressed(InputAction.Up))
            _choiceIndex = (_choiceIndex - 1 + count) % count;
        if (InputSystem.IsPressed(InputAction.Down))
            _choiceIndex = (_choiceIndex + 1) % count;

        Vector2 mouse = Raylib.GetMousePosition();
        for (int i = 0; i < count; i++)
            if (RectContains(ChoiceRect(i), mouse))
                _choiceIndex = i;

        bool confirm =
            InputSystem.IsPressed(InputAction.Confirm) ||
            (Raylib.IsMouseButtonPressed(MouseButton.Left) &&
             RectContains(ChoiceRect(_choiceIndex), mouse));

        if (confirm)
        {
            _runner.Choose(_choiceIndex);
            _choosing = false;
            return Advance();
        }
        return null;
    }

    // Produce the next beat: skip empty lines, show narration, else offer choices,
    // else end the story through the Director.
    private IScene? Advance()
    {
        while (_runner.CanContinue)
        {
            string line = _runner.Continue(out IReadOnlyList<string> tags);
            ApplyTags(tags);
            if (line.Length == 0)
                continue;
            BeginBeat(line);
            Persist();
            return null;
        }

        if (_runner.ChoiceCount > 0)
        {
            _choosing = true;
            _choiceIndex = 0;
            Persist();
            return null;
        }

        return Director.OnStoryComplete(_save);
    }

    // Re-show the beat we were on when the game was last saved (no advancing).
    private void ShowCurrent()
    {
        ApplyTags(_runner.CurrentTags);
        if (_runner.ChoiceCount > 0 && !_runner.CanContinue)
        {
            _choosing = true;
            _choiceIndex = 0;
        }
        else
        {
            string line = _runner.CurrentText;
            BeginBeat(line.Length > 0 ? line : "…");
        }
    }

    private void BeginBeat(string line)
    {
        _choosing = false;
        _typewriter.SetText(line);
        _pageFade = 0f;
    }

    private void Persist()
    {
        _save.World.InkState = _runner.SaveState();
        _save.Story.IntroComplete = false;
        _save.Story.Checkpoint = Checkpoint.Intro;
        SaveSystem.Save(_save);
    }

    private void ApplyTags(IReadOnlyList<string> tags)
    {
        foreach (string tag in tags)
        {
            int c = tag.IndexOf(':');
            if (c < 0) continue;
            string key = tag[..c].Trim().ToLowerInvariant();
            string val = tag[(c + 1)..].Trim();
            switch (key)
            {
                case "heading": _heading = val; break;
                case "bg":      SetBackground(val); break;
                case "music":   PlayMusic(val); break;
            }
        }
    }

    private void SetBackground(string val)
    {
        string k = val.StartsWith("graphics/") ? val : "graphics/" + val;
        if (Assets.Exists(k)) _bgImageKey = k;
        else
        {
            _bgImageKey = null;
            Console.Error.WriteLine($"[story] bg not found: {k} (using parallax backdrop)");
        }
    }

    private void PlayMusic(string val)
    {
        string k = val.Contains('/') ? val : "audio/music/" + val;
        if (_musicKey == k) return;
        if (Assets.Exists(k)) { _musicKey = k; MusicManager.Play(k); }
        else Console.Error.WriteLine($"[story] music not found: {k} (skipped)");
    }

    private static SaveData NewSave()
    {
        var data = new SaveData();
        SaveSystem.Save(data);
        return data;
    }

    // ── drawing ─────────────────────────────────────────────────────────────────

    private void DrawBackground()
    {
        if (_bgImageKey is null)
        {
            _bg.Draw();
            return;
        }

        Texture2D tex = Assets.Texture(_bgImageKey);
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        float scale = MathF.Max((float)sw / tex.Width, (float)sh / tex.Height);
        float dw = tex.Width * scale, dh = tex.Height * scale;
        Raylib.DrawTexturePro(tex,
            new Rectangle(0, 0, tex.Width, tex.Height),
            new Rectangle((sw - dw) / 2f, (sh - dh) / 2f, dw, dh),
            Vector2.Zero, 0f, Color.White);
        Raylib.DrawRectangle(0, 0, sw, sh, new Color(3, 3, 12, 90));
    }

    private void DrawVignette()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        Raylib.DrawRectangleGradientV(0, 0, sw, sh / 3, ColVignette, new Color(0, 0, 0, 0));
        Raylib.DrawRectangleGradientV(0, sh - sh / 3, sw, sh / 3, new Color(0, 0, 0, 0), ColVignette);
    }

    private Rectangle BoxRect()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        return new Rectangle(BoxMargin, sh - BoxHeight - BoxMargin, sw - BoxMargin * 2, BoxHeight);
    }

    private void DrawBox()
    {
        Rectangle box = BoxRect();
        Color Fade(Color col) => new(col.R, col.G, col.B, (byte)(col.A * _pageFade));

        Raylib.DrawRectangleRounded(box, 0.06f, 8, Fade(ColBox));
        Raylib.DrawRectangleRoundedLinesEx(box, 0.06f, 8, 1.5f, Fade(ColBoxEdge));

        float x = box.X + BoxPadding;
        float y = box.Y + BoxPadding;
        float textWidth = box.Width - BoxPadding * 2;

        if (!string.IsNullOrEmpty(_heading))
        {
            Raylib.DrawText(_heading, (int)x + 1, (int)y + 1, HeadingFontSize, Fade(ColShadow));
            Raylib.DrawText(_heading, (int)x,     (int)y,     HeadingFontSize, Fade(ColHeading));
            y += HeadingFontSize + 14;
            Raylib.DrawLine((int)x, (int)y, (int)(x + textWidth), (int)y, Fade(ColBoxEdge));
            y += 16;
        }

        if (_choosing) DrawChoices(x, y, textWidth, Fade);
        else DrawNarration(box, x, y, textWidth, Fade);

        DrawHint(box, (byte)(255 * _pageFade));
    }

    private void DrawNarration(Rectangle box, float x, float y, float textWidth, Func<Color, Color> fade)
    {
        string wrapped = TextWrap.Wrap(_typewriter.Visible, BodyFontSize, textWidth);
        int lineHeight = (int)(BodyFontSize * LineSpacing);
        foreach (string line in wrapped.Split('\n'))
        {
            Raylib.DrawText(line, (int)x + 1, (int)y + 1, BodyFontSize, fade(ColShadow));
            Raylib.DrawText(line, (int)x,     (int)y,     BodyFontSize, fade(ColBody));
            y += lineHeight;
        }

        if (_typewriter.IsComplete && MathF.Sin(_blink * 9f) > 0f)
        {
            const string glyph = "▼";
            int gw = Raylib.MeasureText(glyph, 22);
            Raylib.DrawText(glyph,
                (int)(box.X + box.Width - BoxPadding - gw),
                (int)(box.Y + box.Height - BoxPadding - 22),
                22, fade(ColHeading));
        }
    }

    private void DrawChoices(float x, float y, float textWidth, Func<Color, Color> fade)
    {
        for (int i = 0; i < _runner.ChoiceCount; i++)
        {
            bool sel = i == _choiceIndex;
            Rectangle r = ChoiceRect(i);
            Color col = fade(sel ? ColChoiceSel : ColChoice);
            if (sel)
                Raylib.DrawText(">", (int)x - 4, (int)r.Y, BodyFontSize, col);
            string text = TextWrap.Wrap(_runner.ChoiceText(i), BodyFontSize, textWidth - 24);
            Raylib.DrawText(text.Split('\n')[0], (int)x + 20, (int)r.Y, BodyFontSize, col);
        }
    }

    // Stacked rows within the box body for choice hit-testing.
    private Rectangle ChoiceRect(int index)
    {
        Rectangle box = BoxRect();
        float top = box.Y + BoxPadding + (string.IsNullOrEmpty(_heading) ? 0 : HeadingFontSize + 30);
        int rowH = (int)(BodyFontSize * 1.5f);
        return new Rectangle(box.X + BoxPadding, top + index * rowH, box.Width - BoxPadding * 2, rowH);
    }

    private void DrawHint(Rectangle box, byte alpha)
    {
        string hint = _choosing
            ? (InputSystem.IsGamepadConnected
                ? "[ ↕ ]  choose     [ A ]  select     [ B ]  menu"
                : "[ ↕ ]  choose     [ Enter / Click ]  select     [ Esc ]  menu")
            : (InputSystem.IsGamepadConnected
                ? "[ A ]  continue     [ B ]  menu"
                : "[ Click / Enter / Space ]  continue     [ Esc ]  menu");
        int hw = Raylib.MeasureText(hint, HintFontSize);
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        Raylib.DrawText(hint, sw / 2 - hw / 2, sh - 30, HintFontSize,
            new Color(ColHint.R, ColHint.G, ColHint.B, (byte)(alpha * 0.9f)));
    }

    private static bool RectContains(Rectangle r, Vector2 p) =>
        p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height;
}
