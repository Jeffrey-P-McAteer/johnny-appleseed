// Fully qualify Ink.Runtime.* — the vendored compiler assembly also exposes an
// Ink.Parsed.Story, so an unqualified `using Ink.Runtime` makes `Story` ambiguous.
// Ink types are kept OUT of this class's public surface so that consumers (e.g.
// the probe) don't need the ink assemblies referenced just to use StoryRunner.
using InkStory = Ink.Runtime.Story;

namespace JohnnyAppleseed.Narrative;

/// <summary>
/// Thin wrapper around an ink <see cref="Story"/>: compiles authored <c>.ink</c>
/// source, steps the narrative one line at a time, surfaces the per-line tags
/// (<c># bg:</c>, <c># music:</c>, …) and the current choices, bridges variables
/// to <see cref="GameState"/>, and (de)serializes runtime state for saves.
///
/// We compile at runtime (via <see cref="Ink.Compiler"/>) rather than embedding
/// pre-compiled JSON, so writers' raw <c>.ink</c> is what ships and can be
/// hot-reloaded in dev, with no build-time <c>inklecate</c> step.
/// </summary>
sealed class StoryRunner
{
    private readonly InkStory _story;

    public StoryRunner(string inkSource)
    {
        var compiler = new Ink.Compiler(inkSource);
        _story = compiler.Compile();
    }

    /// <summary>More narration is available before the next choice/end.</summary>
    public bool CanContinue => _story.canContinue;

    /// <summary>Number of choices offered at the current stopping point (may be 0).</summary>
    public int ChoiceCount => _story.currentChoices.Count;

    /// <summary>No more narration and no choices — the story (or thread) is over.</summary>
    public bool IsEnded => !_story.canContinue && _story.currentChoices.Count == 0;

    /// <summary>Advance one line; returns its text and the tags attached to it.</summary>
    public string Continue(out IReadOnlyList<string> tags)
    {
        string text = _story.Continue().TrimEnd('\n', '\r', ' ');
        tags = _story.currentTags ?? (IReadOnlyList<string>)Array.Empty<string>();
        return text;
    }

    /// <summary>The last line produced by <see cref="Continue"/> — used to re-show
    /// the current beat after restoring saved state (without advancing).</summary>
    public string CurrentText => (_story.currentText ?? "").TrimEnd('\n', '\r', ' ');

    /// <summary>Tags for the current beat (mirrors <see cref="CurrentText"/>).</summary>
    public IReadOnlyList<string> CurrentTags =>
        _story.currentTags ?? (IReadOnlyList<string>)Array.Empty<string>();

    public string ChoiceText(int index) => _story.currentChoices[index].text;
    public void Choose(int index) => _story.ChooseChoiceIndex(index);

    // ── persistence ─────────────────────────────────────────────────────────────
    public string SaveState() => _story.state.ToJson();
    public void LoadState(string json) => _story.state.LoadJson(json);

    // ── C# ↔ ink bridge (minigames, inventory queries, …) ───────────────────────
    public void BindExternalFunction(string name, Func<object[], object?> fn, bool lookaheadSafe = false)
        => _story.BindExternalFunctionGeneral(name, args => fn(args)!, lookaheadSafe);

    public object GetVariable(string name) => _story.variablesState[name];
    public void SetVariable(string name, object value) => _story.variablesState[name] = value;
}
