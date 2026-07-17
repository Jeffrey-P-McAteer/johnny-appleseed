using Raylib_cs;
using System.Numerics;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Rendering;

namespace JohnnyAppleseed.Scenes;

sealed class MainMenuScene : IScene
{
    private ParallaxBackground _bg = null!;

    private int  _selectedIndex;
    private bool _isFullscreen;

    // ── layout constants ──────────────────────────────────────────────────────
    private const int TitleFontSize  = 60;
    private const int MenuFontSize   = 36;
    private const int MenuItemHeight = 56;
    private const int PanelPadX      = 60;
    private const int PanelPadY      = 40;

    private static readonly Color ColTitle    = new(255, 210, 80,  255);
    private static readonly Color ColNormal   = new(220, 220, 220, 255);
    private static readonly Color ColSelected = new(255, 240, 120, 255);
    private static readonly Color ColPanel    = new(5,   5,   20,  190);

    // Index constants kept in sync with _baseLabels
    private const int IdxPlay       = 0;
    private const int IdxFullscreen = 1;
    private const int IdxExit       = 2;

    private readonly string[] _baseLabels = ["PLAY", "FULLSCREEN", "EXIT"];

    public void Load()
    {
        _bg = new ParallaxBackground();
        _bg.Load();
        _isFullscreen = Raylib.IsWindowFullscreen();
    }

    public IScene? Update(float dt)
    {
        _bg.Update(dt);
        _isFullscreen = Raylib.IsWindowFullscreen();

        int count = _baseLabels.Length;

        // ── navigation ────────────────────────────────────────────────────────
        // Up / Down / Left stick / D-pad
        if (InputSystem.IsPressed(InputAction.Up)           ||
            InputSystem.IsPressed(InputAction.ShortcutLeft))
            _selectedIndex = (_selectedIndex - 1 + count) % count;

        if (InputSystem.IsPressed(InputAction.Down)          ||
            InputSystem.IsPressed(InputAction.ShortcutRight))
            _selectedIndex = (_selectedIndex + 1) % count;

        // Mouse hover
        var mouse = Raylib.GetMousePosition();
        for (int i = 0; i < count; i++)
        {
            if (RectContains(GetItemRect(i), mouse))
                _selectedIndex = i;
        }

        // ── activation ────────────────────────────────────────────────────────
        bool activate =
            InputSystem.IsPressed(InputAction.Confirm) ||
            (Raylib.IsMouseButtonPressed(MouseButton.Left) &&
             RectContains(GetItemRect(_selectedIndex), mouse));

        if (activate)
            return Activate(_selectedIndex);

        // Cancel / B → highlight Exit without quitting (common UX convention)
        if (InputSystem.IsPressed(InputAction.Cancel))
            _selectedIndex = IdxExit;

        return null;
    }

    public void Draw()
    {
        Raylib.ClearBackground(new Color(3, 3, 12, 255));
        _bg.Draw();
        DrawMenu();
    }

    public void Unload()
    {
        _bg.Dispose();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private IScene? Activate(int index) => index switch
    {
        IdxPlay       => null,   // future: return new GameplayScene()
        IdxFullscreen => ToggleFullscreen(),
        IdxExit       => ExitScene.Instance,
        _             => null,
    };

    private IScene? ToggleFullscreen()
    {
        Raylib.ToggleFullscreen();
        return null;
    }

    private string LabelFor(int i) => i switch
    {
        IdxFullscreen => _isFullscreen ? "WINDOWED" : "FULLSCREEN",
        _             => _baseLabels[i],
    };

    private static bool RectContains(Rectangle r, Vector2 p) =>
        p.X >= r.X && p.X <= r.X + r.Width &&
        p.Y >= r.Y && p.Y <= r.Y + r.Height;

    // ── drawing ───────────────────────────────────────────────────────────────

    private void DrawMenu()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        // title
        const string title = "JOHNNY APPLESEED";
        int tw = Raylib.MeasureText(title, TitleFontSize);
        int ty = sh / 6;
        Raylib.DrawText(title, sw / 2 - tw / 2 + 3, ty + 3, TitleFontSize, new Color(0, 0, 0, 120));
        Raylib.DrawText(title, sw / 2 - tw / 2,     ty,     TitleFontSize, ColTitle);

        int sepY = ty + TitleFontSize + 16;
        int sepW = Math.Min(tw + 40, sw - 80);
        Raylib.DrawLine(sw / 2 - sepW / 2, sepY, sw / 2 + sepW / 2, sepY, new Color(255, 210, 80, 80));

        // panel
        var panelRect = GetPanelRect();
        Raylib.DrawRectangleRounded(panelRect, 0.12f, 8, ColPanel);
        Raylib.DrawRectangleRoundedLinesEx(panelRect, 0.12f, 8, 1.5f, new Color(255, 210, 80, 60));

        // items
        for (int i = 0; i < _baseLabels.Length; i++)
        {
            bool   sel   = i == _selectedIndex;
            string label = LabelFor(i);
            Color  col   = sel ? ColSelected : ColNormal;
            var    rect  = GetItemRect(i);
            int    iy    = (int)(rect.Y + rect.Height / 2f);

            if (sel)
            {
                Raylib.DrawRectangleRounded(
                    new Rectangle(rect.X - 4, rect.Y + 2, rect.Width + 8, rect.Height - 4),
                    0.3f, 6, new Color(255, 210, 80, 30));
                Raylib.DrawText(">", (int)rect.X - 28, iy - MenuFontSize / 2, MenuFontSize, ColSelected);
            }

            Raylib.DrawText(label, (int)rect.X + 2, iy - MenuFontSize / 2 + 2, MenuFontSize, new Color(0, 0, 0, 100));
            Raylib.DrawText(label, (int)rect.X,     iy - MenuFontSize / 2,     MenuFontSize, col);
        }

        // hint — show gamepad status
        string hint = InputSystem.IsGamepadConnected
            ? "[ ↕ / D-pad / L-stick ]  navigate    [ A ]  select    [ LB/RB ]  quick-nav"
            : "[ ↕ / WASD ]  navigate    [ Enter ]  select    [ Q / E ]  quick-nav";

        int hw = Raylib.MeasureText(hint, 14);
        Raylib.DrawText(hint, sw / 2 - hw / 2, sh - 34, 14, new Color(150, 150, 180, 160));
    }

    private Rectangle GetPanelRect()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        int totalH = _baseLabels.Length * MenuItemHeight + PanelPadY * 2;
        int panelW  = 320 + PanelPadX * 2;
        return new Rectangle(sw / 2f - panelW / 2f, sh / 2f - totalH / 2f, panelW, totalH);
    }

    private Rectangle GetItemRect(int index)
    {
        var panel = GetPanelRect();
        return new Rectangle(
            panel.X + PanelPadX,
            panel.Y + PanelPadY + index * MenuItemHeight,
            panel.Width - PanelPadX * 2,
            MenuItemHeight);
    }
}
