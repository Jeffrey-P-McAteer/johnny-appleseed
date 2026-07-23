using Raylib_cs;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Scenes;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Developer aid: renders a scene for a fixed amount of simulated time and writes
/// a PNG screenshot, then exits — no user interaction. Handy for eyeballing UI
/// (e.g. the intro typewriter mid-reveal) in a headless/CI context.
///
/// Invoked via <c>uv run scripts/probe.py capture [menu|intro] [seconds] [out.png]</c>.
/// Lives in the probe (not the game) so the shipped game stays just game logic.
/// </summary>
static class Capture
{
    public static int Run(string scene, float seconds, string outPath)
    {
        AppData.Initialize();
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        Raylib.InitWindow(1280, 720, "Johnny Appleseed — capture");
        Raylib.SetTargetFPS(60);
        Raylib.InitAudioDevice();   // scenes may load sounds on entry (e.g. menu SFX)
        InputSystem.Initialize();

        IScene s = scene switch
        {
            "menu"  => new MainMenuScene(),
            "story" => new StoryScene(),
            _        => new IntroScene(),
        };
        s.Load();

        const float dt = 1f / 60f;
        int frames = (int)MathF.Max(1, seconds * 60f);
        for (int i = 0; i < frames && !Raylib.WindowShouldClose(); i++)
        {
            InputSystem.Update();
            s.Update(dt);
            Raylib.BeginDrawing();
            s.Draw();
            Raylib.EndDrawing();
        }

        Raylib.TakeScreenshot(outPath);   // written relative to CWD
        s.Unload();
        Assets.UnloadAll();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();

        Console.WriteLine($"[capture] wrote {outPath} after {frames} frames of '{scene}'");
        return 0;
    }
}
