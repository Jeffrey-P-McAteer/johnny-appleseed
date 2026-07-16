using Raylib_cs;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Platform;
using JohnnyAppleseed.Scenes;

namespace JohnnyAppleseed;

static class Game
{
    // Checked each frame; set to true to quit cleanly.
    public static bool ShouldExit { get; set; }

    public static void Run()
    {
        // Log build stamp before anything else so it appears even on crash.
        Console.Error.WriteLine(
            $"[johnny-appleseed] {BuildInfo.Version} " +
            $"built {BuildInfo.BuildDate} " +
            $"on {BuildInfo.BuildHost} " +
            $"@ {BuildInfo.GitHash}");

        AppData.Initialize();

        // On Linux: detect display server and guide GLFW's backend selection
        // BEFORE InitWindow — once GLFW initialises the choice is locked in.
        if (OperatingSystem.IsLinux())
            LinuxDisplay.Configure();

        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);
        Raylib.InitWindow(1280, 720, "Johnny Appleseed");
        Raylib.SetWindowMinSize(640, 360);
        Raylib.SetTargetFPS(60);

        IScene scene = new MainMenuScene();
        scene.Load();

        while (!Raylib.WindowShouldClose() && !ShouldExit)
        {
            float dt = Raylib.GetFrameTime();

            // Must be first: captures axis edges before any scene logic runs.
            InputSystem.Update();

            IScene? next = scene.Update(dt);

            if (next is ExitScene)
            {
                ShouldExit = true;
                break;
            }

            if (next != null)
            {
                scene.Unload();
                scene = next;
                scene.Load();
            }

            Raylib.BeginDrawing();
            scene.Draw();
            Raylib.EndDrawing();
        }

        scene.Unload();
        Raylib.CloseWindow();
    }
}
