using JohnnyAppleseed.Save;
using JohnnyAppleseed.Scenes;

namespace JohnnyAppleseed.Narrative;

/// <summary>
/// Central mapper from story outcomes to the next <see cref="IScene"/>. Keeps
/// scenes from hardcoding <c>new XScene()</c> transitions and gives one place for
/// story directives (end-of-story, future <c># goto:</c> targets, minigame
/// hand-offs) to resolve into concrete scenes.
///
/// Phase 1 is deliberately small: a completed story marks the intro finished,
/// clears the in-progress resume blob, and returns to the menu. As gameplay
/// scenes and minigames arrive, their routing lands here.
/// </summary>
static class Director
{
    /// <summary>The story reached its end — persist completion and go to the menu.</summary>
    public static IScene OnStoryComplete(SaveData save)
    {
        save.Story.IntroComplete = true;
        save.Story.Checkpoint = Checkpoint.Overworld;
        save.World.CurrentNode = "";
        save.World.InkState = null;      // finished → nothing to resume
        SaveSystem.Save(save);
        return new MainMenuScene();
    }
}
