using JohnnyAppleseed.Platform;

namespace JohnnyAppleseed;

static class Program
{
    // Point users at a page listing common early-start failures and their fixes.
    const string HelpUrl = "https://jeffrey-p-mcateer.github.io/johnny-appleseed";

    [System.STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Game.Run();
            return 0;
        }
        catch (Exception ex)
        {
            // Any unhandled failure during startup/run that reaches here is shown
            // to the user in a native dialog (see StartupError) rather than a
            // silent exit or a raw .NET crash banner.
            StartupError.Show("Johnny Appleseed — Startup Problem", BuildReport(ex));
            return 1;
        }
    }

    static string BuildReport(Exception ex) =>
        "Johnny Appleseed couldn't start.\n\n" +
        $"{ex.GetType().Name}: {ex.Message}\n\n" +
        $"For help resolving this, see:\n{HelpUrl}\n\n" +
        "Technical details (you can copy this text):\n" +
        ex;
}
