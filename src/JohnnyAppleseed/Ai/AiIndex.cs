using System.Text.Json.Serialization;

namespace JohnnyAppleseed.Ai;

/// <summary>
/// One cached generated asset's provenance and location, persisted in
/// <c>ai-assets/index.json</c>. <see cref="LogicalKey"/> is the embedded-style key the
/// variant selector sees; <see cref="File"/> is its filename within <c>ai-assets/store/</c>.
/// The <see cref="CacheKey"/> / <see cref="SourceSha"/> record exactly what produced it,
/// so a changed source or model no longer matches and the stale file is simply ignored.
/// </summary>
sealed class AiIndexEntry
{
    [JsonPropertyName("setKey")]     public string SetKey { get; set; } = "";
    [JsonPropertyName("logicalKey")] public string LogicalKey { get; set; } = "";
    [JsonPropertyName("tagsSlug")]   public string TagsSlug { get; set; } = "";
    [JsonPropertyName("cacheKey")]   public string CacheKey { get; set; } = "";
    [JsonPropertyName("file")]       public string File { get; set; } = "";
    [JsonPropertyName("sourceKey")]  public string SourceKey { get; set; } = "";
    [JsonPropertyName("sourceSha")]  public string SourceSha { get; set; } = "";
    [JsonPropertyName("model")]      public string Model { get; set; } = "";
}

/// <summary>The on-disk lookup for every cached generated asset.</summary>
sealed class AiIndex
{
    [JsonPropertyName("entries")] public List<AiIndexEntry> Entries { get; set; } = new();
}
