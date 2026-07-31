using Raylib_cs;

namespace JohnnyAppleseed.Rendering;

/// <summary>
/// A frame source that presents static images and animated GIFs behind one API,
/// so callers can draw a backdrop without caring which it is: advance it every
/// frame with <see cref="Advance"/> and draw <see cref="Current"/>.
///
/// Animated GIFs are decoded with <c>LoadImageAnimFromMemory</c>, which returns a
/// single <see cref="Image"/> whose pixel buffer holds every frame back-to-back.
/// We upload one <see cref="Texture2D"/> and, each time the frame advances, re-point
/// it at the next frame's slice with <c>UpdateTexture</c> - the standard raylib GIF
/// idiom. The source image is therefore retained for the lifetime of an animated
/// instance (static instances free theirs immediately).
///
/// Owned/cached by <see cref="Assets"/>; released via <see cref="Dispose"/> from
/// <see cref="Assets.UnloadAll"/> while the GL context is still alive.
/// </summary>
sealed unsafe class AnimatedTexture : IDisposable
{
    // GIFs carry per-frame delays we don't read back here; this is a sane default
    // playback rate for the looping ambient backdrops we use them for.
    private const float DefaultFps = 12f;

    private readonly Texture2D _texture;
    private readonly Image     _image;       // retained only when animated
    private readonly bool      _retainImage;
    private readonly int       _frameCount;
    private readonly int       _frameSize;   // bytes per frame
    private readonly float     _frameDelay;  // seconds per frame

    private int   _frame;
    private float _accum;
    private bool  _disposed;

    private AnimatedTexture(Texture2D texture, Image image, bool retainImage,
                            int frameCount, int frameSize, float frameDelay)
    {
        _texture     = texture;
        _image       = image;
        _retainImage = retainImage;
        _frameCount  = frameCount;
        _frameSize   = frameSize;
        _frameDelay  = frameDelay;
    }

    /// <summary>The texture for the current frame. Do not unload directly.</summary>
    public Texture2D Current => _texture;

    public int  FrameCount => _frameCount;
    public bool IsAnimated => _frameCount > 1;

    /// <summary>Wrap a single still image (PNG/JPG). Frees the CPU image immediately.</summary>
    public static AnimatedTexture Static(Image image)
    {
        Texture2D tex = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);
        return new AnimatedTexture(tex, default, retainImage: false, frameCount: 1, frameSize: 0, frameDelay: 0f);
    }

    /// <summary>
    /// Wrap a decoded animated image (from <c>LoadImageAnimFromMemory</c>). The
    /// image is retained so frames can be streamed into the texture.
    /// </summary>
    public static AnimatedTexture Animated(Image image, int frameCount, float fps = DefaultFps)
    {
        if (frameCount <= 1)
            return Static(image);

        Texture2D tex = Raylib.LoadTextureFromImage(image);   // uploads frame 0
        int frameSize = Raylib.GetPixelDataSize(image.Width, image.Height, image.Format);
        float delay = fps > 0f ? 1f / fps : 1f / DefaultFps;
        return new AnimatedTexture(tex, image, retainImage: true, frameCount, frameSize, delay);
    }

    /// <summary>Advance playback by <paramref name="dt"/> seconds (no-op for stills).</summary>
    public void Advance(float dt)
    {
        if (_disposed || _frameCount <= 1) return;

        _accum += dt;
        while (_accum >= _frameDelay)
        {
            _accum -= _frameDelay;
            _frame = (_frame + 1) % _frameCount;
            // Re-point the texture at this frame's slice of the retained image data.
            Raylib.UpdateTexture(_texture, (byte*)_image.Data + (long)_frameSize * _frame);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Raylib.UnloadTexture(_texture);
        if (_retainImage) Raylib.UnloadImage(_image);
    }
}
