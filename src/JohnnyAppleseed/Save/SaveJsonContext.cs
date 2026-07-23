using System.Text.Json;
using System.Text.Json.Serialization;

namespace JohnnyAppleseed.Save;

/// <summary>
/// System.Text.Json source-generation context.
///
/// Using a compile-time-generated (de)serializer instead of the reflection-based
/// one keeps saving working under this project's single-file / self-contained
/// publish settings, avoids startup reflection cost, and is trim/AOT safe.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    // Emit nulls too so the on-disk shape is stable and easy to hand-edit.
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(SaveData))]
[JsonSerializable(typeof(WorldState))]
internal partial class SaveJsonContext : JsonSerializerContext
{
}
