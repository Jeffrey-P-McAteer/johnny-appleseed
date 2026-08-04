using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OnnxStack.Core;
using OnnxStack.Core.Config;
using OnnxStack.StableDiffusion.Common;
using OnnxStack.StableDiffusion.Config;
using OnnxStack.StableDiffusion.Enums;
using OnnxStack.StableDiffusion.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// Neural image generator: SD-1.5-lineage Latent Consistency (Dreamshaper V7) via
/// OnnxStack + ONNX Runtime on the CPU. Ships in every build; the ~4 GB weights are
/// provisioned at runtime by <see cref="AiModels"/> from the folder passed to the ctor.
///
/// OnnxStack 0.7.0 is entirely config-driven (verified by reflecting the package):
///   * AddOnnxStackStableDiffusion() reads an "OnnxStackConfig" section from an
///     appsettings.json next to the binary (AppContext.BaseDirectory) and throws
///     without it - so we WRITE that file with our model + the real .onnx paths.
///   * The model set carries the SD params (pad/blank token ids, tokenizer limit,
///     embeddings length, VAE scale factor) plus one session per file.
///   * The CLIP tokenizer .onnx uses ORT custom ops, so Microsoft.ML.OnnxRuntime.Extensions
///     must be referenced (it is, in the csproj) or LoadModel fails.
/// Then LoadModel the config's model and GenerateAsImageAsync; img2img supplies an
/// InputImage, txt2img (a scene set with no source) omits it.
/// </summary>
sealed class OnnxImageGenerator : IImageGenerator, IDisposable
{
    // Standard SD-1.5 tokenizer / latent constants (CLIP ViT-L/14).
    private const int PadToken = 49407, BlankToken = 49407, TokenLimit = 77, EmbeddingLen = 768;
    private const double VaeScaleFactor = 0.18215;

    public string ModelId => AiModels.ModelId;   // feeds the cache key -> caches apart from procedural
    public string ModelRevision => "1";
    public bool SupportsTextToImage => true;

    private readonly IServiceProvider _provider;
    private readonly IStableDiffusionService _service;
    private readonly ModelOptions _model;

    public OnnxImageGenerator(string modelDir)
    {
        WriteModelConfig(modelDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOnnxStackStableDiffusion();
        _provider = services.BuildServiceProvider();

        _service = _provider.GetRequiredService<IStableDiffusionService>();
        var config = _provider.GetRequiredService<StableDiffusionConfig>();
        _model = config.OnnxModelSets.First(m => m.Name == AiModels.ModelId);

        _service.LoadModel(_model).GetAwaiter().GetResult();
    }

    // Write appsettings.json (next to the binary, where OnnxStack looks) describing our
    // single model set with absolute .onnx paths. Serialized via System.Text.Json so
    // Windows backslashes in paths are escaped correctly. Enums are written as their
    // string names, which is how OnnxStack's config deserializer expects them.
    private static void WriteModelConfig(string modelDir)
    {
        string M(string rel) => System.IO.Path.Combine(modelDir, rel);
        object Session(string type, string rel) => new { Type = type, OnnxModelPath = M(rel) };

        var payload = new
        {
            OnnxStackConfig = new
            {
                OnnxModelSets = new object[]
                {
                    new
                    {
                        Name = AiModels.ModelId,
                        IsEnabled = true,
                        PipelineType = nameof(DiffuserPipelineType.LatentConsistency),
                        Diffusers = new[] { nameof(DiffuserType.TextToImage), nameof(DiffuserType.ImageToImage) },
                        ExecutionProvider = nameof(OnnxStack.Core.Config.ExecutionProvider.Cpu),
                        PadTokenId = PadToken,
                        BlankTokenId = BlankToken,
                        TokenizerLimit = TokenLimit,
                        EmbeddingsLength = EmbeddingLen,
                        ScaleFactor = VaeScaleFactor,
                        ModelConfigurations = new[]
                        {
                            Session(nameof(OnnxModelType.Tokenizer),   "tokenizer/model.onnx"),
                            Session(nameof(OnnxModelType.TextEncoder), "text_encoder/model.onnx"),
                            Session(nameof(OnnxModelType.Unet),        "unet/model.onnx"),
                            Session(nameof(OnnxModelType.VaeDecoder),  "vae_decoder/model.onnx"),
                            Session(nameof(OnnxModelType.VaeEncoder),  "vae_encoder/model.onnx"),
                        },
                    },
                },
            },
        };

        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool Generate(AiGenRequest req, byte[] sourceBytes, string sourceExt, string outputPath)
    {
        bool img2img = req.Mode != "direct" && sourceBytes.Length > 0;

        var prompt = new PromptOptions
        {
            Prompt = req.Prompt,
            DiffuserType = img2img ? DiffuserType.ImageToImage : DiffuserType.TextToImage,
            SchedulerType = SchedulerType.LCM,
        };
        if (img2img)
            prompt.InputImage = new InputImage(sourceBytes);

        var scheduler = new SchedulerOptions
        {
            InferenceSteps = req.Img2Img?.Steps ?? 6,        // LCM: few steps
            GuidanceScale  = 1.0f,                           // LCM runs at low/no guidance
            Strength       = req.Img2Img?.Strength ?? 0.6f,  // img2img distance from the source
            Seed           = StableSeed(req.SetKey + "|" + req.TagsSlug),
            Width          = 512,
            Height         = 512,
        };

        using Image<Rgba32> image =
            _service.GenerateAsImageAsync(_model, prompt, scheduler, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
        image.SaveAsPng(outputPath);
        return File.Exists(outputPath);
    }

    // Stable across runs (string.GetHashCode is randomized) so a regeneration is reproducible.
    private static int StableSeed(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)(h & 0x7fffffff);
        }
    }

    public void Dispose() => (_provider as IDisposable)?.Dispose();
}
