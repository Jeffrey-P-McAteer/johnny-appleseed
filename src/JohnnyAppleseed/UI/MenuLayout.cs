using System.Text.Json;
using System.Text.Json.Serialization;

namespace JohnnyAppleseed.UI;

/// <summary>What activating a menu button does. Parsed from the authored string.</summary>
enum MenuActionKind { None, Continue, NewStory, Preferences, ToggleFullscreen, Leave }

/// <summary>
/// An artist-authored main-menu layout: a backdrop art-set key plus buttons placed
/// at normalized positions over it. Read from <c>content/menu/main-menu.jsonc</c>
/// via <see cref="MenuLayoutDatabase"/> (same JSONC + fault-tolerant loading the
/// narrative content uses). Positions are resolution-independent so the layout
/// tracks the artwork in any window size / fullscreen.
/// </summary>
sealed class MenuLayoutDef
{
    /// <summary>Art-set key resolved by <see cref="Ambient.ArtVariant"/>, e.g. "graphics/main-menu/backdrop".</summary>
    [JsonPropertyName("backdrop")]
    public string Backdrop { get; set; } = "";

    [JsonPropertyName("buttons")]
    public MenuButtonDef[] Buttons { get; set; } = Array.Empty<MenuButtonDef>();
}

/// <summary>One placed text button. <see cref="X"/>/<see cref="Y"/> are the button's
/// center as a fraction (0..1) of the screen.</summary>
sealed class MenuButtonDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>Center X, normalized 0..1 of screen width.</summary>
    [JsonPropertyName("x")]
    public float X { get; set; }

    /// <summary>Center Y, normalized 0..1 of screen height.</summary>
    [JsonPropertyName("y")]
    public float Y { get; set; }

    /// <summary>Optional fixed plate width, normalized 0..1; 0 = auto-size to the label.</summary>
    [JsonPropertyName("width")]
    public float Width { get; set; }

    /// <summary>Optional font size in px; 0 = the scene default.</summary>
    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; }

    /// <summary>Action id: "continue", "new_story", "preferences", "toggleFullscreen",
    /// "leave" (case-insensitive; "exit" is accepted as an alias for "leave").</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>Parsed <see cref="Action"/>; unknown/empty -> <see cref="MenuActionKind.None"/>.</summary>
    public MenuActionKind ActionKind => Action.Trim().ToLowerInvariant() switch
    {
        "continue"              => MenuActionKind.Continue,
        "new_story" or "newstory" => MenuActionKind.NewStory,
        "preferences"           => MenuActionKind.Preferences,
        "togglefullscreen"      => MenuActionKind.ToggleFullscreen,
        "leave" or "exit"       => MenuActionKind.Leave,
        _                       => MenuActionKind.None,
    };
}

/// <summary>
/// Loads the main-menu layout from the embedded JSONC, degrading to a built-in
/// default (never crashing) when the file is missing or malformed - mirroring the
/// fault tolerance of <see cref="Narrative.ContentDatabase"/>.
/// </summary>
static class MenuLayoutDatabase
{
    private const string LayoutKey = "content/menu/main-menu.jsonc";

    private static MenuLayoutDef? _cached;

    public static MenuLayoutDef Layout => _cached ??= LoadOrDefault();

    /// <summary>Drop the cache so a subsequent access re-reads (dev/hot-reload tooling).</summary>
    public static void Reload() => _cached = null;

    private static MenuLayoutDef LoadOrDefault()
    {
        if (!Assets.Exists(LayoutKey))
        {
            Console.Error.WriteLine($"[menu] '{LayoutKey}' not embedded - using the built-in layout");
            return Default();
        }

        try
        {
            MenuLayoutDef? def = JsonSerializer.Deserialize(
                Assets.Bytes(LayoutKey), MenuJsonContext.Default.MenuLayoutDef);
            if (def is null || def.Buttons.Length == 0)
            {
                Console.Error.WriteLine($"[menu] '{LayoutKey}' has no buttons - using the built-in layout");
                return Default();
            }
            if (string.IsNullOrEmpty(def.Backdrop))
                def.Backdrop = Default().Backdrop;
            return def;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[menu] failed to parse '{LayoutKey}': {ex.Message} - using the built-in layout");
            return Default();
        }
    }

    /// <summary>The fallback layout, matching the shipped content file.</summary>
    public static MenuLayoutDef Default() => new()
    {
        Backdrop = "graphics/main-menu/backdrop",
        Buttons =
        [
            new MenuButtonDef { Id = "continue",    Label = "CONTINUE",    X = 0.6f,  Y = 0.45f, Action = "continue" },
            new MenuButtonDef { Id = "new_story",   Label = "NEW STORY",   X = 0.36f, Y = 0.78f, Action = "new_story" },
            new MenuButtonDef { Id = "preferences", Label = "PREFERENCES", X = 0.8f,  Y = 0.82f, Action = "preferences" },
            new MenuButtonDef { Id = "leave",       Label = "LEAVE",       X = 0.1f,  Y = 0.88f, Action = "leave" },
        ],
    };
}
