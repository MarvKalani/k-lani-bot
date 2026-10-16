using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Pending checkpoint parsing retains the exact admitted operation IDs and never infers a missing operation's success.
public static class CompactWorkflowPendingCodec
{
    public static bool TryRead(JsonNode? node, CompactWorkflowDefinition definition,
        out CompactWorkflowJobAttempt<CompactWorkflowPending> attempt)
    {
        attempt = new(null);
        if (node == null)
        {
            return true;
        }
        attempt = new(null, CompactWorkflowJobError.InvalidCheckpoint);
        if (node is not JsonObject source || !CompactDiscoveryDocumentCodec.Only(source, "stage_id", "phase", "before", "operations",
                "parameters", "step_index", "outputs") || !Text(source, "stage_id", out string id)
            || !definition.Stages.Any(stage => stage.Id == id) || !Text(source, "phase", out string phase)
            || !TryPhase(phase, out var state)
            || !TryEvidence(source["before"], out var evidence)
            || !CompactWorkflowJobCodec.TryOutputValues(source["parameters"], out var parameters)
            || source["operations"] is not JsonArray array || !TryOperations(array, out var operations))
        {
            return false;
        }
        int? stepIndex = null;
        if (source.ContainsKey("step_index"))
        {
            if (source["step_index"] is not JsonValue step || !step.TryGetValue<int>(out int index) || index < 0)
            {
                return false;
            }
            stepIndex = index;
        }
        IReadOnlyDictionary<string, CompactWorkflowValue>? outputs = null;
        if (source.ContainsKey("outputs") && !CompactWorkflowJobCodec.TryOutputValues(source["outputs"], out outputs))
        {
            return false;
        }
        attempt = new(new(id, state, evidence!, parameters!, operations!, stepIndex, outputs));

        return true;
    }

    public static bool TryEvidence(JsonNode? node, out CompactWorkflowStageEvidence? evidence)
    {
        evidence = null;
        if (node is not JsonObject source)
        {
            return false;
        }
        int? count = null;
        if (source.ContainsKey("expected_steps"))
        {
            if (source["expected_steps"] is not JsonValue steps || !steps.TryGetValue<int>(out int value) || value < 1)
            {
                return false;
            }
            count = value;
        }
        int[]? mutations = null;
        if (source["mutation_steps"] is JsonArray array)
        {
            if (array.Any(node => node is not JsonValue value || !value.TryGetValue<int>(out int index) || index < 0))
            {
                return false;
            }
            mutations = array.Select(node => node!.GetValue<int>()).ToArray();
        }
        evidence = new(source.DeepClone().AsObject(), count, mutations);

        return true;
    }

    private static bool TryOperations(JsonArray array, out IReadOnlyList<CompactWorkflowOperation>? operations)
    {
        operations = null;
        var parsed = new List<CompactWorkflowOperation>();
        var identifiers = new HashSet<Guid>();
        foreach (JsonNode? node in array)
        {
            if (!TryOperation(node, out var operation) || !identifiers.Add(operation!.RequestId))
            {
                return false;
            }
            parsed.Add(operation);
        }
        operations = parsed;

        return true;
    }

    private static bool TryOperation(JsonNode? node, out CompactWorkflowOperation? operation)
    {
        operation = null;
        if (node is not JsonObject source
            || !CompactDiscoveryDocumentCodec.Only(source, "request_id", "tool", "step_index", "arguments", "phase", "response")
            || !Text(source, "request_id", out string request) || !Uuid7Time.TryReadMilliseconds(request, out _)
            || !Guid.TryParseExact(request, "D", out Guid identifier) || identifier.ToString() != request
            || !Text(source, "tool", out string tool) || !ToolNames.TryParse(tool, out var action)
            || source["arguments"] is not JsonObject args
            || !Text(args, "request_id", out string argumentId) || argumentId != request || !Text(source, "phase", out string phase)
            || !TryOperationPhase(phase, out var state))
        {
            return false;
        }
        int? stepIndex = null;
        if (source.ContainsKey("step_index"))
        {
            if (source["step_index"] is not JsonValue step || !step.TryGetValue<int>(out int index) || index < 0)
            {
                return false;
            }
            stepIndex = index;
        }
        CompactWorkflowOperationResult? result = null;
        if (source.ContainsKey("response"))
        {
            if (source["response"] is not JsonObject response)
            {
                return false;
            }
            result = Response(response);
        }
        operation = new(identifier, action, stepIndex, args.DeepClone().AsObject(), state, result);

        return true;
    }

    public static CompactWorkflowOperationResult Response(JsonObject source)
    {
        bool success = source["success"] is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;
        ActionErrorCode? code = null;
        if (Text(source, "code", out string reason))
        {
            foreach (ActionErrorCode candidate in Enum.GetValues<ActionErrorCode>())
            {
                if (candidate.ToWireName() == reason)
                {
                    code = candidate;
                }
            }
        }

        return new(success, ActionOutcomeNames.Read(source), code, source.DeepClone().AsObject());
    }

    private static bool TryPhase(string value, out CompactWorkflowPendingPhase phase)
    {
        foreach (CompactWorkflowPendingPhase candidate in Enum.GetValues<CompactWorkflowPendingPhase>())
        {
            if (CompactWorkflowJobNames.Pending(candidate) == value)
            {
                phase = candidate;
                return true;
            }
        }
        phase = CompactWorkflowPendingPhase.Prepared;

        return false;
    }

    private static bool TryOperationPhase(string value, out CompactWorkflowOperationPhase phase)
    {
        foreach (CompactWorkflowOperationPhase candidate in Enum.GetValues<CompactWorkflowOperationPhase>())
        {
            if (CompactWorkflowJobNames.Operation(candidate) == value)
            {
                phase = candidate;
                return true;
            }
        }
        phase = CompactWorkflowOperationPhase.Intent;

        return false;
    }

    private static bool Text(JsonObject source, string name, out string value)
    {
        return CompactDiscoveryDocumentCodec.Text(source, name, out value);
    }
}
