using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Workflow definitions retain original ordered wire content while validating every referenced function and earlier-output binding.
public static class CompactWorkflowDefinitionCodec
{
    public const string Format = "k-lani-bot-workflow/1";
    public const int MaximumStages = 32;
    public const int MaximumEntries = 100;
    private static readonly Regex ApplicationBuild = new(@"\A[a-zA-Z0-9._-]{1,80}\z", RegexOptions.CultureInvariant);

    public static bool TryRead(JsonNode? node, ICompactWorkflowFunctions functions,
        out CompactWorkflowAttempt<CompactWorkflowDefinition> attempt)
    {
        attempt = new(null, CompactWorkflowError.UnsupportedFields);
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Only(document, "format", "key", "revision", "inputs",
                "applications", "stages", "outputs"))
        {
            return false;
        }
        if (!CompactDiscoveryDocumentCodec.Text(document, "format", out string format) || format != Format
            || !CompactDiscoveryDocumentCodec.Text(document, "key", out string key) || !CompactDiscoveryCodec.ValidName(key)
            || !CompactFunctionDocumentCodec.TryInteger(document["revision"], out long revision))
        {
            attempt = new(null, CompactWorkflowError.InvalidIdentity);
            return false;
        }
        if (document["inputs"] is not JsonObject inputs || inputs.Count > MaximumEntries
            || document["applications"] is not JsonObject applications || applications.Count is < 1 or > MaximumEntries
            || document["outputs"] is not JsonObject outputs || outputs.Count > MaximumEntries
            || document["stages"] is not JsonArray stages || stages.Count is < 1 or > MaximumStages)
        {
            attempt = new(null, CompactWorkflowError.InvalidBounds);
            return false;
        }

        return TryComponents(document, key, revision, inputs, applications, stages, outputs, functions, out attempt);
    }

    private static bool TryComponents(JsonObject source, string key, long revision, JsonObject rawInputs, JsonObject rawApplications,
        JsonArray rawStages, JsonObject rawOutputs, ICompactWorkflowFunctions functions,
        out CompactWorkflowAttempt<CompactWorkflowDefinition> attempt)
    {
        if (!TryInputs(rawInputs, out var inputs))
        {
            attempt = new(null, inputs.Error);
            return false;
        }
        if (!TryApplications(rawApplications, out var applications))
        {
            attempt = new(null, applications.Error);
            return false;
        }
        var stages = new List<CompactWorkflowStage>();
        var prior = new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValueKind>>(StringComparer.Ordinal);
        foreach (JsonNode? node in rawStages)
        {
            if (!CompactWorkflowStages.TryRead(node, inputs.Value!, applications.Value!, prior, functions, out var stage, out var outputs))
            {
                attempt = new(null, stage.Error, stage.Detail);
                return false;
            }
            stages.Add(stage.Value!);
            prior.Add(stage.Value!.Id, outputs!);
        }
        if (!TryOutputs(rawOutputs, inputs.Value!, prior, out var bindings))
        {
            attempt = new(null, bindings.Error);
            return false;
        }
        attempt = new(new(key, revision, inputs.Value!, applications.Value!, stages, bindings.Value!, source.DeepClone().AsObject(),
            new(CompactFormFields.Fingerprint(source))));

        return true;
    }

    private static bool TryInputs(JsonObject inputs, out CompactWorkflowAttempt<IReadOnlyDictionary<string, CompactWorkflowInput>> attempt)
    {
        var parsed = new Dictionary<string, CompactWorkflowInput>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> input in inputs)
        {
            if (!CompactDiscoveryCodec.ValidName(input.Key))
            {
                attempt = new(null, CompactWorkflowError.InvalidInputName);
                return false;
            }
            if (!CompactWorkflowInputs.TrySchema(input.Value, out var schema))
            {
                attempt = new(null, schema.Error);
                return false;
            }
            parsed.Add(input.Key, schema.Value!);
        }
        attempt = new(parsed);

        return true;
    }

    private static bool TryApplications(JsonObject applications,
        out CompactWorkflowAttempt<IReadOnlyDictionary<string, CompactWorkflowApplication>> attempt)
    {
        var parsed = new Dictionary<string, CompactWorkflowApplication>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in applications)
        {
            if (!TryApplication(entry.Key, entry.Value, out var application))
            {
                attempt = new(null, application.Error);
                return false;
            }
            parsed.Add(entry.Key, application.Value!);
        }
        attempt = new(parsed);

        return true;
    }

    private static bool TryApplication(string alias, JsonNode? node, out CompactWorkflowAttempt<CompactWorkflowApplication> attempt)
    {
        attempt = new(null, CompactWorkflowError.UnsupportedFields);
        if (node is not JsonObject application
            || !CompactDiscoveryDocumentCodec.Only(application, "key", "version", "process_name", "window"))
        {
            return false;
        }
        string? process = Text(application, "process_name");
        string? word = Text(application, "window");
        CompactWindowKind? window = null;
        if (word != null)
        {
            if (!CompactWindowKindWords.TryParse(word, out CompactWindowKind kind))
            {
                attempt = new(null, CompactWorkflowError.InvalidApplication);
                return false;
            }
            window = kind;
        }
        if (!CompactDiscoveryCodec.ValidName(alias) || !CompactDiscoveryDocumentCodec.Text(application, "key", out string key)
            || !CompactDiscoveryCodec.ValidName(key) || !CompactDiscoveryDocumentCodec.Text(application, "version", out string version)
            || version == "unknown" || !ApplicationBuild.IsMatch(version) || process == null && window == null)
        {
            attempt = new(null, CompactWorkflowError.InvalidApplication);
            return false;
        }
        attempt = new(new(new(key, version), process, window));

        return true;
    }

    private static bool TryOutputs(JsonObject outputs, IReadOnlyDictionary<string, CompactWorkflowInput> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValueKind>> prior,
        out CompactWorkflowAttempt<IReadOnlyDictionary<string, CompactWorkflowBinding>> attempt)
    {
        var bindings = new Dictionary<string, CompactWorkflowBinding>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> output in outputs)
        {
            if (!CompactDiscoveryCodec.ValidName(output.Key))
            {
                attempt = new(null, CompactWorkflowError.InvalidOutputName);
                return false;
            }
            if (!CompactWorkflowBindings.TryRead(output.Value, inputs, prior, out var binding))
            {
                attempt = new(null, binding.Error);
                return false;
            }
            bindings.Add(output.Key, binding.Value!);
        }
        attempt = new(bindings);

        return true;
    }

    private static string? Text(JsonObject record, string name)
    {
        return CompactDiscoveryDocumentCodec.Text(record, name, out string value) ? value : null;
    }
}
