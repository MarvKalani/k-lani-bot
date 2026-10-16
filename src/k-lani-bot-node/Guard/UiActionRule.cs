using System.Text.RegularExpressions;
using KLaniBot.Actions;

namespace KLaniBot.Guard;

// Use case: in the guarded profile the operator approves single controls, for example "the file name field
// of the export dialog of this slicer may be typed into". One rule names the program by exact path and
// SHA-256, the window by exact title, the element by at least one of role, name and automation id, and the
// actions allowed on it. Every application the agent may even read must appear in at least one rule.
//
// Only the three semantic input actions can be authorized: every other mutating tool is refused outright in
// the guarded profile, so a rule naming one would promise something the guard never grants. A target must
// match exactly one rule (see UiActionGuard.FindUniqueRule); two overlapping rules deny, because it would be
// unclear which text policy applies.
public sealed class UiActionRule
{
    public Tool[] Actions { get; set; } = [];
    public string Application { get; set; } = "";
    public string ApplicationSha256 { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string? Role
    {
        get; set;
    }
    public string? Name
    {
        get; set;
    }
    public string? AutoId
    {
        get; set;
    }
    public TextInputPolicy TextPolicy { get; set; } = TextInputPolicy.None;
    public string[] Extensions { get; set; } = [];
    public int MaxTextLength { get; set; } = 4096;

    // For the most sensitive controls ("Delete permanently", "Accept certificate"): the agent must record a decision for every
    // action on it, as for a modal dialog, even when the backend detects no dialog.
    public bool RequiresDecision
    {
        get; set;
    }

    // Throws InvalidDataException naming the first requirement the rule breaks.
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Application) ||
            !Regex.IsMatch(ApplicationSha256, "^[0-9a-f]{64}$"))
        {
            throw new InvalidDataException("A guard rule requires an exact executable path and SHA-256 digest.");
        }

        if (WindowTitle.Length is < 1 or > 512 ||
            Role == null && Name == null && AutoId == null)
        {
            throw new InvalidDataException("A guard rule requires a window and element selector.");
        }

        if (Actions.Length == 0 || Actions.Any(action =>
            action is not (Tool.ClickElement or Tool.SilentClick or Tool.TypeText)))
        {
            throw new InvalidDataException("Only supported semantic input actions may be authorized.");
        }

        if (!Enum.IsDefined(TextPolicy) || MaxTextLength is < 1 or > 65536 ||
            Extensions.Any(extension => !Regex.IsMatch(extension, "^\\.[a-z0-9]{1,10}$")))
        {
            throw new InvalidDataException("The text policy or file-extension boundary is invalid.");
        }
    }
}
