using System.Diagnostics;

namespace ClaudeSwitch.App;

/// <summary>
/// The demo data set, reachable from the UI rather than only from a command line.
/// </summary>
/// <remarks>
/// <c>--fixture</c> has always existed, but only as a flag, and an app installed
/// from the Microsoft Store is launched from the Start menu with no arguments.
/// That left the packaged build with no way to show what it does to anyone who
/// has not already installed Claude Code and signed in — which is how it failed
/// Store certification 10.1.2.10, "Unusable Feature: Add account": a reviewer on
/// a clean machine pressed the one obvious button and got an error.
///
/// So the flag gets a door. The demo starts a second instance of this same
/// executable against a throwaway home directory. The two coexist by design:
/// the single-instance mutex is keyed on the data set, not on the machine (see
/// <c>Program.Main</c>). Starting our own exe by path works inside an MSIX
/// package, where handing arguments to the Start menu shortcut does not.
/// </remarks>
internal static class DemoMode
{
    /// <summary>True when this process is itself the demo instance.</summary>
    public static bool IsActive { get; set; }

    /// <summary>
    /// Throwaway home for the demo accounts: seeded on launch, and nothing under
    /// the real <c>~/.claude</c> is read or written while it is in use.
    /// </summary>
    public static string Root { get; } =
        Path.Combine(Path.GetTempPath(), "ClaudeSwitch.Demo");

    /// <summary>Opens a second window running on demo data.</summary>
    public static void Launch()
    {
        Directory.CreateDirectory(Root);

        var psi = new ProcessStartInfo(StartupHelper.ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--fixture");
        psi.ArgumentList.Add(Root);
        // Not a first run: whatever offered the demo is the introduction.
        psi.ArgumentList.Add("--skip-onboarding");
        Process.Start(psi);
    }
}
