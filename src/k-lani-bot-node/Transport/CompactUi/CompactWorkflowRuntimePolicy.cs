using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Stage policy preserves explicit decisions, immutable consumed inputs and cautious interpretation of node-ledger refusals.
internal static class CompactWorkflowRuntimePolicy
{
    public static string? MissingInput(CompactWorkflowStage stage, CompactWorkflowJob job)
    {
        if (stage.Decision != null && (!job.Parameters.TryGetValue(stage.Decision.Input, out var decision) || decision.Boolean != true))
        {
            return stage.Decision.Input;
        }

        return stage.Bindings.Values.FirstOrDefault(binding => binding.Kind == CompactWorkflowBindingKind.Input
            && !job.Parameters.ContainsKey(binding.Input!))?.Input;
    }

    public static bool TryAnswers(CompactWorkflowJob job, IReadOnlyDictionary<string, CompactWorkflowValue> answers,
        out IReadOnlyDictionary<string, CompactWorkflowValue>? parameters)
    {
        parameters = null;
        foreach (var answer in answers)
        {
            if (job.Parameters.TryGetValue(answer.Key, out var previous)
                && !(job.Status == CompactWorkflowStatus.AwaitingInput && job.Decision?.Input == answer.Key
                    && previous.Boolean == false && answer.Value.Boolean == true && !Consumed(job, answer.Key)))
            {
                return false;
            }
        }
        var merged = job.Parameters.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            merged[answer.Key] = answer.Value;
        }
        parameters = merged;

        return true;
    }

    private static bool Consumed(CompactWorkflowJob job, string input)
    {
        return job.Definition.Stages.Any(stage => job.CompletedStages.Contains(stage.Id)
            && (stage.Decision?.Input == input || stage.Bindings.Values.Any(binding => binding.Input == input)));
    }

    public static bool Refused(CompactWorkflowOperationResult? result)
    {
        return result?.Outcome == ActionOutcome.NotDispatched
            && result.Code is ActionErrorCode.Denied or ActionErrorCode.ConsentDenied;
    }

    public static bool PermissionBlocked(IReadOnlyList<CompactWorkflowOperation> operations)
    {
        return operations.Count > 0 && operations.All(operation => Refused(operation.Response));
    }

    public static bool PrefixOnly(CompactWorkflowPending pending)
    {
        return pending.Phase != CompactWorkflowPendingPhase.Verified && pending.Before.ExpectedSteps is int steps
            && (pending.StepIndex != steps - 1 || pending.Before.MutationSteps?.Any(index =>
                !pending.Operations.Any(operation => operation.StepIndex == index)) == true || MissingNativeOperations(pending));
    }

    private static bool MissingNativeOperations(CompactWorkflowPending pending)
    {
        if (pending.Before.Source["expected_native_operations"] is not System.Text.Json.Nodes.JsonObject counts)
        {
            return false;
        }
        foreach (var count in counts)
        {
            if (!int.TryParse(count.Key, out int index) || count.Value is not System.Text.Json.Nodes.JsonValue value
                || !value.TryGetValue<int>(out int expected) || expected < 0
                || pending.Operations.Count(operation => operation.StepIndex == index) < expected)
            {
                return true;
            }
        }

        return false;
    }
}
