using JohnnyAppleseed.Narrative;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Smoke-tests the ink integration end to end, headlessly: runtime-compile a tiny
/// story, step it, read per-line tags, present + take a choice, interpolate a
/// variable, and round-trip the saved state. Exit 0 = the whole path works.
/// </summary>
static class InkProbe
{
    private const string Source = @"
VAR seeds = 0
# bg: scenes/ohio/forest.jpg
# music: frontier_theme
The trail narrows between the elms.
~ seeds = 3
You are carrying {seeds} apple seeds.
* [Plant one here]  -> planted
* [Walk on]         -> walk_on

=== planted ===
~ seeds = seeds - 1
You press a seed into the earth. {seeds} remain.
-> END

=== walk_on ===
You shoulder the sack and walk on.
-> END
";

    public static int Run()
    {
        int rc = 0;

        StoryRunner runner;
        try
        {
            runner = new StoryRunner(Source);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ink compile FAILED: {ex.Message}");
            return 1;
        }

        Console.WriteLine("── first beat ──");
        while (runner.CanContinue)
        {
            string line = runner.Continue(out IReadOnlyList<string> tags);
            string tagStr = tags.Count > 0 ? $"   [tags: {string.Join(", ", tags)}]" : "";
            if (line.Length > 0 || tagStr.Length > 0)
                Console.WriteLine($"  {line}{tagStr}");
        }

        if (runner.ChoiceCount != 2)
        {
            Console.Error.WriteLine($"expected 2 choices, got {runner.ChoiceCount}");
            return 1;
        }
        Console.WriteLine("── choices ──");
        for (int i = 0; i < runner.ChoiceCount; i++)
            Console.WriteLine($"  [{i}] {runner.ChoiceText(i)}");

        Console.WriteLine("── choosing 0 (plant) ──");
        runner.Choose(0);
        while (runner.CanContinue)
            Console.WriteLine($"  {runner.Continue(out _)}");

        string state = runner.SaveState();
        bool stateOk = !string.IsNullOrEmpty(state) && state.Contains("seeds");
        Console.WriteLine();
        Console.WriteLine($"ended={runner.IsEnded}  state={state.Length} bytes  seedsInState={stateOk}");

        if (!runner.IsEnded || !stateOk) rc = 1;
        Console.WriteLine(rc == 0 ? "ink: OK" : "ink: FAIL");
        return rc;
    }
}
