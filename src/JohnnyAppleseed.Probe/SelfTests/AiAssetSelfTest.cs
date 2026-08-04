using JohnnyAppleseed.Ai;
using JohnnyAppleseed.Ambient;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Headless verification of the AI asset foundation: the deterministic cache-key /
/// naming scheme (<see cref="AiCacheKey"/>), the prompt-spec JSONC loader, and the
/// AI-aware variant planner (<see cref="AiVariant"/>) - especially the two guarantees
/// the design hinges on: (1) an already-generated/authored variant is REUSED and no
/// generation is requested ("skip work if done"), and (2) a genuinely missing variant
/// with an authored prompt DOES trigger a single generation request. No Raylib, no
/// network, no model download - reaches internals via InternalsVisibleTo.
///
/// Run via <c>uv run scripts/probe.py selftest ai</c>. Exit code 0 = all passed.
/// </summary>
static class AiAssetSelfTest
{
    private const string Set  = "graphics/main-menu/backdrop";
    private const string Base = "graphics/main-menu/backdrop.jpg";

    public static int Run()
    {
        Console.WriteLine("AI-ASSET SELF-TEST");
        int fails = 0;

        fails += CacheKeyDeterminism();
        fails += NamingRoundTrip();
        fails += PromptJsoncParsing();
        fails += PlannerReuseAndGenerate();
        fails += SceneBasedGeneration();

        Console.WriteLine(fails == 0
            ? "\nAI-ASSET SELF-TEST: ALL PASSED"
            : $"\nAI-ASSET SELF-TEST: {fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    // -- cache key: stable, order-independent, and sensitive to every real input ----
    private static int CacheKeyDeterminism()
    {
        int fails = 0;

        string a = Key("sha-src", new[] { "rainy", "night" }, "wet streets");
        string b = Key("sha-src", new[] { "rainy", "night" }, "wet streets");
        fails += True(a == b, "same inputs -> same cache key");

        string reordered = Key("sha-src", new[] { "night", "rainy" }, "wet streets");
        fails += True(a == reordered, "tag order does not change the key");

        fails += True(a != Key("sha-DIFFERENT", new[] { "rainy", "night" }, "wet streets"),
            "changed source bytes -> new key (re-art invalidates)");
        fails += True(a != Key("sha-src", new[] { "rainy", "night" }, "DIFFERENT prompt"),
            "changed prompt -> new key");
        fails += True(a != Key("sha-src", new[] { "snowy", "night" }, "wet streets"),
            "changed tags -> new key");

        fails += True(a.Length == 16 && a.All(Uri.IsHexDigit), "key is 16 lowercase hex chars");
        return fails;
    }

    private static string Key(string srcSha, string[] tags, string prompt) =>
        AiCacheKey.Compute("sd15-lcm-img2img", "r1", Base, srcSha, tags, prompt, "{\"steps\":4}");

    // -- names: store filename is unique; the logical key parses back through ArtVariant
    private static int NamingRoundTrip()
    {
        int fails = 0;

        string slug = AiCacheKey.NormalizeTags(new[] { "night", "rainy" });
        fails += Eq(slug, "night.rainy", "tags normalize to sorted dot-joined slug");

        string logical = AiCacheKey.LogicalKey(Set, slug, ".png");
        fails += Eq(logical, "graphics/main-menu/backdrop.night.rainy.png", "logical key shape");

        string store = AiCacheKey.StoreFileName(Set, slug, "abc123", ".png");
        fails += Eq(store, "graphics__main-menu__backdrop.night.rainy.abc123.png", "store filename shape");

        // Untagged base edition (empty tags) - used by fully-generated scene sets.
        fails += Eq(AiCacheKey.LogicalKey(Set, "", ".png"), "graphics/main-menu/backdrop.png",
            "empty tags -> untagged base logical key");
        fails += Eq(AiCacheKey.StoreFileName(Set, "", "abc123", ".png"), "graphics__main-menu__backdrop.abc123.png",
            "empty tags -> base store filename");

        // The cached logical key must be selectable by the UNMODIFIED ArtVariant policy:
        // under rainy+night it should be chosen over the untagged base.
        var pick = ArtVariant.Resolve(Set, Cond(Weather.Rainy, Daylight.Night), new[] { Base, logical });
        fails += Eq(pick?.Key, logical, "ArtVariant picks the cached rainy.night edition over base");
        return fails;
    }

    // -- JSONC loader tolerates comments + trailing commas (authored by hand) --------
    private static int PromptJsoncParsing()
    {
        int fails = 0;
        const string jsonc = """
        {
            // main-menu backdrop weather placeholders
            "graphics/main-menu/backdrop": {
                "base": "graphics/main-menu/backdrop.jpg",
                "img2img": { "strength": 0.45, "steps": 4 },
                "conditions": {
                    "rainy": "overcast, heavy rain, wet reflections",
                    "night": "night, moonlight, deep blue tones",
                },
            },
        }
        """;
        var spec = AiPrompts.Parse(jsonc);
        fails += True(spec.ContainsKey(Set), "jsonc parsed: set present");
        fails += Eq(spec[Set].Base, Base, "jsonc parsed: base");
        fails += Eq(spec[Set].Conditions.GetValueOrDefault("rainy"), "overcast, heavy rain, wet reflections",
            "jsonc parsed: rainy fragment (comment + trailing comma tolerated)");
        fails += True(spec[Set].Img2Img?.Steps == 4, "jsonc parsed: img2img steps");
        return fails;
    }

    // -- the two headline guarantees: reuse (skip) vs. generate-on-miss --------------
    private static int PlannerReuseAndGenerate()
    {
        int fails = 0;

        var prompt = new AiPromptSet
        {
            Base = Base,
            Conditions = new Dictionary<string, string>
            {
                ["rainy"]  = "overcast, heavy rain",
                ["summer"] = "bright summer greenery",
            },
        };

        // (1) Missing rainy edition, prompt exists -> chosen falls back to base AND a
        //     rainy generation is requested.
        var p1 = AiVariant.Resolve(Set, Cond(Weather.Rainy, Daylight.Day),
            embeddedKeys: new[] { Base }, cachedKeys: Array.Empty<string>(), prompt);
        fails += Eq(p1.Chosen?.Key, Base, "no rainy art -> shows base for now");
        fails += True(p1.Generate is { } g1 && g1.Tags.Count == 1 && g1.Tags[0] == "rainy",
            "no rainy art -> requests rainy generation");

        // (2) A cached rainy edition already exists -> it is chosen and NO generation
        //     is requested. This is the "skip work if already done" guarantee.
        string cachedRainy = AiCacheKey.LogicalKey(Set, "rainy", ".png");
        var p2 = AiVariant.Resolve(Set, Cond(Weather.Rainy, Daylight.Day),
            embeddedKeys: new[] { Base }, cachedKeys: new[] { cachedRainy }, prompt);
        fails += Eq(p2.Chosen?.Key, cachedRainy, "cached rainy exists -> it is chosen");
        fails += True(p2.Generate is null, "cached rainy exists -> no regeneration requested");

        // (3) A hand-authored (embedded) rainy edition -> chosen, no generation.
        string embeddedRainy = Set + ".rainy.png";
        var p3 = AiVariant.Resolve(Set, Cond(Weather.Rainy, Daylight.Day),
            embeddedKeys: new[] { Base, embeddedRainy }, cachedKeys: Array.Empty<string>(), prompt);
        fails += Eq(p3.Chosen?.Key, embeddedRainy, "authored rainy exists -> it is chosen");
        fails += True(p3.Generate is null, "authored rainy exists -> no generation (authors win)");

        // (4) Normal weather (no tag) -> weather is skipped; the next desired tag with a
        //     prompt fragment (summer) is requested instead.
        var p4 = AiVariant.Resolve(Set, new Conditions(Weather.Normal, Season.Summer, Daylight.Day),
            embeddedKeys: new[] { Base }, cachedKeys: Array.Empty<string>(), prompt);
        fails += True(p4.Generate is { } g4 && g4.Tags[0] == "summer",
            "normal weather + summer season -> requests summer generation");

        // (5) No prompt authored -> never requests generation (feature inert until authored).
        var p5 = AiVariant.Resolve(Set, Cond(Weather.Rainy, Daylight.Day),
            embeddedKeys: new[] { Base }, cachedKeys: Array.Empty<string>(), prompt: null);
        fails += True(p5.Generate is null, "no prompt spec -> no generation requested");

        // (6) Prompt with NO base -> the scene-supplied sourceFallback becomes the img2img
        //     source (this is how a story `# bg:` image gets weather editions).
        var noBase = new AiPromptSet { Base = "", Conditions = new() { ["rainy"] = "cold rain" } };
        var p6 = AiVariant.Resolve(Set, Cond(Weather.Rainy, Daylight.Day),
            embeddedKeys: Array.Empty<string>(), cachedKeys: Array.Empty<string>(),
            noBase, sourceFallback: "graphics/story/bridge.jpg");
        fails += True(p6.Generate is { } g6 && g6.SourceKey == "graphics/story/bridge.jpg" && g6.Mode == "img2img",
            "no base + sourceFallback -> img2img from the scene image");

        // (7) Explicit direct mode is carried through (engine decides if it can fulfil it).
        var directPrompt = new AiPromptSet { Mode = "direct", Conditions = new() { ["rainy"] = "a rainy vista" } };
        var p7 = AiVariant.Resolve(Set, Cond(Weather.Rainy, Daylight.Day),
            embeddedKeys: Array.Empty<string>(), cachedKeys: Array.Empty<string>(), directPrompt);
        fails += True(p7.Generate is { } g7 && g7.Mode == "direct", "mode:direct -> request carries direct mode");

        return fails;
    }

    // A fully-generated background set (a scene prompt, no source art) - the intro bg.
    private static int SceneBasedGeneration()
    {
        int fails = 0;
        const string set = "graphics/story/natural-bridge";
        var scene = new AiPromptSet
        {
            Mode = "direct",
            Scene = "A scenic photograph of Natural Bridge in Virginia",
            Conditions = new Dictionary<string, string> { ["rainy"] = "heavy rain" },
        };

        // Nothing cached yet -> the untagged BASE is generated first, txt2img from the scene.
        var p1 = AiVariant.Resolve(set, Cond(Weather.Rainy, Daylight.Day),
            Array.Empty<string>(), Array.Empty<string>(), scene);
        fails += True(p1.Generate is { } g1 && g1.Tags.Count == 0 && g1.Mode == "direct"
                      && g1.Prompt == "A scenic photograph of Natural Bridge in Virginia",
            "scene set: base generated first (txt2img, empty tags)");

        // Once the base exists, the rainy edition is requested as scene + fragment.
        string baseKey = AiCacheKey.LogicalKey(set, "", ".png");   // graphics/story/natural-bridge.png
        var p2 = AiVariant.Resolve(set, Cond(Weather.Rainy, Daylight.Day),
            Array.Empty<string>(), new[] { baseKey }, scene);
        fails += True(p2.Generate is { } g2 && g2.Tags.Count == 1 && g2.Tags[0] == "rainy"
                      && g2.Prompt == "A scenic photograph of Natural Bridge in Virginia, heavy rain",
            "scene set: weather edition = scene + fragment");

        return fails;
    }

    private static Conditions Cond(Weather w, Daylight d) =>
        new Conditions(w, Season.Spring, d, ConditionVocab.RepresentativeLumens(d));

    private static int True(bool ok, string label)
    {
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}");
        return ok ? 0 : 1;
    }

    private static int Eq<T>(T actual, T expected, string label)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}"
            + (ok ? "" : $"   (got {actual?.ToString() ?? "null"}, want {expected?.ToString() ?? "null"})"));
        return ok ? 0 : 1;
    }
}
