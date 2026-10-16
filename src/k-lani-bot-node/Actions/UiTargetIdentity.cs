namespace KLaniBot.Actions;

// What the guard checks before an action touches an element: the program behind it, its window, role, name, automation id and
// whether it is visible and enabled.
public sealed record UiTargetIdentity(
    int ProcessId,
    string ExecutablePath,
    string WindowTitle,
    string Role,
    string Name,
    string AutoId,
    bool Visible,
    bool Enabled);
