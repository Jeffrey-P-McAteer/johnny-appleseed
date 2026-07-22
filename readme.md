# Johnny Appleseed

A 2D, cross-platform narrative game built in C# on [Raylib-cs](https://github.com/raylib-cs/raylib-cs) (Raylib 6.0).

Download Here [![Releases Download Page](https://img.shields.io/github/v/release/Jeffrey-P-McAteer/johnny-appleseed)](https://github.com/Jeffrey-P-McAteer/johnny-appleseed/releases)

[Landing Page](https://jeffrey-p-mcateer.github.io/johnny-appleseed)

---

## The game

Johnny Appleseed is a story-driven 2D game set in the American frontier of the
early 1800s. You step into the period through a clickable, typewriter-narrated
introduction and go on from there.

The project is early. What exists today and plays end-to-end:

- A **main menu** rendered over a period still-life painting, with keyboard,
  mouse, and gamepad navigation and focus sound effects.
- A **clickable intro story** that reveals narration one character at a time and
  turns pages on any input device.
- A **save system** that auto-saves your place in the intro, so `PLAY` becomes
  `CONTINUE` and drops you back exactly where you left off.

There is no free-roaming gameplay scene yet — the intro currently returns you to
the menu when it finishes. The rest of this document explains how the pieces fit
together and where to add the next ones. It assumes you know C# but have never
seen this codebase.

### Design principles worth knowing up front

- **Everything ships in one file.** The game is published as a single
  self-contained executable per platform. All art and audio are embedded
  *inside* that binary as resources — there is no loose `assets/` folder next to
  the game at runtime. This shapes where you put new asset files (see
  [Adding assets](#adding-assets)).
- **The game project is pure game logic.** All debug, measurement, and
  data-capture tooling lives in a *separate* project
  (`src/JohnnyAppleseed.Probe`), not in the game. The shipped binary has no debug
  flags or test modes.
- **Cross-platform from a Linux host.** You can build and package Windows, macOS,
  and Linux artifacts from a single Linux machine, using bundled toolchains (Zig,
  pure-Python image/DMG writers) rather than system installs.

---

## Building on Linux

### Requirements

- **.NET 9 SDK** — `dotnet --version` should report 9.x
- **[uv](https://docs.astral.sh/uv/)** — `uv --version`; runs the Python build
  scripts, which declare their own inline dependencies (nothing to `pip install`)
- **X11 development headers** — only needed for the `linux-arm64` cross-compile
  and the native-Wayland build; not required to run or package for `linux-x64`

### Run it

```bash
dotnet run --project src/JohnnyAppleseed/JohnnyAppleseed.csproj
```

A plain `dotnet build`/`dotnet run` (no runtime identifier) produces a
framework-dependent build — quick to iterate on, and referenceable by the probe
project. Passing a runtime identifier (as the packaging script does) switches on
single-file, self-contained publishing.

### Package for distribution

```bash
uv run scripts/package.py                  # every platform
uv run scripts/package.py linux-x64        # one target
uv run scripts/package.py --skip-download  # skip the arm64 native-lib download
```

### Where the built artifacts live

`dotnet build` output lands under `src/JohnnyAppleseed/bin/<Config>/net9.0/`.

`package.py` writes finished, shippable artifacts to `dist/<target>/`:

| Target | Artifact |
|---|---|
| `linux-x64` | `dist/linux-x64/linux-x64` — raw self-contained executable |
| `linux-arm64` | `dist/linux-arm64/linux-arm64` |
| `windows-x64` | `dist/windows-x64/windows-x64.zip` (contains the `.exe`) |
| `windows-arm64` | `dist/windows-arm64/windows-arm64.zip` |
| `macos-x64` | `dist/macos-x64/macos-x64.dmg` (`.app` bundle in an HFS+ image) |
| `macos-arm64` | `dist/macos-arm64/macos-arm64.dmg` |

Generated intermediates (the build-info stamp, rasterized icons) go to `obj/`,
which is gitignored — the build never creates new tracked folders for generated
files.

### About the two arm64 targets and Wayland

The managed C# cross-compiles to every platform cleanly. The catch is the
**native Raylib shared library**: Raylib-cs's NuGet package bundles pre-built
natives only for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`.

`scripts/setup-native-libs.py` fills the gaps without installing anything
system-wide — it downloads Zig and Raylib source into `./build/` and produces the
missing libraries:

```bash
uv run scripts/setup-native-libs.py linux-arm64   # libraylib.so for arm64
uv run scripts/setup-native-libs.py win-arm64      # raylib.dll for arm64
uv run scripts/setup-native-libs.py linux-wayland  # native-Wayland libraylib.so
```

When a native lib exists under `runtimes/<RID>/native/`, the `.csproj`
conditionally overrides the NuGet default for that platform; a clean checkout
with an empty `runtimes/` simply uses the NuGet libraries. After running the
setup script, re-run `package.py` to pick the new libs up.

The stock `linux-x64` native is **X11-only**, so on a Wayland session the game
runs through XWayland by default (safe everywhere). For native Wayland (better
HiDPI and latency), build with `setup-native-libs.py linux-wayland`, then
package. `Platform/LinuxDisplay.cs` detects the session and steers GLFW's backend
choice at startup, guided by a compile-time flag set when the Wayland lib is
present.

---

## Architecture

The whole game is a loop over **scenes**. `Game.cs` owns the window, the audio
device, the per-frame input pass, and a single "current scene." Each scene is
responsible for its own logic and rendering and tells the loop what comes next.

```
Program.cs   →  Game.Run()
                 ├─ InitWindow / InitAudioDevice / InputSystem.Initialize
                 └─ loop:  InputSystem.Update()
                           next = scene.Update(dt)   ── returns a scene, or null
                           scene.Draw()
                           (swap to `next` when non-null; ExitScene quits)
```

### Scenes (`Scenes/`)

Every game state — menu, cutscene, future gameplay — implements `IScene`:

```
IScene
├── Load()                    entering the scene: acquire resources
├── Update(dt) → IScene?      logic; return the next scene, or null to stay
├── Draw()                    render this frame
└── Unload()                  leaving the scene: release resources
```

Returning `ExitScene.Instance` from `Update` quits the game cleanly. Present
scenes: `MainMenuScene`, `IntroScene`.

### Input (`Input/`)

`InputSystem.Update()` runs once per frame *before* any scene logic and unifies
keyboard, mouse, and gamepad into logical `InputAction`s:

| Logical action | Keyboard | Gamepad |
|---|---|---|
| Up / Down / Left / Right | Arrows, WASD | D-pad, left stick |
| Confirm | Enter, Space | A / South |
| Cancel | Escape | B / East |
| ShortcutLeft | Q, PageUp | LB / L1 |
| ShortcutRight | E, PageDown | RB / R1 |

Analog-stick directions fire once per threshold crossing (no runaway repeat).
Mouse clicks are read directly in scenes, since they need the click position.

**Dynamic gamepad tracking:** controllers can connect or disconnect at any time.
Because operating systems routinely expose non-controllers as "gamepads"
(touchpads, lid sensors, virtual/`uinput` devices) on low slots, the system does
not trust slot order. It watches all four slots and counts real input *events*
per slot over a rolling 30-second window; the active pad is the one you're
actually using (most recent events), falling back to the least-virtual slot by
name/axis-count heuristics, then to the current pad for stability. An idle
touchpad never steals focus. The selection policy is a pure function, unit-tested
headlessly in the probe.

If a controller is detected but its buttons do nothing, drop an up-to-date
[`gamecontrollerdb.txt`](https://github.com/mdqinc/SDL_GameControllerDB) next to
the executable or in the app-data folder; `InputSystem.Initialize()` loads it via
`SetGamepadMappings()`.

### Rendering (`Rendering/`)

`ParallaxBackground.cs` is a self-contained, multi-layer scrolling/rotating
background driven by a custom GLSL fragment shader (used by `IntroScene`). Each
layer is a small `ParallaxLayer` data object with scroll/rotate/scale/tint
fields; add or swap layers to restyle without touching the shader. New rendering
primitives belong here.

### Story, UI, and the intro (`Story/`, `UI/`, `Scenes/IntroScene.cs`)

- `Story/IntroScript.cs` holds the narration as `StoryPage(Heading, Body)`
  records. It is **placeholder template copy** — a writer edits the strings and
  adds/removes/reorders pages without touching game code.
- `UI/Typewriter.cs` reveals text one character at a time with longer pauses
  after sentence and clause punctuation.
- `UI/TextWrap.cs` word-wraps to a pixel width (resize-safe).
- `IntroScene` composes these into a dialogue box; any input device advances,
  and it **saves on every page turn**.

### Save system (`Save/`)

A single auto-save slot, stored as human-readable JSON (`savegame.json`) in the
app-data folder. It is built to evolve without breaking old saves:

- **Versioned** — `formatVersion` drives `SaveSystem.Migrate()`.
- **Forward-compatible** — unknown fields from newer builds are preserved via
  `[JsonExtensionData]`; a newer `formatVersion` is never downgraded.
- **Backward-compatible** — missing fields deserialize to defaults.
- **Safe writes** — atomic temp-file swap; a corrupt file is quarantined to
  `.bak` rather than crashing.

Serialization uses a `System.Text.Json` source-generated context
(`Save/SaveJsonContext.cs`) so it works under single-file publish.

### App data path (`AppData.cs`)

Created on first run:

| Platform | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\JohnnyAppleseed` |
| macOS | `~/Library/Application Support/JohnnyAppleseed` |
| Linux | `$XDG_DATA_HOME/JohnnyAppleseed` (default `~/.local/share/JohnnyAppleseed`) |

---

## Where new things go

### Adding a scene

Create a class in `src/JohnnyAppleseed/Scenes/` implementing `IScene`, then return
an instance of it from another scene's `Update` to transition in. That is the
whole contract — the loop handles `Load`/`Draw`/`Unload` timing for you.

### Adding a level

There is no level system yet; the game currently flows menu → intro → menu. When
gameplay arrives, follow the established grain:

- A level is best modeled as **its own `IScene`** (e.g. a future
  `Scenes/OverworldScene.cs`) that loads level *data* and renders/updates it.
- Keep the **data** (tilemaps, spawn points, dialogue triggers) out of code as
  embedded asset files (see below), and load them through `Assets`. That keeps
  levels editable without recompiling and consistent with how art/audio already
  work. The save system already carries a `checkpoint` string in `story` for
  recording which level/checkpoint the player reached.

### Adding assets

All art and audio live at the **repo root**, not under `src/`:

- **`graphics/`** — images (the still-life `.jpg`, `icon.svg`, etc.)
- **`audio/`** — sounds and music (`.mp3`, `.wav`, ...)

The `.csproj` embeds *everything* under these two trees into the binary as
resources, keyed by their path (e.g. `graphics/foo.png`, `audio/bar.mp3`). Load
them at runtime through the `Assets` static class
(`src/JohnnyAppleseed/Assets/Assets.cs`):

```csharp
Texture2D tex   = Assets.Texture("graphics/foo.png");   // cached
Sound     click = Assets.Sound("audio/bar.mp3");        // cached
byte[]    raw   = Assets.Bytes("some/embedded/file");   // anything else
```

So **the only step to add an asset is dropping the file into `graphics/` or
`audio/`** — the build embeds it automatically and `Assets` can load it by key.
The same applies to future level-data files; put them under one of these trees
(or add a similarly-embedded tree) and read them with `Assets.Bytes`.

The window/app icon is a special case: it is *generated* from `graphics/icon.svg`
at build time (rasterized to PNG/ICO/ICNS into `obj/`), since Raylib can't load
SVG directly. Edit `icon.svg` to change the icon everywhere.

### Adding an input action

Add a value to the `InputAction` enum and map it in `InputSystem`. Every scene
picks up the new action automatically.

---

## The hardware probe (`src/JohnnyAppleseed.Probe`)

A separate, debug-only binary for measuring what real controllers and embedded
assets actually do. It references the game project and reuses its internals
(input layer, app-data paths, build stamp — exposed via `InternalsVisibleTo`),
swapping the game loop for measurement logic, so what it reports is exactly what
the game sees. It is deliberately **not** part of the packaging pipeline: build
it locally on the machine under test.

```bash
uv run scripts/probe.py                    # interactive: live gamepad state + edge log
uv run scripts/probe.py list               # gamepads (raylib) + input devices (Linux)
uv run scripts/probe.py raw                # raw kernel events from /dev/input/js0
uv run scripts/probe.py assets             # list assets embedded in the game binary
uv run scripts/probe.py capture menu 1 out.png   # headless screenshot of a scene
uv run scripts/probe.py selftest           # headless save + input self-tests
uv run scripts/probe.py -c Release         # choose build config (default: Debug)
```

The self-tests (save round-tripping/migration, gamepad selection policy) live
here rather than in the game, so the shipped binary stays pure game logic.
`raw` reads the Linux joystick device directly and needs read access to
`/dev/input/jsN` (add yourself to the `input` group if you hit a permission
error).

---

## Repository layout

```
src/JohnnyAppleseed/          the game (pure game logic)
  Program.cs                  entry point → Game.Run()
  Game.cs                     window, audio, input pass, scene loop
  AppData.cs                  per-platform app-data folder
  Scenes/  Input/  Rendering/  Story/  UI/  Save/  Platform/  Assets/
src/JohnnyAppleseed.Probe/    debug/measurement tooling (not shipped)
graphics/                     source images  → embedded into the binary
audio/                        source sounds  → embedded into the binary
scripts/                      uv-run Python: package / publish / native libs / probe / icons
build/                        downloaded toolchains + source + caches (gitignored)
dist/                         packaged, shippable artifacts
www/                          landing page
testbed/                      local VM helpers for manual cross-platform testing
```

---

## Dependencies

| Package | Version | License |
|---|---|---|
| Raylib-cs | 8.0.0 | zlib |
| Raylib (native) | 6.0 | zlib |
| .NET Runtime | 9.0 | MIT |
