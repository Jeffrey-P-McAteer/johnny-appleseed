using JohnnyAppleseed.Narrative;
using JohnnyAppleseed.Save;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Dumps the shared content database (items + characters) parsed from the
/// embedded <c>story/*.jsonc</c> files, proving the comment-tolerant parse works
/// and that greentext/descriptions load. Also runs a small <see cref="GameState"/>
/// round-trip. Fully headless — reads embedded resources only, no window/audio.
/// Exit code 0 = everything parsed and the state round-trip held.
/// </summary>
static class ContentProbe
{
    private const string Green = "\x1b[32m";
    private const string Reset = "\x1b[0m";

    public static int Run()
    {
        int rc = 0;

        IReadOnlyDictionary<string, ItemDef> items = ContentDatabase.Items;
        Console.WriteLine($"Items: {items.Count}");
        foreach (ItemDef it in items.Values)
        {
            Console.WriteLine($"  [{it.Id}] {it.Name}  (stackable={it.Stackable}, icon={it.Icon})");
            Console.WriteLine($"      {it.Description}");
            foreach (string line in it.Greentext)
                Console.WriteLine($"      {Green}{line}{Reset}");   // show greentext in green
        }
        if (items.Count == 0) { Console.Error.WriteLine("  !! no items parsed"); rc = 1; }

        Console.WriteLine();
        IReadOnlyDictionary<string, CharacterDef> chars = ContentDatabase.Characters;
        Console.WriteLine($"Characters: {chars.Count}");
        foreach (CharacterDef c in chars.Values)
        {
            Console.WriteLine($"  [{c.Id}] {c.Name}  (textColor={c.TextColor ?? "-"}, portrait={c.Portrait})");
            Console.WriteLine($"      {c.Description}");
        }
        if (chars.Count == 0) { Console.Error.WriteLine("  !! no characters parsed"); rc = 1; }

        // GameState round-trip on a throwaway save doc (no file I/O).
        Console.WriteLine();
        var gs = GameState.For(new SaveData());
        gs.SetFlag("met_johnny");
        gs.Give("apple-seeds", 3);
        gs.Give("apple-seeds", 2);
        gs.Take("apple-seeds", 1);       // → 4
        gs.AddVar("stamina", 5);
        gs.MarkVisited("ohio_forest");

        bool ok = gs.Flag("met_johnny")
                  && gs.Count("apple-seeds") == 4
                  && gs.Var("stamina") == 5.0
                  && gs.HasVisited("ohio_forest")
                  && !gs.Has("axe");
        Console.WriteLine(
            $"GameState round-trip: seeds={gs.Count("apple-seeds")} stamina={gs.Var("stamina")} " +
            $"met_johnny={gs.Flag("met_johnny")} visited(ohio_forest)={gs.HasVisited("ohio_forest")} " +
            $"=> {(ok ? "OK" : "FAIL")}");
        if (!ok) rc = 1;

        Console.WriteLine();
        Console.WriteLine(rc == 0 ? "content: OK" : "content: FAILURES (see above)");
        return rc;
    }
}
