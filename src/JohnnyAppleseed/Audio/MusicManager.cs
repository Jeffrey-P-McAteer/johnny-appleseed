using Raylib_cs;

namespace JohnnyAppleseed.Audio;

/// <summary>
/// Background-music playback for the game: streamed tracks with looping, a smooth
/// cross-fade on change, and de-duplication so moving between scenes that share a
/// track never restarts it.
///
/// Streamed music must be advanced every frame, so <see cref="Update"/> is called
/// once per frame from the game loop (Game.Run). Tracks themselves are owned and
/// released by <see cref="Assets"/>; this manager only starts/stops/mixes them, so
/// it never unloads anything.
///
/// Phase 0: fully wired but dormant until a scene calls <see cref="Play"/>.
/// </summary>
static class MusicManager
{
    private struct Track
    {
        public string Key;
        public Music Music;
        public bool Active;
    }

    private static Track _cur;    // fading in, then steady
    private static Track _prev;   // fading out during a cross-fade
    private static float _fade;   // 0→1 progress of the current fade
    private static float _fadeDur;
    private static float _master = 1f;

    /// <summary>Overall music volume (0..1), applied on top of per-track fades.</summary>
    public static float MasterVolume
    {
        get => _master;
        set => _master = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Play the track at the embedded asset <paramref name="key"/> (e.g.
    /// "audio/music/frontier_theme.ogg"), cross-fading from whatever is playing.
    /// If that track is already current, this is a no-op — the music keeps playing
    /// seamlessly across the scene change.
    /// </summary>
    public static void Play(string key, bool loop = true, float fadeSeconds = 0.75f)
    {
        if (_cur.Active && _cur.Key == key)
            return;                                   // already current → don't restart

        // Only one outgoing track at a time; retire any older fade immediately.
        if (_prev.Active)
            Raylib.StopMusicStream(_prev.Music);

        _prev = _cur;                                 // current begins fading out

        Music music = Assets.Music(key);
        music.Looping = loop;
        Raylib.PlayMusicStream(music);
        Raylib.SetMusicVolume(music, 0f);             // fade in from silence
        _cur = new Track { Key = key, Music = music, Active = true };

        _fadeDur = MathF.Max(0.0001f, fadeSeconds);
        _fade = 0f;
    }

    /// <summary>Fade the current track out to silence and stop.</summary>
    public static void Stop(float fadeSeconds = 0.5f)
    {
        if (!_cur.Active)
            return;
        if (_prev.Active)
            Raylib.StopMusicStream(_prev.Music);

        _prev = _cur;
        _cur = default;
        _fadeDur = MathF.Max(0.0001f, fadeSeconds);
        _fade = 0f;
    }

    /// <summary>Pump the stream(s) and advance any in-progress cross-fade. Call once per frame.</summary>
    public static void Update(float dt)
    {
        if (_cur.Active) Raylib.UpdateMusicStream(_cur.Music);
        if (_prev.Active) Raylib.UpdateMusicStream(_prev.Music);

        if (!_cur.Active && !_prev.Active)
            return;

        _fade = MathF.Min(1f, _fade + dt / _fadeDur);

        if (_cur.Active) Raylib.SetMusicVolume(_cur.Music, _master * _fade);
        if (_prev.Active) Raylib.SetMusicVolume(_prev.Music, _master * (1f - _fade));

        if (_fade >= 1f && _prev.Active)
        {
            Raylib.StopMusicStream(_prev.Music);
            _prev = default;
        }
    }
}
