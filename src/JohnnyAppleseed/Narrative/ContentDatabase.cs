using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace JohnnyAppleseed.Narrative;

/// <summary>
/// Loads and indexes the shared, authored content definitions (items and
/// characters) embedded from <c>story/</c>. This is the single source both the
/// story scene and the future RPG map read, so a description written once shows
/// up identically everywhere.
///
/// Lazy and fault-tolerant: definitions load on first access, and a missing or
/// malformed file degrades to an empty set (logged) rather than crashing the game
/// before any content exists. The JSONC files may contain <c>//</c> and
/// <c>/* … */</c> comments (see <see cref="ContentJsonContext"/>).
/// </summary>
static class ContentDatabase
{
    private const string ItemsKey = "story/items/items.jsonc";
    private const string CharactersKey = "story/characters/characters.jsonc";

    private static Dictionary<string, ItemDef>? _items;
    private static Dictionary<string, CharacterDef>? _characters;

    public static IReadOnlyDictionary<string, ItemDef> Items =>
        _items ??= Index(Load(ItemsKey, ContentJsonContext.Default.ItemDefArray), i => i.Id, "item");

    public static IReadOnlyDictionary<string, CharacterDef> Characters =>
        _characters ??= Index(Load(CharactersKey, ContentJsonContext.Default.CharacterDefArray), c => c.Id, "character");

    /// <summary>The item with this id, or null if undefined.</summary>
    public static ItemDef? Item(string id) => Items.TryGetValue(id, out ItemDef? v) ? v : null;

    /// <summary>The character with this id, or null if undefined.</summary>
    public static CharacterDef? Character(string id) => Characters.TryGetValue(id, out CharacterDef? v) ? v : null;

    /// <summary>Drop cached content so a subsequent access re-reads (used by dev/hot-reload tooling).</summary>
    public static void Reload()
    {
        _items = null;
        _characters = null;
    }

    private static T[] Load<T>(string key, JsonTypeInfo<T[]> typeInfo)
    {
        if (!Assets.Exists(key))
        {
            Console.Error.WriteLine($"[content] '{key}' not embedded — using an empty set");
            return Array.Empty<T>();
        }

        try
        {
            return JsonSerializer.Deserialize(Assets.Bytes(key), typeInfo) ?? Array.Empty<T>();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[content] failed to parse '{key}': {ex.Message}");
            return Array.Empty<T>();
        }
    }

    private static Dictionary<string, T> Index<T>(T[] defs, Func<T, string> idOf, string label)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (T def in defs)
        {
            string id = idOf(def);
            if (string.IsNullOrWhiteSpace(id))
            {
                Console.Error.WriteLine($"[content] a {label} has no id — skipped");
                continue;
            }
            if (!map.TryAdd(id, def))
                Console.Error.WriteLine($"[content] duplicate {label} id '{id}' — keeping the first");
        }
        return map;
    }
}
