using Raylib_cs;
using System.Numerics;
using JohnnyAppleseed.Ambient;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Rendering;
using JohnnyAppleseed.UI;

namespace JohnnyAppleseed.Scenes;

/// <summary>
/// The main menu. Buttons are authored (label + normalized position + action) in
/// <c>content/menu/main-menu.jsonc</c> and drawn over a backdrop whose edition is
/// chosen for the current weather/season by <see cref="ArtVariant"/> (and animated
/// if the chosen file is a GIF). Focus moves to the nearest button in the pressed
/// direction (<see cref="DirectionalNav"/>), so the layout can be freeform.
/// </summary>
sealed class MainMenuScene : IScene
{
    // Played when the highlighted item changes (keyboard, gamepad, or mouse hover).
    private const string FocusSoundKey = "audio/stone_rock_or_wood_moved_no_tick.mp3";

    // -- layout / style constants ----------------------------------------------
    private const int TitleFontSize = 64;
    private const int MenuFontSize   = 34;
    private const int PlatePadX      = 28;
    private const int PlatePadY      = 12;
    private const float GlowSpeed    = 12f;   // how fast the selection glow settles

    private static readonly Color ColTitle       = new(255, 210, 80,  255);
    private static readonly Color ColTextNormal   = new(220, 220, 220, 255);
    private static readonly Color ColTextSelected = new(255, 240, 120, 255);
    private static readonly Color ColPlateNormal   = new(8,  8,  14);   // alpha applied at draw
    private static readonly Color ColPlateSelected = new(40, 40, 58);
    private static readonly Color ColAccent        = new(255, 210, 80);

    private MenuLayoutDef _layout = MenuLayoutDatabase.Default();
    private AnimatedTexture? _backdrop;
    private float _backdropBrightness = 1f;   // 1.0 = as authored; nudged when no exact time-of-day edition
    private int _conditionsRevision = -1;

    private int      _selectedIndex;
    private bool     _isFullscreen;
    private float[]  _glow = Array.Empty<float>();

    public void Load()
    {
        _layout = MenuLayoutDatabase.Layout;
        _glow   = new float[_layout.Buttons.Length];

        ConditionsProvider.BeginRefresh();   // async; backdrop swaps in if it lands
        ResolveBackdrop();

        _isFullscreen = Raylib.IsWindowFullscreen();
        Assets.Sound(FocusSoundKey);          // pre-decode so the first move isn't silent
    }

    public IScene? Update(float dt)
    {
        _isFullscreen = Raylib.IsWindowFullscreen();

        // A late weather reading (or a refresh) re-picks the backdrop edition.
        if (ConditionsProvider.Revision != _conditionsRevision)
            ResolveBackdrop();
        _backdrop?.Advance(dt);

        int count = _layout.Buttons.Length;
        if (count == 0) return null;

        var rects = BuildRects();
        int previousIndex = _selectedIndex;

        // -- navigation: nearest button in the pressed direction ---------------
        if (InputSystem.IsPressed(InputAction.Up))    _selectedIndex = DirectionalNav.Next(rects, _selectedIndex, NavDirection.Up);
        if (InputSystem.IsPressed(InputAction.Down))  _selectedIndex = DirectionalNav.Next(rects, _selectedIndex, NavDirection.Down);
        if (InputSystem.IsPressed(InputAction.Left))  _selectedIndex = DirectionalNav.Next(rects, _selectedIndex, NavDirection.Left);
        if (InputSystem.IsPressed(InputAction.Right)) _selectedIndex = DirectionalNav.Next(rects, _selectedIndex, NavDirection.Right);

        // Bumper shortcuts still cycle in authoring order (quick-nav on keyboard too).
        if (InputSystem.IsPressed(InputAction.ShortcutLeft))  _selectedIndex = (_selectedIndex - 1 + count) % count;
        if (InputSystem.IsPressed(InputAction.ShortcutRight)) _selectedIndex = (_selectedIndex + 1) % count;

        // Mouse hover.
        var mouse = Raylib.GetMousePosition();
        for (int i = 0; i < count; i++)
            if (RectContains(rects[i], mouse)) _selectedIndex = i;

        // -- activation --------------------------------------------------------
        bool activate =
            InputSystem.IsPressed(InputAction.Confirm) ||
            (Raylib.IsMouseButtonPressed(MouseButton.Left) && RectContains(rects[_selectedIndex], mouse));

        if (activate)
            return Activate(_layout.Buttons[_selectedIndex].ActionKind);

        // Cancel / B -> highlight the leave button without quitting (UX convention).
        if (InputSystem.IsPressed(InputAction.Cancel))
            _selectedIndex = LeaveIndex(count);

        if (_selectedIndex != previousIndex)
            Raylib.PlaySound(Assets.Sound(FocusSoundKey));

        // Ease each button's glow toward its focus state.
        float k = MathF.Min(1f, dt * GlowSpeed);
        for (int i = 0; i < _glow.Length; i++)
            _glow[i] += ((i == _selectedIndex ? 1f : 0f) - _glow[i]) * k;

        return null;
    }

    public void Draw()
    {
        Raylib.ClearBackground(new Color(3, 3, 12, 255));
        DrawBackdrop();
        DrawTitle();

        var rects = BuildRects();
        for (int i = 0; i < _layout.Buttons.Length; i++)
            DrawButton(_layout.Buttons[i], rects[i], _glow.Length > i ? _glow[i] : 0f);

        DrawHint();
    }

    public void Unload()
    {
        // Backdrop textures are owned/cached by Assets (released on shutdown).
    }

    // -- backdrop --------------------------------------------------------------

    private void ResolveBackdrop()
    {
        _conditionsRevision = ConditionsProvider.Revision;
        Conditions conditions = ConditionsProvider.Current;

        string setKey = _layout.Backdrop;
        ArtVariant.Selection? pick = ArtVariant.Resolve(setKey, conditions, Assets.Keys(setKey));
        if (pick is null)
        {
            _backdrop = null;
            _backdropBrightness = 1f;
            return;
        }

        _backdropBrightness = pick.Value.Brightness;
        _backdrop = Assets.Animated(pick.Value.Key);
        Raylib.SetTextureFilter(_backdrop.Current, TextureFilter.Bilinear);  // smooth when scaled
    }

    // Scale-to-fill (centre-cropped), then a soft dark wash for text legibility.
    private void DrawBackdrop()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        if (_backdrop is not null)
        {
            Texture2D tex = _backdrop.Current;
            float scale = MathF.Max((float)sw / tex.Width, (float)sh / tex.Height);
            float dw = tex.Width * scale;
            float dh = tex.Height * scale;
            var src  = new Rectangle(0, 0, tex.Width, tex.Height);
            var dest = new Rectangle((sw - dw) / 2f, (sh - dh) / 2f, dw, dh);

            // Brightness nudge (the ArtVariant "nearest edition +/-15%" rule): darken
            // via a multiply tint (<=1); brighten via a faint additive second pass (>1).
            float b = _backdropBrightness;
            byte mul = (byte)(255 * Math.Clamp(b, 0f, 1f));
            Raylib.DrawTexturePro(tex, src, dest, Vector2.Zero, 0f, new Color(mul, mul, mul, (byte)255));

            if (b > 1f)
            {
                byte add = (byte)(255 * Math.Clamp(b - 1f, 0f, 1f));
                Raylib.BeginBlendMode(BlendMode.Additive);
                Raylib.DrawTexturePro(tex, src, dest, Vector2.Zero, 0f, new Color(add, add, add, (byte)255));
                Raylib.EndBlendMode();
            }
        }

        Raylib.DrawRectangle(0, 0, sw, sh, new Color(3, 3, 12, 110));
    }

    private static void DrawTitle()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        const string title = "JOHNNY APPLESEED";
        int tw = Raylib.MeasureText(title, TitleFontSize);
        int ty = sh / 8;
        Raylib.DrawText(title, sw / 2 - tw / 2 + 3, ty + 3, TitleFontSize, new Color(0, 0, 0, 140));
        Raylib.DrawText(title, sw / 2 - tw / 2,     ty,     TitleFontSize, ColTitle);
    }

    private void DrawButton(MenuButtonDef def, Rectangle rect, float glow)
    {
        int fontSize = def.FontSize > 0 ? def.FontSize : MenuFontSize;

        // Plate: dark/grey semi-transparent shadow that lightens and gains an
        // accent border as it gains focus.
        Color plate = LerpColor(ColPlateNormal, ColPlateSelected, glow);
        plate.A = (byte)(130 + 90 * glow);
        Raylib.DrawRectangleRounded(rect, 0.35f, 6, plate);

        var border = ColAccent;
        border.A = (byte)(220 * glow);
        Raylib.DrawRectangleRoundedLinesEx(rect, 0.35f, 6, 1.5f + glow, border);

        // Label (dynamic for PLAY/CONTINUE and FULLSCREEN/WINDOWED).
        string label = LabelFor(def);
        int lw = Raylib.MeasureText(label, fontSize);
        int lx = (int)(rect.X + rect.Width / 2f - lw / 2f);
        int ly = (int)(rect.Y + rect.Height / 2f - fontSize / 2f);
        Color text = LerpColor(ColTextNormal, ColTextSelected, glow);
        Raylib.DrawText(label, lx + 2, ly + 2, fontSize, new Color(0, 0, 0, 150));
        Raylib.DrawText(label, lx,     ly,     fontSize, text);
    }

    private void DrawHint()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        string hint = InputSystem.IsGamepadConnected
            ? "[ D-pad / L-stick ]  move    [ A ]  select    [ B ]  back"
            : "[ Arrows / WASD ]  move    [ Enter / Click ]  select    [ Esc ]  back";

        int hw = Raylib.MeasureText(hint, 14);
        Raylib.DrawText(hint, sw / 2 - hw / 2, sh - 34, 14, new Color(150, 150, 180, 160));
    }

    // -- geometry / helpers ----------------------------------------------------

    // Screen-space rectangle for each button, from its normalized center + label.
    private List<Rectangle> BuildRects()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        var rects = new List<Rectangle>(_layout.Buttons.Length);
        foreach (MenuButtonDef def in _layout.Buttons)
        {
            int fontSize = def.FontSize > 0 ? def.FontSize : MenuFontSize;
            int textW = Raylib.MeasureText(LabelFor(def), fontSize);
            float w = def.Width > 0 ? def.Width * sw : textW + PlatePadX * 2;
            float h = fontSize + PlatePadY * 2;
            float cx = def.X * sw;
            float cy = def.Y * sh;
            rects.Add(new Rectangle(cx - w / 2f, cy - h / 2f, w, h));
        }
        return rects;
    }

    private int LeaveIndex(int count)
    {
        for (int i = 0; i < count; i++)
            if (_layout.Buttons[i].ActionKind == MenuActionKind.Leave) return i;
        return _selectedIndex;   // no explicit leave button; leave focus where it is
    }

    private IScene? Activate(MenuActionKind kind) => kind switch
    {
        // CONTINUE resumes the saved story; StoryScene falls back to a fresh start
        // on its own when there is nothing to resume, so no special-casing here.
        MenuActionKind.Continue         => new StoryScene(),
        MenuActionKind.NewStory         => new StoryScene(fresh: true),
        MenuActionKind.Preferences      => new PreferencesScene(),
        MenuActionKind.ToggleFullscreen => ToggleFullscreen(),
        MenuActionKind.Leave            => ExitScene.Instance,
        _                               => null,
    };

    private IScene? ToggleFullscreen()
    {
        Raylib.ToggleFullscreen();
        return null;
    }

    private string LabelFor(MenuButtonDef def) => def.ActionKind switch
    {
        MenuActionKind.ToggleFullscreen => _isFullscreen ? "WINDOWED" : "FULLSCREEN",
        _                               => def.Label,
    };

    private static bool RectContains(Rectangle r, Vector2 p) =>
        p.X >= r.X && p.X <= r.X + r.Width &&
        p.Y >= r.Y && p.Y <= r.Y + r.Height;

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)255);
    }
}
