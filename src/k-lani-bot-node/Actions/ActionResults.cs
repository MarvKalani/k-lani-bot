using System.Text.Json.Nodes;

namespace KLaniBot.Actions;

// The shape of a result that did not succeed. Every producer builds it here, so the field names
// and the pairing of code and outcome cannot drift apart between the supervisor, the dispatcher,
// the transport client and the relay.
public static class ActionResults
{
    public static JsonObject Failure(ActionErrorCode code, ActionOutcome outcome, string message)
    {
        JsonObject result = Failure(outcome, message);
        result["code"] = code.ToWireName();
        return result;
    }

    // For failures that have no code, such as a node that cannot be reached at all.
    public static JsonObject Failure(ActionOutcome outcome, string message)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["outcome"] = outcome.ToWireName(),
            ["error"] = message
        };
    }

    // A refusal before anything reached the machine.
    public static JsonObject Rejected(ActionErrorCode code, string message)
    {
        return Failure(code, ActionOutcome.NotDispatched, message);
    }
}
