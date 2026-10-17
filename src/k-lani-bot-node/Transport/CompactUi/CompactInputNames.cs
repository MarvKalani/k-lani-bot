namespace KLaniBot.Transport.CompactUi;

// Use case: the words ui_input, ui_observe and ui_act use for the last-resort route; the JS gateway uses the same words.
public static class CompactInputNames
{
    public const string Tool = "ui_input";

    public const string Route = "last-resort keyboard and pointer input (ui_input) for windows without an accessibility tree";

    // Appended to the no-tree refusal of ui_observe and ui_act, so the agent finds the only route that can operate such a window.
    public const string NoTreeHint = "This window publishes no accessibility tree; operate it with ui_input {window, reason} as a "
        + "last resort.";

    public const string TreePresentRefusal = "This window has an accessibility tree; use ui_observe and ui_act. ui_input is only for "
        + "windows without one.";

    public const string TreeUnknownRefusal = "The accessibility tree of this window could not be read, so ui_input cannot prove that it is "
        + "absent: ";

    public const string ShotRequiredRefusal = "A click needs the shot id of the latest ui_input screenshot of this window; call ui_input "
        + "without steps first.";

    public const string MovedRefusal = "The window moved or changed size since that screenshot; call ui_input without steps for a "
        + "fresh one.";

    public const string OutsideRefusal = "The point lies outside the selected window.";

    public const string ReplacedRefusal = "The observed application window was replaced. Refresh Info before input.";

    public const string Next = "Check the screenshot for the result. Click coordinates are pixels of this screenshot; pass its shot with "
        + "the next click.";

    public const string Retry = "Do not replay completed steps; call ui_input without steps to see the current state.";

    public static string Word(CompactSemanticAbsence absence)
    {
        return absence switch
        {
            CompactSemanticAbsence.NoTree => "no_accessibility_tree",
            CompactSemanticAbsence.EmptyTree => "empty_accessibility_tree",
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    public static string Word(CompactPointerButton button)
    {
        return button switch
        {
            CompactPointerButton.Left => "left",
            CompactPointerButton.Right => "right",
            CompactPointerButton.Double => "double",
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }
}
