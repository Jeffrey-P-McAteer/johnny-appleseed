using JohnnyAppleseed.Save;

namespace JohnnyAppleseed.Probe;

/// <summary>
/// Headless verification of the save format and intro-resume logic. Runs without
/// a window (invoked via <c>uv run scripts/probe.py selftest save</c>) so the
/// "save during the intro, resume where we left off" behaviour can be checked in
/// CI or from the command line. Reaches the game's internal save layer through
/// InternalsVisibleTo.
///
/// Returns a process exit code: 0 = all passed, 1 = a check failed.
/// </summary>
static class SaveSelfTest
{
    public static int Run()
    {
        // Isolate from the real user save by pointing at a throwaway temp file.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "JohnnyAppleseed-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        SaveSystem.SavePath = System.IO.Path.Combine(dir, "savegame.json");

        int failures = 0;
        try
        {
            failures += RoundTripAndResume();
            failures += MissingFieldsUseDefaults();
            failures += UnknownFieldsPreserved();
            failures += NewerFormatNotDowngraded();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine(failures == 0
            ? "\nSAVE SELF-TEST: ALL PASSED"
            : $"\nSAVE SELF-TEST: {failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // Core requirement: advancing through the intro and reloading resumes on the
    // exact page we left off at.
    private static int RoundTripAndResume()
    {
        Console.WriteLine("• round-trip + resume-at-step");
        int fails = 0;

        // Fresh start.
        SaveSystem.Delete();
        var fresh = new SaveData();
        SaveSystem.Save(fresh);

        // Simulate the player clicking from page 0 through to page 3.
        for (int page = 1; page <= 3; page++)
        {
            var s = SaveSystem.Load();
            fails += Check(s != null, "load returns a save");
            s!.Story.IntroStep = page;
            s.Story.IntroComplete = false;
            SaveSystem.Save(s);
        }

        // Reload as if the game was quit and relaunched.
        var resumed = SaveSystem.Load();
        fails += Check(resumed != null, "reload after quit");
        fails += Check(resumed!.Story.IntroStep == 3, $"resume page == 3 (got {resumed.Story.IntroStep})");
        fails += Check(!resumed.Story.IntroComplete, "intro not marked complete");
        fails += Check(resumed.FormatVersion == SaveSystem.CurrentFormatVersion, "format version stamped");
        fails += Check(resumed.Player.Name == "Johnny", "player defaults survive round-trip");

        // Finishing the intro flips the completion flag.
        resumed.Story.IntroComplete = true;
        SaveSystem.Save(resumed);
        var done = SaveSystem.Load();
        fails += Check(done!.Story.IntroComplete, "intro completion persists");

        return fails;
    }

    // A save missing newer fields should deserialize them to defaults, not throw.
    private static int MissingFieldsUseDefaults()
    {
        Console.WriteLine("• missing fields → defaults");
        int fails = 0;

        // Minimal document with only a format version present.
        File.WriteAllText(SaveSystem.SavePath, "{ \"formatVersion\": 1 }");
        var s = SaveSystem.Load();
        fails += Check(s != null, "load minimal document");
        fails += Check(s!.Story.IntroStep == 0, "introStep defaults to 0");
        fails += Check(s.Story.Checkpoint == Checkpoint.Intro, "checkpoint defaults to intro");
        fails += Check(s.Player.Name == "Johnny", "player name defaults");
        return fails;
    }

    // Fields a future build might add must survive a load/save by an older build.
    private static int UnknownFieldsPreserved()
    {
        Console.WriteLine("• unknown (future) fields preserved");
        int fails = 0;

        File.WriteAllText(SaveSystem.SavePath,
            "{ \"formatVersion\": 1, \"futureFeature\": { \"level\": 42 } }");

        var s = SaveSystem.Load();
        fails += Check(s != null, "load doc with unknown field");
        SaveSystem.Save(s!);   // re-save through the current model

        string json = File.ReadAllText(SaveSystem.SavePath);
        fails += Check(json.Contains("futureFeature") && json.Contains("42"),
            "unknown field round-trips untouched");
        return fails;
    }

    // A save written by a newer schema must not have its version number lowered.
    private static int NewerFormatNotDowngraded()
    {
        Console.WriteLine("• newer format version left intact");
        int fails = 0;

        File.WriteAllText(SaveSystem.SavePath,
            "{ \"formatVersion\": 999, \"story\": { \"introStep\": 2 } }");

        var s = SaveSystem.Load();
        fails += Check(s != null, "load newer-format doc");
        fails += Check(s!.FormatVersion == 999, $"version not downgraded (got {s.FormatVersion})");
        fails += Check(s.Story.IntroStep == 2, "readable fields still parsed");
        return fails;
    }

    private static int Check(bool ok, string label)
    {
        Console.WriteLine($"    {(ok ? "pass" : "FAIL")}  {label}");
        return ok ? 0 : 1;
    }
}
