using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_input sends one of three raw inputs to a window that publishes no accessibility tree.
public enum CompactInputAction
{
    Click,
    Key,
    Type
}

// Use case: a ui_input click names the pointer button the node's mouse_click sends; double is two left clicks.
public enum CompactPointerButton
{
    Left,
    Right,
    Double
}

// Use case: ui_input proves from the node's tree read why the semantic route cannot operate the window, and journals that word.
public enum CompactSemanticAbsence
{
    // The accessibility bus publishes no tree at all for the window.
    NoTree,

    // The tree holds the window row and nothing inside it.
    EmptyTree
}

// Use case: one parsed ui_input step; a click carries a point, a key step its key names, a type step its literal text.
public sealed record CompactInputStep(CompactInputAction Action, int X, int Y, CompactPointerButton Button, IReadOnlyList<string> Keys,
    string Text);

// Use case: a whole ui_input call after parsing, before any node call.
public sealed record CompactInputRequest(string Reason, string? Shot, IReadOnlyList<CompactInputStep> Steps);

// Use case: the screenshot ui_input returned last; a click must name it, for the same window instance at the same rectangle.
public sealed record CompactInputShot(string Id, long Handle, string? Started, WindowScreenBounds Bounds);
