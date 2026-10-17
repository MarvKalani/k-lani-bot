namespace KLaniBot.Platforms;

// Use case: a backend observes a window whose program publishes no accessibility tree. The tree reader returns this line, and the
// get_ui_tree handler turns exactly this line into the typed no_accessibility_tree code that the last-resort input route requires.
public static class UiTreeErrors
{
    public const string NoTreeMessage = "The accessibility bus shows no tree for this window.";

    // Backends return a failed tree read as one line, the word error and the exception message.
    public const string NoTreeLine = "error: " + NoTreeMessage;
}
