# Johnny Appleseed

A 2D cross-platform game written in C# using [Raylib-cs](https://github.com/raylib-cs/raylib-cs) (Raylib 6.0).  The current build presents a main menu with a rotating parallax background.  Future development will expand on the scene system laid out here.

---

## Architecture

### Scene system

All game states (menus, gameplay, cutscenes, etc.) implement `IScene`:

```
IScene
├── Load()           called once when entering the scene
├── Update(dt) → IScene?   game logic; return next scene or null to stay
├── Draw()           render
└── Unload()         release scene-owned resources
```

`Game.cs` drives the loop.  Returning `ExitScene.Instance` from `Update` quits cleanly.  Adding a new game state means creating a new `IScene` class and returning it from whichever scene triggers the transition.

### Input system (`Input/`)

A single abstraction layer sits between hardware and game logic:

| Logical action | Keyboard | Gamepad |
|---|---|---|
| Up / Down / Left / Right | Arrow keys, WASD | D-pad, Left stick |
| Confirm | Enter, Space | A / South |
| Cancel | Escape | B / East |
| ShortcutLeft | Q, PageUp | **LB / L1** |
| ShortcutRight | E, PageDown | **RB / R1** |

Mouse clicks are handled directly in scenes — they need spatial context (the click position) that the input system cannot provide.

`InputSystem.Update()` is called once per frame before any scene logic.  Analog-stick directions fire exactly once per threshold crossing, preventing rapid-repeat navigation.

The **ShortcutLeft / ShortcutRight** actions (LB/RB bumpers, Q/E on keyboard) are reserved for single-key navigation to any menu that would otherwise require multiple arrow + Enter presses.  Wire them into new scenes as those menus are built.

#### Gamepad hot-plugging

Controllers can be connected or disconnected at any time — including after the
game has started — and are picked up automatically.  Rather than assume a fixed
slot, `InputSystem.Update()` resolves an *active gamepad* each frame by scanning
all slots: it keeps the current pad while it stays connected, re-scans when that
pad is unplugged, and adopts the first available pad otherwise.  This is why a
controller that enumerates on a non-zero slot (or is plugged in mid-game) works
identically to one present at launch — there is no hardcoded slot 0.  Connect and
disconnect events are logged to stderr.

If a controller is *detected but its buttons do nothing* (a common Linux case
where GLFW's built-in SDL mapping table predates the controller), drop an
up-to-date [`gamecontrollerdb.txt`](https://github.com/mdqinc/SDL_GameControllerDB)
next to the executable or in the app-data folder — `InputSystem.Initialize()`
loads it via `SetGamepadMappings()` at startup.  Missing file is not an error;
GLFW's built-in mappings already cover most mainstream controllers.

The hot-plug selection policy (`InputSystem.ResolveActiveGamepad`) is pure and
unit-tested headlessly:

```bash
dotnet run --project src/JohnnyAppleseed/JohnnyAppleseed.csproj -- --selftest-input
dotnet run --project src/JohnnyAppleseed/JohnnyAppleseed.csproj -- --selftest        # all suites
```

### Parallax background (`Rendering/`)

Four procedurally-generated texture layers (sky gradient, star noise, mountain silhouettes, tree-line) scroll and rotate at different speeds via a custom GLSL fragment shader.  No external image assets are needed.  Each layer is a `ParallaxLayer` data object; add more or swap textures to change the look without touching the shader.

### Intro story (`Scenes/IntroScene.cs`, `Story/`, `UI/`)

A clickable introduction to the early 1800s. Narration is revealed with a
typewriter effect (`UI/Typewriter.cs`) — characters appear one at a time with
longer pauses after sentence and clause punctuation — inside a dialogue box that
word-wraps the text (`UI/TextWrap.cs`). Every input device advances the story:

| Action | Keyboard | Mouse | Gamepad |
|---|---|---|---|
| Continue / finish line | Enter, Space, → | Left-click anywhere | A, →, RB |
| Back a page | ← | — | LB |
| Leave to menu | Esc | — | B |

The first continue press completes the current line instantly; the second turns
the page. The script itself is **placeholder template copy** in
`Story/IntroScript.cs` — a writer replaces the `Heading`/`Body` strings without
touching game code (add/remove/reorder `StoryPage` entries freely).

The intro **saves progress on every page turn**, so quitting mid-story and
returning resumes on the exact page left off (the main menu shows `CONTINUE`).

### Save system (`Save/`)

A single auto-save slot is stored as human-readable JSON in the app-data folder
(`savegame.json`). It is designed to grow without breaking old saves:

- **Versioned** — `formatVersion` drives `SaveSystem.Migrate()`; bump it only for
  changes that adding/removing optional fields can't express.
- **Forward-compatible** — unknown fields written by a newer build are preserved
  via `[JsonExtensionData]`, and a newer `formatVersion` is never downgraded.
- **Backward-compatible** — missing fields deserialize to defaults.
- **Safe writes** — atomic temp-file swap; a corrupt file is quarantined to
  `.bak` rather than crashing.

Serialization uses a `System.Text.Json` source-generated context
(`Save/SaveJsonContext.cs`) so it works under single-file/self-contained publish.

Verify the save/resume behaviour headlessly (no window):

```bash
dotnet run --project src/JohnnyAppleseed/JohnnyAppleseed.csproj -- --selftest-save
```

Capture a screenshot of a scene for visual checks:

```bash
dotnet run --project src/JohnnyAppleseed/JohnnyAppleseed.csproj -- --capture-intro 3 shot.png
dotnet run --project src/JohnnyAppleseed/JohnnyAppleseed.csproj -- --capture-menu 1 menu.png
```

### App data path (`AppData.cs`)

On first run the game creates a platform-appropriate folder for save data and preferences:

| Platform | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\JohnnyAppleseed` |
| macOS | `~/Library/Application Support/JohnnyAppleseed` |
| Linux | `$XDG_DATA_HOME/JohnnyAppleseed` (default `~/.local/share/JohnnyAppleseed`) |

---

## Building

### Requirements

- .NET 9 SDK — `dotnet --version`
- uv — `uv --version`
- X11 dev headers (Linux, for the arm64 cross-compile script only)

### Run locally

```bash
dotnet run --project src/JohnnyAppleseed/JohnnyAppleseed.csproj
```

### Package for distribution

```bash
uv run scripts/package.py                  # all platforms
uv run scripts/package.py windows-x64      # specific target
uv run scripts/package.py --skip-download  # skip arm64 lib download
```

Output lands in `dist/<target>/<target>.[ext]`.

---

## Platform support

| Target | Format | Native lib source |
|---|---|---|
| `windows-x64` | `.zip` containing `.exe` | Raylib-cs NuGet |
| `linux-x64` | self-contained binary | Raylib-cs NuGet |
| `macos-x64` | `.dmg` (HFS+ or ZIP-DMG) | Raylib-cs NuGet |
| `macos-arm64` | `.dmg` | Raylib-cs NuGet |
| `windows-arm64` | `.zip` | see below |
| `linux-arm64` | self-contained binary | see below |

### Why there is no linux-arm64 or windows-arm64 build out of the box

The .NET managed code cross-compiles to every target without issue.  The
problem is the **native Raylib shared library** (`libraylib.so` / `raylib.dll`).
Raylib-cs 8.0.0's NuGet package bundles pre-compiled native libs only for the
four targets listed above; linux-arm64 and win-arm64 are absent.

**Why these two specifically?**

Raylib on Linux requires a windowing backend.  The default desktop backend is
GLFW compiled with X11 support, which means the native library must link
against X11.  X11 is a *system* library — cross-compiling it from a Linux
x64 host to an arm64 target requires either arm64 X11 development headers or
a full arm64 sysroot.  Neither ships with a standard Linux install or with the
Raylib-cs NuGet package.

Windows arm64 (Snapdragon X / Surface Pro X) is a similarly niche build
surface: MSVC arm64 cross-compilation is only available on Windows, and the
MinGW/Zig-based alternative, while it works, has not been distributed through
official Raylib channels.

### Producing arm64 builds

Use the setup script, which downloads Zig (a self-contained cross-compiler)
and Raylib 6.0 source to `./build/` — nothing is installed system-wide:

```bash
# Build libraylib.so for linux-arm64 and raylib.dll for win-arm64
uv run scripts/setup-native-libs.py

# Build one target at a time
uv run scripts/setup-native-libs.py linux-arm64
uv run scripts/setup-native-libs.py win-arm64
```

After the script succeeds, re-run `package.py` — the `.csproj` conditionally
includes the built native libs for their respective RIDs.

**linux-arm64 note:** the script uses your host's X11 headers
(`/usr/include/X11/`) during cross-compilation.  X11 headers are
architecture-neutral C headers (no assembly, no hardware-specific sizes beyond
standard LP64 types), so the host copy works correctly for the arm64 target.
Install them if they are missing:

```bash
# Arch
sudo pacman -S libx11 libxrandr libxi libxinerama libxcursor

# Debian/Ubuntu
sudo apt install libx11-dev libxrandr-dev libxi-dev libxinerama-dev libxcursor-dev
```

**win-arm64 note:** Zig bundles the Windows SDK headers (gdi32, winmm,
opengl32), so no Windows installation is required on the Linux build host.
The resulting DLL uses the GNU ABI (MinGW-style); this is ABI-compatible with
the standard Windows calling convention for arm64 `cdecl` functions.

---

## macOS DMG

On macOS the packaging script uses `hdiutil` to produce a proper compressed
UDIF image with a custom Finder window (custom background, icon positions).

On Linux it falls back to a ZIP-format `.dmg` (standard HFS+ tools are not
available without root).  Install `genisoimage` for a real HFS+ image:

```bash
sudo apt install genisoimage    # Debian/Ubuntu
sudo pacman -S cdrtools         # Arch
```

---

## Extending the game

**Add a scene** — create a class in `Scenes/` that implements `IScene`.
Return an instance of it from another scene's `Update` to transition.

**Add an input action** — add a value to `InputAction` and map it in
`InputSystem.IsPressed` / `IsDown`.  All scenes pick up the new action
automatically.

**Add a parallax layer** — construct a `ParallaxLayer` in
`ParallaxBackground.Load()` and append it to `_layers`.

**Add a render primitive** — extend `Rendering/` with new classes that wrap
Raylib draw calls.  Raylib's API surface covers sprites, tilemaps, 3D meshes,
shaders, render textures, and audio.

---

## Dependencies

| Package | Version | License |
|---|---|---|
| Raylib-cs | 8.0.0 | zlib |
| Raylib (native) | 6.0 | zlib |
| .NET Runtime | 9.0 | MIT |
