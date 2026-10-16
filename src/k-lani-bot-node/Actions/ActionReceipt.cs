using System.Text.Json.Nodes;
using KLaniBot.Guard;
using KLaniBot.Visibility;

namespace KLaniBot.Actions;

// The receipt of one action: what the node answers to the client and what it writes to the journal.
// The fields are added in a fixed order because journal entries and traces are compared as text.
internal sealed class ActionReceipt
{
    private readonly JsonObject _fields;

    public ActionReceipt(ActionRequest request, string nodeId, string snapshotId, long durationMilliseconds, ActionOutcome outcome)
    {
        _fields = new JsonObject
        {
            ["operation_id"] = request.OperationId,
            ["node_id"] = nodeId,
            ["principal_id"] = request.Context.PrincipalId,
            ["session_id"] = request.Context.SessionId,
            ["snapshot_id"] = snapshotId,
            ["action"] = request.Action,
            ["duration_ms"] = durationMilliseconds,
            ["outcome"] = outcome.ToWireName(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    public void AddScreenshots(JsonObject screenshots)
    {
        _fields["screenshots"] = screenshots.DeepClone();
    }

    // Lets the client and a later reader connect the receipt to the journal's record of the admission.
    public void AddOperation(ActionRequest request)
    {
        _fields["operation_key"] = request.OperationKey;
        _fields["request_hash"] = request.Hash;
    }

    // The agent's own statement of why it acted, next to what actually happened.
    public void AddDecision(AgentDecision decision, JsonNode decisionResult)
    {
        _fields["decision"] = decision.ToAudit();
        _fields["decision_result"] = decisionResult;
    }

    public void AddResult(
        bool success,
        JsonObject result,
        ActionRequest request,
        SessionPresentation? presentation,
        WindowInteractionMode windowInteraction)
    {
        _fields["success"] = success;
        _fields["error"] = result["error"]?.DeepClone();
        _fields["target_element_id"] = request.Arguments["id"]?.DeepClone();
        _fields["purpose"] = StatedOrNull(presentation?.Purpose);
        _fields["master"] = StatedOrNull(presentation?.Master);
        _fields["window_interaction"] = windowInteraction.ToString();
    }

    // The receipt records what was stated; an empty field becomes null instead of an empty word.
    private static string? StatedOrNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public JsonObject ToJson()
    {
        return _fields;
    }
}
