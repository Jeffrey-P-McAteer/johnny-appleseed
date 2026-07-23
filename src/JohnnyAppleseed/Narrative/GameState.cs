using JohnnyAppleseed.Save;

namespace JohnnyAppleseed.Narrative;

/// <summary>
/// Ergonomic, intention-revealing accessor over the persisted <see cref="WorldState"/>.
///
/// Scenes, minigames, and (later) the RPG map manipulate story state through this
/// façade instead of poking the raw dictionaries, so the call sites read like the
/// story does — <c>state.Give("apple-seeds", 3)</c>, <c>state.Flag("met_johnny")</c>,
/// <c>if (state.Has("axe"))</c>. It wraps the same object that lives inside
/// <see cref="SaveData"/>, so mutations are captured by the next
/// <see cref="SaveSystem.Save"/>.
///
/// When Ink arrives, an adapter can mirror these reads/writes onto Ink variables
/// so writers and C# systems share one truth; the surface here stays the same.
/// </summary>
sealed class GameState
{
    private readonly WorldState _w;

    public GameState(WorldState world) => _w = world;

    /// <summary>Wrap the world state carried by a save document.</summary>
    public static GameState For(SaveData save) => new(save.World);

    // ── flags ──────────────────────────────────────────────────────────────────
    public bool Flag(string id) => _w.Flags.TryGetValue(id, out bool v) && v;
    public void SetFlag(string id, bool value = true) => _w.Flags[id] = value;
    public void ClearFlag(string id) => _w.Flags.Remove(id);

    // ── numeric variables (counters, scores, relationship values) ───────────────
    public double Var(string id) => _w.Vars.TryGetValue(id, out double v) ? v : 0.0;
    public void SetVar(string id, double value) => _w.Vars[id] = value;
    public double AddVar(string id, double delta) => _w.Vars[id] = Var(id) + delta;

    // ── inventory (item id → count) ─────────────────────────────────────────────
    public int Count(string itemId) => _w.Inventory.TryGetValue(itemId, out int n) ? n : 0;
    public bool Has(string itemId, int atLeast = 1) => Count(itemId) >= atLeast;

    public void Give(string itemId, int count = 1)
    {
        if (count <= 0) return;
        _w.Inventory[itemId] = Count(itemId) + count;
    }

    /// <summary>Remove up to <paramref name="count"/>; returns how many were actually taken.</summary>
    public int Take(string itemId, int count = 1)
    {
        int have = Count(itemId);
        int taken = Math.Min(have, Math.Max(0, count));
        int left = have - taken;
        if (left > 0) _w.Inventory[itemId] = left;
        else _w.Inventory.Remove(itemId);
        return taken;
    }

    // ── visited nodes ───────────────────────────────────────────────────────────
    public bool HasVisited(string nodeId) => _w.Visited.Contains(nodeId);
    public void MarkVisited(string nodeId)
    {
        if (!_w.Visited.Contains(nodeId)) _w.Visited.Add(nodeId);
    }

    public string CurrentNode
    {
        get => _w.CurrentNode;
        set => _w.CurrentNode = value;
    }
}
