using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Job wire snapshots remain private and carry complete runtime values, checkpoints and exact operation IDs for durable reconciliation.
public static class CompactWorkflowJobWire
{
    public const string Format = "k-lani-bot-workflow-job/1";

    public static CompactContractDigest Authority(CompactAcceptanceAuthority authority)
    {
        return new(CompactFormFields.Fingerprint(new JsonObject
        {
            ["node"] = authority.Node,
            ["principal"] = authority.Principal,
            ["connection"] = authority.Connection
        }));
    }

    public static JsonObject Job(CompactWorkflowJob job)
    {
        var result = new JsonObject
        {
            ["format"] = Format,
            ["job_ref"] = job.Reference.Digest.Hex,
            ["authority"] = job.Authority.Hex,
            ["request_fingerprint"] = job.RequestFingerprint.Hex,
            ["definition"] = job.Definition.Source.DeepClone(),
            ["parameters"] = CompactWorkflowWire.Values(job.Parameters),
            ["revision"] = job.Revision,
            ["status"] = CompactWorkflowJobNames.Status(job.Status),
            ["completed_stages"] = Strings(job.CompletedStages),
            ["outputs"] = Outputs(job.Outputs),
            ["pending"] = job.Pending == null ? null : Pending(job.Pending),
            ["applications"] = Instances(job.Applications),
            ["created_at"] = job.CreatedAt,
            ["updated_at"] = job.UpdatedAt
        };
        AddDetails(result, job);

        return result;
    }

    private static void AddDetails(JsonObject result, CompactWorkflowJob job)
    {
        if (job.Decision != null)
        {
            result["decision"] = new JsonObject { ["input"] = job.Decision.Input, ["prompt"] = job.Decision.Prompt };
        }
        if (job.Error != null)
        {
            result["error"] = job.Error;
        }
        if (job.FinalOutputs != null)
        {
            result["final_outputs"] = CompactWorkflowWire.Values(job.FinalOutputs);
        }
        if (job.StageEvidence != null)
        {
            result["stage_evidence"] = Evidence(job.StageEvidence);
        }
        if (job.OutputDigests != null)
        {
            result["output_digests"] = Digests(job.OutputDigests);
        }
        if (job.AdapterState != null)
        {
            foreach (KeyValuePair<string, JsonNode?> field in job.AdapterState)
            {
                result[field.Key] = field.Value?.DeepClone();
            }
        }
    }

    private static JsonObject Instances(IReadOnlyDictionary<string, CompactWorkflowInstance> instances)
    {
        return new JsonObject(instances.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key, new JsonObject
        {
            ["pid"] = entry.Value.ProcessId,
            ["started"] = entry.Value.Started,
            ["handle"] = entry.Value.Handle
        })));
    }

    public static JsonObject Pending(CompactWorkflowPending pending)
    {
        var result = new JsonObject
        {
            ["stage_id"] = pending.StageId,
            ["phase"] = CompactWorkflowJobNames.Pending(pending.Phase),
            ["before"] = pending.Before.Source.DeepClone(),
            ["parameters"] = CompactWorkflowWire.Values(pending.Parameters),
            ["operations"] = new JsonArray(pending.Operations.Select(operation => (JsonNode?)Operation(operation)).ToArray())
        };
        if (pending.StepIndex != null)
        {
            result["step_index"] = pending.StepIndex.Value;
        }
        if (pending.Outputs != null)
        {
            result["outputs"] = CompactWorkflowWire.Values(pending.Outputs);
        }

        return result;
    }

    private static JsonObject Operation(CompactWorkflowOperation operation)
    {
        var result = new JsonObject
        {
            ["request_id"] = operation.RequestId.ToString(),
            ["tool"] = ToolNames.Of(operation.Tool),
            ["arguments"] = operation.Arguments.DeepClone(),
            ["phase"] = CompactWorkflowJobNames.Operation(operation.Phase)
        };
        if (operation.StepIndex != null)
        {
            result["step_index"] = operation.StepIndex.Value;
        }
        if (operation.Response != null)
        {
            result["response"] = Response(operation.Response);
        }

        return result;
    }

    public static JsonObject Response(CompactWorkflowOperationResult response)
    {
        JsonObject result = response.Source?.DeepClone().AsObject() ?? new JsonObject
        {
            ["success"] = response.Success,
            ["outcome"] = response.Outcome.ToWireName()
        };
        if (response.Code != null)
        {
            result["code"] = response.Code.Value.ToWireName();
        }

        return result;
    }

    private static JsonObject Outputs(IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>> outputs)
    {
        return new JsonObject(outputs.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key,
            CompactWorkflowWire.Values(entry.Value))));
    }

    private static JsonObject Evidence(IReadOnlyDictionary<string, CompactWorkflowStageEvidence> evidence)
    {
        return new JsonObject(evidence.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key, entry.Value.Source.DeepClone())));
    }

    private static JsonObject Digests(IReadOnlyDictionary<string, CompactContractDigest> digests)
    {
        return new JsonObject(digests.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key, JsonValue.Create(entry.Value.Hex))));
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        return new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }
}
