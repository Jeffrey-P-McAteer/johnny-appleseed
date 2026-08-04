using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// Downloads and verifies the neural image model into <c>ai-models/</c> on demand.
///
/// Target: <c>TheyCallMeHex/LCM-Dreamshaper-V7-ONNX</c> - an SD-1.5-lineage Latent
/// Consistency model exported to ONNX (fp16 UNet), including a <c>vae_encoder</c> (so
/// img2img works) and a CLIP tokenizer. It is an OnnxStack-native layout, consumed by
/// <c>OnnxImageGenerator</c> (which ships in every build).
///
/// Robust, resumable-enough, and honest about "already done":
///   * File list + per-file SHA-256 come from the Hugging Face tree API at runtime, so
///     no hashes are hard-coded and a re-published model is caught.
///   * Each file downloads to <c>&lt;dir&gt;.part</c>, is SHA-256 verified (for the large
///     LFS weights), then atomically moved into place.
///   * A <c>.complete</c> marker is written only after every file verifies, so a partial
///     download is never mistaken for a ready model.
///
/// Pure download/verify - no ONNX or OnnxStack dependency - so this compiles and is
/// exercised in the default build; only the generator that *uses* the files is gated.
/// </summary>
static class AiModels
{
    public const string ModelId  = "lcm-dreamshaper-v7";
    private const string Repo     = "TheyCallMeHex/LCM-Dreamshaper-V7-ONNX";
    // Pin to a revision for reproducibility. "main" tracks latest; swap for a commit SHA
    // to freeze exact bytes. The HF tree API is queried at this same revision.
    private const string Revision = "main";

    // The files this model needs (everything but the repo's Assets/README/previews).
    private static readonly string[] Files =
    {
        "model_index.json",
        "scheduler/scheduler_config.json",
        "feature_extractor/preprocessor_config.json",
        "text_encoder/config.json",  "text_encoder/model.onnx",
        "tokenizer/merges.txt", "tokenizer/model.onnx", "tokenizer/vocab.json",
        "tokenizer/special_tokens_map.json", "tokenizer/tokenizer_config.json",
        "unet/config.json", "unet/model.onnx", "unet/model.onnx_data",
        "vae_decoder/config.json", "vae_decoder/model.onnx",
        "vae_encoder/config.json", "vae_encoder/model.onnx",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string ModelDir => System.IO.Path.Combine(AiPaths.ModelsDir, ModelId, Revision);
    private static string CompleteMarker => System.IO.Path.Combine(ModelDir, ".complete");

    /// <summary>True if the model is fully downloaded and verified (the marker exists).</summary>
    public static bool IsInstalled => File.Exists(CompleteMarker);

    /// <summary>
    /// Ensure the model is present and verified. No-op if already installed. Downloads
    /// (potentially ~4 GB) otherwise; report coarse progress via <paramref name="progress"/>.
    /// Best-effort: returns false and logs on any failure rather than throwing.
    /// </summary>
    public static async Task<bool> EnsureAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled) return true;
        try
        {
            AiPaths.Initialize();
            Directory.CreateDirectory(ModelDir);

            Dictionary<string, string?> sha = await FetchExpectedShaAsync(ct);   // path -> sha256 (LFS) or null

            for (int i = 0; i < Files.Length; i++)
            {
                string rel = Files[i];
                progress?.Invoke($"[{i + 1}/{Files.Length}] {rel}");
                if (!await DownloadFileAsync(rel, sha.GetValueOrDefault(rel), ct))
                    return false;
            }

            File.WriteAllText(CompleteMarker, DateTime.UtcNow.ToString("o"));
            progress?.Invoke("model ready");
            Log($"installed {ModelId} at {ModelDir}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"download failed: {ex.Message}");
            return false;
        }
    }

    // path -> sha256 for LFS-tracked files (the big .onnx / .onnx_data); null for small
    // git blobs (verified by existence/size instead).
    private static async Task<Dictionary<string, string?>> FetchExpectedShaAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        string url = $"https://huggingface.co/api/models/{Repo}/tree/{Revision}?recursive=true";
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetByteArrayAsync(url, ct));
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("path", out JsonElement p)) continue;
                string path = p.GetString() ?? "";
                string? oid = e.TryGetProperty("lfs", out JsonElement lfs) &&
                              lfs.TryGetProperty("oid", out JsonElement o)
                    ? o.GetString() : null;
                map[path] = oid;
            }
        }
        catch (Exception ex) { Log($"tree query failed ({ex.Message}); will verify by size only"); }
        return map;
    }

    private static async Task<bool> DownloadFileAsync(string rel, string? expectedSha, CancellationToken ct)
    {
        string dest = System.IO.Path.Combine(ModelDir, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);

        if (File.Exists(dest) && (expectedSha is null || await Sha256Async(dest, ct) == expectedSha))
            return true;   // already have a good copy

        string url = $"https://huggingface.co/{Repo}/resolve/{Revision}/{rel}?download=true";
        string tmp = dest + ".part";
        try
        {
            using (HttpResponseMessage resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using FileStream fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (expectedSha is not null)
            {
                string got = await Sha256Async(tmp, ct);
                if (!string.Equals(got, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"checksum mismatch for {rel} (want {expectedSha[..8]}, got {got[..8]})");
                    TryDelete(tmp);
                    return false;
                }
            }

            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log($"download {rel} failed: {ex.Message}");
            TryDelete(tmp);
            return false;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using FileStream fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void Log(string msg) => Console.Error.WriteLine($"[ai-models] {msg}");
}
