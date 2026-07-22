using System.Reflection;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Lists the assets embedded in the game assembly (everything compiled in from the
/// repo's audio/ and graphics/ trees, plus the build-generated icon.png). Confirms
/// the single-file binary actually carries its assets, without shipping a debug
/// flag in the game itself.
///
/// Reads the game's <c>Assets</c> type (internal, exposed via InternalsVisibleTo)
/// to reuse its decode path for reported sizes.
/// </summary>
static class AssetProbe
{
    public static int List()
    {
        Assembly game = typeof(Assets).Assembly;
        string[] names = game.GetManifestResourceNames();
        Array.Sort(names, StringComparer.Ordinal);

        Console.WriteLine($"Embedded assets in {game.GetName().Name} ({names.Length}):");
        long total = 0;
        foreach (string name in names)
        {
            int len = Assets.Bytes(name).Length;
            total += len;
            Console.WriteLine($"  {name}  ({len:N0} bytes)");
        }
        Console.WriteLine($"\nTotal embedded: {total:N0} bytes");
        return 0;
    }
}
