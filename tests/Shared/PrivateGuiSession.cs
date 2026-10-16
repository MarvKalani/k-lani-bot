namespace KLaniBot;

// Use case: a GUI regression is about to create real windows. This check is the last line of
// defence that keeps them off the operator's physical desktop, even when someone starts the test
// binary by hand or a script forgets to clean its environment.
//
// The contract with scripts/private-gui-session.sh: that script creates the X display and the
// runtime directory and records both in K_LANI_BOT_PRIVATE_DISPLAY / K_LANI_BOT_PRIVATE_RUNTIME.
// A test only proceeds if the display it would use is that exact display and GTK is pinned to X11.
// Checking for the flag K_LANI_BOT_PRIVATE_GUI_TEST alone would accept an inherited Wayland setup.
public static class PrivateGuiSession
{
    public const string GuardFlag = "K_LANI_BOT_PRIVATE_GUI_TEST";
    public const string PrivateDisplay = "K_LANI_BOT_PRIVATE_DISPLAY";
    public const string PrivateRuntime = "K_LANI_BOT_PRIVATE_RUNTIME";

    public static string? FindViolation(Func<string, string?> readEnvironment)
    {
        if (readEnvironment(GuardFlag) != "1")
        {
            return "Start GUI tests through scripts/private-gui-session.sh.";
        }
        if (readEnvironment("GDK_BACKEND") != "x11")
        {
            return "GDK_BACKEND must be x11; an inherited Wayland backend reaches the real desktop.";
        }
        if (!string.IsNullOrEmpty(readEnvironment("WAYLAND_DISPLAY")))
        {
            return "WAYLAND_DISPLAY must be empty inside a private GUI session.";
        }
        string? display = readEnvironment("DISPLAY");
        if (string.IsNullOrEmpty(display) || display != readEnvironment(PrivateDisplay))
        {
            return "DISPLAY is not the private display created for this run.";
        }
        string? runtime = readEnvironment("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || runtime != readEnvironment(PrivateRuntime))
        {
            return "XDG_RUNTIME_DIR is not the private runtime directory created for this run.";
        }
        return null;
    }

    public static void Require()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        string? violation = FindViolation(Environment.GetEnvironmentVariable);
        if (violation != null)
        {
            throw new InvalidOperationException("Refusing to create windows: " + violation);
        }
    }
}
