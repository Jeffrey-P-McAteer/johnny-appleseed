namespace JohnnyAppleseed.Scenes;

interface IScene
{
    void Load();
    // Returns the next scene to transition to, or null to stay on this scene.
    // Return ExitScene.Instance to quit.
    IScene? Update(float dt);
    void Draw();
    void Unload();
}

// Sentinel: returning this from Update() causes the game loop to exit.
sealed class ExitScene : IScene
{
    public static readonly ExitScene Instance = new();
    public void Load() { }
    public IScene? Update(float dt) => null;
    public void Draw() { }
    public void Unload() { }
}
