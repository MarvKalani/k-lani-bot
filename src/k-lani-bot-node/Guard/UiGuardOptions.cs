namespace KLaniBot.Guard;

// The guard settings of one principal in policy.json. They are local: the agent can read the effect through
// get_machine_capabilities but no tool can change them. The intent is the task the operator approved in
// plain words; the optional advisory classifier judges each mutating action against it and refuses to run
// without one.
//
// Validate runs again on every lookup of the principal (NodePolicy.Get), because the operator may edit the
// file while the node runs and a broken rule must deny instead of being half applied.
public sealed class UiGuardOptions
{
    public UiGuardMode Mode { get; set; } = UiGuardMode.Guarded;
    public string Intent { get; set; } = "";
    public UiActionRule[] Rules { get; set; } = [];

    // Throws InvalidDataException when the mode, a rule or the intent is invalid.
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Intent.Length > 1024 || Rules.Length > 200)
        {
            throw new InvalidDataException("The local UI guard policy is invalid.");
        }

        foreach (UiActionRule rule in Rules)
        {
            rule.Validate();
        }
    }
}
