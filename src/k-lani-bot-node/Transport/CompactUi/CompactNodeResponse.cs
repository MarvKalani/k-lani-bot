using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Node replies preserve missing/unknown outcomes and exact refusal codes rather than inferring success from bytes or dispatch alone.
public static class CompactNodeResponse
{
    public static CompactWorkflowOperationResult Read(JsonObject response)
    {
        ActionErrorCode? code = Enum.GetValues<ActionErrorCode>().Cast<ActionErrorCode?>()
            .FirstOrDefault(value => response["code"]?.ToString() == value!.Value.ToWireName());
        bool success = response["success"] is JsonValue flag && flag.TryGetValue<bool>(out bool accepted) && accepted;

        return new(success, ActionOutcomeNames.Read(response), code, response.DeepClone().AsObject());
    }

    // Throws CompactNodeException for a refused, malformed or unconfirmed native action response.
    public static JsonObject Require(JsonObject response)
    {
        var result = Read(response);
        if (!result.Success)
        {
            throw new CompactNodeException(response["error"]?.ToString() ?? response["message"]?.ToString()
                ?? "Native action did not establish success.", result);
        }

        return response;
    }
}
