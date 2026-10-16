using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// First-use capture collects verified named functions as stages; runtime values and uncertain candidates never enter definitions.
public sealed class CompactWorkflowCaptureSession
{
    private JsonObject? _source;

    public JsonObject Begin(JsonObject arguments)
    {
        _source = arguments.DeepClone().AsObject();
        _source["format"] = CompactWorkflowDefinitionCodec.Format;
        _source["stages"] = new JsonArray();
        _source["outputs"] = new JsonObject();

        return new JsonObject { ["success"] = true, ["capturing"] = true };
    }

    public bool TryRecord(CompactLoadedFunction function, CompactProgramIdentity application, out string? error)
    {
        error = null;
        if (_source == null)
        {
            return true;
        }
        var applications = _source["applications"]!.AsObject().Where(entry => entry.Value is JsonObject identity
            && identity["key"]?.ToString() == application.Key && identity["version"]?.ToString() == application.Build).ToArray();
        var stages = _source["stages"]!.AsArray();
        if (applications.Length != 1 || stages.Count >= CompactWorkflowDefinitionCodec.MaximumStages || function.Draft.Outputs.Count == 0
            || function.Document["validation"]?.ToString().StartsWith("demonstrated_once", StringComparison.Ordinal) != true)
        {
            error = "Captured application needs one declared alias, bounded stages and verified function outputs.";
            return false;
        }
        var bindings = new JsonObject();
        foreach (var parameter in function.Draft.Steps.Where(step => step.Parameter != null).Select(step => step.Parameter!).Distinct())
        {
            if (_source["inputs"]!.AsObject().ContainsKey(parameter))
            {
                bindings[parameter] = new JsonObject { ["input"] = parameter };
            }
        }
        stages.Add(new JsonObject
        {
            ["id"] = "stage." + (stages.Count + 1),
            ["function_ref"] = new JsonObject { ["application"] = applications[0].Key, ["name"] = function.Draft.Name },
            ["bindings"] = bindings,
            ["preconditions"] = "fresh_unique_enabled_targets",
            ["postconditions"] = "recorded_function_outputs"
        });

        return true;
    }

    // Throws CompactNodeException for missing verified stages or updates that refer to uncaptured stages.
    public JsonObject Finish(JsonObject arguments)
    {
        if (_source?["stages"] is not JsonArray stages || stages.Count == 0)
        {
            throw new CompactNodeException("No verified stages were captured.");
        }
        var source = _source.DeepClone().AsObject();
        var identifiers = stages.Select(stage => stage!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (string section in new[] { "bindings", "decisions" })
        {
            if (arguments[section] is JsonObject overrides && overrides.Any(entry => !identifiers.Contains(entry.Key)))
            {
                throw new CompactNodeException("Capture bindings and decisions must name captured stages.");
            }
        }
        foreach (JsonNode? stage in source["stages"]!.AsArray())
        {
            string id = stage!["id"]!.ToString();
            if (arguments["bindings"]?[id] != null)
            {
                stage["bindings"] = arguments["bindings"]![id]!.DeepClone();
            }
            if (arguments["decisions"]?[id] != null)
            {
                stage["decision"] = arguments["decisions"]![id]!.DeepClone();
            }
        }
        source["outputs"] = arguments["outputs"]!.DeepClone();

        return source;
    }

    public void Clear()
    {
        _source = null;
    }
}
