using System.Diagnostics;
using System.Text.Json.Serialization;

namespace KLaniBot.Visibility;

// Where one authenticated client session stands in the local approval flow. Approval is given per
// session (and per assignment, see AccessVisibility.SetSessionPresentation) instead of per action,
// because delegated operation has no person who could answer a question for every dialog.
public enum SessionConsent
{
    New,
    Pending,
    Allowed,
    Denied,
    Deferred
}

// Who has to approve a new session. The member names are the persisted and invitation spelling that
// existing policy.json and visibility-preferences.json files already contain; renaming them would
// silently turn "unattended" into an unknown value on the next start.
[JsonConverter(typeof(JsonStringEnumConverter<ConsentMode>))]
public enum ConsentMode
{
    [JsonStringEnumMemberName("ask")]
    Ask,

    // For test machines and deliberately unattended fleet nodes. It still shows the indicator and
    // the border; it only removes the question.
    [JsonStringEnumMemberName("unattended")]
    Unattended
}

// The words "ask" and "unattended" also travel outside the node: in fleet invitations, on the
// command line and in administrator commands. Those boundaries parse them here and nowhere else.
public static class ConsentModeNames
{
    public static bool TryParse(string? text, out ConsentMode mode)
    {
        switch (text)
        {
            case "ask":
                mode = ConsentMode.Ask;
                return true;
            case "unattended":
                mode = ConsentMode.Unattended;
                return true;
            default:
                mode = ConsentMode.Ask;
                return false;
        }
    }

    public static string ToProtocolName(ConsentMode mode)
    {
        return mode switch
        {
            ConsentMode.Ask => "ask",
            ConsentMode.Unattended => "unattended",
            _ => throw new UnreachableException()
        };
    }
}

// What the always-visible indicator shows. Hosts map it to a colour; they never compare text.
public enum VisibilityStatus
{
    Idle,
    Connected,
    Active
}
