using Raylib_cs;
using System.Numerics;

namespace JohnnyAppleseed.Rendering;

sealed class ParallaxLayer
{
    public Texture2D Texture;
    public float ScrollSpeed;   // UV units per second (horizontal)
    public float RotateSpeed;   // radians per second
    public float Scale;         // texture tiling multiplier
    public Color Tint;

    // Accumulated state
    public float ScrollX;
    public float Rotation;

    public void Update(float dt)
    {
        ScrollX  += ScrollSpeed  * dt;
        Rotation += RotateSpeed * dt;
    }
}

sealed class ParallaxBackground : IDisposable
{
    // Fragment shader: rotate + tile a texture over a fullscreen quad.
    // fragTexCoord arrives 0→1; we centre, rotate, scale, translate, then
    // GL_REPEAT wrapping on the sampler handles seamless tiling.
    private const string FragSrc = @"
#version 330 core
in vec2 fragTexCoord;
in vec4 fragColor;
out vec4 finalColor;
uniform sampler2D texture0;
uniform float scrollX;
uniform float scrollY;
uniform float rotation;
uniform float scale;
void main() {
    vec2 uv = fragTexCoord - 0.5;
    float c = cos(rotation);
    float s = sin(rotation);
    uv = vec2(c * uv.x - s * uv.y,
              s * uv.x + c * uv.y);
    uv += 0.5;
    uv *= scale;
    uv += vec2(scrollX, scrollY);
    finalColor = texture(texture0, uv) * fragColor;
}";

    private Shader _shader;
    private int _locScrollX, _locScrollY, _locRotation, _locScale;
    private ParallaxLayer[] _layers = [];

    public void Load()
    {
        _shader    = Raylib.LoadShaderFromMemory(null, FragSrc);
        _locScrollX  = Raylib.GetShaderLocation(_shader, "scrollX");
        _locScrollY  = Raylib.GetShaderLocation(_shader, "scrollY");
        _locRotation = Raylib.GetShaderLocation(_shader, "rotation");
        _locScale    = Raylib.GetShaderLocation(_shader, "scale");

        _layers =
        [
            // Far sky: deep blue-to-purple vertical gradient, very slow drift
            MakeLayer(
                GenGradient(1024, 1024, 90,
                    new Color(5, 5, 25, 255), new Color(18, 5, 45, 255)),
                scrollSpeed: 0.004f, rotateSpeed: 0.0008f, scale: 1.2f,
                tint: Color.White),

            // Stars: sparse white noise on near-black, slow rotation
            MakeLayer(
                GenStars(1024, 1024, 0.025f),
                scrollSpeed: 0.008f, rotateSpeed: 0.002f, scale: 1.8f,
                tint: new Color(200, 200, 255, 200)),

            // Mid mountains: cellular Voronoi tinted dark blue-green
            MakeLayer(
                GenCellularTinted(512, 512, 64, new Color(8, 18, 30, 255)),
                scrollSpeed: 0.020f, rotateSpeed: 0.004f, scale: 2.5f,
                tint: new Color(80, 120, 160, 180)),

            // Near tree-line: finer cellular, very dark green
            MakeLayer(
                GenCellularTinted(256, 256, 28, new Color(4, 14, 4, 255)),
                scrollSpeed: 0.050f, rotateSpeed: 0.007f, scale: 3.5f,
                tint: new Color(40, 80, 40, 220)),
        ];
    }

    public void Update(float dt)
    {
        foreach (var layer in _layers)
            layer.Update(dt);
    }

    public void Draw()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();

        foreach (var layer in _layers)
        {
            Raylib.BeginShaderMode(_shader);
            Raylib.SetShaderValue(_shader, _locScrollX,  layer.ScrollX,  ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_shader, _locScrollY,  0f,              ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_shader, _locRotation, layer.Rotation,  ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_shader, _locScale,    layer.Scale,     ShaderUniformDataType.Float);

            Raylib.DrawTexturePro(
                layer.Texture,
                new Rectangle(0, 0, layer.Texture.Width, layer.Texture.Height),
                new Rectangle(0, 0, sw, sh),
                new Vector2(0, 0),
                0f,
                layer.Tint);

            Raylib.EndShaderMode();
        }
    }

    public void Dispose()
    {
        foreach (var layer in _layers)
            Raylib.UnloadTexture(layer.Texture);
        Raylib.UnloadShader(_shader);
        _layers = [];
    }

    // ── texture generators ────────────────────────────────────────────────────

    private static Texture2D GenGradient(int w, int h, int dirDeg, Color from, Color to)
    {
        Image img = Raylib.GenImageGradientLinear(w, h, dirDeg, from, to);
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
        return tex;
    }

    private static Texture2D GenStars(int w, int h, float density)
    {
        // GenImageWhiteNoise: each pixel white with probability `density`
        Image img = Raylib.GenImageWhiteNoise(w, h, density);
        // Multiply alpha to make it look like stars (white dots, transparent BG)
        Raylib.ImageAlphaMask(ref img, img);
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
        return tex;
    }

    private static Texture2D GenCellularTinted(int w, int h, int tile, Color tint)
    {
        Image img = Raylib.GenImageCellular(w, h, tile);
        Raylib.ImageColorTint(ref img, tint);
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
        return tex;
    }

    private static ParallaxLayer MakeLayer(Texture2D tex, float scrollSpeed,
        float rotateSpeed, float scale, Color tint) => new()
    {
        Texture     = tex,
        ScrollSpeed = scrollSpeed,
        RotateSpeed = rotateSpeed,
        Scale       = scale,
        Tint        = tint,
    };
}
