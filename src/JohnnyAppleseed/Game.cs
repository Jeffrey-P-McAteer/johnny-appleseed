using Raylib_cs;
using JohnnyAppleseed.Audio;
using JohnnyAppleseed.Input;
using JohnnyAppleseed.Platform;
using JohnnyAppleseed.Save;
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

        // Tell the user where their save/progress lives, so they can find (or back
        // up) it without digging through platform-specific app-data folders.
        Console.Error.WriteLine($"[johnny-appleseed] save data: {SaveSystem.SavePath}");

        // On Linux: detect display server and guide GLFW's backend selection
        // BEFORE InitWindow - once GLFW initialises the choice is locked in.
        if (OperatingSystem.IsLinux())
            LinuxDisplay.Configure();

        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);
        Raylib.InitWindow(1280, 720, "Johnny Appleseed");

        // If the graphics device couldn't come up (e.g. no compatible OpenGL 3.3
        // driver - common on VMs / remote desktops), surface it as a managed
        // exception so Program.Main can show the native startup-error dialog
        // instead of limping on into a blank or broken window. NOTE: this only
        // catches the case where raylib *returns* from InitWindow; some native
        // raylib builds abort() inside GLFW before we regain control (see readme).
        if (!Raylib.IsWindowReady())
            throw new InvalidOperationException(
                "The graphics device failed to initialize - the window or its " +
                "OpenGL 3.3 context could not be created. This usually means the " +
                "system has no compatible GPU/driver (common on virtual machines " +
                "and remote desktop sessions).");
        Raylib.SetWindowMinSize(640, 360);
        Raylib.SetTargetFPS(60);

        // Raylib defaults ESC to the window-close key, so WindowShouldClose() would
        // return true the instant a player pressed it - quitting the whole game even
        // though every scene advertises Esc as "back to the main menu" and handles
        // InputAction.Cancel itself. Disable the built-in exit key so Esc means only
        // what the scenes say it means; quitting goes through the menu's LEAVE button.
        Raylib.SetExitKey(KeyboardKey.Null);

        TrySetWindowIcon();

        // Audio device for sound effects / music (embedded assets loaded on demand).
        Raylib.InitAudioDevice();

        // Load gamepad mappings now that GLFW is up; controllers are resolved
        // dynamically each frame in InputSystem.Update (hot-plug aware).
        InputSystem.Initialize();

        IScene scene = new MainMenuScene();
        scene.Load();

        while (!Raylib.WindowShouldClose() && !ShouldExit)
        {
            float dt = Raylib.GetFrameTime();

            // Must be first: captures axis edges before any scene logic runs.
            InputSystem.Update();

            // Advance any streamed background music (loop/cross-fade). No-op until
            // a scene calls MusicManager.Play.
            MusicManager.Update(dt);

            // Let the ambient system notice time-of-day (lumen phase) transitions and
            // bump its revision so scenes re-pick day/evening/night art as time passes.
            Ambient.ConditionsProvider.Tick();

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
        Assets.UnloadAll();            // while GL context + audio device still live
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
    }

    // Set the window/taskbar icon from the embedded, build-generated PNG
    // (graphics/icon.xcf -> graphics/icon.png). Honoured on Windows and X11.
    // Cocoa (macOS) and native Wayland ignore per-window icons - the icon there
    // comes from the .app bundle (.icns) or a .desktop file - so skip them to
    // avoid a spurious GLFW warning, and treat the whole thing as best-effort.
    //
    // We hand GLFW SEVERAL concrete sizes via SetWindowIcons rather than the one
    // native 1024x1024 render. On Windows, GLFW maps these to ICON_SMALL (title
    // bar / Alt-Tab, ~16px) and ICON_BIG (taskbar button, ~32-48px); if it is
    // given only the oversized image, Windows must rescale a 1024px icon down for
    // the taskbar and renders it blank - the title-bar icon still appears, so the
    // symptom is "window has an icon but the taskbar doesn't". Supplying real
    // small sizes fixes the taskbar. GLFW copies the pixel data, so we free ours.
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static void TrySetWindowIcon()
    {
        if (OperatingSystem.IsMacOS())
            return;
        if (OperatingSystem.IsLinux() && LinuxDisplay.Detected == LinuxDisplay.Backend.WaylandNative)
            return;
        if (!Assets.Exists("graphics/icon.png"))
            return;

        Image src = Assets.LoadImage("graphics/icon.png");
        Raylib.ImageFormat(ref src, PixelFormat.UncompressedR8G8B8A8); // GLFW wants RGBA8

        var icons = new Image[IconSizes.Length];
        for (int i = 0; i < IconSizes.Length; i++)
        {
            Image scaled = Raylib.ImageCopy(src);
            Raylib.ImageResize(ref scaled, IconSizes[i], IconSizes[i]);
            icons[i] = scaled;
        }

        Raylib.SetWindowIcons(icons);

        foreach (Image ic in icons)
            Raylib.UnloadImage(ic);
        Raylib.UnloadImage(src);
    }
}
