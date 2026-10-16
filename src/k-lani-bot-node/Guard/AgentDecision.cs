using System.Text.Json.Nodes;

namespace KLaniBot.Guard;

// How much the agent itself thinks is at stake. The node records the value and does not grade or act on it.
public enum DecisionSeverity
{
    Light,
    Medium,
    Heavy
}

// Use case: the operator lets the agent operate modal dialogs (a certificate warning, an overwrite question)
// without a person answering each one. In exchange the agent must state before the action what it saw, what
// it chose, why, and what it expects; the dispatcher writes that to the journal before anything is executed,
// so the reasons cannot be adjusted to the outcome afterwards. A semantic action on a target inside a modal
// dialog is refused unless the request carries a decision (ModalDialogPolicy).
//
// The node only checks that a decision is present and well formed. Its content is the agent's claim and is
// recorded as such, never used as a permission.
public sealed record AgentDecision(DecisionSeverity Severity, string Occasion, string Choice, string Reason, string ExpectedResult)
{
    // Throws ArgumentException for an unknown severity word.
    public static AgentDecision? Read(JsonObject arguments)
    {
        if (arguments["decision"] is not JsonObject decision)
        {
            return null;
        }
        var severity = decision["severity"]!.ToString() switch
        {
            "light" => DecisionSeverity.Light,
            "medium" => DecisionSeverity.Medium,
            "heavy" => DecisionSeverity.Heavy,
            _ => throw new ArgumentException("Invalid decision severity.")
        };
        return new AgentDecision(severity, decision["occasion"]!.ToString(), decision["choice"]!.ToString(),
            decision["reason"]!.ToString(), decision["expected_result"]!.ToString());
    }
    public JsonObject ToAudit()
    {
        return new JsonObject
        {
            ["severity"] = Severity.ToString(),
            ["occasion"] = Occasion,
            ["choice"] = Choice,
            ["reason"] = Reason,
            ["expected_result"] = ExpectedResult,
            // The journal signature proves that this entry was written unchanged, not that the agent's
            // rationale or risk assessment is factually correct.
            ["assessment_source"] = "agent"
        };
    }
}
