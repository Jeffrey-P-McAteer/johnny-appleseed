using System.Reflection;
using System.Runtime.InteropServices;
using Raylib_cs;
using JohnnyAppleseed.Rendering;

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
/// its shader): bytes -> <c>Load*FromMemory</c>. Textures are cached and released
/// together via <see cref="UnloadAll"/>.
/// </summary>
static class Assets
{
    private static readonly Assembly Asm = typeof(Assets).Assembly;
    private static readonly Dictionary<string, Texture2D> _textures = new();
    private static readonly Dictionary<string, Sound> _sounds = new();
    // Music streams decode lazily, so raylib keeps a pointer into the source
    // bytes - we pin them (GCHandle) for the stream's lifetime and free both in
    // UnloadAll. (Sounds, by contrast, are fully decoded up front, so their bytes
    // need no pinning.)
    private static readonly Dictionary<string, (Music music, GCHandle pin)> _music = new();
    private static readonly Dictionary<string, AnimatedTexture> _animated = new();

    /// <summary>
    /// Optional resolver for logical keys that are NOT embedded but live on disk - the
    /// AI asset cache installs one (see <see cref="Ai.AiAssets"/>) so a generated
    /// variant loads through the very same <see cref="Texture"/>/<see cref="Animated"/>
    /// path as an embedded asset. Returns an absolute file path it can serve, or null.
    /// A hook (rather than a hard reference) keeps the core loader decoupled and fully
    /// functional when the AI subsystem is absent or disabled.
    /// </summary>
    public static Func<string, string?>? DiskResolver;

    /// <summary>True if this logical key resolves to an embedded resource or a cached file on disk.</summary>
    public static bool Exists(string key) =>
        Asm.GetManifestResourceInfo(key) is not null ||
        (DiskResolver?.Invoke(key) is { } path && File.Exists(path));

    /// <summary>
    /// Every embedded key beginning with <paramref name="prefix"/> (ordinal), e.g.
    /// all members of an art set: <c>Keys("graphics/main-menu/backdrop")</c>. Used
    /// by <see cref="Ambient.ArtVariant"/> to enumerate variant candidates.
    /// </summary>
    public static IEnumerable<string> Keys(string prefix) =>
        Asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Raw bytes of an asset: an embedded resource if one exists, otherwise a cached
    /// file the <see cref="DiskResolver"/> maps this key to. Throws if neither is found.
    /// </summary>
    public static byte[] Bytes(string key)
    {
        Stream? s = Asm.GetManifestResourceStream(key);
        if (s is null)
        {
            if (DiskResolver?.Invoke(key) is { } path && File.Exists(path))
                return File.ReadAllBytes(path);
            throw new FileNotFoundException($"Asset not found (embedded or cached): {key}");
        }
        using (s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Decode an embedded image into a Raylib <see cref="Image"/> (CPU-side).
    /// Caller owns it - unload with <c>Raylib.UnloadImage</c>. The file extension
    /// in <paramref name="key"/> tells Raylib the format (".png", ".jpg", ...).
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
    /// A frame source for an embedded image, decoded on first use and cached.
    /// A <c>.gif</c> becomes an animated instance (advance it each frame); any other
    /// image (<c>.png</c>, <c>.jpg</c>, ...) becomes a single-frame instance. Callers
    /// draw <see cref="AnimatedTexture.Current"/> and never unload it directly; call
    /// <see cref="UnloadAll"/>.
    /// </summary>
    public static AnimatedTexture Animated(string key)
    {
        if (_animated.TryGetValue(key, out AnimatedTexture? cached))
            return cached;

        AnimatedTexture anim;
        if (string.Equals(Path.GetExtension(key), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            Image img = Raylib.LoadImageAnimFromMemory(".gif", Bytes(key), out int frames);
            anim = AnimatedTexture.Animated(img, frames);
        }
        else
        {
            anim = AnimatedTexture.Static(LoadImage(key));
        }

        _animated[key] = anim;
        return anim;
    }

    /// <summary>
    /// A playable sound for an embedded audio file, decoded on first use and
    /// cached. Requires the audio device to be initialised (Game.Run does this).
    /// The extension in <paramref name="key"/> tells Raylib the format (".mp3", ...).
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
    /// A streamed music track for an embedded audio file, opened on first use and
    /// cached. Unlike <see cref="Sound"/> (fully decoded, good for short SFX),
    /// music is streamed, so the caller must pump it every frame via
    /// <c>Raylib.UpdateMusicStream</c> - see <see cref="Audio.MusicManager"/>,
    /// which does this and handles looping/cross-fading. Requires the audio device
    /// to be initialised. Do not unload the returned stream directly; call
    /// <see cref="UnloadAll"/>.
    /// </summary>
    public static Music Music(string key)
    {
        if (_music.TryGetValue(key, out (Music music, GCHandle pin) cached))
            return cached.music;

        byte[] bytes = Bytes(key);
        // Pin so the GC can neither move nor collect the buffer while raylib
        // streams from it; freed in UnloadAll.
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Music music = Raylib.LoadMusicStreamFromMemory(Path.GetExtension(key), bytes);
        _music[key] = (music, pin);
        return music;
    }

    /// <summary>
    /// Release every cached asset. Call after the scene unloads but while the
    /// window and audio device are still open (textures need the GL context,
    /// sounds and music need the audio device).
    /// </summary>
    public static void UnloadAll()
    {
        foreach (Texture2D tex in _textures.Values)
            Raylib.UnloadTexture(tex);
        _textures.Clear();

        foreach (AnimatedTexture anim in _animated.Values)
            anim.Dispose();
        _animated.Clear();

        foreach (Sound snd in _sounds.Values)
            Raylib.UnloadSound(snd);
        _sounds.Clear();

        foreach ((Music music, GCHandle pin) in _music.Values)
        {
            Raylib.UnloadMusicStream(music);
            if (pin.IsAllocated) pin.Free();
        }
        _music.Clear();
    }
}
