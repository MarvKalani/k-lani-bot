using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared job fixtures exercise actual private native/JS stores.
// Only timestamps are normalized, never references or complete runtime values.
internal static class WorkflowJobParityCases
{
    private static readonly CompactAcceptanceAuthority Authority = new("fixture-node", "owner", "fixture-client");

    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            string root = Path.Combine(Path.GetTempPath(), "k-lani-job-parity-" + Guid.NewGuid());
            try
            {
                JsonObject fixture = node!.AsObject();
                output.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = RunCase(root, fixture) });
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        return output;
    }

    private static JsonObject RunCase(string root, JsonObject fixture)
    {
        var store = WorkflowJobStoreCases.Open(root);
        CompactWorkflowDefinition definition = WorkflowJobStoreCases.Definition();
        CompactWorkflowParameters.TryRead(definition.Inputs, fixture["parameters"], true, out var parameters);
        CompactContractHash.TryDigest(fixture["fingerprint"]!.GetValue<string>(), out var fingerprint);
        if (!store.TryCreate(fixture["request_id"]!.GetValue<string>(), fingerprint!, definition, parameters.Value!, out var created))
        {
            return Error(created.Error!.Value, created.Detail);
        }
        CompactWorkflowJob job = created.Value!;
        if (fixture["request_conflict"]?.GetValue<bool>() == true)
        {
            store.TryCreate(fixture["request_id"]!.GetValue<string>(), new(new string('b', 64)), definition,
                parameters.Value!, out var refused);
            return Error(refused.Error!.Value, refused.Detail);
        }
        if (fixture["checkpoint"]?.GetValue<bool>() == true)
        {
            store.TryRead(job.Reference, out var prior);
            var next = job with
            {
                CompletedStages = ["open"],
                Outputs = new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>
                {
                    ["open"] = new Dictionary<string, CompactWorkflowValue>
                    {
                        ["value"] = new(CompactWorkflowValueKind.String, Text: "confirmed full value")
                    }
                }
            };
            store.TryWrite(prior.Value!, next, out var written);
            job = written.Value!.Job;
        }

        return Read(root, fixture, store, job.Reference);
    }

    private static JsonObject Read(string root, JsonObject fixture, CompactWorkflowStore store, CompactWorkflowJobReference reference)
    {
        if (fixture["foreign"]?.GetValue<bool>() == true)
        {
            CompactWorkflowStore.TryOpen(store.Root, Path.Combine(root, "exports"), Authority with
            {
                Node = "other"
            },
                new WorkflowJobStoreCases.Functions(), out var foreign);
            store = foreign.Value!;
        }
        else if (fixture["restart"]?.GetValue<bool>() == true)
        {
            store = WorkflowJobStoreCases.Open(root);
        }
        if (!store.TryRead(reference, out var loaded))
        {
            return Error(loaded.Error!.Value, loaded.Detail);
        }
        JsonObject wire = CompactWorkflowJobWire.Job(loaded.Value!.Job);
        wire["created_at"] = "$time";
        wire["updated_at"] = "$time";

        return wire;
    }

    private static JsonObject Error(CompactWorkflowJobError error, string? detail)
    {
        return new()
        {
            ["error"] = CompactWorkflowJobNames.Error(error, detail)
        };
    }
}
