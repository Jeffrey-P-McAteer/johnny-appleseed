using System.Reflection;
using Raylib_cs;

namespace JohnnyAppleseed;

/// <summary>
/// Access to game assets embedded in the assembly.
///
/// Every file under the repo's <c>audio/</c> and <c>graphics/</c> folders is
/// compiled in as an <c>EmbeddedResource</c> (see JohnnyAppleseed.csproj), so the
/// game ships as a single self-contained file with no loose asset directory to
/// carry alongside it. Resources are addressed by a stable logical key that
/// mirrors the source path, e.g. <c>"graphics/icon.png"</c> or
/// <c>"audio/click.mp3"</c>.
///
/// Raylib loads from memory (the same idiom ParallaxBackground already uses for
/// its shader): bytes → <c>Load*FromMemory</c>. Textures are cached and released
/// together via <see cref="UnloadAll"/>.
/// </summary>
static class Assets
{
    private static readonly Assembly Asm = typeof(Assets).Assembly;
    private static readonly Dictionary<string, Texture2D> _textures = new();
    private static readonly Dictionary<string, Sound> _sounds = new();

    /// <summary>True if an embedded resource with this logical key exists.</summary>
    public static bool Exists(string key) => Asm.GetManifestResourceInfo(key) is not null;

    /// <summary>Raw bytes of an embedded asset. Throws if the key is missing.</summary>
    public static byte[] Bytes(string key)
    {
        using Stream s = Asm.GetManifestResourceStream(key)
            ?? throw new FileNotFoundException($"Embedded asset not found: {key}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Decode an embedded image into a Raylib <see cref="Image"/> (CPU-side).
    /// Caller owns it — unload with <c>Raylib.UnloadImage</c>. The file extension
    /// in <paramref name="key"/> tells Raylib the format (".png", ".jpg", …).
    /// </summary>
    public static Image LoadImage(string key) =>
        Raylib.LoadImageFromMemory(Path.GetExtension(key), Bytes(key));

    /// <summary>
    /// A GPU texture for an embedded image, decoded on first use and cached.
    /// Do not unload the returned texture directly; call <see cref="UnloadAll"/>.
    /// </summary>
    public static Texture2D Texture(string key)
    {
        if (_textures.TryGetValue(key, out Texture2D cached))
            return cached;

        Image img = LoadImage(key);
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        _textures[key] = tex;
        return tex;
    }

    /// <summary>
    /// A playable sound for an embedded audio file, decoded on first use and
    /// cached. Requires the audio device to be initialised (Game.Run does this).
    /// The extension in <paramref name="key"/> tells Raylib the format (".mp3", …).
    /// Do not unload the returned sound directly; call <see cref="UnloadAll"/>.
    /// </summary>
    public static Sound Sound(string key)
    {
        if (_sounds.TryGetValue(key, out Sound cached))
            return cached;

        Wave wave = Raylib.LoadWaveFromMemory(Path.GetExtension(key), Bytes(key));
        Sound sound = Raylib.LoadSoundFromWave(wave);
        Raylib.UnloadWave(wave);
        _sounds[key] = sound;
        return sound;
    }

    /// <summary>
    /// Release every cached asset. Call after the scene unloads but while the
    /// window and audio device are still open (textures need the GL context,
    /// sounds need the audio device).
    /// </summary>
    public static void UnloadAll()
    {
        foreach (Texture2D tex in _textures.Values)
            Raylib.UnloadTexture(tex);
        _textures.Clear();

        foreach (Sound snd in _sounds.Values)
            Raylib.UnloadSound(snd);
        _sounds.Clear();
    }
}
