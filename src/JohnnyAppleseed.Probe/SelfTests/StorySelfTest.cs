using System.Text;
using JohnnyAppleseed;
using JohnnyAppleseed.Narrative;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Headless checks for the ink-driven story pipeline: the intro compiles, steps
/// through the expected beats/headings, reaches the end, and — critically — its
/// runtime state round-trips so <see cref="JohnnyAppleseed.Scenes.StoryScene"/>
/// can resume mid-story. No window/audio required. Exit 0 = all passed.
/// </summary>
static class StorySelfTest
{
    private const string IntroKey = "story/chapters/00-intro/intro.ink";

    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}");
            if (!ok) failures++;
        }

        Console.WriteLine("STORY SELF-TEST");

        string source;
        try
        {
            source = Encoding.UTF8.GetString(Assets.Bytes(IntroKey));
            Check(true, "intro.ink embedded");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL  intro.ink embedded: {ex.Message}");
            return 1;
        }

        StoryRunner runner;
        try
        {
            runner = new StoryRunner(source);
            Check(true, "intro.ink compiles");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL  intro.ink compiles: {ex.Message}");
            return failures + 1;
        }

        var beats = new List<string>();
        var headings = new List<string>();
        while (runner.CanContinue)
        {
            string line = runner.Continue(out IReadOnlyList<string> tags);
            foreach (string t in tags)
                if (t.StartsWith("heading:", StringComparison.OrdinalIgnoreCase))
                    headings.Add(t[(t.IndexOf(':') + 1)..].Trim());
            if (line.Length > 0) beats.Add(line);
        }

        Check(beats.Count == 5, $"5 narration beats (got {beats.Count})");
        Check(headings.Count == 4, $"4 heading tags (got {headings.Count})");
        Check(headings.Contains("Westward"), "a known heading ('Westward') parsed from a tag");
        Check(runner.IsEnded && runner.ChoiceCount == 0, "reaches END with no dangling choices");

        // Resume mechanism: advance a fresh run two beats, save, restore, compare.
        var a = new StoryRunner(source);
        AdvanceBeats(a, 2);
        string state = a.SaveState();
        Check(!string.IsNullOrEmpty(state), "state serializes to non-empty JSON");

        var b = new StoryRunner(source);
        b.LoadState(state);
        Check(b.CurrentText == a.CurrentText && a.CurrentText.Length > 0,
              "resume: restored CurrentText matches the saved beat");

        Console.WriteLine(failures == 0
            ? "STORY SELF-TEST: ALL PASSED"
            : $"STORY SELF-TEST: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void AdvanceBeats(StoryRunner runner, int nonEmpty)
    {
        int seen = 0;
        while (seen < nonEmpty && runner.CanContinue)
            if (runner.Continue(out _).Length > 0) seen++;
    }
}
